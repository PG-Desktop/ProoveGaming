using HidSharp;
using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

/// <summary>
/// Global actuation (key press force) over the Rongyuan RY5088 vendor protocol — T800.
/// </summary>
/// <remarks>
/// <para>
/// Unlike the Smart Infinite implementation there is no read-modify-write: a bulk trip set
/// (<c>0x65</c>, <c>Select=1</c>) rewrites one option for every key, so an apply is four two-byte
/// option sweeps (press / release / RT press / RT release) plus a one-byte key-mode sweep for the
/// RT flag, with <c>FlashEnable</c> only on the final packet — one flash commit per apply.
/// </para>
/// <para>
/// Adjustable ranges are documented constants (press 0.1–4.0 mm, RT 0.01–3.0 mm), not
/// device-reported; the value unit comes from the function-list read (<c>0xE6</c>), falling back
/// to the 0.01 mm default when unreadable. Current values are read with <c>0xE5</c> and reduced by
/// majority over the populated slots — the 128-slot table has 60 unused positions whose zeros must
/// not out-vote the real keys. See <c>docs/knowledge-base/ry5088-magnetic-axis-protocol.md</c>.
/// </para>
/// </remarks>
public class T800KeyboardActuationService : IKeyboardActuationService
{
	// Documented adjustable ranges, in hundredths of a millimetre.
	private const int PressMinCentiMm = 10;    // 0.1 mm
	private const int PressMaxCentiMm = 400;   // 4.0 mm
	private const int RtMinCentiMm = 1;        // 0.01 mm
	private const int RtMaxCentiMm = 300;      // 3.0 mm
	private const int DefaultPressCentiMm = 200;
	private const int DefaultQuickCentiMm = 10;

	// Same rationale as T800KeyboardKeyMappingService.
	private const int MaxAttempts = 3;
	private const int RetryDelayMs = 100;
	private const int InterPacketDelayMs = 20;
	private const int PostWriteSettleMs = 300;

	private static readonly byte[] TwoByteOptions =
	[
		Ry5088Protocol.TripOptionPress,
		Ry5088Protocol.TripOptionRelease,
		Ry5088Protocol.TripOptionRtPress,
		Ry5088Protocol.TripOptionRtRelease,
	];

	private readonly HidDevice _device;
	private readonly ILogger? _logger;
	private const string Tag = "ACT T800";

	public T800KeyboardActuationService(HidDevice device, ILogger? logger = null)
	{
		_device = device ?? throw new ArgumentNullException(nameof(device));
		_logger = logger;
	}

	public async Task<KeyboardActuationState?> ReadAsync(CancellationToken cancellationToken = default)
	{
		using var gate = await DeviceGate.AcquireAsync(_device.DevicePath, cancellationToken).ConfigureAwait(false);
		try
		{
			return await Task.Run(() =>
				WithRetry(() => ReadOnce(cancellationToken), nameof(ReadAsync), cancellationToken),
				CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[{Tag}] actuation read failed", Tag);
			return null;
		}
	}

	public async Task<bool> WriteAsync(KeyboardActuationValues values, CancellationToken cancellationToken = default)
	{
		_logger?.LogInformation("[{Tag}] WriteAsync {Values} on {Path}", Tag, values, _device.DevicePath);

		using var gate = await DeviceGate.AcquireAsync(_device.DevicePath, cancellationToken).ConfigureAwait(false);
		try
		{
			// A deterministic refusal (verify mismatch) is final: WithRetry re-runs only transient
			// failures (null), so a firmware that answered and disagreed is not re-flashed 3x.
			var outcome = await Task.Run(() =>
				WithRetry(() => WriteOnce(values, cancellationToken), nameof(WriteAsync), cancellationToken),
				CancellationToken.None).ConfigureAwait(false);
			return outcome == WriteOutcome.Verified;
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[{Tag}] actuation write failed", Tag);
			return false;
		}
	}

	public async Task<bool> WriteKeysAsync(IReadOnlyCollection<int> slots, KeyboardActuationValues values, CancellationToken cancellationToken = default)
	{
		if (slots.Count == 0)
		{
			_logger?.LogWarning("[{Tag}] WriteKeysAsync called with no slots — refusing", Tag);
			return false;
		}

		_logger?.LogInformation("[{Tag}] WriteKeysAsync {Values} for {Count} slots on {Path}",
			Tag, values, slots.Count, _device.DevicePath);

		using var gate = await DeviceGate.AcquireAsync(_device.DevicePath, cancellationToken).ConfigureAwait(false);
		try
		{
			var outcome = await Task.Run(() =>
				WithRetry(() => WriteKeysOnce(slots, values, cancellationToken), nameof(WriteKeysAsync), cancellationToken),
				CancellationToken.None).ConfigureAwait(false);
			return outcome == WriteOutcome.Verified;
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[{Tag}] per-key actuation write failed", Tag);
			return false;
		}
	}

	/// <summary>How one write attempt ended: a non-null outcome is the device's answer and is
	/// final; a null one means a handle or transfer never completed and is worth retrying.</summary>
	private enum WriteOutcome { Verified, Rejected }

	// ── one-shot operations ───────────────────────────────────────────────────────────────────

	private KeyboardActuationState? ReadOnce(CancellationToken cancellationToken)
	{
		if (!_device.TryOpen(out var stream))
		{
			_logger?.LogWarning("[{Tag}] TryOpen failed on {Path}", Tag, _device.DevicePath);
			return null;
		}

		using (stream)
		{
			int precisionUm = ReadPrecision(stream);

			var press = ReadTripOption(stream, Ry5088Protocol.TripOptionPress, cancellationToken);
			var release = ReadTripOption(stream, Ry5088Protocol.TripOptionRelease, cancellationToken);
			var rtPress = ReadTripOption(stream, Ry5088Protocol.TripOptionRtPress, cancellationToken);
			var rtRelease = ReadTripOption(stream, Ry5088Protocol.TripOptionRtRelease, cancellationToken);
			var modes = ReadKeyModes(stream, cancellationToken);
			if (press == null || release == null || rtPress == null || rtRelease == null || modes == null)
				return null;

			// Majority over the populated slots only: 60 of the 128 positions are unused and hold
			// zero, and an actuation of zero is not a value a real key can carry.
			int pressValue = MajorityNonZero(press);
			if (pressValue == 0)
			{
				_logger?.LogWarning("[{Tag}] trip read produced no non-zero press values — treating as unsupported", Tag);
				return null;
			}

			bool rt = RapidTriggerMajority(modes, press);

			// Per-key map over the populated slots only — the 60 unused positions hold zeros and are
			// not keys. Same clamping rule as the global values below.
			var perKey = new Dictionary<int, KeyboardActuationValues>();
			for (int slot = 0; slot < Ry5088Protocol.MatrixSlots; slot++)
			{
				if (press[slot] == 0) continue;
				perKey[slot] = new KeyboardActuationValues(
					PressCentiMm: Math.Clamp(Ry5088Protocol.TripUnitsToCentiMm(press[slot], precisionUm), PressMinCentiMm, PressMaxCentiMm),
					ReleaseCentiMm: Math.Clamp(Ry5088Protocol.TripUnitsToCentiMm(release[slot], precisionUm), PressMinCentiMm, PressMaxCentiMm),
					RapidTrigger: (modes[slot] & Ry5088Protocol.KeyModeRapidTriggerFlag) != 0,
					QuickPressCentiMm: Math.Clamp(Ry5088Protocol.TripUnitsToCentiMm(rtPress[slot], precisionUm), RtMinCentiMm, RtMaxCentiMm),
					QuickReleaseCentiMm: Math.Clamp(Ry5088Protocol.TripUnitsToCentiMm(rtRelease[slot], precisionUm), RtMinCentiMm, RtMaxCentiMm));
			}

			// Values are clamped into the advertised ranges on the way OUT too: a board that never
			// had RT enabled holds zeros in the RT options, and showing "0.00 mm" against a 0.01 mm
			// minimum puts the slider thumb outside its own track until the first write snaps it.
			var state = new KeyboardActuationState(
				new KeyboardActuationValues(
					PressCentiMm: Math.Clamp(Ry5088Protocol.TripUnitsToCentiMm(pressValue, precisionUm), PressMinCentiMm, PressMaxCentiMm),
					ReleaseCentiMm: Math.Clamp(Ry5088Protocol.TripUnitsToCentiMm(MajorityNonZero(release), precisionUm), PressMinCentiMm, PressMaxCentiMm),
					RapidTrigger: rt,
					QuickPressCentiMm: Math.Clamp(Ry5088Protocol.TripUnitsToCentiMm(MajorityNonZero(rtPress), precisionUm), RtMinCentiMm, RtMaxCentiMm),
					QuickReleaseCentiMm: Math.Clamp(Ry5088Protocol.TripUnitsToCentiMm(MajorityNonZero(rtRelease), precisionUm), RtMinCentiMm, RtMaxCentiMm)),
				new KeyboardActuationLimits(
					PressMinCentiMm, PressMaxCentiMm, RtMinCentiMm, RtMaxCentiMm,
					StepMicrometres: precisionUm,
					Defaults: new KeyboardActuationValues(
						DefaultPressCentiMm, DefaultPressCentiMm, RapidTrigger: false,
						DefaultQuickCentiMm, DefaultQuickCentiMm)))
			{
				PerKeyValues = perKey,
			};

			_logger?.LogInformation("[{Tag}] read actuation: {Values} (precision {Precision} um)",
				Tag, state.Values, precisionUm);
			return state;
		}
	}

	private WriteOutcome? WriteOnce(KeyboardActuationValues values, CancellationToken cancellationToken)
	{
		if (!_device.TryOpen(out var stream))
		{
			_logger?.LogWarning("[{Tag}] TryOpen failed on {Path}", Tag, _device.DevicePath);
			return null;
		}

		using (stream)
		{
			int precisionUm = ReadPrecision(stream);

			ushort press = (ushort)Ry5088Protocol.CentiMmToTripUnits(Math.Clamp(values.PressCentiMm, PressMinCentiMm, PressMaxCentiMm), precisionUm);
			ushort release = (ushort)Ry5088Protocol.CentiMmToTripUnits(Math.Clamp(values.ReleaseCentiMm, PressMinCentiMm, PressMaxCentiMm), precisionUm);
			ushort quickPress = (ushort)Ry5088Protocol.CentiMmToTripUnits(Math.Clamp(values.QuickPressCentiMm, RtMinCentiMm, RtMaxCentiMm), precisionUm);
			ushort quickRelease = (ushort)Ry5088Protocol.CentiMmToTripUnits(Math.Clamp(values.QuickReleaseCentiMm, RtMinCentiMm, RtMaxCentiMm), precisionUm);

			// Option 7 is the key's WHOLE mode (Normal / DKS / MT / TGL / Snap) with RT as bit 7 on
			// top — not an RT flag by itself. Writing one byte to all 128 slots would erase every
			// non-Normal mode a user configured in the vendor app, so this is a read-modify-write:
			// read the current modes, patch ONLY bit 7. A failed read aborts rather than writing
			// blind — same rule the Smart Infinite travel path applies to its whole area.
			var modes = ReadKeyModes(stream, cancellationToken);
			if (modes == null)
			{
				_logger?.LogWarning("[{Tag}] could not read current key modes — refusing to overwrite them blind", Tag);
				return null;
			}

			for (int i = 0; i < modes.Length; i++)
				modes[i] = (byte)((modes[i] & ~Ry5088Protocol.KeyModeRapidTriggerFlag)
					| (values.RapidTrigger ? Ry5088Protocol.KeyModeRapidTriggerFlag : 0));

			_logger?.LogInformation(
				"[{Tag}] writing trip: press={Press} release={Release} rt={Rt} qp={Qp} qr={Qr} units (precision {Precision} um)",
				Tag, press, release, values.RapidTrigger, quickPress, quickRelease, precisionUm);

			// Four two-byte sweeps, then the key-mode sweep; FlashEnable rides only on the very
			// last packet so the whole apply is one flash commit.
			ushort[][] sweeps = [Fill(press), Fill(release), Fill(quickPress), Fill(quickRelease)];
			for (int i = 0; i < TwoByteOptions.Length; i++)
			{
				if (cancellationToken.IsCancellationRequested) return null;
				WriteTwoByteSweep(stream, TwoByteOptions[i], sweeps[i]);
			}

			WriteKeyModeSweep(stream, modes);
			Thread.Sleep(PostWriteSettleMs);
		}

		// Feature writes are not acked — reading the press option back is the only way to tell an
		// applied write from a discarded one (same verification rule as the key-matrix service).
		if (!_device.TryOpen(out var verifyStream))
		{
			_logger?.LogWarning("[{Tag}] TryOpen for verify failed on {Path}", Tag, _device.DevicePath);
			return null;
		}

		using (verifyStream)
		{
			int precisionUm = ReadPrecision(verifyStream);
			ushort wanted = (ushort)Ry5088Protocol.CentiMmToTripUnits(Math.Clamp(values.PressCentiMm, PressMinCentiMm, PressMaxCentiMm), precisionUm);

			var press = ReadTripOption(verifyStream, Ry5088Protocol.TripOptionPress, cancellationToken);
			if (press == null)
			{
				_logger?.LogWarning("[{Tag}] wrote trip settings but could not read them back", Tag);
				return null;
			}

			int actual = MajorityNonZero(press);
			if (actual != wanted)
			{
				// The device answered with different values — a fact about the firmware, not a
				// glitch; retrying would only commit the same refused write to flash again.
				_logger?.LogWarning("[{Tag}] verify mismatch: wanted press={Wanted}, device has {Actual}", Tag, wanted, actual);
				return WriteOutcome.Rejected;
			}

			_logger?.LogInformation("[{Tag}] trip settings verified", Tag);
			return WriteOutcome.Verified;
		}
	}

	private WriteOutcome? WriteKeysOnce(IReadOnlyCollection<int> slots, KeyboardActuationValues values, CancellationToken cancellationToken)
	{
		List<int> validSlots = new(slots.Count);
		foreach (int slot in slots)
			if (slot >= 0 && slot < Ry5088Protocol.MatrixSlots)
				validSlots.Add(slot);

		if (validSlots.Count == 0)
		{
			// Every requested slot is outside the matrix — a fact about the request, not a transfer
			// glitch; there is nothing to write.
			_logger?.LogWarning("[{Tag}] no requested slot exists in the matrix — refusing", Tag);
			return WriteOutcome.Rejected;
		}

		if (!_device.TryOpen(out var stream))
		{
			_logger?.LogWarning("[{Tag}] TryOpen failed on {Path}", Tag, _device.DevicePath);
			return null;
		}

		using (stream)
		{
			int precisionUm = ReadPrecision(stream);

			ushort press = (ushort)Ry5088Protocol.CentiMmToTripUnits(Math.Clamp(values.PressCentiMm, PressMinCentiMm, PressMaxCentiMm), precisionUm);
			ushort release = (ushort)Ry5088Protocol.CentiMmToTripUnits(Math.Clamp(values.ReleaseCentiMm, PressMinCentiMm, PressMaxCentiMm), precisionUm);
			ushort quickPress = (ushort)Ry5088Protocol.CentiMmToTripUnits(Math.Clamp(values.QuickPressCentiMm, RtMinCentiMm, RtMaxCentiMm), precisionUm);
			ushort quickRelease = (ushort)Ry5088Protocol.CentiMmToTripUnits(Math.Clamp(values.QuickReleaseCentiMm, RtMinCentiMm, RtMaxCentiMm), precisionUm);

			// Unlike the global path's Fill, a per-key apply is a read-modify-write of every table it
			// touches: the untouched keys' values ride along byte-identical, and a failed read aborts
			// rather than writing blind — the same rule the key-mode sweep already followed.
			var pressTable = ReadTripOption(stream, Ry5088Protocol.TripOptionPress, cancellationToken);
			var releaseTable = ReadTripOption(stream, Ry5088Protocol.TripOptionRelease, cancellationToken);
			var rtPressTable = ReadTripOption(stream, Ry5088Protocol.TripOptionRtPress, cancellationToken);
			var rtReleaseTable = ReadTripOption(stream, Ry5088Protocol.TripOptionRtRelease, cancellationToken);
			var modes = ReadKeyModes(stream, cancellationToken);
			if (pressTable == null || releaseTable == null || rtPressTable == null || rtReleaseTable == null || modes == null)
			{
				_logger?.LogWarning("[{Tag}] could not read current trip tables — refusing to overwrite them blind", Tag);
				return null;
			}

			foreach (int slot in validSlots)
			{
				pressTable[slot] = press;
				releaseTable[slot] = release;
				rtPressTable[slot] = quickPress;
				rtReleaseTable[slot] = quickRelease;
				modes[slot] = (byte)((modes[slot] & ~Ry5088Protocol.KeyModeRapidTriggerFlag)
					| (values.RapidTrigger ? Ry5088Protocol.KeyModeRapidTriggerFlag : 0));
			}

			// Only the pages that contain a patched slot are written — fewer packets, and the flash
			// pages of untouched keys are never rewritten. FlashEnable still rides only on the very
			// last packet, so the whole apply is one flash commit.
			var twoBytePages = Ry5088Protocol.TripPagesContaining(validSlots, Ry5088Protocol.TripKeysPerPageTwoByte);
			var oneBytePages = Ry5088Protocol.TripPagesContaining(validSlots, Ry5088Protocol.TripKeysPerPageOneByte);

			_logger?.LogInformation(
				"[{Tag}] writing trip for {Count} slots: press={Press} release={Release} rt={Rt} qp={Qp} qr={Qr} units (precision {Precision} um, {Pages}+{ModePages} pages)",
				Tag, validSlots.Count, press, release, values.RapidTrigger, quickPress, quickRelease,
				precisionUm, twoBytePages.Length * TwoByteOptions.Length, oneBytePages.Length);

			ushort[][] tables = [pressTable, releaseTable, rtPressTable, rtReleaseTable];
			for (int i = 0; i < TwoByteOptions.Length; i++)
			{
				if (cancellationToken.IsCancellationRequested) return null;
				WriteTwoBytePages(stream, TwoByteOptions[i], tables[i], twoBytePages);
			}

			WriteKeyModePages(stream, modes, oneBytePages);
			Thread.Sleep(PostWriteSettleMs);
		}

		// Same verification rule as the global write: feature writes are not acked, so the press
		// table is read back and the patched slots compared.
		if (!_device.TryOpen(out var verifyStream))
		{
			_logger?.LogWarning("[{Tag}] TryOpen for verify failed on {Path}", Tag, _device.DevicePath);
			return null;
		}

		using (verifyStream)
		{
			int precisionUm = ReadPrecision(verifyStream);
			ushort wanted = (ushort)Ry5088Protocol.CentiMmToTripUnits(Math.Clamp(values.PressCentiMm, PressMinCentiMm, PressMaxCentiMm), precisionUm);

			var pressTable = ReadTripOption(verifyStream, Ry5088Protocol.TripOptionPress, cancellationToken);
			if (pressTable == null)
			{
				_logger?.LogWarning("[{Tag}] wrote trip settings but could not read them back", Tag);
				return null;
			}

			foreach (int slot in validSlots)
			{
				if (pressTable[slot] == wanted) continue;
				// The device answered with different values — a fact about the firmware, not a
				// glitch; retrying would only commit the same refused write to flash again.
				_logger?.LogWarning("[{Tag}] verify mismatch on slot {Slot}: wanted press={Wanted}, device has {Actual}",
					Tag, slot, wanted, pressTable[slot]);
				return WriteOutcome.Rejected;
			}

			_logger?.LogInformation("[{Tag}] per-key trip settings verified", Tag);
			return WriteOutcome.Verified;
		}
	}

	// ── transfers ─────────────────────────────────────────────────────────────────────────────

	/// <summary>Trip precision from the function list; the documented 0.01 mm default when unreadable.</summary>
	private int ReadPrecision(HidStream stream)
	{
		var request = Ry5088Protocol.BuildGetFunctionList();
		stream.SetFeature(request);
		Thread.Sleep(InterPacketDelayMs);

		var reply = new byte[Ry5088Protocol.ReportLength];
		try
		{
			stream.GetFeature(reply);
		}
		catch (Exception ex) when (ex is IOException or TimeoutException)
		{
			_logger?.LogWarning("[{Tag}] function-list read failed ({Msg}) — assuming 0.01 mm precision", Tag, ex.Message);
			return 10;
		}

		int precision = Ry5088Protocol.ParseTripPrecisionMicrometres(reply[1], reply[2]);
		if (precision == 0)
		{
			_logger?.LogInformation("[{Tag}] function list carried no 0xAA marker — assuming 0.01 mm precision", Tag);
			return 10;
		}

		return precision;
	}

	/// <summary>
	/// Reads one two-byte trip option for all 128 slots. Returns <c>null</c> unless every page
	/// arrived — a partial table would skew the majority reduction.
	/// </summary>
	private ushort[]? ReadTripOption(HidStream stream, byte option, CancellationToken cancellationToken)
	{
		var values = new ushort[Ry5088Protocol.MatrixSlots];
		int pages = Ry5088Protocol.TripPageCount(Ry5088Protocol.TripKeysPerPageTwoByte);
		var reply = new byte[Ry5088Protocol.ReportLength];

		for (byte page = 0; page < pages; page++)
		{
			if (cancellationToken.IsCancellationRequested) return null;

			var request = Ry5088Protocol.BuildGetTripPage(option, page);
			stream.SetFeature(request);
			Thread.Sleep(InterPacketDelayMs);

			Array.Clear(reply);
			stream.GetFeature(reply);

			if (IsEcho(request, reply))
			{
				_logger?.LogWarning("[{Tag}] trip option {Option} page {Page} came back as our own request — command rejected",
					Tag, option, page);
				return null;
			}

			int start = page * Ry5088Protocol.TripKeysPerPageTwoByte;
			int count = Math.Min(Ry5088Protocol.TripKeysPerPageTwoByte, Ry5088Protocol.MatrixSlots - start);
			for (int i = 0; i < count; i++)
				values[start + i] = (ushort)(reply[1 + i * 2] | (reply[2 + i * 2] << 8));
		}

		return values;
	}

	private byte[]? ReadKeyModes(HidStream stream, CancellationToken cancellationToken)
	{
		var modes = new byte[Ry5088Protocol.MatrixSlots];
		int pages = Ry5088Protocol.TripPageCount(Ry5088Protocol.TripKeysPerPageOneByte);
		var reply = new byte[Ry5088Protocol.ReportLength];

		for (byte page = 0; page < pages; page++)
		{
			if (cancellationToken.IsCancellationRequested) return null;

			var request = Ry5088Protocol.BuildGetTripPage(Ry5088Protocol.TripOptionKeyMode, page);
			stream.SetFeature(request);
			Thread.Sleep(InterPacketDelayMs);

			Array.Clear(reply);
			stream.GetFeature(reply);

			if (IsEcho(request, reply))
			{
				_logger?.LogWarning("[{Tag}] key-mode page {Page} came back as our own request — command rejected", Tag, page);
				return null;
			}

			int start = page * Ry5088Protocol.TripKeysPerPageOneByte;
			int count = Math.Min(Ry5088Protocol.TripKeysPerPageOneByte, Ry5088Protocol.MatrixSlots - start);
			Buffer.BlockCopy(reply, 1, modes, start, count);
		}

		return modes;
	}

	private void WriteTwoByteSweep(HidStream stream, byte option, ushort[] values)
	{
		int pages = Ry5088Protocol.TripPageCount(Ry5088Protocol.TripKeysPerPageTwoByte);
		for (byte page = 0; page < pages; page++)
		{
			int start = page * Ry5088Protocol.TripKeysPerPageTwoByte;
			int count = Math.Min(Ry5088Protocol.TripKeysPerPageTwoByte, values.Length - start);
			stream.SetFeature(Ry5088Protocol.BuildSetTripPageTwoByte(
				option, page, values.AsSpan(start, count), flashEnable: false));
			Thread.Sleep(InterPacketDelayMs);
		}
	}

	private void WriteTwoBytePages(HidStream stream, byte option, ushort[] values, int[] pages)
	{
		foreach (int page in pages)
		{
			int start = page * Ry5088Protocol.TripKeysPerPageTwoByte;
			int count = Math.Min(Ry5088Protocol.TripKeysPerPageTwoByte, values.Length - start);
			stream.SetFeature(Ry5088Protocol.BuildSetTripPageTwoByte(
				option, (byte)page, values.AsSpan(start, count), flashEnable: false));
			Thread.Sleep(InterPacketDelayMs);
		}
	}

	private void WriteKeyModePages(HidStream stream, byte[] modes, int[] pages)
	{
		for (int i = 0; i < pages.Length; i++)
		{
			int page = pages[i];
			int start = page * Ry5088Protocol.TripKeysPerPageOneByte;
			int count = Math.Min(Ry5088Protocol.TripKeysPerPageOneByte, modes.Length - start);
			bool last = i == pages.Length - 1;
			stream.SetFeature(Ry5088Protocol.BuildSetTripPageOneByte(
				Ry5088Protocol.TripOptionKeyMode, (byte)page, modes.AsSpan(start, count), flashEnable: last));
			Thread.Sleep(InterPacketDelayMs);
		}
	}

	private void WriteKeyModeSweep(HidStream stream, byte[] modes)
	{
		int pages = Ry5088Protocol.TripPageCount(Ry5088Protocol.TripKeysPerPageOneByte);
		for (byte page = 0; page < pages; page++)
		{
			int start = page * Ry5088Protocol.TripKeysPerPageOneByte;
			int count = Math.Min(Ry5088Protocol.TripKeysPerPageOneByte, modes.Length - start);
			bool last = page == pages - 1;
			stream.SetFeature(Ry5088Protocol.BuildSetTripPageOneByte(
				Ry5088Protocol.TripOptionKeyMode, page, modes.AsSpan(start, count), flashEnable: last));
			Thread.Sleep(InterPacketDelayMs);
		}
	}

	// ── helpers ───────────────────────────────────────────────────────────────────────────────

	private static ushort[] Fill(ushort value)
	{
		var values = new ushort[Ry5088Protocol.MatrixSlots];
		Array.Fill(values, value);
		return values;
	}

	private static int MajorityNonZero(ushort[] values)
	{
		var votes = new Dictionary<int, int>();
		foreach (int value in values)
		{
			if (value == 0) continue;
			votes.TryGetValue(value, out int count);
			votes[value] = count + 1;
		}

		int best = 0, bestCount = 0;
		foreach (var (value, count) in votes)
			if (count > bestCount) { best = value; bestCount = count; }
		return best;
	}

	/// <summary>RT is on when most populated keys (those with a non-zero press travel) carry the flag.</summary>
	private static bool RapidTriggerMajority(byte[] modes, ushort[] press)
	{
		int populated = 0, flagged = 0;
		for (int slot = 0; slot < modes.Length && slot < press.Length; slot++)
		{
			if (press[slot] == 0) continue;
			populated++;
			if ((modes[slot] & Ry5088Protocol.KeyModeRapidTriggerFlag) != 0) flagged++;
		}
		return populated > 0 && flagged * 2 > populated;
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
			if (cancellationToken.IsCancellationRequested) return default;

			if (attempt > 1)
			{
				_logger?.LogInformation("[{Tag}] {Op} retry {Attempt}/{Max}", Tag, tag, attempt, MaxAttempts);
				Thread.Sleep(RetryDelayMs);
			}

			try
			{
				var result = operation();
				if (result is not null && result is not false) return result;
			}
			catch (IOException ex)
			{
				_logger?.LogWarning("[{Tag}] {Op} I/O error on {Path}: {Msg}", Tag, tag, _device.DevicePath, ex.Message);
			}
			catch (TimeoutException)
			{
				_logger?.LogWarning("[{Tag}] {Op} timed out on {Path}", Tag, tag, _device.DevicePath);
			}
		}

		return default;
	}
}
