# Raster Key Assignments — Implementation Plan

## Context

We shipped Rampage key-assignments UI + storage-write on
`features/PG-331-keyassignments`, but the physical key never actually remaps —
the SmartInfinite v1.4 protocol lacks a documented "commit staging → compiled
profile" command.

This branch (`features/PG-331-raster-keyassignments`) implements the same feature
for the **Raster** wireless TKL keyboard (VID `14000` / `0x36B0`, PID `12385` /
`0x3061`), tested in **wired USB mode**.

Unlike Rampage, Raster's SetUserKey command actually reaches the scan matrix
and physical keys emit the new scancode immediately — no unplug/replug needed,
no firmware "commit" step.

## Approach

- **Branch from `main`** (no Rampage code carried over).
- **Interactive on-screen keyboard + target list UI** — click a physical key,
  pick a target scancode from the left panel.
- **Device is the truth source** — no SQLite auto-restore. UI state is derived
  from what firmware actually has, probed on device connect.

## Scope

- Detect Raster (VID `14000`, PID `12385`) and enable KeyAssignments only for it.
- Show all 81 physical keys as clickable overlays on the right-panel keyboard image.
- Show target list (letters, digits, symbols, modifiers) on the left panel.
- Send `Set User Key` (`0x1B`) via DeviceHost gRPC for each selection.
- Probe the actual matrix at device connect to build a `scancode → real slot`
  map (see [protocol-reference.md](protocol-reference.md#matrix-indexing)).
- Read live user overrides so UI reflects real device state.
- Do **not** implement macros, per-profile switching, or RGB in this pass.

## Files added

**Common** (shared between App and Infrastructure)
- `src/ProoveGaming.App/Models/Keyboards/RasterKeyCatalog.cs` — 81 physical keys
  with default HID scancodes.
- `src/ProoveGaming.App/Models/Keyboards/RasterVisualLayout.cs` — canvas coordinates
  for the interactive keyboard image.

**Infrastructure** (DeviceHost-side HID protocol)
- `src/ProoveGaming.Infrastructure/Interfaces/IKeyboardKeyMappingService.cs` —
  `SetKeyMappingAsync`, `ProbeDefaultMatrixAsync`, `ProbeUserOverridesAsync`.
- `src/ProoveGaming.Infrastructure/Interfaces/IDeviceKeyMappingServiceFactory.cs`.
- `src/ProoveGaming.Infrastructure/Services/RasterKeyboardKeyMappingService.cs`.
- `src/ProoveGaming.Infrastructure/Services/DeviceKeyMappingServiceFactory.cs`.

**App**
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs`
  — partial with `PhysicalKeys`, `AssignmentTargets`, `SelectedPhysicalKey`,
  `SelectPhysicalKey`/`SelectAssignmentTarget` commands, `IsRasterDevice` gate.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/KeyAssignmentOptionViewModel.cs`.

## Files modified

**gRPC + DeviceHost**
- `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto` —
  `SetKeyMapping` + `ProbeKeyMatrix` RPCs.
- `src/ProoveGaming.DeviceHost/Services/DeviceService.cs`.
- `src/ProoveGaming.DeviceHost/Program.cs` — register factory.

**App**
- `src/ProoveGaming.App/Services/IUsbHidDeviceService.cs`,
  `UsbHidDeviceService.cs` — client for the two RPCs.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs` —
  inject `LocalStorageService`, add `IsRasterDevice` notification, call
  `RefreshFromDeviceAsync` on device connect.
- `src/ProoveGaming.App/Views/DeviceDetails/Sections/KeyAssignmentsSectionView.axaml*`
  — replace the hardcoded ESC + F1..F12 list with a data-bound target list.
- `src/ProoveGaming.App/Views/DeviceDetails/DeviceDetailsView.axaml` — for
  Raster, replace the static keyboard image with an interactive Canvas overlay.

## Verification

**Build & tests**
- `dotnet build ./src/ProveGaming.slnx` — 0 errors.
- `dotnet test ./src/ProveGaming.slnx` — 43/43 tests pass.

**Manual on hardware (Raster in USB mode)**
1. Launch App + DeviceHost with Raster connected via USB.
2. Confirm the KeyAssignments nav item is visible in the sidebar.
3. Click any physical key in the on-screen keyboard (e.g. `E`).
4. Left panel title updates to `E button`.
5. Pick a target (e.g. `R`) from the target list.
6. Open Notepad, press physical `E` — expect `R`.
7. Close & reopen the app — the same target is still highlighted (state read
   from device, not SQLite).
8. Do a manual factory reset (FN+Space, hold 3 s), reopen the app — UI shows
   the factory state, not the previous "intent" from SQLite.

## Non-goals (explicit)

- No macro editor.
- No per-profile UI — writes go to profile 0 (`0x1B`) only.
- No Rampage code.
- No DragonFang (same VID, different PID; can follow in a sister PR).
- No RGB changes; the Raster RGB path stays unimplemented in this PR.

## Related documents

- [protocol-reference.md](protocol-reference.md) — byte-level protocol details
  extracted from vendor webapp + confirmed by live testing.
- [investigation-log.md](investigation-log.md) — chronological log of what was
  tried, what worked, and the reasoning behind each iteration.
- [reference/](reference/) — vendor materials and extracted webapp chunks.
