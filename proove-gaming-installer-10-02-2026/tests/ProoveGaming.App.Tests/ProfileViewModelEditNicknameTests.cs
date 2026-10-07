using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// v1.7 §10: api/profile/check_nickname answers 409 + up to 5 suggestions when a nickname is taken.
// The edit dialog is a window-wide Popup overlay and the notification toast lives in the window
// content underneath it, so a taken nickname routed through the toast is invisible — it has to
// land inline under the field or the user gets no feedback at all.
public class ProfileViewModelEditNicknameTests
{
    private sealed class RecordingAuthApi : IAuthApiService
    {
        public int CheckProfileNicknameCalls { get; private set; }
        public int UpdateProfileCalls { get; private set; }
        public string? LastCheckedNickname { get; private set; }
        public string? LastUpdatedNickname { get; private set; }
        public ApiResponse CheckProfileNicknameResult { get; set; } = new() { Success = true };
        public ApiResponse UpdateProfileResult { get; set; } = new() { Success = true };

        public Task<ApiResponse> CheckProfileNicknameAsync(string token, string nickname)
        {
            CheckProfileNicknameCalls++;
            LastCheckedNickname = nickname;
            return Task.FromResult(CheckProfileNicknameResult);
        }

        public Task<ApiResponse> UpdateProfileAsync(string token, string? email, string? nickname, string? firstName, string? lastName, string? telephone, string? country, string? dateBirth)
        {
            UpdateProfileCalls++;
            LastUpdatedNickname = nickname;
            return Task.FromResult(UpdateProfileResult);
        }

        // Opening the dialog bootstraps the suggestion pool off the public endpoint. Answer it
        // quietly so only what a test sets up on the 409 path reaches the suggestion chips.
        public Task<ApiResponse> CheckNicknameAsync(string nickname) =>
            Task.FromResult(new ApiResponse { Success = true });

        public Task<ApiResponse> CheckEmailAsync(string email, string password) => throw new NotSupportedException();
        public Task<ApiResponse<CustomerDto>> RegisterAsync(string email, string password, string nickname, bool agreeTerms, bool agreeNewsletter) => throw new NotSupportedException();
        public Task<ApiResponse<CustomerDto>> LoginAsync(string email, string password) => throw new NotSupportedException();
        public Task<ApiResponse> ForgottenPasswordAsync(string email) => throw new NotSupportedException();
        public Task<ApiResponse<CustomerDto>> GetProfileAsync(string token) => throw new NotSupportedException();
        public Task<AvatarUploadResponse> UploadAvatarAsync(string token, Stream fileStream, string fileName, string contentType) => throw new NotSupportedException();
        public Task<ApiResponse> DeleteAccountAsync(string token, string password) => throw new NotSupportedException();
        public Task<ApiResponse> LogoutAsync(string token) => throw new NotSupportedException();
        public Task<ApiResponse> ContactUsAsync(string email, string topic, string message, string? token, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static ApiResponse NicknameTaken(params string[] suggestions) => new()
    {
        Success = false,
        Errors = new List<ApiErrorEntry>
        {
            new() { ErrorCode = AuthErrorCodes.NicknameAlreadyExists, Field = "nickname" },
        },
        Suggestions = suggestions.Length == 0 ? null : suggestions.ToList(),
    };

    private static (ProfileViewModel vm, RecordingAuthApi api, LocalStorageService storage) CreateVm(
        string sessionToken = "token-123")
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        var api = new RecordingAuthApi();
        var user = new UserProfile { DisplayName = "PlayerOne", SessionToken = sessionToken };
        var vm = new ProfileViewModel(user, new NotificationService(), api, storage);
        return (vm, api, storage);
    }

    [Fact]
    public async Task ConfirmEditNickname_NicknameAlreadyTaken_KeepsDialogOpenWithFieldError()
    {
        var (vm, api, storage) = CreateVm();
        using var _s = storage;

        api.CheckProfileNicknameResult = NicknameTaken();

        vm.OpenEditNicknameCommand.Execute(null);
        vm.EditNicknameText = "TakenName";
        await vm.ConfirmEditNicknameCommand.ExecuteAsync(null);

        Assert.True(vm.IsEditNicknameOpen);
        Assert.True(vm.HasEditNicknameError);
        Assert.Equal("Auth_Error_NicknameAlreadyExists", vm.EditNicknameError);
        Assert.Equal(1, api.CheckProfileNicknameCalls);
        Assert.Equal(0, api.UpdateProfileCalls);
        Assert.Equal("PlayerOne", vm.User.DisplayName);
    }

    [Fact]
    public async Task ConfirmEditNickname_Conflict_RefreshesSuggestionChipsFromServer()
    {
        var (vm, api, storage) = CreateVm();
        using var _s = storage;

        api.CheckProfileNicknameResult = NicknameTaken("PlayerOne55", "PlayerPro", "PlayerGG");

        vm.OpenEditNicknameCommand.Execute(null);
        vm.EditNicknameText = "TakenName";
        await vm.ConfirmEditNicknameCommand.ExecuteAsync(null);

        Assert.True(vm.HasNicknameSuggestions);
        Assert.Equal(
            new[] { "PlayerGG", "PlayerOne55", "PlayerPro" },
            vm.NicknameSuggestions.OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ConfirmEditNickname_UpdateRejectsNickname_KeepsDialogOpenWithFieldError()
    {
        var (vm, api, storage) = CreateVm();
        using var _s = storage;

        // Availability check passes, then profile/update rejects the nickname anyway.
        api.UpdateProfileResult = NicknameTaken();

        vm.OpenEditNicknameCommand.Execute(null);
        vm.EditNicknameText = "RacedName";
        await vm.ConfirmEditNicknameCommand.ExecuteAsync(null);

        Assert.True(vm.IsEditNicknameOpen);
        Assert.Equal("Auth_Error_NicknameAlreadyExists", vm.EditNicknameError);
        Assert.Equal(1, api.UpdateProfileCalls);
        Assert.Equal("PlayerOne", vm.User.DisplayName);
    }

    [Fact]
    public async Task ConfirmEditNickname_NetworkFailure_ShowsTheFailureInlineInTheDialog()
    {
        var (vm, api, storage) = CreateVm();
        using var _s = storage;

        // What ApiHttpClient produces for a transport failure: a client error code and no text.
        // A stray English Message must still never reach the dialog
        // (docs/plans/fix-repository-audit-findings, S10/A8).
        api.CheckProfileNicknameResult = new ApiResponse
        {
            Success = false,
            IsNetworkError = true,
            ErrorCode = ClientErrorCodes.NetworkError,
            Message = "Network error. Please check your connection.",
        };

        vm.OpenEditNicknameCommand.Execute(null);
        vm.EditNicknameText = "OfflineName";
        await vm.ConfirmEditNicknameCommand.ExecuteAsync(null);

        // The toast renders behind this dialog's overlay and nothing closes the dialog, so a
        // connectivity failure routed there would be completely invisible. (No resource
        // dictionary is loaded in tests, so GetString returns the key itself.)
        Assert.True(vm.IsEditNicknameOpen);
        Assert.Equal("Api_Error_Network", vm.EditNicknameError);
        Assert.Equal(0, api.UpdateProfileCalls);
    }

    [Fact]
    public async Task ConfirmEditNickname_UpdateRejectsAnotherProfileField_ShowsThatErrorInline()
    {
        var (vm, api, storage) = CreateVm();
        using var _s = storage;

        // profile/update carries every field, so it can reject on something the user never
        // touched in this dialog — here a stored phone number the server considers invalid.
        api.UpdateProfileResult = new ApiResponse
        {
            Success = false,
            ErrorCode = "PHONE_INVALID",
            Message = "Telephone is invalid",
        };

        vm.OpenEditNicknameCommand.Execute(null);
        vm.EditNicknameText = "FreshName";
        await vm.ConfirmEditNicknameCommand.ExecuteAsync(null);

        // Still shown inline in the open dialog (review M2 of fix-profile-and-auth-validation: it
        // used to vanish into a toast behind the overlay), but as localized text: the server's own
        // English message is no longer shown, since it stays English in the Ukrainian UI
        // (docs/plans/fix-repository-audit-findings, S10/A8). GetString returns the key in tests.
        Assert.True(vm.IsEditNicknameOpen);
        Assert.Equal("ProfileView_EditNickname_Error_Generic", vm.EditNicknameError);
        Assert.DoesNotContain("Telephone", vm.EditNicknameError);
        Assert.Equal("PlayerOne", vm.User.DisplayName);
    }

    [Fact]
    public async Task ConfirmEditNickname_UnrecognizedCodeWithNoMessage_FallsBackToLocalizedGeneric()
    {
        var (vm, api, storage) = CreateVm();
        using var _s = storage;

        api.CheckProfileNicknameResult = new ApiResponse { Success = false, ErrorCode = "SOMETHING_NEW" };

        vm.OpenEditNicknameCommand.Execute(null);
        vm.EditNicknameText = "FreshName";
        await vm.ConfirmEditNicknameCommand.ExecuteAsync(null);

        Assert.True(vm.IsEditNicknameOpen);
        Assert.Equal("ProfileView_EditNickname_Error_Generic", vm.EditNicknameError);
    }

    [Fact]
    public async Task ConfirmEditNickname_ExpiredToken_BouncesTheSessionInsteadOfShowingAFieldError()
    {
        var (vm, api, storage) = CreateVm();
        using var _s = storage;

        var sessionInvalidated = false;
        vm.SessionTokenInvalid += () => sessionInvalidated = true;

        api.CheckProfileNicknameResult = new ApiResponse
        {
            Success = false,
            ErrorCode = AuthErrorCodes.InvalidOrExpiredToken,
        };

        vm.OpenEditNicknameCommand.Execute(null);
        vm.EditNicknameText = "FreshName";
        await vm.ConfirmEditNicknameCommand.ExecuteAsync(null);

        // The one failure that legitimately leaves the dialog — the session teardown closes it.
        Assert.True(sessionInvalidated);
        Assert.False(vm.HasEditNicknameError);
    }

    [Fact]
    public async Task ConfirmEditNickname_Available_UpdatesProfileAndClosesDialog()
    {
        var (vm, api, storage) = CreateVm();
        using var _s = storage;

        vm.OpenEditNicknameCommand.Execute(null);
        vm.EditNicknameText = "  FreshName  ";
        await vm.ConfirmEditNicknameCommand.ExecuteAsync(null);

        Assert.Equal("FreshName", api.LastCheckedNickname);
        Assert.Equal("FreshName", api.LastUpdatedNickname);
        Assert.Equal("FreshName", vm.User.DisplayName);
        Assert.False(vm.IsEditNicknameOpen);
        Assert.False(vm.IsNicknameFieldActive);
        Assert.False(vm.HasEditNicknameError);
    }

    [Theory]
    [InlineData("ab")]                       // shorter than 3
    [InlineData("a b")]                      // contains a space
    [InlineData("abcdefghijklmnopqrstu")]    // 21 chars, longer than 20
    [InlineData("наз")]                      // non-Latin characters
    public async Task ConfirmEditNickname_LocalRuleViolation_ShowsFieldErrorAndSkipsBothRequests(string typed)
    {
        var (vm, api, storage) = CreateVm();
        using var _s = storage;

        vm.OpenEditNicknameCommand.Execute(null);
        vm.EditNicknameText = typed;
        await vm.ConfirmEditNicknameCommand.ExecuteAsync(null);

        Assert.True(vm.HasEditNicknameError);
        Assert.Equal(0, api.CheckProfileNicknameCalls);
        Assert.Equal(0, api.UpdateProfileCalls);
        Assert.True(vm.IsEditNicknameOpen);
    }

    [Fact]
    public async Task ConfirmEditNickname_UnchangedNickname_ClosesWithoutAnyRequest()
    {
        var (vm, api, storage) = CreateVm();
        using var _s = storage;

        vm.OpenEditNicknameCommand.Execute(null);
        await vm.ConfirmEditNicknameCommand.ExecuteAsync(null);

        Assert.False(vm.IsEditNicknameOpen);
        Assert.Equal(0, api.CheckProfileNicknameCalls);
        Assert.Equal(0, api.UpdateProfileCalls);
    }

    [Fact]
    public void EditingNicknameText_ClearsPreviousError()
    {
        var (vm, _, storage) = CreateVm();
        using var _s = storage;

        vm.EditNicknameError = "boom";
        vm.EditNicknameText = "typing";

        Assert.False(vm.HasEditNicknameError);
    }
}
