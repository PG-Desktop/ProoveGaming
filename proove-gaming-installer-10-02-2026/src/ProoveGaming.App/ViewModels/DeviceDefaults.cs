namespace ProoveGaming.App.ViewModels;

// Factory-sane starting values for one device, carried from the catalog config to the details view.
// Applied when the device is first added, and what the DPI "Recommendations" and Settings
// "Restore to default" buttons restore — so those buttons and the published table in
// docs/device-defaults.md can never drift apart.
//
// Every value is optional. Null means this device has no default for that aspect (a keyboard has no
// Dpi/ReportRateHz/ButtonDebounceMs/LiftOffDistanceMm, a device without lighting has no Rgb*), and
// the details VM keeps its own built-in constant instead.
//
// Dpi is a DPI VALUE, not a stage index: it is resolved against the device's own DpiStages ladder,
// so a device whose ladder differs from the usual 800/1200/... set still lands on a sensible stage.
public sealed record DeviceDefaults(
	int? Dpi = null,
	int? ReportRateHz = null,
	int? ButtonDebounceMs = null,
	int? LiftOffDistanceMm = null,
	bool? RgbEnabled = null,
	string? RgbEffect = null,
	string? RgbColorHex = null,
	int? RgbBrightness = null,
	int? RgbSpeed = null);
