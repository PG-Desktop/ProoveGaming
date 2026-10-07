# fix-keyboard-qa-findings — QA findings batch: key dialog flow, tooltips, RGB fidelity, 2.4G RGB framing

> **2026-08-14 — Note:** the macro badge and the tooltip suppression this plan describes
> (findings 4/6, the "no tooltip over the badge's key" acceptance criterion) were later removed
> by [`remove-macro-list-selection`](../remove-macro-list-selection/remove-macro-list-selection-plan.md):
> the badge is gone and the hover tooltip now shows on every key, selected or not, on both the
> Macros and Key Assignments sections.

> **Status: retrospective.** This plan documents work that is **already implemented and
> committed** on branch `bugfix/PG-381` (commits `d9b9434`…`d139865`, tickets PG-561,
> PG-563, PG-568, PG-569, PG-581 plus two follow-ups reported in review). It exists so the
> branch has the standard plan artifact for `/reviewer` and for the QA hand-off; `/coder`
> has nothing left to implement unless review findings send work back.

## Goal

Close out a QA pass over the keyboard device-details experience (Cyrex/Rampage focus): the
per-key assignment dialog gave no feedback on macro picks, stale UI survived remap/unbind
operations, hover tooltips lied about remapped keys or duplicated the macro badge, the
on-screen RGB preview falsified the picked colour, and a static colour written to a Rampage
over its 2.4G receiver arrived with shifted colour channels. Each finding is a small,
independent fix; together they make the keyboard pages tell the truth about device state.

## Scope

**In**

1. **Notification toast width** (`MainWindow`): the auth/session toast clipped long
   Ukrainian strings at its fixed 432 px width.
2. **Key dialog MACROS pick commit flow**: clicking a macro in the per-key dialog applied
   the binding but left the dialog open with no feedback; the popup must close immediately
   (optimistically, before the ~0.5 s device write) and raise the undo toast only after a
   successful write.
3. **Macro binding cleared on KEYS pick**: remapping a macro-bound key to a plain key left
   the Macros page radio list claiming the macro was still assigned.
4. **Remap hover tooltip**: the key-overlay tooltip always showed the key's own label; it
   must show what the key currently resolves to (remap target label or bound macro name).
5. **Tooltip glow**: the tooltip's drop-shadow glow was invisible over the black keyboard
   body (visible only for the top row, whose tip lands on the lighter backdrop).
6. **Macro badge vs tooltip**: on the Macros section the selected key already carries a
   static macro badge; the hover tooltip on that same key duplicated it and must be
   suppressed there (Key Assignments unchanged).
7. **RGB preview colour fidelity**: the preview brush ran the picked colour through an HSV
   round-trip with a +0.3 saturation boost — white at 60 % brightness rendered brown.
8. **Rampage 2.4G RGB framing**: the `0x15` feature-area write always used the wired
   56 + 8 chunking; over the 2.4G receiver (which relays 24-byte payloads per factory
   clarification C12) a static white rendered cyan. The write is now chunked at the
   transport's payload size.
9. **Neon / ColorCycle preview swap** *(added 2026-08-14 from the follow-up QA pass)*: the
   mode bytes sent to the device are correct — verified 1:1 against the vendor app's
   `standardBackLightEffect` table, now recorded in
   `docs/knowledge-base/smart-infinite-protocol/backlight-modes.md` — but the App's two
   preview animations were attached to the wrong effects. The physical Neon (mode 2, has a
   direction) is a flowing rainbow, and the physical Spectrum/«Зміна кольору» (mode 8, no
   direction, no colour choice) cycles the whole board in unison; the previews said the
   opposite, and ColorCycle's preview also duplicated Wave's. The two preview timers are
   swapped (`Neon` → rainbow gradient, `ColorCycle` → solid hue cycle); device bytes
   untouched.
10. **Rampage brightness scale** *(added 2026-08-14 from the follow-up QA pass)*: the
   Rampage service mapped the slider into the doc's 0–99 `light_level` range, but this
   family's firmware reads the field as the discrete 0–4 scale (the Fn+↑/↓ steps) —
   the exact non-monotonic quirk Aress/Cyrex already fixed — so a 100 % slider (level 99)
   lit the board visibly dimmer than the vendor app. Rampage now uses the same 0–4
   mapping as its siblings (Off forces 0; any non-zero slider ≥ level 1).

**Out**

- Any change to what is sent to devices over the **wired** transport (byte-for-byte
  unchanged everywhere).
- Aress/Cyrex/Raster/T-Racer/T800 RGB services (no 2.4G transport rows today).
- The macro badge design, the Bluetooth read-only stance for Rampage, and the vendor-side
  receiver protocol questions tracked in
  `docs/knowledge-base/rampage-keyboard-protocol/bluetooth-findings.md`.
- Mouse (hotspot) macro flows — only keyboard paths were touched.

## Options considered

Only the two decisions with real alternatives are recorded; the rest are one-line bug
fixes with no meaningful design space.

**(a) Dialog macro pick: await-then-close vs optimistic close.**
- *Await the device write, then close* keeps "dialog open" as the failure signal, but on
  the immediate-write family (library upload + 225-byte matrix RMW, ~0.5 s measured) the
  dialog visibly hangs — the exact QA complaint. Rejected.
- *Close immediately, commit in background* (chosen): same optimism the Macros page radio
  list already has; the undo toast still waits for the write result, so it cannot claim a
  remap that never landed. Cost: a refused write now surfaces only as the radio re-syncing
  back and (for upload failures) the existing warning banner, not as a still-open dialog.

**(b) Rampage 2.4G colour bug: re-probe first vs adopt the documented receiver framing.**
- *Probe the receiver with a white static write before changing code* is the
  highest-certainty path but blocks the fix on hardware access.
- *Chunk the RGB write at the transport payload size* (chosen): C12 documents 24-byte
  relay payloads as the receiver's native framing; the 2026-08-13 hardware probe verified
  the key-matrix path **requires** it (56-byte shapes arrive silently misaligned) and that
  a bare 24-byte `0x15` transfer persists. The wired path keeps the exact historical
  56 + 8 sequence, so the risk is confined to the transport that is currently broken
  anyway. Residual risk: if QA still sees cyan, the misalignment lives elsewhere and a
  targeted probe (`rampage-24g` arm of `ProoveGaming.Device.Testing`) is the next step.

## Recommended approach

As implemented — ten independent, minimal fixes routed through the mechanisms the
codebase already has:

- The toast becomes auto-width (`MinWidth` 432) with the chamfered background rebuilt as a
  three-column grid (fixed 16 px corner pieces, stretching middle).
- Dialog macro picks route through the existing `CommitRemapPickAsync` (the KEYS/SYSTEM
  commit point), with the dialog closed *before* the apply and the in-flight gate left in
  charge of mid-write picks; `CommitRemapPickAsync` gains the `RefreshMacroListSelection()`
  call so KEYS/SYSTEM picks also un-check a displaced macro row.
- The tooltip resolves a key's current assignment via a new `GetKeyRemapTooltip` that
  reuses the highlight's per-family truth sources (`_slotKeyOverrides`/`_assignments` on
  the immediate family, `_stagedKeyOverrides` on the staging family), with
  `BoundMacroForSelectedKey` generalised to `BoundMacroForKey`.
- The tooltip glow becomes `#8B82FF` at opacity 1 (brighter than the badge's
  `#6357FF`/0.5) so it reads on the black board; the tip is suppressed when the hovered
  key is the selected key whose macro badge is showing.
- `WithBrightness` becomes a plain linear channel scale (no HSV round-trip, no saturation
  boost).
- `RampageKeyboardRgbService` takes a `maxPayload` (defaulting to the wired 56) and chunks
  the `0x15` feature area at that size; `DeviceRgbServiceFactory` resolves the value from
  the config row via the key-mapping factory's existing `TablePayloadFor`.

## File-level change list

All files already modified on the branch; listed with intent for review.

| File | Intent |
|------|--------|
| `src/ProoveGaming.App/Views/MainWindow.axaml` | Toast auto-width (`MinWidth` 432); chamfered background as 16-*-16 grid so the middle stretches; right content margin clears the close button. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyDialog.cs` | `CommitRemapPickAsync`: doc updated to cover macro picks; on success also `RefreshMacroListSelection()` so a displaced macro row un-checks. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` | `AssignMacroToSelectedKeyAsync`: dialog-open picks close the dialog optimistically then commit via `CommitRemapPickAsync`; `BoundMacroForKey` generalisation; new `GetKeyRemapTooltip`. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Rgb.cs` | `WithBrightness` = linear channel scale; removes the HSV round-trip + saturation boost that turned white brown. *(2026-08-14)* Neon/ColorCycle preview timers swapped to match the physical effects. |
| `docs/knowledge-base/smart-infinite-protocol/backlight-modes.md` | *(2026-08-14)* Vendor `standardBackLightEffect` mode table extracted from Keyborder — the written source for the family's `light_mode` values. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml.cs` | Tooltip label = current assignment (`GetKeyRemapTooltip` result) or own label; width estimate follows; tip suppressed over the visible macro badge's key. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml` | Tooltip glow `#8B82FF`/opacity 1 so it reads over the black keyboard body. |
| `src/ProoveGaming.Infrastructure/Services/RampageKeyboardRgbService.cs` | `maxPayload` ctor param; `0x15` feature area chunked at the transport payload size (wired 56+8 unchanged; 2.4G → 24+24+16). *(2026-08-14)* brightness mapped to the family's discrete 0–4 `light_level` scale instead of 0–99. |
| `src/ProoveGaming.Infrastructure/Services/DeviceRgbServiceFactory.cs` | Rampage RGB service resolved with `DeviceKeyMappingServiceFactory.TablePayloadFor(config)`. |
| `tests/ProoveGaming.App.Tests/DeviceDetailsKeyAssignmentDialogTests.cs` | New/updated coverage for items 2–4 (see Test strategy). |

## Acceptance criteria

- [ ] Given a toast message longer than 432 px at 16 px Segoe UI (e.g. the Ukrainian
      password-reset confirmation), when the toast shows, then the full text and the ✕
      button are visible with no clipping, and messages shorter than 432 px render in a
      432 px-wide toast as before. A message that would exceed 720 px (e.g. an unbounded
      backend `result.Message`) ellipsizes inside the chip instead of growing past the
      window edge (PR #101 M2).
- [ ] Given the per-key dialog open on the MACROS tab with a configurable keyboard, when a
      macro row is clicked, then the dialog closes on the same UI tick (before any device
      I/O completes) and the "successfully remapped" undo toast appears only after the
      device write succeeds.
- [ ] Given the device refuses the key write, when a macro is picked from the dialog, then
      no undo toast appears, the macro radio list returns to the unbound state, and the
      dialog comes back up — the reopened dialog is the refusal signal (PR #101 M3).
- [ ] Given a first pick's device write is still in flight, when a second macro pick
      arrives from the (re-opened) dialog, then the pick is ignored and that dialog stays
      open.
- [ ] Given a key bound to a macro, when the key is remapped to any plain key (including
      its own record) from the dialog's KEYS tab, then the macro's `key_macro_bindings`
      row is deleted and the Macros page radio list shows no macro selected.
- [ ] Given a key remapped to another key, when it is hovered on Key Assignments or
      Macros, then the tooltip shows the **target** key's label; an untouched key shows
      its own label; a macro-bound key shows the macro's name.
- [ ] Given the Macros section with a macro bound to the selected key (badge visible),
      when that same key is hovered, then no hover tooltip appears; hovering any other
      key still shows one. Key Assignments tooltips are unaffected.
- [ ] Given static effect, colour `#FFFFFF`, brightness 60 %, when the preview renders,
      then the backlight brush is `#999999` (linear scale — no hue shift); for any colour
      the preview hue/saturation equal the picked colour's.
- [ ] Given a Rampage on the 2.4G receiver (VID 1389 / PID 49271), when a static colour is
      applied, then the `0x15` write goes out as 24 + 24 + 16 payload chunks; over the
      wired transport the byte sequence is unchanged (56 + 8, same commands, same delays).
- [ ] Given a Rampage on any transport, when brightness is applied, then the `0x15`
      block's `light_level` byte is on the 0–4 scale: 100 % → 4, 60 % → 2, any non-zero
      slider ≥ 1, Off/0 % → 0 — never a 0–99 value.
- [ ] `dotnet test ./src/ProveGaming.slnx` passes (App: 780, Infrastructure: 643 at time
      of writing).

## Test strategy

Bug-fix batch → each behavioral fix carries a test that fails on the pre-fix code. All
implemented in `tests/ProoveGaming.App.Tests/DeviceDetailsKeyAssignmentDialogTests.cs`
(items 2–4); they exist and pass on the branch:

- `MacroBinding_FromDialog_ClosesDialog_AndRaisesUndoToast` — Raster (staged family):
  dialog macro pick closes the dialog, raises the toast, picker follows the target list.
- `MacroBinding_FromDialog_OnCyrex_WritesDeviceAndClosesDialog` — immediate-write family:
  device write recorded, dialog closed, toast up.
- `MacroBinding_FromDialog_RefusedWrite_ClosesWithoutToast` — refused `SetKeyMapping`:
  optimistic close, **no** toast, radio re-synced to unbound.
- `MacroBinding_FromDialog_MidWritePick_IsIgnored_AndKeepsDialogOpen` — in-flight gate:
  a pick during a pending write neither closes the dialog nor double-writes.
- `KeyPick_OverMacroBoundKey_ClearsTheMacroBinding` — KEYS pick over a macro-bound key
  deletes the store row and un-checks the radio.
- `KeyRemapTooltip_NamesTheStagedTarget_OnRaster` / `KeyRemapTooltip_NamesTheLiveTargetOrMacro_OnCyrex`
  — `GetKeyRemapTooltip` per family, macro-name case, and null for untouched keys.
- The pre-existing `MacroBinding_KeepsDialogOpen_AndPickerFollowsTheTargetList` was
  **deliberately rewritten** (old behavior was itself the bug).

Deliberately **not** covered by automated tests:

- Toast layout, tooltip glow colour, tooltip suppression, preview brush value — Avalonia
  visual-tree/rendering behavior with no headless harness in this repo; covered by QA
  steps below. (`WithBrightness` is private and its observable surface is a brush; if a
  regression risk emerges, expose it `internal` and pin `#FFFFFF`@0.6 → `#999999`.)
- `RampageKeyboardRgbService` chunking — the service writes through a concrete
  `HidSharp.HidDevice`; there is no HID abstraction seam in
  `tests/ProoveGaming.Infrastructure.Tests` today, and introducing one for this fix was
  judged out of proportion. Verified by hardware QA (step 8 below); the wired path is
  provably unchanged by construction (`maxPayload` defaults to 56 → identical 56 + 8
  split).

## QA (manual test steps)

1. Launch the App, log out if needed, go to "Forgot password", submit a valid e-mail with
   the app language set to Ukrainian. **Expected:** the green confirmation toast at the
   top-right shows the entire message and the ✕ button; nothing is cut off at the right
   edge.
2. Connect a Cyrex over USB. Open its page → МАКРОСИ. Create a macro (e.g. "qwertt").
   Click a key (e.g. Z) — the key dialog opens. Open the МАКРОСИ tab and click the macro.
   **Expected:** the popup closes immediately (no visible delay), a toast "Клавішу Z
   успішно перепризначено на qwertt" with СКАСУВАТИ appears, and pressing Z on the
   physical keyboard plays the macro.
3. In the macro list on the Macros page, confirm the macro's radio is checked for that
   key. Click the same key again, switch to the КЛАВІШІ tab, and click the same key in
   the picker (remap Z to Z). **Expected:** the popup closes, a toast appears, pressing Z
   types "z", and the macro list no longer shows the macro as assigned to Z.
4. On ПРИЗНАЧЕННЯ КЛАВІШ, remap R to T via the dialog. Hover R on the on-screen keyboard.
   **Expected:** the tooltip shows "T" (the key it now types), with a clearly visible
   glow around the tooltip chip on every keyboard row, not just the top row. Hover an
   untouched key — its own name shows.
5. On МАКРОСИ with a macro bound to the selected key (the black macro chip is visible
   next to the key), hover that same key. **Expected:** no second tooltip appears over
   the chip. Hover a different key — the tooltip works.
6. On RGB ПІДСВІТКА, select Статичний, colour white `#FFFFFF`, brightness 60 %.
   **Expected:** the on-screen keyboard preview glows a neutral dimmed white/grey — not
   brown, not any tinted colour. Spot-check two more colours (pure red, a pastel) — the
   preview hue matches the picker exactly, only dimmer with lower brightness.
7. Repeat step 6 while watching the **physical** Cyrex/Rampage over USB. **Expected:**
   LEDs show the picked colour (white stays white).
8. Switch a Rampage to its 2.4G receiver (cable unplugged), restart the DeviceHost
   service, open the Rampage page → RGB, select Статичний white. **Expected:** the
   physical LEDs are white, not cyan. Repeat with red and blue. Then re-test key
   remapping and macro assignment over 2.4G once — both still work.
9. Regression: on a wired Rampage, apply static `#7620EE` at 60 %. **Expected:** LEDs
   match, exactly as before this branch.
10. On a Rampage (wired or 2.4G), set static red and drag Яскравість from 100 % down to
    0 % and back. **Expected:** at 100 % the board is as bright as in the vendor app;
    the level steps down visibly as the slider drops (about five distinct steps), any
    non-zero position keeps the backlight on, and 0 % turns it off.
11. On a Rampage, select Неон, then Зміна кольору, then Хвиля, watching the on-screen
    preview against the physical board. **Expected:** Неон — both show a rainbow flowing
    **top to bottom**; Зміна кольору — both show the whole board cycling through colours
    in unison (no movement across keys); Хвиля — rainbow flowing **along the key rows**
    as before. No effect's preview duplicates another effect's physical look.

## Review focus

- `async/await` — the optimistic dialog close vs the in-flight `_assignmentWriteInFlight`
  gate; the fire-and-forget commit path off `AssignMacroToSelectedButton`.
- `UI-thread affinity` — everything in the commit path runs on the UI thread; the early
  close must not introduce an await before the gate check.
- `thread-safety` — `RampageKeyboardRgbService` is cached per (VID, PID, key) in a
  singleton factory; the new `_maxPayload` is immutable, but confirm no shared mutable
  state was added.
- `performance hot-path` — `GetKeyRemapTooltip` and the badge check run on pointer-enter
  (not per-frame); `WithBrightness` runs on 33 ms animation ticks — the new version
  allocates one string per tick, same as before.

## Performance & quality

- `WithBrightness` sits on the 33 ms RGB animation timers; the linear-scale version is
  strictly cheaper than the old HSV round-trip (no intermediate conversions, same single
  string allocation).
- `GetKeyRemapTooltip` does dictionary lookups plus one `FirstOrDefault` over
  `AssignmentTargets` (~100 rows) per pointer-enter — negligible, not per-frame.
- No new allocations on the device-write path beyond the per-call chunk arrays the 56 + 8
  split already made.
- No new shared state; no sync-over-async introduced; dialog commit paths stay on the UI
  thread with awaits only at device I/O.

## Risks

- **2.4G framing is the inferred fix** for white→cyan: the probe evidence says the
  24-byte framing is the receiver's native relay shape and that a bare 24-byte `0x15`
  persists, but the cyan symptom itself was not reproduced in-house. If QA step 8 still
  fails, run the `rampage-24g` probe with a white static write and capture the read-back.
- **Chunked-and-bracketed is a new combination**: the probe verified 24-byte `0x15`
  chunks *bare*; production sends them inside the `0x10`/`0x11` session with `0x1D` sync.
  Watch QA step 8 for "colour applies but does not persist across power-cycle".
- The optimistic dialog close removes "dialog stays open" as a failure signal for macro
  picks; a refused write is now quieter (radio re-sync + upload banner only).
- The stronger tooltip glow intentionally deviates from the badge's design values
  (`#6357FF`/0.5); if design pushes back it is a one-line revert.

## Open questions

- None blocking. Pending QA confirmation on real 2.4G hardware (step 8) — if it fails,
  the follow-up is a hardware probe, not a code change on this branch.

## Review findings

### Round 1 — Ship: yes

Review surface: `git diff main...HEAD` (7 commits `d9b9434`…`499b266`), 11 files; working
tree clean, nothing staged or untracked. All of `git status --porcelain` is accounted for.

#### Plan conformance

Plan: `docs/plans/fix-keyboard-qa-findings/fix-keyboard-qa-findings-plan.md` (retrospective
— written after the implementation, so conformance is expected; verified it does not
whitewash: the risks it lists are real and match the code).

- Implemented as planned: all 9 file-level change list items, byte-identical wired RGB
  path confirmed by construction (`maxPayload` default 56 reproduces the exact 56+8
  sequence, same order and delays).
- Deviates from plan: none found.
- Missing / partial: none.
- Accepted limitation (per plan): no automated coverage for the Rampage `0x15` chunking
  (no HID seam) and for the visual items — residual risk concentrated in QA step 8
  (real 2.4G hardware); the wired regression risk is negligible by construction.

#### Code

- [x] **S1. Code comment states the 2.4G byte-shift as observed fact** —
  `src/ProoveGaming.Infrastructure/Services/RampageKeyboardRgbService.cs:16-22` (class `<remarks>`) — Should-fix
  **Problem / why it matters:** the remark says QA "saw the wired 56-byte head arrive with
  the colour fields shifted (… R read a neighbouring 0x00)". QA observed only the symptom
  (white → cyan); the field-shift mechanism is an inference, and
  `docs/knowledge-base/smart-infinite-protocol/wireless-receiver-protocol.md` even records
  a probe where the 56+8 `0x15` write *persisted* over 2.4G. If the 24-byte framing does
  not fix the cyan, this comment sends the next debugger down the wrong path with false
  confidence. The plan's Risks section words this honestly; the code comment does not.
  **Fix:** reword to separate observation from inference, e.g. "QA saw a static white
  render cyan over the receiver; consistent with the colour fields shifting one byte
  (R ← 0x00), the same silent misalignment the key-matrix transfers exhibit — inferred,
  not captured on the wire."

#### Tests

- [x] **S2. Refused-write test's radio assert is vacuous** —
  `tests/ProoveGaming.App.Tests/DeviceDetailsKeyAssignmentDialogTests.cs`
  (`MacroBinding_FromDialog_RefusedWrite_ClosesWithoutToast`, the
  `Assert.DoesNotContain(vm.Macros, m => m.IsSelected)` line) — Should-fix
  **Problem:** in the VM-level test nothing ever sets `macro.IsSelected` (the TwoWay radio
  binding that sets it lives in the view), so the assert passes even if
  `RefreshMacroListSelection` were removed — it does not prove the re-sync it claims to.
  The dialog-closed / no-toast asserts are the real regression net and are fine.
  **Fix:** set `macro.IsSelected = true;` immediately before executing the pick, then the
  final assert genuinely exercises the refused-path re-sync. One line.
- Chunking arithmetic in `RampageKeyboardRgbService` untested — accepted in plan
  (no HID abstraction seam; hardware QA step 8 owns it). No action this round.
- *(2026-08-14)* `ToKeyboardBrightness` is now `internal` and pinned by
  `tests/ProoveGaming.Infrastructure.Tests/RampageKeyboardRgbServiceTests.cs` (PR #101 m6)
  — pure arithmetic needs no HID seam; the write path around it stays hardware-QA
  territory.

#### Nits

- Mid-write dialog snap-shut: after an optimistic close, re-opening the dialog for the
  *same* key inside the ~0.5 s write window gets it closed again (plus toast) when the
  first commit lands — `CommitRemapPickAsync`'s success path closes unconditionally
  (`DeviceDetailsViewModel.KeyDialog.cs:197`). Rare (needs a deliberate re-click within
  the window), benign (the toast explains what happened), and a different-key re-open is
  already protected by the selection-identity guard. If it ever bothers QA: capture
  "dialog was open for this pick" at commit entry and only close then.
- `GetKeyRemapTooltip`'s `<remarks>` says it uses "the same truth sources the highlight
  uses", but its precedence (`_slotKeyOverrides` before `_assignments`) actually mirrors
  `CaptureUndoTarget`, while the highlight reads `_assignments` first —
  `DeviceDetailsViewModel.KeyAssignments.cs:1317`. The two disagree only mid-push /
  after a failed push; one-word doc fix.
- The auto-width toast has no `MaxWidth`, so a pathologically long localized string would
  grow past the window edge and clip — `src/ProoveGaming.App/Views/MainWindow.axaml:176`.
  No current resource string comes close; worst case equals the old clipping behavior.

#### What's done well

- Every behavioral fix reuses an existing mechanism instead of adding a parallel one:
  macro picks land in `CommitRemapPickAsync`, the tooltip resolves through the same
  per-family truth sources as the highlight/undo machinery, and the 2.4G chunk size rides
  the key-mapping factory's `TablePayloadFor`. No new state, no new lifetimes.
- The wired Rampage sequence is provably unchanged (default `maxPayload` reproduces the
  historical packets exactly) — the risky transport is the only one whose bytes change.
- The rewritten dialog tests drive the real command surface
  (`AssignMacroToSelectedButtonCommand`, `PickRemapTargetCommand`) rather than internal
  methods, and the mid-write test pins the in-flight gate with a held
  `TaskCompletionSource` — genuinely exercises the race the code guards.
- `WithBrightness` got simpler *and* cheaper on the 33 ms animation path; `HsvToHex`
  stays for the rainbow timers that legitimately need it.

#### Verdict

Ship: yes — no blockers; both should-fixes are five-minute follow-ups that do not affect
runtime behavior (a comment rewording and a one-line test strengthening). The genuinely
open risk is not in the code but in the unverified 2.4G hypothesis, which the plan
already routes to QA step 8 with a named fallback (the `rampage-24g` probe).
First: 1) S2 (make the refused-write assert real), 2) S1 (reword the remark),
3) run QA step 8 on hardware before merging to a release build.

### Round 2 — external review (PR #101 comment by boikoserhii84, 2026-08-14) — dispositions

Findings came in as a PR comment rather than through this journal; recorded here with what
was done about each. All fixes verified: App 780/780, Infrastructure 651/651 (+8 new).

- [x] **M1. 2.4G re-chunking contradicts the repo's own hardware probe** — resolved as the
  reviewer's option B *(the code stays)*: new QA evidence postdates the probe — white
  rendered cyan through the production 56+8 path and colours render correctly after the
  24-byte chunking (the 2026-08-14 brightness report shows `#EE2020` correct on the 2.4G
  Rampage) — so the chunking is kept, and the documentation contradiction is fixed
  instead: `wireless-receiver-protocol.md` gained a dated "does not match live QA" note
  and the `docs/CONTEXT.md` **Feature area** entry now states the conflict as unresolved.
  The `rampage-24g` white-static probe remains the definitive check (plan Risks).
- [x] **M2. Toast lost its width ceiling with unbounded backend messages** —
  `MainWindow.axaml`: `MaxWidth="720"` on `NotificationGrid`,
  `TextTrimming="CharacterEllipsis"` on `NotificationText`, and the icon+text row changed
  from a horizontal StackPanel (infinite measure — trimming could never engage) to an
  `Auto,*` Grid.
- [x] **M3. Refused macro pick left no signal on Key Assignments** — implemented as
  option A, folded into `CommitRemapPickAsync` itself: the commit now captures
  `wasOpen`, closes optimistically at entry (before the ~0.5 s write), and re-opens the
  dialog on a refused apply — restoring "open dialog = refusal" for KEYS, SYSTEM and
  MACROS picks alike. Bonus: KEYS picks get the same optimistic close (no more 0.5 s
  frozen dialog), and the round-1 nit is fixed — a dialog the user re-opens mid-write is
  no longer slammed shut when the pick lands (pinned by the extended mid-write test).
  Tests updated: `MacroBinding_FromDialog_RefusedWrite_ReopensDialogWithoutToast`.
- [x] **m1. Tooltip vs dialog highlight could name different targets** —
  `GetKeyRemapTooltip` now reads `_assignments` first (the highlight's source) with
  `_slotKeyOverrides` as the pre-probe fallback, and its `<remarks>` describe the actual
  precedence.
- [x] **m2. Preview swap leaked onto mice** — the Neon rainbow preview is keyboard-only;
  mice keep the solid hue cycle for both Neon and ColorCycle (single-LED truth per
  `EasenMouseService`).
- [x] **m3. `maxPayload` unvalidated** — ctor guard mirroring
  `SmartInfiniteProtocol.EnumerateChunks` (`ThrowIfLessThan 1` / `ThrowIfGreaterThan 56`).
- [x] **m4. `Thread.Sleep` in the rewritten loop** — now `await Task.Delay`.
- [x] **m5. Stale comments** — `AssignMacroToSelectedKeyAsync` summary covers both paths;
  plan "eight fixes" → ten; `AressKeyboardRgbService` `block[2]` comment fixed to 0–4.
- [x] **m6. Brightness mapping untested** — `ToKeyboardBrightness` made `internal`;
  `RampageKeyboardRgbServiceTests` pins the 0–4 table (255→4, 153→2, non-zero→≥1, 0→0,
  Off→0). Test-strategy section updated.
