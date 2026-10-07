# BLAZE / X43 Gaming Mouse — Knowledge Base

This folder is the **single source of truth** for everything we've learned about the
BLAZE (a.k.a. X43) gaming mouse while building the ProoveGaming companion app.
It is written for the next engineer who has to touch this device and needs to know
*what actually works on the hardware* versus *what the spec/OEM claims*.

> **Keep this current.** When you discover something new about the device —
> a protocol quirk, a firmware limitation, a capture finding, an architecture
> decision — add it here (and to the changelog at the bottom). This is where
> future updates go.

## Contents

| Doc | What's in it |
|-----|--------------|
| [hardware.md](hardware.md) | Device identity (VID/PID), MCU/sensor, HID collections & endpoints, how Windows sees it, and the **code map** (where everything lives) |
| [protocol.md](protocol.md) | The wire protocol as **actually used on this unit**, validated against a USB capture — commands, param_0/param_1 blocks, report-rate encoding, checksum |
| [dpi-stage.md](dpi-stage.md) | Reading the active **DPI stage and report (polling) rate**: the `DpiStageMonitor` service, the `0x80` query, and the gRPC push flow to the UI |
| [key-matrix.md](key-matrix.md) | The 16-button key matrix — physical slot mapping, entry format, KEY6 report-rate binding, and the **safety/recovery** rules for writing it |
| [led.md](led.md) | LED/RGB status and full research history — what lights, what doesn't, the persistence/static-effect open problems, transport findings, and the "DPI change kills the LED" bug |
| [engineering-notes.md](engineering-notes.md) | Cross-cutting gotchas and hard rules (scope-safety, read-back limits, transport, key-matrix danger) — read before writing to the device |

## Related docs (repo root `docs/`)

These predate this folder and remain the long-form references. Do not duplicate
them here — link to them.

- [`docs/x43_protocol_904a4f8.md`](../../x43_protocol_904a4f8.md) — the original **Easen protocol spec v1.0** (2021). Full command tables, key-code table, macro format. The authoritative *spec*; note that not all of it matches our firmware (see [protocol.md](protocol.md)).
- [`docs/oem-vs750-protocol.md`](../../oem-vs750-protocol.md) — protocol **reverse-engineered from the OEM `VS750 Gaming Mouse.exe`** (their Windows driver for this device).
- [`docs/led-protocol-investigation.md`](../../led-protocol-investigation.md) / [`docs/manufacturer-inquiry-led.md`](../../manufacturer-inquiry-led.md) — the LED fault investigation.

## What the device will and won't tell us (read-back matrix)

| Value | Readable from device? | How / why not |
|-------|-----------------------|---------------|
| Active DPI stage | ✅ yes | `0x05` vendor report, offset 3 (via `0x80` query / button press) |
| Report (polling) rate | ✅ yes | same `0x05` report, offset 2 |
| LED **effect** | ⚠️ partial | `0x05` offset 4, but the protocol byte maps ambiguously to UI effects |
| LED **brightness / speed / colour** | ❌ no | not in the `0x05` report; VS750 stores them locally |
| Button **debounce** / **lift-off** | ❌ no | live in param_1 block 0 (64-byte block); the device exposes **no 64-byte input endpoint** — `0x82/0x83` reads return only a 9-byte ACK header, never the block data. Confirmed by interface enumeration (`inLen=8/9/7/3/2`, command collection `inLen=0`) and the "No readable interface found" log. These only change via software anyway, so the app can persist them locally instead (not currently implemented — left as-is per request 2026-07-10). |

## Quick facts (TL;DR)

- **VID `0x04D9` / PID `0xA245`.** MCU: Holtek **HT68FB571** (Easen `mcu_type 0x03`, 7 keys). Sensor type `0x03` (3327-class).
- The device speaks the classic **Easen param protocol**: command headers via **HID SET_REPORT (feature)**, 64-byte data blocks via **interrupt-OUT endpoint 0x03**. It uses **only** `0x02 SetParam0` / `0x03 SetParam1` — not the `0x0B/0x0C/0x0D/0x10` command family (those exist in the OEM exe but target other MCU families).
- **Report rate is a raw millisecond value** `1/2/4/8` (= 1000/500/250/125 Hz), *not* an index `0–3`. We had this wrong; it's fixed.
- The active DPI stage **and report (polling) rate** are delivered together in a `0x05` **notification** on vendor collection `MI_01 Col04` (`0xFF03/0x00F0`) — rate at byte offset 2, DPI stage at offset 3. It fires on change (physical DPI / polling button). HID feature reads return all zeros, but **sending command `0x80` makes the device broadcast its current state** as that same `0x05` report — so both values *are* readable on startup by poking it. See [dpi-stage.md](dpi-stage.md).
- The LED on our test unit is effectively dead; even the OEM's own commands don't light it. Believed to be a **hardware/flash fault**, not software. See [led.md](led.md).

## Changelog

- **2026-07-10** — Folder created. Consolidated USB-capture ground truth (`C:\captures\cap-10.07.2026.pcapng`), the `DpiStageMonitor` + gRPC DPI-push architecture, and the report-rate / key-matrix fixes.
- **2026-07-10 (later)** — **Current DPI stage IS readable on startup.** A cold-start capture (`cap-10.07.2026-with-startup.pcapng`) showed the OEM app sends command `0x80` and the device answers with a `0x05` state notification (dpi_stage at offset 3). Implemented: `IMouseRgbService.RequestCurrentStateAsync()` (sends `0x80`); `DpiStageMonitor` queries on reader-open and via `RequestStateRefresh()`; `DeviceService` refreshes on client subscribe. `200` downgraded from "the answer" to "graceful fallback." Also added App-side caching (`TryGetLastDpiStage`) so a device opened *after* the push still shows the right stage.
- **2026-07-10 (later still)** — **Report (polling) rate tracked the same way.** The `0x05` report carries the rate at byte offset 2 (raw-ms encoded), so the `0x80` query and Col04 reader already deliver it. Added `ReportRateChange` gRPC message, `DpiStageMonitor.ReportRateChanged` (independent dedup on `LastRateHz`), App `ReportRateChanged` event + `TryGetLastReportRate` cache, and a **write-free** `HighlightPollingRate` (the polling-rate UI writes to the device, so the hardware-driven highlight must not echo back).
- **2026-07-10 (research consolidation)** — Moved the standalone research notes into this shared knowledge base so the whole team has them: added [key-matrix.md](key-matrix.md) and [engineering-notes.md](engineering-notes.md), expanded [led.md](led.md) with the full RGB research history (persistence, static-effect, transport findings, the one-time light-up) and [hardware.md](hardware.md) with the code map. Established the read-back matrix. Settings panel trimmed to Button Debounce only; debounce/lift-off read-back confirmed infeasible (left as-is).
