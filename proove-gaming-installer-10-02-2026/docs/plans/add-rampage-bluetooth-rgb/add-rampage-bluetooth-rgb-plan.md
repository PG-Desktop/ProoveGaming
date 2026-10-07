# Plan: Rampage — RGB control over Bluetooth

## Outcome (final, 2026-08-13)

The plan's original target — RGB over **Bluetooth** — was proven impossible at the firmware
level, and the underlying user need (wireless RGB control) was delivered over the **2.4G
receiver** instead. Confirmed working in the App on real hardware by the developer on 2026-08-13.

What actually shipped, in order:

1. **Identity plumbing (as planned, stage 2):** additive `vendor_id`/`product_id` on the gRPC
   `DeviceCatalogEntry`, filled by `HidDeviceMapRegistry`; `HomeViewModel.ApplyBluetoothIdentity`
   stamps Bluetooth cards; `DeviceRgbServiceFactory.SelectConfigRow` gained a Bluetooth-keyboard
   fallback; `SmartInfiniteInterfaceSelector` prefers non-Bluetooth paths and recognizes all
   three Windows Bluetooth path forms (BTHENUM, BTHLE*, and the HOGP service-UUID form
   `{00001812-…}` observed on real hardware). Review round 1 findings S1–S3 fixed.
2. **Bluetooth probe (`rampage-bt`) — outcome C:** over BLE the board enumerates as
   `0x28E9:0x3000` and advertises the vendor collection (col06, 64/64, usage `FF55:0202`,
   Output id `0x06`), but every Windows write API fails — the advertised Output report has no
   writable GATT characteristic behind it. Corroborated by a full documentation sweep: factory
   clarification C12 lists only wired + 2.4G transports; the vendor's own wireless-driver spec is
   receiver-only; the TAIPAN vendor Q&A states Bluetooth is unsupported outright. Fallback
   applied: Rampage's Bluetooth transport is `ProductId "12288"` + `SupportsConfiguration: false`
   (honest read-only details page over BT, the TAIPAN pattern). Evidence:
   `docs/knowledge-base/rampage-keyboard-protocol/bluetooth-findings.md`.
3. **2.4G receiver path (scope extension, replaces the BT goal):** the documentation sweep
   surfaced the untranslated wireless-receiver sheet of the vendor workbook (now
   `docs/knowledge-base/smart-infinite-protocol/wireless-receiver-protocol.md`); the new
   `rampage-24g` probe confirmed on hardware that the receiver (`0x056D:0xC077`) passes the
   keyboard's `0x06` command channel through unchanged — the production
   `RampageKeyboardRgbService` write persists as-is. Delivered as a catalog-only change: a
   `Dongle2_4G` transport for Rampage (`VendorId "1389"`, `ProductId "49271"`,
   `BatteryCommand 31`) in `src/ProoveGaming.DeviceHost/appsettings.json`. **No service code
   changes were needed for 2.4G.**

Known limitations carried forward (see Round 1 follow-up 2 in the review journal):
- Wired and 2.4G sessions keep **separate settings buckets** — the receiver's foreign VID blocks
  `MergeTransports` (registry enforces one VendorId per merged product). Follow-up if product
  wants them unified.
- Battery over the receiver is configured (`BatteryCommand 31`) but **unverified** — if QA sees a
  perpetual red battery icon on the 2.4G card, drop battery support for that transport.
- Key remapping / macros over 2.4G stay untested (the C12 24-byte chunking presumably applies to
  the large table transfers; RGB's 64-byte feature area did not need it).

## Goal

When the Rampage keyboard is connected over Bluetooth, changing RGB (effect, colour, brightness,
speed, on/off) from the App must reach the hardware the same way it does over USB. Today the RGB
section is shown and fully interactive for a Bluetooth-connected Rampage (its catalog entry
declares `SupportsRgb: true` and `SupportsConfiguration` defaults to true), the in-app glow
preview animates — but the device write silently fails: the Bluetooth card has no VID/PID
(`FillBluetoothCard` never sets one, and `BluetoothDeviceResponse` carries none), so
`SetDeviceLedColor` arrives at the DeviceHost with `VendorId=0 / ProductId=0`,
`DeviceRgbServiceFactory` finds no config row and returns `Success=false`. The user sees a UI that
pretends to work. This plan makes the write actually reach the keyboard over the Bluetooth link.

## Scope

**In:**
- A hardware probe that settles how (and whether) the SmartInfinite vendor channel is reachable
  while Rampage is connected over Bluetooth LE, and a knowledge-base doc recording the findings.
- Routing the App's RGB write for a Bluetooth-connected Rampage to the DeviceHost with a real
  device identity (VID/PID), and resolving an RGB service for it in `DeviceRgbServiceFactory`.
- Keeping ONE persisted RGB settings bucket across USB ↔ Bluetooth for the same physical board.
- Re-applying persisted RGB when the board (re)connects over Bluetooth, matching USB behaviour.

**Out:**
- Key remapping and macros over Bluetooth (the 384-byte `0x18` transfer; wired-only today, stays
  wired-only — see `docs/knowledge-base/rampage-keyboard-protocol/hardware-findings.md`).
- Other keyboards' Bluetooth transports (T-Racer BT stays as-is; its wired-only `SupportsRgb`
  override is deliberate).
- Battery over Bluetooth (already served by GATT `0x180F`).
- Any installer/uninstaller change (none needed).

## Background — how the pieces sit today

- **USB path (works):** `DeviceDetailsViewModel.WriteRgbToDeviceAsync` →
  `UsbHidDeviceService.SetDeviceLedColorAsync(vid, pid, deviceKey, …)` → gRPC `SetDeviceLedColor`
  → `DeviceRgbServiceFactory.CreateForDevice` (matches a config row with `!m.HasBluetooth` by
  VID/PID) → `RampageKeyboardRgbService` → `SmartInfiniteInterfaceSelector` picks the vendor HID
  interface (usage `0x20` / page `0xFF87`) → `0x10/0x15/0x1D/0x11` packet session.
- **Bluetooth discovery:** `BluetoothDeviceDiscoveryService` (InTheHand, BLE GATT) tracks the
  board by *name* and reads battery over GATT. The resulting `BluetoothDeviceResponse` has no
  VID/PID; `HomeViewModel.FillBluetoothCard` therefore leaves the card's identity at 0/0.
- **Config:** Rampage's `AllowedDevices` entry (VID 10473 / PID 12888) has two transports, Wired
  and Bluetooth; the Bluetooth row inherits the same VID/PID and `SupportsRgb: true`.
- **The known unknown:** whether Windows exposes the SmartInfinite vendor collection as an
  openable HID interface while the board is on BLE (HID-over-GATT), and under which VID/PID.
  `docs/knowledge-base/rampage-keyboard-protocol/hardware-findings.md` records that a
  BT-connected Rampage reaches the App with VID=PID=0, which suggests the HID stack did *not*
  surface it with the wired identity on the tested unit — but no probe has ever enumerated the
  HID tree in that state. Device Info (`0x12`) declares the board USB+BLE+2.4G, and the vendor's
  own driver family supports BLE, so a reachable vendor channel is plausible and must be proven,
  not assumed.

## Options considered

1. **Reuse the SmartInfinite HID path over BLE (HID-over-GATT), probe-first.** If the vendor
   collection (usage `0x20` / page `0xFF87`) is enumerable and writable while on Bluetooth,
   `RampageKeyboardRgbService` and the whole `0x15` write path work unchanged — the work reduces
   to identity plumbing (give the BT card a VID/PID, let the factory resolve a BT-transport row,
   make the interface selector prefer the right path). Smallest change, no new protocol code, no
   new dependency.
   *Trade-off:* stands or falls on a hardware fact we don't have yet; hence the probe phase.

2. **Write the SmartInfinite frames over a custom GATT characteristic.** Bypass HID entirely:
   extend `BluetoothDeviceDiscoveryService` (or a sibling) to find a vendor GATT service and
   write `0x15` frames to it.
   *Trade-off:* no vendor documentation names such a service/UUID for this family; it would need
   blind GATT exploration or a vendor request, a new `IDeviceRgbService` implementation with a
   different transport, and routing `SetDeviceLedColor` by something other than VID/PID. Much
   bigger and speculative. Keep as fallback only if the probe rules out option 1.

3. **Honest read-only UI on Bluetooth.** Mark the Rampage Bluetooth transport
   `SupportsConfiguration: false` (the TAIPAN pattern) so the RGB section stops pretending.
   *Trade-off:* does not deliver the requested feature; it is the documented fallback if the
   hardware turns out not to expose any writable channel over BLE.

## Recommended approach

Option 1, in two stages inside one change:

**Stage 1 — probe (hardware truth first).** Add `RampageBtProbe` to
`src/ProoveGaming.Device.Testing` (the established probe pattern — cf. `SmartInfiniteProbe`,
`TaipanDongleProbe`). With the board connected over Bluetooth only, it must:
- enumerate **all** HID devices (no VID/PID filter), dumping path, VID/PID, report lengths, and
  whether the vendor usage `0x20`/page `0xFF87` is present — BLE-HID devices surface under
  `BTHENUM`/`BTHLE` device paths and take their VID/PID from the DIS PnP-ID characteristic, which
  may differ from the USB identity or be absent;
- if a vendor-usage candidate exists, run a `0x14` (read function-area) round-trip and then a
  full `0x15` write via the existing `RampageProtocol` framing, reporting acks and whether the
  lighting visibly changes.
The findings go into `docs/knowledge-base/rampage-keyboard-protocol/bluetooth-findings.md`
(same folder as the existing hardware findings). **A human with the hardware runs the probe**;
the implementation below is written so the probe outcome only tunes config values, not the code
shape.

**Stage 2 — plumbing (independent of the probe's exact VID/PID answer).**

1. **Give the Bluetooth card a real identity.** Add `vendor_id` and `product_id` to
   `DeviceCatalogEntry` in `deviceservice.proto` (the entry already carries
   `settings_product_id`; it lacks the routing identity). `HidDeviceMapRegistry.ToCatalogEntry`
   fills them from the `DeviceMap` row. `HomeViewModel.FillBluetoothCard` stamps
   `card.VendorId / card.ProductId / card.SettingsProductId` from the entry when they are unset
   or the card is not already on a live wired transport — reusing the "settings identity is
   sticky, routing identity follows the live transport" rule from `ApplyDeviceIdentity`.
   Because the Bluetooth row inherits VID 10473 / PID 12888, the settings bucket is automatically
   the same one the wired session uses — colours chosen on BT survive a replug to USB and back.
2. **Let the factory resolve a Bluetooth transport row.** In
   `DeviceRgbServiceFactory.CreateForDevice`, when no `!m.HasBluetooth` row matches, fall back to
   a `m.HasBluetooth` row with the same VID/PID and `SupportsRgb` (keyboards only — the existing
   name-based dispatch to `CreateRampageKeyboardRgbService` etc. is unchanged). The wired-first
   preference keeps today's behaviour byte-identical for every existing device, including the
   T-Racer PID-collision case the current comment warns about.
3. **Interface selection.** `SmartInfiniteInterfaceSelector.Select` already matches by VID/PID +
   vendor usage. Add one refinement: when several vendor-usage candidates match, prefer a
   non-`BTHENUM`/`BTHLE` path (i.e. the cable) so a board connected over USB *and* BT at once
   keeps writing over the faster, proven link. This is a pure ordering change; with only one
   candidate nothing differs.
4. **Reapply on Bluetooth (re)connect.** `HandleBluetoothDeviceFoundAsync` currently shows the
   connect notification but never calls the connect-time re-apply. Call it on the
   disconnected→connected transition (it self-gates on `SupportsRgb` and `VendorId > 0`, so
   nothing changes for headsets or for devices whose identity is still unknown).
   *(2026-08-17: `ReapplyRgbOnConnect` was merged into `ReapplyOnConnect`, which restores the whole
   preset in one transaction and falls back to a lighting-only push — the path a Bluetooth board
   still takes. Same self-gating, same call site.)*
5. **Config tuning from the probe.** If the probe shows the BLE identity differs from
   10473/12888, record the observed values as `VendorId`/`ProductId` overrides on Rampage's
   Bluetooth transport in `src/ProoveGaming.DeviceHost/appsettings.json` and set
   `MergeTransports: true` on Rampage so the settings bucket stays pinned to the wired PID (the
   T-Racer/TAIPAN precedent). If the identities match, no config change is needed. If the probe
   finds **no writable vendor channel at all**, fall back to option 3 (set
   `SupportsConfiguration: false` on the Bluetooth transport) and open a vendor request following
   `docs/knowledge-base/rampage-keyboard-protocol/vendor-request.md`'s pattern — the plumbing
   above stays dormant and harmless.

**Flagged:** step 1 is a **gRPC contract change** (`deviceservice.proto`) — additive fields only,
both sides regenerate from `ProoveGaming.GrpcDependencies`; an old App against a new DeviceHost
sees extra fields it ignores, an old DeviceHost against a new App yields 0-valued identity, which
is exactly today's behaviour. No new NuGet packages, no new projects, no TFM change.

## File-level change list

> Note (2026-08-13): this was the pre-probe contract; all items landed (item 10 as the outcome-C
> fallback). The scope extension added files not listed here:
> `src/ProoveGaming.Device.Testing/Rampage24gProbe.cs` (2.4G probe),
> `docs/knowledge-base/smart-infinite-protocol/wireless-receiver-protocol.md` (translated
> receiver spec + hardware results), and the `Dongle2_4G` transport in `appsettings.json`.
> See the Outcome section for the final inventory.

Ordered so tests that pin current behaviour land before the behaviour change.

| # | File | Intent |
|---|------|--------|
| 1 | `tests/ProoveGaming.Infrastructure.Tests/DeviceRgbServiceFactoryTests.cs` (new) | Characterization first: factory returns null for a VID/PID that only matches a Bluetooth row (today's behaviour), keyboard/mouse dispatch by name; then the new fallback cases (BT row resolves to the Rampage service; `SupportsRgb=false` BT row still returns null). |
| 2 | `src/ProoveGaming.Device.Testing/RampageBtProbe.cs` (new) | Probe: full HID enumeration dump + vendor-usage search + `0x14` read + `0x15` write attempt while the board is on Bluetooth. |
| 3 | `src/ProoveGaming.Device.Testing/Program.cs` (edit) | Register the probe under a command name, following the existing probe registrations. |
| 4 | `docs/knowledge-base/rampage-keyboard-protocol/bluetooth-findings.md` (new) | Findings skeleton (device identity table, interface dump, write result) to be filled from the probe run; linked from this plan. |
| 5 | `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto` (edit) | Add `vendor_id` / `product_id` to `DeviceCatalogEntry` (additive field numbers). |
| 6 | `src/ProoveGaming.DeviceHost/Services/HidDeviceMapRegistry.cs` (edit) | Fill the two new fields in `ToCatalogEntry` from the `DeviceMap` row. |
| 7 | `src/ProoveGaming.Infrastructure/Services/DeviceRgbServiceFactory.cs` (edit) | Fall back to a `HasBluetooth` keyboard row (same VID/PID, `SupportsRgb`) when no wired row matches; update the misleading "wired-only" comment. |
| 8 | `src/ProoveGaming.Infrastructure/Classes/SmartInfiniteInterfaceSelector.cs` (edit) | Among vendor-usage matches, order non-Bluetooth device paths first. |
| 9 | `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` (edit) | `FillBluetoothCard`: stamp routing + settings identity from the catalog entry; `HandleBluetoothDeviceFoundAsync`: the connect-time re-apply (`ReapplyOnConnect` since 2026-08-17) on the reconnect transition. |
| 10 | `src/ProoveGaming.DeviceHost/appsettings.json` (edit, conditional on probe) | Bluetooth-transport `VendorId`/`ProductId` overrides and/or `MergeTransports: true` on Rampage — only if the probe shows a different BLE identity; `SupportsConfiguration: false` instead if the probe rules the channel out. |
| 11 | `tests/ProoveGaming.App.Tests/HomeViewModelBluetoothIdentityTests.cs` (new, name per existing conventions) | Bluetooth card gets VID/PID/settings identity from the catalog entry; identity is not clobbered when a wired transport is live; reapply fires on reconnect only. |
| 12 | `docs/CONTEXT.md` (edit) | Add one-line definitions: **HOGP** (HID over GATT — how a BLE keyboard surfaces HID on Windows), **DIS PnP ID** (the GATT characteristic Windows takes a BLE HID device's VID/PID from). |

## Acceptance criteria

> Status column updated 2026-08-13 against the final outcome. The first two criteria were
> conditional on the Bluetooth probe confirming a writable channel; the probe ruled that out
> (outcome C), so they are closed as **superseded** by the read-only-BT fallback plus the 2.4G
> delivery — see the Outcome section.

- [x] *(superseded — BT channel proven not writable)* Given Rampage connected **over Bluetooth
  only**, changing RGB in Device Details reaches the hardware. **Actual:** impossible on this
  firmware; the Bluetooth transport is `SupportsConfiguration: false` and the details page is
  read-only over BT. The equivalent behaviour was delivered over the **2.4G receiver** and
  verified in the App on hardware (2026-08-13): effect/colour/brightness changes reach the board
  through the `Dongle2_4G` transport with the unchanged production write path.
- [x] *(superseded in part)* Bluetooth card identity: the card now carries the catalog identity
  (`VendorId=10473`, `ProductId=12288` over BT) instead of 0/0. The one-settings-bucket half is
  moot for BT (read-only transport persists nothing); for wired ↔ 2.4G the buckets are
  **deliberately separate** for now (foreign receiver VID blocks `MergeTransports`) — recorded as
  a known limitation.
- [x] Reapply-on-reconnect: `ReapplyOnConnect` fires on the Bluetooth reconnect transition
  (self-gating; a no-op while BT is read-only) and, via the existing HID path, on 2.4G reconnect —
  where it actually restores the saved lighting.
- [x] Wired behaviour unchanged: writes still select the wired vendor interface (non-Bluetooth
  paths sort first in `SmartInfiniteInterfaceSelector`); full test suite green
  (Infrastructure 626, App 764).
- [x] TAIPAN on Bluetooth stays read-only — pinned by `DeviceRgbServiceFactoryTests`
  (mouse BT row is never served) and unchanged `SupportsConfiguration` handling.
- [x] Old-DeviceHost compatibility: entry without the new proto fields leaves the card at 0/0 —
  pinned by `HomeViewModelBluetoothIdentityTests.EntryWithoutIdentity_LeavesTheCardUntouched`.
- [x] Probe evidence recorded:
  `docs/knowledge-base/rampage-keyboard-protocol/bluetooth-findings.md` (BLE interface dump,
  `0x28E9:0x3000` identity, three failed write APIs, documentation corroboration) and
  `docs/knowledge-base/smart-infinite-protocol/wireless-receiver-protocol.md` (receiver
  `0x056D:0xC077`, working `0x14`/`0x15` over 2.4G in both framings).

## Test strategy

New feature (`add-*`): the tests below are required coverage, not nice-to-have. CI does not run
the `tests/` projects — run `dotnet test ./src/ProveGaming.slnx` locally.

**`tests/ProoveGaming.Infrastructure.Tests`** (new `DeviceRgbServiceFactoryTests.cs`; mirror the
existing constructor-injected-mock style, e.g. `SmartInfiniteKeyMappingServiceTests`):
- Characterization (written first, passing on current code): VID/PID matching only a
  `HasBluetooth` row → `CreateForDevice` returns null today.
- After the change: same input → returns a `RampageKeyboardRgbService` when the BT row is a
  keyboard with `SupportsRgb=true`; still null when the BT row has `SupportsRgb=false` (T-Racer
  BT shape) or is a mouse with `SupportsConfiguration=false` (TAIPAN shape); a wired row, when
  present, still wins.
- `SmartInfiniteInterfaceSelector`: among two vendor-usage candidates, the non-`BTHENUM` path is
  selected; with a single candidate behaviour is unchanged. (Feasible only if the selector's
  input is mockable — it takes `IEnumerable<HidDevice>`; if constructing fake `HidDevice`
  instances proves impossible under HidSharp, cover the ordering rule by extracting the
  path-classification predicate into a testable static and unit-testing that.)

**`tests/ProoveGaming.App.Tests`** (new `HomeViewModelBluetoothIdentityTests.cs`; follow the
existing `DeviceCardConnectionTooltipTests` setup style):
- `FillBluetoothCard` stamps VendorId/ProductId/SettingsProductId from a catalog entry carrying
  the new fields; leaves an already-wired card's routing identity alone; keeps a previously set
  `SettingsDeviceKey`.
- Old-host compatibility: entry with 0/0 leaves the card at 0/0 and `ReapplyOnConnect`
  self-gates (no throw, no write attempt).
- Reapply fires exactly on the disconnected→connected Bluetooth transition, not on repeated
  connected events.

**Deliberately not covered by automated tests:** everything that needs the physical board — HID
enumeration over BLE, the `0x15` write actually changing LEDs, USB↔BT switching. That is the
probe (stage 1) plus the QA checklist below. UI glow animation timers (Avalonia dispatcher
affinity) also stay manual, as elsewhere in this suite.

## QA (manual test steps)

> Rewritten 2026-08-13 to match the delivered behaviour: RGB control works over the **2.4G
> receiver**; over **Bluetooth** the keyboard is display-only (a firmware limitation confirmed
> with the vendor's own documentation — the settings page is intentionally read-only there).

**2.4G receiver (the new wireless RGB path):**

1. Plug the Rampage 2.4G receiver into a USB port, switch the keyboard to 2.4G mode, and make
   sure its cable is unplugged. Launch the Proove Gaming app. **Expected:** the Rampage card
   appears, marked as connected via dongle.
2. Open the Rampage card → RGB Lighting section. **Expected:** the section is enabled (effects,
   colour picker, sliders active).
3. Pick the Static effect and a clearly different colour (e.g. red). **Expected:** the physical
   keyboard backlight turns static red within about 2 seconds.
4. Move the Brightness slider down and up. **Expected:** the physical backlight dims and
   brightens accordingly (a short delay after releasing the slider is normal).
5. Pick an animated effect (e.g. Wave) and change its Speed. **Expected:** the physical animation
   runs and its speed follows the slider.
6. Toggle the lighting switch off, then on. **Expected:** LEDs go dark, then the previous effect
   returns.
7. Turn the keyboard off and on again (still in 2.4G mode), wait for it to reconnect.
   **Expected:** the last chosen lighting comes back on the board by itself.
8. Check the card's battery indicator while on 2.4G. **Expected:** either a plausible battery
   level, or no battery indicator — but NOT a permanently empty red battery icon. (If the red
   icon appears, report it: battery over the receiver is a known-unverified area.)
9. Plug in the USB cable (keyboard still paired to the receiver). **Expected:** the card switches
   to the cable connection and RGB changes keep working. Note: the colour saved during the 2.4G
   session may differ from the cable session's saved colour — wired and 2.4G currently keep
   separate saved settings; this is a known limitation, not a bug.

**Bluetooth (intentionally read-only):**

10. Switch the keyboard to Bluetooth mode (paired with Windows), no cable, no receiver.
    **Expected:** the Rampage card shows as connected via Bluetooth; its battery level still
    displays; the settings page is read-only (RGB edits and Save Preset are not offered/applied)
    — configuration over Bluetooth is not supported by the keyboard's firmware.
11. While on Bluetooth, typing on the keyboard works normally in any text editor.

**Regressions:**

12. With the cable connected, open the key assignment page. **Expected:** the key layout loads
    and RGB changes still apply (no "could not read the layout" errors).
13. Connect a TAIPAN mouse over Bluetooth. **Expected:** its settings page is still read-only
    (Save Preset disabled).
14. Wired Cyrex / Aress / other keyboards: RGB behaves exactly as before.
15. During all steps, the app never freezes or stutters while sliders are dragged.

## Review focus

1. `gRPC contract` — additive-only proto fields, both sides regenerated, old/new compatibility.
2. `thread-safety` — factory cache and discovery singletons; identity stamping happens on the
   gRPC event path while the UI reads the card.
3. `async/await` — no new sync-over-async in the BT reapply path; `CancellationToken` flow.
4. `null-safety` — cards/entries with absent identity (old host, unknown device) must degrade to
   today's behaviour, never throw.

## Performance & quality

- **No new hot paths.** RGB writes are user-triggered and already debounced (~300 ms slider
  debounce in `DeviceDetailsViewModel.Rgb.cs`); over BLE each HID packet costs a connection
  interval (tens of ms), so the existing 5-command session stays well under a second — acceptable,
  and never on the UI thread (it runs in the DeviceHost).
- `SmartInfiniteInterfaceSelector` runs per write and already parses report descriptors; the new
  ordering must not add a second enumeration pass (reuse the single `matching` list).
- Identity stamping in `FillBluetoothCard` is O(1) field assignment on an existing event path —
  no allocation concerns.
- Singleton state: `DeviceRgbServiceFactory`'s caches are keyed by (VID, PID, deviceKey); a board
  reachable over two transports with the same VID/PID shares one `RampageKeyboardRgbService`
  instance — that service is stateless between calls apart from the `DeviceGate`, so this is
  safe, but the reviewer should confirm no per-transport state creeps in.

## Risks

1. **The probe may find no writable vendor channel over BLE.** Then option 1 is dead for this
   firmware; the fallback (documented above) is an honest read-only RGB section on BT plus a
   vendor request. The plumbing steps are shaped to stay harmless in that case.
2. **Windows may expose the vendor collection but reject writes over HOGP** (acked by the stack,
   ignored by firmware — the same failure mode the key-matrix work hit on USB). The probe's
   read-back (`0x14`) plus eyes-on-LEDs check is the guard; do not trust write acks alone.
3. **Same-VID/PID ambiguity.** If BLE surfaces the wired identity, a board connected over cable
   *and* BT presents two vendor interfaces under one VID/PID; the selector's non-BT preference
   (step 3) is what keeps the cable authoritative. Conversely the HID discovery filters
   (`FilterUsbDevices` matches `!HasBluetooth` rows by VID/PID) would classify the BLE interface
   as "wired", showing a cable icon while on BT — cosmetic, but QA step 1 will catch it; fixing
   connection-type detection by device path is a follow-up, not this change.
4. **`DeviceGate` scope.** The gate is keyed by DevicePath, so wired and BLE interfaces gate
   independently — two simultaneous sessions to one board become possible. The firmware treats
   sessions per interface (per the hardware findings), so this should hold, but watch for it in
   QA step 10.
5. **Commands that must never be sent** (`0x20` Reset, `0x31` round-trip) — the probe must not
   include them; it reuses `RampageProtocol` constants for `0x14`/`0x15` only.

## Open questions

1. **Does the vendor's own driver change RGB while the board is on Bluetooth?** A yes makes the
   probe a formality; a no makes option 3 + vendor request the likely outcome. Worth asking the
   vendor contact in parallel with running the probe (cf.
   `docs/mouse/taipan/taipan-vendor-request-2026-08-05.md` for the format).
2. **Who runs the probe and when?** It needs the physical board and ~15 minutes; the plan assumes
   it happens before merging the config tuning (file 10), while files 1–9 can land first.
3. Should Rampage get `MergeTransports: true` even if the identities match (for consistency with
   T-Racer/TAIPAN)? Not required for this feature; recommend deciding separately.

## Review findings

### Round 1 — Ship: yes

#### Summary
The branch (uncommitted working tree, 7 modified + 6 new files) implements the plan's stage-2
plumbing: additive `vendor_id`/`product_id` on `DeviceCatalogEntry` filled by
`HidDeviceMapRegistry`, Bluetooth-row fallback in `DeviceRgbServiceFactory.SelectConfigRow`
(keyboards with RGB only), non-Bluetooth-first ordering in `SmartInfiniteInterfaceSelector`,
identity stamping + reapply-on-reconnect for Bluetooth cards in `HomeViewModel`, the `rampage-bt`
probe with findings skeleton, and tests on all three seams. Build clean (0 warnings), 625 + 764
tests green.

#### Plan conformance
Plan: this document.
- Implemented as planned: file-list items 1–9, 11, 12 all present and matching intent.
- Deferred as planned: item 10 (`appsettings.json` tuning) — explicitly conditional on the probe
  run; the findings skeleton spells out all three outcomes. Correct call.
- Deviates from plan (sanctioned fallbacks, surfaced for the record): the selector ordering test
  is pinned via the extracted `IsBluetoothDevicePath` predicate instead of ordering over real
  `HidDevice` instances (HidSharp types cannot be constructed/mocked — the plan pre-authorized
  this); the "reapply fires exactly on the reconnect transition" case is not unit-tested because
  `HandleBluetoothDeviceFoundAsync` needs the full DI graph — the call reuses the existing
  `!wasConnected && found.Connected` branch that drives the connect notification, so the guard is
  shared with proven code.
- Accepted limitation (per plan): every hardware acceptance criterion waits on the probe;
  pre-probe, a Bluetooth-only RGB write now fails *addressed* (real VID/PID, no interface) rather
  than *unaddressed* (0/0), which is the intended stepping stone.

#### Code
- [x] **S1. Identity stamping enrolls Bluetooth-connected cards in the 60-s HID battery poll** —
  `src/ProoveGaming.App/ViewModels/HomeViewModel.cs:646` (`PollBatteriesAsync`) — Should-fix
  **Problem:** the poll selects `Connected && HasBattery && SupportsBattery` with no transport
  check. A Bluetooth-connected Rampage gets `HasBattery=true` from its GATT battery and — new in
  this change — a real VID/PID from `ApplyBluetoothIdentity`, so it now enters the poll.
  `GetBatteryLevelAsync(10473, 12888, …)` reaches `HidDeviceDiscoveryService.TryReadBattery`,
  which *writes* command frames to, and reads replies from, every matching HID interface —
  un-coordinated with `DeviceGate`. Before this change the request went out as VID=0/PID=0 and
  matched nothing.
  **Why it matters:** once the probe confirms a BLE vendor interface, an unsolicited malformed
  write + read lands on that interface every 60 seconds and can interleave with an RGB write
  session (the exact interference the DeviceGate comment in `RampageKeyboardRgbService` warns
  about). Battery over Bluetooth already comes from GATT, so the poll adds nothing there.
  **Fix:** exclude Bluetooth-live cards from the HID poll — `.Where(c => … && !c.HasBluetooth)`
  in `PollBatteriesAsync` (GATT keeps the level fresh via `UpdateBatteryAsync`). One line, no
  behavior change for cable/dongle cards.
- [ ] **S2. Settings bucket splits when Bluetooth connects before the cable ever has** —
  `src/ProoveGaming.App/ViewModels/HomeViewModel.cs:820` (`ApplyBluetoothIdentity`) +
  `:532` (`ApplyDeviceIdentity` adoption rule) — Should-fix
  **Problem:** a card born from Bluetooth discovery keeps `SettingsDeviceKey=""` (Bluetooth has
  no Container ID — correct). RGB set in that state persists under `(10473, 12888, "")`. When
  the cable is later plugged in, `ApplyDeviceIdentity` sees the empty key and adopts the wired
  Container ID, moving the bucket to `(10473, 12888, <containerId>)` — the colour chosen over
  Bluetooth is stranded under the old key.
  **Why it matters:** acceptance criterion 2 ("same settings bucket across BT ↔ USB") holds only
  when the cable connects first in the card's lifetime. QA step 8 fails in the BT-first order:
  set green on BT → plug cable → the app shows the *previous wired* colour, not green.
  **Solution options:**
  - Option A (designed mechanism): set `MergeTransports: true` on Rampage in
    `appsettings.json` — it is one physical device over USB+BT, exactly what the flag is for;
    the bucket pins to PID 12888 + empty key on every transport. Trade-off: existing users'
    wired Rampage settings live under `(…, containerId)` and would be stranded once — same
    class of migration cost the TAIPAN merge already paid. Decide together with the probe
    outcome (plan Open question 3 already tees this up).
  - Option B (local): in `ApplyDeviceIdentity`, when adopting a key onto a card that currently
    has none but already persisted settings under the empty key, keep the empty key for
    keyboards. More surgical, but adds a special case the merged flag makes unnecessary.
- [x] **S3. Stale "wired-only" comment now contradicts the RGB factory** —
  `src/ProoveGaming.Infrastructure/Services/DeviceKeyMappingServiceFactory.cs:25` — Should-fix
  **Problem / why it matters:** the comment says "Look up the config entry the same way
  DeviceRgbServiceFactory does: filter to the non-Bluetooth entry … Key remap is wired-only for
  the same reason RGB is". After this change the two factories deliberately differ (RGB has a
  Bluetooth fallback; key remap stays wired-only). The next reader will "fix" one to match the
  other in the wrong direction.
  **Fix:** reword to state the divergence: key remap needs the 384-byte `0x18` transfer and
  remains wired-only; RGB intentionally falls back to a Bluetooth keyboard row (see
  `DeviceRgbServiceFactory.SelectConfigRow`).

#### Tests
- [ ] Double-reapply on a dual-path reconnect is unobserved: post-probe, a BLE Rampage may
  arrive via both the HID discovery path and the OS-Bluetooth path, each firing
  `ReapplyRgbOnConnect` on its own transition — idempotent writes, but worth one QA eye
  (visible as a double LED flicker on reconnect). No automated coverage is practical; add a QA
  note when filling in `bluetooth-findings.md`.
- [ ] `HidDeviceMapRegistry` VendorId/ProductId population has no test — the DeviceHost has no
  test project at all, so this is a repo-wide gap, not a regression of this branch. Accept.

#### Nits
- Probe stage 3 constructs `RampageKeyboardRgbService` without a logger
  (`src/ProoveGaming.Device.Testing/RampageBtProbe.cs:167`), so an open/write failure prints
  only "FAILED" without the Win32 reason; a console `ILogger` would make a probe run
  self-diagnosing. (The green-then-read-back design already makes the verdict robust against the
  known first-write-after-reopen `ERROR_INVALID_FUNCTION` race.)
- `SelectConfigRow` allocates two strings per call via `vendorId.ToString()` — same as the code
  it replaced; called per user action, not per frame. Fine as is.

#### What's done well
- The proto change is genuinely additive with both mixed-version directions reasoned through and
  the old-host case pinned by a test (`EntryWithoutIdentity_LeavesTheCardUntouched`).
- `SelectConfigRow`'s Bluetooth fallback is tightly scoped (RGB + keyboard only), so TAIPAN's
  dead-over-BT config and T-Racer's PID collision keep today's behavior — each pinned by a test.
- The probe follows the house rules: read-only by default, routine `0x15` only behind an explicit
  `write` arm, `0x20`/`0x31` never sent, and the verdict comes from a read-back plus eyes on
  LEDs, not from acks.
- Extracting testable statics (`SelectConfigRow`, `IsBluetoothDevicePath`,
  `ApplyBluetoothIdentity`) instead of leaving the logic inline in un-mockable hosts matches the
  repo's established pattern (`NotificationGroupViewModel`).

#### Verdict
Ship: yes — no blockers; S1 and S3 are small and should land before merge, S2 needs a decision
(Option A ties into the probe-conditional config step this plan already defers). First:
1. S1 — exclude Bluetooth-live cards from `PollBatteriesAsync`.
2. S3 — reword the `DeviceKeyMappingServiceFactory` comment.
3. S2 — decide MergeTransports for Rampage alongside the probe-outcome config tuning.

#### Round 1 follow-up (coder, 2026-08-13)
- S1 fixed: `PollBatteriesAsync` now filters `!c.HasBluetooth` (GATT keeps BT levels fresh).
- S3 fixed: `DeviceKeyMappingServiceFactory` comment now states the deliberate divergence from
  `DeviceRgbServiceFactory.SelectConfigRow`.
- **Probe was run on real hardware — outcome C (channel not writable).** Full record in
  `docs/knowledge-base/rampage-keyboard-protocol/bluetooth-findings.md`: over BLE the board
  enumerates as VID `0x28E9` / PID **`0x3000`** under a HOGP path built from the HID service UUID
  `{00001812-…}` (a third path form — `IsBluetoothDevicePath` extended + test added); the vendor
  collection (col06, 64/64, usage `FF55:0202`, Output id `0x06`) is advertised but all three
  Windows write APIs fail (HidSharp overlapped IO error / `HidD_SetOutputReport` err 6 / raw
  `WriteFile` err 87). Applied fallback: Rampage Bluetooth transport gets `ProductId: "12288"` +
  `SupportsConfiguration: false` in `appsettings.json` — read-only details over BT, wired
  unchanged; vendor request drafted in the findings doc.
- S2 thereby resolved without `MergeTransports`: with Bluetooth read-only nothing persists from a
  BT session, so no bucket can split; the flag stays off to avoid stranding existing wired
  settings. Revisit only if the vendor makes the channel writable (noted in the findings doc).

#### Round 1 follow-up 2 (coder, 2026-08-13) — wireless RGB delivered via the 2.4G receiver
- The documentation sweep surfaced the vendor's only wireless-config path (the 2.4G receiver —
  untranslated workbook sheet, now `docs/knowledge-base/smart-infinite-protocol/wireless-receiver-protocol.md`),
  and the new `rampage-24g` probe confirmed it on hardware: the receiver enumerates as
  `0x056D:0xC077` with the wired interface layout, and the **production
  `RampageKeyboardRgbService` RGB write works through it unchanged** (red and green both
  persisted, `0x14` read-backs matched).
- Catalog change only: Rampage gained a `Dongle2_4G` transport (`VendorId "1389"`,
  `ProductId "49271"`, `BatteryCommand 31`) in `appsettings.json`. No service code changes needed.
- Known limitation: the receiver's foreign VID blocks `MergeTransports` (registry enforces one
  VendorId per merged product), so wired and 2.4G sessions keep separate settings buckets;
  follow-up if product wants them merged. Battery over the receiver is unverified — if QA sees a
  perpetual red battery icon on the 2.4G card, drop `BatteryCommand`/battery support for that
  transport.
- **2026-08-13, developer confirmation:** RGB control for Rampage over 2.4G verified working in
  the App on real hardware. The plan's Outcome section, Acceptance criteria and QA checklist were
  updated to the delivered behaviour.
