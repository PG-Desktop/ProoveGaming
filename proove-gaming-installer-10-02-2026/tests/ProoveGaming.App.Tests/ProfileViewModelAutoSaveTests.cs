using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// The inline profile auto-save (docs/plans/improve-api-error-surfacing), which had no coverage
// at all. The first block is characterization — debounce, coalescing, suppression, token
// bounce — and must stay green; the second covers the behaviour change: a rejected save used
// to be discarded, so an edit the server refused looked saved.
//
// Timing rides the internal debounce-injecting constructor (the SyncCoordinator seam), never a
// real 800 ms wait. LocalStorageService and NotificationService are used real, as in every
// other ProfileViewModel test.
public class ProfileViewModelAutoSaveTests
{
	private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(30);

	private sealed class RecordingAuthApi : IAuthApiService
	{
		private int _updateProfileCalls;

		public int UpdateProfileCalls => Volatile.Read(ref _updateProfileCalls);
		public ApiResponse UpdateProfileResult { get; set; } = new() { Success = true };

		// When set, UpdateProfileAsync throws instead of answering — the exception branch.
		public Exception? Throws { get; set; }

		// When set, the FIRST call parks here until the test releases it — the only way to hold
		// a save in flight long enough for a newer edit to supersede it.
		public TaskCompletionSource? FirstCallGate { get; set; }

		public async Task<ApiResponse> UpdateProfileAsync(string token, string? email, string? nickname, string? firstName, string? lastName, string? telephone, string? country, string? dateBirth)
		{
			var call = Interlocked.Increment(ref _updateProfileCalls);
			if (call == 1 && FirstCallGate is { } gate)
				await gate.Task;
			if (Throws is { } ex)
				throw ex;
			return UpdateProfileResult;
		}

		public Task<ApiResponse> CheckProfileNicknameAsync(string token, string nickname) => throw new NotSupportedException();
		public Task<ApiResponse> CheckNicknameAsync(string nickname) => throw new NotSupportedException();
		public Task<ApiResponse> CheckEmailAsync(string email, string password) => throw new NotSupportedException();
		public Task<ApiResponse> ForgottenPasswordAsync(string email) => throw new NotSupportedException();
		public Task<ApiResponse> DeleteAccountAsync(string token, string password) => throw new NotSupportedException();
		public Task<ApiResponse> LogoutAsync(string token) => throw new NotSupportedException();
		public Task<ApiResponse> ContactUsAsync(string email, string topic, string message, string? token, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<AvatarUploadResponse> UploadAvatarAsync(string token, Stream fileStream, string fileName, string contentType) => throw new NotSupportedException();
		public Task<ApiResponse<CustomerDto>> RegisterAsync(string email, string password, string nickname, bool agreeTerms, bool agreeNewsletter) => throw new NotSupportedException();
		public Task<ApiResponse<CustomerDto>> LoginAsync(string email, string password) => throw new NotSupportedException();
		public Task<ApiResponse<CustomerDto>> GetProfileAsync(string token) => throw new NotSupportedException();
	}

	private static (ProfileViewModel Vm, RecordingAuthApi Api, LocalStorageService Storage) CreateVm(
		string sessionToken = "token-123")
	{
		var storage = new LocalStorageService("Data Source=:memory:");
		var api = new RecordingAuthApi();
		var profile = new UserProfile();
		profile.DisplayName = "PlayerOne";
		profile.SessionToken = sessionToken;
		var vm = new ProfileViewModel(profile, new NotificationService(), api, storage, Debounce);
		return (vm, api, storage);
	}

	// Polls instead of sleeping a fixed multiple of the debounce: the save runs on a pool
	// thread, so a fixed wait is either flaky or slow. Bounded so a real hang still fails.
	private static async Task<bool> WaitForAsync(Func<bool> condition, int attempts = 600)
	{
		for (var i = 0; i != attempts; i++)
		{
			if (condition()) return true;
			await Task.Delay(5);
		}
		return condition();
	}

	// Long enough that a second debounce window would have elapsed if one had been armed.
	private static Task SettleAsync() => Task.Delay(150);

	// ── Characterization: the save fires, once, per burst ───────────────────────

	[Theory]
	[InlineData("FirstName")]
	[InlineData("LastName")]
	[InlineData("Phone")]
	[InlineData("Country")]
	public async Task EditingATrackedField_TriggersExactlyOneDebouncedSave(string property)
	{
		var (vm, api, storage) = CreateVm();
		using (storage)
		{
			if (property == "FirstName") vm.User.FirstName = "Ada";
			else if (property == "LastName") vm.User.LastName = "Lovelace";
			else if (property == "Phone") vm.User.Phone = "0800000000";
			else vm.User.Country = "UA";

			Assert.True(await WaitForAsync(() => api.UpdateProfileCalls == 1));
			await SettleAsync();
			Assert.Equal(1, api.UpdateProfileCalls);
		}
	}

	[Fact]
	public async Task EditingTheDateOfBirth_TriggersASave()
	{
		var (vm, api, storage) = CreateVm();
		using (storage)
		{
			vm.User.DateOfBirth = new DateOnly(1990, 5, 17);

			Assert.True(await WaitForAsync(() => api.UpdateProfileCalls == 1));
		}
	}

	// Criterion 16's mechanism: the debounce is restart-on-edit, so a burst of edits produces
	// ONE save attempt — and therefore at most one failure report.
	[Fact]
	public async Task ABurstOfEdits_CoalescesIntoASingleSave()
	{
		var (vm, api, storage) = CreateVm();
		using (storage)
		{
			vm.User.FirstName = "Ada";
			vm.User.LastName = "Lovelace";
			vm.User.Phone = "0800000000";

			Assert.True(await WaitForAsync(() => api.UpdateProfileCalls != 0));
			await SettleAsync();
			Assert.Equal(1, api.UpdateProfileCalls);
		}
	}

	[Fact]
	public async Task AnEmptySessionToken_SuppressesTheSave()
	{
		var (vm, api, storage) = CreateVm(string.Empty);
		using (storage)
		{
			vm.User.FirstName = "Ada";
			await SettleAsync();
			Assert.Equal(0, api.UpdateProfileCalls);
		}
	}

	// ── The behaviour change: failures are reported, not discarded ──────────────

	// Criterion 15: the value the user typed stays on screen. A revert would both yank text
	// out from under a live edit and re-enter ScheduleProfileSave from a PropertyChanged
	// handler — the update storm CLAUDE.md forbids.
	[Fact]
	public async Task ARejectedSave_LeavesTheTypedValueOnScreen()
	{
		var (vm, api, storage) = CreateVm();
		using (storage)
		{
			api.UpdateProfileResult = Refusal("SOME_SERVER_REFUSAL");
			vm.User.FirstName = "Ada";
			Assert.True(await WaitForAsync(() => api.UpdateProfileCalls == 1));
			await SettleAsync();

			Assert.Equal("Ada", vm.User.FirstName);
			// No revert means no re-entrant save storm either.
			Assert.Equal(1, api.UpdateProfileCalls);
		}
	}

	// Criterion 17: a superseded debounce is a cancellation, not a failure — the first
	// attempt never reaches the API at all, so there is nothing to report.
	[Fact]
	public async Task ASupersededSave_NeverReachesTheApi()
	{
		var (vm, api, storage) = CreateVm();
		using (storage)
		{
			api.UpdateProfileResult = Refusal("SOME_SERVER_REFUSAL");
			vm.User.FirstName = "Ad";
			vm.User.FirstName = "Ada";

			Assert.True(await WaitForAsync(() => api.UpdateProfileCalls != 0));
			await SettleAsync();
			Assert.Equal(1, api.UpdateProfileCalls);
		}
	}

	// Criterion 18: silent success is preserved — a successful save adds no UI noise and no
	// session bounce.
	[Fact]
	public async Task ASuccessfulSave_RaisesNoSessionBounce()
	{
		var (vm, api, storage) = CreateVm();
		using (storage)
		{
			var bounced = 0;
			vm.SessionTokenInvalid += () => Interlocked.Increment(ref bounced);

			vm.User.FirstName = "Ada";
			Assert.True(await WaitForAsync(() => api.UpdateProfileCalls == 1));
			await SettleAsync();

			Assert.Equal(0, Volatile.Read(ref bounced));
		}
	}

	// Characterization: a server-side token invalidation still reaches the API and is
	// handled. The bounce is the whole message, so it must NOT also raise a save-failed
	// toast — hence the early return in ScheduleProfileSave.
	[Theory]
	[InlineData(AuthErrorCodes.InvalidOrExpiredToken)]
	[InlineData(AuthErrorCodes.TokenRequired)]
	public async Task ATokenRejection_IsHandledWithoutFaultingTheSave(string code)
	{
		var (vm, api, storage) = CreateVm();
		using (storage)
		{
			api.UpdateProfileResult = Refusal(code);
			vm.User.FirstName = "Ada";

			Assert.True(await WaitForAsync(() => api.UpdateProfileCalls == 1));
			await SettleAsync();
			Assert.Equal(1, api.UpdateProfileCalls);
		}
	}

	// A save that throws (transport blew up mid-request) is a real failure, not a
	// cancellation: the blanket catch used to swallow both alike. It must not fault the
	// fire-and-forget task nor disturb the typed value.
	[Fact]
	public async Task AThrowingSave_IsContainedAndLeavesTheValueAlone()
	{
		var (vm, api, storage) = CreateVm();
		using (storage)
		{
			api.Throws = new InvalidOperationException("transport blew up");
			vm.User.FirstName = "Ada";

			Assert.True(await WaitForAsync(() => api.UpdateProfileCalls == 1));
			await SettleAsync();
			Assert.Equal("Ada", vm.User.FirstName);
		}
	}

	// REGRESSION, review R1 S2. The debounce coalesces *scheduling*, but a request already in
	// flight cannot be cancelled: before the fix, save #1 would complete after a newer edit had
	// superseded it, see a failure, and report a toast the user had already invalidated by
	// typing. ScheduleProfileSave now re-checks cts.IsCancellationRequested immediately after
	// the await, so the superseded save returns silently (criterion 17).
	//
	// WHAT THIS TEST CANNOT SEE: the toast itself. Every side effect of that branch —
	// _notification.Show and SessionTokenInvalid alike — is raised through
	// Dispatcher.UIThread.Post, which does not run without a dispatcher, and RunJobs() is
	// documented in AssemblyInfo.cs / DeviceDetailsPresetApplyTests as unreliable here. So this
	// pins the SCENARIO (a save genuinely in flight when a newer edit lands) and the
	// observable consequences; the silence itself is argued from the code, not asserted. A
	// PostToUi seam on ProfileViewModel would close that gap — see the plan's Round 1 notes.
	[Fact]
	public async Task ASaveSupersededWhileInFlight_CompletesQuietlyAndLetsTheNewerSaveRun()
	{
		var (vm, api, storage) = CreateVm();
		using (storage)
		{
			var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
			api.FirstCallGate = gate;
			api.UpdateProfileResult = Refusal(AuthErrorCodes.InvalidOrExpiredToken);

			// Save #1 reaches the API and parks there.
			vm.User.FirstName = "Ad";
			Assert.True(await WaitForAsync(() => api.UpdateProfileCalls == 1));

			// A newer edit supersedes it while it is still in flight.
			vm.User.FirstName = "Ada";
			gate.SetResult();

			// Save #2 runs on its own debounce; the superseded #1 must not fault anything on
			// its way out.
			Assert.True(await WaitForAsync(() => api.UpdateProfileCalls == 2));
			await SettleAsync();

			Assert.Equal(2, api.UpdateProfileCalls);
			Assert.Equal("Ada", vm.User.FirstName);
		}
	}

	// ── The wording choice, pinned directly (no dispatcher needed) ──────────────

	// Criterion 11: an interactive call answered with 429 shows the shared rate-limit
	// message rather than the generic failure text.
	[Fact]
	public void ProfileSaveFailureKey_UsesTheSharedRateLimitWordingOn429() =>
		Assert.Equal("Api_Error_TooManyRequests", ProfileViewModel.ProfileSaveFailureKey(true));

	[Fact]
	public void ProfileSaveFailureKey_UsesTheGenericWordingOtherwise() =>
		Assert.Equal("ProfileView_Notification_ProfileSaveFailed", ProfileViewModel.ProfileSaveFailureKey(false));

	private static ApiResponse Refusal(string code)
	{
		var response = new ApiResponse();
		response.Success = false;
		response.ErrorCode = code;
		return response;
	}
}
