# add-global-token-expiry — Centralized session-token rejection handling

> **Note (2026-09-24):** the sign-in, registration, sign-out and guest-expiry logic this plan places in `MainWindow.axaml.cs` now lives in `src/ProoveGaming.App/Services/AuthSessionCoordinator.cs` ([`refactor-auth-session-flow`](../refactor-auth-session-flow/refactor-auth-session-flow-plan.md)). Same statements, same order; `MainWindow` keeps the navigation. Name map: `OnSignInSucceeded` → `CompleteSignInStateAsync` + `FinishSignInAsync`; `OnNicknameChosen` → `RegisterAsync` + `FinishRegistrationAsync`; `OnLogoutRequested` → `LogoutAsync`; `SignOutToFreshSignIn` → `SignOut`; `OnTokenRejected`'s check → `IsCurrentToken`; `OnSessionExpired` → `HandleGuestSessionExpired`; `OnGuestContinueRequested` → `StartGuestSession`; `ClearSignedInState`, `PersistAuthIfTokenPresent` and `EnqueueStorageWrite` moved as they were.

Phase 3 of [add-api-v2-conformance](../add-api-v2-conformance/add-api-v2-conformance-plan.md).
Builds on `ApiHttpClient` from [add-api-v2-sync](../add-api-v2-sync/add-api-v2-sync-plan.md).

## Goal

API v2.0 gives session tokens a 30-day lifetime and invalidates all of them on a password
change (contract §11: the launcher then receives `INVALID_OR_EXPIRED_TOKEN` and must offer
re-login). Today only `ProfileViewModel` detects a rejected token — and only on its own
calls, each site hand-rolling the check before raising `SessionTokenInvalid`. Sync calls
(upgrade push, and the pull/live sync of phases 1–2) have no path to sign the user out at
all. Centralize detection in `ApiHttpClient` so every token-bearing request feeds one
handler that clears the session, informs the user, and lands them on Sign In.

## Scope

**In:**
- A `TokenRejected` event on `ApiHttpClient`, raised when a request that carried a Bearer
  token comes back (non-network) with `TOKEN_REQUIRED` or `INVALID_OR_EXPIRED_TOKEN`.
- `MainWindow` subscription funneling into the existing `OnSessionTokenInvalid` path
  (ClearSignedInState + fresh Sign In form), made idempotent against double-firing.
- A localized notification (en/uk) telling the user the session expired and they need to
  sign in again.
- Sync services stop quietly (the in-flight push aborts at the first token-rejected
  response; later passes no-op on the now-empty token; no extra error surfaces).

**Out:**
- Token refresh (the API has none — re-login is the only recovery).
- Changing `ProfileViewModel`'s per-call handling beyond preventing double-handling — its
  in-dialog flows (e.g. account deletion password errors) keep their local UX.
- Proactive expiry (signing out before the server says so; `SavedAuth.IsExpired` already
  gates the restore path).

## Options considered

### A. Keep per-call checks, add them to sync code too
- Pros: no new event plumbing.
- Cons: the same three-line check copy-pasted into every future call site; sync services
  would need a UI-facing dependency to trigger sign-out — inverted layering. Rejected.

### B. Detect in `ApiHttpClient`, notify via event (recommended)
The one place every API response already flows through (`ReadResponseAsync` sees the parsed
envelope and knows whether the request carried a token). An event keeps `ApiHttpClient`
UI-agnostic; `MainWindow` — which already owns the sign-out choreography — subscribes.
- Pros: zero-per-call-site cost, covers auth + sync + future clients automatically; the
  handler reuses tested sign-out code.
- Cons: an event from a pool thread must be marshaled to the UI thread (the existing
  `OnSessionTokenInvalid` already `Dispatcher.UIThread.Post`s — same pattern).

**Decision: B.**

## Recommended approach

1. `ApiHttpClient`: the POST/send helpers already know whether a token was attached. After
   the envelope is read, if `token was attached && !IsNetworkError && (ErrorCode or any
   Errors[].error_code is TOKEN_REQUIRED / INVALID_OR_EXPIRED_TOKEN)`, raise
   `event Action<string>? TokenRejected` carrying the rejected token — subscribers use it
   to drop stale signals from calls that stayed in flight across a sign-out/re-sign-in
   (fire once per response; exceptions from handlers must not propagate into the API
   call). Constants come from `AuthErrorCodes`.
2. Suppression flag: `ApiHttpClient` keeps a `_tokenRejectionSignaled` latch cleared by a
   new `ResetTokenRejectionLatch()` that `MainWindow` calls when a session starts (sign-in,
   restore, registration) — so one expiry burst (several concurrent calls all failing)
   raises one event, and the next session can signal again.
3. `MainWindow`: subscribe in the constructor next to `_profileViewModel.SessionTokenInvalid
   += OnSessionTokenInvalid` and route to the same handler; unsubscribe in the teardown
   block. The handler acts only when the rejected token still equals the current
   `UserProfile.SessionToken` (compared inside the `Dispatcher.UIThread.Post` so the read
   is UI-thread-affine) — a mismatch is a stale rejection of a previous session's token.
   Make `OnSessionTokenInvalid` idempotent (if already on Welcome/no session, only
   ensure the form state) — it can now fire from both the profile path and the global path.
4. Notification: in the handler, show a toast + bell entry via the existing
   `NotificationService` pattern with new keys `Session_ExpiredTitle` /
   `Session_ExpiredToast` in both `Strings.en.axaml` and `Strings.uk.axaml` ("Session
   expired — please sign in again" family). Emphasis rule: plain text, no colour-only cue.
5. Sync quiescence (amended 2026-08-25 per Round 1 S1): `SettingsSyncService.Aborts` also
   aborts the push when the response carries `TOKEN_REQUIRED` / `INVALID_OR_EXPIRED_TOKEN`
   (via `HasErrorCode`, so `errors[]` shapes count) — the in-flight push stops at the first
   rejected response with a Warning, no dedicated stop API. `ClearSignedInState` empties
   `UserProfile.SessionToken`; the `SettingsSyncService` guard and (phase 2)
   `SyncCoordinator` pass-start token checks make subsequent passes a zero-call no-op.
6. `ProfileViewModel` double-handling: its checks raise `SessionTokenInvalid` for the same
   response that now also raises `TokenRejected`. With the idempotent handler plus the
   latch, the visible result is one sign-out and one notification; do not rip out the
   per-call checks in this plan (they also gate dialog-specific UX).

## File-level change list

1. `src/ProoveGaming.App/Services/ApiHttpClient.cs` — edit: token-rejection detection,
   `TokenRejected` event (carries the rejected token), one-shot latch + reset method,
   internal `(HttpMessageHandler, string baseUrl)` test constructor.
2. `src/ProoveGaming.App/Views/MainWindow.axaml.cs` — edit: subscribe/unsubscribe; latch
   reset on session start; stale-token guard in the handler; idempotent
   `OnSessionTokenInvalid`; expiry notification.
2a. `src/ProoveGaming.App/Services/SettingsSyncService.cs` — edit (Round 1 S1): `Aborts`
   also aborts the push on token-rejection responses; null-entry-safe `HasErrorCode`.
3. `src/ProoveGaming.App/Resources/Strings.en.axaml`, `Strings.uk.axaml` — edit:
   `Session_ExpiredTitle`/`Session_ExpiredToast`.
4. `tests/ProoveGaming.App.Tests/ApiHttpClientTokenRejectionTests.cs` — new: see Test
   strategy.
5. `docs/CONTEXT.md` — edit: `Token rejection` entry.
6. `docs/plans/refactor-change-password-flow/refactor-change-password-flow-plan.md` — edit:
   dated note (the contract's password-change → forced re-login flow is now honored
   globally).

## Acceptance criteria

- [ ] Given any token-bearing request answered 401/`INVALID_OR_EXPIRED_TOKEN` (or
  `TOKEN_REQUIRED`, top-level or in `errors[]`), when the response is processed, then
  `TokenRejected` fires exactly once even if five concurrent requests fail the same way.
- [ ] Given a request without a token (login, register, contact as guest) fails with any
  error, when processed, then `TokenRejected` does not fire.
- [ ] Given a network error on a token-bearing request, then `TokenRejected` does not fire.
- [ ] Given `TokenRejected` fires while signed in, then the user lands on the Sign In tab
  with cleared fields, the session/saved-auth is cleared, and a localized "session expired"
  toast + bell entry appear (both languages resourced).
- [ ] Given the user signs in again after an expiry, when the new token is later rejected,
  then the flow triggers again (latch was reset).
- [ ] Given a sync push/pull is mid-pipeline when the token is rejected, then it aborts at
  the first rejected response — without an error dialog and without further API calls
  carrying the dead token.

## Test strategy

New feature (`add-*`) — in `tests/ProoveGaming.App.Tests`:

- `ApiHttpClientTokenRejectionTests` (new): drive `ApiHttpClient` against a stub
  `HttpMessageHandler` — event fires for top-level `error_code` and for `errors[]` shape;
  once-only under concurrent failures; not for token-less requests; not for network errors;
  latch reset re-arms; handler exception does not fault the API call.
- `MainWindow` wiring and the notification are UI plumbing — QA steps 1–3 (consistent with
  how `SessionTokenInvalid` wiring is already untested).
- Sync quiescence: one case in the existing `SettingsSyncServiceTests` — a token-rejected
  response mid-push aborts the pipeline at the rejected item (Warning, no Error) and no
  call happens after `ClearSignedInState`-shaped token emptying (extend the existing fake).

Deliberately not covered: real 30-day expiry timing; the website password-change end-to-end
(QA step 3).

## QA (manual test steps)

1. Sign in with "Remember me". Have the backend team invalidate the session token (or
   change the account password on the website, which invalidates all tokens). In the app,
   open Profile or make any account action. Expected: within a moment the app returns to
   the Sign In screen with empty fields, and a notification says the session expired and
   asks to sign in again (check in both English and Ukrainian UI).
2. Sign in again with the correct (new) password. Expected: normal Home screen; devices and
   data are present; no repeated expiry notification.
3. Change the password on the website while the app sits idle on Home overnight (or
   simulate). Expected: the next background sync or profile action signs the app out to
   Sign In with the notification — the app never silently keeps working with dead cloud
   sync.
4. As a guest, use Contact Us with the backend rejecting tokens. Expected: guest flows are
   unaffected (no sign-out screen, since there is no session).

## Review focus

1. `UI-thread affinity` — the event fires on a pool thread; every UI mutation in the handler
   goes through `Dispatcher.UIThread.Post` (the existing handler already does — verify the
   new notification path too).
2. `thread-safety` — the latch is raced by concurrent responses; use `Interlocked` so
   exactly one event fires.
3. `null-safety` — `errors[]` scanning on partially-bound envelopes.
4. `resource disposal` — event unsubscribed in `MainWindow` teardown (it outlives no one,
   but symmetry with the existing block).

## Performance & quality

None — no hot paths; one extra branch per API response.

## Risks

- **Double sign-out UX** if both `ProfileViewModel`'s local check and the global event act
  on one response — mitigated by the latch + idempotent handler; QA step 1 watches for a
  doubled toast.
- **False trigger during account deletion**: `profile/delete` succeeded → subsequent calls
  with the dead token would fire the global path while the app is already navigating away.
  The latch makes it one event, and the idempotent handler makes it a no-op on Welcome —
  reviewer should walk this path explicitly.

## Open questions

1. Should the expiry notification also appear when the rejection happens during a
   background sync with the window hidden in tray? Default: yes — the bell entry persists,
   the toast may go unseen; tray flash is out of scope.

## Review findings

### Round 1 (2026-08-25) — Ship: no

#### Summary
Working-tree delta vs `f366c1a` on `features/newapi`. Centralized token-rejection detection
in `ApiHttpClient` (`TokenRejected` event, `Interlocked` one-shot latch, `errors[]`-aware
code scan), `MainWindow` wiring into an idempotent shared `SignOutToFreshSignIn`, localized
en/uk "session expired" toast + bell entry, `docs/CONTEXT.md` glossary entry, dated note in
the refactor-change-password-flow plan, new `ApiHttpClientTokenRejectionTests` (7 tests)
plus one quiescence case in `SettingsSyncServiceTests`. All 19 affected tests pass locally.

#### Plan conformance
Plan: `docs/plans/add-global-token-expiry/add-global-token-expiry-plan.md`
- Implemented as planned: all 6 file-level items; latch reset on every session start
  (restore in the `MainWindow` ctor, `OnSignInSucceeded`, registration login in
  `OnNicknameChosen`); handler-exception isolation; `errors[]` null-entry tolerance;
  unsubscribe symmetry in `OnClosed`. Acceptance criteria 1–3 and 5 are test-covered;
  criterion 4 is QA per test strategy.
- Deviates from plan (verified sound): the expiry notification rides only the
  `OnTokenRejected` path (`MainWindow.axaml.cs:177–204`), not the shared handler —
  step 4 read as "in the handler", but the shared path also fires on successful account
  deletion, where a "session expired" toast would be wrong. Walked the deletion path
  (`ProfileViewModel.ConfirmDeleteAccount:932–937` raises `SessionTokenInvalid` on
  success → `notifySessionExpired: false`): the deviation is correct; keep it. Residual
  ordering race noted in Nits.
- Missing / partial: step 5's claim "in-flight loops abort on their next response check"
  is **false** for the upgrade push — `SettingsSyncService.Aborts` only aborts on
  `IsNetworkError`, so a token rejection warns-and-continues through every remaining item.
  Acceptance criterion 6 ("without further API calls carrying the dead token") is not met
  → S1. Reconcile the criterion wording when S1 lands.

#### Code
- [x] **S1. In-flight push keeps calling with the dead token; AC 6 unmet** —
  `src/ProoveGaming.App/Services/SettingsSyncService.cs:304–311` (`Aborts`) — Should-fix
  **Problem:** Only `IsNetworkError` aborts the pipeline. A `TOKEN_REQUIRED` /
  `INVALID_OR_EXPIRED_TOKEN` response is treated as an ordinary per-item rejection
  (warn-and-continue), so every remaining macro/preset/preference/device-state item still
  makes a doomed API call with the captured dead token.
  **Why it matters:** (a) violates acceptance criterion 6 as written; (b) the dead-token
  calls are not merely server noise: `OnSignInSucceeded` resets the rejection latch, so a
  doomed response landing *after* a quick re-sign-in re-fires `TokenRejected` and signs
  the user out of their brand-new valid session with a spurious "session expired" toast.
  **Ruling (per plan step 5's "verify in review"):** an in-flight stop IS required, in the
  cheap form — no new stop API. Amending AC 6 alone is not acceptable because of the
  latch-reset interaction.
  **Solution options:**
  - Option A (correct, ~5 lines): extend `Aborts` to also return true when the response
    carries `AuthErrorCodes.TokenRequired` / `InvalidOrExpiredToken` (reuse `HasErrorCode`
    so `errors[]` shapes count too — and make `HasErrorCode` null-entry-safe while there,
    see Nit 1). Since `Aborts` runs per response, the pipeline then stops at the first
    rejected call — AC 6 becomes fully true. Update
    `Push_TokenRejectedMidPush_WarnsPerPolicyAndNoPushRunsAfterTokenCleared` (expected
    endpoints become `["macros/create"]`; the abort logs Warning, still no Error).
  - Option B (weaker): keep warn-and-continue and rewrite AC 6 to accept bounded
    dead-token calls — rejected: leaves the S2 window wide open for the whole tail of
    the push.
- [x] **S2. `TokenRejected` carries no token — a stale rejection can sign out the next
  session** — `src/ProoveGaming.App/Services/ApiHttpClient.cs:27,139–152` +
  `src/ProoveGaming.App/Views/MainWindow.axaml.cs:177–180` — Should-fix
  **Problem:** The event is `Action?`, so `MainWindow` cannot tell a late rejection of the
  OLD session's token from a rejection of the current one. Any request that stays in
  flight across sign-out + re-sign-in (HttpClient default timeout is 100 s; with S1 the
  sources shrink to single stray calls) re-fires after the latch reset and bounces the
  freshly signed-in user.
  **Fix:** make it `event Action<string>? TokenRejected` passing the rejected `token`
  (`SendAsync` has it in scope); in the posted handler, act only when the rejected token
  equals `_userProfile.SessionToken` (compare inside the `Dispatcher.UIThread.Post` so the
  read is UI-thread-affine). A mismatch (token already cleared, or a new session's token)
  is a stale signal — ignore it. Update the new tests' subscriber lambdas.
- [x] **S3. Test construction via `GetUninitializedObject` + private-field reflection** —
  `tests/ProoveGaming.App.Tests/ApiHttpClientTokenRejectionTests.cs:185–196` — Should-fix
  **Problem / why it matters:** renaming `_http`/`_baseUrl` (or adding ctor invariants)
  breaks these tests only at runtime via the `!`-suppressed `GetField(...)` NRE, and the
  uninitialized-object trick silently skips any future constructor logic. The test file
  itself asks for the seam.
  **Ruling (test-writer's question 1):** yes — add
  `internal ApiHttpClient(HttpMessageHandler handler, string baseUrl)` chaining the same
  field setup (`InternalsVisibleTo` for `ProoveGaming.App.Tests` already exists); delete
  `NewClient`'s reflection and the `SetField` helper.

#### Tests
- [x] After S1: update the `SettingsSyncServiceTests` quiescence case to assert the push
  aborts at the rejected item (endpoints `["macros/create"]`, Warning log, no Error) —
  `tests/ProoveGaming.App.Tests/SettingsSyncServiceTests.cs:280–314`.
- [x] After S2: extend `ApiHttpClientTokenRejectionTests` with a stale-token case — reject
  a request that carried token A, assert the event reports A (handler-side filtering is
  `MainWindow` plumbing, but the payload contract is testable here).
- Coverage is otherwise strong: both envelope shapes, deterministic 5-way concurrency via
  a TCS gate (no sleeps — low Release-CI timing risk), token-less/network/other-code
  non-triggers, latch re-arm, throwing subscriber. 19/19 pass locally.

#### Nits
- Pre-existing, adjacent: `SettingsSyncService.HasErrorCode` dereferences `error.ErrorCode`
  without a null-entry check (`SettingsSyncService.cs:332–336`) while the new
  `ApiHttpClient.IsTokenRejection` correctly guards `error?` — an `errors:[null,…]` body
  NREs one and not the other. Fold the guard in when touching `Aborts` for S1.
- Account-deletion residual race (plan Risks §2 walked as requested): if a stray in-flight
  token-bearing call is rejected in the window between server-side deletion and the
  delete-success `SignOutToFreshSignIn(notify:false)` post, the `notify:true` post can land
  first and show "Session expired" on an intentional deletion. Narrow (needs a rejection
  inside a few-ms dispatcher window) and harmless; S1 removes the main source of stray
  calls. Accepted — no action beyond S1/S2.
- `ResetTokenRejectionLatch` in the `MainWindow` ctor runs even though the latch is
  necessarily 0 on first construction — harmless, and right if a window is ever rebuilt;
  fine as is.

#### What's done well
- The tests drive the real `ApiHttpClient` against a stub `HttpMessageHandler` — only the
  transport is faked — and the concurrency test is deterministic (arrival-counted TCS
  gate), not sleep-based.
- `SignOutToFreshSignIn`'s idempotency guard (token empty + already on Welcome) is the
  right shape, and the "notification after `ClearSignedInState` so the owner-scoped bell
  wipe can't swallow it" ordering is a subtle catch, correctly commented.
- The notify-only-on-`TokenRejected` deviation shows the account-deletion path was
  actually walked, and the comments at both handlers explain the asymmetry for the next
  reader.
- Docs discipline: CONTEXT.md entry, dated cross-plan note, en/uk strings — all in the
  same change.

#### Verdict
Ship: no — no blockers, but S1 leaves acceptance criterion 6 unmet and, through the
latch-reset interaction, opens a user-visible spurious sign-out of a fresh session; S2
closes the same class of bug at the source. Both are small, localized fixes.
First: 1) S1 (abort push on token rejection + test update), 2) S2 (token payload on the
event + handler guard), 3) S3 (internal test ctor, drop the reflection).
