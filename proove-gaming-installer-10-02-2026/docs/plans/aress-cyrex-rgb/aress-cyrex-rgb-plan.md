# RGB Support Plan — Aress & Cyrex Keyboards

## Context

Rampage already had working RGB via `RampageKeyboardRgbService` (SmartInfinite v1.4 protocol). Aress and Cyrex are magnetic wired keyboards from the same vendor speaking the same wire protocol, but with firmware quirks that differ from Rampage. Goal: extend RGB support to both without touching the working Rampage code path.

## Architectural decision

**One service per keyboard family.** Rejected the "shared SmartInfinite service" approach because per-firmware quirks (brightness scale, effect inversions) can't be safely folded into one class without risking Rampage regressions. Factory dispatches by ProductId; Rampage remains the default fallback for any other SmartInfinite keyboard.

## Work completed

### 1. Aress (VID 10473, PID 12910)

- **`appsettings.json`** — `SupportsRgb: true` on the Aress entry.
- **`AressKeyboardRgbService.cs`** — new file, same packet flow as Rampage.
- Firmware findings encoded in `ToKeyboardBrightness`:
  - Aress uses a **5-step (0..4) brightness scale**, not the doc's 0..99. Confirmed empirically after a 10-step experiment (`* 9` divisor) failed.
  - Any non-zero slider value clamps to at least level 1 (avoids the sub-12% "backlight off" boundary).
  - Off (`0xED`) forces 0.
- Rejected fixes (documented for handoff): Static-only inversion on 0..99 scale, per-effect quirks. Simple 0..4 mapping across all effects worked uniformly.

### 2. Cyrex (VID 10473, PID 12914)

- **`appsettings.json`** — `SupportsRgb: true` on the Cyrex entry.
- **`CyrexKeyboardRgbService.cs`** — new file, starts with the same 5-step brightness assumption as Aress (spec files are byte-identical, hardware Fn+↑/↓ shortcuts match). Ships untested against real Cyrex hardware — per-effect tuning can happen in this file if any behavior diverges.

### 3. Factory routing (`DeviceRgbServiceFactory.cs`)

- Dispatches by the config `Name` field (consistent for all three keyboards — no magic PID constants).
- Keyboard branch:
  - `"Aress"` → `AressKeyboardRgbService`
  - `"Cyrex"` → `CyrexKeyboardRgbService`
  - anything else → `RampageKeyboardRgbService`
- Each service has its own factory helper method that verifies the 64/64 report shape.

## What was explicitly NOT touched

- `RampageKeyboardRgbService.cs` — byte-for-byte identical to before this branch.
- The app-layer effect codes (`Breathing=0x01`, `Neon=0x02`, etc.) and its brightness → 0-255 mapping in `DeviceDetailsViewModel.Rgb.cs`.
- Any other keyboard config (Raster, DragonFang, T800).

## Verification status

- **Rampage**: unchanged code path, no regression risk.
- **Aress**: user-confirmed all six effects work (Static, Breathing, RainbowBreathing, Neon, ColorCycle, Wave, Off) with monotonic 5-step brightness.
- **Cyrex**: builds clean, wired up correctly. **Requires hardware verification** — behavior is inferred from Aress.

## Known / open items

- **Cyrex quirks unknown** until user tests each effect. If any effect misbehaves, patch only `CyrexKeyboardRgbService.cs`.
- **5-step slider UI mismatch** (both Aress and Cyrex): the app slider is continuous 0-100% but the hardware only has 5 discrete steps. Currently the mapping hides this. Optional follow-up: snap the Brightness slider to 5 ticks when the selected device is Aress or Cyrex, so the UI reflects real granularity.
- **`Maximum Brightness Level` from Device Information block** (SmartInfinite doc, byte 12) is not read — we hardcode 5 for Aress/Cyrex based on empirical testing. Reading it would let a single service self-configure for future keyboards, but adds a round-trip and isn't required today.

## File changes

| File | Type |
|---|---|
| `ProoveGaming.DeviceHost/appsettings.json` | modified — RGB enabled on Aress + Cyrex |
| `ProoveGaming.Infrastructure/Services/AressKeyboardRgbService.cs` | new |
| `ProoveGaming.Infrastructure/Services/CyrexKeyboardRgbService.cs` | new |
| `ProoveGaming.Infrastructure/Services/DeviceRgbServiceFactory.cs` | modified — PID-based dispatch |
| `ProoveGaming.Infrastructure/Services/RampageKeyboardRgbService.cs` | unchanged |
