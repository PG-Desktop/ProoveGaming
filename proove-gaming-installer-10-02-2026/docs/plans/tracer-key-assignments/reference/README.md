# Reference materials

Vendor / client-supplied artifacts backing the T-Racer key-assignments plan.

## Files

| File | Source | Purpose |
|------|--------|---------|
| [`key-layout-reference.xlsx`](key-layout-reference.xlsx) | Client (2026-07-20) | `Proove Gaming — Key Layout Reference` spreadsheet. Sheet 1 (Overview) tabulates which physical keys each model exposes (ARESS, CYREX, RAMPAGE, RASTER, T-801, T-RACER); Sheet 2 (Key Lists) is the per-model flat list ready for software import; Sheet 3 (Key Count Summary) confirms form factor and total keys per model. **T-Racer = 82 keys, 75% form factor, Wireless Gaming**, differing from Raster only by the presence of `PrintScreen` |

## What's NOT here

- **Photo of T-Racer** confirming the physical position of `PrintScreen`.
  Needed before the visual overlay in `TRacerVisualLayout` can be finalised —
  the placeholder position must be verified against a real device shot.
- **Vendor firmware / dist bundle for T-Racer specifically.** T-Racer shares
  the Raster vendor family (see `../../tracer-rgb/tracer-rgb-plan.md`); the
  protocol reference in `../../raster-key-assignments/protocol-reference.md`
  covers both.
