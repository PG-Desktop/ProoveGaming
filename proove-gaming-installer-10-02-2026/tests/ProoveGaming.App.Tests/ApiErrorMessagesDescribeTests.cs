using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// Pins ApiErrorMessages.Describe, the one rule for the text a failed API call shows when no
// field-specific message applies (docs/plans/fix-repository-audit-findings, S10/A8). No resource
// dictionary is loaded in tests, so LocalizationManager.GetString returns the key itself; the
// assertions are therefore on keys, which is exactly the contract.
public class ApiErrorMessagesDescribeTests
{
	[Theory]
	[InlineData(ClientErrorCodes.NetworkError, "Api_Error_Network")]
	[InlineData(ClientErrorCodes.Timeout, "Api_Error_Timeout")]
	[InlineData(ClientErrorCodes.InvalidResponse, "Api_Error_Generic")]
	public void ClientTransportCode_MapsToItsLocalizedKey(string code, string expectedKey)
	{
		var response = new ApiResponse { Success = false, IsNetworkError = true, ErrorCode = code };

		Assert.Equal(expectedKey, ApiErrorMessages.Describe(response, "Fallback_Key"));
	}

	[Fact]
	public void RateLimited_WinsOverEverythingElse()
	{
		var response = new ApiResponse { Success = false, StatusCode = 429, ErrorCode = ClientErrorCodes.NetworkError };

		Assert.Equal("Api_Error_TooManyRequests", ApiErrorMessages.Describe(response, "Fallback_Key"));
	}

	[Fact]
	public void ServerMessage_IsNeverReturned()
	{
		// An unmapped server refusal: its English message must not reach a Ukrainian UI.
		var response = new ApiResponse { Success = false, ErrorCode = "SOME_NEW_SERVER_CODE", Message = "Something the server said" };

		Assert.Equal("Fallback_Key", ApiErrorMessages.Describe(response, "Fallback_Key"));
	}
}
