# Add T-Racer Keyboard Artwork — Layered RGB Preview, Card Art & Artwork-Accurate Key Overlays

## Goal

T-Racer is the only supported keyboard with no artwork at all: its `AllowedDevices` entry has
no `ImagePath`, it is absent from `RgbKeyboardArtCatalog`, and it has no measured keycap table.
The observable result today is a details page and a home card that render **nothing** where the
board should be, and Key Assignments / Macros overlays drawn on a bare abstract grid with text
labels instead of on a picture of the keyboard. A new vendor SVG has now been delivered, so this
change brings T-Racer to exactly the same rendering as Raster: split chassis/keycap artwork with
a live backlight drawn between the layers, extracted legends that follow the picker, card artwork
on the home screen, and clickable key overlays positioned on the real keycaps.

This is artwork and rendering only. No protocol, no gRPC, no device I/O, no new UI surface.

## Scope

**In**

- The delivered SVG committed under `src/ProoveGaming.App/Assets/Keyboards/`.
- Split `_base` / `_keys` artwork, glow geometry, legend geometry, card artwork for T-Racer.
- `RgbKeyboardArtCatalog` entry for `T-Racer`.
- `TRacerArtworkLayout` — measured per-keycap bounds in the new artwork's viewBox space.
- Routing T-Racer through the artwork overlay path instead of the abstract-canvas fallback.
- `ImagePath` on the T-Racer `AllowedDevices` entry (flat fallback, parity with the other five).
- The generation script that produces every derived asset, checked in under this plan folder.
- Glossary entries in `docs/CONTEXT.md`; correcting the in-code and in-plan comments that
  currently state T-Racer has no artwork.

**Out**

- T-Racer macros. Still no `SupportsMacros` on its config entry; the Macros nav item does not
  appear. The Macros-section overlay code path is shared, so it will be correct if macros are
  enabled later, but enabling them is not this change.
- Any change to `TRacerVisualLayout`. It stays as the abstract-grid fallback for the case where
  the artwork table cannot resolve, exactly as `RasterVisualLayout` does for Raster.
- Any change to `TRacerKeyboardRgbService`, the RGB protocol, or which transports advertise
  `SupportsRgb`.
- Re-deriving the Raster/Rampage/Aress/Cyrex/T800 assets. The new generation script handles
  T-Racer's artwork structure, which differs from theirs (see below); the existing assets stay
  byte-identical.
- The `PrintScreen` position note in `TRacerVisualLayout` ("Placeholder position — verify against
  a photo"). The artwork now settles it, but that layout stops being used for T-Racer once the
  measured table lands, so touching it is churn.

## What the delivered artwork actually contains

Established by inspecting the file, not assumed. This matters because **T-Racer's SVG is a newer
export generation than every other board's** and needs a different split.

`viewBox="0 0 1595.294 689.31"`, 6.6 MB, seven top-level groups in document order:

| Group | Contents | Role |
|---|---|---|
| `#keyboard-back` | 2 `<image>`, `translate(0 .06) scale(1.184)` | Full board render (chassis **and** keycaps) |
| `#keys-solid` | 6 `<path>`, no transform | **The backlight geometry**, as vectors |
| `#keys-blur` | same 6 paths, blurred, `opacity: .65` | Vendor's glow bloom over the caps |
| `#keyboard-midle` | 2 `<image>`, `translate(96.847 52.908) scale(1.185)` | **Keycaps only**, transparent background |
| `#keyboard-top` | 2 `<image>`, `translate(15.529) scale(1.184)`, `opacity=".6"` | Full-board contrast/gloss overlay |
| `#text-solid` | 174 `<path>` + 3 `<rect>` | Keycap legends, fill from `--keyboard-text-color: #0f53ff` |
| `#text-blur` | same, blurred, `opacity: .50` | Legend halo |

Two facts that shape the recommended approach:

1. **The glow geometry ships in the file.** `#keys-solid` is six untransformed paths already in
   viewBox units — one large rounded region covering rows 2–6, and five smaller ones covering the
   F-row zones. Every previous board's `Glow/*.path` had to be hand-derived; T-Racer's is a
   verbatim copy of what the vendor drew.
2. **Every `<image>` is a pair: the white colourway followed by the black colourway.** They are
   the same render with different keycaps, at identical position and size, second over first. The
   second (black) fully occludes the first: over all three pairs there is **not one pixel** where
   the white layer's alpha exceeds the black layer's by more than 8/255 (differences are confined
   to antialiased edges). The white halves are ~2.9 MB of payload that can never be seen.

Component labelling of `#keyboard-midle`'s black keycap image (alpha ≥ 128, components ≥ 200 px)
finds **81 keycaps**, laid out 14 / 15 / 15 / 14 / 14 / 9 by row — exactly `TRacerKeyCatalog`'s
80 remappable keys plus the FN key, which is not remappable and gets no overlay. This mirrors
Raster (80 components / 79 keys, extra one being FN) and confirms the artwork depicts this board,
which is the check Aress failed.

## Options considered

### 1. How much of the pipeline to reuse

| Option | Trade-off |
|---|---|
| **A. Reuse the existing `RgbKeyboardArt` pipeline verbatim** | **Chosen.** T-Racer becomes the sixth entry in a catalog five boards already use, gains the same four-layer render, the same legend layer, the same card art and the same measured-bounds overlay. No new abstraction, no new control, no new view. The whole cost is asset generation plus one catalog row and one `if`. |
| B. Set only `ImagePath` and stop there | Rejected. Fixes the blank page but gives no backlight preview and leaves the key overlays on the labelled grid. Explicitly not "the same as Raster". |
| C. Render the vendor SVG's own layer stack at runtime, recolouring its `--keyboard-rgb-color` CSS variable | Rejected for the reason already recorded for the other boards: the document is 6.6 MB with six embedded PNGs, and the tint animates at 33 ms. Re-parsing per frame is not viable, which is precisely why the geometry is extracted to `.path` assets instead. |

### 2. Where `#keyboard-top` goes in the split

The vendor stack draws the 60 %-opacity full-board overlay **after** the keycaps, i.e. above the
glow.

| Option | Trade-off |
|---|---|
| **A. Fold `#keyboard-top` into the base (chassis) layer, below the glow** | **Chosen.** The chassis keeps the exact appearance the vendor intended (the overlay is a contrast pass over the same render as `#keyboard-back`), while the glow reaches the gaps at full strength and the black unlit bed reads as *LEDs off*. This is the layer contract `RgbKeyboardView` documents: nothing tinted above the keycaps except the legends. |
| B. Fold it into the keys layer, preserving vendor order | Rejected. A 60 %-opacity full-board image over the glow repaints the gaps with chassis pixels and washes the backlight to ~40 %. That is the "this board has no LEDs" look the unlit bed exists to prevent, and it would make T-Racer's preview visibly weaker than every other board's for no gain. |
| C. Drop `#keyboard-top` entirely | Rejected. It carries the artwork's contrast and edge definition; without it the chassis renders flatter than the delivered file. |

### 3. Whether to strip the redundant white colourway

| Option | Trade-off |
|---|---|
| **A. Strip the first `<image>` of each pair everywhere, including the file committed under `Assets/Keyboards/`** | **Chosen.** Verified unobservable (see above). Cuts every derived asset roughly in half — ~6.5 MB added to the repo instead of ~13 MB — and halves the decode work on first render of the home card, which parses this document. |
| B. Keep the delivered file byte-verbatim for provenance and strip only the derived assets | Rejected as the default, but it is a one-line change to the script if provenance is wanted. It costs 3.3 MB and doubles the card's first-parse cost to display pixels that are painted over. Flagged in Open questions — the user handed over a file, so altering it on the way in is called out rather than done silently. |

### 4. Where the card artwork comes from

Every other board has a distinct `Card/PG_*_card.svg`: a *different* vendor render (lit product
shot) from the one the split came from. For T-Racer only one file was delivered, so a
`Card/PG_T-Racer_card.svg` would be a byte-for-byte duplicate of the file already committed at
the top level.

**Chosen:** point `CardSvg` at `/Assets/Keyboards/PG_Keyboard_RGB-off_T-Racer_black.svg`
directly and create no `Card/` entry, with a comment in the catalog saying why. Saves ~2.5 MB of
pure duplication. If a lit T-Racer product shot is delivered later, adding `Card/` and
repointing `CardSvg` is a one-line change.

## Recommended approach

Generate the derived assets with a checked-in script, add one catalog row, one branch in
`ArtworkKeyOverlayLayout`, one config field, and update the comments and tests that assert
T-Racer has no artwork.

### Asset generation

One new script, [`split-tracer-artwork.py`](split-tracer-artwork.py), run from the repo root.
It is the sibling of [`../tint-keyboard-legends/extract-legends.py`](../tint-keyboard-legends/extract-legends.py)
and follows the same conventions: strict about unrecognised shapes, validating what it produces
rather than trusting it. It performs five passes over
`src/ProoveGaming.App/Assets/Keyboards/PG_Keyboard_RGB-off_T-Racer_black.svg`:

1. **Colourway strip.** Remove the first `<image>` of each pair inside `#keyboard-back`,
   `#keyboard-midle` and `#keyboard-top`, rewriting the file in place. Re-verify the occlusion
   claim before removing: decode both PNGs of each pair and abort if any pixel has
   `alpha(white) - alpha(black) > 8`. This check is the whole justification for the strip, so it
   runs at generation time rather than being taken on trust.
2. **`PG_T-Racer_base.svg`** — the document reduced to `<defs>`, `<style>`, `#keyboard-back` and
   `#keyboard-top` (in that order, `#keyboard-top` keeping its `opacity=".6"`). Same `viewBox`,
   same `width`/`height`.
3. **`PG_T-Racer_keys.svg`** — the document reduced to `#keyboard-midle`, `#text-solid` and
   `#text-blur`. The text groups are included here deliberately: `extract-legends.py` is then run
   over this file and removes them, which is exactly how `PG_Raster_keys.svg` reached its current
   shape. Keeping the two scripts' responsibilities separate means the legend pipeline stays one
   implementation for all boards.
4. **`Glow/T-Racer.path`** — the six `d` attributes of `#keys-solid`, concatenated with single
   spaces, no fill-rule prefix (the shapes are disjoint, matching `Glow/Raster.path`'s form).
   `#keys-blur` is discarded: the App reproduces the bloom itself from the same geometry.
5. **`TRacerArtworkLayout` bounds** — connected-component labelling of the *black* keycap image
   from `#keyboard-midle`, alpha threshold 128, components under 200 px discarded as artefacts.
   Each component's pixel bounding box is mapped into viewBox space by the group's own transform,
   `translate(96.847 52.908) scale(1.185)`, then assigned to a key name by sorting into rows and
   walking each row left-to-right against `TRacerKeyCatalog.Keys`. The script emits the finished
   C# file and **fails loudly** if the component count per row does not match the expected
   `14 / 15 / 15 / 14 / 14 / 9+FN` shape, so an artwork re-export cannot silently produce a
   plausible-looking but shifted table.

Then run the existing legend extractor with T-Racer added to its board map:

```
python docs/plans/add-tracer-artwork/split-tracer-artwork.py
python docs/plans/tint-keyboard-legends/extract-legends.py
```

The second run is idempotent for the five existing boards — their `_keys.svg` files no longer
contain `#text-solid`, so the script prints "skipped" and rewrites nothing.

### App wiring

`RgbKeyboardArtCatalog` gains:

```csharp
["T-Racer"] = new($"{Dir}/PG_T-Racer_base.svg", $"{Dir}/PG_T-Racer_keys.svg", $"{Dir}/Glow/T-Racer.path",
    $"{Dir}/PG_Keyboard_RGB-off_T-Racer_black.svg", 1595.294, 689.31, $"{Dir}/Legend/T-Racer.path", "#0f53ff"),
```

`#0f53ff` is read from the delivered file's own `--keyboard-text-color`, the same way the other
boards' unlit colours were.

`DeviceDetailsViewModel.ArtworkKeyOverlayLayout` gains a T-Racer branch alongside the Raster one,
keyed by `DevKeyName` (T-Racer's catalog names are unique, so no slot join is needed — that
indirection exists only because Aress borrows Cyrex's table). `IsTRacerDevice` already exists in
that file.

Everything else — the card, the layered preview, the overlay stage sizing, the legend layers —
follows from the catalog entry and the layout branch with no further change. `HasRgbKeyboardArt`,
`ShowLayeredKeyboardImage`, `ShowFlatKeyboardImage`, `UseArtworkKeyOverlays`,
`ArtworkViewBoxWidth/Height` and `ArtworkKeyBounds` are all already computed from those two.

### Asset naming

`T-Racer` with the hyphen for the `.path` assets and the SVGs, so
`KeyboardLegendAssetTests`' `$"/Assets/Keyboards/Legend/{board}.path"` convention keeps holding
with T-Racer as one more theory row rather than a special case. C# types stay `TRacerArtworkLayout`,
matching `TRacerVisualLayout` and `TRacerKeyCatalog`.

## File-level change list

### Created — assets

| File | Intent |
|---|---|
| `src/ProoveGaming.App/Assets/Keyboards/PG_Keyboard_RGB-off_T-Racer_black.svg` | The delivered vendor artwork, white colourway stripped (~2.5 MB). Serves as both the flat `ImagePath` fallback and `CardSvg`. |
| `src/ProoveGaming.App/Assets/Keyboards/PG_T-Racer_base.svg` | Chassis layer: `#keyboard-back` + `#keyboard-top`. |
| `src/ProoveGaming.App/Assets/Keyboards/PG_T-Racer_keys.svg` | Keycap layer: `#keyboard-midle`, transparent background, text groups removed by the legend extractor. |
| `src/ProoveGaming.App/Assets/Keyboards/Glow/T-Racer.path` | Backlight geometry, the six `#keys-solid` paths verbatim in viewBox units. |
| `src/ProoveGaming.App/Assets/Keyboards/Legend/T-Racer.path` | Keycap legend outlines, produced by `extract-legends.py`. |

No `.csproj` change: `<AvaloniaResource Include="Assets\**" />` already globs these.

### Created — code & tooling

| File | Intent |
|---|---|
| `src/ProoveGaming.App/Models/Keyboards/TRacerArtworkLayout.cs` | 80 keycap rectangles in the `1595.294 × 689.31` artwork viewBox, keyed by `DevKeyName`. Generated, not hand-authored; header comment records the transform and that FN is deliberately absent. |
| `docs/plans/add-tracer-artwork/split-tracer-artwork.py` | The generation script described above. Referenced from this plan; the only supporting file in this folder. |
| `tests/ProoveGaming.App.Tests/TRacerArtworkLayoutTests.cs` | See Test strategy. |

### Edited

| File | Intent |
|---|---|
| `src/ProoveGaming.App/Models/RgbKeyboardArt.cs` | Add the `T-Racer` catalog row; replace the class comment stating T-Racer is absent "because no split artwork was produced for it"; note why its `CardSvg` points at the top-level file rather than `Card/`. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` | `ArtworkKeyOverlayLayout` gains the `IsTRacerDevice` branch returning `TRacerArtworkLayout`. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml` | Comment currently reads "Boards without a measured table (T-Racer) keep the bare canvas" — T-Racer is no longer the example. Keep the fallback described, drop the naming. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml.cs` | Comment at the fallback branch names T-Racer as the flat-canvas case; same correction. |
| `src/ProoveGaming.DeviceHost/appsettings.json` | T-Racer entry gains `"ImagePath": "/Assets/Keyboards/PG_Keyboard_RGB-off_T-Racer_black.svg"`, matching the other five keyboards. |
| `docs/plans/tint-keyboard-legends/extract-legends.py` | `BOARDS` gains `"PG_T-Racer_keys.svg": "T-Racer"`. |
| `docs/CONTEXT.md` | New terms and corrections — see below. Mandatory per CLAUDE.md. |
| `docs/plans/raster-svg-effects/raster-svg-effects-plan.md` | Two stale statements: Scope "Out" says T-Racer "falls back to the flat image", and QA step 16 expects "it still shows its plain flat picture". One-line corrections pointing here; that document is a historical record, so nothing else in it changes. |

### Edited — tests

| File | Intent |
|---|---|
| `tests/ProoveGaming.App.Tests/ArtworkKeyOverlayGateTests.cs` | `TRacer_FallsBackToTheAbstractCanvas` asserts the behaviour this change reverses. Replace with `TRacer_UsesArtworkOverlays`; extend `EachBoard_ResolvesItsOwnArtworkTable` to cover it. |
| `tests/ProoveGaming.App.Tests/KeyboardLegendAssetTests.cs` | Add `T-Racer` to `BoardsWithLegends`, to `EveryCatalogBoard_Resolves`, and to `UnlitLegendColour_IsPinnedPerBoard` with `#0f53ff`. |

### `docs/CONTEXT.md` entries

- **TRacerArtworkLayout** — new. The 80 T-Racer keycap rectangles in artwork viewBox coordinates,
  the third such table alongside `RasterArtworkLayout` and `CyrexArtworkLayout`.
- **Artwork colourway** — new. The delivered T-Racer SVG stacks a white-keycap render under a
  black-keycap render of the same board; only the top one is ever visible, and the lower one is
  stripped at asset-generation time.
- **Vendor glow geometry (`#keys-solid`)** — new. T-Racer is the first board whose backlight
  geometry ships as vectors in the artwork instead of being hand-derived.
- **T-Racer** — existing entry, extend: it now has split artwork, a measured keycap table and a
  live backlight preview.
- **RgbKeyboardArt** — existing entry, correct: T-Racer is no longer the example of a board that
  falls back to the flat image.
- **Legend layer (keyboard artwork)** — existing entry, extend the board list with T-Racer.

## Acceptance criteria

Boxes marked `[x]` are verified by build/tests, by a reproducible command, or — for the visual
ones — by driving the real app against real hardware (see
[Hardware verification](#hardware-verification-round-1) below).

- [x] Given a connected T-Racer, when its details page opens, then a picture of the T-Racer
      keyboard renders (today nothing renders), with **black** gaps between the keycaps rather
      than the artwork's plate colour. **Verified on hardware.**
- [ ] Given a connected T-Racer with lighting enabled, when a colour is picked, then the gaps
      between the keycaps and the keycap legends take that colour, and the keycaps themselves
      do **not** change colour. *(visual — QA step 4; not exercised in the hardware pass)*
- [x] Given the backlight is off, then the legends render in `#0f53ff` and stay legible.
      **Verified on hardware** — legends read clearly blue on the Key Assignments board.
- [ ] Given ColorCycle or Wave is selected on a T-Racer, then the preview sweeps left to right,
      the same as every other keyboard. *(visual — QA step 5; no T-Racer-specific code path)*
- [x] Given the home screen with a T-Racer in the device list, then its card shows the T-Racer
      artwork. **Verified on hardware** (connected state; the 30 % disconnected dim was not
      re-checked — it is shared code with the other five boards).
- [x] Given the Key Assignments section for a T-Racer, then the clickable key boxes sit **on the
      rendered keyboard's keycaps** — no separate labelled grid, and no visible offset between a
      box and the keycap under it. **Verified on hardware:** hovering the `E` keycap draws the
      highlight box exactly on it. This was the change's single largest risk and it is now closed.
- [x] Given the Key Assignments section for a T-Racer, when the FN key is clicked, then nothing
      is selected — FN has no overlay, matching Raster.
      `TRacerArtworkLayoutTests.DoesNotContainFn`; the overlay view `continue`s on a key with no
      bounds, which is the same path Raster's FN already takes.
- [x] Given a T-Racer, then every one of the 80 keys in `TRacerKeyCatalog` has an overlay box
      (`vm.ArtworkKeyBounds.Count == 80`), and `PrintScreen` in particular has one.
      `ArtworkKeyOverlayGateTests.EachBoard_ResolvesItsOwnArtworkTable`,
      `TRacerArtworkLayoutTests.CoversEveryCatalogKeyAndNothingElse` / `PrintScreen_HasBounds`.
- [x] Given `RgbKeyboardArtCatalog.For("T-Racer")`, then it returns non-null with
      `Width == 1595.294`, `Height == 689.31` and `LegendPathAsset == "/Assets/Keyboards/Legend/T-Racer.path"`.
      `KeyboardLegendAssetTests` covers non-null and the legend path;
      `ArtworkKeyOverlayGateTests.ArtworkDimensions_MatchTheMeasuredTable` (added during
      implementation) pins the dimensions against the measured table for every artwork board.
- [x] Given a Raster, Rampage, Aress, Cyrex or T800, then its rendering is unchanged and its
      existing assets are byte-identical to `main` (`git diff --stat` touches no other board's files).
      Confirmed: the only asset paths in the working tree are the five new `T-Racer` ones.
- [x] Given `python docs/plans/add-tracer-artwork/split-tracer-artwork.py` is re-run on a clean
      checkout, then it reproduces every generated asset and the C# layout file identically, or
      fails with a message naming what changed in the artwork. Verified by `md5sum -c` across a
      second run; the colourway pass detects it has already run and skips.
- [x] `dotnet build ./src/ProveGaming.slnx` — 0 errors; `dotnet test ./src/ProveGaming.slnx` —
      all green (355 App + 355 Infrastructure).

## Test strategy

This is a **new feature** (`add-*`) that also **changes existing behaviour** — T-Racer moves off
the abstract-canvas fallback. The touched behaviour *does* already have a test
(`ArtworkKeyOverlayGateTests.TRacer_FallsBackToTheAbstractCanvas`), so no characterization pass
is needed; that test is inverted as part of the change and the rest of the file must stay green.

All coverage lands in **`tests/ProoveGaming.App.Tests`** — the change touches App and Common
only, and `ProoveGaming.Infrastructure.Tests` has no stake in it.

**`tests/ProoveGaming.App.Tests/ArtworkKeyOverlayGateTests.cs` — edit**

- Replace `TRacer_FallsBackToTheAbstractCanvas` with `TRacer_UsesArtworkOverlays`:
  `UseArtworkKeyOverlays` true, `ArtworkViewBoxWidth/Height` equal to
  `TRacerArtworkLayout.ViewBoxWidth/Height`.
- Extend `EachBoard_ResolvesItsOwnArtworkTable` with a T-Racer view model, asserting its viewBox
  differs from both Raster's and Cyrex's and that its bounds cover its own catalog's names. This
  is the file's stated highest-value case — a board resolving another board's table — and T-Racer
  is the most exposed to it, being the same vendor family and nearly the same layout as Raster.

**`tests/ProoveGaming.App.Tests/TRacerArtworkLayoutTests.cs` — new**

Mirrors the two generated-data checks the file above already applies to `CyrexArtworkLayout`,
because `TRacerArtworkLayout` is generated the same way and fails the same way:

- `CoversEveryCatalogKeyAndNothingElse` — one entry per `TRacerKeyCatalog.Keys` name, count equal,
  no orphan names. Catches a labelling run that mis-assigns a component to the wrong key.
- `DoesNotContainFn` — FN is not remappable and must have no bounds.
- `BoundsLieWithinTheViewBox` — non-negative origin, no overflow of `1595.294 × 689.31`, non-zero
  size. This is what catches the transform being applied wrongly, which otherwise ships as
  silently offset overlays.
- `KeysDoNotOverlap` — no two rectangles intersect by more than 2 units. Not applied to the other
  boards, but T-Racer's table is produced by a row-walk join, and a dropped or doubled component
  in one row shows up here as two keys claiming one keycap while every other assertion still passes.
- `PrintScreen_HasBounds` — the one key T-Racer has and Raster does not, and the one the abstract
  layout carried an explicitly unverified guess for.

**`tests/ProoveGaming.App.Tests/KeyboardLegendAssetTests.cs` — edit**

- T-Racer added to `BoardsWithLegends`, `EveryCatalogBoard_Resolves`, and
  `UnlitLegendColour_IsPinnedPerBoard` (`#0f53ff`). This is the wiring guard
  that stops a board joining the catalog with a typo'd or missing legend asset, which otherwise
  degrades silently to "legends never light up".

**Deliberately not automated**

- Anything that parses the geometry. `Geometry.Parse` and `AssetLoader` both need Avalonia
  platform services a bare xUnit process does not have, so `RgbKeyboardArt.Glow` and `.Legends`
  return null in tests — this is pre-existing and documented in `KeyboardLegendAssetTests`.
  Geometry is validated where it is produced instead: `extract-legends.py` already fails if the
  legend path falls outside the viewBox or collapses, and `split-tracer-artwork.py` applies the
  same containment check to the glow path and the measured bounds.
- Rendered pixel output — layer order, glow visibility, the black bed, the 0.6 dimming, whether
  overlays visually line up with keycaps. No Avalonia render tests in this repo; covered by QA.
- The colourway-occlusion claim. It is a property of two specific PNGs, checked by the generation
  script at the moment it acts on it, not by a unit test that would have to carry the images.

## QA (manual test steps)

Requires a T-Racer keyboard connected by USB cable, plus a Raster keyboard if one is available
for comparison. Start the device service, then the app.

1. On the home screen, find the T-Racer card.
   **Expected:** the card shows a picture of the T-Racer keyboard. Before this change the card
   was empty, so anything appearing at all is the change working.
2. Unplug the T-Racer and look at the home screen again.
   **Expected:** the same picture, faded/greyed out. Clicking the card does nothing; the
   three-dots menu on the card still opens.
3. Plug it back in and open the T-Racer from the home screen.
   **Expected:** the page opens showing the keyboard, and the gaps between the keycaps are
   **black** — not grey or white. It must look right immediately, without clicking another
   menu item first.
4. Go to the RGB / lighting section and pick a bright colour, e.g. pure red.
   **Expected:** the gaps between the keys turn red **and the letters on the keys turn red too**.
   The key tops themselves stay their normal colour — if the whole board tints, that is a bug.
5. Choose the "Color cycle" effect, then "Wave".
   **Expected:** the colour sweeps across the picture from left to right.
6. Turn the lighting toggle off.
   **Expected:** the picture goes dark and the letters on the keys turn a fixed blue, still
   clearly readable.
7. Go to the Key Assignments section.
   **Expected:** you see the same picture of the keyboard, not a plain grid of grey boxes with
   key names written in them. This is the most important visual check in this list.
8. Hover the mouse slowly across the keys, especially the far-left, far-right, top and bottom
   edges of the board.
   **Expected:** the highlight box lands exactly on the keycap under the pointer, with no drift —
   the box for `Esc` must not sit half on `F1`, and the arrow keys in the bottom-right corner must
   each highlight separately.
9. Resize the app window as wide and as narrow as it will go, then hover a few keys again.
   **Expected:** the boxes still line up at every size.
10. Click the `PrintScreen` key (top-right area of the function row, next to the round dial).
    **Expected:** it highlights and its name appears as the selected key. T-Racer has this key
    and Raster does not, so it is the one most likely to be missing.
11. Hover the round dial in the top-right corner, and the `Fn` key in the bottom row.
    **Expected:** the dial does nothing (it is not a key). `Fn` also does nothing — this is
    intentional and matches Raster, not a bug.
12. Click through several keys across every row — a letter, a number, `Space`, `Enter`,
    `Backspace`, `Left Shift`, the arrow keys.
    **Expected:** each one selects normally, and its name appears as the heading above the list.
13. Now remap one. Click `E` on the picture, then scroll the list down and pick `R`.
    **Expected:** the `R` row becomes selected and the `SAVE PRESET` button lights up.
    **The physical keyboard has NOT changed yet — this is normal, not a bug.** Key remaps are
    only sent to the keyboard when you save.
14. Click `SAVE PRESET`, then click `SAVE` in the dialog that appears.
    **Expected:** open Notepad and press the physical `E` key — it now types `r`.
15. Change it back: select `E` on the picture, pick `E` in the list, then `SAVE PRESET` → `SAVE`.
    **Expected:** the physical `E` key types `e` again. Do not leave the keyboard remapped.
16. If a Raster is available, open it and repeat steps 3, 4 and 7.
    **Expected:** Raster looks and behaves exactly as it always has. Also check Rampage, Aress,
    Cyrex and T800 if present — none of them should have changed in any way.
17. Compare the T-Racer picture to the physical keyboard in front of you.
    **Expected:** the same key layout, the same number of keys per row, the round dial in the
    same corner.

## Review focus

- `performance hot-path` — the artwork feeds the 33 ms effect timer's `Path.Fill`. The glow and
  legend geometry must be parsed once and cached on the record (they are, via
  `RgbKeyboardArt.Glow` / `.Legends`); nothing added here may parse or allocate per tick. Also
  worth a look: this adds ~6.5 MB of SVG with embedded PNGs to the published app, decoded on
  first render of the home card and the details page.
- `null-safety` — `ArtworkKeyOverlayLayout` returns null for boards without a table and every
  consumer coalesces; the new branch must not turn a missing asset into a crash. A T-Racer with
  a mistyped asset path should degrade to "no glow / no legends", the way the record's `catch`
  blocks already provide, not to an exception on the UI thread.
- `UI-thread affinity` — no new work on the dispatcher; confirm the change adds no synchronous
  asset load on a binding evaluation path.

## Performance & quality

- **Per-frame hot path.** Unchanged in shape. `SetGlow` writes `AnimatedGlowBrush` every 33 ms
  and the layered canvas re-fills three `Path` layers from cached `Geometry`. T-Racer's glow is
  six subpaths against Raster's one, and its legend geometry is ~90 KB against Raster's ~86 KB —
  both parsed once, cached on the record, never re-read. No new per-tick allocation.
- **Startup / first render.** The additions are ~6.5 MB of SVG across three documents, each
  carrying one or two embedded PNGs. `SvgImageView` caches its raster by
  `uri|width|height|tint`, so the card decodes once per size. The colourway strip (decision 3)
  exists mainly for this: it halves both the repo cost and the first-decode cost, and the pixels
  removed were never visible.
- **Allocations.** `TRacerArtworkLayout.Layout` is a static readonly dictionary of 80 `readonly
  record struct` bounds, built once. `ArtworkKeyBounds` returns the same instance every read —
  the overlay's `RebuildKeyboard` reads it per rebuild, not per frame.
- **Shared state.** None added. `RgbKeyboardArtCatalog` and `TRacerArtworkLayout` are static
  read-only maps, safe to share across the device-I/O and UI threads.
- **Resource lifetime.** None added. No new `IDisposable`, no new event subscription — the
  overlay's existing subscribe/unsubscribe pair is untouched.

## Risks

- **Artwork/layout drift.** `TRacerArtworkLayout`'s coordinates are tied to this artwork
  revision. Replacing `PG_T-Racer_keys.svg` invalidates all 80 rectangles with nothing at
  runtime to detect it — the same standing risk `RasterArtworkLayout` carries. Mitigated by the
  script's row-shape assertion, which fails rather than emitting a plausible-looking wrong table,
  and by keeping the script in the repo so a re-export is a re-run rather than a re-derivation.
- **The row-walk join is positional.** Bounds are matched to key names by sorting into rows and
  walking left to right against the catalog. If the artwork ever draws a key the catalog does not
  list (or vice versa) anywhere but the end of a row, every key after it in that row shifts by
  one. The row-count assertion catches a changed count; it does not catch a substitution.
  `KeysDoNotOverlap` and the manual hover pass in QA step 8 are the backstops.
- **The colourway strip is verified, not proven.** The check is "no pixel where the hidden
  layer's alpha exceeds the visible layer's by more than 8/255", which is the right property but
  is asserted against these two specific images. A future artwork revision with genuinely
  different alpha between colourways would be caught by the script's own guard — it aborts rather
  than stripping — but only if the guard is kept when the script is next edited.
- **T-Racer's RGB write path is not exercised by this change.** `SupportsRgb` currently sits on
  the wired transport only. The preview will follow the picker regardless of whether the hardware
  does, so QA step 4 verifies the *picture*, not the keyboard. If the physical board does not
  follow, that is `../tracer-rgb/tracer-rgb-plan.md`'s territory, not a regression from here.
- **`#keyboard-top` placement is a judgement call.** Decision 2 moves a layer the vendor drew
  above the keycaps to below the glow. It is the right call for the backlight, but it is a
  deviation from the delivered stacking and the first thing to revisit if the chassis looks
  wrong next to the delivered file.

## Open questions

None blocking — `/coder` can execute this as written. Two items to confirm alongside the work:

1. **Committing the delivered SVG with the white colourway stripped.** The default is to strip
   (decision 3): it is unobservable, halves the payload, and halves the card's decode cost. If
   the delivered file is wanted byte-verbatim for provenance, that is a one-line change to the
   script and ~3.3 MB, and `CardSvg` would then point at a stripped `Card/PG_T-Racer_card.svg`
   instead. Called out because the user handed over a file and it is being altered on the way in.
2. **`Card/` convention.** T-Racer is the first board whose card artwork is the same file as its
   flat image, because only one render was delivered. If a lit product shot arrives later, adding
   `Card/PG_T-Racer_card.svg` and repointing `CardSvg` restores the convention with no other change.

## Review findings

Maintained by `/reviewer` as a round-by-round journal. `/coder` and `/test-writer` read this
section and mark items `[x]` as they are closed.

### Round 1 — Ship: yes (conditional on the QA visual pass)

Surface: no commits and no branch — the whole change sits **uncommitted on `main`**
(9 modified, 8 untracked paths). Reviewed via `git diff` plus direct reads of every untracked
file; `git status --porcelain` reconciles with no leftovers. Build clean, 355 + 355 tests pass.

#### Plan conformance

Conformance is high. Every File-level change list item is implemented, and the two divergences
were both disclosed by `/coder` rather than left to be found.

- **Implemented as planned:** the five assets; `TRacerArtworkLayout` (80 keys); the catalog row;
  the `IsTRacerDevice` branch in `ArtworkKeyOverlayLayout`; `ImagePath`; the `extract-legends.py`
  board entry; the overlay `.axaml` comment; all six `docs/CONTEXT.md` entries; the stale-line
  corrections in the `raster-svg-effects` plan; all five planned `TRacerArtworkLayoutTests` cases
  and both planned test edits.
- **Verified independently, not taken on trust:** the glow geometry lies at
  `(94.8, 51.6)–(1551.0, 639.3)` inside the `1595.294 × 689.31` viewBox; all six `#keys-solid`
  subpaths have **non-overlapping bounding boxes**, so the plan's "disjoint, no fill-rule prefix
  needed" claim holds and `Geometry.Parse`'s EvenOdd default cannot punch a hole; the measured
  keycap table's names match `TRacerKeyCatalog` exactly and its bounds nest correctly inside the
  glow region on all four edges.
- **Deviates (accepted, disclosed):** `ArtworkDimensions_MatchTheMeasuredTable` was added beyond
  the plan's test list — `tests/ProoveGaming.App.Tests/ArtworkKeyOverlayGateTests.cs:130`. It is
  in scope (it covers an acceptance criterion nothing else did) and it is the highest-value test
  in the change; no objection.
- **Deviates (no-op):** the plan listed a comment fix in
  `RasterKeyboardOverlayView.axaml.cs`. That file never named T-Racer, so nothing changed. The
  plan line was wrong, not the code.
- **Missing:** the glow containment check the plan promises in **Test strategy** is absent from
  the script — see **S3**. This is the one place the code does not do what the plan says.
- **Accepted limitation (per plan):** the row-walk join is positional and "does not catch a
  substitution". Residual risk stands and is *not* covered by `KeysDoNotOverlap` — see the Nits.
- **Acceptance criteria:** the five mechanically verifiable ones are met and correctly ticked.
  The six visual ones are correctly left unticked; nothing in this repo can prove overlay
  alignment without eyes on the app. That is the whole basis of the conditional verdict below.

#### Code

- [ ] **S1. The home card rasterizes a 3.43 MB SVG synchronously on the UI thread** —
  `src/ProoveGaming.Common/Controls/SvgImageView.axaml.cs:82` (`LoadSvg`) — Should-fix
  **Problem / why it matters:** `LoadSvg` runs from the constructor (`:62`) and from the
  `SourceUri` / `Width` / `Height` class handlers (`:33-37`) — all on the dispatcher — and calls
  `_bitmapCache.GetOrAdd(cacheKey, _ => Rasterize(sourceUri))`. On a cache miss `Rasterize`
  executes **inline on the UI thread**: `AssetLoader.Open` → `SKSvg.Load` (parse 3.43 MB of XML
  and decode three embedded PNGs totalling ~2.1 M pixels) → Skia render → PNG encode → PNG
  decode. `DoubleCard.axaml:120` binds `KeyboardArt.CardSvgUri` to exactly this control, so the
  first appearance of a T-Racer card on the home screen pays that cost on the dispatcher.
  The mechanism is **pre-existing** and every other keyboard card already pays a smaller version
  of it — but before this change T-Racer had no card artwork at all, so its cost was **zero**,
  and its 3.43 MB file is now the largest of the six by 2.3× (Raster's card is 1.47 MB). CLAUDE.md
  is explicit that a visible hitch is a bug to fix rather than accept, and the plan's own **Review
  focus** asked to "confirm the change adds no synchronous asset load on a binding evaluation
  path". It does add one.
  Not graded a blocker because no hitch has been *measured* — asserting a millisecond figure
  without one would be exactly the speculation this review is supposed to avoid.
  **Fix:**
  - **Option A (correct, scoped to this change):** ship a card-sized derived asset. The card
    renders at 336×160; a downscaled `Card/PG_T-Racer_card.svg` (or a plain PNG) cuts the decode
    by an order of magnitude and restores the `Card/` convention that decision 4 broke. Note that
    decision 4 optimised repo bytes and, in doing so, pessimised the UI thread — the trade was
    made without this cost being on the table.
  - **Option B (systemic, fixes all six boards):** move `Rasterize` off the dispatcher —
    `Task.Run` the rasterize, assign `image.Source` back via `Dispatcher.UIThread`. Larger blast
    radius and out of this change's scope, but it is the real fix.
  - **Either way, measure first:** open the home screen with a T-Racer card present and time the
    first frame. If it is imperceptible, downgrade this to a nit and record the number.

- [ ] **S2. The chassis layer ships two full-board renders where one composite would be
  pixel-identical** — `docs/plans/add-tracer-artwork/split-tracer-artwork.py:456` — Should-fix
  **Problem / why it matters:** `PG_T-Racer_base.svg` (2.63 MB) carries both `#keyboard-back` and
  `#keyboard-top` as separate ~1.3 MB PNG layers. But `RgbKeyboardView` draws the unlit black bed
  (`Art.Glow`, spanning `94.8–1551 × 51.6–639.3`) directly over the chassis, and the keycaps over
  that — so **the entire interior of both images is painted over**, and only the ~45–95 unit
  border strip outside the glow is ever visible. Two full-board decodes are paid on every details
  page open to show a picture frame.
  This is partly the pre-existing design (Raster's base is a full-board render too, likewise
  mostly occluded), but T-Racer *doubles* it, and that doubling is a direct consequence of
  decision 2 folding `#keyboard-top` into the base. It also means the plan's justification for
  decision 2 is incomplete in a way worth recording: it says "the chassis keeps the exact
  appearance the vendor intended", which is true — but the **keycaps** no longer get the vendor's
  0.6-opacity contrast pass at all, because `#keyboard-top`'s keycaps are under the bed while the
  visible caps come only from `#keyboard-midle`. The rendered board will read flatter than the
  delivered file, and QA step 14 (compare to the *physical* keyboard) will not catch it.
  **Fix:** composite `#keyboard-back` and `#keyboard-top`-at-0.6 into a single PNG in the script's
  base pass. It is exact (both are premultiplied RGBA at the same scale, differing only in
  translate), halves the base layer, halves that decode, and leaves the render byte-identical.
  Add a QA step comparing the rendered board side by side with the delivered SVG opened in a
  browser, which is the check that would actually catch a contrast regression.

- [ ] **S3. The glow-path containment check the plan promises is not in the script** —
  `docs/plans/add-tracer-artwork/split-tracer-artwork.py:470-475` — Should-fix
  **Problem / why it matters:** the script validates that `#keys-solid` carries no transform and
  that paths exist, then writes them out unchecked. The measured keycap bounds *are* containment
  checked (`:380`), but the glow is not. This matters more than it looks: the plan's **Test
  strategy** states that geometry cannot be asserted in tests (`Geometry.Parse` needs platform
  services) and is therefore "validated where it is produced" — naming this script for both the
  glow and the bounds. So the glow currently has **no validation anywhere**. It happens to be
  correct today (verified during this review), which makes this a latent process gap rather than
  an active bug.
  **Fix:** reuse the walker that already exists — `path_bounds` in
  `docs/plans/tint-keyboard-legends/extract-legends.py` — and fail if the glow falls outside the
  viewBox or spans implausibly little of the board, exactly as that script does for legends.
  Roughly six lines.

- [ ] **S4. `transform_of` degrades silently on a transform it does not understand** —
  `docs/plans/add-tracer-artwork/split-tracer-artwork.py:116` — Should-fix
  **Problem / why it matters:** the scale regex is `scale\(\s*([-\d.]+)\s*\)`, which matches only
  the single-argument form. Against `scale(1.185 1.185)`, `scale(1.185, 1.185)` or a
  `matrix(...)` — all of which an export tool may emit for the same geometry — it does not match
  and the function **returns 1.0**. Every keycap bound is then computed at the wrong scale and
  written out as a plausible-looking table; the row-count assertion still passes because counts
  are unaffected. The containment check at `:380` would probably catch a 1.185→1.0 shrink (the
  bounds get smaller, not larger, so it may well not), but nothing is guaranteed to.
  This is the one place in a script whose stated design principle is "fail loudly rather than
  emit plausible-wrong data" that does the opposite.
  **Fix:** parse the transform strictly — accept `translate(...)`/`scale(...)` in the forms
  actually seen, and `fail()` on anything else (two-argument scale with unequal factors, `matrix`,
  `rotate`) rather than returning a default.

#### Tests

- [ ] **S5. No test asserts the abstract-canvas fallback any more** —
  `tests/ProoveGaming.App.Tests/ArtworkKeyOverlayGateTests.cs` — Should-fix
  Every `UseArtworkKeyOverlays` assertion in the suite is now `Assert.True` (`:41`, `:50`, `:63`,
  `:100`). `TRacer_FallsBackToTheAbstractCanvas` was the only assertion of the negative branch,
  and inverting it was correct — but nothing replaced it, so the gate's false path is unpinned.
  Worth surfacing beyond the test file: with T-Racer crossing over, **all four** boards that
  `HasInteractiveKeyboardOverlay` admits now have measured tables, which makes the bare-canvas
  branch of `RasterKeyboardOverlayView.RebuildKeyboard` (`:72-95`), the `OverlayCanvasWidth` /
  `OverlayCanvasHeight` T-Racer and Raster branches (`DeviceDetailsViewModel.KeyAssignments.cs:410,414`)
  and the `*VisualLayout.Layout` lookups at `:445-460` **unreachable in production**. That is not
  a defect — the plan deliberately kept them as fallbacks — but it is now an entire dead
  subsystem with its only boundary test deleted.
  **Cover:** assert `UseArtworkKeyOverlays == false`, `ArtworkKeyBounds` empty and
  `OverlayCanvasWidth == TRacerVisualLayout.CanvasWidth` for a device whose name does not resolve
  in the catalog (e.g. the pre-rename `DragonFang` name the `tracer-rgb` plan describes, with the
  wired VID/PID). That both restores the lost assertion and pins the real degradation path — a
  card persisted under the old name still renders, on the bare canvas, instead of throwing.

#### Nits

- `KeysDoNotOverlap`'s comment claims it catches a row shift —
  `tests/ProoveGaming.App.Tests/TRacerArtworkLayoutTests.cs:64-66`. It does not: a row shifted by
  one leaves every key on a *neighbour's* rect, which does not overlap. It catches doubling only;
  the real guard against a shift is the script's per-row count assertion. The comment should say
  so. A width-ratio sanity check (Space ≈ 6.25 u, Backspace ≈ 2 u) would be the assertion that
  actually catches a substitution.
- `SKSvg` is created without `using` — `src/ProoveGaming.Common/Controls/SvgImageView.axaml.cs:90`.
  Pre-existing and not introduced here, but it is `IDisposable` holding a native `SKPicture`, and
  this change hands it the largest document in the app, so the retained native memory grows with it.
- `TRacerVisualLayout.cs:37-39` still says `PrintScreen` is a "Placeholder position — verify
  against a photo". The artwork has now settled it. The plan deliberately scoped this out as
  churn, which is defensible, but the comment now invites a future reader to trust a placeholder
  in code that is no longer reached.
- `SVG_OPEN` hardcodes `--keyboard-rgb-color` / `--keyboard-text-color` rather than carrying them
  from the source document — `split-tracer-artwork.py:263`. Inert today (nothing in either split
  layer is styled by them once the legend groups are lifted out), so this is bookkeeping only.
- The change is uncommitted on `main` with no branch and no PR. It needs a branch before it can
  be reviewed by a human or land through CI.

#### What's done well

- **The colourway strip is verified, not asserted.** `strip_colourways` decodes both PNGs of each
  pair and aborts unless the top one occludes the bottom within 8/255 — the saving is measured at
  the moment it is taken, and a future artwork revision that stops occluding fails the build of
  the assets rather than silently losing pixels. This is the right shape for a destructive
  optimisation and should not be weakened.
- **The positional join is fenced by two independent assertions** — per-row component counts, and
  `ROWS` flattened against `TRacerKeyCatalog.Keys` parsed out of the C# source. The second is the
  good one: it ties the generated table to the catalog it must agree with, so the two cannot drift
  apart unnoticed.
- **`ArtworkDimensions_MatchTheMeasuredTable`** cross-checks the catalog's `Width`/`Height`
  against the layout's viewBox for all three artwork boards. A mismatch there scales every overlay
  off its keycaps with no other symptom; this was genuinely uncovered before.
- Idempotency was demonstrated (`md5sum -c` across a second run), not assumed — which is what
  makes the "re-run reproduces it" acceptance criterion worth anything.
- Comments carry the *why*: the keyed-by-name note on the new branch explains why the Aress slot
  join does not apply, and the `CardSvg` note explains an unusual choice at the point someone
  would question it.

#### Hardware verification (Round 1)

Run after the review, against a T-Racer on USB cable with the from-source DeviceHost. The app
was driven programmatically (launch → home card → details → Key Assignments → hover → click →
target pick → Save Preset) and the DeviceHost log read for the device write. Result: **the
feature works end to end.**

| Step | Result |
|---|---|
| Home card | T-Racer artwork renders (it rendered nothing before this change) |
| Details page | Layered board, **black key wells**, legends legible |
| Key Assignments | Board renders; no labelled grid |
| Hover `E` | Highlight box lands **exactly** on the `E` keycap — overlay alignment confirmed |
| Click `E` | Selects; panel header changes to "E button"; key stays highlighted |
| Target list | `E` row is auto-selected, matching the key's current mapping |
| Pick `R` | Stages the remap, `SAVE PRESET` becomes enabled |
| Save Preset → SAVE | `SetKeyMapping matrixIndex=35 code2=0x15`; firmware `get-after` reads back `0x15` |
| Revert to `E` | `code2=0x08` written, read back `0x08` — device left as found |

Two things this settles:

- The artwork/overlay work is correct on real hardware. The alignment risk the verdict was
  conditional on is closed.
- **"Key Assignments doesn't work" was not a defect.** Key remaps on Raster and T-Racer are
  *staged*, not written immediately: `SelectAssignmentTarget` routes plain key remaps to
  `StageAssignmentTarget`, and they only reach the keyboard via `Save Preset` → the confirm
  dialog → `ApplyKeyOverridesToDeviceAsync`. Picking a target and expecting the physical key to
  change does nothing until that dialog is confirmed. See **S6** below.

#### Follow-up findings from the hardware pass

- [ ] **S6. Selecting a key gives no visible feedback in the target list** — Should-fix
  `src/ProoveGaming.App/Views/DeviceDetails/Sections/KeyAssignmentsSectionView.axaml`
  **Problem / why it matters:** the list holds 80 rows and deliberately does not auto-scroll (a
  documented decision — `RadioButton` + `GroupName` in a `ScrollViewer` used to jump the view).
  `SyncAssignmentTargetSelection` does correctly select the matching row, but for any key past
  the F-row that row is **off-screen**, so clicking a keycap appears to do nothing to the list.
  Observed directly: clicking `E` selected the `E` row ~30 rows down, invisible without
  scrolling. Combined with the staged-write model, this is what makes the feature read as
  broken. Not introduced by this change — but this change is what put a realistic keyboard on
  screen and raised the expectation.
  **Fix:** bring the selected row into view without the jump the original decision avoided —
  e.g. `BringIntoView` on the selected container only when selection changes from the *keyboard*
  (not from a list click), or show the current mapping in the "E button" header
  ("E button → R") so the state is visible without scrolling at all.

- [ ] **S7. The matrix probe maps fewer slots than the catalog has keys** — Should-fix
  `src/ProoveGaming.Infrastructure/Services/TRacerKeyboardKeyMappingService.cs`
  **Problem / why it matters:** the log reports `T-Racer probe complete: 76 slots mapped`
  against `TRacerKeyCatalog`'s **80** remappable keys. `ResolveMatrixSlot` falls back to
  `physicalKey.MatrixIndex` for the four unmapped ones — and those catalog indices are seeded
  from Raster, with `PrintScreen`'s marked in-source as an unverified placeholder. Remapping one
  of those four would write to whatever slot the placeholder names, which may belong to a
  different key: the failure mode is "I remapped `PrintScreen` and some other key changed".
  Pre-existing and unrelated to the artwork change, but now reachable, because `PrintScreen`
  finally has an overlay to click.
  **Fix:** log which catalog keys got no probe entry, then either disable those overlays or
  extend the probe. First step is cheap: dump the unmapped `DevKeyName`s at the end of the probe.

#### Verdict

Ship: yes — the QA condition below has since been **met** (see Hardware verification). No blockers: the build is clean, both test
projects are green, and every number in the generated data checks out against the artwork, the
viewBox and the catalog independently. But **nothing here can prove the overlays land on the
keycaps** — that is one human glance, and if it fails the whole change fails, so do not merge on
the test result alone.
First: 1) run QA steps 7–9 and 3–4, since they gate the verdict. 2) **S1** — measure the home-card
rasterize and fix it if it is visible; it is the only finding that can degrade a screen users see
constantly. 3) **S3** and **S4** together — both are small edits to the same script and both close
gaps between what the plan promises about validation and what it actually does.
