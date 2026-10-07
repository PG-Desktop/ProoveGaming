namespace ProoveGaming.App;

public class ApiSettings
{
    public required string UsersApiBaseUrl { get; set; }

    // Update-manifest URL polled by UpdateCheckService; null/empty/whitespace disables the
    // check. Nullable rather than required so existing configs bind unchanged.
    public string? AppUpdateManifestUrl { get; set; }
}
