# BLAZE / X43 — Hardware & HID Layout

## Identity

| Field | Value |
|-------|-------|
| Vendor ID | `0x04D9` (1241 — Holtek) |
| Product ID | `0xA245` (41541) |
| MCU | Holtek **HT68FB571** — Easen `mcu_type 0x03` ("571"), **7 assignable keys** |
| Sensor | `sensor_type 0x03` (3327-class) |
| Report length | 64 bytes |
| Encryption | none (`isEncode=false`) |
| Firmware family | Easen "hid553" command set (see [protocol.md](protocol.md)) |

The OEM device descriptor (from `apConfig.bin`) also lists `keyCount=5` — the UI
only exposes 5 assignable buttons even though the MCU supports 7 key slots.

## HID collections

The device exposes multiple HID collections across two interfaces. The ones we care about:

| Interface / Collection | Usage page / usage | Role |
|------------------------|--------------------|------|
| `MI_00` | standard mouse | Normal mouse input (cursor, buttons, wheel). |
| `MI_01 Col01` (primary) | `0xFF00 / 0xFF00` | **Command collection.** Config commands go here via HID SET_REPORT (feature). |
| `MI_01 Col04` (secondary) | `0xFF03 / 0x00F0` | **Notification collection.** Firmware pushes DPI/profile change events here as 7-byte reports. |

### The Col04 notification collection — important

- `HidSharp` does **not** enumerate `Col04`. You have to find it via **SetupDi**
  (`SetupDiGetClassDevs` with the HID GUID) and match the interface path substring
  `&mi_01&col04`. See `DpiStageMonitor.EnumerateCol04Paths()`.
- It is read with plain **`ReadFile`** (no message window / Raw Input needed).
- It is **single-owner**: whoever opens it for reading blocks others. If the OEM
  VS750 app is running and holding it, our `CreateFile` fails with a sharing
  violation (`err 32`). Open with `FILE_SHARE_READ | FILE_SHARE_WRITE` and be prepared
  to back off/retry.
- `ReadFile` is **denied (`err 5`)** on the mouse/keyboard collections — only the
  vendor collections (`Col01`/`Col04`) allow it.

Report shape on Col04 (7 bytes):

```
[0]=0x05  [1]=profile  [2]=reportRate  [3]=dpi_stage  [4]=ledEffect  [5]=0  [6]=0
                            ^ we read this   ^ and this
                            (report rate,    (dpi_stage,
                             raw ms 1/2/4/8)  0..7)
```

## USB endpoints (for the command path)

From the USB capture (`cap-10.07.2026.pcapng`):

| Direction | Transfer | Endpoint | Carries |
|-----------|----------|----------|---------|
| Host → device | **Control** SET_REPORT (`bmRequestType 0x21`) | EP0 | 8-byte command header `[cmd, profile, index, 0, 0, status, times, cs]` |
| Host → device | **Interrupt-OUT** | `0x03` | 64-byte parameter data block(s) |
| Device → host | **Interrupt-IN** | `0x81` | HID input + vendor notifications (7-byte `0x05…` and 23-byte reports) |

> The original Easen spec talks about "EP5 / EP6". On this unit the data blocks
> ride the interrupt-OUT endpoint `0x03`; the command *header* is a control
> SET_REPORT. See [protocol.md](protocol.md) for the exact framing.

## Where the code lives (code map)

The device work spans three processes: **Infrastructure** (the wire protocol),
**DeviceHost** (an always-on gRPC service that owns the hardware), and the **App**
(Avalonia UI, a gRPC client).

| Concern | File |
|---------|------|
| Wire protocol for this device (LED, DPI, debounce/lift, report rate, key matrix, state query) | `src/ProoveGaming.Infrastructure/Services/EasenMouseService.cs` (impl of `IMouseRgbService`) |
| Picks the right service per device + caches instances | `src/ProoveGaming.Infrastructure/Services/DeviceRgbServiceFactory.cs` |
| Keyboard equivalent (no-ops the mouse-only members) | `src/ProoveGaming.Infrastructure/Services/RampageKeyboardRgbService.cs` |
| DPI-stage / report-rate notification reader (Col04 `ReadFile`, multi-device, 0x80 poll) | `src/ProoveGaming.DeviceHost/Services/DpiStageMonitor.cs` |
| gRPC service — exposes settings RPCs + pushes state changes on the stream | `src/ProoveGaming.DeviceHost/Services/DeviceService.cs` |
| gRPC contract | `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto` |
| App gRPC client (routes stream messages to events, caches last DPI/rate) | `src/ProoveGaming.App/Services/UsbHidDeviceService.cs` |
| Device-details view model (highlights DPI stage / polling rate; RGB, sliders) | `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel*.cs` |
| Recovery / hardware-probe tools | `src/ProoveGaming.Device.Testing/` (esp. `X43Mouse.RestoreAll()`) |

DeviceHost runs continuously and can be installed as a Windows service
(`ProoveGamingDeviceHost`). It logs via Serilog to **`%TEMP%/Logs/logYYYYMMDD.txt`**;
useful grep tags: `[DPI]`, `[RATE]`, `[LED]`, `[MouseSettings]`, `[HID]`.

> Note: earlier notes placed the command code in `HidDeviceDiscoveryService.cs` — that
> was before the rewrite. Device commands now live in `EasenMouseService.cs`;
> `HidDeviceDiscoveryService` handles discovery/battery only.
