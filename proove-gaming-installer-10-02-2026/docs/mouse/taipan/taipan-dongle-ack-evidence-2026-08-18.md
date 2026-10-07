# TAIPAN — the mouse ACKs every config frame on the cable and none over the dongle (2026-08-18)

> ## ⚠️ Superseded 2026-09-02 — the vendor was right
>
> This document is a dated record of a real measurement, and its numbers are accurate: on that day,
> with the framing we were using, the dongle answered every config frame with silence. The
> *conclusion* drawn from it — that the dongle does not relay command `0x03` — was wrong.
>
> Their reply quoted below ("the data sent is exactly the same as with a wired connection") was
> correct. What differed was not the data but the FRAMING AROUND it: their driver enters
> configuration mode (`0x46` Para1 `0x00`) before writing, commits in one frame rather than two, and
> applies with `0x40 F3 <mask>` afterwards. We sent none of those. The cable accepted our writes
> anyway; the radio link did not.
>
> With the handshake added, every frame below is acknowledged over the dongle and the remap takes
> effect (hardware-confirmed 2026-09-02). See the configuration-mode session in `protocol.md`.
>
> Kept unedited below, because what was measured and what was concluded from it are different
> things, and the difference is the lesson.

Measured evidence for the vendor's reply of 2026-08-18:

> "the data sent is exactly the same as with a wired connection. If there is no response, it means
> your proxy code is not connected correctly."

Both runs come from `taipan response` in `ProoveGaming.Device.Testing`
(`src/ProoveGaming.Device.Testing/TaipanConfigResponseProbe.cs`), which replays the button-map write
frame for frame exactly as `TelinkMouseService.SetButtonBindingsAsync` sends it and drains the input
pipe after every frame.

## Why this was not measurable before

`TelinkMouseService.WriteFrameAsync` is write-only:

```csharp
private static async Task WriteFrameAsync(HidStream stream, byte[] frame)
{
    stream.Write(frame);
    await Task.Delay(PacketDelayMs);
}
```

No read. Every configuration write the app has ever issued — **over the cable as well as the
dongle** — discarded whatever came back. Our earlier "no response" was never a measurement; the
question had never been asked. That is why the vendor's sentence could not be answered until now.

## Result

| | Wired (PID 0xFB01) | Dongle (PID 0x8373) |
|---|---|---|
| Collection | `mi_02&col02` | `mi_00&col04` |
| Battery `0x48` round-trip | reply, raw=966 (91 %), usb=True | reply, raw=958 (88 %), usb=False |
| Button-map `0x03`, 8 frames | **8 replies — one per frame** | **0 replies — total silence** |
| Remap took effect | **yes** | **no** |

The restore sequence behaves identically: 8 more ACKs on the cable, 8 more silences on the dongle.

## The wired ACK is a deliberate protocol ACK

Each reply is the frame just sent, echoed back with **`driverPara4` (the sequencing byte) set to
`0x00`** and **the checksum recomputed** for the modified header. Verified for all frame kinds:

| Frame | Sent | Reply | Reply checksum predicted with p4=0 |
|---|---|---|---|
| HEADER `0xD1` | `05 B6 FA 03 00 00 02 D1 …` | `05 99 FA 03 00 00 02 00 …` | `0x99` ✓ |
| CHUNK 1 `0xD2` | `05 8F FA 03 02 00 18 D2 …` | `05 71 FA 03 02 00 18 00 …` | `0x71` ✓ |
| CHUNK 5 `0xD2` | `05 53 FA 03 62 00 04 D2 …` | `05 05 FA 03 62 00 04 00 …` | `0x05` ✓ |
| VALIDBANK `0xD2` | `05 29 FA 03 7F 00 01 D2 1A …` | `05 1B FA 03 7F 00 01 00 1A …` | `0x1B` ✓ |
| COMMIT `0xD3` | `05 B6 FA 03 00 00 00 D3 …` | `05 9B FA 03 00 00 00 00 …` | `0x9B` ✓ |

Checksum rule confirmed as `(0xFA + cmd + p1 + p2 + p3 + p4) & 0xFF ^ 0x66` — the same rule the
outbound frames use. The payload is echoed byte-for-byte alongside it.

This matters: the ACK is not incidental traffic. The firmware recomputes a valid checksum for a
frame it constructs itself, which means acknowledging each config frame is intended behaviour.

## What this rules out

- **Not a dead link.** The battery request `0x48` round-trips on the dongle in the same probe run,
  on the same open stream, moments before the silent write.
- **Not a missing read.** The read path is demonstrably working on that exact collection — it just
  delivered the battery reply. The same `Drain()` call returns nothing for all 16 config frames.
- **Not a transport that refuses host→mouse traffic.** Established 2026-08-07 and re-confirmed here.

So the silence is specific to command `0x03` over the radio link, not to our proxy code's
connection. The dongle relays the query commands and does not relay (or does not answer) the
sequenced flash-write command.

## Still open

Whether `mi_00&col04` is the *right* dongle endpoint for configuration. It is the only collection we
have used, chosen by a 33/33 in/out report-length filter, and it does answer `0x48`. The vendor's
phrase "not connected correctly" may mean we should be addressing a different collection.

`taipan response sweep` maps this: it sends the read-only battery query `0x48` and the
no-flash command `0x46` (`CMD_SetMouseState`, `Para1 = 0x01`) to every collection on the dongle and
reports which answer. **Run before sending this document**, and fold the result in:

- Only our collection answers → no other endpoint exists to try; the finding above stands as stated.
- Another collection answers `0x46` → that is the candidate config channel and must be retested
  first.

## Question for the vendor

Given the cable ACKs every frame of command `0x03` and the 2.4G link ACKs none of them — while the
same link ACKs `0x48` — which endpoint or sequencing does the receiver require for the button-map
write, and does the receiver relay `0x03` at all?

## Reproducing

```
dotnet run --project src/ProoveGaming.Device.Testing -- taipan response wired
dotnet run --project src/ProoveGaming.Device.Testing -- taipan response dongle
dotnet run --project src/ProoveGaming.Device.Testing -- taipan response sweep
```

Stop the `ProoveGamingDeviceHost` service first or it holds the interface. The dongle arm aborts if
the mouse reports `usb=True`, so a cable left plugged in cannot be mistaken for a radio-link result.

## Related

- `taipan-dongle-config-findings-2026-08-04.md` — the original "config never applies over the
  dongle" note, whose premise was corrected on 2026-08-07.
- `taipan-vendor-qa-2026-07-30.md`, `taipan-vendor-request-2026-08-05.md` — earlier exchanges.
- `protocol.md` — frame format and the checksum rule verified above.
