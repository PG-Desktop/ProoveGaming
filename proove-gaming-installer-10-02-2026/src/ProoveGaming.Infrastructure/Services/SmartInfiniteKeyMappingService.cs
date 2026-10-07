using HidSharp;
using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

/// <summary>
/// Key remapping over the Smart Infinite vendor protocol — Cyrex, Aress and (where the firmware
/// allows it) Rampage.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="RasterKeyboardKeyMappingService"/> and
/// <see cref="TRacerKeyboardKeyMappingService"/>, this firmware has <b>no separate user-override
/// layer</b> — the base key matrix is the live state, so the scancode-keyed probe methods those two
/// implement are meaningless here and return <c>null</c>. <see cref="ReadKeyMatrixAsync"/> is the
/// read path, and slot numbers come from the per-model catalog as compile-time constants rather than
/// a runtime probe.
/// </para>
/// <para>
/// <b>Nothing is written until Device Info has been read.</b> Byte 6 of that block is the number of
/// remappable keys, and this service refuses to write when it is zero. (Rampage reports 0 yet
/// accepts the factory-shape write, which is why it has its own gate-free service — see
/// <see cref="RampageKeyboardKeyMappingService"/>.) Byte 4 gives the matrix size, which differs per
/// model (Cyrex 75 slots / 225 bytes, Rampage 128 / 384) and sizes the <b>read</b> transfers; the
/// <b>write</b> transfer is always the full <see cref="SmartInfiniteProtocol.KeyMatrixTableSize"/>.
/// </para>
/// <para>
/// <b>Writes transfer the whole 384-byte key table, not one slot and not just the populated
/// slots.</b> Per the factory's 2026-08 clarifications (C1/C3): the write is a bare <c>0x18</c>
/// transfer of exactly <see cref="SmartInfiniteProtocol.KeyMatrixTableSize"/> bytes — no
/// <c>0x10</c>/<c>0x11</c> bracket, and never a shorter transfer, which the firmware acknowledges
/// and discards (or keeps only in RAM). The procedure: read the current table with <c>0x17</c>,
/// patch the three bytes at <c>slot * 3</c>, send the full 384-byte table back with <c>0x18</c>.
/// Do not "optimise" this into a partial write.
/// </para>
/// <para>
/// The HID transfer mechanics (fresh handles, reply matching, retries) live in
/// <see cref="SmartInfiniteTransport"/>, shared with <see cref="SmartInfiniteActuationService"/>.
/// </para>
/// </remarks>
public class SmartInfiniteKeyMappingService : IKeyboardKeyMappingService
{
	// The firmware needs a moment after a write before the store reads back.
	private const int PostWriteSettleMs = 80;

	private readonly string _devicePath;
	private readonly ILogger? _logger;
	private readonly string _tag;
	private readonly SmartInfiniteTransport _transport;

	public SmartInfiniteKeyMappingService(HidDevice device, string modelName, ILogger? logger = null)
	{
		ArgumentNullException.ThrowIfNull(device);
		_devicePath = device.DevicePath;
		_logger = logger;
		_tag = $"KEY {modelName}";
		_transport = new SmartInfiniteTransport(device, _tag, logger);
	}

	/// <summary>Test seam: run the service against a scripted transport instead of real HID.</summary>
	internal SmartInfiniteKeyMappingService(SmartInfiniteTransport transport, string modelName, ILogger? logger = null)
	{
		_transport = transport ?? throw new ArgumentNullException(nameof(transport));
		_devicePath = transport.DevicePath;
		_logger = logger;
		_tag = $"KEY {modelName}";
	}

	/// <summary>
	/// Not supported on this family: there is no factory-vs-override split to probe, and slot numbers
	/// are known constants from the per-model catalog. Always <c>null</c>.
	/// </summary>
	public Task<IReadOnlyDictionary<byte, byte>?> ProbeDefaultMatrixAsync(int maxSlot = 128, CancellationToken cancellationToken = default)
	{
		_logger?.LogDebug("[{Tag}] no scancode probe on this family — slots come from the catalog", _tag);
		return Task.FromResult<IReadOnlyDictionary<byte, byte>?>(null);
	}

	/// <summary>
	/// Not supported on this family: the base matrix is the live state, so there is no override
	/// layer. Callers want <see cref="ReadKeyMatrixAsync"/>. Always <c>null</c>.
	/// </summary>
	public Task<IReadOnlyDictionary<byte, byte>?> ProbeUserOverridesAsync(int maxSlot = 128, CancellationToken cancellationToken = default)
	{
		_logger?.LogDebug("[{Tag}] no user-override layer — use ReadKeyMatrixAsync", _tag);
		return Task.FromResult<IReadOnlyDictionary<byte, byte>?>(null);
	}

	public async Task<IReadOnlyDictionary<byte, KeyMatrixRecord>?> ReadKeyMatrixAsync(int maxSlot = 128, CancellationToken cancellationToken = default)
	{
		using var gate = await DeviceGate.AcquireAsync(_devicePath, cancellationToken).ConfigureAwait(false);
		try
		{
			return await Task.Run(() =>
				_transport.WithRetry(() => ReadMatrixOnce(cancellationToken), nameof(ReadKeyMatrixAsync), cancellationToken),
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
			_tag, matrixIndex, wanted, _devicePath);

		using var gate = await DeviceGate.AcquireAsync(_devicePath, cancellationToken).ConfigureAwait(false);
		try
		{
			return await Task.Run(() =>
				_transport.WithRetry(() => WriteSlotOnce(matrixIndex, wanted, cancellationToken) ? (object?)true : null,
					nameof(SetKeyMappingAsync), cancellationToken) is not null, CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[{Tag}] SetKeyMapping failed for slot {Slot}", _tag, matrixIndex);
			return false;
		}
	}

	// ── one-shot operations, each on a single fresh handle ────────────────────────────────────

	private IReadOnlyDictionary<byte, KeyMatrixRecord>? ReadMatrixOnce(CancellationToken cancellationToken)
	{
		return _transport.OnHandle(io =>
		{
			var info = _transport.ReadDeviceInfo(io);
			if (info == null) return null;
			_logger?.LogInformation("[{Tag}] device info: {Info}", _tag, info);

			var table = ReadKeyTable(io, info, cancellationToken);
			if (table == null)
			{
				_logger?.LogWarning("[{Tag}] key-matrix read failed on {Path}", _tag, _devicePath);
				return null;
			}

			if (!SmartInfiniteProtocol.IsPlausibleKeyTable(table, info.MatrixSlots, out var valid))
			{
				_logger?.LogError(
					"[{Tag}] key-matrix read looks misaligned ({Valid}/{Total} slots carry a known TYPE) — discarding",
					_tag, valid, info.MatrixSlots);
				return null;
			}

			var map = SmartInfiniteProtocol.ParseTable(table, info.MatrixSlots);
			_logger?.LogInformation("[{Tag}] read key matrix: {Count} slots of {Total}", _tag, map.Count, info.MatrixSlots);
			return map;
		});
	}

	private bool WriteSlotOnce(byte matrixIndex, KeyMatrixRecord wanted, CancellationToken cancellationToken)
	{
		return _transport.OnHandle(io =>
		{
			var info = _transport.ReadDeviceInfo(io);
			if (info == null) return false;
			_logger?.LogInformation("[{Tag}] device info: {Info}", _tag, info);

			if (!info.SupportsKeyRemap)
			{
				_logger?.LogError(
					"[{Tag}] refusing to write: the firmware reports {Keys} remappable keys. Key remapping is not " +
					"enabled in this build — writes are acknowledged but discarded. Device info: {Info}",
					_tag, info.KeyCount, info);
				return false;
			}

			if (matrixIndex >= info.MatrixSlots)
			{
				_logger?.LogWarning("[{Tag}] slot {Slot} is outside this model's {Total}-slot matrix",
					_tag, matrixIndex, info.MatrixSlots);
				return false;
			}

			// Read-modify-write. A failed or implausible read must abort: writing back a table we did
			// not read correctly would scramble every key on the board.
			var table = ReadKeyTable(io, info, cancellationToken);
			if (table == null)
			{
				_logger?.LogWarning("[{Tag}] aborting write to slot {Slot}: could not read the current table",
					_tag, matrixIndex);
				return false;
			}

			if (!SmartInfiniteProtocol.IsPlausibleKeyTable(table, info.MatrixSlots, out var valid))
			{
				_logger?.LogError(
					"[{Tag}] aborting write to slot {Slot}: the table read back does not look like a key matrix " +
					"({Valid}/{Total} slots carry a known TYPE). Refusing to write it back.",
					_tag, matrixIndex, valid, info.MatrixSlots);
				return false;
			}

			var before = SmartInfiniteProtocol.ReadSlot(table, matrixIndex);
			SmartInfiniteProtocol.PatchSlot(table, matrixIndex, wanted);

			// The transfer is the full 384-byte reserve (C1/C3), not the populated MatrixBytes: the
			// table buffer is already reserve-sized with a zero tail, which is what the firmware's
			// own empty slots hold.
			_logger?.LogInformation("[{Tag}] slot {Slot} {Before} -> {Wanted}, writing back the full {Bytes}-byte table",
				_tag, matrixIndex, before, wanted, SmartInfiniteProtocol.KeyMatrixTableSize);

			if (!_transport.WriteTable(io, SmartInfiniteProtocol.CmdSetKeyInfo, table,
				SmartInfiniteProtocol.KeyMatrixTableSize, cancellationToken))
				return false;
			Thread.Sleep(PostWriteSettleMs);

			var after = ReadKeyTable(io, info, cancellationToken);
			if (after == null)
			{
				_logger?.LogWarning("[{Tag}] wrote slot {Slot} but could not read it back", _tag, matrixIndex);
				return false;
			}

			var actual = SmartInfiniteProtocol.ReadSlot(after, matrixIndex);
			if (actual != wanted)
			{
				_logger?.LogWarning(
					"[{Tag}] slot {Slot} verify mismatch: wanted {Wanted}, device has {Actual}",
					_tag, matrixIndex, wanted, actual);
				return false;
			}

			_logger?.LogInformation("[{Tag}] slot {Slot} verified as {Record}", _tag, matrixIndex, wanted);
			return true;
		});
	}

	// Reads are sized from the matrix the device declares; the buffer is reserve-sized because the
	// write path sends the full KeyMatrixTableSize and its zero tail must exist in the buffer.
	private byte[]? ReadKeyTable(ISmartInfiniteIo io, SmartInfiniteDeviceInfo info, CancellationToken cancellationToken) =>
		_transport.ReadTable(io, SmartInfiniteProtocol.CmdReadKeyInfo, info.MatrixBytes,
			SmartInfiniteProtocol.MaxTableBytes, cancellationToken);
}
