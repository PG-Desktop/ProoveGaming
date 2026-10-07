# Smart Infinite (智慧无限) driver protocol — English translation

Translated from `docs/软件驱动协议-20251028-V3.1.xlsx` ("软件驱动协议", Software Driver Protocol,
V3.1, dated 2025-10-28), the vendor sheet covering the Cyrex / Aress / Rampage family. Only the
sheets this repo relies on are reproduced; the workbook remains the authority.

> **This revision does not match the firmware in our units.** Its command numbering has shifted
> since the build our boards run — the sheet lists `0x1A`/`0x1B` for read/write of key matrix 1,
> while our hardware answers on `0x17`/`0x18`, and it calls `0x31` "reset" while `0x20` was measured
> as a **full factory reset** on a real board. Treat command numbers here as a lead to verify, never
> as truth. The **data formats** below have held up against hardware and are the useful part.

> [!IMPORTANT]
> **Transfer discipline settled by the factory (2026-08):** no `0x10`/`0x11` session bracket for
> any command, and writes must transfer the **full table size** (`KEY_MATRIX_TAB_SIZE` 384,
> `MACRO_TAB_SIZE` 2048) — shorter transfers are acknowledged and discarded. The session-lapse
> footnote below (`0x0F` after 5 s) belongs to the bracketed model and is retained only for
> decoding old captures. The workbook's `light_level` 0–99 range is also wrong — real ranges come
> from `0x14` indices 12–15. Full answers, including the unresolved 384-vs-386 and 2048-vs-1024
> conflicts: [`factory-clarifications-2026-08.md`](factory-clarifications-2026-08.md).

## Sheet: 命令 (Commands)

| Cmd | Chinese | English |
|---|---|---|
| `0x10` | 开始通讯 | Start communication |
| `0x11` | 结束通讯 | End communication |
| `0x12` | 读取设备基本信息 | Read device info |
| `0x13` | 设定设备基本信息 | Write device info (reserved) |
| `0x14` / `0x15` | 读取 / 设定功能区信息 | Read / write function-area info |
| `0x16`-`0x19` | 读取默认按键矩阵信息 1-4 | Read **default** key matrix 1-4 |
| `0x1A` / `0x1B` | 读取 / 设定按键矩阵1信息 | Read / write key matrix 1 |
| `0x1C`-`0x21` | … 矩阵 2-4 | … matrices 2-4 |
| `0x22`-`0x2B` | 读取 / 设定自定义灯光矩阵 1-5 | Read / write custom lighting matrix 1-5 |
| **`0x2C`** | **读取宏信息** | **Read macro info** |
| **`0x2D`** | **设定宏信息** | **Write macro info** — confirmed working on a real Cyrex |
| `0x30` | 读取电池状态 | Read battery state |
| `0x31` | 复位 | Reset |
| `0x32` | 读取灯光矩阵信息 | Read the key→LED index map (`0xFF` = key has no LED) |

Footnote in the sheet: if byte 8 of a reply is `0x0F`, the session has lapsed — the firmware treats
the driver as gone after **5 s** without valid traffic, and `0x10` must be sent again.

## Sheet: 按键信息格式 (Key matrix record format) — 3 bytes

`{TYPE, CODE_L (Data_L), CODE_H (Data_H)}`

```c
typedef enum {
    KB_TYPE              = 0X10,  // 基本按键1        basic key 1
    KB_TYPE1             = 0X11,  // 基本按键2        basic key 2
    KB_TYPE2             = 0X12,  // 基本按键3        basic key 3
    SYSTEM_TYPE          = 0X20,  // 系统按键          system key
    CONSUMER_TYPE        = 0X30,  // 多媒体按键        media key
    MS_TYPE              = 0X40,  // MS 鼠标功能类型   mouse function
    FUNC_TYPE            = 0X50,  // 功能类按键        FUNCTION key  <-- NOT a macro
    MACRO_AP_TYPE        = 0X60,  // 单次宏功能按键    single-play macro
    MACRO_AP_REPETI_TYPE = 0X61,  // 多次宏功能按键    repeat macro
    FIRE_TYPE            = 0X70,  // 火力键            rapid-fire
    KB_REPET_TYPE        = 0X80,  // 按键击打          key auto-repeat
} Keyboard_Key_Type;
```

Three basic-key types exist so that one matrix read can hold several keys of the same kind without
the driver confusing them.

**`0x60` — single-play macro.** `CODE_L` is which macro (its index in the library); `CODE_H` is how
it plays:

| `CODE_H` | Chinese | Behaviour |
|---|---|---|
| `0x00` | 正常宏类型 | Press plays the macro through once |
| `0x01` | 释放停止 | Plays while held; stops on release |
| `0x02` | 按下停止 | Starts the moment the key is pressed and released, keeps playing until pressed again |
| `0x03` | 释放立即停止 | Press to start, press again to stop |

The sheet's worked example: to make ESC a normal macro key, find ESC's index in the key matrix and
set its three bytes to `0X60 0X00 0X00`. Macro data must be sent **before** the matrix is changed.

**`0x61` — repeat macro.** `CODE_L` is which macro, `CODE_H` is the repeat count; the macro plays in
the normal (play-through) mode. The sheet's shorthand: "`0X61`, which macro, how many times".

`FUNC_TYPE` (`0x50`) covers layer switching (`KEY_MO0`-`KEY_TO3`), backlight/LOGO/side-light
controls, Bluetooth and 2.4G channel switching, battery query, Win-lock, and screen controls — its
`Keyboard_Function_Type` enum starts at `0x01`, so `CODE_L = 0x00` is not a valid function.

## Sheet: 宏信息格式 (Macro data format)

Header (宏基本信息), 8 bytes:

| Offset | Field | Value |
|---|---|---|
| 0 | 是否使用标志位L | `0xAA` |
| 1 | 是否使用标志位H | `0x55` |
| 2-3 | 使用长度 L/H | Total length of the macro data, header included |
| 4-5 | 宏数量 L/H | How many macros |
| 6-7 | 保留 | Reserved |

Then an offset table, 2 bytes little-endian per macro: macro *N*'s flash offset lives at
`N * 2 + 8`. Each macro block begins at its own offset:

| Offset in block | Field |
|---|---|
| +0, +1 | 动作数量 L/H — number of **actions** (one key press = 2 actions: down and up) |
| +2, +3 | Reserved |
| +4… | Actions, 4 bytes each |

Each action is `{delay_L, delay_H, key value, action type}`. The **action type** byte:

- **bit 7** — key: `1` = pressed, `0` = released. Mouse: `1` = left / scroll-left / scroll-up,
  `0` = right / scroll-right / scroll-down.
- **low bits** — what kind of action:

| Value | Chinese | English |
|---|---|---|
| `0x01` | 键盘普通按键 | Keyboard normal key |
| `0x02` | 键盘热按键 | Keyboard hot key |
| `0x03` | 鼠标按键 | Mouse button |
| `0x04` | 鼠标x移动 | Mouse X movement |
| `0x05` | 鼠标Y移动 | Mouse Y movement |
| `0x06` | 鼠标左右摆 | Mouse horizontal swing |
| `0x07` | 鼠标上下滚 | Mouse wheel |

So a normal keyboard key is `0x81` pressed and `0x01` released — which is what `RasterMacroEncoder`
emits, and why the same packed buffer serves both families.

> The sheet's per-action offsets read +4..+7 for the first action and +9..+12 for the second, which
> would make the stride 5. That is a typo in the sheet: the stride is 4, as the Raster
> implementation and its hardware testing confirm.
