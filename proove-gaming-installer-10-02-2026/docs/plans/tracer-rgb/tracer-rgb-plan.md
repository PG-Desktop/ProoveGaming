# RGB Support Plan — T-Racer Keyboard

## Status

**Working.** T-Racer is the marketing rename of what internally is still `DragonFang` in
the config and session store. Wired USB RGB works via the same protocol as Raster, since
they're the same vendor family (VID 14000). All six app effects (Static, Breathing,
RainbowBreathing, Neon, ColorCycle, Wave, Off) route through the shared code path.

## Context

- Same vendor family as Raster (VID 14000). Wired PID `12370`, dongle PID `12290`, BT PID
  `12370`.
- Previously known as DragonFang (memory notes and prior config used that name). The user
  wants the display name "T-Racer" while keeping backward compatibility with saved session
  cards that were persisted under the DragonFang name.
- Same SmartInfinite-adjacent protocol on the `mi_01` raw-HID interface (report ID `0x00`,
  65-byte reports, `0xAA` magic). Full protocol reference lives in
  `../raster-rgb/raster-rgb-plan.md` — this doc doesn't re-derive it.

## Naming — internal vs display

Three separate labels serve three separate purposes and are deliberately different:

| Field | Value | Purpose |
|---|---|---|
| `Name` (config) | `DragonFang` | Persistence / matching key. Session cards saved before the rename are keyed by this; changing it would strand old cards. |
| `ProductName` (config, wired entry only) | `PG DragonFang` | Must match what the firmware's HID descriptor advertises — the daemon uses this to pick the specific (wired) entry over the generic dongle/BT entries. |
| `DeviceName` (config) | `T-Racer` | Display label. `FillConnectionCard` copies this into `card.Name` on every daemon update, so the UI shows "T-Racer". |

Factory dispatch matches on `Name == "DragonFang"` (case-insensitive) and routes to
`TRacerKeyboardRgbService`. The class file uses the T-Racer name for readability; the
config Name stays DragonFang for compat.

## Architectural decision

**Separate service file, near-copy of `RasterKeyboardRgbService`.** Consistent with the
"one file per keyboard" pattern the codebase already follows. Same wire protocol as Raster,
but isolating the service means any T-Racer-specific firmware quirk discovered later can be
tuned without touching Raster. If the two stay identical after real-world use, they can be
merged into a shared service at that point — trivially safe once known.

## Files

| File | State |
|---|---|
| `ProoveGaming.DeviceHost/appsettings.json` | Three DragonFang entries updated: `DeviceName: T-Racer` on all, `SupportsRgb: true` on the wired entry (PID 12370), `Name` and `ProductName` untouched for persistence/firmware compat. |
| `ProoveGaming.Infrastructure/Services/TRacerKeyboardRgbService.cs` | New — near-copy of `RasterKeyboardRgbService`. Same 0xAA magic, same read-modify-write flow, same mode index table, same LOGO/side mirror. Log tags say `T-Racer`. |
| `ProoveGaming.Infrastructure/Services/DeviceRgbServiceFactory.cs` | New branch: `name.Equals("DragonFang", ...)` → `CreateTRacerKeyboardRgbService`. |
| Rampage / Aress / Cyrex / Raster services | Untouched |

## Effect mapping

Identical to Raster — see `raster-rgb.md` for the mode index table (1-indexed
Streaming=1, Colorful=2, Breathing=3, Steady=4).

| App effect | lightMode | lightMixColor | lightSwitch |
|---|---|---|---|
| 0x04 Static | 4 (Steady) | 0 | 0 (ON) |
| 0x01 Breathing | 3 | 0 | 0 |
| 0x00 RainbowBreathing | 3 | 1 | 0 |
| 0x02 Neon | 2 (Colorful) | 1 | 0 |
| 0x03 ColorCycle | 1 (Streaming) | 1 | 0 |
| 0x05 Wave | 1 (Streaming) | 1 | 0 |
| 0xED Off | 4 | 0 | 1 (OFF) |

Note: `lightSwitch` is **inverted** on this vendor family — `0 = ON`, `1 = OFF`. Same as
Raster.

## Session-card gotcha discovered during setup

Renaming the config's `Name` field from `DragonFang` to `T-Racer` broke RGB detection on
existing installs, because:

- The app persists device cards by `record.Name` (set at add-time).
- `HandleUsbHidDeviceFoundAsync` matches cards to daemon reports via
  `string.Equals(card.Name, clientName, OrdinalIgnoreCase)` where `clientName` derives
  from `DeviceMap.Name`.
- Session-loaded cards kept `Name = "DragonFang"`, daemon reported `clientName = "T-RACER"`
  → no match → `SupportsRgb` never propagated to the existing card, `IsRgbSupported`
  stayed false, and the RGB nav button was hidden.

Fix: revert `Name` to `DragonFang` (matching key preserved). Display name driven by
`DeviceName: T-Racer` instead, which flows into `card.Name` on every daemon update via
`FillConnectionCard`.

**Lesson for future renames:** never touch `Name` on a device that already ships in the
config — change `DeviceName` instead, or provide a migration for saved sessions.

## Sources

T-Racer shares the vendor family with Raster, so the same source material carries the
protocol details — see `../raster-rgb/raster-rgb-plan.md` for the full breakdown of the vendor
dist bundle, the specific JS chunks that hold `setFuncInfo` / `getFuncInfo` / the mode-index
JSON tables, and the runtime observations that pinned down the wire format.

Sources specific to T-Racer / DragonFang:

- **`FW (1) (5).zip`** — contains `PG DragonFang-CSF0CB-V0102-20260328.exe`, the
  firmware-flash tool for this device. Confirms Raster and T-Racer are the same vendor
  family (both bundled together in one FW zip).
- **`memory/project_dragonfang_hid_protocol.md`** — prior investigation notes: two PIDs
  (12290 dongle, 12370 wired), wired battery works, dongle vendor channels do NOT carry
  vendor traffic. Directly informed the "wired-only, dongle can't do RGB" decision.
- **`memory/project_dragonfang_dongle_link_detection.md`** — prior blocker on dongle
  investigation. Confirms attempting dongle-side RGB without a USB capture would repeat
  the same wall we hit before.

No T-Racer-specific vendor documentation was supplied (no separate PDF shortcut card, no
per-device dist bundle). Implementation is inferred from the Raster protocol reference
plus the shared vendor family. If T-Racer's firmware turns out to diverge in any behavior
(different mode indices, additional/missing effects, quirks in the LOGO/side channels),
the per-effect mapping in `TRacerKeyboardRgbService.MapAppEffectToMode` is the place to
adjust — no protocol-level changes should be needed.

## Known / open items

- **Wired USB only.** Dongle (PID 12290) and Bluetooth (PID 12370, `HasBluetooth: true`)
  entries do not have `SupportsRgb: true`. Same limitation Raster hits: the vendor's
  dongle strips vendor HID channels, and BT rarely exposes vendor endpoints. See existing
  DragonFang memory notes (`project_dragonfang_hid_protocol.md`,
  `project_dragonfang_dongle_link_detection.md`).
- **Neon mode may not exactly match the app preview** — same caveat as Raster (currently
  mode 2 五彩缤纷 Colorful; could be tuned to mode 7 / 8 / 12 if user reports mismatch).
- **Phase drift on LOGO/side vs main during Breathing** — same firmware-level constraint
  as Raster. Alternative: pin LOGO/side to Steady, at the cost of losing the animated aux
  LED.
- **Diagnostic log kept** — `[LED] T-Racer read block[0..10] = ...` is INFO-level. Fine to
  drop to Debug once the service has stabilised on user hardware.
