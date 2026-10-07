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

// v1.7 §11 revised flow: the signed-in "Change password" action reuses the forgotten-password
// flow — an in-app email dialog posts to api/forgotten and the reset completes on the website.
// api/profile/password_link is no longer part of the client surface.
public class ProfileViewModelChangePasswordTests
{
    private sealed class RecordingAuthApi : IAuthApiService
    {
        public int ForgottenPasswordCalls { get; private set; }
        public string? LastForgottenEmail { get; private set; }
        public ApiResponse ForgottenPasswordResult { get; set; } = new() { Success = true };
        public bool ThrowOnForgottenPassword { get; set; }

        public Task<ApiResponse> ForgottenPasswordAsync(string email)
        {
            ForgottenPasswordCalls++;
            LastForgottenEmail = email;
            if (ThrowOnForgottenPassword) throw new InvalidOperationException("network down");
            return Task.FromResult(ForgottenPasswordResult);
        }

        public Task<ApiResponse> CheckEmailAsync(string email, string password) => throw new NotSupportedException();
        public Task<ApiResponse> CheckNicknameAsync(string nickname) => throw new NotSupportedException();
        public Task<ApiResponse<CustomerDto>> RegisterAsync(string email, string password, string nickname, bool agreeTerms, bool agreeNewsletter) => throw new NotSupportedException();
        public Task<ApiResponse<CustomerDto>> LoginAsync(string email, string password) => throw new NotSupportedException();
        public Task<ApiResponse<CustomerDto>> GetProfileAsync(string token) => throw new NotSupportedException();
        public Task<ApiResponse> UpdateProfileAsync(string token, string? email, string? nickname, string? firstName, string? lastName, string? telephone, string? country, string? dateBirth) => throw new NotSupportedException();
        public Task<AvatarUploadResponse> UploadAvatarAsync(string token, Stream fileStream, string fileName, string contentType) => throw new NotSupportedException();
        public Task<ApiResponse> CheckProfileNicknameAsync(string token, string nickname) => throw new NotSupportedException();
        public Task<ApiResponse> DeleteAccountAsync(string token, string password) => throw new NotSupportedException();
        public Task<ApiResponse> LogoutAsync(string token) => throw new NotSupportedException();
        public Task<ApiResponse> ContactUsAsync(string email, string topic, string message, string? token, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static (ProfileViewModel vm, RecordingAuthApi api, LocalStorageService storage, NotificationService notification) CreateVm()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        var api = new RecordingAuthApi();
        var notification = new NotificationService();
        var user = new UserProfile { Email = "user@example.com", SessionToken = "token-123" };
        var vm = new ProfileViewModel(user, notification, api, storage);
        return (vm, api, storage, notification);
    }

    [Fact]
    public void ChangePassword_OpensDialogPrefilledWithAccountEmail_NoApiCall()
    {
        var (vm, api, storage, _n) = CreateVm();
        using var _s = storage;

        vm.ChangePasswordCommand.Execute(null);

        Assert.True(vm.IsChangePasswordOpen);
        Assert.Equal("user@example.com", vm.ChangePasswordEmail);
        Assert.Equal(0, api.ForgottenPasswordCalls);
    }

    [Fact]
    public async Task SendReset_InvalidEmailFormat_SetsFieldErrorAndSkipsApi()
    {
        var (vm, api, storage, _n) = CreateVm();
        using var _s = storage;

        vm.ChangePasswordCommand.Execute(null);
        vm.ChangePasswordEmail = "not-an-email";
        await vm.SendChangePasswordResetCommand.ExecuteAsync(null);

        Assert.True(vm.ChangePasswordEmailError);
        Assert.True(vm.IsChangePasswordOpen);
        Assert.Equal(0, api.ForgottenPasswordCalls);
    }

    [Fact]
    public void SendReset_EmptyEmail_CommandDisabled()
    {
        var (vm, _, storage, _n) = CreateVm();
        using var _s = storage;

        vm.ChangePasswordCommand.Execute(null);
        vm.ChangePasswordEmail = string.Empty;

        Assert.False(vm.SendChangePasswordResetCommand.CanExecute(null));
    }

    [Fact]
    public async Task SendReset_Success_ClosesDialogAndShowsNotification()
    {
        var (vm, api, storage, notification) = CreateVm();
        using var _s = storage;

        vm.ChangePasswordCommand.Execute(null);
        await vm.SendChangePasswordResetCommand.ExecuteAsync(null);

        Assert.False(vm.IsChangePasswordOpen);
        Assert.True(notification.IsVisible);
        Assert.Equal(1, api.ForgottenPasswordCalls);
        Assert.Equal("user@example.com", api.LastForgottenEmail);
        Assert.False(vm.IsChangePasswordSubmitting);
    }

    [Fact]
    public async Task SendReset_EmailNotFound_StaysOpenWithApiError()
    {
        var (vm, api, storage, _n) = CreateVm();
        using var _s = storage;
        api.ForgottenPasswordResult = new ApiResponse
        {
            Success = false,
            ErrorCode = AuthErrorCodes.EmailNotFound
        };

        vm.ChangePasswordCommand.Execute(null);
        await vm.SendChangePasswordResetCommand.ExecuteAsync(null);

        Assert.True(vm.IsChangePasswordOpen);
        Assert.False(vm.ChangePasswordEmailError);
        Assert.False(string.IsNullOrEmpty(vm.ChangePasswordApiError));
    }

    [Fact]
    public async Task SendReset_MailError_StaysOpenWithApiError()
    {
        var (vm, api, storage, _n) = CreateVm();
        using var _s = storage;
        api.ForgottenPasswordResult = new ApiResponse
        {
            Success = false,
            ErrorCode = AuthErrorCodes.MailError
        };

        vm.ChangePasswordCommand.Execute(null);
        await vm.SendChangePasswordResetCommand.ExecuteAsync(null);

        Assert.True(vm.IsChangePasswordOpen);
        Assert.False(string.IsNullOrEmpty(vm.ChangePasswordApiError));
    }

    [Fact]
    public async Task SendReset_InvalidEmailFromServer_MarksFieldError()
    {
        var (vm, api, storage, _n) = CreateVm();
        using var _s = storage;
        api.ForgottenPasswordResult = new ApiResponse
        {
            Success = false,
            Errors = new List<ApiErrorEntry> { new() { ErrorCode = AuthErrorCodes.InvalidEmail } }
        };

        vm.ChangePasswordCommand.Execute(null);
        await vm.SendChangePasswordResetCommand.ExecuteAsync(null);

        Assert.True(vm.IsChangePasswordOpen);
        Assert.True(vm.ChangePasswordEmailError);
        Assert.Equal(string.Empty, vm.ChangePasswordApiError);
    }

    [Fact]
    public async Task SendReset_ApiThrows_SetsGenericErrorAndClearsBusy()
    {
        var (vm, api, storage, _n) = CreateVm();
        using var _s = storage;
        api.ThrowOnForgottenPassword = true;

        vm.ChangePasswordCommand.Execute(null);
        await vm.SendChangePasswordResetCommand.ExecuteAsync(null);

        Assert.True(vm.IsChangePasswordOpen);
        Assert.False(string.IsNullOrEmpty(vm.ChangePasswordApiError));
        Assert.False(vm.IsChangePasswordSubmitting);
        Assert.True(vm.SendChangePasswordResetCommand.CanExecute(null));
    }

    [Fact]
    public async Task ReopenAfterSuccess_StartsWithAccountEmailPrefill()
    {
        var (vm, api, storage, _n) = CreateVm();
        using var _s = storage;

        vm.ChangePasswordCommand.Execute(null);
        vm.ChangePasswordEmail = "edited@example.com";
        await vm.SendChangePasswordResetCommand.ExecuteAsync(null);
        Assert.False(vm.IsChangePasswordOpen);

        vm.ChangePasswordCommand.Execute(null);
        Assert.True(vm.IsChangePasswordOpen);
        Assert.Equal("user@example.com", vm.ChangePasswordEmail);
        Assert.Equal("edited@example.com", api.LastForgottenEmail);
    }
}
