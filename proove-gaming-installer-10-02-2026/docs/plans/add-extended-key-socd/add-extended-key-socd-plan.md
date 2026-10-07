# Plan: add the Instant Release (SOCD) extended key

Date: 2026-09-09. Owner: architect. Phase 2 of 4 — depends on
[`../add-cyrex-extended-keys/add-cyrex-extended-keys-plan.md`](../add-cyrex-extended-keys/add-cyrex-extended-keys-plan.md)
(the Extended Keys section, codec, service, gRPC `ExtendedKey` message, storage table and the
Rapid Snap mode). This plan adds **one mode** through the extension points that plan defines; it
changes no proto field, no schema and no shared model.

Protocol source: the vendor bundle extraction in
[`docs/knowledge-base/smart-infinite-protocol/advanced-keys-rapid-snap.md`](../../knowledge-base/smart-infinite-protocol/advanced-keys-rapid-snap.md)
plus the SOCD-specific research summarised in *Recommended approach* below (`handleSaveSOCDData`,
`SOCDContent`, `PrioritySetting`, `SOCDKeyContent` in Keyborder-v3; the research notes are folded
into the knowledge-base file by this change).

> **Status note (2026-09-24).** Extended Keys is hidden on every shipping model until the
> implementation is finished — `"SupportsExtendedKeys"` is `false` for Cyrex and Aress in
> `src/ProoveGaming.DeviceHost/appsettings.json`, which also stops the probe and every write to
> the board. Nothing in this plan runs while that flag is off; see
> [`../add-cyrex-extended-keys/add-cyrex-extended-keys-plan.md`](../add-cyrex-extended-keys/add-cyrex-extended-keys-plan.md) for the full note.

## Goal

Let the user link two keys as an Instant Release (SOCD) pair: when both are held past their
actuation point, the firmware resolves them by a chosen priority — last input wins, neither fires,
Key 1 always wins, or Key 2 always wins. Same board region and same UI shell as Rapid Snap; the pair
editor gains a "Priority Settings" block, and the configured row shows the priority text.

## Scope

In: SOCD record encode/decode (`0x83`, priority in the LEN high nibble), `ExtendedKeyMode.Socd`
accepted by the DeviceHost service, the SOCD row in "Add extended key", the pair editor with the four
priority options (default *Last Input Priority*), configured-row formatting (`E [SOCD] R  Last Input
Priority`; for absolute priority the winning key is marked), preset/API round trip via the existing
`priority` field, en + uk strings, tests, glossary.

Out: any change to the shared model, proto, storage schema, service write sequence or verify. MT,
TGL (their plans). Mouse-button (`codeType 3`) keys — unreachable on this board's factory table.

## Options considered

### A. Priority as a separate editor step after Confirm

- Con: two-step flow the vendor does not have; an extra write if the user changes their mind.

### B. Priority radio block inside the pair editor, default Last Input (recommended)

- Pro: matches the vendor (`PrioritySetting` 2×2 radio grid under the key chips) and the design
  screenshot ("Last Input Priority" under the row). One write on Confirm.

### C. Reuse the RS editor verbatim and expose priority only on the configured row

- Con: priority could then only be changed by delete + re-add. Rejected.

## Recommended approach

**Record** (verified against `handleSaveSOCDData`, bundle line 74490):

```
[0x83, LEN, slot1, codeType1, code1, slot2, codeType2, code2]      8 bytes
LEN = (priority & 0x0F) << 4 | 6      →  0x06 last input · 0x16 mutual cancel · 0x26 key 1 · 0x36 key 2
```

Same payload shape as RS; `codeType`/`code` derive from each key's **factory** record exactly as RS
does (`1` = HID usage, `2` = modifier bitmask). Both slots' matrix triplets point at the record
(`PAIR_MODES` includes SOCD). Parsing already masks `LEN & 0x0F` (foundation codec).

| Priority | Encoding | Label (en) | Vendor tooltip |
|---|---|---|---|
| 0 | `0x06` | Last Input Priority | When both keys pass the actuation point, the last pressed key overrides the first. |
| 1 | `0x16` | Mutual Cancel | When both keys pass the actuation point, neither key triggers. |
| 2 | `0x26` | Absolute Priority (Key 1) | One key always overrides the other. |
| 3 | `0x36` | Absolute Priority (Key 2) | One key always overrides the other. |

**Infrastructure.** `SmartInfiniteAdvancedKeyArea.EncodeSocd(slot1, key1, slot2, key2, priority)` /
`TryDecodeSocd`; the service's per-mode encoder switch gains `Socd` (priority range-checked 0..3,
else `Rejected`); the parser's SOCD nibble handling is already there — extend its tests with our own
records.

**DeviceHost.** No handler change: the generic `ExtendedKey.priority` is already carried.

**App.** In the ExtendedKeys partial: `IsSocdAvailable = true` (turns the row on);
`ExtendedKeyEditorMode.Socd` uses the pair editor (Key 1 / Key 2 chips, same pick/refusal rules) plus
a `SocdPriority` property bound to a 2×2 radio group (`RadioButton` styled as bordered cards, each
with an ⓘ tooltip; items 2/3 append the picked key's label in the accent colour, falling back to
"Key 1"/"Key 2"); Confirm builds `ExtendedKeyRecord(Mode = Socd, Slot1, Slot2, Priority)`. Row
formatting: `Key1 [SOCD] Key2` with the priority text as `ParameterText` for 0/1; for 2/3 the
winning key's chip carries a small "Absolute Priority" badge (the design's yellow SOCD colour).
Editing an existing pair is not offered in this phase (delete + re-add), same as RS.

**Storage / presets / API.** Nothing new: `priority` column and JSON field exist since phase 1;
this plan adds the SOCD cases to the round-trip tests.

## File-level change list

- `src/ProoveGaming.Infrastructure/Classes/SmartInfiniteAdvancedKeyArea.cs` — edit: `EncodeSocd`,
  `TryDecodeSocd`, `SocdPriorityFromLength`, `SocdLength(priority)`.
- `src/ProoveGaming.Infrastructure/Services/SmartInfiniteExtendedKeysService.cs` — edit: encoder
  switch accepts `Socd`; priority validated 0..3.
- `src/ProoveGaming.App/Models/ExtendedKeyRecord.cs` — edit: `SocdPriority` constants and
  `IsPairMode` covering Socd.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.ExtendedKeys.cs` — edit:
  `IsSocdAvailable`, `SocdPriority` + option list, editor mode switch, `BuildRow` SOCD case, Confirm
  path.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/ExtendedKeyRowViewModel.cs` — edit:
  `Key1BadgeText` / `Key2BadgeText` for the absolute-priority marker.
- `src/ProoveGaming.App/Views/DeviceDetails/Sections/ExtendedKeysSectionView.axaml` (+ `.cs`) —
  edit: SOCD mode row; "Priority Settings" block (title, description, 2×2 radio cards with ⓘ) shown
  when the editor mode is SOCD; row badge marker.
- `src/ProoveGaming.App/App.axaml` — edit only if the radio-card style is not already shared (grep
  first; the DPI/Settings sections may already have a card-radio style).
- `src/ProoveGaming.App/Resources/Strings.en.axaml` / `Strings.uk.axaml` — edit: SOCD label and
  description already exist from phase 1; add "Priority Settings", its description, the four option
  labels and tooltips, "Absolute Priority" badge, row texts.
- `docs/knowledge-base/smart-infinite-protocol/advanced-keys-rapid-snap.md` — edit: add the SOCD
  record section (table above, priority semantics, list rendering).
- `docs/CONTEXT.md` — edit: **Instant Release (SOCD)** one-liner.
- `tests/ProoveGaming.Infrastructure.Tests/SmartInfiniteAdvancedKeyAreaTests.cs` — edit: SOCD
  encode/decode for all four priorities, nibble round trip, invalid priority.
- `tests/ProoveGaming.Infrastructure.Tests/SmartInfiniteExtendedKeysServiceTests.cs` — edit: SOCD
  write → record with `0x26` LEN and both pointers; priority 4 → Rejected without write; RS + SOCD
  coexisting in one area.
- `tests/ProoveGaming.App.Tests/DeviceDetailsExtendedKeysViewModelTests.cs` — edit: SOCD row
  visible; default priority 0; Confirm writes `Mode = Socd, Priority = n`; row text per priority;
  a key already in an RS pair is refused for SOCD.
- `tests/ProoveGaming.App.Tests/PresetConfigurationBuilderTests.cs` / `ApplierTests.cs` — edit:
  `priority` round-trips.

## Acceptance criteria

- [ ] Given the Extended Keys section → "Add extended key" shows "Instant Release (SOCD)" with an ⓘ
  reading "When two keys are pressed together, the configured key triggers based on priority."
- [ ] Given the SOCD row is clicked → the pair editor opens with Key 1 / Key 2 chips and a "Priority
  Settings" block of four options, *Last Input Priority* preselected; Confirm enabled only with both
  keys.
- [ ] Given Key 1 = E, Key 2 = R, priority left default → Confirm → one `SetExtendedKeys` with the
  full set including `{Socd, E, R, 0}`; the board reads back `[0x83, 0x06, slotE, 1, 0x08, slotR,
  1, 0x15]` and both slots point at it; the row shows `E [SOCD] R  Last Input Priority`.
- [ ] Given *Absolute Priority (Key 1)* → LEN reads back `0x26`; the row marks E as the priority key.
  Given *Mutual Cancel* → `0x16`; the row text reads "Mutual Cancel".
- [ ] Given the board with E/R last-input → holding E then pressing R outputs R only; releasing R
  resumes E. With Mutual Cancel → holding both outputs nothing. (Hardware check.)
- [ ] Given an RS pair on A/D → picking A for an SOCD pair is refused with the "already assigned"
  message; the reverse holds.
- [ ] Given a saved preset with the SOCD pair → switching away removes it, switching back restores it
  with the same priority; the API v2 configuration carries `priority`.
- [ ] `LanguageResourceParityTests` passes; uk option labels fit their cards.

## Test strategy

Change to an existing feature area with tests (the foundation's) — they must stay green and be
extended:

- Infrastructure codec/service tests: the SOCD cases listed in the change list (four priorities,
  nibble mask on parse, invalid priority rejected, coexistence with RS, unmanaged records intact).
- App VM tests: SOCD row visibility, default priority, write payload per priority, row formatting,
  cross-mode slot refusal, preset push carries priority.
- Builder/applier: `priority` survives build → apply → build.

Not automated: firmware behaviour for each priority (QA steps 3–5), Avalonia layout of the radio
cards (QA step 2 with uk).

## QA (manual test steps)

1. Open the Cyrex page → Extended Keys. **Expected:** "Add extended key" now lists "Instant Release
   (SOCD)" under Rapid Snap, with an ⓘ tooltip describing it.
2. Click it. **Expected:** Key 1 / Key 2 chips plus a "Priority Settings" block with four options;
   "Last Input Priority" is preselected. Switch the language to Ukrainian. **Expected:** the four
   option cards are fully readable, nothing clipped.
3. Click **E** then **R** on the picture, keep the default, Confirm. **Expected:** a row
   "E  SOCD  R" with "Last Input Priority" appears; Save Preset lights up. In a text editor hold **E**
   then press **R**: only "r" repeats; release R: "e" resumes.
4. Delete the row, add E/R again with "Mutual Cancel". **Expected:** holding both keys types nothing;
   releasing one resumes the other.
5. Delete, add E/R with "Absolute Priority (Key 1)". **Expected:** the row marks E as the priority
   key; holding R then pressing E switches to "e"; pressing R while E is held does nothing.
6. Try to add a Rapid Snap pair using **E**. **Expected:** refused — the key already has an extended
   key.
7. Save Preset; switch to another preset and back. **Expected:** the SOCD row and its priority return
   and behave as before.
8. Open Keyborder-v3 → Advanced Keys (optional). **Expected:** it shows the same SOCD pair with the
   same priority.

## Review focus

- `null-safety` — priority range checks on both sides; a foreign SOCD record with an unexpected
  nibble must still parse.
- `localization` — four option labels + tooltips in both languages.
- `gRPC contract` — no field additions; `priority` semantics documented on the existing field.

## Performance & quality

None beyond phase 1: one discrete write per Confirm; radio changes are UI-only until Confirm.

## Risks

- The `Absolute Priority` semantics (which key is "A") are inferred from the vendor UI (Key 1 = byte
  2's key); the hardware check in QA step 5 confirms it. If inverted, only the label mapping changes.
- Rendering the absolute-priority marker on a chip must not widen the row past 336 px with uk text.

## Implementation notes (2026-09-09, /coder)

Decisions taken while building, where the plan left room or the code forced a choice:

- **Absolute priority follows the physical key through normalisation.** Every write path stores a
  pair as `slot1 < slot2` (`ExtendedKeyRecord.Normalized()`), but the firmware's priority 2 / 3 name
  the record's *positions* (byte 2's key / byte 5's key). `Normalized()` therefore flips 2 <-> 3 when
  it swaps the slots, so "Key 1 = R wins" picked in the editor lands on the board as
  `[0x83, 0x36, slotE, .., slotR, ..]` and the row tags R. `ExtendedKeyRecord.Socd(slotA, slotB,
  priority)` takes the priority in the caller's Key 1 / Key 2 terms and normalises. Pinned by
  `Socd_AbsolutePriorityFollowsThePhysicalKey_WhenThePairIsNormalised`.
- **A foreign SOCD nibble is preserved, not managed.** `TryDecodeSocd` returns the raw high nibble
  (0..15) so a vendor-written record with priority 4..15 still parses and keeps the area aligned; the
  service's `TryDecodeManaged` only accepts 0..3 and otherwise treats the record as unmanaged (slots
  blocked, bytes carried verbatim). Decoding it as an entry would make the App's next full-set write
  refuse everything because of one record it cannot re-encode. `EncodeSocd` / `SocdLength` throw on
  a priority outside 0..3; the service range-checks first and answers `Rejected` without a packet.
- **Priority cards are a small option VM**, `SocdPriorityOptionViewModel` (new file, not in the plan's
  list): four instances built on the first SOCD editor open, `IsSelected` two-way bound to a
  `RadioButton`, the absolute cards carrying the picked chip's label ("Key 1" / "Key 2" until one is
  picked). The parent keeps `SocdPriority` as the single truth and resets it to *Last Input* on every
  editor open. Labels resolve at read time like the rest of the section.
- **Shared card style.** `RadioButton.optionCard` in `App.axaml` — the content-sized sibling of
  `macroItemRow` (same 5 % background, 2 px accent border when checked, hover tint) without its fixed
  56 px height, because the uk labels wrap to two lines. `macroItemRow` itself was left alone.
- **Absolute-priority marker** is a small bold tag in the SOCD badge colour *under* the favoured
  chip (`Key1BadgeText` / `Key2BadgeText`, brush bound from the row), not beside it: the row stays
  well inside 336 px with "Абсолютний пріоритет". The rule-based priorities use `ParameterText`,
  which now wraps to two lines before trimming (the uk "Пріоритет останнього натискання" outgrew the
  column in the first live run).
- **Cross-mode refusal** needed no new code: `IsExtendedKeySlot` is mode-agnostic, so a key in an RS
  pair is refused for SOCD and the reverse (pinned by `Socd_AKeyInARapidSnapPairIsRefused_AndTheReverse`).
- **Docs already in place.** The knowledge-base SOCD section and the `docs/CONTEXT.md` glossary line
  were written with the plan set; nothing to add there.

- **Superseded 2026-09-09 (MT phase).** The "record survives a rewrite verbatim" claim above held on
  the read path only: `PlanWrite` classified unmanaged records by TYPE, so a foreign-nibble SOCD record
  was dropped (and its slots restored to factory) by the next write. Fixed in the Mod-Tap change —
  `SmartInfiniteExtendedKeysService.IsManagedRecord` (managed TYPE **and** decodable) now drives both
  paths; pinned by `Write_AroundASocdWithAForeignPriorityNibble_CarriesItVerbatim_AndRefusesItsSlots`.

### Hardware verification — 2026-09-09, PG Cyrex (VID 28E9 PID 3272), fw 0x0002, uk locale

Driven through the real App and DeviceHost (`dotnet run`, Development), reading the host log:

- Section: the SOCD row rendered under Rapid Snap with its info tip; board read clean
  (`0 managed, 0 blocked, 1024/1024 bytes free`).
- Editor: Key 1 / Key 2 chips plus "Налаштування пріоритету" with the four cards, *Last Input*
  preselected, Confirm disabled until both chips were filled; the uk labels wrapped inside their cards
  with nothing clipped; the absolute cards switched from "Клавіша 1 / 2" to "E" / "R" once picked.
- E <-> R with each priority, one `SetExtendedKeys` each, all verified; the read before each delete
  logged the board's record: `83 06 12 01 08 13 01 15`, `83 16 …`, `83 26 …`, `83 36 …`, always
  `slots=[18, 19]` at offset 0 — the acceptance criteria's exact bytes for 0x06 / 0x16 / 0x26 / 0x36.
- Rows: `E [SOCD] R  Пріоритет останнього натискання`, `… Взаємне скасування`, and the absolute
  ones tagging E, then R, under the chip. Both keys lit on the overlay; Save Preset lit after each add
  and went grey after each delete.
- Every delete verified with `0 managed record(s)`; the board was left with no records.
- No EXT warnings or verify mismatches in the host log.
- **Not verified:** the physical typing behaviour per priority (QA steps 3–5) needs a hand on the
  board; the record bytes the vendor firmware reads are confirmed, the behaviour is not. The
  Key 1 = byte 2 semantics (Risks) therefore remain inferred from the vendor UI.

## Open questions

None — the four options and default are the vendor's; the mode is Cyrex + Aress like the section.

## Review findings

### Round 1 — Ship: yes

Reviewed 2026-09-09: the uncommitted working-tree delta of `feature/cyrex-extended-keys` on top of
dd4b283 (17 modified files, 2 new: `SocdPriorityOptionViewModel.cs`, `ExtendedKeyRecordTests.cs`).
Build green; `dotnet test`: Infrastructure 1049, App 1670, all passing. Hardware: E/R written with each
priority and read back as `83 06 / 16 / 26 / 36 …`, board left empty (Implementation notes).

#### Plan conformance
- Implemented as planned: codec `EncodeSocd` / `TryDecodeSocd` / `SocdLength` /
  `SocdPriorityFromLength`; service manages `0x83` with the priority range-checked before the
  encoder; `SocdPriority` constants; `IsSocdModeAvailable`; SOCD mode row; pair editor + "Priority
  Settings" 2x2 radio cards (default Last Input); Confirm → `{Socd, slot1, slot2, priority}`; row text
  for 0/1 and a chip marker for 2/3; en + uk for every new key; tests in all four listed test files.
  Proto, storage schema and shared model untouched. Knowledge-base section and glossary line already
  existed.
- Deviates from plan (all recorded in *Implementation notes*): a new `SocdPriorityOptionViewModel`
  file (plan listed no new file); `KeyboardExtendedKeys.cs` gained an `ExtendedKeyEntry.Socd`
  factory; the absolute-priority marker is a tag *under* the favoured chip rather than on it; a new
  shared `RadioButton.optionCard` style in `App.axaml` next to the fixed-height `macroItemRow`
  (justified — the uk labels wrap to two lines and `macroItemRow` is `Height=56`).
- Beyond the plan, and correct: `ExtendedKeyRecord.Normalized()` flips priority 2 <-> 3 when it swaps
  the slots. The plan did not name this, but without it every reversed pick (Key 1 = the higher slot)
  would silently favour the other key on the board. The three storage / API paths all funnel through
  `Normalized()`, so one fix covers them; pinned by `ExtendedKeyRecordTests` and the storage / applier
  tests.
- Missing / partial: none in the change list. Acceptance criterion 5 (typing behaviour per priority)
  is not verifiable without a hand on the board — record bytes are confirmed, behaviour is not.

#### Code
- [x] **S1. Pulled `priority` is not range-checked at intake** —
  `src/ProoveGaming.App/Services/PresetConfigurationApplier.cs:146` (`ApplyExtendedKeys`) — Should-fix
  **Problem / why it matters:** an API v2 configuration item `{mode: 0x83, priority: 7}` (another
  client, a hand-edited payload) is stored as-is. On the next preset push the App sends it, the host
  refuses the whole set ("SOCD priority 7 is not one of 0..3"), the banner goes up and *every* record
  of that preset stays off the board — one bad row blocks the good ones. Meanwhile the row renders
  with the `_ =>` fallback of `SocdPriorityLabel`, i.e. "Last Input Priority", which misdescribes it.
  The plan's Review focus asks for the range check on both sides; the host has it, the App's intake
  does not.
  **Fix:** in `ApplyExtendedKeys`, skip an SOCD item whose priority fails `SocdPriority.IsDefined`
  (the same treatment as a malformed item — `IsDefined` exists and is currently only used by tests).
  Alternative: clamp to `LastInput` — quieter, but invents a setting the user never chose; skipping
  is the honest one.

#### Tests
- [x] After S1: an SOCD item with `priority` 4 (and -1) is skipped while its neighbours are stored —
  `tests/ProoveGaming.App.Tests/PresetConfigurationApplierTests.cs`
  (`PresetConfigurationApplierExtendedKeysTests.Apply_SocdPriorityOutsideTheFirmwareRange_SkipsOnlyThatItem`).
- The formatted absolute tooltip ("E always overrides …") is not observable in tests because
  `LocalizationManager.GetString` returns the key there; accepted, the format string is pinned by the
  parity test and was checked live in both languages.

#### Nits
- ~~`ExtendedKeyRecord.SocdRecordBytes` (`src/ProoveGaming.App/Models/ExtendedKeyRecord.cs:78`) is
  unused~~ — closed in the fix pass: `ExtendedKeyRecordBytes(mode)` now names it for the Socd case.
- The VM property `DeviceDetailsViewModel.SocdPriority` shadows the `Models.SocdPriority` constants
  inside the partial, forcing `Models.SocdPriority.X` throughout
  (`DeviceDetailsViewModel.ExtendedKeys.cs:100`). `SelectedSocdPriority` would read better and drop
  the prefixes.
- A row with an absolute-priority tag is taller than its neighbours (the tag sits under the chip);
  visible in the live run, cosmetic.

#### What's done well
- The foreign-nibble decision is the right one: `TryDecodeSocd` reports the nibble as found so the
  area stays aligned, the service refuses to *manage* it, and the record survives a rewrite verbatim
  with its slots blocked — a decoded-but-unencodable entry would have made every later write fail.
- Range checks sit where the plan wanted them: `EncodeSocd` throws, the service answers `Rejected`
  without a packet, the App can only produce 0..3 from the editor.
- The editor state stays single-sourced (`_socdPriority`) with the cards as a two-way mirror, reset
  on every open; no re-entrancy (the callback fires on `true` only).
- Every user-facing string landed in both resource files; the uk cards and rows were checked on the
  running app, and the parameter text was made to wrap after the live run showed it truncating.
- Tests exercise the new behaviour with real values (priority 2/3 through the swap, coexistence with
  RS, out-of-range rejection), not defaults.

#### Verdict
Ship: yes — no blockers; S1 is a robustness gap on the API intake path only, a few lines to close.
First: S1, then its applier test, then the `SocdRecordBytes` nit.
