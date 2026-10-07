"""Extract keycap legend geometry from the split keyboard artwork.

For each `<board>_keys.svg` that carries the vendor's recolourable text groups:

  * writes `Assets/Keyboards/Legend/<Board>.path` — every glyph outline from `#text-solid`,
    concatenated, prefixed with `F1` so Avalonia uses the nonzero fill rule SVG defaults to
    (glyph counters like the hole in "O" depend on it);
  * removes `#text-solid` and `#text-blur` from the SVG, leaving the keycap image behind.

Why: those two groups take their fill from a CSS variable pinned to a static brand colour, so
the legends cannot follow the backlight. Re-parsing the SVG to change that variable is not an
option — the documents are ~800 KB with an embedded PNG and the tint animates per frame. Drawing
the geometry ourselves as a tinted Path costs nothing per frame, which is the same reason the
baked-in backlight was stripped into Glow/*.path.

Run from the repository root:
    python docs/plans/tint-keyboard-legends/extract-legends.py
"""
import io
import os
import re

ART = "src/ProoveGaming.App/Assets/Keyboards"
OUT = f"{ART}/Legend"

# _keys.svg -> the name used by RgbKeyboardArtCatalog / the Glow assets.
BOARDS = {
    "PG_Raster_keys.svg": "Raster",
    "PG_Rampage_keys.svg": "Rampage",
    "PG_Aress_keys.svg": "Aress",
    "PG_Cyrex_keys.svg": "Cyrex",
    "PG_T801_keys.svg": "T800",
    "PG_T-Racer_keys.svg": "T-Racer",
}


def attrs_of(tag):
    return {k: v for k, v in re.findall(r'([a-zA-Z-]+)="([^"]*)"', tag)}


def num(a, key, default=0.0):
    try:
        return float(a.get(key, default))
    except ValueError:
        return default


def rect_to_path(tag):
    """A rounded <rect> as path data. These carry the bar-shaped glyph parts — the pipe, the
    hyphen, the arrow stems — so dropping them loses pieces of real legends."""
    a = attrs_of(tag)
    x, y = num(a, "x"), num(a, "y")
    w, h = num(a, "width"), num(a, "height")
    rx = num(a, "rx", num(a, "ry", 0.0))
    ry = num(a, "ry", rx)
    rx, ry = min(rx, w / 2), min(ry, h / 2)

    if rx <= 0 or ry <= 0:
        return f"M{x},{y} H{x + w} V{y + h} H{x} Z"

    return (
        f"M{x + rx},{y} H{x + w - rx} A{rx},{ry} 0 0 1 {x + w},{y + ry} "
        f"V{y + h - ry} A{rx},{ry} 0 0 1 {x + w - rx},{y + h} "
        f"H{x + rx} A{rx},{ry} 0 0 1 {x},{y + h - ry} "
        f"V{y + ry} A{rx},{ry} 0 0 1 {x + rx},{y} Z"
    )


def polygon_to_path(tag):
    pts = re.findall(r"-?[\d.]+", attrs_of(tag).get("points", ""))
    if len(pts) < 4:
        return None
    pairs = [f"{pts[i]},{pts[i + 1]}" for i in range(0, len(pts) - 1, 2)]
    return "M" + " L".join(pairs) + " Z"


def shapes_to_path_data(fragment, board):
    """Every drawable shape in the fragment, as path data.

    Deliberately strict: an unrecognised shape aborts rather than being skipped. The first
    version of this script matched only `d="..."` and silently dropped 10 rects and 2 polygons
    from Rampage, which would have shipped legends with missing pieces.
    """
    out = []
    for m in re.finditer(r"<([a-zA-Z]+)\b[^>]*?/?>", fragment):
        name, tag = m.group(1), m.group(0)
        if name == "path":
            d = attrs_of(tag).get("d")
            if d:
                out.append(d.strip())
        elif name == "rect":
            out.append(rect_to_path(tag))
        elif name == "polygon":
            p = polygon_to_path(tag)
            if p:
                out.append(p)
        elif name in ("g", "title", "desc", "metadata"):
            continue
        else:
            raise SystemExit(
                f"{board}: unhandled shape <{name}> in the legend group. Add a converter for it "
                f"rather than letting it be dropped — that is how the first run lost Rampage's "
                f"rects and polygons.\n  {tag[:160]}"
            )
    return out


# SVG path data omits separators wherever a number is unambiguous, so "0-.276.224-.5" is four
# numbers. A greedy [\d.]+ swallows "-.5.5" and fails to parse; this stops at the second decimal
# point the way the SVG grammar does.
TOKEN = re.compile(r"[MmZzLlHhVvCcSsQqTtAa]|[+-]?(?:\d*\.\d+|\d+\.?)(?:[eE][+-]?\d+)?")


def path_bounds(data):
    """Conservative bounding box of path data, by walking the commands.

    Avalonia's Geometry.Parse needs a platform render interface, so this cannot be checked from
    the unit tests; doing it here catches the failure that actually matters — an artwork re-export
    that shifts, rescales or collapses the glyphs — at the moment the assets are generated.

    Control points are included rather than solved for, so the box is a superset of the true
    outline. That is the right side to err on for a containment check.
    """
    toks = TOKEN.findall(data)
    xs, ys, i = [], [], 0
    cx = cy = sx = sy = 0.0
    cmd = None

    def num():
        nonlocal i
        v = float(toks[i]); i += 1
        return v

    while i < len(toks):
        if re.match(r"[A-Za-z]", toks[i]):
            cmd = toks[i]; i += 1
            if cmd in "Zz":
                cx, cy = sx, sy
                continue
        if cmd is None:
            i += 1
            continue

        rel = cmd.islower()
        c = cmd.upper()
        try:
            if c == "M":
                x, y = num(), num()
                cx, cy = (cx + x, cy + y) if rel else (x, y)
                sx, sy = cx, cy
                cmd = "l" if rel else "L"      # subsequent pairs are implicit linetos
            elif c == "L":
                x, y = num(), num()
                cx, cy = (cx + x, cy + y) if rel else (x, y)
            elif c == "H":
                x = num(); cx = cx + x if rel else x
            elif c == "V":
                y = num(); cy = cy + y if rel else y
            elif c in ("C", "S", "Q", "T"):
                pairs = {"C": 3, "S": 2, "Q": 2, "T": 1}[c]
                pts = [(num(), num()) for _ in range(pairs)]
                for px, py in pts:
                    xs.append(cx + px if rel else px)
                    ys.append(cy + py if rel else py)
                ex, ey = pts[-1]
                cx, cy = (cx + ex, cy + ey) if rel else (ex, ey)
            elif c == "A":
                num(); num(); num(); num(); num()          # rx ry rot large-arc sweep
                x, y = num(), num()
                cx, cy = (cx + x, cy + y) if rel else (x, y)
            else:
                i += 1
                continue
        except (IndexError, ValueError):
            break

        xs.append(cx); ys.append(cy)

    return (min(xs), min(ys), max(xs), max(ys)) if xs else None


def group_span(svg, group_id):
    """Start/end offsets of `<g id="...">…</g>`, counting nested <g> so the right tag closes it."""
    m = re.search(rf'<g\s+id="{re.escape(group_id)}"[^>]*>', svg)
    if not m:
        return None

    depth, i = 1, m.end()
    for tag in re.finditer(r"<g\b|</g\s*>", svg[m.end():]):
        depth += 1 if tag.group(0).startswith("<g") else -1
        if depth == 0:
            i = m.end() + tag.end()
            break
    return m.start(), i


def main():
    os.makedirs(OUT, exist_ok=True)

    for filename, board in BOARDS.items():
        path = f"{ART}/{filename}"
        svg = io.open(path, encoding="utf-8-sig").read()
        original_size = len(svg)

        solid = group_span(svg, "text-solid")
        if solid is None:
            print(f"{board:<9} no #text-solid group — skipped (artwork is structured differently)")
            continue

        glyphs = shapes_to_path_data(svg[solid[0]:solid[1]], board)
        if not glyphs:
            print(f"{board:<9} #text-solid holds no path data — skipped")
            continue

        # Nonzero fill rule, to match how the SVG itself renders these outlines.
        data = "F1 " + " ".join(g.strip() for g in glyphs)
        io.open(f"{OUT}/{board}.path", "w", encoding="utf-8", newline="\n").write(data)

        # Remove the blurred copy first so the solid span offsets stay valid.
        for group in ("text-blur", "text-solid"):
            span = group_span(svg, group)
            if span:
                svg = svg[:span[0]] + svg[span[1]:]

        io.open(path, "w", encoding="utf-8", newline="\n").write(svg)

        vb = re.search(r'viewBox="0 0 ([\d.]+) ([\d.]+)"', svg)
        bounds = path_bounds(data)
        note = ""
        if bounds and vb:
            w, h = float(vb.group(1)), float(vb.group(2))
            x0, y0, x1, y1 = bounds
            inside = x0 >= -1 and y0 >= -1 and x1 <= w + 1 and y1 <= h + 1
            spans = (x1 - x0) > w * 0.5
            note = f"  bounds=({x0:.0f},{y0:.0f})-({x1:.0f},{y1:.0f}) of {w:.0f}x{h:.0f}"
            if not inside:
                raise SystemExit(f"{board}: legend geometry falls outside the artwork viewBox{note}")
            if not spans:
                raise SystemExit(f"{board}: legend geometry spans too little of the board{note} "
                                 f"— it likely collapsed or lost a transform")

        print(f"{board:<9} {len(glyphs):>3} glyphs -> {len(data):>7,} chars   "
              f"svg {original_size:>9,} -> {len(svg):>9,} ({100 * len(svg) / original_size:.0f}%){note}")


if __name__ == "__main__":
    main()
