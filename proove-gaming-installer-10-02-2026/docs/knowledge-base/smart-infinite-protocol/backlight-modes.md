# Smart Infinite — backlight `light_mode` values (from Keyborder)

Extracted 2026-08-14 from **Keyborder-v3** (`C:\Program Files\PixelPrime\Keyborder-v3.exe`,
`resources/app.asar` → `out/renderer/assets/index-C1SFvu4g.js`, the same readable bundle the
[magnetic-axis doc](magnetic-axis-travel.md) came from). The vendor app's
`standardBackLightEffect` array is the authoritative mode-index table for the
[[Smart Infinite protocol]] family — the V3.1 workbook never enumerates the modes (it only says
`light_mode` is "an index, default: wave" and that the mode list is device-reported), so until
now our services' constants (`ModeWave = 0` … `ModeSteadyOn = 10`) had no written source.

## The table (`standardBackLightEffect`)

`value` is what goes into the `0x15` feature area's `light_mode` byte. `effect` lists which
sliders/switches the vendor UI exposes for that mode (`light_RGB` = the mono/multicolour
switch); a field the UI does not expose is simply left at its previous block value.

| value | Label (zh) | Label (en, vendor i18n) | Type | Vendor UI exposes |
|---|---|---|---|---|
| 0 | 波浪 | Wave Flow | dynamic | level, speed, trend, RGB |
| 1 | 彩云 | Cloud Scroll | dynamic | level, speed, trend, RGB |
| 2 | 霓虹 | Neon Glow | dynamic | level, speed, trend |
| 3 | 回旋 | Vortex Spin | dynamic | level, speed, trend, RGB, single_multi_light |
| 4 | 百花 | Blossom Bloom | dynamic | level, speed, single_multi_light |
| 5 | 飞翔 | Light Drift | dynamic | level, speed, RGB |
| 6 | 穿越 | Key Stream | dynamic | level, speed, RGB, single_multi_light |
| 7 | 下雨 | Gentle Rain | dynamic | level, speed, RGB, single_multi_light |
| 8 | 光谱 | Spectrum Overflow | dynamic | level, speed |
| 9 | 呼吸 | Breathing Light | dynamic | level, speed, RGB |
| 10 | 常亮 | Static | dynamic | level, speed, RGB |
| 11 | 涟漪 | Ripple | interactive | level, speed, RGB |
| 12 | 激光 | Laser | interactive | level, speed, RGB |
| 13 | 单点 | Reaction | interactive | level, speed, RGB |
| 14 | 扩散 | Speed/Spread | interactive | level, trend |
| 15 | 扫描 | Light Scan | interactive | level, RGB |
| 16 | 渐变 | Color Shift | interactive | level, changeColor |
| 17 | 自定义 | Custom | custom | level |
| 18 | 关闭 | Off | custom | — |

(A commented-out `music` entry with value 20 exists in the source but is not shipped.)

## What this settles

- The constants in `RampageKeyboardRgbService` / `CyrexKeyboardRgbService` /
  `AressKeyboardRgbService` (`Wave 0`, `Neon 2`, `Spectrum 8`, `Breathing 9`, `SteadyOn 10`)
  match the vendor app **exactly** — the mode bytes our App sends for Неон and Зміна кольору
  are the same ones Keyborder sends for 霓虹 and 光谱. Any perceived mismatch between our app
  and the board is therefore in our **preview animation or effect naming**, not in the byte
  written to the device.
- Modes 11–16 are the interactive (key-reactive) family and 17 is per-key custom — none are
  surfaced by our App today.
- The workbook's per-device caveat still stands: the *count* of modes a given board actually
  implements is device-reported (Device Info), so a smaller/older firmware may implement a
  prefix of this table rather than all of it.
