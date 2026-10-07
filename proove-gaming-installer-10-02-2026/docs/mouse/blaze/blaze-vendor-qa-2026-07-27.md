# BLAZE — Vendor / FW-team Q&A (2026-07-27)

English translation and analysis of the vendor's answers to our BLAZE blocker questions.

**Source:** `docs/mouse/blaze/Blaze 27.07.csv` — the factory / firmware team's replies (originally
in Chinese). The CSV contained only the **answers** column; the questions are reconstructed here
from our own open blockers (see [led.md](led.md), [key-matrix.md](key-matrix.md),
[macros.md](macros.md)). Each item keeps the original Chinese for traceability, the English
translation, and what it means for our implementation.

> Translation note: this file is the English record required by the repo docs rules. The raw CSV
> is kept alongside it for provenance.

---

## A. Restore-to-default / reset command

Original:
> 在规格上没有组合键恢复默认的指令，请确认规格（底部按键为回报率切换和灯效切换）
>
> 从软件端复位 FW 的方式目前为：将预设的规格 profile 写入 FW，也就是软件端需要有一组默认
> profile 参数，通过 0x02、0x03 两个 table 重新刷一遍数据

Translation:
- **There is no key-combination "restore defaults" command in the spec** — please confirm the
  spec. (The two buttons on the underside are **polling-rate switch** and **lighting-effect
  switch**, not a reset combo.)
- **The current way to reset the firmware from software is:** write the preset spec profile into
  the FW. I.e. the software must hold a set of **default-profile parameters** and re-flash them by
  writing the **`0x02` and `0x03` tables** again.

What it means for us:
- There is **no hardware reset combo** to rely on. The "one time the LED lit after a button-combo
  re-plug" (see [led.md](led.md) §"The one time it lit") was **not** a documented reset path — the
  vendor confirms no such combo exists in the spec.
- **Reset-to-default is a software feature we implement:** keep a canonical default profile in the
  App and push it to the device by writing the `0x02` (param_0 / DPI+LED) and `0x03` (key-matrix /
  settings) tables. This is the same block-write mechanism `EasenMouseService` already uses — reset
  is just "write our known-good default set to both tables."

## B. Profiles, flash storage, and persistence across power-cycle

Original:
> 1. 插拔后恢复默认 profile 是当时贵司提出的需求
> 2. 由于 flash 空间限制 FW 端无法存储过多的 profile，目前只有 user profile 和 default
>    profile；拓展的 profile 要靠软件端存储，切换是重新下发软件端所建置的 profile 内容
>
> 此问题确实存在，但因规格上鼠标断电后会恢复为 default，因此不会存在向 FW Sync 的场景；
> 如客户有 Sync FW 的 profile 的需求可再行处理

Translation:
1. **Restoring the default profile after re-plugging was a requirement your company (we) raised**
   at the time — it is intended behaviour, not a bug.
2. Due to **flash-space limits the FW cannot store many profiles**; currently there are only a
   **user profile** and a **default profile**. Additional/"extended" profiles must be stored on
   the **software side**, and switching profiles means **re-sending the software-built profile
   content** to the device.

And: this issue does exist, but **because per the spec the mouse reverts to the default profile
after power loss, there is no "sync to FW" scenario**. If we (the customer) need FW-profile sync,
it can be handled separately.

What it means for us:
- This resolves the long-standing open problem **"No persistence across reconnect"**
  ([led.md](led.md) §"Two open problems" #2): it is **by design**, not a missing commit step. The
  device intentionally reverts to the default profile on power loss. We should **stop hunting for a
  flash-commit trigger** — there isn't one for extended profiles.
- **Persistence is the App's job:** store profiles App-side (already the model — see
  [macros.md](macros.md) "App SQLite is the single source of truth") and **re-apply the active
  profile on every (re)connect** rather than expecting the device to remember it.
- If we ever need *true* on-device persistence, that is a **separate vendor request** ("Sync FW
  profile"), not something reachable with the current firmware.
- Note the device does expose a **user profile** slot in flash alongside the default; per the
  power-off-reverts-to-default rule, do not assume the user profile survives a power cycle. Worth a
  one-line follow-up to the vendor to confirm exactly what the user profile retains, if anything.

## C. LED effects — the full set is only four

Original:
> 在规格上只有 4 个灯效：关闭（0x00）呼吸（0x02）霓虹（0x03）雨滴（0x06），测试与 protocol
> 上写的并无差异，请确认是否为规格问题

Translation:
- **The spec has only 4 lighting effects:** Off (`0x00`), Breathing (`0x02`), Neon (`0x03`),
  Raindrop (`0x06`). Testing shows **no difference from what the protocol document states**; please
  confirm whether the effect count is the "spec issue" you are raising.

What it means for us:
- Confirms open problem **"No true static effect"** ([led.md](led.md) #1): there is **no static /
  direct-RGB effect** — the four effects are the complete set, and none is a plain "set colour, no
  animation." This is a firmware/spec limit, not our bug.
- **Double-check needed — our effect mapping may be off.** [led.md](led.md)'s
  `MapAppEffectToProtocol` table maps **ColorCycle → proto `0x04`**, but `0x04` is **not** in the
  vendor's four-effect list. And the vendor lists **Raindrop (`0x06`)**, which the App does not
  currently expose. Action: reconcile the App's effect list against {Off `0x00`, Breathing `0x02`,
  Neon `0x03`, Raindrop `0x06`} — verify ColorCycle (`0x04`) actually does anything on hardware and
  decide whether to surface Raindrop.

## D. Table / block layout

Original:
> 03 的 table 只有 192 byte，并无第 4 个 block
>
> 目前规划的协议是这样，以 64 byte 为最小单位，需连带其他的参数一并下发

Translation:
- The **`0x03` table is only 192 bytes** — there is **no 4th block** (192 = 3 × 64-byte blocks).
- The protocol as planned uses a **64-byte minimum unit**; when writing you **must send the other
  parameters together** in the same transfer (you cannot write one field in isolation).

What it means for us:
- Confirms the `0x03` (key-matrix / settings) table is **three 64-byte blocks**, not four — no
  hidden 4th block to target.
- Reinforces the existing rule in [macros.md](macros.md) that **"every button write rebuilds the
  whole key matrix"**: because 64 bytes is the atomic unit and neighbouring params ride along, any
  partial update must re-include the full block's parameters. Our block-write code already does
  this; this is confirmation, not a change.

---

## Summary — what this unblocks / confirms

| Blocker | Vendor verdict | Our next step |
|---------|----------------|---------------|
| Reset to default | No hardware combo; do it in software by re-flashing a default profile via the `0x02` + `0x03` tables | Implement an App-side "reset to default" that writes our canonical default set to both tables |
| Persistence across reconnect | **By design** — device reverts to default on power loss; extended profiles live App-side | Stop chasing a flash-commit; re-apply the active profile on every connect. True FW persistence = separate vendor request |
| No static LED effect | Confirmed — only 4 effects exist, none static | Accept as hardware limit; keep driving the LED through the effect engine |
| Effect set | Off `0x00`, Breathing `0x02`, Neon `0x03`, Raindrop `0x06` | Reconcile App effect list; verify ColorCycle (`0x04`) and consider exposing Raindrop (`0x06`) |
| `0x03` table blocks | 192 bytes = 3 blocks, no 4th; 64-byte atomic writes carrying neighbouring params | No change — confirms current block-write behaviour |

## Open follow-ups for the vendor

1. Does the **user profile** retain anything across a power cycle, or does the device always boot
   from the default profile? (Affects whether App-side re-apply is the *only* path.)
2. Is **ColorCycle (`0x04`)** a real firmware effect, or should the App drop it and expose
   **Raindrop (`0x06`)** instead?
3. Confirm the exact byte layout of the three `0x03` blocks so a "write default profile" reset
   sends valid data for every field.
