# add-keyboard-actuation — Actuation (key press force) settings for magnetic keyboards

## Goal

Give the App an **Actuation** section for the three magnetic-switch keyboards — **T800**,
**Cyrex** and **Aress** — letting the user set the actuation point (Initial Actuation Point),
the release point, and Rapid Trigger (on/off + Quick Press / Quick Release travel), applied
globally to all keys and written to the keyboard immediately (debounced) so the change is felt
at once. The T800 protocol for this is already documented and hardware-verified
(`docs/knowledge-base/ry5088-magnetic-axis-protocol.md`); the Cyrex/Aress protocol was extracted
from the vendor's Keyborder-v3 app
(`docs/knowledge-base/smart-infinite-protocol/magnetic-axis-travel.md`) and matches our firmware's
command numbering, but has not yet been exercised on our units — the design below reads before it
ever writes.

## Scope

**In:**
- Global (all-keys) actuation point + release point, in mm.
- Rapid Trigger toggle; Quick Press / Quick Release travel values, in mm.
- Slider ranges, defaults and step taken from the device (Device Info), not hardcoded.
- Immediate, debounced apply to hardware; read-back state on device open.
- New gRPC surface App ↔ DeviceHost; per-family services in Infrastructure.
- Section visible only on supported keyboards (catalog flag).

**Out (explicitly not in this change):**
- Per-key values / key-selection UI (protocol layer is built so this can come later).
- Dead zones, calibration mode (`0x37`), travel test (`0x36`), advanced keys (DKS/MT/TGL/Snap),
  vendor presets (Balanced/Esports/Extreme).
- Preset (SQLite) capture of actuation values — the keyboard's flash is the source of truth,
  matching how Smart Infinite key remaps already bypass the staged-write model.
- The macro-command discrepancy noted in the knowledge-base doc (`0x1B/0x1C` vs `0x2C/0x2D`) —
  unrelated to actuation, do not touch the macro path.

## Options considered

**A. Per-family actuation services + dedicated gRPC RPCs (recommended).**
New `IKeyboardActuationService` in Infrastructure with `SmartInfiniteActuationService`
(Cyrex/Aress) and `T800KeyboardActuationService` (RY5088) behind a factory, exposed by two new
RPCs (`GetKeyActuation` / `SetKeyActuation`). Mirrors exactly how key remapping is structured
(`DeviceKeyMappingServiceFactory`, `SetKeyMapping`/`ReadKeyMatrix`). *Trade-off:* proto change and
both-sides regeneration; more files. *Why it wins:* actuation is its own domain with its own
read-modify-write and limits; overloading an existing RPC would smear the contract.

**B. Ride on `ApplyDeviceConfiguration` / extend an existing RPC.**
Fewer proto changes, but actuation needs a *read* path (limits, current values) that
`ApplyDeviceConfiguration` does not have, and its request would grow model-specific unions.
Rejected: wrong altitude, harder contract to review.

**C. App-side HID access.**
Rejected outright — DeviceHost owns all device I/O in this architecture, and both families need
process-wide session gates (`SmartInfiniteDeviceGate`, `Ry5088DeviceGate`) that live there.

**Unit convention decision (applies to A):** the gRPC contract carries travel values as
**hundredths of a millimetre** (`uint32`, e.g. 2.00 mm = 200). T800 natively uses that unit;
Smart Infinite uses `keyStepMarker`-sized steps (0.01 mm on Cyrex) and the DeviceHost converts.
The App never sees raw device steps.

## Recommended approach

Option A. Flow:

1. **Device open (App)** → `GetKeyActuation(vid, pid)` → DeviceHost resolves the family service →
   service reads limits/defaults from the device and the current per-key table, reduces it to the
   global values shown (see "reduction" below) → App fills sliders and shows the section.
2. **User drags a slider / toggles RT (App)** → debounced (~400 ms after last change, matching the
   live-apply debounce pattern) → `SetKeyActuation` with the full 5-value payload →
   family service does a **read-modify-write**: re-reads the current table, patches only the four
   value fields (+ RT flag) on every key, preserves everything it does not model (axis-type
   nibble, dead zones, T800 key modes other than Normal/RT), writes, reads back, verifies.
3. **Reduction rule** (table → single global values for the UI): use the value of the majority of
   keys; if keys disagree (per-key config written by the vendor app), still show the majority
   value — the next apply from our UI makes them uniform, which is the documented behaviour of a
   global-only editor.

Family specifics:

- **Smart Infinite (Cyrex `12914`, Aress `12910`):** commands `0x38` read / `0x39` write of the
  travel area, chunked 56 B via the existing `SmartInfiniteProtocol` framing and serialized by
  `SmartInfiniteDeviceGate`. Area geometry from Device Info: slot count (byte 7), step
  (byte 16: bit 7 → 0.01/0.001 mm factor × low-7-bit multiplier), record size (byte 44; 0→10,
  else as reported, 12 = has top dead zone), limits (bytes 17–28), defaults (bytes 29–42).
  Per-key record fields (uint16 LE, in steps): DOWN@0, UP@2, QUICKLY_DOWN@4 (bits 15–14 = `0b01`
  flag when RT active, value in bits 13–0), QUICKLY_UP@6, BOTTOM_DEAD_ZONE@8 (bits 15–12 = axis
  nibble — **must be preserved**), TOP_DEAD_ZONE@10 when present.
  **Safety gate:** before the first write the service validates coherence — area size =
  slots × record size, step > 0, limits ordered, read-back of `0x38` succeeds and parses; any
  failure ⇒ `supported=false` returned, no write ever attempted.
- **T800 (RY5088):** command `0x65` bulk set (`Select=1`, paged) for Options 0 (trigger travel),
  1 (release travel), 2 (RT trigger), 3 (RT release) and Option 7 (key mode: Normal=0 /
  `|=0x80` RT) — values two bytes per key packed ~28 keys per page, one-byte options 56 per page;
  the **last** command in the batch sets `FlashEnable=1`. Read via `0xE5` mirror. One's-complement
  checksum and feature-report transport already implemented in `Ry5088Protocol` /
  `Ry5088DeviceGate`.

Section visibility follows the `Defaults`/`SupportsConfiguration` precedent: a new
`SupportsActuation` catalog flag on the device in DeviceHost `appsettings.json`, carried on
`DeviceCatalogEntry`, surfaced on `DeviceCardViewModel` — so the nav item exists before any RPC,
and the section body additionally trusts `GetKeyActuation.supported` (hides on false).

## File-level change list

Infrastructure (shared device logic):

- `src/ProoveGaming.Infrastructure/Classes/SmartInfiniteProtocol.cs` — edit: add magnetic-axis
  command constants (`0x36`–`0x3B`) with doc comments referencing the knowledge-base doc.
- `src/ProoveGaming.Infrastructure/Classes/SmartInfiniteTravelArea.cs` — **new**: pure codec for
  the travel area — record offsets, step conversion, QUICKLY_DOWN flag encode/decode, axis-nibble
  and dead-zone preservation on patch, majority reduction; no device access (unit-testable).
- `src/ProoveGaming.Infrastructure/Classes/SmartInfiniteDeviceInfo.cs` — edit: parse the
  magnetic-axis Device Info fields (slot count, step marker, record size, limits, defaults).
- `src/ProoveGaming.Infrastructure/Classes/Ry5088Protocol.cs` — edit: add `CmdSetTrip = 0x65`,
  `CmdGetTrip = 0xE5`, trip option constants, bulk-page packet builders (two-byte and one-byte
  options) and page parsers.
- `src/ProoveGaming.Infrastructure/Classes/KeyboardActuation.cs` — **new**: transport-neutral
  models — `KeyboardActuationValues` (press/release/RT flag/quick press/quick release, in
  0.01 mm) and `KeyboardActuationLimits` (min/max press-release, min/max RT, defaults, step).
- `src/ProoveGaming.Infrastructure/Interfaces/IKeyboardActuationService.cs` — **new**:
  `ReadAsync` → values+limits+supported; `WriteAsync(values)` → bool.
- `src/ProoveGaming.Infrastructure/Services/SmartInfiniteActuationService.cs` — **new**: Cyrex /
  Aress implementation per the approach above (gate, coherence gate, read-modify-write, verify).
- `src/ProoveGaming.Infrastructure/Services/T800KeyboardActuationService.cs` — **new**: RY5088
  implementation (bulk pages, FlashEnable batching, RT via Option 7 key-mode flag).
- `src/ProoveGaming.Infrastructure/Services/DeviceActuationServiceFactory.cs` — **new**: VID/PID →
  family service, modelled on `DeviceKeyMappingServiceFactory`.

gRPC contract:

- `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto` — edit: `rpc GetKeyActuation`
  (request: vid/pid; response: supported, values, limits) and `rpc SetKeyActuation` (request:
  vid/pid + values; response: success + error string). All travel fields `uint32`, unit 0.01 mm,
  stated in comments. Add `supports_actuation` to `DeviceCatalogEntry`.

DeviceHost:

- `src/ProoveGaming.DeviceHost/Services/DeviceService.cs` — edit: implement the two RPCs, routing
  through `DeviceActuationServiceFactory` exactly as `SetKeyMapping` routes through its factory.
- `src/ProoveGaming.DeviceHost/appsettings.json` — edit: `SupportsActuation: true` on T800, Cyrex,
  Aress catalog entries.
- `src/ProoveGaming.Infrastructure/Classes/DeviceMap.cs` (and `DeviceMapExpander` if the flag is
  per-device like `Defaults`) — edit: carry `SupportsActuation` from config to catalog entry.

App:

- `src/ProoveGaming.App/Services/IUsbHidDeviceService.cs` / `UsbHidDeviceService.cs` — edit:
  `GetKeyActuationAsync` / `SetKeyActuationAsync` client calls.
- `src/ProoveGaming.App/ViewModels/DeviceCardViewModel.cs` — edit: surface `SupportsActuation`
  from the catalog entry.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Actuation.cs` — **new**
  partial: observable values, limits→slider ranges, clamping, RT gating, load-on-open, debounced
  apply (single in-flight write, latest-wins), `IsActuationNavVisible`.
- `src/ProoveGaming.App/Views/DeviceDetails/Sections/ActuationSectionView.axaml` (+ `.axaml.cs`)
  — **new**: two sliders (actuation / release) + RT toggle + two RT sliders, numeric readouts in
  mm; reuse shared slider/section styles, no per-view style copies.
- `src/ProoveGaming.App/Views/DeviceDetails/DeviceDetailsView.axaml` — edit: nav entry + section
  host wiring, visible per `IsActuationNavVisible`.
- `src/ProoveGaming.App/ViewModels/NavSection.cs` — edit: add the Actuation section id.
- `src/ProoveGaming.App/Resources/Strings.en.axaml` / `Strings.uk.axaml` — edit: section title,
  labels, RT description, unit suffix, error toast text.

Docs:

- `docs/CONTEXT.md` — edit: add **Actuation point** and **Rapid Trigger (RT)** glossary entries.

Tests (ordering: codec tests may land with the codecs, before services):

- `tests/ProoveGaming.Infrastructure.Tests/SmartInfiniteTravelAreaTests.cs` — **new**.
- `tests/ProoveGaming.Infrastructure.Tests/Ry5088ProtocolTripTests.cs` — **new**.
- `tests/ProoveGaming.App.Tests/DeviceDetailsActuationViewModelTests.cs` — **new**.

## Acceptance criteria

- [ ] Given a connected Cyrex, Aress or T800 → open device details → an **Actuation** nav item is
  visible; on Raster / Rampage / T-Racer / mice it is absent.
- [ ] Given the section opens → sliders show the device's **current** values and the ranges/steps
  come from the device (Cyrex: step 0.01 mm; ranges = Device Info limits), not constants.
- [ ] Given the user sets actuation point to 1.0 mm and stops moving the slider → within ~1 s one
  write reaches the device; pressing a key to ~1 mm now registers (manually verifiable), and
  re-opening the section (or replugging) reads 1.0 mm back.
- [ ] Given RT is toggled off → Quick Press / Quick Release controls are disabled and the write
  clears the RT state on the device; toggled on → they are enabled and their values are written.
- [ ] Given a slider is dragged continuously for 3 s → no more than ~1 write per debounce window
  is issued (no per-tick RPC storm), and the final device state equals the final slider value.
- [ ] Given the Smart Infinite coherence gate fails (incoherent Device Info or unreadable `0x38`)
  → `GetKeyActuation` returns `supported=false`, the section hides, and **no write command is
  ever sent** to that device.
- [ ] Given a device whose travel table carries per-key differences and a non-zero axis nibble /
  dead zones → after our global write, axis nibble and dead-zone values are byte-identical to the
  pre-write read (verified by the service's read-back and coverable in codec tests).
- [ ] Given the DeviceHost is stopped → the section shows the existing connection-error behaviour
  and the App does not freeze (no sync-over-async on the UI thread).
- [ ] `dotnet build ./src/ProveGaming.slnx` and `dotnet test ./src/ProveGaming.slnx` pass.

## Test strategy

New feature (`add-*`) — the following are required coverage, all against the pure codec /
ViewModel layers (no hardware):

`tests/ProoveGaming.Infrastructure.Tests/SmartInfiniteTravelAreaTests.cs`:
- Record field offsets and LE encoding for 10- and 12-byte records.
- Step conversion both directions (byte 16 = `0x01` → 0.01 mm; `0x81` → 0.001 mm branch).
- QUICKLY_DOWN flag: encode sets `0b01` prefix, decode strips it; RT-off write clears it.
- Patch preserves axis nibble (bits 15–12 of BOTTOM_DEAD_ZONE) and dead-zone values byte-for-byte.
- Majority reduction on a mixed per-key table.
- Coherence gate rejects: size ≠ slots × record, step 0, inverted limits.

`tests/ProoveGaming.Infrastructure.Tests/Ry5088ProtocolTripTests.cs`:
- `0x65` packet layout per option (two-byte vs one-byte), `Select`, `Index`, checksum
  (one's complement — reuse existing checksum test style).
- Bulk paging: 68-key board → page counts and per-page key spans for 28-key and 56-key options.
- FlashEnable only on the final packet of a batch.
- `0xE5` read builders and response parsing; mm ↔ 0.01 mm unit conversion.

`tests/ProoveGaming.App.Tests/DeviceDetailsActuationViewModelTests.cs`:
- Limits map to slider min/max; values clamp to limits.
- RT toggle gates quick press/release enablement.
- Debounce: N rapid changes → one service call with the last values (latest-wins).
- `supported=false` hides the section state.

**Deliberately not automated:** real HID traffic on all three boards, firmware acceptance of the
extracted `0x38`/`0x39` commands, and the physical feel of an actuation change — manual QA below.
DeviceHost RPC plumbing is thin routing over the factory and is exercised indirectly.

## QA (manual test steps)

1. Connect the **PG Cyrex** by cable, launch the app, open the keyboard's page. **Expected:** an
   "Actuation" item appears in the left menu; opening it shows current values (a factory board
   shows its defaults, e.g. 2.0 mm) and sliders with mm markings.
2. Set "Initial Actuation Point" to **0.5 mm**. Wait a second, then type lightly, barely pressing
   the keys. **Expected:** letters register from a very light touch.
3. Set it to **3.0 mm** (or the slider maximum). **Expected:** letters only register when keys are
   pressed almost to the bottom.
4. Enable **Rapid Trigger**, set Quick Press and Quick Release to **0.1 mm**, hold W in a game (or
   a key-test website) and make tiny up-down finger movements without fully releasing.
   **Expected:** the key re-triggers on each micro-movement.
5. Disable Rapid Trigger. **Expected:** the two RT sliders grey out; re-triggering now requires a
   full release past the release point.
6. Set actuation to 1.5 mm, unplug the keyboard, plug it back in, reopen the Actuation section.
   **Expected:** 1.5 mm is shown — the value survived on the keyboard itself.
7. Repeat steps 1–6 on the **Aress** and the **T800**. **Expected:** identical behaviour.
8. Open the same keyboard's Lighting and Key Mapping pages after changing actuation.
   **Expected:** lighting and remaps still work (the new writes did not corrupt other settings).
9. Connect a **Raster** or **Rampage** (non-magnetic). **Expected:** no "Actuation" menu item.
10. Drag a slider back and forth continuously for a few seconds. **Expected:** the app stays
    smooth, no stutter; when you stop, the keyboard behaves per the final slider position.

## Review focus

1. `thread-safety` — both family services must hold their device gate for the whole
   read-modify-write; concurrent RGB / key-mapping calls must serialize, not interleave.
2. `async/await` — debounced apply must be latest-wins with a single in-flight write; no
   sync-over-async anywhere on the UI thread.
3. `gRPC contract` — unit (0.01 mm) and field semantics documented in the proto; both sides
   regenerate from the same file.
4. `UI-thread affinity` — slider drag handlers stay allocation-light; device I/O results marshal
   back via the dispatcher.

## Performance & quality

- Slider `PropertyChanged` fires per pixel of drag — it must only update local state and restart
  the debounce timer; **no RPC, no allocation-heavy work per tick**.
- A Smart Infinite apply writes the whole travel area (~680–900 B ⇒ 13–17 packets) plus a
  read-back; that is why writes are debounced and serialized (single in-flight, latest value
  wins). T800 bulk pages are fewer packets but flash-persisted — FlashEnable batching means one
  flash commit per apply, not per option.
- Shared state: family services are created per call (like the key-mapping services); cross-call
  exclusivity comes from the static per-device-path gates — no new singletons with mutable state.
- No SQLite writes on this path.

## Risks

- **The Smart Infinite magnetic commands are extracted, not yet hardware-verified.** Mitigated by:
  read-only probe first (`0x38` + Device Info coherence) gating all writes; read-modify-write that
  never invents bytes it did not read; read-back verification; and the write command being
  distinct from anything destructive (`0x20` stays untouched). Residual risk: firmware ignores or
  misinterprets `0x39` — outcome is a non-working slider, not a bricked board, and QA step 8
  checks for collateral damage.
- **Unit misinterpretation** (step 0.001 vs 0.01 mm) would make values 10× off — mitigated by
  clamping every write to the device-reported limits and unit tests on both step branches.
- **MK620 (Aress) firmware may differ from MK25005 (Cyrex)** — same family per Keyborder's config,
  but QA must run on both boards.
- **T800 flash wear** — every apply commits to flash; debounce keeps this to a handful per session.
- Adding a proto field + catalog flag: an older DeviceHost paired with a newer App returns
  `supports_actuation=false` by proto3 default → section hidden — safe degradation, but worth a
  reviewer glance.


11. (T800, only if a unit configured with the vendor app is available.) Set one key to an advanced
    mode in the vendor software (e.g. Toggle or DKS), then change actuation in our app.
    Expected: the advanced key still behaves per its mode — our RT toggle must not reset it.
12. If any actuation write on Cyrex/Aress fails: an amber message appears in the section and the
    sliders return to the keyboard's real values. Expected: the message is shown, nothing crashes.

## Open questions

0. **For the factory, alongside the C8 list (PR #84 review, m3):** does the travel area have its
   own fixed transfer reserve (a `KEY_TRAVEL_TAB_SIZE` analogous to `KEY_MATRIX_TAB_SIZE = 384` /
   `MACRO_TAB_SIZE = 2048`), or is the Device-Info-sized whole-area write the vendor app performs
   the correct shape? This family has twice punished trusting Device Info for transfer sizing;
   the very first hardware step on a live Cyrex should be the `0x38` read + one `0x39` write with
   read-back before the rest of QA.
1. Hardware availability: QA needs all three boards (Cyrex, Aress, T800). Is an Aress unit on
   hand, or does it ship on Cyrex verification alone (risk noted above)?
2. When the vendor app's per-key values exist on a board, our global write makes them uniform on
   first apply (documented behaviour above). Acceptable, or should the first apply warn? (Plan
   assumes: acceptable, no warning.)
3. **Open since 2026-09-11: should `GetKeyActuation` separate "the board says no" from "the host
   could not read the board"?** `ReadAsync` returns null for both and the handler maps both to
   `success=true, supported=false`, so a transport hiccup hides the section for the rest of the
   page's life — the Round 1 nit above, seen again. The sibling feature fixed exactly this shape
   (`docs/plans/add-cyrex-extended-keys/`, round 2, S2: `ExtendedKeysReadResult` with
   `Read`/`Unsupported`/`Failed`, and `success=false` for a failure the App already treats as
   "keep loading, retry on reopen"). Doing the same here means the same surgery on
   `SmartInfiniteActuationService`, `T800KeyboardActuationService`, `IKeyboardActuationService`,
   the handler and their tests, so it was deliberately left out of that pass rather than done
   half-way. Nothing in this plan's behaviour has changed.

## Review findings

### Round 1 — Ship: yes

#### Summary
The branch implements global actuation (press/release + Rapid Trigger) for T800, Cyrex and
Aress end-to-end: pure codecs and per-family services in Infrastructure behind
`IDeviceActuationServiceFactory`, two new gRPC RPCs with a 0.01 mm unit contract, a
`SupportsActuation` catalog flag, and an App Actuation section (pointer-driven sliders, RT
toggle, 400 ms debounced latest-wins writes, probe-gated visibility). 17 new unit tests across
three files; full solution builds; both suites green (App suite verified stable across three
consecutive full runs).

#### Plan conformance
Plan: this document.
- Implemented as planned: every File-level change list item is present, including the safety
  order (geometry gate before any Smart Infinite write, read-modify-write with axis-nibble /
  dead-zone preservation, read-back verify, device-reported limits driving slider ranges,
  T800 `FlashEnable` batching, catalog flag on `DeviceCatalogEntry` with safe proto3 default).
- Deviates from plan (all minor, none behavioral):
  - The plan's Strings list included a "unit suffix" and "error toast text" key; the " mm"
    suffix is instead interpolated in `DeviceDetailsViewModel.Actuation.cs` display
    properties, matching the existing `MouseSettingViewModel.DisplayValue` precedent, and
    errors ride the existing `NotifyError` channel. See N2.
  - `IsActuationNavVisible` lives in `DeviceDetailsViewModel.cs` (with the other nav flags)
    rather than the Actuation partial — better placement, noting for plan accuracy.
  - Six existing test fakes gained the two new interface members (forced by the
    `IUsbHidDeviceService` change) and `LoadActuationFromDeviceAsync` is `internal` for
    deterministic tests — justified scope additions.
- Accepted limitation (per plan): the Smart Infinite `0x38`/`0x39` commands are extracted
  from Keyborder-v3, not hardware-verified. Residual risk stands exactly as the plan framed
  it: worst case is a non-working section (probe self-gates to `supported=false`), not a
  corrupted board; QA step 8 checks for collateral damage. The T800 `0xE5` read path is
  likewise documented-but-uncaptured and self-gates the same way.
- Acceptance criteria: all code-verifiable items are implemented and unit-tested (debounce
  single-write, clamp-to-limits, RT gating, coherence-gate refusal, byte-for-byte
  preservation). The hardware-facing items (feel of 0.5 vs 3.0 mm, flash persistence across
  replug, three boards) are QA's — unchecked by design at this stage.

#### Code
- none (no blockers)

- [x] **S1. Actuation section shows live, zeroed sliders while the probe is still running** —
  _Closed: slider body now gated on `IsActuationSupported`; `IsActuationProbePending` shows a
  localized loading line (`DeviceDetails_Actuation_Loading`, en+uk)._ —
  `src/ProoveGaming.App/Views/DeviceDetails/Sections/ActuationSectionView.axaml:13` — Should-fix
  **Problem / why it matters:** the section body is gated only on `IsActuationSection`. On a
  supported keyboard the nav entry is visible while the first `GetKeyActuation` round-trip
  (device-info + 14-chunk area read, ~0.5–2 s) is in flight, and `_actuationInfo` is still
  null — so opening the section in that window shows all sliders at "0.00 mm" with thumbs at
  the far left. They are draggable (the write guard correctly refuses to fire), and the
  values then jump when the load lands — reads as a glitch, and briefly displays actuation
  values the board does not have. The VM already exposes `IsActuationProbePending` /
  `IsActuationSupported` for exactly this, but the view never binds them.
  **Fix:** bind the slider StackPanel's `IsVisible` (or `IsEnabled` + opacity, matching the
  RT-off treatment) to `IsActuationSupported`; optionally show a lightweight "loading" text
  on `IsActuationProbePending`. Pure XAML + one localized string, no VM change needed.

- [x] **S2. Smart Infinite transfer plumbing duplicated into the new service** —
  _Closed via Option A: shared internal `SmartInfiniteTransport`
  (`src/ProoveGaming.Infrastructure/Classes/SmartInfiniteTransport.cs`) now owns
  OnHandle/ReadDeviceInfo/ReadBlock/ReadTable/WriteTable/ReplyFor/WithRetry; both
  `SmartInfiniteKeyMappingService` and `SmartInfiniteActuationService` delegate to it._ —
  `src/ProoveGaming.Infrastructure/Services/SmartInfiniteActuationService.cs:230-340`
  (`OnHandle` / `ReadBlock` / `ReplyFor` / `WithRetry`, and `ReadArea` ≈ `ReadTable`) — Should-fix
  **Problem / why it matters:** ~150 lines are a near-verbatim copy of
  `SmartInfiniteKeyMappingService`'s private transfer helpers. This repo has already paid for
  this exact pattern once — CONTEXT.md records that T-Racer's copied key-mapping file
  "silently drifted" from Raster's until it broke macro bindings. The one-command-lag reply
  matching and the never-drain-before-first-request rule are subtle enough that a future fix
  applied to one copy and not the other is a realistic hardware-level bug.
  **Fix:** Option A (correct): extract a shared internal `SmartInfiniteTransport` helper
  (stream + logger + tag) both services delegate to; mechanical, no behavior change, and the
  existing key-mapping tests keep it honest. Option B (accept): leave the copy, add a
  cross-reference comment in both files naming the other as the sibling to keep in sync —
  the family precedent (RGB services are also copies), acknowledged as debt.

- [x] **S3. T800 mm↔device-unit conversion is untested (plan test item only partially met)** —
  _Closed: conversions moved to `Ry5088Protocol.TripUnitsToCentiMm` / `CentiMmToTripUnits`;
  theories added for precisions 10/5/1 µm incl. rounding and round-trip cases (14 new tests)._ —
  `src/ProoveGaming.Infrastructure/Services/T800KeyboardActuationService.cs:330-333`
  (`UnitsToCentiMm` / `CentiMmToUnits`) — Should-fix
  **Problem / why it matters:** the plan's Ry5088 test list names "mm ↔ 0.01 mm unit
  conversion"; `ParseTripPrecisionMicrometres` is covered but the actual conversions are
  private statics in the service and untested. They are the one place a 0.005 mm-precision
  unit (`precisionUm = 5`) changes the math — exactly the "values 10× off" risk the plan's
  clamps exist for, and a rounding regression there is invisible until a real board behaves
  strangely.
  **Fix:** move the two one-liners onto `Ry5088Protocol` (next to
  `ParseTripPrecisionMicrometres`, same visibility as the other pure helpers) and add
  `[Theory]` cases for precision 10/5/1 to `Ry5088ProtocolTripTests` — including a
  round-trip case at precision 5 where centi-mm cannot represent the value exactly.

#### Tests
- [x] Ry5088 unit conversion theories (see S3) — `tests/ProoveGaming.Infrastructure.Tests/Ry5088ProtocolTripTests.cs`
- [ ] Optional: the write-failure path (`SetKeyActuationAsync` returns false → VM re-reads and
  sliders revert to device truth) is implemented but uncovered —
  `tests/ProoveGaming.App.Tests/DeviceDetailsActuationViewModelTests.cs` already has the
  `ActuationWriteSucceeds` knob for it.

#### Nits
- `IsActuationProbePending` is currently dead (defined and raised, never bound) — absorbed by
  the S1 fix — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Actuation.cs:34`
- `SetActuationValue(... params string[] changedProperties)` allocates a 3-element array on
  every pointer-move tick; harmless in practice but the per-frame-cheap rule would prefer
  three explicit `OnPropertyChanged` calls per setter —
  `DeviceDetailsViewModel.Actuation.cs:82`
- " mm" unit suffix is code-interpolated rather than a resource key (existing
  `MouseSettingViewModel` does the same with "ms"/"mm", so this follows precedent; the plan
  text said otherwise — one of the two should be aligned) — `DeviceDetailsViewModel.Actuation.cs:40-43`
- `GetKeyActuation` reports `supported=false` also when the vendor interface merely isn't
  enumerated at that instant (dongle blip); the section then stays hidden until the device
  page is reopened. Behavior is safe and self-healing on reopen — worth a comment at
  `src/ProoveGaming.DeviceHost/Services/DeviceService.cs` (`GetKeyActuation`, factory-null branch).

#### What's done well
- The safety architecture survived implementation intact: no write path exists that skips the
  geometry gate, the read-modify-write patches only the four modelled fields, and the
  clamp-to-device-limits guard makes the step-unit-misread failure mode self-limiting.
- The VM's write-failure → re-read fallback keeps the UI honest against a board that rejects
  a write — better than the plan asked for.
- The test round already earned its keep: it caught `IsActuationNavVisible` ignoring the
  probe verdict, and the dispatcher-affinity flake was chased to the repo's documented
  first-touch trap and fixed per that convention (suite verified stable 3×).
- The unverified-protocol risk is consistently self-gating end to end: probe failure at any
  layer degrades to "section hidden", never to a blind write.

#### Verdict
Ship: yes — no blockers; the protocol-risk mitigations the plan demanded are all present, and
the remaining items are polish and coverage. First: S1 (one-binding UX fix), then S3 (moves +
theories), then decide S2's option A vs B explicitly rather than by default.

### Round 2 — external PR review (PR #84, boikoserhii84) — all items closed

Findings live in the PR comment; closures (branch also merged with `origin/main`, which already
contains this feature via PR #85's squash — the PR now carries only these fixes):

- [x] **M1. T800 global key-mode sweep erased DKS/MT/TGL/Snap** — `WriteOnce` now reads the
  current modes and patches **bit 7 only** (read-modify-write, aborting when the mode read fails
  rather than writing blind) — the same preservation guarantee the Smart Infinite path has.
  QA step 11 added for the vendor-configured-modes case.
- [x] **m1. Failed write invisible** — new `ActuationWriteFailed` flag + localized amber banner
  (en/uk) in the section explaining the slider snap-back; `UsbHidDeviceService.SetKeyActuationAsync`
  raises `NotifyError` on a clean-RPC `Success == false`.
- [x] **m2. One RPC failure hid the section for the page session** — the VM now distinguishes
  transport failure (client `null` → probe-pending state, section stays, natural retry on reopen)
  from the device's own `supported = false` (section hides).
- [x] **m3. Device-Info-sized write vs the family's full-reserve rule** — recorded as Open
  question 0 (factory `KEY_TRAVEL_TAB_SIZE` question) with the first-contact `0x38`/`0x39`
  ordering called out for QA.
- [x] **m4. Debounce serialized but did not collapse writes** — the semaphore queue replaced by an
  in-flight + queued-flag loop: any number of edits during a slow write collapse into one
  catch-up write with the current values (latest-wins, one flash commit).
- [x] **m5. Transport extraction untested** — the `ISmartInfiniteIo` seam (landed with PR #85's
  fixes) now covers the key-remap path too: internal transport constructor on
  `SmartInfiniteKeyMappingService` + `SmartInfiniteKeyMappingServiceTests` (scripted fake
  firmware: table read, full-384-byte single-slot RMW, key-count-zero refusal, misaligned-read
  discard). The Round 1 claim about "existing key-mapping tests" was wrong and is corrected by
  those tests actually existing now.
- [x] **m6. Verify mismatch retried as transient** — both actuation services return
  Verified/Rejected markers; "answered and disagreed" is final, only incomplete transfers retry.
- [x] **m7. Four slider copies in one view** — extracted `PercentSliderControl`
  (`src/ProoveGaming.App/Controls/`), pointer handling inside the control;
  `ActuationSectionView` uses it four times. (The seven pre-existing copies in Settings/DPI/RGB
  are follow-up debt, not this branch's.)
- [x] **m8. Pct vs fraction scales shared one name** — VM entry points renamed to
  `SetActuation*FromFraction(double fraction)`; the 0..100 percent properties keep "Pct" for the
  width converters, and both scales are documented at the seam.
- [x] **m9. Defaults dead in the contract** — now consumed: a "Default Settings" button
  (`RestoreActuationDefaultsCommand`) applies the device-reported factory values through the
  normal debounced write path.
- [x] **m10. RT fields read as 0.00 mm below the range minimum** — both services clamp read-out
  values into the advertised limits, so a never-RT board shows the range minimum instead of a
  thumb outside its own track.
- [x] **m11. Commit message under-describes T800** — the PR title/description now name all three
  boards; the branch history keeps the original commit (rewriting a pushed branch was not worth
  the churn).
