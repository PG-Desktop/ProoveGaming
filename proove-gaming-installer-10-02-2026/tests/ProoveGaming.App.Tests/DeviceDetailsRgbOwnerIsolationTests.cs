using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// docs/plans/fix-rgb-owner-isolation — the in-session device-state caches on DeviceDetailsViewModel
// are keyed by device identity, and the view model is a DI singleton that outlives every sign-in and
// sign-out. Keyed without the OWNER, account B's page open resolved account A's cached lighting and
// then persisted it into B's rows (DeviceDetailsViewModel.cs's persist-on-open), and going back to A
// wrote B's colours over A's — the user's saved RGB was destroyed, not merely "not saved".
//
// Everything here therefore drives ONE view model instance across owner changes, exactly as the app
// does, and switches accounts through a mutable ICurrentOwnerProvider (the SettingsRepositoryTests
// pattern) over a real on-disk LocalStorageService, so the assertions about "A's rows" are
// assertions about rows that really went to disk.
//
// Navigation is modelled faithfully: leaving a device page calls ClearDeviceForExit (MainWindow's
// PerformExitDeviceDetails), which nulls Device but deliberately does NOT clear these caches — so
// the defect is still reachable through the flow a user actually performs.
public class DeviceDetailsRgbOwnerIsolationTests
{
	private const int Vid = 0x04D9;
	private const int Pid = 0xA23F;
	private const string DeviceKey = "";

	// Distinct enough that a wrong-account leak is unmistakable in a failure message.
	private const string ARed = "#FF0000";
	private const string BGreen = "#00FF00";
	private const string DefaultsHex = "#123456";

	private static SettingsOwner UserA => new(LocalStorageService.OwnerKindUser, "42");
	private static SettingsOwner UserB => new(LocalStorageService.OwnerKindUser, "43");

	// The owner flips under the view model's feet, which is the whole point — so it is a field, not
	// a constructor argument as in the single-owner Details test files.
	private sealed class MutableOwnerProvider : ICurrentOwnerProvider
	{
		public SettingsOwner? Current;
		public SettingsOwner? GetCurrent() => Current;
	}

	// A real file rather than "Data Source=:memory:" (the usual choice in this suite): the plan's
	// acceptance criteria talk about the device_rgb / device_rgb_effect ROWS surviving an account
	// switch, and a file makes that a claim about persisted state. Pooling is off so the handle is
	// gone by Dispose and the file can actually be deleted on Windows.
	private sealed class TempStore : IDisposable
	{
		private readonly string _path;
		public LocalStorageService Store { get; }

		public TempStore()
		{
			_path = Path.Combine(Path.GetTempPath(), $"proove-rgb-owner-{Guid.NewGuid():N}.db");
			Store = new LocalStorageService($"Data Source={_path};Pooling=False");
		}

		public void Dispose()
		{
			Store.Dispose();
			try { File.Delete(_path); } catch { /* a leaked temp file must not fail a test */ }
		}
	}

	private sealed class FakeUsbHidDeviceService : IUsbHidDeviceService
	{
		public int SetLedCallCount { get; private set; }
		public (byte r, byte g, byte b, byte effect, byte brightness, byte speed)? LastLed { get; private set; }

		public Task<bool> SetDeviceLedColorAsync(int vendorId, int productId, string deviceKey, string deviceType, byte r, byte g, byte b, byte effect, byte brightness, byte speed)
		{
			SetLedCallCount++;
			LastLed = (r, g, b, effect, brightness, speed);
			return Task.FromResult(true);
		}

		public Task<bool> ApplyDeviceConfigurationAsync(
			int vendorId, int productId, string deviceKey,
			(byte r, byte g, byte b, byte effect, byte brightness, byte speed)? led,
			IReadOnlyList<(MouseButton button, MouseButtonAction action, MacroDefinition? macro)>? bindings,
			(byte debounce, byte liftOff)? mouseSettings,
			IReadOnlyList<(int dpi, byte r, byte g, byte b)>? dpiStages,
			int? activeStage,
			int? reportRateHz)
			=> Task.FromResult(true);

		public Task<bool> SetButtonBindingsAsync(int vendorId, int productId, string deviceKey, IReadOnlyList<(MouseButton button, MouseButtonAction action, MacroDefinition? macro)> bindings) => Task.FromResult(true);
		public Task<bool> SetMacrosAsync(int vendorId, int productId, string deviceKey, IReadOnlyList<(int Index, MacroRepeatMode Mode, IReadOnlyList<MacroEvent> Events, int RepeatCount)> entries) => Task.FromResult(true);
		public Task<bool> SetKeyMappingAsync(int vendorId, int productId, byte matrixIndex, byte type, byte code1, byte code2) => Task.FromResult(true);
		public List<HidDeviceResponse> GetDevices() => [];
		public Task StartCommunication() => Task.CompletedTask;
		public bool CheckIfAllDevicesAreDisconnected() => true;
		public Task<KeyActuationInfo?> GetKeyActuationAsync(int vendorId, int productId) => Task.FromResult<KeyActuationInfo?>(null);
		public Task<bool> SetKeyActuationAsync(int vendorId, int productId, int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm) => Task.FromResult(false);
		public Task<bool> SetPerKeyActuationAsync(int vendorId, int productId, IReadOnlyCollection<int> slots, int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm) => Task.FromResult(false);
		public void StopCommunication() { }
		public Task<(int batteryValue, bool isCharging)> GetBatteryLevelAsync(int vendorId, int productId, string deviceKey) => Task.FromResult((0, false));
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

#pragma warning disable CS0067 // DeviceFound/NotifyError/BatteryChanged required by the interface but unused here
		public event EventHandler<SubscribeOnDevicesResponse>? DeviceFound;
		public event EventHandler<string>? NotifyError;
		public event EventHandler<bool>? HostAvailabilityChanged;
		public event EventHandler<BatteryChange>? BatteryChanged;
#pragma warning restore CS0067
		public event EventHandler<DpiStageChange>? DpiStageChanged;
		public event EventHandler<ReportRateChange>? ReportRateChanged;

		// The DeviceHost pushing a physical DPI / polling-button change over the gRPC stream.
		public void RaiseDpiStageChanged(DpiStageChange change) => DpiStageChanged?.Invoke(this, change);
		public void RaiseReportRateChanged(ReportRateChange change) => ReportRateChanged?.Invoke(this, change);
	}

	private static (DeviceDetailsViewModel Vm, FakeUsbHidDeviceService Hid, MutableOwnerProvider Owner) NewVm(
		LocalStorageService store, SettingsOwner? initialOwner)
	{
		var ownerProvider = new MutableOwnerProvider { Current = initialOwner };
		var hid = new FakeUsbHidDeviceService();
		var vm = new DeviceDetailsViewModel(
			usbHidDeviceService: hid,
			localStorage: store,
			settings: new SettingsRepository(store, ownerProvider),
			ownerProvider: ownerProvider);
		// Inline rather than Dispatcher.UIThread.Post + RunJobs — the process-wide dispatcher's
		// first-touch thread affinity trap documented in AssemblyInfo.cs.
		vm.PostToUi = action => action();
		return (vm, hid, ownerProvider);
	}

	// A mouse, so the page open stays light (no keyboard key-layout build) and the RGB section is
	// live. Defaults are explicit so "the new owner sees DEFAULTS" is an assertion on a known value
	// rather than on whatever the catalog happens to hold.
	private static SingleCardViewModel Mouse() => new()
	{
		Name = "Test Mouse",
		DeviceTypeLabel = "Wired Mouse",
		VendorId = Vid,
		ProductId = Pid,
		SettingsVendorId = Vid,
		SettingsProductId = Pid,
		DeviceKey = DeviceKey,
		SettingsDeviceKey = DeviceKey,
		SupportsRgb = true,
		Defaults = new DeviceDefaults(
			RgbEnabled: true, RgbEffect: "Breathing", RgbColorHex: DefaultsHex, RgbBrightness: 40, RgbSpeed: 20),
	};

	// Breathing on purpose: Static and ColorCycle are filtered out of a mouse's effect list
	// (KeyboardOnlyEffectKeys), so a state stored under either would not be selectable here.
	private static void SeedRgb(LocalStorageService store, SettingsOwner owner, string hex, bool enabled = true) =>
		store.SaveRgbState(owner.Kind, owner.Id, Vid, Pid, DeviceKey, enabled, "Breathing",
			[new RgbEffectRecord { EffectKey = "Breathing", Hex = hex, Brightness = 50, Speed = 50 }]);

	private static string? StoredHex(LocalStorageService store, SettingsOwner owner) =>
		store.GetRgbState(owner.Kind, owner.Id, Vid, Pid, DeviceKey)?
			.Effects.FirstOrDefault(e => e.EffectKey == "Breathing")?.Hex;

	// Home → Details → back to Home, which is what happens around every sign-out in the app.
	private static async Task OpenAndLeaveAsync(DeviceDetailsViewModel vm, DeviceCardViewModel card)
	{
		await vm.SetDeviceAsync(card);
		vm.ClearDeviceForExit();
	}

	// ── The reproduction (plan acceptance criteria 1 and 2) ──────────────────────────────────

	// A sets red, B has green of its own. Pre-fix, B's page open hit A's cache entry and B saw red,
	// and the persist-on-open then wrote red into B's rows; going back to A repeated the theft in
	// the other direction. Both directions are asserted here, on the view model AND on the rows.
	[Fact]
	public async Task OwnerSwitch_ShowsEachAccountsOwnRgb_AndLeavesTheOtherAccountsRowsIntact()
	{
		using var temp = new TempStore();
		var store = temp.Store;
		SeedRgb(store, UserA, ARed);
		SeedRgb(store, UserB, BGreen);

		var (vm, _, owner) = NewVm(store, UserA);
		var card = Mouse();

		await OpenAndLeaveAsync(vm, card);
		Assert.Equal(ARed, vm.ActiveRgbEffect.SelectedHexColor);

		// A signs out, B signs in, B opens the same device.
		owner.Current = UserB;
		await OpenAndLeaveAsync(vm, card);

		Assert.Equal(BGreen, vm.ActiveRgbEffect.SelectedHexColor);
		Assert.Equal(ARed, StoredHex(store, UserA));
		Assert.Equal(BGreen, StoredHex(store, UserB));

		// ...and back again.
		owner.Current = UserA;
		await OpenAndLeaveAsync(vm, card);

		Assert.Equal(ARed, vm.ActiveRgbEffect.SelectedHexColor);
		Assert.Equal(ARed, StoredHex(store, UserA));
		Assert.Equal(BGreen, StoredHex(store, UserB));
	}

	// The same switch when the new account has never configured this device: it must land on the
	// card's own defaults, not inherit the previous account's colours.
	[Fact]
	public async Task OwnerSwitch_WithNothingStoredForTheNewOwner_ShowsDefaultsNotThePreviousAccountsColours()
	{
		using var temp = new TempStore();
		var store = temp.Store;
		SeedRgb(store, UserA, ARed);

		var (vm, _, owner) = NewVm(store, UserA);
		var card = Mouse();

		await OpenAndLeaveAsync(vm, card);
		owner.Current = UserB;
		await OpenAndLeaveAsync(vm, card);

		Assert.Equal(DefaultsHex, vm.ActiveRgbEffect.SelectedHexColor);
		Assert.Equal(ARed, StoredHex(store, UserA));
	}

	// ── Null owner (welcome screen) — plan acceptance criterion 3 ────────────────────────────

	// Without an owner there is nothing to key an entry by. Pre-fix the snapshot was cached anyway,
	// under the owner-less key, so the defaults resolved on the Welcome screen were then served to
	// the next account that opened the page — which is the same leak with an empty account on one
	// side. Asserting the SIGNED-IN open afterwards is what makes this a real check: a plain
	// "does not throw" would pass either way.
	[Fact]
	public async Task NullOwner_BypassesTheCache_AndDoesNotPoisonTheNextSignedInOpen()
	{
		using var temp = new TempStore();
		var store = temp.Store;
		SeedRgb(store, UserA, ARed);

		var (vm, _, owner) = NewVm(store, initialOwner: null);
		var card = Mouse();

		var exception = await Record.ExceptionAsync(() => OpenAndLeaveAsync(vm, card));
		Assert.Null(exception);
		Assert.Equal(DefaultsHex, vm.ActiveRgbEffect.SelectedHexColor);

		owner.Current = UserA;
		await OpenAndLeaveAsync(vm, card);

		Assert.Equal(ARed, vm.ActiveRgbEffect.SelectedHexColor);
	}

	// ── The cache still does its job within one owner — criterion 4 ──────────────────────────

	// Not asserted through LocalStorageService.OnBeforeStorageAccess: a device open makes dozens of
	// storage accesses (presets, macros, bindings, the default-preset apply), and the seam is a bare
	// Action with no way to tell which call it belongs to — a count over the whole open cannot
	// isolate the RGB read. Mutating the row behind the view model's back proves the same thing
	// without ambiguity: only a cache hit can still show the old colour.
	[Fact]
	public async Task SameOwnerReopen_IsServedFromTheCache_WithoutRereadingStorage()
	{
		using var temp = new TempStore();
		var store = temp.Store;
		SeedRgb(store, UserA, ARed);

		var (vm, _, _) = NewVm(store, UserA);
		var card = Mouse();

		await OpenAndLeaveAsync(vm, card);

		// Changed underneath: a re-read would pick this up, a cache hit cannot.
		SeedRgb(store, UserA, "#ABCDEF");
		await OpenAndLeaveAsync(vm, card);

		Assert.Equal(ARed, vm.ActiveRgbEffect.SelectedHexColor);
	}

	// ── Preset-apply eviction — criterion 5 ──────────────────────────────────────────────────

	// The eviction exists so the applied snapshot is what the page shows next. It must remove the
	// CURRENT owner's entry and only that one: with an owner-less key, applying a preset under A
	// dropped the entry B was relying on too, and B's next open then re-resolved from rows A's
	// apply had just rewritten.
	[Fact]
	public async Task PresetApply_EvictsOnlyTheCurrentOwnersCacheEntry()
	{
		using var temp = new TempStore();
		var store = temp.Store;
		SeedRgb(store, UserA, ARed);
		SeedRgb(store, UserB, BGreen);

		var (vm, _, owner) = NewVm(store, UserA);
		var card = Mouse();

		// Populate both accounts' entries through real page opens.
		await OpenAndLeaveAsync(vm, card);
		owner.Current = UserB;
		await OpenAndLeaveAsync(vm, card);
		owner.Current = UserA;

		// A custom preset of A's, carrying a lighting snapshot of its own.
		var presetId = store.InsertPreset(UserA.Kind, UserA.Id, Vid, Pid, new PresetRecord { Name = "Custom" });
		store.InsertPresetRgbState(presetId, true, "Breathing",
			[new RgbEffectRecord { EffectKey = "Breathing", Hex = "#0000FF", Brightness = 50, Speed = 50 }]);

		await vm.SetDeviceAsync(card);
		var custom = vm.Presets.First(p => p.Id == presetId);
		await vm.SelectPresetCommand.ExecuteAsync(custom);

		// A's entry was evicted, so the page re-resolved and shows the preset's blue.
		Assert.Equal("#0000FF", vm.ActiveRgbEffect.SelectedHexColor);

		// B's entry was not. Its row is changed behind the view model's back so that a surviving
		// cache entry (green) and a re-read (the new value) are distinguishable.
		vm.ClearDeviceForExit();
		SeedRgb(store, UserB, "#ABCDEF");
		owner.Current = UserB;
		await vm.SetDeviceAsync(card);

		Assert.Equal(BGreen, vm.ActiveRgbEffect.SelectedHexColor);
	}

	// ── The account switch landing mid-read ──────────────────────────────────────────────────

	// LoadRgbStateForCurrentDeviceAsync resolves the owner on the calling thread and does the SQLite
	// read on the pool, so a sign-out/sign-in can land in that gap. The record in hand then belongs
	// to the OLD account, and applying it would put A's lighting on B's page and (through the
	// persist-on-open that follows) into B's rows. The post-await owner re-check is what closes it.
	//
	// The flip is driven from the storage seam so it happens while the read is genuinely in flight,
	// rather than being timed with a sleep.
	[Fact]
	public async Task OwnerChangingDuringTheRgbRead_NeverShowsOrPersistsThePreviousAccountsState()
	{
		using var temp = new TempStore();
		var store = temp.Store;
		SeedRgb(store, UserA, ARed);
		SeedRgb(store, UserB, BGreen);

		var (vm, _, owner) = NewVm(store, UserA);
		var card = Mouse();

		// The RGB read is the first storage access of an open on a view model with no device yet
		// (the outgoing-device flush no-ops), so the one-shot arms on exactly that call.
		var armed = true;
		store.OnBeforeStorageAccess = () =>
		{
			if (!armed) return;
			armed = false;
			owner.Current = UserB;
		};

		await vm.SetDeviceAsync(card);
		store.OnBeforeStorageAccess = null;

		Assert.NotEqual(ARed, vm.ActiveRgbEffect.SelectedHexColor);

		// The positive assertion, not merely "not A's red": what the switch must survive is B's OWN
		// row, and the write that endangers it carries the view model's stale state rather than A's
		// record — which is not ARed either, so a NotEqual passes straight through the corruption
		// this test is named for. B's stored green being intact is the only thing that proves the
		// open stopped instead of persisting under the new owner.
		Assert.Equal(BGreen, StoredHex(store, UserB));
	}

	// ── Sign-out with the details page still open (Round 1, B2) ──────────────────────────────

	// The other door into acceptance criteria 1 and 2, and the one the page-exit flow hides: a
	// sign-out does NOT leave the details page. SignOutToFreshSignIn → ClearSignedInState goes
	// straight to Welcome; only PerformExitDeviceDetails calls ClearDeviceForExit. So Device keeps
	// pointing at the departing account's card across the sign-out, and the NEXT account's first
	// open begins with SetDeviceAsync's outgoing flush — built from the previous account's
	// still-displayed colours, filed under whichever owner is current when the write runs.
	//
	// Modelled the way it actually happens: the owner is flipped WITHOUT ClearDeviceForExit, which
	// is what distinguishes this from every other test in this file. The two OnOwnerChanged calls
	// are the cache purge HomeViewModel drives on the transition — included because the app makes
	// them, and because the flush this test is about reads the view model's live properties, which
	// the purge does not touch.
	[Fact]
	public async Task SignOutWithThePageOpen_DoesNotFlushTheDepartingAccountsColoursIntoTheNextAccount()
	{
		using var temp = new TempStore();
		var store = temp.Store;
		SeedRgb(store, UserA, ARed);
		SeedRgb(store, UserB, BGreen);

		var (vm, _, owner) = NewVm(store, UserA);
		var card = Mouse();

		// A opens the device and stays on the page.
		await vm.SetDeviceAsync(card);
		Assert.Equal(ARed, vm.ActiveRgbEffect.SelectedHexColor);

		// The session token is rejected: the owner goes, the page does not.
		owner.Current = null;
		vm.OnOwnerChanged(null);

		// B signs in and opens that same device.
		owner.Current = UserB;
		vm.OnOwnerChanged(UserB);
		await vm.SetDeviceAsync(card);

		// B's own row must be what B's page shows and what disk still holds. A's red reaching either
		// means the flush wrote an account it did not snapshot.
		Assert.Equal(BGreen, StoredHex(store, UserB));
		Assert.Equal(BGreen, vm.ActiveRgbEffect.SelectedHexColor);
		Assert.Equal(ARed, StoredHex(store, UserA));
	}

	// ── The sibling assertion caches — criterion 6 ───────────────────────────────────────────

	// _assertedDpiStages remembers the stage the app itself last wrote, so the DeviceHost echoing it
	// back is not mistaken for a physical DPI-button press. Recorded for A, it must not silence B's
	// push: B's page has to treat it as the genuine change it is.
	[Fact]
	public async Task AssertedDpiStage_RecordedForOneAccount_DoesNotSilenceTheNextAccountsDevicePush()
	{
		using var temp = new TempStore();
		var store = temp.Store;
		var (vm, hid, owner) = NewVm(store, UserA);
		var card = Mouse();

		var presetId = store.InsertPreset(UserA.Kind, UserA.Id, Vid, Pid, new PresetRecord { Name = "A" });
		var preset = new PresetViewModel { Id = presetId, Name = "A" };
		vm.Presets.Add(preset);
		vm.Device = card;
		await vm.SelectPresetCommand.ExecuteAsync(preset); // fresh pick — not dirty yet

		vm.NoteAssertedDpiStage(card, 2);
		hid.RaiseDpiStageChanged(new DpiStageChange { VendorId = Vid, ProductId = Pid, DeviceKey = DeviceKey, StageIndex = 2 });
		Assert.False(vm.HasUnsavedPresetChanges); // the app's own echo, correctly ignored

		owner.Current = UserB;
		hid.RaiseDpiStageChanged(new DpiStageChange { VendorId = Vid, ProductId = Pid, DeviceKey = DeviceKey, StageIndex = 2 });

		Assert.True(vm.HasUnsavedPresetChanges);
	}

	// _assertedReportRates, same rule, same reason.
	[Fact]
	public async Task AssertedReportRate_RecordedForOneAccount_DoesNotSilenceTheNextAccountsDevicePush()
	{
		using var temp = new TempStore();
		var store = temp.Store;
		var (vm, hid, owner) = NewVm(store, UserA);
		var card = Mouse();

		var presetId = store.InsertPreset(UserA.Kind, UserA.Id, Vid, Pid, new PresetRecord { Name = "A" });
		var preset = new PresetViewModel { Id = presetId, Name = "A" };
		vm.Presets.Add(preset);
		vm.Device = card;
		await vm.SelectPresetCommand.ExecuteAsync(preset);

		vm.NoteAssertedReportRate(card, 500);
		hid.RaiseReportRateChanged(new ReportRateChange { VendorId = Vid, ProductId = Pid, DeviceKey = DeviceKey, Hz = 500 });
		Assert.False(vm.HasUnsavedPresetChanges);

		owner.Current = UserB;
		hid.RaiseReportRateChanged(new ReportRateChange { VendorId = Vid, ProductId = Pid, DeviceKey = DeviceKey, Hz = 500 });

		Assert.True(vm.HasUnsavedPresetChanges);
	}

	// _backfilledPresets, the third assertion cache: one backfill per (device, preset) per session,
	// so the snapshot-vs-live equalisation an open performs is not repeated by the second open of
	// the same page. Recorded for one account it must not suppress the backfill the NEXT account
	// needs — its live rows are its own, and a snapshot left unequalised lights SAVE PRESET on a
	// page that account never touched.
	//
	// The same preset id really does appear under two owners in one process: MigrateGuestToUserAsync
	// RE-KEYS the presets rows rather than copying them ("id is globally unique, no natural-key
	// collision to resolve"), so a guest who applies a preset and then signs in hands that exact id
	// to the user account. That is the flow driven here.
	//
	// Deliberately does NOT call OnOwnerChanged: the purge would drop the entry whatever its key
	// looked like, so routing through it would assert nothing about this cache. What is pinned here
	// is the plan's own contract — the owner is part of every KEY, so a missed purge can leave
	// entries behind but can never let one account read another's state. Its two siblings are pinned
	// the same way, by the pair of tests above.
	[Fact]
	public async Task BackfilledPreset_RecordedForOneAccount_DoesNotSuppressTheNextAccountsBackfill()
	{
		using var temp = new TempStore();
		var store = temp.Store;

		var guestSessionId = Guid.NewGuid();
		var guest = new SettingsOwner(LocalStorageService.OwnerKindGuest, guestSessionId.ToString("D"));
		var (vm, _, owner) = NewVm(store, guest);
		var card = Mouse();

		// The guest applies a preset on the device page: the open-chain backfill runs and records
		// this (device, preset) pair.
		var presetId = store.InsertPreset(guest.Kind, guest.Id, Vid, Pid, new PresetRecord { Name = "Shared" });
		var preset = new PresetViewModel { Id = presetId, Name = "Shared" };
		vm.Presets.Add(preset);
		vm.Device = card;
		await vm.SelectPresetCommand.ExecuteAsync(preset);
		await store.DrainStorageQueueAsync();

		// The guest signs in. The preset row — same id — is now the user's.
		await store.MigrateGuestToUserAsync(guestSessionId, int.Parse(UserA.Id));
		await store.DrainStorageQueueAsync();

		// A live row of the user's that the preset snapshot does not hold. Any key would do: the
		// backfill's snapshot copy is generic over device_settings, which is what makes it able to
		// close divergences the app adds later.
		owner.Current = UserA;
		store.SetDeviceSetting(UserA.Kind, UserA.Id, Vid, Pid, DeviceKey, "test.liveOnlyKey", 1);
		Assert.DoesNotContain("test.liveOnlyKey", store.GetPresetSettings(presetId).Keys);

		// The PLAIN open, not another preset apply: an apply's first act is
		// LoadPresetSnapshotToDevice, which deletes every live device_settings row and rewrites it
		// from the snapshot — so on that path a backfill that ran and one that was skipped leave
		// storage identical, and nothing can be asserted. A device open leaves the live rows alone,
		// which is the shape the backfill exists for.
		vm.ClearDeviceForExit();
		await vm.SetDeviceAsync(card);
		await store.DrainStorageQueueAsync();

		// Skipped as a duplicate, the divergence survives and SAVE PRESET lights up unprompted.
		Assert.Contains("test.liveOnlyKey", store.GetPresetSettings(presetId).Keys);
	}

	// The control for the test above, and the dedup's own contract: with the owner UNCHANGED the
	// second open must NOT back fill again — one per (device, preset) per session, because two opens
	// resolving the stage/rate independently is how the stored value came to depend on which
	// finished last. It is also what makes the assertion above mean something: the copy is reached
	// only through the backfill, so "the key appeared" can only be the second backfill running.
	[Fact]
	public async Task BackfilledPreset_ForTheSameAccount_IsNotRepeatedOnTheNextOpen()
	{
		using var temp = new TempStore();
		var store = temp.Store;

		var (vm, _, _) = NewVm(store, UserA);
		var card = Mouse();

		var presetId = store.InsertPreset(UserA.Kind, UserA.Id, Vid, Pid, new PresetRecord { Name = "Shared" });
		var preset = new PresetViewModel { Id = presetId, Name = "Shared" };
		vm.Presets.Add(preset);
		vm.Device = card;
		await vm.SelectPresetCommand.ExecuteAsync(preset);
		await store.DrainStorageQueueAsync();

		store.SetDeviceSetting(UserA.Kind, UserA.Id, Vid, Pid, DeviceKey, "test.liveOnlyKey", 1);

		vm.ClearDeviceForExit();
		await vm.SetDeviceAsync(card);
		await store.DrainStorageQueueAsync();

		Assert.DoesNotContain("test.liveOnlyKey", store.GetPresetSettings(presetId).Keys);
	}

	// ── The purge — criterion 9 ──────────────────────────────────────────────────────────────

	// OnOwnerChanged is housekeeping rather than the correctness mechanism (the owner is in the key),
	// so it is asserted for what it claims: after it runs, no other account's entry is left. Proved
	// by changing A's row while A's entry would still be cached — a purged cache re-reads and shows
	// the new value, a surviving one would still show the old.
	[Fact]
	public async Task OnOwnerChanged_DropsTheOtherAccountsRgbCacheEntry()
	{
		using var temp = new TempStore();
		var store = temp.Store;
		SeedRgb(store, UserA, ARed);

		var (vm, _, owner) = NewVm(store, UserA);
		var card = Mouse();

		await OpenAndLeaveAsync(vm, card);

		owner.Current = UserB;
		vm.OnOwnerChanged(UserB);

		owner.Current = UserA;
		SeedRgb(store, UserA, "#ABCDEF");
		await OpenAndLeaveAsync(vm, card);

		Assert.Equal("#ABCDEF", vm.ActiveRgbEffect.SelectedHexColor);
	}

	// The same sweep over the assertion caches: A's recorded stage must not survive it either.
	[Fact]
	public async Task OnOwnerChanged_DropsTheOtherAccountsAssertionCacheEntry()
	{
		using var temp = new TempStore();
		var store = temp.Store;
		var (vm, hid, owner) = NewVm(store, UserA);
		var card = Mouse();

		var presetId = store.InsertPreset(UserA.Kind, UserA.Id, Vid, Pid, new PresetRecord { Name = "A" });
		var preset = new PresetViewModel { Id = presetId, Name = "A" };
		vm.Presets.Add(preset);
		vm.Device = card;
		await vm.SelectPresetCommand.ExecuteAsync(preset);

		vm.NoteAssertedDpiStage(card, 2);

		// Sign out to B and back to A: without the purge, A's entry is still there when A returns.
		owner.Current = UserB;
		vm.OnOwnerChanged(UserB);
		owner.Current = UserA;
		vm.OnOwnerChanged(UserA);

		hid.RaiseDpiStageChanged(new DpiStageChange { VendorId = Vid, ProductId = Pid, DeviceKey = DeviceKey, StageIndex = 2 });

		Assert.True(vm.HasUnsavedPresetChanges);
	}
}
