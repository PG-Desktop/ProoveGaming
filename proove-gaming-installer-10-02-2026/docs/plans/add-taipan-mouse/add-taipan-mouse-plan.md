# Plan: Add the PG TAIPAN mouse (Telink) — one card across transports, then button assignments

Branch: a new branch off `feat/add-per-device-identity` (never merge to `main`).

Protocol reference distilled from the vendor docs: [`docs/mouse/taipan/protocol.md`](../../mouse/taipan/protocol.md).
Glossary terms added in [`docs/CONTEXT.md`](../../CONTEXT.md).

> **Note (2026-08-14).** Commit `85bb67f` (PG-506, "updated DPI design") moved the TAIPAN
> `scroll-click` hotspot callout target up 32px (`TargetY` 262 → 230 in
> `src/ProoveGaming.DeviceHost/appsettings.json`) without a word in the commit or any plan; the
> sibling `left-click`/`right-click` targets stayed at 245. Recording it here so it is a known
> deliberate-looking tweak rather than silent drift — if the wheel callout on the TAIPAN card
> looks misaligned, this line is the first suspect and reverting to 262 is the undo.

## Goal

Support the **PG TAIPAN** mouse as a first-class device alongside BLAZE. TAIPAN is a **Telink**
device (VID `0x248A`) with a completely different wire protocol from BLAZE's Holtek/Easen
platform, and it reaches the host three ways — wired USB, 2.4G dongle, Bluetooth. Today the app
lists TAIPAN from config but cannot control it, and the RGB service factory routes **any** device
whose `DeviceType` contains "Mouse" to `EasenMouseService`, so driving TAIPAN would send Holtek
frames to Telink silicon.

This plan delivers two things, in order: **(1)** one TAIPAN card no matter which transport(s) it
is connected through — with its settings surviving a transport switch — and **(2)** the first
functional slice, **button assignments**, matching the BLAZE button-remap feature. The remaining
capabilities are scoped as follow-up phases.

## Scope

**In**

- **Transport merge:** one Home card per physical TAIPAN whether it arrives over cable, dongle or
  Bluetooth — including a **stable settings identity** so a transport switch does not lose the
  user's saved settings. Driven by a new `MergeTransports` config flag, so BLAZE's two-identical-
  units behaviour (from `feat/add-per-device-identity`) is untouched.
- A `TelinkMouseService` in `ProoveGaming.Infrastructure` implementing the TAIPAN frame format,
  checksum, packet sequencing and the **button map** (`CMD_RW_Button` 0x03 / 0x43).
- Dispatching mice **by config `Name`** in `DeviceRgbServiceFactory` so BLAZE→Easen and
  TAIPAN→Telink (today every mouse hard-routes to Easen).
- Pure, unit-testable `TaipanButtonMap` + `TaipanFrame` encoders (the analogue of `X43KeyMatrix`).
- TAIPAN config gains its capability flags.
- Per-device button hotspots so TAIPAN's Buttons UI points at TAIPAN's image, not BLAZE's
  coordinates.
- Reusing the existing `MouseButton` / `MouseButtonAction` enums — **no new actions** in this
  slice (parity with what BLAZE already offers).

**Out (explicitly deferred — see [Follow-up phases](#follow-up-phases))**

- TAIPAN RGB, DPI, polling-rate, macros, profiles, battery — enumerated below, not built here.
- TAIPAN-only button actions (DPI cycle/±, Profile cycle/±, polling-rate, rapid-fire, LED
  toggle). They need new `MouseButtonAction` values, proto enum values, UI catalog groups and
  localization — a coherent slice of their own.
- **Bluetooth as a control transport.** BT merges into the card for *display* (that already works
  by name), but button writes go over the USB HID paths (cable/dongle) only; the vendor docs do
  not describe BT framing.
- Telling **two** TAIPANs apart. A merged product is one card by construction — see
  [Risks](#risks).
- Any change to BLAZE behaviour.

## Device capabilities (full enumeration from the vendor docs)

Complete capability set discovered in [`protocol.md`](../../mouse/taipan/protocol.md), with the
phase each lands in.

| # | Capability | Protocol | Phase |
|---|---|---|---|
| 1 | **Button map** — 25 slots × 4 bytes, readable back, 24 functions | `0x03` / `0x43` | **This plan** |
| 2 | DPI table — 8 stages, independent X/Y CPI | `0x02` / `0x42` | Phase 2 |
| 3 | DPI indicator colours — RGB per stage | `0x0C` / `0x4C` | Phase 2 |
| 4 | Polling rate — per profile, 125/250/500/1000 Hz | `0x01` / `0x41` (MsConfig) | Phase 2 |
| 5 | Lift-off distance / wheel speed / sleep timers | `0x01` / `0x41` (MsConfig) | Phase 2 |
| 6 | RGB effects — 10 modes, speed, brightness, colour-source | `0x04` / `0x44` | Phase 3 |
| 7 | Macros — 20 slots × 200 B, ~66 actions, 9 action types | `0x05` / `0x45` | Phase 4 |
| 8 | **Profiles — 5, each with its own DPI/buttons/LED/rate** | `0x07`, MsConfig `0x0006` | Phase 5 |
| 9 | Battery voltage + product/sensor identity | `0x08` → `0x48` | Phase 5 |
| 10 | Device state push (profile / DPI / rate / LED) | `0x49` | Phase 5 |
| 11 | Reset / restore factory defaults | `0x00` | Phase 5 |
| 12 | Mouse enable / all-LEDs-off | `0x06` | Phase 5 |

Capabilities TAIPAN has that **BLAZE does not**, and which the current app model cannot express:
readable button map, 5 profiles, per-axis DPI, remappable DPI/rate buttons, and button functions
for DPI/profile/rate/rapid-fire. Profiles (8) are the biggest modelling gap — see
[Open questions](#open-questions).

## The transport-merge problem

TAIPAN's three transports are **three different USB identities** with **three different Windows
Container IDs**:

| Transport | VID | PID | Enumerates as |
|---|---|---|---|
| Wired | `9354` | `64257` | the mouse |
| 2.4G dongle | `9354` | `33651` | the **dongle** |
| Bluetooth | `9354` | `33651` | the BT stack |

Two independent problems fall out of that:

1. **Card splitting.** `feat/add-per-device-identity` changed Home to group HID entries by
   `(ClientName, DeviceKey)` so two identical BLAZE units get two cards. TAIPAN's cable and
   dongle have different `DeviceKey`s → **two cards for one mouse**. (Bluetooth is unaffected:
   `HandleBluetoothDeviceFoundAsync` already matches an existing card by `Name` alone, so BT
   folds into whichever card exists.)
2. **Settings loss on transport switch.** Settings are persisted by
   `(vendor_id, product_id, device_key)`. Both `product_id` **and** `device_key` change between
   cable (`64257`) and dongle (`33651`), so a user who assigns buttons over the cable and then
   switches to the dongle would see the UI fall back to defaults. (The *mouse* keeps the map — it
   lives in EEPROM — so this is a UI/device mismatch, not lost hardware state, but it is still
   plainly wrong.)

Both must be solved for TAIPAN to be usable, so both are in scope here.

## Options considered

### A. How to keep one card across transports

1. **Explicit `MergeTransports` config flag** — *recommended*. A product marked
   `MergeTransports: true` is always one card, keyed by `ClientName`; `DeviceKey` is used only to
   pick the live routing target, never to split cards. Products without the flag keep today's
   `(ClientName, DeviceKey)` per-unit behaviour. Explicit, stable, and expresses the real intent
   ("this product is one device reachable several ways"). Cost: two TAIPANs cannot be told apart
   (see Risks) — acceptable, and genuinely unknowable anyway (you cannot tell which dongle belongs
   to which mouse).
2. *Split by `DeviceKey` only when the same `(VID,PID)` yields more than one key* — rejected,
   though it was this plan's earlier recommendation. It is **unstable**: unplugging one of two
   BLAZE units flips that `(VID,PID)` from two keys to one, changing the surviving card's
   grouping identity mid-session and orphaning its card/settings.
3. Merge any entries sharing a `ClientName` — rejected: that is exactly what breaks two BLAZE.

### B. How to keep settings stable across transports

1. **Canonical settings identity** — *recommended*. Separate the two identities a card carries:
   - **routing** — `(VendorId, ProductId, DeviceKey)` of the *live* transport; what commands use.
   - **settings** — `(VendorId, SettingsProductId, SettingsDeviceKey)`; stable across transports.

   For a merged product the DeviceHost stamps every catalog entry with the same
   `settings_product_id` (the product's primary/wired PID) and the card uses `SettingsDeviceKey = ""`.
   So all TAIPAN transports persist to one bucket, `(9354, 64257, "")`, while commands still reach
   whichever transport is live. Reuses the existing table schema — no second migration.
2. Re-key persistence to a product string (drop vid/pid) — rejected: another schema migration one
   step after the `device_key` migration, and it would regress BLAZE's per-unit settings.
3. Read the button map back from the device (TAIPAN supports `0x43`) instead of persisting —
   attractive and uniquely possible on this hardware, but it only solves *buttons*; DPI/RGB in
   later phases still need stable persistence. Recorded as a Phase-2+ improvement.

### C. Where the TAIPAN protocol lives

1. **New `TelinkMouseService` implementing `IMouseRgbService`** — *recommended*. Mirrors
   `EasenMouseService`: one class per device family, constructed with its `HidDevice`, cached per
   unit by the factory. The interface's method *signatures* are already device-agnostic; only its
   XML comments are BLAZE-flavoured. Cost: those comments must be generalised.
2. Extend `EasenMouseService` with a protocol switch — rejected. Two unrelated wire formats in one
   class; every method becomes an `if (isTelink)` fork, risking the working BLAZE path.
3. New `ProoveGaming.Telink` project — rejected. A new `.slnx` project for one service when
   `Infrastructure` already houses every device service.

### D. How the factory picks the service

1. **Dispatch mice by config `Name`** (`"BLAZE"` → Easen, `"PG TAIPAN"` → Telink) — *recommended*.
   Exactly what the keyboard branch already does (`Aress`/`Cyrex`/`Raster`/`DragonFang`) — an
   established, symmetric pattern. Unknown mouse names keep falling back to Easen, preserving
   today's behaviour.
2. Add a `Protocol` field to `DeviceMap` — cleaner in principle, but a new config concept for a
   two-device problem, diverging from the keyboard precedent. Revisit at the third mouse family.

### E. Button-function encoding

1. **Pure static `TaipanButtonMap`** — *recommended*. Direct analogue of `X43KeyMatrix`, the seam
   that proved right: byte-exact, hardware-free unit tests.
2. Encode inline in `TelinkMouseService` — rejected; precisely what was refactored *out* of
   `EasenMouseService` to make it testable.

### F. TAIPAN hotspots (Buttons UI)

1. **Per-device hotspot builder keyed on device name** — *recommended* for this slice.
   `ApplyDefaultPartHotspots` currently applies BLAZE's hard-coded coordinates to *any* device
   whose type contains "mouse", so TAIPAN would show BLAZE's hotspots over TAIPAN's image.
2. Move hotspots into config — better long-term (data, not code) but a larger refactor of the
   catalog contract; recorded as a follow-up.

## Recommended approach

Two ordered parts.

**Part 1 — one card, stable settings.** Add `MergeTransports` to the config/catalog. Home groups
HID entries by `(ClientName, DeviceKey)` **only when the product is not merged**; merged products
group and match by `ClientName` alone, so cable + dongle (+ BT, already by name) land on one card.
The card keeps its existing wired-preferred "best transport" pick for the *routing* identity and
gains a *settings* identity (`SettingsProductId`, `SettingsDeviceKey`) that is stable across
transports. `DeviceDetailsViewModel` uses the settings identity for persistence calls and the
routing identity for device commands.

**Part 2 — buttons.** Add `TaipanFrame` + `TaipanButtonMap` (pure encoders), then
`TelinkMouseService` implementing `SetButtonBindingsAsync` over the multi-packet write sequence,
then factory dispatch by name, then config flags + hotspots. Reuse the existing
`MouseButton`/`MouseButtonAction` enums and the whole App button pipeline unchanged — the
device-agnostic `ButtonBinding` contract already carries everything this slice needs.

Only the five shared physical buttons (Left, Right, Wheel, Forward, Back) are exposed, mapped to
TAIPAN table indices 1–5. TAIPAN's Buttons 6/7 (DPI, polling rate) are remappable on this hardware
but have no `MouseButton` value; they arrive with the Phase-2 action set.

Implementation order (each step builds and is coherent alone):

1. `MergeTransports` config → catalog → Home grouping/matching (one card).
2. Canonical settings identity on the card + `DeviceDetailsViewModel` persistence calls.
3. `TaipanFrame` + `TaipanButtonMap` + unit tests.
4. `TelinkMouseService` (`SetButtonBindingsAsync`; other members log + return `false`).
5. Factory dispatch by name; TAIPAN config flags; TAIPAN hotspots.

## File-level change list

### 1. Documentation (already created by this planning step)

- `docs/mouse/taipan/protocol.md` *(new — done)* — distilled English protocol reference.
- `docs/CONTEXT.md` *(edited — done)* — glossary entries for TAIPAN, Telink, BtFuncNum,
  ValidBankID, Container ID, deviceKey, Easen, BLAZE, Profile, Key matrix.

### 2. Transport merge — config & contract

- `src/ProoveGaming.Infrastructure/Classes/DeviceMap.cs` *(edited)* — add
  `bool MergeTransports` — "this product is one physical device reachable over several
  transports; never split its card by device key".
- `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto` *(edited)* — `DeviceCatalogEntry`
  gains `bool merge_transports = 15;` and `int32 settings_product_id = 16;` (the stable PID a
  merged product persists under). Regenerated on build; both sides recompile.
- `src/ProoveGaming.DeviceHost/Services/HidDeviceMapRegistry.cs` *(edited)* — map
  `MergeTransports` onto the catalog entry, and compute `settings_product_id` once per product:
  for a merged product, the PID of its **wired** entry (falling back to the first entry for that
  `Name`); for a non-merged product, its own PID. Stamp the same value on every entry of that
  product so all transports agree.

### 3. Transport merge — App

- `src/ProoveGaming.App/ViewModels/DeviceCardViewModel.cs` *(edited)* — add
  `int SettingsProductId` and `string SettingsDeviceKey` (the stable persistence identity;
  `VendorId` is shared across a product's transports). `VendorId`/`ProductId`/`DeviceKey` keep
  their meaning: the **live routing** target.
- `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` *(edited)* —
  - group HID entries by `(ClientName, mergeTransports ? "" : DeviceKey)` so a merged product is
    one group across transports;
  - match the existing card the same way — by `Name` alone for a merged product, otherwise the
    current exact-then-adopt `(Name, DeviceKey)` logic;
  - relax the disconnect sweep so a merged card stays connected while **any** of its transports is
    present;
  - set the card's routing identity from the wired-preferred `bestEntry` (unchanged) and its
    settings identity from `entry.SettingsProductId` / `""` when merged.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Buttons.cs`,
  `.Rgb.cs`, `.Settings.cs` *(edited)* — persistence calls (`GetButtonBindings`,
  `SetButtonBinding`, `GetRgbState`, `SaveRgbState`, `GetDeviceSettings`, `SetDeviceSetting`) use
  `Device.SettingsProductId` / `Device.SettingsDeviceKey`; **device command** calls keep
  `Device.ProductId` / `Device.DeviceKey`. The in-memory `_rgbStateByDevice` cache keys on the
  settings identity too.

### 4. Infrastructure — encoders

- `src/ProoveGaming.Infrastructure/Classes/TaipanFrame.cs` *(new)* — pure static frame builder:
  `Build(byte cmd, byte p1, byte p2, byte p3, byte p4, ReadOnlySpan<byte> payload)` → the 33-byte
  report incl. the `dummy` checksum. Separate from the service so checksum/framing are testable
  without a device.
- `src/ProoveGaming.Infrastructure/Classes/TaipanButtonMap.cs` *(new)* — pure static encoder:
  `EncodeAction(MouseButtonAction, MacroDefinition?)` → 4-byte `[BtFuncNum, BtModify, BtAlpha1,
  BtAlpha2]`; `ButtonToIndex(MouseButton)` → table index (Left=1 … Back=5, else −1);
  `BuildTable(IReadOnlyList<ButtonBinding>)` → full payload (max-index header + entries) built
  from the factory-default map with requested slots overlaid; constants for table offsets,
  `ValidBankID` and packet-sequence bytes.

### 5. Infrastructure — device service

- `src/ProoveGaming.Infrastructure/Services/TelinkMouseService.cs` *(new)* — implements
  `IMouseRgbService` for TAIPAN. This slice implements `SetButtonBindingsAsync` (multi-packet:
  `0xD1` header → `0xD2` entries → ValidBankID at `0x007F` → `0xD3` commit), serialising writes
  with a `SemaphoreSlim` as `EasenMouseService` does. All other members log "not implemented for
  TAIPAN yet" and return `false` (`SyncStateFromDevice` no-ops) until their phase.
- `src/ProoveGaming.Infrastructure/Interfaces/IMouseRgbService.cs` *(edited)* — generalise XML
  comments that currently describe BLAZE internals (param_0/param_1/key-matrix) as if they were
  the contract. Signatures unchanged.
- `src/ProoveGaming.Infrastructure/Services/DeviceRgbServiceFactory.cs` *(edited)* — in the
  `DeviceType.Contains("Mouse")` branch dispatch on config `Name`: `"PG TAIPAN"` →
  `CreateTelinkMouseService`, else the existing Easen path (unchanged default).
  `CreateTelinkMouseService` selects TAIPAN's HID interface, filters by `deviceKey` exactly as the
  Easen path does, and caches per `(vid, pid, deviceKey)`.

### 6. Config / catalog values

- `src/ProoveGaming.DeviceHost/appsettings.json` *(edited)* — on all three `PG TAIPAN` entries
  (wired `64257`, dongle `33651`, BT `33651`) add `"MergeTransports": true` and
  `"SupportsButtonRemap": true`. Leave `SupportsRgb` / `SupportsMacros` **false** until their
  phases, so the UI does not offer sections that do nothing. Add `"DpiStages"` only once real
  values are confirmed (see [Open questions](#open-questions)).

### 7. App — hotspots

- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs` *(edited)* — split
  `ApplyDefaultPartHotspots` so hotspots are chosen per device (BLAZE coordinates for BLAZE,
  TAIPAN coordinates for TAIPAN) instead of applying BLAZE's to every mouse. TAIPAN's hotspot ids
  reuse the existing ones (`right-click`, `scroll-click`, `side-forward`, `side-back`, plus fixed
  `left-click`) so `HotspotIdToButton` needs no change.
- `src/ProoveGaming.App/Assets/Taipan.svg` *(existing)* — reused; hotspot coordinates derive from
  it. **No RGB vector variant exists** (`RgbImagePath` unset) — fine while RGB is out of scope.

**No changes** to: `UsbHidDeviceService`, `DeviceService`, `LocalStorageService` schema,
`MouseButton`/`MouseButtonAction`. The existing `ButtonBinding` contract expresses this slice
end-to-end; the only proto change is the two `DeviceCatalogEntry` fields in §2.

> **Battery behaviour superseded (2026-08-17, branch `fix/Taipan-connection-and-battery-fixes`).**
> This plan describes battery as a level read over the transport and shown on the card. Three things
> changed since, all measured on hardware and recorded in `docs/mouse/taipan/protocol.md`:
>
> - **The cable shows no battery at all.** Over USB the mouse reports charge VOLTAGE, which rises
>   while charging and pins the percentage at 100 %. TAIPAN's wired transport therefore declares
>   `SupportsBattery: false` in `AllowedDevices`, and the App hides the indicator for it. The dongle
>   and Bluetooth are unaffected.
> - **"No mouse behind the dongle" is now detectable.** The receiver answers a battery request with a
>   ZERO reading when the mouse is switched off, out of range or flat ([[Mouse-absent reply]]), so
>   the card stops showing the last known percentage instead of keeping it indefinitely.
> - **The level is pushed, not only polled.** `TaipanStateMonitor` raises a [[BatteryChange]] the
>   moment it sees one and `DeviceService` streams it, so the card updates in about a second rather
>   than on the App's 60-second poll. The poll remains as the backstop.
>
> Over Bluetooth the App shows the mouse's own GATT percentage rather than converting a raw value, so
> the two transports can differ by a point — see the protocol reference for the measurements and the
> remaining curve error.

## Acceptance criteria

1. Given a TAIPAN connected by cable **and** its dongle plugged in at the same time → Home shows
   **exactly one** "PG TAIPAN" card.
2. Given that one card → unplug the cable (dongle still connected) → the card **stays** (does not
   disconnect, is not duplicated), and remains controllable.
3. Given a button assigned on TAIPAN over the **cable** → switch to the **dongle** → reopen the
   Buttons section → the same assignment is still displayed (stable settings identity).
4. Given a TAIPAN paired over **Bluetooth** while its dongle is connected → still exactly one
   card, showing both transports.
5. Given **two identical BLAZE** units → Home still shows **two** cards, each with its own
   settings → i.e. the `MergeTransports` flag did not regress `feat/add-per-device-identity`.
6. Given TAIPAN's card → open it → the **Buttons** section is available; RGB and Macros are **not**
   offered.
7. Given TAIPAN's Buttons section → hotspots sit over the TAIPAN image at TAIPAN's own button
   positions (not BLAZE's coordinates).
8. Given TAIPAN's Buttons section → assign "Volume Up" to **Forward** → the DeviceHost log shows a
   TAIPAN button write with no error → pressing the physical Forward button raises system volume.
9. Given a BLAZE connected (with or without TAIPAN) → its RGB, DPI, Buttons and Macros behave
   exactly as before → no regression to the Easen path.
10. Given both a BLAZE and a TAIPAN connected → assigning a button on one does **not** change the
    other, and each write reaches its own unit.
11. `dotnet build ./src/ProveGaming.slnx` succeeds and `dotnet test ./src/ProveGaming.slnx` is
    green, including the new `TaipanFrame` / `TaipanButtonMap` tests.

## Test strategy

This is an `add-*` feature; the following automated coverage is **required**. It splits on the
pure-logic vs hardware line — the byte encoders and the grouping rule are testable, real HID I/O
is not.

- `tests/ProoveGaming.Infrastructure.Tests` — **new** `TaipanFrameTests`:
  - `Build` returns exactly 33 bytes, `Byte0 = 0x05`, `Byte2 = 0xFA`.
  - **Checksum golden table**: `dummy == (driverID + cmd + p1 + p2 + p3 + p4) ^ 0x66`, byte-wide,
    carry discarded. Assert against **at least three real vendor frames** from `protocol.md`
    (e.g. `05 B6 FA 03 00 00 02 D1 …` → `dummy == 0xB6`). This is the single most
    breakage-prone detail — BLAZE's firmware ignores its checksum, TAIPAN's does not.
  - Payload longer than 25 bytes is rejected per the chosen contract.
- `tests/ProoveGaming.Infrastructure.Tests` — **new** `TaipanButtonMapTests`:
  - `EncodeAction` full table: every supported `MouseButtonAction` → its exact 4 bytes
    (`LeftClick` → `81 00 00 00`, `Back` → `84 00 00 00`, `Forward` → `85 00 00 00`,
    `VolumeUp` → `8E 00 E9 00`, `MyComputer` → `8F 08 08 00`, …).
  - `EncodeAction(Unspecified)` → null; **completeness**: iterate
    `Enum.GetValues<MouseButtonAction>()` and assert every value except `Unspecified` is either
    encodable or explicitly listed unsupported-on-TAIPAN — the same "added an action, forgot to
    encode it" guard the BLAZE encoder has.
  - `ButtonToIndex`: Left→1, Right→2, Wheel→3, Forward→4, Back→5, `Unspecified`→−1.
  - `BuildTable`: byte-exact against the **factory default map** (`81/82/83/85/84/88` + Button 7
    `97 01`, header `0A 00`); overlaying one binding changes only that entry's 4 bytes and leaves
    Buttons 6/7 and the wheel entries untouched (TAIPAN's analogue of BLAZE's "fixed slots must
    not drift" test).
- `tests/ProoveGaming.App.Tests` — **new** grouping tests (the discovery handler takes a
  `List<HidDeviceResponse>`, so it is reachable without a live gRPC channel):
  - two responses, same `ClientName`, **different** `DeviceKey`, `merge_transports = true`
    (TAIPAN cable + dongle) → **one** card, and its settings identity equals
    `settings_product_id` with an empty settings key.
  - two responses, same `ClientName`, different `DeviceKey`, `merge_transports = false`
    (two BLAZE) → **two** cards — the regression guard for criterion 5.
  - a merged card whose live transport changes (cable entry disappears, dongle remains) keeps the
    same card instance and the same settings identity, and its routing identity follows the
    surviving transport.
- **Not automated** (covered by QA): `TelinkMouseService` opening the correct HID interface, the
  real multi-packet write + ValidBankID + `0xD3` commit sequence, Bluetooth pairing, and anything
  needing the physical mouse. `TelinkMouseService` itself gets no unit tests for the same reason
  `EasenMouseService` has none — it is I/O; the value lives in the extracted encoders.

## QA (manual test steps)

1. Plug in the TAIPAN mouse with its USB cable **and** plug its 2.4G dongle into another port.
   Launch the app. **Expected:** exactly **one** "PG TAIPAN" card on the home screen — not two.
2. Unplug the USB cable, leaving the dongle connected. **Expected:** the same single card stays on
   screen and stays usable — it must not disappear, duplicate, or show as disconnected.
3. Pair the TAIPAN over Bluetooth as well. **Expected:** still exactly one card.
4. Open the TAIPAN card. **Expected:** a "Buttons" section is available in the left menu. There is
   no "RGB Lighting" and no "Macros" section yet (not supported for this mouse yet).
5. Open the Buttons section. **Expected:** the picture is the TAIPAN, and the clickable button
   markers sit on the correct physical buttons of that picture (left, right, wheel, and the two
   side buttons) — not floating in the wrong places.
6. Click the **Forward** side-button marker and choose **Volume Up**. **Expected:** the option
   highlights; no error, no freeze.
7. Press the physical Forward side button. **Expected:** Windows volume goes up.
8. With the cable connected, assign **Volume Up** to Forward. Now unplug the cable and use the
   dongle, and reopen the Buttons section. **Expected:** Volume Up is still shown as assigned —
   settings must survive the transport switch.
9. Reassign Forward back to **Side Forward**, close the app fully, reopen it, open the Buttons
   section. **Expected:** Side Forward is still shown.
10. Regression — plug in a BLAZE mouse. Open its card. **Expected:** RGB, DPI, Buttons and Macros
    all work exactly as before; changing a BLAZE button still works.
11. Regression — connect **two** BLAZE mice at once. **Expected:** **two** separate cards, and a
    button change on one does not affect the other.
12. Regression — with a BLAZE and a TAIPAN connected together, change a button on the TAIPAN.
    **Expected:** only the TAIPAN changes.

## Review focus

- `gRPC contract` — two new `DeviceCatalogEntry` fields; both sides must regenerate, and the
  claim that no *command* message changes must hold.
- `thread-safety` — `TelinkMouseService` is cached per unit in the singleton factory and reached
  from gRPC handlers; its multi-packet write must serialise or the `0xD1`/`0xD2`/`0xD3` state
  machine interleaves and corrupts the table.
- `null-safety` — `deviceKey` may be empty; TAIPAN's config lacks fields BLAZE has
  (`RgbImagePath`, `SupportedMouseSettings`), and `settings_product_id` may be `0` on catalog
  entries written before this change — every read must tolerate absent values.
- `resource disposal` — HID streams opened per write must be disposed; no handle leak when a
  TAIPAN disconnects mid-write.

## Performance & quality

- **No new hot paths.** Button writes are user-initiated and rare. The DPI/LED poll loops are not
  touched (TAIPAN does not participate in `DpiStageMonitor` yet — that is BLAZE-specific via its
  Col04 path filter).
- **Allocations:** `TaipanFrame.Build` fills a single 33-byte buffer per packet; a button write is
  a handful of packets, so no pooling is warranted. Do not allocate per byte.
- **Async:** the write path is `async` end-to-end with a `SemaphoreSlim` (never `lock`) around the
  whole multi-packet sequence.
- **Correctness over cleverness:** `0xD1` erases the sector, so `BuildTable` must always emit the
  *whole* table, never a partial patch.
- The Home grouping runs per discovery pass over a handful of devices — a dictionary lookup per
  entry is ample; no optimisation needed.

## Risks

- **Two TAIPANs become one card.** A direct, accepted consequence of `MergeTransports`: the flag
  says "this product is one device", so two TAIPAN units would collapse into a single card and
  share settings. Unavoidable without pairing information (nothing links a dongle to its mouse).
  Documented, not solved. BLAZE (unflagged) keeps full per-unit behaviour.
- **`0xD1` erases the sector.** A partial button write silently destroys the rest of the map.
  Mitigated by always writing the full table from the factory baseline.
- **Checksum is mandatory.** BLAZE's firmware ignores its checksum, so the codebase has no habit
  of computing one. A wrong `dummy` will most likely make TAIPAN silently ignore every frame — the
  most probable "nothing happens" failure mode.
- **Interface discovery is unspecified.** The vendor docs never say which HID interface carries
  ReportID `0x05`. The Easen path guesses via `outLen == 65 && featLen == 9`; TAIPAN's heuristic
  must be found on hardware and may differ between cable and dongle.
- **Settings identity assumes a shared VID.** The canonical identity keeps `VendorId` and varies
  only `ProductId`; that holds for TAIPAN (all transports are VID `9354`) but would break for a
  product whose dongle used a different VID. Assert/log if a merged product's entries disagree on
  VID.
- **Per-axis DPI layout is ambiguous** between the two vendor docs (Phase 2 risk, not this slice).
- **Vendor-doc conflicts** — the GM `.doc` disagrees with the xlsx on function `0x1F`, macro
  capacity and profile count. `protocol.md` records the xlsx as authoritative; if hardware
  disagrees, correct the doc rather than special-case the code.

## Open questions

1. **TAIPAN's real DPI stages** — unknown, so no `DpiStages` is added to config yet and TAIPAN
   falls back to the App default set. What are the actual values, and how many stages?
2. **Which physical buttons does the shell have,** and which table index is each? The table has 25
   slots; the factory map fills 1–7 + 23/24. This slice assumes 1–5 are Left/Right/Middle/Forward/
   Back per the vendor default map — confirm on hardware before trusting the hotspots.
3. **Profiles (5).** The app has no concept of device profiles. Expose them, or pin profile 0?
   Recommendation: **pin profile 0** for phases 1–4 and design profiles properly in Phase 5.
4. **Should `MergeTransports` also apply to keyboards** with dongle+BT variants? Nothing needs it
   today; the flag is opt-in, so existing devices are unaffected.
5. **Serial number** — do TAIPAN units report a unique HID serial? Not needed while merged, but it
   would be the only route to ever telling two TAIPANs apart.

## Follow-up phases

Sibling plans to be written when this slice lands; each cross-links back here.

- **Phase 2 — `add-taipan-dpi-and-rate`**: DPI table + per-stage colours + polling rate + MsConfig
  settings (lift-off, wheel speed). Also introduces the TAIPAN-only button actions (DPI cycle/±,
  polling-rate) once `MouseButtonAction` is extended. Consider reading state back from the device
  (`0x41`/`0x42`/`0x43`) rather than trusting the App's DB.
- **Phase 3 — `add-taipan-rgb`**: the 10 LED effect modes, speed, brightness, colour-source.
  Needs a TAIPAN RGB vector asset.
- **Phase 4 — `add-taipan-macros`**: 20 slots × 200 B, the 9 action types (incl. 16-bit moves and
  loops, which the current macro model lacks).
- **Phase 5 — `add-taipan-profiles-and-state`**: 5 profiles, battery voltage→%, device-state push
  (`0x49`), reset/restore.
- **Follow-up — hotspots as data**: move per-device hotspot coordinates out of
  `DeviceDetailsViewModel` into config, once a third device makes the code branch untenable.

## Review findings

### Round 1 — Ship: yes

#### Code
- [ ] `RecordToViewModel` doesn't stamp `MergeTransports` (or `DpiStages`) on rehydrated
  cards — `HomeViewModel.cs` (~565) — Should-fix. Benign today (a restored card has an
  empty `DeviceKey`, so the sweep's `IsNullOrEmpty` clause and the adopt-empty-key match
  route it correctly, and `ApplyDeviceIdentity` corrects the flag at first discovery), but
  the window between restore and first discovery relies on that coincidence.
- [ ] `TaipanStateMonitor.TryGetBattery` falls back to the newest reading for the vendor
  id — `TaipanStateMonitor.cs` — Should-fix (follow-up). Two identical TAIPAN units would
  show each other's battery; fine for the single-unit case and documented in code.
- [ ] Battery % curve assumes quarter-millivolt raw units (readings ~4.0 V ≈ 81%) —
  `TaipanStateReport.cs` — Nit. One full-charge reference reading (raw ≈ 1050 expected)
  calibrates or refutes it; constants live in one place.
- [ ] TAIPAN hotspot coordinates are a first-pass placement — `DeviceDetailsViewModel.cs`
  — Nit, QA-tunable by design (plan QA step 5).
- [ ] Reviewed scope drift, both user-approved: `MergeTransports` also applied to the three
  DragonFang entries (same wired+dongle split shape; omitting it would regress that device),
  and the battery push-monitor (`TaipanStateReport`/`TaipanStateMonitor`/`GetBatteryLevel`)
  landed without a plan doc — parser is unit-tested, monitor mirrors `DpiStageMonitor`.

#### Tests
- [ ] HomeViewModel grouping tests declared required by this plan's Test strategy are not
  implemented (one card when merged; two cards for two BLAZE; transport switch keeps the
  card + settings identity) — App.Tests has no ViewModel harness yet; behaviour was
  verified on hardware instead. Implement when a VM test harness exists, or descope
  explicitly.
- [x] Frame checksum golden table (5 real vendor frames), button-map encoding,
  factory-table snapshot, settings/state parsers — covered
  (`TaipanFrameTests`, `TaipanButtonMapTests`, `TaipanStateReportTests`).
