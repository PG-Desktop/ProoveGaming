# Selected-key macro badge (name + unassign ✕) on the keyboard overlay

> **2026-08-14 — Superseded.** The badge this plan added was removed by
> [`remove-macro-list-selection`](../remove-macro-list-selection/remove-macro-list-selection-plan.md):
> the bound macro is now named by the key overlay's hover tooltip only (on both the Macros and
> Key Assignments sections), and that tooltip carries the badge's unassign ✕ for macro-bound
> keys; the Key Assignment Dialog and the shared header's Reset link unassign as well. This
> document is kept as the historical record of the badge's design; do not review current code
> against it.

## Goal

On the Macros page, when the user selects a key on the on-screen keyboard, they currently get a
purple highlight and nothing else: the only place that names the macro bound to that key is the
radio mark in the macro list on the left panel, and the only way to remove it is the ✕ on that
list row or the header **Reset** link. Users read the highlighted keycap as "this key is selected"
and not as "this key plays macro X", so the binding is effectively invisible on the artefact they
are actually looking at. This change puts the answer on the key: the selected key grows a floating
badge above it carrying the bound macro's name, with an unassign (✕) anchored at the badge's
chip beside it. It applies to every keyboard that renders the interactive overlay — Raster,
T-Racer, Cyrex, Aress, T800 and Rampage — because they all share one view.

Reference for the visual: the user's screenshot shows a dark chip reading `Quick Unique Action 1`
floating just above the selected `F1` keycap.

Related work:
[`add-keyboard-macro-unassign`](../add-keyboard-macro-unassign/add-keyboard-macro-unassign-plan.md)
added the ✕ on the macro **list row** and explicitly ruled per-key badges out of scope (Option B
there, rejected because badges on *all* bound keys collide at overlay scale). This plan revisits
exactly that decision, and the collision objection does not apply: the badge is drawn for the
**selected key only**, so at most one badge exists at any time.
[`add-mouse-macros`](../add-mouse-macros/add-mouse-macros-plan.md) is the mouse-side precedent —
its hotspots already show "macro name + ✕", which is the interaction this brings to keyboards.

## Scope

**In scope**

- A floating macro badge on `RasterKeyboardOverlayView`, shown when: the Macros section is
  active **and** a physical key is selected **and** that key has a macro bound.
- The badge shows the bound macro's name and an unassign ✕ inside the chip beside it, the same
  name-then-✕ row a mouse hotspot draws.
- The ✕ dispatches the existing `ResetSelectedMacroTargetCommand` and is gated by the existing
  `CanResetSelectedMacroTarget`, so unassign semantics are byte-for-byte the ones the list-row ✕
  and the header Reset already use, on both the staged (Raster / T-Racer / Rampage / T800) and
  immediate-write (Cyrex / Aress) families.
- Badge placement/sizing computed in the view model so it is board-independent and unit-testable.
- Extending the shared `Button.macroUnassign` template so its size can be set per call site
  (it is currently hard-pinned to 28 px, which renders at ~11 px inside the overlay's Viewbox).

**Out of scope**

- A persistent indicator on **every** key that has a macro bound (so the user can spot bound keys
  without clicking each one). Genuinely useful, genuinely a different feature — see
  *Open questions*.
- Any change to how bindings are stored, staged, resynced or preset-scoped. The badge is a pure
  read of existing state plus the existing reset dispatcher.
- The mouse Macros page. Its hotspots already carry name + ✕; nothing there changes.
- The Key Assignments and Actuation sections. They share the same overlay control, but the badge
  is gated on `IsMacrosSection`, matching how `RefreshHotspotMacroBadges` gates the mouse badges.
- Removing or restyling the existing macro-list-row ✕. Both affordances coexist; they are the same
  command.

## Options considered

### Option A — badge drawn inside the overlay's Viewbox, geometry computed in the view model *(recommended)*

A second `Canvas` layer inside `RasterKeyboardOverlayView`'s `Stage`, declared in XAML and
positioned from view-model properties that are computed from the selected key's bounds and the
stage size.

*Pros:* the badge lives in the same coordinate space as the key overlays, so it stays registered
to the keycap at any window size for free — the shared `Viewbox` already solves scaling for the
key buttons. All the geometry is arithmetic in a view model, therefore testable without a visual
tree. One view = all six boards.

*Cons:* the badge is scaled by the Viewbox along with the board, so its canvas-space sizes have to
be expressed as fractions of the stage width to land at a constant on-screen size (the boards'
viewBoxes differ: Cyrex 1472.5, Rampage 1471.4, Raster 1529.3, T-Racer 1595.3, T800 1742.0 — all
rendered into a fixed 628 px-wide host, so the scale factor ranges 0.36–0.43). Also, the shared
✕ template has to become size-parameterisable.

### Option B — badge drawn outside the Viewbox, in screen space, over the keyboard

Keep a normal-sized (28 px) ✕ and normal font by putting the badge in the parent `Panel` and
converting the key's canvas coordinates to screen coordinates with the live Viewbox scale.

*Rejected:* requires tracking the Viewbox's actual render scale and re-projecting on every layout
pass and window resize — new code whose failure mode is a badge silently drifting off its keycap.
Option A gets registration for free from the existing single-Viewbox design, and the "constant
on-screen size" property Option B is bought for is recoverable in Option A by expressing sizes as
a fraction of stage width.

### Option C — a badge on every key that has a macro bound

The literal mouse-hotspot copy, and the reading of "highlight which macro is bound" that does not
require a selection.

*Rejected as the primary design:* this is the option
[`add-keyboard-macro-unassign`](../add-keyboard-macro-unassign/add-keyboard-macro-unassign-plan.md)
already rejected on the merits — a full-width board can hold dozens of bindings and the badges
overlap into an unreadable stack. It also contradicts the request, which is explicitly scoped to
the selected key ("when I select a key"). A *non-textual* version of it survives as a follow-up in
*Open questions*.

### Option D — extend the existing selected-key highlight instead (tooltip on hover)

*Rejected:* a tooltip is not discoverable, cannot host a ✕, and the user's screenshot shows a
persistent chip, not a hover affordance.

## Recommended approach

**Option A.** Concretely:

**1. A small badge view model.** New `SelectedKeyMacroBadgeViewModel` (App, `ViewModels/DeviceDetails/`)
holding `IsVisible`, `MacroName`, and the canvas-space geometry `Left`, `Top`, `Width`, `Height`,
`CornerRadius`, `FontSize`, `Padding`, `UnassignSize`, `MaxTextWidth`. It exposes one
`Update(string? macroName, KeyBadgeAnchor? anchor, double stageWidth, double stageHeight)` method
that either hides the badge or recomputes every property and raises change notification. All sizes
derive from `stageWidth` so the on-screen result is constant across boards:

| Metric | Canvas-space value | On-screen (stage → 628 px) |
|---|---|---|
| Badge height | `stageWidth * 0.0525` | ≈ 33 px |
| Font size | `stageWidth * 0.0255` | ≈ 16 px |
| Corner radius | `stageWidth * 0.0064` | ≈ 4 px |
| Horizontal padding | `stageWidth * 0.019` | ≈ 12 px |
| Gap between badge and keycap | `stageWidth * 0.013` | ≈ 8 px |
| Gap between name and ✕ | `stageWidth * 0.0127` | ≈ 8 px (same as the mouse hotspot's macro row) |
| Unassign ✕ box | `stageWidth * 0.0446` | ≈ 28 px (matches the shared template's native size) |
| Max text width | `stageWidth * 0.40` | ≈ 251 px, then `CharacterEllipsis` |

Chip layout: a single row, `name` then `✕`, both inside the chip's padding. This is the same row a
mouse hotspot draws for a bound macro (`DeviceImageWithHotspotsView`), which is what the requester
asked for after seeing the first cut: *"close button on tooltip should be inside tooltip. Look how
it works for mouse."* An earlier revision hung the ✕ off the chip's top-right corner, half on and
half off; that is **superseded** — the ✕ is inside the chip, and the chip is the badge's whole
bounding box.

**The chip takes its width from its own content, not from the view model.** Both Grid columns are
`Auto` and the chip's `Panel` has no `Width`; only `Height` is pinned, so every chip is the same
height whatever it holds. `Width` on the view model is a per-character *estimate* used solely to
centre and clamp the chip. Keeping those two jobs separate is load-bearing: a revision that fed the
estimate to the text column as its real width made `TextTrimming` swallow the entire name the
moment the glyphs measured a hair wider than the guess — a macro named `tt` rendered as `…`. A
layout that must be pixel-exact can never be driven by an approximation; a chip a few pixels
off-centre is the right trade for a name that always renders. `MaxTextWidth` remains the only cap,
and it is generous enough that trimming means the name genuinely is too long.

Placement rules, in this order:

- Horizontally centred on the anchor key, then clamped into `[0, stageWidth - Width]` so a badge
  over `Esc` or `Print Screen` never leaves the board.
- Vertically above the key (`anchor.Y - Height - gap`); if that is negative (function row, `Esc`),
  flip to below (`anchor.Y + anchor.Height + gap`). The vertical flip is what keeps the top row
  usable — on Cyrex and T800 the top row is at `Y ≈ 0`.

Width is estimated from the (possibly ellipsised) name using the same cheap per-character estimate
`DevicePartHotspotViewModel.EstimateLabelWidth` uses, scaled to the badge font size. That helper
is currently a `private static` on the hotspot VM; move it to an internal shared helper (see the
change list) rather than copying it — the second copy is the trigger.

**2. One refresh hook.** `DeviceDetailsViewModel` gains `SelectedKeyMacroBadge` (the instance
above) and a private `RefreshSelectedKeyMacroBadge()` that resolves:

- gate: `IsKeyboardDevice && IsMacrosSection && SelectedPhysicalKey is not null`
- name: `BoundMacroForSelectedKey()?.Name` (already cached via `_macroBindingsBySlot`, so this is
  a dictionary probe, not a SQLite read)
- anchor: the selected key's artwork bounds when `UseArtworkKeyOverlays`, else its
  `CanvasX/CanvasY/PixelWidth/PixelHeight` — the same branch `RasterKeyboardOverlayView.RebuildKeyboard`
  makes, extracted into an `internal (double X, double Y, double W, double H)? KeyOverlayBounds(KeyAssignmentOptionViewModel)`
  so the two cannot drift
- stage size: `UseArtworkKeyOverlays ? ArtworkViewBoxWidth/Height : OverlayCanvasWidth/Height`

`RefreshSelectedKeyMacroBadge()` is called from **`NotifySelectedMacroTargetChanged()`** and from
nowhere else. That is deliberately the single hook point: it already fires on every event that can
change what the badge shows — the `SelectedPhysicalKey` setter (including the `= null` inside
`LoadKeyLayoutForDevice`), `RefreshMacroListSelection()` (which itself fires on section change,
key selection, assign, reset, and macro-list rebuild), and both ends of the
`_assignmentWriteInFlight` window. Adding it to any of those individually would repeat the exact
mistake that method's doc comment was written to prevent.

**3. The view.** `RasterKeyboardOverlayView.axaml` gains a `Canvas x:Name="BadgeLayer"` inside
`Stage`, declared *after* `KeyboardCanvas` so it draws on top, bound to
`SelectedKeyMacroBadge`. It is a separate canvas because `RebuildKeyboard()` calls
`KeyboardCanvas.Children.Clear()` — a badge parented there would be destroyed on every rebuild.
The code-behind sizes `BadgeLayer` alongside the other two layers in `RebuildKeyboard()`.

The badge floats over real, clickable keycaps in the neighbouring row, so its filled surface must
not take clicks. Hit testing does **not** descend into a subtree whose ancestor is
`IsHitTestVisible="False"`, so the ✕ cannot simply live inside a non-hit-testable chip: the chip
background is its own `Border` (`IsHitTestVisible="False"`), and the name + ✕ row is a **sibling**
`Grid` with no `Background` — a panel without a background is not a hit target itself, so clicks
fall through everywhere except the ✕ `Button`. The name `TextBlock` inside that row is
`IsHitTestVisible="False"` too: unlike the `Grid` it *does* paint (a glyph run), so it is a real
hit target covering most of the chip, and the ✕ survives because it is the `TextBlock`'s **sibling**
rather than its child (see round 7).

**4. The shared ✕ template becomes size-parameterisable.** `Button.macroUnassign` in `App.axaml`
currently templates a fixed `Panel Width="28" Height="28"`. Wrap that panel in a `Viewbox` and add
`Width`/`Height` style setters of 28, so the two existing call sites (mouse hotspot, macro list
row) are pixel-identical to today while the overlay badge can set `Width`/`Height` to the
canvas-space `UnassignSize`. This is preferred over authoring a second ✕ visual: the round-3
review of `add-keyboard-macro-unassign` specifically consolidated this visual into one template so
the call sites "cannot drift apart".

**5. Unassign wiring.** `Command="{Binding #Root.DataContext.ResetSelectedMacroTargetCommand}"`,
`IsEnabled="{Binding #Root.DataContext.CanResetSelectedMacroTarget}"` — the same pair
`MacrosSectionView` uses on the list-row ✕, reached through the `#Root.DataContext` pattern
already shipping in `DeviceImageWithHotspotsView`. `RasterKeyboardOverlayView` needs an
`x:Name="Root"` for that. No new command, no new state, no new gate: the in-flight gate that
round 3 added closes for this ✕ automatically.

## File-level change list

| File | Action | Intent |
|------|--------|--------|
| `src/ProoveGaming.App/ViewModels/DeviceDetails/SelectedKeyMacroBadgeViewModel.cs` | create | The badge's visibility, name and canvas-space geometry; one `Update(...)` that recomputes everything from the anchor key bounds + stage width. All arithmetic, no dependencies — the unit-testable half of this change. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` | edit | Expose `SelectedKeyMacroBadge`; add `RefreshSelectedKeyMacroBadge()` and call it from `NotifySelectedMacroTargetChanged()`; add `internal KeyOverlayBounds(KeyAssignmentOptionViewModel)` returning the artwork-or-fallback bounds for one key. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DevicePartHotspotViewModel.cs` | edit | `EstimateLabelWidth` moves out to the shared helper below and is called from there — the badge needs the same estimate and a copy would be the second one. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/LabelWidthEstimator.cs` | create | The extracted per-character width estimate, `internal static`, with the existing 8 px / 4.5 px-space constants documented as being calibrated for the hotspot font size and scaled by callers. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml` | edit | `x:Name="Root"`; a `BadgeLayer` canvas above `KeyboardCanvas` holding the badge — a non-hit-testable chip `Border` plus a sibling `Grid` with the name and the ✕ inside it — bound to `SelectedKeyMacroBadge`, hit-test-transparent except the ✕. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml.cs` | edit | Size `BadgeLayer` to the stage in `RebuildKeyboard()`, next to the existing `Stage`/`KeyboardCanvas`/`ArtLayer` sizing. |
| `src/ProoveGaming.App/App.axaml` | edit | `Button.macroUnassign`: template panel wrapped in a `Viewbox`, plus `Width`/`Height` setters of 28 so existing call sites are unchanged and the badge can scale it. |
| `tests/ProoveGaming.App.Tests/SelectedKeyMacroBadgeTests.cs` | create | Geometry + visibility unit tests for the badge view model (centring, clamping, top-row flip, ellipsis, constant on-screen size across board widths). |
| `tests/ProoveGaming.App.Tests/DeviceDetailsKeyboardMacroBindingTests.cs` | edit | Integration tests through the real commands: badge appears on assign, clears on unassign, hides on section change / deselect, and does so on each keyboard family harness. |

**Flags:** no new NuGet package, no TFM bump, no new project in `src/ProveGaming.slnx`, no
`Directory.Build.props` change, no gRPC/proto change, no installer or uninstaller impact, no new
localization strings (the badge shows a user-authored macro name; the ✕ inherits the existing
`Macros_Unassign` tooltip and automation name from the shared style).

## Acceptance criteria

1. Given a Raster keyboard on the Macros page with macro *M* bound to key *K*, when *K* is
   selected on the on-screen keyboard, then a badge appears adjacent to *K* showing *M*'s name,
   with a ✕ inside the chip beside it.
2. Given the same, when a key with **no** macro bound is selected, then no badge is shown.
3. Given no key is selected (fresh page load, or after a device switch), then no badge is shown.
4. Given a key with a bound macro is selected on the Macros page, when the user navigates to any
   other section (Key Assignments, Actuation, RGB, …), then the badge disappears; returning to
   Macros with the same key still selected brings it back.
5. Given the badge is shown, when its ✕ is clicked, then the key reverts to its own keycode, the
   `key_macro_bindings` row is removed, the badge disappears immediately, the macro list row's
   radio mark and its own ✕ clear at the same moment, and the active preset is marked dirty.
6. Given an assignment write is still in flight, then the badge's ✕ is disabled and shows no
   hover frame — identical to the list-row ✕ (same `CanResetSelectedMacroTarget` gate).
7. Given the selected key is in the top row (`Esc`, function row) so there is no room above it,
   then the badge renders **below** the key instead of being clipped at the top of the board.
8. Given the selected key is at the far left or far right edge, then the badge is clamped inside
   the board bounds and the ✕ remains fully visible.
9. Given a macro whose name is longer than the badge's maximum text width, then the name is
   ellipsised (not wrapped, not overflowing) and the chip does not stretch past its text budget.
   The full name stays readable on the macro list row, which shows it with its own tooltip — the
   chip carries none, because the surface it would hang on is deliberately not hit-testable.
10. Criteria 1–9 hold identically on Raster, T-Racer, Cyrex, Aress, T800 and Rampage, and the
    badge's rendered size is visually the same on all of them despite their different artwork
    viewBox widths.
11. Clicking a key in the row that the badge floats over still selects that key — the badge does
    not swallow the click anywhere except on its ✕.
12. The mouse Macros page is unchanged: hotspot badge and list-row ✕ look and behave exactly as
    before.
13. `dotnet build ./src/ProveGaming.slnx` and `dotnet test ./src/ProveGaming.slnx` pass.

## Test strategy

This is an `add-*` change; the tests below are required coverage, not optional. The split is
deliberate: the badge's *geometry* is pure arithmetic and gets thorough unit tests, while its
*wiring* is exercised through the existing view-model command harness. Nothing here needs a
visual tree.

### `tests/ProoveGaming.App.Tests/SelectedKeyMacroBadgeTests.cs` (new)

Against `SelectedKeyMacroBadgeViewModel.Update(...)` directly:

- `Update_WithNoMacro_HidesTheBadge` — null/empty name → `IsVisible` false, and no stale geometry
  left readable.
- `Update_CentresTheBadgeOnTheKey` — a mid-board key → badge horizontal centre equals the key's.
- `Update_PlacesTheBadgeAboveTheKey_WhenThereIsRoom` — `Top + Height + gap <= key.Y`.
- `Update_FlipsBelowTheKey_WhenThereIsNoRoomAbove` — key at `Y = 0` → `Top >= key.Y + key.Height`.
- `Update_ClampsToTheLeftEdge` / `Update_ClampsToTheRightEdge` — key at `X = 0` and at
  `stageWidth - keyWidth` → `Left >= 0` and `Left + Width <= stageWidth`, ✕ still inside.
- `Update_EllipsisesALongMacroName` — a 60-character name → `Width` stays at or below the
  max-text-width budget and the displayed text is trimmed with the full name still exposed.
- `Update_KeepsTheOnScreenSizeConstantAcrossBoards` — the golden invariant behind criterion 10:
  run `Update` at each real stage width (1472.469, 1471.412, 1529.288, 1595.294, 1742.0), scale
  each result by `628 / stageWidth`, and assert the on-screen heights and font sizes agree within
  a pixel. This is the test that fails if someone later hardcodes a pixel size.

### `tests/ProoveGaming.App.Tests/DeviceDetailsKeyboardMacroBindingTests.cs` (extend)

Reusing the file's existing harnesses (`WithRasterInMacrosSection`, `WithCyrexInMacrosSection`,
`WithRampageInMacrosSection`, `WithT800InMacrosSection`) and driving the real commands, exactly
as `Reset_ClearsTheMacroRowSelection_OnTheImmediateWritePath` already does:

- `Badge_ShowsTheBoundMacro_WhenAKeyWithAMacroIsSelected` (Raster, staged path).
- `Badge_IsHidden_ForAKeyWithNoMacro`.
- `Badge_ClearsWhenTheMacroIsUnassigned` — execute `ResetSelectedMacroTargetCommand`, assert
  `SelectedKeyMacroBadge.IsVisible` is false. Run on both families: Raster (staged) and Cyrex
  (immediate write) — the immediate path is where the analogous list-row refresh bug lived.
- `Badge_IsHidden_OutsideTheMacrosSection` — set `ActiveSection` away from `Macros` with the same
  key selected, assert hidden; set it back, assert visible.
- `Badge_ClearsWhenTheDeviceChanges` — the `SelectedPhysicalKey = null` inside
  `LoadKeyLayoutForDevice` must reach the badge; this pins the `NotifySelectedMacroTargetChanged`
  hook point.
- `Badge_TracksTheNewMacro_WhenTheKeyIsRebound` — bind A, rebind to B, assert the badge names B.
- One per-family smoke assertion (T800 and Rampage) that the badge resolves a name at all, since
  those two reach `BoundMacroForSelectedKey` through different slot resolution.

### Deliberately **not** automated

- The badge's rendered appearance, the ✕'s hover frame, the `IsHitTestVisible` split and the
  `#Root.DataContext` command reach — all need a real visual tree. Covered by QA steps 2, 5, 7
  and 8. The `#Root.DataContext` pattern is precedent-backed by two shipped call sites.
- That `Button.macroUnassign` still renders identically at its default size after the `Viewbox`
  change — visual, covered by QA step 9.
- Real HID writes; the suite's existing `FakeUsbHidDeviceService` stands in, with the standing
  caveat (documented on those tests) that the fake completes synchronously.

## QA (manual test steps)

1. Launch the app, connect a Raster keyboard, open its device page and go to **Macros**. Record a
   macro named something short, e.g. `Reload`.
   *Expected:* the macro appears in the list on the left; the on-screen keyboard is clickable.
2. Click the `G` key on the on-screen keyboard, then click the macro in the list to bind it.
   *Expected:* a dark chip appears just above the `G` keycap reading `Reload`, with a small ✕ at
   inside the chip, to the right of the name.
3. Click a different key that has no macro bound.
   *Expected:* the chip disappears. Click back on `G`: the chip comes back.
4. Switch to the **Key Assignments** section, then back to **Macros**.
   *Expected:* the chip is not shown on Key Assignments; it is shown again on Macros with `G`
   still selected.
5. With the chip showing, click its ✕.
   *Expected:* the chip disappears immediately, the macro's row in the list loses its radio mark
   and its own ✕, the **Save Preset** button lights up, and pressing the physical `G` key types a
   normal `g` again.
6. Bind a macro to `G` again and immediately click the chip's ✕ as fast as you can, several times.
   *Expected:* the ✕ is inert while the assignment is still being written (no hover frame, no hand
   cursor), then works once; the macro never flashes back to assigned.
7. Bind a macro to `Esc`, and to a key in the top function row.
   *Expected:* the chip renders **below** those keys rather than being cut off at the top edge of
   the keyboard image.
8. Bind a macro to the leftmost key of a row and to the rightmost key of a row.
   *Expected:* the chip stays fully inside the keyboard image; the ✕ is never clipped.
9. Bind a macro whose name is very long (30+ characters).
   *Expected:* the chip does not stretch across the whole board — the name is cut with `…`, the ✕
   stays fully visible beside it, and the full name is still readable on the macro's row in the
   list on the left.
10. With a chip visible over the row above the selected key, click one of the keys the chip
    overlaps.
    *Expected:* that key gets selected normally — the chip does not block the click.
11. Repeat steps 2, 5 and 7 on a T-Racer, a Cyrex (or Aress), a T800 and a Rampage keyboard.
    *Expected:* identical behaviour on all of them, and the chip is the same physical size on
    screen on every board (the boards are drawn at the same width).
12. Connect a mouse (Blaze or Taipan) and open **Macros**. Bind a macro to a button.
    *Expected:* unchanged from before — the macro name and ✕ on the hotspot, and the ✕ on the
    macro list row, look and behave exactly as they did.

## Review focus

- `UI-thread affinity` — the badge refresh runs on the dispatcher inside
  `NotifySelectedMacroTargetChanged`, which fires on every key click; it must stay a cached
  dictionary probe plus arithmetic, never a SQLite read.
- `performance hot-path` — same reason: `BoundMacroForSelectedKey()` must keep hitting the
  `_macroBindingsBySlot` cache, and the badge must not force an overlay rebuild.
- `null-safety` — the anchor lookup returns null for keys the artwork does not draw, and
  `SelectedPhysicalKey` is nulled by `LoadKeyLayoutForDevice`; both must hide the badge rather
  than throw.
- `localization` — confirm no new user-facing string was introduced and the ✕ still inherits
  `Macros_Unassign` for its tooltip and automation name after the template change.

## Performance & quality

- **Hot paths:** none newly introduced. The badge refresh is triggered by user selection clicks
  and section switches, not by a poll, a frame loop or device I/O. Per refresh it is one cached
  dictionary lookup, one `Macros.FirstOrDefault`, and a dozen `double` operations — the
  `EstimateLabelWidth` loop is over a macro name (tens of characters). No allocation beyond the
  trimmed display string when a name actually needs ellipsising.
- **Not on the UI thread:** nothing new. No `Task`, no await, no device call is added; the ✕
  reuses `ResetSelectedMacroTargetCommand`, whose async behaviour is unchanged.
- **Thread-safety:** no new shared state. `SelectedKeyMacroBadgeViewModel` is owned by
  `DeviceDetailsViewModel` (a DI singleton) and mutated only from the UI thread, like every other
  view-model property there. The singleton device-discovery services are not touched.
- **Resource lifetime:** no new `IDisposable`, no new event subscription. The badge is a plain
  observable object living for the lifetime of its owner; the view binds it and unbinds with the
  DataContext. Note the existing `RasterKeyboardOverlayView` per-key `PropertyChanged`
  subscriptions are untouched — the badge deliberately does **not** add a per-key subscription of
  its own, which is why the refresh hangs off the view model rather than off key events.
- **No premature abstraction:** the badge is one concrete view model for one concrete affordance.
  The only extraction is `EstimateLabelWidth`, and only because this change creates its second
  caller.

## Risks

1. **Constant-on-screen sizing depends on the host width staying 628.** All three
   `RasterKeyboardOverlayView` call sites in `DeviceDetailsView.axaml` set `Width="628"`, and the
   fraction constants are calibrated against that. If the board is ever rendered at a different
   width the badge scales with it — which is correct behaviour, but the "≈ 33 px" figures in this
   plan stop holding. Mitigation: the constants are expressed as fractions of stage width, not as
   pixels, so the relationship is proportional by construction; the unit test asserts the
   cross-board invariant rather than absolute pixels.
2. **The `Button.macroUnassign` template change touches two shipped call sites.** Wrapping the
   panel in a `Viewbox` is behaviour-neutral only if the `Width`/`Height` setters are added in the
   same edit. Getting that wrong shrinks or stretches the mouse hotspot ✕ and the macro list ✕.
   Mitigation: QA step 12 checks both existing sites explicitly.
3. **Badge over a clickable row.** The `IsHitTestVisible="False"` split is easy to get subtly
   wrong (e.g. setting it on the `Canvas` instead of the chip, or forgetting to re-enable it on
   the ✕), and the failure mode is either a dead ✕ or dead keys. Mitigation: QA steps 5 and 10
   test the two halves separately.
4. **`#Root.DataContext` bindings resolve reflectively**, so renaming
   `ResetSelectedMacroTargetCommand` or `CanResetSelectedMacroTarget` breaks the ✕ silently. Same
   accepted risk as the two existing call sites; QA steps 5 and 6 exercise both names.
5. **T800 and Rampage anchor bounds.** T800 reports its artwork viewBox even on the bare-canvas
   fallback path, and Rampage uses a `RampageVisualLayout` canvas; if `KeyOverlayBounds` does not
   mirror `RebuildKeyboard`'s branch exactly, the badge lands offset on exactly those two boards.
   Mitigation: the branch is extracted to one method used by the badge, and QA step 11 covers both
   boards. (Rampage's `SupportsMacros` flag is still firmware-blocked; if macros cannot be bound
   there at all during QA, note it and treat Rampage as untestable rather than failed — see the
   `add-keyboard-macro-unassign` plan.)

## Open questions

All three were put to the requester before `/coder` ran and are now **resolved**; they are kept
here with their answers so the reviewer can see what was decided rather than assumed.

1. **A persistent hint on every key that has a macro bound.** *Resolved: out of scope, follow-up.*
   The current design tells the user what a key does only after they click it, so finding which of
   86 keys carry macros still means clicking around. A textual badge on every bound key is out
   (Option C), but a *non-textual* marker — a small dot in the keycap corner, or a distinct
   resting tint on the `rasterKey` overlay — would solve it without collisions and would compose
   with this badge. The requester had no preference, so it stays a separate change: it is a design
   decision of its own, and the request as written is explicitly selection-scoped. `/coder` must
   **not** implement it here.
2. **Exact badge colours.** *Resolved: use the existing token.* The requester had no preference,
   so the chip is `#1E162E` with `#FFFFFF` text — the same pair `Button.partHotspotButton` uses
   for the mouse hotspot chips, which is the app's existing "label chip over a device image" look
   and matches the reference screenshot. No new colour is introduced by this change.
3. **Should the badge also appear on the Key Assignments section?** *Resolved: no.* The badge is
   gated on `IsMacrosSection`, matching how the mouse hotspot badges are gated. Key Assignments
   keeps its left-panel target list, which already shows a macro target as selected.

## Review findings

*Filled in by `/reviewer`.*

### Round 1 — Ship: yes

Reviewed the complete working-tree delta: 7 modified files plus 4 untracked paths
(`git status --porcelain` reconciled against `git diff`; `git diff --staged` and
`git log main..HEAD` are both empty — see S2). Build clean, `dotnet test` on
`tests/ProoveGaming.App.Tests` green at 665 passed / 0 failed.

#### Plan conformance

Plan: `docs/plans/add-selected-key-macro-badge/add-selected-key-macro-badge-plan.md`

- **Implemented as planned:** all nine File-level change list items exist and do what the plan
  says. The badge geometry lives in a view model driven from one `Update(...)`; the refresh hangs
  off `NotifySelectedMacroTargetChanged` and nowhere else, as specified; `KeyOverlayBounds` mirrors
  `RebuildKeyboard`'s artwork-vs-fallback branch; `BadgeLayer` is a sibling canvas so
  `RebuildKeyboard`'s `Children.Clear()` cannot destroy it; the ✕ reuses
  `ResetSelectedMacroTargetCommand` with the `CanResetSelectedMacroTarget` gate; `App.axaml`'s
  shared template was made size-parameterisable rather than forked. Every resolved Open question is
  honoured in code — the chip is `#1E162E` (`RasterKeyboardOverlayView.axaml:119`), the badge is
  gated on `IsMacrosSection`, and no all-keys indicator was added.
- **Deviates from plan (both benign, plan is the stale side):**
  - The plan's `Update(...)` signature names a `KeyBadgeAnchor?` type; the code reuses the existing
    `RasterKeyBounds` record instead. Strictly better — no new type for a shape that already
    exists, and it is what `ArtworkKeyBounds` already returns.
  - `docs/CONTEXT.md` gained a glossary entry that the File-level change list does not mention.
    Required by the repo's documentation rules, so the change is right and the list was incomplete.
  - One test beyond the plan's list — `Update_FlipsBelowTheFunctionRow_OnTheRealRasterArtwork`.
    In-scope and valuable (see *What's done well*).
- **Missing / partial:** acceptance criterion 9's second half — "the full name is available as a
  tooltip" — is not met. See **S1**.
- **Accepted limitation (per plan):** criterion 7's flip-below means the badge renders *under* the
  function row, where the feature's reference screenshot shows it above. Recorded in the plan, in
  code comments and in a test. **Residual risk:** this is the one row the requester actually
  pictured, so it is the most likely thing to come back as "not what I asked for". It is a
  one-constant change if they disagree, but it should be shown to them before merge, not after.

#### Code

- [x] **S1. The chip's tooltip can never fire, so long macro names have no way to be read in
  full** — `src/ProoveGaming.App/Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml:113-131`
  — Should-fix
  **Problem / why it matters:** the `Border` carries `IsHitTestVisible="False"` (correct — it stops
  the chip swallowing clicks on the keys it floats over), and the `TextBlock` with
  `ToolTip.Tip="{Binding MacroName}"` is inside that Border. Avalonia's tooltip is driven by
  `PointerEntered`, and hit testing does not descend into a subtree whose ancestor is not
  hit-testable — so the tooltip is unreachable markup. A macro named past the text budget is
  trimmed to `Quick Unique Acti…` on the chip with no way to see the rest there. Acceptance
  criterion 9 and QA step 9 both assert this works; QA will file it as a bug.
  **Fix:** pick one and make the plan agree with it.
  - Option A (recommended, honest): delete the dead `ToolTip.Tip`, and amend criterion 9 and QA
    step 9 to drop the tooltip clause. The full name is still reachable one glance away — the
    macro list row already shows it with its own working tooltip (`MacrosSectionView.axaml:313`).
  - Option B (keeps the tooltip): move `ToolTip.Tip` onto the ✕ `Button`, which *is* hit-testable
    — but it already advertises `Macros_Unassign` from the shared style, so this trades a correct
    accessibility label for the name and is worse.
  - Option C (full behaviour): make the Border hit-testable and reinstate click pass-through by
    other means. Not worth it — it reopens the exact problem `IsHitTestVisible="False"` solves.

- [ ] **S2. The whole change is uncommitted on `main`, with no branch and no PR** — repo state —
  Should-fix
  **Problem / why it matters:** `git rev-parse --abbrev-ref HEAD` is `main`, `git log main..HEAD`
  is empty, and every file in this review is an unstaged working-tree edit. This repo ships through
  PRs (this plan's own siblings cite #81, #87, #88), and the branch delta is what `/reviewer` and
  CI are built around. Committing here would put unreviewed UI work straight onto the default
  branch and skip `dotnet-ci.yml` entirely.
  **Fix:** branch (e.g. `features/selected-key-macro-badge`), commit the delta there, open the PR.
  No code change required.

#### Tests

- [x] Coverage matches the plan's Test strategy and then some: 16 geometry cases in
  `tests/ProoveGaming.App.Tests/SelectedKeyMacroBadgeTests.cs` and 9 wiring cases appended to
  `DeviceDetailsKeyboardMacroBindingTests.cs`, all driven through real commands on the existing
  per-family harnesses. CI does run them — `dotnet-ci.yml:43` targets `./src/ProveGaming.slnx`, so
  the standing "tests never run in CI" caveat from PR #81 no longer applies here.
- [x] `Badge_IsHidden_ForAKeyWithNoMacro` can pass for the wrong reason —
  `tests/ProoveGaming.App.Tests/DeviceDetailsKeyboardMacroBindingTests.cs` — it picks the second
  physical key by `DevKeyName` inequality and asserts the badge is hidden. If that key ever lacks
  an artwork entry (Raster's FN has none), the badge hides because there is no anchor, not because
  there is no macro, and the test still passes. Pin it by asserting
  `vm.KeyOverlayBounds(key) is not null` first, or by choosing the key through that method.

#### Nits

- The ✕ overhangs the chip's right edge by `UnassignSize / 2` (≈ 34 canvas units) while the chip's
  horizontal padding is only ≈ 29, so at full text width the ✕ covers roughly the last 2 px of the
  name on screen — `SelectedKeyMacroBadgeViewModel.cs` (`HorizontalPaddingRatio` 0.019 vs
  `UnassignSizeRatio` 0.0446). Raising `HorizontalPaddingRatio` to ≥ 0.0223 removes the overlap.
- `MaxWidth="{Binding MaxTextWidth}"` on the chip's `TextBlock`
  (`RasterKeyboardOverlayView.axaml:126`) is dead: the Border's explicit `ChipWidth` minus padding
  is always ≤ `MaxTextWidth` by construction, so the Border is the binding constraint and the
  `MaxWidth` never applies. Harmless, but it reads as if it were load-bearing.
- The ✕ icon is now rendered through two nested `Viewbox` scales (the template's, then the key
  overlay's), netting 1.0. Vector all the way down, so it should stay sharp, but this is the one
  visual claim in the change nothing verifies — worth an explicit eyeball during QA step 11
  alongside the two original ✕ call sites (QA step 12).
- `SelectedKeyMacroBadgeViewModel.Update(...)` is `public` on a `public` class, so anything in the
  app can drive the badge. Only `DeviceDetailsViewModel` should. `internal` would still satisfy the
  compiled binding (the bound *properties* are what need to be public) and stays test-visible
  through the existing `InternalsVisibleTo`.

#### What's done well

- **The single hook point genuinely pays off.** Hanging the refresh on
  `NotifySelectedMacroTargetChanged` rather than on each caller means paths nobody wrote a test for
  are still correct: deleting the bound macro from the library takes an early return at
  `DeviceDetailsViewModel.Buttons.cs:523` that skips the explicit `RefreshMacroListSelection()`
  below it — and the badge *still* clears, because `Macros.Remove(...)` fires the
  `CollectionChanged` handler at `DeviceDetailsViewModel.cs:139` → `SyncMacrosesGroupOptions()` →
  `RefreshMacroListSelection()`. I traced this expecting a stale-badge bug and found the design had
  already closed it.
- **Sizes as fractions of stage width, with a test that enforces it.** The five-board theory in
  `SelectedKeyMacroBadgeTests` is the right shape of test — it pins the *invariant* (same size on
  screen everywhere) rather than any one board's pixels, so it fails exactly when someone
  substitutes a constant.
- **The shared ✕ was extended, not forked.** Wrapping the template in a `Viewbox` and adding
  `Width`/`Height` setters keeps both original call sites byte-identical while making the visual
  reusable at a third size — which is precisely what PR #81's round 3 consolidated the template to
  allow.
- **The screenshot divergence was surfaced rather than buried**, and then pinned with a test
  against the real `RasterArtworkLayout` bounds so it is a recorded decision instead of a surprise.
- **The new tests were mutation-checked** rather than trusted for going green on the first run.

#### Verdict

Ship: **yes** — no blockers. The behaviour is correct on every path I traced, the geometry is
board-independent and enforced by test, and the unassign reuses the existing command and gate so it
cannot diverge from the two affordances already shipping. The two Should-fixes are a piece of dead
markup that makes an acceptance criterion read as met when it is not, and a process gap, not
defects in the feature. First: **1)** decide S1 (drop the tooltip and amend criterion 9 / QA step 9,
or move it), **2)** branch and open the PR (S2), **3)** show the requester the function-row
flip-below before merge, since that is the row their reference screenshot shows.


### Round 2 — requester feedback, S1 and two nits closed

The requester reviewed the running build and asked for one change:
*"label value is wrong. Remove close button I selected with red. close button on tooltip should be
inside tooltip. Look how it works for mouse."* No screenshot reached the session, so the layout was
confirmed with them directly before any code moved: **macro name only on one line, ✕ moved inside
the chip** (the mouse-hotspot shape). The "wrong label value" was the ✕ hanging outside the chip,
not the text — the macro name stays as the chip's content.

What changed:

- **The ✕ moved inside the chip.** `SelectedKeyMacroBadgeViewModel` lost the corner-overhang
  geometry (`ChipTop`, `ChipWidth`, `ChipHeight`, `UnassignLeft`, `ChipPadding`) and gained
  `ContentLeft` / `ContentWidth` / `ContentSpacing`; the chip is now the badge's whole bounding box
  and `Width = padding + text + spacing + ✕ + padding`. The *Recommended approach* section above
  records the superseded design rather than pretending it never existed.
- **[x] S1 (dead tooltip) — closed by Option A.** The unreachable `ToolTip.Tip` is gone, and
  acceptance criterion 9 plus QA step 9 no longer claim a tooltip the chip cannot show. This fell
  out of the relayout anyway: the ✕ now has to be hit-testable *inside* the chip, so the chip
  background became its own non-hit-testable `Border` with the name + ✕ in a sibling `Grid`.
- **[x] Nit (✕ overlapping the last ~2px of the name) — closed.** It cannot occur now that the ✕
  occupies its own column instead of overhanging the text.
- **[x] Nit (dead `MaxWidth` binding) — closed.** Removed from the view; `MaxTextWidth` survives as
  the computed text budget the geometry and its test use.
- **[x] Test nit — closed.** `Badge_IsHidden_ForAKeyWithNoMacro` now asserts
  `KeyOverlayBounds(key) is not null` first, so it can no longer pass because the key had no anchor.
- **New test:** `Update_FitsTheNameAndTheUnassignButtonInsideTheChip` pins the containment invariant
  the relayout introduced — padding on both sides, and room for the name beside the ✕.

Still open from Round 1: **S2** (the change is uncommitted on `main`, no branch, no PR) and the
remaining two nits (`Update(...)` could be `internal`; the double-`Viewbox` ✕ wants a visual check
during QA). Build clean; `dotnet test ./tests/ProoveGaming.App.Tests` green at **666 passed / 0
failed**.


### Round 3 — requester feedback: the label rendered as an ellipsis

Screenshot from the running build: a macro named **`tt`** in the list, and the chip on the keyboard
showing `…` instead of the name.

**Cause.** The chip's text column was sized from `LabelWidthEstimator`'s 8px-per-character
approximation (`ColumnDefinitions="*,Auto"` on a Grid whose `Width` came from the view model), with
`TextTrimming="CharacterEllipsis"` on the `TextBlock`. The estimate is an average; the instant the
real glyphs measured wider than it, trimming had nowhere to trim to and collapsed the whole name
into an ellipsis. Short names are the worst case, because their absolute slack is smallest — which
is why a two-character name showed the failure most starkly.

**Fix.** The chip now measures itself: both Grid columns are `Auto`, the container `Panel` has no
`Width`, and the background `Border` simply stretches to fill whatever the content comes to. The
view model's `Width` survives only as the estimate used to centre and clamp the chip, and both the
property and `Update` say so. `ContentLeft`/`ContentWidth` are gone (the layout no longer needs
absolute offsets); `ChipPadding` returns as the content row's margin. `MaxTextWidth` is now a real
cap rather than dead markup — it is what makes trimming mean "genuinely too long".

**Residual trade-off:** because the estimate no longer matches the laid-out width exactly, a chip
can sit a few pixels off-centre over its key, and the right-edge clamp is approximate by the same
margin. Both are cosmetic and bounded by the estimator's error; neither can hide a name.

Tests: `Update_FitsTheNameAndTheUnassignButtonInsideTheChip` became
`Update_AllowsForTheNameAndTheUnassignButtonInsideTheChip` and now asserts the estimate *allows* for
the fixed costs rather than pinning an exact layout it no longer controls; the budget and hide-state
assertions moved to `ChipPadding`/`ContentSpacing`. Build clean, **666 passed / 0 failed**.

**Not verifiable here:** that the name now renders in full is a visual property with no headless
harness behind it (see *Deliberately not automated*). It needs the QA pass — specifically a
short-named macro like `tt`, which is the case that failed.

### Round 4 — design spec applied; truncation removed by construction

The requester confirmed the ✕ was in the right place but the text was **still** cut, and supplied
the chip's design as an SVG. Two things came out of it.

**The design (196×80 SVG, `filter0_d_1331_11966`).** A `148×32` chip, `fill="black"`, **square
corners**, white text starting ~12px in, with a drop shadow of `stdDeviation 12` in
`#6357FF` at 50% alpha and **no offset** — a glow, not a shadow. The text `Quick Unique Action 1`
spans 121.1px, which pins the font at ≈12px, not the 16px this had been using. Every ratio was
re-derived from the design's pixels over the 628px host: height `0.05096`, font `0.0194`, glow
`0.0382`, ✕ `0.03185` (20px — the 28px used elsewhere does not fit inside a 32px chip).
`CornerRadiusRatio` and `MaxTextWidthRatio` are gone.

**The truncation, fixed by construction.** Two rounds were spent on width arithmetic, and both
diagnoses were wrong — a render harness proved `tt` was never trimmed at either size. So the fix
stopped being arithmetic: the name is now capped at **21 characters** by the view model
(`DisplayName`, the same rule `MacroViewModel.DisplayName` and
`DevicePartHotspotViewModel.AssignedMacroDisplayName` already use — 21 is exactly what the
design's 148px chip holds), and the view has **no `MaxWidth` and no `TextTrimming` at all**.
Nothing measures text to decide what fits, so nothing can disagree with the real glyphs. This is
the lesson of rounds 3 and 4: a width-based cap has to be right about font metrics to be safe, a
character-based cap does not have to be right about anything.

**Verified before claiming.** A throwaway Avalonia harness (scratchpad `badgeprobe`) reproduces the
chip's exact markup at real canvas sizes and renders it to PNG. It puts `Quick Unique Action 1` at
**121.1px** on screen — the design SVG's own figure to the decimal — with the chip 32px tall, and
`tt` renders in full. This is the first round where the visual claim rests on a render rather than
on reasoning.

Tests: `Update_KeepsALongMacroNameWithinTheTextBudget` became
`Update_CapsALongMacroNameByCharacterCount`, plus a new `Update_ShowsAShortMacroNameVerbatim` for
the case that was actually reported. The cross-board sizing theory now asserts 32px/12px/20px.
**667 passed / 0 failed.**

Still open: **S2** from Round 1 — everything remains uncommitted on `main`, no branch, no PR.

### Round 5 — the truncation was VERTICAL

The requester: *"зроби vertical alignment для label top. Вона знову обрізається."* That reframes
every previous round — the name was being cut **top and bottom**, not left and right, which is why
three rounds of width arithmetic never touched it. `VerticalAlignment="Center"` clips a text box at
**both** ends once it is taller than the space it is given, so the result reads as a squashed label
rather than as an overflow, and nothing about it points at a vertical cause.

Two changes, one asked for and one that removes the cause:

- `TextBlock VerticalAlignment="Top"` (as asked) — any excess now falls downward instead of being
  shaved off both ends.
- The chip's `Height="{Binding Height}"` became **`MinHeight`**. That is the actual fix: a hard
  `Height` pins a box the text must fit inside, and when it does not fit, it gets clipped. With
  `MinHeight` the chip still renders at the design's 32px for every normal name but grows rather
  than clips if the text ever needs more.

This is the same mistake as rounds 3 and 4, on the other axis: **do not pin a dimension that text
has to fit inside.** Horizontally it was fixed by capping the name by character count and deleting
`MaxWidth`/`TextTrimming`; vertically it is fixed by `MinHeight` instead of `Height`. The chip now
pins *no* dimension the content has to fit into — width is `Auto`, height is a floor.

Verified in the `badgeprobe` harness with the same change applied: `Quick Unique Action 1` renders
whole at 121.1px, chip 32px. Build clean, **667 passed / 0 failed**.

Still open: **S2** from Round 1 — uncommitted on `main`, no branch, no PR.

### Round 6 — root cause found: an inherited absolute `LineHeight`

**Cause.** `src/ProoveGaming.App/App.axaml:67-73` styles every `TextBlock` in the app with
`FontSize 16` and **`LineHeight 24`** — an absolute length, correct for 16px screen text. The badge
draws *inside the key overlay's Viewbox*, so its font is in **canvas units** (~28.5 on Rampage,
which renders as 12px on screen). Its glyphs need a ~38-unit line box; the inherited 24-unit box is
shorter, and Avalonia clips the text to it. Centred, that cut the name at top **and** bottom, which
read as a squashed label; top-aligned, it cut the bottom — exactly the two reports.

**Why three rounds missed it.** Every earlier fix targeted width, and the harness that "proved"
those fixes never loaded `App.axaml`'s styles, so it rendered a badge no app user ever saw. A
reproduction that omits the environment is not a reproduction. The harness now applies the global
style in-tree, and the numbers are unambiguous — broken: `lineHeight=24, textBoxH=24, glyphsNeed~38`;
fixed: `lineHeight=42.8, textBoxH=43`. The 1:1 renders show `asd` sliced across the bottom before
and whole after.

**Fix.** `LineHeightRatio = 24.0 / 16.0` — the app's own type ratio — applied to the badge's own
font size and bound in the view (`LineHeight="{Binding LineHeight}"`). `VerticalAlignment` returns
to `Center` (the design's vertical centring) now that the real cause is gone; `Top` was only ever a
symptom-chaser. `MinHeight` stays, on its own merits.

**The general lesson, worth carrying beyond this change:** anything drawn inside the overlay's
Viewbox is in canvas units, so **no absolute length may be inherited into it** — not `LineHeight`,
and not any future global setter in `App.axaml` that lands on a control the badge uses. Sizes here
must scale with the stage, and a global style is invisible at the call site, which is what made
this so hard to see.

Regression test: `Update_GivesTheTextALineBoxTallerThanItsGlyphs` asserts the line box exceeds the
glyph height and is not the inherited 24. **668 passed / 0 failed.**

Still open: **S2** from Round 1 — uncommitted on `main`, no branch, no PR.

### Round 7 — PR #90 review: two Majors closed

Both Majors were cases of a defence that was applied to one element and not to its neighbour.

**M1 — the macro name was still taking clicks.** The chip's `Border` was `IsHitTestVisible="False"`
and the content `Grid` is click-through because it paints no `Background`, but the name
`TextBlock` between them was left hit-testable. A `TextBlock` *does* paint — its glyph run's
rectangle is a hit target — and the chip floats over roughly 3–4 keycaps of the neighbouring row,
with the name spanning most of its width. A click on one of those keys landed on the text, which
carries no command, and bubbled up through the `Grid`, the `Panel` and the `BadgeLayer` without
ever selecting the key: a dead strip of keyboard for as long as the badge was up. This is risk #3
of the Risks section arriving in the half nobody checked. Fix: `IsHitTestVisible="False"` on the
`TextBlock`. The ✕ keeps working because it is that element's sibling, not its child — hit testing
does not descend into a disabled subtree, but a disabled peer costs a sibling nothing.

**M2 — the right-edge clamp trusted an average-glyph estimate.** `LabelWidthEstimator` averages a
character at 8px against a 16px baseline; `Update` fed that estimate to both the centring *and*
`Math.Clamp(left, 0, stageWidth - width)`. Centring tolerates the error (the plan says so, and it
is still centred from the estimate today), but the clamp does not: the chip lays itself out from
its real content, so its true right edge is `left + real`, and clamping against an underestimate
leaves it hanging `real - estimate` off the board. A name in capitals is 30–40% wider than the
average — `W` is nearer 14px than 8 — and macro names are user-typed, so `ULTRA COMBO ATTACK X` on
a right-column key put the whole ✕ past the edge, where the `Viewbox` clips it: the unassign
affordance simply disappears on exactly the keys where it is hardest to reach by other means.

Fix: `LabelWidthEstimator.EstimateUpperBound` bounds every character by the widest glyph, so the
value is a true upper bound on the laid-out chip, and only the clamp uses it (exposed as
`MaxWidth`). Measuring the text for real — the reviewer's preferred option — was rejected here, not
on principle: `FormattedText`/`TextLayout` need `FontManager.Current`, which needs an initialised
Avalonia platform, and this test project has no headless host, so every one of these geometry
tests would have to move behind one. Over-reserving costs a chip near the right edge sitting
further inside than it strictly had to, which is the only direction that is safe.

The clamp test was scored as green-by-construction and it was: it asserted `Left + Width` against
the stage, where `Left` was derived from `Width`. It now asserts against `MaxWidth`, which bounds
the real chip, and two tests join it — an all-capitals name at the right edge, and a mid-board key
proving the conservative clamp does not drag ordinary chips off their keys.

**Minors, all applied.** `RefreshSelectedKeyMacroBadge` resolves `ArtworkKeyOverlayLayout` **once**
into a local and passes it down, instead of touching the uncached property six times per refresh
and rebuilding Cyrex's / T800's LINQ dictionary each time (m1). The ✕'s position is described as
"inside the chip, beside the name" in `docs/CONTEXT.md`, the view comment and the test block header,
matching what round 2 actually built (m2). `Update` is `internal` — the round-1 nit, now closed
(m3). `MaxDisplayNameChars` is documented as what it is: 21 characters *of the name*, plus an
ellipsis when it bites, the same shape as `AssignedMacroDisplayName`'s 12 (m4).
`Badge_ClearsWhenTheDeviceChanges` asserts its key has overlay bounds before relying on the badge
being visible, so it can no longer pass for the wrong reason (m5). The bare-canvas branch of
`KeyOverlayBounds` returns null for a key with no width or height rather than an anchor with no
area (m6).

**670 passed / 0 failed** in `ProoveGaming.App.Tests`, 594 in `ProoveGaming.Infrastructure.Tests`.
