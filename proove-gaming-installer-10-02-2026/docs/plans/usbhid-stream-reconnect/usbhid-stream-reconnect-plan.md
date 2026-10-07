# usbhid-stream-reconnect — reconnect the device-event stream after a DeviceHost restart

Origin: `docs/audits/2026-08-21-performance-memory-audit.md`, section 4 ("Completeness critic"), item 1.

## Goal

When the DeviceHost process dies or is restarted mid-session (e.g. a service update), the App's
gRPC `SubscribeOnDevices` server stream ends — but `UsbHidDeviceService` never notices: the read
task completes silently while the outer keep-alive loop waits on a token nobody cancels, so the
existing reconnect loop in `StartCommunication` never gets a turn. Live device events
(connect/disconnect, DPI stage, battery, report rate) stay permanently dead until the App is
restarted. This plan makes stream termination observable so the already-existing reconnect loop
re-establishes the subscription every time the host comes back, without leaking calls, tasks, or
cancellation sources across restarts.

## Current lifecycle (as read from the code) and the intended state machine

All in `src/ProoveGaming.App/Services/UsbHidDeviceService.cs` (singleton, registered in
`App.axaml.cs::BuildServices()`; driven by `HomeViewModel.Start()/Stop()` which call
`StartCommunication()` / `StopCommunication()` on Home load/unload, including minimise-to-tray).

Current shape:

- `StartCommunication()` (612–650): guard on `IsRunning`; dispose any old channel; create
  `GrpcChannel` + `Device.DeviceClient`; set `_reconnecting = true`; then loop
  `while (_reconnecting && !success) { success = await RestartCommunication(); if (!success) await Task.Delay(3000); }`;
  finally `IsRunning = false`.
- `RestartCommunication()` (652–683): cancel+dispose the previous `_cts`, complete the previous
  `_tcs`; create fresh ones; `await Task.WhenAny(_tcs.Task, AwaitServerStreaming(token))`.
  Returns **true** only when `_tcs.Task` wins — which only `StopCommunication()` (or the *next*
  `RestartCommunication`) can cause. So `true` = "deliberate stop", `false` = "the stream attempt
  ended; retry".
- `StopCommunication()` (685–690): `_reconnecting = false; _cts?.Cancel(); _tcs?.TrySetResult();`.
- `AwaitServerStreaming(token)` (707–759): open the duplex call, write one subscribe request, then
  spawn `ReadServerResponses` inside a `Task.Run` whose exceptions are swallowed into
  `NotifyError` (724–734), and sit in a `while (!token.IsCancellationRequested) await Task.Delay(3000, token)`
  keep-alive loop (736–746). Only cancellation of the token ends the method.

**The bug:** when the host dies, `ReadServerResponses` throws (`RpcException` Unavailable) or
`MoveNext` returns false — `readTask` completes silently, but the keep-alive loop keeps spinning
because nothing cancels the token. `AwaitServerStreaming` therefore never returns,
`Task.WhenAny` in `RestartCommunication` never resolves, and the reconnect loop in
`StartCommunication` (which handles *connect-time* failures fine) never runs again.

Intended state machine after the fix (no new states — the existing ones, made honest):

```
Stopped ──StartCommunication──> Connecting
Connecting ──subscribe attempt fails (host down)──> wait 3 s ──> Connecting      (existing, works today)
Connecting ──stream established──> Streaming
Streaming ──stream ends or faults (host restart)──> Connecting                   (THE FIX: today this dead-ends)
Streaming | Connecting ──StopCommunication──> Stopped (IsRunning=false, no NotifyError noise for the deliberate cancel)
```

The invariant: whenever `_reconnecting` is true and no healthy stream exists, a new subscribe
attempt happens within ~3 s. Per attempt exactly one duplex call (disposed via `using`), one
`CancellationTokenSource` (disposed by the next attempt or `Dispose()`), one
`TaskCompletionSource` — repeated restarts must not accumulate any of them.

## Scope

In scope:

- `AwaitServerStreaming` / `StartCommunication` retry loop in
  `src/ProoveGaming.App/Services/UsbHidDeviceService.cs`.
- Minimal internal test seams on the same class (injected `Device.DeviceClient`, swappable retry
  delay) so the lifecycle is unit-testable.
- New xUnit tests in `tests/ProoveGaming.App.Tests`.

Out of scope (deliberately):

- No change to `deviceservice.proto` or the DeviceHost — the gRPC contract is untouched.
- No recreation of the `GrpcChannel` per attempt: `Grpc.Net.Client` channels re-establish the
  underlying HTTP/2 connection per call, so reusing `_channel`/`_client` across host restarts is
  correct and is what the working connect-time retry path already relies on.
- No backoff policy change (the flat 3 s retry stays), no re-sync of device list on reconnect
  beyond what the re-subscribed stream itself pushes (`DeviceFound` fires for present devices as
  the host reports them — same as first connect).
- The pre-existing `Stop()`→`Start()` race noted in Risks, and the other audit findings
  (notification growth, Serilog limits).

## Options considered

**Option A — keep the structure, watch the read task.** Keep the `Task.Run` and replace the
keep-alive delay loop with `await readTask` (or `Task.WhenAny(readTask, cancellationTask)`), so
the method returns when the reader stops. Smallest diff, but it keeps the pointless `Task.Run`
hop and the inner swallow-to-`NotifyError` wrapper, i.e. two layers of exception handling for one
loop, and the method still ends with a `CompleteAsync` that can itself throw after the channel is
gone.

**Option B — remove the keep-alive loop and the `Task.Run`; await the read loop directly.**
`ReadServerResponses` is already fully async (`await call.ResponseStream.MoveNext(token)`), so
`Task.Run` adds nothing but a thread hop and an exception-hiding wrapper. Await it inline inside
the existing try/catch; on the way out, best-effort `CompleteAsync()` in a `finally` with its own
swallow. The method then returns exactly when the stream ends, faults, or is cancelled — which is
precisely the signal `RestartCommunication`'s `Task.WhenAny` needs. Slightly larger diff than A,
but the result is one loop, one exception path, and no background task to leak.

**Option C — rewrite the supervision (drop the `_tcs`/`WhenAny` machinery, add backoff, recreate
the channel per attempt).** Cleaner in the abstract, but it rewrites a mechanism that mostly works
(connect-time retry is fine today), grows the review surface, and risks new regressions in
`StopCommunication` semantics for no user-visible gain. Rejected as scope creep.

## Recommended approach

**Option B**, plus two small hardening touches and the minimal test seams:

1. **`AwaitServerStreaming`** — delete the keep-alive `while`/`Task.Delay(3000)` loop and the
   `Task.Run` wrapper; `await ReadServerResponses(call, cancellationToken)` directly inside the
   existing `try`. Add a `finally` (inside the outer try, around the read) that best-effort
   `await call.RequestStream.CompleteAsync()` in its own try/catch — after a host death this
   throws and must not mask the real outcome; on a deliberate stop it closes the client half
   politely. The `using var call` continues to own disposal.
2. **Quiet deliberate stops.** In the catch blocks, when `cancellationToken.IsCancellationRequested`
   is true, swallow `OperationCanceledException` and `RpcException` with `StatusCode.Cancelled`
   *without* raising `NotifyError` — a user minimising to tray must not produce an error toast.
   All other exceptions keep the existing `NotifyError` behavior (this is what surfaces
   "Unavailable" while the host is down, matching today's connect-failure path).
3. **Retry loop hygiene** in `StartCommunication`: only delay when a retry will actually happen —
   `if (!success && _reconnecting) await RetryDelayAsync(...)` — so `StopCommunication` during a
   down-host phase doesn't sit out a pointless 3 s; route the delay through the seam below.
4. **Test seams** (repo precedent: the swappable waits in
   `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs`,
   exposed via the existing `InternalsVisibleTo("ProoveGaming.App.Tests")` in
   `src/ProoveGaming.App/ProoveGaming.App.csproj`):
   - an `internal UsbHidDeviceService(Device.DeviceClient client)` constructor storing an
     injected client; `StartCommunication` uses it (skipping channel creation/disposal) when
     present. The generated `Device.DeviceClient` has a `protected` mocking constructor and
     virtual methods, so tests subclass it and override `SubscribeOnDevices()` to return a
     hand-built `AsyncDuplexStreamingCall<,>` (its public constructor exists on `Grpc.Core.Api`
     for exactly this — **no new NuGet package needed**);
   - an `internal Func<CancellationToken, Task>` retry-delay hook defaulting to
     `Task.Delay(3000, ...)` so tests run the reconnect loop instantly.
   The seams must not change release behavior: with the public constructor the code path is
   byte-for-byte today's flow plus the fix.

Async rules hold throughout: no `.Result`/`.Wait()`, the attempt token continues to flow into
`MoveNext`, and nothing here runs on the UI thread (`StartCommunication` is fire-and-forgotten
from `HomeViewModel.Start()`; events keep firing on gRPC continuation threads exactly as today —
`HomeViewModel` already marshals to the dispatcher).

## File-level change list

Ordered so the repro test lands before the fix (bug-fix rule): the seams carry no behavior
change, so steps 1–2 can be committed with the repro test red, then step 3 turns it green.

1. `src/ProoveGaming.App/Services/UsbHidDeviceService.cs` — **(a) seams first:** internal
   client-injection constructor + internal retry-delay hook, no behavior change; **(b) then the
   fix:** remove keep-alive loop and `Task.Run` in `AwaitServerStreaming`, await
   `ReadServerResponses` inline with best-effort `CompleteAsync`, silence
   cancellation-caused exceptions on deliberate stop, guard the retry delay with `_reconnecting`.
2. `tests/ProoveGaming.App.Tests/UsbHidDeviceServiceReconnectTests.cs` — **new**: fake
   `Device.DeviceClient` subclass + scripted `AsyncDuplexStreamingCall` fixtures; the repro test
   (fails before the fix) and the lifecycle tests listed under Test strategy.
3. `docs/CONTEXT.md` — add one-line glossary entries for **SubscribeOnDevices stream** (the
   App↔DeviceHost duplex gRPC subscription carrying live device events) and
   **UsbHidDeviceService** (App-side gRPC client service owning that subscription's lifecycle),
   if not already present.
4. `docs/plans/usbhid-stream-reconnect/usbhid-stream-reconnect-plan.md` — this plan.

No proto changes, no new NuGet packages, no `.slnx`/`Directory.Build.props` changes, no
installer/uninstaller impact.

## Acceptance criteria

- [x] Given an established `SubscribeOnDevices` stream, when the response stream **ends**
      (`MoveNext` returns false), then `AwaitServerStreaming` returns, `RestartCommunication`
      returns false, and a **new** `SubscribeOnDevices` call is issued after the retry delay
      (observable in tests as a second call within the test timeout with an instant delay seam).
- [x] Given an established stream, when the response stream **faults** (`RpcException`, e.g.
      `Unavailable`), then the same reconnect happens and `NotifyError` is raised once for the
      fault.
- [x] Given N consecutive stream deaths (N ≥ 3), each is followed by a new subscribe attempt, and
      every duplex call created is disposed exactly once (observable via the call's dispose
      action) — no unbounded growth of calls, tasks, or cancellation sources.
- [x] Given an established stream, when `StopCommunication()` is called, then the pending
      `StartCommunication()` task completes, `IsRunning` becomes false, **no further** subscribe
      attempts are made, and no `NotifyError` is raised for the deliberate cancellation.
- [x] Given `StopCommunication()` while the host is down (loop in its retry-delay phase), the
      `StartCommunication()` task completes without waiting out a full extra retry delay
      (observable with a delay seam that tracks whether it was awaited after stop).
- [x] Given a reconnected stream, pushed responses still dispatch as before: a
      `DpiStageChange` raises `DpiStageChanged` and updates `TryGetLastDpiStage`; a device entry
      raises `DeviceFound` (golden path through the restructured read path).
- [x] `dotnet test ./src/ProveGaming.slnx` passes; no public API of
      `IUsbHidDeviceService` changes.

## Test strategy

Kind of change: **bug fix** → the repro test is written first and must fail on current code.

Project: `tests/ProoveGaming.App.Tests`, new file `UsbHidDeviceServiceReconnectTests.cs`, xUnit
v2, plain facts with `async Task` bodies and explicit timeouts (never an unbounded await — the
buggy code *hangs*, so every await on the service races a `Task.Delay` timeout via
`Task.WhenAny`, mirroring how the repo's threading tests avoid deadlocking the runner).

Today `UsbHidDeviceService` is only ever faked behind `IUsbHidDeviceService` (12 fakes in
`tests/`), and its public constructor needs an on-disk `appsettings.json` via
`ConfigurationManager` — the class itself has zero tests. The seams in the change list are the
minimal fix for that: tests construct the service via the internal constructor with a
`Device.DeviceClient` subclass whose overridden `SubscribeOnDevices()` returns a scripted
`AsyncDuplexStreamingCall<SubscribeOnDevicesRequest, SubscribeOnDevicesResponse>` (fake
`IClientStreamWriter` + fake `IAsyncStreamReader` driven by a queue/`TaskCompletionSource`, and a
dispose-counting dispose action).

Cases:

1. **Repro (write first — red on current code):** stream yields one response then ends
   (`MoveNext` → false). Assert a second `SubscribeOnDevices` call occurs within the timeout.
   On current code the second call never comes — the test fails by timeout, not by hanging.
2. Stream faults with `RpcException(Unavailable)` → reconnect occurs; `NotifyError` observed.
3. Three consecutive stream deaths → four subscribe calls total; every call's dispose action ran
   exactly once.
4. `StopCommunication` mid-stream → `StartCommunication` task completes; `IsRunning` false; call
   count stops growing; zero `NotifyError` events.
5. `StopCommunication` during the retry-delay phase (host "down": `SubscribeOnDevices` write
   faults immediately) → start task completes promptly; delay hook not awaited again after stop.
6. Event dispatch through the new read path: scripted `DpiStageChange` response →
   `DpiStageChanged` raised, `TryGetLastDpiStage` returns the pushed index.

Deliberately **not** covered by automated tests: real gRPC transport behavior (HTTP/2 reconnect
of `GrpcChannel` after a service restart — exercised by QA below), the real DeviceHost, Avalonia
dispatcher marshalling in `HomeViewModel` (existing coverage/patterns own that), and timing of
the real 3 s delay.

## QA (manual test steps)

1. Install and launch the Proove Gaming app with the device service running (normal install).
   Connect a supported mouse (e.g. TAIPAN) and open Home. Expected: the device card appears and
   shows a battery level.
2. Open Windows **Services** (`services.msc`), find **ProoveGamingDeviceHost**, and **Restart**
   it while the app stays open. Expected: within a few seconds the app is live again — no app
   restart needed.
3. Verify live events resumed: change the DPI stage using the mouse's DPI button. Expected: the
   DPI indicator in the app updates within ~1 second.
4. Unplug the mouse, then plug it back in. Expected: the device card disappears and reappears,
   as it does in a fresh session.
5. Repeat step 2 (service restart) two more times, checking step 3 after each. Expected: events
   resume every time, not just the first.
6. In Services, **Stop** the service and wait ~30 seconds, then **Start** it. Expected: while
   stopped the app may show a connection error notice; after the service starts, device events
   resume within a few seconds without restarting the app.
7. Minimise the app to tray and restore it a few times during/after these restarts. Expected: no
   freezes, no duplicate device cards, no stream of error toasts when simply minimising.

## Review focus

1. `async/await` — no sync-over-async introduced; token flows into `MoveNext`; the `finally`
   around `CompleteAsync` cannot swallow the real outcome.
2. `resource disposal` — per-attempt call/CTS/TCS lifetimes across repeated restarts; `using var
   call` still disposes on every exit path.
3. `thread-safety` — `_reconnecting`/`_cts`/`_tcs` are touched from `StopCommunication` (UI
   thread) and the reconnect loop (thread pool) concurrently; the fix must not widen that race.
4. `gRPC contract` — confirm no proto/behavioral change on the wire; the host sees only an extra
   re-subscribe after its own restart.

## Performance & quality

- No hot path: this is a once-per-3-seconds control loop, not per-frame or per-event work. The
  removed `Task.Run` slightly *reduces* thread churn.
- Nothing here may run on the UI thread; `StartCommunication` stays fire-and-forget from
  `HomeViewModel.Start()` and device events keep arriving on gRPC continuation threads
  (unchanged — `HomeViewModel` already dispatches).
- Shared state: the singleton's `_cts`/`_tcs`/`_reconnecting` cross threads; the design keeps the
  existing coordination (TCS + token) rather than adding locks, and does not add new shared
  mutable state.
- While the host is down, `NotifyError` fires roughly once per 3 s retry (same as today's
  connect-failure path) — acceptable; not a new behavior.

## Risks

- **Pre-existing `Stop()`→`Start()` race (not fixed here):** if `Start()` runs while the old
  `StartCommunication` loop is still unwinding, the `IsRunning` guard makes the new call a no-op
  and the old loop then exits — leaving no stream until the next Home load. This exists today;
  the fix must not make it worse (the `_reconnecting`-guarded delay slightly shrinks the window).
  If the reviewer judges it worse than believed, it becomes a follow-up plan, not scope creep here.
- **`NotifyError` noise on host restart:** the first fault after a host death now surfaces once
  per attempt; QA step 6 checks it is not a toast storm.
- **`_tcs`/`WhenAny` race on stop:** `StopCommunication` cancels the token before completing the
  TCS, so `serverStreaming` may win the `WhenAny` and return false — safe today because
  `_reconnecting` is already false, and the guarded delay keeps it prompt; tests 4–5 pin this.
- **Fake-based tests drift from real gRPC semantics:** `MoveNext` returning false vs. throwing
  differs by failure mode; the tests cover both, and QA covers the real transport.

## Open questions

- None blocking. (If product wants a visible "reconnecting…" indicator instead of the existing
  error notification, that is a separate UI feature — this plan only restores the events.)

## Review findings

### Round 1 — Ship: yes

Scope note: this round reviews the pipeline delta only — the uncommitted changes vs commit
97e7f5a (`src/ProoveGaming.App/Services/UsbHidDeviceService.cs`, new
`tests/ProoveGaming.App.Tests/UsbHidDeviceServiceReconnectTests.cs`, `docs/CONTEXT.md`).
Committed branch history (installer fix, plan docs) is out of scope per dispatch.
Verified independently: `dotnet build ./src/ProveGaming.slnx` clean (0 warnings, 0 errors);
`dotnet test ./src/ProveGaming.slnx` fully green — 1715 passed, 0 failed (Installer 8,
Infrastructure 767, App 940), including the 6 new reconnect tests.

#### Plan conformance

Plan: `docs/plans/usbhid-stream-reconnect/usbhid-stream-reconnect-plan.md`
- Implemented as planned: Option B fix — keep-alive `while`/`Task.Delay(3000)` loop and the
  `Task.Run` wrapper removed; `ReadServerResponses` awaited inline with a best-effort
  `CompleteAsync` in a `finally` that cannot mask the real outcome
  (`UsbHidDeviceService.cs:736-761`); deliberate-stop silencing via exception filters for
  `OperationCanceledException` and `RpcException(Cancelled)` under a cancelled token
  (`:763-770`); `_reconnecting`-guarded retry delay routed through the `RetryDelayAsync`
  seam with `OperationCanceledException` handled (`:651-661`); internal client-injection
  constructor + `_ownsChannel` so the release path through the public constructor is
  unchanged (`:21`, `:41-46`, `:637-642`); both glossary entries (`SubscribeOnDevices
  stream`, `UsbHidDeviceService`) added to `docs/CONTEXT.md`.
- Deviates from plan: none found.
- Missing / partial: none. All 6 Test-strategy cases are present in
  `UsbHidDeviceServiceReconnectTests.cs` (repro stream-end, fault+NotifyError-once, 3
  deaths with dispose-exactly-once, quiet mid-stream stop, stop during retry delay, DPI
  dispatch after reconnect). All acceptance criteria are met.
- Accepted limitation (per plan): the pre-existing `Stop()`→`Start()` race and the
  Stop-vs-`RestartCommunication` CTS lifecycle race (`StopCommunication`'s `_cts?.Cancel()`
  can hit a just-disposed CTS in a tiny window — `:674-686` vs `:705-710`) remain; the fix
  does not widen them (the `_reconnecting`-guarded delay slightly shrinks the stop window).
  Residual risk: a theoretical `ObjectDisposedException` on stop, unchanged from before.

#### Code

- none — no Blockers, no Should-fix in the delta.

#### Tests

- [x] **T1. Production-path quiet-stop filter untested** — the
  `catch (RpcException) when (cancelled && StatusCode.Cancelled)` filter
  (`UsbHidDeviceService.cs:767`) is never exercised: the fake's `BlockUntilCancelled`
  surfaces cancellation as `OperationCanceledException` (from `Task.Delay`), so the
  quiet-stop assertion in `StopCommunication_MidStream_CompletesStartQuietlyAndStopsSubscribing`
  only pins the OCE filter — while real gRPC most often surfaces a deliberate cancel as
  `RpcException(StatusCode.Cancelled)`. Add a variant where the scripted reader throws
  `new RpcException(new Status(StatusCode.Cancelled, ...))` once the token is cancelled and
  assert zero `NotifyError` — `tests/ProoveGaming.App.Tests/UsbHidDeviceServiceReconnectTests.cs`.
  **Closed (Round 1 fix pass):** added `StopCommunication_MidStream_RpcCancelledSurface_CompletesStartQuietly`
  with a `BlockUntilCancelledThenRpcCancelled` reader step; zero `NotifyError` asserted; 7/7 green.

#### Nits

- `IsRunning = false` sits after the retry loop inside the `try`
  (`UsbHidDeviceService.cs:664`); an exception escaping the loop would leave `IsRunning`
  stuck true and permanently no-op future `StartCommunication` calls. Not concretely
  reachable in the fixed code (`AwaitServerStreaming` swallows everything; only a
  disposed-CTS `Token` read during app shutdown could throw) and a pre-existing shape — but
  moving it into a `finally` is a one-line hardening. **Addressed in the Round 1 fix pass:** the
  guard/early-return stays outside the `try`; `IsRunning = false` now runs in a `finally`.
- The plan's Acceptance criteria checkboxes are still unticked; tick the ones now met. **Done** (Round 1 fix pass).

#### What's done well

- Exception filters (`when`) keep deliberate-stop silence from hiding genuine faults; a
  fault without a cancelled token still raises `NotifyError` exactly as before.
- Test fixtures (scripted reader steps, dispose-counting attempts, every await raced
  against a 5 s timeout) match the plan's Test strategy exactly and cannot deadlock the
  runner if the bug regresses.
- The seams are genuinely behavior-neutral for release: `_ownsChannel` defaults true and
  the public-constructor flow is byte-for-byte today's flow plus the fix.

#### Verdict

Ship: yes — the delta implements the plan's Option B faithfully, all acceptance criteria
are met on a green build and full suite, and the only findings are one small test-coverage
gap (T1) and two nits. First: 1) T1 cancelled-`RpcException` quiet-stop test; 2) tick the
acceptance-criteria boxes; 3) optionally move `IsRunning = false` into a `finally`.

### Round 2 — Ship: no

Branch `fix/fixes-on-09.01` (2 commits: `ef8b522` icon sizing / PG-643, `3239bf7` host-loss
deactivation) against `main`. Working tree clean; whole delta is committed. Reviewed against
both addenda below and against `add-headset-glow-and-window-resizing` for the MainWindow hunk.

#### Plan conformance

- **Implemented as planned** — every item in Addendum 1 and Addendum 2's File-level change lists is
  present: deadlines and guards on `GetDevices`/`GetBatteryLevelAsync`, the `RestoreHidTransports`
  empty guard, `HostAvailabilityChanged` end to end, 18 fakes updated, 16 new tests across two files.
- **Deviates from plan** — Addendum 2 states "a deliberate stop stays silent … hence the
  `_reconnecting` gate". The gate does not actually achieve that, because a *later* `Start` sets
  `_reconnecting` back to true before the abandoned attempt reaches its `finally`. See B1: the plan's
  stated invariant and the code disagree, and the code is the one that is wrong.
- **Missing / partial** — no test covers the Stop → Start sequence, which is the exact path B1 breaks
  (T1 below). `add-headset-glow-and-window-resizing` was not updated for the caption attribute (S2).
- **Accepted limitation (per plan)** — `GetDevices` stays synchronous; residual risk is now bounded by
  the 5 s deadline rather than unbounded, which is a real improvement and fine to leave.

#### Code

- [x] **B1. A tray restore or a return to Home can blank every card** — `UsbHidDeviceService.cs:864` — Blocker
  **Problem:** the `finally` gates the "host unavailable" signal on `_reconnecting`, intending to stay
  silent on a deliberate stop. But `RestartCommunication:745` **abandons** `AwaitServerStreaming` — when
  `completionTask` wins the `Task.WhenAny`, the streaming task is never awaited and keeps unwinding on
  its own. `_reconnecting` is shared mutable state that the *next* `StartCommunication:695` sets back to
  true. So the abandoned attempt's `finally` can run after the restart and read `true`.
  **Why it blocks:** `HomeView.OnUnloaded → Stop()` / `OnLoaded → Start()` run on every Home load and
  unload — minimise-to-tray *and* navigating Home → Device Details → Home. Concretely: cards connected
  (`_hostAvailable == 1`); user opens a device's details; `Stop()` unsubscribes and cancels; `Start()`
  re-subscribes the handler and sets `_reconnecting = true`; the old attempt then reaches its `finally`,
  sees `true`, and raises `false`; `HomeViewModel.OnHostAvailabilityChanged` deactivates every card. The
  new subscription's device-list push may have already landed, in which case nothing repopulates them
  and **every device shows as disconnected until the user physically replugs one**. `StartCommunication`
  disposing `_channel` at :690 actively provokes the old attempt to unwind at precisely this moment, so
  this is not a narrow timing coincidence.
  **Solution options:**
  - Option A (correct, one line): gate on the attempt's own token as well —
    `if (_reconnecting && !cancellationToken.IsCancellationRequested) SetHostAvailable(false);`.
    Both `StopCommunication` and the next `RestartCommunication` cancel that token, so an abandoned or
    deliberately-stopped attempt is silent, while a genuine stream fault (token *not* cancelled) still
    reports. Verified against all four paths: host death, deliberate stop, abandoned attempt, retry.
  - Option B (structural): give each attempt a generation number and let only the current generation
    report. More code, same outcome; worth it only if more per-attempt state appears later.
  **Fixed 2026-09-01 (Option A)** — `UsbHidDeviceService.cs:874`. Reproduced first: the new test below
  failed on `Assert.DoesNotContain(false, …)` with the spurious transition at position 1, then passed
  with the token gate in place. `HostDyingMidSession_ReportsAvailableThenUnavailable` still passes, so
  a genuine stream fault continues to report.

- [ ] **S1. `_reconnecting` is read across threads without a barrier** — `UsbHidDeviceService.cs:20` — Should-fix
  **Problem / why it matters:** written by `StopCommunication`/`StartCommunication` (UI thread) and now
  read by `AwaitServerStreaming`'s `finally` (thread pool) as a plain `bool`. The new read is the first
  one whose staleness has a *visible* consequence rather than just a wasted loop iteration. The tests
  already document the flip side — a retry delay that does not yield turns the loop into an unbounded
  spin because the field is never re-read — which is the same missing-barrier smell from the other end.
  **Fix:** mark it `volatile`, or fold the decision into the token check from B1 (Option A makes the
  `_reconnecting` read non-load-bearing, which is the cheaper route — do B1 first and re-judge).

- [ ] **S2. `add-headset-glow-and-window-resizing` is now stale** — `docs/plans/add-headset-glow-and-window-resizing/…-plan.md:63` — Should-fix
  **Problem / why it matters:** that plan owns the window chrome and says "the **two** attributes are
  what keep it square and invisible". This branch adds a third (`DWMWA_CAPTION_COLOR`, `MainWindow.axaml.cs:356`)
  with a hardcoded `#150B2B` chosen to blend with a background gradient. The repo's documentation rule
  is that a plan describing behaviour a change touches is updated in the same change; the next person
  re-tuning the window frame reads "two" and will not know the caption is being painted, or why that
  colour.
  **Fix:** add the caption attribute to that plan's file-level change list and its QA steps, and note
  the colour's derivation. One paragraph.

- [ ] **S3. `RestoreHidTransports` cannot distinguish "no devices" from "could not ask"** — `HomeViewModel.cs:2338` — Should-fix
  **Problem / why it matters:** the early return on an empty list is the right call given today's
  signature, and the comment is honest about it, but the ambiguity is structural: a host that is alive
  and genuinely enumerates nothing now leaves `HasWire`/`HasDongle` stale instead of clearing them. The
  window is small (the flags self-correct on the next HID event, and B1's signal blanks the card
  outright when the host is truly gone) — but the repo already has the idiom for this, in
  `DeviceNodeQuery.EnumerateIds` returning `List<string>?` with null meaning "could not read".
  **Fix:** either leave it with the comment as the record of a known ambiguity (acceptable), or make
  `GetDevices` express failure distinctly. Not worth churning `IUsbHidDeviceService` and 18 fakes for
  on its own — fold it into the next change that touches that signature.

#### Tests

- [x] **T1. No coverage for the Stop → Start sequence** — the exact path B1 breaks. `DeliberateStop_DoesNotReportTheHostAsUnavailable`
  stops and never restarts, so it passes while the bug is live. Add a test that starts, stops, immediately
  starts again with a client whose abandoned attempt unwinds slowly, and asserts no `false` transition
  reaches the subscriber — `tests/ProoveGaming.App.Tests/UsbHidDeviceServiceHostAbsentTests.cs`.
  **Added 2026-09-01** as `StopThenStart_DoesNotBlankTheCards`, with a `CompleteDelay` seam on the fake
  writer that holds the retired attempt in its inner `finally` so the race is deterministic rather than
  a coin flip. Verified red before the B1 fix and green after.
- [ ] **T2. `HostAvailabilityChanged` is never asserted end-to-end into `HomeViewModel`** — the service
  side and the card side are each well covered, but nothing pins that the event is *subscribed*. Deleting
  the line in `SubscribeDeviceEvents` leaves all 2318 tests green. The file's own comment warns this
  exact class of bug already bit `BatteryChanged` (added to Start, missed in Stop). A fake raising the
  event and asserting the cards deactivate would close it — `HomeViewModelHostLossTests.cs`.

#### Nits

- `SingleCard.axaml:229` — the comment still opens "16x16 throughout, matching DoubleCard" and then
  explains four lines later that the wire is 14. Self-contradictory in one block, in the exact place
  someone reads to pick a size.
- `UsbHidDeviceService.cs:76` — the 5 s `GetDevices` deadline is a bare literal while the sibling calls
  use 20–30 s; the reasoning is in prose but the number is unexplained. A named constant, or half a
  sentence saying why enumeration gets a tighter budget than a device write, would carry it.

#### What's done well

- The two guards and the `RestoreHidTransports` early return compose correctly: with the host truly
  gone the card is blanked by the availability signal, so the early return only ever holds a flag
  across a *transient*. That interaction is easy to get wrong and was reasoned about, not stumbled into.
- Edge-triggered rather than level, with the 3 s retry loop named as the reason. A level signal here
  would have been the obvious implementation and would have re-blanked every card forever.
- `true` deliberately carrying no payload, because the host re-sends the device lists on subscribe.
  That is what makes the spurious-`true` case (buffered HTTP/2 write against a dead host) harmless by
  construction rather than by luck, and it is written down.
- `UsbHidDeviceServiceHostAbsentTests` sweeps the whole interface in one test with the reason stated —
  the property is "none of them throws", and seventeen near-identical tests would still miss the
  eighteenth. The `Task.Delay(1)` comment records a real trap (a non-yielding retry delay spins a core)
  that would otherwise be rediscovered painfully.
- Deactivation was checked against the low-battery path rather than assumed safe: `MakeInactive` nulls
  the level and `LowBatteryDecision` returns early on null, so no toast escapes. That is the kind of
  second-order check that usually gets skipped.

#### Verdict

Ship: no — B1 turns a routine navigation (Home → Device Details → Home) or a tray restore into "every
device shows disconnected", which is worse than the stale-card bug this branch set out to fix and hits
far more often. It is a one-line fix (Option A) plus the test that would have caught it. Everything
else on the branch is sound. First: 1) B1 token gate; 2) T1 Stop→Start test; 3) T2 subscription test.

---

## Addendum — 2026-09-01: the unary calls, not just the stream

The plan above made **stream** death observable and recoverable. A follow-up question — "will the
App survive the DeviceHost being stopped or uninstalled?" — surfaced that the same guarantee did
not hold for the **unary** RPCs on the same client. Recorded here rather than in a new plan because
it is the same failure (the host is gone) on the same file, and the state machine above is unchanged.

**What was actually wrong** (two of seventeen unary methods; the other fifteen already returned a
documented fallback):

1. `GetDevices()` had **no deadline**. This is the more serious half and is a *hang*, not a crash:
   `GrpcChannel` disables the underlying `HttpClient` timeout and expects per-call deadlines, so
   against a host that is *unreachable* rather than cleanly *refused* — a service mid-start, a
   half-open localhost connection — the blocking call never returns. It runs on the thread holding
   `HomeViewModel`'s `_eventGate`, so every later device event queues behind it indefinitely. A
   stopped service refuses the connection and fails fast, which is why this never showed in testing.
2. `GetDevices()` and `GetBatteryLevelAsync` also **threw** where every sibling returns a fallback.
   Neither crashed the app: `UsbHidDeviceService_DeviceFound` catches, and both battery call sites
   wrap the call. Both were latent contract violations that the next caller would have inherited.

**Changes**

- `src/ProoveGaming.App/Services/UsbHidDeviceService.cs` — `GetDevices()` gains a 5 s deadline plus
  the standard `RpcException`/`Exception` guards, returning an empty list; `GetBatteryLevelAsync`
  gains a 20 s deadline and the same guards, returning `(-1, false)`.
- `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` — `RestoreHidTransports` now returns early when
  `GetDevices()` enumerates nothing. Necessary *because* of the change above: an empty list is now
  also what an unreachable host produces, and applying it would clear `HasWire` on a device whose
  cable is still connected — the "card went dark while audio kept playing" failure that method
  exists to prevent, triggered by the host dying instead of by Bluetooth dropping.
- `tests/ProoveGaming.App.Tests/UsbHidDeviceServiceHostAbsentTests.cs` — new; 7 tests. Four are red
  against the pre-fix service (verified); three pin stream-loop behaviour this plan already made
  correct.

**Deliberately not done**

- **No user-facing message.** Losing the host raises no toast, dialog or card state; `NotifyError`
  is logged and nothing more. Confirmed as intended on 2026-09-01.
- ~~Cards keep their last known `Connected` state while the host is gone, because nothing marks them
  offline on stream loss.~~ **Reported the same day and fixed — see the second addendum below.**
- `GetDevices()` stays **synchronous**. The deadline removes the unbounded block; making it async
  would change `IUsbHidDeviceService` and ~20 test fakes for no additional safety.
- `_reconnecting` is a plain `bool`. Safe in production (the real retry delay is
  `Task.Delay(3000)`, so it is always re-read across an async yield), but a retry delay that does
  *not* yield turns the loop into an unbounded spin — which is why the new tests use
  `Task.Delay(1)` rather than `Task.CompletedTask`.

**Verification** — full suite in Release: 2308 passed, 0 failed (827 Infrastructure + 1473 App +
8 Installer).

---

## Addendum 2 — 2026-09-01: cards must go inactive when the host stops

Reported directly after the addendum above: *"when we have host service down and device was
connected, it's never get disconnected, but should"*. Correct, and it is the same root cause seen
from the UI side.

**Why it happened.** Device presence reaches the App **only as a push** over the
`SubscribeOnDevices` stream. `DeviceCardViewModel.Connected` is derived (`HasWire || HasDongle ||
HasBluetooth`), and those flags are only ever written by a push handler. So when the service stops,
the last push before it died stands unchallenged forever: no timer, poll or heartbeat in the App
re-derives presence, and the battery poll skips the card rather than contradicting it. The App knew
the stream was dead — the reconnect loop was retrying every 3 s — but nothing connected that
knowledge to the cards.

**Approach.** Make "the host is answering" an explicit signal rather than something only the
reconnect loop knows, and let `HomeViewModel` act on it. Edge-triggered, not level: the loop retries
every 3 s for as long as the service is down, so a level signal would re-blank every card forever.

**File-level change list**

- `src/ProoveGaming.App/Services/UsbHidDeviceService.cs` — adds `_hostAvailable` (an `int` claimed
  through `Interlocked.Exchange`, since the stream attempt and `StopCommunication` run on different
  threads) and raises `HostAvailabilityChanged` on transitions only. True once the subscribe write
  is through; false in a `finally` when the attempt ends, **gated on `_reconnecting`** so a
  deliberate stop stays silent.
- `src/ProoveGaming.App/Services/IUsbHidDeviceService.cs` — declares the event (and so all 18 test
  fakes gain the member).
- `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` — subscribes/unsubscribes alongside the other
  device events, and on `false` marks every connected card inactive via the new
  `internal static DeactivateCards`.
- `tests/ProoveGaming.App.Tests/UsbHidDeviceServiceHostAbsentTests.cs` — 4 more tests (10 total).
- `tests/ProoveGaming.App.Tests/HomeViewModelHostLossTests.cs` — new; 6 tests on the card rule.

**Design notes, and the traps in them**

- **`StopCommunication` must not look like host loss.** Leaving Home and minimising to tray both
  call it — and so does every Home → Device Details → Home navigation. Blanking the cards on the way
  out would be wrong and visible on the next restore. The gate is the **attempt's own cancellation
  token**, not the shared `_reconnecting` flag: `RestartCommunication` abandons `AwaitServerStreaming`
  rather than awaiting it, so a retired attempt outlives the `Start` that flips `_reconnecting` back
  to true and would otherwise report a host loss that never happened. Corrected 2026-09-01 after
  review R2 B1 caught it; `StopThenStart_DoesNotBlankTheCards` pins it.
- **Nothing is shown to the user**, per the 2026-09-01 decision recorded in the first addendum. In
  particular the per-device "device disconnected" notification is deliberately **not** raised here:
  one toast per card the instant a service stops is precisely the announcement we chose not to make.
  Verified that deactivation cannot fire a low-battery toast either — `MakeInactive` nulls the
  level, and `LowBatteryDecision` returns early on a null level.
- **Recovery needed no work.** `DeviceService.SubscribeOnDevices` answers a fresh subscribe with the
  current USB list and each Bluetooth device, so the ordinary `DeviceFound` path lights the cards
  back up. That is why the `true` transition carries no payload and `HomeViewModel` ignores it.
- **A launch that never reaches the host raises nothing**, because "unavailable" is the starting
  state and there are no cards yet to deactivate. Only a host that *was* answering produces the
  transition — which is the reported case, including tray round-trips (a deliberate stop leaves
  `_hostAvailable` true, so a failed re-subscribe on restore does fire).
- **A spurious `true` is harmless by construction.** A real HTTP/2 client-stream write is buffered
  and can complete before the server acknowledges anything, so a dead host may let
  `SubscribeOnDevices` and `WriteAsync` through and fail only at the first read. That yields
  `true` then immediately `false`; since nothing acts on `true`, the net effect is correct.

**Verification** — full suite in Release: 2319 passed, 0 failed (827 Infrastructure + 1484 App +
8 Installer), after the R2 B1 fix.

**Not covered by automated tests** — the end-to-end path (stop the Windows service, watch the cards
dim; start it, watch them come back) needs the hardware. QA steps:

1. Launch the App with at least one device connected; confirm the card is active.
2. `sc stop ProoveGamingDeviceHost` (elevated). **Expected:** within a few seconds every card dims
   to the disconnected style, battery indicators clear, and **no** toast or dialog appears.
3. `sc start ProoveGamingDeviceHost`. **Expected:** the cards come back active on their own within
   ~3 s, with battery levels returning on the next push or poll.
4. Repeat step 2 with the App minimised to tray, then restore. **Expected:** the restored window
   shows the cards inactive, again with no message.
5. Minimise to tray and restore with the service left RUNNING. **Expected:** cards stay active
   throughout — the tray round-trip must not be mistaken for host loss.
