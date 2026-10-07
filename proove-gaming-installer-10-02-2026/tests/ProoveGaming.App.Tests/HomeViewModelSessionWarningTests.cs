using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProoveGaming.App;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// The consumer half of docs/plans/add-guest-session-expiry-warning: SessionService decides WHEN
// the warning is due (SessionServiceTests), HomeViewModel decides what the user actually sees.
// Three rules are pinned here, and each of them is a decision someone could plausibly undo:
//
//   1. ONE bell entry per session, even though the handler legitimately runs twice for a single
//      warning — once from the constructor's ExpiryWarningDue check, once from the event
//      SessionService dispatched before the constructor subscribed. The check-and-claim is a
//      single storage-executor item for exactly this reason.
//   2. The bell and NOTHING else: no in-app banner, no Windows toast. AnnounceUpdateAvailable
//      sits ten lines above it in the same file and uses all three surfaces, so the omission is
//      one copy-paste away from being reinstated.
//   3. The flag is written BEFORE the entry — the plan chooses a missed warning over a
//      duplicated one if the app dies between the two.
//
// Harness mirrors HomeViewModelNotificationThreadingTests, with a real temp-file store (the
// once-per-session flag lives in app_session, which a session restore reads back off disk).
public class HomeViewModelSessionWarningTests : IDisposable
{
    private const int CustomerId = 42;

    // 48 h session, 3 h lead: the shipped defaults. A session started 46 h ago is inside the
    // warning window and two hours short of expiry.
    private static readonly TimeSpan InsideTheWarningWindow = TimeSpan.FromHours(46);

    private readonly List<string> _dbPaths = [];
    private readonly List<ManualResetEventSlim> _gates = [];

    public void Dispose()
    {
        AppSettings.SessionTimeHours = 48;
        AppSettings.SessionExpiryWarningMinutes = 180;
        // Safe to dispose: every gate is Set before its NewVm returns, and each test drains the
        // storage queue, so no executor item can still be waiting on one here.
        foreach (var gate in _gates) gate.Dispose();
        foreach (var path in _dbPaths)
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

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
        UserProfile User,
        NotificationService Banner,
        Guid SessionId);

    private LocalStorageService NewStorage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"pg-sessionwarning-{Guid.NewGuid():N}.db");
        _dbPaths.Add(path);
        return new LocalStorageService($"Data Source={path}");
    }

    /// <summary>
    /// Builds the view model the way App.axaml.cs does: the session is restored (and its
    /// app_session row written, as MainWindow does) BEFORE the view model exists, so a session
    /// already inside the warning window has raised its event into the void by the time the
    /// constructor subscribes.
    /// </summary>
    private Harness NewVm(
        LocalStorageService? storage = null,
        TimeSpan? sessionAge = null,
        bool signedIn = false,
        Action<Action>? sessionDispatch = null,
        Action<Action>? postToUi = null)
    {
        AppSettings.SessionTimeHours = 48;
        AppSettings.SessionExpiryWarningMinutes = 180;

        storage ??= NewStorage();
        var session = new SessionService(sessionDispatch ?? (a => a()));
        var user = new UserProfile();
        if (signedIn)
        {
            user.CustomerId = CustomerId;
            user.SessionToken = "token";
        }

        var sessionId = Guid.NewGuid();
        storage.InitializeSession(sessionId);
        session.RestoreSession(sessionId, startedAt: DateTime.UtcNow - (sessionAge ?? TimeSpan.Zero));

        var banner = new NotificationService();

        var services = new ServiceCollection();
        services.AddTransient<AddDeviceCardViewModel>();
        services.AddTransient<SingleCardViewModel>();
        services.AddTransient<DoubleCardViewModel>();
        services.AddSingleton(banner);

        // The constructor itself can start the announcement (its ExpiryWarningDue check), and the
        // PostToUi seam can only be replaced on a constructed instance — so park the storage
        // executor across construction, and the claim's continuation cannot reach the real
        // dispatcher before the seam has been swapped for the test's.
        var constructionGate = new ManualResetEventSlim(false);
        _gates.Add(constructionGate);
        _ = storage.RunOnStorageAsync(() => constructionGate.Wait());

        var vm = new HomeViewModel(
            new FakeHid(),
            session,
            storage,
            user,
            services.BuildServiceProvider(),
            new SystemNotificationService(NullLogger<SystemNotificationService>.Instance),
            new CurrentOwnerProvider(session, user),
            NullLogger<HomeViewModel>.Instance);

        // Inline by default, mirroring DeviceDetailsPresetApplyTests: the process-wide
        // dispatcher's first-touch thread affinity makes pumping it racy in this assembly
        // (AssemblyInfo.cs), and the seam exists precisely so no pumping is needed.
        vm.PostToUi = postToUi ?? (a => a());
        constructionGate.Set();

        return new Harness(vm, storage, session, user, banner, sessionId);
    }

    // ── The golden path ───────────────────────────────────────────────

    // A guest session restored inside the warning window announces exactly once, and does it
    // without touching either of the two surfaces the plan drops. The bell entry asserted here is
    // the REAL one: AnnounceSessionExpiringAsync now marshals through the PostToUi seam, which
    // the harness runs inline, so there is no unpumped dispatcher hop for the entry to park on
    // and "announced" is what the user would actually see rather than a pending task.
    [Fact]
    public async Task Warning_ClaimsTheSessionsOneWarning_AndShowsNoBanner()
    {
        var h = NewVm(sessionAge: InsideTheWarningWindow);
        using (h.Storage)
        {
            await h.Vm.NotificationsLoaded;
            await h.Storage.DrainStorageQueueAsync();
            await h.Vm.LastSessionExpiryAnnouncement;
            await h.Vm.LastNotificationPersist;

            var entry = Assert.Single(h.Vm.Notifications);
            Assert.Equal(LocalizationManager.GetString("Notification_GuestSessionExpiringTitle"), entry.Title);
            // 3 h lead at the shipped 180-minute default.
            Assert.Equal(
                string.Format(LocalizationManager.GetString("Notification_GuestSessionExpiringBody_Format"), 3),
                entry.Subtitle);
            Assert.Equal("avares://ProoveGaming.Common/Assets/Icons/Info.svg", entry.IconUri);
            Assert.False(entry.IsRead);
            Assert.Equal(1, h.Vm.UnreadNotificationsCount);
            Assert.True(h.Vm.HasUnreadNotifications);
            Assert.True(h.Storage.GetSessionExpiryWarningSent());

            // The in-app banner, asserted against the real service: Show would have made it
            // visible and copied the body into it. (The Windows toast has no observable state at
            // all — see TheWarningPath_AddsABellEntryAndNothingElse.)
            Assert.False(h.Banner.IsVisible);
            Assert.Equal(string.Empty, h.Banner.Text);

            // The latch was acknowledged, so no later consumer replays it.
            Assert.False(h.Session.ExpiryWarningDue);
        }
    }

    // The other half of "one bell entry, persisted under the guest owner": an entry raised while
    // the owner is a guest lands in the GUEST bucket, keyed by the session uuid, so it is still
    // there after the restart the warning is about. Driven through the REAL warning path — the
    // inline PostToUi seam lets its AddNotification actually run, where this test previously had
    // to call AddNotification directly because the entry parked on the dispatcher hop.
    [Fact]
    public async Task ABellEntryRaisedDuringAGuestSession_PersistsUnderTheGuestOwner()
    {
        var h = NewVm(sessionAge: InsideTheWarningWindow);
        using (h.Storage)
        {
            await h.Vm.NotificationsLoaded;
            await h.Vm.LastSessionExpiryAnnouncement;
            await h.Vm.LastNotificationPersist;

            var entry = Assert.Single(h.Vm.Notifications);
            Assert.Equal(LocalizationManager.GetString("Notification_GuestSessionExpiringTitle"), entry.Title);
            Assert.Equal(1, h.Vm.UnreadNotificationsCount);

            var stored = Assert.Single(h.Storage.GetNotifications(
                LocalStorageService.OwnerKindGuest, h.SessionId.ToString("D")));
            Assert.Equal(entry.Title, stored.Title);
            Assert.False(stored.IsRead);
        }
    }

    // The bell-only decision, pinned against the ONE way it will realistically be undone: someone
    // copies the three-surface AnnounceUpdateAvailable, which sits ten lines above the warning in
    // the same file. Asserted structurally because neither surface can be intercepted at runtime —
    // SystemNotificationService.Show is not virtual, and on Windows it neither throws nor logs, so
    // a spy on its logger would pass whether or not it was called. The update path is the positive
    // control in the same test: if the token search could not find these calls at all, it fails
    // there instead of quietly passing below.
    [Fact]
    public void TheWarningPath_AddsABellEntryAndNothingElse()
    {
        var banner = typeof(NotificationService).GetMethod(nameof(NotificationService.Show))!;
        var toast = typeof(SystemNotificationService).GetMethod(nameof(SystemNotificationService.Show))!;

        var updatePath = typeof(HomeViewModel).GetMethod(
            "OnUpdateAvailable", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True(Calls(updatePath, banner), "OnUpdateAvailable should still show the in-app banner.");
        Assert.True(Calls(updatePath, toast), "OnUpdateAvailable should still show the Windows toast.");

        var warningPath = AsyncBodyOf(typeof(HomeViewModel).GetMethod(
            "AnnounceSessionExpiringAsync", BindingFlags.Instance | BindingFlags.NonPublic)!);

        // ONE scope for all three assertions: the warning path's own state machine plus exactly the
        // closures it hands out (`PostToUi(() => AddNotification(…))`, the storage claim). Scoping matters in both directions. Widen it to every nested type of
        // HomeViewModel and the positive goes vacuous — four other closures call the same 3-arg
        // AddNotification (HomeViewModel.cs:1177, :1315, :1665, :1670) and would satisfy it after the
        // warning's own call was deleted — while the negatives would false-positive on the unrelated
        // Contact Us flow (HomeViewModel.cs:368 calls NotificationService.Show from an async method
        // whose MoveNext is also a nested type). Narrow it to MoveNext alone and the negatives stop
        // seeing the lambda, which is the natural place for a pasted Show call to land.
        var warningScope = WarningPathScope(warningPath);

        // The one surface it does use — the bell.
        var bell = typeof(HomeViewModel).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(m => m.Name == "AddNotification" && m.GetParameters().Length == 3);
        Assert.True(
            CallsAnyOf(warningScope, bell),
            "The guest expiry warning must still add its bell entry.");

        Assert.False(CallsAnyOf(warningScope, banner),
            "The guest expiry warning is a bell entry only — no in-app banner (plan → Scope → Out).");
        Assert.False(CallsAnyOf(warningScope, toast),
            "The guest expiry warning is a bell entry only — no Windows toast (plan → Scope → Out).");
    }

    // An async method's body is the compiler-generated MoveNext, reached through the attribute
    // rather than by guessing the state machine's mangled name.
    private static MethodBase AsyncBodyOf(MethodInfo method) =>
        method.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType
            .GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.NonPublic)!;

    // Both callees live in the same assembly as HomeViewModel, so a call site carries their
    // MethodDef token verbatim: finding call/callvirt followed by that token is finding the call.
    private static bool Calls(MethodBase caller, MethodInfo callee)
    {
        var il = caller.GetMethodBody()!.GetILAsByteArray()!;
        var token = BitConverter.GetBytes(callee.MetadataToken);
        for (var i = 0; i + 4 < il.Length; i++)
        {
            if (il[i] is not (0x28 or 0x6F)) continue; // call / callvirt
            if (il[i + 1] == token[0] && il[i + 2] == token[1] &&
                il[i + 3] == token[2] && il[i + 4] == token[3])
                return true;
        }

        return false;
    }

    private static bool CallsAnyOf(IReadOnlyCollection<MethodBase> scope, MethodInfo callee) =>
        scope.Any(m => Calls(m, callee));

    // The code the warning path actually runs, as method bodies: its state machine, plus the
    // closures it hands to other people to run (`PostToUi(() => AddNotification(…))` compiles to
    // a `ldftn` on a display-class method, which is a call site the warning owns even though it is
    // not in MoveNext). Followed transitively, but only into compiler-generated types — a lambda
    // that hands out another lambda is still the warning path; a named method it calls is not, and
    // following those would drag in the whole view model.
    private static IReadOnlyCollection<MethodBase> WarningPathScope(MethodBase stateMachine)
    {
        var scope = new List<MethodBase>();
        var seen = new HashSet<MethodBase>();
        var pending = new Queue<MethodBase>();
        pending.Enqueue(stateMachine);

        while (pending.Count > 0)
        {
            var method = pending.Dequeue();
            if (!seen.Add(method) || method.GetMethodBody()?.GetILAsByteArray() is not { } il) continue;
            scope.Add(method);

            foreach (var target in FunctionPointerTargets(method, il))
            {
                // Only the compiler's own closure/state-machine types, never a real method.
                if (target.DeclaringType?.IsDefined(typeof(CompilerGeneratedAttribute), false) == true)
                    pending.Enqueue(target);
            }
        }

        return scope;
    }

    // Every method whose address the body takes: `ldftn` (0xFE 0x06) / `ldvirtftn` (0xFE 0x07)
    // followed by a method token. Scanning for the two-byte prefix rather than decoding the whole
    // instruction stream can in principle match a byte pair inside an operand; a token that does not
    // resolve to a method is simply skipped, and one that does is a real callee of this body.
    private static IEnumerable<MethodBase> FunctionPointerTargets(MethodBase method, byte[] il)
    {
        for (var i = 0; i + 5 < il.Length; i++)
        {
            if (il[i] != 0xFE || il[i + 1] is not (0x06 or 0x07)) continue;

            var token = BitConverter.ToInt32(il, i + 2);
            MethodBase? target = null;
            try
            {
                target = method.Module.ResolveMethod(
                    token,
                    method.DeclaringType?.GetGenericArguments(),
                    method.GetGenericArguments());
            }
            catch (ArgumentException) { }   // not a method token — a coincidental byte pair
            catch (BadImageFormatException) { }

            if (target is not null) yield return target;
        }
    }

    // ── Once per session ──────────────────────────────────────────────────────

    [Fact]
    public async Task WarningAlreadySentForThisSession_RaisesNothing()
    {
        var storage = NewStorage();
        using (storage)
        {
            // The app_session row this reads is created by NewVm's InitializeSession; the flag is
            // what survives the restart, so set it the way the previous launch would have.
            storage.InitializeSession(Guid.NewGuid());
            storage.SetSessionExpiryWarningSent();

            var h = NewVm(storage, sessionAge: InsideTheWarningWindow);
            await h.Vm.NotificationsLoaded;
            await storage.DrainStorageQueueAsync();
            await h.Vm.LastSessionExpiryAnnouncement;

            // Nothing was claimed, so nothing reached the seam — with PostToUi inline, a claimed
            // warning would be sitting in the bell here.
            Assert.Empty(h.Vm.Notifications);
        }
    }

    // The plan's stated failure direction: the flag is written first, so a crash between the two
    // costs a missed warning rather than a duplicated one. A capturing seam makes the gap directly
    // observable: the announcement hands the entry to PostToUi and completes, the flag is already
    // on disk, and the bell stays empty until the test itself runs the held hop.
    [Fact]
    public async Task TheFlagIsClaimedBeforeTheEntryIsAnnounced()
    {
        var held = new List<Action>();
        var h = NewVm(sessionAge: InsideTheWarningWindow, postToUi: held.Add);
        using (h.Storage)
        {
            await h.Vm.NotificationsLoaded;
            await h.Storage.DrainStorageQueueAsync();
            await h.Vm.LastSessionExpiryAnnouncement;

            // The flag is already on disk while the entry is still held at the seam: that IS the
            // ordering the plan asks for, and the gap between the two is the window a crash would
            // land in — costing a missed warning, never a duplicated one.
            Assert.True(h.Storage.GetSessionExpiryWarningSent());
            Assert.Empty(h.Vm.Notifications);

            // Releasing the held hop lands the entry — it was queued behind the flag, not lost.
            var pending = Assert.Single(held);
            pending();
            Assert.Single(h.Vm.Notifications);
            await h.Vm.LastNotificationPersist;
        }
    }

    // THE double-notify race the single-item claim exists for. The handler runs twice for one
    // warning — the constructor's latch check, then the event SessionService dispatched before
    // the constructor subscribed — and both calls are held at the storage executor here, so the
    // second one's read would overtake the first one's write if the check and the write were two
    // separate hops. One entry, one row.
    [Fact]
    public async Task HandlerRunningTwiceForOneWarning_LetsOnlyOneCallAnnounce()
    {
        var storage = NewStorage();
        using (storage)
        {
            // Parked BEFORE the view model exists, so neither call's claim can complete before
            // the other has started — the interleaving that two awaited hops would lose.
            using var gate = new ManualResetEventSlim(false);
            _ = storage.RunOnStorageAsync(() => gate.Wait());

            // Call 1 is the constructor's latch check; call 2 is the event arriving afterwards.
            var h = NewVm(storage, sessionAge: InsideTheWarningWindow);
            var first = h.Vm.LastSessionExpiryAnnouncement;
            h.Session.RestoreSession(h.SessionId, startedAt: DateTime.UtcNow - InsideTheWarningWindow);
            var second = h.Vm.LastSessionExpiryAnnouncement;
            Assert.NotSame(first, second);

            gate.Set();
            await h.Vm.NotificationsLoaded;
            await storage.DrainStorageQueueAsync();
            await first;
            await second;
            await h.Vm.LastNotificationPersist;

            // Exactly one of the two calls claimed the warning and went on to announce; the other
            // read the flag the first had already written and returned. With two separate storage
            // hops instead of one, both reads would have run before either write, and TWO entries
            // would be in the bell here — the inline seam turns "one pending announcement" into
            // the entry count the user would actually see.
            Assert.Single(h.Vm.Notifications);
            Assert.True(storage.GetSessionExpiryWarningSent());
        }
    }

    // ── The ordering trap ─────────────────────────────────────────────────────

    // App.axaml.cs restores the session before it resolves MainWindow, so for a session restored
    // inside the window the event is raised with nobody listening and the warning exists only as
    // the latch. Asserted with a dispatch that never runs the event at all: if the constructor
    // stopped asking ExpiryWarningDue, the most common real case (a 48 h session outlives the app
    // process) would silently stop warning.
    [Fact]
    public async Task WarningDueBeforeTheViewModelExists_IsRaisedWithoutTheEventEverArriving()
    {
        var dropped = 0;
        var h = NewVm(
            sessionAge: InsideTheWarningWindow,
            sessionDispatch: _ => dropped++);

        using (h.Storage)
        {
            await h.Vm.NotificationsLoaded;
            await h.Storage.DrainStorageQueueAsync();

            await h.Vm.LastSessionExpiryAnnouncement;
            await h.Vm.LastNotificationPersist;

            Assert.Equal(1, dropped); // the event SessionService raised into the void
            Assert.Single(h.Vm.Notifications); // announced from the constructor's latch check alone
            Assert.True(h.Storage.GetSessionExpiryWarningSent());
        }
    }

    // ── Guests only ───────────────────────────────────────────────────────────

    // Signed-in accounts have their own 30-day token expiry. The reachable shape of this is a
    // warning dispatched a moment before the guest session ends (registering from inside it):
    // the queued event lands after EndSession, and must find nothing to announce.
    [Fact]
    public async Task SignedInOwner_RaisesNothing()
    {
        var queued = new List<Action>();
        var h = NewVm(signedIn: true, sessionDispatch: queued.Add);

        using (h.Storage)
        {
            await h.Vm.NotificationsLoaded;

            // The warning becomes due while the user is registering: SessionService dispatches it,
            // the sign-in ends the guest session, and only then does the queued event reach the
            // view model — which must find nothing to announce.
            h.Session.RestoreSession(h.SessionId, startedAt: DateTime.UtcNow - InsideTheWarningWindow);
            h.Session.EndSession();
            Assert.Single(queued);
            queued[0]();

            await h.Storage.DrainStorageQueueAsync();
            await h.Vm.LastSessionExpiryAnnouncement;

            // Nothing reached the seam: with PostToUi inline, an announcement would be visible.
            Assert.Empty(h.Vm.Notifications);
            Assert.False(h.Storage.GetSessionExpiryWarningSent());
            Assert.False(h.Banner.IsVisible);
        }
    }
}
