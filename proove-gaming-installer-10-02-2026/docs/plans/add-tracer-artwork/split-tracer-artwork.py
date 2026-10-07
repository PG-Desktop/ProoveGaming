"""Split the delivered T-Racer artwork into the layers the App renders, and measure its keycaps.

Sibling of `docs/plans/tint-keyboard-legends/extract-legends.py`, and deliberately as strict:
anything unexpected in the artwork aborts rather than being skipped, because every failure this
guards against ships as *plausible-looking but wrong* pixels rather than as an error.

T-Racer's SVG is a newer export generation than the other five boards. Instead of one chassis
image plus one keycap image, it carries seven groups:

    #keyboard-back    full board render (chassis AND keycaps)
    #keys-solid       the backlight geometry, as vector paths
    #keys-blur        the same paths, blurred - the vendor's bloom over the caps
    #keyboard-midle   keycaps only, transparent background
    #keyboard-top     full board again at opacity .6, a contrast pass
    #text-solid       keycap legends
    #text-blur        the legends' halo

...and every <image> is a *pair*: the white keycap colourway with the black one drawn over it.

Run from the repository root:
    python docs/plans/add-tracer-artwork/split-tracer-artwork.py
    python docs/plans/tint-keyboard-legends/extract-legends.py

The second command strips #text-solid / #text-blur out of the keys layer into
Assets/Keyboards/Legend/T-Racer.path. This script leaves them in place on purpose so the legend
pipeline stays one implementation for all boards.

Re-running is safe: the colourway strip detects that it has already run and skips.
"""
import base64
import io
import os
import re
import struct
import zlib

ART = "src/ProoveGaming.App/Assets/Keyboards"
SOURCE = f"{ART}/PG_Keyboard_RGB-off_T-Racer_black.svg"
BASE_OUT = f"{ART}/PG_T-Racer_base.svg"
KEYS_OUT = f"{ART}/PG_T-Racer_keys.svg"
GLOW_OUT = f"{ART}/Glow/T-Racer.path"
LAYOUT_OUT = "src/ProoveGaming.App/Models/Keyboards/TRacerArtworkLayout.cs"
CATALOG = "src/ProoveGaming.App/Models/Keyboards/TRacerKeyCatalog.cs"

VIEWBOX_W = 1595.294
VIEWBOX_H = 689.31

# The groups whose <image> children come in colourway pairs.
PAIRED_GROUPS = ("keyboard-back", "keyboard-midle", "keyboard-top")

# Alpha budget for the occlusion check in pass 1. The two colourways are the same render, so
# their alpha channels should be identical; 8/255 leaves room for antialiasing on the edges and
# nothing else.
OCCLUSION_TOLERANCE = 8

# Keycap measurement, on the keys layer's alpha channel.
ALPHA_THRESHOLD = 128
MIN_COMPONENT_AREA = 200        # discards stray antialiasing specks, not keycaps
ROW_GAP = 40                    # px; row pitch is ~79, so this cannot merge two rows

# Which key each measured keycap is, row by row, left to right. None is the FN key: the artwork
# draws it, but it is not remappable and must get no overlay - the same exclusion Raster makes.
# Flattened, this must equal TRacerKeyCatalog.Keys in declaration order, which is asserted below.
ROWS = [
    ["Escape", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
     "PrintScreen"],
    ["Grave", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "Minus", "Equal", "Backspace",
     "Del"],
    ["Tab", "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P", "LeftBracket", "RightBracket",
     "Backslash", "PgUp"],
    ["CapsLock", "A", "S", "D", "F", "G", "H", "J", "K", "L", "Semicolon", "Apostrophe", "Enter",
     "PgDn"],
    ["LeftShift", "Z", "X", "C", "V", "B", "N", "M", "Comma", "Period", "Slash", "RightShift",
     "Up", "End"],
    ["LeftCtrl", "Win", "LeftAlt", "Space", "RightAlt", None, "Left", "Down", "Right"],
]


def fail(message):
    raise SystemExit(f"split-tracer-artwork: {message}")


# ── SVG ───────────────────────────────────────────────────────────────────────

def group_span(svg, group_id):
    """Start/end offsets of `<g id="...">...</g>`, counting nested <g> so the right tag closes it."""
    m = re.search(rf'<g\s+id="{re.escape(group_id)}"[^>]*>', svg)
    if not m:
        fail(f"the artwork has no #{group_id} group - its structure has changed")

    depth, end = 1, m.end()
    for tag in re.finditer(r"<g\b|</g\s*>", svg[m.end():]):
        depth += 1 if tag.group(0).startswith("<g") else -1
        if depth == 0:
            end = m.end() + tag.end()
            break
    return m.start(), end


def group_of(svg, group_id):
    start, end = group_span(svg, group_id)
    return svg[start:end]


def images_in(fragment):
    return list(re.finditer(r"<image\b[^>]*>", fragment))


def png_of(image_tag):
    m = re.search(r"base64,([A-Za-z0-9+/=]*)", image_tag)
    if not m:
        fail("an <image> carries no base64 payload - the artwork is not self-contained")
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


# ── PNG ───────────────────────────────────────────────────────────────────────

def decode_rgba(data):
    """Minimal 8-bit RGBA PNG decoder. Pillow is not a dependency of this repo's tooling."""
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        fail("an embedded image is not a PNG")

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


# ── Pass 1: colourway strip ───────────────────────────────────────────────────

def strip_colourways(svg):
    """Drop the hidden white-keycap render from each paired group.

    Every <image> in the artwork is doubled: the white colourway, then the black one drawn over
    it at the same position and size. Only the black one is ever seen, so the white halves are
    ~2.9 MB of payload that cannot affect a single pixel. The occlusion is re-verified here
    rather than taken on trust, because it is the entire justification for removing them.
    """
    stripped = 0

    for group in PAIRED_GROUPS:
        images = images_in(group_of(svg, group))
        if len(images) == 1:
            continue                    # already stripped - this script has run before
        if len(images) != 2:
            fail(f"#{group} holds {len(images)} images, expected the usual colourway pair")

        hidden, visible = (decode_rgba(png_of(m.group(0))) for m in images)
        if hidden[:2] != visible[:2]:
            fail(f"#{group}: the two colourways differ in size, so one cannot occlude the other")

        width, height, hidden_px = hidden
        visible_px = visible[2]
        leaks = sum(
            1 for i in range(3, len(hidden_px), 4)
            if hidden_px[i] - visible_px[i] > OCCLUSION_TOLERANCE
        )
        if leaks:
            fail(f"#{group}: {leaks} of {width * height} pixels show through the top colourway - "
                 f"it no longer occludes the one below, so removing it would change the render")

        # Re-find the span in the live document: earlier groups may already have shrunk it.
        start, end = group_span(svg, group)
        fragment = svg[start:end]
        first = images_in(fragment)[0]
        svg = svg[:start] + fragment[:first.start()] + fragment[first.end():] + svg[end:]
        stripped += 1

    return svg, stripped


# ── Pass 2/3: the split layers ────────────────────────────────────────────────

SVG_OPEN = (
    '<?xml version="1.0" encoding="UTF-8"?>\n'
    '<svg id="{id}" xmlns="http://www.w3.org/2000/svg" version="1.1" '
    'xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 {w} {h}" width="{w}" height="{h}" '
    'style="--keyboard-rgb-color: #0f53ff; --keyboard-text-color: #0f53ff; '
    '--keyboard-text-glow-color: #0f53ff;">\n'
)


def write_layer(path, svg_id, parts):
    body = "\n".join(parts)
    document = SVG_OPEN.format(id=svg_id, w=VIEWBOX_W, h=VIEWBOX_H) + body + "\n</svg>\n"
    io.open(path, "w", encoding="utf-8", newline="\n").write(document)
    return len(document)


# ── Pass 5: keycap measurement ────────────────────────────────────────────────

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
    """`TRacerKeyCatalog.Keys` in declaration order, so ROWS cannot drift from it unnoticed."""
    source = io.open(CATALOG, encoding="utf-8-sig").read()
    body = source[source.index("public static IReadOnlyList<PhysicalKey> Keys"):]
    return re.findall(r'new\("([^"]+)"', body[:body.index("];")])


def measure(keys_svg):
    """Key name -> bounds in artwork viewBox units, from the keycap layer's alpha channel."""
    image = images_in(group_of(keys_svg, "keyboard-midle"))[-1].group(0)
    tx, ty, scale = transform_of(image)
    width, height, pixels = decode_rgba(png_of(image))

    rows = into_rows(components(width, height, pixels))
    measured = [len(row) for row in rows]
    expected = [len(row) for row in ROWS]
    if measured != expected:
        fail(f"the keycap layer labels {measured} keycaps per row, expected {expected}. The "
             f"artwork has been re-exported with a different key set - update ROWS to match it "
             f"rather than shifting every key in the affected row by one.")

    catalog = catalog_key_names()
    flattened = [name for row in ROWS for name in row if name is not None]
    if flattened != catalog:
        fail(f"ROWS lists {len(flattened)} keys and TRacerKeyCatalog.Keys has {len(catalog)}, or "
             f"they disagree on order. First divergence: "
             f"{next((f'{a} vs {b}' for a, b in zip(flattened, catalog) if a != b), 'length only')}")

    layout = {}
    for row, names in zip(rows, ROWS):
        for box, name in zip(row, names):
            if name is None:
                continue                # FN: drawn by the artwork, not remappable
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
                 f"{VIEWBOX_W}x{VIEWBOX_H} viewBox - the group transform was applied wrongly")

    return layout


def write_layout(layout):
    def number(value):
        return f"{value:.3f}".rstrip("0").rstrip(".")

    lines = [
        "namespace ProoveGaming.App.Models.Keyboards;",
        "",
        "/// <summary>",
        "/// Per-key bounds for T-Racer in the coordinate space of its artwork's viewBox",
        f"/// (0 0 {VIEWBOX_W} {VIEWBOX_H}), so key-assignment overlays can be drawn directly on the",
        "/// rendered keyboard rather than on a separate abstract canvas.",
        "/// </summary>",
        "/// <remarks>",
        "/// Generated by <c>docs/plans/add-tracer-artwork/split-tracer-artwork.py</c>, not",
        "/// hand-authored: measured from the keycap layer's alpha channel and mapped into viewBox",
        "/// units by that layer's own <c>translate(96.847 52.908) scale(1.185)</c>. The 34px-unit",
        "/// logical grid in <see cref=\"TRacerVisualLayout\"/> has different proportions, so overlays",
        "/// derived from it would drift; that layout remains the fallback for boards with no measured",
        "/// table. The artwork also draws an FN key, which is not remappable and therefore has no",
        "/// entry. Coordinates are tied to this artwork revision - replacing the keys SVG invalidates",
        "/// every one of them.",
        "/// </remarks>",
        "public static class TRacerArtworkLayout",
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


# ── Driver ────────────────────────────────────────────────────────────────────

def main():
    os.makedirs(f"{ART}/Glow", exist_ok=True)

    svg = io.open(SOURCE, encoding="utf-8-sig").read()
    original_size = len(svg)

    svg, stripped = strip_colourways(svg)
    if stripped:
        io.open(SOURCE, "w", encoding="utf-8", newline="\n").write(svg)
    print(f"colourways  {stripped} stripped   "
          f"source {original_size:>10,} -> {len(svg):>10,} chars")

    defs = svg[svg.index("<defs>"):svg.index("</defs>") + len("</defs>")]
    style = svg[svg.index("<style>"):svg.index("</style>") + len("</style>")]

    # The chassis. #keyboard-top is the vendor's contrast pass over the same render as
    # #keyboard-back; it belongs here rather than above the keycaps, where the artwork draws it,
    # because a 60%-opacity full-board image over the glow repaints the gaps with chassis pixels
    # and washes the backlight out. Nothing tinted may sit above the keycap layer.
    base = write_layer(BASE_OUT, "t-racer-base",
                       [group_of(svg, "keyboard-back"), group_of(svg, "keyboard-top")])
    print(f"base        {base:>10,} chars   #keyboard-back + #keyboard-top")

    # The keycaps, plus the legend groups extract-legends.py lifts out next.
    keys = write_layer(KEYS_OUT, "t-racer-keys",
                       [defs, style, group_of(svg, "keyboard-midle"),
                        group_of(svg, "text-solid"), group_of(svg, "text-blur")])
    print(f"keys        {keys:>10,} chars   #keyboard-midle + legends")

    # The backlight. Every other board's glow had to be hand-derived from the artwork; T-Racer
    # ships it as vectors, already in viewBox units with no transform on the group.
    glow_group = group_of(svg, "keys-solid")
    if re.search(r'<g\s+id="keys-solid"[^>]*transform=', glow_group):
        fail("#keys-solid has grown a transform - the glow is no longer in viewBox units")
    glow_paths = re.findall(r'<path\b[^>]*\bd="([^"]+)"', glow_group)
    if not glow_paths:
        fail("#keys-solid holds no path data")
    io.open(GLOW_OUT, "w", encoding="utf-8", newline="\n").write(
        " ".join(d.strip() for d in glow_paths))
    print(f"glow        {len(glow_paths):>10} paths   -> {GLOW_OUT}")

    layout = measure(svg)
    written = write_layout(layout)
    print(f"layout      {len(layout):>10} keys    -> {LAYOUT_OUT} ({written:,} chars)")


if __name__ == "__main__":
    main()
