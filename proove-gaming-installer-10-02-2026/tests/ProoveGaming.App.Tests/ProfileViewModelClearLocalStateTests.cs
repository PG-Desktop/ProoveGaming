using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// Regression guard for the PR #64 review bug: session-token invalidation left profile
// popups covering the Welcome screen because `ClearLocalState` wasn't touching the dialog
// flags. If someone accidentally drops one of these resets, this test fails.
public class ProfileViewModelClearLocalStateTests
{
    private sealed class ThrowingAuthApi : IAuthApiService
    {
        public Task<ApiResponse> CheckEmailAsync(string email, string password) => throw new NotSupportedException();
        public Task<ApiResponse> CheckNicknameAsync(string nickname) => throw new NotSupportedException();
        public Task<ApiResponse<CustomerDto>> RegisterAsync(string email, string password, string nickname, bool agreeTerms, bool agreeNewsletter) => throw new NotSupportedException();
        public Task<ApiResponse<CustomerDto>> LoginAsync(string email, string password) => throw new NotSupportedException();
        public Task<ApiResponse> ForgottenPasswordAsync(string email) => throw new NotSupportedException();
        public Task<ApiResponse<CustomerDto>> GetProfileAsync(string token) => throw new NotSupportedException();
        public Task<ApiResponse> UpdateProfileAsync(string token, string? email, string? nickname, string? firstName, string? lastName, string? telephone, string? country, string? dateBirth) => throw new NotSupportedException();
        public Task<AvatarUploadResponse> UploadAvatarAsync(string token, Stream fileStream, string fileName, string contentType) => throw new NotSupportedException();
        public Task<ApiResponse> CheckProfileNicknameAsync(string token, string nickname) => throw new NotSupportedException();
        public Task<ApiResponse> DeleteAccountAsync(string token, string password) => throw new NotSupportedException();
        public Task<ApiResponse> LogoutAsync(string token) => throw new NotSupportedException();
        public Task<ApiResponse> ContactUsAsync(string email, string topic, string message, string? token, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static (ProfileViewModel vm, LocalStorageService storage) CreateVm()
    {
        var storage = new LocalStorageService("Data Source=:memory:");
        var vm = new ProfileViewModel(new UserProfile(), new NotificationService(), new ThrowingAuthApi(), storage);
        return (vm, storage);
    }

    [Fact]
    public void ClearLocalState_ClosesAllProfileDialogs()
    {
        var (vm, storage) = CreateVm();
        using var _s = storage;

        vm.IsEditNicknameOpen = true;
        vm.IsEditEmailOpen = true;
        vm.IsDeleteAccountOpen = true;
        vm.IsChangePasswordOpen = true;

        vm.ClearLocalState();

        Assert.False(vm.IsEditNicknameOpen);
        Assert.False(vm.IsEditEmailOpen);
        Assert.False(vm.IsDeleteAccountOpen);
        Assert.False(vm.IsChangePasswordOpen);
    }

    [Fact]
    public void ClearLocalState_ClearsDialogTransientText()
    {
        var (vm, storage) = CreateVm();
        using var _s = storage;

        vm.EditNicknameText = "wip";
        vm.EditNicknameError = "boom";
        vm.EditEmailText = "typing@example.com";
        vm.EditEmailError = "bad email";
        vm.DeleteAccountPassword = "hunter2";
        vm.DeleteAccountError = "bad password";
        vm.IsDeleteAccountSubmitting = true;
        vm.ChangePasswordEmail = "typing@example.com";
        vm.ChangePasswordEmailError = true;
        vm.ChangePasswordApiError = "boom";
        vm.IsChangePasswordSubmitting = true;

        vm.ClearLocalState();

        Assert.Equal(string.Empty, vm.EditNicknameText);
        Assert.Equal(string.Empty, vm.EditNicknameError);
        Assert.Equal(string.Empty, vm.EditEmailText);
        Assert.Equal(string.Empty, vm.EditEmailError);
        Assert.Equal(string.Empty, vm.DeleteAccountPassword);
        Assert.Equal(string.Empty, vm.DeleteAccountError);
        Assert.False(vm.IsDeleteAccountSubmitting);
        Assert.Equal(string.Empty, vm.ChangePasswordEmail);
        Assert.False(vm.ChangePasswordEmailError);
        Assert.Equal(string.Empty, vm.ChangePasswordApiError);
        Assert.False(vm.IsChangePasswordSubmitting);
    }

    [Fact]
    public void ClearLocalState_ResetsProfileSectionToAccount()
    {
        var (vm, storage) = CreateVm();
        using var _s = storage;

        vm.SelectedSection = ProfileSection.Security;
        vm.HoveredSection = ProfileSection.Security;

        vm.ClearLocalState();

        Assert.Equal(ProfileSection.Account, vm.SelectedSection);
        Assert.Null(vm.HoveredSection);
    }

    [Fact]
    public void ClearLocalState_ClearsUserFields()
    {
        var (vm, storage) = CreateVm();
        using var _s = storage;

        vm.User.CustomerId = 42;
        vm.User.DisplayName = "player";
        vm.User.Email = "player@example.com";
        vm.User.SessionToken = "token";
        vm.User.Country = "Ukraine";
        vm.User.DateOfBirth = new DateOnly(1990, 1, 1);

        vm.ClearLocalState();

        Assert.Equal(0, vm.User.CustomerId);
        Assert.Equal(string.Empty, vm.User.DisplayName);
        Assert.Equal(string.Empty, vm.User.Email);
        Assert.Equal(string.Empty, vm.User.SessionToken);
        Assert.Equal(string.Empty, vm.User.Country);
        Assert.Null(vm.User.DateOfBirth);
    }
}
