# Cyrex / Aress macros — investigation result

**Status: blocked in firmware, not in this codebase.**
Date: 2026-07-30 · Branch: `features/PG-391-cyrexaress` · Hardware: Cyrex, firmware `0x0002`

The host side is complete and every part of it is verified against the device. The keyboard
acknowledges the macro-data write and stores nothing. This document records what was built, what
each hardware round ruled out, the evidence, and what would unblock it.

> **Scope of the measurement.** Everything below was measured on the **Cyrex**. The Aress was
> connected earlier in the day and reports identical geometry (`matrix=75 slots (225 B) macro=1024 B
> keys=68`, firmware `0x0000`), but only an *empty* library was ever uploaded to it and the
> read-back diagnostic did not exist yet — so the Aress is **untested** for this failure. It shares
> the protocol, the service and the record encoding, so the same result is expected, but that is an
> expectation and not a measurement.

---

## 1. Verdict in one paragraph

Writing the macro library with command `0x2D` is acknowledged with status `0x55`, packet by packet,
on the same interface and inside the same session that successfully rewrites the key matrix. Reading
the same region back with `0x2C` — in a **separate session**, after the closing `0x11` — returns
**all zeros**. The bytes we send are correct against the vendor's own format specification, and the
key record that points at them is byte-for-byte the specification's worked example. The device
simply does not keep macro data. This is the same behaviour Rampage shows for key-matrix writes:
an acknowledgement on this protocol family is not evidence that anything was stored.

---

## 2. What is proven working

Each row was measured on hardware, not assumed.

| Element | How it was established |
|---|---|
| Vendor interface selection | `\\?\hid#vid_28e9&pid_3272&mi_01&col05…` (usage `0x20`, page `0xFF87`, 64/64 reports) — the same handle that key remapping uses successfully |
| Session bracketing `0x10` / `0x11` | Every exchange in the logs completes; no `0x0F` "no session" status appeared |
| Additive checksum | Every command in these runs was accepted and answered; a malformed one would not be |
| Device Info `0x12` | `matrix=75 slots (225 B) macro=1024 B keys=68 transport=USB fw=0x0002 maxBri=4 maxSpd=4` |
| Key matrix read `0x17` | `read key matrix: 75 slots of 75` |
| Key matrix write `0x18` | `wrote the key matrix (5 packets, 225 bytes, 5/5 acked)` then `slot 17 verified as {0x60, 0x00, 0x00}` |
| Slot number | Slot 17 held `{0x10, 0x00, 0x1A}` (`KEY_W`) before the write — the key the user clicked |
| Macro read command `0x2C` | Replies well-formed with status `0x55`; it is a real, implemented command |
| Packed buffer contents | Decoded by hand against the vendor sheet — see §4 |

**Ordinary key remapping works on this exact board, over this exact interface and session.** That is
what makes the macro failure specific rather than a transport problem.

## 3. What does not work

`0x2D` (设定宏信息 / "set macro info") is acknowledged and discarded. The 1024 bytes of macro space
the firmware advertises in Device Info stay zero.

---

## 4. The evidence

From the DeviceHost log, after the read-back diagnostic was added:

```
[CYREX] UploadMacros count=1
[CYREX] device info: matrix=75 slots (225 B) macro=1024 B keys=68 transport=USB fw=0x0002
[CYREX] packed library dataLen=30 bytes, device macro space=1024 bytes
[CYREX] wrote the macro library (1 packets, 30 bytes)
[CYREX] macro library read back DIFFERENT, first divergence at byte 0.
        sent=AA551E00010000000A00040000000A00178150001701DC00178164001701
        read=000000000000000000000000000000000000000000000000000000000000
```

The sent buffer is a macro that types `tt`. Decoded against 宏信息格式 (Macro Information Format):

| Bytes | Value | Meaning |
|---|---|---|
| 0–1 | `AA 55` | In-use flag |
| 2–3 | `1E 00` | Total length = 30, header included |
| 4–5 | `01 00` | 1 macro |
| 6–7 | `00 00` | Reserved |
| 8–9 | `0A 00` | Macro 0's data starts at offset 10 |
| 10–11 | `04 00` | 4 actions (one keypress = press + release) |
| 12–13 | `00 00` | Reserved |
| 14–17 | `0A 00 17 81` | delay 10 ms, `KEY_T` (`0x17`), **press** (bit 7 set, kind `0x01`) |
| 18–21 | `50 00 17 01` | delay 80 ms, `KEY_T`, release |
| 22–25 | `DC 00 17 81` | delay 220 ms, `KEY_T`, press |
| 26–29 | `64 00 17 01` | delay 100 ms, `KEY_T`, release |

Nothing in it deviates from the specification.

And the key that should play it, written and verified in the same run:

```
[KEY CYREX] SetKeyMapping slot=17 record={0x60, 0x00, 0x00}
[KEY CYREX] slot 17 {0x10, 0x00, 0x1A} -> {0x60, 0x00, 0x00}, writing back 225 bytes
[KEY CYREX] wrote the key matrix (5 packets, 225 bytes, 5/5 acked)
[KEY CYREX] slot 17 verified as {0x60, 0x00, 0x00}
```

`{0x60, 0x00, 0x00}` is verbatim the vendor sheet's own worked example for "make this key a normal
macro key playing macro 0".

---

## 5. Documents used

Grouped by the role each played, because they are not interchangeable.

**The specification for this family** — the authority, and the only vendor document that covers
Cyrex/Aress. There is no Cyrex-specific macro reference in this repository; the workbook is
family-wide (智慧无限 = Cyrex, Aress, Rampage) and that is the right level for it, since all three
share the protocol.

| Document | Where | What it gave us |
|---|---|---|
| **软件驱动协议 V3.1**, 2025-10-28 | `docs/软件驱动协议-20251028-V3.1.xlsx`, translated in `docs/knowledge-base/smart-infinite-protocol/` | Sheet 命令: `0x2C`/`0x2D` are read/write macro info. Sheet 按键信息格式: the `Keyboard_Key_Type` enum that corrected the macro type to `0x60`, its play modes, and `0x61` for repeat counts. Sheet 宏信息格式: the macro buffer layout and the action-type bit rule |

**Prior measured work on this family** — what was established on this hardware before this task.

| Document | Where | What it gave us |
|---|---|---|
| Smart Infinite key-assignment plan | `../add-smart-infinite-key-assignments/add-smart-infinite-key-assignments-plan.md` | The framing as *measured* rather than as documented (report `0x06`, 64-byte packets, additive checksum), the Rampage acknowledge-and-discard precedent, and the hazard list |

**Provenance of the code under test** — these are Raster documents, and they are listed because the
buffer we send to the Cyrex is produced by `RasterMacroEncoder`, reused deliberately rather than
reimplemented. They are where its format came from; they are not a stand-in for Cyrex documentation
and nothing about the Cyrex was inferred from them.

| Document | Where | What it gave us |
|---|---|---|
| Vendor web-app protocol chunk (Raster) | `docs/plans/raster-key-assignments/reference/vendor-webapp-protocol-chunk.js` | The original packed-buffer writer `RasterMacroEncoder` was built from, and the press/release attribute values (`0x81` / `0x01`) |
| Macro definition table (Raster) | `docs/plans/raster-key-assignments/reference/macro-definition-table.xlsx` | Cross-check of the action encoding |

> **These two groups agree, independently.** The buffer layout and the `0x81`/`0x01` attribute bytes
> were derived from the Raster material long before the workbook was found, and the workbook's
> 宏信息格式 sheet then described the identical structure — same `AA 55` flag, same length and count
> fields, same 2-byte offset table, same 4-byte actions, same bit-7 press rule. Two unrelated sources
> reaching the same format is why §4 can state the sent bytes are correct rather than merely
> plausible.

### Caveat on the workbook, which cost us time

Its **data formats** match our hardware and are trustworthy — the key record, the macro buffer, and
the action-type bits all verified. Its **command numbering is a later revision than our firmware**:

- The sheet lists `0x1A` / `0x1B` for read/write of key matrix 1. Our boards answer on `0x17` / `0x18`.
- The sheet calls `0x31` "reset". On our boards `0x20` was measured as a **full factory reset**.

So a command number taken from this sheet is a lead to verify, never a fact. That distinction is now
recorded at the top of the translation.

---

## 6. What was tried, and what each round ruled out

| # | Change | Result | Ruled out |
|---|---|---|---|
| 1 | Key record `{0x50, 0x00, 0x00}` — `0x50` taken from a comment in `SmartInfiniteProtocol.KnownTypes` labelling `0x50`–`0x52` "macro" | Stored, verified, key silent | — |
| 2 | `{0x50, 0x00, 0x01}` — hypothesis that `code2` is a repeat count and `0` meant "zero plays" | Stored, verified, key silent | The third byte is not the problem |
| 3 | `{0x60, 0x00, 0x00}` — from the vendor enum, after finding the workbook | Stored, verified, key silent | The key record is not the problem |
| 4 | Read the library back with `0x2C`, in a separate session | **All zeros** | The macro *data* never reaches the device |

Rounds 1 and 2 were wasted on the wrong byte. The cause was a comment written during #72 that had
the vendor's `Keyboard_Key_Type` enum inverted — it called `0x50`–`0x52` "macro" and `0x60`/`0x61`
"custom fn, text", when in fact:

```c
FUNC_TYPE            = 0X50,  // function key: layer switch, backlight, Bluetooth channel
MACRO_AP_TYPE        = 0X60,  // single-play macro
MACRO_AP_REPETI_TYPE = 0X61,  // repeat macro
```

The comment was never load-bearing — `KnownTypes` only sanity-checks table shape and accepts both
ranges — so no test caught it, and it was then cited as if it were documentation. A key bound to
`{0x50, 0x00, n}` is a well-formed record naming function `0`, which does not exist in the function
enum, so the firmware accepts it, verifies it, and does nothing. The comments now name the enum's
own constants and a test pins `TypeMacro == 0x60 && != 0x50` with the reason.

### Real defects found and fixed along the way

Both were genuine, both are fixed and tested, and neither is the cause of the silent key:

1. **Macro bindings were never persisted.** The guard between the device write and the SQLite write
   compared `_keyLayoutGeneration`, which `LoadKeyLayoutForDevice` bumps even when the *same* device
   is reloaded (a card refresh or a preset apply does it). Landing in that window returned after the
   firmware had already been written, so device and database disagreed and the Macros page showed
   the key as unbound. Now guarded on device identity. Cyrex and Aress hit it far more than Raster
   because their key write is a 225-byte read-modify-write measured at ~420 ms.
2. **The Reset link was permanently greyed out** on these boards, so a bound macro could be replaced
   but never removed. `SelectedKeyHasOverride` was computed from the Raster-only override map.

---

## 7. Why it is not working

The firmware on this unit does not accept macro data on command `0x2D`, while acknowledging it.

Two readings fit the evidence equally well, and nothing we can do from the host distinguishes them:

- **`0x2D` is not the macro-write command on this firmware build.** The workbook is a later revision
  and is already known to disagree with these units on the key-matrix commands. If macro storage
  moved the same way, the real write command is a neighbour. Against this: `0x2C` reads plausibly
  and is documented as `0x2D`'s counterpart, so the pair is probably intact.
- **Macro storage is not enabled in this build.** Precedent: Rampage reports a key count of `0` and
  acknowledges-then-discards key-matrix writes, while configuration writes on the same handle
  persist byte for byte. That was established as a firmware limitation, not a driver defect. This is
  the same signature in a different subsystem — except that here the device *does* advertise 1024
  bytes of macro space, which makes the discard harder to explain as intentional.

---

## 8. What is needed to make it work

Ordered by cost. The first is free and decides whether the second is worth its risk.

1. **Confirm the vendor's own software can write a macro to this board.**
   If it cannot, this is a firmware question for the vendor and there is nothing to fix here. If it
   can, capture the exchange (USB capture, or the vendor tool's own log) and the command it uses is
   the answer. This single test resolves §7 either way.

2. **Ask the vendor two specific questions**, quoting firmware `0x0002` and PID `0x3272`:
   - Which command writes macro data on this build, given the driver protocol sheet dated 2025-10-28
     lists `0x2C` / `0x2D` but its key-matrix numbering (`0x1A`/`0x1B`) does not match this firmware,
     which answers on `0x17`/`0x18`?
   - Is there an enable or commit step before or after the macro write — a flag in the function area
     (`0x14`/`0x15`), or a specific ordering relative to `0x10`/`0x11`?

3. **Only if 1 and 2 both fail: probe neighbouring commands.** This carries real risk and must not be
   done blind:
   - **`0x20` is a full factory reset** on these units. Never send it.
   - **A `0x31` profile round-trip (`0→1→0`) freezes the firmware** until the board is physically
     unplugged.
   - Probing belongs in `src/ProoveGaming.Device.Testing`, one command per run, with a read-back
     after each, and never on a board that cannot be re-flashed.

---

## 9. Reproducing the diagnostic

1. Start `ProoveGaming.DeviceHost` and the App, connect a Cyrex.
2. Macros page → record a macro → select a key → click the macro row.
3. In the DeviceHost log look for the line after `wrote the macro library`:

| Line | Meaning |
|---|---|
| `macro library verified across a session boundary: N bytes read back identical` | The data is on the device; the remaining problem is activation, not encoding |
| `read back DIFFERENT … The region is entirely ZERO …` | The current state: acked and discarded |
| `read back DIFFERENT …` with non-zero bytes | The write lands, but not where or how intended; the reported byte index says where |
| `could not read the macro library back` | `0x2C` is not the read command on this build, which also weakens the `0x2D` assumption |

The verification is diagnostic only — it never fails the upload, because a disagreeing read can also
mean the read command behaves differently than assumed.

---

## 10. State of the code, and the one open decision

Everything in §2 is implemented, reviewed and covered by tests: `SmartInfiniteMacroService`, the
factory dispatch, the corrected macro record, per-device macro scoping, the artwork-accurate key
overlays for both boards, and the two defect fixes in §6. App 293 tests and Infrastructure 341 pass;
the solution builds clean. None of it needs to be reverted — it is correct and will work unchanged
the moment the firmware accepts the data.

**The open decision is `SupportsMacros`.** Both boards currently advertise it in
`src/ProoveGaming.DeviceHost/appsettings.json`, which gives users a Macros page that accepts a macro
and silently does nothing. Setting those two entries to `false` hides the page and returns both
boards to honest behaviour; it is a one-line change per board and reversible the moment this is
resolved. It has deliberately been left enabled so the diagnostic above stays reachable for testing.

---

*A standalone, self-contained HTML rendering of this document — no external assets, prints cleanly,
follows the reader's light/dark preference — is [`macro-investigation-result.html`](macro-investigation-result.html),
regenerated with `python md2html.py macro-investigation-result.md macro-investigation-result.html`.*
