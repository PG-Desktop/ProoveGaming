# Lazy keyboard overlay construction (Details-open, phase 2)

## Goal

Opening Device Details for a keyboard still spends ~430 ms (Debug, Rampage) between the
view-visibility flip and the first rendered frame — measured by the `[NavPerf]` log added in
[fix-details-navigation-delay](../fix-details-navigation-delay/fix-details-navigation-delay-plan.md).
The cost is the one-shot canvas construction in all four live `RasterKeyboardOverlayView`
instances (three section overlays bound to `PhysicalKeys`, one dialog picker bound to
`RemapTargetKeys`), each building ~100 styled `Button`s — even though **none of them is visible
when the page opens** (the initial section is Presets; the overlays live in the Key Assignment,
Macros and Actuation sections and in the Key Assignment dialog). This plan makes the overlay
rebuild lazy: an invisible overlay only marks itself dirty, and builds its canvas the first time
it actually becomes visible.

## Scope

**In scope**

- `RasterKeyboardOverlayView` only: gate the (already coalesced) rebuild on effective
  visibility; rebuild on the first layout pass after the overlay becomes visible while dirty.

**Out of scope**

- Deferring the population of `AssignmentTargets` / `RemapTargetKeys` / `SystemCommandTargets`
  in the ViewModel (see Options — rejected for this phase).
- Restructuring `DeviceDetailsView.axaml`'s duplicated section trees or replacing the
  always-instantiated sections with lazy `ContentControl`s (bigger refactor; revisit only if
  the numbers after this phase still warrant it).
- `RgbKeyboardView` and the mouse hotspot views (no `CollectionChanged`-driven rebuild there).
- Removing the `[NavPerf]` instrumentation (separate decision before merge).

## Options considered

**A. Status quo (coalesced but eager).** The previous fix already collapsed the O(n²) rebuild
storm into one posted rebuild per instance. *Pros:* done. *Cons:* still four ~100-button canvas
builds on every device open and every preset switch, all for invisible overlays — the measured
~430 ms of first-frame cost.

**B. Visibility-gated dirty rebuild (recommended).** `QueueRebuild` skips the post when
`!IsEffectivelyVisible` and sets a dirty flag; a `LayoutUpdated` handler (O(1) flag check)
triggers the queued rebuild on the first layout pass in which the overlay participates while
dirty. *Pros:* view-local, ~20 lines, zero canvas work on open (all four overlays are hidden on
the Presets section), preset switches on keyboards get the same win; the cost moves to the first
entry into a section that actually shows the overlay (~one 100-button build, imperceptible
against a section switch). *Cons:* one frame may render the artwork without its overlay boxes on
first section entry — invisible in practice on artwork boards (the buttons are transparent
highlight boxes) and a single frame on abstract-canvas boards.

**C. Defer VM collection population to dialog open.** Rejected for this phase:
`AssignmentTargets` is consumed outside the dialog (the macro-assignment flow appends and
resolves macro rows via `RebuildMacroAssignmentTargets`), so deferring it risks regressing the
Macros-page assignment and resync-highlight paths for a modest prologue saving (~142 ms Debug,
much less in Release). With option B, the dialog picker's canvas cost is already deferred
anyway — the remaining VM `Add` loops are cheap object construction.

**D. One shared overlay instance re-parented between sections.** Removes duplicate instances
entirely. Rejected: re-parenting visuals across three sections and a dialog is a layout-and-
lifetime minefield for the same end result option B reaches with a flag.

## Recommended approach

Option B, entirely inside `RasterKeyboardOverlayView.axaml.cs`:

1. Add `private bool _rebuildDirty;`.
2. `QueueRebuild()` becomes: if `!IsEffectivelyVisible` → set `_rebuildDirty = true` and return
   (no dispatcher post); otherwise post the coalesced rebuild as today. `RebuildKeyboard()`
   clears `_rebuildDirty`.
3. Route `Rebind()`'s direct `RebuildKeyboard()` call through `QueueRebuild()` too, so the
   initial `DataContextChanged` (fired while the whole Details view is still hidden) no longer
   builds four canvases at startup either.
4. Subscribe `LayoutUpdated += OnLayoutUpdated` in the constructor (self-subscription — the
   handler and the target are the same object, so no leak and no unsubscribe needed). The
   handler is a guard-first no-op: `if (!_rebuildDirty || !IsEffectivelyVisible) return;` then
   `QueueRebuild()`. `LayoutUpdated` fires only for layout passes that include this control, so
   a hidden overlay stays silent; the first pass after its section (or the dialog) becomes
   visible triggers the one real build.
5. `IsEffectivelyVisible` (not `IsVisible`) is required: three of the four instances are hidden
   by *ancestor* panels (section switches, the dialog panel), not by their own `IsVisible`.

Interaction with existing behavior, verified against the current file:

- `OnKeyPropertyChanged` (selection highlight) guards with `_buttonByKey.TryGetValue`, so
  selection changes arriving while the canvas is stale are safely dropped; the rebuild re-reads
  `key.IsSelected` when it runs, so the highlight is correct on first show.
- Matrix-probe updates mutate `MatrixIndex` on existing VMs without `CollectionChanged` — no
  rebuild needed, unchanged.
- The hover tip layer is populated per rebuild — unchanged. *(2026-08-14: originally "hover tip
  and badge layers"; the badge layer was removed by
  [`remove-macro-list-selection`](../remove-macro-list-selection/remove-macro-list-selection-plan.md),
  which also makes the QA steps below that expect an on-key macro badge read as the hover
  tooltip instead.)*

## File-level change list

- `src/ProoveGaming.App/Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml.cs` —
  add `_rebuildDirty`, visibility gate in `QueueRebuild`, `LayoutUpdated` trigger, route
  `Rebind` through `QueueRebuild`; update the coalescing comment to describe the laziness.
- `docs/plans/refactor-lazy-keyboard-overlays/refactor-lazy-keyboard-overlays-plan.md` — this
  plan.

No new NuGet packages, no proto, no installer impact, no DI changes.

## Acceptance criteria

- [x] Given a keyboard (e.g. Rampage) on the Home view, when the user opens Device Details,
  then the `[NavPerf]` `firstFrame` figure drops materially versus the current baseline
  (568 ms Debug on the reference machine; expected well under ~300 ms — the four overlay
  builds leave the open path entirely). **Measured (Debug, Rampage, three consecutive
  opens, RequestAnimationFrame-based metric): firstFrame = 111 / 215 / 131 ms** — versus
  2145 ms of fully blocking navigation before this work began.
- [ ] Given the Details page just opened on Presets, when the user switches to the Key
  Assignment section, then the keyboard overlay renders with all keys clickable and the
  previously selected key (if any) highlighted — no permanently blank keyboard.
- [ ] Given the Macros section and the Actuation section, when each is entered for the first
  time, then their keyboard overlays render correctly (same keys, badges/tooltips working).
- [ ] Given the Key Assignment dialog, when it is opened, then the KEYS-tab picker renders all
  keys and clicking one still picks the remap target.
- [ ] Given a device switch (keyboard A → Home → keyboard B), when the user enters Key
  Assignment on B, then the overlay shows B's layout, never A's.
- [ ] Given `dotnet test ./src/ProveGaming.slnx`, then all tests pass unchanged.

## Test strategy

Change kind: behavior-preserving performance refactor (`refactor-*`) of **view** code.

- **Existing coverage that must stay green:** the full App suite (769) — in particular
  `DeviceDetailsKeyAssignmentDialogTests`, `DeviceDetailsKeyboardMacroBindingTests`,
  `ArtworkKeyOverlayGateTests`, and `DeviceDetailsSetDeviceThreadingTests`. These exercise the
  ViewModel collections the overlay consumes; the overlay itself is code-behind.
- **Deliberately not covered by automated tests:** `RasterKeyboardOverlayView` has no unit
  tests today and the repo has no headless-Avalonia rendering rig; visibility-gated canvas
  construction is inherently a layout-pass behavior. Verification is via the `[NavPerf]`
  measurement plus the manual QA checklist below. Adding a headless UI test rig is out of
  scope.
- No new VM-level behavior is introduced, so no new VM tests are required.

## QA (manual test steps)

1. Connect a supported keyboard (e.g. Rampage) and launch the app.
2. From the Home screen, click the keyboard's card. Expected: the Details page appears
   instantly on the Presets section, with the keyboard picture shown; no freeze.
3. Switch to the Key Assignment section. Expected: the on-screen keyboard appears immediately
   with all keys; hovering a key shows its name tooltip; clicking a key opens the assignment
   dialog with that key's name in the title.
4. In the dialog's KEYS tab, click a key on the picker keyboard. Expected: the remap target is
   picked exactly as before.
5. Close the dialog, switch to Macros. Expected: the keyboard overlay appears; assigning a
   macro to a key still works and shows the macro badge on that key.
6. Switch to Actuation (if the board supports it). Expected: the overlay appears and per-key
   selection works.
7. Go Back, open a different keyboard (if available), and enter Key Assignment. Expected: the
   second keyboard's own layout is shown, not the first one's.
8. Switch between two presets on the keyboard. Expected: the switch feels as fast as or faster
   than before; key assignments shown match the selected preset.

## Review focus

- `performance hot-path` — the `LayoutUpdated` handler must be a guard-first O(1) no-op
  (it fires on every layout pass that includes the control).
- `UI-thread affinity` — all rebuild scheduling stays on the dispatcher; no new threading.
- `resource disposal` — the `LayoutUpdated` self-subscription must not pin foreign objects
  (it doesn't: handler and target are the same instance).
- `null-safety` — dirty-path interleavings (`DataContext` swapped, keys collection replaced)
  must keep the existing null guards intact.

## Performance & quality

- Hot path: the `LayoutUpdated` handler — two boolean checks in the common case; no
  allocations. The rebuild itself is unchanged O(n) and now runs at most once per
  becoming-visible transition per mutation batch.
- No allocations added on the open path; the open path *loses* four canvas builds
  (~400 buttons + their text blocks and style application).
- No shared-state or async changes; everything stays on the UI thread.

## Risks

- **One-frame overlay gap on first section entry:** on abstract-canvas boards (no artwork) the
  keys carry their own labels, so a single frame may show the bare canvas before the posted
  rebuild lands. On artwork boards the overlay is invisible highlight boxes — no visible gap.
  If QA finds the frame noticeable, the fallback is to rebuild synchronously from the
  `LayoutUpdated` handler instead of posting (trade: a one-off ~100-button build inside a
  layout tick).
- **`IsEffectivelyVisible` timing:** the value must be current during `LayoutUpdated`; if an
  edge case surfaces where the event fires before visibility propagates, the symptom is a
  still-dirty overlay that heals on the next layout pass — degraded, not broken.
- **Missed rebuild path:** any future code that mutates the key collections *and* expects an
  immediate synchronous canvas rebuild would now observe a deferred one; the existing
  consumers (selection sync, probe updates) were checked and do not.

## Open questions

- None blocking. (The `[NavPerf]` log lines were **stripped before merge** after confirming the
  measured result recorded in Acceptance criteria; re-add the instrumentation from this plan's
  history if a navigation-latency regression is ever reported.)

## Review findings

### Round 1 — Ship: yes

#### Plan conformance
Plan: `docs/plans/refactor-lazy-keyboard-overlays/refactor-lazy-keyboard-overlays-plan.md` (this file)
- Implemented as planned: all five design steps land in
  `src/ProoveGaming.App/Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml.cs` —
  `_rebuildDirty` (line 108), visibility gate in `QueueRebuild` (112–116), `LayoutUpdated`
  self-subscription (63) with guard-first handler (135–140), `Rebind` routed through
  `QueueRebuild` (89), comment updated. No other files touched; diff maps 1:1 to the plan.
- Deviates from plan (coder-flagged, in the plan's spirit, verified sound): the posted rebuild
  re-checks `IsEffectivelyVisible` and re-marks dirty if the overlay hid between queue and tick
  (125–129), and `RebuildKeyboard` clears the dirty flag on entry (146) so an already-satisfied
  mark can't schedule a wasted second build. Both close flag/queue consistency gaps the plan
  did not enumerate.
- Acceptance criteria: `dotnet test` criterion met (769 App + 643 Infrastructure green). The
  `[NavPerf]` `firstFrame` criterion and the five visual criteria (section overlays, dialog
  picker, device switch) are **pending the user's manual run** — not verifiable in review.

#### Code
- none (no Blockers, no Should-fix). Scenarios verified by hand against the full file:
  - No rebuild loop: the post-build `LayoutUpdated` fires with `_rebuildDirty == false` → no-op.
  - Hidden-mutation → show: dirty set (no post), first layout pass after visibility flips
    queues exactly one build; `IsEffectivelyVisible` is current by event time (event fires
    after the pass that applied the visibility change).
  - Logic is correct under **either** `LayoutUpdated` semantics (per-control-arrange or
    WPF-style global-once-subscribed) — the guard-first handler makes the difference purely a
    matter of how often a two-boolean no-op runs.

#### Tests
- none required beyond the existing suites (per plan: view code, no headless rig; suites
  green). Reminder recorded: CI still does not run `tests/` (pre-existing repo-wide gap).

#### Nits
- Stale content is retained while dirty-and-hidden: after a device switch A→B, the first
  layout tick of a newly shown section briefly hosts A's (transparent) highlight boxes and
  A's key VMs stay pinned via `_buttonByKey` + `PropertyChanged` subscriptions until the
  posted rebuild lands — `RasterKeyboardOverlayView.axaml.cs:150–153`. Unobservable on every
  current board (all six ship artwork, so the boxes are invisible) and bounded (~100 objects,
  one tick); if a future artwork-less board joins the catalog, consider clearing the canvas
  and unsubscribing when marking dirty-while-hidden.
- The constructor comment's claim that "hidden overlays are excluded from layout, so the
  event stays silent" (59–62) overpromises if Avalonia's `LayoutUpdated` is global-per-pass
  for subscribers; behavior is correct either way, but the comment could soften to "the
  guard makes hidden-time firings a two-boolean no-op".
- The `Stage`/`ArtLayer` sizes set only inside `RebuildKeyboard` mean one frame at the
  previous board's viewBox size on first show after a device switch — same one-tick window
  as above, cosmetic at worst.

#### What's done well
- The dirty/queued flag pair is kept consistent at every transition (queue-time, tick-time,
  build-time, layout-time) — each path was checkable in isolation.
- Robustness against uncertain `LayoutUpdated` semantics instead of betting on one reading
  of the framework.
- Measurement-first discipline: the change targets exactly what `[NavPerf]` attributed
  (~430 ms of hidden-overlay builds), not a guess.

#### Verdict
Ship: yes — pending the user's `[NavPerf]` confirmation and the plan's 8-step manual QA;
no code findings. First: (1) collect the new NavPerf figures on Rampage, (2) run QA steps
3–7 (each overlay's first-entry render), (3) decide keep-vs-strip for the `[NavPerf]` log
lines before merge.
