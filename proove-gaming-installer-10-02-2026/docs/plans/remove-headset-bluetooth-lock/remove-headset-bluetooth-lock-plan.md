# Plan: remove-headset-bluetooth-lock

## Goal

Headsets connected over Bluetooth are currently shown the "BLUETOOTH MODE" banner and have every
details-page section disabled (Presets, Audio, Firmware), exactly like keyboards and mice — the
uniform read-only Bluetooth mode decided on 2026-08-30
(`docs/plans/add-bluetooth-mode-banner/`). For the two headsets (Cosmo, Starlight) that lock is
wrong in practice: their audio settings target the **OS endpoint** (`AudioVolumeTarget:
OperatingSystem`), which works identically over Bluetooth, so the app blocks controls that would
function. Per the new ticket ("прибрати блокування", 2026-09-02) the lock must be removed for
both headsets, while **keyboards and mice keep the existing Bluetooth blocking unchanged**
(explicit user constraint).

## Scope

**In:**
- Both headsets (Cosmo, Starlight) connected over Bluetooth: no banner, all their sections
  (Presets, Audio, Firmware) fully interactive, preset save/apply allowed.
- The catalog change needed for Starlight's Bluetooth row to permit configuration writes
  (`SupportsConfiguration`), and an explicit `SupportsConfiguration: false` on T-Racer's
  Bluetooth row so the keyboard's lock survives the new condition.
- Switching the lock condition from `HasBluetooth` to a catalog-driven computed property.
- Updating the plans/glossary that pin the superseded behavior (dated notes, live sections).

**Out:**
- Keyboards and mice: Rampage, T-Racer, TAIPAN over Bluetooth stay banner-visible and locked;
  their UX must be pixel-identical to today.
- TAIPAN-on-dongle inconsistency (Round 1 S4 of the banner plan) — still a separate product
  decision; this change does not add a non-Bluetooth "read-only link" banner.
- Any real firmware-update capability work (the Firmware section's button is a placeholder
  either way).
- Localization strings — the banner keys are unchanged (still used by keyboards/mice).
- Home-screen cards, DeviceHost gRPC contract, proto — untouched.

## Options considered

1. **Device-type check in the UI** — lock = `HasBluetooth && DeviceTypeLabel` not containing
   `"headset"` (precedent: `DeviceCardViewModel.SupportsDpiStageSwitch` keys off `"mouse"`).
   - + Keyboards/mice untouched by construction; smallest catalog diff (only Starlight).
   - − Hardcodes a device-type rule into the view model while the catalog already models exactly
     this fact per transport (`SupportsConfiguration`); two sources of truth. A future headset
     whose BT link genuinely can't carry configuration could not be locked without new code.
2. **Catalog-driven lock** *(recommended)* — resurrect the interim
   `IsBluetoothConfigLocked = HasBluetooth && !SupportsConfiguration` property that the banner
   plan built and then removed on 2026-08-30 (see its "Options considered"), and align the
   catalog: remove `SupportsConfiguration: false` from Starlight's Bluetooth row, add it to
   T-Racer's Bluetooth row.
   - + Single source of truth: the same flag that already gates the write side
     (`IsDeviceConfigurable`) now gates the read-only UI, so banner and behavior can't disagree.
     Cosmo needs zero catalog change (its BT row is already configurable). Future devices are
     data-driven.
   - + Aligns the catalog with reality for T-Racer: its BT UI has been locked since 2026-08-30,
     but its catalog row claimed configurable — dead configuration either way.
   - − Touches every section view's `IsEnabled` binding (mechanical, one-line each).
   - − Changes `IsDeviceConfigurable` for T-Racer over BT from true to false — no visible
     change (its UI was already disabled), but the write guards become strict there.
3. **Keep `HasBluetooth` bindings, exempt headsets per view** — sprinkle a second condition into
   each of the ~11 bindings via MultiBinding.
   - − N copies of a compound condition in XAML; the exact per-view duplication the repo's
     style rules exist to prevent. Rejected.

## Recommended approach

Option 2. The lock condition becomes a single computed property on `DeviceCardViewModel`:

```csharp
// True while the live transport is Bluetooth AND that transport cannot carry configuration
// (catalog SupportsConfiguration=false) — drives the BLUETOOTH MODE banner and section locking.
// Headsets (Cosmo, Starlight) are configurable over Bluetooth (OS-endpoint audio), so they
// never lock; keyboards/mice keep their catalog false and lock exactly as before (ticket
// 2026-09-02, superseding the uniform lock of 2026-08-30).
public bool IsBluetoothConfigLocked => HasBluetooth && !SupportsConfiguration;
```

Supporting changes:

- `SupportsConfiguration` (`DeviceCardViewModel.cs:159`) is today a plain non-notifying
  property. Convert it to a notifying one (manual backing field + `SetProperty`, matching the
  file's existing style for non-generated properties) that also raises
  `OnPropertyChanged(nameof(IsBluetoothConfigLocked))`; keep its `= true` default. Add
  `[NotifyPropertyChangedFor(nameof(IsBluetoothConfigLocked))]` to the `HasBluetooth`
  observable property. Both card-stamping paths (`ApplyEntryToCard`,
  `FillBluetoothCard`) already set `SupportsConfiguration` *before* the transport flags, so the
  lock computes from consistent state at each notification.
- `BluetoothModeBannerView.axaml:9` — the one visibility gate for all four call sites:
  `IsVisible="{Binding Device.IsBluetoothConfigLocked, FallbackValue=False}"` (FallbackValue
  stays mandatory: `Device` is null during navigation). Update the view's explanatory comment.
- Section `IsEnabled` bindings switch from `!Device.HasBluetooth` to
  `!Device.IsBluetoothConfigLocked` (FallbackValue=True) in: `PresetsSectionView`,
  `RgbLightingSectionView`, `ButtonsSectionView`, `MacrosSectionView`, `DpiSectionView`,
  `SettingsSectionView`, `AudioSectionView`, `ActuationSectionView`, `FirmwareSectionView`, and
  the header preset controls in `DeviceDetailsView.axaml` (~line 113). The `bluetoothLocked`
  dim class placement is untouched.
- **Deliberately kept on `HasBluetooth`:** the key-assignment read-failed suppression
  MultiBinding in `DeviceDetailsView.axaml` (~line 578). That gate is about the *transport* (a
  matrix read always fails over BT), not about configurability — and it only affects keyboards,
  whose two conditions are equivalent anyway.
- Catalog (`src/ProoveGaming.DeviceHost/appsettings.json`):
  - Starlight → Bluetooth transport: **remove** `"SupportsConfiguration": false`. This also
    flips `IsDeviceConfigurable`, so Save Preset / preset apply work for Starlight over BT
    (presets for headsets hold audio levels applied to the OS endpoint — they work on this
    link), and reapply-preset-on-connect no longer skips it.
  - T-Racer → Bluetooth transport: **add** `"SupportsConfiguration": false`. Required by the
    user constraint — without it the new condition would unlock the T-Racer keyboard over BT,
    because its BT row currently inherits the device default (true).
  - Cosmo, TAIPAN, Rampage: no change (Cosmo BT already configurable; TAIPAN/Rampage BT rows
    already declare false).

Data flow (unchanged plumbing, for the record): catalog `SupportsConfiguration` →
`DeviceMapExpander` → gRPC `ConfigurationUnsupported` → `card.SupportsConfiguration` →
`IsDeviceConfigurable` (write guards) and now also → `IsBluetoothConfigLocked` (UI lock).

## File-level change list

Ordered so characterization/regression tests land before the behavior flip.

| # | File | Action | Intent |
| --- | --- | --- | --- |
| 1 | `tests/ProoveGaming.Infrastructure.Tests/StarlightCatalogTests.cs` | edit | Add a catalog characterization test first: every **non-headset** Bluetooth row (`DeviceType` not containing "Headset") declares `SupportsConfiguration: false` — pins the "keyboards and mice stay locked" constraint. It fails until #2 adds T-Racer's flag, then guards it forever. |
| 2 | `src/ProoveGaming.DeviceHost/appsettings.json` | edit | T-Racer Bluetooth transport: add `"SupportsConfiguration": false` (keeps the keyboard locked under the new condition; matches its shipped read-only BT UI). |
| 3 | `src/ProoveGaming.DeviceHost/appsettings.json` | edit | Starlight Bluetooth transport: remove `"SupportsConfiguration": false` (headset is configurable over BT — audio targets the OS endpoint). Same file as #2, one commit. |
| 4 | `tests/ProoveGaming.Infrastructure.Tests/StarlightCatalogTests.cs` | edit | Update `StarlightExpandsToABluetoothRowWithBatteryAndNoConfiguration` → the row now carries configuration; rename and rewrite the PG-608 rationale comment with a pointer to this plan (the vendor-HID channel is still absent over BT, but headset configuration doesn't need it). |
| 4b | `tests/ProoveGaming.Infrastructure.Tests/HidDeviceMapRegistryBluetoothTests.cs` | edit | Flip `StarlightOverBluetoothGetsItsOwnBluetoothCapabilities`'s `ConfigurationUnsupported` assertion (true → false) and rewrite its comment — the class reads the real catalog, so #3 necessarily flips it; the battery override remains what distinguishes the BT row from the dongle row. *(Row added 2026-09-02 during implementation — the plan originally listed this class as unchanged; see Review findings S1.)* |
| 4c | `tests/ProoveGaming.Infrastructure.Tests/StarlightCatalogTests.cs` | edit | `EveryHeadsetBluetoothRowCarriesConfiguration` — mirror guard for the ticket's POSITIVE half: every headset Bluetooth row stays configurable after expansion, non-vacuous (asserts >= 2 headset BT rows). *(Row added 2026-09-02 by /test-writer; recorded per Review findings S1.)* |
| 5 | `src/ProoveGaming.App/ViewModels/DeviceCardViewModel.cs` | edit | Convert `SupportsConfiguration` to a notifying property; add computed `IsBluetoothConfigLocked`; notify it from both `SupportsConfiguration` and `HasBluetooth`. |
| 6 | `tests/ProoveGaming.App.Tests/DeviceCardBluetoothConfigLockTests.cs` | create | Resurrected lock-property tests: locked on BT+unconfigurable; unlocked on BT+configurable (headset case); unlocked on dongle+unconfigurable (TAIPAN-on-dongle case); `PropertyChanged` raised for `IsBluetoothConfigLocked` when either input flips. |
| 7 | `src/ProoveGaming.App/Views/DeviceDetails/Sections/BluetoothModeBannerView.axaml` | edit | Visibility gate → `Device.IsBluetoothConfigLocked` (FallbackValue=False); refresh the comment (banner = "BT link that cannot carry configuration", not "any BT link"). |
| 8 | `src/ProoveGaming.App/Views/DeviceDetails/Sections/{Presets,RgbLighting,Buttons,Macros,Dpi,Settings,Audio,Actuation,Firmware}SectionView.axaml` | edit | `IsEnabled="{Binding !Device.HasBluetooth…}"` → `IsEnabled="{Binding !Device.IsBluetoothConfigLocked, FallbackValue=True}"` on each section's content root. |
| 9 | `src/ProoveGaming.App/Views/DeviceDetails/DeviceDetailsView.axaml` | edit | Header preset controls (~line 113): same binding swap. The read-failed suppression MultiBinding (~line 578) stays on `HasBluetooth` — add a one-line comment saying why. |
| 10 | `docs/CONTEXT.md` | edit | Update the **Bluetooth mode banner** entry: condition is now `IsBluetoothConfigLocked` (catalog-driven); headsets exempt per ticket 2026-09-02. |
| 11 | `docs/plans/add-bluetooth-mode-banner/add-bluetooth-mode-banner-plan.md` | edit | Dated note: the 2026-08-30 "uniform lock" product decision is superseded for headsets (2026-09-02, this plan); correct the live Scope/visibility-condition text and the affected acceptance criteria (leave `## Review findings` untouched). |
| 12 | `docs/plans/add-starlight-bluetooth/add-starlight-bluetooth-plan.md` | edit | Dated note where `SupportsConfiguration: false` was specified (~lines 127, 150, 214, 274): removed by this plan; presets/audio work over BT via the OS endpoint. Live sections only. |

## Acceptance criteria

- [ ] Given a Cosmo connected over Bluetooth only, when its details page is opened, then no
  "BLUETOOTH MODE" banner is shown on any section, and the Audio sliders (output volume,
  microphone volume) are interactive and change the Windows endpoint levels.
- [ ] Given a Starlight connected over Bluetooth only, when its details page is opened, then no
  banner is shown, Audio sliders work, and Save Preset enables after a slider change; saving and
  re-applying a preset restores the levels.
- [ ] Given a Cosmo or Starlight over Bluetooth, when the Presets and Firmware sections are
  opened, then their content is full-opacity and interactive (no `bluetoothLocked` dimming), and
  the header's Current Preset dropdown / SAVE PRESET / NEW PRESET are enabled.
- [ ] Given a Rampage keyboard connected over Bluetooth, when its details page is opened, then
  the banner is visible and every section plus the header preset controls are dimmed and
  non-interactive — identical to the behavior before this change.
- [ ] Given a T-Racer keyboard over Bluetooth or a TAIPAN mouse over Bluetooth, when their
  details pages are opened, then the banner is visible and the sections are locked (T-Racer's
  new catalog `SupportsConfiguration: false` keeps it locked under the new condition).
- [ ] Given a TAIPAN on its 2.4G dongle (unconfigurable but not Bluetooth), when its details
  page is opened, then no banner is shown — unchanged from today.
- [ ] Given a headset's details page open over Bluetooth, when the headset switches to its
  dongle (or vice versa), then the page stays banner-free in both states and the audio sliders
  keep working against the live endpoint.
- [ ] Given a keyboard's details page open over Bluetooth, when a USB cable is plugged in, then
  the banner clears in place (the 2026-08-31 transport-recovery behavior is unregressed).
- [ ] `dotnet test ./src/ProveGaming.slnx -c Release` passes: the updated
  `StarlightCatalogTests`, the new non-headset catalog guard, the resurrected
  `DeviceCardBluetoothConfigLockTests`, and the existing
  `DeviceDetailsRampageKeyAssignmentTests` / `BluetoothModeBannerResourcesTests` /
  `LanguageResourceParityTests` are all green.

## Test strategy

Change to existing behavior — the touched feature is already covered
(`StarlightCatalogTests`, `BluetoothModeBannerResourcesTests`,
`DeviceDetailsRampageKeyAssignmentTests`), so those stay green and are extended; the one
criterion that flips (Starlight BT configurability) is updated in place. Order: the new
characterization test for the constraint lands before the catalog flip (see change list #1–#4).

`tests/ProoveGaming.Infrastructure.Tests` (reads the real DeviceHost catalog):

- **New guard (non-negotiable):** every Bluetooth row whose `DeviceType` does not contain
  `"Headset"` declares `SupportsConfiguration = false` after expansion. This is the executable
  form of "keyboards and mice must not change" and catches any future BT transport added
  without the flag.
- **New mirror guard (added by /test-writer, 2026-09-02):**
  `EveryHeadsetBluetoothRowCarriesConfiguration` — the positive half of the same contract: every
  headset Bluetooth row keeps `SupportsConfiguration = true` after expansion, with a
  non-vacuity assertion (>= 2 headset BT rows) so a shrunken catalog cannot silently pass. Catches
  a later catalog edit re-locking a headset over Bluetooth, which only manual QA would notice.
- **Updated:** `StarlightExpandsToABluetoothRowWithBatteryAndNoConfiguration` → asserts
  `SupportsConfiguration` is now **true** on the Starlight BT row (renamed accordingly);
  battery/name/id assertions unchanged.
- **Updated (discovered during implementation — change list #4b):**
  `HidDeviceMapRegistryBluetoothTests.StarlightOverBluetoothGetsItsOwnBluetoothCapabilities` →
  its `ConfigurationUnsupported` assertion flips true → false with the catalog; the class reads
  the real DeviceHost catalog, so it could not stay unchanged.
- **Unchanged:** the rest of `StarlightCatalogTests`, `DeviceMapExpanderTests`,
  the rest of `HidDeviceMapRegistryBluetoothTests`, `BluetoothCatalogNameMatchTests`.

`tests/ProoveGaming.App.Tests`:

- **New: `DeviceCardBluetoothConfigLockTests`** — the four cases in change-list #6, including
  the INPC assertions (the XAML bindings are only as live as the notifications).
- **Unchanged but load-bearing:** `DeviceDetailsRampageKeyAssignmentTests` (BT→USB recovery —
  Rampage's catalog rows are untouched, so all 18 must stay green),
  `BluetoothModeBannerResourcesTests`, `LanguageResourceParityTests` (no resource edits),
  `DeviceDetailsPresetsViewModelTests` / `DeviceDetailsPresetApplyTests`
  (`IsDeviceConfigurable` semantics — defaults unchanged).

Deliberately **not** covered by automated tests: the XAML show/hide and section-dimming
bindings themselves (no Avalonia headless harness in the repo — same declared gap as the
original banner plan) and real Bluetooth audio behavior on hardware; both go to manual QA.
CI runs tests in **Release** — verify with a Release slnx test run before pushing.

## QA (manual test steps)

1. Pair the **Cosmo** headset with Windows over Bluetooth (no dongle plugged in) and launch the
   Proove Gaming app. Open the Cosmo card's details page.
   **Expected:** no purple "BLUETOOTH MODE" banner anywhere; the Bluetooth icon still shows in
   the connectivity bar at the bottom.
2. On the Audio section, move the output volume and microphone volume sliders.
   **Expected:** sliders move freely (not greyed out) and the Windows volume for the headset
   actually changes (check the Windows volume mixer / play audio).
3. On the Presets section, change a slider, then save a new preset and switch between presets.
   **Expected:** SAVE PRESET and NEW PRESET are enabled; switching presets restores the saved
   audio levels.
4. Open the Firmware section.
   **Expected:** content is full-brightness and the "Check for updates" button is clickable.
5. Repeat steps 1–3 with the **Starlight** headset over Bluetooth.
   **Expected:** same — no banner, working sliders, working presets.
6. While a headset's details page is open on Bluetooth, plug in its 2.4G dongle (headset
   switches transport), then unplug it again.
   **Expected:** no banner appears in either state; the sliders keep controlling the live
   connection.
7. Connect the **Rampage** keyboard over Bluetooth and open its details page; walk every
   left-nav section.
   **Expected:** the "BLUETOOTH MODE" banner shows above the keyboard image and every section
   is dimmed and non-interactive — exactly as before this change.
8. Repeat step 7 with the **T-Racer** keyboard and the **TAIPAN** mouse over Bluetooth.
   **Expected:** banner visible, sections locked, for both.
9. Connect the TAIPAN via its 2.4G dongle only and open its details page.
   **Expected:** no banner (unchanged behavior).
10. Switch the app language to Ukrainian and repeat steps 1 and 7.
    **Expected:** headset pages show no banner; the keyboard's banner shows "РЕЖИМ BLUETOOTH"
    with the Ukrainian message, unclipped.

## Review focus

- `null-safety` — `Device` is null during navigation; every rebound `IsVisible`/`IsEnabled`
  must keep its `FallbackValue` (`False` for the banner, `True` for sections).
- `UI-thread affinity` — `IsBluetoothConfigLocked` notifications fire from the gRPC stream
  thread via the card re-stamps; they must remain plain INPC raises (marshalled by Avalonia's
  binding layer), no dispatcher calls added in the card.
- `localization` — no new keys expected; confirm no string was hardcoded instead.
- `gRPC contract` — must stay untouched (`ConfigurationUnsupported` already carries the flag).

## Performance & quality

No hot paths. `IsBluetoothConfigLocked` is a two-flag boolean computed on read; the extra
`PropertyChanged` raises fire only on transport/capability re-stamps (device connect/switch
events), not per frame. No allocations, no async, no new subscriptions, no disposal concerns.
The singleton discovery services are untouched.

## Risks

- **Firmware over Bluetooth.** The section unlocks for headsets, but "Check for updates" is a
  placeholder (`FirmwareVersionText` renders a hardcoded v1.2.3). If a real update flow ships
  later and needs the dongle link, the Firmware section may need its own transport gate then —
  out of scope now, but the unlock makes that future gap possible. Flagged in the banner plan's
  Risks too.
- **Reapply-preset-on-connect now fires for Starlight over BT** (it early-returns on
  `SupportsConfiguration == false` today). For headsets a preset is OS-endpoint audio levels,
  so the reapply is desired — but watch `add-reapply-preset-on-connect` tests and behavior for
  surprises on BT connect events.
- **Notification ordering.** The lock reads two properties stamped in sequence; both stamping
  paths set `SupportsConfiguration` before the transport flags, so intermediate states resolve
  toward the correct final value within one event batch. A future re-stamp path that inverts
  the order could briefly flash the banner — the INPC tests pin the property mechanics, and the
  stamping order is documented at the property.
- **The 2026-08-30 product decision is being reversed for headsets.** The plan trail must not
  end up self-contradictory — hence the dated notes in the banner and Starlight-Bluetooth plans
  (change list #10–#12). Reviewers should check those land in the same change.
- **T-Racer BT write guards tighten** (`IsDeviceConfigurable` false where it was true). Its UI
  was already fully locked, so nothing user-visible changes — but any code path that wrote to a
  T-Racer over BT without going through the UI would now be skipped. None is known.

## Open questions

1. ~~Should the Firmware section stay locked for headsets over Bluetooth?~~ **Resolved
   2026-09-02 (user decision): unlock everything** — all headset sections including Firmware,
   per the ticket wording. Revisit only if a real firmware-update flow ships that needs the
   dongle link.
2. ~~Is adding `SupportsConfiguration: false` to T-Racer's Bluetooth row acceptable?~~
   **Resolved 2026-09-02 (user decision): yes** — the explicit flag keeps the keyboard locked
   under the new condition, pinned by the new non-headset catalog test.

None remain — the keyboard/mouse constraint is final and encoded as a test.

## Review findings

### Round 1 — Ship: yes

#### Summary

The branch removes the Bluetooth read-only lock for headsets by resurrecting the catalog-driven
`IsBluetoothConfigLocked = HasBluetooth && !SupportsConfiguration` on `DeviceCardViewModel`,
rebinding the banner and all nine section `IsEnabled` gates (plus the header preset controls) to
it, flipping the catalog (Starlight BT row loses `SupportsConfiguration: false`, T-Racer BT row
gains it), adding characterization tests on both sides, and reconciling the three affected docs
(`docs/CONTEXT.md`, banner plan, Starlight-Bluetooth plan) with dated notes. All changes are
working-tree only; orchestrator-verified Release build 0 errors and 2467/2467 tests green.

#### Plan conformance

Plan: `docs/plans/remove-headset-bluetooth-lock/remove-headset-bluetooth-lock-plan.md`
- Implemented as planned: change-list items #1–#12 all land — notifying `SupportsConfiguration`
  + computed lock (`DeviceCardViewModel.cs:155–182`), banner gate + refreshed comment
  (`BluetoothModeBannerView.axaml:9`), all nine section views rebound with `FallbackValue=True`,
  header preset controls (`DeviceDetailsView.axaml:113`), the deliberate `HasBluetooth` keep on
  the read-failed MultiBinding with its why-comment (`DeviceDetailsView.axaml:578–580`), both
  catalog edits (verified against the real rows: T-Racer BT `ProductId 12370` at
  `appsettings.json:325–330`; Starlight BT row), the non-headset guard test, the renamed
  Starlight row test, the resurrected `DeviceCardBluetoothConfigLockTests` (all four planned
  cases incl. INPC), and the three doc reconciliations. Both edited plans' `## Review findings`
  journals are untouched (banner plan journal at line 327, last edit ends ~322; Starlight plan
  journal at 236, last edit at ~218).
- Deviates from plan (mandatory, already acknowledged by the orchestrator):
  `tests/ProoveGaming.Infrastructure.Tests/HidDeviceMapRegistryBluetoothTests.cs:70` — the plan's
  Test strategy lists this class under **Unchanged**, but it reads the real catalog, so the
  Starlight flip forced `Assert.True(entry.ConfigurationUnsupported)` →
  `Assert.False(...)` plus a comment rewrite. Correct change, stale plan — see S1.
- Deviates from plan (beneficial addition): `StarlightCatalogTests.cs:167`
  `EveryHeadsetBluetoothRowCarriesConfiguration` — the positive-half mirror guard (headset BT rows
  must stay configurable, non-vacuous with a `>= 2` row count) was not in the change list. It
  strengthens the contract; fold into the S1 plan reconciliation rather than removing.
- Accepted limitation (per plan): Firmware section unlocks for headsets over BT while "Check for
  updates" is a placeholder — residual risk: if a real update flow ever needs the dongle link,
  Firmware needs its own transport gate then (resolved Open question 1, 2026-09-02).
- Accepted limitation (per plan): reapply-preset-on-connect now fires for Starlight over BT
  (desired — OS-endpoint audio levels); T-Racer BT write guards tighten
  (`IsDeviceConfigurable` false where its UI was already locked). Both green in tests.

#### Code

- [x] **S1. Plan still describes `HidDeviceMapRegistryBluetoothTests` as unchanged** —
  `docs/plans/remove-headset-bluetooth-lock/remove-headset-bluetooth-lock-plan.md:181` — Should-fix
  **Problem / why it matters:** Test strategy's "Unchanged" bullet lists
  `HidDeviceMapRegistryBluetoothTests`, and the File-level change list has no row for it — but
  the branch necessarily edits it (`StarlightOverBluetoothGetsItsOwnBluetoothCapabilities`
  asserts the real catalog's `ConfigurationUnsupported`, which #3 flips). Per the repo's
  keep-plans-true rule, the next review round would chase a wrong "unchanged" contract.
  While there, record the added `EveryHeadsetBluetoothRowCarriesConfiguration` mirror guard
  (change-list row or a line under Test strategy's "New guard").
  **Fix:** doc-only — add a change-list row (e.g. #4b:
  `tests/ProoveGaming.Infrastructure.Tests/HidDeviceMapRegistryBluetoothTests.cs` | edit |
  flip the `ConfigurationUnsupported` assertion for Starlight's BT row) and move the class out
  of the "Unchanged" bullet; mention the mirror guard.

No Blockers found. Review-focus verification, for the record:
- *null-safety*: every rebound binding keeps its `FallbackValue` — `False` on the banner
  (`BluetoothModeBannerView.axaml:9`), `True` on all nine sections and the header controls;
  the kept read-failed `HasBluetooth` binding retains `FallbackValue="True"`.
- *UI-thread affinity*: the new setter is a plain `SetProperty` + `OnPropertyChanged` chain —
  no dispatcher calls added; Avalonia's binding layer marshals as before. Verified the comment's
  ordering claim in both stamping paths: `ApplyEntryToCard` sets `SupportsConfiguration`
  (`HomeViewModel.cs:2216`) before `HasBluetooth` (:2227); `FillBluetoothCard` at :2311 before
  :2315. Traced both switch directions — each intermediate notification already resolves toward
  the correct final lock value, so no banner flash.
- *localization*: no new user-facing strings; only XML/C# comments added. Resource files
  untouched; `LanguageResourceParityTests` green.
- *gRPC contract*: no change under `ProoveGaming.GrpcDependencies`; the existing
  `ConfigurationUnsupported` field carries the flag as before.
- Remaining `HasBluetooth` bindings in views are display-only (connectivity icons in
  `ConnectivityBarView`/`SingleCard`/`DoubleCard`) plus the deliberate read-failed gate — no
  stray lock binding left behind.

#### Tests

- [ ] none — coverage matches the plan: catalog guards both directions
  (`EveryNonHeadsetBluetoothRowDeclinesConfiguration` is conservative — a BT row with a null
  `DeviceType` counts as non-headset and must decline configuration), the four
  `DeviceCardBluetoothConfigLockTests` cases including INPC raises, and the flipped
  characterization tests. XAML show/hide bindings remain manual-QA-only, as the plan declares
  (no Avalonia headless harness). Release run verified by orchestrator: 2467/2467.

#### Nits

- none

#### What's done well

- The stamping-order invariant is documented at the property it protects
  (`DeviceCardViewModel.cs:160–164`) and pinned by the INPC test — exactly the mechanism the
  plan's "Notification ordering" risk asked for.
- The two-sided catalog guard (non-headsets must decline, headsets must carry) turns the
  ticket's constraint into executable form in both directions, with a non-vacuity assertion so
  a shrunken catalog can't silently pass.
- The deliberate-`HasBluetooth` comment inside the read-failed MultiBinding
  (`DeviceDetailsView.axaml:578`) is the kind of one-liner that prevents the next person from
  "fixing" it into a regression.
- Doc reconciliation is thorough and honest: dated supersession notes in the banner and
  Starlight plans touch only live sections; both review journals left intact.

#### Verdict

Ship: yes — no blockers; the single Should-fix is a documentation reconciliation inside this
plan itself, safe as an immediate follow-up. First: S1 (add the
`HidDeviceMapRegistryBluetoothTests` row to the File-level change list, fix the Test strategy
"Unchanged" bullet, record the mirror guard).
