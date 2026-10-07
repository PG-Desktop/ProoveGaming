using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// The PERSIST TRIGGER of docs/plans/fix-settings-bucket-identity — the third link the pure-helper
// suites do not reach (review round 2, m3). HomeViewModelSettingsIdentityTests covers what the
// stamp computes and LocalStorageServiceOwnerScopedPersistenceTests covers that a write round-trips;
// neither covers whether the discovery tick actually ISSUES the write. That gap is exactly how M1
// shipped: a merged-transport card's CardIdentity is (NAME, 0, 0, "") by construction, so the
// "identity moved" persist condition never fired for the pin ApplyDeviceIdentity had just stamped,
// and a healed two-transport row kept settings_* = 0 forever.
//
// Harness mirrors HomeViewModelPersistenceTests (real HomeViewModel + in-memory storage, HID faked
// at the transport boundary). The tick itself is driven by invoking the private per-sender handlers
// (HandleUsbHidDeviceFoundAsync / HandleBluetoothDeviceFoundAsync) through reflection: the only
// production entry point is an async-void event handler serialized by a private SemaphoreSlim, so
// a test cannot even tell when one event's processing has finished, and adding a seam for that
// would be a production change out of this round's scope.
//
// Every restored card is pre-marked with its live transport flag before the tick so the
// disconnected→connected transition does not fire: that path calls ReapplyOnConnect, which resolves
// DeviceDetailsViewModel's full service graph, and the persist trigger under test is independent of
// the transition. It also keeps the tick's synchronous window free of notification writes, so the
// storage-access counts below attribute cleanly.
//
// With the transition and the battery poll fenced off like that, everything the found-card branch
// does — restamp, identity, PERSIST DECISION — executes synchronously on the invoking thread; the
// handler's first true await is the terminal audio-tracking dispatch. That synchronous window is
// what gives the "a steady-state tick wrote NOTHING" assertions their happens-before WITHOUT
// pumping Dispatcher.UIThread.RunJobs, which the process-wide dispatcher's first-touch thread
// affinity makes unsafe from an arbitrary test class in the sequential suite (the AssemblyInfo.cs
// trap; the DeviceDetails suites' "inline, not Post + RunJobs" comments are the same avoidance).
// The assumption is guarded by construction: every negative test shares RunTick with a positive
// sibling that asserts the row WAS rewritten immediately after the call, so an await ever inserted
// ahead of the persist decision fails that sibling loudly instead of letting the negative test
// pass vacuously.
public class HomeViewModelSettingsPersistTriggerTests
{
    private const string User = LocalStorageService.OwnerKindUser;
    private const int CustomerId = 42;
    private const string UserId = "42";

    // RAMPAGE's real identities — the review's reproduction scenario. The board (and its wired
    // transport) is (10473, 12888); the 2.4 GHz receiver enumerates under (1389, 49271).
    private const int BoardVid = 10473;
    private const int WiredPid = 12888;
    private const int DongleVid = 1389;
    private const int DonglePid = 49271;

    // COSMO's and STARLIGHT's Bluetooth links both enumerate under the SAME routing pair (see
    // AllowedDevices in the DeviceHost appsettings), which is what makes their restored cards
    // collapse into one bucket while settings_* = 0.
    private const int SharedBluetoothVid = 1494;
    private const int SharedBluetoothPid = 10;
    private const int HeadsetVendor = 3468;
    private const int CosmoPid = 820;
    private const int StarlightPid = 877;

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

    private sealed record Harness(HomeViewModel Vm, LocalStorageService Storage);

    private static Harness NewVm(LocalStorageService storage)
    {
        var session = new SessionService(a => a());
        var user = new UserProfile { CustomerId = CustomerId, SessionToken = "token" };

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
            NullLogger<HomeViewModel>.Instance);

        return new Harness(vm, storage);
    }

    // ── Driving one discovery tick ───────────────────────────────────────────

    private static readonly MethodInfo HidTickMethod = typeof(HomeViewModel).GetMethod(
        "HandleUsbHidDeviceFoundAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo BluetoothTickMethod = typeof(HomeViewModel).GetMethod(
        "HandleBluetoothDeviceFoundAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    // Runs one discovery tick. Under this class's fixtures the whole mutation — restamp, identity,
    // persist decision — has executed by the time Invoke returns (see the class note on the
    // synchronous window); the returned task is left suspended at the handler's terminal
    // audio-tracking dispatch, whose job is a no-op here (no audio monitor is wired). A handler
    // that threw inside that window surfaces as the test failure it is instead of vanishing into
    // an unobserved task.
    private static void RunTick(HomeViewModel vm, List<HidDeviceResponse> report) =>
        RunTick((Task)HidTickMethod.Invoke(vm, [report])!);

    private static void RunTick(HomeViewModel vm, BluetoothDeviceResponse report) =>
        RunTick((Task)BluetoothTickMethod.Invoke(vm, [report])!);

    private static void RunTick(Task tick)
    {
        if (tick.IsFaulted)
            tick.GetAwaiter().GetResult();
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    // A devices row as HealDuplicateDeviceRows leaves it for a two-transport user: merged, keyed
    // by whichever transport persisted last, with settings_* at the 0 / 0 / "" the ALTER TABLE
    // default gives every pre-existing row.
    private static DeviceRecord LegacyMergedRow(string name, int vendorId, int productId) => new()
    {
        Name = name,
        DeviceTypeLabel = "Keyboard",
        CardType = "single",
        VendorId = vendorId,
        ProductId = productId,
        DeviceKey = string.Empty,
        MergeTransports = true,
        // SettingsVendorId / SettingsProductId / SettingsDeviceKey deliberately left 0 / 0 / "".
    };

    // BatteryUnsupported keeps the tick's fire-and-forget HID battery poll out of these tests, so
    // the storage-access counts below have exactly one possible writer: PersistDeviceCard.
    private static List<HidDeviceResponse> RampageOnDongle() =>
    [
        new HidDeviceResponse
        {
            ClientName = "RAMPAGE",
            VendorId = DongleVid.ToString(),
            ProductId = DonglePid.ToString(),
            DeviceKey = "receiver-container-id",
            CatalogEntry = new DeviceCatalogEntry
            {
                Name = "RAMPAGE",
                DeviceName = "RAMPAGE",
                DeviceType = "Keyboard",
                CardType = "single",
                ConnectionType = "Dongle2_4G",
                MergeTransports = true,
                SettingsVendorId = BoardVid,
                SettingsProductId = WiredPid,
                BatteryUnsupported = true,
            },
        },
    ];

    private static BluetoothDeviceResponse HeadsetOnBluetooth(string name, int settingsProductId) => new()
    {
        ClientName = name,
        IsConnected = true,
        BatteryLevel = 0,
        CatalogEntry = new DeviceCatalogEntry
        {
            Name = name,
            DeviceName = name,
            DeviceType = "Wireless Headphones",
            CardType = "single",
            ConnectionType = "Bluetooth",
            VendorId = SharedBluetoothVid,
            ProductId = SharedBluetoothPid,
            MergeTransports = true,
            SettingsVendorId = HeadsetVendor,
            SettingsProductId = settingsProductId,
            BatteryUnsupported = true,
        },
    };

    private static (int VendorId, int ProductId, string DeviceKey) SettingsOf(DeviceCardViewModel card) =>
        (card.SettingsVendorId, card.SettingsProductId, card.SettingsDeviceKey ?? string.Empty);

    private static (int VendorId, int ProductId, string DeviceKey) SettingsOf(DeviceRecord row) =>
        (row.SettingsVendorId, row.SettingsProductId, row.SettingsDeviceKey ?? string.Empty);

    // ── The M1 reproduction (HID discovery path) ─────────────────────────────

    // THE REPRODUCTION for review round 2, M1. A two-transport RAMPAGE user upgrades:
    // HealDuplicateDeviceRows leaves one merged row (dongle-keyed here) and the settings columns
    // arrive as 0. The restored card therefore addresses the routing fallback (1389, 49271, "") —
    // asserted below as the precondition — and the first discovery tick stamps the pin
    // (10473, 12888, ""). The row MUST then be rewritten with the pin (plan acceptance criterion
    // 7): CardIdentity is blind to the settings triple for a merged card, so before the fix the
    // persist condition never fired, the columns stayed 0 for every later launch, and each
    // pre-discovery page open kept manufacturing rows in the unreachable fallback bucket.
    [Fact]
    public void RestoredMergedRow_WithLegacySettingsColumns_IsRewrittenWithThePinnedPair_OnTheFirstTick()
    {
        using var storage = new LocalStorageService("Data Source=:memory:");
        storage.AddDevice(User, UserId, LegacyMergedRow("RAMPAGE", DongleVid, DonglePid));
        var h = NewVm(storage);
        var card = Assert.Single(h.Vm.Devices.OfType<DeviceCardViewModel>());
        // The defect's precondition: before discovery the card falls back to the routing pair.
        Assert.Equal((DongleVid, DonglePid, ""), SettingsOf(card));
        card.HasDongle = true; // see the class note on pre-marking the live transport

        RunTick(h.Vm, RampageOnDongle());

        // The stamp reached the card...
        Assert.Equal((BoardVid, WiredPid, ""), SettingsOf(card));
        // ...and — the point of M1 — the ROW: one row, rewritten in place with the pinned pair,
        // its routing identity still following the live transport.
        var row = Assert.Single(storage.GetDevices(User, UserId));
        Assert.Equal((BoardVid, WiredPid, ""), SettingsOf(row));
        Assert.Equal(DongleVid, row.VendorId);
        Assert.Equal(DonglePid, row.ProductId);
    }

    // The guard's reason to exist: discovery is a recurring poll, and once the row carries the
    // pin a tick that changes nothing must WRITE nothing — persisting unconditionally would be
    // one storage write per device per tick for the life of the session.
    [Fact]
    public void SteadyState_ASecondIdenticalTick_IssuesNoStorageWrite()
    {
        using var storage = new LocalStorageService("Data Source=:memory:");
        storage.AddDevice(User, UserId, LegacyMergedRow("RAMPAGE", DongleVid, DonglePid));
        var h = NewVm(storage);
        var card = Assert.Single(h.Vm.Devices.OfType<DeviceCardViewModel>());
        card.HasDongle = true;
        RunTick(h.Vm, RampageOnDongle()); // corrects the row — asserted by the test above

        var accesses = 0;
        storage.OnBeforeStorageAccess = () => Interlocked.Increment(ref accesses);
        RunTick(h.Vm, RampageOnDongle());
        storage.OnBeforeStorageAccess = null;

        Assert.Equal(0, accesses);
        Assert.Equal((BoardVid, WiredPid, ""), SettingsOf(Assert.Single(storage.GetDevices(User, UserId))));
    }

    // ── The Bluetooth discovery path ─────────────────────────────────────────

    // Same trigger through HandleBluetoothDeviceFoundAsync, which before the fix never persisted
    // an EXISTING card at all: a restored card whose stored settings_* columns the Bluetooth
    // stamp corrected kept the stale bucket until some other path happened to write the row.
    [Fact]
    public void BluetoothTick_RewritesARestoredMergedRow_WhoseSettingsTripleItCorrected()
    {
        using var storage = new LocalStorageService("Data Source=:memory:");
        storage.AddDevice(User, UserId, LegacyMergedRow("COSMO", SharedBluetoothVid, SharedBluetoothPid));
        var h = NewVm(storage);
        var card = Assert.Single(h.Vm.Devices.OfType<DeviceCardViewModel>());
        Assert.Equal((SharedBluetoothVid, SharedBluetoothPid, ""), SettingsOf(card));
        card.HasBluetooth = true;

        RunTick(h.Vm, HeadsetOnBluetooth("COSMO", CosmoPid));

        Assert.Equal((HeadsetVendor, CosmoPid, ""), SettingsOf(Assert.Single(storage.GetDevices(User, UserId))));

        // Steady state on this branch too: an identical follow-up event restamps identical
        // values and writes nothing.
        var accesses = 0;
        storage.OnBeforeStorageAccess = () => Interlocked.Increment(ref accesses);
        RunTick(h.Vm, HeadsetOnBluetooth("COSMO", CosmoPid));
        storage.OnBeforeStorageAccess = null;
        Assert.Equal(0, accesses);
    }

    // Plan acceptance criterion 10, on the restored-card path. COSMO and STARLIGHT declare the
    // SAME Bluetooth routing pair, so two headsets last persisted over Bluetooth restore into ONE
    // fallback bucket — each showing and overwriting the other's settings. Discovery stamps each
    // with its own product pin, and the rows must be rewritten apart, not left collapsed.
    [Fact]
    public void CosmoAndStarlight_RestoredFromBluetoothRows_AreRewrittenIntoSeparateBuckets()
    {
        using var storage = new LocalStorageService("Data Source=:memory:");
        storage.AddDevice(User, UserId, LegacyMergedRow("COSMO", SharedBluetoothVid, SharedBluetoothPid));
        storage.AddDevice(User, UserId, LegacyMergedRow("STARLIGHT", SharedBluetoothVid, SharedBluetoothPid));
        var h = NewVm(storage);
        var cards = h.Vm.Devices.OfType<DeviceCardViewModel>().ToList();
        Assert.Equal(2, cards.Count);
        // The defect's precondition: both restored cards address ONE bucket before discovery.
        Assert.All(cards, c => Assert.Equal((SharedBluetoothVid, SharedBluetoothPid, ""), SettingsOf(c)));
        foreach (var c in cards)
            c.HasBluetooth = true;

        RunTick(h.Vm, HeadsetOnBluetooth("COSMO", CosmoPid));
        RunTick(h.Vm, HeadsetOnBluetooth("STARLIGHT", StarlightPid));

        var rows = storage.GetDevices(User, UserId);
        Assert.Equal(2, rows.Count);
        var cosmo = Assert.Single(rows, r => r.Name == "COSMO");
        var starlight = Assert.Single(rows, r => r.Name == "STARLIGHT");
        Assert.Equal((HeadsetVendor, CosmoPid, ""), SettingsOf(cosmo));
        Assert.Equal((HeadsetVendor, StarlightPid, ""), SettingsOf(starlight));
        Assert.NotEqual(SettingsOf(cosmo), SettingsOf(starlight));
        // And the on-screen cards agree with their rows — a click after discovery opens each
        // headset's own bucket.
        Assert.NotEqual(
            SettingsOf(cards.Single(c => c.Name == "COSMO")),
            SettingsOf(cards.Single(c => c.Name == "STARLIGHT")));
    }
}
