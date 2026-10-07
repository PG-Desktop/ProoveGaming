# Plan: add the Mod-Tap (MT) extended key

Date: 2026-09-09. Owner: architect. Phase 3 of 4 — depends on
[`../add-cyrex-extended-keys/add-cyrex-extended-keys-plan.md`](../add-cyrex-extended-keys/add-cyrex-extended-keys-plan.md)
(section, codec, service, gRPC `ExtendedKey`, storage). It introduces the **single-key editor** and
the **output-key picker** that
[`../add-extended-key-toggle/add-extended-key-toggle-plan.md`](../add-extended-key-toggle/add-extended-key-toggle-plan.md)
reuses, so implement MT before TGL. No proto, schema or shared-model change.

Protocol source: Keyborder-v3 `handleSaveMTData` (bundle line 74522), `MTContent` (70012–70687),
`MTKeyContent` (72893–73084); folded into
[`docs/knowledge-base/smart-infinite-protocol/advanced-keys-rapid-snap.md`](../../knowledge-base/smart-infinite-protocol/advanced-keys-rapid-snap.md)
by this change.

> **Status note (2026-09-24).** Extended Keys is hidden on every shipping model until the
> implementation is finished — `"SupportsExtendedKeys"` is `false` for Cyrex and Aress in
> `src/ProoveGaming.DeviceHost/appsettings.json`, which also stops the probe and every write to
> the board. Nothing in this plan runs while that flag is off; see
> [`../add-cyrex-extended-keys/add-cyrex-extended-keys-plan.md`](../add-cyrex-extended-keys/add-cyrex-extended-keys-plan.md) for the full note.

## Goal

Let one physical key do two things: output a **hold** key when held at least a threshold (typically
a modifier — Caps Lock → Ctrl) and a **tap** key when released before it (Caps Lock → Esc). The
user picks the physical key on the overlay, both outputs from a key picker, and the threshold in
milliseconds. The configured row reads `T [MT] W Q  43 ms` per the design.

## Scope

In: MT record encode/decode (`0x81`), `ExtendedKeyMode.ModTap` accepted by the service, the MT row,
the **single-key editor** (physical-key chip from the overlay, Hold / Tap output chips, Time (ms)
slider 0..1000 default 100, hint "Hold time ≥ {0} ms counts as hold; otherwise tap"), the
**output-key picker** (a pick mode of the existing Key Assignment dialog: Keys tab only — see the
Implementation notes for why System is hidden too — no Macros tab, returns a matrix-record triple
instead of staging a remap), row formatting, preset/API
round trip via existing `slot1`, `time_ms`, `hold_key`, `tap_key`, strings, tests, glossary.

Out: shared model/proto/schema changes; a hold ≠ tap constraint (the vendor has none); mouse-button
outputs (`codeType 3`, unreachable from the vendor picker).

## Options considered

### A. Output picker = the Key Assignment dialog in a "pick" mode (recommended)

- Pro: the dialog already renders every keyboard key and the System list with localized labels and
  the modifier semantics (bitmask + 0x00 usage); one picker for MT and TGL; no third keyboard drawing.
- Con: the dialog's staging code paths need a purpose switch so a pick never writes a remap.

### B. Inline `ComboBox` of `AssignmentTargets` grouped by category

- Pro: trivial. Con: 100+ items in a dropdown, poor for modifiers vs letters; does not match the
  vendor's visual picker.

### C. A new mini keyboard picker view

- Con: a third copy of keyboard-drawing code (the repo's second-copy rule).

## Recommended approach

**Record** (`handleSaveMTData`):

```
[0x81, 0x06, time_lo, time_hi, hold_codeType, hold_code, tap_codeType, tap_code]     8 bytes
time      uint16 LE, MILLISECONDS (vendor slider 0..1000 step 1, default 100)
codeType  1 = HID usage · 2 = modifier bitmask
example   Caps Lock: hold → LCtrl, tap → Esc, 150 ms  =  81 06 96 00 02 01 01 29
```

Single-key mode: only the **physical key's** slot triplet becomes `{0x81, offset}`. Both outputs
are encoded from their matrix-record triple (`usage != 0 → (1, usage)` else `(2, mask)`), not from
a factory slot — an output need not be a physical key of this board. Semantics: bytes 4–5 fire on
hold (≥ threshold), bytes 6–7 fire on a release before the threshold.

**Infrastructure.** `EncodeModTap(timeMs, holdKey, tapKey)` / `TryDecodeModTap`; service encoder
switch accepts `ModTap` (both keys required, `time_ms` clamped 0..1000, `slot2`/`priority` unset →
otherwise `Rejected`).

**App.**

- `IsModTapAvailable = true`; editor mode `ModTap` → new `ExtendedKeySingleKeyEditor` block in the
  section view: "Set physical key" chip (filled by the overlay click, refusal rules as RS, disabled
  once filled until cleared), "Hold" and "Tap" output chips (clicking one opens the picker; the
  vendor pre-fills Hold with the physical key itself — do the same), `Time (ms)` slider 0..1000
  step 1 default 100 with the value shown right-aligned, hint line. Confirm enabled when physical
  key, hold and tap are set.
- Output-key picker: `KeyDialogPurpose { Remap, PickExtendedKeyOutput }` on the KeyDialog partial.
  In pick purpose the dialog opens on the Keys tab, hides the Macros and System tabs and the RESET
  link, and a key pick (click or typed) invokes a callback
  (`Action<(byte TypeByte, byte Bitmask, byte ScanCode, string Label)>`) then closes — no
  `StageAssignmentTarget`, no toast, no device write. The dialog title uses a new string ("Choose
  the {0} key" with Hold / Tap); closing by any path drops the callback.
- Row: `T [MT] W Q  100 ms` — physical key chip, MT badge (the phase-1 red `ModTapBadgeHex`), hold
  chip, tap chip, `{ms} ms` parameter text (hidden when 0). Labels of outputs resolved from
  `AssignmentTargets` by (type, bitmask, scancode), falling back to a hex label.

## File-level change list

- `src/ProoveGaming.Infrastructure/Classes/SmartInfiniteAdvancedKeyArea.cs` — edit: `EncodeModTap`,
  `TryDecodeModTap`.
- `src/ProoveGaming.Infrastructure/Services/SmartInfiniteExtendedKeysService.cs` — edit: encoder
  switch accepts `ModTap`; validation.
- `src/ProoveGaming.Infrastructure/Classes/KeyboardExtendedKeys.cs` — edit: `ExtendedKeyEntry.ModTap`
  factory (no field change).
- `src/ProoveGaming.App/Models/ExtendedKeyRecord.cs` — edit: `ExtendedKeyMode.IsSingleKey`,
  `ExtendedKeyTime.MinMs/MaxMs/DefaultMs` (0 / 1000 / 100) shared with TGL, `ModTapRecordBytes`,
  `ExtendedKeyRecord.ModTap` factory.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/ExtendedKeyRowViewModel.cs` — edit: `HoldLabel` /
  `TapLabel` output chips.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.ExtendedKeys.cs` — edit:
  `IsModTapAvailable`, single-key editor state (`EditorPhysicalKey`, `EditorHoldKey`,
  `EditorTapKey`, `EditorTimeMs`), `OpenOutputPicker(role)`, Confirm path, `BuildRow` MT case.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyDialog.cs` — edit:
  `KeyDialogPurpose`, pick callback, Macros tab hidden in pick purpose, no staging in pick purpose.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` — edit:
  the physical-key overlay click routes to the single-key editor when it is open.
- `src/ProoveGaming.App/Views/DeviceDetails/Sections/ExtendedKeysSectionView.axaml` (+ `.cs`) —
  edit: MT mode row; single-key editor block (chips, slider — reuse `PercentSliderControl` or the
  existing slider style, value label, hint); row with two output labels.
- `src/ProoveGaming.App/Views/DeviceDetails/Dialogs/KeyAssignmentDialog.axaml` — edit: title, RESET
  link, System / Macros tabs and the Keys hint bound to purpose.
- `src/ProoveGaming.App/Resources/Strings.en.axaml` / `Strings.uk.axaml` — edit: "Set physical key",
  "Hold", "Tap", "Time (ms)", hint format, picker title format, MT badge (label/description exist).
- `docs/knowledge-base/smart-infinite-protocol/advanced-keys-rapid-snap.md` — edit: MT record
  section (incl. the vendor's un-padded-hex read-back bug note).
- `docs/CONTEXT.md` — edit: **Mod-Tap (MT)** one-liner.
- `docs/plans/add-key-assignment-popup/add-key-assignment-popup-plan.md` — edit: dated note that the
  dialog gained a pick purpose used by Extended Keys.
- `tests/ProoveGaming.Infrastructure.Tests/SmartInfiniteAdvancedKeyAreaTests.cs` — edit: MT
  encode/decode incl. the `81 06 96 00 02 01 01 29` example and `time = 0x0105`.
- `tests/ProoveGaming.Infrastructure.Tests/SmartInfiniteExtendedKeysServiceTests.cs` — edit: MT
  write points only the physical slot; missing tap → Rejected; coexistence with RS/SOCD.
- `tests/ProoveGaming.App.Tests/DeviceDetailsExtendedKeysViewModelTests.cs` — edit: MT row visible;
  Hold pre-filled with the physical key; picker callback fills the chip; default 100 ms; Confirm
  payload; row text; physical key in another record refused.
- `tests/ProoveGaming.App.Tests/DeviceDetailsKeyAssignmentDialogTests.cs` — edit: pick purpose never
  stages a remap or writes; Macros tab hidden.
- `tests/ProoveGaming.App.Tests/PresetConfigurationBuilderTests.cs` / `ApplierTests.cs` — edit:
  `hold_key` / `tap_key` / `time_ms` round trip.

## Acceptance criteria

- [ ] Given the section → "Add extended key" lists "Mod-Tap (MT)" with ⓘ "Distinguish tap and hold
  on one key to trigger different actions."
- [ ] Given the MT row → the editor shows the physical-key chip, Hold and Tap chips, a Time (ms)
  slider at 100 and the hint; Confirm disabled until all three chips are filled.
- [ ] Given clicking **Caps Lock** on the overlay → the physical chip shows Caps Lock and Hold is
  pre-filled with Caps Lock; clicking Hold opens the picker without Macros / System tabs; choosing Left Ctrl
  fills Hold with "CTRL (L)"; choosing Esc for Tap fills Tap; no remap is staged and no device write
  happens during picking.
- [ ] Given Confirm with 150 ms → one `SetExtendedKeys` containing `{ModTap, slot1 = CapsLock,
  time_ms = 150, hold_key = {0x10, 0x01, 0}, tap_key = {0x10, 0, 0x29}}`; the board reads back
  `81 06 96 00 02 01 01 29` pointed at by Caps Lock's slot only; the row shows
  `CAPS [MT] CTRL (L) ESC  150 ms`.
- [ ] Given the board → a quick tap of Caps Lock sends Esc; holding it and pressing C sends Ctrl+C.
  (Hardware check.)
- [ ] Given Caps Lock already in another record → refused; given the picker is opened from Key
  Assignments (remap purpose) → behaviour unchanged.
- [ ] Preset switch removes/restores the record with time and both outputs; API v2 round trip holds.
- [ ] `LanguageResourceParityTests` passes; uk chip labels fit.

## Test strategy

Change to an existing feature area (foundation tests stay green and are extended) plus a change to
the Key Assignment dialog, which **already has tests** (`DeviceDetailsKeyAssignmentDialogTests`) —
extend them for the pick purpose before wiring the editor to it. Cases per the change list. Not
automated: firmware hold/tap timing (QA 3–4), dialog layout in pick purpose (QA 2).

## QA (manual test steps)

1. Open Extended Keys on the Cyrex. **Expected:** "Mod-Tap (MT)" row with ⓘ tooltip.
2. Click it, then click **Caps Lock** on the picture. **Expected:** the physical chip shows Caps
   Lock and "Hold" is already Caps Lock. Click "Hold". **Expected:** the key picker opens with the
   Keys tab only (no System, no Macros, no RESET). Pick **Left Ctrl**. Click "Tap", pick **Esc**. Leave
   100 ms, Confirm.
   **Expected:** a row "CAPS  MT  CTRL (L)  ESC  100 ms"; Save Preset lights up.
3. In a text editor tap **Caps Lock** quickly. **Expected:** acts as Esc (e.g. closes a menu) and
   Caps Lock state does not toggle.
4. Hold **Caps Lock** and press **C**. **Expected:** copy (Ctrl+C) happens.
5. Move the slider to 400 ms, add another MT on **Tab** (Hold = Left Shift, Tap = Tab). **Expected:**
   a tap types Tab; a hold longer than ~0.4 s acts as Shift.
6. Go to Key Assignments and click **Caps Lock**. **Expected:** blocked message, no dialog. Remap
   another key there normally. **Expected:** the remap flow is unchanged.
7. Switch the language to Ukrainian. **Expected:** editor, picker title and row translated, nothing
   clipped.
8. Save Preset, switch presets and back. **Expected:** the MT rows and behaviour return.

## Review focus

- `null-safety` — both outputs required; pick callback cleared when the editor closes so a late
  dialog result cannot land in another editor.
- `UI-thread affinity` — the picker callback runs on the dispatcher; no device write on pick.
- `localization` — chip labels, picker title, hint in both languages.

## Performance & quality

None beyond phase 1; the slider is UI-only until Confirm (no per-tick writes).

## Risks

- The dialog's pick purpose touches a well-tested remap path; a regression there breaks Key
  Assignments for every keyboard. Mitigation: the purpose defaults to `Remap`, and the dialog tests
  pin that a pick never calls `StageAssignmentTarget`/`SetKeyMappingAsync`.
- Vendor read-back bug (un-padded hex) — do not copy; `BinaryPrimitives` + a `0x0105` test.

## Implementation notes (2026-09-09, /coder)

Decisions taken while building, where the plan left room or the code forced a choice:

- **System tab hidden in pick purpose (deviation).** The plan offered Keys + System; the MT record's
  outputs are `(codeType, code)` with `1` = HID keyboard usage and `2` = modifier mask — there is no
  code type for a consumer-page command (`{0x20, lo, hi}`), so a System pick could only be refused by
  the host. `IsKeyDialogSystemTabVisible = IsSystemCommandsSupported && remap purpose`; the service
  refuses such an output anyway (`Write_ModTapWithAConsumerCommandOutput_IsRejectedWithoutWriting`).
  The picker also hides the RESET link (nothing to reset) and swaps the Keys hint for a pick wording.
- **Typed keys pick too.** `TryPickRemapTargetFromKeyboard` takes the pick branch before the
  `SelectedPhysicalKey` gate (the Extended Keys section has no selected key), reusing the remap path's
  usage → bitmask fallback (`FindTypedTarget`). No write can result, so the mid-write guard is skipped.
- **Every close ends the pick.** The `IsKeyAssignmentDialogOpen` setter drops the callback and resets
  the purpose when the dialog closes in pick purpose — ✕, a delivered pick, `CancelExtendedKeyEditor`
  (which closes an open pick dialog) and the device-reload close all funnel through it.
- **Undecodable managed-type records are unmanaged on the write path too (service fix).** `PlanWrite`
  classified records by TYPE, so an MT record with a mouse-button output — and, since phase 2, a SOCD
  record with a foreign priority nibble — was dropped on the next write and its slot handed back to
  the factory key while the read path called it "blocked". `IsManagedRecord` (managed TYPE **and**
  decodes) now drives both paths; the SOCD plan carries a dated note.
- **Time range: refused, not clamped.** The slider cannot produce a value outside 0..1000, so the
  service treats an out-of-range `time_ms` like an out-of-range SOCD priority (`Rejected`, no packet)
  rather than silently changing what a client asked for; `EncodeModTap` throws as `EncodeSocd` does.
- **Hold pre-fill follows the physical key.** Filling the physical chip pre-fills Hold with that key
  (vendor behaviour) and marks it a pre-fill; a user pick clears the mark; clearing the physical chip
  drops a still-pre-filled Hold. A Tap picked before the physical key is kept.
- **Physical chip disabled once filled.** A click on another key while the chip is filled shows
  `ExtendedKeys_Refuse_PhysicalKeySet` instead of silently doing nothing; clicking the same key clears
  it, as in the pair editor.
- **Naming.** `ExtendedKeyTime.MinMs / MaxMs / DefaultMs` (App) and `TimeMsMin / TimeMsMax`
  (Infrastructure codec) rather than `TimeMin/Max/Default` on the record; `ExtendedKeyEntry.ModTap`
  factory added next to `Socd` (no field change to the shared model).
- **Badge colour.** The row uses phase 1's `ModTapBadgeHex` (`#FF383C`, the design's red); the plan's
  "orange" was not adopted to keep one constant per mode.
- **Row shape.** Hold / Tap are two more `extendedKeyChip`s after the badge (`HoldLabel` / `TapLabel`
  on the row VM), not plain labels, so the row reads like the editor.
- **New strings** (en + uk): `ExtendedKeys_PhysicalKey`, `_Hold`, `_Tap`, `_TimeMs`, `_TimeMs_Format`,
  `_ModTap_TimeHint_Format`, `_OutputsHint`, `_ConfirmHint_Single`, `_Refuse_PhysicalKeySet`,
  `_ClearPhysicalKey`, `KeyDialog_PickTitle_Format`, `KeyDialog_Hint_Pick`.

### Hardware verification — 2026-09-09, PG Cyrex (VID 28E9 PID 3272), fw 0x0002, uk locale

Driven through the real App and DeviceHost (`dotnet run`, Development) with synthetic mouse input
only, reading the host log (`%TEMP%/Logs/log20260909.txt`):

- Read first: `advKeys=1024 B`, `GetExtendedKeys` → `0 managed, 0 blocked, 1024/1024 bytes free`. The
  "Mod-Tap (MT)" row rendered under SOCD with its ⓘ.
- Editor: "Фізична клавіша" chip outlined, "Утримання" / "Натиск" chips, "Час (мс) 100 мс" with the
  slider at 10 %, hint line, Confirm disabled. Clicking Caps Lock filled the chip **and pre-filled
  Hold with CAPS**; the hint moved on to the outputs.
- Picker: "Оберіть клавішу: Утримання" opened on the КЛАВІШІ tab **alone** (no СИСТЕМА, no МАКРОСИ,
  no СКИНУТИ), the pick hint shown, CAPS highlighted as the current output. Clicking CTRL (L) closed
  it and filled Hold; the Tap picker likewise took ESC. The host log shows **no `SetKeyMapping`, no
  `[KEY]` write and no toast** during picking.
- Slider: a click at the 150 ms position produced **149 ms** and the next pixel **152 ms** — at the
  336 px track one pixel is ≈ 3 ms, so 150 exactly is not reachable by pointer (see Open items). The
  run therefore used **152 ms**.
- Confirm: one `SetExtendedKeys`; on the wire `0x3B` 19 packets / 1024 B then `0x18` 7 packets /
  384 B; `extended keys verified: 1 managed record(s)`. The next read snapshot (the delete's) logged
  `record type=0x81 offset=0x0000 bytes=8106980002010129 slots=[30]` — i.e.
  `81 06 98 00 02 01 01 29` (152 ms LE, hold = modifier 0x01, tap = usage 0x29) pointed at by Caps
  Lock's slot **only**; the acceptance criterion's `96 00` differs from `98 00` solely by the chosen
  time.
- Row: `CAPS [MT] CTRL (L) ESC  152 мс 🗑` — every chip fits in the 336 px column; the "152 мс"
  parameter wraps onto two lines (readable, nothing clipped). Caps Lock lit on the overlay; Save
  Preset lit.
- Key Assignments: clicking CAPS showed the banner "CAPS є частиною розширеної клавіші (Mod-Tap
  (MT))…", no dialog; the hover tip reads "Mod-Tap (MT)". An ordinary key (G) still opened the remap
  dialog with all three tabs and СКИНУТИ — the remap purpose is unchanged.
- Trash: one write with an empty set, `extended keys verified: 0 managed record(s)`; row gone,
  highlight cleared, Save Preset back to grey. **The board was left with no records.**
- No EXT warnings, verify mismatches or refusals in the host log.
- **Not verified:** the physical tap → Esc / hold → Ctrl behaviour (acceptance criterion 5, QA 3–5)
  needs a hand on the board; the record bytes the firmware reads are confirmed, the behaviour is not.
- **Found, not fixed (pre-existing, outside this plan):** every ⓘ tooltip on the mode rows (RS, SOCD
  and now MT) renders the text "Avalonia.Controls.TextBlock". Cause: the window-level `ToolTip`
  template in `src/ProoveGaming.App/Views/MainWindow.axaml:56` renders `{TemplateBinding Content}`
  through a `TextBlock.Text`, so a control set as `ToolTip.Tip` (the phase-1 wrapping `TextBlock`)
  prints its type name. Fix options: give that template TextBlock `TextWrapping="Wrap"` /
  `MaxWidth` and put plain string tips back on the rows, or switch the template to a
  `ContentPresenter`. The SOCD priority cards' ⓘ tips use the same pattern and are affected too.

## Open questions

None.

## Review findings

### Round 1 — Ship: yes

Reviewed 2026-09-09: the uncommitted working-tree delta of `feature/cyrex-extended-keys` on top of
2f88f88 (23 modified files, no new files: 13 src, 5 tests, 5 docs). Build green (22 pre-existing
warnings, none in touched files); `dotnet test`: Infrastructure 1065/1065, App affected filter
130/130 (full App project 1676/1676 before the last 9 tests were added). **Hardware verification has
not run yet** — it follows the fix pass; the acceptance criterion's bytes are pinned by the service
test (`Write_ModTap_PointsOnlyThePhysicalSlot_…`), not by a board read-back.

#### Plan conformance
- Implemented as planned: `EncodeModTap` / `TryDecodeModTap` (BinaryPrimitives, 0x0105 pinned) plus
  the `KeyCodeToRecord` inverse; service manages `0x81` (outputs from their own identity, time
  range-checked, `slot2` / `priority` must be unset, both outputs required → `Rejected` without a
  packet); `IsModTapModeAvailable`; MT mode row; single-key editor (physical chip from the overlay,
  Hold pre-filled with the physical key, Hold / Tap chips, Time (ms) slider 0..1000 default 100 with
  the value right-aligned and the hint line); `KeyDialogPurpose { Remap, PickExtendedKeyOutput }`
  with a callback `Action<(TypeByte, Bitmask, ScanCode, Label)>`, title "Choose the {0} key", no
  Macros tab, no staging / write / toast, callback dropped on every close; Confirm →
  `ExtendedKeyRecord.ModTap(slot, time, hold, tap)`; row `CAPS [MT] CTRL (L) ESC  150 ms` with output
  labels resolved from `AssignmentTargets` and a hex fallback; en + uk for all 12 new keys;
  knowledge-base, glossary and key-assignment-popup addendum; tests in all six listed test files.
  Proto, storage schema and the shared model's fields untouched.
- Deviates from plan (all recorded in *Implementation notes*, live sections reconciled): the **System
  tab is hidden in pick purpose** (the MT record has no code type for a consumer command — the plan's
  "Keys + System" would only produce refusals); typed keys also pick; the RESET link hides; time out
  of range is **refused** by the service rather than clamped (the plan said "clamped", ambiguous —
  the slider cannot produce such a value, and refusing matches the SOCD priority precedent);
  constants named `ExtendedKeyTime.MinMs/MaxMs/DefaultMs` + `TimeMsMin/Max` (codec) instead of
  `TimeMin/Max/Default`; `ExtendedKeyEntry.ModTap` factory added (no field change); Hold / Tap render
  as chips, not labels; the badge keeps phase 1's red `#FF383C` rather than the plan's "orange".
- Beyond the plan, and correct: `SmartInfiniteExtendedKeysService.IsManagedRecord` — the write path
  classified "unmanaged" by TYPE only, so a managed-type record the decoder refuses (a SOCD foreign
  nibble since phase 2; now an MT mouse-button output) was **dropped on the next write and its slot
  restored to factory** while the read path reported it blocked. Now both paths agree; pinned for
  SOCD (`Write_AroundASocdWithAForeignPriorityNibble_…`) and MT (`Read_ModTapWithAMouseButtonOutput_…`),
  and the SOCD plan carries a dated note.
- Missing / partial: the API-intake validation of an MT item (S1 below); acceptance criterion 5
  (firmware tap/hold behaviour) and the live uk fit / dialog layout (AC 8, QA 2 / 7) await the
  hardware pass.

#### Code
- [x] **S1. Pulled MT items are stored unvalidated** —
  `src/ProoveGaming.App/Services/PresetConfigurationApplier.cs:142-155` (`ApplyExtendedKeys`) — Should-fix
  **Problem / why it matters:** the SOCD round's S1 closed exactly this gap for `priority`; the MT
  fields are open in the same way. An API v2 item `{mode: 0x81, slot1: 30, time_ms: 5000}` (or one
  with `tap_key` missing / `null`, or an output whose `type_byte` is not `0x10`) is stored as-is. On
  the next preset push the host answers `Rejected` for the whole set ("Mod-Tap time 5000 ms is outside
  0..1000" / "the tap output … is not a plain keyboard key"), the banner goes up and every record of
  that preset stays off the board — one bad row blocks the good ones. The row meanwhile renders with
  the hex fallback (`0x0000`) for a missing output, which reads as a bug. The plan's Review focus asks
  for "both outputs required" on both sides; the host has it, the App's intake does not.
  `ExtendedKeyTime.IsDefined` exists for this and is currently unused.
  **Fix:** in `ApplyExtendedKeys`, skip an MT item unless `ExtendedKeyTime.IsDefined(timeMs)` and both
  `hold_key` and `tap_key` are keyboard records (`TypeByte == 0x10` and not `None`) — the same
  "malformed → skipped" treatment as the SOCD priority. Alternative: clamp the time and keep the
  outputs — quieter, but invents a threshold the user never chose; skipping is the honest one.

#### Tests
- [x] After S1: an MT item with `time_ms` 1001 / -1, a missing `tap_key`, and a consumer-type
  `hold_key` are each skipped while their neighbours are stored —
  `tests/ProoveGaming.App.Tests/PresetConfigurationApplierTests.cs`
  (`PresetConfigurationApplierExtendedKeysTests`).
- The "150 ms" / hint wording and the dialog layout in pick purpose are not observable in this
  harness (`LocalizationManager.GetString` returns the key; no headless Avalonia) — accepted, checked
  live in the hardware pass.

#### Nits
- `ExtendedKeyRecord.IsSingleKey` (instance property, `src/ProoveGaming.App/Models/ExtendedKeyRecord.cs:101`)
  is unused — only the static `ExtendedKeyMode.IsSingleKey` is called. Drop it or use it.
- The comment at `DeviceDetailsViewModel.ExtendedKeys.cs:296` calls a zero threshold "always tap";
  with `hold ≥ 0 ms` every press satisfies the hold rule, so it is "always hold". The behaviour
  (hide "0 ms") follows the plan; only the comment is wrong.
- `ExtendedKeyEditorTimeMs`'s setter raises `ExtendedKeyEditorTimeMs` twice (once via `SetProperty`,
  once in `RaiseExtendedKeyEditorTimeChanged`) — `DeviceDetailsViewModel.ExtendedKeys.cs:126-135`.
  Harmless; per-drag, not per-frame.
- The MT row is the widest of the four (`CAPS [MT] CTRL (L) ESC  150 ms` plus the trash, roughly
  320 px of 336): the `*` parameter column absorbs the squeeze and trims to "…" first. Check on the
  live run with the uk "150 мс" — `ExtendedKeysSectionView.axaml:445`.

#### What's done well
- The pick purpose is contained: one enum, one callback, one setter that ends the pick on **every**
  close path (✕, delivery, editor cancel, device reload), and the remap path is untouched apart from
  the `FindTypedTarget` extraction — the `PickPurpose_*` tests pin that a pick never writes, stages or
  toasts and that the very next dialog is an ordinary remap dialog.
- The read/write agreement fix in the service is the right shape (one predicate drives both paths)
  and came with the SOCD regression test the previous round assumed existed.
- Range checks sit where the plan wanted them: `EncodeModTap` throws, the service answers `Rejected`
  without a packet, the editor clamps to the slider — and the decoder deliberately returns the time
  as found so a foreign record stays aligned and preserved.
- Both resource files carry every key; the hint / title strings were written for both languages, and
  the physical-chip refusal is a message rather than a silent no-op.

#### Verdict
Ship: yes — no blockers; S1 is the API-intake robustness gap on the pull path only (a few lines, with
the helper already in place), and the hardware pass is still owed. First: S1, then its applier test,
then the two nits in the ExtendedKeys partial.
