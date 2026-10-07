using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// The audit C2 lifecycle contract for the RGB glow DispatcherTimers: they stop when the user
// leaves Device Details (ClearDeviceForExit) and when the window hides to the tray
// (PauseGlowAnimation), and they come back on re-entry (SetDeviceAsync's existing
// ApplyResolvedRgbState → StartEffectAnimation path) and on tray restore (ResumeGlowAnimation).
// Without the teardown the 33 ms timers kept firing on the UI thread forever after leaving the
// page — work for a view nobody can see, even while the app sat in the tray.
public class DeviceDetailsGlowTimerTests
{
	private const int KbVid = 14000;
	private const int KbPid = 12385;
	private const string NoKey = "";

	private sealed class FakeOwnerProvider(string kind, string id) : ICurrentOwnerProvider
	{
		private readonly SettingsOwner _owner = new(kind, id);
		public SettingsOwner? GetCurrent() => _owner;
	}

	private sealed class FakeHid : IUsbHidDeviceService
	{
		public Task<bool> SetButtonBindingsAsync(int vendorId, int productId, string deviceKey, IReadOnlyList<(MouseButton button, MouseButtonAction action, MacroDefinition? macro)> bindings)
			=> Task.FromResult(true);
		public Task<bool> ApplyDeviceConfigurationAsync(
			int vendorId, int productId, string deviceKey,
			(byte r, byte g, byte b, byte effect, byte brightness, byte speed)? led,
			IReadOnlyList<(MouseButton button, MouseButtonAction action, MacroDefinition? macro)>? bindings,
			(byte debounce, byte liftOff)? mouseSettings,
			IReadOnlyList<(int dpi, byte r, byte g, byte b)>? dpiStages,
			int? activeStage,
			int? reportRateHz)
			=> Task.FromResult(true);
		public Task<bool> SetMacrosAsync(int vendorId, int productId, string deviceKey, IReadOnlyList<(int Index, MacroRepeatMode Mode, IReadOnlyList<MacroEvent> Events, int RepeatCount)> entries)
			=> Task.FromResult(true);
		public Task<bool> SetKeyMappingAsync(int vendorId, int productId, byte matrixIndex, byte type, byte code1, byte code2)
			=> Task.FromResult(true);
		public List<HidDeviceResponse> GetDevices() => [];
		public Task StartCommunication() => Task.CompletedTask;
		public bool CheckIfAllDevicesAreDisconnected() => true;
		public Task<KeyActuationInfo?> GetKeyActuationAsync(int vendorId, int productId) =>
			Task.FromResult<KeyActuationInfo?>(null);
		public Task<bool> SetKeyActuationAsync(int vendorId, int productId, int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm) =>
			Task.FromResult(false);
		public Task<bool> SetPerKeyActuationAsync(int vendorId, int productId, IReadOnlyCollection<int> slots, int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm) =>
			Task.FromResult(false);
		public void StopCommunication() { }
		public Task<(int batteryValue, bool isCharging)> GetBatteryLevelAsync(int vendorId, int productId, string deviceKey) => Task.FromResult((0, false));
		public Task<bool> SetDeviceLedColorAsync(int vendorId, int productId, string deviceKey, string deviceType, byte r, byte g, byte b, byte effect, byte brightness, byte speed) => Task.FromResult(true);
		public Task<bool> SetDpiStagesAsync(int vendorId, int productId, string deviceKey, IReadOnlyList<(int dpi, byte r, byte g, byte b)> stages) => Task.FromResult(true);
		public Task<bool> SetActiveDpiStageAsync(int vendorId, int productId, string deviceKey, byte stageIndex) => Task.FromResult(true);
		public Task<bool> SetReportRateAsync(int vendorId, int productId, string deviceKey, int hz) => Task.FromResult(true);
		public bool TryGetLastDpiStage(int vendorId, int productId, string deviceKey, out int stageIndex) { stageIndex = 0; return false; }
		public bool TryGetLastReportRate(int vendorId, int productId, string deviceKey, out int hz) { hz = 0; return false; }
		public Task<(IReadOnlyDictionary<byte, byte> ScancodeToSlot, IReadOnlyDictionary<byte, byte> UserOverrides)?> ProbeKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
			=> Task.FromResult<(IReadOnlyDictionary<byte, byte>, IReadOnlyDictionary<byte, byte>)?>(null);
		public Task<IReadOnlyDictionary<byte, (byte Type, byte Code1, byte Code2)>?> ReadKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
			=> Task.FromResult<IReadOnlyDictionary<byte, (byte, byte, byte)>?>(null);

public Task<ExtendedKeysInfo?> GetExtendedKeysAsync(int vendorId, int productId) => Task.FromResult<ExtendedKeysInfo?>(null);
public Task<bool> SetExtendedKeysAsync(int vendorId, int productId, IReadOnlyList<ProoveGaming.App.Models.ExtendedKeyRecord> entries) => Task.FromResult(false);

#pragma warning disable CS0067 // events required by the interface but unused in tests
		public event EventHandler<SubscribeOnDevicesResponse>? DeviceFound;
		public event EventHandler<DpiStageChange>? DpiStageChanged;
		public event EventHandler<ReportRateChange>? ReportRateChanged;
		public event EventHandler<BatteryChange>? BatteryChanged;
		public event EventHandler<string>? NotifyError;
		public event EventHandler<bool>? HostAvailabilityChanged;
#pragma warning restore CS0067
	}

	private static (DeviceDetailsViewModel Vm, LocalStorageService Store) NewVm()
	{
		var owner = Guid.NewGuid().ToString("D");
		var store = new LocalStorageService("Data Source=:memory:");
		var vm = new DeviceDetailsViewModel(
			usbHidDeviceService: new FakeHid(),
			localStorage: store,
			settings: null,
			ownerProvider: new FakeOwnerProvider(LocalStorageService.OwnerKindGuest, owner));
		return (vm, store);
	}

	// An RGB keyboard whose catalog default effect is Breathing — a timer-driven animation, so
	// opening the page must arm a glow timer. (Static's preview is a fixed brush; relying on the
	// no-defaults fallback would silently pick it and assert nothing.)
	private static DoubleCardViewModel RgbKeyboard() => new()
	{
		Name = "Raster",
		DeviceTypeLabel = "Wireless Keyboard",
		VendorId = KbVid,
		ProductId = KbPid,
		SettingsVendorId = KbVid,
		SettingsProductId = KbPid,
		DeviceKey = NoKey,
		SettingsDeviceKey = NoKey,
		SupportsRgb = true,
		Defaults = new DeviceDefaults(RgbEnabled: true, RgbEffect: "Breathing"),
	};

	// The repro for C2: on the current code ClearDeviceForExit stopped volume tracking and the
	// slot self-heal loop but left both glow timers running against a nulled Device.
	[Fact]
	public async Task ClearDeviceForExit_StopsGlowTimers()
	{
		var (vm, store) = NewVm();
		using (store)
		{
			await vm.SetDeviceAsync(RgbKeyboard());
			Assert.True(vm.HasActiveGlowTimersForTests);

			vm.ClearDeviceForExit();

			Assert.False(vm.HasActiveGlowTimersForTests);
		}
	}

	// The re-entry contract: no new wiring restarts the animation — SetDeviceAsync's existing
	// ApplyResolvedRgbState → StartEffectAnimation path must bring the timers back.
	[Fact]
	public async Task SetDeviceAsync_AfterExit_RestartsGlowTimers()
	{
		var (vm, store) = NewVm();
		using (store)
		{
			await vm.SetDeviceAsync(RgbKeyboard());
			vm.ClearDeviceForExit();
			Assert.False(vm.HasActiveGlowTimersForTests);

			await vm.SetDeviceAsync(RgbKeyboard());

			Assert.True(vm.HasActiveGlowTimersForTests);
		}
	}

	// The tray seam's VM half: hide-to-tray pauses without touching Device …
	[Fact]
	public async Task PauseGlowAnimation_StopsTimers()
	{
		var (vm, store) = NewVm();
		using (store)
		{
			await vm.SetDeviceAsync(RgbKeyboard());
			Assert.True(vm.HasActiveGlowTimersForTests);

			vm.PauseGlowAnimation();

			Assert.False(vm.HasActiveGlowTimersForTests);
			Assert.NotNull(vm.Device); // pause is not an exit — the page stays populated
		}
	}

	// … and restore resumes for the still-open device with lighting enabled. Twice: a second
	// resume against live timers must be a no-op, not a second set of timers.
	[Fact]
	public async Task ResumeGlowAnimation_RestartsTimers_WhenRgbEnabled()
	{
		var (vm, store) = NewVm();
		using (store)
		{
			await vm.SetDeviceAsync(RgbKeyboard());
			vm.PauseGlowAnimation();

			vm.ResumeGlowAnimation();
			Assert.True(vm.HasActiveGlowTimersForTests);

			vm.ResumeGlowAnimation();
			Assert.True(vm.HasActiveGlowTimersForTests);
		}
	}

	// The other resume guard: a device is open but the user switched its lighting off — a tray
	// round-trip must not switch the glow animation back on behind the toggle's back.
	[Fact]
	public async Task ResumeGlowAnimation_RgbDisabled_IsNoOp()
	{
		var (vm, store) = NewVm();
		using (store)
		{
			await vm.SetDeviceAsync(RgbKeyboard());
			vm.IsRgbEnabled = false;
			Assert.False(vm.HasActiveGlowTimersForTests);

			vm.ResumeGlowAnimation();

			Assert.False(vm.HasActiveGlowTimersForTests);
		}
	}

	// Restoring from the tray while on Home (no device open) is the common case — nothing to
	// animate, nothing may throw.
	[Fact]
	public void ResumeGlowAnimation_NoDevice_IsNoOp()
	{
		var (vm, store) = NewVm();
		using (store)
		{
			vm.ResumeGlowAnimation();

			Assert.False(vm.HasActiveGlowTimersForTests);
		}
	}
}
