using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// Centralized token-rejection detection (docs/plans/add-global-token-expiry): the REAL
// ApiHttpClient is driven against a stub HttpMessageHandler — HTTP transport is the only
// boundary faked, per the repo's test policy. Covers the TokenRejected event contract:
// fires for both envelope shapes (top-level error_code and errors[]), delivers the exact
// token the failing request carried (stale-signal filtering contract), at most once per
// session even under concurrent failures, never for token-less requests or network errors,
// re-arms via ResetTokenRejectionLatch, and a faulting subscriber never turns a readable
// response into an API error.
public class ApiHttpClientTokenRejectionTests
{
	private const string Token = "session-token-1";

	private const string TopLevelRejectionBody =
		"""{"success":false,"message":"Session expired","error_code":"INVALID_OR_EXPIRED_TOKEN"}""";

	// A null entry rides along deliberately: a partially-bound envelope must not NRE the
	// errors[] scan (plan review focus 3).
	private const string ErrorsArrayRejectionBody =
		"""{"success":false,"errors":[null,{"error_code":"TOKEN_REQUIRED","message":"Token required"}]}""";

	// ── Detection: both envelope shapes ──────────────────────────────────────

	[Fact]
	public async Task PostAsync_TokenRequestAnsweredTopLevelInvalidOrExpiredToken_RaisesTokenRejected()
	{
		using var client = NewClient(StubHandler.Json(HttpStatusCode.Unauthorized, TopLevelRejectionBody));
		var fired = 0;
		client.TokenRejected += _ => Interlocked.Increment(ref fired);

		var result = await client.PostAsync<ApiResponse>("profile/get", body: null, Token);

		Assert.Equal(1, fired);
		Assert.False(result.Success);
		Assert.Equal(AuthErrorCodes.InvalidOrExpiredToken, result.ErrorCode);
	}

	[Fact]
	public async Task PostAsync_TokenRequestAnsweredTokenRequiredInsideErrorsArray_RaisesTokenRejected()
	{
		using var client = NewClient(StubHandler.Json(HttpStatusCode.Unauthorized, ErrorsArrayRejectionBody));
		var fired = 0;
		client.TokenRejected += _ => Interlocked.Increment(ref fired);

		var result = await client.PostAsync<ApiResponse>("profile/get", body: null, Token);

		Assert.Equal(1, fired);
		Assert.False(result.Success);
	}

	// ── Payload contract ─────────────────────────────────────────────────────

	// MainWindow drops stale signals by comparing the delivered token against the current
	// session's (Round 1 S2): a rejection landing after sign-out + re-sign-in reports the
	// OLD session's token and must be ignorable. That only works if the event delivers
	// exactly the token the failing request carried — per request, never a cached or
	// current value.
	[Fact]
	public async Task PostAsync_TokenRejected_DeliversTheExactTokenTheFailingRequestCarried()
	{
		using var client = NewClient(StubHandler.Json(HttpStatusCode.Unauthorized, TopLevelRejectionBody));
		var delivered = new List<string>();
		client.TokenRejected += t => delivered.Add(t);

		// A request attached with the old session's token comes back rejected…
		await client.PostAsync<ApiResponse>("profile/get", body: null, "stale-token-A");
		// …the user re-signs in (MainWindow re-arms the latch), and the new token is later
		// rejected too — each event must carry its own request's token.
		client.ResetTokenRejectionLatch();
		await client.PostAsync<ApiResponse>("profile/get", body: null, "fresh-token-B");

		Assert.Equal(new[] { "stale-token-A", "fresh-token-B" }, delivered);
	}

	// ── Once-only latch ──────────────────────────────────────────────────────

	// One expiry burst spans several concurrent calls (profile probe + sync pass): every
	// request is parked until all five are in flight, so their responses race the latch on
	// the pool — exactly one may win (plan review focus 2, Interlocked).
	[Fact]
	public async Task PostAsync_FiveConcurrentTokenRejections_RaiseTokenRejectedExactlyOnce()
	{
		const int calls = 5;
		var arrivals = 0;
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		using var client = NewClient(new StubHandler(async (_, _) =>
		{
			if (Interlocked.Increment(ref arrivals) == calls)
				gate.SetResult();
			await gate.Task;
			return JsonResponse(HttpStatusCode.Unauthorized, TopLevelRejectionBody);
		}));
		var fired = 0;
		client.TokenRejected += _ => Interlocked.Increment(ref fired);

		var results = await Task.WhenAll(
			Enumerable.Range(0, calls).Select(_ => client.PostAsync<ApiResponse>("profile/get", body: null, Token)));

		Assert.Equal(1, fired);
		// Every caller still gets its own parsed envelope — the latch mutes the event, not
		// the responses.
		Assert.All(results, r => Assert.Equal(AuthErrorCodes.InvalidOrExpiredToken, r.ErrorCode));
	}

	[Fact]
	public async Task ResetTokenRejectionLatch_ReArmsTheEventForTheNextSession()
	{
		using var client = NewClient(StubHandler.Json(HttpStatusCode.Unauthorized, TopLevelRejectionBody));
		var fired = 0;
		client.TokenRejected += _ => Interlocked.Increment(ref fired);

		await client.PostAsync<ApiResponse>("profile/get", body: null, Token);
		await client.PostAsync<ApiResponse>("profile/get", body: null, Token);
		// Sequential rejections within one session stay latched to a single event.
		Assert.Equal(1, fired);

		// MainWindow calls this when a new session starts (sign-in / restore / registration).
		client.ResetTokenRejectionLatch();
		await client.PostAsync<ApiResponse>("profile/get", body: null, Token);

		Assert.Equal(2, fired);
	}

	// ── Non-triggers ─────────────────────────────────────────────────────────

	// Login/register/guest Contact Us carry no Bearer token; even a rejection-shaped answer
	// must not sign anyone out (plan QA step 4).
	[Fact]
	public async Task PostAsync_TokenlessRequestWithRejectionShapedError_DoesNotRaiseTokenRejected()
	{
		using var client = NewClient(StubHandler.Json(HttpStatusCode.Unauthorized, TopLevelRejectionBody));
		var fired = 0;
		client.TokenRejected += _ => Interlocked.Increment(ref fired);

		var result = await client.PostAsync<ApiResponse>("auth/login", body: null, token: null);

		Assert.Equal(0, fired);
		Assert.Equal(AuthErrorCodes.InvalidOrExpiredToken, result.ErrorCode);
	}

	[Fact]
	public async Task PostAsync_NetworkErrorOnTokenRequest_DoesNotRaiseTokenRejected()
	{
		using var client = NewClient(new StubHandler(
			(_, _) => throw new HttpRequestException("connection refused")));
		var fired = 0;
		client.TokenRejected += _ => Interlocked.Increment(ref fired);

		var result = await client.PostAsync<ApiResponse>("profile/get", body: null, Token);

		Assert.Equal(0, fired);
		Assert.True(result.IsNetworkError);
	}

	[Fact]
	public async Task PostAsync_TokenRequestWithNonTokenErrorCode_DoesNotRaiseTokenRejected()
	{
		using var client = NewClient(StubHandler.Json(
			HttpStatusCode.BadRequest,
			"""{"success":false,"error_code":"INVALID_PASSWORD"}"""));
		var fired = 0;
		client.TokenRejected += _ => Interlocked.Increment(ref fired);

		var result = await client.PostAsync<ApiResponse>("profile/change_password", body: null, Token);

		Assert.Equal(0, fired);
		Assert.Equal(AuthErrorCodes.InvalidPassword, result.ErrorCode);
	}

	// ── Subscriber faults ────────────────────────────────────────────────────

	// SendAsync's blanket catch maps unexpected exceptions to IsNetworkError, so a throwing
	// subscriber would silently corrupt the caller's envelope if SignalTokenRejected let the
	// exception escape.
	[Fact]
	public async Task PostAsync_ThrowingTokenRejectedSubscriber_DoesNotFaultTheApiCall()
	{
		using var client = NewClient(StubHandler.Json(HttpStatusCode.Unauthorized, TopLevelRejectionBody));
		client.TokenRejected += _ => throw new InvalidOperationException("subscriber bug");

		var result = await client.PostAsync<ApiResponse>("profile/get", body: null, Token);

		// The response the server actually sent comes back intact.
		Assert.False(result.IsNetworkError);
		Assert.Equal((int)HttpStatusCode.Unauthorized, result.StatusCode);
		Assert.Equal(AuthErrorCodes.InvalidOrExpiredToken, result.ErrorCode);
	}

	// ── Test doubles / construction ──────────────────────────────────────────

	// ApiHttpClient's internal test constructor (InternalsVisibleTo covers this project)
	// takes the stub handler directly; the latch field starts at its zeroed (armed) default.
	private static ApiHttpClient NewClient(HttpMessageHandler handler) =>
		new(handler, "http://api.test.local/");

	private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) =>
		new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

	private sealed class StubHandler : HttpMessageHandler
	{
		private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

		public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
			_respond = respond;

		public static StubHandler Json(HttpStatusCode status, string body) =>
			new((_, _) => Task.FromResult(JsonResponse(status, body)));

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
			_respond(request, ct);
	}
}
