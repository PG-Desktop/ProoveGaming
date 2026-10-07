# Plan: add the Extended Keys section (Cyrex / Aress) with Rapid Snap (RS)

> **Note (2026-09-24):** the sign-in, registration, sign-out and guest-expiry logic this plan places in `MainWindow.axaml.cs` now lives in `src/ProoveGaming.App/Services/AuthSessionCoordinator.cs` ([`refactor-auth-session-flow`](../refactor-auth-session-flow/refactor-auth-session-flow-plan.md)). Same statements, same order; `MainWindow` keeps the navigation. Name map: `OnSignInSucceeded` → `CompleteSignInStateAsync` + `FinishSignInAsync`; `OnNicknameChosen` → `RegisterAsync` + `FinishRegistrationAsync`; `OnLogoutRequested` → `LogoutAsync`; `SignOutToFreshSignIn` → `SignOut`; `OnTokenRejected`'s check → `IsCurrentToken`; `OnSessionExpired` → `HandleGuestSessionExpired`; `OnGuestContinueRequested` → `StartGuestSession`; `ClearSignedInState`, `PersistAuthIfTokenPresent` and `EnqueueStorageWrite` moved as they were.

Date: 2026-09-09. Owner: architect. Phase 1 of 4 — this plan builds the **section, the shared
protocol stack, the shared storage model and the first mode (Rapid Snap)**. The other three modes
each have their own plan that plugs into the extension points defined here:

- [`../add-extended-key-socd/add-extended-key-socd-plan.md`](../add-extended-key-socd/add-extended-key-socd-plan.md) — Instant Release (SOCD)
- [`../add-extended-key-mod-tap/add-extended-key-mod-tap-plan.md`](../add-extended-key-mod-tap/add-extended-key-mod-tap-plan.md) — Mod-Tap (MT)
- [`../add-extended-key-toggle/add-extended-key-toggle-plan.md`](../add-extended-key-toggle/add-extended-key-toggle-plan.md) — Toggle (TGL)

Protocol reference (read first):
[`docs/knowledge-base/smart-infinite-protocol/advanced-keys-rapid-snap.md`](../../knowledge-base/smart-infinite-protocol/advanced-keys-rapid-snap.md).
The vendor calls these "advanced keys"; the product calls the section **Extended Keys** (design
screenshot of 2026-09-09: `DEFAULT SETTINGS` button, "Add extended key" list of four modes with an
ⓘ per mode, "Configured Extended Keys" list of rows `E [RS] W`, `E [SOCD] R  Last Input Priority`,
`T [MT] W Q  43 ms`, each with a trash icon).

> **Status note (2026-09-24 — feature hidden).** The Extended Keys feature is **switched off for
> every shipping model** until the implementation is finished: `"SupportsExtendedKeys"` is `false`
> for Cyrex and Aress in `src/ProoveGaming.DeviceHost/appsettings.json`. That single catalog flag is
> the whole switch — the App gates the nav entry, the `GetExtendedKeys` probe, the preset apply, the
> self-heal write and the Key Assignments / Macros slot protection on it — so while it is off nothing
> described below reaches the keyboard's flash and no key click is refused. Everything in this plan
> stays the contract; it simply does not run. Flip the two catalog entries back to `true` to re-enable
> it (a DeviceHost change: it takes effect after the service is reinstalled or updated). Known
> consequence of being off: the App no longer reads which slots a vendor app's own advanced-key
> records occupy, so remapping such a key on Key Assignments can orphan that record's pointer —
> accepted for a hidden feature, see PR #139.

## Goal

Give Cyrex and Aress a new **Extended Keys** details section, modelled on the Actuation section,
where the user configures the four firmware behaviours the vendor app exposes on the same board
(Rapid Snap, Instant Release, Mod-Tap, Toggle). They all live in the Smart Infinite advanced-key
storage area (`0x3A`/`0x3B`) with `0x8x` key-matrix pointers, so this phase implements that region
once — read, rebuild, write, verify — behind one generic gRPC contract and one generic storage table,
and ships Rapid Snap (two keys, the deeper-pressed one wins) as the first mode. Extended keys are
carried by presets like every other keyboard setting, which is also what stops the existing preset
key push from erasing them (see *Options considered*).

## Scope

In (this phase):

- Infrastructure: Device Info byte 8, a generic advanced-key-area codec (parse records, rebuild the
  area compactly, re-point matrix triplets, RS record encode/decode, key-code derivation), a gated
  read / read-modify-write-verify service, a factory for Cyrex and Aress.
- gRPC: `GetExtendedKeys` / `SetExtendedKeys` carrying a typed `ExtendedKey` message whose fields
  cover all four modes (so the mode plans add no proto changes), plus `supports_extended_keys` on
  the catalog entry.
- App: `NavSection.ExtendedKeys`, the section view exactly as the screenshot (Default Settings, the
  four mode rows with ⓘ tooltips, the configured list with mode badges, parameter text and delete),
  the add-pair editor for RS (Key 1 / Key 2 picked on the keyboard overlay, Cancel / Confirm), the
  overlay `extendedKey` highlight, write-failed banner, preset scoping
  (`device_extended_keys` / `preset_extended_keys`), API v2 `configuration.extended_keys`, the
  preset push re-asserting them and the key/macro pushes skipping their slots, Key Assignments /
  Macros blocking on extended-key slots.
- en + uk strings, glossary, this plan, dated note in the Smart Infinite key-assignments plan.

Out:

- The SOCD, MT and TGL editors, encoders and list formatting — their plans. In this phase their
  rows in "Add extended key" are hidden behind a per-mode availability flag that each mode plan
  turns on (no dead UI ships). Foreign records of those types found on the board are preserved
  byte-for-byte and their slots are blocked, never decoded.
- DKS (`0x80`) and END (`0x85`): never managed; preserved and blocked.
- Rampage / T800.

## Options considered

### A. Device-scoped only (board flash is the truth, no preset tables)

- Con — **fatal**: `ApplySlotKeyOverridesToDeviceAsync` (the Cyrex preset key push, run on every
  Save / switch / Discard / connect) writes a key's own record into any slot whose live record
  differs from the preset's desired record. An extended-key slot holds `{0x8x, off_lo, off_hi}`, so
  the next preset push restores the plain key record and orphans the record. Skipping "any `0x8x`
  slot" there would hide the problem but leave presets unable to carry extended keys.

### B. Preset-scoped, mirrored like actuation, one generic table (recommended)

Live rows in `device_extended_keys` are what the UI edits and what a snapshot copies;
`preset_extended_keys` is the saved copy; the keyboard push re-asserts the preset's set after the
key push and skips extended-key slots in the key and macro pushes.

- Pro: consistent with actuation / key bindings / macro bindings; the snapshot, dirty-diff,
  copy/reset and API v2 machinery has a slot for one more table; **one wide table serves all four
  modes**, so the three follow-up plans touch no schema.
- Con: `LocalStorageService` in six places plus builder/applier — the bulk of the App work.
- Con (accepted): a board configured with the vendor app is overwritten by the active preset's set on
  the first connect after this ships. Not a regression — today's key push already destroys
  vendor-written `0x84` pointers — and it matches `key_macro_bindings`.

### C. Per-mode RPCs, per-mode tables

Four RPC pairs and four table pairs. Rejected: the firmware stores them in one region written as a
unit; a per-mode write could not preserve the other modes' records without reading them anyway, and
the preset diff would span eight tables.

## Recommended approach

Option B, with the **generic model fixed in this phase** so the mode plans are additive.

### Shared model (all layers)

```
ExtendedKey
  Mode        0x81 MT | 0x82 TGL | 0x83 SOCD | 0x84 RS       (the firmware type byte)
  Slot1       physical key (MT/TGL) or first key of the pair (RS/SOCD)   — matrix slot
  Slot2       second key of the pair (RS/SOCD); -1 / unset otherwise
  Priority    SOCD priority (0 = last input, 1 = mutual cancel, 2 = key 1, 3 = key 2 — see the
              SOCD plan for the verified encoding); 0 otherwise
  TimeMs      MT / TGL threshold in milliseconds; 0 otherwise
  HoldKey     MT hold output / TGL toggled output, as a matrix-record triple {type, code1, code2}
  TapKey      MT tap output, same form
```

The App works in matrix-record triples (`0x10, modifierMask, usage` — the form its target list
already has); the DeviceHost derives the firmware's `(codeType, code)` pair from them
(`usage != 0 → (1, usage)`, else `(2, mask)`) and from the **factory table** for pair modes.

### Infrastructure

- `SmartInfiniteDeviceInfo.AdvancedKeyAreaBytes = block[8] * 64`.
- `SmartInfiniteAdvancedKeyArea` (pure codec): `ParseRecords(matrix, area)` → records
  (`Type`, `Offset`, `Bytes`, owning `Slots`), honouring SOCD's length nibble, rejecting overlaps or
  out-of-area lengths; `BuildArea(records, areaBytes)` → compact area + new offsets, in the vendor's
  group order (TGL, MT, SOCD, RS, then unmanaged DKS/END in original order — order is cosmetic, the
  pointers carry identity); `PointerRecord(type, offset)`; `KeyCodeFromRecord(KeyMatrixRecord)`;
  RS: `EncodeRapidSnap(slot1, key1, slot2, key2)` → 8 bytes / `TryDecodeRapidSnap`. **Extension
  point:** one `Encode*/TryDecode*` pair per mode, added by its plan; `ParseRecords` is
  type-agnostic.
- `ExtendedKeyEntry` (Infrastructure record mirroring the shared model) and
  `ExtendedKeysState(Entries, MaxRecords, BlockedSlots, AreaBytes, FreeBytes)`.
- `IKeyboardExtendedKeysService`: `ReadAsync` → state or null (unsupported);
  `WriteAsync(IReadOnlyList<ExtendedKeyEntry> desired)` → `Verified | Rejected | null`. Desired is
  the **full set of managed modes**; unmanaged records on the board are preserved and re-pointed.
- `SmartInfiniteExtendedKeysService`: under `DeviceGate`, on one handle: Device Info (gate: valid,
  `SupportsKeyRemap`, `AdvancedKeyAreaBytes > 0`), factory table `0x16`, current table `0x17`
  (plausibility check), area `0x3A` (exact size); parse; refuse (`Rejected`, no write) when a
  requested slot is out of range, is FN, is owned by an unmanaged record, appears in two entries, or
  the rebuilt area overflows; encode via the per-mode encoder (this phase: RS only — any other mode
  in `desired` is `Rejected` with a log line until its plan lands); restore the factory triplet on
  every slot leaving a record; point every participating slot at its record; write area `0x3B`
  (full, zero-padded) then matrix `0x18` (full 384); settle; read both back; verify record bytes,
  pointers, unmanaged records moved intact, all other slots byte-identical. Retry only on transport
  `null`.
- `DeviceExtendedKeysServiceFactory`: wired entry named Cyrex **or Aress**, `SmartInfiniteInterfaceSelector`.

### gRPC

```
message ExtendedKeyCode { int32 type_byte; int32 code1; int32 code2; }   // matrix-record form
message ExtendedKey { int32 mode; int32 slot1; int32 slot2; int32 priority; int32 time_ms;
                      ExtendedKeyCode hold_key; ExtendedKeyCode tap_key; }
rpc GetExtendedKeys → { success, supported, repeated ExtendedKey entries, int32 area_bytes,
                        int32 free_bytes, repeated int32 blocked_slots }
rpc SetExtendedKeys (vid, pid, repeated ExtendedKey entries /* full desired set */) → { success }
DeviceCatalogEntry.supports_extended_keys
```

### App

- `NavSection.ExtendedKeys`; nav entry `EXTENDED KEYS` below Actuation; visibility = keyboard ∧
  `Device.SupportsExtendedKeys` ∧ probe not hard-false (the Actuation tri-state rule).
- `DeviceDetailsViewModel.ExtendedKeys.cs` partial: probe on device open, `ExtendedKeys`
  observable list of `ExtendedKeyRowViewModel` (Mode, badge text/colour, Key1Label, Key2Label,
  extra key labels, ParameterText, delete command), `AvailableExtendedKeyModes` (this phase: RS
  only visible; SOCD/MT/TGL rows appear when their `Is*Available` flag is turned on by their plan),
  `Default Settings` command (writes an empty managed set after the shared confirm dialog pattern
  used by preset delete, since it removes everything), editor state machine
  (`ExtendedKeyEditorMode`, `EditorSlot1/Slot2` options, `EditorActiveTarget`, `CanConfirm`,
  `EditorMessage`), `SelectExtendedKey(option)` refusal rules (same key twice, key already in a
  record, FN, blocked unmanaged slot, remapped or macro-bound key), write path with in-flight guard,
  `ExtendedKeysWriteFailed` banner + reload, `PersistExtendedKeysState()` →
  `ReplaceDeviceExtendedKeys` + `StagePresetDirtyOnly()`, `ApplyStoredExtendedKeysToDeviceAsync()`
  (skip when desired equals device-confirmed), `IsExtendedKeySlot(slot)`,
  `GetExtendedKeyTooltip(key)`. **Extension points for mode plans:** `BuildRow(ExtendedKeyRecord)`
  (badge + parameter text formatting switch), the editor content switch, and the
  `Is<Mode>Available` flag.
- `ExtendedKeysSectionView.axaml`: 336-wide hidden-scroll shell (Actuation); `sectionActionButton`
  "DEFAULT SETTINGS" (existing string `DeviceDetails_Settings_DefaultSettings`); "Add extended key"
  heading; one row per available mode (icon, label, ⓘ with the mode description as tooltip); the
  editor block replaces the mode list while open (title + description, Key 1 / Key 2 chips with the
  active one outlined and a clear ✕, hint / refusal message, Cancel / Confirm with the shared
  `cancelDelete` / `confirmDelete` styles — content-sized, no fixed width); "Configured Extended
  Keys" heading; rows: key chips (underlined labels as in the design) with a coloured mode badge
  between them (RS green, SOCD yellow, MT red, TGL blue — constants, not per-view copies), optional
  parameter text, trash (`Delete-Small.svg`) button; empty-state line when none.
- Overlay: `extendedKey` key class from a new `IsExtendedKeyHighlighted` flag on
  `KeyAssignmentOptionViewModel`; editor picks use `IsSelected`. `ShowKeyboardOverlayInExtendedKeys`
  = section active ∧ overlay available (always clickable on this section); `ShowBacklight` off there.
- Key Assignments / Macros: clicking a key in an extended-key record shows a blocked message and does
  not open the dialog; tooltip names the mode and partner ("Rapid Snap · D");
  `ApplySlotKeyOverridesToDeviceAsync` and `ResyncKeyMacroBindingsAsync` skip extended-key slots
  taken from the **live rows**; `_assignments` is nulled after every extended-keys write.
- Preset push: `ApplyStoredExtendedKeysToDeviceAsync()` after `ResyncKeyMacroBindingsAsync()`.
  Empty set is a valid opinion ("none"); no "unset" state.

### Storage

`device_extended_keys(owner_kind, owner_id, vendor_id, product_id, device_key, mode, slot1, slot2,
priority, time_ms, hold_type, hold_code1, hold_code2, tap_type, tap_code1, tap_code2)` and
`preset_extended_keys(preset_id, …same columns…)`; pair modes store `slot1 < slot2`. Wired into
`SavePresetSnapshot`, `LoadPresetSnapshotToDevice`, `PresetMatchesLiveState`, `CopyPresetSnapshot`,
`ResetPresetSnapshot`, the `BackfillPresetSnapshotFromLive` table list, and
`PresetConfigurationBuilder`/`Applier` as `configuration.extended_keys = [{mode, slot1, slot2,
priority, time_ms, hold_key:{type_byte,code1,code2}, tap_key:{…}}]`.

### Hardware verification (allowed by the user for this work)

The Cyrex is connected on this machine (`USB\VID_28E9&PID_3272`). The DeviceHost is not running:
start it with `dotnet run --project src/ProoveGaming.DeviceHost` (its launch profile sets
`DOTNET_ENVIRONMENT=Development`), then launch the App from its build output folder with the
working directory set to that folder (it reads `appsettings.json` from CWD). Order: verify
`GetExtendedKeys` reads back cleanly (log the parsed records) **before** the first write; then add
an A ↔ D pair and confirm the typing behaviour; remove it; run "Default Settings". Any verify
mismatch stops the work and is reported, not retried around.

## File-level change list

Infrastructure:

- `src/ProoveGaming.Infrastructure/Classes/SmartInfiniteDeviceInfo.cs` — edit: `AdvancedKeyAreaBytes`.
- `src/ProoveGaming.Infrastructure/Classes/SmartInfiniteProtocol.cs` — edit: `TypeExtendedModTap
  = 0x81`, `TypeExtendedToggle = 0x82`, `TypeExtendedSocd = 0x83`, `TypeExtendedRapidSnap = 0x84`,
  `TypeExtendedDks = 0x80`, `TypeExtendedEnd = 0x85`, `IsExtendedKeyType`; add `0x85` to
  `KnownTypes`; fix the comment calling `0x84` "Rapid Trigger".
- `src/ProoveGaming.Infrastructure/Classes/SmartInfiniteAdvancedKeyArea.cs` — **new** codec.
- `src/ProoveGaming.Infrastructure/Classes/KeyboardExtendedKeys.cs` — **new**: `ExtendedKeyMode`
  enum, `ExtendedKeyCode`, `ExtendedKeyEntry`, `ExtendedKeysState`.
- `src/ProoveGaming.Infrastructure/Interfaces/IKeyboardExtendedKeysService.cs` — **new** (+ factory
  interface).
- `src/ProoveGaming.Infrastructure/Services/SmartInfiniteExtendedKeysService.cs` — **new**, with the
  `internal` scripted-transport constructor seam.
- `src/ProoveGaming.Infrastructure/Services/DeviceExtendedKeysServiceFactory.cs` — **new**.
- `src/ProoveGaming.Infrastructure/Classes/DeviceMap.cs`, `DeviceGroupConfig.cs`,
  `DeviceMapExpander.cs` — edit: `SupportsExtendedKeys`.

gRPC contract:

- `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto` — edit: messages and RPCs above;
  `bool supports_extended_keys` on `DeviceCatalogEntry` (next free number). Comments: full-set
  semantics, `supported=false` forbids `Set` (and is the board's verdict, while `success=false` is
  the host saying it could not read the board — round 2, S2), codes in matrix-record form, mode byte
  values.

DeviceHost:

- `src/ProoveGaming.DeviceHost/Program.cs` — edit: register the factory.
- `src/ProoveGaming.DeviceHost/Services/DeviceService.cs` — edit: `GetExtendedKeys` /
  `SetExtendedKeys` (pattern of `GetKeyActuation` / `SetPerKeyActuation`).
- `src/ProoveGaming.DeviceHost/Services/HidDeviceMapRegistry.cs` — edit: map the flag.
- `src/ProoveGaming.DeviceHost/appsettings.json` — edit: `"SupportsExtendedKeys": true` on the
  Cyrex **and Aress** entries.

App:

- `src/ProoveGaming.App/Services/IUsbHidDeviceService.cs` — edit: `ExtendedKeysInfo(Supported,
  Entries, AreaBytes, FreeBytes, BlockedSlots)`, `GetExtendedKeysAsync`, `SetExtendedKeysAsync`.
- `src/ProoveGaming.App/Services/UsbHidDeviceService.cs` — edit: the two client calls.
- `src/ProoveGaming.App/Models/ExtendedKeyRecord.cs` — **new**: the shared model + pair normaliser
  + `ExtendedKeyMode` constants (App does not reference Infrastructure).
- `src/ProoveGaming.App/ViewModels/DeviceCardViewModel.cs`, `HomeViewModel.cs` — edit: flag.
- `src/ProoveGaming.App/ViewModels/NavSection.cs` — edit: `ExtendedKeys`.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs` — edit: section flags,
  nav visibility/count/height, section-change notifications and editor cancel, `IsSectionAvailable`,
  load in the open sequence after actuation, reset on clear, and `ResetExtendedKeysState()` in
  `SetDeviceAsync`'s prologue beside the sibling device-truth caches (round 3, S8).
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.ExtendedKeys.cs` — **new**
  partial (see Recommended approach), plus `ResetExtendedKeysState()` and
  `IsExtendedKeyProtectionUnknown` (round 3, S8).
- `src/ProoveGaming.App/ViewModels/DeviceDetails/ExtendedKeyRowViewModel.cs` — **new**.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs` — edit:
  `SelectPhysicalKey` branch, blocked message on Key Assignments / Macros,
  `ShowKeyboardOverlayInExtendedKeys`, tooltip, skip sets in the two pushes, layout reload clears the
  editor, and the probe-window refusal above the protected-slot guard (round 3, S8).
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs` — edit: call
  after the macro resync; cancel editor on preset apply.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/KeyAssignmentOptionViewModel.cs` — edit:
  `IsExtendedKeyHighlighted`.
- `src/ProoveGaming.App/Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml` (+ `.cs`) —
  edit: `extendedKey` class (restate `BorderBrush`/`BorderThickness` on hover).
- `src/ProoveGaming.App/Views/DeviceDetails/Sections/ExtendedKeysSectionView.axaml` (+ `.axaml.cs`)
  — **new**.
- `src/ProoveGaming.App/Views/DeviceDetails/DeviceDetailsView.axaml` — edit: nav button (icon:
  `Keyboard-Simple.svg`), section view, overlay instance, `ShowBacklight`.
- `src/ProoveGaming.App/App.axaml` — edit: shared `extendedKeyBadge` / `extendedKeyChip` styles (used
  by the list rows and the editor — second copy rule), only if not expressible with existing styles.
- `src/ProoveGaming.App/Views/MainWindow.axaml` (+ `.axaml.cs`, `MainWindow.Controls.cs`) — edit: the
  Default Settings confirmation as a `ConfirmDialog` (`ResetExtendedKeysDialogPanel`), wired like the
  preset delete dialog (`IsResetExtendedKeysOpen`).
- `src/ProoveGaming.App/Views/DeviceDetails/Sections/MacrosSectionView.axaml` — edit: the blocked-key
  banner (same `ExtendedKeyBlockedMessage` the Key Assignments panel shows).
- `src/ProoveGaming.App/Services/LocalStorageService.cs` — edit: schema, `GetDeviceExtendedKeys`,
  `ReplaceDeviceExtendedKeys`, `GetPresetExtendedKeys`, `InsertPresetExtendedKeys`, the six
  snapshot/dirty sites.
- `src/ProoveGaming.App/Services/PresetConfigurationBuilder.cs` / `PresetConfigurationApplier.cs` —
  edit: `extended_keys`.
- `src/ProoveGaming.App/Resources/Strings.en.axaml` / `Strings.uk.axaml` — edit: nav label, section
  texts (Add extended key, Configured Extended Keys, empty state), the four mode labels and
  descriptions (all four now — the mode plans reuse them), Key 1 / Key 2, pick hint, refusal
  messages, blocked message on Key Assignments, tooltip format, delete tooltip, Default Settings
  confirm text, `ExtendedKeys_Blocked_ProbePending` (round 3, S8).

Docs:

- `docs/knowledge-base/smart-infinite-protocol/advanced-keys-rapid-snap.md` — written with this plan.
- `docs/knowledge-base/smart-infinite-protocol/magnetic-axis-travel.md` — pointer updated (done).
- `docs/CONTEXT.md` — **Extended Keys**, **Advanced-key storage area**, **Rapid Snap (RS)** (the last
  two done; add the first and fix the RS entry's plan path).
- `docs/plans/add-smart-infinite-key-assignments/add-smart-infinite-key-assignments-plan.md` — edit:
  dated note (extended-key slots blocked from remap/macro; slot push skips them).

Tests (required — see Test strategy):

- `tests/ProoveGaming.Infrastructure.Tests/SmartInfiniteAdvancedKeyAreaTests.cs` — **new**.
- `tests/ProoveGaming.Infrastructure.Tests/SmartInfiniteExtendedKeysServiceTests.cs` — **new**.
- `tests/ProoveGaming.App.Tests/DeviceDetailsExtendedKeysViewModelTests.cs` — **new**.
- `tests/ProoveGaming.App.Tests/LocalStorageServiceExtendedKeysTests.cs` — **new**.
- `tests/ProoveGaming.App.Tests/PresetConfigurationBuilderTests.cs` / `ApplierTests.cs` — edit.
- Every `IUsbHidDeviceService` fake in `tests/` gains the two members.

## Acceptance criteria

_Suspended as of 2026-09-24 while the catalog flag is off (see the status note above): with
`SupportsExtendedKeys` false, no device reaches any of the criteria below. They describe the
contract to re-verify when the feature is turned back on._

- [ ] Given a Cyrex or Aress on a current DeviceHost → its page shows an **EXTENDED KEYS** nav
  entry below Actuation; Rampage, T800 and every mouse show none.
- [ ] Given `GetExtendedKeys` returns `supported=false` → the entry disappears; given the RPC fails
  (including UNIMPLEMENTED from an old host) → the entry stays and the section shows the loading
  line; nothing is written in either case.
- [ ] Given the host could not read the board (a table or the area never arrived) → `GetExtendedKeys`
  answers `success=false`, **not** `supported=false`, so the section keeps loading and a reopen
  retries; only a board that answered and has no advanced-key area reports `supported=false`
  (round 2, S2).
- [ ] Given a board whose area does not parse against its matrix pointers (a torn two-region write)
  → the next read resets its advanced keys — every pointer slot back to its factory triplet, the area
  zeroed — and reports `supported=true` with no records, so the section and DEFAULT SETTINGS stay
  reachable; a board that still does not parse after that one reset reports `supported=false` and is
  never rewritten again (round 2, S1).
- [ ] Given the section with no records → it shows DEFAULT SETTINGS, "Add extended key" with exactly
  one row (Rapid Snap (RS)) whose ⓘ tooltip reads the RS description, and an empty "Configured
  Extended Keys" state; the keyboard image is the clickable overlay with no highlight.
- [ ] Given the RS row is clicked → the editor replaces the mode list: title, description, Key 1
  chip active → clicking **A** fills it and activates Key 2 → clicking **D** fills it; Confirm is
  enabled only with both filled; Cancel returns to the mode list with no write.
- [ ] Given Key 1 = A → picking A for Key 2 is refused with a message; FN, a key already in a
  record, a slot owned by an unmanaged record, a remapped key and a macro-bound key are each refused
  with their message; no write occurs on a refusal.
- [ ] Given Confirm → exactly one `SetExtendedKeys` call with the full desired set; on the wire one
  full-area `0x3B` write then one 384-byte `0x18` write; read-back holds
  `[0x84, 6, slotA, 1, 0x04, slotD, 1, 0x07]` with both slots pointing at it; the list shows a row
  `A [RS] D`, both keys carry the `extendedKey` highlight, the editor closes, Save Preset lights up.
- [ ] Given the board → holding A then pressing D deeper outputs D; releasing D resumes A
  (hardware check).
- [ ] Given a row's trash → one write without that record; the two slots read back as factory
  records; highlights clear; an unmanaged record present on the board is byte-identical afterwards
  and still pointed at by its slot.
- [ ] Given DEFAULT SETTINGS → after confirmation one write with an empty managed set; every managed
  record is gone, unmanaged records untouched, list empty.
- [ ] Given the device refuses or verify fails → banner shown, list reloaded from the device.
- [ ] Given a record on A/D → Key Assignments: clicking A shows the blocked message, no dialog;
  hovering A shows "Rapid Snap · D"; Macros: A cannot be bound.
- [ ] Given device A is open with records and the user goes Home and opens device B → B's page shows
  no highlight and no protected slot from A before B's own probe lands, and until it does, a click on
  any key on Key Assignments / Macros is refused with the "still reading" message instead of opening
  the dialog (round 3, S8).
- [ ] Given a saved preset with the record → switching to a preset without it removes it from the
  board and back restores it; the key push writes neither A nor D itself during either switch.
- [ ] Given the preset already holds exactly the board's set → a push issues no `SetExtendedKeys`.
- [ ] Given `free_bytes < 8` → the RS row is disabled with a "storage full" hint; the host refuses
  an overflowing set with `success=false` and writes nothing.
- [ ] `LanguageResourceParityTests` passes; uk strings fit (content-sized buttons).
- [ ] An `ExtendedKey` with a mode other than RS in `SetExtendedKeys` is rejected with
  `success=false` and no write, until the mode's plan lands.

## Test strategy

New feature — required coverage.

`tests/ProoveGaming.Infrastructure.Tests` (scripted `ISmartInfiniteIo` fake from
`SmartInfiniteActuationServiceTests`, extended to serve `0x16`, `0x17`/`0x18`, `0x3A`/`0x3B`, Device
Info byte 8 = 4 → 256-byte area):

- `SmartInfiniteAdvancedKeyAreaTests`: RS encode/decode round trip for a plain key `(1, usage)` and a
  modifier `(2, mask)`; `ParseRecords` finds an RS record owned by two slots, honours the SOCD length
  nibble on a foreign record, ignores non-`0x8x` slots, returns null/invalid on an offset or length
  past the area and on overlapping records; `BuildArea` keeps unmanaged bytes verbatim, returns
  per-record offsets, reports overflow at `areaBytes + 1`; `KeyCodeFromRecord` on a non-`0x10` type
  → null.
- `SmartInfiniteExtendedKeysServiceTests`: byte 8 = 0 → `ReadAsync` null and no `0x3A` traffic;
  write A/D → record + pointers + all other slots unchanged → Verified; write empty set → factory
  triplets restored, area zeroed; unmanaged (`0x80`) record survives byte-for-byte and its slot is
  re-pointed; slot owned by unmanaged record → Rejected, no write packet; overflow → Rejected, no
  write packet; duplicate slot across entries → Rejected; non-RS mode → Rejected; `DiscardWrites`
  → Rejected after exactly one attempt; matrix write is 384 bytes and follows the area write.

`tests/ProoveGaming.App.Tests` (harness of `DeviceDetailsActuationViewModelTests`):

- `DeviceDetailsExtendedKeysViewModelTests`: nav visibility per card/flag/probe; mode list shows RS
  only; add flow → one write with the full set; each refusal → no write + message; delete → one
  write; Default Settings → one write with empty set; failed write → banner + reload; section leave
  / preset apply cancels the editor; extended-key slot blocks the dialog and names its partner;
  `ApplySlotKeyOverridesToDeviceAsync` skips extended slots; `ApplyStoredExtendedKeysToDeviceAsync`
  writes when desired ≠ confirmed, silent when equal; row formatting for RS (`A [RS] D`, no
  parameter text).
- `LocalStorageServiceExtendedKeysTests`: replace/get normalises pair order; snapshot save/load
  (empty snapshot empties live rows); `PresetMatchesLiveState` true/false cases incl. slot order;
  copy and reset include the table.
- `PresetConfigurationBuilderTests` / `ApplierTests`: `extended_keys` round trip, malformed item
  skipped, nested `hold_key`/`tap_key` objects tolerated when null.

Not automated: the physical "deeper key wins" behaviour, real HID transfer, Avalonia layout
(UI-thread affinity; QA steps 2 and 12), the vendor app's reading of our records (QA step 9).

## QA (manual test steps)

1. Connect the PG Cyrex by cable and open its page. **Expected:** the left menu shows a new
   "EXTENDED KEYS" entry under "ACTUATION". Connect any other keyboard or a mouse. **Expected:** no
   such entry (an Aress shows it).
2. Open Extended Keys. **Expected:** a "DEFAULT SETTINGS" button, a heading "Add extended key" with
   one row "Rapid Snap (RS)" and an ⓘ; hovering ⓘ shows "When two keys are pressed together, the
   deeper-pressed key triggers."; below, "Configured Extended Keys" with an empty-state line; the big
   keyboard picture is clickable. Switch the app language to Ukrainian. **Expected:** everything
   translated, no button text clipped.
3. Click the "Rapid Snap (RS)" row, then click **A** on the picture, then **D**. **Expected:** the
   row list is replaced by the editor; "Key 1: A", "Key 2: D", both keys highlighted; "Confirm"
   becomes enabled.
4. With Key 2 active click **A** again. **Expected:** refused with a short message. Click **FN**.
   **Expected:** refused.
5. Click "Confirm". **Expected:** within about a second the editor closes; the configured list shows
   "A  RS  D" with a trash icon; "Save Preset" lights up. In a text editor hold **A**, then press
   **D** harder while still holding A. **Expected:** output switches from "a" to "d"; release D →
   "a" resumes.
6. Go to "KEY ASSIGNMENTS" and click **A**. **Expected:** no dialog; a message says the key is used
   by an extended key. Hover **A**. **Expected:** tooltip mentions Rapid Snap and D. Go to "MACROS"
   and try to bind a macro to **D**. **Expected:** refused the same way.
7. On Key Assignments remap **Q** to something. Back on Extended Keys start a new RS pair and click
   **Q**. **Expected:** refused, asking to reset the key's assignment first. Reset Q, retry.
   **Expected:** accepted.
8. Click "Save Preset". Select a preset without extended keys and apply it. **Expected:** the row
   disappears and A/D type normally. Switch back. **Expected:** the row returns and the behaviour from
   step 5 is back.
9. Optional vendor cross-check: close our app, open Keyborder-v3 → Advanced Keys. **Expected:** it
   lists the same A ↔ D Rapid Snap pair. Add a pair there (W ↔ S), close it, reopen our app.
   **Expected:** our list shows the active preset's set and the board ends up holding exactly that
   (the vendor-added pair is removed on connect — intended).
10. Unplug and replug the keyboard, reopen Extended Keys. **Expected:** rows still listed and working.
11. Click the trash on a row. **Expected:** the row disappears, both keys type normally, no banner.
12. Click "DEFAULT SETTINGS", confirm. **Expected:** all rows removed, all keys type normally.
13. Open Actuation and RGB Lighting afterwards. **Expected:** unchanged.
14. With two keyboards connected (a Cyrex with an A ↔ D record and any other), open the Cyrex, go
    Home, then open the second one and go straight to "KEY ASSIGNMENTS". **Expected:** no key on the
    new board carries the extended-key highlight at any point, and a click landing before the new
    board's read finishes is refused with "still reading this keyboard's extended keys" rather than
    opening the dialog; a second later the same click opens it normally (round 3, S8).

## Review focus

- `gRPC contract` — generic `ExtendedKey` message, full-set semantics, unimplemented-host fallback,
  mode byte values documented.
- `thread-safety` — the two-region write on one handle under the shared `DeviceGate`.
- `UI-thread affinity` — partial state is UI-thread-only; device reads land through the
  `ReferenceEquals(Device, device)` / generation fences.
- `localization` — all four mode labels/descriptions and every message in both files.

## Performance & quality

- Hot paths: none per frame. One area transfer (`areaBytes / 56` packets) + one 384-byte matrix
  transfer + read-backs per user action, on a worker under the gate; no dispatcher work.
- Allocations: codec on `byte[]`/`Span<byte>`; a list of records; no per-slot allocations in verify.
- Shared state: `DeviceGate`; skip sets read from live rows on the UI thread.
- Sync-over-async: none; storage via the existing executor pattern.
- Flash wear: the preset push skips equal sets.

## Risks

- **Vendor-extracted wire format.** Mitigation: read first on the real Cyrex (allowed); verify is
  authoritative; mismatch → `Rejected`, no retry.
- **Unmanaged records.** Moved on rebuild exactly as the vendor does; parse inconsistency → null →
  unsupported → no write; their slots are blocked.
- **Preset push overwrites vendor-configured records on first connect.** Accepted, documented.
- **Key remap / macro interplay.** Skip sets must come from live rows, not the device mirror.
- **Factory-table dependency** on removal; failure refuses the write.
- **Follow-up plans** must not change the shared model, proto or schema; if a mode needs a field
  not listed here, that is a change to this plan first.

## Implementation notes (2026-09-09, /coder)

Decisions taken while building, where the plan left room or the code forced a choice:

- **Preset push placement.** `ApplyStoredExtendedKeysToDeviceAsync` runs inside the same
  `pushActuation` branch as actuation (preset paths: Save / switch / Discard / first connect), not on
  the debounced auto-apply — the set cannot change from an RGB or DPI edit, and the equal-set skip
  would make the extra storage read pure overhead. It returns `true` when the board was written, and
  the core then re-runs `ApplyKeyOverridesToDeviceAsync` + `ResyncKeyMacroBindingsAsync` once: slots
  that just left a record were restored to their factory triplets by the host, so a remap or macro the
  incoming preset stages on them has to be written after the extended-key write, not before.
- **Editor buttons.** Cancel / Confirm use the shared `secondaryButton` / `primaryButton` classes with
  the `ConfirmDialog` sizing (`MinWidth=105 Padding=16,0`), not `cancelDelete` / `confirmDelete`: the
  red confirm belongs to destructive dialogs, and adding a pair is not one.
- **Blocked-key message.** A `warningBanner` (`ExtendedKeyBlockedMessage`, auto-hides after 5 s,
  cleared on section change) in the Key Assignments banner stack and the Macros panel — the remap
  toast carries an UNDO and a success icon, which would misdescribe a refusal.
- **`free_bytes` after a write.** Recomputed locally from the record sizes of the old and new sets
  (`ExtendedKeyRecordBytes`); the host's exact figure returns on the next read.
- **Refusal of "FN".** The overlay never offers FN (the catalogs exclude slot 70), so the check is
  "not a plain keyboard key with a code" — it also covers a placeholder slot.
- **Preset Reset** (`ResetPresetAspectsToDefaultsAsync`) does not touch extended keys: the plan lists
  no default for them, and an empty set is a real opinion, so the row-menu Reset must not invent one.
- **Row primary key** is `slot1` (a slot belongs to at most one record), which is why pairs are
  normalised (`slot1 < slot2`) on every write path (`ReplaceDeviceExtendedKeys`,
  `InsertPresetExtendedKeys`, `ExtendedKeyRecord.NormalizeSet`).
- **`SmartInfiniteDeviceInfo`** gained a positional `AdvancedKeyAreaBytes` (byte 8 × 64) and a
  `SupportsExtendedKeys` gate (`SupportsKeyRemap && AdvancedKeyAreaBytes > 0`).
- **Verify scope.** Record bytes at their new offsets and every populated matrix slot; the zero
  padding past the last record is logged but not a verify criterion.

### Fix pass — 2026-09-11, /coder (Round 2 blockers B1 + B2)

Both live in `src/ProoveGaming.App/Services/PresetConfigurationApplier.cs`; nothing else in that
round was touched (S1–S5 and the Nits stay open for the developer to schedule).

- **B1 — option A + option C, not B.** The de-dupe now builds the record, calls `Normalized()`, and
  only then checks the seen-slot set — and it reserves **both** of a pair's slots, because the board
  holds at most one record per key, so `slot1` alone never described what an item consumes. `INSERT
  OR IGNORE` (option B) was rejected: it would hide the collision from the intake that is supposed to
  decide it, and would silently pick the first of two conflicting records rather than skipping the
  loser as every other malformed shape is skipped. Option C was taken as well — `Apply` now runs each
  section through `ApplySection`, which catches and logs rather than letting one section cost the
  remaining presets, the device-state step and every pending-apply mark of that sign-in pull.
- **Logger is optional.** `ApplySection` needs somewhere to report a swallowed fault, and the class
  had no logger. It takes `ILogger<PresetConfigurationApplier>? logger = null`: DI supplies the real
  one (`AddLogging` is registered in `App.axaml.cs`), and the 18 direct constructions across four
  test files stay untouched. A required parameter would have been churn for no behaviour.
- **B2 — validation widened past the pair slots.** Beyond the journal's four lines, `mode` is
  checked against `IsPair || IsSingleKey` and `slot1 < 0` is refused for **every** mode, not only the
  pairs — the round's own example `{mode:0x82, slot1:-4}` is a single-key row, and a negative slot
  names no physical key on any mode. Pairs additionally refuse `slot2 < 0` (which covers the explicit
  `NoSlot`) and `slot2 == slot1`.
- **Corrected an existing test rather than letting it pass.** `PresetConfigurationApplierTests.cs`'s
  item commented "duplicate slot1 — dropped" asserted the over-eager half of B1 as correct (it
  followed a record that normalises to a *different* `slot1`), and it was additionally malformed —
  a Toggle with no output — so it would have been dropped either way. It is now a well-formed Toggle
  on slot 33, dropped because the normalised Rapid Snap pair above it owns 33 as its second slot,
  with the comment saying exactly that.

### Fix pass — 2026-09-11, /coder (Round 3 — S8 only)

S8 alone; S1–S7, S9, S10 and the Nits stay open for triage.

- **Half 1 — the prologue clear.** The whole per-board belief moved into one
  `ResetExtendedKeysState()` in `DeviceDetailsViewModel.ExtendedKeys.cs` (probe verdict, `info`,
  record set, blocked slots, write-failure banner, editor, blocked message, rows and highlights), and
  `SetDeviceAsync`'s prologue calls it beside `_appliedMacroSlots` / `_appliedSlotOverrides`. The
  probe still calls it too, so nothing changed for the direct-reload callers; the write-failure reload
  (`clearWriteFailure: false`) keeps its old narrower reset, which now lives in an explicit `else`.
- **The journal's "every consumer already treats null as unknown" is not true, and that is the
  point.** Verified consumer by consumer: `ApplyStoredExtendedKeysToDeviceAsync`, the two
  `_extendedKeysOnDevice is null` guards and `PersistExtendedKeysState` do return on null, but
  `ExtendedKeySlotsInUse()` (and therefore `IsExtendedKeyProtectedSlot`, `FindExtendedKeyRecord`,
  `RefreshExtendedKeyHighlights` and the overlay's `IsExtendedKeyHighlighted`) reads `?? []` — i.e.
  null answers **"not protected"**, not "unknown". Clearing alone would therefore have swapped the
  wrong-board answer for an equally wrong empty-board answer, which is why half 2 is the half that
  closes the damaging path.
- **Half 2 — the probe window refuses the click.** New `IsExtendedKeyProtectionUnknown`
  (`Device.SupportsExtendedKeys == true && _extendedKeysSupported is null`) gates `SelectPhysicalKey`
  on Key Assignments / Macros, one step above the existing protected-slot guard, with a new banner
  message. `ExtendedKeys_Refuse_Blocked` was **not** reused: it says the key belongs to a setting the
  app does not manage, which is a permanent verdict about a known record — the opposite of what this
  window means.
- **New key `ExtendedKeys_Blocked_ProbePending`**, added to both `Strings.en.axaml` and
  `Strings.uk.axaml` ("Still reading this keyboard's extended keys. Try this key again in a moment.").
  `ShowExtendedKeyBlockedMessage` and the new `ShowExtendedKeyProbePendingMessage` now share
  `ShowExtendedKeyBannerMessage` for the auto-hide plumbing.
- **Only `SelectPhysicalKey` needs the gate — checked, not assumed.** The Macros page's macro→key
  binding (`AssignMacroToSelectedButton` → `AssignMacroToSelectedKeyAsync`) and every Key Assignment
  dialog commit (`CommitRemapPickAsync`, `ResetKeyFromDialog`, `PickRemapTarget`) act on
  `SelectedPhysicalKey`, and `OpenKeyAssignmentDialog` has exactly one caller — this command. The
  device open nulls `SelectedPhysicalKey` (`ResetKeyLayoutState`), so during the probe window there
  is no selected key for those paths to write to unless this command fills one. The single other
  writer of `SelectedPhysicalKey` is `UnassignMacroFromKey`'s `SelectPhysicalKeyCore` call — that is
  **S7**, whose fix is a guard in the same method, and it inherits this one for free once added.
- **A probe RPC failure now also refuses the click, deliberately.** `_extendedKeysSupported` stays
  null when the RPC fails (an old DeviceHost's UNIMPLEMENTED), so Key Assignments stays refused on
  that board for the rest of the open. That matches what the Extended Keys section itself already
  does (it sits in its loading state and a reopen retries), and an unknown board is exactly as unsafe
  to write as an unprobed one. Recorded here because it is a behaviour change beyond the window the
  journal describes.
- **Residual, not fixed here:** a preset switch during the probe window still pushes keys with an
  empty skip set. It was equally wrong before (the skip set held the *previous* board's slots), it is
  not a click path, and fixing it means deferring or re-running the push rather than gating it —
  out of S8's scope.
- Tests: `dotnet test ./tests/ProoveGaming.App.Tests --configuration Release` → 1745/1745, baseline
  preserved. The S8 case itself is left to the test-writer round; the seam is
  `ResetExtendedKeysState()` / `IsExtendedKeyProtectionUnknown`, both `internal`.

### Fix pass — 2026-09-11, /coder (Round 3 — S7 only)

S7 alone; S1–S6, S9, S10 and the Nits stay open for triage. No new strings, no new device traffic.

- **Half 1 — the load-bearing guard.** `UnassignMacroFromKey` now refuses the same two extended-key
  cases `SelectPhysicalKey` does, placed right under the Actuation mirror it already carried and
  before the in-flight gate, so the refusal happens before `SelectPhysicalKeyCore` moves the
  selection. Without it the ✕'s reset writes the key's **own** record into a slot that holds a
  record's `{0x8x, offset}` pointer, and the record is orphaned — listed and painted, dead on the
  board.
- **Deliberately not scoped to `IsButtonsSection || IsMacrosSection`.** `SelectPhysicalKey` scopes
  its two branches because the Extended Keys page must still select keys through that command; this
  command has one affordance, and the view already withholds it outside Macros / Key Assignments —
  so any other caller is a direct invocation, and the write it would make is unsafe whichever
  section asked. Same reasoning the Actuation mirror states for itself ("this closes the
  direct-invocation path").
- **No third message invented — each existing one is truthful for its own case.** Probe still
  running → `ExtendedKeys_Blocked_ProbePending` (S8's key): the App genuinely has no verdict yet and
  the very next attempt may be allowed. Verdict in hand → `ShowExtendedKeyBlockedMessage(key)`,
  i.e. `ExtendedKeys_Blocked_Format` naming the key and the record's mode · partner, with its
  existing `ExtendedKeys_Refuse_Blocked` fallback for a slot owned by a record this build does not
  manage. The probe check must come **first**: while it is open `IsExtendedKeyProtectedSlot` answers
  "not protected" for every key, so the named verdict below it cannot be believed.
- **S8's note was right that S7 inherits the probe-window gate for free** — but only for the
  *protection* half. `SelectPhysicalKeyCore` is still reached from here directly, so the gate had to
  be restated in this method rather than borrowed; what S8 supplied for free was the message and the
  `IsExtendedKeyProtectionUnknown` predicate.
- **Half 2 — the tip stops offering the ✕.** New `IsExtendedKeyWriteBlocked(key)` in
  `ExtendedKeys.cs` collapses the two refusals into one question (`IsExtendedKeyProtectionUnknown ||
  IsExtendedKeyProtectedSlot(ResolveMatrixSlot(key))`) and `ShowKeyTip`'s `showUnassign` now requires
  it to be false. The command asks the two halves separately because each names a different reason
  to the user; the tip has only one thing to do with the answer, so it asks once.
- **Reachability, unchanged from the journal's reading:** a slot needs both a `key_macro_bindings`
  row and an extended-key record. The App's own UI cannot make one; the API intake validates the two
  sections independently, so a pulled preset can. In that state the ✕ was the only affordance the
  key offered, since the click path was already refused — which is why the honesty half matters as
  much as the guard.
- Tests: `dotnet test ./tests/ProoveGaming.App.Tests --configuration Release` → **1750/1750**,
  baseline preserved. The S7 case is left to the test-writer round; the seams are
  `IsExtendedKeyWriteBlocked` (the tip's question, `internal`) and the command's own refusal, which
  is observable through `ExtendedKeyBlockedMessage` plus the binding surviving the click.

### Fix pass — 2026-09-11, /coder (Round 2/3 — S1, S2, S3, S4, S5: the device-side group)

The five device-side findings, closed together because they share the read path. Files:
`SmartInfiniteExtendedKeysService.cs`, `IKeyboardExtendedKeysService.cs`,
`DeviceHost/Services/DeviceService.cs`, and comments only in `deviceservice.proto`. No App file was
touched (a sibling pass owned them), no new strings, no schema change. Supersedes Round 3's
"S1–S5 remain open".

- **S4 — a single-key mode may no longer sit on a slot that is not a key.** `PlanWrite`'s slot loop
  now runs `KeyCodeFromRecord(factory[slot]) is null → refuse` for **every** mode, next to the range
  and FN checks, so all four agree on what a usable slot is. The pair modes kept this incidentally
  through `PairKeyCodes`; that check stays, because `TryEncodeManaged` is the wire format's own
  precondition and must not depend on its caller. **Consequence worth knowing:** the rule is the
  vendor's `getKeyCodeAndType` — a plain `{0x10, mask, usage}` record naming something. A board whose
  factory table gives a physical key a non-keyboard type (a consumer/media record, say) would now
  refuse MT / TGL on that key as the pair modes already do. No catalog board does today (Cyrex and
  Aress are 0x10 throughout except FN), but it is the one way this check can over-refuse.
- **S3 — the pair branches now refuse the single-key fields.** New `IsPlainPair` mirrors the MT / TGL
  guards: RS refuses a `TimeMs`, a `Priority` or either output; SOCD refuses everything but its
  priority. Without it `Verified` was returned for a set the board does not hold, so the App's
  "already matches" skip could never fire and every preset apply rewrote the flash.
  **Follow-up this creates, and it belongs with S10:** `PresetConfigurationApplier` validates
  `mode`/slots/priority/time per mode but does not drop a **pair** item carrying `time_ms` or
  `hold_key`, so such a row from a foreign payload now poisons the whole set instead of merely
  rewriting the flash. The host must refuse what it cannot honour — that is the contract the
  single-key modes already had — so the fix belongs in the App's intake (one more clause in the same
  guard block), not in a host that lies about what it wrote.
- **S5 — the two output codes are range-checked, not cast.** `FromProto` became
  `TryFromProto(code, out result)`; `SetExtendedKeys` refuses the request when `type_byte`, `code1`
  or `code2` is outside a byte, the same shape Round 1 gave `mode`. `code2: 260` no longer becomes
  key 4.
- **S2 — a read now answers three things, not two.** `IKeyboardExtendedKeysService.ReadAsync` returns
  `ExtendedKeysReadResult` (`Read` / `Unsupported` / `Failed`) instead of a nullable state, and
  `ReadSnapshot` / `ReadPlausibleTable` carry the same distinction internally: a table or area that
  never arrived is `Failed`, a board with no advanced-key area or a misaligned matrix is
  `Unsupported`. `GetExtendedKeys` maps `Unsupported` to `success=true, supported=false` and `Failed`
  to `success=false`, which the App already treats as "keep the section in its loading state, retry
  on reopen" — so no App change was needed and the tri-state of acceptance criterion 2 is finally
  real. `WriteOnce` gained the same split: an unsupported board is now `Rejected` (final) rather than
  retried three more times for an answer that cannot change.
  **`GetKeyActuation` was deliberately left alone**, which the journal asked to be justified: it has
  the identical shape and the identical App-side contract, but fixing it means the same surgery on
  two services (`SmartInfiniteActuationService`, `T800KeyboardActuationService`), their interface,
  their tests and the actuation plan — a different feature's contract, and this pass was scoped to
  five findings. The harm is also smaller there: actuation's `supported=false` hides a section that
  holds no repair control and latches nothing for the session. Recorded as an open follow-up under
  the actuation plan rather than silently diverging.
- **S1 — a torn write is repaired on the next read, and the write is no longer cancellable
  mid-flight.** Of the journal's three directions, two were taken and one was not:
  - **Taken: the reset write (`RepairIncoherentBoard`).** When the area does not parse against the
    pointers, the read no longer reports "unsupported" — it does what the vendor's
    `restoreWindowsDefaultLayer` does: every slot holding an advanced-key pointer goes back to its
    factory triplet, the area is zeroed, and the board is read again. Matrix first here (the mirror of
    the write path's order, and safe for the mirror reason: dropping a pointer before the record it
    aims at never leaves a pointer aimed at a hole). One attempt, never a loop — a board that still
    does not parse is `Unsupported` and no probe rewrites its flash again.
    **Why on the read rather than behind DEFAULT SETTINGS:** the section is hidden exactly when the
    board is torn, so the control that would repair it is unreachable; and the App cannot be given a
    new verdict to act on from inside this pass. It is destructive, so the gate is narrow and worth
    stating: `ParseRecords` refuses only self-incoherent data (a pointer past the area, a TYPE that
    does not match the record it aims at, overlapping records) and never a record type this build
    merely preserves — a board the vendor app configured parses; a board with a torn write does not.
    `WriteOnce` gets the repair for free, since it goes through the same `ReadSnapshot`.
  - **Taken: the commit point.** Cancellation is now checked once, before the first packet; both
    `WriteTable` calls then run with `CancellationToken.None`. A cancelled transfer was the *ordinary*
    way to tear the two regions (a dropped packet is not), and finishing costs a few hundred
    milliseconds on a caller that has already gone away.
  - **Not taken: changing `WriteTable` to report its ack count.** It is shared with the actuation,
    key-mapping and macro services, and every one of them would start treating a lost ack as a failed
    transfer. A lost ack is not the same as a lost write — the firmware may have applied the packet
    and only the reply went missing — and the transports differ (56-byte cable vs the 24-byte 2.4G
    receiver), so the change could turn writes that work today into failures on hardware this pass
    cannot test. It also buys nothing here: the read-back verify is already authoritative, and the
    repair path recovers from a tear whatever caused it. Left open in the journal rather than
    half-done.
- **Rollback was considered and rejected.** Restoring the snapshot's own area+matrix after a failed
  verify would preserve the user's previous records instead of dropping them — but it is least likely
  to work precisely when it is needed (the transport that just failed has to carry two more full
  tables), and it doubles the packets of every genuine refusal. The repair-on-read recovers in all
  the same cases and one more (a board already torn before this build shipped).
- Verified locally against a scripted board before hand-off (throwaway cases, removed again — the
  recipes are here for the test-writer round): a transport whose `TryOpen` always fails reads as
  `Failed`, not `Unsupported`; a board whose slot points at an area byte of a different TYPE is
  repaired on read and comes back `Read` with no entries, factory triplet restored and the area
  zeroed; `ModTap` on placeholder slot 42 and the three pair-with-single-key-field shapes are each
  `Rejected` with zero packets; the golden two-mode write still verifies.
- Tests: `dotnet test ./tests/ProoveGaming.Infrastructure.Tests --configuration Release` →
  **1082/1082**, baseline preserved. The only test edit was mechanical — `ReadAsync()` call sites now
  read `.State`, and the no-area case asserts `Unsupported` rather than null. New coverage (the two
  write-side gates, the repair, the new refusals) is the test-writer round's.

### Hardware verification — 2026-09-09, PG Cyrex (VID 28E9 PID 3272), fw 0x0002, uk locale

Driven through the real App and DeviceHost (`dotnet run`, Development), reading the host log:

- Read path first: Device Info byte 8 → `advKeys=1024 B`; `GetExtendedKeys` → `0 managed, 0 blocked,
  1024/1024 bytes free`. Nav entry appeared below Actuation; section rendered as designed.
- Add A ↔ D: one `SetExtendedKeys`; on the wire `0x3B` 19 packets / 1024 B then `0x18` 7 packets /
  384 B; verified. Re-read on the next device open parsed `84 06 1F 01 04 21 01 07` with
  `slots=[31, 33]` pointing at offset 0 and `1016/1024 bytes free` — the acceptance criterion's exact
  bytes. List showed `A [RS] D`, both keys highlighted, Save Preset lit.
- Key Assignments: clicking A showed the blocked banner, no dialog; hover tip "Rapid Snap (RS) · D".
- Save Preset with the pair on the board: the push issued no `SetExtendedKeys` and no `SetKeyMapping`
  for A or D (equal-set skip + slot skip).
- Trash: one write with an empty set, verified; row gone, highlights cleared.
- Default Settings: confirm dialog (uk strings fit), one empty-set write, verified.
- Discard onto the saved preset holding the pair: the preset push wrote the record back (verified) —
  the restore path. Final delete + Save left the board with no records and the preset clean.
- **Not verified:** the physical "deeper key wins" typing behaviour (QA step 5) needs a hand on the
  board; the record bytes and pointers the vendor firmware reads are confirmed, the behaviour is not.
- Fixed on the spot: the ⓘ tooltip clipped the uk sentence (bare string tip); now a wrapping
  TextBlock.

## Open questions

Decided with the user on 2026-09-09: hardware writes allowed for verification; Cyrex **and Aress**;
local branch `feature/cyrex-extended-keys`, no push; implementation sequential (this plan, then
SOCD, MT, TGL). Remaining:

1. Nav icon: `Keyboard-Simple.svg` reused; dedicated icon is a design follow-up.
2. Refusing remapped / macro-bound keys instead of the vendor's silent remap drop — as planned.
3. API v2 `configuration.extended_keys` under the same opaque-blob assumption as
   `key_macro_bindings`.

## Test notes

### Round 3 — 2026-09-11 — the probe window (`/test-writer`)

Covers **S8 only** (Review findings → Round 3 → Tests). **5 tests added, no production code
touched.** App 1745 → **1750/1750**, Infrastructure **1082/1082**, both in Release.

`tests/ProoveGaming.App.Tests/DeviceDetailsPulledPresetApplyTests.cs` — the two halves of the window,
both driven through a real `SetDeviceAsync` with two keyboards:

- `KeyAssignments_ClickedDuringTheNewBoardsProbeWindow_OpensNoDialogAndWritesNoKeyRecord` — the
  damaging path. The Cyrex holds a record on A/D, the Aress one on Q/W; the Aress's open is parked
  before its own probe and Q is clicked. No dialog, no selected key, the banner reads
  `ExtendedKeys_Blocked_ProbePending`, and the commit the open dialog would have reached writes no
  key record. Released, the probe then refuses Q as a *named record* and lets a free key through, so
  the gate is a window and not a lock on the board.
- `ExtendedKeys_DuringTheNewBoardsProbeWindow_KeepsNeitherTheOldBoardsRowsNorItsSlots` — the cosmetic
  half: in that window the Cyrex's rows are gone, its keys unhighlighted, and
  `IsExtendedKeyProtectedSlot` false for both its slots; afterwards the page carries the Aress's
  record and only that, with no write to either board.

`tests/ProoveGaming.App.Tests/DeviceDetailsExtendedKeysViewModelTests.cs` — the single-board edges:

- `KeyAssignments_OnABoardWhoseCatalogDoesNotClaimExtendedKeys_IsNotRefusedByTheProbeGate` — a board
  that never claimed advanced keys has a permanently null verdict, so keying the gate on the verdict
  alone would lock Key Assignments on every such keyboard for the session.
- `KeyAssignments_OnABoardWhoseProbeRpcFailed_StaysRefusedForTheRestOfTheOpen` — pins the behaviour
  change the fix pass recorded deliberately (an old DeviceHost's UNIMPLEMENTED keeps the verdict
  null, and an unknown board is as unsafe to write as an unprobed one).
- `FailedWrite_KeepsTheBannerAndTheRecordSetWhileItsReloadIsInFlight` — the branch the fix
  deliberately left narrow. Its difference from the full reset exists **only while the reload's probe
  is in flight**, so the probe is parked there: every assertion made after the reload returns passes
  either way, which is exactly how a regression here would be silent.

**Where the cases live, and why it is not the one file the review named.** The review pointed at
`DeviceDetailsExtendedKeysViewModelTests.cs`, whose harness deliberately bypasses `SetDeviceAsync`
(`Open()` = set `Device`, build the layout, await the probe) — so it cannot exercise the prologue at
all, which is half of the fix. The two-board cases went to `DeviceDetailsPulledPresetApplyTests.cs`,
which already owns the per-board `BoardInfo`, the `AressCard` and the `ProbeEntered` /
`ActuationEntered` gates the self-heal round built for exactly this shape; duplicating that fake
would have made a third copy of 130 lines. Its file header now says so.

**The `ProbeEntered` / `ProbeGate` seam is the wrong one for these two, and that is worth recording**
(the same trap the self-heal round hit from the other side). Parking the arriving board at its
*extended-keys* probe proves nothing, because `LoadExtendedKeysFromDeviceAsync` clears the state
itself before it probes — under the pre-fix code the window under test would already be closed by
the time the gate is reached. The arriving board is therefore held at its **actuation** probe
(`ActuationEntered` / `ActuationGate`), one step earlier: `PopulateKeyLayout()` has already run for
the new keyboard, its extended-keys load has not. `ProbeGate` is still the right seam for the
failed-write case, which is a reload on one board, and that is where it is used (added to that file's
fake as an opt-in, inert for its other cases). No sleeps anywhere — every wait is on a signal from
the fake.

**Non-vacuity, each half reverted separately and restored byte-for-byte (md5-verified):**

- Prologue call removed (`DeviceDetailsViewModel.cs`, `ResetExtendedKeysState()` beside
  `_appliedMacroSlots`) → **both** device-switch cases fail and nothing else does. Worth reading
  carefully: the click case fails too, because with the previous board's verdict still standing
  `IsExtendedKeyProtectionUnknown` is false and the gate never fires. The two halves are not
  independent — the clearing is what makes the gate apply.
- `SelectPhysicalKey`'s gate disabled (`KeyAssignments.cs`) → the click case and the probe-RPC-failure
  case fail; the cosmetic case still passes, which is correct: it pins the clearing only.
- With the gate disabled and the click case's earlier assertions muted, it fails on
  `Assert.Empty(hid.KeyMappingWrites)` — i.e. the orphaning **write** really reaches slot 16 on the
  Aress, not merely an open dialog. That is the review's actual failure scenario, and it is the
  assertion the coder's note warned a clearing-only test would never have earned
  (`ExtendedKeySlotsInUse()` reads `?? []`, so a dropped set answers "not protected").

**Not covered, deliberately:** the residual the fix pass recorded — a preset switch landing inside the
probe window still pushes keys with an empty skip set. It is not a click path, it was equally wrong
before (the skip set held the *previous* board's slots), and the fix is to defer or re-run the push
rather than gate it. S6 and S7 remain untested, as does the review's Round 2 item about the two
Infrastructure write-side gates. *(Dated note, 2026-09-11: S7 was covered later the same day — see
the block below. S6 and the two Infrastructure gates are still untested.)*

### Round 3 — 2026-09-11 — the unassign ✕ (`/test-writer`)

Covers **S7 only** (Review findings → Round 3 → Tests). **6 tests added, no production code
touched.** App 1750 → **1756/1756**, Infrastructure **1082/1082**, both in Release.

All six live in `tests/ProoveGaming.App.Tests/DeviceDetailsExtendedKeysViewModelTests.cs`, in a new
`── The hover tip's unassign ✕ (review round 3, S7) ──` section beside the click-path seams it
mirrors. That file's harness already owns the Cyrex board, the record set and the `KeyWrites` list;
what it did not own was a macro binding, so a small `BindMacroToSlot` helper seeds the impossible
state the review names — a `key_macro_bindings` row straight into SQLite plus the record straight
onto the fake board, the way a pulled preset leaves it (the App's own UI cannot create one, because
the click path refuses the key long before a macro can be bound there).

The command's three cases:

- `UnassignMacroFromKey_OnASlotThatAlsoHoldsARecord_WritesNoKeyRecord_AndKeepsTheBinding` — the
  damaging path, **asserted on the write**. A different key is selected first (the tip acts on the
  hovered key, not the selected one), then the ✕ is invoked on A. `Assert.Empty(hid.KeyWrites)` is
  the load-bearing assertion: what orphans the record is the 3-byte key record reaching slot 31, not
  the missing banner. Also pinned: the binding row survives, `SelectedPhysicalKey` does not move, the
  banner is the record's named one (`ExtendedKeys_Blocked_Format`), and the record itself is intact.
- `UnassignMacroFromKey_DuringTheProbeWindow_SaysTheBoardIsStillBeingRead_AndWritesNothing` — the
  same ✕ one step earlier, with the board parked on `ProbeEntered` / `ProbeGate` mid-probe. It pins
  the **ordering**: the test asserts `IsExtendedKeyProtectedSlot(SlotA)` is *false* in that window —
  the set is null, so the named check below would wave the write straight through — and that the
  refusal is therefore `ExtendedKeys_Blocked_ProbePending`. Released, the same ✕ is refused by the
  named verdict instead, so the gate is a window over the open and not a second lock on the key.
- `UnassignMacroFromKey_OnAnOrdinaryBoundKey_StillUnassigns_WithRecordsElsewhereOnTheBoard` — the
  regression guard that the guard is about the SLOT, not the board: with a record on A/D, the macro
  on S unassigns exactly as before (one key write of S's own record, binding row dropped, selection
  moved onto it, no banner).

The tip's half:

- `IsExtendedKeyWriteBlocked_IsTrueForAKeyInARecord_AndFalseForAFreeOne` and
  `IsExtendedKeyWriteBlocked_CoversTheUnmanagedSlotAndTheProbeWindow_ButNotOtherBoards` — the
  predicate's four answers: a key in a record, a slot the host preserves but does not manage, every
  key while the verdict is missing, and — the one that keeps it from being over-broad — *false* on a
  board whose catalog never claimed advanced keys, where a true would delete the ✕ on every keyboard
  in the product.
- `HoverTip_WithholdsTheUnassignCross_ForAKeyTheCommandWouldRefuse` — the view wiring itself.
  `ShowKeyTip` decides the ✕'s visibility imperatively and this project has no Avalonia headless
  harness, so the `showUnassign` assignment is pinned by reading `RasterKeyboardOverlayView.axaml.cs`
  copied into the test output as `.cs.txt` — the same technique `LanguageResourceParityTests` uses
  for `DeviceTypeLabelConverter.cs` and `MacrosSectionRowBindingTests` for the macro row. One new
  `<None Include=…>` in `ProoveGaming.App.Tests.csproj`; nothing else in the test project changed.
  Without this pin the predicate could be correct and simply unconsulted, which is the exact state
  S7 found.

**Non-vacuity, each half reverted separately and restored byte-for-byte (md5-verified):**

- The guard removed from `UnassignMacroFromKey` (`KeyAssignments.cs`) → exactly the two command
  cases fail and nothing else does. The first failure in the protected-slot case is
  `Assert.Empty(hid.KeyWrites)` — the orphaning **write** really reaches slot 31, not merely a
  missing banner.
- With the guard removed **and** the probe-window case's message assertion muted, that case also
  fails on `Assert.Empty(hid.KeyWrites)` — so the probe half is not message-only either: inside the
  window the write lands for the same reason S8 recorded (`ExtendedKeySlotsInUse()` reads `?? []`,
  so a null set answers "not protected").
- `showUnassign`'s third clause removed (`RasterKeyboardOverlayView.axaml.cs`) → only the view pin
  fails; every view-model case still passes, which is correct, since the two halves are independent.
- `IsExtendedKeyWriteBlocked` narrowed to its protected-slot half → only the predicate's
  probe-window case fails.

**Not covered, deliberately:** the ✕'s own rendering (Avalonia headless is not a dependency this
project has — the pin above reads the source instead), and the recovery path out of the seeded
state. Note it exists and is not a trap: the record can still be deleted from the Extended Keys
section, after which the slot is free and the ✕ returns. What stays unreachable while the record
stands is the binding row itself, and refusing to touch it is the point of the fix.

### Round 3 — 2026-09-11 — the device-side group, S1–S4 (`/test-writer`)

Covers **S1, S2, S3, S4 and Round 2's still-open Tests item** (the two write-side gates).
**13 tests added, no production code touched.** Infrastructure 1082 → **1095/1095**, Release. All of
them live in `tests/ProoveGaming.Infrastructure.Tests/SmartInfiniteExtendedKeysServiceTests.cs`,
against the scripted-board fake that file already owns; the fake gained four seams — an open that
refuses a handle, a Device Info that never answers, a current table that arrives as non-matrix bytes,
and a per-**transfer** write log (`Transfers`, one entry per table, recorded at addr 0) that stays
unambiguous when a read repairs the board and a write follows it. The recipes came from the device-
side coder's `## Implementation notes` entry, which is exactly what they were written down for.

**S1 — the repair (4 cases).** The torn state is built the way a dropped `0x18` packet leaves it: the
area holds the record the write landed, slots 18/19 still carry the *previous* layout's SOCD pointer
at offset 0.

- `Read_AreaThatDoesNotParseAgainstThePointers_IsRepairedAndReadsBackEmpty` — `Read`, not
  `Unsupported`: no entries, no blocked slots, the whole area free, factory triplets back on 18/19
  and the area zeroed.
- `Read_RepairingATornBoard_WritesTheMatrixFirst_AndExactlyOnce` — `Transfers` is exactly
  `[0x18, 0x3B]`: matrix first (the mirror of the write path's order), two whole-table transfers and
  no more, one session.
- `Read_BoardThatStillDoesNotParseAfterTheRepair_IsUnsupportedAndIsNotRepairedAgain` — the firmware
  takes both transfers and keeps its old bytes, so the reset changes nothing: `Unsupported`, and
  still exactly two transfers. This is the "one attempt, never a loop" pin.
- `Read_BoardCarryingOnlyRecordsThisBuildDoesNotManage_IsNotRepaired` — **the safety argument, and
  the sharpest case of the round.** A DKS record, an END record and a SOCD with a priority nibble
  past 3 all parse, so they are a user's configuration: `Read`, all four slots blocked, and **zero**
  transfers with both regions byte-for-byte what they were.

**S2 — the split (3 cases).** `Read_TransportThatWillNotOpen_IsFailedNotUnsupported` and
`Read_DeviceInfoThatNeverAnswers_IsFailedNotUnsupported` both assert `Failed` *and* that the read was
retried rather than answered — a verdict stops the retries, a failure must not. The third,
`Read_CurrentTableThatIsNotAKeyMatrix_IsUnsupportedNotFailed`, is the other side: the board answered
with something this build will not build a write on, so that *is* a verdict — returned once, no
retry, no packet written. The `Unsupported`-for-no-area half was already pinned by
`Read_NoAdvancedKeyArea_IsUnsupportedAndNeverTouchesTheArea`.

**S3 — the pair guards (2 cases).** `Write_RapidSnapCarryingASingleKeyField_IsRejectedWithoutWriting`
covers all four shapes (a time, a hold output, a tap output, a priority);
`Write_SocdCarryingASingleKeyField_IsRejectedWithoutWriting` covers the three that apply and then
writes the plain pair successfully, so the case pins the guard and not merely "SOCD is broken".

**S4 — a single-key mode needs a real key (1 case).**
`Write_SingleKeyModeOnASlotThatIsNotAKey_IsRejectedWithoutWriting` puts MT and TGL on matrix
placeholder 42, then writes the same MT on a real key to show the refusal is about the slot.

**The two write-side gates, and the contract that changed under them.** Round 2 asked for "zero
packets when Device Info reports no advanced-key area, **and** when the area does not parse". The
first half is unchanged and is pinned as asked (`Write_NoAdvancedKeyArea_IsRejectedWithoutWriting`:
`Rejected`, no packets, the area never even read, one session — a board without an area will not grow
one on a retry). **The second half is no longer the contract**, and the test says so rather than
asserting the old rule: `WriteOnce` goes through the same `ReadSnapshot`, so a torn board is repaired
and the write then proceeds.

- `Write_AreaThatDoesNotParse_RepairsTheBoardFirst_ThenWritesTheDesiredSet` — `Verified`, with
  `Transfers` exactly `[0x18, 0x3B, 0x3B, 0x18]`: the repair's matrix-then-area, then the write's
  area-then-matrix. The stale pointers are gone and only the requested record is on the board.
- `Write_AreaThatStillDoesNotParseAfterTheRepair_IsRejectedWithoutWritingTheSet` — the surviving half
  of the gate, and the one worth keeping: when the repair cannot make the board coherent, the desired
  set is `Rejected` and not one packet of it is sent — only the single repair attempt was.

**S5 stays unpinned, and here is why.** The byte-range check lives in `DeviceService.TryFromProto`, a
`private static` of the gRPC service reachable only through `SetExtendedKeys(request, ServerCallContext)`;
there is no RPC harness anywhere in `tests/` and inventing one was out of scope. It has no
Infrastructure-level twin to test instead: `ExtendedKeyCode` is three `byte`s by construction, so by
the time a value reaches this service the truncation has already happened or has already been
refused. A comment at the foot of the test file records this so the gap is visible where someone
would look for it. (Reflecting on the private method was considered and rejected — it would pin a
name, not a behaviour.)

**Non-vacuity — eight separate reverts, each run alone, production files restored byte-for-byte and
sha256-verified after every one:**

- Repair call removed (`ReadSnapshot` → `Unsupported`) → exactly the 5 repair-dependent cases fail.
- Repair's two `WriteTable` calls swapped → exactly the 4 order-asserting cases fail; the
  outcome-only repair case still passes, which is right — it pins the result, not the order.
- A bounded second reset pass added before the give-up → exactly the 2 "never a loop" cases fail.
  *(The first attempt at this revert made the repair recurse unbounded and the test run hung — which
  is the point made rather too well; the bounded version is the usable experiment.)*
- `ReadAsync`'s `?? Failed` → `?? Unsupported` → exactly the 2 `Failed` cases fail.
- Both `IsPlainPair` guards removed → exactly the 2 S3 cases fail.
- `KeyCodeFromRecord(factorySlot) is null` removed from `PlanWrite` → exactly the S4 case fails. The
  existing `Write_InvalidSlot_…(SlotA, 42)` row keeps passing, which is correct and is the plan's own
  point: the pair modes are protected incidentally by `PairKeyCodes`.
- `WriteOnce`'s `Unsupported → Rejected` mapping removed → exactly the 2 write-gate cases fail.
- `ReadPlausibleTable`'s misalignment branch `Unsupported` → `Failed` → exactly the 1 case fails.
- Repair gate widened by one preserved type (fire on an END record too) → **only** the safety case
  fails, confirming it is the sole case standing between the repair and a user's DKS/END records.

**No product bug found in this area.** One candidate was chased and does not exist: S3's new
`IsPlainPair` guard would break every pair write if a zero-filled proto sub-message did not map to
`ExtendedKeyCode.None` — it does (`None` is `default`, `IsNone` is the all-zero check), so a client
that always populates `hold_key` / `tap_key` is unaffected. The already-recorded S11 (a *stored* pair
row carrying `time_ms`) is the real version of that risk and is App-side.

**Not covered, deliberately:**

- S5, for the reason above.
- The repair is silent to the user: a board whose records were dropped reads back as an ordinary
  empty board, and the read result carries no flag to say why. The device-side coder recorded this as
  a known limit of the pass ("the App cannot be given a new verdict to act on from inside this
  pass"), so it is a gap in the design rather than one in the tests — there is nothing at this layer
  to assert.
- A repair whose *post-reset read* fails returns `Failed`, so `WithRetry` re-runs the whole attempt
  and the reset is written again, up to four times. Left untested because it is harmless: the reset
  is idempotent, and after a successful matrix transfer the board parses (no pointers, an orphaned
  area) so the second attempt does not repair at all. Recorded here so it is a known shape rather
  than a surprise.
- The cancellation commit point (`WriteOnce` checks the token once, then writes both regions with
  `CancellationToken.None`) — S1's second half. The fake's transport has no way to cancel between two
  `WriteTable` calls that are deliberately uncancellable, so a test here could only assert that the
  pre-first-packet check exists, which the existing refusal cases already imply.

### Round 3 — 2026-09-11 — the App-side group S6 / S9 / S10 (`/test-writer`)

Eight cases, all in `tests/ProoveGaming.App.Tests/DeviceDetailsExtendedKeysViewModelTests.cs`
(71 in the class, suite 1756 → 1764, Release, all green). Harness additions: a
`SetMacrosSucceeds` / `SetMacrosCalls` pair on the file's `FakeHid`, a `supportsMacros` flag on
`CyrexCard` / `Open` (IsMacrosSupported gates the resync), `StallStorage`, `AressCard`,
`SelectAFreshPreset`, and the two off-board slot constants (96 = past the Cyrex's 75-slot table,
42 = an in-range placeholder naming no key).

- **S6 — two cases, both asserted on the WRITE.** `_appliedMacroSlots` is private with no seam, so
  both build the state by driving the real resync around a real extended-keys push in the order
  `ApplyCurrentStateToDeviceCoreAsync` uses (resync → extended push → resync):
  `MacroResync_AfterTheExtendedKeysPushClaimedABoundSlot_WritesNoKeyRecordIntoIt` (the ordinary
  apply, no failure) and `MacroResync_AfterAFailedLibraryUpload_StillWritesNoKeyRecordIntoARecordsSlot`
  (the route the review documented, with pass 1 parked on `MacroUploadFailed`). Each keeps an
  ordinary stale *unprotected* slot in play and asserts its clear still happens, so a `.Where` that
  merely stopped the loop firing would fail them too.
- **S9 — three cases.** `Persist_IsQueuedOnTheStorageExecutor_AndTheRowsStillLandWhenItRuns` stalls
  the executor and pins that the command has not completed and nothing is in SQLite while it is
  stalled (an inline write finishes anyway — that is the whole observable difference), then that the
  rows land on release. `Persist_OnTheOpenBoard_StoresTheRowsAndMarksItsPresetDirty` is the ordinary
  case and the control. `Persist_ADeviceSwitchWhileTheWriteIsQueued_StoresTheRows_ButDoesNotDirtyTheNewBoardsPreset`
  swaps `Device` to an Aress mid-queue: the rows still land under the captured Cyrex pair and Save
  Preset stays dark. The Aress is deliberately given live rows the selected preset's snapshot lacks,
  or the assertion would hold whether the re-check existed or not.
- **S10 — three cases.** `PresetPush_DropsRowsNamingSlotsThisBoardHasNoKeyOn_AndSendsTheRest` (both
  the out-of-range slot1 and the in-range placeholder as slot2; the rows stay in storage),
  `PresetPush_ASetDifferingOnlyByADroppedRow_WritesNothing_OnThisOpenOrTheNext` (the BEFORE-the-skip
  placement, across two opens), and `PresetPush_WhileTheKeyLayoutIsNotBuiltYet_DropsNothing`
  (`LoadKeyLayoutForDevice` deliberately not called).

**Non-vacuity.** Six targeted reverts, each restored byte-for-byte and hash-verified, each failing
exactly its own cases and nothing else: S6's `extendedSlots` clause (2 cases); the persist called
inline (2); the persist's post-await device/owner re-check alone (1, the switch case); the filter
moved after the "already matches" skip (1); the filter removed entirely (2); the filter's
`|| PhysicalKeys.Count == 0` guard alone (1, the deferred-layout case). The last revert was needed
because the deferred-layout case passes when the filter is absent altogether — it pins the guard,
not the filter, and saying so is the honest read.

**Correction to the S6 finding above — the window is wider than Round 3 recorded, and the fix
already covers it.** The finding says it opens only when pass 1 returns early on `MacroUploadFailed`.
It also opens on an ordinary, fully successful preset apply: at pass 1 the record is not on the board
yet, so the binding loop APPLIES the slot and `_appliedMacroSlots` is re-seeded with it
(`KeyAssignments.cs:1130`); the push at `Presets.cs:1055` then installs the record there; pass 2
skips the slot in the binding loop and the stale loop clears it. The first of the two cases above
reproduces exactly that and fails pre-fix with no upload failure anywhere — so the first apply of a
pulled preset carrying both a `key_macro_bindings` row and a record on one slot would have orphaned
the record. No code change is owed; the reachability paragraph in the Round 3 entry is what is stale.

**Note for later reverts in this file.** `git checkout --` does not round-trip
`DeviceDetailsViewModel.ExtendedKeys.cs`: `core.autocrlf=true` rewrites its LF endings to CRLF, so
the bytes change while `git diff` stays empty. Restore with `git show HEAD:<path> > <path>` and check
the hash.

Not covered: the Avalonia layout / UI-thread side of S9 (no headless harness here — the
`ImmutableSolidColorBrush` retype is only reachable through the existing badge assertions), and the
real HID transfer behind any of it.

### Round 3 — 2026-09-11 — the plain-pair intake guard, S11 (`/test-writer`)

Covers **S11 only**. **3 tests added, no production code touched.** App 1764 → **1767/1767** in
Release (Infrastructure untouched this pass).

`tests/ProoveGaming.App.Tests/PresetConfigurationApplierTests.cs`, class
`PresetConfigurationApplierExtendedKeysTests` — the intake mirror of the host's `IsPlainPair`:

- `Apply_PairItemCarryingSingleKeyFields_SkipsOnlyThoseItems` — the case S11 exists for. Four pair
  rows carrying a field their mode has no room for (RS with a `time_ms`, RS with a `hold_key`, SOCD
  with a `tap_key`, SOCD with both) are dropped, and a plain RS, a priority-carrying SOCD, a real
  Mod-Tap and a real Toggle all survive beside them. The two single-key rows are load-bearing: they
  are what a guard that simply dropped the whole set would fail on.
- `Apply_RapidSnapCarryingAnSocdValidPriority_IsSkippedWhileTheSocdPairKeepsIt` — the subtle half.
  `IsPlainPair` takes `priorityAllowed: false` for Rapid Snap and `true` for SOCD, so the SAME value
  has to fall on one mode and stand on the other. Priorities 1 and 3 — both well inside SOCD's 0..3
  range — drop the Rapid Snap rows and stay on the SOCD ones, and an explicit `priority = 0` on RS is
  not treated as a carried field. An out-of-range priority here would have proved nothing: the old
  code dropped those too.
- `Apply_TheSingleKeyRulesAreScopedToTheirOwnMode_AndThePairsNeverReachThem` — see below.

**The coverage S11 displaced, and why it came back in a different shape.**
`Apply_SocdPriorityOutsideTheFirmwareRange_SkipsOnlyThatItem` used to carry a Rapid Snap row with
`priority = 9`, and `Apply_ModTapOutsideTheFirmwareRules_SkipsOnlyThoseItems` one with
`time_ms = 9999`, each as the control proving those rules are judged **per mode** rather than on
every item. S11 drops such a row for carrying the field at all, so both controls went silent and were
removed. They cannot be restored literally: post-S11 a pair row can never distinguish a scoped rule
from an unscoped one, because S11 drops it either way. The live control that remains is the **other
single-key mode** — a well-formed Toggle has no tap output, so it dies the moment MT's "two keyboard
outputs" rule stops being MT-only; a well-formed Mod-Tap has one, so it dies the moment Toggle's "no
tap output" rule stops being Toggle-only. The new test pins both directions at once, with a plain RS
and a plain SOCD holding the far end (no time, no outputs — they reach neither rule and are kept, and
both would drop if either mode guard were removed) and an out-of-range SOCD keeping the range rule
itself live.

**One scoping fact is now unpinnable, on purpose.** The `mode == ExtendedKeyMode.Socd` guard on the
priority-range check is **behaviourally unobservable** after S11: for every other mode the priority is
either 0 (which `SocdPriority.IsDefined` accepts) or non-zero (dropped by the single-key guard or by
S11), so deleting that guard changes no output for any input. Rather than fake a test for it, the
suite pins the stronger statement that *is* true and *is* observable — a priority is a SOCD-only
field: every other mode carrying one is dropped (S11 for the pairs, the single-key guard for MT/TGL),
and SOCD is the only mode where one is judged against the range and kept.

**Non-vacuity, guard reverted to exactly its pre-S11 state and restored byte-for-byte
(sha256-verified, the revert confirmed identical to `HEAD` by `git diff --exit-code`):** the two S11
cases fail (4-vs-8 and 3-vs-5 stored rows) and **nothing else moves** — 1765 of 1767 still pass. The
scoping test passes both with and without the guard, which is correct: it pins the MT / Toggle / range
rules, not S11.


### Round 4 — 2026-09-12 — the device-side group, m3(a) / m3(b) / m6 / m1-host (`/test-writer`)

Covers the Infrastructure half of the Round 4 `#### Tests` block. **8 tests added, no production
code touched.** Infrastructure 1095 → **1103/1103** in Release (App untouched this pass).

`tests/ProoveGaming.Infrastructure.Tests/SmartInfiniteExtendedKeysServiceTests.cs`:

**The scripted transport m3 and the review both asked for.** `FakeBoard` gained
`DropFirstAreaChunkOfTransfer` (1-based, 0 = never) and an `AreaTransfers` counter: on the named
whole-area transfer the chunk at address 0 is acked like every other and never lands — a lost packet,
not a rejection. It has to be per-transfer rather than a flag, because `DiscardWrites` makes the board
refuse every write forever and can only ever produce the negative case; per-transfer lets the write's
own transfer be torn while the repair's and the retry's land. Address 0 specifically, because the
records live at the start of the area — a later chunk carries only the zero padding, which
`VerifyWrite` deliberately does not check, so dropping one there would verify clean.

- `Write_OneDroppedAreaChunk_IsHealedByTheSecondAttempt_AndVerifies` — **m3(a), the positive case**
  `Write_FirmwareDiscardsTheWrite_IsRejectedAfterExactlyTwoAttemptsInOneSession` could not carry. One
  chunk is lost, the matrix goes out with pointers aimed at bytes that were never written, the verify
  fails; the second attempt's snapshot read finds a board that no longer parses, repairs it, and the
  rewrite lands. `Verified`, the record and both pointers on the board, six transfers in the order
  area/matrix → matrix/area → area/matrix, one session throughout (so the retry is `WriteOnce`'s loop
  and not `WithRetry` re-opening), and a fresh read gets the pair back with nothing blocked.
- `Read_ThatHadToResetTheBoard_ReportsRepaired_AndAnOrdinaryReadDoesNot` — **m3(b), host half.** Both
  kinds of read come back as `Read` with a state, so the flag is the App's only way to tell a board
  whose records it is looking at from one whose records the repair just deleted. Torn board → true; a
  board carrying a real configuration → false; and the next read of the board the repair left behind →
  false, because the flag describes *this* read, not a board that was ever torn.
- `Read_RapidSnapRecordNamingOtherSlotsThanItsPointers_IsNotManaged` and
  `Read_SocdRecordNamingOtherSlotsThanItsPointers_IsNotManaged` — **m6.** A record at offset 0 whose
  own slot bytes name 18 and 19, with the pointers sitting on 31 and 33: no entry, both pointer slots
  blocked, `Repaired` false and not one packet written — the area parses, so nothing is reset. Each
  carries its **control**: the same bytes with the pointers on the slots they name still decode as a
  managed pair. Without it the pair would only prove that something was refused.
- `Read_PairRecordWhosePointersOnlyHalfAgreeWithIt_IsNotManaged` — one slot in common is not
  agreement. A check that merely asked whether the pointers are *among* the record's slots would let
  this through.
- `Write_AroundAPairRecordNamingOtherSlotsThanItsPointers_CarriesItVerbatim_AndRefusesItsSlots` —
  the write side of m6, and the pin on **blocked, not repaired**: its slots are refused to anyone who
  asks for them, a rewrite around it moves its eight bytes verbatim to offset 8 and re-aims its
  pointers, and the write makes exactly its own two transfers — the board is never reset on the way,
  which is what keeps a user's DKS / END records alive.
- `Write_SingleKeyOutputCarryingBothAModifierMaskAndAUsage_IsRejectedWithoutWriting` — **m1, host
  half.** `{0x10, 0x01, 0x06}` as an MT hold output, an MT tap output and a TGL output: `Rejected`,
  zero packets. Either half on its own is still a legal output, so the refusal is about naming two
  keys at once and not about masks or usages.
- `Write_PairOnFactoryKeysCarryingAMask_StillEncodesTheVendorsWay` — **the deliberate non-change, and
  the half of m1 that matters most.** Rapid Snap *and* SOCD on Left Shift (factory `{0x10, 0x02,
  0x00}`) plus a factory key whose record carries **both** a mask and a usage still encode
  `(2, mask)` and `(1, usage)` — `getKeyCodeAndType`'s own truncation, which is why the rule exists.
  `KeyCodeFromRecord` is the reader of a board key's factory identity and is read by the pair modes
  and the plain-key gate; moving the mask+usage refusal in there to tidy up the divergence would make
  the host reject pairs on real keys the vendor accepts, and would buy nothing, because a pair entry's
  stored row carries only slots and cannot produce m1's never-converging self-heal.

**Non-vacuity.** Sources snapshotted byte-for-byte before each revert and restored from that snapshot
(sha256-verified; `core.autocrlf=true` and the Round 4 changes were uncommitted, so `git checkout --`
was not used). Each revert was run against the whole project in Release:

| Case | Revert | Result of 1103 |
|---|---|---|
| m3(a) | `WriteAttempts` 2 → 1 | 2 fail: the new positive **and** the existing negative |
| m6 | both `PointersNameTheSamePair` conjuncts dropped | 4 fail — every m6 case, nothing else |
| m1 refusal | mask+usage guard deleted from `OutputKeyCode` | 1 fails |
| m1 non-change | guard **added** to `KeyCodeFromRecord` (the tidy-up itself) | 1 fails |
| m3(b) | `Repaired: true` → `false` | 1 fails |

The m3(a) revert necessarily moves two tests: both pin the same `WriteAttempts` constant from
opposite sides — the negative asserts exactly two pairs of transfers, the positive asserts the second
one heals — and no narrower revert separates them. The m1 non-change runs backwards on purpose: a
deliberate absence has no guard to remove, so the proof is to apply the regression it exists to stop.
That revert also showed the codec's own `KeyCodeFromRecord_UsageWins_ModifierFallsBack_OthersAreNull`
covers usage-only, mask-only and the three nulls but **not** mask+usage — so this test is the only
thing standing between the codebase and that tidy-up.

**Not asserted, deliberately:** the m1 refusal's *reason string*. It is observable only through
`ILogger`, and this file's style for refusals (S3, S4, the MT / TGL guards) asserts the verdict and
zero packets, never log text; adding a capturing logger for one string would couple the suite to a
message. The refusal is discriminated instead by the control beside it and by the revert landing on
that test alone. S5 stays uncovered for the pre-existing reason recorded at the foot of the file.


### Round 4 — 2026-09-12 — the App-side gaps (M2, m1-App, m2, m3b-App, m4, m5) (`/test-writer`)

Closes the App half of Round 4's `#### Tests` block. **7 tests added, no production code touched.**
App 1758 → **1765/1765** in Release (Infrastructure untouched this pass).

`tests/ProoveGaming.App.Tests/PresetConfigurationApplierTests.cs`:

- `Apply_OutputShapesTheHostRefuses_SkipOnlyThoseItems` — **m1, intake half.** One item per refused
  output shape per single-key mode: `{0x10, 0, 0}` on Mod-Tap's hold, on its tap and on Toggle's one
  output; a code at 256 and at -1; and `{0x10, 0x01, 0x06}` (Ctrl+C) carrying a mask and a usage
  together. Nine items, each dropped alone, with a real Mod-Tap, a real Toggle and a plain Rapid Snap
  surviving beside them — the assertion that matters is that the SET is not taken down, which is the
  whole of m1.

`tests/ProoveGaming.App.Tests/DeviceDetailsExtendedKeysViewModelTests.cs`:

- `PresetPush_DropsARowLandingOnABlockedSlot_AndSendsTheRest` — **m1, `DropRowsOffThisBoard` half.**
  The board-side twin of S10's off-catalog drop. A row whose slot1 is blocked AND a pair whose
  **slot2** is blocked are both dropped (a pair is judged on both of its slots, not the one it is
  keyed by), the good pair still goes out, and all three rows stay in storage, since the vendor app
  can free the slot again.
- `FailedWrite_WhoseReReadWasRepaired_ShowsTheBoardButKeepsTheOwnersRows` — **m3(b), App half.** The
  one path that reaches a repaired read: the `persist:true` branch after a refused write. Both halves
  are asserted because they pull in opposite directions — the SECTION renders the repaired board
  (a fix that simply skipped the re-read would fail this), while the ROWS keep the owner's previous
  set. The flag is then shown not to be sticky: the next accepted write persists as normal. Its
  negative twin already existed — `FailedWrite_RaisesTheBanner_ReloadsFromTheDevice_AndPersistsTheTruth`
  is the same flow with `repaired` clear, and it still persists.
- `Race_APresetApplyDuringAnInFlightWrite_EndsOnTheAppliedPresetsSet` — **m2.** The trash is parked
  inside `SetExtendedKeys` (a new `WriteEntered` / `WriteGate` seam on the fake, twin of the existing
  probe gate), a preset switch is driven in that window, then the write is released. Two assertions,
  because m2 is wrong in two places at once: while the write holds the gate the apply has made no
  progress at all — not even the snapshot load, which is the step that used to land on top of the
  write — and once both finish, preset B's set is what the board wears, what the live rows hold and
  what the section shows, with the delete's empty write still visible first so the end state cannot
  be B's push having pre-empted a delete that never ran. The mid-test absence uses a fixed 250 ms
  wait on purpose: every store here is in memory, so under the unordered code the whole apply had
  already finished.

`tests/ProoveGaming.App.Tests/DeviceDetailsPulledPresetApplyTests.cs`:

- `DebouncedEdit_WhileTheProbeHasFailed_WritesNoKeyRecordOverAPointer` — **M2, end to end.** The
  existing `SlotKeyPush_SkipsEverySlotHoldingARecordPointer_ButStillRestoresPlainSlots` pins the
  guard, but reaches it through neither of the two conditions the review names: its unread board is
  an EMPTY SUCCESSFUL probe rather than a failed one, and it calls `ApplyKeyOverridesToDeviceAsync`
  directly rather than arriving through an edit. This one is a real `SetDeviceAsync` whose
  `GetExtendedKeys` answers `null`, followed by an RGB-aspect `StageChangeAndApply` — not a key
  click, so S8's click guard has nothing to say about it. The owner's remap on a plain slot is the
  positive half (the push has to actually run, or the absence is vacuous), and the board carries an
  app-made `0x84` pair AND an unmanaged `0x80` DKS record, which is the half that never comes back.
- `SelfHeal_ThatWroteTheBoard_RunsTheKeyPushAfterIt` and `SelfHeal_ThatWroteNothing_DoesNotRunTheKeyPush`
  — **m4**, as a pair. The owner's remap sits on a plain slot nothing else in the open would push
  (the account has an applied preset, so the default-preset push stays out), so it reaches the board
  when — and only when — the heal wrote. The negative case cannot itself fail on an m4 revert; that
  is its job, and it is what stops the positive passing on a push that simply always runs.

**m5 needed nothing, and that was verified rather than assumed.** Moving `CancelExtendedKeyEditor()`
back to the first statement of `ApplyStoredExtendedKeysToDeviceAsync` fails
`Editor_AnApplyThatWritesNothing_LeavesItOpen` and nothing else (1764 of 1765 still pass). The split
the fix pass made is a correct pair and there is no gap to fill.

**Non-vacuity, each guard reverted on its own and restored byte-for-byte (sha256-verified against a
snapshot taken before any edit — `core.autocrlf=true` and the Round 4 changes were uncommitted, so
`git checkout --` is not a safe restore here):**

| Guard reverted | Fails | Rest |
| --- | --- | --- |
| `IsKeyboardOutput` reduced to the type-byte check | `Apply_OutputShapesTheHostRefuses…` | 1764 pass |
| `Takes()` loses `!_extendedKeysBlockedSlots.Contains` | `PresetPush_DropsARowLandingOnABlockedSlot…` | 1764 pass |
| `BoardMirrorHoldsExtendedKeyPointer(slot)` removed | `DebouncedEdit_WhileTheProbeHasFailed…` **and** `SlotKeyPush_SkipsEverySlotHoldingARecordPointer…` | 1763 pass |
| `DeleteExtendedKey` no longer takes `_presetApplyGate` | `Race_APresetApplyDuringAnInFlightWrite…` | 1764 pass |
| `if (_extendedKeysReadWasRepaired) return;` disabled | `FailedWrite_WhoseReReadWasRepaired…` | 1764 pass |
| the self-heal's `true` no longer re-runs the key push | `SelfHeal_ThatWroteTheBoard_RunsTheKeyPushAfterIt` | 1764 pass |

For m2 the two ordering assertions were also removed one at a time, to confirm the END STATE is
wrong under the bug and not only the ordering: with the gate gone the live rows already hold preset
B's snapshot while the write is still parked, and the last write to reach the board is the delete's
empty set rather than B's records.

## Review findings

### Round 1 — Ship: yes

Reviewed 2026-09-09: the full working-tree delta of `feature/cyrex-extended-keys` vs `main` (one
docs commit plus 68 uncommitted entries: 10 new files, 52 modified, 6 test files added/extended).
Build green; `dotnet test ./src/ProveGaming.slnx`: Infrastructure 1032, App 1644, Installer 8, all
passing. Hardware: read / add / delete / Default Settings / preset-restore verified by read-back on
the Cyrex; the physical "deeper key wins" typing (QA 5) is still unverified.

#### Plan conformance
- Implemented as planned: every File-level change list item (Infrastructure codec + service +
  factory, proto `ExtendedKey` / `ExtendedKeyCode` / `GetExtendedKeys` / `SetExtendedKeys` /
  `supports_extended_keys = 40`, DeviceHost RPCs and catalog flag, App section + editor + overlay
  highlight + Key Assignments block + skip sets + preset push, `device_extended_keys` /
  `preset_extended_keys` wired into all six snapshot sites plus backfill, re-key and guest clear,
  API v2 `extended_keys`, en + uk strings, key-assignments plan note). The shared model, proto and
  schema match the plan field-for-field, so the three mode plans need no contract change.
- Deviates from plan (all recorded in *Implementation notes*): the extended-key push runs only on
  preset paths (`pushActuation`) and re-runs the key/macro pushes when it removed records;
  Cancel/Confirm use `secondaryButton`/`primaryButton` (ConfirmDialog sizing) rather than
  `cancelDelete`/`confirmDelete`; the hover tip reads "Rapid Snap (RS) · D" because the mode label
  resource carries the abbreviation (plan wording: "Rapid Snap · D") — cosmetic; three call-site
  files were added (`MainWindow.axaml`/`.cs`/`.Controls.cs`, `MacrosSectionView.axaml`).
- Missing / partial: **unmanaged (blocked) slots are protected by the service but not by the App's
  pushes or the Key Assignments block** — see S1. The plan's Scope says DKS/END records are
  "preserved and blocked"; today the preset key push would restore a blocked slot's factory key
  and orphan the vendor's record.
- Accepted limitation (per plan): a vendor-configured set is replaced by the active preset's set on
  the first connect / next preset apply — residual risk: a Keyborder user loses their pairs once,
  silently (the section shows the board's set until the push runs). Documented in the plan and in
  the storage schema comment.

#### Code
- [x] **S1. Blocked (unmanaged) slots are not skipped by the key/macro pushes nor blocked on Key Assignments** —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.ExtendedKeys.cs:219`
  (`ExtendedKeySlotsInUse`), `DeviceDetailsViewModel.KeyAssignments.cs:1041`, `:1528`, `:1902` — Should-fix
  **Problem / why it matters:** `ExtendedKeySlotsInUse()` is built from the managed live rows only.
  A slot the host reports in `blocked_slots` (a DKS/END record, or a SOCD/MT/TGL record until its
  plan lands) still holds a `{0x8x, off}` pointer, which differs from the key's own record, so
  `ApplySlotKeyOverridesToDeviceAsync` writes the factory key into it on every Save / switch /
  connect and the vendor's record is orphaned — exactly what the plan says must not happen to
  unmanaged records. The same slot also opens the Key Assignment dialog (the block at `:1528`
  tests `IsExtendedKeySlot`, managed records only), so a remap there orphans it too.
  **Fix:** include `_extendedKeysBlockedSlots` in `ExtendedKeySlotsInUse()` (one line — both pushes
  read it), and extend the `:1528` guard to `IsExtendedKeySlot(slot) || _extendedKeysBlockedSlots.Contains(slot)`,
  with `ShowExtendedKeyBlockedMessage` falling back to `ExtendedKeys_Refuse_Blocked` wording when
  there is no managed record to name. Trade-off: none — the service already refuses these slots,
  so the App is only being made consistent with it.
- [x] **S2. Mode byte validated after truncation** —
  `src/ProoveGaming.DeviceHost/Services/DeviceService.cs:732` — Should-fix
  **Problem / why it matters:** `Enum.IsDefined(typeof(ExtendedKeyMode), (byte)entry.Mode)` casts
  the int32 first, so `mode = 0x184` passes as `0x84` and is written as Rapid Snap. Unreachable
  from this App, but the RPC is a contract for any client.
  **Fix:** refuse `entry.Mode is < 0 or > 255` before the cast (two lines).

#### Tests
- [x] Blocked-slot skip in `ApplySlotKeyOverridesToDeviceAsync` / `ResyncKeyMacroBindingsAsync` and
  the Key Assignments block for a blocked slot (after S1) — `tests/ProoveGaming.App.Tests/DeviceDetailsExtendedKeysViewModelTests.cs`
  (`SlotKeyPush_AlsoSkipsSlotsOwnedByAnUnmanagedRecord`, `KeyAssignments_ClickingAKeyOwnedByAnUnmanagedRecord_IsBlockedTheSameWay`,
  plus `EditorHint_SwitchesToTheConfirmHint_OnceBothChipsAreFilled` for the closed nit)
- [x] The macro-bound refusal (`ExtendedKeys_Refuse_MacroBound`) — `Refuses_AMacroBoundKey` (a
  `key_macro_bindings` row plus a `MacroViewModel` in `vm.Macros`, no host needed) —
  `tests/ProoveGaming.App.Tests/DeviceDetailsExtendedKeysViewModelTests.cs`
- S2 (host mode-range check) has no automated test: there is no `DeviceService` test harness in
  the repo (no RPC on this host is unit-tested); the guard is a two-line range clause.

#### Nits
- ~~`ExtendedKeyEditorHint` still says "set Key 2" once both chips are filled~~ — closed in the
  fix pass (`ExtendedKeys_ConfirmHint`, en + uk).
- `PresetConfigurationApplier.ApplyExtendedKeys` de-duplicates on `slot1` only; a pulled pair whose
  `slot2` collides with another row is stored and later refused by the host (banner). Harmless, but
  a `slot2` check would refuse it at pull time — `PresetConfigurationApplier.cs:139`.
  - **Overtaken 2026-09-11 by the B1/B2 fix pass, verified by the orchestrator:** the intake now keeps
    one `seenSlots` set and adds *both* slots of every accepted record, so a pair colliding on `slot2`
    is dropped at pull time exactly as this nit asked. No separate action is owed.
- An older DeviceHost's UNIMPLEMENTED raises `NotifyError` on every device open
  (`UsbHidDeviceService.cs:696`); consistent with `GetKeyActuation`, so accepted, but worth a
  quieter path if the two hosts are ever meant to overlap for long.
- A `TryReadSlotKeyedOnceAsync` in flight across an extended-key write lands the pre-write matrix
  into `_assignments` after the write nulled it; the next push re-diffs against a stale baseline
  for one round. Self-heals on the following read; noted, not actionable.

#### What's done well
- The service is defensive in exactly the actuation service's shape: nothing is written until
  Device Info, both matrices and the whole area read and parse; a refusal sends zero packets;
  "answered and disagreed" is final (no retry storm); every populated slot is verified after the
  two-region write. The hardware log confirms the acceptance criterion's exact record bytes.
- The codec is type-agnostic with the per-mode encode/decode as the only extension point, and the
  SOCD length nibble is handled (and tested) before any SOCD code exists.
- The App keeps the actuation discipline: device-confirmed set vs. live rows, no seeding on read
  (no phantom Save Preset), equal-set skip on the preset push, truth persisted on a failed push,
  editor cancelled on section change / layout rebuild / preset apply.
- Both language files, the parity test, content-sized buttons; the uk layout was checked live.

#### Verdict
Ship: yes — no blockers; S1 is a real gap against the plan's "preserved and blocked" promise for
unmanaged records but only bites a board configured with the vendor's DKS/END/SOCD/MT/TGL, and the
fix is a few lines. First: S1, then its tests, then S2.

### Round 2 — 2026-09-11 — branch-level pre-merge review — Ship: no

Scope: the whole branch `features/PG-662-investigation` versus `main` (12 commits, 90 files,
14 107 insertions, working tree clean), reviewed subsystem by subsystem. Every finding below was
re-verified in the code by the reviewer before being recorded, and B1 was reproduced against the real
classes. The per-phase rounds in this and the sibling plans are not re-raised. Recorded here, in the
branch's foundation plan, because the delta spans six plans; each finding names its own file.

#### Code

- [x] **B1. A pulled preset with a reversed pair crashes the sign-in pull and half-writes the preset** —
  `src/ProoveGaming.App/Services/PresetConfigurationApplier.cs:139`, with
  `src/ProoveGaming.App/Services/LocalStorageService.cs:2136` and the table's
  `PRIMARY KEY (preset_id, slot1)` — **Blocker**
  **Problem:** `ApplyExtendedKeys` de-duplicates on `slot1` **as it arrives in JSON**, but
  `InsertPresetExtendedKeys` calls `Normalized()`, which swaps `slot1`/`slot2` for pair modes. Two
  items that pass the guard can collide after normalization.
  **Why it blocks:** reproduced with the real classes —
  `[{mode:132,slot1:20,slot2:5},{mode:132,slot1:5,slot2:30}]` throws
  `SqliteException: UNIQUE constraint failed: preset_extended_keys.preset_id, preset_extended_keys.slot1`.
  The throw escapes the `RunOnStorageAsync` delegate and is swallowed by the pull's best-effort catch,
  so the rest of that sign-in pull never runs — remaining presets, the device-state step and therefore
  every pending-apply mark, notifications, the device roster — with no UI signal. The preset row,
  already committed in its own transaction, survives **partially hydrated**: rgb and actuation
  applied, extended keys rolled back, key/button/macro bindings never reached because they run after.
  On the next sign-in that uid is already known and is skipped forever, so the user keeps a silently
  mutilated preset and applies it to their board. This intake exists precisely to tolerate payloads
  this client did not write — its own comment promises a malformed item is skipped — and a throw here
  breaks that contract at its most important moment.
  **Reachability, stated honestly:** our own builder always writes normalized rows, so the trigger is
  a payload from another client, another version, or a corrupted record — which is the case the
  tolerant intake is for.
  **Solution options:** A (correct): normalize first, then de-duplicate on the normalized record and
  on **both** of its slots. B (belt): make the insert `INSERT OR IGNORE`. C (recommended alongside A):
  wrap each `Apply*` section in its own try/catch so one bad section can never cost the rest of the
  pull.

- [x] **B2. Pair-mode items get no slot validation at intake, and one bad row poisons the whole set
  permanently** — `src/ProoveGaming.App/Services/PresetConfigurationApplier.cs:165-176` — **Blocker**
  **Problem:** the intake validates SOCD priority, MT/TGL time and outputs, and the single-key
  `slot2`/`priority` rules — each with the stated rationale "stored, it would make the host refuse the
  preset's whole set". The pair modes get none of it, and `mode` itself is never checked against the
  four known values.
  **Why it blocks:** rows such as `{mode:0x84, slot1:7, slot2:-1}` (missing second slot),
  `{mode:0x83, slot1:9, slot2:9}` (paired with itself), `{mode:0x82, slot1:-4}` or an unknown mode are
  stored. `SetExtendedKeys` is a **whole-set** request, so `PlanWrite` refuses all of it and the
  user's real records never reach the board. Preset-driven pushes use `persist: false`, so the
  poisoned live rows are kept and every later apply of that preset refuses again; the section renders
  the board, so the phantom rows are invisible and the user sees only a permanent write-failure
  banner.
  **Solution options:** A: in the same guard block, skip an item whose mode is neither `IsPair` nor
  `IsSingleKey`, and for a pair mode skip when `slot1 < 0 || slot2 < 0 || slot2 == NoSlot ||
  slot2 == slot1` — four lines, the shape the other checks already use. B (additionally): have the
  host name the entry it refused, so a poisoned set is diagnosable rather than silent.

Open from this round, deliberately **not** in the current fix pass — for the developer to schedule:

- [x] **S1. A torn two-region write makes the section vanish with no in-app recovery** —
  `src/ProoveGaming.Infrastructure/Services/SmartInfiniteExtendedKeysService.cs:326-332`, `:489-496`,
  with `SmartInfiniteTransport.WriteTable` returning `true` regardless of how many chunks were acked.
  Area written, matrix not (cancellation, or a dropped `0x18` packet): pointers and area disagree,
  `ParseRecords` returns null, the board reads as "unsupported" and `IsExtendedKeysNavVisible` goes
  false — so the user cannot reach DEFAULT SETTINGS, the one control that would rewrite it. Verified
  end to end. Fix: allow a reset write when the area does not parse (the vendor's own
  `restoreWindowsDefaultLayer` does exactly that), and/or finish the matrix transfer once the first
  area byte is out.
- [x] **S2. A transport failure is reported to the App as a device verdict** —
  `src/ProoveGaming.DeviceHost/Services/DeviceService.cs:693`. `ReadAsync` returns null both for "no
  advanced-key area" and for "could not talk to the board"; the handler maps both to
  `success=true, supported=false`, hiding the section and making the preset push early-return for the
  rest of the session. Defeats the tri-state design (phase-1 acceptance criterion 2).
- [x] **S3. Pair modes ignore the single-key fields on the write path** —
  `SmartInfiniteExtendedKeysService.cs:172-190`. RS/SOCD never look at `TimeMs`/`HoldKey`/`TapKey`
  (nor RS at `Priority`), while MT/TGL refuse the pair fields. `Verified` is returned for a set the
  board does not hold, so the "already matches" skip never fires and every preset apply rewrites the
  flash.
- [x] **S4. Single-key modes accept a slot that is not a real key** —
  `SmartInfiniteExtendedKeysService.cs:389-400`; found independently by two passes. Pair modes are
  protected incidentally by `KeyCodeFromRecord`; MT/TGL get only range, FN, blocked and duplicate
  checks. A record on an unused placeholder is written, verified, consumes 8 bytes and blocks the slot
  in Key Assignments and Macros — and can never fire.
- [x] **S5. Key-code fields are truncated into `byte` without a range check** —
  `src/ProoveGaming.DeviceHost/Services/DeviceService.cs:778-781`. `code2: 260` becomes `4`: a key
  nobody configured is written and reported as success. Round 1 closed this for `mode` only.

#### Tests

- [x] B1 and B2 both need intake cases: a reversed pair that must NOT throw and must store one row;
  and each malformed pair shape (missing / negative / self-paired second slot, unknown mode) stored as
  nothing. File: `tests/ProoveGaming.App.Tests/PresetConfigurationApplierTests.cs`
  (`Apply_ReversedPairsCollidingAfterNormalisation_AreSkippedRatherThanFaultingTheInsert`,
  `Apply_PairItemsWithoutTwoRealKeys_AndUnknownModes_AreSkipped`).
- [x] `PresetConfigurationApplierTests.cs:209-211` pins the de-dupe on the wrong key: the item
  commented "duplicate slot1 — dropped" follows a record that normalizes to a different `slot1`, so it
  asserts the over-eager half of B1 as correct. Fold into the B1 fix.
- [x] The two write-side safety gates have no test: `WriteAsync` must send zero packets when Device
  Info reports no advanced-key area, and when the area does not parse against the matrix pointers.
  File: `tests/ProoveGaming.Infrastructure.Tests/SmartInfiniteExtendedKeysServiceTests.cs`.
  - **Closed 2026-09-11 (`/test-writer`) — see Test notes → Round 3 (the device-side group).** The
    file named here was the right one. Note the second gate's **contract changed** after this round
    was written: S1's repair-on-read means a torn board is repaired and the write then proceeds, so
    "zero packets when the area does not parse" is no longer what the code does. The surviving —
    and narrower — rule is pinned instead: when the repair cannot make the board coherent, the
    desired set is refused and not one packet of it is sent.

#### Nits

- `ClearData` never deletes `sync_meta`, and this branch is the first code to write guest-scoped rows
  there (`MarkDeviceStatePendingApply` is owner-kind agnostic) — one orphan row per guest session and
  device type, forever. Also corrects a claim in the `fix-account-switch-device-state` journal.
- `preset_extended_keys`' schema comment says "slot1 alone keys the row" because "a slot belongs to at
  most one record" — true of the intent, false of the key, since `slot2` is in no index. Three merge
  sites join on `slot1` alone; none is reachable today (no catalog device is both `MergeTransports`
  and `SupportsExtendedKeys`), which is worth recording rather than relying on.
- The pending-apply mark is per `(owner, vid, pid)`: with two identical units whose applied presets
  differ, the sweep's last card wins the key and **neither** unit is re-applied — the earlier journal
  records this as "one unit misses it".
- `DeviceExtendedKeysServiceFactory` hardcodes the Cyrex/Aress names while this branch added a
  `SupportsExtendedKeys` catalog flag that drives the App's nav — two sources of truth, one decides.

#### What's done well

- The protocol layer is the strongest part: the SOCD length nibble is masked on every path that walks
  the area, both 16-bit time fields go through `BinaryPrimitives` (with the vendor's own
  un-padded-hex bug pinned as a do-not-copy), unmanaged DKS/END records are preserved byte-for-byte
  and re-pointed, overflow is refused at the exact boundary, and every refusal provably sends zero
  packets. The read-back verify is authoritative everywhere — no path treats an ack as success.
- The gRPC delta is purely additive: no field renumbered, `supports_extended_keys` on the next free
  number, and an older DeviceHost is never even asked, because the App gates its probe on a catalog
  flag that host does not set.
- Storage wiring is complete: both new tables appear in every sibling site — snapshot save/load,
  reset, copy, cascade delete, dirty comparison, backfill, guest→user re-key and the data-driven
  vendor re-key list — which is the part most often missed when a table is added.
- Normalization is enforced on every local write path, and the SOCD absolute priority correctly
  follows the slot swap, so a pair picked in either order compares equal and no preset reads
  permanently dirty.

#### Verdict

Ship: no — two blockers, both in the API intake, both about data this client did not write. B1 throws
and silently truncates a sign-in pull; B2 stores rows that make the host refuse the user's real
records forever. Everything else is Should-fix or smaller, and the protocol layer is solid. First: B1
(normalize before de-duplicating), then B2 (validate the pair slots and the mode), then their tests.

### Round 3 — 2026-09-11 — second branch-level pre-merge review — Ship: no

Scope: the branch at `bf77606` (13 commits). This round (a) re-reviews the blocker fix, which no
round had seen, (b) records the UI-layer findings, each re-verified in the code by the reviewer, and
(c) restates what is still open. Handles continue from Round 2 so they stay unique.

#### Round 2 status

- **B1, B2 — closed and independently re-verified.** The reviewer re-ran the original reproduction
  against the real classes: `[{mode:132,slot1:20,slot2:5},{mode:132,slot1:5,slot2:30}]` now stores one
  row instead of throwing, and a payload of malformed pairs plus an unknown mode stores nothing but
  the one legitimate record. The fix reads well: normalize-then-de-duplicate reserving **both** slots,
  `slot1 < 0` refused for every mode (wider than the journal asked, and right — the round's own
  example was single-key), and per-section isolation so an unforeseen fault costs one section rather
  than the rest of the pull.
- **S1–S5 remain open**, unchanged and still accurate.

#### Code — new in this round

- [x] **S6. The macro resync's stale-slot loop can write a key record over an extended-key pointer** —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.KeyAssignments.cs:1046`
  (the skip) and `:1093` (the stale loop) — Should-fix
  **Problem:** a slot inside a record is skipped by the binding loop, so it never enters `nowApplied`;
  the stale loop then treats every slot in `_appliedMacroSlots` that is absent from `nowApplied` as
  "no longer holds a macro" and writes the key's own record into it — over the `{0x8x, offset}`
  pointer.
  **Reachability — narrower than first reported, verified by the reviewer:** `_appliedMacroSlots =
  nowApplied` runs at the end of a completed resync (`:1118`), and in the ordinary two-pass push the
  stale write happens *before* the extended-keys push, which then reinstalls the pointer. The window
  opens when pass 1 returns early — the documented `MacroUploadFailed` path at `:1025` — leaving the
  old slot set in place; pass 2 then runs the stale loop *after* the record is on the board.
  **Failure scenario:** preset A has macro *M* on J; switch to preset B, whose records include J; the
  library upload fails once; pass 2 writes J's key record over B's pointer. The section still lists
  the record and paints J, but J does nothing on the board.
  **Fix:** exclude the protected slots from the stale loop —
  `.Where(slot => !nowApplied.Contains(slot) && !extendedSlots.Contains(slot))`. Seeding `nowApplied`
  with the skipped slots would also work but misreports "a macro is applied there" to the next resync.
  **Closed 2026-09-11 (`/coder`, App-side group) — see the Fix pass note below.**
  **Dated correction, 2026-09-11 (`/test-writer`): the reachability paragraph above is understated,
  and the fix as written already covers the wider case.** The window does not need pass 1's
  `MacroUploadFailed` early return. On an ordinary, fully successful preset apply the record is not on
  the board yet at pass 1, so the binding loop *applies* the slot and re-seeds `_appliedMacroSlots`
  with it (`KeyAssignments.cs:1130`); the extended-keys push (`Presets.cs:1055`) then installs the
  record there; pass 2 skips the slot in the binding loop and the stale loop clears it. So the very
  first apply of a pulled preset carrying both a `key_macro_bindings` row and a record on one slot
  would have orphaned the record. `MacroResync_AfterTheExtendedKeysPushClaimedABoundSlot_WritesNoKeyRecordIntoIt`
  reproduces exactly that and fails pre-fix with no upload failure anywhere. No code change is owed —
  the paragraph is what was stale. See Test notes → Round 3 (the App-side group).

- [x] **S7. The hover tip's unassign ✕ bypasses the protected-slot guard** —
  `DeviceDetailsViewModel.KeyAssignments.cs:1638` (`UnassignMacroFromKey`), with
  `Views/DeviceDetails/Sections/RasterKeyboardOverlayView.axaml.cs:382` — Should-fix
  **Problem:** `SelectPhysicalKey` refuses a key inside a record, and `UnassignMacroFromKey` mirrors
  the *Actuation* guard (`IsActuationSection && ActuationPerKeyMode`) but has no extended-key guard;
  it calls `SelectPhysicalKeyCore` and then writes the key's own record into the slot. The view gates
  the ✕ only on "this key has a bound macro".
  **Failure scenario:** a slot carries both a `key_macro_bindings` row and a record — the App's own UI
  prevents that, but the API intake validates the two sections independently, so a pulled preset can.
  The user hovers the key on Key Assignments, the ✕ appears, and clicking it orphans the record. In
  that state the ✕ is the *only* affordance the key offers, since the click path is blocked.
  **Fix:** the load-bearing half is a guard at the top of `UnassignMacroFromKey`, exactly as the
  Actuation branch does; also drop the ✕ in the view so it stops offering an action it will refuse.

- [x] **S8. Extended-keys state is not cleared on a device switch — fix this one first** —
  `DeviceDetailsViewModel.cs:1212` (the prologue clears `_appliedMacroSlots` and
  `_appliedSlotOverrides` and nothing else) with `ExtendedKeys.cs:867` — Should-fix
  **Problem:** `_extendedKeysOnDevice`, `_extendedKeysBlockedSlots`, `_extendedKeysSupported`, the
  rows and the `IsExtendedKeyHighlighted` flags are replaced only inside
  `LoadExtendedKeysFromDeviceAsync`, which sits late in the follow-up chain — after the page is
  visible and after `PopulateKeyLayout()` has re-derived the **previous** device's rows onto the new
  keyboard's keys.
  **Failure scenario, and it is ordinary use:** with a Cyrex and an Aress both connected, open one,
  go Home, open the other. In the window before the new probe lands, the old board's records are
  highlighted on the new keyboard and its slots are refused with the old partner's name. The damaging
  direction: the new board holds a record on a slot the old one did not, so
  `IsExtendedKeyProtectedSlot` is false, the key dialog opens and a remap writes a key record into
  that slot — orphaning a record the App had not read yet.
  **Fix:** clear the four fields in the prologue beside the sibling caches (every consumer already
  treats null as "unknown, refuse"), and additionally refuse the click while `_extendedKeysSupported`
  is null on a board whose catalog flag claims support, so "unknown" stops reading as "not protected".

- [x] **S9. `PersistExtendedKeysState` runs a synchronous SQLite transaction on the dispatcher** —
  `DeviceDetailsViewModel.ExtendedKeys.cs:998` — Should-fix
  **Problem:** `ReplaceDeviceExtendedKeys` takes the storage semaphore with a blocking `Wait()` and
  runs a DELETE plus one INSERT per record inside a transaction, on the calling thread;
  `WriteExtendedKeysAsync` runs on the UI thread. This is the write twin of the read closed as S2 in
  the self-heal round — that one now goes through `RunOnStorageAsync`, this one does not.
  **Failure scenario:** Confirm / trash / DEFAULT SETTINGS while a sign-in pull is inserting macros on
  the storage executor freezes the window until that write finishes.
  **Fix:** `await _localStorage.RunOnStorageAsync(...)`, capturing owner and device first. Note
  `PersistActuationState` has the same shape, so this is an inherited pattern — but it is new traffic
  on the dispatcher and the repo rule is unconditional.

- [x] **S10. An out-of-range slot still poisons the whole set, and the intake cannot catch it** —
  `src/ProoveGaming.App/Services/PresetConfigurationApplier.cs` (post-B2) with
  `SmartInfiniteExtendedKeysService.cs:392` — Should-fix
  **Problem:** B2 closed the shapes the intake can judge, but a slot index beyond the board's matrix
  is only knowable at push time. `SetExtendedKeys` is whole-set, so one such row still makes the host
  refuse every record the user has, permanently and invisibly.
  **Fix:** drop out-of-range rows in the App's push (the per-model catalogs know the matrix size), or
  have the host refuse per entry and report which — see the Round 2 nit about the bare bool response.

#### Tests

- [x] S6, S7 and S8 each need a case; S8's is the cheapest and the most valuable (open device A with
  records, switch to device B, assert no highlight and no protected slot survive into B's page before
  its probe lands). File: `tests/ProoveGaming.App.Tests/DeviceDetailsExtendedKeysViewModelTests.cs`.
  - **S8's half closed 2026-09-11 (`/test-writer`) — see Test notes → Round 3.** The box stays
    unticked because S6 and S7 still have no case. The file named above turned out to be only half
    right: the two device-switch cases live in `DeviceDetailsPulledPresetApplyTests.cs`, which owns
    the two-board / parked-open harness; the single-board edges are in the file named here.
  - **S7's half closed 2026-09-11 (`/test-writer`) — see Test notes → Round 3 (the unassign ✕).**
    Six cases in the file named above: the command's three (the orphaning write, the probe window,
    the ordinary key that must still unassign) and the tip's three (the predicate's four answers,
    plus a source-text pin on `showUnassign`, since there is no Avalonia headless harness here).
    Following the precedent set for S8, the box stays unticked while **S6** still has no case —
    ticking it would close a bundle that still carries an open item.
  - **S6's half closed 2026-09-11 (`/test-writer`) — see Test notes → Round 3 (the App-side group),
    which also covers S9 and S10.** Two cases, both asserted on the write rather than on the section,
    each keeping an ordinary unprotected stale slot in play so a `.Where` that merely disabled the
    loop would fail them too. **The bundle is now complete, so this box is ticked.**
- [x] Still open from Round 2: the two Infrastructure write-side gates (no advanced-key area; area
  that does not parse) must be pinned to send zero packets.
  - **Closed 2026-09-11 (`/test-writer`) — see Test notes → Round 3 (the device-side group), and the
    dated note on Round 2's own Tests item for the half whose contract S1 changed.** That round also
    covered S1–S4; **S5 stays unpinned** — the byte-range check lives in `DeviceService.TryFromProto`
    and there is no RPC harness in `tests/`, nor an Infrastructure-level twin of the rule.

#### Nits

- `ApplySection` catches `Exception` broadly, which would also swallow a cancellation if one is ever
  threaded through this path. Fine today (the applier is synchronous and takes no token); worth a
  narrower catch or a comment if that changes.
- A section that faults still leaves the preset permanently half-hydrated, because the uid is known
  from then on. The blast radius is now one section instead of the whole pull, which is the right
  trade, but the residual is real and nothing retries it.
- Round 2's nits stand unchanged.

#### What's done well

- The blocker fix is better than the journal asked for: it widened the negative-slot check to every
  mode rather than only pairs, and it corrected — openly, with a comment naming the review — an
  existing test that pinned the wrong half of the bug and in fact pinned nothing at all.
- Everything praised in Round 2 still holds; nothing in the fix touched the protocol layer.

#### Verdict

Ship: no — no blockers remain, but three independent paths (S6, S7, S8) can silently orphan an
extended-key record, which is the feature's core promise and the exact symptom that started this
work, and S1 can make the section disappear with no in-app recovery. S8 is reachable in ordinary use
by anyone with two keyboards connected, which is the configuration this was developed on. First: S8
(clear the state in the prologue), then S1, then S6 and S7.

#### Fix pass — 2026-09-11 — App-side group (S6, S9, S10), recorded by the orchestrator

The App-side coder ran concurrently with the device-side one and was told not to write this file, so
that two agents could not silently overwrite each other's notes. Its decisions, verbatim in substance:

- **S6** — took the journal's `.Where(... && !extendedSlots.Contains(slot))` rather than seeding
  `nowApplied`, for the reason the journal gives: seeding would tell the next resync that a macro is
  applied on a slot that holds a record pointer. `extendedSlots` covers managed records *and*
  host-blocked slots. The narrow reachability (pass 1's `MacroUploadFailed` early return leaving
  `_appliedMacroSlots` stale for pass 2) is recorded in the code comment.
- **S9** — `PersistExtendedKeysStateAsync` now writes through the storage executor, captures
  owner/device/record-set into locals first, and re-checks device and owner after the await before
  `StagePresetDirtyOnly()` — otherwise a device switch landing mid-queue lights Save Preset on the new
  board's preset for the old board's write. Awaited rather than fire-and-forget so the
  "board confirmed → rows written → dirty recomputed" ordering the existing tests rely on survives.
  Accepted cost: the in-flight flag now spans the storage queue, so Add/Confirm stay disabled a little
  longer; the window no longer freezes, which was the point.
- **S10** — closed App-side, and the coder verified the App has a trustworthy slot source before
  choosing: the proto carries no matrix size, but for this family `ResolveMatrixSlot` returns the
  per-model catalog's index verbatim, so `DropRowsOffThisBoard` filters the stored set against the
  slots the catalog actually has keys on. Stricter than the host's range check — it also drops a
  record on an in-range but unused placeholder. Applied only in the storage-sourced push, **before**
  the "already matches" skip (so a set differing only by a dropped row still writes nothing), a no-op
  while `PhysicalKeys` is empty, and it logs when it drops, since nothing in the UI could otherwise
  name a key that does not exist.
- **Outside the three items, and flagged as such:** `ExtendedKeyRowViewModel.BadgeBrushFor` now
  returns `ImmutableSolidColorBrush`. Forced by S9, not opportunistic — the new await resumes off the
  dispatcher, and `SolidColorBrush.Color` is a styled property whose `VerifyAccess` then throws. These
  four badge colours are constants, so an immutable brush is the correct type regardless; views bind
  `IBrush` so XAML is unaffected, and two test assertions were retyped.

#### New in this pass

- [x] **S11. The intake still stores a pair item carrying single-key fields, and S3 made that
  fatal** — `src/ProoveGaming.App/Services/PresetConfigurationApplier.cs` — Should-fix
  **Problem:** Round 2's S3 fix gave the host the mirror guard the single-key modes already had, so a
  Rapid Snap or SOCD entry carrying `time_ms`, `priority` or an output is now **refused**. The intake
  does not drop such an item: B2 taught it to validate pair *slots*, not pair *fields*.
  **Why it matters now:** before S3 such a foreign row merely caused a redundant flash rewrite; after
  S3 it makes the host refuse the whole set, so none of the user's real records reach the board — the
  same permanent, invisible failure B2 was about. The device-side coder found this while fixing S3
  and could not fix it, being scoped out of App files.
  **Fix:** extend the intake's pair branch to skip an item with a non-zero `time_ms`, a non-zero
  `priority` on Rapid Snap, or either output present — mirroring the host's new rule, which is the
  contract the single-key branch already mirrors.
  **Closed 2026-09-11 (`/coder`).** `ApplyExtendedKeys` gained a pair-field guard directly after the
  pair-slot guard, mirroring `IsPlainPair` term for term: a non-zero `time_ms`, either output present,
  or a non-zero `priority` **on Rapid Snap only** now drops the item, since SOCD's LEN nibble
  legitimately carries a priority (already range-checked above). **Dropped rather than field-cleared**,
  deliberately: every other malformed shape in this method drops, and clearing would fabricate intent —
  a pair carrying `hold_key` / `time_ms` is evidence the producer meant a *different mode*, so
  stripping the fields would silently bind two physical keys into Rapid Snap the user never asked for,
  permanently and all the way to flash. It would also make build → apply → build non-idempotent for
  that item.
  **Fallout, and a coverage loss to know about:** two existing tests carried a Rapid Snap control row
  asserting the pre-S3 rule (`Apply_SocdPriorityOutsideTheFirmwareRange_SkipsOnlyThatItem`'s
  "RS: priority ignored, kept" and `Apply_ModTapOutsideTheFirmwareRules_SkipsOnlyThoseItems`'s
  "RS: time ignored, kept") and failed against the fix. Both rows lost the foreign field and both
  comments now explain that the range rules are SOCD-/MT-only *because only those records have room
  for the field*. The orchestrator verified the edits are honest — each test still tests what it is
  named for — but those control rows were also what pinned the two rules' **scoping**, and nothing
  pins it now; handed to `/test-writer`.
  **Pinned 2026-09-11 (`/test-writer`) — see Test notes → Round 3 (the plain-pair intake guard).**
  Three cases: the positive drop, the Rapid Snap / SOCD asymmetry on a priority *inside* SOCD's range
  (an out-of-range one would have passed pre-S11 and proved nothing), and a replacement for the
  scoping coverage the two edited tests lost, rebuilt around the other single-key mode because a pair
  row can no longer discriminate a scoped rule from an unscoped one. App 1764 → 1767/1767, Release.
  **Checked and clean elsewhere:** `"extended_keys"` has exactly one JSON intake; the two
  `LocalStorageService` sites are trusted read-backs, and `UsbHidDeviceService.FromProto` cannot
  produce a pair carrying a foreign field (the 8-byte RS/SOCD record has no room for one).
  Build green, App suite 1764/1764 in Release, both verified by the orchestrator.

- [ ] **S12. `PersistActuationState` has S9's blocking shape and fires far more often** —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Actuation.cs:723` —
  Should-fix
  Raised by the App-side coder while closing S9, and it argues this one deserves the higher priority:
  the same synchronous `Replace…` inside the storage semaphore on the dispatcher, but on every
  actuation commit and per-key write rather than the occasional extended-key edit. Harder, too — it
  computes `rows` and assigns `_actuationOnDevice = rows` around the write, so the conversion needs
  the same capture-and-re-check treatment plus a decision about when that field updates. Out of this
  branch's scope (it predates it); worth its own item.

### Round 4 — 2026-09-11 — external review on PR #136 — Ship: no

Posted as a single comment on <https://github.com/wezom-company/proove-gaming/pull/136> (the PR also
carries an APPROVED review from the same reviewer; these findings came with it). The comment was
written in Russian and is translated here, because this repo's documentation is English-only. Each
finding keeps the reviewer's own `file:line`, failure scenario and refutation notes, and the
reviewer's own confidence grade (`CONFIRMED` / `PLAUSIBLE`). **M1, M2, m4 and m5 were independently
re-verified against the code by the orchestrator before any fix was dispatched.**

#### Code

- [x] **M1. An account switch silently overwrites the incoming owner's unsaved settings with a preset
  snapshot** — `src/ProoveGaming.App/ViewModels/HomeViewModel.cs:1790-1797` (the sweep writes the
  mark), `DeviceDetailsViewModel.Presets.cs:832-841` (mouse: consume then `LoadPresetSnapshotToDevice`),
  `:1914-1942` with `DeviceDetailsViewModel.cs:1359` (keyboard: `MaterializePulledPresetAsync` then
  `ApplyPresetToDeviceAsync` then `Presets.cs:651`) — Major, `CONFIRMED`
  **Problem:** the `pending_apply` mark was designed to mean "apply a preset that arrived from the
  server and has never been loaded on this machine", so its consumers **overwrite the live rows with
  the preset snapshot** (`LoadPresetSnapshotToDevice` deletes `device_rgb`, `device_settings`,
  `button_bindings`, `key_bindings`, `key_macro_bindings`, `device_actuation` and
  `device_extended_keys` — `LocalStorageService.cs:3681-3701`). Commit `14be823` made the owner-change
  sweep write that same mark, so an ordinary account switch now destroys the incoming account's
  applied-but-unsaved state. The app itself calls that state legitimate at `Presets.cs:762-765`:
  "Reading the snapshot instead would discard a legitimately applied-but-unsaved state".
  **Failure scenario:** account B has a mouse on custom preset P, changes DPI and colour without
  pressing Save Preset (Save Preset lights). B signs out — `ClearSignedInState`
  (`MainWindow.axaml.cs:460-475`) shows no unsaved-changes dialog, so B's live rows survive. A signs
  in, then B again, in the same process. `ShouldReapplyDeviceState(B, A)` is true, the sweep queues
  `MarkDeviceStatePendingApply(B)` with value P and calls `ReapplyOnConnect`;
  `ReapplyLastCustomPresetAsync` passes every guard and consumes the mark;
  `LoadPresetSnapshotToDevice(P)` overwrites B's live rows and pushes the snapshot to the mouse. B's
  DPI and colour are gone with no warning. The keyboard hits the same thing on its next page open.
  **Why it matters:** silent loss of user configuration, and a regression — before this branch the
  sweep pushed B's live rows for the mouse. It is also restart-dependent: after a restart the mark is
  not written and the self-heal pushes live rows, so the same user gets the snapshot one time and
  their own edits the next.
  **Reviewer's refutation attempt:** steel-manned "reverting to the preset is correct on a switch"
  and rejected it on three grounds — the `fix-account-switch-device-state` plan's own acceptance
  criterion ("mouse ... unchanged"), the explicit rule at `Presets.cs:762-765`, and the
  `add-extended-keys-api-sync` plan's dated 2026-09-11 note saying the mark no longer owns the
  account-switch case. Also checked that the unsaved state is not discarded earlier
  (`DiscardUnsavedChanges` only runs on Back, `Presets.cs:2043-2065`) and that a mark left unconsumed
  by the sweep is still taken by the first page open.
  **Solution options:** **A (minimal, matches the api-sync plan's note)** — stop writing the mark in
  the sweep. Extended keys are already covered on a switch by the self-heal, and keyboard
  remaps/actuation fall back to the accepted limitation in
  `../fix-extended-keys-self-heal/fix-extended-keys-self-heal-plan.md:66-72`. Update the
  `fix-account-switch-device-state` plan (criteria 1-5) and `docs/CONTEXT.md`. **B (keeps
  `14be823`'s goal)** — give the switch mark its own value (`switch:<id>`), whose consumer pushes
  **live rows** via `ApplyCurrentStateToDeviceAsync(pushActuation: true)` rather than
  `LoadPresetSnapshotToDevice`, leaving the snapshot path to the pull mark only.
  **Orchestrator's decision: Option A.** It restores the pre-branch behaviour the reviewer shows was
  regressed, and it is what the api-sync plan already states the design to be. B would keep a second,
  differently-shaped mark alive to preserve a goal the design has since moved away from.

- [x] **M2. While the board is unread (the probe window, or a probe that failed) any debounced
  keyboard push overwrites the extended-key pointers** —
  `DeviceDetailsViewModel.KeyAssignments.cs:1957-1983` (`ApplySlotKeyOverridesToDeviceAsync`),
  `DeviceDetailsViewModel.ExtendedKeys.cs:393-402` (`ExtendedKeySlotsInUse` with its `?? []`), `:985-990`
  (a failed probe leaves `_extendedKeysOnDevice` null), `Presets.cs:1023-1027` — Major, `CONFIRMED`
  **Problem:** the key push's skip set is built from `_extendedKeysOnDevice` and
  `_extendedKeysBlockedSlots`. Until the probe answers — or forever, if it failed — both are empty,
  so no slot is skipped. `ApplySlotKeyOverridesToDeviceAsync` then compares the board mirror
  (`_assignments`), finds the record's `{0x8x, off_lo, off_hi}` pointer differs from the key's own
  record, and **writes the key record over the pointer**. That is precisely what the skip set exists
  to prevent.
  **Failure scenario:** a Cyrex carries an app-made RS(A,D) and/or a vendor-configured DKS record.
  The page opens and `GetExtendedKeys` fails (`success=false`). Key Assignments correctly blocks
  clicks (S8), but that only covers clicks. The user changes an RGB colour; 250 ms later the debounced
  `ApplyCurrentStateToDeviceCoreAsync` runs the key push, which writes key records onto A, D and the
  DKS slot. The records are orphaned: A and D type ordinary letters, the DKS is dead. The self-heal is
  skipped this open (`_extendedKeysSupported != true`), the app's own records come back only on a
  later open with a successful probe, and the **unmanaged** DKS/END records never come back at all —
  the app does not store them, and the next rebuild discards the orphaned area bytes.
  **Why it matters:** it breaks both of the feature's core promises — that a key push cannot orphan a
  record, and that unmanaged records are preserved byte-for-byte. The plan's residual note on S8
  (`plan:576-579`) describes the window as "a preset switch during the probe window"; the real
  trigger is **any** debounced keyboard edit, and on a failed probe the window is the whole open.
  **Reviewer's refutation attempt:** looked for a guard on the auto-apply path.
  `ApplyStoredExtendedKeysToDeviceAsync` is guarded (`ExtendedKeys.cs:1135-1149`) but the key push
  does not call it; the "no baseline" branch (`:1974-1980`) does not save it, because `_assignments`
  read fine so the comparison branch is taken; `IsExtendedKeyProtectionUnknown` is only used on click
  paths.
  **Solution options:** **A (minimal and universal)** — in `ApplySlotKeyOverridesToDeviceAsync` and in
  `ResyncKeyMacroBindingsAsync`'s stale loop, skip a slot whose **board mirror** `_assignments[slot]`
  carries a type byte in `0x80..0x85`. Safe unconditionally: removing a record is the extended-keys
  push's own job, and it restores the factory triplet before re-running the key push
  (`Presets.cs:1055-1059`), by which point the pointer is gone. **B (narrow, following S8)** — while
  `IsExtendedKeyProtectionUnknown`, skip the slot push and the macro resync entirely and run them once
  after a successful probe.
  **Orchestrator's decision: Option A.** It protects unmanaged records too, it needs no new state, and
  unlike B it does not defer legitimate work behind a probe that may never succeed.

- [x] **m1. Whole-set poisoning: the intake and `DropRowsOffThisBoard` still do not mirror the host's
  rules** — `PresetConfigurationApplier.cs:240-241` (`IsKeyboardOutput`),
  `DeviceDetailsViewModel.ExtendedKeys.cs:410-431` (`DropRowsOffThisBoard`); host rules at
  `DeviceService.cs:800-812`, `SmartInfiniteExtendedKeysService.cs:293-300` and `:463-466`, codec at
  `SmartInfiniteAdvancedKeyArea.cs:205-211` — Minor, `CONFIRMED` (mechanism; the trigger is a foreign
  payload or the vendor app)
  **Problem:** S11 was closed as "mirrors `IsPlainPair` term for term", but the MT/TGL **outputs** got
  no mirror. `hold_key: {type_byte:16, code1:0, code2:0}` passes `IsKeyboardOutput` (it is not
  `IsNone`), while the host's `KeyCodeFromRecord` returns null and refuses the **whole** set on every
  push and every self-heal, with a banner on every open — the B2 / S11 class again. A `code1`/`code2`
  above 255 or below 0 passes the intake and is refused by the host's `TryFromProto` (S5), again
  whole-set. `{0x10, mask non-zero, usage non-zero}` (Ctrl+C, say) is silently encoded usage-only by
  the host (`KeyCodeFromRecord:208`) and verified, so the read-back is `{0x10,0,usage}`, the rows never
  match the board, and the self-heal writes flash on **every** open without ever converging. From the
  board side, an owner row on a slot the vendor app gave a DKS record (so it lands in
  `_extendedKeysBlockedSlots`) is not dropped by `DropRowsOffThisBoard`, so the whole set is refused
  on every open.
  **Fix:** `IsKeyboardOutput` must require `Code1`/`Code2` in `0..255`, a non-zero code, and **exactly
  one** of mask/usage; `DropRowsOffThisBoard` must also drop rows landing on
  `_extendedKeysBlockedSlots`. The host should refuse mask+usage rather than truncate silently,
  following S3.
  **Reviewer's refutation attempt:** the app's own picker cannot produce these values — the
  Cyrex/Aress catalogs give modifiers `usage=0` (`CyrexKeyCatalog.cs:119-140`). Hence Minor rather
  than Major; the counter-argument the reviewer records itself is that the intake exists precisely for
  foreign data, and this branch graded B2, the same class, a Blocker.

- [x] **m2. Race: switching preset during an in-flight extended-keys write silently skips the
  preset's push and then overwrites its rows** — `DeviceDetailsViewModel.ExtendedKeys.cs:1024-1026`
  (in-flight returns false), `:1198`, `:1065-1066`, with `Presets.cs:1055` — Minor, `PLAUSIBLE`
  **Problem:** the user presses Confirm or the trash (a 1-2 s write) and immediately switches preset.
  `ApplyStoredExtendedKeysToDeviceAsync` gets `false` from the in-flight guard — the same answer as
  "already matches" — so there is no banner and no re-run of the key push. The user's write then
  completes and `PersistExtendedKeysStateAsync` writes the pre-switch set into the live rows, on top
  of the snapshot just loaded for B. Preset B counts as applied, the board and the rows carry A's set,
  and B shows dirty.
  **Fix:** have `ConfirmExtendedKey` / `DeleteExtendedKey` / `ConfirmResetExtendedKeys` take
  `_presetApplyGate`, or make the preset push wait for the in-flight write instead of returning.
  **Reviewer's refutation attempt:** `_extendedKeysWriteInFlight` does correctly prevent a double
  Confirm (it is set synchronously before the first await), but `_presetApplyGate` only serialises
  preset reverts and those three commands never take it, so the two operations are unordered.
  `PLAUSIBLE` because the window depends on how long the user takes to reach Presets and pick one.

- [x] **m3. One lost or rejected packet during a write leaves the repair-on-read erasing every
  advanced key, and the post-failure re-read saves the empty board into the user's rows** —
  `SmartInfiniteExtendedKeysService.cs:387-393`, `:405-426`, `:590-632`, with
  `DeviceDetailsViewModel.ExtendedKeys.cs:1037-1049` — Minor, `PLAUSIBLE`
  **Problem:** `WriteTable` returns true regardless of how many packets were acknowledged
  (`SmartInfiniteTransport.cs:236`, pre-existing). A NAK or a dropped area chunk still lets the matrix
  go out with its new pointers, verify then fails, and the result is `Rejected` — final, even though
  the board is known to be torn and rewriting it in the same call would heal it. The App re-reads, the
  repair wipes everything including DKS/END, and with `persist:true` the empty set is written into the
  user's live rows. The plan deliberately declined to change ack accounting or add a rollback
  (`plan:689-705`), but two cheap options remain: (a) on a verify mismatch, retry the write once in
  the same call against a fresh snapshot; (b) do not persist the result of a re-read that went
  through a repair — which needs a `repaired` flag in the response, since the App currently cannot
  tell.
  **Reviewer's refutation attempt:** read-back verify is authoritative, so a bad write cannot pass as
  success — true. But `WithRetry` does not retry a verify failure (`SmartInfiniteTransport.cs:302`),
  and the repair fires on any pointer/area mismatch, so nothing stands between one transient fault and
  total data loss. `PLAUSIBLE` because the real NAK rate on the cable cannot be judged from the code.
  **To confirm:** on hardware, pull the cable or force a NAK between `0x3B` and `0x18`; in tests, a
  scripted transport that drops one area chunk.

- [x] **m4. The self-heal does not re-run the key push after it writes** —
  `DeviceDetailsViewModel.cs:1385-1386` against `Presets.cs:1055-1059` — Minor, `CONFIRMED`
  **Problem:** the preset push re-runs `ApplyKeyOverridesToDeviceAsync` and
  `ResyncKeyMacroBindingsAsync` after removing records, because the freed slots went back to their
  factory triplets. The self-heal ignores the `true` it gets back — and the method's own doc comment
  says that return value exists for exactly this. Macro bindings are caught up by
  `RefreshFromDeviceAsync` at the end of the chain, but the owner's slot remaps on those slots are
  not: the board gives the factory key while storage holds a remap.
  **Fix:** one line — `if (await ApplyStoredExtendedKeysToDeviceAsync()) await ApplyKeyOverridesToDeviceAsync();`
  **Reviewer's refutation attempt:** checked whether `RefreshFromDeviceAsync` covers it
  (`KeyAssignments.cs:1214-1222`) — it only reads the matrix and resyncs macros, with no key push.

- [x] **m5. The self-heal closes an open editor on every page open, even when it writes nothing** —
  `DeviceDetailsViewModel.ExtendedKeys.cs:1126` — Minor, `CONFIRMED` (UX)
  **Problem:** `CancelExtendedKeyEditor()` is the method's first statement, before every guard, and
  the method now runs at the end of every open. The editor is usable as soon as the probe lands
  (`DeviceDetailsViewModel.cs:1349`), so anything the user opened before the chain finishes is closed
  with no write to show for it.
  **Fix:** move the cancel to just before `return await WriteExtendedKeysAsync(...)` (`:1198`).
  **Reviewer's refutation attempt:** the cancel is genuinely needed on the preset path ("an apply
  supersedes an edit") and belongs there; the issue is only that the self-heal writes nothing in most
  opens (`:1195`) yet cancels before reaching that check.

- [x] **m6. The pair decoder trusts the slots written inside the record rather than the slots that
  point at it** — `SmartInfiniteExtendedKeysService.cs:134-143`,
  `SmartInfiniteAdvancedKeyArea.cs:104-141` — Minor, `PLAUSIBLE`
  **Problem:** MT/TGL take their slot from `record.Slots` (the pointer side) while RS/SOCD take theirs
  from bytes 2 and 5 (inside the record). A torn-but-parsing board — delete A-D, add B-C, lose the
  `0x18` write, and the old pointers at offset 0 find a `0x84` there — reads as the pair B-C while the
  pointers sit on A and D. The post-failure re-read then shows the new pair as stored, and with
  `persist:true` it lands in the rows.
  **Fix:** `TryDecodeManaged` should require `{slot1, slot2}` decoded from the record to equal
  `record.Slots`, and treat a mismatch as incoherent — repair or blocked.
  **Reviewer's refutation attempt:** `ParseRecords` does check that the type under the pointer
  matches, that records do not overlap and that the bounds are sane (`:114-135`) — but nothing checks
  that the slots agree. Reachable only through a torn write, as in m3, hence `PLAUSIBLE`.

#### Tests

- [x] M2 needs a case: an unread board (a failed probe) plus a debounced push, asserting no key record
  is written onto a slot whose mirror carries a `0x8x` type. m2, m4 and m5 each need one, in
  `tests/ProoveGaming.App.Tests`. m1 needs intake cases for each newly refused output shape and a
  `DropRowsOffThisBoard` case for a blocked slot. m3 and m6 need a scripted transport that drops one
  area chunk and a torn-but-parsing matrix respectively, in `tests/ProoveGaming.Infrastructure.Tests`.

#### Verdict

Ship: no — two Majors, both confirmed, both silent data loss. M1 destroys the incoming account's
unsaved settings on a switch; M2 orphans extended-key records, including unmanaged vendor ones which
never come back, on any keyboard edit made before the board has been read. First: M2, then M1, then
m4 and m5, which are one-liners.

#### Fix pass — 2026-09-12 — device side (m6, m1-host, m3a, m3b-host), recorded by the orchestrator

The device-side coder was told not to write this file so that it and the App-side coder could not
overwrite each other's notes. It verified all four items against the code before fixing and reports
that all four hold. Build clean, Infrastructure 1095/1095, App 1767/1767 in Release — re-run and
confirmed by the orchestrator.

- [x] **m6** — fixed 2026-09-12. `TryDecodeManaged` now requires an RS / SOCD record's own
  `slot1`/`slot2` bytes to be exactly the pair of slots whose triplets point at it
  (`PointersNameTheSamePair`); a disagreement means the record is not one this build owns.
  **Decision: blocked, not repaired.** The record is preserved byte-for-byte, re-pointed on the
  rebuild, and its slots reported blocked — the same treatment the knowledge base already prescribes
  for "an MT / TGL record pointed at by more than one slot", which is the identical failure on the
  single-key side. The repair path was rejected because it is whole-board: its own doc gates it to an
  area that does not parse at all, where every record is already unreachable, whereas here the area
  parses and every other record — including the user's DKS / END, which this build cannot recreate and
  whose loss this same review grades a Major — is still reachable. Blocked is also the recoverable
  outcome: the record survives and the vendor app can remove it. Cost: the two slots stay blocked
  until someone does, exactly as DKS / END slots already are. Knowledge base updated.

- [x] **m1 (host half)** — fixed 2026-09-12, **not in the place the finding names**, and the
  orchestrator verified the divergence before accepting it. The refusal went into
  `SmartInfiniteExtendedKeysService.OutputKeyCode`, not into
  `SmartInfiniteAdvancedKeyArea.KeyCodeFromRecord:205-211`. An MT / TGL output carrying both a
  non-zero modifier mask and a non-zero usage is now a whole-entry refusal with the reason
  `the {role} output {x} carries both a modifier mask and a usage`, in the S3 shape.
  `KeyCodeFromRecord` is deliberately unchanged: its other two callers (`PairKeyCodes` at `:303-304`,
  and the plain-key gate at `PlanWrite:539`) read the **factory table**, where usage-wins truncation
  is the vendor's own documented rule
  (`docs/knowledge-base/smart-infinite-protocol/advanced-keys-rapid-snap.md:83-85`,
  `getKeyCodeAndType`). Refusing there would make the host reject RS / SOCD pairs on real factory keys
  the vendor accepts, for no gain — a pair entry's stored row carries only slots, so the
  never-converging self-heal m1 describes cannot arise on that path. It arises only on the output
  path, which is now closed. Knowledge base updated with the distinction. ~~The box stays unticked while the App half is open.~~ **Ticked
  2026-09-12: the App half landed in the App fix pass below, and both sides are pinned — including a
  reverse revert that applies the tidy-up this note warns against and shows it breaks pairs on real
  modifier keys.**

- [x] **m3(a)** — fixed 2026-09-12. Confirmed: `WriteTable` returns true regardless of acks
  (`SmartInfiniteTransport.cs:236`) and `WithRetry` does not re-run a non-null, so one verify mismatch
  was final. `WriteOnce` now runs up to `WriteAttempts` (2) inside the same handle: on a mismatch it
  re-reads the snapshot — which runs the area reset first if the board no longer parses — re-plans and
  rewrites, and only a second mismatch reports `Rejected`. A `PlanWrite` refusal and a transport
  `null` keep their old immediate verdicts; only a read-back mismatch retries. Cancellation is still
  honoured before the first packet and deliberately **not** before the second attempt, because leaving
  the board torn is the outcome this retry exists to prevent. The verify block was extracted to
  `VerifyWrite` so both attempts share it.
  **Test changed, not weakened:** `Write_FirmwareDiscardsTheWrite_IsRejectedAfterExactlyOneAttempt`
  asserted exactly one pair of transfers per write call, which is the bound m3(a) deliberately
  changes, and it failed (`Expected: 5, Actual: 10`) before the code was touched. It is retargeted as
  `..._IsRejectedAfterExactlyTwoAttemptsInOneSession`: same `Rejected` verdict, still one session (no
  retry storm across handles), exactly two pairs of transfers. The positive case — a board that fails
  only the *first* attempt is healed and returns `Verified` — is **not** covered, because `FakeBoard`
  has no "drop one chunk then behave" knob; that is the scripted transport the Tests block already
  asks for.

- [x] **m3(b), host half** — fixed 2026-09-12. `GetExtendedKeysResponse` gains **`bool repaired = 7`**
  (an additive contract change in `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto`; host
  and client both regenerate cleanly, and the generated `RepairedFieldNumber = 7` was checked). It is
  true when that read had to reset the board's advanced keys before it could answer, so the entries,
  blocked slots and free bytes in that same response are what survived the reset rather than a
  configuration anyone chose. Carried as `BoardSnapshot.Repaired` → `ExtendedKeysState.Repaired` → the
  response. `docs/CONTEXT.md` gains the term **Repaired read**. **The box stays unticked while the App
  consumer is open:** the App must display such a read but must never persist it as the owner's rows,
  which is the `persist:true` branch after a failed write.

#### Fix pass — 2026-09-12 — App side (M1, M2, m1-App, m2, m3b-App, m4, m5), recorded by the orchestrator

Ran after the device-side pass, so it could consume the new `repaired` field. Build clean, App
1767 → **1758/1758**, Infrastructure **1095/1095**, Release — re-run and confirmed by the
orchestrator. The App count falls because ten tests of deleted code went with it and the m5 split
added one; the orchestrator checked that no deleted name has a live reference left (the two hits that
remain are comments explaining the removal).

- [x] **M2** — Option A, in `DeviceDetailsViewModel.KeyAssignments.cs`. `ApplySlotKeyOverridesToDeviceAsync`
  and **both** loops of `ResyncKeyMacroBindingsAsync` (the binding loop as well as the stale loop — the
  existing `extendedSlots` skip is in both, and a macro write orphans a record exactly as a key write
  does) now also skip a slot whose board mirror `_assignments[slot]` carries a type byte in
  `0x80..0x85`, via `BoardMirrorHoldsExtendedKeyPointer` and a new `ExtendedKeyMode.IsPointerTypeByte`.
  The existing `extendedSlots` skip stays: the two answer different questions — what the owner's rows
  and the host's blocked list *claim*, versus what the board actually *holds* — and only the second
  survives a probe that never landed.
  **Test inverted, not weakened:** `SlotKeyPush_SkipsTheSlotsOfConfiguredRecords_ButRestoresThemWhenNoRecordClaimsThem`
  asserted that `0x84` pointers plus an empty probe means both keys are "restored" — which is the
  orphaning write itself. Renamed `SlotKeyPush_SkipsEverySlotHoldingARecordPointer_ButStillRestoresPlainSlots`
  and split three ways, the third case (a plain record nothing claims → still written) keeping the new
  skip from becoming a blanket "never touch a differing slot". The legitimate restore still happens
  through the write path: `WriteExtendedKeysAsync` nulls `_assignments`, so the next push re-reads the
  matrix and finds the factory triplet rather than a pointer.

- [x] **M1** — Option A. The owner-change sweep writes no pending-apply mark.
  `ShouldReapplyDeviceState`, `_lastAppliedOwner` and `MarkDeviceStatePendingApply` had no remaining
  caller and are deleted; `OwnerSweepState` drops to `(Restored, Lit)`. The sign-in pull is unaffected
  — it writes the mark inline in `SettingsSyncService.cs:862`, never through the helper — so the mark
  and its storage helpers stay live for the pull case, which is what the design says they are for.
  Docs reconciled: `docs/CONTEXT.md`'s **Owner-change sweep** and **Pending device apply** entries
  contradicted each other and the code, and now agree; criteria 1-5 of
  `../fix-account-switch-device-state/fix-account-switch-device-state-plan.md` are rewritten (1-4 met,
  by the self-heal rather than the mark; 5 marked not met and deliberately deferred), with dated notes
  in the api-sync and self-heal plans whose live prose M1 invalidated.
  **Accepted cost, stated plainly:** an account switch no longer pushes the incoming owner's key
  remaps, and no longer clears the departing owner's macro record from the board. That is the
  limitation `../fix-extended-keys-self-heal/fix-extended-keys-self-heal-plan.md:66-72` already names
  and defers. It is a retraction **within this branch**, not a regression against `main`: the
  switch-time keyboard re-apply was itself added by `14be823` on this branch, and it was implemented
  in the data-destroying way M1 describes. Extended keys are unaffected — the self-heal covers them,
  and both tests pinning the user's originally reported Toggle-between-accounts bug (in-process and
  across a restart) still pass. Two tests were **inverted rather than deleted**, so the deferred
  follow-up has two tests to turn back over:
  `KeyboardPage_OpenedAfterAnAccountSwitch_DoesNotYetCarryTheNewOwnersRemap` and
  `…_WritesTheNewOwnersMacroBinding_ButLeavesTheOthersOnTheBoard`.

- [x] **m1 (App half; closes m1 with the device-side half above)** — `IsKeyboardOutput` now requires
  type `0x10`, `Code1`/`Code2` within `0..255`, and exactly one of mask/usage non-zero — term for term
  with the host's `OutputKeyCode` as the device-side coder left it, composed with `KeyCodeFromRecord`
  and `DeviceService.TryFromProto`'s byte range. `DropRowsOffThisBoard` now also drops a row landing on
  `_extendedKeysBlockedSlots`; the row stays in storage, since the vendor app can free the slot again.

- [x] **m2** — `ConfirmExtendedKey`, `DeleteExtendedKey` and `ConfirmResetExtendedKeys` run under
  `_presetApplyGate` through a shared `UnderPresetApplyGateAsync` wrapper, rather than making the
  preset push wait. **Waiting fixes only half of it:** `LoadPresetSnapshotToDevice` overwrites the live
  rows long before the apply reaches its extended-keys push, so a write landing after that point would
  still persist the old preset's set over the new snapshot. Holding the gate puts the whole apply
  behind the write. The dispatcher is never blocked — `WaitAsync` is awaited, so the window keeps
  pumping; the only visible effect of contention is the command's own button staying disabled
  (`AsyncRelayCommand` refuses concurrent execution and Avalonia's `Button` honours `CanExecute`).
  `ConfirmResetExtendedKeys` closes its dialog and cancels the editor **before** the wait so the click
  stays instant, and the other two re-check their guards after it. Lock order unchanged (this gate,
  then `_applyGate`), and there is no re-entrancy: the preset push calls `WriteExtendedKeysAsync`
  directly, and nothing under `PersistExtendedKeysStateAsync` reaches `ApplyPresetToDeviceAsync`.

- [x] **m3(b) (App half; closes m3(b) with the host half above)** — `GetExtendedKeysResponse.repaired`
  is mapped into a new `ExtendedKeysInfo.Repaired` (an init property, so a DeviceHost predating the
  field and every existing construction read false) and latched per read. `PersistExtendedKeysStateAsync`
  refuses outright while it is set, which covers the `persist:true` branch after a failed write without
  special-casing it; the flag is cleared by any accepted write, whose set the App chose rather than
  found. The section still renders the repaired board — only the persist is refused, so the owner's
  rows keep their previous set and the next open pushes them back.

- [x] **m4** — the self-heal's `true` now re-runs `ApplyKeyOverridesToDeviceAsync`, the way the preset
  push does. It needed a `Quietly<T>(Task<T>, string)` overload so the chain can read the result while
  keeping the same swallow-and-name contract (a failure yields `default`, i.e. "nothing happened",
  the safe reading for a step that only gates work because it DID something). Re-fenced with `Stale()`
  between the two calls.

- [x] **m5** — `CancelExtendedKeyEditor()` moved from the top of `ApplyStoredExtendedKeysToDeviceAsync`
  to immediately before the write, so a heal that writes nothing stops closing the user's open editor.
  The preset path still cancels twice over: `ApplyPresetToDeviceCoreAsync` reloads the key layout first
  and `ResetKeyLayoutState` cancels there, and the write itself cancels.
  **Test inverted, not weakened:** `Editor_APresetApply_CancelsIt` drove the method on a matching board
  — a heal that writes nothing — so it encoded the bug. Split into
  `Editor_AnApplyThatWritesTheBoard_CancelsIt` (rows seeded so the apply really writes) and
  `Editor_AnApplyThatWritesNothing_LeavesItOpen`.

**Deleted with the code they tested** (ten cases in `HomeViewModelOwnerRestoreTests.cs`, all covering
`ShouldReapplyDeviceState` / the sweep's mark loop, both gone):
`DoesNotReapplyDeviceStateForTheFirstOwnerOfTheSession`, `ReappliesDeviceStateWhenOneAccountReplacesAnother`,
`DoesNotReapplyDeviceStateForAnUnchangedOwner`, `DoesNotReapplyDeviceStateOnSignOut`,
`TheDeviceStateDecisionIsReadOffItsOwnFieldNotTheLightingOne`, `MarksEveryConnectedTypeForTheIncomingOwner`,
`MarksNothingForAnOwnerWithNoAppliedPresetForThatDevice`, `MarksTheIncomingOwnerOnly`,
`Sweep_WhenOnlyTheLightingFieldSaysSwitch_WritesNoMark` and `Sweep_SkipsACardWhoseSettingsIdentityNeverResolved`.
`Sweep_WhenOnlyTheDeviceStateFieldSaysSwitch_WritesTheMark` became
`Sweep_OnAnAccountSwitch_WritesNoPendingApplyMark`, asserting absence with an applied preset seeded so
it cannot pass trivially.
