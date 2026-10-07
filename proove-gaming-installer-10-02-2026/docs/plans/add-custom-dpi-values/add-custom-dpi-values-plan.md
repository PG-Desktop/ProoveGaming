# Custom DPI stage values for BLAZE and TAIPAN (PG-660)

Ticket: PG-660 · Branch: `feat/PG-660-Custom-DPI-setting`

## Goal

Today the DPI section shows a fixed ladder of seven values that come from the device catalog
(`800, 1200, 2400, 3600, 4800, 6000, 12000`) and the user can only pick which of them is active.
This plan makes each ladder entry — each **DPI point** — user-editable: the radio list stays as the
active-stage selector, and gains a customization block below it that edits the selected point's DPI
value with a numeric input and a slider, in steps of 50 DPI, bounded by the device's own minimum and
maximum. The user can also **remove** a point and **add** a new one, within the slot count the device
supports (1–8 on both mice). Per-point indicator colours stay fixed constants and get no editing UI:
TAIPAN's are fixed in firmware and unwritable, and BLAZE's are display-only in the App.

Values are already preset-scoped and already sync to the account — no backend work is needed (see
[No API change](#no-api-change-required) below).

## Scope

**In:**

- Per-point custom DPI values for BLAZE and PG TAIPAN, persisted per preset, written to hardware.
- Add / remove DPI points, bounded by per-device slot min/max.
- New customization block in the DPI section: slot-number header, step caption, value input, slider.
- Per-device DPI bounds (`min`, `max`, `step`) and slot bounds (`min`, `max`) declared in the
  DeviceHost catalog and carried to the App over gRPC.
- Stale-key cleanup in persistence so a removed point does not resurrect.
- Both language resource files (`en` / `uk`).

**Out:**

- Per-point indicator colour editing (impossible on TAIPAN, display-only on BLAZE).
- Per-axis (X/Y) independent DPI, and the TAIPAN "doubled" flag — see
  [Risks](#risks); this plan deliberately stays inside the DPI band where the two candidate
  encodings agree.
- Any device other than BLAZE and PG TAIPAN. Keyboards have no DPI section; other mice added later
  inherit the mechanism by declaring the new catalog fields.
- Backend / API changes. None are required.
- Changing which point is active by any mechanism other than the existing radio + physical DPI
  button.

## Background: what the hardware allows

Established by reading the protocol docs and the two encoder classes; these numbers are the
constraints the plan is built on.

| | BLAZE (X43 / Easen) | PG TAIPAN (Telink) |
|---|---|---|
| Max slots | 8 (`X43DpiTable.MaxStages`; block 0 byte 4 is the stage count, clamped 1–8) | 8 (`TaipanDpiTable.MaxStages`; table byte 0 is the stage count; MsConfig `0x0011` active stage range `0x00`–`0x07`) |
| Min slots | 1 (the count byte is clamped to ≥ 1) | 1 (unused slots are written disabled) |
| Value encoding | raw CPI, 16-bit little-endian, written twice (primary table at byte 9, mirror at byte 25) | register = **CPI / 50**, 16-bit little-endian, mirrored to X and Y |
| Documented min/max | **none** — the vendor left "Minimum DPI / Highest DPI" blank in `X43 Standard Mouse Function Specifications 3311.xlsx` | none stated |
| Unused slots | must hold a **valid non-zero** DPI (`X43DpiTable.UnusedStageDpi` = 2000); a zero entry makes the firmware reject the whole param_1 write and takes the LED down with it | written with the enable byte cleared |

Two consequences worth stating up front:

1. **50 is TAIPAN's native quantum.** Its firmware stores CPI in 50-DPI units, so a step of 50 is
   exactly one register increment — the requested step is free on that device. BLAZE stores raw CPI,
   so 50 is structurally fine there too, but whether its 3327-class sensor actually resolves 50-DPI
   increments (rather than snapping to 100) is unverified — a QA step, not a blocker.
2. **Capping TAIPAN at 12000 keeps the unresolved encoding ambiguity moot.** Vendor Q&A T7
   (`docs/mouse/taipan/taipan-vendor-qa-2026-07-30.md`) leaves two candidate layouts for the DPI
   table: `[enable, X_L, X_H, Y_L, Y_H]` (what `TaipanDpiTable` implements) versus
   `[enable, X_value, X_double, Y_value, Y_double]`. While the register fits in one byte — i.e. while
   DPI ≤ 12750 — the high byte is `0x00` and **both layouts encode and decode to the same DPI**. The
   current ladder tops out at 12000 (register 240), which is why the ambiguity has never bitten. A
   max of 12000 keeps every user-settable value inside that band, so this feature does not depend on
   T7 being resolved.

## No API change required

Confirmed by tracing the whole path; recorded here so it is not re-litigated during implementation.

- `PresetConfigurationBuilder.Build` serializes `configuration.settings` as the raw
  `Dictionary<string, double>` returned by `GetPresetSettings(presetId)` — no allow-list, no typed
  DTO (`src/ProoveGaming.App/Services/PresetConfigurationBuilder.cs:85`).
- `PresetConfigurationApplier.ApplySettings` reads back **every** numeric property of that object
  (`src/ProoveGaming.App/Services/PresetConfigurationApplier.cs:80-96`), and the response DTO holds
  `configuration` as a `JsonElement` so unknown keys survive binding.
- `preset_settings` is a whole-key-space copy of `device_settings` in both directions with no key
  filter (`src/ProoveGaming.App/Services/LocalStorageService.cs:3698-3710` capture,
  `:3538-3545` restore).

DPI point values already live under the `dpi.stage.{i}.{value|r|g|b}` keys, so they already travel to
the server inside `configuration.settings` whenever a preset is pushed — this feature only changes
where those numbers come from. The one backend unknown (open question 2 of
[`add-api-v2-sync`](../add-api-v2-sync/add-api-v2-sync-plan.md), *"is `configuration` an opaque
blob?"*, unanswered since 2026-08-25) is covered by a QA step here rather than by new work.

## Options considered

### Option A — client-only ladder, catalog declares the bounds (recommended)

Bounds (`DpiMin`, `DpiMax`, `DpiStep`, `DpiSlotMin`, `DpiSlotMax`) become per-device catalog fields
in the DeviceHost `AllowedDevices` section, carried to the App over the existing gRPC catalog
message. The App edits an in-memory ladder, persists it through the existing
`SaveDpiPresetsAsync`, and writes it with the existing `SetDpiStages` RPC, which both device services
already implement for a variable-length list.

- **+** No new RPC, no new device protocol work; the write path is already proven on both mice.
- **+** Adding a third mouse later costs a catalog entry, not a code change — the same shape
  `DpiStages` already has.
- **+** Bounds live next to the ladder they bound, so they cannot drift apart.
- **−** Touches the proto (five new fields) and therefore both sides of the gRPC seam.

### Option B — hardcode the bounds in the App next to `DefaultDpiStages`

A static per-device table in `DeviceDetailsViewModel` beside the existing `DefaultDpiStages` /
`TaipanDpiStageColors` constants.

- **+** Smallest diff; no proto change, no DeviceHost change.
- **−** Splits one device's capability description across two projects: the ladder comes from the
  catalog, its bounds from a C# constant keyed by device name. The existing
  `string.Equals(device?.Name, "PG TAIPAN")` palette switch is exactly this smell, and it already
  forces a code edit per device.
- **−** A device whose bounds differ then needs an App release, while everything else about it is
  configuration.

### Option C — read the bounds from the device

Query the sensor's supported range over HID at connect.

- **−** Not available: BLAZE's feature reads return all zeros and it exposes no 64-byte input
  endpoint, so block 0 is unreadable; TAIPAN's `0x42` DPI-table read is unreliable over the dongle
  and its layout is the unresolved T7 question. There is nothing to read.
- Rejected as infeasible, not as undesirable.

## Recommended approach

**Option A.** Five new catalog fields describe the DPI capability per device; the App renders and
edits within them.

### Values shipped in the catalog

| Field | BLAZE | TAIPAN | Rationale |
|---|---|---|---|
| `dpi_min` | 200 | 200 | Conservative; the historical BLAZE stage-0 fallback was 200, so the ladder is known to reach there. Vendor minimum is undocumented for both. |
| `dpi_max` | 12000 | 12000 | The top of the shipped ladder on both devices, and — for TAIPAN — inside the ≤ 12750 band where the T7 encoding ambiguity cannot change the written value. |
| `dpi_step` | 50 | 50 | TAIPAN's native register unit. BLAZE accepts any raw CPI. |
| `dpi_slot_min` | 1 | 1 | Both firmwares accept a one-entry table. |
| `dpi_slot_max` | 8 | 8 | Both tables hold 8 stages. |

`0` means "unset" on every one of these, matching the convention `default_dpi` and friends already
use — an older DeviceHost that never sets them leaves the App on its built-in fallbacks
(`200 / 12000 / 50 / 1 / 8`), so the App does not break against a stale host.

### UI

The section keeps its current order (Recommendations button → DPI points → Polling Rate → Lift-off)
and gains the point list's delete affordance, the add button, and the customization block between the
point list and Polling Rate.

```
 [ RECOMMENDATIONS ]

 (•) ● 800      ( ) ● 1200     ( ) ● 2400        ← delete icon appears on row hover, right-aligned
 ( ) ● 3600     ( ) ● 4800     ( ) ● 6000
 ( ) ● 12000

 + Add point                                     ← pgLinkButton; hidden at dpi_slot_max

 Point 1                              [ 800 ]    ← 18px SemiBold white  |  value input, right-aligned
 Step: 50 dpi                                    ← 14px #B2BEBCEA
 ━━━━━●─────────────────────────────             ← PercentSliderControl, 50-DPI quantized

 Polling Rate
 …
```

- **Point rows.** Unchanged radio visual (`RadioButton.dpiRadioOption`) and constant colour dot, plus
  a delete button on the right reproducing the macro-step pattern from
  `MacroRecorderStepsView.axaml`: `Delete.svg` tinted `#6357FF` inside a 28×28 panel with the
  octagon `deleteHoverFrame`, `Opacity=0` until the row is hovered. The row is a `UniformGrid` cell,
  so the icon is right-aligned inside the cell rather than at the section edge.
- **`+ Add point`.** `Classes="pgLinkButton"` — the shared style the installer's in-progress Cancel
  button uses (`src/ProoveGaming.Common/Styles/SharedActionButtonStyles.axaml:61`), already consumed
  by App views, so nothing needs extracting. Collapsed (`IsVisible=false`) when the ladder is at
  `dpi_slot_max`. A new point is appended with the device's default DPI value, clamped into range,
  and becomes the selected point.
- **Customization block.** Targets the **currently selected** point, so the radio does double duty as
  "which point is active" and "which point am I editing". Layout mirrors the Actuation section's
  title/subtitle/slider stack (`ActuationSectionView.axaml:90-104`): a `ColumnDefinitions="*,Auto"`
  grid with the point title on the left and the value input on the right, the step caption beneath
  the title, then the slider.
- **Value input.** Reuses the closed-state hex-input chrome — `Border.rgbHexInputHost` (height 40,
  1px `#6357FF` border, `12,0` padding) wrapping an editable `TextBox`. Those styles currently live
  inside `RgbColorPickerControl.axaml`; this is the second consumer, so per CLAUDE.md they move to
  `App.axaml` and both call sites reference them there. No fixed `Width` — `MinWidth` plus padding,
  so the Ukrainian caption cannot push the box out of shape.
- **Slider.** `controls:PercentSliderControl` with a `FractionChanged` handler, i.e. the same control
  and visual as the RGB brightness and Actuation sliders. The handler maps the 0–1 fraction across
  `[dpi_min, dpi_max]` and snaps to `dpi_step`.
- **No colour UI anywhere in the section.** The palettes stay constants and gain an 8th entry so an
  added eighth point does not silently reuse the seventh's colour.

### Editing, clamping and writes

- The input commits on `Enter` and on lost focus, not per keystroke — a keystroke must never reach
  the device. Non-numeric or out-of-range text reverts to the current value on commit.
- Every committed value is clamped to `[dpi_min, dpi_max]` and rounded to the nearest `dpi_step`
  multiple, in one shared helper used by the input, the slider and the add path, so the three cannot
  disagree.
- A commit persists the whole ladder via `SaveDpiPresetsAsync`, then calls
  `StageChangeAndApply(DeviceApplyAspects.DpiStages | DeviceApplyAspects.Settings)` — marking the
  preset dirty and letting the existing debounce coalesce the write. This preserves the single
  all-blocks write BLAZE requires; a burst of separate per-point writes is exactly the shape that
  bricked the buttons (`docs/mouse/blaze/engineering-notes.md`).
- **Removing a point re-indexes the ladder.** `StageIndex` is positional and the device table is
  positional, so removing point 3 of 7 shifts 4–7 down. Two follow-ons: the active stage must be
  clamped to `count - 1` (and re-persisted when it moves), and the stale
  `dpi.stage.{count..7}.*` rows must be deleted — `SaveDpiPresetsAsync` is upsert-only today, so a
  7→5 shrink would leave rows 5 and 6 behind and `LoadDpiPresetsAsync`, which enumerates whatever
  indices it finds, would resurrect the removed points on the next open.
- Removing is refused at `dpi_slot_min` (the last point's delete button is hidden).

## File-level change list

### Contract and catalog

| File | Intent |
|---|---|
| `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto` | Add `dpi_min = 41`, `dpi_max = 42`, `dpi_step = 43`, `dpi_slot_min = 44`, `dpi_slot_max = 45` to `DeviceCatalogEntry`, each documented as "0 = unset → App falls back". (Planned as 40-44 when 39 was the highest; `main` merged `supports_extended_keys = 40` first, so the unreleased side moved — corrected 2026-09-14.) |
| `src/ProoveGaming.Infrastructure/Classes/DeviceMap.cs` | Add the five nullable `int?` properties to the catalog device record, beside `DpiStages`, with a comment stating the unit of each. |
| `src/ProoveGaming.Infrastructure/Classes/DeviceGroupConfig.cs` | Mirror the five fields on the expanded per-transport config. |
| `src/ProoveGaming.Infrastructure/Classes/DeviceMapExpander.cs` | Copy the five fields when expanding a catalog device into its per-transport configs. |
| `src/ProoveGaming.DeviceHost/Services/HidDeviceMapRegistry.cs` | Map the five fields onto the outgoing `DeviceCatalogEntry`, following the existing `if (m.DpiStages is { Count: > 0 })` guard style. |
| `src/ProoveGaming.DeviceHost/appsettings.json` | Declare the values for BLAZE and PG TAIPAN per the table above. |

### App — state and view models

| File | Intent |
|---|---|
| `src/ProoveGaming.App/ViewModels/DeviceCardViewModel.cs` | Carry the five bounds alongside `DpiStages`, defaulting to the built-in fallbacks when the catalog leaves them unset. |
| `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` | Populate them from the catalog entry next to the existing `card.DpiStages = [.. entry.DpiStages]` (~line 2208). |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DpiPresetViewModel.cs` | Make `DpiValue` settable through an explicit internal setter used by the edit path (keep `UpdateFromPersisted` for the restore path); add `SlotNumber` (1-based, for the header), `CanRemove`, and `RemoveCommand`. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Dpi.cs` | **New partial.** Holds the whole feature: `SelectedDpiPoint`, `CustomDpiText`, `CustomDpiPct`, `DpiStepLabel`, `PointTitle`, `CanAddDpiPoint`, `AddDpiPointCommand`, `RemoveDpiPointCommand`, the shared clamp/quantize helper, and the persist-then-`StageChangeAndApply` commit path. Kept out of `.Settings.cs` so the existing partial does not grow a second responsibility. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs` | Extend `DpiStageColors` and `TaipanDpiStageColors` to 8 entries; have `RebuildDpiPresets` seed `CanRemove` and keep the customization block bound to the selected row. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Settings.cs` | `RestoreDpiSectionDefaultsAsync` must also restore the catalog's **point count**, not just the values, and reset the selected point — today it rebuilds from the catalog, which already does this, so the change is to persist the shrink (see the storage change below) and re-clamp the active stage. |

### App — persistence

| File | Intent |
|---|---|
| `src/ProoveGaming.App/Services/LocalStorageService.cs` | Add `ReplaceDeviceSettingsWithPrefix(owner…, prefix, values)` — delete every `device_settings` row whose key starts with `prefix`, then insert `values`, in **one** transaction so the single-commit property of the existing batched write is preserved. |
| `src/ProoveGaming.App/Services/SettingsRepository.cs` | `SaveDpiPresetsAsync` switches to the new replace-by-prefix call with prefix `dpi.stage.`, so a shrunk ladder cannot leave orphan rows. |

### App — view

| File | Intent |
|---|---|
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/DpiSectionView.axaml` | Add the per-row delete button, the `+ Add point` link button, and the customization block (header grid + step caption + `PercentSliderControl`). |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/DpiSectionView.axaml.cs` | Add the slider's `FractionChanged` handler and the value `TextBox` handlers: `GotFocus` (records the row being edited) plus the `Enter` / `LostFocus` commits. |
| `src/ProoveGaming.App/App.axaml` | Host the extracted `Border.rgbHexInputHost` / editable-hex-input styles under `Application.Styles` so the DPI input and the RGB picker share one definition. |
| `src/ProoveGaming.App/Controls/RgbColorPickerControl.axaml` | Drop the local copies of the styles moved to `App.axaml`; keep any setter genuinely unique to the picker as a variant class. |

### Localization (both files, mandatory)

| File | Intent |
|---|---|
| `src/ProoveGaming.App/Resources/Strings.en.axaml` | `DeviceDetails_Dpi_AddPoint`, `DeviceDetails_Dpi_PointFormat` ("Point {0}"), `DeviceDetails_Dpi_StepFormat` ("Step: {0} dpi"). |
| `src/ProoveGaming.App/Resources/Strings.uk.axaml` | The same three keys in Ukrainian. `LanguageResourceParityTests` fails the build if either side is missed. |

### Docs

| File | Intent |
|---|---|
| `docs/CONTEXT.md` | Define **DPI point** (one user-editable entry of a mouse's DPI ladder; 1–8 per device) and **DPI step** (the quantum a DPI value snaps to; 50 on both mice, TAIPAN's native register unit). |
| `docs/mouse/blaze/dpi-stage.md` | Note that the ladder is now user-defined and that the 7-gear default is a starting point, not an invariant. |
| `docs/mouse/taipan/protocol.md` | Record the ≤ 12750 reasoning in the DPI section: why a 12000 cap makes the T7 layout ambiguity moot for every settable value. |
| `docs/plans/add-custom-dpi-values/add-custom-dpi-values-plan.md` | This plan. |

## Acceptance criteria

1. Given a BLAZE on the DPI section, when the user selects point 3 and types `2450` into the value
   input and presses Enter, then the point-3 radio label reads `2450`, the slider thumb moves to
   `(2450-200)/(12000-200)` of the track, and one device write is issued after the debounce.
2. Given any mouse, when the user drags the DPI slider, then every value the label passes through is
   a multiple of 50 and never leaves `[dpi_min, dpi_max]` for that device.
3. Given a value typed that is not a multiple of 50 (e.g. `2430`), when the user commits it, then it
   is stored and displayed as the nearest multiple of 50 (`2450`), not rejected silently.
4. Given a number outside the device's range (0, 199, 99999), when the user commits it, then it is
   CLAMPED into range — 0 and 199 land on `DpiMin`, 99999 on `DpiMax` — and the box shows the stored
   value. Given text that is not a number at all (an emptied box), the input reverts to the point's
   current value and no device write is issued. *(Revised 2026-09-14: out-of-range used to revert
   too; typing 0 now means "as low as this mouse goes".)*
4a. Given the value box has focus, when the user types a letter, sign or separator, then the
   keystroke is refused. Given text arrives by any other route — paste, drag-drop, IME — then every
   non-digit is stripped from it, so the box can only ever contain digits.
5. Given a ladder of 7 points, when the user clicks `+ ADD POINT`, then an 8th point appears at the
   device's default DPI, is selected, and `+ ADD POINT` disappears. (The per-point indicator dot is
   hidden as of 2026-09-14 — see the UI addendum — so no colour is asserted here. The palettes still
   carry an 8th entry for when the dot returns.)
6. Given a ladder at `dpi_slot_max` (8), then `+ Add point` is not visible.
7. Given a ladder of 7 points, when the user deletes point 3, then the list shows 6 points, the
   former points 4–7 renumber to 3–6 keeping their values, and the header of the customization block
   shows the renumbered selected point.
8. Given a ladder of exactly `dpi_slot_min` (1) point, then that point's delete button is not
   visible.
9. Given the active point is 6 of 7 and the user deletes points until 4 remain, then the active point
   is the last surviving point (index 3) and the device is written with an active stage inside the
   new table, never an index past its end.
10. Given a ladder shrunk from 7 to 5 points, when the user leaves the device details and returns (or
    switches preset away and back), then 5 points are shown — the removed two do not reappear.
11. Given two presets on the same mouse with different ladders, when the user switches between them,
    then each preset shows and writes its own ladder and its own point count.
12. Given a customized ladder, when the user presses `RECOMMENDATIONS`, then the ladder returns to the
    catalog's values **and** the catalog's point count, and the selection lands on a defined point.
13. Given the Ukrainian UI, then `+ Add point`, the point header and the step caption are all
    Ukrainian, and none of the three is clipped or overlapping at the section's 336px width.
14. Given the DPI section is open, when the user drags the slider continuously for several seconds,
    then the UI does not stutter and no device write is issued until dragging stops (the existing
    debounce is the only writer).
15. Given a TAIPAN, then no value the UI can produce exceeds 12000, so the DPI register written stays
    ≤ 255 and one byte wide.
16. Given a signed-in user who customized a ladder on PC A, when they sign in on PC B with the same
    mouse, then PC B shows the customized ladder — with no backend change deployed.
17. Given a ladder of 7 points, when the user clicks a point's delete icon, then a confirmation
    dialog opens and the ladder is unchanged — still 7 points, nothing written to device or storage.
    *(Added 2026-09-14 with the confirmation dialog.)*
18. Given that dialog open, when the user cancels (button or corner close), then the point survives
    and the ladder is still 7 points.
19. Given that dialog open, when the user confirms, then the point is removed, the survivors
    renumber contiguously from zero, and the active stage lands inside the shorter table.
20. Given the DPI slider is dragged and the user opens a DIFFERENT device within the 300 ms persist
    debounce, then nothing is written under the second device's settings key — in particular its
    stored active stage is never the first device's stage index.

## Test strategy

This is an `add-*` change, so the tests below are required coverage, not optional.

### `tests/ProoveGaming.Infrastructure.Tests`

Both encoders already have tests (`X43DpiTableTests`, `TaipanDpiTableTests`) — extend them:

- `X43DpiTableTests`: a 5-stage ladder writes stage count 5, the five values in both the primary and
  mirror tables, and `UnusedStageDpi` (never zero) in slots 5–7. A 1-stage and an 8-stage ladder are
  both encoded correctly (the count-byte clamp boundaries).
- `TaipanDpiTableTests`: arbitrary values round-trip through `DpiRegister`/`CpiFromRegister` at the
  step boundary (200 → 4, 2450 → 49, 12000 → 240); a value of 12000 produces a **zero high byte**,
  pinning the ≤ 12750 invariant that keeps the T7 ambiguity harmless; a shrunk ladder disables the
  trailing slots.

### `tests/ProoveGaming.App.Tests`

- **Clamp/quantize helper** — table-driven: below min, above max, exact multiples, midpoint rounding,
  non-numeric input. The single helper the input, slider and add path share.
- **Add/remove semantics** — add at max is a no-op; remove at min is a no-op; remove re-indexes the
  survivors in order; the added point takes the next colour.
- **Active-stage clamping** — removing points below the active index moves the active stage to the
  last surviving point and persists it; the applied configuration never carries a stage ≥ count.
  Extends `DeviceDetailsPresetApplyTests`, which already owns the "never write a stage we don't know"
  contract.
- **Persistence shrink** — save a 7-point ladder, save a 5-point ladder over it, reload: exactly 5
  points come back. This is the regression test for the upsert-only bug and must fail against the
  current `SaveDpiPresetsAsync`.
- **Preset isolation** — two presets, two ladders, switching between them yields each one's own
  values and count (mirrors the existing preset-scoped settings tests).
- **Recommendations restore** — restores both values and count from the catalog.
- **Bounds fallback** — a card whose catalog leaves the five fields unset falls back to
  `200 / 12000 / 50 / 1 / 8` rather than to zeros (a zero `dpi_step` would divide by zero in the
  slider math).
- **Localization parity** — covered automatically by the existing `LanguageResourceParityTests` once
  the three keys are added; no new test needed.
- **Configuration round-trip** — `PresetConfigurationBuilder` → `PresetConfigurationApplier` carries a
  5-point ladder unchanged, proving the sync path needs no new work.

### Deliberately not automated

- Real HID writes to either mouse — no hardware in CI; covered by the QA steps.
- Whether the BLAZE sensor honours 50-DPI granularity (a physical-measurement question).
- The slider's pointer handling and the hover-reveal of the delete icon — Avalonia UI-thread
  affinity; covered by QA steps 5–7.
- Whether the backend echoes the extra `dpi.stage.*` keys — a live-server behaviour; QA step 12.

## QA (manual test steps)

1. Launch the app and connect the BLAZE mouse by cable. Open its details and go to the DPI section.
   **Expected:** seven DPI points listed with coloured dots, one of them selected, and a
   customization block below the list showing the selected point's number, "Step: 50 dpi", a value
   box and a slider.
2. Click a different DPI point. **Expected:** the mouse switches to that DPI (cursor speed changes),
   and the customization block's header and value update to the point you just selected.
3. In the value box type `2450` and press Enter. **Expected:** the point's label in the list becomes
   2450, the slider thumb moves accordingly, and moving the mouse confirms the new sensitivity.
4. Try to type letters or a minus sign into the box, then try PASTING `1a2b3` and `-400` into it.
   **Expected:** nothing non-numeric ever appears — typing is refused outright, and a pasted value
   arrives stripped to its digits (`123`, `400`). Now clear the box and press Enter: it returns to the last good value and
   nothing on the mouse changes. Then type `0` and press Enter, and separately `50000` and press
   Enter. **Expected:** the value becomes the mouse's minimum (200) and its maximum (12000)
   respectively, rather than snapping back.
5. Drag the slider slowly from one end to the other. **Expected:** the value changes in steps of 50,
   stops at 200 and at 12000, and the app stays responsive with no stutter while dragging.
6. Hover the pointer over a DPI point in the list. **Expected:** a delete (bin) icon appears at the
   right of that row and disappears when you move away.
7. Click the third point's delete icon. **Expected:** a confirmation dialog appears — "Delete DPI
   point?" with Cancel and a red DELETE. Click Cancel: the point is still there and nothing changed.
   Click the icon again and confirm with DELETE. **Expected:** the list now has six points, the
   remaining ones renumber so there is no gap, and the mouse's physical DPI button now cycles
   through six settings, not seven.
8. Keep deleting until one point remains. **Expected:** the delete icon is no longer offered on the
   last point.
9. Click `+ Add point` repeatedly. **Expected:** points are added up to a total of eight, each with
   its own indicator colour, and the button disappears once eight are reached.
10. Press `RECOMMENDATIONS`. **Expected:** the list returns to the original seven factory values.
11. Create a second preset, give it a different set of DPI values, then switch back and forth between
    the two presets. **Expected:** each preset keeps its own values and its own number of points, and
    the mouse follows the preset you switch to.
12. Sign in with an account, customize the ladder, then sign in with the same account on a second PC
    with the same mouse model. **Expected:** the second PC shows the customized values. *(If it does
    not, report it — it means the server is not storing the extra values, which is the one open
    question this feature depends on.)*
13. Unplug and replug the mouse. **Expected:** the customized values are re-applied and the point
    list still matches what you configured.
14. Switch the app language to Ukrainian and reopen the DPI section. **Expected:** the add button, the
    point header and the step caption are all in Ukrainian and none of the text is cut off or
    overlapping the value box.
15. Repeat steps 1–11 and 13–14 with the PG TAIPAN mouse connected **by cable**. **Expected:** the
    same behaviour. *(TAIPAN cannot be configured over its 2.4G receiver or Bluetooth — that is
    existing, expected behaviour, not a bug in this feature.)*
16. On BLAZE, set a point to a value that is not a multiple of 100 (e.g. 2450) and compare cursor
    travel against 2400 and 2500 over the same physical distance. **Expected / to report:** whether
    2450 behaves distinctly from 2400 — this is the open hardware question about the sensor's real
    granularity; record the answer either way.
17. On BLAZE, after changing DPI values, check the lighting. **Expected:** the LED effect is
    unchanged — a DPI write must never disturb it.

## UI addendum (2026-09-14)

Five rounds of UI feedback landed after the plan above was written. They are recorded here rather
than rewritten into the sections above, so the original design intent stays readable and the
delta is explicit. Everything in this section ships.

### Delete is confirmed, not immediate

The bin icon only **arms** a confirmation; nothing touches the ladder until it is confirmed. The
dialog reuses the shared `ConfirmDialog` rather than a fourth copy of its 620x220 chrome — that
control gained an `IsDestructive` flag which swaps its button pair to the shared
`cancelDelete` / `confirmDelete` styles (red confirm). The two class pairs are toggled as mutually
exclusive `Classes.x` / `Classes.!x` bindings, because both styles set the same properties and
leaving one applied alongside the other would let declaration order decide the winner.

Additional file-level changes:

| File | Intent |
|---|---|
| `src/ProoveGaming.App/Views/Dialogs/ConfirmDialog.axaml(.cs)` | `IsDestructive` styled property; the button pair binds its classes off it. |
| `src/ProoveGaming.App/Views/MainWindow.axaml` | Host the `DeleteDpiPointDialogPanel` instance (`IsDestructive="True"`). |
| `src/ProoveGaming.App/Views/MainWindow.axaml.cs` | `PendingDeleteDpiPoint` joins the dialog-overlay coordinator (property filter, flags, overlay + panel visibility). |
| `src/ProoveGaming.App/Views/MainWindow.Controls.cs` | `GetDeleteDpiPointDialogPanel()` accessor. |
| `src/ProoveGaming.App/Resources/Strings.{en,uk}.axaml` | `DeleteDpiPoint_Title` / `_Body` / `_Confirm`. |

`App.axaml` also gained white `Foreground` on `cancelDelete` / `confirmDelete` across base,
`:pointerover` and `:pressed`, restated on the **content presenter** as well as the button —
Fluent repaints `PART_ContentPresenter` in those states, which was both washing the label out and
erasing the cancel button's border on hover. That fix reaches the device-delete dialog too.

### Point rows

- **Indicator dots hidden** (`IsVisible="False"`), by request — not deleted. `DpiPresetViewModel`
  still carries R/G/B and `BuildDpiPresets` still assigns them positionally, so restoring the dot
  is one attribute. AC5 above was corrected accordingly.
- **Radio circle is 20x20** (was 18), in both the DPI and polling-rate lists — they share the
  template. The 32px hover frame is unchanged.
- **Delete icon is 20px** inside a 32x32 frame that reuses the shared `CutCornerFrameGeometry`, so
  a row's two hover targets match. It sits **in flow right after the value**, not right-aligned:
  right-aligned, `800` left a wide gap while `12000` had none. Spacing is 0 because the frame
  supplies its own 6px inset.
- Rows carry `Width="108"` and a `dpiPointRow` class that closes the template's 6px content margin,
  which is what buys `12000` (43px at 16px) enough room to clear the 32px button inside 76px.

### Add button

`+ ADD POINT` is an item of the point flow, not a control below it, so the panel is a **WrapPanel**
rather than a UniformGrid: a UniformGrid forces every cell to one third of 336px, and the label is
90px in en / 130px in uk — with the icon it needs 116/156px against a 108px cell. Points carry
their own 108px width so they still lay out three per row; the button sizes to content and wraps
when the remainder of a row is too narrow. The `+` is the shared `Add.svg` at 18px, an icon rather
than a glyph in the label, so `pgLinkButton`'s hover underline lands on the words alone.

### Ordinal point names

The customization header reads `3rd point`. The ordinals are **eight resource strings per
language**, not a `{0}` format with suffix logic in the view model: `st/nd/rd/th` is English
grammar and Ukrainian needs endings agreeing with `точка` being feminine (`1-ша`, `2-га`, `3-тя`,
`4-та` … `7-ма`, `8-ма`). The set is bounded at 8 because that is every slot either firmware has;
`DeviceDetails_Dpi_PointFormat` remains the fallback above that.

### Spacing

16px between the point grid and the customization block (the parent `StackPanel`'s own 8px spacing
plus an 8px margin — a spacer `Border` would have had to be 0px high to land on 16). The
polling-rate → lift-off spacer dropped 24px → 8px, reclaiming the 16px that pushed the bottom of
the lift-off slider past the viewport.

## Review focus

- `gRPC contract` — five new `DeviceCatalogEntry` fields; numbering must continue from 39 and the
  "0 = unset" fallback must hold against an older DeviceHost.
- `UI-thread affinity` — the slider drag and the text input run on the dispatcher; commits must be
  debounced and quantization must stay allocation-light.
- `localization` — three new keys in both `en` and `uk`, no fixed `Width` on the new controls.
- `null-safety` — `SelectedDpiPoint` is null while no device is shown and immediately after the last
  point is removed; every binding and command path must tolerate it.

## Performance & quality

- **Hot path: the slider drag.** `FractionChanged` fires per pointer move. It must do integer math
  (clamp + round to step), set at most two properties, and must **not** persist or write to the
  device — persistence and the device write happen once, on commit, through the existing debounce.
  No allocation per move: reuse the selected `DpiPresetViewModel` rather than rebuilding the list.
- **Rebuilding the point list.** Add and remove currently go through `RebuildDpiPresets`, which
  clears and repopulates an `ObservableCollection`, i.e. a full re-render of seven rows. That is
  acceptable for a click, but must not be called from the slider or per keystroke.
- **Storage.** The replace-by-prefix write stays one transaction / one commit fsync, matching the
  existing batched behaviour; it must run on the storage executor via `RunOnStorageAsync`, never on
  the UI thread.
- **Device writes.** Unchanged shape: one coalesced all-blocks write per debounce window. Under no
  circumstances one write per point — that is the burst that bricked BLAZE's buttons.
- **Shared state.** No new shared state; the ladder lives in the per-device-details view model and
  the device services are untouched.

## Risks

- **Sensor granularity on BLAZE is unverified.** If the 3327-class sensor snaps to 100 DPI, a value
  of 2450 will behave as 2400 or 2500 and the UI will claim something the hardware does not do. QA
  step 16 settles it; if it snaps, the fix is a one-line catalog change (`dpi_step: 100` for BLAZE),
  which is why the step is catalog-driven rather than a constant.
- **Minimum DPI is a guess.** 200 is inferred from the old BLAZE stage-0 fallback, not from a vendor
  statement — both spec-sheet fields are blank. A device that refuses very low values would show it
  as the mouse ignoring the write. Watch for it in QA steps 5 and 15.
- **TAIPAN's T7 layout ambiguity.** Mitigated, not resolved, by the 12000 cap. If anyone later raises
  `dpi_max` past 12750 without settling T7, values will be written wrong — the cap's reasoning is
  recorded in `protocol.md` and pinned by a test for exactly this reason.
- **Re-indexing on delete.** `StageIndex` is positional in the UI, in persistence keys and in the
  device table. Any place that assumes a stable index across edits (the `_assertedDpiStages` cache,
  the persisted active stage, a preset snapshot taken before the edit) can point at the wrong point.
  The active-stage clamp plus the stale-key deletion are the two guards; both have tests.
- **A preset pulled from the server keeps its own point count.** A preset created before this feature
  carries seven `dpi.stage.*` keys and will still show seven points — correct, but worth knowing
  before someone reports it as the count "not respecting" a newly shrunk ladder on another PC.
- **Style extraction touches the RGB picker.** Moving the hex-input styles to `App.axaml` puts a
  working control in the diff. Check the picker's closed state and popup still look identical.

## Open questions

1. **Point header wording.** The header is specified as "which point it is (1–8)". This plan ships it
   as `Point {0}` via a format key so it can be changed to the bare number by editing one resource
   string. Confirm which is wanted before the Ukrainian string is written.
2. **Value for a newly added point.** The plan appends the device's catalog default DPI
   (`Defaults.Dpi`, 1200 on both mice), clamped. The alternatives — the max, or the previous point
   plus one step — are each one line; say if 1200 is wrong.
3. **TAIPAN's 8th indicator colour.** Its stage colours are fixed in firmware and we only know seven.
   The 8th palette entry will be a placeholder; QA step 9 on real hardware should record what the
   mouse actually shows so the constant can be corrected.
4. **Does the shrink apply to the device immediately?** This plan writes the new table on every
   commit, including deletes, so the physical DPI button immediately cycles through fewer points.
   That is the behaviour QA step 7 asserts — confirm it is what is wanted rather than deferring the
   count change to the next full apply.

## Review findings

*filled in by /reviewer*

### Round 1 — Ship: no

Reviewed: `feat/PG-660-Custom-DPI-setting` vs `main` (3 commits, `main` merged in at 3632a23).
Working tree clean; full delta 32 files, +1985/-187. Build clean; 2910 tests pass
(App 1769 + 1 skipped, Infrastructure 1103, Installer 38).

#### Plan conformance

- **Implemented as planned:** every File-level change-list item is present — the five catalog
  fields threaded through `DeviceMap` → `DeviceGroupConfig` → `DeviceMapExpander` →
  `HidDeviceMapRegistry` → proto → `DeviceCardViewModel` → `HomeViewModel`; the
  `DeviceDetailsViewModel.Dpi.cs` partial; `ReplaceDeviceSettingsWithPrefix` +
  `SaveDpiPresetsAsync`; the section UI; both language files; `docs/CONTEXT.md`,
  `dpi-stage.md`, `taipan/protocol.md`.
- **Deviates from plan — proto numbering.** Plan says fields 40–44; shipped as **41–45**
  (`deviceservice.proto:339`). `main` merged `supports_extended_keys = 40` first, so the
  unreleased side moved — the right call, but the plan is now stale.
- **Deviates from plan — behaviour added after it was written.** None of the following appear
  in the plan, yet all ship: the delete **confirmation dialog** (`ConfirmDialog.IsDestructive`,
  `PendingDeleteDpiPoint`, three new resource keys); the indicator **colour dots hidden**;
  the 20px radio; the pixel-font add button; ordinal point names (8 keys × 2 languages,
  replacing the planned `Point {0}` format).
- **Acceptance criterion invalidated by the above.** AC5 reads "an 8th point appears with a
  **distinct indicator colour**". Colour dots are now `IsVisible="False"`, so AC5 cannot be
  verified as written, and the QA steps describe a bin icon the plan never introduced.
  CLAUDE.md requires the owning plan to be corrected in the same change — see S1.
- **Missing / partial:** the Test strategy's required coverage is largely absent — see S2.
- **Accepted limitation (per plan):** BLAZE 50-DPI sensor granularity unverified; TAIPAN T7
  byte-layout ambiguity held off by the 12000 cap. Residual risk unchanged and correctly
  recorded in `taipan/protocol.md`.

#### Code

- [x] **B1. Debounced ladder persist can write one device's active stage onto another** —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Dpi.cs:286-305` — Blocker

  **Problem:** `DebouncePersistDpiLadderAsync` captures `activeStage` at schedule time, but
  `PersistDpiLadder` re-reads `Device` and `DpiPresets` 300 ms later with no check that the
  shown device is still the one the edit belonged to. `_dpiLadderPersistCts` is cancelled only
  by the next DPI edit — never by a device switch, a preset switch, or the page closing.

  **Why it blocks:** drag the slider on a 7-point BLAZE whose active stage is 6, release, and
  within 300 ms open a device (or preset) whose ladder has 3 points. At fire time `Device` is
  the new one, so `SaveActiveDpiStageAsync` stores **stage 6 under the new device's settings
  key** — an index past the end of its 3-entry table. `AssertActiveDpiStageAsync`
  (`DeviceDetailsViewModel.cs:842-862`) applies no clamp, so on the next open that value is
  read back and written to the mouse, selecting a stage the DPI table wrote as disabled.

  **Solution options:**
  - Option A (correct, matches the repo): capture the `DeviceCardViewModel` when scheduling and
    bail at fire time with the guard already used four times in `DeviceDetailsViewModel.cs`
    (`if (!ReferenceEquals(Device, device)) return;`), re-deriving the stage from the live list
    rather than trusting the captured int.
  - Option B (quick): cancel `_dpiLadderPersistCts` in the `Device` setter / `SetDeviceAsync`
    prologue so a pending write cannot outlive its page. Closes the device switch but not a
    preset switch on the same device.

- [x] **S1. The plan no longer describes what ships** —
  `docs/plans/add-custom-dpi-values/add-custom-dpi-values-plan.md` — Should-fix

  **Problem / why it matters:** four rounds of UI change landed after the plan was written
  (confirmation dialog, hidden colour dots, 20px radio, ordinal naming, proto renumber) and
  none are reflected in the File-level change list, Acceptance criteria or QA steps. AC5 now
  asserts a colour that is deliberately hidden. CLAUDE.md is explicit that a criterion which
  silently stops matching reality is worse than none, because the next review passes by reading
  the wrong contract.

  **Fix:** update the File-level change list (add `ConfirmDialog.*`, `MainWindow.axaml`,
  `MainWindow.axaml.cs`, `MainWindow.Controls.cs`, the three `DeleteDpiPoint_*` keys and the
  eight ordinal keys), correct AC5, add acceptance criteria for the dialog (arm / cancel /
  confirm), renumber the proto fields to 41–45, and add a QA step for the dialog. Leave this
  journal untouched.

- [x] **S2. Required test coverage from the plan is largely unimplemented** —
  `tests/ProoveGaming.App.Tests/DeviceDetailsPresetApplyTests.cs` — Should-fix

  **Problem / why it matters:** the plan labels its Test strategy "required coverage, not
  optional". Shipped: four tests (edit-marks-dirty, delete arm/cancel/confirm) plus one skipped.
  Absent: the **persistence-shrink regression** — the plan's own "must fail against the current
  `SaveDpiPresetsAsync`" test — so the new `ReplaceDeviceSettingsWithPrefix` storage primitive,
  whose entire job is delete-then-insert, has **zero** tests. Also absent: table-driven
  `QuantizeDpi`; active-stage clamping on removal; add-at-max / remove-at-min no-ops; preset
  isolation; Recommendations restoring the count; bounds fallback; builder→applier round-trip.
  `tests/ProoveGaming.Infrastructure.Tests/` is untouched, so neither `X43DpiTableTests` nor
  `TaipanDpiTableTests` was extended — including the case the plan wanted pinned, that 12000
  encodes with a zero high byte (the invariant the whole TAIPAN safety argument rests on).

  **Fix:** hand the plan's Test strategy list to `/test-writer`; prioritise the shrink
  regression and the 12000 high-byte assertion.

- [ ] **S3. Unrelated Starlight vendor docs committed into this branch** —
  `docs/headset/starlight/starlight-vendor-request-2026-09-10.md` (+236),
  `starlight-vendor-request-2026-08-07.md` (+8) — Should-fix

  **Problem / why it matters:** both arrived in f14450e and have nothing to do with PG-660 —
  they were uncommitted working-tree files when the branch started. Scope drift makes the PR
  harder to review and couples an unrelated doc to this branch's merge.

  **Fix:** move them to their own commit/branch, or call them out explicitly in the PR body.

- [x] **S4. ~290 lines of whitespace-only churn around a 6-line change** —
  `src/ProoveGaming.App/Controls/RgbColorPickerControl.axaml` — Should-fix

  **Problem / why it matters:** `git diff --ignore-all-space` reduces this file to **6
  insertions / 7 deletions** (the hex-input styles moving out and back). The other ~290 changed
  lines are re-indentation. It hides the real edit and will conflict with any other branch
  touching the picker.

  **Fix:** restore the original indentation and keep only the style move.

- [x] **S5. The value box commits against whichever point is selected at that moment** —
  `src/ProoveGaming.App/Views/DeviceDetails/Sections/DpiSectionView.axaml.cs`
  (`OnCustomDpiLostFocus`) — Should-fix

  **Problem / why it matters:** `CommitCustomDpiText` resolves its target through
  `SelectedDpiPoint` at call time, not the point that was selected when the text was typed.
  Type `5000` into point 1's box and click point 3's radio: if `LostFocus` runs after the radio
  flips `IsSelected`, `5000` is written to **point 3**. Avalonia normally moves focus on
  pointer-press and raises the command on release, which would order it safely — but the code
  depends on that ordering without stating it, and no test covers it.

  **Fix:** capture the target point on `GotFocus` and pass it to the commit, so a value can only
  land on the point being edited. Cheap, and removes the dependency entirely.

- [x] **S6. The step grid is anchored at `min`, not at zero** —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Dpi.cs:132` — Should-fix

  **Problem / why it matters:** `snapped = round((clamped - min) / step) * step + min` produces
  values congruent to `min` (mod `step`), not to 0. Harmless today because both mice use
  `DpiMin 200` with `DpiStep 50`, and 200 is a multiple of 50. If a catalog ever sets a min that
  is not a multiple of the step, every produced value is off-grid and TAIPAN's CPI/50 register
  silently rounds it on the wire — the UI would then show a number the mouse does not hold.

  **Fix:** anchor the grid at zero (`round(clamped / step) * step`, then clamp), or validate at
  load that `DpiMin % DpiStep == 0` and log when it is not.

#### Tests

- [x] Persistence shrink: save a 7-point ladder, save a 5-point ladder over it, reload → exactly
  5 points; must fail against a plain upsert. — `tests/ProoveGaming.App.Tests/` (the only cover
  for `ReplaceDeviceSettingsWithPrefix`)
- [x] `QuantizeDpi` table-driven: below min, above max, exact multiples, midpoint rounding,
  `step <= 0`. — `tests/ProoveGaming.App.Tests/`
- [x] Active-stage clamping: removing a point below the active index moves the active stage and
  never yields an index ≥ count. — `DeviceDetailsPresetApplyTests`
- [x] Add-at-max and remove-at-min are no-ops. — `DeviceDetailsPresetApplyTests`
- [x] Preset isolation: two presets keep two ladders and two counts across switching. —
  `DeviceDetailsPresetApplyTests`
- [x] Bounds fallback: a card with the five catalog fields unset resolves to 200/12000/50/1/8. —
  `tests/ProoveGaming.App.Tests/`
- [x] `X43DpiTableTests`: a 5-stage ladder writes count 5, both mirror tables, and
  `UnusedStageDpi` (never zero) in the trailing slots; 1- and 8-stage boundaries. —
  `tests/ProoveGaming.Infrastructure.Tests/`
- [x] `TaipanDpiTableTests`: 12000 encodes with a **zero high byte** (pins the ≤12750 invariant
  the TAIPAN safety argument depends on); a shrunk ladder disables the trailing slots. —
  `tests/ProoveGaming.Infrastructure.Tests/`


**Round 2 (test-writer, 2026-09-14):** all eight closed — 20 tests added
(13 App, 7 Infrastructure). Two are proven regression tests, each confirmed to FAIL against the
pre-fix code and pass after it:
`DpiPresets_SavingAShorterLadder_DropsTheRemovedStages` (fails on the old upsert
`SaveDpiPresetsAsync`) and
`DraggingThenSwitchingDevice_DoesNotWriteTheFirstDevicesStageUnderTheSecond` (fails with B1's
`ReferenceEquals` guard removed). Suite: 2943 passing, 1 skipped.
Still uncovered by design: the Avalonia focus/commit ordering behind S5 (no headless harness),
real HID writes, and BLAZE's true sensor granularity — all QA-step territory.

#### Nits

- `OpeningDeviceWithPreset_WithoutUserInput_LeavesPresetClean` is carried as `[Fact(Skip=…)]`
  documenting a **pre-existing** dirty-on-open bug (verified to fail on `main` too). Correctly
  scoped out, but it rides in this PR — mention it in the PR body so it is not read as a
  regression introduced here. — `tests/ProoveGaming.App.Tests/DeviceDetailsPresetApplyTests.cs`
- `PendingDeleteDpiPoint` is not cleared when the shown device changes, so the dialog can
  outlive its row. `ConfirmDeleteDpiPoint` re-checks membership and no-ops, and
  `PendingDeleteMacro` behaves the same way, so this is consistency rather than a defect. —
  `DeviceDetailsViewModel.Dpi.cs:208`
- Adding a point immediately changes the mouse's **active** DPI to the new point (it is
  selected and `_dpiStageKnown` is forced true at `Dpi.cs:185`). Intended per AC5, but it means
  "add a point" silently re-points the hardware. — `DeviceDetailsViewModel.Dpi.cs:174-187`
- `Width="108"` on the point row is a hardcoded width tied to the 336px section (336/3 − 4).
  Justified — the row renders only digits, never localized text — but it breaks silently if the
  section width ever changes. — `DpiSectionView.axaml`

#### What's done well

- The catalog-driven bounds are threaded through **every** layer with no missed path: grepping
  the assignment sites for `DpiStages`/`DpiMin` finds exactly one card-population site
  (`HomeViewModel.cs:2237-2242`), so there is no half-populated route.
- `0 = unset` on all five proto fields, resolved in `DeviceCardViewModel`'s getters, means an
  older DeviceHost degrades to the built-in bounds instead of a divide-by-zero on `DpiStep` —
  the exact failure the plan called out.
- The proto conflict was resolved the way this file's own precedent dictates (the unreleased
  side moves), with the reasoning recorded inline beside the fields.
- `ConfirmDialog.IsDestructive` is a genuine variant rather than a third copy of the 620×220
  chrome, and the `Classes.x` / `Classes.!x` pair keeps the two button styles mutually exclusive.
- The Fluent `PART_ContentPresenter` repaint trap (vanishing border, theme-coloured label) is
  handled on base, `:pointerover` and `:pressed` for both delete buttons — the same failure the
  RGB picker documents from a past bug.
- Storage writes stay on the executor and the slider's per-pointer-move stream is debounced, so
  a full-range drag is one transaction rather than hundreds.

#### Verdict

Ship: **no** — one blocker. B1 persists a stale active-stage index against the wrong device and
that value reaches the hardware unclamped on the next open; the fix is the `ReferenceEquals`
guard this codebase already uses four times in the same file. Everything else is a should-fix:
the plan has drifted from what ships (S1), and the coverage the plan itself called
non-negotiable is mostly absent (S2) — including any test at all for the new delete-then-insert
storage primitive. First: (1) B1, (2) S2's shrink regression plus the TAIPAN 12000 high-byte
test, (3) S1 plan reconciliation.

### Round 2 — Ship: yes

Reviewed: `feat/PG-660-Custom-DPI-setting` vs `main` at 85196d8 (4 commits, working tree clean).
Full delta 35 files, +2857/-46. Build clean; **2967 passing, 1 skipped** (App 1813+1,
Infrastructure 1116, Installer 38). Round 1's findings were re-verified against `main`, not taken
on trust.

#### Plan conformance

- **Round 1 findings closed and verified:**
  - **B1** — the guard is present (`DeviceDetailsViewModel.Dpi.cs:347`) and `activeStage` was
    removed from the signature entirely, so the ladder and the stage are both read from live state.
    That closes the same-device preset-switch half of the race too, which a device-identity guard
    alone would not have.
  - **S4** — `git diff main...HEAD` and `--ignore-all-space` now return the **same** 13 lines for
    `RgbColorPickerControl.axaml`. The re-indentation is gone.
  - **S5** — the edited row is captured on `GotFocus` and held for the whole focus session.
  - **S6** — `QuantizeDpi` is zero-anchored (`Dpi.cs:138`).
  - **S1** — plan reconciled: proto renumbered to 41-45 with the reason, AC5 corrected, ACs 17-20
    and QA step 7 added for the dialog, and a dated **UI addendum** records the five post-plan UI
    rounds rather than rewriting the original intent.
  - **S2** — all eight Tests entries closed; see "What's done well".
- **S3 remains open** (Starlight vendor docs in f14450e) — unchanged, still a commit-hygiene call
  for the author, carried forward below as S2.
- **Behaviour added after Round 1**, all reflected in the plan: commit-on-outside-click, digits-only
  input, out-of-range clamping (AC4 revised — it previously specified reverting), and the DPI
  viewport height (660 → 684).
- **Acceptance criteria:** every code-reachable criterion is now backed by a test. AC13 (uk layout),
  AC14 (no stutter), AC16 (cross-PC sync) and the hardware ones remain QA-only, as planned.

#### Code

- [ ] **S1. The outside-click handler can outlive the view it belongs to** —
  `src/ProoveGaming.App/Views/DeviceDetails/Sections/DpiSectionView.axaml.cs:91-103` — Should-fix

  **Problem / why it matters:** the handler is added to `TopLevel.GetTopLevel(this)` on `GotFocus`
  and removed by re-resolving `TopLevel.GetTopLevel(this)`. In `OnDetachedFromVisualTree` the
  control is already off the visual tree, so that lookup can return `null` and `RemoveHandler` never
  runs — the delegate stays on the window. Reachable whenever the details page is closed while the
  value box still holds focus (click into the box, then close the device), and it accumulates one
  delegate per such visit.

  Bounded, which is why this is not a blocker: `StopWatchingForOutsideClick` nulls `_customDpiInput`
  unconditionally, so a leaked handler returns on its first line and can never act on a stale box.
  It is dead weight on the window's event route, not a correctness bug. Note the existing
  `DeviceCardBase.UnsubscribeFromTopLevel` has the same shape, so this is a repo-wide pattern rather
  than something this branch invented.

  **Fix:** cache the `TopLevel` you subscribed to in a field and unsubscribe from *that* reference,
  instead of resolving it again at detach time. `VisualTreeAttachmentEventArgs.Root` is the other
  supported route. Either removes the doubt without changing behaviour.

- [ ] **S2. Unrelated Starlight vendor docs still in the branch** (was Round 1 S3) —
  `docs/headset/starlight/starlight-vendor-request-2026-09-10.md` (+236),
  `starlight-vendor-request-2026-08-07.md` (+8) — Should-fix

  **Problem / why it matters:** both arrived in f14450e and have nothing to do with PG-660 — they
  were uncommitted working-tree files when the branch started. Not a code risk; it couples an
  unrelated doc to this branch's merge and makes the PR harder to read.

  **Fix:** move them to their own commit/branch, or name them in the PR body so a reviewer is not
  left wondering. Author's call — no code depends on it.

#### Tests

- [x] All eight Round 1 entries closed. Two are **proven** regression tests, each confirmed to fail
  against the pre-fix code and pass after it: `DpiPresets_SavingAShorterLadder_DropsTheRemovedStages`
  (fails on the old upsert) and
  `DraggingThenSwitchingDevice_DoesNotWriteTheFirstDevicesStageUnderTheSecond` (fails with B1's
  guard removed). That is the right standard and it was actually applied, not claimed.
- [ ] Not covered, by design and correctly stated: the Avalonia focus/commit ordering behind S5 and
  the outside-click path itself (no headless harness in this repo), real HID writes, and BLAZE's
  true sensor granularity. All are QA steps in the plan.

#### Nits

- The value box hides its scrollbar (`VerticalScrollBarVisibility="Hidden"`) while its siblings
  reserve a 12px scrollbar zone (`Width="348"` vs this section's `336`). Pre-existing, and now that
  the section is taller and can hold 8 points plus the customization block, scrolling is
  wheel-only with no visual affordance. Worth a look during QA. —
  `DpiSectionView.axaml:204-210`
- `CommitCustomDpiText` returns early when the edited row is gone (preset switch mid-edit) without
  calling `RefreshDpiPointState`, so the box can keep text belonging to a row that no longer exists
  until the next selection change repaints it. Cosmetic only — nothing is written. —
  `DeviceDetailsViewModel.Dpi.cs:166-169`
- Pasting non-digits now empties the box rather than showing the rejected text; applying an empty
  box then restores the previous value. Correct per AC4/AC4a, but it is a silent correction with no
  feedback — fine for a numeric field, worth knowing if anyone reports "my paste disappeared".

#### What's done well

- **The regression tests were proven, not assumed.** Both the shrink test and the B1 test were run
  against the pre-fix code and observed to fail. That is the difference between a test that guards a
  bug and one that merely accompanies a fix.
- **B1 was fixed at the source rather than patched.** Dropping the `activeStage` parameter (every
  caller was passing "whatever `BuildDpiPresets` just selected") removed a whole class of staleness
  instead of guarding one instance of it.
- **The TAIPAN 12000 high-byte theory pins the safety argument itself.** If anyone raises `DpiMax`
  past 12750, that test fails and tells them to settle vendor Q&A T7 on hardware first — the
  reasoning is now enforced, not just documented.
- **Digits-only is enforced in the right layer.** The keystroke filter is UX; the `TextChanged`
  sanitizer is the guarantee, because paste, drag-drop and IME never raise `TextInput`. The pure
  part (`KeepDigitsOnly`) sits on the view model where it is unit-testable, with the caret
  bookkeeping left in the view where it cannot be.
- **The height change reduced an inconsistency instead of adding one.** The 24px bottom margin was
  unique to this section (siblings use 12 or 0) and the host grid's 42px inset already clears the
  chrome's bottom band, so the 24px was pure subtraction.
- **The plan was reconciled honestly.** The UI addendum is dated and additive; the original design
  intent is still readable, and AC4/AC5 were corrected where the shipped behaviour genuinely
  changed rather than quietly left to rot.

#### Verdict

Ship: **yes** — no blockers. Round 1's blocker is closed and verified, and the two regression tests
were proven against the pre-fix code. S1 is a bounded, inert handler leak that mirrors an existing
repo pattern and can follow; S2 is commit hygiene, not code. First: (1) decide S2 before opening the
PR, (2) S1 whenever the `DeviceCardBase` pattern is next touched, (3) run the plan's QA checklist —
the hardware questions (BLAZE 50-DPI granularity, TAIPAN cable-only) are the only real unknowns left.

### Round 3 — Ship: yes

Scope: commit c084081 "slider fixes" (5 files, +149/-9), on top of Round 2. Working tree clean.
Build clean; **2969 passing, 1 skipped**. Two user-reported bugs, both introduced by this branch's
own storage-debounce design, both fixed with a regression test each.

#### Plan conformance

- **Both fixes are plan-relevant and the plan now needs one line.** AC14 reads "no device write is
  issued until dragging stops (**the existing debounce is the only writer**)". That was already the
  intent — the code just did not honour it, because a pause mid-drag is indistinguishable from
  letting go when the only signal is a 250 ms timer. The behaviour now matches AC14; the parenthetical
  is stale, since the writer is `DragCompleted`, not the debounce. Worth correcting when the plan is
  next touched (see S3).
- The UI addendum does not yet mention the preview/commit split or `PercentSliderControl.DragCompleted`.
- No other plan item is affected: the ladder, bounds, persistence and dialog contracts are unchanged.

#### Code

- [ ] **S1. The same mid-drag device write still happens on the Actuation and lift-off sliders** —
  `DeviceDetailsViewModel.Actuation.cs:602-618` (`ScheduleActuationApply`),
  `DpiSectionView.axaml.cs:19-41` (lift-off) — Should-fix (follow-up, out of PG-660 scope)

  **Problem / why it matters:** this branch fixed the defect for the DPI slider only. The Actuation
  sliders go `SetActuationValue` → `ScheduleActuationApply` → `Task.Delay(ActuationApplyDebounceMs)`
  → write to the keyboard, with no notion of the drag ending; the lift-off slider goes through its
  own Grid pointer handlers into the 300 ms mouse-settings debounce. Both therefore write to the
  device whenever the user pauses mid-drag for longer than the debounce — the exact symptom reported
  for DPI, on a magnetic keyboard's actuation depth and on the mouse's lift-off.

  Not a regression from this commit — it is pre-existing on both — but this commit built the missing
  mechanism, so leaving the siblings unfixed is now an inconsistency rather than an oversight.

  **Fix:** subscribe `DragCompleted` in `ActuationSectionView` and split its handler the same way
  (`FractionChanged` previews, `DragCompleted` applies). Lift-off needs a little more: it is not a
  `PercentSliderControl`, so either port it onto that control or raise its own release from
  `OnLiftOffPointerReleased`. Best done as its own change with its own tests, not folded into PG-660.

- [ ] **S2. The dirty compare runs a synchronous multi-table SQLite read per pointer move** —
  `DeviceDetailsViewModel.Presets.cs:220-230` (`TryRecomputeHasUnsavedPresetChangesNow`) —
  Should-fix

  **Problem / why it matters:** `StagePresetDirtyOnly` calls `PresetMatchesLiveState`, which compares
  several tables synchronously, on the UI thread. The drag path calls it on **every** pointer move.
  Pre-existing — before this commit the per-move `StageChangeAndApply` began with the same call — but
  now that dragging no longer writes to the device, this is the dominant per-move cost left on the
  hot path the plan's Performance section explicitly says must stay cheap, and CLAUDE.md treats a
  visible drag hitch as a bug rather than something to accept.

  No stutter has been reported, which is why this is not a blocker. It scales with preset-table size,
  so it will get worse before anyone notices a cause.

  **Fix:** during a drag, skip the compare and mark dirty optimistically (the edit is known to be a
  change — `SetDpiPointValue` already returned early if the value was unchanged), leaving the
  authoritative recompute to the debounce continuation, which already runs one. Cheap and keeps the
  flag correct, because the continuation is the one that settles it today anyway.

- [ ] **S3. Plan text for AC14 and the UI addendum is now stale** —
  `docs/plans/add-custom-dpi-values/add-custom-dpi-values-plan.md` — Should-fix

  **Problem / why it matters:** AC14 still credits "the existing debounce" as the only writer, which
  is no longer how the guarantee is produced, and the UI addendum has no entry for the
  preview/commit split. A criterion that describes a mechanism the code no longer uses sends the next
  reviewer looking for the wrong thing.

  **Fix:** reword AC14 to name `DragCompleted` as the writer, and add a short addendum entry for the
  preview/commit split and the new slider event.

- [ ] **S4. (carried, Round 2 S1) Outside-click handler can outlive the view** —
  `DpiSectionView.axaml.cs:91-103` — Should-fix. Unchanged this round; still bounded and inert.

- [ ] **S5. (carried, Round 2 S2 / Round 1 S3) Starlight vendor docs still in the branch** —
  `docs/headset/starlight/…` — Should-fix, commit hygiene, author's call.

#### Tests

- [x] Both bugs have a regression test, and the first was **proven** — `DraggingTheDpiSlider_MarksThePresetDirty`
  was written first and observed to fail (`Assert.True() Failure`) before the fix.
- [x] `DraggingTheDpiSlider_DoesNotWriteToTheDeviceUntilTheDragEnds` pauses **600 ms** between moves —
  longer than both the 250 ms apply debounce and the 300 ms persist debounce — so it reproduces the
  exact shape of the reported bug rather than a fast synthetic drag that would have passed either way.
  It also asserts the preset still goes dirty during the drag, pinning both halves at once.
- [ ] `PercentSliderControl` itself has no tests — `DragCompleted` firing once per drag, the
  capture-lost path, and the "release without a press does nothing" guard are all verified only by
  reading. An Avalonia headless harness would be needed; consistent with the repo's existing
  treatment of controls, so noted rather than demanded. — `tests/ProoveGaming.App.Tests/`

#### Nits

- `OnPointerReleased` now sets `e.Handled = true`, which the control did not do before. Symmetric with
  its handled press and nothing upstream listens for the release (the section's outside-click watcher
  is on PointerPressed), so this is safe — just a behaviour change in a shared control worth knowing
  about if a future parent wants to see releases. — `PercentSliderControl.axaml.cs:71`
- `CommitCustomDpiDrag` fires on every release, including a plain click that moved nothing. The apply
  is then a no-op push of unchanged values through the 250 ms debounce — harmless, but it does mean a
  click on the track costs one device write. — `DeviceDetailsViewModel.Dpi.cs` (`CommitCustomDpiDrag`)

#### What's done well

- **The re-entrancy ordering in `OnPointerReleased` is correct and subtle.** `_dragging = false` is set
  *before* `e.Pointer.Capture(null)`, and releasing capture synchronously raises
  `OnPointerCaptureLost` — so the guard is already down and `DragCompleted` fires exactly once. Written
  the other way round it would double-fire, and the second fire would push a redundant device write.
- **Capture-lost is handled, not just release.** Another control grabbing the pointer or the window
  losing activation ends the drag just as truly; a consumer waiting only on `PointerReleased` would
  silently never apply the value the thumb was left on.
- **The dirty fix restored the repo's own ordering rather than inventing one.**
  `ApplyMouseSettingsAsync` already awaits its save and *then* stages; the DPI debounce had broken
  that sequence, and the fix puts it back — which is why it is one call and not a new mechanism.
- **`DragCompleted` is additive.** Actuation subscribes only `FractionChanged`, so its behaviour is
  byte-identical; the new event costs nothing until a consumer opts in.
- **The preview/commit split keeps the preset dirty during the drag.** Separating "the edit is real"
  from "the hardware has it" is the right seam, and it is what lets the value be safe on release
  without lying to the user about whether they have unsaved changes.


#### Round 3 follow-up — M1 (external review), fixed 2026-09-14

**Confirmed and fixed.** An externally reported blocker, reproduced before fixing and proven after.

**The defect:** the ladder was only restored from a preset snapshot when that snapshot actually had
`dpi.stage.*` rows. Those rows are written by exactly two callers — a point edit and RECOMMENDATIONS —
so a preset nobody ever edited the DPI on has none. Discarding, or switching to such a preset, left
the ladder the user had just abandoned standing in the UI, and the apply that follows pushed those
values to the mouse under the new preset's name, with an active stage that could sit past the end of
the shorter table (AC9/AC11 both violated). Harmless before this branch, because the in-memory ladder
always equalled the catalog; not harmless once it became user-defined.

**Fix (the reviewer's Option A):**
- `DeviceDetailsViewModel.cs` (`LoadPersistedDeviceStateAsync`) — when `forcePersisted` is set and the
  snapshot carries no ladder, rebuild from the catalog. "No rows" means "the catalog ladder", which is
  what an untouched preset has always represented.
- `DeviceDetailsViewModel.Presets.cs` (apply) — clamp `activeStage` to the table actually being
  written, as a second barrier independent of whichever UI path resolved the stage.

Option B (always writing the ladder into every snapshot) was not taken: it changes the dirty-state
meaning of every existing preset and deserves its own change with its own tests.

**The test criticism was also correct.** `TwoPresets_KeepSeparateLaddersAndCounts` captured B's point
count *after* switching to B and compared it against itself, so it passed even while B inherited A's
ladder — it could never have caught this. It now compares against the catalog count captured before
any edit. Verified by removing the fix: both
`SwitchingToAPresetWithNoSavedLadder_RestoresTheCatalogLadder` (new) and the corrected
`TwoPresets_KeepSeparateLaddersAndCounts` fail without it and pass with it.

Suite after the fix: **2970 passing, 1 skipped**.

#### Verdict

Ship: **yes** — no blockers. Both reported bugs are fixed at their real cause, each with a regression
test, and one was proven against the pre-fix code. Everything open is a follow-up: S1 is the same
defect on sibling sliders (pre-existing, now cheap to fix, deserves its own change), S2 is a
pre-existing per-move synchronous DB read, S3 is plan text, S4/S5 carry over unchanged.
First: (1) S3 while the context is fresh, (2) S2 if any drag hitch is reported, (3) S1 as a separate
follow-up covering Actuation and lift-off together.
