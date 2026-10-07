# PG Taipan — button remapping over the 2.4G dongle: solved, plus five open questions

**Date:** 2026-09-01
**Updated:** 2026-09-02 with Bus Hound formatting (section 2b) and our findings from your
capture of the same date (section 5).
**Follows:** your reply of 2026-08-18 — *"the data sent is exactly the same as with a wired
connection. If there is no response, it means your proxy code is not connected correctly."*

## SOLVED — thank you for the capture

**Your Bus Hound capture of 2026-09-02 answered the question.** Configuration writes over the 2.4G
dongle now work on our side, and the problem was ours, not the dongle's.

You were right that "the data sent is exactly the same as with a wired connection". What differed
was not the data but the **framing around it**. Your driver does three things ours never did:

1. **Enters configuration mode first** — `0x46` with Para1 = `0x00`, repeated until acknowledged
   (your capture needed five attempts; ours is answered on the second).
2. **Commits in ONE frame** — ValidBankID at offset `0x007F` carrying `driverPara4 = 0xD3`. We were
   sending two: a `0xD2` ValidBankID followed by an empty `0xD3`. That is why we had never seen the
   `driverPara4 = 0xEE` reply anywhere in our logs.
3. **Applies afterwards** — `0x40` with Para1 = `0xF3` and Para2 = a per-table bitmask.
   And then **exits configuration mode** with `0x46` Para1 = `0x01`.

Confirmed on our hardware the same day, cable unplugged, mouse live on the dongle: **every frame of
a button-table write is acknowledged, the commit answers `0xEE`, and the remap takes effect.** The
cable had always accepted our writes without the handshake, which is exactly why this took so long
to find — the transport that worked was hiding the fault in the transport that did not.

We have implemented the full sequence and are no longer treating the dongle as configuration-
incapable. **No action needed from you on this point** — it is recorded here so your side has the
same picture.

What follows is the original document: the exact bytes we send, kept because you asked for them,
plus five remaining questions in section 5 that your capture raised and we cannot answer alone.

---

## The original report (what we sent before the fix)

The data below is a real capture, not a reconstruction. It is the state of things **before** the
handshake was added: the same 8 frames on both transports, acknowledged on the cable and silent
over the dongle. It is left unedited so the two behaviours can be compared.

---

## 1. The operation

| | |
| --- | --- |
| Command | `0x03` (`CMD_RW_Button`) |
| Button | Button 2 (right click), table offset `0x0006` |
| From | `82 00 00 00` — factory right click |
| To | `8F 08 08 00` — function `0x8F` (general key), modifier `0x08` (Left GUI), key `0x08` (E) = **Win+E, "My PC"** |
| Frames | 8 (header → 5 chunks → ValidBankID → commit) |
| Report | 33 bytes, report id `0x05` |

The whole 102-byte button table is re-sent for a one-button change, because the first (`0xD1`)
packet erases the flash sector — so every retained byte, including the wheel pair and Buttons 6/7,
has to be written again.

---

## 2. The exact bytes on the wire

Checksum (byte 1) = `(0xFA + cmd + p1 + p2 + p3 + p4) & 0xFF ^ 0x66`.
Header is `[reportId, checksum, driverID, cmd, p1, p2, p3, p4]`, payload follows in bytes 8–32.

```c
/* ------------------------------------------------------------------------
 * PG Taipan — remap Button 2 (right click) to "My PC" (Win+E)
 *
 * These 8 frames are sent in this order, 12 ms apart, as raw 33-byte HID
 * output reports. Byte-for-byte identical on the cable and over the 2.4G
 * dongle; only the target collection differs (see section 3).
 * ------------------------------------------------------------------------ */

/* Frame 1 — HEADER, driverPara4 = 0xD1.
 * 0xD1 ERASES THE FLASH SECTOR, so it must come first and the whole table
 * must follow. Payload is the 2-byte table header: max button-event count
 * 0x0A, high byte 0x00.
 * Cable:  ACK received.   Dongle: no reply. */
05 B6 FA 03 00 00 02 D1 0A 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00

/* Frame 2 — CHUNK 1, table offset 0x0002, 24 bytes, driverPara4 = 0xD2.
 * Buttons 1..6. THIS IS THE ONLY FRAME THAT DIFFERS FROM A FACTORY TABLE:
 *   bytes 8..11  = 81 00 00 00  Button 1  Left        (unchanged)
 *   bytes 12..15 = 8F 08 08 00  Button 2  >>> My PC <<<  (was 82 00 00 00)
 *   bytes 16..19 = 83 00 00 00  Button 3  Middle      (unchanged)
 *   bytes 20..23 = 85 00 00 00  Button 4  Forward     (unchanged)
 *   bytes 24..27 = 84 00 00 00  Button 5  Back        (unchanged)
 *   bytes 28..31 = 88 00 00 00  Button 6  DPI cycle   (unchanged)
 * Cable:  ACK received.   Dongle: no reply. */
05 8F FA 03 02 00 18 D2 81 00 00 00 8F 08 08 00 83 00 00 00 85 00 00 00 84 00 00 00 88 00 00 00 00

/* Frame 3 — CHUNK 2, table offset 0x001A, 24 bytes.
 * Button 7 (poll-rate cycle, BtModify 0x01) then unused slots.
 * Cable:  ACK received.   Dongle: no reply. */
05 67 FA 03 1A 00 18 D2 97 01 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00

/* Frame 4 — CHUNK 3, table offset 0x0032, 24 bytes. Unused slots.
 * Cable:  ACK received.   Dongle: no reply. */
05 7F FA 03 32 00 18 D2 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00

/* Frame 5 — CHUNK 4, table offset 0x004A, 24 bytes.
 * Contains the wheel pair: 8C = wheel down at slot 23, 8B = wheel up at
 * slot 24. Re-sent unchanged; the sector erase means they cannot be omitted.
 * Cable:  ACK received.   Dongle: no reply. */
05 57 FA 03 4A 00 18 D2 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 8C 00 00 00 8B 00 00 00 00

/* Frame 6 — CHUNK 5, table offset 0x0062, 4 bytes. Slot 25, unused.
 * Cable:  ACK received.   Dongle: no reply. */
05 53 FA 03 62 00 04 D2 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00

/* Frame 7 — VALIDBANK: writes 0x1A to table offset 0x007F.
 * Cable:  ACK received.   Dongle: no reply. */
05 29 FA 03 7F 00 01 D2 1A 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00

/* Frame 8 — COMMIT, driverPara4 = 0xD3, triggers Update_Para().
 * Cable:  ACK received, and the remap is live.
 * Dongle: no reply, and the button still does right click. */
05 B6 FA 03 00 00 00 D3 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00
```

### Reproducing the frames

```c
/* Standalone C — produces exactly the 8 frames above. No dependencies. */
#include <stdint.h>
#include <string.h>

#define FRAME_LEN     33
#define REPORT_ID     0x05
#define DRIVER_ID     0xFA
#define CMD_BUTTON    0x03
#define SEQ_FIRST     0xD1   /* erases the flash sector */
#define SEQ_CONTINUE  0xD2
#define SEQ_COMMIT    0xD3
#define VALID_BANK_ID 0x1A
#define BANK_ID_OFF   0x007F
#define TABLE_LEN     102    /* 2-byte header + 25 buttons x 4 bytes */
#define HEADER_LEN    2
#define CHUNK_BYTES   24     /* entry-aligned: 6 buttons per frame */

static uint8_t checksum(uint8_t cmd, uint8_t p1, uint8_t p2, uint8_t p3, uint8_t p4)
{
    return (uint8_t)((uint8_t)(DRIVER_ID + cmd + p1 + p2 + p3 + p4) ^ 0x66);
}

static void build_frame(uint8_t out[FRAME_LEN], uint8_t cmd,
                        uint8_t p1, uint8_t p2, uint8_t p3, uint8_t p4,
                        const uint8_t *payload, int payload_len)
{
    memset(out, 0, FRAME_LEN);
    out[0] = REPORT_ID;
    out[1] = checksum(cmd, p1, p2, p3, p4);
    out[2] = DRIVER_ID;
    out[3] = cmd;
    out[4] = p1;                       /* table offset, low  */
    out[5] = p2;                       /* table offset, high */
    out[6] = p3;                       /* payload length     */
    out[7] = p4;                       /* driverPara4 = sequencing */
    if (payload_len > 0) memcpy(out + 8, payload, (size_t)payload_len);
}

/* Factory Profile-1 table with Button 2 changed to My PC. */
static void build_button_table(uint8_t t[TABLE_LEN])
{
    memset(t, 0, TABLE_LEN);
    t[0] = 0x0A; t[1] = 0x00;                       /* max button-event count */

    /* Button N entry starts at 2 + (N-1)*4: [BtFuncNum, BtModify, BtAlpha1, BtAlpha2] */
    t[2]  = 0x81;                                   /* B1  left            */
    t[6]  = 0x8F; t[7] = 0x08; t[8] = 0x08;         /* B2  My PC = Win+E   <<< the change */
    t[10] = 0x83;                                   /* B3  middle          */
    t[14] = 0x85;                                   /* B4  forward         */
    t[18] = 0x84;                                   /* B5  back            */
    t[22] = 0x88;                                   /* B6  DPI cycle       */
    t[26] = 0x97; t[27] = 0x01;                     /* B7  poll-rate cycle */
    t[90] = 0x8C;                                   /* B23 wheel down      */
    t[94] = 0x8B;                                   /* B24 wheel up        */
}

/* Emits the 8 frames in order. send() writes one 33-byte HID output report. */
void write_button_map(void (*send)(const uint8_t *frame, int len))
{
    uint8_t table[TABLE_LEN], frame[FRAME_LEN], bank = VALID_BANK_ID;
    int written, len, offset;

    build_button_table(table);

    /* 1) header — 0xD1 erases the sector */
    build_frame(frame, CMD_BUTTON, 0x00, 0x00, HEADER_LEN, SEQ_FIRST, table, HEADER_LEN);
    send(frame, FRAME_LEN);

    /* 2) the 100 entry bytes, entry-aligned, 24 at a time */
    for (written = 0; written < TABLE_LEN - HEADER_LEN; written += CHUNK_BYTES) {
        len = TABLE_LEN - HEADER_LEN - written;
        if (len > CHUNK_BYTES) len = CHUNK_BYTES;
        offset = HEADER_LEN + written;
        build_frame(frame, CMD_BUTTON,
                    (uint8_t)(offset & 0xFF), (uint8_t)(offset >> 8),
                    (uint8_t)len, SEQ_CONTINUE, table + offset, len);
        send(frame, FRAME_LEN);
    }

    /* 3) ValidBankID at 0x007F */
    build_frame(frame, CMD_BUTTON,
                (uint8_t)(BANK_ID_OFF & 0xFF), (uint8_t)(BANK_ID_OFF >> 8),
                0x01, SEQ_CONTINUE, &bank, 1);
    send(frame, FRAME_LEN);

    /* 4) commit — Update_Para() */
    build_frame(frame, CMD_BUTTON, 0x00, 0x00, 0x00, SEQ_COMMIT, 0, 0);
    send(frame, FRAME_LEN);
}
```

---

## 2b. The same frames in Bus Hound format

Per your request of 2026-09-02. Same 8 frames as section 2, same operation (right click -> My PC),
rendered in the format of the capture you sent so the two can be diffed line by line.

Note that our **HEADER frame is byte-identical to yours** (`05 b6 fa 03 00 00 02 d1 0a 00 ...`, reply
`05 99 fa 03 00 00 02 00 ...`), as are the chunk-3 and chunk-4 headers. Only the payloads differ,
because we are writing a different button map.

### Cable (VID 0x248A / PID 0xFB01) - every frame acknowledged, remap takes effect

```
Device  Address              Phase  Data                                                                                                    Description                       Delta  Cmd.Phase.Ofs(rep)  Date        Time        
------  -------------------  -----  ------------------------------------------------------------------------------------------------------  --------------------------------  -----  ------------------  ----------  ------------
 122                         OUT    05 b6 fa 03  00 00 02 d1  0a 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  ................................  12ms           1.1.0        2026/09/02  17:03:22.617  
                                    00                                                                                                      .                                                1.1.32                                      
 122                         IN     05 99 fa 03  00 00 02 00  0a 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  ................................  15ms           2.1.0        2026/09/02  17:03:22.632  
                                    00                                                                                                      .                                                2.1.32                                      
 122                         OUT    05 8f fa 03  02 00 18 d2  81 00 00 00  8f 08 08 00  83 00 00 00  85 00 00 00  84 00 00 00  88 00 00 00  ................................  12ms           3.1.0        2026/09/02  17:03:22.644  
                                    00                                                                                                      .                                                3.1.32                                      
 122                         IN     05 71 fa 03  02 00 18 00  81 00 00 00  8f 08 08 00  83 00 00 00  85 00 00 00  84 00 00 00  88 00 00 00  .q..............................  15ms           4.1.0        2026/09/02  17:03:22.659  
                                    00                                                                                                      .                                                4.1.32                                      
 122                         OUT    05 67 fa 03  1a 00 18 d2  97 01 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  .g..............................  12ms           5.1.0        2026/09/02  17:03:22.671  
                                    00                                                                                                      .                                                5.1.32                                      
 122                         IN     05 49 fa 03  1a 00 18 00  97 01 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  .I..............................  15ms           6.1.0        2026/09/02  17:03:22.686  
                                    00                                                                                                      .                                                6.1.32                                      
 122                         OUT    05 7f fa 03  32 00 18 d2  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  ....2...........................  12ms           7.1.0        2026/09/02  17:03:22.698  
                                    00                                                                                                      .                                                7.1.32                                      
 122                         IN     05 21 fa 03  32 00 18 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  .!..2...........................  15ms           8.1.0        2026/09/02  17:03:22.713  
                                    00                                                                                                      .                                                8.1.32                                      
 122                         OUT    05 57 fa 03  4a 00 18 d2  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  8c 00 00 00  8b 00 00 00  .W..J...........................  12ms           9.1.0        2026/09/02  17:03:22.725  
                                    00                                                                                                      .                                                9.1.32                                      
 122                         IN     05 39 fa 03  4a 00 18 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  8c 00 00 00  8b 00 00 00  .9..J...........................  15ms          10.1.0        2026/09/02  17:03:22.740  
                                    00                                                                                                      .                                               10.1.32                                      
 122                         OUT    05 53 fa 03  62 00 04 d2  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  .S..b...........................  12ms          11.1.0        2026/09/02  17:03:22.752  
                                    00                                                                                                      .                                               11.1.32                                      
 122                         IN     05 05 fa 03  62 00 04 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  ....b...........................  15ms          12.1.0        2026/09/02  17:03:22.767  
                                    00                                                                                                      .                                               12.1.32                                      
 122                         OUT    05 29 fa 03  7f 00 01 d2  1a 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  .)..............................  12ms          13.1.0        2026/09/02  17:03:22.779  
                                    00                                                                                                      .                                               13.1.32                                      
 122                         IN     05 1b fa 03  7f 00 01 00  1a 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  ................................  15ms          14.1.0        2026/09/02  17:03:22.794  
                                    00                                                                                                      .                                               14.1.32                                      
 122                         OUT    05 b6 fa 03  00 00 00 d3  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  ................................  12ms          15.1.0        2026/09/02  17:03:22.806  
                                    00                                                                                                      .                                               15.1.32                                      
 122                         IN     05 9b fa 03  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  ................................  15ms          16.1.0        2026/09/02  17:03:22.821  
                                    00                                                                                                      .                                               16.1.32
```

### 2.4G dongle (VID 0x248A / PID 0x8373) - the same 8 frames, no reply to any of them

```
Device  Address              Phase  Data                                                                                                    Description                       Delta  Cmd.Phase.Ofs(rep)  Date        Time        
------  -------------------  -----  ------------------------------------------------------------------------------------------------------  --------------------------------  -----  ------------------  ----------  ------------
 122                         OUT    05 b6 fa 03  00 00 02 d1  0a 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  ................................  12ms           1.1.0        2026/09/02  17:11:04.114  
                                    00                                                                                                      .                                                1.1.32                                      
 122                         OUT    05 8f fa 03  02 00 18 d2  81 00 00 00  8f 08 08 00  83 00 00 00  85 00 00 00  84 00 00 00  88 00 00 00  ................................  12ms           2.1.0        2026/09/02  17:11:04.126  
                                    00                                                                                                      .                                                2.1.32                                      
 122                         OUT    05 67 fa 03  1a 00 18 d2  97 01 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  .g..............................  12ms           3.1.0        2026/09/02  17:11:04.138  
                                    00                                                                                                      .                                                3.1.32                                      
 122                         OUT    05 7f fa 03  32 00 18 d2  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  ....2...........................  12ms           4.1.0        2026/09/02  17:11:04.150  
                                    00                                                                                                      .                                                4.1.32                                      
 122                         OUT    05 57 fa 03  4a 00 18 d2  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  8c 00 00 00  8b 00 00 00  .W..J...........................  12ms           5.1.0        2026/09/02  17:11:04.162  
                                    00                                                                                                      .                                                5.1.32                                      
 122                         OUT    05 53 fa 03  62 00 04 d2  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  .S..b...........................  12ms           6.1.0        2026/09/02  17:11:04.174  
                                    00                                                                                                      .                                                6.1.32                                      
 122                         OUT    05 29 fa 03  7f 00 01 d2  1a 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  .)..............................  12ms           7.1.0        2026/09/02  17:11:04.186  
                                    00                                                                                                      .                                                7.1.32                                      
 122                         OUT    05 b6 fa 03  00 00 00 d3  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  00 00 00 00  ................................  12ms           8.1.0        2026/09/02  17:11:04.198  
                                    00                                                                                                      .                                                8.1.32
```

The dongle listing has no IN rows. That is the whole report: not a truncation, not a filter - the
mouse returns nothing at all for these 8 frames, while the `0x48` battery query on that same open
stream answers normally.

---

## 3. Cable vs dongle — the only difference on our side

| | Cable | 2.4G dongle |
| --- | --- | --- |
| USB VID / PID | `0x248A` / `0xFB01` (the mouse) | `0x248A` / `0x8373` (the dongle) |
| HID collection written | `mi_02&col02` | `mi_00&col04` |
| Frames sent | the 8 above | **the same 8, byte for byte** |
| Replies received | **8 — one per frame** | **0 — total silence** |
| Remap takes effect | **Yes** | **No** |

Same code path, same frame builder, same 12 ms spacing. We select the collection and nothing else.

**Resolved 2026-09-02:** with the configuration-mode handshake from your capture in place, the
dongle column now reads the same as the cable column — 8 frames, 8 replies, remap applied.

### The wired reply is a deliberate protocol ACK

Each cable reply is the frame we sent, echoed back with `driverPara4` set to `0x00` and **the
checksum recomputed** for that modified header:

| Frame | Sent | Reply | Reply checksum recomputed with p4 = 0 |
| --- | --- | --- | --- |
| HEADER `0xD1` | `05 B6 FA 03 00 00 02 D1 …` | `05 99 FA 03 00 00 02 00 …` | `0x99` ✓ |
| CHUNK 1 `0xD2` | `05 8F FA 03 02 00 18 D2 …` | `05 71 FA 03 02 00 18 00 …` | `0x71` ✓ |
| CHUNK 5 `0xD2` | `05 53 FA 03 62 00 04 D2 …` | `05 05 FA 03 62 00 04 00 …` | `0x05` ✓ |
| VALIDBANK `0xD2` | `05 29 FA 03 7F 00 01 D2 1A …` | `05 1B FA 03 7F 00 01 00 1A …` | `0x1B` ✓ |
| COMMIT `0xD3` | `05 B6 FA 03 00 00 00 D3 …` | `05 9B FA 03 00 00 00 00 …` | `0x9B` ✓ |

The firmware builds a valid checksum for a frame it constructs itself, so acknowledging each
config frame looks like intended behaviour rather than incidental traffic.

### What this rules out

- **Not a dead link.** In the same probe run, on the same open dongle stream, moments before the
  silent write, the battery request `0x48` round-trips and returns a valid reading.
- **Not a missing read on our side.** The read path is demonstrably working on that exact
  collection — it just delivered the battery reply. The identical drain returns nothing for all 8
  config frames.
- **Not a transport that refuses host→mouse traffic in general**, for the same reason.

The silence is specific to the sequenced flash-write command `0x03` over the radio link.

---

## 4. What our application does NOT send

Listed so you can rule these out as causes, and so you know what we are not currently relying on.

| Not used | Why |
| --- | --- |
| `0x43` (read button map) | Returns nothing on either transport, so we never read the table back and cannot verify a write except by pressing the button. |
| `0x0C` / `0x4C` (per-DPI-stage colour) | You confirmed (T1) it is reserved and has no effect in shipped firmware. We treat it as display-only. |
| Any profile switch before writing | We always write Profile 1. We never send a profile-select command before the sequence above. |
| Any command between the 8 frames | The sequence is issued as one uninterrupted transaction; other writers on the interface (for example the battery poll) are locked out for its duration. |
| Retry / re-send of an unacknowledged frame | We send each frame exactly once. In production we do not read replies at all — the ACKs above were captured with a dedicated probe. |
| `0x00 / 0xF3 / 0x80` (factory reset) | Not used; scope and commit sequence still unconfirmed (T10). |

---

## 5. What we found in your capture of 2026-09-02

Your capture answers several questions we had open, and shows four places where our writer differs
from your driver. We list them so you can tell us which of them matter.

### 5.1 DPI encoding - we believe our formula is wrong by one step

Your DPI table write (`cmd 0x02`) contains six enabled stages:

| Table offset | Payload | Register (byte 1) | `(reg + 1) x 50` |
| --- | --- | --- | --- |
| `0x01` | `01 0f 00 0f 00` | 15 | **800** |
| `0x06` | `01 1f 00 1f 00` | 31 | **1600** |
| `0x0b` | `01 3f 00 3f 00` | 63 | **3200** |
| `0x10` | `01 5f 00 5f 00` | 95 | **4800** |
| `0x15` | `01 7f 00 7f 00` | 127 | **6400** |
| `0x1a` | `01 7b 01 7b 01` | 123, second byte `01` | see 6.2 |

Five consecutive stages land on exact round DPI values under `DPI = (register + 1) x 50`, and on
nothing round under any other rule we tried. Our application currently writes `register = DPI / 50`,
with no `-1`. If your formula is the correct one, then **every DPI we have ever written is one step
(50 DPI) too high** - a user asking for 800 gets 850. We would rather correct this than keep it.

**Please confirm the exact formula**, including whether register `0x00` is a valid stage.

### 5.2 DPI byte layout - your capture appears to settle our open question T7

We have been unable to reconcile the xlsx (per-axis "double" flag) with the address-table PDF
(plain `X_L / X_H / Y_L / Y_H`). Stage 6 above decides it: read as a 16-bit little-endian value,
`7b 01` is 379, i.e. **18,950 DPI**, which is not a plausible stage. Read as *value 0x7b + double
flag 0x01*, it is a sane top stage.

So we now believe the layout is `[enable, X_value, X_double, Y_value, Y_double]`, and we intend to
change our encoder to match. **Please confirm**, and tell us what "double" multiplies - the raw
register, or the final DPI.

This also sets the maximum: with one value byte the ceiling is `(255 + 1) x 50 = 12,800` DPI, or
25,600 with the double flag set. Please confirm the maximum DPI the sensor actually supports.

### 5.3 Commit sequence - you send one frame where we send two

| | Frames | Reply |
| --- | --- | --- |
| Your driver | **one**: `cmd, p1=0x7F, p2=0x00, p3=0x01, p4=0xD3, payload 1A` | `driverPara4 = 0xEE` |
| Our app | **two**: ValidBankID with `p4=0xD2`, then an empty frame with `p4=0xD3` | `driverPara4 = 0x00` |

You use this identical single-frame form for all four tables in the capture - `0x01`, `0x03`, `0x02`
and `0x04` - and every one is answered with `driverPara4 = 0xEE`. We have never seen a `0xEE` reply,
because we never send that frame form.

**Is `0xEE` the "committed" acknowledgement, and is our two-frame form a valid alternative or merely
tolerated on the cable?** If the combined frame is the only properly supported commit, that could
explain why our writes behave differently from yours over the radio link.

### 5.4 A five-frame sequence after the tables that we do not send at all

After committing every table, your driver sends five more frames, each answered with
`driverPara4 = 0xEE`:

```
OUT  05 57 fa 40  f3 04 00 00 ...      IN  05 79 fa 40  f3 04 00 ee ...
OUT  05 48 fa 40  f3 01 00 00 ...      IN  05 7a fa 40  f3 01 00 ee ...
OUT  05 49 fa 40  f3 02 00 00 ...      IN  05 7b fa 40  f3 02 00 ee ...
OUT  05 53 fa 40  f3 08 00 00 ...      IN  05 45 fa 40  f3 08 00 ee ...
OUT  05 5b fa 40  f3 10 00 00 ...      IN  05 4d fa 40  f3 10 00 ee ...
```

`p2` takes the values `0x04, 0x01, 0x02, 0x08, 0x10`, which look like a per-table bitmask (button,
config, DPI, LED, macro). **Our application sends none of these.**

**What does `cmd 0x40` with `p1 = 0xF3` do?** If it is what makes a committed table take effect,
that would be the single most useful thing we could learn - and it is a strong candidate for why our
writes do not apply over the 2.4G dongle.

### 5.5 Factory button map - four slots differ from ours

| Slot | Your capture | Ours |
| --- | --- | --- |
| 4 | `0x84` | `0x85` |
| 5 | `0x85` | `0x84` |
| 6 | `0x00` (none) | `0x88` (DPI cycle) |
| 7 | `0x88` (DPI cycle) | `0x97` + BtModify `0x01` (polling-rate cycle) |
| 25 | `ff ff ff ff` | `00 00 00 00` |

Slots 23 and 24 match exactly (`0x8C` wheel down, `0x8B` wheel up), which confirms the wheel order
we had to establish by hand.

We rebuilt our default table from the documentation rather than from a capture, so we would like to
know which is correct - in particular whether **Buttons 4 and 5 are Back and Forward in that order**,
and whether slot 25 should be `ff ff ff ff` rather than zeros. Note that because the `0xD1` packet
erases the sector, we re-send this whole table on every button change, so an error here affects
every write we make.

---

## 6. What we are asking

The dongle question is closed. What remains are the five items in section 5, in priority order:

1. **The DPI formula** (5.1). We believe it is `DPI = (register + 1) x 50`, which would mean every
   DPI we have written so far is one step too high. This affects shipped users, so it is the most
   urgent of the five.
2. **The DPI byte layout** (5.2) — `[enable, X_value, X_double, Y_value, Y_double]`, and what
   "double" multiplies. Plus the maximum DPI the sensor supports.
3. **`cmd 0x40` with Para1 `0xF3`** (5.4) — please confirm our reading that Para2 is a per-table
   bitmask (`0x01` config, `0x02` DPI, `0x04` button, `0x08` LED, `0x10` macro), and whether the
   apply is required or merely advisable once a table is committed.
4. **The factory button map** (5.5) — in particular whether Buttons 4 and 5 are Back and Forward in
   that order, and whether slot 25 should be `ff ff ff ff` rather than zeros.
5. **Is the `0xD1` sector erase safe over the radio link?** Now that we do write over the dongle,
   this matters more than it did: if a sequenced flash write can be interrupted mid-transaction by
   the radio, we would like to know what the firmware does with a half-written table. On our other
   device a partial table write left the buttons unusable.

   > **ANSWERED 2026-09-02** — original: *"理论上是可能出现这个问题，但是我们现在做的数据发送不完全，
   > 没有遇到过不公过的现象。"*
   >
   > English: *"Theoretically this problem can occur, but the data sending we currently do is not
   > complete, and we have not encountered any [failure] cases."* (`不公过` is not a word — a typo,
   > most likely for `不通过` "not passing" or `不成功` "unsuccessful"; either way the sense is "we
   > have never seen it fail".)
   >
   > **What this settles:** the vendor confirms an interrupted sequenced flash write over the radio
   > **is possible**. That is the first acknowledgement of the risk.
   >
   > **What it does not settle:** the actual question — what the firmware *does* with a half-written
   > table. No rollback, validity check or recovery path is described, so the failure mode stands as
   > possible and **unmitigated**. "We have not encountered it" also carries limited weight here:
   > their own Bus Hound capture is a WIRED session, and a wired write never traverses the radio, so
   > cable-dominated testing would not exercise the case being reassured about.
   >
   > **Middle clause unresolved.** "我们现在做的数据发送不完全" could mean either that the data sets
   > they send are incomplete, or that they avoid full-sector operations. Worth asking them to
   > restate — if they deliberately do something different from a full table write over the radio,
   > that is a material difference from what we do.
   >
   > **Why it matters here:** on 2026-09-02 our unit's MsConfig sector read back as all-`FF`
   > (erased) over the dongle — see the configuration-mode session notes in `protocol.md`. That is a
   > candidate instance of exactly the failure the vendor has just called theoretically possible.

One more, for completeness: **is `driverPara4 = 0xEE` the documented "committed" acknowledgement?**
We now rely on it to tell a committed table from a merely echoed frame.

And one we had to solve ourselves, which you may want to correct in your documentation:

**Is `Para4` (the app flag) inside or outside the checksum of an `0x48` battery reply?** Your
documented worked example `05 DA FA 48 68 10 01 01` includes it — 0xDA is the checksum *with*
`Para4 = 1`. Your 2026-09-02 capture does not: frames 68 and 69 are two consecutive battery replies,
`05 49 fa 48 ea 03 00 00` and `05 49 fa 48 ea 03 00 01`, carrying the **same** checksum byte despite
differing in `Para4`. Our own unit agrees with the capture. So the shipped firmware appears to set
the app flag *after* checksumming, and your documentation describes the other behaviour.

This cost us a working battery indicator: our parser followed the documentation, and once the mouse
began setting the app flag it rejected every reading as corrupt. We now accept either form, but a
definitive answer would let us tighten it again.
