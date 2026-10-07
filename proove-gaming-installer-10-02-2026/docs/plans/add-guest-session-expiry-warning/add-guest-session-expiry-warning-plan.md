# add-guest-session-expiry-warning — Warn a guest before the session ends

> **Note (2026-09-24):** `appsettings.json` is now compiled into the App and DeviceHost assemblies and no longer shipped as a file, so an installed build has no settings file to edit. Steps below that edit `appsettings.json` next to the EXE now mean: change `src/ProoveGaming.App/appsettings.json` in the repository and rebuild (see `docs/plans/fix-repository-audit-findings`, 2026-09-24 notes).

Date: 2026-08-27.

## Goal

A guest session lasts `AppSettings.SessionTimeHours` (48 by default, from
`src/ProoveGaming.App/appsettings.json`) and then ends silently: `SessionService`'s one-shot timer
flips `_timerExpired`, and the session is actually torn down the next time the app goes to tray
(`SessionService.CheckAndExpire` returns unless `_isInTray`). When it happens the guest's devices,
presets, macros and settings are wiped (`LocalStorageService.ClearData` deletes every
`owner_kind = 'guest'` row) with no prior warning — the user's only chance to keep that work is to
register an account, and today nothing tells them the clock is running out. This plan raises a
notification a configurable lead time (default **3 hours**) before the session expires, so the guest
can act while the data still exists.

## Scope

**In:**
- A second one-shot timer in `SessionService` that fires a lead-time warning, scheduled from the same
  persisted `started_at` the expiry timer already uses, so it survives an app restart.
- A latch (`ExpiryWarningDue`) alongside the event, because the warning can become due **before** the
  consumer exists — see the ordering trap in Recommended approach.
- One-time-per-session semantics persisted in `app_session`, so restarting inside the warning window
  does not re-notify on every launch.
- The notification itself as a **bell entry only** — one row in the notification list, persisted under
  the guest owner. Explicitly not an in-app banner and not a Windows toast (see Scope → Out).
- A configurable lead time (`SessionExpiryWarningMinutes`, default 180) mirroring `SessionTimeHours`.
- en + uk strings.

**Out:**
- Any change to when the session actually **expires**, or to the tray-gated teardown. This plan only
  adds a warning; expiry behaviour is untouched.
- Signed-in accounts. Their 30-day token expiry is a different mechanism with its own handling
  (`docs/plans/add-global-token-expiry/`).
- **The in-app banner (`NotificationService.Show`) and the Windows toast
  (`SystemNotificationService.Show`).** Product decided (2026-08-27) that this warning is a bell entry
  and nothing more. `AnnounceUpdateAvailable` uses all three surfaces; this feature deliberately does
  not, so `/coder` must not "complete the pattern" by copying it wholesale, and `/reviewer` must not
  read the two missing calls as an omission. The accepted consequence is recorded under Risks.
- Any call to action beyond the notification text (no "Register now" button, no modal). If product
  wants a deep link into sign-up later, the notification is the hook it will attach to.
- A repeat/escalating reminder (e.g. also at T-30min). One warning per session.

## Options considered

### A. Second timer inside `SessionService` (recommended)
`SessionService` already owns the expiry timer, the `startedAt` arithmetic and the
`AppSettings.SessionTimeHours` read. Adding a warning timer next to it keeps every "when does this
session end" decision in one class, and that class is already hermetically testable (its tests inject
a synchronous dispatch and mutate `AppSettings`).
- Pros: one owner for session timing; reuses the restart-survival math; existing test harness applies
  unchanged; no new dependency in a service that today has none but a dispatch delegate.
- Cons: `SessionService` must expose both an event and a latch (see the ordering trap), which is
  slightly more surface than an event alone.

### B. Poll from `HomeViewModel`
Piggyback on the 60-second battery poll timer already running in `HomeViewModel` and compare
`DateTime.UtcNow` against the session deadline on each tick.
- Pros: no new timer object; the view model already owns the notification surfaces.
- Cons: puts session-lifetime logic in a view model that has no business knowing it, and couples the
  warning's accuracy to an unrelated poll interval. Worse, it re-derives the deadline from data
  `SessionService` already owns — the kind of duplicated rule that drifts. Rejected.

### C. Evaluate only on existing events (tray transitions, device connect/disconnect)
No timer at all; check the deadline whenever something already happens.
- Pros: zero new timing machinery.
- Cons: fails the exact case that matters. A guest who leaves the app minimised overnight generates
  no events, so the warning would arrive late or never — and "minimised in tray" is precisely when
  expiry bites. Rejected.

**Decision: A.**

## Recommended approach

### 1. Configurable lead time

Add `SessionExpiryWarningMinutes` to `AppSettings` (default `180`) and to
`src/ProoveGaming.App/appsettings.json`, loaded the same way `SessionTimeHours` already is.

**Minutes, not hours, deliberately.** `SessionTimeHours` is an `int`, so the shortest testable
session is one hour; with an hours-granular lead time QA could not produce a warning without a
45-hour wait. With minutes, QA sets `SessionTimeHours: 1` and `SessionExpiryWarningMinutes: 45` and
sees the warning ~15 minutes in.

### 2. Timer and latch (`SessionService`)

- `StartActiveTimer` gains a sibling `StartWarningTimer(DateTime startedAt)`, scheduled at
  `SessionTimeHours - SessionExpiryWarningMinutes` from `startedAt`.
- If that instant has already passed (restart inside the window, or a restored session), the warning
  becomes due **immediately** rather than being skipped.
- **Skip the warning entirely when `SessionExpiryWarningMinutes >= SessionTimeHours * 60`** — a lead
  time at or beyond the whole session is a misconfiguration, and firing at t=0 would be noise. This
  also keeps the existing `SessionServiceTests` green: several of them set `SessionTimeHours = 0` to
  force expiry, and must not start emitting warnings.
- `StopActiveTimer` stops and disposes both timers and clears both latches; `EndSession` and
  `Dispose` therefore already cover the new one.
- New public surface:
  - `event Action? SessionExpiringSoon`
  - `bool ExpiryWarningDue { get; }` — set when the warning becomes due, cleared by
    `AcknowledgeExpiryWarning()`.

**The ordering trap this latch exists for.** `App.OnFrameworkInitializationCompleted` calls
`session.RestoreSession(...)` at `App.axaml.cs:99`, but `MainWindow` — and therefore `HomeViewModel`,
which owns every notification surface — is not resolved until `:125`. A session restored already
inside the warning window would raise the event into the void. The same shape of defect was just
fixed for notifications raised before an owner exists (see
`docs/plans/fix-owner-scoped-state-persistence/`), and the comments at `App.axaml.cs:130-133` and
`:144-148` show the codebase already reasons about this ordering. So the consumer must be able to
**ask**, not only listen: `HomeViewModel` subscribes to the event *and* checks `ExpiryWarningDue`
during its own initialization.

### 3. Once per session, across restarts (`LocalStorageService`)

- `app_session` gains `expiry_warning_sent INTEGER NOT NULL DEFAULT 0`, added through the existing
  guarded `AddColumnIfMissing` path (the column list at `LocalStorageService.cs:211` and the
  migration block around `:560-590` are the reference).
- Two thin methods: `bool GetSessionExpiryWarningSent()` and `int SetSessionExpiryWarningSent()`. The
  setter returns the number of rows its `UPDATE` affected, because with no `app_session` row the
  statement is a silent no-op that still returns normally — the caller must claim the warning only on
  `> 0`, or it would announce on a flag that was never written. It is deliberately **not** an upsert:
  inserting the row here would fabricate an `app_session` with an empty `session_uuid`, and
  `InitializeSession` early-returns when a row exists, so that phantom row would permanently stop the
  next real guest session from persisting its identity.
- Both methods are called **only from the storage executor**. Read-then-write is atomic against other
  executor items, not against a bare `Task.Run`.
- The row is deleted with the session (`ClearData` / `ClearSession` already `DELETE FROM app_session`),
  so a new guest session starts with a clean flag automatically — no reset code needed.

`SessionService` deliberately does **not** take a storage dependency: it has none today, its tests
construct it with a single delegate, and adding one would ripple through that suite. The flag is
checked and set by the consumer, which already holds `LocalStorageService`.

### 4. Raising the notification (`HomeViewModel`)

A single call — the bell entry, persisted under the guest owner:

```
AddNotification(title, body, SessionExpiringIcon);
```

`AnnounceUpdateAvailable` (`HomeViewModel.cs:~885`) is the nearest precedent and adds
`NotificationService.Show` (the in-app banner) and `_systemNotifications.Show` (the Windows toast)
alongside it. **Both are deliberately omitted here**, per the product decision recorded in Scope.
The entry persists and carries an unread badge, so the user meets it the next time they look at the
app; the trade-off that they will not be interrupted while the window is hidden is accepted and
recorded under Risks.

Guarded by, in order: the session is a guest session; `GetSessionExpiryWarningSent()` is false; then
set the flag before raising, so a crash between the two cannot produce a double notification on the
next launch (a missed warning is better than a duplicated one).

_Implementation note (2026-08-27, `/coder`)._ The check and the write run inside **one**
`RunOnStorageAsync` item, not two awaited hops, and the item returns whether this call claimed the
warning. The handler genuinely runs twice for a single warning — once from the constructor's
`ExpiryWarningDue` check and once from the `SessionExpiringSoon` that `SessionService` dispatched
before the constructor subscribed — and with two separate hops the second call's read overtakes the
first call's write, producing exactly the double notification this guard exists to prevent. Executor
items never interleave, so the single-item claim is what makes "once per session" true; the
write-before-announce ordering is unchanged.

_Fix-pass note (2026-08-28, `/coder`, Round 1 S1)._ The item now claims on
`SetSessionExpiryWarningSent() > 0` rather than on the call returning normally, so a missing
`app_session` row yields "not claimed" and nothing is announced — the plan's stated failure direction
(miss, never duplicate).

`SessionExpiringIcon` reuses the shipped `avares://ProoveGaming.Common/Assets/Icons/Info.svg` at the
same 22×22 sizing as `DeviceConnectedIcon`. **No new asset** — there is no clock/timer glyph in
`ProoveGaming.Common/Assets/Icons/`, and inventing one is out of scope.

### 5. Strings

`Notification_GuestSessionExpiringTitle` and `Notification_GuestSessionExpiringBody_Format` in **both**
`Strings.en.axaml` and `Strings.uk.axaml` (`LanguageResourceParityTests`, newly arrived from `main`,
fails the build otherwise). The body takes the whole remaining hours as its single argument, so a
non-default lead time still reads correctly. Wording must say what is at stake — that guest data is
removed when the session ends — not merely that a session is ending.

## File-level change list

1. `src/ProoveGaming.App/AppSettings.cs` — new `SessionExpiryWarningMinutes` property + `Load()` line.
2. `src/ProoveGaming.App/appsettings.json` — `"SessionExpiryWarningMinutes": 180`.
3. `src/ProoveGaming.App/Services/SessionService.cs` — warning timer, `SessionExpiringSoon` event,
   `ExpiryWarningDue` latch, `AcknowledgeExpiryWarning()`, teardown in `StopActiveTimer`/`Dispose`.
4. `src/ProoveGaming.App/Services/LocalStorageService.cs` — `expiry_warning_sent` column (CREATE +
   guarded migration) and its getter/setter.
5. `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` — subscribe to `SessionExpiringSoon`, check
   `ExpiryWarningDue` at init, `SessionExpiringIcon`, and the three-surface announcement guarded by
   the persisted flag.
6. `src/ProoveGaming.App/Resources/Strings.en.axaml` — the two keys.
7. `src/ProoveGaming.App/Resources/Strings.uk.axaml` — the two keys.
8. `tests/ProoveGaming.App.Tests/SessionServiceTests.cs` — extend; see Test strategy.
9. `tests/ProoveGaming.App.Tests/HomeViewModelSessionWarningTests.cs` — new; see Test strategy.
10. `docs/CONTEXT.md` — one-line entries for the warning and the new setting.

## Acceptance criteria

- [ ] Given `SessionTimeHours = 1` and `SessionExpiryWarningMinutes = 45`, when a guest session starts
  and 15 minutes elapse, then exactly one bell entry appears and the unread badge increments.
- [x] Given the warning is raised, then **no** in-app banner and **no** Windows toast are shown —
  `NotificationService.Show` and `SystemNotificationService.Show` are never called on this path.
- [x] Given a guest session already inside the warning window, when the app is restarted, then the
  warning is raised once on that launch even though the due instant passed while the app was closed.
- [x] Given the warning has already been raised for a session, when the app is restarted again inside
  the same window, then no further warning appears (`expiry_warning_sent` is honoured).
- [x] Given a new guest session is started after the previous one ended, then the flag is clear and
  that new session can warn again.
- [x] Given the warning becomes due while the app is minimised to tray, then the bell entry is still
  created — the warning is **not** tray-gated, unlike expiry — and it is visible when the window is
  restored.
- [x] Given `SessionExpiryWarningMinutes >= SessionTimeHours * 60` (including the `SessionTimeHours = 0`
  used by existing tests), when a session starts, then no warning is ever raised and no timer is left
  running.
- [x] Given a guest signs up or signs in before the warning is due, when `EndSession` runs, then the
  warning timer is stopped and no warning is raised afterwards.
- [ ] Given the user is signed in (not a guest), then no expiry warning is ever raised.
- [ ] Given the warning fires, when the bell is opened after an app restart, then the entry is still
  there (it was persisted under the guest owner, not memory-only).
- [x] Given either language is selected, then both new strings resolve — `LanguageResourceParityTests`
  passes.

## Test strategy

New feature (`add-*`) — the following are mandatory coverage, in `tests/ProoveGaming.App.Tests`.

`SessionServiceTests` (extend — the file is already hermetic: synchronous dispatch, `AppSettings`
mutated per test, so no new harness is needed):
- `StartSession` with a lead time larger than the session raises nothing and leaves no timer.
- `RestoreSession` with a `startedAt` inside the window makes `ExpiryWarningDue` true immediately and
  raises `SessionExpiringSoon`.
- `RestoreSession` with a fresh `startedAt` does not.
- The warning fires **without** `GoToTray` — the explicit contrast with the expiry tests directly
  above it.
- `EndSession` before the due instant prevents the warning (mirrors
  `EndSession_PreventsPendingTimerExpiry`).
- `AcknowledgeExpiryWarning` clears the latch; a second subscriber does not re-fire it.
- Existing expiry tests must stay green **unmodified** — several set `SessionTimeHours = 0` and are
  the guard that the skip rule works.

`HomeViewModelSessionWarningTests` (new, following the existing HomeViewModel test fixtures with a
real temp-file `LocalStorageService`):
- Event → exactly one bell entry, persisted under the guest owner, **and** neither
  `NotificationService.Show` nor `SystemNotificationService.Show` is called. Assert the absence
  explicitly: the omission is a product decision, and without a test the next person copying
  `AnnounceUpdateAvailable` silently reinstates both.
- Persisted flag already set → nothing raised.
- Flag is written **before** the announcement (assert ordering, since the plan chooses "miss rather
  than duplicate").
- `ExpiryWarningDue` already true at init → the warning is raised without the event (the
  `App.axaml.cs` ordering trap).
- Signed-in owner → nothing raised.

Deliberately **not** covered: real elapsed-time behaviour of the 45-hour default (tests drive short
configured values only); `App.axaml.cs` wiring itself, which has no headless harness — QA step 5
owns it.

_Test note (2026-08-28, `/test-writer`)._ Both files are in place and green
(`tests/ProoveGaming.App.Tests/SessionServiceTests.cs`, `HomeViewModelSessionWarningTests.cs`), with
two deviations from the shapes described above, forced by the test assembly rather than chosen:

- **The bell entry itself is not observed at runtime.** `AnnounceSessionExpiringAsync` ends with
  `Dispatcher.UIThread.InvokeAsync(() => AddNotification(...))`, and this assembly has no Avalonia
  application: the dispatcher is owned by whichever thread touched it first and nothing pumps its
  queue, so that job never executes. Pumping it from a dedicated owner thread was tried and
  **crashed the test host** (no catchable exception), so it was backed out. What the tests assert
  instead is exact rather than approximate: a call that claimed the warning parks on that hop and
  its task stays incomplete, a call that did not claim returns a completed task — so "announced
  once" is *one pending announcement and no others*, and the double-invoke race is still pinned.
  The entry's persistence under the guest owner is covered separately by driving `AddNotification`
  during a guest session. Closing the gap properly needs a one-line production seam — a `PostToUi`
  indirection on `HomeViewModel`, as `DeviceDetailsViewModel` already has — which is a decision for
  `/coder`, not this round.
- **"Neither surface is called" is asserted structurally.** `SystemNotificationService.Show` is not
  virtual and, on Windows, neither throws nor logs, so a spy on its logger would pass whether or not
  it was called (verified: it logged nothing when called directly). The test scans the compiled IL
  of the warning path for calls to `NotificationService.Show` / `SystemNotificationService.Show`,
  with `OnUpdateAvailable` as the positive control in the same test, and additionally asserts the
  real in-app banner stayed hidden at runtime.

Also not covered: the `Math.Max(1, SessionExpiryWarningMinutes / 60)` body argument. Localized
strings do not resolve without an Avalonia application (`ResourceStrings.Get` returns the key), so
the formatted body is unobservable here; an internal static helper for the hours computation would
make it testable.

_Fix-pass note (2026-08-28, `/coder`, Round 1 N5)._ That helper now exists —
`internal static int HomeViewModel.WarningLeadHours(int minutes)` — and the announcement calls it, so
the hours computation is directly testable without an Avalonia application. The formatted body string
itself remains unobservable here; QA step 7 still owns it.

_Seam note (2026-08-28, `/coder`, whole-PR review S3 — docs/plans/add-api-v2-conformance)._ The
`PostToUi` seam the first test-note asked for now exists on `HomeViewModel` (same shape as
`DeviceDetailsViewModel`'s), and `AnnounceSessionExpiringAsync` marshals its `AddNotification`
through it. The tests run the seam inline, so the "one pending announcement" technique is retired:
the golden-path test observes the REAL bell entry (title, body, icon, unread badge) and the real
`NotificationService` staying hidden; the flag-before-entry ordering is shown with a capturing seam
that releases the held hop; the double-invoke race asserts one visible entry, not one pending task;
and the persistence test drives the real warning path instead of calling `AddNotification` directly.
The IL-scan guard stays — it proves no banner/toast call exists in the compiled path, which no
runtime test can. N1's fire-and-forget fault note is also resolved as a side effect: the hop no
longer awaits, and the `try`/`catch` now covers the seam invocation.

## QA (manual test steps)

Steps 1–4 need a settings override so you are not waiting 45 hours. Ask a developer for a build (or
edit `appsettings.json` next to the app's executable) with **`SessionTimeHours: 1`** and
**`SessionExpiryWarningMinutes: 45`** — that makes the warning appear about 15 minutes into a guest
session. Step 6 uses the shipped defaults.

1. Launch the app and choose **Continue as guest**. Connect a device and create something you would
   not want to lose (a preset, or a macro). Note the time.
2. Leave the app open. About 15 minutes after starting the session, watch the bell icon in the header.
   *Expected:* it gains an unread badge, and opening it shows a new entry saying the guest session is
   ending soon and that guest data will be removed. *Also expected — deliberately:* **no** pop-up bar
   inside the app and **no** Windows notification. This warning lives only in the bell list; seeing a
   banner or a system toast is a bug, not a bonus.
3. Close the app completely and reopen it (still as a guest, same session). *Expected:* the bell still
   contains that entry, and **no second warning appears** — you are told once per session.
4. Repeat step 1 from scratch (sign out of the guest session, or let it end, then start a new guest
   session). *Expected:* the new session warns again at its own 15-minute mark.
5. Start another guest session, minimise the app to the tray (close the window — it hides to tray) and
   leave it there past the 15-minute mark, then restore the window. *Expected:* the bell already
   carries the unread warning entry — it was created while the window was hidden. Nothing pops up
   over your desktop while the app is in the tray; that is intended.
6. With a **default** build (no overrides), start a guest session and immediately register an account
   from it. *Expected:* no expiry warning appears at any point afterwards — the warning is for guests
   only.
7. Switch the app language to Ukrainian and repeat step 2. *Expected:* the message is fully in
   Ukrainian, is not cut off and does not overflow.
8. Throughout: the app must never freeze, and the warning must not steal focus from what you are doing.

## Review focus

1. `resource disposal` — two timers now exist; both must be stopped and disposed by
   `StopActiveTimer`, `EndSession` and `Dispose`, with no dangling `Elapsed` handler. This is the most
   likely place to leak.
2. `UI-thread affinity` — `System.Timers.Timer.Elapsed` runs on a pool thread; the warning must reach
   the notification surfaces through the same `_dispatch` indirection the expiry path already uses,
   and `AddNotification` must run on the dispatcher.
3. `thread-safety` — the pool-thread timer callback races `EndSession` on the UI thread; ensure a
   session ended a moment before the callback cannot still announce.
4. `localization` — both keys in both files; the format argument must survive a non-default lead time.

## Performance & quality

None of this is on a hot path. Two one-shot `System.Timers.Timer` instances per guest session, one
extra column read at startup and one write per session. The timer callback must not do storage work
inline on the pool thread — the flag read/write goes through the storage executor
(`RunOnStorageAsync`) like every other write, and only the announcement is marshalled to the UI
thread.

## Risks

- **A bell-only warning can go unseen until the user opens the app.** This is the accepted cost of
  the product decision to drop the banner and the Windows toast. It matters more than it looks: the
  session is torn down when the app goes to tray, so a guest who leaves the app minimised for the
  last three hours meets the warning and the wipe at the same moment — the entry survives, but the
  chance to act on it may not. Recorded so the decision is revisited deliberately if support reports
  data loss, rather than rediscovered as a defect.
- **Double notification across a crash.** Mitigated by writing the flag before announcing; the
  accepted failure direction is a missed warning, not a duplicated one. Called out so review does not
  read the ordering as accidental.
- **A misconfigured lead time silences the feature.** `SessionExpiryWarningMinutes >= SessionTimeHours * 60`
  disables the warning by design; if someone sets it that way in production the feature vanishes with
  no error, so it is logged when the warning timer is skipped. _Corrected 2026-08-28 (Round 1 S3):
  the original wording said "Debug is enough" — it is not. `Debug.WriteLine` is
  `[Conditional("DEBUG")]` and does not exist in the shipped Release build, which is the only build a
  support case comes from. The diagnostic is `Trace.WriteLine`: TRACE is defined in Release too, and
  `DefaultTraceListener` routes it to `OutputDebugString`, where DebugView captures it on a user's
  machine. An `ILogger` was considered and rejected — the App registers only `AddDebug()`, whose
  provider emits nothing without an attached debugger, so a logger would be equally invisible in
  Release while giving `SessionService` the first dependency this plan deliberately keeps it free of.
  If the App ever gains a real logging sink (file/Serilog, as `DeviceHost` has), move this there._
- **Restored-session ordering.** If the latch is dropped in review as "redundant with the event", the
  restart-inside-the-window case silently stops working — and it is the case most users will hit,
  because 48 hours is longer than most people leave an app running. The plan's test for it is the
  guard.
- **Notification fatigue is low here** (once per 48-hour session), but if product later adds a second
  reminder, the single boolean flag becomes insufficient — it would need a per-threshold record.

## Open questions

1. **Wording and call to action.** The plan ships an informational message. If product wants it to say
   explicitly "register to keep your settings" — or to open the sign-up screen when clicked — say so
   before `/coder` runs; the click-through would add a view-model dependency the plan currently avoids.
2. **Should the warning also appear for a guest session that is about to end because all devices
   disconnected?** That is the other expiry path (`GoToTray(allDevicesDisconnected: true)`) and it has
   no lead time at all — it fires immediately. Out of scope here; flagging because a user could
   reasonably expect a warning there too.

## Review findings

_filled in by /reviewer_

### Round 1 (2026-08-28) — Ship: yes

Reviewed surface: the working-tree delta for this feature only — `AppSettings.cs`, `appsettings.json`,
`SessionService.cs`, `LocalStorageService.cs` (the `expiry_warning_sent` column + its two accessors),
`HomeViewModel.cs`, `Strings.en/uk.axaml`, `docs/CONTEXT.md`, `SessionServiceTests.cs` (extended) and
`HomeViewModelSessionWarningTests.cs` (new). The rest of the tree on this branch is prior work and was
not re-reviewed.

#### Plan conformance

- **Implemented as planned:** all ten File-level change list items. The lead time is configurable and
  minutes-granular; the second one-shot timer schedules off the same persisted `startedAt`; the latch
  plus event pair exists for the ordering trap; the once-per-session flag is a column on `app_session`
  that dies with the row (`ClearSession` / `ClearData` both `DELETE FROM app_session`,
  `LocalStorageService.cs:2887` and `:2937`), so a new guest session starts clear with no reset code;
  both strings are in both language files; `docs/CONTEXT.md` gained both terms.
- **Bell-only decision honoured.** `OnSessionExpiringSoon` / `AnnounceSessionExpiringAsync`
  (`HomeViewModel.cs:913-960`) call `AddNotification` and nothing else. No `NotificationService.Show`,
  no `_systemNotifications.Show`. Verified by reading the method, not only by the structural test.
- **Not tray-gated.** `RaiseExpiryWarning` (`SessionService.cs:185`) never consults `_isInTray`, in
  deliberate contrast with `CheckAndExpire` (`:197`). Covered by
  `WarningTimer_WhenItElapses_FiresWithoutGoToTray`.
- **Skip rule correct.** `leadTime >= sessionLength` returns before any timer is created
  (`SessionService.cs:131`), so `SessionTimeHours = 0` — the configuration several existing expiry
  tests run under — can never warn. Those existing tests are **unmodified** in the diff (the only
  changes to `SessionServiceTests.cs` are the new `IDisposable` teardown, new usings, and appended
  cases), and the skip is separately pinned by the `(0, 180)` row of
  `StartSession_WithALeadTimeCoveringTheWholeSession_RaisesNothing_AndLeavesNoTimer`.
- **Disposal is clean.** `StopWarningTimer` (`SessionService.cs:158`) stops, **unsubscribes**
  `Elapsed`, disposes and nulls the timer under `_warningGate`; it is called from `StopActiveTimer`
  (so `EndSession` covers it), from `StartWarningTimer` itself, and from `Dispose`. No dangling
  handler. (Pre-existing and out of scope: `Dispose` still does not unsubscribe `_activeTimer.Elapsed`
  — it duplicates rather than calls `StopActiveTimer`. Untouched by this change.)
- **Deviation from plan — accepted and already documented.** The check-and-claim was collapsed into a
  single `RunOnStorageAsync` item returning whether *this* call claimed the warning. The plan records
  it under Recommended approach → §4 as a `/coder` implementation note, so the plan is not stale.
  **Verified correct** — see the ruling below.
- **Deviations in the test shapes** (no runtime observation of the bell entry on the warning path;
  "neither surface is called" asserted over IL) are likewise already recorded in the plan's Test
  strategy note. Ruled on below (S4, and the acceptance note under Tests).
- **Accepted limitation (per plan):** the bell-only warning can go unseen until the user opens the
  app, and a guest who leaves the app in the tray for the last three hours meets the warning and the
  wipe together. Residual risk unchanged by the implementation; recorded here so it stays a decision
  rather than becoming a defect report.

**Ruling — is the single-item claim genuinely atomic, and does write-before-announce still hold? Yes
to both.** `RunOnStorageAsync<T>` (`LocalStorageService.cs:117-128`) chains each item onto
`_executorTail` under `_executorGate`, so items are strict FIFO and *never* run concurrently with each
other; the returned task completes only after the delegate has run (it is a write barrier, not an
enqueue ack). The accessors take `_lock` **inside** the item, and the executor holds no lock across
items, so there is no re-entrancy and no deadlock. The double invocation the coder describes is real
and I traced it end to end: `RestoreSession` inside the window → `RaiseExpiryWarning` sets the latch
**and** posts the event → `HomeViewModel`'s constructor (`:877-879`) subscribes, then reads
`ExpiryWarningDue` and handles it synchronously (call 1) → the posted event later arrives (call 2).
Each enqueues one item; the second reads the flag the first wrote. Write-before-announce holds because
`SetSessionExpiryWarningSent` runs inside the item and the `await` on that item precedes the
`Dispatcher.UIThread.InvokeAsync` hop. Teardown is also right: after `IsShutDown` the item is dropped
and the task yields `default(bool)` = `false`, i.e. "not claimed", so nothing is announced.
*Boundary worth recording:* the claim is atomic only against other **executor** items. A future caller
invoking `GetSessionExpiryWarningSent` / `SetSessionExpiryWarningSent` from a bare `Task.Run` (the
`SetDeviceAsync` pattern) would sit outside the FIFO and could interleave. Today there is no such
caller; a one-line "call only from the storage executor" note on the two methods would keep it that
way.

#### Code

- [x] **S1. `SetSessionExpiryWarningSent` silently no-ops when the `app_session` row is missing** —
  `src/ProoveGaming.App/Services/LocalStorageService.cs:2854` — Should-fix
  **Problem:** the setter is `UPDATE app_session SET expiry_warning_sent = 1 WHERE id = 1`. With no
  row, `ExecuteNonQuery` affects zero rows and returns normally, so the claim reports success while
  nothing was written. The once-per-session guarantee would not stick, and the double-invoke handler
  would announce twice.
  **Why it does not block:** I could not reach it in production. A fresh guest session writes the row
  synchronously right after `StartSession` (`MainWindow.axaml.cs:393-394`, both on the UI thread) and
  the warning cannot be due for minutes afterwards; a restored session only exists because
  `GetSessionState` (`:2897`) found the row. The in-session deletes (`ClearSession` / `ClearData`) are
  accompanied by `EndSession`, after which `!_session.IsGuest` short-circuits the handler. It is
  latent, and the code comment at `:2837-2840` reasons about it correctly. So: **not required for this
  branch to ship** — but the guard should be made true rather than left resting on an argument.
  **Solution options:**
  - Option A (correct, and the one to take): have the setter return the affected row count and have
    the executor item claim only on `> 0` — `if (Get…()) return false; return _localStorage.SetSessionExpiryWarningSent() > 0;`.
    No row ⇒ no claim ⇒ no announcement, which is the plan's stated failure direction (miss, never
    duplicate).
  - Option B (**do not take**): the "obvious" `INSERT … ON CONFLICT(id) DO UPDATE` upsert is actively
    harmful here. It would fabricate an `app_session` row with an empty `session_uuid` and a bogus
    `started_at`; `InitializeSession` (`:2819-2835`) early-returns when a row already exists, so that
    phantom row would then permanently prevent the next real guest session from persisting its
    identity, and `GetSessionState` would report it `Active` with a null uuid on every launch. Naming
    it explicitly so a later round does not "fix" S1 into a worse bug.

- [x] **S2. `RaiseExpiryWarning` sets the latch outside `_warningGate`, so a losing `Elapsed` callback
  can still re-arm it after `EndSession`** — `src/ProoveGaming.App/Services/SessionService.cs:173-189`
  — Should-fix
  **Problem:** `OnWarningTimerElapsed` takes the gate, compares `sender` against `_warningTimer`, then
  **releases the gate** before calling `RaiseExpiryWarning`, which writes `_expiryWarningDue = true`
  ungated. Interleaving: pool thread passes the identity check → UI thread runs `EndSession` →
  `StopWarningTimer` sets `_expiryWarningDue = false` → pool thread resumes and sets it back to
  `true`. The latch is then true for a session that no longer exists. (`AcknowledgeExpiryWarning`,
  `:41`, also writes it ungated, and `_expiryWarningDue` is a plain non-volatile field read from the
  UI thread.)
  **Why it matters but does not block:** today the consequences are bounded — the dispatched handler
  re-checks `!_session.IsGuest`, and the only reader of the latch is the singleton `HomeViewModel`
  constructor, which has already run by then. So no user-visible defect exists at this revision. But
  the gate was added precisely to make the racing callback lose, and it currently only half-wins; the
  next consumer that reads `ExpiryWarningDue` (a rebuilt view model, a second surface) inherits a
  stale `true`.
  **Fix:** set `_expiryWarningDue = true` inside the same `lock (_warningGate)` that performs the
  `ReferenceEquals` check, and keep `_dispatch(...)` outside the lock (dispatch must not run under the
  gate — the tests inject a synchronous dispatch, which would execute the whole handler inside it).
  Make `AcknowledgeExpiryWarning` take the gate too, for one owner of the field.

- [x] **S3. The misconfiguration diagnostic is compiled out of the shipped build** —
  `src/ProoveGaming.App/Services/SessionService.cs:136-138` — Should-fix
  **Problem:** the plan's Risks section asks for a startup log when `SessionExpiryWarningMinutes >=
  SessionTimeHours * 60`, because "the feature vanishes with no error". The implementation uses
  `Debug.WriteLine`, which is `[Conditional("DEBUG")]` — it does not exist in the Release build that
  ships. In exactly the build where a support case would arise, the mitigation is absent.
  **Fix:** Option A — an optional `ILogger<SessionService>?` constructor parameter defaulted to null
  (the tests' single-delegate `new SessionService(a => a())` keeps compiling) and a
  `LogWarning`/`LogDebug` call. Option B — expose `bool ExpiryWarningDisabled` and log it once from
  `App.OnFrameworkInitializationCompleted`, keeping `SessionService` dependency-free as the plan
  intends. B is the smaller change and fits the plan's "SessionService deliberately takes no
  dependencies" stance.

#### Tests

**Ruling on the structural (IL) assertion — acceptable, no production seam required.** Requiring a
`virtual`/interface seam on `SystemNotificationService.Show` purely so a test can assert a *negative*
is out of proportion: the seam would exist for nothing else, and the test-writer verified the
alternative (spying the logger) is not merely awkward but wrong — the method logs nothing when called,
so the spy would pass either way. The IL scan with `OnUpdateAvailable` as an in-test positive control
is a legitimate regression guard against the one realistic failure (someone copies the three-surface
pattern), and it is honest about what it is. **The technique is sound; this instance of it is not yet
wired to the right scope** — as written its positive control cannot fail and its negatives cannot see
the closure the paste would land in. Fix S4 and the ruling stands; leave S4 and the acceptance
argument for "structural is good enough here" no longer holds, because the structure it inspects is
not the warning path's.

**Ruling on the reflection helper for `_warningTimer` — acceptable.** "No timer is left running" has
no behavioural surface at all (a leaked default-config timer fires 45 hours later), so a private-field
read is the proportionate tool for a criterion that would otherwise go uncovered. Nit N2 only.

**Ruling on the real-timer tests — acceptable, low CI risk.** `DisableTestParallelization` is set
assembly-wide (`tests/ProoveGaming.App.Tests/AssemblyInfo.cs:10`), so these run sequentially with no
sibling class competing for CPU or for the process-wide `AppSettings` statics — which also removes
what would otherwise have been a real cross-class flake, since both new test classes mutate
`SessionTimeHours`. The residual window is a stall between computing `startedAt` and
`StartWarningTimer`'s own `DateTime.UtcNow` read. `WarningTimer_WhenItElapses_FiresWithoutGoToTray`
(150 ms) degrades gracefully — an over-run makes the warning raise synchronously and the event still
fires. `EndSession_BeforeTheWarningIsDue_PreventsIt` (800 ms) does not: an over-run turns both
`Assert.True(WarningTimerIsRunning(...))` and `Assert.False(warned)` red with a misleading message.
800 ms on a sequential Release run is a wide margin; keep it, and see N3 for the cheap legibility fix.

- [x] **S4. The bell-only guard scans the wrong scope in both directions: its positive control cannot
  fail, and its negatives cannot see the closure where the paste would land** —
  `tests/ProoveGaming.App.Tests/HomeViewModelSessionWarningTests.cs:264-273` — Should-fix
  **Problem — two halves of one mistake.** The IL search has two scopes: `Calls` (one method body) and
  `CallsFromAnyClosureOf` (that body **plus every nested type of `HomeViewModel`**). The test picks the
  wrong one at each site.
  1. *The positive assertion is vacuous.* `AddNotification` sits inside the
     `Dispatcher.UIThread.InvokeAsync(() => …)` lambda, so the test widens to
     `CallsFromAnyClosureOf` — but four other closures in the same file call the identical 3-argument
     `AddNotification` overload (`HomeViewModel.cs:1177`, `:1315`, `:1665`, `:1670`, the device
     connect/disconnect announcements). The search finds one of those and returns true no matter what
     the warning path does. Delete the warning's own `AddNotification` call and the assertion
     "The guest expiry warning must still add its bell entry" still passes.
  2. *The negatives are too narrow.* `Calls(warningPath, …)` searches `MoveNext` only. An author who
     "completes the pattern" by putting `_services.GetService<NotificationService>()?.Show(body)`
     beside the `AddNotification` call — inside that same lambda, the natural place — lands in the one
     region the guard does not search, and the test stays green. (The other placement, directly in
     the async body, *is* caught — so this is a hole, not a broken guard.)
  **Fix:** from `MoveNext`, collect the closure methods actually referenced by `ldftn` / `ldvirtftn`
  and scan **only the warning path's own state machine plus those**, then use that one scope for the
  positive and both negatives alike.
  **Do not** simply widen the negatives to `CallsFromAnyClosureOf` — I checked, and it false-positives
  immediately: `HomeViewModel.cs:368` calls `NotificationService.Show` from inside an async method
  (the Contact Us flow), whose `MoveNext` is itself a nested type of `HomeViewModel`. That assertion
  would fail on code that is perfectly correct.

#### Nits

- N1. `AnnounceSessionExpiringAsync` is fire-and-forget and its `try`/`catch` covers only the storage
  claim, so a fault from the `Dispatcher.UIThread.InvokeAsync` hop lands on a task nobody observes.
  `MainWindow.axaml.cs:384-388` already has the house pattern (`ContinueWith(…, OnlyOnFaulted, …)`
  logging) — `HomeViewModel.cs:925-960`.
- N2. `WarningTimerIsRunning` uses `GetField(…)!`, so renaming `_warningTimer` fails with a bare
  `NullReferenceException` instead of a message naming the seam. A null-check with
  `Assert.Fail("SessionService._warningTimer was renamed — update this helper")` keeps the next
  failure readable — `SessionServiceTests.cs:288-292`.
- N3. In `EndSession_BeforeTheWarningIsDue_PreventsIt` the 800 ms due-instant is asserted implicitly;
  a one-line message on `Assert.True(WarningTimerIsRunning(service))` ("the warning became due before
  EndSession ran — widen the window") would turn the rare stall from a puzzling red into a legible one
  — `SessionServiceTests.cs:424`.
- N4. `AcknowledgeExpiryWarning` (`SessionService.cs:41`) writes `_expiryWarningDue` ungated while
  `StopWarningTimer` writes it under `_warningGate`. Cosmetic on its own; fold into S2 so one lock
  owns the field.
- N5. The `Math.Max(1, AppSettings.SessionExpiryWarningMinutes / 60)` body argument is untestable here
  (localized strings do not resolve without an Avalonia application) and is therefore unverified. An
  `internal static int WarningLeadHours(int minutes)` helper would make it a one-line test. Recorded
  by `/test-writer` in Test strategy; agreed, and low priority — the default (180 → 3) is right and QA
  step 7 covers the rendered string.

#### What's done well

- **The double-invoke race was found, understood and closed at the right layer.** Collapsing check and
  claim into one executor item — rather than adding a `bool _announced` field on the view model — uses
  the FIFO guarantee the storage executor already documents, and it keeps the once-per-session promise
  true across restarts as well as within a run. The comment at `HomeViewModel.cs:948-956` explains
  *why* two hops fail, which is the part a future reader would otherwise undo.
- **The bell-only decision is defended in the code itself**, with a comment naming the neighbouring
  three-surface method as the thing not to copy, plus a test that fails if someone does. That is the
  correct response to a product decision that looks like an omission.
- **`OnWarningTimerElapsed` compares `sender` against the live timer** instead of relying on `Stop()`,
  which is the only reliable way to make an already-queued `System.Timers.Timer` callback lose the
  race. S2 is a narrowing of this, not a rejection of it.
- **The skip rule is stated in terms of the existing tests it must not disturb**, and the `(0, 180)`
  theory row makes that contract explicit rather than incidental.
- **The test-writer documented what it could not observe** and what it substituted, instead of writing
  an assertion that merely appears to cover the gap. The pending-task technique for "exactly one
  announcement" is a genuinely exact substitute, not an approximation.

#### Verdict

Ship: yes. No blockers: the once-per-session claim is genuinely atomic, write-before-announce holds,
the warning is bell-only and not tray-gated, the skip rule keeps the existing expiry tests honest,
both languages carry both keys, and both timers are unsubscribed and disposed on `EndSession` and
`Dispose`. The four Should-fixes are all latent or test-side — none produces a user-visible defect at
this revision.

Verified: `dotnet test ./tests/ProoveGaming.App.Tests/ProoveGaming.App.Tests.csproj -c Release` —
**1409 passed, 0 failed** (1 m 41 s), matching the pre-review baseline.

First: **S4** (scope the IL search to the warning path's own state machine and its `ldftn` closures —
it is the only finding where a stated guarantee is currently unguarded), then **S1** (return
rows-affected; do **not** upsert), then **S2** (one lock owns `_expiryWarningDue`).
