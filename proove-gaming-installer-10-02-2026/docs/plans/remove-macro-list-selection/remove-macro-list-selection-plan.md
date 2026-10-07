# Remove macro-list selection; assigned-macro tooltip on hover only

> **2026-08-14 — Follow-up:**
> [`add-macro-inspection-highlight`](../add-macro-inspection-highlight/add-macro-inspection-highlight-plan.md)
> made the macro rows selectable again, with **new** semantics: the radio is an inspection
> control (`IsInspected`) that highlights the keys/buttons the macro is bound to. What this
> plan removed stays removed — rows still never assign, the old bound-to-selected-key radio
> meaning lives only in the dialog, and the hover tooltip (+ its ✕) is unchanged.

> **2026-08-31 — Open question 1 answered: no (for mice).** QA reported the exact residual
> risk this plan's review flagged (a mouse user clicking a macro row and nothing assigning —
> BLAZE). [`fix-mouse-macro-list-assignment`](../fix-mouse-macro-list-assignment/fix-mouse-macro-list-assignment-plan.md)
> restored the pre-#104 assign semantics (radio mirrors the selected button's binding, click
> assigns, in-row ✕ unassigns) on **mouse** devices only. Keyboards keep everything this plan
> and its follow-up shipped: display-only-turned-inspection rows and dialog-only assignment.

## Goal

Make the Macros section's macro list a **display-only library**: rows no longer carry a radio
button, cannot be selected or clicked to assign, and only the kebab (3-dot Edit/Remove) menu
stays interactive. The indication of which macro a keyboard key is bound to moves from the
always-visible selected-key badge chip to the **existing hover tooltip** (which already names
the bound macro), and appears **only on mouse hover** — on both the Macros and the Key
Assignments sections, so the two keyboards look and behave identically. Assignment itself
stays where it already lives: the Key Assignment Dialog (opened by clicking a key on either
section's keyboard).

## Scope

**In:**

- `MacrosSectionView` macro-row template: drop the radio visuals, the checked highlight, the
  row's assign command/checked binding, and the in-row unassign ✕. Keep the name (+ full-name
  ToolTip), the search filter, the "+ NEW MACRO" button, the selected-target header, and the
  kebab menu.
- Remove the selected-key macro badge feature (`SelectedKeyMacroBadgeViewModel`, the
  `BadgeLayer` in `RasterKeyboardOverlayView`, and its refresh plumbing in
  `DeviceDetailsViewModel.KeyAssignments`).
- Remove the hover-tip suppression for the selected key in
  `RasterKeyboardOverlayView.ShowKeyTip` (it existed only to avoid stacking a tip on top of
  the badge).
- Rework the badge-oriented unit tests into hover-tooltip (`GetKeyRemapTooltip`) assertions.
- Update the plans that describe the old behaviour (dated notes, live sections).

**Out:**

- The Key Assignment Dialog's MACROS tab keeps its radio list, assign-on-click, and unassign
  ✕ — the dialog remains the assignment surface. Its `GroupName="KeyDialogMacros"` list and
  the shared `RadioButton.macroItemRow` / `macroRadioOuter/Inner` styles in `App.axaml` stay.
- Mouse hotspot macro chips (`DevicePartHotspotViewModel.AssignedMacroName` /
  `ShowAssignedMacro`) are unchanged — the request is about the keyboard surfaces.
- The Macros section's hidden Commands tab (dead UI behind `IsVisible="False"`) is untouched.
- The Buttons section is untouched.

## Options considered

1. **View-level display rows + delete the badge feature (recommended).** The section view
   stops binding `IsSelected`/`AssignCommand`; the badge VM, its XAML layer, its refresh
   calls, and its tests are removed. The hover tip already resolves the bound macro through
   `GetKeyRemapTooltip`, so no new tooltip code is needed.
   - - Smallest live surface; deletes an entire feature's code instead of disabling it;
     makes the two keyboards identical by construction.
   - − Deletes ~250 lines of geometry code and its tests that took real effort to get right;
     if the design later wants an always-visible chip back, it comes back from git history.
2. **Keep the badge but show it only while the pointer hovers the selected key; keep the
   RadioButton rows but make them hit-test-invisible except the kebab.** Rejected: a
   hover-driven badge duplicates what the hover tip already renders (two chips, two code
   paths, same content), and non-interactive RadioButtons keep dead selection state and
   styling around purely as decoration. It also would not make the Macros keyboard identical
   to the Key Assignments one — the badge layer only exists on one of them.

## Recommended approach

Option 1. Concretely:

- **Macro rows** become plain `Border` rows (56 h, `#0DFFFFFF` background — the same metrics
  the `macroItemRow` style uses) with the truncated name (+ `ToolTip.Tip` full name) and the
  kebab button. No `:checked` border, no `:pointerover` tint, no `Cursor=Hand` on the row
  body — the only interactive element is the kebab (which keeps its own hover). The style is
  unique to this view, so it lives in `MacrosSectionView.axaml`'s `UserControl.Styles`
  (the shared `RadioButton.macroItemRow` in `App.axaml` still has its second consumer, the
  dialog, and stays there).
- **Badge removal**: delete `SelectedKeyMacroBadgeViewModel.cs`, the `BadgeLayer` canvas in
  `RasterKeyboardOverlayView.axaml`, the `BadgeLayer` sizing/visibility lines in
  `RebuildKeyboard`, the `_selectedKeyMacroBadge` field, the `SelectedKeyMacroBadge`
  property, and `RefreshSelectedKeyMacroBadge` (and its call in
  `NotifySelectedMacroTargetChanged`). If the `KeyOverlayBounds` helpers lose their last
  caller with the badge gone, remove them too; if tests still use them, keep them.
- **Hover tip**: remove the `SelectedKeyMacroBadge.IsVisible && ReferenceEquals(...)`
  early-return in `ShowKeyTip` and its comment. Nothing else changes — the tip already shows
  the bound macro's name (or the remap target's label) via `GetKeyRemapTooltip`, appears on
  `PointerEntered` and hides on `PointerExited`, on both section keyboards (the dialog's
  target picker stays tip-less via `IsTargetPicker`).
- **VM selection state stays**: `MacroViewModel.IsSelected` and
  `RefreshMacroListSelection()` keep working exactly as today — the dialog's MACROS tab
  binds them. Only the section view stops rendering that state.
- **What replaces in-place assign/unassign on the Macros page**: clicking a key already
  opens the Key Assignment Dialog (`SelectPhysicalKey` gates on
  `IsButtonsSection || IsMacrosSection`); the dialog's MACROS tab assigns, its RESET link
  and macro-row ✕ unassign. The shared header's RESET link on the Macros page also remains.
  *(Amended 2026-08-14: the hover tooltip itself also carries an unassign ✕ for macro-bound
  keys — see the Amendment section below.)*

## File-level change list

| File | Intent |
|---|---|
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/MacrosSectionView.axaml` | Macro row: `RadioButton.macroItemRow` → plain display row (name + kebab); remove radio ellipses, `IsChecked`, `Command`, unassign ✕; add local display-row style; update the stale style-sharing comment at the top. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml` | Delete the `BadgeLayer` canvas subtree and its explanatory comments. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml.cs` | Remove `BadgeLayer` sizing + `BadgeLayer.IsVisible = !IsTargetPicker` in `RebuildKeyboard`; remove the badge-based early-return in `ShowKeyTip`. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` | Remove `_selectedKeyMacroBadge`, `SelectedKeyMacroBadge`, `RefreshSelectedKeyMacroBadge` (and its call in `NotifySelectedMacroTargetChanged`); remove `KeyOverlayBounds` helpers only if nothing else references them after the test rework. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/SelectedKeyMacroBadgeViewModel.cs` | Delete — the feature is removed. |
| `tests/ProoveGaming.App.Tests/SelectedKeyMacroBadgeTests.cs` | Delete — geometry tests for the removed VM. |
| `tests/ProoveGaming.App.Tests/DeviceDetailsKeyboardMacroBindingTests.cs` | Rework: every `SelectedKeyMacroBadge` assertion (assign → shown, reset → hidden, key switch, macro replace, preset restore) becomes the equivalent `GetKeyRemapTooltip(key)` assertion (macro name / null), preserving each scenario rather than deleting it. |
| `docs/plans/add-selected-key-macro-badge/add-selected-key-macro-badge-plan.md` | Dated note: badge superseded by the hover tooltip (this plan); leave the review journal untouched. |
| `docs/plans/add-macro-button-assignment/add-macro-button-assignment-plan.md` | Reconcile live sections that describe the radio list / in-row assign on the Macros page; dated note pointing here. |
| `docs/plans/fix-keyboard-qa-findings/fix-keyboard-qa-findings-plan.md` | Reconcile any live acceptance criteria describing the badge/suppressed tip; dated note pointing here. |
| `docs/plans/add-key-assignment-popup/add-key-assignment-popup-plan.md` | Check for prose describing the Macros-page list as an assignment surface; reconcile with a dated note if so. |
| `docs/plans/add-keyboard-macro-unassign/add-keyboard-macro-unassign-plan.md` | Dated note: the list-row ✕ no longer ships; the dialog / hotspot ✕ and the gate live on. |
| `docs/plans/refactor-lazy-keyboard-overlays/refactor-lazy-keyboard-overlays-plan.md` | Dated correction: the overlay no longer has a badge layer. |
| `src/ProoveGaming.App/App.axaml` | Comment-only: `macroUnassign` call-site notes no longer reference the section rows or the on-key badge. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/LabelWidthEstimator.cs` | Comment-only: the second caller is the hover tip, not the badge. |
| `docs/CONTEXT.md` | Glossary: mark **Selected-key macro badge** as removed and point at the hover tooltip. |

## Acceptance criteria

- [ ] Given a connected keyboard with at least one macro in the library, when the Macros
  section is open, then macro rows render with **no radio circle**, no checked border, and no
  hover tint on the row body; clicking the row body changes nothing (no assignment write, no
  visual state change).
- [ ] Given the same list, when the kebab (3 dots) on a row is clicked, then the Edit/Remove
  flyout opens and both actions work as before.
- [ ] Given a macro bound to key K, when the pointer hovers K on the **Macros** section
  keyboard, then the black tooltip chip above K shows the macro's name; when the pointer
  leaves K, the chip disappears. No chip is visible for any key (selected or not) while
  nothing is hovered.
- [ ] Given the same binding, hovering K on the **Key Assignments** section keyboard shows
  the identical tooltip — the two sections' keyboards render and behave the same (selection
  highlight, hover highlight, hover tooltip, click-opens-dialog).
- [ ] Given the Macros section, when a key is clicked, then the Key Assignment Dialog opens
  (unchanged), and its MACROS tab still shows the radio list, still assigns on click, and its
  ✕ / RESET still unassign.
- [ ] Given a macro bound to the selected key, hovering that key shows the tooltip (the old
  suppression is gone — previously the selected key showed no tip while its badge was up).
- [ ] Given text in the Macros search box, rows still filter by name; "+ NEW MACRO" still
  opens the recorder.
- [ ] `dotnet build ./src/ProveGaming.slnx` and `dotnet test ./src/ProveGaming.slnx` pass;
  no remaining references to `SelectedKeyMacroBadge` anywhere in `src/` or `tests/`.

## Test strategy

This is a **change to existing behaviour** (`remove-*`), and the touched feature already has
tests — they must be reworked first so the scenarios survive, then the removal lands:

- `tests/ProoveGaming.App.Tests/DeviceDetailsKeyboardMacroBindingTests.cs` — the badge test
  region asserts, per family: assign → badge visible with name, reset → hidden, selection
  switch → follows, macro replace → renames, preset restore → reappears. Each becomes the
  same scenario asserted through `vm.GetKeyRemapTooltip(key)` (macro name when bound, `null`
  when not) — the exact value the hover tip renders. Order the work so these rewrites land
  in the same change as the badge removal (the old asserts stop compiling the moment the
  property goes).
- `tests/ProoveGaming.App.Tests/SelectedKeyMacroBadgeTests.cs` — deleted with the VM; its
  geometry concerns (chip position, clamping, ellipsis) have no successor because the hover
  tip's geometry already exists and is view-code.
- `tests/ProoveGaming.App.Tests/DeviceDetailsMacroAssignmentTests.cs` and
  `DeviceDetailsKeyAssignmentDialogTests.cs` — must stay green untouched: they exercise
  `MacroViewModel.IsSelected` sync and `GetKeyRemapTooltip`, both of which this change keeps
  (the dialog still consumes them).

Deliberately **not** covered by automated tests: the XAML row template being
non-interactive, and the tooltip's actual show/hide on pointer enter/leave — both live in
view markup / pointer-event code-behind with no headless harness in this repo; they are
covered by the QA checklist.

## QA (manual test steps)

1. Connect a supported keyboard (e.g. Cyrex or Rampage) and open its Details page →
   **Macros** section. Create two macros ("A", "B") via + NEW MACRO if the list is empty.
   Expected: each macro row shows only its name and a 3-dot menu on the right — no round
   radio circle on the left.
2. Click the body of a macro row (on the name). Expected: nothing happens — no purple
   border, no checkmark, no assignment, no dialog.
3. Click the 3-dot menu on a row. Expected: Edit and Remove menu opens; Edit opens the
   macro editor, Remove asks to delete — both work as before.
4. On the Macros section keyboard, click a key (e.g. Q). Expected: the key-assignment
   dialog opens. Open its MACROS tab and click macro "A". Expected: the dialog closes and
   the macro is assigned (undo toast appears).
5. Move the mouse over key Q (without clicking). Expected: a black tooltip chip appears
   above the key showing "A" (the macro's name). Move the mouse away. Expected: the chip
   disappears. With the mouse not over any key, no chip is visible anywhere on the board.
6. Switch to the **Key assignments** section and hover the same key Q. Expected: the same
   tooltip with "A" appears on hover and disappears on leave — the keyboard looks and acts
   exactly like the one on the Macros section.
7. Press key Q on the physical keyboard. Expected: macro "A" plays (assignment still
   works end-to-end).
8. Re-open the dialog for Q (click the key) and press RESET. Expected: the binding is
   removed; hovering Q now shows only the key's own name ("Q"), no macro name.
9. Type into the macro search box. Expected: the list filters by name as before.
10. If a supported mouse is available, open its Details → Macros. Expected: the macro list
    is display-only there too (no radio circles); assigning a macro to a mouse button is
    done from the Buttons section (Macros group), and the assigned-macro chips on the mouse
    picture behave as before.

## Review focus

1. `null-safety` — every removed member (`SelectedKeyMacroBadge`, `RefreshSelectedKeyMacroBadge`,
   possibly `KeyOverlayBounds`) must have zero remaining references; watch for XAML bindings
   that fail only at runtime.
2. `UI-thread affinity` — the tip handlers stay cheap, per-pointer-event work only; no new
   work added to `PointerEntered`.
3. `performance hot-path` — `NotifySelectedMacroTargetChanged` runs several times per key
   click; confirm the badge refresh (which resolved `ArtworkKeyOverlayLayout`) is fully gone
   from that path, not stubbed.

## Performance & quality

- Removing `RefreshSelectedKeyMacroBadge` deletes per-key-click UI-thread work (it resolved
  the artwork layout table on every selection change) — a small win, no regression risk.
- The hover tip path is untouched except for deleting an early-return; it stays
  allocation-light (string lookups, no layout rebuilds).
- No new shared state, no async changes, no disposables. No hot paths added.

## Risks

- **Mouse Macros page loses in-place assignment.** The macro rows were the only way to bind
  a macro from the Macros page on a mouse (keyboards moved to the dialog earlier). After
  this change a mouse macro is assigned from the Buttons section's Macros group. If that is
  not acceptable, the row template needs a device-type split — see Open questions.
- **Shared `MacroViewModel.IsSelected`.** The dialog's MACROS tab still renders selection
  from the same VM instances; accidentally removing `RefreshMacroListSelection` or the
  `IsSelected` sweeps would silently break the dialog's checked row. Only the *section
  view's* bindings go.
- **Test rework scope.** ~25 badge assertions across the binding-tests file cover distinct
  regressions (per-family assign/reset, preset restore, catalog-slot fallbacks). Rewriting
  them mechanically as tooltip asserts must preserve each scenario's intent — deleting the
  ones that are awkward to port would drop real coverage.
- **Unassign discoverability.** ~~With the row ✕ and the on-key badge ✕ gone, unassigning is
  only in the dialog (RESET / macro-row ✕) and the shared header's RESET.~~ Resolved by the
  2026-08-14 amendment: the hover tooltip carries its own unassign ✕ for macro-bound keys,
  restoring the in-place affordance on both keyboard sections.

## Open questions

1. **Mouse devices:** is it accepted that on the Macros page a mouse macro can no longer be
   assigned by clicking a list row (assignment then lives only in the Buttons section)? The
   request talks about keyboards; the list view is shared by both device types, and the plan
   makes it display-only for both.
2. **Selected-target header:** the "selected key + RESET" header above the list stays (it
   was not part of the request). Confirm it should remain.

## Amendment (2026-08-14) — unassign ✕ in the hover tooltip

User follow-up after Round 1: after assigning a macro, the tooltip naming it must let the
user cancel the binding with a ✕, like the removed badge did — on both the Macros and Key
Assignments keyboards.

**Behaviour.** A macro-bound key's hover tip renders the shared `macroUnassign` ✕ beside the
name — **on the Macros and Key Assignments sections only**. The Actuation page renders the
same overlay (Per-key mode) but owns the `IsSelected` flags through its own single-select,
so both layers gate it: `ShowKeyTip` withholds the ✕ outside Macros/Key Assignments, and
`UnassignMacroFromKey` no-ops under `IsActuationSection && ActuationPerKeyMode` (round 2
B1). Clicking the ✕ unassigns that key's macro (the key becomes the selected key, without
opening the dialog, so the whole existing reset path runs unchanged — family dispatch,
staged-vs-immediate write, list/header refresh, and the PR #81 in-flight-write gate). Tips
for keys without a macro (plain keys and key remaps) are unchanged and stay fully
click-through.

**Interaction mechanics** (`RasterKeyboardOverlayView`): the tip layer is hit-testable only
while the ✕ is shown; a transparent gap filler under the chip bridges the 8 px key-to-chip
gap so the pointer can travel from key to ✕ without leaving the hover region; a 150 ms grace
timer (with an `IsPointerOver` re-check) hides the tip only once the pointer has left both
the key and the tip. Hiding resets the layer to click-through.

**Files (on top of the original change list):**

| File | Intent |
|---|---|
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` | `SelectPhysicalKeyCore` extracted (selection without the dialog); new `KeyHasBoundMacro` + `UnassignMacroFromKey` command reusing `ResetSelectedKeyAsync` behind the in-flight gate. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml` | Tip restructured: chip (chrome + texts + ✕) over a transparent gap filler; layer hit-testability documented. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml.cs` | ✕ sizing/enabling per show, grace-timer show/hide, `HideTipNow` everywhere the tip is torn down, click handler dispatching the VM command. |
| `tests/ProoveGaming.App.Tests/DeviceDetailsKeyboardMacroBindingTests.cs` | Four new tests: unassign on staged + immediate families, hovered-key-not-selected, no-op for unbound key. |

**Added acceptance criteria:**

- [ ] Given a macro bound to key K, when K is hovered, then the tooltip shows the macro's
  name and an ✕; clicking the ✕ removes the binding (K reverts to its own keycode), the tip
  hides, and re-hovering K shows only the key's own name — identically on the Macros and Key
  Assignments sections.
- [ ] Given the tooltip with ✕ is showing, when the pointer moves from the keycap up into
  the tooltip, then the tooltip stays visible long enough to click the ✕ (it does not vanish
  on leaving the keycap).
- [ ] Given a key without a macro (unbound or plain remap), its tooltip carries no ✕ and the
  tip remains click-through (keys under it stay hoverable), as before.
- [ ] Clicking the ✕ never opens the Key Assignment Dialog.

**Added QA steps:**

11. Assign macro "A" to key Q (dialog MACROS tab). Hover Q: the tooltip shows "A" with a
    small ✕ beside it. Move the mouse from the keycap up into the tooltip — it must not
    disappear — and click the ✕. Expected: the tooltip closes, hovering Q now shows only
    "Q", and pressing the physical key types Q again.
12. Repeat step 11 from the **Key assignments** section. Expected: identical behaviour.
13. Hover a key with no macro (e.g. a plain or remapped key). Expected: the tooltip has no ✕.
14. On a keyboard with actuation support, open Actuation → Per-key mode and hover the key
    from step 11 while it has a macro bound. Expected: the tooltip appears but has **no ✕**
    (unassigning is not offered on the Actuation page).

**Known trade-offs:** while an ✕-tip is visible it shields the keys directly beneath it from
hover (they come back the moment the tip hides); the ✕'s enabled state is evaluated when the
tip appears, so during the ~0.5 s in-flight assignment window it shows disabled until the
next hover (the command re-checks the gate on click either way).

**Addendum (2026-08-14): unassign toast.** A successful ✕ unassign raises the shared undo
toast — "*macro* unassigned from *key*" (`Macros_UnassignToast_Format`, en/uk) with UNDO
re-binding the macro through the standard assignment path (the undo target is the macro's
own `AssignmentTargets` row, so it works on both write families). The toast is raised only
after `BoundMacroForKey` confirms the binding is gone (`ResetSelectedKeyAsync` has silent
early-outs), and it hides when an inspection radio clears the key selection — its UNDO acts
on the selected key and must not outlive it. Covered by
`UnassignMacroFromKey_RaisesTheUndoToast`, `…_RaisesNoToast_WhenNothingWasUnassigned` and
`InspectMacro_HidesTheUnassignToast`.

**Added QA step:**

15. Hover a macro-bound key and click the tooltip's ✕. Expected: a notification appears at
    the usual toast spot — "*macro* unassigned from *key*" with UNDO — and disappears after
    ~5 s. Clicking UNDO re-binds the macro (hovering the key names it again).

## Review findings

### Round 1 — Ship: yes

#### Summary
The working tree (all uncommitted, on `main`) makes the Macros section's macro list
display-only (plain `Border` rows: name + kebab, no radio/checked/assign/✕), deletes the
selected-key macro badge feature end-to-end (`SelectedKeyMacroBadgeViewModel`, the overlay's
`BadgeLayer`, `RefreshSelectedKeyMacroBadge`, both `KeyOverlayBounds` helpers), removes the
hover-tip suppression so the bound-macro tooltip now appears on hover for every key on both
the Macros and Key Assignments keyboards, reworks the nine badge wiring tests into
`GetKeyRemapTooltip` assertions, and reconciles six older plans plus `docs/CONTEXT.md`.

#### Plan conformance
Plan: `docs/plans/remove-macro-list-selection/remove-macro-list-selection-plan.md`
- Implemented as planned: every File-level change list item is closed, including the
  conditional one (`KeyOverlayBounds` had no callers left after the test rework and was
  removed). Repo-wide grep for `SelectedKeyMacroBadge|KeyOverlayBounds|RefreshSelectedKeyMacroBadge`
  over `*.cs`/`*.axaml` returns nothing — the "no remaining references" acceptance criterion holds.
- Deviates from plan: none of substance. Comment-only touches in `App.axaml`,
  `LabelWidthEstimator.cs` and `DeviceDetailsViewModel.KeyAssignments.cs:340` (stale
  badge/list references) plus dated notes in two extra plans
  (`add-keyboard-macro-unassign`, `refactor-lazy-keyboard-overlays`) — all were added to the
  change list in the same change, so plan and code agree.
- Missing / partial: the view-level acceptance criteria (row non-interactivity, tooltip
  show/hide on pointer enter/leave) are covered by manual QA only, exactly as the Test
  strategy declares — no headless pointer harness exists in this repo.
- Accepted limitation (per plan, open question 1): a mouse can no longer get a macro
  assigned from the Macros page list; the Buttons section's Macros group is the only path.
  Residual risk: a mouse user following the old flow finds clicking a macro row does
  nothing, with no hint where assignment moved. Needs the user's explicit confirmation.
- Accepted limitation (per plan, out of scope): mouse hotspot macro chips on the Macros
  page remain always-visible, so mice and keyboards now indicate bound macros differently
  (static chip vs hover tip). Consistent with the request's keyboard focus, but worth a
  designer's glance.
- Test strategy honored: tests were reworked scenario-for-scenario, not deleted — assign
  (both write families), unassign (staged + immediate), key switch, rebind, device change,
  T800/Rampage slot resolution all survive as tooltip assertions, and the old
  section-gating test was deliberately inverted (`HoverTooltip_NamesTheBoundMacro_OnEverySection`)
  to pin the new cross-section requirement.

#### Code
- none (no Blockers, no Should-fix). Checked specifically: the dialog's MACROS tab still
  binds `MacroViewModel.IsSelected`/`AssignCommand` and `RefreshMacroListSelection` is
  untouched, so removing the section view's bindings cannot break the dialog's checked row;
  `using ProoveGaming.Common.Classes` in `DeviceDetailsViewModel.KeyAssignments.cs` is still
  needed (`RasterKeyBounds` at lines 491–502); `NotifySelectedMacroTargetChanged` still
  raises all four header properties, and deleting the badge refresh removes per-click
  `ArtworkKeyOverlayLayout` rebuilds from the UI thread (a small win, no regression); the
  overlay's `RebuildKeyboard` no longer touches a badge layer and `TipPanel` reset on
  rebuild is intact; XAML is well-formed and compiled-binding-checked (build clean).

#### Tests
- [ ] none required beyond what shipped — `ProoveGaming.App.Tests` 765/765 pass locally.
  Note (pre-existing, not this change): CI builds only the App csproj, so these tests run
  locally only.

#### Nits
- `x:Name="Root"` in `src/ProoveGaming.App/Views/DeviceDetails/Sections/MacrosSectionView.axaml:10`
  is now unreferenced (the row ✕'s `#Root.DataContext` bindings were its last consumers).
  Harmless; drop it next time the file is touched.
- The old `macroItemRow` rows carried a 2 px transparent border inside their 56 px height;
  the new `Border.macroDisplayRow` has none, so the row's content box is ~2 px taller /
  wider than before. Invisible in practice (content is centred), noted only so a
  pixel-diff against old screenshots isn't mistaken for a bug.

#### What's done well
- The removal is complete rather than disabled-in-place: −937/+161 lines, no dead
  properties, no orphaned styles, and every stale comment that named the badge was updated
  in the same pass.
- The test rework preserves each regression's intent (including the PR #81 immediate-write
  reset case and the T800/Rampage slot-resolution cases) instead of deleting awkward tests,
  and adds a test that pins the new cross-section tooltip behavior.
- The documentation trail is exemplary: six affected plans got dated supersession notes
  (review journals untouched), and the glossary entry was corrected rather than deleted.

#### Verdict
Ship: yes — no blockers, no should-fixes; build clean, 765/765 tests green, plan and code
agree. First: (1) get the user's confirmation on open question 1 (mouse Macros-page
assignment now lives in the Buttons section only) and open question 2 (keeping the
selected-key header), (2) run the plan's manual QA checklist on a real keyboard (hover
show/hide and row non-interactivity are view-level and untestable headlessly), (3) commit —
everything is currently uncommitted on `main`, so branch it before pushing.

### Round 2 — Ship: no

Scope of this round: the 2026-08-14 Amendment (unassign ✕ in the hover tooltip) — new VM
command `UnassignMacroFromKey` + `KeyHasBoundMacro` + `SelectPhysicalKeyCore` extraction,
the tip's ✕/gap-filler/grace-timer interaction in `RasterKeyboardOverlayView`, four new VM
tests, and the doc reconciliation. Build clean; 769/769 App tests pass.

#### Plan conformance
Plan: this plan, Amendment section.
- Implemented as planned: every Amendment file-list item is present — ✕ beside the name for
  macro-bound keys, hit-testable-only-with-✕ layer, transparent gap filler, 150 ms grace
  timer with `IsPointerOver` re-check, VM command reusing `ResetSelectedKeyAsync` behind the
  in-flight gate, four new tests covering both write families, hovered≠selected, and the
  unbound no-op.
- Deviates from plan: the Amendment says tips exist "on both keyboard sections", but the
  overlay also renders on the **Actuation** page (Per-key mode) with the same hover-tip
  wiring — the Amendment never considered that third instance, and the code inherited the
  gap (see B1).
- Accepted limitation (per Amendment, stands): ✕-tip shields the keys directly beneath it
  while visible; ✕ enabled-state is computed at show time, not live.

#### Code
- [x] **B1. The tip's ✕ is offered on the Actuation page and corrupts per-key actuation
  selection** — `src/ProoveGaming.App/Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml.cs:362`
  (`ShowKeyTip`, `showUnassign`) and
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs:1404`
  (`UnassignMacroFromKey`) — Blocker
  **Problem:** hover tips are wired on every non-picker overlay instance, including the
  Actuation section's Per-key overlay. `showUnassign` is gated only on
  `KeyHasBoundMacro(key)`, so a macro-bound key shows the ✕ there too — and
  `UnassignMacroFromKey` calls `SelectPhysicalKeyCore` unconditionally, bypassing exactly
  the `IsActuationSection && ActuationPerKeyMode` branch that `SelectPhysicalKey` (line
  1356) exists to protect. Actuation's own selection is a separate single-select
  (`_actuationSelectedKey` in `DeviceDetailsViewModel.Actuation.cs:203`) that shares the
  same `IsSelected` flags.
  **Why it blocks:** (1) Macros page: bind macro M1 to key W. (2) Actuation → Per-key mode →
  click key A: sliders edit A, `_actuationSelectedKey = A`, A highlighted. (3) Hover W: tip
  shows "M1" with an ✕; click it. (4) `SelectPhysicalKeyCore` sweeps `IsSelected` (A
  cleared, W set) and sets `SelectedPhysicalKey = W`, while `_actuationSelectedKey` still
  points at A. The board now highlights W, `ActuationSelectedKeysLabel` still names A, and
  moving a slider writes actuation for A — a key that no longer looks selected. Wrong-key
  hardware writes from a UI click, plus the macro was unassigned from a page that has no
  business doing so.
  **Solution options:**
  - Option A (correct): gate both layers. View: in `ShowKeyTip`, offer the ✕ only where key
    selection semantics belong to Key Assignments/Macros —
    `var showUnassign = _boundVm.KeyHasBoundMacro(key) && (_boundVm.IsMacrosSection || _boundVm.IsButtonsSection);`
    (both properties are public, `DeviceDetailsViewModel.cs:810/812`). VM: mirror
    `SelectPhysicalKey`'s guard at the top of `UnassignMacroFromKey`
    (`if (IsActuationSection && ActuationPerKeyMode) return;`) so a stale/direct invocation
    can never cross the selection models. Add a VM test pinning the guard.
  - Option B (quick): VM guard only. Safe, but the Actuation tip then shows an ✕ that
    silently does nothing — a dead control, worse UX than no ✕.

#### Tests
- [x] After fixing B1, add a guard test: with Actuation section active and Per-key mode on,
  `UnassignMacroFromKeyCommand.Execute(boundKey)` leaves the binding, the actuation
  selection and `SelectedPhysicalKey` untouched — `tests/ProoveGaming.App.Tests/DeviceDetailsKeyboardMacroBindingTests.cs`
  (or `DeviceDetailsActuationViewModelTests.cs`, whichever already builds the actuation
  fixture more cheaply). *(Closed: `UnassignMacroFromKey_IsANoOp_InActuationPerKeyMode`.)*

#### Nits
- none

#### What's done well
- The ✕ rides the existing reset path end-to-end (family dispatch, staged-vs-immediate,
  in-flight gate) instead of growing a parallel unassign write — one truth for unassign.
- The hit-testability flip + gap filler + grace timer with `IsPointerOver` re-check is a
  careful solution to the "tooltip you can click" problem, and plain tips keep their exact
  old click-through behaviour.
- The Amendment documents the trade-offs (shielded keys under the tip, show-time enabled
  state) honestly, and the four new tests cover both write families and the
  hovered-is-not-selected case.

#### Verdict
Ship: no — B1 lets a Macros-page affordance leak onto the Actuation page and desync its
selection model, producing actuation writes to the wrong key. First: (1) fix B1 (Option A:
section gate in `ShowKeyTip` + guard in `UnassignMacroFromKey`), (2) add the actuation
guard test, (3) re-run the full App suite and update the Amendment's behaviour paragraph to
name the section gate.
