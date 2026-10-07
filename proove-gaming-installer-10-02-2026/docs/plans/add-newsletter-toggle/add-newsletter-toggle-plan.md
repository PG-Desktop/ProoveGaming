# add-newsletter-toggle — Newsletter switch in Profile via profile/notifications

Phase 4 of [add-api-v2-conformance](../add-api-v2-conformance/add-api-v2-conformance-plan.md).
Independent of the other phases.

> **STATUS (2026-08-27): NOT SHIPPED — built, then removed. This plan describes a feature
> that is not in the product.**
>
> Product decided against a marketing-newsletter toggle in Profile and chose **Option A**
> ("Do nothing; document the endpoints as unused") from *Options considered* below, which
> resolves **Open question 1**. The feature had already been implemented and reviewed
> (Round 1, ship: yes) on `features/newapi`; it was removed again on 2026-08-27 before the
> branch merged, so the toggle never reached users.
>
> **Removed:** the `ProfileView.axaml` toggle row; `IsNewsletterEnabled` /
> `IsNewsletterLoaded` / `IsNewsletterBusy`, `LoadNewsletterAsync`,
> `ToggleNewsletterCommand` and the `ClearLocalState` resets in `ProfileViewModel`;
> `GetNotificationsAsync` / `UpdateNotificationsAsync` on `IAuthApiService` /
> `AuthApiService`; `NotificationsResponse` / `NotificationsDto` in `ApiResponse.cs`;
> `ProfileView_Newsletter` / `ProfileView_NewsletterUpdateFailed` from `Strings.en.axaml`
> and `Strings.uk.axaml`; `tests/ProoveGaming.App.Tests/ProfileViewModelNewsletterTests.cs`
> and the `NotSupportedException` stubs the feature had added to five other fake
> `IAuthApiService` implementations.
>
> **Deliberately kept:** the registration flow's `agree_newsletter` checkbox and
> `CustomerDto.Newsletter` (a separate feature — the opt-in is still chosen at sign-up), and
> the Command-mode keyboard interceptor in
> `src/ProoveGaming.App/Controls/ToggleSwitchControl.axaml.cs` added for S1 below — it fixes
> a real gap in a shared control and is kept even though this was its only Command-mode call
> site. All remaining `ToggleSwitchControl` call sites (NewPresetDialog, SettingsSectionView,
> ActuationSectionView, RgbLightingSectionView) bind `IsChecked` only, never `Command`, so
> both interceptors fall through and those toggles self-toggle exactly as before.
>
> **Net effect on the contract:** `POST api/profile/notifications` and
> `api/profile/notifications_update` are unimplemented by choice — the newsletter
> subscription is set at registration and changed only on the website. Everything below the
> line is preserved as the record of the design that was built and reverted; read it as
> history, not as the current contract.

## Goal

API v2.0 (and v1.4+) ships `POST api/profile/notifications` / `notifications_update`, but
the app never calls them: the newsletter opt-in is set once at registration
(`agree_newsletter`) and can never be changed in the app. Add a newsletter toggle to the
Profile view backed by those two endpoints, so the endpoints stop being dead contract and
the user controls the subscription without visiting the website.

## Scope

*(Superseded 2026-08-27 — nothing below is in the product; Option A was chosen. Kept as the
record of what was built and reverted.)*

**In:**
- `IAuthApiService`/`AuthApiService`: `GetNotificationsAsync(token)` and
  `UpdateNotificationsAsync(token, newsletterEnabled)`.
- A "Newsletter" toggle row in `ProfileView` (App project), localized en/uk, loading its
  state when the profile loads and pushing changes with optimistic UI + revert-on-failure.
- Signed-in users only (the Profile view is already gated to accounts).

**Out:**
- Any other notification kinds (the contract only defines `newsletter`).
- Touching the registration flow (its `agree_newsletter` checkbox stands).
- Guest UI (guests have no profile).

## Options considered

### A. Do nothing; document the endpoints as unused
- Pros: zero work.
- Cons: the user asked to close all v2.0 gaps; the subscription set at registration is
  unchangeable in-app, which is a real (if small) product hole. Rejected — but recorded as
  the fallback if product decides against the UI (Open question 1).
- **Chosen after all (2026-08-27):** product took this option; the small product hole is
  accepted and the endpoints stay unused. See the status note at the top.

### B. Toggle in Profile (recommended)
Load via `profile/notifications` when the profile screen loads; flip via
`notifications_update` with optimistic UI and revert + toast on failure.
- Pros: minimal UI, matches contract, uses the existing ProfileViewModel error plumbing
  (including `SessionTokenInvalid` on token rejection).
- Cons: one more server call per profile open (skippable: cache per session after first
  load).

**Decision: B.** — *(reversed 2026-08-27: product chose **A**; B was built, reviewed and
then removed. The final decision is A.)*

## Recommended approach

*(Superseded 2026-08-27 — this is the Option B design that was built and then removed. It is
not how the product behaves.)*

1. **API**: `NotificationsResponse : ApiResponse` with
   `[JsonPropertyName("notifications")] NotificationsDto? Notifications` where
   `NotificationsDto` has `[JsonPropertyName("newsletter")] int Newsletter` (contract: 1 =
   enabled). Requests: `{}` for get, `{ "newsletter": 0|1 }` for update. Both methods on
   `IAuthApiService` next to the other profile calls (this is profile surface, not sync —
   `SyncApiService` stays device-data-only).
2. **ViewModel** (`ProfileViewModel`):
   - `bool IsNewsletterEnabled` + `bool IsNewsletterBusy` (disables the switch while a call
     runs, so it can't double-fire) + `bool IsNewsletterLoaded` (row hidden/disabled until
     the first successful get).
   - Load once per sign-in inside/after `LoadFromServerAsync` (suppress the change handler
     while applying the server value — same `_suppressAutoSave` idea already used there).
   - On user flip: optimistic set, call `UpdateNotificationsAsync`; on failure revert the
     property, show the existing error-toast pattern; on token rejection reuse the
     established `SessionTokenInvalid` handling (same check as neighboring calls).
3. **View** (`ProfileView.axaml`): a labeled `ToggleSwitch` row after the profile fields —
   label from `ProfileView_Newsletter` resource; no fixed `Width` (uk label is longer);
   toggle state is not colour-only (ToggleSwitch's knob position is the second cue).
   Strings: `ProfileView_Newsletter` ("Marketing newsletter" / "Маркетингова розсилка"),
   `ProfileView_NewsletterUpdateFailed` for the revert toast, en + uk.
4. **DTO/`CustomerDto.Newsletter`** already exists (register/login echo) — the toggle's
   initial pre-load hint may seed from it, but the get call is the source of truth.

## File-level change list

*(All items below were implemented and then reverted on 2026-08-27 — every file in this list
is back to having no newsletter code. The one exception is item 8, `docs/CONTEXT.md`, whose
`Newsletter toggle` entry was amended rather than deleted so the decision stays findable.
`ToggleSwitchControl.axaml.cs`, which the S1 fix touched, is deliberately **not** reverted.)*

1. `src/ProoveGaming.App/Services/IAuthApiService.cs` — edit: the two methods.
2. `src/ProoveGaming.App/Services/AuthApiService.cs` — edit: implement over `ApiHttpClient`.
3. `src/ProoveGaming.App/Services/ApiResponse.cs` (or a small new
   `NotificationsDtos.cs` if `ApiResponse.cs` is the wrong neighborhood — match the file's
   existing role) — edit/new: `NotificationsResponse`/`NotificationsDto`.
4. `src/ProoveGaming.App/ViewModels/ProfileViewModel.cs` — edit: state, load, flip logic,
   failure revert.
5. `src/ProoveGaming.App/Views/ProfileView.axaml` — edit: the toggle row.
6. `src/ProoveGaming.App/Resources/Strings.en.axaml`, `Strings.uk.axaml` — edit: the two
   keys (`ProfileView_Newsletter`, `ProfileView_NewsletterUpdateFailed`).
7. `tests/ProoveGaming.App.Tests/ProfileViewModelNewsletterTests.cs` — new: see Test
   strategy.
8. `docs/CONTEXT.md` — edit: `Newsletter toggle` entry.

## Acceptance criteria

**Current (Option A, 2026-08-27) — these are what the code must satisfy now:**

- [x] The Profile view shows no newsletter row in either language, and nothing in Profile
  calls `api/profile/notifications` or `api/profile/notifications_update`.
- [x] `IAuthApiService` declares neither `GetNotificationsAsync` nor
  `UpdateNotificationsAsync`, and no `NotificationsResponse` / `NotificationsDto` type
  remains — no dead API surface is left behind.
- [x] `ProfileView_Newsletter` and `ProfileView_NewsletterUpdateFailed` are absent from both
  `Strings.en.axaml` and `Strings.uk.axaml`.
- [x] Registration still offers the `agree_newsletter` checkbox and `CustomerDto.Newsletter`
  still round-trips — the sign-up opt-in is untouched.
- [x] `ToggleSwitchControl` still compiles with its Command-mode pointer and keyboard
  interceptors, and every remaining call site (all `IsChecked`-only, no `Command`) toggles
  exactly as before.
- [x] `ProoveGaming.App.Tests` builds and passes with no newsletter test file and no
  newsletter stubs in the other fakes.

**Superseded (the shipped-toggle criteria for Option B) — none of these apply any more; kept
as history:**

- [ ] Given a signed-in user whose server newsletter = 1, when the Profile view loads, then
  the toggle shows ON without emitting an update call.
- [ ] Given the user flips the toggle, when the server accepts, then exactly one
  `notifications_update` with the flipped value was sent and the toggle stays.
- [ ] Given the server rejects the flip (error or network), then the toggle reverts to its
  previous state and a localized failure toast appears; no retry loop.
- [ ] Given `profile/notifications` fails on load, then the row is disabled/hidden and no
  update can be sent; the rest of the profile works.
- [ ] Given the flip response is `INVALID_OR_EXPIRED_TOKEN`, then the standard sign-out flow
  triggers (same as other profile calls).
- [ ] Given the uk locale, the label renders fully without truncation (no fixed Width).

## Test strategy

**Current (Option A):** no newsletter tests exist —
`ProfileViewModelNewsletterTests.cs` was deleted with the feature. The remaining
`ProfileViewModel*Tests` (change-password, clear-local-state, edit-email, edit-nickname) and
`WelcomeViewModelForgotPasswordTests` keep their fake `IAuthApiService` implementations,
minus the two newsletter stubs, and must stay green. Nothing new is needed: removing a
feature is pinned by the interface no longer declaring it (a re-added call would not
compile).

**Superseded (Option B strategy) — kept as history:**

New feature (`add-*`) — `tests/ProoveGaming.App.Tests/ProfileViewModelNewsletterTests.cs`
(new), fake `IAuthApiService` like the existing ProfileViewModel test files: load maps 1/0
to the property without firing update; flip → one update call with the right value; failure
revert + no further calls; load-failure disables the row; token-rejection raises
`SessionTokenInvalid`; suppressed handler during programmatic set (no update emitted on
load). Existing `ProfileViewModel*Tests` must stay green.

Deliberately not covered: XAML rendering/truncation (QA step 4), live HTTP.

## QA (manual test steps)

**Current (Option A) — verify the removal:**

1. Sign in, open Profile → Account. Expected: no "Marketing newsletter" row anywhere on the
   screen; the fields above it (email, nickname, etc.) and the section below are laid out
   normally with no gap left behind.
2. Switch the app language to Ukrainian and reopen Profile. Expected: same — no newsletter
   row, no missing-resource placeholder or blank label.
3. Register a new account. Expected: the marketing-newsletter checkbox is still on the
   sign-up form and still submits (`agree_newsletter`) — this part was not removed.
4. Open a device's RGB Lighting, Actuation and Settings sections and the New Preset dialog.
   Expected: every toggle switch still flips by click **and** by keyboard (Tab to it, press
   Space/Enter) exactly as before — the shared control kept its interceptors.

**Superseded (Option B steps) — kept as history:**

1. Sign in, open Profile. Expected: a "Marketing newsletter" switch shows your current
   subscription state (matches what was chosen at registration or on the website).
2. Flip the switch, wait a second, close and reopen Profile. Expected: the new state
   persisted (and the website account page shows the same state).
3. Turn off the network, flip the switch. Expected: the switch snaps back and a short
   "couldn't update" message appears; no crash, no stuck spinner.
4. Switch the app language to Ukrainian and reopen Profile. Expected: the label reads
   "Маркетингова розсилка" fully, not cut off.

## Review focus

1. `null-safety` — `Notifications` nullable on error envelopes.
2. `localization` — both keys present in both files; no fixed widths.
3. `async/await` — flip handler is async void-free (AsyncRelayCommand / awaited task),
   busy-flag prevents overlap.

## Performance & quality

None — one extra HTTP call per profile open; no hot paths.

## Risks

- The contract's example shows only `newsletter` inside `notifications`; if the backend adds
  kinds later, the DTO ignores unknown fields by default — forward-safe.
- Server may treat `notifications_update` body values as int vs bool inconsistently; the
  request sends `0|1` int per the documented example.
- (2026-08-25, post-Round-1) S1 closed via **Option A**: `ToggleSwitchControl` now adds a
  tunneled `KeyDown` interceptor for Space/Enter alongside the `PointerReleased` one —
  when a `Command` is bound, the key press is marked handled and routed through the
  command (Enter clicks on KeyDown; Space's click is armed on KeyDown, so handling
  KeyDown covers both). Call sites with no bound `Command` (the RGB toggles) are
  unaffected: both interceptors fall through and the inner `ToggleButton` keeps
  self-toggling. The Round 1 nit was also applied: `ToggleNewsletter()` starts with
  `if (IsNewsletterBusy) return;` to guard direct `ExecuteAsync` callers.

## Open questions

1. ~~Product confirmation that the toggle belongs in Profile (vs. leaving the endpoints
   unused — Option A). Default: build it.~~ **Resolved 2026-08-27: product chose Option A.**
   The toggle does not belong in Profile; the endpoints stay unused and the implementation
   was removed. See the status note at the top.
2. ~~Placement/visual spec of the row.~~ Moot — there is no row. Would need re-answering only
   if the feature is ever revived.

## Review findings

### Round 1 (2026-08-25) — Ship: yes

Scope note: the working tree carries four phases; this round reviews only the
newsletter delta (the files in this plan's change list plus the `NotSupportedException`
stubs added to five existing fake `IAuthApiService` files). The `Session_Expired*`
string additions in the same resource files belong to `add-global-token-expiry` and are
covered by that plan's journal.

#### Plan conformance
Plan: docs/plans/add-newsletter-toggle/add-newsletter-toggle-plan.md
- Implemented as planned: all 8 file-level items — `GetNotificationsAsync`/
  `UpdateNotificationsAsync` on `IAuthApiService`/`AuthApiService`;
  `NotificationsResponse`/`NotificationsDto` in `ApiResponse.cs` (nullable
  `Notifications`, int `newsletter` 0|1); VM state trio + load-once-per-sign-in +
  optimistic flip with revert/toast/token-rejection; hidden-until-loaded toggle row in
  `ProfileView.axaml`; both keys in both `Strings.en.axaml`/`Strings.uk.axaml`;
  `ProfileViewModelNewsletterTests.cs` (12 tests, all passing); `docs/CONTEXT.md`
  "Newsletter toggle" entry.
- Deviates from plan: the get posts **no body** (`PostAsync(..., body: null, ...)`,
  `AuthApiService.cs:73`) where the plan says "`{}` for get". This matches the file's
  established convention for `api/profile` and `api/logout`, so the plan text was
  informal, not a contract — no code change needed.
- Missing / partial: the `CustomerDto.Newsletter` pre-load seed (approach item 4) is not
  used — allowed, the plan says "may"; the get remains the sole source of truth.
- Accepted limitation (flagged by /coder): keyboard input bypasses the Command — see S1.
  Residual risk reported there, not silently passed.
- Accepted limitation (flagged by /test-writer): `ToggleNewsletter()` has no internal
  busy re-check — see Nits; documented by-design.
- Acceptance criteria: 1–5 are pinned by tests (load maps 1/0 without emitting an
  update; exactly one update per flip; revert + localized toast on rejection and on
  network error with no retry; load failure leaves the row unavailable and
  `CanExecute` false; `INVALID_OR_EXPIRED_TOKEN` raises `SessionTokenInvalid` with no
  toast). 6 (uk label, no fixed Width) verified by inspection — the label column is
  `*`-sized with no `Width` anywhere in the row; rendering itself stays QA step 4 per
  the test strategy.

#### Code
- none (Blockers)
- [x] **S1. Keyboard toggle bypasses the command — silent server desync** —
  `src/ProoveGaming.App/Controls/ToggleSwitchControl.axaml.cs:34` (interceptor) +
  `ToggleSwitchControl.axaml:78` (inner `ToggleButton`, TwoWay to `IsChecked`) —
  first Command-mode call site: `src/ProoveGaming.App/Views/ProfileView.axaml:386-390`
  — Should-fix
  **Problem:** the Command-mode interceptor handles only tunneled `PointerReleased`.
  The inner `ToggleButton` is focusable and TwoWay-bound, so keyboard Space/Enter on
  the focused switch self-toggles it, the TwoWay chain writes `IsNewsletterEnabled`
  directly, and `ToggleNewsletterCommand` never runs.
  **Why it matters:** signed-in user tabs to the switch, presses Space → the UI shows
  the flipped subscription but `notifications_update` is never sent and nothing
  reverts; the server still has the old value and the toggle silently snaps back on the
  next sign-in load. No crash or corruption, and pointer input (the dominant path) is
  correct — hence Should-fix, not Blocker. Pre-existing control characteristic
  (`Command` existed unused before this branch), but this phase creates its first live
  call site, so the gap is now user-reachable.
  **Solution options:**
  - Option A (correct, fixes every future Command-mode call site): in
    `ToggleSwitchControl`, when `Command != null`, also intercept the keyboard path —
    e.g. a tunneled `KeyDown`/`KeyUp` handler for Space/Enter that marks handled and
    routes through the command, mirroring the pointer interceptor.
  - Option B (quick): make the inner `ToggleButton` non-focusable while a `Command` is
    bound (`Focusable=False`), removing the keyboard path entirely — trades keyboard
    accessibility for consistency instead of desyncing.
- [x] **S1 follow-through note:** whichever option lands, record the choice in this
  plan (Risks or a dated note) since the control is shared with the non-Command RGB
  call sites, which must keep self-toggling.

#### Tests
- 12/12 pass (`dotnet test --filter FullyQualifiedName~ProfileViewModelNewsletterTests`,
  Debug, 2026-08-25). Coverage matches the test strategy: load mapping without update
  emission, load-once caching, exactly-one-update flips both directions, revert paths
  (reject / network / token rejection), load-failure and missing-payload gating, busy
  guard via `CanExecute`, `ClearLocalState` reset + refetch. Fakes exercise the real
  `LoadFromServerAsync` path rather than poking properties — good.
- [ ] S1 has no test (keyboard interaction is view-layer; the suite has no headless UI
  tests) — if Option A lands, a control-level headless test would be the first of its
  kind here; acceptable to leave to QA otherwise.
- *(2026-08-25, fix-pass test-writer)* Assessed after Option A landed: still not
  testable within existing patterns — the interceptors are private instance methods on
  a control whose constructor runs `InitializeComponent()`, the test project has no
  `Avalonia.Headless` package, and several test files explicitly document the
  convention of not instantiating UserControls. Left unticked for QA (plan QA steps
  cover the flip path). Instead, the Round 1 **nit** fix (internal
  `if (IsNewsletterBusy) return;` in `ToggleNewsletter()`) is now pinned by
  `ToggleNewsletter_DirectExecuteWhileBusy_ReturnsImmediatelyWithoutASecondUpdate`
  (13/13 class, 1139/1139 project, Debug): a second direct `ExecuteAsync` while an
  update is parked in flight completes synchronously, leaves the optimistic state
  untouched, and the API sees exactly one update.

#### Nits
- `ToggleNewsletter()` (`ProfileViewModel.cs:224`) has no internal busy re-check;
  `AsyncRelayCommand.ExecuteAsync` does not gate on `CanExecute`, so a *direct*
  `ExecuteAsync` while an update is in flight would double-fire. By-design and
  currently unreachable from the view: the pointer interceptor checks `CanExecute`
  before `Execute`, and `IsEnabled="{Binding !IsNewsletterBusy}"` disables the control
  while busy. A one-line `if (IsNewsletterBusy) return;` would future-proof it against
  new programmatic callers.

#### What's done well
- Envelope null-safety exactly as the Review focus asked: `!result.Success ||
  result.Notifications == null` guards the malformed-2xx case, and the row stays
  hidden until a successful get, so no update can race unknown state.
- Async discipline: generated `AsyncRelayCommand` (no `async void`), busy/loaded
  gating via `NotifyCanExecuteChangedFor`, no sync-over-async anywhere in the delta.
- Localization: both keys in both files; the row uses a `*` column + content-sized
  toggle, no fixed `Width` on localized text.
- `ClearLocalState` resets all three newsletter fields, so a sign-out/sign-in cannot
  leak the previous account's subscription state — and a test pins the refetch.
- The five fake-file stubs throw `NotSupportedException`, keeping unrelated suites
  loud rather than silently succeeding if they ever wander into the new surface.

#### Verdict
Ship: yes — no blockers; S1 is a real but low-severity accessibility-path desync in a
shared control, acceptable as a follow-up and best fixed at the control level where it
covers future Command-mode call sites too. First: 1) decide S1 Option A vs B and record
it here; 2) optionally add the one-line busy re-check while touching the VM.
