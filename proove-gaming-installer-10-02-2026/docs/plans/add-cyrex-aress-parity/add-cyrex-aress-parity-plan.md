# Cyrex & Aress — Parity With The Raster Work

## Goal

Bring the two Smart Infinite 65% keyboards up to what Raster got in
[../raster-svg-effects/raster-svg-effects-plan.md](../raster-svg-effects/raster-svg-effects-plan.md):
key overlays drawn on the **rendered board** instead of an idealised grid, and macro support.
The two halves have very different risk profiles, so they are deliberately separated:

- **Phase 1 — artwork-accurate key overlays.** No protocol unknowns. The artwork already ships
  for the RGB preview; only the measured key bounds are missing. Committed work.
- **Phase 2 — macros.** Implemented, unverified on hardware. The spike gate was **lifted by the
  user** ("enable Macros for Aress/Cyrex … the same workflow like for Raster"), so the pipeline is
  built now and the hardware test replaces the spike. Two protocol values are inferred rather than
  documented for these boards — see [Phase 2 — inferred values](#phase-2--macros-inferred-protocol-values)
  — so a negative hardware result is still a live possibility, and the logging is built to say
  which inference failed.

Dependency: Phase 2 inherits macro scoping from
[../migrate-macros-per-device/migrate-macros-per-device-plan.md](../migrate-macros-per-device/migrate-macros-per-device-plan.md),
which should land first.

## Scope

**In**

- Phase 1: measured key-overlay bounds for Cyrex and Aress, and widening the artwork gate.
- Phase 1: the per-board test coverage for that gate which
  [../raster-svg-effects/raster-svg-effects-plan.md](../raster-svg-effects/raster-svg-effects-plan.md)
  Round 6 **S1** flagged as missing.
- Phase 2: a scoped protocol spike with explicit exit criteria, then — only if it succeeds — the
  macro pipeline for both boards.

**Out**

- Rampage. Same protocol family, but its firmware reports a key count of 0 and silently
  discards key-matrix writes; there is no reason to expect macros to behave better, and it has
  no artwork-accurate overlay work pending.
- Changing Raster's macro pipeline. Phase 2 mirrors it; it does not refactor it.
- The RGB preview for these boards, which already ships.
- Sending Smart Infinite command `0x20` under any circumstance — it is a **full factory reset**,
  not a soft reset.

---

# Phase 1 — Artwork-accurate key overlays

## Options considered

### How to share geometry between two physically identical boards

#72 established the right instinct: Cyrex and Aress are the same hardware differing in three
navigation-key *labels*, so `SmartInfinite65Geometry` defines positions once **keyed by matrix
slot** and each model's layout is `LayoutFor(itsCatalog)`. The question is whether measured
artwork bounds can share a table the same way.

**They cannot, and this corrects the assumption this plan started from.** Measured from the
shipped assets:

| Board | keys-layer raster | aspect | artwork viewBox | viewBox aspect |
|---|---|---|---|---|
| Raster | 1247 × 484 | 2.58 | 1529.288 × 701.593 | 2.18 |
| Cyrex | 1463 × 453 | **3.23** | 1472.469 × 480.666 | 3.06 |
| Aress | 3136 × 1124 (content inset to 2854 × 814) | **3.51** | 1571.695 × 1563.529 | **1.005** |

Cyrex's artwork is a flat 65% board at very nearly the theoretical 3.25 aspect. Aress's viewBox
is **near-square** with a wide keyboard image inside it — so the two SVGs frame the same physical
board completely differently, with different placement transforms and, in Aress's case,
substantial vertical dead space. Normalised (0..1) bounds would not transfer either, because
normalisation only helps when both artworks frame the subject identically.

| Option | Trade-off |
|---|---|
| **A. One measured table per artwork, slot-keyed, joined against each catalog** | **Chosen for Cyrex.** Keeps #72's slot-keyed sharing where sharing is real (which slot is which key) and accepts per-artwork bounds where the artworks genuinely differ. |
| B. One shared measured table for both boards | Rejected on the measurements above — it would misplace every overlay on at least one of the two. |
| C. Re-export both artworks to a common viewBox first | Cleanest end state, but an asset change with knock-on effects on the already-shipped RGB preview and card art. |

### Aress: its shipped artwork is not the Aress board

Discovered while deriving the tables. Component labelling gives the row structure of each artwork,
compared against the 67-slot catalog both boards share:

| | Row counts |
|---|---|
| Aress / Cyrex board (catalog + `SmartInfinite65Geometry`) | 15, 15, 14, 14, 9 |
| **Cyrex** artwork | 15, 15, 14, 14, **10** ✓ (extra = FN, as on Raster) |
| **Aress** artwork | **16, 16, 13, 13, 10** ✗ |

`PG_Aress_keys.svg` shows a board with sixteen keys across its top two rows; the Aress board has
fifteen. No placement transform reconciles that — it is different hardware in the picture. Note the
consequence beyond this plan: **that artwork already ships in the RGB preview**, so Aress users are
looking at the wrong keyboard today. Correct Aress art is needed regardless of this work.

| Option for Aress overlays | Trade-off |
|---|---|
| **Aress renders the Cyrex artwork and Cyrex's measured table** | **Chosen.** The two are the *same physical board* — #72 established that, sharing their geometry by matrix slot — so Cyrex's art is a faithful picture of an Aress, and the overlays align exactly. Cost: three navigation keycaps carry Cyrex's printed labels (Delete/PageDown/End) where an Aress reads Insert/Delete/PageDown. A cosmetic inaccuracy on 3 of 67 keys. The clickable overlays and the header still name the **Aress** keys, because those come from `AressKeyCatalog`, not from the picture. |
| Leave Aress on the bare canvas | No wrong labels, but no keyboard picture at all on Key Assignments or Macros — which is precisely what this change was asked to fix. |
| Show Aress's own artwork with grid-positioned overlays | Rejected outright: the wrong keyboard *and* overlays that do not line up with the keys drawn under them. |

## Recommended approach

Mirror the Raster process per board:

1. **Derive bounds by connected-component labelling** of each keys-layer alpha channel — the
   same method that produced `RasterArtworkLayout` (which found 80 components for 79 keys, the
   extra being FN). Then apply that artwork's own placement transform to land in viewBox
   coordinates. Raster needed `translate(50.378 82.803) scale(1.146)`; each board's transform
   must be read from its own SVG, **not** assumed.
2. **Key the result by matrix slot**, following `SmartInfinite65Geometry`, and expose
   `LayoutFor(catalog)` so `CyrexArtworkLayout` / `AressArtworkLayout` are joins rather than
   copies. FN (slot 70) has no overlay on either board, exactly as today.
3. **Widen the artwork gate.** `UseArtworkKeyOverlays` is currently
   `IsRasterDevice && KeyboardArt is not null`
   (`DeviceDetailsViewModel.KeyAssignments.cs:262`). It must resolve the *layout table* per
   board rather than test for a specific device — the invariant is "we have measured bounds for
   this board's artwork", and that is what the property should express.
4. **The overlay view already supports this.** `RasterKeyboardOverlayView` puts an
   `RgbKeyboardView` art layer (`ShowBacklight="False"`) and the button canvas in one `Viewbox`,
   sized in code-behind from either the artwork viewBox or `OverlayCanvasWidth/Height`. Only the
   layout lookup is Raster-specific; nothing about the view needs restructuring.

### The sizing double-source to clean up while here

`OnPropertyChanged(nameof(OverlayCanvasWidth))` / `…Height` at
`DeviceDetailsViewModel.KeyAssignments.cs:391-392` are currently **dead**: the merge that landed
Raster's overlay removed the XAML binding they served, and the code-behind now reads those
properties imperatively. Either drop the notifications or restore a binding — do not leave a
reader assuming something subscribes. (Recorded as a nit in the raster plan, Round 6.)

## File-level change list — Phase 1

### Created

| File | Intent |
|---|---|
| `src/ProoveGaming.App/Models/Keyboards/CyrexArtworkLayout.cs` | Measured keycap bounds for the Cyrex artwork, slot-keyed, plus `Layout` as the join against `CyrexKeyCatalog`. |
| ~~`src/ProoveGaming.App/Models/Keyboards/AressArtworkLayout.cs`~~ | **Not created.** Superseded by the chosen option above: the Aress artwork does not depict the Aress board, so no table can be measured from it. Aress reuses `CyrexArtworkLayout`, joined against `AressKeyCatalog` by matrix slot. |
| `docs/plans/add-cyrex-aress-parity/md2html.py` | Minimal Markdown→HTML converter for the investigation report, committed so the HTML stays regenerable rather than drifting from its source. Handles only the constructs that document uses. |
| `src/ProoveGaming.Infrastructure/Classes/MacroLibraryVerification.cs` | Pure verdict for a macro-library read-back — match / all-zero / divergent-at-N. Extracted from `SmartInfiniteMacroService` so the one piece of its behaviour that needs no hardware can be unit tested (review Round 1, Tests). |
| `tests/ProoveGaming.App.Tests/ArtworkKeyOverlayGateTests.cs` | Per-board assertions on `UseArtworkKeyOverlays` and the resolved layout — the coverage Round 6 **S1** asked for. |

### Edited

| File | Intent |
|---|---|
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` | Resolve the artwork layout per board and express `UseArtworkKeyOverlays` as "measured bounds exist"; remove the two dead `OverlayCanvas*` notifications (or restore their binding). Adds `KeyOverlayArt` — the artwork the overlay draws on, which is Cyrex's for both 65% boards — and `MacroTypeByteForDevice` for the family-specific macro type. |
| `src/ProoveGaming.App/Models/RgbKeyboardArt.cs` | `RgbKeyboardArtCatalog.SmartInfinite65` so the Aress overlay can ask for the Cyrex artwork by name without spreading the lookup key. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml` | Art layer binds `KeyOverlayArt` instead of `KeyboardArt` — they diverge on Aress, and the RGB preview and home card must keep the shipped artwork. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml.cs` | Take the layout table and artwork viewBox from the view model instead of referencing `RasterArtworkLayout` directly. |
| `docs/CONTEXT.md` | Glossary entries for `CyrexArtworkLayout` / `AressArtworkLayout`, and update `RasterArtworkLayout` now that it is one of three. |

## Acceptance criteria — Phase 1

- [ ] Given a Cyrex is connected, when Key Assignments opens, then the overlays sit on the
      rendered Cyrex board (not a bare canvas) and each overlay covers its own keycap.
- [ ] Given an Aress is connected, when Key Assignments opens, then the same holds for Aress —
      rendered on the **Cyrex** artwork, since the two are the same physical board and the Aress
      artwork does not depict it. Three navigation keycaps carry Cyrex's printed labels; the
      clickable identities and the header stay Aress's.
- [x] Given an Aress, then the RGB backlight preview and the home card still show the artwork Aress
      ships with — only the key overlay borrows. Asserted in `ArtworkKeyOverlayGateTests`.
- [ ] Given a Cyrex, when a key overlay is clicked, then the header names **that** key — i.e.
      overlay identity still matches the catalog, not just position.
- [ ] Given a T-Racer (artwork exists in no measured table), when Key Assignments opens, then it
      still renders the bare canvas at `OverlayCanvasWidth/Height` and every key has an overlay.
- [x] Given each supported board in turn, then `UseArtworkKeyOverlays` is **true** for Raster,
      Cyrex and Aress, and **false** for T-Racer — asserted in tests, not by inspection.
- [x] Given an Aress, then every key in `AressKeyCatalog` has overlay bounds, resolved under its own
      key names via the slot-keyed join. Asserted in `ArtworkKeyOverlayGateTests`.
- [ ] Given Cyrex or Aress, when the details window is resized, then overlays stay registered to
      their keycaps at every size (they share the artwork's `Viewbox`).
- [ ] Given a key remap is applied on Cyrex, then it still writes and reads back exactly as it
      does today — this phase must not touch the Smart Infinite key path.
- [x] `dotnet build ./src/ProveGaming.slnx` — 0 errors; both test projects green
      (App 266, Infrastructure 319).

---

# Phase 2 — Macros (gated on the spike)

> **Outcome: blocked in firmware.** The full findings — evidence, the documents used, what each
> hardware round ruled out, and what would unblock it — are in
> [`macro-investigation-result.md`](macro-investigation-result.md). Read that first; the sections
> below are the round-by-round record it summarises.


## Phase 2 — macros: inferred protocol values

Two values could not be sourced from documentation for these boards. Both are recorded here so the
hardware test can confirm or refute each one independently, and both are logged at runtime.

| Value | Basis | If wrong, the symptom is |
|---|---|---|
| **Macro-data command `0x2D`** | Raster's `CMD_SET_MACRODATA` is `0x2D`, and Raster is documented in-tree as *"SmartInfinite-adjacent"* running the same 智慧无限 v1.4 spec these boards run. Command numbering is expected to carry across the family even though the framing differs (Raster: report `0x00`, 65 B, `0xAA` tag · Smart Infinite: report `0x06`, 64 B, additive checksum). | Chunk writes are not acked, or acked with a status other than `0x55`. The upload reports failure and no binding is written. |
| **Macro key-record type `0x50`** | **Documented**, not guessed: `SmartInfiniteProtocol.KnownTypes` — populated from the vendor spec by #72 — lists `0x50, 0x51, 0x52` as *macro* on this family, with `0x60/0x61` being *custom fn* / *text*. This is a genuine divergence from Raster, which uses `0x60` for macros. | The key plays nothing, or plays the wrong function. `0x51` and `0x52` are the other two macro types to try. |

Buffer layout is **not** inferred: the packed macro buffer is produced by the existing
`RasterMacroEncoder`, reused deliberately rather than reimplemented, because the buffer is a
property of the shared spec rather than of the transport. That also inherits the press/release
attribute semantics (`0x81` down / `0x01` up) that cost a debugging round to establish on Raster.
The only added check is that the device's reported macro space (Device Info byte 5 × 256) is large
enough for the packed length — a board reporting **0** is the Rampage signal and the upload refuses
rather than writing into space that does not exist.

## Phase 2 — hardware result (2026-07-30, Cyrex + Aress)

**Both inferred values are confirmed at the wire level.** From the DeviceHost log of a real Cyrex
(VID `0x28E9` PID `0x3272`, firmware `0x0002`):

```
[CYREX] device info: matrix=75 slots (225 B) macro=1024 B keys=68 transport=USB
[CYREX] packed library dataLen=30 bytes, device macro space=1024 bytes
[CYREX] wrote the macro library (1 packets, 30 bytes)
[KEY CYREX] SetKeyMapping slot=19 record={0x50, 0x00, 0x00}
[KEY CYREX] wrote the key matrix (5 packets, 225 bytes, 5/5 acked)
[KEY CYREX] slot 19 verified as {0x50, 0x00, 0x00}
```

- **Command `0x2D`** — accepted and acked. Had it been wrong, the chunk write would have gone
  unacked and the upload would have refused.
- **Type `0x50`** — accepted into the key matrix and **read back verified**, so the firmware's own
  table validation treats it as a legal record.
- **Macro space** — 1024 bytes on both boards, so the Rampage zero-space refusal never triggers.
- Aress reports the same geometry (`matrix=75, macro=1024 B, keys=68`, fw `0x0000`).

**Playback: first attempt did nothing** — the record was stored and verified, and the key typed
nothing. See the code2 finding below.

### The macro type was wrong: `0x50` is FUNC_TYPE, the macro type is `0x60`

Two hardware rounds were spent on the third byte — first `0x00`, then `0x01` — while the **first**
byte was the broken one. Both runs wrote a record the firmware accepted and verified on read-back,
and both played nothing.

The vendor workbook `docs/软件驱动协议-20251028-V3.1.xlsx` settles it (now translated to
`docs/knowledge-base/smart-infinite-protocol/`). Its `Keyboard_Key_Type` enum reads:

```c
FUNC_TYPE            = 0X50,  // 功能类按键      function key
MACRO_AP_TYPE        = 0X60,  // 单次宏功能按键  single-play macro
MACRO_AP_REPETI_TYPE = 0X61,  // 多次宏功能按键  repeat macro
```

`0x50` is the **function** type — layer switch, backlight, Bluetooth channel — and its function enum
starts at `0x01`, so `{0x50, 0x00, n}` is a well-formed record naming function 0, which does not
exist. Stored, verified, inert. Exactly what was observed, twice.

The macro type is `0x60`, **the same byte Raster uses**: it is one spec, not two. The sheet even
gives the worked example — to make ESC a normal macro key, set its three matrix bytes to
`0X60 0X00 0X00`, which is what this branch sent before the type was "corrected" to `0x50`.

| User setting | Record | Source |
|---|---|---|
| Play once | `{0x60, index, 0x00}` | 正常宏类型 |
| Repeat while held | `{0x60, index, 0x01}` | 释放停止 |
| Play *n* times | `{0x61, index, n}` | "`0X61`, which macro, how many times" |

Two further play modes are documented and unused: `0x02` (start on press-and-release, run until
pressed again) and `0x03` (press to start, press again to stop). The UI has no control for either.

**Where the bad value came from.** `SmartInfiniteProtocol.KnownTypes` — written during #72 — carried
a comment labelling `0x50`-`0x52` "macro" and `0x60/0x61` "custom fn, text". That is the vendor enum
inverted. The comment was never load-bearing (`KnownTypes` is only a table-shape sanity check, and
it accepts both ranges) so nothing caught it, and it was then cited as documentation. The comments
now name the enum's own constants.

**Type `0x61` is used on Smart Infinite only.** Not because the spec is family-specific — it covers
both — but because Raster's `0x60` is hardware-confirmed and its `0x61` is not. A repeat count is
silently ignored on Raster today, which is wrong but harmless; sending an unverified type to a
working board could stop its macros entirely. Worth unifying when a Raster is on the bench.

### Round 3: the record is now spec-exact and the key still plays nothing

`{0x60, 0x00, 0x00}` — byte for byte the sheet's worked example — written to slot 17, 5/5 packets
acked, verified on read-back. Library written, 30 bytes, 1 packet, into 1024 bytes of macro space.
Nothing types.

Everything on the key side is now positively established rather than assumed:

| Element | Status |
|---|---|
| Interface, session, checksum | Proven — the same handle remaps ordinary keys, which works |
| Slot number | Proven — `{0x10, 0x00, 0x1A}` was read from slot 17 and is the key the user picked |
| Key record type / mode | Spec-exact, and verified on read-back |
| Macro command `0x2D` | Documented (设定宏信息) and acked |
| Packed buffer layout | Matches the sheet field for field, including `dataLen` at bytes 2-3 |
| Event attribute bytes | Match the sheet's bit-7 rule |
| **What the device actually stored** | **Never observed** |

That last row is the gap, and it is the only one left worth spending a hardware round on. So rather
than change another byte on a hunch, `SmartInfiniteMacroService` now reads the library back with
`0x2C` (读取宏信息) after writing and logs whether it matches, with the first divergent byte and
both hex strings. An acked write proves nothing on this family — Rampage acks key-matrix writes and
discards them — and nothing in the log so far answers "is the firmware holding the bytes we sent".

The verification is diagnostic and does not fail the upload: a read that disagrees is at least as
likely to mean the read command behaves differently than assumed as it is to mean the write was lost.

**The next run splits the remaining space cleanly:**

- *"macro library verified: 30 bytes read back identical"* → the data is on the device exactly as
  intended and the key record is spec-exact, so the missing piece is activation, not encoding.
  Suspects then: a function-area (`0x14`/`0x15`) flag that enables macro playback, or the board
  running a different key-matrix layer than the one we write.
- *"read back DIFFERENT at byte N"* → the write is landing somewhere or something other than
  intended, and byte N says where. Most likely the address base for `0x2D` is not the macro region's
  offset 0.
- *"could not read the macro library back"* → `0x2C` is not the read command on this firmware, which
  also weakens the assumption that `0x2D` is the write command despite the ack.

### Round 4: the device stores nothing — `0x2D` is acked and discarded

The read-back answered it:

```
sent=AA551E00010000000A00040000000A00178150001701DC00178164001701
read=000000000000000000000000000000000000000000000000000000000000
```

The sent buffer decodes against the sheet exactly: `AA 55`, length 30, 1 macro, offset table
pointing at 10, 4 actions, then `0A00 17 81` (delay 10, `KEY_T` = `0x17`, press), `5000 17 01`
(release), `DC00 17 81`, `6400 17 01`. That is "tt", byte for byte.

The device holds **all zeros**. The `0x2C` read replied well-formed and `0x55`-acked, so the read
command works and the macro region is genuinely empty. The write is acknowledged and dropped.

**This is the Rampage pattern**, one subsystem over: the firmware acks a vendor write on a handle
that demonstrably works for other writes, and keeps nothing. On Rampage it is key-matrix writes with
a Device Info key count of 0; here it is macro writes on a board that reports 1024 bytes of macro
space. The parallel is close enough to be worth stating plainly — an ack on this family is not
evidence of storage, and that is now the second subsystem where it has misled this project.

The verification now runs in its **own session**, after the closing `0x11`. An in-session read
cannot distinguish "discarded" from "not committed to flash yet", and those want opposite responses;
reading across a session boundary removes the ambiguity. The log also classifies the result, so an
all-zero region says so in words rather than leaving the next reader to decode hex.

### Where that leaves Phase 2

Everything host-side is correct and proven so: interface, session, checksum, slot, key record,
buffer layout, event encoding, and the two commands. The one thing that does not work is the
firmware accepting macro data.

What is worth trying next, cheapest first:

1. **Is `0x2D` really the write on *this* build?** The command table in the sheet is a later
   revision and is already known to disagree with these units on the key-matrix commands
   (`0x1A`/`0x1B` there, `0x17`/`0x18` measured here). If macro storage shifted the same way, the
   real write command is a neighbour. `0x2C` reads plausibly, which argues the pair is right — but
   the pair could equally be `0x2E`/`0x2F` on this build. This must be probed carefully: `0x20` is
   a full factory reset and `0x31` a reset in the newer numbering.
2. **Does a vendor tool write macros to this board at all?** If the vendor's own software cannot
   either, this is a firmware limitation to report, exactly as Rampage's key remapping was, and the
   answer is a vendor question rather than a defect here.
3. **Is there an enable/commit step?** The function area (`0x14`/`0x15`) is the only other writable
   region we know of, and its contents are not fully decoded.

Until one of those lands, `SupportsMacros` for these two boards advertises a feature the hardware
does not accept. Backing the two `appsettings.json` entries out is a one-line change and returns
both boards to the current, honest behaviour.

### What the workbook also confirmed

- **`0x2D` is right** — 设定宏信息, with `0x2C` to read it back. That inference held.
- **The packed buffer layout is right** — `0xAA 0x55`, length, count, 2-byte offset table, per-macro
  action count, 4-byte actions. Reusing `RasterMacroEncoder` was correct.
- **`0x81` press / `0x01` release is right** — bit 7 is the press flag and the low bits are the
  action kind, where `0x01` is "keyboard normal key".
- **Its command numbering is a later revision than our firmware** and disagrees with measured values
  (it lists `0x1A`/`0x1B` for key matrix 1 where our boards answer on `0x17`/`0x18`, and calls
  `0x31` reset while `0x20` was measured as a full factory reset). Data formats: trust. Command
  numbers: verify.

### The defect this session actually found

The write above succeeded end to end and `key_macro_bindings` was *still* empty — the Macros page
showed the key as unbound, which is what "I cannot assign it" was. The guard between the device
write and the SQLite write compared `_keyLayoutGeneration`, which `LoadKeyLayoutForDevice` bumps
even when the **same** device is reloaded (card refresh, preset apply). Landing in that window made
the method return after the firmware had already been written, so device and database disagreed.

Fixed by guarding on device identity — `(VendorId, SettingsProductId, SettingsDeviceKey)` captured
before the awaits — which is the condition the guard was always meant to express. Cyrex and Aress
were hit far more often than Raster because their key write is a full 225-byte read-modify-write of
the matrix, measured at ~420 ms, against Raster's single-slot write. Regression tests:
`Cyrex_LayoutReloadDuringTheWrite_StillPersistsTheBinding` (reload keeps the binding) and
`Cyrex_DeviceSwitchDuringTheWrite_DiscardsTheBinding` (a real switch still discards it).

## Phase 2a — The original spike (superseded)

Retained for the reasoning, but the gate no longer blocks implementation — the user chose to test on
hardware directly. Step 4 below is still the only exit criterion that counts.

**This is an investigation, not a feature. It produces a written answer and a decision, and it
must not leave half-finished production code behind.**

What is known:

- `SmartInfiniteProtocol.cs` implements `0x10`/`0x11` (session), `0x12` (device info),
  `0x14`/`0x15` (func info), `0x16`/`0x17`/`0x18` (key tables), `0x19` (custom lighting). There
  is **no macro command**.
- Device Info (`0x12`) reports a **macro space** field, so the firmware plausibly has storage.
- Report ID `0x06`, 64-byte reports (8-byte header + 56-byte payload), header
  `cmd / addr LE / len / checksum LE / status`, byte 7 = `0x55` on success. The checksum is a
  plain 16-bit additive sum, **not** a CRC despite the vendor sheet's label.
- `SmartInfiniteDeviceGate` — one `SemaphoreSlim` per `DevicePath` — must be held by any new
  service. The firmware allows one session per interface and the closing `0x11` ends it for the
  *interface*, not the sender, so a service that ignores the gate silently truncates the
  key-mapping or RGB session mid-flight.
- **The precedent that matters:** Rampage is in this family, reports a key count of 0, and
  **acknowledges then discards** key-matrix writes. An ack proves nothing.

Spike steps, in order, using `src/ProoveGaming.Device.Testing` (which already hosts
`SmartInfiniteProbe`) so nothing lands in shipping code:

1. Read and record Device Info on both boards; report the macro-space value. If it is 0, stop —
   that is the Rampage signal and the answer is "not supported".
2. Search the vendor material already in the repo (`docs/knowledge-base/`, the Smart Infinite
   protocol spreadsheet referenced by the #72 plan) for a macro command id. Do **not** probe
   command ids blindly — `0x20` is a factory reset, and neighbouring unknown ids on this family
   are not safe to poke.
3. If a candidate command exists, write one minimal macro and read it back.
4. **Exit criterion — the only one that counts:** bind that macro to a key and confirm the
   physical key plays it. A successful write plus a matching read-back is *not* success; that is
   exactly what Rampage does before discarding.
5. Write the findings — including a negative result — into this plan's Open questions and the
   knowledge base.

**Gate:** if step 4 does not pass on both boards, Phase 2b is not started. Cyrex/Aress keep
`SupportsMacros` absent and the Macros nav item stays hidden, which is the current behaviour and
is therefore a safe stopping point.

## Phase 2b — Implementation (built; awaiting the hardware test)

Mirror Raster's pipeline; **do not assume its wire format**. Raster's 1096-byte packed buffer,
its `0x2D` command, and its `[type=0x60, code1=slot, code2=play mode]` key record are
Raster-family specifics. What is expected to carry over is the *shape*, not the bytes:

| Raster component | Cyrex/Aress equivalent |
|---|---|
| `RasterMacroEncoder` (packed buffer, `PackResult` with discard counts) | **Reused as-is**, not reimplemented — see the inferred-values section above for why the buffer is a spec property rather than a transport one. `PackResult`'s discard counts are surfaced in the new service's log. |
| `RasterKeyboardMacroService` | `SmartInfiniteMacroService` — validates every chunk ack via `ReplyFor` (command echo, address echo, status `0x55`) and fails the whole upload on any one of them; holds `SmartInfiniteDeviceGate`; brackets its work in `0x10`/`0x11`; refuses when Device Info reports zero macro space or less than the packed length. |
| `IKeyboardMacroService` / `IDeviceKeyboardMacroServiceFactory` | Reused as-is. Cyrex and Aress dispatch through `CreateSmartInfiniteMacroService`, which picks the interface with `SmartInfiniteInterfaceSelector` (usage `0x20` / page `0xFF87`) rather than Raster's `outLen==65` predicate. Rampage is deliberately absent. |
| `SetMacros` RPC | Reused as-is. |
| Key record `[0x60, slot, playMode]` | Type byte differs: `MacroTypeByteForDevice` sends `0x50` on the 65% boards and `0x60` elsewhere. `code1` (library slot) and `code2` (play mode) are unchanged — the play-mode semantics were hardware-confirmed on Raster and are assumed to carry across. |
| `key_macro_bindings` + `ResyncKeyMacroBindingsAsync` | Reused as-is, device-scoped per the macro-per-device plan. `ApplyAssignmentTargetAsync` used to `return` early for this family before reaching the persistence block, which would have left Smart Infinite bindings unpersisted — fixed so the write runs for every family. |
| `appsettings.json` | `SupportsMacros: true` + `Macros { MaxDelayMs: 1000, MaxRepetitions: 255 }` on both entries. Set **before** hardware confirmation at the user's direction ("the same workflow like for Raster") — so if the hardware test fails, backing these two entries out is what hides the Macros page again. |

Still open, and only hardware can answer it: whether a repeat count is expressible. On Raster it
is not (the 3-byte record has no room, so `MacroViewModel.Repetitions` is inert there), and nothing
about this family's record suggests otherwise — the same three bytes carry type, slot and play mode.
`MaxRepetitions: 255` in the config therefore describes the recorder's ceiling, not something the
firmware honours. If the hardware test shows Smart Infinite *can* express a count, that is a UI
difference between families worth surfacing rather than quietly ignoring.

## Acceptance criteria — Phase 2

Spike — **superseded.** The user chose to test on hardware directly rather than run a separate
investigation, so these were not pursued:

- [~] Given both boards, then the Device Info macro-space value is recorded in this plan. *Not
      recorded up front; instead `SmartInfiniteMacroService` reads and logs it on every upload and
      refuses to write when it is zero, which is the guard the spike step existed to provide.*
- [~] Given the spike concludes, then this plan's Open questions state either the working command
      sequence **or** an explicit "not supported, here is the evidence". *Replaced by the
      inferred-values table above, which states both values, their basis, and the symptom if wrong.*
- [~] Given the spike ran, then no new code remains outside `src/ProoveGaming.Device.Testing`.
      *Deliberately not met — the code shipped straight into the production path at the user's
      direction. If the hardware test fails, the two `appsettings.json` entries are what to back out.*

Implementation — all of these need hardware and none are verified yet:

- [ ] Given a Cyrex with a recorded macro bound to a key, when that physical key is pressed, then
      the macro plays, and afterwards **no key remains held** (the stuck-key failure mode from the
      Raster work).
- [ ] Given a HID write or ack fails during upload, then the upload reports failure and **no** key
      binding is written or persisted.
- [ ] Given a macro is edited and saved, then the library is re-uploaded and the bound key plays
      the new content — not the old one.
- [ ] Given a macro upload runs while an RGB write is in flight on the same board, then neither
      truncates the other (the device gate is honoured).
- [x] Given a macro is recorded on one board, then it does not appear in another board's library —
      macros are device-scoped (see `../migrate-macros-per-device/`).
- [x] Given a macro is bound to a key on Cyrex or Aress, then the binding is written to
      `key_macro_bindings` so it survives a restart. Was **not** true when this was written: the
      apply path returned early for this family before reaching the persistence block.

## Test strategy

**Phase 1 — `tests/ProoveGaming.App.Tests` (must be added)**

- `UseArtworkKeyOverlays` per board: true for Raster / Cyrex / Aress, false for T-Racer. This is
  the gap Round 6 **S1** identified, and it already let a real bug reach a committed merge —
  Cyrex and Aress were briefly routed through *Raster's* coordinates because the gate keyed on
  "has artwork" rather than "has measured bounds", and 559 passing tests did not notice.
- The resolved layout table is the board's own — assert a Cyrex device resolves `CyrexArtworkLayout`,
  not `RasterArtworkLayout`. A property returning the wrong table is the failure this phase can
  most plausibly introduce.
- Every catalog key except FN has bounds in its board's artwork layout, and no layout entry
  references a slot absent from the catalog. This is the generated-data sanity check that catches
  a mis-run labelling pass.
- Aress borrows **only** the overlay artwork: `KeyOverlayArt` equals Cyrex's while `KeyboardArt`
  stays the Aress art. Without this, the obvious way to implement the borrow — changing
  `KeyboardArt` — would silently swap the RGB preview and home card too, and nothing else would
  have noticed.
- Aress's borrowed bounds are keyed by **Aress** key names, and cover every key in its catalog. The
  join is by matrix slot precisely so the three divergent navigation keys resolve; a name-keyed join
  would drop exactly those three overlays, which is a quiet failure on a board where the other 64
  keys look right.

**Phase 2 — `tests/ProoveGaming.Infrastructure.Tests`**

Landed in `SmartInfiniteProtocolTests`:

- The packed library survives the framing: chunk it with `EnumerateChunks`, build each report, and
  reassemble the payloads — must equal the encoder's buffer byte-for-byte over `DataLen`. This is
  the one part of the new path that *is* testable without hardware, and it covers the plausible
  silent bug (a dropped or overlapping packet leaves the device holding a library the key slots
  point into wrongly).
- The two inferred values are pinned with the reason stated: `CmdSetMacroData` is asserted **not**
  to be `0x20` (factory reset — the dangerous neighbour if anyone re-derives it) and `TypeMacro`
  **not** to be Raster's `0x60` (the natural copy-across mistake).
- A macro-bound slot passes `IsPlausibleKeyTable`, so `SetKeyInfo` will accept the table.

Not written, and why:

- No new golden-byte encoder tests — the buffer is `RasterMacroEncoder`'s, already covered by
  `RasterMacroEncoderTests`. Duplicating them would assert the same code twice.
- No ack-validation tests for `SmartInfiniteMacroService`. **The known obstacle stands:** it takes a
  concrete `HidSharp.HidDevice` and opens a concrete `HidStream`, neither substitutable from outside
  HidSharp — the same reason `RasterKeyboardMacroService` has none. Introducing a stream seam would
  make both testable and is worth doing, but as a change to Raster's service too, not smuggled in
  here.

**Deliberately not automated**

- Rendered overlay positions (no Avalonia render tests here) — QA covers it visually.
- Real HID exchange with either board — no device in CI.

**CI reality:** `.github/workflows/dotnet-ci.yml` runs on `ubuntu-latest` with `dotnet test`
pointed at the App csproj, which references no test SDK — it passes having run **zero** tests,
and both test projects are Windows-only TFMs. Nothing here is protected by CI; all of it must be
run locally.

## QA (manual test steps)

**Phase 1 — key overlays**

Requires a Cyrex and/or an Aress keyboard.

1. Connect the Cyrex and open it. Go to the Key Assignments page.
   **Expected:** you see a picture of the actual keyboard, and the clickable key boxes sit
   directly on top of the real keys — not floating on a plain grid.
2. Click several keys spread across the board — a corner key, a space bar, an arrow key, a
   navigation key on the right-hand column.
   **Expected:** each click highlights the key you actually clicked, and the heading names that
   same key.
3. Resize the app window smaller and larger.
   **Expected:** the key boxes stay lined up with the keys at every size.
4. Remap one key (e.g. make `A` type `B`), then open Notepad and press it.
   **Expected:** it types the new letter, exactly as before this change.
5. Repeat steps 1–4 with the Aress.
   **Expected:** the same — a real keyboard picture with the boxes on the keys. **Known and
   intentional:** the picture shown is the Cyrex board, because the artwork that ships for the
   Aress draws a different keyboard. It is the same physical hardware, so everything lines up; the
   only visible difference is three keycaps in the right-hand column printed `Delete / PageDown /
   End` where an Aress reads `Insert / Delete / PageDown`. Clicking them must still show the
   **Aress** names in the heading — if the heading says Delete when you clicked Insert, report it.
6. Still on the Aress, go to the RGB / lighting page and to the device list home screen.
   **Expected:** those keep showing the Aress's own artwork (unchanged by this work) — only the
   Key Assignments and Macros pages borrow the Cyrex picture.
7. If a T-Racer is available, open its Key Assignments page.
   **Expected:** it still shows the plain grid layout with a box for every key — unchanged, and
   intentional.

**Phase 2 — macros**

These steps are the hardware confirmation itself: two protocol values were worked out by
reasoning rather than measured, so a failure here is an expected outcome, not necessarily a bug.
Report what you see either way.

8. Open the Cyrex and go to the Macros section.
   **Expected:** the section exists, and shows the same clickable keyboard as Key Assignments.
9. Record a macro that types `hello`, save it, select a key, assign the macro.
   **Expected:** assignment is accepted with no error message.
10. Open Notepad and tap that key once.
    **Expected:** `hello` is typed. Afterwards the keyboard behaves normally — no character
    repeats forever and no key acts stuck. If anything sticks, note it and reset the keyboard.
    **If nothing is typed at all**, that is the most likely failure: note it and say whether step 9
    reported an error, because the two point at different causes.
11. Edit the macro to type `world` instead and save. Tap the key again.
    **Expected:** it types `world`, not `hello`.
12. Create a macro, then switch to a different keyboard and open its Macros section.
    **Expected:** the macro you just created is **not** listed there — macros belong to the device
    they were recorded on.
13. Change the lighting colour while a macro assignment is in progress.
    **Expected:** both complete; neither the lighting nor the macro silently fails.
14. Unplug the keyboard and try to assign a macro.
    **Expected:** the app reports a failure. It must **not** look like it worked.
15. Assign a macro to a key, close the app, reopen it and connect the same keyboard.
    **Expected:** the Macros page still shows that key as holding that macro, and the key still
    plays it.
16. Repeat 8–15 on the Aress.

## Review focus

- `gRPC contract` — Phase 2 reuses `SetMacros`; confirm both sides move together and the new
  board dispatch does not change the existing Raster path.
- `thread-safety` — `SmartInfiniteDeviceGate` is the load-bearing invariant of this family; a
  macro service that does not hold it will truncate other services' sessions intermittently.
- `resource disposal` — HID streams per upload; the session must be closed on every path,
  including failure.
- `async/await` — upload and resync paths, and failure propagation out of the new service.

## Performance & quality

- **Phase 1 has no hot path.** Overlay layout is computed on device load and on
  `PhysicalKeys` change, not per frame. The artwork layout tables are static readonly
  dictionaries built once. Keep the glow `Geometry` parsing lazy-and-cached as the existing
  `RgbKeyboardArt` does — it must never be re-parsed per rebuild.
- **Phase 2:** a macro upload is a chunked HID exchange with a read per chunk. It must stay off
  the UI thread (it goes through the gRPC client, as Raster's does) and must not be issued
  per-keystroke — the whole library is re-sent per library change, which is the deliberate
  trade-off Raster already accepted.
- **Generated data size:** two more artwork layout tables of ~68 entries each. Negligible, but
  keep them generated and regenerable, with the derivation recorded in the file header so a
  future artwork change can be re-run rather than reverse-engineered.

## Dated notes

- *2026-10-01 (QA - yellow-green stripes between keys on the Cyrex card).* The delivered Cyrex
  artwork has its RGB backlight BAKED IN as a rainbow rim light along every keycap edge. For the
  layered build that light is supposed to be stripped out - the app draws its own - and for
  Rampage, Raster and Aress it was. For Cyrex it never happened: `PG_Cyrex_keys.svg` carried a
  raster pixel-identical to the as-delivered `Card/PG_Cyrex_card.svg`.

  It only became visible at card size. Those rim lights are about a pixel wide, the card draws the
  board at roughly a quarter scale, and the downscale point-samples: most of the lines average away
  and the handful that happen to land on a sample column survive at full brightness. That is why QA
  saw a few bright stripes between individual keys rather than a uniformly lit board. Confirmed by
  downscaling the raster three ways - point sampling reproduces exactly those isolated stripes,
  bilinear and Lanczos do not.

  The raster is now stripped the way the other boards were: the sharp rim lines are inpainted from
  their neighbours and what remained of the bloom is neutralised to grey, leaving an unlit board.
  Hue separated the two - the backlight runs red through cyan while the modifier keycaps are
  blue-violet (HSV hue 145-205), so the blue caps are untouched. 4.8% of the raster changed.
  `Card/PG_Cyrex_card.svg` keeps the lit look: it is the as-delivered record, and nothing draws it
  for Cyrex anyway (`UseUnlitCardArt`).

  **Not verified:** the Cyrex RGB page. With the baked light gone the live preview depends entirely
  on `Glow/Cyrex.path`, which - unlike Rampage/Raster/Aress/T-Racer - is a plain rectangle over the
  whole key well rather than per-key geometry. It should read better than before (the baked rainbow
  ignored the colour picker entirely), but there was no Cyrex connected to this machine to look at.
  Worth a QA pass, and the rectangle glow is worth its own ticket.

## Risks

- **Phase 2 may simply not be possible.** Rampage in the same family acks and discards key
  writes; the firmware may treat macros the same way. The spike gate exists so this ends as a
  documented negative rather than half-built code — and a negative result is a legitimate,
  useful outcome of this plan.
- **Probing unknown command ids on this family is dangerous.** `0x20` is a full factory reset.
  The spike must work from vendor material, not by sweeping ids.
- **Artwork layouts are tied to a specific asset revision.** `RasterArtworkLayout` already
  carries this exposure; adding two more triples it. Replacing any keys SVG silently invalidates
  every overlay position on that board, with no automated check. The catalog-coverage test in
  Test strategy is the only guard — keep it.
- **Aress's near-square viewBox** means its placement transform is materially different from
  Cyrex's despite identical hardware. Deriving one and assuming it applies to both would misplace
  every Aress overlay. They are separate derivations.
- **Two boards, one physical layout, three navigation keys apart.** The slot-keyed join is what
  keeps the catalogs honest; hand-editing either artwork table by key *name* would reintroduce
  the drift #72 designed that join to prevent.

## Open questions

1. **Spike outcome — the gating question.** Does the Smart Infinite firmware accept and *honour*
   macro writes on Cyrex and Aress? To be answered by Phase 2a, with the Device Info macro-space
   value recorded either way.
2. ~~**Can Smart Infinite express a macro repeat count?**~~ **Answered.** Yes — the vendor workbook
   documents `MACRO_AP_REPETI_TYPE 0x61`, where `code1` is the macro index and `code2` the repeat
   count. Implemented in `MacroBindingRecord` for Smart Infinite only; Raster keeps `0x60` because
   its `0x61` is unverified and a repeat count is inert rather than wrong there. So the control does
   mean different things on different keyboards, and the divergence is deliberate and documented —
   worth revisiting if a Raster ever reaches a bench.
3. **Should the new macro service be built against a stream seam?** It would make ack-validation
   unit-testable, which Raster's is not, and could retro-fit to Raster. Slightly more
   indirection now for real coverage of the exact code path that has already produced one blocker.
4. **Re-export the artworks to a common viewBox?** Option C above. Rejected for this plan, but if
   a third 65% board arrives it becomes the cheaper path.

## Review findings

### Round 1 — Ship: no

Reviewed at `034fc0b`, against `origin/main` (`b9791d5`). **Note on surface:** local `main` is stale
(`e20554c`), so `git diff main...HEAD` attributes #70 and #73 to this branch. The real delta is
`origin/main...HEAD` — 25 files, +3976/-113. Working tree clean; every `git status` entry appears in
the committed delta.

#### Plan conformance

Plan: `docs/plans/add-cyrex-aress-parity/add-cyrex-aress-parity-plan.md` (plus the per-device macro
work in `../migrate-macros-per-device/`).

- **Implemented as planned (Phase 1):** measured `CyrexArtworkLayout` with its derivation recorded in
  the file header (as Performance & quality requires, so a future artwork revision can be re-run
  rather than reverse-engineered); the artwork gate expressed as "measured bounds exist" rather than
  "has artwork"; the overlay view taking layout and viewBox from the view model; the dead
  `OverlayCanvas*` notifications removed.
- **Deviates from plan, documented:** `AressArtworkLayout.cs` was **not** created. Superseded by the
  option the plan itself selected — the Aress artwork does not depict the Aress board, so no table
  can be measured from it, and Aress reuses Cyrex's joined by matrix slot. The change list is struck
  through with the reason. Correct handling of a deviation.
- **Deviates from plan, documented:** `0x61` (repeat macro) is used on Smart Infinite only, not on
  Raster, despite being one spec. Reasoned in `MacroBindingRecord`: Raster's `0x60` is
  hardware-confirmed and its `0x61` is not, and a repeat count is inert on Raster today rather than
  wrong. Conservative and correctly justified.
- **Phase 2 outcome:** the plan's Risks section anticipated exactly this — "Phase 2 may simply not be
  possible… a negative result is a legitimate, useful outcome of this plan." The negative is
  established with evidence and written up. That is conformance, not failure.
- **Open question 2 is now answered and the plan does not say so.** "Can Smart Infinite express a
  macro repeat count?" — yes, via `MACRO_AP_REPETI_TYPE 0x61`, per the workbook. The Open questions
  section still poses it as unresolved. Stale.
- **Open question 3 remains open, with a consequence:** no stream seam was introduced, so
  `SmartInfiniteMacroService` (323 lines, the whole of Phase 2) has **zero** unit coverage. Predicted
  by the plan; still a real gap (see Tests).
- **Acceptance criteria:** Phase 1's automated criteria are checked and genuinely met; its hardware /
  QA criteria are correctly left unchecked. Phase 2's implementation criteria are unchecked and
  cannot be met on current firmware. The superseded spike criteria are marked `[~]` with honest
  explanations rather than quietly dropped.

#### Code

- [~] **B1. Both boards advertise a Macros feature that provably does not work, with no user
      feedback** — `src/ProoveGaming.DeviceHost/appsettings.json:278` (Aress), `:314` (Cyrex) — Blocker
  **Problem:** `SupportsMacros: true` is the only gate on the Macros page
  (`DeviceDetailsViewModel.MacroRecorder.cs:44` — `IsMacrosSupported => Device?.SupportsMacros == true`).
  Round 4 proved the firmware discards macro data. Every step in the flow still reports success: the
  library write is acked, `SetKeyMapping` succeeds and verifies, and the binding is persisted.
  **Why it blocks:** a user opens Cyrex → Macros, records a macro, assigns it to a key, sees it
  listed as bound, presses the key, and nothing happens. No error, no warning, no degraded state in
  the UI — the only evidence is a `[CYREX] macro library read back DIFFERENT` line in the DeviceHost
  log. The plan already named the remedy for this exact outcome ("if the hardware test fails, backing
  these two entries out is what hides the Macros page again"); the hardware test failed and the
  remedy has not been applied.
  **Solution options:**
  - Option A (honest, one line per board): set `SupportsMacros: false` for Aress and Cyrex. The nav
    item disappears and both boards return to their pre-branch behaviour. All Phase 2 code stays and
    works unchanged the moment the firmware accepts data.
  - Option B (better, more work): use the signal we now have. `VerifyStoredLibrary` already
    distinguishes "region is entirely zero" from "read command misbehaved". Fail the upload on the
    all-zero case, so the existing `if (!uploaded) return;` in `ApplyAssignmentTargetAsync` prevents
    the binding and the UI stops claiming success. Converts a silent lie into a visible failure and
    keeps the page available for testing.
  - Option C (only if this branch is explicitly not merging to `main` yet): leave as-is. The page
    stays reachable for diagnosis, at the cost of shipping a lie if it does merge.

  **Decision (2026-07-31): declined — Option C.** The Macros section stays visible on both boards
  and the upload continues to report success regardless of what the firmware retains. Recorded here
  so this reads as a deliberate call rather than an oversight. **Residual risk, unchanged:** on
  Cyrex and Aress a user can record a macro, assign it, see it listed as bound, press the key and
  get nothing, with no error anywhere in the UI — the only evidence is the
  `macro library read back DIFFERENT` warning in the DeviceHost log, which now requires Debug
  logging (S4). Revisit if the firmware question in
  [`macro-investigation-result.md`](macro-investigation-result.md) is resolved, or before this
  reaches end users.

- [x] **S1. The read-back diagnostic can fail an upload that actually succeeded, contradicting its own
      documented contract** — `src/ProoveGaming.Infrastructure/Services/SmartInfiniteMacroService.cs:205`
      (called at `:84-86`) — Should-fix
  **Problem / why it matters:** the XML doc says "Diagnostic only — it never fails the upload", but
  `VerifyStoredLibrary` has only `try/finally` and no `catch`. `_device.TryOpen` returning false is
  handled; a non-timeout `IOException` from `stream.Write` (`:217`, `:225`, `:269`) or `stream.Read`
  is not — `ReplyFor` catches `TimeoutException` only (`:301`). Such an exception propagates out of
  `UploadOnce`, is caught by the blanket handler in `UploadMacrosAsync` (`:71-75`), and returns
  **false**. Concrete scenario: the write session completes and the library is on the device; in the
  gap between the two sessions the interface is momentarily busy or the board is unplugged; the
  verify throws; `SetMacros` reports failure; `ApplyAssignmentTargetAsync` hits `if (!uploaded)
  return;` and skips both the key write and the SQLite binding — for an upload that worked. The log
  then reads "macro upload failed", pointing the next debugger at the write path that succeeded.
  **Fix:** wrap the body of `VerifyStoredLibrary` in `try/catch (Exception ex)` that logs at Warning
  and returns, so it cannot influence the upload result. One `catch`, after which the code matches
  what its own doc promises.

- [x] **S2. The service's class documentation states the protocol values that were proven wrong** —
      `src/ProoveGaming.Infrastructure/Services/SmartInfiniteMacroService.cs:24-31` — Should-fix
  **Problem / why it matters:** the `<remarks>` still says both values are "inferred, not documented
  for these boards", and that `TypeMacro` is `0x50` "from the vendor type table — a genuine
  divergence from Raster's `0x60`". All three claims are now false: `TypeMacro` is `0x60`
  (`SmartInfiniteProtocol.cs:100`), it is *identical* to Raster rather than a divergence, and both
  commands are documented in the workbook (`0x2C` / `0x2D`). This is not cosmetic staleness — the
  most expensive mistake on this branch was trusting exactly this kind of comment as documentation
  (the inverted `KnownTypes` labels, two hardware rounds). The corrected constants carry good
  explanations; the file a reader opens **first** still teaches the old wrong thing.
  **Fix:** rewrite the paragraph to state the documented values, note that the encoding is shared
  with Raster, and point at `docs/knowledge-base/smart-infinite-protocol/`. Record the actual open
  item instead: the firmware discards the data.

- [x] **S3. The unacked-chunk log message repeats the same stale claim** —
      `src/ProoveGaming.Infrastructure/Services/SmartInfiniteMacroService.cs:172-176` — Should-fix
  **Problem / why it matters:** on a failed chunk write it tells the reader that command `0x2D` "is
  inferred from Raster, not documented for this family". It **is** documented — 设定宏信息 in the
  vendor command table — and it is acked in practice. Whoever hits this message next is sent to
  re-derive a command number that is already confirmed, instead of to the real question.
  **Fix:** reword to note that `0x2D` is documented as 设定宏信息 and is acked on Cyrex firmware
  `0x0002`, so an unacked chunk means this firmware build differs — and point at the
  command-numbering caveat in `docs/knowledge-base/smart-infinite-protocol/`.

- [x] **S4. The diagnostic read-back runs on every upload, permanently** —
      `src/ProoveGaming.Infrastructure/Services/SmartInfiniteMacroService.cs:84-86` — Should-fix (low)
  **Problem / why it matters:** every macro save now performs a second device open, a second
  `0x10`/`0x11` session and a full re-read of the library. For a small library that is one extra
  round-trip; for a full 16-macro library (~1096 bytes) it is ~20 extra reads plus 20 × 5 ms of
  inter-packet sleep. Not a hot path — the plan is explicit that uploads are per-library-change, not
  per-keystroke — so this is cost, not a regression. But it is investigation scaffolding wired into
  the shipping path with no way to turn it off.
  **Fix:** gate on `_logger?.IsEnabled(LogLevel.Debug)`, matching the pattern
  `RasterKeyboardRgbService` already uses for its verify read-back; or drop it once the firmware
  question is settled. Keep it while Phase 2 is unresolved — it is the single most informative line
  in the log today.

#### Tests

- [ ] `SmartInfiniteMacroService` has **no unit tests** — 323 lines covering all ack validation, the
      Device Info refusal paths, the chunked write and the verify. It takes a concrete
      `HidSharp.HidDevice` and opens a concrete `HidStream`, neither substitutable, so this is
      structural rather than an oversight; the plan raised it as Open question 3 and the Test strategy
      records it. It matters more now than when written: **S1 is exactly the class of bug a stream
      seam would have caught**, and the same gap already produced a blocker on the Raster branch.
      Introducing the seam would retro-fit to `RasterKeyboardMacroService` too.
      — `tests/ProoveGaming.Infrastructure.Tests/`
- [x] No test asserts the **all-zero read-back** verdict path, which is the branch's headline finding
      and the discriminator Option B in B1 would depend on. The classification logic
      (`SmartInfiniteMacroService.cs:254-265`) is pure and could be extracted and tested without
      hardware. — `tests/ProoveGaming.Infrastructure.Tests/SmartInfiniteProtocolTests.cs`

#### Nits

- ~~`RgbKeyboardArtCatalog.SmartInfinite65 => Map["Cyrex"]`~~ **(fixed)** — `src/ProoveGaming.App/Models/RgbKeyboardArt.cs:90`
  — a hard indexer in a catalog whose only other accessor (`For`) is null-tolerant, reached on every
  Cyrex/Aress device open via `KeyOverlayArt`. Renaming the map key throws `KeyNotFoundException`
  during binding evaluation rather than degrading to the abstract canvas. Low risk: the literal and
  the map entry are twelve lines apart, and `Aress_BorrowsArtworkOnlyForTheOverlay` would fail loudly.
- ~~`catch (Exception ex)` at `SmartInfiniteMacroService.cs:71`~~ **(fixed)** — also swallows `OperationCanceledException`.
  No cancellation flows here today, so it is latent.
- ~~`md2html.py` — a Python tool committed under `docs/plans/`~~ **(fixed — added to the change list below)** — in a .NET repo, linked from
  `macro-investigation-result.md` but not from the plan. Defensible (it keeps the HTML regenerable
  rather than drifting) but worth a line in the plan's change list so it is not mistaken for stray
  scaffolding.

#### What's done well

- **The investigation stopped guessing and went to the source.** Three rounds of single-byte changes
  were converging on nothing; finding the vendor workbook settled the type byte, confirmed `0x2D`,
  confirmed the buffer layout and confirmed the `0x81` / `0x01` attribute bytes in one pass. The
  translation into `docs/knowledge-base/` with the command-numbering caveat at the top is the durable
  output.
- **The read-back diagnostic is the right instrument.** It converts "the key does nothing" —
  unfalsifiable from the host — into a precise, classified statement about what the device holds, and
  running it across a session boundary was the detail that made it conclusive rather than ambiguous.
  It is what turned an open-ended hunt into a documented negative.
- **Two real defects were found and fixed with verified regression tests.** The `_keyLayoutGeneration`
  guard dropping bookkeeping after a successful device write is a genuinely subtle bug — device and
  database disagreeing, worse on Cyrex/Aress purely because their key write is ~420 ms — and guarding
  on device identity is the right abstraction, not a patch. Both fixes were confirmed to fail without
  them.
- **The per-device macro migration is careful:** sentinel-based idempotency with no version marker,
  transaction-wrapped, first owner keeps the original id so existing references stay valid, extra
  owners get copies with device-scoped repointing across all three referencing tables. The merge
  resolution against #70's competing "fresh start" implementation is documented with its reason.
- **Root causes are recorded, not just fixes.** The inverted `KnownTypes` comment is named as the
  cause of two wasted rounds, in both the code and the plan. That is what stops it recurring.

#### Verdict

Ship: no — one blocker, and it is a product-honesty issue rather than a code defect: two boards
advertise a Macros feature the firmware provably discards, with no user-visible failure. The
engineering on this branch is sound and the Phase 2 negative is a legitimate, well-evidenced outcome
the plan explicitly allowed for; what is missing is the plan's own named remedy for that outcome.
First: (1) decide B1 — Option A hides the page, Option B makes the failure visible and keeps it
testable; (2) S1, a one-`catch` fix so the diagnostic cannot fail a successful upload; (3) S2 and S3,
delete the stale protocol claims before they cost someone else a hardware round.

### Round 2 — external review (PR #74, boikoserhii84)

Two comments on the PR. Recorded here so the branch's review history stays in one place.

#### First comment (2026-07-30, reviewed `034fc0b`)

- **M1** — identical to Round 1's **B1**: `SupportsMacros: true` advertises a feature the firmware
  discards, same two files and lines, same Option A/B. Independently found by both passes.
  **Declined by the same decision as B1** — the Macros section stays visible. The reviewer rates it
  "the worst possible failure mode" and predicts users will re-record macros believing they got it
  wrong, and separately notes Aress was never tested for this and is assumed to behave like Cyrex.
  Approved anyway, so it is a recorded shipping risk rather than a merge blocker.
- **M2, part 1** — [x] `SmartInfiniteProtocol.cs` documented `0x2D` as "acks the write and **stores**
  the library". Round 4 disproved that. Valid, missed by Round 1, fixed in `9fd02db`. The comment was
  written before round 4 and never corrected; the reviewer's point about compounding stale comments
  is the same failure this branch already paid for twice.
- **M2, part 2** — [x] `SmartInfiniteMacroService` still calling `TypeMacro` `0x50`. Already fixed
  independently as Round 1 **S2** in `b2cc4c9`, which landed after the reviewed revision.

#### Second comment (2026-07-31)

- [x] **m1. `_assignments` goes stale across a resync, so a rewritten key reads as unbound** —
  `DeviceDetailsViewModel.KeyAssignments.cs` (`ResyncKeyMacroBindingsAsync`, with
  `RefreshFromDeviceAsync` and `SyncAssignmentTargetSelection`).
  **Verified in full.** `RefreshFromDeviceAsync` probes the matrix first and resyncs second, and the
  resync loop wrote `SetKeyMappingAsync` without updating `_assignments`. Smart Infinite matches the
  selection on the whole `{type, code1, code2}` triple, so a macro that moved library slot — or whose
  repeat count changed, flipping the type `0x60` → `0x61` — matched no target and the key showed as
  unbound on the Macros page while the device held the binding. Self-heals only on the next device
  open. Raster is immune: its branch excludes macro rows outright.
  The reviewer's framing is right and worth keeping: `ResyncKeyMacroBindingsAsync` did not change,
  but it was **unreachable** for this family until Round 1's fix removed the early return in
  `ApplyAssignmentTargetAsync` and `key_macro_bindings` started holding rows for these boards. The
  fix opened the path.
  **Fixed** with the reviewer's Option A — update the mirror after each successful write and resync
  the selection once after the loop — rather than Option B, which would cost an extra HID exchange
  after every write. Regression test `Resync_AfterAMacroChangesSlot_LeavesTheKeyShowingAsBound`,
  verified to fail without the fix with the reported symptom.
  **Also taken while in that method:** it had no device guard despite running fire-and-forget from
  `SetDevice` with an await per key, so switching boards mid-resync would write one keyboard's
  bindings to another. Now captures the device up front and checks identity, the same guard
  `ApplyAssignmentTargetAsync` uses. The loop also ignored the `SetKeyMappingAsync` result; the
  mirror is only updated when the write actually succeeded.

App 316, Infrastructure 355, solution builds clean.
