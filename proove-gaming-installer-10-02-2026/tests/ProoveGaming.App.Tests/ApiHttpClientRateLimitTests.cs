using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// HTTP 429 handling (docs/plans/improve-api-error-surfacing, criterion 8). The REAL
// ApiHttpClient runs against a stub HttpMessageHandler, mirroring
// ApiHttpClientTokenRejectionTests — transport is the only boundary faked.
//
// RFC 9110 allows Retry-After in two forms, delta-seconds and an HTTP-date, and HttpClient
// parses both into RetryConditionHeaderValue. The date form is the one that can go wrong: a
// date already past, or a client clock ahead of the server, yields a NEGATIVE wait that a
// caller would hand straight to Task.Delay.
//
// StubHandler is duplicated here rather than promoted out of the token-rejection file: sharing
// it would mean editing a file full of passing tests for no behavioural reason (the plan
// names this as the default, open question 5).
public class ApiHttpClientRateLimitTests
{
	private const string LimitedBody =
		"""{"success":false,"message":"Too many requests"}""";

	[Fact]
	public async Task PostAsync_429WithDeltaSecondsRetryAfter_SetsIsRateLimitedAndTheWait()
	{
		using var client = NewClient(Limited(h => h.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30))));

		var result = await client.PostAsync<ApiResponse>("contact", body: null);

		Assert.True(result.IsRateLimited);
		Assert.Equal(429, result.StatusCode);
		Assert.Equal(TimeSpan.FromSeconds(30), result.RetryAfter);
		Assert.False(result.Success);
		Assert.False(result.IsNetworkError);
	}

	// The header may instead name an absolute moment. HttpClient hands it over as a
	// DateTimeOffset, so the client has to turn it into a remaining wait itself.
	[Fact]
	public async Task PostAsync_429WithFutureHttpDateRetryAfter_ConvertsItToARemainingWait()
	{
		using var client = NewClient(Limited(h =>
			h.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(2))));

		var result = await client.PostAsync<ApiResponse>("contact", body: null);

		Assert.True(result.IsRateLimited);
		Assert.True(result.RetryAfter.HasValue);
		// A window rather than an exact value: the conversion is relative to "now", so the
		// assertion must not race the clock between the header being written and read.
		Assert.InRange(result.RetryAfter!.Value, TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(2));
	}

	// The clamp that the plan calls required rather than optional: a date already in the
	// past (a stale header, or a client clock running ahead of the server) must yield zero,
	// never a negative TimeSpan — Task.Delay throws on those, and a cooldown computed from one
	// would be nonsense.
	[Fact]
	public async Task PostAsync_429WithHttpDateAlreadyPast_ClampsTheWaitToZero()
	{
		using var client = NewClient(Limited(h =>
			h.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(-5))));

		var result = await client.PostAsync<ApiResponse>("contact", body: null);

		Assert.True(result.IsRateLimited);
		Assert.Equal(TimeSpan.Zero, result.RetryAfter);
	}

	// api/contact may omit the header entirely. The refusal is still a refusal — callers fall
	// back to their own default cooldown — so IsRateLimited must not depend on the header.
	[Fact]
	public async Task PostAsync_429WithoutRetryAfter_IsStillRateLimitedWithANullWait()
	{
		using var client = NewClient(Limited(_ => { }));

		var result = await client.PostAsync<ApiResponse>("contact", body: null);

		Assert.True(result.IsRateLimited);
		Assert.Null(result.RetryAfter);
	}

	// IsRateLimited keys off the status alone, so every other response must read false —
	// otherwise the sync pass would arm a cooldown for an ordinary refusal.
	[Theory]
	[InlineData(HttpStatusCode.OK)]
	[InlineData(HttpStatusCode.BadRequest)]
	[InlineData(HttpStatusCode.Unauthorized)]
	[InlineData(HttpStatusCode.Conflict)]
	[InlineData(HttpStatusCode.InternalServerError)]
	public async Task PostAsync_NonRateLimitedStatus_LeavesIsRateLimitedFalse(HttpStatusCode status)
	{
		using var client = NewClient(new StubHandler((_, _) =>
			Task.FromResult(JsonResponse(status, LimitedBody))));

		var result = await client.PostAsync<ApiResponse>("contact", body: null);

		Assert.False(result.IsRateLimited);
		Assert.Null(result.RetryAfter);
	}

	// A Retry-After on a non-429 is still parsed (the header is read per response, not per
	// status) — worth pinning so nobody "optimizes" the read behind the 429 branch and
	// quietly changes what the field means.
	[Fact]
	public async Task PostAsync_RetryAfterOnANon429_IsStillCaptured()
	{
		using var client = NewClient(new StubHandler((_, _) =>
		{
			var response = JsonResponse(HttpStatusCode.ServiceUnavailable, LimitedBody);
			response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(5));
			return Task.FromResult(response);
		}));

		var result = await client.PostAsync<ApiResponse>("contact", body: null);

		Assert.False(result.IsRateLimited);
		Assert.Equal(TimeSpan.FromSeconds(5), result.RetryAfter);
	}

	// A network error never reached the server, so there is no status to read: IsRateLimited
	// must stay false rather than keying off a zeroed StatusCode.
	[Fact]
	public async Task PostAsync_NetworkError_IsNotRateLimited()
	{
		using var client = NewClient(new StubHandler(
			(_, _) => throw new HttpRequestException("connection refused")));

		var result = await client.PostAsync<ApiResponse>("contact", body: null);

		Assert.True(result.IsNetworkError);
		Assert.False(result.IsRateLimited);
		Assert.Null(result.RetryAfter);
	}

	// ── Test doubles / construction ──────────────────────────────────────────

	private static ApiHttpClient NewClient(HttpMessageHandler handler) =>
		new(handler, "http://api.test.local/");

	// A 429 whose headers the caller decorates — the Retry-After form under test.
	private static StubHandler Limited(Action<HttpResponseHeaders> decorate) =>
		new((_, _) =>
		{
			var response = JsonResponse(HttpStatusCode.TooManyRequests, LimitedBody);
			decorate(response.Headers);
			return Task.FromResult(response);
		});

	private static HttpResponseMessage JsonResponse(HttpStatusCode status, string body) =>
		new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

	private sealed class StubHandler : HttpMessageHandler
	{
		private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

		public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
			_respond = respond;

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
			_respond(request, ct);
	}
}
