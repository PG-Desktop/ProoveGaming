using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using ProoveGaming.App.Models.Keyboards;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// Covers the Cyrex key-assignment path on DeviceDetailsViewModel. Cyrex differs from Raster /
// T-Racer in three ways that are easy to regress and invisible without hardware:
//   1. Matrix slots come from the catalog, never from the scancode probe.
//   2. Its base matrix IS the live state, so reads use ReadKeyMatrix, not ProbeKeyMatrix.
//   3. Modifier keys are identified by (bitmask, scancode) — every one of them has scancode 0x00.
public class DeviceDetailsCyrexKeyAssignmentTests
{
	private const int CyrexVid = 10473;
	private const int CyrexPid = 12914;

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

		// Same purpose as ReadGate: hold a write open so a device switch can happen mid-flight.
		public TaskCompletionSource? WriteGate { get; set; }

		public async Task<bool> SetKeyMappingAsync(int vendorId, int productId, byte matrixIndex, byte type, byte code1, byte code2)
		{
			Writes.Add((matrixIndex, type, code1, code2));
			if (WriteSucceeds && Matrix is not null) Matrix[matrixIndex] = (type, code1, code2);

			if (WriteGate is not null)
				await WriteGate.Task;

			return WriteSucceeds;
		}

		// Set to hold a read open so a test can have two in flight at once, the way a user switching
		// devices mid-read does. Each read captures the Matrix as it was when the call was made, so a
		// later change to Matrix cannot retroactively alter an already-started read.
		public TaskCompletionSource? ReadGate { get; set; }

		public async Task<IReadOnlyDictionary<byte, (byte Type, byte Code1, byte Code2)>?> ReadKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
		{
			ReadKeyMatrixCalls++;
			var snapshot = Matrix is null ? null : new Dictionary<byte, (byte, byte, byte)>(Matrix);

			if (ReadGate is not null)
				await ReadGate.Task;

			return snapshot;
		}

		public Task<(IReadOnlyDictionary<byte, byte> ScancodeToSlot, IReadOnlyDictionary<byte, byte> UserOverrides)?> ProbeKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
		{
			ProbeKeyMatrixCalls++;
			return Task.FromResult<(IReadOnlyDictionary<byte, byte>, IReadOnlyDictionary<byte, byte>)?>(null);
		}

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
		public Task<bool> ApplyDeviceConfigurationAsync(
			int vendorId, int productId, string deviceKey,
			(byte r, byte g, byte b, byte effect, byte brightness, byte speed)? led,
			IReadOnlyList<(MouseButton button, MouseButtonAction action, MacroDefinition? macro)>? bindings,
			(byte debounce, byte liftOff)? mouseSettings,
			IReadOnlyList<(int dpi, byte r, byte g, byte b)>? dpiStages,
			int? activeStage,
			int? reportRateHz)
			=> Task.FromResult(true);
		// Keyboard macro-library upload; Cyrex and Aress do not declare macro support, so these
		// tests never reach it.
		public Task<bool> SetMacrosAsync(int vendorId, int productId, string deviceKey, IReadOnlyList<(int Index, MacroRepeatMode Mode, IReadOnlyList<MacroEvent> Events, int RepeatCount)> entries) => Task.FromResult(true);
		public bool TryGetLastDpiStage(int vendorId, int productId, string deviceKey, out int stageIndex) { stageIndex = 0; return false; }
		public bool TryGetLastReportRate(int vendorId, int productId, string deviceKey, out int hz) { hz = 0; return false; }

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

	private static (DeviceDetailsViewModel Vm, FakeHid Hid) NewVm()
	{
		var hid = new FakeHid();
		var vm = new DeviceDetailsViewModel(
			usbHidDeviceService: hid,
			localStorage: new LocalStorageService("Data Source=:memory:"),
			settings: null,
			ownerProvider: new FakeOwnerProvider(LocalStorageService.OwnerKindGuest, Guid.NewGuid().ToString("D")));
		// A failed read now self-heals through a retry loop with real-world waits; collapse them so
		// tests drive the loop to its outcome instantly (awaited via SelfHeal below).
		vm.SlotReadRetryDelay = _ => Task.CompletedTask;
		return (vm, hid);
	}

	// The failure banner raises from the fire-and-forget self-heal loop, not from the read call
	// itself — awaiting the loop's task is what makes a "read failed" outcome observable.
	private static Task SelfHeal(DeviceDetailsViewModel vm) =>
		vm.SlotReadSelfHealTask ?? Task.CompletedTask;

	private static SingleCardViewModel CyrexCard() => new()
	{
		Name = "Cyrex",
		DeviceTypeLabel = "Wired Keyboard",
		VendorId = CyrexVid,
		ProductId = CyrexPid,
		SettingsVendorId = CyrexVid,
		SettingsProductId = CyrexPid,
		DeviceKey = "",
		SettingsDeviceKey = "",
	};

	private static SingleCardViewModel AressCard() => new()
	{
		Name = "Aress",
		DeviceTypeLabel = "Wired Keyboard",
		VendorId = AressKeyCatalog.AressVendorId,
		ProductId = AressKeyCatalog.AressProductId,
		SettingsVendorId = AressKeyCatalog.AressVendorId,
		SettingsProductId = AressKeyCatalog.AressProductId,
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

	// The real slots this keyboard reports, for the keys these tests touch.
	private static Dictionary<byte, (byte, byte, byte)> LiveMatrix() => new()
	{
		[18] = (0x10, 0x00, 0x08), // E
		[31] = (0x10, 0x00, 0x04), // A
		[35] = (0x10, 0x00, 0x0A), // G
		[45] = (0x10, 0x02, 0x00), // Left Shift
		[57] = (0x10, 0x20, 0x00), // Right Shift
		[14] = (0x10, 0x00, 0x4C), // Delete — Cyrex's navigation column
	};

	// Aress reports the same matrix except in the three navigation slots. Slot 14 is the one these
	// tests lean on: Insert here, Delete on Cyrex.
	private static Dictionary<byte, (byte, byte, byte)> AressLiveMatrix()
	{
		var m = LiveMatrix();
		m[14] = (0x10, 0x00, 0x49); // Insert
		return m;
	}

	// Set the device and build the layout without SetDevice's unrelated side effects (button
	// bindings, presets, persisted state), which would drag SQLite into these tests.
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
	public void LoadKeyLayout_BuildsTheCyrexCatalogWithVisualBounds()
	{
		var (vm, _) = NewVm();
		Load(vm, CyrexCard());

		Assert.True(vm.HasInteractiveKeyboardOverlay);
		Assert.Equal(CyrexKeyCatalog.Keys.Count, vm.PhysicalKeys.Count);
		Assert.Equal(67, vm.PhysicalKeys.Count);
		Assert.All(vm.PhysicalKeys, k =>
		{
			Assert.True(k.PixelWidth > 0);
			Assert.True(k.PixelHeight > 0);
		});
	}

	[Fact]
	public void LoadKeyLayout_UsesTheCyrexCanvasSizeNotRasters()
	{
		var (vm, _) = NewVm();
		Load(vm, CyrexCard());

		Assert.Equal(CyrexVisualLayout.CanvasWidth, vm.OverlayCanvasWidth);
		Assert.Equal(CyrexVisualLayout.CanvasHeight, vm.OverlayCanvasHeight);
		// Regression guard for the previously hardcoded 665x221 canvas.
		Assert.NotEqual(RasterVisualLayout.CanvasWidth, vm.OverlayCanvasWidth);
	}

	[Fact]
	public void LoadKeyLayout_CarriesModifierMasksOntoTheOptionViewModels()
	{
		var (vm, _) = NewVm();
		Load(vm, CyrexCard());

		var leftShift = Key(vm, "LeftShift");
		Assert.Equal(0x02, leftShift.Bitmask);
		Assert.Equal(0x00, leftShift.ScanCode);

		var e = Key(vm, "E");
		Assert.Equal(0x00, e.Bitmask);
		Assert.Equal(0x08, e.ScanCode);
	}

	[Fact]
	public void LoadKeyLayout_HasNoFunctionRow()
	{
		var (vm, _) = NewVm();
		Load(vm, CyrexCard());

		Assert.DoesNotContain(vm.PhysicalKeys, k => k.ScanCode is >= 0x3A and <= 0x45);
	}

	[Fact]
	public async Task Refresh_UsesTheSlotKeyedReadAndNeverTheScancodeProbe()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = LiveMatrix();
		Load(vm, CyrexCard());

		await vm.RefreshFromDeviceAsync();

		Assert.Equal(1, hid.ReadKeyMatrixCalls);
		Assert.Equal(0, hid.ProbeKeyMatrixCalls);
	}

	[Fact]
	public async Task ApplyTarget_WritesTheCatalogSlotAndTheTargetsFullRecord()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = LiveMatrix();
		Load(vm, CyrexCard());
		await vm.RefreshFromDeviceAsync();

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "E"));
		await vm.SelectAssignmentTargetCommand.ExecuteAsync(Target(vm, "A"));

		var write = Assert.Single(hid.Writes);
		Assert.Equal(18, write.MatrixIndex); // E's slot in the device's own factory table
		Assert.Equal(0x10, write.Type);
		Assert.Equal(0x00, write.Code1);
		Assert.Equal(0x04, write.Code2); // A
	}

	// The failure mode a scancode-keyed map would introduce: after remapping E to A, the probe map
	// would report "A -> slot 18" and lose E, so the next click on E would resolve the wrong slot.
	[Fact]
	public async Task ApplyTarget_KeepsTheSlotStableAfterARemap()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = LiveMatrix();
		Load(vm, CyrexCard());
		await vm.RefreshFromDeviceAsync();

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "E"));
		await vm.SelectAssignmentTargetCommand.ExecuteAsync(Target(vm, "A"));

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "G"));
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "E"));

		Assert.True(Target(vm, "A").IsSelected);
		Assert.False(Target(vm, "E").IsSelected);

		await vm.SelectAssignmentTargetCommand.ExecuteAsync(Target(vm, "G"));
		Assert.Equal(18, hid.Writes[^1].MatrixIndex);
	}

	[Fact]
	public async Task SelectPhysicalKey_HighlightsTheRecordTheDeviceActuallyHas()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = LiveMatrix();
		hid.Matrix[18] = (0x10, 0x00, 0x04); // E already remapped to A on the device
		Load(vm, CyrexCard());
		await vm.RefreshFromDeviceAsync();

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "E"));

		Assert.True(Target(vm, "A").IsSelected);
		Assert.False(Target(vm, "E").IsSelected);
	}

	[Fact]
	public async Task SelectModifierKey_HighlightsExactlyOneTarget()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = LiveMatrix();
		Load(vm, CyrexCard());
		await vm.RefreshFromDeviceAsync();

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "LeftShift"));

		var only = Assert.Single(vm.AssignmentTargets, t => t.IsSelected);
		Assert.Equal("LeftShift", only.DevKeyName);
	}

	[Fact]
	public async Task FailedWrite_LeavesTheHighlightWhereTheDeviceIs()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = LiveMatrix();
		Load(vm, CyrexCard());
		await vm.RefreshFromDeviceAsync();

		hid.WriteSucceeds = false;

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "E"));
		await vm.SelectAssignmentTargetCommand.ExecuteAsync(Target(vm, "A"));

		// The optimistic highlight must roll back to the device's real state.
		Assert.True(Target(vm, "E").IsSelected);
		Assert.False(Target(vm, "A").IsSelected);
	}

	[Fact]
	public async Task FailedRead_FallsBackToFactoryAssignmentsRatherThanBlankState()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = null; // read fails
		Load(vm, CyrexCard());

		await vm.RefreshFromDeviceAsync();

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "E"));
		Assert.True(Target(vm, "E").IsSelected);
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

	// ── Aress — the sibling board. Same protocol and geometry, its own catalog. ──────────────

	[Fact]
	public void Aress_LoadsItsOwnCatalogAndTheSharedCanvasSize()
	{
		var (vm, _) = NewVm();
		Load(vm, AressCard());

		Assert.True(vm.HasInteractiveKeyboardOverlay);
		Assert.Equal(AressKeyCatalog.Keys.Count, vm.PhysicalKeys.Count);
		Assert.Equal(67, vm.PhysicalKeys.Count);
		Assert.Equal(SmartInfinite65Geometry.CanvasWidth, vm.OverlayCanvasWidth);
		Assert.All(vm.PhysicalKeys, k => Assert.True(k.PixelWidth > 0 && k.PixelHeight > 0));
	}

	// The three navigation slots are the whole difference between the two boards, so the loaded
	// layout must reflect Aress's labels and not Cyrex's.
	[Fact]
	public void Aress_UsesItsOwnNavigationColumnNotCyrexs()
	{
		var (vm, _) = NewVm();
		Load(vm, AressCard());

		Assert.Equal(14, Key(vm, "Insert").MatrixIndex);
		Assert.Equal(44, Key(vm, "Delete").MatrixIndex);
		Assert.Equal(59, Key(vm, "PageDown").MatrixIndex);
		Assert.DoesNotContain(vm.PhysicalKeys, k => k.DevKeyName == "End");
	}

	[Fact]
	public void Cyrex_UsesItsOwnNavigationColumnNotAress()
	{
		var (vm, _) = NewVm();
		Load(vm, CyrexCard());

		Assert.Equal(14, Key(vm, "Delete").MatrixIndex);
		Assert.Equal(44, Key(vm, "PageDown").MatrixIndex);
		Assert.Equal(59, Key(vm, "End").MatrixIndex);
		Assert.DoesNotContain(vm.PhysicalKeys, k => k.DevKeyName == "Insert");
	}

	[Fact]
	public async Task Aress_UsesTheSlotKeyedReadAndWritesTheCatalogSlot()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = LiveMatrix();
		Load(vm, AressCard());

		await vm.RefreshFromDeviceAsync();
		Assert.Equal(1, hid.ReadKeyMatrixCalls);
		Assert.Equal(0, hid.ProbeKeyMatrixCalls);

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "E"));
		await vm.SelectAssignmentTargetCommand.ExecuteAsync(Target(vm, "A"));

		var write = Assert.Single(hid.Writes);
		Assert.Equal(18, write.MatrixIndex);
		Assert.Equal(0x04, write.Code2);
	}

	[Fact]
	public async Task Aress_FailedWriteLeavesTheHighlightWhereTheDeviceIs()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = LiveMatrix();
		Load(vm, AressCard());
		await vm.RefreshFromDeviceAsync();

		hid.WriteSucceeds = false;

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "E"));
		await vm.SelectAssignmentTargetCommand.ExecuteAsync(Target(vm, "A"));

		Assert.True(Target(vm, "E").IsSelected);
		Assert.False(Target(vm, "A").IsSelected);
	}

	// ── A failed read must not masquerade as the device's real state. ───────────────────────────

	// The fallback path shows factory defaults. That is the right fallback, but it is a guess: for a
	// user who has already remapped a key it is simply wrong, so the failure has to reach the view.
	[Fact]
	public async Task FailedMatrixRead_IsSurfacedInsteadOfSilentlyShowingFactoryDefaults()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = null; // the device did not answer
		Load(vm, CyrexCard());

		await vm.RefreshFromDeviceAsync();

		// SelfHeal drains the retry loop: the banner raises only once the whole budget failed.
		// (That it does NOT raise for a transient blip is DeviceAnsweringDuringTheQuickRetries'.)
		await SelfHeal(vm);
		Assert.True(vm.KeyAssignmentReadFailed);
	}

	[Fact]
	public async Task SuccessfulMatrixRead_LeavesNoFailureFlag()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = LiveMatrix();
		Load(vm, CyrexCard());

		await vm.RefreshFromDeviceAsync();

		Assert.False(vm.KeyAssignmentReadFailed);
	}

	// A stale warning on a healthy keyboard is its own kind of lie.
	[Fact]
	public async Task SwitchingDevices_ClearsAStaleFailureFlag()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = null;
		Load(vm, CyrexCard());
		await vm.RefreshFromDeviceAsync();
		await SelfHeal(vm);
		Assert.True(vm.KeyAssignmentReadFailed);

		hid.Matrix = LiveMatrix();
		Load(vm, AressCard());

		Assert.False(vm.KeyAssignmentReadFailed);
	}

	// ── Switching devices while a read is in flight (PR #72 review, M1). ────────────────────────

	// Cyrex slot 14 is Delete, Aress slot 14 is Insert. The highlight matches on (type, mask, usage),
	// not on slot, so a stale Cyrex result landing on an Aress page highlights DEL for the INS key —
	// wrong, plausible-looking, and confined to the three slots that distinguish the two boards.
	[Fact]
	public async Task StaleReadFromAPreviousDevice_IsDiscardedInsteadOfLandingOnTheCurrentOne()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = LiveMatrix();

		// Read A starts on Cyrex and is held open.
		var gateA = new TaskCompletionSource();
		hid.ReadGate = gateA;
		Load(vm, CyrexCard());
		var readA = vm.RefreshFromDeviceAsync();

		// The user switches to Aress before A comes back; read B completes normally.
		hid.ReadGate = null;
		hid.Matrix = AressLiveMatrix();
		Load(vm, AressCard());
		await vm.RefreshFromDeviceAsync();

		// Slot 14 is the divergent one, so select that physical key: Insert on Aress.
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Insert"));
		Assert.True(Target(vm, "Insert").IsSelected);

		// Now A lands. Left unguarded it would overwrite _assignments with Cyrex's table, whose slot 14
		// is Delete, and re-sync the highlight onto the DEL row.
		gateA.SetResult();
		await readA;

		Assert.True(Target(vm, "Insert").IsSelected);
		Assert.False(Target(vm, "Delete").IsSelected);
	}

	// The nastier direction: a stale *failure* raising the banner over a device that read fine.
	[Fact]
	public async Task StaleFailedRead_DoesNotRaiseTheBannerOverAHealthyDevice()
	{
		var (vm, hid) = NewVm();

		hid.Matrix = null; // read A will fail
		var gateA = new TaskCompletionSource();
		hid.ReadGate = gateA;
		Load(vm, CyrexCard());
		var readA = vm.RefreshFromDeviceAsync();

		hid.ReadGate = null;
		hid.Matrix = AressLiveMatrix();
		Load(vm, AressCard());
		await vm.RefreshFromDeviceAsync();
		Assert.False(vm.KeyAssignmentReadFailed);

		gateA.SetResult();
		await readA;

		Assert.False(vm.KeyAssignmentReadFailed);
	}

	// The reverse ordering, which is just as wrong: a stale success clearing a banner that belongs to
	// the device actually on screen.
	[Fact]
	public async Task StaleSuccessfulRead_DoesNotClearTheBannerOfTheCurrentDevice()
	{
		var (vm, hid) = NewVm();

		hid.Matrix = LiveMatrix(); // read A will succeed
		var gateA = new TaskCompletionSource();
		hid.ReadGate = gateA;
		Load(vm, CyrexCard());
		var readA = vm.RefreshFromDeviceAsync();

		hid.ReadGate = null;
		hid.Matrix = null; // read B fails
		Load(vm, AressCard());
		await vm.RefreshFromDeviceAsync();
		await SelfHeal(vm); // read B's whole retry budget fails too, raising the banner
		Assert.True(vm.KeyAssignmentReadFailed);

		gateA.SetResult();
		await readA;

		Assert.True(vm.KeyAssignmentReadFailed);
	}

	// Promised in the Round 2 journal: the pair of flag tests never covered a device that recovers
	// without a device switch, which is the realistic recovery path.
	[Fact]
	public async Task ARetryAfterAFailedRead_ClearsTheBannerWithoutSwitchingDevices()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = null;
		Load(vm, CyrexCard());

		await vm.RefreshFromDeviceAsync();
		await SelfHeal(vm);
		Assert.True(vm.KeyAssignmentReadFailed);

		// Slot 18 is E in the factory catalog; report it as A so the highlight can only be right if the
		// retry's data actually reached _assignments. Asserting it still reads E would pass on the
		// factory fallback too, and prove nothing.
		var recovered = LiveMatrix();
		recovered[18] = (0x10, 0x00, 0x04); // A
		hid.Matrix = recovered;
		await vm.RefreshFromDeviceAsync();

		Assert.False(vm.KeyAssignmentReadFailed);

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "E"));
		Assert.True(Target(vm, "A").IsSelected);
		Assert.False(Target(vm, "E").IsSelected);
	}

	// ── The self-heal loop (QA 2026-08-19: a wireless board sleeping through the page-open read). ──

	[Fact]
	public async Task DeviceAnsweringDuringTheQuickRetries_NeverRaisesTheBanner()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = null;
		Load(vm, CyrexCard());

		// The link comes back before the first retry fires — the sleep/wake blip. Slot 18 reports A
		// instead of its factory E, so the highlight below proves the retry's data actually landed.
		var recovered = LiveMatrix();
		recovered[18] = (0x10, 0x00, 0x04); // A
		vm.SlotReadRetryDelay = _ => { hid.Matrix = recovered; return Task.CompletedTask; };

		await vm.RefreshFromDeviceAsync();
		await SelfHeal(vm);

		Assert.False(vm.KeyAssignmentReadFailed);
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "E"));
		Assert.True(Target(vm, "A").IsSelected);
	}

	[Fact]
	public async Task DeviceComingBackBehindTheBanner_ClearsItWithoutAPageReopen()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = null;
		Load(vm, CyrexCard());

		var bannerWasUp = false;
		var delays = 0;
		vm.SlotReadRetryDelay = _ =>
		{
			// Stay dead through the two quick retries; come back on the first slow-phase wait,
			// by which point the banner must already be up.
			if (++delays == 3)
			{
				bannerWasUp = vm.KeyAssignmentReadFailed;
				hid.Matrix = LiveMatrix();
			}
			return Task.CompletedTask;
		};

		await vm.RefreshFromDeviceAsync();
		await SelfHeal(vm);

		Assert.True(bannerWasUp);
		Assert.False(vm.KeyAssignmentReadFailed);
	}

	// PR #112 m3: leaving Device Details must retire the loop immediately — no further device
	// reads on a page nobody is looking at, and no banner raised for it.
	[Fact]
	public async Task LeavingThePage_CancelsTheSelfHealLoop()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = null;
		Load(vm, CyrexCard());

		// The user exits during the first retry wait; the exit hook cancels the loop.
		vm.SlotReadRetryDelay = _ => { vm.ClearDeviceForExit(); return Task.CompletedTask; };

		await vm.RefreshFromDeviceAsync();
		await SelfHeal(vm);

		Assert.Equal(1, hid.ReadKeyMatrixCalls); // the page-open read only — zero retries
		Assert.False(vm.KeyAssignmentReadFailed);
	}

	// B1 (Round 4): the generation guard had been applied to the read path only, so a write completing
	// after a device switch recorded its result against the keyboard now on screen.
	[Fact]
	public async Task WriteLandingAfterADeviceSwitch_IsNotRecordedAgainstTheNewDevice()
	{
		var (vm, hid) = NewVm();
		hid.Matrix = LiveMatrix();
		Load(vm, CyrexCard());
		await vm.RefreshFromDeviceAsync();

		// Remap E -> A on Cyrex, holding the write open.
		var writeGate = new TaskCompletionSource();
		hid.WriteGate = writeGate;
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "E"));
		var write = vm.SelectAssignmentTargetCommand.ExecuteAsync(Target(vm, "A"));

		// The user moves to Aress before it returns.
		hid.WriteGate = null;
		hid.Matrix = AressLiveMatrix();
		Load(vm, AressCard());
		await vm.RefreshFromDeviceAsync();

		writeGate.SetResult();
		await write;

		// Aress slot 18 is E and was never written. Unguarded, the Cyrex write records A here.
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "E"));
		Assert.True(Target(vm, "E").IsSelected);
		Assert.False(Target(vm, "A").IsSelected);
	}

}
