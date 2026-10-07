# Draft: Manufacturer Inquiry — X43 LED Engine Disabled (PID 0xA245)

**To:** Easen / firmware team
**Subject:** X43 Wired Gaming Mouse (VID 04D9, PID A245, FW 1.1) — LED engine permanently disabled after parameter writes; need recovery procedure

---

Hello,

We are developing the companion software for the X43 wired gaming mouse using your
protocol document *"X43 Wired Gaming Mouse driver protocol_20260427"*. During
development, a unit's LED subsystem became permanently disabled, and we cannot
recover it with any documented command. We need your guidance on two specific
points (questions at the end).

## Device identification

Read live from the unit via Get firmware version (80H); reply received over
GetFeature:

| Field | Value |
|---|---|
| USB VID / PID | 0x04D9 / 0xA245 |
| Firmware version | H-Ver 0x01, L-Ver 0x01 (v1.1) |
| mcu_type | 0x03 (HT68FB571) |
| sensor_type | 0x0A |
| key count | 8 |

The command processor is fully responsive: 80H returns correct data, and all
Set parameter_0 (02H) / Set parameter_1 (03H) writes are accepted and take
effect (DPI values, key matrix, debounce, lift-off all verified working).

## What happened

During a software bug, the device received, within ~5 seconds, eight rapid
interleaved sequences of:

1. Read parameter_1 command (83H) with no host reading the EP5 data,
2. Set parameter_0 (02H) command header,
3. 64-byte EP6 data write (param_0 with ledEffect=0x00),

plus a Set parameter_1 (03H) write of all 3 blocks (status=0x07, times=3) with
mostly zeroed data. Since then:

- **No LED effect ever displays.** All effect types 00H–09H were written to
  param_0 with brightness 0xFF, all speeds, all colour slots, both Option
  values — LED stays dark.
- **The DPI stage indicator flash no longer appears** when the DPI stage
  changes (this is firmware-internal behaviour, so the LED engine itself is
  off, not just our effect settings).
- The condition **persists across replug and full power-down.**
- Everything else works: buttons, key matrix remapping, DPI stages and DPI
  switch key, report rate, sensor.

## What we have already verified/tried (all with no LED response)

- param_0 rewritten with every documented effect/brightness/speed/colour/option
  combination, with and without the byte-8 checksum (doc says checksum unused).
- Full Set parameter_1 (03H) writes: single-block (index=1, status=0x02) and
  all-blocks (index=0, status=0x07, times=3) with valid colour tables at bytes
  64–111 — including a byte-exact replay of a sequence that previously
  produced working LED effects on this same unit.
- All six profiles selected via Set current profile (01H) — LED dark on all.
- Key matrix restored to the documented default (KEY0–4 = L/R/M/B4/B5,
  KEY5–15 null) and with LED-control key (0AH/03H) bound to the LED switch key.
- Reserved areas (param_1 bytes 41–63, 112–127; param_0 bytes 7–63) filled
  with 0x00 / 0x01 / 0xFF.
- Candidate reset commands: 01H with Profile#=0xFF and 0x06; undocumented
  command bytes 06H–0FH and FFH.

## Questions

1. **Restore to defaults**: the vendor code table documents a notification
   `Type 0xFF, Code 00` = "当前profile 已恢复默认设置" (current profile restored
   to default settings). **What triggers this restore?** Is there a host
   command, or a button/power-on combination on the mouse? This unit was once
   accidentally restored this way, which fixed the LED, so a deliberate
   procedure would solve our problem.

2. **Fourth parameter block**: Read parameter_1 (83H) is documented to return
   4×64 bytes, while Set parameter_1 (03H) writes at most 3×64. **What does the
   fourth 64-byte block contain, and can it become corrupted by interleaved
   command/data traffic?** If yes, how can it be re-initialised?

3. **LED engine state**: is there any firmware state outside param_0/param_1
   (e.g. an LED enable latch controlled by the LED switch key, or calibration
   data) that gates all LED output including the DPI-stage indicator flash?
   How can the host read or reset it?

4. **EP5 reads on Windows**: the interface carrying EP5 is not accessible
   through the Windows HID API (the vendor collection reports no input
   report). What is the intended way for Windows software to read parameters
   back — is there an alternative to EP5, e.g. chunked GetFeature?

If it helps, we can provide full USB traces of every sequence described above.

Thank you,
Pavlo Karmazin
ProoveGaming software team

---

*(Optional Chinese summary for faster routing:)*

您好！我们在为 X43 鼠标（VID 04D9 / PID A245，固件 v1.1，HT68FB571）开发驱动软件时，
一台设备的灯效系统被永久关闭：所有灯效（00H–09H）写入 param_0 后 LED 均不亮，DPI 段
切换时的指示灯也不再闪烁，重新插拔和断电均无效，但按键、DPI、回报率等其他功能全部正常，
80H 指令也能正确返回版本信息。请问：
1. 协议文档中的通知 "Type 0xFF, Code 00：当前 profile 已恢复默认设置" 是由什么触发的？
   是否有主机指令或按键组合可以主动恢复出厂设置？
2. Read parameter_1 (83H) 返回 4×64 字节，但 Set parameter_1 (03H) 最多写 3×64 字节，
   第 4 个 64 字节块存储什么内容？如果损坏如何重新初始化？
3. 除 param_0/param_1 之外，固件是否还有控制 LED 总开关的状态？主机如何读取或复位？
谢谢！
