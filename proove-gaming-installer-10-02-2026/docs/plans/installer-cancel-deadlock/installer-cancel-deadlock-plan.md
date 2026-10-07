# installer-cancel-deadlock — fix the installer's UI-thread deadlock on the cancel/pause gate

Fixes finding **C1 (HIGH / ui-thread-block)** of the 2026-08-21 performance/memory audit
(`docs/audits/2026-08-21-performance-memory-audit.md`, section C1, panel vote 3/3 real).

## Goal

During a real (Release) install, `RunInstallAsync` runs its continuations on the Avalonia UI
thread (it is awaited from the `async void` next-click handler, `src/ProoveGaming.Installer/Views/MainWindow.axaml.cs:106`,
and sets controls directly). At `src/ProoveGaming.Installer/Views/MainWindow.Installation.cs`
lines 52, 92 and 107 it calls `WaitIfInstallPausedOrCancelled`, which does a **blocking**
`_installPauseGate?.Wait(cancellationToken)` (line 181) on a `ManualResetEventSlim`. The gate is
`Reset()` by `OpenCancelModal` (`MainWindow.Modal.cs:23`) and `Set()` only by `CloseCancelModal`
(`MainWindow.Modal.cs:46`); the token is cancelled only by `ConfirmCancelModal`
(`MainWindow.Modal.cs:60`) — all three are UI-thread click handlers. If the user opens the Cancel
modal after the background copy worker has passed the *last* file's per-file gate check
(`Installation.cs:751`), the copy `Task` completes freely, the `await` at line 90 resumes on the
UI thread, and line 92 blocks the UI thread on a gate that only the now-frozen UI thread can
open. Permanent deadlock: the modal's buttons never respond, the `finally` block never runs (the
multi-hundred-MB staging dir leaks in `%TEMP%`), rollback never runs (half-written install dir),
and the DeviceHost service — stopped before the copy — stays stopped. The goal is to make the
pause/cancel wait UI-thread-safe so this deadlock is impossible by construction, while preserving
the existing pause-while-modal-open semantics for the background copy worker.

## Scope

**In scope**

- Replace the blocking `ManualResetEventSlim` pause gate with an async-awaitable gate.
- Convert the three UI-thread wait sites and the copy worker's per-file wait to `await`.
- Unit tests for the gate logic (new `tests/ProoveGaming.Installer.Tests` project).
- `docs/CONTEXT.md` glossary entry for the new gate term.

**Out of scope**

- Any other audit finding (C2+).
- The `#if DEBUG` mock install path (`RunMockInstallAsync`) — it never touches the gate today and
  keeps not touching it.
- Rollback behaviour, staging cleanup, service stop/start logic — unchanged; they merely become
  reachable again in the trigger scenario.
- Uninstaller (it has no pause gate).

## Options considered

1. **Async pause gate type in the Installer + a new `tests/ProoveGaming.Installer.Tests` project**
   *(recommended)*. Extract the gate into an `internal sealed class InstallPauseGate`
   (`TaskCompletionSource`-based) inside `ProoveGaming.Installer`; all wait sites `await` it.
   Test it from a new xUnit project via `InternalsVisibleTo`, exactly as CLAUDE.md prescribes for
   installer internals. Trade-off: a new project must be added to `src/ProveGaming.slnx`
   (explicitly flagged below), and the regular CI workflow does not run it (pre-existing CI
   limitation for all of `tests/`).
2. **Put the gate in `ProoveGaming.Common` and test it from the existing
   `tests/ProoveGaming.App.Tests`** (Common already has `InternalsVisibleTo ProoveGaming.App.Tests`).
   Avoids any new project or `.slnx` edit. Rejected: `Common` is chartered as *shared* Avalonia UI
   plumbing consumed by App **and** Installer; an installer-only threading primitive placed there
   solely for test reachability abuses the layering, and App tests asserting installer behaviour
   is misleading to the next reader.
3. **Keep `ManualResetEventSlim` but delete the UI-thread wait sites** (lines 52, 92, 107), keeping
   only the worker-side per-file gating plus token checks. Smallest diff and removes the deadlock,
   but silently changes semantics: with the modal open, the install would march on past the copy
   step into service configuration — the "everything pauses while the user decides" contract is
   lost — and it leaves a blocking primitive in place for the next person to re-introduce the bug.
4. **Wrap the existing blocking wait in `Task.Run` at the UI-thread sites.** Rejected outright:
   sync-over-async offload burns a pool thread per wait and violates the repo's async conventions;
   the gate itself stays untestable.

## Recommended approach

Option 1. Introduce `InstallPauseGate`, an `internal sealed` class in the Installer project:

```csharp
internal sealed class InstallPauseGate
{
    void Pause();                                        // idempotent; called by OpenCancelModal
    void Resume();                                       // idempotent; called by CloseCancelModal
    Task WaitWhilePausedAsync(CancellationToken token);  // completes immediately when open;
                                                         // throws OperationCanceledException on cancel
}
```

Implementation sketch: a private lock guarding a nullable
`TaskCompletionSource` created with `TaskCreationOptions.RunContinuationsAsynchronously`
(`null` = gate open). `Pause()` installs a fresh TCS if none; `Resume()` completes and clears it;
`WaitWhilePausedAsync` fast-paths to `Task.CompletedTask` when open, otherwise returns
`pauseTask.WaitAsync(cancellationToken)` so a cancel releases waiters even if `Resume()` is never
called. No `IDisposable` — unlike `ManualResetEventSlim` there is no kernel handle, so the two
`_installPauseGate?.Dispose()` calls in `RunInstallAsync` (lines 33, 172) are removed and the
field is simply replaced per run / nulled in `finally`.

Wiring:

- `MainWindow.Installation.cs`: field becomes `private InstallPauseGate? _installPauseGate;`.
  `WaitIfInstallPausedOrCancelled` becomes
  `private async Task WaitIfInstallPausedOrCancelledAsync(CancellationToken)` — leading
  `ThrowIfCancellationRequested`, `await gate.WaitWhilePausedAsync(token)` (null-tolerant, as
  today), trailing `ThrowIfCancellationRequested` (covers "Confirm fired Cancel *and* Resume":
  the wait may complete successfully, the trailing check still throws). The three call sites
  (lines 52, 92, 107) become `await WaitIfInstallPausedOrCancelledAsync(cancellationToken);`, and
  the per-file call inside `CopyDirectoryAsync`'s already-`async` `Task.Run` lambda (line 751)
  becomes an `await` of the same helper.
- `MainWindow.Modal.cs`: `OpenCancelModal` calls `_installPauseGate?.Pause()` instead of
  `.Reset()`; `CloseCancelModal` calls `_installPauseGate?.Resume()` instead of `.Set()`.
  `ConfirmCancelModal` is unchanged.
- **No `ConfigureAwait(false)` at the `RunInstallAsync` await sites** — the code after each wait
  touches controls and must resume on the Avalonia UI thread; the captured context does that.
  Inside the `Task.Run` copy worker there is no context, so awaits resume on the pool as today.

Why this kills the bug: when the UI-thread continuation reaches the wait while the modal is open,
it now *awaits* — control returns to the dispatcher, the modal's buttons stay live. "No, continue"
→ `Resume()` releases the wait and the install proceeds; "Yes, cancel" → the token cancels, the
awaiting site throws `OperationCanceledException`, and the existing catch block performs rollback
and closes. The background copy worker keeps its exact semantics: it parks before each file while
the modal is open. `RunContinuationsAsynchronously` guarantees `Resume()`/`Pause()` on the UI
thread never inline-run a released continuation inside the click handler.

Flags (per repo constraints):

- **Installer flow touched** — release-process implications: this code ships inside the single-EXE
  Release installer (embedded payload, `installer-release` workflow). The fix must be verified on a
  Release-published installer, not just the DEBUG mock path, and the PR must call this out.
- **New project in `.slnx`** — `tests/ProoveGaming.Installer.Tests` is added to
  `src/ProveGaming.slnx`. No new NuGet dependencies beyond the same xUnit packages App.Tests
  already uses; no `Directory.Build.props` change; no TFM bump.
- CI note *(corrected 2026-08-23, PR #119 review m1 — the original claim that CI builds/tests
  only the App csproj was wrong)*: `.github/workflows/dotnet-ci.yml` builds the App csproj and
  then runs `dotnet test ./src/ProveGaming.slnx --configuration Release` — the **full** test
  suite, including this new project, on every push/PR, in **Release**. Two consequences:
  (a) timing-sensitive tests must be verified with a Release slnx test run before pushing, and
  (b) adding `tests/ProoveGaming.Installer.Tests` to the slnx makes every CI run build the
  Installer in Release, which triggers its heavy embedded-payload publish — this is what broke
  CI with NETSDK1047 until commit `372e4a7` pinned `<RuntimeIdentifiers>win-x64</RuntimeIdentifiers>`
  into App and DeviceHost, and it measurably lengthens every CI run.

## File-level change list

Ordered so tests land first (bug-fix rule: repro test before the fix).

1. **`src/ProoveGaming.Installer/InstallPauseGate.cs`** *(new)* — the `internal sealed`
   TCS-based async pause gate described above (project root, beside `InstallerConstants.cs`;
   not under `Views/` — it is not a view).
2. **`src/ProoveGaming.Installer/ProoveGaming.Installer.csproj`** *(edit)* — add
   `<InternalsVisibleTo Include="ProoveGaming.Installer.Tests" />` (same MSBuild-item style as
   `ProoveGaming.Common.csproj:27`).
3. **`tests/ProoveGaming.Installer.Tests/ProoveGaming.Installer.Tests.csproj`** *(new)* — xUnit v2
   test project mirroring `tests/ProoveGaming.App.Tests/ProoveGaming.App.Tests.csproj` (same
   package versions), `ProjectReference` to `ProoveGaming.Installer.csproj`, `net10.0` target.
4. **`tests/ProoveGaming.Installer.Tests/InstallPauseGateTests.cs`** *(new)* — gate unit tests,
   including the named deadlock-repro test (see Test strategy).
5. **`src/ProveGaming.slnx`** *(edit)* — add the new test project entry.
6. **`src/ProoveGaming.Installer/Views/MainWindow.Installation.cs`** *(edit)* — field type change,
   remove the two gate `Dispose()` calls, `WaitIfInstallPausedOrCancelled` →
   `WaitIfInstallPausedOrCancelledAsync`, `await` at the three UI-thread sites and in the
   `CopyDirectoryAsync` worker.
7. **`src/ProoveGaming.Installer/Views/MainWindow.Modal.cs`** *(edit)* — `Reset()`/`Set()` →
   `Pause()`/`Resume()`.
8. **`docs/CONTEXT.md`** *(edit)* — add a one-line glossary entry for **Install pause gate**.

No existing plan under `docs/plans/` owns the installer cancel/pause flow (checked: only
incidental mentions of `MainWindow.Installation.cs` in `fix-qa-bug-batch`, about uninstall
registry values), so this plan becomes the owning document.

## Acceptance criteria

- [ ] Given a Release-published installer mid-copy, when the user opens the Cancel modal at any
      moment — including after the last payload file's copy has begun — and leaves it open
      indefinitely, then the window stays responsive and both modal buttons react to hover/click
      (no permanent freeze; the C1 trigger scenario no longer reproduces).
- [ ] Given the Cancel modal is open during the copy step, when the background worker reaches its
      next file, then it parks before copying it (pause-while-modal-open semantics preserved), and
      closing the modal with "continue" resumes the copy to a successful install (Complete step).
- [ ] Given the Cancel modal is open and the copy step has already completed (the deadlock window),
      when the user confirms cancellation, then the existing cancel path runs: rollback executes,
      the staging directory in `%TEMP%` is deleted, and the installer window closes — i.e. the
      `finally` block is reached.
- [ ] Given the fixed source, `Grep` for `ManualResetEventSlim` and for `.Wait(` under
      `src/ProoveGaming.Installer/` returns no pause-gate hits — no blocking wait remains on any
      path that can run on the UI thread.
- [ ] Given the repo, `dotnet test ./tests/ProoveGaming.Installer.Tests/ProoveGaming.Installer.Tests.csproj`
      passes, and `dotnet test ./src/ProveGaming.slnx` (which now includes the new project) passes.
- [ ] Given the DEBUG build, the mock install path behaves exactly as before (it never consulted
      the gate).

## Test strategy

Level: unit tests on the extracted gate, in the **new `tests/ProoveGaming.Installer.Tests`**
project (xUnit v2, mirroring the style of `tests/ProoveGaming.App.Tests`). The gate is extracted
precisely so the logic is testable without Avalonia.

Bug-fix ordering: the literal deadlock lives in an internal Avalonia partial class and cannot be
unit-reproduced against the *current* code (the blocking wait is inline in `MainWindow` and needs
a real dispatcher). The honest equivalent is: land `InstallPauseGate` **with its tests first**
(steps 1–5 of the change list), where the named repro test encodes the deadlock contract —
*a single thread must be able to pause, wait, and resume the gate by itself*, which is exactly
what the `ManualResetEventSlim.Wait` design makes impossible — then wire `MainWindow` to it.

`InstallPauseGateTests` cases:

- `WaitWhilePausedAsync_GateOpen_CompletesSynchronously` — golden path, zero overhead when no
  modal is open.
- **`WaitWhilePausedAsync_PausedThenResumedFromSameThread_Completes`** — *the deadlock repro*:
  `Pause()`, call `WaitWhilePausedAsync` (assert the method returns an incomplete `Task` rather
  than blocking the caller), then `Resume()` from the same thread and assert the task completes.
  Under the old blocking-wait semantics this single-threaded sequence can never finish.
- `WaitWhilePausedAsync_CancelledWhilePaused_ThrowsOperationCanceled` — the "confirm cancel"
  escape hatch works even if `Resume()` is never called.
- `WaitWhilePausedAsync_TokenAlreadyCancelled_ThrowsImmediately`.
- `Pause_IsIdempotent` / `Resume_WithoutPause_IsNoOp` — the modal handlers' guard behaviour.
- `Resume_ReleasesAllPendingWaiters` — UI-thread waiter and copy-worker waiter both parked, one
  `Resume()` releases both.
- `PauseAfterResume_BlocksNextWait` — modal reopened: a fresh wait parks again.

Deliberately **not** covered by automated tests: the `MainWindow` wiring itself (UI-thread
affinity, Avalonia controls, real file copy, sc.exe service calls) — that requires a running
Avalonia dispatcher and elevation, and is covered by the QA checklist below. There are no
pre-existing installer tests to keep green.

## QA (manual test steps)

Prereq: a Release-built installer EXE (`ProoveGaming.Installer.exe`) on a Windows 11 machine.
The deadlock window is the copy of the last payload file, so steps 3–4 use timing near the end of
the copy phase; repeat them a few times since the exact moment is a race.

1. Run the installer, accept the agreement, pick an install folder, and click Install. Expected:
   status shows "Copying application files" with a moving progress bar.
2. Early in the copy phase, click Cancel to open the confirmation dialog and leave it open for
   ~10 seconds. Expected: the window stays responsive (dialog buttons highlight on hover), and
   the progress bar pauses while the dialog is open.
3. Choose "continue installation" (close the dialog). Expected: copying resumes and the install
   finishes on the completion screen. Launch the installed app once to confirm it works.
4. Run the installer again over the same folder. When the copy progress is close to its end
   (roughly the last moments before the status changes away from "Copying application files"),
   click Cancel and **leave the dialog open for at least 30 seconds**. Expected: the window and
   both dialog buttons remain fully responsive the whole time — no freeze. (Before this fix, this
   exact timing froze the installer permanently.) Repeat this step 3–5 times to hit different
   timings.
5. From the open dialog in step 4, confirm the cancellation. Expected: status changes to the
   cancelling message, and the installer window closes by itself after rollback finishes.
6. After step 5: open `%TEMP%` and confirm there is no leftover `ProoveGamingInstaller` folder
   containing hundreds of MB; open Windows Services and confirm the "Proove Gaming Device Host"
   service is present and running if it was installed before the cancelled re-install.
7. Run the installer one final time to completion. Expected: normal successful install; the app
   and its tray icon work.

## Review focus

- `installer/uninstaller`
- `UI-thread affinity`
- `async/await`
- `thread-safety`

## Performance & quality

- **No hot path.** The gate is touched once per UI step and once per copied file (a few thousand
  times per install, each amortised against a file copy); the open-gate fast path is a lock-free
  read returning `Task.CompletedTask` — no allocation when no modal is open.
- **UI thread never blocks**: every wait on the gate is an `await`; the only synchronisation is a
  short private lock around TCS swap, held for pointer assignments only.
- **Async correctness**: `TaskCreationOptions.RunContinuationsAsynchronously` prevents
  `Resume()`/cancel from inline-running install continuations inside a button click handler;
  no `ConfigureAwait(false)` on UI-resuming awaits (matches the file's existing convention);
  `Task.WaitAsync(CancellationToken)` gives cancellation without leaking registrations.
- **Shared state**: the gate instance is created per install run on the UI thread and read by the
  copy worker; the internal lock makes `Pause`/`Resume`/`WaitWhilePausedAsync` safe from both.
- **Resource lifetime**: the new gate holds no unmanaged resources, removing a `Dispose`
  obligation rather than adding one; the `CancellationTokenSource` lifecycle is unchanged.

## Risks

- **Behavioural resume point**: with the async wait, "close modal → continue" now resumes the
  UI-thread continuation via a dispatcher post instead of unblocking inline. Watch that the
  progress/status updates after each wait still run in order (they do — same thread, queued).
- **Release-only reproduction**: the deadlock path is compiled out in DEBUG (`RunMockInstallAsync`
  returns before the first wait), so only a Release publish exercises the real wiring — hence the
  explicit QA steps. Budget time for a full `dotnet publish -c Release` of the installer.
- **Cancel-vs-resume race**: `ConfirmCancelModal` cancels the token *then* calls
  `CloseCancelModal` → `Resume()`. A waiter may observe the resume before the cancellation; the
  trailing `ThrowIfCancellationRequested` in the helper makes the outcome deterministic
  (cancellation wins). A reviewer should verify that check survives the edit.
- **New test project friction** *(corrected 2026-08-23, PR #119 review m1 — "tests default to
  Debug" was wrong for CI)*: `dotnet test ./src/ProveGaming.slnx` build time grows, and CI runs
  that command in **Release** on every push/PR, so every CI run now reaches the Installer
  project and triggers its embedded-payload publish. That publish initially failed CI with
  NETSDK1047 until `372e4a7` pinned win-x64 into the App/DeviceHost restores; the remaining
  cost is a longer CI run per PR.
- The modal is also usable outside an install (`_installInProgress == false`), where the gate
  field is null — the existing null-conditional guards keep that path a no-op; do not tighten
  them away.

## Open questions

- None. The change is self-contained; no user decision required before `/coder` runs.

## Review findings

### Round 1 — Ship: yes

#### Summary
The change set replaces the installer's blocking `ManualResetEventSlim` pause gate with the async
`InstallPauseGate` (TCS-based, `RunContinuationsAsynchronously`), converts the three UI-thread wait
sites and the copy worker's per-file wait to `await WaitIfInstallPausedOrCancelledAsync`, rewires the
modal handlers to `Pause()/Resume()`, and adds `tests/ProoveGaming.Installer.Tests` (9 asserts across
8 tests) wired into `src/ProveGaming.slnx` via `InternalsVisibleTo`. The branch also carries the
committed 2026-08-21 audit docs and several unrelated untracked plan folders (out of this plan's scope).

#### Plan conformance
Plan: docs/plans/installer-cancel-deadlock/installer-cancel-deadlock-plan.md
- Implemented as planned: all 8 File-level change list items, in the planned order and shape
  (gate beside `InstallerConstants.cs`, not under Views/; two `Dispose()` calls removed; field nulled
  in `finally`; no `ConfigureAwait(false)` on UI-resuming awaits; `ConfirmCancelModal` untouched).
- Deviates from plan: none material. The test file adds a synchronous-cancellation assert to
  `WaitWhilePausedAsync_TokenAlreadyCancelled_ThrowsImmediately` beyond the plan's sketch (stronger,
  in-spirit).
- Missing / partial: acceptance criteria 1-3 (Release-installer behaviour) are QA-only by design;
  not verifiable in this environment. Criteria 4-6 verified: grep clean (only a comment mentions
  `ManualResetEventSlim`), Installer.Tests 8/8 green, full slnx suite 767+8+934 green, DEBUG mock
  path untouched.
- Accepted limitation (per plan): CI (`dotnet-ci.yml`) never runs `tests/` projects, so the new tests
  run locally/slnx only — residual risk: a future regression in the gate passes CI silently.

#### Code
- none (no Blockers, no Should-fix)

Verified specifically against the Review focus:
- UI-thread affinity: every former blocking site now awaits; the `async void` click handler awaits
  `RunInstallAsync` on the captured Avalonia context (`MainWindow.axaml.cs:106`); parked continuations
  return control to the dispatcher, so the modal buttons stay live in the C1 trigger window.
- Cancel-vs-resume race: `_closeAfterInstallCancellation = true` is set before `Cancel()` in
  `ConfirmCancelModal` (`MainWindow.Modal.cs:59-61`), and the trailing
  `ThrowIfCancellationRequested` survives in the helper (`MainWindow.Installation.cs:186`) — the
  plan's named risk is closed; cancellation deterministically wins even if a waiter observes the
  resume first.
- Thread-safety: `_installPauseGate` is written on the UI thread before the copy worker starts
  (`Task.Run` publication) and nulled only in `finally`, which runs strictly after the copy task is
  awaited — no concurrent field write; the gate's own state is lock-guarded and
  `RunContinuationsAsynchronously` prevents inline continuation execution inside click handlers.
- Resource lifetime: the gate holds no unmanaged resources; the CTS dispose/lifecycle is unchanged;
  `Task.WaitAsync` disposes its cancellation registration on completion.
- The `CS0162` unreachable-code warning at `MainWindow.Installation.cs:51` is the pre-existing
  `#if DEBUG` early-return pattern (same warning exists on main and in the Uninstaller), merely moved.

#### Tests
- none failing; all 7 plan cases covered 1:1 plus the strengthened immediacy assert. The MainWindow
  wiring itself (dispatcher affinity, real copy, sc.exe) is deliberately uncovered per the plan and
  falls to the QA checklist.

#### Nits
- The new glossary entry uses a typographic apostrophe (U+2019, "installer’s") where neighbouring
  entries use ASCII apostrophes — `docs/CONTEXT.md` (Install pause gate entry).
- The working tree mixes this change with an unrelated `docs/CONTEXT.md` "Storage executor" entry
  (from `docs/plans/ui-thread-storage-async/`) and four unrelated untracked plan folders. Not a
  defect of this change, but the eventual commit should separate the changesets.

#### What's done well
- The gate is exactly as narrow as the plan asked: no IDisposable obligation, lock held for pointer
  swaps only, open-gate fast path allocation-free, and the deadlock contract is encoded as a named
  single-threaded repro test rather than a timing-dependent one.
- The helper keeps the null-tolerant guard so the modal outside an install stays a no-op, and the
  comment explains why both cancellation checks exist — the next editor is warned off deleting the
  trailing check.

#### Verdict
Ship: yes — no blockers or should-fixes; the fix is correct by construction and fully conformant
with the plan. Advisory for the human reviewer: (1) this touches the elevated single-EXE installer,
so run the built-in /security-review skill on the branch and verify the fix on a Release-published
installer per the QA steps (the deadlock path is compiled out in DEBUG); (2) split the unrelated
working-tree changesets before committing; (3) note the release-process implications in the PR.
