# Keyboard macro unassign (✕) — implementation plan

> **2026-08-14 — Partially superseded** by
> [`remove-macro-list-selection`](../remove-macro-list-selection/remove-macro-list-selection-plan.md):
> the Macros page's macro list is now display-only, so the list-row ✕ this plan added no longer
> ships there. The ✕ (and the in-flight `CanResetSelectedMacroTarget` gate this plan
> established) lives on in the Key Assignment Dialog's MACROS tab, the mouse hotspots and the
> key overlay's hover tooltip (macro-bound keys), and the shared header's Reset link is
> unchanged.

> **As-built plan.** This change was implemented directly from the user's request ("make it
> possible to cancel an already-assigned macro, like the mouse does — for all keyboards") and
> reviewed before this document was written; the plan records the design as shipped on
> `features/keyboard-macro-unassign` (PR #81) so the docs/plans convention holds and the review
> journal has its home. Sections below use the standard structure; nothing here is speculative.

Sibling work: the mouse-side unassign this mirrors was added with the Macros-page hotspots (see
`DeviceImageWithHotspotsView` and [`add-mouse-macros`](../add-mouse-macros/add-mouse-macros-plan.md));
the keyboard binding model this operates on is described in
[`add-preset-scoped-macro-bindings`](../add-preset-scoped-macro-bindings/add-preset-scoped-macro-bindings-plan.md).

## Goal

Let the user cancel a macro that is already bound to a keyboard key, directly and discoverably —
the way the mouse already allows it. On the mouse's Macros page every hotspot whose button plays a
macro shows the macro name plus an unassign (✕); keyboards only offered the header **Reset** link,
which requires selecting the key first and reads as a remap action rather than a macro one. The
result: users reported being unable to remove a set macro from a key at all.

## Scope

**In scope**

- An unassign (✕) affordance on the macro list row whose macro is bound to the currently selected
  key (keyboards) or button (mouse) — the row the radio mark already identifies.
- Reusing the existing reset dispatch (`ResetSelectedMacroTargetCommand`) so unassign semantics
  are identical to the header Reset on every device family.
- Extracting the ✕ chrome into a shared App-level style before it gains a second copy.
- Fixing the pre-existing radio-refresh gap on the immediate-write reset path.

**Out of scope**

- Per-key badges/✕ on the keyboard overlay itself — keycaps at overlay scale are too small for
  the mouse-style two-line badge, and the macro list row is where the binding is already shown.
- Any change to binding storage, resync, or preset scoping — the affordance rides entirely on
  existing commands and state.
- Rampage macros (firmware-blocked; `SupportsMacros: false` until the factory answers) — the ✕
  works there automatically once the flag returns.

## Options considered

### Option A — ✕ on the bound macro's list row, wired to the existing reset dispatcher *(shipped)*

The macro list's radio mark already mirrors "bound to the selected target" for both device types
(`RefreshMacroListSelection`), so the row is the natural anchor: show the same octagon-✕ the mouse
hotspot uses, visible when `MacroViewModel.IsSelected`, command = `ResetSelectedMacroTargetCommand`.

*Why it wins:* zero new commands or state — the unassign IS the reset, so keyboards (staged and
immediate families) and the mouse all behave identically by construction; the affordance also
appears for the mouse's list row, making the two pages uniform.

### Option B — mouse-style badge + ✕ on the keyboard overlay keys

Closest literal copy of the mouse UX (see the binding at a glance, ✕ in place).

*Rejected:* keycaps render tens of pixels wide inside the overlay's Viewbox — a name badge and a
28 px hit target do not fit without occluding neighbouring keys; 86 keys can hold many bindings at
once, so badges would collide where hotspot labels (few, with connector lines) do not.

### Option C — a dedicated "Unassign" entry in the row's kebab menu

*Rejected:* hides the action behind a menu the mouse page does not require, and adds a second
code path for the same semantics; the kebab is scoped to library operations (Edit / Delete).

## Recommended approach

**Option A.** Concretely:

- The row template (`MacrosSectionView`) gains a fourth column between the name and the kebab: a
  `Button.macroUnassign` (28 px octagon hover frame + `Close.svg`), `IsVisible` bound to the
  row's `IsSelected`, command bound to the parent VM's `ResetSelectedMacroTargetCommand` via the
  `#Root.DataContext` pattern the hotspot view already uses. A `Button` inside the `RadioButton`
  handles its own click, so pressing ✕ never re-assigns the macro (same precedent as the kebab).
- The ✕ chrome styles move from `DeviceImageWithHotspotsView` to `App.axaml` as the role-named
  `Button.macroUnassign` (+ `Path.unassignHoverFrame`), per the second-copy rule; the mouse
  hotspot switches to the shared class in the same change.
- Bug fix that the ✕ exposed: `ResetSelectedKeyAsync` never called `RefreshMacroListSelection`
  on the immediate-write path (Smart Infinite boards), leaving the just-unbound row checked — and
  its ✕ visible — until the next selection change. The staged path already refreshed inside
  `StageAssignmentTarget`; the call is now unconditional at the end of the reset (idempotent, so
  the staged path's double refresh is harmless).

## File-level change list

| File | Action | Intent |
|------|--------|--------|
| `src/ProoveGaming.App/App.axaml` | edit | Shared `Button.macroUnassign` / `Path.unassignHoverFrame` styles (moved from the hotspot view; role-named because two views now use them). |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/DeviceImageWithHotspotsView.axaml` | edit | Drop the local ✕ styles; the hotspot ✕ uses the shared class. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/MacrosSectionView.axaml` | edit | `x:Name="Root"`; the row grid grows a fourth column with the ✕ button (visible on the bound row, command = reset dispatcher). |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` | edit | `ResetSelectedKeyAsync` ends with `RefreshMacroListSelection()` so the immediate-write path clears the radio/✕ instantly. |
| `tests/ProoveGaming.App.Tests/DeviceDetailsKeyboardMacroBindingTests.cs` | edit | `Reset_ClearsTheMacroRowSelection_OnTheImmediateWritePath` — the regression test for the radio-refresh fix, through the Macros page's real assign path. |

**Flags:** no new NuGet package, no TFM bump, no new project, no `Directory.Build.props` change,
no installer/uninstaller impact, no gRPC change, no new localization strings (the ✕ is an icon
button, matching the mouse's).

## Acceptance criteria

1. Given a keyboard (Raster, T-Racer, Cyrex or Aress) with a macro bound to a key, when that key
   is selected on the Macros page, then the bound macro's list row shows an unassign ✕ next to
   the kebab; rows of unbound macros show none.
2. Given the ✕ is clicked, then the key reverts to its own keycode, the binding row leaves
   SQLite, the radio mark and the ✕ disappear immediately (both keyboard families), and the
   physical key types its own letter again after the push.
3. Given a macro bound to a mouse button, then its list row shows the same ✕ and clicking it
   resets the button to its default action — identical to the hotspot ✕.
4. Given the ✕ is pressed, then the click never re-assigns the macro (the row must not
   re-check).
5. Given a macro was unassigned, then the active preset is marked dirty (binding removal is a
   preset edit) and Save Preset lights up.
6. Given no key/button is selected, or the selected one has no macro, then no row shows a ✕.
7. `dotnet build ./src/ProveGaming.slnx` and `dotnet test ./src/ProveGaming.slnx` pass.

## Test strategy

The affordance itself is view markup (untestable without a visual tree); the invariant it hangs on
— `MacroViewModel.IsSelected` mirrors the binding and clears on reset — is what gets automated.

### `tests/ProoveGaming.App.Tests`

- **`Reset_ClearsTheMacroRowSelection_OnTheImmediateWritePath`** (added): on a Cyrex, bind via the
  Macros page's real path (`macro.AssignCommand`), assert `IsSelected` set; execute
  `ResetSelectedMacroTargetCommand`; assert the SQLite binding is gone **and** `IsSelected` is
  false. This is the reproduce-then-fix test for the radio-refresh bug.
- Pre-existing coverage relied on: `ResetIsOfferedForAKeyThatOnlyHasAMacroBound` (staged family,
  enablement + storage), the resync stale-slot tests (device-side clearing), and the mouse reset
  suite — all untouched and green.

### Deliberately not automated

- The ✕'s visibility/command bindings and the click-does-not-reassign behaviour — need a real
  visual tree; covered by QA steps 1–5. The `#Root.DataContext` command pattern is
  precedent-backed by the shipped hotspot ✕.

## QA (manual test steps)

1. Connect a Raster (or T-Racer). Open its device page → Macros. Record a macro, select a key on
   the on-screen keyboard, click the macro in the list to bind it.
   *Expected:* the bound macro's row shows a small ✕ between the name and the ⋮ menu; other rows
   show none.
2. Click the ✕.
   *Expected:* the row's radio mark and the ✕ disappear immediately; pressing the physical key
   types its own letter again; the Save Preset button lights up.
3. Repeat steps 1–2 on a Cyrex or Aress.
   *Expected:* identical, including the ✕ disappearing right away.
4. Connect a mouse (Blaze or Taipan). Macros page: bind a macro to a button.
   *Expected:* the ✕ appears both on the hotspot badge (as before) and on the macro's list row;
   clicking either unassigns, and both look and hover the same.
5. Bind a macro again and press the ✕ deliberately fast several times.
   *Expected:* the macro never flashes re-assigned; after the first click the row is unbound and
   stays unbound.
6. Hover the ✕.
   *Expected:* the octagonal frame appears on hover, exactly like the mouse hotspot's ✕.

## Review focus

- `null-safety` — the ✕ has no CanExecute; the reset dispatcher's own guards
  (`SelectedPhysicalKey`/hotspot null checks) are the protection, and visibility gates the
  affordance to rows that are actually bound.
- `UI-thread affinity` — the added refresh is a small in-memory loop on the UI thread; no device
  I/O moved.

## Performance & quality

- **Hot paths:** none. The ✕ renders per macro row (a handful); the extra
  `RefreshMacroListSelection` on reset iterates the macro list once — allocation-free.
- **Thread-safety / resource lifetime:** no new state, subscriptions or disposables; the button
  binds to an existing `RelayCommand`.

## Risks

1. **Compiled-binding reach through `#Root.DataContext`** — resolves reflectively at runtime, so
   a rename of `ResetSelectedMacroTargetCommand` would break the ✕ silently. Accepted: the exact
   pattern already ships in the hotspot view, and QA step 2 exercises it directly.
2. **Row real estate** — the ✕ consumes ~50 px, so long macro names clip slightly earlier on the
   selected row; the star column absorbs it (no overflow), `DisplayName` truncation still rules.

## Open questions

- none.

## Review findings

### Round 1 — Ship: yes

Reviewed at `bc7c740` (branch `features/keyboard-macro-unassign`, PR #81), working tree clean.
Delivered in chat before this as-built plan existed; transcribed here verbatim in substance.

#### Plan conformance

Plan: written retroactively (this document) — the review was code-only against the user's request
and the mouse-side precedent. The as-built behaviour matches the request: in-place cancel of a
bound macro, all keyboard families, mouse semantics preserved.

#### Blockers

- none

#### Code

- none — three candidates checked and dropped: (1) `#Root.DataContext.ResetSelectedMacroTargetCommand`
  inside a typed DataTemplate — exact shipped precedent in `DeviceImageWithHotspotsView`, build
  clean; (2) ✕ without CanExecute — visibility is gated by `IsSelected` (only a genuinely bound
  row shows it) and the dispatcher's own null guards hold; (3) ✕ click re-assigning the macro —
  a `Button` inside the `RadioButton` handles its own click, kebab precedent in the same row.

#### Tests

- [x] `Reset_ClearsTheMacroRowSelection_OnTheImmediateWritePath` covers the exact invariant the
  ✕'s visibility hangs on, through public commands and the Macros page's real assign path. The
  staged family was already covered. Standing caveat: CI runs no `tests/` projects.

#### Nits

- Double `RefreshMacroListSelection` on the staged path (inside `StageAssignmentTarget` plus the
  new unconditional call) — idempotent, cheap; the price of a single invariant point.
- Long macro names clip slightly earlier on a selected row (✕ + spacing ≈ 50 px); cosmetic, the
  star column prevents overflow — eyeball in QA step 1.

#### What's done well

- Zero new commands: the ✕ reuses the `ResetSelectedMacroTarget` dispatcher, so unassign
  semantics are provably identical to the header Reset for both keyboard families and the mouse.
- The second-copy rule applied at the right moment: styles moved to `App.axaml` with a role name
  before the duplicate landed, and the mouse view switched to the shared class in the same commit.
- The pre-existing radio-refresh bug was fixed in the VM with a regression test rather than
  papered over by the new affordance.

#### Verdict

Ship: **yes** — no blockers, two cosmetic nits. First: **1)** visual QA steps 1–3 on Raster +
Cyrex, **2)** confirm Save Preset lights after an unassign, **3)** mouse check (step 4) that both
✕ affordances behave identically.

### Round 2 — external review (PR #81 comment), M1 fixed

boikoserhii84's review (2026-08-05) found one major and six minors. Disposition at `HEAD`:

- [x] **M1. ✕ active while the assignment write is in flight** — *fixed, option A as
  recommended:* the ✕ now carries `IsEnabled="{Binding #Root.DataContext.CanResetSelectedMacroTarget}"`
  — the same gate the header Reset link uses, which reads false until the binding is actually
  recorded (no SQLite row, no `_assignments` update inside the window) and is raised by the
  existing `NotifySelectedMacroTargetChanged()` calls when the write completes. `App.axaml` adds
  `:disabled` rules so a gated ✕ shows no hover frame and no hand cursor (the icon itself cannot
  be dimmed — Avalonia.Svg.Skia ignores ancestor opacity). Regression test
  `UnassignGate_StaysClosedWhileTheAssignmentWriteIsInFlight` uses the fake's `DuringKeyWrite`
  hook to pin the gate false inside the write and true after, exactly the property the ✕ binds.
- [ ] m1 (✕ markup duplicated across two views — templated control) — open, follow-up.
- [ ] m2 (hidden ✕ column still costs 22 px ColumnSpacing on every row) — open, follow-up.
- [ ] m3 (no ToolTip / AutomationProperties.Name on a destructive icon button) — open, follow-up.
- [ ] m4 (new tests depend on the fake completing synchronously) — open; noted, ties into M1's
  option B (awaitable assignment) if the team takes it.
- [ ] m5 (CI does not run `tests/`) — standing repo-wide caveat, tracked outside this branch.
- [ ] m6 (silent early-out in `ResetSelectedKeyAsync` when the key has no self target) — open,
  follow-up (log the exit).

### Round 3 — external review (PR #81 comment 2026-08-06), all items fixed

- [x] **M1 (re-bind window).** The stored-state gate closed only the clean-key case; while macro
  B's write was in flight the old binding A kept `CanResetSelectedMacroTarget` true. Fixed with
  the reviewer's option A: `_assignmentWriteInFlight` set for the whole span of
  `ApplyAssignmentTargetAsync` (before the first await, cleared in `finally`, notifying at both
  ends) and read first inside `SelectedKeyHasOverride` — one flag closes the primary bind, the
  re-bind and the header-Reset entry alike. Regression test
  `UnassignGate_StaysClosedWhileReplacingAnExistingBinding` pins the gate false during the
  re-bind write and the final state on B.
- [x] **m1 (disabled ✕ indistinguishable).** `TintColor` IS a styled property — the reviewer was
  right and the code comment was wrong; `:disabled` now dims the icon to `#5A5878` via a
  descendant selector, and the comment was corrected.
- [x] **m2 (read-only transport leaves a checked row).** `AssignMacroToSelectedButton` now
  re-syncs the list in the `IsDeviceConfigurable` early exit, like every other early exit there.
- [x] **m3 (duplicated ✕ markup).** The whole visual moved into `Button.macroUnassign`'s
  `Template` in App.axaml; both call sites are now one bare `<Button/>` and cannot drift.
- [x] **m4 (hidden column still costs 22 px).** `ColumnSpacing` removed from the row grid; gaps
  live on the elements (name / ✕ / kebab margins), so a collapsed ✕ costs nothing on any row.
- [x] **m5 (no tooltip / automation name).** `ToolTip.Tip` and `AutomationProperties.Name` are
  style setters on `Button.macroUnassign`, fed by the new localized `Macros_Unassign` (en/uk) —
  both call sites get them for free.
- [x] **m6 (silent early-out).** `DeviceDetailsViewModel` gained an optional
  `ILogger<DeviceDetailsViewModel>` (DI-resolved, null in tests) and the no-self-target exit in
  `ResetSelectedKeyAsync` logs a warning with the key name and scancode.
- [x] **m7 (tests never run in CI).** `dotnet-ci.yml`'s Test step now targets
  `./src/ProveGaming.slnx`, so the `tests/` projects finally execute in CI. (The synchronous-fake
  caveat stands and is documented on the tests; command-level serialization remains the option-B
  follow-up if the team wants races closed below the UI gate.)
