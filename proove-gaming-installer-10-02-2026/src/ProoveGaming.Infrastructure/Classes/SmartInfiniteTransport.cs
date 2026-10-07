using HidSharp;
using Microsoft.Extensions.Logging;

namespace ProoveGaming.Infrastructure.Classes;

/// <summary>
/// The shared HID transfer plumbing of the Smart Infinite services — fresh-handle sessions,
/// block/table reads, whole-table writes, and the reply matching those transfers depend on.
/// One instance per service call, wrapping the device and a log tag.
/// </summary>
/// <remarks>
/// <para>
/// Extracted from <c>SmartInfiniteKeyMappingService</c> so the key-mapping and actuation services
/// cannot drift apart — this codebase has already paid for a copied transfer layer once (T-Racer's
/// key mapping silently diverged from Raster's; see the Raster family notes in
/// <c>docs/CONTEXT.md</c>). The behavioural subtleties below are load-bearing and hardware-measured:
/// </para>
/// <para>
/// Every reply arrives <b>one command behind</b> its request, so replies are matched by the command
/// and address they echo rather than by arrival order (<see cref="ReplyFor"/>); the input pipe is
/// never drained before the first real request, because reading that early leaves the HidSharp
/// Windows stream in a state where subsequent writes fail; and Windows can hold the previous HID
/// handle in a closing state for tens of milliseconds, so the first write on a fresh handle can
/// fail with ERROR_INVALID_FUNCTION — <see cref="WithRetry{T}"/> retries the whole open+exchange.
/// </para>
/// </remarks>
/// <summary>
/// Minimal I/O surface of one open Smart Infinite session — the seam that lets the transport (and
/// the services behind it) run against a scripted fake device in tests instead of HidSharp.
/// Production code never sees anything but the <see cref="HidStream"/>-backed implementation.
/// </summary>
internal interface ISmartInfiniteIo : IDisposable
{
	int Read(byte[] buffer);
	void Write(byte[] buffer);
}

internal sealed class SmartInfiniteTransport
{
	private const int MaxAttempts = 4;
	private const int RetryDelayMs = 100;
	private const int StreamTimeoutMs = 600;

	// Pacing. The firmware drops requests that arrive back-to-back too fast.
	private const int InterPacketDelayMs = 4;

	private readonly Func<ISmartInfiniteIo?> _open;
	private readonly string _devicePath;
	private readonly ILogger? _logger;
	private readonly string _tag;
	// Table-transfer chunk size for the transport this instance was resolved for: 56 over the
	// cable, SmartInfiniteProtocol.Receiver24GPayloadLength over the 2.4G receiver. Identity/
	// config blocks (ReadBlock) keep their 56+8 shape on every transport — only tables differ.
	private readonly int _maxPayload;

	public SmartInfiniteTransport(HidDevice device, string tag, ILogger? logger,
		int maxPayload = SmartInfiniteProtocol.MaxPayloadLength)
	{
		ArgumentNullException.ThrowIfNull(device);
		_open = () => device.TryOpen(out var stream) ? new HidStreamIo(stream) : null;
		_devicePath = device.DevicePath;
		_tag = tag;
		_logger = logger;
		_maxPayload = maxPayload;
	}

	/// <summary>Test seam: run the transport against a scripted device instead of real HID.</summary>
	internal SmartInfiniteTransport(Func<ISmartInfiniteIo?> open, string devicePath, string tag, ILogger? logger,
		int maxPayload = SmartInfiniteProtocol.MaxPayloadLength)
	{
		_open = open ?? throw new ArgumentNullException(nameof(open));
		_devicePath = devicePath;
		_tag = tag;
		_logger = logger;
		_maxPayload = maxPayload;
	}

	public string DevicePath => _devicePath;

	private sealed class HidStreamIo : ISmartInfiniteIo
	{
		private readonly HidStream _stream;

		public HidStreamIo(HidStream stream)
		{
			_stream = stream;
			_stream.ReadTimeout = StreamTimeoutMs;
			_stream.WriteTimeout = StreamTimeoutMs;
		}

		public int Read(byte[] buffer) => _stream.Read(buffer);
		public void Write(byte[] buffer) => _stream.Write(buffer);
		public void Dispose() => _stream.Dispose();
	}

	/// <summary>
	/// Opens a fresh session for the body and always closes it. No <c>0x10</c>/<c>0x11</c> bracket —
	/// the factory's 2026-08 clarifications state the session commands are not used for anything,
	/// and the bare shape is what makes writes persist (proven on Rampage keys).
	/// </summary>
	public T? OnHandle<T>(Func<ISmartInfiniteIo, T?> body)
	{
		var io = _open();
		if (io == null)
		{
			_logger?.LogWarning("[{Tag}] TryOpen failed on {Path}", _tag, _devicePath);
			return default;
		}

		using (io)
		{
			return body(io);
		}
	}

	/// <summary>
	/// Reads and decodes the device identity block. Deliberately <b>not</b> cached: service
	/// lifetimes differ per family — key-mapping services are built fresh per call (so an
	/// instance-scoped cache would never be hit there), while keyboard RGB services are
	/// process-cached (so an instance-scoped cache there, like a static one, would risk serving
	/// stale identity after a firmware change or replug). That lifetime split is the same reason
	/// <see cref="DeviceGate"/> is static. A couple of packets on a user-initiated action is the
	/// cheaper side of that trade.
	/// </summary>
	public SmartInfiniteDeviceInfo? ReadDeviceInfo(ISmartInfiniteIo io)
	{
		var block = ReadBlock(io, SmartInfiniteProtocol.CmdReadDeviceInfo);
		if (block == null)
		{
			_logger?.LogWarning("[{Tag}] could not read Device Info", _tag);
			return null;
		}

		var info = SmartInfiniteDeviceInfo.Parse(block);
		if (!info.IsValid)
		{
			_logger?.LogWarning("[{Tag}] Device Info is missing its 0xAA 0xBB marker — not decoding it", _tag);
			return null;
		}

		return info;
	}

	/// <summary>Reads a 64-byte identity/config block: two packets, offset 0 then offset 56.</summary>
	public byte[]? ReadBlock(ISmartInfiniteIo io, byte readCommand)
	{
		var block = new byte[64];
		bool any = false;

		foreach (var (addr, len) in new[] { ((ushort)0, 56), ((ushort)56, 8) })
		{
			io.Write(SmartInfiniteProtocol.BuildReadCommand(readCommand, addr, (byte)len));
			var reply = ReplyFor(io, readCommand, addr);
			Thread.Sleep(InterPacketDelayMs);
			if (reply == null) continue;
			Buffer.BlockCopy(reply, SmartInfiniteProtocol.ResponsePayloadOffset, block, addr, len);
			any = true;
		}

		return any ? block : null;
	}

	/// <summary>
	/// Reads a whole table into a buffer of <paramref name="bufferSize"/> bytes (≥
	/// <paramref name="dataSize"/>; callers that write back a larger reserve size the buffer for
	/// it). Returns <c>null</c> unless every chunk arrived — a partial table must never escape,
	/// because callers use the result as the basis of a whole-table write.
	/// </summary>
	public byte[]? ReadTable(ISmartInfiniteIo io, byte readCommand, int dataSize, int bufferSize, CancellationToken cancellationToken)
	{
		var table = new byte[Math.Max(bufferSize, dataSize)];
		var chunks = SmartInfiniteProtocol.EnumerateChunks(dataSize, _maxPayload);
		var filled = new bool[chunks.Count];

		// Two passes: the first sends every request, the second re-requests anything that went
		// missing. Replies are matched by echoed address, so the one-command lag is harmless.
		for (int pass = 0; pass < 2; pass++)
		{
			for (int i = 0; i < chunks.Count; i++)
			{
				if (cancellationToken.IsCancellationRequested) return null;
				if (filled[i]) continue;
				var (addr, len) = chunks[i];
				io.Write(SmartInfiniteProtocol.BuildReadCommand(readCommand, addr, (byte)len));
				var reply = ReplyFor(io, readCommand, addr);
				Thread.Sleep(InterPacketDelayMs);
				if (reply == null) continue;
				Buffer.BlockCopy(reply, SmartInfiniteProtocol.ResponsePayloadOffset, table, addr, len);
				filled[i] = true;
			}
			if (filled.All(f => f)) break;
		}

		if (filled.All(f => f)) return table;

		var missing = string.Join(", ", chunks.Where((_, i) => !filled[i]).Select(c => $"0x{c.Addr:X4}"));
		_logger?.LogWarning("[{Tag}] cmd 0x{Cmd:X2} read incomplete — chunks never arrived: {Missing}",
			_tag, readCommand, missing);
		return null;
	}

	/// <summary>
	/// Writes <paramref name="dataSize"/> bytes of <paramref name="table"/> in transport-sized
	/// chunks (56 over the cable, 24 over the 2.4G receiver). Returns false when cancelled
	/// mid-transfer — safe to abandon, the firmware discards a table shorter than the declared
	/// size.
	/// </summary>
	public bool WriteTable(ISmartInfiniteIo io, byte writeCommand, byte[] table, int dataSize, CancellationToken cancellationToken)
	{
		var chunks = SmartInfiniteProtocol.EnumerateChunks(dataSize, _maxPayload);
		int acked = 0;

		foreach (var (addr, len) in chunks)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				_logger?.LogInformation(
					"[{Tag}] cmd 0x{Cmd:X2} write cancelled at addr=0x{Addr:X4}; the partial transfer is discarded by the firmware",
					_tag, writeCommand, addr);
				return false;
			}

			io.Write(SmartInfiniteProtocol.BuildCommand(writeCommand, addr, table.AsSpan(addr, len)));

			// The ack must be consumed so it cannot be mistaken for a read reply later in this
			// session, and its status byte is the firmware's only channel for reporting a rejection.
			var ack = ReplyFor(io, writeCommand, addr);
			if (ack != null) acked++;
			else _logger?.LogWarning("[{Tag}] write addr=0x{Addr:X4}: no matching ack", _tag, addr);

			Thread.Sleep(InterPacketDelayMs);
		}

		_logger?.LogInformation("[{Tag}] cmd 0x{Cmd:X2}: wrote {Packets} packets, {Bytes} bytes, {Acked}/{Total} acked",
			_tag, writeCommand, chunks.Count, dataSize, acked, chunks.Count);
		return true;
	}

	/// <summary>
	/// Reads replies until one echoes the given command and address, discarding anything else — the
	/// one-command lag, leftovers from an earlier phase. Never drains the pipe before the first
	/// real request.
	/// </summary>
	public byte[]? ReplyFor(ISmartInfiniteIo io, byte command, ushort addr)
	{
		const int MaxDrain = 5;

		for (int attempt = 0; attempt < MaxDrain; attempt++)
		{
			byte[] reply = new byte[SmartInfiniteProtocol.ReportLength];
			try
			{
				if (io.Read(reply) < SmartInfiniteProtocol.HeaderLength) continue;
			}
			catch (TimeoutException)
			{
				_logger?.LogWarning("[{Tag}] reply timed out on {Path}", _tag, _devicePath);
				return null;
			}

			int replyAddr = reply[2] | (reply[3] << 8);
			if (reply[1] != command || replyAddr != addr) continue;

			if (reply[SmartInfiniteProtocol.ResponseStatusOffset] != SmartInfiniteProtocol.ResponseSuccess)
			{
				_logger?.LogWarning("[{Tag}] cmd 0x{Cmd:X2} addr=0x{Addr:X4} status 0x{Status:X2} (expected 0x55)",
					_tag, command, addr, reply[SmartInfiniteProtocol.ResponseStatusOffset]);
				return null;
			}

			return reply;
		}

		return null;
	}

	/// <summary>
	/// Runs an operation, retrying when Windows rejects the first write on a handle it has not
	/// finished releasing.
	/// </summary>
	public T? WithRetry<T>(Func<T?> operation, string operationName, CancellationToken cancellationToken)
	{
		for (int attempt = 1; attempt <= MaxAttempts; attempt++)
		{
			// The client's deadline has expired: stop burning the shared gate on retries nobody
			// is waiting for. The device is left as-is; a partial transfer is never stored.
			if (cancellationToken.IsCancellationRequested)
			{
				_logger?.LogInformation("[{Tag}] {Op} cancelled before attempt {Attempt}", _tag, operationName, attempt);
				return default;
			}

			if (attempt > 1)
			{
				_logger?.LogInformation("[{Tag}] {Op} retry {Attempt}/{Max}", _tag, operationName, attempt, MaxAttempts);
				Thread.Sleep(RetryDelayMs);
			}

			try
			{
				var result = operation();
				if (result is not null && result is not false) return result;
			}
			catch (IOException ex)
			{
				// ERROR_INVALID_FUNCTION — the previous handle is still closing, or we picked an
				// input-only collection. Retrying covers the former.
				_logger?.LogWarning("[{Tag}] {Op} I/O error on {Path}: {Msg}", _tag, operationName, _devicePath, ex.Message);
			}
			catch (TimeoutException)
			{
				_logger?.LogWarning("[{Tag}] {Op} timed out on {Path}", _tag, operationName, _devicePath);
			}
		}

		return default;
	}
}
