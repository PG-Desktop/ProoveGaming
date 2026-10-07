# Add T-Racer Macros — Library Upload, Key Binding & the Type-Byte Fix

## Goal

T-Racer is the last supported keyboard in the Raster vendor family without macros: it has no
Macros nav item, no macro service, and — the part that matters — a key-mapping service that
would silently discard a macro binding even if the rest were wired up. Raster, Cyrex and Aress
all have the full pipeline. This change brings T-Racer to parity by fixing the type-byte
regression it inherited, giving the macro factory a T-Racer branch, and turning the feature on
in config. The App side needs no work at all: the macro UI is driven entirely by the
`SupportsMacros` config flag, so once the host advertises it, the recorder, library, binding and
resync paths already work.

## Scope

**In**

- Fix `TRacerKeyboardKeyMappingService` to pass macro-kind type bytes through instead of forcing
  `0x10` — without this nothing else in the plan produces a working macro.
- A T-Racer branch in `DeviceKeyboardMacroServiceFactory`.
- Reuse of the existing Raster-family macro service, parameterised by model name for logging.
- `SupportsMacros` + `Macros { MaxDelayMs, MaxRepetitions }` on the T-Racer `AllowedDevices` entry.
- Glossary entries and correction of the stale comments that say T-Racer macros are out of scope.
- Tests per the Test strategy, plus hardware verification on the connected unit.

**Out**

- Any change to the App (`ProoveGaming.App`). Verified: `IsMacrosSupported` is
  `Device?.SupportsMacros == true` (`DeviceDetailsViewModel.MacroRecorder.cs:44`), fed from
  `entry.SupportsMacros` via `HomeViewModel:525`. Every other macro gate gets there through it.
- Any change to the gRPC contract. `SetMacros` and `SetKeyMapping` already exist and are
  device-agnostic on the wire.
- Any change to `RasterMacroEncoder`. The packed buffer, the 56-byte chunking, the press/release
  attribute and the `0x60` binding record are the family's format and T-Racer is in that family.
- **Macros over Bluetooth and over the 2.4G dongle.** Wired only — see Risks.
- Cyrex / Aress behaviour — unchanged; that family stays fully device-truth and immediate.
- ~~Raster behaviour.~~ **No longer true.** The staged-macro change below deliberately alters
  Raster too: it shares the staged path with T-Racer, so macro bindings there now also wait for
  Save Preset. Called out rather than smuggled in — see
  [Staging macro bindings](#staging-macro-bindings).

## The bug this has to fix first

`src/ProoveGaming.Infrastructure/Services/TRacerKeyboardKeyMappingService.cs:268-270`:

```csharp
// Firmware records type=0x10 for keyboard slots; override the caller's byte for parity
// with what GetDefault reads back (same policy as Raster).
var effectiveType = TypeKeyboardKey;
```

Raster's equivalent (`RasterKeyboardKeyMappingService.cs:301`):

```csharp
var effectiveType = RasterMacroEncoder.IsMacroBindingType(type) ? type : TypeKeyboardKey;
```

T-Racer's service was copied from Raster **before** the macro fix landed and never received it.
The comment "(same policy as Raster)" is now false. A macro binding arrives as
`{type=0x60, code1=slot, code2=playMode}`; forcing `0x10` rewrites it as an ordinary keyboard
record whose scancode is the macro's slot index — slot 0 becomes scancode `0x00`, so the key
produces nothing at all. The failure is silent: the write acks, the read-back agrees, and the
key just stops working. `../raster-svg-effects/raster-svg-effects-plan.md` records this as
costing a full debugging round on Raster.

**This is the single most important line in the plan.** Every other item is inert without it.

## Options considered

### 1. New `TRacerKeyboardMacroService` vs reusing the Raster-family service

| Option | Trade-off |
|---|---|
| **A. Reuse `RasterKeyboardMacroService`, adding a `modelName` parameter for log tags** | **Chosen.** One implementation of the chunked upload, the ack validation and the discard reporting. A future fix reaches both models by construction. |
| B. New `TRacerKeyboardMacroService`, near-copy | Rejected — see below. Matches the RGB / key-mapping precedent, and is the reason this plan exists. |
| C. Extract a shared base class, thin per-model subclasses | Rejected. Speculative generality for zero behavioural difference; a `modelName` string is the entire delta. |

**Why B is rejected despite being the established pattern.** `TRacerKeyboardRgbService` and
`TRacerKeyboardKeyMappingService` are both near-copies of their Raster counterparts, and the
`tracer-rgb` plan's stated rationale was that isolation lets a T-Racer-specific quirk be tuned
without touching Raster. That rationale has now been tested by events and failed: **the copy
drifted, and the drift is exactly the bug this plan opens with.** `RasterKeyboardMacroService`
carries the ack-validation that closed a review blocker (`B1` in the raster-svg-effects journal);
copying it now would clone that logic into a second file where the next fix can miss it again.

The macro layer also already contradicts the one-file-per-keyboard convention:
`SmartInfiniteMacroService(device, modelName, logger)` serves **both** Cyrex and Aress from one
file, dispatched by name in the same factory this plan edits. Option A follows the convention
that the macro layer actually uses.

**Naming.** The class keeps the name `RasterKeyboardMacroService`. In this codebase the `Raster*`
prefix already denotes the family's shared implementation rather than the single model:
`RasterMacroEncoder` packs Cyrex and Aress libraries, and `RasterKeyBounds` is the bounds type
for `TRacerArtworkLayout` and `CyrexArtworkLayout`. Renaming to `RasterFamilyMacroService` would
be more honest and costs only two code references, but it would strand the name used throughout
two historical plan documents. Recorded as an Open question rather than done silently.

### 2. Where the config flag goes

`DeviceMapExpander.cs:47` copies `SupportsMacros` and `Macros` **device-level down to every
transport** — unlike `SupportsRgb`, which has a `t.SupportsRgb ?? device.SupportsRgb` override.

| Option | Trade-off |
|---|---|
| **A. Device-level `SupportsMacros`, accept that all three transports advertise it** | **Chosen.** Matches every other macro device and needs no expander change. The Macros nav item appears on Bluetooth and dongle too, where upload will fail — same pre-existing shape as key remap, which is already wired-only while the overlay shows on all transports. |
| B. Add a per-transport `SupportsMacros` override to `DeviceMapExpander` | Rejected for this change. It is the correct long-term fix and would also fix key remap's version of the problem, but it is a shared-config change affecting every device, and T-Racer is only the first model to expose the gap. Belongs in its own plan. |

## Recommended approach

Four edits and a config line. In dependency order:

1. **`TRacerKeyboardKeyMappingService.SetKeyMappingAsync`** — replace the forced
   `effectiveType = TypeKeyboardKey` with the Raster expression, and correct the comment so it
   states the rule rather than claiming parity it does not have.
2. **`RasterKeyboardMacroService`** — add a `modelName` constructor parameter (defaulting to
   `"Raster"`, so the existing call site is unchanged) and use it in place of the literal
   `"Raster"` in all seven log messages. No behavioural change.
3. **`DeviceKeyboardMacroServiceFactory.CreateForDevice`** — add the T-Racer name branch next to
   Raster's, routing through the same `SelectVendorInterface` (T-Racer exposes the identical
   `mi_01` 65/65 vendor interface — `DeviceKeyMappingServiceFactory` already relies on this and
   the hardware log confirms it). Delete the "T-Racer follows in its own PR" comment.
4. **`appsettings.json`** — `"SupportsMacros": true` and
   `"Macros": { "MaxDelayMs": 1000, "MaxRepetitions": 255 }` on the T-Racer device entry,
   matching Raster exactly (same firmware family, same 1096-byte library, same 16-macro cap).

Then documentation and tests.

## Macro bindings and the preset push

Added after hardware verification: macros worked, but saving a preset afterwards did not carry
them to the device.

The keyboard branch of `ApplyCurrentStateToDeviceAsync` (`Presets.cs:259`) — the full-state push
used by **Save Preset, preset switch and connect-default** — pushed RGB and key remaps only. Macro
bindings were re-asserted on device connect (`RefreshFromDeviceAsync:792`) and after a macro edit
or delete (`Buttons.cs:484`, `:576`), but never by the preset push.

Two consequences, the second worse than the first:

1. Saving or switching a preset left the macro library and its bindings untouched, so the preset
   was not the full-state snapshot it claims to be.
2. `ApplyKeyOverridesToDeviceAsync` writes the **factory keycode** into any slot the device reports
   as overridden but the active preset does not stage. A macro-bound slot looks exactly like that
   to it — bound immediately, never staged — so the push could replace a macro binding with a
   plain keycode while the App still showed the macro.

**Confirmed on hardware, and consequence 2 was real — not theoretical.** A Save Preset with a
macro bound to slot 33 logged, in order:

```
[KEY] SetKeyMapping matrixIndex=33 type=0x10 code2=0x1A
     get-before idx=33 → 55-60-00-00      ← the slot held a MACRO record (0x60)
     set-write         → 10-00-1A          ← overwritten with a plain keycode
     get-after         → 55-10-00-1A       ← macro binding destroyed
[MACRO] T-Racer UploadMacros count=1       ← the new call
[KEY] SetKeyMapping matrixIndex=33 type=0x60 (caller-type=0x60) code2=0x00
     get-after         → 55-60-00-00       ← binding restored, firmware-verified
```

So before this change, saving a preset **silently replaced the macro binding with a keycode**
while the Macros page went on showing the macro as bound. The fix does not merely add a missing
push; it repairs active destruction.

**Fix:** call the existing `ResyncKeyMacroBindingsAsync()` at the end of the keyboard branch.
Ordering is load-bearing — it must run **after** `ApplyKeyOverridesToDeviceAsync()`, so that any
slot that push reset is re-established. That makes the preset push self-healing rather than merely
non-destructive. The method already self-gates on `IsMacrosSupported` and device identity and is
idempotent, which is why the two existing call sites invoke it unconditionally.

Macro bindings remain **device-scoped, not preset-scoped** (`key_macro_bindings` is keyed by
owner + device + slot). This change re-asserts them on every push; it does not make different
presets carry different macro bindings. That would be a larger change and is not in scope here.

**Precedence, now explicit.** One slot can carry both a staged key remap (preset-scoped, in
`key_bindings`) and a macro binding (device-scoped, in `key_macro_bindings`) — the hardware trace
above is exactly that case, with a staged `Q → W` and a macro on the same slot. Because the
resync runs last, **the macro wins**. That matches what the Macros page displays, and it is the
opposite of the previous behaviour, where the key remap won by silently destroying the macro.
Neither source stops the other being recorded, so the conflict itself still exists and is only
resolved by ordering — worth revisiting if presets ever become the owner of macro bindings too.

## Resetting a macro-bound key

Reported after the preset-push fix: a key with a macro on it could not be returned to its
default, nor remapped to another key, from Key Assignments.

`StageAssignmentTarget` (`KeyAssignments.cs:914`) writes `key_bindings` but never touched
`key_macro_bindings`. The immediate path has always kept the two in step —
`ApplyAssignmentTargetAsync` does `SetKeyMacroBinding` for a macro target and
`DeleteKeyMacroBinding` for anything else — but the staged path lost that half when key remaps
moved off immediate writes. Nothing else deletes the row, so it outlived the remap.

On its own that was latent: the orphaned row sat in SQLite doing nothing, and the key push
happened to overwrite the macro on the device anyway. Adding the resync to the preset push turned
it into the reported bug — the resync now faithfully re-writes the macro the user just cleared.
Two symptoms, one cause:

- **Reset** stages the key's own scancode, clears the override, leaves the row → next Save Preset
  re-writes the macro. The key never returns to default.
- **Remapping a macro-bound key** to another key stages the new code, leaves the row → the key
  push writes the remap and the resync immediately overwrites it with the macro. The remap never
  takes effect.

**Fix:** delete the `key_macro_bindings` row in `StageAssignmentTarget`, unconditionally — only
non-macro targets reach it, since `SelectAssignmentTarget` routes `IsMacroTarget` to the immediate
path. Keyed by matrix slot (`ResolveMatrixSlot`), not by scancode, because the two tables use
different keys.

Note the device is cleared on the **next Save Preset**, not on the reset click, which is
consistent with every other staged edit. It relies on the probe having recorded the macro slot as
overridden so the key push sees a difference worth writing — confirmed in the hardware trace
above, where `get-before` on the macro slot returned `55-60-00-00`.

## Staging macro bindings

Requested after the reset fix: macro bindings were still writing to the keyboard the moment they
were picked, while plain key remaps waited for Save Preset. Two models on one page.

**Decision: stage them like key remaps, per family.** Raster/T-Racer stage everything — key remaps
and macro bindings alike — and the preset push is the only thing that touches the device.
Cyrex/Aress stay fully immediate, because their key remaps are immediate too; each family is now
internally consistent, which is the property that was missing.

**A correction to what was said when the option was offered.** Discard does *not* roll back a
staged edit — it clears the dirty flag. `StageAssignmentTarget` writes the live `key_bindings` row
at selection time, and only a preset switch reloads the live rows from the `preset_key_bindings`
snapshot. So staging changes *when the device is written*, not whether SQLite keeps the choice.
That is true of key remaps today and is now equally true of macro bindings; making Discard
genuinely revert either would need the preset-scoped option, which was not taken.

**Shape of the change** (all in `KeyAssignments.cs`):

- `SelectAssignmentTarget` no longer routes `IsMacroTarget` to the immediate path — only
  `IsSmartInfinite65Device` does.
- `StageAssignmentTarget` gained a macro branch: record the `key_macro_bindings` row, clear any
  staged key override on that scancode. The non-macro branch clears the macro row (the earlier
  reset fix). **A slot is now either a macro or a key record, never both** — which also retires
  the ordering-dependent precedence noted under
  [Macro bindings and the preset push](#macro-bindings-and-the-preset-push).
- `AssignMacroToSelectedKeyAsync` (the Macros-page button) now goes through
  `SelectAssignmentTarget` instead of calling the immediate path directly, so binding from the
  Macros page and picking the same macro in the target list behave identically. They had quietly
  diverged.

**Consequence worth knowing:** a failed library upload no longer discards the user's choice. The
staged row survives and the next successful push retries it — the same way a key remap staged
while the keyboard is unplugged is applied on the next save. `Assign_WritesNothingWhenUploadFails`
was updated to assert that explicitly rather than the old "nothing persisted".

## Removing a macro from a key

Reported as a missing capability: no way to take a macro back off a key. The affordance existed —
the **Reset** link in the Macros / Key Assignments header — but was **disabled** for exactly the
case it was needed in.

`CanResetSelectedMacroTarget` → `SelectedKeyHasOverride` consulted two things: staged key
overrides (`_stagedKeyOverrides`) and the device probe (`_userOverrides`). A macro binding is in
neither — it lives in `key_macro_bindings`. Staging macros made it strictly worse: a freshly
assigned macro is not on the keyboard yet, so the probe map cannot stand in for it either. Result:
bind a macro, and Reset greys out.

The Smart Infinite branch a few lines below already closes this same gap for Cyrex and Aress, with
a comment saying so — it was never carried across to Raster/T-Racer.

**Fix:** `SelectedKeyHasOverride` returns true when `BoundMacroForSelectedKey()` is non-null. No
new UI; the existing Reset link now lights up and `StageAssignmentTarget` already clears the row.

Two related fixes went in alongside, both from the same report:

- **The Macros radio list never reflected a keyboard binding.** `RefreshMacroListSelection`
  resolved the bound macro from `SelectedHotspot` / `_buttonMacros`, both mouse-only, so on a
  keyboard it cleared every radio. It now resolves via `BoundMacroForSelectedKey()`, and is called
  when the *key* selection changes — it previously was not called at all on that path.
- **Reset "not always" sticking.** `ResolveMatrixSlot` returns the probed slot once `_matrixMap`
  has landed and the catalog `MatrixIndex` before that, and for T-Racer those disagree (`E` probes
  to 35, catalog says 45 — the catalog is Raster-seeded). The probe is fire-and-forget on page
  open, so a binding assigned after it landed and reset before it landed deleted the wrong row;
  the real one survived and the next Save Preset re-applied the macro. `StageAssignmentTarget` now
  deletes both slot candidates. The underlying slot ambiguity remains — see **S7** in the review
  journal, same root cause.

## File-level change list

### Edited — Infrastructure

| File | Intent |
|---|---|
| `src/ProoveGaming.Infrastructure/Services/TRacerKeyboardKeyMappingService.cs` | Pass macro-kind type bytes through via `RasterMacroEncoder.IsMacroBindingType`; correct the false "(same policy as Raster)" comment and record what the byte means. |
| `src/ProoveGaming.Infrastructure/Services/RasterKeyboardMacroService.cs` | Add a `modelName` parameter (default `"Raster"`) and use it in the log tags, so one implementation serves both models with correct diagnostics. Behaviour unchanged. |
| `src/ProoveGaming.Infrastructure/Services/DeviceKeyboardMacroServiceFactory.cs` | Add the T-Racer branch, passing `"T-Racer"` as the model name; remove the stale "follows in its own PR" comment. |

### Edited — App (added after the Round 1 review, on user report)

| File | Intent |
|---|---|
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs` | `ApplyCurrentStateToDeviceAsync` re-asserts macro bindings on the keyboard branch, **after** the key-override push — see [Macro bindings and the preset push](#macro-bindings-and-the-preset-push). |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` | `StageAssignmentTarget` owns both stores — stages macro bindings and clears whichever of the two the other branch replaces; `SelectAssignmentTarget` and `AssignMacroToSelectedKeyAsync` route through it. See [Resetting a macro-bound key](#resetting-a-macro-bound-key) and [Staging macro bindings](#staging-macro-bindings). |
| `tests/ProoveGaming.App.Tests/DeviceDetailsKeyboardMacroBindingTests.cs` | Four tests moved off the immediate-write assumption (assert after the push, not after selection); two added pinning the staged model and the one-slot-one-record rule. |

### Edited — DeviceHost

| File | Intent |
|---|---|
| `src/ProoveGaming.DeviceHost/appsettings.json` | T-Racer entry gains `SupportsMacros: true` and the `Macros` limits block. |

### Edited — docs

| File | Intent |
|---|---|
| `docs/CONTEXT.md` | New term **Raster family (vendor family)**; extend **T-Racer** and **Macro library** to say T-Racer is included. Mandatory per CLAUDE.md. |
| `docs/plans/raster-svg-effects/raster-svg-effects-plan.md` | Scope "Out" and QA step 16 still say T-Racer has no macro nav item. Annotate as superseded, the same way the artwork line already is. |

### Edited — tests

| File | Intent |
|---|---|
| `tests/ProoveGaming.Infrastructure.Tests/DeviceMapExpanderTests.cs` | Extend the existing T-Racer case (`:119`) so `SupportsMacros` / `Macros` are asserted on every expanded transport row, pinning the config contract the App gates on. |

`RasterMacroEncoderTests` needs **no** change — `IsMacroBindingType` is already covered there.

No `.slnx`, `Directory.Build.props`, NuGet, installer or gRPC changes.

## Acceptance criteria

- [x] Given a T-Racer connected by USB, when its details page opens, then a **Macros** item
      appears in the left nav (it does not today).
- [ ] Given the Macros section, when a key on the on-screen keyboard is clicked, then that key's
      name appears above the "+ NEW MACRO" button with a Reset action, exactly as on Raster.
- [x] Given a recorded macro and a selected key, when the macro is assigned, then the DeviceHost
      log shows `[KEY] T-Racer SetKeyMapping matrixIndex=<n> type=0x60 (caller-type=0x60)` —
      **`type` and `caller-type` both `0x60`**. A log line reading `type=0x10 (caller-type=0x60)`
      means the fix is absent and the binding is dead.
- [x] Given the same assignment, then the log shows `[MACRO] T-Racer UploadMacros count=<n>` —
      the model name, not `Raster`, proving the parameterised service is wired correctly.
- [ ] Given a macro bound to a key, when that physical key is tapped once, then the recorded
      sequence types out **and no key remains held** afterwards.
- [ ] Given a macro binding, then it takes effect **without** Save Preset — macro bindings are on
      the immediate-write path (`SelectAssignmentTarget` routes `IsMacroTarget` to
      `ApplyAssignmentTargetAsync`), unlike plain key remaps.
- [x] Given a key bound to a macro, when **Save Preset** is clicked and confirmed, then the key
      still plays the macro — the host log shows a second `[MACRO] T-Racer UploadMacros` and a
      `SetKeyMapping … type=0x60` for that slot, issued *after* the key-override push.
- [x] Given a key bound to a macro and a plain remap staged on a different key, when Save Preset
      is confirmed, then **both** hold: the remapped key types its new character and the bound key
      still plays its macro.
- [ ] Given a macro is assigned to a key, then the keyboard does **not** change until Save Preset
      is confirmed — no `[MACRO] … UploadMacros` and no `SetKeyMapping` appear in the host log at
      selection time.
- [ ] Given a Raster, then its macro bindings stage the same way — this change alters Raster too.
- [ ] Given a key with a macro bound and nothing else, then the **Reset** link is enabled — it was
      greyed out, leaving no way to remove the macro.
- [ ] Given a key with a macro bound, then that macro's row in the Macros list shows as selected,
      and clears when the key is reset.
- [ ] Given a key bound to a macro, when Reset is used on it and the preset is saved, then the key
      types its factory character and **stays** that way across further saves — the macro does not
      come back.
- [ ] Given a key bound to a macro, when it is remapped to a different key and the preset is saved,
      then the key produces the remapped character, not the macro.
- [ ] Given a key bound to a macro, when Reset is used, then the key returns to its factory
      keycode and its `key_macro_bindings` row is removed.
- [ ] Given macros bound to keys, when the App is restarted with the keyboard connected, then
      those keys still play the same macros.
- [ ] Given a HID write or ack fails during upload, then the upload reports failure and no key
      binding is written or persisted.
- [ ] Given a Raster, Cyrex or Aress, then its macro behaviour and log output are unchanged —
      in particular Raster's log lines still read `[MACRO] Raster …`.
- [ ] `dotnet build ./src/ProveGaming.slnx` — 0 errors; `dotnet test ./src/ProveGaming.slnx` — green.

## Test strategy

This is a **new feature** (`add-*`) whose core is a **one-line fix to existing behaviour**. The
touched behaviour — `TRacerKeyboardKeyMappingService.SetKeyMappingAsync` — has **no tests**, and
cannot get meaningful ones: it opens a `HidStream` directly with no seam, the same reason
`RasterKeyboardMacroService` has none (recorded in `../add-cyrex-aress-parity/`). Introducing a
stream seam for one line would retro-fit to four services and is out of scope. **The type-byte
fix is therefore verified by the log assertion in QA step 6, not by a unit test** — that is a
deliberate, stated gap, and it is why the acceptance criteria name the exact log line.

All automated coverage lands in **`tests/ProoveGaming.Infrastructure.Tests`**.

**Already covered — do not duplicate**

- `IsMacroBindingType` is tested at `RasterMacroEncoderTests.cs:186` (accepts `0x60`/`0x61`) and
  `:192` (rejects `0x10`). The predicate the fix depends on is green today; the gap is purely
  that `TRacerKeyboardKeyMappingService` never calls it. No new encoder test is needed.

**Must add**

- Extend the **existing** `DeviceMapExpanderTests.TRacer_WiredOnlyOverridesApply_OthersFallBack`
  (`:119`) — or add a sibling next to it — asserting that `SupportsMacros` and the `Macros` block
  reach **all three** expanded T-Racer rows, including Bluetooth. This pins the config contract
  the App's `IsMacrosSupported` reads *and* documents the device-level-only behaviour chosen in
  Option 2A, so the Bluetooth limitation is a recorded decision rather than a surprise. The
  fixture at `:26` already sets `SupportsMacros = true`, so mirror that style.
- A device **without** `SupportsMacros` expands to rows with it false and `Macros` null, so the
  flag cannot leak on by default — unless an existing case already covers this, in which case
  leave it alone.

**Deliberately not automated**

- `TRacerKeyboardKeyMappingService` and the macro service's HID exchange — no stream seam, no
  device in CI. Covered by QA and by the log assertions in the acceptance criteria.
- The factory's name dispatch — it needs `IHidDeviceProvider` + `IDeviceInfoProvider` and real
  enumerated devices; no existing test constructs it, and adding that harness is a larger change
  than the branch it would guard.
- End-to-end macro playback — hardware only.

## QA (manual test steps)

Requires a T-Racer keyboard connected **by USB cable**, and Notepad. Start the device service,
then the app.

1. Open the T-Racer from the home screen and look at the menu on the left.
   **Expected:** there is now a **Macros** entry. Before this change there was not.
2. Open the Macros section.
   **Expected:** a picture of the keyboard you can click, and a "+ NEW MACRO" button. No key is
   highlighted to begin with.
3. Click the `Q` key on the picture.
   **Expected:** "Q" appears as a heading above the "+ NEW MACRO" button, with a Reset link.
4. Create a macro: record typing `qwerty`, save it, then with `Q` still selected click that macro
   in the list to assign it.
   **Expected:** the assignment is accepted with no error.
5. **Do not click Save Preset.** Open Notepad and tap the physical `Q` key once.
   **Expected:** `qwerty` is typed. Macro bindings apply immediately — unlike ordinary key
   remaps, they do **not** need Save Preset. Critically: afterwards the keyboard behaves
   normally — no character repeats forever and no key acts stuck. If anything sticks, note it and
   reset the keyboard (FN+Space, hold 3 s).
6. Tap `W` and `E`.
   **Expected:** they type their normal letters. Only the bound key runs the macro.
7. Now click `SAVE PRESET` and confirm `SAVE` in the dialog. Tap physical `Q` again.
   **Expected:** `qwerty` still types. Saving a preset must not disturb a macro binding.
8. Go to Key Assignments, remap a *different* key (e.g. `F9` to `F10`), then `SAVE PRESET` →
   `SAVE`. Tap `F9`, then tap `Q`.
   **Expected:** `F9` types `F10` **and** `Q` still plays the macro — both survive the same save.
9. Select `Q` again and click Reset.
   **Expected:** tapping physical `Q` types `q` again.
10. Re-assign the macro to `Q`, close the app completely, reopen it and tap `Q`.
   **Expected:** the macro still plays — the binding survived the restart.
11. Delete that macro from the list, then tap the key it was bound to.
   **Expected:** the key no longer runs the macro and does not misbehave.
12. Unplug the cable, then try to assign a macro.
    **Expected:** the app reports a failure. It must **not** look like it worked.
13. Open a Raster (if available) and run steps 2–5 on it.
    **Expected:** unchanged from before this work.
14. Reconnect the T-Racer over the 2.4G dongle or Bluetooth and open Macros.
    **Expected:** the section is present but assigning a macro fails. This is a known limitation,
    not a new bug — record what you see so it can be tracked.

## Review focus

- `resource disposal` — the macro service opens a `HidStream` per upload; stop-comm must run even
  on failure or the next session on `mi_01` hangs on the read timeout.
- `async/await` — upload and resync failure propagation; a rejected chunk must never be reported
  to the App as stored.
- `null-safety` — the factory returns `null` for an unresolved device and every caller must
  already coalesce; the new branch must not change that contract.

## Performance & quality

- **No hot path.** Macro upload is a one-shot 1096-byte chunked exchange on user action, off the
  UI thread via gRPC. Nothing here runs per frame or per poll.
- **No new allocations of note.** The `modelName` parameter is a string literal held per service
  instance, which is created per call from singleton providers and holds no cross-call state.
- **Shared state** — none added. The factory resolves a fresh service per call.
- **Resource lifetime** — unchanged; the `using (stream)` and unconditional stop-comm in
  `RasterKeyboardMacroService` are inherited as-is by T-Racer, which is the point of reusing it.

## Risks

- **The fix is unit-untestable.** The one line that makes or breaks the feature is verified only
  by a log line and a physical key press. If QA step 5 is skipped, a regression here ships
  silently — the write acks and reads back cleanly even when wrong.
- **Dongle and Bluetooth advertise macros they cannot deliver.** `SupportsMacros` is device-level
  only, so the nav item appears on all three transports while the factory filters to non-Bluetooth
  and the vendor interface realistically exists only on the wired PID. Over the dongle
  (PID `12290`) it is **unverified** whether the 65/65 vendor interface is even present — Raster's
  dongle exposes it, but that is a different product ID. QA step 12 exists to find out.
- **Reusing the Raster service couples the two models.** A genuine T-Racer-only firmware quirk
  would now require re-splitting the file. Accepted: the drift risk demonstrated by this very bug
  outweighs a hypothetical divergence, and re-splitting later is mechanical.
- **The other two copied T-Racer services may carry their own drift.** This plan fixes the
  key-mapping copy because macros expose it. `TRacerKeyboardRgbService` was copied from
  `RasterKeyboardRgbService` at a similar time and has **not** been diffed against it as part of
  this work. Worth a follow-up pass; explicitly not done here.
- **16-macro / 60-event caps are silent.** Inherited from the encoder, unchanged, and already
  logged as warnings by the service — but not surfaced in the UI.

## Open questions

None blocking — `/coder` can execute this as written. One to confirm alongside:

1. **Rename `RasterKeyboardMacroService` to `RasterFamilyMacroService`?** It will serve two
   models after this change. Two code references, but it strands the name used in two historical
   plan docs. Default is **no rename** — the `Raster*` prefix already means "family" elsewhere in
   this codebase (`RasterMacroEncoder` packs Cyrex/Aress libraries; `RasterKeyBounds` backs
   `TRacerArtworkLayout`). Say so if you would rather have the clearer name.

## Review findings

Maintained by `/reviewer` as a round-by-round journal. `/coder` and `/test-writer` read this
section and mark items `[x]` as they are closed.

### Round 1 — Ship: yes (conditional on the QA hardware pass)

**Surface.** No commits, no branch — everything is uncommitted on `main`. `git status --porcelain`
lists 13 modified + 8 untracked paths; the union of `git diff` and the untracked reads accounts
for all of them. Build clean, 357 + 355 tests pass after the final edit.

**Out of scope this round:** the T-Racer *artwork* change is still uncommitted in the same tree
(`RgbKeyboardArt.cs`, `DeviceDetailsViewModel.KeyAssignments.cs`, `RasterKeyboardOverlayView.axaml`,
`extract-legends.py`, the five `Assets/Keyboards/*T-Racer*` files, `TRacerArtworkLayout.cs`,
`ArtworkKeyOverlayGateTests.cs`, `KeyboardLegendAssetTests.cs`, `TRacerArtworkLayoutTests.cs`,
`docs/plans/add-tracer-artwork/`). Reviewed in Round 1 of
[../add-tracer-artwork/add-tracer-artwork-plan.md](../add-tracer-artwork/add-tracer-artwork-plan.md)
and not re-litigated here. Three files carry **both** changes and were read hunk by hunk to
separate them: `appsettings.json` (the `ImagePath` line is artwork, the `SupportsMacros`/`Macros`
block is this round), `docs/CONTEXT.md`, and `raster-svg-effects-plan.md`.

#### Plan conformance

Full conformance. Every File-level item is implemented, nothing extra was added, and the one
deviation was caught by the author rather than shipped.

- **Implemented as planned:** the type-byte fix; the `modelName` parameter and its 11 log sites;
  the factory branch; the config flag and `Macros` block; the `CONTEXT.md` term plus two
  extensions; the `raster-svg-effects` supersession notes; the `DeviceMapExpanderTests` extension.
- **Deviates from plan (correctly):** the plan specified `modelName` default `"Raster"` "so the
  existing call site is unchanged", then the author changed the call site anyway to pass a
  literal. That was the right call — `DeviceMapExpander.cs:29` upper-cases `Name`, so passing
  `config.Name` through would have turned Raster's existing `[MACRO] Raster …` lines into
  `[MACRO] RASTER …`, a silent regression to a shipping device's diagnostics. Verified: the
  factory now passes `"Raster"` / `"T-Racer"` literals, matching `DeviceKeyMappingServiceFactory`.
  The plan's stated reason for the default is now stale — see **S2**.
- **Correctly did NOT change:** `RasterMacroEncoderTests` (`IsMacroBindingType` already covered at
  `:186`/`:192`) and anything in `ProoveGaming.App`. Both verified — `IsMacrosSupported` is
  `Device?.SupportsMacros == true` and every macro gate reaches it through that.
- **Accepted limitation (per plan):** macros advertised on Bluetooth and the dongle where the
  factory may not resolve a service. Residual risk confirmed as *graceful*: `CreateForDevice`
  returns `null` and the upload reports failure rather than silently no-op'ing. Whether the
  dongle PID `12290` exposes a 65/65 interface at all is still unverified — QA step 12.
- **Acceptance criteria:** the code-side ones are met. The eight hardware criteria are unmet
  because nothing has been run on the device — that is the whole basis of the conditional verdict.

#### Code

- [ ] **S1. Nothing this change introduced has any test coverage** — `tests/ProoveGaming.Infrastructure.Tests/DeviceMapExpanderTests.cs:140` — Should-fix
  **Problem / why it matters:** both added tests exercise `DeviceMapExpander`, which this change
  **did not modify** — it already copied `SupportsMacros` down to every row. They pin a contract
  and document the Bluetooth decision, which is worth having, but they would pass identically on
  `main`. The three things this change actually introduced — the type pass-through, the factory
  branch, and the config flag — have **zero** automated coverage between them.
  The plan waved the factory off with "it needs `IHidDeviceProvider` + `IDeviceInfoProvider` and
  real enumerated devices; no existing test constructs it, and adding that harness is a larger
  change than the branch it would guard." **That justification is half wrong.** Moq is already a
  dependency and `HidDeviceDiscoveryTests.cs:97` already does
  `_mockDeviceInfoProvider.Setup(x => x.DevicesMaps).Returns(...)` — mocking the config side is
  one line. The part the plan got right is `HidDevice`: `TestHidDeviceFactory` at
  `HidDeviceDiscoveryTests.cs:473` resorts to `DeviceList.Local.GetHidDevices()`, i.e. real
  hardware, so a test that gets a *service* back is genuinely infeasible.
  **Concrete failure this leaves open:** someone renames the `AllowedDevices` entry, or edits the
  name check, and T-Racer silently loses macros — the only symptom is a
  `"is not a macro-supported keyboard"` warning in a log nobody reads, and every test stays green.
  **Fix:** test the dispatch through the logger, which is what the branch actually decides. Mock
  `IDeviceInfoProvider.DevicesMaps` with a T-Racer row and `IHidDeviceProvider.GetDevices()` with
  an empty list, call `CreateForDevice(14000, 12370)`, and assert the warning is
  `"vendor config interface (outLen=65/inLen=65) not found"` (T-Racer branch taken) and **not**
  `"is not a macro-supported keyboard"` (fell through). Same shape for Raster, to pin that the
  shared branch did not break it. `Mock<ILogger<T>>` verification is already the house style in
  that file.

- [ ] **S2. The `modelName` default is now dead and is a silent-wrong-tag trap** — `src/ProoveGaming.Infrastructure/Services/RasterKeyboardMacroService.cs:22` — Should-fix
  **Problem / why it matters:** `string modelName = "Raster"` was defaulted so the existing call
  site would not need changing, but that call site now passes the name explicitly, so the default
  is never used. What it leaves behind is a trap: a third model added via
  `new RasterKeyboardMacroService(device)` compiles, runs, and logs its entire upload as
  `[MACRO] Raster …`, which is precisely the kind of misleading diagnostic this change existed to
  avoid. It also sits between two other parameters, so a positional call is easy to get subtly
  wrong.
  **Fix:** drop the default — `RasterKeyboardMacroService(HidDevice device, string modelName, ILogger? logger = null)`.
  One call site, already compliant; the compiler then forces every future model to name itself.

#### Tests

- [ ] The factory-dispatch coverage described in **S1** — `tests/ProoveGaming.Infrastructure.Tests`.
- [ ] **Deliberately not automatable, and correctly identified as such by the plan:** the type
  pass-through in `TRacerKeyboardKeyMappingService.SetKeyMappingAsync`. No stream seam exists, and
  adding one would retro-fit to four services. This means **the single line the whole feature
  depends on is verified only by QA step 5 and the log assertion in the acceptance criteria.** If
  that step is skipped, a regression here ships silently: the write acks, the read-back agrees,
  and the key simply does nothing. Worth stating plainly rather than leaving in a test-strategy
  footnote.

#### Nits

- `docs/CONTEXT.md` — the new **Raster family (vendor family)** entry is inserted between
  **Macro library** and **Macro type byte (key record)**, i.e. in the middle of the M-terms, while
  **Raster** and **RasterArtworkLayout** sit ~20 lines below. The file is only loosely
  alphabetical (it already has violations), but this one separates the new term from its obvious
  neighbours.
- Repeat counts are silently ignored on T-Racer. `MacroBindingRecord`
  (`DeviceDetailsViewModel.KeyAssignments.cs:567`) gates the `0x61` repeat type on
  `IsSmartInfinite65Device`, so T-Racer always writes `0x60` — yet this change gives it
  `"MaxRepetitions": 255`, letting a user set a repeat count that does nothing. This is exactly
  Raster's behaviour and the plan's goal was parity, so it is not a regression; but it is newly
  reachable, and the doc comment at `:554-558` explaining it still names only Raster.
- The plan's Test strategy assertion that no test constructs the factory harness is wrong on the
  `IDeviceInfoProvider` half (see **S1**) — worth correcting in the plan so the next change does
  not inherit the excuse.

#### What's done well

- **The fix is the real fix, not a workaround.** `effectiveType` now matches Raster's expression
  exactly, and the comment explains the failure mode *and* records that this file drifted from
  Raster — which is the fact most likely to prevent the same bug a third time.
- **Reusing the service instead of copying it, with the reasoning written down.** The remarks on
  `RasterKeyboardMacroService` name the drift that caused this bug as the argument against a
  second copy. That is the right lesson drawn from the right evidence, and it keeps the ack
  validation that closed a previous blocker in one place.
- **Catching the upper-casing before it shipped.** `DeviceMapExpander` upper-cases `Name`; passing
  it through would have silently degraded Raster's logs. Caught in self-review, not by a reviewer.
- **All 11 log placeholders are bound** — including the two calls that previously had no arguments
  and would have rendered a literal `{Model}`. Verified mechanically, not by eye.
- The Bluetooth/dongle limitation is written into a *test comment* rather than only a plan, so the
  next person to add a per-transport override gets a failing test that explains itself.

#### Verdict

Ship: yes — conditional on QA steps 1–5 on the connected T-Racer. No blockers: the change is
small, correct, minimal, and the one deviation from the plan was an improvement. But the line the
entire feature rests on has **no automated proof of any kind**, and its failure mode is silent, so
a green suite here means very little. Do not merge on the test result alone.
First: 1) run QA steps 1–5 and confirm the log reads `type=0x60 (caller-type=0x60)` and
`[MACRO] T-Racer UploadMacros`. 2) **S1** — add the factory-dispatch log test; it is the only
automated guard available for any part of this change. 3) **S2** — drop the `modelName` default.
