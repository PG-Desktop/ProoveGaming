# Plan: Fix "Remember me" being defeated by a stale guest session

> **Note (2026-09-24):** the sign-in, registration, sign-out and guest-expiry logic this plan places in `MainWindow.axaml.cs` now lives in `src/ProoveGaming.App/Services/AuthSessionCoordinator.cs` ([`refactor-auth-session-flow`](../refactor-auth-session-flow/refactor-auth-session-flow-plan.md)). Same statements, same order; `MainWindow` keeps the navigation. Name map: `OnSignInSucceeded` → `CompleteSignInStateAsync` + `FinishSignInAsync`; `OnNicknameChosen` → `RegisterAsync` + `FinishRegistrationAsync`; `OnLogoutRequested` → `LogoutAsync`; `SignOutToFreshSignIn` → `SignOut`; `OnTokenRejected`'s check → `IsCurrentToken`; `OnSessionExpired` → `HandleGuestSessionExpired`; `OnGuestContinueRequested` → `StartGuestSession`; `ClearSignedInState`, `PersistAuthIfTokenPresent` and `EnqueueStorageWrite` moved as they were.

## Goal

A user who signs in with "Remember me" gets thrown back to the sign-in/sign-up
screen even though their auth is correctly persisted. Diagnosis (verified
against a live `proovegaming.db`): signing in never deletes the persisted guest
`app_session` row — `MainWindow.OnSignInSucceeded` calls
`SessionService.EndSession()`, which is memory-only, and the only code that
deletes the row is `LocalStorageService.ClearData()`. On the next launch,
`App.OnFrameworkInitializationCompleted` sees the stale row, judges it Active
(younger than `SessionTimeHours` = 48h), and calls `RestoreSession(...)`, which
sets `IsGuest = true` and re-arms the guest expiry timer — while also restoring
the remember-me auth. The app is now a signed-in/guest hybrid. When the window
hides to tray (default close behaviour), `SessionService.GoToTray` fires
`SessionExpired` (because `IsGuest` is wrongly true and either all devices are
disconnected or the old timer has elapsed), and `MainWindow.OnSessionExpired`
unconditionally kicks to the Welcome screen — it never checks that a signed-in
user is active. Re-activating the app (single-instance restore from tray, which
users experience as "restarting the app") lands them on the auth screen. The
`saved_auth` row survives (as observed), because `ClearData` doesn't touch it.
Fix: clear the persisted session on sign-in, never resurrect a guest session
when valid saved auth exists, and make guest-session expiry a no-op for a
signed-in user.

## Scope

**In:**

- New `LocalStorageService.ClearSession()` — deletes the session-scoped rows
  (`app_session`, `devices`), leaving `saved_auth` and all owner-scoped guest
  data (`device_settings`, `macros`, presets, …) untouched. This is the
  session-only subset of `ClearData()`.
- Call `ClearSession()` at the two sign-in completion points in `MainWindow`
  (`OnSignInSucceeded`, and `OnNicknameChosen` **after**
  `MigrateGuestToUserAsync` has consumed the guest session id).
- Startup ordering fix in `App.OnFrameworkInitializationCompleted`: read
  `saved_auth` **before** deciding what to do with the persisted session. With
  valid saved auth, never `RestoreSession` — drop the stale row instead
  (`ClearSession` when the row is Active — do not wipe guest preference data;
  keep the existing `ClearData` for the Expired case). Without saved auth,
  behaviour is unchanged. This also **migrates installs already in the broken
  state** (stale row written before this fix ships).
- Defense-in-depth guard in `MainWindow.OnSessionExpired`: when a session token
  is present, end the phantom guest session silently (`EndSession` +
  `ClearSession`) and keep the user where they are — no `ClearData`, no kick to
  Welcome.
- Unit tests for the new storage seam + a characterization test documenting why
  the explicit clear is needed.
- `docs/CONTEXT.md` glossary entries for the new/clarified terms.

**Out:**

- Any change to `SessionService` itself — its guest timer/tray semantics are
  correct once it is no longer resurrected for signed-in users.
- Guest-session UX changes (48h TTL, tray expiry rules for real guests) —
  unchanged.
- Token validation against the server at startup — `LoadFromServerAsync`
  already handles `INVALID_OR_EXPIRED_TOKEN` and that path (which clears
  `saved_auth`) is not this bug.
- The `ShowSignUpTab()` choice inside `OnSessionExpired` for genuine guest
  expiry — pre-existing UX, untouched.

## Options considered

### A. Where to break the guest/signed-in hybrid

- **A1 — Clear the persisted session at sign-in AND guard startup restore
  (recommended).** Fix the root (row left behind) and the amplifier (startup
  resurrects it). Two small call-site changes plus one storage method. The
  startup guard is also the only thing that heals installs that already have a
  stale row — the sign-in fix alone would leave them broken until the row ages
  past 48h.
- **A2 — Only guard `OnSessionExpired` (symptom fix).** One-line check, but the
  app would still restore `IsGuest = true` alongside a signed-in profile —
  every future consumer of `SessionService.IsGuest` (tray logic, guest menus,
  `HomeViewModel` guest UI) keeps seeing a phantom guest. Fragile.
- **A3 — Make `EndSession` itself delete the DB row.** Tempting single choke
  point, but `SessionService` deliberately has no storage dependency (it is
  constructible with just a dispatcher — see `SessionServiceTests`), and
  `OnSessionExpired` calls `EndSession` right after `ClearData` already removed
  the row. Wiring storage into it inverts the existing layering for no gain.

**Pick A1**, plus the `OnSessionExpired` guard from A2 as defense-in-depth (it
is one `if` and protects against any future path that re-arms the timer).

### B. What `ClearSession` deletes

- **B1 — `app_session` + `devices` (recommended).** Mirrors exactly the
  session-scoped (non-owner-scoped) subset of `ClearData`. Leaves no orphaned
  device rows: persisted devices are only ever read back through the
  session-restore path (`GetDevices` at startup) and only flushed for guest
  sessions on exit, so they are meaningless without a session row.
- **B2 — `app_session` only.** Smaller statement, but leaves orphaned `devices`
  rows that would be silently re-attached to the *next* guest session restore,
  showing a previous guest's device cards. Worse consistency for one saved
  line.

**Pick B1.**

### C. Startup handling of an Active stale row when saved auth is valid

- **C1 — `ClearSession()` (recommended).** The row is garbage, but the
  guest-owned preference/macro rows may still be wanted the next time someone
  uses guest mode on this machine; `ClearData`'s guest-data wipe is the
  *expiry* contract, not the *sign-in* contract (sign-in deliberately does not
  migrate or destroy guest data — see the comment in `OnSignInSucceeded`).
- **C2 — `ClearData()`.** Simpler mental model ("any stale session dies
  fully"), but destroys guest data as a side effect of merely signing in, which
  contradicts the documented decision that sign-in leaves guest rows alone.

**Pick C1.**

## Recommended approach

### 1. `LocalStorageService.ClearSession()` (new)

Next to `InitializeSession` / `GetSessionState` in the `── Session ──` region:

```csharp
// Removes the persisted session and its session-scoped device list without
// touching saved_auth or owner-scoped guest data. Sign-in calls this so a
// finished guest session cannot be resurrected on the next launch; ClearData
// remains the guest-expiry wipe (session + guest-owned rows).
public void ClearSession()
{
    AcquireLock();
    try
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            DELETE FROM app_session;
            DELETE FROM devices;
            """;
        cmd.ExecuteNonQuery();
    }
    finally { _lock.Release(); }
}
```

*(2026-08-17, PR #107 review m8: the two DELETEs are now wrapped in one
`BeginTransaction()`/`Commit()` like `ClearData` — without it each statement
autocommits and a failure between them leaves orphaned `devices` rows, the exact
state Option B2 above was rejected for.)*

*(2026-08-19, superseded detail: `devices` became owner-scoped so a signed-in
user's cards survive restarts — see
`docs/plans/persist-devices-and-notifications/`. The `DELETE FROM devices` in
`ClearSession` and `ClearData` is now `WHERE owner_kind = 'guest'`: B1's
"no orphans" reasoning still holds for guest rows, while user-owned rows must
outlive the sign-in that calls this.)*

### 2. Sign-in completion clears the persisted session (`MainWindow.axaml.cs`)

- `OnSignInSucceeded`: add `_localStorage.ClearSession();` immediately after
  `_session.EndSession();`.
- `OnNicknameChosen` (registration path): add `_localStorage.ClearSession();`
  after the `MigrateGuestToUserAsync` block and `_session.EndSession();` — the
  migration must read the guest session id first.

### 3. Startup: saved auth wins over a persisted guest session (`App.axaml.cs`)

Reorder `OnFrameworkInitializationCompleted` so the saved-auth read comes
first, and gate the session restore on its absence:

```csharp
var localStorage = _services.GetRequiredService<LocalStorageService>();

// Restore "Remember me" authentication if still valid — BEFORE touching the
// persisted guest session: a signed-in launch must never resurrect one.
var savedAuth = localStorage.GetSavedAuth();
var hasValidAuth = savedAuth != null && !savedAuth.IsExpired;
if (savedAuth != null && savedAuth.IsExpired)
    localStorage.ClearSavedAuth();
if (hasValidAuth)
{
    var userProfile = _services.GetRequiredService<UserProfile>();
    userProfile.CustomerId = savedAuth!.CustomerId;
    // …same field copies as today…
}

var (sessionState, sessionUuid, startedAt) = localStorage.GetSessionState();
if (sessionState == SessionState.Expired)
{
    localStorage.ClearData();                    // unchanged: guest expiry wipe
}
else if (sessionState == SessionState.Active && sessionUuid.HasValue)
{
    if (hasValidAuth)
        localStorage.ClearSession();             // stale pre-fix row: drop, don't restore
    else
        _services.GetRequiredService<SessionService>()
            .RestoreSession(sessionUuid.Value, localStorage.GetDevices(), startedAt);
}
```

The `hasValidAuth`-gated `ClearSession` is what repairs installs that signed
in before this fix and still carry the stale row.

### 4. Guard in `MainWindow.OnSessionExpired`

```csharp
private void OnSessionExpired()
{
    // Guest-session expiry must never sign out an authenticated user. With a
    // token present the guest session is a phantom (pre-fix stale row or a
    // future regression) — dispose of it silently and stay where we are.
    if (!string.IsNullOrEmpty(_userProfile.SessionToken))
    {
        _session.EndSession();
        _localStorage.ClearSession();
        return;
    }

    _localStorage.ClearData();
    _session.EndSession();
    GetWelcomeView().ShowSignUpTab();
    _currentStep = AppStep.Welcome;
    UpdateStepUi();
}
```

## File-level change list

Ordered tests-first where the harness allows (see Test strategy for why the
end-to-end repro itself is manual).

1. **`tests/ProoveGaming.App.Tests/LocalStorageServiceSessionTests.cs`** — new.
   Characterization test pinning the defect at the storage seam
   (`SaveAuth_DoesNotRemovePersistedSession` — documents that sign-in must
   clear explicitly) plus the `ClearSession` invariants (see Test strategy).
   The `ClearSession` tests fail on current code (method absent) and pass after
   change 2.
2. **`src/ProoveGaming.App/Services/LocalStorageService.cs`** — add
   `ClearSession()` in the Session region as specified above.
3. **`src/ProoveGaming.App/Views/MainWindow.axaml.cs`** — call `ClearSession()`
   in `OnSignInSucceeded` and `OnNicknameChosen`; add the signed-in guard to
   `OnSessionExpired`.
4. **`src/ProoveGaming.App/App.axaml.cs`** — reorder saved-auth restore before
   session-state handling; gate `RestoreSession` on the absence of valid saved
   auth; `ClearSession()` the stale Active row when auth is valid.
5. **`docs/CONTEXT.md`** — glossary: **Guest session (`app_session`)** — the
   persisted 48h guest session row; created by "Continue as guest", removed by
   sign-in (`ClearSession`) or expiry (`ClearData`); never coexists with a
   restored remember-me auth. Cross-link from the existing **Session Token**
   entry is not needed (different credential); add **ClearSession vs
   ClearData** one-liner.

## Acceptance criteria

- **AC1** Given a guest session row exists and the user signs in successfully
  (sign-in tab), the `app_session` and `devices` rows are deleted from the DB
  while `saved_auth` (when "Remember me" is checked) and guest-owned rows
  (`device_settings` / `macros` / presets with `owner_kind='guest'`) remain.
- **AC2** Given the user registered via the guest→sign-up path, after the
  guest-data migration completes the `app_session` row is likewise deleted.
- **AC3** Given valid (non-expired) `saved_auth` and an Active-looking stale
  `app_session` row in the DB, when the app starts:
  `SessionService.IsGuest` stays `false`, no guest timer is armed, the stale
  row is deleted, and the user lands on Home signed in.
- **AC4** Given valid `saved_auth` and no session row, app start lands on Home
  signed in (regression guard — unchanged behaviour).
- **AC5** Given no `saved_auth` and an Active session row, app start restores
  the guest session exactly as before (`IsGuest == true`, devices restored,
  timer armed from the persisted `started_at`).
- **AC6** Given no `saved_auth` and an Expired session row, app start calls
  `ClearData` exactly as before.
- **AC7** Given a signed-in user (token present), if `SessionExpired` fires for
  any reason, the UI does not navigate away from the current step, `ClearData`
  is not called, and the phantom session state is discarded (`SessionId`
  becomes empty, `app_session` row deleted).
- **AC8** Signed-in user with "Remember me": close the window to tray with no
  devices connected, then re-activate the app — the Home screen is shown still
  signed in (pre-fix: Welcome/sign-up screen). Full process restart likewise
  lands on Home signed in.
- **AC9** Genuine guest flow is unaffected: guest session expiry (row older
  than `SessionTimeHours`) with no token still wipes guest data and shows the
  Welcome sign-up tab when surfacing from tray.
- **AC10** `dotnet build ./src/ProveGaming.slnx` succeeds;
  `dotnet test ./src/ProveGaming.slnx` is green, including the unchanged
  `SessionServiceTests`.

## Test strategy

Kind of change: **bug fix (`fix-*`)**.

**Repro-first caveat, stated honestly:** the end-to-end repro (sign in →
tray → re-activate → auth screen) runs through `MainWindow` and
`App.OnFrameworkInitializationCompleted`, neither of which is constructible in
the existing headless xUnit harness (no Avalonia headless test infrastructure
in `tests/`). The automated repro therefore pins the bug at its storage seam,
and the UI-flow repro lives in QA steps 1–3 below, which must be executed
before and after the fix.

New file `tests/ProoveGaming.App.Tests/LocalStorageServiceSessionTests.cs`
(in-memory `LocalStorageService("Data Source=:memory:")`, mirroring the other
`LocalStorageService*Tests` files):

1. `SaveAuth_DoesNotRemovePersistedSession` — **the storage-level bug
   characterization, written first and passing on current code**: after
   `InitializeSession(...)` + `SaveAuth(...)`, `GetSessionState()` still
   returns Active. This documents that nothing implicit cleans the row —
   the reason the explicit `ClearSession` calls must exist.
2. `ClearSession_RemovesSessionRow` — `InitializeSession` → `ClearSession` →
   `GetSessionState()` returns `SessionState.None`. (Fails on current code:
   the method does not exist until the fix lands.)
3. `ClearSession_RemovesPersistedDevices` — persist a device (same API the
   session flush uses), `ClearSession`, `GetDevices()` is empty.
4. `ClearSession_KeepsSavedAuth` — `SaveAuth` + `ClearSession` →
   `GetSavedAuth()` still returns the row.
5. `ClearSession_KeepsGuestOwnedRows` — write one guest-owned row through an
   existing public API (pick the simplest, e.g. device settings or a macro with
   `owner_kind='guest'`), `ClearSession`, the row is still readable.

Existing tests that must stay green unmodified: `SessionServiceTests`
(SessionService is untouched), all `LocalStorageService*Tests`.

Deliberately **not** covered by automated tests, and why:

- The startup ordering in `App.axaml.cs` and the `OnSessionExpired` guard in
  `MainWindow.axaml.cs` — both live in Avalonia types the harness cannot
  instantiate; covered by QA steps 2–3 and 5–6. If a headless-Avalonia harness
  is ever added, AC3/AC7 are the first candidates to automate.
- The tray/single-instance activation path — OS-level, manual only.

## QA (manual test steps)

Precondition: a test account, app installed, no devices connected (this makes
the bug easiest to trigger).

1. **Repro before the fix (once, to confirm the scenario):** launch the app,
   sign in with a valid account and "Remember me" checked. Close the window
   with the X button (the app goes to the tray). Click the app icon / tray icon
   to bring it back. Expected on the broken build: the sign-in/sign-up screen
   appears instead of the Home screen.
2. **Main fix:** on the fixed build repeat step 1 exactly. Expected: the window
   comes back on the Home screen, still signed in.
3. **Real restart:** while signed in with "Remember me", fully exit the app
   (tray icon → exit, or end the process), then launch it again. Expected: the
   app opens on the Home screen already signed in — no sign-in form.
4. **Upgrade of a broken install:** on a machine that saw the bug before the
   fix (signed in earlier, still gets the auth screen), install the fixed
   build and launch. Expected: opens on Home signed in; closing to tray and
   returning keeps it signed in.
5. **Guest flow unaffected:** sign out. Choose "Continue as guest", note the
   Home screen works, close to tray with no devices connected, bring the app
   back. Expected: guest is returned to the sign-up screen (this is the
   intended guest behaviour, unchanged).
6. **Guest expiry unaffected:** (test build only) set `SessionTimeHours` to a
   small value in the app configuration, start a guest session, wait past the
   limit, close to tray and bring the app back. Expected: sign-up screen, guest
   settings reset.
7. **Remember me off:** sign out, sign in with "Remember me" unchecked, fully
   exit and relaunch. Expected: the sign-in form appears (nothing is
   remembered except the pre-filled email) — unchanged behaviour.

## Review focus

- `thread-safety` — `ClearSession` uses the same `AcquireLock` discipline as
  its neighbours; `OnSessionExpired` guard runs on the UI thread via the
  existing dispatcher post.
- `UI-thread affinity` — the guard must not introduce blocking storage work on
  a hot UI path (`ClearSession` is two DELETEs on an idle-path event; fine, but
  verify it is not called per-frame anywhere).
- `null-safety` — `savedAuth!` dereference after the `hasValidAuth` check in
  the reordered startup block; `sessionUuid.HasValue` gating.

## Performance & quality

No hot paths. `ClearSession` executes two DELETE statements on user-driven
events (sign-in, startup, a rare expiry) — negligible. Startup adds no extra
DB round-trips: the same two reads (`GetSavedAuth`, `GetSessionState`) happen
in reversed order. No new allocations, no new shared state; `SessionService`'s
threading model is untouched.

## Risks

- **Devices table coupling.** `ClearSession` deletes `devices`; if any future
  feature starts persisting signed-in users' device cards in that table, this
  delete becomes wrong. Today only the guest flush writes it (verified:
  `Flush` runs only when `CurrentSession != null`), so the risk is
  forward-looking only — the CONTEXT.md entry records the contract.
- **Ordering inside `OnNicknameChosen`.** `ClearSession` must run after
  `MigrateGuestToUserAsync` (which needs the guest session id) — the change
  list pins the position; reviewer should verify it.
- **Startup regression for pure guests.** The reorder must leave the
  no-saved-auth branches byte-for-byte equivalent (AC5/AC6); the risk is a
  subtle condition inversion — covered by QA step 5–6 since the harness cannot
  execute this code.
- **Users mid-bug during upgrade.** Handled by the startup `ClearSession` when
  valid auth exists (AC3, QA step 4); without it the fix only helps future
  sign-ins.

## Open questions

- None. The guest-expiry UX (sign-up tab on genuine expiry) and the sign-in
  no-migration rule are kept exactly as documented in the existing code
  comments; no product decision is needed.

## Review findings

### Round 1 — Ship: yes

#### Plan conformance

Plan: `docs/plans/fix-remember-me-guest-session/fix-remember-me-guest-session-plan.md`
- Implemented as planned: `LocalStorageService.ClearSession()` (session-scoped subset of
  `ClearData`, same `AcquireLock` discipline); `ClearSession` calls at both sign-in
  completion points (`OnSignInSucceeded`, and `OnNicknameChosen` **after**
  `MigrateGuestToUserAsync` — ordering verified); startup reorder in `App.axaml.cs` with
  the `hasValidAuth` gate and stale-row `ClearSession`; the `OnSessionExpired` signed-in
  guard; `docs/CONTEXT.md` glossary entry; new
  `tests/ProoveGaming.App.Tests/LocalStorageServiceSessionTests.cs` with the storage-level
  characterization + four `ClearSession` invariants, exactly as the Test strategy lists.
- Deviates from plan: none of substance. The startup block uses
  `if (expired) ClearSavedAuth(); else if (hasValidAuth) {…}` where the plan sketch had
  two independent `if`s — the branches are mutually exclusive, so behaviour is identical.
- Missing / partial: none.
- Accepted limitation (per plan): the startup decision (`App.axaml.cs`) and the
  `OnSessionExpired` guard (`MainWindow.axaml.cs`) have no automated coverage — no
  headless-Avalonia harness exists. Residual risk: AC3–AC7 regressions would only surface
  via QA steps 2–6; the storage seam alone cannot catch a future condition inversion in
  the startup branch. Verified by reading that all four branch combinations
  (auth × session state) reduce to the plan's table.
- Acceptance criteria: AC1/AC2 (storage effects of sign-in paths) hold by code reading +
  the `ClearSession` invariant tests; AC3–AC7 verified by reading against the plan's
  branch table; AC8/AC9 are the manual QA contract; AC10 — 795/795 tests green, App +
  test projects build clean (full `slnx` build currently blocked only by a running
  `ProoveGaming.DeviceHost` process locking its own output DLL — environment, not code).

#### Code

- none — no Blockers, no Should-fix. Checks that came back clean:
  - Branch equivalence of the reordered startup block for the no-auth paths (AC5/AC6):
    `Expired → ClearData` and `Active+no-auth → RestoreSession(uuid, GetDevices(), startedAt)`
    are byte-for-byte the old calls — `src/ProoveGaming.App/App.axaml.cs:83-103`.
  - `OnSessionExpired` guard vs. genuine guest expiry: `ClearLocalState` (logout /
    token-bounce) resets `User.SessionToken` to empty, so a real guest's expiry still
    takes the wipe-and-Welcome path — `src/ProoveGaming.App/Views/MainWindow.axaml.cs:302`.
  - No device-row resurrection: `Flush` runs only when `CurrentSession != null` at exit,
    which is never the case after sign-in (`EndSession` precedes) —
    `src/ProoveGaming.App/Services/LocalStorageService.cs` (`Flush`).
  - `ClearSession` from a non-guest sign-in (no session row) is a harmless no-op DELETE.
  - Thread-safety: `ClearSession` follows the `AcquireLock`/`finally Release` pattern of
    every neighbour; all new call sites run on the UI thread like the sibling storage
    calls (`SaveLastSignIn`, `ClearData`) — consistent with the existing codebase pattern.
  - Null-safety: the `savedAuth!` at `App.axaml.cs:74` is guarded by `hasValidAuth`
    (which implies non-null); flagged in Review focus, verified correct.

#### Tests

- none required beyond the plan. The five new tests in
  `LocalStorageServiceSessionTests.cs` each exercise real behaviour (the characterization
  test pins the root cause; the invariants cover row/devices deletion and
  auth/guest-data preservation). Standing repo caveat, not new to this change: CI
  (`dotnet-ci.yml`) builds only the App csproj and does not execute `tests/` — these
  tests gate local runs only.

#### Nits

- `savedAuth != null && savedAuth.IsExpired` alongside `hasValidAuth` reads slightly
  redundantly; `if (savedAuth is { IsExpired: true })` / `is { IsExpired: false }` pattern
  matching would drop the `!` at `App.axaml.cs:74`. Cosmetic — the current form is correct.

#### What's done well

- The fix lands at all three layers the diagnosis named (root: sign-in cleanup;
  amplifier: startup gate that also heals already-broken installs; defense:
  expiry guard) without touching `SessionService` — its tests stay green unmodified.
- `ClearSession` vs `ClearData` contract is stated in code comments, the glossary, and
  the tests — the next person cannot easily confuse the sign-in cleanup with the expiry
  wipe.
- The storage-level characterization test (`SaveAuth_DoesNotRemovePersistedSession`)
  documents *why* the explicit calls exist, which is exactly what prevents a future
  "simplification" from re-introducing the bug.

#### Verdict

Ship: yes — no blockers, no should-fixes; the untestable startup/guard paths are an
accepted plan limitation gated by QA. First: 1) run QA steps 1–4 (tray repro + real
restart + broken-install upgrade) on a real build, 2) QA steps 5–6 to confirm genuine
guest expiry still behaves, 3) commit before further work piles into the working tree.
