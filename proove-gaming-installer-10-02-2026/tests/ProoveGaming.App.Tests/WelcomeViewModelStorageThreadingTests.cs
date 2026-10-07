using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// Pins that sign-in writes nothing to storage inline on the calling (UI) thread
// (docs/plans/fix-repository-audit-findings, A4). Before, SaveLastSignIn ran synchronously inside
// SignInAsync; now it is queued on the storage executor.
//
// Thread ids are not a reliable signal in a test process, so this uses a gate instead: a blocking
// item is queued FIRST, which stops everything behind it from running. If the sign-in wrote inline,
// the storage seam would fire while the gate is still closed.
public class WelcomeViewModelStorageThreadingTests
{
    [Fact]
    public async Task SignIn_QueuesTheLastSignInWrite_InsteadOfWritingInline()
    {
        using var storage = new LocalStorageService("Data Source=:memory:");
        var vm = new WelcomeViewModel(new FakeAuthApi(), new UserProfile(), storage, new NotificationService());
        vm.SignInEmail = "user@example.com";
        vm.SignInPassword = "Password1";
        vm.IsRememberMeChecked = true;

        using var gate = new ManualResetEventSlim(false);
        var blocker = storage.RunOnStorageAsync(() => gate.Wait());
        var inlineAccesses = 0;
        storage.OnBeforeStorageAccess = () => Interlocked.Increment(ref inlineAccesses);

        await vm.SignInCommand.ExecuteAsync(null);

        Assert.Equal(0, Volatile.Read(ref inlineAccesses));

        gate.Set();
        await blocker;
        await storage.DrainStorageQueueAsync();

        var saved = storage.GetLastSignIn();
        Assert.Equal("user@example.com", saved?.Email);
        Assert.True(saved?.RememberMe);
    }

    private sealed class FakeAuthApi : IAuthApiService
    {
        public Task<ApiResponse<CustomerDto>> LoginAsync(string email, string password) =>
            Task.FromResult(new ApiResponse<CustomerDto>
            {
                Success = true,
                Data = new CustomerDto { CustomerId = 42, Email = email, Nickname = "Player" },
                Token = "token",
            });

        public Task<ApiResponse> ForgottenPasswordAsync(string email) =>
            throw new NotSupportedException();
        public Task<ApiResponse> CheckEmailAsync(string email, string password) =>
            throw new NotSupportedException();
        public Task<ApiResponse> CheckNicknameAsync(string nickname) =>
            throw new NotSupportedException();
        public Task<ApiResponse<CustomerDto>> RegisterAsync(string email, string password, string nickname, bool agreeTerms, bool agreeNewsletter) =>
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
}
