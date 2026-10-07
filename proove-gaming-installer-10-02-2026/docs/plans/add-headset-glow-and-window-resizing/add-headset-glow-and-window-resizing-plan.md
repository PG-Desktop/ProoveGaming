# Plan: Headset glow animation, and a window that fits the screen

Written after the fact (2026-08-27) — the work was done conversationally and this records what
shipped, why, and how to regenerate the parts that are generated. Two unrelated pieces of pre-release
visual work, kept in one document because they landed together.

## Goal

1. **Headset glow.** Cosmo and Starlight ship artwork with RGB light regions drawn in. Animate those
   on the device details page with the same travelling gradient BLAZE's Wave effect uses, always on,
   with no user-facing RGB controls.
2. **Window fits the screen.** The app opened at 1440x900 with a 1200x880 minimum. On a small display
   — or, far more commonly, at 150-200 % Windows display scaling — the window is larger than the
   screen can show and the title bar, with minimise and close, ends up off-screen with no way back.

## Scope

**In** — the two headsets' details pages; window sizing, decorations and content scaling for the App.

**Out** — any host-controllable lighting for the headsets (they have RGB in hardware and nothing we
can address; `SupportsRgb: false` stays). Making the layout genuinely fluid: it is built on fixed
dimensions and the scaling below is what makes small screens usable without that rewrite. The
Installer and Uninstaller keep `WindowDecorations="None"` — fixed-size wizards, unaffected.

## Established facts (verified, do not re-derive)

1. **Avalonia renders SVG but does not run its CSS.** The supplied artwork declares
   `--headphones-rgb-color` and a Gaussian filter; neither animates at runtime, so the SVG's own
   glow would paint as a frozen fill.
2. **The artwork's draw order is `rgb-solid` → photo → `rgb-blur`.** Read from byte offsets in the
   source files. The crisp core belongs UNDER the device (light escaping around the ear cup) and the
   blurred halo OVER it. Both behind leaves only a thin line visible beneath the device, because the
   photo is opaque where the glow sits.
3. **The device-only bitmap rasterizer re-frames artwork.** It renders into a fixed 934x1040 canvas,
   drawn at the top-left. Those constants suit BLAZE (fills to within a pixel). Starlight landed in
   the top 67 % with 340 px of dead space beneath, so the picture no longer occupied its own viewBox
   and the glow — whose geometry is in that viewBox — rendered off the device.
4. **`WindowDecorations="None"` removes the resize borders.** `CanResize="True"` was already set and
   had nothing to act on. `BorderOnly` keeps the frame (and its grips) without a system title bar.
5. **Windows 11 rounds and outlines the frame through DWM.** `CornerRadius="0"` does not affect it —
   that is Avalonia painting its own content; both effects belong to the OS frame around it.

## Recommended approach

### Glow

Split each artwork in two: a `-Vector.svg` with the glow paths REMOVED (used as `RgbImagePath`, so
`DeviceDetailImagePath` picks it up for details while the home card keeps its PNG), and the geometry
lifted into `DeviceGlowArtCatalog` as Avalonia paths filled with the existing `AnimatedGlowBrush`.
Two layers straddle the artwork in the view, matching fact 2.

`HasDecorativeGlow` is a SEPARATE gate from `IsRgbSupported`, deliberately: everything downstream of
that flag means "the user can change this device's lighting", and turning it on for a headset would
offer controls that cannot do anything.

`IsDecorativeGlowVisible` additionally requires the decoded artwork, so the glow cannot appear before
the picture it sits on — vector geometry paints on the first frame, a multi-megabyte SVG does not.

### Window

`BorderOnly` plus two DWM attributes (`DWMWA_WINDOW_CORNER_PREFERENCE` = `DONOTROUND`,
`DWMWA_BORDER_COLOR` = `COLOR_NONE`) — a package: keeping the frame is what makes resizing work, and
the two attributes are what keep it square and invisible.

`FitToScreen` clamps to the working area on open and lowers `MinWidth`/`MinHeight`, which are a floor
the user cannot drag below. The fit re-runs, shrink-only, whenever the room the window lives in
changes — it moved to another monitor, or the monitor changed under it (display scale, resolution,
taskbar). See the 2026-10-01 note for what each of those needed.

The root sits in a `LayoutTransformControl`: below the design size the whole UI scales down
uniformly. It measures its child through the INVERSE transform, so at 0.76 a 1280 px window still
hands the layout its full 1200 px of logical width — nothing is cramped, just drawn smaller. Above
the design size the scale is exactly 1 and the control is inert.

## File-level change list

- `src/ProoveGaming.App/Models/DeviceGlowArt.cs` — **new, generated.** Glow geometry per device.
- `src/ProoveGaming.App/Assets/Headphones/{Cosmo,Starlight}-Vector.svg` — **new, generated.**
- `.../DeviceDetailsViewModel.Rgb.cs` — decorative-glow properties; aspect-preserving rasterisation;
  the wave runs for these devices though `IsRgbSupported` is false.
- `.../DeviceDetailsViewModel.cs` — republish the glow properties on device change.
- `.../Views/DeviceDetails/DeviceDetailsView.axaml` — the two glow layers.
- `src/ProoveGaming.DeviceHost/appsettings.json` — `RgbImagePath` for both headsets.
- `src/ProoveGaming.App/Views/MainWindow.axaml` — `BorderOnly`; root wrapped in `LayoutTransformControl`.
- `src/ProoveGaming.App/Views/MainWindow.axaml.cs` — frame appearance, fit-to-screen, root scale.
- `tests/ProoveGaming.App.Tests/LayoutRuleTests.cs` — **new.**

## Regenerating the generated files

Both `-Vector.svg` files and `DeviceGlowArt.cs` are derived from the designer's source SVGs. Those
sources are NOT in the repo: they are ~7.7 MB, `<AvaloniaResource Include="Assets\**" />` embeds
everything under `Assets/`, and nothing referenced them — they shipped in the app and the installer
for no reason. Ask the designer for the current files when regenerating.

Given a source `PG_<Device>_Headphones_SVG_<colour>.svg`:

1. **Artwork** — copy it, delete every `<path id="rgb-solid…">` and `<path id="rgb-blur…">`, save as
   `<Device>-Vector.svg`. Keep the `<image>` and the `<defs>`; leave the viewBox alone, since the
   glow geometry is expressed in it.
2. **Geometry** — take each `d` attribute from those deleted paths into `DeviceGlowArtCatalog`:
   `rgb-solid*` become `LayersBehind` (blur 0), `rgb-blur*` become `LayersOver` (blur = the file's
   `stdDeviation` x3 — Avalonia's `BlurEffect.Radius` is not SVG's `stdDeviation`).
   `StrokeThickness` is 1.5 % of canvas width, which is what widens the band without moving it.
3. `CanvasWidth`/`CanvasHeight` are the source viewBox.
4. Add the new `-Vector.svg` path to `DeviceGlowArtCatalog.ArtworkPaths`, or the rasterizer will
   re-frame it and the glow will silently go back off the device.

## Acceptance criteria

- Opening Cosmo or Starlight details shows an animated travelling gradient on the ear cups, sitting
  ON the device, every time, with no interaction.
- Neither headset shows an RGB section, colour picker or Save Preset for lighting, and nothing is
  written to the hardware.
- The artwork appears before or with the glow, never after it.
- The home card keeps its PNG artwork at its catalog `ImageScale`.
- The window can be dragged to any size at or above the screen-adjusted minimum, with square corners
  and no visible frame border.
- At 150 % and 200 % display scaling the window opens fully on-screen with the title bar, minimise
  and close reachable.
- The window's OUTER edges — frame included — stay inside the screen's working area: nothing sits
  past the right edge or under the taskbar, and the resize grips on every side are reachable
  (PG-683).
- No device artwork crosses the edge of the panel it is drawn in, at any window size down to the
  design minimum — the board previews cap at 628 and scale down inside a 42px inset below that
  (PG-683).
- Dragging the window to a smaller or higher-scaled monitor shrinks it to fit rather than overflowing.
- A window at or above 1200x880 renders at scale 1.0 — unchanged from before this work.

## Test strategy

`tests/ProoveGaming.App.Tests/LayoutRuleTests.cs` covers the pure rules, all of which fail silently:
`ResolveRootScale` (never above 1, never below the 0.5 floor, tighter dimension wins, degenerate
sizes), `AspectPreservingSize` (aspect preserved both orientations, long side carries resolution,
never zero), and `DeviceGlowArtCatalog` (both headsets present, behind-layer crisp / over-layer
blurred, every layer drawable and widened, unknown devices get none, artwork paths stay in step).

Deliberately NOT automated: that the glow visually registers with the ear cups, the DWM frame
appearance, and behaviour at real display scalings — all verified by hand.

## QA (manual test steps)

1. Open Cosmo details. **Expected:** headset picture appears, then/with it a rainbow gradient on both
   ear cups, animating continuously. No RGB section in the left menu.
2. Same for Starlight (one ear cup). **Expected:** the glow sits on the device, not beneath it.
3. Leave the page and return several times. **Expected:** the effect restarts each time; the picture
   is never preceded by a floating glow.
4. Check the home page cards for both. **Expected:** the photo artwork, unchanged, no glow.
5. Drag the window edges and corners. **Expected:** resizes freely; corners stay square; no grey
   outline.
6. Set Windows display scaling to 150 %, restart the app. **Expected:** opens fully on-screen, title
   bar and window buttons reachable, UI proportionally smaller.
7. Repeat at 200 %. **Expected:** as above, smaller again but complete.
8. With two monitors at different resolutions or scalings, drag the window between them.
   **Expected:** on the smaller one it shrinks to fit rather than overflowing.
9. Maximise and restore. **Expected:** layout identical to before this work at full size.

## Review focus

`UI-thread affinity` (the glow timer and the scale run on the dispatcher), `performance hot-path`
(33 ms glow timer; multi-megabyte SVG decode moved off the open path), `resource disposal`
(the rasterized bitmap cache is process-lifetime by design).

## Performance & quality

The glow reuses the existing 33 ms timer and adds no new one. Artwork decode happens in
`PrewarmDeviceOnlyBitmapAsync` on a background thread as soon as a card is known, so opening a
headset does not parse 3-5 MB of SVG on the UI thread. `ApplyRootScale` short-circuits when the scale
has not moved, so resizing does not rebuild the transform every frame.

## Dated notes

- *2026-09-30 (PG-683 - the fit was a frame's worth short).* QA reported the app cut off on every
  edge from 1280x720 down. `ShrinkToFit` clamped `Width`/`Height` to the working area, but on a
  `Window` those are the **client** size, and `WindowDecorations="BorderOnly"` keeps a real OS frame
  around it - that frame is what makes the window resizable in the first place. Measured live on
  Windows 11 at `OnOpened`: `FrameSize` 1456x909 against `Bounds` 1440x900, so the frame is exactly
  **16x9** logical pixels. Clamping the client size to the working area therefore always produced a
  window 16 px too wide and 9 px too tall for the screen: the right border went past the edge and
  the bottom slid under the taskbar, taking the resize grips with them. `CenterOnCurrentScreen` had
  the same hole from the other side - it centred the client area, so half the overflow went to each
  edge, which is what made it read as "cut on every edge" rather than "cut on the right".

  `ResolveUsableClientSize` now pays the frame out of the working area before anything is clamped,
  and the centring uses the outer size. The overhead is measured, not assumed: `FrameSize` minus the
  current client bounds, and zero when the platform window cannot report it - which leaves the old
  behaviour exactly as it was rather than guessing a constant. `LayoutRuleTests` pins the rule
  including the degenerate case, since `MinWidth` is assigned from it.

- *2026-09-30 (PG-683 - the device outgrew its block).* The ticket's second half, reproduced on a
  live Rampage at a 1200x880 client. The board previews on the device pages asked for a hard
  `Width="628"` while the column they sit in is the layout's `*` in `ColumnDefinitions="164,420,*"`:
  the two fixed columns take 584, so near the design width the panel is only ~558 wide and the
  board hung ~35px over each edge - across the preset list on the left, off the window on the
  right. Mice were never affected, exactly as QA reported: they render through the separate
  `DeviceImageWidth` box and, being tall, fit it by height rather than claiming its full width.

  All seven board previews in `DeviceDetailsView` now take `MaxWidth="628"` plus `Margin="42,0"`
  instead. A cap rather than a size: every root there is a Uniform `Viewbox`, so a narrower box
  scales the whole board down rather than cropping it. The 42 is the chamfer module of the panel
  chrome, so a shrunken board lands in the panel's flat middle; it is inert until the panel drops
  below 712.

  *Corrected 2026-10-01:* this note originally claimed the cap keeps the board the same size on
  every section, and that the mouse/headset box needed the same treatment. Neither holds. Key
  Assignments spans columns 1+2 and stays at the cap, while the column-2 sections land near 468 at
  the design width — a visible resize when switching sections, accepted rather than equalised, see
  the comment at the first preview. And the mouse/headset wrapper was reverted; see the note below.

  Verified by A/B against a stashed tree on the same machine and window size: before, the board
  crosses the panel edge on Presets; after, it sits inside with ~45px of clearance each side, and
  Key Assignments - whose panel is full-width and never hit the cap - is unchanged.

  **Still open on PG-683:** the ticket also asks for a vertical scrollbar when the app does not
  fit. This plan deliberately chose uniform scale-down over scrolling (see "Window" above) because
  `DeviceDetailsView` pins 26 blocks to a fixed height and the cards position icons by absolute
  `Canvas` coordinates - there is nothing to reflow. Adding a scroll is a product decision against
  that choice, not a bug fix, and wants its own plan.

- *2026-10-01 (PR #142 review — what the PG-683 fit still got wrong).* Three findings from the
  external review of the branch, all confirmed, plus what live testing turned up between them.

  **Refitting on every move rewrote the restore rectangle.** Dropping the "only when the screen
  changed" condition made the fit run on any move — and maximising is a move. A maximised window's
  client area is exactly the working area while its `GetWindowRect` is larger by the frame, so
  `ShrinkToFit` read "does not fit" and shrank it; Win32 `WindowImpl.Resize` applies that to a
  maximised window by rewriting its RESTORE rectangle. Maximise then restore and the window came
  back at nearly screen size with its close button off the edge — the exact bug this path exists to
  prevent. Fixed on both counts: the refit leaves a non-normal window alone, and the
  "room actually changed" test is back, now reading `_lastWorkingArea`/`_lastScaling` (which the
  first attempt wrote and never read).

  **The refit has to be POSTED.** Inside the notification for a display change the screen still
  reports its old geometry: at the moment the scale went to 150 % the handler read the 100 % working
  area, found the window already fitted, and left it hanging 220 px below the taskbar. Nudging the
  window a few pixels a moment later shrank it correctly, which is how this was caught. It now runs
  at `DispatcherPriority.Background`, and `ScalingChanged` + `Screens.Changed` are subscribed
  alongside `PositionChanged` — a resolution or taskbar change neither moves the window nor changes
  its scaling, so neither of the other two signals covers it.

  **A minimum equal to the working area cannot be reached.** Even with fresh geometry the last 10
  physical px stayed under the taskbar at 150 %. The window refused to go below 1018 physical px
  outer however it was asked — including a forced Win32 `MoveWindow` at 1008, 900, 800 and 600 —
  because the platform enforces the minimum using a larger frame than the window actually has
  (22 physical px against a real 12). `MinWidth`/`MinHeight` set to the target size therefore block
  the window from ever reaching it. They now come down to the readable floor instead —
  `DesignSize x MinimumRootScale`, 600x440 — extracted as `ResolveMinimumExtent` and pinned by
  `LayoutRuleTests`. Measured after: window 1920x1008 against a 1920x1008 working area, 0 px over
  on both edges.

  **Not carried:** the `DownOnly` Viewbox that the first attempt put around the mouse / headset
  artwork box. The box is `DeviceImageWidth x DeviceImageHeight`, not the size of the picture in it,
  and Cosmo's PNG occupies only ~522 px of its 868-wide box — so the wrapper was scaling empty
  margin, and shrank Cosmo ~18 % at the standard 1440 window where nothing was wrong. Reverted; the
  keyboard previews, which really did overflow, keep their cap.

## Risks

- **Generated files with no generator in the repo.** The procedure above is the mitigation; if a
  future device needs this, consider committing the extraction script.
- **`MinWidth`/`MinHeight` are lowered for the session** and not restored when the window moves back
  to a larger screen. Harmless — the root scale keeps the layout correct at any size — but it means
  the guard against shrinking into a cramped layout is weaker after a small-screen start, and since
  2026-10-01 it is weaker than this originally described: the floor now drops to the readable size
  (600x440) rather than to what the screen offered, so after one small-screen start the window can
  be dragged that small on any monitor. `ResolveMinimumExtent` and its tests carry the reasoning.
- **Scaling below 0.5 clips rather than shrinks.** Deliberate: past that the UI is unreadable.

## Review findings

*Filled in by /reviewer. The round that produced this document raised S1 (unreferenced assets
shipping), S2 (untested rules), S3 (fit ran only at startup) and S4 (no plan, no regeneration
procedure); all four are addressed above and in the code.*
