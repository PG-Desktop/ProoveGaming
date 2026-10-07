using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProoveGaming.App.Models;

namespace ProoveGaming.App.Services;

public enum SignOutOutcome { Nothing, SignedOut }

public enum GuestExpiryOutcome { StayWhereYouAre, ReturnToSignUp }

// NicknameError: the response to show on the nickname field; ToastText: the localized toast for
// every other failure. The View picks one (MainWindow.OnNicknameChosen).
public sealed record RegistrationResult(bool Succeeded, ApiResponse? NicknameError, string ToastText)
{
    public static readonly RegistrationResult Success = new(true, null, string.Empty);
}

// The sign-in, registration, sign-out and guest-session state transitions, moved out of
// MainWindow's code-behind so their ordering is testable (docs/plans/refactor-auth-session-flow).
// MainWindow keeps navigation only: it calls one of these, then changes screen. Every method runs
// on the caller's (UI) context, exactly as the handlers did.
public sealed class AuthSessionCoordinator
{
    private readonly IAuthApiService _authApi;
    private readonly ApiHttpClient _apiHttp;
    private readonly SessionService _session;
    private readonly LocalStorageService _localStorage;
    private readonly UserProfile _userProfile;
    private readonly ISettingsSyncService _settingsSync;
    private readonly IOwnerScopedState _ownerScopedState;
    private readonly IProfileSession _profileSession;
    private readonly IDeviceDetailsExit _deviceDetailsExit;
    private readonly ILogger<AuthSessionCoordinator> _logger;

    public AuthSessionCoordinator(
        IAuthApiService authApi,
        ApiHttpClient apiHttp,
        SessionService session,
        LocalStorageService localStorage,
        UserProfile userProfile,
        ISettingsSyncService settingsSync,
        IOwnerScopedState ownerScopedState,
        IProfileSession profileSession,
        IDeviceDetailsExit deviceDetailsExit,
        ILogger<AuthSessionCoordinator> logger)
    {
        _authApi = authApi;
        _apiHttp = apiHttp;
        _session = session;
        _localStorage = localStorage;
        _userProfile = userProfile;
        _settingsSync = settingsSync;
        _ownerScopedState = ownerScopedState;
        _profileSession = profileSession;
        _deviceDetailsExit = deviceDetailsExit;
        _logger = logger;
    }

    // ApiHttpClient.TokenRejected, passed through: raised on a pool thread, subscribers marshal.
    public event Action<string>? TokenRejected
    {
        add => _apiHttp.TokenRejected += value;
        remove => _apiHttp.TokenRejected -= value;
    }

    // ISettingsSyncService.PullCompletedWithChanges, passed through: raised on a pool thread.
    public event Action? PullCompletedWithChanges
    {
        add => _settingsSync.PullCompletedWithChanges += value;
        remove => _settingsSync.PullCompletedWithChanges -= value;
    }

    public void ResumeRestoredSession()
    {
        // A restored session counts as a session start: re-arm the token-rejection latch
        // before the profile probe below can trip it.
        _apiHttp.ResetTokenRejectionLatch();
        _ = _profileSession.LoadFromServerAsync();
    }

    // The part of a successful sign-in that must land before Home is shown. FinishSignInAsync runs
    // after the navigation, so the profile request never delays the Home screen.
    public async Task CompleteSignInStateAsync(bool rememberMe)
    {
        // A fresh session token was just issued — re-arm the latch so a later rejection of
        // THIS token can raise TokenRejected again.
        _apiHttp.ResetTokenRejectionLatch();
        // Deliberately NO guest-data migration here: signing in reaches an account that
        // already owns its data, and pulling this machine's guest macros/settings into it
        // was reported as a bug. Guest rows follow the user only when a NEW account is
        // registered from the guest session — see RegisterAsync.
        _session.EndSession();
        // Drop the persisted guest session too — EndSession is memory-only, and a row left
        // behind would resurrect a phantom guest session (and its tray expiry) next launch.
        // Queued + awaited: the delete runs off the UI thread, FIFO-ordered after any
        // pending guest writes.
        await _localStorage.RunOnStorageAsync(_localStorage.ClearSession);
        if (rememberMe)
            PersistAuthIfTokenPresent();
        else
            EnqueueStorageWrite(_localStorage.ClearSavedAuth);
        // The owner just changed to the signed-in user: re-anchor the device cards and the bell
        // under that owner (and bring back the account's stored offline cards).
        _ownerScopedState.ReloadOwnerScopedState();
    }

    public async Task FinishSignInAsync()
    {
        await _profileSession.LoadFromServerAsync();
        // The account may hold data this machine has never seen — hydrate it in the background
        // (returns immediately; PullCompletedWithChanges refreshes Home when rows landed).
        // Deliberately NOT fired after guest→register (RegisterAsync): a fresh account has
        // nothing to pull, its upgrade push runs instead.
        await _settingsSync.OnSignedInAsync(_userProfile.CustomerId);
    }

    // The part of a registration that must land before Home is shown; FinishRegistrationAsync runs
    // after the navigation, for the same reason as sign-in.
    public async Task<RegistrationResult> RegisterAsync(string email, string password, string nickname, bool agreeTerms, bool agreeNewsletter)
    {
        var result = await _authApi.RegisterAsync(
            email,
            password,
            nickname,
            agreeTerms: agreeTerms,
            agreeNewsletter: agreeNewsletter);

        if (!result.Success)
        {
            var resolved = ApiErrorMessages.ResolveAll(result).ToList();

            // Other recognised codes (race-condition email/password/terms) belong on the previous
            // screen; the View shows this as a localized toast so the user knows what happened.
            var firstNonNickname = resolved.Where(r => r.Field != AuthField.Nickname)
                .Select(r => r.Message).FirstOrDefault();
            var toast = firstNonNickname ?? ApiErrorMessages.Describe(result, "Auth_Error_RegistrationFailed");

            // Nickname errors → highlight the field in place, no toast (the inline red box is enough).
            return new RegistrationResult(false, resolved.Any(r => r.Field == AuthField.Nickname) ? result : null, toast);
        }

        if (result.Data != null)
        {
            _userProfile.CustomerId = result.Data.CustomerId;
            _userProfile.Email = result.Data.Email;
            _userProfile.DisplayName = result.Data.Nickname;
        }

        var loginResult = await _authApi.LoginAsync(email, password);
        if (loginResult.Success)
        {
            // Registration just started a session — same latch re-arm as CompleteSignInStateAsync.
            _apiHttp.ResetTokenRejectionLatch();
            _userProfile.SessionToken = loginResult.Token ?? string.Empty;
            _userProfile.SessionTokenExpiresAt = DateTime.UtcNow.AddDays(loginResult.TokenExpiresInDays ?? 30);
            if (loginResult.Data != null)
            {
                _userProfile.CustomerId = loginResult.Data.CustomerId;
                _userProfile.Email = loginResult.Data.Email;
                _userProfile.DisplayName = loginResult.Data.Nickname;
            }

            // Stay signed-in after registration by default; a future Logout will clear this.
            PersistAuthIfTokenPresent();
        }

        var guestSessionId = _session.SessionId;
        var customerId = _userProfile.CustomerId;
        if (customerId > 0 && guestSessionId != Guid.Empty)
        {
            await _localStorage.MigrateGuestToUserAsync(guestSessionId, customerId);
            await _settingsSync.OnGuestUpgradedAsync(customerId);
        }
        _session.EndSession();
        // After the migration has consumed the guest session id, drop the persisted row
        // too — see CompleteSignInStateAsync. Queued + awaited like the migration above, so it
        // lands strictly after it.
        await _localStorage.RunOnStorageAsync(_localStorage.ClearSession);
        // Same re-anchor as CompleteSignInStateAsync — the migration above has already moved the
        // guest rows to the new account, so this adopts the on-screen cards and reloads the bell.
        _ownerScopedState.ReloadOwnerScopedState();
        return RegistrationResult.Success;
    }

    public Task FinishRegistrationAsync() => _profileSession.LoadFromServerAsync();

    public async Task LogoutAsync()
    {
        var token = _userProfile.SessionToken;
        if (!string.IsNullOrEmpty(token))
        {
            // Best-effort server-side invalidation; proceed with local cleanup regardless of network result.
            try { await _authApi.LogoutAsync(token); } catch { }
        }
        ClearSignedInState();
    }

    // A mismatch means the rejection is stale — a call that stayed in flight across sign-out
    // (token already cleared) or re-sign-in (a NEW session's token is current) — and must not sign
    // the user out of a session the server never rejected. Call it on the UI thread: the
    // SessionToken read is UI-thread-affine.
    public bool IsCurrentToken(string rejectedToken) => rejectedToken == _userProfile.SessionToken;

    // Shared sign-out choreography, idempotent: the profile path and the global path can both
    // observe the same dead token (plus stray in-flight calls after an account deletion), and
    // whichever lands second must find nothing to do — not wipe a Sign In form the user may
    // already be typing into.
    public SignOutOutcome SignOut(bool notifySessionExpired, bool onWelcomeScreen)
    {
        if (string.IsNullOrEmpty(_userProfile.SessionToken) && onWelcomeScreen)
            return SignOutOutcome.Nothing;

        ClearSignedInState();

        if (notifySessionExpired)
        {
            // After ClearSignedInState, so the owner-scoped bell wipe cannot swallow the entry.
            // With no owner left the entry stays in-memory only — right, it belongs to no account.
            _ownerScopedState.AddNotification(
                LocalizationManager.GetString("Session_ExpiredTitle"),
                LocalizationManager.GetString("Session_ExpiredToast"));
        }

        return SignOutOutcome.SignedOut;
    }

    public GuestExpiryOutcome HandleGuestSessionExpired()
    {
        // Guest-session expiry must never sign out an authenticated user. With a token
        // present the guest session is a phantom (pre-fix stale row or a future
        // regression) — dispose of it silently and stay where we are.
        if (!string.IsNullOrEmpty(_userProfile.SessionToken))
        {
            _session.EndSession();
            EnqueueStorageWrite(_localStorage.ClearSession);
            return GuestExpiryOutcome.StayWhereYouAre;
        }

        // Queued: FIFO orders the wipe after any guest writes still sitting in the executor
        // queue, so a pending flush cannot land after it and resurrect the wiped rows
        // (review round 1, S1). Writes for the NEXT session are enqueued later, so they
        // stay safely behind the wipe.
        EnqueueStorageWrite(_localStorage.ClearData);
        _session.EndSession();
        // Same sign-off rule as ClearSignedInState: the expired guest's offline cards must not
        // survive on screen — the next "Continue as guest" would re-register them into the fresh
        // session, undoing the ClearData wipe above (PR #112 m1).
        _ownerScopedState.ClearOwnerScopedState();
        return GuestExpiryOutcome.ReturnToSignUp;
    }

    public void StartGuestSession()
    {
        _session.StartSession();
        var sessionId = _session.SessionId;
        EnqueueStorageWrite(() => _localStorage.InitializeSession(sessionId));
    }

    private void ClearSignedInState()
    {
        // Signing out has to LEAVE the details page, not just navigate away from it. Both sign-out
        // paths go straight to Welcome without calling PerformExitDeviceDetails, so without this
        // Device kept pointing at the departing account's card: the next account's first open began
        // by flushing that card's still-displayed RGB, and (before the owner guard in
        // BuildRgbPersistWrite) wrote it into the arriving account's rows. It also stops the glow
        // timers and the audio/key-matrix polling for a page nobody is on any more.
        _deviceDetailsExit.ClearDeviceForExit();
        _profileSession.ClearLocalState();
        EnqueueStorageWrite(_localStorage.ClearSavedAuth);
        // After the profile is cleared (so Home sees no owner): drop the departing owner's
        // offline cards and bell. Left in place, the next sign-in's adopt pass would persist
        // this owner's devices into the next owner's account (PR #112 M1).
        _ownerScopedState.ClearOwnerScopedState();
    }

    private void PersistAuthIfTokenPresent()
    {
        if (string.IsNullOrEmpty(_userProfile.SessionToken) || _userProfile.SessionTokenExpiresAt == null)
            return;
        // Captured now: the profile can change (sign-out) before the queued write runs.
        var (customerId, email, nickname, token, expiresAt) = (
            _userProfile.CustomerId,
            _userProfile.Email,
            _userProfile.DisplayName,
            _userProfile.SessionToken,
            _userProfile.SessionTokenExpiresAt.Value);
        EnqueueStorageWrite(() => _localStorage.SaveAuth(customerId, email, nickname, token, expiresAt));
    }

    // Fire-and-forget enqueue for ordering-sensitive writes from sync handlers (same
    // contract as the HomeViewModel helper): FIFO on the storage executor preserves write
    // order without awaiting; a fault is logged, never thrown.
    private void EnqueueStorageWrite(Action write)
    {
        _ = _localStorage.RunOnStorageAsync(write).ContinueWith(
            t => _logger.LogError(t.Exception, "Queued storage write failed."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
