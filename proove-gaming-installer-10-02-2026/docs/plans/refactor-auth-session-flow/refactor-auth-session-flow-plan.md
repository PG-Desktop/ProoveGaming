# Refactor the auth/session flow out of MainWindow

**Date:** 2026-09-24
**Kind:** `refactor-*`, no intended behaviour change. The touched flow has **no tests today** (it lives in a View's code-behind), so characterization tests land first, at the first testable seam; see Test strategy.
**Origin:** [`fix-repository-audit-findings`](../fix-repository-audit-findings/fix-repository-audit-findings-plan.md), finding **A5**. The requester chose "refactor now, in this branch" on 2026-09-24 (that plan's Open question 3).

## Goal

Sign-in, registration, logout, token rejection and guest-session expiry are implemented in `MainWindow`'s code-behind (`src/ProoveGaming.App/Views/MainWindow.axaml.cs`, 1,150 lines). The handlers mix three concerns: session **state transitions** (end the guest session, persist or clear the saved sign-in, migrate guest data, re-anchor owner-scoped state, start the sync), **API calls**, and **navigation**. Because they live in a Window, none of it can be unit-tested. That matters because this is exactly where earlier reviews fixed ordering bugs, each now guarded only by a comment:
- the guest-session row is cleared before the saved sign-in is decided (a phantom guest session resurrected on the next launch);
- the departing owner's cards are dropped only after the profile is cleared (PR #112 M1: devices leaked into the next account);
- sign-in does not migrate guest data, but registration does.

This change moves the state transitions and API calls into a DI-registered `AuthSessionCoordinator` in `Services/`, leaving `MainWindow` with navigation only, and pins the current behaviour with tests.

## Scope

**In:** the logic of `OnSignInSucceeded`, `OnNicknameChosen` (register → log in → migrate), `OnLogoutRequested`, `ClearSignedInState`, `SignOutToFreshSignIn`'s state part, `OnTokenRejected`'s staleness check, `OnSessionExpired`, `OnGuestContinueRequested`, and `PersistAuthIfTokenPresent`.

**Out:**
- Any behaviour change, including the absence of try/catch in these handlers today. Unhandled failures still reach `AppResilience`, exactly as now.
- Navigation (`_currentStep`, `UpdateStepUi`, view lookups such as `GetWelcomeView().ShowSignUpTab()`), which stays in `MainWindow`.
- `WelcomeViewModel`'s own sign-in call (`SignInAsync`) and lockout, which are already in a ViewModel and tested.
- The window-chrome, recorder and key-dialog code in the same file.

## Options considered

1. **One coordinator service with narrow interfaces for the ViewModel operations it drives (recommended).** `AuthSessionCoordinator` owns the transitions and depends on `IAuthApiService`, `ApiHttpClient`, `SessionService`, `LocalStorageService`, `UserProfile`, `ISettingsSyncService`, and three tiny interfaces for the ViewModels (`IOwnerScopedState`, `IProfileSession`, `IDeviceDetailsExit`). Tests fake those three instead of constructing `HomeViewModel` / `ProfileViewModel` / `DeviceDetailsViewModel`, which need the whole device graph. Downside: three small interfaces that exist mainly to make the coordinator testable.
2. **Coordinator depending on the concrete ViewModels.** No new interfaces, but every test would have to construct `DeviceDetailsViewModel` and `HomeViewModel`, whose harnesses need full fake device services. The ordering tests would become the heaviest tests in the suite for the least-complex logic.
3. **Move the logic into `WelcomeViewModel` / `HomeViewModel`.** Sign-out spans four ViewModels, so any one of them owning it would reach sideways into the others, which is the same coupling as today, relocated.

## Recommended approach

Option 1. **The move is mechanical: statements move in the same order, with the same comments.** The only edits are replacing field access with the coordinator's dependencies, and returning a result where a handler used to navigate.

**New types** (all in `src/ProoveGaming.App/Services/`):
- `IOwnerScopedState` { `void ReloadOwnerScopedState(); void ClearOwnerScopedState(); void AddNotification(string title, string subtitle);` }, implemented by `HomeViewModel`. It already has all three methods. `AddNotification(title, subtitle)` is `internal`, so it is implemented explicitly, with a one-line forward to the same overload; no method body changes.
- `IProfileSession` { `Task LoadFromServerAsync(); void ClearLocalState();` }, implemented by `ProfileViewModel`.
- `IDeviceDetailsExit` { `void ClearDeviceForExit();` }, implemented by `DeviceDetailsViewModel`, again explicitly with a one-line forward, because `ClearDeviceForExit` is `internal`.
- The interfaces are `public`: `MainWindow`'s public constructor takes the coordinator, whose constructor takes them.
- `AuthSessionCoordinator` (sealed, singleton):
  - `Task CompleteSignInAsync(bool rememberMe)` is the body of `OnSignInSucceeded` from the latch reset to `OnSignedInAsync`, **with navigation removed**. It becomes two calls, `CompleteSignInStateAsync(rememberMe)` (everything up to and including `ReloadOwnerScopedState`) and `FinishSignInAsync()` (profile load plus pull), because `MainWindow` navigates to Home *between* them today. The profile load must not delay the Home screen, so the order of those UI steps is kept exactly.
  - `Task<RegistrationResult> RegisterAsync(string email, string password, string nickname, bool agreeTerms, bool agreeNewsletter)`. `RegistrationResult(bool Succeeded, ApiResponse? NicknameError, string ToastText)`: on failure, `NicknameError` is the response when any error targets the nickname field, and `ToastText` is always the resolved toast. The View applies the nickname error when the nickname view is present, and otherwise shows the toast, exactly the fallback the old handler had (`ChooseNicknameViewModel.ApplyNicknameError` / `NotificationService.Show`). On success it runs log-in, persist, migrate, end session, clear the session row and re-anchor, in today's order. The log-in uses the email and password `RegisterAsync` was called with. The old handler re-read the sign-up form after the register call returned, so a user who went Back and edited the form mid-request could be logged in with different credentials from the ones registered. *(The only behaviour difference, an improvement; noted 2026-09-24 after review Round 1.)* The profile load after navigation is a separate `FinishRegistrationAsync()`, split for the same reason as sign-in.
  - `Task LogoutAsync()`: best-effort server logout (exception swallowed, as now), then `ClearSignedInState()`.
  - `ClearSignedInState()` (private, reached through `LogoutAsync` and `SignOut`): details exit → profile clear → saved-auth clear (queued) → owner-scoped clear, in that order.
  - `bool IsCurrentToken(string rejectedToken)`: the staleness check `OnTokenRejected` makes.
  - `SignOutOutcome SignOut(bool notifySessionExpired, bool onWelcomeScreen)` returns `Nothing` for the idempotent case (no token and already on Welcome), otherwise `SignedOut`. When asked, it adds the session-expired bell entry *after* `ClearSignedInState`, as today. The toast text is shown by the View.
  - `GuestExpiryOutcome HandleGuestSessionExpired()` returns `StayWhereYouAre` (a token is present: end the phantom guest session and queue `ClearSession`) or `ReturnToSignUp` (queue `ClearData`, **then** `EndSession`, then `ClearOwnerScopedState`).
  - `void StartGuestSession()`: `StartSession` plus a queued `InitializeSession`.
  - `EnqueueStorageWrite` and `PersistAuthIfTokenPresent` move here from `MainWindow` unchanged (same fault logging).
  - `void ResumeRestoredSession()`: the restored-sign-in start-up step from `MainWindow`'s constructor (latch reset, then the fire-and-forget profile load). *(Added during implementation, 2026-09-24.)* Without it `MainWindow` would still need `ApiHttpClient`.
  - `event Action<string>? TokenRejected` and `event Action? PullCompletedWithChanges`: pass-through accessors onto `ApiHttpClient` and `ISettingsSyncService`, so `MainWindow` subscribes through the coordinator and no longer takes either dependency. *(Added during implementation, 2026-09-24.)*
- DI: `services.AddSingleton<AuthSessionCoordinator>()`, plus the three interface registrations as forwards, in `internal static App.AddAuthSessionServices(IServiceCollection)`, which `App.BuildServices` calls (a separate method so the forwarding test can call it) (`services.AddSingleton<IOwnerScopedState>(sp => sp.GetRequiredService<HomeViewModel>())`, and so on) so each resolves to the **same singleton**. Every dependency is already a singleton, so there is no captive-lifetime risk.

**`MainWindow` after the move:** each handler becomes "call the coordinator, then navigate". For example `OnLogoutRequested` becomes `await _auth.LogoutAsync(); _welcomeViewModel.PrepareForFreshSignIn(); _currentStep = AppStep.Welcome; UpdateStepUi();`. The fields it no longer needs are removed from the constructor: `_authApi`, `_apiHttp`, `_settingsSync`, and `_localStorage` if nothing else uses it (it still does for the start-up lockout read). `async void` stays only on the event handlers themselves, which is allowed.

## File-level change list

Order: tests first (against the extracted coordinator, see Test strategy), then the move.

- T: `tests/ProoveGaming.App.Tests/AuthSessionCoordinatorTests.cs`: **new**. The characterization tests, plus the DI forwarding test.
- `src/ProoveGaming.App/Services/AuthSessionCoordinator.cs`: **new**. The moved logic.
- `src/ProoveGaming.App/Services/IOwnerScopedState.cs`, `IProfileSession.cs`, `IDeviceDetailsExit.cs`: **new**. One-line interfaces (a single file is fine if the repo prefers).
- `src/ProoveGaming.App/ViewModels/HomeViewModel.cs`, `ProfileViewModel.cs`, `DeviceDetails/DeviceDetailsViewModel.cs`: **edit**. Declare the interface; no body changes.
- `src/ProoveGaming.App/Views/MainWindow.axaml.cs`: **edit**. Handlers become coordinator call plus navigation; unused dependencies removed.
- `src/ProoveGaming.App/App.axaml.cs`: **edit**. `AddAuthSessionServices` registers the coordinator and the three forwards.
- The 13 older plans that name the moved `MainWindow` handlers: **edit**. Each gets a dated note with the old → new name map; the plans are not rewritten.
- `docs/CONTEXT.md`: **edit**, done with this plan: **AuthSessionCoordinator** is defined.
- `docs/plans/fix-repository-audit-findings/fix-repository-audit-findings-plan.md`: **edit**. Dated note on A5 pointing here.

## Acceptance criteria

- [ ] `MainWindow.axaml.cs` no longer calls `IAuthApiService`, `ApiHttpClient`, `ISettingsSyncService`, `SessionService.EndSession/StartSession`, `LocalStorageService.MigrateGuestToUserAsync`, `SaveAuth`, `ClearSavedAuth`, `ClearSession` or `ClearData` (`git grep` on the file returns none). Its auth handlers contain only a coordinator call and navigation.
- [ ] Given sign-in succeeds with Remember me, when the coordinator runs, then the observed order is: token-rejection latch reset → guest session ended → session row cleared (awaited) → saved sign-in written → owner-scoped reload. `FinishSignInAsync` then does profile load → sign-in pull for the signed-in customer id, and **no** guest migration happens.
- [ ] Given sign-in without Remember me, then the saved sign-in is cleared instead of written, and everything else is identical.
- [ ] Given registration fails with a nickname error, then no log-in call is made and the result carries the response for the nickname field. Given it fails otherwise, the result carries the localized toast text.
- [ ] Given registration succeeds, then the order is: register → log in → latch reset → token set → saved sign-in written (always) → guest migration (only when a guest session and customer id exist) → upgrade push → guest session ended → session row cleared → owner-scoped reload. `FinishRegistrationAsync` loads the profile and does **not** start the sign-in pull.
- [ ] Given logout, when the server call throws, then the local sign-out still completes, in the order: details exit → profile cleared → saved sign-in cleared → owner-scoped cleared.
- [ ] Given a token rejection for a token that is no longer current, then nothing is signed out.
- [ ] Given sign-out is requested with no token while already on Welcome, then it returns `Nothing` and clears nothing.
- [ ] Given the guest session expires while signed in, then only the phantom guest session is ended and cleared (`StayWhereYouAre`). Given it expires as a guest, then `ClearData` is queued before `EndSession`, and owner-scoped state is cleared (`ReturnToSignUp`).
- [ ] Given the DI registrations, when `IOwnerScopedState`, `IProfileSession` and `IDeviceDetailsExit` are resolved, then each is the same instance as the registered `HomeViewModel`, `ProfileViewModel` and `DeviceDetailsViewModel`.
- [ ] The existing suite passes unchanged (`dotnet test ./src/ProveGaming.slnx -c Release`), including every `WelcomeViewModel`, `ProfileViewModel`, `HomeViewModel` and storage test.

## Test strategy

Kind is `refactor-*` and the flow has no tests, so **characterization tests come first**. Because the logic cannot be tested while it sits in a Window, the first testable seam *is* the extracted coordinator. The order of work is therefore:
1. Create `AuthSessionCoordinator` by moving the statements verbatim.
2. Write the characterization tests against it.
3. Only then rewire `MainWindow` and DI.

Step 1 changes no behaviour by construction (same statements, same order), and the reviewer checks the moved code line by line against the removed code. That line-by-line check is the characterization safety net for step 1.

`tests/ProoveGaming.App.Tests/AuthSessionCoordinatorTests.cs` uses:
- real `SessionService` (`new SessionService(a => a())`), an in-memory `LocalStorageService`, and a real `UserProfile`;
- a real `ApiHttpClient` through its internal handler seam, observing the latch via `TokenRejected`;
- fakes for `IAuthApiService`, `ISettingsSyncService`, `IOwnerScopedState`, `IProfileSession` and `IDeviceDetailsExit`, which all append to **one shared call log**, so order is asserted directly.

Storage effects are read back from the in-memory store after `DrainStorageQueueAsync()`: the saved sign-in present or absent, the session row gone, the migrated rows under the new customer, and the guest's device rows gone after guest expiry. One test per acceptance criterion above. A gated-executor test (a blocking work item queued first) pins that sign-in *awaits* the session-row delete before re-anchoring Home.

**Not automated:** navigation (`_currentStep`, which screen is visible), because it is View code; QA steps 1–7 cover it.

## QA (manual test steps)

1. Open the app as a guest, add some settings, then choose Sign Up, register a new account and pick a nickname. **Expected:** you land on Home signed in, and your guest settings are on the new account.
2. Close the app from the tray and reopen it. **Expected:** still signed in.
3. Sign out. **Expected:** back on the Sign In screen with empty fields; your devices from the account are no longer shown.
4. Sign in with "Remember me" **off**, close and reopen the app. **Expected:** asked to sign in again.
5. Sign in with "Remember me" **on**, close and reopen. **Expected:** still signed in.
6. While signed in, change your password on the website, then use the app (open Profile). **Expected:** you are signed out with a "session expired" message.
7. Choose "Continue as guest", then wait for the guest session to expire (or ask the developer for a build with a short session time). **Expected:** you return to the Sign Up screen, and the guest's devices are gone from Home.

## Review focus

`DI lifetimes` (forwarded singletons), `async/await` (the sign-in/registration split around navigation), `UI-thread affinity` (the coordinator is called from UI handlers and must not introduce new marshalling), `thread-safety`

## Performance & quality

- **Hot paths:** none.
- **UI thread:** unchanged. The coordinator runs the same awaits the handlers did, on the same (UI) context, and adds no synchronous I/O. The sign-in and registration split keeps navigation to Home *before* the profile load, as today.

## Risks

- **Ordering drift during the move.** This is the whole point of the characterization tests; the reviewer also compares moved against removed code.
- **The split around navigation.** Merging `CompleteSignInStateAsync` and `FinishSignInAsync` into one call would delay the Home screen behind the profile request. The split is deliberate; the acceptance criteria name both halves.
- **Interface forwards resolving a second instance.** Registering the three interfaces with their own `AddSingleton<IX, X>()` would create a *second* `HomeViewModel` and the others. They must forward to the existing singleton; a DI test resolves both and asserts `ReferenceEquals`.

## Open questions

- none. The requester decided to do this in the branch; the design keeps behaviour identical.

## Review findings

_Filled in by /reviewer._

### Round 1 — Ship: yes

Reviewed on 2026-09-24. Scope: the uncommitted extraction.
- **New files:**
  - `Services/AuthSessionCoordinator.cs`;
  - `IOwnerScopedState.cs`, `IProfileSession.cs`, `IDeviceDetailsExit.cs`;
  - `tests/ProoveGaming.App.Tests/AuthSessionCoordinatorTests.cs`.
- **Edited:** `Views/MainWindow.axaml.cs`, `App.axaml.cs`, and the interface declarations on `HomeViewModel`, `ProfileViewModel` and `DeviceDetailsViewModel`.
- **Docs:** the dated notes in 13 older plans, and the parent plan's A5 note.
- **Out of scope, noted only:** the rest of the working tree is the named-pipe work, reviewed in that plan's Rounds 1–2, and the unplanned NuGet bumps, which are that plan's Round 2 S1 and still await the requester.

**Evidence:**
- Full Release suite at hand-off: 3,128 passed, 0 failed, 1 skipped (App 1,856, of which 13 are new).
- The acceptance `git grep` on `MainWindow.axaml.cs` returns nothing.

#### Plan conformance
- **Implemented as planned:**
  - the coordinator with the split sign-in (`CompleteSignInStateAsync` / `FinishSignInAsync`) and the split registration (`RegisterAsync` / `FinishRegistrationAsync`);
  - `LogoutAsync`, `IsCurrentToken`, `SignOut` (returns `Nothing` on the idempotent path), `HandleGuestSessionExpired` and `StartGuestSession`;
  - `EnqueueStorageWrite` and `PersistAuthIfTokenPresent` moved unchanged;
  - three narrow interfaces, forwarded in DI to the existing singletons, with a test that pins identity;
  - `MainWindow` reduced to "call the coordinator, then navigate".
- **Moved code compared line by line with the removed code:** same statements, same order, same comments. The only substitutions are:
  - fields → coordinator dependencies;
  - `_welcomeViewModel.IsRememberMeChecked` → the `rememberMe` parameter;
  - the sign-up email/password → the `RegisterAsync` parameters;
  - navigation → return values.
- **Deviations, all already recorded in the live sections:**
  - `ResumeRestoredSession` and the pass-through `TokenRejected` / `PullCompletedWithChanges` accessors. They are needed so `MainWindow` drops `ApiHttpClient` and `ISettingsSyncService`, and they are justified.
  - `ClearSignedInState` is private.
  - `RegistrationResult` has the shape (`Succeeded`, `NicknameError`, `ToastText`). This keeps the old handler's fallback to the toast when the nickname view is absent.
  - Explicit one-line interface forwards for the two `internal` members.
  - `App.AddAuthSessionServices`.
- **Planned order change (per the plan's `SignOut` design):** on "session expired", the bell entry is now added before `PrepareForFreshSignIn`, `UpdateStepUi` and the toast, instead of after. Verified harmless: neither `UpdateStepUi` nor `PrepareForFreshSignIn` touches the bell, and the entry still lands after `ClearSignedInState`, which is the ordering the original comment protects.
- **Unplanned difference, an improvement:** `RegisterAsync` logs in with the email and password it was *called* with. The old handler re-read `_welcomeViewModel.SignUpEmail/SignUpPassword` after the register call returned. With Back → edit the form while the register request is in flight, the old code could log in with different credentials from the ones it registered. The new code cannot. Record this in the plan's live text, since the plan promises no behaviour change.
- **Review focus:**
  - **DI lifetimes:** clean. Every dependency is a singleton, the forwards resolve to the same instances (tested), and there is no cycle: no ViewModel or service depends on the coordinator or on `MainWindow`.
  - **async/await:** clean. The split keeps navigation between the two halves, and `async void` remains only on the three event handlers.
  - **UI-thread affinity:** unchanged. There is no new marshalling, `IsCurrentToken` is still read inside the dispatcher `Post`, and `ResumeRestoredSession` runs in the constructor, as before.
  - **thread-safety:** clean. The pass-through event accessors add and remove on the original events, so unsubscribing in `OnClosed` still removes the same delegates.

#### Code
- [x] **S1. Two comments still say `MainWindow` makes the calls that moved.** Should-fix.
  - **Files:**
    - `src/ProoveGaming.App/ViewModels/HomeViewModel.cs:1064`: `ReloadOwnerScopedState`'s summary, "(MainWindow calls this once the old session is torn down)";
    - `src/ProoveGaming.App/Services/ApiHttpClient.cs:170`: "MainWindow calls this whenever a session starts (sign-in, restore, registration)".
  - **Problem / why it matters:** both calls are now made by `AuthSessionCoordinator`. A reader tracing why the latch is reset, or when owner state is re-anchored, is sent to a file that no longer contains the code. This is exactly the kind of drift this refactor exists to remove.
  - **Fix:** say `AuthSessionCoordinator` in both comments. Comment-only, so at most a scoped App build.

#### Tests
- [x] **The "awaited" in "session row cleared (awaited)" is not pinned.** `SignIn_WithRememberMe_…` asserts the end state after `DrainStorageQueueAsync`. If `await _localStorage.RunOnStorageAsync(_localStorage.ClearSession)` were weakened to a fire-and-forget enqueue, the FIFO end state would be identical and the test would stay green, even though Home would then re-anchor before the row is gone. This is optional: the line-by-line check covers it for this round. If it is wanted, gate the executor (queue a blocking work item first, as `WelcomeViewModelStorageThreadingTests` does) and assert that `CompleteSignInStateAsync` has not completed and `owner.reload` is not logged until the gate opens. Project: `tests/ProoveGaming.App.Tests`.
- [x] **`GuestExpiry_AsAGuest_…` does not show that guest data is wiped.** It checks the session row and `owner.clear`, both of which `EndSession` or `ClearSession` alone would also satisfy. Add one guest-owned row (for example a device or setting written under the session id) before expiry, and assert that it is gone after the drain. That pins `ClearData`.

#### Nits
- The `RegisterAsync` credentials change (see Plan conformance) belongs in the plan's Recommended approach as a one-line note.

#### What's done well
- **Test design.** One shared call log across every fake makes the orderings earlier reviews fixed directly assertable. The registration test snapshots state *inside* the upgrade push (`token=…, saved=True`), which proves the FIFO order of the saved sign-in before the migration, not just the end state.
- **Latch check.** It is driven through the real `ApiHttpClient` (trip, then re-arm), not a counter on a fake.
- **Forwarding test.** It uses uninitialized ViewModels, so it pins singleton identity without the device graph. There are no finalizers on those types, so it is safe.
- **Faithful move.** The comments came across with the code, so the "why" behind each ordering now sits next to the code that has to keep it.

#### Verdict
Ship: yes. The move is faithful, the review-focus checks are clean, and the suite is green. The remaining items are a stale-comment fix and two optional test tightenings. First:
1. S1, the two comments.
2. The guest-data wipe assertion.
3. Optionally, the gated-executor test.

Then, before merge: resolve the NuGet-bump question, commit Phase 5, run QA steps 1–7, and run `/security-review` over the whole branch.
