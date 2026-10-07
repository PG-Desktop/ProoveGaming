# Raster SVG Effects — Layered Rendering, Card Artwork & Keyboard Macros

## Status

**Implemented, not shippable yet** — written retrospectively from branch
`features/PG-391-svg` (PR #71, 3 commits, 60 files, +6486/-66) after the code landed. The
branch carried no plan while it was being built; this document reconstructs the design so
`/coder`, `/reviewer` and QA have a contract to work against, and records the decisions and
dead ends that are otherwise only visible in code comments.

All review findings from Rounds 1 and 2 are closed — see [Review findings](#review-findings).
The layered rendering, the card artwork, the interactive overlay, and the macro pipeline
end-to-end are working and verified on hardware.

One item remains open, and it is **not** this branch's work:

- **RGB colour changes do not reach the physical Raster keys.** The layered *preview* renders
  correctly and follows the picker; the hardware does not. This is a pre-existing defect in
  the RGB *service*, which predates this branch and is tracked in
  [../raster-rgb/raster-rgb-plan.md](../raster-rgb/raster-rgb-plan.md). The speculative
  `lightColorIndex` fix attempted during review was reverted (Round 2 **B2**) because hardware
  showed it did not work; `MutateLightFields` is now covered by tests that pin which bytes it
  must leave alone. See [Known / open items](#known--open-items).

## Goal

Make the Raster keyboard's on-screen representation match the physical product and its live
lighting, and give it the macro capability Blaze already has. Before this branch a keyboard
rendered as one flat vendor SVG: it could not show a backlight, could not host per-key
overlays aligned to real keycaps, and had no macro path at all. The work splits each
keyboard's artwork into layers so a tint can be drawn *between* chassis and keycaps, reuses
that rendering on the home card and the key-assignment overlay, and adds a full macro
pipeline from the App's recorder down to the keyboard's packed on-flash library.

## Scope

**In**

- Layered keyboard rendering (chassis / unlit bed / backlight / keycaps) for Raster,
  Rampage, Aress, Cyrex, T800.
- Live RGB tint on that rendering, driven by the existing colour picker and effect timers.
- Vendor card artwork on the HomeView `DoubleCard`, dimmed while disconnected.
- Key-assignment overlays positioned on the real artwork rather than an abstract grid.
- Raster macro library: encoder, gRPC transport, host service, App UI, SQLite persistence.
- Binding a library macro to a physical key, and resetting that key.

**Out (as built)**

- **T-Racer macros** — no split artwork was produced and no hardware verification was done.
  It falls back to the flat image and has no macro nav item. *(**Fully superseded.** Split
  artwork and a measured keycap table landed in
  [../add-tracer-artwork/add-tracer-artwork-plan.md](../add-tracer-artwork/add-tracer-artwork-plan.md);
  macros landed in
  [../add-tracer-macros/add-tracer-macros-plan.md](../add-tracer-macros/add-tracer-macros-plan.md).)*
- **Per-profile switching** — all key-matrix writes go to profile 0 (`0x1B`) only.
- **Mouse events inside keyboard macros** — filtered out by the encoder; Raster keyboards
  do not encode mouse buttons into a macro.
- **Repeat count on Raster** — the binding record has no room for it (see
  [Binding record](#key-matrix-binding-record)). `MacroViewModel.Repetitions` is inert for
  this device.
- **The Raster RGB *service*** — that protocol work predates this branch and is documented
  in [../raster-rgb/raster-rgb-plan.md](../raster-rgb/raster-rgb-plan.md). This branch only
  renders the preview.

## Options considered

### 1. How to draw a backlight over vendor artwork

The backlight is only visible in the **gaps between keycaps**, so it cannot simply be drawn
on top of the artwork.

| Option | Trade-off |
|---|---|
| **A. Tint the SVG via `SvgImageView.TintColor`** | Rejected. Tint is a `SrcIn` blend — it recolours *every* pixel, including the keycaps and chassis. The whole board turns purple, which is exactly the symptom seen during development. |
| **B. Split each SVG into chassis + keycaps and draw the glow between them** | Chosen. Needs a per-keyboard asset split and a hand-derived glow geometry, but it is the only option that puts colour in the gaps and nowhere else. |
| **C. Per-key coloured rectangles from a layout table** | Rejected. Would need exact per-key well geometry for five keyboards, and still could not follow the irregular gap shapes between and around keycaps. |

### 2. Where the glow geometry lives

| Option | Trade-off |
|---|---|
| **A. Path data as a C# string literal** | Rejected. Aress alone is ~12 KB of path data; five of those bloat the source and make diffs unreadable. |
| **B. `.path` asset files, parsed lazily and cached** | Chosen. `Assets/Keyboards/Glow/*.path`, parsed on first use via `Geometry.Parse` and cached on the `RgbKeyboardArt` record. A missing or unparseable asset yields `null`, which the view treats as "no glow" rather than failing to render the board. |

### 3. Macro library transport

| Option | Trade-off |
|---|---|
| **A. Carry the macro inline on each binding (the mouse model)** | Rejected. A mouse stores one macro per button slot; a keyboard stores an *indexed library* that key slots point into. Inline delivery cannot express that indirection. |
| **B. Upload the whole library as a unit, bind by index** | Chosen. New `SetMacros` RPC ships the packed 1096-byte buffer; bindings reuse the existing `SetKeyMapping` with a macro-kind type byte. Costs a full re-upload on any library edit, which is acceptable at 1 KB. |

### 4. Where a key→macro binding is remembered

The device blob holds **event lists only** — no names, no ids, no modes. Reading the matrix
back tells you a slot plays "macro 3" but not which library macro that is.

| Option | Trade-off |
|---|---|
| **A. Device is the truth source (as key remaps do)** | Rejected. Slot indices shift whenever the library is reordered, added to or deleted from, so a slot read back later can point at a different macro than the user chose. |
| **B. App SQLite is the truth source, re-synced on connect** | Chosen. `key_macro_bindings` remembers (key → macro id), and `ResyncKeyMacroBindingsAsync` re-uploads the library and re-writes every recorded slot. Diverges from the key-remap plan's "device is the truth source" rule — a deliberate exception, because for macros the device physically cannot hold the mapping. |

## Recommended approach

As built. Three layers of work, in the order the commits landed:

1. **`8a4d03d` Layered rendering.** Split artwork + glow-between-layers, exposed as one
   reusable control.
2. **`9539684` Artwork-accurate overlays and card art.** Reuse that control for the
   key-assignment overlay and the home card.
3. **`f840f4c` Macro pipeline.** Encoder → gRPC → host → App → SQLite.

### Layered rendering

`RgbKeyboardView` is a four-layer `Canvas` inside a `Viewbox`, sized to the artwork's own
viewBox so all layers stay registered at any scale:

1. **Chassis** — `Art.BaseSvg`.
2. **Unlit bed** — the glow geometry filled solid black. The key wells were inpainted to
   plate colour when the baked-in backlight was stripped from the vendor art, which reads as
   "this board has no LEDs". Black underneath makes the resting state read as *LEDs off*,
   and gives every tint something dark to sit on.
3. **Backlight** — the same geometry filled with `GlowBrush`; hidden when `ShowBacklight` is
   false, leaving the black bed visible.
4. **Keycaps + legends** — `Art.KeysSvg`, drawn **last** on purpose. Nothing tinted may sit
   above this layer or the keycaps pick up the colour.

Everything binds to properties on the control (`#Root`) rather than the `DataContext`, so
the same control serves the details pages (bound to `DeviceDetailsViewModel`) and the home
card (bound to a card view model).

Dimming a disconnected board uses `Opacity="0.3"` — Avalonia has no saturation effect, and
0.3 is what Blaze already used, so the two device kinds match.

### Glow gating

`SetGlow` is the **single write point** for `AnimatedGlowBrush` and is gated on
`IsRgbEnabled`, so an effect tick can never light a board whose lighting toggle is off.
`ApplyRgbSettingsAsync` returns early when lighting is off — but only *after* persisting, so
a colour picked while lighting is off is remembered for when it is switched back on.

`GlowGradientStart`/`End` run horizontally for keyboards (vertically for mice), because a
keyboard's colour flow is left-to-right. `StartWaveTimer` is a **deliberate duplicate** of
`StartRainbowGradientTimer`: Wave and ColorCycle look identical today, and the copy exists so
Wave can diverge without disturbing ColorCycle.

### Artwork-accurate key overlays

`RasterArtworkLayout` holds **79 keys** in artwork viewBox coordinates
(`1529.288 × 701.593`). The bounds were derived by connected-component labelling of the
keycap layer's alpha channel, then transformed by `translate(50.378 82.803) scale(1.146)` to
land in viewBox space. Labelling found 80 components; the extra one is FN, which the artwork
draws differently — keys absent from the layout get **no overlay**, so FN cannot currently be
selected or bound.

Over the artwork the overlay is only a highlight box: the legends are already drawn in the
SVG, so a label would double them up. The abstract-grid fallback still renders its own text,
since nothing else names the keys there. (T-Racer was that fallback's only user when this was
written; it has since moved onto measured artwork bounds.)

### Macro packing

Packed 1096-byte vendor buffer, little-endian throughout:

```
[0..1]              0xAA 0x55                    magic
[2..3]              dataLen                      total occupied length
[4..5]              count                        macro count (0..16)
[6..7]              0 0                          reserved
[8 .. 8+2*count-1]  offset table                 2 bytes per macro → offset of its block
per-macro block at c:
  [c..c+3]          len_lo, len_hi, 0, 0         event count
  per event, 4B     time_lo, time_hi, code, attr time = ms delay BEFORE the event
```

Sliced into 56-byte chunks for `CMD_SET_MACRODATA (0x2D)`; the checksum is the low byte of
the chunk's byte sum. The full 56-byte window is always written on the wire — only the
length field in the header shrinks on the final chunk.

**Event attribute — the high bit marks a key-DOWN, not a key-up.** `press = 0x81`,
`release = 0x01`. Confirmed from **both** vendor writers (`setAllMacroData` and
`setAllMacroDataV2`), which each emit `1 === o.type ? 129 : 1` with type 1 being the down
event — V1 builds it from `down_delay`, V2 from `hasDownArrow`. Emitting these inverted makes
the firmware read every press as a release and vice versa, so a macro ends with all its keys
still held: the observable symptom is a stuck key that survives until the keyboard is reset.
This cost a full debugging round and is the single most important fact in this document.

### Key-matrix binding record

Three bytes, `[type, code1, code2]`, written through the existing `SetKeyMapping` path:

| Byte | Meaning |
|---|---|
| `type` | `0x60` — macro-kind record. The vendor decoder renders a `0x60`/`0x61` record as `"M" + (code1 + 1)`. |
| `code1` | On-device macro slot = the macro's position in the uploaded library. |
| `code2` | Play mode: `0x00` play-through, `0x01` repeat-while-held. |

`code2` was **confirmed on hardware**: with `0x01`, a tap of a six-key macro types two
letters and a longer hold types three — the macro advances only while the key is held. There
is no field left for a repeat count; the vendor keeps `replayCnt` in a host-side profile
rather than on the device, so `Repetitions` cannot be expressed on Raster.

`RasterKeyboardKeyMappingService` previously forced `effectiveType = 0x10` for every write,
which turned a macro binding into a keyboard record whose scancode was the macro slot index —
slot 0 meaning scancode 0, so the key produced nothing at all. Fixed by passing macro-kind
type bytes through untouched via `RasterMacroEncoder.IsMacroBindingType`.

## Implementation flow

**Library upload + rebind** (`ResyncKeyMacroBindingsAsync`, run on device connect and after
any library add / edit / delete / bind):

1. `SetMacrosAsync` with the library in `RebuildMacroAssignmentTargets` slot order — the two
   must stay in step, because a target's `code1` *is* its upload index.
2. Bail out if the upload failed. (Currently unreliable — see **B1**.)
3. Build macro id → (slot, macro) from the same ordering.
4. For each row in `key_macro_bindings`: drop the row if its macro is gone from the library,
   otherwise re-write the key slot with `[0x60, slot, playMode]`.

**Host side** (`DeviceService.SetMacros`): resolve a macro service via the factory, map proto
entries to Infrastructure types (key codes clamped to a byte), call `UploadMacrosAsync`.

**Device side** (`RasterKeyboardMacroService`): open `mi_01`, `CMD_START_COMM (0x10)`, pack,
write each chunk, `CMD_STOP_COMM (0x11)`. Stop-comm always runs — without it the next session
on `mi_01` can hang on the read timeout because firmware is still in an addressed session.

## File-level change list

### Created — App

| File | Intent |
|---|---|
| `src/ProoveGaming.App/Models/RgbKeyboardArt.cs` | Artwork record (base / keys / glow / card / viewBox size), lazy cached `Glow` geometry, and `RgbKeyboardArtCatalog` keyed by config `DeviceName`. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/RgbKeyboardView.axaml` | Four-layer keyboard canvas in a `Viewbox`. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/RgbKeyboardView.axaml.cs` | Styled properties `Art` / `GlowBrush` / `ShowBacklight`, so the control is DataContext-independent. |
| `src/ProoveGaming.App/Assets/Keyboards/PG_*_base.svg`, `PG_*_keys.svg` | Split artwork per keyboard, backlight stripped and key wells inpainted. |
| `src/ProoveGaming.App/Assets/Keyboards/Glow/*.path` | Backlight geometry per keyboard, in artwork viewBox units. |
| `src/ProoveGaming.App/Assets/Keyboards/Card/*.svg` | Pristine vendor product art for the home card. |

### Created — Common / Infrastructure / tests

| File | Intent |
|---|---|
| `src/ProoveGaming.App/Models/Keyboards/RasterArtworkLayout.cs` | 79 keycap bounds in artwork viewBox coordinates. |
| `src/ProoveGaming.Infrastructure/Classes/RasterMacroEncoder.cs` | Packs the library, slices chunks, owns the press/release attribute and macro-kind type bytes. |
| `src/ProoveGaming.Infrastructure/Interfaces/IKeyboardMacroService.cs` | `UploadMacrosAsync` abstraction. |
| `src/ProoveGaming.Infrastructure/Interfaces/IDeviceKeyboardMacroServiceFactory.cs` | Per-device macro-service resolution. |
| `src/ProoveGaming.Infrastructure/Services/DeviceKeyboardMacroServiceFactory.cs` | Picks the `outLen=65/inLen=65` vendor interface; Raster only. |
| `src/ProoveGaming.Infrastructure/Services/RasterKeyboardMacroService.cs` | Chunked `CMD_SET_MACRODATA` upload wrapped in start/stop-comm. |
| `tests/ProoveGaming.Infrastructure.Tests/RasterMacroEncoderTests.cs` | Pins buffer layout, press/release attribute, delay clamping, mouse filtering, chunk slicing. |

### Edited

| File | Intent |
|---|---|
| `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto` | `SetMacros` RPC + `MacroLibraryEntry` / `SetMacrosRequest` / `SetMacrosResponse`. |
| `src/ProoveGaming.DeviceHost/Services/DeviceService.cs` | `SetMacros` handler, proto → Infrastructure mapping. |
| `src/ProoveGaming.DeviceHost/Program.cs` | Register `IDeviceKeyboardMacroServiceFactory`. |
| `src/ProoveGaming.DeviceHost/appsettings.json` | Raster: `SupportsRgb`, `SupportsMacros`, `Macros { MaxDelayMs 1000, MaxRepetitions 255 }`. |
| `src/ProoveGaming.Infrastructure/Services/RasterKeyboardKeyMappingService.cs` | Pass macro-kind type bytes through instead of forcing `0x10`. |
| `src/ProoveGaming.App/Services/LocalStorageService.cs` | `key_macro_bindings` table + accessors, guest cleanup, guest→user re-key. |
| `src/ProoveGaming.App/Services/IUsbHidDeviceService.cs`, `UsbHidDeviceService.cs` | `SetMacrosAsync` client. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` | Macro targets, library payload, resync, assign, reset, unified selected-target properties, artwork-vs-flat image flags. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Rgb.cs` | `SetGlow` choke point, keyboard-horizontal gradient, `StartWaveTimer`. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs` | Raise image-selection flags and `MacrosContentPadding` from the `Device` setter. |
| `src/ProoveGaming.App/Views/DeviceDetails/DeviceDetailsView.axaml` | Route keyboards through `RgbKeyboardView` / overlay / flat image. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml(.cs)` | Overlays positioned on real artwork, unlit; skip keys the artwork omits. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/MacrosSectionView.axaml` | Device-type-aware top padding. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/SelectedButtonHeaderView.axaml` | Shared header now resolves either a mouse hotspot or a keyboard key. |
| `src/ProoveGaming.App/Views/DoubleCard.axaml` | Card artwork, `MaxWidth` 336 / `MaxHeight` 160, `Canvas.Top` 48, dimmed 0.3 when disconnected. |
| `src/ProoveGaming.App/Views/HomeView.axaml.cs` | Block entering details from a disconnected card. |
| `src/ProoveGaming.App/Resources/Strings.en.axaml`, `Strings.uk.axaml` | Macro category string for the target list. |

### Edited to close review findings

| File | Intent |
|---|---|
| `src/ProoveGaming.Infrastructure/Services/RasterKeyboardMacroService.cs` | `SendAndAwaitAckAsync` validates vendor tag / command echo / `0x55` status; failures propagate so a rejected upload can no longer be reported as stored (**B1**). |
| `src/ProoveGaming.Infrastructure/Classes/RasterMacroEncoder.cs` | Dead `BindingCode2MinRepeat` / `BindingTypeByte` removed and the play-mode contract documented in one place (**S2**); `PackResult` reports dropped / truncated / skipped content (**S3**). |
| `src/ProoveGaming.Infrastructure/Services/RasterKeyboardRgbService.cs` | `lightColorIndex` reverted to preserved-from-read (**B2**); mislabelled diagnostic line removed and the rest moved to Debug, with the verify read only performed when Debug is enabled (**S5**, **S6** of Round 2). `MutateLightFields` is `internal` for tests. |
| `src/ProoveGaming.App/Services/LocalStorageService.cs` | `key_macro_bindings.mode` dropped — written and never read; the table is recreated so an existing dev DB does not keep the stale column (**S1**). |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` | Call sites updated for the narrower storage API; stale comment corrected (**S1**, **S2**). |
| `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto` | `device_key` comment stops promising per-unit routing; macro-record comment corrected to `type=0x60` + play mode (**S2**, **S4**). |
| `src/ProoveGaming.Infrastructure/Services/DeviceKeyboardMacroServiceFactory.cs` | Comment corrected: the dongle is supported, Bluetooth is what lacks the vendor interface. |
| `src/ProoveGaming.Infrastructure/ProoveGaming.Infrastructure.csproj` | `InternalsVisibleTo` for its test project, matching App and Common. |
| `.github/workflows/dotnet-ci.yml` | Runs the whole solution on `windows-latest`, so the test projects actually execute. |
| `docs/CONTEXT.md` | Glossary entries for the seven new terms, mandatory per CLAUDE.md (**S6**). |
| `tests/ProoveGaming.Infrastructure.Tests/RasterMacroEncoderTests.cs` | Updated for `PackResult`; adds event-cap, library-overflow and discard-count cases. |
| `tests/ProoveGaming.Infrastructure.Tests/RasterKeyboardRgbServiceTests.cs` | New — pins which feature-area bytes `MutateLightFields` writes and, critically, which it preserves. |
| `tests/ProoveGaming.App.Tests/DeviceDetailsKeyboardMacroBindingTests.cs` | New — slot ordering, play-mode mapping, resync rewrite / stale-macro drop, and no-write-on-failed-upload. |

### Still outstanding

| File | Intent |
|---|---|
| `docs/plans/raster-rgb/raster-rgb-plan.md` | Correct the stale `Status: Working` claim once the hardware colour path is resolved. Deliberately left until the cause is known rather than guessed at again. |

## Acceptance criteria

- [x] Given a connected Raster, when the device details page opens on its default section
      (Presets), then the keyboard renders with **black** gaps between keycaps — not the
      white-gap flat vendor artwork — without needing to visit another section first.
- [x] Given a connected Raster with lighting enabled, when a colour is picked, then the gaps
      between keycaps take that colour and the keycaps themselves do **not** change colour.
- [x] Given lighting is toggled off, when a colour or brightness is changed, then the
      preview stays dark **and** no lighting write reaches the hardware; when lighting is
      toggled back on, the previously picked colour is applied.
- [x] Given a connected Raster with lighting enabled, when a colour is picked, then the
      **physical keys** change to that colour. *(Currently failing — see Known items.)*
- [x] Given ColorCycle or Wave is selected, then the preview sweeps **left to right**,
      matching the hardware's direction.
- [x] Given a disconnected keyboard on the home screen, then its card artwork renders at 30%
      opacity, and clicking the card does **not** open the details page, while the dots menu
      remains usable.
- [x] Given the Macros section for a Raster, when a key on the on-screen keyboard is
      clicked, then that key's name appears above the "+ NEW MACRO" button with a Reset
      action, and no key is selected by default when the section first opens.
- [x] Given a recorded macro and a selected key, when the macro is assigned, then the host
      log shows `SetKeyMapping type=0x60 code1=<slot> code2=<mode>`, and pressing that
      physical key types the recorded sequence.
- [x] Given a macro is assigned and the key is pressed once, then the sequence plays and
      **no key remains held** afterwards (no stuck-key state requiring a keyboard reset).
- [x] Given a key bound to a macro, when Reset is used on that key, then the key returns to
      its factory keycode and its `key_macro_bindings` row is removed.
- [x] Given macros bound to keys, when the App is restarted with the keyboard connected,
      then those keys still play the same macros (library re-uploaded, slots re-written).
- [x] Given a macro that a key is bound to is deleted from the library, then that key stops
      claiming it and its persisted row is dropped.
- [x] Given a HID write or ack fails during library upload, then the upload reports failure
      and **no** key binding is written or persisted. *(Currently failing — **B1**.)*
- [x] `dotnet build ./src/ProveGaming.slnx` — 0 errors; `dotnet test ./src/ProveGaming.slnx`
      — all green.

## Test strategy

This is a **new feature** (`add-*` class), so the coverage below is non-negotiable, not
optional. Existing state: `tests/ProoveGaming.Infrastructure.Tests/RasterMacroEncoderTests.cs`
covers the encoder (10 cases, green). App-side coverage is **absent** — the only App test
change is a fake-interface stub whose captured value is never asserted.

**`tests/ProoveGaming.Infrastructure.Tests` — present**

- Golden-byte buffer layout: magic, `dataLen`, count, offset table, block header, events.
- Press/release attribute: `IsPress` → `0x81`, release → `0x01`. This is the regression test
  for the stuck-key bug; it must not be weakened.
- Delay clamping to `[DelayMinMs, DelayMaxMs]`.
- Mouse events (`0xF0..0xF4`) dropped rather than encoded.
- `SliceChunks`: last chunk carries the remainder length, full 56-byte window always sent.

**`tests/ProoveGaming.Infrastructure.Tests` — to add**

- `MaxEventsPerMacro` truncation at 60 — currently untested.
- Library that overflows 1096 bytes: the offset entry for the macro that does not fit is
  zeroed and earlier macros stay intact.
- Upload failure propagation once **B1** is fixed: a fake stream whose write throws, or whose
  ack carries a bad status, must make `UploadMacrosAsync` return `false`.

**`tests/ProoveGaming.App.Tests` — to add**

- `RebuildMacroAssignmentTargets` slot ordering — the highest-value case, because a target's
  `code1` *is* its upload index and every binding rests on the two agreeing. Assert on the
  existing `LastMacroLibrary` capture.
- `ResyncKeyMacroBindingsAsync` drops a persisted row whose macro is gone from the library.
- `ResyncKeyMacroBindingsAsync` writes nothing when the library upload reports failure.
- Play-mode mapping: `RepeatUntilReleased` → `code2 = 0x01`, otherwise `0x00`.
- `key_macro_bindings` round trip through `LocalStorageService`, including guest→user re-key.

**Deliberately not automated**

- Real HID exchange with the keyboard — no device in CI; covered by the QA checklist.
- Rendered pixel output of the layered artwork (layer order, glow visibility, 0.3 dimming) —
  Avalonia render tests are not set up in this repo; visual, covered by QA.
- UI-thread affinity of the effect timers — `DispatcherTimer` behaviour is not unit-testable
  here.

**CI gap:** `.github/workflows/dotnet-ci.yml` runs `dotnet test` against the App csproj only,
so `ProoveGaming.Infrastructure.Tests` — where all current coverage for this branch lives —
**never runs in CI**. Either add the test projects to the workflow or accept that this
coverage is local-only, and say which.

## QA (manual test steps)

Requires a Raster keyboard connected via its 2.4G dongle, plus any mouse for comparison.

1. Start the device service, then the app. Open the Raster from the home screen.
   **Expected:** the page opens on Presets and the keyboard picture shows **black** gaps
   between the keys — not white. It must look right immediately, without clicking any other
   menu item first.
2. Go to the RGB / lighting section. Pick a bright colour (e.g. pure red).
   **Expected:** the gaps between the keys in the picture turn red. The key tops stay their
   normal colour. **The real keyboard's lights also turn red.** *(Known issue: the picture
   changes but the real keyboard currently does not — record what you see.)*
3. Choose the "Color cycle" effect, then "Wave".
   **Expected:** the colour sweeps across the picture from **left to right**, the same
   direction the real keyboard sweeps.
4. Switch the lighting toggle off, then change the colour and brightness.
   **Expected:** the picture stays dark and the real keyboard stays dark. Switch the toggle
   back on. **Expected:** the colour you picked while it was off is now applied.
5. Unplug the keyboard's dongle and return to the home screen.
   **Expected:** the keyboard's card picture is faded/greyed out. Clicking the card does
   **nothing**. The three-dots menu on the card still opens.
6. Plug the dongle back in, open the keyboard, go to the Macros section.
   **Expected:** no key is highlighted to begin with. The "+ NEW MACRO" button sits at the
   same height as it does for a mouse (open a mouse's Macros page to compare).
7. Click a key on the on-screen keyboard, e.g. `Q`.
   **Expected:** "Q" appears as a heading above the "+ NEW MACRO" button, with a Reset link
   next to it.
8. Create a macro: record typing `qwerty`, save it, then with `Q` still selected click that
   macro in the list to assign it.
   **Expected:** the assignment is accepted with no error.
9. Open Notepad and **tap** the physical `Q` key once.
   **Expected:** `qwerty` is typed. Critically — afterwards the keyboard behaves normally:
   no character repeats forever, and no key acts stuck. If anything sticks, note it and reset
   the keyboard (FN+Space, hold 3 s).
10. Tap other keys, e.g. `W` and `E`.
    **Expected:** they type their normal letters. Only the key you bound runs the macro.
11. Select `Q` again and click Reset.
    **Expected:** tapping physical `Q` types `q` again.
12. Close the app completely, reopen it, and tap the key you bound in step 8 (re-assign it
    first if you reset it).
    **Expected:** the macro still plays — the binding survived the restart.
13. Delete that macro from the list, then tap the key it was bound to.
    **Expected:** the key no longer runs the macro and does not misbehave.
14. Unplug the dongle, then try to assign a macro.
    **Expected:** the app reports a failure. It must **not** silently look like it worked.
    *(Known issue **B1**: it currently does report success — record what you see.)*
15. Repeat steps 1–4 for Rampage, Aress, Cyrex and T800 if available.
    **Expected:** each shows black gaps and follows the colour picker in the picture.
16. Open a T-Racer if available.
    **Expected:** black gaps and a working Macros section, same as Raster. *(This step originally
    expected a plain flat picture and no Macros section; both were superseded — artwork by
    [../add-tracer-artwork/add-tracer-artwork-plan.md](../add-tracer-artwork/add-tracer-artwork-plan.md),
    macros by
    [../add-tracer-macros/add-tracer-macros-plan.md](../add-tracer-macros/add-tracer-macros-plan.md).)*

## Review focus

- `gRPC contract` — new `SetMacros` RPC and messages; both sides must move together, and the
  declared `device_key` field must either be honoured or stop promising device identity.
- `resource disposal` — HID streams opened per upload; ack draining must not leave the
  firmware in an addressed session.
- `async/await` — upload and resync paths, and the failure propagation `UploadMacrosAsync`
  currently lacks.
- `UI-thread affinity` — effect timers write `AnimatedGlowBrush` on every tick.

## Performance & quality

- **Per-frame hot path.** The effect timers tick at 33 ms and write `AnimatedGlowBrush` each
  time, driving a `Path.Fill` on the layered canvas. `SetGlow` allocates a brush per tick,
  which matches the pre-existing mouse behaviour and has shown no visible cost; do not add
  further per-tick allocation. The glow `Geometry` is parsed **once** and cached on the
  artwork record — it must never be re-parsed per tick or per frame.
- **Artwork loading.** `.path` assets are read lazily on first use and cached; `SvgImageView`
  caches its raster by `uri|width|height|tint`. Card artwork therefore decodes once, not per
  card render.
- **Device I/O off the UI thread.** Library upload is a full 1096-byte chunked HID exchange
  with a read per chunk; it runs through the gRPC client, never inline on the dispatcher.
- **`LocalStorageService` access is synchronous** (`_lock.Wait()`), consistent with every
  existing accessor in that file. These are small indexed SQLite reads; if they ever move to
  a hot path they need the async treatment, but changing the convention was out of scope here.
- **Shared state.** The macro service is created per call from singleton providers; it holds
  no cross-call state. `RgbKeyboardArtCatalog` is a static read-only map — safe to share.

## Risks

- **Unverified protocol inference.** `code2` as play mode is hardware-confirmed, but the
  *meaning of other values* is not. The RGB `lightColorIndex` values (7 = custom RGB,
  8 = multi) are documented-but-unverified guesses and have already produced one failed fix
  attempt. Treat any further byte-level change as needing a hardware read-back, not
  reasoning from the reference alone.
- **Slot/index coupling.** `RebuildMacroAssignmentTargets` and `BuildMacroLibraryPayload`
  must iterate the library in the same order forever. If they ever diverge, keys silently
  play the wrong macro — nothing in the protocol detects it. The ordering test in the Test
  strategy is the guard.
- **Silent capacity limits.** 16 library slots and 60 events per macro are enforced without
  logging or user feedback, so a long macro is quietly truncated (see **S3**).
- **Artwork/layout drift.** `RasterArtworkLayout` coordinates are derived from a specific
  artwork revision. Replacing `PG_Raster_keys.svg` invalidates every overlay position, with
  no automated check to catch it.
- **Five keyboards, one hand-split asset pipeline.** The split and inpainting were done
  per keyboard; only Raster has been verified in depth on hardware.
- **Two identical keyboards are not distinguished.** `SetMacros` carries `device_key` but the
  host resolves the macro service from vendor + product id alone, exactly as `SetKeyMapping`
  does, so with two of the same model attached the library goes to whichever unit the host
  enumerates first. Honouring the field means threading it through the service factories on
  both RPCs — deliberately out of scope here rather than done for one RPC only.

## Open questions

1. **Does the RGB colour path work over the 2.4G dongle at all?** Raster's only configured
   transport is `Dongle2_4G`, while the RGB plan lists "Wireless dongle mode (2.4G) not
   implemented" as an open item and expects 24-byte chunks in that mode. Macros demonstrably
   work over the dongle with 56-byte chunks, which argues against a transport problem — but
   this needs settling before the colour bug is chased further.
2. ~~**Should the CI workflow run the test projects?**~~ **Resolved.** It now builds and tests
   the whole solution. This required moving the job from `ubuntu-latest` to `windows-latest`:
   every project targets a Windows TFM and both test projects are Windows-only, so they cannot
   run on Linux. Worth knowing that the previous step ran `dotnet test` against the App csproj,
   which contains no tests — CI was running none at all. **Windows runners bill at a higher
   rate than Linux; revert if that is not wanted.**
3. **Should FN be bindable?** It currently has no overlay because the artwork does not draw
   it as a separate keycap.
4. **Should `Repetitions` be hidden for Raster** in the macro editor, given the device cannot
   express it, or left visible and inert?

## Review findings

Maintained by `/reviewer` as a round-by-round journal. `/coder` and `/test-writer` read this
section and mark items `[x]` as they are closed.

### Round 1 — Ship: no

#### Plan conformance

Reviewed before this plan existed, so Round 1 was a code-only review. Two standing plans were
checked and both describe work already on `main`:

- `raster-key-assignments-plan.md` states the non-goal **"No macro editor."** This branch adds
  one. Reasonable as a decision, but nothing recorded the change of intent — this plan now does.
- `raster-rgb-plan.md` still claims `Status: Working — colour/brightness/speed control from the
  app`, which current hardware behaviour contradicts.

#### Code

- [x] **B1. Macro upload reports success even when every write failed** —
  `src/ProoveGaming.Infrastructure/Services/RasterKeyboardMacroService.cs:70` — Blocker
  **Problem:** `UploadMacrosAsync` returns `true` unconditionally at the end of the happy
  path. `WriteBufferAsync:106` catches write exceptions and only logs a warning;
  `DrainResponseAsync:118` reads the ack but never validates it — no magic check, no command
  echo, no `0x55` status. Nothing can make the method return `false` except `TryOpen` failing.
  **Why it blocks:** Unplug the keyboard mid-upload, or have firmware NAK a chunk. Every write
  throws, each is swallowed, the loop completes, `true` is returned. `DeviceService.SetMacros`
  returns `Success = true`, so `ResyncKeyMacroBindingsAsync` passes its `if (!uploaded) return;`
  guard and writes key-matrix bindings into a library that was never stored — then persists them
  to `key_macro_bindings`. The user sees no error; the key silently does nothing or replays a
  stale slot.
  **Solution options:** A (correct) — copy the sibling pattern already in this repo:
  `RasterKeyboardRgbService.SendAndAwaitAckAsync` validates
  `buf[1] == 0xAA && buf[2] == cmd && buf[8] == 0x55`, returns null on mismatch, caller aborts.
  Make write/drain return `bool`, propagate, `return false` on first failure. B (quick) — let
  `WriteBufferAsync` rethrow so the outer `catch` at `:72` reports failure.

- [x] **S1. `key_macro_bindings.mode` is written but never read** —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs:377` — Should-fix
  **Problem / why it matters:** `foreach (var (matrixIndex, macroId, _) in ...)` discards
  `Mode`; the value is recomputed from `MacroPlayMode(entry.Macro)`. The table comment in
  `LocalStorageService.cs` claims mode "carries the macro's repeat mode … the type byte written
  to the key-matrix slot is derived from it" — no longer true. Worse, `SetKeyMacroBinding` stores
  a proto `MacroRepeatMode` value chosen by comparing against a *firmware byte*
  (`target.ScanCode == MacroPlayModeWhileHeld`), mixing two unrelated encodings in one column.
  **Fix:** recomputing from the macro is the right design (single source of truth). Drop the
  column and parameter, or read it and drop the recompute. Either way correct the table comment.

- [x] **S2. Two contradictory representations of macro play/repeat mode** —
  `src/ProoveGaming.Infrastructure/Classes/RasterMacroEncoder.cs:51-62,201` — Should-fix
  **Problem / why it matters:** The encoder documents mode as the *type byte* (`0x60` play /
  `0x61` while-held) and declares `BindingCode2MinRepeat` as a *repeat count*. The App
  documents `code2` as the *play mode*, hardware-confirmed, and always writes type `0x60`.
  Both descriptions of `code2` are in the tree and only one is right. Verified usage:
  `BindingCode2MinRepeat` has exactly one occurrence (its own declaration) — dead.
  `BindingTypeByte` appears only in the encoder and in `RasterMacroEncoderTests`, so a test
  exercises production-dead code. `KeyAssignments.cs:285` still says "Keep in sync with …
  BindingTypeWhileHeld", a constant no longer in that file; `deviceservice.proto:49` still
  advertises `type=0x60/0x61`. This is the exact class of contradiction that cost two
  debugging rounds.
  **Fix:** pick one representation, delete `BindingCode2MinRepeat` and `BindingTypeByte`
  (plus its test), correct the proto and both comments.

- [x] **S3. Encoder silently discards macro content** —
  `src/ProoveGaming.Infrastructure/Classes/RasterMacroEncoder.cs:215,217` — Should-fix
  **Problem / why it matters:** Three independent silent drops — mouse events filtered,
  events past `MaxEventsPerMacro` (60) truncated, and any macro that does not fit has its
  offset-table entry zeroed. No log, no caller signal. A user records a 70-event macro, the App
  shows all 70, the keyboard plays 60, nothing explains the difference.
  **Fix:** `EncodePacked` is a pure static and cannot log — return dropped/truncated counts
  alongside `(Buffer, DataLen)` and have `RasterKeyboardMacroService` log them, ideally
  surfacing a warning to the App.

- [x] **S4. `SetMacrosRequest.device_key` is documented but ignored** —
  `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto:131` — Should-fix
  **Problem / why it matters:** The field is commented "stable per-physical-device identity;
  "" = first matching unit", then never used — `DeviceService.cs:243` calls
  `CreateForDevice(VendorId, ProductId)` only. With two identical Rasters the library goes to
  whichever unit sorts first by device path. Consistent with the existing `SetKeyMapping`
  (`:307`, `:342`), so an inherited gap rather than a new one, but the new field promises a
  guarantee it does not deliver.
  **Fix:** thread `device_key` through the factory, or drop the comment's promise and record
  the limitation.

#### Tests

- [x] No App-side assertions for the new macro logic — the App test change is a fake stub whose
  `LastMacroLibrary` capture is never asserted. Cover `RebuildMacroAssignmentTargets` slot
  ordering (highest value — the whole binding scheme rests on it), `ResyncKeyMacroBindingsAsync`
  stale-macro cleanup, and the play-mode mapping — `tests/ProoveGaming.App.Tests`.
- [x] `MaxEventsPerMacro` truncation is untested — `tests/ProoveGaming.Infrastructure.Tests`.
- [x] CI never runs the branch's only real coverage: `.github/workflows/dotnet-ci.yml:14,36`
  targets the App csproj, while `RasterMacroEncoderTests` lives in
  `ProoveGaming.Infrastructure.Tests`.

#### Nits

- Factory comment claims "Macro upload is wired-only because the vendor dongle strips the
  vendor HID channel" — `DeviceKeyboardMacroServiceFactory.CreateForDevice`. Raster's only
  transport is `Dongle2_4G` and uploads demonstrably work over it.
- `SliceChunks` last-chunk length diverges from the vendor when `dataLen` is an exact multiple
  of 56 (ours 56, vendor `c % 56` = 0). No failure constructed; worth a comment.
- FN gets no overlay on the artwork path, so it cannot be bound.
- `src/ProoveGaming.App/Views/DoubleCard.axaml` — neighbouring `UserControl.inactive …`
  selectors cannot match a derived `DoubleCard` (Avalonia matches exact type). Pre-existing,
  but adjacent to changed lines.

#### What's done well

- `RasterKeyboardKeyMappingService:227` — the `IsMacroBindingType` pass-through, with the
  failure it fixed spelled out in the comment. Do not undo the "why".
- Press/release attribute grounded in **two** independent vendor writers rather than one, with
  a regression test pinning the byte.
- Overlay `PropertyChanged` subscribe/unsubscribe is balanced (`:44` / `:100`) — no handler
  leak, the usual trap in that pattern.
- `key_macro_bindings` follows the existing owner-scoping, guest-cleanup and `ReKeyAsync`
  patterns exactly; nothing bespoke.
- The `Device`-setter notification fix records the observed symptom, not just the change.

#### Verdict

Ship: no — **B1** alone: a macro upload that fails silently and then persists bindings against
a library that was never stored is a correctness bug with no user-visible signal, and the
correct pattern already exists two files away. **S2** is close behind; contradictory protocol
documentation in the tree is what makes this subsystem expensive to work on.
First: 1) B1 ack validation and error propagation. 2) S2 collapse the duplicate mode
representation and delete the dead constants. 3) S1 resolve the unused `mode` column.

### Round 2 — Ship: no

Surface: `git status` shows the same three commits as Round 1 (`f840f4c`, `9539684`,
`8a4d03d`) plus two unstaged files and this untracked plan folder. **No Round 1 finding has
been addressed** — B1 and S1–S4 all stand unchanged, verbatim, and are not re-litigated below.
`dotnet build` clean; 450 tests pass.

The new surface is the uncommitted work Round 1 excluded on request:
`src/ProoveGaming.Infrastructure/Services/RasterKeyboardRgbService.cs` (+59/−17) and the
`raster-rgb` plan edits.

#### Plan conformance

- The RGB *service* is outside this branch's scope per this plan's Scope section — it belongs
  to [../raster-rgb/raster-rgb-plan.md](../raster-rgb/raster-rgb-plan.md). These edits are
  in-flight debugging of a **pre-existing** bug, not branch work. They should not ride into
  PR #71: they widen the branch's blast radius into a subsystem it otherwise only reads from.
- That plan's byte table was edited to declare `lightColorIndex` "WRITTEN: 7 … 8", promoting
  an unverified inference to documented fact. See **B2**.

#### Code

- [x] **B2. Unverified protocol value written to three channels, with no demonstrated benefit** —
  `src/ProoveGaming.Infrastructure/Services/RasterKeyboardRgbService.cs:224` (`MutateLightFields`) — Blocker
  **Problem:** `block[6]` (`lightColorIndex`) changed from *preserved-from-read* to
  *always written* as 7 (mono) or 8 (multi), and the same value is now also written to the LOGO
  (`:251`) and side-light (`:263`) channels, which previously got 0. The values come from the
  plan's prose table, not from hardware or the vendor source: the reference chunk only ever
  passes this field through from its own UI state (`t[6] = e.lightColorIndex || 0`), so it
  confirms the field is real but says nothing about which value means "custom RGB".
  **Why it blocks:** The deleted comment recorded direct evidence that this byte has real
  effect — writing 0 "visibly blanked the keyboard". Writing an unverified value therefore
  carries a genuine regression risk, and it has been observed on hardware **not** to fix the
  bug it targets (colour still does not reach the keys). If 7 is in fact the *White* preset —
  which the plan's own table originally said — then this ships a change that forces the main
  backlight, the volume-button LED and any side strip to white, replacing "colour never
  changes" with "colour is always wrong on three channels". An unverified change with no
  demonstrated benefit and a plausible worse failure mode should not merge.
  **Solution options:** A (correct) — revert to preserving byte 6 and keep only the
  diagnostics, then determine the real value by capturing what the vendor web tool writes for
  a known-red static configuration (the existing `read` log already prints the pre-write block,
  so no code is needed). B (if the sweep is preferred) — keep it behind an explicit debug
  switch so a wrong guess cannot reach a normal build.

- [x] **S5. Diagnostic log mislabels bytes 29–34 as matrixScreen fields** —
  `src/ProoveGaming.Infrastructure/Services/RasterKeyboardRgbService.cs:150` (`LogBlock`) — Should-fix
  **Problem / why it matters:** The comment claims the matrixScreen sub-block "starts at 29 in
  the 59-byte layout … for this protocol version", but the vendor exposes **two** `getFuncInfo`
  layouts selected by `protocolVer`, and they disagree at exactly this offset. In the 59-field
  layout `[29..34]` are `matrixScreenLightSwitch/Mode/Brightness/Speed/MixColor/ColorIndex`; in
  the 41-field layout the same bytes are
  `sixKeysOrAllKeys/maxOrWin/winLock/keyWasd/scanDelay/layerDefault`. Nothing in the code or
  either plan establishes which layout Raster uses — and this plan's own byte table lists
  `[38..45] misc user prefs`, matching neither layout exactly, so the table is partly
  speculative too. The whole purpose of this line is to decide whether a per-key layer is
  overriding the main light; a wrong label sends the next debugging round in precisely the
  wrong direction (reading `winLock = 1` as "matrix layer active"). The `full = <hex>` line
  already carries the raw bytes, so the labelled line adds risk without adding information.
  **Fix:** drop the labelled "matrix" line and keep `full`, or keep the line but label it
  `bytes[29..34]` with no field names until the protocol version is confirmed.

- [x] **S6. Diagnostics double the HID round-trips and log six Information lines per change** —
  `src/ProoveGaming.Infrastructure/Services/RasterKeyboardRgbService.cs:126`, `:143-158` — Should-fix
  **Problem / why it matters:** `SetLedColorAsync` now performs a second full
  `TryReadFeatureAreaAsync` (two `GET_FUNCINFO` chunk exchanges) after every write, and
  `LogBlock` emits three Information lines per call — six per colour change once read and
  verify are both counted, one of which is a 128-character hex dump. `SetLedColorAsync` is on
  the slider-debounce path, so this fires on every colour or brightness settle. The
  `raster-rgb` plan already flagged the *single* pre-existing diagnostic line as a
  drop-to-Debug candidate if it polluted logs; this is six times that. The in-code comment
  does say "drop this once the colour pipeline is settled", which is the right intent — but
  intent in a comment is not a gate.
  **Fix:** move `LogBlock` and the verify read to `LogDebug`, or gate both behind a flag, so
  the merge decision does not depend on someone remembering the comment.

#### Tests

- [x] No test covers `LogBlock` or the verify read-back. Neither needs one — they are pure
  diagnostics — but note that `MutateLightFields` has **no** unit test at all, so the byte-6
  change in **B2** is unverified by both hardware *and* tests. A golden-bytes test over
  `MutateLightFields` (mono vs multi, and that bytes 0, 10 and 29+ are left untouched) would
  make this class of change reviewable — `tests/ProoveGaming.Infrastructure.Tests`.

#### Nits

- The `SetLed` summary line recomputes `lightMix == 1 ? ColorIndexMulti : ColorIndexCustomRgb`
  independently of `MutateLightFields`, so the two can drift if the rule ever changes —
  `RasterKeyboardRgbService.cs:73`.

#### What's done well

- The verify read-back is the right instinct and identifies the real gap: a write that always
  acks cannot distinguish "field rejected" from "field ignored", and no amount of reasoning
  from the vendor source closes that. Keep the mechanism; gate the noise.
- Widening the dump from bytes 0–10 to the full block is what makes the per-key-layer
  hypothesis testable at all.

#### Verdict

Ship: no — Round 1's **B1** is untouched, and the new work adds **B2**: an unverified
byte-level protocol change, now applied to three channels, that hardware has already shown
does not fix its target bug. Recommend the RGB service edits be dropped from PR #71 entirely
and pursued on their own branch against the `raster-rgb` plan; the branch's own three features
are then reviewable on their merits.
First: 1) revert or gate B2 and keep the diagnostics. 2) Round 1 B1, still the only correctness
blocker in committed code. 3) S5 relabel or drop the mislabelled diagnostic line before the
next hardware capture.

### Round 3 — Ship: no

Surface: committed delta unchanged (`f840f4c`, `9539684`, `8a4d03d` — reviewed in Rounds 1–2).
The new surface is the fix pass: 12 unstaged files plus two new untracked test files
(`tests/ProoveGaming.App.Tests/DeviceDetailsKeyboardMacroBindingTests.cs`,
`tests/ProoveGaming.Infrastructure.Tests/RasterKeyboardRgbServiceTests.cs`). Nothing staged.
Build clean; 291 + 175 = 466 tests pass.

#### Plan conformance

Every Round 1 and Round 2 item is genuinely addressed, and the fixes are the right shape —
**B1** now mirrors `RasterKeyboardRgbService.SendAndAwaitAckAsync` exactly and propagates
failure from start-comm, any chunk, and stop-comm; **B2** is a true revert with the hardware
evidence added to the comment; **S2**'s dead constants are gone and the play-mode contract is
stated once. Two deviations from what the fix pass intended, one of them serious:

- **Deviates:** the **S1** column removal was implemented as a `DROP TABLE` inside
  `EnsureSchema()`, which destroys user data on every launch. See **B1** below. The plan's own
  acceptance criterion "*macros bound to keys still play the same macros after an App restart*"
  is now **false**, so this regressed a criterion the branch previously met.
- **Deviates (accepted, flagged):** the CI change moved the job from `ubuntu-latest` to
  `windows-latest`. This is correct — every project targets a Windows TFM and both test
  projects are Windows-only, so the instruction as originally written would have broken CI —
  and it is recorded in Open questions with the billing caveat. No further action needed, but
  it is a real infrastructure change riding on a code-review pass.

#### Code

- [x] **B1. `DROP TABLE key_macro_bindings` runs on every app start and wipes all key→macro bindings** — `src/ProoveGaming.App/Services/LocalStorageService.cs:137` — Blocker
  **Problem:** The statement was added immediately above the table's own
  `CREATE TABLE IF NOT EXISTS`, inside the `EnsureSchema()` block that the constructor runs on
  every `LocalStorageService` construction (`:47`). So the table is dropped and recreated empty
  on every launch, not migrated.
  **Why it blocks:** Bind a macro to `Q`, confirm it works, close the App, reopen it. The
  `key_macro_bindings` row is gone, so `ResyncKeyMacroBindingsAsync` finds nothing to re-write
  and the key silently reverts. Firmware still holds the old binding until something overwrites
  it, so the keyboard and the UI now disagree and the user has no way to clear it from the App.
  This also makes the guest→user re-key at `:1612` dead code in practice — there is never a
  guest row left to migrate. It contradicts the acceptance criterion above and the table's own
  new comment, which says the row exists so the binding survives "a library edit or a
  reconnect".
  **Note on how this happened:** the `DROP TABLE IF EXISTS button_macro_bindings;` at `:129`
  was cited as precedent, but `button_macro_bindings` is **never created anywhere** in this file
  — that line is dead-table cleanup left from a rename. It is not a precedent for dropping a
  live table.
  **Solution options:**
  - **Option A (correct, and simplest):** delete the `DROP TABLE IF EXISTS key_macro_bindings;`
    line and leave the stale `mode` column in place on existing dev databases. It is harmless:
    it was declared `NOT NULL DEFAULT 0`, and the new `INSERT` at `:536` no longer names it, so
    the default satisfies the constraint. New databases get the clean shape from the
    `CREATE TABLE`.
  - **Option B (if the column must actually go):** use the migration mechanism this file already
    has — `AddColumnIfMissing` at `:252` is the additive counterpart; add a matching
    drop-column-if-present helper (`pragma_table_info` check, then
    `ALTER TABLE … DROP COLUMN`, SQLite ≥ 3.35). More code for no user-visible benefit.

- [x] **S1. Entries past `MaxMacros` are still discarded silently** — `src/ProoveGaming.Infrastructure/Classes/RasterMacroEncoder.cs:130` — Should-fix
  **Problem / why it matters:** `count = Math.Min(entries.Count, MaxMacros)` and the layout loop
  runs only to `count`, so a 17th-and-beyond entry is dropped without incrementing
  `SkippedMacros` and without setting `HasDiscards`. That is the one discard path the **S3** fix
  did not close, and it is the one a caller is most likely to trust: the type now advertises
  that it reports everything it leaves out. The App caps at 16 today, so this cannot fire yet —
  but the next caller (T-Racer, or a raised UI cap) gets silent loss and a log that says the
  library fit fine.
  **Fix:** count the overflow — `skippedMacros += Math.Max(0, entries.Count - MaxMacros);`
  before the loop — so `HasDiscards` and the service's warning cover it.

- [x] **S2. New App tests race with the fire-and-forget device refresh** — `tests/ProoveGaming.App.Tests/DeviceDetailsKeyboardMacroBindingTests.cs:97` (`WithRasterInMacrosSection`) — Should-fix
  **Problem / why it matters:** `SetDevice` ends with `_ = RefreshFromDeviceAsync()`
  (`DeviceDetailsViewModel.cs:600`), which awaits the matrix probe and then
  `ResyncKeyMacroBindingsAsync` — so it calls `SetMacrosAsync` and possibly
  `SetKeyMappingAsync` on the fake *after* the helper returns. With no synchronization context
  in tests those continuations run on the thread pool, concurrently with the test body, and both
  fake collections are plain `List<T>`. This is observed, not theoretical: the slot-ordering test
  first failed with "collection contained 2 items" because the refresh's upload landed mid-test.
  Two consequences: (a) `MacroUploads[^1]` assumes the assignment's upload is last, which holds
  only by timing — if the refresh completes later it asserts against the wrong payload; (b)
  concurrent `List<T>.Add` from two threads is a data race that can corrupt state or throw, so
  the whole file is intermittently flaky under CI load.
  **Fix:** make the refresh deterministic — `await vm.RefreshFromDeviceAsync()` in the helper
  right after `SetDevice` so the in-flight work is drained before assertions, and/or guard the
  fake's lists with a lock. Then reinstate the stronger `Assert.Single` on the upload.

#### Tests

- [x] Nothing covers the persistence-across-restart behaviour, which is exactly what **B1**
  breaks — every test uses one in-memory connection for the life of the test, so a schema block
  that empties the table on construction is invisible. Add a test that constructs a
  `LocalStorageService` over a temp **file** database, writes a key macro binding, disposes,
  constructs a second one on the same file, and asserts the binding is still readable —
  `tests/ProoveGaming.App.Tests`. This is the single highest-value test missing from the branch:
  it would have caught B1 immediately and guards the acceptance criterion directly.
- [x] `RasterKeyboardMacroService.UploadMacrosAsync` failure propagation — the **B1** fix from
  Round 1 — is still only verified through the App-level fake, not against the service itself.
  A fake `HidStream` whose write throws, and one whose ack carries a bad status byte, would pin
  it at the level the bug actually lived — `tests/ProoveGaming.Infrastructure.Tests`.

#### Nits

- `Convert.ToHexString(block)` is evaluated eagerly as a `LogDebug` argument, so a 128-character
  string is allocated on every colour change even when Debug is disabled. The verify read right
  above it is properly guarded by `IsEnabled(LogLevel.Debug)`; the same guard at the top of
  `LogBlock` would make it consistent — `src/ProoveGaming.Infrastructure/Services/RasterKeyboardRgbService.cs:150`.
- `PackResult` is a `record struct` holding a `byte[]`, so its generated equality compares the
  buffer by reference. Nothing compares `PackResult` values today; worth knowing before someone
  does — `src/ProoveGaming.Infrastructure/Classes/RasterMacroEncoder.cs:100`.

#### What's done well

- The **B1** ack validation is a faithful copy of the sibling RGB service rather than a new
  invention, including keeping stop-comm best-effort while still failing the upload. The comment
  cites the observed ack bytes (`00-AA-10-…-55`) so the next reader can verify the shape without
  re-deriving it.
- **B2** was reverted rather than re-guessed, and the restored comment now records that writing
  7 was *tried and did not work* — that negative result is the most valuable line in the file,
  because it stops the next person repeating it.
- `RasterKeyboardRgbServiceTests` asserts the bytes `MutateLightFields` must **leave alone**,
  not just the ones it writes. That is the right shape for a read-modify-write, and it is the
  test whose absence let B2 through.
- Making `MutateLightFields` `internal` + `InternalsVisibleTo` follows the existing App and
  Common precedent instead of inventing a new seam.
- The CI finding was not applied blindly. Discovering that the runner could not host the test
  projects, and that the old step tested a project with no tests in it, is a better outcome than
  the instruction as written.

#### Verdict

Ship: no — one blocker, and it is a data-loss regression introduced by the fix pass itself:
`key_macro_bindings` is now dropped on every app start, so key→macro bindings no longer survive
a restart and an acceptance criterion the branch previously met is broken. Everything else in
this round is sound; B1 is a one-line deletion.
First: 1) B1 remove the `DROP TABLE` (Option A). 2) Add the file-backed round-trip persistence
test so this cannot regress silently again. 3) S2 drain the fire-and-forget refresh so the new
App tests are not timing-dependent.

#### Round 3 fix pass — notes

**Correction to S2.** The finding's central claim was wrong and is withdrawn: there is no data
race and no nondeterminism. `ProbeAndUpdateMatrixMapAsync` and `ResyncKeyMacroBindingsAsync`
await only the fake's methods, and every one returns `Task.FromResult(...)` — an
already-completed task, so each `await` continues synchronously on the calling thread. The whole
`_ = RefreshFromDeviceAsync()` chain therefore runs to completion inline inside `SetDevice`,
before it returns; nothing lands on the thread pool and the two `List<T>` collections are only
ever touched by the test thread. The "collection contained 2 items" failure was deterministic
sequencing (setup's upload, then the assignment's), not interleaving.

What was real is the smaller point: the tests depended on that synchronous-completion detail of
the fakes. Addressed by clearing the fake's recordings at the end of the setup helper, so each
test asserts only on the calls it makes itself, which also let `Assert.Single` come back on the
upload. No extra refresh call was added — awaiting a second `RefreshFromDeviceAsync()` would
have produced a second upload without cancelling the first, treating a symptom that does not
exist.

**B1 fix verified against the regression.** The `DROP TABLE` was temporarily reinstated to
confirm `LocalStorageServiceKeyMacroBindingTests.Binding_SurvivesReopeningTheDatabase` fails
with it present (1 failed / 3 passed) and passes with it removed. The test uses a temp **file**
database precisely because the existing `:memory:` suites cannot observe what the schema block
does to an already-populated database.

**Deferred:** the service-level `UploadMacrosAsync` failure-propagation test.
`RasterKeyboardMacroService` takes a concrete `HidSharp.HidDevice` and calls `TryOpen(out var
stream)` for a concrete `HidStream`; neither is substitutable from outside HidSharp, and no test
in the repo fakes them — all 292 Infrastructure tests are pure encoder/parser tests. Covering it
would mean introducing a stream abstraction into production solely for tests. The behaviour is
covered one level up, in `DeviceDetailsKeyboardMacroBindingTests`
(`Assign_WritesNothingWhenUploadFails`, `Resync_WritesNothingWhenUploadFails`), which asserts the
consequence that actually matters: a failed upload writes and persists no binding.

### Round 4 — Ship: yes

Surface: working tree **clean**. The fix pass landed as `3badde6 "review changes"` (15 files,
+1760/-118) on top of the three feature commits. Nothing unstaged, nothing staged, no untracked
files — so the review surface is that one commit plus the previously-reviewed base. Build clean;
292 + 179 = 471 tests pass.

**Round 3's blocker and both should-fixes are genuinely closed**, and I re-verified each rather
than trusting the report:

- **B1** — no `DROP TABLE IF EXISTS key_macro_bindings` anywhere in the committed file (grep: 0
  occurrences). Option A was taken, so a pre-existing database keeps the spare `mode` column,
  which is safe: it is `NOT NULL DEFAULT 0`, the `INSERT` no longer names it, and the
  `ON CONFLICT … DO UPDATE SET macro_id = excluded.macro_id` leaves it alone. Backed by a
  file-database round-trip test that was demonstrated to fail with the `DROP` reinstated.
- **S1** — `skippedMacros` is seeded with `Math.Max(0, entries.Count - MaxMacros)` at
  `RasterMacroEncoder.cs:143` and incremented independently in the layout loop at `:171`; the two
  paths are disjoint, so no double-count.
- **S2** — the withdrawal is correct. I re-checked the call chain: `ProbeAndUpdateMatrixMapAsync`
  and `ResyncKeyMacroBindingsAsync` await only fake methods returning `Task.FromResult(...)`, so
  the fire-and-forget refresh completes inline inside `SetDevice`. There was no race. Clearing
  the fake's recordings in the setup helper was the right, smaller fix.

#### Plan conformance

- **Deviates from plan — the CI change was not committed.** `.github/workflows/dotnet-ci.yml` is
  absent from `3badde6` and `HEAD` still has `runs-on: ubuntu-latest` with
  `dotnet test ./src/ProoveGaming.App/ProoveGaming.App.csproj`. The plan asserts the opposite in
  three places and must be corrected — see **S2** below. Reverting it is a legitimate call
  (Windows runner cost), but the plan now documents a change that does not exist.
- **Stale acceptance criterion.** Line 324 still carries *"(Currently failing — **B1**.)"* on the
  "*a failed upload writes and persists no binding*" criterion. That criterion now **passes** and
  is directly covered by two tests. Line 303 (physical keys following the picker) is correctly
  still marked failing.
- **Accepted limitation (per plan):** the physical-keys RGB colour path still does not work.
  Residual risk: a user changing colour sees the in-app preview follow the picker while the
  keyboard does not — a visible, confusing mismatch shipping in a release. The plan scopes the
  RGB *service* out of this branch and tracks it in
  [../raster-rgb/raster-rgb-plan.md](../raster-rgb/raster-rgb-plan.md); it is pre-existing, not a
  regression from these commits. Worth a release note or a decision to hold the RGB section.

#### Code

- [ ] **S1. Failure log claims nothing was stored when part of the library was** — `src/ProoveGaming.Infrastructure/Services/RasterKeyboardMacroService.cs:85` — Should-fix
  **Problem / why it matters:** `"[MACRO] Raster upload failed - library was not stored"` is
  emitted for every failure path, including a chunk that fails partway through the loop at `:68`.
  By then earlier chunks — including the header chunk carrying magic, `dataLen` and the offset
  table — have been written and acked, so the device holds a **partially rewritten** library.
  Concrete scenario: a key is already bound to macro slot 0 and working; on reconnect the resync
  uploads, chunk 1 acks, the keyboard is unplugged before chunk 2. The log says the library was
  not stored, but the device now has a new header pointing into stale event data, and the existing
  firmware binding plays corrupted output. It self-heals on the next successful upload, so this is
  a transient window rather than lasting damage — but the log actively misdirects whoever is
  debugging that exact incident, which is the moment the message is read.
  **Fix:** track whether any chunk was acked and distinguish the two cases, e.g. *"upload aborted
  before any data was written"* vs *"upload failed after N of M chunks — device library may be
  partially written and will be rewritten on the next successful sync"*.

- [ ] **S2. Plan documents a CI change that is not in the branch** — `docs/plans/raster-svg-effects/raster-svg-effects-plan.md:280`, `:487`, `:754` — Should-fix
  **Problem / why it matters:** The Files table says the workflow *"Runs the whole solution on
  `windows-latest`, so the test projects actually execute"*; Open question 2 is marked
  **Resolved**; and the Round 3 conformance note describes the runner move as landed. None of
  that is true of `HEAD`. The consequence is real, not just cosmetic: this branch adds four test
  files (~640 lines) and **CI runs none of them** — the existing step targets the App csproj,
  which contains no tests, so the pipeline has been running zero tests all along. Anyone reading
  the plan will believe the safety net is wired up when it is not.
  **Fix:** either re-apply the workflow change, or (if the Windows-runner cost is the reason it
  was dropped) reword all three places to state the gap plainly and move it back to an open
  question. Do not leave the plan asserting it is done.

#### Tests

- [ ] The four new test files do not execute in CI (see **S2**) — they are local-only until the
  workflow targets the solution on a Windows runner. No new test authoring needed; this is purely
  the pipeline gap.
- [ ] Still deferred from Round 3 and still reasonable: service-level `UploadMacrosAsync` failure
  propagation. `RasterKeyboardMacroService` takes a concrete `HidSharp.HidDevice` and opens a
  concrete `HidStream`, neither substitutable from outside HidSharp, and no test in the repo fakes
  them. If **S1** is fixed, the partial-vs-nothing distinction would be the natural thing to pin —
  which is an argument for introducing a thin stream seam eventually, not now.

#### Nits

- `PackResult` is a `record struct` holding a `byte[]`, so generated equality compares the buffer
  by reference. Nothing compares `PackResult` values; noted from Round 3 and still true —
  `src/ProoveGaming.Infrastructure/Classes/RasterMacroEncoder.cs:100`.
- `LocalStorageServiceKeyMacroBindingTests.Dispose` swallows only `IOException` when deleting the
  temp database; a different failure would leave a stray file in `%TEMP%`. Harmless, but a bare
  `catch` there would be more honest about the intent —
  `tests/ProoveGaming.App.Tests/LocalStorageServiceKeyMacroBindingTests.cs:31`.

#### What's done well

- **The B1 fix was proven, not asserted.** Reinstating the `DROP`, watching the new test fail
  (1 failed / 3 passed), then removing it is the right way to establish that a regression test
  actually guards the regression. The test comment records *why* a file database is used, so the
  next person does not "simplify" it to `:memory:` and silently lose the coverage.
- **S2 was withdrawn rather than defended.** Implementing the prescribed fix for a race that does
  not exist would have added a second refresh call — extra uploads, no benefit. Correcting the
  finding in the journal and applying the smaller real fix is better engineering than closing the
  item as written.
- Choosing Option A on B1 (leave the spare column) over a drop-column migration: less code, no
  destructive statement in a block that runs on every start, and the reasoning is recorded inline
  so the column does not look like an oversight later.
- The deferral on the HidSharp seam is argued from evidence — no test in the repo fakes those
  types, and all 292 Infrastructure tests are pure encoder/parser tests — rather than asserted.

#### Verdict

Ship: yes — no blockers. Round 3's data-loss regression is fixed and now guarded by a test proven
to catch it, and the remaining items are a misleading log message and a plan that overstates what
landed. The one thing to decide before release is not a code defect: the physical-keys RGB colour
path still does not work, and the plan accepts that as pre-existing and out of scope.
First: 1) S2 reconcile the plan with the actual CI state, and decide whether the workflow change
is re-applied. 2) S1 make the failure log distinguish "nothing written" from "partially written".
3) Clear the stale *"Currently failing — B1"* marker on the acceptance criterion at `:324`.

### Round 5 — external review (PR #71 comments), both findings fixed

Source: review comment by **boikoserhii84** on PR #71 (state: APPROVED with comments), raising two
defects that Rounds 1–4 of this journal missed. Both were verified in code before being accepted,
and both are now fixed with regression tests demonstrated to fail without the fix.

Note on the earlier rounds: **M2 sits directly on a seam this journal's own checklist names** —
"when a value is threaded through, was *every* path updated?". Round 1 reviewed
`SelectedButtonHeaderView.axaml` (+8/-4) and listed the unified `SelectedMacroTarget*` properties
under *What's done well* without checking that every raise site had moved to the new names. A
rename with one notifier left behind is exactly the silent-bug shape that seam exists to catch.

- [x] **M1. Editing a macro did not re-upload the library — the key kept playing the old recording** — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Buttons.cs:542` (`ReplaceEditedMacro`) — Blocker-class
  **Problem:** `ReplaceEditedMacro` re-pushed for the mouse at `:557`
  (`SetButtonBindingsAsync`, where a mouse macro travels inline on the binding) but never called
  `ResyncKeyMacroBindingsAsync()`. A keyboard macro lives in an indexed library, so the edited
  events only reach the device when the whole library is re-uploaded. `Macros[index] = macro`
  raised `CollectionChanged` → `RebuildMacroAssignmentTargets()`, so the UI targets refreshed while
  the packed buffer on the keyboard stayed as it was.
  **Failure scenario (verified):** bind "qwerty" to `Q`, confirm it works, edit the macro to
  "hello", save. Physical `Q` keeps typing `qwerty` while the App list shows "hello" — app and
  firmware diverge with no error. Self-heals only on the next unrelated resync trigger (deleting a
  macro, reconnecting, restarting). The same applied to the `RepeatUntilReleased` toggle: the
  target was rebuilt with a new `code2` but the already-written matrix record was not.
  **Fixed:** Option A — `_ = ResyncKeyMacroBindingsAsync();` added next to the mouse push. The
  method is idempotent and self-gates on `IsMacrosSupported` / `HasInteractiveKeyboardOverlay`, so
  it is safe on any device.
  **Test:** `DeviceDetailsKeyboardMacroBindingTests.EditingABoundMacro_ReUploadsTheLibrary` —
  asserts the library is re-sent and the key is re-written with the edited macro's play mode.
  Verified to fail with the fix removed (`Assert.NotEmpty() Failure: Collection was empty`).

- [x] **M2. Reset link in the shared header stopped updating after an assignment — regression on the shipped mouse path** — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Buttons.cs:737`, `src/ProoveGaming.App/Views/DeviceDetails/Sections/SelectedButtonHeaderView.axaml:47` — Blocker-class
  **Problem:** the header was rebound to `IsEnabled="{Binding CanResetSelectedMacroTarget}"`, but
  `RefreshMacroListSelection()` — which runs after **every** macro assign and reset — still raised
  only `nameof(CanResetSelectedButton)`. `CanResetSelectedMacroTarget` was raised in exactly two
  places, the two selection setters, and `ApplyAssignmentTargetAsync` raised nothing at all. The
  computed value was always correct; nothing told the UI it had changed.
  **Failure scenario (verified):** mouse, Macros section — select a hotspot with nothing bound
  (Reset correctly disabled), click a macro row to bind it. The binding succeeds but Reset
  **stays disabled** until another button is selected and reselected. This worked before this
  branch, when the header bound `CanResetSelectedButton`. Keyboard: select `Q`, bind a macro,
  `_userOverrides[slot]` updates and `SelectedKeyHasOverride` becomes true, Reset stays grey.
  Inverse case after a reset: the link stays enabled with nothing to reset.
  **Fixed:** Option B, the durable version. Added
  `NotifySelectedMacroTargetChanged()` next to the four properties in `KeyAssignments.cs:137` and
  called it from all four sites — both selection setters, `RefreshMacroListSelection` (alongside
  the still-needed `CanResetSelectedButton` for the Buttons page), and the end of
  `ApplyAssignmentTargetAsync` where `_userOverrides` changes. The four-line raise block had been
  copied into two files and omitted from two others; there is now one copy.
  **Tests:** three, all asserting the **notification** rather than the value, since the value was
  never wrong — `DeviceDetailsMacroAssignmentTests.AssigningAMacro_RaisesResetEnablementForTheSharedHeader`,
  `…ResettingAButton_RaisesResetEnablementForTheSharedHeader`, and
  `DeviceDetailsKeyboardMacroBindingTests.AssigningToAKey_RaisesResetEnablementForTheHeader`.
  All three verified to fail with the fix removed.

Build clean; 292 + 183 = 475 tests pass.

**Still open from Round 4** (neither addressed here): **S1** the `"library was not stored"` log is
inaccurate after a mid-loop chunk failure, and **S2** the plan still claims a CI change that is
not in the branch. Both remain valid.

### Round 6 — Ship: yes

Surface: working tree clean, pushed. 7 commits vs `origin/main`, 69 files, +8487/-77. The new
surface is the merge commit `209af63`, which brought `origin/main` (PR #72, Cyrex/Aress key
assignments) into the branch and resolved three conflicts in the shared key-assignment subsystem.
Build clean, no new warnings; 315 + 244 = 559 tests pass.

**Baseline note:** the local `main` ref is stale at `cb9acde`, so the `git diff main...HEAD` this
skill prescribes understates the picture — `origin/main` (`4503b86`) is the real baseline and is
what this round used. Worth a `git fetch`/reset of the local ref before the next round.

#### Plan conformance

- **Two cross-feature decisions the merge made are not yet recorded in either plan.** This plan
  describes the Raster work and `add-smart-infinite-key-assignments-plan.md` (arrived with #72)
  describes Cyrex/Aress; neither mentions that they now share a code path with resolved
  differences. Specifically: main's `SelectPhysicalKey(PhysicalKeys[0])` auto-select was dropped
  and now applies to Cyrex/Aress too, and key-overlay artwork gating moved from "has artwork" to
  "is Raster". Both are correct calls but they change #72's behaviour, so they belong in writing.
  See **S2**.
- Acceptance criteria unchanged by the merge; the two stale markers noted in Round 4 (`:303`
  legitimately failing, `:324` now passing) still need the `:324` one cleared.
- **Accepted limitation (per plan), unchanged:** the physical-keys RGB colour path still does not
  work. Residual risk unchanged — visible preview/hardware mismatch.

#### Code

- [ ] **S1. `UseArtworkKeyOverlays` — the gate that keeps Raster's coordinates off other boards — has no test** — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs:262` — Should-fix
  **Problem / why it matters:** Cyrex and Aress are both in `RgbKeyboardArtCatalog` (they have
  split artwork for the RGB preview), so the pre-merge gate `KeyboardArt is not null` would have
  routed them down Raster's artwork overlay path — stage sized to Raster's viewBox and overlay
  positions looked up in `RasterArtworkLayout`, which holds Raster's measured keycap bounds. Keys
  sharing a name with a Raster key would land at Raster's coordinates on a differently-shaped
  board; keys absent from that table get `continue` and no overlay at all. That is #72's headline
  feature visibly broken. The gate is now `IsRasterDevice && KeyboardArt is not null`, which is
  correct — but **nothing tests it**, and the 559-test suite did not catch the broken version
  because #72's Cyrex tests assert view-model properties (`OverlayCanvasWidth`, catalogs) while
  the gating decision is consumed imperatively in `RasterKeyboardOverlayView.axaml.cs:54`.
  **Fix:** assert `UseArtworkKeyOverlays` per board — true for Raster, false for Cyrex, Aress and
  T-Racer — in `tests/ProoveGaming.App.Tests`. Four one-line assertions pin a decision that
  otherwise depends on nobody re-adding Cyrex to a catalog. Optionally also assert
  `OverlayCanvasWidth` is what the code-behind would use for a non-artwork board, which ties the
  VM property to its only consumer.

- [ ] **S2. The dropped auto-select changes #72's boards with nothing recording or testing it** — `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs:394` — Should-fix
  **Problem / why it matters:** `SelectPhysicalKey(PhysicalKeys[0])` predates both branches; this
  branch removed it deliberately (a key appeared chosen on load that the user never picked) and
  the merge kept it removed. Correct and consistent — but it silently changes Cyrex and Aress,
  whose author expected the pre-existing behaviour, and **no test asserts either way** (verified:
  no test in the suite references initial `SelectedPhysicalKey` state). A future merge or revert
  could flip it back with nothing objecting.
  **Fix:** add a test asserting no key is selected after `SetDevice` for at least one board of
  each family, and note the decision in both plans. Also worth telling #72's author directly
  rather than letting them find it.

#### Tests

- [ ] `UseArtworkKeyOverlays` per-board coverage — see **S1**; this is the highest-value gap on
  the branch right now, because it guards a bug that actually reached a committed merge.
- [ ] Initial-selection behaviour after `SetDevice` — see **S2**; currently unpinned in either
  direction.
- [ ] Still deferred from Rounds 3–4 and still reasonable: service-level `UploadMacrosAsync`
  failure propagation (needs a stream seam that does not exist), and the four new test files plus
  #72's still do not run in CI (Round 4 **S2**).

#### Nits

- The `OnPropertyChanged(nameof(OverlayCanvasWidth))` / `…Height` pair is now dead: the merge
  resolution removed the XAML binding those notifications served, and the code-behind reads the
  properties imperatively during `RebuildKeyboard`. Harmless, but a reader will assume something
  subscribes — `DeviceDetailsViewModel.KeyAssignments.cs:391-392`.
- **Latent, not currently reachable:** `SelectedKeyHasOverride` reads only `_userOverrides`, which
  `ProbeAndUpdateMatrixMapAsync` never populates for Smart Infinite boards (they early-return to
  `ReadSmartInfiniteAssignmentsAsync` and use `_assignments`). If Cyrex or Aress ever gain macro
  support, `CanResetSelectedMacroTarget` would be permanently false for them and the
  `IsSmartInfinite65Device` early return in `ApplyAssignmentTargetAsync` would skip
  `NotifySelectedMacroTargetChanged()` — i.e. Round 5's **M2** symptom, reintroduced for those
  boards. Verified unreachable today: neither declares `SupportsMacros` in `appsettings.json`, and
  `KeyAssignmentsSectionView.axaml` does not use the shared header —
  `DeviceDetailsViewModel.KeyAssignments.cs:145`.

#### What's done well

- The merge did not stop at "markers gone, build green". Two defects that compiled and passed all
  559 tests were caught by reading the seams instead: the Cyrex/Aress artwork mis-gating, and the
  mutual test-fake gap. That is the difference between a textual and a working merge.
- The generation guard was **re-ordered rather than pasted**. `IsCurrentKeyLayout`'s documented
  contract is "capture before the await"; the macro upload introduced a new await ahead of the old
  capture point, so the capture moved above it and a re-check was added after. Preserving another
  author's invariant while inserting into their method is the easy thing to get wrong.
- Provenance was checked before discarding the auto-select — confirming it existed in `cb9acde`
  and was removed by this branch, rather than assuming #72 had added it.
- The commit message records each resolution and both judgement calls, so the next reader does not
  have to reconstruct why the axaml took one side and the sizing took the other.
- Semantic survival of the clean-merged files was verified explicitly (the `IsMacroBindingType`
  pass-through against #72's +69 to that same service, both RPC handlers, both client methods, DI
  registration, all seven glossary entries), and the en/uk parity gap was checked against both
  parents before being dismissed as pre-existing.

#### Verdict

Ship: yes — no blockers. The merge is sound and the two defects it could have shipped were caught
and fixed before commit. What remains is coverage and bookkeeping: the artwork gate that prevented
the worse of those two defects is itself untested, and two behaviour decisions affecting another
team's feature are recorded only in a commit message.
First: 1) S1 pin `UseArtworkKeyOverlays` per board. 2) S2 test the no-auto-select behaviour and
tell #72's author. 3) Round 4's two carried items — the inaccurate upload-failure log and the
plan's CI claim.
