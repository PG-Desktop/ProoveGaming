using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using ProoveGaming.App.Models.Keyboards;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// Covers the Rampage key-assignment path on DeviceDetailsViewModel. Rampage differs from
// Raster / T-Racer in three ways that are easy to regress and invisible without hardware:
//   1. Matrix slots come from the catalog, never from the scancode probe.
//   2. Its base matrix IS the live state, so reads use ReadKeyMatrix, not ProbeKeyMatrix.
//   3. Modifier keys are identified by (bitmask, scancode) — every one of them has scancode 0x00.
public class DeviceDetailsRampageKeyAssignmentTests
{
	private const int RampageVid = 10473;
	private const int RampagePid = 12888;

	private sealed class FakeOwnerProvider(string kind, string id) : ICurrentOwnerProvider
	{
		private readonly SettingsOwner _owner = new(kind, id);
		public SettingsOwner? GetCurrent() => _owner;
	}

	private sealed class FakeHid : IUsbHidDeviceService
	{
		public List<(byte MatrixIndex, byte Type, byte Code1, byte Code2)> Writes { get; } = [];
		public Dictionary<byte, (byte Type, byte Code1, byte Code2)>? Matrix { get; set; }
		public bool WriteSucceeds { get; set; } = true;
		public int ProbeKeyMatrixCalls { get; private set; }
		public int ReadKeyMatrixCalls { get; private set; }

		public Task<bool> SetKeyMappingAsync(int vendorId, int productId, byte matrixIndex, byte type, byte code1, byte code2)
		{
			Writes.Add((matrixIndex, type, code1, code2));
			if (WriteSucceeds && Matrix is not null) Matrix[matrixIndex] = (type, code1, code2);
			return Task.FromResult(WriteSucceeds);
		}

		public Task<IReadOnlyDictionary<byte, (byte Type, byte Code1, byte Code2)>?> ReadKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
		{
			ReadKeyMatrixCalls++;
			return Task.FromResult<IReadOnlyDictionary<byte, (byte, byte, byte)>?>(
				Matrix is null ? null : new Dictionary<byte, (byte, byte, byte)>(Matrix));
		}

		public Task<(IReadOnlyDictionary<byte, byte> ScancodeToSlot, IReadOnlyDictionary<byte, byte> UserOverrides)?> ProbeKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
		{
			ProbeKeyMatrixCalls++;
			return Task.FromResult<(IReadOnlyDictionary<byte, byte>, IReadOnlyDictionary<byte, byte>)?>(null);
		}

		public Task<bool> ApplyDeviceConfigurationAsync(
			int vendorId, int productId, string deviceKey,
			(byte r, byte g, byte b, byte effect, byte brightness, byte speed)? led,
			IReadOnlyList<(MouseButton button, MouseButtonAction action, MacroDefinition? macro)>? bindings,
			(byte debounce, byte liftOff)? mouseSettings,
			IReadOnlyList<(int dpi, byte r, byte g, byte b)>? dpiStages,
			int? activeStage,
			int? reportRateHz) => Task.FromResult(true);

		public Task<bool> SetMacrosAsync(
			int vendorId, int productId, string deviceKey,
			IReadOnlyList<(int Index, MacroRepeatMode Mode, IReadOnlyList<MacroEvent> Events, int RepeatCount)> entries)
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
		public Task<bool> SetButtonBindingsAsync(int vendorId, int productId, string deviceKey, IReadOnlyList<(MouseButton button, MouseButtonAction action, MacroDefinition? macro)> bindings) => Task.FromResult(true);
		public bool TryGetLastDpiStage(int vendorId, int productId, string deviceKey, out int stageIndex) { stageIndex = 0; return false; }
		public bool TryGetLastReportRate(int vendorId, int productId, string deviceKey, out int hz) { hz = 0; return false; }

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

	private static (DeviceDetailsViewModel Vm, FakeHid Hid) NewVm()
	{
		var hid = new FakeHid();
		var vm = new DeviceDetailsViewModel(
			usbHidDeviceService: hid,
			localStorage: new LocalStorageService("Data Source=:memory:"),
			settings: null,
			ownerProvider: new FakeOwnerProvider(LocalStorageService.OwnerKindGuest, System.Guid.NewGuid().ToString("D")));
		// A failed read self-heals through a retry loop with real-world waits; collapse them so a
		// test that fails a read doesn't leave a loop sleeping in the background.
		vm.SlotReadRetryDelay = _ => Task.CompletedTask;
		return (vm, hid);
	}

	private static SingleCardViewModel RampageCard() => new()
	{
		Name = "Rampage",
		DeviceTypeLabel = "Wired Keyboard",
		VendorId = RampageVid,
		ProductId = RampagePid,
		SettingsVendorId = RampageVid,
		SettingsProductId = RampagePid,
		DeviceKey = "",
		SettingsDeviceKey = "",
	};

	private static SingleCardViewModel RasterCard() => new()
	{
		Name = "Raster",
		DeviceTypeLabel = "Wireless Keyboard",
		VendorId = RasterKeyCatalog.RasterVendorId,
		ProductId = RasterKeyCatalog.RasterProductId,
		SettingsVendorId = RasterKeyCatalog.RasterVendorId,
		SettingsProductId = RasterKeyCatalog.RasterProductId,
		DeviceKey = "",
		SettingsDeviceKey = "",
	};

	// Factory state for the slots the tests touch.
	private static Dictionary<byte, (byte, byte, byte)> FactoryMatrix() => new()
	{
		[35] = (0x10, 0x00, 0x08), // E
		[49] = (0x10, 0x00, 0x04), // A
		[53] = (0x10, 0x00, 0x0A), // G
		[64] = (0x10, 0x02, 0x00), // Left Shift
		[76] = (0x10, 0x20, 0x00), // Right Shift
	};

	// Set the device and build the key layout, without SetDevice's unrelated side effects
	// (button bindings, presets, persisted state) which would drag SQLite into these tests.
	private static void Load(DeviceDetailsViewModel vm, DeviceCardViewModel card)
	{
		vm.Device = card;
		vm.LoadKeyLayoutForDevice();
	}

	private static KeyAssignmentOptionViewModel Key(DeviceDetailsViewModel vm, string name) =>
		vm.PhysicalKeys.Single(k => k.DevKeyName == name);

	private static KeyAssignmentOptionViewModel Target(DeviceDetailsViewModel vm, string name) =>
		vm.AssignmentTargets.Single(t => t.DevKeyName == name);

	[Fact]
	public void LoadKeyLayout_BuildsTheRampageCatalogWithVisualBounds()
	{
		var (vm, _) = NewVm();
		Load(vm, RampageCard());

		Assert.True(vm.HasInteractiveKeyboardOverlay);
		Assert.Equal(86, vm.PhysicalKeys.Count);
		// One target per catalog key, plus the Smart Infinite consumer-command rows behind the
		// Key Assignment Dialog's SYSTEM tab (see DeviceDetailsViewModel.KeyDialog).
		Assert.Equal(86, vm.AssignmentTargets.Count(t => !t.DevKeyName.StartsWith("Command:")));
		Assert.Equal(vm.SystemCommandTargets.Count,
			vm.AssignmentTargets.Count(t => t.DevKeyName.StartsWith("Command:")));
		Assert.All(vm.PhysicalKeys, k =>
		{
			Assert.True(k.PixelWidth > 0);
			Assert.True(k.PixelHeight > 0);
		});
	}

	[Fact]
	public void LoadKeyLayout_UsesTheRampageCanvasSizeNotRasters()
	{
		var (vm, _) = NewVm();
		Load(vm, RampageCard());

		Assert.Equal(RampageVisualLayout.CanvasWidth, vm.OverlayCanvasWidth);
		Assert.Equal(RampageVisualLayout.CanvasHeight, vm.OverlayCanvasHeight);
		// Regression guard for the previously hardcoded 665x221 canvas.
		Assert.NotEqual(RasterVisualLayout.CanvasWidth, vm.OverlayCanvasWidth);
	}

	[Fact]
	public void LoadKeyLayout_CarriesModifierMasksOntoTheOptionViewModels()
	{
		var (vm, _) = NewVm();
		Load(vm, RampageCard());

		var leftShift = Key(vm, "LeftShift");
		Assert.Equal(0x02, leftShift.Bitmask);
		Assert.Equal(0x00, leftShift.ScanCode);

		var e = Key(vm, "E");
		Assert.Equal(0x00, e.Bitmask);
		Assert.Equal(0x08, e.ScanCode);
	}

	[Fact]
	public async Task Refresh_UsesTheSlotKeyedReadAndNeverTheScancodeProbe()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = FactoryMatrix();
		Load(vm, RampageCard());

		await vm.RefreshFromDeviceAsync();

		Assert.Equal(1, hid.ReadKeyMatrixCalls);
		Assert.Equal(0, hid.ProbeKeyMatrixCalls);
	}

	[Fact]
	public async Task ApplyTarget_WritesTheCatalogSlotAndTheTargetsFullRecord()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = FactoryMatrix();
		Load(vm, RampageCard());
		await vm.RefreshFromDeviceAsync();

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "E"));
		await vm.SelectAssignmentTargetCommand.ExecuteAsync(Target(vm, "A"));

		var write = Assert.Single(hid.Writes);
		Assert.Equal(35, write.MatrixIndex); // E's slot in the vendor factory table
		Assert.Equal(0x10, write.Type);
		Assert.Equal(0x00, write.Code1);
		Assert.Equal(0x04, write.Code2); // A
	}

	// The failure mode a scancode-keyed map would have introduced: after remapping E to A, the
	// probe map would report "A -> slot 35" and lose E, so the next click on E would resolve the
	// wrong slot.
	[Fact]
	public async Task ApplyTarget_KeepsTheSlotStableAfterARemap()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = FactoryMatrix();
		Load(vm, RampageCard());
		await vm.RefreshFromDeviceAsync();

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "E"));
		await vm.SelectAssignmentTargetCommand.ExecuteAsync(Target(vm, "A"));

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "G"));
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "E"));

		Assert.True(Target(vm, "A").IsSelected);
		Assert.False(Target(vm, "E").IsSelected);

		await vm.SelectAssignmentTargetCommand.ExecuteAsync(Target(vm, "G"));
		Assert.Equal(35, hid.Writes[^1].MatrixIndex);
	}

	[Fact]
	public async Task SelectPhysicalKey_HighlightsTheRecordTheDeviceActuallyHas()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = FactoryMatrix();
		hid.Matrix[35] = (0x10, 0x00, 0x04); // E already remapped to A on the device
		Load(vm, RampageCard());
		await vm.RefreshFromDeviceAsync();

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "E"));

		Assert.True(Target(vm, "A").IsSelected);
		Assert.False(Target(vm, "E").IsSelected);
	}

	[Fact]
	public async Task SelectModifierKey_HighlightsExactlyOneTarget()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = FactoryMatrix();
		Load(vm, RampageCard());
		await vm.RefreshFromDeviceAsync();

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "LeftShift"));

		var selected = vm.AssignmentTargets.Where(t => t.IsSelected).ToList();
		var only = Assert.Single(selected);
		Assert.Equal("LeftShift", only.DevKeyName);
	}

	[Fact]
	public async Task FailedWrite_LeavesTheHighlightWhereTheDeviceIs()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = FactoryMatrix();
		Load(vm, RampageCard());
		await vm.RefreshFromDeviceAsync();

		hid.WriteSucceeds = false;

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "E"));
		await vm.SelectAssignmentTargetCommand.ExecuteAsync(Target(vm, "A"));

		// The optimistic highlight must be rolled back to the device's real state.
		Assert.True(Target(vm, "E").IsSelected);
		Assert.False(Target(vm, "A").IsSelected);
	}

	// Bluetooth cannot carry the matrix read (the read-only-transport branch shows the fallback
	// banner and starts no self-heal loop), so a cable landing while the page is OPEN must re-run
	// the on-connect refresh — before this hook, only leaving to Home and reopening the page
	// recovered (QA 2026-08-31).
	[Fact]
	public async Task BluetoothToUsbSwitch_RereadsTheMatrixAndClearsTheBanner()
	{
		var (vm, hid) = NewVm();
		var card = RampageCard();
		card.HasBluetooth = true;
		card.SupportsConfiguration = false;
		hid.Matrix = null; // unreadable over Bluetooth
		Load(vm, card);
		await vm.ProbeAndUpdateMatrixMapAsync();
		Assert.True(vm.KeyAssignmentReadFailed);

		// Reverse-order variant: wire raised before bluetooth drops, so the HasBluetooth flip is
		// the completing event. The REAL ApplyEntryToCard order (bluetooth first, wire after) is
		// covered by the next test.
		hid.Matrix = FactoryMatrix();
		card.SupportsConfiguration = true;
		card.HasWire = true;
		card.HasBluetooth = false;

		Assert.NotNull(vm.TransportSwitchRefreshTask);
		await vm.TransportSwitchRefreshTask!;

		Assert.False(vm.KeyAssignmentReadFailed);
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "E"));
		Assert.True(Target(vm, "E").IsSelected);
	}

	// The exact event order ApplyEntryToCard produces: HasBluetooth drops BEFORE HasWire rises.
	// A hook keyed on the HasBluetooth flip alone sees a card with no live link at that moment
	// and skips the refresh — which is how the stale banner survived the first fix (QA 2026-08-31,
	// round 2). The armed/disarmed recovery must fire on whichever later event completes the
	// "off Bluetooth, on a live link" state.
	[Fact]
	public async Task BluetoothToUsbSwitch_InHomeViewModelEventOrder_StillRefreshes()
	{
		var (vm, hid) = NewVm();
		var card = RampageCard();
		card.HasBluetooth = true;
		card.SupportsConfiguration = false;
		hid.Matrix = null;
		Load(vm, card);
		await vm.ProbeAndUpdateMatrixMapAsync();
		Assert.True(vm.KeyAssignmentReadFailed);

		hid.Matrix = FactoryMatrix();
		card.SupportsConfiguration = true;
		card.HasBluetooth = false; // ApplyEntryToCard's order: bluetooth first...
		card.HasWire = true;       // ...wire after

		Assert.NotNull(vm.TransportSwitchRefreshTask);
		await vm.TransportSwitchRefreshTask!;

		Assert.False(vm.KeyAssignmentReadFailed);
	}

	[Fact]
	public async Task BluetoothDropWithNothingPlugged_IsADisconnectNotARefresh()
	{
		var (vm, hid) = NewVm();
		var card = RampageCard();
		card.HasBluetooth = true;
		card.SupportsConfiguration = false;
		hid.Matrix = null;
		Load(vm, card);
		await vm.ProbeAndUpdateMatrixMapAsync();
		var readsAfterOpen = hid.ReadKeyMatrixCalls;

		card.HasBluetooth = false; // link gone, no wire, no dongle

		Assert.Null(vm.TransportSwitchRefreshTask);
		Assert.Equal(readsAfterOpen, hid.ReadKeyMatrixCalls);
		Assert.True(vm.KeyAssignmentReadFailed);
	}

	// The recovery must stay armed across a gap: the Bluetooth link dying first (nothing plugged,
	// no refresh — the test above) and the cable landing seconds later is one user story, not two.
	[Fact]
	public async Task BluetoothDropThenCableLater_StillRecovers()
	{
		var (vm, hid) = NewVm();
		var card = RampageCard();
		card.HasBluetooth = true;
		card.SupportsConfiguration = false;
		hid.Matrix = null;
		Load(vm, card);
		await vm.ProbeAndUpdateMatrixMapAsync();

		card.HasBluetooth = false; // step 1: link gone, nothing plugged — no refresh yet
		Assert.Null(vm.TransportSwitchRefreshTask);

		hid.Matrix = FactoryMatrix(); // step 2: the cable lands
		card.SupportsConfiguration = true;
		card.HasWire = true;

		Assert.NotNull(vm.TransportSwitchRefreshTask);
		await vm.TransportSwitchRefreshTask!;
		Assert.False(vm.KeyAssignmentReadFailed);
	}

	// A page opened on cable arms only when Bluetooth takes over mid-session (cable pulled),
	// so a later cable return still refreshes — the arm is event-driven, not open-time-only.
	[Fact]
	public async Task CableToBluetoothToCable_RearmsAndRefreshes()
	{
		var (vm, hid) = NewVm();
		var card = RampageCard();
		card.HasWire = true;
		hid.Matrix = FactoryMatrix();
		Load(vm, card);
		await vm.ProbeAndUpdateMatrixMapAsync();
		var readsAfterOpen = hid.ReadKeyMatrixCalls;

		card.HasWire = false; // cable pulled...
		card.HasBluetooth = true; // ...Bluetooth takes over: arms the recovery
		Assert.Null(vm.TransportSwitchRefreshTask);

		card.HasBluetooth = false; // cable returns, ApplyEntryToCard order
		card.HasWire = true;

		Assert.NotNull(vm.TransportSwitchRefreshTask);
		await vm.TransportSwitchRefreshTask!;
		Assert.True(hid.ReadKeyMatrixCalls > readsAfterOpen);
	}

	// Leaving the page unsubscribes from the card: transport flips after exit must not run a
	// refresh against a page nobody is on.
	[Fact]
	public async Task TransportSwitchAfterPageExit_DoesNotRefresh()
	{
		var (vm, hid) = NewVm();
		var card = RampageCard();
		card.HasBluetooth = true;
		card.SupportsConfiguration = false;
		hid.Matrix = null;
		Load(vm, card);
		await vm.ProbeAndUpdateMatrixMapAsync();
		var readsAfterOpen = hid.ReadKeyMatrixCalls;

		vm.Device = null; // page exit (ClearDeviceForExit's path)

		hid.Matrix = FactoryMatrix();
		card.SupportsConfiguration = true;
		card.HasWire = true;
		card.HasBluetooth = false;

		Assert.Null(vm.TransportSwitchRefreshTask);
		Assert.Equal(readsAfterOpen, hid.ReadKeyMatrixCalls);
	}

	[Fact]
	public async Task FailedRead_FallsBackToFactoryAssignmentsRatherThanBlankState()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = null; // read fails
		Load(vm, RampageCard());

		await vm.RefreshFromDeviceAsync();

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "E"));
		Assert.True(Target(vm, "E").IsSelected);
	}

	[Fact]
	public void BluetoothRampage_WithoutVidPid_StillRendersTheOverlay()
	{
		var (vm, _) = NewVm();
		Load(vm, new SingleCardViewModel
		{
			Name = "Rampage",
			DeviceTypeLabel = "Wireless Keyboard",
			VendorId = 0,
			ProductId = 0,
			DeviceKey = "",
			SettingsDeviceKey = "",
		});

		Assert.True(vm.HasInteractiveKeyboardOverlay);
		Assert.Equal(86, vm.PhysicalKeys.Count);
	}

	// Regression guard: Raster must keep its own catalog, canvas and probe-based read path.
	[Fact]
	public async Task RasterIsUnaffected_KeepsItsCatalogAndScancodeProbe()
	{
		var (vm, hid) = NewVm();
		Load(vm, RasterCard());

		await vm.RefreshFromDeviceAsync();

		Assert.Equal(RasterKeyCatalog.Keys.Count, vm.PhysicalKeys.Count);
		Assert.Equal(RasterVisualLayout.CanvasWidth, vm.OverlayCanvasWidth);
		Assert.Equal(1, hid.ProbeKeyMatrixCalls);
		Assert.Equal(0, hid.ReadKeyMatrixCalls);
	}
}
