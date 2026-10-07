# TAIPAN — Vendor / FW-team Q&A (2026-07-30)

English translation and analysis of the vendor's answers to our TAIPAN protocol blocker
questions (items T1–T10).

**Source:** `docs/mouse/taipan/Taipan-update-info.csv` — the factory / firmware team's replies
(originally in Chinese), one row per item. Columns: ID, device, title, status code, our
detail/problem description, our request, vendor's answer, plus a few extra columns the vendor
used for structured values (see T3/T7). The CSV's status codes are the team's own triage:
(A) undocumented/missing, (B) doc error/contradiction, (C) documented but not actually
implemented, (D) actual behaviour disagrees with the doc. This file keeps the original Chinese
for traceability, the English translation, and what it means for our implementation.

> Translation note: this file is the English record required by the repo docs rules
> (`CLAUDE.md` → Documentation rules). The raw CSV is kept alongside it for provenance.

---

## T1. Per-DPI-stage indicator LED colour (`0x0C` / `0x4C`)

**Status:** (C) documented but not actually effective

Our problem statement:
> `CMD_RW_DPI_Color` (`0x0C`/`0x4C`) exists in the command list, but the address table marks it
> as reserved — the firmware does not actually apply the per-stage DPI indicator colour.

Our request:
> Please confirm whether `0x0C` is implemented in shipping firmware. If it is reserved, mark it
> explicitly as unsupported and say whether there is any alternative.

Vendor answer (original):
> 出货的固件目前不支持修改颜色

Translation:
> The shipped firmware currently does not support changing [the DPI indicator] colour.

**What it means for us:** Confirms `0x0C`/`0x4C` is a no-op on real hardware. Matches what
`TaipanDpiTable`/`EasenMouseService`-equivalent code already assumes — the App already treats
per-stage DPI colour as **display-only** (see the code comment in `TaipanDpiTable.cs`: *"per-stage
indicator colours live in the separate DPI-colour table (0x0C) and are display-only"*). No code
change needed; this upgrades that comment from "assumed" to **vendor-confirmed**.

## T2. Writing settings over the 2.4G dongle and Bluetooth

**Status:** (A) undocumented / missing

Our problem statement:
> Over the 2.4G dongle and Bluetooth, the mouse does not accept host→device write frames; only
> device→host push frames (`0x48`/`0x49`) get relayed.

Our request:
> Document which commands are supported per transport (wired/2.4G/Bluetooth), and say whether
> configuration writes over the dongle/Bluetooth are possible at all.

Vendor answer (original):
> 只支持有线和2.4G 不支持蓝牙 协议都是一样的，如果2.4G 不能下值需要把你们对应连接的方式发过来或者
> 整个连接设备的代码发过来我们这边看一下

Translation:
> Only wired and 2.4G are supported; Bluetooth is not supported. The protocol is identical across
> transports. If 2.4G writes aren't landing on your end, send us your connection method (or the
> full device-connection code) so we can look into it.

**What it means for us:**
- Bluetooth writes are **not supported by the vendor's own stack**, not just undocumented — this
  is a firmware/hardware limitation, not something we can work around in the App.
- 2.4G writes are supposed to work with the **same frame format** as wired, per the vendor. **We
  tested this directly on hardware (2026-07-31) and it does not hold**: DPI table, MsConfig, and
  button-map writes all reach the dongle without a transport error but have zero effect on the
  device — confirmed unambiguously via a Button-3-to-Mute distinguishing write (see
  [protocol.md](protocol.md#confirmed-on-hardware--24g-dongle-readwrite-behaviour)). Reads are
  equally dead (`0x41`/`0x42`/`0x43`/`0x08` all timed out with no reply). **Follow-up:** send the
  vendor our `TelinkMouseService` write path per their own offer — this contradicts their "protocol
  is identical across transports" claim and is worth escalating, but until/unless they resolve it,
  the App must treat the 2.4G dongle as read-only-via-push (battery/state) and route all
  configuration writes (buttons, DPI, polling rate, lift-off) through the wired cable only.

## T3. Active DPI stage byte (`MsConfig 0x0011`) lags behind real state

**Status:** (D) actual behaviour disagrees with the doc

Our problem statement:
> The doc describes `DpiSwitch` (`0x0011`) as the *current* stage, but the stored byte lags one
> physical button press behind — a read returns the *previous* stage.

Our request:
> Explain when the firmware updates `0x0011`, and provide a reliable way to read the true current
> stage.

Vendor answer (original):
> 当前DPI档数 0x01 范围:0x00…0x07
> 0x00是第一档 0x01是第二档

Translation:
> Current DPI stage count: `0x01` [example]. Range: `0x00`–`0x07`. `0x00` is the first stage,
> `0x01` is the second stage.

**What it means for us:** The vendor restated the **indexing convention** (0-based, confirmed —
matches what `protocol.md` already documents) but did **not address the lag** we actually asked
about — this question is effectively unanswered. **Worth a direct one-line re-ask**: "when exactly
does the firmware commit a new value to `0x0011` relative to the button press?" Until then, keep
treating `0x0011` as unreliable for "read current stage right now": the App already prefers the
device-pushed `Report_MouseState` (`0x49`) / `TryGetLastDpiStage` live-tracking over polling
`0x0011` directly, which sidesteps this — no code change indicated, just don't add a new read path
that trusts `0x0011` at face value.

## T4. On-demand current-state query

**Status:** (A) undocumented / missing

Our problem statement:
> `Report_MouseState` (`0x49`) is only pushed on a physical button press or on connect; there is no
> command to request the current state on demand.

Our request:
> Provide or confirm a command that reads current state immediately.

Vendor answer (original):
> 0x49只是用来接受鼠标回传的信息的，不能主动读取

Translation:
> `0x49` is only for receiving the mouse's pushed-back information; it cannot be actively
> (host-initiated) read.

**What it means for us:** Confirms there is **no on-demand read** — this is by design, not a gap
in our docs. Matches the existing architecture: rely on the connect-time push plus live event
tracking (`DpiStageChanged`/`ReportRateChanged`), never a polled "give me state now" call. No
action needed beyond marking this closed in `protocol.md`'s Open items.

## T5. `CMD_SetProfile` (`0x07`) — switch the active profile

**Status:** (C) documented but not actually effective

Our problem statement:
> `0x07` is documented but flagged as not implemented in current firmware; the host cannot switch
> profiles.

Our request:
> Implement `0x07`, or confirm the host cannot switch profiles and explain how a profile should be
> selected instead.

Vendor answer (original):
> 出货的固件目前不支持profile

Translation:
> The shipped firmware currently does not support profiles [switching via `0x07`].

**What it means for us:** Confirms `protocol.md`'s existing hedge ("Not implemented in current
firmware — may not ACK"), now vendor-verified rather than inferred. The App already never sends
`0x07` — grepped `TelinkMouseService.cs` and the `Taipan*` helper classes, no reference to it
exists. No code change; just remove the hedge in `protocol.md` and state it flatly.

## T6. Battery percentage

**Status:** (A) undocumented / missing

Our problem statement:
> Battery is only reported as millivolts (`CMD_GetBATV` `0x08` → reply `0x48`); there is no
> voltage→percentage curve or threshold documented.

Our request:
> Provide the official voltage-to-percentage curve or thresholds used by the factory software.

Vendor answer (original):
> Baifen = (100 * (981 - 750)) / (985 - 750);
> 0x03D5 = 981,981这个会变，其他是程序固定的计算出来的结果大于100时让他不超过100就行

Translation:
> Percentage = `(100 × (V − 750)) / (985 − 750)`. `0x03D5 = 981` [example reading] — `981` is the
> value that changes on each read; `750` and `985` are fixed constants in the formula. If the
> computed result exceeds 100, clamp it to 100.

**What it means for us — resolved, but not by using this formula.** The example value (`981`, i.e.
`0x03D5`) and the thresholds (`750`, `985`) are **far too small to be the mV field** `protocol.md`
already documents (`Para1/Para2`, e.g. `0x1068 = 4200` mV), and real hardware captures of the SAME
`Para1/Para2` field (via the mouse's own unsolicited `0x48` push, not an active `0x08` query — see
T2 above, active queries get no reply over the dongle) read `1001`–`1008`, which doesn't match the
vendor's `750`–`985` range either. Rather than chase which undocumented byte the vendor's formula
applies to, the App uses its own independently-calibrated interpretation: `Para1/Para2` as
**quarter-millivolts** (`raw × 4`), which lands in a realistic charged-Li-ion range (`4004`–`4032`
mV) and is what's actually implemented and live (`TaipanStateReport.PercentFromRaw`,
`TaipanStateMonitor`). See [protocol.md](protocol.md#what-the-app-actually-does--push-not-active-query-implemented).
Still open: whether the generic `3300`–`4200` mV empty/full endpoints match this battery's real
capacity curve, and what the vendor's `750`–`985` formula actually refers to (never identified) —
low priority now that a working implementation exists via a different path.

## T7. DPI table per-axis layout / "doubled" flag

**Status:** (B) doc error / self-contradictory

Our problem statement:
> The xlsx documents a per-axis "doubled" flag; the PDF shows plain `X_L/X_H/Y_L/Y_H`. The two
> documents' byte stride disagrees.

Our request:
> Provide an updated file, or give one single, current-firmware-correct DPI stage byte layout.

Vendor answer (original):
> 每个DPI档位的长度都是五个bit一共是1+5*8，41个字节但是因为长度限制每条数据长度有限
> Telink-USB20260205.xlsx文档里面就没有写完
>
> DPI[0]:0x00关闭 / 0x01打开 → DPI[0]X轴DPI值 → DPI[0]X轴DPI值翻倍 0x00关，0x01开 →
> DPI[0]Y轴DPI值 → DPI[0]Y轴DPI值翻倍 0x00关，0x01开

Translation:
> Each DPI stage's length is [sic — vendor wrote "bit", almost certainly means "byte"] five bytes,
> for a total of `1 + 5×8 = 41` bytes; the `Telink-USB20260205.xlsx` document was left incomplete
> due to a length limit on each data row.
>
> `DPI[0]`: `0x00` off / `0x01` on (enable) → `DPI[0]` X-axis DPI value → `DPI[0]` X-axis DPI value
> **doubled**, `0x00` off / `0x01` on → `DPI[0]` Y-axis DPI value → `DPI[0]` Y-axis DPI value
> **doubled**, `0x00` off / `0x01` on.

**What it means for us — the most actionable item in this batch.** The vendor is describing
**5 distinct fields per stage** — `[Enable, X_value, X_doubled, Y_value, Y_doubled]` — which is a
genuinely **different interpretation** of the same 5-byte stride our code already uses:

| | Vendor's T7 answer | `TaipanDpiTable.cs` (current App code) |
|---|---|---|
| Bytes per stage | 5 (matches) | 5 (matches) |
| Byte 0 | Enable | Enable |
| Bytes 1–4 | `[X_value(1B), X_doubled(1B), Y_value(1B), Y_doubled(1B)]` — X and Y independent, each an 8-bit value + a doubling multiplier | `[X_lo, X_hi, Y_lo, Y_hi]` — one 16-bit little-endian CPI register (`CPI / 50`), **X mirrors Y** |

The total table size (41 bytes) and per-stage stride (5 bytes) **agree** — so the current code's
*framing* of the table is right. What's uncertain is whether bytes 1–4 within a stage are really a
16-bit mirrored register (current App assumption, based on the vendor's own worked full-table
write example — see the comment in `TaipanDpiTable.cs`) or an 8-bit-value-plus-doubling-flag pair
per axis (this T7 answer). These two encodings would decode the **same raw bytes into different
DPI values**, so this is not cosmetic.

The vendor's own phrasing is rough (mixes "bit"/"byte", and the arithmetic summary doesn't fully
walk through how 4 fields collapse into exactly 4 bytes), so I would not flip the implementation on
this text alone. **Recommended next step:** read the DPI table back from real hardware (`0x42`)
after writing a known stage via the App, and compare the raw bytes against both interpretations —
this is exactly the verification `protocol.md`'s existing "Open items" list already calls for
(item 1), now with a concrete alternative encoding to test against. If this is confirmed as
independent X/Y with a doubling flag, `TaipanDpiTable.BuildTable`/the register conversion needs a
rewrite (and independent X/Y would also be a **new capability** — the UI currently only carries one
DPI value per stage, mirrored to both axes).

## T8. HID interface/usage for `ReportID 0x05`

**Status:** (A) undocumented / missing

Our problem statement:
> The docs don't say which HID interface/usage page carries the `0x05` driver report.

Our request:
> Document, per transport, which interface/usage page/report ID carries the driver channel.

Vendor answer (original):
> report ID都是一样的05,下面是数据参考图，我们这边的是分开下的

Translation:
> The report ID is the same, `0x05`, [across transports]. Below is a reference data diagram; on our
> side it's issued as separate [per-transport definitions].

**What it means for us:** The "reference data diagram" mentioned isn't present in the CSV export
(it was an embedded image in the original correspondence, lost on export) — **the visual reference
is missing**, but the CSV's own trailing rows (below the Q&A table, see the raw file) supply the
concrete numbers directly:

| Transport | VID | PID | Usage | UsagePage | Feature len | Input len | Output len |
|---|---|---|---|---|---|---|---|
| Wired | `0x248A` | `0xFB01` | `0x0000` | `0xFFEF` | `0x0040` (64) | `0x0040` (64) | `0x0040` (64) |
| 2.4G dongle | `0x248A` | `0x8373` | `0x0000` | `0x0001` | `0x0008` (8) | `0x0021` (33) | `0x0021` (33) |

This directly answers the question T8 asked (better than the prose reply did): the wired path uses
a **vendor-specific 64-byte** usage page (`0xFFEF`), while the 2.4G dongle uses the **Generic
Desktop** usage page (`0x0001`) with **33-byte** input/output reports — matching the 33-byte frame
`protocol.md` already documents. **Action:** fold this table into `protocol.md`'s transport section
so the App's report-discovery logic has a concrete target instead of the BLAZE-style
outLen/featLen heuristic alone.

## T9. Bluetooth frame transport

**Status:** (A) undocumented / missing

Our problem statement:
> The documented frame structure only describes USB HID; whether the same 33-byte frame carries
> over Bluetooth GATT/HID is unstated.

Our request:
> Describe the Bluetooth transport: GATT service/characteristics vs. HID, and any frame-structure
> differences.

Vendor answer (original):
> 程序现在不支持蓝牙

Translation:
> The [driver/host] software currently does not support Bluetooth at all.

**What it means for us:** Reinforces T2 — Bluetooth is not just "writes unsupported", it's **not
supported by the vendor's stack, period**, so there is nothing to reverse-engineer here yet.
Bluetooth stays out of scope for TAIPAN until the vendor ships support and a follow-up spec.

## T10. Factory reset (`0x00` / `0xF3` / `0x80`)

**Status:** (A) scope/commit/transport to confirm

Our problem statement:
> `CMD_Reset 0x00`, `Para1=0xF3`, `Para2=0x80` describes a combined reset, but its scope, commit
> sequence, and which transports it works on are all unconfirmed.

Our request:
> Confirm: does this reset restore DPI/buttons/LED/macros/MsConfig; does it need the
> `ValidBankID` + `0xD3` commit; does it work only over wired, or also over the dongle/Bluetooth?

Vendor answer (original):
> 没有回复单独的恢复出厂设置，默认值需要根据要求去下对应的值才行，或者你先用组合键回复默认后读值

Translation:
> There is no dedicated "restore factory defaults" reply/acknowledgement. Default values need to be
> written by the host according to the requirements — or use the button-combo to restore defaults
> first, then read the values back.

**What it means for us:** Same resolution shape as the BLAZE vendor Q&A's item A
([blaze-vendor-qa-2026-07-27.md](../blaze/blaze-vendor-qa-2026-07-27.md)) — **reset-to-default is
an App-side feature, not a firmware command we can trust.** `0xF3`/`0x80` is not confirmed to do
anything reliable; do not build a "reset" feature around it. Instead: keep a canonical default
profile in the App and push it via the normal write path (DPI table, button map, LED table,
MsConfig), the same way `ApplyDeviceConfigurationAsync`/preset-apply already works. TAIPAN doesn't
currently expose a "reset to default" action in the App — this is a **candidate feature**, not a
bug fix, if we want to offer one.

---

## Summary — what this unblocks / confirms

| Item | Vendor verdict | Our next step |
|---|---|---|
| T1 DPI stage colour (`0x0C`) | Confirmed not implemented in shipped firmware | None — App already treats it as display-only |
| T2 2.4G / Bluetooth writes | Bluetooth unsupported; 2.4G *should* work, vendor offered to debug | **Verified on hardware (2026-07-31): 2.4G writes do NOT land** — reads and writes are both dead on this transport (button-map, DPI table, MsConfig all confirmed). Button assignment requires the wired cable. Candidate: report this back to the vendor per their offer to debug. See [protocol.md](protocol.md#confirmed-on-hardware--24g-dongle-readwrite-behaviour). |
| T3 `0x0011` lag | Not actually answered — vendor just restated indexing | Re-ask the vendor directly; keep relying on push events, not polling `0x0011` |
| T4 On-demand state read | Confirmed impossible — `0x49` is push-only | None — App already relies on push + connect-time state |
| T5 `CMD_SetProfile` (`0x07`) | Confirmed not implemented | None — App never sends `0x07`; update doc hedge to a flat statement |
| T6 Battery percentage curve | Formula given, but for a field that doesn't match our documented mV pair | **Resolved via a different mechanism**: the App doesn't use the vendor's formula or the active `0x08` query at all — it reads the mouse's own unsolicited `0x48` push and interprets `Para1/Para2` as quarter-millivolts, calibrated from real captures. Already implemented (`TaipanStateReport`/`TaipanStateMonitor`). See [protocol.md](protocol.md#what-the-app-actually-does--push-not-active-query-implemented). |
| T7 DPI table layout | Vendor describes independent X/Y + doubling flag, conflicting with our current mirrored-16-bit-register implementation | **Read the DPI table back from hardware and compare both interpretations before changing `TaipanDpiTable.cs`** |
| T8 `ReportID 0x05` interface/usage | Prose answer thin, but the CSV's device-identity rows answer it directly | Fold the VID/PID/Usage/UsagePage/report-length table into `protocol.md` |
| T9 Bluetooth transport | Confirmed unsupported end-to-end | Keep Bluetooth out of scope until the vendor ships support |
| T10 Factory reset | No reliable device-side reset; App must push its own defaults | Candidate feature (not a bug): App-side "reset to default" mirroring the BLAZE resolution |

## Open follow-ups for the vendor

1. **T3 unanswered:** exactly when does the firmware commit a new value to `MsConfig 0x0011`
   relative to the physical button press that changed the stage?
2. **T6:** which byte(s) in the `0x48` battery reply does the `750`/`985` percentage formula apply
   to — is it the same `Para1/Para2` mV pair `protocol.md` documents, or a different field?
3. **T7:** the byte-level breakdown is still ambiguous (mixed "bit"/"byte" wording); could the
   vendor supply an actual annotated hex dump of one written + read-back DPI table entry?
4. **T2:** our own hardware test (2026-07-31) confirms 2.4G writes fail — send the vendor our
   `TelinkMouseService` write path (or a packet capture) as they requested, since this contradicts
   their claim that the protocol is identical across transports.

## Terms

See [`docs/CONTEXT.md`](../../CONTEXT.md) for **TAIPAN**, **Telink**, **MsConfig**,
**DpiSwitch**, **ValidBankID**.
