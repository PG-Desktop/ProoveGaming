# add-api-v2-conformance — Remaining API v2.0 work (umbrella)

> **Note (2026-09-24):** the sign-in, registration, sign-out and guest-expiry logic this plan places in `MainWindow.axaml.cs` now lives in `src/ProoveGaming.App/Services/AuthSessionCoordinator.cs` ([`refactor-auth-session-flow`](../refactor-auth-session-flow/refactor-auth-session-flow-plan.md)). Same statements, same order; `MainWindow` keeps the navigation. Name map: `OnSignInSucceeded` → `CompleteSignInStateAsync` + `FinishSignInAsync`; `OnNicknameChosen` → `RegisterAsync` + `FinishRegistrationAsync`; `OnLogoutRequested` → `LogoutAsync`; `SignOutToFreshSignIn` → `SignOut`; `OnTokenRejected`'s check → `IsCurrentToken`; `OnSessionExpired` → `HandleGuestSessionExpired`; `OnGuestContinueRequested` → `StartGuestSession`; `ClearSignedInState`, `PersistAuthIfTokenPresent` and `EnqueueStorageWrite` moved as they were.

Date: 2026-08-25. Follows [add-api-v2-sync](../add-api-v2-sync/add-api-v2-sync-plan.md)
(completed on branch `features/newapi`: `api/contact` rename, `uid`/`server_updated_at`
columns, one-shot guest→new-account upgrade push). Contract:
`docs/knowledge-base/proove-gaming-launcher-api-v2.0-en.md`.

## Goal

Close the gap between the app and API v2.0 for the **signed-in** side: today nothing pulls
account data from the server on sign-in, nothing pushes changes made after registration, an
expired token is only detected by profile screens, and the notifications endpoints are
unused. This umbrella decomposes that work into four independently shippable phase plans and
records the decisions they share.

## Phase map

| Phase | Plan | Depends on | Size |
| ----- | ---- | ---------- | ---- |
| 1 | [add-api-v2-pull](../add-api-v2-pull/add-api-v2-pull-plan.md) — hydrate local DB from the server on sign-in / session restore | add-api-v2-sync | M |
| 2 | [add-api-v2-live-sync](../add-api-v2-live-sync/add-api-v2-live-sync-plan.md) — dirty tracking + ongoing push (update/delete, conflicts, reconcile) | Phase 1 | L |
| 3 | [add-global-token-expiry](../add-global-token-expiry/add-global-token-expiry-plan.md) — centralize INVALID_OR_EXPIRED_TOKEN handling in ApiHttpClient | add-api-v2-sync | S |
| 4 | [add-newsletter-toggle](../add-newsletter-toggle/add-newsletter-toggle-plan.md) — profile/notifications(_update) toggle in Profile — **reverted, see note below** | none | S |

Recommended execution order: **3 → 1 → 2 → 4**. Phase 3 first because phases 1–2 generate
many token-bearing calls and should inherit the global expiry path from day one. Phase 4 is
independent and can run any time.

> **2026-08-31 — API v2.1 delivered.** The backend shipped v2.1
> (`docs/knowledge-base/proove-gaming-launcher-api-v2.1-en.md`): a per-user device roster
> (`api/devices/add|list|remove`, list returns `active_preset_uid`) and a Notifications
> Management family (`api/notifications/*`, list returns `unread_count`). Integration is
> planned outside this umbrella in two sibling plans that extend phase 2's machinery:
> [add-api-v21-device-roster](../add-api-v21-device-roster/add-api-v21-device-roster-plan.md)
> and
> [add-api-v21-notifications-sync](../add-api-v21-notifications-sync/add-api-v21-notifications-sync-plan.md).
> The shared decisions below carry over to both.

> **2026-08-27 — phase 4 reverted.** Product decided against the Profile newsletter toggle
> and chose **Option A** ("do nothing; document the endpoints as unused") from the phase-4
> plan, resolving its Open question 1. The toggle, its two `IAuthApiService` methods
> (`GetNotificationsAsync` / `UpdateNotificationsAsync`), the `NotificationsResponse` /
> `NotificationsDto` DTOs, both localized strings and the phase's test file were removed
> from `features/newapi`. **`POST api/profile/notifications` and
> `api/profile/notifications_update` are therefore deliberately unimplemented** — the
> newsletter opt-in remains set once at registration (`agree_newsletter`) and changeable
> only on the website. This is the only intentional v2.0 gap on the signed-in side; phases
> 1–3 are unaffected. See the phase-4 plan for the full record.

## Shared decisions (all phases inherit these)

- **Guest mode stays fully local.** No phase syncs guest-owned rows; the only guest↔server
  touchpoint remains the upgrade push from add-api-v2-sync. Signing into an existing account
  still does NOT migrate guest data (deliberate, see `MainWindow.OnSignInSucceeded`).
- **Conflict policy: client wins, one refresh-retry.** On `*_CONFLICT` the app fetches the
  server's current `updated_at` (via the matching `get` endpoint) and resends the local
  content once with it. Rationale: a single-user product — the machine where the user just
  acted holds their intent. Pull (phase 1) is **additive only** (never overwrites local
  rows), so the two policies cannot fight.
- **`server_updated_at` stays an opaque string**, echoed verbatim, never parsed (contract
  format has no timezone).
- **Sync work never blocks or surfaces in the UI**: background tasks, storage access only
  via `RunOnStorageAsync`, failures logged, `*Task` internal seams for tests (the
  `SettingsSyncService.PushTask` pattern).
- **API client growth stays in `SyncApiService`** over the shared `ApiHttpClient`; DTOs in
  `SyncApiDtos.cs`, snake_case, nullable response fields (PHP shape-quirk tolerance is the
  norm: a 2xx that fails to bind is "success without write-back" on pushes and "skip" on
  pulls).
- **Backend open questions carried over** from add-api-v2-sync (still unanswered):
  `configuration` opacity / macro-uid format (builder switch stays), `repeat_mode` values,
  per-unit `device_key` in `devices/*`, and the undocumented `presets/list`/`macros/list`
  response shapes (phase 1 states its assumption).

## Acceptance criteria

- [ ] Each phase plan exists with its full section set and is executable by `/coder`
  standalone in the order above; this umbrella carries no code changes of its own.

## Test strategy / QA / Review focus / Performance & quality / Risks

Owned by the phase plans — this document intentionally has no code-facing contract. See each
phase's sections.

## Open questions

Consolidated in the phase plans; the backend blockers are listed under Shared decisions.

## Review findings

_per phase — each phase plan carries its own journal; the umbrella itself has no code delta._

### Round 1 (2026-08-28) — whole-PR review of `features/newapi` vs `origin/main` — Ship: yes

Scope: the **entire branch delta** for PR #120 — 10 commits, 115 files, +23,350/−688 —
covering the four API v2.0 phases, `fix-duplicate-device-cards`, `fix-rgb-owner-isolation`,
the PR-review fixes (M1/M2/m1/m3/m5/m6), the newsletter-toggle revert,
`fix-owner-scoped-state-persistence`, `improve-api-error-surfacing`,
`fix-settings-bucket-identity`, `add-guest-session-expiry-warning`, and two merges from
`main`. The working tree is clean; the surface reconciles (`git status` empty, everything in
`git diff origin/main...HEAD`). **Local `main` is stale on this machine — the review is
against `origin/main`;** a review against local `main` would wrongly re-review PR #121/#122.

#### Plan conformance

Plan: this umbrella plus eleven per-change plans, each with its own reviewed-and-closed
journal (every one reached Ship: yes after its fix pass; every blocker raised in those rounds
is closed and mutation- or repro-tested). This round did **not** re-litigate the per-phase
findings; it verified the **cross-phase seams** no single-phase review could see:

- **`EnsureSchema` migration ordering** (`LocalStorageService.cs:570-641`) — verified sound.
  Column adds precede `MigrateMacrosToDevices`, which precedes `BackfillMissingUids`, which
  precedes the unique uid indexes. `HealDuplicateDeviceRows` runs at `:582` before the
  settings-identity columns exist (`:588-590`) and was read clause-by-clause: it references
  only pre-existing columns, so a legacy DB cannot crash there; its two-unit bail-out
  (`:992-1004`) and `MAX(id)`-over-PK predicate (`:1006-1014`) are correct.
- **The `image_scale` merge resolution** — all four `devices` column lists carry the merged
  union with reader ordinal 14 (`:4115`, `:4207`), both insert sites bind `$scale` (`:4164`,
  `:4299`), and the round-trip is closed end to end: `PersistDeviceCard` copies
  `card.ImageScale` (`HomeViewModel.cs:2460`) and `RecordToViewModel` restores it with the
  `> 0 ? : 1.0` fallback (`:2384`). A re-persist cannot zero a stored scale.
- **`RekeySettingsVendor` lock discipline** (`LocalStorageService.cs:1101-1148`) — takes
  `AcquireLock`; the one-shot backup runs inside the lock but outside the transaction (a
  failed backup cannot roll the migration back, nor vice versa); the vendor-0 sentinel guard
  is present; INSERT and DELETE carry the **same** `vendor_id = $from AND product_id = $pid`
  predicate, generated from one table-list constant, which is the property that prevents the
  "one missing AND clause wipes a configuration" failure the plan warned about.
- **Mid-pass re-key vs a running sync pass** — bounded and self-healing: a `devices/update`
  read under the stale vendor pair lands server-side under the alternate vendor, which the
  pull-side canonicalization alias (Open question 1 → option b, implemented) folds back.
- **Guest expiry-warning claim vs `ClearData`** — a claim racing session teardown updates
  zero rows and therefore does not announce; the failure direction is the plan's chosen
  "miss, never duplicate".

Verified independently after the final commit: full solution in Release — **App.Tests
1409/1409, Infrastructure.Tests 827/827, Installer.Tests 8/8, 0 failures.** No installer or
uninstaller files are in the delta, so no `/security-review` trigger applies. The one gRPC
contract change (`DeviceCatalogEntry` fields `settings_vendor_id = 38`,
`transport_vendor_ids = 39`) is additive, compatible in both directions, and requires
GrpcDependencies + DeviceHost + App to ship together — already stated in `f607fd4`'s message.

#### Code

- none — no new blockers or code defects found at the seams; all per-phase Code findings are
  closed in their own journals.

#### Should-fix

- [ ] **S1. PR #120's description still does not enumerate what the branch now carries** —
  process, not code. The original reviewer's m4 stands: ten workstreams sit behind messages
  like "adapting app to new api" and "other api changes". `f607fd4`'s body is thorough, but a
  merger reading the PR page sees none of it. **Fix:** update the PR description with the
  drafted phase/plan list (already prepared in-session) before merge.
- [ ] **S2. The settings-vendor re-key ships hardware-unverified.** Its DB-level behaviour is
  test-backed (`LocalStorageServiceSettingsVendorRekeyTests`), but acceptance criteria 1, 2
  and 7 of `../fix-settings-bucket-identity/fix-settings-bucket-identity-plan.md` require a
  Rampage with both cable and dongle (and Cosmo/Starlight over BT) and remain open. The
  migration touches 12 tables on real user databases. **Fix:** gate the release on that
  plan's QA steps 1–8; keep the `.pre-vendor-rekey.bak` recovery instruction in the ticket.
- [x] **S3. Two product decisions are guarded only indirectly** because `HomeViewModel` has
  no UI-marshalling seam: "bell entry only" is enforced by an IL-scan test (real — it was
  mutation-tested in both directions), and the session-warning misconfiguration diagnostic
  survives Release only via `Trace.WriteLine`. Both substitutes dissolve into ordinary
  observable tests the day `HomeViewModel` gains the `PostToUi` seam
  `DeviceDetailsViewModel` already has. **Fix:** adopt that seam as a small follow-up
  (already recommended in `../improve-api-error-surfacing/`'s journal).
  _Done (2026-08-28, `/coder`)._ `HomeViewModel` now has the same-shaped `PostToUi` seam,
  routing only the guest-expiry-warning announcement (`AnnounceSessionExpiringAsync`);
  `HomeViewModelSessionWarningTests` runs it inline and observes the real bell entry, the
  hidden banner, and the flag-before-entry ordering directly (the IL-scan guard stays for the
  compiled-path negative). `ProfileViewModel`'s recommendation in
  `../improve-api-error-surfacing/` remains a separate open follow-up.

#### Tests

- none failing; coverage gaps are the per-phase accepted ones (hardware paths, dispatcher-hop
  observability) and are recorded in their own journals rather than restated here.

#### Nits

- Several closed plans still show unticked `## Acceptance criteria` checkboxes although
  their journals confirm the criteria are met (e.g. `fix-duplicate-device-cards`) — cosmetic
  bookkeeping for whoever next reads those plans.
  _Done (2026-08-28, `/coder`)._ Ticked exactly what each journal states: all 9 in
  `fix-duplicate-device-cards`; 3, 4, 5, 6, 8, 9 in `fix-rgb-owner-isolation` (1, 2, 7 and
  10–12 stay open per its Round 1); 2–8 and 11 in `add-guest-session-expiry-warning`
  (1, 9, 10 not explicitly confirmed there). Journal wording untouched.
- The local `main` ref on this machine is behind `origin/main`; anyone reviewing or diffing
  locally should fetch first (this round nearly reviewed PR #121/#122's code as if it were
  ours).

#### What's done well

- Every phase carries a written review journal with stable finding handles, and the two
  places a reviewer overturned an "obvious" fix are now documented **in the code** where the
  next person would make the same mistake (`SetSessionExpiryWarningSent`'s no-upsert comment;
  `RekeySettingsVendor`'s symmetric-predicate comment).
- Migration surface expressed as data (`SettingsBucketTables`) rather than N hand-written
  statements — the strongest structural defence this branch has against the silent-wipe
  class of bug.
- Guards that guard: the bell-only IL scan and the rate-limit regression test were both
  mutation-tested (fix removed → test fails), not merely written.

#### Verdict

Ship: **yes** — no blockers; the three Should-fix items are a PR-description update, a
hardware QA gate, and a testability follow-up, none of which changes code on this branch.
First: 1) update the PR #120 description (S1); 2) run the settings-bucket hardware QA before
release (S2); 3) schedule the `PostToUi` seam follow-up (S3).
