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

// Delete-account error resolution (docs/plans/improve-api-error-surfacing). The switch now
// scans every errors[] entry instead of reading entry 0 only, and its fallback is null-safe
// after review R1 S4. RecognizedDeleteAccountCode itself is private, so these drive the real
// ConfirmDeleteAccountCommand and assert the inline error it produces — no dispatcher needed,
// DeleteAccountError is set on the calling thread.
public class ProfileViewModelDeleteAccountTests
{
	private sealed class RecordingAuthApi : IAuthApiService
	{
		public ApiResponse DeleteAccountResult { get; set; } = new() { Success = true };

		public Task<ApiResponse> DeleteAccountAsync(string token, string password) => Task.FromResult(DeleteAccountResult);

		public Task<ApiResponse> UpdateProfileAsync(string token, string? email, string? nickname, string? firstName, string? lastName, string? telephone, string? country, string? dateBirth) => Task.FromResult(new ApiResponse { Success = true });

		public Task<ApiResponse> CheckProfileNicknameAsync(string token, string nickname) => throw new NotSupportedException();
		public Task<ApiResponse> CheckNicknameAsync(string nickname) => throw new NotSupportedException();
		public Task<ApiResponse> CheckEmailAsync(string email, string password) => throw new NotSupportedException();
		public Task<ApiResponse> ForgottenPasswordAsync(string email) => throw new NotSupportedException();
		public Task<ApiResponse> LogoutAsync(string token) => throw new NotSupportedException();
		public Task<ApiResponse> ContactUsAsync(string email, string topic, string message, string? token, CancellationToken ct = default) => throw new NotSupportedException();
		public Task<AvatarUploadResponse> UploadAvatarAsync(string token, Stream fileStream, string fileName, string contentType) => throw new NotSupportedException();
		public Task<ApiResponse<CustomerDto>> RegisterAsync(string email, string password, string nickname, bool agreeTerms, bool agreeNewsletter) => throw new NotSupportedException();
		public Task<ApiResponse<CustomerDto>> LoginAsync(string email, string password) => throw new NotSupportedException();
		public Task<ApiResponse<CustomerDto>> GetProfileAsync(string token) => throw new NotSupportedException();
	}

	private static (ProfileViewModel Vm, RecordingAuthApi Api, LocalStorageService Storage) CreateVm()
	{
		var storage = new LocalStorageService("Data Source=:memory:");
		var api = new RecordingAuthApi();
		var profile = new UserProfile();
		profile.DisplayName = "PlayerOne";
		profile.SessionToken = "token-123";
		var vm = new ProfileViewModel(profile, new NotificationService(), api, storage);
		vm.DeleteAccountPassword = "hunter2";
		return (vm, api, storage);
	}

	// REGRESSION, review R1 S4. No top-level code, nothing recognized in errors[], and a null
	// first entry: this reaches the fallback, which used to dereference Errors[0] unguarded and
	// throw NullReferenceException out of an async RelayCommand with no catch above it.
	[Fact]
	public async Task ConfirmDeleteAccount_NullFirstEntryAndNoRecognizedCode_ShowsAnErrorInsteadOfThrowing()
	{
		var (vm, api, storage) = CreateVm();
		using (storage)
		{
			api.DeleteAccountResult = new ApiResponse
			{
				Success = false,
				Errors = new List<ApiErrorEntry> { null!, new() { ErrorCode = "UNKNOWN_CODE" } },
			};

			await vm.ConfirmDeleteAccountCommand.ExecuteAsync(null);

			Assert.False(string.IsNullOrEmpty(vm.DeleteAccountError));
			Assert.False(vm.IsDeleteAccountSubmitting);
		}
	}

	// The scan half: a recognized code behind an unrecognized one used to fall into the generic
	// branch because only entry 0 was read.
	[Fact]
	public async Task ConfirmDeleteAccount_RecognizedCodeInALaterEntry_ShowsTheSpecificError()
	{
		var (vm, api, storage) = CreateVm();
		using (storage)
		{
			api.DeleteAccountResult = new ApiResponse
			{
				Success = false,
				Errors = new List<ApiErrorEntry>
				{
					new() { ErrorCode = "UNKNOWN_CODE" },
					new() { ErrorCode = AuthErrorCodes.InvalidPassword },
				},
			};

			await vm.ConfirmDeleteAccountCommand.ExecuteAsync(null);

			Assert.Equal("ProfileView_DeleteAccount_Error_InvalidPassword", vm.DeleteAccountError);
		}
	}

	// A token rejection anywhere in the envelope still bounces the session rather than showing
	// an inline error.
	[Fact]
	public async Task ConfirmDeleteAccount_TokenRejectionInALaterEntry_BouncesTheSession()
	{
		var (vm, api, storage) = CreateVm();
		using (storage)
		{
			var bounced = 0;
			vm.SessionTokenInvalid += () => Interlocked.Increment(ref bounced);
			api.DeleteAccountResult = new ApiResponse
			{
				Success = false,
				Errors = new List<ApiErrorEntry>
				{
					new() { ErrorCode = "UNKNOWN_CODE" },
					new() { ErrorCode = AuthErrorCodes.InvalidOrExpiredToken },
				},
			};

			await vm.ConfirmDeleteAccountCommand.ExecuteAsync(null);

			Assert.Equal(1, Volatile.Read(ref bounced));
			Assert.Empty(vm.DeleteAccountError);
		}
	}
}
