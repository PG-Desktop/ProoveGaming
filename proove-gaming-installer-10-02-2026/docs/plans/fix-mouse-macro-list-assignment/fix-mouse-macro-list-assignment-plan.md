# Fix: restore macro assignment from the Macros-page list on mice

## Goal

On mouse devices, clicking a macro in the Macros-page list must again assign that macro to the
currently-selected button hotspot — the behaviour the product shipped with
([`add-macro-button-assignment`](../add-macro-button-assignment/add-macro-button-assignment-plan.md))
and lost on 2026-08-14 in PR #104 (`e699d36`,
[`remove-macro-list-selection`](../remove-macro-list-selection/remove-macro-list-selection-plan.md)).
That change was requested for keyboards, but `MacrosSectionView` is shared by both device
types, so mice lost their only Macros-page assignment gesture as collateral. The plan's own
open question 1 ("is this accepted for mice?") and the review's residual risk ("a mouse user
following the old flow finds clicking a macro row does nothing") were never confirmed by the
product — and QA has now reported exactly that risk as a bug (2026-08-31, BLAZE). Keyboards
keep their current, deliberately-designed flow: inspection rows + assignment in the Key
Assignment Dialog.

The entire ViewModel path is intact and verified end-to-end on current `main`
(`AssignMacroToSelectedButtonCommand` → `SetButtonBinding` → `StageChangeAndApply` → debounced
`ApplyDeviceConfigurationAsync` with the macro riding inline). Only the XAML gesture is gone,
so this is a view-layer restore, not new plumbing.

## Scope

**In:**

- `MacrosSectionView` macro rows become device-type-aware:
  - **Mouse** rows restore the pre-#104 semantics: radio mirrors "bound to the selected
    button" (`IsChecked="{Binding IsSelected, Mode=TwoWay}"`), click assigns
    (`Command="{Binding AssignCommand}"`), and the in-row unassign ✕ returns
    (visible on `IsSelected`, gated by `CanResetSelectedMacroTarget`, invoking
    `ResetSelectedMacroTargetCommand` — which already dispatches to `ResetSelectedButton()`
    for mice).
  - **Keyboard** rows keep today's inspection semantics unchanged
    (`IsInspected` / `InspectCommand`, group `MacrosInspect`).
- Extract the kebab (3-dot Edit/Remove) menu, currently inlined in the row template, into a
  shared piece used by both row variants — per the "second copy is the trigger" rule in
  `CLAUDE.md`. Its `OnMacroMenuItemClick` flyout-closing handler moves with it.
- A structural regression test that pins the mouse row's assign bindings in the AXAML (the
  reproducing test — it fails on current `main`).
- An end-to-end VM test that drives the production open chain (`SetDeviceAsync` → create
  macro → assign → debounced device push carries the `MacroDefinition`), because the existing
  macro-assignment tests hand-stamp the VM and bypass the open chain.
- Dated supersession notes in the three affected plans (see File-level change list).

**Out:**

- Keyboard flows: the Key Assignment Dialog, keyboard inspection rows, the hover tooltip and
  its ✕ — all unchanged.
- Mouse *inspection* highlight from the list (checking a row to light up its bound hotspots).
  On mice the radio goes back to meaning "bound to the selected button"; the always-visible
  hotspot chips already name every bound macro on the Macros page, so no information is lost.
  The `InspectMacro` code stays (keyboards use it).
- Any change to the assignment persistence or device-write path (`SetButtonBinding`,
  `StageChangeAndApply`, `ApplyDeviceConfigurationAsync`) — verified working, untouched.
- The Buttons-page Macros group (the other mouse assignment path) — unchanged.
- Localization: no new user-facing strings (the ✕ is icon-only; the shared header already
  exists in both languages).

## Options considered

1. **Device-type-switched rows in the template (recommended).** The item template carries two
   `RadioButton` variants — the restored mouse assign row and the current keyboard inspect
   row — with visibility switched on `IsMouseDevice` / `IsKeyboardDevice` (composed with the
   search filter's `IsVisible` via a `BoolConverters.And` MultiBinding, the pattern
   `DeviceDetailsView.axaml` already uses). Distinct `GroupName`s (`Macros` vs
   `MacrosInspect`) keep the hidden variant out of the visible group's coordinator.
   *Trade-off:* two radios per row in the tree (the hidden one is collapsed and its flyout is
   only realized on open, so the cost is negligible); the row content must be shared to avoid
   duplicating the kebab menu.

2. **VM-routed single row.** Keep one `RadioButton`; add `MacroViewModel.RowCommand` /
   `IsRowChecked` that `WireMacro` and the refresh paths point at assign-or-inspect depending
   on device type. *Trade-off:* touches `MacroViewModel`, `WireMacro`,
   `RefreshMacroListSelection` and the inspection paths for what is purely a view-wiring bug;
   a TwoWay checked property with per-device meaning is exactly the ambiguity #104 split
   `IsSelected`/`IsInspected` to remove. Rejected.

3. **Full revert of #104's row change for both device types.** Literally "як раніше", but it
   would also undo the keyboard inspection rows, which were a separate, explicitly-requested
   product change ([`add-macro-inspection-highlight`](../add-macro-inspection-highlight/add-macro-inspection-highlight-plan.md)).
   Rejected.

## Recommended approach

Option 1. All required building blocks already exist and are verified:

- `MacroViewModel.IsSelected` is still maintained for mice by `RefreshMacroListSelection`
  (mirrors `_buttonMacros` of the selected hotspot) — the radio's checked state comes back
  for free, including reflecting the current binding when the user switches hotspots.
- `AssignCommand` is still wired on every macro by `WireMacro`
  (`DeviceDetailsViewModel.Buttons.cs`), and `AssignMacroToSelectedButton` still handles the
  no-hotspot / read-only / keyboard early-outs.
- The reset dispatcher pair (`CanResetSelectedMacroTarget`, `ResetSelectedMacroTargetCommand`)
  routes to the mouse's `ResetSelectedButton()` — the same wiring the shared header uses.
- The ✕ chrome is the shared `Button.macroUnassign` style in `App.axaml` (including its
  `:disabled` re-states, so the PG-629 `bluetoothLocked` dimming composes correctly).
- The Key Assignment Dialog's MACROS tab (`KeyAssignmentDialog.axaml:237`) is the exact
  markup precedent for the restored row, `#Root.DataContext` bindings included.

Concrete XAML shape (mouse variant; keyboard variant is today's row unchanged):

```xml
<RadioButton Classes="macroItemRow" GroupName="Macros"
             IsChecked="{Binding IsSelected, Mode=TwoWay}"
             Command="{Binding AssignCommand}" CommandParameter="{Binding}">
  <RadioButton.IsVisible>
    <MultiBinding Converter="{x:Static BoolConverters.And}">
      <Binding Path="IsVisible" />
      <Binding Path="#Root.DataContext.IsMouseDevice" FallbackValue="False" />
    </MultiBinding>
  </RadioButton.IsVisible>
  <!-- shared row content: radio visuals, name, ✕ (mouse only), kebab -->
</RadioButton>
```

`x:Name="Root"` must be re-added to the `UserControl` (PR #104's review removed it as
unreferenced; the ✕'s `#Root.DataContext.…` bindings need it back — same pattern as
`KeyAssignmentDialog.axaml`). The device type cannot change while a macro library is loaded
(the library reloads on every device open), so visibility switched at bind time is safe.

## File-level change list

Ordered: reproducing test first (fails on current `main`), fix second, docs last.

| File | Action | Intent |
| --- | --- | --- |
| `tests/ProoveGaming.App.Tests/MacrosSectionRowBindingTests.cs` | create | **Reproducing test.** Structural pins over the AXAML text (same technique as `BluetoothModeBannerResourcesTests` / `LanguageResourceParityTests`): (a) the mouse row binds `AssignCommand` and `IsSelected, Mode=TwoWay` — fails on current `main`; (b) the keyboard inspect row (`InspectCommand` / `IsInspected` / `MacrosInspect`) is still present; (c) the mouse row's ✕ binds `ResetSelectedMacroTargetCommand` and `CanResetSelectedMacroTarget`. |
| `tests/ProoveGaming.App.Tests/ProoveGaming.App.Tests.csproj` | edit | Copy `MacrosSectionView.axaml` to the test output (`<None Include=…>` item, mirroring the existing resource-file items) so the structural test can read it. |
| `tests/ProoveGaming.App.Tests/DeviceDetailsMacroAssignmentTests.cs` | edit | Add the production-chain E2E case: `await SetDeviceAsync(mouse card)` (default preset seeds + selects), create a macro via `NewMacroName` + `SaveCreateMacroCommand`, select a hotspot, execute `AssignMacroToSelectedButtonCommand`, then await the debounced apply and assert `ApplyDeviceConfigurationAsync` received the binding with a non-null `MacroDefinition`. Existing tests in this file hand-stamp the VM and never cross `SetDeviceAsync` or the debounce. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/MacrosSectionView.axaml` | edit | The fix. Re-add `x:Name="Root"`; replace the single inspect row with the two device-type-switched variants; restore the mouse ✕ column (4-column grid, `macroUnassign`, visible on `IsSelected`, enabled on `CanResetSelectedMacroTarget`); reference the extracted kebab in both variants; update the row comment block to name both semantics and this plan. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/MacroRowKebabView.axaml` (+ `.axaml.cs`) | create | The kebab menu (3-dot button + Edit/Remove flyout) extracted from the row template, `x:DataType="viewmodels:MacroViewModel"`, used by both row variants; hosts the flyout-closing click handler moved from `MacrosSectionView.axaml.cs`. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/MacrosSectionView.axaml.cs` | edit | Remove `OnMacroMenuItemClick` (moves to the kebab view). |
| `docs/plans/remove-macro-list-selection/remove-macro-list-selection-plan.md` | edit | Dated note: open question 1 answered **no** by QA report 2026-08-31 — the mouse Macros-page assignment is restored by this plan; rows stay assign-free on keyboards only. Review journal untouched. |
| `docs/plans/add-macro-inspection-highlight/add-macro-inspection-highlight-plan.md` | edit | Dated note: inspection rows are now keyboard-only; on mice the radio reverted to assignment semantics (hotspot chips already show bindings there). |
| `docs/plans/add-macro-button-assignment/add-macro-button-assignment-plan.md` | edit | Dated note under the 2026-08-14 supersession banner: mouse behaviour restored by this plan; the banner's "does not match the shipped UI" caveat no longer applies to mice. |

No new NuGet packages, no `.slnx` or `Directory.Build.props` changes, no installer impact,
no gRPC contract change, no new localization keys.

## Acceptance criteria

- [ ] Given a connected mouse with button remap support (e.g. BLAZE) and a saved macro, when
  the user selects an assignable hotspot on the **Macros** page and clicks the macro's row,
  then the row's radio stays checked, the hotspot shows the macro-name chip, and
  `button_bindings` holds `(button, action=Macro, macro_id)` for that button.
- [ ] Given that assignment, within ~1 s (the 250 ms debounce plus write time) the device
  receives an `ApplyDeviceConfiguration` write whose binding for that button carries the
  inline `MacroDefinition` (observable in tests via the fake HID service).
- [ ] Given a macro bound to the selected hotspot, when the user clicks the ✕ on that macro's
  row, then the button reverts to its factory action, the chip disappears, and the row
  unchecks.
- [ ] Given the Macros page with **no** hotspot selected, when the user clicks a macro row,
  then nothing is assigned and no row stays checked (the existing
  `Assign_WithNoHotspotSelected_IsNoOp` contract).
- [ ] Given the user switches the selected hotspot, then each row's checked state re-mirrors
  the newly-selected button's binding (checked only on the row whose macro is bound to it).
- [ ] Given a **keyboard** (e.g. Raster) on the Macros page, when the user clicks a macro row,
  then it inspects (highlights bound keys) and assigns nothing — behaviour identical to
  current `main`; `MacrosSectionRowBindingTests` pins both variants structurally.
- [ ] Given a mouse whose live transport is Bluetooth (`HasBluetooth` true), the macro list
  stays disabled/dimmed exactly as PG-629 shipped it — the restored bindings add no way to
  write to a read-only transport (`AssignMacroToSelectedButton`'s `IsDeviceConfigurable`
  early-out also still guards it).
- [ ] `dotnet test ./src/ProveGaming.slnx -c Release` passes, including the previously-failing
  structural test and the new E2E test.

## Test strategy

Kind: **bug fix (`fix-*`)** — the reproducing test lands first and fails on current `main`.

- `tests/ProoveGaming.App.Tests/MacrosSectionRowBindingTests.cs` (new, reproducing):
  structural regex pins over `MacrosSectionView.axaml` for the mouse assign bindings, the ✕
  wiring, and the keyboard inspect bindings. This is the only automated way to pin the
  *gesture*: the repo has no headless pointer/XAML-interaction harness (declared repeatedly in
  prior plans), and this exact technique is already used by
  `BluetoothModeBannerResourcesTests` and `LanguageResourceParityTests`.
- `tests/ProoveGaming.App.Tests/DeviceDetailsMacroAssignmentTests.cs` (extend): the
  production-chain E2E described in the change list. During the investigation this chain was
  driven manually and passes on `main` at the VM level — adding it keeps the whole
  open→create→assign→push path pinned so the next view-layer regression is at least bounded
  to XAML.
- Existing coverage that must stay green: all of `DeviceDetailsMacroAssignmentTests`
  (assign / unassign / reset / badges / list-selection mirroring — these exercise the same VM
  members the restored bindings call) and `DeviceDetailsKeyboardMacroBindingTests` (keyboard
  side unaffected).
- Deliberately **not** covered by automated tests: actual pointer hit-testing, radio-group
  visual behaviour, flyout open/close, and real HID writes — no headless UI harness / no
  hardware in CI; covered by the QA checklist below.

## QA (manual test steps)

1. Connect a PROOVE BLAZE mouse over USB and launch the app. Open the BLAZE card →
   **Macros** section.
2. Click **+ NEW MACRO**, record a short macro (e.g. two key presses), name it "Test1",
   save it. Expected: "Test1" appears in the macro list.
3. On the mouse image, click the **Right Click** button hotspot. Expected: the
   selected-button header shows "Right Click" at the top of the list.
4. Click the "Test1" row in the macro list. Expected: the row's radio becomes checked and
   stays checked; the Right Click hotspot on the image now shows "Test1" on a second line
   with a small ✕.
5. Press the mouse's right button in a text editor. Expected: the recorded macro plays
   instead of a right click.
6. Click another hotspot (e.g. Scroll Click). Expected: the "Test1" row unchecks (it is not
   bound to this button). Click back on Right Click. Expected: "Test1" re-checks.
7. With Right Click selected, click the ✕ on the "Test1" row (or on the hotspot chip).
   Expected: the chip disappears, the row unchecks, and the physical right button works as a
   normal right click again.
8. Restart the app, reopen BLAZE → Macros. Expected: the state from step 7 is preserved
   (macro in the list, no binding).
9. Repeat step 4, then restart the app. Expected: the Right Click hotspot still shows
   "Test1" and the physical button still plays the macro.
10. Connect a supported keyboard (e.g. Raster), open its Macros section, click a macro row.
    Expected: no assignment happens — the row highlights the keys the macro is bound to on
    the keyboard image (same as the current build); assignment still works via clicking a
    key and using the Key Assignment Dialog.
11. If a Bluetooth-capable mouse (e.g. TAIPAN) is available, connect it via Bluetooth and
    open its Macros page. Expected: the section shows the "Bluetooth mode" banner and the
    macro list is dimmed/inactive — clicking rows does nothing (unchanged from the current
    build).

## Review focus

1. `null-safety` — the ✕ and visibility bindings go through untyped `#Root.DataContext.…`
   paths (compiled-bindings escape hatch); verify `FallbackValue`s cover the null-DataContext
   window during navigation.
2. `UI-thread affinity` — the restored bindings call the same synchronous VM commands the
   Buttons page uses; confirm nothing new blocks the dispatcher (the device write stays on
   the debounced path).
3. `localization` — no new strings; verify the restored row adds no fixed `Width` and the uk
   strings still fit (row name column is star-sized).

## Performance & quality

No hot paths. The item template now instantiates two radios per macro row; the inactive
variant is `IsVisible=false` (collapsed — not measured/arranged) and its kebab flyout content
is only realized on open, so the per-row cost is one extra collapsed control. Macro libraries
are small (device caps ~dozens). No new allocations on any per-frame or per-keystroke path;
no shared-state or threading changes — all restored commands run on the UI thread exactly as
the Buttons-page path does.

## Risks

- **Radio-group cross-talk.** Two `RadioButton`s per row; if both variants shared one
  `GroupName`, hidden radios would still register with the group coordinator. Mitigated by
  distinct groups (`Macros` vs `MacrosInspect`) — worth a reviewer glance all the same.
- **Compiled bindings.** The file sets `x:DataType`; `#Root.DataContext.…` bindings must
  compile (precedent: `KeyAssignmentDialog.axaml` does exactly this today). If the compiler
  objects, the fallback is `x:CompileBindings="False"` on the specific binding, not the file.
- **Kebab extraction drift.** Moving the flyout into `MacroRowKebabView` must not change the
  flyout-closing behaviour (`OnMacroMenuItemClick`); QA step 2's edit/remove flows and the
  existing kebab styling cover it.
- **PG-629 interplay.** The row sits inside the `bluetoothLocked` + `IsEnabled`-gated subtree;
  the ✕ uses `macroUnassign:disabled` re-states, which PR #124 M1 already tuned — verify the
  dimmed look on a Bluetooth mouse (QA step 11) rather than restyling.

## Open questions

1. **Mouse inspection is dropped from the list** (the radio reverts to "bound to the selected
   button"). The always-visible hotspot chips already answer "where is this macro bound?" on
   mice, so nothing is lost functionally — but if design wants both semantics on mice, that
   is a different (larger) UX design task and should be a follow-up plan, not this fix.
   Proceeding on the assumption that restoring the old behaviour is the product decision
   (per the request "зробити щоб працювало як раніше").
2. None otherwise — the reset/✕ gating, persistence and device-write contracts are reused
   as-is.

## Review findings

### Round 1 — Ship: yes

#### Summary
The working-tree delta (all uncommitted, on `main`) restores the mouse Macros-page
assignment gesture removed by PR #104: `MacrosSectionView` now carries two device-type
row variants (mouse assign / keyboard inspect), the kebab flyout is extracted into a shared
`MacroRowKebabView`, and coverage lands as a structural AXAML pin (reproduced failing
pre-fix) plus a production-chain E2E through `SetDeviceAsync`. Three predecessor plans got
dated supersession notes. Reviewed: 7 modified + 4 untracked files — the full
`git status --porcelain` inventory; nothing out of scope.

#### Plan conformance
Plan: `docs/plans/fix-mouse-macro-list-assignment/fix-mouse-macro-list-assignment-plan.md`
- Implemented as planned: all nine File-level change list items, in the planned order
  (reproducing test first — confirmed failing 2/3 before the fix, green after). Mouse row
  restores `IsSelected`/`AssignCommand` + in-row ✕ on the shared reset dispatcher; keyboard
  row byte-for-byte keeps inspection semantics; distinct `GroupName`s as planned.
- Deviates from plan: none of substance. The E2E's wait deadline is 5 s where the plan's
  acceptance criterion says "~1 s" — the criterion describes expected latency, the test
  allows CI headroom; consistent.
- Missing / partial: the acceptance criterion "`dotnet test ./src/ProveGaming.slnx -c
  Release` passes" was run as App.Tests only (Debug + Release, 1460/1460 both).
  Infrastructure/Installer tests are untouched by this change; the full-slnx Release run is
  left to CI / pre-push (see Nits).
- Accepted limitation (per plan): pointer hit-testing, radio-group visuals, flyout
  behaviour and the runtime resolution of the new bindings are not automatable (no headless
  UI harness) — residual risk is covered by QA steps 1–11 and mitigated by exact in-tree
  precedent: `DeviceImageWithHotspotsView` already resolves `{Binding #Root.DataContext.…}`
  inside an ItemsControl DataTemplate in the shipped product, which is the same name-scope
  shape the new rows use (element-form `<Binding Path="#Root.…">` is the same `Binding`
  object with the same path grammar).

#### Code
- none (no Blockers). Checked specifically:
  - **Neither-mouse-nor-keyboard device** would hide BOTH row variants (empty-looking
    list). Not constructible today: every `SupportsMacros` catalog entry resolves a
    "…Mouse"/"…Keyboard" `DeviceType` (Rampage/T-Racer carry it per transport), and cards
    restored from storage persist `DeviceTypeLabel`; devices without the label also lack
    `SupportsMacros`, so the Macros section is unreachable. Recorded as a nit, not a bug.
  - **Radio-group interplay**: hidden keyboard rows on a mouse (and vice versa) sit in
    distinct groups (`Macros` / `MacrosInspect`); the TwoWay `IsSelected` write-backs the
    hidden group coordinator can produce are exactly the values
    `RefreshMacroListSelection` assigns anyway — no divergent state. Same coupling as the
    pre-#104 arrangement with the dialog's `KeyDialogMacros` group.
  - **✕ race gate**: `IsEnabled` reuses `CanResetSelectedMacroTarget`, preserving the
    PR #81 M1 in-flight-write guard; `ResetSelectedMacroTargetCommand` dispatches to
    `ResetSelectedButton()` for mice — verified in
    `DeviceDetailsViewModel.KeyAssignments.cs:359`.
  - **Kebab extraction**: `MacroRowKebabView` inherits the row's `MacroViewModel`
    DataContext, keeps the deferred-close `OnMacroMenuItemClick` verbatim, and
    `MacrosSectionView.axaml.cs` shrank to the constructor — no dangling subscriptions,
    no behaviour change.
  - **PG-629 interplay**: rows remain inside the `bluetoothLocked` + `IsEnabled`-gated
    subtree; `macroUnassign:disabled` re-states already exist in `App.axaml` (PR #124 M1),
    so the dimmed look composes; the VM's `IsDeviceConfigurable` early-out still guards a
    read-only transport even if the gate ever failed.
  - **Build/warnings**: scoped App build clean; every warning in the log pre-exists
    (HomeViewModel CS8604/CS8620, AddDeviceCardViewModel CS0067, MainWindow AVLN3001).
- [x] **S1. Un-scoped ✕ pin in the structural test** —
  *(fixed 2026-09-01, PR #125 m2: `FullRowWith` tempered regex captures the whole
  `<RadioButton>…</RadioButton>` mouse row and the three ✕ asserts run against `row.Value`.)*
  `tests/ProoveGaming.App.Tests/MacrosSectionRowBindingTests.cs:46` — Should-fix
  **Problem / why it matters:** `MouseRow_CarriesTheUnassignCross_WiredToTheSharedResetDispatcher`
  asserts `ResetSelectedMacroTargetCommand` / `CanResetSelectedMacroTarget` /
  `Classes="macroUnassign"` anywhere in the file, while the sibling tests deliberately scope
  their assertions to the owning `<RadioButton>` via `RowWith` (the comment above `RowWith`
  states why). If a future edit moved the ✕ into the keyboard row (or outside any row), this
  test would still pass — the pin does not pin the thing its name claims.
  **Fix:** run the three `Assert.Contains` against
  `RowWith(text, @"Command=""{Binding AssignCommand}""")`-style row scope — e.g. capture the
  whole mouse `<RadioButton …>…</RadioButton>` region (extend `RowWith` to a tempered match
  up to the closing tag) and assert within it. Test-only change, no production impact.

#### Tests
- [x] none required beyond S1 (closed 2026-09-01) — the reproducing structural test failed pre-fix exactly as
  the plan demanded (2/3 red) and passes post-fix; the new E2E crosses `SetDeviceAsync`,
  the default-preset gate, the dialog-flow macro creation and the 250 ms debounce, and its
  poll-then-assert is race-safe (`CurrentBindingSet` reads the dictionaries at push time,
  so any late apply still carries the macro). Affected suites 90/90; full
  `ProoveGaming.App.Tests` 1460/1460 in Debug and Release. CI runs the full slnx suite in
  Release on push, which closes the remaining acceptance-criterion gap.

#### Nits
- Both row variants hidden for a hypothetical `SupportsMacros` device whose
  `DeviceTypeLabel` names neither "mouse" nor "keyboard" — unreachable with today's
  catalog (see Code above); worth a comment or a keyboard-row fallback only if such a
  device type ever ships — `src/ProoveGaming.App/Views/DeviceDetails/Sections/MacrosSectionView.axaml:232`.
- Full-slnx Release test run not executed locally (App.Tests only, both configurations);
  Infrastructure/Installer are untouched, CI covers it — run it once before pushing if the
  branch will bypass CI.
- The ✕ `IsEnabled` binding carries no `FallbackValue`; during the null-`DataContext`
  navigation window the button defaults to enabled — but it is only visible when
  `IsSelected` is true, which requires a live DataContext, so the window is unreachable.
  Matches the dialog's existing ✕ wiring — noting for symmetry only.

#### What's done well
- The reproducing test was written first and demonstrably failed (2/3 red) before the fix —
  the plan's fix-* discipline followed to the letter, and the row-scoped `RowWith` regex
  (S1 aside) pins bindings to the *owning* radio rather than the file as a bag of strings.
- The E2E test closes exactly the gap the investigation exposed: every prior test
  hand-stamped the VM, so a view-level regression was invisible; open→create→assign→push
  is now pinned end-to-end including the preset gate that silently swallows device writes.
- The kebab extraction honours the "second copy is the trigger" rule instead of duplicating
  an ~55-line flyout per variant, and the moved click handler kept its full rationale
  comment.
- The three predecessor plans got precise dated notes (journals untouched), so the paper
  trail that made this regression diagnosable stays coherent for the next reader.

#### Verdict
Ship: yes — no blockers; S1 is a five-minute test-quality follow-up that does not affect
the shipped behaviour. First: (1) close S1 by row-scoping the ✕ assertions, (2) run the
plan's QA steps 1–9 on a real BLAZE (and 10–11 on a keyboard / Bluetooth mouse if
available) since pointer gestures are QA-only by declared strategy, (3) commit on a branch
— everything is currently uncommitted on `main`.

### Round 2 (branch `bugfix/PG-642`, commit 0b17d1a "PG-642") — Ship: yes

The commit is byte-identical to the working tree Round 1 reviewed — no re-review needed.
Round 1's **S1** (row-scoping the ✕ assertions in
`MacrosSectionRowBindingTests.MouseRow_CarriesTheUnassignCross_WiredToTheSharedResetDispatcher`)
was **not** addressed before committing and remains open as a follow-up. QA steps 1–9 of
this plan are still the pre-merge gate for the pointer-gesture half of the fix.
