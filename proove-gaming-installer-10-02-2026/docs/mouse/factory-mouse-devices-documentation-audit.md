# Factory Documentation Audit — BLAZE (X43) and TAIPAN

**Purpose of this document.** This is a **technical review of the documentation and protocol
files the factory supplied**, listing — per model — where the documentation is incomplete,
incorrect, or describes functions that do not behave as documented on the actual hardware, and the
**specific action needed from the factory** to close each gap.

> ## ⚠️ Please read first — what this request IS and IS NOT
>
> **This is NOT a request for the factory to develop any software or a third-party application.**
> A previous version of this file was misread that way. We already have our own firmware-integration
> software.
>
> **This IS a request to correct / complete the protocol documentation and to confirm firmware
> behaviour**, so that our integration matches the functions the factory has declared. For each item
> below the only factory deliverables we need are one of: an **updated/corrected document**, a
> **written explanation of how a function works**, or a **confirmation/correction of the protocol or
> SDK**. No application development is requested.
>
> The factory has stated that all supplied files are correct because they built their own working
> software from them. We do not dispute that their software works — we are asking them to point us to
> **the exact file, command, and byte-level structure** for the functions listed below, because those
> details are either **absent, ambiguous, or contradicted by two of their own documents**, and the
> hardware does not respond to the documented commands as written.

## How to read each row

For every model we record, per issue:

1. **File / protocol** — which supplied file or protocol layer the problem is in
   (HID descriptor, driver protocol document, address-table PDF, SDK/example, or firmware behaviour).
2. **Problem type** — one of:
   - **(A) Missing** — a required function is not documented at all.
   - **(B) Incorrect** — a function is documented, but the description is wrong.
   - **(C) Documented but non-functional** — the function is in the docs but the hardware does not
     perform it when the documented command is sent.
   - **(D) Implementation differs from declared** — the function works, but differently from the
     documentation.
3. **Factory action** — one of: **provide updated file**, **supplement documentation**, **explain
     how the function works**, **change protocol / SDK**.

---

# BLAZE (X43 Wired Gaming Mouse)

- USB VID `0x04D9` / PID `0xA245`; MCU Holtek **HT68FB571**; Easen "param" protocol.
- Factory files referenced: **`X43 Wired Gaming Mouse driver protocol_20260427.pdf`**,
  **`软件驱动协议-20251028-V3.1.xlsx`** (Easen driver protocol), **`X43 Standard Mouse Function
  Specifications 3311.xlsx`**, HT68FB571 datasheet.
- All command processing works (firmware-version query `0x80` returns correct data; DPI, key matrix,
  debounce, lift-off, report rate all verified). The gaps below are specific.

| # | Function | File / protocol | Problem type | What we observe on hardware | Factory action needed |
|---|---|---|---|---|---|
| B1 | **Restore-to-factory-defaults (host-triggered)** | Driver protocol doc `..._20260427.pdf` (vendor notification code table) | **(A) Missing** | The notification **`Type 0xFF, Code 00` = "当前 profile 已恢复默认设置"** (current profile restored to defaults) is documented, but **no host command or button combination that triggers this restore is documented**. A unit once restored this way accidentally (fixed a fault), so a deliberate procedure exists but is not written down. | **Explain how the function works** + **supplement documentation**: state the exact command (or on-device button combo) that restores factory defaults, and which parameter blocks it rewrites (see also B2 "single structure"). |
| B2 | **"Reset to factory settings" as one combined structure** | Driver protocol doc / Easen `软件驱动协议…V3.1.xlsx` | **(A) Missing** | The factory explains reset is a **single structure** — one command/parameter set that applies all related settings together, not per-feature calls. We cannot find **which file describes that combined structure, its byte layout, and whether it needs the commit step**. | **Supplement documentation**: identify the exact file + the command/parameter-block structure that represents "reset to factory", including the values written and any commit/apply command. |
| B3 | **`0x0B SetCurrentDPI` (set active DPI stage)** | Vendor driver / SDK (command appears in the OEM `VS750` driver command map) | **(C) Documented but non-functional** | The command `0x0B SetCurrentDPI(stage)` exists in the vendor command set, but sending it **does not change the active DPI stage** on this firmware. We had to change the stage by rewriting the `param_0` block instead. | **Explain how the function works** or **change/​correct the protocol**: confirm whether `0x0B` is supported on the HT571/X43 firmware, and if not, document the supported way to set the active stage from the host. |
| B4 | **LED effect persistence across power-cycle** | Driver protocol doc (LED / profile section) | **(D) Implementation differs from declared** | The device advertises **"8 Profiles in Flash"**, and `brightness`/`speed` survive a reconnect — but the **effect *type* (`ledEffect`) does not persist**; it reverts to the factory profile on replug. No documented "commit effect to flash" step works (`0x01 Select Profile` was tried and ruled out). | **Explain how the function works**: document the exact flash-commit command/sequence that persists the selected LED effect (and profile) across power-cycle. |
| B5 | **True static (non-animated) LED colour** | Driver protocol doc / Easen protocol xlsx (LED Effect Types) | **(A) Missing** | Every documented effect (`0x01`–`0x09`) animates; even `0x02` "Always On" still breathes. There is **no documented command to set an RGB colour with no animation**. | **Supplement documentation** or **change protocol**: provide a command that sets a fixed RGB colour with the effect engine off, or confirm the device cannot do this. |
| B6 | **Reading parameters back (`0x82` / `0x83`)** | HID interface + driver protocol doc | **(C) Documented but non-functional** | `Read parameter_0/1` (`0x82`/`0x83`) is documented to return the parameter blocks (the doc says `Read parameter_1` returns **4×64 bytes**), but on this firmware/Windows these reads return **zeros or only a 9-byte ACK header — never the block data**. The device exposes **no 64-byte input endpoint**. Result: the host cannot read back DPI/LED/debounce/lift-off state. | **Explain how the function works** + **supplement documentation**: document the intended Windows read path (endpoint / GetFeature chunking) and confirm which reads are actually supported. |
| B7 | **The undocumented 4th parameter-1 block** | Driver protocol doc | **(A) Missing** | `Read parameter_1` is documented to return **4×64 bytes**, but `Set parameter_1` writes at most **3×64**. The **content of the 4th 64-byte block is undocumented**, and we suspect interleaved command/data traffic can corrupt it (a corruption there coincided with our LED fault). | **Supplement documentation**: describe the 4th block's contents and how to re-initialise it. |
| B8 | **`param_0` couples DPI + LED + report-rate in one block** | Driver protocol doc | **(D) Implementation differs from declared / documentation gap** | Changing the DPI stage or report rate via `param_0` also rewrites the **LED-effect byte** in the same 64-byte block; the docs do not warn of this coupling. Writing an incomplete block silently changes unrelated settings (this caused an LED-off fault on our side). | **Supplement documentation**: state explicitly that `param_0` is a single combined structure (bytes = report-rate, DPI stage, LED effect/brightness/speed/colour) and that any write must carry the full current block. |

**BLAZE — the LED-engine question (highest priority).** On our test units the LED does **not light
via any documented command — including the factory's own commands**. We believe this may be a
flash/hardware fault, but we cannot confirm it without B1/B2 (the deliberate factory-reset
procedure) and B4/B6 (persistence + read-back). If the factory can supply the reset procedure and the
read-back path, we can self-diagnose whether a given unit is faulty or mis-programmed.

---

# TAIPAN

- USB VID `0x9354` (`0x248A`); MCU **Telink**; 33-byte HID "driver" protocol.
- Factory files referenced: **`Telink-USB20260205.xlsx`** (marked authoritative / current firmware),
  **`Telink_USB协议_地址表v1.3.pdf`** (EEPROM address tables), **`Telink GM协议 V1.1.doc`** (older
  generic 2.4G spec).
- The wired protocol is well documented and mostly works (Config `0x01`, DPI `0x02`, Buttons `0x03`,
  Macros `0x05`, Battery `0x08` all verified over cable). The gaps below are specific.

| # | Function | File / protocol | Problem type | What we observe on hardware | Factory action needed |
|---|---|---|---|---|---|
| T1 | **Per-DPI-stage indicator colour (`0x0C` / `0x4C`)** | `Telink-USB20260205.xlsx` + address-table PDF | **(C) Documented but non-functional** | `CMD_RW_DPI_Color` (`0x0C`/`0x4C`) is listed, but the address table marks it **"暂保留" (reserved)** and the current firmware **does not apply per-stage DPI colours**. So the function is documented but not usable. | **Change/correct protocol** or **explain**: confirm whether `0x0C` is implemented in the shipping firmware; if reserved, mark it clearly as unimplemented and state the supported way (if any) to set per-stage DPI colours. |
| T2 | **Writing settings over the 2.4G dongle and Bluetooth** | Whole `0x0X` write protocol / transport | **(A) Missing (transport limitation not documented)** | Over the **2.4G dongle and Bluetooth the mouse does not accept host→device write frames** — DPI/polling/button/LED writes have no effect; only **device→host pushes** (`0x48` battery, `0x49` state) are relayed. The docs describe the 33-byte frame generically and **do not state that configuration writes are cable-only**. (Open item #6 already flags BT as undocumented.) | **Supplement documentation**: state clearly which commands are supported on each transport (wired / 2.4G / BT), and whether config writes over dongle/BT are possible at all. |
| T3 | **Active DPI stage byte (`MsConfig 0x0011`) lags reality** | Address-table PDF + firmware behaviour | **(D) Implementation differs from declared** | `DpiSwitch` at `0x0011` is documented as the active stage, but the **stored byte trails the real stage by one physical button press** (the firmware flushes it on its own schedule). Reading it back returns the *previous* stage, which corrupts any read-modify-write of the config block. | **Explain how the function works**: document exactly when the firmware updates `0x0011`, and provide a reliable way to read the **current** active stage. |
| T4 | **On-demand query of the current state (DPI stage / rate / profile)** | `Telink-USB20260205.xlsx` (read commands) | **(A) Missing** | `Report_MouseState` (`0x49`) is **push-only** — it is emitted on a physical button press or link establishment. There is **no documented command that makes the device report its current state on demand**; the read-form commands (`0x44`–`0x4F`) do not answer such a query. | **Supplement documentation** or **change protocol**: provide (or document) a "read current state now" command, analogous to BLAZE's `0x80` state broadcast. |
| T5 | **`CMD_SetProfile` (`0x07`) — switch active profile** | `Telink-USB20260205.xlsx` | **(C) Documented but non-functional** | `0x07` is documented but noted as **not implemented in the current firmware** (may not ACK). Profiles therefore cannot be switched from the host. | **Change/correct protocol**: implement `0x07`, or document that host profile-switching is unsupported and how profiles are meant to be selected. |
| T6 | **Battery percentage** | `Telink-USB20260205.xlsx` (`CMD_GetBATV` `0x08`→`0x48`) | **(A) Missing** | Battery is reported as **millivolts only**; the docs provide **no voltage→percentage curve or thresholds**, so we cannot show a correct battery %. | **Supplement documentation**: provide the official voltage-to-percentage curve (or the thresholds the factory's own software uses). |
| T7 | **DPI table per-axis layout ("double" flag)** | `Telink-USB20260205.xlsx` vs `Telink_USB协议_地址表v1.3.pdf` | **(B) Incorrect / self-contradictory** | The **xlsx annotates a per-axis "翻倍 (double)" flag**, while the **PDF address table shows plain `X_L/X_H/Y_L/Y_H`** pairs. The two layouts have **different strides**, so the DPI-table byte layout is ambiguous. | **Provide updated file** / **reconcile documents**: state the single correct DPI-stage byte layout for the current firmware. |
| T8 | **Which HID interface / usage carries ReportID `0x05`** | HID descriptor / protocol doc | **(A) Missing** | The docs **do not state which HID interface / usage page** carries the `0x05` driver reports. We must discover it heuristically. | **Supplement documentation**: specify the interface / usage page / report IDs for the driver channel on each transport. |
| T9 | **Bluetooth frame carriage** | Protocol doc (transport) | **(A) Missing** | All frames are described for USB HID. Whether the **same 33-byte frame is carried unchanged over BT GATT/HID is not documented** (Open item #6). | **Supplement documentation**: describe the BT transport (GATT service/characteristics or HID) and any framing differences. |

**TAIPAN — "reset to factory settings" (the single-structure question).** TAIPAN **does** document a
combined reset: **`CMD_Reset 0x00`, `Para1 = 0xF3` (restore defaults), `Para2 = 0x80` (all
parameters)** in `Telink-USB20260205.xlsx`. This matches the factory's "single structure"
explanation. What we need confirmed: **(a)** that `0x00 / 0xF3 / 0x80` restores **all** tables (DPI,
button, LED, macro, MsConfig) in one call; **(b)** whether it requires the standard commit sequence
(`ValidBankID` + `0xD3`); and **(c)** whether it works over the dongle/BT or cable only (see T2).
→ **Explain how the function works** (a short confirmation of scope + commit + transport).

---

# Summary of what we are asking the factory for

For **each numbered item above**, one of these — **and nothing else**:

- **Provide an updated / corrected file** (B7, T7 reconciliation).
- **Supplement the documentation** (B2, B5, B8, T2, T4, T6, T8, T9).
- **Explain how a function works** in writing (B1, B3, B4, B6, T1, T3, T5, and the reset scope for
  both models).
- **Correct the protocol / SDK** where a documented command does not run on the shipping firmware
  (B3, B6, T1, T5).

We are **not** asking the factory to write software, and we are **not** asking for a third-party
application. Every item is a documentation, protocol, or firmware-behaviour clarification.

We can supply **full USB captures** for any of the behaviours above (e.g. the BLAZE LED fault
sequences, the TAIPAN dongle write-no-op, the `0x0011` lag) to speed diagnosis.

---

*Internal note (not for the factory): the observations above are grounded in our in-repo device
knowledge base — `docs/mouse/blaze/*` (LED, DPI-stage, OEM VS750 protocol, engineering notes) and
`docs/mouse/taipan/protocol.md` — and in the DeviceHost integration code. See those for the
byte-level detail and USB-capture references behind each row.*
