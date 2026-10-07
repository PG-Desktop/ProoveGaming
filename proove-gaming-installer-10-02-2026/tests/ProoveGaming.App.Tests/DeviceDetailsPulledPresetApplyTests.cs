using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using ProoveGaming.App.Models.Keyboards;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// Materializing a preset the sign-in pull brought down (docs/plans/add-extended-keys-api-sync).
// The pull writes device_applied_preset and a pending-apply mark but holds no unit, so nothing has
// copied the preset's snapshot into a unit's live rows or pushed it to the hardware. These cover
// the two paths that DO hold a unit and consume the mark once: the details page's open chain
// (MaterializePulledPresetAsync, where the extended keys and the macro bindings ride along) and the
// connect-time reapply (mice).
//
// A file of its own rather than more cases in DeviceDetailsPresetApplyTests: everything here runs
// under a USER owner (a guest never pulls, so the mark is never consumed for one), and the fake HID
// has to record SetExtendedKeys and SetMacros, which that file's fake deliberately does not.
//
// Same harness conventions as the actuation / extended-keys VM tests: the card goes in through
// SetDeviceAsync (or straight into ReapplyOnConnectAsync), the load is awaited directly, and
// PostToUi runs inline so nothing depends on the process-wide dispatcher.
//
// It has since grown a second tenant, for the same harness reason it was split off in the first
// place: every case that needs a real SetDeviceAsync with TWO keyboards and a chain parked mid-open
// — the self-heal's device-switch fences, and the extended-keys probe window at the bottom of this
// file — needs the per-board Info, the Aress card and the ProbeEntered / ActuationEntered gates
// that only this fake has. Duplicating them into DeviceDetailsExtendedKeysViewModelTests (whose
// harness deliberately bypasses SetDeviceAsync) would be a third copy of a 130-line fake.
public class DeviceDetailsPulledPresetApplyTests
{
	private const int CyrexVid = 10473;
	private const int CyrexPid = 12914;
	// The second extended-keys keyboard, for the cross-device cases. Same vendor as the Cyrex —
	// they really do share one — so every assertion about "which board" is on the PRODUCT id.
	private const int AressVid = 10473;
	private const int AressPid = 12910;
	private const int MouseVid = 0x04D9;
	private const int MousePid = 0xA23F;
	private const string DeviceKey = "";
	private const string User = LocalStorageService.OwnerKindUser;
	private const string Guest = LocalStorageService.OwnerKindGuest;

	private const int SlotA = 31;
	private const int SlotD = 33;
	private const int SlotCaps = 30;
	// Deliberately outside the extended-key records above: a slot an extended key claims is skipped
	// by the macro resync, which would make the macro-binding case pass for the wrong reason.
	private const int SlotQ = 16;
	// Q's partner for the Aress's own record in the probe-window cases — the pair has to sit on
	// slots the Cyrex's record does not touch for "the stale set says this slot is free" to be the
	// thing under test.
	private const int SlotW = 17;

	// The remap cases. Both accounts remap the SAME key — slot 32, S, factory scancode 0x16 — to a
	// different target, so which account's record reached the board is decidable from the record.
	private const byte SlotS = 32;
	private const byte F5 = 0x3E;
	private const byte F6 = 0x3F;
	// Smart Infinite (Cyrex / Aress / Rampage) stores a plain key as {0x10, bitmask, scancode}.
	private const byte KeyRecordType = 0x10;

	// The macro-binding cases bind DIFFERENT keys per account: each account's macro sits at slot 0
	// of its OWN library, so two bindings of the same key would produce identical device records
	// and "whose binding is on the board" would not be answerable. Slot 34 is F.
	private const byte SlotF = 34;

	private sealed class FakeOwnerProvider(string kind, string id) : ICurrentOwnerProvider
	{
		// Settable: the account-switch cases move the owner under a LIVE view model, which is what a
		// sign-out-and-in does to the singleton the App holds. Every other case here leaves it alone.
		public SettingsOwner? Owner { get; set; } = new(kind, id);
		public SettingsOwner? GetCurrent() => Owner;
	}

	// Records the writes these tests judge: the extended-keys push and the macro-library upload
	// (keyboards), and the coordinated configuration frame (mice). Everything else answers
	// "nothing to report".
	private sealed class FakeHid : IUsbHidDeviceService
	{
		public ExtendedKeysInfo? Info { get; set; }
		public bool ExtendedKeysWriteSucceeds { get; set; } = true;
		// The target rides with the entries: a cross-device push is only visible if the write says
		// which board it went to (see the device-switch case).
		public List<(int VendorId, int ProductId, List<ExtendedKeyRecord> Entries)> ExtendedKeysWrites { get; } = [];

		// Every macro-library upload, in order. An EMPTY payload is the observable of the
		// load-ordering bug the materialization used to have, so the list keeps them all rather
		// than only the last.
		public List<IReadOnlyList<(int Index, MacroRepeatMode Mode, IReadOnlyList<MacroEvent> Events, int RepeatCount)>> MacroUploads { get; } = [];

		// Every key-record write, in order. Both a key remap and a key→macro binding reach a Smart
		// Infinite board through this one call, so "whose remap / whose binding did the board get"
		// is decidable from the records alone (see the account-switch cases).
		public List<(int VendorId, byte Slot, byte Type, byte Code1, byte Code2)> KeyMappingWrites { get; } = [];

		// The board's live key matrix. Left null (a declined read) for every case that does not care,
		// exactly as before; the remap and macro-binding cases set it, because the key push is a DIFF
		// against this baseline and without one it can only write the slots a persisted override
		// claims — which would make "the previous account's record came OFF the board" unobservable.
		// Mutated by the writes below, so the fake board really does end up holding what it accepted.
		public Dictionary<byte, (byte Type, byte Code1, byte Code2)>? KeyMatrix { get; set; }

		public int ApplyConfigCallCount { get; private set; }
		public (byte r, byte g, byte b, byte effect, byte brightness, byte speed)? LastLed { get; private set; }

		// Test seam for the device-switch case: the probe signals that the open's chain has reached
		// it (ProbeEntered) and then parks on ProbeGate, which is the step immediately before
		// MaterializePulledPresetAsync — the only place a switch can be landed deterministically.
		public TaskCompletionSource? ProbeEntered { get; set; }
		public Task? ProbeGate { get; set; }

		// The same seam one step EARLIER in the open chain, for the cases that hold a SECOND
		// device's open parked. The extended-keys probe is the wrong place to park that one:
		// LoadExtendedKeysFromDeviceAsync nulls _extendedKeysOnDevice and bumps its load
		// generation BEFORE it probes, so a device parked there has already erased the board
		// baseline the cross-device cases are about (and made the other chain's own load bail).
		// LoadActuationFromDeviceAsync runs immediately before it and touches none of that.
		// Only reached by a card with SupportsActuation — AressCard sets it for exactly this.
		public TaskCompletionSource? ActuationEntered { get; set; }
		public Task? ActuationGate { get; set; }

		// Per-BOARD extended-keys state, for the cases that have two keyboards in play: with one
		// shared Info the fake cannot say which board holds what, so "the write landed on the
		// OTHER board" stops being decidable. Empty for every other case, which keeps using Info.
		public Dictionary<(int VendorId, int ProductId), ExtendedKeysInfo?> BoardInfo { get; } = [];

		private ExtendedKeysInfo? BoardOf(int vendorId, int productId) =>
			BoardInfo.TryGetValue((vendorId, productId), out var board) ? board : Info;

		public async Task<ExtendedKeysInfo?> GetExtendedKeysAsync(int vendorId, int productId)
		{
			ProbeEntered?.TrySetResult();
			if (ProbeGate is { } gate) await gate;
			return BoardOf(vendorId, productId);
		}

		public Task<bool> SetExtendedKeysAsync(int vendorId, int productId, IReadOnlyList<ExtendedKeyRecord> entries)
		{
			ExtendedKeysWrites.Add((vendorId, productId, entries.ToList()));
			// The fake board keeps what it accepted, so a failure-reload sees the previous set.
			if (ExtendedKeysWriteSucceeds && BoardOf(vendorId, productId) is { } board)
			{
				var kept = board with { Entries = entries.ToList() };
				if (BoardInfo.ContainsKey((vendorId, productId)))
					BoardInfo[(vendorId, productId)] = kept;
				else
					Info = kept;
			}
			return Task.FromResult(ExtendedKeysWriteSucceeds);
		}

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
			LastLed = led;
			return Task.FromResult(true);
		}

		public async Task<KeyActuationInfo?> GetKeyActuationAsync(int vendorId, int productId)
		{
			ActuationEntered?.TrySetResult();
			if (ActuationGate is { } gate) await gate;
			// A declined probe, as before — the actuation section is not what these cases judge.
			return null;
		}
		public Task<bool> SetKeyActuationAsync(int vendorId, int productId, int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm) => Task.FromResult(false);
		public Task<bool> SetPerKeyActuationAsync(int vendorId, int productId, IReadOnlyCollection<int> slots, int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm) => Task.FromResult(false);
		public Task<IReadOnlyDictionary<byte, (byte Type, byte Code1, byte Code2)>?> ReadKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
			=> Task.FromResult<IReadOnlyDictionary<byte, (byte, byte, byte)>?>(KeyMatrix);
		public Task<bool> SetKeyMappingAsync(int vendorId, int productId, byte matrixIndex, byte type, byte code1, byte code2)
		{
			KeyMappingWrites.Add((vendorId, matrixIndex, type, code1, code2));
			if (KeyMatrix is not null)
				KeyMatrix[matrixIndex] = (type, code1, code2);
			return Task.FromResult(true);
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
		public Task<bool> SetMacrosAsync(int vendorId, int productId, string deviceKey, IReadOnlyList<(int Index, MacroRepeatMode Mode, IReadOnlyList<MacroEvent> Events, int RepeatCount)> entries)
		{
			MacroUploads.Add(entries);
			return Task.FromResult(true);
		}
		public Task<(IReadOnlyDictionary<byte, byte> ScancodeToSlot, IReadOnlyDictionary<byte, byte> UserOverrides)?> ProbeKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
			=> Task.FromResult<(IReadOnlyDictionary<byte, byte>, IReadOnlyDictionary<byte, byte>)?>(null);
		public bool TryGetLastDpiStage(int vendorId, int productId, string deviceKey, out int stageIndex) { stageIndex = 0; return false; }
		public bool TryGetLastReportRate(int vendorId, int productId, string deviceKey, out int hz) { hz = 0; return false; }

#pragma warning disable CS0067 // events required by the interface but unused in tests
		public event EventHandler<SubscribeOnDevicesResponse>? DeviceFound;
		public event EventHandler<DpiStageChange>? DpiStageChanged;
		public event EventHandler<ReportRateChange>? ReportRateChanged;
		public event EventHandler<BatteryChange>? BatteryChanged;
		public event EventHandler<string>? NotifyError;
		public event EventHandler<bool>? HostAvailabilityChanged;
#pragma warning restore CS0067
	}

	private static ExtendedKeysInfo Info(params ExtendedKeyRecord[] entries) =>
		new(Supported: true, Entries: entries, AreaBytes: 1024, FreeBytes: 1024 - entries.Length * 8, BlockedSlots: []);

	// A USER owner, not a guest: the mark is a sign-in-pull artefact and is never consumed for a
	// guest. Real SettingsRepository — the connect-time reapply refuses to run without one.
	private static (DeviceDetailsViewModel Vm, LocalStorageService Store, FakeHid Hid, string Owner) NewVm()
	{
		var (vm, store, hid, owner, _) = NewSwitchableVm();
		return (vm, store, hid, owner);
	}

	// The same harness with the owner provider handed back, for the cases that move the settings
	// owner mid-test. Kept as the one construction site so the two cannot drift apart.
	private static (DeviceDetailsViewModel Vm, LocalStorageService Store, FakeHid Hid, string Owner, FakeOwnerProvider OwnerProvider) NewSwitchableVm()
	{
		var owner = Guid.NewGuid().ToString("D");
		var store = new LocalStorageService("Data Source=:memory:");
		var hid = new FakeHid();
		var ownerProvider = new FakeOwnerProvider(User, owner);
		var vm = new DeviceDetailsViewModel(
			usbHidDeviceService: hid,
			localStorage: store,
			settings: new SettingsRepository(store, ownerProvider),
			ownerProvider: ownerProvider);
		// Inline rather than through the process-wide dispatcher — see the note in
		// DeviceDetailsPresetApplyTests.NewVm.
		vm.PostToUi = action => action();
		return (vm, store, hid, owner, ownerProvider);
	}

	// supportsMacros is opt-in: it turns on ResyncKeyMacroBindingsAsync, which is the whole point
	// of the macro-binding case below but pure noise for every other one.
	private static SingleCardViewModel CyrexCard(bool supportsMacros = false) => new()
	{
		Name = "Cyrex",
		DeviceTypeLabel = "Wired Keyboard",
		VendorId = CyrexVid,
		ProductId = CyrexPid,
		SettingsVendorId = CyrexVid,
		SettingsProductId = CyrexPid,
		DeviceKey = DeviceKey,
		SettingsDeviceKey = DeviceKey,
		SupportsExtendedKeys = true,
		SupportsMacros = supportsMacros,
	};

	// The other keyboard the cross-device cases switch to. SupportsActuation is on for one reason
	// only: it is what makes the open call GetKeyActuationAsync, the probe whose gate holds this
	// chain one step before its own extended-keys load (see FakeHid.ActuationGate). The fake
	// declines that probe, so nothing else in these tests changes because of it.
	private static SingleCardViewModel AressCard() => new()
	{
		Name = "Aress",
		DeviceTypeLabel = "Wired Keyboard",
		VendorId = AressVid,
		ProductId = AressPid,
		SettingsVendorId = AressVid,
		SettingsProductId = AressPid,
		DeviceKey = DeviceKey,
		SettingsDeviceKey = DeviceKey,
		SupportsExtendedKeys = true,
		SupportsActuation = true,
	};

	private static SingleCardViewModel MouseCard() => new()
	{
		Name = "Test Mouse",
		DeviceTypeLabel = "Wired Mouse",
		VendorId = MouseVid,
		ProductId = MousePid,
		SettingsVendorId = MouseVid,
		SettingsProductId = MousePid,
		DeviceKey = DeviceKey,
		SettingsDeviceKey = DeviceKey,
		SupportsRgb = true,
	};

	private static string PendingApplyKey(int vendorId, int productId) =>
		LocalStorageService.SyncMetaDeviceStatePendingApply(vendorId, productId);

	// The mark names the preset the pull adopted — the consumer materializes it only while that
	// preset is still the applied one, so the id has to be the real one, not a placeholder.
	private static void SetPendingApplyMark(
		LocalStorageService store, string owner, int vendorId, int productId, int presetId, string kind = User) =>
		store.SetSyncMeta(kind, owner, PendingApplyKey(vendorId, productId),
			LocalStorageService.SyncMetaPendingApplyValue(presetId));

	// `kind` is what makes "sync_meta is owner-KEYED" assertable: the guest case below reads the
	// same device type's mark under both kinds and only one of them is spent.
	private static string? PendingApplyMark(
		LocalStorageService store, string owner, int vendorId, int productId, string kind = User) =>
		store.GetSyncMeta(kind, owner, PendingApplyKey(vendorId, productId));

	// The exact state a sign-in pull leaves behind for a keyboard: a preset whose SNAPSHOT holds the
	// extended keys, an applied row naming it, and empty live rows — the preset was never applied on
	// this machine.
	private static (int PresetId, List<ExtendedKeyRecord> Records) SeedPulledKeyboardPreset(
		LocalStorageService store, string owner)
	{
		var presetId = store.InsertPreset(User, owner, CyrexVid, CyrexPid,
			new PresetRecord { Name = "Pulled", IsDefault = true });

		var records = new List<ExtendedKeyRecord>
		{
			ExtendedKeyRecord.RapidSnap(SlotA, SlotD),
			ExtendedKeyRecord.ModTap(SlotCaps, 300,
				new ExtendedKeyCodeRecord(0x10, 0x01, 0x00), new ExtendedKeyCodeRecord(0x10, 0x00, 0x29)),
		};
		store.ReplaceDeviceExtendedKeys(User, owner, CyrexVid, CyrexPid, DeviceKey, records);
		store.SavePresetSnapshot(presetId, User, owner, CyrexVid, CyrexPid, DeviceKey);
		// ...and back to what a machine that has never applied this preset actually holds.
		store.ReplaceDeviceExtendedKeys(User, owner, CyrexVid, CyrexPid, DeviceKey, []);

		store.SetAppliedPreset(User, owner, CyrexVid, CyrexPid, DeviceKey, presetId, markDirty: false);
		return (presetId, records);
	}

	// The same state for a keyboard whose pulled preset binds a library macro to a key. The macro
	// itself is an ordinary (pulled) library row; the BINDING lives only in the preset's snapshot,
	// because this machine has never applied the preset.
	private static (int PresetId, int MacroId) SeedPulledKeyboardPresetWithMacroBinding(
		LocalStorageService store, string owner)
	{
		var macroId = store.SaveMacro(User, owner, CyrexVid, CyrexPid, DeviceKey,
			new MacroRecord
			{
				Name = "Pulled macro",
				RepeatCount = 1,
				Mode = MacroRepeatMode.MacroPlayCount,
				Events = [new MacroEventRecord { KeyCode = 0x04, IsPress = true, DelayMs = 10 }],
			},
			markDirty: false);

		var presetId = store.InsertPreset(User, owner, CyrexVid, CyrexPid,
			new PresetRecord { Name = "Pulled", IsDefault = true });

		store.SetKeyMacroBinding(User, owner, CyrexVid, CyrexPid, DeviceKey, SlotQ, macroId);
		store.SavePresetSnapshot(presetId, User, owner, CyrexVid, CyrexPid, DeviceKey);
		store.DeleteKeyMacroBinding(User, owner, CyrexVid, CyrexPid, DeviceKey, SlotQ);

		store.SetAppliedPreset(User, owner, CyrexVid, CyrexPid, DeviceKey, presetId, markDirty: false);
		return (presetId, macroId);
	}

	private static List<ExtendedKeyRecord> LiveExtendedKeys(
		LocalStorageService store, string owner, int vendorId = CyrexVid, int productId = CyrexPid) =>
		store.GetDeviceExtendedKeys(User, owner, vendorId, productId, DeviceKey);

	// The state an account that HAS configured this board leaves behind, as opposed to the pulled
	// state above: the records sit in the unit's live rows, the preset the account saved holds the
	// same set in its snapshot, and that preset is recorded as applied. `records` empty is an
	// account whose own preset simply has no extended keys — a perfectly ordinary opinion, and the
	// one the account-switch case turns on.
	//
	// `kind` is User for every case but the guest one at the end of the file — Scope names
	// guest ↔ account in both directions, and a guest configures a board exactly the same way.
	//
	// `vendorId` / `productId` default to the Cyrex; the cross-device cases below
	// pass the Aress's so one owner can have a configured board of each.
	private static int SeedConfiguredKeyboardPreset(
		LocalStorageService store, string owner, IReadOnlyList<ExtendedKeyRecord> records, string kind = User,
		int vendorId = CyrexVid, int productId = CyrexPid)
	{
		var presetId = store.InsertPreset(kind, owner, vendorId, productId,
			new PresetRecord { Name = "Mine", IsDefault = true });

		store.ReplaceDeviceExtendedKeys(kind, owner, vendorId, productId, DeviceKey, records);
		store.SavePresetSnapshot(presetId, kind, owner, vendorId, productId, DeviceKey);
		store.SetAppliedPreset(kind, owner, vendorId, productId, DeviceKey, presetId, markDirty: false);
		return presetId;
	}

	// The remap twin of SeedConfiguredKeyboardPreset: this account's opinion about the board is a
	// KEY REMAP rather than an extended key. Same three artefacts an account that saved a preset on
	// this machine leaves behind — live rows, snapshot, applied row.
	private static int SeedConfiguredKeyboardRemap(
		LocalStorageService store, string owner, byte slot, byte targetCode)
	{
		var presetId = store.InsertPreset(User, owner, CyrexVid, CyrexPid,
			new PresetRecord { Name = "Mine", IsDefault = true });

		store.SetKeyBinding(User, owner, CyrexVid, CyrexPid, DeviceKey, slot, KeyRecordType, 0x00, targetCode);
		store.SavePresetSnapshot(presetId, User, owner, CyrexVid, CyrexPid, DeviceKey);
		store.SetAppliedPreset(User, owner, CyrexVid, CyrexPid, DeviceKey, presetId, markDirty: false);
		return presetId;
	}

	// ...and the key→macro twin. The macro is an ordinary library row for this owner; unlike the
	// PULLED seed above, the binding is in the live rows AND in the snapshot, because this account
	// really did apply its preset on this machine before the switch.
	private static (int PresetId, int MacroId) SeedConfiguredKeyboardMacroBinding(
		LocalStorageService store, string owner, int slot)
	{
		var macroId = store.SaveMacro(User, owner, CyrexVid, CyrexPid, DeviceKey,
			new MacroRecord
			{
				Name = "Mine",
				RepeatCount = 1,
				Mode = MacroRepeatMode.MacroPlayCount,
				Events = [new MacroEventRecord { KeyCode = 0x04, IsPress = true, DelayMs = 10 }],
			},
			markDirty: false);

		var presetId = store.InsertPreset(User, owner, CyrexVid, CyrexPid,
			new PresetRecord { Name = "Mine", IsDefault = true });

		store.SetKeyMacroBinding(User, owner, CyrexVid, CyrexPid, DeviceKey, slot, macroId);
		store.SavePresetSnapshot(presetId, User, owner, CyrexVid, CyrexPid, DeviceKey);
		store.SetAppliedPreset(User, owner, CyrexVid, CyrexPid, DeviceKey, presetId, markDirty: false);
		return (presetId, macroId);
	}

	// The factory record for one Cyrex slot, straight from the catalog the view model builds
	// PhysicalKeys from — {0x10, modifier mask, HID usage} — so "the slot went back to its factory
	// triplet" is asserted against the same source the push reads rather than a copied byte.
	private static (byte Type, byte Code1, byte Code2) FactoryRecord(byte slot)
	{
		var key = CyrexKeyCatalog.Keys.Single(k => k.MatrixIndex == slot);
		return (KeyRecordType, key.ModifierMask, key.HidUsageCode);
	}

	// The board's live key matrix as the firmware reports it: every key on its factory record, with
	// the departing account's records laid on top. The push diffs against this, so it is what makes
	// a reset of the OTHER account's slot visible.
	private static Dictionary<byte, (byte Type, byte Code1, byte Code2)> BoardMatrix(
		params (byte Slot, byte Type, byte Code1, byte Code2)[] held)
	{
		var matrix = CyrexKeyCatalog.Keys.ToDictionary(
			k => k.MatrixIndex, k => (KeyRecordType, k.ModifierMask, k.HidUsageCode));
		foreach (var (slot, type, code1, code2) in held)
			matrix[slot] = (type, code1, code2);
		return matrix;
	}

	// The account switch, as HomeViewModel's owner sweep performs it: the details view model is told
	// the owner changed (its per-owner cache purge) and the incoming owner becomes current. That is
	// the whole of it.
	//
	// The sweep used to ALSO write the pending-apply mark for every connected card, and this helper
	// used to do the same. It no longer does, because the mark's consumers answer it with
	// LoadPresetSnapshotToDevice — they overwrite the live rows with the preset's snapshot, which on
	// an ordinary switch destroys the incoming account's applied-but-unsaved state
	// (docs/plans/add-cyrex-extended-keys, review round 4, M1). What still gives the arriving account
	// its own board is what these cases exercise: the page open's own pushes and the extended-keys
	// self-heal. The mark itself is unchanged and still the sign-in pull's — SetPendingApplyMark
	// above writes it directly, exactly as SettingsSyncService does, for the cases about the pull.
	//
	// The reproducing case above spells the same steps out inline on purpose: it is the plan's
	// reproduction and reads better with nothing hidden behind a helper.
	private static void SwitchOwnerTo(
		DeviceDetailsViewModel vm, FakeOwnerProvider ownerProvider, string owner, string kind = User)
	{
		var incoming = new SettingsOwner(kind, owner);
		vm.OnOwnerChanged(incoming);
		ownerProvider.Owner = incoming;
	}

	// The mouse equivalent: the preset's own lighting is GREEN in its snapshot, while the live rows
	// hold RED — the colour a machine that never applied this preset would push. Which of the two
	// comes back out is what says whether the snapshot was materialized.
	private static int SeedPulledMousePreset(LocalStorageService store, string owner)
	{
		// The pulled preset has to be a CUSTOM one, so it needs a default alongside it: the
		// connect-time reapply deliberately skips a device sitting on its default preset, and
		// opening the page would otherwise promote a lone row to default
		// (LocalStorageService.CreateDefaultPresetIfNotExists heals a scope with no default).
		store.InsertPreset(User, owner, MouseVid, MousePid,
			new PresetRecord { Name = "Default", IsDefault = true });

		var presetId = store.InsertPreset(User, owner, MouseVid, MousePid,
			new PresetRecord { Name = "Pulled" });

		store.SaveRgbState(User, owner, MouseVid, MousePid, DeviceKey,
			isEnabled: true, selectedEffect: "Neon",
			effects: [new RgbEffectRecord { EffectKey = "Neon", Hex = "#00FF00", Brightness = 55, Speed = 33 }]);
		store.SavePresetSnapshot(presetId, User, owner, MouseVid, MousePid, DeviceKey);

		store.SaveRgbState(User, owner, MouseVid, MousePid, DeviceKey,
			isEnabled: true, selectedEffect: "Neon",
			effects: [new RgbEffectRecord { EffectKey = "Neon", Hex = "#FF0000", Brightness = 55, Speed = 33 }]);

		store.SetAppliedPreset(User, owner, MouseVid, MousePid, DeviceKey, presetId, markDirty: false);
		return presetId;
	}

	private static string? LiveRgbHex(LocalStorageService store, string owner) =>
		store.GetRgbState(User, owner, MouseVid, MousePid, DeviceKey)?
			.Effects.FirstOrDefault(e => e.EffectKey == "Neon")?.Hex;

	// ── The details page consumes the mark ────────────────────────────────────

	// The storage half of the acceptance criterion: opening the page consumes the mark, copies the
	// pulled preset's snapshot into this unit's live rows, and leaves Save Preset off (the rows the
	// user never touched must not read as unsaved changes).
	[Fact]
	public async Task KeyboardPage_OpenedWithThePendingApplyMark_MaterializesTheSnapshotAndClearsTheMark()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var (presetId, records) = SeedPulledKeyboardPreset(store, owner);
			SetPendingApplyMark(store, owner, CyrexVid, CyrexPid, presetId);
			hid.Info = Info(); // a board holding nothing yet

			await vm.SetDeviceAsync(CyrexCard());

			Assert.Equal(ExtendedKeyRecord.NormalizeSet(records), LiveExtendedKeys(store, owner));
			Assert.Null(PendingApplyMark(store, owner, CyrexVid, CyrexPid));
			Assert.False(vm.HasUnsavedPresetChanges);
		}
	}

	// The hardware half of the same acceptance criterion, deliberately its own test: "the board
	// receives one SetExtendedKeys with P's records, with no click"
	// (docs/plans/add-extended-keys-api-sync, acceptance criterion 3).
	//
	// This is the test that says B1 is closed. It failed until the fix pass moved the consumption
	// out of LoadPresetsForDeviceAsync into SetDeviceAsync's follow-up chain: run before the
	// extended-keys probe, ApplyStoredExtendedKeysToDeviceAsync returns at its
	// _extendedKeysSupported != true guard and writes nothing, and the mark is already spent so
	// nothing pushes them later either.
	[Fact]
	public async Task KeyboardPage_OpenedWithThePendingApplyMark_PushesTheSnapshotsExtendedKeysOnce()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var (presetId, records) = SeedPulledKeyboardPreset(store, owner);
			SetPendingApplyMark(store, owner, CyrexVid, CyrexPid, presetId);
			hid.Info = Info(); // a board holding nothing yet

			await vm.SetDeviceAsync(CyrexCard());

			var write = Assert.Single(hid.ExtendedKeysWrites);
			Assert.Equal(CyrexVid, write.VendorId);
			Assert.Equal(ExtendedKeyRecord.NormalizeSet(records), write.Entries);
		}
	}

	// The mark is consumed once and only once: the second open of the same page must not rewrite the
	// board (flash wear, and a second of HID traffic on every page visit).
	//
	// Asserted against the write the FIRST open really makes, not against a count-vs-count that
	// held while no keyboard case wrote anything at all (review round 1, Tests): the single write
	// has to be there after open one and still be the only one after open two.
	[Fact]
	public async Task KeyboardPage_OpenedASecondTime_DoesNotWriteAgain()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var (presetId, records) = SeedPulledKeyboardPreset(store, owner);
			SetPendingApplyMark(store, owner, CyrexVid, CyrexPid, presetId);
			hid.Info = Info();

			await vm.SetDeviceAsync(CyrexCard());
			var firstWrite = Assert.Single(hid.ExtendedKeysWrites);
			Assert.Equal(ExtendedKeyRecord.NormalizeSet(records), firstWrite.Entries);

			await vm.SetDeviceAsync(CyrexCard());

			Assert.Same(firstWrite.Entries, Assert.Single(hid.ExtendedKeysWrites).Entries);
			Assert.Equal(ExtendedKeyRecord.NormalizeSet(records), LiveExtendedKeys(store, owner));
			Assert.Null(PendingApplyMark(store, owner, CyrexVid, CyrexPid));
		}
	}

	// No mark (the ordinary case — a preset applied on this very machine): the open is silent. This
	// is the regression guard for "opening a page rewrites the board".
	//
	// Since the self-heal landed (2026-09-11) the silence has a second reason worth naming, because
	// it is what the seed below arranges: the board already agrees with the owner's rows. An open
	// whose board DISAGREES is supposed to write — that is the whole point of the self-heal — so this
	// case guards "no gratuitous rewrite", not "page opens never write".
	[Fact]
	public async Task KeyboardPage_OpenedWithoutTheMark_WritesNothingToTheBoard()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			SeedPulledKeyboardPreset(store, owner);
			hid.Info = Info();

			await vm.SetDeviceAsync(CyrexCard());

			Assert.Empty(hid.ExtendedKeysWrites);
			// The live rows stay as they were — no snapshot was loaded over them.
			Assert.Empty(LiveExtendedKeys(store, owner));
		}
	}

	// The mark is cleared BEFORE the device write, so a board that refuses the set cannot re-trigger
	// the MATERIALIZATION on every open — the snapshot is loaded once and the preset stays selected.
	// The write is genuinely attempted and genuinely refused here, so this covers the refusal itself
	// and not just the clear-before-apply ordering.
	//
	// The "and never writes again" half this case used to assert was superseded on 2026-09-11 by the
	// self-heal (docs/plans/fix-extended-keys-self-heal): a board that disagrees with the signed-in
	// owner's rows is now corrected at every page open, and a refusing board is exactly such a board.
	// What bounds it is ONE attempt per open, not none ever again — never retrying would leave a
	// board that recovers stuck wearing the wrong set until the user re-applied a preset by hand.
	[Fact]
	public async Task KeyboardPage_DeviceWriteFails_ClearsTheMarkAndAttemptsAtMostOncePerOpen()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var (presetId, records) = SeedPulledKeyboardPreset(store, owner);
			SetPendingApplyMark(store, owner, CyrexVid, CyrexPid, presetId);
			hid.Info = Info();
			hid.ExtendedKeysWriteSucceeds = false;

			await vm.SetDeviceAsync(CyrexCard());

			// Exactly one attempt: the materialization's, with the self-heal declining to follow a
			// refusal it would only repeat.
			Assert.Single(hid.ExtendedKeysWrites);
			Assert.True(vm.ExtendedKeysWriteFailed);
			Assert.Null(PendingApplyMark(store, owner, CyrexVid, CyrexPid));

			await vm.SetDeviceAsync(CyrexCard());

			// One more attempt, and only one — the self-heal's, because the board still disagrees.
			Assert.Equal(2, hid.ExtendedKeysWrites.Count);
			// It is the owner's own set that keeps being offered, not the board's: the failed push
			// never wrote the board's records into the owner's rows, so the snapshot the first open
			// materialized is still what the live rows hold.
			Assert.Equal(ExtendedKeyRecord.NormalizeSet(records), hid.ExtendedKeysWrites[^1].Entries);
			Assert.Equal(ExtendedKeyRecord.NormalizeSet(records), LiveExtendedKeys(store, owner));
		}
	}

	// A mark belongs to the account that pulled it: the consumption is keyed on the owner, not on
	// "is the key there for this device type". Asserted on the live rows and on the other account's
	// mark as well as on the board — the board alone was a vacuous assertion while no keyboard case
	// wrote anything (review round 1, Tests).
	[Fact]
	public async Task KeyboardPage_MarkUnderADifferentOwner_IsNotConsumed()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var (presetId, _) = SeedPulledKeyboardPreset(store, owner);
			// Another account's mark for the same device type.
			var otherOwner = Guid.NewGuid().ToString("D");
			store.SetSyncMeta(User, otherOwner, PendingApplyKey(CyrexVid, CyrexPid),
				LocalStorageService.SyncMetaPendingApplyValue(presetId));
			hid.Info = Info();

			await vm.SetDeviceAsync(CyrexCard());

			Assert.Empty(hid.ExtendedKeysWrites);
			// The snapshot was never loaded over this account's live rows...
			Assert.Empty(LiveExtendedKeys(store, owner));
			// ...and the other account's mark is still waiting for its own first page open.
			Assert.Equal(LocalStorageService.SyncMetaPendingApplyValue(presetId),
				PendingApplyMark(store, otherOwner, CyrexVid, CyrexPid));
		}
	}

	// B2 regression (review round 1). The materialization runs ResyncKeyMacroBindingsAsync, which
	// resolves every restored binding's macro_id against the Macros VM collection and DELETES the
	// row when it does not resolve. Consumed where round 1 put it — at the end of
	// LoadPresetsForDeviceAsync, before LoadMacroLibraryAsync has filled that collection — Macros
	// was empty, so the open uploaded an EMPTY macro library to the board and then deleted every
	// key_macro_bindings row the pull had just delivered. Silent data loss, no exception, mark
	// already spent.
	//
	// Written to fail against that ordering — and confirmed to (2026-09-10, with the call moved back
	// to the round-1 position). Both halves of the damage are asserted, and the library assertion
	// covers every upload rather than the last one, because the empty upload the bug produced was
	// followed by a healthy one from the end-of-open resync.
	[Fact]
	public async Task KeyboardPage_PulledPresetWithAMacroBinding_UploadsTheLibraryAndKeepsTheBinding()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var (presetId, macroId) = SeedPulledKeyboardPresetWithMacroBinding(store, owner);
			SetPendingApplyMark(store, owner, CyrexVid, CyrexPid, presetId);
			hid.Info = Info();

			await vm.SetDeviceAsync(CyrexCard(supportsMacros: true));

			// The row the old ordering deleted.
			var binding = Assert.Single(store.GetKeyMacroBindings(User, owner, CyrexVid, CyrexPid, DeviceKey));
			Assert.Equal((SlotQ, macroId), binding);

			// ...and the library the old ordering wiped off the board.
			Assert.NotEmpty(hid.MacroUploads);
			Assert.All(hid.MacroUploads, upload => Assert.NotEmpty(upload));

			Assert.Null(PendingApplyMark(store, owner, CyrexVid, CyrexPid));
		}
	}

	// S3 (review round 1): the mark's value is the LOCAL id of the preset the pull adopted, so a
	// mark that outlived the user's next choice is recognised as stale rather than spent against
	// whatever is applied now — which would reload the pulled snapshot over the live rows and
	// silently discard an applied-but-unsaved tweak. Spent all the same: a stale mark that survived
	// would be re-examined on every open forever.
	[Fact]
	public async Task KeyboardPage_MarkNamingAPresetThatIsNoLongerApplied_IsSpentWithoutApplying()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var (pulledId, _) = SeedPulledKeyboardPreset(store, owner);
			// The user has since chosen something else on this machine.
			var chosenId = store.InsertPreset(User, owner, CyrexVid, CyrexPid,
				new PresetRecord { Name = "Chosen" });
			store.SetAppliedPreset(User, owner, CyrexVid, CyrexPid, DeviceKey, chosenId, markDirty: false);
			SetPendingApplyMark(store, owner, CyrexVid, CyrexPid, pulledId);
			hid.Info = Info();

			await vm.SetDeviceAsync(CyrexCard());

			Assert.Empty(hid.ExtendedKeysWrites);
			Assert.Empty(LiveExtendedKeys(store, owner));
			Assert.Null(PendingApplyMark(store, owner, CyrexVid, CyrexPid));
		}
	}

	// S1 (review round 1): a device switch landing inside the open must not turn one board's
	// pending apply into a push at whatever device is open when it resumes — the payload would be
	// another device's whole configuration.
	//
	// The switch is landed at the last point this harness can reach deterministically: the open's
	// chain parked inside the extended-keys probe, which is the step immediately before
	// MaterializePulledPresetAsync. SetDeviceAsync sets Device and bumps the generation in its
	// SYNCHRONOUS prologue, so the page belongs to the mouse before the keyboard's chain resumes.
	// The narrower window the `target` card fences — a switch landing while the materialization is
	// already queued behind _presetApplyGate — is not separately reachable from here; it is the same
	// guard expression, checked once more inside ApplyPresetToDeviceCoreAsync.
	[Fact]
	public async Task KeyboardPage_DeviceSwitchedDuringTheOpen_DoesNotPushThePresetAtTheOtherDevice()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var (presetId, _) = SeedPulledKeyboardPreset(store, owner);
			SetPendingApplyMark(store, owner, CyrexVid, CyrexPid, presetId);
			hid.Info = Info();

			// The device the user clicks next. Its own lighting is what a cross-push would replace.
			store.SaveRgbState(User, owner, MouseVid, MousePid, DeviceKey,
				isEnabled: true, selectedEffect: "Neon",
				effects: [new RgbEffectRecord { EffectKey = "Neon", Hex = "#0000FF", Brightness = 55, Speed = 33 }]);

			var probeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var probeGate = new TaskCompletionSource();
			hid.ProbeEntered = probeEntered;
			hid.ProbeGate = probeGate.Task;

			var keyboardOpen = vm.SetDeviceAsync(CyrexCard());
			await probeEntered.Task;

			var mouseOpen = vm.SetDeviceAsync(MouseCard());
			probeGate.SetResult();
			await keyboardOpen;
			await mouseOpen;

			// The Cyrex's preset was neither recorded as the mouse's nor pushed at it.
			Assert.NotEqual(presetId, store.GetAppliedPreset(User, owner, MouseVid, MousePid, DeviceKey));
			Assert.Empty(LiveExtendedKeys(store, owner, MouseVid, MousePid));
			Assert.DoesNotContain(hid.ExtendedKeysWrites, w => w.VendorId == MouseVid);
			// Nothing was consumed, so the keyboard still gets its preset on its next open.
			Assert.Equal(LocalStorageService.SyncMetaPendingApplyValue(presetId),
				PendingApplyMark(store, owner, CyrexVid, CyrexPid));
		}
	}

	// ── The connect-time reapply consumes the mark (mice) ─────────────────────

	// A mouse gets its pulled preset without its page ever being opened: the mark makes
	// ReapplyOnConnectAsync load the snapshot into this unit's live rows FIRST, so the one
	// coordinated write carries the preset's lighting rather than whatever the live rows held.
	[Fact]
	public async Task ReapplyOnConnect_MouseWithThePendingApplyMark_CarriesTheSnapshotsRgb()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			// The snapshot holds green, the live rows red, so "the snapshot was materialized" and
			// "the live rows happened to be right" cannot be confused.
			var presetId = SeedPulledMousePreset(store, owner);
			SetPendingApplyMark(store, owner, MouseVid, MousePid, presetId);

			await vm.ReapplyOnConnectAsync(MouseCard());

			Assert.Equal(1, hid.ApplyConfigCallCount);
			Assert.NotNull(hid.LastLed);
			Assert.Equal(0x00, hid.LastLed!.Value.r);
			Assert.Equal(0xFF, hid.LastLed!.Value.g);
			Assert.Null(PendingApplyMark(store, owner, MouseVid, MousePid));
		}
	}

	// S2 (review round 1): the connect path now WRITES live rows, and HomeViewModel.ReapplyOnConnect
	// fires for every connecting card with no "skip the open device" guard of its own. Landing the
	// materialize half for the unit whose details page is open would diverge the live rows from
	// what the page holds — SetDeviceAsync's awaited PersistRgbStateForCurrentDeviceAsync writes the
	// page's pre-materialization RGB straight back over them, the board wears the snapshot colour,
	// and Save Preset lights up on a page the user never touched.
	//
	// So IsOpenDeviceCard declines it, and the mark survives for the page's own consumption — which
	// the second half of this test takes: the materialization happens exactly once, on the page.
	// The card passed to the connect path is a fresh instance on purpose: HomeViewModel hands it
	// the roster's card, which for a reconnect need not be the one the page was opened with, so the
	// comparison has to be on the settings identity rather than by reference.
	[Fact]
	public async Task ReapplyOnConnect_ForTheUnitWhosePageIsOpen_LeavesTheMarkToThePage()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			var presetId = SeedPulledMousePreset(store, owner);

			// The page is opened BEFORE the mark exists — the sign-in pull lands while the user is
			// already sitting on this device's page.
			await vm.SetDeviceAsync(MouseCard());
			SetPendingApplyMark(store, owner, MouseVid, MousePid, presetId);
			var writesBeforeConnect = hid.ApplyConfigCallCount;

			await vm.ReapplyOnConnectAsync(MouseCard());

			// The connect push carried the LIVE rows (red), not the snapshot (green): the
			// materialize half was declined.
			Assert.True(hid.ApplyConfigCallCount > writesBeforeConnect);
			Assert.NotNull(hid.LastLed);
			Assert.Equal(0xFF, hid.LastLed!.Value.r);
			Assert.Equal(0x00, hid.LastLed!.Value.g);
			Assert.Equal("#FF0000", LiveRgbHex(store, owner));
			// Untouched, so nothing is lost by declining.
			Assert.Equal(LocalStorageService.SyncMetaPendingApplyValue(presetId),
				PendingApplyMark(store, owner, MouseVid, MousePid));

			// The page owns it: reopening this unit materializes the snapshot, once.
			await vm.SetDeviceAsync(MouseCard());

			Assert.Equal("#00FF00", LiveRgbHex(store, owner));
			Assert.Null(PendingApplyMark(store, owner, MouseVid, MousePid));
		}
	}

	// ── An account switch, carried by the page open itself ───────────────────

	// The reported bug (docs/plans/fix-account-switch-device-state), written first and failing on
	// the code as it stood: A configured a Toggle on a shared keyboard, B signed in and saw A's
	// Toggle in the Extended Keys list, and deleting it under B took it off A's board too.
	//
	// Nothing leaked in storage — each account's rows were always its own. The flash is shared
	// hardware, and unlike lighting a keyboard's configuration was never re-applied when the
	// settings owner changed: the board kept A's records and the section, which renders what it
	// probed off the board, faithfully showed them to B.
	//
	// So the assertion is on the BOARD, not on the tables: opening the page as B must write B's own
	// set to it — and A's live rows and preset snapshot must come through the switch untouched,
	// because a fix that "works" by overwriting A's records is the same bug wearing the other hat.
	[Fact]
	public async Task KeyboardPage_OpenedAfterAnAccountSwitch_WritesTheNewOwnersSetAndLeavesTheOthersRecords()
	{
		var (vm, store, hid, ownerA, ownerProvider) = NewSwitchableVm();
		using (store)
		{
			// A's Toggle on Q: saved into A's preset, in A's live rows, and physically on the board.
			var aRecords = new List<ExtendedKeyRecord>
			{
				ExtendedKeyRecord.Toggle(SlotQ, 100, new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A)),
			};
			var aPresetId = SeedConfiguredKeyboardPreset(store, ownerA, aRecords);
			hid.Info = Info([.. ExtendedKeyRecord.NormalizeSet(aRecords)]);

			// B has an applied preset of their own for the same board, holding no extended keys —
			// which is why nothing else on the open would write: EnsureDefaultPresetAppliedAsync
			// early-returns for an account that already has an applied row.
			var ownerB = Guid.NewGuid().ToString("D");
			SeedConfiguredKeyboardPreset(store, ownerB, []);

			// The account switch, as HomeViewModel's owner sweep performs it: the incoming owner
			// becomes current, and nothing else (see SwitchOwnerTo). The self-heal at the end of B's
			// open is what takes A's Toggle off the board — no mark is written or needed.
			ownerProvider.Owner = new SettingsOwner(User, ownerB);

			// B opens the page.
			await vm.SetDeviceAsync(CyrexCard());

			// The board wears B's set now, written once.
			var write = Assert.Single(hid.ExtendedKeysWrites);
			Assert.Equal(CyrexVid, write.VendorId);
			Assert.Empty(write.Entries);

			// A lost nothing: both the live rows and the preset A saved still hold the Toggle.
			Assert.Equal(ExtendedKeyRecord.NormalizeSet(aRecords), LiveExtendedKeys(store, ownerA));
			Assert.Equal(ExtendedKeyRecord.NormalizeSet(aRecords), store.GetPresetExtendedKeys(aPresetId));
			Assert.Equal(aPresetId, store.GetAppliedPreset(User, ownerA, CyrexVid, CyrexPid, DeviceKey));

			// Spent, so B's next open of the page is silent.
			Assert.Null(PendingApplyMark(store, ownerB, CyrexVid, CyrexPid));
		}
	}

	// The case above covers a switch made INSIDE one process. The user reproduced the original bug
	// again on 2026-09-11 by restarting the app between the accounts, and that path has no mark at
	// all: the sweep decides from _lastAppliedOwner, an in-memory field, so a fresh process reads the
	// restored session as the FIRST owner of the session and its "first owner is not a switch" rule —
	// inherited from the lighting sweep — answers no. The rule does not transfer, because unlike
	// lighting a keyboard's configuration lives in FLASH and outlives both the process and the power.
	//
	// So this case deliberately seeds NO pending-apply mark. That is the whole point: a version of it
	// that marked first would pass today and prove nothing. What must carry the switch instead is the
	// board itself disagreeing with the signed-in owner's rows
	// (docs/plans/fix-extended-keys-self-heal).
	[Fact]
	public async Task KeyboardPage_OpenedAfterARestartUnderAnotherAccount_WritesTheNewOwnersSet()
	{
		var (vm, store, hid, ownerA, ownerProvider) = NewSwitchableVm();
		using (store)
		{
			// A's Toggle on Q, saved and physically on the board — the state a previous session left.
			var aRecords = new List<ExtendedKeyRecord>
			{
				ExtendedKeyRecord.Toggle(SlotQ, 100, new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A)),
			};
			var aPresetId = SeedConfiguredKeyboardPreset(store, ownerA, aRecords);
			hid.Info = Info([.. ExtendedKeyRecord.NormalizeSet(aRecords)]);

			// B's own applied preset holds no extended keys, so nothing else on the open writes:
			// EnsureDefaultPresetAppliedAsync early-returns for an account that has an applied row.
			var ownerB = Guid.NewGuid().ToString("D");
			SeedConfiguredKeyboardPreset(store, ownerB, []);

			// The restart: the app comes up signed in as B. No OnOwnerChanged, no mark — a fresh
			// process has neither, which is exactly why the previous fix does not fire here.
			ownerProvider.Owner = new SettingsOwner(User, ownerB);
			Assert.Null(PendingApplyMark(store, ownerB, CyrexVid, CyrexPid));

			// B opens the page.
			await vm.SetDeviceAsync(CyrexCard());

			// The board wears B's set now, written once.
			var write = Assert.Single(hid.ExtendedKeysWrites);
			Assert.Equal(CyrexVid, write.VendorId);
			Assert.Empty(write.Entries);

			// And A lost nothing: a fix that "works" by overwriting A is the same bug reversed.
			Assert.Equal(ExtendedKeyRecord.NormalizeSet(aRecords), LiveExtendedKeys(store, ownerA));
			Assert.Equal(ExtendedKeyRecord.NormalizeSet(aRecords), store.GetPresetExtendedKeys(aPresetId));
		}
	}

	// The case above lands B on an EMPTY set, which a "clear the board when the owner has nothing"
	// fix would also satisfy. This one gives B records of its own, so only a fix that writes the
	// signed-in owner's ACTUAL rows passes: the board must end up holding B's pair, not A's Toggle
	// and not nothing.
	[Fact]
	public async Task KeyboardPage_OpenedAfterARestart_WritesTheNewOwnersOwnRecordsNotJustAnEmptySet()
	{
		var (vm, store, hid, ownerA, ownerProvider) = NewSwitchableVm();
		using (store)
		{
			var aRecords = new List<ExtendedKeyRecord>
			{
				ExtendedKeyRecord.Toggle(SlotQ, 100, new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A)),
			};
			SeedConfiguredKeyboardPreset(store, ownerA, aRecords);
			hid.Info = Info([.. ExtendedKeyRecord.NormalizeSet(aRecords)]);

			var bRecords = new List<ExtendedKeyRecord> { ExtendedKeyRecord.RapidSnap(SlotA, SlotD) };
			var ownerB = Guid.NewGuid().ToString("D");
			SeedConfiguredKeyboardPreset(store, ownerB, bRecords);

			// The restart: signed in as B, no mark anywhere.
			ownerProvider.Owner = new SettingsOwner(User, ownerB);
			Assert.Null(PendingApplyMark(store, ownerB, CyrexVid, CyrexPid));

			await vm.SetDeviceAsync(CyrexCard());

			Assert.Equal(ExtendedKeyRecord.NormalizeSet(bRecords), Assert.Single(hid.ExtendedKeysWrites).Entries);
			// ...and the section shows the board it just corrected, not the probe's first reading.
			Assert.Equal(ExtendedKeyRecord.NormalizeSet(bRecords), vm.ExtendedKeys.Select(r => r.Record));
		}
	}

	// The self-heal asks the DEVICE what it holds. A probe that never answered leaves that unknown,
	// which is not the same as "the board is empty" — pushing the owner's set over records nobody has
	// read, or clearing them, would both be guesses. So an open with no probe result writes nothing,
	// even though the owner's rows and the (unknown) board trivially "differ".
	//
	// What enforces that today is the "is it supported" gate, not the explicit unknown-board guard
	// next to it: a declined probe leaves BOTH unset. Verified by reverting that guard — this case
	// still passes. So it pins the outcome, which is what matters to the user, and not the guard,
	// which is defensive and currently unreachable (see its own comment).
	[Fact]
	public async Task KeyboardPage_WhenTheProbeDeclined_WritesNothingEvenThoughTheOwnerHasRecords()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			SeedConfiguredKeyboardPreset(store, owner,
				[ExtendedKeyRecord.Toggle(SlotQ, 100, new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A))]);
			// The probe returns nothing at all — a transport failure, not a "board has no records".
			hid.Info = null;

			await vm.SetDeviceAsync(CyrexCard());

			Assert.Empty(hid.ExtendedKeysWrites);
		}
	}

	// A read-only transport (Bluetooth on a board whose vendor channel is wired-only) must not be
	// written to at all — the self-heal is a device write like any other and is gated the same way.
	[Fact]
	public async Task KeyboardPage_OnAReadOnlyTransport_WritesNothingEvenWhenTheBoardDisagrees()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			SeedConfiguredKeyboardPreset(store, owner, []);
			// The board disagrees with the owner's (empty) rows, so only the transport gate can stop
			// the write.
			hid.Info = Info(ExtendedKeyRecord.RapidSnap(SlotA, SlotD));

			var card = CyrexCard();
			card.SupportsConfiguration = false;
			await vm.SetDeviceAsync(card);

			Assert.Empty(hid.ExtendedKeysWrites);
		}
	}

	// S1 (review round 1, Tests) — the self-heal's twin of
	// KeyboardPage_DeviceSwitchedDuringTheOpen_DoesNotPushThePresetAtTheOtherDevice. The self-heal
	// reads Device and the signed-in owner for ITSELF, so without the Stale() fence at its call site
	// a chain whose page has already been replaced compares the OLD board's probe result against the
	// NEW device's stored rows — and sends the new board a flash commit it never needed.
	//
	// Building that window takes more than the extended-keys probe gate on its own, and the reason
	// is worth writing down because the obvious shape of this test is vacuous:
	// LoadExtendedKeysFromDeviceAsync nulls the board baseline and bumps its load generation BEFORE
	// it probes. So parking the SECOND device at that probe erases the very state the bug reads, and
	// parking the FIRST device there makes its own load bail (Device has changed by the time it
	// resumes) and set no baseline at all. Either way nothing can go wrong and the case proves
	// nothing. What is needed is three things at once:
	//   - the Cyrex's open parked at its extended-keys probe and then RELEASED, so the baseline
	//     settles to the Cyrex's board while Device is still the Cyrex;
	//   - the storage executor stalled first, so the chain parks on its very next step
	//     (LoadPersistedDeviceStateAsync → LoadDpiPresetsAsync) instead of running on to the
	//     self-heal;
	//   - the Aress's open landed in that gap and held at its ACTUATION probe — one step before its
	//     own extended-keys load — so it cannot touch the baseline while the Cyrex's chain resumes.
	// No sleeps: every step waits on a signal from the fake, from the storage executor, or from the
	// section's own rows.
	//
	// Both boards already wear their owner's set, so a healthy run writes NOTHING at all and the one
	// spurious write a missing fence produces is the whole signal.
	[Fact]
	public async Task KeyboardPage_DeviceSwitchedBeforeTheSelfHeal_WritesNeitherBoard()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			// The Cyrex: the owner's rows and the board already agree, so its own open is silent.
			var cyrexRecords = new List<ExtendedKeyRecord> { ExtendedKeyRecord.RapidSnap(SlotA, SlotD) };
			SeedConfiguredKeyboardPreset(store, owner, cyrexRecords);
			hid.BoardInfo[(CyrexVid, CyrexPid)] = Info([.. ExtendedKeyRecord.NormalizeSet(cyrexRecords)]);

			// The Aress: likewise, and with a DIFFERENT set, so a write carrying the Aress's rows is
			// attributable and neither board has any legitimate reason to be written.
			var aressRecords = new List<ExtendedKeyRecord>
			{
				ExtendedKeyRecord.Toggle(SlotQ, 100, new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A)),
			};
			SeedConfiguredKeyboardPreset(store, owner, aressRecords, vendorId: AressVid, productId: AressPid);
			hid.BoardInfo[(AressVid, AressPid)] = Info([.. ExtendedKeyRecord.NormalizeSet(aressRecords)]);

			var probeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var probeGate = new TaskCompletionSource();
			hid.ProbeEntered = probeEntered;
			hid.ProbeGate = probeGate.Task;

			var cyrexOpen = vm.SetDeviceAsync(CyrexCard());
			await probeEntered.Task;

			// Stall the storage executor while the chain is still parked in HID, so the step right
			// after the probe has nowhere to go and cannot carry the chain to the self-heal.
			var blockerRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var releaseStorage = new TaskCompletionSource();
			_ = store.RunOnStorageAsync(() => { blockerRunning.TrySetResult(); releaseStorage.Task.Wait(); });
			await blockerRunning.Task;

			// The section's rows are the signal that the probe landed and the baseline is now the
			// Cyrex's board — RebuildExtendedKeyRows runs inside the load, before it returns.
			var baselineSettled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			vm.ExtendedKeys.CollectionChanged += (_, _) =>
			{
				if (vm.ExtendedKeys.Count > 0) baselineSettled.TrySetResult();
			};
			probeGate.SetResult();
			await baselineSettled.Task;

			// The user clicks the other keyboard. Its open is held one step before its own
			// extended-keys load, so it changes Device and nothing else.
			var holdAress = new TaskCompletionSource();
			hid.ProbeEntered = null;
			hid.ProbeGate = null;
			hid.ActuationGate = holdAress.Task;

			var aressOpen = vm.SetDeviceAsync(AressCard());

			// Let the Cyrex's chain run out — it reaches the self-heal with the page already gone.
			releaseStorage.SetResult();
			await cyrexOpen;

			// Nothing was written while a chain finished on a page that is no longer its own.
			Assert.Empty(hid.ExtendedKeysWrites);

			holdAress.SetResult();
			await aressOpen;

			// ...and the Aress's own open is silent too, because its board already wears its set.
			Assert.Empty(hid.ExtendedKeysWrites);
			Assert.Equal(ExtendedKeyRecord.NormalizeSet(aressRecords), hid.BoardInfo[(AressVid, AressPid)]!.Entries);
			Assert.Equal(ExtendedKeyRecord.NormalizeSet(cyrexRecords), hid.BoardInfo[(CyrexVid, CyrexPid)]!.Entries);
		}
	}

	// S2 (review round 1) — the half of the same problem Stale() does NOT reach. The preset pushes
	// call ApplyStoredExtendedKeysToDeviceAsync directly, with no chain fence of their own
	// (DeviceDetailsViewModel.Presets.cs, at the end of ApplyCurrentStateToDeviceCoreAsync). Since
	// the fix pass moved that method's storage read onto the storage executor, the read is its FIRST
	// yield point, and what covers those callers is the device/owner re-check taken after it: a
	// device switch landing while the read is queued must not turn the previous device's rows into a
	// write at the board now on screen.
	//
	// Driven the same way as the case above, and for the same reasons: the executor is stalled so
	// the read cannot finish before the switch lands, and the arriving keyboard is held at its
	// actuation probe so the baseline it would otherwise clear — which would stop the write for the
	// WRONG reason, and hide a missing re-check — is still the Cyrex's when the read completes.
	[Fact]
	public async Task PresetPush_DeviceSwitchedBetweenTheReadAndTheWrite_DoesNotWriteAtTheOtherBoard()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			// Both boards empty, both scopes' rows empty: every page open is silent, so the only
			// write this case can possibly observe is the push's.
			SeedConfiguredKeyboardPreset(store, owner, []);
			SeedConfiguredKeyboardPreset(store, owner, [], vendorId: AressVid, productId: AressPid);
			hid.Info = Info();

			await vm.SetDeviceAsync(CyrexCard());
			Assert.Empty(hid.ExtendedKeysWrites);

			// What a preset apply does immediately before it pushes: the preset's records land in
			// the unit's live rows and the board has not been told yet.
			var pushed = new List<ExtendedKeyRecord>
			{
				ExtendedKeyRecord.Toggle(SlotQ, 100, new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A)),
			};
			store.ReplaceDeviceExtendedKeys(User, owner, CyrexVid, CyrexPid, DeviceKey, pushed);

			var holdAress = new TaskCompletionSource();
			hid.ActuationGate = holdAress.Task;

			var blockerRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			var releaseStorage = new TaskCompletionSource();
			_ = store.RunOnStorageAsync(() => { blockerRunning.TrySetResult(); releaseStorage.Task.Wait(); });
			await blockerRunning.Task;

			// Enqueued synchronously by the call itself — the method has no await before its read.
			var push = vm.ApplyStoredExtendedKeysToDeviceAsync();

			// The user clicks the other keyboard. SetDeviceAsync sets Device in its SYNCHRONOUS
			// prologue, so the page belongs to the Aress before the queued read ever runs.
			var aressOpen = vm.SetDeviceAsync(AressCard());

			releaseStorage.SetResult();
			Assert.False(await push);

			holdAress.SetResult();
			await aressOpen;

			// Nothing reached either board — above all not the Aress, whose flash would have taken
			// the Cyrex scope's Toggle.
			Assert.Empty(hid.ExtendedKeysWrites);
			// ...and the rows the push was carrying are untouched, so the Cyrex's next open still
			// heals it.
			Assert.Equal(ExtendedKeyRecord.NormalizeSet(pushed), LiveExtendedKeys(store, owner));
		}
	}

	// Extended keys are how the user noticed it, and they ARE carried across a switch — by the
	// self-heal, above. A KEY REMAP is not, and this is the case that says so.
	//
	// It used to assert the opposite: A remapped S to F5, B remapped it to F6, and B's open landed
	// B's record. What made that work was the pending-apply mark the owner sweep wrote, whose
	// consumer answers it with LoadPresetSnapshotToDevice — and that is exactly what M1 showed
	// destroys the incoming account's applied-but-unsaved state (docs/plans/add-cyrex-extended-keys,
	// review round 4). The mark is gone from the sweep, so a plain open by an account that already
	// has an applied preset runs no preset push at all and writes no key.
	//
	// That is the limitation docs/plans/fix-extended-keys-self-heal names and defers by name —
	// "the same root cause affects key remaps, key→macro bindings and actuation ... follow-up plan,
	// with the matrix read's position as its first question". Pinned rather than deleted, so the
	// follow-up has a test to invert and nobody rediscovers it as a surprise.
	[Fact]
	public async Task KeyboardPage_OpenedAfterAnAccountSwitch_DoesNotYetCarryTheNewOwnersRemap()
	{
		var (vm, store, hid, ownerA, ownerProvider) = NewSwitchableVm();
		using (store)
		{
			var aPresetId = SeedConfiguredKeyboardRemap(store, ownerA, SlotS, F5);
			// No extended keys anywhere in this case: an empty board and an empty set agree, so the
			// extended-keys push stays out of the way and the key writes are the whole story.
			hid.Info = Info();
			hid.KeyMatrix = BoardMatrix((SlotS, KeyRecordType, 0x00, F5));

			var ownerB = Guid.NewGuid().ToString("D");
			SeedConfiguredKeyboardRemap(store, ownerB, SlotS, F6);

			SwitchOwnerTo(vm, ownerProvider, ownerB);

			await vm.SetDeviceAsync(CyrexCard());

			// The board still types A's F5 on S. B's own remap reaches it on B's next preset action.
			Assert.DoesNotContain(hid.KeyMappingWrites, w => w.Slot == SlotS);
			Assert.Equal((KeyRecordType, (byte)0x00, F5), hid.KeyMatrix[SlotS]);

			// What has never been in question, and must stay that way: neither account's ROWS move.
			// A silent overwrite of B's live rows with a snapshot is the failure M1 is about, and it
			// would show here as B's bucket changing under an open that wrote nothing.
			Assert.Equal((SlotS, KeyRecordType, (byte)0x00, F5),
				Assert.Single(store.GetKeyBindings(User, ownerA, CyrexVid, CyrexPid, DeviceKey)));
			Assert.Equal((SlotS, KeyRecordType, (byte)0x00, F5),
				Assert.Single(store.GetPresetKeyBindings(aPresetId)));
			Assert.Equal((SlotS, KeyRecordType, (byte)0x00, F6),
				Assert.Single(store.GetKeyBindings(User, ownerB, CyrexVid, CyrexPid, DeviceKey)));

			Assert.Null(PendingApplyMark(store, ownerB, CyrexVid, CyrexPid));
		}
	}

	// ...and the same for a key→MACRO binding, the third record family in the deferred follow-up.
	// The two accounts bind different keys because each one's macro is slot 0 of its own library —
	// the records would otherwise be identical bytes and "whose binding" would not be answerable.
	//
	// B's binding DOES reach the board: ResyncKeyMacroBindingsAsync runs from RefreshFromDeviceAsync
	// at the end of every open, unconditionally. Taking A's binding OFF A's key is the half that
	// needed the pending-apply mark's preset push, and it is the half that is now deferred with the
	// remap above (review round 4, M1) — the stale-slot clear is keyed on _appliedMacroSlots, which
	// is session state and empty on a fresh open, so nothing tells this open that Q ever had one.
	[Fact]
	public async Task KeyboardPage_OpenedAfterAnAccountSwitch_WritesTheNewOwnersMacroBinding_ButLeavesTheOthersOnTheBoard()
	{
		var (vm, store, hid, ownerA, ownerProvider) = NewSwitchableVm();
		using (store)
		{
			var (aPresetId, aMacroId) = SeedConfiguredKeyboardMacroBinding(store, ownerA, SlotQ);
			hid.Info = Info();
			// A macro record, not a key record: the exact bytes are the firmware's business, all
			// that matters is that Q is not on its factory triplet when B arrives.
			var aOnBoard = ((byte)0x50, (byte)0x01, (byte)0x00);
			hid.KeyMatrix = BoardMatrix(((byte)SlotQ, aOnBoard.Item1, aOnBoard.Item2, aOnBoard.Item3));

			var ownerB = Guid.NewGuid().ToString("D");
			var (_, bMacroId) = SeedConfiguredKeyboardMacroBinding(store, ownerB, SlotF);

			SwitchOwnerTo(vm, ownerProvider, ownerB);

			await vm.SetDeviceAsync(CyrexCard(supportsMacros: true));

			// B's key carries a macro record now — something other than its factory triplet.
			Assert.Contains(hid.KeyMappingWrites, w => w.Slot == SlotF);
			Assert.NotEqual(FactoryRecord(SlotF), hid.KeyMatrix[SlotF]);
			// A's is still on A's key — the deferred half. When the follow-up lands, this becomes
			// FactoryRecord((byte)SlotQ).
			Assert.Equal(aOnBoard, hid.KeyMatrix[(byte)SlotQ]);
			// The library that reached the board is B's, and it is not the empty one.
			Assert.NotEmpty(hid.MacroUploads);
			Assert.All(hid.MacroUploads, upload => Assert.NotEmpty(upload));

			// B's row is the only one in B's bucket...
			Assert.Equal((SlotF, bMacroId),
				Assert.Single(store.GetKeyMacroBindings(User, ownerB, CyrexVid, CyrexPid, DeviceKey)));
			// ...and A's survived the switch in both the live rows and the preset A saved. The macro
			// resync deletes a binding whose macro it cannot resolve, so this is the assertion that
			// says B's open did not run A's rows through B's library.
			Assert.Equal((SlotQ, aMacroId),
				Assert.Single(store.GetKeyMacroBindings(User, ownerA, CyrexVid, CyrexPid, DeviceKey)));
			Assert.Equal((SlotQ, aMacroId), Assert.Single(store.GetPresetKeyMacroBindings(aPresetId)));
		}
	}

	// The other direction, which is the half the user checks in QA step 4: coming BACK to A must
	// restore A's records with no manual preset selection. The two accounts keep their own setups
	// indefinitely as the user switches back and forth.
	[Fact]
	public async Task KeyboardPage_OpenedAfterSwitchingBackToTheFirstAccount_RestoresItsRecords()
	{
		var (vm, store, hid, ownerA, ownerProvider) = NewSwitchableVm();
		using (store)
		{
			var aRecords = new List<ExtendedKeyRecord>
			{
				ExtendedKeyRecord.Toggle(SlotQ, 100, new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A)),
			};
			var aPresetId = SeedConfiguredKeyboardPreset(store, ownerA, aRecords);
			hid.Info = Info([.. ExtendedKeyRecord.NormalizeSet(aRecords)]);

			// B's own opinion is a Rapid Snap pair rather than an empty set, so "the board came back
			// to A's records" cannot pass by the board simply never having been written.
			var ownerB = Guid.NewGuid().ToString("D");
			var bRecords = new List<ExtendedKeyRecord> { ExtendedKeyRecord.RapidSnap(SlotA, SlotD) };
			SeedConfiguredKeyboardPreset(store, ownerB, bRecords);

			SwitchOwnerTo(vm, ownerProvider, ownerB);
			await vm.SetDeviceAsync(CyrexCard());
			Assert.Equal(ExtendedKeyRecord.NormalizeSet(bRecords), Assert.Single(hid.ExtendedKeysWrites).Entries);

			// ...and back to A, the same switch in the other direction.
			SwitchOwnerTo(vm, ownerProvider, ownerA);
			await vm.SetDeviceAsync(CyrexCard());

			Assert.Equal(2, hid.ExtendedKeysWrites.Count);
			Assert.Equal(ExtendedKeyRecord.NormalizeSet(aRecords), hid.ExtendedKeysWrites[^1].Entries);
			// A's own tables never moved through any of it, and A is still on A's preset.
			Assert.Equal(ExtendedKeyRecord.NormalizeSet(aRecords), LiveExtendedKeys(store, ownerA));
			Assert.Equal(ExtendedKeyRecord.NormalizeSet(aRecords), store.GetPresetExtendedKeys(aPresetId));
			Assert.Equal(aPresetId, store.GetAppliedPreset(User, ownerA, CyrexVid, CyrexPid, DeviceKey));
		}
	}

	// The hole the plan's step 2 covers, and the one the fix deliberately needed no code for: an
	// account that has NEVER configured this board has no preset of its own to push, so the sweep
	// marks nothing — and the board must still stop wearing the previous account's records.
	//
	// What clears it is the page's existing EnsureDefaultPresetAppliedAsync, which fires precisely
	// on "no applied row" and pushes the seeded default preset, whose empty extended-keys set is a
	// genuine "none". Pinned rather than trusted to the reading (implementation notes, 2026-09-10).
	[Fact]
	public async Task KeyboardPage_OpenedByAnOwnerWithNoAppliedPreset_ClearsThePreviousOwnersRecords()
	{
		var (vm, store, hid, ownerA, ownerProvider) = NewSwitchableVm();
		using (store)
		{
			var aRecords = new List<ExtendedKeyRecord>
			{
				ExtendedKeyRecord.Toggle(SlotQ, 100, new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A)),
			};
			var aPresetId = SeedConfiguredKeyboardPreset(store, ownerA, aRecords);
			hid.Info = Info([.. ExtendedKeyRecord.NormalizeSet(aRecords)]);

			// B has nothing at all for this board — no preset, no applied row, no live rows.
			var ownerB = Guid.NewGuid().ToString("D");
			SwitchOwnerTo(vm, ownerProvider, ownerB);
			Assert.Null(PendingApplyMark(store, ownerB, CyrexVid, CyrexPid));

			await vm.SetDeviceAsync(CyrexCard());

			// One write, and it takes A's Toggle off the board.
			Assert.Empty(Assert.Single(hid.ExtendedKeysWrites).Entries);
			Assert.Empty(LiveExtendedKeys(store, ownerB));

			// A's records are where A left them.
			Assert.Equal(ExtendedKeyRecord.NormalizeSet(aRecords), LiveExtendedKeys(store, ownerA));
			Assert.Equal(ExtendedKeyRecord.NormalizeSet(aRecords), store.GetPresetExtendedKeys(aPresetId));
		}
	}

	// The Risks section's "double apply", pinned: the owner-change mark and
	// EnsureDefaultPresetAppliedAsync are two pushes on the same open, and the ordering that keeps
	// them from both firing is that the materialization records its preset as applied — which is
	// exactly what makes the default push below it a no-op. Two applies would be two board writes.
	//
	// The second open is the flash-wear half: the mark is spent, so the page is silent from then on.
	[Fact]
	public async Task KeyboardPage_OpenedAfterAnAccountSwitch_WritesTheBoardOnceAndIsSilentOnTheSecondOpen()
	{
		var (vm, store, hid, ownerA, ownerProvider) = NewSwitchableVm();
		using (store)
		{
			var aRecords = new List<ExtendedKeyRecord>
			{
				ExtendedKeyRecord.Toggle(SlotQ, 100, new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A)),
			};
			SeedConfiguredKeyboardPreset(store, ownerA, aRecords);
			hid.Info = Info([.. ExtendedKeyRecord.NormalizeSet(aRecords)]);

			var ownerB = Guid.NewGuid().ToString("D");
			var bRecords = new List<ExtendedKeyRecord> { ExtendedKeyRecord.RapidSnap(SlotA, SlotD) };
			SeedConfiguredKeyboardPreset(store, ownerB, bRecords);

			SwitchOwnerTo(vm, ownerProvider, ownerB);

			await vm.SetDeviceAsync(CyrexCard());
			var firstWrite = Assert.Single(hid.ExtendedKeysWrites);
			Assert.Equal(ExtendedKeyRecord.NormalizeSet(bRecords), firstWrite.Entries);

			await vm.SetDeviceAsync(CyrexCard());

			Assert.Same(firstWrite.Entries, Assert.Single(hid.ExtendedKeysWrites).Entries);
			Assert.Null(PendingApplyMark(store, ownerB, CyrexVid, CyrexPid));
		}
	}

	// The GUEST side of the switch, and the only case that fails if the guest short-circuit is put
	// back into ConsumePendingApplyMarkAsync (implementation notes, round 1 — "one amendment
	// outside the plan's 'nothing else changes'"). That guard read "guests never pull, so they never
	// carry the mark", and answered a mark in a guest bucket by leaving it standing forever with the
	// previous account's records still on the board. Scope names guest ↔ account in both directions,
	// so the consumer has to key on owner.Kind rather than assume one.
	//
	// The guest's mark is written here directly rather than by the switch: the owner sweep no longer
	// writes one at all (review round 4, M1). That does not retire the case — sync_meta is keyed by
	// (owner_kind, owner_id, key), and a consumer that special-cases guests is wrong about a bucket
	// it can still be handed, by a future writer or by a restored database.
	//
	// The reviewer's reason the removal is safe — a guest cannot reach a signed-in user's mark,
	// because both the read and the clear pass owner.Kind — is asserted here rather than reasoned
	// about: A carries a mark of its own across the switch, and the guest's open must spend the
	// GUEST's and leave A's standing.
	[Fact]
	public async Task KeyboardPage_OpenedByAGuestAfterAnAccountSwitch_SpendsOnlyTheGuestsMark()
	{
		var (vm, store, hid, ownerA, ownerProvider) = NewSwitchableVm();
		using (store)
		{
			var aRecords = new List<ExtendedKeyRecord>
			{
				ExtendedKeyRecord.Toggle(SlotQ, 100, new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A)),
			};
			var aPresetId = SeedConfiguredKeyboardPreset(store, ownerA, aRecords);
			hid.Info = Info([.. ExtendedKeyRecord.NormalizeSet(aRecords)]);
			// A's own mark, as a sign-in pull would have left it. Nothing in this test consumes it;
			// it is here so "the guest could not reach it" is an assertion and not an argument.
			SetPendingApplyMark(store, ownerA, CyrexVid, CyrexPid, aPresetId);

			// The guest's own opinion is a Rapid Snap pair, not an empty set, so "the board wears
			// the guest's set" cannot pass by the board simply having been cleared.
			var guest = Guid.NewGuid().ToString("D");
			var guestRecords = new List<ExtendedKeyRecord> { ExtendedKeyRecord.RapidSnap(SlotA, SlotD) };
			var guestPresetId = SeedConfiguredKeyboardPreset(store, guest, guestRecords, kind: Guest);

			SwitchOwnerTo(vm, ownerProvider, guest, kind: Guest);
			SetPendingApplyMark(store, guest, CyrexVid, CyrexPid, guestPresetId, kind: Guest);
			Assert.NotNull(PendingApplyMark(store, guest, CyrexVid, CyrexPid, Guest));

			await vm.SetDeviceAsync(CyrexCard());

			// The board wears the GUEST's set, written once.
			Assert.Equal(ExtendedKeyRecord.NormalizeSet(guestRecords),
				Assert.Single(hid.ExtendedKeysWrites).Entries);
			// The guest's mark is spent — the short-circuit would have left it standing forever,
			// with the account's records still on the board.
			Assert.Null(PendingApplyMark(store, guest, CyrexVid, CyrexPid, Guest));
			// ...and A's mark is exactly where A left it: same device type, different owner key.
			Assert.Equal(LocalStorageService.SyncMetaPendingApplyValue(aPresetId),
				PendingApplyMark(store, ownerA, CyrexVid, CyrexPid));

			// A's records came through the switch untouched, live rows and snapshot alike.
			Assert.Equal(ExtendedKeyRecord.NormalizeSet(aRecords), LiveExtendedKeys(store, ownerA));
			Assert.Equal(ExtendedKeyRecord.NormalizeSet(aRecords), store.GetPresetExtendedKeys(aPresetId));
			Assert.Equal(aPresetId, store.GetAppliedPreset(User, ownerA, CyrexVid, CyrexPid, DeviceKey));
		}
	}

	// The contrast: without the mark the connect path reads the live rows exactly as before, so a
	// device configured on THIS machine is not quietly reverted to its snapshot on every plug-in.
	[Fact]
	public async Task ReapplyOnConnect_MouseWithoutTheMark_SendsTheLiveRowsUntouched()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			SeedPulledMousePreset(store, owner);

			await vm.ReapplyOnConnectAsync(MouseCard());

			Assert.Equal(1, hid.ApplyConfigCallCount);
			Assert.NotNull(hid.LastLed);
			Assert.Equal(0xFF, hid.LastLed!.Value.r);
			Assert.Equal(0x00, hid.LastLed!.Value.g);
		}
	}

	// ── The new board's probe window (review round 3, S8) ─────────────────────
	//
	// A device switch replaces Device in SetDeviceAsync's SYNCHRONOUS prologue, but the new board's
	// extended-keys probe sits near the end of the follow-up chain — so between the two the page is
	// on screen, the key layout is the NEW keyboard's, and everything the view model believes about
	// extended keys is still the OLD board's. Two halves: the prologue now drops that belief as a
	// unit, and Key Assignments / Macros refuse the click until the new probe answers, because a
	// dropped set answers "not protected" for every slot (ExtendedKeySlotsInUse reads `?? []`) and
	// a remap accepted on that answer writes a key record over a record pointer nobody has read.
	//
	// The window is built exactly as KeyboardPage_DeviceSwitchedBeforeTheSelfHeal_WritesNeitherBoard
	// builds its own, and for the same reason: the Aress is held at its ACTUATION probe, one step
	// before its extended-keys load, so its own probe has genuinely not run. Parking it at the
	// extended-keys probe instead would prove nothing — that method clears the state itself, before
	// it probes, so the window under test would never exist. No sleeps.

	private static KeyAssignmentOptionViewModel Key(DeviceDetailsViewModel vm, string name) =>
		vm.PhysicalKeys.Single(k => k.DevKeyName == name);

	private static KeyAssignmentOptionViewModel PickerKey(DeviceDetailsViewModel vm, string name) =>
		vm.RemapTargetKeys.Single(k => k.DevKeyName == name);

	// Both boards configured and already wearing their owner's set, so every open is silent and any
	// write these cases observe is one they provoked. The two record sets sit on DIFFERENT slots on
	// purpose: the damaging direction is a slot the new board has a record on and the old one did
	// not, which is the case the stale set answers "free" for.
	private static void SeedTwoAgreeingBoards(
		LocalStorageService store, string owner, FakeHid hid,
		IReadOnlyList<ExtendedKeyRecord> cyrex, IReadOnlyList<ExtendedKeyRecord> aress)
	{
		SeedConfiguredKeyboardPreset(store, owner, cyrex);
		hid.BoardInfo[(CyrexVid, CyrexPid)] = Info([.. ExtendedKeyRecord.NormalizeSet(cyrex)]);

		SeedConfiguredKeyboardPreset(store, owner, aress, vendorId: AressVid, productId: AressPid);
		hid.BoardInfo[(AressVid, AressPid)] = Info([.. ExtendedKeyRecord.NormalizeSet(aress)]);
	}

	// Starts the Aress's open and returns once its chain is parked at the actuation probe — Device,
	// the page and the key layout are the Aress's, its extended-keys probe has not run. The caller
	// releases `gate` to let the open finish.
	private static async Task<Task> ParkAressAtItsActuationProbeAsync(
		DeviceDetailsViewModel vm, FakeHid hid, TaskCompletionSource gate)
	{
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		hid.ActuationEntered = entered;
		hid.ActuationGate = gate.Task;

		var aressOpen = vm.SetDeviceAsync(AressCard());
		await entered.Task;
		return aressOpen;
	}

	// The damaging half. The click must not open the dialog and must not put a key record into a
	// slot the arriving board holds a record on — which is what the pre-fix code did, orphaning a
	// record the App had not read yet.
	[Fact]
	public async Task KeyAssignments_ClickedDuringTheNewBoardsProbeWindow_OpensNoDialogAndWritesNoKeyRecord()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			SeedTwoAgreeingBoards(store, owner, hid,
				cyrex: [ExtendedKeyRecord.RapidSnap(SlotA, SlotD)],
				aress: [ExtendedKeyRecord.RapidSnap(SlotQ, SlotW)]);

			await vm.SetDeviceAsync(CyrexCard());

			var release = new TaskCompletionSource();
			var aressOpen = await ParkAressAtItsActuationProbeAsync(vm, hid, release);

			vm.ActiveSection = NavSection.Buttons;
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Q"));

			Assert.False(vm.IsKeyAssignmentDialogOpen);
			Assert.Null(vm.SelectedPhysicalKey);
			Assert.True(vm.HasExtendedKeyBlockedMessage);
			// The refusal says the board is still being read, not that the key belongs to a record —
			// there is no record to name, and the very next click may well be allowed. (Strings
			// resolve to their keys with no Application running, so the wording IS the key here.)
			Assert.Equal("ExtendedKeys_Blocked_ProbePending", vm.ExtendedKeyBlockedMessage);

			// ...and the commit the open dialog would have reached writes nothing either, because
			// the refusal left it no selected key to act on. This is the assertion the clearing
			// alone would NOT have earned: a null record set reads as "not protected".
			await vm.PickRemapTargetCommand.ExecuteAsync(PickerKey(vm, "Escape"));
			Assert.Empty(hid.KeyMappingWrites);

			release.SetResult();
			await aressOpen;

			// The probe landed: Q is refused again, now as a named record rather than a pending read.
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Q"));
			Assert.False(vm.IsKeyAssignmentDialogOpen);
			Assert.NotEqual("ExtendedKeys_Blocked_ProbePending", vm.ExtendedKeyBlockedMessage);

			// ...and a key no record claims opens the dialog as usual, so the gate is a window and
			// not a lock on the board.
			vm.SelectPhysicalKeyCommand.Execute(Key(vm, "S"));
			Assert.True(vm.IsKeyAssignmentDialogOpen);
			Assert.Empty(hid.KeyMappingWrites);
		}
	}

	// The cosmetic half, which is what the user actually sees: the previous board's records listed
	// on a keyboard that does not have them, its keys painted, and its slots answering as protected.
	[Fact]
	public async Task ExtendedKeys_DuringTheNewBoardsProbeWindow_KeepsNeitherTheOldBoardsRowsNorItsSlots()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			SeedTwoAgreeingBoards(store, owner, hid,
				cyrex: [ExtendedKeyRecord.RapidSnap(SlotA, SlotD)],
				aress: [ExtendedKeyRecord.RapidSnap(SlotQ, SlotW)]);

			await vm.SetDeviceAsync(CyrexCard());

			// The Cyrex's page really does wear its own record first, or everything below passes
			// for the wrong reason.
			Assert.Equal([ExtendedKeyRecord.RapidSnap(SlotA, SlotD)], vm.ExtendedKeys.Select(r => r.Record));
			Assert.True(Key(vm, "A").IsExtendedKeyHighlighted);
			Assert.True(vm.IsExtendedKeyProtectedSlot(SlotA));

			var release = new TaskCompletionSource();
			var aressOpen = await ParkAressAtItsActuationProbeAsync(vm, hid, release);

			Assert.Empty(vm.ExtendedKeys);
			Assert.False(Key(vm, "A").IsExtendedKeyHighlighted);
			Assert.False(Key(vm, "D").IsExtendedKeyHighlighted);
			Assert.False(vm.IsExtendedKeyProtectedSlot(SlotA));
			Assert.False(vm.IsExtendedKeyProtectedSlot(SlotD));

			release.SetResult();
			await aressOpen;

			// The Aress's own probe then fills the page with the Aress's record, and only that.
			Assert.Equal([ExtendedKeyRecord.RapidSnap(SlotQ, SlotW)], vm.ExtendedKeys.Select(r => r.Record));
			Assert.True(Key(vm, "Q").IsExtendedKeyHighlighted);
			Assert.False(Key(vm, "A").IsExtendedKeyHighlighted);
			Assert.Empty(hid.ExtendedKeysWrites);
		}
	}

	// ── An unread board and an ordinary edit (review round 4, M2) ─────────────
	//
	// S8 above covers the probe WINDOW and the path a key CLICK takes. M2 is the rest of it: the
	// window is the whole open when the probe fails outright, and the trigger is not a click at all.
	// Any staged edit anywhere on the page — a colour, a slider — schedules the debounced apply, and
	// on a keyboard that runs the key push. The push's skip set was built only from the owner's rows
	// and the host's blocked list, both empty until the probe answers and forever after one that
	// failed, so it compared the board mirror against the key's own record, found the record's
	// {0x8x, off_lo, off_hi} pointer different, and wrote the key record over it.
	//
	// The cost is not symmetric. An app-made pair comes back on a later open with a successful probe;
	// an unmanaged DKS / END record does not, because the App never stored it and the next rebuild
	// discards the orphaned area bytes. Both are covered here.

	// Polls rather than sleeping a fixed span past the 250 ms debounce, for the reason
	// DeviceDetailsPresetApplyTests.WaitUntil gives: the fixed waits were the flakiest thing in that
	// family on a loaded runner. The assertion this guards is a PRESENCE, so a poll settles it.
	private static async Task WaitUntilAsync(Func<bool> condition, string what, int timeoutMs = 5000)
	{
		var deadline = Environment.TickCount64 + timeoutMs;
		while (!condition() && Environment.TickCount64 < deadline)
			await Task.Delay(25);
		Assert.True(condition(), what);
	}

	[Fact]
	public async Task DebouncedEdit_WhileTheProbeHasFailed_WritesNoKeyRecordOverAPointer()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			// The owner's one opinion about this board is a remap on a PLAIN slot. It is the positive
			// half of this test: the push has to actually run, or "nothing was orphaned" is vacuous.
			SeedConfiguredKeyboardRemap(store, owner, SlotS, F5);

			// The probe fails — the RPC answers null, a transport fact rather than a device verdict.
			// The section stays pending and the view model learns nothing about the board's records
			// for the whole of this open.
			hid.Info = null;
			// ...while the board really carries an app-made pair on A/D and a vendor-configured DKS
			// on Q. Only the mirror knows, and only because the key push reads it for its own diff.
			hid.KeyMatrix = BoardMatrix(
				((byte)SlotA, (byte)0x84, (byte)0x00, (byte)0x00),
				((byte)SlotD, (byte)0x84, (byte)0x00, (byte)0x00),
				((byte)SlotQ, (byte)0x80, (byte)0x00, (byte)0x00));

			await vm.SetDeviceAsync(CyrexCard());

			Assert.True(vm.IsExtendedKeysProbePending);
			// The open itself writes no key record, so the one below is the debounced edit's and
			// nothing else's. (The owner has an applied preset, so the default-preset push stays out.)
			Assert.Empty(hid.KeyMappingWrites);
			// ...and there is a selected preset, or StageChangeAndApply would be a silent no-op.
			Assert.NotNull(vm.SelectedPreset);

			// An RGB colour change. Not a key click: S8's click guard has nothing to say about it.
			vm.StageChangeAndApply(DeviceApplyAspects.Led);

			await WaitUntilAsync(
				() => hid.KeyMappingWrites.Any(w => w.Slot == SlotS), "the debounced key push landed");

			// The owner's own remap went out, so the push really ran...
			Assert.Equal((KeyRecordType, (byte)0x00, F5), hid.KeyMatrix[SlotS]);
			// ...and every slot the board says holds a record pointer was left exactly as it was.
			Assert.DoesNotContain(hid.KeyMappingWrites, w => w.Slot is (byte)SlotA or (byte)SlotD or (byte)SlotQ);
			Assert.Equal(((byte)0x84, (byte)0x00, (byte)0x00), hid.KeyMatrix[(byte)SlotA]);
			Assert.Equal(((byte)0x84, (byte)0x00, (byte)0x00), hid.KeyMatrix[(byte)SlotD]);
			Assert.Equal(((byte)0x80, (byte)0x00, (byte)0x00), hid.KeyMatrix[(byte)SlotQ]);
		}
	}

	// ── The self-heal's follow-up key push (review round 4, m4) ───────────────
	//
	// ApplyStoredExtendedKeysToDeviceAsync returns true when it WROTE the board, and the preset push
	// has always used that to re-run the key push: the slots a removed record freed went back to
	// their factory triplets, so whatever the owner has on them is in storage and no longer on the
	// board. The self-heal — which now runs at the end of every open — ignored the same answer.
	// RefreshFromDeviceAsync at the end of the chain catches macro bindings up, but it never pushes
	// keys, so the slot typed its factory code until the next open.
	//
	// The pair below is what makes the positive case mean anything: the owner's remap is on a plain
	// slot nothing else in the open would push, so it goes out when — and only when — the heal wrote.

	// The two opinions one account can hold about one board at once: extended keys AND a key remap,
	// in the live rows, in the snapshot and recorded as applied. Kept apart from the two single-
	// concern seeds above, which every other case here is written against.
	private static int SeedConfiguredKeyboardPresetWithRemap(
		LocalStorageService store, string owner, IReadOnlyList<ExtendedKeyRecord> records,
		byte slot, byte targetCode)
	{
		var presetId = store.InsertPreset(User, owner, CyrexVid, CyrexPid,
			new PresetRecord { Name = "Mine", IsDefault = true });

		store.ReplaceDeviceExtendedKeys(User, owner, CyrexVid, CyrexPid, DeviceKey, records);
		store.SetKeyBinding(User, owner, CyrexVid, CyrexPid, DeviceKey, slot, KeyRecordType, 0x00, targetCode);
		store.SavePresetSnapshot(presetId, User, owner, CyrexVid, CyrexPid, DeviceKey);
		store.SetAppliedPreset(User, owner, CyrexVid, CyrexPid, DeviceKey, presetId, markDirty: false);
		return presetId;
	}

	[Fact]
	public async Task SelfHeal_ThatWroteTheBoard_RunsTheKeyPushAfterIt()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			SeedConfiguredKeyboardPresetWithRemap(
				store, owner, [ExtendedKeyRecord.RapidSnap(SlotA, SlotD)], SlotS, F5);

			// An empty board against an owner who has a pair: the heal has something to write.
			hid.Info = Info();
			// Every slot on its factory record, S included — so the owner's remap is genuinely owed.
			hid.KeyMatrix = BoardMatrix();

			await vm.SetDeviceAsync(CyrexCard());

			// The heal wrote...
			Assert.Equal([ExtendedKeyRecord.RapidSnap(SlotA, SlotD)], Assert.Single(hid.ExtendedKeysWrites).Entries);
			// ...and the key push ran behind it, so S carries the owner's record rather than the
			// factory one it would otherwise have kept until the next open.
			Assert.Contains(hid.KeyMappingWrites, w => w.Slot == SlotS);
			Assert.Equal((KeyRecordType, (byte)0x00, F5), hid.KeyMatrix[SlotS]);
		}
	}

	// The other half, and the one that keeps the case above from passing on a push that simply always
	// runs: the overwhelmingly common open is one where the board already wears the owner's set. The
	// heal writes nothing, returns false, and nothing follows it — the owner's remap stays owed to
	// the next preset action exactly as it was before this branch.
	[Fact]
	public async Task SelfHeal_ThatWroteNothing_DoesNotRunTheKeyPush()
	{
		var (vm, store, hid, owner) = NewVm();
		using (store)
		{
			SeedConfiguredKeyboardPresetWithRemap(
				store, owner, [ExtendedKeyRecord.RapidSnap(SlotA, SlotD)], SlotS, F5);

			// The only difference from the case above: the board already holds the owner's set.
			hid.Info = Info(ExtendedKeyRecord.RapidSnap(SlotA, SlotD));
			hid.KeyMatrix = BoardMatrix();

			await vm.SetDeviceAsync(CyrexCard());

			Assert.Empty(hid.ExtendedKeysWrites);
			Assert.DoesNotContain(hid.KeyMappingWrites, w => w.Slot == SlotS);
			Assert.Equal(FactoryRecord(SlotS), hid.KeyMatrix[SlotS]);
		}
	}
}
