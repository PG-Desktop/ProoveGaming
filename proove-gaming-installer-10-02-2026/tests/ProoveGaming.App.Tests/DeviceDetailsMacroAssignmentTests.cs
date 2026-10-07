using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// Covers the Macros-page macro→button assignment flow on DeviceDetailsViewModel: assign / unassign
// / per-button reset, the hotspot macro badges, and the macro-list selection tracking the selected
// button. Mirrors DeviceDetailsPresetsViewModelTests' in-memory-storage setup.
public class DeviceDetailsMacroAssignmentTests
{
	private const int Vid = 0x04D9;
	private const int Pid = 0xA23F;
	private const string DeviceKey = "";

	private sealed class FakeOwnerProvider(string kind, string id) : ICurrentOwnerProvider
	{
		private readonly SettingsOwner _owner = new(kind, id);
		public SettingsOwner? GetCurrent() => _owner;
	}

	// Captures the last button-binding set written; everything else is a no-op returning defaults.
	private sealed class FakeUsbHidDeviceService : IUsbHidDeviceService
	{
		public IReadOnlyList<(MouseButton button, MouseButtonAction action, MacroDefinition? macro)>? LastBindings { get; private set; }

		public Task<bool> SetButtonBindingsAsync(int vendorId, int productId, string deviceKey, IReadOnlyList<(MouseButton button, MouseButtonAction action, MacroDefinition? macro)> bindings)
		{
			LastBindings = bindings;
			return Task.FromResult(true);
		}

		public int ApplyConfigCallCount { get; private set; }

		public Task<bool> ApplyDeviceConfigurationAsync(
			int vendorId, int productId, string deviceKey,
			(byte r, byte g, byte b, byte effect, byte brightness, byte speed)? led,
			IReadOnlyList<(MouseButton button, MouseButtonAction action, MacroDefinition? macro)>? bindings,
			(byte debounce, byte liftOff)? mouseSettings,
			IReadOnlyList<(int dpi, byte r, byte g, byte b)>? dpiStages,
			int? activeStage,
			int? reportRateHz)
		{
			ApplyConfigCallCount++;
			if (bindings != null) LastBindings = bindings;
			return Task.FromResult(true);
		}

		// Keyboard-only path (Raster macro library upload); these tests exercise mouse
		// bindings, so it just records the call.
		public IReadOnlyList<(int Index, MacroRepeatMode Mode, IReadOnlyList<MacroEvent> Events, int RepeatCount)>? LastMacroLibrary { get; private set; }

		public Task<bool> SetMacrosAsync(
			int vendorId, int productId, string deviceKey,
			IReadOnlyList<(int Index, MacroRepeatMode Mode, IReadOnlyList<MacroEvent> Events, int RepeatCount)> entries)
		{
			LastMacroLibrary = entries;
			return Task.FromResult(true);
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
		public bool TryGetLastDpiStage(int vendorId, int productId, string deviceKey, out int stageIndex) { stageIndex = 0; return false; }
		public bool TryGetLastReportRate(int vendorId, int productId, string deviceKey, out int hz) { hz = 0; return false; }
		public Task<bool> SetKeyMappingAsync(int vendorId, int productId, byte matrixIndex, byte type, byte code1, byte code2) => Task.FromResult(true);
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

	private static SingleCardViewModel FakeMouse() => new()
	{
		Name = "Test Mouse",
		DeviceTypeLabel = "Wired Mouse",
		VendorId = Vid,
		ProductId = Pid,
		SettingsVendorId = Vid,
		SettingsProductId = Pid,
		DeviceKey = DeviceKey,
		SettingsDeviceKey = DeviceKey,
		SupportsButtonRemap = true,
		SupportsMacros = true,
	};

	private static DevicePartHotspotViewModel Hotspot(string id, string label) => new()
	{
		Id = id,
		Label = label,
		ButtonPoint = new Point(0, 0),
		TargetPoint = new Point(0, 0),
	};

	// Sets the device with a couple of assignable hotspots and enters the Macros section.
	private static DeviceDetailsViewModel WithMouseInMacrosSection(DeviceDetailsViewModel vm, params (string id, string label)[] hotspots)
	{
		var device = FakeMouse();
		vm.Device = device;
		foreach (var (id, label) in hotspots)
			device.PartHotspots.Add(Hotspot(id, label));
		vm.ActiveSection = NavSection.Macros;
		return vm;
	}

	private static MacroViewModel AddMacro(DeviceDetailsViewModel vm, int id, string name)
	{
		var macro = new MacroViewModel { Id = id, Name = name };
		vm.Macros.Add(macro);
		return macro;
	}

	// ── Assign ──────────────────────────────────────────────────────────────

	[Fact]
	public void Assign_BindsMacroToSelectedButton_PersistsWithoutWriting()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			WithMouseInMacrosSection(vm, ("right-click", "Right Click"));
			var macro = AddMacro(vm, 101, "M1");
			var hotspot = vm.PartHotspots.Single();

			vm.SelectHotspotCommand.Execute(hotspot);
			vm.AssignMacroToSelectedButtonCommand.Execute(macro);

			var bindings = store.GetButtonBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
			var right = bindings.Single(b => b.Button == (int)MouseButton.ButtonRight);
			Assert.Equal((int)MouseButtonAction.Macro, right.Action);
			Assert.Equal(101, right.MacroId);

			// The assignment stages into the store; the device is written on Save Preset, not here.
			Assert.Null(hid.LastBindings);

			Assert.True(macro.IsSelected);
			Assert.Equal("M1", hotspot.AssignedMacroName);
			Assert.True(hotspot.ShowAssignedMacro);
		}
	}

	[Fact]
	public void Assign_WithNoHotspotSelected_IsNoOp()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			WithMouseInMacrosSection(vm, ("right-click", "Right Click"));
			var macro = AddMacro(vm, 101, "M1");

			vm.AssignMacroToSelectedButtonCommand.Execute(macro);

			Assert.Empty(store.GetButtonBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
			Assert.Null(hid.LastBindings);
			Assert.False(macro.IsSelected);
		}
	}

	// ── Macro inspection (radio → highlight bound buttons) ──────────────────
	// The Macros list's inspection radio lights the hotspots of the buttons the macro is
	// bound to (selected-look accents), without becoming the selected hotspot and without
	// touching IsSelected (the bound-to-selected-button mirror).
	[Fact]
	public void InspectMacro_HighlightsTheBoundHotspot()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			WithMouseInMacrosSection(vm, ("right-click", "Right Click"), ("side-forward", "Forward"));
			var m1 = AddMacro(vm, 101, "M1");
			var m2 = AddMacro(vm, 102, "M2");
			var right = vm.PartHotspots.Single(h => h.Id == "right-click");
			var forward = vm.PartHotspots.Single(h => h.Id == "side-forward");

			vm.SelectHotspotCommand.Execute(right);
			vm.AssignMacroToSelectedButtonCommand.Execute(m1);

			vm.InspectMacroCommand.Execute(m1);

			Assert.True(m1.IsInspected);
			Assert.True(right.IsMacroBindingHighlighted);
			Assert.False(forward.IsMacroBindingHighlighted);
			Assert.Same(right, vm.SelectedHotspot);   // inspection moves no hotspot selection

			// The highlight is derived state: unassigning the button drops its accent while the
			// macro stays inspected — no re-click of the row (PR #104 m6).
			vm.UnassignHotspotCommand.Execute(right);
			Assert.True(m1.IsInspected);
			Assert.False(right.IsMacroBindingHighlighted);

			// Re-binding brings it back, and moving the inspection to an unbound macro clears it.
			vm.AssignMacroToSelectedButtonCommand.Execute(m1);
			Assert.True(right.IsMacroBindingHighlighted);
			vm.InspectMacroCommand.Execute(m2);
			Assert.False(m1.IsInspected);
			Assert.False(right.IsMacroBindingHighlighted);
		}
	}

	// ── Unassign / reset ────────────────────────────────────────────────────

	[Fact]
	public void Unassign_RevertsButtonToDefault()
	{
		var (vm, store, _, owner) = NewVm();
		using (store)
		{
			WithMouseInMacrosSection(vm, ("right-click", "Right Click"));
			var macro = AddMacro(vm, 101, "M1");
			var hotspot = vm.PartHotspots.Single();
			vm.SelectHotspotCommand.Execute(hotspot);
			vm.AssignMacroToSelectedButtonCommand.Execute(macro);

			vm.UnassignHotspotCommand.Execute(hotspot);

			var right = store.GetButtonBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey)
				.Single(b => b.Button == (int)MouseButton.ButtonRight);
			Assert.Equal((int)MouseButtonAction.RightClick, right.Action);
			Assert.Null(right.MacroId);
			Assert.False(hotspot.ShowAssignedMacro);
			Assert.False(macro.IsSelected);
		}
	}

	[Fact]
	public void ResetSelectedButton_RevertsOnlyThatButton()
	{
		var (vm, store, _, owner) = NewVm();
		using (store)
		{
			WithMouseInMacrosSection(vm, ("right-click", "Right Click"), ("scroll-click", "Scroll Click"));
			var macroA = AddMacro(vm, 101, "A");
			var macroB = AddMacro(vm, 102, "B");
			var right = vm.PartHotspots.Single(h => h.Id == "right-click");
			var wheel = vm.PartHotspots.Single(h => h.Id == "scroll-click");

			vm.SelectHotspotCommand.Execute(right);
			vm.AssignMacroToSelectedButtonCommand.Execute(macroA);
			vm.SelectHotspotCommand.Execute(wheel);
			vm.AssignMacroToSelectedButtonCommand.Execute(macroB);

			// Reset only the right button.
			vm.SelectHotspotCommand.Execute(right);
			vm.ResetSelectedButtonCommand.Execute(null);

			var bindings = store.GetButtonBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
			var rightBinding = bindings.Single(b => b.Button == (int)MouseButton.ButtonRight);
			var wheelBinding = bindings.Single(b => b.Button == (int)MouseButton.ButtonWheel);
			Assert.Equal((int)MouseButtonAction.RightClick, rightBinding.Action);
			Assert.Equal((int)MouseButtonAction.Macro, wheelBinding.Action);
			Assert.Equal(102, wheelBinding.MacroId);
		}
	}

	// ── Macro-list selection tracks the selected hotspot ────────────────────

	[Fact]
	public void MacroListSelection_FollowsSelectedHotspot()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			WithMouseInMacrosSection(vm, ("right-click", "Right Click"), ("scroll-click", "Scroll Click"), ("side-back", "Back"));
			var macroA = AddMacro(vm, 101, "A");
			var macroB = AddMacro(vm, 102, "B");
			var right = vm.PartHotspots.Single(h => h.Id == "right-click");
			var wheel = vm.PartHotspots.Single(h => h.Id == "scroll-click");
			var back = vm.PartHotspots.Single(h => h.Id == "side-back");

			vm.SelectHotspotCommand.Execute(right);
			vm.AssignMacroToSelectedButtonCommand.Execute(macroA);
			vm.SelectHotspotCommand.Execute(wheel);
			vm.AssignMacroToSelectedButtonCommand.Execute(macroB);

			vm.SelectHotspotCommand.Execute(right);
			Assert.True(macroA.IsSelected);
			Assert.False(macroB.IsSelected);

			vm.SelectHotspotCommand.Execute(wheel);
			Assert.False(macroA.IsSelected);
			Assert.True(macroB.IsSelected);

			vm.SelectHotspotCommand.Execute(back);
			Assert.False(macroA.IsSelected);
			Assert.False(macroB.IsSelected);
		}
	}

	// ── Badge gating by section ─────────────────────────────────────────────

	[Fact]
	public void HotspotBadge_ShownOnlyInMacrosSection()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			WithMouseInMacrosSection(vm, ("right-click", "Right Click"));
			var macro = AddMacro(vm, 101, "M1");
			var hotspot = vm.PartHotspots.Single();
			vm.SelectHotspotCommand.Execute(hotspot);
			vm.AssignMacroToSelectedButtonCommand.Execute(macro);
			Assert.True(hotspot.ShowAssignedMacro);

			vm.ActiveSection = NavSection.Buttons;
			Assert.False(hotspot.ShowAssignedMacro);
			Assert.Equal("M1", hotspot.AssignedMacroName); // name retained; only the badge hides

			vm.ActiveSection = NavSection.Macros;
			Assert.True(hotspot.ShowAssignedMacro);
		}
	}

	// ── Delete a bound macro ────────────────────────────────────────────────

	[Fact]
	public void DeleteBoundMacro_RevertsButtonAndClearsBadge()
	{
		var (vm, store, _, owner) = NewVm();
		using (store)
		{
			WithMouseInMacrosSection(vm, ("right-click", "Right Click"));
			var macro = AddMacro(vm, 101, "M1");
			var hotspot = vm.PartHotspots.Single();
			vm.SelectHotspotCommand.Execute(hotspot);
			vm.AssignMacroToSelectedButtonCommand.Execute(macro);

			vm.RequestDeleteMacroCommand.Execute(macro);
			vm.ConfirmDeleteMacroCommand.Execute(null);

			Assert.DoesNotContain(macro, vm.Macros);
			Assert.False(hotspot.ShowAssignedMacro);
			var right = store.GetButtonBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey)
				.Single(b => b.Button == (int)MouseButton.ButtonRight);
			Assert.Equal((int)MouseButtonAction.RightClick, right.Action);
		}
	}

	// ── Reset enablement ─────────────────────────────────────────────────────

	[Fact]
	public void CanResetSelectedButton_MacrosSection_OnlyWhenMacroAssigned()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			WithMouseInMacrosSection(vm, ("right-click", "Right Click"));
			var macro = AddMacro(vm, 101, "M1");
			var hotspot = vm.PartHotspots.Single();

			Assert.False(vm.CanResetSelectedButton); // no selection

			vm.SelectHotspotCommand.Execute(hotspot);
			Assert.False(vm.CanResetSelectedButton); // at default

			vm.AssignMacroToSelectedButtonCommand.Execute(macro);
			Assert.True(vm.CanResetSelectedButton); // macro assigned

			vm.ResetSelectedButtonCommand.Execute(null);
			Assert.False(vm.CanResetSelectedButton); // back to default
		}
	}

	[Fact]
	public void CanResetSelectedButton_ButtonsSection_WhenAnyNonDefault()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			var device = FakeMouse();
			vm.Device = device;
			device.PartHotspots.Add(Hotspot("right-click", "Right Click"));
			vm.ActiveSection = NavSection.Buttons;
			var hotspot = vm.PartHotspots.Single();

			vm.SelectHotspotCommand.Execute(hotspot);
			Assert.False(vm.CanResetSelectedButton); // at default

			// Assign a non-default, non-macro action via the Buttons catalog.
			var option = vm.ButtonActionGroups
				.SelectMany(g => g.Options)
				.First(o => o.Action == MouseButtonAction.VolumeUp);
			vm.SelectButtonActionCommand.Execute(option);

			Assert.True(vm.CanResetSelectedButton); // non-default assignment → resettable

			vm.ResetSelectedButtonCommand.Execute(null);
			Assert.False(vm.CanResetSelectedButton); // reverted to default
		}
	}

	// ── Reset enablement notification for the shared Macros header ──────────

	// The Macros header binds CanResetSelectedMacroTarget, which dispatches to
	// CanResetSelectedButton for a mouse. RefreshMacroListSelection raised only the latter, so the
	// header never heard about an assignment and its Reset link stayed greyed out until the user
	// picked another button and came back. The computed value was always correct — what was missing
	// was the notification, which is what this asserts.
	[Fact]
	public void AssigningAMacro_RaisesResetEnablementForTheSharedHeader()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			WithMouseInMacrosSection(vm, ("right-click", "Right Click"));
			var macro = AddMacro(vm, 101, "M1");
			var hotspot = vm.PartHotspots.Single();
			vm.SelectHotspotCommand.Execute(hotspot);

			var raised = new List<string>();
			vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

			vm.AssignMacroToSelectedButtonCommand.Execute(macro);

			Assert.True(vm.CanResetSelectedMacroTarget);
			Assert.Contains(nameof(DeviceDetailsViewModel.CanResetSelectedMacroTarget), raised);
		}
	}

	[Fact]
	public void ResettingAButton_RaisesResetEnablementForTheSharedHeader()
	{
		var (vm, store, _, _) = NewVm();
		using (store)
		{
			WithMouseInMacrosSection(vm, ("right-click", "Right Click"));
			var macro = AddMacro(vm, 101, "M1");
			var hotspot = vm.PartHotspots.Single();
			vm.SelectHotspotCommand.Execute(hotspot);
			vm.AssignMacroToSelectedButtonCommand.Execute(macro);

			var raised = new List<string>();
			vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

			vm.ResetSelectedButtonCommand.Execute(null);

			// The inverse of the assign case: nothing is left to reset, and the header has to hear
			// about it or the link stays active.
			Assert.False(vm.CanResetSelectedMacroTarget);
			Assert.Contains(nameof(DeviceDetailsViewModel.CanResetSelectedMacroTarget), raised);
		}
	}

	// ── Non-mouse ───────────────────────────────────────────────────────────

	[Fact]
	public void Assign_OnKeyboard_IsSafeNoOp()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			vm.Device = new SingleCardViewModel
			{
				Name = "Test Keyboard",
				DeviceTypeLabel = "Wired Keyboard",
				VendorId = Vid,
				ProductId = Pid,
				SettingsVendorId = Vid,
				SettingsProductId = Pid,
				DeviceKey = DeviceKey,
				SettingsDeviceKey = DeviceKey,
				SupportsMacros = true,
			};
			vm.ActiveSection = NavSection.Macros;
			var macro = AddMacro(vm, 101, "M1");

			vm.AssignMacroToSelectedButtonCommand.Execute(macro);

			Assert.Empty(vm.PartHotspots);
			Assert.Null(hid.LastBindings);
			Assert.False(macro.IsSelected);
		}
	}

	// ── Production open chain ───────────────────────────────────────────────
	// Every test above hand-stamps the VM; none crosses SetDeviceAsync (default-preset seed +
	// selection, hotspot build from the catalog config) or the debounced device apply. The #104
	// regression this file's plan fixes lived past that boundary — the VM was healthy while the
	// page no longer invoked it — so this pins the whole open→create→assign→push chain.
	[Fact]
	public async Task OpenChain_CreateAndAssign_PushesMacroInlineToDevice()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var card = FakeMouse();
			card.Hotspots = [new DeviceHotspotInfo("right-click", 785, 200, 679, 245, "Right", false)];
			await vm.SetDeviceAsync(card);

			// The debounced apply is gated on a selected preset; without it every staged edit
			// silently skips the device write.
			Assert.NotNull(vm.SelectedPreset);

			vm.ActiveSection = NavSection.Macros;
			vm.SelectHotspotCommand.Execute(vm.PartHotspots.Single());

			// The tester's clicks, through the real dialog flow rather than Macros.Add.
			vm.NewMacroName = "E2E";
			vm.NewMacroRepetitions = "1";
			vm.SaveCreateMacroCommand.Execute(null);
			var macro = Assert.Single(vm.Macros);
			Assert.True(macro.Id > 0);

			var callsBeforeAssign = hid.ApplyConfigCallCount;
			vm.AssignMacroToSelectedButtonCommand.Execute(macro);

			var rows = store.GetButtonBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
			var right = rows.Single(b => b.Button == (int)MouseButton.ButtonRight);
			Assert.Equal((int)MouseButtonAction.Macro, right.Action);
			Assert.Equal(macro.Id, right.MacroId);

			// The 250 ms debounce must land a device write carrying the macro inline.
			var deadline = DateTime.UtcNow.AddSeconds(5);
			while (hid.ApplyConfigCallCount == callsBeforeAssign && DateTime.UtcNow < deadline)
				await Task.Delay(50);

			Assert.True(hid.ApplyConfigCallCount > callsBeforeAssign,
				"the assign never reached the device — the debounced apply was skipped");
			var pushed = hid.LastBindings!.Single(b => b.button == MouseButton.ButtonRight);
			Assert.Equal(MouseButtonAction.Macro, pushed.action);
			Assert.NotNull(pushed.macro);
		}
	}
}
