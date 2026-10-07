# OEM VS750 Protocol — recovered from the shipped executable

Source: `VS750 Gaming Mouse.exe` (HXCGAMINGMOUSE install), a MinGW/Qt4 build whose
C++ symbol names and INI config files are in cleartext. This is the vendor's own
driver for the X43/BLAZE (VID 04D9 / PID A245). Extracted for interoperability —
we are the brand building the companion software for this device.

## Device addressing (from apConfig.bin device [1])

```
vid=04D9  pid=A245
usagePage=ff00  usage=ff00            (primary command collection)
usagePage2=ff03 usage2=00f0           (secondary collection)
reportLength=64
isEncode=false  sCode=000000          (no payload encryption)
keyCount=5                            (only 5 assignable keys)
icType=571  sensorType=3327
```

## Command map (from exe symbol table)

Two families by MCU class — `hid553_*` and `hid560_*`. Our unit (mcu_type 0x03 =
HT571) is driven by the **hid553** family:

| Cmd | Function | Args |
|-----|----------|------|
| 0x01 | SetReportRate | uchar |
| 0x02 | SetParameter_0 / SetCurrentKeyMatrix | uchar* (64), profileNo |
| 0x03 | SetKeyboardoption / SetParameter_1 | uchar* (192), profileNo |
| 0x04 | SetGunData | 4×uchar |
| 0x0B | SetCurrentDPI | uchar |
| 0x0C | **SetFixLEDColor** | uchar* (colour data block) |
| 0x0D | **SetRGBLedEffect** | effect, option1, option2 |
| 0x10 | **SetLED8StageColor** | stage, uchar* |
| 0x11 | SetDPISet | dpiStage, uchar* (8) |
| 0x12 | SetKeyMatrix | uchar*, uchar |
| 0x13 | SetMacroKey | uchar, uchar* |
| 0x80 | **SyncData** (apply/commit) | — |
| 0x81 | GetKeyboardoption | — |
| 0x82 | GetCurrentProfile | — |
| 0x83 | GetKeyboardoption | — |
| 0x84 | GetCurrentDPI | — |
| 0x8B | GetCurrentDPI (alt) | — |
| 0x8C | readProfile | uchar* |

Key insight vs. our earlier work: the LED is driven by a **dedicated runtime
command 0x0D SetRGBLedEffect(effect, option1, option2)** and colour by 0x0C /
0x10 — NOT only by the param_0/param_1 stores (0x02/0x03) we used all along. The
old-protocol 0x0A we tried is not in this firmware's map.

Command header is the 9-byte SetFeature `[00, cmd, p1..p6, CS]`, CS = (sum of
bytes 1..7) & 0xFF XOR 0xAA. Data blocks follow as 65-byte output reports.

## Reusable settings format (from p1.bin … p7.bin, plain INI)

Each profile file:

```
[mouseKeyMatrix]      16 keys, "idx = b0, b1, b2, b3"
                      mouse buttons: 0,0,<code>,0  (240=L 241=R 242=M 244=Fwd 243=Back)
                      keys 5-7:      5,0,<fn>,0
                      keys 14-15:    255,0,<n>,0  (vendor)
[mouseFwKeyMatrix]    alternate/function bindings
[mouse]
  reportRate=0|3      (index into rate table 1000/125/250/500)
  dpiCount, currentDpiCount
  dpiValueArray=      up to 8 DPI values
  lightValue=e, o1, o2   (the 0x0D SetRGBLedEffect args: effect, option1, option2)
  colorValue=        8 gears × RGB (24 values)
  mouseRate=174  doubleRate=174  scrollRate=…   (cursor/double-click/scroll speeds)
```

`lightValue` and `colorValue` map directly onto 0x0D / 0x0C-0x10. `mouseRate=174`
etc. are the Windows pointer-speed values the OEM applies via SystemParametersInfo,
reusable for our Settings section.

## Why our app disabled the LED on DPI change (root cause confirmed)

The OEM has a **dedicated single-purpose DPI command** `0x0B SetCurrentDPI(uchar)`
that changes only the active DPI stage and touches nothing else. The OEM UI does
not even let you pick a DPI stage directly — you configure the value ranges, then
switch stages with the physical DPI button; the app merely *listens* (GetCurrentDPI
0x84 / notification interface) and updates its display.

Our app instead changed the active stage by writing **param_0 (cmd 0x02)**, whose
64-byte block includes `Byte2 = ledEffect`. So every DPI selection rewrote the LED
effect byte — and when that byte was 0x00 (All Off), the DPI changed *and the LED
turned off in the same write*. That is exactly the behaviour observed.

**Fix**: switch `EasenMouseService.SetActiveDpiStageAsync` from a param_0 write to
`0x0B SetCurrentDPI(stageIndex)`. DPI then changes without ever touching LED state,
matching the OEM design. (This does not revive the already-damaged unit, but it
prevents the failure mode entirely on healthy hardware.)
