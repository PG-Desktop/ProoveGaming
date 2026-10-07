using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// Covers per-device-type defaults (docs/device-defaults.md): the values a device declares in the
// catalog must be what it starts on, what the DPI "Recommendations" button restores, and what the
// Settings "Restore to default" button restores — with the app's built-in values used only when the
// device declares nothing.
public class DeviceDefaultsTests
{
	private const int Vid = 0x04D9;
	private const int Pid = 0xA23F;

	private sealed class FakeOwnerProvider(string kind, string id) : ICurrentOwnerProvider
	{
		private readonly SettingsOwner _owner = new(kind, id);
		public SettingsOwner? GetCurrent() => _owner;
	}

	private static (DeviceDetailsViewModel Vm, LocalStorageService Store, string Owner) NewVm()
	{
		var owner = System.Guid.NewGuid().ToString("D");
		var store = new LocalStorageService("Data Source=:memory:");
		var ownerProvider = new FakeOwnerProvider(LocalStorageService.OwnerKindGuest, owner);
		var vm = new DeviceDetailsViewModel(
			usbHidDeviceService: null,
			localStorage: store,
			settings: new SettingsRepository(store, ownerProvider),
			ownerProvider: ownerProvider);
		return (vm, store, owner);
	}

	// A mouse that declares the given defaults. SupportedMouseSettings drives which sliders exist;
	// both are listed so the debounce/lift-off assertions have something to act on.
	private static SingleCardViewModel FakeMouse(DeviceDefaults defaults) => new()
	{
		Name = "Test Mouse",
		DeviceTypeLabel = "Wired Mouse",
		VendorId = Vid,
		ProductId = Pid,
		SettingsVendorId = Vid,
		SettingsProductId = Pid,
		DeviceKey = "",
		SettingsDeviceKey = "",
		SupportedMouseSettings = ["ButtonDebounce", "LiftOffDistance"],
		Defaults = defaults,
	};

	[Fact]
	public async Task ResolveDefaultDpiStageIndex_PicksTheStageNearestTheDeclaredDpi()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			await vm.SetDeviceAsync(FakeMouse(new DeviceDefaults(Dpi: 1200)));

			var stage = vm.ResolveDefaultDpiStageIndex();

			Assert.NotNull(stage);
			var chosen = vm.DpiPresets.Single(p => p.StageIndex == stage!.Value).DpiValue;
			var nearest = vm.DpiPresets.Min(p => System.Math.Abs(p.DpiValue - 1200));
			Assert.Equal(nearest, System.Math.Abs(chosen - 1200));
		}
	}

	[Fact]
	public async Task ResolveDefaultDpiStageIndex_ReturnsNull_WhenDeviceDeclaresNoDpi()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			await vm.SetDeviceAsync(FakeMouse(new DeviceDefaults()));

			Assert.Null(vm.ResolveDefaultDpiStageIndex());
		}
	}

	[Fact]
	public async Task DefaultReportRateHz_PrefersTheDeclaredValue_AndFallsBackWhenUnset()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			await vm.SetDeviceAsync(FakeMouse(new DeviceDefaults(ReportRateHz: 500)));
			Assert.Equal(500, vm.DefaultReportRateHz);

			await vm.SetDeviceAsync(FakeMouse(new DeviceDefaults()));
			Assert.Equal(DeviceDetailsViewModel.FallbackPollingRateHz, vm.DefaultReportRateHz);
		}
	}

	// S2. PollingRateOptions is a fixed list (125/250/500/1000) and HighlightPollingRate selects by
	// exact match, so a configured rate that is not one of them would deselect every row: the
	// section renders blank while _reportRateKnown claims a value is known, and the restore buttons
	// would persist it, making the blank permanent. Resolve to the nearest supported rate instead,
	// mirroring what ResolveDefaultDpiStageIndex does for an off-ladder DPI value.
	[Fact]
	public async Task DefaultReportRateHz_ResolvesAnUnsupportedConfiguredRateToTheNearestOption()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			await vm.SetDeviceAsync(FakeMouse(new DeviceDefaults(ReportRateHz: 900)));

			Assert.Equal(1000, vm.DefaultReportRateHz);
		}
	}

	// The section must still land on a selectable row, not render blank.
	[Fact]
	public async Task ApplyDpiRecommendations_WithAnUnsupportedConfiguredRate_StillSelectsARate()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			await vm.SetDeviceAsync(FakeMouse(new DeviceDefaults(Dpi: 1200, ReportRateHz: 900)));
			Thread.Sleep(600);

			await vm.ApplyDpiRecommendationsCommand.ExecuteAsync(null);
			Thread.Sleep(600);

			Assert.Equal(1000, vm.PollingRateOptions.Single(o => o.IsSelected).Hz);
		}
	}

	[Fact]
	public async Task RebuildDeviceSettings_SeedsSlidersFromTheDeclaredDefaults()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			await vm.SetDeviceAsync(FakeMouse(new DeviceDefaults(ButtonDebounceMs: 20, LiftOffDistanceMm: 3)));

			Assert.Equal(20, vm.ButtonDenounceSetting!.Value);
			Assert.Equal(3, vm.LiftOffSetting!.Value);
		}
	}

	[Fact]
	public async Task RebuildDeviceSettings_KeepsTheSliderDefaults_WhenDeviceDeclaresNone()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			await vm.SetDeviceAsync(FakeMouse(new DeviceDefaults()));

			Assert.Equal(vm.ButtonDenounceSetting!.DefaultValue, vm.ButtonDenounceSetting.Value);
			Assert.Equal(vm.LiftOffSetting!.DefaultValue, vm.LiftOffSetting.Value);
		}
	}

	[Fact]
	public async Task RebuildDeviceSettings_ClampsAnOutOfRangeDeclaredDefault()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			// 3 mm is the lift-off slider's maximum; a catalog value above it must not distort the UI.
			await vm.SetDeviceAsync(FakeMouse(new DeviceDefaults(LiftOffDistanceMm: 99)));

			Assert.Equal(vm.LiftOffSetting!.Max, vm.LiftOffSetting.Value);
		}
	}

	[Fact]
	public async Task ResetSettings_RestoresTheSlidersToTheDeclaredDefaults()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			await vm.SetDeviceAsync(FakeMouse(new DeviceDefaults(ButtonDebounceMs: 12, LiftOffDistanceMm: 2)));
			vm.ButtonDenounceSetting!.Value = 44;
			vm.LiftOffSetting!.Value = 3;

			await vm.ResetSettingsCommand.ExecuteAsync(null);

			Assert.Equal(12, vm.ButtonDenounceSetting.Value);
			Assert.Equal(2, vm.LiftOffSetting.Value);
		}
	}

	// Scope: "Recommendations" owns the whole DPI section — ladder, active stage AND polling rate.
	// The rate is often the only visibly-changing part on a device whose ladder is already stock.
	[Fact]
	public async Task ApplyDpiRecommendations_AlsoRestoresThePollingRate()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			await vm.SetDeviceAsync(FakeMouse(new DeviceDefaults(Dpi: 1200, ReportRateHz: 500)));
			Thread.Sleep(600);

			// Move off the default so the assertion cannot pass by accident.
			vm.PollingRateOptions.First(o => o.Hz != 500).SelectCommand.Execute(null);

			await vm.ApplyDpiRecommendationsCommand.ExecuteAsync(null);
			Thread.Sleep(600);

			Assert.Equal(500, vm.PollingRateOptions.Single(o => o.IsSelected).Hz);
		}
	}

	// Scope: "Restore to default" is the broad reset — it restores the DPI section as well as the
	// sliders and buttons. Without this it looked like it did nothing on a device whose sliders
	// already sat on their catalog values.
	[Fact]
	public async Task ResetSettings_AlsoRestoresTheDpiStageAndPollingRate()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			await vm.SetDeviceAsync(FakeMouse(new DeviceDefaults(Dpi: 1200, ReportRateHz: 500)));
			Thread.Sleep(600);
			var defaultStage = vm.ResolveDefaultDpiStageIndex()!.Value;

			vm.DpiPresets.First(p => p.StageIndex != defaultStage).SelectCommand.Execute(null);
			vm.PollingRateOptions.First(o => o.Hz != 500).SelectCommand.Execute(null);

			await vm.ResetSettingsCommand.ExecuteAsync(null);
			Thread.Sleep(600);

			Assert.Equal(defaultStage, vm.DpiPresets.Single(p => p.IsSelected).StageIndex);
			Assert.Equal(500, vm.PollingRateOptions.Single(o => o.IsSelected).Hz);
		}
	}

	// A restore button must always land on a DEFINED stage. It used to keep whichever stage the user
	// was on when the device declared no default DPI — indistinguishable from the button doing
	// nothing, which is how it behaved against a DeviceHost that predates the Defaults catalog block.
	[Fact]
	public async Task ApplyDpiRecommendations_WithNoDeclaredDpi_FallsBackToTheFirstStage()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			await vm.SetDeviceAsync(FakeMouse(new DeviceDefaults()));
			Thread.Sleep(600);

			var first = vm.DpiPresets.First().StageIndex;
			vm.DpiPresets.First(p => p.StageIndex != first).SelectCommand.Execute(null);

			await vm.ApplyDpiRecommendationsCommand.ExecuteAsync(null);
			Thread.Sleep(600);

			Assert.Equal(first, vm.DpiPresets.Single(p => p.IsSelected).StageIndex);

			// Also persisted, which only happens when the restore marks the stage KNOWN. Leaving it
			// unknown would show the right row but push nothing to the device.
			var live = store.GetDeviceSettings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, "");
			Assert.Equal(first, live["dpi.activeStage"]);
		}
	}

	// Recommendations restores the ladder VALUES too, not just which stage is active.
	[Fact]
	public async Task ApplyDpiRecommendations_RestoresEditedLadderValues()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			await vm.SetDeviceAsync(FakeMouse(new DeviceDefaults(Dpi: 1200)));
			Thread.Sleep(600);

			var target = vm.DpiPresets.First();
			var original = target.DpiValue;
			target.UpdateFromPersisted(original + 3000);

			await vm.ApplyDpiRecommendationsCommand.ExecuteAsync(null);
			Thread.Sleep(600);

			Assert.Equal(original, vm.DpiPresets.First().DpiValue);
		}
	}

	[Fact]
	public async Task ApplyDpiRecommendations_SelectsTheStageNearestTheDeclaredDpi()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			await vm.SetDeviceAsync(FakeMouse(new DeviceDefaults(Dpi: 1200)));
			var expected = vm.ResolveDefaultDpiStageIndex();

			// Move off the default first, so the assertion can't pass by accident.
			vm.DpiPresets.First(p => p.StageIndex != expected).SelectCommand.Execute(null);

			await vm.ApplyDpiRecommendationsCommand.ExecuteAsync(null);

			Assert.Equal(expected, vm.DpiPresets.Single(p => p.IsSelected).StageIndex);
		}
	}

	// The seed has to land in the freshly created default preset's SNAPSHOT, not only in the live
	// rows. EnsureDefaultPresetAppliedAsync applies that preset, and LoadPresetSnapshotToDevice
	// WIPES device_settings before copying the snapshot back — so a seed written after the snapshot
	// is taken disappears. Asserting the live rows once that apply has run is what catches it.
	[Fact]
	public async Task FirstOpen_SeedsTheCatalogDpiStageAndRate_AndTheySurviveTheDefaultPresetApply()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			await vm.SetDeviceAsync(FakeMouse(new DeviceDefaults(Dpi: 1200, ReportRateHz: 500)));
			var expectedStage = vm.ResolveDefaultDpiStageIndex()!.Value;

			// EnsureDefaultPresetAppliedAsync is fire-and-forget from SetDevice; let it complete.
			Thread.Sleep(600);

			var live = store.GetDeviceSettings(
				LocalStorageService.OwnerKindGuest, owner, Vid, Pid, "");

			Assert.Equal(expectedStage, live["dpi.activeStage"]);
			Assert.Equal(500, live["pollingRate.hz"]);
		}
	}

	// A device that has already been configured must not be re-seeded: the seed is gated on the
	// default preset having just been created, so a second open has to leave the user's value alone.
	[Fact]
	public async Task SecondOpen_DoesNotReSeedOverTheUsersOwnDpiStage()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			var device = FakeMouse(new DeviceDefaults(Dpi: 1200));
			await vm.SetDeviceAsync(device);
			Thread.Sleep(600);

			// The user picks a different stage, saves it into the preset, then reopens the page.
			var defaultStage = vm.ResolveDefaultDpiStageIndex();
			var other = vm.DpiPresets.First(p => p.StageIndex != defaultStage);
			other.SelectCommand.Execute(null);
			vm.SavePresetCommand.Execute(null);
			await vm.ConfirmSavePresetCommand.ExecuteAsync(null);
			Thread.Sleep(600);

			await vm.SetDeviceAsync(device);
			Thread.Sleep(600);

			var live = store.GetDeviceSettings(
				LocalStorageService.OwnerKindGuest, owner, Vid, Pid, "");
			Assert.Equal(other.StageIndex, live["dpi.activeStage"]);
		}
	}

	// Reported from a real app start: the Polling Rate section had nothing highlighted.
	// ApplyKnownDeviceState deselects every option when the DeviceHost has reported no live rate,
	// and on an existing DB with no stored rate nothing put one back. Same for the DPI stage, which
	// sat on an unowned stage-0 placeholder. Both must fall back to the device's catalog default.
	//
	// The preset is inserted up front deliberately: that is what an existing install looks like, and
	// it makes CreateDefaultPresetIfNotExists report Seeded = false, so SeedCatalogDefaultDpiAndRate
	// does NOT run. Without it the fresh-DB seed masks the bug and the test passes either way.
	[Fact]
	public async Task DeviceOpen_WithNoLiveOrStoredValue_FallsBackToTheCatalogDpiStageAndRate()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
				new PresetRecord { Name = "A", IsDefault = true });

			await vm.SetDeviceAsync(FakeMouse(new DeviceDefaults(Dpi: 1200, ReportRateHz: 500)));
			var expectedStage = vm.ResolveDefaultDpiStageIndex()!.Value;

			// LoadPersistedDeviceStateAsync is fire-and-forget from SetDevice; let it settle.
			Thread.Sleep(600);

			Assert.Equal(500, vm.PollingRateOptions.Single(o => o.IsSelected).Hz);
			Assert.Equal(expectedStage, vm.DpiPresets.Single(p => p.IsSelected).StageIndex);
		}
	}

	// The fallback must stay out of the live tables: a row the selected preset's snapshot does not
	// have is what PresetMatchesLiveState compares on, so persisting it would enable Save Preset on
	// a page the user never touched.
	[Fact]
	public async Task DeviceOpen_FallbackDoesNotEnableSavePreset()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
				new PresetRecord { Name = "A", IsDefault = true });

			await vm.SetDeviceAsync(FakeMouse(new DeviceDefaults(Dpi: 1200, ReportRateHz: 500)));
			Thread.Sleep(600);

			Assert.False(vm.HasUnsavedPresetChanges);
		}
	}

	[Fact]
	public async Task ApplyRgbDefaults_UsesTheDeclaredEffectColourAndBrightness()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			// Nothing is stored for this device, so opening RGB falls through to the defaults path.
			await vm.SetDeviceAsync(FakeMouse(new DeviceDefaults(
				RgbEnabled: true, RgbEffect: "Breathing", RgbColorHex: "#112233",
				RgbBrightness: 25, RgbSpeed: 75)));

			Assert.True(vm.IsRgbEnabled);
			Assert.Equal("Breathing", vm.ActiveRgbEffect!.EffectKey);
			Assert.Equal("#112233", vm.ActiveRgbEffect.SelectedHexColor);
			Assert.Equal(25, vm.ActiveRgbEffect.Brightness);
			Assert.Equal(75, vm.ActiveRgbEffect.Speed);
		}
	}

	[Fact]
	public async Task ApplyRgbDefaults_FallsBackToAVisibleEffect_WhenTheDeclaredOneIsNotRenderable()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			// Static is filtered out of a mouse's effect list; the section must still land on
			// something selectable rather than leaving no effect active.
			await vm.SetDeviceAsync(FakeMouse(new DeviceDefaults(RgbEffect: "Static")));

			Assert.NotNull(vm.ActiveRgbEffect);
			Assert.Contains(vm.ActiveRgbEffect!, vm.VisibleRgbEffects);
		}
	}
}
