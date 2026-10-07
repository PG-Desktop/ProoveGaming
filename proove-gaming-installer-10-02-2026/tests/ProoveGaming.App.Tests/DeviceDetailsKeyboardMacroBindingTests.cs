using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// Covers binding a library macro to a keyboard KEY (Raster), which is a different path from the
// mouse flow in DeviceDetailsMacroAssignmentTests: the macro library is uploaded as a unit and
// the key-matrix record only carries an index into it, so the App has to keep the key→macro
// mapping in SQLite and re-assert it whenever the library changes.
public class DeviceDetailsKeyboardMacroBindingTests
{
	// Raster's real ids — HasInteractiveKeyboardOverlay gates on the catalog, not on a flag.
	private const int Vid = 14000;
	private const int Pid = 12385;
	private const string DeviceKey = "";

	// The Raster-family key-matrix record for a macro: [0x60, library slot, play mode].
	// Hardware-verified on Raster; the Smart Infinite family uses 0x50/0x51 instead (see below).
	private const byte RasterMacroTypeByte = 0x60;
	private const byte PlayModeOnce = 0x00;
	private const byte PlayModeWhileHeld = 0x01;

	private sealed class FakeOwnerProvider(string kind, string id) : ICurrentOwnerProvider
	{
		private readonly SettingsOwner _owner = new(kind, id);
		public SettingsOwner? GetCurrent() => _owner;
	}

	// Records the macro-library uploads and key-matrix writes, and can fail the upload so the
	// "library never made it, so do not bind" path is observable.
	private sealed class FakeUsbHidDeviceService : IUsbHidDeviceService
	{
		public bool SetMacrosResult { get; set; } = true;

		public List<IReadOnlyList<(int Index, MacroRepeatMode Mode, IReadOnlyList<MacroEvent> Events, int RepeatCount)>> MacroUploads { get; } = [];
		public List<(byte MatrixIndex, byte Type, byte Code1, byte Code2)> KeyWrites { get; } = [];

		public Task<bool> SetMacrosAsync(
			int vendorId, int productId, string deviceKey,
			IReadOnlyList<(int Index, MacroRepeatMode Mode, IReadOnlyList<MacroEvent> Events, int RepeatCount)> entries)
		{
			MacroUploads.Add(entries);
			return Task.FromResult(SetMacrosResult);
		}

		/// <summary>Runs inside the key write, standing in for anything that happens while the
		/// device I/O is in flight — on Cyrex/Aress that window is ~420 ms of real HID.</summary>
		public System.Action? DuringKeyWrite { get; set; }

		public Task<bool> SetKeyMappingAsync(int vendorId, int productId, byte matrixIndex, byte type, byte code1, byte code2)
		{
			KeyWrites.Add((matrixIndex, type, code1, code2));
			DuringKeyWrite?.Invoke();
			return Task.FromResult(true);
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
		// Null by default (no probe result), so the catalog slots stand. Set it to make the probe
		// land and move a key to a different firmware slot than its catalog index — which is what
		// creates the two-candidate ambiguity key_macro_bindings rows can be written under.
		public IReadOnlyDictionary<byte, byte>? ProbedScancodeToSlot { get; set; }

		public Task<(IReadOnlyDictionary<byte, byte> ScancodeToSlot, IReadOnlyDictionary<byte, byte> UserOverrides)?> ProbeKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
			=> Task.FromResult<(IReadOnlyDictionary<byte, byte>, IReadOnlyDictionary<byte, byte>)?>(
				ProbedScancodeToSlot is null
					? null
					: (ProbedScancodeToSlot, new Dictionary<byte, byte>()));
		// Smart Infinite read path (Cyrex / Aress); Raster does not use it. Settable so a test can
		// stand in for what the firmware actually holds before a resync rewrites it.
		public Dictionary<byte, (byte Type, byte Code1, byte Code2)>? KeyMatrix { get; set; }

		public Task<IReadOnlyDictionary<byte, (byte Type, byte Code1, byte Code2)>?> ReadKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
			=> Task.FromResult<IReadOnlyDictionary<byte, (byte, byte, byte)>?>(KeyMatrix);

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

	// SetDevice is the production entry point — it is what loads the key layout. Assigning the
	// Device property alone leaves PhysicalKeys empty.
	//
	// SetDevice ends with a fire-and-forget `_ = RefreshFromDeviceAsync()` that probes the matrix
	// and resyncs the macro library, so it calls the fake during setup. Every fake method returns
	// an already-completed task, so that chain runs inline and is finished before SetDevice
	// returns — but rather than have each test reason about which setup calls to expect, the
	// recordings are cleared here so a test only ever asserts on the calls it makes itself.
	private static async Task<DeviceDetailsViewModel> WithRasterInMacrosSection(DeviceDetailsViewModel vm, FakeUsbHidDeviceService hid)
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
		vm.ActiveSection = NavSection.Macros;

		hid.MacroUploads.Clear();
		hid.KeyWrites.Clear();
		return vm;
	}

	// Cyrex's real ids. Same VM path, different keyboard family: Smart Infinite framing, no
	// factory-vs-override map, and a different macro TYPE byte — so every one of those has to be
	// exercised separately from Raster rather than assumed to follow.
	private const int CyrexVid = 10473;
	private const int CyrexPid = 12914;
	// This firmware's OWN KeyType enum (extracted from the vendor's Keyborder app, which drives
	// our Cyrex):
	//   MACRO_NO_COUNT   = 0x50  play with a stop mode; code2 = stop mode
	//   MACRO_WITH_COUNT = 0x51  play n times;          code2 = the count
	// 0x60 here is FUNCTION — binding a key to it is stored, verifies on read-back, and plays
	// nothing, which was exactly the broken state this family shipped in. The V3.1 workbook says
	// the opposite because it describes a different firmware line. Keep in sync with
	// SmartInfiniteProtocol.TypeMacroStopMode / TypeMacroPlayCount.
	private const byte SmartInfiniteMacroTypeByte = 0x50;
	private const byte SmartInfiniteMacroRepeatTypeByte = 0x51;

	private static async Task<DeviceDetailsViewModel> WithCyrexInMacrosSection(DeviceDetailsViewModel vm, FakeUsbHidDeviceService hid)
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
		vm.ActiveSection = NavSection.Macros;

		hid.MacroUploads.Clear();
		hid.KeyWrites.Clear();
		return vm;
	}

	// The third Smart Infinite board — a TKL, not a 65% — sharing the family's macro path.
	private const int RampageVid = 10473;
	private const int RampagePid = 12888;

	private static async Task<DeviceDetailsViewModel> WithRampageInMacrosSection(DeviceDetailsViewModel vm, FakeUsbHidDeviceService hid)
	{
		await vm.SetDeviceAsync(new DoubleCardViewModel
		{
			Name = "Rampage",
			DeviceTypeLabel = "Wired Keyboard",
			VendorId = RampageVid,
			ProductId = RampagePid,
			SettingsVendorId = RampageVid,
			SettingsProductId = RampagePid,
			DeviceKey = DeviceKey,
			SettingsDeviceKey = DeviceKey,
			SupportsMacros = true,
		});
		vm.ActiveSection = NavSection.Macros;

		hid.MacroUploads.Clear();
		hid.KeyWrites.Clear();
		return vm;
	}

	private static MacroViewModel AddMacro(DeviceDetailsViewModel vm, int id, string name, bool repeatUntilReleased = false)
	{
		var macro = new MacroViewModel { Id = id, Name = name, RepeatUntilReleased = repeatUntilReleased };
		vm.Macros.Add(macro);
		return macro;
	}

	private static List<KeyAssignmentOptionViewModel> MacroTargets(DeviceDetailsViewModel vm) =>
		vm.AssignmentTargets.Where(t => t.IsMacroTarget).ToList();

	// ── Slot ordering ───────────────────────────────────────────────────────
	// The single most load-bearing invariant here: a target's Bitmask is code1, which IS the
	// macro's index in the uploaded library. If the target list and the upload payload ever
	// order the library differently, keys silently play the wrong macro and nothing detects it.
	[Fact]
	public async Task MacroTargets_SlotIndex_MatchesUploadedLibraryOrder()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "First");
			AddMacro(vm, 102, "Second");
			AddMacro(vm, 103, "Third");

			var targets = MacroTargets(vm);
			Assert.Equal(3, targets.Count);
			Assert.Equal([0, 1, 2], targets.Select(t => (int)t.Bitmask));
			Assert.Equal(["First", "Second", "Third"], targets.Select(t => t.KeyLabel));

			// Select a key and assign. Raster/T-Racer stage the binding, so nothing reaches the
			// keyboard until the preset push — the resync below is that push's macro half.
			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			vm.SelectAssignmentTargetCommand.Execute(targets[1]);
			Assert.Empty(hid.MacroUploads);
			Assert.Empty(hid.KeyWrites);

			await vm.ResyncKeyMacroBindingsAsync();

			// Setup recordings were cleared, so this is the push's own upload.
			var uploaded = Assert.Single(hid.MacroUploads);
			Assert.Equal([0, 1, 2], uploaded.Select(e => e.Index));

			// The macro the user clicked (index 1) is the slot written to the key.
			var write = Assert.Single(hid.KeyWrites);
			Assert.Equal(RasterMacroTypeByte, write.Type);
			Assert.Equal(1, write.Code1);
		}
	}

	// ── Staging (Raster / T-Racer) ──────────────────────────────────────────
	// Macro bindings used to write to the keyboard inline, on their own path, while key remaps went
	// through staging — two models on one page, and the reason a reset key could go back to playing
	// its macro. They are staged alike now: selecting records the intent and writes nothing inline;
	// the device is written by the shared push (the debounced auto-apply, or Save Preset / switch).
	// This asserts only the inline half — that no write happens synchronously with the click.
	[Fact]
	public async Task SelectingAMacroTarget_StagesInsteadOfWriting()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());

			// Nothing on the wire yet — this is the whole point of the change.
			Assert.Empty(hid.KeyWrites);
			Assert.Empty(hid.MacroUploads);

			// But the choice is recorded, so the push has something to apply.
			Assert.Single(store.GetKeyMacroBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
		}
	}

	// One slot is either a macro or a key record, never both. Whichever is staged last must clear
	// the other's store, or the two pushes fight and the later one silently wins: resetting a
	// macro-bound key left its row behind, so the resync re-wrote the macro over the reset.
	[Fact]
	public async Task StagingAKeyTarget_ClearsAnyMacroBindingOnThatSlot()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			var key = vm.PhysicalKeys.First();
			vm.SelectPhysicalKeyCommand.Execute(key);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());
			Assert.Single(store.GetKeyMacroBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));

			// Reset: stage the key back to its own scancode.
			var self = vm.AssignmentTargets.First(t => !t.IsMacroTarget && t.DevKeyName == key.DevKeyName);
			vm.SelectAssignmentTargetCommand.Execute(self);

			Assert.Empty(store.GetKeyMacroBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
		}
	}

	// The Macros page's radio list resolved its selection from the mouse hotspot only, so on a
	// keyboard nothing was ever marked and the user could not see which macro a key played.
	[Fact]
	public async Task MacroList_ReflectsTheBindingOfTheSelectedKey()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");
			AddMacro(vm, 102, "M2");

			var key = vm.PhysicalKeys.First();
			vm.SelectPhysicalKeyCommand.Execute(key);
			Assert.All(vm.Macros, m => Assert.False(m.IsSelected));

			// Bind the second macro; only its row may light up.
			var second = MacroTargets(vm).Single(t => t.MacroId == 102);
			vm.SelectAssignmentTargetCommand.Execute(second);

			Assert.False(vm.Macros.Single(m => m.Id == 101).IsSelected);
			Assert.True(vm.Macros.Single(m => m.Id == 102).IsSelected);

			// Reset the key: the row must clear again.
			var self = vm.AssignmentTargets.First(t => !t.IsMacroTarget && t.DevKeyName == key.DevKeyName);
			vm.SelectAssignmentTargetCommand.Execute(self);

			Assert.All(vm.Macros, m => Assert.False(m.IsSelected));
		}
	}

	// Removing a macro from a key: the Reset link is the affordance, and its enablement is
	// CanResetSelectedMacroTarget. That looked only at staged key overrides and the device probe,
	// neither of which knows about a macro binding — so the link stayed greyed out and a macro
	// could be assigned or replaced but never removed.
	[Fact]
	public async Task ResetIsOfferedForAKeyThatOnlyHasAMacroBound()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			var key = vm.PhysicalKeys.First();
			vm.SelectPhysicalKeyCommand.Execute(key);
			Assert.False(vm.CanResetSelectedMacroTarget);   // nothing bound yet

			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());

			// Bound but not yet pushed to the device — Reset must still be offered.
			Assert.True(vm.IsSelectedMacroTargetResettable);
			Assert.True(vm.CanResetSelectedMacroTarget);

			vm.ResetSelectedMacroTargetCommand.Execute(null);

			Assert.Empty(store.GetKeyMacroBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
			Assert.False(vm.CanResetSelectedMacroTarget);
		}
	}

	// The unassign gate (PR #81 review M1), today carried by the hover tip's ✕, the dialog's
	// macro-row ✕ and the header Reset link: an ungated unassign pressed inside the ~0.5 s
	// window while the immediate-write families are still finishing an assignment races the
	// in-flight write, and the resuming assignment silently re-binds the macro the user just
	// watched disappear. The gate is CanResetSelectedMacroTarget: it must read FALSE while the
	// assignment's key write is still in flight (no SQLite row, no _assignments update yet)
	// and TRUE once the binding is actually recorded.
	[Fact]
	public async Task UnassignGate_StaysClosedWhileTheAssignmentWriteIsInFlight()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			hid.KeyMatrix = new() { [0] = (0x10, 0x00, 0x29) };
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
				new MacroRecord { Name = "M1", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });
			await WithCyrexInMacrosSection(vm, hid);
			var macro = Assert.Single(vm.Macros);

			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());

			bool? gateDuringWrite = null;
			hid.DuringKeyWrite = () => gateDuringWrite = vm.CanResetSelectedMacroTarget;

			macro.AssignCommand!.Execute(macro);

			Assert.False(gateDuringWrite);                   // ✕ disabled inside the race window
			Assert.True(vm.CanResetSelectedMacroTarget);     // and enabled once the binding is real
			Assert.Single(store.GetKeyMacroBindings(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey));
		}
	}

	// Round 3 M1: gating on "is a binding recorded" alone left RE-binding open — while macro B's
	// write is in flight, the OLD binding A is still in SQLite, so a stored-state gate read true
	// and the ✕ on B's freshly-checked row was live inside the write window. The in-flight flag
	// must close the gate regardless of what the stores hold.
	[Fact]
	public async Task UnassignGate_StaysClosedWhileReplacingAnExistingBinding()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			hid.KeyMatrix = new() { [0] = (0x10, 0x00, 0x29) };
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
				new MacroRecord { Name = "A", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
				new MacroRecord { Name = "B", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });
			await WithCyrexInMacrosSection(vm, hid);
			var a = vm.Macros.Single(m => m.Name == "A");
			var b = vm.Macros.Single(m => m.Name == "B");

			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			a.AssignCommand!.Execute(a);
			Assert.True(vm.CanResetSelectedMacroTarget);     // A is bound, gate open

			bool? gateDuringRebind = null;
			hid.DuringKeyWrite = () => gateDuringRebind = vm.CanResetSelectedMacroTarget;

			b.AssignCommand!.Execute(b);

			Assert.False(gateDuringRebind);                  // closed even though A was still stored
			Assert.True(vm.CanResetSelectedMacroTarget);     // reopen once B is recorded
			var row = Assert.Single(store.GetKeyMacroBindings(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey));
			Assert.Equal(b.Id, row.MacroId);
			Assert.True(b.IsSelected);
			Assert.False(a.IsSelected);
		}
	}

	// The dialog's macro row binds its unassign ✕ visibility to MacroViewModel.IsSelected
	// (bound-to-this-key), so both reset paths must clear that mirror immediately. The staged
	// path (Raster/T-Racer) did this via StageAssignmentTarget; the immediate path (Smart
	// Infinite) did not, leaving the mirror checked — and the dialog's ✕ visible — for a
	// binding that was already gone, until the next selection change.
	[Fact]
	public async Task Reset_ClearsTheMacroRowSelection_OnTheImmediateWritePath()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			hid.KeyMatrix = new() { [0] = (0x10, 0x00, 0x29) };
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
				new MacroRecord { Name = "M1", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });
			await WithCyrexInMacrosSection(vm, hid);
			var macro = Assert.Single(vm.Macros);

			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			// The dialog's assign path (AssignCommand routes through the same commit the MACROS
			// tab uses) — it ends with the radio refresh the IsSelected mirror relies on.
			macro.AssignCommand!.Execute(macro);
			Assert.True(macro.IsSelected);

			vm.ResetSelectedMacroTargetCommand.Execute(null);

			Assert.Empty(store.GetKeyMacroBindings(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey));
			Assert.False(vm.Macros.Single().IsSelected);
		}
	}

	// The resync writes the bindings that exist; it had no notion of a slot that USED to hold one.
	// So removing a binding — by Reset, or by a preset revert that deletes the row straight out of
	// SQLite — left the macro playing on the keyboard with nothing in the App claiming it. That is
	// the "Discard doesn't revert the macro" report: storage reverted, the device did not.
	[Fact]
	public async Task Resync_ClearsASlotWhoseBindingWasRemoved()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			var key = vm.PhysicalKeys.First();
			vm.SelectPhysicalKeyCommand.Execute(key);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());
			await vm.ResyncKeyMacroBindingsAsync();

			var bound = Assert.Single(hid.KeyWrites);
			Assert.Equal(RasterMacroTypeByte, bound.Type);
			hid.KeyWrites.Clear();

			// Remove the binding the way a preset revert does — straight out of storage, bypassing
			// the view model entirely — then resync.
			store.DeleteKeyMacroBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, bound.MatrixIndex);
			await vm.ResyncKeyMacroBindingsAsync();

			// The slot must be written back to the key's own scancode, not left holding the macro.
			var cleared = Assert.Single(hid.KeyWrites);
			Assert.Equal(bound.MatrixIndex, cleared.MatrixIndex);
			Assert.NotEqual(RasterMacroTypeByte, cleared.Type);
			Assert.Equal(key.ScanCode, cleared.Code2);
		}
	}

	// PR #79 M1/M2. The matrix probe is fire-and-forget, so a binding made before it lands is keyed
	// by the CATALOG slot while everything afterwards uses the PROBED one. Re-binding the same key
	// then wrote a second row and orphaned the first — and the resync writes every row it finds, so
	// the orphan put a macro on whichever physical key owns the catalog slot. A key the user never
	// touched, and could not reset, because its own Reset deletes by its own slot.
	//
	// The guard that was supposed to prevent this compared ResolveMatrixSlot() against
	// MatrixIndex — but the probe overwrites MatrixIndex in place, so the two were always equal and
	// the second delete never ran. CatalogMatrixIndex is the untouched copy that makes it work.
	[Fact]
	public async Task RebindingAfterTheProbeLands_LeavesNoOrphanRowUnderTheCatalogSlot()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");
			AddMacro(vm, 102, "M2");

			var key = vm.PhysicalKeys.First();
			var catalogSlot = key.CatalogMatrixIndex;

			// Bind BEFORE the probe: the row is keyed by the catalog slot.
			vm.SelectPhysicalKeyCommand.Execute(key);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single(t => t.MacroId == 101));
			Assert.Equal(catalogSlot, Assert.Single(Bindings(store, owner)).MatrixIndex);

			// Probe lands and moves this key to a different firmware slot.
			var probedSlot = (byte)(catalogSlot + 40);
			hid.ProbedScancodeToSlot = new Dictionary<byte, byte> { [key.ScanCode] = probedSlot };
			await vm.ProbeAndUpdateMatrixMapAsync();
			Assert.Equal(probedSlot, vm.PhysicalKeys.First().MatrixIndex);
			Assert.Equal(catalogSlot, vm.PhysicalKeys.First().CatalogMatrixIndex);

			// Re-bind the SAME key to the other macro.
			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single(t => t.MacroId == 102));

			// Exactly one row, under the probed slot. Before the fix the catalog-slot row survived
			// and the resync replayed macro 101 on whatever key owns that slot.
			var only = Assert.Single(Bindings(store, owner));
			Assert.Equal(probedSlot, only.MatrixIndex);
			Assert.Equal(102, only.MacroId);
		}
	}

	// PR #79 M4. The resync sits behind StageChangeAndApply, which has 15 call sites — RGB, DPI,
	// report rate, mouse settings. Without an early-out, changing a colour on a keyboard with no
	// macros at all fired a full 1096-byte chunked HID library upload 250 ms later.
	[Fact]
	public async Task Resync_UploadsNothing_WhenThereAreNoBindingsAndNothingApplied()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");   // a library exists, but no key is bound to it
			hid.MacroUploads.Clear();

			await vm.ResyncKeyMacroBindingsAsync();

			Assert.Empty(hid.MacroUploads);
			Assert.Empty(hid.KeyWrites);
		}
	}

	private static List<(int MatrixIndex, int MacroId)> Bindings(LocalStorageService store, string owner) =>
		store.GetKeyMacroBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

	// PR #79 M3. The bindings are cached in memory now, so the risk moves from "too many SQLite
	// reads" to "a stale read". Every mutation has to invalidate, and a missed point shows up as
	// the UI insisting a key is still bound after it was cleared — the Reset link staying lit and
	// the Macros radio staying checked.
	[Fact]
	public async Task BindingCache_IsInvalidatedByEveryMutation()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");
			AddMacro(vm, 102, "M2");

			var key = vm.PhysicalKeys.First();
			vm.SelectPhysicalKeyCommand.Execute(key);
			Assert.Null(vm.BoundMacroForSelectedKey());        // primes the cache with "nothing"

			// Staged bind must be visible immediately.
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single(t => t.MacroId == 101));
			Assert.Equal(101, vm.BoundMacroForSelectedKey()?.Id);

			// Re-bind to another macro.
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single(t => t.MacroId == 102));
			Assert.Equal(102, vm.BoundMacroForSelectedKey()?.Id);

			// Reset clears it — and the header's Reset link reads the same source.
			var self = vm.AssignmentTargets.First(t => !t.IsMacroTarget && t.DevKeyName == key.DevKeyName);
			vm.SelectAssignmentTargetCommand.Execute(self);
			Assert.Null(vm.BoundMacroForSelectedKey());
			Assert.False(vm.CanResetSelectedMacroTarget);
		}
	}

	// A preset restore writes key_macro_bindings in SQL, outside the view model, then reloads the
	// key layout. If that reload did not invalidate, the cache would keep serving the pre-restore
	// answer and Discard would look like it had done nothing.
	[Fact]
	public async Task BindingCache_IsInvalidatedByAPresetRestoreWritingStorageDirectly()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			var key = vm.PhysicalKeys.First();
			vm.SelectPhysicalKeyCommand.Execute(key);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());
			Assert.Equal(101, vm.BoundMacroForSelectedKey()?.Id);

			// Behind the view model's back, as LoadPresetSnapshotToDevice does.
			store.DeleteKeyMacroBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey,
				vm.PhysicalKeys.First().MatrixIndex);
			vm.LoadKeyLayoutForDevice();

			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			Assert.Null(vm.BoundMacroForSelectedKey());
		}
	}

	// ── Play mode ───────────────────────────────────────────────────────────

	[Theory]
	[InlineData(false, PlayModeOnce)]
	[InlineData(true, PlayModeWhileHeld)]
	public async Task MacroTarget_PlayMode_FollowsRepeatUntilReleased(bool repeatUntilReleased, byte expectedCode2)
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1", repeatUntilReleased);

			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());
			await vm.ResyncKeyMacroBindingsAsync();

			var write = Assert.Single(hid.KeyWrites);
			Assert.Equal(expectedCode2, write.Code2);
		}
	}

	// ── Persistence + resync ────────────────────────────────────────────────

	[Fact]
	public async Task Resync_RewritesPersistedBinding()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");
			var key = vm.PhysicalKeys.First();

			vm.SelectPhysicalKeyCommand.Execute(key);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());

			var persisted = store.GetKeyMacroBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
			var row = Assert.Single(persisted);
			Assert.Equal(101, row.MacroId);

			hid.KeyWrites.Clear();
			await vm.ResyncKeyMacroBindingsAsync();

			var rewritten = Assert.Single(hid.KeyWrites);
			Assert.Equal(row.MatrixIndex, rewritten.MatrixIndex);
			Assert.Equal(RasterMacroTypeByte, rewritten.Type);
			Assert.Equal(0, rewritten.Code1);
		}
	}

	// A key must stop claiming a macro that is no longer in the library, or its slot index would
	// point at whatever macro shifted into that position.
	[Fact]
	public async Task Resync_DropsBindingWhoseMacroIsGone()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			var macro = AddMacro(vm, 101, "M1");

			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());
			Assert.Single(store.GetKeyMacroBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));

			// Remove it from the library without going through the delete command, so the resync
			// path itself is what has to notice.
			vm.Macros.Remove(macro);
			hid.KeyWrites.Clear();

			await vm.ResyncKeyMacroBindingsAsync();

			Assert.Empty(store.GetKeyMacroBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
			Assert.Empty(hid.KeyWrites);
		}
	}

	// If the library upload fails the device holds no library, so binding a key to a slot in it
	// would point at nothing. Nothing may be written.
	[Fact]
	public async Task Resync_WritesNothingWhenUploadFails()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());
			Assert.Single(store.GetKeyMacroBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));

			hid.KeyWrites.Clear();
			hid.SetMacrosResult = false;

			await vm.ResyncKeyMacroBindingsAsync();

			Assert.Empty(hid.KeyWrites);
			// The row survives: the binding is still what the user asked for, it just is not on
			// the device yet. Dropping it here would silently lose their configuration.
			Assert.Single(store.GetKeyMacroBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
		}
	}

	// A failed upload must also block the initial assignment, not just the resync.
	[Fact]
	public async Task Assign_WritesNothingWhenUploadFails()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");
			hid.SetMacrosResult = false;

			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());
			await vm.ResyncKeyMacroBindingsAsync();

			// The library never made it, so no key slot may be pointed into it.
			Assert.Empty(hid.KeyWrites);

			// The staged row DOES survive — the binding is app-truth recorded at selection time,
			// exactly like a staged key remap, so the next successful push retries it. Asserting
			// it away here would be asserting that a failed device write silently discards the
			// user's choice.
			Assert.Single(store.GetKeyMacroBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
		}
	}

	// ── Editing a bound macro (M1) ──────────────────────────────────────────
	// A keyboard macro lives in an indexed library, so edited events only reach the device when
	// that library is re-uploaded. Saving an edit used to push for the mouse only, leaving the key
	// playing the old recording while the App displayed the new one.
	[Fact]
	public async Task EditingABoundMacro_ReUploadsTheLibrary()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			var macro = AddMacro(vm, 101, "M1");

			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());

			hid.MacroUploads.Clear();
			hid.KeyWrites.Clear();

			// Reopen it for editing, flip the play mode, save.
			vm.EditMacroCommand.Execute(macro);
			vm.NewMacroRepeatUntilReleased = true;
			vm.SaveCreateMacroCommand.Execute(null);

			// The library must have been re-sent...
			Assert.NotEmpty(hid.MacroUploads);
			// ...and the key re-written with the edited macro's play mode.
			var write = hid.KeyWrites[^1];
			Assert.Equal(RasterMacroTypeByte, write.Type);
			Assert.Equal(PlayModeWhileHeld, write.Code2);
		}
	}

	// ── Reset enablement notification (M2) ──────────────────────────────────
	// The shared header binds CanResetSelectedMacroTarget. Its computed value was always right;
	// the bug was that nothing raised it after an assignment, so the Reset link stayed greyed out
	// until the user selected a different target and came back. Hence this asserts on the
	// PropertyChanged notification, not on the property.
	[Fact]
	public async Task AssigningToAKey_RaisesResetEnablementForTheHeader()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");
			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());

			var raised = new List<string>();
			vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? string.Empty);

			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());

			Assert.Contains(nameof(DeviceDetailsViewModel.CanResetSelectedMacroTarget), raised);
		}
	}

	// ── Smart Infinite (Cyrex / Aress) ──────────────────────────────────────
	// This family binds a macro with ITS OWN type 0x50 (MACRO_NO_COUNT), code2 = stop mode. The
	// previous shared-0x60 binding is FUNCTION on this firmware — stored, verified, never played —
	// which is the "macros don't work on Cyrex/Aress" bug this pins against ever coming back.
	[Fact]
	public async Task Cyrex_MacroBinding_UsesThisFirmwaresMacroType()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithCyrexInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());

			var write = Assert.Single(hid.KeyWrites);
			Assert.Equal(SmartInfiniteMacroTypeByte, write.Type);   // 0x50 — MACRO_NO_COUNT
			Assert.NotEqual(0x60, write.Type);                      // FUNCTION: stored, verified, never played
			Assert.Equal(0, write.Code1);                           // library slot
			Assert.Equal(PlayModeOnce, write.Code2);
		}
	}

	// Hold mode is a code2 value on type 0x50, not a separate type.
	[Fact]
	public async Task Cyrex_RepeatUntilReleased_SetsTheWhileHeldPlayMode()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
				new MacroRecord { Name = "Held", RepeatCount = 1, Mode = MacroRepeatMode.MacroWhileHeld });

			await WithCyrexInMacrosSection(vm, hid);
			var macro = Assert.Single(vm.Macros);
			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			macro.AssignCommand!.Execute(macro);

			var write = Assert.Single(hid.KeyWrites);
			Assert.Equal(SmartInfiniteMacroTypeByte, write.Type);
			Assert.Equal(PlayModeWhileHeld, write.Code2);
		}
	}

	// A repeat count does not fit on type 0x50 — code2 there is the stop mode — so it switches the
	// record to MACRO_WITH_COUNT (0x51), where code2 IS the count.
	[Fact]
	public async Task Cyrex_RepeatCount_SwitchesToTheRepeatMacroType()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
				new MacroRecord { Name = "Thrice", RepeatCount = 3, Mode = MacroRepeatMode.MacroPlayCount });

			await WithCyrexInMacrosSection(vm, hid);
			var macro = Assert.Single(vm.Macros);
			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			macro.AssignCommand!.Execute(macro);

			var write = Assert.Single(hid.KeyWrites);
			Assert.Equal(SmartInfiniteMacroRepeatTypeByte, write.Type);
			Assert.Equal(3, write.Code2);
		}
	}

	// Rampage joined the Smart Infinite macro path with the factory's 2026-08 clarifications, and
	// its repeat records must take the same 0x51 branch as the 65% boards — the predicate widened
	// from IsSmartInfinite65Device to the whole family, and this is the seam that would silently
	// fall back to play-once if that branch regressed.
	[Fact]
	public async Task Rampage_RepeatCount_SwitchesToTheRepeatMacroType()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, RampageVid, RampagePid, DeviceKey,
				new MacroRecord { Name = "Thrice", RepeatCount = 3, Mode = MacroRepeatMode.MacroPlayCount });

			await WithRampageInMacrosSection(vm, hid);
			var macro = Assert.Single(vm.Macros);
			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			macro.AssignCommand!.Execute(macro);

			var write = Assert.Single(hid.KeyWrites);
			Assert.Equal(SmartInfiniteMacroRepeatTypeByte, write.Type);
			Assert.Equal(3, write.Code2);
		}
	}

	// The primary (play) binding for Rampage — the same 0x50 record as the 65% boards, and the
	// library upload must reach the device before the slot can point into it. Rampage's macros are
	// UI-enabled by the catalog's SupportsMacros flip riding this change: the vendor's Keyborder
	// app registers this board (PID 12888) on the same GamingKeyboard2 protocol, macro path
	// included, so it is no longer the family member with no evidence.
	[Fact]
	public async Task Rampage_MacroRowAssignCommand_BindsWithThisFirmwaresMacroType()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, RampageVid, RampagePid, DeviceKey,
				new MacroRecord { Name = "M1", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });

			await WithRampageInMacrosSection(vm, hid);
			var macro = Assert.Single(vm.Macros);
			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			macro.AssignCommand!.Execute(macro);

			// The library has to reach the device before the slot can point into it.
			Assert.Single(hid.MacroUploads);
			var write = Assert.Single(hid.KeyWrites);
			Assert.Equal(SmartInfiniteMacroTypeByte, write.Type);
			Assert.Equal(0, write.Code1);
			Assert.Equal(PlayModeOnce, write.Code2);
		}
	}

	// ── T800 (RY5088) ───────────────────────────────────────────────────────
	// This family's record is [0x09, playType, slot] — code1 is the PLAY TYPE and code2 the
	// library slot, the mirror image of both families above. Keep in sync with
	// Ry5088Protocol.TypeMacro / MacroPlayType*.
	private const int T800Vid = 12625;
	private const int T800Pid = 20525;
	private const byte T800MacroTypeByte = 0x09;
	private const byte T800PlayTypeRepeat = 0x00;
	private const byte T800PlayTypeWhileHeld = 0x02;

	private static async Task<DeviceDetailsViewModel> WithT800InMacrosSection(DeviceDetailsViewModel vm, FakeUsbHidDeviceService hid)
	{
		await vm.SetDeviceAsync(new DoubleCardViewModel
		{
			Name = "T800",
			DeviceTypeLabel = "Wired Keyboard",
			VendorId = T800Vid,
			ProductId = T800Pid,
			SettingsVendorId = T800Vid,
			SettingsProductId = T800Pid,
			DeviceKey = DeviceKey,
			SettingsDeviceKey = DeviceKey,
			SupportsMacros = true,
		});
		vm.ActiveSection = NavSection.Macros;

		hid.MacroUploads.Clear();
		hid.KeyWrites.Clear();
		return vm;
	}

	// The operand swap is the whole point: writing this record Raster-style ([type, slot, mode])
	// would bind the key to "play macro <mode> with play type <slot>" — macro 0 bound anywhere
	// but slot 0 would play the wrong macro, silently.
	[Fact]
	public async Task T800_MacroBinding_PutsThePlayTypeInCode1AndTheSlotInCode2()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, T800Vid, T800Pid, DeviceKey,
				new MacroRecord { Name = "First", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, T800Vid, T800Pid, DeviceKey,
				new MacroRecord { Name = "Second", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });

			await WithT800InMacrosSection(vm, hid);
			var second = vm.Macros.Single(m => m.Name == "Second");
			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			second.AssignCommand!.Execute(second);

			// The library has to reach the device before the slot can point into it.
			Assert.Single(hid.MacroUploads);
			var write = Assert.Single(hid.KeyWrites);
			Assert.Equal(T800MacroTypeByte, write.Type);
			Assert.Equal(T800PlayTypeRepeat, write.Code1);   // play type, NOT the slot
			Assert.Equal(1, write.Code2);                    // slot of "Second", NOT a play mode
		}
	}

	// Hold mode is play type 2 in code1; the slot stays in code2.
	[Fact]
	public async Task T800_RepeatUntilReleased_UsesTheWhileHeldPlayType()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, T800Vid, T800Pid, DeviceKey,
				new MacroRecord { Name = "Held", RepeatCount = 1, Mode = MacroRepeatMode.MacroWhileHeld });

			await WithT800InMacrosSection(vm, hid);
			var macro = Assert.Single(vm.Macros);
			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			macro.AssignCommand!.Execute(macro);

			var write = Assert.Single(hid.KeyWrites);
			Assert.Equal(T800MacroTypeByte, write.Type);
			Assert.Equal(T800PlayTypeWhileHeld, write.Code1);
			Assert.Equal(0, write.Code2);
		}
	}

	// A repeat count does NOT change the record on this family — it has no counted type. The
	// count rides the library payload instead, into the blob header the device plays from.
	[Fact]
	public async Task T800_RepeatCount_StaysOnTheSameRecordAndRidesTheLibraryPayload()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, T800Vid, T800Pid, DeviceKey,
				new MacroRecord { Name = "Thrice", RepeatCount = 3, Mode = MacroRepeatMode.MacroPlayCount });

			await WithT800InMacrosSection(vm, hid);
			var macro = Assert.Single(vm.Macros);
			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			macro.AssignCommand!.Execute(macro);

			var write = Assert.Single(hid.KeyWrites);
			Assert.Equal(T800MacroTypeByte, write.Type);
			Assert.Equal(T800PlayTypeRepeat, write.Code1);   // unlike Smart Infinite, no 0x51 switch
			Assert.Equal(0, write.Code2);

			var upload = Assert.Single(hid.MacroUploads);
			Assert.Equal(3, Assert.Single(upload).RepeatCount);
		}
	}

	// Round 1 S1: the resync re-write path builds its record through the same MacroBindingRecord
	// as the assign path, but its call site passes the triple explicitly — a regression back to
	// the pre-T800 form (type, entry.Slot, code2) would keep every Raster/Smart Infinite resync
	// test green (their code1 IS the slot) while T800 keys silently re-bound to
	// "play type = slot" on every device connect. Slot 1 is used so the transposition is visible:
	// correct = (0x09, 0x00, 0x01), regressed = (0x09, 0x01, 0x01).
	[Fact]
	public async Task T800_Resync_RewritesTheBindingWithTheSwappedOperands()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, T800Vid, T800Pid, DeviceKey,
				new MacroRecord { Name = "First", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, T800Vid, T800Pid, DeviceKey,
				new MacroRecord { Name = "Second", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });

			await WithT800InMacrosSection(vm, hid);
			var second = vm.Macros.Single(m => m.Name == "Second");
			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			second.AssignCommand!.Execute(second);
			var bound = Assert.Single(hid.KeyWrites);

			// The resync — what runs on device connect and after every library change — must
			// reproduce the exact record the assignment wrote.
			hid.KeyWrites.Clear();
			hid.MacroUploads.Clear();
			await vm.ResyncKeyMacroBindingsAsync();

			Assert.Single(hid.MacroUploads);                 // library re-uploaded first
			var rewrite = Assert.Single(hid.KeyWrites);
			Assert.Equal(bound, rewrite);
			Assert.Equal(T800MacroTypeByte, rewrite.Type);
			Assert.Equal(T800PlayTypeRepeat, rewrite.Code1); // play type, NOT the slot
			Assert.Equal(1, rewrite.Code2);                  // slot of "Second"
		}
	}

	// The resync also refreshes the _assignments mirror the selection matcher reads. It used to
	// hard-code the slot into code1 there — right for the families where code1 IS the slot, wrong
	// on T800 where code1 is the play type — so the mirror stopped matching the macro row's
	// {type, bitmask, scancode} triple and the bound macro lost its highlight after every resync.
	[Fact]
	public async Task T800_Resync_KeepsTheBoundMacroRowSelected()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, T800Vid, T800Pid, DeviceKey,
				new MacroRecord { Name = "First", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, T800Vid, T800Pid, DeviceKey,
				new MacroRecord { Name = "Second", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });

			await WithT800InMacrosSection(vm, hid);
			var second = vm.Macros.Single(m => m.Name == "Second");
			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			second.AssignCommand!.Execute(second);
			var boundRow = MacroTargets(vm).Single(t => t.ScanCode == 1); // "Second" sits in slot 1
			Assert.True(boundRow.IsSelected);

			await vm.ResyncKeyMacroBindingsAsync();

			Assert.True(boundRow.IsSelected);
		}
	}

	// Raster is the family whose record IS hardware-confirmed; the Smart Infinite change must not
	// have leaked into it. code2 there is a play mode, and 0x00 (play through) is correct.
	[Fact]
	public async Task Raster_MacroRecord_KeepsTheConfirmedPlayModeSemantics()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());
			await vm.ResyncKeyMacroBindingsAsync();

			var write = Assert.Single(hid.KeyWrites);
			Assert.Equal(RasterMacroTypeByte, write.Type);
			Assert.Equal(PlayModeOnce, write.Code2);
		}
	}

	// The apply path used to return early for this family before reaching the SQLite write, so the
	// binding existed only in the firmware — which cannot say "macro #101" when read back. Nothing
	// re-uploaded the library and the key stopped playing after a reconnect.
	[Fact]
	public async Task Cyrex_MacroBinding_IsPersisted()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			await WithCyrexInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());

			var persisted = store.GetKeyMacroBindings(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey);
			var row = Assert.Single(persisted);
			Assert.Equal(101, row.MacroId);
		}
	}

	// The shared header's Reset link reads CanResetSelectedMacroTarget, which for a keyboard resolves
	// to "the firmware holds something other than the factory record for this slot". That was
	// computed from the Raster-only override map, so on this family it was permanently false: a macro
	// could be bound to a key and then never unassigned from the header.
	[Fact]
	public async Task Cyrex_ResetLink_EnablesOnceTheKeyIsBound()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithCyrexInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			Assert.False(vm.CanResetSelectedMacroTarget);

			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());
			Assert.True(vm.CanResetSelectedMacroTarget);
		}
	}

	// Resetting has to put the slot back to the key's own record, and clear the persisted binding —
	// otherwise the next resync re-writes the macro the user just removed.
	[Fact]
	public async Task Cyrex_ResetSelectedKey_ClearsTheBindingAndTheResetLink()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			await WithCyrexInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");
			var key = vm.PhysicalKeys.First();

			vm.SelectPhysicalKeyCommand.Execute(key);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());
			hid.KeyWrites.Clear();

			await vm.ResetSelectedMacroTargetCommand.ExecuteAsync(null);

			var write = Assert.Single(hid.KeyWrites);
			Assert.Equal(key.TypeByte, write.Type);
			Assert.Equal(key.Bitmask, write.Code1);
			Assert.Equal(key.ScanCode, write.Code2);

			Assert.Empty(store.GetKeyMacroBindings(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey));
			Assert.False(vm.CanResetSelectedMacroTarget);
		}
	}

	// Reproduces the user report "I cannot assign a macro on Cyrex/Aress". The tests above drive
	// SelectAssignmentTargetCommand — the target list on Key Assignments. The Macros page is a
	// different entry point: the macro ROW carries AssignCommand, which is
	// AssignMacroToSelectedButtonCommand. That is the click the user actually makes.
	[Fact]
	public async Task Cyrex_MacroRowAssignCommand_BindsTheSelectedKey()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			// Seeded into the store and loaded through SetDevice, so the row is wired exactly as
			// production wires it — AddMacro() adds to the collection directly and skips WireMacro.
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
				new MacroRecord { Name = "M1", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });

			await WithCyrexInMacrosSection(vm, hid);
			var macro = Assert.Single(vm.Macros);

			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			Assert.NotNull(macro.AssignCommand);
			macro.AssignCommand!.Execute(macro);

			var write = Assert.Single(hid.KeyWrites);
			Assert.Equal(SmartInfiniteMacroTypeByte, write.Type);
		}
	}

	// M2 from the PR review: a refused upload used to be completely silent — the bind was skipped
	// by design (a key must never point into a library the board does not hold), but nothing
	// anywhere said so, and the click read as "nothing happened". The flag drives the Macros-page
	// banner; the key write must not happen; a later successful upload clears it.
	[Fact]
	public async Task Cyrex_UploadFailure_SurfacesTheFlagAndSkipsTheBind()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
				new MacroRecord { Name = "M1", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });

			await WithCyrexInMacrosSection(vm, hid);
			var macro = Assert.Single(vm.Macros);
			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());

			hid.SetMacrosResult = false;
			macro.AssignCommand!.Execute(macro);

			Assert.True(vm.MacroUploadFailed);
			Assert.Empty(hid.KeyWrites);

			hid.SetMacrosResult = true;
			macro.AssignCommand!.Execute(macro);

			Assert.False(vm.MacroUploadFailed);
			Assert.Single(hid.KeyWrites);
		}
	}

	// Aress is the other half of the family and has never been exercised here — its catalog, its
	// PID and (since the artwork change) its borrowed overlay table are all separate code paths
	// from Cyrex's. Same production-shaped flow: seed the store, open the device, click a key,
	// click the macro row.
	[Fact]
	public async Task Aress_MacroRowAssignCommand_BindsTheSelectedKey()
	{
		const int aressVid = 10473;
		const int aressPid = 12910;

		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, aressVid, aressPid, DeviceKey,
				new MacroRecord { Name = "M1", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });

			await vm.SetDeviceAsync(new DoubleCardViewModel
			{
				Name = "Aress",
				DeviceTypeLabel = "Wired Keyboard",
				VendorId = aressVid,
				ProductId = aressPid,
				SettingsVendorId = aressVid,
				SettingsProductId = aressPid,
				DeviceKey = DeviceKey,
				SettingsDeviceKey = DeviceKey,
				SupportsMacros = true,
			});
			vm.ActiveSection = NavSection.Macros;
			hid.MacroUploads.Clear();
			hid.KeyWrites.Clear();

			// The preconditions the UI depends on: a clickable board, and a macro row to click.
			Assert.True(vm.IsMacrosSupported);
			Assert.True(vm.ShowKeyboardOverlayInMacros);
			Assert.NotEmpty(vm.PhysicalKeys);

			var macro = Assert.Single(vm.Macros);
			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());
			Assert.NotNull(macro.AssignCommand);
			macro.AssignCommand!.Execute(macro);

			// The library has to reach the device before the slot can point into it.
			Assert.Single(hid.MacroUploads);
			var write = Assert.Single(hid.KeyWrites);
			Assert.Equal(SmartInfiniteMacroTypeByte, write.Type);

			Assert.Single(store.GetKeyMacroBindings(
				LocalStorageService.OwnerKindGuest, owner, aressVid, aressPid, DeviceKey));
		}
	}

	// The reported bug: on Cyrex the macro reached the firmware (the DeviceHost log showed the
	// library written and slot 19 verified as {0x50,0x00,0x00}) but key_macro_bindings stayed
	// empty, so the Macros page still showed the key as unbound — "I cannot assign it".
	//
	// The cause was the guard between those two steps. It compared _keyLayoutGeneration, which
	// LoadKeyLayoutForDevice bumps even when the SAME device is reloaded — a card refresh or a
	// preset apply does it. Landing inside the write window meant the method returned after the
	// firmware was already written, and the binding was silently dropped. Reproduced here by
	// reloading the layout for the same device mid-write.
	[Fact]
	public async Task Cyrex_LayoutReloadDuringTheWrite_StillPersistsTheBinding()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
				new MacroRecord { Name = "M1", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });

			await WithCyrexInMacrosSection(vm, hid);
			var macro = Assert.Single(vm.Macros);
			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());

			// Same keyboard, layout rebuilt underneath the in-flight write.
			hid.DuringKeyWrite = () => vm.LoadKeyLayoutForDevice();

			macro.AssignCommand!.Execute(macro);

			Assert.Single(hid.KeyWrites);
			var row = Assert.Single(store.GetKeyMacroBindings(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey));
			Assert.Equal(macro.Id, row.MacroId);
		}
	}

	// The other half of the same guard: a real device switch must still discard the bookkeeping,
	// or an assignment lands against a keyboard that was never sent it.
	[Fact]
	public async Task Cyrex_DeviceSwitchDuringTheWrite_DiscardsTheBinding()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
				new MacroRecord { Name = "M1", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });

			await WithCyrexInMacrosSection(vm, hid);
			var macro = Assert.Single(vm.Macros);
			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First());

			// A different board takes over mid-write. The switch's synchronous prologue (Device,
			// generation, key layout) is what discards the bookkeeping; the load task is awaited
			// before asserting so its background storage work can't outlive the store.
			Task? switchTask = null;
			hid.DuringKeyWrite = () => switchTask = vm.SetDeviceAsync(new DoubleCardViewModel
			{
				Name = "Aress",
				DeviceTypeLabel = "Wired Keyboard",
				VendorId = 10473,
				ProductId = 12910,
				SettingsVendorId = 10473,
				SettingsProductId = 12910,
				DeviceKey = DeviceKey,
				SettingsDeviceKey = DeviceKey,
				SupportsMacros = true,
			});

			macro.AssignCommand!.Execute(macro);
			if (switchTask is not null)
				await switchTask;

			Assert.Empty(store.GetKeyMacroBindings(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey));
			Assert.Empty(store.GetKeyMacroBindings(
				LocalStorageService.OwnerKindGuest, owner, 10473, 12910, DeviceKey));
		}
	}

	// PR #74 review, m1. RefreshFromDeviceAsync probes the matrix FIRST and resyncs SECOND, so after
	// the resync rewrites a slot the in-memory _assignments mirror still holds the pre-resync read.
	// Smart Infinite matches the selection highlight on the whole {type, code1, code2} triple, so a
	// macro that moved slot in the library matched no target at all and the key read as unbound on
	// the Macros page even though the device held the binding — self-healing only on the next open.
	//
	// The scenario: the macro sits at library slot 1 on the device, an earlier macro is gone so it is
	// now slot 0, and the resync writes slot 0 to the firmware.
	[Fact]
	public async Task Resync_AfterAMacroChangesSlot_LeavesTheKeyShowingAsBound()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var macroId = store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
				new MacroRecord { Name = "M", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });

			// The firmware still points this key at the macro's OLD library slot (1).
			const byte slot = 17;
			hid.KeyMatrix = new() { [slot] = (SmartInfiniteMacroTypeByte, 1, PlayModeOnce) };

			await WithCyrexInMacrosSection(vm, hid);
			var key = vm.PhysicalKeys.First(k => k.MatrixIndex == slot);
			vm.SelectPhysicalKeyCommand.Execute(key);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());

			// Re-open the device: the read returns the stale slot-1 record, then the resync rewrites
			// the firmware to slot 0 — which is what the macro target now carries.
			store.SetKeyMacroBinding(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey, slot, macroId);
			hid.KeyMatrix = new() { [slot] = (SmartInfiniteMacroTypeByte, 1, PlayModeOnce) };
			await WithCyrexInMacrosSection(vm, hid);

			vm.SelectPhysicalKeyCommand.Execute(vm.PhysicalKeys.First(k => k.MatrixIndex == slot));

			var macroTarget = MacroTargets(vm).Single();
			Assert.Equal(0, macroTarget.Bitmask);   // the macro is at library slot 0 now
			Assert.True(macroTarget.IsSelected,
				"the key was rewritten to the macro's new slot but still reads as unbound: " +
				"_assignments went stale against the post-resync record");
		}
	}

	// ── Bound-macro hover tooltip ───────────────────────────────────────────
	// The key overlay's hover tip names what a macro-bound key plays through GetKeyRemapTooltip —
	// the value the view renders on PointerEntered. These pin the wiring: the tooltip follows the
	// binding through the real commands, on every keyboard family, and (unlike the old on-key
	// badge chip it replaced) does not depend on the selection or the active section.
	[Fact]
	public async Task HoverTooltip_NamesTheBoundMacro_AfterAssignment()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");
			AddMacro(vm, 102, "M2");

			var key = vm.PhysicalKeys.First();
			vm.SelectPhysicalKeyCommand.Execute(key);
			Assert.Null(vm.GetKeyRemapTooltip(key));   // selected, but nothing bound yet

			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single(t => t.MacroId == 102));

			Assert.Equal("M2", vm.GetKeyRemapTooltip(key));
		}
	}

	// The tooltip names what THAT key plays — a binding on one key must not leak into the tip of
	// any other.
	[Fact]
	public async Task HoverTooltip_IsNull_ForAKeyWithNoMacro()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			var bound = vm.PhysicalKeys.First();
			vm.SelectPhysicalKeyCommand.Execute(bound);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());
			Assert.Equal("M1", vm.GetKeyRemapTooltip(bound));

			var unbound = vm.PhysicalKeys.First(k => k.DevKeyName != bound.DevKeyName);

			Assert.Null(vm.GetKeyRemapTooltip(unbound));
		}
	}

	// Unassigning must clear the tip the moment it lands — a tip still naming the macro would be
	// offering a binding that no longer exists. Staged family (Raster/T-Racer): the binding is
	// recorded but not pushed.
	[Fact]
	public async Task HoverTooltip_ClearsWhenTheMacroIsUnassigned_OnTheStagedPath()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			var key = vm.PhysicalKeys.First();
			vm.SelectPhysicalKeyCommand.Execute(key);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());
			Assert.Equal("M1", vm.GetKeyRemapTooltip(key));

			vm.ResetSelectedMacroTargetCommand.Execute(null);

			Assert.Null(vm.GetKeyRemapTooltip(key));
		}
	}

	// The immediate-write family (Smart Infinite) is the one where the analogous macro-list refresh
	// was missing and left a checked row behind after a reset (PR #81), so it gets its own case
	// rather than being assumed to follow Raster.
	[Fact]
	public async Task HoverTooltip_ClearsWhenTheMacroIsUnassigned_OnTheImmediateWritePath()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			hid.KeyMatrix = new() { [0] = (0x10, 0x00, 0x29) };
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
				new MacroRecord { Name = "M1", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });
			await WithCyrexInMacrosSection(vm, hid);
			var macro = Assert.Single(vm.Macros);

			var key = vm.PhysicalKeys.First();
			vm.SelectPhysicalKeyCommand.Execute(key);
			macro.AssignCommand!.Execute(macro);
			Assert.Equal("M1", vm.GetKeyRemapTooltip(key));

			vm.ResetSelectedMacroTargetCommand.Execute(null);

			Assert.Null(vm.GetKeyRemapTooltip(key));
			Assert.Empty(store.GetKeyMacroBindings(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey));
		}
	}

	// The overlay is shared between the Macros and Key Assignments pages, and the tip is the ONLY
	// place either of them names a bound macro on the keyboard — so unlike the old badge, the value
	// must survive a section change rather than be gated to one page.
	[Fact]
	public async Task HoverTooltip_NamesTheBoundMacro_OnEverySection()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			var key = vm.PhysicalKeys.First();
			vm.SelectPhysicalKeyCommand.Execute(key);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());
			Assert.Equal("M1", vm.GetKeyRemapTooltip(key));

			vm.ActiveSection = NavSection.Buttons;
			Assert.Equal("M1", vm.GetKeyRemapTooltip(key));

			vm.ActiveSection = NavSection.Macros;
			Assert.Equal("M1", vm.GetKeyRemapTooltip(key));
		}
	}

	// A device switch reloads the key layout and the binding cache. A tip surviving the switch
	// would name a macro from a keyboard that is no longer on screen.
	[Fact]
	public async Task HoverTooltip_DoesNotCarryAcrossADeviceChange()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			var key = vm.PhysicalKeys.First();
			vm.SelectPhysicalKeyCommand.Execute(key);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());
			Assert.Equal("M1", vm.GetKeyRemapTooltip(key));

			hid.KeyMatrix = new() { [0] = (0x10, 0x00, 0x29) };
			await WithCyrexInMacrosSection(vm, hid);

			Assert.Null(vm.GetKeyRemapTooltip(vm.PhysicalKeys.First()));
		}
	}

	// Re-binding replaces rather than stacks: the tip must name what the key plays NOW.
	[Fact]
	public async Task HoverTooltip_TracksTheNewMacro_WhenTheKeyIsRebound()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "A");
			AddMacro(vm, 102, "B");

			var key = vm.PhysicalKeys.First();
			vm.SelectPhysicalKeyCommand.Execute(key);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single(t => t.MacroId == 101));
			Assert.Equal("A", vm.GetKeyRemapTooltip(key));

			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single(t => t.MacroId == 102));

			Assert.Equal("B", vm.GetKeyRemapTooltip(key));
		}
	}

	// T800 and Rampage reach the binding through different slot resolution than Raster (catalog
	// slots, no probe) — either one resolving differently would leave the tip nameless on exactly
	// that board.
	[Fact]
	public async Task HoverTooltip_NamesTheBoundMacro_OnT800()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, T800Vid, T800Pid, DeviceKey,
				new MacroRecord { Name = "Reload", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });

			await WithT800InMacrosSection(vm, hid);
			var macro = Assert.Single(vm.Macros);
			var key = vm.PhysicalKeys.First();
			vm.SelectPhysicalKeyCommand.Execute(key);
			macro.AssignCommand!.Execute(macro);

			Assert.Equal("Reload", vm.GetKeyRemapTooltip(key));
		}
	}

	[Fact]
	public async Task HoverTooltip_NamesTheBoundMacro_OnRampage()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, RampageVid, RampagePid, DeviceKey,
				new MacroRecord { Name = "Reload", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });

			await WithRampageInMacrosSection(vm, hid);
			var macro = Assert.Single(vm.Macros);
			var key = vm.PhysicalKeys.First();
			vm.SelectPhysicalKeyCommand.Execute(key);
			macro.AssignCommand!.Execute(macro);

			Assert.Equal("Reload", vm.GetKeyRemapTooltip(key));
		}
	}

	// ── Hover tooltip unassign (✕) ──────────────────────────────────────────
	// The ✕ inside a macro-bound key's tip dispatches UnassignMacroFromKey with the HOVERED
	// key — which may not be the selected one — and must run the same reset path as every
	// other unassign affordance.
	[Fact]
	public async Task UnassignMacroFromKey_RemovesTheBinding_OnTheStagedPath()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			var key = vm.PhysicalKeys.First();
			vm.SelectPhysicalKeyCommand.Execute(key);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());
			Assert.True(vm.KeyHasBoundMacro(key));

			vm.UnassignMacroFromKeyCommand.Execute(key);

			Assert.False(vm.KeyHasBoundMacro(key));
			Assert.Null(vm.GetKeyRemapTooltip(key));
		}
	}

	[Fact]
	public async Task UnassignMacroFromKey_RemovesTheBinding_OnTheImmediateWritePath()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			hid.KeyMatrix = new() { [0] = (0x10, 0x00, 0x29) };
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
				new MacroRecord { Name = "M1", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });
			await WithCyrexInMacrosSection(vm, hid);
			var macro = Assert.Single(vm.Macros);

			var key = vm.PhysicalKeys.First();
			vm.SelectPhysicalKeyCommand.Execute(key);
			macro.AssignCommand!.Execute(macro);
			Assert.True(vm.KeyHasBoundMacro(key));

			vm.UnassignMacroFromKeyCommand.Execute(key);

			Assert.False(vm.KeyHasBoundMacro(key));
			Assert.Empty(store.GetKeyMacroBindings(
				LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey));
		}
	}

	// The tip shows on hover regardless of selection, so its ✕ must act on ITS key even while
	// another key is selected — by moving the selection there first (without the dialog), so
	// the shared header and the reset path both point at the key being unassigned.
	[Fact]
	public async Task UnassignMacroFromKey_ActsOnTheHoveredKey_NotTheSelectedOne()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			var bound = vm.PhysicalKeys.First();
			vm.SelectPhysicalKeyCommand.Execute(bound);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());

			var other = vm.PhysicalKeys.First(k => k.DevKeyName != bound.DevKeyName);
			vm.SelectPhysicalKeyCommand.Execute(other);
			Assert.True(vm.KeyHasBoundMacro(bound));

			vm.UnassignMacroFromKeyCommand.Execute(bound);

			Assert.False(vm.KeyHasBoundMacro(bound));
			Assert.Same(bound, vm.SelectedPhysicalKey);
		}
	}

	// A ✕ can only ever be rendered for a macro-bound key, but the command is the last line of
	// defence: a stale click must not move the selection or touch the stores.
	[Fact]
	public async Task UnassignMacroFromKey_IsANoOp_ForAKeyWithNoMacro()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			var selected = vm.PhysicalKeys.First();
			vm.SelectPhysicalKeyCommand.Execute(selected);
			var unbound = vm.PhysicalKeys.First(k => k.DevKeyName != selected.DevKeyName);

			vm.UnassignMacroFromKeyCommand.Execute(unbound);

			Assert.Same(selected, vm.SelectedPhysicalKey);
			Assert.False(vm.KeyHasBoundMacro(unbound));
		}
	}

	// The ✕ never renders on the Actuation page (ShowKeyTip gates it to Macros/Key
	// Assignments), and the command is the second line of defence: Per-key mode owns the
	// IsSelected flags through its own single-select (review round 2 B1), so a stale or
	// direct invocation must neither drop the binding nor move any selection.
	[Fact]
	public async Task UnassignMacroFromKey_IsANoOp_InActuationPerKeyMode()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			var bound = vm.PhysicalKeys.First();
			vm.SelectPhysicalKeyCommand.Execute(bound);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());
			Assert.True(vm.KeyHasBoundMacro(bound));

			vm.ActiveSection = NavSection.Actuation;
			vm.ActuationPerKeyMode = true;
			var actuationKey = vm.PhysicalKeys.First(k => k.DevKeyName != bound.DevKeyName);
			vm.SelectPhysicalKeyCommand.Execute(actuationKey);
			Assert.True(actuationKey.IsSelected);

			vm.UnassignMacroFromKeyCommand.Execute(bound);

			Assert.True(vm.KeyHasBoundMacro(bound));
			Assert.True(actuationKey.IsSelected);   // the actuation selection is untouched
			Assert.False(bound.IsSelected);         // no sweep moved the highlight
		}
	}

	// The tip's ✕ raises the same undo toast the dialog's picks do: the user acted from a
	// hover surface with no other feedback, so the unassign must announce itself — and UNDO
	// must re-bind the macro through the standard assignment path.
	[Fact]
	public async Task UnassignMacroFromKey_RaisesTheUndoToast()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			var key = vm.PhysicalKeys.First();
			vm.SelectPhysicalKeyCommand.Execute(key);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());

			vm.UnassignMacroFromKeyCommand.Execute(key);

			Assert.True(vm.IsRemapToastVisible);
			// GetString falls back to the key itself when resources aren't loaded (tests), so
			// the expectation is built from the same source rather than a literal.
			Assert.Equal(
				string.Format(
					ProoveGaming.App.LocalizationManager.GetString("Macros_UnassignToast_Format"),
					"M1", key.KeyLabel),
				vm.RemapToastText);
			Assert.False(vm.KeyHasBoundMacro(key));

			vm.UndoRemapCommand.Execute(null);

			Assert.False(vm.IsRemapToastVisible);
			Assert.True(vm.KeyHasBoundMacro(key));
		}
	}

	// Every silent early-out (unbound key, actuation guard) must stay silent — a toast
	// announcing an unassign that did not happen would lie.
	[Fact]
	public async Task UnassignMacroFromKey_RaisesNoToast_WhenNothingWasUnassigned()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			var unbound = vm.PhysicalKeys.First();

			vm.UnassignMacroFromKeyCommand.Execute(unbound);

			Assert.False(vm.IsRemapToastVisible);
		}
	}

	// Review round 1 S1: a ✕ on another key moves the selection, and the toast's UNDO acts on
	// the SELECTED key — so the previous toast must hide the moment the selection moves, not
	// linger through the new unassign's device write offering an UNDO that would silently
	// no-op. Cyrex (immediate-write family): its reset issues a real key write, and the fake's
	// DuringKeyWrite hook observes the toast state inside exactly that in-flight window.
	[Fact]
	public async Task UnassignMacroFromKey_HidesThePreviousToast_BeforeTheNextWrite()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			hid.KeyMatrix = new() { [0] = (0x10, 0x00, 0x29) };
			await WithCyrexInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");
			AddMacro(vm, 102, "M2");

			var keyA = vm.PhysicalKeys[0];
			var keyB = vm.PhysicalKeys[1];
			vm.SelectPhysicalKeyCommand.Execute(keyA);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single(t => t.MacroId == 101));
			vm.SelectPhysicalKeyCommand.Execute(keyB);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single(t => t.MacroId == 102));

			vm.UnassignMacroFromKeyCommand.Execute(keyA);
			Assert.True(vm.IsRemapToastVisible);   // toast A is up

			bool? visibleDuringWrite = null;
			hid.DuringKeyWrite = () => visibleDuringWrite = vm.IsRemapToastVisible;

			vm.UnassignMacroFromKeyCommand.Execute(keyB);

			Assert.False(visibleDuringWrite);      // hidden the moment the selection moved
			Assert.True(vm.IsRemapToastVisible);   // and replaced by B's own toast afterwards
			Assert.Equal(
				string.Format(
					ProoveGaming.App.LocalizationManager.GetString("Macros_UnassignToast_Format"),
					"M2", keyB.KeyLabel),
				vm.RemapToastText);
		}
	}

	// Checking an inspection radio clears the key selection, and the toast's UNDO acts on the
	// selected key — so the toast must go with it rather than keep offering an UNDO that would
	// silently no-op.
	[Fact]
	public async Task InspectMacro_HidesTheUnassignToast()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");
			AddMacro(vm, 102, "M2");

			var key = vm.PhysicalKeys.First();
			vm.SelectPhysicalKeyCommand.Execute(key);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single(t => t.MacroId == 101));
			vm.UnassignMacroFromKeyCommand.Execute(key);
			Assert.True(vm.IsRemapToastVisible);

			vm.InspectMacroCommand.Execute(vm.Macros.Single(m => m.Id == 102));

			Assert.False(vm.IsRemapToastVisible);
		}
	}

	// ── Macro inspection (radio → highlight bound keys) ─────────────────────
	// The Macros list's radio is an inspection control: checking a macro flags every key it
	// is bound to (IsMacroBindingHighlighted → the overlay's "macroBound" box). It never
	// assigns, and it never touches IsSelected — that flag still carries the dialog's
	// bound-to-this-key semantics.
	[Fact]
	public async Task InspectMacro_HighlightsEveryKeyBoundToIt()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");
			AddMacro(vm, 102, "M2");

			var first = vm.PhysicalKeys[0];
			var second = vm.PhysicalKeys[1];
			var third = vm.PhysicalKeys[2];
			vm.SelectPhysicalKeyCommand.Execute(first);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single(t => t.MacroId == 101));
			vm.SelectPhysicalKeyCommand.Execute(second);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single(t => t.MacroId == 101));
			vm.SelectPhysicalKeyCommand.Execute(third);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single(t => t.MacroId == 102));

			var m1 = vm.Macros.Single(m => m.Id == 101);
			vm.InspectMacroCommand.Execute(m1);

			Assert.True(m1.IsInspected);
			Assert.True(first.IsMacroBindingHighlighted);
			Assert.True(second.IsMacroBindingHighlighted);
			Assert.False(third.IsMacroBindingHighlighted);
			// The leftover key selection is dropped, so the highlight is not eclipsed by the
			// stronger .selected fill on the key that was just assigned (see InspectMacro).
			Assert.Null(vm.SelectedPhysicalKey);
			Assert.DoesNotContain(vm.PhysicalKeys, k => k.IsSelected);
		}
	}

	[Fact]
	public async Task InspectMacro_MovesTheHighlight_WhenAnotherMacroIsInspected()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");
			AddMacro(vm, 102, "M2");

			var first = vm.PhysicalKeys[0];
			var second = vm.PhysicalKeys[1];
			vm.SelectPhysicalKeyCommand.Execute(first);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single(t => t.MacroId == 101));
			vm.SelectPhysicalKeyCommand.Execute(second);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single(t => t.MacroId == 102));

			var m1 = vm.Macros.Single(m => m.Id == 101);
			var m2 = vm.Macros.Single(m => m.Id == 102);
			vm.InspectMacroCommand.Execute(m1);
			Assert.True(first.IsMacroBindingHighlighted);

			vm.InspectMacroCommand.Execute(m2);

			Assert.False(m1.IsInspected);
			Assert.True(m2.IsInspected);
			Assert.False(first.IsMacroBindingHighlighted);
			Assert.True(second.IsMacroBindingHighlighted);
		}
	}

	// The highlight is derived state: unassigning a highlighted key must drop its box without
	// the user re-clicking the row (the refresh rides RefreshMacroListSelection).
	[Fact]
	public async Task InspectedHighlight_FollowsUnassign()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			var first = vm.PhysicalKeys[0];
			var second = vm.PhysicalKeys[1];
			vm.SelectPhysicalKeyCommand.Execute(first);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());
			vm.SelectPhysicalKeyCommand.Execute(second);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());

			var m1 = vm.Macros.Single();
			vm.InspectMacroCommand.Execute(m1);
			Assert.True(first.IsMacroBindingHighlighted);
			Assert.True(second.IsMacroBindingHighlighted);

			vm.UnassignMacroFromKeyCommand.Execute(first);

			Assert.False(first.IsMacroBindingHighlighted);
			Assert.True(second.IsMacroBindingHighlighted);
		}
	}

	[Fact]
	public async Task InspectedHighlight_FollowsANewAssignment()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			var first = vm.PhysicalKeys[0];
			var second = vm.PhysicalKeys[1];
			vm.SelectPhysicalKeyCommand.Execute(first);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());

			var m1 = vm.Macros.Single();
			vm.InspectMacroCommand.Execute(m1);
			Assert.False(second.IsMacroBindingHighlighted);

			vm.SelectPhysicalKeyCommand.Execute(second);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());

			Assert.True(second.IsMacroBindingHighlighted);
		}
	}

	// PR #104 M1: a preset switch calls LoadKeyLayoutForDevice, which throws away every key
	// view model and builds new ones with the highlight flags at their default — while the
	// inspection radio (the macro list is NOT reloaded on a preset switch) stays checked.
	// PopulateKeyLayout must re-derive the highlights against the fresh keys, or the page
	// shows a checked radio over a dark board — reading as "this macro is bound nowhere".
	[Fact]
	public async Task InspectedHighlight_SurvivesAKeyLayoutReload()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			var key = vm.PhysicalKeys[0];
			vm.SelectPhysicalKeyCommand.Execute(key);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());

			var m1 = vm.Macros.Single();
			vm.InspectMacroCommand.Execute(m1);
			Assert.True(key.IsMacroBindingHighlighted);

			// The preset-apply path: same device, fresh key view models.
			vm.LoadKeyLayoutForDevice();

			Assert.True(m1.IsInspected);   // the radio survives the reload…
			var rebuilt = vm.PhysicalKeys.First(k => k.DevKeyName == key.DevKeyName);
			Assert.NotSame(key, rebuilt);
			Assert.True(rebuilt.IsMacroBindingHighlighted);   // …and so must its highlight
		}
	}

	// Inspection is a Macros-page affordance: leaving the section clears it, and coming back
	// does not restore it.
	[Fact]
	public async Task Inspection_ClearsOnLeavingTheMacrosSection()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			var first = vm.PhysicalKeys[0];
			vm.SelectPhysicalKeyCommand.Execute(first);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());

			var m1 = vm.Macros.Single();
			vm.InspectMacroCommand.Execute(m1);
			Assert.True(first.IsMacroBindingHighlighted);

			vm.ActiveSection = NavSection.Buttons;

			Assert.False(m1.IsInspected);
			Assert.False(first.IsMacroBindingHighlighted);

			vm.ActiveSection = NavSection.Macros;
			Assert.False(m1.IsInspected);
			Assert.False(first.IsMacroBindingHighlighted);
		}
	}

	[Fact]
	public async Task Inspection_ClearsOnDeviceChange()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			var first = vm.PhysicalKeys[0];
			vm.SelectPhysicalKeyCommand.Execute(first);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());
			vm.InspectMacroCommand.Execute(vm.Macros.Single());

			hid.KeyMatrix = new() { [0] = (0x10, 0x00, 0x29) };
			await WithCyrexInMacrosSection(vm, hid);

			Assert.DoesNotContain(vm.Macros, m => m.IsInspected);
			Assert.DoesNotContain(vm.PhysicalKeys, k => k.IsMacroBindingHighlighted);
		}
	}

	[Fact]
	public async Task Inspection_ClearsWhenTheInspectedMacroIsDeleted()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");

			var first = vm.PhysicalKeys[0];
			vm.SelectPhysicalKeyCommand.Execute(first);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single());

			var m1 = vm.Macros.Single();
			vm.InspectMacroCommand.Execute(m1);
			Assert.True(first.IsMacroBindingHighlighted);

			vm.RequestDeleteMacroCommand.Execute(m1);
			vm.ConfirmDeleteMacroCommand.Execute(null);

			Assert.False(first.IsMacroBindingHighlighted);
		}
	}

	// The user-reported flow: the binding was made in an EARLIER session (persisted rows only,
	// nothing assigned in this run), the device page is freshly opened, and the first thing the
	// user does is check the radio. The highlight must resolve from the persisted binding map,
	// exactly like the hover tooltip already does.
	[Fact]
	public async Task InspectMacro_HighlightsKeysBoundInAnEarlierSession()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var macroId = store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey,
				new MacroRecord { Name = "M1", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });

			// First open only resolves the slot the previous session would have written.
			await WithRasterInMacrosSection(vm, hid);
			var slot = vm.PhysicalKeys[0].MatrixIndex;
			store.SetKeyMacroBinding(
				LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, slot, macroId);

			// Fresh open: caches and the macro list load from the store alone.
			await WithRasterInMacrosSection(vm, hid);
			var key = vm.PhysicalKeys[0];
			var m1 = Assert.Single(vm.Macros);
			Assert.Equal("M1", vm.GetKeyRemapTooltip(key));   // the tooltip already resolves

			vm.InspectMacroCommand.Execute(m1);

			Assert.True(m1.IsInspected);
			Assert.True(key.IsMacroBindingHighlighted);
		}
	}

	// Inspection is a new focus: checking a radio drops the leftover key selection (the dialog
	// leaves the just-assigned key selected, and its stronger .selected fill would eclipse the
	// .macroBound highlight on exactly that key). The bound-macro mirror (IsSelected) follows
	// the selection through the standard refresh, so no row reads as "bound to the selected
	// key" while nothing is selected — and re-selecting a key restores the dialog semantics.
	[Fact]
	public async Task InspectMacro_ClearsTheKeySelection()
	{
		var (vm, store, hid, _) = NewVm();
		using (store)
		{
			await WithRasterInMacrosSection(vm, hid);
			AddMacro(vm, 101, "M1");
			AddMacro(vm, 102, "M2");

			var first = vm.PhysicalKeys[0];
			vm.SelectPhysicalKeyCommand.Execute(first);
			vm.SelectAssignmentTargetCommand.Execute(MacroTargets(vm).Single(t => t.MacroId == 101));

			var m1 = vm.Macros.Single(m => m.Id == 101);
			var m2 = vm.Macros.Single(m => m.Id == 102);
			Assert.True(m1.IsSelected);   // bound to the selected key

			vm.InspectMacroCommand.Execute(m2);

			Assert.Null(vm.SelectedPhysicalKey);
			Assert.False(first.IsSelected);
			Assert.DoesNotContain(vm.Macros, m => m.IsSelected);
			Assert.True(m2.IsInspected);

			// Selecting a key again restores the dialog's bound-to-this-key mirror.
			vm.SelectPhysicalKeyCommand.Execute(first);
			Assert.True(m1.IsSelected);
		}
	}
}
