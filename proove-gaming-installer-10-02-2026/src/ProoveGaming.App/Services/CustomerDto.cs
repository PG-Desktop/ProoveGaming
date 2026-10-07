using System.Text.Json.Serialization;

namespace ProoveGaming.App.Services;

public sealed class CustomerDto
{
    [JsonPropertyName("customer_id")]
    public int CustomerId { get; set; }

    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;

    [JsonPropertyName("nickname")]
    public string Nickname { get; set; } = string.Empty;

    [JsonPropertyName("firstname")]
    public string? FirstName { get; set; }

    [JsonPropertyName("lastname")]
    public string? LastName { get; set; }

    [JsonPropertyName("telephone")]
    public string? Telephone { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("date_birth")]
    public string? DateBirth { get; set; }

    [JsonPropertyName("avatar")]
    public string? Avatar { get; set; }

    [JsonPropertyName("newsletter")]
    public int Newsletter { get; set; }

    [JsonPropertyName("date_joined")]
    public string? DateJoined { get; set; }
}
