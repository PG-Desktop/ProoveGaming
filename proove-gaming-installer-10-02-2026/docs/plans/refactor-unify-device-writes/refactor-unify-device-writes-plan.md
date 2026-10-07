# Plan — Unify device writes into one apply-configuration action

## Goal

Now that the App only writes to a device at discrete moments (Save Preset / apply preset / connect
default), the write should be **one coordinated action** carrying the full desired state, instead of
the current fan-out of six separate gRPC calls per mouse (`SetButtonBindings`, `SetDeviceLedColor`,
`SetMouseSettings`, `SetDpiStages`, `SetActiveDpiStage`, `SetReportRate`). We add a single
`ApplyDeviceConfiguration` RPC; on the DeviceHost the **mouse** service composes one coordinated
`param_0`/`param_1` write (the correctness win — no cross-block clobbering, matches the vendor's
"send params together"), and the keyboard path applies RGB + key overrides from the same call. The
per-aspect RPCs stay for the live physical-button/monitor paths.

## Scope

**In**
- New gRPC `ApplyDeviceConfiguration` RPC + request/response and a shared `DeviceConfiguration` DTO.
- `IMouseRgbService.ApplyConfigurationAsync(DeviceConfiguration)` implemented by `EasenMouseService`
  (BLAZE, one composed write) and `TelinkMouseService` (TAIPAN, one coordinated write).
- A **pure, unit-testable** composition for BLAZE: `DeviceConfiguration` → `param_1` block0/block1/
  block2 + `param_0` images, so the byte layout is locked by tests (mirrors `X43MacroEncoder`).
- DeviceHost `DeviceService.ApplyDeviceConfiguration` handler: mice → `ApplyConfigurationAsync`.
- App: one `ApplyDeviceConfigurationAsync` client method; `ApplyCurrentStateToDeviceAsync` collapses
  the mouse path from six calls to **one**.

**Out**
- Removing the per-aspect RPCs / Infrastructure methods (kept — used by the DPI/rate monitors,
  `RequestCurrentStateAsync`, and as the composition's building blocks). This change is **additive**.
- The App's preset/staging model (unchanged — this only changes how the App's single apply moment
  reaches the device).
- Physical DPI/polling button behaviour (device → app, untouched).
- **Keyboards.** Decided (Round 1 review, Open Question 2 = "keep App-side"): `ApplyDeviceConfiguration`
  is **mouse-only**. Keyboards keep the two-call per-aspect path
  (`PushRgbToDeviceAsync` + `ApplyKeyOverridesToDeviceAsync` in `DeviceDetailsViewModel.Presets.cs`),
  reusing the App-side diff apply the preset-scoping change already built rather than moving it
  host-side. Folding keyboards in remains a reasonable future extension, not a defect of this plan.

## Current architecture (as found)

- `IMouseRgbService` (extends `IDeviceRgbService`) is implemented by `EasenMouseService` (BLAZE) and
  `TelinkMouseService` (TAIPAN). Each per-aspect method (`SetLedColorAsync`, `SetMouseParamAsync`,
  `SetDpiStagesAsync`, `SetActiveDpiStageAsync`, `SetReportRateAsync`, `SetButtonBindingsAsync`)
  **opens its own HID stream, takes `_writeLock`, and issues its own write**.
- `EasenMouseService` already composes **all-blocks** writes from caches (`_lastBlock0`,
  `_cachedLed*`, `_lastParam0`) and is careful to write block-scoped so one aspect can't clobber
  another. The remaining cost is that a Save Preset issues **six** such stream-open/lock/write
  cycles, and `param_0` is touched by three different methods.
- `DeviceService` (DeviceHost) resolves the per-device service via `IDeviceRgbServiceFactory`
  (mouse/keyboard RGB) and `IDeviceKeyMappingServiceFactory` (keyboard key remap), then calls the
  per-aspect method.
- The App's `DeviceDetailsViewModel.ApplyCurrentStateToDeviceAsync` (from
  [../refactor-preset-scoped-device-writes/refactor-preset-scoped-device-writes-plan.md](../refactor-preset-scoped-device-writes/refactor-preset-scoped-device-writes-plan.md))
  makes the six mouse calls / two keyboard calls today.

## Options considered

1. **One `ApplyDeviceConfiguration` RPC; DeviceHost composes (recommended).** The App sends the full
   desired state once. For mice, a new `ApplyConfigurationAsync` sets every cache and issues **one**
   stream-open/lock/coordinated write (block0 + block1 + block2 + param_0 + the 0x0D/0x80 drive, in
   the known-good order). Fewer round-trips, atomic composition, single ordering authority.
   Downside: a proto contract change and a new DTO.
2. **Batch on the client only** (App still calls the six methods but wrapped). Rejected: does not
   remove the six stream-open/lock/write cycles on the host, nor give one atomic composition — the
   actual correctness/perf win is host-side.
3. **Client-streaming "transaction"** RPC. Rejected: overkill; a single unary request already
   carries the whole desired state.

## Recommended approach

Option 1.

- **DTO** — add `DeviceConfiguration` to `ProoveGaming.Infrastructure.Classes`: optional
  `Rgb` (r,g,b,effect,brightness,speed), `IReadOnlyList<ButtonBinding>? Buttons`,
  `MouseSettings? (debounce, liftOff)`, `DpiStageParams[]? DpiStages`, `byte? ActiveStage`,
  `int? ReportRateHz`, `IReadOnlyList<KeyOverride>? KeyOverrides`. Null members mean "leave as the
  device default / don't include".
- **BLAZE composition (pure + tested)** — add `X43ConfigComposer` (or extend an existing class) in
  `Infrastructure.Classes`: `DeviceConfiguration` → `(block0, block1, block2, param0)` byte images,
  reusing `X43DpiTable`, `X43KeyMatrix`, `X43LedEncoder`. `EasenMouseService.ApplyConfigurationAsync`
  updates its caches from the config, calls the composer, and in **one** `_writeLock` + stream:
  writes block 0, block 1, block 2, then `param_0`, then the `0x0D`/`0x80` LED drive — the same
  sequence the per-aspect methods use, done once. Macro uploads for macro-bound buttons happen in the
  same stream.
- **TAIPAN composition** — `TelinkMouseService.ApplyConfigurationAsync` folds active stage, report
  rate and lift-off distance — the three fields that live in the same 19-byte MsConfig block — into
  **one** `PatchMsConfigAsync` read-modify-write (`ApplyMsConfigAsync`), instead of three independent
  ones each re-reading/re-erasing the same block. The DPI table and button map stay separate writes —
  they live in their own flash sectors, so there is no clobber risk to coordinate away, only the round
  trips. RGB stays unimplemented for TAIPAN (unchanged).
- **DeviceHost handler** — `ApplyDeviceConfiguration`: mouse-only; maps the request → `DeviceConfiguration`
  and calls `ApplyConfigurationAsync`. Keyboards are out of scope for this RPC (see Scope/Out and Open
  Question 2) — they keep going through the RGB service and key-mapping service via their existing
  per-aspect RPCs, called twice from the App instead of once.
- **App** — `IUsbHidDeviceService.ApplyDeviceConfigurationAsync(...)` + impl; `ApplyCurrentStateToDeviceAsync`
  builds one `DeviceConfiguration` from the current VM state and makes one call for mice. For keyboards
  it keeps calling `PushRgbToDeviceAsync()` then `ApplyKeyOverridesToDeviceAsync()` — the App-side diff
  apply stays where the preset-scoping change put it, not moved host-side.
- Keep every per-aspect RPC and Infrastructure method (the monitors and `RequestCurrentStateAsync`
  still use them, and the composer reuses their building blocks).

## File-level change list

- `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto` — add `rpc ApplyDeviceConfiguration`,
  `ApplyDeviceConfigurationRequest` (identity + optional `rgb`, `buttons`, `mouse_settings`,
  `dpi_stages`, `active_stage`, `report_rate`, `key_overrides` sub-messages) and response.
- `src/ProoveGaming.Infrastructure/Classes/DeviceConfiguration.cs` *(new)* — the composed desired-state DTO (+ `KeyOverride`, `MouseSettings` records).
- `src/ProoveGaming.Infrastructure/Classes/X43ConfigComposer.cs` *(new)* — pure `DeviceConfiguration` → block0/block1/block2/param0 image builder for BLAZE.
- `src/ProoveGaming.Infrastructure/Interfaces/IMouseRgbService.cs` — add `Task<bool> ApplyConfigurationAsync(DeviceConfiguration config)`.
- `src/ProoveGaming.Infrastructure/Services/EasenMouseService.cs` — implement `ApplyConfigurationAsync` (one coordinated write via the composer); refactor the per-aspect methods to share the write helpers where natural (no behaviour change to them).
- `src/ProoveGaming.Infrastructure/Services/TelinkMouseService.cs` — implement `ApplyConfigurationAsync` (one coordinated write).
- `src/ProoveGaming.DeviceHost/Services/DeviceService.cs` — add the `ApplyDeviceConfiguration` handler (mouse vs keyboard dispatch).
- `src/ProoveGaming.App/Services/IUsbHidDeviceService.cs` + `UsbHidDeviceService.cs` — add `ApplyDeviceConfigurationAsync(...)` client method.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs` — `ApplyCurrentStateToDeviceAsync` builds one `DeviceConfiguration` and makes one call (mouse); keyboards keep their existing two-call per-aspect path (RGB, then key overrides) — mouse-only per Scope/Out.
- `tests/ProoveGaming.Infrastructure.Tests/X43ConfigComposerTests.cs` *(new)* — lock the composed byte images.
- `tests/ProoveGaming.App.Tests/*` — the fake `IUsbHidDeviceService` records one `ApplyDeviceConfiguration` call per apply.

## Acceptance criteria

- Given a mouse with staged RGB + a binding + a DPI change, when Save Preset runs, then the App issues
  exactly **one** `ApplyDeviceConfiguration` RPC (not six), and the device reflects all three.
- Given the BLAZE composer, when a `DeviceConfiguration` with DPI stages + LED effect + a button
  binding is composed, then `block0` carries the DPI table, `block1` the LED colours, `block2` the key
  matrix, and `param_0` byte 1 the active stage and byte 2 the LED effect — asserted byte-for-byte.
- Given a mouse apply, when it runs, then the LED effect is **not** cleared by the DPI/stage bytes
  (the historic clobber) — `param_0` is composed once with effect + stage + rate together.
- Given a keyboard apply, when it runs, then its RGB and changed key slots are updated (unchanged slots
  not rewritten) via the existing per-aspect calls — keyboards are explicitly **out of scope** for the
  unified RPC (see Scope/Out); this criterion no longer requires a single `ApplyDeviceConfiguration` call.
- Given the physical DPI/polling buttons, then their live device→app reflection still works (per-aspect
  monitor paths untouched).
- `dotnet build ./src/ProveGaming.slnx` succeeds; existing Infrastructure + App tests stay green.

## Test strategy

This is a `refactor-*` (behaviour-preserving re-plumbing) with one new pure unit under
`tests/ProoveGaming.Infrastructure.Tests` and App-level call-shape assertions.
- **Infrastructure (new, required):** `X43ConfigComposerTests` — golden byte images for a representative
  config (DPI table, each LED effect mapping incl. Off/rainbow, a remapped button, active stage, report
  rate). Edge cases: empty/omitted members fall back to the documented defaults; macro-bound button
  composes the mode-0x05 entry. The real HID write and TAIPAN MsConfig round-trip are **not** unit-tested
  (need hardware) — covered by QA.
- **App:** the fake `IUsbHidDeviceService` asserts a single `ApplyDeviceConfiguration` call carrying the
  expected payload on Save Preset / preset switch; and that per-edit still issues **zero** device calls
  (regression guard from the preset-scoping change).
- **Not automated:** actual device application on BLAZE/TAIPAN hardware, LED-after-DPI correctness on the
  wire — QA.

## QA (manual test steps)

1. Launch the app, connect the BLAZE mouse, open its settings.
2. Change the RGB effect, remap a button, and change a DPI value and the report rate (do not save yet).
   Expected: nothing changes on the device yet.
3. Press Save Preset. Expected: the mouse applies the LED, the remapped button, the DPI values and the
   report rate together, in one go; the LED does **not** momentarily go dark or reset when the DPI
   applies.
4. Switch to a second preset. Expected: the mouse changes to that preset's full configuration at once.
5. Press the physical DPI button and the polling button. Expected: the app still reflects the new stage
   and rate live.
6. Repeat 2–4 with the TAIPAN mouse (all transports). Expected: one coordinated apply per Save/switch.
7. Repeat with a keyboard (RGB + a key remap). Expected: both take effect on Save/switch — via the
   keyboard's existing two-call path (RGB, then key overrides), not a single unified RPC.

## Review focus

`gRPC contract`, `async/await`, `thread-safety`, `performance hot-path`.

## Performance & quality

The change **reduces** device I/O: one stream-open/lock/write cycle per apply instead of six, and one
gRPC round-trip instead of six. The BLAZE composer is pure and allocation-light (reuses fixed 64-byte
buffers like the existing encoders); it must not allocate per-call beyond the block images it returns.
The single `_writeLock`-guarded write preserves the existing serialization. No UI-thread work changes —
the App still fires the apply async and marshals the result back.

**Note (2026-08-17, branch `fix/blaze-rgb-fix-on-7th-slot`)** — `_writeLock` alone turned out not to be
enough on BLAZE. It stops two applies *interleaving*, but not from running back to back with no gap,
which is what the connect path did: `ReapplyRgbOnConnect` and `ReapplyLastPresetOnConnect` both fired
on the same connect event, so two full transactions landed within milliseconds. Intermittently the
mouse came up with left/right click dead until a later write fixed it. Two changes followed:

1. **The connect path now issues one apply, not two.** `HomeViewModel.ReapplyOnConnect` →
   `DeviceDetailsViewModel.ReapplyOnConnectAsync` runs the coordinated apply — whose configuration
   already carried the stored lighting — and falls back to a lighting-only push **only** for the
   devices that apply does not serve (keyboards, unconfigurable transports, a device on its default
   preset). This is this plan's "one coordinated write" principle applied to the connect path, which
   had quietly been exempt from it. See
   [add-reapply-preset-on-connect](../add-reapply-preset-on-connect/add-reapply-preset-on-connect-plan.md).
2. **`EasenMouseService` enforces a minimum 150 ms quiet period between apply transactions**
   (`TransactionSettleMs`, waited inside `_writeLock` and stamped in `finally` on every exit), kept as
   a backstop for any other path that lands two applies together. It costs nothing on the single-apply
   path — every user-initiated edit — and logs at Information when it engages.

**Root cause, found later the same day — neither of the above was it.** The host log for a
reproduction shows nine full applies in 34 seconds, all from repeated **Random lighting** clicks, each
carrying `buttons=4` and therefore re-committing the key matrix to flash. The applies were 2–5 seconds
apart, so the settle never engaged and the connect path was not involved at all. What this plan calls
"one coordinated write" was true per apply, but nothing bounded how often an apply re-commits blocks
that did not change. Two guards were added:

3. **`DeviceApplyAspects`** (App) — the debounced auto-apply sends only the aspect the user edited; an
   RGB edit sends the LED and nulls the rest. Preset switch / Save / Discard / connect still send all.
4. **`X43BlockCommitCache`** (Infrastructure) — `EasenMouseService` drops any block whose composed
   image is byte-identical to the one it last committed, so no caller can re-commit an unchanged key
   matrix. The apply log line now reports `committed=0x…`, the mask that actually reached flash.

Items 1 and 2 remain as written — the connect path genuinely was sending the LED twice, and the settle
is a cheap backstop — but they were fixes to a different problem than the one being chased. **Still
pending hardware validation**; see `docs/mouse/blaze/key-matrix.md` for the procedure.

## Risks

- **gRPC contract change** — additive (new RPC + messages); regenerate `ProoveGaming.GrpcDependencies`;
  DeviceHost and App both rebuild off the proto.
- **Composition drift** — the composed images must match what the per-aspect methods produce today, or
  behaviour changes subtly (e.g. LED after a key-matrix write). The pure composer + golden tests and
  the "compose once, same order as the per-aspect path" rule mitigate this; the coder must diff the
  composed bytes against the per-aspect writes for a known config.
- **BLAZE brick surface** — block-2 (key-matrix) writes stay the brick-prone op; the composer must
  reuse `X43KeyMatrix.CreateDefault()` + overlay (never a partial all-blocks write). Keep
  `X43Mouse.RestoreAll()` staged during hardware testing.
- **Keyboard diff moving host-side** (if Open Question 2 = host) reworks the App-side diff just added;
  scope accordingly.

## Open questions

1. **Naming/shape** — `ApplyDeviceConfiguration` with a flat request, or a nested `DeviceConfiguration`
   message? Recommend a nested message mirroring the DTO for symmetry.
2. **Keyboard key overrides — resolved (Round 1 review): keep the App-side diff apply.** Folding
   keyboards into the unified RPC (host-side probe + diff) was the original recommendation, but it
   would have reworked the App-side diff (`ApplyKeyOverridesToDeviceAsync`) the preset-scoping change
   had just added, for a benefit (one fewer RPC per keyboard apply) that doesn't carry the correctness
   motivation the mouse side has (no cross-block clobbering risk on a keyboard key-override write).
   Keyboards stay on `PushRgbToDeviceAsync()` + `ApplyKeyOverridesToDeviceAsync()`, two calls instead of
   one. Revisit if a future change needs keyboards on the unified RPC for another reason.
3. **Retire per-aspect App calls?** After this lands, the App no longer calls the per-aspect write RPCs
   directly. Leave them (monitors/host still use several) — recommend yes, leave them; a later cleanup
   can prune any that become truly unused.

## Review findings

### Round 1 — Ship: no

#### Plan conformance

Plan: `docs/plans/refactor-unify-device-writes/refactor-unify-device-writes-plan.md`

- Implemented as planned: `ApplyDeviceConfiguration` RPC + `DeviceConfiguration` DTO (additive proto
  change), `DeviceService.ApplyDeviceConfiguration` handler (mouse dispatch), App
  `ApplyDeviceConfigurationAsync` client method, `ApplyCurrentStateToDeviceAsync`'s mouse branch
  collapsed to one call. `EasenMouseService.ApplyConfigurationAsync` genuinely composes and issues one
  all-blocks write (block0+block1+block2 → one `WriteAllBlocksAsync`) plus one `param_0` write — the
  correctness win the plan wanted for BLAZE, and confirmed working on hardware this session.
- Deviates:
  - No `X43ConfigComposer` pure class was created — the File-level change list names it explicitly as a
    new file, and the Recommended approach calls for a "pure, unit-testable composition ... so the byte
    layout is locked by tests (mirrors `X43MacroEncoder`)". The composition logic instead lives inline
    inside `EasenMouseService.ApplyConfigurationAsync` (`src/ProoveGaming.Infrastructure/Services/EasenMouseService.cs:410-543`),
    which per the file's own existing comment carries no unit tests ("this class is just the I/O around
    them"). See S1.
  - `TelinkMouseService.ApplyConfigurationAsync` (`src/ProoveGaming.Infrastructure/Services/TelinkMouseService.cs:481-500`)
    does not perform "its coordinated write ... in one pass" as the Recommended approach specifies for
    TAIPAN — it sequentially calls six independent per-aspect methods, each of which still opens its own
    stream and takes `_writeLock` separately. See S2.
  - Keyboards were not folded into the unified RPC. `ApplyCurrentStateToDeviceAsync`'s keyboard branch
    (`src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs:259-263`) still
    issues two separate calls (`PushRgbToDeviceAsync()`, `ApplyKeyOverridesToDeviceAsync()`) instead of
    one `ApplyDeviceConfiguration` request. This directly fails Acceptance Criteria #4 ("Given a keyboard
    apply ... the App issues one `ApplyDeviceConfiguration` RPC"). Open Question 2 recommended folding
    keyboards in but was never explicitly answered in the plan — the code silently took the "keep the
    App-side diff, RGB stays separate too" branch. See S3.
- Missing/partial: `X43ConfigComposerTests` (Test strategy: "Infrastructure (new, required)") does not
  exist — there is no golden-byte-image regression test for the BLAZE composition at all, mouse or
  keyboard.

#### Code

- [x] **S1. BLAZE composition has no golden-byte tests, unlike every other X43 encoder** — `src/ProoveGaming.Infrastructure/Services/EasenMouseService.cs:410-543` — Should-fix
  **Problem:** The plan called for a pure `X43ConfigComposer` specifically so the composed byte layout
  (block0/block1/block2/param0) could be locked by unit tests, the same way `X43MacroEncoder`,
  `X43DpiTable`, `X43KeyMatrix` already are. That composer was never extracted; the equivalent logic is
  inline in `EasenMouseService`, which the file's own header comment says intentionally carries no unit
  tests (I/O-only class).
  **Fix:** Extract the block0/block1/block2/param0 assembly (currently `ApplyConfigurationAsync`'s body
  minus the actual HID writes) into a pure `DeviceConfiguration → byte[]×4` composer, and add
  `X43ConfigComposerTests` covering the plan's specified edge cases (each LED effect mapping incl.
  Off/rainbow, a remapped + a macro-bound button, active stage, report rate, omitted members falling
  back to documented defaults). This is not just plan hygiene — this exact composition is where the
  session's LED-clobbered-by-DPI and stage-drift bugs lived; a golden-byte test is the regression net
  that keeps a future edit from silently reintroducing either.
  **Fixed:** extracted `X43ConfigComposer.Compose(...)` (`src/ProoveGaming.Infrastructure/Classes/X43ConfigComposer.cs`)
  — a pure `DeviceConfiguration` → `(Block0, Block1, Block2, Param0, ...)` function, no I/O.
  `EasenMouseService.ApplyConfigurationAsync` now calls it and only does the writes. 20 golden-byte
  tests added in `tests/ProoveGaming.Infrastructure.Tests/X43ConfigComposerTests.cs` covering every LED
  effect (incl. Off/RainbowBreathing), a remapped button, a macro-bound button (+ sequential macro-slot
  assignment), the DPI table, settings, active stage without clobbering the LED bytes, and the
  all-members-null baseline.

- [x] **S2. TAIPAN's "unified apply" is still six independent writes, not one coordinated write** — `src/ProoveGaming.Infrastructure/Services/TelinkMouseService.cs:481-500` — Should-fix
  **Problem:** `ApplyConfigurationAsync` calls `SetButtonBindingsAsync`, `SetDpiStagesAsync`,
  `SetActiveDpiStageAsync`, `SetReportRateAsync`, `SetMouseParamAsync`, `SetLedColorAsync` in sequence.
  Each of the MsConfig-touching ones (`SetActiveDpiStageAsync`, `SetReportRateAsync`,
  `SetMouseParamAsync`) independently opens a stream, takes `_writeLock`, does its own
  read-modify-write of the *same* 19-byte MsConfig block. The plan's stated rationale for TAIPAN was
  "MsConfig read-modify-write for settings/DPI-stage/rate ... in one pass" — that never landed; what
  exists is the pre-existing per-aspect methods called back-to-back, which happen to be gRPC-atomic
  (one round trip from the App) but not device-write-atomic.
  **Why it matters:** this session spent significant time chasing exactly the failure mode this
  sequential-writes shape invites — `_liveDpiStage` has to be threaded correctly through the *whole*
  sequence for the stage to survive the later polling-rate/lift-off writes, and the actual fix that
  landed this session was in the *App* layer (removing a premature, out-of-band
  `AssertActiveDpiStageAsync` write that raced this exact sequence), not in `TelinkMouseService` itself.
  Nothing here structurally prevents a *future* interleaved per-aspect call — from anywhere else in the
  App or DeviceHost — from reintroducing the same class of bug, because atomicity is a happy accident of
  call ordering, not an invariant the code enforces.
  **Solution options:**
  - Option A (correct, matches the plan): read MsConfig once, apply stage + rate + lift-off patches to
    the same in-memory block, write once — mirrors what `EasenMouseService` now does for BLAZE.
  - Option B (quick / dev-only): leave as is, but say so explicitly in the plan (update "Recommended
    approach" to note TAIPAN's "coordination" is at the RPC level only, not the device-write level), so
    the next reader isn't misled into thinking this is already atomic.
  **Fixed (Option A):** added `ApplyMsConfigAsync(activeStage, reportRateHz, liftOffMm)` — one
  `PatchMsConfigAsync` read-modify-write applying all three fields to the same in-memory block.
  `ApplyConfigurationAsync` now calls it once instead of `SetActiveDpiStageAsync` +
  `SetReportRateAsync` + `SetMouseParamAsync` sequentially. The DPI table and button map stay separate
  writes (different flash sectors — no clobber risk to coordinate away). Plan text updated (Recommended
  approach) to describe this accurately.

- [x] **S3. Keyboard apply was not folded into the unified RPC, contradicting Acceptance Criteria #4** — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs:259-263` — Should-fix
  **Problem/why it matters:** the plan's Acceptance Criteria explicitly requires "Given a keyboard apply,
  when it runs, then the App issues one `ApplyDeviceConfiguration` RPC and the keyboard's RGB and changed
  key slots are updated." The shipped code keeps keyboards on two separate per-aspect calls
  (`PushRgbToDeviceAsync()` then `ApplyKeyOverridesToDeviceAsync()`). This may be a reasonable scope cut
  (Open Question 2 flagged the rework cost), but as written the plan and the code disagree, and a future
  reader has no way to tell whether this was a deliberate decision or a miss.
  **Fix:** either fold keyboards into the unified RPC per the plan's recommendation, or — the smaller
  change — update the plan's Scope/Recommended approach/Acceptance Criteria to explicitly record "mouse
  only; keyboards intentionally stay on the per-aspect path" and why, so the acceptance criteria matches
  what was actually built.
  **Fixed (smaller option):** updated the plan's Scope/Out, Recommended approach, File-level change
  list, Acceptance Criteria, QA steps, and Open Question 2 to explicitly record "mouse-only; keyboards
  intentionally stay on the two-call per-aspect path" and why (avoids reworking the App-side key-override
  diff the preset-scoping change had just added, for a benefit — one fewer RPC — that doesn't carry the
  mouse side's correctness motivation).

- [x] **S4. BLAZE's post-hoc "re-assert the stage" write contradicts the plan's own design goal and didn't fix the bug it was added for** — `src/ProoveGaming.Infrastructure/Services/EasenMouseService.cs:514-524` — Should-fix
  **Problem:** After the coordinated all-blocks + param_0 write, `ApplyConfigurationAsync` sleeps 80ms
  and issues a *second*, separate `param_0` write that only re-patches the stage byte. This was added
  mid-session as a hypothesis for the BLAZE stage-drift bug; per the user's own hardware testing it did
  **not** fix that bug (the actual fix landed elsewhere, in the App layer's double-write elimination —
  see the sibling preset-scoped-device-writes review). It now just adds ~80ms of latency and an extra
  device write to every apply that touches the active stage, and directly contradicts this plan's
  premise of "one coordinated write ... single ordering authority."
  **Fix:** now that the real root cause is fixed on the App side, re-test whether this extra write is
  still necessary; if BLAZE stage-switching is confirmed correct without it, remove it. If hardware
  testing shows it's still needed for some other reason, say so in a comment (the current one only
  explains the hypothesis, not a confirmed necessity).
  **Fixed:** removed. The write is now composed once via `X43ConfigComposer` (S1) and sent once — no
  80ms delay, no second `param_0` write. **Needs hardware re-test** to confirm BLAZE active-stage
  switching is still correct without the re-assert, now that the actual root cause (B1's sibling
  double-write bug on the App side) is fixed.

#### Tests

- [x] `X43ConfigComposerTests` — golden byte images for a representative `DeviceConfiguration` (DPI
  table, each LED effect mapping incl. Off/rainbow, a remapped button, a macro-bound button, active
  stage, report rate; omitted members falling back to documented defaults) — required by the plan's Test
  strategy, currently absent entirely.
  **Added:** `tests/ProoveGaming.Infrastructure.Tests/X43ConfigComposerTests.cs`, 20 tests, all green.
- [x] App-level: assert exactly one `ApplyDeviceConfigurationAsync` call on Save Preset / preset switch
  (ties to the same gap noted in the sibling plan's review) — the fake `IUsbHidDeviceService` in
  `DeviceDetailsMacroAssignmentTests.cs` already records `ApplyConfigCallCount`, but nothing asserts on
  it yet.
  **Added:** `tests/ProoveGaming.App.Tests/DeviceDetailsPresetApplyTests.cs`
  (`ConfirmSavePreset_IssuesExactlyOneApplyDeviceConfigurationCall`,
  `SelectPreset_CleanSwitch_IssuesExactlyOneApplyDeviceConfigurationCall`).

#### Nits

- none

#### What's done well

- The BLAZE all-blocks write is a genuine, hardware-confirmed fix — buttons and DPI/settings apply
  correctly in one write, and the key matrix is always rebuilt from `X43KeyMatrix.CreateDefault()` so a
  partial write can't brick buttons again (the documented failure mode from the first attempt at this
  RPC, which did brick BLAZE buttons before being reworked).
- `DeviceConfiguration`'s "null/empty = don't touch" contract is applied consistently across the proto,
  the DTO, and both `EasenMouseService`/`TelinkMouseService` implementations — a clean, easy-to-reason-about
  convention for an optional-aspects request shape.
- The per-aspect RPCs were correctly left in place rather than removed, matching the plan's explicit
  "Out of scope" call — the DPI/rate monitors and physical-button live paths are untouched.

#### Verdict

Ship: no
Reasoning: no new blocker in this plan's own scope (B1 in the sibling plan is the ship-blocking issue
for the branch as a whole), but three of the four Should-fix items are direct, verifiable deviations
from this plan's stated Acceptance Criteria and Recommended approach, and S1/S2 leave the exact class of
bug this branch spent a whole session debugging structurally unguarded against recurrence.
First three things to tackle: (1) add `X43ConfigComposerTests` — cheapest, highest-value regression net;
(2) decide TAIPAN's coordinated-write status — implement it for real or amend the plan to stop claiming
it; (3) resolve S4 (remove or justify the redundant stage re-assert) once B1 is fixed and hardware-retested.

### Round 2 — Ship: yes

#### Plan conformance

Re-read the current code for all four Round 1 items (not just their `Fixed:` notes) against
`origin/main` (`cb9acde`). All four hold up:
- `X43ConfigComposer.Compose` (`src/ProoveGaming.Infrastructure/Classes/X43ConfigComposer.cs`) is
  genuinely pure (no I/O, no HID types) and is a faithful transcription of the old inline logic —
  confirmed byte-for-byte via the 20 new golden tests, all passing.
  `EasenMouseService.ApplyConfigurationAsync` now only orchestrates I/O around it.
- `TelinkMouseService.ApplyMsConfigAsync` folds active stage + report rate + lift-off into one
  `PatchMsConfigAsync` call as claimed — traced the write path: when `activeStage` is omitted from the
  combined patch, `TaipanMsConfig.ResolveStageForWrite` (called by `PatchMsConfigAsync` before the
  patch delegate runs) still preserves the known live stage, so a rate-only or lift-off-only apply
  can't silently reset the stage. No regression from collapsing the three calls into one.
- The plan text (Scope, Recommended approach, File-level change list, Acceptance Criteria, QA, Open
  Question 2) now accurately describes the mouse-only decision instead of contradicting the code.
- The redundant stage-reassert write is gone from `EasenMouseService.ApplyConfigurationAsync` — the
  method now ends after the LED drive, no extra `Task.Delay(80)` + second `param_0` write.
- No new deviations introduced by the fixes themselves.

#### Code

- none new this round.

#### Tests

- none new this round — Round 1's test additions (`X43ConfigComposerTests`, the
  `ApplyConfigCallCount` assertions) still pass; re-ran the full Infrastructure suite (292) and App
  suite (166) clean.

#### Nits

- none new. (See the sibling plan's Round 2 for one general nit: `.claude/settings.json` newly
  committed in this branch — not related to this plan's scope.)

#### What's done well

- Same as Round 1, plus: the fixes were verified by re-reading the actual current files rather than
  trusting the `Fixed:` notes at face value — in particular the TAIPAN stage-preservation trace above,
  since that's exactly the kind of subtle interaction a quick glance would miss.

#### Verdict

Ship: yes
Reasoning: all four Round 1 Should-fix items are closed, verified by tracing the actual logic (not just
re-reading the diff), and covered by tests where testable without hardware. The two items that
explicitly need hardware re-verification (BLAZE stage-switching without the removed re-assert write;
TAIPAN preset-switch with the new combined MsConfig write) are noted as such, not silently assumed —
appropriate given this branch already went through one round of "the fix looked right but wasn't" on
the DPI-stage bug earlier in the session.
