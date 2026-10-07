using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// Pins how ApiHttpClient reports requests that got no usable answer
// (docs/plans/fix-repository-audit-findings, A9 and S10/A8).
//
//   * A cancellation the CALLER asked for propagates. It used to come back as a network error
//     ("Request timed out"), so a sign-out or navigation looked like a connectivity failure.
//   * Every real transport failure comes back as IsNetworkError with a ClientErrorCodes value and
//     NO Message: exception text is English whatever the UI language, and HttpRequestException's
//     text names hosts and sockets. The UI localizes from the code (ApiErrorMessages.Describe).
//
// Runs against a stub HttpMessageHandler, mirroring ApiHttpClientRateLimitTests.
public class ApiHttpClientCancellationTests
{
	[Fact]
	public async Task CallerCancellation_Propagates()
	{
		using var cts = new CancellationTokenSource();
		using var client = NewClient(async (_, ct) =>
		{
			await Task.Delay(Timeout.Infinite, ct);
			return new HttpResponseMessage(HttpStatusCode.OK);
		});

		var call = client.PostAsync<ApiResponse>("profile", body: null, ct: cts.Token);
		cts.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
	}

	[Fact]
	public async Task TimeoutWithoutCallerCancellation_IsANetworkErrorWithTheTimeoutCode()
	{
		// HttpClient reports its own timeout as TaskCanceledException while the caller's token
		// is still live.
		using var client = NewClient((_, _) => throw new TaskCanceledException("HttpClient.Timeout elapsed"));

		var result = await client.PostAsync<ApiResponse>("profile", body: null);

		Assert.True(result.IsNetworkError);
		Assert.False(result.Success);
		Assert.Equal(ClientErrorCodes.Timeout, result.ErrorCode);
		Assert.Null(result.Message);
	}

	[Fact]
	public async Task HttpRequestException_IsANetworkError_WithoutItsText()
	{
		using var client = NewClient((_, _) =>
			throw new HttpRequestException("No such host is known. (proovegaming.com:443)"));

		var result = await client.PostAsync<ApiResponse>("profile", body: null);

		Assert.True(result.IsNetworkError);
		Assert.Equal(ClientErrorCodes.NetworkError, result.ErrorCode);
		Assert.Null(result.Message);
	}

	[Fact]
	public async Task UnreadableBody_CarriesTheInvalidResponseCode_NotEnglishText()
	{
		using var client = NewClient((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
		{
			Content = new StringContent("<html>not json</html>")
		}));

		var result = await client.PostAsync<ApiResponse>("profile", body: null);

		Assert.False(result.Success);
		Assert.True(result.MalformedPayload);
		Assert.Equal(ClientErrorCodes.InvalidResponse, result.ErrorCode);
		Assert.Null(result.Message);
	}

	private static ApiHttpClient NewClient(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
		new(new StubHandler(respond), "https://example.test/index.php?route=");

	private sealed class StubHandler : HttpMessageHandler
	{
		private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

		public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) =>
			_respond = respond;

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
			_respond(request, ct);
	}
}
