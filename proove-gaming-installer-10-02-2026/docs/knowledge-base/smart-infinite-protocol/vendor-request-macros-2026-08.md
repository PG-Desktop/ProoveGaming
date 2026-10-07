# Smart Infinite keyboards — macro storage request to the firmware vendor

**Subject:** Macro data is never stored on either tested keyboard. On Cyrex, `0x2D` acknowledges
every packet — at every transfer shape including the full 2048-byte `MACRO_TAB` you prescribed —
and `0x2C` reads the whole region back as zeros. On Rampage (RM1), `0x2C`/`0x2D` return status
`0x0F` on every packet, with **and without** a preceding `0x10`. Key remapping works on both boards
using exactly the rules from your C1–C12 answers, so the transport is proven; the macro subsystem
specifically does not store.

**Date:** 2026-08-04. Follow-up to your C1–C12 clarifications, which we have implemented in full.

---

## 0. First: your key-write answer is confirmed working

Before the macro problem — your C1/C3 instruction (no `0x10`/`0x11`, bare `0x18`, full-table
transfer) was implemented and verified on both keyboards:

| Board | Firmware | Key write (bare `0x18`, 384 bytes, 7 packets) |
|---|---|---|
| Rampage (RM1) | `0x0100` | ✅ 7/7 acked `0x55`, read-back matches, **physical key follows immediately** |
| Cyrex | `0x0002` | ✅ 7/7 acked `0x55`, read-back matches, physical key follows |

This matters for everything below: the same interface, framing, checksum and pacing that make key
writes persist are used byte-for-byte for the macro writes that do not.

## 1. Devices under test

| Field | Cyrex | Rampage (RM1) |
|---|---|---|
| USB VID / PID | `0x28E9` / `0x3272` | `0x28E9` / `0x3258` |
| Interface | `mi_01&col05`, usage `0x20`, page `0xFF87`, 64/64 | same |
| Device Info (`0x12`) | `productId=0x2404, matrix=75 slots (225 B), macro=1024 B, keys=68, USB, fw=0x0002` | `productId=0x3258, matrix=128 slots (384 B), macro=2048 B, keys=0, USB+BT+2.4G, fw=0x0100` |

Note the two macro-space values against your C8 `MACRO_TAB_SIZE = 2048`: Cyrex reports **1024**.
We wrote the full 2048 anyway (see below) — every chunk, including addresses ≥ 1024, was
acknowledged.

## 2. The macro payload we write

One macro that types `tt` — 4 actions, packed per your 宏信息格式 (Macro Information Format),
30 bytes of data. Decoded example from an earlier capture of the identical library:

```
AA 55 1E 00 01 00 00 00 0A 00 04 00 00 00 0A 00 17 81 50 00 17 01 DC 00 17 81 64 00 17 01
│AA 55│ in-use  │1E 00│ total length 30  │01 00│ 1 macro  │0A 00│ macro 0 offset 10
│04 00│ 4 actions │ then per action: key 0x17 (T), attr 0x81 press / 0x01 release, delay
```

The same buffer, zero-padded to 2048 bytes, is what the full-table transfers below carry.

## 3. Cyrex `fw 0x0002` — acknowledged at every shape, stored at none

Every variant below was acknowledged `0x55` **chunk by chunk**, and after each one a scan of the
**entire 2048-byte region** via `0x2C` (every read acknowledged `0x55`) returned **all zeros**:

| # | Transfer shape | Session bracket | Result |
|---|---|---|---|
| 1 | 30 bytes (data only), 1 packet | inside `0x10`…`0x11` | acked; region zero |
| 2 | 30 bytes (data only), 1 packet | none (bare) | acked; region zero |
| 3 | full 2048 bytes, 37 packets (36 × 56 + 32) | inside `0x10`…`0x11` | 37/37 acked; region zero |
| 4 | full 2048 bytes, 37 packets | none (bare) | 37/37 acked; region zero |

Verbatim tool output for shapes 3–4 (read-back scan covers all 2048 bytes, so the data is not
merely landing at an unexpected offset):

```
A  bare 0x2C read:      status 0x55 (OK)
B  bare 0x2D write:     status 0x55 (OK)
C1 bracketed 0x2D 2048: acked=37 rejected=0 silent=0
C2 in-session 0x2C:     ALL ZERO — acked and discarded
D  post-End  0x2C:      ALL ZERO — acked and discarded

bare 0x2D 2048: 37/37 acked
scan after full write:    ALL 2048 BYTES ZERO
bare 0x2D 30-byte: status 0x55
scan after 30-byte write: ALL 2048 BYTES ZERO
```

For completeness: a key record `{0x60, 0x00, 0x00}` (MACRO_AP_TYPE, macro 0, play-through) was
also written to a key slot and verified by read-back — the physical key then did nothing, which is
consistent with the macro region being empty.

## 4. Rampage (RM1) `fw 0x0100` — `0x2C`/`0x2D` rejected with `0x0F`, session or not

On the same firmware where bare `0x18` key writes work (your RM1 answer, confirmed), the macro
commands are not reachable at all:

```
A  bare 0x2C read:      status 0x0F
B  bare 0x2D write:     status 0x0F
C1 bracketed 0x2D 2048: acked=0 rejected=37 (every status 0x0F) silent=0
C2 in-session 0x2C:     every read status 0x0F
```

Your documentation describes `0x0F` as the session-lapse status — but here it is returned on the
very first command **after `0x10` was just sent and acknowledged**, so that reading cannot apply.
This firmware uses the older command numbering (key matrix on `0x17`/`0x18`, not `0x1A`/`0x1B`),
and we have never had the macro command bytes documented for it.

## 5. Ruled out

- **Transport / framing / checksum / interface** — proven by working key writes on the same
  handle, same session style, same additive checksum, both boards.
- **Transfer length** — 30, 1024-boundary and full 2048 all behave identically on Cyrex.
- **Session bracket** — with and without, identically.
- **Wrong read offset** — the read-back scans the entire 2048-byte region, not just offset 0.
- **Packet pacing** — same 4–5 ms pacing the working key writes use.

## 6. What we need

Any one of these unblocks the feature; they are in order of preference.

### 6.1 The exact macro procedure, per firmware

For **each** of `fw 0x0002` (Cyrex) and `fw 0x0100` (RM1):

- the command bytes that read and write `MACRO_TAB`;
- whether a session (`0x10`) is required for them;
- the exact required transfer length (your C1 said 384 for keys, your C3 said 386 — which of
  these logics applies to the 2048-byte macro table?);
- whether any enable, commit or bank-select step must precede or follow the write;
- whether macro storage is even enabled in these firmware builds — on Cyrex the region is
  writable-in-name-only (every ack, zero storage), which looks exactly like our earlier RM1 key
  situation that turned out to be a missing prerequisite.

### 6.2 A USB capture of your own tool storing one macro

A Wireshark/USBPcap capture on `mi_01&col05` of your configuration tool recording one macro and
binding it to a key, from clicking "save" until the physical key plays it — both directions, with
timestamps. One trace answers every question above at once.

### 6.3 Answers to the specific status question

What does status `0x0F` mean when it is returned immediately after an acknowledged `0x10`
(Rampage, `fw 0x0100`, commands `0x2C`/`0x2D`)? Is it "unknown command", "subsystem disabled", or
something else?

## 7. What we can offer

- Full host logs of any sequence you ask us to try, with every request and reply in hex, results
  the same day.
- Our implementation is complete on the host side: packing (byte-identical to your 宏信息格式
  worked example), chunked transfer at any shape you specify, read-back verification, key-record
  binding (`0x60`/`0x61`) — all finished and tested. The only missing piece is the firmware-side
  procedure that makes `MACRO_TAB` actually store.

---

*Related material:* our implementation of your C1–C12 answers and the measurement details are
recorded in [`factory-clarifications-2026-08.md`](factory-clarifications-2026-08.md); the original
macro investigation with the full packet decode is in
`docs/plans/add-cyrex-aress-parity/macro-investigation-result.md`.
