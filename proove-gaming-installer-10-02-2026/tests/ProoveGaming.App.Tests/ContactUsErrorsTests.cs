using System.Collections.Generic;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// The Contact Us failure-wording resolver (docs/plans/improve-api-error-surfacing). The
// dialog used to answer every refusal with ContactUs_SendFailed ("please try again"), which
// for the three anti-spam codes is advice that re-hits the same limit.
//
// ResolveKey returns a RESOURCE KEY, not a rendered string, so these tests need no loaded
// resource dictionary and no dispatcher — they assert the mapping, which is where the
// behaviour lives.
public class ContactUsErrorsTests
{
	private const string Generic = "ContactUs_SendFailed";

	// Criteria 1-3: the three anti-spam refusals each get their own wording, because the
	// user action differs (wait a minute / wait a day / change the message).
	[Theory]
	[InlineData(AuthErrorCodes.RateLimitExceeded, "ContactUs_Error_RateLimited")]
	[InlineData(AuthErrorCodes.DailyLimitExceeded, "ContactUs_Error_DailyLimit")]
	[InlineData(AuthErrorCodes.DuplicateMessage, "ContactUs_Error_Duplicate")]
	public void ResolveKey_MapsEachAntiSpamCodeToItsOwnWording(string code, string expected) =>
		Assert.Equal(expected, ContactUsErrors.ResolveKey(TopLevel(code)));

	// The criteria are explicit that these must be three DISTINCT strings, and that none of
	// them is the generic one — a regression that points two of them at one key would still
	// pass the theory above.
	[Fact]
	public void ResolveKey_GivesTheThreeAntiSpamCodesThreeDistinctKeys()
	{
		var keys = new HashSet<string>
		{
			ContactUsErrors.ResolveKey(TopLevel(AuthErrorCodes.RateLimitExceeded)),
			ContactUsErrors.ResolveKey(TopLevel(AuthErrorCodes.DailyLimitExceeded)),
			ContactUsErrors.ResolveKey(TopLevel(AuthErrorCodes.DuplicateMessage)),
		};

		Assert.Equal(3, keys.Count);
		Assert.DoesNotContain(Generic, keys);
	}

	// Criterion 4: the remaining contract codes each map, and MAIL_ERROR stays separable
	// from the validation codes — retrying a mail-relay failure is useful, retrying a
	// too-long message is not.
	[Theory]
	[InlineData(AuthErrorCodes.MailError, "ContactUs_Error_MailError")]
	[InlineData(AuthErrorCodes.InvalidEmail, "ContactUs_Error_InvalidEmail")]
	[InlineData(AuthErrorCodes.EmailRequired, "ContactUs_Error_InvalidEmail")]
	[InlineData(AuthErrorCodes.TopicRequired, "ContactUs_Error_TopicLength")]
	[InlineData(AuthErrorCodes.TopicTooShort, "ContactUs_Error_TopicLength")]
	[InlineData(AuthErrorCodes.TopicTooLong, "ContactUs_Error_TopicLength")]
	[InlineData(AuthErrorCodes.MessageRequired, "ContactUs_Error_MessageLength")]
	[InlineData(AuthErrorCodes.MessageTooShort, "ContactUs_Error_MessageLength")]
	[InlineData(AuthErrorCodes.MessageTooLong, "ContactUs_Error_MessageLength")]
	public void ResolveKey_MapsTheRemainingContractCodes(string code, string expected) =>
		Assert.Equal(expected, ContactUsErrors.ResolveKey(TopLevel(code)));

	// INVALID_JSON is a client bug with nothing the user can act on, and an unrecognized code
	// is whatever the contract adds next — both keep the generic wording (criterion 4).
	[Theory]
	[InlineData(AuthErrorCodes.InvalidJson)]
	[InlineData("SOME_CODE_ADDED_LATER")]
	public void ResolveKey_FallsBackToTheGenericWording(string code) =>
		Assert.Equal(Generic, ContactUsErrors.ResolveKey(TopLevel(code)));

	[Fact]
	public void ResolveKey_FallsBackToTheGenericWordingWhenNoCodeIsSent() =>
		Assert.Equal(Generic, ContactUsErrors.ResolveKey(new ApiResponse { Success = false }));

	// A network error means no code can be trusted, and here "please try again" IS the
	// right advice — so the generic wording is correct rather than a fallback accident.
	// Checked before the 429 arm, so a transport failure that never reached the server can
	// never be reported as a rate limit.
	[Fact]
	public void ResolveKey_TreatsANetworkErrorAsTheGenericFailure()
	{
		var response = new ApiResponse { Success = false, IsNetworkError = true };

		Assert.Equal(Generic, ContactUsErrors.ResolveKey(response));
	}

	// Criterion 1 via the transport: the contract documents 429 for this endpoint, and the
	// server need not repeat the code inside the envelope.
	[Fact]
	public void ResolveKey_MapsABareHttp429ToTheRateLimitWording()
	{
		var response = new ApiResponse { Success = false, StatusCode = 429 };

		Assert.Equal("ContactUs_Error_RateLimited", ContactUsErrors.ResolveKey(response));
	}

	// The defect the plan calls out: a recognized code that is not FIRST in errors[] used to
	// be invisible, because only entry 0 was read. It must be found wherever it sits.
	[Fact]
	public void ResolveKey_FindsARecognizedCodeInALaterErrorsEntry()
	{
		var response = new ApiResponse
		{
			Success = false,
			Errors = new List<ApiErrorEntry>
			{
				new() { ErrorCode = "UNKNOWN_CODE" },
				new() { ErrorCode = AuthErrorCodes.DuplicateMessage },
			},
		};

		Assert.Equal("ContactUs_Error_Duplicate", ContactUsErrors.ResolveKey(response));
	}

	// A partially-bound envelope must not NRE the scan (the null entry rides along the same
	// way ApiHttpClientTokenRejectionTests keeps one in its errors[] fixture).
	[Fact]
	public void ResolveKey_ToleratesANullErrorsEntry()
	{
		var response = new ApiResponse
		{
			Success = false,
			Errors = new List<ApiErrorEntry> { null!, new() { ErrorCode = AuthErrorCodes.MailError } },
		};

		Assert.Equal("ContactUs_Error_MailError", ContactUsErrors.ResolveKey(response));
	}

	// Priority, not envelope order: when the server refuses for several reasons at once the
	// rate limit is the one the user has to act on, so it outranks a validation code no
	// matter which entry each arrived in.
	[Fact]
	public void ResolveKey_PrefersTheRateLimitOverALowerPriorityCode()
	{
		var response = new ApiResponse
		{
			Success = false,
			Errors = new List<ApiErrorEntry>
			{
				new() { ErrorCode = AuthErrorCodes.TopicTooShort },
				new() { ErrorCode = AuthErrorCodes.RateLimitExceeded },
			},
		};

		Assert.Equal("ContactUs_Error_RateLimited", ContactUsErrors.ResolveKey(response));
	}

	private static ApiResponse TopLevel(string code) =>
		new() { Success = false, ErrorCode = code };
}
