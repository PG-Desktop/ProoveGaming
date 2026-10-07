# Smart Infinite — factory clarifications, August 2026 (C1–C12)

The factory's answers to the Cyrex question list (C1–C12), received 2026-08. These supersede the
matching parts of the [translated workbook](README.md) and settle the transfer discipline for the
whole family (Cyrex, Aress, Rampage). Where an answer conflicts with itself or with a device's own
Device Info, the conflict is recorded here rather than smoothed over.

## The transfer rule (the headline)

- **C1.** Do **not** use `0x10` / `0x11`. Send bare `0x18`; the total transfer must be **384
  bytes**. The factory confirmed from our capture video that the observed failure is caused by
  transfers shorter than 384 bytes.
- **C3.** Every key reassignment must send the full all-keys array — sending only the changed
  key's data is not allowed. Total size: **386 bytes**.

> [!IMPORTANT]
> **C1 says 384; C3 says 386. The factory did not explain the difference.** The implementation
> uses **384**: it matches C1, matches `KEY_MATRIX_TAB_SIZE` (C8), and is the value already
> **proven on hardware** for Rampage (RM1 update: bare `0x18`, 7 packets of 6 × 56 + 48, physical
> key follows immediately). If a Cyrex/Aress write fails to verify or persist at 384, try 386
> (384 data + 2 trailing zero bytes, final chunk 50) as the next experiment and record the result
> here.

The same rule extended to the macro table: `0x2D` transfers the full `MACRO_TAB_SIZE` (2048, C8)
— the 30-byte partial write of the
[macro investigation](../../plans/add-cyrex-aress-parity/macro-investigation-result.md) is the
macro-side instance of the same defect.

## Everything else

- **C2.** In our capture the factory saw replies to `0x12`, `0x16`, `0x17`, `0x1F`, `0x22`,
  `0x30` — and asks us to verify the replies carry *correct data*, not merely that they arrive.
  Note: this codebase does not send `0x1F` or `0x22`; their appearance in the capture is
  unexplained (possibly firmware-initiated or another tool's traffic). Observe during the next
  hardware session.
- **C4.** No new answer on a slot-index ↔ physical-key table (we derive it from the factory
  table read; catalogs are already built).
- **C5.** No new answer on the FN combination for factory reset.
- **C6.** `0x20` Reset clears **all** configurable parameters (functional zones, key matrix, key
  travel, …), takes no payload, needs no `0x10`/`0x11`, and reboots the keyboard afterwards.
  This confirms the standing **DO NOT SEND** on `0x20`.
- **C7.** Only physical **Profile 0** exists — flash constraints rule out multiple profiles.
  `0x18` and `0x34` act on Profile 0.
- **C8.** Table sizes: `INFO_TAB_SIZE` 64, `FUNC_TAB_SIZE` 64, `KEY_MATRIX_TAB_SIZE` **384**,
  `FN_KEY_MATRIX_TAB_SIZE` 384, `LED_DEFINE_TAB_SIZE` 384, `MACRO_TAB_SIZE` **2048**.
  **Conflict:** Cyrex firmware `0x0002` reports 1024 bytes of macro space in Device Info byte 5.
  The C8 constant outranks the Device Info field for *transfer* sizing (precedent: the key table
  is 384 while Cyrex reports a 225-byte matrix and Rampage reports key count 0); the Device Info
  value is still treated as the *storage* the firmware claims, i.e. the data-must-fit gate.
- **C9.** The workbook's `light_level` 0–99 range is wrong. Use the device's actual levels from
  `0x14`: index 12 = max brightness, 13 = max speed, 14 = default brightness, 15 = default
  speed. (Matches the long-standing observation that Rampage reports max brightness 4.)
- **C10.** The host must **not** enable reporting — the keyboard sends its messages unprompted.
  Work with the vendor-defined HID collection the factory indicated, not the first available
  collection. (Matches `SmartInfiniteInterfaceSelector`: usage `0x20`, page `0xFF87`.)
- **C11.** Functional key codes are in `keyboard_key_define.h`. `0x33`/`0x34` (FN layer) are
  structured like `0x17`/`0x18`. The format of `0x35` was **not provided** — the FN layer stays
  unimplemented until it is.
- **C12.** Interface and framing per transport:
  - wired and 2.4G: UsagePage `0xFF87`, Usage `0x20`; additionally UsagePage `0xFF88`, Usage
    `0x21` was named;
  - **wired**: 64-byte packet, **56** payload bytes (what everything here implements);
  - **2.4G**: 64-byte packet, **24** payload bytes — a different chunking that no code supports
    yet. This is why Smart Infinite config work is wired-only until the dongle transport gets its
    own implementation and testing.

## Hardware results, 2026-08-04 — the full-table shape does NOT unblock macros

Measured on real boards after implementing the C1/C8 discipline (bare commands, full-table
transfers):

**Cyrex, firmware `0x0002` (macro cmds exist, storage dead):**

- Key writes: bare `0x18` × 384 bytes — 7/7 acked, verified, works. ✔
- `0x2C` / `0x2D` answer `0x55` bare (no session needed).
- **Every** macro write shape is acknowledged chunk-by-chunk and stored nowhere: 30-byte bare,
  30-byte bracketed, full 2048 bare, full 2048 bracketed — after each, a scan of the **entire
  2048-byte region** via `0x2C` returns all zeros (every read `0x55`, zero payload). The
  2048-vs-1024 question is moot until storage works at all: chunks at addresses ≥ 1024 are
  acked like the rest.

**Rampage, firmware `0x0100` (macro cmds not reachable):**

- Key commands (`0x17`/`0x18`) work bare — the RM1-confirmed behavior. ✔
- `0x2C` and `0x2D` answer **status `0x0F`** ("no session") — bare **and inside a `0x10`
  session**, every packet. On this firmware (which uses the older command numbering where the
  key matrix is `0x17`/`0x18`), `0x2C`/`0x2D` are evidently not the macro commands, or macro
  access is gated by something undisclosed. The macro command byte for this firmware was never
  documented — it is the Rampage knowledge base's oldest open question.

**Conclusion:** macros are blocked in firmware on both boards, by two different mechanisms. The
host implementation is complete and factory-conformant; no host-side shape change can fix this.

## Questions for the factory (macro follow-up)

1. On firmware `0x0002` (Cyrex): `0x2D 设定宏信息` acknowledges every chunk with `0x55` — at 30
   bytes, at the full 2048, bare and bracketed — yet `0x2C` reads the whole 2048-byte region
   back as zeros in every case. What actually stores `MACRO_TAB`? Is macro storage disabled in
   this build, or is there an enable/commit step?
2. On firmware `0x0100` (Rampage RM1): `0x2C` and `0x2D` return status `0x0F` on every packet,
   with and without a preceding `0x10`. What are the macro read/write command bytes on this
   firmware, and do they require a session?
3. What does status `0x0F` mean outside the session-lapse case, given `0x10` immediately before
   the command does not clear it?

## Open questions carried forward

1. 384 vs 386 (C1 vs C3) — unexplained by the factory; 384 implemented and verified working for
   key writes on both Cyrex (`fw 0x0002`) and Rampage (`fw 0x0100`).
2. What `0x1F` and `0x22` are (C2) — observed in the factory's reading of our capture, never
   sent by this codebase.
