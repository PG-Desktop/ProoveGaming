# Contact Us wiring + app update check

> **Note (2026-09-24):** `appsettings.json` is now compiled into the App and DeviceHost assemblies and no longer shipped as a file, so an installed build has no settings file to edit. Steps below that edit `appsettings.json` next to the EXE now mean: change `src/ProoveGaming.App/appsettings.json` in the repository and rebuild (see `docs/plans/fix-repository-audit-findings`, 2026-09-24 notes).

> Written 2026-08-21 from items 2 ("Contact Us — shipped UI silently discards messages") and
> 4 ("App self-update — no version-check endpoint") of
> [docs/audits/2026-08-21-api-needs.md](../../audits/2026-08-21-api-needs.md). Both features
> are client-side only: the backend routes are out of repo scope, so this plan documents the
> request/response contracts the client expects and is designed to ship **before** those
> routes exist. No new NuGet packages. Neither feature may block the UI thread.

> **2026-08-25:** API v2.0 renamed the route to `api/contact`, and the
> `AcceptedRouteMissing` degraded mode this plan introduced is retired by
> [../add-api-v2-sync/add-api-v2-sync-plan.md](../add-api-v2-sync/add-api-v2-sync-plan.md):
> `ContactUsOutcome` is now two-way (`Delivered`/`Failed`), the `ContactUs_Sent*` "accepted"
> strings are removed, and a 404 or malformed 2xx shows the failure line. The acceptance
> criteria and test-strategy mapping below have been corrected to the live behaviour; the
> Goal/Scope/Options prose is left as the historical design record.

## Goal

Two changes. (1) Wire the shipped Contact Us dialog to the already-coded
`IAuthApiService.ContactUsAsync` (POST `api/contact_us`) so messages actually leave the
machine, in a way that degrades gracefully while the backend route is still missing: a 2xx
gets honest "sent" wording, a missing route keeps today's "accepted" behaviour, and a
transient failure is shown to the user instead of silently dropping the message. (2) Add a
minimal client-side update check: a configurable manifest URL in `appsettings.json`, polled
at startup and periodically by the tray-resident App, surfacing "new version available"
through the existing notification paths (bell + toast + Windows system notification). No
auto-download or auto-install.

## Scope

**In scope**

- `SendContactUsAsync` calls the real endpoint with a `CancellationToken` (the file's own
  TODO), keeps the 4-frame pixel loader animating for the duration of the request, and
  branches on outcome (delivered / route-missing / failed).
- HTTP status + payload-shape discrimination in `AuthApiService.ReadResponseAsync` so the
  ViewModel can tell "route not deployed yet" from "real failure".
- New `UpdateCheckService` (App project): background poll of a JSON manifest, version
  comparison against the running assembly version, one announcement per new version per
  process run, silent skip on any error.
- New `ApiSettings.AppUpdateManifestUrl` config key (empty/absent = feature disabled).
- An explicit `<Version>` on `ProoveGaming.App.csproj` so the running assembly version is
  intentional rather than the 1.0.0.0 SDK default.
- en/uk resource strings for all new user-facing text.
- xUnit tests for every new decision rule (see Test strategy).
- Documentation upkeep: dated note in the existing
  [add-contact-us-dialog plan](../add-contact-us-dialog/add-contact-us-dialog-plan.md),
  new terms in `docs/CONTEXT.md`.

**Out of scope**

- The backend `api/contact_us` route and the hosted update manifest (contracts documented
  below; server work happens elsewhere).
- Auto-download / auto-install of updates; any installer or uninstaller change.
- A clickable "download now" affordance in the bell (the notification card has no action
  slot today — adding one is UI scope creep; the notification text carries the version and
  points at the website; recorded as a follow-up candidate).
- Offline queue-and-retry for Contact Us messages (rejected — see Options considered).
- Firmware update (audit item 3) and every other audit item.

## Expected server contracts (documented, not implemented here)

### POST `api/contact_us` (already coded client-side, unchanged)

Request (JSON body; `Authorization: Bearer <token>` attached only when a session token
exists — guests post anonymously):

```json
{ "email": "user@host.com", "topic": "string ≤100", "message": "string ≤2000" }
```

Response: the standard `ApiResponse` envelope — `{"success": true}` on acceptance;
`{"success": false, "message": "...", "error_code": "...", "errors": [...]}` on validation
failure. Any 2xx with `success: true` is treated as delivered.

### GET update manifest (new; a static JSON file behind the site suffices)

The URL is whatever `ApiSettings.AppUpdateManifestUrl` points at. Response body
(snake_case, matching the rest of the backend API):

```json
{
  "version": "1.1.0",
  "download_url": "https://proovegaming.com/download",
  "release_notes": "Optional free text shown nowhere yet; reserved."
}
```

- `version` — required; must parse with `System.Version.TryParse` (`major.minor[.build[.revision]]`).
- `download_url` — required; where the user can get the new build (carried in
  `AppUpdateInfo`, currently only reserved for a future clickable affordance).
- `release_notes` — optional; tolerated missing.

Unknown extra fields are ignored. Anything that fails to parse (non-JSON, missing/bad
`version`) is a malformed payload → the check is skipped silently.

## Options considered

### Contact Us degradation while the route is missing

1. **Graceful degradation (recommended):** always call the endpoint; on a 2xx `success`
   show new honest "sent" wording; on "route missing" (HTTP 404, or a 2xx whose body is not
   the JSON envelope) fall back to exactly today's shipped behaviour — dialog closes,
   "accepted" toast + bell entry; on transient failure (network error, timeout, 5xx, or a
   well-formed `success: false` body) keep the dialog open with the draft intact and show
   the in-dialog error (`ContactUs_SendFailed`, already wired in `MainWindow.axaml`).
   *Trade-off:* a message that hits the missing route is still lost — but that is the
   status quo, the wording still only claims acceptance, and the moment the backend deploys
   the route, users get real delivery with zero client update.
2. **Queue-and-retry:** persist failed/unroutable messages in SQLite and retry in the
   background. *Rejected:* it silently promises future delivery the client cannot
   guarantee (no server ack, no queue UI, messages rot invisibly if the route never ships
   or validation rejects them), adds schema + retry-loop complexity, and contradicts the
   PR #118 M1 product decision that wording must never overstate what happened.

Why "route missing" includes a 2xx with a non-envelope body: `UsersApiBaseUrl` is a PHP
query-router (`index.php?route=`); an unknown route on such a backend commonly answers 200
with an HTML page rather than a clean 404. Both shapes therefore map to the degraded path.
The residual risk (a genuine server bug returning HTML gets "accepted" wording) is accepted
and noted in Risks; it is strictly no worse than today, where every send gets "accepted".

> **Note (2026-08-23, PR #119 review m3):** "non-envelope body" now also covers *valid JSON*
> that is not the envelope. A 2xx JSON object without a `success` property (e.g.
> `{"error":"unknown route"}`) used to deserialize into `ApiResponse` with `Success=false`,
> `MalformedPayload=false` and read as a real refusal ("failed") instead of degrading.
> `ReadResponseAsync` now parses the 2xx body and sets `MalformedPayload` when the root is not
> a JSON object carrying `success`.

### Where the update poll lives

1. **Dedicated `UpdateCheckService` singleton started from `App` (recommended):**
   `HomeViewModel.Start()/Stop()` run on every Home load/unload *including
   minimise-to-tray* (the battery-poll timer is deliberately stopped there), so a timer
   inside HomeViewModel would stop exactly when the tray-resident poll matters most. A
   singleton service started once in `App.OnFrameworkInitializationCompleted` and disposed
   with the container polls for the whole process lifetime, tray included.
2. **Timer in `HomeViewModel.Start()`:** rejected for the reason above.
3. **Check-once-at-startup only:** rejected — the app sits in the tray for days (per
   AppResilience comments); a single check at launch would miss every release shipped
   during that residency.

### How "update available" reaches the user

1. **Bell entry + in-app toast + Windows system notification (recommended):** all three
   producers already exist (`AddNotification`, `NotificationService.Show`,
   `SystemNotificationService.Show`) and this is exactly the low-battery pattern
   (`HomeViewModel` line ~1908). The system notification covers the tray-resident case
   where nobody sees the in-app surfaces.
2. **A dedicated banner/dialog:** rejected — new UI for v1 of a notice-only feature;
   the notification center is the audit's own suggested channel.

## Recommended approach

### Part 1 — Contact Us wiring

- **`IAuthApiService.ContactUsAsync`** gains a `CancellationToken ct = default` parameter;
  `AuthApiService.PostAsync`/`ReadResponseAsync` flow it into `SendAsync` and
  `ReadFromJsonAsync`. (Optional parameter → no other call site changes.)
- **`ApiResponse`** gains two `[JsonIgnore]` transport-diagnostic properties set by
  `ReadResponseAsync`: `int StatusCode` (0 when the request never completed) and
  `bool MalformedPayload` (body failed to deserialize into the envelope). Existing callers
  are unaffected — both are additive and ignored by serialization.
- **`HomeViewModel`**:
  - New `internal enum ContactUsOutcome { Delivered, AcceptedRouteMissing, Failed }` and
    the static decision rule (same pattern as `ContactUsFormValid` / `ContactUsCanCancel`
    so tests drive it directly):
    - `Success == true` → `Delivered`;
    - `StatusCode == 404`, or 2xx `StatusCode` with `MalformedPayload` → `AcceptedRouteMissing`;
    - everything else (`IsNetworkError`, timeout, 5xx, well-formed `success:false`) → `Failed`.
  - `SendContactUsAsync(CancellationToken ct)`: create a CTS linked to the command token
    with a **15 s timeout** (bounds the inert-dialog window; the timeout surfaces through
    the existing `TaskCanceledException` → `IsNetworkError` path in `AuthApiService`);
    resolve `IAuthApiService` via the file's existing `_services.GetRequiredService`
    pattern; pass `_user.SessionToken` (null when empty — guest); send the **trimmed**
    topic/message. Replace the fixed 2 s `Task.Delay` loop with: start the request task,
    then loop the 4 spinner frames (250 ms each) until the task completes, with a minimum
    of one full revolution (1 s) so a fast response doesn't flash. The command is created
    with the `AsyncRelayCommand` cancellable-delegate overload so the token plumbing is
    real, even though nothing cancels it interactively today (the dialog stays inert
    during send per review R1 S2 — unchanged).
  - Outcome handling: `Delivered` → close/clear + new `ContactUs_DeliveredToast` toast +
    `ContactUs_DeliveredTitle` bell entry (same `ContactUsSentIcon`);
    `AcceptedRouteMissing` → identical to today (close/clear + existing
    `ContactUs_SentToast`/`ContactUs_SentTitle` "accepted" wording); `Failed` → dialog
    stays open, draft intact, `ContactUsError = ContactUs_SendFailed` (view already
    renders it, `MainWindow.axaml` ~797). `OnContactUsFieldChanged` additionally clears
    `ContactUsError` on the next edit so a stale error doesn't linger (matches the
    dialog's existing error-reset-on-open behaviour).
- **Strings:** new keys `ContactUs_DeliveredTitle` ("Message sent" / «Повідомлення
  надіслано») and `ContactUs_DeliveredToast` ("Your message has been sent" / «Ваше
  повідомлення надіслано») in both `Strings.en.axaml` and `Strings.uk.axaml`. The existing
  accepted-wording keys stay for the degraded path; `ContactUs_SendFailed` already exists
  in both languages. No layout/width changes (all strings land in existing auto-sizing
  toast/bell/error surfaces).

### Part 2 — App update check

- **Config:** `ApiSettings` gains `public string? AppUpdateManifestUrl { get; set; }`
  (nullable, *not* `required`, so existing configs bind unchanged); `appsettings.json`
  gains `"AppUpdateManifestUrl": ""` — empty string (or absent) disables the feature.
- **`Models/AppUpdateInfo.cs`:** `sealed class AppUpdateInfo` with `Version`
  (`[JsonPropertyName("version")]`), `DownloadUrl` (`download_url`), `ReleaseNotes`
  (`release_notes`).
- **`Services/UpdateCheckService.cs`:** `sealed class UpdateCheckService : IDisposable`,
  DI singleton, own `HttpClient` (15 s `Timeout`), injected `ConfigurationManager` +
  `ILogger<UpdateCheckService>`.
  - `Start()` — no-op when `AppUpdateManifestUrl` is null/whitespace; otherwise launches
    one background loop (`Task.Run`): initial delay **30 s** after startup (stay off the
    startup path), then a check every **6 h** (`PeriodicTimer`), until the CTS is
    cancelled by `Dispose()` (container disposal on exit also disposes the `HttpClient`).
  - Each check: `GetStringAsync(url, ct)` → static `TryParseManifest(json)` → static
    `IsNewerVersion(runningVersion, manifest.Version)` using `System.Version.TryParse`
    (unparseable → not newer) against
    `typeof(UpdateCheckService).Assembly.GetName().Version` — captured once at
    construction. Any exception (unreachable host, DNS, TLS, timeout, non-2xx, bad JSON)
    → `LogDebug` and skip; **never** a user-visible error.
  - Announcement dedupe: static `ShouldAnnounce(lastAnnouncedVersion, manifestVersion)` —
    announce a given version string at most once per process run (a restart re-announces
    once, which is the desired periodic reminder).
  - On a new version: cache `LatestKnownUpdate` and raise
    `event Action<AppUpdateInfo>? UpdateAvailable` **via `Dispatcher.UIThread.Post`** so
    subscribers run with UI affinity.
- **Wiring:** register in `BuildServices()`; in `OnFrameworkInitializationCompleted`, call
  `Start()` after `MainWindow` is resolved (so `HomeViewModel`, a `MainWindow` ctor
  dependency, is already subscribed — no missed first event; `LatestKnownUpdate` is
  checked at subscription time anyway as a belt-and-braces).
- **`HomeViewModel`:** inject nothing new — resolve `UpdateCheckService` via `_services`
  in the constructor, subscribe `UpdateAvailable` (both singletons live for the process —
  no unsubscribe needed, same as the `LanguageChanged` pattern), and on fire (already on
  the UI thread): `AddNotification(AppUpdate_AvailableTitle, body, AppUpdateIcon)` (bell,
  persisted per owner like every other notification), `NotificationService.Show(body)`
  (in-app toast), `SystemNotificationService.Show(title, body)` (Windows toast — the
  tray-resident surface, mirroring low battery). `AppUpdateIcon` is a new inline
  `NotificationIcon` path constant (download-arrow glyph), like `ContactUsSentIcon`.
- **Version identity:** add `<Version>1.0.0</Version>` to
  `src/ProoveGaming.App/ProoveGaming.App.csproj`. ⚠ Release-process implication: from now
  on the manifest's `version` is compared against this value, and it must be bumped
  manually when publishing a build the manifest will advertise (the date-tagged installer
  release flow itself is unchanged).
- **Strings:** `AppUpdate_AvailableTitle` ("Update available" / «Доступне оновлення») and
  `AppUpdate_AvailableBody_Format` ("Version {0} is available — get it at
  proovegaming.com" / «Доступна версія {0} — завантажте на proovegaming.com»), formatted
  with `string.Format` like `HomeView_Greeting_Format`. Notification/toast surfaces
  auto-size; no fixed widths anywhere.

## File-level change list

Ordered so tests for each rule land with the rule (new-feature strategy).

1. `src/ProoveGaming.App/Services/ApiResponse.cs` — add `[JsonIgnore] int StatusCode` and
   `[JsonIgnore] bool MalformedPayload`.
2. `src/ProoveGaming.App/Services/IAuthApiService.cs` — `ContactUsAsync(..., CancellationToken ct = default)`.
3. `src/ProoveGaming.App/Services/AuthApiService.cs` — flow the token through
   `PostAsync`/`SendAsync`/`ReadFromJsonAsync`; populate `StatusCode`/`MalformedPayload`
   in `ReadResponseAsync`.
4. `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` — real send (outcome enum + static
   `DecideContactUsOutcome`, spinner-while-awaiting with 1 s minimum, 15 s linked-timeout
   CTS, three outcome branches, error cleared on next edit); `UpdateAvailable`
   subscription + bell/toast/system notification + `AppUpdateIcon` constant.
5. `src/ProoveGaming.App/ApiSettings.cs` — nullable `AppUpdateManifestUrl`.
6. `src/ProoveGaming.App/appsettings.json` — `"AppUpdateManifestUrl": ""`.
7. `src/ProoveGaming.App/Models/AppUpdateInfo.cs` — **new**: manifest DTO (snake_case
   `JsonPropertyName`s).
8. `src/ProoveGaming.App/Services/UpdateCheckService.cs` — **new**: background poll loop,
   static `TryParseManifest` / `IsNewerVersion` / `ShouldAnnounce`, UI-thread event,
   `IDisposable`.
9. `src/ProoveGaming.App/App.axaml.cs` — register `UpdateCheckService` in
   `BuildServices()`; `Start()` it after `MainWindow` resolution.
10. `src/ProoveGaming.App/ProoveGaming.App.csproj` — `<Version>1.0.0</Version>`.
11. `src/ProoveGaming.App/Resources/Strings.en.axaml` / `Strings.uk.axaml` — the four new
    keys (`ContactUs_Delivered*`, `AppUpdate_*`).
12. `tests/ProoveGaming.App.Tests/HomeViewModelContactUsTests.cs` — extend with
    `DecideContactUsOutcome` cases (see Test strategy).
13. `tests/ProoveGaming.App.Tests/UpdateCheckServiceTests.cs` — **new**: manifest parsing,
    version comparison, announce dedupe.
14. `docs/plans/add-contact-us-dialog/add-contact-us-dialog-plan.md` — dated note in the
    Behaviour section: the interim no-API send is superseded by this plan (Review findings
    journal untouched).
15. `docs/CONTEXT.md` — one-line definitions: `UpdateCheckService`, `AppUpdateInfo`,
    update manifest, `AppUpdateManifestUrl`, `ContactUsOutcome`.

No changes to: `MainWindow.axaml` (error surface already bound), Installer/Uninstaller,
DeviceHost, gRPC contracts, `Directory.Build.props`, `.slnx`. No new NuGet packages.

## Acceptance criteria

Contact Us:

- [ ] Given a backend that answers `api/contact` with 2xx `{"success":true}`, when the
  user presses SEND with a valid form, then exactly one POST with `{email, topic, message}`
  (topic/message trimmed) is issued — with a Bearer token when signed in, without one as
  guest — the dialog closes, and the toast/bell wording is the "sent" text
  (`ContactUs_Delivered*`).
- [ ] Given a 404, or a 2xx whose body does not deserialize into the `ApiResponse`
  envelope (e.g. an HTML page), when the user presses SEND, then it is treated as a
  failure like any other (2026-08-25: the former "accepted" degraded mode is retired —
  see the note at the top).
- [ ] Given a transient failure (no network, DNS failure, timeout, 5xx, or a well-formed
  `success:false` body), when the user presses SEND, then the dialog **stays open** with
  the draft intact and the red failure line appears in the dialog; pressing
  SEND again retries. No toast, no bell entry, and the message is never claimed accepted.
  **Updated 2026-08-27** (`docs/plans/improve-api-error-surfacing/`): the line's wording is
  now chosen per `error_code` by `ContactUsErrors.ResolveKey` — the anti-spam refusals get
  their own messages and `ContactUs_SendFailed` is the generic fallback — and the line is
  rendered SemiBold so the emphasis is not colour-only.
- [ ] Given any send, the pixel loader animates from the moment SEND is pressed until the
  request completes (minimum 1 s, i.e. one full 4-frame revolution), and the whole dialog
  stays inert for that duration (existing `ContactUsCanCancel` gate unchanged); a hung
  server stops spinning within ~15 s via the timeout → failure branch.
- [ ] Given the send fails and the error line is visible, when the user edits any field,
  then the error line clears.
- [ ] During a send, the UI thread never blocks: typing in another window area, moving the
  window, and the spinner animation all stay fluid (no sync-over-async anywhere in the path).

Update check:

- [ ] Given `AppUpdateManifestUrl` is empty or absent, when the app runs, then no update
  HTTP request is ever issued and nothing update-related is logged above Debug.
- [ ] Given the URL points at a manifest whose `version` parses higher than the running
  assembly version, when the app has been running ~30 s, then one bell entry
  ("Update available" + "Version X…"), one in-app toast, and one Windows system
  notification appear; the bell entry persists into the owner's stored history like any
  other notification.
- [ ] Given the same manifest is served unchanged, when subsequent 6 h polls run, then no
  second announcement is made in the same process run; after an app restart the version is
  announced once again.
- [ ] Given the manifest version equals or is lower than the running version, or fails
  `Version.TryParse`, then nothing is announced.
- [ ] Given the endpoint is unreachable (offline, DNS failure, 404/500, TLS error) or the
  body is not JSON, then the check skips silently — no user-visible surface, no crash, no
  unobserved-task exception (AppResilience stays quiet), and the next scheduled poll still
  happens.
- [ ] Given the app is minimised to tray, the poll keeps running (it does not live in
  `HomeViewModel.Start/Stop`), and an update found while in the tray still produces the
  Windows system notification.
- [ ] Given app exit, `UpdateCheckService.Dispose` cancels the loop and disposes its
  `HttpClient`; shutdown does not hang.
- [ ] With the app language set to uk, all new strings render from `Strings.uk.axaml`; no
  control shows a clipped/flush uk string (all new text is in auto-sizing surfaces; no
  fixed `Width` introduced anywhere).

> **Note (2026-08-27, `fix-owner-scoped-state-persistence`):** the claim above — that the
> update-available bell entry "persists into the owner's stored history like any other" — is now
> true on the Welcome-screen launch path too, where it previously was not. `HomeView.OnLoaded`
> starts this VM before sign-in, so an announcement raised while no owner existed was memory-only
> and was then cleared from the bell when the owner resolved. Such entries are now flushed to the
> first owner that resolves (see [[Pre-owner notification flush]]). Unchanged for every launch that
> already had an owner — remembered auth or a restored guest session — which is the path this
> plan's criteria were written against. See `docs/plans/fix-owner-scoped-state-persistence/`.
## Test strategy

New-feature change (`add-*`-kind): the tests below are non-negotiable coverage, all in
`tests/ProoveGaming.App.Tests` (xUnit v2), mirroring the repo's static-decision-rule style.
`tests/ProoveGaming.Infrastructure.Tests` is untouched (nothing infrastructural changes).

**`HomeViewModelContactUsTests.cs` (extend; existing cases must stay green unchanged):**

- `DecideContactUsOutcome` (mapping corrected 2026-08-25, add-api-v2-sync):
  - 2xx + `Success=true` → `Delivered`.
  - `StatusCode=404` (any body) → `Failed`.
  - 2xx + `MalformedPayload=true` → `Failed` (the PHP-router HTML case).
  - `IsNetworkError=true` (network error and timeout are the same flag) → `Failed`.
  - `StatusCode=500` → `Failed`.
  - 2xx + well-formed `Success=false` (validation reject) → `Failed`.

**`UpdateCheckServiceTests.cs` (new):**

- `TryParseManifest`: valid full manifest → all three fields; missing `release_notes` →
  still valid; missing/empty `version` → null; non-JSON ("<html>…") → null; unknown extra
  fields → ignored.
- `IsNewerVersion`: `1.0.0` vs `1.1.0` → true; equal → false; lower → false; two- vs
  four-part forms compare correctly (`1.0` vs `1.0.0.1`); unparseable manifest version
  ("latest", "v1.2", "") → false.
- `ShouldAnnounce`: first sighting → true; same version again → false; a different newer
  version after an announcement → true.

**Deliberately not covered by automated tests, and why:**

- The live HTTP paths (`AuthApiService` post, the poll loop): `AuthApiService` constructs
  its own `HttpClient` with no handler seam, and adding one is out of scope; both are
  exercised by QA steps instead.
- The instance seam of `SendContactUsAsync` (spinner timing, dialog close, toast/bell
  raising) and the `UpdateAvailable` subscription: they need the full service graph +
  Avalonia dispatcher — the same accepted manual-coverage boundary the previous Contact Us
  review recorded (its Round 2 m3 option-B ticket remains the escape hatch if this class
  of regression recurs).
- `PeriodicTimer` scheduling and `Dispose` semantics: timing-dependent; covered by QA and
  the acceptance criteria.

## QA (manual test steps)

Setup for steps 6–10: ask the developer for a test manifest URL, or host one locally —
create a file `manifest.json` containing
`{"version":"9.9.9","download_url":"https://proovegaming.com/download","release_notes":"test"}`,
serve the folder over HTTP on this machine, and put that URL into the
`AppUpdateManifestUrl` value inside `appsettings.json` next to the app EXE. The app's
current version is 1.0.0.

1. Launch the app (guest or signed in), open the profile/guest menu → Contact Us, fill a
   valid email, topic and message, press SEND. Expected today (backend route not deployed
   yet): the pixel loader spins at least ~1 s, the dialog closes, the green toast "Your
   message has been accepted" appears and the bell shows "Message accepted" with the
   topic — exactly as before this change.
2. Disconnect the network (unplug/disable Wi-Fi), reopen Contact Us, fill the form, press
   SEND. Expected: the loader spins briefly, the dialog STAYS OPEN, everything typed is
   still there, and a red line "Failed to send the message. Please try again." appears
   under the form. No toast, no bell entry.
3. Still in that dialog: type one character in any field. Expected: the red error line
   disappears. Reconnect the network and press SEND again. Expected: the send completes
   as in step 1.
4. During any send: try clicking CANCEL and the ✕ while the loader spins. Expected: the
   dialog does not close until the send finishes; the window itself stays responsive
   (can be dragged).
5. (Once the backend route is deployed) Repeat step 1. Expected: the wording changes to
   "Your message has been sent" / "Message sent", and the message actually arrives in the
   backend inbox. Repeat signed in and as guest — both must arrive.
6. With the test manifest configured (see Setup), launch the app and wait ~1 minute.
   Expected: a bell entry "Update available" with "Version 9.9.9…", a matching in-app
   toast, and a Windows notification. It appears once — waiting longer must not repeat it.
7. Close the window (app goes to tray) before the first minute is up on a fresh launch.
   Expected: the Windows notification still appears while the app sits in the tray.
8. Edit the manifest to `"version":"0.0.1"`, restart the app, wait ~1 minute. Expected:
   no update notice of any kind.
9. Break the manifest (replace the file's content with `hello`), restart, wait ~1 minute.
   Expected: no notice, no error dialog, app fully usable. Same when the URL points at a
   dead address.
10. Set `AppUpdateManifestUrl` back to `""`, restart. Expected: no update notice ever, and
    everything else works normally.
11. Switch the app language to Ukrainian (Settings) and repeat steps 1, 2 and 6.
    Expected: all new messages appear in Ukrainian and fit their surfaces without
    clipping.
12. Exit the app from the tray menu. Expected: it exits promptly (no hang).

## Review focus

1. `async/await` — no sync-over-async anywhere; the CancellationToken genuinely flows
   HttpClient-deep; the spinner loop awaits, never spins the dispatcher.
2. `UI-thread affinity` — `UpdateAvailable` marshalled via `Dispatcher.UIThread.Post`
   before touching `AddNotification`/toasts; nothing in the poll loop touches UI state.
3. `resource disposal` — `UpdateCheckService` owns its CTS + `HttpClient` with a clear
   disposal point; the background loop observes cancellation; no dangling subscriptions
   (singleton-to-singleton events documented as intentional).
4. `localization` — every new user-facing string in both en and uk resource files, no
   fixed `Width`, format strings via resources.

## Performance & quality

- **No hot paths.** One HTTP request per send click; one manifest GET every 6 hours. Both
  are I/O on background continuations.
- **Startup:** the first update check is delayed 30 s so it adds zero work to the startup
  path; `Start()` itself only launches a task.
- **UI thread:** the send awaits; the spinner is `Task.Delay`-paced property changes; the
  poll loop runs entirely off-thread and marshals only the final event. Nothing blocks.
- **Allocations:** negligible and event-driven (one DTO per poll); no per-frame or
  per-keystroke work added (`OnContactUsFieldChanged` gains one cheap null-check/clear).
- **Shared state:** `UpdateCheckService`'s `LatestKnownUpdate`/last-announced are written
  only from the single poll loop and read after a UI-thread `Post` — single-writer, no
  locking needed; document this invariant in the class.
- **Sync-over-async:** none introduced; `Dispose` cancels rather than waits on the loop.

## Risks

- **Route-missing heuristic can mask a real server bug** (a genuine 200-HTML error page
  reads as "accepted"). Accepted: identical to shipped behaviour today, and the window
  closes the day the route deploys. Revisit the heuristic (drop the malformed-2xx branch)
  once the backend confirms the route is live.
- **User-secrets/config drift:** `ConfigurationManager` layers user secrets over
  `appsettings.json`; a stale secret could silently enable/disable the update check on a
  dev machine. Mitigation: the feature logs (Debug) whether it started and with which URL.
- **Version discipline:** the check is only as truthful as the manually-bumped
  `<Version>` and the manifest. A forgotten bump means users are nagged about a version
  they already run (equal-version guard limits this to "manifest ahead of csproj").
  Called out in the release-path note; the manifest publisher owns consistency.
- **15 s worst-case inert dialog** on a black-holed connection. Bounded by the linked
  timeout; the spinner communicates activity. If QA finds it too long, the constant is one
  number.
- **Bell entry has no action** — users must go to the site themselves. Deliberate v1
  scope; the `download_url` is already carried end-to-end for the follow-up.
- **`HttpClient` DNS pinning** in a days-long process (singleton client caches
  connections): irrelevant in practice at one request per 6 h with default
  `PooledConnectionLifetime` behaviour on .NET 10's `SocketsHttpHandler`.

## Open questions

1. **Final manifest URL + hosting** — who publishes `manifest.json` (site vs. a GitHub
   raw/pages URL), and should `appsettings.json` ship with the real URL already filled in,
   or empty until the file exists? (Plan default: ship `""` = disabled; flip in the same
   PR that publishes the manifest.)
2. **Product sign-off** on the degraded-path wording: keeping "accepted" (not an error)
   while the route is missing is a product statement — confirm with the PR #118 M1
   decision owner.
3. **Version numbering policy** — who bumps `<Version>` and when (per installer-release
   run? per milestone?), given the release tags stay date-based.
4. **Poll cadence** — is 6 h acceptable, or should it be configurable in `ApiSettings`?
   (Plan default: a code constant; configurability is a one-liner later.)

## Review findings

### Round 1 (working tree vs 793f37d, 2026-08-22) — Ship: yes

#### Summary

Both features implemented as one uncommitted delta: Contact Us now really posts to
`api/contact_us` with a linked 15 s timeout and branches on `DecideContactUsOutcome`
(delivered / route-missing / failed), with additive `StatusCode`/`MalformedPayload`
diagnostics on `ApiResponse`; a new `UpdateCheckService` singleton polls the manifest
(30 s initial, 6 h cadence), compares versions, and announces once per version per run via
bell + toast + system notification. Build green; full suite 1790/1790.

#### Plan conformance

- Implemented as planned: all 15 file-level items, including both docs items (supersession
  note in add-contact-us-dialog plan, five CONTEXT.md terms) and the `<Version>1.0.0</Version>`
  csproj addition. All six `DecideContactUsOutcome` test cases and all three
  `UpdateCheckService` rule groups from Test strategy are present and green.
- Deviates from plan (benign, surfaced): the plan claimed the optional `CancellationToken`
  parameter meant "no other call site changes", but interface *implementations* must still
  declare it — five test fakes were updated
  (`tests/ProoveGaming.App.Tests/ProfileViewModel*Tests.cs`,
  `WelcomeViewModelForgotPasswordTests.cs`). Necessary, minimal, in scope.
- Accepted limitation (per plan): a genuine 200-with-HTML server bug reads as "accepted"
  — residual risk stands as documented in Risks; also an IO failure while reading a 2xx
  body maps to the same degraded branch, which is safe because the server did receive the
  message in that scenario.
- Accepted limitation (per plan): live HTTP paths and the `SendContactUsAsync` instance
  seam (spinner timing, dialog close, toast raising) are manual-QA coverage — unchanged
  boundary from the previous Contact Us review.

#### Code

- none (no Blockers)
- [x] **S1. Cap the manifest response size** (closed 2026-08-22: `MaxResponseContentBufferSize` = 64 KB; the `Version!` bang nit was also dropped via a pattern match) — `src/ProoveGaming.App/Services/UpdateCheckService.cs:87` — Should-fix
  **Problem / why it matters:** `GetStringAsync` buffers the whole response. If
  `AppUpdateManifestUrl` is ever misconfigured to point at a large file (an installer zip is
  the realistic mistake), every 6 h poll buffers it fully into memory in a days-long
  tray-resident process.
  **Fix:** set `MaxResponseContentBufferSize` (e.g. 64 KB) on the service's `HttpClient` —
  a manifest is a few hundred bytes; oversize then fails into the existing Debug-skip path.

#### Tests

- none — plan's Test strategy fully covered (30 new cases, all green; suite total
  1790 passed / 0 failed). Remaining uncovered surface is the plan's own accepted
  manual-QA boundary.

#### Nits

- `manifest.Version!` null-forgiveness — `UpdateCheckService.cs:97`; guarded by
  `TryParseManifest`/`IsNewerVersion` immediately above, but a pattern extraction would
  drop the bang.
- `ShouldAnnounce` keeps only the last announced version, so a manifest oscillating
  1.1.0 → 1.2.0 → 1.1.0 within one run would re-announce 1.1.0 — matches the plan's own
  rule signature; only a server anomaly triggers it.
- The service's `HttpClient` is allocated even when the feature is disabled (empty URL);
  idle and disposed on exit — negligible.
- Belt-and-braces `LatestKnownUpdate` check in the `HomeViewModel` ctor could double-announce
  if a future refactor made the VM construct *after* `Start()` with a Post in flight —
  impossible today and called out in the ctor comment; keep the ordering note in
  `App.axaml.cs` intact.

#### What's done well

- The decision rules are statics with direct tests, exactly matching the repo's
  `ContactUsFormValid`/`LowBatteryDecision` pattern — no service-graph gymnastics.
- Cancellation is threaded HttpClient-deep (`SendAsync(request, ct)`,
  `ReadFromJsonAsync(ct)`), with `OperationCanceledException` deliberately rethrown out of
  `ReadResponseAsync` so a timeout surfaces as `IsNetworkError` → Failed rather than being
  mislabelled a malformed 2xx.
- UI-thread affinity is airtight: the poll loop touches no UI state and marshals the single
  event via `Dispatcher.UIThread.Post`; `Dispose` cancels rather than waits.
- Localization complete in both languages, all strings on auto-sizing surfaces, no `Width`
  anywhere in the diff; the stale "no backend yet" comments and the shipped-behaviour
  wording notes were updated alongside the code, and the prior plan carries a dated
  supersession note without touching its review journal.

#### Verdict

Ship: yes — no blockers; S1 is a one-line hardening that can ride the fix pass. First:
1) S1 response-size cap; 2) (optional) drop the `Version!` bang via pattern match.
