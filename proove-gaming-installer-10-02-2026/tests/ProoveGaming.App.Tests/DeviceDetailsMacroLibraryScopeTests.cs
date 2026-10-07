using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// The macro library is per device, so it reloads on every device switch rather than once per app
// session. DeviceDetailsViewModel is a singleton reused through SetDevice, which is what makes the
// reload — and its ordering against LoadButtonBindings — load-bearing.
public class DeviceDetailsMacroLibraryScopeTests
{
	private const int MouseVid = 0x04D9;
	private const int MousePid = 0xA23F;
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
		public event System.EventHandler<SubscribeOnDevicesResponse>? DeviceFound;
		public event System.EventHandler<DpiStageChange>? DpiStageChanged;
		public event System.EventHandler<ReportRateChange>? ReportRateChanged;
		public event System.EventHandler<BatteryChange>? BatteryChanged;
		public event System.EventHandler<string>? NotifyError;
		public event System.EventHandler<bool>? HostAvailabilityChanged;
#pragma warning restore CS0067
	}

	private static (DeviceDetailsViewModel Vm, LocalStorageService Store, string Owner) NewVm()
	{
		var owner = System.Guid.NewGuid().ToString("D");
		var store = new LocalStorageService("Data Source=:memory:");
		var vm = new DeviceDetailsViewModel(
			usbHidDeviceService: new FakeHid(),
			localStorage: store,
			settings: null,
			ownerProvider: new FakeOwnerProvider(LocalStorageService.OwnerKindGuest, owner));
		return (vm, store, owner);
	}

	// Hotspots come from catalog config, and SetDevice rebuilds PartHotspots from them on every
	// call — so the config is what has to carry the hotspot, not a hand-added PartHotspot.
	private static SingleCardViewModel Mouse() => new()
	{
		Name = "Test Mouse",
		DeviceTypeLabel = "Wired Mouse",
		VendorId = MouseVid,
		ProductId = MousePid,
		SettingsVendorId = MouseVid,
		SettingsProductId = MousePid,
		DeviceKey = NoKey,
		SettingsDeviceKey = NoKey,
		SupportsButtonRemap = true,
		SupportsMacros = true,
		Hotspots = [new DeviceHotspotInfo("right-click", 0, 0, 0, 0, "Right", false)],
	};

	private static DoubleCardViewModel Keyboard() => new()
	{
		Name = "Raster",
		DeviceTypeLabel = "Wireless Keyboard",
		VendorId = KbVid,
		ProductId = KbPid,
		SettingsVendorId = KbVid,
		SettingsProductId = KbPid,
		DeviceKey = NoKey,
		SettingsDeviceKey = NoKey,
		SupportsMacros = true,
	};

	private static MacroViewModel Record(DeviceDetailsViewModel vm, string name)
	{
		vm.OpenCreateMacroCommand.Execute(null);
		vm.NewMacroName = name;
		vm.SaveCreateMacroCommand.Execute(null);
		return vm.Macros.Single(m => m.Name == name);
	}

	[Fact]
	public async Task Library_ShowsOnlyTheOpenDevicesMacros()
	{
		var (vm, store, _) = NewVm();
		using (store)
		{
			await vm.SetDeviceAsync(Mouse());
			Record(vm, "MouseOnly");
			Assert.Contains(vm.Macros, m => m.Name == "MouseOnly");

			await vm.SetDeviceAsync(Keyboard());
			Assert.DoesNotContain(vm.Macros, m => m.Name == "MouseOnly");

			Record(vm, "KeyOnly");
			Assert.Contains(vm.Macros, m => m.Name == "KeyOnly");

			await vm.SetDeviceAsync(Mouse());
			Assert.Contains(vm.Macros, m => m.Name == "MouseOnly");
			Assert.DoesNotContain(vm.Macros, m => m.Name == "KeyOnly");
		}
	}

	[Fact]
	public async Task Library_IsPersistedAgainstTheOpenDevice()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			await vm.SetDeviceAsync(Keyboard());
			Record(vm, "OnKeyboard");

			Assert.Single(store.GetMacros(LocalStorageService.OwnerKindGuest, owner, KbVid, KbPid, NoKey));
			Assert.Empty(store.GetMacros(LocalStorageService.OwnerKindGuest, owner, MouseVid, MousePid, NoKey));
		}
	}

	// LoadButtonBindings resolves each bound button's macro by looking its id up in Macros, so the
	// library has to be loaded first. Wrong order and the button keeps its Macro action but silently
	// loses the macro itself — no crash, no message.
	[Fact]
	public async Task BoundButton_KeepsItsMacro_AcrossADeviceSwitch()
	{
		var (vm, store, _) = NewVm();
		using (store)
		{
			var mouse = Mouse();
			await vm.SetDeviceAsync(mouse);
			vm.ActiveSection = NavSection.Macros;

			var macro = Record(vm, "Bound");
			var hotspot = vm.PartHotspots.Single();
			vm.SelectHotspotCommand.Execute(hotspot);
			vm.AssignMacroToSelectedButtonCommand.Execute(macro);
			Assert.Equal("Bound", hotspot.AssignedMacroName);

			// Away and back through the production entry point, on the same card instance — a
			// fresh one would carry no hotspots and prove nothing.
			await vm.SetDeviceAsync(Keyboard());
			await vm.SetDeviceAsync(mouse);

			vm.ActiveSection = NavSection.Macros;
			var restored = vm.PartHotspots.Single(h => h.Id == "right-click");
			Assert.Equal("Bound", restored.AssignedMacroName);
		}
	}

	// An abandoned recording must not follow the user to the next device and save against it.
	[Fact]
	public async Task InProgressRecording_IsDiscardedOnDeviceSwitch()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			await vm.SetDeviceAsync(Mouse());
			vm.OpenCreateMacroCommand.Execute(null);
			vm.NewMacroName = "HalfDone";

			await vm.SetDeviceAsync(Keyboard());

			Assert.False(vm.IsCreateMacroOpen);
			Assert.Empty(vm.Macros);
			Assert.Empty(store.GetMacros(LocalStorageService.OwnerKindGuest, owner, KbVid, KbPid, NoKey));
		}
	}
}
