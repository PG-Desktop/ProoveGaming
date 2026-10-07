# Plan: Rampage — key assignments over the 2.4G receiver

## Goal

Make key remapping (and macro-to-key assignment) for the Rampage keyboard work while it is
connected through its 2.4G receiver, the same way it works over the cable. RGB over 2.4G was
delivered by [`../add-rampage-bluetooth-rgb/add-rampage-bluetooth-rgb-plan.md`](../add-rampage-bluetooth-rgb/add-rampage-bluetooth-rgb-plan.md)
as a catalog-only change (the receiver `0x056D:0xC077` passes the keyboard's `0x06` command
channel through unchanged); key assignment moves much bigger payloads — the full 384-byte key
matrix per write and the 2048-byte macro region — and the factory's clarification **C12** states
2.4G table transfers use **24-byte payload chunking** (wired uses 56), which no code implements.
Whether the wired shapes also pass through this receiver (as they did for RGB's 64-byte block) is
the open hardware question this plan settles first.

## Scope

**In:**
- Extending the `rampage-24g` probe with a `keys` arm: `0x17` table reads and a guarded
  read-modify-write-restore of one slot over the receiver, trying the wired shape first, then the
  C12 24-byte chunking; results recorded in the knowledge base.
- Whatever transport-aware chunking the probe proves necessary in `RampageProtocol` /
  `RampageKeyboardKeyMappingService` / `SmartInfiniteMacroService` (Case B below), or the
  documentation-only closure if the wired shapes just work (Case A).
- Serialising the DeviceHost's HID battery read against the shared `DeviceGate`, so the 60-second
  battery poll cannot inject frames into a multi-second radio key-table transfer.
- QA checklist for key assignment over 2.4G, including macro-on-key.

**Out:**
- Key remap over Bluetooth (firmware-level impossible — settled by the previous plan).
- The `0xAA` receiver-channel handshake (state query, VID/PID bonding) — RGB needed none of it
  and key transfers are expected not to either; only revisit if the probe fails both chunkings.
- Unifying wired ↔ 2.4G settings buckets (`MergeTransports` blocked by the receiver's foreign
  VID) — a follow-up, tracked in the previous plan's outcome.
- Other keyboards' 2.4G transports (Raster/T-Racer already have their own dongle stories).

## Background — what already lines up

The RGB work left the chain almost connected end-to-end:

- **Catalog:** Rampage has a `Dongle2_4G` transport (`VendorId "1389"`, `ProductId "49271"`,
  `SupportsConfiguration` true) — a non-Bluetooth row.
- **Host:** `DeviceKeyMappingServiceFactory.CreateForDevice(1389, 49271)` matches any
  non-Bluetooth row by VID/PID, dispatches by `Name == "Rampage"` to
  `RampageKeyboardKeyMappingService`, and `SmartInfiniteInterfaceSelector` finds the receiver's
  col05 (usage `FF87:20`, 64/64) exactly as it does for RGB. The same holds for
  `DeviceKeyboardMacroServiceFactory` → `SmartInfiniteMacroService`.
- **App:** the Key Assignments nav shows for any keyboard; `IsRampageDevice` matches by **name**
  (`RampageKeyCatalog.IsRampage(Device.Name)`) when VID/PID are not the wired pair, so the page
  loads the Rampage catalog/layout over 2.4G; reads/writes go out with the card's routing
  identity (1389/49271); write gating is `IsDeviceConfigurable`, true on this transport.
- **Gate:** `RampageKeyboardKeyMappingService` and `RampageKeyboardRgbService` share the
  `DeviceGate` keyed by DevicePath — both select the same receiver interface, so their mutual
  exclusion carries over unchanged.

So the only genuinely unknown link is the wire shape of the 336/384-byte table read/write (and
later the 2048-byte macro region) through the receiver's radio. Known specifics from the wired
investigation that must hold or be re-checked over 2.4G: replies run one command behind and are
matched by echoed address; never drain before the first request; the firmware discards transfers
whose total is not exactly 384 bytes (which is also what makes a failed experiment safe).

## Options considered

1. **Probe-first, then implement only what the hardware demands (recommended).** Extend
   `Rampage24gProbe` with a `keys` arm driving the production service where possible. Case A
   (wired shapes pass, as they did for RGB): no service code changes; close with docs + QA +
   the battery-gate hardening. Case B (24-byte chunking required): parameterise the chunk size
   through `RampageProtocol` and select it per transport in the factories.
   *Trade-off:* two possible end states; the plan carries both, and the coder stops at whichever
   the probe proves.
2. **Implement C12 chunking unconditionally for the 2.4G transport.** Skip the probe; always send
   24-byte chunks when the matched config row is `Dongle2_4G`.
   *Trade-off:* writes code the hardware may not need (RGB suggests the receiver relays wired
   shapes fine), doubles the shapes to maintain, and still needs the same hardware verification
   before anyone can trust it — the probe is not actually avoidable, only the decision is.
3. **Declare key assignment wired-only on 2.4G** (`SupportsConfiguration: false` on the dongle
   row). Rejected: that flag is per-transport, not per-feature — it would also kill the RGB
   control just delivered, and C12 explicitly documents a 2.4G table path, so "impossible" is not
   established.

## Recommended approach

Option 1, in two stages:

**Stage 1 — probe (`rampage-24g keys`, hardware present).** In `Rampage24gProbe`:
- **K1 (read-only):** read the current key matrix (`0x17`) through the receiver with the wired
  shape (6 × 56 full packets, address-matched replies). Validate with the existing plausibility
  gate (`RampageProtocol.IsPlausibleKeyTable`) and the FN canary (slot 90 = `60-01-00`). If the
  wired shape fails, retry with 24-byte read chunks. Also read `0x16` once for comparison.
- **K2 (`write` arg):** drive the production `RampageKeyboardKeyMappingService.SetKeyMappingAsync`
  against the receiver interface for slot 33 (Q → W), verify by read-back, prompt the human to
  press the key, then **restore Q and verify again — restore runs whatever the outcome**, exactly
  like `SmartInfiniteProbe.WriteTest`. If the production shape is acked-and-discarded, repeat the
  read-modify-write hand-rolled with 24-byte write chunks (16 × 24 = 384 exactly).
- Record everything in `wireless-receiver-protocol.md` (new "key matrix over 2.4G" section).
- Macro region: **read-only check only** (`0x2C`/macro-read equivalent the macro service uses) to
  see whether the region answers over 2.4G; macro *writes* are exercised via the App during QA
  once key writes are proven, not by the probe — the macro buffer format is already
  hardware-verified on this family and the write discipline is the same full-region transfer.

**Stage 2 — implementation, per probe outcome.**

*Case A — wired shapes pass (expected, by analogy with RGB):* no transport code changes.
Remaining work is hardening and truth-telling:
1. **Battery vs. table-transfer race:** `HidDeviceDiscoveryService.TryReadBattery` writes command
   frames to the same col05 interface with no coordination; on 2.4G the card has
   `HasBattery=true` (dongle) so `PollBatteriesAsync` hits it every 60 s while a key write takes
   whole seconds over the radio. Wrap the battery exchange for a given device path in
   `DeviceGate.AcquireAsync(path)` — the same gate the key/RGB services hold, so a battery poll
   waits out a table transfer instead of splicing into it. (Wired Rampage never battery-polls —
   `HasBattery` is false for wired cards — which is why this race never mattered before.)
2. Update the stale remark on `RampageKeyCatalog.IsRampage(string)` ("key writes still require
   the wired vendor interface") to name the 2.4G path.
3. Docs: findings section + `docs/CONTEXT.md` term updates as needed.

*Case B — 24-byte chunking required:* additionally
4. `RampageProtocol.EnumerateChunks` / `EnumerateWriteChunks` take a `maxPayload` parameter
   (default 56, so every existing caller and test is untouched); write chunking must still total
   exactly 384 (16 × 24).
5. `RampageKeyboardKeyMappingService` takes the chunk size via constructor;
   `DeviceKeyMappingServiceFactory` passes 24 when the matched row's
   `ConnectionType == "Dongle2_4G"`, 56 otherwise. Same wiring for `SmartInfiniteMacroService`
   via `DeviceKeyboardMacroServiceFactory` (macro region 2048 = 86 × 24 chunks — verify the
   exact tail packet against the probe result, and confirm the macro region's own "full declared
   length" rule with a hardware macro-assign during QA).

No proto changes, no new NuGet packages, no new projects, no installer impact either way.

## File-level change list

Ordered: probe and its registration first, protocol tests before any chunking change, config-time
code after the probe verdict.

| # | File | Intent |
|---|------|--------|
| 1 | `src/ProoveGaming.Device.Testing/Rampage24gProbe.cs` (edit) | Add the `keys` arm: K1 table reads (both chunkings), K2 guarded slot write + restore via the production service, macro-region read check. |
| 2 | `src/ProoveGaming.Device.Testing/Program.cs` (edit) | Register `rampage-24g keys [write]`; update the header comment and the WRITES table. |
| 3 | `docs/knowledge-base/smart-infinite-protocol/wireless-receiver-protocol.md` (edit) | New "Key matrix over 2.4G" section recording the probe run (which chunking, ack behaviour, physical-key result). |
| 4 | `tests/ProoveGaming.Infrastructure.Tests/RampageProtocolTests.cs` (edit, Case B only) | Pin `EnumerateChunks`/`EnumerateWriteChunks` with `maxPayload: 24` — 16 packets, exact 384 total, correct tail; default parameter keeps every existing expectation green. |
| 5 | `src/ProoveGaming.Infrastructure/Classes/RampageProtocol.cs` (edit, Case B only) | `maxPayload` parameter on both chunk enumerators (default 56). |
| 6 | `src/ProoveGaming.Infrastructure/Services/RampageKeyboardKeyMappingService.cs` (edit, Case B only) | Chunk-size constructor parameter used by `ReadTableOnStream`/`WriteTableOnStream`. |
| 7 | `src/ProoveGaming.Infrastructure/Services/DeviceKeyMappingServiceFactory.cs` (edit, Case B only) | Pass 24 for a `Dongle2_4G`-matched row; also fixes the row lookup comment. |
| 8 | `src/ProoveGaming.Infrastructure/Services/SmartInfiniteMacroService.cs` + `DeviceKeyboardMacroServiceFactory.cs` (edit, Case B only) | Same chunk-size wiring for the macro region. |
| 9 | `src/ProoveGaming.Infrastructure/Services/HidDeviceDiscoveryService.cs` (edit, both cases) | Take `DeviceGate` for the candidate device path around the battery write/read exchange. |
| 10 | `tests/ProoveGaming.Infrastructure.Tests/` (new/extend, both cases) | Coverage for the battery-gate change at whatever seam is mockable (see Test strategy). |
| 11 | `src/ProoveGaming.App/Models/Keyboards/RampageKeyCatalog.cs` (edit, both cases) | Refresh the "wired-only remap" remark on the name-fallback doc comment. |
| 12 | `docs/CONTEXT.md` (edit) | Update the [[Matrix slot]]/receiver-channel entries if the probe adds new facts (24-byte tail shape etc.); no new terms expected. |

## Acceptance criteria

- [ ] Given the Rampage on 2.4G (receiver plugged, no cable) and the probe run
  `rampage-24g keys write`, the knowledge base records: which chunking reads `0x17` correctly
  (FN canary at slot 90), whether the production write persisted, whether the physical key
  emitted the new scancode, and that the original mapping was restored.
- [ ] Given Rampage on 2.4G, when the user opens Key Assignments, then the layout loads from the
  device (populated slots, no "could not read the layout" error) within a few seconds.
- [ ] Given Rampage on 2.4G, when the user remaps a key (e.g. Q → W) in the dialog, then the
  physical key types the new character immediately, without replugging, and the change survives
  a keyboard power cycle.
- [ ] Given Rampage on 2.4G, when the user assigns a recorded macro to a key, then pressing the
  key plays the macro.
- [ ] Given Rampage on the cable, key assignment behaves exactly as before this change (same
  packet shapes on the wire — existing `RampageProtocolTests` and the key-mapping tests stay
  green with unchanged expectations).
- [ ] Given a key-table transfer in flight over 2.4G, a concurrent battery poll does not corrupt
  it: with the battery gate in place, `dotnet test` covering the gate passes, and QA step 8
  (rapid remap while battery indicator visible) shows no spurious "could not read the layout".
- [ ] Given the App on a transport where the read genuinely fails (e.g. receiver unplugged
  mid-session), the existing misalignment/plausibility guards still refuse the write — no
  scrambled table is ever written back (probe K2's restore path proves the guard fires).

## Test strategy

Change to existing behaviour (`add-*` on a new transport for an existing feature). The touched
feature already has tests — `RampageProtocolTests` (framing, chunk enumeration, checksum,
plausibility gate) and `SmartInfiniteKeyMappingServiceTests` — which must stay green with
**unchanged** expectations; Case B extends them rather than editing them.

- **`tests/ProoveGaming.Infrastructure.Tests/RampageProtocolTests.cs`** (Case B): new cases for
  `EnumerateChunks(dataSize, maxPayload: 24)` and `EnumerateWriteChunks(maxPayload: 24)` — packet
  count 16, offsets 0/24/…/360, lengths all 24, total exactly 384; and the default-parameter
  overloads still produce today's 6×56 / 6×56+48 shapes (characterization, written first).
- **Battery gate (both cases):** the battery read lives in `HidDeviceDiscoveryService` against
  real `HidDevice` handles, which unit tests cannot construct. Test at the seam that IS mockable:
  if the gate acquisition is extracted as a small internal hook (mirroring how `DeviceGate` is
  already tested in `DeviceGateTests`), pin that a battery exchange for path P waits on
  `DeviceGate.AcquireAsync(P)`; otherwise extend `DeviceGateTests` with the contention scenario
  (holder + battery-waiter) and rely on QA step 8 for the integration.
- **Factory dispatch:** the dongle row resolving to the Rampage key service is inline
  `FirstOrDefault` + name dispatch; a service-type assertion needs real HID devices, so cover the
  row-selection contract only if Case B touches the factory anyway (then extract/pin it the same
  way `DeviceRgbServiceFactory.SelectConfigRow` was).
- **Deliberately not covered by automated tests:** everything requiring the physical receiver —
  radio transfer timing, ack lag through the relay, the physical key emitting the new scancode,
  macro playback. That is stage 1's probe plus the QA checklist.

## QA (manual test steps)

1. Plug the Rampage 2.4G receiver into a USB port, switch the keyboard to 2.4G mode, unplug its
   cable. Launch the Proove Gaming app. **Expected:** the Rampage card shows as connected via
   dongle.
2. Open the Rampage card → Key Assignments. **Expected:** the keyboard layout appears with the
   current assignments loaded (no error banner), within a few seconds.
3. Click the Q key, assign W to it. **Expected:** the app confirms the change; typing the
   physical Q key in any text editor now produces "w" immediately — no replug needed.
4. Power the keyboard off and on (still 2.4G), wait for it to reconnect. **Expected:** the
   remapped key still types "w".
5. Restore Q back to Q in the app. **Expected:** the physical key types "q" again.
6. Record a short macro (e.g. types "abc") and assign it to a side key. **Expected:** pressing
   that key plays the macro.
7. Change an RGB colour while on 2.4G. **Expected:** lighting still updates (no regression from
   the key-assignment work — both features share one channel).
8. With the battery indicator visible on the card, remap three different keys in quick
   succession. **Expected:** every remap lands; no "could not read the layout" error appears.
9. Plug the cable in (keyboard still on the receiver's dongle in another port). **Expected:** the
   card switches to cable; key assignment keeps working as it always did over the cable. Note:
   assignments saved during the 2.4G session may not be listed under the cable session — wired
   and 2.4G currently keep separate saved settings; this is a known limitation, not a bug.
10. Regression — wired Cyrex/Aress/T800/Raster/T-Racer key assignment unchanged.
11. During all steps the app stays responsive; a remap over 2.4G may take visibly longer than
    over the cable (radio), but the UI must not freeze.

## Review focus

1. `thread-safety` — the battery-poll vs. table-transfer race and the `DeviceGate` scope; no new
   lock ordering against the gate the key/RGB services already hold.
2. `performance hot-path` — table transfers over the radio take seconds; nothing may block the
   UI thread or the gRPC event loop while they run (existing `Task.Run` pattern preserved).
3. `async/await` — cancellation still flows through the retry/read loops after any chunk-size
   parameterisation.
4. `null-safety` — probe-dependent code paths must degrade to today's behaviour when the
   receiver row/interface is absent.

## Performance & quality

- A 384-byte write is 7 packets (Case A) or 16 (Case B) plus read-before-write of 6–16 packets,
  each with a receiver round-trip — over the radio this is seconds, not milliseconds. The
  existing service already runs transfers on `Task.Run` off the caller thread and holds the
  `DeviceGate` for the duration; that pattern must not change. No per-frame/hot-path code is
  touched.
- Chunk enumerators allocate one small list per transfer — unchanged; do not "optimise" the
  parameterisation into shared mutable state.
- The battery gate adds one semaphore acquisition per battery read (every 60 s) — negligible, and
  it removes a correctness race rather than adding contention.

## Risks

1. **The receiver may relay only some commands.** RGB (`0x15`, 64 B) passing does not guarantee
   `0x17`/`0x18` (336/384 B) pass; C12's 24-byte chunking exists for a reason. The probe's
   read-first design and the firmware's discard-on-wrong-total rule make every experiment
   recoverable; the restore step runs regardless of outcome.
2. **Reply lag through the relay may differ from wired.** The service already matches
   replies/acks by echoed address, never by order — that must be preserved verbatim in any Case B
   variant; positional reading is the documented historical bug.
3. **Macro region over 2.4G is bigger (2048 B)** and only QA-exercised in this plan; if it fails
   while key writes succeed, macro-on-key over 2.4G degrades to a reported error, and a follow-up
   applies the same chunking fix with its own probe run.
4. **Settings-bucket split** (wired vs 2.4G identities) also applies to per-key saved selections
   and `key_macro_bindings` — accepted here, same as for RGB; the merge follow-up covers both.
5. **`0x20`/`0x31` remain forbidden** — the probe arm must never send them (reset/factory-wipe
   history on this board).

## Open questions

1. None blocking stage 1. For stage 2, only the probe's verdict (Case A vs B) decides the
   implementation set — no user decision needed.
2. For the product: should battery polling over 2.4G stay if QA finds the indicator unreliable?
   (Tracked from the RGB plan; not a blocker here once the gate serialises the exchanges.)

## Review findings

### Round 1 — Ship: yes

#### Summary
Branch `features/Rampage-24-bt`, single commit `a73d753`, clean tree; the delta spans this plan
plus the preceding RGB/Bluetooth plan (that half was reviewed in the RGB plan's journal, Round 1,
its S1–S3 closed). This plan's implementation: the `rampage-24g keys` probe proved Case B on
hardware (56-byte table transfers arrive misaligned / are refused over the receiver; 24-byte
chunking reads 96/96 with the FN canary intact and a production-service write with
`maxPayload=24` persisted, was read-back-verified and restored); `RampageProtocol` /
`SmartInfiniteProtocol` chunk enumerators gained a `maxPayload` parameter (defaults keep the
wired shapes byte-identical), threaded through `RampageKeyboardKeyMappingService`,
`SmartInfiniteTransport`, `SmartInfiniteMacroService` and both factories via
`TablePayloadFor(config)`; the battery poll now yields to the shared `DeviceGate`
(`TryAcquire` with a 2-s timeout) instead of splicing into radio transfers. Build clean, 643 +
764 tests green (+19 new).

#### Plan conformance
Plan: this document.
- Implemented as planned: file-list items 1–11 all present; stage-1 probe run on real hardware
  and recorded in `wireless-receiver-protocol.md`; Case B implemented exactly as specified.
- Deviates from plan (improvement, surfaced for the record): K2's fallback is not a hand-rolled
  24-chunk write but the **production service constructed with `maxPayload=24`** — a strictly
  stronger proof, since the persisted write ran the exact retry/gate/verify path the DeviceHost
  executes, including its 500 ms per-packet stream timeouts over the radio.
- Missing / partial: item 12 (`docs/CONTEXT.md`) deferred with justification — the plan itself
  expected no new terms and the receiver-channel glossary entry already documents the 24-byte
  chunking. Agreed; no action.
- Accepted limitation (per plan): macro **writes** over 2.4G (2048 B, 85×24+8 tail) are enabled
  but hardware-unverified — residual risk is a graceful, reported upload failure (full-length
  rule discards partial transfers; the verify-read is authoritative), covered by QA step 6.
  Settings-bucket split wired↔2.4G (risk 4) and the battery-value semantics over the receiver
  remain accepted carries from the RGB plan.

#### Code
- [ ] **S1. A busy device gate during the connect-time battery acquire can permanently mark the
  2.4G card as no-battery** — `src/ProoveGaming.App/ViewModels/HomeViewModel.cs:683`
  (`RefreshBatteryAsync` → `MarkNoBattery`) interacting with
  `src/ProoveGaming.Infrastructure/Services/HidDeviceDiscoveryService.cs` (`TryGetBatteryLevel`
  gate skip) — Should-fix
  **Problem:** the new gate skip makes a battery read return `-1` when col05 is busy — correct
  in itself, but `RefreshBatteryAsync` runs exactly at connect time, in 3 attempts spaced 3 s,
  and a 2.4G (re)connect is precisely when `ReapplyRgbOnConnect` + `ReapplyLastCustomPresetAsync`
  hold that same gate for multi-second radio transfers. col05 is the only candidate that can
  actually answer a battery query, so "busy three times" is indistinguishable from "no battery",
  and `_knownNoBattery.TryAdd` then suppresses the 60-s poll **for the rest of the session** —
  the battery indicator never appears until an app restart.
  **Why it matters:** timing-dependent but structural: the reapply chain and the battery acquire
  are triggered by the same connect event, so the collision is not rare bad luck.
  **Solution options:**
  - Option A (host, small — recommended): in `TryGetBatteryLevel`, track whether any candidate
    was skipped gate-busy; if so and no reading was obtained, sleep ~300 ms and retry the busy
    candidates once or twice (bounded). The connect-time burst finishes within a few seconds, so
    one delayed retry converts "busy" into a real answer.
  - Option B (App): don't `MarkNoBattery` from the connect-time acquire when the card connected
    within the last ~30 s (the reapply window); let the 60-s poll make the permanent call.
    Cheaper but leaves `_knownNoBattery` semantics split across two call sites.
- [ ] **S2. `TablePayloadFor` lives on the key-mapping factory but is transport policy** —
  `src/ProoveGaming.Infrastructure/Services/DeviceKeyMappingServiceFactory.cs:150` — Should-fix
  (borderline nit, promoted because a third caller is foreseeable)
  **Problem / why it matters:** `DeviceKeyboardMacroServiceFactory` now reaches into its sibling
  factory for the transport→chunk mapping. The mapping is a property of the Smart Infinite
  transport, not of key mapping; the next service that needs it (actuation, if a 2.4G magnetic
  board ever ships) will either deepen the cross-factory dependency or fork the logic.
  **Fix:** move it to `SmartInfiniteProtocol` (e.g. `PayloadForConnectionType(string?)`) next to
  the two constants it selects between; both factories and the test keep working with a rename.

#### Tests
- [ ] The `RampageKeyboardKeyMappingService` payload plumbing (ctor → both enumerator call
  sites) has no unit test — HidSharp devices cannot be constructed, and the probe covered it
  end-to-end on hardware via the production service. Accepted; noted so nobody mistakes the
  enumerator tests for covering the service wiring.
- [ ] Battery-gate integration (S1 scenario) is manual-only — QA step 8 plus, if S1 Option A
  lands, a `DeviceGateTests`-style contention test for the retry-after-busy path.

#### Nits
- `Rampage24gProbe.RunAsync` lists the same VID/PID twice under "Non-Bluetooth vendor-usage
  candidates" (col05 and the input-only col06 companion) — cosmetic; a suffix naming the
  collection would make probe transcripts clearer — `src/ProoveGaming.Device.Testing/Rampage24gProbe.cs`.
- `SmartInfiniteMacroService`'s new `maxPayload` ctor parameter is undocumented while the
  neighbouring services grew XML remarks for theirs — one sentence would keep the pattern
  uniform — `src/ProoveGaming.Infrastructure/Services/SmartInfiniteMacroService.cs:60`.

#### What's done well
- The probe drove the **production** service for both framings — the persisted 24-chunk write is
  proof of the exact code path the DeviceHost runs, not of a parallel reimplementation; and the
  restore-regardless-of-outcome discipline was kept.
- Default-parameter design keeps every wired shape byte-identical, and the tests pin that from
  both directions (new 24-shapes asserted, defaults re-asserted, out-of-range payloads rejected).
- The plausibility gate earned its keep live: the misaligned 56-byte read over the radio was
  refused before it could become the basis of a whole-table write — exactly the failure the
  guard was built for.
- `DeviceGate.TryAcquire` yields instead of queueing — the right priority inversion for
  housekeeping traffic — and its test proves it takes the same permit `AcquireAsync` uses.

#### Verdict
Ship: yes — no blockers; S1 is the only finding with user-visible impact and is a bounded,
host-side fix; S2 is a placement cleanup. First:
1. S1 — retry gate-busy battery candidates once inside `TryGetBatteryLevel`.
2. S2 — move `TablePayloadFor` onto `SmartInfiniteProtocol`.
3. Run QA steps 2–6 (remap, power-cycle survival, macro-on-key) on the physical board via the
   App — the last unproven links are App-UI-level, not protocol-level.
