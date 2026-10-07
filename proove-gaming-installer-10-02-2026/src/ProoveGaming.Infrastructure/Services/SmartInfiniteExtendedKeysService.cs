using HidSharp;
using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

/// <summary>
/// Extended Keys (vendor: "advanced keys") over the Smart Infinite vendor protocol — Cyrex and Aress.
/// </summary>
/// <remarks>
/// <para>
/// Two regions are involved and both are written on every change: the advanced-key storage area
/// (<c>0x3A</c> read / <c>0x3B</c> write, size = Device Info byte 8 × 64) holding the records, and
/// the key matrix (<c>0x17</c> / <c>0x18</c>), where every participating key's triplet becomes a
/// <c>{TYPE, off_lo, off_hi}</c> pointer into the area. The record format was extracted from the
/// vendor's Keyborder-v3 app (<c>docs/knowledge-base/smart-infinite-protocol/advanced-keys-rapid-snap.md</c>),
/// so this service is deliberately defensive: <b>nothing is written until the gate passes</b> —
/// Device Info must report remappable keys and a non-zero area, and the factory table, the current
/// table and the whole area must read back and parse. A board that fails the gate reports
/// "unsupported" and is never written to — with one exception: an area that does not parse against
/// the pointers is a torn write rather than a configuration, and is reset in place
/// (<see cref="RepairIncoherentBoard"/>) so the section stays reachable. A board that could not be
/// talked to at all is neither — it is reported as a failure, because "unsupported" hides the
/// section for the session and a cable hiccup must not do that.
/// </para>
/// <para>
/// A write is a read-modify-write of both regions on one handle under the shared
/// <see cref="DeviceGate"/>: the area is rebuilt from the desired managed records plus every
/// unmanaged record found on the board (kept byte-for-byte and re-pointed), the factory triplet is
/// restored on every slot leaving a record, every participating slot is pointed at its record, the
/// full area is written (zero-padded), then the full 384-byte matrix, and both are read back and
/// verified. Cancellation is honoured up to the first packet and not after it: the two regions only
/// mean anything together. An invalid request is refused outright; a read-back mismatch means the
/// board is torn rather than that the request was wrong, so the set is laid down once more in the
/// same call against a fresh snapshot and only a second mismatch is final. A transport failure
/// (<c>null</c>) is the transport's own business to retry.
/// </para>
/// <para>
/// <b>Extension point:</b> <see cref="IsManagedType"/> and <see cref="TryDecodeManaged"/> /
/// <see cref="TryEncodeManaged"/> are the per-mode switches. This build manages Rapid Snap,
/// Instant Release (SOCD), Mod-Tap and Toggle; a record of any other type — DKS, END — is preserved
/// and its slots reported as blocked, as is a managed-type record this build cannot re-encode (a SOCD
/// priority nibble past 3, an MT / TGL time past 1000 ms, a mouse-button output, or a pair record
/// whose own slot bytes disagree with the slots pointing at it).
/// </para>
/// </remarks>
public class SmartInfiniteExtendedKeysService : IKeyboardExtendedKeysService
{
	// The firmware needs a moment after a write before the store reads back.
	private const int PostWriteSettleMs = 80;

	// How many times one WriteAsync lays the set down before it gives up. Two, not one, because a
	// verify mismatch leaves the board torn: see WriteOnce.
	private const int WriteAttempts = 2;

	private readonly string _devicePath;
	private readonly ILogger? _logger;
	private readonly string _tag;
	private readonly SmartInfiniteTransport _transport;

	public SmartInfiniteExtendedKeysService(HidDevice device, string modelName, ILogger? logger = null)
	{
		ArgumentNullException.ThrowIfNull(device);
		_devicePath = device.DevicePath;
		_logger = logger;
		_tag = $"EXT {modelName}";
		_transport = new SmartInfiniteTransport(device, _tag, logger);
	}

	/// <summary>Test seam: run the service against a scripted transport instead of real HID.</summary>
	internal SmartInfiniteExtendedKeysService(SmartInfiniteTransport transport, string modelName, ILogger? logger = null)
	{
		_transport = transport ?? throw new ArgumentNullException(nameof(transport));
		_devicePath = transport.DevicePath;
		_logger = logger;
		_tag = $"EXT {modelName}";
	}

	public async Task<ExtendedKeysReadResult> ReadAsync(CancellationToken cancellationToken = default)
	{
		using var gate = await DeviceGate.AcquireAsync(_devicePath, cancellationToken).ConfigureAwait(false);
		try
		{
			// WithRetry re-runs only a null, so a verdict — the board has no advanced-key area — is
			// returned once and never retried, while a board that did not answer is tried again and,
			// if it still does not, reported as Failed. The two must not collapse into one value:
			// "unsupported" hides the section for the session, and a cable hiccup has no business
			// doing that.
			var result = await Task.Run(() =>
				_transport.WithRetry(() => ReadOnce(cancellationToken), nameof(ReadAsync), cancellationToken),
				CancellationToken.None).ConfigureAwait(false);
			return result ?? ExtendedKeysReadResult.Failed;
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[{Tag}] extended-keys read failed", _tag);
			return ExtendedKeysReadResult.Failed;
		}
	}

	public async Task<ExtendedKeysWriteResult?> WriteAsync(IReadOnlyList<ExtendedKeyEntry> desired, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(desired);
		_logger?.LogInformation("[{Tag}] WriteAsync {Count} managed record(s) on {Path}", _tag, desired.Count, _devicePath);

		using var gate = await DeviceGate.AcquireAsync(_devicePath, cancellationToken).ConfigureAwait(false);
		try
		{
			// Rejected is a fact about the request or the firmware, not a glitch: WithRetry re-runs
			// only transient failures (null), so a refused set is not re-written three more times.
			return await Task.Run(() =>
				_transport.WithRetry(() => WriteOnce(desired, cancellationToken), nameof(WriteAsync), cancellationToken),
				CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[{Tag}] extended-keys write failed", _tag);
			return null;
		}
	}

	// ── per-mode switches (the extension point) ───────────────────────────────────────────────

	/// <summary>Record TYPEs this build manages — decoded on read, rebuilt on write. Everything else is preserved.</summary>
	private static bool IsManagedType(byte type) =>
		type is SmartInfiniteProtocol.TypeExtendedRapidSnap or SmartInfiniteProtocol.TypeExtendedSocd
			or SmartInfiniteProtocol.TypeExtendedModTap or SmartInfiniteProtocol.TypeExtendedToggle;

	/// <summary>
	/// A record this build owns: a managed TYPE that also decodes into an entry it can write back.
	/// The read path and the write path must agree on this — a managed-type record the decoder
	/// refuses (foreign SOCD nibble, MT / TGL mouse-button output) is reported as blocked on read, so the
	/// rebuild has to carry it verbatim too, not drop it and hand its slot back to the factory key.
	/// </summary>
	private static bool IsManagedRecord(AdvancedKeyRecord record) =>
		IsManagedType(record.Type) && TryDecodeManaged(record) is not null;

	private static ExtendedKeyEntry? TryDecodeManaged(AdvancedKeyRecord record)
	{
		if (record.Type == SmartInfiniteProtocol.TypeExtendedRapidSnap &&
			SmartInfiniteAdvancedKeyArea.TryDecodeRapidSnap(record.Bytes, out var rs) &&
			PointersNameTheSamePair(record, rs.Slot1, rs.Slot2))
			return ExtendedKeyEntry.RapidSnap(rs.Slot1, rs.Slot2);

		// A priority nibble past 3 parses (the area stays aligned) but means nothing to this build, so
		// the record is left alone — decoding it would let a rewrite of the full set refuse everything.
		if (record.Type == SmartInfiniteProtocol.TypeExtendedSocd &&
			SmartInfiniteAdvancedKeyArea.TryDecodeSocd(record.Bytes, out var socd) &&
			IsSocdPriority(socd.Priority) &&
			PointersNameTheSamePair(record, socd.Slot1, socd.Slot2))
			return ExtendedKeyEntry.Socd(socd.Slot1, socd.Slot2, socd.Priority);

		// Single-key: the physical key is the one slot pointing at the record. A time past the slider
		// range or a mouse-button output (codeType 3) is nothing this build can write back, so such a
		// record stays unmanaged for the same reason as a foreign SOCD nibble.
		if (record.Type == SmartInfiniteProtocol.TypeExtendedModTap &&
			record.Slots.Count == 1 &&
			SmartInfiniteAdvancedKeyArea.TryDecodeModTap(record.Bytes, out var mt) &&
			IsTimeMs(mt.TimeMs) &&
			SmartInfiniteAdvancedKeyArea.KeyCodeToRecord(mt.HoldCodeType, mt.HoldCode) is { } hold &&
			SmartInfiniteAdvancedKeyArea.KeyCodeToRecord(mt.TapCodeType, mt.TapCode) is { } tap)
			return ExtendedKeyEntry.ModTap(record.Slots[0], mt.TimeMs, ExtendedKeyCode.FromMatrixRecord(hold), ExtendedKeyCode.FromMatrixRecord(tap));

		// Toggle: the same single-key rules with one output.
		if (record.Type == SmartInfiniteProtocol.TypeExtendedToggle &&
			record.Slots.Count == 1 &&
			SmartInfiniteAdvancedKeyArea.TryDecodeToggle(record.Bytes, out var tgl) &&
			IsTimeMs(tgl.TimeMs) &&
			SmartInfiniteAdvancedKeyArea.KeyCodeToRecord(tgl.CodeType, tgl.Code) is { } toggled)
			return ExtendedKeyEntry.Toggle(record.Slots[0], tgl.TimeMs, ExtendedKeyCode.FromMatrixRecord(toggled));

		return null;
	}

	/// <summary>
	/// The two sides of a pair record must name the same two keys: the slots written inside it (bytes
	/// 2 and 5) and the slots whose triplets point at it. They can disagree — a torn write lands the
	/// area and loses the <c>0x18</c>, and the previous layout's pointers find a record of the right
	/// TYPE at the old offset. Decoding that would report a pair the board does not have, and a
	/// re-read after a failed write would save it into the user's rows (review m6). So it is not a
	/// record this build owns: it stays unmanaged, preserved byte-for-byte with its slots blocked —
	/// the same treatment, for the same reason, as an MT / TGL record more than one slot points at.
	/// </summary>
	private static bool PointersNameTheSamePair(AdvancedKeyRecord record, int slot1, int slot2) =>
		record.Slots.Count == 2 &&
		((record.Slots[0] == slot1 && record.Slots[1] == slot2) ||
			(record.Slots[0] == slot2 && record.Slots[1] == slot1));

	private static bool IsSocdPriority(int priority) =>
		priority is >= SmartInfiniteAdvancedKeyArea.SocdPriorityLastInput and <= SmartInfiniteAdvancedKeyArea.SocdPriorityMax;

	private static bool IsTimeMs(int timeMs) =>
		timeMs is >= SmartInfiniteAdvancedKeyArea.TimeMsMin and <= SmartInfiniteAdvancedKeyArea.TimeMsMax;

	/// <summary>
	/// Encodes one desired entry, or explains why it cannot be. Pair modes take their codes from the
	/// factory table; the single-key modes carry their outputs in the entry. Slot range, duplicates
	/// and blocked slots are checked by the caller; this is the per-mode wire format.
	/// </summary>
	private static byte[]? TryEncodeManaged(ExtendedKeyEntry entry, byte[] factory, out string? reason)
	{
		reason = null;
		switch (entry.Mode)
		{
			case ExtendedKeyMode.RapidSnap:
			{
				// The mirror of the single-key guards below, and it is load-bearing: a pair record has
				// no room for a time, a priority or an output, so an entry carrying one would be
				// written, verified and reported as Verified for a set the board does not hold. The
				// App would then never see its desired set on the next read, so the "already matches"
				// skip could not fire and every preset apply would rewrite the flash.
				if (!IsPlainPair(entry, priorityAllowed: false, out reason)) return null;

				var keys = PairKeyCodes(entry, factory, out reason);
				if (keys is null) return null;
				return SmartInfiniteAdvancedKeyArea.EncodeRapidSnap(entry.Slot1, keys.Value.Key1, entry.Slot2, keys.Value.Key2);
			}

			case ExtendedKeyMode.Socd:
			{
				// Same rule, except that SOCD's LEN nibble does carry the priority.
				if (!IsPlainPair(entry, priorityAllowed: true, out reason)) return null;

				if (!IsSocdPriority(entry.Priority))
				{
					reason = $"SOCD priority {entry.Priority} is not one of 0..{SmartInfiniteAdvancedKeyArea.SocdPriorityMax}";
					return null;
				}

				var keys = PairKeyCodes(entry, factory, out reason);
				if (keys is null) return null;
				return SmartInfiniteAdvancedKeyArea.EncodeSocd(entry.Slot1, keys.Value.Key1, entry.Slot2, keys.Value.Key2, entry.Priority);
			}

			case ExtendedKeyMode.ModTap:
			{
				if (entry.Slot2 != ExtendedKeyEntry.NoSlot || entry.Priority != 0)
				{
					reason = "a Mod-Tap entry carries no second slot and no priority";
					return null;
				}

				if (!IsTimeMs(entry.TimeMs))
				{
					reason = $"Mod-Tap time {entry.TimeMs} ms is outside {SmartInfiniteAdvancedKeyArea.TimeMsMin}..{SmartInfiniteAdvancedKeyArea.TimeMsMax}";
					return null;
				}

				var hold = OutputKeyCode(entry.HoldKey, "hold", out reason);
				if (hold is null) return null;
				var tap = OutputKeyCode(entry.TapKey, "tap", out reason);
				if (tap is null) return null;
				return SmartInfiniteAdvancedKeyArea.EncodeModTap(entry.TimeMs, hold.Value, tap.Value);
			}

			case ExtendedKeyMode.Toggle:
			{
				// The toggled output rides HoldKey; a tap output has no place in the record.
				if (entry.Slot2 != ExtendedKeyEntry.NoSlot || entry.Priority != 0 || !entry.TapKey.IsNone)
				{
					reason = "a Toggle entry carries no second slot, no priority and no tap output";
					return null;
				}

				if (!IsTimeMs(entry.TimeMs))
				{
					reason = $"Toggle time {entry.TimeMs} ms is outside {SmartInfiniteAdvancedKeyArea.TimeMsMin}..{SmartInfiniteAdvancedKeyArea.TimeMsMax}";
					return null;
				}

				var output = OutputKeyCode(entry.HoldKey, "toggled", out reason);
				if (output is null) return null;
				return SmartInfiniteAdvancedKeyArea.EncodeToggle(entry.TimeMs, output.Value);
			}

			default:
				reason = $"mode 0x{(byte)entry.Mode:X2} is not managed by this build";
				return null;
		}
	}

	// A pair record is two slots and (for SOCD) a priority — nothing else fits in it. Both of its
	// slots are checked for range, FN and duplication by the caller; what is checked here is that the
	// single-key fields are absent, so that what the board ends up holding is what the entry asked for.
	private static bool IsPlainPair(ExtendedKeyEntry entry, bool priorityAllowed, out string? reason)
	{
		reason = null;
		if (entry.TimeMs != 0 || !entry.HoldKey.IsNone || !entry.TapKey.IsNone || (!priorityAllowed && entry.Priority != 0))
		{
			reason = $"a {entry.Mode} entry carries no time, no outputs{(priorityAllowed ? "" : " and no priority")}";
			return false;
		}

		return true;
	}

	// Both keys of a pair carry their FACTORY identity (the vendor's getKeyCodeAndType), never a remap.
	private static ((byte CodeType, byte Code) Key1, (byte CodeType, byte Code) Key2)? PairKeyCodes(
		ExtendedKeyEntry entry, byte[] factory, out string? reason)
	{
		reason = null;
		var key1 = SmartInfiniteAdvancedKeyArea.KeyCodeFromRecord(SmartInfiniteProtocol.ReadSlot(factory, entry.Slot1));
		var key2 = SmartInfiniteAdvancedKeyArea.KeyCodeFromRecord(SmartInfiniteProtocol.ReadSlot(factory, entry.Slot2));
		if (key1 is null || key2 is null)
		{
			reason = $"slot {(key1 is null ? entry.Slot1 : entry.Slot2)} is not a plain key in the factory table";
			return null;
		}

		return (key1.Value, key2.Value);
	}

	// An MT / TGL output is any keyboard key or modifier, encoded from its own {0x10, mask, usage}
	// identity — it need not be a key of this board, so the factory table plays no part.
	private static (byte CodeType, byte Code)? OutputKeyCode(ExtendedKeyCode output, string role, out string? reason)
	{
		reason = null;

		// A record carrying BOTH a mask and a usage names two keys, and the wire format has room for
		// one. KeyCodeFromRecord answers with the usage, which is the vendor's rule for a key's own
		// factory identity but wrong for an output the caller chose: the board would be written with a
		// key nobody asked for, verified, and the caller's row could never match the read-back — so
		// the App's self-heal would rewrite the flash on every page open and never converge (review
		// m1). Refuse the whole entry instead, the way the pair modes refuse a foreign field (S3).
		if (output.TypeByte == KeyMatrixRecord.TypeKeyboardKey && output.Code1 != 0 && output.Code2 != 0)
		{
			reason = $"the {role} output {output} carries both a modifier mask and a usage";
			return null;
		}

		var code = SmartInfiniteAdvancedKeyArea.KeyCodeFromRecord(output.ToMatrixRecord());
		if (code is null)
			reason = $"the {role} output {output} is not a plain keyboard key";
		return code;
	}

	// ── one-shot operations, each on a single fresh handle ────────────────────────────────────

	/// <summary>
	/// Everything one operation reads before it decides anything: identity, both matrices, the area and
	/// its parsed records. <paramref name="Repaired"/> says the snapshot describes a board this read
	/// had to reset to get it (<see cref="RepairIncoherentBoard"/>), so its records are what survived
	/// the reset rather than a configuration.
	/// </summary>
	private sealed record BoardSnapshot(
		SmartInfiniteDeviceInfo Info,
		byte[] Factory,
		byte[] Current,
		byte[] Area,
		IReadOnlyList<AdvancedKeyRecord> Records,
		bool Repaired);

	/// <summary>
	/// Why one operation has no snapshot: <see cref="Unsupported"/> is a verdict about the board and
	/// is final, <see cref="Failed"/> is a transport fact and is worth another attempt. Collapsing the
	/// two is exactly what made a cable hiccup hide the section for the session (review S2).
	/// </summary>
	private enum SnapshotOutcome { Ok, Unsupported, Failed }

	private ExtendedKeysReadResult? ReadOnce(CancellationToken cancellationToken)
	{
		return _transport.OnHandle<ExtendedKeysReadResult?>(io =>
		{
			var (outcome, snapshot) = ReadSnapshot(io, cancellationToken);
			// Failed stays null so WithRetry runs the whole open+exchange again; only the verdict is
			// an answer, and only an answer stops the retries.
			if (snapshot == null)
				return outcome == SnapshotOutcome.Unsupported ? ExtendedKeysReadResult.Unsupported : null;

			var entries = new List<ExtendedKeyEntry>();
			var blocked = new List<int>();
			int usedBytes = 0;
			foreach (var record in snapshot.Records)
			{
				usedBytes += record.Length;
				var entry = IsManagedRecord(record) ? TryDecodeManaged(record) : null;
				if (entry is null)
					blocked.AddRange(record.Slots);
				else
					entries.Add(entry);
			}

			int areaBytes = snapshot.Info.AdvancedKeyAreaBytes;
			var state = new ExtendedKeysState(
				entries,
				MaxRecords: areaBytes / SmartInfiniteAdvancedKeyArea.RapidSnapRecordBytes,
				blocked,
				areaBytes,
				FreeBytes: areaBytes - usedBytes,
				snapshot.Repaired);

			_logger?.LogInformation("[{Tag}] read extended keys: {Managed} managed, {Blocked} blocked slot(s), {Free}/{Area} bytes free",
				_tag, entries.Count, blocked.Count, state.FreeBytes, areaBytes);
			return ExtendedKeysReadResult.Read(state);
		});
	}

	private ExtendedKeysWriteResult? WriteOnce(IReadOnlyList<ExtendedKeyEntry> desired, CancellationToken cancellationToken)
	{
		return _transport.OnHandle<ExtendedKeysWriteResult?>(io =>
		{
			// A verify mismatch is not a refusal of the request — the plan was accepted and the packets
			// went out — it says one of the two regions did not land, which is the torn board of review
			// m3. Answering Rejected there hands the App a board whose very next read is repaired, i.e.
			// wiped, and with persist:true that empty set lands in the user's rows. So the set is laid
			// down once more in the same call, against a snapshot read after the failure: if the read
			// finds an incoherent board the repair makes it coherent first, and the write then restores
			// exactly what the user asked for. A second mismatch is the firmware genuinely disagreeing
			// and is final. Everything else keeps its old verdict: a request PlanWrite refuses is wrong
			// on any snapshot, and a transport failure is WithRetry's business, not this loop's.
			for (int attempt = 1; attempt <= WriteAttempts; attempt++)
			{
				var (outcome, snapshot) = ReadSnapshot(io, cancellationToken);
				// A board without an advanced-key area will not grow one on a retry: that is a refusal,
				// not a glitch, and re-running the gate three more times only delays the answer.
				if (snapshot == null)
					return outcome == SnapshotOutcome.Unsupported ? ExtendedKeysWriteResult.Rejected : null;

				var plan = PlanWrite(snapshot, desired);
				if (plan == null) return ExtendedKeysWriteResult.Rejected;
				var (area, matrix, recordOffsets) = plan.Value;

				_logger?.LogInformation("[{Tag}] writing advanced-key area ({AreaBytes} B, {Records} record(s)) then the full {MatrixBytes}-byte matrix (attempt {Attempt}/{Attempts})",
					_tag, area.Length, recordOffsets.Count, SmartInfiniteProtocol.KeyMatrixTableSize, attempt, WriteAttempts);

				// The last point at which abandoning the write costs nothing — and only on the first
				// attempt, because by the second the board is already torn and leaving it that way is
				// the outcome this retry exists to avoid. Past it the two regions are written with
				// CancellationToken.None: a transfer that stops between them leaves the pointers and
				// the area disagreeing, which reads back as "unsupported" and takes the whole section —
				// including the DEFAULT SETTINGS control that would repair it — away from the user
				// (review S1). Finishing takes a few hundred milliseconds on a caller that has already
				// gone away; a torn board lasts until the next read repairs it.
				if (attempt == 1 && cancellationToken.IsCancellationRequested)
				{
					_logger?.LogInformation("[{Tag}] extended-keys write cancelled before the first packet — nothing written", _tag);
					return null;
				}

				// Area first, matrix second — the vendor's order, and the safe one: a pointer must never
				// aim at a record that is not there yet.
				if (!_transport.WriteTable(io, SmartInfiniteProtocol.CmdWriteAdvancedKeyArea, area, area.Length, CancellationToken.None))
					return null;
				Thread.Sleep(PostWriteSettleMs);

				if (!_transport.WriteTable(io, SmartInfiniteProtocol.CmdSetKeyInfo, matrix, SmartInfiniteProtocol.KeyMatrixTableSize, CancellationToken.None))
					return null;
				Thread.Sleep(PostWriteSettleMs);

				var areaAfter = ReadArea(io, snapshot.Info, cancellationToken);
				var matrixAfter = ReadTable(io, SmartInfiniteProtocol.CmdReadKeyInfo, snapshot.Info, cancellationToken);
				if (areaAfter == null || matrixAfter == null)
				{
					_logger?.LogWarning("[{Tag}] wrote both regions but could not read them back", _tag);
					return null;
				}

				if (!VerifyWrite(snapshot.Info, recordOffsets, areaAfter, matrix, matrixAfter))
					continue;

				_logger?.LogInformation("[{Tag}] extended keys verified: {Count} managed record(s)", _tag, desired.Count);
				return ExtendedKeysWriteResult.Verified;
			}

			_logger?.LogWarning("[{Tag}] extended-keys write did not verify after {Attempts} attempts — rejecting", _tag, WriteAttempts);
			return ExtendedKeysWriteResult.Rejected;
		});
	}

	/// <summary>
	/// True when the board holds exactly what was written: every record byte-for-byte where it was put
	/// — this is what proves an unmanaged record moved intact; the zero padding past the last record is
	/// the firmware's business — and every slot of the matrix, the pointers, the restored factory
	/// triplets and the untouched rest alike.
	/// </summary>
	private bool VerifyWrite(
		SmartInfiniteDeviceInfo info,
		IReadOnlyList<(byte[] Bytes, int Offset)> records,
		byte[] areaAfter,
		byte[] matrix,
		byte[] matrixAfter)
	{
		foreach (var (bytes, offset) in records)
		{
			if (!areaAfter.AsSpan(offset, bytes.Length).SequenceEqual(bytes))
			{
				_logger?.LogWarning("[{Tag}] verify mismatch: record at 0x{Offset:X4} reads back as {Actual}, wanted {Wanted}",
					_tag, offset, Convert.ToHexString(areaAfter, offset, bytes.Length), Convert.ToHexString(bytes));
				return false;
			}
		}

		for (int slot = 0; slot < info.MatrixSlots; slot++)
		{
			var wanted = SmartInfiniteProtocol.ReadSlot(matrix, slot);
			var actual = SmartInfiniteProtocol.ReadSlot(matrixAfter, slot);
			if (actual != wanted)
			{
				_logger?.LogWarning("[{Tag}] verify mismatch: slot {Slot} wanted {Wanted}, device has {Actual}",
					_tag, slot, wanted, actual);
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Turns the desired managed set into the two buffers to write, or <c>null</c> with a logged
	/// reason when the request is invalid: an unmanaged mode, a slot out of range, FN or a
	/// placeholder, a slot owned by an unmanaged record, a slot named twice, or an area overflow.
	/// Nothing here touches the device — a refusal writes no packet.
	/// </summary>
	private (byte[] Area, byte[] Matrix, IReadOnlyList<(byte[] Bytes, int Offset)> Records)? PlanWrite(
		BoardSnapshot snapshot, IReadOnlyList<ExtendedKeyEntry> desired)
	{
		var info = snapshot.Info;
		var unmanaged = snapshot.Records.Where(r => !IsManagedRecord(r)).ToList();
		var blockedSlots = new HashSet<int>(unmanaged.SelectMany(r => r.Slots));

		var encoded = new List<byte[]>(desired.Count);
		var claimed = new HashSet<int>();
		foreach (var entry in desired)
		{
			var slots = entry.IsPair ? new[] { entry.Slot1, entry.Slot2 } : new[] { entry.Slot1 };
			foreach (int slot in slots)
			{
				if (slot < 0 || slot >= info.MatrixSlots)
					return Refuse($"slot {slot} is outside this board's {info.MatrixSlots}-slot matrix");

				var factorySlot = SmartInfiniteProtocol.ReadSlot(snapshot.Factory, slot);
				if (factorySlot.IsFn)
					return Refuse($"slot {slot} is FN");
				// All four modes need a real key under the slot. The pair modes had this incidentally,
				// because KeyCodeFromRecord refuses a factory triplet that names no key; MT / TGL never
				// read that triplet, so a record on a matrix placeholder was accepted — it would eat
				// area bytes, block the slot in Key Assignments and Macros, and never fire (review S4).
				if (SmartInfiniteAdvancedKeyArea.KeyCodeFromRecord(factorySlot) is null)
					return Refuse($"slot {slot} is not a plain key in the factory table");
				if (blockedSlots.Contains(slot))
					return Refuse($"slot {slot} is owned by an advanced-key record this build does not manage");
				if (!claimed.Add(slot))
					return Refuse($"slot {slot} appears in two entries");
			}

			var bytes = TryEncodeManaged(entry, snapshot.Factory, out var reason);
			if (bytes == null)
				return Refuse(reason ?? "encoder refused the entry");
			encoded.Add(bytes);
		}

		var all = new List<byte[]>(encoded.Count + unmanaged.Count);
		all.AddRange(encoded);
		all.AddRange(unmanaged.Select(r => r.Bytes));

		var built = SmartInfiniteAdvancedKeyArea.BuildArea(all, info.AdvancedKeyAreaBytes);
		if (built == null)
			return Refuse($"the rebuilt area does not fit the {info.AdvancedKeyAreaBytes}-byte storage area");
		var (area, offsets) = built.Value;

		var matrix = (byte[])snapshot.Current.Clone();

		// Every slot that pointed at a managed record goes back to its factory key first; the
		// pointers below then re-claim the slots that stay in a record.
		foreach (var record in snapshot.Records)
		{
			if (!IsManagedRecord(record)) continue;
			foreach (int slot in record.Slots)
				SmartInfiniteProtocol.PatchSlot(matrix, slot, SmartInfiniteProtocol.ReadSlot(snapshot.Factory, slot));
		}

		for (int i = 0; i < desired.Count; i++)
		{
			var entry = desired[i];
			var pointer = SmartInfiniteAdvancedKeyArea.PointerRecord((byte)entry.Mode, offsets[i]);
			SmartInfiniteProtocol.PatchSlot(matrix, entry.Slot1, pointer);
			if (entry.IsPair)
				SmartInfiniteProtocol.PatchSlot(matrix, entry.Slot2, pointer);
		}

		for (int i = 0; i < unmanaged.Count; i++)
		{
			var record = unmanaged[i];
			var pointer = SmartInfiniteAdvancedKeyArea.PointerRecord(record.Type, offsets[encoded.Count + i]);
			foreach (int slot in record.Slots)
				SmartInfiniteProtocol.PatchSlot(matrix, slot, pointer);
		}

		var placed = new List<(byte[], int)>(all.Count);
		for (int i = 0; i < all.Count; i++)
			placed.Add((all[i], offsets[i]));

		return (area, matrix, placed);

		(byte[], byte[], IReadOnlyList<(byte[], int)>)? Refuse(string reason)
		{
			_logger?.LogWarning("[{Tag}] refusing extended-keys write: {Reason}", _tag, reason);
			return null;
		}
	}

	/// <summary>
	/// The gate. Device Info must be valid, report remappable keys and a non-zero advanced-key area;
	/// the factory and current tables must read back and look like key matrices; the whole area must
	/// read back and parse — or be repairable, see <see cref="RepairIncoherentBoard"/>. There is no
	/// snapshot on any failure, and the outcome says whether that is the board's verdict
	/// (<see cref="SnapshotOutcome.Unsupported"/>, final) or a transport fact
	/// (<see cref="SnapshotOutcome.Failed"/>, worth retrying). Either way the board is never written
	/// to on the strength of a table this gate rejected.
	/// </summary>
	private (SnapshotOutcome Outcome, BoardSnapshot? Snapshot) ReadSnapshot(ISmartInfiniteIo io, CancellationToken cancellationToken)
	{
		var info = _transport.ReadDeviceInfo(io);
		if (info == null) return (SnapshotOutcome.Failed, null);
		_logger?.LogInformation("[{Tag}] device info: {Info}", _tag, info);

		if (!info.SupportsExtendedKeys)
		{
			_logger?.LogInformation("[{Tag}] no advanced-key area or no remappable keys — extended keys unsupported: {Info}", _tag, info);
			return (SnapshotOutcome.Unsupported, null);
		}

		var (factoryOutcome, factory) = ReadPlausibleTable(io, SmartInfiniteProtocol.CmdReadDefaultKeyInfo, info, "factory", cancellationToken);
		if (factory == null) return (factoryOutcome, null);

		var (currentOutcome, current) = ReadPlausibleTable(io, SmartInfiniteProtocol.CmdReadKeyInfo, info, "current", cancellationToken);
		if (current == null) return (currentOutcome, null);

		var area = ReadArea(io, info, cancellationToken);
		if (area == null)
		{
			_logger?.LogWarning("[{Tag}] advanced-key area read failed on {Path}", _tag, _devicePath);
			return (SnapshotOutcome.Failed, null);
		}

		var records = SmartInfiniteAdvancedKeyArea.ParseRecords(current, info.MatrixSlots, area);
		if (records == null)
			return RepairIncoherentBoard(io, info, factory, current, cancellationToken);

		foreach (var record in records)
			_logger?.LogInformation("[{Tag}] record type=0x{Type:X2} offset=0x{Offset:X4} bytes={Bytes} slots=[{Slots}]",
				_tag, record.Type, record.Offset, Convert.ToHexString(record.Bytes), string.Join(", ", record.Slots));

		return (SnapshotOutcome.Ok, new BoardSnapshot(info, factory, current, area, records, Repaired: false));
	}

	/// <summary>
	/// The pointers and the area disagree, which is what a torn two-region write leaves behind — a
	/// dropped <c>0x18</c> packet, or a transfer that stopped between the regions. Nothing can be
	/// rebuilt on top of it: every record is already unreachable, and reporting "unsupported" would
	/// hide the section and with it the one control (DEFAULT SETTINGS) that could rewrite the board
	/// (review S1). So the board is reset the way the vendor's own <c>restoreWindowsDefaultLayer</c>
	/// does it — every pointer slot back to its factory key, the area zeroed — and read again.
	/// </summary>
	/// <remarks>
	/// The reset is destructive, which is why it is gated this narrowly: <see cref="SmartInfiniteAdvancedKeyArea.ParseRecords"/>
	/// refuses only data that is incoherent in itself (a pointer past the area, a TYPE that does not
	/// match the record it aims at, two records overlapping), never a record of a type this build does
	/// not manage. A board the vendor app configured parses; a board with a torn write does not. Both
	/// transfers are run with <see cref="CancellationToken.None"/> for the reason the write path is —
	/// stopping halfway is what produced this state. Matrix first here, the mirror of the write path's
	/// order and safe for the same reason: dropping the pointers before the records they aim at leaves
	/// no pointer aimed at a hole.
	/// </remarks>
	private (SnapshotOutcome Outcome, BoardSnapshot? Snapshot) RepairIncoherentBoard(
		ISmartInfiniteIo io, SmartInfiniteDeviceInfo info, byte[] factory, byte[] current, CancellationToken cancellationToken)
	{
		_logger?.LogError("[{Tag}] advanced-key area does not parse against the matrix pointers — resetting the advanced keys of this board", _tag);

		var matrix = (byte[])current.Clone();
		for (int slot = 0; slot < info.MatrixSlots; slot++)
		{
			if (SmartInfiniteProtocol.IsExtendedKeyType(SmartInfiniteProtocol.ReadSlot(current, slot).Type))
				SmartInfiniteProtocol.PatchSlot(matrix, slot, SmartInfiniteProtocol.ReadSlot(factory, slot));
		}

		if (!_transport.WriteTable(io, SmartInfiniteProtocol.CmdSetKeyInfo, matrix, SmartInfiniteProtocol.KeyMatrixTableSize, CancellationToken.None))
			return (SnapshotOutcome.Failed, null);
		Thread.Sleep(PostWriteSettleMs);

		if (!_transport.WriteTable(io, SmartInfiniteProtocol.CmdWriteAdvancedKeyArea, new byte[info.AdvancedKeyAreaBytes], info.AdvancedKeyAreaBytes, CancellationToken.None))
			return (SnapshotOutcome.Failed, null);
		Thread.Sleep(PostWriteSettleMs);

		var (outcome, currentAfter) = ReadPlausibleTable(io, SmartInfiniteProtocol.CmdReadKeyInfo, info, "current", cancellationToken);
		if (currentAfter == null) return (outcome, null);

		var areaAfter = ReadArea(io, info, cancellationToken);
		if (areaAfter == null)
		{
			_logger?.LogWarning("[{Tag}] advanced-key area read failed after the reset on {Path}", _tag, _devicePath);
			return (SnapshotOutcome.Failed, null);
		}

		// One attempt, never a loop: a board that still does not parse is one this build cannot make
		// sense of, and the section stays hidden rather than the read rewriting the flash on every probe.
		var records = SmartInfiniteAdvancedKeyArea.ParseRecords(currentAfter, info.MatrixSlots, areaAfter);
		if (records == null)
		{
			_logger?.LogError("[{Tag}] advanced-key area still does not parse after the reset — refusing extended keys on this board", _tag);
			return (SnapshotOutcome.Unsupported, null);
		}

		_logger?.LogWarning("[{Tag}] advanced keys reset: the board's records were unreachable and have been dropped, {Records} left",
			_tag, records.Count);
		return (SnapshotOutcome.Ok, new BoardSnapshot(info, factory, currentAfter, areaAfter, records, Repaired: true));
	}

	// A matrix read that must also look like a matrix: a misaligned table would put pointers where
	// keys are and vice versa, and a write built on it would scramble the board. A table that did not
	// arrive is a transport failure; one that arrived misaligned is a board this build will not touch.
	private (SnapshotOutcome Outcome, byte[]? Table) ReadPlausibleTable(ISmartInfiniteIo io, byte readCommand, SmartInfiniteDeviceInfo info, string name, CancellationToken cancellationToken)
	{
		var table = ReadTable(io, readCommand, info, cancellationToken);
		if (table == null)
		{
			_logger?.LogWarning("[{Tag}] {Name} table read failed on {Path}", _tag, name, _devicePath);
			return (SnapshotOutcome.Failed, null);
		}

		if (!SmartInfiniteProtocol.IsPlausibleKeyTable(table, info.MatrixSlots, out var valid))
		{
			_logger?.LogError("[{Tag}] {Name} table looks misaligned ({Valid}/{Total} slots carry a known TYPE) — discarding",
				_tag, name, valid, info.MatrixSlots);
			return (SnapshotOutcome.Unsupported, null);
		}

		return (SnapshotOutcome.Ok, table);
	}

	// Reads are sized from the matrix the device declares; the buffer is reserve-sized because the
	// write path sends the full KeyMatrixTableSize and its zero tail must exist in the buffer.
	private byte[]? ReadTable(ISmartInfiniteIo io, byte readCommand, SmartInfiniteDeviceInfo info, CancellationToken cancellationToken) =>
		_transport.ReadTable(io, readCommand, info.MatrixBytes, SmartInfiniteProtocol.MaxTableBytes, cancellationToken);

	// Exact-size buffer: the whole area IS the transfer, both ways.
	private byte[]? ReadArea(ISmartInfiniteIo io, SmartInfiniteDeviceInfo info, CancellationToken cancellationToken) =>
		_transport.ReadTable(io, SmartInfiniteProtocol.CmdReadAdvancedKeyArea, info.AdvancedKeyAreaBytes, info.AdvancedKeyAreaBytes, cancellationToken);
}
