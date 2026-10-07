# Add macro-to-button assignment on the Macros page

> **2026-08-14 — Partially superseded** by
> [`remove-macro-list-selection`](../remove-macro-list-selection/remove-macro-list-selection-plan.md):
> the Macros page's macro list stopped assigning on click — assignment happens in the Key
> Assignment Dialog (keyboards) or the Buttons section's Macros group (mice), and the in-row
> unassign ✕ is gone. The VM plumbing this plan added (`MacroViewModel.AssignCommand`,
> `IsSelected`, `RefreshMacroListSelection`, `RefreshHotspotMacroBadges`) still exists and now
> feeds the dialog's MACROS tab and the mouse hotspot chips. The rows' radio later returned via
> [`add-macro-inspection-highlight`](../add-macro-inspection-highlight/add-macro-inspection-highlight-plan.md)
> with **inspection** semantics (checking a macro highlights its bound keys/buttons) — so the
> acceptance criteria and QA steps below that describe assigning by clicking macro **list
> rows** still do not match the shipped UI.

> **2026-08-31 — Mouse behaviour restored.**
> [`fix-mouse-macro-list-assignment`](../fix-mouse-macro-list-assignment/fix-mouse-macro-list-assignment-plan.md)
> brought the assign-on-click row (and its in-row ✕) back for **mouse** devices after QA
> reported the lost gesture, so this plan's acceptance criteria and QA steps describe the
> shipped mouse UI again. The 2026-08-14 caveat above now applies to keyboards only, whose
> rows inspect and whose assignment lives in the Key Assignment Dialog.

## Goal

Let a user bind a saved macro to a physical mouse button directly from the **Macros**
page. Today the Macros page only creates / edits / deletes macros; there is no way to put
a macro on a button without going to the **Buttons** page. This change gives the Macros
page the same device-image-with-hotspots interaction the Buttons page has (the buttons and
connector lines with coordinates), but scoped to macro work only: the user clicks a button
on the image to select it, then clicks a macro in the list to assign it; the assigned macro
shows as a second line inside that button's hotspot with an **✕** to unassign (revert to the
button's factory default). A shared "selected button" header with a **Reset** action is
added to both the Buttons and Macros pages so the currently-selected button can be reset to
default from either place. The assignment reuses the existing button-binding store and
device-write path — a macro assigned here is identical to a `Macro` action assigned on the
Buttons page.

## Scope

**In:**
- Macros page (mouse devices) shows the device image with the same `PartHotspots`
  connector-line hotspots as the Buttons page.
- Selecting a hotspot on the Macros page + clicking a macro row assigns that macro to the
  selected button (persisted + written to the device).
- Each Macros-page hotspot whose button is bound to a macro shows the macro name on a second
  line inside the hotspot, with an **✕** button that unassigns it (reverts that button to its
  factory default). Hotspots bound to a non-macro action (e.g. Right Click) show **only** the
  button label on the Macros page — no assignment sub-line.
- A shared selected-button header row (button title on the left, **Reset** button on the
  right) added to the top of **both** the Buttons and Macros section panels. Reset reverts the
  **currently-selected** button to its factory default.
- Macro list radio state reflects which macro (if any) is currently bound to the selected
  hotspot; switching hotspots updates it.
- Localization keys (en/uk) for the new Reset label and any new hint text.
- Unit tests for the new assign / unassign / reset / badge-refresh VM logic.

**Out:**
- Keyboard devices. Keyboards have no `PartHotspots` (see `ApplyDefaultPartHotspots`, which
  returns early for non-mice); their Macros page is unchanged (list only). Assigning macros
  to keyboard keys is a separate key-assignment flow and is not in scope.
- Any change to how macros are created / edited / deleted, or to the Buttons-page action
  catalog.
- The Commands tab on the Macros page (currently hidden) — untouched.
- New device-write protocol: assignment uses the existing
  `IUsbHidDeviceService.SetButtonBindingsAsync` path unchanged.

## Options considered

### Option 1 — Extract a shared device-image-with-hotspots view; reuse it for Macros (recommended)

The Buttons panel (`DeviceDetailsView.axaml` ~469–556) and the non-Buttons device-image panel
(~647–679) already render a near-identical `Grid Width="1236"` block (animated glow + device
`Svg`/`Image`). The Buttons one additionally overlays the `PartHotspots` `ItemsControl`
(connector line + target marker + hotspot button). Extract that inner block — glow + image +
hotspot overlay — into one reusable control and host it in **both** panels. The hotspot
overlay is shown when the active section is **Buttons or Macros**; the per-hotspot macro
sub-line is shown only in **Macros** mode.

- **Pros:** removes an existing large duplication; guarantees the Macros hotspots align to the
  image identically to the Buttons page (same canvas construction, same `SetCanvasWidth` path —
  the width itself is per-page since 2026-10-01); one hotspot template to maintain (repo rule: extract on the second
  copy). Columns already match — non-Buttons sections already use content col 1 + image col 2,
  so no column restructor is needed; Macros just gains the overlay on col 2.
- **Cons:** touches the shared `DeviceImageWithHotspotsView` used by every non-Buttons section,
  so a regression there is visible everywhere — must verify Presets/RGB/DPI/Settings/Firmware
  still render the image unchanged.

### Option 2 — Duplicate the hotspot overlay into the Macros column-2 panel only

Leave the Buttons panel as-is and paste a second copy of the `PartHotspots` `ItemsControl`
(with the new macro sub-line) into the non-Buttons device-image panel, gated to
`IsMacrosSection`.

- **Pros:** smaller blast radius; the Buttons page markup is untouched.
- **Cons:** violates the repo's "don't duplicate UI — the trigger is the second copy" rule;
  two hotspot templates drift over time (the Buttons page single-line vs the Macros page
  two-line would diverge); re-derives the 1236 alignment context by hand, risking
  misalignment.

### Option 3 — Compact button picker on the Macros page (rejected)

A small list/row of assignable buttons instead of the device image. Rejected by the user in
favor of mirroring the Buttons-page layout.

**Decision:** Option 1. It matches the user's "mirror the Buttons page" choice, removes
duplication rather than adding it, and keeps a single hotspot template.

## Recommended approach

1. **Extract** the glow + device image + hotspot overlay into
   `Views/DeviceDetails/Sections/DeviceImageWithHotspotsView.axaml(.cs)`
   (a `UserControl` with `x:DataType="DeviceDetailsViewModel"`). It contains the animated-glow
   `Viewbox`, the device `Svg` + `Image`, and the `PartHotspots` `ItemsControl`. The overlay's
   `IsVisible` is bound to "Buttons or Macros section" (a new VM helper `AreHotspotsVisible`).
   Replace the inline blocks in both `DeviceDetailsView.axaml` panels with this control.
2. **Hotspot template** (inside the extracted view): keep the connector `Path`, target marker,
   and hotspot `Button`. Add a second row inside the hotspot button: `Label` (row 1) and, when
   `ShowAssignedMacro` is true, a row 2 with the macro name + an **✕** `Button`
   (`Command="{Binding #Root.DataContext.UnassignHotspotCommand}" CommandParameter="{Binding}"`).
   The whole macro row is `IsVisible="{Binding ShowAssignedMacro}"`.
3. **`DevicePartHotspotViewModel`** gains display state:
   - `AssignedMacroName : string?` and `ShowAssignedMacro : bool` (name present **and** the
     parent is in Macros mode). Raise `ButtonWidth`/`ButtonHeight`/geometry change when it
     flips.
   - When `ShowAssignedMacro`, `ButtonHeight` grows to fit two lines (24 + 24 + vertical
     padding ≈ 56) and `ButtonWidth` becomes `max(label, macroName + ✕)` + padding. The
     existing `ButtonY = ButtonPoint.Y - ButtonHeight/2` keeps the hotspot centered on its
     point, so the connector line stays anchored.
4. **`DeviceDetailsViewModel.Buttons.cs`** (assignment logic — reuses the existing store +
   device write):
   - `AssignMacroToSelectedButtonCommand(MacroViewModel)`: if `SelectedHotspot` maps to an
     assignable button and the device supports remap, set `_buttonAssignments[button]=Macro`,
     `_buttonMacros[button]=macro`, persist via `_localStorage.SetButtonBinding(..., (int)Macro,
     macro.Id)`, write via `SetButtonBindingsAsync(CurrentBindingSet())`, then
     `RefreshHotspotMacroBadges()` + `RefreshMacroListSelection()` + `NotifyPresetChanged()`.
     No-op (and re-sync selection) if no assignable hotspot is selected.
   - `ResetSelectedButtonCommand()`: reverts `SelectedHotspot`'s button to
     `DefaultActionFor(button)` (per-button; **not** all buttons).
   - `UnassignHotspotCommand(DevicePartHotspotViewModel)`: same per-button reset for the ✕ on a
     specific hotspot. Both delegate to a shared private `ResetButtonToDefault(MouseButton)`
     that clears `_buttonMacros`, sets the default action, persists, writes, and refreshes.
   - `RefreshHotspotMacroBadges()`: for each `PartHotspot`, set `AssignedMacroName` /
     `ShowAssignedMacro` from `_buttonMacros[HotspotIdToButton(id)]` when the action is `Macro`
     and `IsMacrosSection`; clear otherwise.
   - `RefreshMacroListSelection()`: set each `MacroViewModel.IsSelected` to
     `ReferenceEquals(m, _buttonMacros[selectedButton])` (or all false when no hotspot / no
     macro bound), so the list radio reflects the selected button's binding.
   - Wire `AssignCommand` onto each macro in `WireMacro` (alongside `EditCommand` /
     `RemoveCommand`).
   - Call `RefreshHotspotMacroBadges()` + `RefreshMacroListSelection()` from `SelectHotspot`,
     `ActiveSection` change (to/from Macros), and after assign / unassign / reset / macro
     delete / macro edit so the image and list stay in sync.
5. **`MacroViewModel`** gains `IRelayCommand<MacroViewModel>? AssignCommand`.
6. **Shared selected-button header**: extract
   `Views/DeviceDetails/Sections/SelectedButtonHeaderView.axaml(.cs)` — a row binding
   `SelectedHotspotFullLabel` (left) + a **Reset** `Button`
   (`Command="{Binding ResetSelectedButtonCommand}"`), the whole row visible only when a
   hotspot is selected and assignable (new VM helper `IsSelectedButtonResettable`). Place it at
   the top of `ButtonsSectionView` (replacing the current bare title `StackPanel`, lines
   160–167) and at the top of the Macros tab content in `MacrosSectionView`.
6a. **Reset button style — shared text-link (purple, underline on hover).** The Reset button
   must look like the installer's progress-step **Cancel** button
   (`InstallingStepView.axaml` ~46–66): transparent background, no border, no padding, `#6357FF`
   text, and `TextDecorations=Underline` on the inner `TextBlock` on `:pointerover` (the
   `ContentPresenter` stays transparent on hover). That style is currently **inline** in the
   installer, so per the repo rule ("extract on the second copy", "styles shared between App and
   Installer live in `ProoveGaming.Common`", "name by role not location", "keep the variant,
   share the rest"):
   - Add a role-named `Button.pgLinkButton` style to
     `src/ProoveGaming.Common/Styles/SharedActionButtonStyles.axaml` holding the shared bits
     (transparent bg, `BorderThickness=0`, `Padding=0`, `Foreground=#6357FF`,
     `:pointerover /template/ ContentPresenter#PART_ContentPresenter` → transparent,
     `:pointerover TextBlock` → `TextDecorations=Underline`). Leave font family / size to the
     call site (the installer uses `InstallerPixelFont` @18; the App Reset uses the App's font),
     since those are the per-site variant.
   - Include `SharedActionButtonStyles.axaml` in `src/ProoveGaming.App/App.axaml` (today only
     the Installer's `App.axaml` includes it) so the App can use `pgLinkButton`.
   - Apply `Classes="pgLinkButton"` to the Reset `Button` in `SelectedButtonHeaderView`.
   - Refactor `InstallingStepView.axaml` to apply `Classes="pgLinkButton"` and drop the inline
     `Button.Styles` copy (keep its font/size at the call site). **Installer touch — has
     release-process implications; the change is visual-parity only (no behavior change).**
7. **`MacrosSectionView`**: add the `SelectedButtonHeaderView` at the top; add
   `Command="{Binding AssignCommand}" CommandParameter="{Binding}"` to the `macroItemRow`
   `RadioButton` (its `IsChecked`/`IsSelected` now reflects the selected button's binding).
8. **Localization**: add `DeviceDetails_Buttons_Reset` (and any hint key) to `Strings.en.axaml`
   and `Strings.uk.axaml`. Reuse existing `DeviceDetails_Header_SelectMouseButton` where a
   "no button selected" prompt is needed.
9. **`docs/CONTEXT.md`**: add one-line entries for any new term introduced (e.g. "macro
   badge" / "hotspot macro assignment") if not already covered by existing hotspot / macro /
   button-binding entries.

## File-level change list

**Created**
- `src/ProoveGaming.App/Views/DeviceDetails/Sections/DeviceImageWithHotspotsView.axaml` — shared
  glow + device image + `PartHotspots` overlay (hotspot template with the optional macro
  sub-line + ✕). `.axaml.cs` — minimal code-behind (`InitializeComponent`).
- `src/ProoveGaming.App/Views/DeviceDetails/Sections/SelectedButtonHeaderView.axaml` — shared
  selected-button title + Reset row. `.axaml.cs` — minimal code-behind.
- `docs/plans/add-macro-button-assignment/add-macro-button-assignment-plan.md` — this plan.
- `tests/ProoveGaming.App.Tests/DeviceDetailsMacroAssignmentTests.cs` — VM tests for assign /
  unassign / reset / badge + list-selection refresh.

**Edited**
- `src/ProoveGaming.App/Views/DeviceDetails/DeviceDetailsView.axaml` — replace the two inline
  device-image blocks (Buttons panel + non-Buttons column-2 panel) with
  `DeviceImageWithHotspotsView`; the overlay shows for Buttons or Macros.
- `src/ProoveGaming.App/Views/DeviceDetails/Sections/ButtonsSectionView.axaml` — replace the
  bare selected-hotspot title with `SelectedButtonHeaderView`.
- `src/ProoveGaming.App/Views/DeviceDetails/Sections/MacrosSectionView.axaml` — add
  `SelectedButtonHeaderView` at top; add assign `Command` to the macro row.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DevicePartHotspotViewModel.cs` — add
  `AssignedMacroName`, `ShowAssignedMacro`, two-line `ButtonHeight`/`ButtonWidth` sizing.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Buttons.cs` — add
  `AssignMacroToSelectedButton`, `ResetSelectedButton`, `UnassignHotspot` commands +
  `ResetButtonToDefault`, `RefreshHotspotMacroBadges`, `RefreshMacroListSelection`; wire
  `AssignCommand` in `WireMacro`; call refreshers on delete/edit paths.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs` — add
  `AreHotspotsVisible`, `IsSelectedButtonResettable` helpers; call the refreshers from
  `SelectHotspot` and on `ActiveSection` change; raise the new props.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/ButtonActionGroupViewModel.cs` (contains
  `MacroViewModel`) — add `AssignCommand`.
- `src/ProoveGaming.App/Resources/Strings.en.axaml`, `Strings.uk.axaml` — new keys.
- `src/ProoveGaming.Common/Styles/SharedActionButtonStyles.axaml` — add the shared
  `Button.pgLinkButton` text-link style (purple, underline on hover).
- `src/ProoveGaming.App/App.axaml` — add the `StyleInclude` for
  `SharedActionButtonStyles.axaml` (currently only the Installer includes it).
- `src/ProoveGaming.Installer/Views/InstallingStepView.axaml` — apply `Classes="pgLinkButton"`
  to the Cancel button and remove its inline `Button.Styles` copy (visual parity, **installer
  touch — release-process implications**).
- `docs/CONTEXT.md` — glossary entries for any new term.

## Acceptance criteria

1. Given a connected mouse that supports button remap, when the user opens the **Macros**
   section, then the device image is shown on the right with the same clickable hotspot
   buttons + connector lines as the Buttons section.
2. Given the Macros section with no hotspot selected, when the user clicks a macro row, then
   nothing is assigned and no macro row stays visually checked.
3. Given the Macros section, when the user clicks an assignable hotspot and then clicks a macro
   row, then that button's binding becomes that macro: the store row for
   `(owner, vid, settingsPid, settingsDeviceKey, button)` records action `Macro` with the
   macro's id, and `SetButtonBindingsAsync` is invoked with a binding set whose entry for that
   button carries the macro definition.
4. Given a button bound to a macro, when the Macros section is shown, then that hotspot shows
   the macro name on a second line with an **✕**; a hotspot bound to a non-macro action shows
   only its label (no second line).
5. Given a hotspot bound to a macro, when the user clicks its **✕**, then that button reverts
   to its factory default (`DefaultActionFor`): the store records the default action with a null
   macro id, the device is re-written, and the second line disappears.
6. Given a selected assignable hotspot on either the Buttons or Macros page, when the user
   clicks **Reset** in the selected-button header, then that single button reverts to its
   factory default (other buttons unchanged) and the store + device reflect it.
6a. Given the selected-button header, the **Reset** button renders as a transparent text-link
   with `#6357FF` text and no border/background; on hover its text is underlined and the
   background stays transparent — matching the installer progress-step Cancel button. The
   installer Cancel button renders identically after being switched to the shared style.
7. Given a selected hotspot, when its bound macro is displayed, then exactly the matching macro
   row is checked in the list; switching to a hotspot bound to a different macro (or none)
   updates the checked row accordingly.
8. Given a macro that is bound to a button, when the user deletes that macro, then any button
   bound to it reverts to default and its hotspot second line is removed (existing
   `ConfirmDeleteMacro` behavior, extended to refresh the badges).
9. Given a keyboard device, when the Macros section is shown, then it renders as before (list
   only, no device-image hotspots) and no assignment UI appears.
10. Given the Presets/RGB/DPI/Settings/Firmware sections after the image extraction, when each
    is shown, then the device image renders exactly as before (no hotspots, unchanged
    position/scale).

## Test strategy

New feature (`add-*`): the following unit tests are **required**, in
`tests/ProoveGaming.App.Tests/DeviceDetailsMacroAssignmentTests.cs`, built like
`DeviceDetailsPresetsViewModelTests` (`DeviceDetailsViewModel` with an in-memory
`LocalStorageService("Data Source=:memory:")` + `FakeOwnerProvider`, a `SingleCardViewModel`
with `SupportsButtonRemap=true`, `SupportsMacros=true`, and one or more `Hotspots`).
`IUsbHidDeviceService` can be null (assert store state) or a fake capturing the last binding
set (assert the macro flows to the device).

- **Assign golden path:** select a hotspot, add a macro, call
  `AssignMacroToSelectedButtonCommand` → store row is `Macro` + macro id; `_buttonMacros` has
  it; the hotspot's `ShowAssignedMacro`/`AssignedMacroName` are set; the macro row `IsSelected`
  is true.
- **Assign with no hotspot selected:** command is a no-op; no store write; no macro row checked.
- **Unassign (✕):** after an assign, `UnassignHotspotCommand(hotspot)` → store row back to
  default action + null macro id; `ShowAssignedMacro` false.
- **Reset selected button:** `ResetSelectedButtonCommand` reverts only the selected button;
  a second, differently-bound button is untouched.
- **List selection tracks hotspot:** bind macro A to button 1 and macro B to button 2; selecting
  button 1 checks A, selecting button 2 checks B, selecting an unbound button checks none.
- **Badge gating by section:** `ShowAssignedMacro` is false when `ActiveSection != Macros`
  even though the binding exists (so the Buttons page hotspots stay single-line).
- **Delete bound macro:** extend/verify `ConfirmDeleteMacro` reverts the bound button and clears
  the badge.
- **Non-mouse:** a keyboard `SingleCardViewModel` has no hotspots; assign command is a safe
  no-op.

**Deliberately not automated:** the Avalonia visual layout (hotspot alignment to the image,
two-line button sizing, connector geometry, the Reset-row appearance) — verified via the QA
checklist. `SetButtonBindingsAsync` is fire-and-forget (`_ =`) as in existing code; tests assert
it was invoked, not its device-side effect.

## QA (manual test steps)

1. Launch the app and open a connected mouse that supports button remapping (e.g. the X43/BLAZE).
2. Open the **Macros** section. Expected: the device image appears on the right with the same
   clickable buttons and connector lines as the Buttons section.
3. Without selecting a button, click a macro in the list. Expected: nothing is assigned; no macro
   stays highlighted.
4. Create a macro if none exist (**+ NEW MACRO**), then click a button on the device image (e.g.
   the scroll-click button). Expected: that button highlights and its name appears in the header
   row at the top of the list, with a **Reset** button on the right.
5. Click a macro in the list. Expected: the macro is assigned — the selected button's hotspot now
   shows the macro's name on a second line with an **✕**, and that macro row is marked selected.
6. Click the **✕** on that button's hotspot. Expected: the macro is removed and the button returns
   to its normal function; the second line disappears.
7. Assign a macro again, then click **Reset** in the header row. Expected: that button returns to
   its default; other buttons are unaffected.
8. Assign macro A to one button and macro B to another. Click each button in turn. Expected: the
   list highlights A when the first button is selected and B when the second is — matching the
   button's current binding.
9. Open the **Buttons** section. Expected: the same header row with the button title and a
   **Reset** button appears when a button is selected; hotspots there show only the button label
   (no macro second line). The **Reset** button is purple text with no box; hovering underlines it.
9a. Run the installer to the installing/progress step. Expected: the **Cancel** button looks and
    behaves exactly as before (purple text, underline on hover).
10. Assign a macro to a button, then delete that macro from the list (kebab → Remove → confirm).
    Expected: the button reverts to its default and no longer shows the macro.
11. Open Presets, RGB, DPI, Settings, and Firmware in turn. Expected: the device image looks
    exactly as before, with no hotspots overlaid.
12. Repeat step 2 with a keyboard device. Expected: the Macros page shows only the macro list (no
    device image / hotspots / assignment UI).

Canvas sizing (added 2026-10-01 — the hotspot canvas follows the panel, see the dated note):

13. At window widths **1200**, **1440** and **>=1600**, on **both** a BLAZE and a TAIPAN, walk
    **Presets -> Buttons -> Macros without resizing in between**, then back. Expected: on every
    page every label is fully inside its panel, and the labels do not change size when you return
    to a page you have already seen. This order is the one that regressed: a panel shown again
    without a resize raises no SizeChanged, so the canvas kept the previous page's width.
14. On Macros, bind a macro with a long name (20+ characters) to **Side Forward**. Expected: the
    label grows leftwards but stays inside the panel, and it never covers the ring it points at —
    TAIPAN is the device to check, its side buttons sit closest to their labels.
15. Shrink the window to its minimum on the Buttons page with a hotspot selected (the details
    panel open). Expected: the device image and its labels stay inside the panel; the right-hand
    details panel does not push them out of it.

## Review focus

- `installer/uninstaller` — the installer Cancel button is refactored onto the shared
  `pgLinkButton` style; verify it looks/behaves identically (visual-parity only) and note the
  release-process implication in the PR.
- `localization` — new Reset/hint strings in both en and uk; no hard-coded user-facing text.
- `null-safety` — assignment guards on `SelectedHotspot`, `Device`, `_localStorage`,
  `_ownerProvider`, `_usbHidDeviceService` (all optional/nullable as in existing button code).
- `UI-thread affinity` — all VM/collection mutations on the UI thread (SQLite reads sync, as
  existing); device writes stay `async` fire-and-forget as today.
- `resource disposal` — no new subscriptions; if any, unsubscribed with the existing lifecycle.

## Performance & quality

No hot paths. Assignment/reset are user-driven, infrequent, and reuse the existing persist +
`SetButtonBindingsAsync` path (already async). `RefreshHotspotMacroBadges` /
`RefreshMacroListSelection` iterate the small `PartHotspots` (≤ a handful) and `Macros`
collections — trivial cost, no per-frame work, no new allocations of concern. Shared VM state
(`_buttonAssignments` / `_buttonMacros`) is touched only on the UI thread, as in the current
button code.

## Dated notes

- *2026-10-01 (PG-683 follow-up - the hotspot canvas outgrew its panel).* QA: on the Macros page a
  hotspot label rendered as "e Forward" with half the bound macro's name missing, and it survived
  resizing on a large screen. The canvas the labels are positioned on is 1236 wide by design, but
  the panel hosting it is the layout's `*` column - around 600 near the design window width. Pinned
  at 1236 and centred, it hung ~318px off each edge and the panel clipped whatever landed there.
  The longer the bound macro's name the worse it got, because `ButtonWidth` is measured from that
  name and a left-side label is laid out *leftwards* from its anchor.

  The machinery for this already existed and was dormant: `SetCanvasWidth`/`TransformX` re-map the
  hotspots into a narrower canvas, `DeviceDetailsView.OnContentPanelSizeChanged` reports the panel
  width - but the handler was never wired in XAML and `SyncHotspotCanvasWidth` discarded its
  argument in favour of `const double fixedWidth = 1236`. Now wired on both hosting panels, with
  `ResolveHotspotCanvasWidth` capping at the design width and treating a zero (a hidden section
  still raises SizeChanged) as "not laid out" rather than "no room".

  **Two things that had to be split to make it correct.** `TransformX` squeezed every x toward the
  centre, label anchors and target markers alike - but the artwork does not narrow with the canvas,
  it is a fixed `DeviceImageWidth` box that only re-centres. Enabling the transform as it stood
  pulled the side-button rings off the buttons they point at and onto the body of the mouse (caught
  on a live BLAZE, three-way screenshot comparison). Target points now only re-centre
  (`RecentreX`); only label anchors compress. And compressing the anchors is still not enough on
  its own, because the box grows with its content, so `ClampButtonX` slides a box that would
  overhang back onto the canvas - pinning it to the left edge if it is wider than the whole canvas,
  so it loses the trailing unassign x rather than the start of the text.

  Both rules are pure arithmetic and pinned in `LayoutRuleTests`.

- *2026-10-01 (PR #144 review - the first attempt had two holes).* Both confirmed, both mine.

  **The width was measured on the wrong element, and only on a resize.** It came from the hosting
  panels, which are not the width of the control inside them: Buttons spans two columns and insets
  its content by a margin that moves with the details panel. Worse, a panel shown again without the
  window being resized raises no `SizeChanged` at all - Avalonia skips arrange for a hidden control,
  so its `Bounds` never change. Presets -> Buttons -> Macros therefore left the canvas at the
  Buttons width and brought the clipping straight back, on the most ordinary path through the
  navigation. The note above claimed a hidden section still reports; it does not.
  `DeviceImageWithHotspotsView` now measures itself and re-reports whenever it becomes visible, so
  each page keeps its own width.

  **Squeezing the anchors walked labels onto their own markers.** The anchor compresses toward the
  centre while the marker only re-centres, so the design's gap between them shrinks with the canvas.
  TAIPAN draws its side buttons 40px from their labels against BLAZE's 104, so its anchors cross
  their markers at any canvas under ~923 - including the 808 the Macros panel gets at the DEFAULT
  1440 window. The label box then covers the ring it points at and the connector runs backwards;
  before this work TAIPAN was drawn correctly there. A live check on a BLAZE missed it because BLAZE
  still has room at 808. `ResolveAnchorX` now stops an anchor crossing its own marker.

  The tests that came with the first attempt all stayed green through both defects: they pinned the
  helpers in isolation and never the invariant. `ALabelNeverLandsOnTheMarkerItPointsAt` runs the
  real catalog coordinates for both mice through the view model at six canvas widths, and fails on
  the pre-fix code with the exact overlap (TAIPAN 199.4 against a marker at 150).

## Risks

- **Image-extraction regression:** the shared `DeviceImageWithHotspotsView` is used by every
  non-Buttons section; a layout mistake shows up across all of them. Mitigate with acceptance
  criterion 10 + QA step 11 (verify each section's image is unchanged) and by keeping the
  extracted markup byte-equivalent to the current Buttons-panel block.
- **Hotspot alignment drift:** the Macros hotspots must sit on the same canvas as the Buttons
  page. Reusing the identical extracted control (not a re-derived copy) is what guarantees this —
  the reason Option 1 beats Option 2. *Updated 2026-10-01:* that canvas is no longer a fixed 1236.
  It is sized to the room each page gives the control, which is NOT the same on the two pages —
  Buttons spans two columns, the rest get the `*` column alone — so the two now differ by design
  and each instance measures itself. The alignment that matters is hotspot-to-artwork within one
  canvas, and that is what `SetCanvasWidth` preserves.
- **Two-line hotspot geometry:** growing `ButtonHeight` when a macro is shown changes the
  connector anchor; verify the line still meets the button edge (the existing centered
  `ButtonY` formula should hold, but confirm visually).
- **Macro-row dual role:** the row radio now reflects "bound to selected button" and also drives
  assignment on click. Ensure `RefreshMacroListSelection` runs after every binding change so the
  radio never lies, and that a stray click with no hotspot selected re-syncs (doesn't stick).
- **Shared header on the Buttons page:** replacing the current bare title must not change the
  Buttons-page scroll-to-assigned behavior (`ScrollToAssignedRequested`) or spacing.
- **Installer style refactor:** moving the installer Cancel button onto the shared
  `pgLinkButton` must be pixel-parity — the shared style keeps color + underline-on-hover and the
  installer keeps its font/size at the call site. Since the installer ships as the single-file
  EXE, verify the progress-step Cancel visually and mention the touch in the PR (release path).

## Open questions

- None blocking. (Layout, assignment flow, and Reset scope were confirmed: mirror the Buttons
  layout; select-button-then-click-macro; per-button Reset on both pages.)

## Post-plan refinements (implemented after the initial build)

These were requested during review of the running app and implemented on top of the plan above.
Recorded here so the plan reflects what actually shipped.

### Selected-button header / Reset
- **Reset label style:** pixel font (`AppPixelFont`), uppercase, 18px — resource values uppercased
  (`RESET` / `СКИНУТИ`).
- **Reset disabled state:** the Reset button is **disabled and greyed to the HEX colour
  `#70698B`** when there is nothing to reset. Driven by a section-aware VM property
  `CanResetSelectedButton` (replaces the earlier `IsSelectedButtonAtDefault`):
  - **Buttons page:** enabled when the selected button's assignment is **any non-default** action.
  - **Macros page:** enabled **only when a macro is assigned** (the macros area only resets macro
    bindings).
  - Enablement is re-raised on hotspot select, section change, and every binding change — including
    `SelectButtonAction` (the Buttons catalog path), which previously left it stale.
  - The disabled colour/transparent-background are set on the template `ContentPresenter`
    (`Button.resetButton /template/ ContentPresenter#PART_ContentPresenter`) so the theme's default
    disabled *box* does not appear (it stays a text link, only the foreground greys).
- **Header title:** shows the button name only (`SelectedHotspotLabel`) — the `(Secondary Button)`
  style description was removed from both pages.

### Hotspot macro badge (device image)
- **Unassign control:** the ✕ is the `Close.svg` icon (16px) tinted **`#9894D3`**, with the same
  octagon **Back-button hover frame** as the macro recorder's delete step (`Fill="#3F339C"`,
  revealed on `:pointerover`). Row height / width grew (`MacroLineHeight` 60, `MacroRowExtraWidth`
  36) to fit the 28px frame.
- **Selected hotspot background:** changed to **`#2E2471`** (`DevicePartHotspotViewModel.ButtonBackgroundColor`).
- **Macro name truncation:** the assigned-macro name is capped at 12 chars with an ellipsis
  (`AssignedMacroDisplayName`, e.g. "Quick Unique Action 1" → "Quick Unique...") with a tooltip
  showing the full name; the hotspot sizes to the truncated text.
- **Two-line label alignment:** when a macro is shown, the button's label left-aligns to match the
  macro name (`LabelAlignment`); single-line hotspots keep their centred label.

### Macro recorder — recorded-steps summary row (related, same session)
Not part of the original assignment plan but implemented alongside it: in the Create/Edit Macro
modal, a summary row was added right after the Start/Stop recording button (16px above and below):
- Left: **"Strings: N"** (`RecordedStepsCountLabel`, N = captured step count), colour `#8584A4`,
  regular `BaseAppFont` 18px.
- Right: **CLEAR** — the shared `pgLinkButton` (enabled Reset style), clears all recorded steps
  (`ClearRecordedStepsCommand`). Row visible only when there are recorded steps.
- New resources: `Macros_RecordedCount`, `Macros_ClearRecording`.
- Files: `DeviceDetailsViewModel.MacroRecorder.cs`, `MainWindow.axaml`, `Strings.en/uk.axaml`.

## Review findings

_filled in by /reviewer_
