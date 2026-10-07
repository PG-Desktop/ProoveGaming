# T-Racer Key Assignments — Implementation Plan

## Context

We shipped Raster key-assignments (branch `features/PG-331-raster-keyassignments`) —
the physical remap works instantly over `mi_01` on Raster (VID `14000` / PID `12385`).
See `../raster-key-assignments/raster-key-assignments-plan.md` for the full breakdown.

This plan does the same for the **T-Racer** wireless TKL keyboard, tested in
**wired USB mode** (VID `14000` / PID `12370`). T-Racer is internally still named
`DragonFang` in the config (kept for backward-compat with persisted session cards —
same reason documented in `../tracer-rgb/tracer-rgb-plan.md`). Display name is
`T-Racer`; matching key on the wire is `DragonFang`.

T-Racer sits in the **same vendor family** as Raster — the RGB work already proved
they share the SmartInfinite-adjacent protocol on `mi_01` (report ID `0x00`,
65-byte reports, `0xAA` magic). The key-remap wire format is the same protocol
family (commands `0x10`/`0x11` START/STOP, `0x14`/`0x16`/`0x1A`/`0x1B` for
GetFuncInfo/GetDefault/GetUserKey/SetUserKey — see
`../raster-key-assignments/protocol-reference.md`).

## Approach

- **Reuse the wire protocol we already reverse-engineered for Raster.** No
  fresh reverse-engineering pass — the RGB experience already established that
  T-Racer speaks the same protocol on the same interface.
- **Separate service file, near-copy of `RasterKeyboardKeyMappingService`**,
  matching the pattern set by `TRacerKeyboardRgbService`. Same wire format,
  isolated service so a future T-Racer-only firmware quirk can be tuned without
  touching Raster. If the two stay identical after real-world use they can be
  merged into a shared service — trivially safe once known.
- **Same runtime-probe strategy** for the matrix map. Raster's investigation
  (Phase 5, `../raster-key-assignments/investigation-log.md`) proved that static
  vendor JSON layouts don't match the actual firmware slot indexing on this
  family. Probing `GET_DEFAULT_0` slots 0..127 at device-connect builds an
  accurate `scancode → slot` map for whatever T-Racer's firmware ships with.
- **Device is the truth source** — same policy as Raster. No SQLite auto-restore;
  UI state is derived from `GET_USERKEY_0` probe on connect.
- **Layout is Raster + `PrintScreen` (82 keys vs Raster's 81)** — per the
  client-supplied `Proove_Gaming_Key_Layout.xlsx` (saved under
  `reference/key-layout-reference.xlsx`). Everything else — F-row, number row,
  alpha rows, right-column `End/PgUp/PgDn/Del`, arrows, Knob presence — is
  identical between the two 75% models. `PrintScreen` is the one physical key
  unique to T-Racer among the two. So T-Racer needs its own catalog + visual
  layout — reusing Raster's would silently drop `PrintScreen` from the
  remappable list. The runtime matrix probe still overwrites `MatrixIndex`
  per key with the firmware's real slot.
- **Same policy on FN and Knob/Dial as Raster.** Both are marked remappable in
  the spreadsheet, but Raster's catalog intentionally excludes them (`FN`
  breaks FN+X shortcuts on the shortcut card; `Knob/Dial` has no HID scancode
  to remap to). T-Racer's catalog follows the same exclusions. T-Racer also
  omits `RightCtrl` (same as Raster — the spreadsheet marks it `—` for both
  models).

## Scope

- Detect T-Racer (config `Name = "DragonFang"`, wired PID `12370`) and enable
  the KeyAssignments UI for it.
- Reuse the interactive on-screen keyboard + target-list UI already shipped for
  Raster (`RasterKeyboardOverlayView`, `KeyAssignmentsSectionView`).
- Send `Set User Key` (`0x1B`) via DeviceHost gRPC (existing `SetKeyMapping` RPC
  — no proto changes).
- Probe the actual matrix on device connect (existing `ProbeKeyMatrix` RPC).
- Wired USB only. Dongle (PID 12290) and Bluetooth (PID 12370, `HasBluetooth: true`)
  entries stay opted-out, matching the RGB restriction — the vendor dongle strips
  vendor HID channels and BT rarely exposes them.
- **Not in scope**: macro editor, per-profile switching, RGB (already shipped for
  T-Racer separately). No changes to the Raster path.

## Files added

**Common** (shared between App and Infrastructure)
- `src/ProoveGaming.App/Models/Keyboards/TRacerKeyCatalog.cs` — 82 physical keys.
  Mirrors `RasterKeyCatalog`'s 81 entries plus one extra:
  `new("PrintScreen", "PRTSC", CategoryFunctionRow /* or similar */, matrixIndex: TBD, hidUsageCode: 0x46)`.
  `MatrixIndex` is a placeholder — the runtime probe (`ProbeDefaultMatrixAsync`)
  will pick up its real slot. FN, Knob/Dial, and RightCtrl stay excluded, same
  as Raster.
- `src/ProoveGaming.App/Models/Keyboards/TRacerVisualLayout.cs` — canvas coordinates
  for the interactive keyboard image. Same 15-unit-wide 75% footprint as
  Raster with one extra key overlay for `PrintScreen`. Its exact position on
  T-Racer (top-right cluster? FN-row far right? beside the Knob?) is TBD —
  needs a photo of the physical device. **Placeholder** position in the
  initial commit; verify against a photo before shipping.

**Infrastructure** (DeviceHost-side HID protocol)
- `src/ProoveGaming.Infrastructure/Services/TRacerKeyboardKeyMappingService.cs`
  — near-copy of `RasterKeyboardKeyMappingService.cs`. Same 65-byte
  `mi_01` transport, same `0xAA` framing, same 3-byte `[type, code1, code2]`
  record, same runtime probe pattern. Log tags say `T-Racer`.

**Reference**
- `docs/plans/tracer-key-assignments/reference/key-layout-reference.xlsx` —
  the client-supplied `Proove_Gaming_Key_Layout.xlsx`, covering ARESS, CYREX,
  RAMPAGE, RASTER, T-801, and T-RACER. Source of truth for what keys T-Racer
  exposes to the remap UI. Vendored so future work doesn't have to re-request
  it.

## Files modified

**Infrastructure**
- `src/ProoveGaming.Infrastructure/Services/DeviceKeyMappingServiceFactory.cs`
  — currently hardcoded to Raster's VID/PID. Refactor the identity gate: match
  against config `DevicesMaps` by VID/PID/Name (mirroring what
  `DeviceRgbServiceFactory` already does), then dispatch:
  - `Name == "Raster"` → `RasterKeyboardKeyMappingService` (unchanged).
  - `Name == "DragonFang"` (wired PID 12370 only) → new
    `TRacerKeyboardKeyMappingService`.
  Keep the existing `outLen=65 / inLen=65` interface selection — same physical
  interface characteristic as Raster.

**App**
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs`
  — extend `HasInteractiveKeyboardOverlay` and the `LoadKeyLayoutForDevice`
  build path to dispatch on device identity: Raster → `RasterKeyCatalog` +
  `RasterVisualLayout`, T-Racer → `TRacerKeyCatalog` + `TRacerVisualLayout`.
  Match T-Racer by VID 14000 / PID 12370, or by `Device.Name` being
  `"T-Racer"` or `"DragonFang"` (both are possible depending on how the card
  was persisted vs how the daemon labels it — see the RGB session-card gotcha
  in `../tracer-rgb/tracer-rgb-plan.md`).
- `src/ProoveGaming.App/Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml{,.cs}`
  — the overlay view is already data-driven off `PhysicalKeys` (each with its
  own `Canvas.Left/Top/Width/Height` from the catalog), so it renders T-Racer
  correctly as soon as `LoadKeyLayoutForDevice` populates the collection from
  `TRacerVisualLayout`. Only rename needed if we want the class name to stop
  saying "Raster" — e.g. `KeyboardRemapOverlayView`. Optional; if renamed,
  update the `<detailSections:RasterKeyboardOverlayView …>` reference in
  `DeviceDetailsView.axaml`. Keep as `RasterKeyboardOverlayView` to minimise
  churn if in doubt.
- **Rename** `RasterKeyCatalog.IsRaster` / the `HasInteractiveKeyboardOverlay`
  gate is a candidate for renaming to something family-neutral
  (`IsRemappableKeyboard` / `IsRemappableSmartInfiniteKeyboard`) so a third
  device doesn't have to be spelled out as another special-case disjunction.
  Optional — keep it purely additive if the rename risks touching too many
  surfaces.

**DeviceHost**
- No changes to `deviceservice.proto`, `DeviceService.cs`, or `Program.cs` —
  the `SetKeyMapping` / `ProbeKeyMatrix` RPCs and the factory registration
  already cover this. All the added routing lives inside
  `DeviceKeyMappingServiceFactory`.

**Config**
- `src/ProoveGaming.DeviceHost/appsettings.json` — no schema changes needed for
  key remap (unlike RGB, which had a `SupportsRgb` flag). If we ever want to
  gate key-remap explicitly from config, adding a `SupportsKeyRemap` flag would
  belong here — but currently the App resolves eligibility itself via
  `HasInteractiveKeyboardOverlay`, and the factory decides by VID/PID. No
  change in this PR.

## Verification

**Build & tests**
- `dotnet build ./src/ProveGaming.slnx` — 0 errors.
- `dotnet test ./src/ProveGaming.slnx` — all tests pass. No new tests added for
  the mapping service (existing key-assignments have no unit tests either —
  the code path is a pure device round-trip).

**Manual on hardware (T-Racer in USB mode)**
1. Launch App + DeviceHost with T-Racer connected via USB.
2. Sidebar shows the KeyAssignments nav item.
3. On-screen keyboard renders 81 keys (same layout Raster uses).
4. Runtime matrix probe completes (log: `[KEY] T-Racer probe complete: N slots mapped`).
5. Click a physical key (e.g. `E`) — left panel title updates to `E button`.
6. Pick a target (e.g. `R`) — expect success ACK in log.
7. Open Notepad, press physical `E` — expect `R`.
8. Close & reopen the app — the same target is still highlighted (state read
   from device, not SQLite).
9. Manual factory reset (FN+Space, hold 3 s), reopen — UI shows factory state.
10. Confirm the Raster path still works on Raster hardware if available — same
    codebase, this is a smoke check that the factory dispatch didn't regress it.

**If the runtime probe returns an empty / very small map on T-Racer**: matrix
indexing may differ from Raster. Log first-slot bytes for slots 0..30 with
`GET_DEFAULT_0` and confirm the `[type, code1, code2]` layout matches. If T-Racer
uses a different type byte or a different address stride, `SetKeyMappingAsync`
needs to be adapted accordingly (unlikely — the RGB pass confirmed the same
family framing).

## Non-goals (explicit)

- No macro editor.
- No per-profile UI — writes go to profile 0 (`0x1B`) only, same as Raster.
- No dongle or Bluetooth support — same wired-only restriction as T-Racer RGB.
- No RGB changes; already shipped in `../tracer-rgb/`.
- No Raster regressions — factory dispatch is additive; Raster's code path
  stays byte-identical.

## Open questions / risks

- **`PrintScreen` position on the device.** The spreadsheet confirms T-Racer
  has one physical key Raster doesn't (`PrintScreen`), but not where it sits
  on the keyboard. We need a photo of T-Racer to place the overlay
  correctly in `TRacerVisualLayout`. Initial commit uses a placeholder
  position (top-right, above the arrow cluster) with a `TODO` comment;
  verify against a device photo before shipping.
- **`PrintScreen` matrix slot.** Runtime probe will discover it as long as
  the firmware stores it as a keyboard-type record (`type=0x10`) with no
  modifier and scancode `0x46`. Verify from the first live probe log that
  `0x46` appears in the returned map. If not, the probe's filter or `maxSlot`
  cap may need adjustment.
- **Matrix indexing range.** We're betting T-Racer's `scancode → slot` map
  fits in Raster's probe range (`0..127`). If T-Racer has a shifted range,
  the probe cap needs bumping.
- **Session-card name gotcha.** Same trap as RGB (see `../tracer-rgb/`): the
  App's card matching by `Name` can lag behind config's `DeviceName`. Make
  sure `HasInteractiveKeyboardOverlay` handles both `DragonFang` (persisted
  name) and `T-Racer` (fresh daemon-reported display name).

## Related documents

- [`../raster-key-assignments/raster-key-assignments-plan.md`](../raster-key-assignments/raster-key-assignments-plan.md)
  — the shipped Raster plan this one mirrors.
- [`../raster-key-assignments/protocol-reference.md`](../raster-key-assignments/protocol-reference.md)
  — wire-format reference, applies unchanged to T-Racer.
- [`../raster-key-assignments/investigation-log.md`](../raster-key-assignments/investigation-log.md)
  — the diagnostic journey (dead-ends to avoid repeating: `type=0x0F`,
  `mi_02&col05`, missing `0xAA`, static QK75 matrix layout).
- [`../tracer-rgb/tracer-rgb-plan.md`](../tracer-rgb/tracer-rgb-plan.md) —
  companion T-Racer RGB plan; documents the internal/display-name split
  (`DragonFang` vs `T-Racer`) and the session-card matching gotcha.
