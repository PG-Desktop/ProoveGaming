# Plan: Mouse macros (record → upload → assign) for BLAZE

## Goal

Let the user record a **macro** — a timed sequence of key/mouse events — store it, upload
it to the mouse's flash, and **assign it to a remappable button** so the device replays it
in hardware. Build on the existing button-remapping pipeline and the half-built macro UI.
Device mechanics: [docs/mouse/blaze/macros.md](../../mouse/blaze/macros.md).

## Scope

**In:**
- A macro model (name + repeat count + repeat mode + ordered events with delays).
- **Recording** key/mouse events with timing in the App.
- **Encoding** to the device byte format (§III) and **uploading** via command `0x04`.
- **Assigning** a macro to a button (key-matrix mode `0x05`) — extends the existing
  button-action model with a "Macro" action carrying a macro index.
- **Host-side persistence** of macros (reuse the `ButtonBindingStore` pattern) and of the
  macro→button assignment (already handled by the binding store, extended).
- Capability-gated like remapping (a `SupportsMacros` flag; BLAZE = true).

**Out (future / explicit):**
- Reading macros back from the device (impossible — write-only; host store is the truth).
- Multimedia keys inside macros (firmware disallows).
- Per-profile macro banks (single profile 0 for now; 48-macro budget).
- Advanced editor (drag-reorder, per-event delay tuning) beyond a basic recorded list.

## Key risk that shapes the rollout

**Command `0x04` is unverified on this unit** (never seen in a VS750 capture), and
**binding writes the brick-prone key-matrix block 2**. So the plan is **phased**, with a
minimal hardware-testable slice first.

## Phased approach

### Phase 1 — end-to-end vertical slice (verify `0x04` + binding)
A hard-coded / minimal macro (e.g. the spec's "1 2 3") uploaded and bound to a button, so
we confirm on hardware that `0x04` takes and the mode-`0x05` binding fires — **before**
building the recorder UI. Deliverables:
- `EasenMouseService.UploadMacroAsync(index, MacroDefinition)` — encodes + sends `0x04`.
- Extend the button-action model with a **`Macro` action** (carries a macro index) →
  `EncodeAction` emits `[index, 00, type, 05]` into block 2.
- gRPC `SetMacro` RPC + `MacroDefinition` message; `DeviceService` wires it.
- A throwaway trigger (temporary button in the macro UI, or a `Device.Testing` entry) to
  upload a fixed macro and bind it — **hardware test with `X43Mouse.RestoreAll()` staged.**

**Gate:** only proceed to Phase 2 once `0x04` + binding are confirmed working on hardware.

**Status:** spike implemented in `src/ProoveGaming.Device.Testing/Program.cs` — interactive
and reversible (binds the Forward button to a "type 1 2 3" macro, reverts on keypress; the
revert writes the full factory matrix, so it self-recovers). Run:
`dotnet run --project src/ProoveGaming.Device.Testing` (stop the DeviceHost first). Awaiting
hardware result before Phase 2.

### Phase 1 result (2026-07-12) — CONFIRMED
`0x04` upload + mode-`0x05` bind work on BLAZE. Confirmed protocol (upload header byte6=`00`,
data `[repeat_lo,repeat_hi]`+`[attr,code]`+`00 00`, bind entry `[index,00,type,05]`) is in
[macros.md](../../mouse/blaze/macros.md). Mouse-button events (code `0xF0`) also play inside
macros (double-click test passed). **Proceed to Phase 2.**

### Phase 2 — recorder, persistence, device assignment, UI

Concrete scope from the confirmed protocol + the existing half-built UI:

**A. Device-agnostic macro model** (Infrastructure):
- `MacroDefinition { string Name; int RepeatCount; MacroRepeatMode Mode; IReadOnlyList<MacroEvent> Events }`.
- `MacroEvent { byte KeyCode; bool IsPress; int DelayMs }` — `KeyCode` = USB HID code
  (keyboard) or mouse-button code `F0–F4`.
- `MacroRepeatMode { PlayCount=0, LoopForever=1, WhileHeld=2 }` → the key-matrix macro type byte.
- Portable — reusable across any macro-capable device; only the encoder is per-device.

**B. Recorder** (App): `MacroRecorder` captures **keyboard + mouse-click** events
(window-focused `KeyDown`/`KeyUp` + pointer press/release on the record surface) with
timestamps → `MacroEvent`s (delays = inter-event gaps, quantised ×10 ms, clamped ≤1.27 s;
honour "auto-record delays"). An Avalonia `Key`→HID-code table (+ mouse buttons → `F0–F4`).
Wire the **"START RECORDING"** button (`MainWindow.axaml`, currently no `Command`) to
start/stop; the captured events render as a scrollable list (see item spec).

**Recorded-item UI spec** (new `MacroEventViewModel` + template — nothing exists today):
- Row size **338 × 40** (same as the START RECORDING button); background **`#0DFFFFFF`**
  (the RGB-effect item colour), **4 px** vertical gap between rows.
- Layout: `[type icon] [action name] … [ms] [↑/↓]`
  - **type icon**: `Keyboard.svg` for keys, `Mouse.svg` for mouse clicks.
  - **action name**: e.g. "Left Click", "A", "Ctrl".
  - **ms**: the event's delay (`MacroEvent.DelayMs`) — measured gap when auto-delays on.
  - **↑/↓ arrow** (`Arrow-Up.svg`/`Arrow-Down.svg`): **release (↑) vs press (↓)** for that step.
- **On hover**: reveal **Edit** (`Edit.svg`) and **Remove** (`Delete.svg`/`Remove.svg`) actions.
- The recorder list area is a `ScrollViewer` with a **visible thumb** (unlike the hidden
  scrollbars used elsewhere).

**C. Storage — App SQLite (decided 2026-07-12), NOT a host JSON store**:
The **macro library is App-owned** in the existing SQLite DB (`LocalStorageService` →
`%LocalAppData%/ProoveGaming/proovegaming.db`), same store profiles/presets will live in —
because macro↔profile↔preset is a relational, per-user concern (foreign keys, not
cross-file IDs). The **host stays a stateless executor**: it receives a macro's definition
*inline* when a button is assigned and uploads it; it does not own the library.
- App: new `macros` table(s) via `LocalStorageService` (id, name, repeat, mode, events);
  later joined to profiles/presets.
- No `GetMacros`/`SetMacro` RPC and no host `MacroStore` — the macro rides inline in the
  binding (below). (The earlier host-owned button-binding store may converge App-side when
  profiles land; not in scope now.)

**D. Assignment flow** (reuses the button pipeline; macro travels inline):
- `ButtonBinding` gains an optional **`MacroDefinition macro`** (set only when
  `action = Macro`). A saved macro appears in the button catalog (the existing
  `_macrosesGroup`) as a **`Macro` action**; assigning sends the full definition inline.
- `EasenMouseService.SetButtonBindingsAsync`: for each `Macro` binding, **encode + upload the
  macro (`0x04`, byte6=`00`, 128 B)** to a sequential flash index (0..N, ≤4 assignable
  buttons ≪ 48), then write the key-matrix entry `[index, 00, type, 05]`. `EncodeAction`
  returns null for `Macro` (handled specially since it needs the runtime index).
- The host's binding store records the button → (Macro, macro name/id) for reflection; the
  App resolves the id to a library macro for display.

**E. Capability flag** `SupportsMacros` — config → gRPC → App (mirror `SupportsButtonRemap`);
gate the Macros section + assignment. BLAZE = `true`.

**F. UI fixes**:
- **Tabs** (`MacrosSectionView.axaml`): make Macros/Commands VM-driven (`ActiveMacroTab`),
  **Macros selected by default** (today the `RadioButton IsChecked="True"` doesn't visibly
  take — drive it from the VM). Content already gates on the tab.
- **Create-Macro dialog** (`MainWindow.axaml`): wire START RECORDING + show recorded events;
  `SaveCreateMacro` builds a `MacroDefinition` (name + repeat + mode + events) and saves it to
  the library; the macro then shows in the Macros list and the button catalog.
- **Commands tab**: today a hardcoded list (Volume/Brightness/Back…). For v1, make it
  selectable and back it with the **existing button-action catalog** (single predefined
  actions) rather than new data — confirm intent (open question).
- Load macros + reflect assignments on device open (like button bindings).

## Options considered

### A. Macro action representation in the binding model
- **A1 (recommended)** — add `Macro` to `MouseButtonAction` as a family, but the *index*
  must travel too. Since `MouseButtonAction` is a flat enum, carry the macro index in the
  `ButtonBinding` (add an optional `macro_index`), and `EncodeAction` special-cases the
  `Macro` action to emit mode `0x05` with that index. Keeps one binding path.
- **A2** — a separate `SetMacroBinding` RPC/store distinct from button bindings. More
  surfaces, and a button can hold *either* an action or a macro, so unifying in
  `ButtonBinding` (A1) is simpler and keeps "one button → one thing".

### B. Where macros are stored
- **B1 (recommended)** — host-side `MacroStore` (JSON at `%ProgramData%/ProoveGaming/`),
  mirroring `ButtonBindingStore` (async, atomic write, name-based, durability rules).
- **B2** — App-side. Rejected for the same reason as bindings (host owns device state; a
  LocalSystem service can't use per-user folders).

### C. Recording input
- **C1 (recommended)** — capture at the App/OS level (global key/mouse hook or focused
  capture) into an ordered event list with timestamps; convert deltas → 10 ms-quantised
  delays at encode time.
- **C2** — record on the device. Not supported by this firmware.

## Recommended approach

Phase 1 vertical slice → hardware-gate → Phase 2. **A1 + B1 + C1.** Macros are a new
host-persisted entity; assigning one reuses the block-2 key-matrix write with a new action
type + a macro index carried on `ButtonBinding`; uploading uses a new `0x04` path in
`EasenMouseService`.

### Encoding (from [macros.md](../../mouse/blaze/macros.md))
`MacroDefinition { int RepeatCount; MacroRepeatMode Mode; MacroEvent[] Events }`,
`MacroEvent { byte Code; bool IsPress; int DelayMs }`. Encoder: `[repeat_lo, repeat_hi]`
then per event `[(IsPress?0:0x80) | clamp(DelayMs/10, 0, 0x7F), Code]`, then `00 00`,
padded to 128 bytes. Key codes from §V.

## File-level change list

### Phase 1
- `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto` — `MacroEvent`,
  `MacroDefinition`, `SetMacroRequest`/`SetMacroResponse`, RPC `SetMacro`; add
  `int32 macro_index = 3` to `ButtonBinding`; add `Macro` to `MouseButtonAction`; add
  `bool supports_macros = 13` to `DeviceCatalogEntry`.
- `src/ProoveGaming.Infrastructure/Classes/MouseButtonAction.cs` — add `Macro` action;
  add `MacroDefinition` / `MacroEvent` / `MacroRepeatMode`; add `MacroIndex` to
  `ButtonBinding` (or a parallel `ButtonBinding` with an optional index).
- `src/ProoveGaming.Infrastructure/Interfaces/IMouseRgbService.cs` — `Task<bool> UploadMacroAsync(int index, MacroDefinition macro)`.
- `src/ProoveGaming.Infrastructure/Services/EasenMouseService.cs` — `UploadMacroAsync`
  (encode + `0x04` header + 128-byte data on the OUT endpoint via the existing stream
  helper); `EncodeAction` handles `Macro` → `[index, 00, type, 05]` (needs the index +
  type from the binding); `RampageKeyboardRgbService` no-ops `UploadMacroAsync`.
- `src/ProoveGaming.DeviceHost/Services/DeviceService.cs` — `SetMacro` RPC; thread
  `macro_index` through `SetButtonBindings`.
- `src/ProoveGaming.DeviceHost/appsettings.json` + `AllowedDevice.cs` +
  `DeviceInfoProvider.cs` + `DeviceMap.cs` + `HidDeviceMapRegistry.cs` — `SupportsMacros`
  flag (mirror `SupportsButtonRemap`); BLAZE = true.
- Temporary hardware-spike trigger (App button or `src/ProoveGaming.Device.Testing`).

### Phase 2
- `src/ProoveGaming.DeviceHost/Services/MacroStore.cs` (+ interface, Program registration)
  — host-side JSON persistence (mirror `ButtonBindingStore`: async, atomic, name-based,
  don't-clobber-on-failed-load).
- `src/ProoveGaming.App/Services/IUsbHidDeviceService.cs` + `UsbHidDeviceService.cs` —
  `SetMacroAsync` / `GetMacrosAsync`.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/*` — replace the stub `MacroViewModel`
  with real events; a recorder (`MacroRecorder`); wire create/edit/list; the macros
  appear in the button catalog as `Macro` actions carrying their index; load on device open.
- `src/ProoveGaming.App/Views/DeviceDetails/Sections/MacrosSectionView.axaml(.cs)` —
  recorder UI (record/stop, event list, repeat count, repeat-until-released).
- `src/ProoveGaming.App/Resources/Strings.en.axaml` / `Strings.uk.axaml` — macro strings.

## Risks

- **`0x04` unverified** — the whole feature hinges on it; Phase 1 de-risks before UI cost.
- **Brick risk** on the mode-`0x05` binding write (block 2) — recovery tool staged, block-2-only writes, exact baseline.
- **Macro size / packet count** — the doc says 128 bytes via `0x04` but §I mentions 250;
  confirm the real buffer size on hardware (start with 128, terminator-padded).
- **Global input capture** for recording has OS-permission and focus considerations.
- **Contract churn** — proto changes both sides; `ButtonBinding` gains `macro_index`
  (default 0 is safe for non-macro actions).
- **Key-code coverage** — need a keyboard/mouse code table in the encoder (subset of §V).

## Decisions (Phase 2, 2026-07-12)

1. **Recording** — capture **keyboard + mouse clicks**, **window-focused** (while the
   Create-Macro dialog is active). No global OS hook. `KeyDown`/`KeyUp` + pointer
   press/release on the dialog's record surface, with a clear affordance so UI clicks
   (Save/Cancel) aren't recorded.
2. **Commands tab** — **placeholder for now**: make it selectable and fix the default
   (Macros selected), but wire no real command data yet. v1 focuses on Macros.
3. **Macro type** — default to "play N times"; confirm the type byte (`0x00` vs the `0x01`
   VS750 used) with a quick hardware check during implementation.
4. **Macro flash-index allocation** — first-free of 0–47 per device, mapping kept in the host
   store; re-upload on change.

## Phase-1 status

Spike done + hardware-confirmed (double-click test passed). Ready to implement Phase 2.

Plan ready. Invoke /coder to implement Phase 2 (or say "go" and I'll start).
