namespace ProoveGaming.App.ViewModels;

// Per-device button-remap hotspot geometry carried from the catalog config to the details view.
// Only what varies per device lives here (id + coordinates + label side, in the 1236x756 hotspot
// canvas space) plus a Disabled flag; the label text is derived from the id in
// DeviceDetailsViewModel.
public sealed record DeviceHotspotInfo(
	string Id,
	double ButtonX,
	double ButtonY,
	double TargetX,
	double TargetY,
	string LabelSide,
	bool Disabled);
