# add-api-v2-sync — Adapt the App to Launcher API v2.0

> **Note (2026-09-24):** the sign-in, registration, sign-out and guest-expiry logic this plan places in `MainWindow.axaml.cs` now lives in `src/ProoveGaming.App/Services/AuthSessionCoordinator.cs` ([`refactor-auth-session-flow`](../refactor-auth-session-flow/refactor-auth-session-flow-plan.md)). Same statements, same order; `MainWindow` keeps the navigation. Name map: `OnSignInSucceeded` → `CompleteSignInStateAsync` + `FinishSignInAsync`; `OnNicknameChosen` → `RegisterAsync` + `FinishRegistrationAsync`; `OnLogoutRequested` → `LogoutAsync`; `SignOutToFreshSignIn` → `SignOut`; `OnTokenRejected`'s check → `IsCurrentToken`; `OnSessionExpired` → `HandleGuestSessionExpired`; `OnGuestContinueRequested` → `StartGuestSession`; `ClearSignedInState`, `PersistAuthIfTokenPresent` and `EnqueueStorageWrite` moved as they were.

Date: 2026-08-25. Source contract: "Proove Gaming Launcher API v2.0" (docx delivered 2026-08-25,
file name "API 1.8_.docx"; the English translation must land as
`docs/knowledge-base/proove-gaming-launcher-api-v2.0-en.md` as part of this change — see
File-level change list). Review of the docx against the codebase was done in-session on
2026-08-25; the gaps found there drive the Open questions below.

## Goal

Adopt the three client-side consequences of API v2.0: (1) the Contact Us route is now
`api/contact` (the app ships `api/contact_us` plus a degraded "route not deployed" mode that
must retire); (2) server-side sync of presets and macros requires each record to carry a `uid`
and echo the server's `updated_at`, which the local SQLite rows do not have; (3) the
`ISettingsSyncService.OnGuestUpgradedAsync` no-op stub becomes a real one-shot push of the
freshly migrated guest data (macros → presets → preferences → device state) to the new
account. This makes a newly registered account's cloud state match what the user built as a
guest, which is the only guest↔server touchpoint the product defines today.

## Scope

**In:**
- Rename the Contact Us route to `api/contact`; remove the `AcceptedRouteMissing` outcome and
  its "accepted" wording (en/uk).
- Schema migration: `uid` + `server_updated_at` columns on `presets` and `macros`, backfilled
  for existing rows, populated on insert for new rows.
- New `ISyncApiService`/`SyncApiService` HTTP client for `presets/create`, `macros/create`,
  `preferences/update`, `devices/update` (only what the push needs).
- Shared HTTP envelope helper extracted from `AuthApiService` so the two API clients do not
  duplicate `PostAsync`/`ReadResponseAsync`.
- Real `SettingsSyncService` implementing the one-shot upgrade push, best-effort and
  non-blocking for the sign-up UI.
- English translation of the v2.0 docx into `docs/knowledge-base/`; reconciliation notes in
  the plans this change invalidates; `docs/CONTEXT.md` glossary entries.

**Out (deliberately):**
- Ongoing two-way sync (pull on sign-in, conflict resolution via `updated_at`, `presets/list`,
  `presets/update`, `macros/update`, deletes). This plan only lays the columns and the API
  client that a future sync feature will reuse.
  *(2026-08-25: the pull on sign-in now exists — see `docs/plans/add-api-v2-pull/`, phase 1
  of `docs/plans/add-api-v2-conformance/`. Conflict handling, updates and deletes now exist
  too — see `docs/plans/add-api-v2-live-sync/`, phase 2. That phase also closes this push's
  two accepted gaps: an **interrupted push** now converges via the coordinator's reconcile
  pass, which enumerates rows with `server_updated_at IS NULL`, and **macros under device
  types without a device card** are pushed because the pass enumerates dirty/unsynced rows
  rather than walking device cards.)*
- Persisting the server's preferences `updated_at` (there is no natural row for it yet; the
  future sync plan will add a `sync_meta` store when it actually needs conflict handling).
- Any UI. The push is invisible except for log lines.
- `profile/notifications` / `notifications_update` (no UI consumes them).
- Migration of guest data into an *existing* account on sign-in — deliberately absent today
  (`MainWindow.OnSignInSucceeded` comment) and stays absent.

## Options considered

### A. Push synchronously inside `OnGuestUpgradedAsync`, caller awaits end-to-end
`MainWindow.OnNicknameChosen` already awaits the stub, so the simplest implementation keeps
that contract and performs N HTTP calls inline.
- Pros: trivial ordering guarantees; test awaits the same task the app does.
- Cons: sign-up completion (UI transition to Home) blocks on up to dozens of sequential HTTP
  round-trips against a slow server; a 15 s timeout × N requests is an unacceptable stall in
  the sign-up flow. Violates the "never freeze the app" rule in spirit even though the await
  is async — the user stares at a stuck wizard.

### B. `OnGuestUpgradedAsync` starts a background push and returns immediately (recommended)
The method reads nothing before returning: it captures the customer id, schedules the push on
the thread pool, records the task in an internal property (`PushTask`) and completes. The
push reads local rows through the storage executor (`RunOnStorageAsync`), calls the API
sequentially, and writes back `server_updated_at` through the executor. Failures are logged,
never thrown, never surfaced in UI.
- Pros: sign-up UI transition is unaffected; FIFO executor ordering guarantees the push reads
  post-migration rows (it is scheduled after `MigrateGuestToUserAsync`'s executor item);
  tests await `PushTask` directly (same pattern as `HomeViewModel.NotificationsLoaded`).
- Cons: the push can lose the race against app exit (accepted: it is best-effort by design,
  and the next sync feature will reconcile); slightly more test plumbing.

### C. Defer preset `configuration` content, push name/metadata only
Because the macro-reference format inside `configuration` is not confirmed by the backend,
push presets with an empty `configuration` and let the future sync plan fill it.
- Pros: smallest possible surface, no risk of the server rejecting our JSON.
- Cons: produces server presets that are lies — a name with no content that a second machine
  would "restore" into an empty device. Worse than not pushing. Rejected; instead the
  configuration serializer is in scope and the macro-reference format is an explicit open
  question that blocks only the `key_macro_bindings` part of the payload.

**Decision: B**, with the full configuration payload (option C rejected), and the
`key_macro_bindings` section of the payload gated on Open question 2.

## Recommended approach

### Stage 1 — Contact route rename (independently shippable)

1. `AuthApiService.ContactUsAsync` posts to `"api/contact"` instead of `"api/contact_us"`.
2. `HomeViewModel`: delete the `AcceptedRouteMissing` member; `DecideContactUsOutcome`
   returns `Delivered` on `Success`, otherwise `Failed` (404 and malformed-2xx now fall into
   `Failed` — with the route documented and deployed, "the backend answered garbage" is a
   real failure the user must see, not something to euphemise). The send-completion branch
   loses the wording fork and always uses `ContactUs_DeliveredToast`/`ContactUs_DeliveredTitle`.
3. Strings: remove `ContactUs_SentTitle`/`ContactUs_SentToast` and the "degraded wording"
   comments from `Strings.en.axaml`/`Strings.uk.axaml`.
4. `MalformedPayload`/`StatusCode` plumbing in `ApiResponse`/`AuthApiService` **stays** — it
   is generic transport diagnostics, not contact-specific.

### Stage 2 — Local `uid` + `server_updated_at`

Schema (in `LocalStorageService`'s table-creation batch):
- Add a `ColumnExists(table, column)` helper next to `TableExists` (PRAGMA `table_info`).
- `presets`: `ALTER TABLE ... ADD COLUMN uid TEXT NOT NULL DEFAULT ''`,
  `ADD COLUMN server_updated_at TEXT NULL` when missing. Same two columns on `macros`.
- Backfill: `UPDATE presets SET uid = <new guid> WHERE uid = ''` row-by-row (SQLite cannot
  generate GUIDs; iterate ids and update each). Same for `macros`. Runs in the constructor
  migration section, like the `preset_settings` backfill.
- `CREATE UNIQUE INDEX IF NOT EXISTS idx_presets_uid ON presets(uid)` (and macros) **after**
  the backfill.

Semantics:
- `uid` is generated client-side (`Guid.NewGuid()`, `"D"` format — matches the API's UUID
  examples) at insert time (`InsertPreset`, `SaveMacro`) and never changes afterwards;
  `MigrateGuestToUserAsync` only rewrites owner columns, so uids survive the rekey untouched.
- `server_updated_at` is the server's `updated_at` echoed verbatim as an **opaque string**
  (the contract's format has no timezone, so it must never be parsed); `NULL` = never synced.
  Only sync code writes it.
- `PresetRecord` gains `Uid` + `ServerUpdatedAt`; `MacroRecord` likewise. Reads populate
  them; inserts take `Uid` from the record when non-empty (tests) or generate one.

### Stage 3 — Sync API client + real `SettingsSyncService`

**Shared envelope helper.** Extract `PostAsync<T>`/`ReadResponseAsync<T>` from
`AuthApiService` into an internal `ApiHttpClient` (App/Services) that owns the `HttpClient`
and base-URL; `AuthApiService` delegates to it (public surface and behaviour unchanged),
`SyncApiService` composes the same class. This is the CLAUDE.md second-copy rule, applied
before the copy exists.

**`ISyncApiService` / `SyncApiService`** (App/Services), all POST, Bearer token parameter:
- `CreateMacroAsync(token, MacroCreateRequest)` → `api/macros/create`
- `CreatePresetAsync(token, PresetCreateRequest)` → `api/presets/create`
- `UpdatePreferencesAsync(token, Dictionary<string, object> preferences)` →
  `api/preferences/update` (no `updated_at` — fresh account, first write)
- `UpdateDeviceStateAsync(token, vendorId, productId, activePresetUid)` →
  `api/devices/update` (no `updated_at` — first write)
Request/response DTOs live in a new `SyncApiDtos.cs`, `snake_case` via `JsonPropertyName`,
matching the v2.0 contract verbatim (`uid`, `vendor_id`, `product_id`, `repeat_count`,
`repeat_mode`, `events[{delay_ms,is_press,key_code}]`, `is_default`, `configuration`,
responses carrying `preset.updated_at` / `macro.updated_at`).

**Configuration serializer.** New `PresetConfigurationBuilder` (App/Services): given a
`presetId` + owner scope, reads the snapshot tables (`preset_rgb`, `preset_rgb_effect`,
`preset_settings`, `preset_button_bindings`, `preset_key_bindings`, `preset_actuation`,
`preset_key_macro_bindings`) via `LocalStorageService` and produces the `configuration`
object in the doc's shape: `rgb { is_enabled, selected_effect, effects[] }`, `settings {}`,
`actuation []`, `key_bindings []`, `button_bindings []`, plus a client-defined
`key_macro_bindings []` whose entries reference macros by **uid** (`{ matrix_index,
macro_uid }`) — same for macro references inside `button_bindings`
(`macro_uid` instead of the local integer `macro_id`). Missing read methods for snapshot rows
(most exist near `GetPresetActuation`/`GetKeyMacroBindings`) get thin per-table getters in
`LocalStorageService`. If Open question 2 answers "server validates the shape", the two
extra sections are dropped by the coder behind a single boolean at the top of the builder.

**`SettingsSyncService`** (replaces the stub body; interface unchanged):
- ctor deps: `ISyncApiService`, `LocalStorageService`, `UserProfile` (token source:
  `SessionToken`), `PresetConfigurationBuilder`, `ILogger<SettingsSyncService>`.
- `OnGuestUpgradedAsync(customerId)`: captures `customerId` + token, starts
  `Task.Run(PushAsync)`, stores it in `internal Task PushTask` (test seam), returns
  `Task.CompletedTask`. Never throws.
- `PushAsync` (all storage reads/writes through `RunOnStorageAsync` — FIFO puts them after
  the migration's rekey):
  1. Enumerate device types: `GetDevices("user", customerId)` → distinct `(vendorId, productId)`.
  2. **Macros first** (presets reference them): for each device type + each `device_key`
     present, `GetMacros` → `CreateMacroAsync`. On success, write `server_updated_at`.
  3. **Presets**: `GetPresets` per device type; build configuration; `CreatePresetAsync` with
     `is_default` from the record. On success, write `server_updated_at`.
  4. **Preferences**: `LoadAppPreferences`-equivalent read; if any of the 5 rows exist, one
     `UpdatePreferencesAsync` with the 5 documented keys (`SleepTimer`, `ScrollLines`,
     `DoubleClickSpeed`, `GenericLiftOffDistance`, `ScrollOneScreenAtATime`).
  5. **Device state**: per device type, collapse per-unit `GetAppliedPreset` rows to one —
     a row under a real `device_key` wins (the first by ordinal sort when several units have
     one), and `device_key = ''` is used only as the fallback when no unit-keyed row exists;
     log when units disagreed (API v2.0 has no per-unit key — accepted lossy collapse, Open
     question 4). `UpdateDeviceStateAsync` with the preset's uid.

     > **2026-08-26 correction** (PR #120 review, M1): this originally read "the row with
     > `device_key = ''` wins, else the first by ordinal sort", which was right while `''`
     > only ever meant a merged-transport unit. The [sign-in pull](../add-api-v2-pull/add-api-v2-pull-plan.md)
     > changed what `''` means — it is now where the *cloud's* applied preset is written for
     > every device type — so an `''`-first rule made the pulled value outrank the preset the
     > user actually applied on this machine, and that user choice never reached the server.
     > The rule above is the corrected one, shared by both push paths as
     > `SettingsSyncService.ChooseAppliedPresetRow`.
- Error policy: `*_UID_ALREADY_EXISTS` counts as success for that item (idempotent retry
  semantics — the item is already on the server); any `IsNetworkError` response aborts the
  remaining pipeline (server unreachable — later calls would each burn a timeout); any other
  per-item failure logs and continues. Nothing ever propagates to the caller.
  **Updated 2026-08-27** (`docs/plans/improve-api-error-surfacing/`): HTTP 429 is now a third
  abort reason in both `SettingsSyncService.Aborts` and `SyncCoordinator.Aborts`. The
  coordinator additionally arms a cooldown (the response's `Retry-After`, else 60 s, clamped
  to 5 minutes), waits it out **once** before a single retry pass, and then yields to the
  next signal — dirty state is preserved throughout.
- DI: `App.axaml.cs::BuildServices` registers `ApiHttpClient`, `ISyncApiService`,
  `PresetConfigurationBuilder`; the existing `ISettingsSyncService` singleton line gains the
  new ctor deps automatically.

## File-level change list

Ordered so that characterization/regression tests land with or before the behaviour they pin.

**Stage 1 — contact rename**
1. `src/ProoveGaming.App/Services/AuthApiService.cs` — edit: route literal `api/contact` (and
   the comment naming it).
2. `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` — edit: drop `AcceptedRouteMissing`
   from `ContactUsOutcome` and `DecideContactUsOutcome`; single success wording branch.
3. `src/ProoveGaming.App/Resources/Strings.en.axaml`, `Strings.uk.axaml` — edit: remove
   `ContactUs_SentTitle`/`ContactUs_SentToast` and the degraded-wording comments.
4. `tests/ProoveGaming.App.Tests/HomeViewModelContactUsTests.cs` — edit: 404 and
   malformed-2xx cases now expect `Failed`; remove `AcceptedRouteMissing` assertions.
5. `docs/plans/contact-us-and-update-check/contact-us-and-update-check-plan.md` — edit:
   dated note ("2026-08-25: API v2.0 renamed the route to `api/contact`; the
   AcceptedRouteMissing degraded mode is retired by ../add-api-v2-sync/add-api-v2-sync-plan.md")
   + correct the live acceptance criteria that name `api/contact_us` or the accepted wording.
   Review-findings journal untouched.

**Stage 2 — uid + server_updated_at**
6. `src/ProoveGaming.App/Services/LocalStorageService.cs` — edit: `ColumnExists` helper;
   ALTER TABLE + backfill + unique indexes for `presets`/`macros`; `InsertPreset`/`SaveMacro`
   write `uid`; `GetPresets`/`GetMacros` read the new columns; new
   `SetPresetServerUpdatedAt(int presetId, string value)` / `SetMacroServerUpdatedAt(...)`;
   thin snapshot getters needed by `PresetConfigurationBuilder` (only those that don't exist).
7. `src/ProoveGaming.App/Models/PresetRecord.cs` — edit: `Uid`, `ServerUpdatedAt`.
8. `src/ProoveGaming.App/Models/MacroRecord.cs` — edit: `Uid`, `ServerUpdatedAt`.
9. `tests/ProoveGaming.App.Tests/LocalStorageServicePresetsTests.cs` — edit: new-row uid
   generation, uniqueness, legacy-DB backfill (create the old shape by hand, reopen, assert
   distinct non-empty uids), rekey preserves uid.
10. `tests/ProoveGaming.App.Tests/LocalStorageServiceMacroScopeTests.cs` — edit: same four
    cases for macros.

**Stage 3 — sync client + push**
11. `src/ProoveGaming.App/Services/ApiHttpClient.cs` — new: envelope POST helper extracted
    from `AuthApiService` (owns `HttpClient`, base URL, timeout/network-error mapping).
12. `src/ProoveGaming.App/Services/AuthApiService.cs` — edit: delegate to `ApiHttpClient`;
    no public-surface change.
13. `src/ProoveGaming.App/Services/ISyncApiService.cs` — new: the four methods above.
14. `src/ProoveGaming.App/Services/SyncApiService.cs` — new: implementation over
    `ApiHttpClient`.
15. `src/ProoveGaming.App/Services/SyncApiDtos.cs` — new: request/response DTOs matching the
    v2.0 contract.
16. `src/ProoveGaming.App/Services/PresetConfigurationBuilder.cs` — new: snapshot →
    `configuration` object.
17. `src/ProoveGaming.App/Services/SettingsSyncService.cs` — edit: stub → real push
    (background task, `PushTask` seam, error policy above).
18. `src/ProoveGaming.App/Services/ISettingsSyncService.cs` — edit: comment update only (the
    stub note is stale once implemented); interface shape unchanged.
19. `src/ProoveGaming.App/App.axaml.cs` — edit: register `ApiHttpClient`, `ISyncApiService`,
    `PresetConfigurationBuilder`.
20. `tests/ProoveGaming.App.Tests/SettingsSyncServiceTests.cs` — new: see Test strategy.
21. `tests/ProoveGaming.App.Tests/PresetConfigurationBuilderTests.cs` — new: see Test
    strategy.

**Docs**
22. `docs/knowledge-base/proove-gaming-launcher-api-v2.0-en.md` — new: full English
    translation of the v2.0 docx (repo rule: non-English docs are translated into the
    knowledge base), with a header diff-vs-v1.7 note.
23. `docs/CONTEXT.md` — edit: one-line entries for `SyncApiService`, `ApiHttpClient`,
    `PresetConfigurationBuilder`, `server_updated_at`, "upgrade push".
24. `docs/plans/presets/`, `docs/plans/share-presets-by-device-type/`,
    `docs/plans/migrate-macros-per-device/` — edit: dated cross-reference note that
    `presets`/`macros` gained `uid`/`server_updated_at` for API v2.0 sync (this plan). No
    contract rewrites — schema note only.

## Acceptance criteria

Stage 1:
- [ ] Given the app sends a Contact Us message, when the HTTP request is captured, then it
  POSTs to `<base>/api/contact` and no code or resource references `api/contact_us` anymore
  (`grep -r contact_us src/` is empty).
- [ ] Given the backend answers 2xx `{"success":true}`, when the send completes, then the
  toast/bell use the `ContactUs_Delivered*` strings in both languages.
- [ ] Given the backend answers 404, or 2xx with a non-envelope body, when the send
  completes, then the dialog stays open with `ContactUs_SendFailed` (no "accepted" outcome
  exists; `ContactUs_Sent*` keys are gone from both resource files).

Stage 2:
- [ ] Given a fresh database, when a preset or macro is inserted, then its row has a unique
  36-char lowercase `uid` and `server_updated_at IS NULL`.
- [ ] Given a database created by the previous app version (no `uid` columns), when
  `LocalStorageService` is constructed over it, then every existing preset/macro row has a
  distinct non-empty `uid`, and existing data is otherwise untouched.
- [ ] Given a guest with presets/macros, when `MigrateGuestToUserAsync` rekeys them, then
  each row keeps the same `uid` it had as a guest.

Stage 3:
- [ ] Given a guest session with ≥1 macro, ≥1 preset (with snapshot content), saved app
  preferences and an applied preset, when a new account is registered and
  `OnGuestUpgradedAsync` fires, then (observed via a fake `ISyncApiService`) the service
  issues `macros/create`* → `presets/create`* → `preferences/update` → `devices/update` in
  that order, with the local uids, and `devices/update` carries the applied preset's uid.
- [ ] Given the fake returns success with an `updated_at`, when the push completes
  (`PushTask` awaited), then the corresponding local rows have that value in
  `server_updated_at`.
- [ ] Given the fake returns `PRESET_UID_ALREADY_EXISTS` / `MACRO_UID_ALREADY_EXISTS` for an
  item, when the push runs, then the push continues past it and does not log it as an error.
- [ ] Given the fake reports a network error on the first call, when the push runs, then no
  further API calls are made and `OnGuestUpgradedAsync`'s returned task (and `PushTask`)
  complete without throwing.
- [ ] Given a guest with no local data, when the push runs, then zero API calls are made.
- [ ] Given `OnGuestUpgradedAsync` is called, when it returns, then it has made no HTTP call
  and no storage read on the caller's thread (returns within the synchronous part).
- [ ] Given two physical units of one model with different applied presets, when device state
  is pushed, then exactly one `devices/update` is sent for that model and a warning is
  logged about the collapse.

## Test strategy

Change kinds: Stage 1 is a behaviour change to a **tested** feature —
`HomeViewModelContactUsTests` already pins `DecideContactUsOutcome`, so the edit is to the
existing cases (they must fail before the code change if run against new expectations, pass
after). Stages 2–3 are **new features** (`add-*`) — the tests below are mandatory coverage,
in `tests/ProoveGaming.App.Tests`:

- `HomeViewModelContactUsTests` (edit): outcome mapping table shrinks to Delivered/Failed;
  404 → Failed; 2xx+Malformed → Failed; success → Delivered; cancel/send gates unchanged.
- `LocalStorageServicePresetsTests` / `LocalStorageServiceMacroScopeTests` (edit): uid
  generation, uid uniqueness across inserts, legacy-DB backfill (build the pre-v2 shape with
  raw SQL in a temp file, then construct the service over it), rekey preserves uid,
  `SetPresetServerUpdatedAt` round-trip.
- `PresetConfigurationBuilderTests` (new): golden path — a preset with rgb + settings +
  actuation + button/key/key-macro bindings serializes to the documented shape with
  `macro_uid` references; empty snapshot → empty sections (arrays present, not omitted).
- `SettingsSyncServiceTests` (new): drives the real service against a real temp
  `LocalStorageService` and a recording fake `ISyncApiService` — ordering, ALREADY_EXISTS
  tolerance, network-error abort, no-data no-calls, `server_updated_at` write-back,
  synchronous-return criterion (fake that blocks until released; assert
  `OnGuestUpgradedAsync` returned before release), malformed-2xx counted as success without
  write-back (Round 1 S1), no-token early exit, and the PushTask-replacement Information
  log on a second upgrade in one session.

Deliberately not covered by automated tests: live HTTP against the real backend (no test
server; `ApiHttpClient`'s behaviour is already characterized through the existing
AuthApiService-level tests), the `MainWindow` wiring of the push after `OnNicknameChosen`
(UI-thread/window plumbing — covered by QA step 3), and translation fidelity of the
knowledge-base doc.

## QA (manual test steps)

1. Launch the app without signing in (Continue as guest). Connect a supported mouse or
   keyboard. Expected: the device card appears.
2. As a guest: create a macro in Macros, save a new preset in Presets (add a description,
   change an RGB effect first), change Settings sliders (scroll lines, double-click speed),
   and apply the preset. Expected: all of it works as before.
3. From the guest session, register a **new** account (Sign up → choose nickname). Expected:
   sign-up completes into Home without any extra delay or freeze; the devices, macros and
   presets created as a guest are still there.
4. Within ~a minute of step 3, on the backend (or via an API inspection tool with the new
   account's token): call `api/macros/list` and `api/presets/list`. Expected: the guest's
   macro and preset are listed with the same names; `api/devices/get` for the device's
   VID/PID returns the applied preset's uid.
5. Repeat step 3 with airplane mode on (no network). Expected: sign-up still cannot complete
   (registration needs the network) — so instead: complete step 3 online, then immediately
   kill the network mid-push. Expected: the app does not crash, shows no error dialog, and
   local data is intact.
6. Open Contact Us (headset icon → Contact Us), fill a valid email/topic/message, press
   SEND. Expected: the dialog closes and the toast says the message was **sent** (en:
   "Your message has been sent" wording family) — not "accepted".
7. Point the app at a backend without the contact route (or block the route). Send again.
   Expected: the dialog stays open with a red "failed to send" line; no success toast.
8. Uninstall nothing / no installer impact expected — skip installer checks.

## Review focus

1. `thread-safety` — every storage access in the push goes through the executor; no reads on
   the UI thread; `PushTask` races (app exit mid-push) end in a logged fault, not a crash.
2. `async/await` — no sync-over-async anywhere in the push; `OnGuestUpgradedAsync` returns
   without doing I/O.
3. `null-safety` — DTO deserialization of partial server answers (missing `preset` object on
   an error response) must not NRE the push loop.
4. `localization` — removed string keys are removed from **both** languages and no XAML
   references them.

## Performance & quality

- The push is a one-shot background pipeline on the thread pool; it must never touch the UI
  thread and never block sign-up completion (Acceptance: synchronous-return criterion).
- Storage reads for serialization go through `RunOnStorageAsync` — sequential with other
  storage work by design; the push is not latency-sensitive, so no parallel HTTP (sequential
  calls also keep macro-before-preset ordering trivially correct).
- No hot paths touched: no per-frame, per-keystroke or device-poll code changes. The schema
  migration runs once at startup on the storage connection before any UI needs it — backfill
  is O(rows) with tiny row counts.
- `ApiHttpClient` owns the single `HttpClient` (no per-call clients / socket exhaustion).

## Risks

- **`api/contact` not actually deployed yet** — after Stage 1 a send against an old backend
  shows "failed" instead of the old white lie. This is honest but user-visible; gate merging
  Stage 1 on Open question 1.
- **Server validates `configuration` strictly** — our `key_macro_bindings` extension and
  `macro_uid` references could be rejected (`CONFIGURATION_REQUIRED`-family errors) or,
  worse, silently stripped. Mitigation: single switch in `PresetConfigurationBuilder`; gate
  on Open question 2.
- **`repeat_mode` numeric mismatch** — the app's proto enum (`Macro_PlayCount = 0`, …) may
  not match the server's expectation (`INVALID_REPEAT_MODE` exists but values are
  undocumented). Gate on Open question 3.
- **PHP empty-array/object quirk** — v2.0's `preferences/get` shows `[]` for an empty object;
  create/update responses may have similar shape instability. DTOs must tolerate it (the push
  only needs `updated_at`; treat any undeserializable success payload as success without
  write-back).
- **Half-pushed state on failure** — no batch endpoint; an aborted push leaves some items on
  the server. Accepted: creates are idempotent-by-uid from our side (ALREADY_EXISTS →
  success), so a future sync pass converges; nothing re-runs the push automatically in this
  plan.
- **Schema migration on live user DBs** — an ALTER TABLE bug can brick startup. The
  legacy-DB tests in Stage 2 are the guard; the migration must be tolerant of re-running
  (all steps IF-NOT-EXISTS / guarded).

## Open questions

1. **Is `api/contact` deployed?** Backend must confirm before Stage 1 merges; until then the
   branch holds. (The doc says yes; the code comment says the old route wasn't live as of
   PR #118.)
2. **Is `configuration` an opaque blob?** If the server validates its shape, confirm the
   exact allowed schema and the macro-reference format for `key_bindings`/`button_bindings`;
   our proposal is `macro_uid` fields plus a `key_macro_bindings` array — sent to the backend
   team 2026-08-25, unanswered.
   *(2026-09-10: verified empirically against the live server — a preset carrying both
   `key_macro_bindings` and `extended_keys` round-tripped through `presets/create` → `presets/get`
   / `presets/list` unchanged; `configuration` is stored opaquely. No backend change needed. See
   `docs/plans/add-extended-keys-api-sync/`.)*
3. **`repeat_mode` allowed values** — confirm they equal the proto enum's numeric values.
4. **Per-unit device state** — v2.0's `devices/*` keys on VID/PID only; we collapse per-unit
   applied presets. Does the backend plan a `device_key`? If yes soon, hold step 5 of the
   push.
5. **Where does the push trigger belong long-term?** This plan keeps the existing
   `MainWindow.OnNicknameChosen` call site untouched. A future sign-in pull will need its own
   trigger; out of scope here.

## Review findings

### Round 1 (2026-08-25) — Ship: yes

Reviewed: full working-tree delta vs `main` (no commits on the branch — 21 modified files
plus 9 untracked, reconciled against `git status --porcelain`; every entry reviewed).
Verified independently: `dotnet test ./tests/ProoveGaming.App.Tests` — 1037 passed, 0 failed
(the xUnit1031 warnings are pre-existing in untouched files).

#### Summary
The branch implements all three stages of this plan: the Contact Us route rename with the
`AcceptedRouteMissing` outcome retired (code, both string files, tests, cross-plan notes);
the `uid`/`server_updated_at` schema migration with backfill and unique indexes; and the
real one-shot upgrade push (`ApiHttpClient` extraction, `ISyncApiService`/`SyncApiService`,
`SyncApiDtos`, `PresetConfigurationBuilder`, `SettingsSyncService` with the `PushTask` seam),
plus the KB translation, `docs/CONTEXT.md` entries and reconciliation notes in five plans.

#### Plan conformance
Plan: this document.
- Implemented as planned: all 24 File-level change list items are present and match the
  described shapes. Spot-verified: route literal (`AuthApiService.cs:169`), two-branch
  `DecideContactUsOutcome`, `ColumnExists`/`AddColumnIfMissing` refactor, backfill before
  unique-index creation (`LocalStorageService.cs:513–531`), all four `INSERT INTO
  presets|macros` paths supply a uid (lines 753, 1674, 1953, 2090 — no path can trip
  `idx_*_uid` with a second `''`), rekey touches only owner columns (lines 3516, 3526),
  the `devices` table is rekeyed too (line 3561) so `GetDevices("user", …)` feeds the push,
  DTO field names match the KB contract verbatim (`uid`, `vendor_id`, `repeat_mode`,
  `events[{delay_ms,is_press,key_code}]`, `preferences` wrapper, `active_preset_uid`), and
  DI registrations are complete (`App.axaml.cs:193–196`; `ApiHttpClient` is a container-owned
  `IDisposable` singleton, `AuthApiService` correctly dropped `IDisposable`).
- Review-focus checks all pass:
  1. *thread-safety* — every storage access in `PushCoreAsync` goes through
     `RunOnStorageAsync`; `PresetConfigurationBuilder.Build` runs as ONE executor item whose
     getters take `_lock` per call (SemaphoreSlim is non-reentrant, and the code correctly
     never nests it); `Dispose` drains the executor before flipping `_disposed`, so a push
     item mid-run cannot hit a disposed connection; post-shutdown reads yield `null` and
     every one is null-checked into a silent stop.
  2. *async/await* — no `.Result`/`.Wait()`/`GetAwaiter().GetResult()` anywhere in the
     delta; `OnGuestUpgradedAsync` reads only the in-memory token before returning
     (pinned by `OnGuestUpgradedAsync_ReturnsWhileTheFirstApiCallIsStillBlocked`).
  3. *null-safety* — `response.Macro?.UpdatedAt is { Length: > 0 }` guards both write-backs;
     a PHP `"macro": []` shape fails the whole-envelope bind → `MalformedPayload` 2xx →
     treated as success without write-back, exactly the plan's risk note; no `!` bangs added.
  4. *localization* — `ContactUs_Sent*` removed from **both** `Strings.en.axaml` and
     `Strings.uk.axaml`; `grep -r contact_us|ContactUs_Sent src/` is empty; the only
     remaining `AcceptedRouteMissing` mention anywhere is a retrospective test comment.
- Deviates from plan: none found. (Step 2's "each device_key present" is implemented via a
  new `GetMacroDeviceKeys` getter rather than iterating device-card keys — a strictly more
  complete reading of the same step, called out in its code comment.)
- Missing / partial: none.
- Accepted limitations (per plan) — residual risks restated, not re-graded:
  - A non-network per-item failure (including 401/expired token) logs and continues through
    every remaining item (plan error policy). Residual: with a revoked token the push burns
    one fast failed round-trip per item plus a warning per item. Harmless; a future sync
    pass converges.
  - Per-unit applied presets collapse to one `devices/update` (Open question 4) — warning
    logged, pinned by test.
  - Macros/presets under a device type with no `devices` row are not pushed (enumeration is
    defined by `GetDevices`, plan step 1).
  - `key_macro_bindings`/`macro_uid` payload sections gated on Open question 2 via
    `PresetConfigurationBuilder.IncludeMacroReferences` — still unanswered by the backend,
    as is Open question 1 (`api/contact` deployment), which the plan says gates merging
    Stage 1.

#### Code
- Blockers: none.
- Should-fix: none. (S1 below is a test-coverage gap, filed under Tests.)

#### Tests
- [x] **S1. Malformed-2xx success path of `Succeeded` is untested** — the plan's "PHP
  empty-array/object quirk" risk says an undeserializable 2xx must count as success without
  write-back, and `SettingsSyncService.Succeeded` implements it
  (`src/ProoveGaming.App/Services/SettingsSyncService.cs:283` — the
  `StatusCode is >= 200 and < 300 && MalformedPayload` branch), but no test exercises it:
  every fake response in `SettingsSyncServiceTests` uses `Success`, `ErrorCode` or
  `IsNetworkError`. If a later edit inverted that branch, the push would start logging every
  PHP-quirk response as a rejection (or worse, writing back nothing forever) and the suite
  would stay green. **What to cover:** a `MacroCreateResponse { Success = false,
  StatusCode = 200, MalformedPayload = true }` → push continues, logs nothing at Warning+,
  writes no `server_updated_at`. — `tests/ProoveGaming.App.Tests/SettingsSyncServiceTests.cs`.
- Optional (same file, lower value): the no-token early exit (`OnGuestUpgradedAsync` with an
  empty `SessionToken` → zero API calls, `PushTask` stays completed) is also uncovered.

> 2026-08-25 (test-writer, post-Round-1 fix pass): S1 closed —
> `Push_Malformed2xxOnCreate_CountsAsSuccessWithoutWriteBack` covers the malformed-2xx
> success path for both creates (continues to devices/update, no Warning+, no
> `server_updated_at` write-back). The optional no-token early exit is covered by
> `OnGuestUpgradedAsync_EmptySessionToken_MakesZeroApiCalls`. The fix pass's
> PushTask-replacement Information log is pinned by
> `OnGuestUpgradedAsync_SecondCallWhileFirstPushRuns_LogsReplacementAtInformation`, and the
> disagreement-warning test now asserts the uid+name appear in the message. Full
> `ProoveGaming.App.Tests` run: 1040 passed, 0 failed.

#### Nits
- The unit-disagreement warning logs the local `PresetId` integer; the preset uid (or name)
  would make the log line actionable against the server — `SettingsSyncService.cs:194`.
- A second guest→register cycle in one app session overwrites `PushTask` while the first
  push may still be running. Both pushes are correct (different owner ids, serialized
  storage), only the seam/diagnostics lose the first task — worth a one-line comment at most.
- `deviceTypes` is a `List` probed with `Contains` (O(n²)); n = a user's device models, so
  irrelevant in practice — `SettingsSyncService.cs:87`.

#### What's done well
- The executor discipline in the push is exactly right: one `RunOnStorageAsync` item per
  logical read (including the whole `Build`), write barriers respected, and the
  `MainWindow.axaml.cs:466–467` ordering makes the FIFO post-migration claim true by
  construction, not by hope.
- `SettingsSyncServiceTests` drive the real service against a real store with HTTP as the
  only fake, and the gate-based synchronous-return test pins the plan's hardest acceptance
  criterion precisely.
- The legacy-DB backfill tests build the genuine pre-v2 schema with raw SQL and assert
  stability across reopens — the exact guard the "brick startup" risk asked for.
- `ApiHttpClient` extraction is a clean behaviour-preserving lift; `AuthApiService`'s public
  surface and the envelope semantics (malformed-2xx detection) survive unchanged, pinned by
  the existing contact tests.
- The KB translation (864 lines) with the v1.7-diff header and the honest naming note
  ("API 1.8_.docx" titling itself v2.0) is exemplary knowledge-base hygiene.

#### Verdict
Ship: yes — no blockers; the one should-fix is a test-coverage gap (S1) that is a safe
follow-up, and both open backend questions (1: `api/contact` deployment, which the plan
itself gates the Stage 1 merge on; 2: configuration shape) are external, not code defects.
No installer/uninstaller files are touched, so no `/security-review` pass is required.
First: 1) add the S1 malformed-2xx push test; 2) get Open question 1 answered before merge
per the plan's own gate; 3) optionally cover the no-token early exit.
