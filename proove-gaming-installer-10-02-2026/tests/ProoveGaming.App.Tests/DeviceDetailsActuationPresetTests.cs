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

// Actuation as preset state: an edit is mirrored into device_actuation, Save Preset snapshots it,
// and a preset apply pushes it back to the board.
//
// The load-bearing rule, and the one worth breaking a build over: a preset with NO actuation rows
// writes NO actuation. Every preset saved before actuation was preset-scoped is in that state, so
// getting this wrong resets a tuned board the first time the user selects an old preset after
// upgrading. ApplyingAPresetWithNoActuationRows_WritesNoActuation is the guard.
//
// Devices are set via the plain Device property rather than SetDevice, and the actuation load is
// awaited directly (every fake completes synchronously) — SetDevice's other fire-and-forget chains
// outlive the test on thread-pool threads and can steal Dispatcher.UIThread's first-touch affinity
// from the RunJobs()-based tests elsewhere in this assembly (see AssemblyInfo.cs).
public class DeviceDetailsActuationPresetTests
{
	private const int CyrexVid = 10473;
	private const int CyrexPid = 12914;
	private const string DeviceKey = "";

	// Comfortably past the 400 ms actuation debounce, even on a loaded CI runner.
	private const int DebounceSettleMs = 800;

	private sealed class FakeOwnerProvider(string kind, string id) : ICurrentOwnerProvider
	{
		private readonly SettingsOwner _owner = new(kind, id);
		public SettingsOwner? GetCurrent() => _owner;
	}

	private sealed class FakeHid : IUsbHidDeviceService
	{
		public KeyActuationInfo? ActuationInfo { get; set; }
		// Lets a test make the board refuse a write, the way a failed verify does on real hardware.
		public bool ActuationWriteSucceeds { get; set; } = true;
		// Holds a global write "in flight" so a test can queue a second edit behind it — the state
		// PR #91's M1 lives in, and one a synchronously-completing fake can never reach.
		public TaskCompletionSource<bool>? WriteGate { get; set; }
		public List<(int Press, int Release, bool Rt, int QuickPress, int QuickRelease)> ActuationWrites { get; } = [];
		public List<(int[] Slots, int Press, int Release, bool Rt, int QuickPress, int QuickRelease)> PerKeyActuationWrites { get; } = [];

		public int TotalActuationWrites => ActuationWrites.Count + PerKeyActuationWrites.Count;

		public Task<KeyActuationInfo?> GetKeyActuationAsync(int vendorId, int productId) =>
			Task.FromResult(ActuationInfo);

		public Task<bool> SetKeyActuationAsync(int vendorId, int productId, int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm)
		{
			ActuationWrites.Add((pressCentiMm, releaseCentiMm, rapidTrigger, quickPressCentiMm, quickReleaseCentiMm));
			return WriteGate?.Task ?? Task.FromResult(ActuationWriteSucceeds);
		}

		public Task<bool> SetPerKeyActuationAsync(int vendorId, int productId, IReadOnlyCollection<int> slots, int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm)
		{
			PerKeyActuationWrites.Add(([.. slots], pressCentiMm, releaseCentiMm, rapidTrigger, quickPressCentiMm, quickReleaseCentiMm));
			return Task.FromResult(ActuationWriteSucceeds);
		}

		public List<HidDeviceResponse> GetDevices() => [];
		public Task StartCommunication() => Task.CompletedTask;
		public bool CheckIfAllDevicesAreDisconnected() => true;
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
		public Task<bool> SetMacrosAsync(int vendorId, int productId, string deviceKey, IReadOnlyList<(int Index, MacroRepeatMode Mode, IReadOnlyList<MacroEvent> Events, int RepeatCount)> entries) => Task.FromResult(true);
		public Task<bool> SetKeyMappingAsync(int vendorId, int productId, byte matrixIndex, byte type, byte code1, byte code2) => Task.FromResult(true);
		public Task<(IReadOnlyDictionary<byte, byte> ScancodeToSlot, IReadOnlyDictionary<byte, byte> UserOverrides)?> ProbeKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
			=> Task.FromResult<(IReadOnlyDictionary<byte, byte>, IReadOnlyDictionary<byte, byte>)?>(null);
		public Task<IReadOnlyDictionary<byte, (byte Type, byte Code1, byte Code2)>?> ReadKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
			=> Task.FromResult<IReadOnlyDictionary<byte, (byte, byte, byte)>?>(null);
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

	// ── Capture ──────────────────────────────────────────────────────────────

	[Fact]
	public async Task ActuationEdit_IsMirroredIntoTheLiveRows_AndMarksThePresetDirty()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			await OpenWithPreset(vm, store, hid, owner);
			Assert.Empty(Live(store, owner));   // nothing stored until the user actually tunes it

			vm.ActuationRapidTrigger = true;
			Thread.Sleep(DebounceSettleMs);
			WaitUntilLiveActuationStored(store, owner);

			var row = Assert.Single(Live(store, owner));
			Assert.True(row.RapidTrigger);
			Assert.True(vm.HasUnsavedPresetChanges);
		}
	}

	[Fact]
	public async Task SavePreset_CapturesActuation()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var (presetId, _) = await OpenWithPreset(vm, store, hid, owner);

			vm.ActuationRapidTrigger = true;
			Thread.Sleep(DebounceSettleMs);
			// The save below snapshots the live rows — they must have landed first.
			WaitUntilLiveActuationStored(store, owner);
			vm.SavePresetCommand.Execute(null);
			await vm.ConfirmSavePresetCommand.ExecuteAsync(null);

			var saved = Assert.Single(store.GetPresetActuation(presetId));
			Assert.True(saved.RapidTrigger);
		}
	}

	// Edit-then-undo: returning the control to the preset's saved value must turn Save Preset
	// back OFF. Actuation used to mark the preset dirty unconditionally (NotifyPresetChanged);
	// it now goes through the same live-vs-snapshot recompute as every other section.
	[Fact]
	public async Task EditingBackToTheSavedValue_ClearsThePresetDirtyFlag()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			await OpenWithPreset(vm, store, hid, owner);

			vm.ActuationRapidTrigger = true;
			Thread.Sleep(DebounceSettleMs);
			// The save below snapshots the live rows — they must have landed first.
			WaitUntilLiveActuationStored(store, owner);
			vm.SavePresetCommand.Execute(null);
			await vm.ConfirmSavePresetCommand.ExecuteAsync(null);
			Assert.False(vm.HasUnsavedPresetChanges);

			vm.ActuationRapidTrigger = false;
			Thread.Sleep(DebounceSettleMs);
			WaitUntilPresetDirty(vm);
			Assert.True(vm.HasUnsavedPresetChanges);

			vm.ActuationRapidTrigger = true;
			WaitUntilPresetClean(vm);
			Assert.False(vm.HasUnsavedPresetChanges);
		}
	}

	// ── Restore ──────────────────────────────────────────────────────────────

	// The Discard path ("go back → don't save"): an abandoned actuation edit must not stay on the
	// board. The selected preset holds NO actuation rows (nothing was ever saved under it), so the
	// revert cannot restore rows — it returns the board to the state the probe reported at page
	// open (which is the previously saved state, NOT the factory defaults: the probe here is
	// deliberately different from them), WITHOUT persisting: the live set stays empty and keeps
	// matching the empty snapshot, so the just-discarded preset does not read as dirty again.
	[Fact]
	public async Task DiscardWithoutSaving_ReturnsAnUnsetPresetsBoardToItsAtOpenState()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			// Board opens holding 1.50 / 1.40 mm with RT on — a previously tuned state, distinct
			// from the factory defaults (2.00 / 2.00 mm, RT off).
			await OpenWithPreset(vm, store, hid, owner, info: CyrexInfo() with
			{
				PressCentiMm = 150,
				ReleaseCentiMm = 140,
				RapidTrigger = true,
			});

			vm.SetActuationPressFromFraction(1.0);
			Thread.Sleep(DebounceSettleMs);
			Assert.Single(Live(store, owner));   // the edit landed in the live rows

			hid.ActuationWrites.Clear();
			vm.OpenUnsavedChangesForLeaving();
			await vm.DiscardUnsavedChangesCommand.ExecuteAsync(null);
			Thread.Sleep(DebounceSettleMs);

			// The board got its at-open state back — not the defaults...
			var write = Assert.Single(hid.ActuationWrites);
			Assert.Equal((150, 140, true, 10, 10), write);
			// ...while the live rows stayed empty, matching the preset's empty snapshot.
			Assert.Empty(Live(store, owner));
			WaitUntilPresetClean(vm);
			Assert.False(vm.HasUnsavedPresetChanges);
		}
	}

	// The counterpart guard: a PLAIN preset switch onto a rows-less preset must keep writing
	// nothing — the Discard reset above must not leak into the path that would otherwise reset a
	// tuned board the first time an old preset is selected after upgrading.
	[Fact]
	public async Task SwitchingToAPresetWithNoActuationRows_StillWritesNoActuation()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var (_, _) = await OpenWithPreset(vm, store, hid, owner, "A");
			var bId = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid,
				new PresetRecord { Name = "B" });
			var bRow = Row(bId, "B");
			vm.Presets.Add(bRow);

			hid.ActuationWrites.Clear();
			hid.PerKeyActuationWrites.Clear();
			await vm.SelectPresetCommand.ExecuteAsync(bRow);

			Assert.Equal(0, hid.TotalActuationWrites);
		}
	}

	[Fact]
	public async Task SwitchingPresets_RestoresActuation()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var (aId, aRow) = await OpenWithPreset(vm, store, hid, owner, "A");

			// A carries a tuned board.
			store.ReplaceDeviceActuation(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
				[new ActuationRecord(ActuationRecord.AllKeysSlot, 120, 130, true, 5, 6)]);
			store.SavePresetSnapshot(aId, LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey);

			// B carries a different one.
			var bId = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid,
				new PresetRecord { Name = "B" });
			var bRow = Row(bId, "B");
			vm.Presets.Add(bRow);
			store.ReplaceDeviceActuation(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
				[new ActuationRecord(ActuationRecord.AllKeysSlot, 300, 310, false, 20, 21)]);
			store.SavePresetSnapshot(bId, LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey);

			hid.ActuationWrites.Clear();
			await vm.SelectPresetCommand.ExecuteAsync(aRow);

			// The board was written A's values, and the sliders followed.
			Assert.Contains(hid.ActuationWrites, w => w.Press == 120 && w.Release == 130 && w.Rt);
			Assert.Equal(1.20, vm.ActuationPressMm, 3);
			Assert.True(vm.ActuationRapidTrigger);

			hid.ActuationWrites.Clear();
			await vm.SelectPresetCommand.ExecuteAsync(bRow);

			Assert.Contains(hid.ActuationWrites, w => w.Press == 300 && w.Release == 310 && !w.Rt);
			Assert.Equal(3.00, vm.ActuationPressMm, 3);
		}
	}

	// THE migration guard. A preset saved before actuation was preset-scoped holds no actuation
	// rows; applying it must leave the board's own values completely alone. If this ever fails,
	// every upgrading user with a tuned board loses their settings on the first preset click.
	[Fact]
	public async Task ApplyingAPresetWithNoActuationRows_WritesNoActuation()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var (presetId, row) = await OpenWithPreset(vm, store, hid, owner);
			// Saved with no actuation captured — an "old" preset.
			store.SavePresetSnapshot(presetId, LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey);
			Assert.Empty(store.GetPresetActuation(presetId));

			hid.ActuationWrites.Clear();
			hid.PerKeyActuationWrites.Clear();
			await vm.SelectPresetCommand.ExecuteAsync(row);

			Assert.Equal(0, hid.TotalActuationWrites);
		}
	}

	[Fact]
	public async Task PerKeyActuation_SurvivesTheRoundTrip_WithoutFlattening()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var (presetId, row) = await OpenWithPreset(vm, store, hid, owner);

			// Two keys deliberately tuned differently.
			store.ReplaceDeviceActuation(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
			[
				new ActuationRecord(3, 150, 160, true, 20, 25),
				new ActuationRecord(7, 240, 250, false, 10, 15),
			]);
			store.SavePresetSnapshot(presetId, LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey);

			hid.PerKeyActuationWrites.Clear();
			await vm.SelectPresetCommand.ExecuteAsync(row);

			// Two distinct value sets, each targeting its own slot — not one uniform board write.
			Assert.Equal(2, hid.PerKeyActuationWrites.Count);
			var slot3 = hid.PerKeyActuationWrites.Single(w => w.Slots.Contains(3));
			var slot7 = hid.PerKeyActuationWrites.Single(w => w.Slots.Contains(7));
			Assert.Equal(150, slot3.Press);
			Assert.True(slot3.Rt);
			Assert.Equal(240, slot7.Press);
			Assert.False(slot7.Rt);
			Assert.Empty(hid.ActuationWrites);   // never collapsed to a global write
		}
	}

	// Slots that share a value go out in ONE call — a uniformly tuned 100-key board must not cost
	// 100 device writes on every preset switch.
	[Fact]
	public async Task SlotsSharingAValue_AreWrittenInOneCall()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var (presetId, row) = await OpenWithPreset(vm, store, hid, owner);

			store.ReplaceDeviceActuation(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
			[
				new ActuationRecord(1, 150, 160, true, 20, 25),
				new ActuationRecord(2, 150, 160, true, 20, 25),
				new ActuationRecord(3, 150, 160, true, 20, 25),
			]);
			store.SavePresetSnapshot(presetId, LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey);

			hid.PerKeyActuationWrites.Clear();
			await vm.SelectPresetCommand.ExecuteAsync(row);

			var write = Assert.Single(hid.PerKeyActuationWrites);
			Assert.Equal([1, 2, 3], [.. write.Slots.OrderBy(s => s)]);
		}
	}

	// ── Blocker regressions (round 1) ────────────────────────────────────────

	// B1. The actuation push lives in ApplyCurrentStateToDeviceCoreAsync, which is ALSO where the
	// 250 ms staged-edit debounce lands — so an RGB or key-remap edit was dragging a full actuation
	// write along with it. On T800 that write is a flash commit with a 300 ms settle, and the
	// actuation queue's own coalescing exists precisely to avoid repeated flash commits. Changing a
	// colour must cost nothing on the actuation path.
	[Fact]
	public async Task AStagedRgbEdit_DoesNotWriteActuation()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var (presetId, _) = await OpenWithPreset(vm, store, hid, owner);

			// The board is tuned, so there ARE rows to push — without this the test would pass on
			// the no-opinion early-out instead of on the fix.
			store.ReplaceDeviceActuation(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
				[new ActuationRecord(ActuationRecord.AllKeysSlot, 120, 130, true, 5, 6)]);
			store.SavePresetSnapshot(presetId, LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey);

			hid.ActuationWrites.Clear();
			hid.PerKeyActuationWrites.Clear();

			// A staged edit through a path the user actually has: clicking an RGB effect tile.
			vm.RgbEffects.First(e => e.EffectKey == "Static").SelectCommand.Execute(null);
			Thread.Sleep(DebounceSettleMs);

			Assert.Equal(0, hid.TotalActuationWrites);
		}
	}

	// B2. A preset apply supersedes an actuation edit still inside its 400 ms debounce. Before the
	// fix the stale edit fired AFTER the switch: it wrote the outgoing preset's value to the board,
	// then persisted it into the incoming preset's live rows and marked that preset dirty with a
	// change the user never made.
	[Fact]
	public async Task APendingActuationEdit_DoesNotFollowTheUserOntoTheNextPreset()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var (aId, aRow) = await OpenWithPreset(vm, store, hid, owner, "A");

			var bId = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid,
				new PresetRecord { Name = "B" });
			var bRow = Row(bId, "B");
			vm.Presets.Add(bRow);
			store.ReplaceDeviceActuation(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
				[new ActuationRecord(ActuationRecord.AllKeysSlot, 300, 310, false, 20, 21)]);
			store.SavePresetSnapshot(bId, LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey);
			store.ReplaceDeviceActuation(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey, []);

			// Arm an edit under A, then switch to B well inside the debounce window.
			vm.ActuationRapidTrigger = true;
			await vm.SelectPresetCommand.ExecuteAsync(bRow);

			// Let the abandoned debounce fire if it is still armed.
			Thread.Sleep(DebounceSettleMs);

			// B's own values are what the board and the stores hold — the abandoned edit reached
			// neither.
			var live = Assert.Single(Live(store, owner));
			Assert.Equal(300, live.PressCentiMm);
			Assert.False(live.RapidTrigger);
			Assert.DoesNotContain(hid.ActuationWrites, w => w.Rt && w.Press != 300);
			WaitUntilPresetClean(vm);
			Assert.False(vm.HasUnsavedPresetChanges);
		}
	}

	// A board that refuses the preset's values must say so, not leave the sliders asserting values
	// the hardware rejected. Same treatment a refused slider edit already gets: banner up, re-read.
	[Fact]
	public async Task APresetPushTheBoardRefuses_RaisesTheBanner_AndDoesNotShowThePresetsValues()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var (presetId, row) = await OpenWithPreset(vm, store, hid, owner);
			store.ReplaceDeviceActuation(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
				[new ActuationRecord(ActuationRecord.AllKeysSlot, 120, 130, true, 5, 6)]);
			store.SavePresetSnapshot(presetId, LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey);

			hid.ActuationWriteSucceeds = false;
			await vm.SelectPresetCommand.ExecuteAsync(row);

			Assert.True(vm.ActuationWriteFailed);
			// The re-read put the board's own value back on the sliders, not the preset's 1.20 mm.
			Assert.Equal(2.00, vm.ActuationPressMm, 3);
		}
	}

	// M1 (PR #91 review). An edit that has left the debounce but is QUEUED behind an in-flight write
	// survives both the debounce cancel and the generation fence. Before the fix it drained on the
	// next edit — writing the outgoing preset's value to the board and into the incoming preset's
	// live rows, long after the switch.
	[Fact]
	public async Task AQueuedActuationEdit_DoesNotDrainOntoTheNextPreset()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var (aId, aRow) = await OpenWithPreset(vm, store, hid, owner, "A");

			var bId = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid,
				new PresetRecord { Name = "B" });
			var bRow = Row(bId, "B");
			vm.Presets.Add(bRow);
			store.ReplaceDeviceActuation(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
				[new ActuationRecord(ActuationRecord.AllKeysSlot, 300, 310, false, 20, 21)]);
			store.SavePresetSnapshot(bId, LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey);
			store.ReplaceDeviceActuation(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey, []);

			// First edit under A: hold its write in flight so the second one has to queue.
			var gate = new TaskCompletionSource<bool>();
			hid.WriteGate = gate;
			vm.SetActuationPressFromFraction(0.1);
			Thread.Sleep(DebounceSettleMs);

			// Second edit under A — queues behind the in-flight write, and is past its own debounce.
			hid.WriteGate = null;
			vm.SetActuationReleaseFromFraction(0.9);
			Thread.Sleep(DebounceSettleMs);

			// Switch to B while the first write is still in flight and the second sits in the queue.
			await vm.SelectPresetCommand.ExecuteAsync(bRow);
			gate.SetResult(true);
			Thread.Sleep(DebounceSettleMs);

			// An edit on B must not drag A's queued release depth onto the board with it.
			hid.ActuationWrites.Clear();
			vm.SetActuationPressFromFraction(0.5);
			Thread.Sleep(DebounceSettleMs);

			var live = Assert.Single(Live(store, owner));
			Assert.Equal(310, live.ReleaseCentiMm);   // B's release, never A's
			Assert.DoesNotContain(hid.ActuationWrites, w => w.Release != 310);
		}
	}

	// m1 (PR #91 review). Live rows hold exactly what the board last accepted, so Save Preset was
	// re-writing values already in place — on T800 a full flash commit per click of Save.
	[Fact]
	public async Task SavePreset_DoesNotRewriteActuationTheBoardAlreadyHolds()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			await OpenWithPreset(vm, store, hid, owner);

			vm.ActuationRapidTrigger = true;
			Thread.Sleep(DebounceSettleMs);
			Assert.Equal(1, hid.TotalActuationWrites);   // the edit itself

			hid.ActuationWrites.Clear();
			hid.PerKeyActuationWrites.Clear();
			vm.SavePresetCommand.Execute(null);
			await vm.ConfirmSavePresetCommand.ExecuteAsync(null);

			Assert.Equal(0, hid.TotalActuationWrites);
		}
	}

	// m2 (PR #91 review). Preset Reset wrote the defaults itself and then the full-state push wrote
	// the very same rows again — two flash commits per Reset on T800.
	[Fact]
	public async Task ResetPreset_WritesActuationToTheBoardOnlyOnce()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var (presetId, _) = await OpenWithPreset(vm, store, hid, owner);
			store.ReplaceDeviceActuation(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
				[new ActuationRecord(ActuationRecord.AllKeysSlot, 120, 130, true, 5, 6)]);
			store.SavePresetSnapshot(presetId, LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey);

			hid.ActuationWrites.Clear();
			await vm.ResetActuationToDefaultAsync();
			await vm.ApplyStoredActuationToDeviceAsync();

			Assert.Single(hid.ActuationWrites);
		}
	}

	// m3 (PR #91 review). A refused push used to leave device_actuation holding the preset's values,
	// so PresetMatchesLiveState called the preset clean while the board held something else.
	[Fact]
	public async Task APresetPushTheBoardRefuses_LeavesTheStoresAgreeingWithTheBoard()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var (presetId, row) = await OpenWithPreset(vm, store, hid, owner);
			store.ReplaceDeviceActuation(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
				[new ActuationRecord(ActuationRecord.AllKeysSlot, 120, 130, true, 5, 6)]);
			store.SavePresetSnapshot(presetId, LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey);

			hid.ActuationWriteSucceeds = false;
			await vm.SelectPresetCommand.ExecuteAsync(row);

			Assert.True(vm.ActuationWriteFailed);
			// The live rows follow the board (2.00 mm from the probe), not the refused preset.
			var live = Assert.Single(Live(store, owner));
			Assert.Equal(200, live.PressCentiMm);
			Assert.False(live.RapidTrigger);
		}
	}

	// ── Boards without actuation ─────────────────────────────────────────────

	[Fact]
	public async Task UnsupportedBoard_NeverWritesActuation()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var (presetId, row) = await OpenWithPreset(vm, store, hid, owner, supportsActuation: false);

			// Even with rows somehow present for this device key, a board that declares no actuation
			// must never be written — the capability flag is the gate, not the data.
			store.ReplaceDeviceActuation(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
				[new ActuationRecord(ActuationRecord.AllKeysSlot, 120, 130, true, 5, 6)]);
			store.SavePresetSnapshot(presetId, LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey);

			hid.ActuationWrites.Clear();
			hid.PerKeyActuationWrites.Clear();
			await vm.SelectPresetCommand.ExecuteAsync(row);

			Assert.Equal(0, hid.TotalActuationWrites);
		}
	}

	// ── Reset ────────────────────────────────────────────────────────────────

	// Reset must both push the board's own defaults AND land them in the snapshot. The snapshot half
	// is the one that can silently regress: the caller snapshots the live rows the moment the reset
	// returns, so a reset that went through the 400 ms slider debounce would be captured too late
	// and the preset would keep the old values.
	[Fact]
	public async Task ResetPreset_RestoresDeviceDefaultActuation_AndSavesItIntoThePreset()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var (presetId, _) = await OpenWithPreset(vm, store, hid, owner);

			store.ReplaceDeviceActuation(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
				[new ActuationRecord(ActuationRecord.AllKeysSlot, 120, 130, true, 5, 6)]);
			store.SavePresetSnapshot(presetId, LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey);

			hid.ActuationWrites.Clear();
			await vm.ResetActuationToDefaultAsync();

			// The board got the defaults the probe reported (2.00 mm, RT off).
			var write = Assert.Single(hid.ActuationWrites);
			Assert.Equal(200, write.Press);
			Assert.Equal(200, write.Release);
			Assert.False(write.Rt);

			// And they reached the live rows synchronously, so a snapshot taken now captures them.
			var live = Assert.Single(Live(store, owner));
			Assert.Equal(200, live.PressCentiMm);
			Assert.False(live.RapidTrigger);
		}
	}

	// ── Helpers ──────────────────────────────────────────────────────────────

	// Polls for the dirty flag to clear instead of a fixed sleep — the same deflake the
	// change→revert tests in DeviceDetailsPresetApplyTests got in PR #96. The 800 ms settle
	// is calibrated for a quiet machine; a loaded CI runner can take longer and fail the run
	// on timing alone (PR #101 run 31783436857 — EditingBackToTheSavedValue). Only
	// test-ENDING clean asserts go through this: a poll returns the moment the condition
	// holds, so using it mid-test would shorten the settle window the fixed sleeps give the
	// steps that follow.
	private static void WaitUntilPresetClean(DeviceDetailsViewModel vm, int timeoutMs = 5000)
	{
		var deadline = Environment.TickCount64 + timeoutMs;
		while (vm.HasUnsavedPresetChanges && Environment.TickCount64 < deadline)
			Thread.Sleep(50);
	}

	// The same deflake for the OTHER side of the debounce: the capture tests need the
	// debounced live-row write to have LANDED (in device_actuation) before Save Preset
	// copies it or an assert reads it, and the fixed settle is a bet a loaded CI runner
	// already lost once (PR #104 run 31800794207 — SavePreset_CapturesActuation). Used
	// AFTER the fixed sleep, never instead of it: the sleep still gives the surrounding
	// steps their full settle window, the poll only extends it on a slow runner.
	private static void WaitUntilLiveActuationStored(
		LocalStorageService store, string owner, int timeoutMs = 5000)
	{
		var deadline = Environment.TickCount64 + timeoutMs;
		while (Live(store, owner).Count == 0 && Environment.TickCount64 < deadline)
			Thread.Sleep(50);
	}

	// The third side of the same debounce. WaitUntilPresetClean covers "became clean" and
	// WaitUntilLiveActuationStored covers "the row landed"; this covers "became DIRTY", which
	// was still on a bare fixed sleep and lost the same bet on a loaded runner (Release CI,
	// 2026-08-26 — EditingBackToTheSavedValue again, this time on the Assert.True at the
	// revert step rather than the clean assert at the end).
	//
	// Used AFTER the fixed sleep, never instead of it, for the reason
	// WaitUntilLiveActuationStored gives: the sleep keeps the full settle window for the steps
	// that follow, and the poll only extends it when the runner is slow.
	private static void WaitUntilPresetDirty(DeviceDetailsViewModel vm, int timeoutMs = 5000)
	{
		var deadline = Environment.TickCount64 + timeoutMs;
		while (!vm.HasUnsavedPresetChanges && Environment.TickCount64 < deadline)
			Thread.Sleep(50);
	}

	// A Cyrex-shaped probe: press 0.20-3.50 mm, RT 0.01-2.50 mm, 0.01 mm step, defaults at 2.00 mm.
	// PerKeyValues is what makes Per-key mode available at all.
	private static KeyActuationInfo CyrexInfo(IReadOnlyDictionary<int, KeyActuationKeyValues>? perKey = null) => new(
		Supported: true,
		PressCentiMm: 200, ReleaseCentiMm: 200, RapidTrigger: false,
		QuickPressCentiMm: 10, QuickReleaseCentiMm: 10,
		PressMinCentiMm: 20, PressMaxCentiMm: 350,
		RtMinCentiMm: 1, RtMaxCentiMm: 250,
		StepMicrometres: 10,
		DefaultPressCentiMm: 200, DefaultReleaseCentiMm: 200, DefaultRapidTrigger: false,
		DefaultQuickPressCentiMm: 10, DefaultQuickReleaseCentiMm: 10)
	{
		PerKeyValues = perKey ?? new Dictionary<int, KeyActuationKeyValues>(),
	};

	private static (DeviceDetailsViewModel Vm, LocalStorageService Store, FakeHid Hid, string Owner) NewVm()
	{
		var owner = Guid.NewGuid().ToString("D");
		var store = new LocalStorageService("Data Source=:memory:");
		var hid = new FakeHid();
		var vm = new DeviceDetailsViewModel(
			usbHidDeviceService: hid,
			localStorage: store,
			settings: null,
			ownerProvider: new FakeOwnerProvider(LocalStorageService.OwnerKindGuest, owner));
		vm.PostToUi = action => action();
		return (vm, store, hid, owner);
	}

	private static SingleCardViewModel CyrexCard(bool supportsActuation = true) => new()
	{
		Name = "Cyrex",
		DeviceTypeLabel = "Wired Keyboard",
		VendorId = CyrexVid,
		ProductId = CyrexPid,
		SettingsVendorId = CyrexVid,
		SettingsProductId = CyrexPid,
		DeviceKey = DeviceKey,
		SettingsDeviceKey = DeviceKey,
		SupportsActuation = supportsActuation,
	};

	private static PresetViewModel Row(int id, string name) => new() { Id = id, Name = name };

	private static List<ActuationRecord> Live(LocalStorageService s, string owner) =>
		s.GetDeviceActuation(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey);

	// Opens the board with a preset row selected, exactly as the page does.
	private static async Task<(int PresetId, PresetViewModel RowVm)> OpenWithPreset(
		DeviceDetailsViewModel vm, LocalStorageService store, FakeHid hid, string owner,
		string name = "A", bool supportsActuation = true,
		IReadOnlyDictionary<int, KeyActuationKeyValues>? perKey = null,
		KeyActuationInfo? info = null)
	{
		hid.ActuationInfo = info ?? CyrexInfo(perKey);
		var id = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid,
			new PresetRecord { Name = name });
		var row = Row(id, name);
		vm.Presets.Add(row);
		vm.Device = CyrexCard(supportsActuation);
		await vm.LoadActuationFromDeviceAsync();
		await vm.SelectPresetCommand.ExecuteAsync(row);
		return (id, row);
	}
}
