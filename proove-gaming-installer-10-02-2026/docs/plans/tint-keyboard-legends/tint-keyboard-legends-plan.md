# Keycap legends follow the backlight

## Goal

On the layered SVG keyboards the RGB colour only reached the gaps between keycaps. The legends
printed *on* the caps stayed a fixed brand colour — red on Cyrex and Aress, blue on Raster and
Rampage — so a board set to green showed green gaps and red letters. On the real hardware the
legends are shine-through and lit by the same LEDs, so they should follow the colour too.

## What was actually wrong

Not a rendering bug. The colour is baked into the artwork. Each `_keys.svg` contains:

- `<image id="keys-image">` — the keycap bodies, an embedded PNG
- `<g id="text-solid">` — the glyph outlines
- `<g id="text-blur">` — the same glyphs again under a Gaussian blur, i.e. the legend's halo

Both text groups take their fill from a CSS variable (`--text-color` / `--keyboard-text-color`)
pinned to a single colour. The artwork was authored to be recoloured *through that variable*.

## Options considered

| Option | Verdict |
|---|---|
| Override the CSS variable on the `Svg` control | **Rejected.** It would need the document re-parsed to take effect, and these are ~800 KB files with an embedded PNG. The tint animates per frame under Breathing / ColorCycle, so this would re-parse megabytes per frame and stutter the UI. |
| Tint the whole keys layer | **Rejected.** `SvgImageView`'s tint is a `SrcIn` blend over every pixel, so the keycaps would take the colour too — the same reason the view's existing comment gives. |
| **Extract the glyph geometry and draw it ourselves** | **Chosen.** Binding a `Fill` to the existing glow brush costs nothing per frame. It is also exactly what this codebase already did for the backlight: the baked-in glow was stripped out of the artwork and re-drawn as a `Path` over geometry we own (`Glow/*.path`). |

## What was done

1. **`extract-legends.py`** — for each board that has the text groups: writes every shape in
   `#text-solid` to `Assets/Keyboards/Legend/<Board>.path`, then removes `#text-solid` and
   `#text-blur` from the SVG so nothing draws the legends at a fixed colour any more. Shrinks the
   artwork 9-25% as a side effect. The as-delivered vendor SVGs (`PG_*_Keyboard_SVG_*.svg`) are not
   touched — only the derived `_keys.svg` files, which were already generated artefacts.
2. **`RgbKeyboardArt`** gains `LegendPathAsset` and `UnlitLegendColor`, plus a lazily parsed and
   cached `Legends` geometry — the same shape as the existing `Glow` member, including returning
   null rather than throwing when an asset is missing.
3. **`RgbKeyboardView`** gains three layers above the keycaps: a blurred tinted halo and a solid
   tinted glyph layer when lit, and a static-coloured glyph layer when not.

### Lit vs unlit

`ShowBacklight="False"` is used in exactly one place — the art layer behind the clickable key
overlay on Key Assignments and Macros. Legends must stay **readable** there, so they are drawn at a
fixed colour with no halo: by default the one the artwork fixed them at, which leaves those pages
looking exactly as they did before this change. Readability is the actual contract, though, so a
board whose keycaps are too close to that colour overrides it — see the 2026-09-30 note below for
Rampage. Everywhere the backlight is shown, legends follow it.

### Fill rule

The assets are written with an `F1` prefix so Avalonia uses the nonzero fill rule that SVG defaults
to. Without it glyph counters — the hole in "O", "P", "8" — could fill solid.

## File-level change list

| File | Intent |
|---|---|
| `docs/plans/tint-keyboard-legends/extract-legends.py` | Generator. Converts `<path>`, `<rect>` (rounded) and `<polygon>` to path data, strips the text groups, and validates the result. |
| `src/ProoveGaming.App/Assets/Keyboards/Legend/*.path` | **New**, generated: Raster, Rampage, Aress, Cyrex. |
| `src/ProoveGaming.App/Assets/Keyboards/PG_*_keys.svg` | Text groups removed; keycap image retained. |
| `src/ProoveGaming.App/Models/RgbKeyboardArt.cs` | `LegendPathAsset`, `UnlitLegendColor`, cached `Legends`, `UnlitLegendBrush`; catalog wiring. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/RgbKeyboardView.axaml` | Three legend layers above the keycaps. |
| `tests/ProoveGaming.App.Tests/KeyboardLegendAssetTests.cs` | **New.** Catalog wiring and unlit colours. |

## Two things the generator got wrong first, and now guards

Both were caught before shipping, and the guards are the useful part:

1. **It matched only `d="…"`,** silently dropping 10 `<rect>` and 2 `<polygon>` elements from
   Rampage, 3 rects from Raster and 9 from Aress — real pieces of legends (the bar-shaped glyph
   parts). It now converts every shape type and **aborts on any it does not recognise** rather than
   skipping it. Shape counts reconcile exactly: Raster 170+3, Rampage 185+10+2, Aress 132+9,
   Cyrex 138.
2. **Its bounds validator used a greedy number regex.** SVG omits separators where a number is
   unambiguous, so `0-.276.224-.5` is four numbers; `[\d.]+` swallowed `-.5.5` and threw, which made
   the walk stop after one glyph and report a 6×3 box. Fixed to the SVG number grammar. The check
   now confirms each board's geometry sits inside its viewBox and spans over half the width, which
   is what an artwork re-export would break.

## Testing

Automated coverage is catalog wiring only. `Geometry.Parse` and `AssetLoader` both need Avalonia
platform services that a bare xUnit process does not provide — `RgbKeyboardArt.Glow` returns null
under test too, and has since long before this change — and reaching them means adding
`Avalonia.Headless`, a new dependency. The geometry is therefore validated in the generator, at the
point where a bad re-export would actually be introduced.

**Not covered, and needs eyes:** that the tinted glyphs land exactly on the keycaps and that the
halo reads as backlighting rather than smear. See QA below.

## QA (manual test steps)

1. Open a Cyrex or Aress → RGB / lighting. Pick a strong colour, e.g. green.
   **Expected:** the gaps between keys *and* the letters on the keys are both green. No red letters.
2. Drag the colour picker around.
   **Expected:** letters track the colour continuously, with no stutter or lag against the gaps.
3. Select the Breathing effect, then Color Cycle.
   **Expected:** the letters pulse and cycle in step with the gaps, not out of phase.
4. Turn the lighting off / set brightness to minimum.
   **Expected:** letters go dark with the board. They should not stay lit at a fixed colour.
5. Go to Key Assignments, then Macros, on the same board.
   **Expected:** the keyboard picture shows readable letters in the board's own colour (red on
   Cyrex/Aress, blue on Raster/T-Racer) — i.e. unchanged from before. Not dark, not tinted.
   **Rampage is the exception** (PG-688, note below): bright green `#00FF3C`, and every row must be
   readable — including the dark bottom-row modifiers and the nav block, which is what the artwork
   colour lost.
6. Look closely at a letter with an enclosed hole — O, P, 8, R.
   **Expected:** the hole is open, not filled in.
7. Repeat 1-4 on a Raster and a Rampage.
8. Open a T800.
   **Expected:** unchanged — its artwork does not separate the legends, so they stay as they are.

## Dated notes

- *2026-09-30 (PG-688 - Rampage legends were unreadable).* QA reported that on Rampage "not all
  keys are visible" on the click-target pages and asked for a bright green backlight colour. The
  cause was the default above meeting an artwork it does not suit: the Rampage keycaps are
  blue-violet, so its artwork colour `#0f53ff` sits at a contrast ratio of **1.05-1.41** against
  them, where 1.0 is literally indistinguishable. The cap colour is the whole of it - the board's
  `KeyOverlayArtDim` scrim (0.15 since `152db5b`) dims legend and cap together and barely moves the
  ratio. Sampled from the keycap raster, through that scrim:

  | Keycap sampled | `#0f53ff` | `#00FF3C` |
  |---|---|---|
  | Row 1 (blue) | 1.26 | 2.97 |
  | Row 3 (blue) | 1.05 | 3.60 |
  | Row 5 (violet) | 1.10 | 4.13 |
  | Bottom row modifiers (dark) | 1.41 | 5.30 |

  Rampage's `UnlitLegendColor` is therefore `#00FF3C` - the bright green already in
  `RgbColorPickerControl`'s preset swatches, so no new colour enters the palette. The brand green
  `#00CE4F` was measured too and rejected: it only reaches 1.96 on the row-1 caps. No other board
  changed: each of the others keeps its artwork colour, and the contrast that made this a bug is
  specific to Rampage's caps. `KeyboardLegendAssetTests.UnlitLegendColour_IsPinnedPerBoard`
  pins the override so a board cannot drift back to an unreadable colour in silence.

## Risks

- **The assets are tied to this artwork revision.** Re-exporting any `_keys.svg` invalidates its
  legend geometry, exactly as it already does for `Glow/*.path` and the artwork layout tables. The
  generator's bounds check is the guard; re-run it after any artwork change.
- **The generator is destructive** — it edits the `_keys.svg` in place. Re-running it on already
  stripped files is a no-op (no `#text-solid` to find), so it is safe to run twice, but the vendor
  originals are the only recovery path if the derived files are lost.
- **T800 is excluded**, so its legends still do not follow the backlight. Fixing it needs artwork
  with separable legends, which is a vendor request rather than a code change.
