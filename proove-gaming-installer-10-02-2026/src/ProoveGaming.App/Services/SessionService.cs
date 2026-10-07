using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Timers;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ProoveGaming.App.Models;

namespace ProoveGaming.App.Services;

public sealed partial class SessionService : ObservableObject, IDisposable
{
    [ObservableProperty] private bool _isGuest;

    public Guid SessionId { get; private set; }
    public UserSession? CurrentSession { get; private set; }

    public event Action? SessionExpired;

    // Raised a configurable lead time BEFORE SessionExpired would be due
    // (docs/plans/add-guest-session-expiry-warning). Unlike expiry this is not tray-gated: the
    // warning exists so the guest can act on it, which they can only do with the window open.
    public event Action? SessionExpiringSoon;

    private readonly Action<Action> _dispatch;
    // Fully qualified: an unqualified `using System.Threading` would make `Timer` ambiguous here.
    private readonly System.Threading.Lock _warningGate = new();
    private Timer? _activeTimer;
    private Timer? _warningTimer;
    private bool _isInTray;
    private bool _timerExpired;
    private bool _expiryWarningDue;

    // Latch beside the event, because the warning can become due before any consumer exists:
    // App.OnFrameworkInitializationCompleted restores the session well before it resolves
    // MainWindow (and therefore HomeViewModel), so a session restored already inside the warning
    // window would raise SessionExpiringSoon into the void. The consumer must be able to ask, not
    // only listen. Cleared by AcknowledgeExpiryWarning once it has been handled.
    // Every read and write of _expiryWarningDue goes through _warningGate — the field is shared
    // between the pool thread that runs Elapsed and the UI thread that ends the session, and a
    // write outside the gate lets a losing callback re-arm the latch after StopWarningTimer.
    public bool ExpiryWarningDue
    {
        get
        {
            lock (_warningGate)
            {
                return _expiryWarningDue;
            }
        }
    }

    public void AcknowledgeExpiryWarning()
    {
        lock (_warningGate)
        {
            _expiryWarningDue = false;
        }
    }

    public SessionService(Action<Action>? dispatch = null)
    {
        _dispatch = dispatch ?? (a => Dispatcher.UIThread.Post(a));
    }

    public bool IsInTray
    {
        get => _isInTray;
        set => _isInTray = value;
    }

    public void GoToTray(bool allDevicesDisconnected = false)
    {
        _isInTray = true;
        if (_timerExpired || allDevicesDisconnected)
            _dispatch(() => SessionExpired?.Invoke());
    }

    public void StartSession()
    {
        SessionId = Guid.NewGuid();
        CurrentSession = new UserSession(SessionId);
        IsGuest = true;
        var startedAt = DateTime.UtcNow;
        StartActiveTimer(startedAt);
        // After StartActiveTimer: it opens with StopActiveTimer, which tears the warning down too.
        StartWarningTimer(startedAt);
    }

    public void RestoreSession(Guid sessionId, IEnumerable<DeviceRecord>? devices = null, DateTime? startedAt = null)
    {
        SessionId = sessionId;
        CurrentSession = devices != null
            ? new UserSession(sessionId, devices)
            : new UserSession(sessionId);
        IsGuest = true;
        if (startedAt.HasValue)
        {
            StartActiveTimer(startedAt.Value);
            StartWarningTimer(startedAt.Value);
        }
    }

    public void EndSession()
    {
        StopActiveTimer();
        CurrentSession = null;
        SessionId = Guid.Empty;
        IsGuest = false;
    }

    private void StartActiveTimer(DateTime startedAt)
    {
        StopActiveTimer();
        var remaining = TimeSpan.FromHours(AppSettings.SessionTimeHours) - (DateTime.UtcNow - startedAt);
        if (remaining <= TimeSpan.Zero)
        {
            _timerExpired = true;
            CheckAndExpire();
            return;
        }

        _activeTimer = new Timer(remaining.TotalMilliseconds) { AutoReset = false };
        _activeTimer.Elapsed += OnActiveTimerElapsed;
        _activeTimer.Start();
    }

    private void StopActiveTimer()
    {
        if (_activeTimer != null)
        {
            _activeTimer.Stop();
            _activeTimer.Elapsed -= OnActiveTimerElapsed;
            _activeTimer.Dispose();
            _activeTimer = null;
        }
        _timerExpired = false;
        StopWarningTimer();
    }

    // Scheduled at (SessionTimeHours - SessionExpiryWarningMinutes) from the same persisted
    // startedAt the expiry timer uses, so the lead time survives an app restart.
    private void StartWarningTimer(DateTime startedAt)
    {
        StopWarningTimer();

        var sessionLength = TimeSpan.FromHours(AppSettings.SessionTimeHours);
        var leadTime = TimeSpan.FromMinutes(AppSettings.SessionExpiryWarningMinutes);
        if (leadTime >= sessionLength)
        {
            // A lead time at or beyond the whole session is a misconfiguration, and firing at
            // t=0 would be pure noise — so the warning is disabled rather than degraded. Logged
            // because the feature then vanishes with no other symptom.
            // Trace, not Debug: Debug.WriteLine is [Conditional("DEBUG")] and so does not exist
            // in the Release build that ships, which is precisely the build a support case comes
            // from. TRACE is defined in Release too, and DefaultTraceListener routes this to
            // OutputDebugString, where DebugView picks it up on a user's machine. An ILogger
            // would not help: the App registers only AddDebug(), which emits nothing unless a
            // debugger is attached — and taking one would give SessionService the first
            // dependency the plan deliberately keeps it free of.
            Trace.WriteLine(
                $"Session expiry warning disabled: SessionExpiryWarningMinutes ({AppSettings.SessionExpiryWarningMinutes}) " +
                $"is not shorter than SessionTimeHours ({AppSettings.SessionTimeHours}).");
            return;
        }

        var remaining = sessionLength - leadTime - (DateTime.UtcNow - startedAt);
        if (remaining <= TimeSpan.Zero)
        {
            // The due instant passed while the app was closed — warn now rather than never.
            RaiseExpiryWarning();
            return;
        }

        lock (_warningGate)
        {
            _warningTimer = new Timer(remaining.TotalMilliseconds) { AutoReset = false };
            _warningTimer.Elapsed += OnWarningTimerElapsed;
            _warningTimer.Start();
        }
    }

    private void StopWarningTimer()
    {
        lock (_warningGate)
        {
            if (_warningTimer != null)
            {
                _warningTimer.Stop();
                _warningTimer.Elapsed -= OnWarningTimerElapsed;
                _warningTimer.Dispose();
                _warningTimer = null;
            }
            _expiryWarningDue = false;
        }
    }

    private void OnWarningTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        // Elapsed runs on a pool thread and can already be queued when EndSession clears the
        // timer on the UI thread. Comparing the sender against the live timer under the same
        // gate makes that callback lose the race instead of announcing into an ended session.
        lock (_warningGate)
        {
            if (!ReferenceEquals(sender, _warningTimer)) return;
            // Latched under the same gate as the identity check: otherwise EndSession could run
            // between the two and StopWarningTimer's clear would be undone by this callback.
            _expiryWarningDue = true;
        }
        // Dispatch outside the gate — the tests inject a synchronous dispatch, which would
        // otherwise run the whole consumer (including AcknowledgeExpiryWarning) under the lock.
        _dispatch(() => SessionExpiringSoon?.Invoke());
    }

    // The due instant had already passed when the timer was armed, so there is no callback to
    // race: latch and announce straight away.
    private void RaiseExpiryWarning()
    {
        lock (_warningGate)
        {
            _expiryWarningDue = true;
        }
        _dispatch(() => SessionExpiringSoon?.Invoke());
    }

    private void OnActiveTimerElapsed(object? sender, ElapsedEventArgs e)
    {
        _timerExpired = true;
        CheckAndExpire();
    }

    private void CheckAndExpire()
    {
        if (!_isInTray) return;
        if (!_timerExpired) return;
        _dispatch(() => SessionExpired?.Invoke());
    }

    public void Dispose()
    {
        _activeTimer?.Stop();
        _activeTimer?.Dispose();
        _activeTimer = null;
        StopWarningTimer();
    }
}
