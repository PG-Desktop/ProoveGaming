# Plan: add the Toggle (TGL) extended key

Date: 2026-09-09. Owner: architect. Phase 4 of 4 — depends on
[`../add-cyrex-extended-keys/add-cyrex-extended-keys-plan.md`](../add-cyrex-extended-keys/add-cyrex-extended-keys-plan.md)
and reuses the single-key editor and output-key picker introduced by
[`../add-extended-key-mod-tap/add-extended-key-mod-tap-plan.md`](../add-extended-key-mod-tap/add-extended-key-mod-tap-plan.md)
(implement MT first). No proto, schema or shared-model change.

Protocol source: Keyborder-v3 `handleSaveTGLData` (bundle lines 74530–74546), `TGLContent`
(71689–72104), `TGLKeyContent` (73112); folded into
[`docs/knowledge-base/smart-infinite-protocol/advanced-keys-rapid-snap.md`](../../knowledge-base/smart-infinite-protocol/advanced-keys-rapid-snap.md)
by this change.

> **Status note (2026-09-24).** Extended Keys is hidden on every shipping model until the
> implementation is finished — `"SupportsExtendedKeys"` is `false` for Cyrex and Aress in
> `src/ProoveGaming.DeviceHost/appsettings.json`, which also stops the probe and every write to
> the board. Nothing in this plan runs while that flag is off; see
> [`../add-cyrex-extended-keys/add-cyrex-extended-keys-plan.md`](../add-cyrex-extended-keys/add-cyrex-extended-keys-plan.md) for the full note.

## Goal

Let one physical key act as a **toggle**: a tap shorter than a threshold latches the output key
on (continuous trigger) and the next tap releases it; a hold at or above the threshold behaves as a
normal momentary key. The user picks the physical key on the keyboard overlay, the output key from
the key picker, and the threshold in milliseconds.

## Scope

In: TGL record encode/decode (`0x82`), `ExtendedKeyMode.Toggle` accepted by the service, the TGL row
in "Add extended key", the single-key editor variant (physical key, one output key, time slider),
row formatting (`T [TGL] W  100 ms`), preset/API round trip via the existing `slot1`, `time_ms`,
`hold_key` fields, strings, tests, glossary.

Out: shared model / proto / schema changes; DKS and END.

## Options considered

### A. Reuse the MT single-key editor with one output chip (recommended)

- Pro: the vendor's TGL editor is the MT editor minus the second output key; same slider, same
  picker; one code path to test.

### B. Separate TGL editor

- Con: a second copy of the physical-key chip, picker hookup and slider — the repo's "second copy"
  rule says extract instead.

## Recommended approach

**Record** (`handleSaveTGLData`):

```
[0x82, 0x04, time_lo, time_hi, codeType, code]       6 bytes
time      uint16 LE, MILLISECONDS (0..1000; vendor slider step 1, default 100) — not travel steps
codeType  1 = HID usage · 2 = modifier bitmask · 3 = mouse button (unused here)
code      the output key
```

Single-key mode: only the **physical key's** slot triplet becomes `{0x82, offset}`; the output key's
own slot is untouched. The output key is any keyboard key or modifier from the picker, encoded from
its matrix-record triple (`usage != 0 → (1, usage)` else `(2, mask)`) — **not** from a factory
slot, because the output need not be a physical key of this board (vendor picker includes F-keys and
numpad).

Semantics (vendor summary line): "hold ≥ N ms is the normal trigger behaviour; otherwise the tap
toggles the key's continuous trigger on/off".

**Infrastructure.** `EncodeToggle(timeMs, outputKey)` / `TryDecodeToggle`; service encoder switch
accepts `Toggle` with `time_ms` range-checked against 0..1000 (out of range → `Rejected`, not clamped —
the MT convention) and `hold_key` required (none → `Rejected`); `slot2`, `tap_key`, `priority` must be
unset (→ `Rejected`).

**App.** `IsToggleAvailable = true`; editor mode `Toggle` reuses the MT single-key editor
(`ExtendedKeySingleKeyEditor`): "Set physical key" chip filled from the overlay click (refusal rules
as RS), one output chip labelled "Toggled key" opening the shared output-key picker (MT plan), a
`Time (ms)` slider 0..1000 step 1 default 100 with the value shown, and the summary line "Hold ≥
{0} ms triggers normally; a shorter tap toggles the key on/off". Confirm enabled when the physical
key and the output key are set. Row: `T [TGL] W  100 ms` (badge colour blue per the design's mode
palette). The output key label comes from the same target-list lookup MT uses.

## File-level change list

- `src/ProoveGaming.Infrastructure/Classes/SmartInfiniteAdvancedKeyArea.cs` — edit: `EncodeToggle`,
  `TryDecodeToggle`.
- `src/ProoveGaming.Infrastructure/Services/SmartInfiniteExtendedKeysService.cs` — edit: encoder
  switch accepts `Toggle`; validation.
- `src/ProoveGaming.Infrastructure/Classes/KeyboardExtendedKeys.cs` — edit: `ExtendedKeyEntry.Toggle`
  factory (no field change).
- `src/ProoveGaming.App/Models/ExtendedKeyRecord.cs` — edit: `ToggleRecordBytes` (6) and the
  `ExtendedKeyRecord.Toggle` factory; the time constants are the MT phase's shared `ExtendedKeyTime`
  (0 / 1000 / 100), `IsSingleKey` already covered Toggle.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.ExtendedKeys.cs` — edit:
  `IsToggleModeAvailable`, `CanAddToggle`, `StartToggleEditor`, the output chip's caption / column span,
  the Toggle time hint and picker role, Confirm path, `BuildExtendedKeyRow` TGL case (`{ms} ms`).
- `src/ProoveGaming.App/Services/PresetConfigurationApplier.cs` — edit: API-intake validation of a
  Toggle item (time range, keyboard `hold_key`, no `tap_key`), the S1 pattern of the SOCD / MT rounds.
- `src/ProoveGaming.App/Views/DeviceDetails/Sections/ExtendedKeysSectionView.axaml` — edit: TGL mode
  row (icon, label, ⓘ); the single-key editor's first output chip takes its caption from the ViewModel
  and spans both columns when the Tap chip is hidden. The code-behind needed no change.
- `src/ProoveGaming.App/Resources/Strings.en.axaml` / `Strings.uk.axaml` — edit: "Toggled key" (chip),
  "Toggled" (picker title role), the Toggle time hint format (label / description / badge exist from
  phase 1).
- `docs/knowledge-base/smart-infinite-protocol/advanced-keys-rapid-snap.md` — edit: TGL record
  section (incl. the note that the vendor's list decoder has an un-padded hex bug we must not copy).
- `docs/CONTEXT.md` — edit: **Toggle (TGL)** one-liner.
- `tests/ProoveGaming.Infrastructure.Tests/SmartInfiniteAdvancedKeyAreaTests.cs` — edit: TGL
  encode/decode incl. `time = 0x0105` (low byte < 0x10 — the vendor bug case) and a modifier output.
- `tests/ProoveGaming.Infrastructure.Tests/SmartInfiniteExtendedKeysServiceTests.cs` — edit: TGL
  write points only the physical slot; output key's slot unchanged; missing output → Rejected;
  time outside 0..1000 → Rejected; `tap_key` / `slot2` set → Rejected; coexistence with RS/SOCD/MT
  (6-byte offset shift); mouse-button output preserved as unmanaged; the "unmanaged mode" test now
  uses DKS.
- `tests/ProoveGaming.App.Tests/DeviceDetailsExtendedKeysViewModelTests.cs` — edit: TGL row
  visible; default 100 ms; Confirm payload; row text; physical key in another record refused.
- `tests/ProoveGaming.App.Tests/PresetConfigurationBuilderTests.cs` / `ApplierTests.cs` — edit:
  `time_ms` + `hold_key` round trip for a Toggle record.

## Acceptance criteria

- [ ] Given the section → "Add extended key" lists "Toggle (TGL)" with ⓘ "Tap to toggle continuous
  trigger on or off; hold for normal trigger behaviour."
- [ ] Given the TGL row → the editor shows a physical-key chip, one "Toggled key" chip, a Time (ms)
  slider at 100, and the summary line; Confirm enabled only when both chips are filled (clicking the
  physical key pre-fills the toggled key with itself, as MT pre-fills Hold, so one click can fill both).
- [ ] Given physical **T**, toggled key **W**, 100 ms → one `SetExtendedKeys` with
  `{Toggle, slot1 = T, time_ms = 100, hold_key = {0x10, 0, 0x1A}}`; the board reads back
  `[0x82, 0x04, 0x64, 0x00, 1, 0x1A]` pointed at by T's slot only; W's slot is unchanged; the row
  shows `T [TGL] W  100 ms`.
- [ ] Given the board → a quick tap of T starts continuous "w"; a second tap stops it; holding T for
  longer than 100 ms types "w" only while held. (Hardware check.)
- [ ] Given a toggled output of a modifier (Left Shift) → record bytes `[…, 2, 0x02]`; holding T
  briefly latches Shift (letters type upper-case until the next tap).
- [ ] Given T already in an RS/SOCD/MT record → picking T is refused.
- [ ] Preset switch removes/restores the record with its time and output; API v2 round trip holds.
- [ ] `LanguageResourceParityTests` passes.

## Test strategy

Change to an existing feature area (the foundation + MT tests stay green and are extended): the
codec, service, VM, storage-round-trip cases in the change list. Time values at the byte boundary
(`0x0105`) are a required codec case. Not automated: firmware toggle behaviour (QA 3–4), layout.

## QA (manual test steps)

1. Open Extended Keys on the Cyrex. **Expected:** "Toggle (TGL)" row with ⓘ tooltip.
2. Click it, click **T** on the picture, click the "Toggled key" chip and pick **W** in the picker,
   leave 100 ms, Confirm. **Expected:** a row "T  TGL  W  100 ms"; Save Preset lights up.
3. In a text editor tap **T** quickly. **Expected:** "w" repeats continuously. Tap **T** again.
   **Expected:** it stops.
4. Hold **T** for a second. **Expected:** "w" types only while held, then stops.
5. Delete the row; add T → Left Shift as the toggled key. **Expected:** a quick tap of T latches
   Shift (letters come out upper-case); another tap releases it.
6. Move the slider to 500 ms and confirm a new record. **Expected:** the row shows "500 ms"; a hold
   shorter than half a second now toggles instead of typing.
7. Switch the language to Ukrainian. **Expected:** editor and row texts translated, nothing clipped.
8. Save Preset, switch presets and back. **Expected:** the row and behaviour return.

## Review focus

- `null-safety` — `hold_key` required for Toggle; `tap_key`/`slot2` rejected if set.
- `localization` — summary format string in both languages.
- `gRPC contract` — no new fields; document that `hold_key` doubles as the toggled output.

## Performance & quality

None beyond phase 1.

## Risks

- Whether the firmware treats `codeType 2` (modifier) outputs as latching modifiers is a hardware
  question (QA step 5); if not, the picker restricts outputs to non-modifier keys for TGL.
- The vendor's list decoder mis-reads times whose low byte is < 0x10 (un-padded hex concat). Our
  decoder uses `BinaryPrimitives`; the test pins it.

## Implementation notes (2026-09-09, /coder)

Decisions taken while building, where the plan left room or the code forced a choice:

- **Time range: refused, not clamped.** The plan's "clamped to 0..1000" was reconciled to the MT / SOCD
  convention: `EncodeToggle` throws, the service answers `Rejected` without a packet, the editor's
  slider cannot produce such a value, and the API intake skips the item (below). The live sections say
  so now.
- **`tap_key` on a Toggle is refused, not ignored.** The service refuses a Toggle entry whose `TapKey`
  is not `None` (as it refuses `slot2` / `priority`), per the Review focus; so the App's API intake
  (`PresetConfigurationApplier.ApplyExtendedKeys`) skips a Toggle item with a `tap_key`, a time off
  the slider or a non-keyboard / missing `hold_key` — the same "malformed → skipped, neighbours kept"
  rule the SOCD and MT rounds' S1 established, applied up front this time rather than after review.
- **Pre-fill kept.** The shared single-key editor pre-fills the first output chip with the physical key;
  for Toggle that chip is "Toggled key", so one overlay click yields "T toggles T" (the auto-run /
  push-to-talk latch), and Confirm is enabled at once. The plan is silent on a TGL pre-fill; the shared
  code path and the useful default decided it. Acceptance criterion 2 records it.
- **One chip, two columns.** The Tap chip hides in Toggle mode (`ShowExtendedKeyEditorTapChip`) and the
  first output chip binds `Grid.ColumnSpan` to `ExtendedKeyEditorOutputChipSpan` (3 for Toggle, 1 for
  MT) so it does not sit at half width beside an empty column; its caption binds
  `ExtendedKeyEditorOutputCaption` ("Hold" / "Toggled key"). No second copy of the editor.
- **Picker title.** `KeyDialog_PickTitle_Format` ("Choose the {0} key") takes a new role string
  `ExtendedKeys_Toggled` ("Toggled" / uk "Перемикання") rather than the chip caption, which would read
  "Choose the Toggled key key".
- **Storage gate.** `CanAddToggle` shares the 8-byte `IsExtendedKeyStorageFull` gate although a Toggle
  record is 6 bytes: one "storage full" note and one rule for the four rows, at the cost of the 6–7
  free-bytes corner case. `ExtendedKeyRecordBytes(Toggle)` is 6 for the local free-bytes update.
- **Row.** `T [TGL] W  100 ms` — the toggled output renders through the MT row's `HoldLabel` chip; the
  parameter text hides at 0 ms as MT's does (one rule for the single-key modes).
- **Shared model.** `ExtendedKeyEntry.Toggle` / `ExtendedKeyRecord.Toggle` factories were added next to
  the MT ones (no field, proto or schema change); the toggled output rides `hold_key`.
- **New strings** (en + uk): `ExtendedKeys_ToggledKey`, `ExtendedKeys_Toggled`,
  `ExtendedKeys_Toggle_TimeHint_Format`.
- **Fix pass (2026-09-09, review S1): skip, not normalise.** `ApplyExtendedKeys` now skips a single-key
  item (MT or TGL) whose `slot2` is not `NoSlot` or whose `priority` is not 0 — the established
  "malformed → skipped, neighbours kept" rule rather than silently zeroing the two fields — so the
  intake now mirrors every refusal the host applies to a single-key entry.
- **Existing test retargeted.** `Write_ModeNotManagedByThisBuild_IsRejectedWithoutWriting` used Toggle
  as the unmanaged example; it now uses DKS (`(ExtendedKeyMode)0x80`), the only TYPE left that this
  build preserves without managing.

### Hardware verification — 2026-09-09, PG Cyrex (VID 28E9 PID 3272), fw 0x0002, uk locale

Driven through the real App and DeviceHost (`dotnet run`, Development) with synthetic mouse input
only, reading the host log (`%TEMP%/Logs/log20260909.txt`), after the fix pass:

- Read first: `advKeys=1024 B`; `GetExtendedKeys` → `0 managed, 0 blocked, 1024/1024 bytes free`. The
  "Toggle (TGL)" row rendered under Mod-Tap with its icon and ⓘ, like the other three (AC 1).
- Editor: "Фізична клавіша" chip outlined, one "Перемикана клавіша" chip stretched across both
  columns (the `Grid.ColumnSpan` binding works), "Час (мс) 100 мс" with the slider at 10 %, the
  Toggle hint "Утримання ≥ 100 мс спрацьовує звичайно; коротший натиск вмикає або вимикає клавішу.",
  Confirm disabled. Clicking **T** filled the physical chip, pre-filled the toggled chip with **T** and
  enabled Confirm; the hint moved on to "Усе задано…" (AC 2).
- Picker: "Оберіть клавішу: Перемикання" opened on the КЛАВІШІ tab alone, T highlighted as the current
  output; clicking **W** closed it and put W in the chip. No `SetKeyMapping`, no `[KEY]` write and no
  toast in the host log during the pick.
- Confirm at 100 ms: one `SetExtendedKeys`; on the wire `0x3B` 19 packets / 1024 B then `0x18`
  7 packets / 384 B, 19/19 and 7/7 acked; `extended keys verified: 1 managed record(s)`. The next read
  snapshot (the delete's) logged **`record type=0x82 offset=0x0000 bytes=82046400011A slots=[20]`** —
  i.e. `82 04 64 00 01 1A` pointed at by T's slot 20 **only**; W's slot 17 is not a pointer and the
  full-matrix verify passed with it untouched (AC 3).
- Row: `T [TGL] W  100 мс 🗑` with the blue badge; T lit on the overlay, W not; Save Preset lit.
- Trash: one write with an empty set, `extended keys verified: 0 managed record(s)`; row gone,
  highlight cleared, Save Preset back to grey. **The board was left with no records.**
- No EXT warnings, verify mismatches or refusals in the host log.
- **Not verified:** the physical tap → latch / hold → momentary behaviour and the Left Shift latch
  (AC 4, the behaviour half of AC 5; QA 3–6) need a hand on the board — the record bytes the firmware
  reads are confirmed, the behaviour is not. The modifier record bytes (`… 02 02`) are pinned by the
  codec and service tests, not by a board read-back.
- The ⓘ tooltips still render "Avalonia.Controls.TextBlock" (the pre-existing window-level tooltip
  template bug recorded in the MT round; out of scope, unchanged).

## Open questions

None.

## Review findings

### Round 1 — Ship: yes

Reviewed 2026-09-09: the uncommitted working-tree delta of `feature/cyrex-extended-keys` on top of
e7b665c (19 modified files, no new files: 9 src, 3 docs, 7 tests). Build green (no warnings in the
touched files); `dotnet test ./src/ProveGaming.slnx` (Debug): Installer 8, Infrastructure 1082, App
1700, all passing (+17 / +24 over the MT round). **Hardware verification has not run yet** — it follows
the fix pass; the acceptance criterion's bytes and pointers are pinned by the service test
(`Write_Toggle_PointsOnlyThePhysicalSlot_AndLeavesTheOutputKeysSlotAlone`), not by a board read-back.

#### Plan conformance
- Implemented as planned: `EncodeToggle` / `TryDecodeToggle` (BinaryPrimitives, `0x0105` pinned,
  6-byte record `[0x82, 0x04, time, codeType, code]`); service manages `0x82` (output from its own
  `{0x10, mask, usage}` identity via `KeyCodeToRecord`, time range-checked, `slot2` / `priority` /
  `tap_key` must be unset, missing output → `Rejected` without a packet; a mouse-button, multi-slot
  or over-range record stays unmanaged on both paths through `IsManagedRecord`); `IsToggleModeAvailable`;
  the TGL mode row with icon and ⓘ; the shared single-key editor with one "Toggled key" chip spanning
  both columns, the Time (ms) slider at 100 and the Toggle hint; Confirm →
  `ExtendedKeyRecord.Toggle(slot, time, output)`; row `T [TGL] W  100 ms` in the blue `ToggleBadgeHex`;
  en + uk for the three new keys; knowledge-base example bytes and the `hold_key` note; glossary line;
  tests in all five listed test files plus the record-model and storage tests.
- Deviates from plan (all recorded in *Implementation notes*, live sections reconciled): time out of
  range is **refused**, not clamped (the MT / SOCD convention); the toggled-key chip is **pre-filled
  with the physical key** (the plan is silent; AC 2 now says so); `ExtendedKeyTime` is reused instead
  of new `ToggleTimeMin/Max/Default` constants; `ExtendedKeyEntry.Toggle` / `ExtendedKeyRecord.Toggle`
  factories added (no field change); a second string `ExtendedKeys_Toggled` feeds the picker title;
  `CanAddToggle` shares the 8-byte storage gate; the code-behind was not touched (nothing needed).
- Beyond the plan, and correct: the API-intake validation in `PresetConfigurationApplier` was written
  up front (time range, keyboard `hold_key`, no `tap_key`) — the S1 the SOCD and MT rounds each had
  to ask for. See S1 below for the part it still misses.
- Missing / partial: none in the change list. Acceptance criteria 3 (board read-back), 4 (firmware
  toggle behaviour) and the latch half of 5 need the hardware pass; the uk fit of "Перемикана
  клавіша" on the chip and the spanning chip's layout are not observable in the test harness.

#### Code
- [x] **S1. A pulled Toggle (or MT) item with `slot2` / `priority` set passes intake and blocks the whole set** —
  `src/ProoveGaming.App/Services/PresetConfigurationApplier.cs:157-165` (`ApplyExtendedKeys`) — Should-fix
  **Problem / why it matters:** the service refuses a single-key entry whose `Slot2 != NoSlot` or
  `Priority != 0` (`SmartInfiniteExtendedKeysService.cs:215-219`, and the MT case above it), but the
  intake only checks time, outputs and (for Toggle) `tap_key`, then stores `slot2` / `priority` as
  received. An API v2 item `{mode: 0x82, slot1: 20, slot2: 5, time_ms: 100, hold_key: W}` — or one
  with `priority: 2` — is stored; on the next preset push the host answers `Rejected` for the entire
  set ("a Toggle entry carries no second slot, no priority and no tap output"), the banner goes up and
  every record of that preset stays off the board. The same gap exists for MT since the previous
  round (pre-existing, outside this plan, but it is the same predicate). The plan's Review focus asks
  for "`tap_key`/`slot2` rejected if set" — the host has it, the intake has half of it.
  **Fix:** in `ApplyExtendedKeys`, for `ExtendedKeyMode.IsSingleKey(mode)` skip the item when
  `(GetInt(item, "slot2") ?? NoSlot) != NoSlot || priority != 0` — the same "malformed → skipped,
  neighbours kept" rule as the other intake checks (one condition, covers MT and TGL). Alternative:
  normalise the two fields to `NoSlot` / 0 and keep the item — quieter, and defensible because neither
  field means anything on a single-key mode, but it departs from the established skip rule; either is
  acceptable, say which.

#### Tests
- [x] After S1: a Toggle item with `slot2 = 5` and one with `priority = 2` (and an MT item with
  `slot2` set) are each skipped while their neighbours are stored —
  `Apply_SingleKeyItemWithASecondSlotOrAPriority_SkipsOnlyThoseItems` —
  `tests/ProoveGaming.App.Tests/PresetConfigurationApplierTests.cs`
  (`PresetConfigurationApplierExtendedKeysTests`, next to `Apply_ToggleOutsideTheFirmwareRules_SkipsOnlyThoseItems`).
- The "{0} ms" substitution in the Toggle hint, the uk chip caption fit and the spanning chip are not
  observable in this harness (`LocalizationManager.GetString` returns the key; no headless Avalonia) —
  accepted, checked live in the hardware pass.

#### Nits
- `ExtendedKeyRowViewModel` still describes its output chips as MT-only ("MT hold / tap",
  `HoldLabel` = "The hold output of a single-key mode") while the TGL row now rides `HoldLabel` —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/ExtendedKeyRowViewModel.cs:8-10, 49-50`. A word each.
- `IsExtendedKeyStorageFull`'s summary says "the RS and SOCD rows are disabled" — all four rows are,
  since MT — `DeviceDetailsViewModel.ExtendedKeys.cs` (`IsExtendedKeyStorageFull`).
- `ExtendedKeys_Toggled` ("Toggled") exists only to feed `KeyDialog_PickTitle_Format`; right for the
  en grammar, but a second key a translator has to keep in step with `ExtendedKeys_ToggledKey`.
- The TGL row's ⓘ inherits the pre-existing window-level tooltip bug ("Avalonia.Controls.TextBlock",
  MT round, out of scope) — expected on the live run, not a regression.

#### What's done well
- The mode landed entirely through the extension points phase 1 built: one codec pair, one
  `IsManagedType` member, one decode and one encode case, one availability flag, one row case, one
  Confirm case — no second editor, no second picker, and the XAML change is a caption binding and a
  column span rather than a copied block.
- Read and write agree again on what "managed" means for `0x82` (multi-slot, over-range and
  mouse-button records stay verbatim with their slot blocked), and the coexistence test pins the
  6-byte offset shift that the 8-byte records could have hidden.
- Refusals sit where the earlier rounds put them: encoder throws, service refuses without a packet,
  the editor cannot produce the value, the intake skips — and this time the intake came with the
  change rather than after review.
- The tests exercise real values (T = 20, W = 17, the modifier `[…, 2, 0x02]`, `0x0105`), assert
  that W's slot and highlight are untouched, and cover the mode switch of caption / hint and the
  consumer-command guard the plan's Review focus names.
- Both resource files carry every key; the parity test is green.

#### Verdict
Ship: yes — no blockers; S1 is the last corner of the API-intake rule (a one-line predicate covering
MT and TGL), and the hardware pass is still owed for AC 3–5. First: S1, then its applier test, then
the two doc-comment nits.
