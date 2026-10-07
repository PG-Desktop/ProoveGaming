using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// When the connect-time restore is re-driven because the SETTINGS OWNER changed.
//
// Every read that restore performs is keyed by the owner, so while the owner is null it returns
// without touching storage or the device — and it normally runs only on the connect TRANSITION,
// which by then has passed. Any launch that does not auto-restore an owner (first run, after a
// sign-out, an expired "Remember me" or an expired guest session) hits exactly that: the DeviceHost
// streams devices while the user is still on the Welcome screen, so the cards appear, the restore
// silently no-ops, and the device keeps its power-on lighting until the user opens its details page.
// That was the "BLAZE RGB only changes when I click the card" report.
//
// HomeViewModel's constructor needs the full device/session service graph, so — as with the battery
// source rules — the decision is a static helper driven directly.
public class HomeViewModelOwnerRestoreTests
{
	private static SettingsOwner Guest(string id = "11111111-1111-1111-1111-111111111111") =>
		new(LocalStorageService.OwnerKindGuest, id);

	private static SettingsOwner User(string id = "42") =>
		new(LocalStorageService.OwnerKindUser, id);

	// ── The case the fix exists for ──────────────────────────────────────────────────────────

	[Fact]
	public void RestoresWhenAnOwnerFirstResolves()
	{
		// Devices connected while the user was still on the Welcome screen: nothing was restored,
		// and picking guest is the first moment a restore can be keyed at all.
		Assert.True(HomeViewModel.ShouldRestoreForOwner(Guest(), lastRestored: null));
	}

	[Fact]
	public void RestoresWhenGuestSignsInAsUser()
	{
		// The signed-in customer's saved presets live in a different bucket from the guest's, so
		// the connected devices must be re-driven against the new one.
		Assert.True(HomeViewModel.ShouldRestoreForOwner(User(), lastRestored: Guest()));
	}

	[Fact]
	public void RestoresWhenADifferentUserSignsIn()
	{
		Assert.True(HomeViewModel.ShouldRestoreForOwner(User("43"), lastRestored: User("42")));
	}

	// ── Where a second hardware write must NOT happen ────────────────────────────────────────

	[Fact]
	public void DoesNotRestoreForAnUnchangedOwner()
	{
		// One sign-in raises both IsGuest and CustomerId, so this method is re-entered with the
		// same owner. BLAZE's firmware bricks its buttons on a burst of applies — the duplicate
		// sweep has to be dropped here, not deduplicated at the hardware.
		Assert.False(HomeViewModel.ShouldRestoreForOwner(User(), lastRestored: User()));
		Assert.False(HomeViewModel.ShouldRestoreForOwner(Guest(), lastRestored: Guest()));
	}

	[Fact]
	public void DoesNotRestoreWhileNoOwnerIsResolved()
	{
		// Pre-guest startup: there is no bucket to read, so there is nothing to send.
		Assert.False(HomeViewModel.ShouldRestoreForOwner(null, lastRestored: null));
	}

	[Fact]
	public void DoesNotRestoreOnSignOut()
	{
		// EndSession clears the owner. Losing one is not a reason to write to the device.
		Assert.False(HomeViewModel.ShouldRestoreForOwner(null, lastRestored: User()));
	}

	[Fact]
	public void RestoresAgainAfterSignOutAndBackIn()
	{
		// The caller records the null, so the same owner returning still counts as a change —
		// without that, signing out and back in would leave the devices unrestored for the session.
		Assert.True(HomeViewModel.ShouldRestoreForOwner(User(), lastRestored: null));
	}

	// ── Whether the sweep may RESET lighting the new owner never configured ──────────────────
	//
	// A different question from ShouldRestoreForOwner above, answered against a different field.
	// ShouldRestoreForOwner asks "is there a new bucket to read"; this asks "is another account's
	// lighting sitting on the board right now, which this owner did not put there". Only the second
	// justifies overwriting a never-configured device — so it is keyed off _lastLitOwner, the last
	// owner the sweep actually restored FOR, which a sign-out deliberately does not clear (losing an
	// owner does not take the departing account's colours off the hardware).
	//
	// The case this pins is QA step 7: a plain launch must leave a never-configured board on its
	// FACTORY lighting. Resetting there would overwrite it with the app's defaults on every start.

	[Fact]
	public void DoesNotResetForTheFirstOwnerOfTheSession()
	{
		// Launch with "Remember me", and "Continue as guest". Nothing has been lit by this app in
		// this process, so there are no foreign colours to take off the board — and a device the
		// account has never configured must keep whatever lighting it powered on with.
		Assert.False(HomeViewModel.ShouldResetUnconfiguredLighting(User(), lastLit: null));
		Assert.False(HomeViewModel.ShouldResetUnconfiguredLighting(Guest(), lastLit: null));
	}

	[Fact]
	public void ResetsWhenOneAccountReplacesAnother()
	{
		// The reported case: B arrives to find A's colours on the board.
		Assert.True(HomeViewModel.ShouldResetUnconfiguredLighting(User("43"), lastLit: User("42")));
		// A guest upgrading to a signed-in account, and the reverse, are the same switch.
		Assert.True(HomeViewModel.ShouldResetUnconfiguredLighting(User(), lastLit: Guest()));
		Assert.True(HomeViewModel.ShouldResetUnconfiguredLighting(Guest(), lastLit: User()));
	}

	[Fact]
	public void ResetsWhenTheNextAccountArrivesAfterASignOut()
	{
		// A → sign-out → B. The sign-out step itself lights nothing (below), so _lastLitOwner is
		// still A when B lands — which is exactly right, because A's colours are still on the board.
		Assert.True(HomeViewModel.ShouldResetUnconfiguredLighting(User("43"), lastLit: User("42")));
	}

	[Fact]
	public void DoesNotResetWhenTheSameAccountSignsBackIn()
	{
		// A → sign-out → A. The board is already wearing this account's own lighting, so a device it
		// has never configured has no foreign colours to be rescued from.
		Assert.False(HomeViewModel.ShouldResetUnconfiguredLighting(User(), lastLit: User()));
		Assert.False(HomeViewModel.ShouldResetUnconfiguredLighting(Guest(), lastLit: Guest()));
	}

	[Fact]
	public void DoesNotResetWhileNoOwnerIsResolved()
	{
		// The sign-out step of the A → sign-out → B sequence, and the pre-guest startup window.
		// Losing an owner is not a reason to write to the device.
		Assert.False(HomeViewModel.ShouldResetUnconfiguredLighting(null, lastLit: User()));
		Assert.False(HomeViewModel.ShouldResetUnconfiguredLighting(null, lastLit: null));
	}

	// ── What the sweep deliberately does NOT do: write the pending-apply mark ────────────────
	//
	// Between 14be823 and 2026-09-12 the sweep asked a third question — "whose remaps, macro
	// bindings, actuation and extended keys are in the board's FLASH" — and answered it by writing
	// the pending-apply mark for every connected card. The question, its field and the marking
	// helper are all gone (docs/plans/add-cyrex-extended-keys, review round 4, M1): the mark's
	// consumers answer it with LoadPresetSnapshotToDevice, which overwrites the live rows with the
	// preset's snapshot. That is right for a preset that arrived from the server and has never been
	// loaded on this machine, and silent data loss for an account switch, where the incoming
	// account's applied-but-unsaved state is legitimate and its own.
	//
	// What carries a switch instead: lighting by the per-card restore below, extended keys by the
	// self-heal that runs at the end of every page open, and keyboard remaps / actuation by the
	// accepted limitation in docs/plans/fix-extended-keys-self-heal.
	//
	// The sweep cases at the bottom of this file therefore assert the ABSENCE of a mark, with an
	// applied preset seeded so that "no mark" cannot pass for the trivial reason that there was
	// nothing to name. The mark itself is unchanged and still the sign-in pull's: its writer is
	// SettingsSyncService and its consumers are pinned in DeviceDetailsPulledPresetApplyTests.

	private static int SeedAppliedPreset(LocalStorageService store, SettingsOwner owner, int vendorId, int productId)
	{
		var presetId = store.InsertPreset(owner.Kind, owner.Id, vendorId, productId,
			new PresetRecord { Name = "Mine", IsDefault = true });
		store.SetAppliedPreset(owner.Kind, owner.Id, vendorId, productId, NoKey, presetId, markDirty: false);
		return presetId;
	}

	private static string? Mark(LocalStorageService store, SettingsOwner owner, int vendorId, int productId) =>
		store.GetSyncMeta(owner.Kind, owner.Id,
			LocalStorageService.SyncMetaDeviceStatePendingApply(vendorId, productId));

	// ── What the sweep actually SENDS (docs/plans/fix-rgb-owner-isolation) ───────────────────
	//
	// The decision above says whether to re-drive the connected devices; these say what the
	// re-drive does to a board the NEW account has never configured. Left alone it keeps burning
	// the previous account's colours, which is the visible half of the account-switch report.
	//
	// RestoreConnectedDevicesForNewOwner passes resetWhenUnset: true and nothing else does, so the
	// flag is exercised on DeviceDetailsViewModel.ReapplyOnConnectAsync directly — HomeViewModel's
	// constructor needs the full device/session service graph, the same reason the decision above
	// is driven through a static helper.

	private const int KbVid = 14000;
	private const int KbPid = 12385;
	private const string NoKey = "";
	private const string DefaultsHex = "#FF00FF";

	private sealed class FakeOwnerProvider(SettingsOwner? owner) : ICurrentOwnerProvider
	{
		public SettingsOwner? GetCurrent() => owner;
	}

	private sealed class RecordingHid : IUsbHidDeviceService
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

#pragma warning disable CS0067 // events required by the interface but unused in these tests
		public event EventHandler<SubscribeOnDevicesResponse>? DeviceFound;
		public event EventHandler<string>? NotifyError;
		public event EventHandler<bool>? HostAvailabilityChanged;
		public event EventHandler<DpiStageChange>? DpiStageChanged;
		public event EventHandler<ReportRateChange>? ReportRateChanged;
		public event EventHandler<BatteryChange>? BatteryChanged;
#pragma warning restore CS0067
	}

	private static (DeviceDetailsViewModel Vm, RecordingHid Hid, LocalStorageService Store) NewDetails(SettingsOwner owner)
	{
		var store = new LocalStorageService("Data Source=:memory:");
		var ownerProvider = new FakeOwnerProvider(owner);
		var hid = new RecordingHid();
		var vm = new DeviceDetailsViewModel(
			usbHidDeviceService: hid,
			localStorage: store,
			settings: new SettingsRepository(store, ownerProvider),
			ownerProvider: ownerProvider);
		vm.PostToUi = action => action();
		return (vm, hid, store);
	}

	// A keyboard: ReapplyLastCustomPresetAsync skips keyboards outright, so the connect restore
	// lands on the RGB fallback — the branch resetWhenUnset lives in.
	private static DoubleCardViewModel KeyboardCard() => new()
	{
		Name = "Test Keyboard",
		DeviceTypeLabel = "Wireless Keyboard",
		VendorId = KbVid,
		ProductId = KbPid,
		SettingsVendorId = KbVid,
		SettingsProductId = KbPid,
		DeviceKey = NoKey,
		SettingsDeviceKey = NoKey,
		SupportsRgb = true,
		Defaults = new DeviceDefaults(
			RgbEnabled: true, RgbEffect: "Static", RgbColorHex: DefaultsHex, RgbBrightness: 100, RgbSpeed: 0),
	};

	[Fact]
	public async Task OwnerChangeSweep_WithNothingStoredForTheNewOwner_PushesTheDevicesDefaultLighting()
	{
		var owner = User("77");
		var (vm, hid, store) = NewDetails(owner);
		using (store)
		{
			await vm.ReapplyOnConnectAsync(KeyboardCard(), resetWhenUnset: true);

			// The card's catalog defaults, encoded the same way the page would: Static = 0x04,
			// brightness 100% = 0xFF, speed 0% = 1.
			Assert.Equal(1, hid.SetLedCallCount);
			Assert.Equal((byte)0xFF, hid.LastLed!.Value.r);
			Assert.Equal((byte)0x00, hid.LastLed!.Value.g);
			Assert.Equal((byte)0xFF, hid.LastLed!.Value.b);
			Assert.Equal((byte)0x04, hid.LastLed!.Value.effect);
			Assert.Equal((byte)0xFF, hid.LastLed!.Value.brightness);
			Assert.Equal((byte)1, hid.LastLed!.Value.speed);
		}
	}

	[Fact]
	public async Task PlainReconnect_WithNothingStored_PushesNothing()
	{
		var owner = User("77");
		var (vm, hid, store) = NewDetails(owner);
		using (store)
		{
			// No owner change, so no resetWhenUnset: a device the account never configured keeps
			// its factory lighting on a replug, exactly as before this change.
			await vm.ReapplyOnConnectAsync(KeyboardCard());

			Assert.Equal(0, hid.SetLedCallCount);
		}
	}

	// An account that turned lighting OFF is saying "not my colours" just as much as one that never
	// configured the device, so the switch has to take the previous account's lighting off the board
	// rather than leave it burning. The off encoding is BuildLedConfig's own — effect 0xED,
	// brightness 0 — not the card's defaults, which would light it up instead.
	[Fact]
	public async Task OwnerChangeSweep_WhenTheNewOwnerStoredRgbButDisabledIt_PushesTheOffEncoding()
	{
		var owner = User("77");
		var (vm, hid, store) = NewDetails(owner);
		using (store)
		{
			store.SaveRgbState(owner.Kind, owner.Id, KbVid, KbPid, NoKey, isEnabled: false, "Static",
				[new RgbEffectRecord { EffectKey = "Static", Hex = "#00FF00", Brightness = 50, Speed = 50 }]);

			await vm.ReapplyOnConnectAsync(KeyboardCard(), resetWhenUnset: true);

			Assert.Equal(1, hid.SetLedCallCount);
			Assert.Equal((byte)0xED, hid.LastLed!.Value.effect);
			Assert.Equal((byte)0, hid.LastLed!.Value.brightness);
			Assert.Equal((byte)0, hid.LastLed!.Value.r);
			Assert.Equal((byte)0, hid.LastLed!.Value.g);
			Assert.Equal((byte)0, hid.LastLed!.Value.b);
		}
	}

	// The other half of the same rule: with no owner change there is no other account's lighting to
	// take off the board, so a stored-but-disabled record still writes nothing on a plain reconnect.
	[Fact]
	public async Task PlainReconnect_WhenTheOwnerStoredRgbButDisabledIt_PushesNothing()
	{
		var owner = User("77");
		var (vm, hid, store) = NewDetails(owner);
		using (store)
		{
			store.SaveRgbState(owner.Kind, owner.Id, KbVid, KbPid, NoKey, isEnabled: false, "Static",
				[new RgbEffectRecord { EffectKey = "Static", Hex = "#00FF00", Brightness = 50, Speed = 50 }]);

			await vm.ReapplyOnConnectAsync(KeyboardCard());

			Assert.Equal(0, hid.SetLedCallCount);
		}
	}

	// ── The sweep ITSELF, not its parts (review round 1, S3) ─────────────────
	//
	// Everything above drives a piece in isolation: the predicates as free functions. That leaves
	// the wiring inside RestoreConnectedDevicesForNewOwner unpinned — and the wiring is where the
	// pending-apply mark was written and is now not (review round 4, M1). A test of the predicates
	// alone cannot see a marking loop being re-added, because the loop is the part that has no
	// predicate any more.
	//
	// RestoreConnectedDevicesForNewOwner and the OwnerSweepState accessor are internal for exactly
	// this (InternalsVisibleTo ProoveGaming.App.Tests): the accessor seeds the two "who was the
	// sweep last driven for" fields, which is the only way to drive the sweep from a chosen
	// transition rather than from whatever a fresh view model happens to start at.
	//
	// Harness: a real HomeViewModel over in-memory storage, HID faked at the transport boundary —
	// the same construction HomeViewModelSettingsPersistTriggerTests uses. The sweep resolves its
	// owner provider and DeviceDetailsViewModel out of the SERVICE PROVIDER rather than the
	// constructor argument, so both go into the collection.

	private static (HomeViewModel Vm, DeviceDetailsViewModel Details) NewHome(
		LocalStorageService store, SettingsOwner? owner)
	{
		var ownerProvider = new FakeOwnerProvider(owner);
		var hid = new RecordingHid();
		var details = new DeviceDetailsViewModel(
			usbHidDeviceService: hid,
			localStorage: store,
			settings: new SettingsRepository(store, ownerProvider),
			ownerProvider: ownerProvider);
		details.PostToUi = action => action();

		var services = new ServiceCollection();
		services.AddSingleton<ICurrentOwnerProvider>(ownerProvider);
		services.AddSingleton(details);
		services.AddTransient<AddDeviceCardViewModel>();
		services.AddTransient<SingleCardViewModel>();
		services.AddTransient<DoubleCardViewModel>();

		var session = new SessionService(a => a());
		var vm = new HomeViewModel(
			hid,
			session,
			store,
			new UserProfile(),
			services.BuildServiceProvider(),
			new SystemNotificationService(NullLogger<SystemNotificationService>.Instance),
			ownerProvider,
			NullLogger<HomeViewModel>.Instance);
		vm.PostToUi = action => action();
		return (vm, details);
	}

	// A connected keyboard, deliberately WITHOUT RGB: ReapplyLastCustomPresetAsync skips keyboards
	// and ReapplyPersistedRgbAsync needs SupportsRgb, so the sweep's trailing per-card restore is
	// inert here and the pending-apply mark is the only thing these cases can be reading.
	private static DoubleCardViewModel ConnectedKeyboardCard(int settingsVendorId = KbVid) => new()
	{
		Name = "Sweep Keyboard",
		DeviceTypeLabel = "Wired Keyboard",
		VendorId = KbVid,
		ProductId = KbPid,
		SettingsVendorId = settingsVendorId,
		SettingsProductId = settingsVendorId <= 0 ? 0 : KbPid,
		DeviceKey = NoKey,
		SettingsDeviceKey = NoKey,
		SupportsRgb = false,
		HasWire = true,
	};

	// The account-to-account switch, which is the transition the sweep used to mark on — and the
	// one M1 showed the mark destroys data on. An applied preset is seeded for the incoming owner,
	// so a marking loop re-added to the sweep would have a real preset id to name and this would
	// fail loudly rather than pass on an empty scope.
	//
	// This is the case for the whole of it: the marking loop had no predicate left once
	// ShouldReapplyDeviceState went, so there is no "decides not to" to pin — only "does not".
	[Fact]
	public async Task Sweep_OnAnAccountSwitch_WritesNoPendingApplyMark()
	{
		using var store = new LocalStorageService("Data Source=:memory:");
		var incoming = User("43");
		SeedAppliedPreset(store, incoming, KbVid, KbPid);

		var (vm, _) = NewHome(store, incoming);
		vm.Devices.Add(ConnectedKeyboardCard());
		vm.OwnerSweepState = (Restored: User("42"), Lit: User("42"));

		vm.RestoreConnectedDevicesForNewOwner();
		// Any write the sweep made would go through EnqueueStorageWrite — it runs from a
		// PropertyChanged handler on the UI thread and must not touch storage there. Drained rather
		// than slept on, so "no mark" cannot pass merely because a write is still in the queue.
		await store.DrainStorageQueueAsync();

		Assert.Null(Mark(store, incoming, KbVid, KbPid));
		// ...and both fields now name the owner the sweep ran for, so the switch itself did happen.
		Assert.Equal(((SettingsOwner?)incoming, (SettingsOwner?)incoming), vm.OwnerSweepState);
	}

	// The sign-out guard, at the level it now lives at. It used to be asserted through the page
	// re-drive's own null check; the re-drive is gone (implementation notes, round 2), so the sweep
	// is where "owner → null writes nothing and does not throw" has to be pinned.
	//
	// _lastLitOwner deliberately keeps naming the DEPARTING owner: its question is "whose colours
	// are on the board", and signing out does not take them off the hardware. _lastRestoredOwner is
	// the one that must go null, so the next sign-in — even of the same account — reads as a change.
	[Fact]
	public async Task Sweep_OnSignOut_WritesNothingAndKeepsTheDepartingOwnerInTheLightingField()
	{
		using var store = new LocalStorageService("Data Source=:memory:");
		var departing = User("42");
		// The departing account has an applied preset, so a sweep that failed to guard on the null
		// owner would have something to write rather than falling over the missing row.
		SeedAppliedPreset(store, departing, KbVid, KbPid);

		var (vm, _) = NewHome(store, owner: null);
		vm.Devices.Add(ConnectedKeyboardCard());
		vm.OwnerSweepState = (Restored: departing, Lit: departing);

		// The whole assertion of "does not throw": OnOwnerChanged still runs for a sign-out (the
		// per-owner cache purge must), and everything after it is guarded.
		vm.RestoreConnectedDevicesForNewOwner();
		await store.DrainStorageQueueAsync();

		Assert.Null(Mark(store, departing, KbVid, KbPid));
		Assert.Equal(((SettingsOwner?)null, (SettingsOwner?)departing), vm.OwnerSweepState);
	}
}
