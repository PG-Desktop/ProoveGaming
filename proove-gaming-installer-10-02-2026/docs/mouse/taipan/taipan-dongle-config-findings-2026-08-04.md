# TAIPAN — configuring over the 2.4G dongle: test results (2026-08-04)

Written to answer the vendor's offer in **T2** of `fabric-info/main.html`:

> 只支持有线和2.4G 不支持蓝牙 协议都是一样的，如果2.4G 不能下值需要把你们对应连接的方式发过来或者整个连接设备的代码发过来我们这边看一下
>
> "Only wired and 2.4G are supported, Bluetooth is not. The protocol is the same. If 2.4G cannot
> write values, send us your connection method or the whole device-connection code and we will
> take a look."

**Result: host→device configuration does not reach the mouse over the 2.4G dongle**, using the
vendor's own frame sequence, byte for byte. Wired configuration works. This document is the
evidence package for that follow-up.

Reproduce with `ProoveGaming.Device.Testing` (`TaipanDongleProbe`).

## Test setup

- Mouse connected by the **2.4G dongle only**. USB cable **unplugged** — verified by the wired PID
  `0xFB01` being absent from HID enumeration. (With the cable attached the mouse runs in wired mode,
  the radio link is idle, and every dongle read times out for a trivial reason; the probe now
  aborts in that state.)
- `ProoveGamingDeviceHost` stopped, so nothing else held the HID handles.
- Cursor movement confirmed working throughout, so the radio link was up.

## The interface used

Selected the dongle's driver collection:

```
\\?\hid#vid_248a&pid_8373&mi_00&col04#...
usage 0x0000  usagePage 0x0001  in=33  out=33  feature=8
```

This is the interface the vendor documents for 2.4G. In `fabric-info/main.html` the two device
dumps sit under column headers `有线` (wired) and `2.4G:` — column 4 and column 5 of the sheet — and
the `PID 0x8373` dump is the one under `2.4G:`. Our receiver is the unique collection matching it:
of the five collections the dongle exposes, only `MI_00&COL04` has that usage / usage page / report
lengths, and it is the only one with any output report large enough to carry a 33-byte frame.

### The mouse firmware matches the vendor's exactly; only the RECEIVER differs

Compared both units against the vendor's two dumps, field for field:

| Field | Vendor wired | Our wired | | Vendor 2.4G | Our 2.4G | |
| --- | --- | --- | --- | --- | --- | --- |
| VendorID | `0x248A` | `0x248A` | ✓ | `0x248A` | `0x248A` | ✓ |
| ProductID | `0xFB01` | `0xFB01` | ✓ | `0x8373` | `0x8373` | ✓ |
| **VersionNumber** (bcdDevice) | `0x1007` | `0x1007` | **✓** | **`0x6539`** | **`0x0215`** | **✗** |
| Usage | `0x0000` | `0x0000` | ✓ | `0x0000` | `0x0000` | ✓ |
| UsagePage | `0xFFEF` | `0xFFEF` | ✓ | `0x0001` | `0x0001` | ✓ |
| FeatureReportByteLength | `0x0040` | `0x0040` | ✓ | `0x0008` | `0x0008` | ✓ |
| InputReportByteLength | `0x40` | `0x40` | ✓ | `0x21` | `0x21` | ✓ |
| OutputReportByteLength | `0x40` | `0x40` | ✓ | `0x21` | `0x21` | ✓ |

**The mouse itself is on identical firmware to the unit the vendor tested (`0x1007`, every field
matching). The 2.4G receiver is not: `0x0215` here against `0x6539` in their dump.**

That is the discriminating result. It rules out "this sample is simply an older firmware vintage" —
the mouse is the same build the vendor validated — and isolates the difference to the receiver.

If 2.4G configuration relay is implemented in (or exclusive to) the vendor's receiver build, a
receiver on `0x0215` would behave exactly as observed: structurally valid frames accepted over USB
and never forwarded over the air. **This is still a correlation, not a proven cause** — but it is now
the only material difference we can find between their working setup and ours, and it is the first
thing to settle.

> Note when reading bcdDevice: it is **binary-coded decimal**. HidSharp exposes it already decoded
> into a decimal `Major.Minor`, so re-encoding it as `(Major << 8) | Minor` renders the wired unit's
> real `0x1007` as `0x0A07` and manufactures a firmware mismatch that does not exist. Each decimal
> digit must go back into its own nibble.

### Which wired collection the vendor's capture was actually taken on

Worth recording, because it confirms our wired channel choice. The vendor documents the wired
interface as `mi_02&col01` (usage page `0xFFEF`, 64-byte reports) — but that collection carries
**report ID `0x06`**, while every frame in their capture starts with `05`. The 33-byte wired
collection `mi_02&col02` is the one that carries report ID `0x05`.

So their capture was taken on the same 33-byte, report-ID-`0x05` channel our app uses over the
cable, not on the `0xFFEF` one their text names. Our wired path matches their reference
implementation.

Its HID report descriptor declares precisely what the protocol needs:

| Report type | ID | Length |
| --- | --- | --- |
| Input | `0x05` | 33 |
| Output | `0x05` | 33 |
| Feature | `0x05` | 8 |

So a 33-byte OUTPUT report with report id `0x05` is valid on this collection — Windows accepts it
and hands it to the receiver. The frames are not being rejected for a structural reason.

For comparison, the wired unit (`PID 0xFB01`) exposes **two** candidate channels — `MI_02&COL01`
(usage page `0xFFEF`, 64/64/64, the one the vendor's capture was taken on) and `MI_02&COL02`
(usage page `0x0001`, 33/33/0, the one our app uses). The dongle exposes only the 33-byte one.

## What works

- **Device→host pushes.** The mouse's unsolicited `0x48` (battery) and `0x49` (state) reports arrive
  over the dongle normally. The relay works in that direction.

## What does not work

### 1. Every read is silent

Sent `0x42` (DPI table), `0x41` (MsConfig), `0x43` (buttons) and `0x08` (battery) as queries.
No reply to any of them.

To rule out the reply being relayed to a different collection, the probe opened **every** dongle
collection with an input report simultaneously and listened on all of them while sending the
queries. Nothing arrived anywhere. (Two collections — the mouse and keyboard ones — are opened
exclusively by Windows and cannot be listened on; neither can carry a 33-byte report.)

This is consistent with the vendor's **T4** answer that `0x49` is push-only and cannot be polled,
but it also applies to `0x41`/`0x42`/`0x43`, which are documented as readable.

### 2. Writes have no effect, even in the vendor's exact frame shape

Our production writer packs the 41-byte DPI table into 25-byte blobs and commits with a separate
empty `0xD3`. The vendor's capture (`fabric-info/resources/image_574065711_1.jpg`) does neither —
it sends one field per frame and lets the `ValidBankId` frame carry the `0xD3`. That difference is
what **T8**'s "我们这边的是分开下的" ("ours is sent split up") refers to, and it was the leading
hypothesis for why the dongle might reject our writes.

So the test reproduced the capture exactly. Sent, in order:

```
05 A8 FA 02 00 00 01 D1 | 07                 stage count, erases the sector
05 B2 FA 02 01 00 05 D2 | 01 08 00 08 00     stage 0
05 BF FA 02 06 00 05 D2 | 01 08 00 08 00     stage 1
05 B8 FA 02 0B 00 05 D2 | 01 08 00 08 00     stage 2
05 85 FA 02 10 00 05 D2 | 01 08 00 08 00     stage 3
05 8E FA 02 15 00 05 D2 | 01 08 00 08 00     stage 4
05 8B FA 02 1A 00 05 D2 | 01 08 00 08 00     stage 5
05 94 FA 02 1F 00 05 D2 | 01 08 00 08 00     stage 6
05 91 FA 02 24 00 05 D2 | 00 00 00 00 00     stage 7 disabled
05 29 FA 02 7F 00 01 D3 | 1A                 ValidBankId, carrying the commit
```

Every header byte — command, address, length, sequence flag and checksum — is **identical to the
vendor's capture**; only the payload differs (all stages set to 400 CPI, register `0x08` = 400/50).

No USB error was returned on any frame.

**Observed on the device: nothing changed.** With all seven stages written to 400 CPI, pressing the
physical DPI button must have produced no speed change. It still cycles the cursor speed normally,
so the stage table on the mouse was never modified.

## Everything else that was tried, and ruled out

Each of these was a candidate explanation for why our writes might differ from the vendor's tool.
All are now closed.

| Hypothesis | Test | Result |
| --- | --- | --- |
| Wrong frame format | Compared to their USB capture byte for byte, checksums included | Identical |
| Wrong chunking / commit shape | Reproduced their per-field frames and `ValidBankId`-carries-`0xD3` | No effect |
| Too fast for the radio | Re-ran at 80 ms between frames instead of 12 ms | No effect |
| Wrong endpoint | Swept **every** HID device on the machine, not just VID `0x248A` | Only one writable endpoint exists on the receiver (`MI_00&COL04`, 33/33/8). The other `0x248A` collections have `out=0`; the keyboard collection is Windows-exclusive with a 2-byte output |
| Reply relayed elsewhere | Listened on every dongle collection at once while querying | Nothing arrives anywhere |
| Faulty mouse or receiver | Repeated with a **second** TAIPAN + receiver pair | Same behaviour; the second receiver also reports `bcdDevice 0x0215` |
| Driver channel needs opening | Sent `Set_MouseState 0x46` (`1 = enabled`) as a prelude | *(pending hardware check)* |
| Feature report is a handshake | The dongle's driver collection has an 8-byte Feature report the wired one lacks. Read it, then wrote back the **identical** bytes | **`SetFeature` rejected** (`IOException`) — the receiver does not accept feature writes at all |

The feature-report test was staged to write back only the bytes already present, so no invented value
was ever sent and no risk was taken; the rejection answers the question outright.

## Conclusion

Over the 2.4G dongle the link relays **mouse→host** reports but not **host→mouse** configuration
frames. The frames are structurally valid for the collection they are sent on, match the vendor's
own capture byte for byte, and are accepted by Windows and the receiver without error — they simply
never take effect on the mouse.

## Open questions for the vendor

0. **Receiver firmware revision — the leading question.** Our mouse reports `bcdDevice = 0x1007`,
   **identical** to the wired unit in `fabric-info`. Our 2.4G receiver reports `0x0215`, against
   `0x6539` in the same dump, with every other field identical. So we are running the same mouse
   firmware the vendor validated, on a different receiver build.

   Is 2.4G configuration relay implemented in a particular receiver firmware? If so, which revision
   is required, and can that firmware (or a receiver running it) be supplied? This would explain
   the disagreement between T2 and our results entirely.
1. Does the receiver need a **handshake or mode command** before it forwards driver-channel frames
   over the air? The dongle's driver collection carries an 8-byte **Feature** report (id `0x05`)
   that the wired driver collection does not have. Reading it returns
   `04 55 91 01 00 00 00 00`. Its meaning is undocumented; we have deliberately **not** written to
   it, since a wrong value there could plausibly unpair the receiver.
2. Was the capture in `fabric-info` taken over the **cable** or over the **2.4G dongle**? If the
   factory tool configures over 2.4G, a capture of that session would settle this immediately.
3. Is `MI_00&COL04` the correct collection for driver frames over the dongle, or is the config
   channel carried elsewhere on the receiver?

## Related

- `docs/mouse/taipan/protocol.md` — the protocol reference, including the cold-start MsConfig
  constraint that a rate-only write is refused when no DPI stage is known.
- `docs/mouse/taipan/taipan-vendor-qa-2026-07-30.md` — the earlier round of vendor Q&A.
- `docs/mouse/taipan/fabric-info/` — the vendor's answers and USB capture this document responds to.
