"""Measure the Rampage keycaps and generate RampageArtworkLayout.cs.

Sibling of `docs/plans/add-tracer-artwork/split-tracer-artwork.py`, and deliberately as strict:
anything unexpected in the artwork aborts rather than being skipped, because every failure this
guards against ships as *plausible-looking but wrong* overlay positions rather than as an error.

Unlike T-Racer, the Rampage artwork is already split (`PG_Rampage_base.svg` / `PG_Rampage_keys.svg`
exist, legends extracted) - this script only performs the measurement pass: decode the keycap
layer's embedded PNG, connected-component-label its alpha channel, assign components to
`RampageKeyCatalog` entries row by row, verify every assignment against `RampageVisualLayout` by
normalised centre position, and emit the C# table.

The hard gate is the per-row component count: the board has 87 physical keys (86 remappable + FN),
laid out in the six ROWS below. The Aress precedent - artwork that does not depict its own board -
must be caught here, never shipped.

Run from the repository root:
    python docs/plans/add-rampage-artwork-overlays/measure-rampage-artwork.py
"""
import base64
import io
import re
import struct
import sys
import zlib

KEYS_SVG = "src/ProoveGaming.App/Assets/Keyboards/PG_Rampage_keys.svg"
CATALOG = "src/ProoveGaming.App/Models/Keyboards/RampageKeyCatalog.cs"
GRID = "src/ProoveGaming.App/Models/Keyboards/RampageVisualLayout.cs"
LAYOUT_OUT = "src/ProoveGaming.App/Models/Keyboards/RampageArtworkLayout.cs"

# Must match RgbKeyboardArtCatalog's entry for Rampage.
VIEWBOX_W = 1471.412
VIEWBOX_H = 577.563

ALPHA_THRESHOLD = 128
MIN_COMPONENT_AREA = 200        # discards stray antialiasing specks, not keycaps
ROW_GAP = 40                    # px; row pitch is ~80, so this cannot merge two rows

# How far a measured keycap centre may sit from RampageVisualLayout's centre for the same key,
# after normalising both sets to their own extents. The grid is derived, not measured, so some
# drift is expected; a whole-key mismatch (an assignment off by one) is ~0.055 in x on the densest
# row and fails both bounds.
MAX_RESIDUAL_X = 0.045
MAX_RESIDUAL_Y = 0.080

# The six visual rows, top to bottom, each left to right - the standard TKL arrangement the
# artwork depicts, confirmed against its legend layer (Insert/Home/PageUp on the number row,
# Delete/End/PageDown on the Tab row). Note this deliberately differs from the firmware-slot
# order in RampageKeyCatalog.Keys, which packs PageUp/PageDown electrically onto the CapsLock
# row - the catalog cross-check below is therefore by SET, not by order. None marks FN: drawn
# by the artwork, not remappable, deliberately absent from the output.
ROWS = [
    ["Escape", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
     "PrintScreen", "ScrollLock", "Pause"],
    ["Grave", "D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8", "D9", "D0", "Minus", "Equal",
     "Backspace", "Insert", "Home", "PageUp"],
    ["Tab", "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P", "BracketLeft", "BracketRight",
     "Backslash", "Delete", "End", "PageDown"],
    ["CapsLock", "A", "S", "D", "F", "G", "H", "J", "K", "L", "Semicolon", "Quote", "Enter"],
    ["LeftShift", "Z", "X", "C", "V", "B", "N", "M", "Comma", "Period", "Slash", "RightShift",
     "Up"],
    ["LeftCtrl", "LeftWin", "LeftAlt", "Space", "RightAlt", None, "Menu", "RightCtrl",
     "Left", "Down", "Right"],
]


def fail(message):
    sys.exit(f"ABORT: {message}")


def images_in(fragment):
    return list(re.finditer(r"<image\b[^>]*>", fragment))


def png_of(image_tag):
    m = re.search(r"base64,([A-Za-z0-9+/=]*)", image_tag)
    if not m:
        fail("the <image> carries no base64 payload - the artwork is not self-contained")
    return base64.b64decode(m.group(1))


def transform_of(image_tag):
    """The (tx, ty, scale) of an <image>'s `translate(...) scale(...)`, either part optional."""
    transform = re.search(r'transform="([^"]*)"', image_tag)
    if not transform:
        return 0.0, 0.0, 1.0

    value = transform.group(1)
    translate = re.search(r"translate\(\s*([-\d.]+)(?:[ ,]+([-\d.]+))?\s*\)", value)
    scale = re.search(r"scale\(\s*([-\d.]+)\s*\)", value)
    tx = float(translate.group(1)) if translate else 0.0
    ty = float(translate.group(2) or 0) if translate else 0.0
    return tx, ty, float(scale.group(1)) if scale else 1.0


def decode_rgba(data):
    """Minimal 8-bit RGBA PNG decoder. Pillow is not a dependency of this repo's tooling."""
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        fail("the embedded image is not a PNG")

    idat, header, offset = b"", None, 8
    while offset < len(data):
        length = struct.unpack(">I", data[offset:offset + 4])[0]
        kind = data[offset + 4:offset + 8]
        chunk = data[offset + 8:offset + 8 + length]
        offset += 12 + length
        if kind == b"IHDR":
            header = struct.unpack(">IIBBBBB", chunk)
        elif kind == b"IDAT":
            idat += chunk
        elif kind == b"IEND":
            break

    width, height, depth, colour, _, _, interlace = header
    if (depth, colour, interlace) != (8, 6, 0):
        fail(f"unsupported PNG format (depth={depth} colour={colour} interlace={interlace})")

    raw = zlib.decompress(idat)
    stride, bpp = width * 4, 4
    out = bytearray(height * stride)
    previous = bytearray(stride)
    offset = 0

    for y in range(height):
        method = raw[offset]
        offset += 1
        line = bytearray(raw[offset:offset + stride])
        offset += stride

        if method == 1:
            for x in range(bpp, stride):
                line[x] = (line[x] + line[x - bpp]) & 255
        elif method == 2:
            for x in range(stride):
                line[x] = (line[x] + previous[x]) & 255
        elif method == 3:
            for x in range(stride):
                left = line[x - bpp] if x >= bpp else 0
                line[x] = (line[x] + ((left + previous[x]) >> 1)) & 255
        elif method == 4:
            for x in range(stride):
                left = line[x - bpp] if x >= bpp else 0
                upper_left = previous[x - bpp] if x >= bpp else 0
                upper = previous[x]
                pa = abs(upper - upper_left)
                pb = abs(left - upper_left)
                pc = abs(left + upper - 2 * upper_left)
                if pa <= pb and pa <= pc:
                    predictor = left
                elif pb <= pc:
                    predictor = upper
                else:
                    predictor = upper_left
                line[x] = (line[x] + predictor) & 255
        elif method != 0:
            fail(f"unknown PNG filter type {method}")

        out[y * stride:(y + 1) * stride] = line
        previous = line

    return width, height, bytes(out)


def components(width, height, pixels):
    """Bounding boxes of the opaque connected components, one per keycap.

    Two-pass row labelling with union-find, 4-connected. The keycaps are separated by fully
    transparent gaps, so nothing more sophisticated is needed.
    """
    parent = [0]
    labels = [0] * (width * height)
    next_label = 1

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    def union(a, b):
        ra, rb = find(a), find(b)
        if ra != rb:
            parent[max(ra, rb)] = min(ra, rb)

    def opaque(i):
        return pixels[i * 4 + 3] >= ALPHA_THRESHOLD

    for y in range(height):
        for x in range(width):
            i = y * width + x
            if not opaque(i):
                continue
            left = labels[i - 1] if x > 0 and opaque(i - 1) else 0
            above = labels[i - width] if y > 0 and opaque(i - width) else 0
            if left and above:
                labels[i] = min(left, above)
                union(left, above)
            elif left or above:
                labels[i] = left or above
            else:
                parent.append(next_label)
                labels[i] = next_label
                next_label += 1

    boxes = {}
    for y in range(height):
        for x in range(width):
            label = labels[y * width + x]
            if not label:
                continue
            root = find(label)
            box = boxes.get(root)
            if box is None:
                boxes[root] = [x, y, x, y, 1]
            else:
                box[0] = min(box[0], x)
                box[1] = min(box[1], y)
                box[2] = max(box[2], x)
                box[3] = max(box[3], y)
                box[4] += 1

    return [b for b in boxes.values() if b[4] >= MIN_COMPONENT_AREA]


def into_rows(boxes):
    """Group keycaps into rows top-to-bottom, each sorted left-to-right."""
    rows, current = [], []
    for box in sorted(boxes, key=lambda b: b[1]):
        if current and box[1] - current[0][1] > ROW_GAP:
            rows.append(sorted(current, key=lambda b: b[0]))
            current = []
        current.append(box)
    if current:
        rows.append(sorted(current, key=lambda b: b[0]))
    return rows


def catalog_key_names():
    """`RampageKeyCatalog.Keys` in declaration order, so ROWS cannot drift from it unnoticed."""
    source = io.open(CATALOG, encoding="utf-8-sig").read()
    body = source[source.index("public static IReadOnlyList<PhysicalKey> Keys"):]
    return re.findall(r'new\("([^"]+)"', body[:body.index("];")])


def grid_centres():
    """Key name -> centre of its RampageVisualLayout bounds, for the residual cross-check."""
    source = io.open(GRID, encoding="utf-8-sig").read()

    constants = {}
    for name, value in re.findall(r"private const double (\w+) = ([\d.]+);", source):
        constants[name] = float(value)
    unit = float(re.search(r"public const double UnitSize = ([\d.]+);", source).group(1))

    def term(token):
        token = token.strip()
        return float(token) if re.fullmatch(r"[\d.]+", token) else constants[token]

    centres = {}
    for name, x, y, w, h in re.findall(
            r'\["(\w+)"\] = U\(([^,]+),\s*([^,]+),\s*([^,]+),\s*([^)]+)\)', source):
        centres[name] = ((term(x) + term(w) / 2) * unit, (term(y) + term(h) / 2) * unit)
    return centres


def normalise(centres):
    """Map an iterable of (x, y) to [0,1]^2 over its own extents."""
    xs = [c[0] for c in centres]
    ys = [c[1] for c in centres]
    x0, x1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
    return [((x - x0) / (x1 - x0), (y - y0) / (y1 - y0)) for x, y in centres]


def measure():
    keys_svg = io.open(KEYS_SVG, encoding="utf-8-sig").read()
    images = images_in(keys_svg)
    if len(images) != 1:
        fail(f"{KEYS_SVG} holds {len(images)} <image> elements, expected exactly the keycap layer")

    image = images[0].group(0)
    tx, ty, scale = transform_of(image)
    width, height, pixels = decode_rgba(png_of(image))

    rows = into_rows(components(width, height, pixels))
    measured = [len(row) for row in rows]
    expected = [len(row) for row in ROWS]
    if measured != expected:
        fail(f"the keycap layer labels {measured} keycaps per row, expected {expected}. Either "
             f"the artwork does not depict the Rampage board (the Aress precedent) or it was "
             f"re-exported with a different key set - resolve WHICH before trusting any output.")

    # By set, not order: the catalog is in firmware-slot order, which puts PageUp/PageDown on the
    # CapsLock row electrically while the physical board (and this artwork) has them in the nav
    # column of the two rows above. Per-key assignment correctness is guarded by the residual
    # check below instead.
    catalog = catalog_key_names()
    flattened = [name for row in ROWS for name in row if name is not None]
    if len(flattened) != len(set(flattened)) or set(flattened) != set(catalog):
        missing = set(catalog) - set(flattened)
        extra = set(flattened) - set(catalog)
        fail(f"ROWS and RampageKeyCatalog.Keys disagree: missing={sorted(missing)}, "
             f"extra={sorted(extra)}, duplicates included - the two must name the same 86 keys.")

    # Assign row by row, then verify EVERY assignment against the grid by normalised centre -
    # the count gate cannot catch two same-width keys swapped by a labelling quirk; this can.
    assigned = []                      # (name, box) with FN dropped
    for row, names in zip(rows, ROWS):
        for box, name in zip(row, names):
            if name is not None:
                assigned.append((name, box))

    grid = grid_centres()
    art_norm = normalise([((b[0] + b[2]) / 2, (b[1] + b[3]) / 2) for _, b in assigned])
    grid_norm = normalise([grid[name] for name, _ in assigned])
    worst = (0.0, "")
    for (name, _), (ax, ay), (gx, gy) in zip(assigned, art_norm, grid_norm):
        dx, dy = abs(ax - gx), abs(ay - gy)
        if dx > MAX_RESIDUAL_X or dy > MAX_RESIDUAL_Y:
            fail(f"{name}: measured centre is off the RampageVisualLayout position by "
                 f"(dx={dx:.3f}, dy={dy:.3f}) normalised - an assignment slipped a key.")
        if max(dx, dy) > worst[0]:
            worst = (max(dx, dy), name)
    print(f"residuals   worst {worst[0]:.3f} normalised at {worst[1]}")

    layout = {}
    for name, box in assigned:
        x0, y0, x1, y1 = box[0], box[1], box[2], box[3]
        layout[name] = (
            tx + x0 * scale,
            ty + y0 * scale,
            (x1 - x0 + 1) * scale,
            (y1 - y0 + 1) * scale,
        )

    for name, (x, y, w, h) in layout.items():
        if x < 0 or y < 0 or x + w > VIEWBOX_W + 1 or y + h > VIEWBOX_H + 1:
            fail(f"{name} measures to ({x:.1f},{y:.1f}) {w:.1f}x{h:.1f}, outside the "
                 f"{VIEWBOX_W}x{VIEWBOX_H} viewBox - the image transform was applied wrongly")

    return layout, (tx, ty, scale)


def write_layout(layout, transform):
    def number(value):
        return f"{value:.3f}".rstrip("0").rstrip(".")

    tx, ty, scale = transform
    lines = [
        "namespace ProoveGaming.App.Models.Keyboards;",
        "",
        "/// <summary>",
        "/// Per-key bounds for Rampage in the coordinate space of its artwork's viewBox",
        f"/// (0 0 {VIEWBOX_W} {VIEWBOX_H}), so key-assignment overlays can be drawn directly on the",
        "/// rendered keyboard rather than on a separate abstract canvas.",
        "/// </summary>",
        "/// <remarks>",
        "/// Generated by <c>docs/plans/add-rampage-artwork-overlays/measure-rampage-artwork.py</c>,",
        "/// not hand-authored: measured from the keycap layer's alpha channel and mapped into",
        f"/// viewBox units by that layer's own <c>translate({tx} {ty}) scale({scale})</c>. The",
        "/// 34px-unit logical grid in <see cref=\"RampageVisualLayout\"/> has different proportions,",
        "/// so overlays derived from it would drift; that layout remains the fallback data source",
        "/// for boards with no measured table. The artwork also draws an FN key, which is not",
        "/// remappable and therefore has no entry. Coordinates are tied to this artwork revision -",
        "/// replacing the keys SVG invalidates every one of them.",
        "/// </remarks>",
        "public static class RampageArtworkLayout",
        "{",
        f"\tpublic const double ViewBoxWidth = {VIEWBOX_W};",
        f"\tpublic const double ViewBoxHeight = {VIEWBOX_H};",
        "",
        "\tpublic static System.Collections.Generic.IReadOnlyDictionary<string, RasterKeyBounds> Layout { get; } =",
        "\t\tnew System.Collections.Generic.Dictionary<string, RasterKeyBounds>",
        "\t\t{",
    ]

    row_comments = ["function row", "number row", "top alpha row", "mid alpha row",
                    "bottom alpha row", "bottom row"]
    for names, comment in zip(ROWS, row_comments):
        lines.append(f"\t\t\t// {comment}")
        for name in names:
            if name is None:
                continue
            x, y, w, h = layout[name]
            lines.append(
                f'\t\t\t["{name}"] = new({number(x)}, {number(y)}, {number(w)}, {number(h)}),')

    lines += ["\t\t};", "}", ""]
    document = "\n".join(lines)
    io.open(LAYOUT_OUT, "w", encoding="utf-8", newline="\n").write(document)
    return len(document)


def main():
    layout, transform = measure()
    size = write_layout(layout, transform)
    print(f"layout      {len(layout)} keys -> {LAYOUT_OUT} ({size:,} chars)")


if __name__ == "__main__":
    main()
