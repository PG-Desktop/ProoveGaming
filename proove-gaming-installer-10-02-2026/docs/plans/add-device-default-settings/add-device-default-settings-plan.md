# Plan: Per-device-type default settings, applied on add-device and restorable

## Goal

Every supported device should have a documented set of factory-sane defaults — buttons doing their
factory function, a sensible DPI (1200), report rate (1000 Hz), debounce, lift-off and a defined
lighting look — that are applied when the device is first added, and that the two existing "reset"
affordances restore. Today these values are scattered: button defaults live in
`DeviceDetailsViewModel.Buttons.cs`, RGB defaults in `ApplyRgbDefaults()`, debounce/lift-off in
`MouseSettingViewModel.Create`, the report rate in a `const`, and the DPI default does not exist at
all. Nothing is written down, and adding a device means editing App code. This plan moves the
defaults into the per-device catalog in DeviceHost `appsettings.json`, makes both reset buttons read
from that one source, seeds them on add-device, and publishes the resulting table as documentation.

## Scope

**In:**
- A `Defaults` block per entry in `AllowedDevices` (DeviceHost `appsettings.json`), carried to the
  App through the existing catalog path (`DeviceMap` → `DeviceCatalogEntry` proto → `HomeViewModel`
  → `DeviceCardViewModel`).
- Concrete seeded values for all eight catalogued devices (BLAZE, PG TAIPAN, Rampage, Raster,
  T-Racer, Aress, T800, Cyrex), including honestly leaving fields unset where a device has no such
  concept.
- Applying the defaults when a device is added (first connect, no preset ever applied).
- Rewiring the two reset affordances to that same source:
  - DPI **Recommendations** → DPI table + active stage.
  - Settings **Restore to default** → buttons + debounce + lift-off.
- Replacing the `IsKeyboardDevice` device-type check inside `ApplyRgbDefaults()` with catalog data,
  consistent with the capability-not-type rule established for `PersistDisplayedDpiAndRate`.
- A published reference table: `docs/device-defaults.md`.
- `docs/CONTEXT.md` glossary entry for the new term.

**Out:**
- The app-scoped preferences block (`ScrollLines`, `DoubleClickSpeed`, `SleepTimer`, generic
  `LiftOffDistance`) — OS-level behaviour, not per-device, not part of a preset. `ResetSettings()`
  keeps resetting those to their existing code constants; only the device-scoped half changes.
- Slider **ranges** (`Min`/`Max` in `MouseSettingViewModel.Create`) — they are firmware limits, not
  user-tunable defaults. Only the default **value** moves to config. Stated explicitly because the
  two live in the same factory method and it would be easy to move both.
- Macro defaults, key-remap defaults, and per-DPI-stage indicator colours (TAIPAN's are
  firmware-fixed and vendor-confirmed unsettable; BLAZE's are display-only).
- Any change to how a preset is applied to hardware. `ApplyCurrentStateToDeviceAsync` stays the
  single coordinated write.

## Current state (verified — do not re-derive)

| Concern | Where it lives today | Problem |
|---|---|---|
| Button defaults | `DeviceDetailsViewModel.Buttons.cs` → `DefaultButtonAssignments()` | Works, but code-defined and mouse-shaped; not documented |
| RGB defaults | `DeviceDetailsViewModel.Rgb.cs` → `ApplyRgbDefaults()`: `#7620EE`, brightness 60, speed 50, `IsRainbow=false`, effect = `Static` for keyboards else `Breathing` | Hardcoded, and selects the effect via an `IsKeyboardDevice` **device-type** check |
| Debounce / lift-off | `MouseSettingViewModel.Create`: `ButtonDebounce` 2–50 ms default **8**; `LiftOffDistance` 2–3 mm default **2** | Default and range fused in one place; not per-device |
| Report rate | `DeviceDetailsViewModel.Settings.cs` → `DefaultPollingRateHz = 1000` | Global const, not per-device |
| DPI default | *nothing* | `ApplyDpiRecommendations()` only re-persists the current `DpiPresets` and marks dirty — it never restores recommended values |
| DPI ladder | `appsettings.json` → `DpiStages` (BLAZE & TAIPAN both `[800,1200,2400,3600,4800,6000,12000]`) | Already per-device — the pattern to follow |

The catalog path is already proven end to end by `Macros` → `macro_max_delay_ms` /
`macro_max_repetitions`: a nested config object flattened into scalar proto fields with `0` meaning
unset. This plan copies that precedent exactly.

## Options considered

**Where the defaults live**

- **A. Per-device `Defaults` block in `appsettings.json`, flattened into the catalog proto
  (chosen — and pre-decided by the user).** Device knowledge stays in the one file that already
  holds `DpiStages`, `SupportedMouseSettings` and `Hotspots`; onboarding a new device is
  config-only. Costs a proto change and DeviceHost + App plumbing.
- **B. App-side static table keyed by capability.** No contract change, lands faster. Rejected:
  splits device knowledge across DeviceHost config and App code, and every new device would need an
  App rebuild — the opposite of the stated goal.

**How the default DPI is expressed**

- **A. A DPI *value* (1200), resolved to the nearest stage in that device's own ladder (chosen).**
  Survives a device whose ladder is not the shared `[800,1200,…]` set, and reads the way a human
  would state it. Resolution rule: exact match wins; otherwise the closest value; ties → lower.
- **B. A stage *index* (1).** Simpler, but silently means a different DPI on any device with a
  different ladder, and a config author has to count positions.

**Where the defaults get seeded on add-device**

- **A. Seed the default preset's snapshot when that preset is created (chosen).** The default
  preset is created by `CreateDefaultPresetIfNotExists` on first open; seeding its snapshot there
  means `EnsureDefaultPresetAppliedAsync` then applies a preset that genuinely contains the
  defaults, and **every existing mechanism just works** — revert restores them, `PresetMatchesLiveState`
  compares against them, `restoreUnsetToDefaults` has real values to fall back to.
- **B. Push defaults straight to hardware in `EnsureDefaultPresetAppliedAsync`, leaving the preset
  snapshot empty.** Rejected: recreates exactly the bug fixed in
  [`live-apply-preset-dirty-diff`](../live-apply-preset-dirty-diff/live-apply-preset-dirty-diff-plan.md)
  (M1/round 2) where a preset that holds no stage/rate has nothing to revert to.

## Recommended approach

Option A throughout. Concretely:

1. **Config schema** — new optional `Defaults` object per `AllowedDevices` entry:
   ```json
   "Defaults": {
     "Dpi": 1200,
     "ReportRateHz": 1000,
     "ButtonDebounceMs": 8,
     "LiftOffDistanceMm": 2,
     "RgbEnabled": true,
     "RgbEffect": "Breathing",
     "RgbColorHex": "#7620EE",
     "RgbBrightness": 60,
     "RgbSpeed": 50
   }
   ```
   Every field optional. Omitted → that aspect has no default and is left alone (a keyboard omits
   `Dpi`/`ReportRateHz`/`ButtonDebounceMs`/`LiftOffDistanceMm`; a non-RGB device omits the `Rgb*`
   fields). `RgbEffect` must name one of the App's effect keys — `Static`, `Breathing`, `Neon`,
   `ColorCycle`, `Wave` — and must be one the device can actually render (`Static`/`ColorCycle` are
   keyboard-only, per `KeyboardOnlyEffectKeys`). *(Corrected 2026-08-17: the keyboard-only pair was
   `Static`/`Wave` until mice were given `Wave` as the name for their protocol-0x04 effect and lost
   `ColorCycle`, which on a single-LED mouse was never a colour cycle. A mouse default naming
   `ColorCycle` is therefore no longer valid; `Wave` now is.)*

2. **`DeviceMap`** — add `public DeviceDefaultsConfig? Defaults { get; set; }` plus the
   `DeviceDefaultsConfig` class, mirroring `DeviceMacrosConfig`'s shape and comment style
   (all nullable, "null → the App uses its built-in default").

3. **Proto** (`ProoveGaming.GrpcDependencies` — **contract change, flag in the PR**) — extend
   `DeviceCatalogEntry` with fields 21-29, following the `macro_max_*` precedent that `0`/empty
   means unset:
   ```
   int32  default_dpi = 21;
   int32  default_report_rate_hz = 22;
   int32  default_button_debounce_ms = 23;
   int32  default_lift_off_distance_mm = 24;
   bool   default_rgb_enabled = 25;
   string default_rgb_effect = 26;      // "" → unset
   string default_rgb_color_hex = 27;   // "" → unset
   int32  default_rgb_brightness = 28;
   int32  default_rgb_speed = 29;
   ```
   `default_rgb_enabled` is a `bool` and so cannot express "unset" — gate it on
   `default_rgb_effect` being non-empty, and say so in the proto comment.

4. **`HidDeviceMapRegistry.FindCatalogEntry`** — map the block through, exactly like
   `MacroMaxDelayMs = m.Macros?.MaxDelayMs ?? 0`.

5. **`DeviceCardViewModel`** — one `DeviceDefaults` record property carrying the nullable values,
   so consumers ask the card rather than re-reading the catalog. **`HomeViewModel`** — populate it
   in *both* `FillConnectionCard` and `FillBluetoothCard` (the second is easy to miss and would
   leave Bluetooth-connected devices without defaults).

6. **Single resolution point** — one internal helper on `DeviceDetailsViewModel` that turns
   `Device.Defaults` into concrete values, including the DPI-value → stage-index resolution against
   `DpiPresets`, and falls back to today's constants when a field is unset. Both reset affordances
   and the seeding path call it, so the documented table and the buttons can never drift.

7. **Wire the consumers:**
   - `ApplyRgbDefaults()` — take effect/colour/brightness/speed/enabled from the card; drop the
     `IsKeyboardDevice` branch (fall back to `Breathing`, or `Static` where that is the configured
     value, only if the catalog says nothing).
   - `ApplyDpiRecommendations()` — actually restore: reset `DpiPresets` values from
     `Device.DpiStages`, highlight the resolved default stage, set `_dpiStageKnown = true`, persist,
     then `StageChangeAndApply()`.
   - `ResetSettings()` — keep the app-preferences resets; additionally restore `ButtonDebounce` /
     `LiftOffDistance` to the catalog defaults alongside the existing
     `ResetButtonBindingsToDefault()`.
   - `CreateDefaultPresetIfNotExists` caller (`LoadPresetsForDevice`) — after a *newly created*
     default preset, seed the live tables with the defaults and snapshot them, so the preset owns
     them. Must run under `_suppressPresetDirty` so seeding does not mark the fresh preset dirty.

8. **Docs** — `docs/device-defaults.md` generated by hand from the config, one row per device, with
   an explicit "—" where a field does not apply; linked from this plan and from `docs/CONTEXT.md`.

### Proposed values (all config-tunable afterwards without a rebuild)

`—` = deliberately unset because the device has no such concept.

| Device | Type | DPI | Rate | Debounce | Lift-off | RGB effect | Colour | Bright | Speed |
|---|---|---|---|---|---|---|---|---|---|
| BLAZE | Mouse | 1200 | 1000 Hz | 8 ms | 2 mm | Breathing | `#7620EE` | 60 | 50 |
| PG TAIPAN | Mouse | 1200 | 1000 Hz | — ¹ | 2 mm | — ² | — | — | — |
| Rampage | Keyboard | — | — | — | — | Static | `#7620EE` | 60 | — ³ |
| Raster | Keyboard | — | — | — | — | Static | `#7620EE` | 60 | — ³ |
| T-Racer | Keyboard | — | — | — | — | Static | `#7620EE` | 60 | — ³ |
| Aress | Keyboard | — | — | — | — | Static | `#7620EE` | 60 | — ³ |
| Cyrex | Keyboard | — | — | — | — | Static | `#7620EE` | 60 | — ³ |
| T800 | Keyboard | — | — | — | — | — ⁴ | — | — | — |

¹ TAIPAN's `SupportedMouseSettings` is `["LiftOffDistance"]` only — it exposes no debounce slider.
² TAIPAN's catalog entry sets no `SupportsRgb`, so it has no lighting section to default.
³ `Static` has `hasSpeed: false`, so a speed value would never be used.
⁴ T800's catalog entry sets no `SupportsRgb`.

Values carried over from today's code so this change alters no existing behaviour by accident:
`#7620EE` / 60 / 50 and the Static-vs-Breathing split are exactly what `ApplyRgbDefaults()` already
does; 8 ms and 2 mm are exactly `MouseSettingViewModel.Create`'s current defaults; 1000 Hz is
`DefaultPollingRateHz`. **1200 DPI is the only genuinely new value** — see Open questions.

## File-level change list

- `src/ProoveGaming.DeviceHost/appsettings.json` — add a `Defaults` block to each of the eight
  `AllowedDevices` entries, per the table above.
- `src/ProoveGaming.Infrastructure/Classes/DeviceMap.cs` — add `Defaults` property and the
  `DeviceDefaultsConfig` class (all-nullable, commented like `DeviceMacrosConfig`).
- `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto` — extend `DeviceCatalogEntry` with
  fields 21-29. **Contract change.**
- `src/ProoveGaming.DeviceHost/Services/HidDeviceMapRegistry.cs` — map `Defaults` into the catalog
  entry.
- `src/ProoveGaming.App/ViewModels/DeviceCardViewModel.cs` — carry the defaults as a
  `DeviceDefaults` record.
- `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` — populate it in `FillConnectionCard` **and**
  `FillBluetoothCard`.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs` — the single resolution
  helper (catalog → concrete values, DPI value → stage index).
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Rgb.cs` — `ApplyRgbDefaults()`
  reads the card; remove the `IsKeyboardDevice` branch.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Settings.cs` —
  `ApplyDpiRecommendations()` genuinely restores; `ResetSettings()` restores debounce/lift-off.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs` — seed the
  freshly created default preset's snapshot with the defaults, under `_suppressPresetDirty`.
- `src/ProoveGaming.App/Models/Settings/MouseSettingViewModel.cs` — allow the default value to be
  supplied by the caller; `Min`/`Max` stay here.
- `docs/device-defaults.md` — **new**, the published reference table.
- `docs/CONTEXT.md` — glossary entry for **Device defaults**.
- `tests/ProoveGaming.App.Tests/…` — see Test strategy.

## Acceptance criteria

- Given a BLAZE that has never been opened → open its Device Details → the DPI section highlights
  1200, the polling rate highlights 1000 Hz, all four remappable buttons show their factory
  functions, debounce reads 8 ms, lift-off 2 mm, and lighting is Breathing `#7620EE` at brightness
  60 / speed 50.
- Given that same first open → the device's default preset's snapshot contains those values (a
  later Discard restores them), and **Save Preset is disabled** — seeding must not mark the fresh
  preset dirty.
- Given a user who changed DPI to 6000 → click DPI **Recommendations** → the DPI ladder returns to
  the catalog values, 1200 becomes the active stage, and the change reaches the device within the
  normal debounce.
- Given a user who remapped a button and moved the debounce slider → click Settings **Restore to
  default** → buttons return to factory functions, debounce returns to 8 ms, **and** the DPI section
  is restored too (ladder, active stage 1200, rate 1000 Hz). *(Scope widened after the original
  draft — see "Restore-button scopes" under Implementation outcome. The first version scoped this
  button away from DPI; in practice that made it look inert on a device whose sliders and buttons
  were already at their defaults.)*
- Given DPI **Recommendations** on a device whose ladder does not contain the configured default
  value → the nearest available stage is selected, never an out-of-range index or a crash.
- Given a keyboard (e.g. Cyrex) → its defaults apply Static `#7620EE` at brightness 60, and **no**
  DPI stage or report rate is written to its preset snapshot (the capability gate from
  `live-apply-preset-dirty-diff` M1/m1 still holds).
- Given T800 or PG TAIPAN (no `SupportsRgb`) → no lighting default is applied and no RGB rows appear
  in the preset snapshot.
- Given a device entry with **no** `Defaults` block at all → behaviour is exactly today's: existing
  code constants are used, nothing crashes.
- `docs/device-defaults.md` matches the shipped `appsettings.json` values row for row.

## Test strategy

New feature → the following coverage is required, not optional. All in
`tests/ProoveGaming.App.Tests` (this is App-side behaviour; the DeviceHost mapping is a one-line
projection covered indirectly).

- **Defaults resolution** (new test class, e.g. `DeviceDefaultsTests`):
  - DPI value → stage index: exact match; nearest when absent; tie → lower; empty ladder → no stage.
  - Unset field → falls back to the existing code constant.
  - A device with no `Defaults` at all → every fallback used, no exception.
- **`DeviceDetailsPresetApplyTests`** (extends the existing fake-`IUsbHidDeviceService` harness):
  - First open of a fresh device seeds the default preset snapshot **and leaves
    `HasUnsavedPresetChanges == false`** — the regression this most plausibly introduces.
  - DPI Recommendations restores ladder + active stage and issues exactly one device apply.
  - Restore-to-default restores buttons + debounce and leaves `LastActiveStage` /
    `LastReportRateHz` unchanged (proves the "each section resets its own scope" decision).
  - Keyboard device → no `dpi.activeStage` / `pollingRate.hz` in the snapshot, extending the
    existing `ConfirmSavePreset_OnKeyboard_DoesNotSnapshotDpiStageOrReportRate`.
- **Deliberately not automated:** that the configured values *feel* right on hardware, and that the
  RGB effect renders — QA below. Also note `dotnet-ci.yml` builds only the App csproj and does not
  run `tests/`, so this coverage is local-only.

## QA (manual test steps)

1. Connect a BLAZE mouse that this app has never seen before (or delete the local database first).
   Open its Device Details. **Expected:** DPI shows 1200 selected, polling rate 1000, lighting is
   breathing purple, all buttons do their normal functions, and the SAVE PRESET button is greyed
   out (nothing to save).
2. Change the DPI to 6000 and the polling rate to 125 Hz, and confirm the mouse gets faster. Now
   click **Recommendations** in the DPI section. **Expected:** the DPI list returns to its standard
   values with 1200 selected, the polling rate returns to 1000 Hz, and the mouse speed changes back
   within about a quarter second. Buttons and the debounce slider are left alone.
3. Remap the right-side button to something else, drag the debounce slider away from 8 ms, and set
   the DPI to 6000 again. Then click **Restore to default** in Settings. **Expected:** everything
   returns to stock in one go — the button does its normal function again, debounce returns to
   8 ms, **and** the DPI returns to 1200 with the polling rate at 1000 Hz. (This button is the broad
   reset; **Recommendations** is the DPI-only one.)
4. Change the lighting colour and effect, then leave Device Details without saving the preset.
   **Expected:** lighting returns to breathing purple.
5. Connect a Cyrex or Rampage keyboard, never opened before. **Expected:** lighting is a solid
   purple, and there is no DPI or polling-rate section at all.
6. Connect a PG TAIPAN, never opened before. **Expected:** DPI 1200 and polling rate 1000 are
   selected; there is no lighting section and no debounce slider (this model has neither).
7. With everything at defaults, click SAVE PRESET if it is enabled, then leave and re-open the page.
   **Expected:** all values are unchanged and SAVE PRESET is greyed out again.

## Review focus

- `gRPC contract` — `DeviceCatalogEntry` gains nine fields; confirm field numbers are new (21-29),
  that DeviceHost populates every one, and that both `FillConnectionCard` and `FillBluetoothCard`
  consume them.
- `null-safety` — every default is optional at three layers (JSON absent → `DeviceDefaultsConfig`
  null property → proto zero/empty sentinel). Confirm "unset" survives all three hops and is not
  confused with a legitimate `0`.
- `performance hot-path` — none introduced, but confirm the seeding path runs once per device and
  not on every `SetDevice`.

## Performance & quality

No hot paths touched. The defaults are read once per device open and once per reset click; the
resolution helper is pure and allocation-light. The seeding path adds one snapshot write on the
first open of a device and never again. The existing single coordinated `ApplyCurrentStateToDeviceAsync`
write is reused unchanged — no new device-write path, so the BLAZE constraint is untouched.

## Risks

- **Seeding could mark a fresh preset dirty**, defeating the "clean on first open" acceptance
  criterion and re-enabling SAVE PRESET for a user who has changed nothing. Seeding must happen
  inside `_suppressPresetDirty` and be snapshotted, so live state and snapshot match. This is the
  single most likely regression — it is called out first in Test strategy for that reason.
- **Proto field-number collision** if another branch is also extending `DeviceCatalogEntry`. Check
  before merging; renumber rather than reusing.
- **`default_rgb_enabled` cannot express "unset"** as a proto `bool`. Gating it on
  `default_rgb_effect` being non-empty works, but it is an implicit coupling — if someone later
  configures `RgbEnabled: false` with no effect name, it silently does nothing.
- **The documented table can drift from `appsettings.json`.** It is hand-maintained; the acceptance
  criteria include a row-for-row match, and the reset affordances read the config rather than the
  doc, so drift misleads humans but cannot change behaviour.
- **A configured `RgbEffect` the device cannot render** (e.g. `Wave` on a mouse) would be filtered
  out of `VisibleRgbEffects` and leave the selection empty. Validate on read and fall back.

## Open questions

- **1200 DPI is the only value not carried over from existing code.** It is stage index 1 on both
  current mice. Confirm 1200 is the intended default rather than, say, 1600 (a common gaming
  default) — trivial to change in config afterwards, but it is what will ship.
- **Keyboard lighting default is `Static #7620EE` at brightness 60**, matching today's
  `ApplyRgbDefaults()`. Confirm that solid purple is the intended out-of-box look rather than a
  moving effect like `Wave` or `ColorCycle`.
- **T800 and PG TAIPAN get no lighting default** because their catalog entries set no
  `SupportsRgb`. Confirm that is correct for the hardware and not a gap in the existing config.
- **Should `Defaults` be required rather than optional?** Optional keeps this change additive and
  lets a new device ship before its defaults are tuned, at the cost of silent fallback to code
  constants. Recommended: keep optional now, revisit once every device has a block.

## Implementation outcome (2026-08-03)

Implemented; `dotnet build ./src/ProveGaming.slnx` and `dotnet test ./src/ProveGaming.slnx`
(733 tests) both green.

Delivered as planned:

- `DeviceMap.Defaults` + `DeviceDefaultsConfig`; proto fields 21-29 on `DeviceCatalogEntry`;
  `HidDeviceMapRegistry.ToCatalogEntry` mapping; `Defaults` blocks for 7 of 8 catalog devices
  (T800 has no adjustable DPI and no lighting, so it declares nothing).
- New `DeviceDefaults` record; `DeviceCardViewModel.Defaults`; populated from both
  `FillConnectionCard` and `FillBluetoothCard` via `HomeViewModel.ToDeviceDefaults`.
- `DeviceDetailsViewModel.ResolveDefaultDpiStageIndex()` (nearest stage, ties to the lower value)
  and `DefaultReportRateHz`; `DefaultPollingRateHz` renamed to `FallbackPollingRateHz` to make the
  catalog-over-constant layering explicit at every call site.
- `ApplyDpiRecommendations()` now genuinely restores: rebuilds the ladder from the catalog,
  highlights the resolved default stage, and persists both the table and the active stage.
- `ResetSettings()` restores debounce / lift-off via `ResetDeviceSettingsToDefault()`, alongside
  the existing button reset. (It restores the DPI section too — see "Restore-button scopes" below;
  the original draft scoped it away from DPI.)
- `ApplyRgbDefaults()` is catalog-driven and the `IsKeyboardDevice` branch is gone. An effect key
  the device cannot render falls back to its first visible effect.
- `MouseSettingViewModel.DefaultValue` carries each slider's own fallback.
- `docs/device-defaults.md` (published reference table) and the **Device defaults** glossary entry.
- `tests/ProoveGaming.App.Tests/DeviceDefaultsTests.cs` — initially 10 tests covering nearest-stage
  resolution, the null case, report-rate layering, slider seeding / fallback / clamping,
  `ResetSettings`, `ApplyDpiRecommendations`, and both RGB-default paths.

Deviations from the plan, both deliberate:

1. **First-use seeding is split across three consumers.** The sliders are seeded in
   `RebuildDeviceSettings()`, lighting in the existing no-stored-RGB path `ApplyRgbDefaults()`, and
   the DPI stage / report rate by `SeedCatalogDefaultDpiAndRate()`. Only the last one touches the
   preset snapshot, as the plan intended — see the note below on where it had to go.
2. **`MouseSettingViewModel` lives in `ViewModels/DeviceDetails/`**, not `Models/Settings/` as the
   change list guessed.

### Post-merge correction (merge of `main` @ `ed388db`)

`main`'s Cyrex/Aress RGB fix landed the mechanism this plan needed and I had originally routed
around: `CreateDefaultPresetIfNotExists` now returns a `Seeded` flag that distinguishes a fresh
insert from a legacy promote, and `LoadPresetsForDevice` snapshots live state into the brand-new
default preset only when `Seeded` is true.

The first implementation seeded the DPI stage / report rate from `EnsureDefaultPresetAppliedAsync`,
which runs **after** that snapshot is taken. `LoadPresetSnapshotToDevice` wipes `device_settings`
before copying the snapshot back, so the seed was silently discarded on first open — it never
reached the hardware or the snapshot. Corrected by moving the seed into the `if (defaultPreset.
Seeded)` branch of `LoadPresetsForDevice`, immediately before `SavePresetSnapshot`, and making it
synchronous (`SettingsRepository` writes straight through to SQLite and returns completed tasks,
the same property `PersistRgbStateForCurrentDevice` relies on).

`Seeded` is also a better gate than the original "nothing stored" check: it means this device type
has no presets at all. The live-value checks (`TryGetLastDpiStage` / `TryGetLastReportRate`) are
kept, so a stage the DeviceHost has already read off the hardware is never overwritten by a default
— the B1 guard from `live-apply-preset-dirty-diff` still holds.

Regression test: `DeviceDefaultsTests.FirstOpen_SeedsTheCatalogDpiStageAndRate_AndTheySurviveThe
DefaultPresetApply`, verified to fail (`KeyNotFoundException: 'dpi.activeStage'`) with the seed call
removed. Plus `SecondOpen_DoesNotReSeedOverTheUsersOwnDpiStage`.

One further merge fix, in `PersistDisplayedDeviceState`: this branch's m1 fix added an early
`if (!Device.SupportsDpiStageSwitch) return;` that would have skipped `main`'s new
`PersistRgbStateForCurrentDevice()` call for keyboards — precisely the devices its RGB fix targets.
The RGB persist now runs first, outside that capability gate, which only wraps the DPI/rate writes.

Suite after the merge: 738 passing (355 Infrastructure + 383 App), build clean.

### Post-draft fixes (hardware testing, 2026-08-03)

Four problems only visible on real hardware, in the order they surfaced. Each fix was verified to
fail against the pre-fix code before being kept.

1. **Polling rate rendered blank on app start.** `ApplyKnownDeviceState` deselects every rate option
   when the DeviceHost has reported no live rate, and on an install with no stored rate nothing put
   one back. `LoadPersistedDeviceStateAsync` now resolves both stage and rate as
   **live → stored → catalog default**. The fallback runs last and on both paths (including
   `forcePersisted`), because a preset apply is the final word on a device open and would otherwise
   wipe a fallback applied before it. Deliberately not persisted: a live row the active preset's
   snapshot lacks is what `PresetMatchesLiveState` compares on, so writing it would enable Save
   Preset on an untouched page.
2. **TAIPAN's polling rate appeared stuck until the physical DPI button was pressed.** The fallback
   above issued a *bare* rate-only write. On TAIPAN that is a rate-only MsConfig patch carrying no
   DPI stage, which `TaipanMsConfig.IsFactoryDefaultFallbackSafe` refuses at cold start when the
   host knows no live stage — the vendor factory-default block would otherwise move the user's real
   DPI. `_liveDpiStage` only becomes known when the host writes a stage itself or the mouse pushes
   `0x49` on a physical DPI-button press. The bare write is now gated on `assertStageToDevice`, so
   the preset path lets `ApplyCurrentStateToDeviceAsync` send stage + rate as one coordinated write.
   Constraint recorded in `docs/mouse/taipan/protocol.md`.
3. **Restore-button scopes widened** (user decision, supersedes the original draft). Both buttons
   looked inert because the only parts they could visibly change were already at their defaults.
   **Recommendations** now restores the whole DPI section — ladder, active stage **and polling
   rate**. **Restore to default** is the broad reset: preferences, sliders, buttons **and** the DPI
   section. Both share `RestoreDpiSectionDefaultsAsync`, and every part stages synchronously so the
   debounce coalesces a whole reset into one coordinated device write. A restore also now falls back
   to the ladder's first stage rather than keeping the user's current stage, which is
   indistinguishable from the button doing nothing.
4. **The `Defaults` block never reached the App at all** — the root cause behind BLAZE restoring to
   800 instead of 1200. `appsettings` binds `AllowedDevices:Devices` into **`DeviceGroupConfig`**,
   not `DeviceMap`; `DeviceMapExpander` then flattens group → per-transport rows. `Defaults` was
   added to the expander's *output* but never to the class binding actually reads, and configuration
   binding silently ignores JSON keys with no matching property — so the whole block was discarded
   at the first hop while `appsettings.json`, `DeviceMap`, the proto, `ToCatalogEntry` and the App
   all read correctly. Fixed by adding `Defaults` to `DeviceGroupConfig` and cloning it per
   transport row. Covered by three `DeviceMapExpanderTests`, including non-aliasing between rows.

Final state: 18 tests in `DeviceDefaultsTests`, 3 in `DeviceMapExpanderTests`, 1 TAIPAN write-shape
regression test in `DeviceDetailsPresetApplyTests`. Suite 748 passing, build clean.

## Review findings

### Round 1 — Ship: yes

Reviewed the full branch delta of `fix/PG-512-afterwards` against **`origin/main`** (not local
`main`, which is stale at `d146223` and would have wrongly attributed PR #78's Aress SVG /
`DoubleCard.axaml` / RGB-loop fix to this branch). Working tree clean; 27 files, +1912/−73.
Build green, 748 tests pass (358 Infrastructure + 390 App).

#### Plan conformance

Plan: `docs/plans/add-device-default-settings/add-device-default-settings-plan.md`. The branch also
carries closing work for two sibling plans (`live-apply-preset-dirty-diff` M1/M3 fixes,
`share-presets-by-device-type` M2 `PromotePresetToDefault` rescope) — in scope for the branch,
tracked in those plans' own journals.

- **Implemented as planned:** `Defaults` config block → `DeviceMap` → proto fields 21-29 →
  `HomeViewModel` → `DeviceCardViewModel`; both `FillConnectionCard` and `FillBluetoothCard`
  consume it; `ResolveDefaultDpiStageIndex` (DPI *value* → nearest stage, ties low) per Option A;
  `ApplyRgbDefaults` catalog-driven with the `IsKeyboardDevice` branch removed; seeding gated on
  the default preset being freshly `Seeded`; `docs/device-defaults.md` + `docs/CONTEXT.md`.
- **Acceptance criterion verified mechanically:** the published table matches `appsettings.json`
  row for row for all 8 devices (checked by script against the live config).
- **Deviates from plan — scope of "Restore to default"** (see S4). The plan states the button must
  leave DPI and report rate **untouched**; the shipped code restores them. This was an explicit
  mid-flight user decision, but three plan sections still assert the old contract.
- **Accepted limitation (per plan Risks):** `default_rgb_enabled` cannot express "unset" and is
  gated on `default_rgb_effect` being non-empty. Residual risk unchanged: configuring
  `RgbEnabled: false` with no effect name silently does nothing, with no validation or log.

#### Code

- [x] **S1. The report-rate fallback re-writes the device on every page open** —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs:513-533` — Should-fix

  **Problem / why it matters:** the fallback sets `_reportRateKnown = true` and issues
  `SetReportRateAsync`, but deliberately does **not** persist the value. `_reportRateKnown` is reset
  to `false` in the `Device` setter on every open, and `ApplyKnownDeviceState` leaves it false when
  the host reports no live rate — so on an install where nothing ever stored a rate, the condition
  is true again on *every* `SetDevice`.

  **Failure scenario:** existing install (default preset already exists, so
  `SeedCatalogDefaultDpiAndRate` never runs), user never saves a preset. Opening Device Details 20
  times issues 20 identical report-rate writes. On TAIPAN that is 20 MsConfig commits, i.e. flash
  erase+write blocks — the code's own comment notes a mandatory pause after each commit "so a
  follow-up change doesn't read back a mid-erase sector". The adjacent DPI-stage path already
  guards exactly this with `_assertedDpiStages`, "so re-opening a device page doesn't re-write
  flash for a stage the device already holds"; the new rate write has no equivalent.

  **Solution options:**
  - A (correct, matches existing precedent): add a `_assertedReportRates` dictionary keyed by
    `AssertedStageIdentity(device)` and skip the write when the value is unchanged.
  - B (simpler): persist the fallback via `SavePollingRateAsync` so `storedHz` is non-null on the
    next open. Costs the property the current comment protects — a live row the preset snapshot
    lacks would re-enable Save Preset — so it needs the snapshot seeded too, and is the riskier of
    the two.

- [x] **S2. A configured `ReportRateHz` outside the four supported options silently breaks the
  section** — `DeviceDetailsViewModel.Settings.cs:196-201` and `:265-269` — Should-fix

  **Problem / why it matters:** `PollingRateOptions` is a fixed list (125/250/500/1000).
  `HighlightPollingRate(hz)` sets `IsSelected = option.Hz == hz` for each, so an unmatched value
  deselects **everything**. The DPI default gets nearest-stage resolution and the sliders get
  `ClampToSetting`; the report rate gets neither.

  **Failure scenario:** a config author writes `"ReportRateHz": 2000` for a new device. Opening the
  page renders the Polling Rate section blank while `_reportRateKnown = true` claims a value is
  known; pressing Recommendations then persists 2000 via `SavePollingRateAsync`, so every later
  open reads 2000 back, matches no option, and the section stays permanently blank — with no
  validation error and no log line anywhere.

  **Solution options:**
  - A (correct, mirrors the DPI treatment): resolve the configured rate to the nearest supported
    option, the same way `ResolveDefaultDpiStageIndex` resolves a DPI value to a stage.
  - B (minimal): in `DefaultReportRateHz`, ignore a configured value that no `PollingRateOptions`
    entry matches and fall back to `FallbackPollingRateHz`, logging once.

- [~] **S3. The new catalog-defaults log cannot be seen in a normal run** —
  `src/ProoveGaming.App/ViewModels/HomeViewModel.cs:513-521`, sink at `App.axaml.cs:177` —
  Should-fix

  **Problem / why it matters:** commit `e8569d8` added this line specifically so an old DeviceHost
  sending `0` becomes visible rather than silently changing which value is restored. But the App
  registers only `services.AddLogging(builder => builder.AddDebug())` — a `System.Diagnostics.Debug`
  sink. Outside a debugger the output goes nowhere, so for the user this diagnostic does not exist.
  This is the exact failure mode that cost most of a debugging session on this branch (the
  `DeviceGroupConfig` drop looked identical to "device declares no defaults").

  **Fix:** give the App a file sink alongside `AddDebug()` (the DeviceHost already writes
  `%TEMP%/Logs/log.txt` via Serilog and is the obvious model), or surface the mismatch where a user
  can act on it. Adding a Serilog package to the App is a dependency change and per `CLAUDE.md`
  must be raised explicitly rather than slipped in — flagging for a decision rather than
  prescribing.

- [x] **S4. The plan contradicts the shipped behaviour of "Restore to default"** —
  plan Acceptance criteria, QA step 3, and Implementation outcome — Should-fix

  **Problem / why it matters:** three places still state the button must leave DPI and report rate
  untouched ("**and the DPI stage and report rate are left untouched**"; "the DPI stays where step 2
  left it — this button must not touch DPI"; "the DPI stage and report rate are deliberately left
  alone"). Commit `c2f8d68` deliberately widened it at the user's request. A QA engineer handed
  step 3 verbatim — which the plan explicitly says is its purpose — will raise a false bug.

  **Fix:** update those three passages to the shipped scope (Recommendations = DPI section incl.
  polling rate; Restore to default = everything). The Implementation outcome should also record the
  four post-merge fixes it predates (polling-rate fallback, TAIPAN bare-write gate, widened button
  scopes, `DeviceGroupConfig` binding) and the test count (18, not 10).

#### Tests

- [x] **No coverage of the plain-open device write** — every test in `DeviceDefaultsTests`
  constructs the VM with `usbHidDeviceService: null`, so none of them can observe a device write.
  The only write-shape assertion is `PresetApply_DoesNotIssueABareReportRateWrite`, which pins the
  *preset* path at zero. Nothing pins the plain-open path at exactly one — which is precisely why
  S1 (a write per open, unbounded) is invisible to the suite. Add a test using the
  `FakeUsbHidDeviceService` harness asserting `SetReportRateCallCount == 1` after two consecutive
  `SetDevice` calls on the same device — `tests/ProoveGaming.App.Tests/DeviceDetailsPresetApplyTests.cs`.
- [x] **No coverage for an unsupported configured `ReportRateHz`** (S2) — a test asserting the
  section still lands on a selectable option would have caught it — `DeviceDefaultsTests`.

#### Nits

- The tie-break in `ResolveDefaultDpiStageIndex` (`distance == bestDistance && preset.DpiValue <
  best.DpiValue`) is unreachable for an ascending ladder, since the lower of two equally-distant
  stages is always encountered first — `DeviceDetailsViewModel.cs:378-388`. Harmless and defensive;
  it only earns its keep if a ladder is ever authored descending.
- The DPI-stage fallback is not gated on `SupportsDpiStageSwitch` the way the rate fallback beside
  it is — `DeviceDetailsViewModel.cs:487-491`. Currently contained (a keyboard declares no `Dpi`, so
  resolution returns null, and both sections are hidden for keyboards anyway), but the asymmetry
  invites a future keyboard config to set `_dpiStageKnown = true` for a device that has no stages.
- `ResetDeviceSettingsToDefault` ends in `_ = ApplyMouseSettingsAsync();` —
  `DeviceDetailsViewModel.Settings.cs:381`. Fire-and-forget, so a throw from
  `SaveMouseSettingsAsync` becomes an unobserved task exception rather than anything visible.
- `SeedCatalogDefaultDpiAndRate` depends on `SettingsRepository` writing through synchronously and
  returning completed tasks — documented in the method comment, and pinned by
  `FirstOpen_SeedsTheCatalogDpiStageAndRate_AndTheySurviveTheDefaultPresetApply`, but it is an
  implementation detail of an `async`-typed interface, so the guarantee lives only in that test.

#### What's done well

- **The root-cause fix in `DeviceGroupConfig`** (`d4034eb`) is the important one on this branch.
  `Defaults` was added to `DeviceMap` (the expander's *output*) but never to the class configuration
  actually binds to, so the block was dropped silently while `appsettings.json`, `DeviceMap`, the
  proto and the App all read correctly. Three `DeviceMapExpanderTests` now pin it, including
  non-aliasing across transport rows.
- **Every fix on this branch was verified to fail against the pre-fix code before being kept** —
  including one case where the first version of a test passed either way and was rewritten with an
  existing preset so it genuinely reproduced the reported symptom. That discipline is the reason
  the late fixes are trustworthy; don't relax it.
- **The merge of `origin/main` preserved the keyboard RGB fix.** This branch's m1 guard
  (`if (!Device.SupportsDpiStageSwitch) return;`) would have skipped main's new
  `PersistRgbStateForCurrentDevice()` for exactly the devices that fix targets; the resolution moves
  RGB outside the capability gate rather than taking either side wholesale.
- **`docs/device-defaults.md` matches `appsettings.json` row for row** — verified by script, not by
  eye — and `docs/mouse/taipan/protocol.md` now records the cold-start MsConfig constraint that made
  the polling-rate symptom so confusing.

#### Verdict

Ship: yes — no blockers; the feature works end to end and the hardest bug (config binding silently
dropping the whole block) is fixed with regression cover. S1 and S2 are latent-quality issues worth
closing before this pattern is copied to the next device, and S4 is a documentation correction that
matters because QA reads it verbatim. First: (1) S4, cheapest and actively misleading; (2) S1, with
the test that exposes it; (3) S2.

### Round 1 — fixes applied (2026-08-03)

All Should-fix items closed except S3, which the user explicitly deferred. Build clean, 751 tests
pass (358 Infrastructure + 393 App). Every fix was verified to fail against the pre-fix code.

- **S4 — plan corrected.** Acceptance criteria, QA steps 2-3 and the Implementation outcome now
  describe the shipped scopes (Recommendations = DPI section incl. polling rate; Restore to default
  = everything). QA step 2 additionally exercises the polling rate, and step 3 now asserts the DPI
  *does* move. Added a "Post-draft fixes" section recording the four hardware-driven changes the
  outcome section predated, and corrected the test count.

- **S1 — deduplicated, and my severity claim was wrong.** The review said the fallback re-writes
  "on every page open". That is **incorrect**: `SetDevice` assigns `Device`, and the setter's
  `SetProperty` short-circuits on an unchanged reference, so re-opening the *same* card never resets
  `_reportRateKnown` and never re-runs the fallback. The first regression test I wrote for this
  passed with the fix removed, which is what exposed the error. The real trigger is **alternating
  between two devices**: each switch resets the flag, finds nothing stored (the fallback is
  deliberately never persisted), and writes again — 4 writes across 2 switches instead of 2.
  Narrower than reported, still real, and on TAIPAN still an MsConfig flash erase+write per switch.

  Fixed by routing the write through the existing `_assertedReportRates` cache
  (`DeviceDetailsViewModel.cs:513-546`) rather than adding a new one. This also closes a second
  defect the review missed: the bare write never called `NoteAssertedReportRate`, so the
  DeviceHost's confirmation push for the app's *own* write could be read by `OnReportRateChanged`
  as a physical polling-button press → `StageChangeAndApply()` → **preset marked dirty on a page the
  user never touched**. The coordinated apply path has always noted both caches for exactly this
  reason; the fallback was the only writer that did not.

  Test: `DeviceDetailsPresetApplyTests.AlternatingBetweenDevices_DoesNotRepeatTheFallbackReportRateWrite`,
  verified to fail (2 expected, 4 actual) without the dedup.

- **S2 — configured report rate now resolved to the nearest supported option.**
  `DefaultReportRateHz` (`DeviceDetailsViewModel.cs:391-417`) picks the nearest entry in
  `PollingRateOptions`, ties to the lower rate, mirroring `ResolveDefaultDpiStageIndex`. A value
  like `900` now lands on 1000 instead of deselecting every row and leaving the section blank while
  `_reportRateKnown` claimed a value was known. Tests:
  `DefaultReportRateHz_ResolvesAnUnsupportedConfiguredRateToTheNearestOption` and
  `ApplyDpiRecommendations_WithAnUnsupportedConfiguredRate_StillSelectsARate`, both verified to fail
  (1000 expected, 900 actual) without the fix.

- **S3 — deferred by the user.** The App still registers only `AddDebug()`, so the `[Catalog]` line
  added in `e8569d8` remains invisible outside a debugger. Residual risk unchanged: the next
  "defaults did not arrive" problem presents identically to "this device declares no defaults", with
  no signal a user can read. Revisit if that recurs; the options were a hand-rolled `ILoggerProvider`
  (no new dependency) or Serilog + file sink to match the DeviceHost.

Nits: not addressed this round. The unreachable tie-break in `ResolveDefaultDpiStageIndex` is now
mirrored deliberately in `DefaultReportRateHz` (both are defensive against a non-ascending list);
the ungated DPI-stage fallback and the fire-and-forget `ApplyMouseSettingsAsync` stand as reported.
