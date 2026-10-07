# Reference materials

Vendor-supplied artifacts and extracted webapp chunks used during protocol
reverse-engineering. Kept here for future reference so anyone continuing the
work has the raw inputs.

## Files

| File | Source | Purpose |
|------|--------|---------|
| [`shortcut-card.pdf`](shortcut-card.pdf) | Client / vendor (2026-05-20) | User-facing FN shortcut card. Confirms physical layout: 81-key TKL with `DEL / PGUP / PGDN / END` in right column, `FN` between `ALT (R)` and `←` |
| [`macro-definition-table.xlsx`](macro-definition-table.xlsx) | Client / vendor | Macro storage format specification. Not used for key remap but included for completeness |
| [`vendor-webapp-protocol-chunk.js`](vendor-webapp-protocol-chunk.js) | Extracted from `Raster keyboard dist (2).zip` → `_next/static/chunks/app/page-ba6d4e19076b59c9.js` | **The main source of protocol knowledge.** Contains the compiled `KeyboardDevice` class with command constants (`CMD_START_COMM`, `CMD_SET_USERKEY_*`, etc.) and the `setKeyMatrixData` implementation |
| [`qk75-layout-chunk.js`](qk75-layout-chunk.js) | Extracted from vendor webapp | QK75 Pro V2 (`0x304C`) layout JSON, used as an initial guess for Raster's matrix indexing. **Turned out to be inaccurate** — Raster's actual matrix differs. See `investigation-log.md` |

## What's NOT here

- **The full extracted webapp** (~1 MB of minified JS across 47 chunks) — kept
  out of the repo to avoid bloat. Recover from `Raster keyboard dist (2).zip`
  if needed.
- **`36b0_3061_0.bin` firmware binary** — vendor-supplied, in
  `dist (5).zip` → `fw-files/`. Not analyzed.
- **`PG Raster-CSA6D6-V0100-20260330.exe`** — firmware update executable from
  `FW (1) (5).zip`. Not tested; may or may not change protocol behavior.
- **Client's specific device serial-numbers, logs with device paths, etc.** —
  those are captured only in commit messages and PR comments as needed.

## Searching the vendor webapp chunk

Useful grep patterns for future protocol digs:

```bash
grep -oE 'CMD_[A-Z_0-9]+' vendor-webapp-protocol-chunk.js | sort -u
grep -oE 'setKeyMatrixData[^{]*\{[^}]{,200}' vendor-webapp-protocol-chunk.js
grep -oE 'REPORT_ID[^,]*' vendor-webapp-protocol-chunk.js
```

The chunk is minified but readable if piped through `js-beautify` or similar.
