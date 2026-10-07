# RGB service-cache eviction and glow-timer teardown

Fixes two confirmed finding groups from the 2026-08-21 performance/memory audit
(`docs/audits/2026-08-21-performance-memory-audit.md`):

- **C2** — the Device Details RGB glow animation timers (33 ms `DispatcherTimer`s) keep
  running forever after leaving the page, burning UI-thread CPU even while the app sits in
  the tray.
- **C7 + C11** — `DeviceRgbServiceFactory`'s two mouse-service caches (`_serviceCache` for
  Easen, `_telinkCache` for TAIPAN) are never evicted, so a mouse replugged into a
  different USB port is served a service bound to the old, dead `DevicePath` until the
  `ProoveGamingDeviceHost` Windows Service restarts.

## Goal

Stop the App from doing perpetual 30 Hz animation work for a page nobody can see, and stop
DeviceHost from silently failing every RGB/DPI/button write to a replugged mouse. Both are
lifecycle bugs, not feature work: the glow timers need a teardown at the page's existing
deactivation seam (with the existing re-entry path restarting them), and the mouse caches
need the same device-list-change invalidation the keyboard cache already received (factory
constructor, PR #115 review M1) — the mouse dictionaries were simply left out of that
handler, as the factory's own `KNOWN PRE-EXISTING DEFECT` comment records.

## Scope

**In scope**

- Stop both glow timers (`_animationTimer`, `_hueCycleTimer`) when the user leaves Device
  Details (`ClearDeviceForExit`, called from `MainWindow.PerformExitDeviceDetails`).
- Pause the glow timers when the window hides to tray while Device Details is open, and
  resume them on restore — the audit's harm statement is "CPU work continues even in
  tray", and Back-to-Home alone does not cover close-to-tray from the details page.
- Evict `_serviceCache` and `_telinkCache` in `DeviceRgbServiceFactory.OnDeviceListChanged`,
  mirroring the existing `_keyboardCache` invalidation.
- Rewrite the now-false `KNOWN PRE-EXISTING DEFECT` comments in the factory (lines 14–25).
- Repro-first xUnit tests for both fixes (see Test strategy).

**Out of scope**

- The sibling factories `DeviceActuationServiceFactory`, `DeviceKeyMappingServiceFactory`,
  and `DeviceKeyboardMacroServiceFactory` were checked for the same pattern and **do not
  share it**: none of them caches service instances — each `CreateForDevice` call runs a
  fresh config lookup + interface selection and constructs a new service. There is nothing
  to evict, so they need **no change** (this is a verified no-op, not a deferral).
- `_loggedMissingConfig` in the RGB factory stays as is — it is a bounded log-dedup set
  keyed by VID/PID, not a device-object cache; clearing it would only re-log a debug line.
- Targeted per-entry eviction (removing only entries whose `DevicePath` vanished) — see
  Options considered.
- Other audit findings (C3–C6, C8–C24).
- Disposal of evicted services: `EasenMouseService` and `TelinkMouseService` are not
  `IDisposable` and open their HID stream per write, so dropping the reference is the
  complete teardown. No disposal work exists to schedule.

### Process impact (shared-project constraint)

`DeviceRgbServiceFactory` lives in `ProoveGaming.Infrastructure`, which both App and
DeviceHost reference. However, the factory is **registered and constructed only in
DeviceHost** (`src/ProoveGaming.DeviceHost/Program.cs` line 59,
`AddSingleton<IDeviceRgbServiceFactory, DeviceRgbServiceFactory>()`); its consumers are
`DeviceService`, `DpiStageMonitor`, and `TaipanStateMonitor`, all DeviceHost-side. The App
performs device I/O exclusively over gRPC and never instantiates the factory, so:

- **DeviceHost**: gets the behavioral change — mouse caches now clear on every HID
  device-list change; the next `CreateForDevice` re-enumerates and binds to the live
  `DevicePath`. The discovery singletons (`HidDeviceDiscoveryService`,
  `BluetoothDeviceDiscoveryService`, `DeviceInfoProvider`) are **not touched** — only the
  factory's derived caches are evicted, per the CLAUDE.md rule against resetting the
  long-lived discovery state.
- **App**: recompiles against the changed Infrastructure assembly but has no runtime path
  through the factory; its own change is confined to `DeviceDetailsViewModel` /
  `App.axaml.cs` (the C2 fix).

## Options considered

### C2 — where to stop the timers

1. **Stop in `ClearDeviceForExit` + explicit tray pause/resume (recommended).**
   The app has no navigation framework — page flips are `IsVisible` toggles driven by
   `MainWindow._currentStep`/`UpdateStepUi()`, and the single deactivation seam for Device
   Details is `PerformExitDeviceDetails()` → `_deviceDetailsViewModel.ClearDeviceForExit()`
   (`MainWindow.axaml.cs:595–608`). `ClearDeviceForExit` already stops volume tracking and
   the slot self-heal loop; adding the two timer stops completes the teardown symmetric
   with what re-entry restarts. Re-entry needs **no new code**: `SetDeviceAsync` →
   `LoadRgbStateForCurrentDeviceAsync` → `ApplyResolvedRgbState` already calls
   `StartEffectAnimation` when RGB is enabled (`DeviceDetailsViewModel.Rgb.cs:926–942`).
   The tray case (close-to-tray *while on* Device Details) is handled at the tray seam:
   `App.OnMinimizeToTrayRequested()` / `App.RestoreFromTray()` (`App.axaml.cs:260–291`)
   call new `PauseGlowAnimation()` / `ResumeGlowAnimation()` on the singleton VM.
   *Trade-off*: two seams instead of one, but each is the existing owner of its
   transition; no new events or view attach/detach machinery.

2. **Null-guard the tick lambdas (`if (Device is null) return`).** One-line-per-timer, but
   the timers keep firing 30 times a second — the dispatcher wakeups and the timer
   allocation remain, only the brush work goes away. Fails the audit's actual complaint
   (perpetual UI-thread wakeups in a tray-resident app). Rejected.

3. **Stop timers from the view's `DetachedFromVisualTree` / `IsVisible` change.** The view
   is never detached (only `IsVisible`-toggled), so attach/detach never fires;
   `IsVisible`-watching would put lifecycle logic in the view for a VM-owned resource and
   still miss the tray case (hiding the *window* does not change the view's `IsVisible`).
   Rejected.

### C7 + C11 — how to evict the mouse caches

1. **Wholesale `Clear()` of both mouse caches in the existing `OnDeviceListChanged`
   handler (recommended).** Exactly the mechanism `_keyboardCache` already uses, in the
   handler the factory already subscribes in its constructor
   (`hidDeviceProvider.SubscribeToDeviceListChanged`, backed by HidSharp's
   `DeviceList.Local.Changed` — the same signal `DpiStageMonitor` refreshes on, raised by
   the OS on any HID arrival/removal; no new subscription, no new disconnect plumbing
   needed). *Trade-off*: any device-list change — including an unrelated USB device —
   drops the Easen service instances, so the next mouse write re-resolves interfaces (one
   HID enumeration). List changes are human-scale events (plug/unplug). Accepted.
   *Correction (2026-08-23, PR #119 review M1):* the original trade-off text claimed the
   recreated service "does one read-before-write to repopulate state" — false; read-back
   was removed with `TryReadBlock`, the caches are authoritative and a fresh instance
   starts from `CreateDefault*`. Losing them on eviction made the first narrow apply after
   any USB plug event write factory report-rate/DPI-stage over the user's and re-commit an
   unchanged key matrix to flash. Fixed by parking evicted instances as state donors: the
   factory copies `_lastParam0`, `_lastBlock0`, `_cachedLed*`, `_ledEverApplied` and the
   `X43BlockCommitCache` record into the recreated instance
   (`EasenMouseService.AdoptStateFrom`, `_evictedServiceCache` in the factory), so
   eviction only rebinds the DevicePath and never resets device state.

2. **Targeted eviction — remove only entries whose `DevicePath` no longer exists.**
   Preserves unaffected devices' write caches, but requires exposing the bound
   `DevicePath`(s) from both service classes, enumerating HID devices inside the change
   handler, and handling the two-interface Easen case (write + read device). More code and
   a new public surface on the services for a benefit measured in one skipped feature-report
   read per unrelated plug event. Rejected as premature — can be revisited if list-change
   frequency ever becomes a problem.

3. **Subscribe the factory to `HidDeviceDiscoveryService.HidDeviceListUsbConnected`
   instead.** That event is a DeviceHost-side aggregation of the same underlying HidSharp
   signal, and taking it would give the Infrastructure factory a dependency on the
   discovery service's event surface it does not need — the factory already holds the
   lower-level `IHidDeviceProvider` subscription that the keyboard cache proved out.
   Rejected.

## Recommended approach

- **C2**: add `StopSolidHueCycleTimer(); StopAnimationTimer();` to `ClearDeviceForExit`
  (before `Device = null`, alongside the existing `StopVolumeTracking()` /
  `CancelSlotReadSelfHeal()` teardown). Add two small internal methods in the Rgb partial:
  `PauseGlowAnimation()` (stop both timers) and `ResumeGlowAnimation()` (no-op unless
  `Device != null && IsRgbEnabled`; otherwise `StartEffectAnimation(_selectedEffect)` —
  `StartEffectAnimation` itself already guards `!IsRgbSupported`). Call them from
  `App.OnMinimizeToTrayRequested()` and `App.RestoreFromTray()` respectively, resolving
  the singleton VM from `App.Services`. Add an internal test seam
  `HasActiveGlowTimersForTests => _animationTimer is not null || _hueCycleTimer is not null`
  following the file's existing `*ForTests` seam convention (`InternalsVisibleTo` is
  already in place).
- **C7 + C11**: in `DeviceRgbServiceFactory.OnDeviceListChanged`, add
  `_serviceCache.Clear(); _telinkCache.Clear();` next to the existing
  `_keyboardCache.Clear();`. Update the field comments: delete the
  `KNOWN PRE-EXISTING DEFECT` paragraphs (they become false) and state that all three
  service caches are invalidated on device-list changes, with the null-result-never-cached
  contract unchanged.

## File-level change list

Ordered so the reproducing tests land before the fixes (fix-type change).

1. `tests/ProoveGaming.Infrastructure.Tests/DeviceRgbServiceFactoryTests.cs` — **edit**:
   add mouse-cache repro tests (fail on current code): device-list change must clear the
   Easen and Telink caches (fresh instance + re-enumeration after the captured handler
   fires), and a fresh mouse service must be re-cached after one clear. Mirror the
   existing keyboard tests (`CreateForDevice_Keyboard_DeviceListChange_ClearsCacheAndReEnumerates`,
   `..._FreshServiceIsCachedAgain`): `Mock<HidDevice>` shaped for Easen
   (`GetMaxOutputReportLength()==65`, `GetMaxFeatureReportLength()==9`, mouse VID/PID) and
   for Telink (`GetMaxOutputReportLength()==GetMaxInputReportLength()==TaipanFrame.Length`,
   `Name = "PG TAIPAN"` config row), `deviceKey: string.Empty` to skip Container-ID
   filtering.
2. `tests/ProoveGaming.App.Tests/DeviceDetailsGlowTimerTests.cs` — **create**: repro tests
   for C2 (fail on current code). Reuse the `FakeHid` / in-memory `LocalStorageService` /
   `PumpingSyncContext` setup style of `DeviceDetailsSetDeviceThreadingTests`; card with
   `SupportsRgb = true` (the pattern `DeviceDetailsPresetApplyTests` already proves starts
   real glow timers in this test host). Cases: timers active after `SetDeviceAsync`, then
   `ClearDeviceForExit()` → no active timers; re-`SetDeviceAsync` → active again;
   `PauseGlowAnimation()` → none; `ResumeGlowAnimation()` with device + RGB enabled →
   active; `ResumeGlowAnimation()` with `Device == null` → none.
3. `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs` — **edit**:
   `ClearDeviceForExit()` additionally stops both glow timers; extend the method's comment
   with the teardown rationale (audit C2).
4. `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Rgb.cs` —
   **edit**: add internal `PauseGlowAnimation()` / `ResumeGlowAnimation()` and the internal
   `HasActiveGlowTimersForTests` seam (near the existing timer methods, following the
   file's comment style).
5. `src/ProoveGaming.App/App.axaml.cs` — **edit**: `OnMinimizeToTrayRequested()` calls
   `PauseGlowAnimation()`, `RestoreFromTray()` calls `ResumeGlowAnimation()`, both via the
   DI singleton `DeviceDetailsViewModel` (null-safe if `_services` is not built yet).
6. `src/ProoveGaming.Infrastructure/Services/DeviceRgbServiceFactory.cs` — **edit**: clear
   `_serviceCache` and `_telinkCache` in `OnDeviceListChanged`; rewrite the stale
   `KNOWN PRE-EXISTING DEFECT` field comments to describe the now-uniform invalidation.
7. `docs/CONTEXT.md` — **edit only if needed**: the change introduces no new domain term
   ("glow" is already in the glossary); add a one-liner only if the coder coins one.

No new NuGet packages, no proto changes, no installer/uninstaller impact, no `.slnx` or
`Directory.Build.props` changes.

## Acceptance criteria

- [ ] Given DeviceHost running with an RGB mouse (e.g. BLAZE/X43) connected, when the
  mouse is unplugged and replugged into a **different USB port**, then an RGB colour
  change from the App reaches the hardware (LED visibly changes) **without restarting
  `ProoveGamingDeviceHost`**.
- [ ] Given the mouse-cache unit tests, when a captured device-list-changed handler is
  invoked between two `CreateForDevice` calls for the same Easen or Telink mouse, then the
  second call re-runs HID enumeration (`GetDevices()` invoked again) and returns a
  **different instance** than the first; a third call returns the second instance (cache
  re-populated, not degraded to per-call construction).
- [ ] Given Device Details open on an RGB-capable device (glow animating), when the user
  presses Back to Home, then both glow `DispatcherTimer`s are stopped
  (`HasActiveGlowTimersForTests == false`; no further `AnimatedGlowBrush` updates occur).
- [ ] Given the user is back on Home after leaving Device Details, when they re-open an
  RGB-capable device, then the glow animation runs again (timers active, preview visibly
  animating).
- [ ] Given Device Details open on an RGB-capable device, when the window is closed to
  tray, then both glow timers are stopped; when the window is restored from the tray with
  Device Details still open, the glow animation resumes.
- [ ] Given the window is hidden to tray from the **Home** view (no device open), when it
  is restored, then `ResumeGlowAnimation` is a no-op (no timer started, no exception).
- [ ] Given the existing keyboard-cache and config-row-selection tests in
  `DeviceRgbServiceFactoryTests`, when the full Infrastructure test suite runs, then all
  pre-existing tests still pass (keyboard invalidation behaviour unchanged).
- [ ] Given `DeviceActuationServiceFactory`, `DeviceKeyMappingServiceFactory`, and
  `DeviceKeyboardMacroServiceFactory`, when the branch delta is reviewed, then none of
  them is modified (verified cache-free; no eviction applies).

## Test strategy

This is a **bug fix**, so the reproducing tests are written first and must fail on the
current code before the fixes land (order in the File-level change list reflects this).

**`tests/ProoveGaming.Infrastructure.Tests` — `DeviceRgbServiceFactoryTests.cs`** (Moq +
xUnit, mirroring the existing keyboard-cache tests in the same file):

- Repro: `CreateForDevice_EasenMouse_DeviceListChange_ClearsCacheAndReEnumerates` — create
  for an Easen-shaped mock, fire the captured `DeviceListChangedEventArgs` handler, create
  again → `Assert.NotSame` + `GetDevices()` verified twice. *Fails today* (`Assert.Same`
  holds on current code).
- Repro: `CreateForDevice_TelinkMouse_DeviceListChange_ClearsCacheAndReEnumerates` — same
  contract for the TAIPAN path (`_telinkCache`).
- `CreateForDevice_EasenMouse_AfterDeviceListChange_FreshServiceIsCachedAgain` — the
  invalidation must not degrade the cache to per-call construction (mirrors the keyboard
  variant).
- Golden path guard: existing keyboard/cache/config-row tests stay green untouched.

**`tests/ProoveGaming.App.Tests` — new `DeviceDetailsGlowTimerTests.cs`** (plain xUnit,
`InternalsVisibleTo` seam, `PumpingSyncContext` for `SetDeviceAsync`):

- Repro: `ClearDeviceForExit_StopsGlowTimers` — open an `SupportsRgb = true` card, assert
  `HasActiveGlowTimersForTests`, call `ClearDeviceForExit()`, assert stopped. *Fails
  today.*
- `SetDeviceAsync_AfterExit_RestartsGlowTimers` — the re-entry contract.
- `PauseGlowAnimation_StopsTimers` / `ResumeGlowAnimation_RestartsTimers_WhenRgbEnabled` /
  `ResumeGlowAnimation_NoDevice_IsNoOp` — the tray seam's VM half.

**Deliberately not covered by automated tests**

- Real HID replug and the end-to-end gRPC → DeviceHost → hardware write (needs physical
  devices; covered by QA steps).
- The `App.axaml.cs` tray wiring itself (constructing `App`/`TrayIcon` needs an Avalonia
  platform lifetime the test project does not initialize); the VM-side pause/resume it
  calls is fully covered, the two call sites are one line each and covered by QA step 5.
- CPU-usage measurement of the stopped timers (observable via Task Manager in QA, not
  assertable in xUnit).

## QA (manual test steps)

1. Install/launch the app with the DeviceHost service running and connect an RGB-capable
   mouse (e.g. PG BLAZE). Open its Device Details page and confirm the device image shows
   an animated glow (e.g. Breathing pulses).
2. Click Back to return to Home, then open Task Manager → Details and watch the Proove
   Gaming app's CPU column for ~30 seconds. Expected: CPU sits at 0% (occasional 0.1%
   flickers at most) — no steady per-second activity while idle on Home.
3. From Home, re-open the same mouse's Device Details. Expected: the glow animation runs
   again exactly as before (pulsing/cycling matches the selected effect).
4. With Device Details open, close the window (it hides to the tray). Watch the app's CPU
   in Task Manager for ~30 seconds. Expected: 0% steady. Restore the app from the tray
   icon. Expected: Device Details is still open and the glow animation is running again.
5. Hide the app to tray while on the Home screen, then restore it. Expected: no error, no
   glow anywhere, app behaves normally.
6. With the mouse's RGB set to a distinctive colour, unplug the mouse and plug it into a
   **different USB port** (or through a USB hub). Wait for its card to reappear on Home,
   open Device Details, and change the RGB colour. Expected: the mouse's LED changes to
   the new colour immediately — **without** restarting the "ProoveGamingDeviceHost"
   Windows service or rebooting.
7. Repeat step 6 for a TAIPAN mouse if available (different protocol, same fix). Expected:
   same result.
8. Replug an RGB keyboard (e.g. Rampage) into a different port and change its lighting.
   Expected: still works (unchanged behaviour — regression check).
9. (Added 2026-08-22, review S1.) With Device Details open on an RGB device, close the
   window to the tray, then launch the app EXE a second time (double-click the installed
   shortcut). Expected: the running instance's window is restored with Device Details
   still open and the glow animation running — same result as restoring via the tray
   icon.

## Review focus

1. `resource disposal` — timers stopped and nulled at every deactivation seam; no dangling
   `Tick` subscriptions; evicted mouse services need no disposal (open-per-write, verify
   nothing acquired a stream in the ctor path).
2. `thread-safety` — `OnDeviceListChanged` runs on HidSharp's notification thread while
   gRPC/monitor threads call `CreateForDevice`; `ConcurrentDictionary.Clear()` vs
   concurrent `TryGetValue`/indexer-set must leave no torn state (same contract the
   keyboard cache already accepted).
3. `UI-thread affinity` — `PauseGlowAnimation`/`ResumeGlowAnimation` are called only from
   UI-thread code paths (`ClearDeviceForExit`, tray handlers); `DispatcherTimer`
   start/stop must not happen off-thread.
4. `DI lifetimes` — the factory stays a DeviceHost singleton with its single constructor
   subscription (existing `Constructor_SubscribesToDeviceListChanged_ExactlyOnce` test
   guards it); the App resolves the singleton `DeviceDetailsViewModel`, never constructs a
   second one.

## Performance & quality

- **Hot paths touched**: the 33 ms glow tick (removed when the page is not visible — the
  entire point) and DeviceHost's `CreateForDevice`, which sits on `DpiStageMonitor`'s poll
  and every RGB gRPC call. Cache hits remain allocation-free dictionary lookups; the only
  new cost is a full re-creation (HID enumeration + state adoption from the parked
  predecessor) after a device-list change — a human-scale event (plug/unplug), never
  per-frame or per-poll.
- **Allocations**: no new steady-state allocations in either process. `Clear()` on the
  three dictionaries allocates nothing.
- **Shared singleton state**: only the factory's derived caches are cleared; the discovery
  singletons and their in-memory device state are untouched (CLAUDE.md constraint).
  `ConcurrentDictionary` keeps the handler lock-free.
- **UI thread**: strictly less work — two timer `Stop()` calls at exit/hide; no blocking
  I/O added anywhere; `ResumeGlowAnimation` only re-arms a timer.
- **Sync-over-async**: none introduced; no async surface changes.

## Risks

- **A creation in flight during a list change can re-cache a stale instance**: a
  `CreateForDevice` that enumerated before the change commits its (possibly dead-path)
  service after `Clear()` runs. The window is milliseconds, the same race the keyboard
  cache accepted, and the next list change (or the failed write's retry after one) clears
  it again. Accepted; do not add locking for it.
- **Lost Easen write-state on unrelated plug events**: closed (2026-08-23, PR #119
  review M1). The device is never read back — the caches are authoritative — so an
  eviction used to reset them to factory defaults, and the first narrow apply after any
  USB plug event wrote those defaults to the mouse. Evicted instances are now parked in
  `_evictedServiceCache` and donate their full write-state (`_lastParam0`, `_lastBlock0`,
  `_cachedLed*`, `_ledEverApplied`, the flash-commit record) to the recreated instance
  via `EasenMouseService.AdoptStateFrom`. The residual exposure is the accepted
  in-flight-creation race above (an instance cached between the park loop and `Clear()`
  is lost without donating — one defaults-based apply, same as a DeviceHost restart).
  Watch QA step 6 for any visible DPI/LED glitch right after a replug.
- **Long-lived captures of a created service**: `DeviceService`, `DpiStageMonitor`, and
  `TaipanStateMonitor` are believed to call `CreateForDevice` per operation/poll rather
  than caching the returned instance across polls — the coder must verify this while
  implementing; a consumer that holds an instance across list changes would keep a stale
  service alive despite the eviction and needs a follow-up note if found.
- **Resume double-start**: `ResumeGlowAnimation` firing when timers are already running
  (e.g. restore-from-tray after a path that never paused) must not stack timers —
  `StartEffectAnimation` already routes through `Stop*` on re-selection, but the resume
  implementation should stop-then-start (or no-op when a timer is live) to make
  double-resume idempotent.
- **Tray restore on Home**: `ResumeGlowAnimation` must guard `Device is null` — restoring
  from tray while on Home is the common case.

## Open questions

- Is the tray pause/resume half of C2 wanted in this change, or should the fix ship with
  only the Back-navigation teardown (the audit's required criterion)? The plan includes it
  because the audit's stated harm is "CPU work continues even in tray" and the marginal
  cost is two one-line call sites; strike change-list items 4 (partially) and 5 to
  descope.

## Review findings

### Round 1 (2026-08-22) — Ship: yes

Scope reviewed: the uncommitted working-tree delta vs commit `e2641bc` on
`bugfixes/performance` (5 modified files + 1 new test file; earlier branch commits are
unrelated and out of scope). Build verified green; full suite 1760/1760 (baseline 1751 + 9
new tests).

#### Plan conformance

- Implemented as planned: all 7 File-level change list items — mouse-cache repro tests
  (items 1), glow-timer tests (2, as `DeviceDetailsGlowTimerTests.cs`), `ClearDeviceForExit`
  teardown (3), `PauseGlowAnimation`/`ResumeGlowAnimation` + `HasActiveGlowTimersForTests`
  seam (4), tray call sites (5), factory eviction + comment rewrite (6), no `docs/CONTEXT.md`
  term needed (7 — "glow" already in the glossary).
- Repro-first honored: the 3 mouse-cache tests were verified failing against the pre-fix
  factory (stash check); the glow tests cannot compile pre-fix (seam absent) — acceptable
  for a seam-based repro.
- Deviation (benign): one extra test beyond the plan's list —
  `ResumeGlowAnimation_RgbDisabled_IsNoOp` — covers the `!IsRgbEnabled` resume guard the
  plan's Risks section demands. Plan's list said "5 cases"; 6 shipped.
- Risk-note verification recorded: `DeviceService`, `DpiStageMonitor`, `TaipanStateMonitor`
  all call `CreateForDevice` per operation/poll (locals only, no instance held across list
  changes) — the plan's "long-lived captures" risk is confirmed absent.
- Sibling factories untouched, discovery singletons untouched, no NuGet/proto/installer
  changes — as planned.

#### Code

- [x] **S1. Second-instance restore path never resumes the glow animation** —
  `src/ProoveGaming.App/App.axaml.cs:168-190` (`InitializeSingleInstanceActivation`) —
  Should-fix
  **Problem:** the single-instance activation loop restores the window via its own inline
  `Dispatcher.UIThread.Post(...)` block instead of calling `RestoreFromTray()`. That block
  already drifted before this change (it never resets `SessionService.IsInTray`), and this
  change widens the drift: it does not call `ResumeGlowAnimation()` either.
  **Failure scenario:** open Device Details on an RGB device (glow animating) → close to
  tray (timers paused by this change) → launch the app EXE a second time. The second
  instance signals the activation event, the inline block shows/activates the window —
  Device Details is visible again with the glow frozen on its last brush until the user
  navigates Home and back. Acceptance criterion 5's "restored from the tray" fails on this
  restore path (the tray-icon and tray-menu paths are correct).
  **Fix:** replace the inline block's body with a call to `RestoreFromTray()` (it is already
  posted to the UI thread; `RestoreFromTray` does the same Show/Activate/tray-hide plus
  `IsInTray = false` plus the resume). Keep the `_mainWindow is null` guard. This also heals
  the pre-existing `IsInTray` drift for free. Alternative (minimal): add the one
  `ResumeGlowAnimation()` line to the inline block — but that leaves two restore
  implementations to keep in sync, which is how this gap appeared.

#### Tests

- [x] After fixing S1, no new automated test is expected (the activation loop needs the
  named-event + Avalonia lifetime, which the test host does not provide — same accepted gap
  as the rest of the `App.axaml.cs` tray wiring). Add QA step: restore via launching the app
  a second time while hidden on Device Details, expect the glow running.
- Coverage otherwise matches the plan's Test strategy; the deliberate exclusions (hardware
  replug, tray wiring, CPU measurement) stand.

#### Nits

- `_telinkCache` re-population after a clear is only asserted for the Easen path
  (`CreateForDevice_EasenMouse_AfterDeviceListChange_FreshServiceIsCachedAgain`); the Telink
  path's `_telinkCache[key] = service` line is exercised but its post-clear cache-hit is
  not. The plan's test list only required the Easen variant — fine to leave.
- `tests/ProoveGaming.Infrastructure.Tests/DeviceRgbServiceFactoryTests.cs:481` —
  `CreateMouseFactory` is a fine extraction; the two older keyboard invalidation tests could
  later be folded onto it, but not in this change's scope.

#### What's done well

- The factory eviction is exactly the keyboard mechanism extended — no new subscriptions, no
  locking added for the accepted in-flight race, and the stale `KNOWN PRE-EXISTING DEFECT`
  comments were rewritten rather than left to rot.
- `ResumeGlowAnimation` is genuinely idempotent (no-op when a timer is live), which the
  double-resume test pins — the plan's "resume double-start" risk is closed, not just noted.
- The glow tests use a Breathing-defaults card on purpose (Static would arm no timer and
  the tests would assert nothing) and the comment says so — future editors won't "simplify"
  it into a vacuous test.

#### Verdict

Ship: yes — no blockers; S1 is a real but narrow UX gap on one of three restore paths and
is a one-line-to-one-call fix. First: 1) fix S1 by routing the activation restore through
`RestoreFromTray()`; 2) add the second-instance restore to the QA steps.
