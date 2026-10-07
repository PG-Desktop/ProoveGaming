# add-per-key-actuation — Per-key actuation on top of the global editor

## Goal

Extend the Actuation section (shipped in
[`../add-keyboard-actuation/add-keyboard-actuation-plan.md`](../add-keyboard-actuation/add-keyboard-actuation-plan.md),
PR #84) so the user can assign the actuation parameters — press point, release point, Rapid
Trigger on/off, Quick Press and Quick Release — not only to **all keys** (today's behaviour) but
also to a **selection of individual keys**. Both device families already store every one of these
values per key (Smart Infinite: one travel record per slot; T800: per-slot trip options and a
per-slot RT bit in the key mode), so this is an exposure of existing hardware capability, not a
protocol invention. Decisions taken with the user: an **All keys / Per-key mode toggle** at the
top of the section, **multi-select** of keys on the interactive keyboard overlay, and **all five
parameters** assignable per key (RT included).

## Scope

**In:**
- A two-mode Actuation section: **All keys** (identical to today) and **Per-key**.
- Per-key mode renders the existing interactive keyboard overlay
  (`RasterKeyboardOverlayView`) in the device image area; clicking a key toggles it in/out of
  the selection (multi-select); the sliders + RT toggle edit every selected key at once through
  the same 400 ms debounced latest-wins write path.
- Per-key read: the App learns each key's current values on section load (one device read — the
  existing `GetKeyActuation` response is extended with a per-slot map).
- Per-key write: a new `SetPerKeyActuation` RPC carrying the target slots + one set of values;
  family services patch **only** those slots via read-modify-write and verify.
- All three boards: T800, Cyrex, Aress.
- Safe degradation: an older DeviceHost (empty per-key map / unimplemented RPC) hides the
  Per-key mode; the All keys mode keeps working unchanged.

**Out (explicitly not in this change):**
- Per-key value badges or a colour map painted on the keycaps (the overlay shows selection
  highlight only; a "customised keys" visualisation is possible follow-up work).
- Named per-key profiles, preset (SQLite) capture of actuation — flash stays the only truth,
  as decided in the parent plan.
- Dead zones, calibration, advanced key modes (DKS/MT/TGL/Snap) — still untouched and still
  preserved byte-for-byte on write.
- Any change to the All-keys write semantics: applying in All-keys mode still makes the board
  uniform (documented behaviour), with no warning dialog.

## Options considered

**A. Extend the existing read RPC + one new per-key write RPC (recommended).**
`GetKeyActuationResponse` gains `map<int32, KeyActuationValues> per_key_values` (slot → values),
filled from the same single device read the RPC already performs (both services already read the
whole table to reduce it — the map is free). Writes go through a **new** `SetPerKeyActuation`
RPC (slots + values). *Trade-offs:* the response grows, and per-key state rides on a call whose
name says "global". *Why it wins:* zero extra device round-trips on the read path, and the write
being a **separate RPC** is a safety property — an older DeviceHost answers it with
`UNIMPLEMENTED` instead of silently reinterpreting a widened `SetKeyActuation` as "write all
keys" and flattening the user's per-key setup.

**B. Add `repeated int32 slots` to the existing `SetKeyActuationRequest` (empty = all keys).**
Fewest proto changes. Rejected for exactly the degradation case above: proto3 silently drops
unknown fields, so a new App talking to an old DeviceHost would send slots, have them ignored,
and every key on the board would be overwritten — the worst possible failure mode of this
feature, undetectable by the App.

**C. Dedicated `GetPerKeyActuation` read RPC.**
Cleanest naming, but each call re-reads the entire travel area (~15 packets on Smart Infinite) or
five trip tables (T800) that `GetKeyActuation` has just read anyway on section open. Two reads
per section visit for no informational gain. Rejected.

**UI selection plumbing decision (applies to A):** per-key selection reuses the existing
`PhysicalKeys` collection, overlay view and `SelectPhysicalKey` command, branching on
"Actuation section + Per-key mode" to *toggle* `IsSelected` instead of radio-selecting. The
alternative — a parallel keys collection and a second overlay code path — duplicates the
per-board layout logic (the exact drift risk S2 of the parent plan was about) and is rejected.

## Recommended approach

Option A. Flow:

1. **Section load (App)** — `GetKeyActuationAsync` now also returns the per-slot map. The VM
   keeps it as `_perKeyActuation` (slot → values). `IsActuationPerKeyAvailable` is true only
   when the map is non-empty **and** the board has an interactive overlay — an old DeviceHost
   returns an empty map, so the mode toggle simply doesn't render and nothing else changes.
2. **Mode toggle** — `ActuationPerKeyMode : bool` on the VM. All-keys mode is the existing UI
   untouched. Entering Per-key mode clears the selection, shows the keyboard overlay in the
   device image area (`ShowKeyboardOverlayInActuation`, same pattern as
   `ShowKeyboardOverlayInMacros`) and swaps the static/layered keyboard image out while it is
   visible. Leaving the section or switching device resets the mode to All keys.
3. **Key click (Per-key mode)** — `SelectPhysicalKey` branches: instead of radio-select it
   toggles `option.IsSelected` and adds/removes the key's `MatrixIndex` in
   `_actuationSelectedSlots` (a `HashSet<byte>`). The sliders then display the values of the
   **most recently clicked selected key** (from `_perKeyActuation`); with an empty selection the
   sliders are disabled with a localized "select keys on the keyboard" hint.
4. **Edit (Per-key mode)** — the same slider entry points and RT toggle feed the same debounce
   (`ScheduleActuationApply`). The apply branches on mode: Per-key calls the new
   `SetPerKeyActuationAsync(vid, pid, slots, values)` with a snapshot of the selection; success
   updates `_perKeyActuation` for those slots locally; failure raises the existing
   `ActuationWriteFailed` banner and re-reads from the device (existing pattern).
5. **DeviceHost / services** — `IKeyboardActuationService` gains
   `WriteKeysAsync(IReadOnlyCollection<int> slots, KeyboardActuationValues values, ...)`, and
   `KeyboardActuationState` gains `IReadOnlyDictionary<int, KeyboardActuationValues> PerKeyValues`:
   - **Smart Infinite (Cyrex/Aress):** read-validated area (existing geometry gate unchanged) →
     new codec method `SmartInfiniteTravelArea.PatchSlots(area, recordBytes, slots, …)` patches
     only the listed slots (same field masking/preservation as `PatchAllSlots`) → whole-area
     write (the protocol writes the area as a unit regardless) → read back → verify the patched
     slots carry the new values **and** every unpatched slot is byte-identical to the pre-write
     read. Slots outside `0..TravelSlots-1` are ignored (logged), never written.
   - **T800 (RY5088):** the four two-byte option tables are now **read-modify-write** for a
     per-key apply (the global path's `Fill` remains for All keys): read each table (abort on a
     partial read, same rule as key modes today) → patch selected slots → write **only the pages
     that contain a patched slot** (`Ry5088Protocol.TripPagesContaining(slots, keysPerPage)`,
     a new pure helper) — fewer packets and no needless flash traffic; the key-mode sweep
     patches bit 7 on selected slots only; `FlashEnable` rides on the final written packet.
     Verify re-reads the press table and compares the selected slots.
6. **Slot identity** — the UI key's `MatrixIndex` (runtime-probed where applicable) **is** the
   actuation slot on both families: Smart Infinite travel records and the key matrix share slot
   numbering, and T800 trip tables use the same 128-slot space as its key matrix. This is an
   assumption inherited from the vendor apps' behaviour, not yet hardware-verified — QA step 3
   is the explicit first-contact check for it.

## File-level change list

Infrastructure:

- `src/ProoveGaming.Infrastructure/Classes/KeyboardActuation.cs` — edit: add
  `PerKeyValues` (`IReadOnlyDictionary<int, KeyboardActuationValues>`) to
  `KeyboardActuationState` (empty dictionary default keeps existing constructions compiling).
- `src/ProoveGaming.Infrastructure/Interfaces/IKeyboardActuationService.cs` — edit: add
  `WriteKeysAsync(IReadOnlyCollection<int> slots, KeyboardActuationValues values, CancellationToken)`.
- `src/ProoveGaming.Infrastructure/Classes/SmartInfiniteTravelArea.cs` — edit: add
  `PatchSlots(...)` (selective-slot variant of `PatchAllSlots`, same preservation guarantees)
  and `ReadAllSlots(...)` (slot → values decode used to build the per-key map).
- `src/ProoveGaming.Infrastructure/Classes/Ry5088Protocol.cs` — edit: add pure
  `TripPagesContaining(IReadOnlyCollection<int> slots, int keysPerPage)` helper returning the
  ordered set of page indices a per-key write must touch.
- `src/ProoveGaming.Infrastructure/Services/SmartInfiniteActuationService.cs` — edit: build
  `PerKeyValues` in `ReadOnce`; implement `WriteKeysAsync` (patch-selected-slots
  read-modify-write with the unpatched-slots-unchanged verify).
- `src/ProoveGaming.Infrastructure/Services/T800KeyboardActuationService.cs` — edit: build
  `PerKeyValues` in `ReadOnce` (populated slots only); implement `WriteKeysAsync`
  (option-table RMW, page-selective writes, FlashEnable on last packet, selected-slot verify).

gRPC contract:

- `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto` — edit: add
  `map<int32, KeyActuationValues> per_key_values = 10` to `GetKeyActuationResponse`
  (slot-keyed, comment stating an empty map means "host predates per-key");
  add `rpc SetPerKeyActuation` with `SetPerKeyActuationRequest` (vid/pid,
  `repeated int32 slots`, `KeyActuationValues values`) and `SetPerKeyActuationResponse`
  (success). Unit convention comments unchanged (0.01 mm).

DeviceHost:

- `src/ProoveGaming.DeviceHost/Services/DeviceService.cs` — edit: fill `PerKeyValues` in
  `GetKeyActuation`; implement `SetPerKeyActuation` (route through
  `IDeviceActuationServiceFactory`, reject empty slot lists with `success=false`).

App:

- `src/ProoveGaming.App/Services/IUsbHidDeviceService.cs` — edit: `KeyActuationInfo` gains
  `IReadOnlyDictionary<int, KeyActuationKeyValues>` (a small record mirroring the five fields);
  add `SetPerKeyActuationAsync(vid, pid, IReadOnlyCollection<int> slots, press, release, rt,
  quickPress, quickRelease)`.
- `src/ProoveGaming.App/Services/UsbHidDeviceService.cs` — edit: map the proto per-key map into
  `KeyActuationInfo`; implement the new client call (existing deadline/`NotifyError` pattern).
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Actuation.cs` — edit:
  `ActuationPerKeyMode`, `IsActuationPerKeyAvailable`, `_perKeyActuation` cache,
  `_actuationSelectedSlots` + last-clicked tracking, selection-aware slider/RT state
  (`HasActuationSelection`, `ActuationSelectedKeysLabel`), mode reset on device/section change,
  per-key branch in the debounced apply (snapshot the selection before the await), local cache
  update on success / re-read + banner on failure.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` —
  edit: `SelectPhysicalKey` branches to toggle-select when Actuation Per-key mode is active;
  add `ShowKeyboardOverlayInActuation`; extend the image-selection flags
  (`ShowLayeredKeyboardImage` / `ShowFlatKeyboardImage`) so the static art hides while the
  actuation overlay is up; clear actuation selection when the layout reloads.
- `src/ProoveGaming.App/Views/DeviceDetails/DeviceDetailsView.axaml` — edit: render
  `RasterKeyboardOverlayView` in the device image area when `ShowKeyboardOverlayInActuation`
  (same instance pattern as the Macros one).
  *(2026-08-17, PG-589 item 2: the All-keys mode's static layered image now renders UNLIT —
  `ShowBacklight="{Binding !IsActuationSection}"` on the details-page `RgbKeyboardView` —
  matching the unlit board Key Assignments / Macros / Per-key mode already show. The
  Actuation page is about switch travel, not lighting.)*
- `src/ProoveGaming.App/Views/DeviceDetails/Sections/ActuationSectionView.axaml` (+ `.axaml.cs`)
  — edit: mode toggle (two buttons, existing tab/toggle styling — grep for an existing
  two-state selector style before writing one), selected-keys label, empty-selection hint,
  sliders/RT disabled without a selection in Per-key mode.
- `src/ProoveGaming.App/Resources/Strings.en.axaml` / `Strings.uk.axaml` — edit: mode labels
  (All keys / Per-key), "Selected: …" label format, "Select keys on the keyboard" hint,
  All-keys "applies to every key" subtitle if the design wants it.

Docs:

- `docs/CONTEXT.md` — edit: add **Per-key actuation** one-liner.

Tests (new feature — required coverage, see Test strategy):

- `tests/ProoveGaming.Infrastructure.Tests/SmartInfiniteTravelAreaTests.cs` — edit: `PatchSlots`
  and `ReadAllSlots` cases.
- `tests/ProoveGaming.Infrastructure.Tests/Ry5088ProtocolTripTests.cs` — edit:
  `TripPagesContaining` cases.
- `tests/ProoveGaming.Infrastructure.Tests/SmartInfiniteActuationServiceTests.cs` — **new**
  (scripted `ISmartInfiniteIo` fake, mirroring `SmartInfiniteKeyMappingServiceTests`): per-key
  write patches only requested slots; verify-mismatch → Rejected; unpatched-slot corruption
  detected.
- `tests/ProoveGaming.App.Tests/DeviceDetailsActuationViewModelTests.cs` — edit: per-key mode
  cases.

## Acceptance criteria

- [ ] Given a connected Cyrex/Aress/T800 on a current DeviceHost → open Actuation → an
  **All keys / Per-key** toggle is visible; All keys mode looks and behaves exactly as before
  this change (sliders, RT, defaults button, write-failed banner).
- [ ] Given Per-key mode is entered → the device image area shows the clickable keyboard
  overlay; clicking W, A, S, D highlights all four; clicking W again un-highlights only W.
- [ ] Given no keys are selected in Per-key mode → sliders and RT toggle are disabled and a
  localized hint says to select keys; nothing is written to the device.
- [ ] Given W+A+S+D selected and the press slider set to 1.2 mm → within ~1 s exactly one
  per-key write reaches the device targeting exactly those four slots; re-reading (section
  re-open or replug) shows 1.2 mm on WASD and the previous value on every other key.
- [ ] Given RT is enabled for a selection → only the selected keys gain the RT flag
  (Smart Infinite QUICKLY_DOWN flag bits / T800 key-mode bit 7); unselected keys' RT state and
  T800 advanced key modes (DKS/MT/TGL/Snap bytes) are byte-identical before/after — covered by
  codec/service tests.
- [ ] Given per-key edits exist and the user switches to All keys and applies → the whole board
  becomes uniform (existing documented behaviour, now also reflected in the per-key map on the
  next read).
- [ ] Given an App with this change talks to an **older DeviceHost** → the per-key map is empty,
  the mode toggle is absent, All-keys editing still works, and `SetPerKeyActuation` is never
  called.
- [ ] Given a per-key write fails or the board rejects it (verify mismatch) → the amber
  write-failed banner appears and the sliders/selection values re-read from the device — no
  silent divergence between UI and board.
- [ ] Given a slider is dragged for 3 s with a selection held → no more than ~1 write per
  debounce window (no RPC storm), and the UI stays smooth (no blocking on the UI thread).
- [ ] `dotnet build ./src/ProveGaming.slnx` and `dotnet test ./src/ProveGaming.slnx` pass.

## Test strategy

New feature (`add-*`) — required coverage, all hardware-free:

`tests/ProoveGaming.Infrastructure.Tests/SmartInfiniteTravelAreaTests.cs` (extend):
- `PatchSlots` writes the four fields + RT flag on exactly the listed slots; every other slot,
  and every unmodelled byte (axis nibble, dead zones) of patched slots, byte-identical.
- `PatchSlots` with out-of-range slot indices: ignored, area otherwise correct.
- `ReadAllSlots` round-trips a hand-built mixed table (10- and 12-byte records).

`tests/ProoveGaming.Infrastructure.Tests/Ry5088ProtocolTripTests.cs` (extend):
- `TripPagesContaining`: single slot → one page; slots straddling a page boundary (27, 28 at
  28/page) → two pages; same slots at 56/page → one page; empty → empty; order and dedup.

`tests/ProoveGaming.Infrastructure.Tests/SmartInfiniteActuationServiceTests.cs` (new, scripted
`ISmartInfiniteIo` fake in the style of `SmartInfiniteKeyMappingServiceTests`):
- `WriteKeysAsync` writes an area where only requested slots changed and verifies Verified.
- Fake firmware answering different bytes for a patched slot → Rejected, no retry storm.
- Read builds `PerKeyValues` keyed by slot with correct step conversion.

`tests/ProoveGaming.App.Tests/DeviceDetailsActuationViewModelTests.cs` (extend):
- Per-key mode available only when the info's per-key map is non-empty (old-host degradation).
- Toggle-select semantics: clicking selected key deselects it; selection survives slider edits;
  cleared on mode exit and device change.
- Empty selection gates the apply: slider entry points with no selection never call the service.
- Debounced per-key apply: N rapid edits → one `SetPerKeyActuationAsync` with the last values
  and the slot snapshot; success updates the local per-key cache; failure sets
  `ActuationWriteFailed` and triggers a re-read.
- Sliders display the last-clicked key's values from the per-key map.

**Deliberately not automated:** real HID traffic (the slot-identity assumption between matrix
slots and travel/trip slots is verified physically in QA step 3), the T800 page-selective flash
behaviour on real firmware, and overlay hit-testing/visuals (UI-thread/rendering) — manual QA.

## QA (manual test steps)

1. Connect the PG Cyrex by cable, open its page, open the Actuation section. **Expected:** two
   mode buttons appear at the top — "All keys" and "Per-key"; "All keys" is active and the
   section works exactly as it did before this update.
2. Switch to "Per-key". **Expected:** the big keyboard picture becomes clickable; the sliders
   are greyed out with a hint telling you to select keys.
3. Click only the **Q** key, set "Initial Actuation Point" to **0.5 mm**, wait a second. Then
   type Q with a feather-light touch, and type W the same way. **Expected:** Q registers from a
   very light touch; W still needs the old (deeper) press. *(This step is the critical check
   that the right physical key changed — run it first on every board.)*
4. Click W, A, S, D (all four highlight), set press to **1.0 mm**. **Expected:** all four react
   at the new depth; other keys unchanged. Click A again. **Expected:** only A un-highlights.
5. With W, S, D still selected, enable **Rapid Trigger** and set Quick Press/Release to
   **0.1 mm**. Hold W and make tiny up-down finger movements. **Expected:** W re-triggers on
   micro-movements; a key outside the selection (e.g. Space) behaves normally.
6. Unplug the keyboard, replug, reopen Actuation → Per-key, click Q. **Expected:** Q still
   shows 0.5 mm — per-key values survived on the keyboard.
7. Switch to "All keys", set press to **2.0 mm**, wait, then check Q and W. **Expected:** every
   key, including the previously customised ones, now actuates at 2.0 mm (All keys overwrites
   per-key setup — this is intended).
8. Repeat steps 2–7 on the **Aress** and the **T800**.
9. T800 only, if a unit configured with the vendor app is available: set one key to an advanced
   mode (DKS/Toggle) in the vendor software, then do a per-key RT change on *other* keys in our
   app. **Expected:** the advanced key keeps behaving per its vendor-configured mode.
10. Open Lighting and Key Assignments after per-key edits. **Expected:** lighting and remaps
    still work — actuation writes did not corrupt other settings.
11. Drag a slider back and forth for a few seconds with ~10 keys selected. **Expected:** the app
    stays smooth; the keys land on the final slider value.
12. If any write fails (e.g. yank the cable mid-drag): **Expected:** an amber message appears in
    the section and the sliders return to the keyboard's real values; no crash.

## Review focus

1. `gRPC contract` — the old-host degradation path: empty per-key map ⇒ no per-key UI, and the
   per-key write being a separate RPC (never a widened `SetKeyActuation`).
2. `thread-safety` — services hold their device gate across the whole read-modify-write; the
   VM's selection snapshot is taken on the UI thread before the await, so a selection edit
   during a slow write cannot mutate the slot list mid-flight.
3. `async/await` — the per-key apply rides the existing latest-wins single-in-flight loop; no
   new sync-over-async.
4. `UI-thread affinity` — key-click toggling and slider ticks stay allocation-light; the
   per-key map lookup on click is a dictionary hit, not a device call.

## Performance & quality

- Key clicks and slider drags are UI-thread hot paths: toggling selection is a `HashSet` op +
  one `IsSelected` change; no per-tick allocation beyond the existing property raises.
- A Smart Infinite per-key write still transfers the whole area (the protocol writes it as a
  unit) — same cost as today's global write, debounced identically. T800 per-key writes are
  *cheaper* than global (only pages containing selected slots), and FlashEnable batching keeps
  it at one flash commit per apply.
- The per-key map is ~128 entries of a 5-field record, built once per section load — negligible.
- Shared state: no new singletons; services stay per-call with the static per-device-path gates.
- No SQLite on this path.

## Risks

- **Slot-identity assumption** (UI `MatrixIndex` == travel/trip slot) is inherited from vendor
  behaviour, not yet hardware-verified. Worst case: the wrong key changes. QA step 3 is designed
  as the first-contact check on every board; the write path itself cannot corrupt non-actuation
  state (only modelled fields are patched).
- **Old DeviceHost + new App**: handled by design (Option A rationale) — but the reviewer must
  confirm no code path falls back from a failed `SetPerKeyActuation` to the global
  `SetKeyActuation`, which would flatten the board.
- **T800 partial-page semantics**: writing a subset of pages of one option is assumed valid
  (pages are independently addressed). If firmware treats an option sweep as all-or-nothing,
  the verify catches it (Rejected) and per-key on T800 degrades loudly, not silently.
- **Selection UX drift**: `SelectPhysicalKey` now serves three consumers (Key Assignments radio,
  Macros radio, Actuation toggle). The branch must key on section+mode, and mode must reset on
  section/device change, or a stale toggle mode breaks Key Assignments selection.
- **Verify cost on Smart Infinite** grows (unpatched-slot comparison) but stays within the
  existing read-back it already performs.

## Open questions

1. Should the All-keys mode show a small note that applying it overrides per-key setup
   ("Applies to every key")? Plan assumes yes as a subtitle string — cheap and prevents a
   support question. Say the word and it's dropped.
2. Selected-keys label format when many keys are selected — plan assumes "Selected: W, A, S, D"
   up to ~6 names, then "Selected: N keys". Fine-tune at implementation if design says
   otherwise.

## Review findings

### Round 1 — Ship: no

#### Summary
The branch implements per-key actuation end-to-end exactly along the plan's architecture: a
slot-selective `PatchSlots`/`ReadAllSlots` codec and page-selective T800 RMW in Infrastructure
behind `WriteKeysAsync`, the per-key map riding on `GetKeyActuationResponse` plus a **separate**
`SetPerKeyActuation` RPC, and an App-side All keys / Per-key mode with multi-select on the
existing keyboard overlay and the same 400 ms latest-wins debounce. 22 new tests (scripted
`ISmartInfiniteIo` fake for the service, codec/paging theories, 9 VM cases); full solution
builds; both suites green and the App suite verified stable twice. One real timing bug in the
edit-flush path blocks: it can turn a single-key edit into a whole-board write.

#### Plan conformance
Plan: this document.
- Implemented as planned: every File-level change list item, including the safety order —
  read-modify-write on both families, unpatched-slot byte-identity in the Smart Infinite verify,
  T800 key-mode bit-7-only patching, page-selective T800 writes with one flash commit,
  empty-slot refusals at every layer, and the old-host degradation (empty map ⇒ no mode toggle;
  separate RPC ⇒ no silent flatten). No fallback from a failed `SetPerKeyActuation` to the
  global RPC exists (checked the apply path explicitly, per this plan's Risks).
- Deviates from plan (surfaced, none behavioral):
  - `src/ProoveGaming.App/App.axaml` gained a shared `sectionModeButton`/`.active` style pair —
    not in the change list, but the plan told the coder to grep for an existing two-state style
    first and none existed; role-named per the style rules.
  - `SmartInfiniteActuationService` gained an internal transport-seam constructor
    (`_device` → `_devicePath`), mirroring the key-mapping service's m5 seam — test-enabling,
    no behavior change.
  - Open question 1 resolved as the plan assumed: the "applies to every key" note renders, and
    only when the mode toggle is visible.
- Missing / partial: none.
- Accepted limitation (per plan): slot identity (UI `MatrixIndex` == travel/trip slot) and the
  T800 partial-page flash semantics are hardware-unverified — QA step 3 is the first-contact
  check; the write paths self-limit to modelled fields either way.

#### Code
- [x] **B1. A pending edit flushed while a write is in flight retargets to the FUTURE mode/selection — a single-key edit can flatten the whole board** —
  _Closed via Option A: the bare `_actuationApplyQueued` flag is now an `ActuationWriteRequest`
  snapshot (mode + slots + values) built by `BuildActuationWriteRequest()` when the edit leaves
  its debounce window (debounce fire or flush, both before any caller mutation); the apply loop
  consumes queued snapshots instead of re-reading current state. Regression tests:
  `ModeSwitchDuringInFlightWrite_KeepsThePendingEditTargetedAtItsKeys` and
  `SectionSwitchDuringInFlightWrite_NeverIssuesAGlobalWrite` (gated fake write holds the first
  apply in flight)._ —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Actuation.cs:265-271`
  (`FlushPendingActuationApply`) + `:498-502` (in-flight branch of `ApplyActuationToDeviceAsync`) — Blocker
  **Problem:** `FlushPendingActuationApply` exists to commit a debounce-window edit against the
  selection/mode it was made in. In the normal case it works: the apply's snapshot runs
  synchronously before the caller mutates state. But when a previous apply is **in flight**, the
  flushed call only sets `_actuationApplyQueued = true` — no context is captured — and the
  queued follow-up later re-reads *current* mode, selection and slider values.
  **Why it blocks:** per-key mode, W selected. Drag → apply #1 starts (real HID writes take
  ~0.5–2 s). Drag again (edit sits in the 400 ms window), then click "All keys" — or navigate to
  another section, which forces `ActuationPerKeyMode = false` via the `ActiveSection` setter
  (`DeviceDetailsViewModel.cs:759-760`). The setter flushes → queued-flag only; then mode flips
  to All-keys and `ShowActuationMajorityValues()` rewrites the slider fields. When apply #1
  completes, the loop consumes the queued flag with `perKey = false` and issues a **global**
  `SetKeyActuationAsync`, silently wiping every per-key value the user configured — from an
  interaction that never asked for a write. The milder variant (clicking another key instead of
  switching modes) writes the last-clicked key's cached values onto the new selection.
  **Solution options:**
  - Option A (correct): snapshot the edit's context at schedule time — replace the bare
    `_actuationApplyQueued` bool with a pending-context field (e.g.
    `(bool PerKey, int[]? Slots)? _actuationQueuedTarget`) set by `ScheduleActuationApply` /
    `FlushPendingActuationApply` and consumed by the loop instead of re-reading
    `_actuationPerKeyMode`/`_actuationSelectedKeys`. Values stay latest-wins (coalescing keeps
    working); only the *target* is pinned to edit time. Selection/mode changes already flush
    first, so the pinned target is always coherent.
  - Option B (quick): in `FlushPendingActuationApply`, when `_actuationApplyInFlight` is true,
    **drop** the pending edit (clear the flag, cancel the CTS, do not queue). Trade-off: an edit
    made <400 ms before a mode/selection/section change during a slow write is lost instead of
    committed — a dropped edit is strictly safer than a mistargeted write, and the sliders
    re-sync from the cache on the next click.

- [x] **S1. `IsSelected` ghost-state cross-talk between the Key-Assignments radio selection and the actuation multi-select** —
  _Closed: entering Per-key mode clears `IsSelected` on all `PhysicalKeys`; leaving restores
  `SelectedPhysicalKey.IsSelected`. Test:
  `EnteringPerKeyMode_ClearsTheRadioGhostHighlight_AndLeavingRestoresIt`._ —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Actuation.cs:155-182`
  (`ToggleActuationKey` / `ClearActuationSelection`) — Should-fix
  **Problem / why it matters:** both consumers highlight keys via the same
  `KeyAssignmentOptionViewModel.IsSelected` flag. Entering Per-key mode does not clear a
  radio-selected key's flag, so a key picked earlier in Key Assignments/Macros renders
  highlighted on the actuation overlay while `_actuationSelectedKeys` is empty — the hint says
  "click keys" under a keyboard that already shows one chosen. Inversely, actuation-toggling
  that same key off clears its flag, so back in Key Assignments the header still names the key
  (`SelectedPhysicalKey` is untouched) but its overlay highlight is gone.
  **Fix:** on entering Per-key mode clear `IsSelected` on all `PhysicalKeys` before starting the
  actuation selection; on leaving (mode setter's `ClearActuationSelection` path) restore the
  radio state with `if (SelectedPhysicalKey is not null) SelectedPhysicalKey.IsSelected = true;`.
  Both are UI-thread list walks, VM-testable.

#### Tests
- [x] Regression test for B1 once fixed: with a slow (TaskCompletionSource-gated) fake per-key
  write in flight and an edit pending, flip `ActuationPerKeyMode` off (and a second case:
  switch `ActiveSection`) → assert **no** `SetKeyActuationAsync` (global) call is ever issued
  and the board-flatten write never happens —
  `tests/ProoveGaming.App.Tests/DeviceDetailsActuationViewModelTests.cs` (the fake needs an
  awaitable write, a small extension of `FakeHid`). _Added: `PerKeyWriteGate` on `FakeHid`,
  both scenarios covered; the mode-switch case also asserts the flushed edit's own value (120)
  reaches its key._
- [x] Optional, with S1: entering Per-key mode clears a radio-selected key's highlight and
  leaving restores it — same file. _Added._

#### Nits
- Nested `:disabled` opacity styles stack: with no selection (outer 0.4) and RT off (inner 0.4)
  the RT block renders at ~0.16 opacity, dimmer than its siblings —
  `src/ProoveGaming.App/Views/DeviceDetails/Sections/ActuationSectionView.axaml:60-65,122-127`.
- `ActuationSelectedKeysLabel` composes via `LocalizationManager.GetString` and is not re-raised
  on a language switch — follows the existing VM precedent (`WelcomeViewModel`), noting only.
- The All-keys cache update iterates `_perKeyActuation.Keys` while assigning values in place —
  legal since .NET Core 3.0 (value overwrite doesn't invalidate enumerators) but subtle enough
  to deserve a one-line comment — `DeviceDetailsViewModel.Actuation.cs:558-559`.
- After a failed write whose re-read also fails (cable yanked), the section shows the loading
  line while Per-key mode keeps the overlay clickable; clicks mutate selection harmlessly (the
  apply guard holds) but the state reads oddly until reopen — transient, self-healing.

#### What's done well
- The degradation story is airtight where it matters: the separate RPC means an old DeviceHost
  can never misread a per-key write as global, the empty-map gate keeps the toggle from ever
  rendering against such a host, and there is no fallback path between the two write RPCs.
- Preservation discipline carried through both families: Smart Infinite verifies patched slots
  by value AND unpatched slots byte-for-byte against the pre-write read; T800 turned its global
  `Fill` into a true RMW and only rewrites pages that contain a patched slot — genuinely fewer
  flash commits than the global path.
- The scripted-fake service tests pin the safety rules without hardware (discarded write ⇒
  Rejected in ONE session, no retry storm), and the VM tests assert the exact slot snapshot in
  the write, not just call counts.
- Reusing `SelectPhysicalKey`/`PhysicalKeys` for the overlay instead of a parallel path avoided
  the copied-layout drift this repo has been burned by — and the section-leave mode reset keeps
  the branch from leaking into the radio consumers.

#### Verdict
Ship: no — B1 is a plausible-timing path where the feature's own UI destroys the per-key
configuration it just created; everything else is polish. First: fix B1 (Option A preferred —
Option B acceptable if shipping today), add its regression test, then S1.
