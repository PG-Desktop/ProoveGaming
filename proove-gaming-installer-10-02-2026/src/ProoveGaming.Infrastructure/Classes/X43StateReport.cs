namespace ProoveGaming.Infrastructure.Classes;

// Decodes the BLAZE / X43 vendor "state" report (HID report id 0x05) the firmware pushes on
// its DPI collection when the physical DPI / polling-rate button is pressed, or in reply to
// the 0x80 query at startup:
//
//   [0x05, profile, reportRate, dpi_stage, ledEffect, 0, 0]
//     byte 2 = report-rate raw period (see X43Settings.HzFromReportRateByte)
//     byte 3 = active DPI stage (0-based; valid 0..7)
//     byte 4 = current LED effect
//
// Pure/testable — the byte offsets and the stage validity range are the critical DPI read
// path (used by DeviceHost's DpiStageMonitor). See docs/mouse/blaze/dpi-stage.md.
public static class X43StateReport
{
	public const byte ReportId = 0x05;

	// Highest DPI stage the firmware defines; a byte above this is treated as unknown so a
	// garbage report can't drive the UI to a non-existent stage.
	public const int MaxStage = 7;

	// Parses a state report. Returns false (and leaves outputs at their "unknown" sentinels)
	// when the buffer isn't a 0x05 report or is too short to carry the stage.
	//   stage         = -1 when absent/out of range, else 0..MaxStage
	//   reportRateHz  =  0 when unknown, else the decoded Hz
	//   ledEffect     = -1 when absent (report shorter than 5 bytes), else the raw byte
	public static bool TryParse(ReadOnlySpan<byte> report, out int stage, out int reportRateHz, out int ledEffect)
	{
		stage = -1;
		reportRateHz = 0;
		ledEffect = -1;

		if (report.Length < 4 || report[0] != ReportId)
			return false;

		var rawStage = report[3];
		stage = rawStage <= MaxStage ? rawStage : -1;
		reportRateHz = X43Settings.HzFromReportRateByte(report[2]);
		if (report.Length >= 5)
			ledEffect = report[4];

		return true;
	}
}
