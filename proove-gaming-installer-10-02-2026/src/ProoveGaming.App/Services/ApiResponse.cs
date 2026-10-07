using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace ProoveGaming.App.Services;

public class ApiResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error_code")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("errors")]
    public List<ApiErrorEntry>? Errors { get; set; }

    [JsonPropertyName("nicknames")]
    public List<string>? Nicknames { get; set; }

    // Returned on 409 by api/check_nickname and api/profile/check_nickname when the requested
    // nickname is taken — up to 5 server-generated alternatives.
    [JsonPropertyName("suggestions")]
    public List<string>? Suggestions { get; set; }

    [JsonIgnore]
    public bool IsNetworkError { get; set; }

    // Transport diagnostics set by ApiHttpClient.ReadResponseAsync (never serialized): the
    // HTTP status (0 when the request never completed) and whether the body failed to
    // deserialize into this envelope — generic per-response facts each caller maps onto its
    // own outcome (Contact Us treats both as failure; the sync push treats a malformed 2xx
    // as success without write-back).
    [JsonIgnore]
    public int StatusCode { get; set; }

    [JsonIgnore]
    public bool MalformedPayload { get; set; }

    // Rate limiting (contract v2.0 §"HTTP status codes": 429 Too Many Requests). Transport-level
    // like IsNetworkError — ApiHttpClient decides it once per response and every caller consumes
    // the same fact: interactive callers show the shared Api_Error_TooManyRequests wording, the
    // sync services abort the pass and sit out a cooldown. RetryAfter carries the server's
    // Retry-After header when it sent one (null when it did not); it is never negative.
    [JsonIgnore]
    public bool IsRateLimited => StatusCode == 429;

    [JsonIgnore]
    public TimeSpan? RetryAfter { get; set; }
}

public class ApiResponse<T> : ApiResponse
{
    [JsonPropertyName("customer")]
    public T? Data { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("token_expires_in_days")]
    public int? TokenExpiresInDays { get; set; }
}

public sealed class ApiErrorEntry
{
    [JsonPropertyName("error_code")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("field")]
    public string? Field { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

public sealed class AvatarUploadResponse : ApiResponse
{
    [JsonPropertyName("avatar")]
    public string? AvatarUrl { get; set; }
}
