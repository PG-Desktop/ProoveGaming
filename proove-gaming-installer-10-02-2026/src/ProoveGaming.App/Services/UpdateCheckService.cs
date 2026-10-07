using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using ProoveGaming.App.Models;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ProoveGaming.App.Services;

// Client-side "new version available" check (docs/plans/contact-us-and-update-check): a DI
// singleton started once from App.OnFrameworkInitializationCompleted, polling the manifest at
// ApiSettings.AppUpdateManifestUrl for the whole process lifetime — tray residency included,
// which is why the loop deliberately does NOT live in HomeViewModel.Start()/Stop(). Every
// failure mode is a silent Debug-logged skip; the user only ever sees the announcement.
//
// Shared-state invariant: LatestKnownUpdate and _lastAnnouncedVersion are written only from
// the single poll loop and read after a Dispatcher.UIThread.Post — single-writer, no locking.
public sealed class UpdateCheckService : IDisposable
{
    // Off the startup path first (30 s), then a cadence that still catches a release within
    // the multi-day tray residencies AppResilience documents.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    // A manifest is a few hundred bytes; the cap turns a misconfigured URL pointing at a
    // large file into the ordinary Debug-logged skip instead of a 6-hourly memory spike
    // (review R1 S1).
    private const long MaxManifestBytes = 64 * 1024;

    private readonly HttpClient _http = new()
    {
        Timeout = RequestTimeout,
        MaxResponseContentBufferSize = MaxManifestBytes,
    };
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger<UpdateCheckService> _logger;
    private readonly string? _manifestUrl;
    private readonly Version? _runningVersion;
    private Task? _pollTask;
    private string? _lastAnnouncedVersion;

    /// <summary>The newest update manifest seen this run, for subscribers that attach late.</summary>
    public AppUpdateInfo? LatestKnownUpdate { get; private set; }

    /// <summary>Raised via <see cref="Dispatcher.UIThread"/> — handlers run with UI affinity.</summary>
    public event Action<AppUpdateInfo>? UpdateAvailable;

    public UpdateCheckService(ConfigurationManager config, ILogger<UpdateCheckService> logger)
    {
        _logger = logger;
        _manifestUrl = config.ApiSettings.AppUpdateManifestUrl;
        // Captured once: the running version cannot change within a process.
        _runningVersion = typeof(UpdateCheckService).Assembly.GetName().Version;
    }

    /// <summary>Launches the background poll loop; a no-op when the feature is disabled.</summary>
    public void Start()
    {
        if (string.IsNullOrWhiteSpace(_manifestUrl))
        {
            _logger.LogDebug("Update check disabled: AppUpdateManifestUrl is not configured.");
            return;
        }
        if (_pollTask != null)
            return;

        _logger.LogDebug("Update check started against {ManifestUrl}.", _manifestUrl);
        var url = _manifestUrl;
        _pollTask = Task.Run(() => PollLoopAsync(url, _cts.Token));
    }

    private async Task PollLoopAsync(string url, CancellationToken ct)
    {
        try
        {
            await Task.Delay(InitialDelay, ct);
            await CheckOnceAsync(url, ct);

            using var timer = new PeriodicTimer(PollInterval);
            while (await timer.WaitForNextTickAsync(ct))
                await CheckOnceAsync(url, ct);
        }
        catch (OperationCanceledException)
        {
            // Dispose() — normal shutdown.
        }
    }

    private async Task CheckOnceAsync(string url, CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync(url, ct);
            if (TryParseManifest(json) is not { } manifest)
            {
                _logger.LogDebug("Update manifest at {ManifestUrl} is malformed; skipping.", url);
                return;
            }
            if (manifest.Version is not { } manifestVersion
                || !IsNewerVersion(_runningVersion, manifestVersion))
                return;

            LatestKnownUpdate = manifest;
            if (!ShouldAnnounce(_lastAnnouncedVersion, manifestVersion))
                return;

            _lastAnnouncedVersion = manifestVersion;
            _logger.LogDebug(
                "Update {Version} available (running {Running}).", manifestVersion, _runningVersion);
            Dispatcher.UIThread.Post(() => UpdateAvailable?.Invoke(manifest));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — let the loop end; an HttpClient timeout lands in the catch below.
            throw;
        }
        catch (Exception ex)
        {
            // Unreachable host, DNS, TLS, timeout, non-2xx, bad payload: silently skip and
            // let the next scheduled poll try again. Never a user-visible error.
            _logger.LogDebug(ex, "Update check against {ManifestUrl} failed; skipping.", url);
        }
    }

    /// <summary>
    /// Parses the manifest JSON; null when the body is not JSON, lacks the required
    /// version / download_url fields, or download_url is not an absolute https URL. Unknown extra
    /// fields are ignored. Static so tests
    /// drive the rule directly (the repo's decision-rule pattern).
    /// </summary>
    internal static AppUpdateInfo? TryParseManifest(string json)
    {
        AppUpdateInfo? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<AppUpdateInfo>(json);
        }
        catch (JsonException)
        {
            return null;
        }

        return manifest != null
            && !string.IsNullOrWhiteSpace(manifest.Version)
            && IsHttpsUrl(manifest.DownloadUrl)
            ? manifest
            : null;
    }

    // An absolute https URL only. Checked here rather than where the link is opened, so no
    // announcement ever carries a link that could not safely be opened
    // (docs/plans/fix-repository-audit-findings, A11).
    private static bool IsHttpsUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>
    /// True only when the manifest version parses (System.Version forms) and is strictly
    /// higher than the running version; unparseable or missing versions are never "newer".
    /// </summary>
    internal static bool IsNewerVersion(Version? running, string? manifestVersion) =>
        running is not null
        && Version.TryParse(manifestVersion, out var candidate)
        && candidate > running;

    /// <summary>
    /// Announce a given version string at most once per process run; a restart re-announces
    /// once, which is the desired periodic reminder.
    /// </summary>
    internal static bool ShouldAnnounce(string? lastAnnouncedVersion, string manifestVersion) =>
        !string.Equals(lastAnnouncedVersion, manifestVersion, StringComparison.Ordinal);

    // Cancels the loop rather than waiting on it — shutdown must not hang.
    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
        _http.Dispose();
    }
}
