namespace ProoveGaming.Infrastructure.Classes;

// Pure parser for the TAIPAN (Telink) device-pushed vendor reports on the 33-byte collection.
// The mouse volunteers these on 2.4G link establishment (dongle plug-in, USB unplug, mode-switch
// flip, wake) — the only way battery reaches the host over the dongle, which does NOT relay
// host→mouse queries (confirmed on hardware 2026-07-19). See TaipanStateReportTests.
//
//   0x48 — battery + identity: Para1/2 = battery raw LE, Para3 = USB flag, Para4 = app flag.
//          Raw is NOT millivolts: real captures read 1001-1008 while the doc's "4200 mV" example
//          values are explicitly hypothetical. Interpreted as quarter-millivolts (raw × 4 =
//          4004-4032 mV, a realistic charged Li-ion) until a full-charge reference calibrates it.
//   0x49 — state push: Para1 = profile, Para2 = DPI stage, Para3 = report-rate raw, Para4 = LED.
public static class TaipanStateReport
{
	public const int Length = 33;
	public const byte BatteryReply = 0x48;

	// The battery REQUEST is the same byte as the reply — this protocol reuses one opcode for both
	// directions. Verified on hardware 2026-08-07: `cmd 0x48` with zero parameters gets exactly one
	// reply per request over the 2.4G dongle, and does not repeat without a new request.
	//
	// Named separately from BatteryReply on purpose. An earlier investigation sent the request as
	// 0x08, got silence, and concluded the dongle ignored host->mouse traffic entirely — a wrong
	// conclusion about a whole transport, from one wrong byte. Spelling the request out makes the
	// coincidence explicit rather than something to rediscover.
	public const byte BatteryRequest = 0x48;
	public const byte StateReply = 0x49;

	// Parses a pushed 0x48 battery report. Validates frame shape AND checksum — the dongle
	// emits these unsolicited, so a strict parse keeps garbage out of the battery cache.
	public static bool TryParseBattery(ReadOnlySpan<byte> report, out int rawValue, out bool usbConnected)
	{
		rawValue = 0;
		usbConnected = false;

		if (!IsValidFrame(report, BatteryReply, eitherPara4: true)) return false;

		rawValue = report[4] | (report[5] << 8);
		usbConnected = report[6] != 0;
		return rawValue > 0;
	}

	/// <summary>
	/// True when this is a well-formed battery reply carrying NO reading — the dongle's way of
	/// saying no mouse is behind it.
	/// </summary>
	/// <remarks>
	/// Measured on hardware 2026-08-14, 8 requests in each state on one unit:
	///
	///     mouse ON   ->  05-73-FA-48-D0-03-...   raw 976, every time
	///     mouse OFF  ->  05-24-FA-48-00-00-...   raw 0,   every time
	///
	/// The dongle ALWAYS answers — it is a USB device in its own right and stays enumerated whether
	/// or not a mouse is talking through it — so silence is not the signal; a zero reading is. That
	/// makes presence a positive, immediate per-request fact rather than something inferred from a
	/// timeout, which is why this is preferred over ageing the cache.
	///
	/// The frame is otherwise valid, checksum included, so it must be distinguished from garbage:
	/// <see cref="TryParseBattery"/> rejects both alike and cannot tell the caller which it saw.
	/// </remarks>
	public static bool IsMouseAbsentReply(ReadOnlySpan<byte> report)
	{
		if (!IsValidFrame(report, BatteryReply, eitherPara4: true)) return false;

		return (report[4] | (report[5] << 8)) == 0;
	}

	// Parses a pushed 0x49 state report (profile / DPI stage / report rate / LED stage).
	public static bool TryParseState(ReadOnlySpan<byte> report, out int profile, out int dpiStage, out byte reportRateRaw)
	{
		profile = 0;
		dpiStage = 0;
		reportRateRaw = 0;

		if (!IsValidFrame(report, StateReply)) return false;

		profile = report[4];
		dpiStage = report[5];
		reportRateRaw = report[6];
		return true;
	}

	// Battery percentage. The SHAPE is the vendor's conversion (fabric-info Q&A, T6):
	//
	//     Baifen = (100 * (raw - RawEmpty)) / (RawFull - RawEmpty)
	//
	// with their note "0x03D5 = 981, 981 这个会变，其他是程序固定的" — the raw value is what the
	// mouse pushes and varies, the two endpoints are fixed in their software — and
	// "计算出来的结果大于100时让他不超过100就行" (clamp anything over 100). Integer division
	// deliberately, matching their expression rather than rounding.
	//
	// Their endpoints were 750 and 985. RawFull is now 987 instead, measured against the device's
	// own percentage — see the constant below for the samples. RawEmpty is still theirs.
	//
	// It applies to the RAW pushed value directly. An earlier implementation instead treated the raw
	// value as quarter-millivolts and mapped 3300-4200 mV, which under-reported badly: a full
	// battery could never read above ~78 %.
	//
	// Real hardware pushes above RawFull — 1008 seen on a charged unit, and higher again while
	// charging — so the top of the range clamps to 100 %, per the vendor's clamp instruction. Over
	// the CABLE the reading is charge voltage rather than charge level and is not usable at all; the
	// TAIPAN wired transport therefore declares SupportsBattery: false in the device catalog.
	private const int RawEmpty = 750;

	// 987, not the vendor's 985. Calibrated against the mouse's OWN percentage, which it publishes
	// over Bluetooth as the standard GATT Battery Service (0x180F / 0x2A19) — the same value Windows
	// shows in its tray. Two paired samples ten minutes apart, on one unit, discharging (2026-08-14):
	//
	//     raw 987  ->  GATT 100 %
	//     raw 985  ->  GATT  99 %
	//
	// So raw 985 is demonstrably NOT full, and the vendor's constant reported 100 % for a battery the
	// device itself called 99 %. 987 is the lowest raw observed at 100 %, and it reproduces both
	// samples exactly ((985-750)*100/237 = 99.15 -> 99; (987-750)*100/237 = 100).
	//
	// CAVEAT: this calibrates the TOP of the range only — both samples are at 99-100 %. RawEmpty
	// (750) is still the vendor's and remains unvalidated, and a lithium discharge curve is not
	// linear, so mid-scale readings may drift from what the device reports. Correcting that needs
	// paired samples taken as the battery actually drains: over Bluetooth the DeviceHost logs the
	// GATT percentage and over the dongle it logs the raw value, so a session on each transport at
	// the same charge level yields a usable pair.
	private const int RawFull = 987;

	// Rounded, not truncated. The vendor's expression uses integer division, which discards the
	// fraction and so reads systematically LOW by up to a point: raw 972 works out at 93.67 and was
	// reported as 93 where the mouse's own percentage said 94. Measured against GATT on four paired
	// samples (2026-08-16) rounding matches exactly on two and truncation on none, and it costs
	// nothing — every calibration and edge case pinned in the tests is unchanged by it.
	//
	// It narrows the gap but does not close it, and the remainder is not a bug to chase here: the
	// two numbers are produced by DIFFERENT computations. Over Bluetooth the App shows the mouse's
	// own percentage (GATT), while over the dongle it converts a raw voltage with the linear curve
	// below. In the same window the raw fell 973 -> 969 while GATT fell 96 % -> 94 %, so the
	// device's curve is far steeper here than a straight line — exactly the non-linearity RawFull's
	// remarks warn about. Solving for the RawFull each pair would need gives 982 / 986 / 986 / 983;
	// they disagree, which is what fitting a line to a curve looks like. Closing it properly needs
	// paired samples across the whole discharge range, not more constants tuned at the top of it.
	public static int PercentFromRaw(int rawValue) =>
		Math.Clamp(
			(int)Math.Round((rawValue - RawEmpty) * 100.0 / (RawFull - RawEmpty), MidpointRounding.AwayFromZero),
			0, 100);

	// `eitherPara4` accepts the checksum computed EITHER with Para4 as sent OR with Para4 as zero.
	//
	// The 0x48 battery reply needs it, because the vendor's own two artefacts disagree and both are
	// real:
	//
	//   * Their DOCUMENTED worked example, `05 DA FA 48 68 10 01 01` (voltage 0x1068, USB 1, App 1),
	//     carries a checksum that INCLUDES Para4 — 0xDA. TryParseBattery_ReadsUsbConnectedFlag pins it.
	//   * Their 2026-09-02 CAPTURE and our own unit EXCLUDE it. The capture holds two consecutive
	//     replies, `05 49 fa 48 ea 03 00 00` and `05 49 fa 48 ea 03 00 01`, carrying the SAME
	//     checksum byte despite differing in Para4; our unit answered `05 EB FA 48 48 03 00 01`,
	//     where 0xEB is the Para4 = 0 value (0xE8 would be the Para4 = 1 one). So the shipped
	//     firmware sets the app flag AFTER checksumming.
	//
	// Accepting both is deliberate rather than indecisive: picking either alone discards genuine
	// readings from a device that demonstrably sends the other. The cost is that a corrupt frame has
	// two acceptable checksums instead of one — negligible next to a battery indicator that silently
	// goes blank.
	//
	// Rejecting the app-flagged form was a latent bug from the day this parser was written. Nothing
	// had made the device set that flag until the configuration-mode session landed (2026-09-02), so
	// that change exposed this bug rather than causing it.
	//
	// Config-write ACKs are NOT part of this relaxation and keep the strict rule: the commit reply
	// `05 0D FA 03 7F 00 01 EE` validates only with Para4 = 0xEE.
	private static bool IsValidFrame(ReadOnlySpan<byte> report, byte expectedCmd, bool eitherPara4 = false)
	{
		if (report.Length < 8) return false;
		if (report[0] != TaipanFrame.ReportId || report[2] != TaipanFrame.DriverId || report[3] != expectedCmd)
			return false;

		if (report[1] == TaipanFrame.Checksum(report[3], report[4], report[5], report[6], report[7]))
			return true;

		return eitherPara4 &&
			report[1] == TaipanFrame.Checksum(report[3], report[4], report[5], report[6], 0x00);
	}
}
