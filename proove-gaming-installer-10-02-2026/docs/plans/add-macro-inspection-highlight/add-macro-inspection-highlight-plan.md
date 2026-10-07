# Macro list: selectable rows that highlight the keys the macro is bound to

Follow-up to [`remove-macro-list-selection`](../remove-macro-list-selection/remove-macro-list-selection-plan.md),
which made the Macros-page list display-only. The user now wants the rows interactive again,
with **new** selection semantics: selecting a macro shows *where it is bound* on the device
image. Assignment still happens only in the Key Assignment Dialog (keyboards) / Buttons
section (mice) — that part of the previous plan stands.

> **2026-08-31 — Inspection rows are keyboard-only now.**
> [`fix-mouse-macro-list-assignment`](../fix-mouse-macro-list-assignment/fix-mouse-macro-list-assignment-plan.md)
> reverted the **mouse** row to the pre-#104 assignment semantics after QA reported the lost
> gesture; on mice the always-visible hotspot chips already answer "where is this macro
> bound?", so the list radio assigns again there. Everything below still describes keyboards
> accurately (`IsInspected`, the `macroBound` key highlight, the `MacrosInspect` group).

## Goal

Give the Macros section's macro list back its radio button and interactivity, as an
**inspection** affordance: hovering a row shows the usual hover tint and a hand cursor;
clicking a row (or its radio) marks that macro as selected and highlights, on the on-screen
keyboard (or the mouse hotspots), every key/button the macro is currently assigned to. The
selection never assigns or unassigns anything — it answers "which keys play this macro?",
which today requires hovering keys one by one.

## Scope

**In:**

- Macros section rows become `RadioButton`s again (shared `macroItemRow` style: hover tint,
  checked border, radio circle) plus a hand cursor.
- New per-macro inspection state (`MacroViewModel.IsInspected`) — exclusive across the list,
  independent from `IsSelected` (which the Key Assignment Dialog still uses to mean "bound
  to the dialog's key").
- Keyboard: a new highlight visual on overlay keys bound to the inspected macro
  (`KeyAssignmentOptionViewModel.IsMacroBindingHighlighted` → a `macroBound` class on the
  key button), distinct from and subordinate to the click-selection highlight.
- Mouse: the hotspots whose button is bound to the inspected macro get the selected-look
  accents (marker/border/background) via an equivalent flag, without touching the real
  hotspot selection.
- Highlight lifecycle: recomputed when bindings change (assign/reset from any surface),
  cleared when the inspected macro is deleted, when the macro library reloads (device
  switch), and when the user leaves the Macros section.
- Tests for the new VM behavior; doc reconciliation (previous plan's notes, `docs/CONTEXT.md`).

**Out:**

- No change to assignment flows, the Key Assignment Dialog, the hover tooltip / its ✕, or
  the kebab menu.
- No change to the dialog's MACROS tab rows (they keep `IsSelected` = bound-to-this-key).
- No un-check gesture: clicking the already-inspected macro keeps it inspected (a
  `RadioButton` cannot uncheck itself); leaving the section clears it.

## Options considered

1. **Separate inspection state (`IsInspected`) + dedicated highlight flags (recommended).**
   New properties on `MacroViewModel`, `KeyAssignmentOptionViewModel` and
   `DevicePartHotspotViewModel`; the list's radio binds `IsInspected`, and a VM sweep maps
   the inspected macro onto per-key/per-hotspot highlight flags.
   - + No interference with the three existing selection models that already share state
     (`IsSelected` on macros feeds the dialog; `IsSelected` on keys is shared by the
     Key-Assignments radio and the Actuation per-key select; hotspot `IsSelected` drives the
     Buttons flow). Round 2's B1 was exactly a cross-model bleed — this avoids the class of
     bug by construction.
   - − Three small new properties and one more sweep to keep in sync.
2. **Reuse the old semantics: re-bind the row to `IsSelected` and highlight via the keys'
   `IsSelected`.** Rejected: macro `IsSelected` means "bound to the currently selected key"
   and is swept by `RefreshMacroListSelection` on every key click — the user's inspection
   choice would be overwritten by the next key click, and checking a row would falsely show
   in the dialog. Setting key `IsSelected` for highlight would fight `SelectedPhysicalKey`
   single-select and the Actuation page's use of the same flag.

## Recommended approach

Option 1, concretely:

- **Row markup** (`MacrosSectionView.axaml`): back to
  `<RadioButton Classes="macroItemRow" GroupName="MacrosInspect" IsChecked="{Binding IsInspected, Mode=TwoWay}" Command="{Binding InspectCommand}" CommandParameter="{Binding}">`
  with the radio ellipses (`macroRadioOuter`/`macroRadioInner` — their `:checked` styles
  work unchanged), the name (+ full-name `ToolTip.Tip`) and the kebab. No unassign ✕ in the
  row (unchanged from the previous plan). The local `Border.macroDisplayRow` style is
  removed. The hover tint comes from the shared `macroItemRow:pointerover:not(:checked)`
  style; the hand cursor is added to the shared `macroItemRow` style in `App.axaml`
  (deliberately also improving the dialog's rows — same affordance, same style, per the
  don't-duplicate-UI rule).
- **VM state**: `MacroViewModel` gains `IsInspected` (INPC) and
  `IRelayCommand<MacroViewModel>? InspectCommand` (wired in `WireMacro`).
  `DeviceDetailsViewModel` gains `_inspectedMacro`, an `InspectMacro(macro)` relay command
  (sweep `IsInspected` exclusively, then refresh highlights), `ClearMacroInspection()`, and
  `RefreshMacroBindingHighlights()`. *(Amended 2026-08-14 after live verification:)*
  `InspectMacro` also **drops the leftover key selection** on keyboards — right after
  assigning, the bound key is still the selected key (the dialog leaves it that way), and
  the stronger `.selected` fill eclipsed the `.macroBound` box on exactly that key, so
  checking the radio looked like nothing happened. With the selection cleared, the board
  lights precisely the macro's keys. (Mouse hotspot selection is not cleared — its accents
  OR together, so nothing is eclipsed there.)
  - keyboard: for each `PhysicalKeys` key, `IsMacroBindingHighlighted =
    _inspectedMacro != null && BoundMacroForKey(key)?.Id == _inspectedMacro.Id` (guard the
    sweep with `_inspectedMacro is null` fast-path that only clears);
  - mouse: for each hotspot, highlighted when `_buttonMacros` maps its button to the
    inspected macro.
- **Lifecycle hooks**: `RefreshMacroListSelection()` (already called after every
  assign/reset/key-click/section-change) additionally calls
  `RefreshMacroBindingHighlights()`; `ActiveSection` setter clears inspection when the new
  section is not Macros; `LoadMacroLibraryAsync` clears it (rows are recreated); the
  delete-macro confirm path clears it when the deleted macro is the inspected one.
  *(PR #104 M1:)* `PopulateKeyLayout` ends with the same `RefreshMacroListSelection()` call,
  because a **preset switch** rebuilds `PhysicalKeys` (fresh view models, flags at default)
  without reloading the macro library — the checked radio survives, so its highlights must
  be re-derived against the new keys.
- **Keyboard visual** (`RasterKeyboardOverlayView`): new style
  `Button.rasterKey.macroBound /template/ ContentPresenter#PART_ContentPresenter` — the
  design's box (`#6357FF` border, fill `#6357FF` at 0.3 opacity), declared **between** the
  hover and `.selected` styles so a click-selected key still reads stronger. Code-behind
  applies the class in `RebuildKeyboard` from the flag and toggles it in
  `OnKeyPropertyChanged` (extending the existing `IsSelected` listener to also watch
  `IsMacroBindingHighlighted`).
- **Mouse visual** (`DevicePartHotspotViewModel`): new `IsMacroBindingHighlighted` flag that
  ORs into the existing computed colors (`MarkerStrokeColor`, `ButtonBackgroundColor`) and
  raises them — the hotspot lights up like a selected one without becoming
  `SelectedHotspot`.

## File-level change list

| File | Intent |
|---|---|
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/MacrosSectionView.axaml` | Row template: display-only `Border` → `RadioButton.macroItemRow` (GroupName `MacrosInspect`) with radio ellipses, bound to `IsInspected`/`InspectCommand`; drop the `macroDisplayRow` style; update the header comments. |
| `src/ProoveGaming.App/App.axaml` | Add `Cursor=Hand` setter to the shared `RadioButton.macroItemRow` style (section list and dialog rows both gain it). |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/ButtonActionGroupViewModel.cs` | `MacroViewModel`: add `IsInspected` (INPC) and `InspectCommand`. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Buttons.cs` | Wire `InspectCommand` in `WireMacro`; add `InspectMacro` command, `_inspectedMacro`, `ClearMacroInspection`, `RefreshMacroBindingHighlights` (keys + hotspots); call the refresh from `RefreshMacroListSelection`; clear inspection in `LoadMacroLibraryAsync` and on inspected-macro delete. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs` | `ActiveSection` setter: clear inspection when leaving the Macros section. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/KeyAssignmentOptionViewModel.cs` | Add `IsMacroBindingHighlighted` (INPC). |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DevicePartHotspotViewModel.cs` | Add `IsMacroBindingHighlighted`; OR it into `MarkerStrokeColor` / `ButtonBackgroundColor` and raise them on change. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml` | New `.rasterKey.macroBound` style between hover and `.selected`. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml.cs` | Apply/toggle the `macroBound` class from `IsMacroBindingHighlighted` (initial in `RebuildKeyboard`, live in `OnKeyPropertyChanged`). |
| `tests/ProoveGaming.App.Tests/DeviceDetailsKeyboardMacroBindingTests.cs` | New tests: inspect → bound keys flagged; inspect other macro → flags move; unassign → flag drops; leave section / reload library → inspection cleared; dialog `IsSelected` unaffected by inspection. |
| `tests/ProoveGaming.App.Tests/DeviceDetailsMacroAssignmentTests.cs` | Mouse-side test: inspect → bound hotspot highlighted; unassign → cleared. |
| `docs/plans/remove-macro-list-selection/remove-macro-list-selection-plan.md` | Dated note: rows are selectable again for inspection (this plan); display-only stands only in the sense that rows still never assign. |
| `docs/plans/add-macro-button-assignment/add-macro-button-assignment-plan.md` | Extend the existing 2026-08-14 note: rows are now an inspection control, still not an assignment one. |
| `docs/CONTEXT.md` | Add a one-line **Macro inspection (Macros page)** glossary entry. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/SelectedButtonHeaderView.axaml` | *(follow-up 2026-08-14)* Comment: document the Macros page's fixed slot. |

**Follow-up (2026-08-14): stable list position.** The selected-key header appearing /
disappearing (key click, inspection clearing the selection, reset) shifted "+ NEW MACRO",
the search box and the whole list up and down by ~46 px. The Macros page now wraps
`SelectedButtonHeaderView` in an always-visible fixed 38 px slot (`MacrosSectionView.axaml`),
so the list never moves; the Buttons page call site is unchanged.

## Acceptance criteria

- [ ] Given the Macros section with macros in the list, when the pointer moves over a row,
  then the row shows the hover tint and a hand cursor (kebab unchanged).
- [ ] Given macro M assigned to keys W and E (keyboard), when the user clicks M's row (or
  its radio circle), then the radio marks checked and both W and E get the purple
  bound-macro highlight box on the on-screen keyboard; all other keys are unchanged.
- [ ] Given macro M inspected, when the user clicks another macro N (bound to key R), then
  M's radio unchecks, N's checks, W/E highlights drop and R highlights.
- [ ] Given macro M inspected and highlighted on key W, when W's binding is removed (tip ✕,
  dialog RESET, or header Reset), then W's highlight disappears without re-clicking the row.
- [ ] Given macro M inspected, when the macro is assigned to an additional key via the
  dialog, then the new key highlights as soon as the assignment lands.
- [ ] Given macro M inspected, when the user switches to another section and back to Macros,
  then no macro is checked and no key is highlighted (inspection resets).
- [ ] Given macro M inspected, when M is deleted via the kebab, then the highlight clears.
- [ ] Clicking a macro row never assigns, unassigns, or opens the dialog; clicking a key
  still opens the dialog exactly as before; the dialog's MACROS tab checked row still
  reflects the dialog's key (inspection does not leak into it).
- [ ] On a mouse, inspecting a macro highlights the hotspot(s) of the button(s) it is bound
  to (marker/border accents), and clears by the same rules.
- [ ] Given a key selected (e.g. right after assigning via the dialog), when a macro row's
  radio is checked, then the key selection clears (`SelectedPhysicalKey` null, no
  `.selected` key, the "selected key + RESET" header hides) and only the inspected macro's
  keys are highlighted — the highlight is never eclipsed by a leftover selection.
- [ ] Clicking a key after inspecting still selects it and opens the dialog as before
  (inspection does not break re-selection).
- [ ] Given the Macros section, when the selected-key header appears or disappears (key
  click, inspection, reset), then "+ NEW MACRO", the search box and the macro list stay at
  the same vertical position — nothing jumps.
- [ ] Given macro M inspected with keys highlighted, when the user switches the preset (the
  header dropdown, available on every section), then the radio stays checked and the
  highlights re-appear on the rebuilt keyboard per the new preset's bindings.
- [ ] Given a highlighted key, when the pointer hovers it, then the key still gives hover
  feedback (a slightly stronger fill), like every other key.
- [ ] `dotnet build ./src/ProveGaming.slnx` and the App test suite pass.

## Test strategy

New feature (`add-*`) — the following tests are required coverage, in
`tests/ProoveGaming.App.Tests`:

- `DeviceDetailsKeyboardMacroBindingTests` (keyboard, existing fixture):
  - inspect a macro bound to two keys → exactly those keys have
    `IsMacroBindingHighlighted == true`;
  - inspect a different macro → highlight moves;
  - reset/unassign a highlighted key → its flag drops after the existing refresh path (no
    manual re-inspect);
  - assign the inspected macro to another key (dialog path) → that key's flag raises;
  - `ActiveSection = Buttons` → inspection and all flags cleared; returning to Macros does
    not restore them;
  - device switch (library reload) → cleared;
  - deleting the inspected macro → cleared;
  - inspection does not modify `MacroViewModel.IsSelected` (dialog semantics intact).
- `DeviceDetailsMacroAssignmentTests` (mouse): inspect → bound hotspot's
  `IsMacroBindingHighlighted` true, others false; unassign → cleared.

Existing suites must stay green — especially the Actuation selection tests (the new flags
must not touch `IsSelected`) and the dialog tests.

Deliberately not covered by automated tests: the visual styles (hover tint, hand cursor,
the `macroBound` box, style precedence vs `.selected`) and the class-toggling code-behind —
view-level, no headless harness; covered by QA below.

## QA (manual test steps)

1. Connect a supported keyboard and open Details → Macros. Create two macros ("A", "B").
2. Move the mouse over a macro row. Expected: the row background tints and the cursor
   becomes a hand; each row shows an empty radio circle on the left.
3. Click a key on the keyboard (e.g. W), assign macro "A" from the dialog's MACROS tab.
   Repeat for key E. Assign macro "B" to key R.
4. Click macro "A" in the list. Expected: its radio fills in, and keys W and E light up
   with a purple box on the keyboard; R does not.
5. Click macro "B". Expected: the radio moves to "B", W/E highlights disappear, R lights up.
6. With "A" inspected, hover key W and click the ✕ in its tooltip. Expected: W's highlight
   disappears immediately; E stays highlighted.
7. With "A" inspected, assign "A" to one more key via the dialog. Expected: that key lights
   up as soon as the assignment completes.
8. Switch to another section (e.g. RGB) and back to Macros. Expected: no macro is checked,
   no key is highlighted.
9. Inspect "A", then delete it via the row's 3-dot menu. Expected: highlights clear with it.
10. Click a highlighted key. Expected: the assignment dialog opens as usual; the clicked key
    shows the stronger selected fill while highlighted keys keep the lighter one.
11. Clicking macro rows never changes any key binding — verify by pressing the keys.
12. If a supported mouse is available: assign a macro to a side button (Buttons section),
    open Macros, click that macro's row. Expected: the corresponding button hotspot on the
    mouse image gets the selected-style accents; clicking another macro or leaving the
    section clears it.

## Review focus

1. `null-safety` — the sweeps run against collections that reload asynchronously
  (`PhysicalKeys`, `Macros`, `_buttonMacros`); `_inspectedMacro` must never dangle after a
  reload or delete.
2. `UI-thread affinity` — all refreshes ride existing UI-thread paths
  (`RefreshMacroListSelection`); no new dispatching.
3. `performance hot-path` — `RefreshMacroBindingHighlights` runs on every key click via
  `RefreshMacroListSelection`; it must fast-path to a cheap clear when nothing is inspected
  and stay allocation-light (no LINQ in the sweep).

## Performance & quality

- `RefreshMacroBindingHighlights` is O(keys) with dictionary lookups per key
  (`BoundMacroForKey` over the cached `MacroBindingsBySlot`) — same cost class as the
  existing `SyncAssignmentTargetSelection` sweep that already runs per click. Guard: when
  `_inspectedMacro is null` and nothing is flagged, do nothing.
- Property setters no-op on unchanged values (`SetProperty`), so re-sweeps do not storm
  `PropertyChanged`/class toggles.
- No async work, no shared-singleton state, no disposables introduced.

## Risks

- **Three selection models on two flags.** Keys' `IsSelected` is shared by Key-Assignments
  and Actuation; macros' `IsSelected` by the dialog. The new flags must remain strictly
  separate — any "convenient" reuse recreates round 2's B1. The tests pin this.
- **Stale highlights.** Every binding mutation path must end in
  `RefreshMacroListSelection` (they all do today — assign, stage, reset, dialog commit); if
  a future write path skips it, highlights will lag. The refresh living inside
  `RefreshMacroListSelection` keeps the coupling in one place.
- **Radio cannot uncheck.** The only ways to clear inspection are picking another macro or
  leaving the section. Accepted; noted in Scope.
- **Pre-existing (observed during live verification, NOT introduced here): Rampage slot
  drift across sessions.** The same `key_macro_bindings` row (`matrix_index 34`) resolved to
  key **W** in one app session and key **E** in the next on the same Rampage unit — so a
  binding made in an earlier session can highlight (and tooltip, and dialog-check) a
  different key than the one originally clicked. All read paths share `BoundMacroForKey`,
  so the UI stays internally consistent per session, but the mapping itself is
  session-dependent. Likely the factory-table / catalog slot resolution racing the device
  read; deserves its own investigation (out of scope for this plan).
- **Cursor on the dialog rows.** Adding `Cursor=Hand` to the shared style also affects the
  dialog's macro rows — intended, but it is a visible change outside the requested surface;
  flagged here so the reviewer does not treat it as accidental drift.

## Open questions

1. Highlight color: the plan uses the selection family (`#6357FF` box at 0.3 fill) so
   inspected keys read as "related but not selected". If a visually distinct color is
   preferred (e.g. the design system introduces one), say so before `/coder` runs.
2. Should inspection also survive within the session when returning to the Macros section?
   The plan clears it on leaving the section (matches the reset-on-navigation behavior of
   the rest of the page). Confirm.

## Review findings

### Round 1 — Ship: yes

Scope: the whole uncommitted delta on top of commit `b68cc56` — the inspection feature
(this plan), its two same-day follow-ups (selection-eclipse fix, fixed 38 px header slot),
and the unassign toast documented in the sibling plan's Amendment addendum
(`../remove-macro-list-selection/remove-macro-list-selection-plan.md`). Build clean;
`ProoveGaming.App.Tests` 783/783 green. The inspection highlight and the eclipse fix were
additionally verified on live hardware (Rampage) with screenshots.

#### Plan conformance
Plan: this plan.
- Implemented as planned: every file-list item, including the follow-up rows; the
  `IsInspected` / `IsMacroBindingHighlighted` flags stay strictly separate from all three
  `IsSelected` selection models (the round-2 B1 class of bug is structurally excluded, and
  `UnassignMacroFromKey_IsANoOp_InActuationPerKeyMode` still pins the actuation guard);
  lifecycle clears (section leave, device switch, macro delete, edited-instance carry-over
  in `ReplaceEditedMacro`) are all present and tested.
- Deviates from plan: two behaviour amendments were made during verification and folded
  back into the live sections the same day — `InspectMacro` clears the keyboard selection
  (eclipse fix), and the header got a fixed slot; both are recorded in this plan, so plan
  and code agree.
- Related but journaled elsewhere: the ✕-unassign toast (`ShowToastCore` refactor,
  `Macros_UnassignToast_Format` en/uk, truth-checked raise, hide-on-inspection) belongs to
  the sibling plan's Amendment; reviewed here, no findings beyond S1 below.
- Accepted limitation (per plan): radio cannot uncheck; inspection resets on section leave.
- Pre-existing risk logged in Risks (slot drift on Smart Infinite boards) — confirmed
  out of scope; all read paths share `BoundMacroForKey`, so this feature is internally
  consistent with the tooltip and the dialog regardless.

#### Code
- [x] **S1. A toast can outlive the selection its UNDO acts on when the ✕ moves the
  selection** — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs:1404`
  (`UnassignMacroFromKey` → `SelectPhysicalKeyCore` at line 1375) — Should-fix
  **Problem / why it matters:** the undo toast's UNDO is guarded on
  `ReferenceEquals(SelectedPhysicalKey, _remapToastKey)`. Every other selection move ends
  the undo window by hiding the toast (`OpenKeyAssignmentDialog`, and now `InspectMacro`),
  but the ✕ path's own `SelectPhysicalKeyCore` call does not. Scenario: unassign key A via
  its tip ✕ (toast A up, 5 s window) → within that window hover key B's tip and click its ✕
  while A's device write is still in flight → B becomes the selection, B's unassign
  early-outs on the in-flight gate (correctly silent) → toast A stays on screen offering an
  UNDO that now silently no-ops (guard fails). A visible button that does nothing.
  **Fix:** call `HideRemapToast()` at the top of `SelectPhysicalKeyCore` (one line; makes
  "a new selection ends the previous undo window" hold on every path — the dialog caller
  already hides, so the duplication is harmless). Alternatively hide it only in
  `UnassignMacroFromKey` before the reset.

#### Tests
- [x] After S1: extend `UnassignMacroFromKey_RaisesTheUndoToast` (or add a sibling) to pin
  that a second ✕ on another key hides/replaces the first toast even when the second
  unassign is gated — `tests/ProoveGaming.App.Tests/DeviceDetailsKeyboardMacroBindingTests.cs`.
  *(Closed: `UnassignMacroFromKey_HidesThePreviousToast_BeforeTheNextWrite` — the fake's
  `DuringKeyWrite` hook observes the toast state inside the second unassign's in-flight
  window on Cyrex; fix is `HideRemapToast()` at the top of `SelectPhysicalKeyCore`.)*

#### Nits
- The eclipse-fix branch (`InspectMacro`, `DeviceDetailsViewModel.Buttons.cs:882`) nulls the
  selection without re-sweeping `AssignmentTargets.IsSelected`; the stale flag is invisible
  (only the dialog consumes it, and opening the dialog re-syncs), noted so the next reader
  doesn't chase it.
- `Panel Height="38"` in `MacrosSectionView.axaml` duplicates the header's 14+24 geometry;
  both files carry keep-in-sync comments, which is the accepted mitigation.

#### What's done well
- The mid-write safety of nulling `SelectedPhysicalKey` from `InspectMacro` was verified
  against `ApplyAssignmentTargetCoreAsync`, which captures device/key/slot before its first
  await precisely for this class of interruption — no race, no NRE.
- The highlight is derived state riding the existing `RefreshMacroListSelection` bundle, so
  every binding mutation path updates it for free; the null-inspected fast path keeps the
  per-click sweep allocation-free.
- Live-hardware verification caught two real issues (selection eclipse, header jump) that
  headless tests could not, and both fixes landed with tests and plan amendments the same
  day.

#### Verdict
Ship: yes — no blockers; S1 is a narrow 5-second-window UX gap with a one-line fix. First:
(1) S1 + its test, (2) run this plan's QA checklist 1–12 plus the sibling plan's steps
11–15 on a real keyboard, (3) commit — the whole feature set is still uncommitted on `main`
(branch first), and note the pre-existing slot-drift risk as its own ticket.

### Round 2 — external review (PR #104, boikoserhii84) — fixes applied

The branch shipped as PR #104 (`bugfix/macros-tooltip`); the PR review raised 1 Major +
7 Minor. All actionable items are fixed on the branch:

- [x] **M1. Preset switch left the checked radio without highlights** — fixed with the
  reviewer's Option A: `PopulateKeyLayout` now ends with `RefreshMacroListSelection()`
  (`DeviceDetailsViewModel.KeyAssignments.cs`), re-deriving highlights against the rebuilt
  keys. Test: `InspectedHighlight_SurvivesAKeyLayoutReload` (asserts the rebuilt key
  instance is flagged).
- [x] **m1. Toast raised after the selection moved away** — the raise is now guarded with
  `ReferenceEquals(SelectedPhysicalKey, key)` in `UnassignMacroFromKey`.
- [x] **m2. Gated ✕ still moved the selection** — the in-flight gate
  (`_assignmentWriteInFlight`) is checked **before** `SelectPhysicalKeyCore`; with a macro
  bound it is equivalent to the old post-selection `CanResetSelectedMacroTarget` check, so
  the no-op contract now truly has no side effects.
- [x] **m3. Highlighted keys lost hover feedback** — added
  `Button.rasterKey.macroBound:pointerover` at 0.35 fill (between the highlight's 0.3 and
  `.selected`'s 0.4) in `RasterKeyboardOverlayView.axaml`.
- [x] **m4. Stale dialog comment** — rewritten to the assignment-vs-inspection split and
  the real group name (`KeyAssignmentDialog.axaml`).
- [x] **m5. Stale test rationales** — both comments now reference the hover-tip ✕ / the
  dialog's macro row instead of the removed list-row ✕
  (`DeviceDetailsKeyboardMacroBindingTests.cs`).
- [x] **m6. Mouse unassign coverage** — `InspectMacro_HighlightsTheBoundHotspot` now also
  pins unassign → accent cleared (macro still inspected) and re-bind → accent back.
- [x] **m7 (actionable part)** — unused `x:Name="Root"` removed from
  `MacrosSectionView.axaml`. The remaining m7 bullets are notes/accepted mitigations
  (38 px slot comment pairing, stale-but-invisible `AssignmentTargets.IsSelected`,
  theoretical `MacroLibrarySlots` undo fallback) — no code change.

`ProoveGaming.App.Tests`: 785/785 green after the fixes.
