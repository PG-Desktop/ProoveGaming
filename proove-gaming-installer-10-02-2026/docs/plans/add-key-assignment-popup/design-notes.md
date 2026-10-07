# Key Assignment Dialog — decoded design spec

Source: three SVG mockups supplied with the request (Figma export, ids `4499_18573`,
`4506_18891`, `4511_24319`). The text in the mockups is outlined (glyph paths, no
`<text>` elements); the wording below was decoded from the path geometry, so **confirm
final copy against Figma before shipping** (see plan → Open questions). Notably the
middle tab decodes as **SYSTEM**, not "SELECT" as paraphrased in the request.

## Overall composition

- Full-window modal on a dimmed/blurred background. The panel emits a `#6357FF`
  glow (SVG drop shadow: 100&nbsp;px blur, 35 % opacity, colour `0.388, 0.341, 1.0`).
- Central black panel **836 × 620** px (first mockup 836 × 631) at x = 242.
- Two decorative black **side rails, 42 px wide**, flanking the panel
  (left at x = 200, right at x = 1078), with 45° chamfer cuts top and bottom
  (top-left cut ~21 px, bottom cuts mirrored).
- The **right rail carries the close ✕** near its top (glyph ~14 px, colour
  `#BEBCEA` at 70 %).

## Header row (inside panel, top)

- Left: title in the blocky pixel display font, white — the selected key name plus
  the word "button": mockups show **"ESC button"**.
- Right: circular-arrow **reset icon + "RESET"** label, both `#BEBCEA` at 50 %.

## Tab strip (≈ 84 px below panel top, row height 56)

Three tabs: **KEYS · SYSTEM · MACROS** (white pixel-font labels).

- Active tab: `#6357FF` at 30 % fill block behind the label, with chamfered
  parallelogram accents (15 px 45° wedges) on both sides of the block.
- Inactive tabs: label only, no fill.
- Mockup 1 = KEYS active, mockup 2 = SYSTEM active, mockup 3 = MACROS active.

## Hint line (single sentence under the tabs)

- ~16 px, colour `#BEBCEA` at 70 %, one line, differs per tab
  (mockup 1 reads approximately "To assign …", mockup 3 "Choose …"; exact copy TBD).

## KEYS tab body (mockup 1)

- Keyboard photo, full panel width (836 × 379), acting as the **remap target
  picker**: key rects overlaid on the photo, `2.5 px #6357FF` stroke with 10 %
  fill, corner radius ≈ 3.75; the **currently assigned target key is filled
  `#6357FF` at 80 %**.
- Implementation note: this maps 1:1 onto the existing interactive keyboard
  overlay (`RasterKeyboardOverlayView`) rendered with target-picking semantics.

## SYSTEM / MACROS tab body (mockups 2 & 3)

- **Search field**: 836 × 47, 1 px border `#BEBCEA` at 30 %, magnifier icon left,
  placeholder "Search" in `#BEBCEA` at 50 %.
- **Rows**: full-width, height 54–56, fill white at 5 %, 8–10 px vertical gap,
  16 px white label. Selected row gets a **2 px `#6357FF` border** (mockup 2, first
  row). Sample row labels decode as "Quick Unique Action 1", "Push to Talk",
  "Screenshot" (placeholder content).
- MACROS mockup shows the same list shape with no selection.

## Palette summary

| Role | Value |
|---|---|
| Panel / rails | `#000000` |
| Accent (selection, active tab, glow) | `#6357FF` (fills at 10 / 30 / 80 %) |
| Secondary text / icons | `#BEBCEA` (at 30 / 50 / 70 %) |
| Primary text | `#FFFFFF` |
| Row fill | `#FFFFFF` at 5 % |
