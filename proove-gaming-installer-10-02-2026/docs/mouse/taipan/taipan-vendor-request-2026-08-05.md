# PG Taipan — status and open requests

**Date:** 2026-08-05
**Follows:** your answers to our earlier questions (items T1–T10)

Everything below was measured on real hardware, on Windows. Where we quote frames, they are exactly
what went on the wire. Where we quote enumeration values, they are read straight from the HID/USB
descriptors.

---

## Part 1 — What now works

| Area | Transport | Status |
| --- | --- | --- |
| Button remapping, macros | Cable | Working |
| DPI stage table + active stage | Cable | Working |
| Polling rate, lift-off distance (MsConfig) | Cable | Working |
| Battery percentage | 2.4G | Formula corrected — see Part 4 |
| Device identification over Bluetooth | Bluetooth | Fixed — see note below |

Two corrections we made on our side after your answers:

- **Battery percentage.** We now use the formula from your **T6** answer,
  `(100 × (raw − 750)) / (985 − 750)`, clamped at 100, applied to the raw value in the `0x48` push.
  Previously we treated the raw value as quarter-millivolts over a 3300–4200 mV range, which
  under-reported everywhere. Two real pushes measured here — raw `986` and `982` — now read **100 %**
  and **98 %**, consistent with your worked example (`0x03D5` = 981 → 98 %). Under our old curve they
  read 71 % and 69 %. **Your formula is confirmed correct against hardware.**

- **Bluetooth Product ID.** In Bluetooth mode the mouse enumerates under **PID `0x8266`**, not the
  2.4G receiver's `0x8373`. Our configuration had the wrong value, so the mouse was never recognised
  over Bluetooth. Fixed. *(Please confirm `0x8266` is the intended, final BLE Product ID.)*

---

## Part 2 — REQUEST A: configuration over the 2.4G dongle

**Your T2 answer:** "只支持有线和2.4G 不支持蓝牙 协议都是一样的" — wired and 2.4G are both supported
and the protocol is identical; if 2.4G does not work, send us your connection method and we will
look.

**What we observe: no host→mouse configuration frame has any effect over the 2.4G dongle.** Reads
are silent and writes do nothing. Wired configuration works, using the same code and the same frames.

### Setup

- Mouse on the 2.4G receiver only, **USB cable unplugged** (verified: wired PID `0xFB01` absent from
  enumeration). Cursor movement confirmed working throughout, so the radio link was up.
- Interface used — the one your own dump documents under the `2.4G` column:

```
\\?\hid#vid_248a&pid_8373&mi_00&col04#...
usage 0x0000   usagePage 0x0001   in=33   out=33   feature=8
report descriptor:  Input id=0x05 len=33 | Output id=0x05 len=33 | Feature id=0x05 len=8
```

A 33-byte OUTPUT report with report ID `0x05` is therefore valid on this collection; Windows accepts
every frame without error.

### We reproduced your own capture byte for byte

From your USB capture (`fabric-info`, image 2), we matched the frame sequence exactly — including
your per-field chunking and your `ValidBankId` frame carrying the `0xD3` commit, which differ from
what we originally sent:

```
05 A8 FA 02 00 00 01 D1 | 07                 stage count (0xD1 erases the sector)
05 B2 FA 02 01 00 05 D2 | 01 08 00 08 00     stage 0
05 BF FA 02 06 00 05 D2 | 01 08 00 08 00     stage 1
…
05 91 FA 02 24 00 05 D2 | 00 00 00 00 00     stage 7 (disabled)
05 29 FA 02 7F 00 01 D3 | 1A                 ValidBankId + commit
```

Every header byte — command, address, length, sequence flag and checksum — is identical to your
capture. Only the payload differs (all stages set to 400 CPI so the result would be unmistakable).

**Result: no change on the device.** The DPI button still cycled through different speeds, which
would be impossible if all seven stages had been written to the same value.

The same was then done for the **button map** (command `0x03`, right-click → `8F 08 08 00`,
i.e. Win+E): no effect either.

### Everything we ruled out

| Hypothesis | How tested | Result |
| --- | --- | --- |
| Wrong frame format | Compared to your capture byte for byte, checksums included | Identical |
| Wrong chunking / commit shape | Reproduced your per-field frames and `ValidBankId`+`0xD3` | No effect |
| Too fast for the radio | Repeated at 80 ms between frames instead of 12 ms | No effect |
| Wrong endpoint | Enumerated **every** HID device on the machine | The receiver exposes exactly **one** writable endpoint (`MI_00&COL04`). Its other collections have `out=0`; the keyboard collection is OS-exclusive with a 2-byte output |
| Reply sent to another collection | Listened on all receiver collections simultaneously while querying | Nothing arrives anywhere |
| Faulty mouse or receiver | Repeated with a **second** Taipan + receiver pair | Identical behaviour |
| Channel needs opening | Sent `Set_MouseState 0x46` (`1 = enabled`) before the write | No effect |
| Feature report is a handshake | Read the 8-byte Feature report, wrote back the **same** bytes | `SetFeature` **rejected** by the device |

### The one difference we cannot test — please advise

| | Your dump | Our hardware |
| --- | --- | --- |
| Mouse (`0xFB01`) `bcdDevice` | `0x1007` | **`0x1007` — identical** |
| 2.4G receiver (`0x8373`) `bcdDevice` | **`0x6539`** | **`0x0215`** |

Our mouse runs the **same firmware you validated**. Our receivers do not — and we have two, both
reporting `0x0215`.

> **Request A.** Is 2.4G configuration relay implemented in a specific **receiver** firmware build?
> If so, which revision is required, and can you supply that firmware or a receiver running it?
> If `0x0215` should already support it, what else differs between your test setup and the frames
> above?

---

## Part 3 — REQUEST B: detecting that the mouse is switched OFF

The mouse has a physical switch: **OFF / 2.4G / Bluetooth**. We need to show the user whether their
mouse is actually connected.

Measured by cycling the switch while watching HID enumeration and the receiver's driver collection:

| Action | What the host sees |
| --- | --- |
| Switch **OFF** (from 2.4G) | **Nothing at all.** The receiver's interfaces stay enumerated; no report arrives |
| Switch to **2.4G** | A `0x48` battery push arrives within a few seconds |
| Switch to **Bluetooth** | The BLE device (`0x8266`) **appears** |
| Switch **OFF** (from Bluetooth) | The BLE device **disappears** |

So over Bluetooth the link state is directly observable, but **over the 2.4G dongle it is not**: the
receiver is its own USB device and stays present whether or not a mouse is talking to it. A mouse
switched fully off is indistinguishable from one in normal use, so we currently display it as
connected when it is not.

> **Request B.** Is there any way for the host to learn the 2.4G link state?
> - Does the receiver expose a link-status byte anywhere we can read — for example the 8-byte
>   **Feature report** on `MI_00&COL04` (it reads `04 55 91 01 00 00 00 00` here; what do those
>   bytes mean)?
> - Does the mouse emit any report on **disconnect / power-off**, or the receiver on link loss?
> - If neither exists today, could a periodic keep-alive or a link-state push be added to the
>   receiver firmware?

---

## Part 4 — REQUEST C: reading battery on demand

Battery reaches us **only** in the unsolicited `0x48` push, and that push arrives **only when the
2.4G link is established** — confirmed by measurement: switching into 2.4G produces one, and nothing
else does.

Consequence: if our application starts while the mouse is already connected, it never receives a
battery reading at all, and the user sees no battery level until they physically re-link the mouse.
This matches your **T4** answer that `0x49` cannot be polled.

Two real pushes, both checksum-valid, with no cable attached (`Para3 = 0`):

```
05 79 FA 48 DA 03 00 00 …    raw = 0x03DA = 986  ->  100 %
05 7D FA 48 D6 03 00 00 …    raw = 0x03D6 = 982  ->   98 %
```

*(Payload byte 10 is `0x01` in both. We do not know what it means — please confirm.)*

> **Request C.** How can the host obtain the current battery level **on demand**?
> - Is there a command that makes the mouse send a `0x48` immediately? `CMD_GetBATV 0x08` produces
>   no reply over the dongle in our tests.
> - If not, can the mouse push `0x48` **periodically** (for example every 30–60 s) rather than only
>   on link establishment?
> - Alternatively, can the receiver cache the last value and return it when queried?

---

## Part 5 — Additional question: unique device identity

The mouse reports **no USB serial number**, so Windows derives its device identity from the USB port.
Moving the receiver to a different port makes the same mouse look like a new device to host software,
and its saved settings no longer follow it.

We can see that a unique identifier exists in the hardware: in Bluetooth mode the interface path
contains the BLE address (`D1:01:77:9C:59:0F` on this unit). But that is only visible over Bluetooth.

> **Request D.**
> - Can the firmware program a unique `iSerialNumber` in the USB device descriptor? This is the
>   standard solution and would fix the problem for any host software, not only ours.
> - Or: is a unique chip ID / pairing address readable over the existing configuration protocol —
>   and if so, via which command and address?

---

## Summary of requests

| # | Request | Impact if unresolved |
| --- | --- | --- |
| **A** | Receiver firmware that relays configuration over 2.4G — or what else differs | Users must plug in the cable to change DPI, buttons or macros |
| **B** | A way to detect the 2.4G link state / mouse switched off | We show a mouse as connected when it is powered off |
| **C** | On-demand or periodic battery reporting | Battery is blank until the user re-links the mouse |
| **D** | A unique per-unit identifier (USB serial, or readable chip ID) | Settings do not follow a device between USB ports |

## Notes on method

All measurements were taken with a small standalone HID test tool talking directly to the device —
no driver, no service, nothing else holding the interfaces open — so that results depend only on the
frames sent and the device's response.

For each test the physical setup was verified first: for 2.4G tests the USB cable was unplugged and
its absence confirmed in enumeration, because with the cable attached the mouse runs in wired mode
and the radio link is idle, which would make every 2.4G read time out for a trivial reason.

We are happy to run any additional test you would like, or to capture and send the USB traffic from
your own configuration tool for comparison.
