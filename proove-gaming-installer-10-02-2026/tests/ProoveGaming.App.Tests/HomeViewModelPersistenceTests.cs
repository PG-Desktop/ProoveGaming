using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// VM-level half of the persist-devices-and-notifications plan (review S3): the storage layer is
// covered by LocalStorageServiceOwnerScopedPersistenceTests; these pin HomeViewModel's wiring —
// that the bell and the card list actually read from and write through to that storage, and that
// the owner transitions re-anchor state instead of leaking it across owners.
public class HomeViewModelPersistenceTests
{
    private const int CustomerId = 42;
    private const string UserId = "42";

    private sealed class FakeHid : IUsbHidDeviceService
    {
        public List<HidDeviceResponse> GetDevices() => [];
        public Task StartCommunication() => Task.CompletedTask;
        public void StopCommunication() { }
        public bool CheckIfAllDevicesAreDisconnected() => true;
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
        public bool TryGetLastDpiStage(int vendorId, int productId, string deviceKey, out int stageIndex) { stageIndex = 0; return false; }
        public bool TryGetLastReportRate(int vendorId, int productId, string deviceKey, out int hz) { hz = 0; return false; }
        public Task<KeyActuationInfo?> GetKeyActuationAsync(int vendorId, int productId) => Task.FromResult<KeyActuationInfo?>(null);
        public Task<bool> SetKeyActuationAsync(int vendorId, int productId, int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm) => Task.FromResult(false);
        public Task<bool> SetPerKeyActuationAsync(int vendorId, int productId, IReadOnlyCollection<int> slots, int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm) => Task.FromResult(false);
        public Task<IReadOnlyDictionary<byte, (byte Type, byte Code1, byte Code2)>?> ReadKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
            => Task.FromResult<IReadOnlyDictionary<byte, (byte, byte, byte)>?>(null);
        public Task<(IReadOnlyDictionary<byte, byte> ScancodeToSlot, IReadOnlyDictionary<byte, byte> UserOverrides)?> ProbeKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
            => Task.FromResult<(IReadOnlyDictionary<byte, byte>, IReadOnlyDictionary<byte, byte>)?>(null);
        public Task<bool> SetKeyMappingAsync(int vendorId, int productId, byte matrixIndex, byte type, byte code1, byte code2) => Task.FromResult(false);

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

    private sealed record Harness(
        HomeViewModel Vm,
        LocalStorageService Storage,
        SessionService Session,
        UserProfile User);

    // Signed-in by default (owner = user 42); pass signedIn: false for the pre-guest state.
    private static Harness NewVm(
        LocalStorageService? storage = null, bool signedIn = true, ISettingsSyncService? settingsSync = null)
    {
        storage ??= new LocalStorageService("Data Source=:memory:");
        var session = new SessionService(a => a());
        var user = new UserProfile();
        if (signedIn)
        {
            user.CustomerId = CustomerId;
            user.SessionToken = "token";
        }

        var services = new ServiceCollection();
        services.AddTransient<AddDeviceCardViewModel>();
        services.AddTransient<SingleCardViewModel>();
        services.AddTransient<DoubleCardViewModel>();

        var vm = new HomeViewModel(
            new FakeHid(),
            session,
            storage,
            user,
            services.BuildServiceProvider(),
            new SystemNotificationService(NullLogger<SystemNotificationService>.Instance),
            new CurrentOwnerProvider(session, user),
            NullLogger<HomeViewModel>.Instance,
            settingsSync: settingsSync);

        return new Harness(vm, storage, session, user);
    }

    // Raisable NotificationsPulled for the bell-reload wiring; the pull/push pipelines
    // themselves are out of scope here.
    private sealed class RaisableSettingsSync : ISettingsSyncService
    {
        public Task PullTask => Task.CompletedTask;
        public event Action? PullCompletedWithChanges { add { } remove { } }
        public event Action? SyncCompleted { add { } remove { } }
        public event Action? NotificationsPulled;
        public void RaiseNotificationsPulled() => NotificationsPulled?.Invoke();
        public Task OnGuestUpgradedAsync(int customerId, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
        public Task OnSignedInAsync(int customerId, System.Threading.CancellationToken ct = default) => Task.CompletedTask;
    }

    private static NotificationRecord Bell(string title, bool isRead = false) => new()
    {
        Title = title,
        Subtitle = "PG Taipan",
        CreatedAt = DateTime.Now,
        IsRead = isRead
    };

    private static DeviceRecord Mouse(string name = "PG Taipan") => new()
    {
        Name = name,
        DeviceTypeLabel = "Wireless Mouse",
        CardType = "single",
        VendorId = 0x28E9,
        ProductId = 0x3252,
        BatteryLevel = 55
    };

    // ── Notifications ────────────────────────────────────────────────────────

    [Fact]
    public async Task SignedInLaunch_LoadsStoredNotificationsNewestFirst()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        storage.AddNotification(LocalStorageService.OwnerKindUser, UserId, Bell("Older", isRead: true));
        storage.AddNotification(LocalStorageService.OwnerKindUser, UserId, Bell("Newer"));

        var h = NewVm(storage);
        // The bell now loads through the storage executor; the seam completes when it is in.
        await h.Vm.NotificationsLoaded;

        Assert.Equal(2, h.Vm.Notifications.Count);
        Assert.Equal("Newer", h.Vm.Notifications[0].Title);
        Assert.Equal("Older", h.Vm.Notifications[1].Title);
        Assert.True(h.Vm.Notifications[1].IsRead);
        Assert.Equal(1, h.Vm.UnreadNotificationsCount);
    }

    [Fact]
    public async Task MarkAsRead_WritesThroughToStorage()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        storage.AddNotification(LocalStorageService.OwnerKindUser, UserId, Bell("Unread"));
        var h = NewVm(storage);
        await h.Vm.NotificationsLoaded;

        h.Vm.Notifications[0].MarkAsReadCommand.Execute(null);
        await storage.DrainStorageQueueAsync();

        var stored = Assert.Single(storage.GetNotifications(LocalStorageService.OwnerKindUser, UserId));
        Assert.True(stored.IsRead);
    }

    [Fact]
    public async Task DeletingANotification_RemovesItsRow()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        storage.AddNotification(LocalStorageService.OwnerKindUser, UserId, Bell("Doomed"));
        storage.AddNotification(LocalStorageService.OwnerKindUser, UserId, Bell("Kept"));
        var h = NewVm(storage);
        await h.Vm.NotificationsLoaded;

        h.Vm.Notifications.Single(n => n.Title == "Doomed").DeleteCommand.Execute(null);
        await storage.DrainStorageQueueAsync();

        var stored = Assert.Single(storage.GetNotifications(LocalStorageService.OwnerKindUser, UserId));
        Assert.Equal("Kept", stored.Title);
        Assert.Single(h.Vm.Notifications);
    }

    [Fact]
    public async Task DeleteAll_EmptiesTheStore()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        storage.AddNotification(LocalStorageService.OwnerKindUser, UserId, Bell("One"));
        storage.AddNotification(LocalStorageService.OwnerKindUser, UserId, Bell("Two"));
        var h = NewVm(storage);
        await h.Vm.NotificationsLoaded;

        h.Vm.DeleteAllNotificationsCommand.Execute(null);
        await storage.DrainStorageQueueAsync();

        Assert.Empty(storage.GetNotifications(LocalStorageService.OwnerKindUser, UserId));
        Assert.Empty(h.Vm.Notifications);
    }

    // The bell follows the owner: continuing as guest after a signed-in stretch must not show
    // the user's history to the guest.
    [Fact]
    public async Task BecomingGuest_ReplacesTheBellWithTheGuestHistory()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        storage.AddNotification(LocalStorageService.OwnerKindUser, UserId, Bell("User secret"));
        var h = NewVm(storage);
        await h.Vm.NotificationsLoaded;
        Assert.Single(h.Vm.Notifications);

        h.User.CustomerId = 0; // logout cleared the profile
        h.Session.StartSession();

        // The clear is synchronous and the guest reload finds nothing — awaited so the
        // assertion also holds after the queued read lands.
        await h.Vm.NotificationsLoaded;
        Assert.Empty(h.Vm.Notifications);
    }

    // ── Devices ──────────────────────────────────────────────────────────────

    [Fact]
    public void SignedInLaunch_LoadsStoredDeviceCards()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, Mouse());

        var h = NewVm(storage);

        var card = Assert.Single(h.Vm.Devices.OfType<DeviceCardViewModel>());
        Assert.Equal("PG Taipan", card.Name);
        Assert.True(card.StorageId > 0);
    }

    // The restore half of the low-battery guard (fix-low-battery-notifications, Round 3 S1):
    // RecordToViewModel must seed the card's band mirror from the stored row — and do it
    // before WireDelete's initial check, or every launch with a sub-20 % mouse toasts again.
    [Fact]
    public void SignedInLaunch_RestoresLowBatteryBandOntoTheCard()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        var stored = Mouse();
        stored.LowBatteryAnnouncedBand = 10;
        storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, stored);

        var h = NewVm(storage);

        var card = Assert.Single(h.Vm.Devices.OfType<DeviceCardViewModel>());
        Assert.Equal(10, card.LowBatteryAnnouncedBand);
    }

    // PR #125 m1: the adopt pass is delete+insert, and a card discovery created from scratch
    // (device connected while the user was still on Welcome) carries a zero band mirror.
    // Adopting it must not overwrite the band the user's stored row already holds — that is
    // exactly the launch-time re-toast the persistence exists to suppress.
    [Fact]
    public void ReloadOwnerScopedState_SeedsLowBatteryBand_BeforeAdoptRewritesTheRow()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        var stored = Mouse();
        stored.LowBatteryAnnouncedBand = 20;
        storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, stored);

        var h = NewVm(storage, signedIn: false); // pre-guest: nothing restores at construction
        // Discovery created this card from scratch — its band mirror is 0.
        var live = new SingleCardViewModel
        {
            Name = "PG Taipan",
            DeviceTypeLabel = "Wireless Mouse",
            VendorId = 0x28E9,
            ProductId = 0x3252,
            SettingsVendorId = 0x28E9,
            SettingsProductId = 0x3252,
            HasWire = true,
        };
        h.Vm.Devices.Insert(0, live);

        h.User.CustomerId = CustomerId; // sign-in populated the profile
        h.Vm.ReloadOwnerScopedState();

        // The mirror was seeded from the stored row before the adopt loop reinserted it…
        Assert.Equal(20, live.LowBatteryAnnouncedBand);
        // …so the rewritten row still carries the guard instead of a zero.
        var row = Assert.Single(storage.GetDevices(LocalStorageService.OwnerKindUser, UserId));
        Assert.Equal(20, row.LowBatteryAnnouncedBand);
    }

    [Fact]
    public void ReloadOwnerScopedState_AdoptsOnScreenCardsAndPullsStoredOnes()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, Mouse("Stored Offline"));
        var h = NewVm(storage, signedIn: false); // pre-guest: nothing loads at construction
        Assert.Empty(h.Vm.Devices.OfType<DeviceCardViewModel>());

        // A card discovered before sign-in sits on screen unpersisted.
        var onScreen = new SingleCardViewModel { Name = "Live Mouse", DeviceTypeLabel = "Wired Mouse", VendorId = 1, ProductId = 2 };
        h.Vm.Devices.Insert(0, onScreen);

        h.User.CustomerId = CustomerId; // sign-in populated the profile
        h.Vm.ReloadOwnerScopedState();

        // Adopted: the on-screen card now has a user row; pulled: the stored card is on screen.
        Assert.True(onScreen.StorageId > 0);
        var names = h.Vm.Devices.OfType<DeviceCardViewModel>().Select(c => c.Name).ToList();
        Assert.Contains("Live Mouse", names);
        Assert.Contains("Stored Offline", names);
        Assert.Equal(2, storage.GetDevices(LocalStorageService.OwnerKindUser, UserId).Count);

        // Idempotent: a second reload neither duplicates rows nor cards.
        h.Vm.ReloadOwnerScopedState();
        Assert.Equal(2, storage.GetDevices(LocalStorageService.OwnerKindUser, UserId).Count);
        Assert.Equal(2, h.Vm.Devices.OfType<DeviceCardViewModel>().Count());
    }

    // PR #112 M1: user A's offline cards must not survive their sign-off — the next sign-in's
    // adopt pass would otherwise persist A's devices into B's account. Connected cards stay:
    // they describe hardware physically on the machine, and discovery owns them.
    [Fact]
    public void SignOff_DropsOfflineCards_SoTheNextOwnerAdoptsOnlyConnectedHardware()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, Mouse("A Offline Mouse"));
        var h = NewVm(storage); // signed in as A (42); the offline card loads
        var live = new SingleCardViewModel { Name = "Live Mouse", DeviceTypeLabel = "Wired Mouse", VendorId = 1, ProductId = 2, HasWire = true };
        h.Vm.Devices.Insert(0, live);

        // A signs off (MainWindow.ClearSignedInState clears the profile, then this).
        h.User.CustomerId = 0;
        h.Vm.ClearOwnerScopedState();

        var remaining = Assert.Single(h.Vm.Devices.OfType<DeviceCardViewModel>());
        Assert.Equal("Live Mouse", remaining.Name);
        Assert.Empty(h.Vm.Notifications);
        // A's own store is untouched — the card is back on A's next sign-in.
        Assert.Single(storage.GetDevices(LocalStorageService.OwnerKindUser, UserId));

        // B signs in: only the physically present device lands in B's account.
        h.User.CustomerId = 77;
        h.Vm.ReloadOwnerScopedState();
        var bRow = Assert.Single(storage.GetDevices(LocalStorageService.OwnerKindUser, "77"));
        Assert.Equal("Live Mouse", bRow.Name);
    }

    // ── Merged-transport cards: one card, one row (fix-duplicate-device-cards) ──

    // RAMPAGE as each of its transports enumerates: the 2.4 GHz receiver under its own VID/PID and
    // Container ID, the board's Bluetooth under the board's own with no key.
    private const int DongleVid = 1389;
    private const int DonglePid = 49271;
    private const string DongleKey = "C1";
    private const int BluetoothVid = 10473;
    private const int BluetoothPid = 12288;

    private static DeviceRecord Rampage(int vendorId, int productId, string deviceKey) => new()
    {
        Name = "RAMPAGE",
        DeviceTypeLabel = "Keyboard",
        CardType = "single",
        VendorId = vendorId,
        ProductId = productId,
        DeviceKey = deviceKey,
        MergeTransports = true
    };

    private static SingleCardViewModel LiveRampageOnBluetooth() => new()
    {
        Name = "RAMPAGE",
        DeviceTypeLabel = "Keyboard",
        VendorId = BluetoothVid,
        ProductId = BluetoothPid,
        DeviceKey = string.Empty,
        MergeTransports = true,
        HasBluetooth = true
    };

    // THE REPRODUCTION (field report). The user's account holds a RAMPAGE row persisted over the
    // dongle; the keyboard is now live over Bluetooth, so the card on screen carries the OTHER
    // routing identity. Sign-in adopts the live card and then revives every stored row that is not
    // on screen.
    // Pre-fix: the stored row's raw tuple matched no on-screen tuple, so it was revived as a
    // SECOND, permanently "NO CONNECTION" RAMPAGE card that live discovery could never adopt —
    // two cards and two rows.
    [Fact]
    public void SignIn_WithAStoredGhostRow_LeavesOneMergedCard_AndItIsTheConnectedOne()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, Rampage(DongleVid, DonglePid, DongleKey));
        var h = NewVm(storage, signedIn: false); // pre-guest: nothing loads at construction
        h.Vm.Devices.Insert(0, LiveRampageOnBluetooth());

        h.User.CustomerId = CustomerId; // sign-in populated the profile
        h.Vm.ReloadOwnerScopedState();

        var card = Assert.Single(h.Vm.Devices.OfType<DeviceCardViewModel>());
        Assert.Equal("RAMPAGE", card.Name);
        Assert.True(card.Connected);
        Assert.Single(storage.GetDevices(LocalStorageService.OwnerKindUser, UserId));
    }

    // The API v2 branch subscribed PullCompletedWithChanges → ReloadOwnerScopedState, so the
    // adopt-and-revive pass now runs TWICE per sign-in. Whatever the first pass leaves, the second
    // must not grow — in cards or in rows.
    [Fact]
    public void RepeatedReloadOwnerScopedState_IsIdempotent_ForMergedCards()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, Rampage(DongleVid, DonglePid, DongleKey));
        var h = NewVm(storage, signedIn: false);
        h.Vm.Devices.Insert(0, LiveRampageOnBluetooth());
        h.User.CustomerId = CustomerId;

        h.Vm.ReloadOwnerScopedState();
        var cardsAfterFirst = h.Vm.Devices.OfType<DeviceCardViewModel>().Count();
        var rowsAfterFirst = storage.GetDevices(LocalStorageService.OwnerKindUser, UserId).Count;

        h.Vm.ReloadOwnerScopedState();

        Assert.Equal(1, cardsAfterFirst);
        Assert.Equal(1, rowsAfterFirst);
        Assert.Equal(cardsAfterFirst, h.Vm.Devices.OfType<DeviceCardViewModel>().Count());
        Assert.Equal(rowsAfterFirst, storage.GetDevices(LocalStorageService.OwnerKindUser, UserId).Count);
    }

    // The collapse must stop at merged-transport products: two identical units of one NON-merged
    // model are told apart by their Container ID (PR #112 M2), and the one that is currently
    // offline still has to come back as its own card.
    [Fact]
    public void SignIn_WithTwoStoredUnitsOfANonMergedModel_KeepsBothCards()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        var unitA = Mouse();
        unitA.DeviceKey = "A";
        var unitB = Mouse();
        unitB.DeviceKey = "B";
        storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, unitA);
        storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, unitB);

        var h = NewVm(storage, signedIn: false);
        // Unit A is the one physically present right now.
        h.Vm.Devices.Insert(0, new SingleCardViewModel
        {
            Name = "PG Taipan",
            DeviceTypeLabel = "Wireless Mouse",
            VendorId = 0x28E9,
            ProductId = 0x3252,
            DeviceKey = "A",
            HasWire = true
        });

        h.User.CustomerId = CustomerId;
        h.Vm.ReloadOwnerScopedState();

        var cards = h.Vm.Devices.OfType<DeviceCardViewModel>().ToList();
        Assert.Equal(2, cards.Count);
        Assert.Equal(new[] { "A", "B" }, cards.Select(c => c.DeviceKey).OrderBy(k => k).ToArray());
        Assert.Equal(2, storage.GetDevices(LocalStorageService.OwnerKindUser, UserId).Count);
    }

    // Review S1: a card carried over from a signed-in stretch kept its user-store StorageId, so
    // the guest registration skipped it (register only takes StorageId == 0) and the card was
    // absent from the guest session on the next restart.
    [Fact]
    public async Task BecomingGuest_ReRegistersCarriedOverCardsIntoTheGuestSession()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, Mouse());
        var h = NewVm(storage);
        var card = Assert.Single(h.Vm.Devices.OfType<DeviceCardViewModel>());
        Assert.True(card.StorageId > 0); // user row id from the signed-in load

        h.User.CustomerId = 0; // logout cleared the profile
        h.Session.StartSession();
        // The adopt flush is queued on the storage executor now — drain before reading rows.
        await storage.DrainStorageQueueAsync();

        // The card was adopted into the fresh guest session and flushed to the guest rows.
        var guestRow = Assert.Single(storage.GetDevices());
        Assert.Equal("PG Taipan", guestRow.Name);
        // The user's own row is untouched — their card is back on their next sign-in.
        Assert.Single(storage.GetDevices(LocalStorageService.OwnerKindUser, UserId));
    }

    // ── Notifications raised before any owner exists (D3) ─────────────────────

    // THE REPRODUCTION (D3, fix-owner-scoped-state-persistence). HomeView sits in MainWindow's
    // visual tree from window open, so HomeView.OnLoaded starts discovery while the user is still on
    // the Welcome screen. Every "New device connected" raised in that window has no owner to key a
    // row by, and LoadNotificationsForOwner then cleared the list when the owner finally resolved.
    // Pre-fix: the entry was gone from the bell AND the notifications table held nothing — on this
    // launch path not one row had ever been inserted.
    [Fact]
    public async Task NotificationsRaisedBeforeAnyOwner_ArePersistedToTheFirstOwner()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        var h = NewVm(storage, signedIn: false); // Welcome screen: CurrentOwner is null
        await h.Vm.NotificationsLoaded;

        h.Vm.AddNotification("New device connected", "PG Taipan");
        Assert.Single(h.Vm.Notifications);
        Assert.Empty(storage.GetNotifications(LocalStorageService.OwnerKindUser, UserId));

        h.User.CustomerId = CustomerId; // sign-in populated the profile
        h.Vm.ReloadOwnerScopedState();
        await h.Vm.NotificationsLoaded;

        // Exactly once, in the bell and in the table — the flush rides the storage executor ahead
        // of the reload, so the reload observes the insert instead of racing it.
        var shown = Assert.Single(h.Vm.Notifications);
        Assert.Equal("New device connected", shown.Title);
        Assert.True(shown.StorageId > 0); // came back as a real row, not the in-memory item
        var row = Assert.Single(storage.GetNotifications(LocalStorageService.OwnerKindUser, UserId));
        Assert.Equal("New device connected", row.Title);
        Assert.Equal("PG Taipan", row.Subtitle);
    }

    // The same launch, but the user continues as guest instead of signing in: the entry belongs to
    // whichever owner arrives first, and a guest is an owner.
    [Fact]
    public async Task NotificationsRaisedBeforeAnyOwner_AreFlushedToAnArrivingGuest()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        var h = NewVm(storage, signedIn: false);
        await h.Vm.NotificationsLoaded;

        h.Vm.AddNotification("New device connected", "PG Taipan");

        h.Session.StartSession(); // "Continue as guest" — raises IsGuest, which reloads the bell
        await h.Vm.NotificationsLoaded;

        Assert.Single(h.Vm.Notifications);
        var guestId = h.Session.SessionId.ToString("D");
        Assert.Single(storage.GetNotifications(LocalStorageService.OwnerKindGuest, guestId));
    }

    // The latch's other direction, and the reason the flush is one-shot rather than "whenever there
    // is no owner". After a sign-out the session-expired entry belongs to NOBODY; flushing it to
    // the next owner would hand one user an entry raised during another user's session.
    [Fact]
    public async Task PostSignOutNotification_IsNotFlushedToTheNextOwner()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        var h = NewVm(storage); // signed in as A — an owner exists, so the latch is already set
        await h.Vm.NotificationsLoaded;

        h.User.CustomerId = 0; // A signs off
        h.Vm.ClearOwnerScopedState();
        await h.Vm.NotificationsLoaded;

        h.Vm.AddNotification("Session expired", string.Empty); // raised with no owner

        h.User.CustomerId = 77; // a DIFFERENT account signs in on the same machine
        h.Vm.ReloadOwnerScopedState();
        await h.Vm.NotificationsLoaded;

        Assert.Empty(h.Vm.Notifications);
        Assert.Empty(storage.GetNotifications(LocalStorageService.OwnerKindUser, "77"));
        // Nor did it leak back into A's history.
        Assert.Empty(storage.GetNotifications(LocalStorageService.OwnerKindUser, UserId));
    }

    // The flush happens at most once per process: a second owner change must not re-flush entries
    // that were already attributed, nor sweep up entries raised after the first owner arrived.
    [Fact]
    public async Task PreOwnerFlush_HappensAtMostOnce()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        var h = NewVm(storage, signedIn: false);
        await h.Vm.NotificationsLoaded;
        h.Vm.AddNotification("New device connected", "PG Taipan");

        h.User.CustomerId = CustomerId;
        h.Vm.ReloadOwnerScopedState();
        await h.Vm.NotificationsLoaded;
        Assert.Single(storage.GetNotifications(LocalStorageService.OwnerKindUser, UserId));

        h.Vm.ReloadOwnerScopedState(); // the post-pull refresh runs the same pass again
        await h.Vm.NotificationsLoaded;

        Assert.Single(storage.GetNotifications(LocalStorageService.OwnerKindUser, UserId));
        Assert.Single(h.Vm.Notifications);
    }

    // Review round 1, S1. The flush captures items by `StorageId == 0`, but that only means "not yet
    // persisted": AddNotification hands the insert to the storage executor and the id arrives with
    // its continuation. So an entry raised while an owner ALREADY exists, but before the first
    // LoadNotificationsForOwner, is still 0 at capture time. Pre-fix the latch was set only by the
    // reload, so that entry was swept into pendingFlush and written a SECOND time — one bell entry,
    // two rows. AddNotification now sets the latch itself when it attributes an entry to an owner,
    // which makes the capture's premise true by construction.
    // Not reachable through the production constructor today (see the review); driven here through
    // the internal seams so the invariant is guarded rather than left to an ordering accident.
    [Fact]
    public async Task NotificationRaisedWithAnOwner_BeforeTheFirstReload_IsPersistedExactlyOnce()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        // No owner at construction, so the constructor's reload leaves the latch unset.
        var h = NewVm(storage, signedIn: false);
        await h.Vm.NotificationsLoaded;

        // Hold the storage executor so the notification INSERT below cannot complete before the
        // reload captures. Without this the race is unobservable in-process: the insert runs on a
        // pool thread while ReloadOwnerScopedState is still doing its synchronous device work, so
        // StorageId is already set by the time the capture reads it and the bug hides. FIFO puts
        // the insert strictly behind this gate.
        using var gate = new System.Threading.ManualResetEventSlim(false);
        _ = storage.RunOnStorageAsync(() => gate.Wait());

        // The owner arrives BEFORE the entry is raised, so AddNotification persists it directly...
        h.User.CustomerId = CustomerId;
        h.Vm.AddNotification("New device connected", "PG Taipan");

        // ...and only THEN does the first owner-resolving reload run, while that insert is still
        // queued and the item therefore still reads StorageId == 0.
        h.Vm.ReloadOwnerScopedState();

        gate.Set();
        await h.Vm.NotificationsLoaded;
        await storage.DrainStorageQueueAsync();

        var rows = storage.GetNotifications(LocalStorageService.OwnerKindUser, UserId);
        Assert.Single(rows);
        Assert.Equal("New device connected", rows[0].Title);
        Assert.Single(h.Vm.Notifications);
    }

    // ── The revive decision is taken before the adopt loop writes (D1) ────────

    // CHARACTERIZATION, not a reproduction. AddDevice's delete predicate and the revive skip
    // predicate coincide on the current code, so reordering the read is behaviour-preserving and
    // nothing here failed before the change — see the plan's Q4. What this pins is that the revive
    // outcome is a FUNCTION OF THE PRE-ADOPT ROW SET under the shared swallow rule, rather than a
    // by-product of the adopt loop's deletes having already run. Written against the
    // IsRepresentedOnScreen seam so it fails loudly if the two rules ever drift apart.
    [Fact]
    public void ReviveDecision_IsTakenAgainstThePreAdoptSnapshot()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        // A ghost row for the live keyboard, under the OTHER transport identity...
        storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, Rampage(DongleVid, DonglePid, DongleKey));
        // ...and a genuinely offline card that is nowhere on screen.
        storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, Mouse("Stored Offline"));

        var h = NewVm(storage, signedIn: false);
        h.Vm.Devices.Insert(0, LiveRampageOnBluetooth());

        // The prediction, computed from the row set as it stands BEFORE anything is adopted.
        var storedBeforeAdopt = storage.GetDevices(LocalStorageService.OwnerKindUser, UserId);
        var onScreen = h.Vm.Devices.OfType<DeviceCardViewModel>()
            .Select(HomeViewModel.CardKey.From)
            .ToList();
        var expectedRevived = storedBeforeAdopt
            .Where(r => !HomeViewModel.IsRepresentedOnScreen(r, onScreen))
            .Select(r => r.Name)
            .OrderBy(n => n)
            .ToArray();
        // The merged card swallows the dongle row; only the offline mouse is revived.
        Assert.Equal(new[] { "Stored Offline" }, expectedRevived);

        h.User.CustomerId = CustomerId;
        h.Vm.ReloadOwnerScopedState();

        var revived = h.Vm.Devices.OfType<DeviceCardViewModel>()
            .Where(c => !c.Connected)
            .Select(c => c.Name)
            .OrderBy(n => n)
            .ToArray();
        Assert.Equal(expectedRevived, revived);
        // And the ghost row was swallowed rather than revived: one card per name, two rows total.
        Assert.Equal(2, h.Vm.Devices.OfType<DeviceCardViewModel>().Count());
        Assert.Equal(2, storage.GetDevices(LocalStorageService.OwnerKindUser, UserId).Count);
    }

    // The swallow rule itself, stated as the asymmetry it is. A merged card swallows a stored row
    // written under any transport identity; the reverse must never hold, or two identical units of
    // a non-merged model would collapse into one card.
    [Fact]
    public void IsRepresentedOnScreen_MergedCard_SwallowsAnyTransportIdentityOfThatName()
    {
        var onScreen = new[] { HomeViewModel.CardKey.From(LiveRampageOnBluetooth()) };

        Assert.True(HomeViewModel.IsRepresentedOnScreen(Rampage(DongleVid, DonglePid, DongleKey), onScreen));
        // Case-folded, matching AddDevice's `name = $name COLLATE NOCASE`.
        var lowerCased = Rampage(DongleVid, DonglePid, DongleKey);
        lowerCased.Name = "rampage";
        Assert.True(HomeViewModel.IsRepresentedOnScreen(lowerCased, onScreen));
    }

    [Fact]
    public void IsRepresentedOnScreen_NonMergedCard_SwallowsOnlyItsExactTuple()
    {
        var unitA = Mouse();
        unitA.DeviceKey = "A";
        var unitB = Mouse();
        unitB.DeviceKey = "B";
        var onScreen = new[]
        {
            HomeViewModel.CardKey.From(new SingleCardViewModel
            {
                Name = "PG Taipan",
                DeviceTypeLabel = "Wireless Mouse",
                VendorId = 0x28E9,
                ProductId = 0x3252,
                DeviceKey = "A"
            })
        };

        Assert.True(HomeViewModel.IsRepresentedOnScreen(unitA, onScreen));
        // The second physical unit is NOT represented — it must come back as its own card.
        Assert.False(HomeViewModel.IsRepresentedOnScreen(unitB, onScreen));
    }

    // A stored MERGED row is not swallowed by a non-merged card of the same name: the asymmetry
    // runs one way only, and the card on screen is what decides.
    [Fact]
    public void IsRepresentedOnScreen_NonMergedCard_DoesNotSwallowAMergedRowOfAnotherTransport()
    {
        var onScreen = new[]
        {
            HomeViewModel.CardKey.From(new SingleCardViewModel
            {
                Name = "RAMPAGE",
                DeviceTypeLabel = "Keyboard",
                VendorId = BluetoothVid,
                ProductId = BluetoothPid,
                DeviceKey = string.Empty
            })
        };

        Assert.False(HomeViewModel.IsRepresentedOnScreen(Rampage(DongleVid, DonglePid, DongleKey), onScreen));
    }

    // ── Notifications sync wiring (docs/plans/add-api-v21-notifications-sync) ─

    // The type slug rides on the icon a call site picks and round-trips through storage —
    // the internal seam uses the default (iconless) icon, whose category is "info".
    [Fact]
    public async Task AddNotification_TypeRoundTripsThroughStorageAndReload()
    {
        var h = NewVm();
        h.Vm.AddNotification("Typed entry", "subtitle");
        await h.Vm.LastNotificationPersist;

        var stored = Assert.Single(h.Storage.GetNotifications(LocalStorageService.OwnerKindUser, UserId));
        Assert.Equal(NotificationTypes.Info, stored.Type);

        var reloaded = NewVm(h.Storage);
        await reloaded.Vm.NotificationsLoaded;
        Assert.Equal(NotificationTypes.Info, Assert.Single(reloaded.Vm.Notifications).Type);
    }

    // NotificationsPulled (a poll merged server rows on a pool thread) reloads the bell for
    // the signed-in owner — the pulled entry appears without any user action.
    [Fact]
    public async Task NotificationsPulled_UserOwner_ReloadsTheBell()
    {
        var sync = new RaisableSettingsSync();
        var h = NewVm(settingsSync: sync);
        h.Vm.PostToUi = a => a(); // inline: the handler marshals through this seam
        await h.Vm.NotificationsLoaded;
        Assert.Empty(h.Vm.Notifications);

        // What the poll's merge writes: a pull-shaped row carrying the server's id.
        h.Storage.AddNotification(LocalStorageService.OwnerKindUser, UserId, new NotificationRecord
        {
            Title = "Announcement",
            Subtitle = "from the server",
            CreatedAt = DateTime.Now,
            ServerId = 7,
            Type = NotificationTypes.AppUpdate,
        }, markDirty: false);

        sync.RaiseNotificationsPulled();
        await h.Vm.NotificationsLoaded;

        Assert.Equal("Announcement", Assert.Single(h.Vm.Notifications).Title);
    }

    // No signed-in owner — the event must not touch the bell (a poll racing a sign-out, or
    // an event arriving on the welcome screen).
    [Fact]
    public async Task NotificationsPulled_NoUserOwner_DoesNotReload()
    {
        var sync = new RaisableSettingsSync();
        var h = NewVm(signedIn: false, settingsSync: sync);
        h.Vm.PostToUi = a => a();
        await h.Vm.NotificationsLoaded;
        h.Storage.AddNotification(LocalStorageService.OwnerKindUser, UserId, new NotificationRecord
        {
            Title = "Stranger's entry",
            CreatedAt = DateTime.Now,
            ServerId = 7,
        }, markDirty: false);

        sync.RaiseNotificationsPulled();
        await h.Vm.NotificationsLoaded;

        Assert.Empty(h.Vm.Notifications);
    }
}
