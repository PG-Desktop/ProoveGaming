# fix-rgb-owner-isolation — Per-owner device state caches

> **Note (2026-09-24):** the sign-in, registration, sign-out and guest-expiry logic this plan places in `MainWindow.axaml.cs` now lives in `src/ProoveGaming.App/Services/AuthSessionCoordinator.cs` ([`refactor-auth-session-flow`](../refactor-auth-session-flow/refactor-auth-session-flow-plan.md)). Same statements, same order; `MainWindow` keeps the navigation. Name map: `OnSignInSucceeded` → `CompleteSignInStateAsync` + `FinishSignInAsync`; `OnNicknameChosen` → `RegisterAsync` + `FinishRegistrationAsync`; `OnLogoutRequested` → `LogoutAsync`; `SignOutToFreshSignIn` → `SignOut`; `OnTokenRejected`'s check → `IsCurrentToken`; `OnSessionExpired` → `HandleGuestSessionExpired`; `OnGuestContinueRequested` → `StartGuestSession`; `ClearSignedInState`, `PersistAuthIfTokenPresent` and `EnqueueStorageWrite` moved as they were.

Date: 2026-08-26. Ships **inside the current API v2.0 PR** (branch `features/newapi`), next to
[fix-duplicate-device-cards](../fix-duplicate-device-cards/fix-duplicate-device-cards-plan.md).
Reported from manual testing: "RGB was not correctly saved when I switched accounts."

## Goal

`DeviceDetailsViewModel` is a DI singleton (`App.axaml.cs:235`) whose in-session device-state
caches are keyed by device identity **only** — no owner:

```csharp
// DeviceDetailsViewModel.Rgb.cs:55
private readonly Dictionary<(int vid, int pid, string key), RgbDeviceState> _rgbStateByDevice = new();
```

No sign-in / sign-out path clears them (`MainWindow.ClearSignedInState:239`,
`HomeViewModel.ClearOwnerScopedState:874`, `ReloadOwnerScopedState:832` — none reference the
details view model). Opening a device page resolves RGB **cache-first**
(`Rgb.cs:893-894`) and then unconditionally writes the resolved state into the **current
owner's** rows (`DeviceDetailsViewModel.cs:1114`). So account B's page-open persists account
A's colours into B's rows, and returning to A persists B's colours over A's — the user's
saved RGB is destroyed, not merely "not saved". The same defect class affects three sibling
caches keyed by `AssertedStageIdentity` (`DeviceDetailsViewModel.cs:705`): `_assertedDpiStages`
(`:702`), `_assertedReportRates` (`:714`) and `_backfilledPresets` (`:642`).

Separately, the owner-transition sweep leaves the **hardware** lit with the previous account's
colours: `ReapplyPersistedRgbAsync` returns silently when the new owner has no stored record
(`Rgb.cs:441`), and there is no "nothing stored → push defaults" branch.

This plan makes every in-session device-state cache owner-scoped, makes an account switch reset
lighting the new owner has never configured, and binds every RGB persist to the account whose state
it snapshotted so no path can file one account's colours under another's.

## Scope

**In:**
- Owner in the key of `_rgbStateByDevice` and of the three `AssertedStageIdentity`-keyed caches,
  plus an explicit purge of other owners' entries when the owner changes (bounded memory,
  no stale reads even if a future call site forgets the owner).
- On an **account-to-account switch** only (`HomeViewModel.RestoreConnectedDevicesForNewOwner`),
  when the new owner has no usable stored RGB for a connected device, push a reset — the app's
  default lighting, or the off encoding when the new owner's record says lighting is off — instead
  of leaving the previous owner's colours on the board. Never on the first owner of a session, and
  never on a plain reconnect.
- Making an RGB persist land only under the account it snapshotted, so neither a sign-out with the
  details page open nor an account switch landing mid-open can write one account's state into
  another's rows (ruled in scope by Round 1; see approach 5).
- Tests: reproduction first, then coverage of both behaviours.

**Out (pre-existing, separately tracked — see Follow-ups):**
- The RGB bucket keying on the **live** vendor id, which gives a merged-transport product one
  bucket per transport (Rampage: dongle `1389/12888` vs cable/BT `10473/12888`). Documented at
  `HidDeviceMapRegistry.cs:30-35` as a known catalog limitation; fixing it means changing how
  every settings bucket is addressed and migrating existing rows.
- A card **restored from storage** deriving its settings bucket from the stored routing product
  id (`HomeViewModel.cs:1959-1960`), so a page opened before discovery stamps identity
  reads/writes a bucket that no discovered card uses.
- The persist-on-open at `DeviceDetailsViewModel.cs:1114` itself. It is deliberate (its comment
  explains that without it a never-configured device is lit from ViewModel-only defaults and
  `ReapplyPersistedRgbAsync` finds nothing on reconnect) and stays — once the cache is
  owner-scoped it writes the **correct** owner's resolved state, which is the intended
  behaviour.
- Recovering account A's already-overwritten rows. They are gone; this fix stops the bleeding.

## Options considered

### A. Clear the caches on owner change only
Subscribe the details view model to the same owner-change signals `HomeViewModel` already uses
(`UserProfile.CustomerId` PropertyChanged, `SessionService.IsGuest`) and empty every dictionary.
- Pros: smallest diff; no key-shape churn.
- Cons: correctness depends on every present *and future* owner-transition path remembering to
  fire; a missed path silently reintroduces cross-account data destruction — the exact failure
  mode we are fixing. The signal also has to reach a view model that today has no owner-change
  wiring at all.

### B. Owner in the cache key, plus a purge on owner change (recommended)
The key becomes `(ownerKind, ownerId, vid, pid, deviceKey)`; the owner is already resolvable at
every call site via the injected `ICurrentOwnerProvider` (`Rgb.cs:897`, `:933`). The purge is an
optimisation and a stale-guard, not the correctness mechanism.
- Pros: correct by construction — a cache entry can never be read under a different owner, no
  matter which path led there; no new event plumbing; the purge failing is harmless.
- Cons: five call sites change shape; a null owner (welcome screen, no session) needs a defined
  behaviour — bypass the cache entirely, matching what the storage reads already do
  (`Rgb.cs:898` returns null for a null owner).

### C. Drop the caches entirely and always read storage
- Pros: no cache, no bug.
- Cons: the cache exists because a preset apply resolves RGB **synchronously** on the calling
  thread (`Rgb.cs:921-948`) and the page open must not block on SQLite; removing it turns every
  open and every preset apply into a storage round trip on the UI path. Rejected — it trades a
  correctness bug for a responsiveness regression the repo explicitly guards against.

**Decision: B.**

## Recommended approach

### 1. Owner-scoped RGB cache (`DeviceDetailsViewModel.Rgb.cs`)

- Introduce one private helper that produces the cache key, so the shape is defined once:
  `RgbCacheKey(SettingsOwner owner, DeviceCardViewModel device) => (owner.Kind, owner.Id,
  device.VendorId, device.SettingsProductId, device.SettingsDeviceKey)`.
- `_rgbStateByDevice` is re-typed to that key.
- `BuildRgbPersistWrite` (`:850`): resolve the owner once; when it is null, **skip the cache
  write** (and the storage write is already skipped downstream) rather than caching under a
  sentinel.
- `LoadRgbStateForCurrentDeviceAsync` (`:882`) and `LoadRgbStateForCurrentDevice` (`:921`):
  resolve the owner **before** the cache probe — both already resolve it a few lines later for
  the storage read, so this is a move, not a new call. A null owner bypasses the cache and
  falls through to `ApplyResolvedRgbState(null)` → defaults, matching today's storage behaviour.
- `ApplyPresetToDeviceCoreAsync`'s eviction (`Presets.cs:642`) uses the same helper.

### 2. Owner-scoped assertion caches (`DeviceDetailsViewModel.cs`)

`AssertedStageIdentity` (`:705`) currently returns `$"{VendorId}:{SettingsProductId}:{SettingsDeviceKey}"`.
Prefix it with the owner (`$"{kind}:{id}:…"`), returning `null` when there is no owner and
having the three call sites treat `null` as "do not cache". This keeps `_assertedDpiStages`
(`:702`), `_assertedReportRates` (`:714`) and `_backfilledPresets` (`:642`) from asserting a
previous account's DPI stage / report rate, and from skipping a backfill the new owner needs.

### 3. Purge on owner change

Add `internal void OnOwnerChanged()` to `DeviceDetailsViewModel` that drops every cache entry
whose owner is not the current one, and call it from the existing owner-transition point in
`HomeViewModel` — the same place that already re-drives the connected devices
(`RestoreConnectedDevicesForNewOwner`, `HomeViewModel.cs:1270`). Resolve the view model from
`_services` there (it is already the pattern used for `NotificationService` at `:368`) so no
constructor dependency is added and no new DI cycle risk appears.

### 4. Reset lighting the new owner never configured

`ReapplyOnConnect` gains an opt-in `resetWhenUnset` flag, left `false` on the ordinary connect path
(so a never-configured device still keeps its factory lighting on a plain reconnect — unchanged
behaviour) and passed `true` from `RestoreConnectedDevicesForNewOwner` **on an account-to-account
switch only**. When set and the new owner has no usable stored lighting, the app pushes a reset
instead of returning at `Rgb.cs:441`:

- no `device_rgb` record at all → the card's catalog **defaults**;
- a record present with `IsEnabled == false` → the **off** encoding (effect `0xED`, brightness 0),
  because "I turned lighting off" is as much "these are not my colours" as never having configured
  it. Both writes are skipped entirely on a plain reconnect.

**Which transitions count as a switch** (2026-08-26, Round 1 / B1). `RestoreConnectedDevicesForNewOwner`
runs on *every* owner transition — its own contract covers guest start, sign-in and session restore,
and it exists precisely to catch devices that connected before the owner resolved. Resetting on all
of them would overwrite a never-configured device's factory lighting with app defaults on every
launch. The flag is therefore gated on a **previous owner having been lit**, tracked in a dedicated
`_lastLitOwner` field:

- first owner of the process (launch with "Remember me", guest start, sign-in from Welcome) → **no
  reset**; nobody else's colours are on the board.
- A → B → **reset**.
- A → sign-out → B → **reset**. `_lastLitOwner` is deliberately *not* `_lastRestoredOwner`, which
  sign-out clears to null so the next sign-in reads as a change; signing out does not take A's
  lighting off the hardware, so a null there must not read as "no previous account".
- A → sign-out → A → **no reset**; the board already wears that account's own lighting.

### 5. A write may only ever land under the account it snapshotted

Two destructive writes survive an owner-scoped cache, both because a persist resolves its owner when
it *runs* rather than when it *snapshots* (`SettingsRepository.SaveRgbAsync` calls
`GetCurrent()` itself):

- **Sign-out with the details page open.** Neither sign-out path leaves the page, so `Device` still
  points at the departing account's card and the next account's first open flushes the previous
  account's displayed colours into its rows. Closed by binding the persist to the owner whose state
  the page is **displaying**, not to whichever owner is current when the snapshot is taken:
  `ApplyResolvedRgbState` records that owner (`_displayedRgbOwner` — the one moment the page starts
  showing a specific account's colours), `ClearDeviceForExit` clears it, and `BuildRgbPersistWrite`
  refuses **both** the storage write and the in-session cache write when it no longer matches the
  current owner. Snapshot-time capture alone could not do this: both callers build and invoke the
  write in one expression on one thread, so a captured owner always compares equal to the current
  one — in this shape the owner has already moved to B before the flush is built. The retained
  captured-owner check on the returned write now covers only a caller that genuinely defers the
  invoke. `MainWindow.ClearSignedInState` also calls `ClearDeviceForExit` so a sign-out genuinely
  leaves the page — defence in depth on top of the structural guard, and it stops the glow timers
  and the audio/key-matrix polling for a page nobody is on. The structural guard is what covers the
  paths that never reach `ClearSignedInState` at all, such as a guest session expiring.
- **A switch landing mid-open.** The post-await owner re-check aborts the *load*, but the persist
  that follows it in `SetDeviceAsync` then writes the view model's stale state into the new owner's
  rows — and the captured-owner check does not catch it, because that write is *built* after the
  switch. `SetDeviceAsync` captures the owner once and folds it into `Stale()`, so an owner change
  makes the whole open stale and every later loader stops too. Nothing is lost: the transition
  re-drives the page itself.

## File-level change list

Tests land first (bug-fix rule): items 1–2 must fail on current code.

1. `tests/ProoveGaming.App.Tests/DeviceDetailsRgbOwnerIsolationTests.cs` — new: the
   reproduction (A → B → A over one singleton view model) plus the sibling-cache cases.
2. `tests/ProoveGaming.App.Tests/HomeViewModelOwnerRestoreTests.cs` — edit: owner-change sweep
   resets lighting the new owner has not configured, and does not reset on a plain reconnect.
3. `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Rgb.cs` — edit:
   owner-scoped cache key helper; owner resolved before every cache probe; null-owner bypass;
   `resetWhenUnset` branches in `ReapplyPersistedRgbAsync` (nothing stored → catalog defaults,
   stored-but-disabled → `WriteRgbOffToDeviceAsync`); `ApplyResolvedRgbState` records the owner whose
   state the page displays (`_displayedRgbOwner`) and `BuildRgbPersistWrite` refuses the storage and
   cache writes when it no longer matches the current owner.
4. `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs` — edit:
   owner-prefixed `AssertedStageIdentity` (nullable) and its three call sites; `OnOwnerChanged`
   (takes the owner as a parameter rather than re-resolving it); `SetDeviceAsync` captures the
   opening owner and `Stale()` treats an owner change as staleness; `ClearDeviceForExit` clears the
   recorded displayed owner.
5. `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs` — edit:
   cache eviction uses the shared key helper; `ReapplyOnConnectAsync` passes the new flag
   through.
6. `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` — edit: call `OnOwnerChanged` on the owner
   transition and pass `resetWhenUnset` only on an account-to-account switch
   (`ShouldResetUnconfiguredLighting` + `_lastLitOwner`).
7. `src/ProoveGaming.App/Views/MainWindow.axaml.cs` — edit: `ClearSignedInState` calls
   `ClearDeviceForExit`, so signing out leaves the details page instead of leaving `Device`
   pointing at the departed account's card.
8. `docs/CONTEXT.md` — edit: "owner-scoped device caches" one-liner.
9. `docs/plans/fix-duplicate-device-cards/fix-duplicate-device-cards-plan.md` — edit: dated
   cross-reference (both fixes came out of the same account-switching test session).

## Acceptance criteria

- [ ] Given account A has set RGB on a device and signed out, when account B signs in and opens
  that device's page, then the page shows **B's** state (defaults when B has none) and A's
  `device_rgb` / `device_rgb_effect` rows are byte-for-byte unchanged. (Fails today: B sees A's
  colours.)
- [ ] Given B then changes RGB and A signs back in and opens the device, then A sees **A's**
  original state and B's rows are unchanged. (Fails today: A sees B's colours and A's rows are
  overwritten with them.)
- [x] Given no owner (welcome screen), when the RGB load runs, then nothing is read from or
  written to the cache, and the view falls back to defaults without throwing.
- [x] Given a device page opened under A and reopened under A in the same session, when the
  second open runs, then the state is served from the cache (no storage read) — the cache still
  does its job within one owner.
- [x] Given a preset apply evicts the RGB cache entry, when it runs, then only the current
  owner's entry for that device is removed.
- [x] Given the DPI stage / report rate was asserted for A, when B opens the same device, then
  the assertion runs again for B rather than being skipped.
- [ ] Given the owner changes **from one account to another** and the new account has **no** stored
  RGB for a connected device, when the sweep runs, then the device is pushed the app's default
  lighting; and when the new account has a record with lighting **disabled**, then the device is
  pushed the off encoding (`0xED`, brightness 0) rather than being left on the previous account's
  colours.
- [x] Given an ordinary reconnect (no owner change) of a device the owner never configured — or
  configured and switched off — when the reapply runs, then **no** RGB write is sent — unchanged
  behaviour.
- [x] Given the **first** owner of the session resolves (app launch with "Remember me", guest start,
  sign-in from Welcome), when the sweep runs for a device that owner never configured, then **no**
  RGB write is sent: a launch never overwrites factory lighting. Same for signing out and back into
  the same account.
- [ ] Given the owner changes, when the purge runs, then no cache entry belonging to another
  owner remains.
- [ ] Given account A's details page is still open when the session is signed out (expiry, token
  rejection), when B signs in and opens that device, then B's stored rows are unchanged and B's page
  shows B's own state — the flush of A's displayed colours never lands under B.
- [ ] Given an account switch lands while a device open is mid-flight, when the open resumes, then it
  stops instead of persisting: the new owner's rows are byte-for-byte unchanged.

## Test strategy

Bug fix (`fix-*`): the reproduction is written **first** and must fail on the current code —
name it `PreFix_...` while proving it, as the previous fix's tests did (a detached worktree at
`HEAD` is the cheapest way to run against pre-fix code without touching the working tree).

`tests/ProoveGaming.App.Tests`:

- `DeviceDetailsRgbOwnerIsolationTests` (new), driving **one** `DeviceDetailsViewModel`
  instance across owner changes via the existing `FakeOwnerProvider` pattern
  (`SettingsRepositoryTests:163` shows how the owner is switched) plus a real temp-file
  `LocalStorageService`:
  - **Reproduction:** A sets RGB → owner switches to B → open → B's rows are not A's state and
    A's rows are untouched; then back to A → A's state intact.
  - Null owner bypasses the cache without throwing.
  - Same-owner reopen is served from the cache. Note (2026-08-26, test round): asserted by
    changing the stored row behind the view model's back and reopening — only a cache hit can
    still show the old colour. `LocalStorageService.OnBeforeStorageAccess` was the plan's original
    mechanism but cannot serve here: it is a bare `Action` with no call identity, and one device
    open makes dozens of accesses (presets, macros, bindings, the default-preset apply, which
    itself re-resolves RGB through the same `GetRgbState`), so a count over the open cannot
    isolate the RGB read.
  - Preset-apply eviction removes only the current owner's entry.
  - DPI-stage / report-rate assertion caches re-assert after an owner change.
  - Purge drops other owners' entries.
  - The mid-read race asserts B's row still holds **B's own** colour (a `NotEqual` against A's red
    passes straight through a write of the view model's stale state, which is what the owner clause
    in `Stale()` prevents).
- `HomeViewModelOwnerRestoreTests` (edit): owner-change sweep with no stored RGB for the new owner
  pushes defaults, and with a stored-but-disabled record pushes the off encoding; plain reconnect
  pushes nothing in either case. `ShouldResetUnconfiguredLighting` is a static helper for the same
  reason `ShouldRestoreForOwner` is — the launch-does-not-reset rule is pinnable without the full
  `HomeViewModel` service graph.

Deliberately **not** covered: real hardware RGB writes (the device service is faked, as in the
existing RGB tests); the `MainWindow` sign-in/sign-out wiring (no headless Avalonia harness —
QA steps own it); and the two out-of-scope bucket defects listed under Follow-ups.

## QA (manual test steps)

1. Sign into account A, open the Rampage keyboard page, set a distinctive lighting (for example
   solid red at full brightness). Go back to the device list.
2. Sign out and sign into account B. Open the same keyboard's page. Expected: the lighting shown
   is **B's own** (its previously saved lighting, or the standard default if B never set one) —
   **not** account A's red. The keyboard itself must not stay red.
3. Under B, set a clearly different lighting (for example solid blue). Go back, sign out, sign
   into A again, open the keyboard page. Expected: A's red is back, exactly as A left it.
4. Repeat the A → B → A cycle twice more. Expected: each account keeps its own lighting every
   time; neither account's setting ever changes on its own.
5. Under A, restart the app completely and open the keyboard page. Expected: A's red is still
   there (it was really saved, not just remembered in memory).
6. With account A signed in and the keyboard never configured for DPI, check that DPI and report
   rate behave normally after switching accounts — no setting from the other account appears.
7. Unplug and replug the keyboard while staying signed in as A (no account switch). Expected:
   A's lighting is restored on reconnect, exactly as before this change.
8. With "Remember me" on and a keyboard whose lighting **no** account has ever set in the app, close
   the app completely and start it again. Expected: the keyboard keeps the lighting it powered on
   with — the app does not change it at launch. Repeat with "Continue as guest": same result.
9. Under B, turn the keyboard's lighting **off** on its page. Sign out, sign into A (whose red is
   saved), then sign out and back into B. Expected: on returning to B the keyboard goes **dark** —
   it does not stay red.
10. Sign in as A and open the keyboard's page. Leave it open and force the session to end (sign out
    from another device, or wait for the token to expire) so the app drops to the sign-in screen on
    its own. Sign in as B and open that keyboard. Expected: B sees B's own lighting, and after
    signing back into A, A's red is still exactly as A left it — neither account's saved lighting was
    touched by the other's page being open.

## Review focus

1. `thread-safety` — `_rgbStateByDevice` is touched from the UI thread and from `Task.Run`
   continuations in `LoadRgbStateForCurrentDeviceAsync`; the added purge must not mutate it
   while a load is in flight (the generation guard at `Rgb.cs:905` is the existing mechanism).
2. `UI-thread affinity` — the owner-change purge is invoked from `HomeViewModel`'s owner
   transition; confirm it runs on the dispatcher and does no storage work.
3. `null-safety` — every new owner resolution can return null (welcome screen); no call site may
   dereference it, and `AssertedStageIdentity` becoming nullable must not silently disable
   caching for a signed-in owner.
4. `DI lifetimes` — resolving `DeviceDetailsViewModel` from `_services` inside `HomeViewModel`
   must not create a second instance or a resolution cycle (both are singletons).

## Performance & quality

- The cache stays a plain dictionary lookup on the page-open path; the key gains two string
  fields, so the only cost is a slightly larger tuple hash — no allocation on the hot path
  beyond what the existing tuple already does.
- Cache entries are now per (owner, device). Bounded by accounts-used-in-one-session × devices,
  i.e. single digits; the purge on owner change keeps even that from growing.
- The default-push on owner change is one device write per connected device, once per account
  switch — not a hot path. It must not run on ordinary reconnects (acceptance criterion 8).
- No change to per-frame RGB animation, device polling or startup.

## Risks

- **Missed call site.** If a cache read is left on the old key shape it will not compile (the
  dictionary type changes), which is the point of changing the key rather than only clearing.
- **The default-push could surprise a user** who switches accounts on a device only one account
  configured: the board goes to app defaults instead of keeping the other account's colours.
  That is the intended behaviour (an account must not inherit another's lighting), but it is a
  visible change — called out for review and covered by QA step 2. It fires on an
  account-to-account switch only; a launch or a guest start leaves factory lighting alone
  (QA step 8), which is what keeps the surprise confined to the case that earns it.
- **Out-of-scope bucket defects still bite.** If the user switches transport (dongle ↔
  Bluetooth) between account switches, RGB will still look "lost" because the bucket moved with
  the live vendor id. QA should keep the keyboard on one transport while verifying this fix, or
  the result will be ambiguous.
- **Assertion caches becoming nullable** could disable DPI/report-rate assertion caching if the
  owner resolves null more often than expected, turning a skip into a repeated hardware
  assertion. Review focus 3 covers it.

## Follow-ups (not in this plan)

> **2026-08-27 — items 1 and 2 are CLOSED**, by
> [`../fix-settings-bucket-identity/fix-settings-bucket-identity-plan.md`](../fix-settings-bucket-identity/fix-settings-bucket-identity-plan.md),
> which pins a settings vendor id in the catalog (`DeviceCatalogEntry.settings_vendor_id`), persists
> the full settings identity on the `devices` row, and re-keys existing split rows. Nothing in this
> plan's Follow-ups list remains open. Left below as written, because the Round 1 journal refers to
> them by number.

1. `device_rgb` (and every settings bucket) keys on the live vendor id, splitting merged
   transports — needs a catalog-level settings vendor id plus a row migration.
2. A card restored from storage derives its settings bucket from the stored routing product id,
   so a page opened before discovery reads and writes a bucket no discovered card uses.
> **2026-08-26, after Round 1.** Items 3 and 4 below were ruled **in scope** by the review (B2 and
> B3) and are now implemented — see "Recommended approach → 5. A write may only ever land under the
> account it snapshotted" and the acceptance criteria that cover them. They are left here as written
> because the Round 1 journal refers to them by number; read them as history, not as outstanding
> work. Only items 1 and 2 remain open.

3. **(Found 2026-08-26 by the test round; CLOSED in this plan — see the note above.)** The outgoing-device RGB flush at
   the top of `SetDeviceAsync` (`DeviceDetailsViewModel.cs:1075`) persists whatever the page is
   currently DISPLAYING under whatever owner is current NOW. Sign-out does not leave the details
   page — `MainWindow.SignOutToFreshSignIn` / `ClearSignedInState` never call `ClearDeviceForExit`,
   only `PerformExitDeviceDetails` does — so after a session expiry or a token rejection while the
   page is open, `Device` still points at account A's card. The next open under account B then
   flushes A's displayed colours into **B's** `device_rgb` rows before the load runs, and B's page
   shows them. Verified on this branch: with A red and B green stored, B's row reads `#FF0000`
   after the switch. Two candidate fixes: call `ClearDeviceForExit` from `ClearSignedInState`, or
   capture the owner alongside the payload in `BuildRgbPersistWrite` so a flush can only ever write
   the account whose state it snapshotted.
4. **(Same round, same root; CLOSED in this plan — see the note above.)** The post-await owner re-check in `LoadRgbStateForCurrentDeviceAsync`
   stops the previous account's record being shown or cached, but the `PersistRgbStateForCurrentDeviceAsync`
   that follows it in `SetDeviceAsync` is not owner-guarded, so a switch landing mid-read still
   overwrites the new owner's rows — with the view model's stale state rather than the old
   account's, which is a smaller leak but the same write. Follow-up 3's second fix closes both.

## Open questions

1. Should the default-push on owner change also cover DPI / report rate (the same "new owner
   inherits the previous account's hardware state" problem), or is lighting enough for now?
   The plan does lighting only, because that is what was reported and what is visible.

## Review findings

### Round 1 (2026-08-26) — Ship: no

Scope reviewed: this fix's delta only — `DeviceDetailsViewModel.Rgb.cs`, `DeviceDetailsViewModel.cs`,
`DeviceDetailsViewModel.Presets.cs`, the `ReapplyOnConnect` / `OnOwnerChanged` hunks of
`HomeViewModel.cs`, `DeviceDetailsRgbOwnerIsolationTests.cs`, the `HomeViewModelOwnerRestoreTests.cs`
additions and the doc edits. The API v2.0 phases and `fix-duplicate-device-cards` (including
`CardIdentity`, `UpsertDevice`, `MergeTransports` in the same `HomeViewModel` diff) are reviewed in
their own plans and are out of scope here.

Verification: `dotnet test ./tests/ProoveGaming.App.Tests/ProoveGaming.App.Tests.csproj -c Release`
— 1185 passed, 0 failed. (Solution build is unusable in this environment: a running DeviceHost dev
process holds the DLLs, producing MSB3021/MSB3027 file locks, not compile errors.)

#### Plan conformance

Plan: `docs/plans/fix-rgb-owner-isolation/fix-rgb-owner-isolation-plan.md`

- **Implemented as planned:** all 8 File-level change-list items are present. Option B was
  implemented as designed — `_rgbStateByDevice` is re-typed to `(ownerKind, ownerId, vid, pid, key)`
  with a single `RgbCacheKey` helper (`Rgb.cs:61`), the owner is resolved before every cache probe in
  both loaders (`Rgb.cs:972`, `:1014`), the null owner bypasses the cache (`GetCachedRgbState`,
  `Rgb.cs:68`), `AssertedStageIdentity` is owner-prefixed and nullable with all five call sites
  updated (`DeviceDetailsViewModel.cs:712`), `OnOwnerChanged` purges all four caches
  (`DeviceDetailsViewModel.cs:738`), the preset eviction uses the shared helper (`Presets.cs:644`),
  and `resetWhenUnset` is threaded `HomeViewModel → ReapplyOnConnect → ReapplyOnConnectAsync →
  ReapplyPersistedRgbAsync`.
- **Review focus verified:**
  1. *thread-safety* — no new race. The purge and every cache mutation run on the dispatcher; the
     `await Task.Run` in `LoadRgbStateForCurrentDeviceAsync` resumes on the captured UI context, and
     `RestoreConnectedDevicesForNewOwner` already enumerated the observable `Devices` collection
     before this change, so UI affinity there is a pre-existing invariant, not a new assumption. A
     stale re-add after a purge is impossible: the post-await owner re-check (`Rgb.cs:988`) returns
     before the insert.
  2. *UI-thread affinity* — `OnOwnerChanged` is pure dictionary work, no storage, no I/O. Correct.
  3. *null-safety* — no new `!` bangs; every owner resolution is `is { }`-matched. `AssertedStageIdentity`
     returns null only when `_ownerProvider?.GetCurrent()` is null, which is exactly the welcome-screen
     case; a signed-in owner always yields a key, so caching is not silently disabled (Risk 4 cleared).
  4. *DI lifetimes* — `HomeViewModel` and `DeviceDetailsViewModel` are both `AddSingleton`
     (`App.axaml.cs:233`, `:235`) and the resolve is inside a method, not a constructor, so no second
     instance and no cycle. `SettingsOwner` is a `readonly record struct`, so `Nullable.Equals` gives
     correct, allocation-free value equality.
- **Deviates from plan (scope drift):** the plan says `resetWhenUnset` is passed `true` "**only**
  from `RestoreConnectedDevicesForNewOwner`" and frames that as "the account-switch sweep". But that
  method's own contract — stated in its comment at `HomeViewModel.cs:1259-1273` — is broader: "guest
  start, sign-in and session restore all land here", and it exists precisely to cover the launch case
  where cards are already connected before the owner resolves. Passing `true` unconditionally there
  therefore also fires on first owner resolution, not only on an A→B switch. See **B1**.
- **Acceptance criteria:** 3, 4, 5, 6, 8 and 9 are met and covered by tests. **1 and 2 are not met**
  — they fail through the sign-out-with-page-open door (**B2**) and the mid-open switch door (**B3**).
  Criterion 7 is met for "no row at all" but not for "row present with lighting disabled" (**S1**).

#### Code

- [x] **B1. The owner sweep resets lighting on app launch and guest start, not only on an account switch** — `src/ProoveGaming.App/ViewModels/HomeViewModel.cs:1293` — Blocker

  **Problem:** `RestoreConnectedDevicesForNewOwner` passes `resetWhenUnset: true` for every owner
  transition, including the *first* one (`_lastRestoredOwner` null → owner). Its own comment says
  that transition is the normal launch path: the DeviceHost is already streaming devices while the
  owner is still resolving, which is the entire reason the sweep was added. `ReapplyOnConnectAsync`
  passes keyboards straight through to `ReapplyPersistedRgbAsync` (`Presets.cs:866-870`), so
  `WriteDefaultRgbToDeviceAsync` fires for any connected device the owner has never configured.

  **Why it blocks:** User signs in with "Remember me" and has never touched their keyboard's RGB.
  They launch the app. The keyboard card appears connected, then `UserProfile.CustomerId` resolves →
  sweep runs → `GetRgbState` returns null → the board's factory lighting is overwritten with the
  app's defaults. Every launch. The same happens on "Continue as guest". This is exactly the
  behaviour the plan promises stays unchanged ("a never-configured device still keeps its factory
  lighting", QA step 7), applied to a far more common path than the account switch it was designed
  for, and the plan's Risks section only sanctions the account-switch case.

  **Solution options:**
  - Option A (correct): reset only on a genuine account *change*, i.e. when a previous owner existed.
    Capture the old owner before overwriting it and pass
    `resetWhenUnset: previousOwner is not null && !Nullable.Equals(previousOwner, owner)`.
    Add a test alongside the existing `ShouldRestoreForOwner` cases pinning "first resolution does
    not reset".
  - Option B (quick): gate on `_lastRestoredOwner is not null` at the call site only. Same effect,
    but leaves the rule implicit at the one call site instead of stated as a value.

- [x] **B2. Sign-out with the details page open flushes the departing account's colours into the next account's rows** — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs:1075` — Blocker
  *(recorded as Follow-up 3; ruled here as in-scope, not a follow-up)*

  **Problem:** `SetDeviceAsync`'s outgoing-device flush builds its payload from whatever the page is
  currently displaying, but the owner is resolved at **write** time, not snapshot time —
  `SettingsRepository.SaveRgbAsync:109` calls `_ownerProvider.GetCurrent()` itself. Sign-out never
  leaves the details page: `MainWindow.SignOutToFreshSignIn` → `ClearSignedInState`
  (`MainWindow.axaml.cs:239-247`) clears the profile, auth and Home's owner-scoped state but never
  calls `ClearDeviceForExit` — only `PerformExitDeviceDetails` (`:684`) does. So `Device` keeps
  pointing at account A's card across the sign-out.

  **Why it blocks:** A sets the keyboard red and leaves the page open. The session token is rejected
  (expiry, or an account deleted server-side) → `SignOutToFreshSignIn` → Welcome. B signs in and
  opens that keyboard. `SetDeviceAsync`'s first act is the flush, which snapshots A's red from the
  still-displayed state and writes it under B — B's stored green is destroyed before the load for B
  even starts, and B's page then shows red. Empirically confirmed by the test round (B's row reads
  `#FF0000`). This is acceptance criteria 1 and 2 failing through a second door, and it is the plan's
  own stated harm — "the user's saved RGB is destroyed, not merely 'not saved'" — so it cannot be
  deferred without also narrowing the criteria, which would contradict the Goal.

  **Solution options:**
  - Option A (correct, and closes the class): capture the owner in `BuildRgbPersistWrite`
    (`Rgb.cs:922`) next to the payload, and have the returned lambda abort when
    `_ownerProvider?.GetCurrent()` no longer equals it. Fully local to a file this plan already
    changes, no repository signature change, and it makes a flush structurally incapable of writing
    an account it did not snapshot — whatever future path leads there.
  - Option B (complementary, recommended in addition): call `ClearDeviceForExit()` from
    `ClearSignedInState`, so a sign-out genuinely leaves the page instead of leaving `Device`
    pointing at the departed account's card. This also fixes the page showing another account's
    device, but on its own it is path-dependent (the same class of fragility Option A in the plan's
    "Options considered" was rejected for), and MainWindow has no headless test harness.
  - Do **not** rely on `OnOwnerChanged` here: the purge drops cache entries, while this write comes
    from the view model's live displayed properties, which the purge does not touch.

- [x] **B3. The persist-on-open in `SetDeviceAsync` is not owner-guarded, so a mid-open switch overwrites the new owner's rows** — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs:1161` — Blocker
  *(recorded as Follow-up 4; ruled here as in-scope)*

  **Problem:** The post-await owner re-check added at `Rgb.cs:988` correctly aborts the *load* when
  the owner moves mid-read — but it aborts by `return`ing, which leaves `ApplyResolvedRgbState`
  unrun and the view model holding stale RGB properties. `StaleAndRelease()` (`:1126`) tests only
  the generation and `Device` reference, neither of which an owner switch changes, so the chain
  proceeds to line 1161 and persists that stale state. `IsDeviceConfigurable` defaults to true
  (`DeviceCardViewModel.cs:147`), so the write really fires.

  **Why it blocks:** A is signed in with green stored for B. A opens the device; the SQLite read is
  in flight on the pool when the token is rejected and B signs in. The load returns early (good), but
  the very next statement writes the view model's stale colours into **B's** `device_rgb` rows under
  B's owner. B's saved lighting is destroyed — a smaller leak than B2 (stale VM state rather than
  A's colours) but the same destructive write, and B2's Option A does *not* close it, because here
  the flush is *built* after the switch, so a captured-owner check compares equal.

  **Solution options:**
  - Option A (correct): capture the owner once at the top of `SetDeviceAsync` and fold it into
    `Stale()` — `!Nullable.Equals(openOwner, _ownerProvider?.GetCurrent())`. An owner change makes
    the whole open stale, which is right: `RestoreConnectedDevicesForNewOwner` re-drives the page
    anyway. One expression, and it protects every step after it, not just the persist.
  - Option B (narrow): guard only line 1161 with the same comparison. Leaves the later loaders
    (presets, macros, bindings) still running under the wrong owner.

- [x] **S1. An account that stored "RGB off" keeps the previous account's lighting after a switch** — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Rgb.cs:477` — Should-fix

  **Problem / why it matters:** The reset branch keys on `record is null` only; a record present with
  `IsEnabled == false` falls to the untouched `return Task.CompletedTask`. So B, who explicitly
  turned lighting **off** on this device, switches in and the board keeps burning A's red — which is
  the reported complaint verbatim. `WriteDefaultRgbToDeviceAsync` already contains the off encoding
  (`0xED`, brightness 0) for the `Defaults.RgbEnabled == false` case, so the machinery exists. The
  behaviour is currently pinned by
  `HomeViewModelOwnerRestoreTests.OwnerChangeSweep_WhenTheNewOwnerStoredRgbButDisabledIt_PushesNothing`
  with an honest "KNOWN GAP" comment, but the plan records it nowhere — not in Acceptance criteria,
  not in Follow-ups, not as an accepted limitation. Per the repo's documentation rules a pinned
  contract must live in the plan, or the next round reads criterion 7 as the whole story.

  **Fix:** Either (preferred) extend the branch to
  `if (record is null) return resetWhenUnset ? WriteDefault… : …; if (!record.IsEnabled) return resetWhenUnset ? WriteRgbOffAsync(card) : Task.CompletedTask;`
  and flip the test's assertion to the `0xED`/brightness-0 write; or, if deliberately deferred, add
  it to the plan's Follow-ups and add a matching line to Acceptance criterion 7 so the criterion
  states what it actually guarantees.

- [x] **S2. The mid-read race test cannot detect B3** — `tests/ProoveGaming.App.Tests/DeviceDetailsRgbOwnerIsolationTests.cs:369` — Should-fix

  **Problem / why it matters:** `OwnerChangingDuringTheRgbRead_…` asserts
  `Assert.NotEqual(ARed, StoredHex(store, UserB))`. Under B3, B's stored `#00FF00` is replaced by the
  view model's stale colours — which are not `ARed`, so the assertion passes while the exact
  corruption the test is named for happens. The test currently proves only that A's *record* was not
  propagated, not that B's rows survived.

  **Fix:** Assert the positive: `Assert.Equal(BGreen, StoredHex(store, UserB))`. It will fail until
  B3 is fixed, which is precisely what a bug-fix test should do.

#### Tests

- [x] Launch / guest-start sweep must not reset lighting (**B1**) — add a case pinning
  "first owner resolution (`_lastRestoredOwner` null) pushes nothing for a never-configured device",
  next to the existing `ShouldRestoreForOwner` cases — `tests/ProoveGaming.App.Tests/HomeViewModelOwnerRestoreTests.cs`
- [x] Sign-out with the page open (**B2**) — model it at the view-model level the way
  `OpenAndLeaveAsync` models navigation: open under A, flip the owner **without** calling
  `ClearDeviceForExit`, open under B, assert B's row still holds B's colour —
  `tests/ProoveGaming.App.Tests/DeviceDetailsRgbOwnerIsolationTests.cs`
- [x] Strengthen the mid-read assertion (**S2**) — same file, `:369`
- [x] `_backfilledPresets` has no owner-isolation test of its own, unlike its three siblings — a case
  where the backfill a new owner needs is currently skipped would close the set —
  `tests/ProoveGaming.App.Tests/DeviceDetailsRgbOwnerIsolationTests.cs`

#### Nits

- `Color.TryParse`'s result is discarded — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Rgb.cs:515`. A malformed catalog hex silently yields `default(Color)`, so the board goes black with the effect still running. `ApplyRgbDefaults` passes the hex through as a string and never has this failure mode. Falling back to `FallbackRgbColorHex` on a parse failure would keep the two agreeing.
- Mixed comparison rules across the owner-keyed caches — `RgbCacheKey`'s tuple compares the owner ordinally (case-**sensitive**), while `_assertedDpiStages` / `_assertedReportRates` are `StringComparer.OrdinalIgnoreCase` and `BelongsToOwner` uses `OrdinalIgnoreCase` (`DeviceDetailsViewModel.cs:762`). Harmless for numeric customer ids, but the two halves of "same owner" are now defined differently.
- `OnOwnerChanged` re-resolves the owner from the provider (`DeviceDetailsViewModel.cs:740`) although its only caller read it one line earlier (`HomeViewModel.cs:1276`). Taking it as a parameter would remove the possibility of the two disagreeing.

#### What's done well

- **Choosing Option B over clearing on an event was the right call, and it paid off.** Re-typing the
  dictionary makes a missed call site a compile error rather than a silent cross-account read, and
  the plan's own "Risks → Missed call site" reasoning holds up in the delta.
- **One `RgbCacheKey` helper**, used by the loaders, the persist and the preset eviction alike — the
  key shape genuinely is defined once, which is what makes the eviction fix a one-liner.
- **The post-await owner re-check** (`Rgb.cs:988`) is a real deviation-for-the-better: the plan did
  not call for it, and it closes the "switch lands mid-read" show/cache leak. B3 is that it stops one
  statement short, not that it was wrong.
- **The null-owner bypass mirrors the storage layer's own behaviour** (`SaveRgbAsync` returns early
  with no owner) instead of inventing a sentinel — the two now degrade identically.
- **Comment density and voice match the surrounding files**: every new block says *why*, with the
  concrete failure it prevents, which is this repo's house style.
- **The tests drive one singleton view model across owner flips over a real on-disk store**, and the
  `OnBeforeStorageAccess`-driven flip makes the mid-read race deterministic rather than timed with a
  sleep. Pinning the "stored but disabled" gap with a named KNOWN GAP test, and recording in the plan
  why `OnBeforeStorageAccess` could not serve the cache-hit assertion, are both honest choices that
  leave the next round better informed than a green tick would have.

#### Verdict

Ship: **no**. The owner-in-the-key mechanism is correct, well-factored and well-tested, and it closes
the reported bug on the path it was aimed at — but three destructive writes survive: two that defeat
acceptance criteria 1 and 2 through doors the plan discovered late (B2, B3), and one the change
introduces itself by resetting lighting on every launch and guest start rather than only on an
account switch (B1).

**Ruling on Follow-ups 3 and 4 — both are Blockers and both belong in this plan, not a follow-up.**
Follow-up 3 (B2) makes acceptance criterion 1 false as written; deferring it would mean shipping a
fix whose stated goal — "an account's saved RGB is never destroyed by another account" — is still
violated by a session expiry, which is not a rare path. Follow-up 4 (B3) is the same destructive
write from a second trigger. Neither needs new architecture: B2 is a captured-owner check inside
`BuildRgbPersistWrite`, B3 is one clause added to `Stale()`, both in files this plan already edits.
Splitting them out would leave a plan whose Acceptance criteria and Follow-ups contradict each other.

First three things to tackle:
1. **B3** — fold the owner into `SetDeviceAsync`'s `Stale()`. Smallest fix, protects the whole chain.
2. **B2** — capture the owner in `BuildRgbPersistWrite` and abort the write if it moved; additionally
   call `ClearDeviceForExit` from `ClearSignedInState`.
3. **B1** — pass `resetWhenUnset` only on a genuine account change (previous owner non-null), plus
   the pinning test.

Then S1 (decide: fix the disabled-record case or record it in the plan) and S2.
