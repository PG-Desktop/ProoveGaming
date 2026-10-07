# BLAZE / X43 — Key Matrix (button bindings)

The 16-button key matrix is **param_1 block 2** (bytes 128–191 of the 192-byte
param_1), 16 slots × 4 bytes. This is what maps each physical button to a function.

> ⚠️ **Writing the key matrix is the operation that has bricked the buttons on this
> device.** Read the [safety rules](#safety--recovery) before touching it.

## Entry format

Each 4-byte entry is stored **low address first**: `[Code, b1, b2, Mode]` — the
**Mode** byte is last (byte 3). Mode selects how the firmware interprets the entry;
`Code`/`b1`/`b2` are the arguments. Full mode/code tables:
[`docs/x43_protocol_904a4f8.md`](../../x43_protocol_904a4f8.md) §II / §V.

Common modes seen on this unit:

| Mode | Meaning |
|------|---------|
| `0x02` | Mouse button (Code = `F0`=L, `F1`=R, `F2`=M, `F3`=B4, `F4`=B5; `00`=null) |
| `0x04` | ACPI key |
| `0x06` | **Report-rate** control (this MCU's encoding — see below) |
| `0x0A` | LED control (Code `03` = effect cycle) |
| `0x0B` | DPI switch (Code `01`=+, `02`=-, `03`=+loop) |
| `0xF0` | Special key (profile / report-rate / snipe / tab) |

## Physical slot → function (this unit)

Confirmed by hardware testing (the `MapUnknownSlots` letter-binding diagnostic bound
each slot to a distinct keyboard letter and observed which physical press produced
it) and cross-checked against the byte-exact matrix VS750 writes (from the USB
capture, 2026-07-10):

| Slot | Bytes | Physical button | Function |
|------|-------|-----------------|----------|
| 0 | `F0 00 00 02` | Left click | Mouse L |
| 1 | `F1 00 00 02` | Right click | Mouse R |
| 2 | `F2 00 00 02` | **Wheel click** | Mouse M |
| 3 | `F4 00 00 02` | Top side button | Forward (B5) |
| 4 | `F3 00 00 02` | Bottom side button | Back (B4) |
| 5 | `03 00 00 0B` | **DPI button** | DPI + loop (cycles stage 0→6, wraps) |
| 6 | `03 00 00 06` | **Polling button** | Report-rate loop (see note) |
| 7 | `00 00 00 0E` | **RGB slide switch** | released to software — see below (OEM writes `03 00 00 0A`) |
| 8–13 | `00 00 00 02` | — | null mouse buttons |
| 14 | `00 01 00 04` | — | ACPI |
| 15 | `00 02 00 04` | — | ACPI |

The three "loop" buttons (DPI / rate / LED) all share loop-code `Code=0x03` and differ
only by Mode (`0x0B` / `0x06` / `0x0A`).

> ### KEY6 = report-rate loop, mode `0x06` (resolved 2026-07-10)
> The polling button's real factory binding is `[03 00 00 06]` — **mode `0x06`**, which
> the generic Easen spec marks "reserved". This is what VS750 actually writes; it is
> **not** the `[03 00 02 F0]` (mode `0xF0` / special-key type 02) form some docs list,
> and earlier `0xF0`-based rebind guesses failed for exactly this reason. Its loop steps
> the polling **period** up (1→2→4→8 ms, wraps) → Hz reads **1000→500→250→125**. There
> is no documented decrement/reverse loop, so making the button cycle Hz **min→max** is
> very likely not firmware-expressible. See [dpi-stage.md](dpi-stage.md#report-rate-polling-rate).

## The RGB slide switch (slot 7)

> ### RESOLVED 2026-08-17 — this slot is why RGB did not survive a reconnect
> We now write **`00 00 00 0E`**, not the OEM's `03 00 00 0A`.
>
> With `0A/03` the slot is bound to "LED effect cycle" and the switch owns the LED **in firmware**.
> The colour the app sets applies fine while the mouse stays connected — which is why this went
> unnoticed for so long — but on **replug the firmware takes the effect back and the LED is simply
> off**. Every earlier investigation read that as "the LED does not persist across reconnect" and
> treated it as a firmware limitation; the vendor Q&A of 2026-07-27 even records persistence as
> being by design. It was this slot all along.
>
> Mode `0x0E` came from the vendor on 2026-08-17 and is **not in the protocol spec they supplied**
> (which documents `01`/`02`/`03`/`04`/`05`/`0A`/`0B`/`0C`/`F0`). Confirmed on hardware the same
> day: with `0x0E` the colour survives a reconnect, with `0x0A` it does not.
>
> **Still to confirm with the vendor:** what mode `0x0E` means and in which byte position, since it
> is absent from the specification. Note also that the value is a deliberate divergence from what
> their own VS750 software writes.
>
> **Open issue from the same session:** after the first write carrying `0x0E`, right-click stopped
> responding and left-click became intermittent. Not yet explained — the slots for those buttons
> (0 and 1) are written byte-identically to factory in the same transaction. See
> [engineering-notes.md](engineering-notes.md) before writing this matrix again.
>
> **Mitigation in place, NOT yet validated.** The symptom shows up on cold start, which is where the
> App used to fire **two** full apply transactions back to back: a saved-RGB push followed by the
> last-preset apply, even though the preset configuration already carried the same lighting. Two
> things changed on 2026-08-17:
>
> 1. **The pair is now one apply.** `HomeViewModel.ReapplyOnConnect` →
>    `DeviceDetailsViewModel.ReapplyOnConnectAsync` issues the coordinated apply (lighting included)
>    and falls back to a lighting-only push **only** for devices the apply does not serve — keyboards,
>    transports that cannot carry configuration, a device on its default preset. At most one hardware
>    transaction per connect, either way. This removes the burst at its source.
> 2. **`EasenMouseService` also enforces a minimum 150 ms quiet period between apply transactions**
>    (`TransactionSettleMs`), kept as a backstop for any other path that lands two applies together.
>
> Both rest on the theory that a burst is what upsets the firmware, which is what the July 2026
> finding recorded. That theory is still **unproven**, and slot 7 itself is an equally plausible
> cause.
>
> **ROOT CAUSE FOUND 2026-08-17 — it was not the connect.** The host log for a reproduction shows
> **nine** full `ApplyDeviceConfiguration` calls between 19:50:02 and 19:50:36, each
> `settings=true dpiStages=7 buttons=4 led=true` and differing only in the LED effect, brightness and
> speed — i.e. repeated clicks of **Random lighting**. Five seconds after the last one the device
> stopped opening at all (`[DPI] state query: HidSharp TryOpen failed`), and the buttons were dead.
>
> Every one of those carried `buttons=4`, so `ChangedBlocksMask` set bit 2 and **the key matrix was
> re-committed to flash on every colour change**. Nine flash rewrites of the button table in 34
> seconds is what kills it. This explains both loose ends: "not constantly" (it depends how much you
> play with the RGB section) and "changing anything restores it" (a later apply rewrites the matrix).
>
> Note the applies were 2–5 seconds apart, so the 150 ms `TransactionSettleMs` never engaged — it was
> aimed at the wrong burst, and so was the connect-path merge above. Both were still worth doing;
> neither was this.
>
> **Two guards now stop it:**
> 1. **The App sends only what the edit touched.** An RGB edit stages `DeviceApplyAspects.Led`, so
>    bindings / DPI / settings go as null — the frame's existing "leave this alone". Preset switch,
>    Save Preset, Discard and connect still send everything.
> 2. **The service drops blocks whose content has not changed** (`X43BlockCommitCache`), so no caller
>    can re-commit an identical key matrix, now or later. The first apply of a session still commits
>    whatever was asked for.
>
> **To validate:** open the RGB section and click Random lighting 10+ times, then test left and right
> click. The apply log line now ends with `committed=0x…` — the mask that actually reached flash
> (bit0 settings/DPI, bit1 LED, bit2 key matrix). Every one of those clicks must show `committed=0x02`
> and never bit 2. If the buttons still die while bit 2 is never set, the flash-commit theory is wrong
> and slot 7's `0x0E` is the remaining suspect — ask the vendor what that mode means before going
> further.

### Earlier notes

Slot 7 is the physical LED slide switch on the mouse body. Notes from testing:
- Factory leaves it able to cycle the LED effect (`0A/03`). When bound as a **vendor
  key** `[01 00 00 F1]`, each swipe instead emits a `0x05` report on the vendor
  collection — the OEM design has the driver receive that and toggle the LED in
  software. (We do **not** use that mode now; the DPI/rate notification channel is the
  same `0x05` report — see [dpi-stage.md](dpi-stage.md).)
- On our unit the LED itself is dead regardless of binding — see [led.md](led.md).
- **CONFIRMED not software-hookable (2026-07-13).** Instrumented the proven Col04 reader
  (`DpiStageMonitor`, `ReadFile`) to dump every raw `0x05` report and toggled the slide
  switch ~6× in a clean run: **every report was byte-identical** (`05 00 08 06 02 00 00`) —
  no push, no `ledEffect` (byte 4) change — while the same reader cleanly caught DPI-button
  presses (byte 3 `1→…→6`). Tested in **both** factory (`0A/03`) and vendor-key (`01 00 00 F1`)
  slot-7 modes; neither surfaces the switch. So the switch cycles the LED purely in firmware
  and can't be detected or repurposed from software on this hardware. Don't re-investigate.

## Writing the matrix

Write **block 2 only** to avoid disturbing the DPI table (block 0) or colours (block 1):
`SetFeature [0, 0x03, profile, 2, 0, 0, 0x04, 1, 0]` then one 65-byte output report
(report-ID 0 + 64 bytes). `EasenMouseService.WriteParam1BlockAsync(stream, 2, matrix)`
does exactly this.

Gotcha: the **DPI button only cycles if param_1 block 0 holds a valid DPI table**
(`dpiStage_Max` + values). Restoring the key matrix alone leaves the DPI button "dead"
if block 0 is still zeroed.

## Safety & recovery

The buttons were bricked once by a `param_1` write with `status=0x07, times=3` that
sent **all three blocks with two of them zeroed** — this wiped the key matrix and the
DPI table in one shot. Rules learned:

1. **Write one block at a time** (`status` = single bit, `times=1`) unless you have
   complete, correct content for all three blocks.
2. **Never fabricate a "plausible" default matrix** to make a write "look valid" —
   that silently destroys the user's real bindings. See [engineering-notes.md](engineering-notes.md).
3. Keep the recovery tool ready: **`ProoveGaming.Device.Testing` → `X43Mouse.RestoreAll()`**
   is a one-shot full recovery (blocks 0+1+2 + param_0).

## Read-back

The current matrix **cannot be read back** — reading param_1 blocks needs a 64-byte
input endpoint the device doesn't expose; `0x83` returns only a 9-byte ACK header. So
any matrix write is effectively blind. See the read-back matrix in [README.md](README.md).
