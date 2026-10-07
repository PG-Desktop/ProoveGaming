using System;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// The Contact Us dialog's SEND gate (docs/plans/add-contact-us-dialog): a well-formed email
// plus a topic of 3-100 and a message of 10-2000 characters. Like DeriveHidTransports and
// LowBatteryDecision, the rule is a static helper because HomeViewModel's constructor needs
// the full device/session service graph.
//
// The minimums were "non-blank" until docs/plans/improve-api-error-surfacing aligned them with
// the v2.0 §20 contract; the default fixture message below grew past 10 characters with it.
public class HomeViewModelContactUsTests
{
	private static bool Valid(
		string email = "user@proove.gg", string topic = "Firmware", string message = "The switch broke.") =>
		HomeViewModel.ContactUsFormValid(email, topic, message);

	[Fact]
	public void CompleteForm_IsValid()
	{
		Assert.True(Valid());
	}

	[Theory]
	[InlineData("")]
	[InlineData("not-an-email")]
	[InlineData("user@host")]
	[InlineData("user @host.com")]
	public void MalformedEmail_IsInvalid(string email)
	{
		Assert.False(Valid(email: email));
	}

	[Theory]
	[InlineData("")]
	[InlineData("   ")]
	public void BlankTopicOrMessage_IsInvalid(string blank)
	{
		Assert.False(Valid(topic: blank));
		Assert.False(Valid(message: blank));
	}

	// The TextBoxes hard-stop at MaxLength, so these only trip on a programmatic set — but the
	// rule is the contract, and the API limit must not depend on a UI property staying in place.
	[Fact]
	public void OverLimitTopicOrMessage_IsInvalid()
	{
		Assert.True(Valid(topic: new string('a', HomeViewModel.ContactUsTopicMax)));
		Assert.False(Valid(topic: new string('a', HomeViewModel.ContactUsTopicMax + 1)));

		Assert.True(Valid(message: new string('a', HomeViewModel.ContactUsMessageMax)));
		Assert.False(Valid(message: new string('a', HomeViewModel.ContactUsMessageMax + 1)));
	}

	// Criterion 5: the client gate now matches the contract's 3 / 10 minimums, so the
	// TOPIC_TOO_SHORT / MESSAGE_TOO_SHORT codes stop being reachable through the UI.
	[Fact]
	public void UnderLimitTopicOrMessage_IsInvalid()
	{
		Assert.False(Valid(topic: new string('a', HomeViewModel.ContactUsTopicMin - 1)));
		Assert.True(Valid(topic: new string('a', HomeViewModel.ContactUsTopicMin)));

		Assert.False(Valid(message: new string('a', HomeViewModel.ContactUsMessageMin - 1)));
		Assert.True(Valid(message: new string('a', HomeViewModel.ContactUsMessageMin)));
	}

	// Whitespace padding does not buy length: the send trims before posting, so the gate has
	// to measure the trimmed value or a message of ten spaces would sail through.
	[Fact]
	public void WhitespaceOnlyPaddingDoesNotSatisfyTheMinimums()
	{
		Assert.False(Valid(topic: "   "));
		Assert.False(Valid(message: new string(' ', HomeViewModel.ContactUsMessageMin)));
	}

	// The open-time prefill rule (PR #118 m2/m3): the profile email fills only an EMPTY field —
	// a draft the user typed (any owner's) is never overwritten on open; owner changes clear the
	// draft via ResetContactUsOwnerState, which is what makes this guard safe.
	[Fact]
	public void EmailOnOpen_EmptyDraft_PrefilledFromProfile()
	{
		Assert.Equal("me@proove.gg", HomeViewModel.ContactUsEmailOnOpen("", "me@proove.gg"));
	}

	[Fact]
	public void EmailOnOpen_ExistingDraft_IsKept()
	{
		Assert.Equal("draft@host.com", HomeViewModel.ContactUsEmailOnOpen("draft@host.com", "me@proove.gg"));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public void EmailOnOpen_NoProfileEmail_StaysEmpty(string? profileEmail)
	{
		Assert.Equal(string.Empty, HomeViewModel.ContactUsEmailOnOpen(string.Empty, profileEmail));
	}

	// The ✕/CANCEL gate (review R1 S2, pinned per PR #118 m3): closing is a no-op while the
	// send loader runs, so a mid-"send" close can never end in the success toast.
	[Fact]
	public void CanCancel_OnlyWhenNotSending()
	{
		Assert.True(HomeViewModel.ContactUsCanCancel(sending: false));
		Assert.False(HomeViewModel.ContactUsCanCancel(sending: true));
	}

	// The outcome rule (docs/plans/add-api-v2-sync): Success wins; everything else — network
	// error, 404, 5xx, a 2xx whose body was not the JSON envelope, a well-formed
	// success:false — is a real failure the user must see. API v2.0 documents and deploys
	// api/contact, so the former AcceptedRouteMissing degraded mode is retired.
	private static ApiResponse Response(
		bool success = false, int status = 0, bool malformed = false, bool networkError = false) =>
		new() { Success = success, StatusCode = status, MalformedPayload = malformed, IsNetworkError = networkError };

	[Fact]
	public void Outcome_2xxSuccessEnvelope_IsDelivered()
	{
		Assert.Equal(
			HomeViewModel.ContactUsOutcome.Delivered,
			HomeViewModel.DecideContactUsOutcome(Response(success: true, status: 200)));
	}

	// Any body shape: a clean JSON 404 and an HTML 404 are both failures now that the route
	// is documented and deployed — no "accepted" euphemism.
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Outcome_404_IsFailed(bool malformed)
	{
		Assert.Equal(
			HomeViewModel.ContactUsOutcome.Failed,
			HomeViewModel.DecideContactUsOutcome(Response(status: 404, malformed: malformed)));
	}

	[Fact]
	public void Outcome_2xxNonEnvelopeBody_IsFailed()
	{
		Assert.Equal(
			HomeViewModel.ContactUsOutcome.Failed,
			HomeViewModel.DecideContactUsOutcome(Response(status: 200, malformed: true)));
	}

	// Network error and the 15 s timeout surface as the same IsNetworkError flag
	// (StatusCode stays 0: the request never completed).
	[Fact]
	public void Outcome_NetworkErrorOrTimeout_IsFailed()
	{
		Assert.Equal(
			HomeViewModel.ContactUsOutcome.Failed,
			HomeViewModel.DecideContactUsOutcome(Response(networkError: true)));
	}

	[Fact]
	public void Outcome_ServerError_IsFailed()
	{
		Assert.Equal(
			HomeViewModel.ContactUsOutcome.Failed,
			HomeViewModel.DecideContactUsOutcome(Response(status: 500)));
	}

	// A well-formed success:false envelope on a 2xx is a validation reject, not a missing route.
	[Fact]
	public void Outcome_2xxValidationReject_IsFailed()
	{
		Assert.Equal(
			HomeViewModel.ContactUsOutcome.Failed,
			HomeViewModel.DecideContactUsOutcome(Response(success: false, status: 200)));
	}

	// ── SendWithTimeoutAsync (docs/plans/fix-repository-audit-findings, A9 / review round 2) ──
	//
	// ApiHttpClient now lets a caller-token cancellation propagate. The dialog's 15 s timeout IS a
	// caller token, so without this helper a black-holed connection would throw out of the send
	// and leave the dialog on its spinner instead of showing the send-failed line.

	[Fact]
	public async Task SendWithTimeout_OwnTimeoutFires_ReturnsTheFailedShape_InsteadOfThrowing()
	{
		var response = await HomeViewModel.SendWithTimeoutAsync(
			async token =>
			{
				await Task.Delay(Timeout.Infinite, token);
				return new ApiResponse { Success = true };
			},
			TimeSpan.FromMilliseconds(50),
			CancellationToken.None);

		Assert.True(response.IsNetworkError);
		Assert.Equal(ClientErrorCodes.Timeout, response.ErrorCode);
		Assert.Equal(HomeViewModel.ContactUsOutcome.Failed, HomeViewModel.DecideContactUsOutcome(response));
		Assert.Equal("ContactUs_SendFailed", ContactUsErrors.ResolveKey(response));
	}

	[Fact]
	public async Task SendWithTimeout_CallerCancels_Propagates()
	{
		using var cts = new CancellationTokenSource();
		var send = HomeViewModel.SendWithTimeoutAsync(
			async token =>
			{
				await Task.Delay(Timeout.Infinite, token);
				return new ApiResponse { Success = true };
			},
			TimeSpan.FromMinutes(5),
			cts.Token);

		cts.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
	}

	[Fact]
	public async Task SendWithTimeout_AnsweredInTime_ReturnsTheServerResponse()
	{
		var delivered = new ApiResponse { Success = true, StatusCode = 200 };

		var response = await HomeViewModel.SendWithTimeoutAsync(_ => Task.FromResult(delivered), TimeSpan.FromMinutes(5), CancellationToken.None);

		Assert.Same(delivered, response);
	}
}
