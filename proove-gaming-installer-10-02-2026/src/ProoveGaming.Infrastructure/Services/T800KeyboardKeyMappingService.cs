using HidSharp;
using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

/// <summary>
/// Key remapping over the Rongyuan RY5088 vendor protocol — T800.
/// </summary>
/// <remarks>
/// <para>
/// Like <see cref="SmartInfiniteKeyMappingService"/>, this firmware has <b>no factory-vs-override
/// layer</b> — the live key matrix is the state — so the scancode-keyed probe methods return
/// <c>null</c> and <see cref="ReadKeyMatrixAsync"/> is the read path. Slot numbers come from
/// <c>T800KeyCatalog</c>, generated from the device's own factory table.
/// </para>
/// <para>
/// <b>Unlike Smart Infinite, a write here touches exactly one slot.</b> RY5088 accepts
/// <c>SET_KEYMATRIX</c> addressed to a single key (<c>Type</c> = slot rather than 255), verified on
/// hardware to change that slot and nothing else. So there is no read-modify-write of the full
/// table, and consequently no way for a failed or misread table to corrupt keys the user never
/// touched — the failure mode that shapes the Smart Infinite implementation simply does not exist
/// here. Reads are for <i>showing</i> current assignments, never a precondition for writing.
/// </para>
/// <para>
/// Records are 4 bytes on the wire (<c>[Type, Code1, Code2, Code3]</c>) but <c>Code3</c> is
/// <c>0x00</c> throughout the factory table, so they map onto the App's 3-byte
/// <see cref="KeyMatrixRecord"/> without loss. Note a plain key's type byte is <c>0x00</c> here, not
/// Smart Infinite's <c>0x10</c>, and modifiers are ordinary HID usages <c>0xE0</c>-<c>0xE7</c>
/// rather than a bitmask — a key on this board is identified by its usage code alone.
/// </para>
/// </remarks>
public class T800KeyboardKeyMappingService : IKeyboardKeyMappingService
{
	// Windows can hold a closing HID handle for tens of milliseconds, so the first exchange on a
	// fresh handle can fail with ERROR_INVALID_FUNCTION. Same rationale as the Smart Infinite service.
	private const int MaxAttempts = 3;
	private const int RetryDelayMs = 100;

	// The firmware needs a moment between a feature write and the following read; measured during
	// the hardware write test.
	private const int InterPacketDelayMs = 20;
	private const int PostWriteSettleMs = 300;

	private readonly HidDevice _device;
	private readonly ILogger? _logger;
	private readonly string _tag;

	public T800KeyboardKeyMappingService(HidDevice device, string modelName, ILogger? logger = null)
	{
		_device = device ?? throw new ArgumentNullException(nameof(device));
		_logger = logger;
		_tag = $"KEY {modelName}";
	}

	/// <summary>Not supported: no factory-vs-override split to probe. Always <c>null</c>.</summary>
	public Task<IReadOnlyDictionary<byte, byte>?> ProbeDefaultMatrixAsync(int maxSlot = 128, CancellationToken cancellationToken = default)
	{
		_logger?.LogDebug("[{Tag}] no scancode probe on this family — slots come from the catalog", _tag);
		return Task.FromResult<IReadOnlyDictionary<byte, byte>?>(null);
	}

	/// <summary>Not supported: the base matrix is the live state. Callers want <see cref="ReadKeyMatrixAsync"/>.</summary>
	public Task<IReadOnlyDictionary<byte, byte>?> ProbeUserOverridesAsync(int maxSlot = 128, CancellationToken cancellationToken = default)
	{
		_logger?.LogDebug("[{Tag}] no user-override layer — use ReadKeyMatrixAsync", _tag);
		return Task.FromResult<IReadOnlyDictionary<byte, byte>?>(null);
	}

	public async Task<IReadOnlyDictionary<byte, KeyMatrixRecord>?> ReadKeyMatrixAsync(int maxSlot = 128, CancellationToken cancellationToken = default)
	{
		using var gate = await DeviceGate.AcquireAsync(_device.DevicePath, cancellationToken).ConfigureAwait(false);
		try
		{
			return await Task.Run(
				() => WithRetry(() => ReadMatrixOnce(cancellationToken), nameof(ReadKeyMatrixAsync), cancellationToken),
				CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[{Tag}] key-matrix read failed", _tag);
			return null;
		}
	}

	public async Task<bool> SetKeyMappingAsync(byte matrixIndex, byte type, byte code1, byte code2, CancellationToken cancellationToken = default)
	{
		var wanted = new KeyMatrixRecord(type, code1, code2);
		_logger?.LogInformation("[{Tag}] SetKeyMapping slot={Slot} record={Record} on {Path}",
			_tag, matrixIndex, wanted, _device.DevicePath);

		if (matrixIndex >= Ry5088Protocol.MatrixSlots)
		{
			_logger?.LogWarning("[{Tag}] slot {Slot} is outside this model's {Total}-slot matrix",
				_tag, matrixIndex, Ry5088Protocol.MatrixSlots);
			return false;
		}

		using var gate = await DeviceGate.AcquireAsync(_device.DevicePath, cancellationToken).ConfigureAwait(false);
		try
		{
			return await Task.Run(() =>
				WithRetry(() => WriteSlotOnce(matrixIndex, wanted, cancellationToken) ? (object?)true : null,
					nameof(SetKeyMappingAsync), cancellationToken) is not null,
				CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[{Tag}] SetKeyMapping failed for slot {Slot}", _tag, matrixIndex);
			return false;
		}
	}

	// ── one-shot operations ───────────────────────────────────────────────────────────────────

	private IReadOnlyDictionary<byte, KeyMatrixRecord>? ReadMatrixOnce(CancellationToken cancellationToken)
	{
		var matrix = ReadMatrix(cancellationToken);
		if (matrix == null)
		{
			_logger?.LogWarning("[{Tag}] key-matrix read failed on {Path}", _tag, _device.DevicePath);
			return null;
		}

		var map = Ry5088Protocol.ParseMatrix(matrix);
		if (!Ry5088Protocol.IsPlausibleKeyTable(map))
		{
			_logger?.LogError(
				"[{Tag}] key-matrix read decoded only {Count} slots — treating as a failed read, not an empty board",
				_tag, map.Count);
			return null;
		}

		_logger?.LogInformation("[{Tag}] read key matrix: {Count} slots", _tag, map.Count);
		return map;
	}

	private bool WriteSlotOnce(byte matrixIndex, KeyMatrixRecord wanted, CancellationToken cancellationToken)
	{
		if (!_device.TryOpen(out var stream))
		{
			_logger?.LogWarning("[{Tag}] TryOpen failed on {Path}", _tag, _device.DevicePath);
			return false;
		}

		using (stream)
		{
			stream.SetFeature(Ry5088Protocol.BuildSetKeyRecord(
				matrixIndex, wanted.Type, wanted.CodeLow, wanted.CodeHigh));
			Thread.Sleep(PostWriteSettleMs);
		}

		// The firmware does not ack a feature write, so read the slot back: that is the only way to
		// tell an applied write from a discarded one.
		var after = ReadMatrix(cancellationToken);
		if (after == null)
		{
			_logger?.LogWarning("[{Tag}] wrote slot {Slot} but could not read it back", _tag, matrixIndex);
			return false;
		}

		var actual = Ry5088Protocol.ParseMatrix(after).TryGetValue(matrixIndex, out var record)
			? record
			: default;

		if (actual != wanted)
		{
			_logger?.LogWarning("[{Tag}] slot {Slot} verify mismatch: wanted {Wanted}, device has {Actual}",
				_tag, matrixIndex, wanted, actual);
			return false;
		}

		_logger?.LogInformation("[{Tag}] slot {Slot} verified as {Record}", _tag, matrixIndex, wanted);
		return true;
	}

	// ── transfers ─────────────────────────────────────────────────────────────────────────────

	/// <summary>
	/// Reads the whole 512-byte matrix. Returns <c>null</c> unless every page arrived — a partial
	/// table must never escape, because the caller renders it as the board's current assignments.
	/// </summary>
	/// <remarks>
	/// Callers hold <see cref="DeviceGate"/>. Eight request/reply pairs on an interface the
	/// lighting service also writes to means an ungated read can be handed an LED reply mid-way and
	/// copy it into the matrix as a page of garbage — <see cref="IsEcho"/> will not catch that,
	/// because the reply is genuinely not our own request.
	/// </remarks>
	private byte[]? ReadMatrix(CancellationToken cancellationToken)
	{
		if (!_device.TryOpen(out var stream))
		{
			_logger?.LogWarning("[{Tag}] TryOpen failed on {Path}", _tag, _device.DevicePath);
			return null;
		}

		using (stream)
		{
			var matrix = new byte[Ry5088Protocol.MatrixPages * Ry5088Protocol.PageBytes];
			var reply = new byte[Ry5088Protocol.ReportLength];

			for (byte page = 0; page < Ry5088Protocol.MatrixPages; page++)
			{
				// Between packets, per the interface's cancellation contract: a single HID exchange
				// is at most one stream-timeout long, and abandoning a READ is always safe.
				if (cancellationToken.IsCancellationRequested)
				{
					_logger?.LogInformation("[{Tag}] key-matrix read cancelled at page {Page}", _tag, page);
					return null;
				}

				var request = Ry5088Protocol.BuildGetKeyMatrixPage(page);
				stream.SetFeature(request);
				Thread.Sleep(InterPacketDelayMs);

				Array.Clear(reply);
				stream.GetFeature(reply);

				// A rejected command leaves GetFeature returning the driver's cached copy of the
				// request. That is indistinguishable from a reply except by comparing the two.
				if (IsEcho(request, reply))
				{
					_logger?.LogWarning("[{Tag}] page {Page} came back as our own request — command rejected",
						_tag, page);
					return null;
				}

				Buffer.BlockCopy(reply, 1, matrix, page * Ry5088Protocol.PageBytes, Ry5088Protocol.PageBytes);
			}

			return matrix;
		}
	}

	private static bool IsEcho(byte[] request, byte[] reply)
	{
		for (int i = 1; i <= 8; i++)
			if (request[i] != reply[i]) return false;
		return true;
	}

	private T? WithRetry<T>(Func<T?> operation, string tag, CancellationToken cancellationToken)
	{
		for (int attempt = 1; attempt <= MaxAttempts; attempt++)
		{
			// Between attempts, not mid-exchange: retrying against a wedged device is exactly what
			// the App's deadline is trying to stop.
			if (cancellationToken.IsCancellationRequested) return default;

			if (attempt > 1)
			{
				_logger?.LogInformation("[{Tag}] {Op} retry {Attempt}/{Max}", _tag, tag, attempt, MaxAttempts);
				Thread.Sleep(RetryDelayMs);
			}

			try
			{
				var result = operation();
				if (result is not null && result is not false) return result;
			}
			catch (IOException ex)
			{
				_logger?.LogWarning("[{Tag}] {Op} I/O error on {Path}: {Msg}", _tag, tag, _device.DevicePath, ex.Message);
			}
			catch (TimeoutException)
			{
				_logger?.LogWarning("[{Tag}] {Op} timed out on {Path}", _tag, tag, _device.DevicePath);
			}
		}

		return default;
	}
}
