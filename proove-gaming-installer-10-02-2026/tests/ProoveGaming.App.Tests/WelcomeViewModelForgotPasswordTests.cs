using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

public class WelcomeViewModelForgotPasswordTests
{
    // Hand-rolled fake — only the ForgottenPasswordAsync path is exercised. All other members
    // throw so an unexpected call surfaces as a clear test failure rather than a silent stub.
    private sealed class FakeAuthApi : IAuthApiService
    {
        public int ForgottenPasswordCallCount { get; private set; }
        public string? LastForgottenEmail { get; private set; }
        public Func<string, ApiResponse>? ForgottenPasswordHandler { get; set; }

        public Task<ApiResponse> ForgottenPasswordAsync(string email)
        {
            ForgottenPasswordCallCount++;
            LastForgottenEmail = email;
            if (ForgottenPasswordHandler is null)
                return Task.FromResult(new ApiResponse { Success = true });
            var result = ForgottenPasswordHandler(email);
            return Task.FromResult(result);
        }

        public Task<ApiResponse> CheckEmailAsync(string email, string password) =>
            throw new NotSupportedException();
        public Task<ApiResponse> CheckNicknameAsync(string nickname) =>
            throw new NotSupportedException();
        public Task<ApiResponse<CustomerDto>> RegisterAsync(string email, string password, string nickname, bool agreeTerms, bool agreeNewsletter) =>
            throw new NotSupportedException();
        public Task<ApiResponse<CustomerDto>> LoginAsync(string email, string password) =>
            throw new NotSupportedException();
        public Task<ApiResponse<CustomerDto>> GetProfileAsync(string token) =>
            throw new NotSupportedException();
        public Task<ApiResponse> UpdateProfileAsync(string token, string? email, string? nickname, string? firstName, string? lastName, string? telephone, string? country, string? dateBirth) =>
            throw new NotSupportedException();
        public Task<AvatarUploadResponse> UploadAvatarAsync(string token, Stream fileStream, string fileName, string contentType) =>
            throw new NotSupportedException();
        public Task<ApiResponse> CheckProfileNicknameAsync(string token, string nickname) =>
            throw new NotSupportedException();
        public Task<ApiResponse> DeleteAccountAsync(string token, string password) =>
            throw new NotSupportedException();
        public Task<ApiResponse> LogoutAsync(string token) =>
            throw new NotSupportedException();
        public Task<ApiResponse> ContactUsAsync(string email, string topic, string message, string? token, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static (WelcomeViewModel vm, FakeAuthApi api, LocalStorageService storage, NotificationService notification) CreateVm()
    {
        var api = new FakeAuthApi();
        var storage = new LocalStorageService("Data Source=:memory:");
        var notification = new NotificationService();
        var vm = new WelcomeViewModel(api, new UserProfile(), storage, notification);
        return (vm, api, storage, notification);
    }

    [Fact]
    public void EnterForgotPassword_FromSignInWithFilledEmail_PrefillsForgotEmail()
    {
        var (vm, _, storage, _n) = CreateVm();
        using var _s = storage;

        vm.SignInEmail = "player@example.com";
        vm.EnterForgotPasswordCommand.Execute(null);

        Assert.True(vm.IsForgotPassword);
        Assert.False(vm.IsSignIn);
        Assert.Equal("player@example.com", vm.ForgotEmail);
    }

    [Fact]
    public async Task SendPasswordReset_EmptyEmail_ButtonDisabled_DoesNotCallApi()
    {
        var (vm, api, storage, _n) = CreateVm();
        using var _s = storage;

        vm.EnterForgotPasswordCommand.Execute(null);
        vm.ForgotEmail = string.Empty;

        Assert.False(vm.SendPasswordResetCommand.CanExecute(null));
        await vm.SendPasswordResetCommand.ExecuteAsync(null);
        Assert.Equal(0, api.ForgottenPasswordCallCount);
    }

    [Fact]
    public async Task SendPasswordReset_InvalidEmailFormat_SetsFieldErrorAndSkipsApi()
    {
        var (vm, api, storage, _n) = CreateVm();
        using var _s = storage;

        vm.EnterForgotPasswordCommand.Execute(null);
        vm.ForgotEmail = "not-an-email";
        await vm.SendPasswordResetCommand.ExecuteAsync(null);

        Assert.True(vm.ForgotEmailError);
        Assert.False(vm.IsForgotSent);
        Assert.Equal(0, api.ForgottenPasswordCallCount);
    }

    [Fact]
    public async Task SendPasswordReset_Success_FlipsToSentAndPreservesEmail()
    {
        var (vm, api, storage, _n) = CreateVm();
        using var _s = storage;

        api.ForgottenPasswordHandler = _ => new ApiResponse { Success = true };
        vm.EnterForgotPasswordCommand.Execute(null);
        vm.ForgotEmail = "player@example.com";
        await vm.SendPasswordResetCommand.ExecuteAsync(null);

        Assert.True(vm.IsForgotSent);
        Assert.Equal("player@example.com", vm.ForgotEmail);
        Assert.Equal(1, api.ForgottenPasswordCallCount);
        Assert.Equal("player@example.com", api.LastForgottenEmail);
        Assert.Equal(string.Empty, vm.ForgotApiError);
    }

    [Fact]
    public async Task SendPasswordReset_Success_ShowsTheResetLinkSentToast()
    {
        var (vm, api, storage, notification) = CreateVm();
        using var _s = storage;

        api.ForgottenPasswordHandler = _ => new ApiResponse { Success = true };
        vm.EnterForgotPasswordCommand.Execute(null);
        vm.ForgotEmail = "player@example.com";
        await vm.SendPasswordResetCommand.ExecuteAsync(null);

        Assert.True(notification.IsVisible);
        Assert.Equal("WelcomeView_Notification_ResetLinkSent", notification.Text);
    }

    [Theory]
    [InlineData("MAIL_ERROR")]
    [InlineData("EMAIL_NOT_FOUND")]
    public async Task SendPasswordReset_Failure_ShowsNoToast(string errorCode)
    {
        var (vm, api, storage, notification) = CreateVm();
        using var _s = storage;

        api.ForgottenPasswordHandler = _ => new ApiResponse { Success = false, ErrorCode = errorCode };
        vm.EnterForgotPasswordCommand.Execute(null);
        vm.ForgotEmail = "player@example.com";
        await vm.SendPasswordResetCommand.ExecuteAsync(null);

        Assert.False(notification.IsVisible);
        Assert.Equal(string.Empty, notification.Text);
    }

    [Fact]
    public async Task SendPasswordReset_InvalidEmailFormat_ShowsNoToast()
    {
        var (vm, _, storage, notification) = CreateVm();
        using var _s = storage;

        vm.EnterForgotPasswordCommand.Execute(null);
        vm.ForgotEmail = "not-an-email";
        await vm.SendPasswordResetCommand.ExecuteAsync(null);

        Assert.False(notification.IsVisible);
    }

    [Fact]
    public async Task SendPasswordReset_EmailNotFound_KeepsInputAndSetsApiError()
    {
        var (vm, api, storage, _n) = CreateVm();
        using var _s = storage;

        api.ForgottenPasswordHandler = _ => new ApiResponse
        {
            Success = false,
            ErrorCode = "EMAIL_NOT_FOUND",
            Message = "Email not found",
        };
        vm.EnterForgotPasswordCommand.Execute(null);
        vm.ForgotEmail = "missing@example.com";
        await vm.SendPasswordResetCommand.ExecuteAsync(null);

        Assert.False(vm.IsForgotSent);
        Assert.Equal("WelcomeView_Forgot_Error_NotFound", vm.ForgotApiError);
    }

    [Fact]
    public async Task SendPasswordReset_MailError_KeepsInputAndSetsApiError()
    {
        var (vm, api, storage, _n) = CreateVm();
        using var _s = storage;

        api.ForgottenPasswordHandler = _ => new ApiResponse
        {
            Success = false,
            ErrorCode = "MAIL_ERROR",
            Message = "Mail error",
        };
        vm.EnterForgotPasswordCommand.Execute(null);
        vm.ForgotEmail = "player@example.com";
        await vm.SendPasswordResetCommand.ExecuteAsync(null);

        Assert.False(vm.IsForgotSent);
        Assert.Equal("WelcomeView_Forgot_Error_Mail", vm.ForgotApiError);
    }

    [Fact]
    public async Task SendPasswordReset_NetworkException_SetsGenericErrorAndReenablesButton()
    {
        var (vm, api, storage, _n) = CreateVm();
        using var _s = storage;

        api.ForgottenPasswordHandler = _ => throw new HttpRequestSimulator();
        vm.EnterForgotPasswordCommand.Execute(null);
        vm.ForgotEmail = "player@example.com";
        await vm.SendPasswordResetCommand.ExecuteAsync(null);

        Assert.False(vm.IsForgotSent);
        Assert.Equal("WelcomeView_Forgot_Error_Generic", vm.ForgotApiError);
        Assert.False(vm.IsForgotSubmitting);
        Assert.True(vm.SendPasswordResetCommand.CanExecute(null));
    }

    [Fact]
    public async Task BackToSignIn_FromConfirmationState_ResetsIsForgotSent()
    {
        var (vm, api, storage, _n) = CreateVm();
        using var _s = storage;

        api.ForgottenPasswordHandler = _ => new ApiResponse { Success = true };
        vm.EnterForgotPasswordCommand.Execute(null);
        vm.ForgotEmail = "player@example.com";
        await vm.SendPasswordResetCommand.ExecuteAsync(null);
        Assert.True(vm.IsForgotSent);

        vm.BackToSignInCommand.Execute(null);

        Assert.False(vm.IsForgotSent);
        Assert.False(vm.IsForgotPassword);
        Assert.True(vm.IsSignIn);

        // Re-entering the flow starts on the input state again.
        vm.EnterForgotPasswordCommand.Execute(null);
        Assert.True(vm.IsForgotPassword);
        Assert.False(vm.IsForgotSent);
    }

    [Fact]
    public async Task IsForgotSubmitting_TogglesAroundApiCall_AllPaths()
    {
        var (vm, api, storage, _n) = CreateVm();
        using var _s = storage;

        vm.EnterForgotPasswordCommand.Execute(null);
        vm.ForgotEmail = "player@example.com";
        Assert.False(vm.IsForgotSubmitting);

        api.ForgottenPasswordHandler = _ => new ApiResponse { Success = true };
        await vm.SendPasswordResetCommand.ExecuteAsync(null);
        Assert.False(vm.IsForgotSubmitting);

        vm.BackToSignInCommand.Execute(null);
        vm.EnterForgotPasswordCommand.Execute(null);
        vm.ForgotEmail = "player@example.com";
        api.ForgottenPasswordHandler = _ => new ApiResponse { Success = false, ErrorCode = "MAIL_ERROR" };
        await vm.SendPasswordResetCommand.ExecuteAsync(null);
        Assert.False(vm.IsForgotSubmitting);

        api.ForgottenPasswordHandler = _ => throw new HttpRequestSimulator();
        await vm.SendPasswordResetCommand.ExecuteAsync(null);
        Assert.False(vm.IsForgotSubmitting);
    }

    private sealed class HttpRequestSimulator : Exception
    {
        public HttpRequestSimulator() : base("simulated network failure") { }
    }
}
