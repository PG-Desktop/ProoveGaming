# Rampage key assignments — implementation plan

Source material: [`docs/knowledge-base/rampage-keyboard-protocol/`](../../knowledge-base/rampage-keyboard-protocol/README.md)
(vendor "Smart Infinite" protocol drop, landed by PR #67).
Sibling plans: [`raster-key-assignments`](../raster-key-assignments/), [`tracer-key-assignments`](../tracer-key-assignments/).

## Implementation status

**Complete, pending hardware QA.** Every layer in the change list is implemented on
`features/Rampage-newkeysdoc`: protocol (`RampageProtocol`, `KeyMatrixRecord`,
`RampageKeyboardKeyMappingService`, factory branch, framing consolidated out of the three RGB
services), catalogs (`RampageKeyCatalog`, `RampageVisualLayout`), the additive `ReadKeyMatrix` gRPC
contract and its DeviceHost handler and App client, and the ViewModel / overlay wiring.
48 new tests; full suite **430 green**; `dotnet build ./src/ProveGaming.slnx` clean.

Open question 0 was resolved by the user as **option (c)**: implement fresh on this branch and
leave PR #45 untouched. No commit, branch or file belonging to `features/PG-331-keyassignments`
was modified.

**Revised after the PR #67 review (boikoserhii84).** That review pointed at the two vendor
screenshots nobody had opened — image1 (device enumeration) and image5 (a complete working remap).
Reading them directly overturned three things in this plan:

1. **The checksum is a plain additive sum, not CRC16/Modbus.** Confirmed against all six packets of
   image6 and the write packet in image5. This also explains the anomaly this plan previously
   recorded as unexplainable: the README's `…38 4a 00…` is the correct sum for command `0x12`,
   copy-pasted onto a `0x16` packet. **A wrong checksum is now a second candidate cause for PR #45's
   writes never taking effect**, alongside the partial write.
2. **Transfers are 6 packets / 336 bytes, not 7 / 384.** Length derives from `key_count × 3` = 288,
   rounded up to whole 56-byte packets. Both captures agree. The docx's "send a full 384 bytes"
   contradicts the vendor's own driver; the capture wins, and the conflict is now documented rather
   than explained away.
3. **Interface selection uses HID usage `0x20` / usage page `0xFF87`**, the vendor's own rule. The
   device exposes *three* vendor-defined interfaces, so the previous report-length heuristic was
   picking by enumeration order.

**Behaviour change to flag:** the three RGB services (Rampage, Aress, Cyrex) share this framing, so
they now emit the corrected checksum. Their wire bytes changed. They should get an RGB smoke test
before merge — see Risks.

**Blocked on device firmware, not on our code (settled 2026-07-29).** Two measurements close this:

1. **Device Info (`0x12`) index 6 — actual key count — reads `0x00`.** Every other field in the
   block decodes correctly. The device declares it has no remappable keys.
2. **`0x15 Set General Info` persists and reads back byte-for-byte**, while `0x18` (key matrix) and
   `0x19` (per-key lighting) are acknowledged `0x55` and discarded.

(2) is the control this investigation lacked for days: a successful write round trip on the same
handle, same session style, same checksum proves our transport, checksum, framing, interface
selection and read path are all correct. Nothing in this repository is at fault. The straightforward
reading is that key remapping is not enabled in this firmware build.

Everything up to the write is verified working: interface selection, checksum, framing, the read
(cross-checked by the FN canary at slot 90), the catalog, the gRPC contract and the UI. Eight write
variants were tried; see
[`hardware-findings.md`](../../knowledge-base/rampage-keyboard-protocol/hardware-findings.md).

The full-384-byte hypothesis that motivated re-attempting this feature is **disproved**: 336, 392
and 3-byte-aligned transfers all behave identically. So is PR #45's staging-buffer model, which
rested on two observations their misaligned read path cannot support.

The commit step is not in any material supplied so far. The request that unblocks it is written up
in [`vendor-request.md`](../../knowledge-base/rampage-keyboard-protocol/vendor-request.md).
Until it is answered, the branch should not ship the Key assignments section as functional for
Rampage — the write path is correct as far as it can be verified, and the UI correctly refuses to
report a remap it cannot confirm.

## Goal

Let a user remap any physical key on the **Rampage** keyboard to any other standard HID keyboard
key from the App's Key assignments section, the same way they already can on Raster and T-Racer.
Rampage speaks a different vendor protocol from those two (Smart Infinite, report ID `0x06`, whole
384-byte key-matrix block transfers) and its firmware exposes **no separate user-override layer** —
the base key matrix *is* the live state. That difference means the existing scancode-keyed probe
contract cannot be reused as-is, so this plan adds a slot-keyed read path alongside it rather than
bending the existing one.

## Scope

**In scope**

- `RampageKeyboardKeyMappingService` — Smart Infinite key-matrix read (`0x16`) and read-modify-write
  (`0x17`) over the existing 64/64 vendor HID interface.
- A shared `RampageProtocol` frame helper, extracted from the duplicated framing currently inline in
  `RampageKeyboardRgbService`.
- `RampageKeyCatalog` + `RampageVisualLayout` in `ProoveGaming.Common`, derived from the vendor's
  factory `key_matrix_tab` (all 96 slots, 86 of them remappable).
- A new slot-keyed `ReadKeyMatrix` gRPC RPC and its App-side client method.
- App wiring: Rampage recognised by `HasInteractiveKeyboardOverlay`, correct slot resolution,
  correct "currently assigned" highlight including modifier-only keys.
- Making the overlay canvas size device-driven (Rampage is narrower than Raster/T-Racer).
- Correcting the two wire-format errors in the knowledge-base README (see [Risks](#risks)).
- Glossary entries in `docs/CONTEXT.md` for any new term introduced.

**Out of scope**

- Non-keyboard assignment targets: consumer/media (`TYPE=0x20`), mouse (`0x40`), macro (`0x50`),
  custom function (`0x60`), advanced keys DKS/Toggle/SOCD/Rapid-Trigger (`0x80`–`0x84`). The
  protocol layer keeps `type`/`code1`/`code2` general so these are additive later, but no UI.
- The FN-layer table (`0x18`), the 2 KB macro table, and profile switching (`PROFILE_NUM = 3`).
  This plan writes profile 0 only.
- Changing Raster / T-Racer behaviour. Their existing probe path stays byte-for-byte identical.
- Persisting assignments to SQLite. As with Raster, the device is the truth source.

## Options considered

### Option A — reuse `IKeyboardKeyMappingService` as it stands

Implement Rampage behind the existing `ProbeDefaultMatrixAsync` (scancode → slot) +
`ProbeUserOverridesAsync` (slot → code2) contract, no interface change.

*Rejected.* Three concrete breakages:

1. **No override layer.** Raster has `GetDefault0` (`0x16`, factory) and `GetUserKey0` (`0x1A`,
   overrides) as two distinct tables. Rampage has one table. Reading `0x16` after the user remaps
   E → A returns `A` at slot 35, so `ProbeDefaultMatrixAsync` would report `scancode A → slot 35`
   and lose the `E → slot 35` entry entirely. `DeviceDetailsViewModel.ResolveMatrixSlot` would then
   resolve the wrong slot on the next click — remapping a key once would make it unaddressable.
2. **Modifier keys are invisible.** On Rampage, LShift is `{0x10, 0x02, 0x00}` — the identity lives
   in `code1`, and `code2` is `0x00`. Both probe methods filter on `code2 != 0`, so all seven
   modifier keys would silently drop out of the map.
3. **`code2`-only overrides are lossy.** `ProbeUserOverridesAsync` returns a single byte per slot,
   which cannot express "this slot is now Right Shift".

### Option B — add a slot-keyed full-record read alongside the existing probe *(recommended)*

Add one member to `IKeyboardKeyMappingService` that returns the whole record per slot:

```csharp
Task<IReadOnlyDictionary<byte, KeyMatrixRecord>?> ReadKeyMatrixAsync(int maxSlot = 128);
```

Rampage implements it natively from its 384-byte block read. Raster and T-Racer implement it from
their existing per-slot loop (they already read `type`/`code1`/`code2` and discard two of the three
bytes) and keep their old methods untouched. Rampage never calls the old scancode probe — its slot
numbers are hardware-fixed and fully known from the vendor's `key_matrix_tab`, so the catalog
carries the real index and no probe is needed to resolve slots at all.

*Trade-off, stated honestly:* the codebase carries two read paths for a while — the scancode-keyed
probe (Raster, T-Racer) and the slot-keyed read (Rampage). That is deliberate: converting Raster
and T-Racer to the slot-keyed path means re-testing two shipped devices on real hardware, which is
not what this change is for. `ReadKeyMatrixAsync` is the better-shaped contract, so the eventual
convergence is a later, separate refactor.

### Option C — new parallel interface `IKeyMatrixBlockService`

Leave `IKeyboardKeyMappingService` alone entirely and give Rampage its own interface, factory and
gRPC surface.

*Rejected.* `SetKeyMappingAsync(matrixIndex, type, code1, code2)` already fits Rampage exactly, and
`DeviceKeyMappingServiceFactory` / `DeviceService` / `UsbHidDeviceService` would all need a second
duplicated dispatch path. That is more duplication than the one extra interface member costs.

## Recommended approach

**Option B.** Concretely:

### Wire protocol

The framing is already proven in `RampageKeyboardRgbService.SendCommandAsync` and is **not** what
the knowledge-base README §4.1 describes. Real layout (confirmed by the shipped RGB service *and* by
the README's own hex samples):

```
byte 0      0x06        magic / report id
byte 1      cmd         0x10 start, 0x11 end, 0x16 read matrix, 0x17 write matrix
byte 2..3   addr        little-endian byte offset into the table
byte 4      len         payload length in this packet (0x38 = 56 max)
byte 5..6   crc         CRC16/Modbus over report[1 .. 3+len]
byte 7      0
byte 8..    payload
```

The README's sample `06 16 00 00 38 4a 00 00` decodes cleanly as `addr=0x0000, len=56, crc=0x004A` —
which is why §4.1's "byte 2 = sequence, byte 3 = sub-index, bytes 4–7 reserved" is wrong and gets
corrected as part of this work.

**Command bytes come from PR #45's hardware testing, not from the new README, which is wrong about
two of them.** PR #45 (`features/PG-331-keyassignments`) drove a real Rampage and confirmed:

| Cmd | Verified meaning | New README claims | Verdict |
|-----|------------------|-------------------|---------|
| `0x10` / `0x11` | Start / End communication | (not listed) | — |
| `0x16` | Read **Default** Key Info (factory table) | read base matrix | agrees |
| `0x17` | **Read** Key Info (current table) | **write** base matrix | **README wrong** |
| `0x18` | **Set** Key Info (the write, acks `0x55`) | read/write FN-combo matrix | **README wrong** |
| `0x30` / `0x31` | Read / Set current profile | (not listed) | — |
| `0x32`/`0x33`/`0x34` | FN-combo read-default / read / set | — | — |
| `0x1D` | Sync Lighting | — | — |
| `0x20` | **Factory reset** — wipes user config, needs vendor recovery | "Reset" | **never send** |

Had this plan been implemented as originally written, writes would have gone to `0x17` — a *read*
command — and silently done nothing.

- **Read** the 384-byte matrix as 7 `0x16` (factory) or `0x17` (current) requests at
  `addr = 0, 56, 112, 168, 224, 280, 336` (last `len = 48`), wrapped in `0x10` … `0x11`. Each key
  occupies `table[3*slot .. 3*slot+2]`.
- **Write** as read-modify-write: read all 384 bytes, patch the 3 bytes at `3*matrixIndex`, push all
  7 chunks back with **`0x18`**, then `0x11`.

### Why the full-table write is the whole point of this change

PR #45 implemented the write as a **3-byte** payload at `addr = slot*3` and hit a hard wall
(`docs/rampage-key-mapping-result.md`): the firmware acked, `Read Key Info` echoed the new value,
but the physical key kept emitting its factory scancode until the user unplugged and replugged the
keyboard. PR #45 concluded a "commit staging → compiled profile" command must exist and is
undocumented, and shipped a "unplug and replug to apply" banner as a workaround.

The vendor drop in PR #67 does not contain such a commit command — but it does contain, twice, the
instruction PR #45 did not have:

> "When you write a command, you must send **the full length** shown above — the keyboard will only
> **store** the payload if the transfer is complete."
>
> "Note: you must send a **full 384-byte** payload for the keyboard to store the assignment table."

That is a direct description of PR #45's symptom. A partial write reaches the volatile staging
buffer (hence the successful `0x17` read-back, and hence `Set General Info` zeroing it) but never
triggers the store into the compiled profile the scan matrix reads. **The hypothesis this plan
tests is that the "missing commit command" was never a command at all — it was the full 384-byte
transfer length.** This is the single reason the feature is worth re-attempting now, and QA step 4
is the experiment that settles it.

- **Verify** with a re-read via `0x17` after the write and compare the 3 bytes. Storage-side
  read-back was already proven to work by PR #45, so a `false` here means the transfer itself
  failed, not that the remap failed to take effect physically — only a human keypress can confirm
  the latter.

### Slot resolution

Rampage matrix indices are fixed hardware positions, taken verbatim from `keyboard_user_flash.c`
(slots 0–95, six rows of sixteen). The catalog is authoritative; there is no probe-and-correct step
like Raster's.

This is the second significant win from the new docs. PR #45 had no factory table, so it discovered
slots at runtime with `DiscoverKeyIndexAsync`, iterating 128 slots and matching on content — a loop
that had to carry an **off-by-one correction** because every SmartInfinite command (including
`Start`) generates a reply, so the *n*th `Read()` returns the *(n-1)*th request's answer. PR #45's
own notes flag that this probe reports different slots for the same target under rapid clicks.
`keyboard_user_flash.c` makes the entire probe unnecessary: the slots are compile-time constants.
Deleting it removes the lag correction, the 128-iteration round trip on every section open, and the
flakiness — this plan ships **no** runtime slot probe for Rampage.

### Operational lessons carried over from PR #45

These were paid for with hardware debugging and must not be re-discovered:

1. **Never drain `Start`'s ack.** Calling `Read()` with a timeout before the first real read request
   leaves the HidSharp Windows stream in a state where every subsequent `Write()` fails with
   `ERROR_INVALID_FUNCTION`. Accept the reply lag; do not flush the pipe.
2. **Rampage exposes two 64/64 vendor interfaces.** `mi_01&col05` is writable; `mi_01&col07` is
   input-only and rejects every write with `ERROR_INVALID_FUNCTION`. Select by sorting `DevicePath`
   and taking the lowest `col##` — plain `FirstOrDefault` is unstable across replugs. Note this also
   means the *existing* `DeviceRgbServiceFactory.CreateRampageKeyboardRgbService`, which uses an
   unsorted `FirstOrDefault` over 64/64 interfaces, is latently racy; out of scope here but worth a
   follow-up.
3. **Retry open+write.** Windows can hold the previous HID handle in "closing" state for tens of ms,
   so the first `Write()` after a fresh `TryOpen` can fail with Win32 error 1. Retry up to 4 times
   with a 100 ms gap.
4. **`0x20` is a factory reset**, not a session reset. It wipes user config and leaves the board in
   rainbow/pairing mode needing vendor recovery. It must not appear in this codebase except as a
   commented-out constant with a `DO NOT USE` warning.
5. **`0x31` profile round-trip (`0→1→0`) freezes the firmware** and requires a physical replug. Do
   not use profile switching as a reload trigger. Of the 96 slots: 8 are empty (`60, 65, 77, 83, 84, 85, 87, 88`), slot `79` is a blank
custom-function slot and slot `90` is FN — leaving **86 remappable keys**, which is what the catalog
and the target list both contain. FN is excluded for the same reason as on Raster: remapping it
breaks every FN+X hardware shortcut.

### Modifier keys

`PhysicalKey` gains an optional trailing `byte ModifierMask = 0` so LShift/RShift/LCtrl/RCtrl/
LAlt/RAlt/LWin carry their bitmask (`0x02/0x20/0x01/0x10/0x04/0x40/0x08`) with `HidUsageCode = 0`.
Positional record with a default → all existing Raster and T-Racer call sites compile unchanged.
Target matching in the VM must key on the **`(TypeByte, Bitmask, ScanCode)` triple**, not on
`ScanCode` alone, otherwise every modifier key collides on scancode `0x00`.

## File-level change list

### `src/ProoveGaming.Infrastructure`

| File | Action | Intent |
|------|--------|--------|
| `Classes/RampageProtocol.cs` | create | Pure static Smart Infinite framing: `BuildCommand(cmd, addr, payload)`, `Crc16Modbus`, `ChunkTable(...)` yielding the 7 `(addr, len)` pairs, and the `KEY_MATRIX_TAB_SIZE = 384` / `MaxPayload = 56` constants. Fully testable without HID. |
| `Classes/KeyMatrixRecord.cs` | create | `readonly record struct KeyMatrixRecord(byte Type, byte Code1, byte Code2)` — the per-slot triple returned by the new read. |
| `Services/RampageKeyboardRgbService.cs` | edit | Delete the private `SendCommandAsync` / `Crc16Modbus` copies and call `RampageProtocol` instead. No behaviour change — same bytes on the wire. Required by the repo's "extract on the second copy" rule. |
| `Interfaces/IKeyboardKeyMappingService.cs` | edit | Add `ReadKeyMatrixAsync(int maxSlot = 128)` returning `slot → KeyMatrixRecord`, with XML docs stating it is the slot-keyed successor to the scancode probe and that Rampage implements only this one. |
| `Services/RampageKeyboardKeyMappingService.cs` | create | The device service, **adapted from PR #45's version of this file** rather than written fresh — it carries hardware-earned handling this plan must not lose (open+write retry on `ERROR_INVALID_FUNCTION`, the no-drain rule, the `DO NOT USE` comment on `0x20`). Changes vs PR #45: the write becomes a full-384-byte read-modify-write via `0x18` instead of a 3-byte partial write; `DiscoverKeyIndexAsync` and its off-by-one lag correction are **deleted** (the catalog supplies slots); adds `ReadKeyMatrixAsync` (7× `0x17`). The two legacy probe methods return `null` with a logged reason. |
| `Services/RasterKeyboardKeyMappingService.cs` | edit | Implement `ReadKeyMatrixAsync` by reusing the existing per-slot `0x16` loop, keeping all three bytes. Existing methods untouched. |
| `Services/TRacerKeyboardKeyMappingService.cs` | edit | Same as Raster. |
| `Services/DeviceKeyMappingServiceFactory.cs` | edit | Add the `"Rampage"` name branch plus a `SelectRampageVendorInterface` helper selecting the **64/64** report interface (Raster/T-Racer's `SelectVendorInterface` hardcodes 65/65 and will not match Rampage). |

### `src/ProoveGaming.GrpcDependencies`

| File | Action | Intent |
|------|--------|--------|
| `Protos/deviceservice.proto` | edit | Add `rpc ReadKeyMatrix (ReadKeyMatrixRequest) returns (ReadKeyMatrixResponse)`, message `KeyMatrixRecordProto { int32 type = 1; int32 code1 = 2; int32 code2 = 3; }`, and a `map<int32, KeyMatrixRecordProto> records` response field. Purely additive — existing `ProbeKeyMatrix` and `SetKeyMapping` keep their field numbers. |

### `src/ProoveGaming.DeviceHost`

| File | Action | Intent |
|------|--------|--------|
| `Services/DeviceService.cs` | edit | Implement the `ReadKeyMatrix` handler, mirroring `ProbeKeyMatrix`'s create-service / try-catch / `Success=false` shape. |

### `src/ProoveGaming.Common`

| File | Action | Intent |
|------|--------|--------|
| `Classes/RasterKeyCatalog.cs` | edit | Add the optional `byte ModifierMask = 0` parameter to the shared `PhysicalKey` record declared here. Nothing else in this file changes. |
| `Classes/RampageKeyCatalog.cs` | create | `IsRampage(vid, pid)` (VID `10473`, PID `12888`) and `IsRampage(name)`, plus the 86-entry catalog transcribed from the vendor `key_matrix_tab` with real matrix indices, labels, categories, HID codes and modifier masks. Doc comment cites `keyboard_user_flash.c` as the source. |
| `Classes/RampageVisualLayout.cs` | create | Unit-grid pixel bounds for the compact-TKL footprint (main block + 2-wide nav column + arrow cluster), reusing `RasterKeyBounds` and the `UnitSize = 34.0` convention. `CanvasWidth` ≈ `17.25 × UnitSize`, `CanvasHeight` = `6.5 × UnitSize`. |

### `src/ProoveGaming.App`

| File | Action | Intent |
|------|--------|--------|
| `Services/IUsbHidDeviceService.cs` | edit | Add `ReadKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)`. |
| `Services/UsbHidDeviceService.cs` | edit | Implement it over the new RPC, following the existing `NotifyError` / `RpcException` handling in `ProbeKeyMatrixAsync`. |
| `ViewModels/KeyAssignmentOptionViewModel.cs` | edit | Comment update only — `Bitmask` is now genuinely populated (it was hardcoded `0`), and `MatrixIndex` is catalog-final on Rampage rather than probe-corrected. |
| `ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` | edit | The bulk of the App work: recognise Rampage in `HasInteractiveKeyboardOverlay`; add `IsRampageDevice`; dispatch catalog + layout for it in `LoadKeyLayoutForDevice`; set `Bitmask = key.ModifierMask`; branch `RefreshFromDeviceAsync` to `ReadKeyMatrixAsync` for Rampage (skipping the scancode probe entirely) into a new `slot → KeyMatrixRecord` field; make `ResolveMatrixSlot` return the catalog index directly on Rampage; make `SyncAssignmentTargetSelection` and `ApplyAssignmentTargetAsync` match on the `(TypeByte, Bitmask, ScanCode)` triple. |
| `ViewModels/DeviceDetails/DeviceDetailsViewModel.cs` | edit | Expose `OverlayCanvasWidth` / `OverlayCanvasHeight` from the active layout and raise them alongside `HasInteractiveKeyboardOverlay` on device change. |
| `Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml` | edit | Bind `Canvas` `Width`/`Height` to the new VM properties instead of the hardcoded `665`/`221`, so Rampage's narrower board is not offset inside a Raster-sized canvas. Update the stale "81 physical-key buttons" comment. |

### `docs`

| File | Action | Intent |
|------|--------|--------|
| `knowledge-base/rampage-keyboard-protocol/README.md` | edit | Four corrections, all evidenced: (1) §4's command table has `0x17` and `0x18` wrong — hardware says `0x17` = Read Key Info, `0x18` = Set Key Info; (2) §4.1's packet layout is wrong — the real framing is `addr`/`len`/`CRC16-Modbus`; (3) §1's "6 × 64 = 384" math is wrong — it is 7 packets of ≤56 payload bytes; (4) add the verified commands the drop omits (`0x10`/`0x11` session, `0x30`/`0x31` profile, `0x32`–`0x34` FN-combo) and a prominent `DO NOT SEND` warning on `0x20`. Cite PR #45's investigation as the source. |
| `knowledge-base/rampage-keyboard-protocol/hardware-findings.md` | create | Port the durable findings of `docs/rampage-key-mapping-investigation.md` and `-result.md` from PR #45 into the knowledge base so they survive whether or not that PR merges: the read-lag/off-by-one behaviour, the two-vendor-interface split, the `ERROR_INVALID_FUNCTION` retry, and the staging-vs-compiled-profile diagnosis. Link it from the README. |
| `CONTEXT.md` | edit | Add one-line glossary entries for any term this change introduces (`key matrix table`, `matrix slot`, `Smart Infinite`, `CRC16/Modbus`) that is not already recorded. |
| `plans/rampage-key-assignments/rampage-key-assignments-plan.md` | create | This document. |

**Flags:** no new NuGet package, no TFM bump, no new project in `.slnx`, no `Directory.Build.props`
change, and no installer/uninstaller impact. The only cross-process contract change is the additive
`.proto` edit — App and DeviceHost must ship together, which they already do.

## Acceptance criteria

1. Given a wired Rampage (VID `10473`, PID `12888`) is connected, when the user opens Device
   details → Key assignments, then an interactive on-screen keyboard renders with **86** clickable
   key overlays and no key is clipped outside the canvas bounds.
2. Given the Key assignments section is open, when the user clicks the `E` key overlay, then the
   header reads `E button` and the target list highlights `E` (its factory assignment).
3. Given `E` is selected, when the user picks `A` from the target list, then within 2 seconds the
   physical `E` key on the hardware types `a`, and the highlighted target row is `A`.
4. Given `E` has been remapped to `A`, when the user clicks another key and clicks `E` again, then
   the target list still highlights `A` — i.e. slot resolution did not drift after the write
   (this is the specific failure mode Option A would have introduced).
5. Given `E` has been remapped to `A`, when the App is closed and reopened and the device re-read,
   then the target list for `E` shows `A` — read-back reflects real firmware state, not cached
   intent.
6. Given the user selects the physical `Left Shift` key, then the target list highlights
   `SHIFT (L)` and not any other modifier — modifier keys are distinguished by bitmask, not by
   their shared `0x00` scancode.
7. Given a Rampage key write, when the read-back verification shows the slot did not change, then
   `SetKeyMappingAsync` returns `false` and the UI highlight does not move to the requested target.
8. Given a matrix read returns fewer than 384 bytes or times out, then no `0x17` write is issued at
   all and the operation returns `false` — a short read must never be padded and written back.
9. Given a Raster or T-Racer keyboard is connected, when the user remaps a key, then behaviour is
   unchanged from before this plan (same probe path, same writes).
9a. Given a Rampage, Aress or Cyrex keyboard, when the user changes an RGB effect, then the
    lighting still responds as before — the shared checksum fix did not regress RGB.
10. Given the Key assignments section is open on any keyboard, when the user clicks through 20 keys
    and targets in quick succession, then the UI never freezes and no operation runs on the
    dispatcher thread (all HID work stays behind the gRPC call).
11. `dotnet build ./src/ProveGaming.slnx` and `dotnet test ./src/ProveGaming.slnx` both pass.

## Test strategy

This is an `add-*` change; the tests below are required coverage, not optional.

### `tests/ProoveGaming.Infrastructure.Tests`

Follow the pattern already established by `TaipanFrameTests.cs` and `X43KeyMatrixTests.cs` — pure
static encoders tested without any HID device.

**`RampageProtocolTests.cs`** (new)
- `BuildCommand` produces the documented header: magic `0x06` at byte 0, cmd at 1, little-endian
  addr at 2–3, len at 4, CRC at 5–6, payload from byte 8.
- CRC16/Modbus over a known vector matches the byte-identical output of the old inline
  implementation in `RampageKeyboardRgbService` — this is the characterization test that proves the
  extraction in step 3 of the change list did not alter the RGB wire bytes.
- Golden vector: the README's own `06 16 00 00 38 4a 00 00` read request round-trips exactly.
- `ChunkTable(384)` yields exactly 7 chunks with offsets `0,56,112,168,224,280,336` and lengths
  `56,56,56,56,56,56,48`, summing to 384.
- Payload longer than 56 bytes is rejected rather than silently truncated.

**`RampageKeyMatrixTests.cs`** (new)
- Parsing a synthetic 384-byte table yields the expected `slot → KeyMatrixRecord` map, including
  slot 0 = `{0x10, 0x00, 0x29}` (Esc) and slot 64 = `{0x10, 0x02, 0x00}` (LShift).
- Patching slot *n* rewrites exactly bytes `3n..3n+2` and leaves all other 381 bytes byte-identical
  — the guard against clobbering the table on a read-modify-write.
- A 383-byte (short) table is rejected by the patch helper.

**`RampageKeyCatalogTests.cs`** (new)
- The catalog has 86 entries; matrix indices are unique and all within `0..95`.
- The catalog excludes exactly the empty slots `{60,65,77,83,84,85,87,88}`, the blank custom slot
  `79`, and FN at `90`.
- Every catalog entry matches the vendor factory table: for each entry, `(type, ModifierMask,
  HidUsageCode)` equals the triple at `3 × MatrixIndex` in a copy of `key_matrix_tab` embedded in
  the test. This is the test that actually catches a transcription typo in an 86-row table.
- Every catalog `DevKeyName` has a bounds entry in `RampageVisualLayout.Layout`, and no layout key
  is orphaned.
- No two entries share the same `(TypeByte, ModifierMask, HidUsageCode)` triple — the precondition
  that makes the target-list highlight unambiguous.

### `tests/ProoveGaming.App.Tests`

**`DeviceDetailsRampageKeyAssignmentTests.cs`** (new), mirroring the fake-service style of the
existing `DeviceDetailsMacroAssignmentTests.cs`:
- `LoadKeyLayoutForDevice` on a Rampage device populates 86 physical keys and 86 targets, each with
  a non-zero canvas size.
- Selecting a physical key and applying a target calls `SetKeyMappingAsync` with the **catalog**
  matrix index (not a probed one) and the target's exact `(type, bitmask, scancode)`.
- After a successful apply, re-selecting the same physical key still highlights the new target
  (acceptance criterion 4).
- Selecting `Left Shift` highlights exactly one target row (acceptance criterion 6).
- A failed `SetKeyMappingAsync` leaves the previous highlight in place (acceptance criterion 7).
- Loading a Raster device still produces the Raster catalog and still calls the scancode probe —
  the regression guard for acceptance criterion 9.

### Deliberately not automated

- **Real HID traffic.** No Rampage hardware in CI, and `HidSharp.HidDevice` is a sealed
  vendor type that is not worth wrapping for this change. The framing and table maths are covered
  by pure tests; the device round-trip is covered by the manual QA checklist below.
- **Whether `0x17` is genuinely the write command.** Unprovable without hardware — that is exactly
  why the service verifies by read-back at runtime, and why QA step 4 is the gate on this plan.
- **Avalonia UI-thread affinity and overlay rendering.** Canvas layout and hit-testing need a real
  visual tree; verified by QA steps 2 and 8.

## QA (manual test steps)

1. Install and launch the Proove Gaming app with a **Rampage** keyboard plugged in over USB. Open
   the Rampage device card, then open the **Key assignments** section.
   *Expected:* a picture of the keyboard appears with every key clickable, and a list of possible
   replacement keys on the left. Nothing is cut off at the edges of the keyboard picture.
2. Click the **E** key on the on-screen keyboard.
   *Expected:* the title above the list reads "E button", and **E** is the highlighted row in the
   list.
3. In the list, choose **A**.
   *Expected:* the **A** row becomes highlighted within about a second.
4. **The critical step.** Open Notepad (or any text field) and press the physical **E** key on the
   keyboard. Do **not** unplug the keyboard first.
   *Expected:* the letter **a** is typed.
   *(If **e** is typed instead: stop, do not continue the checklist, and report it as "the
   full-table write did not take effect physically". A previous attempt at this feature failed in
   exactly this way, so this one step decides whether the feature works at all. Please also note
   whether unplugging and replugging the keyboard then makes **a** appear — that detail is what the
   developers need.)*
5. Go back to the app, click a different key (for example **G**), then click **E** again.
   *Expected:* the list still shows **A** highlighted for E.
6. Fully close the app (including from the system tray) and reopen it. Open the Rampage device →
   Key assignments → click **E**.
   *Expected:* **A** is still highlighted, and pressing physical **E** still types **a**.
7. Click the **Left Shift** key on the on-screen keyboard.
   *Expected:* exactly one row is highlighted in the list and it is the left Shift row — not right
   Shift, not Ctrl, not a blank row.
8. Click rapidly through 15–20 different keys on the on-screen keyboard and pick a different target
   for several of them.
   *Expected:* the app stays responsive throughout — no freeze, no spinning cursor, no window going
   white.
9. Unplug the Rampage keyboard while the Key assignments section is open, then plug it back in.
   *Expected:* the app does not crash or hang; after reconnecting, the section works again.
10. Connect a **Raster** or **T-Racer** keyboard instead and remap one key on it.
    *Expected:* it behaves exactly as it did before this change — this confirms nothing regressed on
    the already-shipped keyboards.
11. **Lighting check — please do this even though it sounds unrelated.** On the Rampage, and then
    on an **Aress** and a **Cyrex** keyboard if available, open RGB settings and switch between a
    few effects (Static, Breathing, Wave), then change brightness.
    *Expected:* the lighting responds normally on all three. These keyboards share low-level code
    with the key-assignment feature, so a change made for remapping could in principle affect their
    lighting — this step is what catches it.

## Review focus

- `gRPC contract` — the `.proto` edit must be purely additive; no reused or renumbered field tags,
  and `ProbeKeyMatrix` / `SetKeyMapping` must be untouched.
- `async/await` — the new service is HID I/O end to end: `ReadAsync`/`WriteAsync` throughout, a
  `CancellationToken` flowed where the surrounding code has one, and no `.Result` / `.Wait()`. Note
  the existing `RampageKeyboardRgbService` uses `Thread.Sleep` between packets; the new service must
  not copy that pattern.
- `resource disposal` — every `TryOpen`'d `HidStream` is inside a `using`, including on the
  early-return paths where a read fails partway through the 7-packet sequence.
- `null-safety` — `ReadKeyMatrixAsync` returns `null` on failure and every caller must treat that as
  "do not write", not as "empty table".

## Performance & quality

- **Hot paths:** none introduced. Key assignment is user-initiated and low-frequency (one read on
  section open, one read-modify-write per user click). The 384-byte table is small enough that no
  buffer pooling is warranted; a plain `byte[384]` per operation is correct and reviewable.
- **UI thread:** all HID work happens in DeviceHost, reached over gRPC — the App's dispatcher only
  awaits the RPC. `RefreshFromDeviceAsync` is already the fire-and-forget entry point called from
  `SetDevice`, and this plan keeps it that way; the new read must not be awaited synchronously
  during device selection.
- **Shared state:** `HidDeviceProvider` and `DeviceInfoProvider` are DeviceHost singletons.
  `DeviceKeyMappingServiceFactory.CreateForDevice` already constructs a fresh service per call and
  holds no cache — the Rampage branch keeps that, so there is no new cross-thread state to
  coordinate. The read-modify-write sequence is not, however, atomic against a second concurrent
  write to the same device; see [Risks](#risks).
- **Allocations:** `RampageProtocol.BuildCommand` allocates one 64-byte report per packet, i.e. 7
  per table transfer. Negligible at this call rate; not worth a pooled buffer.
- **Resource lifetime:** the service owns no `IDisposable` beyond the per-operation `HidStream`,
  and adds no event subscriptions, so there is nothing to unsubscribe.

## Risks

1. **The full-384-byte-write hypothesis may not hold.** This is the headline risk: the entire reason
   to re-attempt the feature is the theory that PR #45's remap never took effect physically because
   it wrote 3 bytes instead of the full table. The evidence is strong (the vendor doc says it twice,
   and it explains PR #45's exact symptom) but it is still a hypothesis until a human presses the
   key. *Mitigation:* QA step 4 is a hard gate. If the physical key still emits the factory code
   after a full-table write, the feature is blocked on the vendor again and the correct outcome is
   to stop and re-open PR #45's vendor request — **not** to ship PR #45's "unplug and replug"
   banner as a workaround without the team explicitly choosing that. Escalate rather than paper
   over it.
1a. **The RGB services' wire bytes changed.** Fixing the checksum touched shared framing, so
   Rampage / Aress / Cyrex RGB now send a different (correct) value in bytes 5–6. Those services
   demonstrably worked with the wrong CRC, which strongly suggests the firmware ignores the field —
   but "worked with a wrong value" is not proof that it will work with a right one. *Mitigation:*
   QA must set an RGB effect on each of the three keyboards before merge. This is cheap to check
   and cheap to revert (one function in `RampageProtocol`).

2. **Read-modify-write can clobber the whole keymap.** A short or corrupted read followed by a
   384-byte write would overwrite every key on the board. *Mitigation:* the write path aborts unless
   the read returned all 384 bytes; covered by acceptance criterion 8 and a unit test.
3. **The knowledge-base README's §4.1 framing is wrong** and contradicts the shipped, working RGB
   service. Anyone implementing straight from the doc would build a broken frame. *Mitigation:* the
   doc correction is in the change list, not left as a follow-up.
4. **Concurrent writes are not serialised.** Two rapid clicks could interleave read-modify-write
   cycles and lose the first write. *Mitigation:* each cycle re-reads immediately before patching,
   so the loser of a race is overwritten with a *valid* table rather than a corrupted one — the
   observable worst case is one dropped assignment, which the read-back verify reports as `false`.
   A per-device `SemaphoreSlim` would close it fully; deferred as out of proportion to the risk at
   human click rates, and called out here so the reviewer can overrule that judgement.
5. **The visual layout is derived, not measured.** Key positions come from the vendor matrix
   interpreted as a standard compact-TKL grid, per the user's decision. Cosmetic drift versus the
   real board is possible (this is the same starting position `TRacerVisualLayout` shipped from).
   Functionally harmless — matrix indices are independent of pixel positions.
6. **Two read paths coexist.** Stated in Option B. The risk is a future contributor extending the
   wrong one; mitigated by XML docs on `ReadKeyMatrixAsync` naming it the successor.

## Open questions

0. **Blocking decision — how does this land relative to PR #45?** PR #45
   (`features/PG-331-keyassignments`, still open, 37 files) already contains a working App/UI/gRPC/
   persistence layer for Rampage key assignments: `RampageKeyCatalog`, `RampageVisualLayout`,
   `RampageFnCombos`, `SystemActionCatalog`, the proto RPCs, the DeviceHost handlers, the
   ViewModel and the section view. This plan's App-layer work overlaps it almost entirely. Three
   options: **(a)** rebase PR #45 onto current `main` and land this plan's protocol corrections on
   top of it — least duplicated work, and the option this plan recommends; **(b)** cherry-pick just
   PR #45's `Common/` catalogs and App layer onto this branch and abandon the PR; **(c)** implement
   the App layer fresh here and close PR #45. Picking (a) or (b) changes this plan's File-level
   change list substantially — several "create" entries become "adopt and amend". **This needs a
   human decision before the App-layer work starts.** The Infrastructure/protocol work is
   unaffected either way and can proceed immediately.
1. ~~Is the Rampage's vendor HID interface reliably the only 64/64 interface?~~ **Answered by PR
   #45:** no — there are two (`col05` writable, `col07` input-only). Handled by sorting `DevicePath`
   and taking the lowest `col##`.
2. Should the target list be visually grouped by the `Category` values the catalog already carries
   (Function Row, Alpha Row 1, …)? Raster and T-Racer populate `Category` but render one flat list.
   With 86 targets on Rampage the flat list is long. Not blocking — flat list matches the existing
   devices and is what this plan implements.
3. Is a "reset all keys to factory" action wanted? The full factory table is known, so it is one
   `0x17` write, but no equivalent exists for Raster/T-Racer today and it was not requested.

## Review findings

### Round 1 — Ship: yes (conditional on QA step 11)

Reviewed at `91401f6` (branch `features/Rampage-newkeysdoc`, post-merge of `origin/main`),
working tree clean. Review surface: full delta vs `origin/main` (31 files, +5057/−126), all
seven branch commits read, no staged/unstaged/untracked files outside the delta.

#### Plan conformance

Plan: `docs/plans/rampage-key-assignments/rampage-key-assignments-plan.md` (this file).

- **Implemented as planned:** Option B contract (`ReadKeyMatrixAsync` on
  `IKeyboardKeyMappingService`), `RampageProtocol` + `KeyMatrixRecord`, catalog-final slot
  resolution with no runtime probe, `RampageKeyCatalog` (86 entries) + `RampageVisualLayout`,
  additive proto RPC, factory branch, VM wiring with whole-record `(TypeByte, Bitmask, ScanCode)`
  matching, device-driven canvas size, knowledge-base corrections, and the full test strategy
  (protocol, catalog, VM suites — all green, 842 total).
- **Deviates from plan (superseded by the RM1 factory update of 2026-08-04, post-plan):** the plan
  text still describes a 336-byte write inside a `0x10`…`0x11` session (and, in older sections,
  CRC16 and `0x17`-as-write). Shipped behavior is a **bare `0x18` write of exactly 384 bytes
  (7 packets, 6 × 56 + 48), no session bracket** — confirmed on physical hardware (slot 33 Q → W:
  7/7 acked, read-back matched for the first time ever, physical key emitted `w` immediately,
  restore verified). The knowledge base records this; the plan's status section does not → S4.
- **Deviates from plan (Review focus):** the plan demanded `ReadAsync`/`WriteAsync` and "no
  `Thread.Sleep`" in the new service. `RampageKeyboardKeyMappingService` uses HidSharp
  **synchronous** I/O with `Thread.Sleep` pacing, wrapped in `Task.Run`. Acceptable in context:
  the reply-lag/address-matching logic is built on timeout-bounded sync reads (hardware-earned in
  PR #45), it runs on DeviceHost thread-pool threads, and no dispatcher is ever blocked. Recorded
  as a deviation, not a defect.
- **Deviates from plan (Risk 4 overruled — correctly):** the plan deferred a per-device
  `SemaphoreSlim`; the code adds one. Right call — but it is a *private* gate rather than the
  shared family gate → S2.
- **Scope beyond plan (from the merge, not drift):** `IsSmartInfiniteDevice` dispatch in the VM
  unifying Rampage with main's Cyrex/Aress paths; `RampageInterfaceSelector` deleted in favour of
  main's identical `SmartInfiniteInterfaceSelector`. Both are merge reconciliation, reviewed here.
- **Accepted limitation (per plan / hardware-findings):** Bluetooth is display-only for remapping;
  a BT-connected Rampage arrives with VID = PID = 0. Residual risk: see Nits (error banner UX).
- **Acceptance criteria:** 1–8, 10–11 met (3/4/5 verified on hardware 2026-08-04); 9 met (Raster /
  T-Racer files are byte-identical to main after the merge); **9a (RGB unchanged) is NOT yet
  verified** → S1.

#### Blockers

- none

#### Code

- [ ] **S1. RGB wire bytes changed on three shipped keyboards without hardware re-verification** —
  `src/ProoveGaming.Infrastructure/Services/AressKeyboardRgbService.cs:162` (same hunk in
  `CyrexKeyboardRgbService.cs` and `RampageKeyboardRgbService.cs`) — Should-fix (QA gate)
  **Problem:** the three RGB services' inline CRC16/Modbus framing was replaced with
  `RampageProtocol.BuildCommand`, which computes the additive checksum. Bytes 5–6 of every RGB
  command change on the wire. `origin/main` ships Aress/Cyrex RGB **today** with the old CRC16
  bytes (verified working on hardware in PRs #74/#78).
  **Why it matters:** "worked with a wrong checksum" implies the firmware ignores the field, and
  the additive sum matches the vendor captures byte-for-byte — but that is inference. If any of
  the three firmwares does validate the field, RGB breaks on a shipped device the moment this
  merges. Plan Risk 1a explicitly gates the merge on QA step 11.
  **Solution options:** A (required): run QA step 11 — switch a few RGB effects + brightness on
  Rampage (cabled now), Aress and Cyrex before merge, and note the result here. B (fallback if a
  board fails): per-family checksum selection in `BuildCommand` — one function, cheap to revert.
- [x] **S2. Key-mapping service does not share the family device gate with RGB** —
  *Closed:* `ReadKeyMatrixAsync` / `SetKeyMappingAsync` now acquire
  `SmartInfiniteDeviceGate.AcquireAsync(_device.DevicePath)`; the private `DeviceGates`
  dictionary is deleted. Verified on hardware (cabled Rampage, production read path). —
  `src/ProoveGaming.Infrastructure/Services/RampageKeyboardKeyMappingService.cs:89-91` — Should-fix
  **Problem:** the service serialises itself through a private
  `ConcurrentDictionary<string, SemaphoreSlim>`, while `RampageKeyboardRgbService` (and the whole
  Smart Infinite family) serialises through `SmartInfiniteDeviceGate` (`Classes/
  SmartInfiniteDeviceGate.cs:27`). Two different locks for the same `DevicePath` — both factories
  now select the same interface via `SmartInfiniteInterfaceSelector`, so RGB and key I/O can
  genuinely interleave.
  **Failure scenario:** the user clicks a key-assignment target (read 336 B → write 384 B → verify)
  while a debounced RGB push fires (`0x15 Set General Info` — documented in the knowledge base to
  **zero the key-assignment staging buffer**). The `0x15` lands between `0x18` packets; the key
  write is discarded, verify fails, the click reports `false` for no visible reason. No corruption
  (the table sent is internally valid), but a flaky, unexplainable assignment failure.
  **Fix:** replace the private gate with `SmartInfiniteDeviceGate.AcquireAsync(_device.DevicePath)`
  in `ReadKeyMatrixAsync` / `SetKeyMappingAsync` (acquire before `Task.Run`, dispose after) and
  delete `DeviceGates`. Small, mechanical; recommended before merge.
- [ ] **S3. Post-merge duplication: two protocol classes and two key-mapping services for one
  family** — `src/ProoveGaming.Infrastructure/Classes/RampageProtocol.cs` vs
  `Classes/SmartInfiniteProtocol.cs` — Should-fix (follow-up PR)
  **Problem:** the merge leaves ~90 %-identical `BuildCommand` / `BuildReadCommand` /
  `ComputeChecksum` / `ReadSlot` / `PatchSlot` / `ParseTable` / `IsPlausibleKeyTable` in both
  classes, plus `RampageKeyboardKeyMappingService` alongside `SmartInfiniteKeyMappingService`.
  The RM1 384-byte-write knowledge lives **only** in `RampageProtocol.EnumerateWriteChunks`; a
  future Cyrex firmware demanding the same shape would be fixed in the wrong class first.
  **Why deferred out of this branch:** the Rampage path is hardware-verified as-is; folding it
  into the shared service (with a key-count-gate bypass for Rampage) is behavior-affecting churn
  that deserves its own branch and its own hardware pass.
  **Fix:** follow-up PR — fold `RampageProtocol` into `SmartInfiniteProtocol` (add
  `EnumerateWriteChunks`, keep per-model write shapes), teach `SmartInfiniteKeyMappingService` the
  Rampage bypass, delete the Rampage-only pair. Track it; do not let it linger.
- [ ] **S4. Plan status is stale — still describes the blocked-on-firmware era** — this file,
  `## Implementation status` — Should-fix (docs)
  **Problem:** the status section asserts "Blocked on device firmware", "full-384-byte hypothesis
  disproved", and 336-byte transfers — all overturned by the RM1 factory update and the 2026-08-04
  hardware confirmation. Anyone reading the plan top-down gets the wrong current state.
  **Fix:** add a short dated addendum at the top of `## Implementation status` pointing to the
  factory update, the confirmed 384-byte bare-`0x18` write, and `hardware-findings.md`.
- [x] **S5. Investigation-only diagnostics remain in the production service** —
  *Closed:* the write-shape sweep, `WriteShape`, `ApplyWriteShape`, `DumpBlock`,
  `TestLightingTableWrite`, `WriteTableRaw`, `CommitWithSetProfile` and the marker-file plumbing
  are deleted (~330 lines); `LogBothTableViews` stays for the verify-mismatch path. Also
  addresses PR #67 review M1 (with gRPC deadlines added client-side) and closes M2 by removal. —
  `src/ProoveGaming.Infrastructure/Services/RampageKeyboardKeyMappingService.cs:62-67, 182-186,
  ~203-460` (`RunWriteDiagnostics`, `WriteShape`, `TestLightingTableWrite`, `DumpBlock`, the
  temp-file marker) — Should-fix
  **Problem:** ~300 lines of write-shape sweep exist to answer a question that is now answered
  (the write works). The branch's own status report recommends removing them before merge. They
  are gated (marker file / env var, once per process, only after a failed write), so the risk is
  dead weight and reviewer confusion, not behavior.
  **Fix:** delete the sweep and the marker plumbing; keep the normal-path logging. If the team
  prefers to keep it for future firmware regressions, say so here and close this item.

#### Tests

- [ ] Coverage matches the plan's test strategy (protocol framing + chunking golden vectors from
  vendor captures, catalog transcription vs an embedded copy of `key_matrix_tab`, VM behavior
  through public commands). All 842 tests green locally. Standing caveat, not new to this branch:
  `dotnet-ci.yml` builds only the App csproj — **CI never runs `tests/`**, so local runs are the
  only gate. Worth fixing repo-wide.

#### Nits

- RGB services still bracket their sessions in `0x10`/`0x11`, which the factory update says are
  unused. They demonstrably work; align them the next time the RGB path is touched —
  `AressKeyboardRgbService.cs` (CmdStart/CmdEnd) and siblings.
- A Bluetooth-connected Rampage shows the interactive overlay (name fallback) but always lands in
  `KeyAssignmentReadFailed` (VID/PID = 0 over BT), so the user sees an error banner rather than
  "remapping needs the cable". Consider a BT-specific message later.
- `RampageKeyCatalog` category strings ("Function Row", …) are hardcoded English — consistent with
  Raster/Cyrex catalogs; a localization pass over the family is a separate task.
- The private `DeviceGates` dictionary never evicts entries (bounded by distinct device paths;
  moot once S2 lands).

#### What's done well

- The write path earns its confidence: read-modify-write with `IsPlausibleKeyTable` alignment
  guard, address-matched replies (never positional), read-back verify, and abort-don't-write on
  any short read. Acceptance criterion 8 is genuinely enforced.
- The knowledge base is exemplary: every correction cites a capture, the vendor reply is recorded
  verbatim, and the "392-byte variant never actually sent 384" bounds-guard discovery is written
  down where the next investigator will find it.
- Catalog tests verify all 86 rows against an embedded copy of the vendor factory table — the one
  test shape that actually catches transcription typos.
- The merge reconciliation kept both hardware-verified paths intact (Rampage's dedicated service
  vs the key-count-gated shared one) instead of forcing an untested unification.

#### Verdict

Ship: **yes** — no blockers; the key-assignment path is hardware-confirmed end-to-end and the
merge leaves Raster/T-Racer/Cyrex/Aress byte-identical or verified-equivalent to main. The single
release risk is the deliberately changed RGB checksum (S1), which the plan itself gates on QA
step 11 — run it before merging. First: **1)** S1 RGB smoke test on Rampage/Aress/Cyrex,
**2)** S2 shared device gate (small, do before merge), **3)** S4 plan-status addendum; S3 and S5
may follow as a cleanup PR.

### Round 2 — Ship: yes

Reviewed at `bedbf80`, working tree clean. Surface since Round 1's follow-ups: `1ec3f19`
(closes S5 and the PR #67 external review's M1–M5) and `bedbf80` (unlit home cards — no plan;
reviewed in chat, summarized here for the record). `8351cc1` is review journal + vendor-request
HTML only.

#### Plan conformance

- **S5 closure verified:** the write-shape sweep is gone (−307 lines); no dangling references
  (`ReadAckFor` keeps its one production caller in `WriteTableOnStream:429`; `LogBothTableViews`
  keeps its verify-mismatch call site at `:235`). `RampageProtocol`'s profile/lighting command
  constants remain as protocol documentation — correct.
- **M1 deadline sub-item verified:** `UsbHidDeviceService` — `SetMacros` 60 s,
  `SetKeyMapping`/`ProbeKeyMatrix`/`ReadKeyMatrix` 20 s, all `DateTime.UtcNow` (gRPC requires
  UTC), all landing in the existing `RpcException` → `NotifyError` → false/null paths. Budgets
  are ~5–10× the measured worst case (4 retries × ~19 exchanges ≈ 3 s), so a deadline can only
  fire on a genuinely wedged host.
- **M4 golden test verified by hand:** `0x15 + 0x0A + Σpayload = 0x01F2` → bytes `F2 01` — the
  test pins the additive sum and its comment names the CRC16 change and the QA-11 gate.

#### Blockers

- none

#### Code

- [ ] **S6. Aress unlit home card renders at less than half its previous size** —
  `src/ProoveGaming.App/ViewModels/DeviceCardViewModel.cs` (`UseUnlitCardArt`) +
  `Views/DoubleCard.axaml` unlit branch — Should-fix (from the out-of-plan `bedbf80` change)
  **Problem:** the unlit branch renders `KeyboardArt` through `RgbKeyboardView`, whose
  `Viewbox` scales the full artwork canvas. Aress's canvas is near-square
  (1571.695 × 1563.529, the board a horizontal band in the middle), while its previous card
  image `PG_Aress_card.svg` was a wide crop of that canvas (`viewBox="34.5 541 1503.5 481.5"`).
  In the card's 336 × 160 box the near-square canvas is height-bound: ~161 px wide with the
  physical board a ~49 px band — versus 336 × 107 before. Cyrex is unaffected (its full canvas
  is already the wide 1472 × 481).
  **Failure scenario:** open HomeView with Aress and Cyrex cards side by side — the Aress board
  reads at roughly half the size of every other keyboard, floating in an empty square.
  **Solution options:** A (matches the user's "like Key Assignments" ask literally): render the
  Aress unlit card from the borrowed wide art the overlay already uses
  (`RgbKeyboardArtCatalog.SmartInfinite65`) via a small `UnlitCardArt` property — precedent
  exists, boards are physically identical bar three nav legends. B (keeps Aress's own art):
  teach the unlit branch a crop/viewport (clip to the card-svg's viewBox region) — more code,
  preserves product identity exactly. C (fallback): drop Aress from `UseUnlitCardArt` until A
  or B lands.

#### Tests

- [ ] `DeviceCardArtTests` reshape keeps full coverage (3 unlit + 3 product-shot + unknown).
  No automated test can catch S6 (pure rendering geometry) — visual QA on HomeView is the check.

#### Nits

- none new.

#### What's done well

- The sweep deletion is clean surgery: 307 lines out, both intentionally-kept diagnostics
  (`LogBothTableViews`, address-matched acks) untouched, and the external reviewer's M1–M5 each
  dispositioned with evidence on the PR.
- Deadlines went exactly where the external review pointed, with budgets derived from measured
  exchange counts rather than guesses.

#### Verdict

Ship: **yes** — S6 is a visual regression on one card and has a one-property fix; nothing else
outstanding from this round. First: **1)** S6 (recommend option A), **2)** the standing QA-11
RGB smoke test, **3)** visual QA of all three unlit cards on HomeView.
