# Plan: PG-130 — Persist device settings locally, migrate on guest → user upgrade

> **Note (2026-09-24):** the sign-in, registration, sign-out and guest-expiry logic this plan places in `MainWindow.axaml.cs` now lives in `src/ProoveGaming.App/Services/AuthSessionCoordinator.cs` ([`refactor-auth-session-flow`](../refactor-auth-session-flow/refactor-auth-session-flow-plan.md)). Same statements, same order; `MainWindow` keeps the navigation. Name map: `OnSignInSucceeded` → `CompleteSignInStateAsync` + `FinishSignInAsync`; `OnNicknameChosen` → `RegisterAsync` + `FinishRegistrationAsync`; `OnLogoutRequested` → `LogoutAsync`; `SignOutToFreshSignIn` → `SignOut`; `OnTokenRejected`'s check → `IsCurrentToken`; `OnSessionExpired` → `HandleGuestSessionExpired`; `OnGuestContinueRequested` → `StartGuestSession`; `ClearSignedInState`, `PersistAuthIfTokenPresent` and `EnqueueStorageWrite` moved as they were.

## Goal

Every user-tunable device setting the App exposes must be written to the local
SQLite DB while the user is a **guest** so that closing and reopening the App
restores the exact UI state (per device). When a guest **signs in or signs up**
successfully, the guest-owned settings must be **re-associated** with the newly
authenticated user in the same local DB (the "migration") instead of being
wiped by the current `_localStorage.ClearData()` call. A separate seam
(`ISettingsSyncService`) is introduced for a future server push, but the actual
network sync is out of scope for this task — no `/api/settings` endpoint exists
today.

## Scope

**In:**

- New SQLite table(s) for per-owner, per-device settings inside the existing
  `proovegaming.db` (managed by `LocalStorageService`).
- Persist the following App-side settings on every change (or debounced batch
  where a debounce already exists):
  1. **RGB per-device state** — currently held in `_rgbStateByDevice`
     `Dictionary<(vid,pid), RgbDeviceState>` in
     `DeviceDetailsViewModel.Rgb.cs` and lost on app restart. Persist:
     `IsEnabled`, `SelectedEffectKey`, and per-effect `(Hex, Brightness, Speed)`.
  2. **DPI presets** — the 7-stage table `(DpiValue, R, G, B)` from
     `DeviceDetailsViewModel.cs::DpiPresets` (write on
     `ApplyDpiRecommendationsCommand`, load on `SetDevice`).
  3. **Active DPI stage index** — the last stage the DeviceHost reported for
     the device (used to prefill the highlight; DPI-button presses on the
     device still drive live changes).
  4. **Polling rate** — the currently selected `Hz` from `PollingRateOptions`.
  5. **BLAZE per-device settings** — `ButtonDebounce` and `LiftOffDistance`
     from `DeviceDetailsViewModel.Settings.cs`, i.e. the `_deviceSettings`
     collection.
  6. **Generic mouse settings** — `ScrollOneScreenAtATime`, `ScrollLines`,
     `DoubleClickSpeed`, `SleepTimer`, and the fallback `LiftOffDistance`
     (the ones behind the empty `SaveSettingsCommand` stub today). Store as
     an App-scoped preference block, not per device.
  7. **RGB enabled toggle per device** — already inside `RgbDeviceState` (1),
     called out explicitly because it drives `IsRgbEnabled`.
- New `ISettingsRepository` (App-side) on top of `LocalStorageService`, so the
  DB access is testable and the VM does not talk raw SQL.
- Migration step on **guest → user** transition (`OnSignInSucceeded`,
  `OnNicknameChosen` → post-login branch): stop calling
  `_localStorage.ClearData()` unconditionally; instead, re-key the guest's
  settings rows to the newly authenticated customer.
- **User → guest transition (logout) leaves the new guest blank.** No
  re-key runs on logout; the user's rows stay under `('user', customer_id)`
  and are ignored by the new guest owner. Guarantee flows from
  `SessionService.StartSession()` minting a fresh `Guid.NewGuid()` on every
  guest continue and `CurrentOwnerProvider` scoping reads/writes to that
  new UUID — no additional code required, but the behaviour is asserted by
  AC11.
- Empty seam `ISettingsSyncService` for a future server push (invoked from
  the migration step); default implementation is a no-op that logs at
  Information level. **No network calls in this plan.**

**Out:**

- **Button remapping** (`SetButtonBindingsAsync`). Already persisted by the
  **DeviceHost** at `%ProgramData%/ProoveGaming/button-bindings.json` per
  `docs/plans/add-button-remapping/add-button-remapping-plan.md` (locked
  decision #4). The plan explicitly rejected App-side LocalStorage for
  bindings; do not duplicate that decision.
  *(Note, 2026-09-24: this is no longer true. Bindings are persisted by the App in SQLite, and the
  DeviceHost store was removed as dead code in `docs/plans/fix-repository-audit-findings/`, Phase 3 D1.)*
- **DeviceHost-side settings** (LED protocol quirks per keyboard family, HID
  discovery, etc.). Those are host state, not user preferences.
- **Firmware-check timestamps** — `CheckFirmwareUpdatesCommand` is a stub.
- **Server sync** — `/api/settings` endpoint does not exist in
  `IAuthApiService`. Migration on guest → user is local-only (re-key rows);
  the server round-trip is a follow-up task with its own backend work.
- **Macros** — the create-macro flow is UI-only; the `Macros` collection
  isn't wired to the device or persisted today (macro protocol `0x04`/`0x13`
  is deferred per the button-remap plan).

## Options considered

### A. Where the settings DB lives

- **A1 — Extend `LocalStorageService` (recommended).** Add tables inside the
  existing `%LocalAppData%/ProoveGaming/proovegaming.db` and expose them
  through a new `ISettingsRepository`. Reuses `Microsoft.Data.Sqlite`, one
  connection, one `EnsureSchema` call. Migration to a user is a single
  `UPDATE … SET owner_kind='user', owner_id=$customer WHERE
  owner_kind='guest'` inside a transaction.
- **A2 — Separate settings DB file.** Cleaner separation but two connection
  managers, two dispose paths, and cross-file transactions are hard. Rejected.
- **A3 — JSON file per device** (mirroring the host's button-bindings store).
  Simpler than SQL for small blobs, but poor fit for a per-owner rekey step
  (would need a file-scan-and-rewrite). Rejected.

### B. Owner key model

- **B1 — `(owner_kind, owner_id)` composite (recommended).** `owner_kind` is
  `'guest'` or `'user'`; `owner_id` is the guest session `Guid` string for
  guests and `customer_id` (integer, stored as text for uniformity) for
  users. Simple `UPDATE` on migration. Handles the case where a guest signs
  in as an *existing* customer that already has local settings from a prior
  session (conflict resolution below).
- **B2 — Store guest and user in different tables.** Doubles the DDL and
  every read has to `UNION`. Rejected.

### C. When to write

- **C1 — Write-through on every semantic change (recommended).** RGB and
  BLAZE settings already have a 300 ms debounce in the VM
  (`_sliderDebounceCts`, `_mouseSettingsDebounceCts`); reuse that same
  debounce tick to also persist to the DB after applying to the device.
  DPI-preset edits happen only on the explicit `Apply` command. Polling
  rate writes on the click that changes it.
- **C2 — Write on app exit only.** Matches `LocalStorageService.Flush`
  today (devices are flushed on `desktop.Exit`), but any crash loses the
  session. Rejected — the whole point is durability.

### D. Conflict resolution when a guest signs in as an existing customer

The signed-in customer may already have rows in the local DB from a previous
session on this machine. Options:

- **D1 — Guest wins (recommended for step 1).** The `UPDATE … WHERE
  owner_kind='guest'` re-keys the guest rows to the customer, `INSERT OR
  REPLACE` semantics on the composite `(owner_kind, owner_id, vid, pid,
  category)` primary key: guest values overwrite the user's existing local
  values. Rationale: the user just spent time in guest mode configuring the
  device — that state is fresher than anything on disk from a prior login.
  Simple, deterministic, documented in a comment.
- **D2 — User wins.** Discard guest state on login. Effectively "restore
  what I had before" — but the guest may have re-tuned a mouse in the same
  session and expects those tweaks to survive. Rejected.
- **D3 — Ask the user.** Adds a modal to a background flow. Rejected for
  step 1; can be revisited when server sync lands.

## Recommended approach

**A1 + B1 + C1 + D1.** Extend `LocalStorageService` with two new tables:
`device_settings` (per-owner, per-device, categorized JSON blob) and
`app_preferences` (per-owner, App-scoped preferences). Wrap them behind
`ISettingsRepository`. The categorized-blob shape avoids one column per
setting (fewer schema migrations) while still allowing category-level upserts
(e.g. only touch the `rgb` row when RGB changes).

### Schema (added to `LocalStorageService.EnsureSchema`)

```sql
CREATE TABLE IF NOT EXISTS device_settings (
    owner_kind   TEXT NOT NULL,       -- 'guest' | 'user'
    owner_id     TEXT NOT NULL,       -- guest session UUID (guests) or customer_id as text (users)
    vendor_id    INTEGER NOT NULL,
    product_id   INTEGER NOT NULL,
    category     TEXT NOT NULL,       -- 'rgb' | 'dpi_presets' | 'dpi_active' | 'polling_rate' | 'mouse_settings'
    payload_json TEXT NOT NULL,       -- JSON serialized shape per category
    updated_at   TEXT NOT NULL,       -- ISO 8601 UTC
    PRIMARY KEY (owner_kind, owner_id, vendor_id, product_id, category)
);

CREATE TABLE IF NOT EXISTS app_preferences (
    owner_kind   TEXT NOT NULL,
    owner_id     TEXT NOT NULL,
    payload_json TEXT NOT NULL,       -- one row per owner, holds Scroll/DoubleClick/Sleep etc.
    updated_at   TEXT NOT NULL,
    PRIMARY KEY (owner_kind, owner_id)
);
```

`payload_json` shapes (one C# record per category, `System.Text.Json`
`SerializeAsync`):

- `rgb` → `{ IsEnabled, SelectedEffectKey, Effects: { EffectKey: { Hex, Brightness, Speed } } }`
- `dpi_presets` → `[{ StageIndex, DpiValue, R, G, B }, …]` (7 entries)
- `dpi_active` → `{ StageIndex }`
- `polling_rate` → `{ Hz }`
- `mouse_settings` → `{ ButtonDebounce, LiftOffDistance }` (BLAZE-scoped keys —
  future device families extend the shape as needed)

App preferences payload:

- `{ ScrollOneScreenAtATime, ScrollLines, DoubleClickSpeed, SleepTimer, GenericLiftOffDistance }`

### Migration on guest → user

Introduce `LocalStorageService.MigrateGuestToUser(int customerId)`:

```csharp
public void MigrateGuestToUser(int customerId)
{
    using var tx = _connection.BeginTransaction();
    // 1. Delete any existing user rows that collide with guest rows (D1: guest wins).
    // 2. Re-key guest rows to the user.
    // 3. Delete the guest session row so ClearData() semantics still apply for
    //    guest-only state (last_sign_in / lockout are unaffected).
    tx.Commit();
}
```

Call it from `MainWindow.axaml.cs`:

- `OnSignInSucceeded`: replace `_localStorage.ClearData()` with
  `_localStorage.MigrateGuestToUser(customerId)` **before**
  `_session.EndSession()` (so the guest owner_id is still on
  `_session.SessionId`).
- `OnNicknameChosen`: same replacement on the branch that runs after the
  post-register `LoginAsync`.
- `OnSessionExpired`: keep `ClearData()` (guest timed out — no migration).

### Guest owner_id lifecycle

- On `SessionService.StartSession()` the guest owner_id becomes
  `SessionId.ToString("D")`. Newly-written settings are keyed to this string.
- On `SessionService.RestoreSession(uuid, …)` we reuse the same UUID, so the
  restored guest sees the same rows.
- `LocalStorageService.ClearData()` today deletes `devices` and `app_session`.
  Extend it to also delete `device_settings` and `app_preferences` for the
  current guest owner — but only when we truly wipe guest state (session
  expiry, not migration).

### Data flow

```
User edits an RGB slider
  → RgbEffectSettingsViewModel.PropertyChanged
    → DeviceDetailsViewModel debounce (300 ms)
      → ApplyRgbSettingsAsync (to device via gRPC)
      → SettingsRepository.SaveRgbAsync(vid, pid, state)   ← new
        → LocalStorageService (SQLite UPSERT)

User signs in
  → WelcomeViewModel.SignInAsync succeeds
    → MainWindow.OnSignInSucceeded
      → LocalStorageService.MigrateGuestToUser(customerId)   ← new
      → ISettingsSyncService.OnGuestUpgradedAsync(customerId) ← new (stub, no-op)
      → _session.EndSession()
      → UI redraw
```

## File-level change list

### `src/ProoveGaming.App/Services/LocalStorageService.cs`

- Add `device_settings` and `app_preferences` tables to `EnsureSchema()`.
- Add methods (async where feasible, matching the "Prefer async" rule from
  CLAUDE.md — Microsoft.Data.Sqlite supports async):
  - `Task UpsertDeviceSettingAsync(string ownerKind, string ownerId, int vid, int pid, string category, string payloadJson, CancellationToken ct = default)`
  - `Task<string?> ReadDeviceSettingAsync(string ownerKind, string ownerId, int vid, int pid, string category, CancellationToken ct = default)`
  - `Task<IReadOnlyList<(int vid, int pid, string category, string payload)>> ReadAllDeviceSettingsAsync(string ownerKind, string ownerId, CancellationToken ct = default)`
  - `Task UpsertAppPreferencesAsync(string ownerKind, string ownerId, string payloadJson, CancellationToken ct = default)`
  - `Task<string?> ReadAppPreferencesAsync(string ownerKind, string ownerId, CancellationToken ct = default)`
  - `Task MigrateGuestToUserAsync(Guid guestSessionId, int customerId, CancellationToken ct = default)`
- Extend `ClearData()` to also drop guest rows in the new tables (guard: only
  for the current guest owner, do not touch user rows).
- All new SQL uses `SqliteCommand.ExecuteNonQueryAsync` /
  `ExecuteReaderAsync` — do not fold sync-over-async on top.
  **Callers on `MainWindow` will need `await`**; keep the existing sync
  methods where they already are so this change stays scoped.

### `src/ProoveGaming.App/Services/ISettingsRepository.cs` — **new**

Typed façade over `LocalStorageService` for a specific owner (guest UUID or
customer_id). The VM never touches raw SQL / JSON — it calls `SaveRgbAsync`,
`LoadRgbAsync`, etc.

```csharp
public interface ISettingsRepository
{
    Task<RgbDeviceSettings?> LoadRgbAsync(int vid, int pid, CancellationToken ct = default);
    Task SaveRgbAsync(int vid, int pid, RgbDeviceSettings state, CancellationToken ct = default);

    Task<DpiPresetsSettings?> LoadDpiPresetsAsync(int vid, int pid, CancellationToken ct = default);
    Task SaveDpiPresetsAsync(int vid, int pid, DpiPresetsSettings state, CancellationToken ct = default);

    Task<int?> LoadActiveDpiStageAsync(int vid, int pid, CancellationToken ct = default);
    Task SaveActiveDpiStageAsync(int vid, int pid, int stageIndex, CancellationToken ct = default);

    Task<int?> LoadPollingRateAsync(int vid, int pid, CancellationToken ct = default);
    Task SavePollingRateAsync(int vid, int pid, int hz, CancellationToken ct = default);

    Task<MouseSettingsPayload?> LoadMouseSettingsAsync(int vid, int pid, CancellationToken ct = default);
    Task SaveMouseSettingsAsync(int vid, int pid, MouseSettingsPayload payload, CancellationToken ct = default);

    Task<AppPreferencesPayload?> LoadAppPreferencesAsync(CancellationToken ct = default);
    Task SaveAppPreferencesAsync(AppPreferencesPayload payload, CancellationToken ct = default);
}
```

### `src/ProoveGaming.App/Services/SettingsRepository.cs` — **new**

Default implementation. Depends on `LocalStorageService` and a
`ICurrentOwnerProvider` (see next) so the owner_kind/id used for every read
and write is always the current one. Serializes/deserializes each payload
with `System.Text.Json`.

### `src/ProoveGaming.App/Services/ICurrentOwnerProvider.cs` + `CurrentOwnerProvider.cs` — **new**

Returns `(ownerKind, ownerId)` for the current process state:

- If `UserProfile.CustomerId > 0` → `("user", customerId.ToString())`.
- Else if `SessionService.IsGuest && SessionId != Guid.Empty` →
  `("guest", SessionId.ToString("D"))`.
- Else `null` — repository methods must no-op for reads and skip writes.

Registered as singleton in DI. Depends on `SessionService` + `UserProfile`,
both already singletons.

### `src/ProoveGaming.App/Services/ISettingsSyncService.cs` + `SettingsSyncService.cs` — **new (stub)**

```csharp
public interface ISettingsSyncService
{
    Task OnGuestUpgradedAsync(int customerId, CancellationToken ct = default);
}
```

Default `SettingsSyncService` implementation is a **no-op** that logs
`"Settings sync stub: guest -> customer {CustomerId}. Server API not yet
available."` at `LogLevel.Information`. This is the future server-push seam;
plan closes without a real network call.

### `src/ProoveGaming.App/Models/Settings/*.cs` — **new**

Small DTO records used by both `ISettingsRepository` payloads and JSON:

- `RgbDeviceSettings(bool IsEnabled, string SelectedEffectKey, Dictionary<string, PerEffectSettings> Effects)`
- `PerEffectSettings(string Hex, double Brightness, double Speed)`
- `DpiPresetsSettings(IReadOnlyList<DpiPresetSetting> Stages)` +
  `DpiPresetSetting(int StageIndex, int DpiValue, byte R, byte G, byte B)`
- `MouseSettingsPayload(byte ButtonDebounce, byte LiftOffDistance)`
- `AppPreferencesPayload(bool ScrollOneScreenAtATime, double ScrollLines, double DoubleClickSpeed, double SleepTimer, double GenericLiftOffDistance)`

### `src/ProoveGaming.App/App.axaml.cs`

- Register the new services in `BuildServices()`:
  - `services.AddSingleton<ICurrentOwnerProvider, CurrentOwnerProvider>();`
  - `services.AddSingleton<ISettingsRepository, SettingsRepository>();`
  - `services.AddSingleton<ISettingsSyncService, SettingsSyncService>();`
- Inject `ISettingsRepository` into `DeviceDetailsViewModel` (add a nullable
  ctor parameter, matching the existing optional `IUsbHidDeviceService`
  pattern).

### `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Rgb.cs`

- Replace the in-memory `_rgbStateByDevice` dictionary with async load/save
  through `ISettingsRepository`:
  - `SaveRgbStateForCurrentDevice()` → fire-and-forget `SaveRgbAsync`
    (already runs on UI thread; keep the async call outside `await`).
  - `LoadRgbStateForCurrentDevice()` → becomes async; kick off from
    `SetDevice(device)` via a small helper. Preserve the current
    `_suppressAutoApply = true` guard.
- Extend `ApplyRgbSettingsAsync` / `SelectEffect` / `OnRgbEffectPropertyChanged`
  to persist inside the existing 300 ms debounce (same tick that writes to
  the device — no second timer).
- Fold the RGB-enabled toggle (`IsRgbEnabled`) into `RgbDeviceSettings.IsEnabled`.

### `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Settings.cs`

- On `RebuildDeviceSettings()` — after building `_deviceSettings`, load the
  persisted `MouseSettingsPayload` and pre-fill `ButtonDenounceSetting` /
  `LiftOffSetting` values (before subscribing to `PropertyChanged`, so
  loading doesn't retrigger `ApplyMouseSettingsAsync`).
- Extend `ApplyMouseSettingsAsync` to also persist the current values.
- Wire `SaveSettingsCommand` (currently empty) and `ResetSettingsCommand` to
  persist / restore the `AppPreferencesPayload` (the generic Scroll /
  DoubleClick / Sleep block).
- Hook `LiftOffDistance`, `ScrollOneScreenAtATime`, `ScrollLines`,
  `DoubleClickSpeed`, `SleepTimer` setters to a shared 300 ms debounced
  `SaveAppPreferencesAsync` — no per-slider immediate write.

### `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs`

- `SetDevice`: after `LoadRgbStateForCurrentDevice()`, also load DPI presets
  (`DpiPresets` list), active DPI stage (`HighlightActiveDpiStage`), and
  polling rate (`HighlightPollingRate`) from persisted state — using the
  device's live push as fallback per the existing `ApplyKnownDeviceState`
  logic. Persisted state has priority for the *initial UI value*; live
  device pushes still update it thereafter.
- `SelectPollingRate`: after the existing `SetReportRateAsync`, call
  `SavePollingRateAsync(vid, pid, hz)`.
- `ApplyDpiRecommendationsCommand`: after the existing `SetDpiStagesAsync`,
  call `SaveDpiPresetsAsync(vid, pid, current 7-stage snapshot)`.
- `OnDpiStageChanged`: after `HighlightActiveDpiStage`, call
  `SaveActiveDpiStageAsync(vid, pid, stageIndex)` (persist device-driven
  stage changes too).

### `src/ProoveGaming.App/Views/MainWindow.axaml.cs`

- `OnSignInSucceeded`: **remove** the `_localStorage.ClearData()` call;
  replace with `await _localStorage.MigrateGuestToUserAsync(sessionId,
  customerId)` followed by
  `await _settingsSyncService.OnGuestUpgradedAsync(customerId)`. The method
  is already `async void` today, so the `await`s slot in naturally. Capture
  `_session.SessionId` **before** `_session.EndSession()` clears it.
- `OnNicknameChosen`: same replacement after the post-register `LoginAsync`
  branch that sets `_userProfile.CustomerId`.
- `OnSessionExpired`: leave `_localStorage.ClearData()` in place — guest
  timed out; no migration.
- Inject `ISettingsSyncService` via DI (already resolves through the same
  ctor path as `SessionService` / `LocalStorageService`).

### `src/ProoveGaming.App/ViewModels/WelcomeViewModel.cs`

No change — the migration is triggered downstream from the succeeded event,
not inside the VM. This keeps `WelcomeViewModel` free of session-lifecycle
concerns.

### `docs/CONTEXT.md`

Add the two new terms alphabetically:

```
- **Guest → user migration** — the local-DB re-keying step that runs when a guest signs in or up, re-associating settings rows from the guest session UUID to the customer_id.
- **SettingsRepository** — the App-side façade over LocalStorageService that stores per-device and per-owner user preferences (RGB, DPI, polling rate, mouse settings) in the SQLite DB.
```

### `tests/ProoveGaming.App.Tests/ProoveGaming.App.Tests.csproj`

No `.csproj` edit — `Microsoft.Data.Sqlite` will be transitively available
through the referenced App assembly, and in-memory (`Data Source=:memory:`)
mode works out of the box. If it isn't, add the same package reference the
App uses.

### `tests/ProoveGaming.App.Tests/LocalStorageServiceSettingsTests.cs` — **new**

xUnit; see Test strategy.

### `tests/ProoveGaming.App.Tests/SettingsRepositoryTests.cs` — **new**

xUnit; see Test strategy.

## Acceptance criteria

- **AC1** Given a guest launches the App for the first time, when they change
  an RGB effect's brightness and close the App, then reopen it, the same
  brightness value appears in the UI without any device round-trip.
- **AC2** Given a guest configures BLAZE `ButtonDebounce` = 20 ms and
  `LiftOffDistance` = 3 mm, when they close and reopen the App, the two
  sliders show 20 and 3, respectively.
- **AC3** Given a guest sets polling rate to 500 Hz, when they close and
  reopen the App, the "500 Hz" pill is highlighted before the DeviceHost
  first push arrives.
- **AC4** Given a guest edits DPI presets and hits Apply Recommendations,
  when they reopen the App and open the same device, the 7 preset values
  are exactly what they applied (not the hardcoded defaults).
- **AC5** Given a guest has RGB / DPI / polling / mouse-settings state, when
  the guest signs in successfully, the same values appear in the UI for the
  authenticated user on the next device open in the same session.
- **AC6** Given a guest has state and the customer_id they sign in as
  already has different local settings for the same `(vid, pid)`, when
  migration runs, the guest values overwrite the pre-existing user values
  (**D1 — guest wins**).
- **AC7** Given a guest session expires (48h timeout), when
  `_localStorage.ClearData()` runs, all `device_settings` and
  `app_preferences` rows for that guest owner are deleted; user rows in the
  same tables are untouched.
- **AC8** Given a guest is on the sign-in screen, when they sign in
  successfully, `_localStorage.ClearData()` is **not** called (the current
  wipe is replaced with a re-key).
- **AC9** Given the `SettingsSyncService` stub is invoked on guest → user
  transition, it logs one Information-level entry and returns a completed
  `Task` — no network calls are made.
- **AC10** DB access on hot UI paths does not block the UI thread — every
  new repository method is `async` and `await`-able; there are no
  `.Result` / `.Wait()` / `.GetAwaiter().GetResult()` on the added code.
- **AC11** Given a signed-in user has persisted RGB / DPI / polling /
  mouse-settings state, when they log out and click "Continue as guest",
  the new guest sees the built-in defaults (no user rows leak into the
  guest owner). Verified at the repository level: `GetCurrent()` returns
  `('guest', NEW_UUID)` for the fresh guest session, and every
  `Load*Async` for that owner returns `null` / an empty result.

## Test strategy

Kind of change: **`add-*`** (new feature — local persistence + migration
seam). Test coverage is non-negotiable for the persistence and migration
paths.

Framework: xUnit v2, mirroring `SessionServiceTests` in
`tests/ProoveGaming.App.Tests/`. New file locations:

- `tests/ProoveGaming.App.Tests/LocalStorageServiceSettingsTests.cs`
- `tests/ProoveGaming.App.Tests/SettingsRepositoryTests.cs`

Setup pattern: use `Microsoft.Data.Sqlite` **in-memory** (`Data
Source=:memory:;Cache=Shared`) so each test gets an isolated DB. Extract a
small factory `LocalStorageService.CreateForTests(SqliteConnection open)` if
the current ctor closes over a file path — otherwise pass a connection
string.

Tests to add (must-cover for the acceptance criteria above):

`LocalStorageServiceSettingsTests`:

1. `UpsertDeviceSettingAsync_Insert_ThenRead_ReturnsSamePayload` — round-trip
   for each category (`rgb`, `dpi_presets`, `dpi_active`, `polling_rate`,
   `mouse_settings`).
2. `UpsertDeviceSettingAsync_Upsert_OverwritesPreviousPayload` — same
   composite key twice, second value wins.
3. `UpsertDeviceSettingAsync_DifferentOwners_DoNotCollide` — same
   `(vid,pid,category)` written for a guest owner and a user owner both
   round-trip independently.
4. `MigrateGuestToUserAsync_MovesGuestRowsToUser` — writes a `device_settings`
   row under `('guest', UUID)`, calls migrate, verifies the row's
   `(owner_kind, owner_id)` is now `('user', customerId.ToString())`.
5. `MigrateGuestToUserAsync_GuestWinsOnCollision` — pre-seed a user row for
   `(vid, pid, 'rgb')` with payload A, seed a guest row with payload B,
   migrate → the surviving row's payload is B (D1).
6. `MigrateGuestToUserAsync_LeavesOtherUsersUntouched` — a second customer's
   rows are unaffected by the migration.
7. `ClearData_DeletesGuestSettingsRows_LeavesUserSettingsRows`.
8. `ReadDeviceSettingAsync_UnknownKey_ReturnsNull`.
9. `MigrateGuestToUserAsync_WithNoGuestRows_IsANoOp` (does not throw).

`SettingsRepositoryTests`:

10. Round-trip test per category with realistic payload shapes (RGB with
    multi-effect dictionary, DPI 7-stage list, polling rate, mouse
    settings, app preferences).
11. `LoadRgbAsync_WithNoRow_ReturnsNull` (VM falls back to defaults).
12. `SaveRgbAsync_WhenOwnerIsNull_DoesNotThrowAndSkipsWrite` — repository
    tolerates the pre-guest state (no session yet).
13. `Owner_ChangesFromGuestToUser_ReadsPickUpTheNewOwner` — using a
    controllable `ICurrentOwnerProvider` fake.
14. `Owner_ChangesFromUserToNewGuest_ReadsReturnNullForNewGuest` — seed
    rows under `('user', C1)`, flip the fake to `('guest', UUID2)`, assert
    every `Load*Async` returns `null` / empty (AC11).

Not covered by automated tests (deliberate):

- Full sign-in end-to-end flow that walks `MainWindow.OnSignInSucceeded` —
  needs Avalonia UI thread, main window construction, `IAuthApiService`,
  and gRPC. Verify manually per the QA section.
- `SettingsSyncService` stub — logs only; assertion would test the logger,
  not behavior. Skipped.
- Race between debounced device write and DB write — both are chained off
  the same `await` in `ApplyRgbSettingsAsync`; visual inspection of the VM
  diff is sufficient.

Explicit ordering (per architect rules for `add-*` changes): `LocalStorageService`
and `SettingsRepository` tests must land in the same PR as the
implementation — this is coverage, not "add later".

## QA (manual test steps)

Copy this section verbatim into the PG-130 ticket for the QA engineer.

1. Launch the App fresh (no prior session on this machine). Click **Continue as
   guest**. Connect a BLAZE mouse.
2. Open the mouse card. Navigate to **RGB Lighting**. Pick the **Breathing**
   effect. Change the brightness slider to ~30% and the colour to red. Wait 1 s.
   Expected: the mouse LED shows the new state.
3. Navigate to **DPI**. Change one preset value (e.g. stage 3 from 1600 to 2200)
   and click **Apply**. Expected: the DPI button on the mouse cycles include
   2200.
4. Navigate to **Polling rate**. Click **500 Hz**. Expected: the 500 Hz pill
   highlights.
5. Navigate to **Settings**. Set **Button debounce** to ~20 ms and
   **Lift-off distance** to 3 mm. Wait 1 s.
6. Close the App fully (tray → Exit).
7. Reopen the App. Continue as guest (should auto-restore into the guest home).
   Open the same mouse. Expected: the RGB, DPI presets, polling rate, and BLAZE
   settings you changed in steps 2–5 are all showing your previous values — no
   defaults.
8. Sign up a new account through the Welcome tab. Complete the nickname flow.
   Expected: after landing on Home, the mouse card is present, and opening
   the mouse card shows the exact same RGB / DPI / polling / mouse settings
   you had as guest (migration succeeded).
9. Close the App. Reopen it. You should stay signed in (Remember-me was
   defaulted on). Open the mouse. Expected: settings are still there.
10. Sign out. Continue as guest again. Change the RGB brightness to ~80%.
    Sign in with the account from step 8. Expected: the RGB brightness now
    shows 80% (the guest's fresh change won over the user's older stored 30%
    from step 2 — this is the documented "guest wins" resolution).
11. Wait for the guest-session expiry (or edit `SessionTimeHours` in the debug
    build) and let the tray notification fire. Expected: on next launch, no
    guest-side settings from that expired session remain, but any signed-in
    user's settings are unaffected.

## Review focus

- `async/await` — every new repository method is async; every caller uses
  `await`; no sync-over-async on hot paths.
- `DI lifetimes` — new services registered as singletons; the shared
  `LocalStorageService` connection stays a single owner.
- `null-safety` — `ICurrentOwnerProvider` returns `null` before any session
  exists; every repository method handles this without throwing.
- `resource disposal` — no new `IDisposable` beyond what
  `LocalStorageService` already owns; do not open a second `SqliteConnection`.

## Performance & quality

- **Hot paths.** The persistence writes ride the *existing* 300 ms debouncers
  in `DeviceDetailsViewModel.Rgb.cs` and `Settings.cs` — no new timer, no
  per-slider-tick DB write. RGB animation timers (`DispatcherTimer` @ 33 ms)
  MUST NOT touch the DB; they compute the glow brush only.
- **Allocations.** Payload records are small structs/records serialised with
  `System.Text.Json`; do not build one-off `Dictionary` per write in the
  animation loop. RGB save runs once per debounce, not per frame.
- **UI thread.** All DB access is `async` with `ConfigureAwait` behavior
  matching the rest of the App (no `ConfigureAwait(false)` — the App code
  currently doesn't use it, do not diverge). VM writes are fire-and-forget
  `_ = SaveXAsync(...)` inside handlers that already accept
  fire-and-forget (matches how `ApplyRgbSettingsAsync` is used today).
- **Shared state.** No new singletons hold cross-thread mutable state.
  `LocalStorageService` is already singleton and single-connection; SQLite
  serialises writes internally, which is fine at these rates.
- **Startup cost.** `EnsureSchema` runs once per launch; the two new
  `CREATE TABLE IF NOT EXISTS` statements are microseconds. No measurable
  startup impact.

## Risks

- **Signing in as a customer with pre-existing local settings** — D1 says
  guest wins, which means a user's pre-existing local values are overwritten
  by whatever the guest changed in this session. Documented; alternative
  needs a future modal or server-side reconciliation.
- **Same customer, two machines** — local DB is not synchronized between
  installations. Users will see per-machine settings until server sync is
  built. Acceptable and documented as follow-up.
- **Migration idempotency** — if `OnSignInSucceeded` fires twice (unlikely
  but possible on double-tap), the second migration must be a no-op because
  the guest rows are already gone. Handled by the `WHERE owner_kind='guest'
  AND owner_id=$sessionId` filter.
- **Schema drift as more devices are added** — the `payload_json` blob
  shape is per-category; adding a new category (e.g. keyboard-per-key RGB)
  is additive. Do NOT reshape existing categories without a schema
  migration.
- **`SessionService.SessionId` cleared before migration** — must capture
  `sessionId` before `EndSession()` in every migration call site. Failing
  to do so leaves guest rows behind under the pre-clear UUID, invisible to
  the customer forever.
- **Testing races** — in-memory SQLite `:memory:` is per-connection unless
  `Cache=Shared` is used; pick one convention and stick to it in tests.
- **`Firmware` / `Macros` sections currently stub-only** — resist the temptation
  to persist them "while we're here". Out of scope; they aren't wired to the
  device yet.

## Open questions — resolved (2026-07-13)

1. **Server sync scope** — RESOLVED: local-only for step 1.
   `SettingsSyncService` stays a logging stub in this PR. Server-push is a
   follow-up task once the backend endpoint exists.
2. **`Microsoft.Data.Sqlite` async** — RESOLVED: accept as-is. Do not wrap
   in `Task.Run`. Writes are tiny and rare (debounced), so any internal
   trampolining is not worth the wrapper. Revisit only if profiling flags it.
3. **Guest vs user "same account" collision** — RESOLVED: **D1 always
   wins** — the guest's values overwrite any pre-existing user rows for the
   same `(vid, pid, category)`. No per-setting "was touched" tracking.
4. **Generic App preferences (Scroll / DoubleClick / Sleep sliders)** —
   RESOLVED: persist them. Included in the `app_preferences` table and the
   `AppPreferencesPayload` DTO as specified in the file-level change list.
5. **User → guest (logout) carry-over** — RESOLVED: **new guest is blank.**
   Logging out starts a fresh guest session with a fresh UUID; no user rows
   are copied into the new guest owner. The existing sign-out path is
   sufficient — no additional code needed for this decision.

## Review findings

### Round 1 — Ship: yes

#### Code
- [ ] `LoadRgbStateForCurrentDeviceAsync` awaits the RGB load BEFORE cancelling `_sliderDebounceCts`. A slider drag from the previous device that hadn't debounced yet can now fire between the device flip in `SetDevice` and the async load returning; the fired `ApplyRgbSettingsAsync` reads the new `Device` and writes NEW-device coords with STALE effect state. — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Rgb.cs:236-248` (approx.) — Should-fix
- [ ] `MainWindow.OnSignInSucceeded` / `OnNicknameChosen` sign-in race: `WelcomeViewModel.SignInAsync` writes `_userProfile.CustomerId` **before** raising `SignInSucceeded`. During the `await _localStorage.MigrateGuestToUserAsync(...)`, `CurrentOwnerProvider.GetCurrent()` already returns `("user", customerId)` while the guest session is still active. A late debounced write from a slider drag in flight when Sign In was clicked lands in USER rows; if the corresponding category was also a guest row, the migration deletes it and the late write is lost (last-writer-loses in a narrow window). — `src/ProoveGaming.App/Views/MainWindow.axaml.cs:243-257` and `338-348` — Should-fix (unlikely in practice; document + drain in-flight debouncers before the migrate call, or delay the CustomerId flip until after migrate)
- [ ] `EnsureAppPreferencesLoadedAsync` sets `_appPreferencesLoaded = true` **before** the DB read completes. If `_settings.LoadAppPreferencesAsync()` throws (corrupt JSON, e.g.), the flag stays flipped and no future `SetDevice` call retries. Set the flag only after successful application (or after a `null` load which is a legitimate "no row" outcome). — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Settings.cs:322-329` (approx.) — Should-fix
- [ ] `LoadRgbStateForCurrentDeviceAsync` ends with `await ApplyRgbSettingsAsync()` for keyboards, and `ApplyRgbSettingsAsync` now also persists via `SaveRgbStateForCurrentDevice()`. Net result: immediately after loading persisted state, we write it back to the DB unchanged. Redundant but harmless. — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Rgb.cs:518-522` (approx.) — Nit
- [ ] DTOs don't defend against corrupted JSON: `RgbDeviceSettings.Effects` and `DpiPresetsSettings.Stages` are non-nullable but `System.Text.Json` can deserialize them as `null` from a manually-edited or truncated blob. `ApplyRgbState` would NRE on `state.Effects.TryGetValue(...)`. Add a default value or explicit null-guard on load. — `src/ProoveGaming.App/Models/Settings/RgbDeviceSettings.cs` and `.../ViewModels/DeviceDetails/DeviceDetailsViewModel.Rgb.cs:ApplyRgbState` — Nit
- [ ] `Device` setter is `public` but `LoadPersistedDeviceStateAsync` only runs from `SetDevice`. A caller that assigns `Device = x` directly bypasses persistence load. No such caller exists today; still worth tightening the setter to `internal set` or moving the load into the setter to make the invariant explicit. — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs` — Nit

#### Tests
- [ ] No test asserts that `MigrateGuestToUserAsync` also re-keys the `app_preferences` row when only guest prefs exist (the existing multi-row test covers combined state but does not exercise the standalone app-preferences code path in isolation) — add `MigrateGuestToUserAsync_MovesGuestAppPreferencesWithNoDeviceSettings`.
- [ ] No test covers the ordering claim in AC8 (`ClearData` is NOT called on sign-in). A `MainWindow`-level test isn't feasible without Avalonia, but a `SettingsRepository`/`LocalStorageService` interaction test that seeds guest device rows, invokes the migration path, and asserts the user-owned rows survive would give confidence. Partially covered by `MigrateGuestToUserAsync_MovesGuestRowsToUser`; add an assertion that verifies user rows *not* related to the guest UUID are unaffected in the same test to explicitly guard AC8's spirit.
- [ ] `SettingsSyncService` stub — plan explicitly opts out; leave as-is.

