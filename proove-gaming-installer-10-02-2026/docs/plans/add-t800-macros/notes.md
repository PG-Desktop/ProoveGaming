# Research notes — RY5088/YC3123 macro format extraction

Raw material for the knowledge-base translation (file-level change #14). Extracted from
`docs/ry-yc3123-机械轴__2025_07_18.doc` with `docs/plans/t800-rgb/extract-doc.py` on 2026-08-06.

## SET_MACRO / GET_MACRO framing (doc §"Set macro" / §"Read macro")

```
Set macro (FEA_CMD_SET_MACRO)
OUT: SET_MACRO(0x0B) | Macro# | Page# | Len | End | 00 | 00 | CS | 56-byte payload
  Macro: 0~63 (64 macros total)
  Page: 256/56 (total 5 pages)
  Len: effective bytes in this page
  End: end flag, = 1, macro end            [结束位，=1，宏结束]

Read macro (FEA_CMD_GET_MACRO)
OUT: GET_MACRO(0x8B) | Macro# | Page# | 00 | 00 | 00 | 00 | CS
IN:  64 bytes
  Page = 256/64 (total 4)                  [reply pages are 64 bytes, write pages 56]
```

## Macro Table memory information (verbatim from the doc, translated)

```
Macro Max 100, each macro has 256 bytes.   [T800/YC3123 hardware spec section says 64 (G0-G63)]
Repeat length: 0~65536 times.

Byte0: Repeat Low byte | Byte1: Repeat High byte     e.g. 01 00

Then per action, 2-byte words:
Byte2      | Byte3       | Byte4          | Byte5
Code0      | Attribute0  | Delay Low byte | Delay High byte

Attribute: bit7:   0 = break, 1 = make (only for kb/mouse button)
           bit0-6: if bit0-bit6 equal 0, next word equals 16-bit delay time.
                   Or max 127 ms delay time.
Code: 0x04-0xEF (kb code), 0xF0-0xF8 (mouse code).

Example 1:
 0x01, 0x00,   // repeat time
 0x04, 0x81,   // press A, delay 1 ms, read next word
 0x04, 0x01,   // release A, delay 1 ms, read next word
 0x00, 0x00,   // end macro

Example 2 (extended delay):
 0x01, 0x00,   // repeat time
 0x04, 0x80,   //
 0x01, 0x00,   // press A, and delay 1 ms (16-bit word), read next word
 0x04, 0x00,   //
 0x01, 0x00,   // release A, delay 1 ms, read next word
 0x00, 0x00    // end macro

Code 0xF9: XY motion. Next word is XY delta, Code=X, Attribute=Y (1~127 or -1~-127).
Code 0xFA: jump macro (Macro A jumps to Macro B; repeat time equals macro A's).

Macro Type (play mode, lives in the KEY-MATRIX binding, not the blob):
  0 = repeat  (plays repeat-count times; any key press stops playback)
  1 = always  (loops forever; any key press or the macro key stops it)
  2 = release (plays on press, stops on release)
```

## Macro key record (from the doc's key-code table)

```
Macro Key | Macro | 09 | Type | Index | 00
```

i.e. key-matrix record `[0x09, playType, slotIndex, 0x00]` — code1 = play type,
code2 = slot. This is the mirror image of Raster/Smart Infinite (code1 = slot there).
Consistent with the record table already decoded in
[`../add-t800-key-assignments/notes.md`](../add-t800-key-assignments/notes.md)
("(spec) macro | `09 <type> <index> 00`").

## Confidence notes

- The key-matrix and multimedia rows of the same table were verified against T800 hardware
  during the key-assignments work (they match the factory matrix byte-for-byte), which is the
  independent confirmation that this table describes the T800's firmware line.
- NOT yet hardware-verified: `End`-flag persistence across replug, `0x8B` reply shape
  (assumed raw 64 bytes, no echoed header, like `GET_KEYMATRIX`), and type-2 while-held loop
  behaviour. These are the plan's Risks.
