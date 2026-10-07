# Plan: fix-card-status-icons-centering

## Goal

On the home screen's wide (double) device cards — Cyrex, Rampage, T-Racer, Raster, and every
other `CardType: "double"` keyboard — the connection/battery status icons under the device name
render off-center (ticket "відображення іконок", 2026-09-02). The cause: `DoubleCard.axaml`
lays that row out on a `Canvas` at **fixed coordinates** (transport icon at `Canvas.Left=294`,
separator stroke at x=320, battery at 332, battery text at 352) tuned for the widest
combination "icon + separator + battery + percentage". Any shorter visible combination — e.g.
Cyrex wired with no battery percentage — stays pinned to those coordinates and sits left of the
card's content center (x=344 of the 42..646 band). `SingleCard.axaml` has no such bug: its row
is a `HorizontalAlignment="Center"` StackPanel that re-centers whatever is visible. Fix: make
the double card center its status row the same way — by sharing one row control between the two
cards, per the repo's "second copy triggers extraction" rule.

## Scope

**In:**
- The status row (transport icons, separators, audio-mute indicators, battery icon variants,
  battery percentage, "NO CONNECTION" text, connection tooltip) centered as a group on both
  card types, for every visible combination.
- Extracting that row into one shared view used by `SingleCard` and `DoubleCard` — the two
  near-copies already drifted (the double card's fixed-coordinate variant is the bug; it also
  overlays all three transport icons on the same coordinate, so two simultaneously visible
  transports would overlap).
- Removing the double card's tooltip hover-rectangle workaround (the row control carries the
  tooltip itself, as the single card's row already does).

**Out:**
- Any change to which icons show when (`HasWire`/`HasDongle`/`HasBluetooth`/battery display
  logic in `DeviceCardViewModel` / `HomeViewModel.ResolveTransports`) — display semantics are
  untouched; only layout/positioning changes.
- The device-details connectivity bar (`ConnectivityBarView`) — separate view, not reported,
  not touched.
- The cards' name/type labels, artwork, kebab menu — untouched.
- Localization strings — none added; the existing `Card_NoConnection` DynamicResource moves
  with the row.

## Options considered

1. **Minimal local fix** — replace only `DoubleCard`'s fixed-coordinate icon block with a
   centered horizontal StackPanel inside the canvas band (Left 42, Width 604), keeping the
   markup per-card.
   - + Smallest possible diff; single card untouched.
   - − Creates a *second* near-identical centered row (the single card already has one), which
     the repo's dedup rule explicitly forbids at the second copy; the two would keep drifting
     (this bug *is* that drift).
2. **Extract a shared row view** *(recommended)* — move `SingleCard`'s proven centered row
   verbatim into a new `CardStatusRowView` UserControl and host it from both cards; the double
   card places it across its 604-wide content band so the row centers by construction.
   - + One source of truth; the fix and the mandated extraction are the same change. The
     double card also inherits the single card's richer, correct behavior: per-pair separators
     (`HasDongleSeparator`/`HasBluetoothSeparator`) instead of one `Connected`-gated stroke,
     no same-coordinate transport-icon overlap, and audio-mute indicators (inert on keyboards:
     gated on `HasAudioIndicators`, which needs `SupportedAudioSettings`).
   - − Slightly larger diff (three files instead of one); the double card's row geometry
     changes from hand-placed pixels to flow layout — needs a careful QA eye on Rampage with
     battery percentage (the longest combination).
3. **Keep the canvas, recompute coordinates from the visible set in code-behind** — measure
   which icons are visible and shift `Canvas.Left` values.
   - − Hand-rolled layout code duplicating what a panel does natively; per-combination math
     that breaks the next time an icon is added. Rejected.

## Recommended approach

Option 2.

**Drift audit between the two existing copies** (per the dedup rule — divergences and which
side wins):

| Aspect | SingleCard (wins) | DoubleCard today | Note |
| --- | --- | --- | --- |
| Layout | centered StackPanel, `Spacing="10"` | fixed `Canvas.Left` per element | the bug |
| Transport icons | wire 14×14, dongle/BT SVG 16×16, may show side by side with 1×16 separators | same sizes but all three at the same coordinate (would overlap) | single card's is the deliberate design (its own comment says so) |
| Separators | `VerticalSeparator.svg` gated per pair + `HasBatteryIndicator` before battery | one stroked `Path` gated on `Connected` | single card's gating is correct for every combination |
| Audio indicators | present, gated on `HasAudioIndicators` | absent | harmless on keyboards (gate is false without `SupportedAudioSettings`) |
| Battery "Full" icon | `Margin="0,4,0,0"` | `Canvas.Top` 272 vs 268 (+4) | same intent, keep the margin |
| NO CONNECTION | inline in the row, `DynamicResource Card_NoConnection` | separate fixed TextBlock, same resource | inline version replaces it |
| Tooltip | on the row StackPanel (`Background="Transparent"` for hit-testing) | transparent 108×28 hover `Rectangle` hack | control carries the tooltip; rectangle deleted |

**New view** `src/ProoveGaming.App/Views/CardStatusRowView.axaml` (+ trivial code-behind, like
the section views): `x:DataType="viewmodels:DeviceCardViewModel"`, content = the single card's
row StackPanel moved **verbatim** (icons, separators, audio block, five battery variants,
battery text, NO CONNECTION TextBlock, `ToolTip.*` on the panel, `Background="Transparent"`),
`HorizontalAlignment="Center"` so it centers inside whatever width the host gives it. App-only
reuse → lives in the App project, not `ProoveGaming.Common`.

**`SingleCard.axaml`**: the inline row (lines ~218–332) is replaced by
`<views:CardStatusRowView />` in the same slot; surrounding layout untouched. Rendering must
be pixel-identical — the control *is* the old markup.

**`DoubleCard.axaml`**: delete the fixed-coordinate block (transport icons, separator stroke,
battery variants, battery text, the NO CONNECTION TextBlock at Top 268, and the hover
rectangle at 288/262) and host the control on the canvas across the full content band:
`<views:CardStatusRowView Canvas.Left="42" Canvas.Top="268" Width="604" />` — the control's
internal centering puts the visible set on the band's center (x=344), the same slot the old
row occupied vertically. *(2026-09-02, post-review: Top corrected from the drafted 266 to 268 —
the row's 16px glyphs sit flush at the panel top, so 268 lands them exactly on the old Top 268
glyph line; battery-Full's 0,4,0,0 margin reproduces the old 272.)*

No ViewModel, localization, catalog, or gRPC changes. No new NuGet, no `.slnx` edits.

## File-level change list

Ordered so the reproducing test lands first (bug-fix rule).

| # | File | Action | Intent |
| --- | --- | --- | --- |
| 1 | `tests/ProoveGaming.App.Tests/CardStatusRowTests.cs` | create | Repro-first AXAML pins (technique of `MacrosSectionRowBindingTests`): (a) `DoubleCard.axaml` contains **no** fixed-coordinate status row (no `Canvas.Left="294"` transport icon, no 288/262 hover rectangle) and hosts `CardStatusRowView` — **fails on current code**; (b) `SingleCard.axaml` hosts the same control and no longer carries an inline row (no `HasDongleSeparator` binding left in the file); (c) `CardStatusRowView.axaml` keeps the load-bearing pieces: `HorizontalAlignment="Center"`, `ToolTip.Tip="{Binding ConnectionTooltip}"`, `DynamicResource Card_NoConnection`, and the `HasAudioIndicators` gate. |
| 2 | `tests/ProoveGaming.App.Tests/ProoveGaming.App.Tests.csproj` | edit | Copy `SingleCard.axaml`, `DoubleCard.axaml`, `CardStatusRowView.axaml` to test output (`<None Include>` items, mirroring the existing MacrosSectionView entry). |
| 3 | `src/ProoveGaming.App/Views/CardStatusRowView.axaml` | create | The shared status row: single card's row markup verbatim, centered, tooltip on the panel, audio indicators gated, NO CONNECTION inline. |
| 4 | `src/ProoveGaming.App/Views/CardStatusRowView.axaml.cs` | create | Trivial `UserControl` code-behind (`InitializeComponent`), `internal`, mirroring sibling views. |
| 5 | `src/ProoveGaming.App/Views/SingleCard.axaml` | edit | Replace the inline row with the shared control; everything else untouched. |
| 6 | `src/ProoveGaming.App/Views/DoubleCard.axaml` | edit | Delete the fixed-coordinate row + NO CONNECTION TextBlock + hover rectangle; host the control at `Canvas.Left="42" Canvas.Top="268" Width="604"`; update the stale coordinate comment (lines ~191–201) to describe the centered row. |
| 7 | `docs/CONTEXT.md` | edit | One-line glossary entry for **Card status row** (the shared `CardStatusRowView`), since a new named component appears. |

## Acceptance criteria

- [ ] Given a Cyrex (wired keyboard, no battery percentage shown) connected by cable, when the
  Devices page renders its card, then the visible icon group (wire icon + separator + battery
  icon) is horizontally centered on the card — its group center aligns with the centered
  "CYREX" name above it, not left of it.
- [ ] Given a Rampage connected via 2.4G dongle with a battery reading, when its card renders,
  then the full combination (dongle icon, separator, battery icon, percentage text) is
  centered as a group on the card.
- [ ] Given any double card with no connection, when it renders, then "NO CONNECTION" is
  centered (unchanged from today) and no icons show.
- [ ] Given any single card (BLAZE, TAIPAN, Cosmo…), when it renders, then its status row is
  visually unchanged from before this fix (same icons, sizes, spacing, tooltip).
- [ ] Given a connected card of either type, when the pointer hovers over the icon row, then
  the connection tooltip ("Connected via cable" / dongle / Bluetooth) appears below the row —
  on the double card too, where the old invisible hover rectangle is gone.
- [ ] Given a headset card with audio indicators (muted output or mic), when it renders, then
  the mute icons still show in the row (single card unchanged); keyboard cards show no audio
  icons.
- [ ] Given the app language switched to Ukrainian at runtime, when a disconnected card
  renders, then the no-connection text shows the uk resource (DynamicResource wiring survived
  the move).
- [ ] `CardStatusRowTests` pass, and pin (a) fails when run against the pre-fix
  `DoubleCard.axaml` (repro-first requirement).
- [ ] Full Release suite green: `dotnet test ./src/ProveGaming.slnx -c Release`.

## Test strategy

Bug fix (`fix-*`) — the reproducing test comes first:

- **`CardStatusRowTests` (new, `tests/ProoveGaming.App.Tests`)** — AXAML-pinning tests in the
  established `MacrosSectionRowBindingTests` style (read the view file from test output).
  Written before the view change; pin (a) — "DoubleCard hosts the shared centered row, not
  fixed coordinates" — **fails on the current code** and is the repro. Pins (b)/(c) guard the
  extraction against re-inlining and against losing the tooltip / localization / audio-gate
  wiring in the move.
- **Existing suites that must stay green (VM layer, untouched):**
  `DeviceCardConnectionTooltipTests` (tooltip text the row binds),
  `DeviceCardAudioIndicatorTests` (`HasAudioIndicators` gate),
  `DeviceCardLanguageRefreshTests` (`ConnectionTooltip`/label refresh),
  `HomeViewModelTransportPriorityTests` (display-flag semantics the icons bind).

Deliberately **not** covered by automated tests: actual pixel positions and the rendered
centering itself — no Avalonia headless harness exists in the repo (same declared gap as every
view change); covered by the manual QA steps. The AXAML pins assert structure, which is the
strongest signal available at unit level.

## QA (manual test steps)

1. Launch the Proove Gaming app with the Cyrex keyboard connected by USB cable. Look at the
   Cyrex card on the Devices page.
   **Expected:** the small icons under the name "CYREX" (cable icon, thin divider, battery
   icon) sit centered under the name — the middle of the icon group lines up with the middle
   of the name text, not to the left of it.
2. Hover the mouse over those icons.
   **Expected:** a tooltip like "Connected via cable" appears just below them.
3. Connect the Rampage keyboard via its 2.4G dongle and wait for the battery percentage to
   appear on its card.
   **Expected:** the whole group — dongle icon, divider, battery icon, percentage — is
   centered under the name "RAMPAGE".
4. Unplug the Rampage so its card shows no connection.
   **Expected:** the "NO CONNECTION" text is centered on the card, no icons remain.
5. Check the small cards (a mouse such as BLAZE or TAIPAN, and a headset such as Cosmo).
   **Expected:** they look exactly as before this fix — same icons, same sizes, same spacing;
   the tooltip still appears on hover.
6. If a headset is connected, mute its microphone in Windows and look at its card.
   **Expected:** the red mic-off icon still appears in the icon row, as before.
7. Switch the app language to Ukrainian (Profile → language) and look at a disconnected card.
   **Expected:** the no-connection label shows in Ukrainian, still centered.

## Review focus

- `localization` — the `Card_NoConnection` DynamicResource must survive the move (a VM string
  here shipped untranslated once before); no new strings expected.
- `null-safety` — the row binds plain card properties on its own DataContext (the card VM);
  verify no binding acquired a null-prone path in the move (no `Device.` prefix here, unlike
  the details page).

## Performance & quality

None — static XAML bound to existing observable properties; no timers, no per-frame work, no
allocations on any hot path, no new subscriptions or disposables. The extraction removes ~110
lines of duplicated markup and one hit-test workaround.

## Risks

- **Double-card pixel shifts.** The row moves from hand-placed coordinates to flow layout; the
  vertical slot must be checked visually against the pre-fix rendering. *(2026-09-02: resolved
  by arithmetic in review Round 1 — the host now sits at Canvas.Top 268, which puts the flush
  16px glyphs on the old Top 268 line, so no vertical drift is expected; QA step 1/3 confirms.)*
  The horizontal change is the point of the ticket.
- **Single-card regression.** Mitigated by moving the markup verbatim into the control; the
  single card's rendering is the reference the shared control is defined by. The AXAML pins
  guard the wiring, QA step 5 guards the pixels.
- **Hover-area change on double cards.** The old tooltip rectangle was a fixed 108×28 zone;
  now the hover target is the actual row bounds — slightly smaller when few icons show.
  Accepted: it matches the single card's behavior exactly.
- **Overlap latent bug disappears silently.** Today two simultaneously-true transport flags
  would overlap at one coordinate on double cards; after the fix they render side by side with
  a separator. That is a behavior improvement, but if QA compares screenshots pixel-for-pixel
  on a multi-transport keyboard they will see a (correct) difference — called out here so it
  is not mistaken for a regression.

## Open questions

None — the ticket asks exactly for centered icons; display semantics are unchanged.

## Review findings

### Round 1 — Ship: yes

Reviewed 2026-09-02. Delta = uncommitted working tree vs HEAD (the branch's PG-647 commit is
out of scope, reviewed separately). Verification: the moved row was diffed mechanically against
`git show HEAD:src/ProoveGaming.App/Views/SingleCard.axaml` (whitespace-normalized) — the only
difference is one comment word ("matching DoubleCard." dropped), so the **verbatim-move
requirement holds**. `CardStatusRowTests` re-run independently: 3/3 pass; orchestrator confirmed
Release build clean, Release suite 2470/2470, and the repro pin failing against pre-fix
`DoubleCard.axaml`.

#### Plan conformance

- Implemented as planned: all 7 file-level items — repro-first `CardStatusRowTests` (pins a/b/c
  exactly as specified), csproj `<None Include>` copies mirroring the MacrosSectionView entry,
  `CardStatusRowView.axaml` (verbatim row, centered, tooltip on the panel), `internal` trivial
  code-behind matching sibling views (`MacrosSectionView.axaml.cs`), SingleCard row replaced
  in-slot, DoubleCard fixed-coordinate block + NO CONNECTION TextBlock + hover rectangle deleted
  and the control hosted at `Canvas.Left="42" Canvas.Top="266" Width="604"`, `docs/CONTEXT.md`
  glossary entry for **Card status row**.
- Drift audit honored: every divergence resolved to the SingleCard side as the table specifies
  (per-pair separators, no icon overlap, audio block gated on `HasAudioIndicators`, battery-Full
  margin, inline NO CONNECTION, tooltip on the row).
- `x:DataType="viewmodels:DeviceCardViewModel"` is compatible with both hosts: `SingleCardViewModel`
  and `DoubleCardViewModel` both derive from `DeviceCardViewModel`; all bindings are plain
  non-null card properties (`ConnectionTooltip` returns a non-null string, `BatteryColor` a
  non-null brush) — the null-safety review focus is satisfied, no `Device.`-prefixed path
  appeared in the move.
- Localization review focus satisfied: `{DynamicResource Card_NoConnection}` survived the move
  (pinned by test c); no new strings; the disconnected tooltip falls back to the pre-existing
  `Card_Connection_None` resource.
- Accepted limitation (per plan): hover target is now the actual row bounds instead of the fixed
  108×28 rectangle — residual risk: smaller hover zone with few icons; matches SingleCard by
  design. Related observation: the old rectangle was gated on `Connected`, so a disconnected
  double card now shows a tooltip (localized "not connected" text) on hover where it previously
  showed none — again identical to SingleCard's existing behavior, not a regression.
- Deviations / missing: none found.

#### Code

- [x] **S1. `statusText` style stays duplicated in both hosts while its only consumer moved into
  the shared control** — `src/ProoveGaming.App/Views/SingleCard.axaml:51`,
  `src/ProoveGaming.App/Views/DoubleCard.axaml:52`, consumer at
  `src/ProoveGaming.App/Views/CardStatusRowView.axaml:122` — Should-fix
  **Problem / why it matters:** the NO CONNECTION TextBlock inside `CardStatusRowView` gets its
  `#B2BEBCEA` foreground only via Avalonia style cascade from whichever card view hosts it. The
  extraction split the style from its consumer across files: neither host file now contains any
  element with `Classes="statusText"`, so the style looks dead in both and a cleanup pass (or a
  future third host that doesn't copy it) silently renders NO CONNECTION with the default
  foreground — no build error, no exception. This is exactly the second-copy drift the plan's
  own dedup rule targets; today the copies are still value-identical (`#B2BEBCEA` in both).
  **Fix:** move the base `Style Selector="TextBlock.statusText"` into
  `CardStatusRowView.axaml`'s own `<UserControl.Styles>` and delete the two host copies, making
  the control self-contained. Caveat: SingleCard also carries an
  `UserControl.inactive TextBlock.statusText` override (`SingleCard.axaml:54`) which
  DoubleCard's own comment records as never matching — verify that claim once (styles nearer
  the element win, so a base style inside the control would outrank the ancestor override) and
  either delete the dead override with the move or keep the base style in the hosts as-is with
  a pointer comment. Acceptable as a follow-up; rendering today is correct.

#### Tests

- No gaps found. The three pins are real: pin (a) uses a same-element regex (so a stray
  `Canvas.Left` elsewhere can't satisfy it) and was confirmed failing against the pre-fix
  `DoubleCard.axaml`; pins (b)/(c) guard against re-inlining and against losing the tooltip /
  `Card_NoConnection` DynamicResource / `HasAudioIndicators` gate in the move. The plan's
  declared gap (no rendered-pixel assertions — no headless Avalonia harness in the repo) stands
  and is covered by the QA steps. VM-layer suites the row binds against are untouched and green
  in the orchestrator's Release run.

#### Nits

- The new DoubleCard comment claims "Top 266 + the row's internal metrics keeps the glyphs on
  the old Top 268 line" — `src/ProoveGaming.App/Views/DoubleCard.axaml:186-188`. Arithmetic
  disagrees: the row's tallest children are 16px (transport SVGs, battery text at
  `LineHeight="16"`), so the StackPanel is 16px tall and the 16×16 glyphs sit flush at y=266 —
  2px above the old 268. The plan pre-accepts a 1–2px vertical drift as the likely nit pending
  QA step 1/3; if QA confirms the shift is visible, change `Canvas.Top` to `268`, and either
  way correct the comment so it doesn't assert a compensation that isn't there.
- DoubleCard's surviving `statusText` style comment (`DoubleCard.axaml:47-51`, "'NO CONNECTION'
  label, same as the mouse card…this label only appears while disconnected") still narrates a
  label that no longer lives in this file — stale after the move; folds into the S1 cleanup.

#### What's done well

- The verbatim-move requirement was actually met — a normalized diff of the 115-line row against
  the HEAD SingleCard shows zero markup changes, which is what makes the "single card is
  visually unchanged" acceptance criterion credible without pixel tests.
- The repro-first pin is well-engineered: same-element regex for the host, plus negative
  assertions on the exact fixed coordinates that were the bug (294/295, the `M320 268V284`
  stroke, the 288/262 rectangle).
- DoubleCard's replacement comment preserves the archaeology (which coordinates were where and
  why they were wrong), so the next reader doesn't need `git log` to understand the band math.
- No leftover debris: both hosts still use their `converters:`/`common:` namespaces elsewhere,
  the code-behind matches the sibling-view `internal partial` convention, and the CONTEXT.md
  entry follows the glossary format.

#### Verdict

Ship: yes — no blockers; the fix is exactly the planned extraction, mechanically verbatim, with
real repro-first pins and both review-focus areas (localization, null-safety) clean. S1 is a
maintainability follow-up, not a correctness issue. First: 1) QA steps 1/3 with an eye on the
2px vertical drift (then fix or vindicate the Top-266 comment), 2) S1 — move the `statusText`
base style into the shared control, 3) fold the stale DoubleCard style comment into that same
cleanup.
