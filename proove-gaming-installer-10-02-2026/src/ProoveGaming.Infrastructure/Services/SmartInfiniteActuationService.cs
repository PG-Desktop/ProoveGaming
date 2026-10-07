using HidSharp;
using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

/// <summary>
/// Global actuation (key press force) over the Smart Infinite vendor protocol — Cyrex and Aress.
/// </summary>
/// <remarks>
/// <para>
/// The travel-area commands (<c>0x38</c> read / <c>0x39</c> write) come from the vendor's
/// Keyborder-v3 app rather than a hardware capture, so this service is deliberately defensive:
/// <b>nothing is written until the geometry gate passes</b> — Device Info's magnetic fields must
/// describe a coherent area (<see cref="SmartInfiniteTravelArea.ValidateGeometry"/>) and the
/// current area must read back completely. A board that fails the gate reports "unsupported" and
/// is never written to.
/// </para>
/// <para>
/// A write is a read-modify-write of the whole area: the current table is read, only the four
/// edited fields are patched on every slot (axis-type nibble and dead zones are preserved
/// byte-for-byte), the full area is written back and then re-read to verify. Same session gate as
/// every other Smart Infinite service — the firmware allows one session per interface. The HID
/// transfer mechanics live in <see cref="SmartInfiniteTransport"/>, shared with
/// <see cref="SmartInfiniteKeyMappingService"/>.
/// </para>
/// </remarks>
public class SmartInfiniteActuationService : IKeyboardActuationService
{
	// The firmware needs a moment after a write before the store reads back.
	private const int PostWriteSettleMs = 80;

	private readonly string _devicePath;
	private readonly ILogger? _logger;
	private readonly string _tag;
	private readonly SmartInfiniteTransport _transport;

	public SmartInfiniteActuationService(HidDevice device, string modelName, ILogger? logger = null)
	{
		ArgumentNullException.ThrowIfNull(device);
		_devicePath = device.DevicePath;
		_logger = logger;
		_tag = $"ACT {modelName}";
		_transport = new SmartInfiniteTransport(device, _tag, logger);
	}

	/// <summary>Test seam: run the service against a scripted transport instead of real HID.</summary>
	internal SmartInfiniteActuationService(SmartInfiniteTransport transport, string modelName, ILogger? logger = null)
	{
		_transport = transport ?? throw new ArgumentNullException(nameof(transport));
		_devicePath = transport.DevicePath;
		_logger = logger;
		_tag = $"ACT {modelName}";
	}

	public async Task<KeyboardActuationState?> ReadAsync(CancellationToken cancellationToken = default)
	{
		using var gate = await DeviceGate.AcquireAsync(_devicePath, cancellationToken).ConfigureAwait(false);
		try
		{
			return await Task.Run(() =>
				_transport.WithRetry(() => ReadOnce(cancellationToken), nameof(ReadAsync), cancellationToken),
				CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[{Tag}] actuation read failed", _tag);
			return null;
		}
	}

	public async Task<bool> WriteAsync(KeyboardActuationValues values, CancellationToken cancellationToken = default)
	{
		_logger?.LogInformation("[{Tag}] WriteAsync {Values} on {Path}", _tag, values, _devicePath);

		using var gate = await DeviceGate.AcquireAsync(_devicePath, cancellationToken).ConfigureAwait(false);
		try
		{
			// A deterministic refusal (geometry gate, verify mismatch) is final: WithRetry re-runs
			// only transient failures (null), so a firmware that answered and disagreed does not
			// get the whole ~15-packet area re-written three more times under the family gate.
			var outcome = await Task.Run(() =>
				_transport.WithRetry(() => WriteOnce(values, cancellationToken),
					nameof(WriteAsync), cancellationToken), CancellationToken.None).ConfigureAwait(false);
			return outcome == WriteOutcome.Verified;
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[{Tag}] actuation write failed", _tag);
			return false;
		}
	}

	public async Task<bool> WriteKeysAsync(IReadOnlyCollection<int> slots, KeyboardActuationValues values, CancellationToken cancellationToken = default)
	{
		if (slots.Count == 0)
		{
			_logger?.LogWarning("[{Tag}] WriteKeysAsync called with no slots — refusing", _tag);
			return false;
		}

		_logger?.LogInformation("[{Tag}] WriteKeysAsync {Values} for {Count} slots on {Path}",
			_tag, values, slots.Count, _devicePath);

		using var gate = await DeviceGate.AcquireAsync(_devicePath, cancellationToken).ConfigureAwait(false);
		try
		{
			var outcome = await Task.Run(() =>
				_transport.WithRetry(() => WriteKeysOnce(slots, values, cancellationToken),
					nameof(WriteKeysAsync), cancellationToken), CancellationToken.None).ConfigureAwait(false);
			return outcome == WriteOutcome.Verified;
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[{Tag}] per-key actuation write failed", _tag);
			return false;
		}
	}

	// ── one-shot operations, each on a single fresh handle ────────────────────────────────────

	private KeyboardActuationState? ReadOnce(CancellationToken cancellationToken)
	{
		return _transport.OnHandle(io =>
		{
			var (info, area) = ReadValidatedArea(io, cancellationToken);
			if (info == null || area == null) return null;

			var (press, release, rt, quickPress, quickRelease) =
				SmartInfiniteTravelArea.ReduceMajority(area, info.TravelRecordBytes);

			int step = info.TravelStepMicrometres;
			var slotSteps = SmartInfiniteTravelArea.ReadAllSlots(area, info.TravelRecordBytes);
			var perKey = new Dictionary<int, KeyboardActuationValues>(slotSteps.Count);
			foreach (var (slot, (p, r, slotRt, qp, qr)) in slotSteps)
				perKey[slot] = new KeyboardActuationValues(
					PressCentiMm: ClampToCentiMm(p, info.PressMinSteps, info.PressMaxSteps, step),
					ReleaseCentiMm: ClampToCentiMm(r, info.PressMinSteps, info.PressMaxSteps, step),
					RapidTrigger: slotRt,
					QuickPressCentiMm: ClampToCentiMm(qp, info.RtMinSteps, info.RtMaxSteps, step),
					QuickReleaseCentiMm: ClampToCentiMm(qr, info.RtMinSteps, info.RtMaxSteps, step));
			// Clamped into the device's own limits on the way OUT too: a board that never had RT
			// enabled holds zeros in the RT fields, and "0.00 mm" against a non-zero minimum puts
			// the slider thumb outside its own track until the first write snaps it.
			var state = new KeyboardActuationState(
				new KeyboardActuationValues(
					PressCentiMm: ClampToCentiMm(press, info.PressMinSteps, info.PressMaxSteps, step),
					ReleaseCentiMm: ClampToCentiMm(release, info.PressMinSteps, info.PressMaxSteps, step),
					RapidTrigger: rt,
					QuickPressCentiMm: ClampToCentiMm(quickPress, info.RtMinSteps, info.RtMaxSteps, step),
					QuickReleaseCentiMm: ClampToCentiMm(quickRelease, info.RtMinSteps, info.RtMaxSteps, step)),
				new KeyboardActuationLimits(
					PressMinCentiMm: SmartInfiniteTravelArea.StepsToCentiMm(info.PressMinSteps, step),
					PressMaxCentiMm: SmartInfiniteTravelArea.StepsToCentiMm(info.PressMaxSteps, step),
					RtMinCentiMm: SmartInfiniteTravelArea.StepsToCentiMm(info.RtMinSteps, step),
					RtMaxCentiMm: SmartInfiniteTravelArea.StepsToCentiMm(info.RtMaxSteps, step),
					StepMicrometres: step,
					Defaults: new KeyboardActuationValues(
						PressCentiMm: SmartInfiniteTravelArea.StepsToCentiMm(info.DefaultPressSteps, step),
						ReleaseCentiMm: SmartInfiniteTravelArea.StepsToCentiMm(info.DefaultReleaseSteps, step),
						RapidTrigger: info.DefaultRapidTrigger,
						QuickPressCentiMm: SmartInfiniteTravelArea.StepsToCentiMm(info.DefaultQuickPressSteps, step),
						QuickReleaseCentiMm: SmartInfiniteTravelArea.StepsToCentiMm(info.DefaultQuickReleaseSteps, step))))
			{
				PerKeyValues = perKey,
			};

			_logger?.LogInformation("[{Tag}] read actuation: {Values} (step {Step} um)", _tag, state.Values, step);
			return state;
		});
	}

	/// <summary>How one write attempt ended: a non-null outcome is the device's answer and is
	/// final; a null one means a handle or transfer never completed and is worth retrying.</summary>
	private enum WriteOutcome { Verified, Rejected }

	private WriteOutcome? WriteOnce(KeyboardActuationValues values, CancellationToken cancellationToken)
	{
		return _transport.OnHandle<WriteOutcome?>(io =>
		{
			var (info, area) = ReadValidatedArea(io, cancellationToken);
			if (info == null || area == null) return null;

			int step = info.TravelStepMicrometres;

			// Clamp to the device's own limits — the strongest guard against a misread step unit
			// pushing a value 10x out of range.
			int press = Math.Clamp(SmartInfiniteTravelArea.CentiMmToSteps(values.PressCentiMm, step),
				info.PressMinSteps, info.PressMaxSteps);
			int release = Math.Clamp(SmartInfiniteTravelArea.CentiMmToSteps(values.ReleaseCentiMm, step),
				info.PressMinSteps, info.PressMaxSteps);
			int quickPress = Math.Clamp(SmartInfiniteTravelArea.CentiMmToSteps(values.QuickPressCentiMm, step),
				info.RtMinSteps, info.RtMaxSteps);
			int quickRelease = Math.Clamp(SmartInfiniteTravelArea.CentiMmToSteps(values.QuickReleaseCentiMm, step),
				info.RtMinSteps, info.RtMaxSteps);

			SmartInfiniteTravelArea.PatchAllSlots(
				area, info.TravelRecordBytes, press, release, values.RapidTrigger, quickPress, quickRelease);

			_logger?.LogInformation(
				"[{Tag}] writing travel area: press={Press} release={Release} rt={Rt} qp={Qp} qr={Qr} steps, {Bytes} bytes",
				_tag, press, release, values.RapidTrigger, quickPress, quickRelease, area.Length);

			if (!_transport.WriteTable(io, SmartInfiniteProtocol.CmdWriteTravelArea, area, area.Length, cancellationToken))
				return null;
			Thread.Sleep(PostWriteSettleMs);

			var after = ReadArea(io, info.TravelAreaBytes, cancellationToken);
			if (after == null)
			{
				_logger?.LogWarning("[{Tag}] wrote the travel area but could not read it back", _tag);
				return null;
			}

			var actual = SmartInfiniteTravelArea.ReduceMajority(after, info.TravelRecordBytes);
			if (actual.PressSteps != press || actual.ReleaseSteps != release ||
			    actual.RapidTrigger != values.RapidTrigger ||
			    actual.QuickPressSteps != quickPress || actual.QuickReleaseSteps != quickRelease)
			{
				// The device answered with different bytes — a fact about the firmware, not a
				// glitch; retrying would only re-write the same refused area.
				_logger?.LogWarning(
					"[{Tag}] verify mismatch: wanted press={Press} release={Release} rt={Rt} qp={Qp} qr={Qr}, device has {Actual}",
					_tag, press, release, values.RapidTrigger, quickPress, quickRelease, actual);
				return WriteOutcome.Rejected;
			}

			_logger?.LogInformation("[{Tag}] travel area verified", _tag);
			return WriteOutcome.Verified;
		});
	}

	private WriteOutcome? WriteKeysOnce(IReadOnlyCollection<int> slots, KeyboardActuationValues values, CancellationToken cancellationToken)
	{
		return _transport.OnHandle<WriteOutcome?>(io =>
		{
			var (info, area) = ReadValidatedArea(io, cancellationToken);
			if (info == null || area == null) return null;

			var validSlots = new List<int>(slots.Count);
			foreach (int slot in slots)
				if (slot >= 0 && slot < info.TravelSlots)
					validSlots.Add(slot);

			if (validSlots.Count == 0)
			{
				// Every requested slot is outside this board's table — a fact about the request, not
				// a transfer glitch; writing the unmodified area back would only wear the flash.
				_logger?.LogWarning("[{Tag}] no requested slot exists on this board (slots 0..{Max}) — refusing",
					_tag, info.TravelSlots - 1);
				return WriteOutcome.Rejected;
			}

			int step = info.TravelStepMicrometres;
			int press = Math.Clamp(SmartInfiniteTravelArea.CentiMmToSteps(values.PressCentiMm, step),
				info.PressMinSteps, info.PressMaxSteps);
			int release = Math.Clamp(SmartInfiniteTravelArea.CentiMmToSteps(values.ReleaseCentiMm, step),
				info.PressMinSteps, info.PressMaxSteps);
			int quickPress = Math.Clamp(SmartInfiniteTravelArea.CentiMmToSteps(values.QuickPressCentiMm, step),
				info.RtMinSteps, info.RtMaxSteps);
			int quickRelease = Math.Clamp(SmartInfiniteTravelArea.CentiMmToSteps(values.QuickReleaseCentiMm, step),
				info.RtMinSteps, info.RtMaxSteps);

			// The pre-patch area is the verify baseline for every slot this write must NOT touch.
			var original = (byte[])area.Clone();
			SmartInfiniteTravelArea.PatchSlots(
				area, info.TravelRecordBytes, validSlots, press, release, values.RapidTrigger, quickPress, quickRelease);

			_logger?.LogInformation(
				"[{Tag}] writing travel area for {Count} slots: press={Press} release={Release} rt={Rt} qp={Qp} qr={Qr} steps",
				_tag, validSlots.Count, press, release, values.RapidTrigger, quickPress, quickRelease);

			if (!_transport.WriteTable(io, SmartInfiniteProtocol.CmdWriteTravelArea, area, area.Length, cancellationToken))
				return null;
			Thread.Sleep(PostWriteSettleMs);

			var after = ReadArea(io, info.TravelAreaBytes, cancellationToken);
			if (after == null)
			{
				_logger?.LogWarning("[{Tag}] wrote the travel area but could not read it back", _tag);
				return null;
			}

			var patched = new HashSet<int>(validSlots);
			foreach (int slot in validSlots)
			{
				var actual = SmartInfiniteTravelArea.ReadSlot(after, info.TravelRecordBytes, slot);
				if (actual.PressSteps != press || actual.ReleaseSteps != release ||
				    actual.RapidTrigger != values.RapidTrigger ||
				    actual.QuickPressSteps != quickPress || actual.QuickReleaseSteps != quickRelease)
				{
					_logger?.LogWarning(
						"[{Tag}] verify mismatch on slot {Slot}: wanted press={Press} release={Release} rt={Rt} qp={Qp} qr={Qr}, device has {Actual}",
						_tag, slot, press, release, values.RapidTrigger, quickPress, quickRelease, actual);
					return WriteOutcome.Rejected;
				}
			}

			int recordBytes = info.TravelRecordBytes;
			for (int slot = 0; slot < info.TravelSlots; slot++)
			{
				if (patched.Contains(slot)) continue;
				var before = original.AsSpan(slot * recordBytes, recordBytes);
				var now = after.AsSpan(slot * recordBytes, recordBytes);
				if (!before.SequenceEqual(now))
				{
					_logger?.LogWarning("[{Tag}] verify mismatch: unpatched slot {Slot} changed across the write", _tag, slot);
					return WriteOutcome.Rejected;
				}
			}

			_logger?.LogInformation("[{Tag}] per-key travel area verified", _tag);
			return WriteOutcome.Verified;
		});
	}

	private static int ClampToCentiMm(int steps, int minSteps, int maxSteps, int stepMicrometres) =>
		SmartInfiniteTravelArea.StepsToCentiMm(Math.Clamp(steps, minSteps, maxSteps), stepMicrometres);

	/// <summary>
	/// The geometry gate: Device Info must describe a coherent travel area and the whole area must
	/// read back. Returns (null, null) on any failure — the callers then report "unsupported"
	/// rather than proceeding, so a board this gate rejects is never written to.
	/// </summary>
	private (SmartInfiniteDeviceInfo? Info, byte[]? Area) ReadValidatedArea(ISmartInfiniteIo io, CancellationToken cancellationToken)
	{
		var info = _transport.ReadDeviceInfo(io);
		if (info == null) return (null, null);
		_logger?.LogInformation("[{Tag}] device info: {Info}", _tag, info);

		if (info.TravelSlots == 0 || info.TravelStepMicrometres == 0)
		{
			_logger?.LogInformation("[{Tag}] no magnetic travel fields in Device Info — actuation unsupported: {Info}",
				_tag, info);
			return (null, null);
		}

		var area = ReadArea(io, info.TravelAreaBytes, cancellationToken);
		if (area == null)
		{
			_logger?.LogWarning("[{Tag}] travel-area read failed on {Path}", _tag, _devicePath);
			return (null, null);
		}

		if (!SmartInfiniteTravelArea.ValidateGeometry(info, area.Length))
		{
			_logger?.LogError(
				"[{Tag}] travel geometry is incoherent (slots={Slots} record={Record} B step={Step} um " +
				"press {PMin}..{PMax} rt {RMin}..{RMax}) — refusing actuation on this board",
				_tag, info.TravelSlots, info.TravelRecordBytes, info.TravelStepMicrometres,
				info.PressMinSteps, info.PressMaxSteps, info.RtMinSteps, info.RtMaxSteps);
			return (null, null);
		}

		return (info, area);
	}

	// Exact-size buffer: unlike the key matrix there is no larger reserve to write back — the
	// whole area IS the transfer, so ValidateGeometry can check the byte count strictly.
	private byte[]? ReadArea(ISmartInfiniteIo io, int dataSize, CancellationToken cancellationToken) =>
		_transport.ReadTable(io, SmartInfiniteProtocol.CmdReadTravelArea, dataSize, dataSize, cancellationToken);
}
