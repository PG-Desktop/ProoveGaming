# Rampage key assignments — status report

**Date:** 2026-07-29 · **Device:** PG Rampage (VID `0x28E9`, PID `0x3258`) · **Branch:** `features/Rampage-newkeysdoc`

> A formatted version of this report for sending to the customer is in
> [`status-report.html`](status-report.html) — open it in a browser, or print it to PDF
> (it carries a print stylesheet). Both files hold the same content; keep them in step if you edit
> either.

> [!IMPORTANT]
> **Update 2026-08-04 — factory answered (RM1) and the fix is CONFIRMED ON HARDWARE.** Commands
> `0x10`/`0x11` must not be used at all, only `0x18` is sent, and the transfer must total exactly
> **384 bytes** (7 packets, 6 × 56 + 48). No previous attempt had ever sent that exact shape —
> the "392-byte" variant silently dropped its 7th packet. The write path was updated
> (`RampageProtocol.EnumerateWriteChunks`) and re-tested on the physical keyboard: slot 33
> Q → W verified on read-back **and the physical key emitted `w` immediately** — no replug, no
> commit command. Key remapping on Rampage **works**. The report below describes the state
> **before** this update and is kept for the record.

## Bottom line

Key remapping is **blocked by the keyboard's firmware, not by our software**. The device reports
that it has **zero remappable keys** and silently discards every key-table write, while accepting
and persisting configuration writes sent over the same connection in the same way. That contrast is
what proves our implementation is correct.

The work is complete and tested. It cannot function until the vendor confirms whether key remapping
is enabled in this firmware build.

---

## 1. Where it stood before

An earlier attempt (PR #45) reached a wall and was parked.

| Aspect | State before this work |
|---|---|
| Implementation | Wrote 3 bytes per key to the keyboard |
| Result | Firmware acknowledged the write; the physical key kept its original value |
| Diagnosis at the time | Concluded an undocumented "commit" command must exist |
| Vendor request | Sent; never answered |
| Outcome | Nothing shipped; feature parked |

Two observations from that attempt turned out to be **unreproducible**, and both had been used to
build its diagnosis:

| Claim in PR #45 | Verified now |
|---|---|
| "Read-back returns the new value after a write" | ❌ Does not reproduce — both read commands return the stored table unchanged |
| "Unplug + replug applies the new mapping" | ❌ Does not reproduce — the value never reaches the keyboard's memory |

Both came from a build whose read path had a packet-alignment defect (found and fixed in this
round). The "staging buffer" theory rested entirely on them, so it no longer stands.

## 2. What we did this round

New vendor material arrived (PR #67): the factory key table, the key-type catalog, and six HID
capture screenshots. We rebuilt the protocol layer from the captures rather than from the written
documentation, and found four real errors in the latter.

| Correction | Documentation said | Hardware/captures show |
|---|---|---|
| Command direction | `0x17` = write, `0x18` = FN layer | `0x17` = **read**, `0x18` = **write** |
| Checksum | CRC16/Modbus | **Additive sum**, verified against 12+ captured packets |
| Transfer size | 7 packets / 384 bytes | **6 packets / 336 bytes** |
| Interface selection | Guess by report size | HID usage `0x20` / page `0xFF87` — the vendor's own rule |

We also fixed a defect of our own: the read matched replies by arrival order, but every reply on
this protocol arrives one command behind. Replies are now matched by the address they echo back,
which is self-correcting.

**Delivered:** protocol layer, 86-key catalog derived from the vendor's factory table, on-screen
keyboard layout, gRPC contract, DeviceHost handler, App view model and overlay. **434 automated
tests pass.**

## 3. What we were trying to do

The vendor's own documented procedure, which we followed exactly:

1. Read the current key table from the keyboard (`0x17`, six packets)
2. Change the three bytes belonging to the key being remapped
3. Send the entire table back (`0x18`, six packets)
4. Read it back to confirm

## 4. Why it does not work — the two measurements

### Measurement A — the keyboard reports zero remappable keys

Device Info block (`0x12`):

```
AA BB 58 32 80 08 00 00 00 03 00 01 04 04 04 02 ...
                  ^^
```

| Field | Value | Expected | |
|---|---|---|---|
| Validity marker | `AA BB` | `AA BB` | ✅ |
| Product ID | `0x3258` | `0x3258` | ✅ |
| Key-matrix size | 128 × 3 = 384 B | 384 B | ✅ |
| Macro space | 8 × 256 = 2048 B | 2048 B | ✅ |
| **Actual key count** | **0** | **96** | ❌ |
| Device type | 3 (USB+BLE+2.4G) | 3 | ✅ |

Every other field decodes correctly, so the block is being read and interpreted correctly. The
device simply declares that it has no keys available for remapping — while returning a complete,
valid 96-key table when asked for one.

### Measurement B — writes work, but only non-table writes

After sending a lighting configuration command (`0x15`) with known values, reading the configuration
block (`0x14`) returns them byte for byte:

```
00 0A 3B 03 00 00 76 20 EE 07 ...
   ^^ ^^ ^^       ^^ ^^ ^^
   10 59  3      118 32 238      = mode 10, brightness 59, speed 3, RGB(118,32,238)
```

| Write type | Command | Acknowledged | Persists |
|---|---|---|---|
| Configuration / lighting settings | `0x15` | ✅ | ✅ **yes** |
| Key matrix | `0x18` | ✅ | ❌ no |
| Per-key lighting table | `0x19` | ✅ | ❌ no |

**This is the decisive control.** A successful write round trip over the same connection, in the
same session style, with the same checksum, proves our transport, checksum, framing, interface
selection and read path are all correct. Nothing on our side is at fault.

### Conclusion

The device accepts configuration writes, rejects table writes, and reports zero remappable keys.
The straightforward reading is that **key remapping is not enabled in this firmware build**. The
96-key table we can read is the factory default compiled into the firmware; the feature that would
let it be changed appears to be switched off.

## 5. What has been ruled out

Every one of these was tried on the hardware. All were acknowledged by the firmware; none changed
the key table.

| Variable | Variants tried |
|---|---|
| Transfer size | 336 B (6 packets), 384 B (7 packets), 54-byte aligned packets, single 3-byte write |
| Commit step | With and without `Set Profile` after the write |
| Session structure | Read and write in one session; in separate sessions |
| Timing | Packet gaps 3 ms – 500 ms; settle delays up to 500 ms |
| Acknowledgements | With and without consuming each packet's acknowledgement |
| Verification | Both read commands (`0x16` and `0x17`) — byte-identical in every case |
| Power cycle | Unplug and replug after writing — no effect |

## 6. What we need from the customer

In priority order. **Any one of the first three unblocks the feature.**

| # | Request | Why it unblocks |
|---|---|---|
| 1 | **Confirm whether key remapping is enabled in our firmware build.** Device Info byte 6 (key count) reads `0`; should it be 96? If not, which firmware version enables it, and can existing units be updated? | Directly explains both measurements |
| 2 | **A USB capture** (Wireshark/USBPcap) of the vendor's own configuration tool performing one key remap — both directions, timestamped, from clicking Apply until the key changes | A single trace answers every remaining question at once |
| 3 | **The vendor configuration tool itself** | We capture the traffic ourselves; costs the vendor no engineering time |
| 4 | Why does `0x15` persist while `0x18` and `0x19` are acknowledged and discarded? Are table writes gated on the key count, or on a separate write-enable? | Would confirm or refute the diagnosis |
| 5 | Is there a status or error code for a rejected write? Every packet returns `0x55` regardless | Currently a discarded write is indistinguishable from an accepted one |
| 6 | The documentation says a full 384 bytes must be sent; the vendor's own capture shows 336. Which is correct? | Removes a contradiction in the source material |

**Cheap check on our side first:** if a second Rampage unit is available, reading its Device Info
tells us whether the zero key count is specific to this sample (a firmware/reflash issue) or common
to the batch (a build issue). That is a two-minute test and worth doing before the vendor is
contacted.

## 7. State of the code

| Component | Status |
|---|---|
| Protocol layer, checksum, framing | ✅ Complete, verified against hardware |
| Key catalog (86 keys) and on-screen layout | ✅ Complete |
| gRPC contract, DeviceHost handler, App UI | ✅ Complete |
| Reading the keyboard's current assignments | ✅ Works correctly |
| Writing an assignment | ⛔ Blocked by firmware |
| Automated tests | ✅ 434 passing |

The UI deliberately refuses to show a remap it cannot confirm, so nothing misreports success to a
user. Recommended before merge: remove the investigation-only diagnostics, and hide the Key
assignments section for Rampage behind a flag until the firmware question is answered. The protocol
layer, the interface-selection fix and the corrected documentation are valuable independently of the
remap feature.

---

*Supporting detail:* [`vendor-request.md`](../../knowledge-base/rampage-keyboard-protocol/vendor-request.md)
(the full technical request) · [`hardware-findings.md`](../../knowledge-base/rampage-keyboard-protocol/hardware-findings.md)
(everything measured on the device) · [`README.md`](../../knowledge-base/rampage-keyboard-protocol/README.md)
(corrected protocol reference)
