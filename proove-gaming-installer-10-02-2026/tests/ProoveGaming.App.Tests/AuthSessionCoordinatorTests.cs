using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// Characterization tests for the sign-in / registration / sign-out / guest-expiry transitions
// moved out of MainWindow (docs/plans/refactor-auth-session-flow). Real SessionService,
// in-memory LocalStorageService, UserProfile and ApiHttpClient (stub transport); the auth API,
// the sync service and the three ViewModel seams are fakes that append to ONE call log, so the
// order the earlier reviews fixed is asserted directly.
public sealed class AuthSessionCoordinatorTests : IDisposable
{
    private const string Token = "session-token";
    private const int CustomerId = 42;

    private readonly List<string> _log = new();
    private readonly SessionService _session = new(a => a());
    private readonly LocalStorageService _store = new("Data Source=:memory:");
    private readonly UserProfile _profile = new();
    private readonly ApiHttpClient _apiHttp = new(new RejectingHandler(), "http://api.test.local/");
    private readonly FakeAuthApi _authApi;
    private readonly FakeSettingsSync _sync;
    private readonly AuthSessionCoordinator _coordinator;

    public AuthSessionCoordinatorTests()
    {
        _authApi = new FakeAuthApi(_log);
        _sync = new FakeSettingsSync(_log, _store, _profile);
        _coordinator = new AuthSessionCoordinator(
            _authApi,
            _apiHttp,
            _session,
            _store,
            _profile,
            _sync,
            new FakeOwnerScopedState(_log, _session),
            new FakeProfileSession(_log, _profile),
            new FakeDeviceDetailsExit(_log),
            NullLogger<AuthSessionCoordinator>.Instance);
    }

    public void Dispose()
    {
        _apiHttp.Dispose();
        _session.Dispose();
        _store.Dispose();
    }

    // ── Sign-in ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SignIn_WithRememberMe_EndsTheGuestSession_SavesTheSignIn_ReanchorsHome_ThenLoadsAndPulls()
    {
        _coordinator.StartGuestSession();
        SignInAs(CustomerId);
        await TripTheTokenRejectionLatchAsync();

        await _coordinator.CompleteSignInStateAsync(rememberMe: true);

        Assert.Equal(["owner.reload(sessionEnded=True)"], _log);
        await _store.DrainStorageQueueAsync();
        Assert.Equal(SessionState.None, _store.GetSessionState().State);
        Assert.Equal(Token, _store.GetSavedAuth()?.Token);
        Assert.True(await TokenRejectionLatchIsArmedAsync());

        await _coordinator.FinishSignInAsync();

        Assert.Equal(["owner.reload(sessionEnded=True)", "profile.load", $"sync.signedIn({CustomerId})"], _log);
        Assert.DoesNotContain(_log, e => e.StartsWith("sync.guestUpgraded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SignIn_WaitsForTheSessionRowDelete_BeforeReanchoringHome()
    {
        // The delete is awaited, not just queued: Home re-anchors only once the guest row is gone.
        _coordinator.StartGuestSession();
        SignInAs(CustomerId);
        using var gate = new ManualResetEventSlim(false);
        var blocker = _store.RunOnStorageAsync(() => gate.Wait());

        var signIn = _coordinator.CompleteSignInStateAsync(rememberMe: true);

        Assert.False(signIn.IsCompleted);
        Assert.Empty(_log);

        gate.Set();
        await blocker;
        await signIn;
        Assert.Equal(["owner.reload(sessionEnded=True)"], _log);
    }

    [Fact]
    public async Task SignIn_WithoutRememberMe_ClearsTheSavedSignIn_AndOtherwiseDoesTheSame()
    {
        _store.SaveAuth(7, "old@example.com", "old", "old-token", DateTime.UtcNow.AddDays(10));
        _coordinator.StartGuestSession();
        SignInAs(CustomerId);

        await _coordinator.CompleteSignInStateAsync(rememberMe: false);
        await _coordinator.FinishSignInAsync();

        Assert.Equal(["owner.reload(sessionEnded=True)", "profile.load", $"sync.signedIn({CustomerId})"], _log);
        await _store.DrainStorageQueueAsync();
        Assert.Null(_store.GetSavedAuth());
        Assert.Equal(SessionState.None, _store.GetSessionState().State);
    }

    // ── Registration ────────────────────────────────────────────────────────

    [Fact]
    public async Task Register_NicknameError_ReturnsItForTheField_AndNeverLogsIn()
    {
        var rejected = new ApiResponse<CustomerDto> { Success = false, ErrorCode = AuthErrorCodes.NicknameAlreadyExists };
        _authApi.RegisterResult = rejected;

        var result = await RegisterAsync();

        Assert.False(result.Succeeded);
        Assert.Same(rejected, result.NicknameError);
        Assert.Equal(["auth.register"], _log);
    }

    [Fact]
    public async Task Register_OtherError_ReturnsTheLocalizedToast_AndNeverLogsIn()
    {
        _authApi.RegisterResult = new ApiResponse<CustomerDto> { Success = false, ErrorCode = AuthErrorCodes.EmailAlreadyExists };

        var result = await RegisterAsync();

        Assert.False(result.Succeeded);
        Assert.Null(result.NicknameError);
        Assert.Equal(LocalizationManager.GetString("Auth_Error_EmailAlreadyExists"), result.ToastText);
        Assert.Equal(["auth.register"], _log);
    }

    [Fact]
    public async Task Register_FromAGuestSession_LogsIn_SavesTheSignIn_MigratesAndPushes_ThenEndsTheSession()
    {
        _coordinator.StartGuestSession();
        await TripTheTokenRejectionLatchAsync();

        var result = await RegisterAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(
            [
                "auth.register",
                "auth.login",
                // Snapshot taken inside the upgrade push: the token is set and the saved sign-in
                // already landed (FIFO: SaveAuth was queued before the awaited migration).
                $"sync.guestUpgraded({CustomerId}, token={Token}, saved=True)",
                "owner.reload(sessionEnded=True)",
            ],
            _log);
        Assert.True(await TokenRejectionLatchIsArmedAsync());
        await _store.DrainStorageQueueAsync();
        Assert.Equal(SessionState.None, _store.GetSessionState().State);
        Assert.Equal(Token, _store.GetSavedAuth()?.Token);

        await _coordinator.FinishRegistrationAsync();

        Assert.Equal("profile.load", _log[^1]);
        Assert.DoesNotContain(_log, e => e.StartsWith("sync.signedIn", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Register_WithoutAGuestSession_SkipsTheMigrationAndThePush()
    {
        var result = await RegisterAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(["auth.register", "auth.login", "owner.reload(sessionEnded=True)"], _log);
    }

    // ── Sign-out ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Logout_ServerCallThrows_LocalSignOutStillCompletes_InOrder()
    {
        SignInAs(CustomerId);
        _store.SaveAuth(CustomerId, "user@example.com", "user", Token, DateTime.UtcNow.AddDays(10));
        _authApi.LogoutThrows = true;

        await _coordinator.LogoutAsync();

        Assert.Equal(["auth.logout", "details.exit", "profile.clear", "owner.clear"], _log);
        await _store.DrainStorageQueueAsync();
        Assert.Null(_store.GetSavedAuth());
    }

    [Fact]
    public void IsCurrentToken_OnlyForTheSessionThatIsActiveNow()
    {
        SignInAs(CustomerId);

        Assert.True(_coordinator.IsCurrentToken(Token));
        Assert.False(_coordinator.IsCurrentToken("an-older-session"));
    }

    [Fact]
    public void SignOut_NoTokenAndAlreadyOnWelcome_DoesNothing()
    {
        var outcome = _coordinator.SignOut(notifySessionExpired: true, onWelcomeScreen: true);

        Assert.Equal(SignOutOutcome.Nothing, outcome);
        Assert.Empty(_log);
    }

    [Fact]
    public void SignOut_SessionExpired_ClearsInOrder_ThenAddsTheBellEntry()
    {
        SignInAs(CustomerId);

        var outcome = _coordinator.SignOut(notifySessionExpired: true, onWelcomeScreen: false);

        Assert.Equal(SignOutOutcome.SignedOut, outcome);
        Assert.Equal(
            [
                "details.exit",
                "profile.clear",
                "owner.clear",
                $"owner.notify({LocalizationManager.GetString("Session_ExpiredTitle")})",
            ],
            _log);
    }

    // ── Guest-session expiry ────────────────────────────────────────────────

    [Fact]
    public async Task GuestExpiry_WhileSignedIn_OnlyDisposesOfThePhantomSession()
    {
        _coordinator.StartGuestSession();
        SignInAs(CustomerId);

        var outcome = _coordinator.HandleGuestSessionExpired();

        Assert.Equal(GuestExpiryOutcome.StayWhereYouAre, outcome);
        Assert.Equal(Guid.Empty, _session.SessionId);
        Assert.Empty(_log);
        await _store.DrainStorageQueueAsync();
        Assert.Equal(SessionState.None, _store.GetSessionState().State);
    }

    [Fact]
    public async Task GuestExpiry_AsAGuest_WipesTheGuestData_EndsTheSession_AndClearsHome()
    {
        _coordinator.StartGuestSession();
        var guestId = _session.SessionId.ToString("D");
        await _store.RunOnStorageAsync(() => _store.AddDevice(LocalStorageService.OwnerKindGuest, guestId, new DeviceRecord { Name = "Guest mouse" }));

        var outcome = _coordinator.HandleGuestSessionExpired();

        Assert.Equal(GuestExpiryOutcome.ReturnToSignUp, outcome);
        Assert.Equal(Guid.Empty, _session.SessionId);
        Assert.Equal(["owner.clear"], _log);
        await _store.DrainStorageQueueAsync();
        Assert.Equal(SessionState.None, _store.GetSessionState().State);
        Assert.Empty(_store.GetDevices(LocalStorageService.OwnerKindGuest, guestId));
    }

    // ── DI ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Registration_ForwardsEachSeamToTheExistingViewModelSingleton()
    {
        // Identity only, so the ViewModels need no constructor graph.
        var home = (HomeViewModel)RuntimeHelpers.GetUninitializedObject(typeof(HomeViewModel));
        var profile = (ProfileViewModel)RuntimeHelpers.GetUninitializedObject(typeof(ProfileViewModel));
        var details = (DeviceDetailsViewModel)RuntimeHelpers.GetUninitializedObject(typeof(DeviceDetailsViewModel));
        var services = new ServiceCollection();
        services.AddSingleton(home);
        services.AddSingleton(profile);
        services.AddSingleton(details);

        App.AddAuthSessionServices(services);
        using var provider = services.BuildServiceProvider();

        Assert.Same(home, provider.GetRequiredService<IOwnerScopedState>());
        Assert.Same(profile, provider.GetRequiredService<IProfileSession>());
        Assert.Same(details, provider.GetRequiredService<IDeviceDetailsExit>());
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    // What WelcomeViewModel's sign-in leaves behind before MainWindow hands over.
    private void SignInAs(int customerId)
    {
        _profile.CustomerId = customerId;
        _profile.Email = "user@example.com";
        _profile.DisplayName = "user";
        _profile.SessionToken = Token;
        _profile.SessionTokenExpiresAt = DateTime.UtcNow.AddDays(30);
    }

    private Task<RegistrationResult> RegisterAsync() =>
        _coordinator.RegisterAsync("user@example.com", "Passw0rd", "user", agreeTerms: true, agreeNewsletter: false);

    private async Task TripTheTokenRejectionLatchAsync()
    {
        Assert.True(await TokenRejectionLatchIsArmedAsync());
        Assert.False(await TokenRejectionLatchIsArmedAsync());
    }

    // True when a rejected request raises TokenRejected, i.e. the latch is armed. Probing trips it.
    private async Task<bool> TokenRejectionLatchIsArmedAsync()
    {
        var fired = 0;
        Action<string> onRejected = _ => Interlocked.Increment(ref fired);
        _apiHttp.TokenRejected += onRejected;
        try
        {
            await _apiHttp.PostAsync<ApiResponse>("profile/get", body: null, "probe-token");
        }
        finally
        {
            _apiHttp.TokenRejected -= onRejected;
        }
        return fired == 1;
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(
                    """{"success":false,"error_code":"INVALID_OR_EXPIRED_TOKEN"}""", Encoding.UTF8, "application/json"),
            });
    }

    private sealed class FakeAuthApi(List<string> log) : IAuthApiService
    {
        public ApiResponse<CustomerDto> RegisterResult { get; set; } = new()
        {
            Success = true,
            Data = new CustomerDto { CustomerId = CustomerId, Email = "user@example.com", Nickname = "user" },
        };

        public bool LogoutThrows { get; set; }

        public Task<ApiResponse<CustomerDto>> RegisterAsync(string email, string password, string nickname, bool agreeTerms, bool agreeNewsletter)
        {
            log.Add("auth.register");
            return Task.FromResult(RegisterResult);
        }

        public Task<ApiResponse<CustomerDto>> LoginAsync(string email, string password)
        {
            log.Add("auth.login");
            return Task.FromResult(new ApiResponse<CustomerDto>
            {
                Success = true,
                Token = Token,
                TokenExpiresInDays = 30,
                Data = new CustomerDto { CustomerId = CustomerId, Email = email, Nickname = "user" },
            });
        }

        public Task<ApiResponse> LogoutAsync(string token)
        {
            log.Add("auth.logout");
            return LogoutThrows ? throw new HttpRequestException("offline") : Task.FromResult(new ApiResponse { Success = true });
        }

        public Task<ApiResponse> CheckEmailAsync(string email, string password) => throw new NotSupportedException();
        public Task<ApiResponse> CheckNicknameAsync(string nickname) => throw new NotSupportedException();
        public Task<ApiResponse> ForgottenPasswordAsync(string email) => throw new NotSupportedException();
        public Task<ApiResponse> ContactUsAsync(string email, string topic, string message, string? token, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ApiResponse<CustomerDto>> GetProfileAsync(string token) => throw new NotSupportedException();
        public Task<ApiResponse> UpdateProfileAsync(string token, string? email, string? nickname, string? firstName, string? lastName, string? telephone, string? country, string? dateBirth) => throw new NotSupportedException();
        public Task<AvatarUploadResponse> UploadAvatarAsync(string token, Stream fileStream, string fileName, string contentType) => throw new NotSupportedException();
        public Task<ApiResponse> CheckProfileNicknameAsync(string token, string nickname) => throw new NotSupportedException();
        public Task<ApiResponse> DeleteAccountAsync(string token, string password) => throw new NotSupportedException();
    }

    private sealed class FakeSettingsSync(List<string> log, LocalStorageService store, UserProfile profile) : ISettingsSyncService
    {
        public Task PullTask => Task.CompletedTask;

        // Never raised here — explicit no-op accessors instead of field-like events, which
        // would warn CS0067 (declared but never used).
        public event Action? PullCompletedWithChanges { add { } remove { } }
        public event Action? SyncCompleted { add { } remove { } }
        public event Action? NotificationsPulled { add { } remove { } }

        public async Task OnGuestUpgradedAsync(int customerId, CancellationToken ct = default)
        {
            // Read through the executor, so it cannot race a queued write on the connection.
            var saved = await store.RunOnStorageAsync(() => store.GetSavedAuth());
            log.Add($"sync.guestUpgraded({customerId}, token={profile.SessionToken}, saved={saved != null})");
        }

        public Task OnSignedInAsync(int customerId, CancellationToken ct = default)
        {
            log.Add($"sync.signedIn({customerId})");
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOwnerScopedState(List<string> log, SessionService session) : IOwnerScopedState
    {
        public void ReloadOwnerScopedState() => log.Add($"owner.reload(sessionEnded={session.SessionId == Guid.Empty})");
        public void ClearOwnerScopedState() => log.Add("owner.clear");
        public void AddNotification(string title, string subtitle) => log.Add($"owner.notify({title})");
    }

    // ClearLocalState drops the token, as ProfileViewModel's does.
    private sealed class FakeProfileSession(List<string> log, UserProfile profile) : IProfileSession
    {
        public Task LoadFromServerAsync()
        {
            log.Add("profile.load");
            return Task.CompletedTask;
        }

        public void ClearLocalState()
        {
            log.Add("profile.clear");
            profile.SessionToken = string.Empty;
        }
    }

    private sealed class FakeDeviceDetailsExit(List<string> log) : IDeviceDetailsExit
    {
        public void ClearDeviceForExit() => log.Add("details.exit");
    }
}
