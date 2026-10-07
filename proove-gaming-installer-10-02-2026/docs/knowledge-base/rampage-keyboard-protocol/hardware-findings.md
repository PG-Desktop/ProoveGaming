# Rampage — findings from driving real hardware

Durable results of the key-mapping investigation carried out in PR #45
(`features/PG-331-keyassignments`), ported into the knowledge base so they survive whether or not
that PR merges. Original write-ups: `docs/rampage-key-mapping-investigation.md` and
`docs/rampage-key-mapping-result.md` on that branch.

Everything here was observed against a physical device. Where it contradicts
[`README.md`](README.md) or the vendor screenshots, **this file wins**.

## Factory update of 2026-08-04 (RM1) — the write shape everything below was missing

The factory's answer to [`vendor-request.md`](vendor-request.md):

- commands `0x10` and `0x11` must **not** be used (for the key write or any other command);
- only command `0x18` needs to be sent;
- the total transfer length must be exactly **384 bytes**.

That is 7 packets — 6 × 56 plus a final 48-byte packet at offset `0x150`. No shape tried below
ever actually sent that: the "392-byte" sweep variant had a bounds guard that silently dropped its
7th packet, so it really sent 336 too. The implementation
(`RampageProtocol.EnumerateWriteChunks`, `RampageKeyboardKeyMappingService`) now sends the bare
384-byte `0x18` transfer with no session bracket.

**Hardware re-test of 2026-08-04: CONFIRMED WORKING.** On the cabled board (`mi_01&col05`), via
the production code path (`RampageKeyboardKeyMappingService`):

- reads work without the `0x10`/`0x11` bracket — 96 slots, FN canary `{0x60, 0x01, 0x00}` at
  slot 90, correct alignment;
- the 384-byte bare `0x18` write of slot 33 Q → W was acked `0x55` on all 7 packets (the final
  one `06 18 50 01 30 99 00` — addr `0x150`, len `0x30`), **and the read-back returned the new
  value for the first time ever**;
- the **physical key emitted `w`** immediately — no replug, no commit command, no session bracket;
- restoring Q the same way also verified and took effect immediately.

The whole "missing commit step" hypothesis is dead: the firmware stores the table iff the
transfer totals exactly 384 bytes. Everything below this section describes the pre-update state
and is kept for the record.

## Device identification

| Field | Value |
|-------|-------|
| Product | Wireless Keyboard Rampage (TKL, 96 matrix slots / 86 remappable keys) |
| USB VID | `0x28E9` (10473) |
| USB PID | `0x3258` (12888) |
| Protocol | Smart Infinite v1.4 (2025-11-14) |
| Vendor interface | `mi_01&col05`, `outLen=64`, `inLen=64`, report ID `0x06` |

## Two vendor interfaces, only one writable

Rampage exposes **two** interfaces with `outLen=64 / inLen=64`:

- `mi_01&col05` — writable. RGB, battery and key config all work here.
- `mi_01&col07` — input-only. Every host→device `Write()` returns `ERROR_INVALID_FUNCTION`.

Select by sorting `DevicePath` and taking the lowest `col##`. An unsorted `FirstOrDefault` was
found to be unstable across replugs. `DeviceKeyMappingServiceFactory` does this; note that
`DeviceRgbServiceFactory` still uses an unsorted `FirstOrDefault` and is latently exposed to the
same flip.

## Reply lag — never drain the Start ack

Every command generates a reply on the input pipe, **including `Start Communication (0x10)`**. If a
caller sends N read requests and calls `Read()` N times, each read returns the reply to the
*previous* request.

The tempting fix — read once up front to drain Start's ack — is actively harmful: calling `Read()`
with a timeout before the first read request leaves the HidSharp Windows stream in a state where
every subsequent `Write()` fails with `ERROR_INVALID_FUNCTION`. **Do not drain.** Accept the lag and
account for it, either by correcting indices or by pipelining and sending one extra request at the
end to flush the final reply.

## `ERROR_INVALID_FUNCTION` on the first write

Windows can hold the previous HID handle in a "closing" state for tens of milliseconds after the
prior stream is disposed. `TryOpen` returns a fresh stream, but its first `Write()` fails with
Win32 error 1. Retry the whole open+exchange up to 4 times with a ~100 ms gap.

## The staging-buffer split (why PR #45 stalled)

PR #45 wrote **3 bytes** at `addr = slot * 3` with `Set Key Info (0x18)`. Observed behaviour:

- The firmware acked with status `0x55`.
- `Read Key Info (0x17)` immediately after returned the new value, and progressed correctly across
  successive writes.
- The **physical key kept emitting its factory scancode** — verified both by HID input capture on
  `mi_00` and independently in a text editor.
- A `Set General Info (0x15)` write (i.e. any RGB change) **zeroed the pending value back to
  `0x00`**.
- Unplugging and replugging the USB cable made the new mapping take effect.

Every documented command that could plausibly trigger a scan-matrix reload was tried and none
worked: `Sync Lighting (0x1D)` at 10 ms and 250 ms, `Set Profile (0x31)` same-profile and
round-trip at 30 ms and 500 ms, `Set General Info (0x15)`, and waiting 30 seconds.
`Reset (0x20)` turned out to be a full factory reset.

The conclusion drawn at the time was that an undocumented "commit staging → compiled profile"
command must exist. **The vendor drop in PR #67 suggests a simpler explanation:** it states twice
that the keyboard only *stores* a table when the full declared length (384 bytes) is transferred.
A 3-byte write is exactly the partial write that warning describes. The current implementation
therefore writes the whole table; if the physical key still fails to update, the vendor request in
PR #45 needs re-opening.

## 2026-07-28: the write does not persist at all — corrects the section above

A second, independent implementation reached the same wall with a **verified** read path, and in
doing so overturned part of the diagnosis above.

Confirmed correct this time: interface selection by usage `0x20`/page `0xFF87`; the additive
checksum (validated byte-for-byte against the vendor captures); 6 × 56-byte table framing; and the
read, cross-checked by the FN canary at slot 90 (`{0x60, 0x01, 0x00}`) and by all 96 slots decoding
to valid records.

With that in place, `0x18` behaves as follows:

- Every packet is acknowledged `0x55`.
- `0x17` read-back: **unchanged**.
- `0x16` read-back: **unchanged, and byte-identical to `0x17` in every test.**
- Physical key: **unchanged**.
- **After unplug + replug: still unchanged.**

Write shapes tried, all acknowledged, none persisted: 336 B (6 × 56), 392 B (7 × 56), 6 × 54 B
(3-byte aligned), a single 3-byte packet, each of those followed by `0x31 SetProfile` with the same
profile, read and write in one session and in separate sessions, and inter-packet delays from 3 ms
to 500 ms.

**Two claims in the section above should therefore be treated as unreliable**, because the build
that produced them had the one-packet read misalignment described earlier:

1. *"`Read Key Info (0x17)` immediately after the write returns the new value."* Not reproducible.
   Both read commands return the stored table unchanged.
2. *"Unplug + replug applies the new mapping."* Not reproducible. The data is not reaching flash.

The staging-buffer model was inferred from those two observations, so it no longer has support.
The simpler reading of the evidence: `0x18` is accepted and discarded, and the command that commits
it is not present in any material supplied so far. See [`vendor-request.md`](vendor-request.md).

## 2026-07-29: the device reports `key count = 0`, and only table writes fail

Dumping the two blocks nobody had inspected settled the question.

**Device Info (`0x12`)** — `AA BB 58 32 80 08 00 00 00 03 00 01 04 04 04 02 …`

Every field decodes correctly against the vendor's table: validity marker `AA BB`, product ID
`0x3258`, key-matrix size `0x80` × 3 = 384 B, macro space `0x08` × 256 = 2048 B, device type 3
(USB+BLE+2.4G). Except index 6 — **actual key count — which reads `0x00`**. The device declares it
has no remappable keys, while `0x17` returns a full, valid 96-key table.

**Function/config (`0x14`)** — `00 0A 3B 03 00 00 76 20 EE 07 …`

This is byte-for-byte the RGB state we had just written with `0x15` (`mode=10, level=59, speed=3,
R=118, G=32, B=238, colour index 7`).

**Conclusion: writes on this interface work.** `0x15 Set General Info` persists and reads back
correctly, which independently proves the transport, additive checksum, framing, session handling
and interface selection are all correct — the last remaining doubts about our own implementation.

What does **not** persist is table writes: `0x18` (key matrix) and `0x19` (per-key lighting) are
both acknowledged `0x55` on every packet and both discarded. The lighting-table control was written
`FF FF FF` and read back `00 00 00`.

Taken with `key count = 0`, the straightforward reading is that the key-matrix and per-key-lighting
features are **not enabled in this firmware build**. The 96-record table we can read is the
compile-time default from `key_matrix_tab`; the runtime feature behind it appears to be off.

This is now the headline of [`vendor-request.md`](vendor-request.md).

## Commands that must never be sent

- **`0x20` (Reset)** — full factory reset. Wipes user config, leaves the board in rainbow/pairing
  mode requiring vendor recovery.
- **`0x31` round-trip (`0 → 1 → 0`)** — freezes the firmware until a physical replug.

## Bluetooth

`BluetoothDeviceResponse` in `deviceservice.proto` does not carry VID/PID, so a Bluetooth-connected
Rampage arrives on the App side with VID = PID = 0. Device identity must fall back to a name match
(`RampageKeyCatalog.IsRampage(string)`). Firmware writes still require the wired vendor interface —
Bluetooth is display-only for remapping.
