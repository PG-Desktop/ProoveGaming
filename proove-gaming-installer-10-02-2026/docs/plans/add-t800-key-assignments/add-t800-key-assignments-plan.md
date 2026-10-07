# Key Assignments — T800 Keyboard

## Status

Phase 0 complete and **R4 passed on hardware** — writes stick, and the single-key form is surgical.
That removed the whole-matrix read-modify-write from the design (see § Recommended approach).
Catalog, artwork layout, protocol, key-mapping service and App wiring are implemented; what remains
is Open Question #1 (write timing) and hardware QA.

## Goal

Give the T800 (Rongyuan RY5088, VID `12625` / `0x3151`, PID `20525` / `0x502D`) the same
interactive key-remapping surface Cyrex and Aress already have: the user opens **Key Assignments**,
clicks a keycap drawn on the rendered board, picks a target, and the firmware's key matrix is
rewritten. Everything above the protocol layer already exists and is model-agnostic — the work is a
T800 key catalog, a measured keycap layout, an RY5088 key-matrix service, and the dispatch wiring
that lets the existing UI find them.

The T800's RGB path is already implemented and hardware-verified
([`../t800-rgb/t800-rgb-plan.md`](../t800-rgb/t800-rgb-plan.md)), so the transport, the interface
selection and the checksum are settled and reusable. What is **not** settled is how this firmware
lays out a key record and numbers its slots — and that is the single thing this plan refuses to
guess.

## Scope

**In scope**

- Phase 0 reconnaissance against the connected device: key-record encoding, matrix geometry,
  slot → physical key map, and whether a write persists. Recorded in `notes.md`.
- `T800KeyCatalog` — slot → key name / label / HID usage, generated from the device's own factory
  table (the way `CyrexKeyCatalog` was).
- `T800ArtworkLayout` — measured keycap bounds in artwork viewBox coordinates, keyed by matrix slot.
- `T800KeyboardKeyMappingService` implementing `IKeyboardKeyMappingService` over
  `SET_KEYMATRIX (0x0A)` / `GET_KEYMATRIX (0x8A)`, plus dispatch in `DeviceKeyMappingServiceFactory`.
- App wiring: `HasInteractiveKeyboardOverlay`, catalog/layout dispatch, `KeyOverlayArt`,
  `OverlayCanvasWidth/Height`, and the no-override-layer branch T800 shares with Smart Infinite.
- Config, tests, glossary.

**Out of scope**

- **Macros.** T800 macros are currently UI-only (`SupportsMacros: true` with no service — see
  [`../t800-rgb/t800-rgb-plan.md`](../t800-rgb/t800-rgb-plan.md) § "Macros — UI only"). Binding a
  macro to a key *does* depend on this work, since the binding is a key-matrix record, but the
  **macro payload encoding is still undocumented** and uploading a library is a separate protocol
  (`0x0B`). Do not enable macro upload here. Once this plan lands, the remaining macro work is
  "decode `0x0B` and write the encoder" — the key-side half will already exist.
- Magnetic-axis travel / RT / DKS settings (`0x65`), profiles beyond 0, per-key travel layers
  beyond index 0.
- Unifying the write-timing exception (see Options B) across Smart Infinite and T800.

## Phase 0 — reconnaissance (do this before any code)

Two attempts at this device have already failed by guessing an undocumented field, and the failure
mode is silent: a wrong checksum, a wrong record size or a wrong slot count is discarded without an
ack, and `GetFeature` then returns the driver's cached copy of our own request, which reads as a
plausible reply. **Nothing in the change list below may be written until `notes.md` answers these.**

| # | Question | How |
|---|---|---|
| R0 | Does `GET_KEYMATRIX (0x8A)` answer at all? | Send `0x8A` with Profile 0, Type 255, Page 0, Index 0 and the Bit7 checksum. A reply whose body is **not** a byte-for-byte echo of the request means it parsed. |
| R1 | Record size — 3 bytes or 4? | The YC3123 action table implies 4 (`[09, Type, Index, 00]`, `[0A, 01, 00, 00]`, `[03, 00, B5, 00]`) and 512 / 4 = 128 slots. Confirm by finding the QWERTY run in the dump: consecutive alpha keys must appear at a constant stride. |
| R2 | Page geometry | The spec contradicts itself — `0x0A` says 512/56 = 10 pages, `0x8A` says "504/56, 9 pages" in one place and "8 pages of 64" in another. Measure by dumping pages until the data repeats or stops. |
| R3 | Slot → physical key | Match decoded HID usage codes against the known 65% layout. The artwork gives the expected key set and row structure independently (see below), so the two must agree. |
| R4 | Does a write stick? | Write one harmless slot (e.g. Right Ctrl → Right Alt) with `FlashEnable=1` on the final packet, re-read, then restore. Also watch `col05` for the Flash Write bracket (`05 0F 01` … `05 0F 00`), which the RGB work showed the firmware emits on an accepted persisted write. |
| R5 | Pacing | The ROYUAN notes warn the config endpoint stops answering until re-enumeration if page writes are pushed too fast. Establish a safe inter-packet delay for a full 10-page matrix write. |

**Exit criterion:** `notes.md` contains a decoded factory table with every slot's record and a
proposed slot → key name mapping. Coding starts only then.

Reconnaissance runs through the existing probe project, which is already wired for this device —
add a `keys` mode to `src/ProoveGaming.Device.Testing/T800LedProbe.cs` (or a sibling
`T800KeyProbe.cs`) rather than starting a new project.

### The artwork already corroborates the layout

A connected-component pass over the alpha channel of `PG_T801_keys.svg` finds **68 keycaps** in rows
of **15 / 15 / 14 / 14 / 10**. That is a 65% board, the same shape as Cyrex (68 real keys = 67
remappable + FN). This is an independent check on R3: if the decoded matrix does not yield 68 keys
in that row structure, the decode is wrong — do not proceed.

## Options considered

### Option A — mirror the Smart Infinite shape exactly (recommended)

A dedicated `T800KeyboardKeyMappingService`, a `T800KeyCatalog` generated from the factory table,
and a `T800ArtworkLayout` measured from the artwork — the same three-part structure Cyrex has, with
T800 taking the existing `IsSmartInfinite65Device` *behavioural* branch (no factory-vs-override
layer; "overridden" means the live record differs from the catalog default).

- Pros: the App-side branch for a no-override family already exists and is exercised by two
  shipping boards; RY5088 has exactly the same property (the live table *is* the state). Isolates
  RY5088 framing in one service, matching how every other keyboard family in this repo is handled.
- Cons: adds a fourth catalog + layout pair. The App's device-identity branching grows another arm
  (`IsT800Device`), which is already the messiest part of `DeviceDetailsViewModel.KeyAssignments`.

### Option B — generalise the identity branching first, then add T800

Refactor `HasInteractiveKeyboardOverlay` / `ArtworkKeyOverlayLayout` / `LoadKeyLayoutForDevice` onto
a per-board descriptor (catalog + layout + canvas + write-timing) resolved from one table, then
register T800 as a row in it.

- Pros: removes the repeated `IsXDevice` ladders; the next board is a data row.
- Cons: a refactor of the most branch-heavy file in the App, touching four shipping boards, bundled
  into a feature the user asked for. If the descriptor design is wrong it regresses Raster, T-Racer,
  Cyrex and Aress at once. The repo's own guidance is no speculative refactors. **Rejected** — but
  worth doing as its own `refactor-*` plan once T800 makes it the fifth board.

### Option C — reuse `SmartInfiniteKeyMappingService` with an RY5088 framing flag

- Pros: one service.
- Cons: RY5088 shares nothing byte-wise with Smart Infinite — different transport (feature vs
  output reports), different checksum, different record size, different session model (Smart
  Infinite brackets `0x10`/`0x11`; RY5088 has no session). The flag would be the entire method.
  Explicitly the trap the RGB factory comment already warns about. **Rejected.**

**Recommended: Option A.**

## Recommended approach

1. **Phase 0** as above. No production code until `notes.md` is complete.
2. **Generate, don't hand-write, the catalog.** A script in this plan folder decodes the Phase 0
   dump and emits `T800KeyCatalog.cs`, exactly as Cyrex's was generated from its factory table.
   Hand-transcribing 67 records is where a silent off-by-one enters.
3. **Measure, don't estimate, the layout.** A second script does the connected-component pass and
   emits `T800ArtworkLayout.cs`. **Coordinates must be in the cropped viewBox** `172 672 1742 600`
   (the T800 artwork was cropped this session — see
   [`../t800-rgb/t800-rgb-plan.md`](../t800-rgb/t800-rgb-plan.md) § "Artwork cropping"), so the
   image's own placement transform `translate(216.208 687.954)` must be reduced by the crop origin
   `(172, 672)` before emitting. Getting this wrong puts every overlay ~172 px to the right.
   The script must assert the per-row keycap counts (15/15/14/14/10) and abort rather than emit if
   the artwork changes.
4. **Service.** ~~Read-modify-write the whole matrix~~ — **superseded by R4.** RY5088 accepts a
   single-key write (`Type` = slot instead of 255), so one packet rewrites one key and provably
   touches nothing else. Reads are only for *showing* current assignments, never a precondition for
   writing, which deletes the abort-on-failed-read dance Smart Infinite needs. Pin Profile 0 and
   Index (layer) 0; set `FlashEnable=1` on the packet or the change is lost on replug. Verify by
   reading the slot back — the firmware does not ack a feature write.
5. **Wire the App** by adding `IsT800Device` alongside the existing identity properties, and letting
   T800 take the no-override behavioural branch.

## File-level change list

| File | Change | Intent |
|---|---|---|
| `docs/plans/add-t800-key-assignments/notes.md` | **new** | Phase 0 output: raw matrix dumps, decoded records, page geometry, pacing, and the slot → key mapping everything else is generated from. |
| `docs/plans/add-t800-key-assignments/decode-t800-matrix.py` | **new** | Decodes a Phase 0 dump and emits `T800KeyCatalog.cs`. Asserts 68 keys and a single FN slot; aborts otherwise. |
| `docs/plans/add-t800-key-assignments/measure-t800-keycaps.py` | **new** | Connected-component pass over `PG_T801_keys.svg`, emits `T800ArtworkLayout.cs` in cropped-viewBox coordinates. Asserts row counts 15/15/14/14/10. |
| `src/ProoveGaming.Device.Testing/T800KeyProbe.cs` | **new** | Phase 0 probe: dump the matrix, decode it, write-and-restore one slot. Read-only by default; the write step is opt-in. |
| `src/ProoveGaming.Device.Testing/Program.cs` | edit | Route a `keys` argument to the new probe, alongside the existing `auto` / `send` / `speed` / `learn`. |
| `src/ProoveGaming.Infrastructure/Classes/Ry5088Protocol.cs` | **new** | RY5088 framing shared by RGB and keys: report length, the two checksum variants, packet builders for `0x0A` / `0x8A`, record pack/unpack, page enumeration, and a `IsPlausibleKeyTable` guard. Extracted so the key service does not re-derive what `T800KeyboardRgbService` already knows. |
| `src/ProoveGaming.Infrastructure/Services/T800KeyboardRgbService.cs` | edit | Move its checksum + report constants onto `Ry5088Protocol` so there is one definition. Behaviour unchanged — this is the only reason to touch a hardware-verified file, and its tests must stay green. |
| `src/ProoveGaming.Infrastructure/Services/T800KeyboardKeyMappingService.cs` | **new** | `IKeyboardKeyMappingService` over `0x0A` / `0x8A`. Read-modify-write whole matrix; abort on implausible read; `FlashEnable` on the last packet only; `ProbeDefaultMatrixAsync` / `ProbeUserOverridesAsync` return null (no override layer), matching Smart Infinite. |
| `src/ProoveGaming.Infrastructure/Services/DeviceKeyMappingServiceFactory.cs` | edit | `T800` branch selecting the `mi_02` feature interface (same predicate `DeviceRgbServiceFactory` uses). |
| `src/ProoveGaming.App/Models/Keyboards/T800KeyCatalog.cs` | **new** (generated) | Slot → key name / label / category / HID usage / modifier mask, plus `IsT800(vid,pid)` and `IsT800(name)`. |
| `src/ProoveGaming.App/Models/Keyboards/T800ArtworkLayout.cs` | **new** (generated) | Slot → measured keycap bounds + `ViewBoxWidth/Height` (1742 × 600) + `LayoutFor(catalog)`, mirroring `CyrexArtworkLayout`. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` | edit | Add `IsT800Device`; include it in `HasInteractiveKeyboardOverlay`, `ArtworkKeyOverlayLayout`, `LoadKeyLayoutForDevice`, `OverlayCanvasWidth/Height`, and every branch currently keyed on `IsSmartInfinite65Device` **for the no-override-layer reason** (record-compare `IsOverridden`, immediate apply). Introduce a named property for that concept rather than widening `IsSmartInfinite65Device`, which would then be lying about the family. |
| `src/ProoveGaming.DeviceHost/appsettings.json` | edit | Nothing new required — `SupportsRgb` / `SupportsMacros` already set. Confirm no `SupportsButtonRemap` is needed (that is the mouse flag). |
| `tests/ProoveGaming.Infrastructure.Tests/Ry5088ProtocolTests.cs` | **new** | Framing, both checksum variants, record pack/unpack, page enumeration, plausibility guard. |
| `tests/ProoveGaming.App.Tests/T800KeyCatalogTests.cs` | **new** | Catalog integrity: 67 remappable keys, unique names, unique slots, FN excluded, every slot inside the matrix. |
| `tests/ProoveGaming.App.Tests/T800ArtworkLayoutTests.cs` | **new** | Every catalog key has bounds; every bound lies inside the 1742 × 600 viewBox; no overlaps beyond tolerance. |
| `tests/ProoveGaming.App.Tests/ArtworkKeyOverlayGateTests.cs` | edit | Extend the existing gate tests so T800 resolves an interactive overlay and its own artwork. |
| `docs/CONTEXT.md` | edit | Glossary: **T800 key matrix**, **RY5088 key record**, **T800KeyCatalog**, **T800ArtworkLayout**, **FlashEnable**. |

## Acceptance criteria

1. Given the T800 is connected and DeviceHost is running, when the user opens the device and selects
   **Key Assignments**, then the rendered T800 board appears with clickable overlays on all 67
   remappable keycaps and none on FN.
2. Given Key Assignments is open, when the user clicks the `Z` keycap, then that key is selected and
   the assignment-target list shows the current assignment for it.
3. Given a key is selected, when the user picks a different target and the change reaches the device
   (per the write-timing decided in Open Questions #1), then re-reading the matrix returns the new
   record for that slot and the physical key produces the new output.
4. Given a remap has been applied, when the keyboard is unplugged and replugged, then the remap
   survives — i.e. `FlashEnable` persisted it.
5. Given the device read fails or returns an implausible table, when a write is attempted, then no
   write is sent and the failure is logged — the board is never left with a partially written matrix.
6. Given a remapped key, when the user selects **Reset** on it, then the slot returns to its factory
   record and the key produces its original output.
7. Given the App renders the overlay, when the window is resized, then every keycap overlay stays
   registered to its drawn keycap at any scale (bounds are in viewBox units, not pixels).
8. Given any other keyboard (Raster, T-Racer, Cyrex, Aress), when Key Assignments is opened, then
   its behaviour is unchanged from before this plan.
9. `dotnet build ./src/ProveGaming.slnx` succeeds with no new warnings, and
   `dotnet test ./src/ProveGaming.slnx` is green — including the existing
   `T800KeyboardRgbServiceTests`, which must stay green through the `Ry5088Protocol` extraction.

## Test strategy

This is a **new feature** (`add-*`), so the tests below are non-negotiable coverage, not optional.
One part is a refactor and is treated accordingly.

**`tests/ProoveGaming.Infrastructure.Tests`**

- `Ry5088ProtocolTests` (new): packet framing for `0x0A` and `0x8A` (command byte, profile, type,
  page, len, FlashEnable, index at their documented offsets); **both** checksum variants against
  hand-computed vectors — reuse the frames already proven on hardware in the RGB work
  (`07 01 02 02 07 76 20 EE` → `0x68`, `87 00 …` → `0x78`) so the extraction is provably behaviour-
  preserving; record pack/unpack round-trip; page enumeration covering the full matrix exactly once
  with a correct final `Len`; `IsPlausibleKeyTable` rejecting an all-zero table, an all-`0xFF`
  table, and a table whose records carry unknown type bytes.
- `T800KeyboardRgbServiceTests` (existing, **must stay green unchanged**): these are the
  characterization tests for the `Ry5088Protocol` extraction. The extraction lands first and these
  must pass before the key service is written.

**`tests/ProoveGaming.App.Tests`**

- `T800KeyCatalogTests` (new): 67 remappable keys; `DevKeyName` unique; `MatrixIndex` unique and
  `< MatrixSlots`; FN absent; every modifier key carries a non-zero mask with usage `0x00` (the
  Cyrex lesson — modifiers are identified by the pair, never the usage alone).
- `T800ArtworkLayoutTests` (new): every catalog key resolves bounds via `LayoutFor`; every bound is
  inside `0 ≤ x, x+w ≤ 1742` and `0 ≤ y, y+h ≤ 600` — this is the assertion that catches the crop
  origin being applied wrongly; row grouping yields 15/15/14/14/10.
- `ArtworkKeyOverlayGateTests` (extend): T800 reports an interactive overlay and resolves its own
  artwork, not a borrowed one; the four existing boards are unchanged.

**Deliberately not covered by automated tests**

- Real HID I/O. There is no HID fake in this repo and the existing services are not written against
  one; `SmartInfiniteKeyMappingService` has no I/O-level tests either. The wire format is covered by
  `Ry5088ProtocolTests` on pure functions, and the I/O path is covered by the QA checklist and the
  Phase 0 probe.
- The Avalonia overlay rendering (UI-thread affinity, `Viewbox` scaling). Covered by QA step 6.
- Flash persistence across replug — physical, covered by QA step 7.

## QA (manual test steps)

1. Connect the T800 keyboard by USB cable. Start the Proove Gaming app. **Expected:** the T800
   appears on the home screen and its picture matches the keyboard you are holding.
2. Open the T800 and go to the **Key Assignments** section. **Expected:** a picture of the keyboard
   is shown, and every key on it can be clicked — except the **FN** key, which is not clickable.
3. Click the **Z** key. **Expected:** it becomes highlighted, and a list of things you can assign to
   it appears. The list shows what Z is currently set to.
4. Assign a different key to it — pick **B** from the list, then save/confirm if the app asks you to.
   **Expected:** the app reports success and shows Z as changed.
5. Open a text editor and press the physical **Z** key. **Expected:** the letter **b** is typed.
6. Resize the app window — make it much narrower, then much wider. **Expected:** the clickable
   boxes stay exactly on top of their keys at every size; nothing drifts or overlaps.
7. Unplug the keyboard, wait five seconds, plug it back in, and press **Z** again in the text editor.
   **Expected:** it still types **b** — the change survived the reconnect.
8. Go back to Key Assignments, select **Z**, and choose **Reset**. Press the physical **Z** key.
   **Expected:** it types **z** again.
9. Repeat steps 3–5 for the **Right Shift** key, assigning it to **Left Ctrl**. **Expected:** it
   behaves as Left Ctrl (e.g. holding it and pressing A selects all). This checks modifier keys,
   which are stored differently from letters.
10. Connect a **Cyrex** or **Aress** keyboard (if available) and open its Key Assignments.
    **Expected:** it works exactly as it did before — this change must not affect other keyboards.
11. Throughout all of the above: the app must never freeze, stutter, or show a spinner that never
    finishes.

## Review focus

`async/await`, `null-safety`, `resource disposal`, `thread-safety`.

- **`null-safety`** is the highest-stakes one here: `ReadKeyMatrixAsync` returning null must be
  treated as "do not write", never as "the table is empty". Writing a fabricated table back would
  scramble every key on the board — the exact hazard `SmartInfiniteKeyMappingService` documents.
- **`async/await`**: HID I/O must not run on the Avalonia dispatcher; the existing services wrap
  blocking HidSharp calls in `Task.Run` and flow a token.
- **`resource disposal`**: every `HidStream` opened per operation is disposed on all paths,
  including the abort paths.
- **`thread-safety`**: `IHidDeviceProvider` / `IDeviceInfoProvider` are singletons holding device
  state shared with the device-I/O thread.

## Performance & quality

No per-frame hot path — key remapping is a user-initiated action, not a poll loop, and nothing here
runs on the RGB animation timer.

Three constraints that do matter:

- **Do not block the UI thread.** A full matrix read-modify-write is ~20 HID transfers with
  deliberate pacing between them; at the R5 delay this is on the order of hundreds of milliseconds.
  It must run off the dispatcher, and the UI must stay responsive while it does.
- **Pacing is a correctness constraint, not a tuning knob.** The ROYUAN notes report the config
  endpoint wedging until re-enumeration when page writes are pushed too fast. Whatever R5 measures
  is a floor; do not "optimise" it away.
- **`Ry5088Protocol` builders should not allocate per packet in a loop** beyond the report buffer
  itself — reuse a single buffer across a page loop the way the existing table writers do. This is
  cheap to get right and matches the surrounding code, not a micro-optimisation.

## Risks

- ~~**The record encoding is 4 bytes, not 3.**~~ **Resolved by R1.** Records are 4 bytes but the
  fourth is `0x00` throughout the factory table, so they ride the App's 3-byte `KeyMatrixRecord`
  unchanged. No cross-board model change.
- ~~**A wrong write scrambles the keyboard.**~~ **Largely resolved by R4.** RY5088 accepts a
  single-key write (`Type` = slot), verified to change that slot and nothing else, so there is no
  whole-table write to get wrong and a failed read can no longer corrupt untouched keys. The residual
  risk is a wrong *slot* number, which the generated catalog and its tests guard.
- **Artwork coordinates are tied to this SVG revision.** The crop landed this session; a future
  re-export silently invalidates every bound. Mitigated by the generator asserting row counts and by
  `T800ArtworkLayoutTests` bounding every rect to the viewBox.
- **Touching `T800KeyboardRgbService` for the protocol extraction** risks regressing a
  hardware-verified path. Mitigated by landing the extraction first with the existing RGB tests
  unchanged as the characterization suite.
- **Firmware may refuse remapping entirely**, the way Rampage does (reports 0 remappable keys and
  discards writes while acking them). R4 is the gate: if writes do not stick, stop and report rather
  than shipping a UI that silently does nothing.

## Open questions

1. **Write timing — immediate or staged?** The repo's current model is [[Staged edit]]: edits mark
   the preset dirty and hardware is written on Save Preset. Smart Infinite key remaps are an
   explicit exception that writes immediately. T800 shares Smart Infinite's *protocol* property (no
   override layer, live table is the state) but not necessarily its *timing* choice. Recommendation:
   follow Smart Infinite (immediate) for launch, because that branch is proven for a no-override
   family; unifying onto staged is a follow-up. **Needs a decision before `/coder` runs**, because it
   changes acceptance criterion 3 and QA step 4.
2. **Does the fourth record byte carry meaning?** Answered by R1; determines whether
   `KeyMatrixRecord` stays a 3-byte type (see Risks).
3. **Profile handling.** The firmware has 8 profiles and the App has no profile concept for
   keyboards. Confirm pinning Profile 0 is correct and that the device boots on Profile 0 — if it
   can boot on another, a remap written to 0 would appear not to work.

## Review findings

### Round 1 — Ship: no

#### Plan conformance

Plan: `docs/plans/add-t800-key-assignments/add-t800-key-assignments-plan.md`
(reviewed delta: `origin/main..HEAD`, 4 commits, 41 files — local `main` is stale at `3d9605b`, so
`main...HEAD` wrongly pulls in upstream #76-79).

- **Implemented as planned:** Phase 0 + `notes.md`; both generators; `T800KeyProbe` + `Program.cs`
  routing; `Ry5088Protocol`; `T800KeyboardKeyMappingService`; factory dispatch on `mi_02`;
  `T800KeyCatalog` / `T800ArtworkLayout` (generated, not hand-written); App wiring with
  `IsT800Device` and the new `HasNoKeyOverrideLayer` concept; `Ry5088ProtocolTests`,
  `T800KeyCatalogTests`, `T800ArtworkLayoutTests`, extended `ArtworkKeyOverlayGateTests`. The
  `T800KeyboardRgbService` extraction kept its 37 characterization tests green.
- **Deviates from plan (design, accepted):** the service does a single-key write instead of the
  whole-matrix read-modify-write — correct, driven by R4, and the plan text was updated to match.
- **Deviates from plan (test strategy, plan now stale):** the plan asks `T800KeyCatalogTests` to
  assert "every modifier key carries a non-zero mask with usage `0x00`". The code asserts the
  opposite, because R1 found T800 stores modifiers as plain usages `0xE0`-`0xE7`. The code is right;
  the Test strategy paragraph is stale and now contradicts the shipped tests.
- **Missing:** `docs/CONTEXT.md` glossary entries (change-list row 15) — see B1.
- **Scope drift:** the home-card fade work (`CardArtOpacity` / `CardArtDimOpacity`,
  `RgbKeyboardView.DimOpacity`, `DoubleCard.axaml`, `DeviceCardArtOpacityTests`) belongs to no plan —
  neither this one nor `t800-rgb`. A real fix with tests, but it is undocumented.
- **Acceptance criteria:** 1, 2, 7, 8, 9 met (solution builds, 889 tests green, no new warnings from
  these files). 3 partially confirmed by the author on hardware. **4 (replug persistence) and 6
  (Reset) are unverified** — QA steps 7 and 8 have not been run. 5 is now largely N/A: single-slot
  writes mean a failed read can no longer leave a partial matrix.
- **Open question #1 (write timing) was never answered**, and the code chose *immediate* by
  inheritance (T800 falls into `HasNoKeyOverrideLayer` → `ApplyAssignmentTargetAsync`). The plan says
  this "needs a decision before `/coder` runs". It works, but the decision should be recorded rather
  than arrived at by accident.

#### Code

- [x] **B1. Mandatory glossary entries missing** — `docs/CONTEXT.md` — Blocker — **fixed**
  *Closed:* added **FlashEnable**, **HasNoKeyOverrideLayer**, **RY5088 key record**,
  **T800 key matrix**, **T800ArtworkLayout** and **T800KeyCatalog**, each in its alphabetical
  section (Option A), with the RY5088 entries grouped together.
  **Problem:** this change introduces `T800KeyCatalog`, `T800ArtworkLayout`, the RY5088 key-record
  encoding, the T800 key matrix and `HasNoKeyOverrideLayer`; none is in the glossary. The delta adds
  only the RGB-era terms (`RY5088`, `AP colour slot`, …).
  **Why it blocks:** `CLAUDE.md` states the documentation rules are "mandatory for every
  contribution" and that a new term "must" be defined in `docs/CONTEXT.md` in the same change. This
  plan's own file-level change list carries it as a row. Merging leaves the repo in violation of its
  own contribution rule, and the next reader meets `HasNoKeyOverrideLayer` with no definition.
  **Solution options:**
  - Option A (correct): add one-line entries for **T800 key matrix**, **RY5088 key record**,
    **T800KeyCatalog**, **T800ArtworkLayout**, **FlashEnable** and **HasNoKeyOverrideLayer**, each in
    its alphabetical section rather than as one block.
  - Option B: none — this is a repo rule, not a preference.

- [ ] **S1. `IsPlausibleKeyTable` accepts an all-`0xFF` read** — `src/ProoveGaming.Infrastructure/Classes/Ry5088Protocol.cs:150` — Should-fix
  **Problem / why it matters:** the guard is `map.Count >= 32`, and `ParseMatrix` skips only all-zero
  records — so a buffer of `0xFF` decodes into **128** slots and passes. An all-`0xFF` read is a
  classic HID failure mode, and the plan's Test strategy explicitly named "an all-`0xFF` table" as a
  case this guard must reject. Concretely: a failed read yields `FF FF FF FF` per slot → 128 records
  of type `0xFF` → the overlay renders 128 fabricated assignments, and because `IsOverridden`
  compares each record against the catalog, **every key displays as remapped**. Not corrupting —
  writes are built from the catalog, never from this map — but the UI lies about the board.
  **Fix:** reject records whose type is not one this family uses (`0x00` plain, `0x0A` special,
  `0x03` multimedia, `0x09` macro) before counting, or reject an all-`0xFF` buffer outright. Cover
  the all-zero and all-`0xFF` cases in `Ry5088ProtocolTests`.

- [x] **S2. No serialization on the shared `mi_02` interface** — `src/ProoveGaming.Infrastructure/Services/T800KeyboardKeyMappingService.cs:179` — Should-fix — **fixed**
  *Closed:* independently raised as **M3** in PR #80. Added `Ry5088DeviceGate` (per-`DevicePath`
  `SemaphoreSlim`, modelled on `SmartInfiniteDeviceGate`), taken by `SetLedColorAsync`,
  `ReadKeyMatrixAsync` and `SetKeyMappingAsync` — the three public entry points, so no nested
  acquisition and no deadlock. `SetLedColorAsync` became genuinely `async` to await it. Covered by
  `Ry5088DeviceGateTests` (waiting, per-path isolation, double-dispose, cancellation, 24-way
  contention). Note it removes retries *caused by* a stolen reply, but does not cap retries within
  one call — that is PR finding m4 and still open.
  **Problem / why it matters:** `DeviceRgbServiceFactory` and `DeviceKeyMappingServiceFactory` select
  the **same** `mi_02` interface and neither takes a lock. `SmartInfiniteDeviceGate` exists for
  exactly this hazard on the other family; RY5088 has no equivalent. Every transfer here is a
  `SetFeature` followed by a `GetFeature`, and that pair is not atomic against another handle.
  Concretely: the user is on Key Assignments (a matrix read is 8 request/reply pairs) while an RGB
  apply runs — `T800KeyboardRgbService.LogReadBack` issues `SetFeature(0x87)` + `GetFeature` on the
  same interface. The key read's `GetFeature` can return the LED reply, that page decodes as garbage,
  and the read is rejected → `KeyAssignmentReadFailed`, so the overlay falls back to factory defaults.
  During write-verify it is worse: a stolen reply fails the verify, `WithRetry` re-writes, and each
  retry is another **flash** write.
  **Fix:** a per-`DevicePath` `SemaphoreSlim` shared by the RY5088 RGB and key services, in the shape
  of `SmartInfiniteDeviceGate`. It also bounds the retry-driven flash writes.

- [ ] **S3. Binding a macro to a T800 key silently does nothing** — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs:1294` — Should-fix
  **Problem / why it matters:** T800 ships `SupportsMacros: true` with no macro service (accepted in
  the `t800-rgb` plan as "UI only"). Before this branch that was unreachable — binding a macro needs a
  selected physical key, and T800 had no interactive overlay. **This branch makes it reachable.** The
  flow is safe (`SetMacros` returns `Success = false` → `if (!uploaded) return;` fires before any key
  write, so no record and no `key_macro_bindings` row), but the user gets **no feedback**: they pick a
  macro for a key and nothing happens.
  **Fix:** gate the macro target list on a real macro service, or surface the failed upload. Cheapest
  honest option: set `SupportsMacros: false` for T800 until the `0x0B` encoder exists, so the section
  disappears instead of presenting a dead end.

- [ ] **S4. Nothing pins the per-device key type byte** — `tests/ProoveGaming.App.Tests` — Should-fix
  **Problem / why it matters:** `KeyboardTypeByte` returning `0x00` for T800 and `0x10` for every
  other board is the most fragile new invariant — it was a live bug during implementation, and
  getting it wrong writes a record the firmware ignores *and* makes every key read as overridden. No
  test covers it.
  **Fix:** `PhysicalKeys` and `LoadKeyLayoutForDevice()` are public and `SetDevice` already calls the
  latter (`DeviceDetailsViewModel.cs:868`), so a test in the style of `ArtworkKeyOverlayGateTests` can
  assert `vm.PhysicalKeys.First().TypeByte` is `0x00` for T800 and `0x10` for Cyrex/Raster.

#### Tests

- [ ] Add the all-zero and all-`0xFF` plausibility cases the plan's Test strategy named but that are
  not covered (drives S1) — `tests/ProoveGaming.Infrastructure.Tests/Ry5088ProtocolTests.cs`
- [ ] Per-device type byte via `PhysicalKeys[..].TypeByte`, T800 vs Cyrex (drives S4) —
  `tests/ProoveGaming.App.Tests`
- [ ] No coverage of `T800KeyboardKeyMappingService` itself. The slot-range guard in
  `SetKeyMappingAsync` runs before any I/O and is the one branch reachable without hardware; the rest
  needs a HID fake this repo does not have, consistent with `SmartInfiniteKeyMappingService` having
  none. Acceptable — recorded so it is a decision rather than an oversight.

#### Nits

- `Thread.Sleep(PostWriteSettleMs)` sits inside the `using`, holding the HID handle 300 ms longer
  than needed — `src/ProoveGaming.Infrastructure/Services/T800KeyboardKeyMappingService.cs:146`
- `ReadKeyMatrixAsync(int maxSlot = 128)` ignores `maxSlot`, as `SmartInfiniteKeyMappingService`
  does — consistent, but silently so — `T800KeyboardKeyMappingService.cs:73`
- A failed verify retries the whole write up to 3x, i.e. up to three flash writes for one user
  action — `T800KeyboardKeyMappingService.cs:99`

#### What's done well

- **Phase 0 was actually honoured.** The encoding, page geometry and slot map were measured rather
  than guessed, and the artwork cross-check (15/15/14/14/10 agreeing between the firmware table and
  the keycap pixels) is what makes the slot assignment trustworthy. That is the discipline the two
  earlier failed attempts at this device lacked.
- **R4 was used to simplify, not just to confirm.** Finding the single-key write deleted the
  whole-matrix read-modify-write, the abort-on-failed-read dance and the pacing constraint in one
  move — the design got smaller after contact with hardware.
- **Both data files are generated by strict, aborting generators** that fail loudly on a changed row
  structure or a moved viewBox instead of emitting plausible-but-wrong numbers.
- **`HasNoKeyOverrideLayer` was introduced instead of widening `IsSmartInfinite65Device`**, keeping
  the Smart-Infinite-only macro-repeat branch honest — exactly what the plan asked for.
- **The protocol extraction landed against the RGB characterization tests**, with hardware-derived
  byte vectors pinned in `Ry5088ProtocolTests`, so the checksum now has one definition rather than two.

#### Verdict

Ship: no — one blocker, and it is a ten-minute fix. The engineering is sound and hardware-verified;
what is missing is the mandatory glossary that both the repo's contribution rules and this plan's
change list require. S1 (all-`0xFF` reads accepted) and S2 (unserialized shared `mi_02`) are real but
degrade display rather than corrupt, so they are fair follow-ups.

First: 1) B1 — add the glossary entries. 2) S1 — tighten `IsPlausibleKeyTable` plus its two tests.
3) Record the Open Question #1 decision (immediate writes) in the plan, then run QA steps 7 and 8 to
close acceptance criteria 4 and 6.
