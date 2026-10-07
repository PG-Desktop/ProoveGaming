using System.Collections.Generic;
using System.Linq;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// First coverage of the shared auth error resolver (docs/plans/improve-api-error-surfacing).
// ResolveAll was already multi-error-aware and registration already consumes it — these tests
// lock that in, because the audit that produced this plan claimed the opposite and the next
// reader deserves an executable answer.
public class ApiErrorMessagesTests
{
	[Theory]
	[InlineData(AuthErrorCodes.EmailRequired, AuthField.Email)]
	[InlineData(AuthErrorCodes.InvalidEmail, AuthField.Email)]
	[InlineData(AuthErrorCodes.EmailAlreadyExists, AuthField.Email)]
	[InlineData(AuthErrorCodes.PasswordRequired, AuthField.Password)]
	[InlineData(AuthErrorCodes.PasswordTooShort, AuthField.Password)]
	[InlineData(AuthErrorCodes.PasswordNoUppercase, AuthField.Password)]
	[InlineData(AuthErrorCodes.PasswordNoNumber, AuthField.Password)]
	[InlineData(AuthErrorCodes.PasswordHasSpaces, AuthField.Password)]
	[InlineData(AuthErrorCodes.NicknameRequired, AuthField.Nickname)]
	[InlineData(AuthErrorCodes.NicknameTooShort, AuthField.Nickname)]
	[InlineData(AuthErrorCodes.NicknameTooLong, AuthField.Nickname)]
	[InlineData(AuthErrorCodes.NicknameHasSpaces, AuthField.Nickname)]
	[InlineData(AuthErrorCodes.NicknameInvalidCharacters, AuthField.Nickname)]
	[InlineData(AuthErrorCodes.NicknameAlreadyExists, AuthField.Nickname)]
	[InlineData(AuthErrorCodes.TermsRequired, AuthField.Terms)]
	public void Resolve_MapsEveryKnownCodeToItsField(string code, AuthField expected)
	{
		var resolved = ApiErrorMessages.Resolve(code);

		Assert.True(resolved.HasValue);
		Assert.Equal(expected, resolved!.Value.Field);
		Assert.False(string.IsNullOrEmpty(resolved.Value.Message));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("SOME_CODE_THE_CONTRACT_ADDED_LATER")]
	public void Resolve_ReturnsNothingForUnknownCodes(string? code) =>
		Assert.Null(ApiErrorMessages.Resolve(code));

	// The contract's own multi-error example. This is criterion 20: the one genuinely
	// multi-field form must surface BOTH entries, not just the first.
	[Fact]
	public void ResolveAll_YieldsEveryRecognizedEntryInOrder()
	{
		var response = new ApiResponse
		{
			Success = false,
			Errors = new List<ApiErrorEntry>
			{
				new() { ErrorCode = AuthErrorCodes.InvalidEmail, Field = "email" },
				new() { ErrorCode = AuthErrorCodes.PasswordTooShort, Field = "password" },
			},
		};

		var fields = ApiErrorMessages.ResolveAll(response).Select(r => r.Field).ToList();

		Assert.Equal(new[] { AuthField.Email, AuthField.Password }, fields);
	}

	// An unrecognized entry must not shadow a recognized one behind it — the defect the
	// single-field resolvers had.
	[Fact]
	public void ResolveAll_SkipsUnrecognizedEntriesInsteadOfStopping()
	{
		var response = new ApiResponse
		{
			Success = false,
			Errors = new List<ApiErrorEntry>
			{
				new() { ErrorCode = "UNKNOWN_CODE" },
				new() { ErrorCode = AuthErrorCodes.NicknameAlreadyExists, Field = "nickname" },
			},
		};

		var fields = ApiErrorMessages.ResolveAll(response).Select(r => r.Field).ToList();

		Assert.Equal(new[] { AuthField.Nickname }, fields);
	}

	// The single-code envelope shape: error_code at the top level, no errors[] array.
	[Fact]
	public void ResolveAll_IncludesTheTopLevelErrorCode()
	{
		var response = new ApiResponse { Success = false, ErrorCode = AuthErrorCodes.TermsRequired };

		var fields = ApiErrorMessages.ResolveAll(response).Select(r => r.Field).ToList();

		Assert.Equal(new[] { AuthField.Terms }, fields);
	}

	[Fact]
	public void ResolveAll_YieldsNothingWhenTheEnvelopeCarriesNoRecognizedCode()
	{
		var response = new ApiResponse
		{
			Success = false,
			ErrorCode = "UNKNOWN_CODE",
			Errors = new List<ApiErrorEntry> { new() { ErrorCode = "ANOTHER_UNKNOWN" } },
		};

		Assert.Empty(ApiErrorMessages.ResolveAll(response));
	}
}
