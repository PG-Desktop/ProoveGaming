"""Measure the T800 keycaps from the artwork and generate `T800ArtworkLayout.cs`.

Sibling of `../add-tracer-artwork/split-tracer-artwork.py`, and deliberately as strict: anything
unexpected in the artwork aborts rather than being skipped, because every failure this guards
against ships as plausible-looking but wrong overlay positions rather than as an error.

Connected-component labelling of the alpha channel of the keycap layer
(`src/ProoveGaming.App/Assets/Keyboards/PG_T801_keys.svg`), mapped into artwork viewBox space by
that image's own placement transform, then joined to matrix slots by walking each measured row
left to right against the factory table in `samples/factory-matrix.txt`.

Two coordinate facts this depends on, both of which will silently ruin the output if they change:

  * The keys image is placed at `translate(216.208 687.954)` and drawn at 1672x536 from a
    1673x537 PNG.
  * The T800 artwork was cropped to viewBox `172 672 1742 600` (see ../t800-rgb/t800-rgb-plan.md,
    "Artwork cropping"), so the crop origin must be subtracted. Skipping that puts every overlay
    172 units to the right.

Run from the repository root:
    python docs/plans/add-t800-key-assignments/measure-t800-keycaps.py
"""
import base64
import io
import re
import sys
from collections import deque

from PIL import Image

KEYS_SVG = "src/ProoveGaming.App/Assets/Keyboards/PG_T801_keys.svg"
SAMPLE = "docs/plans/add-t800-key-assignments/samples/factory-matrix.txt"
OUT = "src/ProoveGaming.App/Models/Keyboards/T800ArtworkLayout.cs"

VIEWBOX = (172.0, 672.0, 1742.0, 600.0)
PLACEMENT = (216.208, 687.954)      # the keys image's translate()
DRAWN = (1672.0, 536.0)             # width/height it is drawn at

ALPHA_THRESHOLD = 40
MIN_COMPONENT_PX = 800
ROW_SPLIT_PX = 40                   # vertical gap that separates one keycap row from the next
EXPECTED_ROWS = [15, 15, 14, 14, 10]
FN_SLOT = 65


def die(msg):
    sys.exit(f"measure-t800-keycaps.py: {msg}")


def load_keycap_png():
    svg = open(KEYS_SVG, encoding="utf-8-sig").read()

    viewbox = [float(v) for v in re.search(r'viewBox="([^"]+)"', svg).group(1).split()]
    if tuple(viewbox) != VIEWBOX:
        die(f"viewBox is {viewbox}, expected {list(VIEWBOX)} — artwork was re-cropped, re-check this script")

    tag = re.search(r"<image[^>]*?>", svg, re.S).group(0)
    drawn = (float(re.search(r'width="([\d.]+)"', tag).group(1)),
             float(re.search(r'height="([\d.]+)"', tag).group(1)))
    if drawn != DRAWN:
        die(f"keys image is drawn at {drawn}, expected {DRAWN}")

    tr = re.search(r"translate\(([\d.\-]+)[ ,]+([\d.\-]+)\)", tag)
    placement = (float(tr.group(1)), float(tr.group(2)))
    if placement != PLACEMENT:
        die(f"keys image placement is {placement}, expected {PLACEMENT}")

    data = re.search(r"base64,([A-Za-z0-9+/=]+)", svg).group(1)
    return Image.open(io.BytesIO(base64.b64decode(data))).convert("RGBA")


def components(png):
    w, h = png.size
    solid = png.getchannel("A").point(lambda v: 1 if v > ALPHA_THRESHOLD else 0)
    px = solid.load()
    seen = [[False] * h for _ in range(w)]
    boxes = []

    for sx in range(w):
        for sy in range(h):
            if not px[sx, sy] or seen[sx][sy]:
                continue
            queue = deque([(sx, sy)])
            seen[sx][sy] = True
            x0 = x1 = sx
            y0 = y1 = sy
            count = 0
            while queue:
                x, y = queue.popleft()
                count += 1
                x0, x1 = min(x0, x), max(x1, x)
                y0, y1 = min(y0, y), max(y1, y)
                for dx, dy in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                    nx, ny = x + dx, y + dy
                    if 0 <= nx < w and 0 <= ny < h and px[nx, ny] and not seen[nx][ny]:
                        seen[nx][ny] = True
                        queue.append((nx, ny))
            if count >= MIN_COMPONENT_PX:
                boxes.append((x0, y0, x1 - x0 + 1, y1 - y0 + 1))
    return boxes


def group_rows(boxes):
    rows = []
    for box in sorted(boxes, key=lambda b: b[1]):
        if rows and box[1] - rows[-1][-1][1] <= ROW_SPLIT_PX:
            rows[-1].append(box)
        else:
            rows.append([box])
    return [sorted(r, key=lambda b: b[0]) for r in rows]


def catalog_rows():
    """Factory-table slots grouped into the same 5 visual rows, ordered left to right."""
    slots = []
    for line in open(SAMPLE, encoding="utf-8"):
        m = re.match(r"slot\s+(\d+):", line.strip())
        if m:
            slots.append(int(m.group(1)))

    rows = {}
    for slot in slots:
        rows.setdefault(slot % 6, []).append(slot)
    # Column-major matrix: within a visual row, left-to-right is ascending column = ascending slot.
    return [sorted(rows[r]) for r in sorted(rows)]


def to_viewbox(box, png_size):
    x, y, w, h = box
    sx = DRAWN[0] / png_size[0]
    sy = DRAWN[1] / png_size[1]
    return (PLACEMENT[0] + x * sx - VIEWBOX[0],
            PLACEMENT[1] + y * sy - VIEWBOX[1],
            w * sx,
            h * sy)


def main():
    png = load_keycap_png()
    rows = group_rows(components(png))

    counts = [len(r) for r in rows]
    if counts != EXPECTED_ROWS:
        die(f"measured rows {counts} != {EXPECTED_ROWS} — the artwork changed, or row splitting is wrong")

    slot_rows = catalog_rows()
    if [len(r) for r in slot_rows] != EXPECTED_ROWS:
        die(f"factory table rows {[len(r) for r in slot_rows]} != {EXPECTED_ROWS}")

    entries = []
    for measured_row, slot_row, row_no in zip(rows, slot_rows, range(1, 6)):
        entries.append((row_no, []))
        for box, slot in zip(measured_row, slot_row):
            if slot == FN_SLOT:
                continue        # FN is not remappable and gets no overlay
            x, y, w, h = to_viewbox(box, png.size)
            if x < 0 or y < 0 or x + w > VIEWBOX[2] or y + h > VIEWBOX[3]:
                die(f"slot {slot} bounds ({x:.1f},{y:.1f},{w:.1f},{h:.1f}) fall outside the viewBox")
            entries[-1][1].append((slot, x, y, w, h))

    total = sum(len(e[1]) for e in entries)
    if total != 67:
        die(f"emitted {total} bounds, expected 67")

    body = []
    titles = {1: "Row 1", 2: "Row 2", 3: "Row 3", 4: "Row 4", 5: "Row 5"}
    for row_no, items in entries:
        body.append(f"\t\t\t// ── {titles[row_no]} ──")
        for slot, x, y, w, h in items:
            body.append(f"\t\t\t[{slot}] = new({x:.3f}, {y:.3f}, {w:.3f}, {h:.3f}),")

    open(OUT, "w", encoding="utf-8", newline="\r\n").write('''using System.Collections.Generic;
using System.Linq;

namespace ProoveGaming.App.Models.Keyboards;

/// <summary>
/// Keycap bounds for the <b>T800</b> artwork, in that artwork's viewBox coordinates, keyed by
/// <b>matrix slot</b>.
/// </summary>
/// <remarks>
/// <para>
/// Measured from the artwork itself rather than placed on an idealised grid, so key overlays can be
/// drawn directly on the rendered board and stay registered to the real keycaps at any scale — the
/// same approach as <see cref="CyrexArtworkLayout"/> and <see cref="TRacerArtworkLayout"/>.
/// </para>
/// <para>
/// Derived by connected-component labelling of the alpha channel of the keycap layer in
/// <c>Assets/Keyboards/PG_T801_keys.svg</c>, then mapped into viewBox space by that image's own
/// placement transform <c>translate(216.208 687.954)</c> <b>less the artwork's crop origin</b>
/// (172, 672). The pass finds 68 keycaps in rows of 15/15/14/14/10, which matches the firmware's
/// factory key table exactly — that agreement between two independent sources is what verifies the
/// slot assignment. FN is excluded: it is not remappable and gets no overlay.
/// </para>
/// <para>
/// Slots are joined by walking each measured row left to right against the factory table. The
/// matrix is column-major, so within a visual row left-to-right is ascending slot number, which is
/// why the join is a plain zip rather than a nearest-centroid match.
/// </para>
/// <para>
/// These numbers are tied to that specific artwork revision. Replacing the keys SVG invalidates
/// every entry here, and nothing detects that automatically — regenerate with
/// <c>docs/plans/add-t800-key-assignments/measure-t800-keycaps.py</c> rather than hand-editing.
/// </para>
/// </remarks>
public static class T800ArtworkLayout
{
	/// <summary>Artwork viewBox width — the bounds below are in these units.</summary>
	public const double ViewBoxWidth = ''' + f"{VIEWBOX[2]}" + ''';

	/// <summary>Artwork viewBox height.</summary>
	public const double ViewBoxHeight = ''' + f"{VIEWBOX[3]}" + ''';

	/// <summary>Slot → measured keycap bounds. FN is absent.</summary>
	public static IReadOnlyDictionary<byte, RasterKeyBounds> BySlot { get; } =
		new Dictionary<byte, RasterKeyBounds>
		{
''' + "\n".join(body) + '''
		};

	/// <summary>
	/// Joins this table against a catalog by matrix slot, the same way
	/// <see cref="CyrexArtworkLayout.LayoutFor"/> does.
	/// </summary>
	public static IReadOnlyDictionary<string, RasterKeyBounds> LayoutFor(IReadOnlyList<PhysicalKey> catalog) =>
		catalog
			.Where(k => BySlot.ContainsKey(k.MatrixIndex))
			.ToDictionary(k => k.DevKeyName, k => BySlot[k.MatrixIndex]);
}
''')
    print(f"wrote {OUT}: {total} keycap bounds in rows {counts}")


main()
