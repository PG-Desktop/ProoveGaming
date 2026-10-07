# Improve API error surfacing (client-side, API v2.0)

Status: ready for `/coder`
Branch: `features/newapi`
Source of truth for error codes: `docs/knowledge-base/proove-gaming-launcher-api-v2.0-en.md`

## Goal

An audit of the App against the v2.0 Launcher API contract found five places where the client
throws away information the server already sent, and tells the user something less useful — or
actively misleading — than what actually happened. The worst case is Contact Us: the contract
defines a 60-second cooldown, a 5-per-day cap and a 10-minute duplicate block, and the app
answers all three with "Failed to send the message. Please try again." — an instruction that,
followed literally, re-hits the same limit. This plan reads the `error_code` the server already
returns, adds first-class handling for HTTP 429 (documented in v2.0, handled nowhere in `src/`),
stops the debounced profile auto-save from swallowing rejections, and closes the remaining gaps
in the avatar and multi-error paths. It is deliberately polish, not a redesign: no new dialogs,
no new error surfaces on existing controls, no backend work.

## Scope

### In

1. **Contact Us** — map `api/contact`'s documented codes onto distinct localized wording, with
   real guidance for the three anti-spam codes; align the client-side minimum lengths with the
   contract so the validation codes stop being reachable in the first place.
2. **HTTP 429** — a generic, transport-level notion of "rate limited" on `ApiResponse`
   (`Retry-After` captured), a shared localized message for interactive callers, and a cooldown
   so `SyncCoordinator` / `SettingsSyncService` stop hammering a limited server.
3. **Avatar upload** — add the three missing contract codes (`UPLOAD_ERROR`, `UPLOAD_FAILED`,
   `AVATAR_REQUIRED`) to `AuthErrorCodes` and to the `ProfileViewModel` mapping.
4. **Inline profile auto-save** — surface failures instead of discarding them (toast; see
   *Recommended approach* for why not a revert or inline field errors).
5. **Multi-error `errors[]`** — scan every entry for a code the resolver recognizes instead of
   blindly reading `Errors[0]`, in the three single-field resolvers that do so today.
6. **Characterization tests first** for the two paths that have **no** coverage at all (avatar
   upload mapping, profile auto-save), then the behavior change.
7. Localized strings added to **both** `Strings.en.axaml` and `Strings.uk.axaml`; new terms
   recorded in `docs/CONTEXT.md`; the four owning plans reconciled.

### Out

- Any backend / API change. Every gap here is client-side.
- New per-field error surfaces in the Contact Us dialog (it has one error line by design) — see
  *Options considered* §1.
- A live countdown timer / SEND re-enable clock on a rate-limited Contact Us dialog. Wording
  only; the countdown is noted as a follow-up.
- A multi-error UI for single-field forms. The one genuinely multi-field form (registration)
  **already** surfaces every entry via `ApiErrorMessages.ResolveAll` — see *Findings* §5.
- Retry/backoff for interactive one-shot calls (sign-in, avatar). 429 there is a message, not a
  retry loop.
- Reworking `ApiHttpClient`'s malformed-2xx detection, the sync conflict (`409`) handling, or
  the token-rejection latch. All out of scope and all currently correct.

## Findings from reading the code

These are the facts the plan is built on; each was read on `features/newapi`, not assumed. The
line numbers in the task brief had drifted, so the real locations are recorded here.

| # | Claim in the audit | What the code actually shows |
| - | ------------------ | ---------------------------- |
| 1 | Contact Us collapses every error | Confirmed. `HomeViewModel.DecideContactUsOutcome` (`src/ProoveGaming.App/ViewModels/HomeViewModel.cs:272`) is literally `response.Success ? Delivered : Failed`; the single string is set at `:343`. `ErrorCode` is never read. **New finding:** the client's own gate (`ContactUsFormValid`, `:238`) enforces only *non-blank* + max length, while the contract requires topic ≥ 3 and message ≥ 10 — so `TOPIC_TOO_SHORT` / `MESSAGE_TOO_SHORT` are genuinely reachable today, not just theoretical. |
| 2 | 429 handled nowhere | Confirmed. No `429`, `TooManyRequests`, `Retry-After` or backoff anywhere in `src/`. `ApiHttpClient.ReadResponseAsync` (`:120-126`) treats every non-2xx identically and writes the non-localized `$"Request failed ({status} {reason})"`. `ApiResponse` already carries `StatusCode` (`:37`), so the plumbing is half-built. |
| 3 | 3 of 5 avatar codes missing | Confirmed. Mapping is at `ProfileViewModel.cs:443-448`; `AuthErrorCodes` (`:35-37`) declares only `FileTooLarge` / `InvalidFileType`. `UPLOAD_ERROR`, `UPLOAD_FAILED`, `AVATAR_REQUIRED` appear nowhere in the repo. |
| 4 | Inline saves swallow failures | Confirmed. `ScheduleProfileSave` (`ProfileViewModel.cs:200-237`); the result is inspected **only** for the token-rejection bounce (`:227-232`); everything else falls into `catch { /* auto-save stays silent */ }` (`:235`). Debounce is `ProfileSaveDebounce`, a `private static readonly` with **no injectable seam**. |
| 5 | Only `errors[0]` surfaced | **Partly true, and narrower than stated.** `ApiErrorMessages.ResolveAll` (`src/ProoveGaming.App/Services/ApiErrorMessages.cs:39`) already iterates *every* entry, and registration already consumes it via `WelcomeViewModel.TryApplySignUpFieldErrors` (`:312-336`) — so the multi-field form is **already correct**. The first-entry-only pattern survives in exactly three places, all **single-field** forms: `ForgottenPasswordErrors.Resolve` (`:11-12`), `ProfileViewModel` delete-account (`:946-947`), and the nickname/email dialogs. |

Two further observations, recorded so `/reviewer` does not read them as regressions:

- `TryApplySignUpFieldErrors` has no `AuthField.Nickname` case. That is **correct by design** —
  the sign-up step renders only email, password and terms (nickname is a later step, confirmed
  by grep: no `SignUpNickname*` property exists). A nickname-only rejection correctly falls
  through to the generic banner. Do not "fix" this.
- The Contact Us error line (`Views/MainWindow.axaml:797-804`) already wraps and has no fixed
  `Width`, so the longer rate-limit wording fits in both languages. It is, however, emphasized
  by colour alone (`#FF383C`) — which CLAUDE.md forbids. Since this change puts materially more
  important text on that line, add the second cue there (§ File-level change list).

## Options considered

### 1. Contact Us — how much per-code detail?

- **A. Status quo + only the three anti-spam codes.** Smallest diff. Rejected: it leaves
  `MAIL_ERROR` (server accepted, mail relay died — retrying *is* right) indistinguishable from
  `MESSAGE_TOO_LONG` (retrying is pointless), and leaves the reachable `TOPIC_TOO_SHORT` gap.
- **B. Full per-code mapping onto the existing single error line, plus tighten the client gate
  to the contract's 3 / 10 minimums.** ✅ **Chosen.** One new resolver class mirroring the
  existing `ForgottenPasswordErrors`, one new localized string per meaningful code, zero new UI.
  Tightening the client gate means the validation codes become a defence-in-depth net rather
  than a user-visible path.
- **C. Field-level errors under each of the three inputs.** Rejected as disproportionate: the
  dialog is built around one `ContactUsError` line; per-field surfacing means three new error
  properties, three visibility bindings and a layout revision of a fixed-height (620×524) modal
  — a redesign, for codes that option B makes nearly unreachable.

### 2. HTTP 429 — where does it live?

- **A. Handle it in each caller off `StatusCode == 429`.** Rejected: four call sites re-deriving
  the same fact, and nothing stops the sync loop.
- **B. A transport-level `IsRateLimited` + `RetryAfter` on `ApiResponse`, set once in
  `ApiHttpClient`; interactive callers map it to one shared localized string; the two sync
  services treat it as an abort with a cooldown.** ✅ **Chosen.** Mirrors exactly how
  `IsNetworkError` already works — one place decides, every caller consumes.
- **C. Full retry-with-exponential-backoff in `ApiHttpClient`.** Rejected: it would block or
  silently delay interactive calls (the Contact Us dialog is already inert for the request's
  duration under a 15 s timeout) and is far more machinery than the contract's fixed windows
  need.

### 3. Profile auto-save failure — toast, inline error, or revert?

- **A. Revert the field to the server value.** Rejected, and it is the actively harmful option:
  the save fires 800 ms after typing stops, so a user still working in the field would have
  their text yanked out mid-edit; worse, the revert writes the bound property, which re-enters
  `OnUserPropertyChanged` → `ScheduleProfileSave` → another save → another revert. That is the
  re-entrant update storm CLAUDE.md explicitly forbids.
- **B. Inline per-field error.** Rejected as disproportionate: five fields, two of which
  (Country, Date of birth) are pickers with no error surface at all, so it means new error
  state + new visibility bindings on five controls — a redesign of the Account tab.
- **C. A toast via the already-injected `NotificationService`, value left on screen.**
  ✅ **Chosen.** Zero new UI, consistent with how avatar failures already report
  (`ShowAvatarNotification`), and honest: it tells the user the save failed while keeping their
  typing so the next edit retries. Coalesced to one toast per failed burst so a debounce storm
  cannot spam the notification stack.

### 4. Multi-error handling

- **A. Build a multi-error list UI.** Rejected: the only multi-field form already handles it;
  the remaining three sites are single-field forms where a list has nowhere to render.
- **B. Make the three first-entry resolvers scan all entries for a code they recognize.**
  ✅ **Chosen.** Fixes the real defect (a response like
  `[{UNKNOWN_CODE}, {EMAIL_NOT_FOUND}]` currently yields the *generic* message because entry 0
  is unrecognized) in ~3 lines each, and adds the missing tests for `ResolveAll`.

## Recommended approach

Six steps, ordered so that **tests land before the behavior changes they protect**.

**Step 0 — characterization tests for the two uncovered paths.** Avatar upload mapping and
profile auto-save have zero coverage today (verified: every existing `ProfileViewModel*Tests`
fake stubs `UploadAvatarAsync` as `throw new NotSupportedException()`). Pin current behavior
first, then change it. This step also introduces the one seam the auto-save tests need: an
`internal` `ProfileViewModel` constructor overload taking the debounce `TimeSpan`, exactly
mirroring the precedent in `SyncCoordinator` (`:65`, an `internal` ctor with an injectable
debounce used by `SyncCoordinatorTests`).

**Step 1 — transport.** Add `RetryAfter` (`TimeSpan?`) and `IsRateLimited` (`=> StatusCode == 429`)
to `ApiResponse`; populate `RetryAfter` in `ApiHttpClient.ReadResponseAsync` from the
`Retry-After` header, supporting both forms the RFC allows (delta-seconds and an HTTP-date,
which is clamped at zero so a skewed clock cannot produce a negative wait). Leave the existing
`$"Request failed (…)"` string exactly where it is: it is a *diagnostic* for logs, and the UI
layers now never show it for a 429 because they branch on `IsRateLimited` first. Localizing
inside `ApiHttpClient` would be wrong — the string is baked at parse time and would not follow a
later language switch.

**Step 2 — Contact Us.** New `ContactUsErrors` resolver (same shape and file placement as
`ForgottenPasswordErrors`), returning the resource key for a response. `DecideContactUsOutcome`
keeps its two-branch signature — the outcome is unchanged, only the *wording* of `Failed` is —
so its 6 existing tests stay green; the message selection is a new, separately-tested static.
`ContactUsFormValid` gains the contract's `>= 3` / `>= 10` minimums.

**Step 3 — avatar.** Three constants, three mapping arms. `UPLOAD_ERROR` and `UPLOAD_FAILED`
both mean "the server could not store it, retrying may work", and share the existing
`AvatarUploadFailed` wording rather than inventing two near-identical strings; `AVATAR_REQUIRED`
means the multipart part never arrived, which is a client bug, and gets the same retry wording
with its own resource key so it is distinguishable in a bug report.

**Step 4 — auto-save.** Inspect the result: token rejection keeps its existing bounce, 429 gets
the shared rate-limit toast, any other failure gets a "couldn't save" toast, and the silent
`catch` narrows to `OperationCanceledException` (a superseded debounce is not a failure) with
other exceptions reporting the same toast. Toasts marshal to the UI thread via
`Dispatcher.UIThread.Post`, matching `ShowAvatarNotification`.

**Step 5 — multi-error + sync cooldown.** The three resolvers scan all entries. `Aborts` in both
`SyncCoordinator` (`:873`) and `SettingsSyncService` (`:807`) gains a 429 arm; `SyncCoordinator`
records `_rateLimitedUntil` and `RunPassLoopAsync` waits out the remaining cooldown **once**
before its retry, then gives up and waits for the next signal — so a limited server is neither
hammered nor is dirty state stranded forever. The cooldown default (60 s, the contract's
tightest window) is injectable through the existing `internal` constructor for tests.

### Proportionality call (explicitly requested)

Four of the five items are small and self-contained. **Item 2's sync-cooldown half is the one
that is meaningfully larger** — it touches the concurrency-sensitive pass loop, needs its own
timing tests, and is the only part of this plan that can deadlock or stall sync if written
carelessly. It is kept in scope because a rate-limited server with a 3-second debounce is
exactly the scenario that *earns* a 429, but it is the clean split line: if implementation shows
the loop change growing beyond ~40 lines or the tests turning flaky, split Step 5's sync half
into `docs/plans/improve-api-error-surfacing-sync-backoff/` and ship steps 0-4 plus the
429 *message* work without it. The UI-facing 429 work does not depend on it.

## File-level change list

### Tests first (Step 0)

| File | Action | Intent |
| ---- | ------ | ------ |
| `tests/ProoveGaming.App.Tests/ProfileViewModelAvatarUploadTests.cs` | create | Characterization: pin today's avatar mapping (`FILE_TOO_LARGE` → too-large, `INVALID_FILE_TYPE` → invalid-type, unknown → generic), the token-rejection bounce, and the transport-exception branch — **before** adding the three new codes. Needs a fake `IAuthApiService` whose `UploadAvatarAsync` returns a settable `AvatarUploadResponse` (existing fakes all throw). |
| `tests/ProoveGaming.App.Tests/ProfileViewModelAutoSaveTests.cs` | create | Characterization: pin that editing `FirstName`/`LastName`/`Phone`/`Country`/`DateOfBirth` triggers exactly one debounced `UpdateProfileAsync`, that a burst coalesces, that `_suppressAutoSave` and an empty `SessionToken` suppress it, and that a token rejection raises `SessionTokenInvalid`. Uses the new `internal` debounce ctor with a short (~50 ms) debounce. |
| `tests/ProoveGaming.App.Tests/ApiErrorMessagesTests.cs` | create | First-ever coverage of `Resolve` / `ResolveAll`: every mapped code resolves to its field, unknown codes yield nothing, and a multi-entry `errors[]` yields **all** recognized entries in order (the case the contract's own example returns). |

### Source

| File | Action | Intent |
| ---- | ------ | ------ |
| `src/ProoveGaming.App/Services/ApiResponse.cs` | edit | Add `[JsonIgnore] TimeSpan? RetryAfter` and `[JsonIgnore] bool IsRateLimited => StatusCode == 429`, documented as transport diagnostics alongside the existing `StatusCode` / `MalformedPayload`. |
| `src/ProoveGaming.App/Services/ApiHttpClient.cs` | edit | In `ReadResponseAsync`, populate `RetryAfter` from `response.Headers.RetryAfter` (delta-seconds or HTTP-date → clamp negatives to zero). No other behavior change; the diagnostic fallback message stays. |
| `src/ProoveGaming.App/Services/AuthErrorCodes.cs` | edit | Add avatar codes `UploadError`, `UploadFailed`, `AvatarRequired`; add the Contact Us group `InvalidJson`, `TopicRequired`, `TopicTooShort`, `TopicTooLong`, `MessageRequired`, `MessageTooShort`, `MessageTooLong`, `RateLimitExceeded`, `DailyLimitExceeded`, `DuplicateMessage`. (`EmailRequired` / `InvalidEmail` / `MailError` already exist and are reused.) |
| `src/ProoveGaming.App/Services/ContactUsErrors.cs` | **create** | `internal static string ResolveKey(ApiResponse)` → the resource key for the failure, mirroring `ForgottenPasswordErrors`' shape. Order: network error → rate-limited (429 or `RATE_LIMIT_EXCEEDED`) → daily limit → duplicate → mail error → validation codes → generic. Scans all `errors[]` entries, not just the first. |
| `src/ProoveGaming.App/Services/ForgottenPasswordErrors.cs` | edit | Replace the `Errors[0]` read with a scan for the first entry whose code the switch recognizes, falling back to the old behavior when none matches. |
| `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` | edit | `SendContactUsAsync` sets `ContactUsError` from `ContactUsErrors.ResolveKey(response)` instead of the hardcoded `ContactUs_SendFailed`; `ContactUsFormValid` enforces the contract's topic ≥ 3 / message ≥ 10 minimums. `DecideContactUsOutcome` unchanged. |
| `src/ProoveGaming.App/ViewModels/ProfileViewModel.cs` | edit | (a) Avatar switch gains `UploadError`/`UploadFailed`/`AvatarRequired` arms plus a 429 arm; (b) `ScheduleProfileSave` reports failures by toast (rate-limit / generic) instead of discarding them, narrows the blanket `catch`, and coalesces to one toast per failed burst; (c) delete-account's `Errors[0]` read becomes the recognizing scan; (d) new `internal` ctor overload taking the debounce `TimeSpan`. |
| `src/ProoveGaming.App/Services/SyncCoordinator.cs` | edit | `Aborts` gains a 429 arm that records `_rateLimitedUntil` (from `RetryAfter`, default 60 s); `RunPassLoopAsync` waits out the remaining cooldown once before retrying, then yields to the next signal. Cooldown default injectable via the existing `internal` ctor. |
| `src/ProoveGaming.App/Services/SettingsSyncService.cs` | edit | `Aborts` gains the matching 429 arm (log + abort), keeping the two abort predicates symmetrical. |
| `src/ProoveGaming.App/Views/MainWindow.axaml` | edit | Contact Us error line: add `FontWeight="SemiBold"` so the emphasis is not colour-only (CLAUDE.md). No `Width` added — the block already wraps. |

### Resources (both languages, mandatory)

| File | Action | Intent |
| ---- | ------ | ------ |
| `src/ProoveGaming.App/Resources/Strings.en.axaml` | edit | Add: `ContactUs_Error_RateLimited`, `ContactUs_Error_DailyLimit`, `ContactUs_Error_Duplicate`, `ContactUs_Error_MailError`, `ContactUs_Error_InvalidEmail`, `ContactUs_Error_TopicLength`, `ContactUs_Error_MessageLength`, `Api_Error_TooManyRequests`, `ProfileView_Notification_ProfileSaveFailed`, `ProfileView_Notification_AvatarRequired`. Keep `ContactUs_SendFailed` as the generic fallback. |
| `src/ProoveGaming.App/Resources/Strings.uk.axaml` | edit | The same ten keys in Ukrainian. Wording must be actionable, not literal: the rate-limit string tells the user to wait ~a minute, the daily string that the 5-message cap is reached, the duplicate string to change the message or wait. |

### Documentation (mandatory per CLAUDE.md)

| File | Action | Intent |
| ---- | ------ | ------ |
| `docs/CONTEXT.md` | edit | Add one-line entries for the new terms: **Retry-After**, **Rate limit (429)**, **Contact Us anti-spam window**, **ContactUsErrors**, **Profile auto-save toast**. Update the existing **ContactUsOutcome** entry to note that `Failed` now carries per-code wording. |
| `docs/plans/add-contact-us-dialog/add-contact-us-dialog-plan.md` | edit | Live sections only: the acceptance criterion asserting a single failure message no longer matches; record the per-code wording and the new 3 / 10 minimums. Add a dated note pointing here. **Do not touch `## Review findings`.** |
| `docs/plans/contact-us-and-update-check/contact-us-and-update-check-plan.md` | edit | Same reconciliation for whatever it states about Contact Us failure wording; dated pointer to this plan. |
| `docs/plans/add-api-v2-sync/add-api-v2-sync-plan.md` | edit | Record that the pass now also aborts on 429 with a cooldown (its abort contract is currently network-error + token-rejection only). |
| `docs/plans/fix-profile-and-auth-validation/fix-profile-and-auth-validation-plan.md` | edit | Record that inline profile auto-save is no longer silent on failure. |
| `docs/plans/add-forgot-password/add-forgot-password-plan.md` | edit | Record the `errors[]` scan replacing the first-entry read. |

## Acceptance criteria

Contact Us:

1. Given the server answers `api/contact` with `RATE_LIMIT_EXCEEDED` (or HTTP 429) → the dialog
   stays open with the draft intact and shows the rate-limit message, which tells the user to
   **wait** — the word "try again" alone must not be the whole instruction.
2. Given `DAILY_LIMIT_EXCEEDED` → the message states the daily cap is reached and that the user
   should try again later/tomorrow; it is a **different string** from criterion 1.
3. Given `DUPLICATE_MESSAGE` → the message states the same message was already sent and to
   change it or wait; a **third distinct string**.
4. Given `MAIL_ERROR`, `INVALID_EMAIL`, a topic/message length code, or an unrecognized code →
   the corresponding mapped string (generic `ContactUs_SendFailed` for the unrecognized case) is
   shown; no code path shows the raw `$"Request failed (…)"` diagnostic.
5. Given a topic of 2 characters or a message of 9 characters → SEND stays disabled (client gate
   now matches the contract's 3 / 10 minimums).
6. Given a successful send → behavior is byte-for-byte unchanged: dialog closes, toast + bell
   entry appear. The 6 existing `DecideContactUsOutcome` tests still pass unmodified.
7. Editing any field after a failure clears the error, as today.

HTTP 429:

8. Given any endpoint answers 429 with `Retry-After: 30` → `ApiResponse.IsRateLimited` is true
   and `RetryAfter` is 30 seconds; given `Retry-After` is an HTTP-date in the past → `RetryAfter`
   is `TimeSpan.Zero`, never negative; given no header → `RetryAfter` is null and
   `IsRateLimited` is still true.
9. Given a sync pass receives 429 at any endpoint → the pass aborts, dirty state is preserved,
   a warning is logged, and **no further API call is issued** until the cooldown elapses.
10. Given the cooldown elapses and dirty state remains → exactly one retry pass runs; if it is
    limited again the coordinator stops and waits for the next signal (no unbounded loop).
11. Given an interactive call (avatar, profile save) receives 429 → the user sees the shared
    localized rate-limit message, not the generic failure text.

Avatar:

12. Given `UPLOAD_ERROR`, `UPLOAD_FAILED` or `AVATAR_REQUIRED` → a mapped localized toast
    appears; none of the three falls into the unknown-code default any more.
13. Given `FILE_TOO_LARGE` / `INVALID_FILE_TYPE` → unchanged wording (characterization tests
    from Step 0 still pass).
14. Given `INVALID_OR_EXPIRED_TOKEN` → `SessionTokenInvalid` still fires and no avatar toast is
    shown.

Profile auto-save:

15. Given a first/last name, telephone, country or date-of-birth edit whose save is rejected by
    the server → a toast reports the failure within ~1 s of the debounce firing, **and the value
    the user typed remains on screen** (no revert, no re-entrant save).
16. Given a burst of edits producing one failed save → exactly **one** toast is shown, not one
    per property changed.
17. Given a save superseded by a newer edit (debounce cancellation) → **no** toast (a cancelled
    save is not a failure).
18. Given the save succeeds → no toast at all (silent success is preserved).

Multi-error:

19. Given `errors: [{UNKNOWN_CODE}, {EMAIL_NOT_FOUND}]` from `api/forgotten` → the
    **`EMAIL_NOT_FOUND`** message is shown, not the generic one (today it shows generic).
20. Given the contract's own two-error example (`INVALID_EMAIL` + `PASSWORD_TOO_SHORT`) at
    registration → both the email and password fields show their errors simultaneously
    (already true; now covered by a test that locks it in).

Cross-cutting:

21. Every new user-facing string exists in **both** `Strings.en.axaml` and `Strings.uk.axaml`
    with the same key set; no new control renders localized text at a fixed `Width`; the Contact
    Us error line carries a non-colour emphasis cue.
22. `dotnet build ./src/ProveGaming.slnx` is clean and `dotnet test ./src/ProveGaming.slnx -c Release`
    is fully green.

## Test strategy

This is a **behaviour-change** plan (`improve-*`), so the skill's rule applies: confirm existing
coverage, keep it green and extend it; **add characterization tests before changing anything
that has none.** The inventory below was verified against the current branch.

### Already covered — must stay green, extend in place

| Path | File | Extend with |
| ---- | ---- | ----------- |
| Contact Us rules & outcome (14 tests, incl. 6 `DecideContactUsOutcome`) | `HomeViewModelContactUsTests.cs` | Message-selection cases for each new code (`ContactUsErrors.ResolveKey`), the 429 case, unknown-code → generic, and the new 3 / 10 minimum-length boundary cases (2/3 and 9/10). Existing tests must not be edited — the outcome enum is unchanged by design. |
| `ApiHttpClient` envelope (9 tests, 401 + one 400) | `ApiHttpClientTokenRejectionTests.cs` | A new sibling file for 429 (see below) rather than growing the token-rejection file past its topic. |
| Forgot-password resolution (12 tests) | `WelcomeViewModelForgotPasswordTests.cs` | A multi-entry `errors[]` case proving the scan picks the recognized code (criterion 19). |
| Sync abort contract (~40 tests) | `SyncCoordinatorTests.cs` | 429 abort preserves dirty state and issues no further calls; cooldown elapse → exactly one retry; still-limited → stops. Reuses the existing `RecordingSyncApi` (settable per-operation handlers) and the injectable-debounce `internal` ctor. |
| Error-code constants (14 of 21 pinned) | `AuthErrorCodesTests.cs` | Add `InlineData` rows for all 13 new constants **and** the 7 currently-unpinned ones, so the theory becomes exhaustive. |

### Not covered today — characterization tests land FIRST (Step 0)

- `ProfileViewModelAvatarUploadTests.cs` (new) — pin the current 2-code mapping, the
  token bounce and the exception branch, **then** add the three new codes + 429.
- `ProfileViewModelAutoSaveTests.cs` (new) — pin debounce/coalescing/suppression/token-bounce,
  **then** add the failure-toast, cancellation-is-not-failure, and one-toast-per-burst cases.
- `ApiErrorMessagesTests.cs` (new) — first coverage of `Resolve`/`ResolveAll`, including the
  contract's multi-error example (criterion 20).
- `ApiHttpClientRateLimitTests.cs` (new) — `IsRateLimited`, `Retry-After` as delta-seconds, as
  an HTTP-date, absent, and in the past (clamped to zero).

### Conventions `/test-writer` must follow

- **xUnit v2, hand-written fakes.** `ProoveGaming.App.Tests` references **no** mocking library
  (Moq exists only in `ProoveGaming.Infrastructure.Tests` and is unavailable here). Follow the
  house pattern: `private sealed` nested fake implementing only the members under test, all
  others `throw new NotSupportedException();`.
- `NotificationService` and `LocalStorageService` are used **real**
  (`new LocalStorageService("Data Source=:memory:")`, disposed via `using var`), as in every
  existing `ProfileViewModel*Tests`.
- The 429/transport tests reuse the `internal ApiHttpClient(HttpMessageHandler, string)` seam.
  The existing `StubHandler` is a `private sealed` nested class in
  `ApiHttpClientTokenRejectionTests`; **duplicate it** in the new file rather than promoting it
  — promoting it to a shared helper would edit a file full of passing tests for no behavioral
  reason. (If `/test-writer` prefers sharing, that is acceptable but must be called out.)
- Assembly-wide test parallelization is **disabled** (`AssemblyInfo.cs`) for Avalonia dispatcher
  affinity. Toast assertions go through `Dispatcher.UIThread.Post`, so tests must drain with
  `Dispatcher.UIThread.RunJobs()` — the pattern in `DeviceDetailsPresetApplyTests`.
- Timing tests use the **injected** short debounce/cooldown, never a real 800 ms / 60 s wait.

### Deliberately not automated

- The actual visual rendering of the wrapped error line and the SemiBold cue in both languages —
  covered by QA step 9.
- Real HTTP 429 from the live backend (the anti-spam windows are server-side and cannot be
  driven deterministically from a unit test) — covered by QA steps 2-4.
- Avalonia UI-thread affinity of the toast surface beyond `RunJobs()` draining.

## QA (manual test steps)

1. Launch the app and sign in with a normal account. Open **Contact Us** from the profile
   dropdown in the Home header.
2. Fill in a valid email, a topic and a message, and press **SEND**. *Expected:* the loader
   runs, the dialog closes, a green "message sent" toast appears and a matching entry is added
   to the notification bell list.
3. Immediately reopen **Contact Us**, enter a **different** topic and message, and press
   **SEND** again within one minute. *Expected:* the dialog stays open with your text intact and
   shows a message telling you to **wait** before sending another message — not a bare
   "please try again".
4. Wait just over a minute, then send the **exact same** topic and message you sent in step 2.
   *Expected:* a different message, saying this message was already sent and to change it or
   wait. (If the account has already sent 5 messages in 24 hours, you will instead see the
   daily-limit message — also acceptable, and it must be different wording again.)
5. In the Contact Us dialog, type a 2-character topic and a short (under 10 characters) message.
   *Expected:* the **SEND** button stays greyed out and cannot be pressed.
6. Switch the app language to Ukrainian (Settings) and repeat steps 3 and 5. *Expected:* every
   message appears fully in Ukrainian, is not cut off, and does not overflow the dialog edges.
7. Go to **Profile → Account**. Change your first name, then click elsewhere and wait ~2 seconds.
   *Expected:* no error and no toast — the change is saved silently.
8. Disconnect your network (turn off Wi-Fi), change your last name, wait ~2 seconds.
   *Expected:* a toast appears saying the profile changes could not be saved, **and the text you
   typed is still in the field** — it must not revert or disappear. Reconnect and edit the field
   again; the change saves normally.
9. Still in Profile, change first name, last name and telephone in quick succession while
   offline. *Expected:* exactly **one** failure toast, not three.
10. Click the avatar and upload a file larger than 2 MB. *Expected:* a toast saying the image is
    too large. Then upload a `.bmp` or other unsupported file. *Expected:* a toast saying the
    format is not supported. Then upload a valid PNG under 2 MB. *Expected:* the avatar updates
    and a success toast appears.
11. Use **Forgot password** on the sign-in screen with an email that is not registered.
    *Expected:* the message says the email was not found — not a generic failure.
12. Leave the app running and signed in for a few minutes while changing device settings.
    *Expected:* no repeated error toasts, no visible stutter, and the app never freezes.

## Review focus

- `localization` — ten new keys must exist in **both** resource files with identical key sets;
  Ukrainian wording is 30-50% longer, and the error line must not overflow or gain a fixed
  `Width`; the emphasis cue must not be colour-only.
- `async/await` — the auto-save toast and the sync cooldown must not block; no `.Result` /
  `.Wait()`; `OperationCanceledException` must stay distinguishable from a real failure.
- `UI-thread affinity` — toasts raised from the pool thread (auto-save runs in `Task.Run`,
  `TokenRejected` fires on a pool thread) must marshal via `Dispatcher.UIThread.Post`.
- `thread-safety` — `SyncCoordinator._rateLimitedUntil` is read and written from the pass loop
  and the signal path; it must be guarded by the existing `_gate` or be an atomic read/write,
  and the cooldown wait must not be held under the lock.

## Performance & quality

No hot path is touched: none of these code paths runs per-frame, per-poll or at startup. The
per-keystroke path **is** touched indirectly — `OnUserPropertyChanged` → `ScheduleProfileSave`
fires on every keystroke in the profile fields — so the added failure handling must stay on the
**post-debounce** side (it runs once per completed save attempt, never per keystroke), and must
not allocate per-keystroke. The chosen "toast, no revert" UX is partly a performance decision:
the revert option would write a bound property from a `PropertyChanged` handler and re-enter the
same handler, which is precisely the re-entrant update storm CLAUDE.md forbids.

`ApiHttpClient` gains one header read per response — negligible, and only on the response path.
`SyncCoordinator`'s cooldown adds one `DateTimeOffset` comparison per pass and one bounded
`Task.Delay`; it must not hold `_gate` across that delay. `ContactUsErrors` is a static switch
over string constants with no dictionary allocation per call. No new `IDisposable` is
introduced; no new event subscriptions are added, so there is nothing new to unsubscribe.

## Risks

- **Sync cooldown stalling sync.** The largest risk in the plan. A bug in the cooldown could
  leave `_rateLimitedUntil` permanently in the future and silently stop all sync. Mitigations:
  cooldown is bounded (clamped to a sane maximum even if the server sends an absurd
  `Retry-After`), the retry is single-shot, and any later `Signal()` re-arms a pass normally.
  This is the piece to split out if it grows (see *Proportionality call*).
- **Tightening the Contact Us gate is a visible behaviour change.** Users who could previously
  press SEND with a 2-character topic now cannot. This is intentional — the server rejected
  those anyway — but it changes a UI affordance and must appear in the QA notes and the
  dialog's owning plan.
- **Toast fatigue on profile auto-save.** A user typing offline could, without the coalescing
  requirement, generate a toast per field. Criterion 16 exists specifically to prevent this and
  must not be dropped as a "nit".
- **Ukrainian string length.** The rate-limit and daily-limit strings are the longest new text in
  the dialog; verify the 620×524 modal in `uk` before shipping.
- **`Retry-After` as HTTP-date with client clock skew** could compute a negative or enormous
  wait; clamping at both ends is required, not optional.
- **Characterization tests could pin a bug as "correct".** The Step 0 tests describe *current*
  behaviour; where current behaviour is one of the five defects, the test must be written
  against the **target** behaviour in the same change and clearly marked, not frozen.

## Open questions

None of these block `/coder` — each has a stated default that is safe to implement. Flagging
them so the human can overrule before review.

1. **Rate-limit countdown.** Default: **wording only**, no live countdown and no timed SEND
   re-enable. A countdown means a per-second UI timer in the dialog, which is a feature, not
   polish. Overrule if product wants the timer.
2. **`Retry-After` in the visible message.** Default: **no** — the message says "wait a moment"
   rather than interpolating a server-provided number of seconds, because `api/contact` may omit
   the header entirely and a message that sometimes has a number and sometimes does not reads as
   broken. `RetryAfter` is still captured and used by the sync cooldown.
3. **`UPLOAD_ERROR` vs `UPLOAD_FAILED` wording.** Default: **share** the existing
   `AvatarUploadFailed` string — the contract does not distinguish them in a way a user could
   act on differently. Overrule if support wants them distinguishable in screenshots.
4. **Should a 429 on interactive calls also start a client-side cooldown** (e.g. disabling the
   avatar picker briefly)? Default: **no** — message only. The sync loop is the only automated
   repeat-caller and it is the one that gets the cooldown.
5. **`StubHandler` sharing.** Default: duplicate it into the new 429 test file rather than
   refactoring a green test file. `/test-writer` may promote it to a shared internal helper
   instead if it prefers, but should say so.

## Review findings

### Round 1 — 2026-08-27 — Ship: no

Delta reviewed: working tree of `worktree-agent-a6b42fabf66e0f94a` vs branch point `02f8d19`
(`features/newapi`). No commits — 20 modified + 8 untracked entries, reconciled against
`git status --porcelain`. Verified by the reviewer, not taken on report:
`dotnet build ./src/ProveGaming.slnx -c Release` clean (0 errors);
`dotnet test ./src/ProveGaming.slnx -c Release` green — App 1313/1313,
Infrastructure 821/821, Installer 8/8.

#### Plan conformance

- **Implemented as planned:** every File-level change list row. Transport (`ApiResponse`,
  `ApiHttpClient`), `AuthErrorCodes` (3 avatar + 10 Contact Us), new `ContactUsErrors`,
  `ForgottenPasswordErrors` scan, `HomeViewModel` Contact Us region, `ProfileViewModel`
  (a) avatar arms + 429, (b) auto-save toasts, (c) delete-account scan, (d) internal debounce
  ctor, `SyncCoordinator` + `SettingsSyncService` 429 arms, `MainWindow.axaml` SemiBold cue,
  10 keys in both languages, `docs/CONTEXT.md` + all five owning plans reconciled.
- **Scope constraint honoured.** `LocalStorageService.cs` is absent from the delta, and the
  `HomeViewModel.cs` changes are confined to the Contact Us region (constants, the SEND gate,
  the failure-wording line). The concurrent agent's device-card / notification regions are
  untouched. Not a finding — recorded because it was an explicit risk.
- **Deviates from plan:** avatar / auto-save characterization drives extracted statics rather
  than the delivery path — see ruling (a); accepted.
- **Missing / partial:** acceptance criterion 9 is not satisfied on all paths — see **B1**.
- **Accepted limitation (per plan):** no live 429 from the backend, no rendered-wording test —
  QA steps 2-4, 6, 9. Residual risk: the uk rate-limit and daily-limit strings are the longest
  new text in a fixed 620x524 modal and have not been seen rendered.

#### Requested rulings

- **(a) Extracted statics instead of the fire-and-forget path — ACCEPTED.**
  `SetAvatarFromFileAsync` (`ProfileViewModel.cs:434`) launches the upload with a discard *and*
  writes into the real `LocalApplicationData` avatars folder while decoding an Avalonia
  `Bitmap`. An end-to-end test would be non-deterministic and would pollute the developer's own
  profile directory — that is not a test, it is a liability. Extracting `ResolveAvatarErrorKey`
  and `ProfileSaveFailureKey` is the same shape the plan itself chose for `ContactUsErrors`, and
  the mapping is where the behaviour lives. **Residual gap, recorded not waived:** the
  token-rejection early return, and the `Dispatcher.UIThread.Post` marshalling in
  `ShowAvatarNotification` / `ReportProfileSaveFailure`, are verified by reading only — yet
  "UI-thread affinity" is named in this plan's own Review focus. There is a cheap way to close
  it that the repo already uses: `DeviceDetailsViewModel` exposes a `PostToUi` seam which
  `DeviceDetailsPresetApplyTests:166` sets to `action => action()`, precisely because
  `RunJobs()` proved fragile on CI. Adding the same seam to `ProfileViewModel` would make toast
  delivery assertable with no dispatcher. Optional, not required for this round.
- **(b) Fixture default changed instead of weakening the gate — CORRECT CALL.** The two
  failures were incidental dependence on a 9-character default, not assertions about the
  minimum: `CompleteForm_IsValid` asserts that a complete form is valid, and
  `OverLimitTopicOrMessage_IsInvalid` asserts the *maxima*. Weakening the gate would have
  defeated acceptance criterion 5, which is the point of the change. The new default keeps both
  tests testing what their names say, and the coder added `UnderLimitTopicOrMessage_IsInvalid`
  plus `WhitespaceOnlyPaddingDoesNotSatisfyTheMinimums`, so the boundary is now pinned
  explicitly rather than implicitly. The other user of `Valid()`, `MalformedEmail_IsInvalid`,
  varies only the email, so no test silently changed meaning.
- **(c) The `_lifetimeCts.Token`-outside-the-gate fix — CORRECT AND COMPLETE.** `Dispose`
  (`SyncCoordinator.cs:997`) sets `_disposed = true`, then cancels, then disposes, all under
  `_gate`. The loop (`:220-228`) now checks `_disposed` and captures `.Token` inside that same
  lock, so it can never read off a disposed source. A `Dispose` landing *after* the capture is
  also safe: `Cancel()` precedes `Dispose()`, and `Task.Delay` short-circuits to
  `Task.FromCanceled` on an already-cancelled token without registering a callback. Those are
  the only two `_lifetimeCts` accesses in the file, so the fix is complete. Good catch — this
  was a genuine permanent-sync-deadlock.
- **(d) `MaxRateLimitCooldown` untested — NOT acceptable as-is, but do NOT inject the ceiling.**
  Injecting it would add a ninth parameter to an already eight-parameter internal constructor
  purely for test reach, and would still leave the null / zero / negative branches implicit.
  Cheaper and strictly better: extract the whole selection as a pure static — see **S3**.
- **(e) Structural glance at the new test files — CLEAN.** No duplicated or dead test methods
  in any of the six new files. Verified by parsing method names for repeats and by confirming
  that `[Fact]` + `[Theory]` counts equal the public test-method count in every file (RateLimit
  6+1=7, ContactUsErrors 7+3=10, ForgottenPasswordErrors 7+1=8, ApiErrorMessages 4+2=6,
  AutoSave 9+2=11, AvatarUpload 4+2=6), which also rules out an un-attributed method silently
  not running.

#### Code

- [x] **B1. A rate-limit cooldown is silently discarded, and the follow-up path bypasses it entirely** — `src/ProoveGaming.App/Services/SyncCoordinator.cs:211-263` (`RunPassLoopAsync`) — Blocker
  **Problem:** `TakeRateLimitCooldown()` (`:985`) *clears* `_rateLimitedUntil` every time it is
  called, but the caller only honours the value when `!cooldownRetryUsed`. Once the single retry
  has been used, the next 429 arms a cooldown that is then read, discarded, and never waited
  out. Control falls straight into the follow-up branch (`:254-260`), which starts another pass
  immediately with no wait at all.
  **Why it blocks:** concretely — a signal arrives, the pass 429s, the loop waits the cooldown
  and retries once (`cooldownRetryUsed = true`). The retry 429s again and re-arms
  `_rateLimitedUntil`. Meanwhile the user edited something during that retry, so `Signal()` into
  `StartPass()` saw `_passRunning` and set `_followUpRequested = true` (`:158`). The loop now
  takes the fresh cooldown, throws it away because `cooldownRetryUsed` is already true, sees
  `_followUpRequested`, and continues into a full pass against a server that just answered 429 —
  with zero delay. Every further edit repeats this. That is the hammering the feature exists to
  prevent, and it contradicts acceptance criterion 9 ("no further API call is issued until the
  cooldown elapses"). It is invisible today because
  `RateLimited_AbortsThePassPreservingDirtyStateAndRetriesExactlyOnce` never signals during the
  pass, so `_followUpRequested` stays false.
  **Solution options:**
  - Option A (correct): make the cooldown gate *every* iteration rather than only the automatic
    retry. Peek `_rateLimitedUntil` at the top of the loop — do not clear it unless it is about
    to be awaited — and wait out any remaining time before running a pass, whatever triggered
    that pass. Keep `cooldownRetryUsed` for its actual job, bounding *unsignalled* retries, so a
    follow-up signal is still honoured, just not early.
  - Option B (minimal): split peek from consume. Only clear `_rateLimitedUntil` on the branch
    that actually awaits it; in the follow-up branch, if a cooldown is still pending, leave it
    armed and exit the loop with `_followUpRequested` intact so the next `Signal()` reschedules
    normally. Smaller diff, but it leans on a later signal to make progress.

- [x] **S1. A UTF-8 BOM was added to both `Strings.*.axaml` files** — `src/ProoveGaming.App/Resources/Strings.en.axaml:1`, `Strings.uk.axaml:1` — Should-fix
  **Problem / why it matters:** neither file had a BOM at `02f8d19`; both have one now (verified
  by comparing the first three bytes against the branch point). `MainWindow.axaml` already had
  one and is unchanged, so this is not a repo convention being applied — it is a tool artifact
  from the editing pass. It matters beyond tidiness: the whole point of grouping the ten new
  keys into one contiguous block was to keep the concurrent agent's merge trivial, and an
  encoding change on **line 1 of both files** is exactly the hunk that will collide with another
  agent editing the same resource files.
  **Fix:** rewrite both files without the BOM, leaving only the intended key block in the diff.

- [x] **S2. A superseded save that is already in flight still raises a failure toast** — `src/ProoveGaming.App/ViewModels/ProfileViewModel.cs:237-266` (`ScheduleProfileSave`) — Should-fix
  **Problem / why it matters:** the cancellation check happens *before* the API call (`:226`),
  never after it. A save that has already reached `UpdateProfileAsync` is not cancellable, so
  when a newer edit supersedes it the old task still completes, still sees a failure, and still
  calls `ReportProfileSaveFailure`. Scenario: the user types, 800 ms elapses, save #1 goes out
  over a slow link; the user types again, 800 ms elapses, save #2 goes out; both fail; two
  toasts. Acceptance criterion 17 says a superseded save produces **no** toast, and the comment
  at `:259-261` asserts a guarantee the code does not provide ("only the last one reaches the
  API and only it can report" — untrue once a request is in flight).
  **Fix:** re-check the token before reporting — `if (cts.IsCancellationRequested) return;`
  immediately after the `await`, ahead of the `result.Success` check — and correct the comment
  to say the debounce coalesces *scheduling*, not in-flight requests.

- [x] **S3. The `Retry-After` clamp has no test, and the ceiling is unreachable from one** — `src/ProoveGaming.App/Services/SyncCoordinator.cs:950-953` — Should-fix
  **Problem / why it matters:** this plan's Risks section calls clamping "required, not
  optional", and the cooldown selection is the one piece of new logic with no coverage.
  `MaxRateLimitCooldown` (absurd `Retry-After` to 5 minutes), the `> TimeSpan.Zero` guard, and
  the fallback-to-default path are all decided inline inside `Aborts`, where the only way to
  reach them is to run a pass and wait out the result. Testing the ceiling today would mean a
  real multi-minute wait, so it simply is not tested.
  **Fix:** extract the selection as a pure function —
  `internal static TimeSpan SelectCooldown(TimeSpan? retryAfter, TimeSpan fallback)` — and have
  `Aborts` call it. Four table-driven asserts then cover null, zero-or-negative, in-range and
  over-ceiling with no timing and no constructor churn. Preferred over making the ceiling a
  constructor parameter, which buys less and costs an extra argument on an already wide internal
  ctor.

- [x] **S4. The new scan helpers null-guard the loop, then dereference unsafely in the fallback** — `src/ProoveGaming.App/Services/ForgottenPasswordErrors.cs:47-48`, `src/ProoveGaming.App/ViewModels/ProfileViewModel.cs:1052-1053` — Should-fix
  **Problem / why it matters:** both helpers iterate with `error?.ErrorCode` — correctly, because
  a partially-bound envelope really can contain a null entry (`ApiHttpClientTokenRejectionTests:32`
  keeps one in its fixture deliberately, citing this plan family's review focus). Two lines later
  the fallback does `response.Errors[0].ErrorCode` with no null guard. The envelope
  `{"errors":[null,{"error_code":"SOMETHING_UNKNOWN"}]}` with no top-level `error_code` reaches
  that fallback (nothing matched) and throws `NullReferenceException`. For delete-account it
  escapes an async `[RelayCommand]` through a `try/finally` that has no `catch`; for
  forgot-password it escapes the resolver into the view-model. The expression is inherited
  verbatim from the pre-change code, so this is not a regression — but it was copied into a
  brand-new method that demonstrably knows the null case exists, and the new tests only cover the
  null-entry path where a *recognized* code follows, so the crash path stays green.
  **Fix:** `response.Errors[0]?.ErrorCode` in both places.

##### Round 1 fix pass — 2026-08-27 (`/coder`)

Code findings B1, S1, S2, S3, S4 closed; the three Nits: the section-sign typo is fixed, the
`ContactUsErrors` layering and `Retry-After: 0` nits are deliberately left for a later round.

- **B1** — took Option A. `RunPassLoopAsync` now decides *whether* another pass should run
  (signalled follow-up, or the one unsignalled retry) and then calls the new
  `WaitOutRateLimitCooldownAsync`, which gates **every** iteration whatever triggered it. The
  cooldown is no longer consumed by a caller that then discards it: `TakeRateLimitCooldown` is
  gone, and `_rateLimitedUntil` is cleared only on the path that actually awaits it.
  `cooldownRetryUsed` now bounds unsignalled retries only, so a follow-up signal is still
  honoured — just never early. Traced against the reported scenario: after the automatic retry
  is spent, a 429 plus a follow-up signal now waits out the freshly-armed cooldown before the
  next pass instead of running one immediately.
- **S1** — BOMs stripped from both `Strings.*.axaml`; `MainWindow.axaml`'s pre-existing BOM
  kept. Both resource diffs are now pure 12-line insertions with no line-1 hunk.
- **S2** — `if (cts.IsCancellationRequested) return;` added immediately after the
  `UpdateProfileAsync` await, ahead of the `result.Success` check; the coalescing comment now
  says the debounce coalesces *scheduling* and points at the re-check for the in-flight case.
- **S3** — cooldown selection extracted to
  `internal static TimeSpan SelectCooldown(TimeSpan? retryAfter, TimeSpan fallback)`; `Aborts`
  calls it. No constructor parameter added, per the ruling.
- **S4** — `response.Errors[0]?.ErrorCode` in both fallbacks.

Verified: `dotnet build ./src/ProveGaming.slnx` clean (0 errors, no new warnings);
`dotnet test ./tests/ProoveGaming.App.Tests` 1313/1313. The Tests findings remain open for
`/test-writer` — note that B1's regression test is now writable, since the production fix is in.

#### Tests

- [x] **B1 regression test** — a 429 pass with a `Signal()` raised *during* it must not issue a
  further API call until the cooldown elapses. `SyncCoordinatorTests.cs`: drive it by calling
  `coordinator.Signal()` from inside a rate-limited `MacroUpdateResponse` handler, then assert
  the endpoint log does not grow past the expected count before the cooldown. This is the case
  the existing rate-limit tests structurally cannot catch.
- [x] **S3** — table-driven tests for the extracted `SelectCooldown`: null header to fallback,
  `TimeSpan.Zero` to fallback, in-range passthrough, over-ceiling clamped to 5 minutes.
- [x] **S4** — extend `ForgottenPasswordErrorsTests` and add a delete-account case with
  `errors: [null, {UNKNOWN}]` and no top-level code; today that throws rather than falling back.
- [x] **S2** — assert that no toast is raised for a save superseded while in flight (needs
  either the `PostToUi` seam from ruling (a) or a notification-count probe).
- [ ] Optional, follows ruling (a): if the `PostToUi` seam is added, cover the avatar
  token-rejection early return and confirm that a rate-limited avatar upload shows the shared
  wording *through the real delivery path*, not only through the mapping.

##### Round 1 test pass — 2026-08-27 (`/test-writer`)

Tests findings B1, S2, S3, S4 closed; the optional `PostToUi` item is deliberately left open.
Suite: 1328/1328 App, 821/821 Infrastructure, 8/8 Installer, Release.

- **B1** — `RateLimited_SignalDuringThePass_StillWaitsOutTheCooldownBeforeCallingAgain`
  (`SyncCoordinatorTests.cs`). Raises `Signal()` from inside the rate-limited handler, then
  asserts the gap between every consecutive API call respects the injected cooldown. **Verified
  to fail against the defect:** temporarily reintroducing the discard produced
  `call 3 came 1 ms after call 2; the 400 ms cooldown was not respected`. 1 ms against a 300 ms
  floor, so the margin is not jitter-sensitive.
- **S3** — `SelectCooldown_ClampsAtBothEnds` (7 rows) plus a negative-input case: no header,
  zero, in-range both directions, exactly at the ceiling, and over it. Closes the Risks item
  that calls clamping "required, not optional"; the five-minute ceiling is now asserted
  directly rather than by waiting one out.
- **S4** — `ForgottenPasswordErrorsTests` gained the `[null, {UNKNOWN_CODE}]`-with-no-top-level
  case and a null-only case. The delete-account half turned out to be reachable without any
  production change: new `ProfileViewModelDeleteAccountTests` drives the real
  `ConfirmDeleteAccountCommand` (3 tests — the null-entry fallback, a recognized code in a
  later entry, and a token rejection in a later entry).
- **S2** — `ASaveSupersededWhileInFlight_CompletesQuietlyAndLetsTheNewerSaveRun`, using a
  first-call gate so save #1 is genuinely parked at the API when the newer edit lands.
  **Known limitation, stated rather than papered over:** the toast itself is not asserted.
  Every side effect of that branch (`_notification.Show` and `SessionTokenInvalid` alike) goes
  through `Dispatcher.UIThread.Post`, which does not run without a dispatcher, and `RunJobs()`
  is documented in `AssemblyInfo.cs` / `DeviceDetailsPresetApplyTests` as unreliable here. The
  test pins the scenario and its observable consequences; the silence is argued from the code.
  A `PostToUi` seam on `ProfileViewModel` (the pattern `DeviceDetailsViewModel` already uses)
  would close this properly and would also unlock the avatar token-rejection and
  rate-limited-avatar delivery assertions — recommended as a small follow-up production change,
  not made here.

#### Nits

- `ContactUsErrors.Has` delegates to `SettingsSyncService.HasErrorCode` — `src/ProoveGaming.App/Services/ContactUsErrors.cs:57`. A Contact Us resolver reaching into the sync service for a generic envelope predicate is upside-down layering; the helper would sit more naturally on `ApiResponse` or a small shared `ApiErrors` static. Reuse over duplication was the right instinct, just from the wrong home.
- `HomeViewModelContactUsTests.cs:13` reads "the v2.0 20 contract" — a lost section sign; should be "v2.0 §20".
- `Retry-After: 0` is treated as "no header" and falls back to the 60 s default (`SyncCoordinator.cs:950`, `retryAfter > TimeSpan.Zero`). A server explicitly saying "retry immediately" gets a full minute instead. Conservative rather than harmful, but it is not what the header said.

#### What's done well

- **The `_lifetimeCts` catch.** Reading `.Token` outside the gate against a source that `Dispose`
  cancels *and* disposes would have thrown `ObjectDisposedException` into a loop that only
  catches `OperationCanceledException`, leaving `_passRunning = true` forever and killing sync
  for the process lifetime. Self-caught during the coder's own diff review, and fixed the right
  way (capture under the gate) rather than by widening the catch.
- **Priority-ordered code matching in `ContactUsErrors`, with distinctness actually asserted.**
  `ResolveKey_GivesTheThreeAntiSpamCodesThreeDistinctKeys` is the test that earns its place — a
  theory alone would pass if two codes were pointed at the same key, which is the most likely way
  this feature would silently rot.
- **The three anti-spam strings are genuinely actionable** ("wait about a minute", "limit of 5
  per day", "change the topic or message"), not three paraphrases of "try again" — which was the
  entire point of the plan.
- **Deterministic sync tests.** Keying assertions off `CurrentPassTask` completion (which by
  contract completes only after the cooldown *and* the retry) instead of wall-clock polling, and
  proving Retry-After preference through the log rather than by timing, avoids the flaky timing
  tests the plan warned about.
- **`Theory_CoversEveryConstant`** turns `AuthErrorCodesTests` from a list someone must remember
  to extend into one that fails when they forget, and names the missing constant.
- **Plan reconciliation was done properly** — five owning plans updated with dated notes, live
  sections corrected, and no `## Review findings` journal rewritten.

#### Verdict

Ship: no — one Blocker. **B1** means a rate-limited server is still hammered whenever the user is
actively editing, which is both the common case and the exact scenario the cooldown was added
for; acceptance criterion 9 is not met on that path. Everything else is small, and the change is
otherwise well-built and well-tested.

First: (1) fix **B1** and add the signal-during-cooldown regression test; (2) strip the two added
BOMs (**S1**) before the concurrent agent's merge; (3) **S2** and **S4**, both one-liners, then
**S3**'s extraction.

### Round 2 — 2026-08-28 — fix pass (`/coder`)

The round-2 PR review confirmed one Minor against this plan (**m1**): the cooldown wait sat at the
BOTTOM of `RunPassLoopAsync`'s body, so it gated every iteration *within* one run of the loop but
never the first iteration of the NEXT run. A second 429 exits the loop with `_rateLimitedUntil`
freshly armed and unconsumed; a debounced `Signal()` a few seconds later starts a new run whose
first pass calls the server immediately, inside the window it had just named — a steady one request
per ~3 s against a persistently limited server while the user keeps editing. A side effect of the
stale marker was one extra pointless wait-then-pass long after the window had elapsed. Acceptance
criterion 9 held within a run but not across runs.

**Fix:** `if (!await WaitOutRateLimitCooldownAsync()) return;` moved to the TOP of the
`while (true)` body; the exit decision under the gate is unchanged. The marker now deliberately
survives loop exit and is waited out by the first iteration of whichever run comes next, whatever
signalled it. The loop's leading comment records the reasoning. Regression test left to
`/test-writer`: after the retries-exactly-once scenario, raise `Signal()` *after* the loop has
exited and assert the next API call is separated from the previous one by at least the cooldown —
the existing `RateLimited_SignalDuringThePass_…` test covers only the signal-still-pending branch
of the same `if`.

### Round 2 — 2026-08-28 — test pass (`/test-writer`)

The prescribed regression landed as
`SyncCoordinatorTests.RateLimited_SignalAfterTheLoopExits_StillWaitsOutTheArmedCooldown`: the
retries-exactly-once scenario runs to its end (loop exited, marker armed by the second 429), then
one `Signal()` starts a fresh run, and every consecutive-call gap in the fake API's timestamped
call log — decisively the 2→3 gap, the first call of the NEW run — must be at least 0.75× the
injected 400 ms cooldown. Pre-fix that gap was only the ~100 ms debounce, so the assertion
distinguishes fixed from broken without wall-clock sleeps (same gap technique as the round-1 B1
test directly above it). Full `ProoveGaming.App.Tests` in Release: **1422/1422**.
