# Key assignments for the Smart Infinite keyboard family (Cyrex, Aress)

> **Status:** implemented and shipped in PR [#72](https://github.com/wezom-company/proove-gaming/pull/72)
> (`features/cyrex-key-assignments` → `main`).
>
> This plan was written **retrospectively**, after the implementation, to record the design that was
> arrived at through hardware experimentation. It is not a forward-looking proposal. It exists because
> the repo requires a plan per change and because the protocol knowledge below was expensive to obtain
> and must not live only in commit messages. Where the implementation deviates from what a plan would
> normally prescribe, the deviation is called out rather than back-dated into a tidy story.

## Goal

Let users remap physical keys on the **Cyrex** and **Aress** keyboards from the desktop app, the same
way they already can on Raster and T-Racer. Both boards speak a vendor protocol (**Smart Infinite**,
报文 header `0x06`) that the codebase did not previously support, so this required a new transport in
`ProoveGaming.Infrastructure`, a new gRPC call to expose reading the live key matrix, and per-model key
catalogs. The work also had to establish *whether remapping is possible at all* per model — the third
member of the family, **Rampage**, turned out to be firmware-blocked, and distinguishing "our code is
wrong" from "this unit will not accept writes" was the bulk of the effort.

## Scope

**In:**

- A Smart Infinite protocol implementation: framing, checksum, chunked transfers, reply matching.
- Reading the device's live key matrix and writing a single slot via read-modify-write.
- Cyrex and Aress physical-key catalogs plus a shared 65% visual geometry.
- A new additive gRPC method `ReadKeyMatrix` and its App-side client method.
- Wiring the existing key-assignment UI to the new family.
- A hardware probe under `ProoveGaming.Device.Testing` for read/write verification on a real board.

**Out:**

- **Rampage key remapping.** Proven blocked in firmware (see [Rampage](#rampage-why-it-is-excluded)).
  Rampage is routed through the new service defensively but exposes no UI.
- Macros, RGB, and per-profile key layers on this family. The protocol has commands for these
  (`0x19` custom lighting, `0x30`/`0x31` profile) but they are untouched here.
- Remapping FN. Excluded by design on both boards — it would break every FN+X hardware shortcut.
- Any change to the Raster / T-Racer write paths. They only gained a `ReadKeyMatrixAsync`
  implementation of the newly-widened interface.

## Options considered

### 1. Extend `RasterKeyboardKeyMappingService` to speak both protocols

**Rejected.** Raster is a different OEM (睿鼎荣) with an incompatible frame: header byte `0xAA`, 65-byte
reports, QMK usage page `0xFF60`, per-slot commands `0x1A`/`0x1B`, and a 5-second session timeout.
Smart Infinite uses `0x06`, 64-byte reports, usage page `0xFF87`, and whole-table transfers. Sharing one
class would have meant a protocol discriminator threaded through every method for two families with
nothing in common but the concept of a key matrix.

### 2. Per-slot writes, mirroring how Raster works

**Rejected — the protocol does not offer it.** Smart Infinite's `0x18` writes a *region* of the table by
address, not a slot by index. There is no single-slot write command. Emulating one still means
transferring the whole table, so the read-modify-write in option 3 is the honest expression of it.

### 3. A dedicated `SmartInfiniteKeyMappingService` doing read-modify-write on the full table

**Chosen.** See below.

### 4. Transcribe the key catalogs from the vendor's documentation

**Rejected in favour of generating them from the hardware.** The vendor docs that prompted this work
(`软件驱动协议-20250927-V3.0.xlsx`, `keyboard_key_define.h`, `keyboard_user_flash.c`,
`智慧无限协议数据格式说明.docx`) arrived with **no statement of which keyboard they describe**, and one of
them contains a copy-paste error in a worked example (a `0x16` packet carrying the checksum for a `0x12`
packet). Documentation of unknown provenance is a poor source of truth for 67 hand-written table rows.
Reading the device's own 225-byte factory table and generating the catalog from it makes the hardware the
authority — and makes the catalog testable against the bytes it came from.

## Recommended approach

A new `SmartInfiniteKeyMappingService` in `ProoveGaming.Infrastructure`, selected by
`DeviceKeyMappingServiceFactory` on VID/PID, performing **read-modify-write of the whole key table inside
a single Start/End session on a single HID handle**.

Five decisions carry the design, each forced by observed hardware behaviour rather than chosen for
elegance:

**Replies are matched by echoed address, never by arrival order.** The firmware runs one command behind:
the reply to request *N* arrives after request *N+1* is sent, and the Start command's ack is itself
queued ahead of everything. An implementation that reads "the next reply" gets the *previous* request's
data. This produced the sharpest bug of the whole effort — a 56-byte offset in the read table, which
lands mid-record because `56 ≡ 2 (mod 3)`, so every slot decoded as a plausible-looking wrong key.
`ReplyFor(stream, cmd, addr)` drains until the echoed command and address match.

**The Start ack is never drained up front.** The obvious fix to the lag — read and discard the ack before
the first real request — corrupts the HidSharp Windows stream such that later writes fail. The ack is
instead absorbed by the address-matched draining above.

**Transfer sizes come from Device Info, never from a constant.** Byte 4 is the matrix reserve in 3-byte
units and byte 6 is the real key count. Cyrex and Aress report 75 slots (225 bytes, 5 packets: 4×56 + 1);
Rampage reports 128 (384 bytes). Hardcoding either number silently corrupts the other model.

**A write is refused unless the table just read back looks like a key matrix.** Write is
read-modify-write, so a misread table written back would scramble every key on the board. Two guards
stand in front of every write: the firmware must report a non-zero key count, and the table must pass
`IsPlausibleKeyTable`. That check must **not** accept `0x00` as a valid record TYPE — a table shifted by
one packet takes its TYPE column from the all-zero modifier column, so accepting `0x00` makes the guard
pass on exactly the corruption it exists to catch.

**Geometry is keyed by matrix slot, not by key name.** Cyrex and Aress are the same physical board. Their
factory tables differ in exactly three slots — 14, 44, 59, the navigation column, where Cyrex has
Delete / PageDown / End and Aress has Insert / Delete / PageDown. Slot is therefore the stable
identifier and key name is not, so `SmartInfinite65Geometry` maps slot → bounds once and each model's
visual layout is the join of its own catalog against it.

### Protocol reference

Established by experiment and cross-checked against the vendor documents. Recorded here because it is
not derivable from the code alone.

| Field | Bytes | Notes |
|---|---|---|
| Report ID | `[0]` | Always `0x06`. Reports are 64 bytes. |
| Command | `[1]` | See table below. |
| Address | `[2..3]` | Little-endian offset into the target table. |
| Length | `[4]` | Payload length, max 56. |
| Checksum | `[5..6]` | Little-endian, **plain additive sum**: `cmd + addr_lo + addr_hi + len + Σpayload`. Not CRC16. |
| Status | `[7]` | `0x00` in a request, `0x55` in a successful reply. |
| Payload | `[8..]` | Up to 56 bytes. |

| Cmd | Meaning |
|---|---|
| `0x10` / `0x11` | Start / end communication |
| `0x12` | Read device info |
| `0x14` / `0x15` | Read / write function config |
| `0x16` | Read **factory** key matrix |
| `0x17` | Read **current** key matrix |
| `0x18` | **Write** key matrix |
| `0x19` | Custom lighting |
| `0x30` / `0x31` | Read / write active profile |

Two commands are hazardous and must never be issued from the app:

- **`0x20` is a full factory reset.** Not a key-matrix reset — everything.
- **A `0x31` profile round-trip (`0→1→0`) freezes the firmware** until the board is physically
  unplugged. Discovered the hard way while probing whether writes landed in a profile-specific layer.

A key matrix record is 3 bytes `{TYPE, CODE_L, CODE_H}`. For `TYPE = 0x10`, `CODE_L` is a **modifier
bitmask** and `CODE_H` is the HID usage. So modifier keys are `{0x10, mask, 0x00}` and carry no usage
code — meaning a key must be identified by the **(mask, usage) pair**, never by usage alone, or all seven
modifiers collide on usage `0x00`.

### Rampage: why it is excluded

Rampage is the same protocol and was the original target of this work. It does not accept key-matrix
writes, and two independent measurements establish that this is firmware, not our code:

1. **Device Info byte 6 (key count) reads `0`** while every other field in the same block decodes
   correctly — matrix size, macro space, device type, version, max brightness, max speed.
2. **A `0x15` config write to the same device, over the same handle, in the same session, with the same
   checksum, persists and reads back byte-for-byte.** So transport, checksum, framing, interface
   selection and the read path are all provably correct on this unit.

`0x18` and `0x19` writes are acknowledged with `0x55` and discarded. Eight write variants, both read
commands, and a power cycle were tried. Two observations reported in earlier work (PR #45) — that
read-back returns the new value, and that replugging applies the mapping — **did not reproduce**, and the
staging-buffer theory built on them does not hold. The conclusion is that key remapping is not enabled in
this firmware build, which is a question for the vendor, not a defect to fix here.

The service refuses to write when the key count is zero and logs why. Rampage is routed to the service by
the factory but has no catalog, so `HasInteractiveKeyboardOverlay` is false and no UI is offered.

## File-level change list

### `ProoveGaming.Infrastructure` — protocol and transport

| File | Intent |
|---|---|
| `Classes/SmartInfiniteProtocol.cs` | **New.** Framing, additive checksum, `BuildCommand`, `EnumerateChunks` (trims the final packet), `ReadSlot`/`PatchSlot`, `ParseTable`, `IsPlausibleKeyTable`. No I/O — all of it unit-testable. |
| `Classes/SmartInfiniteDeviceInfo.cs` | **New.** Parses the 64-byte identity block; exposes `KeyCount`, `MatrixSlots`, `MatrixBytes` and `SupportsKeyRemap`. This type is what makes the Rampage block explicit rather than a mystery failure. |
| `Classes/KeyMatrixRecord.cs` | **New.** `readonly record struct` for the 3-byte record, with `IsUnusedSlot` and `IsFn`. Value equality is what the write verify compares. |
| `Classes/SmartInfiniteInterfaceSelector.cs` | **New.** Picks the writable vendor interface by HID usage `0x20` / page `0xFF87`, ordering candidates by `DevicePath` first. Extracted because the RGB factory had the same bug — an unsorted `FirstOrDefault` over several 64/64 interfaces that picked an input-only one. |
| `Classes/SmartInfiniteDeviceGate.cs` | **New (Round 3).** The one-session-per-interface gate, shared by every service that opens this interface rather than owned by key mapping. Added for M2. |
| `Services/DeviceRgbServiceFactory.cs` | **Round 3.** Rampage / Aress / Cyrex now select their interface through `SmartInfiniteInterfaceSelector` instead of an unsorted shape match — the fix the selector was extracted for (M3). |
| `Services/{Cyrex,Aress,Rampage}KeyboardRgbService.cs` | **Round 3.** Each takes `SmartInfiniteDeviceGate` before opening the interface, so an RGB write can no longer end a key-matrix read's session (M2). |
| `Services/SmartInfiniteKeyMappingService.cs` | **New.** The read/write implementation: one session per operation, static per-`DevicePath` gate, address-matched replies, key-count and plausibility guards, read-back verify. |
| `Interfaces/IKeyboardKeyMappingService.cs` | Add `ReadKeyMatrixAsync`. The existing `ProbeDefaultMatrixAsync`/`ProbeUserOverridesAsync` pair assumes a factory-vs-override split this family does not have. |
| `Services/RasterKeyboardKeyMappingService.cs`, `Services/TRacerKeyboardKeyMappingService.cs` | Implement the new interface member over their existing per-slot loop, keeping all three record bytes instead of just the usage code. |
| `Services/DeviceKeyMappingServiceFactory.cs` | Route Cyrex / Aress / Rampage to the new service. |

### `ProoveGaming.Common` — catalogs and geometry

| File | Intent |
|---|---|
| `Classes/SmartInfinite65Geometry.cs` | **New.** Slot → pixel bounds for the 65% board, plus `LayoutFor(catalog)`. The single definition of the geometry both models share. |
| `Classes/CyrexKeyCatalog.cs` | **New.** 67 remappable keys, generated from the device's 225 factory bytes. |
| `Classes/AressKeyCatalog.cs` | **New.** Same, from Aress's own bytes. |
| `Classes/CyrexVisualLayout.cs`, `Classes/AressVisualLayout.cs` | **New / rewritten.** Each derives from the shared geometry. |
| `Classes/RasterKeyCatalog.cs` | Add an optional `ModifierMask` to the shared `PhysicalKey` record. Modifier keys carry no usage code, so without this all seven are indistinguishable. |

### gRPC contract and host

| File | Intent |
|---|---|
| `ProoveGaming.GrpcDependencies/Protos/deviceservice.proto` | Additive only: `rpc ReadKeyMatrix`, `ReadKeyMatrixRequest`, `KeyMatrixRecordProto`, `ReadKeyMatrixResponse`. No existing field number reused. |
| `ProoveGaming.DeviceHost/Services/DeviceService.cs` | Handler mirroring the shape of the existing `ProbeKeyMatrix`. |

### App

| File | Intent |
|---|---|
| `Services/IUsbHidDeviceService.cs`, `Services/UsbHidDeviceService.cs` | Client method returning a tuple dictionary — the App does not reference `Infrastructure`, so `KeyMatrixRecord` cannot cross this boundary. |
| `ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` | `IsSmartInfinite65Device` dispatch, canvas size from the shared geometry, live assignments read into a slot-keyed map, target matching on the full `(type, mask, usage)` triple, highlight rollback on a failed write. |
| `Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml` | Bind canvas size instead of the hardcoded `665×221`, which is Raster's size. |
| `Views/DeviceDetails/Sections/KeyAssignmentsSectionView.axaml` | **Round 1.** Warning banner shown when the live matrix could not be read, so the factory fallback is labelled as a fallback. |
| `Resources/Strings.en.axaml`, `Resources/Strings.uk.axaml` | **Round 1.** The banner's text, in both languages. |

### Tooling

| File | Intent |
|---|---|
| `ProoveGaming.Device.Testing/SmartInfiniteProbe.cs` | **New.** Device-info and matrix dump, plus a write test with restore. This is what produced every byte in the catalogs and every finding in this document. |
| `ProoveGaming.Device.Testing/Program.cs` | Point at the new probe. |

## Acceptance criteria

- [x] Given a Cyrex is connected, when the user opens Key assignments, then a 65% overlay of 67 keys
      renders and each key shows its **live** mapping read from the device, not the factory default.
- [x] Given an Aress is connected, the same holds, and the top-right navigation key reads **Insert**
      (Cyrex shows Delete there).
- [x] Given a key is remapped in the UI, when the user presses that physical key, then the new key is
      produced, and the change survives unplugging and reconnecting the keyboard.
- [x] Given a write fails or cannot be verified, then the highlight returns to what the device actually
      holds, and no partial table is left written.
- [x] Given the table read back before a write does not decode as a key matrix, then the write is
      refused and the reason is logged — the board is never left scrambled.
- [x] Given a device reports zero remappable keys (Rampage), then no write is attempted, the refusal is
      logged with the decoded device info, and no key-assignment UI is offered.
- [x] Given Raster and T-Racer, their key-assignment behaviour is unchanged.
- [x] Given concurrent read and write requests for the same device, then they serialise on one handle —
      no interleaved sessions on the vendor interface.
- [x] `0x20` and `0x31` are never sent by App or DeviceHost code.
- [x] `dotnet build ./src/ProveGaming.slnx` is clean and `dotnet test ./src/ProveGaming.slnx` is green.

> *2026-08-19 — the read-failed banner self-heals.* QA hit a wireless Rampage dozing through the
> page-open matrix read: the `KeyAssignmentReadFailed` banner appeared and stayed until the page was
> reopened, although the link was back within seconds. The slot-keyed read path in
> `DeviceDetailsViewModel.KeyAssignments.cs` now retries around the banner instead of flagging one
> failed read: two quick attempts (1 s, 2 s) run before the flag is raised at all — a sleep/wake blip
> never shows the banner — then a slow loop (every 5 s for ~2 min) keeps re-reading behind it and
> clears it the moment a read lands, so a reconnect needs no page reopen. A read-only transport
> (Rampage over BT, VID/PID = 0 — see the Rampage plan) skips the loop: its banner is deliberate and
> permanent. Covered by the self-heal tests in `DeviceDetailsCyrexKeyAssignmentTests`.

## Test strategy

This is an `add-*` change, so the coverage below is required, not optional. It landed as **511 passing
tests** (290 Infrastructure + 221 App).

**`tests/ProoveGaming.Infrastructure.Tests/SmartInfiniteProtocolTests.cs`** — the pure protocol layer,
18 tests. The cases that matter are the ones that pin a bug already made once:

- The checksum is an additive sum, verified against real captured packets — with the oracle zeroing
  bytes 5–6 first. An earlier characterization test hashed the checksum field it was verifying, which
  *locked in* the wrong algorithm instead of catching it.
- `EnumerateChunks` trims the final packet: 225 bytes → 4×56 + 1×1, and 384 → exact 56s.
- `IsPlausibleKeyTable` **rejects** a table shifted by one packet. This is the regression test for the
  56-byte misalignment, and it only works because `0x00` is not a valid TYPE.

**`tests/ProoveGaming.App.Tests/CyrexKeyCatalogTests.cs`** (15) and **`AressKeyCatalogTests.cs`** (21) —
each catalog checked against the raw factory hex it was generated from, entry by entry. Also: the real
key count derived from the table independently equals the 68 the firmware reports; modifier keys carry
their mask and no usage; every `(mask, usage)` pair is unique; every key has non-overlapping bounds
inside the canvas; the shared geometry covers both catalogs.

The highest-value test in the suite is `DiffersFromCyrex_InExactlyThreeNavigationSlots`, which asserts
the two catalogs use an identical slot set and differ in **exactly** slots 14, 44, 59. A fourth
difference means one of the two generators is wrong, and nothing else would catch it.

**`tests/ProoveGaming.App.Tests/DeviceDetailsCyrexKeyAssignmentTests.cs`** (17) — ViewModel behaviour
against a fake HID service: the slot-keyed read is used and the legacy probe is not; the write targets
the catalog slot; a failed write leaves the highlight where the device is; and each board loads its own
navigation column.

**Deliberately not covered by automated tests:** real HID I/O, the one-command reply lag, session
bracketing, and interface selection. All of it needs the physical board — hence the probe and the manual
checklist below. The reply-lag behaviour in particular cannot be faked without a firmware simulator, and
writing one would encode the same assumptions the transport is trying to verify.

## QA (manual test steps)

Requires a physical Cyrex and a physical Aress. Ensure the ProoveGamingDeviceHost service is running
before starting.

1. Connect the **Cyrex** keyboard and launch the app. Open the Cyrex device page → **Key assignments**.
   *Expected:* a 65% keyboard picture appears with all keys labelled. The top-right key of the first
   row reads **DEL**.
2. Click the **E** key on the picture, then choose **A** from the assignment list.
   *Expected:* the highlight moves to A within about a second and does not jump back to E.
3. Press the physical **E** key in any text field.
   *Expected:* the letter **a** is typed.
4. Unplug the keyboard, plug it back in, wait for the app to show it as connected, and press physical
   **E** again.
   *Expected:* still types **a** — the change is stored in the keyboard, not the app.
5. In the app, click **E** on the picture and set it back to **E**. Press physical **E**.
   *Expected:* types **e** again.
6. Disconnect the Cyrex. Connect the **Aress** keyboard and open its **Key assignments**.
   *Expected:* the same 65% picture, but the top-right key of the first row reads **INS**, not DEL.
7. Repeat steps 2–5 on the Aress.
   *Expected:* identical behaviour.
8. While a remap is being applied, click rapidly on several different keys in the picture.
   *Expected:* the app stays responsive — no freeze, no spinner that never ends, no key ending up with
   the wrong assignment.
9. Open the **Cyrex** page and, while it is still loading, immediately change the lighting colour.
   *Expected:* the colour applies and the key picture fills in with the real layout — no orange
   "could not read the current layout" warning. (Lighting and key assignments share one channel to the
   keyboard; this checks they no longer interrupt each other.)
10. Connect the **Rampage** keyboard and open its device page.
   *Expected:* no interactive keyboard picture is offered. The app does not error or hang. (Rampage
   remapping is blocked in the keyboard's firmware and is pending a vendor answer.)
11. Check the FN key on both Cyrex and Aress.
    *Expected:* FN is not clickable/remappable, and FN+arrow-key shortcuts still work on the hardware.

## Review focus

`thread-safety`, `gRPC contract`, `async/await`, `resource disposal`.

Concretely: the static per-`DevicePath` gate is the only thing preventing two sessions on one vendor
interface, and it must be static because the factory builds a fresh service per gRPC call — an instance
field there serialises nothing. All HID work runs inside `Task.Run` so the Avalonia dispatcher never
blocks on it, and `InSession` must close the handle and send End on every exit path including exceptions.

## Performance & quality

**Not a hot path.** Key remapping is user-initiated, a handful of times per session. A write is
~11 packets with deliberate inter-packet delays and settle time — on the order of a second, entirely
intentional, and entirely off the UI thread inside `Task.Run`. `Thread.Sleep` in the transport is correct
here for the same reason: it is a background thread pacing a HID device, not the dispatcher.

**Thread-safety.** The vendor interface tolerates exactly one session at a time. Both read and write take
the same static `SemaphoreSlim` keyed by `DevicePath`, so a read arriving mid-write waits rather than
interleaving. The gates are process-lifetime and never disposed, which is acceptable for a fixed set of
device paths.

**Allocations.** A 384-byte table per operation. Irrelevant at this frequency; not worth pooling.

**UI thread.** `_assignments` is mutated only from awaited continuations on the dispatcher, so it needs
no lock — but that invariant is implicit and would break if any of this moved to a background thread.

## Risks

- **The catalogs are generated from two specific physical units.** If a later hardware revision ships a
  different factory table, the catalog tests will keep passing (they check against a stored hex string)
  while the UI mislabels keys. The mitigation is that the tests pin the *device-reported* key count too,
  so a changed matrix size surfaces; a changed key *arrangement* at the same size would not.
- **`0x20` and the `0x31` round-trip are destructive** and are documented but not mechanically
  prevented. Nothing stops a future contributor from adding profile support and freezing firmware.
- **Rampage stays unresolved** pending the vendor. If a firmware update enables remapping, the only
  work needed is a Rampage catalog and geometry — the transport already handles its 128-slot table.
- **The read path depends on the one-command lag being exactly one command.** It is handled by draining
  until the address matches, which is robust to *more* lag, but a firmware that replies out of order
  entirely would need a different scheme.
- **Interface selection depends on usage `0x20` / page `0xFF87`.** The fallback shape heuristic
  (64/64 reports) previously picked an input-only interface, which is what broke RGB.

## Note — 2026-09-09: Extended Keys share the matrix

`docs/plans/add-cyrex-extended-keys/` adds the Extended Keys section (Rapid Snap first). A key inside
an extended-key record has a `{0x8x, off_lo, off_hi}` pointer in its matrix slot, not a key record, so:
Key Assignments and Macros refuse to open the dialog for such a key (a banner names the record), the
hover tip reads "Rapid Snap · D", and `ApplySlotKeyOverridesToDeviceAsync` /
`ResyncKeyMacroBindingsAsync` skip those slots (from the live `device_extended_keys` rows) — without
the skip the slot push would "restore" the key record and orphan the record on every preset push.
The preset push runs the extended-key write after the key and macro pushes and re-runs both when
records were removed.

## Review findings

_Filled in by /reviewer._

### Round 1 — self-review of the shipped PR #72

Reviewed by the same author who wrote the code, which is a known weakness of this round: self-review
systematically misses what the author considered obvious. An independent pass is still warranted,
particularly on the transport.

Verified sound: the additive checksum against captured packets; the address-matched reply dispatch; log
placeholder/argument pairing in the script-generated Raster and T-Racer methods (a scripted edit that
compiles cleanly can still emit malformed log lines — checked, correct); the proto change being purely
additive with no reused field numbers; all HID I/O confined to `Task.Run`; and the write path's three
guards plus read-back verify, which together mean a failed write returns `false` rather than leaving a
partial table.

#### Code

1. **Should fix — FIXED.** *A failed matrix read silently displayed factory defaults as live state.*
   `ReadSmartInfiniteAssignmentsAsync` returns early when the gRPC read yields `null`, leaving
   `_assignments` null. `SyncAssignmentTargetSelection` then falls back to the catalog's factory values,
   so a user who previously remapped E→W saw **E** highlighted with nothing indicating the read failed.
   Wrong state shown confidently is worse than an error.
   *Resolution:* added `KeyAssignmentReadFailed`, set on a null read and cleared on success and on device
   switch, with a localized (en/uk) warning banner above the target list. The banner keeps the factory
   fallback — it is still the least-wrong thing to draw — but labels it as a fallback. Emphasis is not
   colour-only: border and semibold weight carry it alongside the amber. Covered by three new tests,
   including that switching devices clears a stale warning.

2. **Should fix — FIXED.** *`_info` was a dead cache.* `ReadDeviceInfo` guarded on an instance field, but
   the factory constructs a fresh service per gRPC call, so the guard could never hit. The class already
   documented that per-call lifetime in the comment justifying the *static* gate, which made the
   instance-scoped cache beside it an inconsistency a reader would trust.
   *Resolution:* deleted the field rather than making it static. Behaviour is unchanged — every operation
   already re-read Device Info — and a static cache would have traded two packets on a ~1 s user-initiated
   action for the risk of serving stale identity after a firmware change or replug. The reasoning is now
   in a comment on the method so the absence of caching reads as a decision, not an oversight.

3. **Consider — `WriteTable` counts acks and only logs them.** `acked` is never compared against
   `chunks.Count`, so a firmware rejection is caught only downstream by the verify read. Given that
   ack accounting off by one was precisely what previously overstated the Rampage evidence, this
   deserves to be a hard check rather than a log line.

4. **Consider — `maxSlot` is accepted and ignored.** `ReadKeyMatrixAsync(int maxSlot = 128)` uses
   `info.MatrixSlots` instead, and the proto advertises `max_slot = 3; // e.g. 128`. Deriving the bound
   from Device Info is the correct behaviour; silently ignoring a documented parameter is not. Drop it
   from this path or document it as advisory.

5. **Consider — the probe writes to the device on a plain `dotnet run`.** `SmartInfiniteProbe` calls
   `WriteTest` whenever the device reports a non-zero key count. It restores afterwards, but a crash
   between write and restore leaves the user's board remapped. Put it behind an argv flag.

6. **Nit — no `CancellationToken` anywhere in the new path,** which `CLAUDE.md` asks for. Consistent
   with the existing Raster services and blocked by the shared interface, so this is a wider refactor
   rather than a defect in this change. Recorded so it is not mistaken for an oversight.

7. **Informational — `Program.cs` lost 136 lines** of earlier probe code. `Device.Testing` is a scratch
   project and it is recoverable from history, but a reviewer should confirm it was intended.

#### Tests

No findings. Coverage matches the strategy above and the catalog tests check against the bytes the
catalogs were generated from rather than restating them. One gap was named and closed with finding 1:
the ViewModel tests asserted the failed-*write* rollback but nothing exercised a failed *read*. The suite
now stands at **514 passing** (290 Infrastructure + 224 App).

Findings 3–7 remain open and are deliberately not fixed in this round — 3 and 4 are behavioural changes
worth their own review, 5 touches only the scratch probe, 6 needs an interface-wide refactor, and 7 is a
question for a human rather than a change.

### Round 2 — Ship: yes

Full branch delta: `main..HEAD` = 2 commits (`7b4b1cc` Cyrex and Aress key assignments, `4803cf1`
review changes), 33 files, +3789/−146. Working tree clean — nothing unstaged, staged, or untracked, so
the committed delta is the whole review surface. Round 1 covered the ViewModel, the write path and the
generated Raster/T-Racer methods; this round goes after what Round 1 only skimmed — the protocol
primitives, interface selection, the gRPC seam and the device-config wiring — and verifies the Round 1
fixes as shipped.

**Same caveat as Round 1, and it has not weakened:** author and reviewer are the same. Two rounds of
self-review are not a substitute for one independent pass over the transport.

#### Plan conformance

Plan: `docs/plans/add-smart-infinite-key-assignments/add-smart-infinite-key-assignments-plan.md`

- **Implemented as planned:** every entry in the File-level change list exists and does what its stated
  intent says. Spot-checked the load-bearing claims rather than trusting the table: transfer sizing is
  taken from `SmartInfiniteDeviceInfo` and never hardcoded; `IsPlausibleKeyTable` genuinely excludes
  `0x00` from `KnownTypes` (`SmartInfiniteProtocol.cs:201-209`); `EnumerateChunks` trims the tail so 225
  bytes yield 4×56+1; `0x20` appears only as a documented do-not-send comment
  (`SmartInfiniteProtocol.cs:74-76`) and is issued nowhere; no `0x30`/`0x31` traffic anywhere in
  `src/**`. Device identifiers in `appsettings.json` (Rampage 10473/12888, Aress 10473/12910, Cyrex
  10473/12914) match the catalogs and their tests.
- **Deviates from plan (documentation, not code):** the File-level change list predates the Round 1 fix
  and omits three files that shipped with it — `src/ProoveGaming.App/Resources/Strings.en.axaml`,
  `Strings.uk.axaml`, and `src/ProoveGaming.App/Views/DeviceDetails/Sections/KeyAssignmentsSectionView.axaml`.
  The code is right; the plan's table is stale and should gain those rows.
- **Round 1 fixes verified as shipped:** `_info` is gone from the service entirely (zero occurrences at
  `HEAD`), and the banner binds `KeyAssignmentReadFailed` with the string present in **both** `en` and
  `uk` — a localization pair that is easy to half-do and was not.
- **Accepted limitations (per plan), unchanged:** Rampage remains firmware-blocked and UI-less; findings
  3–7 from Round 1 remain open by explicit decision. Residual release risk is unchanged and is carried in
  Open questions.
- **Acceptance criteria:** all ten are met. The last one re-verified this round — build clean (only
  pre-existing warnings in Installer / Uninstaller / `AddDeviceCardViewModel`, none from this branch),
  `dotnet test ./src/ProveGaming.slnx` green at **514 passing** (290 Infrastructure + 224 App).

#### Code

- [ ] **S1. Interface selection logs every candidate at Information and parses each report descriptor
  twice — on every read and every write.** — `src/ProoveGaming.Infrastructure/Classes/SmartInfiniteInterfaceSelector.cs:37-47` — Should-fix

  **Problem / why it matters:** the diagnostic `foreach` at line 42 calls `HasVendorConfigUsage(candidate)`
  for every candidate to build its log line, and line 47 then calls `HasVendorConfigUsage` again on the
  same candidates to make the actual choice. Each call parses the HID report descriptor. Because the
  factory constructs a fresh service per gRPC call, this runs on *every* matrix read and *every* key
  write — not once per session. A user remapping ten keys produces twenty selections, each emitting a
  full candidate dump at **Information** level into the DeviceHost log, where it competes with the
  transport diagnostics that are the reason that log exists.

  This is the same shape as a defect already caught once on this feature — a diagnostic sweep left
  running in production on every click — which is why it is called out rather than left as a nit.

  **Fix:** project the candidates once into `(device, hasVendorUsage, outLen, inLen)` and select from
  that, and drop the per-candidate dump to `LogDebug`, keeping the single "selected path=…" line at
  Information. Behaviour is unchanged; the log stops growing linearly with user activity.

#### Tests

- [ ] `SuccessfulMatrixRead_LeavesNoFailureFlag` — `tests/ProoveGaming.App.Tests/DeviceDetailsCyrexKeyAssignmentTests.cs`
  asserts `KeyAssignmentReadFailed` is `false` after a good read, but `false` is also the field's initial
  value, so the test passes on an implementation that never sets the flag at all. It has real value only
  as a pair with `SwitchingDevices_ClearsAStaleFailureFlag`, which does set the flag first and is the one
  carrying the weight. Strengthen it by failing a read, asserting `true`, then succeeding a second read
  on the same device and asserting it flips back — that is the only ordering the current pair leaves
  unexercised, and it is the realistic one (device recovers without a device switch).

#### Nits

- Device-name matching diverges between layers: the factory uses exact `name.Equals("Cyrex", …)`
  (`DeviceKeyMappingServiceFactory.cs:50-52`) while the ViewModel uses substring
  `IsCyrex(Device.Name)` → `Contains("cyrex", …)`. Not reachable today — `appsettings.json` holds exactly
  `Cyrex` / `Aress` / `Rampage` — but the two layers now disagree on what counts as a Cyrex, so renaming
  a config entry to something like `Cyrex Pro` would leave the UI offering a feature the factory refuses
  to build a service for. The exact-match style is pre-existing (Raster, T-Racer), so this is about the
  new substring matcher meeting it.
- `ReadSlot` / `PatchSlot` guard the upper bound but not a negative slot, so a negative index throws
  `IndexOutOfRangeException` instead of the `ArgumentOutOfRangeException` the method documents —
  `SmartInfiniteProtocol.cs:157-178`. Callers pass a `byte`, so it is unreachable from current code.
- `KnownTypes` includes record type `0x20` (consumer) — `SmartInfiniteProtocol.cs:204` — which shares its
  numeric value with the destructive `0x20` *command* warned about twenty lines above. Different
  namespaces, no defect, but a one-line comment would stop a future reader connecting them.
- `ComputeChecksum` indexes `report[1..4]` with no length guard; a span shorter than 8 bytes throws
  rather than returning a diagnosable error — `SmartInfiniteProtocol.cs:132-139`. Internal helper, all
  call sites pass a 64-byte report.

#### What's done well

- **The failure that motivated Round 1's fix was narrower than Round 1 stated, and the fix still lands in
  the right place.** `UsbHidDeviceService.ReadKeyMatrixAsync` *does* raise `NotifyError` on
  `RpcException`, so a dead DeviceHost was never fully silent. What was silent is the commoner case: RPC
  succeeds, `Success = false`, `return null`, no notification. The banner covers exactly that gap.
- Catalogs generated from device bytes and tested against those same bytes, with
  `DiffersFromCyrex_InExactlyThreeNavigationSlots` pinning the Cyrex/Aress relationship. A fourth
  divergence between the two boards has no other way of being caught.
- Hazards are recorded where someone about to trip them will read them — `0x20` beside the command
  constants, the reply lag and the Start-ack rule in the type-level remarks, the `56 ≡ 2 (mod 3)`
  reasoning attached to the guard it justifies rather than in a commit message.
- The write path refuses on three independent grounds before touching the device and verifies by
  read-back after, so a failed write returns `false` rather than leaving a half-written table.
- `SmartInfinite65Geometry` keys on matrix slot rather than key name — the one identifier that is stable
  across the two models — so the shared geometry cannot drift out of step with either catalog.

#### Verdict

Ship: yes — no blockers. The single Should-fix is log volume and duplicated descriptor parsing on a
user-initiated path, not correctness, and is a safe follow-up. Plan conformance is complete on code; only
the plan's own file table is stale. Standing caveat: this branch has now had two rounds of review from
its own author and none from anyone else — the transport deserves an independent pass before it is
treated as settled.

First: 1) S1 — collapse the double descriptor parse and drop the candidate dump to Debug. 2) Strengthen
`SuccessfulMatrixRead_LeavesNoFailureFlag` into a recovery test. 3) Add the three Round 1 fix files to
the plan's File-level change list.

### Round 3 — external review (PR #72, @boikoserhii84), all findings fixed

The independent pass called for at the end of Round 2. It found three defects that two rounds of
self-review did not, which is the point: **M1 and M2 are concurrency bugs whose failure modes look like
correct-but-wrong UI, and M3 is an extraction I documented as a bug fix and then did not apply at the
site of the bug.** All three are accepted as correct. Round 2's own S1 was folded in, since M3 multiplies
the number of call sites it affects.

- [x] **M1. Device-switch race: one keyboard's read lands in another's state.**
  `DeviceDetailsViewModel.KeyAssignments.cs` — `ReadSmartInfiniteAssignmentsAsync`, started
  fire-and-forget from `DeviceDetailsViewModel.cs:568`. A read takes ~1 s over gRPC and real HID; a user
  switching devices inside that window has two in flight, and the slower one wrote its records — or its
  failure flag — into state belonging to the newer device. Because the highlight matches on
  `(type, mask, usage)` rather than on slot, a stale Cyrex result on an Aress page mislabels exactly the
  three navigation slots the two boards differ in. The reviewer's sharper point: a stale *failure*
  raises the Round 1 banner over correctly-read data, and a stale *success* clears a banner that
  belongs — the fix for "wrong state shown confidently" reintroducing the same fault from the other side.
  *Resolution:* option A — `_keyLayoutGeneration`, incremented in `LoadKeyLayoutForDevice`, captured
  before the `await`, compared after, returning before any state is touched.

- [x] **M2. The vendor interface is shared with RGB, but only key mapping took the gate.**
  The static per-`DevicePath` semaphore was documented as the invariant preventing two sessions on one
  interface — but it lived inside `SmartInfiniteKeyMappingService`, while `Cyrex`/`Aress`/`Rampage`
  `KeyboardRgbService` open the same 64/64 interface and send the same `0x10` … `0x11` bracket without
  it. A closing `0x11` ends the session *for the interface*, so an RGB write during the page-load matrix
  read strands the reader's remaining chunks: `Success = false`, banner, factory layout.
  *Resolution:* option A — extracted `SmartInfiniteDeviceGate` (`Infrastructure/Classes`), taken by key
  mapping and all three RGB services. Exposed as `AcquireAsync` returning a disposable releaser, with
  double-dispose guarded via `Interlocked.Exchange` — releasing a permit twice would reopen precisely
  the window the type exists to close.

- [x] **M3. The selector was extracted as a fix for the RGB factory's bug, then not applied there.**
  `SmartInfiniteInterfaceSelector`'s own XML doc and this plan's change list both state it was extracted
  because `DeviceRgbServiceFactory` picked an interface with an unsorted `FirstOrDefault` over several
  64/64 collections. All three call sites (Rampage, Aress, Cyrex) were still on the old code. Beyond the
  original `ERROR_INVALID_FUNCTION` symptom, this branch made it worse: key mapping and RGB were
  selecting by *different rules*, so they could land on different `DevicePath`s — which would have
  quietly defeated M2's gate even after fixing it.
  *Resolution:* option A — all three now call the selector.

- [x] **S1 (Round 2).** Folded in: candidates are projected once into `(device, hasVendorUsage, outLen,
  inLen)` instead of parsing each report descriptor twice, and the per-candidate dump moved to `Debug`.
  With M3 this path now runs on RGB changes too, so the duplicated parse and the Information-level noise
  would have grown by more than the read/write traffic Round 2 measured.

#### Tests

Four new tests in `tests/ProoveGaming.App.Tests/DeviceDetailsCyrexKeyAssignmentTests.cs`, on a `FakeHid`
extended with a `ReadGate` `TaskCompletionSource` so two reads can genuinely be in flight at once. Each
read captures the matrix at call time, so changing `Matrix` afterwards cannot retroactively alter a read
already started.

- `StaleReadFromAPreviousDevice_IsDiscardedInsteadOfLandingOnTheCurrentOne` — the slot-14 case:
  Cyrex Delete vs Aress Insert, with the divergent key explicitly selected so the highlight is actually
  driven by it.
- `StaleFailedRead_DoesNotRaiseTheBannerOverAHealthyDevice` and
  `StaleSuccessfulRead_DoesNotClearTheBannerOfTheCurrentDevice` — both orderings of the flag case.
- `ARetryAfterAFailedRead_ClearsTheBannerWithoutSwitchingDevices` — the recovery path promised in Round 2.
  First attempt asserted the key still reads `E`, which the factory fallback satisfies too and so proved
  nothing; it now reports slot 18 as `A`, a value reachable only from live data.

**All three race tests were verified to fail with the generation guard disabled and pass with it.** A
concurrency test that passes either way is worse than no test, and this one is cheap to get wrong.

Suite: **518 passing** (290 Infrastructure + 228 App), build clean.

#### Verdict

Ship: yes. Three real defects closed, two of them concurrency faults that self-review missed across two
rounds. The Round 2 caveat is now discharged — this branch has had an independent pass, and it earned its
keep.

Remaining open from Round 1, unchanged and still deliberate: 3 (write-ack accounting only logged),
4 (`maxSlot` accepted and ignored), 5 (probe writes on a plain `dotnet run`), 6 (no `CancellationToken`
anywhere in the new path — note the reviewer's M1 option C would have subsumed this), 7 (`Program.cs`
lost 136 lines of earlier probe code).

### Round 4 — Ship: no

Surface: 3 commits (`7b4b1cc`, `4803cf1`, `48a53c0`) plus 11 uncommitted working-tree entries — 10
modified, 1 untracked (`SmartInfiniteDeviceGate.cs`, opened and reviewed in full since a three-dot diff
does not show it). Nothing staged. The union reconciles with `git status --porcelain` exactly. This round
targets the Round 3 fixes, which are the newest and least-reviewed code on the branch.

#### Plan conformance

- **Implemented as planned:** the Round 3 change list matches the working tree. Verified the two claims
  that could have been half-done rather than trusting them: `SetLedColorAsync` is the *only* method in
  each of the three RGB services that opens the interface (every other member is a `Task.FromResult(false)`
  stub), so the M2 gate is complete for those services, not partial; and across `src/**` only those three
  plus `SmartInfiniteKeyMappingService` open a Smart Infinite interface at all, so no fifth consumer was
  missed. All three RGB factory call sites now go through the selector.
- **Deviation, benign:** the RGB factory's per-model "vendor interface not found" warnings were dropped
  when the selector replaced the shape match. Not a loss of diagnostics — `SmartInfiniteInterfaceSelector`
  logs its own warning and the `RGB <model>` tag keeps it attributable.
- **Behaviour change worth naming:** RGB interface selection previously *required* 64/64. It now prefers
  usage `0x20`/page `0xFF87` and falls back to 64/64, so on a board where the usage matches but the shape
  differs, RGB will pick an interface it used to reject. This is the intended vendor rule and key mapping
  already runs on it, but it is a real change to a shipped subsystem and belongs in the PR description.
- **Acceptance criteria:** all still met. Build clean; **518 passing** (290 Infrastructure + 228 App).

#### Code

- [x] **B1. FIXED. The M1 device-switch race was fixed in the read path only — the write path still has it.** —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs:397-424`
  (`ApplyAssignmentTargetAsync`) — Blocker

  **Problem:** `ReadSmartInfiniteAssignmentsAsync` now captures `_keyLayoutGeneration` before its `await`
  and bails if the page changed underneath it. `ApplyAssignmentTargetAsync` awaits
  `SetKeyMappingAsync` at line 403 — the same ~1–2 s of real HID through the same gRPC hop — and then
  mutates shared state at line 422 with no such check.

  **Why it blocks:** user selects **E** on the Cyrex page and picks target **A**; the write starts. Inside
  that window they switch to Aress: `LoadKeyLayoutForDevice` bumps the generation, clears `_assignments`,
  and the Aress read repopulates it. The Cyrex write then returns `ok = true` and executes
  `_assignments[18] = (0x10, 0x00, 0x04)` — into **Aress's** map. The Aress page now shows E remapped to A
  when the Aress firmware was never told any such thing. That is precisely the "wrong state shown
  confidently" failure the Round 1 banner exists to prevent, arriving through the one door left open.

  Two aggravating details. `slot` is resolved *before* the await (line 402) but `IsSmartInfinite65Device`
  is evaluated *after* it (line 419), so the two halves of the method can disagree about which device they
  are serving — a Raster write completing after a switch to Cyrex takes the Smart Infinite branch and
  writes into `_assignments`. And the `!ok` path at line 415 calls `SyncAssignmentTargetSelection()`
  against the new device's state.

  This is graded a blocker rather than a should-fix because the guard's presence in the sibling method
  makes the class look protected. A partial concurrency guard is worse than none: the next reader sees
  `_keyLayoutGeneration` and reasonably assumes the ViewModel handles device switching.

  **Solution options:**
  - **Option A (correct, mirrors the accepted fix):** capture `var generation = _keyLayoutGeneration;`
    before line 403 and `if (generation != _keyLayoutGeneration) return;` immediately after the await,
    ahead of the `!ok` branch, the `IsSmartInfinite65Device` test and both map mutations. Three lines,
    identical in shape to the read-path fix, and testable with the `ReadGate` pattern already in
    `FakeHid` — add a `WriteGate` alongside it.
  - **Option B (broader):** hoist the guard into a small helper both methods call, so a third async entry
    point added later cannot forget it. Better long-term, slightly more churn now.

- [x] **S1. FIXED. `SmartInfiniteDeviceGate` is the branch's only concurrency primitive and has no tests.** —
  `src/ProoveGaming.Infrastructure/Classes/SmartInfiniteDeviceGate.cs` — Should-fix

  **Problem / why it matters:** the type is pure C# with no HID dependency, so unlike the transport it is
  fully unit-testable — and it is now load-bearing for four services. Its three behaviours all have
  plausible ways to regress silently: mutual exclusion per path, independence between different paths, and
  the double-dispose guard (`Interlocked.Exchange`, line 48-52) whose whole purpose is to stop a released
  permit letting two sessions run at once. A future refactor to `_gate.Release()` without the exchange
  would pass every existing test.

  **Fix:** three tests in `tests/ProoveGaming.Infrastructure.Tests` — a second `AcquireAsync` on the same
  path does not complete until the first is disposed; two different paths do not block each other; and
  disposing a releaser twice does not let a third waiter in.

#### Tests

- [ ] The M2 and M3 fixes have no automated coverage — verifying them needs hardware, correctly noted as
  out of scope. But **S1's gate tests are the testable half of M2** and should not be skipped along with
  the untestable half. Cover the gate; leave the RGB-vs-key-mapping interleaving to the manual checklist.
- [ ] The manual QA checklist has not been extended for M2: nothing there tells a tester to change the
  colour while a device page is loading, which is the exact interleaving the shared gate was added for.
  Add a step to the plan's QA section.

#### Nits

- `using System.Collections.Concurrent;` is now unused in
  `src/ProoveGaming.Infrastructure/Services/SmartInfiniteKeyMappingService.cs:1` — the
  `ConcurrentDictionary` moved out with the gate.
- `SmartInfiniteDeviceGate.For` uses `Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1))`. Under
  contention `GetOrAdd`'s factory can run more than once; the losing `SemaphoreSlim` instances are
  discarded undisposed. Harmless (no OS handle is allocated until a wait actually blocks on one) but it is
  the kind of thing a future reader will flag, so a one-line note or `LazyThreadSafetyMode` would settle
  it — `SmartInfiniteDeviceGate.cs:44-45`.
- `AcquireAsync` takes a `CancellationToken` that no caller passes, so a queued operation cannot be
  abandoned. Bounded in practice by the stream timeouts on both sides (worst case a few seconds), and it
  is the right signature for when finding 6 is addressed — noted so it is not mistaken for dead API.
- `_keyLayoutGeneration` is a plain `int` read and written only from dispatcher continuations. Correct
  today and consistent with how `_assignments` is already treated, but the invariant is implicit; the
  plan's Performance & quality section says as much about `_assignments` and could name this too.

#### What's done well

- The race tests were verified to **fail with the generation guard disabled and pass with it**. A
  concurrency test that passes either way is worse than none, and confirming the direction is the step
  most often skipped.
- `ARetryAfterAFailedRead` was caught mid-flight asserting something the factory fallback also satisfies,
  and rewritten to report slot 18 as `A` — a value reachable only from live data. The first version would
  have looked like coverage while proving nothing.
- `FakeHid.ReadKeyMatrixAsync` snapshots the matrix *before* awaiting the gate, so mutating `Matrix` to set
  up the second device cannot retroactively change what the first read returns. Without that the race
  tests would have been testing the fake.
- M3's fix removes a real divergence rather than just a duplicate: key mapping and RGB now resolve the
  same `DevicePath`, which is what makes M2's gate mean anything at all. Fixing M2 without M3 would have
  produced a gate that looked right and guarded two different interfaces.

#### Verdict

Ship: no — one blocker. B1 is the same defect the external review raised as M1, left in the sibling
method; the fix is three lines mirroring one already made, and the test harness for it already exists.
Everything else on the branch is in good shape, and M1–M3 were resolved properly.

First: 1) B1 — guard the write path, with a `WriteGate` test mirroring the stale-read ones. 2) S1 — three
unit tests for `SmartInfiniteDeviceGate`. 3) Add the "change colour during page load" step to the QA
checklist.

## Open questions

1. **Rampage.** Awaiting a vendor answer on whether key remapping can be enabled in firmware
   (Device Info byte 6 reports 0 remappable keys). Nothing in this codebase can move it forward.
2. **Which keyboard the four undocumented vendor files describe** was never confirmed by the customer.
   They proved consistent with Cyrex/Aress and were used only as corroboration, never as the source of
   truth. Worth confirming if further protocol work follows.
3. Whether the remaining Smart Infinite capabilities — macros, custom lighting `0x19`, profiles `0x30`
   — should be scoped next for this family, given profiles carry the firmware-freeze hazard.

### Round 5 — B1 and S1 closed

**B1.** The generation guard now covers `ApplyAssignmentTargetAsync`: captured before the write, checked
after, ahead of the `!ok` branch, the `IsSmartInfinite65Device` test and both map mutations. Both call
sites go through a named `IsCurrentKeyLayout(generation)` whose doc comment states the rule for any future
async method here, so the third one has something to copy rather than a bare integer comparison to
reverse-engineer.

`WriteLandingAfterADeviceSwitch_IsNotRecordedAgainstTheNewDevice` covers it, using a `WriteGate` added to
`FakeHid` beside the existing `ReadGate`. **Verified to fail with the guard disabled.**

*Correction to Round 4.* That round also claimed the `!ok` branch calling `SyncAssignmentTargetSelection()`
against the new device was "less harmful but still wrong". The test written for it passed with the guard
disabled, which is the right way to find out a finding was overstated: that method recomputes the
highlight from the *current* `_assignments` and the *current* `SelectedPhysicalKey`, so a stray call is
idempotent and produces no observable defect. The guard still short-circuits it — no reason to do work for
a device that is gone — but there is nothing there to test, and the test was deleted rather than left
green and hollow. The real defect in B1 was the `_assignments` mutation alone.

**S1.** Five tests in `tests/ProoveGaming.Infrastructure.Tests/SmartInfiniteDeviceGateTests.cs`: mutual
exclusion on one path, independence between paths, double-dispose safety, one-at-a-time admission under
eight concurrent workers, and a cancelled waiter not consuming the permit. Each test uses a fresh GUID
path, since the gate dictionary is process-wide and never cleared — shared literals would have leaked
state between tests. **`DisposingAReleaserTwice_DoesNotAdmitAnExtraCaller` was verified to fail when
`Interlocked.Exchange` is replaced with a bare `Release()`**, which is the regression it exists for.

Nit also closed: the unused `using System.Collections.Concurrent;` is gone from
`SmartInfiniteKeyMappingService`.

QA checklist gained the M2 step — change the colour while a device page is loading — which is the
interleaving the shared gate was added for and the part no automated test can reach.

Suite: **524 passing** (295 Infrastructure + 229 App), build clean.

Ship: yes.
