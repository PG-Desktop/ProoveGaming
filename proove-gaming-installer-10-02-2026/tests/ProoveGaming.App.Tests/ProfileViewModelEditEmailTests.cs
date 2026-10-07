using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// v1.7 has no email-verification endpoint (§6 api/profile/update takes the address directly),
// so the change-email dialog is a single step: validate locally, then PATCH the profile.
public class ProfileViewModelEditEmailTests
{
    private sealed class RecordingAuthApi : IAuthApiService
    {
        public int UpdateProfileCalls { get; private set; }
        public string? LastEmail { get; private set; }
        public ApiResponse UpdateProfileResult { get; set; } = new() { Success = true };

        public Task<ApiResponse> UpdateProfileAsync(string token, string? email, string? nickname, string? firstName, string? lastName, string? telephone, string? country, string? dateBirth)
        {
            UpdateProfileCalls++;
            LastEmail = email;
            return Task.FromResult(UpdateProfileResult);
        }

        public Task<ApiResponse> CheckEmailAsync(string email, string password) => throw new NotSupportedException();
        public Task<ApiResponse> CheckNicknameAsync(string nickname) => throw new NotSupportedException();
        public Task<ApiResponse<CustomerDto>> RegisterAsync(string email, string password, string nickname, bool agreeTerms, bool agreeNewsletter) => throw new NotSupportedException();
        public Task<ApiResponse<CustomerDto>> LoginAsync(string email, string password) => throw new NotSupportedException();
        public Task<ApiResponse> ForgottenPasswordAsync(string email) => throw new NotSupportedException();
        public Task<ApiResponse<CustomerDto>> GetProfileAsync(string token) => throw new NotSupportedException();
        public Task<AvatarUploadResponse> UploadAvatarAsync(string token, Stream fileStream, string fileName, string contentType) => throw new NotSupportedException();
        public Task<ApiResponse> CheckProfileNicknameAsync(string token, string nickname) => throw new NotSupportedException();
        public Task<ApiResponse> DeleteAccountAsync(string token, string password) => throw new NotSupportedException();
        public Task<ApiResponse> LogoutAsync(string token) => throw new NotSupportedException();
        public Task<ApiResponse> ContactUsAsync(string email, string topic, string message, string? token, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static (ProfileViewModel vm, RecordingAuthApi api, LocalStorageService storage) CreateVm(string sessionToken = "token-123")
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        var api = new RecordingAuthApi();
        var user = new UserProfile { Email = "old@example.com", SessionToken = sessionToken };
        var vm = new ProfileViewModel(user, new NotificationService(), api, storage);
        return (vm, api, storage);
    }

    [Fact]
    public async Task ConfirmEditEmail_MalformedAddress_SetsErrorAndSkipsApi()
    {
        var (vm, api, storage) = CreateVm();
        using var _s = storage;

        vm.EditEmailText = "not-an-email";
        await vm.ConfirmEditEmailCommand.ExecuteAsync(null);

        Assert.True(vm.HasEditEmailError);
        Assert.Equal(0, api.UpdateProfileCalls);
        Assert.Equal("old@example.com", vm.User.Email);
    }

    [Fact]
    public async Task ConfirmEditEmail_ValidAddress_UpdatesProfileAndClosesDialog()
    {
        var (vm, api, storage) = CreateVm();
        using var _s = storage;

        vm.IsEditEmailOpen = true;
        vm.EditEmailText = "  new@example.com  ";
        await vm.ConfirmEditEmailCommand.ExecuteAsync(null);

        Assert.Equal(1, api.UpdateProfileCalls);
        Assert.Equal("new@example.com", api.LastEmail);
        Assert.Equal("new@example.com", vm.User.Email);
        Assert.False(vm.IsEditEmailOpen);
        Assert.False(vm.HasEditEmailError);
    }

    [Fact]
    public async Task ConfirmEditEmail_EmailAlreadyExists_KeepsDialogOpenWithFieldError()
    {
        var (vm, api, storage) = CreateVm();
        using var _s = storage;

        api.UpdateProfileResult = new ApiResponse
        {
            Success = false,
            Errors = new List<ApiErrorEntry>
            {
                new() { ErrorCode = AuthErrorCodes.EmailAlreadyExists },
            },
        };

        vm.IsEditEmailOpen = true;
        vm.EditEmailText = "taken@example.com";
        await vm.ConfirmEditEmailCommand.ExecuteAsync(null);

        Assert.True(vm.IsEditEmailOpen);
        Assert.True(vm.HasEditEmailError);
        Assert.Equal("old@example.com", vm.User.Email);
    }

    [Fact]
    public async Task ConfirmEditEmail_GuestSession_UpdatesLocallyWithoutApiCall()
    {
        var (vm, api, storage) = CreateVm(sessionToken: string.Empty);
        using var _s = storage;

        vm.IsEditEmailOpen = true;
        vm.EditEmailText = "guest@example.com";
        await vm.ConfirmEditEmailCommand.ExecuteAsync(null);

        Assert.Equal(0, api.UpdateProfileCalls);
        Assert.Equal("guest@example.com", vm.User.Email);
        Assert.False(vm.IsEditEmailOpen);
    }

    [Theory]
    [InlineData("old@example.com")]      // untouched
    [InlineData("  old@example.com  ")]  // whitespace only
    [InlineData("OLD@Example.com")]      // capitalisation only — same mailbox
    [InlineData("")]
    [InlineData("   ")]
    public void HasEmailChange_IsFalse_WhenAddressIsEffectivelyUnchanged(string typed)
    {
        var (vm, _, storage) = CreateVm();
        using var _s = storage;

        vm.OpenEditEmailCommand.Execute(null);
        vm.EditEmailText = typed;

        Assert.False(vm.HasEmailChange);
        Assert.False(vm.ConfirmEditEmailCommand.CanExecute(null));
    }

    [Fact]
    public void HasEmailChange_IsTrue_OnceAddressDiffers()
    {
        var (vm, _, storage) = CreateVm();
        using var _s = storage;

        vm.OpenEditEmailCommand.Execute(null);
        Assert.False(vm.HasEmailChange);

        vm.EditEmailText = "new@example.com";

        Assert.True(vm.HasEmailChange);
        Assert.True(vm.ConfirmEditEmailCommand.CanExecute(null));
    }

    [Fact]
    public void HasEmailChange_GoesBackToFalse_AfterASuccessfulUpdate()
    {
        var (vm, _, storage) = CreateVm();
        using var _s = storage;

        vm.EditEmailText = "new@example.com";
        Assert.True(vm.HasEmailChange);

        vm.User.Email = "new@example.com";

        Assert.False(vm.HasEmailChange);
    }

    [Fact]
    public void EditingEmailText_ClearsPreviousError()
    {
        var (vm, _, storage) = CreateVm();
        using var _s = storage;

        vm.EditEmailError = "boom";
        vm.EditEmailText = "typing@example.com";

        Assert.False(vm.HasEditEmailError);
    }
}
