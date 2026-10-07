using System.Collections.Generic;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// api/forgotten error resolution (docs/plans/add-forgot-password, amended by
// docs/plans/improve-api-error-surfacing). The resolver used to read errors[0] only, so a
// recognized code sitting behind an unrecognized one resolved to the GENERIC message —
// criterion 19. It now scans every entry for a code it handles.
//
// LocalizationManager.GetString falls back to the key when no resource dictionary is loaded,
// so the assertions below compare resource KEYS: no Avalonia app, no dispatcher.
public class ForgottenPasswordErrorsTests
{
	private const string NotFound = "WelcomeView_Forgot_Error_NotFound";
	private const string Mail = "WelcomeView_Forgot_Error_Mail";
	private const string Generic = "WelcomeView_Forgot_Error_Generic";

	// Characterization: the single-code envelope shapes are unchanged by the scan.
	[Fact]
	public void Resolve_EmailNotFound_ShowsTheNotFoundMessage()
	{
		var (isFieldError, message) = ForgottenPasswordErrors.Resolve(TopLevel(AuthErrorCodes.EmailNotFound));

		Assert.False(isFieldError);
		Assert.Equal(NotFound, message);
	}

	[Fact]
	public void Resolve_MailError_ShowsTheMailMessage()
	{
		var (isFieldError, message) = ForgottenPasswordErrors.Resolve(TopLevel(AuthErrorCodes.MailError));

		Assert.False(isFieldError);
		Assert.Equal(Mail, message);
	}

	// These two route to the field-level error under the email input, which carries no text.
	[Theory]
	[InlineData(AuthErrorCodes.EmailRequired)]
	[InlineData(AuthErrorCodes.InvalidEmail)]
	public void Resolve_EmailValidationCodes_MarkTheFieldRatherThanShowingText(string code)
	{
		var (isFieldError, message) = ForgottenPasswordErrors.Resolve(TopLevel(code));

		Assert.True(isFieldError);
		Assert.Empty(message);
	}

	[Fact]
	public void Resolve_UnrecognizedCode_FallsBackToTheGenericMessage()
	{
		var (isFieldError, message) = ForgottenPasswordErrors.Resolve(TopLevel("SOME_CODE_ADDED_LATER"));

		Assert.False(isFieldError);
		Assert.Equal(Generic, message);
	}

	// CRITERION 19, the actual behaviour change: before the scan this returned the generic
	// message, because entry 0 was unrecognized and nothing looked further.
	[Fact]
	public void Resolve_RecognizedCodeBehindAnUnrecognizedOne_ShowsTheRecognizedMessage()
	{
		var response = new ApiResponse
		{
			Success = false,
			Errors = new List<ApiErrorEntry>
			{
				new() { ErrorCode = "UNKNOWN_CODE" },
				new() { ErrorCode = AuthErrorCodes.EmailNotFound },
			},
		};

		var (isFieldError, message) = ForgottenPasswordErrors.Resolve(response);

		Assert.False(isFieldError);
		Assert.Equal(NotFound, message);
	}

	// The scan looks past a null entry too — a partially-bound envelope must not NRE it.
	[Fact]
	public void Resolve_ToleratesANullErrorsEntry()
	{
		var response = new ApiResponse
		{
			Success = false,
			Errors = new List<ApiErrorEntry> { null!, new() { ErrorCode = AuthErrorCodes.MailError } },
		};

		Assert.Equal(Mail, ForgottenPasswordErrors.Resolve(response).Message);
	}

	// The top-level code still wins over the array when both are present — that is the
	// shape the single-code endpoints use, and the scan must not reorder it.
	[Fact]
	public void Resolve_PrefersTheTopLevelCodeOverTheErrorsArray()
	{
		var response = new ApiResponse
		{
			Success = false,
			ErrorCode = AuthErrorCodes.EmailNotFound,
			Errors = new List<ApiErrorEntry> { new() { ErrorCode = AuthErrorCodes.MailError } },
		};

		Assert.Equal(NotFound, ForgottenPasswordErrors.Resolve(response).Message);
	}

	// Nothing recognized anywhere: the old first-entry read remains the fallback, so the
	// generic branch still has a code behind it rather than silently seeing null.
	[Fact]
	public void Resolve_NoRecognizedCodeAnywhere_StillFallsBackToTheGenericMessage()
	{
		var response = new ApiResponse
		{
			Success = false,
			Errors = new List<ApiErrorEntry>
			{
				new() { ErrorCode = "UNKNOWN_ONE" },
				new() { ErrorCode = "UNKNOWN_TWO" },
			},
		};

		Assert.Equal(Generic, ForgottenPasswordErrors.Resolve(response).Message);
	}

	// REGRESSION, review R1 S4. The scan null-guards its loop, but the fallback used to do
	// response.Errors[0].ErrorCode unguarded. This envelope reaches that fallback — no
	// top-level code, and nothing in errors[] is recognized — with a null first entry, which
	// threw NullReferenceException straight out of the resolver into the view-model. A
	// partially-bound envelope is a real shape here: ApiHttpClientTokenRejectionTests keeps a
	// null entry in its own fixture for the same reason.
	[Fact]
	public void Resolve_NullFirstEntryAndNoRecognizedCode_FallsBackInsteadOfThrowing()
	{
		var response = new ApiResponse
		{
			Success = false,
			Errors = new List<ApiErrorEntry> { null!, new() { ErrorCode = "UNKNOWN_CODE" } },
		};

		var (isFieldError, message) = ForgottenPasswordErrors.Resolve(response);

		Assert.False(isFieldError);
		Assert.Equal(Generic, message);
	}

	// The degenerate version: a single null entry and nothing else at all.
	[Fact]
	public void Resolve_OnlyANullEntry_FallsBackInsteadOfThrowing()
	{
		var response = new ApiResponse
		{
			Success = false,
			Errors = new List<ApiErrorEntry> { null! },
		};

		Assert.Equal(Generic, ForgottenPasswordErrors.Resolve(response).Message);
	}

	private static ApiResponse TopLevel(string code) =>
		new() { Success = false, ErrorCode = code };
}
