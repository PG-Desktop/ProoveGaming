using System.Collections.Generic;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// Avatar upload failure mapping (v2.0 §12), which had no coverage at all before
// docs/plans/improve-api-error-surfacing. The first two rows are characterization — they pin
// wording that must NOT change — and the rest are the three codes the client used to drop into
// the unknown-code default (UPLOAD_ERROR, UPLOAD_FAILED, AVATAR_REQUIRED) plus HTTP 429.
//
// The tests drive ProfileViewModel.ResolveAvatarErrorKey rather than the upload method:
// SetAvatarFromFileAsync launches the upload fire-and-forget AND writes into the real
// LocalApplicationData avatars folder while decoding an Avalonia Bitmap, so the end-to-end path
// is neither deterministic nor unit-testable. The mapping is the part that carries the
// behaviour, so it is the part pinned here. The token-rejection bounce and the
// transport-exception branch are asserted in ProfileViewModelAutoSaveTests / left to QA step 10
// respectively — see the plan's "Deliberately not automated".
public class ProfileViewModelAvatarUploadTests
{
	// Characterization: these two were already mapped and their wording must survive the change.
	[Theory]
	[InlineData(AuthErrorCodes.FileTooLarge, "ProfileView_Notification_AvatarTooLarge")]
	[InlineData(AuthErrorCodes.InvalidFileType, "ProfileView_Notification_AvatarInvalidType")]
	public void ResolveAvatarErrorKey_KeepsTheExistingMapping(string code, string expectedKey) =>
		Assert.Equal(expectedKey, ProfileViewModel.ResolveAvatarErrorKey(Failure(code)));

	// The three contract codes that used to fall into the unknown default (criterion 12).
	// UPLOAD_ERROR / UPLOAD_FAILED deliberately share the existing retry wording; AVATAR_REQUIRED
	// gets its own key so a support screenshot can tell it apart.
	[Theory]
	[InlineData(AuthErrorCodes.UploadError, "ProfileView_Notification_AvatarUploadFailed")]
	[InlineData(AuthErrorCodes.UploadFailed, "ProfileView_Notification_AvatarUploadFailed")]
	[InlineData(AuthErrorCodes.AvatarRequired, "ProfileView_Notification_AvatarRequired")]
	public void ResolveAvatarErrorKey_MapsTheContractCodesThatUsedToFallThrough(string code, string expectedKey)
	{
		Assert.Equal(expectedKey, ProfileViewModel.ResolveAvatarErrorKey(Failure(code)));

		// Guard against a regression that "maps" a code by pointing it at the generic default:
		// AVATAR_REQUIRED must stay distinguishable from an unrecognized code.
		if (code == AuthErrorCodes.AvatarRequired)
			Assert.NotEqual(ProfileViewModel.ResolveAvatarErrorKey(Failure("UNKNOWN")), expectedKey);
	}

	[Fact]
	public void ResolveAvatarErrorKey_FallsBackToTheGenericWordingForAnUnknownCode() =>
		Assert.Equal(
			"ProfileView_Notification_AvatarUploadFailed",
			ProfileViewModel.ResolveAvatarErrorKey(Failure("SOME_CODE_ADDED_LATER")));

	[Fact]
	public void ResolveAvatarErrorKey_FallsBackToTheGenericWordingWhenNoCodeIsSent() =>
		Assert.Equal(
			"ProfileView_Notification_AvatarUploadFailed",
			ProfileViewModel.ResolveAvatarErrorKey(new AvatarUploadResponse { Success = false }));

	// Criterion 11: a 429 shows the shared rate-limit wording, not the generic failure text.
	[Fact]
	public void ResolveAvatarErrorKey_PrefersTheSharedRateLimitWordingOn429() =>
		Assert.Equal(
			"Api_Error_TooManyRequests",
			ProfileViewModel.ResolveAvatarErrorKey(new AvatarUploadResponse { Success = false, StatusCode = 429 }));

	// 429 is transport-level, so it wins even when the envelope also carries a mapped code —
	// the server refused the call, and "image is too large" would be a lie about why.
	[Fact]
	public void ResolveAvatarErrorKey_LetsTheRateLimitWinOverAnEnvelopeCode()
	{
		var response = Failure(AuthErrorCodes.FileTooLarge);
		response.StatusCode = 429;

		Assert.Equal("Api_Error_TooManyRequests", ProfileViewModel.ResolveAvatarErrorKey(response));
	}

	private static AvatarUploadResponse Failure(string code) => new()
	{
		Success = false,
		ErrorCode = code,
		Errors = new List<ApiErrorEntry> { new() { ErrorCode = code } },
	};
}
