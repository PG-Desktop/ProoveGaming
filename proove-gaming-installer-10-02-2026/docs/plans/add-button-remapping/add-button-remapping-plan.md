# Plan: Button remapping (key assignment) for mice

> **Superseded, 2026-09-24:** host-side persistence of button bindings is gone. The App's SQLite
> store (`LocalStorageService.GetButtonBindings` / its save path) became the source of truth in an
> earlier plan, after which `ButtonBindingStore` was never written again. `IButtonBindingStore`,
> `ButtonBindingStore`, the `GetButtonBindings` RPC and `IUsbHidDeviceService.GetButtonBindingsAsync`
> were removed as dead code in `docs/plans/fix-repository-audit-findings/` (Phase 3, D1). The sections
> below that describe `%ProgramData%/ProoveGaming/button-bindings.json` are the original design, kept
> as a record.

## Locked decisions (2026-07-11)

1. **Left-click is NOT remappable** — permanently fixed as the primary click; its
   hotspot is display-only.
2. **"Desktop" = Win+D** via a keyboard-mode combo entry (`07 E3 00 01`) — we try it;
   drop/replace if hardware rejects it.
3. **Rename the middle/wheel button to "Wheel click"** everywhere (label + hotspot).
4. **No App-side LocalStorage.** The persistent store lives in the **DeviceHost**: the
   host owns and persists the bindings and serves them to the App. This supersedes the
   "A1 App persistence" option below — see **Recommended approach**.

## Goal

Let the user reassign the mouse's physical buttons — **right-click, middle (wheel)
click, forward, and back** — to a curated catalog of actions (standard mouse buttons,
media/"recommended" actions, and system actions like My PC / Desktop / Search), have
those assignments written to the device, reflected back in the UI (including a
"revert to standard" option), and persist across app restarts. The feature must be
**capability-gated** so the UI clearly shows whether a given device supports remapping
(BLAZE does; others declare it via config).

## Scope

**In:**
- Reassign the 4 remappable BLAZE slots: right (slot 1), middle/wheel (slot 2), forward
  (slot 3), back (slot 4). See [key-matrix.md](../../mouse/blaze/key-matrix.md).
- Action catalog wired to real key-matrix entries from
  [x43_protocol_904a4f8.md §V](../../x43_protocol_904a4f8.md):
  - **Standard** (revert): Left / Right / Middle / Forward / Back mouse buttons.
  - **Recommended** (media): Play/Pause, Next, Previous, Stop, Mute, Volume Up,
    Volume Down, Media Player, WWW Home.
  - **System**: My PC (My Computer), Desktop, Search (WWW Search).
- Write the assignment to the device (key-matrix block 2), **block-2-only, safely**.
- Persist assignments locally per device and reflect them in the UI on open + on
  hotspot select (highlight the currently-assigned action).
- Capability flag `SupportsButtonRemap` end-to-end (config → gRPC → App), with a
  "not supported for this device" UI state when absent.

**Out (call out as future work):**
- **Macros** — the macro group/flow stays a stub (`0x04`/`0x13` macro protocol is a
  separate, larger feature).
- **Left-click (slot 0)** and **DPI-switch (slot 5)** remain fixed (primary click and
  the DPI cycler); their hotspots are display-only.
- Remapping for non-BLAZE devices (no other device declares `SupportsButtonRemap` yet).
- Keyboard key-assignment (the keyboard `KeyAssignmentsSectionView` is untouched).

## Key constraint that shapes the whole design

**The key matrix cannot be read back from this firmware.** `0x83` returns only a
9-byte ACK header, there is no 64-byte input endpoint, and feature reads return zeros
(see the read-back matrix in [README.md](../../mouse/blaze/README.md) and
[engineering-notes.md](../../mouse/blaze/engineering-notes.md)). Therefore:

1. **The app is the source of truth.** Assignments are persisted locally per device;
   "read what's assigned from the device" is fulfilled by reflecting that persisted
   state (there is no honest device read).
2. **Every write sends the full desired matrix**, not a delta. The device host rebuilds
   block 2 from a known-good **baseline** (the byte-exact factory matrix) and overlays
   the current assignments for slots 1–4, leaving the fixed slots (DPI loop 5, rate
   loop 6, LED cycle 7) and slots 0/8–15 exactly as the factory matrix has them. This
   is a read-modify-write where the "read" is replaced by the documented baseline.
3. **Block-2-only writes.** `status=0x04, times=1` — never an all-blocks write (that is
   what previously bricked the buttons). The recovery tool
   (`ProoveGaming.Device.Testing` → `X43Mouse.RestoreAll()`) stays the safety net.

> Note: the baseline matrix builder (`CreateDefaultKeyMatrix`) was recently deleted as
> dead code; this feature reinstates it as the write baseline. Its byte-exact values
> are preserved in [key-matrix.md](../../mouse/blaze/key-matrix.md).

## Options considered

### A. Source of truth for assignments
- **A1 — App-side local persistence (recommended).** App persists `(vid,pid) → slot →
  action` and sends the full desired set on every change. Survives host/app restart,
  matches how VS750 behaves, and is the only option given no device read-back.
- **A2 — DeviceHost-side cache only.** Lost on host restart with no way to reload from
  the device → UI would show wrong/empty state after a restart. Rejected.

### B. Where the action → key-matrix bytes are encoded
- **B1 — Semantic all the way (recommended).** A gRPC `MouseButtonAction` enum; the App
  sends `(slot, action)`; `DeviceService` maps to an Infrastructure enum;
  `EasenMouseService` owns the byte encoding. Protocol knowledge stays in Infrastructure,
  mirroring the existing `DpiStage → DpiStageParams` pattern.
- **B2 — App computes raw 4-byte codes.** Leaks the wire format into the UI and makes a
  risky matrix write trivially corruptible from the client. Rejected.

### C. UI binding model
- **C1 — Per-hotspot binding (recommended).** Selecting an (assignable) hotspot drives
  the action catalog: it highlights that button's current assignment, and picking an
  option assigns it to *that* hotspot. Requires refactoring `ButtonActionGroups` from a
  single global selection into a picker bound to `SelectedHotspot`.
- **C2 — Keep the current global group selection.** Cannot express per-button state.
  Rejected (it's the current stub and doesn't meet the requirement).

## Recommended approach

**Host-owned store + B1 + C1.** The **DeviceHost** owns and persists the bindings;
a shared gRPC `MouseButtonAction` enum carries intent; `EasenMouseService` rebuilds
block 2 from the factory baseline + overlays and writes block 2 only; the Buttons UI is
a per-hotspot picker gated on `SupportsButtonRemap` that reads current bindings from the
host. (This replaces the earlier "A1 App LocalStorage" option per locked decision #4.)

### Data flow

```
Buttons UI (per hotspot) --pick action--> DeviceDetailsViewModel.Buttons
  -> IUsbHidDeviceService.SetButtonBindingsAsync(vid,pid, [(slot,action)…])   // full set
      -> gRPC SetButtonBindings(repeated ButtonBinding{slot, MouseButtonAction})
          -> DeviceService:
               1. IButtonBindingStore.Save(vid,pid, bindings)   // persist to disk
               2. resolve as IMouseRgbService, map enum→Infra
                  -> EasenMouseService.SetButtonBindingsAsync:
                       matrix = CreateDefaultKeyMatrix() (baseline)
                       for each binding: matrix[slot*4 .. +4] = Encode(action)
                       WriteParam1BlockAsync(stream, 2, matrix)   // block 2 only

On device open: App calls GetButtonBindings(vid,pid)
  -> DeviceService -> IButtonBindingStore.Load(vid,pid) -> reflect in hotspots + highlight
```

### Host-side persistence store

- `IButtonBindingStore` / `ButtonBindingStore` (singleton, DeviceHost or Infrastructure):
  a JSON file keyed by `"vid:pid" → { slot → action }`, loaded on startup, saved on
  every `SetButtonBindings`.
- Location: `%ProgramData%/ProoveGaming/button-bindings.json`
  (`Environment.SpecialFolder.CommonApplicationData` — writable by the Windows service,
  survives host restart). The store is the single source of truth; the device itself is
  never read (it can't be).
- Optional/future: on device connect, re-apply the persisted matrix so the device always
  matches stored intent (deferred — every key-matrix write carries brick risk).

### Action encoding (source: [x43_protocol_904a4f8.md §V](../../x43_protocol_904a4f8.md))

Matrix entry order is `[Byte0, Byte1, Byte2, Mode]`. `/coder` must transcribe each row
from §V exactly; representative targets:

| Action | Mode | Entry `[b0,b1,b2,mode]` |
|--------|------|-------------------------|
| Left / Right / Middle / Fwd / Back | 0x02 | `F0/F1/F2/F4/F3, 00, 00, 02` |
| Play/Pause | 0x03 | `CD, 00, 00, 03` |
| Next / Previous | 0x03 | `B5 …` / `B6 …` |
| Stop | 0x03 | `B7, 00, 00, 03` |
| Mute / Vol+ / Vol− | 0x03 | `E2` / `E9` / `EA, 00, 00, 03` |
| Media Player (Media Select) | 0x03 | `83, 02, 00, 03` |
| WWW Home | 0x03 | `23, 02, 00, 03` |
| My PC (My Computer) | 0x03 | `94, 02, 00, 03` |
| Search (WWW Search) | 0x03 | `21, 02, 00, 03` |
| **Desktop** | 0x01 (keyboard) | **Win+D = `07, E3, 00, 01`** — unverified, see Open questions |

## File-level change list

### gRPC contract
- `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto` — add `enum MouseButtonAction`, `message ButtonBinding { int32 slot; MouseButtonAction action; }`, `SetButtonBindingsRequest { vid, pid, repeated ButtonBinding }` / `SetButtonBindingsResponse { bool success }`, **`GetButtonBindingsRequest { vid, pid }` / `GetButtonBindingsResponse { repeated ButtonBinding }`**, RPCs `SetButtonBindings` and `GetButtonBindings`; add `bool supports_button_remap = 12;` to `DeviceCatalogEntry`.

### Infrastructure
- `src/ProoveGaming.Infrastructure/Classes/MouseButtonAction.cs` — **new**: Infrastructure enum mirroring the proto action set + a `ButtonBinding(int Slot, MouseButtonAction Action)` record.
- `src/ProoveGaming.Infrastructure/Interfaces/IMouseRgbService.cs` — add `Task<bool> SetButtonBindingsAsync(IReadOnlyList<ButtonBinding> bindings)`.
- `src/ProoveGaming.Infrastructure/Services/EasenMouseService.cs` — reinstate `CreateDefaultKeyMatrix()` (baseline, byte-exact from key-matrix.md); add `EncodeAction(MouseButtonAction)→byte[4]`; implement `SetButtonBindingsAsync` (baseline + overlay slots 1–4, write block 2 only via existing `WriteParam1BlockAsync(stream, 2, matrix)`; guard slot range to 1–4).
- `src/ProoveGaming.Infrastructure/Classes/DeviceMap.cs` — add `bool SupportsButtonRemap`.

### DeviceHost
- `src/ProoveGaming.DeviceHost/Interfaces/IButtonBindingStore.cs` + `Services/ButtonBindingStore.cs` — **new**: JSON-file persistence at `%ProgramData%/ProoveGaming/button-bindings.json`, `Load(vid,pid)` / `Save(vid,pid, bindings)`, loaded on startup. (Infrastructure is an alternative home if we want it reusable; DeviceHost is fine since it's host-owned state.)
- `src/ProoveGaming.DeviceHost/Program.cs` — register `IButtonBindingStore` as a singleton.
- `src/ProoveGaming.DeviceHost/Services/DeviceService.cs` — implement `SetButtonBindings` (persist to store, then resolve `as IMouseRgbService`, map proto enum → Infra, call `SetButtonBindingsAsync`) and `GetButtonBindings` (read from store); populate `supports_button_remap` in `MapHidWithCatalog`.
- `src/ProoveGaming.DeviceHost/Services/HidDeviceMapRegistry.cs` — carry `SupportsButtonRemap` from `DeviceMap` into the `DeviceCatalogEntry`.
- `src/ProoveGaming.DeviceHost/appsettings.json` — BLAZE (1241/41541): add `"SupportsButtonRemap": true`.

### App
- `src/ProoveGaming.App/Services/IUsbHidDeviceService.cs` + `Services/UsbHidDeviceService.cs` — add `SetButtonBindingsAsync(...)` and `GetButtonBindingsAsync(int vid, int pid)` (both map to gRPC; the host is the store — no App-side persistence).
- `src/ProoveGaming.App/ViewModels/DeviceCardViewModel.cs` — add `bool SupportsButtonRemap` (populated from catalog entry, alongside `SupportsRgb`).
- `src/ProoveGaming.App/ViewModels/DeviceDetails/ButtonActionGroupViewModel.cs` — add a semantic `MouseButtonAction? Action` (and `Slot`-agnostic) field to `ButtonActionOptionViewModel` so an option carries what it binds to.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Buttons.cs` — rebuild the catalog with the Standard / Recommended / System groups mapped to `MouseButtonAction`; make `SelectButtonAction` assign to the **selected hotspot**, call `SetButtonBindingsAsync` with the full current set, and persist; add a slot map (right→1, scroll→2, forward→3, back→4).
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs` — mark assignable vs fixed hotspots; in `SetDevice`, load persisted bindings and reflect; expose `SupportsButtonRemap`/`IsButtonRemapSupported` for gating.
- `src/ProoveGaming.App/Views/DeviceDetails/Sections/ButtonsSectionView.axaml` (+`.axaml.cs`) — show the catalog for the selected assignable hotspot, highlight the current assignment, show a "revert to standard" affordance (it's just the Standard group), and a "not supported on this device" state when `!SupportsButtonRemap`; disable the picker for fixed hotspots (left, DPI).
- `src/ProoveGaming.App/Resources/Strings.en.axaml` + `Strings.uk.axaml` — add strings: Standard group + items, System items (My PC / Desktop / Search), the "not supported" message, and any renamed group titles.

## Risks

- **Bricking the buttons.** Any block-2 write is the historically dangerous op. Mitigations: block-2-only writes, a correct baseline (from key-matrix.md), never an all-blocks write, and the `X43Mouse.RestoreAll()` recovery tool on standby. `/coder` should hardware-test on the working-LED unit and be ready to recover.
- **Baseline drift.** If the on-device matrix ever differs from our baseline (factory reset, another app), overlaying our baseline could revert those slots. Acceptable given the app is the manager; document it.
- **Stale UI vs device.** Persisted state can diverge from reality (no read-back). Same accepted tradeoff as debounce/lift.
- **`Desktop` action may not exist** as a single HID usage; the Win+D keyboard-combo entry is unverified.
- **Fixed-slot preservation.** Encoding the baseline wrong for slots 5/6/7 would break the DPI/rate/LED buttons — the baseline must match key-matrix.md exactly.
- **gRPC + capability plumbing** touches the proto (both sides regenerate) — keep client/server in lockstep.

## Open questions — resolved

1. **Left-click** — RESOLVED: locked, not remappable.
2. **Desktop action** — RESOLVED: try Win+D (`07 E3 00 01`), keyboard mode.
3. **Persistence** — RESOLVED: host-side JSON store, not App LocalStorage.
4. **Wheel-click** — RESOLVED: remappable, labelled "Wheel click".
5. **Non-BLAZE devices** — omit `SupportsButtonRemap` → show "not supported" state (keep the Buttons nav visible).

Plan ready. Implementing now (host store → contract → Infrastructure → DeviceHost → App → view).
