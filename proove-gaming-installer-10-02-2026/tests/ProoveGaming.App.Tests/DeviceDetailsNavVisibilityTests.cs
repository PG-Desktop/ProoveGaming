using System.Collections.Generic;
using System.Linq;
using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// Nav-section visibility is now driven by CAPABILITIES rather than by a DeviceTypeLabel string
// check. That refactor changes EXISTING devices, so these tests pin the exact visible set per
// device family: getting a capability wrong silently hides a section a mouse or keyboard needs.
//
// Before the change every non-keyboard was treated as a mouse (`!IsKeyboardDevice`), which is
// precisely why a headset had to be the case that forced this — Cosmo would otherwise have shown
// Buttons, DPI and Settings, none of which it has.
//
// Reading Device directly (rather than SetDevice) is deliberate and safe HERE: every nav flag is a
// pure function of the card's capability fields, and the Device setter is what raises them. These
// tests must not depend on SetDevice's storage/dispatcher work.
public class DeviceDetailsNavVisibilityTests
{
	// The sections the view can show. Presets is unconditional; the rest are bound to a flag
	// (Firmware to IsFirmwareNavVisible, hidden for now), so the "visible set" is what these tests
	// compare against.
	private static IReadOnlyList<string> VisibleSections(DeviceDetailsViewModel vm)
	{
		var visible = new List<string> { "Presets" };
		if (vm.IsRgbSupported) visible.Add("RgbLighting");
		if (vm.IsButtonsNavVisible) visible.Add("Buttons");
		if (vm.IsMacrosSupported) visible.Add("Macros");
		if (vm.IsDpiNavVisible) visible.Add("Dpi");
		if (vm.IsSettingsNavVisible) visible.Add("Settings");
		if (vm.IsKeyAssignmentNavVisible) visible.Add("KeyAssignments");
		if (vm.IsAudioNavVisible) visible.Add("Audio");
		if (vm.IsFirmwareNavVisible) visible.Add("Firmware");
		return visible;
	}

	private static DeviceDetailsViewModel VmFor(DeviceCardViewModel card)
		=> new() { Device = card };

	// BLAZE-shaped: remap, macros, RGB, both sliders, and a mouse label for the DPI capability.
	private static SingleCardViewModel Mouse() => new()
	{
		Name = "BLAZE",
		DeviceTypeLabel = "Wired Mouse",
		SupportsRgb = true,
		SupportsButtonRemap = true,
		SupportsMacros = true,
		SupportedMouseSettings = ["ButtonDebounce", "LiftOffDistance"],
	};

	// Cyrex-shaped keyboard: RGB + macros, no remap capability, no mouse settings.
	private static DoubleCardViewModel Keyboard() => new()
	{
		Name = "Cyrex",
		DeviceTypeLabel = "Wired Keyboard",
		SupportsRgb = true,
		SupportsMacros = true,
	};

	private static SingleCardViewModel Headset() => new()
	{
		Name = "Cosmo",
		DeviceTypeLabel = "Wireless Headset",
		SupportsRgb = false,
		SupportedAudioSettings = ["OutputVolume", "MicrophoneVolume"],
	};

	// ── Per-family visible sets ───────────────────────────────────────────────

	[Fact]
	public void Mouse_ShowsTheSameSectionsAsBeforeTheRefactor()
	{
		var vm = VmFor(Mouse());

		Assert.Equal(
			["Presets", "RgbLighting", "Buttons", "Macros", "Dpi", "Settings"],
			VisibleSections(vm));
	}

	[Fact]
	public void Keyboard_ShowsTheSameSectionsAsBeforeTheRefactor()
	{
		var vm = VmFor(Keyboard());

		Assert.Equal(
			["Presets", "RgbLighting", "Macros", "KeyAssignments"],
			VisibleSections(vm));
	}

	// The headset is the case the refactor exists for: no Buttons, no DPI, no Settings.
	[Fact]
	public void Headset_ShowsOnlyPresetsAndAudio()
	{
		var vm = VmFor(Headset());

		Assert.Equal(["Presets", "Audio"], VisibleSections(vm));
	}

	// Would have failed before the refactor: `!IsKeyboardDevice` made all three visible for
	// anything that was not a keyboard, and a headset is not a keyboard.
	[Fact]
	public void Headset_DoesNotInheritTheMouseSections()
	{
		var vm = VmFor(Headset());

		Assert.False(vm.IsButtonsNavVisible);
		Assert.False(vm.IsDpiNavVisible);
		Assert.False(vm.IsSettingsNavVisible);
		Assert.False(vm.IsKeyAssignmentNavVisible);
	}

	// RGB Lighting appears for a headset only once the device actually declares lighting support —
	// it stays hidden for Cosmo until the vendor documents report 0xBC.
	[Fact]
	public void Headset_ShowsRgbOnlyWhenTheDeviceDeclaresIt()
	{
		var withoutRgb = VmFor(Headset());
		Assert.DoesNotContain("RgbLighting", VisibleSections(withoutRgb));

		var lit = Headset();
		lit.SupportsRgb = true;
		Assert.Contains("RgbLighting", VisibleSections(VmFor(lit)));
	}

	// ── Nav backdrop height ───────────────────────────────────────────────────

	// The backdrop is sized from the button count, so Audio has to be counted or the frame is a row
	// short on a headset (the mirror of the empty strip TAIPAN showed when RGB was uncounted).
	[Fact]
	public void Firmware_IsHiddenForEveryDevice_AndCannotBeOpened()
	{
		// Firmware update does not work yet: hidden, not removed (see IsFirmwareNavVisible).
		foreach (var vm in new[] { VmFor(Mouse()), VmFor(Keyboard()), VmFor(Headset()) })
		{
			Assert.False(vm.IsFirmwareNavVisible);

			vm.SelectSectionCommand.Execute(NavSection.Firmware);

			Assert.False(vm.IsFirmwareSection);
		}
	}

	[Fact]
	public void KeyboardNavButtonsCount_LeavesNoRowForTheHiddenFirmwareEntry()
	{
		var vm = VmFor(Keyboard());

		// Presets and Key Assignments, plus the keyboard's own capability entries.
		var expected = 2 + (vm.IsRgbSupported ? 1 : 0) + (vm.IsMacrosSupported ? 1 : 0)
			+ (vm.IsActuationNavVisible ? 1 : 0) + (vm.IsExtendedKeysNavVisible ? 1 : 0);
		Assert.Equal(expected, vm.KeyboardNavButtonsCount);
	}

	[Fact]
	public void VisibleNavButtonsCount_MatchesTheSectionsActuallyShown()
	{
		Assert.Equal(VisibleSections(VmFor(Mouse())).Count, VmFor(Mouse()).VisibleNavButtonsCount);
		Assert.Equal(VisibleSections(VmFor(Keyboard())).Count, VmFor(Keyboard()).VisibleNavButtonsCount);
		Assert.Equal(VisibleSections(VmFor(Headset())).Count, VmFor(Headset()).VisibleNavButtonsCount);
	}

	// ── Layout selection ──────────────────────────────────────────────────────

	// The details layout is picked by shape, not by device type: keyboards get the wide board frame,
    // everything else the single-image frame. Gating it on IsMouseDevice left a headset with no nav
	// frame and no content panel at all, because a headset is neither a mouse nor a keyboard.
	[Fact]
	public void StackNavLayout_IsUsedByEverythingExceptKeyboards()
	{
		Assert.True(VmFor(Mouse()).IsStackNavLayout);
		Assert.True(VmFor(Headset()).IsStackNavLayout);
		Assert.False(VmFor(Keyboard()).IsStackNavLayout);
	}

	// ── Section flag notification ─────────────────────────────────────────────

	// Each section view binds its own Is*Section flag for visibility. If ActiveSection's setter
	// forgets to raise one, that section can NEVER appear: the binding is evaluated once at load,
	// while the page is on Presets, and is never re-read. That is precisely what left the Audio
	// section blank, so this walks every section rather than testing Audio alone.
	[Theory]
	[InlineData(NavSection.Presets, nameof(DeviceDetailsViewModel.IsPresetsSection))]
	[InlineData(NavSection.RgbLighting, nameof(DeviceDetailsViewModel.IsRgbLightingSection))]
	[InlineData(NavSection.Buttons, nameof(DeviceDetailsViewModel.IsButtonsSection))]
	[InlineData(NavSection.Macros, nameof(DeviceDetailsViewModel.IsMacrosSection))]
	[InlineData(NavSection.Dpi, nameof(DeviceDetailsViewModel.IsDpiSection))]
	[InlineData(NavSection.Settings, nameof(DeviceDetailsViewModel.IsSettingsSection))]
	[InlineData(NavSection.Audio, nameof(DeviceDetailsViewModel.IsAudioSection))]
	[InlineData(NavSection.Firmware, nameof(DeviceDetailsViewModel.IsFirmwareSection))]
	public void ChangingTheActiveSection_RaisesThatSectionsVisibilityFlag(NavSection section, string flag)
	{
		var vm = VmFor(Mouse());
		// Start somewhere else so the assignment below is a real change.
		vm.ActiveSection = section == NavSection.Firmware ? NavSection.Presets : NavSection.Firmware;

		var raised = new List<string?>();
		vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
		vm.ActiveSection = section;

		Assert.Contains(flag, raised);
	}
}
