# Smart Infinite — wireless (2.4G receiver) driver protocol

Translated from the previously un-transcribed sheet **外设说明** ("Peripheral notes") of
`docs/软件驱动协议-20251028-V3.1.xlsx` (the sheet also covers LOGO/side-light music-rhythm,
matrix-screen and LCD peripherals, which are not reproduced here). The workbook's title row dates
this capability to protocol **V03 — "增加无线驱动" (wireless driver added)**.

> **Why this page exists.** This is the vendor's ONLY documented wireless configuration path for
> the Smart Infinite family, and it goes through the **2.4G receiver** — there is no Bluetooth
> host protocol anywhere in the workbook. See
> [`../rampage-keyboard-protocol/bluetooth-findings.md`](../rampage-keyboard-protocol/bluetooth-findings.md)
> for the full evidence that Bluetooth configuration does not exist on this family.

## Receiver channel basics

- Communication **with the receiver itself** uses magic byte **`0xAA`**, not the keyboard's
  `0x06` — the receiver is addressed as its own device ("this command talks to the receiver and
  must not be handled with the keyboard's logic").
- Per the factory clarification C12
  ([`factory-clarifications-2026-08.md`](factory-clarifications-2026-08.md)): the 2.4G transport
  presents the same vendor interface (UsagePage `0xFF87`, Usage `0x20`) but chunks table
  transfers as 64-byte packets with **24 payload bytes** (wired uses 56) — no code in this repo
  implements that chunking yet.

## Pairing/identity flow (driver ↔ receiver)

1. Driver sends **`0xAA 0x10`** (start). Through the reply, the keyboard's **VID/PID is reported
   to the receiver**, which stores it.
2. Driver sends the **2.4G state query**: `AA D0 00 00 18 00 00 00`.
3. The receiver replies with its own state plus the stored keyboard identity. Reply layout:

| Byte | Meaning |
|------|---------|
| 9 | Number of bonded 2.4G devices (keyboards: always 1) |
| 10 | Bonded device USB VID, low byte (example in sheet: `B0`) |
| 11 | Bonded device USB VID, high byte (example: `36`) |
| 12–13 | Bonded device USB PID (low/high; "per customer") |
| 14 | Connection state of bonded device 1 — see enum below |

```c
typedef enum {
    APP_2G4_STATE_INVALID,        // default
    APP_2G4_STATE_IDLE,           // idle
    APP_2G4_STATE_RETURN,         // reconnecting
    APP_2G4_STATE_BONDING,        // pairing
    APP_2G4_STATE_COMMNUICATING,  // connected (sic — vendor spelling)
    APP_2G4_STATE_COM_SLEEP,      // sleeping
    APP_2G4_STATE_MAX
} app_2g4_state_e;
```

4. "The driver only needs to send the receiver-state query; data returned by other commands can
   be ignored — this command's data is authoritative."

## Operational notes from the sheet

- The state query does **not** require the `0xAA 0x10` start command first. Recommended
  sequence: on connecting to an authorized receiver, query state first; if the receiver is not in
  a normal communicating state, surface a prompt and stop; if it is connected, send `0xAA 0x10`
  and proceed with the flow above.
- When the user switches the keyboard from 2.4G to USB, the 2.4G link drops but the receiver
  stays enumerated: it still receives data but stops replying. After several unanswered frames or
  a timeout, the driver should re-query the receiver state (expect "not connected") and then use
  the stored VID/PID to authorize the device over USB.

## Confirmed on hardware — Rampage receiver, 2026-08-13

Probed with the `rampage-24g` arm of `ProoveGaming.Device.Testing` against a real Rampage on its
2.4G receiver (keyboard switched to 2.4G, cable unplugged):

- The receiver enumerates as **VID `0x056D` (1389) / PID `0xC077` (49271)** — its own identity,
  NOT the keyboard's `0x28E9` USB VID — with the exact wired interface layout: `mi_00` boot
  keyboard, `mi_01` col01–col07, **col05 = usage `FF87:20`, 64/64 — the writable vendor
  channel**, col06 = the `FF88:21` companion (input-only), col07 = 64/64 input-only.
- **The keyboard's `0x06` command channel passes through the receiver unchanged.** `0x14` reads
  work with the standard wired 56-byte chunking, and the RGB write (`0x15`) persists with BOTH
  framings: the production `RampageKeyboardRgbService` sequence (56+8, bracketed) and a bare
  24-byte-chunk transfer. The C12 24-byte chunking is therefore NOT required for the 64-byte
  feature area — it presumably matters for the large table transfers (384 B key matrix, macros),
  which remain untested over 2.4G.
  - **2026-08-14 — the conclusion above does not match live QA.** On a real Rampage receiver a
    static white sent through the production 56+8 sequence *rendered cyan* (colour fields shifted
    by one: R ← 0x00), and after `RampageKeyboardRgbService` switched the `0x15` write to
    24-byte chunks (24+24+16, still bracketed with `0x10`/`0x1D`/`0x11`) the picked colours
    rendered correctly. This contradicts the read-back result above; whether the discrepancy is
    receiver-firmware state, persisted-vs-live-render divergence, or something else is
    **unresolved** — the definitive check remains a `rampage-24g` probe run with a white static
    write and a `0x14` read-back on the same unit that showed cyan.
- **Key matrix over 2.4G (probed 2026-08-13, `rampage-24g keys` arm):** C12's 24-byte chunking is
  **required** for table transfers, in both directions:
  - `0x17`/`0x16` reads with the wired 56-byte shape return a **misaligned** table (42/96 valid
    TYPE bytes, FN canary broken) — the corruption is silent apart from the plausibility gate;
    12 × 24-byte packets read perfectly (96/96, FN slot 90 = `60-01-00`).
  - A `0x18` write via the production 56+48 shape is refused by the service's own guards (the
    read-before-write comes back implausible); the same write as **16 × 24 = 384 bytes** persists,
    read-back-verified, and was exercised end-to-end through the production
    `RampageKeyboardKeyMappingService` with `maxPayload = 24` — the exact path the DeviceHost now
    runs for a `Dongle2_4G`-matched row.
  - The macro region answers over 2.4G (`0x1B` head packet returns the `AA 55` marker; region
    2048 B, 20 slots); macro **writes** are QA-exercised via the App, not probed.
- The `0xAA` receiver-channel frame is **rejected by the Windows HID stack** on col05
  ("The parameter is incorrect"): the collection declares report ID `0x06` only, so a report
  starting `0xAA` is an undeclared report ID. Where the receiver channel actually lives (another
  collection, or `0xAA` placed after the `0x06` report ID) is unresolved — and unnecessary for
  RGB, which needs no receiver handshake.
- Applied to the catalog: Rampage gained a `Dongle2_4G` transport
  (`VendorId "1389"`, `ProductId "49271"`) in `src/ProoveGaming.DeviceHost/appsettings.json`, so
  the App treats the receiver like any dongle device and the existing RGB path works end-to-end.
  Note the receiver's foreign VID blocks `MergeTransports` (the registry requires one VendorId
  per merged product), so wired and 2.4G sessions keep separate settings buckets for now.

## Relevance to this repo

- Rampage's Device Info declares device type 3 (USB+BLE+2.4G), so the board supports a 2.4G
  receiver even though the current catalog only lists Wired and Bluetooth transports for it.
  If a Rampage receiver ships to users, wireless RGB/config would be implementable through THIS
  protocol (receiver handshake + 24-byte chunking) — unlike Bluetooth, which the vendor stack
  does not support at all.
- The TAIPAN mouse family got the equivalent statement directly from the vendor
  ([`../../mouse/taipan/taipan-vendor-qa-2026-07-30.md`](../../mouse/taipan/taipan-vendor-qa-2026-07-30.md),
  T2/T9): "only wired and 2.4G are supported; Bluetooth is not supported", "the software currently
  does not support Bluetooth at all".
