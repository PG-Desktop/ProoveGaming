# Rampage — vendor channel over Bluetooth LE (probe findings)

Companion to [`hardware-findings.md`](hardware-findings.md), for the Bluetooth transport. Filled
in from runs of the `rampage-bt` probe
(`dotnet run --project ./src/ProoveGaming.Device.Testing -- rampage-bt`) on 2026-08-13, with the
board connected over Bluetooth only and the DeviceHost stopped. Plan:
[`add-rampage-bluetooth-rgb`](../../plans/add-rampage-bluetooth-rgb/add-rampage-bluetooth-rgb-plan.md).

**Status: RUN — verdict: the vendor channel is advertised over BLE but NOT writable.** Windows
exposes the SmartInfinite vendor collection over HID-over-GATT, with the expected report shape,
yet every host→device write API fails before reaching the radio. RGB (and any other
configuration) over Bluetooth is impossible on this firmware; the App keeps the Bluetooth
details page read-only (`SupportsConfiguration: false` on the Bluetooth transport), the same
pattern as TAIPAN. This needs a vendor-side fix — see the request section at the end.

## Device identity over BLE

| Field | Value |
|-------|-------|
| VID over BLE | `0x28E9` (10473) — same GigaDevice VID as USB |
| PID over BLE | **`0x3000` (12288)** — differs from USB's `0x3258` (12888); recorded as the Bluetooth transport's `ProductId` in `appsettings.json` |
| Revision | `0x0109` |
| Vendor interface path | `\\?\hid#{00001812-0000-1000-8000-00805f9b34fb}_dev_vid&0228e9_pid&3000_rev&0109_cc482ebf5532&col06#…` |
| Path form | **Not** `BTHENUM`/`BTHLEDEVICE`: current Windows 11 builds the HOGP path from the HID service UUID `{00001812-…}`. `SmartInfiniteInterfaceSelector.IsBluetoothDevicePath` matches this form too. |

## Interface dump (stage 1)

Six HID collections enumerate for `28E9:3000`:

| Collection | out/in | What it is |
|------------|--------|------------|
| col01 | 2/9 | boot keyboard (`\kbd`) |
| col02 | 0/16 | input-only (`\kbd`) |
| col03 | 0/3 | input-only |
| col04 | 0/2 | input-only |
| col05 | 0/8 | input-only |
| **col06** | **64/64** | **the SmartInfinite vendor channel shape** |

## Vendor collection descriptor (col06)

```
usages: FF55:202          <- NOT the USB build's FF87:20
report Input   id=0x06 len=64
report Output  id=0x06 len=64
```

Two deviations from USB worth pinning:

- The vendor usage over BLE is **page `0xFF55`, usage `0x0202`** — the USB rule
  (page `0xFF87` / usage `0x20`) matches nothing over BLE. The 64/64 shape heuristic is what
  identifies the collection.
- The report framing is otherwise identical: report ID `0x06` doubling as the SmartInfinite
  magic byte, 64-byte reports.

## `0x14` read result (stage 2)

**FAILED — the read request cannot be delivered.** A `0x14` read is a write-then-read, and the
write half fails (below), so no reply was ever observable.

## Write result — three independent APIs, all fail

The same correctly-framed 64-byte report (`06 14 00 00 38 4c 00 00 …`, verified checksum) was
sent three ways at 3-second timeouts:

| API | Result |
|-----|--------|
| `WriteFile` via HidSharp (overlapped) | IO completes with an error ("Operation failed after some time" — not a timeout) |
| `HidD_SetOutputReport` (control path) | fails, Win32 error 6 (`ERROR_INVALID_HANDLE`) |
| `WriteFile`, raw synchronous handle | fails, Win32 error 87 (`ERROR_INVALID_PARAMETER`) |

The boot-keyboard input works over the same link (typing reaches Windows), so the connection and
its encryption are healthy. The consistent reading: the firmware's **report map advertises an
Output report the GATT database does not actually implement** — there is no writable Output
Report characteristic behind `id=0x06`, so every Windows write path dies in the BTHLE driver.

## Consequences (applied)

- `src/ProoveGaming.DeviceHost/appsettings.json`, Rampage Bluetooth transport:
  `ProductId: "12288"` (the true BLE identity) and `SupportsConfiguration: false` — the details
  page over Bluetooth is read-only, exactly like TAIPAN over its receiver/BT. Wired behaviour is
  untouched.
- `MergeTransports` was deliberately **not** enabled: with Bluetooth read-only nothing persists
  from a BT session, so there is no bucket to unify, and enabling it would strand existing wired
  settings (saved under the Container-ID key) for no benefit. Revisit if the vendor makes the
  channel writable.
- The App-side identity plumbing (catalog `vendor_id`/`product_id`, Bluetooth card stamping,
  factory Bluetooth fallback, non-Bluetooth-first interface ordering) stays in place and dormant;
  if a fixed firmware arrives, enabling BT RGB is a config flip plus a re-run of this probe.

## Documentation sweep (2026-08-13) — the vendor's own materials corroborate the probe

A full pass over every document in the repo (including the untranslated sheets of the original
`软件驱动协议-20251028-V3.1.xlsx` and the `smart-infinite-protocol-original.docx`) found **no
Bluetooth host protocol anywhere**, and three independent confirmations that wireless
configuration is 2.4G-receiver-only on this vendor's stack:

1. **Factory clarification C12**
   ([`../smart-infinite-protocol/factory-clarifications-2026-08.md`](../smart-infinite-protocol/factory-clarifications-2026-08.md)):
   when the factory enumerated interface/framing **per transport**, it listed *wired* (56-byte
   payloads) and *2.4G* (24-byte payloads) — **Bluetooth is absent from the factory's own
   transport list.**
2. **The V3.1 workbook's previously untranslated 外设说明 sheet** documents the "wireless driver"
   (V03) flow entirely through the **2.4G receiver** (magic `0xAA`, receiver stores the
   keyboard's VID/PID, state query `AA D0 00 00 18 00 00 00`) — now translated in
   [`../smart-infinite-protocol/wireless-receiver-protocol.md`](../smart-infinite-protocol/wireless-receiver-protocol.md).
   The workbook's only BLE mentions are *key functions* (`MODE_BLE1..3`,
   `SWITCH_BT_MODE 0x60`/`SWITCH_BT_1..3` in `keyboard_key_define.h`) — keys the USER presses to
   switch the keyboard between its three BT host channels; nothing host→device.
3. **The TAIPAN vendor Q&A**
   ([`../../mouse/taipan/taipan-vendor-qa-2026-07-30.md`](../../mouse/taipan/taipan-vendor-qa-2026-07-30.md),
   items T2 and T9) got flat answers for the mouse family: 只支持有线和2.4G 不支持蓝牙 ("only
   wired and 2.4G are supported; Bluetooth is not supported") and 程序现在不支持蓝牙 ("the
   software currently does not support Bluetooth at all").

Device Info byte 9 = 3 (USB+**BLE**+2.4G) therefore means BLE as a *typing* transport only. The
BLE report map advertising an Output report the GATT database never implements matches that
reading exactly.

**Practical wireless option that IS documented:** the board supports a 2.4G receiver, and the
receiver protocol is specified (identity handshake + 24-byte chunking). If Rampage units ship
with (or can be sold with) a 2.4G dongle, wireless RGB/config is implementable along the
documented path — that, not BLE, is the productive direction.

## What to ask the vendor (for the next request, following [`vendor-request.md`](vendor-request.md))

1. Confirm what your materials imply: Smart Infinite configuration over **Bluetooth is
   unsupported by design** (C12 lists only wired + 2.4G; over BLE the PID-`0x3000` col06
   collection advertises Output id `0x06`/usage `FF55:0202` but every Windows write API fails).
   If it IS meant to work, name the GATT service/characteristic and timing constraints.
2. Does the Rampage (RM1) retail package include a 2.4G receiver, and does its receiver implement
   the documented `0xAA` protocol (state query `AA D0 00 00 18 00 00 00`) plus 24-byte-payload
   table chunking? If yes, we would implement wireless configuration through the receiver.
3. If neither path exists, confirm so the driver can permanently state read-only over Bluetooth
   in the UI.
