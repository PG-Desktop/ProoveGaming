using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// Covers the per-key assignment dialog (Key Assignment Dialog): clicking a key on the Key
// Assignments / Macros keyboard opens a modal whose KEYS tab is a visual remap-target picker.
// The picker must route through the same SelectAssignmentTarget path as the old inline list —
// these tests pin the open/close semantics, the picker's construction, and that routing.
public class DeviceDetailsKeyAssignmentDialogTests
{
	// Raster's real ids — HasInteractiveKeyboardOverlay gates on the catalog, not on a flag.
	private const int Vid = 14000;
	private const int Pid = 12385;
	private const string DeviceKey = "";

	private sealed class FakeOwnerProvider(string kind, string id) : ICurrentOwnerProvider
	{
		private readonly SettingsOwner _owner = new(kind, id);
		public SettingsOwner? GetCurrent() => _owner;
	}

	private sealed class FakeUsbHidDeviceService : IUsbHidDeviceService
	{
		public List<(byte MatrixIndex, byte Type, byte Code1, byte Code2)> KeyWrites { get; } = [];

		// Settable so a test can stand in for a firmware that refuses the write — the commit's
		// "successfully remapped" toast must not appear then.
		public bool SetKeyMappingResult { get; set; } = true;

		// When set, SetKeyMappingAsync returns this instead of completing synchronously — the
		// stand-in for the ~420 ms read-modify-write window on the immediate-apply family,
		// inside which a second pick must be ignored (PR #94 M2).
		public TaskCompletionSource<bool>? PendingKeyWrite { get; set; }

		public Task<bool> SetMacrosAsync(
			int vendorId, int productId, string deviceKey,
			IReadOnlyList<(int Index, MacroRepeatMode Mode, IReadOnlyList<MacroEvent> Events, int RepeatCount)> entries)
			=> Task.FromResult(true);

		public Task<bool> SetKeyMappingAsync(int vendorId, int productId, byte matrixIndex, byte type, byte code1, byte code2)
		{
			KeyWrites.Add((matrixIndex, type, code1, code2));
			return PendingKeyWrite?.Task ?? Task.FromResult(SetKeyMappingResult);
		}

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
		// Settable so a test can stand in for a keyboard whose firmware already holds an
		// override — the probe is how the App learns what a remapped key now emits.
		public (IReadOnlyDictionary<byte, byte> ScancodeToSlot, IReadOnlyDictionary<byte, byte> UserOverrides)? ProbeResult { get; set; }

		public Task<(IReadOnlyDictionary<byte, byte> ScancodeToSlot, IReadOnlyDictionary<byte, byte> UserOverrides)?> ProbeKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
			=> Task.FromResult(ProbeResult);
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

	private static (DeviceDetailsViewModel Vm, LocalStorageService Store, FakeUsbHidDeviceService Hid, string Owner) NewVm()
	{
		var owner = System.Guid.NewGuid().ToString("D");
		var store = new LocalStorageService("Data Source=:memory:");
		var hid = new FakeUsbHidDeviceService();
		var vm = new DeviceDetailsViewModel(
			usbHidDeviceService: hid,
			localStorage: store,
			settings: null,
			ownerProvider: new FakeOwnerProvider(LocalStorageService.OwnerKindGuest, owner));
		return (vm, store, hid, owner);
	}

	// SetDevice is the production entry point — it is what loads the key layout.
	private static async Task<DeviceDetailsViewModel> WithRaster(DeviceDetailsViewModel vm, NavSection section)
	{
		await vm.SetDeviceAsync(new DoubleCardViewModel
		{
			Name = "Raster",
			DeviceTypeLabel = "Wireless Keyboard",
			VendorId = Vid,
			ProductId = Pid,
			SettingsVendorId = Vid,
			SettingsProductId = Pid,
			DeviceKey = DeviceKey,
			SettingsDeviceKey = DeviceKey,
			SupportsMacros = true,
		});
		vm.ActiveSection = section;
		return vm;
	}

	// Cyrex — the Smart Infinite family: immediate-apply writes, and the only family with the
	// documented consumer record (CON_TYPE 0x20) behind the dialog's SYSTEM tab.
	private const int CyrexVid = 10473;
	private const int CyrexPid = 12914;

	private static async Task<DeviceDetailsViewModel> WithCyrex(DeviceDetailsViewModel vm, NavSection section)
	{
		await vm.SetDeviceAsync(new DoubleCardViewModel
		{
			Name = "Cyrex",
			DeviceTypeLabel = "Wired Keyboard",
			VendorId = CyrexVid,
			ProductId = CyrexPid,
			SettingsVendorId = CyrexVid,
			SettingsProductId = CyrexPid,
			DeviceKey = DeviceKey,
			SettingsDeviceKey = DeviceKey,
			SupportsMacros = true,
		});
		vm.ActiveSection = section;
		return vm;
	}

	private static KeyAssignmentOptionViewModel Key(DeviceDetailsViewModel vm, string devKeyName) =>
		vm.PhysicalKeys.First(k => k.DevKeyName == devKeyName);

	private static KeyAssignmentOptionViewModel PickerKey(DeviceDetailsViewModel vm, string devKeyName) =>
		vm.RemapTargetKeys.First(k => k.DevKeyName == devKeyName);

	// ── Open / close ──────────────────────────────────────────────────────────

	[Fact]
	public async Task SelectKey_InKeyAssignmentsSection_OpensDialogOnKeysTab()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Buttons);

			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());

			Assert.True(vm.IsKeyAssignmentDialogOpen);
			Assert.True(vm.IsKeyDialogKeysTabActive);
			Assert.False(vm.IsKeyDialogMacrosTabActive);
		}
	}

	[Fact]
	public async Task SelectKey_InMacrosSection_OpensDialog()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Macros);

			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());

			Assert.True(vm.IsKeyAssignmentDialogOpen);
		}
	}

	[Fact]
	public async Task SelectKey_ReopensOnKeysTab_AfterATabSwitch()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Buttons);

			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			vm.SelectKeyDialogMacrosTabCommand.Execute(null);
			Assert.True(vm.IsKeyDialogMacrosTabActive);
			vm.CloseKeyAssignmentDialogCommand.Execute(null);

			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());

			Assert.True(vm.IsKeyAssignmentDialogOpen);
			Assert.True(vm.IsKeyDialogKeysTabActive);
		}
	}

	[Fact]
	public async Task SwitchToMacrosTab_RaisesTheKeysHintVisibility_SoTheHintsDoNotOverlap()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Buttons);
			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			Assert.True(vm.IsKeyDialogRemapKeysHintVisible);

			var raised = new List<string?>();
			vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
			vm.SelectKeyDialogMacrosTabCommand.Execute(null);

			Assert.False(vm.IsKeyDialogRemapKeysHintVisible);
			Assert.Contains(nameof(vm.IsKeyDialogRemapKeysHintVisible), raised);
			Assert.Contains(nameof(vm.IsKeyDialogPickKeysHintVisible), raised);
		}
	}

	[Fact]
	public async Task SelectKey_InActuationPerKeyMode_DoesNotOpenDialog()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Actuation);
			vm.ActuationPerKeyMode = true;

			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());

			Assert.False(vm.IsKeyAssignmentDialogOpen);
		}
	}

	[Fact]
	public async Task Close_LeavesThePhysicalKeySelected()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Buttons);
			var key = vm.PhysicalKeys.First();

			vm.SelectPhysicalKeyCommand.Execute(key);
			vm.CloseKeyAssignmentDialogCommand.Execute(null);

			Assert.False(vm.IsKeyAssignmentDialogOpen);
			Assert.Same(key, vm.SelectedPhysicalKey);
			Assert.True(key.IsSelected);
		}
	}

	// ── Empty-space deselect (click on the panel outside any key) ─────────────

	[Fact]
	public async Task Deselect_ClearsTheSelectionAndEveryHighlight()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Macros);
			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			vm.CloseKeyAssignmentDialogCommand.Execute(null);

			vm.DeselectPhysicalKeyCommand.Execute(null);

			Assert.Null(vm.SelectedPhysicalKey);
			Assert.DoesNotContain(vm.PhysicalKeys, k => k.IsSelected);
			Assert.DoesNotContain(vm.AssignmentTargets, t => t.IsSelected);
			Assert.DoesNotContain(vm.RemapTargetKeys, k => k.IsSelected);
		}
	}

	[Fact]
	public async Task Deselect_WithNothingSelected_IsANoOp()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Macros);

			vm.DeselectPhysicalKeyCommand.Execute(null);

			Assert.Null(vm.SelectedPhysicalKey);
			Assert.DoesNotContain(vm.PhysicalKeys, k => k.IsSelected);
		}
	}

	[Fact]
	public async Task Deselect_InActuationPerKeyMode_ClearsTheActuationSelection()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Actuation);
			vm.ActuationPerKeyMode = true;
			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			Assert.True(vm.HasActuationSelection);

			vm.DeselectPhysicalKeyCommand.Execute(null);

			Assert.False(vm.HasActuationSelection);
			Assert.DoesNotContain(vm.PhysicalKeys, k => k.IsSelected);
		}
	}

	// PG-494: every section is entered with no key selected — a key picked on Key
	// Assignments must not arrive on Macros still highlighted.
	[Fact]
	public async Task SectionSwitch_ClearsThePhysicalKeySelection()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Buttons);
			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			vm.CloseKeyAssignmentDialogCommand.Execute(null);
			Assert.NotNull(vm.SelectedPhysicalKey);

			vm.ActiveSection = NavSection.Macros;

			Assert.Null(vm.SelectedPhysicalKey);
			Assert.DoesNotContain(vm.PhysicalKeys, k => k.IsSelected);
		}
	}

	[Fact]
	public async Task DeviceReload_ClosesTheDialog()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Buttons);
			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			Assert.True(vm.IsKeyAssignmentDialogOpen);

			// A reload clears the selection the dialog was opened for (device change or preset
			// switch both come through here).
			vm.LoadKeyLayoutForDevice();

			Assert.False(vm.IsKeyAssignmentDialogOpen);
		}
	}

	// ── The KEYS tab picker ───────────────────────────────────────────────────

	[Fact]
	public async Task RemapTargetKeys_MirrorPhysicalKeysGeometry_AndCarryThePickCommand()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Buttons);

			Assert.Equal(vm.PhysicalKeys.Count, vm.RemapTargetKeys.Count);
			Assert.Equal(
				vm.PhysicalKeys.Select(k => k.DevKeyName),
				vm.RemapTargetKeys.Select(k => k.DevKeyName));

			var physical = vm.PhysicalKeys.First();
			var picker = vm.RemapTargetKeys.First();
			Assert.Equal(physical.CanvasX, picker.CanvasX);
			Assert.Equal(physical.CanvasY, picker.CanvasY);
			Assert.Equal(physical.PixelWidth, picker.PixelWidth);
			Assert.Equal(physical.PixelHeight, picker.PixelHeight);
			Assert.Same(vm.PickRemapTargetCommand, picker.SelectCommand);
		}
	}

	[Fact]
	public async Task PickRemapTarget_StagesTheSameRowAsTheInlineTargetList()
	{
		var (vm, store, _, owner) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Buttons);
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));

			vm.PickRemapTargetCommand.Execute(PickerKey(vm, "F1"));

			var binding = Assert.Single(store.GetKeyBindings(
				LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
			Assert.Equal(Key(vm, "Escape").ScanCode, binding.PhysScancode);
			Assert.Equal(Key(vm, "F1").ScanCode, binding.TargetCode);
		}
	}

	[Fact]
	public async Task PickRemapTarget_HighlightsThePickedKey_AndResetRestoresSelf()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Buttons);
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));

			vm.PickRemapTargetCommand.Execute(PickerKey(vm, "F1"));
			Assert.Same(PickerKey(vm, "F1"), vm.RemapTargetKeys.Single(k => k.IsSelected));

			// Picking the key's own code clears the override; the picker follows the target list
			// back to the identity mapping.
			vm.PickRemapTargetCommand.Execute(PickerKey(vm, "Escape"));
			Assert.Same(PickerKey(vm, "Escape"), vm.RemapTargetKeys.Single(k => k.IsSelected));
		}
	}

	[Fact]
	public async Task MacroBinding_FromDialog_ClosesDialog_AndRaisesUndoToast()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Macros);
			var macro = new MacroViewModel { Id = 101, Name = "M1" };
			vm.Macros.Add(macro);

			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			Assert.True(vm.IsKeyAssignmentDialogOpen);

			// The MACROS tab's macro row click — a pick commits like a KEYS pick: dialog closes,
			// undo toast comes up (PG-381: it used to keep the dialog open with no feedback).
			vm.AssignMacroToSelectedButtonCommand.Execute(macro);

			Assert.False(vm.IsKeyAssignmentDialogOpen);
			Assert.True(vm.IsRemapToastVisible);

			// On the staging family the target list falls back to the key's own row for a
			// macro-bound key (macro rows are excluded from its highlight branch), and the picker
			// mirrors the list — the same semantics the inline list had.
			Assert.Same(PickerKey(vm, "Escape"), vm.RemapTargetKeys.Single(k => k.IsSelected));

			// Reset still works after the close and leaves the picker on the key's own code.
			vm.ResetSelectedMacroTargetCommand.Execute(null);
			Assert.Same(PickerKey(vm, "Escape"), vm.RemapTargetKeys.Single(k => k.IsSelected));
		}
	}

	[Fact]
	public async Task MacroBinding_FromDialog_OnCyrex_WritesDeviceAndClosesDialog()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			// The immediate-write family the bug was reported on: the macro upload and the key
			// slot write both happen before the dialog may close.
			await WithCyrex(vm, NavSection.Macros);
			var macro = new MacroViewModel { Id = 101, Name = "M1" };
			vm.Macros.Add(macro);

			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			Assert.True(vm.IsKeyAssignmentDialogOpen);

			vm.AssignMacroToSelectedButtonCommand.Execute(macro);

			Assert.False(vm.IsKeyAssignmentDialogOpen);
			Assert.True(vm.IsRemapToastVisible);
			Assert.NotEmpty(hid.KeyWrites);
		}
	}

	[Fact]
	public async Task MacroBinding_FromDialog_RefusedWrite_ReopensDialogWithoutToast()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithCyrex(vm, NavSection.Macros);
			hid.SetKeyMappingResult = false;
			var macro = new MacroViewModel { Id = 101, Name = "M1" };
			vm.Macros.Add(macro);

			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));

			// In the app the radio checks itself before the command runs (TwoWay binding);
			// simulate that here so the final assert genuinely proves the refused-path
			// re-sync unchecks it rather than passing on a value nothing ever set.
			macro.IsSelected = true;
			vm.AssignMacroToSelectedButtonCommand.Execute(macro);

			// The close is optimistic, but a refused write brings the dialog BACK — on the
			// Key Assignments section it is the only visible refusal signal (PR #101 M3) —
			// with no "successfully remapped" toast, and the macro radio re-syncs to unbound.
			Assert.True(vm.IsKeyAssignmentDialogOpen);
			Assert.False(vm.IsRemapToastVisible);
			Assert.DoesNotContain(vm.Macros, m => m.IsSelected);
		}
	}

	[Fact]
	public async Task KeyRemapTooltip_NamesTheStagedTarget_OnRaster()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Buttons);
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			vm.PickRemapTargetCommand.Execute(PickerKey(vm, "F1"));

			// The hover tooltip names what the key now resolves to; an untouched key stays null
			// so its tip remains the key name alone.
			Assert.Equal(Key(vm, "F1").KeyLabel, vm.GetKeyRemapTooltip(Key(vm, "Escape")));
			Assert.Null(vm.GetKeyRemapTooltip(Key(vm, "F1")));
		}
	}

	[Fact]
	public async Task KeyRemapTooltip_NamesTheLiveTargetOrMacro_OnCyrex()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithCyrex(vm, NavSection.Macros);
			var macro = new MacroViewModel { Id = 101, Name = "M1" };
			vm.Macros.Add(macro);

			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			vm.AssignMacroToSelectedButtonCommand.Execute(macro);
			Assert.Equal("M1", vm.GetKeyRemapTooltip(Key(vm, "Escape")));

			// Remap the same key to a plain key: the tip follows the device record. (Cyrex is a
			// 65% board — no F-row, so the target is a letter key.)
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			vm.PickRemapTargetCommand.Execute(PickerKey(vm, "T"));
			Assert.Equal(Key(vm, "T").KeyLabel, vm.GetKeyRemapTooltip(Key(vm, "Escape")));
		}
	}

	[Fact]
	public async Task KeyPick_OverMacroBoundKey_ClearsTheMacroBinding()
	{
		var (vm, store, _, owner) = NewVm();
		using (store)
		{
			await WithCyrex(vm, NavSection.Macros);
			var macro = new MacroViewModel { Id = 101, Name = "M1" };
			vm.Macros.Add(macro);

			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			vm.AssignMacroToSelectedButtonCommand.Execute(macro);
			Assert.True(macro.IsSelected);

			// Remap the macro-bound key to its own record from the dialog's KEYS tab. The slot
			// is a key again: the store row goes and the Macros list radio unchecks — it used
			// to stay checked, reading as "the macro never went away" (PG-381).
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			vm.PickRemapTargetCommand.Execute(PickerKey(vm, "Escape"));

			Assert.False(vm.IsKeyAssignmentDialogOpen);
			Assert.DoesNotContain(vm.Macros, m => m.IsSelected);
			Assert.Empty(store.GetKeyMacroBindings(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey));
		}
	}

	[Fact]
	public async Task MacroBinding_FromDialog_MidWritePick_IsIgnored_AndKeepsDialogOpen()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithCyrex(vm, NavSection.Macros);
			var macro = new MacroViewModel { Id = 101, Name = "M1" };
			vm.Macros.Add(macro);

			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));

			// Hold the first pick's device write open, then pick from the reopened dialog while
			// it is still in flight: the commit's gate ignores the second pick, so the dialog
			// must not close for it either.
			hid.PendingKeyWrite = new TaskCompletionSource<bool>();
			vm.AssignMacroToSelectedButtonCommand.Execute(macro);
			Assert.False(vm.IsKeyAssignmentDialogOpen);

			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			Assert.True(vm.IsKeyAssignmentDialogOpen);
			vm.AssignMacroToSelectedButtonCommand.Execute(macro);
			Assert.True(vm.IsKeyAssignmentDialogOpen);

			// The first pick's write lands: it may toast, but it must NOT slam shut the
			// dialog the user deliberately re-opened during the write (round 1 nit — the
			// commit only restores/keeps state it captured at its own entry).
			hid.PendingKeyWrite.SetResult(true);
			Assert.True(vm.IsKeyAssignmentDialogOpen);
		}
	}

	// ── Type-to-remap (physical keyboard capture) ─────────────────────────────

	[Fact]
	public async Task TypedKey_PicksTheMatchingTarget_AndStagesIt()
	{
		var (vm, store, _, owner) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Buttons);
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));

			// 0x3A = HID usage for F1 (what HidKeyCodeMap yields for Key.F1).
			Assert.True(vm.TryPickRemapTargetFromKeyboard(0x3A));

			var binding = Assert.Single(store.GetKeyBindings(
				LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
			Assert.Equal(Key(vm, "F1").ScanCode, binding.TargetCode);
			Assert.Same(PickerKey(vm, "F1"), vm.RemapTargetKeys.Single(k => k.IsSelected));

			// A typed pick commits like a clicked one: dialog closes, undo toast comes up.
			Assert.False(vm.IsKeyAssignmentDialogOpen);
			Assert.True(vm.IsRemapToastVisible);
		}
	}

	[Fact]
	public async Task TypedModifier_FallsBackToTheBitmaskRow_OnSmartInfinite()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			// Cyrex identifies modifiers by bitmask with usage 0x00 — the usage lookup misses
			// and the modifier fallback must resolve 0xE1 (Left Shift) to mask 0x02.
			await WithCyrex(vm, NavSection.Buttons);
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));

			Assert.True(vm.TryPickRemapTargetFromKeyboard(0xE1));

			var picked = vm.AssignmentTargets.Single(t => t.IsSelected);
			Assert.Equal("LeftShift", picked.DevKeyName);
			Assert.Equal((byte)0x02, picked.Bitmask);
		}
	}

	[Fact]
	public async Task TypedKey_IsIgnored_WhenDialogClosedOrOnAnotherTab()
	{
		var (vm, store, _, owner) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Buttons);

			// Closed (no key selected yet): nothing to remap.
			Assert.False(vm.TryPickRemapTargetFromKeyboard(0x3A));

			// MACROS tab: typing belongs to the search box, never to remapping.
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			vm.SelectKeyDialogMacrosTabCommand.Execute(null);
			Assert.False(vm.TryPickRemapTargetFromKeyboard(0x3A));

			Assert.Empty(store.GetKeyBindings(
				LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
		}
	}

	[Fact]
	public async Task TypedKey_WithNoMatchingTarget_IsNotConsumed()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Buttons);
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));

			// 0x53 (Num Lock) is a valid HID usage no Raster catalog row carries.
			Assert.False(vm.TryPickRemapTargetFromKeyboard(0x53));
		}
	}

	[Fact]
	public async Task TypedKey_MatchingTheCurrentRemap_RestoresTheKeysOwnCode()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithCyrex(vm, NavSection.Buttons);
			var d1 = Key(vm, "D1");
			vm.SelectPhysicalKeyCommand.Execute(d1);

			// 0x1A = HID usage for W: remap "1" to W the normal way.
			Assert.True(vm.TryPickRemapTargetFromKeyboard(0x1A));
			Assert.Equal((d1.MatrixIndex, (byte)0x10, (byte)0x00, (byte)0x1A), hid.KeyWrites[^1]);

			// The remap lives in the firmware, so the physical "1" now EMITS W — pressing it to
			// put "1" back arrives as W again (QA 2026-08-20). The pick must read that as the key
			// itself and write its own record, not re-assert W.
			vm.SelectPhysicalKeyCommand.Execute(d1);
			Assert.True(vm.TryPickRemapTargetFromKeyboard(0x1A));
			Assert.Equal((d1.MatrixIndex, (byte)0x10, (byte)0x00, d1.ScanCode), hid.KeyWrites[^1]);
			Assert.Same(PickerKey(vm, "D1"), vm.RemapTargetKeys.Single(k => k.IsSelected));

			// A self-pick is a restore, not a remap — "1 remapped to 1" read as a bug (PR #117 m2).
			Assert.Equal(
				string.Format(
					ProoveGaming.App.LocalizationManager.GetString("KeyDialog_ResetToast_Format"),
					d1.KeyLabel),
				vm.RemapToastText);
		}
	}

	[Fact]
	public async Task TypedKey_MatchingTheDeviceOverride_RestoresTheKeysOwnCode_OnRaster()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Buttons);
			var esc = Key(vm, "Escape");

			// The firmware already holds Escape → F1 (an earlier applied preset), so the physical
			// Escape key emits 0x3A. The probe is how the App knows that.
			hid.ProbeResult = (
				new Dictionary<byte, byte> { [esc.ScanCode] = esc.MatrixIndex },
				new Dictionary<byte, byte> { [esc.MatrixIndex] = 0x3A });
			await vm.ProbeAndUpdateMatrixMapAsync();

			vm.SelectPhysicalKeyCommand.Execute(esc);
			Assert.True(vm.TryPickRemapTargetFromKeyboard(0x3A));

			// The press is the overridden Escape itself: the pick stages the key's own code —
			// a reset (no binding row) — rather than re-picking F1.
			Assert.Empty(store.GetKeyBindings(
				LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
			Assert.Same(PickerKey(vm, "Escape"), vm.RemapTargetKeys.Single(k => k.IsSelected));
		}
	}

	[Fact]
	public async Task TypedKey_MatchingOnlyTheStagedOverride_StillPicksThatKey()
	{
		var (vm, store, _, owner) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Buttons);
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));

			// Stage Escape → F1. The staged override is NOT on the board yet, so the physical
			// Escape still emits its own code — an incoming 0x3A can only be the real F1 key,
			// and it must keep picking F1, not fold back into a reset.
			Assert.True(vm.TryPickRemapTargetFromKeyboard(0x3A));
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			Assert.True(vm.TryPickRemapTargetFromKeyboard(0x3A));

			var binding = Assert.Single(store.GetKeyBindings(
				LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
			Assert.Equal(Key(vm, "F1").ScanCode, binding.TargetCode);
			Assert.Same(PickerKey(vm, "F1"), vm.RemapTargetKeys.Single(k => k.IsSelected));
		}
	}

	// ── Pick commit: close + undo toast ───────────────────────────────────────

	[Fact]
	public async Task Pick_ClosesTheDialog_AndShowsTheUndoToast()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Buttons);
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));

			vm.PickRemapTargetCommand.Execute(PickerKey(vm, "F1"));

			Assert.False(vm.IsKeyAssignmentDialogOpen);
			Assert.True(vm.IsRemapToastVisible);
			Assert.Equal(
				string.Format(
					ProoveGaming.App.LocalizationManager.GetString("KeyDialog_RemapToast_Format"),
					Key(vm, "Escape").KeyLabel, Key(vm, "F1").KeyLabel),
				vm.RemapToastText);
		}
	}

	[Fact]
	public async Task ToastUndo_RevertsThePick_AndHidesTheToast()
	{
		var (vm, store, _, owner) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Buttons);
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			vm.PickRemapTargetCommand.Execute(PickerKey(vm, "F1"));
			Assert.Single(store.GetKeyBindings(
				LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));

			vm.UndoRemapCommand.Execute(null);

			Assert.False(vm.IsRemapToastVisible);
			Assert.Empty(store.GetKeyBindings(
				LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
			Assert.Same(PickerKey(vm, "Escape"), vm.RemapTargetKeys.Single(k => k.IsSelected));
		}
	}

	// Test-ENDING clean asserts must poll, not read the flag synchronously: after an undo the
	// sync compare may say "clean" while staged writes are still queued on the storage executor,
	// in which case StagePresetDirtyOnly deliberately holds the flag true until the queued
	// authoritative recompute settles it (CI run 32533280112 lost that race under Release load).
	// Same doctrine as DeviceDetailsPresetApplyTests.WaitUntilPresetClean.
	private static void WaitUntilPresetClean(DeviceDetailsViewModel vm, int timeoutMs = 5000)
	{
		var deadline = Environment.TickCount64 + timeoutMs;
		while (vm.HasUnsavedPresetChanges && Environment.TickCount64 < deadline)
			Thread.Sleep(50);
	}

	// QA finding (PR #100): pick a remap, then UNDO — nothing has net-changed anywhere, so the
	// preset must read clean again and Save Preset must be disabled. One test per family: the
	// staging path (Raster) and the immediate-write path (Cyrex — same family as Rampage, where
	// the report came from).
	[Fact]
	public async Task ToastUndo_AfterPickFromFactoryRecord_ClearsPresetDirty_Staging()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Buttons);
			Assert.False(vm.HasUnsavedPresetChanges);

			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			vm.PickRemapTargetCommand.Execute(PickerKey(vm, "F1"));
			Assert.True(vm.HasUnsavedPresetChanges);

			vm.UndoRemapCommand.Execute(null);

			WaitUntilPresetClean(vm);
			Assert.False(vm.HasUnsavedPresetChanges);
		}
	}

	[Fact]
	public async Task ToastUndo_AfterPickFromFactoryRecord_ClearsPresetDirty_ImmediateWrite()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithCyrex(vm, NavSection.Buttons);
			Assert.False(vm.HasUnsavedPresetChanges);

			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			vm.PickRemapTargetCommand.Execute(PickerKey(vm, "Q"));
			Assert.True(vm.HasUnsavedPresetChanges);

			vm.UndoRemapCommand.Execute(null);

			WaitUntilPresetClean(vm);
			Assert.False(vm.HasUnsavedPresetChanges);
		}
	}

	// The full shape of the QA report: an edit from an EARLIER open was never saved into the
	// preset, so the preset genuinely has unsaved changes — but the SelectedPreset setter cleared
	// the flag on reopen, masking it, and the first key edit's recompute "suddenly" revealed it,
	// which an Undo then could not clear (the divergence was never that key's). With the open-time
	// recompute the button is truthful from the start, and assign + undo lands back where it began.
	[Fact]
	public async Task ReopenWithUnsavedEdit_SavePresetTruthfulFromOpen_AndUndoReturnsToIt()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithCyrex(vm, NavSection.Buttons);
			Assert.False(vm.HasUnsavedPresetChanges);

			// A remap staged but never saved into the preset…
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			vm.PickRemapTargetCommand.Execute(PickerKey(vm, "Q"));
			Assert.True(vm.HasUnsavedPresetChanges);

			// …survives a Back + reopen as REAL divergence, so the flag must come back true.
			vm.ClearDeviceForExit();
			await WithCyrex(vm, NavSection.Buttons);
			Assert.True(vm.HasUnsavedPresetChanges);

			// A second key's assign + Undo nets out to nothing — the flag stays where the open
			// put it, instead of the undo "mysteriously" leaving Save Preset enabled.
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Tab"));
			vm.PickRemapTargetCommand.Execute(PickerKey(vm, "Q"));
			vm.UndoRemapCommand.Execute(null);
			Assert.True(vm.HasUnsavedPresetChanges);
		}
	}

	[Fact]
	public async Task ToastDismiss_HidesWithoutReverting()
	{
		var (vm, store, _, owner) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Buttons);
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			vm.PickRemapTargetCommand.Execute(PickerKey(vm, "F1"));

			vm.DismissRemapToastCommand.Execute(null);

			Assert.False(vm.IsRemapToastVisible);
			Assert.Single(store.GetKeyBindings(
				LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
		}
	}

	[Fact]
	public async Task SelectingAnotherKey_HidesTheToast_AndUndoBecomesInert()
	{
		var (vm, store, _, owner) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Buttons);
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			vm.PickRemapTargetCommand.Execute(PickerKey(vm, "F1"));
			Assert.True(vm.IsRemapToastVisible);

			// The undo window belongs to Escape's pick; moving the selection ends it.
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Q"));
			Assert.False(vm.IsRemapToastVisible);
			Assert.True(vm.IsKeyAssignmentDialogOpen);

			// A stale undo (e.g. a queued click) must not reset the newly selected key.
			vm.UndoRemapCommand.Execute(null);
			Assert.Single(store.GetKeyBindings(
				LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
		}
	}

	// ── Commit honesty (review r2-S1) ─────────────────────────────────────────

	[Fact]
	public async Task ReadOnlyTransport_NeverOpensTheDialog_AndAPickShowsNoToast()
	{
		var (vm, store, _, owner) = NewVm();
		using (store)
		{
			await vm.SetDeviceAsync(new DoubleCardViewModel
			{
				Name = "Raster",
				DeviceTypeLabel = "Wireless Keyboard",
				VendorId = Vid,
				ProductId = Pid,
				SettingsVendorId = Vid,
				SettingsProductId = Pid,
				DeviceKey = DeviceKey,
				SettingsDeviceKey = DeviceKey,
				SupportsMacros = true,
				SupportsConfiguration = false,
			});
			vm.ActiveSection = NavSection.Buttons;

			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			Assert.False(vm.IsKeyAssignmentDialogOpen);

			// Even a direct pick (nothing stops a queued command) stages nothing on a read-only
			// transport, so it must not claim success.
			vm.PickRemapTargetCommand.Execute(PickerKey(vm, "F1"));

			Assert.False(vm.IsRemapToastVisible);
			Assert.Empty(store.GetKeyBindings(
				LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
		}
	}

	[Fact]
	public async Task RefusedDeviceWrite_KeepsTheDialogOpen_WithNoToast()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithCyrex(vm, NavSection.Buttons);
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			hid.SetKeyMappingResult = false;

			// Q, not F1 — Cyrex is a 65% board with no function row in its catalog.
			vm.PickRemapTargetCommand.Execute(PickerKey(vm, "Q"));

			// The write was refused: no success claim, the dialog stays up for another try,
			// and the highlight is back on the key's own code rather than the failed pick.
			Assert.True(vm.IsKeyAssignmentDialogOpen);
			Assert.False(vm.IsRemapToastVisible);
			Assert.Same(PickerKey(vm, "Escape"), vm.RemapTargetKeys.Single(k => k.IsSelected));
		}
	}

	// ── Immediate-family persistence & preset revert (unsaved remap must not survive) ──

	[Fact]
	public async Task ImmediateFamilyPick_PersistsASlotKeyedRow_AndSelfPickClearsIt()
	{
		var (vm, store, _, owner) = NewVm();
		using (store)
		{
			await WithCyrex(vm, NavSection.Buttons);
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));

			vm.PickRemapTargetCommand.Execute(
				vm.SystemCommandTargets.First(t => t.DevKeyName == "Command:VolumeUp"));

			// Slot-keyed (Escape = slot 0 on Cyrex): the row is what Save Preset snapshots and
			// what a Discard restores — the immediate device write alone leaves no trace.
			var row = Assert.Single(store.GetKeyBindings(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey));
			Assert.Equal(Key(vm, "Escape").MatrixIndex, row.PhysScancode);
			Assert.Equal(((byte)0x20, (byte)0xE9, (byte)0x00),
				(row.TypeByte, row.Bitmask, row.TargetCode));

			// Picking the key's own record clears the override row again.
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			vm.PickRemapTargetCommand.Execute(PickerKey(vm, "Escape"));
			Assert.Empty(store.GetKeyBindings(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey));
		}
	}

	[Fact]
	public async Task PresetPush_WritesBackTheDefault_WhenTheOverrideRowIsGone()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			await WithCyrex(vm, NavSection.Buttons);
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			vm.PickRemapTargetCommand.Execute(
				vm.SystemCommandTargets.First(t => t.DevKeyName == "Command:VolumeUp"));
			var slot = Key(vm, "Escape").MatrixIndex;

			// A Discard restores key_bindings from the preset snapshot (here: no row) and
			// reloads the layout — which nulls the device-truth mirror.
			store.DeleteKeyBinding(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey, slot);
			vm.LoadKeyLayoutForDevice();
			hid.KeyWrites.Clear();

			await vm.ApplyKeyOverridesToDeviceAsync();

			// Even with no readable baseline (the fake's matrix read returns null), the push
			// knows this session put a non-default record on the slot and reverts it.
			var write = Assert.Single(hid.KeyWrites);
			Assert.Equal((slot, (byte)0x10, (byte)0x00, (byte)0x29),
				(write.MatrixIndex, write.Type, write.Code1, write.Code2));
		}
	}

	[Fact]
	public async Task PresetPush_ReassertsThePersistedOverride_AfterAReload()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithCyrex(vm, NavSection.Buttons);
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			vm.PickRemapTargetCommand.Execute(
				vm.SystemCommandTargets.First(t => t.DevKeyName == "Command:VolumeUp"));
			var slot = Key(vm, "Escape").MatrixIndex;

			// Preset switch / connect: layout reloads (mirror nulled), the row survives.
			vm.LoadKeyLayoutForDevice();
			hid.KeyWrites.Clear();

			await vm.ApplyKeyOverridesToDeviceAsync();

			var write = Assert.Single(hid.KeyWrites);
			Assert.Equal((slot, (byte)0x20, (byte)0xE9, (byte)0x00),
				(write.MatrixIndex, write.Type, write.Code1, write.Code2));
		}
	}

	// ── Title / gates ─────────────────────────────────────────────────────────

	[Fact]
	public async Task Title_ComesFromTheLocalizedFormatResource()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Buttons);
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));

			// Headless, GetString falls back to the key itself — asserting that proves the title
			// is built from the resource rather than a hardcoded " button" suffix.
			Assert.Equal(
				string.Format(
					ProoveGaming.App.LocalizationManager.GetString("KeyDialog_Title_Format"),
					Key(vm, "Escape").KeyLabel),
				vm.SelectedPhysicalKeyWithSuffix);
		}
	}

	// ── SYSTEM tab (Smart Infinite consumer commands) ─────────────────────────

	[Fact]
	public async Task SystemTab_IsGatedOff_OutsideTheSmartInfiniteFamily()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Buttons);

			Assert.False(vm.IsSystemCommandsSupported);
			Assert.Empty(vm.SystemCommandTargets);
			Assert.DoesNotContain(vm.AssignmentTargets, t => t.DevKeyName.StartsWith("Command:"));
		}
	}

	[Fact]
	public async Task SystemCommands_AreBuiltAsConsumerRecords_OnCyrex()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithCyrex(vm, NavSection.Buttons);

			Assert.True(vm.IsSystemCommandsSupported);
			Assert.NotEmpty(vm.SystemCommandTargets);
			Assert.All(vm.SystemCommandTargets, t => Assert.Equal((byte)0x20, t.TypeByte));

			// Usage split little-endian across code1/code2, per the vendor header's CON_*_L/_H:
			// Volume Up 0x00E9 and Browser Back (AC Back) 0x0224 cover both byte orders.
			var volumeUp = vm.SystemCommandTargets.First(t => t.DevKeyName == "Command:VolumeUp");
			Assert.Equal((byte)0xE9, volumeUp.Bitmask);
			Assert.Equal((byte)0x00, volumeUp.ScanCode);
			var back = vm.SystemCommandTargets.First(t => t.DevKeyName == "Command:BrowserBack");
			Assert.Equal((byte)0x24, back.Bitmask);
			Assert.Equal((byte)0x02, back.ScanCode);

			// The rows are real assignment targets — that is what wires selection sync and reset.
			Assert.All(vm.SystemCommandTargets, t => Assert.Contains(t, vm.AssignmentTargets));
		}
	}

	[Fact]
	public async Task PickingASystemCommand_WritesTheConsumerRecord_AndCommitsLikeAKeyPick()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithCyrex(vm, NavSection.Buttons);
			hid.KeyWrites.Clear();
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			var volumeUp = vm.SystemCommandTargets.First(t => t.DevKeyName == "Command:VolumeUp");

			vm.PickRemapTargetCommand.Execute(volumeUp);

			var write = Assert.Single(hid.KeyWrites);
			Assert.Equal(Key(vm, "Escape").MatrixIndex, write.MatrixIndex);
			Assert.Equal((byte)0x20, write.Type);
			Assert.Equal((byte)0xE9, write.Code1);
			Assert.Equal((byte)0x00, write.Code2);

			Assert.False(vm.IsKeyAssignmentDialogOpen);
			Assert.True(vm.IsRemapToastVisible);
			Assert.True(volumeUp.IsSelected);
		}
	}

	[Fact]
	public async Task ToastUndo_AfterACommandPick_RestoresTheKeysOwnRecord()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithCyrex(vm, NavSection.Buttons);
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			vm.PickRemapTargetCommand.Execute(
				vm.SystemCommandTargets.First(t => t.DevKeyName == "Command:VolumeUp"));
			hid.KeyWrites.Clear();

			vm.UndoRemapCommand.Execute(null);

			var write = Assert.Single(hid.KeyWrites);
			var escape = Key(vm, "Escape");
			Assert.Equal((escape.MatrixIndex, escape.TypeByte, escape.Bitmask, escape.ScanCode),
				(write.MatrixIndex, write.Type, write.Code1, write.Code2));
			Assert.False(vm.IsRemapToastVisible);
		}
	}

	// ── Undo restores the DISPLACED state, not the factory default (PR #94 M1) ────

	[Fact]
	public async Task ToastUndo_RestoresThePreviousStagedRemap_NotTheFactoryDefault()
	{
		var (vm, store, _, owner) = NewVm();
		using (store)
		{
			await WithRaster(vm, NavSection.Buttons);

			// A remap the user already made (and could have saved into the preset).
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			vm.PickRemapTargetCommand.Execute(PickerKey(vm, "F1"));

			// A second pick on the same key, immediately regretted.
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			vm.PickRemapTargetCommand.Execute(PickerKey(vm, "F2"));

			vm.UndoRemapCommand.Execute(null);

			// UNDO means "put back what was there": Escape → F1 survives, the F2 pick is
			// gone. A reset here would have destroyed the earlier remap (the M1 bug).
			var row = Assert.Single(store.GetKeyBindings(
				LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
			Assert.Equal(Key(vm, "F1").ScanCode, row.TargetCode);
			Assert.Same(PickerKey(vm, "F1"), vm.RemapTargetKeys.Single(k => k.IsSelected));
			Assert.False(vm.IsRemapToastVisible);
		}
	}

	[Fact]
	public async Task ToastUndo_RestoresThePreviousCommand_OnTheImmediateFamily()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			await WithCyrex(vm, NavSection.Buttons);

			// Escape already carries Volume Down (persisted slot-keyed row + device write).
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			vm.PickRemapTargetCommand.Execute(
				vm.SystemCommandTargets.First(t => t.DevKeyName == "Command:VolumeDown"));

			// The regretted pick: Volume Up on the same key.
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			vm.PickRemapTargetCommand.Execute(
				vm.SystemCommandTargets.First(t => t.DevKeyName == "Command:VolumeUp"));
			hid.KeyWrites.Clear();

			vm.UndoRemapCommand.Execute(null);

			// The device gets Volume Down back ({0x20, 0xEA, 0x00}), and the persisted row
			// again says Volume Down — not deleted, which is what the old reset did.
			var write = Assert.Single(hid.KeyWrites);
			Assert.Equal((Key(vm, "Escape").MatrixIndex, (byte)0x20, (byte)0xEA, (byte)0x00),
				(write.MatrixIndex, write.Type, write.Code1, write.Code2));
			var row = Assert.Single(store.GetKeyBindings(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey));
			Assert.Equal(((byte)0x20, (byte)0xEA, (byte)0x00),
				(row.TypeByte, row.Bitmask, row.TargetCode));
		}
	}

	// ── Second pick during an in-flight write is ignored (PR #94 M2) ──────────────

	[Fact]
	public async Task SecondPick_WhileAWriteIsInFlight_IsIgnored()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithCyrex(vm, NavSection.Buttons);
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));

			// Hold the first pick's device write open — the ~420 ms matrix RMW window.
			hid.PendingKeyWrite = new TaskCompletionSource<bool>();
			var firstPick = vm.PickRemapTargetCommand.ExecuteAsync(PickerKey(vm, "Q"));
			Assert.Single(hid.KeyWrites);

			// A clicked pick and a typed pick both land inside the window: neither may start
			// a second write — finish-order of two writes to one slot is not guaranteed, so
			// the device and the bookkeeping could end up recording different picks.
			vm.PickRemapTargetCommand.Execute(PickerKey(vm, "W"));
			Assert.False(vm.TryPickRemapTargetFromKeyboard(Key(vm, "W").ScanCode));
			Assert.Single(hid.KeyWrites);

			hid.PendingKeyWrite.SetResult(true);
			hid.PendingKeyWrite = null;
			await firstPick;

			// Only the first pick committed: its toast, its record.
			Assert.False(vm.IsKeyAssignmentDialogOpen);
			Assert.True(vm.IsRemapToastVisible);
			var write = Assert.Single(hid.KeyWrites);
			Assert.Equal(Key(vm, "Q").ScanCode, write.Code2);
		}
	}

	// ── Pick purpose (Extended Keys output picker, docs/plans/add-extended-key-mod-tap) ──
	// The same dialog opened as a key picker: KEYS only, a callback instead of a remap. These pin
	// the one property the remap path cannot afford to lose — a pick never stages or writes.

	[Fact]
	public async Task PickPurpose_OpensOnKeysWithoutMacrosOrSystemTabs_AndNamesTheRole()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithCyrex(vm, NavSection.ExtendedKeys);
			Assert.True(vm.IsSystemCommandsSupported);

			vm.OpenKeyDialogForPick("Hold", null, _ => { });

			Assert.True(vm.IsKeyAssignmentDialogOpen);
			Assert.True(vm.IsKeyDialogKeysTabActive);
			Assert.True(vm.IsKeyDialogPickPurpose);
			Assert.False(vm.IsKeyDialogRemapPurpose);
			Assert.False(vm.IsKeyDialogMacrosTabVisible);
			Assert.False(vm.IsKeyDialogSystemTabVisible);
			Assert.True(vm.IsKeyDialogPickKeysHintVisible);
			Assert.False(vm.IsKeyDialogRemapKeysHintVisible);
			Assert.False(string.IsNullOrEmpty(vm.KeyDialogTitle));
			Assert.NotEqual(vm.SelectedPhysicalKeyWithSuffix, vm.KeyDialogTitle);
		}
	}

	[Fact]
	public async Task PickPurpose_ClickDeliversTheTripleAndCloses_WithoutStagingOrWriting()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			await WithCyrex(vm, NavSection.ExtendedKeys);
			(byte TypeByte, byte Bitmask, byte ScanCode, string Label)? picked = null;

			vm.OpenKeyDialogForPick("Hold", null, p => picked = p);
			vm.PickRemapTargetCommand.Execute(PickerKey(vm, "LeftCtrl"));

			Assert.Equal(((byte)0x10, (byte)0x01, (byte)0x00, "CTRL (L)"), picked);
			Assert.False(vm.IsKeyAssignmentDialogOpen);
			Assert.False(vm.IsRemapToastVisible);
			Assert.Empty(hid.KeyWrites);
			Assert.Empty(store.GetKeyBindings(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey));
			Assert.DoesNotContain(vm.AssignmentTargets, t => t.IsSelected);
			// The dialog is back to its everyday self.
			Assert.True(vm.IsKeyDialogRemapPurpose);
			Assert.True(vm.IsKeyDialogMacrosTabVisible);
		}
	}

	[Fact]
	public async Task PickPurpose_TypedKeyDeliversThePick_WithTheModifierFallback()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithCyrex(vm, NavSection.ExtendedKeys);
			var picks = new List<(byte TypeByte, byte Bitmask, byte ScanCode, string Label)>();

			// No selected key on this section — the remap path would refuse; the pick path takes it.
			vm.OpenKeyDialogForPick("Tap", null, picks.Add);
			Assert.True(vm.TryPickRemapTargetFromKeyboard(0x29));
			Assert.False(vm.IsKeyAssignmentDialogOpen);

			vm.OpenKeyDialogForPick("Hold", null, picks.Add);
			Assert.True(vm.TryPickRemapTargetFromKeyboard(0xE0));   // Left Ctrl usage → bitmask row

			Assert.Equal(2, picks.Count);
			Assert.Equal(((byte)0x10, (byte)0x00, (byte)0x29, "ESC"), picks[0]);
			Assert.Equal(((byte)0x10, (byte)0x01, (byte)0x00, "CTRL (L)"), picks[1]);
			Assert.Empty(hid.KeyWrites);
			// A closed dialog consumes nothing, as before.
			Assert.False(vm.TryPickRemapTargetFromKeyboard(0x29));
		}
	}

	[Fact]
	public async Task PickPurpose_CloseWithoutPicking_DropsTheCallback_AndTheRemapFlowIsUnchanged()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithCyrex(vm, NavSection.ExtendedKeys);
			int calls = 0;

			vm.OpenKeyDialogForPick("Hold", null, _ => calls++);
			vm.CloseKeyAssignmentDialogCommand.Execute(null);
			Assert.Equal(0, calls);
			Assert.True(vm.IsKeyDialogRemapPurpose);

			// The next dialog is an ordinary remap dialog: Macros tab back, and a pick writes the board
			// instead of calling the stale callback.
			vm.ActiveSection = NavSection.Buttons;
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Escape"));
			Assert.True(vm.IsKeyAssignmentDialogOpen);
			Assert.True(vm.IsKeyDialogMacrosTabVisible);
			Assert.Equal(vm.SelectedPhysicalKeyWithSuffix, vm.KeyDialogTitle);

			vm.PickRemapTargetCommand.Execute(PickerKey(vm, "Q"));
			Assert.Equal(0, calls);
			Assert.NotEmpty(hid.KeyWrites);
		}
	}

	[Fact]
	public async Task PickPurpose_HighlightsTheCurrentOutputOnThePicker_AndClearsItOnClose()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			await WithCyrex(vm, NavSection.ExtendedKeys);

			vm.OpenKeyDialogForPick("Tap", (0x10, 0x00, 0x29), _ => { });
			Assert.Same(PickerKey(vm, "Escape"), vm.RemapTargetKeys.Single(k => k.IsSelected));

			vm.CloseKeyAssignmentDialogCommand.Execute(null);
			Assert.DoesNotContain(vm.RemapTargetKeys, k => k.IsSelected);
		}
	}
}
