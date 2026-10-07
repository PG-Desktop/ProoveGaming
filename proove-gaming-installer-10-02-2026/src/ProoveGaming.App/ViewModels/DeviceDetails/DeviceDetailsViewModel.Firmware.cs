using CommunityToolkit.Mvvm.Input;

namespace ProoveGaming.App.ViewModels;

public partial class DeviceDetailsViewModel
{
	// The whole Firmware section is a non-functional placeholder — CheckFirmwareUpdates does
	// nothing and no device reports a version yet. The version string is therefore a constant, but
	// it lives here rather than in the view so the surrounding label can be a localized FORMAT
	// string: baking "Current Version: v1.2.3" into the XAML made the label untranslatable, and
	// baking it into the resource would have forced the placeholder version into every language
	// file, to be corrected in each one the day firmware reading lands.
	private const string PlaceholderFirmwareVersion = "v1.2.3";

	// HIDDEN, not removed (2026-09-24): firmware update does not work yet. This one flag hides both
	// nav entries, makes the section unreachable (IsSectionAvailable) and drops it from the nav
	// counts that size the backdrop; set it to true to bring all of that back together.
	private const bool FirmwareUpdateEnabled = false;

	public bool IsFirmwareNavVisible => FirmwareUpdateEnabled;

	public string FirmwareVersionText =>
		string.Format(LocalizationManager.GetString("DeviceDetails_Firmware_CurrentVersionFormat"),
			PlaceholderFirmwareVersion);

	[RelayCommand]
	private void CheckFirmwareUpdates() { }
}
