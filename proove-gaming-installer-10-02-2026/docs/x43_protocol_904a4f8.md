# Gaming Mouse SPEC

| Date       | Version | Content                  | Author |
|------------|---------|--------------------------|--------|
| 2021/07/20 | 1.0     | New mouse protocol       | Easen  |

---

## Software Specification

### 1. Application Program Function List

- 8 Profiles in Flash.
- 8 Macro Stages (G0~G48), each macro 250 bytes (125 actions) length for all profiles.
- Macro Loop = 1~65535.
- All standard keys can change key map (Assign a key).
- Send macro key or send key on boot protocol with USB key code format.
- All standard keys can be configured to fire key.
- 1/2/4/8 mSec report rate.
- Can be set to a maximum of 8 DPI stages.

### I. Driver Release Package

- Setup Package
- INF Package (WHQL)

---

## USB Protocol

1. Use `SetFeatureReport` command for issuing commands.
2. Use `GetFeatureReport` command for getting results.
3. Use EP5 for data. Report size 64 Bytes.
4. Use EP6 for data. Report 64 Bytes.
5. `SetFeatureReport` checksum mechanism — **checksum is not used**.
   > CS = (Byte0 + Byte1 + …… + Byte6 + Byte7) XOR (10101010B)

6. Vendor Page `0FF01H`, Vendor Usage `022H`.

---

## Commands

### Get Firmware Version (80H) — Read F/W Version

|       | Byte 0 | Byte 1 | Byte 2 | Byte 3   | Byte 4      | Byte 5  | Byte 6 | Byte 7 |
|-------|--------|--------|--------|----------|-------------|---------|--------|--------|
| Setup | 21     | 09     | 00     | 03       | 02          | 00      | 08     | 00     |
| OUT   | 80     | 00     | 00     | 00       | 00          | 00      | 00     | CS     |
| Setup | A1     | 01     | 00     | 03       | 02          | 00      | 08     | 00     |
| IN    | 80     | H-Ver  | L-Ver  | mcu_type | sensor_type | key_sum | 00     | 00     |

**mcu_type:**

| MCU   | Code | Notes (key count) |
|-------|------|-------------------|
| 50343 | 01H  | 9                 |
| 52341 | 02H  | 9                 |
| 571   | 03H  | 7                 |
| 572   | 04H  | 9                 |
| 574   | 05H  | 9                 |
| 553   | 06H  | 7                 |
| 560   | 07H  | 9                 |
| 541   | 08H  | 9                 |
| Others| FFH  |                   |

**sensor_type:**

| Sensor | Code | Notes |
|--------|------|-------|
| 3050   | 01H  |       |
| 3205   | 02H  |       |
| 3325   | 03H  |       |
| 3327   | 04H  |       |
| 3336   | 05H  |       |
| 3337   | 06H  |       |
| 3360   | 07H  |       |
| 3389   | 08H  |       |
| 4090   | 09H  |       |
| OM36   | 21H  |       |
| OM37   | 22H  |       |

---

### Set Current Profile# (01H)

|       | Byte 0 | Byte 1   | Byte 2 | Byte 3 | Byte 4 | Byte 5 | Byte 6 | Byte 7 |
|-------|--------|----------|--------|--------|--------|--------|--------|--------|
| Setup | 21     | 09       | 00     | 03     | 02     | 00     | 08     | 00     |
| OUT   | 01     | Profile# | 00     | 00     | 00     | 00     | 00     | CS     |

**Profile range: 0~5**

---

### Read Current Profile# (81H)

|       | Byte 0 | Byte 1   | Byte 2 | Byte 3 | Byte 4 | Byte 5 | Byte 6 | Byte 7 |
|-------|--------|----------|--------|--------|--------|--------|--------|--------|
| Setup | 21     | 09       | 00     | 03     | 02     | 00     | 08     | 00     |
| OUT   | 81     | 00       | 00     | 00     | 00     | 00     | 00     | CS     |
| Setup | A1     | 01       | 00     | 03     | 02     | 00     | 08     | 00     |
| IN    | 81     | Profile# | 00     | 00     | 00     | 00     | 00     | 00     |

**Profile#:** Current profile.

---

### Set Parameter_0 (02H)

|          | Byte 0 | Byte 1   | Byte 2 | Byte 3 | Byte 4 | Byte 5 | Byte 6 | Byte 7 |
|----------|--------|----------|--------|--------|--------|--------|--------|--------|
| Setup    | 21     | 09       | 00     | 03     | 02     | 00     | 08     | 00     |
| OUT      | 02     | Profile# | 00     | 00     | 00     | 00     | 01     | CS     |
| OUT(EP6) | total 64-byte parameter 0 data |||||||

- **Profile#:** Current profile, range 0~5. (Return value greater than 5 indicates error.)
- **01:** Indicates the number of data transfers required (64 bytes each).

**Data:**

| Byte  | Field           | Description                          |
|-------|-----------------|--------------------------------------|
| Byte0 | reportrate      | Report rate (1ms / 2ms / 4ms / 8ms) |
| Byte1 | dpi_stage       | DPI stage (0~7)                      |
| Byte2 | ledEffect       | LED effect                           |
| Byte3 | ledBrightness   | Brightness                           |
| Byte4 | ledSpeed        | Speed                                |
| Byte5 | ledColor        | Color                                |
| Byte6 | ledOption       | LED light flow direction             |
| Byte7~Byte63 | —      | Reserved                             |

**LED Effect Types:**

| Effect Name     | Type   | Brightness | Speed  | Color          | Option | Notes |
|-----------------|--------|------------|--------|----------------|--------|-------|
| All Off         | 00H    | xx         | xx     | xx             | xx     |       |
| Flowing Light   | 01H    | 00--FF     | 01--07 | 0--7 (7:loop)  | 0&1    |       |
| Always On       | 02H    | 00--FF     | 01--07 | 0--7 (7:loop)  | xx     |       |
| Neon            | 03H    | 00--FF     | 01--07 | 0--7 (7:loop)  | xx     |       |
| Breathing       | 04H    | 00--FF     | 01--07 | 0--7 (7:loop)  | xx     |       |
| Finger Move     | 05H    | 00--FF     | 01--07 | 0--7 (7:loop)  | xx     |       |
| Raindrop        | 06H    | 00--FF     | 01--07 | 0--7 (7:loop)  | xx     |       |
| Marquee         | 07H    | 00--FF     | 01--07 | 0--7 (7:loop)  | 0&1    |       |
| Spot Move       | 08H    | 00--FF     | 01--07 | 0--7 (7:loop)  | xx     |       |
| Collision       | 09H    | 00--FF     | 01--07 | 0--7 (7:loop)  | xx     |       |
| Reserved        | 0a~9FH |            |        |                |        |       |

---

### Read Parameter_0 (82H)

|          | Byte 0 | Byte 1   | Byte 2 | Byte 3 | Byte 4 | Byte 5 | Byte 6 | Byte 7 |
|----------|--------|----------|--------|--------|--------|--------|--------|--------|
| Setup    | 21     | 09       | 00     | 03     | 02     | 00     | 08     | 00     |
| OUT      | 82     |          | 00     | 00     | 00     | 00     | 00     | CS     |
| Setup    | A1     | 01       | 00     | 03     | 02     | 00     | 08     | 00     |
| IN       | 82     | Profile# | 00     | 00     | 00     | 00     | 01     | 00     |
| IN(EP5)  | total 64-byte parameter 0 data |||||||

- **Profile#:** Current profile, range 0~5. (Return value greater than 5 indicates error.)
- **01:** Indicates the number of data transfers required (64 bytes each).

---

### Set Parameter_1 (03H)

|          | Byte 0 | Byte 1   | Byte 2 | Byte 3 | Byte 4 | Byte 5 | Byte 6 | Byte 7 |
|----------|--------|----------|--------|--------|--------|--------|--------|--------|
| Setup    | 21     | 09       | 00     | 03     | 02     | 00     | 08     | 00     |
| OUT      | 03     | Profile# | index  | 00     | 00     | status | times  | CS     |
| OUT(EP6) | total (64*3) bytes parameter 1 data |||||||

- **Profile#:** Current profile, range 0~5. (Return value greater than 5 indicates error.)
- **times:** Indicates the number of data transfers required (64 bytes each). Maximum 3×64 = 192 bytes.
- **index:** Data start storage index (index × 64).
- **Status:**
  - bit0-1: 1st 64-byte block has changes
  - bit0-2: 2nd 64-byte block has changes
  - bit0-3: 3rd 64-byte block has changes

**Data:**

| Byte       | Field           | Description                                               |
|------------|-----------------|-----------------------------------------------------------|
| Byte0      | btn_debounce    | Button debounce time (unit: ms), max 50ms                |
| Byte1      | wheel_debounce  | Scroll wheel debounce time (unit: ms) — currently not used |
| Byte2      | sensor_config   | Sensor-related settings — currently not used             |
| Byte3      | sensor_lift     | Sensor lift-off height (2mm && 3mm)                      |
| Byte4      | dpiStage_Max    | DPI stage count (max 8) — currently not used             |
| Byte5~6    | scale_x         | DPI X direction scaling ratio (scale_x / 100) — currently not used |
| Byte7~8    | scale_y         | DPI Y direction scaling ratio (scale_y / 100) — currently not used |
| Byte9~24   | dpi_x_value[8]  | 8-stage DPI X direction values (16-bit, driver does not need to process) |
| Byte25~40  | dpi_y_value[8]  | 8-stage DPI Y direction values (same as X direction)     |
| Byte41~63  | reserve_var[23] | Reserved 23 bytes                                        |

**dpi_x_value layout:**

| Byte0         | Byte1          | …… | Byte14        | Byte15         |
|---------------|----------------|----|---------------|----------------|
| value[0] low  | value[0] high  | …… | value[7] low  | value[7] high  |

---

| Byte64~87  | dpi RGB Color — DPI stage indicator RGB color data |
|------------|-----------------------------------------------------|

| Byte0        | Byte1        | Byte2        | …… | Byte21       | Byte22       | Byte23       |
|--------------|--------------|--------------|-----|--------------|--------------|--------------|
| color[0][0]  | color[0][1]  | color[0][2]  | …… | color[7][0]  | color[7][1]  | color[7][2]  |

| Byte88~111  | LED_RGB_Color — LED effect RGB color data (same format as DPI RGB Color) |
|-------------|---------------------------------------------------------------------------|

| Byte112~127 | reserve_var[16] — Reserved 16 bytes |
|-------------|--------------------------------------|

---

**Byte128~191: KEY_matrix_table — Key definition data (4 bytes per KEY, 16 keys total)**

| Default | Type                |
|---------|---------------------|
| KEY0    | Mouse Left Button   |
| KEY1    | Mouse Right Button  |
| KEY2    | Mouse Middle Button |
| KEY3    | Mouse B4            |
| KEY4    | Mouse B5            |
| KEY5    | Reserved            |
| KEY6    | Reserved            |
| KEY7    | Reserved            |
| KEY7~15 | Reserved            |

---

### Read Parameter_1 (83H) — Not Used

|          | Byte 0 | Byte 1   | Byte 2 | Byte 3 | Byte 4 | Byte 5 | Byte 6 | Byte 7 |
|----------|--------|----------|--------|--------|--------|--------|--------|--------|
| Setup    | 21     | 09       | 00     | 03     | 02     | 00     | 08     | 00     |
| OUT      | 83     |          | 00     | 00     | 00     | 00     | 00     | CS     |
| Setup    | A1     | 01       | 00     | 03     | 02     | 00     | 08     | 00     |
| IN       | 83     | Profile# | 00     | 00     | 00     | 00     | 04     | 00     |
| IN(EP5)  | total (64*4) bytes parameter 1 data |||||||

- **Profile#:** Current profile, range 0~5. (Return value greater than 5 indicates error.)
- **04:** Indicates the number of data transfers required (64 bytes each).

---

### Set Macro Key (04H)

|          | Byte 0 | Byte 1   | Byte 2 | Byte 3 | Byte 4 | Byte 5 | Byte 6 | Byte 7 |
|----------|--------|----------|--------|--------|--------|--------|--------|--------|
| Setup    | 21     | 09       | 00     | 03     | 02     | 00     | 08     | 00     |
| OUT      | 04     | Profile# | Macro# | 00     | 00     | 00     | 02     | CS     |
| OUT(EP6) | total 128-byte macro key data for each |||||||

**Macro# = 0~47**

---

### Read Macro Key (84H)

|          | Byte 0 | Byte 1   | Byte 2 | Byte 3 | Byte 4 | Byte 5 | Byte 6 | Byte 7 |
|----------|--------|----------|--------|--------|--------|--------|--------|--------|
| Setup    | 21     | 09       | 00     | 03     | 02     | 00     | 08     | 00     |
| OUT      | 84     | 00       | 00     | 00     | 00     | 00     | 00     | CS     |
| Setup    | A1     | 01       | 00     | 03     | 02     | 00     | 08     | 00     |
| IN       | 84     | Profile# | Macro# | 00     | 00     | 00     | 02     | 00     |
| IN(EP5)  | total (64*2) bytes macro key data for each |||||||

- **04:** Indicates the number of data transfers required (64 bytes each).

---

### Set USB String (05H) — Not Used

|          | Byte 0 | Byte 1 | Byte 2 | Byte 3 | Byte 4 | Byte 5 | Byte 6 | Byte 7 |
|----------|--------|--------|--------|--------|--------|--------|--------|--------|
| Setup    | 21     | 09     | 00     | 03     | 02     | 00     | 08     | 00     |
| OUT      | 05     | 00     | 00     | 00     | 00     | 00     | 08     | CS     |
| OUT(EP6) | total (64*8) |||||||

- **08:** Indicates the number of data transfers required (64 bytes each).

> **Note:** Manufacture String is fixed at 100 bytes. `length` indicates the effective length; invalid Unicode is padded with 0. Product String and SerialNum follow the same rule.
> Example: "ABC" → length = 3×2+2. 3 characters × 2, plus length and type fields = +2.

**Manufacture String, 100 bytes, max 49 characters**

| Byte1  | Byte2 | Byte3        | Byte4 | Byte… | Byte98       | Byte99 |
|--------|-------|--------------|-------|-------|--------------|--------|
| Length | Type  | Unicode Char | …     | …     | Unicode Char |        |

- Length = string length × 2 + 2
- Type = fixed 0x03

**Product String, 100 bytes, max 49 characters**

| Byte1  | Byte2 | Byte3        | Byte4 | Byte… | Byte98       | Byte99 |
|--------|-------|--------------|-------|-------|--------------|--------|
| Length | Type  | Unicode Char | …     | …     | Unicode Char |        |

- Length = string length × 2 + 2
- Type = fixed 0x03

**SerialNumber String, 100 bytes, max 49 characters**

| Byte1  | Byte2 | Byte3        | Byte4 | Byte… | Byte98       | Byte99 |
|--------|-------|--------------|-------|-------|--------------|--------|
| Length | Type  | Unicode Char | …     | …     | Unicode Char |        |

- Length = string length × 2 + 2
- Type = fixed 0x03

**Date, 100 bytes, max 49 characters** (same format as SerialNumber String)

---

### Read USB String (85H) — Not Used

|          | Byte 0 | Byte 1 | Byte 2 | Byte 3 | Byte 4 | Byte 5 | Byte 6 | Byte 7 |
|----------|--------|--------|--------|--------|--------|--------|--------|--------|
| Setup    | 21     | 09     | 00     | 03     | 02     | 00     | 08     | 00     |
| OUT      | 85     | 00     | 00     | 00     | 00     | 00     | 00     | CS     |
| Setup    | A1     | 01     | 00     | 03     | 02     | 00     | 08     | 00     |
| IN       | 85     | 00     | 00     | 00     | 00     | 00     | 08     | 00     |
| IN(EP5)  | total (64*8) |||||||

---

## II. Profile Structure Summary

### Key Matrix (Index Table) Structure

There are at most 16 keys in a gaming keyboard or mouse. Every key has a corresponding 4-byte space that stores the key mode and the mapping data to the Host. The profile has an index table that records the function definition for each key. The firmware needs to read out the function definition of each key, and parse it by the corresponding key mode, then send out the right data sequence to the Host.

**Key index table format:**

| Byte3 (4th)          | Byte2 (3rd) | Byte1 (2nd) | Byte0 (1st) |
|----------------------|-------------|-------------|-------------|
| Null (00H)           | --          | --          | --          |
| KeyBoard (01H)       | Code#2      | Code#1      | Code#0      |
| Mouse Button (02H)   | --          | --          | Code        |
| Multi-media key (03H)| --          | Code#1      | Code#0      |
| ACPI Key (04H)       | --          | --          | Code        |
| Macro Key (05H)      | Macro Type  | --          | Macro Index |
| Reserved (06–09)     |             |             |             |
| LED Control (0AH)    | --          | --          | Code#0      |
| DPI settings (0BH)   | Code#2      | Code#1      | Code#0      |
| Fire Key (0CH)       | Fire type   | FireTimes   | Code#0      |
| Reserved (0F–7F)     |             |             |             |
| Special Key (F0H)    | type        | Code#1      | Code#0      |
| Vendor key (F1H)     | Code#2      | Code#1      | Code#0      |

---

### Key Mode Descriptions

**1. Keyboard Key**  
Key mode = 01H. The 1st, 2nd, and 3rd bytes indicate the keyboard key USB output key code (refer to key code table).

**2. Mouse Button**  
Key mode = 02H. The 1st byte indicates the button key USB output key code (refer to button key table).

**3. Multi-media Key**  
Key mode = 03H. The multi-key code is stored in the 1st and 2nd bytes respectively (refer to Multi-key table).

**4. ACPI Key**  
Key mode = 04H. The 1st byte indicates the ACPI key USB output key code (refer to ACPI key table).

**5. Macro Key**  
Key mode = 05H. The 3rd byte represents macro key type. The 2nd byte represents the index of the macro key. There is a table of indices for all macro keys. The firmware will look up the corresponding macro key codes from the macro key definition buffer.

**6. LED Control**  
Key mode = 0AH. Functions defined as follows:

| 3rd Byte | 1st Byte | Function                  |
|----------|----------|---------------------------|
| 00       | 03H      | LED effect cycle switch   |
|          | Reserved |                           |

**7. DPI Switch Key**  
Key mode = 0BH. Functions defined as follows:

| 1st Byte | Function    |
|----------|-------------|
| 01H      | DPI+        |
| 02H      | DPI-        |
| 03H      | DPI+ loop   |

**8. Fire Key**

| Byte3 | Byte2         | Byte1 | Byte0 |
|-------|---------------|-------|-------|
| 0x0C  | Repeat number | Delay | XX    |

- XX must be between KEY_A (0x04) and Mouse Scroll Down (0xF8).
- Delay Time = Delay (1~255) × Time base (10ms)
- Send Press → Delay × Time base → Send Release → Delay × Time base
- Repeat number = 00: repeat stops on release of button key
- Repeat number = xx: repeat stops after counting xx repetitions

**9. Special Switch Key**  
Key mode = F0H. Functions defined as follows:

| 3rd | 2nd        | 1st Byte   | Function            |
|-----|------------|------------|---------------------|
| 00  | --         | --         | Fn                  |
| 01  | --         | 01H        | Profile number +    |
| 01  | --         | 02H        | Profile number -    |
| 01  | --         | 03H        | Profile number Loop |
| 01  | value      | 04H        | Profile number value|
| 02  | --         | 01H        | Report rate +       |
| 02  | --         | 02H        | Report rate -       |
| 02  | --         | 03H        | Report rate loop    |
| 02  | value      | 03H        | Report rate value   |
| 03  | DPIValue_H | DPIValue_L | Snipe               |
| 04  | Delay time | Modify key | TAB Function        |

**TAB Function:**

| Byte1 | Byte2 | Byte3      | Byte4      |
|-------|-------|------------|------------|
| 0xF0  | 04H   | Delay time | Modify key |

- Modify key code = ALT (0xE2) or Win (0xE3).
- Delay time (1~255) = Modify key release delay time (Time base = 10ms).
- Send Modify+Tab → release Tab → delay → release modify key.

**10. Vendor Hot Key**  
Key mode = F1H.

---

## III. Macro Key Data Format

There are 48 macro numbers in total. Each macro is 250 bytes. The first 2 bytes are the macro code repeat counter. Every 2 bytes represent one action. When both bytes are 00, it indicates the end of the macro code, or when the last action is reached.

**Macro code types for Key matrix:**

| Function | Description                                                                  |
|----------|------------------------------------------------------------------------------|
| 0x00     | 1. Normal macro code, ends when repeat counter is reached. 2. Any button press ends the current macro code. |
| 0x01     | 1. Always repeat macro code, ignoring the repeat counter value. 2. Any button press ends the current macro code. |
| 0x02     | 1. Always repeat until button release.                                       |

There are long M × (2 + P×2) bytes buffer for all macro key definitions. Every macro key has max P key actions (P×2 bytes) and two bytes for repeat time. The macro buffer is located after the index buffer in Flash. Every macro key is defined as follows:

a. Ability to define delay in milliseconds between keys' press-and-release.  
b. Example: Key "A" is defined as "Shift+W" 150 millisecond delay "Release W" 100 millisecond delay "Release Shift".  
c. The mapping data is shown below:

**Macro:**

| Byte1           | Byte2            |
|-----------------|------------------|
| Repeat Low byte | Repeat High byte |

Range of repeat values: 1 to 65535.

| Byte3       | Byte4      | Byte5       | Byte6      | …… | Byte2*n     | Byte2*n+1  |
|-------------|------------|-------------|------------|----|-------------|------------|
| Attribute1  | Code No.1  | Attribute2  | Code No.2  | …… | Attribute n | Code No.n  |

**Macro Key Example:**  
The following is a macro key that sends "1 2 3" repeated twice. After Byte 15, all remaining bytes must be padded with 00.

| Byte1 | Byte2 |
|-------|-------|
| 00    | 02    |

| Byte3 | Byte4 | Byte5 | Byte6 | Byte7 | Byte8 | Byte9 | Byte10 | Byte11 | Byte12 |
|-------|-------|-------|-------|-------|-------|-------|--------|--------|--------|
| 01    | 1E    | 81    | 1E    | 01    | 1F    | 81    | 1F     | 01     | 20     |

| Byte13 | Byte14 | Byte15 | Byte16 | …… | …… | Byte2*n | Byte2*n+1 |
|--------|--------|--------|--------|----|-----|---------|-----------|
| 81     | 20     | 00     | 00     | …… | ……  | 00      | 00        |

**Notes:**
1. Key Code No: refer to Key Code in FLASH Table.
2. The content of macro key cannot be multi-media key.
3. Attribute: includes make/break key flag and delay time.
   - Bit 7: 0 = make key; 1 = break key.
   - Bit 0~6: delay time. Range: 0.01 to 1.27 seconds, 10ms resolution. Real delay time = Delay Time × 10ms. If delay = 0, proceed immediately to next action.

**Special key codes:**

| Special Key Code | Function                                         |
|------------------|--------------------------------------------------|
| 0F5H             | Send wheel data, next active for wheel value     |
| 0F6H             | Send wheel data, next active for wheel value     |
| 0F7H             | Send AC Pan, next active for AC Pan value        |
| 0F8H             | Send AC Pan, next active for AC Pan value        |
| 0F9H             | Send 8-bit X data, next active for x value       |
| 0FAH             | Send 8-bit Y data, next active for y value       |

---

## IV. Vendor Code Definition

Vendor code report format total 11 bytes.

To send 11-byte data according to the following data format to inform host when parameter changes via endpoint 2 of interface 1 (REPORT ID = 0x05):

| Byte0   | Byte1          | Byte2      | Byte3     | Byte4     | Byte5    | Byte6    |
|---------|----------------|------------|-----------|-----------|----------|----------|
| ID-0x04 | Profile Number | reportrate | dpi_stage | ledEffect | Reserved | Reserved |

| Byte7    | Byte8    | Byte9 | Byte10 |
|----------|----------|-------|--------|
| Reserved | Reserved | Code  | Type   |

| Type | Code        | Description                                  |
|------|-------------|----------------------------------------------|
| FEH  | 02H         | Set param_0 succeed                          |
| FEH  | 03H         | Set param_1 succeed                          |
| FEH  | 04H         | Set macro succeed                            |
| FEH  | 05H         | Set strings succeed                          |
| 0xFF | 00          | Current profile has been restored to default settings |
| 0xF1 | Vendor code | AP identification                            |

---

## V. Key Code Table

| Key Defined              | Key             | For Macro Key — Key Mode | For Macro Key — Key Code | For Key Matrix — Byte3 | For Key Matrix — Byte2 | For Key Matrix — Byte1 | For Key Matrix — Byte0 |
|--------------------------|-----------------|--------------------------|--------------------------|------------------------|------------------------|------------------------|------------------------|
| No Event                 | Normal key      | 00                       | 00 (macro end)           | 00                     | 00                     | 00                     | 00                     |
| Overrun Error            | Normal key      | 00                       | 01 (delay only)          |                        |                        |                        |                        |
| POST Fail                | Normal key      | 00                       | 02 (delay only)          |                        |                        |                        |                        |
| ErrorUndefined           | Normal key      | 00                       | 03 (delay only)          |                        |                        |                        |                        |
| a A                      | Normal key      | 00                       | 04                       | 00                     | 00                     | 00                     | 04                     |
| b B                      | Normal key      | 00                       | 05                       | 00                     | 00                     | 00                     | 05                     |
| c C                      | Normal key      | 00                       | 06                       | 00                     | 00                     | 00                     | 06                     |
| d D                      | Normal key      | 00                       | 07                       | 00                     | 00                     | 00                     | 07                     |
| e E                      | Normal key      | 00                       | 08                       | 00                     | 00                     | 00                     | 08                     |
| f F                      | Normal key      | 00                       | 09                       | 00                     | 00                     | 00                     | 09                     |
| g G                      | Normal key      | 00                       | 0A                       | 00                     | 00                     | 00                     | 0A                     |
| h H                      | Normal key      | 00                       | 0B                       | 00                     | 00                     | 00                     | 0B                     |
| i I                      | Normal key      | 00                       | 0C                       | 00                     | 00                     | 00                     | 0C                     |
| j J                      | Normal key      | 00                       | 0D                       | 00                     | 00                     | 00                     | 0D                     |
| k K                      | Normal key      | 00                       | 0E                       | 00                     | 00                     | 00                     | 0E                     |
| l L                      | Normal key      | 00                       | 0F                       | 00                     | 00                     | 00                     | 0F                     |
| m M                      | Normal key      | 00                       | 10                       | 00                     | 00                     | 00                     | 10                     |
| n N                      | Normal key      | 00                       | 11                       | 00                     | 00                     | 00                     | 11                     |
| o O                      | Normal key      | 00                       | 12                       | 00                     | 00                     | 00                     | 12                     |
| p P                      | Normal key      | 00                       | 13                       | 00                     | 00                     | 00                     | 13                     |
| q Q                      | Normal key      | 00                       | 14                       | 00                     | 00                     | 00                     | 14                     |
| r R                      | Normal key      | 00                       | 15                       | 00                     | 00                     | 00                     | 15                     |
| s S                      | Normal key      | 00                       | 16                       | 00                     | 00                     | 00                     | 16                     |
| t T                      | Normal key      | 00                       | 17                       | 00                     | 00                     | 00                     | 17                     |
| u U                      | Normal key      | 00                       | 18                       | 00                     | 00                     | 00                     | 18                     |
| v V                      | Normal key      | 00                       | 19                       | 00                     | 00                     | 00                     | 19                     |
| w W                      | Normal key      | 00                       | 1A                       | 00                     | 00                     | 00                     | 1A                     |
| x X                      | Normal key      | 00                       | 1B                       | 00                     | 00                     | 00                     | 1B                     |
| y Y                      | Normal key      | 00                       | 1C                       | 00                     | 00                     | 00                     | 1C                     |
| z Z                      | Normal key      | 00                       | 1D                       | 00                     | 00                     | 00                     | 1D                     |
| 1 !                      | Normal key      | 00                       | 1E                       | 00                     | 00                     | 00                     | 1E                     |
| 2 @                      | Normal key      | 00                       | 1F                       | 00                     | 00                     | 00                     | 1F                     |
| 3 #                      | Normal key      | 00                       | 20                       | 00                     | 00                     | 00                     | 20                     |
| 4 $                      | Normal key      | 00                       | 21                       | 00                     | 00                     | 00                     | 21                     |
| 5 %                      | Normal key      | 00                       | 22                       | 00                     | 00                     | 00                     | 22                     |
| 6 ^                      | Normal key      | 00                       | 23                       | 00                     | 00                     | 00                     | 23                     |
| 7 &                      | Normal key      | 00                       | 24                       | 00                     | 00                     | 00                     | 24                     |
| 8 *                      | Normal key      | 00                       | 25                       | 00                     | 00                     | 00                     | 25                     |
| 9 (                      | Normal key      | 00                       | 26                       | 00                     | 00                     | 00                     | 26                     |
| 0 )                      | Normal key      | 00                       | 27                       | 00                     | 00                     | 00                     | 27                     |
| Return                   | Normal key      | 00                       | 28                       | 00                     | 00                     | 00                     | 28                     |
| Escape                   | Normal key      | 00                       | 29                       | 00                     | 00                     | 00                     | 29                     |
| Backspace                | Normal key      | 00                       | 2A                       | 00                     | 00                     | 00                     | 2A                     |
| Tab                      | Normal key      | 00                       | 2B                       | 00                     | 00                     | 00                     | 2B                     |
| Space                    | Normal key      | 00                       | 2C                       | 00                     | 00                     | 00                     | 2C                     |
| - _                      | Normal key      | 00                       | 2D                       | 00                     | 00                     | 00                     | 2D                     |
| = +                      | Normal key      | 00                       | 2E                       | 00                     | 00                     | 00                     | 2E                     |
| [ {                      | Normal key      | 00                       | 2F                       | 00                     | 00                     | 00                     | 2F                     |
| ] }                      | Normal key      | 00                       | 30                       | 00                     | 00                     | 00                     | 30                     |
| \ \|                     | Normal key      | 00                       | 31                       | 00                     | 00                     | 00                     | 31                     |
| Europe 1 (Note 2)        | Normal key      | 00                       | 32                       | 00                     | 00                     | 00                     | 32                     |
| ; :                      | Normal key      | 00                       | 33                       | 00                     | 00                     | 00                     | 33                     |
| ' "                      | Normal key      | 00                       | 34                       | 00                     | 00                     | 00                     | 34                     |
| ` ~                      | Normal key      | 00                       | 35                       | 00                     | 00                     | 00                     | 35                     |
| , <                      | Normal key      | 00                       | 36                       | 00                     | 00                     | 00                     | 36                     |
| . >                      | Normal key      | 00                       | 37                       | 00                     | 00                     | 00                     | 37                     |
| / ?                      | Normal key      | 00                       | 38                       | 00                     | 00                     | 00                     | 38                     |
| Caps Lock                | Normal key      | 00                       | 39                       | 00                     | 00                     | 00                     | 39                     |
| F1                       | Normal key      | 00                       | 3A                       | 00                     | 00                     | 00                     | 3A                     |
| F2                       | Normal key      | 00                       | 3B                       | 00                     | 00                     | 00                     | 3B                     |
| F3                       | Normal key      | 00                       | 3C                       | 00                     | 00                     | 00                     | 3C                     |
| F4                       | Normal key      | 00                       | 3D                       | 00                     | 00                     | 00                     | 3D                     |
| F5                       | Normal key      | 00                       | 3E                       | 00                     | 00                     | 00                     | 3E                     |
| F6                       | Normal key      | 00                       | 3F                       | 00                     | 00                     | 00                     | 3F                     |
| F7                       | Normal key      | 00                       | 40                       | 00                     | 00                     | 00                     | 40                     |
| F8                       | Normal key      | 00                       | 41                       | 00                     | 00                     | 00                     | 41                     |
| F9                       | Normal key      | 00                       | 42                       | 00                     | 00                     | 00                     | 42                     |
| F10                      | Normal key      | 00                       | 43                       | 00                     | 00                     | 00                     | 43                     |
| F11                      | Normal key      | 00                       | 44                       | 00                     | 00                     | 00                     | 44                     |
| F12                      | Normal key      | 00                       | 45                       | 00                     | 00                     | 00                     | 45                     |
| Print Screen             | Normal key      | 00                       | 46                       | 00                     | 00                     | 00                     | 46                     |
| Scroll Lock              | Normal key      | 00                       | 47                       | 00                     | 00                     | 00                     | 47                     |
| Break (Ctrl-Pause)       | Normal key      | 00                       | 48                       | 00                     | 00                     | 00                     | 48                     |
| Pause                    | Normal key      | 00                       | 48                       | 00                     | 00                     | 00                     | 48                     |
| Insert                   | Normal key      | 00                       | 49                       | 01                     | 00                     | 00                     | 49                     |
| Home                     | Normal key      | 00                       | 4A                       | 00                     | 00                     | 00                     | 4A                     |
| Page Up                  | Normal key      | 00                       | 4B                       | 00                     | 00                     | 00                     | 4B                     |
| Delete                   | Normal key      | 00                       | 4C                       | 00                     | 00                     | 00                     | 4C                     |
| End                      | Normal key      | 00                       | 4D                       | 00                     | 00                     | 00                     | 4D                     |
| Page Down                | Normal key      | 00                       | 4E                       | 00                     | 01                     | 01                     | 4E                     |
| Right Arrow              | Normal key      | 00                       | 4F                       | 00                     | 00                     | 00                     | 4F                     |
| Left Arrow               | Normal key      | 00                       | 50                       | 00                     | 00                     | 00                     | 50                     |
| Down Arrow               | Normal key      | 00                       | 51                       | 00                     | 00                     | 00                     | 51                     |
| Up Arrow                 | Normal key      | 00                       | 52                       | 00                     | 00                     | 00                     | 52                     |
| Num Lock                 | Normal key      | 00                       | 53                       | 00                     | 00                     | 00                     | 53                     |
| Keypad /                 | Normal key      | 00                       | 54                       | 00                     | 00                     | 00                     | 54                     |
| Keypad *                 | Normal key      | 00                       | 55                       | 00                     | 00                     | 00                     | 55                     |
| Keypad -                 | Normal key      | 00                       | 56                       | 00                     | 00                     | 00                     | 56                     |
| Keypad +                 | Normal key      | 00                       | 57                       | 00                     | 00                     | 00                     | 57                     |
| Keypad Enter             | Normal key      | 00                       | 58                       | 00                     | 00                     | 00                     | 58                     |
| Keypad 1 End             | Normal key      | 00                       | 59                       | 00                     | 00                     | 00                     | 59                     |
| Keypad 2 Down            | Normal key      | 00                       | 5A                       | 00                     | 00                     | 00                     | 5A                     |
| Keypad 3 PageDn          | Normal key      | 00                       | 5B                       | 00                     | 00                     | 00                     | 5B                     |
| Keypad 4 Left            | Normal key      | 00                       | 5C                       | 00                     | 00                     | 00                     | 5C                     |
| Keypad 5                 | Normal key      | 00                       | 5D                       | 00                     | 00                     | 00                     | 5D                     |
| Keypad 6 Right           | Normal key      | 00                       | 5E                       | 00                     | 00                     | 00                     | 5E                     |
| Keypad 7 Home            | Normal key      | 00                       | 5F                       | 00                     | 00                     | 00                     | 5F                     |
| Keypad 8 Up              | Normal key      | 00                       | 60                       | 00                     | 00                     | 00                     | 60                     |
| Keypad 9 PageUp          | Normal key      | 00                       | 61                       | 00                     | 00                     | 00                     | 61                     |
| Keypad 0 Insert          | Normal key      | 00                       | 62                       | 00                     | 00                     | 00                     | 62                     |
| Keypad . Delete          | Normal key      | 00                       | 63                       | 00                     | 00                     | 00                     | 63                     |
| Europe 2                 | Normal key      | 00                       | 64                       | 00                     | 00                     | 00                     | 64                     |
| App                      | Normal key      | 00                       | 65                       | 00                     | 00                     | 00                     | 65                     |
| Keyboard Power           | Normal key      | 00                       | 66                       | 00                     | 00                     | 00                     | 66                     |
| Keypad =                 | Normal key      | 00                       | 67                       | 00                     | 00                     | 00                     | 67                     |
| F13                      | Normal key      | 00                       | 68                       | 00                     | 00                     | 00                     | 68                     |
| F14                      | Normal key      | 00                       | 69                       | 00                     | 00                     | 00                     | 69                     |
| F15                      | Normal key      | 00                       | 6A                       | 00                     | 00                     | 00                     | 6A                     |
| F16                      | Normal key      | 00                       | 6B                       | 00                     | 00                     | 00                     | 6B                     |
| F17                      | Normal key      | 00                       | 6C                       | 00                     | 00                     | 00                     | 6C                     |
| F18                      | Normal key      | 00                       | 6D                       | 00                     | 00                     | 00                     | 6D                     |
| F19                      | Normal key      | 00                       | 6E                       | 00                     | 00                     | 00                     | 6E                     |
| F20                      | Normal key      | 00                       | 6F                       | 00                     | 00                     | 00                     | 6F                     |
| F21                      | Normal key      | 00                       | 70                       | 00                     | 00                     | 00                     | 70                     |
| F22                      | Normal key      | 00                       | 71                       | 00                     | 00                     | 00                     | 71                     |
| F23                      | Normal key      | 00                       | 72                       | 00                     | 00                     | 00                     | 72                     |
| F24                      | Normal key      | 00                       | 73                       | 00                     | 00                     | 00                     | 73                     |
| Keypad , (Brazilian Keypad .) | Normal key | 00                   | 85                       | 00                     | 00                     | 00                     | 85                     |
| Keyboard Int'l 1 ろ (Ro) | Normal key     | 00                       | 87                       | 00                     | 00                     | 00                     | 87                     |
| Keyboard Int'l 2 かたかな ひらがな ローマ字 (Katakana/Hiragana) | Normal key | 00 | 88              | 00                     | 00                     | 00                     | 88                     |
| Keyboard Int'l 2 ¥ (Yen)| Normal key      | 00                       | 89                       | 00                     | 00                     | 00                     | 89                     |
| Keyboard Int'l 4 前候補 変換(次候補) 全候補 (Henkan) | Normal key | 00 | 8A            | 00                     | 00                     | 00                     | 8A                     |
| Keyboard Int'l 5 無変換 (Muhenkan) | Normal key | 00              | 8B                       | 00                     | 00                     | 00                     | 8B                     |
| Keyboard Lang 1 한/옝 (Hanguel/English) | Normal key | 00         | 90                       | 00                     | 00                     | 00                     | 90                     |
| Left Control             | Normal key      | 00                       | E0                       | 00                     | 00                     | 00                     | E0                     |
| Left Shift               | Normal key      | 00                       | E1                       | 00                     | 00                     | 00                     | E1                     |
| Left Alt                 | Normal key      | 00                       | E2                       | 00                     | 00                     | 00                     | E2                     |
| Left GUI                 | Normal key      | 00                       | E3                       | 00                     | 00                     | 00                     | E3                     |
| Right Control            | Normal key      | 00                       | E4                       | 00                     | 00                     | 00                     | E4                     |
| Right Shift              | Normal key      | 00                       | E5                       | 00                     | 00                     | 00                     | E5                     |
| Right Alt                | Normal key      | 00                       | E6                       | 00                     | 00                     | 00                     | E6                     |
| Right GUI                | Normal key      | 00                       | E7                       | 00                     | 00                     | 00                     | E7                     |
| Mouse L Button           | Mouse button    | 00                       | F0                       | 02                     | 00                     | 00                     | F0                     |
| Mouse R Button           | Mouse button    | 00                       | F1                       | 02                     | 00                     | 00                     | F1                     |
| Mouse M Button           | Mouse button    | 00                       | F2                       | 02                     | 00                     | 00                     | F2                     |
| Mouse B4 Button          | Mouse button    | 00                       | F3                       | 02                     | 00                     | 00                     | F3                     |
| Mouse B5 Button          | Mouse button    | 00                       | F4                       | 02                     | 00                     | 00                     | F4                     |
| Mouse wheel up           | Mouse button    | 00                       | F5                       | 02                     | 00                     | Value                  | F5                     |
| Mouse wheel down         | Mouse button    | 00                       | F6                       | 02                     | 00                     | Value                  | F6                     |
| Mouse AC Pan left        | Mouse button    | 00                       | F7                       | 02                     | 00                     | Value                  | F7                     |
| Mouse AC Pan right       | Mouse button    | 00                       | F8                       | 02                     | 00                     | Value                  | F8                     |
| System Power             | ACPI            |                          |                          | 04                     | 00                     | 00                     | 01                     |
| System Sleep             | ACPI            |                          |                          | 04                     | 00                     | 00                     | 02                     |
| System Wake              | ACPI            |                          |                          | 04                     | 00                     | 00                     | 04                     |
| Scan Next Track          | Multi-media key |                          |                          | 03                     | 00                     | 00                     | B5                     |
| Scan Previous Track      | Multi-media key |                          |                          | 03                     | 00                     | 00                     | B6                     |
| Stop                     | Multi-media key |                          |                          | 03                     | 00                     | 00                     | B7                     |
| Play/Pause               | Multi-media key |                          |                          | 03                     | 00                     | 00                     | CD                     |
| Mute                     | Multi-media key |                          |                          | 03                     | 00                     | 00                     | E2                     |
| Volume Up                | Multi-media key |                          |                          | 03                     | 00                     | 00                     | E9                     |
| Volume Down              | Multi-media key |                          |                          | 03                     | 00                     | 00                     | EA                     |
| Media Select             | Multi-media key |                          |                          | 03                     | 00                     | 02                     | 83                     |
| Mail                     | Multi-media key |                          |                          | 03                     | 00                     | 02                     | 8A                     |
| Calculator               | Multi-media key |                          |                          | 03                     | 00                     | 02                     | 92                     |
| My Computer              | Multi-media key |                          |                          | 03                     | 00                     | 02                     | 94                     |
| WWW Search               | Multi-media key |                          |                          | 03                     | 00                     | 02                     | 21                     |
| WWW Home                 | Multi-media key |                          |                          | 03                     | 00                     | 02                     | 23                     |
| WWW Back                 | Multi-media key |                          |                          | 03                     | 00                     | 02                     | 24                     |
| WWW Forward              | Multi-media key |                          |                          | 03                     | 00                     | 02                     | 25                     |
| WWW Stop                 | Multi-media key |                          |                          | 03                     | 00                     | 02                     | 26                     |
| WWW Refresh              | Multi-media key |                          |                          | 03                     | 00                     | 02                     | 27                     |
| WWW Favorites            | Multi-media key |                          |                          | 03                     | 00                     | 02                     | 2A                     |

---

### Key Matrix Extended Reference

| Category    | Function           | Byte3 | Byte2   | Byte1   | Byte0 |
|-------------|--------------------|-------|---------|---------|-------|
| Vendor code |                    | F1    | XX      | YY      | ZZ    |
| Macro Key   | Macro              | 05    | Type    | 00      | Index |
| DPI switch  | DPI+               | 0B    | 00      | 00      | 01    |
| DPI switch  | DPI-               | 0B    | 00      | 00      | 02    |
| DPI switch  | DPI+ loop          | 0B    | 00      | 00      | 03    |
| DPI switch  | DPI Value          | 0B    | Value_H | Value_L | 04    |
| Special Key | Fn                 | F0    | 00      | 00      | 00    |
| Special Key | Profile+           | F0    | 01      | 00      | 01    |
| Special Key | Profile-           | F0    | 01      | 00      | 02    |
| Special Key | Profile+ loop      | F0    | 01      | 00      | 03    |
| Special Key | Profile value      | F0    | 01      | value   | 04    |
| Special Key | Report rate +      | F0    | 02      | 01      | 00    |
| Special Key | Report rate -      | F0    | 02      | 02      | 00    |
| Special Key | Report rate + loop | F0    | 02      | 03      | 00    |
| Special Key | Report rate Value  | F0    | 02      | Value   | 04    |
| Special Key | Snipe              | F0    | 03      | Value_H | Value_L |
| Special Key | Tab Function       | F0    | 04      | Delay time | Alt or Win |
| Fire Key    | Fire mode          | 0C    | repeat  | delaytime | code |
| RGB Key     | Effect Loop        | 0A    | 00      | 00      | 03    |
| RGB Key     | Effect Inc         | 0A    | 00      | 00      | 01    |
| RGB Key     | Effect Dec         | 0A    | 00      | 00      | 02    |
| RGB Key     | Effect Value       | 0A    | 00      | Value   | 04    |
| RGB Key     | Brightness Loop    | 0A    | 01      | 00      | 03    |
| RGB Key     | Brightness Inc     | 0A    | 01      | 00      | 01    |
| RGB Key     | Brightness Dec     | 0A    | 01      | 00      | 02    |
| RGB Key     | Brightness Value   | 0A    | 01      | Value   | 04    |
| RGB Key     | Speed Loop         | 0A    | 02      | 00      | 03    |
| RGB Key     | Speed Inc          | 0A    | 02      | 00      | 01    |
| RGB Key     | Speed Dec          | 0A    | 02      | 00      | 02    |
| RGB Key     | Speed Value        | 0A    | 02      | Value   | 04    |
| RGB Key     | Color Loop         | 0A    | 03      | 00      | 03    |
| RGB Key     | Color Inc          | 0A    | 03      | 00      | 01    |
| RGB Key     | Color Dec          | 0A    | 03      | 00      | 02    |
| RGB Key     | Color Value        | 0A    | 03      | Value   | 04    |
| RGB Key     | Option Loop        | 0A    | 04      | 00      | 03    |
| RGB Key     | Option Inc         | 0A    | 04      | 00      | 01    |
| RGB Key     | Option Dec         | 0A    | 04      | 00      | 02    |
| RGB Key     | Option Value       | 0A    | 04      | Value   | 04    |
