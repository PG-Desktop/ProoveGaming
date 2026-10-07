using System.Text.Json.Serialization;

namespace ProoveGaming.App.Models;

// The client's view of the update manifest (docs/plans/contact-us-and-update-check):
// snake_case JSON matching the rest of the backend API; unknown extra fields are ignored.
// Validation (required version / download_url) lives in UpdateCheckService.TryParseManifest.
public sealed class AppUpdateInfo
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("download_url")]
    public string? DownloadUrl { get; set; }

    [JsonPropertyName("release_notes")]
    public string? ReleaseNotes { get; set; }
}
