using System;
using System.Reflection;
using System.Threading;
using ProoveGaming.App;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

public class SessionServiceTests : IDisposable
{
    // Runs the dispatch action synchronously so tests don't need Avalonia's UI thread.
    private static SessionService Create() => new(a => a());

    // xUnit builds a fresh instance per test, so this is per-test teardown. AppSettings is a
    // process-wide static that tests here mutate (SessionTimeHours = 0 to force expiry, short
    // lead times to force the warning); restoring the shipped defaults keeps a failed test from
    // leaking a misconfiguration into whatever class runs next.
    public void Dispose()
    {
        AppSettings.SessionTimeHours = 48;
        AppSettings.SessionExpiryWarningMinutes = 180;
    }

    // ── Basic state ───────────────────────────────────────────────────────────

    [Fact]
    public void IsGuest_IsFalseByDefault()
    {
        var service = Create();
        Assert.False(service.IsGuest);
    }

    [Fact]
    public void StartSession_SetsIsGuestTrue()
    {
        var service = Create();
        service.StartSession();
        Assert.True(service.IsGuest);
    }

    [Fact]
    public void StartSession_GeneratesNonEmptyUuid()
    {
        var service = Create();
        service.StartSession();
        Assert.NotEqual(Guid.Empty, service.SessionId);
    }

    [Fact]
    public void StartSession_GeneratesUniqueUuidEachCall()
    {
        var service = Create();
        service.StartSession();
        var first = service.SessionId;
        service.EndSession();
        service.StartSession();
        Assert.NotEqual(first, service.SessionId);
    }

    [Fact]
    public void EndSession_SetsIsGuestFalse()
    {
        var service = Create();
        service.StartSession();
        service.EndSession();
        Assert.False(service.IsGuest);
    }

    [Fact]
    public void EndSession_ClearsSessionId()
    {
        var service = Create();
        service.StartSession();
        service.EndSession();
        Assert.Equal(Guid.Empty, service.SessionId);
    }

    [Fact]
    public void RestoreSession_SetsIsGuestTrue()
    {
        var service = Create();
        service.RestoreSession(Guid.NewGuid());
        Assert.True(service.IsGuest);
    }

    [Fact]
    public void RestoreSession_PreservesProvidedUuid()
    {
        var service = Create();
        var uuid = Guid.NewGuid();
        service.RestoreSession(uuid);
        Assert.Equal(uuid, service.SessionId);
    }

    // ── Requirement 1: closed-application timer ───────────────────────────────
    // When the app was closed and the stored startedAt is already past the
    // timeout, restoring the session must fire SessionExpired as soon as the
    // app goes to tray (or immediately if it already is).

    [Fact]
    public void RestoreSession_WithExpiredStartedAt_AndGoToTray_FiresSessionExpired()
    {
        AppSettings.SessionTimeHours = 48;
        var service = Create();
        var expiredStart = DateTime.UtcNow.AddHours(-(AppSettings.SessionTimeHours + 1));

        var fired = false;
        service.SessionExpired += () => fired = true;

        service.RestoreSession(Guid.NewGuid(), startedAt: expiredStart);
        service.GoToTray();

        Assert.True(fired);
    }

    [Fact]
    public void RestoreSession_WithExpiredStartedAt_NotInTray_DoesNotFireSessionExpired()
    {
        AppSettings.SessionTimeHours = 48;
        var service = Create();
        var expiredStart = DateTime.UtcNow.AddHours(-(AppSettings.SessionTimeHours + 1));

        var fired = false;
        service.SessionExpired += () => fired = true;

        service.RestoreSession(Guid.NewGuid(), startedAt: expiredStart);
        // GoToTray never called

        Assert.False(fired);
    }

    [Fact]
    public void RestoreSession_WithFreshStartedAt_AndGoToTray_DoesNotFireSessionExpired()
    {
        AppSettings.SessionTimeHours = 48;
        var service = Create();

        var fired = false;
        service.SessionExpired += () => fired = true;

        service.RestoreSession(Guid.NewGuid(), startedAt: DateTime.UtcNow);
        service.GoToTray();

        Assert.False(fired);
    }

    // ── Requirement 2: active-application timer ───────────────────────────────
    // The timer starts on StartSession. When it elapses while the app is in
    // the tray, SessionExpired fires. We simulate an already-elapsed timer by
    // setting SessionTimeHours to 0.

    [Fact]
    public void StartSession_WhenTimerElapsesAndGoToTray_FiresSessionExpired()
    {
        AppSettings.SessionTimeHours = 0;
        var service = Create();

        var fired = false;
        service.SessionExpired += () => fired = true;

        service.StartSession();
        service.GoToTray();

        Assert.True(fired);
        AppSettings.SessionTimeHours = 48;
    }

    [Fact]
    public void StartSession_WhenTimerElapsesButNotInTray_DoesNotFireSessionExpired()
    {
        AppSettings.SessionTimeHours = 0;
        var service = Create();

        var fired = false;
        service.SessionExpired += () => fired = true;

        service.StartSession();
        // GoToTray never called

        Assert.False(fired);
        AppSettings.SessionTimeHours = 48;
    }

    [Fact]
    public void GoToTray_WithoutTimerExpired_DoesNotFireSessionExpired()
    {
        AppSettings.SessionTimeHours = 48;
        var service = Create();

        var fired = false;
        service.SessionExpired += () => fired = true;

        service.StartSession();
        service.GoToTray();

        Assert.False(fired);
    }

    // ── Requirement 3: all devices disconnected while going to tray ───────────
    // App.axaml.cs calls CheckIfAllDevicesAreDisconnected() before GoToTray and
    // passes the result so both expiry conditions are evaluated in a single call.

    [Fact]
    public void GoToTray_WithAllDevicesDisconnected_FiresSessionExpired()
    {
        AppSettings.SessionTimeHours = 48;
        var service = Create();

        var fired = false;
        service.SessionExpired += () => fired = true;

        service.StartSession();
        service.GoToTray(allDevicesDisconnected: true);

        Assert.True(fired);
    }

    [Fact]
    public void GoToTray_WithDevicesConnected_DoesNotFireSessionExpired()
    {
        AppSettings.SessionTimeHours = 48;
        var service = Create();

        var fired = false;
        service.SessionExpired += () => fired = true;

        service.StartSession();
        service.GoToTray(allDevicesDisconnected: false);

        Assert.False(fired);
    }

    [Fact]
    public void GoToTray_WithTimerExpiredAndAllDevicesDisconnected_FiresSessionExpiredOnce()
    {
        AppSettings.SessionTimeHours = 0;
        var service = Create();

        var count = 0;
        service.SessionExpired += () => count++;

        service.StartSession();
        service.GoToTray(allDevicesDisconnected: true);

        Assert.Equal(1, count);
        AppSettings.SessionTimeHours = 48;
    }

    // ── EndSession resets state ───────────────────────────────────────────────

    [Fact]
    public void EndSession_PreventsPendingTimerExpiry()
    {
        AppSettings.SessionTimeHours = 0;
        var service = Create();

        var fired = false;
        service.SessionExpired += () => fired = true;

        service.StartSession();
        service.EndSession();
        service.GoToTray();

        Assert.False(fired);
        AppSettings.SessionTimeHours = 48;
    }

    // ── Guest session-expiry warning ──────────────────────────────────────────
    // docs/plans/add-guest-session-expiry-warning. A second one-shot timer fires a lead time
    // (SessionExpiryWarningMinutes) before the expiry above would be due. The expiry tests above
    // are the guard that this addition is invisible to them: several set SessionTimeHours = 0,
    // which is a lead time at/beyond the whole session and must therefore warn about nothing.

    // Scheduling the real timer is the only way to reach OnWarningTimerElapsed: the due instant
    // is (session - lead) from startedAt, so an already-elapsed lead is answered synchronously
    // instead. One configured hour minus a 59-minute lead leaves a one-minute window, and
    // backdating startedAt to just inside it puts the callback `dueInMs` away.
    private static DateTime StartedAtSoDueIn(int dueInMs)
    {
        AppSettings.SessionTimeHours = 1;
        AppSettings.SessionExpiryWarningMinutes = 59;
        return DateTime.UtcNow.AddMinutes(-1).AddMilliseconds(dueInMs);
    }

    // "No timer is left running" is one of the plan's acceptance criteria and has no behavioural
    // surface at all: a leaked warning timer for the default configuration fires 45 hours later,
    // long after any test could observe it. The private field is read directly rather than
    // leaving the criterion uncovered.
    private static bool WarningTimerIsRunning(SessionService service)
    {
        var field = typeof(SessionService)
                .GetField("_warningTimer", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new Xunit.Sdk.XunitException(
                "SessionService._warningTimer was renamed — update this helper.");
        return field.GetValue(service) is System.Timers.Timer { Enabled: true };
    }

    // A lead time at or beyond the whole session is a misconfiguration; the feature is disabled
    // rather than firing at t = 0. The (0, 180) row is the configuration every expiry test above
    // runs under — it must stay silent, or those tests would start seeing warnings.
    [Theory]
    [InlineData(0, 180)]
    [InlineData(1, 60)]
    [InlineData(1, 180)]
    public void StartSession_WithALeadTimeCoveringTheWholeSession_RaisesNothing_AndLeavesNoTimer(
        int sessionHours, int warningMinutes)
    {
        AppSettings.SessionTimeHours = sessionHours;
        AppSettings.SessionExpiryWarningMinutes = warningMinutes;
        var service = Create();

        var warned = false;
        service.SessionExpiringSoon += () => warned = true;

        service.StartSession();

        Assert.False(warned);
        Assert.False(service.ExpiryWarningDue);
        Assert.False(WarningTimerIsRunning(service));
    }

    // The restart-inside-the-window case, and the reason ExpiryWarningDue exists: the due instant
    // passed while the app was closed, so the warning must be raised now rather than never.
    [Fact]
    public void RestoreSession_WithStartedAtInsideTheWarningWindow_WarnsImmediately()
    {
        AppSettings.SessionTimeHours = 48;
        AppSettings.SessionExpiryWarningMinutes = 180;
        var service = Create();

        var warned = false;
        service.SessionExpiringSoon += () => warned = true;

        // 46 h in: past the 45 h due instant, still two hours short of expiry.
        service.RestoreSession(Guid.NewGuid(), startedAt: DateTime.UtcNow.AddHours(-46));

        Assert.True(warned);
        Assert.True(service.ExpiryWarningDue);
    }

    // The latch has to be true for a consumer that did not exist when the event was raised —
    // App.OnFrameworkInitializationCompleted restores the session before it resolves MainWindow,
    // so HomeViewModel's constructor asks instead of only listening.
    [Fact]
    public void RestoreSession_InsideTheWarningWindow_SetsTheLatchForALaterConsumer()
    {
        AppSettings.SessionTimeHours = 48;
        AppSettings.SessionExpiryWarningMinutes = 180;
        var service = Create();

        // Nobody is subscribed yet — exactly the ordering trap.
        service.RestoreSession(Guid.NewGuid(), startedAt: DateTime.UtcNow.AddHours(-46));

        Assert.True(service.ExpiryWarningDue);
    }

    [Fact]
    public void RestoreSession_WithAFreshStartedAt_RaisesNoWarning()
    {
        AppSettings.SessionTimeHours = 48;
        AppSettings.SessionExpiryWarningMinutes = 180;
        var service = Create();

        var warned = false;
        service.SessionExpiringSoon += () => warned = true;

        service.RestoreSession(Guid.NewGuid(), startedAt: DateTime.UtcNow);

        Assert.False(warned);
        Assert.False(service.ExpiryWarningDue);
    }

    [Fact]
    public void StartSession_DoesNotWarnAtTheStartOfTheSession()
    {
        AppSettings.SessionTimeHours = 48;
        AppSettings.SessionExpiryWarningMinutes = 180;
        var service = Create();

        var warned = false;
        service.SessionExpiringSoon += () => warned = true;

        service.StartSession();

        Assert.False(warned);
        Assert.False(service.ExpiryWarningDue);
        // Scheduled rather than skipped: this configuration does warn, 45 h from now.
        Assert.True(WarningTimerIsRunning(service));
    }

    // The explicit contrast with the expiry tests above: expiry is tray-gated (CheckAndExpire
    // returns unless _isInTray), the warning is not. It must reach the user with the window open,
    // which is the only state they can act on it in.
    [Fact]
    public void WarningTimer_WhenItElapses_FiresWithoutGoToTray()
    {
        var startedAt = StartedAtSoDueIn(150);
        var service = Create();

        using var warned = new ManualResetEventSlim(false);
        var expired = false;
        service.SessionExpiringSoon += () => warned.Set();
        service.SessionExpired += () => expired = true;

        service.RestoreSession(Guid.NewGuid(), startedAt: startedAt);
        // GoToTray never called.

        Assert.True(warned.Wait(TimeSpan.FromSeconds(10)), "The warning timer never fired.");
        Assert.True(service.ExpiryWarningDue);
        Assert.False(expired);
    }

    // Mirrors EndSession_PreventsPendingTimerExpiry for the second timer: a guest who registers
    // before the due instant must not be warned about a session that no longer exists. Also
    // covers the pool-thread callback racing the UI thread — an Elapsed already queued when
    // EndSession runs loses the race instead of announcing.
    [Fact]
    public void EndSession_BeforeTheWarningIsDue_PreventsIt()
    {
        var startedAt = StartedAtSoDueIn(800);
        var service = Create();

        var warned = false;
        service.SessionExpiringSoon += () => warned = true;

        service.RestoreSession(Guid.NewGuid(), startedAt: startedAt);
        Assert.True(
            WarningTimerIsRunning(service),
            "the warning became due before EndSession ran — widen the window in StartedAtSoDueIn.");
        service.EndSession();

        Thread.Sleep(1500);

        Assert.False(warned);
        Assert.False(service.ExpiryWarningDue);
        Assert.False(WarningTimerIsRunning(service));
    }

    [Fact]
    public void AcknowledgeExpiryWarning_ClearsTheLatch_AndALaterSubscriberIsNotReFired()
    {
        AppSettings.SessionTimeHours = 48;
        AppSettings.SessionExpiryWarningMinutes = 180;
        var service = Create();

        service.RestoreSession(Guid.NewGuid(), startedAt: DateTime.UtcNow.AddHours(-46));
        Assert.True(service.ExpiryWarningDue);

        service.AcknowledgeExpiryWarning();

        Assert.False(service.ExpiryWarningDue);

        // A consumer that arrives after the acknowledgement (a second HomeViewModel, or one
        // rebuilt after a navigation) must not re-announce the same warning.
        var warnedAgain = false;
        service.SessionExpiringSoon += () => warnedAgain = true;

        Assert.False(warnedAgain);
        Assert.False(service.ExpiryWarningDue);
    }
}
