using HidSharp;
using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

/// <summary>
/// Rampage (VID 10473 / PID 12888) key-remap over the Smart Infinite vendor protocol.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="RasterKeyboardKeyMappingService"/> and
/// <see cref="TRacerKeyboardKeyMappingService"/>, this firmware has <b>no separate user-override
/// layer</b> — the base key matrix is the live state. So the scancode-keyed probe methods those two
/// implement are meaningless here and return <c>null</c>; the slot-keyed
/// <see cref="ReadKeyMatrixAsync"/> is the read path, and slots come from
/// <c>RampageKeyCatalog</c> as compile-time constants rather than a runtime probe.
/// </para>
/// <para>
/// <b>Writes must transfer the whole 384-byte key table, not one slot.</b> Per the factory update
/// of 2026-08 (RM1): the write is a bare <c>0x18</c> transfer of exactly 384 bytes — no
/// <c>0x10</c>/<c>0x11</c> session bracket, which the factory says is not used for any command.
/// The procedure is: read the current table with <c>0x17</c>, patch the three bytes at
/// <c>index * 3</c>, then send the entire 384-byte table back with <c>0x18</c> in 7 packets
/// (6 × 56 + 48). PR #45 instead wrote 3 bytes at <c>slot * 3</c>, and earlier revisions of this
/// service sent 336 bytes inside a session bracket; the firmware acked both and persisted neither.
/// Do not "optimise" this back into a partial write.
/// </para>
/// <para>
/// <paramref name="maxPayload"/> is the table-transfer chunk size for the transport this service
/// was resolved for: 56 over the cable, <see cref="RampageProtocol.Receiver24GPayloadLength"/>
/// over the 2.4G receiver, which relays only 24-byte-payload packets (C12; hardware-confirmed
/// 2026-08-13 — a 56-byte read arrives misaligned and a 56-byte-chunked write is discarded).
/// </para>
/// </remarks>
public class RampageKeyboardKeyMappingService(
	HidDevice device, ILogger? logger = null, int maxPayload = RampageProtocol.MaxPayloadLength) : IKeyboardKeyMappingService
{
	// Windows can hold the previous HID handle in a closing state for tens of milliseconds after
	// the last stream was disposed, so the first Write() on a fresh handle fails with Win32 error 1
	// (ERROR_INVALID_FUNCTION). Retrying the whole open+exchange closes that race.
	private const int MaxAttempts = 4;
	private const int RetryDelayMs = 100;

	private const int StreamTimeoutMs = 500;

	// Pacing between packets. The firmware drops requests that arrive back-to-back too fast.
	private const int InterPacketDelayMs = 3;

	// Gap between finishing the write and reading it back to verify.
	private const int PostWriteSettleMs = 50;

	// FN's fixed slot in the vendor factory table. Logged as an alignment canary: on a correctly
	// aligned read it is always {0x60, 0x01, 0x00}. Duplicated from RampageKeyCatalog, which lives
	// in Common and is not referenced from Infrastructure.
	private const int RampageKeyCatalogFnSlot = 90;

	private readonly HidDevice _device = device ?? throw new ArgumentNullException(nameof(device));
	private readonly ILogger? _logger = logger;

	// Serialisation is the shared DeviceGate, NOT a private semaphore: the RGB service
	// opens the same vendor interface (both factories select it via SmartInfiniteInterfaceSelector),
	// and an RGB 0x15 write is documented to zero the key-assignment staging buffer — so a private
	// gate would let an RGB push land between 0x18 packets and silently void the key write. The gate
	// is static and keyed by DevicePath because the two service families sharing the interface have
	// different lifetimes — this key-mapping service is built fresh per call while the RGB service
	// is process-cached — so only a static gate spans both.

	/// <summary>
	/// Not supported on Rampage: there is no factory-vs-override split to probe, and slot numbers
	/// are known constants from the vendor's factory table. Always returns <c>null</c>.
	/// </summary>
	public Task<IReadOnlyDictionary<byte, byte>?> ProbeDefaultMatrixAsync(int maxSlot = 128, CancellationToken cancellationToken = default)
	{
		_logger?.LogDebug(
			"[KEY] Rampage does not implement the scancode probe — slots come from RampageKeyCatalog");
		return Task.FromResult<IReadOnlyDictionary<byte, byte>?>(null);
	}

	/// <summary>
	/// Not supported on Rampage: the base matrix is the live state, so there is no override layer.
	/// Callers want <see cref="ReadKeyMatrixAsync"/>. Always returns <c>null</c>.
	/// </summary>
	public Task<IReadOnlyDictionary<byte, byte>?> ProbeUserOverridesAsync(int maxSlot = 128, CancellationToken cancellationToken = default)
	{
		_logger?.LogDebug(
			"[KEY] Rampage has no user-override layer — use ReadKeyMatrixAsync instead");
		return Task.FromResult<IReadOnlyDictionary<byte, byte>?>(null);
	}

	public async Task<IReadOnlyDictionary<byte, KeyMatrixRecord>?> ReadKeyMatrixAsync(int maxSlot = 128, CancellationToken cancellationToken = default)
	{
		// Takes the same gate as the write AND the RGB service. Opening the section fires this read
		// while the user may already be clicking a key or an RGB push may be in flight; two
		// overlapping exchanges on one interface produce replies that satisfy each other's address
		// checks, and the spliced table would then become the basis of a whole-table write.
		using var gate = await DeviceGate.AcquireAsync(_device.DevicePath, cancellationToken).ConfigureAwait(false);

		var table = await ReadTableAsync(RampageProtocol.CmdReadKeyInfo, cancellationToken).ConfigureAwait(false);
		if (table is null)
		{
			_logger?.LogWarning("[KEY] Rampage key-matrix read failed on {Path}", _device.DevicePath);
			return null;
		}

		if (!RampageProtocol.IsPlausibleKeyTable(table, out var validSlots))
		{
			_logger?.LogError(
				"[KEY] Rampage key-matrix read looks misaligned ({Valid}/{Total} slots have a known TYPE byte) — discarding",
				validSlots, RampageProtocol.KeyCount);
			return null;
		}

		var map = RampageProtocol.ParseTable(table, maxSlot);
		_logger?.LogInformation("[KEY] Rampage read key matrix: {Count} populated slots, slot 90 (FN) = {Fn}",
			map.Count, RampageProtocol.ReadSlot(table, RampageKeyCatalogFnSlot));
		return map;
	}

	public async Task<bool> SetKeyMappingAsync(byte matrixIndex, byte type, byte code1, byte code2, CancellationToken cancellationToken = default)
	{
		if (matrixIndex >= RampageProtocol.MatrixSlotCount)
		{
			_logger?.LogWarning("[KEY] Rampage slot {Slot} is outside the addressable range", matrixIndex);
			return false;
		}

		var wanted = new KeyMatrixRecord(type, code1, code2);

		_logger?.LogInformation(
			"[KEY] Rampage SetKeyMapping slot={Slot} record={Record} on {Path}",
			matrixIndex, wanted, _device.DevicePath);

		using var gate = await DeviceGate.AcquireAsync(_device.DevicePath, cancellationToken).ConfigureAwait(false);
		try
		{
			// The whole read-modify-write happens on ONE open handle, with no session bracket:
			// the factory update of 2026-08 says 0x10/0x11 must not be sent at all, and the key
			// write is a bare 0x18 transfer of the full 384-byte table. The read ("before changing
			// keys, send 0x17 once to read the current key data") stays, because a single-slot
			// change must be written back as the whole table.
			var ok = await Task.Run(() =>
				WithRetry(() => ReadModifyWriteSlot(matrixIndex, wanted, cancellationToken) ? (object?)true : null,
					nameof(SetKeyMappingAsync), cancellationToken) is not null, CancellationToken.None).ConfigureAwait(false);

			return ok;
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[KEY] Rampage SetKeyMapping failed for slot {Slot}", matrixIndex);
			return false;
		}
	}

	/// <summary>
	/// Reads slot <paramref name="matrixIndex"/> through <b>both</b> table-read commands and logs
	/// each result.
	/// </summary>
	/// <remarks>
	/// The command semantics come from PR #45, whose read path is now known to have had a
	/// one-packet misalignment bug — so "0x16 = factory, 0x17 = current" was never actually
	/// verified. If the two disagree after a write, the write is landing and the verification has
	/// been reading the wrong (immutable) table all along, which would explain every failure so far.
	/// </remarks>
	private void LogBothTableViews(HidStream stream, byte matrixIndex, string tag, CancellationToken cancellationToken)
	{
		var viaDefault = ReadTableOnStream(stream, RampageProtocol.CmdReadDefaultKeyInfo, cancellationToken);
		var viaCurrent = ReadTableOnStream(stream, RampageProtocol.CmdReadKeyInfo, cancellationToken);

		var fromDefault = viaDefault is null
			? "read failed"
			: RampageProtocol.ReadSlot(viaDefault, matrixIndex).ToString();
		var fromCurrent = viaCurrent is null
			? "read failed"
			: RampageProtocol.ReadSlot(viaCurrent, matrixIndex).ToString();

		_logger?.LogWarning(
			"[KEY-DIAG] {Tag} slot {Slot}: via 0x16 (\"default\") = {Default}, via 0x17 (\"current\") = {Current}{Note}",
			tag, matrixIndex, fromDefault, fromCurrent,
			fromDefault == fromCurrent ? "  (identical)" : "  <-- THE TWO TABLES DIFFER");
	}

	private bool ReadModifyWriteSlot(byte matrixIndex, KeyMatrixRecord wanted, CancellationToken cancellationToken)
	{
		if (!_device.TryOpen(out var stream))
		{
			_logger?.LogWarning("[KEY] Rampage TryOpen failed on {Path}", _device.DevicePath);
			return false;
		}

		using (stream)
		{
			stream.ReadTimeout = StreamTimeoutMs;
			stream.WriteTimeout = StreamTimeoutMs;

			// Read the live table. A failed or implausible read must abort: writing back a
			// table we did not read correctly would scramble every key on the board.
			var table = ReadTableOnStream(stream, RampageProtocol.CmdReadKeyInfo, cancellationToken);
			if (table is null)
			{
				_logger?.LogWarning(
					"[KEY] Rampage aborting write to slot {Slot}: could not read the current table", matrixIndex);
				return false;
			}

			if (!RampageProtocol.IsPlausibleKeyTable(table, out var validSlots))
			{
				_logger?.LogError(
					"[KEY] Rampage aborting write to slot {Slot}: the table we read does not look like a key matrix "
					+ "({Valid}/{Total} slots have a known TYPE byte). Refusing to write it back.",
					matrixIndex, validSlots, RampageProtocol.KeyCount);
				return false;
			}

			var before = RampageProtocol.ReadSlot(table, matrixIndex);
			RampageProtocol.PatchSlot(table, matrixIndex, wanted);
			_logger?.LogInformation("[KEY] Rampage slot {Slot} {Before} -> {Wanted}, writing the full table back",
				matrixIndex, before, wanted);

			if (!WriteTableOnStream(stream, table, cancellationToken))
				return false;

			// Settle before reading back — the config store is not necessarily updated by the
			// time the last packet is acknowledged.
			Thread.Sleep(PostWriteSettleMs);

			var after = ReadTableOnStream(stream, RampageProtocol.CmdReadKeyInfo, cancellationToken);
			if (after is null)
			{
				_logger?.LogWarning("[KEY] Rampage wrote slot {Slot} but could not read it back", matrixIndex);
				return false;
			}

			var actual = RampageProtocol.ReadSlot(after, matrixIndex);
			if (actual != wanted)
			{
				_logger?.LogWarning(
					"[KEY] Rampage slot {Slot} verify mismatch: wanted {Wanted}, device has {Actual}. "
					+ "The write was acknowledged but did not stick.",
					matrixIndex, wanted, actual);

				// Cross-check the other read command before concluding the write failed — we may
				// simply be verifying against the wrong table.
				LogBothTableViews(stream, matrixIndex, "post-write", cancellationToken);
				return false;
			}

			_logger?.LogInformation("[KEY] Rampage slot {Slot} verified as {Record}", matrixIndex, wanted);
			return true;
		}
	}

	/// <summary>
	/// Reads a full 384-byte table, or <c>null</c> if any chunk failed. Never returns a partial
	/// table — callers use the result as the basis of a whole-table write.
	/// </summary>
	private Task<byte[]?> ReadTableAsync(byte readCommand, CancellationToken cancellationToken) =>
		Task.Run(() => WithRetry(() => ReadTable(readCommand, cancellationToken), nameof(ReadTableAsync), cancellationToken),
			CancellationToken.None);

	/// <summary>
	/// Runs <paramref name="operation"/> against a freshly opened stream, retrying when Windows
	/// rejects the first write on a handle it has not finished releasing.
	/// </summary>
	private T? WithRetry<T>(Func<T?> operation, string tag, CancellationToken cancellationToken) where T : class
	{
		for (int attempt = 1; attempt <= MaxAttempts; attempt++)
		{
			// The client's deadline has expired: stop burning the gate on retries nobody is
			// waiting for. The device is left as-is; a partial transfer is never stored.
			if (cancellationToken.IsCancellationRequested)
			{
				_logger?.LogInformation("[KEY] Rampage {Tag} cancelled before attempt {Attempt}", tag, attempt);
				return null;
			}

			if (attempt > 1)
			{
				_logger?.LogInformation("[KEY] Rampage {Tag} retry {Attempt}/{Max}", tag, attempt, MaxAttempts);
				Thread.Sleep(RetryDelayMs);
			}

			try
			{
				var result = operation();
				if (result is not null) return result;
			}
			catch (IOException ex)
			{
				// ERROR_INVALID_FUNCTION — the previous handle is still closing, or we picked the
				// input-only vendor interface. Retrying covers the former.
				_logger?.LogWarning("[KEY] Rampage {Tag} I/O error on {Path}: {Msg}",
					tag, _device.DevicePath, ex.Message);
			}
			catch (TimeoutException)
			{
				_logger?.LogWarning("[KEY] Rampage {Tag} timed out on {Path}", tag, _device.DevicePath);
			}
		}

		return null;
	}

	/// <summary>
	/// Files one reply into <paramref name="table"/> at the offset it echoes back. Returns true only
	/// if this reply filled a chunk that was not already filled.
	/// </summary>
	private bool TryStoreReply(
		byte[] reply,
		byte readCommand,
		IReadOnlyList<(ushort Addr, int Length)> chunks,
		byte[] table,
		bool[] filled)
	{
		// Ignore anything that isn't a reply to the command we're reading — a straggler from an
		// earlier exchange on the same handle lands here.
		if (reply[1] != readCommand)
			return false;

		if (reply[RampageProtocol.ResponseStatusOffset] != RampageProtocol.ResponseSuccess)
		{
			_logger?.LogWarning("[KEY] Rampage cmd 0x{Cmd:X2} reply status 0x{Status:X2} (expected 0x55)",
				reply[1], reply[RampageProtocol.ResponseStatusOffset]);
			return false;
		}

		int addr = reply[2] | (reply[3] << 8);
		int index = -1;
		for (int i = 0; i < chunks.Count; i++)
		{
			if (chunks[i].Addr == addr) { index = i; break; }
		}

		if (index < 0)
		{
			_logger?.LogWarning("[KEY] Rampage reply echoed unexpected addr 0x{Addr:X4}", addr);
			return false;
		}

		if (filled[index])
			return false; // duplicate, already have it

		Buffer.BlockCopy(reply, RampageProtocol.ResponsePayloadOffset, table, addr, chunks[index].Length);
		filled[index] = true;
		return true;
	}

	/// <summary>Opens its own handle. Used by the standalone read path.</summary>
	private byte[]? ReadTable(byte readCommand, CancellationToken cancellationToken)
	{
		if (!_device.TryOpen(out var stream))
		{
			_logger?.LogWarning("[KEY] Rampage TryOpen failed on {Path}", _device.DevicePath);
			return null;
		}

		using (stream)
		{
			stream.ReadTimeout = StreamTimeoutMs;
			stream.WriteTimeout = StreamTimeoutMs;

			return ReadTableOnStream(stream, readCommand, cancellationToken);
		}
	}

	/// <summary>
	/// Reads a whole key table on an already-open handle. Returns null unless every chunk arrived
	/// — callers use the result as the basis of a write, so a partial table must never escape.
	/// </summary>
	private byte[]? ReadTableOnStream(HidStream stream, byte readCommand, CancellationToken cancellationToken)
	{
		// The buffer is the full 384-byte firmware reserve, but only the first KeyDataSize bytes
		// are meaningful, so the read covers 336 bytes over the cable (or 288 in 24-byte packets
		// over the receiver) — the shape the vendor driver's own reads use. The remaining reserve
		// stays zero, which is what the firmware keeps there; the 384-byte write sends it back as
		// zeros.
		var table = new byte[RampageProtocol.KeyMatrixTableSize];
		var chunks = RampageProtocol.EnumerateChunks(maxPayload: maxPayload);

		// Replies are matched by the address they echo back (bytes 2-3), never by arrival order.
		// Order cannot be trusted: replies run one command behind the requests, and we must not
		// drain the pipe up front, because reading before the first real request corrupts the
		// HidSharp Windows stream so that every later Write() fails. Address matching is
		// self-correcting: a lagged, reordered or duplicated reply still lands in the right slot,
		// and anything unrecognised is ignored.
		var filled = new bool[chunks.Count];
		int remaining = chunks.Count;

		for (int i = 0; i < chunks.Count && remaining > 0; i++)
		{
			if (cancellationToken.IsCancellationRequested) return null;

			var (addr, length) = chunks[i];
			stream.Write(RampageProtocol.BuildReadCommand(readCommand, addr, (byte)length));

			var reply = ReadReply(stream);
			if (reply is not null && TryStoreReply(reply, readCommand, chunks, table, filled))
				remaining--;

			Thread.Sleep(InterPacketDelayMs);
		}

		for (int attempt = 0; attempt < chunks.Count * 2 && remaining > 0; attempt++)
		{
			if (cancellationToken.IsCancellationRequested) return null;

			int missing = Array.IndexOf(filled, false);
			if (missing < 0) break;

			var (addr, length) = chunks[missing];
			stream.Write(RampageProtocol.BuildReadCommand(readCommand, addr, (byte)length));

			var reply = ReadReply(stream);
			if (reply is not null && TryStoreReply(reply, readCommand, chunks, table, filled))
				remaining--;

			Thread.Sleep(InterPacketDelayMs);
		}

		if (remaining > 0)
		{
			var missingOffsets = string.Join(", ",
				chunks.Where((_, i) => !filled[i]).Select(c => $"0x{c.Addr:X4}"));
			_logger?.LogWarning(
				"[KEY] Rampage cmd 0x{Cmd:X2} read incomplete — {N} chunk(s) never arrived: {Offsets}",
				readCommand, remaining, missingOffsets);
			return null;
		}

		return table;
	}

	/// <summary>
	/// Writes the whole key table on an already-open handle. Returns <c>false</c> when cancelled
	/// mid-transfer — safe to abandon, the firmware discards a table shorter than 384 bytes.
	/// </summary>
	private bool WriteTableOnStream(HidStream stream, byte[] table, CancellationToken cancellationToken)
	{
		// The whole 384-byte key table, every time — never a single slot, never the 336-byte
		// capture shape. See the class remarks (factory update of 2026-08).
		var chunks = RampageProtocol.EnumerateWriteChunks(maxPayload);
		int acked = 0;

		foreach (var (addr, length) in chunks)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				_logger?.LogInformation(
					"[KEY] Rampage write cancelled at addr=0x{Addr:X4}; the partial transfer is discarded by the firmware", addr);
				return false;
			}

			var request = RampageProtocol.BuildCommand(
				RampageProtocol.CmdSetKeyInfo, addr, table.AsSpan(addr, length));
			stream.Write(request);

			// Match the ack by the address it echoes back, exactly as the read path does. Reading
			// positionally does not work: the reply pipe runs one command behind, so the first
			// "ack" is really a leftover read reply and every packet gets credited with its
			// predecessor's status — while the LAST packet's ack is never read at all. That made
			// the log claim "6 acked" from five confirmations plus one unrelated reply.
			var ack = ReadAckFor(stream, RampageProtocol.CmdSetKeyInfo, addr);
			if (ack is null)
			{
				_logger?.LogWarning(
					"[KEY] Rampage write addr=0x{Addr:X4} req={Req}: NO MATCHING ACK — the firmware never acknowledged this packet",
					addr, BitConverter.ToString(request, 0, 8));
			}
			else
			{
				var status = ack[RampageProtocol.ResponseStatusOffset];
				if (status == RampageProtocol.ResponseSuccess) acked++;

				_logger?.LogInformation(
					"[KEY] Rampage write addr=0x{Addr:X4} req={Req} ack={Ack} status=0x{Status:X2}{Note}",
					addr,
					BitConverter.ToString(request, 0, 8),
					BitConverter.ToString(ack, 0, 8),
					status,
					status == RampageProtocol.ResponseSuccess ? "" : "  <-- NOT 0x55, firmware rejected this packet");
			}

			Thread.Sleep(InterPacketDelayMs);
		}

		_logger?.LogInformation(
			"[KEY] Rampage wrote the key matrix ({Packets} packets, {Size} bytes, {Acked}/{Total} acked 0x55)",
			chunks.Count, chunks.Sum(c => c.Length), acked, chunks.Count);
		return true;
	}


	/// <summary>
	/// Reads replies until one echoes <paramref name="command"/> and <paramref name="addr"/>,
	/// discarding anything else (leftovers from an earlier phase, the Start ack, the lag).
	/// Returns <c>null</c> if no matching reply arrives.
	/// </summary>
	private byte[]? ReadAckFor(HidStream stream, byte command, ushort addr)
	{
		// Bounded so a silent device cannot stall the session; a few extra reads is enough to clear
		// the one-command lag plus any stragglers.
		const int MaxDrain = 4;

		for (int attempt = 0; attempt < MaxDrain; attempt++)
		{
			var reply = ReadReply(stream);
			if (reply is null) return null;

			int replyAddr = reply[2] | (reply[3] << 8);
			if (reply[1] == command && replyAddr == addr)
				return reply;
		}

		return null;
	}

	private byte[]? ReadReply(HidStream stream)
	{
		var reply = new byte[RampageProtocol.ReportLength];
		try
		{
			int read = stream.Read(reply);
			if (read >= RampageProtocol.ResponsePayloadOffset) return reply;

			_logger?.LogWarning("[KEY] Rampage short reply ({N} bytes)", read);
			return null;
		}
		catch (TimeoutException)
		{
			_logger?.LogWarning("[KEY] Rampage reply timed out on {Path}", _device.DevicePath);
			return null;
		}
	}
}
