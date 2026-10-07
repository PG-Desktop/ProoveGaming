# Add key assignment popup (per-key assignment dialog)

> **2026-08-14 — Note:** two behaviours this plan references changed in
> [`remove-macro-list-selection`](../remove-macro-list-selection/remove-macro-list-selection-plan.md):
> the selected-key macro badge (and the overlay's badge layer / its picker suppression) was
> removed — the bound macro is named by the hover tooltip only — and the Macros **section**'s
> macro list became display-only (no radios, no assign-on-click; open question 5 below is thereby
> resolved). The dialog's own MACROS tab keeps the radio list this plan specifies.

## Goal

Replace the inline keyboard key-assignment / macro-assignment workflow with a modal
popup: clicking a key on the interactive on-screen keyboard opens a per-key dialog
(titled with the key name, e.g. "ESC button") with tabs **KEYS / SYSTEM / MACROS**,
a RESET action, and a close ✕ — per the supplied design mockups (decoded in
[design-notes.md](design-notes.md)). Today the same actions are spread across two
left-panel sections (`KEY ASSIGNMENTS` flat target list, `MACROS` radio list); the
popup consolidates them into one surface per key, matching the new design language
(chamfered black chrome, `#6357FF` accents).

## Scope

**In:**
- New modal dialog for keyboards with an interactive overlay (Raster, T-Racer,
  Smart Infinite incl. Rampage/Cyrex/Aress, T800 — i.e. every family that supports
  key assignment; verified via `HasInteractiveKeyboardOverlay` +
  `IsSmartInfiniteDevice` in `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs:175,588`).
- Dialog opens when a key is clicked in the **Key Assignments** (`NavSection.Buttons`)
  or **Macros** section. Actuation per-key selection keeps its existing fork and never
  opens the dialog.
- **KEYS tab**: visual remap-target picker — the same keyboard rendering, where
  clicking a key remaps the selected physical key to it (routes through the existing
  `SelectAssignmentTarget` staging/apply logic, unchanged).
- **MACROS tab**: search field + macro rows (reusing `MacroViewModel.AssignCommand`,
  `IsSelected`, unassign ✕) assigning the macro to the selected key via the existing
  `AssignMacroToSelectedKeyAsync` path.
- **SYSTEM tab**: tab button and static content present in markup but gated off by a
  `false` capability flag — same precedent as the hidden COMMANDS strip in
  `src/ProoveGaming.App/Views/DeviceDetails/Sections/MacrosSectionView.axaml:233-249`.
  Commands are not device-assignable today (targets only carry HID keyboard
  `TypeByte 0x10` / family macro types; no consumer-control type exists).
- **RESET** in the dialog header = existing `ResetSelectedMacroTargetCommand`
  (keyboards → `ResetSelectedKeyAsync`).
- Remove the inline `AssignmentTargets` list from
  `KeyAssignmentsSectionView.axaml`; the left panel keeps section title, selected key
  name and a (newly localized) hint.
- Localize the two known hardcoded strings this touches: "Select a key on the
  keyboard" and the `" button"` suffix of `SelectedPhysicalKeyWithSuffix`.

**Out:**
- Making SYSTEM commands actually assignable (needs per-family consumer-control
  protocol work; follow-up plan).
- Mouse assignment flows (`ButtonsSectionView`, hotspots) — untouched.
- Actuation UI — untouched.
- Macro CRUD (create/edit/record dialogs) — untouched; the Macros nav section keeps
  its library management role and its keyboard overlay (which now opens the dialog).
- Any DeviceHost / gRPC / firmware change. No new NuGet packages, no `.slnx` change.

## Options considered

**A. Modal hosted in MainWindow `DialogOverlay` (existing Pattern A).**
The app's only real modal infrastructure: `MainWindow.axaml:516` overlay +
`RefreshDialogOverlay()` (`MainWindow.axaml.cs:229`) with background blur, driven by
`Is…Open` flags on the singleton `DeviceDetailsViewModel`.
*Pros:* consistent with every existing device dialog (Create Macro, presets),
gets dim + blur for free, one wiring pattern for the reviewer to check.
*Cons:* the wiring is spread across four places (whitelist, accessor, refresh line,
overlay child) and is easy to miss one of.

**B. Self-contained window-sized `Popup` inside `DeviceDetailsView` (Pattern C,
as `ProfileView.axaml:430`).**
*Pros:* no MainWindow coupling. *Cons:* no background blur (design implies the
standard dialog treatment), diverges from how all other DeviceDetails dialogs work,
Popup layering interacts poorly with the existing overlay dialogs.

**C. Keep the inline panels and add the popup only for macro assignment.**
*Pros:* smallest diff. *Cons:* two competing assignment flows, contradicts the
request ("the workflow must change"), leaves the dead inline list to maintain.

For the KEYS tab presentation: **visual keyboard picker** (matches mockup 1) vs. a
grouped text list of `AssignmentTargets`. The picker is chosen; it reuses the
existing overlay control and catalog canvas geometry, so cost is low. The flat
`AssignmentTargets` collection stays as the routing/apply backbone either way.

## Recommended approach

**Option A** + visual KEYS picker. All new state lives on the singleton
`DeviceDetailsViewModel` (a transient VM would break the `RefreshDialogOverlay`
wiring — see DI notes below).

1. **ViewModel** — new partial `DeviceDetailsViewModel.KeyDialog.cs`:
   - `IsKeyAssignmentDialogOpen` (bool, `OnPropertyChanged`), `CloseKeyAssignmentDialogCommand`.
   - Tab state: `IsKeyDialogKeysTabActive` / `IsKeyDialogMacrosTabActive` (+ derived
     `IsKeyDialogSystemTabActive`) with `SelectKeyDialog*TabCommand`s; KEYS is the
     default on every open. `IsSystemCommandsSupported => false` gates the SYSTEM tab.
   - `KeyDialogTitle` — localized format string `"{0} button"` applied to the selected
     key label; `SelectedPhysicalKeyWithSuffix` migrates to the same resource.
   - `RemapTargetKeys` (`ObservableCollection<KeyAssignmentOptionViewModel>`) — built
     once per device inside `LoadKeyLayoutForDevice` alongside `PhysicalKeys`, same
     canvas geometry, but `SelectCommand = PickRemapTargetCommand`. That command
     resolves the matching `AssignmentTargets` row by `DevKeyName` and invokes the
     existing `SelectAssignmentTarget` — staging vs. immediate-apply per family stays
     exactly as is. A `SyncRemapTargetPickerSelection()` (called from
     `SyncAssignmentTargetSelection`) mirrors the currently bound target onto the
     picker (`IsSelected` drives the filled-key visual). Separate collection ⇒ no
     `IsSelected` collisions with the physical-key overlay.
   - `SelectPhysicalKey` (`…KeyAssignments.cs:1258`): after the existing actuation
     fork and selection/sync logic, set `IsKeyAssignmentDialogOpen = true` when
     `HasInteractiveKeyboardOverlay && (IsButtonsSection || IsMacrosSection)` and the
     device is a keyboard.
2. **Dialog view** — new `Views/DeviceDetails/Dialogs/KeyAssignmentDialog.axaml(.cs)`
   following the `ConfirmDialog` conventions (chamfered chrome, `DropShadowEffect`
   `#6357FF` glow) with: header (title + RESET), close ✕ on the right rail, tab
   strip, KEYS tab hosting a `RasterKeyboardOverlayView` bound to `RemapTargetKeys`,
   MACROS tab hosting search + macro rows (reusing the existing macro row styles and
   the existing macro search/filter property), SYSTEM tab content gated off.
3. **Overlay control** — `RasterKeyboardOverlayView.axaml.cs` gains a
   `KeysSource`-style property (defaults to `PhysicalKeys`) so the dialog instance
   renders `RemapTargetKeys`; the badge layer is suppressed for the picker instance.
4. **MainWindow wiring** — add the dialog as a `DialogOverlay` child bound to the
   DeviceDetails VM; add `IsKeyAssignmentDialogOpen` to the property whitelist
   (`MainWindow.axaml.cs:198`), a `GetKeyAssignmentDialogPanel()` accessor
   (`MainWindow.Controls.cs`), and a line in `RefreshDialogOverlay()`.
5. **Inline panel cleanup** — `KeyAssignmentsSectionView.axaml` drops the
   `AssignmentTargets` `ItemsControl`; keeps title + localized hint.
   `MacrosSectionView.axaml` is left functionally unchanged (its rows keep working
   because dialog and section share the same singleton state); its `macroTab*` styles
   move to `App.axaml` so the dialog's tab strip reuses them instead of copying
   (CLAUDE.md second-copy rule).
6. **Localization** — new keys in `Strings.en.axaml` / `Strings.uk.axaml`:
   `KeyDialog_Title_Format`, `KeyDialog_Tab_Keys`, `KeyDialog_Tab_System`,
   `KeyDialog_Tab_Macros`, `KeyDialog_Hint_Keys`, `KeyDialog_Hint_System`,
   `KeyDialog_Hint_Macros`, `KeyAssignments_SelectKeyHint`; reuse
   `Macros_SearchPlaceholder`, `DeviceDetails_Buttons_Reset`, `Macros_Unassign`.
7. **Glossary** — "Key Assignment Dialog" added to `docs/CONTEXT.md` (done with this
   plan).

## File-level change list

| File | Action | Intent |
|---|---|---|
| `tests/ProoveGaming.App.Tests/DeviceDetailsKeyAssignmentDialogTests.cs` | create | New coverage for dialog open/close, tab defaults, picker routing, picker selection sync (see Test strategy; lands with the VM change). |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyDialog.cs` | create | Dialog state (`IsKeyAssignmentDialogOpen`, tabs, title), `RemapTargetKeys` picker collection, `PickRemapTargetCommand`, close command, `IsSystemCommandsSupported`. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` | edit | Open the dialog from `SelectPhysicalKey` (after the actuation fork); build `RemapTargetKeys` in `LoadKeyLayoutForDevice`; call `SyncRemapTargetPickerSelection()` from `SyncAssignmentTargetSelection`; localize `SelectedPhysicalKeyWithSuffix` suffix. |
| `src/ProoveGaming.App/Views/DeviceDetails/Dialogs/KeyAssignmentDialog.axaml` | create | Dialog markup per design-notes: chrome, header (title/RESET), ✕, tab strip, KEYS picker, MACROS search+list, gated SYSTEM content. |
| `src/ProoveGaming.App/Views/DeviceDetails/Dialogs/KeyAssignmentDialog.axaml.cs` | create | Code-behind (ConfirmDialog-style; minimal — bindings do the work). |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml.cs` | edit | `KeysSource` property (default `PhysicalKeys`) + badge-layer suppression flag so the dialog can host a target-picker instance. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/KeyAssignmentsSectionView.axaml` | edit | Remove inline `AssignmentTargets` list; localized "select a key" hint. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/MacrosSectionView.axaml` | edit | Move `macroTab`/`macroTabFill`/`macroTabLabel` styles out to `App.axaml`; no behavioral change. |
| `src/ProoveGaming.App/App.axaml` | edit | Receive the shared tab styles; add dialog-specific shared styles (search field row, list row) named by role. |
| `src/ProoveGaming.App/Views/MainWindow.axaml` | edit | Add `KeyAssignmentDialog` child to `DialogOverlay` with the DeviceDetails DataContext binding. |
| `src/ProoveGaming.App/Views/MainWindow.axaml.cs` | edit | Whitelist `IsKeyAssignmentDialogOpen` in `OnDeviceDetailsViewModelPropertyChanged`; drive visibility in `RefreshDialogOverlay()`. |
| `src/ProoveGaming.App/Views/MainWindow.Controls.cs` | edit | `GetKeyAssignmentDialogPanel()` accessor. |
| `src/ProoveGaming.App/Resources/Strings.en.axaml` | edit | New `KeyDialog_*` + `KeyAssignments_SelectKeyHint` keys. |
| `src/ProoveGaming.App/Resources/Strings.uk.axaml` | edit | Ukrainian translations of the same keys. |
| `docs/CONTEXT.md` | edit | Glossary entry "Key Assignment Dialog" (already added with this plan). |

## Acceptance criteria

- [ ] Given a Raster/T-Racer/Smart Infinite/T800 keyboard on the **Key Assignments**
  section → clicking a key on the on-screen keyboard → the modal dialog opens,
  background blurs, title shows "<KEY> button", **KEYS** tab is active.
- [ ] Given the **Macros** section on the same devices → clicking a key → the same
  dialog opens.
- [ ] Given the **Actuation** section with Per-key mode on → clicking a key →
  the dialog does **not** open; the key toggles actuation selection as before.
- [ ] Given a mouse device → no interaction opens the dialog.
- [ ] Given the dialog's KEYS tab → clicking a target key → the remap is
  staged/applied through the same path as before (Raster/T-Racer: SQLite
  `key_bindings` row + debounced apply; Smart Infinite/T800: immediate device
  write), and the clicked target renders as the filled/selected key.
- [ ] Given the dialog's MACROS tab → typing in search filters the macro list;
  clicking a macro row binds it to the selected key (`key_macro_bindings` row
  written / device write for immediate-apply families); the bound row shows selected
  with an unassign ✕.
- [ ] Given a key with a macro bound → dialog RESET → binding removed, macro badge
  and picker selection clear, dialog stays open.
- [ ] Given the dialog open → clicking ✕ → dialog closes, blur removed, the
  physical key remains selected in the section underneath.
- [ ] The SYSTEM tab button and content are not visible while
  `IsSystemCommandsSupported` is false.
- [ ] The Key Assignments left panel no longer shows the flat target list; it shows
  the localized hint, in both `en` and `uk`.
- [ ] The dialog title suffix ("button") and all new dialog strings come from
  resources and switch with the app language.
- [ ] All pre-existing tests in `tests/ProoveGaming.App.Tests` pass unchanged
  (macro assignment, keyboard macro binding, badge, Cyrex/Rampage assignment,
  actuation, nav visibility).

## Test strategy

Kind of change: **behavior change to an already-tested feature + new UI feature.**
The touched core (`SelectPhysicalKey`, `SelectAssignmentTarget`,
`AssignMacroToSelectedKeyAsync`, reset) already has characterization coverage —
`DeviceDetailsMacroAssignmentTests`, `DeviceDetailsKeyboardMacroBindingTests`,
`SelectedKeyMacroBadgeTests`, `DeviceDetailsCyrexKeyAssignmentTests`,
`DeviceDetailsRampageKeyAssignmentTests`, `DeviceDetailsActuationViewModelTests` —
these must stay green (they exercise the VM directly, not the removed XAML list, so
no rewrites are expected).

New, non-negotiable tests in
`tests/ProoveGaming.App.Tests/DeviceDetailsKeyAssignmentDialogTests.cs`
(xUnit, mirroring the existing VM-level test style and `InternalsVisibleTo` usage):

1. Key click in Key Assignments section (keyboard) → `IsKeyAssignmentDialogOpen`
   true and KEYS tab active (also when reopened after a tab switch).
2. Key click in Macros section → dialog opens.
3. Key click with `IsActuationSection && ActuationPerKeyMode` → dialog stays closed.
4. `RemapTargetKeys` is populated per device family with the same count/geometry
   source as `PhysicalKeys` and rebuilt on device layout change.
5. `PickRemapTargetCommand` on a picker key routes to the matching
   `AssignmentTargets` row: staging families produce the same `key_bindings`
   persistence as a direct `SelectAssignmentTarget` call (assert via
   `LocalStorageService` as existing tests do).
6. After assignment/reset, picker `IsSelected` mirrors the bound target
   (`SyncRemapTargetPickerSelection`).
7. `CloseKeyAssignmentDialogCommand` clears the flag and leaves
   `SelectedPhysicalKey` intact.
8. Reset with dialog open clears macro binding and picker selection, keeps the
   dialog open.
9. `KeyDialogTitle` uses the localized format resource.

Deliberately **not** covered by automated tests: MainWindow overlay wiring
(visual-tree + blur — needs a running Avalonia UI thread), dialog chrome/visuals,
artwork rendering of the picker overlay, real HID writes. These are covered by QA.

## QA (manual test steps)

1. Launch the app and connect a supported keyboard (e.g. T-Racer or Cyrex). Open the
   device page → **KEY ASSIGNMENTS**. Expected: the left panel shows only a hint
   ("Select a key on the keyboard…"), no long list of keys.
2. Click any key on the pictured keyboard. Expected: a dark popup opens over a
   blurred background; its title is the key's name plus "button" (e.g. "ESC button");
   tabs KEYS / MACROS are visible (no SYSTEM tab); KEYS is active.
3. On the KEYS tab, click a different key on the pictured keyboard inside the popup
   (e.g. remap Q → W). Expected: the clicked key becomes highlighted/filled. Type on
   the physical keyboard: pressing Q now types W.
4. Switch to the MACROS tab. Expected: a search box and the list of your macros.
   Type part of a macro name — the list filters.
5. Click a macro row. Expected: the row becomes selected with an ✕; pressing the
   physical key plays the macro.
6. Click **RESET** in the popup header. Expected: the macro selection clears, the
   key returns to its default behavior, and the popup stays open.
7. Close the popup with ✕. Expected: popup closes, the background is sharp again,
   and the same key is still shown as selected on the page.
8. Go to the **MACROS** section and click a key on the pictured keyboard there.
   Expected: the same popup opens.
9. Go to **ACTUATION**, enable Per-key mode, click a key. Expected: no popup — the
   key just gets the actuation selection as before.
10. Connect a supported mouse (e.g. X43). Expected: buttons/macros for the mouse
    work exactly as before; no popup anywhere.
11. Switch app language to Ukrainian. Reopen the popup. Expected: title suffix,
    tab labels, hints and search placeholder are in Ukrainian.
12. Restart the app. Expected: the remap from step 3 and any macro binding from
    step 5 (if not reset) survive and are re-applied.

## Review focus

1. `UI-thread affinity` — dialog open/close and overlay refresh run on the
   dispatcher; no device I/O on the UI thread from the new commands.
2. `DI lifetimes` — all new state on the singleton `DeviceDetailsViewModel`;
   MainWindow whitelist/accessor/refresh wiring complete (all four touchpoints).
3. `localization` — no new hardcoded user-facing strings; both `en` and `uk` keys
   present; previously hardcoded hint + " button" suffix migrated.
4. `async/await` — `PickRemapTargetCommand` reuses the existing async apply paths
   without introducing sync-over-async.

## Performance & quality

- No per-frame hot path is touched. The costly operation is the imperative
  `RebuildKeyboard()` pass of the picker overlay: `RemapTargetKeys` must be built
  **once per device layout load** (inside `LoadKeyLayoutForDevice`), never per
  dialog open, so opening the dialog only flips a bool and toggles visibility.
- The dialog lives permanently (hidden) in `DialogOverlay`, matching the other
  dialogs — no allocation on open.
- Selection sync iterates two ~100-element collections on the UI thread; trivial.
- All new state is UI-thread-only on the singleton VM; no new locking needed. The
  existing staged/immediate apply paths (with their own async discipline) are
  reused, not reimplemented.

## Risks

- **MainWindow wiring fragility** — four separate touchpoints; missing one makes the
  dialog silently never show or never blur. Mitigated by acceptance criteria + QA 2/7.
- **Selection-state crosstalk** — the picker and the physical-key overlay both use
  `KeyAssignmentOptionViewModel.IsSelected`; separate collections prevent collisions,
  but `LoadKeyLayoutForDevice` must clear/rebuild both coherently (it already clears
  actuation selection for the same reason, `…KeyAssignments.cs:656`).
- **Immediate-apply families (Smart Infinite/T800)** — a mis-click in the picker
  writes to the device instantly. Same behavior as the current inline list, but the
  visual picker makes mis-clicks easier; RESET is the recovery path.
- **Design copy uncertainty** — mockup text is outlined; decoded wording (SYSTEM,
  "Quick Unique Action…") may not match final Figma copy.
- **Removing the inline list** breaks any muscle-memory workflow relying on it; the
  Macros section rows still work as a fallback assignment surface.

## Open questions

1. Confirm final copy from Figma: tab labels (decoded **SYSTEM** vs. "SELECT" in the
   request paraphrase), the per-tab hint sentences, and the row content of SYSTEM.
2. When opening a key that already has a macro bound — open on KEYS (plan default)
   or jump to MACROS with the bound row visible?
3. Should picking a target auto-close the dialog? Plan assumes **stays open**
   (mockup 2 shows a selected row with the dialog open).
4. SYSTEM tab while unsupported: fully hidden (plan default) or visible-disabled?
5. Should the Macros section keep its inline assign radios for keyboards, or become
   library-CRUD-only once the dialog exists? Plan keeps them (zero-cost, shared state).
6. Scope check: is the T800's dialog expected too? Plan includes it (it has the
   interactive overlay); exclude if the design targets only the photographed board.

## Addendum — type-to-remap (requested after round 1)

While the dialog's **KEYS tab** is open, pressing a key on the **physical keyboard** picks
the remap target, equivalent to clicking it on the on-screen picker.

- Capture: MainWindow tunneled `KeyDown`/`KeyUp` handlers (same pattern and auto-repeat
  suppression as the macro recorder), gated on
  `IsKeyAssignmentDialogOpen && IsKeyDialogKeysTabActive` and off while recording.
- A press whose keydown the handler saw stays owned until its release: its auto-repeats
  and its keyup are swallowed even after the pick has closed the dialog (2026-08-17).
  Without this the picked key's repeats bubbled to the still-focused key tile Button and
  Space/Enter "clicked" it, reopening the dialog right after the remap — easiest to hit
  on the 2.4G transports, whose slow chunked write leaves the dialog closed while the
  key is still held. The tracked set clears on window `Deactivated` (PR #107 review m2):
  a key still held through a focus loss never delivers its KeyUp, and the stale entry
  would eat that key's next make anywhere in the app.
- Mapping: `HidKeyCodeMap` (Avalonia `Key` → HID usage) →
  `DeviceDetailsViewModel.TryPickRemapTargetFromKeyboard(byte)`, which resolves the
  `AssignmentTargets` row by usage, with a bitmask fallback for modifier usages
  (0xE0–0xE7) on the Smart Infinite boards, and routes through `SelectAssignmentTarget`.
- **Remapped-key echo (QA 2026-08-20):** the remap lives in the keyboard's firmware, so an
  overridden key emits its TARGET — with "1" remapped to F1, pressing physical "1" arrives
  as F1, indistinguishable from the real F1 key. Re-picking the code the selected key
  already emits is a no-op either way, so when the resolved target matches the selected
  key's current DEVICE record (`SelectedKeyCurrentlyEmits` — the live device maps, not the
  staged ones), the pick is redirected to the key's own record. That makes "press the key
  to put it back" work; the trade-off is that pressing the real F1 key to re-confirm an
  existing 1→F1 remap now resets instead — a no-op made into an undo-able restore. A
  self-pick's toast uses the reset wording (`KeyDialog_ResetToast_Format`, en/uk) rather
  than "remapped to itself" (PR #117 m2).
- The KEYS-tab gate keeps typing in the MACROS search box from remapping anything.
  **Escape is a pick, not a dismiss** — every key must stay bindable; ✕ closes the dialog.
- `KeyDialog_Hint_Keys` copy updated (en/uk) to advertise the capability.

Acceptance criteria (additional):
- [ ] Given the dialog's KEYS tab on a supported keyboard → pressing e.g. F1 on the
  physical keyboard → the selected key is remapped to F1 through the same staging/apply
  path, and F1 renders as the filled picker key.
- [ ] Given the MACROS tab → typing in the search box filters macros and remaps nothing.
- [ ] Holding a key produces exactly one pick (no auto-repeat writes), and the dialog
  does not reopen when the key is released — even when the write is still in flight
  (2.4G) and repeats fire after the dialog closed.
- [ ] Keys with no catalog row (e.g. Num Lock on a TKL board) do nothing.
- [ ] Given a key already remapped on the device (e.g. 1 → F1) with its dialog open →
  pressing that physical key (which now emits F1) → the key is restored to its own code,
  not re-remapped to F1. A staged-but-unapplied override does not trigger the redirect —
  the board still emits the old code there, so an incoming F1 is the real F1 key.

Tests: `TypedKey_PicksTheMatchingTarget_AndStagesIt`,
`TypedModifier_FallsBackToTheBitmaskRow_OnSmartInfinite`,
`TypedKey_IsIgnored_WhenDialogClosedOrOnAnotherTab`,
`TypedKey_WithNoMatchingTarget_IsNotConsumed`,
`TypedKey_MatchingTheCurrentRemap_RestoresTheKeysOwnCode`,
`TypedKey_MatchingTheDeviceOverride_RestoresTheKeysOwnCode_OnRaster`,
`TypedKey_MatchingOnlyTheStagedOverride_StillPicksThatKey` in
`tests/ProoveGaming.App.Tests/DeviceDetailsKeyAssignmentDialogTests.cs`. The MainWindow
capture wiring itself is manual-QA, like the rest of the overlay wiring.

QA (additional): with the popup open on KEYS, press a letter on your physical keyboard —
the key remaps and the pressed key highlights; switch to MACROS and type in search —
nothing remaps; hold a key — only one change happens. Remap 1 → F1, reopen the dialog on
1 and press the physical 1 — the key goes back to typing "1".

## Addendum — empty-space deselect (2026-08-17)

Clicking empty space on a keyboard panel (anywhere that is not a key button) clears the
key selection — before this the selection could only move, never go away.

- Wiring: the two keyboard host panels in `DeviceDetailsView.axaml` (the merged Key
  Assignments panel and the Macros/Actuation panel) get `Background="Transparent"` and a
  bubbling `PointerPressed` → `OnKeyboardPanelPointerPressed`. A click on a key button
  never reaches the handler (the Button marks PointerPressed handled), so anything that
  does is empty space.
- `DeviceDetailsViewModel.DeselectPhysicalKey` is the "no key" counterpart of
  `SelectPhysicalKey` and branches the same way: Actuation's Per-key mode clears its own
  single-select; every other section clears `SelectedPhysicalKey`, every key/target
  highlight, hides the remap toast (its UNDO acts on the selected key), and refreshes
  the Macros radio list back to the no-selection state.

Acceptance criteria (additional):
- [ ] Given a key selected on the Macros (or Key Assignments) keyboard → clicking empty
  panel space → the key highlight clears, the selected-key header empties, and the
  Macros radio list shows no bound-macro selection.
- [ ] Given Actuation Per-key mode with a key selected → clicking empty space → the
  actuation selection clears (sliders go inert, hint returns).
- [ ] Clicking a key still selects it / opens the dialog exactly as before.

Tests: `Deselect_ClearsTheSelectionAndEveryHighlight`,
`Deselect_WithNothingSelected_IsANoOp`,
`Deselect_InActuationPerKeyMode_ClearsTheActuationSelection` in
`DeviceDetailsKeyAssignmentDialogTests.cs`. The panel hit-test wiring is manual QA,
like the rest of the overlay wiring.

*(2026-08-17, PG-494: the shared clear was extracted as `ClearPhysicalKeySelection`
and the `ActiveSection` setter now calls it — every section is entered with no key
selected, so a key picked on Key Assignments no longer arrives on Macros still
highlighted with the header and Reset armed. Test:
`SectionSwitch_ClearsThePhysicalKeySelection`.)*

## Addendum — localization-safe tab sizing (2026-08-17)

The dialog's tab strip no longer hardcodes per-caption widths (90/111px measured off the
en strings — the uk "КЛАВІШІ" rendered flush against the parallelogram's edges). Each tab
is now natural-width: the `macroTabFill` is a three-column grid (15px slant `Polygon`s +
a stretching `Rectangle`), and the label carries `Margin="24,0"` (15 slant + 9 text
padding), so a longer caption widens the tab. Same markup in `MacrosSectionView`'s
(hidden) strip; the `App.axaml` fill-visibility selectors now target `Grid.macroTabFill`.
The shared `cancelDelete`/`confirmDelete` dialog buttons moved their sizing into the
style for the same reason (`Padding="16,0"` + `MinWidth="105"`, no fixed `Width` at call
sites). The general rule is now in CLAUDE.md ("Localization-safe sizing").

## Addendum 2 — pick closes the dialog + undo toast; header RESET removed

Requested after the type-to-remap addendum, per a second design mockup (chamfered 432×88
black bar, green circle-check, message + purple UNDO link + ✕, `#6357FF` glow):

- The dialog header's **RESET button is removed**. Undo now lives on the toast; a
  toast-less reset remains possible by picking the key's own code in the picker (the
  pre-existing clear-override semantics). Macro unassign (row ✕) is unchanged.
- **Any KEYS-tab pick — clicked or typed — commits through one path**
  (`CommitRemapPickAsync`): it awaits `SelectAssignmentTarget` (so "successfully remapped"
  never precedes the device write on the immediate-apply families), closes the dialog, and
  raises the toast "**{key} key successfully remapped to {target}**"
  (`KeyDialog_RemapToast_Format`, en/uk).
- **UNDO** on the toast performs the reset (`ResetSelectedKeyAsync`), guarded on the
  selection still being the toast's key; **✕** dismisses; with no action the toast
  **auto-hides after 5 s** (`RemapToastAutoHideMs`, cancel-and-replace CTS + `Task.Delay`,
  the preset-debounce pattern).
- The toast hides whenever its undo window ends: opening the dialog for another key, or a
  device/preset layout reload.
- View: the toast lives in `DeviceDetailsView.axaml` (top-right, pure bindings — no
  MainWindow wiring), reusing the `NotificationGrid` chamfer geometry at 88 px tall with
  the mockup's icon paths.

Acceptance criteria (additional):
- [ ] Picking a target (click or physical key press) → dialog closes, toast appears with
  the correct key/target names.
- [ ] Toast UNDO → the remap is reverted (staged row deleted / device restored), toast hides.
- [ ] Toast ✕ → toast hides, the remap stays.
- [ ] No action → toast disappears by itself after ~5 s.
- [ ] Selecting another key while the toast is up → toast hides; a stale UNDO does nothing.

Tests (additional, same file): `Pick_ClosesTheDialog_AndShowsTheUndoToast`,
`ToastUndo_RevertsThePick_AndHidesTheToast`, `ToastDismiss_HidesWithoutReverting`,
`SelectingAnotherKey_HidesTheToast_AndUndoBecomesInert`; the typed-pick test now also
asserts close+toast. The 5 s auto-hide timing is deliberately untested (wall-clock) —
covered by QA.

QA (additional): remap a key from the popup — the popup closes and a black notification
with a green check appears top-right; click UNDO — the key returns to its default and the
notification closes; remap again and wait ~5 seconds — the notification disappears on its
own; remap again and click ✕ — it closes and the remap stays.

## Addendum 3 — SYSTEM tab goes live (Smart Infinite family)

The SYSTEM tab is no longer a dead gate: it is enabled for the **Smart Infinite** boards
(Cyrex / Aress / Rampage), whose firmware documents a consumer-control key record —
`CON_TYPE 0x20` with the HID Consumer Page usage split little-endian across code1/code2
(vendor's own `docs/knowledge-base/rampage-keyboard-protocol/keyboard_key_define.h`:
`CONSUME_MEDIA 0x0183 → CON_MEDIA_L 0x83 / CON_MEDIA_H 0x01`). The DeviceHost's table
sanity check (`SmartInfiniteProtocol.KnownTypes`) already accepts `0x20`, so **no DeviceHost
change** is needed. Raster/T-Racer and T800 keep the tab hidden until their firmware shows
an equivalent record type. Note: `SmartInfiniteProtocol.cs:303` labels 0x20/0x30 the other
way round ("SYSTEM_TYPE, CONSUMER_TYPE") — the vendor header is taken as authoritative.

- `IsSystemCommandsSupported => IsSmartInfiniteDevice`; re-announced on every layout load.
- **Command set** (vendor-header-backed usages): Volume Up `0x00E9`, Volume Down `0x00EA`,
  Mute `0x00E2`, Play/Pause `0x00CD`, Next Track `0x00B5`, Previous Track `0x00B6`,
  Brightness Up `0x006F`, Brightness Down `0x0070`, Browser Back (AC Back) `0x0224`.
  The design's ambiguous "Back"/"Volume Toggle" placeholders were dropped in favour of
  these; labels reuse `Macros_Command_*` plus three new `KeyDialog_Command_*` keys (en/uk).
- **Architecture:** command rows are ordinary `KeyAssignmentOptionViewModel` targets
  appended to `AssignmentTargets` (`RebuildSystemCommandTargets`, called from
  `LoadKeyLayoutForDevice`; they survive the macro-row rebuild) and mirrored in
  `SystemCommandTargets` for the tab's list. That makes everything downstream free:
  the whole-record selection match highlights a command-bound key, the key reads as
  overridden (resettable), a pick routes through `PickRemapTargetCommand` →
  `CommitRemapPickAsync` → closes the dialog + undo toast, and UNDO restores the key's own
  record — identical to a KEYS pick.
- Tab list rows reuse the shared `macroItemRow` style with **OneWay** `IsChecked` (the
  highlight belongs to `SyncAssignmentTargetSelection`; a TwoWay radio group would fight
  its sweep). No search field on this tab (9 fixed rows) — deliberate deviation from
  mockup 2.

Acceptance criteria (additional):
- [ ] On Cyrex/Aress/Rampage the dialog shows KEYS / SYSTEM / MACROS; on Raster/T-Racer/
  T800 the SYSTEM tab is absent.
- [ ] Picking e.g. Volume Up writes `{0x20, 0xE9, 0x00}` to the key's slot, closes the
  dialog and raises the toast; the physical key then adjusts system volume.
- [ ] Reopening the dialog on that key highlights the Volume Up row on SYSTEM.
- [ ] Toast UNDO restores the key's own record; the key types its letter again.

Tests (additional, same file): `SystemTab_IsGatedOff_OutsideTheSmartInfiniteFamily`,
`SystemCommands_AreBuiltAsConsumerRecords_OnCyrex`,
`PickingASystemCommand_WritesTheConsumerRecord_AndCommitsLikeAKeyPick`,
`ToastUndo_AfterACommandPick_RestoresTheKeysOwnRecord`; the Rampage layout
characterization test now counts command rows separately. **Hardware verification of the
`0x20` consumer write is pending** — the record shape is vendor-documented but this app has
never written one; QA on a real board is the gate.

QA (additional): on Cyrex — open the popup for a letter key, switch to SYSTEM, pick
"Volume Up". Expected: popup closes, toast appears; pressing the key changes the Windows
volume. Reopen the popup on that key → SYSTEM shows Volume Up selected. Press UNDO on a
fresh pick → the key types its letter again. On T-Racer — no SYSTEM tab in the popup.

## Addendum 4 — unsaved remaps on the immediate-write family revert on Discard

Hardware QA (Addendum 3) surfaced a real gap: on Cyrex, a remap made in the dialog and
**not saved** to the preset survived exiting the page — no unsaved-changes prompt, no
revert. Root cause: the Smart Infinite / T800 pick writes the device immediately and left
**no trace the preset system could see** — nothing to make the preset dirty, nothing for
`LoadPresetSnapshotToDevice` to restore, and no push path to write the previous record
back. (Raster/T-Racer were never affected: their staged rows already round-trip through
`key_bindings ↔ preset_key_bindings`.)

Fix — bring the family into the existing preset model, reusing all its plumbing:

- **Persistence:** every immediate pick now also writes `key_bindings`, keyed by **matrix
  slot** (stored in the same `phys_scancode` column; this family's modifiers all share
  scancode 0x00, so scancode keying cannot work — the keyings never mix because a device
  is always one family). The key's own record or a macro target deletes the row. New maps:
  `_slotKeyOverrides` (mirror of the rows) and `_appliedSlotOverrides` (slots this session
  wrote a non-default record to; survives layout reloads like `_appliedMacroSlots`,
  cleared on device change).
- **Dirty / prompt:** `PresetMatchesLiveState` already compares `key_bindings`, and the
  pick already called `StageChangeAndApply` — with rows now written, the unsaved-changes
  prompt appears on exit. Snapshot backfill runs once at page load (pre-edit), so it
  cannot legalise the edit.
- **Revert / push:** `ApplyKeyOverridesToDeviceAsync` branches to a new slot-keyed twin,
  `ApplySlotKeyOverridesToDeviceAsync`: desired = persisted row (or the key's own record),
  baseline = the `_assignments` device mirror, re-read via
  `ReadSlotKeyedAssignmentsAsync` when a reload nulled it; if the re-read fails, only
  slots claimed by a row or by `_appliedSlotOverrides` are written (a blind full-matrix
  write is not an option — each slot write is a read-modify-write of the whole table).
  Runs from every preset push (Save / switch / **Discard** / connect). Macro-bound slots
  keep the documented ordering: the key push writes through them, the macro resync
  re-asserts right after — and the resync's stale-slot restore now consults
  `_slotKeyOverrides` so a discarded macro falls back to the persisted remap, not blindly
  to factory.
- Bonus: `SelectedKeyHasOverride` also counts a persisted row, so Reset/undo affordances
  work before the device read lands. Save Preset now snapshots these remaps, so they are
  correctly re-asserted per preset and on reconnect.

Acceptance criteria (additional):
- [ ] Cyrex/Aress/Rampage/T800: remap or bind a command in the dialog, do NOT save, press
  Back → the unsaved-changes prompt appears; Discard → the key physically reverts.
- [ ] Save Preset after a remap → switch presets away and back → the remap follows the
  preset (reverted on the other preset, re-asserted on return).
- [ ] Reconnect after Save → the remap survives (preset re-apply pushes it).
- [ ] Raster/T-Racer staged behavior unchanged.

Tests (additional, same file): `ImmediateFamilyPick_PersistsASlotKeyedRow_AndSelfPickClearsIt`,
`PresetPush_WritesBackTheDefault_WhenTheOverrideRowIsGone` (no-baseline fallback path),
`PresetPush_ReassertsThePersistedOverride_AfterAReload`. Full-suite 740/740.

QA (additional): on Cyrex — remap Q to Volume Up in the popup, do not save the preset,
press Back. Expected: the unsaved-changes dialog appears; choose Discard → Q types "q"
again. Repeat and choose Save instead → Q keeps Volume Up after app restart and device
reconnect. Note the Discard revert re-reads the key matrix (~1 s) before writing.

## Addendum 5 — Key Assignments panels merged into one full-width panel

Requested after Addendum 4, per a new design mockup: on the keyboard **Key Assignments**
page the former two-panel layout (420px left panel with the selected-key title / hint +
the keyboard panel) becomes **one full-width panel** with the keyboard centered — the
dialog already names the selected key, so the left panel only restated it.

- `DeviceDetailsView.axaml`: the keyboard Buttons branch drops the 420×756 left panel and
  `KeyAssignmentsSectionView`; the keyboard panel spans the whole content area.
- `ButtonsBackground3Margin` returns the 12px offset for keyboards unconditionally (the
  436px branch now applies to the mouse layout only).
- The `KeyAssignmentReadFailed` warning banner moves into the merged panel (top-center,
  over the keyboard area) so the factory-fallback state is still announced.
- Deleted: `KeyAssignmentsSectionView.axaml(.cs)`; removed the now-unused
  `KeyAssignments_SelectKeyHint` (en/uk) and the dead `HasSelectedPhysicalKey` property.
- Mouse Buttons layout and all other keyboard sections are untouched.

## Addendum 6 — per-key hover tooltip on the keyboard overlay

Per a design mockup: hovering any key on the interactive keyboard shows a tooltip — the
key's name (white) with its **press actuation** underneath ("ESC / 2.08", muted `#B2BEBCEA`)
on the badge's black-chip-with-`#6357FF`-glow chrome. On devices without actuation support
the value line collapses and the tooltip is just the shorter name chip.

- Implemented inside `RasterKeyboardOverlayView` as a `TipLayer` canvas (the badge's
  pattern), positioned from code-behind on each key button's `PointerEntered`/`Exited` —
  Avalonia's `ToolTip` chrome (popup panel, own theme, below-pointer placement) is exactly
  what the design is not. The layer is fully click-through; sizes are stage-width fractions
  for the badge's ~18%-viewBox-variance reason. The tip always sits above the key,
  horizontally centred on it (average-width estimate centres, upper-bound estimate feeds
  the right-edge clamp — the badge's split) — including past the stage top on the ESC /
  function rows, as the mockup shows. That required `ClipToBounds="False"` on **both** the overlay's Viewbox (Avalonia
  overrides the Viewbox default to clip) and the UserControl root (clips in practice too —
  the repo's other chrome-overflowing UserControls all disable it there). Verified on a
  live Cyrex: top-row tip fully visible above the board on the section page.
- `DeviceDetailsViewModel.GetKeyActuationTooltip(key)` (Actuation partial) supplies the
  value line: per-key value by matrix slot when the board reports one, else the uniform
  press value, formatted invariantly with no unit; null when actuation is unsupported.
- Shows on the section-page overlay instances (Key Assignments, Macros, Actuation). The
  dialog's KEYS picker goes without it — like its badge layer — per explicit request: the
  dialog's hint already names the interaction, and the chip over the dialog chrome reads
  as clutter.
- Tests: `KeyTooltip_ShowsThePerKeyPressValue`, `KeyTooltip_FallsBackToTheUniformValue_WithoutAPerKeyMap`,
  `KeyTooltip_IsNull_WhenActuationIsUnsupported` in `DeviceDetailsActuationViewModelTests`.
  Hover wiring/positioning is view-only and covered by QA.

## Addendum 7 — pick purpose for Extended Keys (2026-09-09)

The dialog gained a second purpose, `KeyDialogPurpose.PickExtendedKeyOutput`, used by the Extended
Keys single-key editor (Mod-Tap now, Toggle later — `docs/plans/add-extended-key-mod-tap/`) as its
output-key picker. `DeviceDetailsViewModel.OpenKeyDialogForPick(role, current, onPicked)` opens it on
KEYS with the title "Choose the {role} key", no SYSTEM / MACROS tab and no RESET link; a click on the
picker or a typed key hands the target's `(TypeByte, Bitmask, ScanCode, Label)` to the callback and
closes — it never calls `StageAssignmentTarget` / `CommitRemapPickAsync`, writes nothing and raises
no toast. Every close (✕, pick, editor cancel, device reload) resets the purpose to `Remap` and drops
the callback, so the remap flow described above is unchanged whenever no pick is in progress.
Pinned by the `PickPurpose_*` tests in `DeviceDetailsKeyAssignmentDialogTests`.

## Review findings

### Round 6 (PR #117, external review) — Minor fixed

- [x] **m2. The restore pick's toast read "Клавішу 1 успішно перепризначено на 1"** —
  the Round 5 nit, confirmed by the external review — fixed 2026-08-20:
  `ShowRemapToast` uses the new `KeyDialog_ResetToast_Format` resource (en/uk) whenever
  the pick is the key's own row (typed-echo redirect and picker self-click alike); the
  undo target is unchanged. Pinned by the toast assertion added to
  `TypedKey_MatchingTheCurrentRemap_RestoresTheKeysOwnCode`.

### Round 5 (branch `bugfix/PG-366`, working tree) — Ship: yes

#### Summary
One fix in this plan's scope: the type-to-remap "remapped-key echo" (QA 2026-08-20). The
board remaps in firmware, so a key overridden to F1 EMITS F1 — pressing it to restore the
key re-picked F1 forever. `TryPickRemapTargetFromKeyboard` now redirects a press that
matches the selected key's current device record to the key's own row
(`SelectedKeyCurrentlyEmits`, `DeviceDetailsViewModel.KeyDialog.cs:301-353`). 3 new tests.
The branch also carries an unrelated low-battery notification fix — reviewed in its own
plan (`docs/plans/fix-low-battery-notifications/`, Round 1).

#### Plan conformance
Plan: this plan, addendum "type-to-remap".
- Implemented as planned: the addendum's new echo bullet, acceptance criterion, test list
  and QA step all match the code and the actual test names.
- Accepted limitation (per plan): pressing the real F1 key to re-confirm an existing →F1
  remap now resets the key instead — residual risk: a surprised user, mitigated by the
  undo toast; the alternative (no redirect) leaves the reported bug in place.

#### Code
- none. Probed specifically: both push paths keep the device-truth baselines the redirect
  reads in step (`ApplyKeyOverridesToDeviceAsync` updates `_userOverrides` per write,
  `KeyAssignments.cs:1825-1829`; the immediate path updates `_assignments` and
  `_slotKeyOverrides`, `KeyAssignments.cs:2004-2056`), so the redirect is correct
  mid-session, not only after a re-probe. Macro (0x50/0x51/0x60/0x09) and consumer (0x20)
  records can never false-match a keyboard-usage target row, and command rows' ScanCode
  bytes (0x00/0x02) sit below the first real keyboard usage (0x04).

#### Tests
- none missing. The three new tests cover the three branches that matter: immediate-write
  family redirect (Cyrex), applied-override redirect on the staging family (Raster via the
  new settable `ProbeResult`), and the negative — a staged-but-unapplied override must NOT
  redirect, because the board still emits the old code.

#### Nits
- The restore pick's toast reads "Клавішу 1 успішно перепризначено на 1" — truthful but
  self-referential; a reset-flavoured message would read better.
  `DeviceDetailsViewModel.KeyDialog.cs:308-313` / `ShowRemapToast`.
- `TypedKey_MatchingTheCurrentRemap_RestoresTheKeysOwnCode` hard-codes `(byte)0x10` where
  `d1.TypeByte` would survive a family type change —
  `DeviceDetailsKeyAssignmentDialogTests.cs:674,680`.

#### What's done well
- `SelectedKeyCurrentlyEmits` reads the DEVICE-truth maps, not the staged ones, and the
  doc comment says why — that distinction is exactly what makes the Raster negative test
  correct semantics rather than a coincidence.
- The redirect is scoped to the one ambiguous case (press == what the selected key emits),
  where the old behaviour was a guaranteed no-op — so nothing reachable got worse.

#### Verdict
Ship: yes — no blockers, no should-fixes; the two nits are cosmetic. First: nothing
required; optionally the toast wording nit.

### Round 4 (branch `bugfix/PG-371`) — Ship: no

#### Summary
Three-commit bugfix branch: **PG-587** — a typed pick's still-held key no longer re-clicks
the focused key tile and reopens the dialog (owned-press swallow in MainWindow's key-dialog
handlers); **PG-582** — the Change-password dialog's email field made fully inert
(`IsReadOnly` + `Focusable="False"` + `IsHitTestVisible="False"`); **PG-585** — clicking
empty space on a keyboard panel clears the key selection (`DeselectPhysicalKey` +
transparent hit-test panels). Also touches `docs/plans/presets` (dropdown closed before the
unsaved-changes dialog opens) and `docs/plans/refactor-change-password-flow` — both updated
with dated notes, no findings in either. 4 new tests, suites green locally (CI does not run
`tests/`).

#### Plan conformance
Plan: this plan (addenda "type-to-remap" and "empty-space deselect (2026-08-17)"), plus
`docs/plans/presets/presets-plan.md` and
`docs/plans/refactor-change-password-flow/refactor-change-password-flow-plan.md`.
- Implemented as planned: all three fixes match their dated plan notes; acceptance criteria
  and QA steps updated in the same commits; named tests exist and pass.
- Deviates from plan: none.
- Missing / partial: the deselect addendum says the Actuation branch mirrors the Per-key
  single-select — it mirrors the selection clear but **not** the flush discipline that every
  other selection change carries (B1 below).

#### Code
- [x] **B1. Empty-space deselect in Actuation Per-key mode drops a pending slider edit** —
  *(fixed 2026-08-17 with Option A: `FlushPendingActuationApply()` before
  `ClearActuationSelection()` in `DeselectPhysicalKey`'s actuation branch; regression test
  `EmptySpaceDeselect_FlushesThePendingEditToTheDeselectedKey`.)*
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs:1391`
  (`DeselectPhysicalKey`, actuation branch) — Blocker
  **Problem:** every existing context change flushes the debounced actuation edit before
  mutating the selection — `SelectActuationKey` (`…Actuation.cs:206`) and the mode toggle
  (`…Actuation.cs:163`) both call `FlushPendingActuationApply()` first, and the field doc
  (`…Actuation.cs:71-73`) states that discipline explicitly. The new deselect branch calls
  `ClearActuationSelection()` without flushing.
  **Why it blocks:** Per-key mode, key W selected → drag the press-actuation slider → click
  empty panel space within the 400 ms debounce (`ActuationApplyDebounceMs`) → the debounce
  fires with nothing selected, `BuildActuationWriteRequest()` returns null, and the edit is
  silently dropped: W keeps its old actuation and re-selecting it shows the old value. A
  user edit is lost with zero feedback.
  **Solution options:**
  - Option A (correct, one line): call `FlushPendingActuationApply()` at the top of the
    actuation branch in `DeselectPhysicalKey`, before `ClearActuationSelection()` — same
    order as `SelectActuationKey`.
  - Option B (equivalent): route the branch through a small shared
    `DeselectActuationKey()` on the Actuation partial that owns flush + clear + raise, so
    the discipline has one home.
- [x] **S1. A fresh key press during the in-flight assignment write can still reopen the
  dialog** —
  *(fixed 2026-08-17 with the recommended root fix: the overlay key-tile Buttons are created
  with `Focusable = false` in `RasterKeyboardOverlayView.RebuildKeyboard`, so Space/Enter can
  never "click" a tile regardless of timing. View-level — covered by the QA step.)* `src/ProoveGaming.App/Views/MainWindow.axaml.cs:603` (`OnKeyDialogKeyDown`) /
  `RasterKeyboardOverlayView.axaml.cs:220` (tile `Button` creation) — Should-fix
  **Problem / why it matters:** the owned-press swallow only covers the press that made the
  pick. A *second* discrete press of Space/Enter inside the write window (dialog already
  optimistically closed, `IsKeyAssignmentDialogOpen == false`, so the handler early-outs
  before tracking) reaches the still-focused key tile and clicks it — the dialog reopens,
  which is the same symptom PG-587 fixes, just needing a double-tap instead of a hold. The
  2.4G write window (~1 s) makes this reachable.
  **Fix:** make the overlay key-tile Buttons `Focusable = false` in
  `RasterKeyboardOverlayView.RebuildKeyboard` — they are pointer-only controls (the physical
  keyboard is the keyboard path), keyboard focus on them serves nothing, and this kills the
  whole click-by-Space class at the root (repeat, double-tap, and any future variant).
  Alternative: expose `IsAssignmentWriteInFlight` and swallow mapped keydowns while it is
  true — narrower, leaves the focus landmine in place.

#### Tests
- [x] With the B1 fix: a test that a pending per-key actuation edit is flushed to the
  previously selected key on empty-space deselect (pattern: the existing flush tests in
  `DeviceDetailsActuationViewModelTests.cs`; assert the fake HID received the per-key write
  for the deselected key's slot). *(Added:
  `EmptySpaceDeselect_FlushesThePendingEditToTheDeselectedKey`; full App suite 818/818.)*
- [x] With the S1 fix (if Option A): no automated test possible for focus — cover via the
  QA step already added to the type-to-remap addendum. *(S1 fixed via `Focusable = false` —
  manual QA per the addendum.)*

#### Nits
- Keyups of *unconsumed* tracked presses are now swallowed too
  (`MainWindow.axaml.cs:635`): in the reopened-dialog-during-write corner a focused
  control's Space keydown can set `IsPressed` whose keyup never arrives — transient stuck
  pressed visual, no functional effect.
- The transparent deselect panel is live on every non-Buttons keyboard section, including
  RGB lighting — a click on the keyboard art there silently clears a selection made on
  Macros. Harmless and arguably consistent; noting so it is a decision, not an accident.

#### What's done well
- Each fix updates its owning plan in the same commit (dated notes, AC and QA reconciled) —
  exactly the drift-prevention CLAUDE.md asks for.
- The owned-press model in the key-dialog handlers (track on keydown, swallow repeats and
  keyup until release) is the right shape — it protects *whatever* holds focus, not just
  the key tile, and the comments explain the 2.4G timing that makes it reachable.
- `DeselectPhysicalKey` correctly mirrors `SelectPhysicalKey`'s branch structure and closes
  the same bookkeeping (toast, target highlights, macro radio list) the selection paths do.
- The preset-dropdown fix lands at the one choke point (`OpenUnsavedChangesForSwitch`) with
  a test that pins the light-dismiss reasoning.

#### Verdict
Ship: no — B1 silently loses a user's actuation edit and contradicts the module's own flush
discipline; the fix is one line, so this is a fast turnaround. First: (1) B1 flush before
clear, (2) its flush test, (3) S1 `Focusable = false` on the overlay tiles.

### Round 3 (PR #94, external review) — Majors fixed

- [x] **M1. Toast UNDO reset the key to factory instead of restoring the displaced state,
  destroying a previously saved remap** — fixed with the reviewer's Option A:
  `CommitRemapPickAsync` captures the displaced target (`CaptureUndoTarget`) BEFORE the
  apply mutates the override maps — immediate family: persisted `_slotKeyOverrides` row,
  else the `_assignments` device mirror (which for a macro-bound slot is the macro record,
  so undo re-binds the macro through the full path); staging family: the
  `_stagedKeyOverrides` entry, key rows only. UNDO routes the captured row back through
  `SelectAssignmentTargetCore`; a key with no previous override falls back to the existing
  reset. Tests: `ToastUndo_RestoresThePreviousStagedRemap_NotTheFactoryDefault`,
  `ToastUndo_RestoresThePreviousCommand_OnTheImmediateFamily`; the pre-existing
  own-record undo tests still pass through the fallback.
- [x] **M2. A second pick during the ~420 ms in-flight write started a second RMW on the
  same slot, letting the device and the bookkeeping record different picks** — fixed with
  the reviewer's Option A: `CommitRemapPickAsync` early-outs on `_assignmentWriteInFlight`
  (set before the first await, all UI-thread — race-free), and
  `TryPickRemapTargetFromKeyboard` returns false on it so the ignored press is not
  swallowed either. Test: `SecondPick_WhileAWriteIsInFlight_IsIgnored` (holds the write
  open via a `TaskCompletionSource` in the fake HID; clicked and typed second picks both
  land inside the window).
- Minors m1–m13: not addressed in this pass (follow-up sized; m3 extends r1-S2/r2-S4).

### Round 1 — Ship: yes

#### Summary
The branch implements the Key Assignment Dialog: clicking a key on the interactive keyboard
in Key Assignments / Macros opens a modal (chamfered chrome, title "<KEY> button", RESET, ✕)
with a KEYS visual remap picker and a MACROS search+list tab; the SYSTEM tab ships gated off.
The inline `AssignmentTargets` list is removed from the Key Assignments panel; shared
tab/search/row styles moved to `App.axaml`; MainWindow `DialogOverlay` wiring added at all
four touchpoints; 8 new localization keys (en+uk) including the two previously hardcoded
strings; 12 new VM tests. Full suite green (724/724), solution builds with 0 errors and only
pre-existing warnings. All work is uncommitted on `main`'s working tree.

#### Plan conformance
Plan: `docs/plans/add-key-assignment-popup/add-key-assignment-popup-plan.md`
- Implemented as planned: all 14 file-level items (incl. `docs/CONTEXT.md` glossary done at
  plan time); dialog opens from both keyboard sections and never from Actuation per-key;
  picker routes through `SelectAssignmentTarget` unchanged; dialog closes on
  `LoadKeyLayoutForDevice` (device change / preset switch); stays open across picks.
- Deviates from plan (all reported by /coder, none harmful):
  1. No separate `KeyDialogTitle` — the dialog binds the now-localized
     `SelectedPhysicalKeyWithSuffix` (`…KeyAssignments.cs:165`). Plan's intent (resourced
     title) is met without a duplicate property.
  2. SYSTEM tab static command rows were **not** duplicated into the dialog markup — only the
     gated tab button + empty content host (`KeyAssignmentDialog.axaml:117,146`), with a
     comment pointing at the existing hidden COMMANDS strip. Better than the plan's letter.
  3. Picker highlight for a macro-bound key on the **staging** families (Raster/T-Racer)
     shows the key's own row, not an empty selection — faithful to the pre-existing target
     list semantics; documented in `DeviceDetailsViewModel.KeyDialog.cs:104-111` and pinned
     by `MacroBinding_KeepsDialogOpen_AndPickerFollowsTheTargetList`.
- Missing / partial: of the plan's nine mandatory tests, "picker rebuilt on device layout
  change" is only implicitly covered, and the acceptance criterion "mouse device never opens
  the dialog" has no direct test (see Tests below).
- Accepted limitation (per plan): hint/tab copy pending Figma confirmation — residual risk:
  visible-string churn later, localization keys already in place. SYSTEM commands not
  device-assignable — residual risk: none (tab hidden).

#### Code
- none (no Blockers)

- [ ] **S1. Picker highlight lags the click on immediate-apply keyboards** —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs:1322` — Should-fix
  **Problem / why it matters:** `SelectAssignmentTarget` optimistically highlights the
  clicked target row (`foreach` at :1322-1323) before any await, but
  `SyncRemapTargetPickerSelection()` only runs when `SyncAssignmentTargetSelection()` does —
  for Smart Infinite / T800 that is *after* the device write completes inside
  `ApplyAssignmentTargetCoreAsync` (~420 ms measured on Cyrex). Concrete scenario: Cyrex,
  KEYS tab, click "W" → nothing fills for ~0.4 s, then the highlight appears. The old inline
  list gave instant feedback (the same foreach), so the dialog feels less responsive than
  what it replaced. Staging families are unaffected (their sync is synchronous).
  **Fix:** call `SyncRemapTargetPickerSelection()` immediately after the foreach at :1323 —
  exactly as optimistic as the target-row highlight it mirrors; the existing failure path
  (`!ok` → `SyncAssignmentTargetSelection()`) already reverts both together.

- [ ] **S2. Second copy of the search field + macro row markup** —
  `src/ProoveGaming.App/Views/DeviceDetails/Dialogs/KeyAssignmentDialog.axaml:151-166,168-215`
  vs `MacrosSectionView.axaml` — Should-fix
  **Problem / why it matters:** the styles were correctly moved to `App.axaml`, but the
  search-field block (Border+TextBox+icon) and the macro row template (radio + name + ✕) are
  now structural copies in two views — CLAUDE.md's "the trigger is the second copy" rule.
  The kebab menu is the only real variant. Drift risk is real: the ✕ gate comment exists in
  one copy only.
  **Fix:** extract the search field into a small shared control (or a `DataTemplate`
  resource for the row with an `IsKebabVisible`-style variant), per "keep the variant, share
  the rest". Acceptable as an immediate follow-up if the team prefers not to grow this
  branch.

#### Tests
- [ ] Mouse device never opens the dialog — acceptance criterion with no direct test; add a
  case that sets a mouse card (e.g. BLAZE) and asserts `IsKeyAssignmentDialogOpen` stays
  false through hotspot/section interactions — `tests/ProoveGaming.App.Tests/DeviceDetailsKeyAssignmentDialogTests.cs`
- [ ] `RemapTargetKeys` rebuilt on device layout change — plan test 4's second half; add a
  second `SetDevice` (different family, e.g. Cyrex after Raster) and assert the picker
  collection now mirrors the new catalog — same test file

#### Nits
- Fourth per-file copy of `FakeUsbHidDeviceService` in the test project — repo convention,
  but a shared fixture is overdue — `tests/ProoveGaming.App.Tests/DeviceDetailsKeyAssignmentDialogTests.cs:29`
- Dialog is not dismissible with Esc or a scrim click — consistent with every existing
  overlay dialog, but for a high-frequency surface (opens on every key click) worth a
  product decision — `KeyAssignmentDialog.axaml`
- MACROS tab renders (empty) even for a keyboard card with `SupportsMacros = false`; no
  shipped keyboard hits this today — `KeyAssignmentDialog.axaml:126`
- The always-present dialog adds a fourth `RasterKeyboardOverlayView` doing its O(n)
  rebuild-per-insert during layout load — pre-existing pattern, +1 instance, load-time only.

#### What's done well
- Routing every pick through `SelectAssignmentTarget` preserves the per-family
  staging/immediate semantics and their regression tests untouched — zero new device-write
  paths.
- A separate `RemapTargetKeys` collection cleanly avoids `IsSelected` crosstalk with the
  physical-key overlay and the actuation selection.
- MainWindow wiring is complete at all four fragile touchpoints (whitelist, refresh,
  accessor, overlay child) and the dialog is force-closed on layout reload.
- Localization is thorough: 8 new keys in both languages, plus the two pre-existing
  hardcoded strings ("Select a key on the keyboard", " button" suffix) migrated.
- The style move (not copy) into `App.axaml` follows the repo's second-copy rule for the
  tab/search/row *styles*, and the new tests pin open/close, routing and picker-sync
  behavior rather than restating defaults.

#### Verdict
Ship: yes — no blockers; S1 is a small, contained UX polish and S2/tests are follow-up
sized. First: 1) S1 (one-line picker sync in `SelectAssignmentTarget`), 2) the two test
additions, 3) decide whether S2's extraction lands now or as a follow-up.

### Round 2 — Ship: yes

#### Summary
Since round 1 the branch gained three addenda: type-to-remap (physical-keyboard capture
while the dialog's KEYS tab is up, tunneled MainWindow handlers + `HidKeyCodeMap` with a
Smart Infinite modifier-bitmask fallback), pick-commits-and-closes with an undo toast
(header RESET removed; `CommitRemapPickAsync` awaits the apply, closes the dialog, raises a
432×88 toast in `DeviceDetailsView` with UNDO / ✕ / 5 s auto-hide), and a live SYSTEM tab
for the Smart Infinite family (consumer records `{0x20, usageL, usageH}` built as ordinary
`AssignmentTargets` rows, mirrored in `SystemCommandTargets`). 735/735 tests pass; the App
builds clean (the only full-slnx failures are the running DeviceHost service locking its
own exe — unrelated).

#### Plan conformance
Plan: this document, Addenda 1–3.
- Implemented as planned: all three addenda match their stated design, including the
  deliberate deviations they record (no SYSTEM search field; "Back"/"Volume Toggle"
  replaced by header-backed usages; Escape is a pick, not a dismiss).
- Round 1 items: **S1 (picker highlight lag) is superseded** by Addendum 2 — the dialog
  now closes on pick, so the optimistic-highlight window barely exists; recommend marking
  it closed-as-superseded. **S2 (markup duplication)** unchanged, still open, and Addendum
  3 added a third copy of the row template (see r2-S4). The two round-1 test gaps (mouse
  never opens the dialog; picker rebuilt on device change) are still open.
- Accepted limitation (per plan): hardware verification of the `0x20` consumer write is
  pending — see r2-S2 for the reviewer's source-conflict analysis and residual risk.

#### Code
- none (no Blockers)

- [x] **S1 (r2). Toast claims success for writes that never happened** — *(fixed: Option A —
  `SelectAssignmentTargetCore` returns whether anything was staged/written;
  `StageAssignmentTarget` → `bool`, `ApplyAssignmentTarget(Core)Async` → `Task<bool>`;
  `CommitRemapPickAsync` gates close+toast on it and the guard early-outs re-sync the
  optimistic highlight. Plus Option B's "ideally": `SelectPhysicalKey` no longer opens the
  dialog at all on a read-only transport.)* —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyDialog.cs:151-163`
  (`CommitRemapPickAsync`) — Should-fix
  **Problem / why it matters:** the commit closes the dialog and shows "successfully
  remapped" unconditionally after the await, but both apply paths can silently do nothing:
  `StageAssignmentTarget` (`…KeyAssignments.cs:1400`) and `ApplyAssignmentTargetAsync`
  (`…KeyAssignments.cs:1526`) early-return on a read-only transport
  (`!IsDeviceConfigurable`), and the immediate-write path swallows a failed
  `SetKeyMappingAsync` (`!ok` → highlight revert only). Concrete scenario: keyboard on a
  non-configurable transport (the guards exist precisely because that state is reachable),
  user picks F1 → nothing staged, nothing written, toast affirms success. Also: Cyrex USB
  yanked mid-write → same lying toast.
  **Fix:** Option A (correct): thread a success bool up (`StageAssignmentTarget` returns
  whether it staged; `ApplyAssignmentTargetCoreAsync` already computes `ok`) and gate the
  close+toast on it, showing nothing (or an error) otherwise. Option B (quick): gate
  `CommitRemapPickAsync` — and ideally `OpenKeyAssignmentDialog` — on
  `IsDeviceConfigurable`; the failed-write case remains a known gap, matching the inline
  list's old silent behavior.

- [ ] **S2 (r2). Hardware-verify the `0x20` consumer write before merge (source conflict
  resolved on paper, not on the bench)** —
  `DeviceDetailsViewModel.KeyDialog.cs` (`SmartInfiniteConsumerType`) — Should-fix
  **Problem / why it matters:** the KB contains two contradictory enums:
  `docs/knowledge-base/smart-infinite-protocol/README.md:53-54` says `0x20 = SYSTEM_TYPE,
  0x30 = CONSUMER_TYPE`, while `macro-format-gamingkeyboard2.md:70` ("the KeyType enum —
  this firmware line", from Keyborder's `index-C1SFvu4g.js`) and the Rampage vendor header
  both say `0x20` = multimedia/consumer. The reviewer resolves this in the code's favour:
  the README enum is the *other* firmware line — it also puts macros at `0x60/0x61`, the
  exact mapping already proven wrong on this hardware (the historic
  stored-verified-plays-nothing bug), whereas the GamingKeyboard2 enum's `0x50/0x51` macros
  are hardware-verified. Still, this family verifies read-back even for records it never
  plays, so a wrong type byte is undetectable in software — only the bench settles it.
  **Fix:** run the plan's Addendum 3 QA on a real Cyrex/Rampage before merging. If Volume
  Up does nothing, flip to `0x30` (and re-test per board). Also fix the misleading
  `SmartInfiniteProtocol.cs:303` comment (see Nits).

- [x] **S3 (r2). SYSTEM rows' OneWay `IsChecked` may go stale after a user click** —
  *(fixed: rows are now plain Buttons with `Classes.selected="{Binding IsSelected}"` via the
  shared `commandOptionRow` style in App.axaml — the proven inline-list pattern; ellipse
  `.selected` overrides declared after the base styles so the later-wins rule keeps the dot
  visible.)* —
  `src/ProoveGaming.App/Views/DeviceDetails/Dialogs/KeyAssignmentDialog.axaml:117` — Should-fix
  **Problem / why it matters:** the row is a grouped `RadioButton` whose `IsChecked` binds
  OneWay. A user click sets the control's checked state locally; whether the next binding
  push (from `SyncAssignmentTargetSelection`) reliably overrides that local value depends
  on Avalonia's toggle implementation using `SetCurrentValue`. If it does not, the failure
  is: pick Volume Up for key A (row checked), reopen the dialog on unbound key B → Volume
  Up still renders selected. The removed inline target list used a plain `Button` +
  `Classes.selected` for exactly this class of RadioButton-vs-sync fights (its own comment
  said so).
  **Fix:** verify in the running app during QA; if stale, switch the row to the proven
  Button + `Classes.selected="{Binding IsSelected}"` pattern (no radio group at all).

- [ ] **S4 (r2). Row-template copies grew from two to three** —
  `KeyAssignmentDialog.axaml:113-137` (SYSTEM rows) vs `:186-231` (MACROS rows) vs
  `MacrosSectionView.axaml` — Should-fix (extends r1-S2)
  **Problem / why it matters:** the radio-ellipse + label row markup now exists in three
  places (styles are shared, structure is not). CLAUDE.md's second-copy rule triggered a
  round ago; the third copy compounds drift risk (the ✕-gate comment already lives in only
  one of them).
  **Fix:** one shared row control/DataTemplate with "kebab" and "unassign" variants, per
  "keep the variant, share the rest" — reasonable as an immediate follow-up.

#### Tests
- [ ] (carried from r1) Mouse device never opens the dialog — acceptance criterion still
  untested — `tests/ProoveGaming.App.Tests/DeviceDetailsKeyAssignmentDialogTests.cs`
- [ ] (carried from r1) `RemapTargetKeys` rebuilt on device layout change (second
  `SetDevice` with a different family) — same file
- [x] S1 (r2) regression test once fixed: pick on a non-configurable device → dialog stays
  open (or closes without toast), `IsRemapToastVisible` stays false — *(added:
  `ReadOnlyTransport_NeverOpensTheDialog_AndAPickShowsNoToast` and
  `RefusedDeviceWrite_KeepsTheDialogOpen_WithNoToast`)* — same file
- 5 s auto-hide deliberately untested (wall-clock) — accepted in Addendum 2.

#### Nits
- `SmartInfiniteProtocol.cs:303` comment labels `0x20, 0x30` as "SYSTEM_TYPE,
  CONSUMER_TYPE" — quoted from the enum of the *other* firmware line; after S2's bench
  check, align the comment with whichever byte actually plays.
- Typed picks are not gated on `_assignmentWriteInFlight`: mashing keys on a Cyrex can
  start overlapping 225-byte read-modify-write matrix writes. Pre-existing exposure
  (inline-list double-clicks could too), but type-to-remap makes it much easier to hit —
  `MainWindow.axaml.cs` (`OnKeyDialogKeyDown`) / `KeyDialog.cs:197`.
- Command labels and toast text snapshot `LocalizationManager.GetString` at build/show
  time; a language switch needs a device reload to update them — matches the existing
  `RebuildMacroAssignmentTargets` precedent.
- The remap toast (DeviceDetailsView, top-right) can overlap MainWindow's
  `NotificationGrid` (also top-right) if a device notification fires simultaneously.
- `_remapToastCts` instances are cancelled but never disposed — same as the
  `_deviceApplyDebounceCts` precedent.

#### What's done well
- Command rows as ordinary `KeyAssignmentOptionViewModel` targets is the right cut: the
  whole-record highlight, resettability, undo and the toast all came for free, with zero
  new device-write paths and no DeviceHost change.
- The consumer usages come from the vendor's own defines, and the tests pin both
  byte-order cases (`0x00E9` and `0x0224`) plus the exact wire record — if the byte order
  regresses, a test names it.
- `CommitRemapPickAsync` awaits the apply before announcing success on the immediate-write
  family, and the `ReferenceEquals` guard keeps a mid-write device switch from toasting a
  stale key.
- The auto-repeat suppression on typed picks documents *why* (each repeat is a device
  write), and the recorder-ownership guard makes the input pipeline ordering explicit.
- The Rampage characterization test was updated to count command rows separately instead
  of being deleted or loosened.

#### Verdict
Ship: yes — no blockers; S2's bench check is the plan's own merge gate and the user has
the hardware in hand. First: 1) S2 (verify `0x20` on Cyrex/Rampage), 2) S1 (gate the
toast on an actual write), 3) S3 (confirm the SYSTEM highlight survives reopen; swap to
the Button pattern if not).
