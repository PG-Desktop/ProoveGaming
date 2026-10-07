# Device defaults

Every device in the catalog can declare a set of **factory-sane starting values**. These are the
values the app uses in three situations:

1. **A device is used for the first time** — no saved configuration exists for it yet, so the app
   seeds its settings from these values instead of from generic slider minimums.
2. **DPI section → "Recommendations"** — restores that section only: the DPI stage ladder, the
   active stage (nearest the default DPI), and the polling rate.
3. **Settings section → "Restore to default"** — the broad reset. Restores the remappable buttons to
   their factory functions, the per-device sliders (button debounce, lift-off distance), **and**
   everything "Recommendations" covers.

The two overlap deliberately: "Recommendations" is the narrow, DPI-only reset; "Restore to default"
puts the whole device back to stock.

Once the user changes a setting, their value is saved and wins. Defaults are only consulted for
devices that have nothing saved, or when the user explicitly presses one of the two restore buttons.

## Where they are configured

Defaults live next to each device in the DeviceHost catalog, under a `Defaults` block:

`src/ProoveGaming.DeviceHost/appsettings.json` → `AllowedDevices:Devices[*]:Defaults`

They travel to the App over gRPC as part of `DeviceCatalogEntry`
(`src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto`), and are surfaced to the UI as
`DeviceCardViewModel.Defaults`.

Every field is optional. **Omitting a field means "no opinion"** — the app then falls back to its
own built-in value (listed under [Fallbacks](#fallbacks) below). Only declare a field for a device
that actually has the corresponding capability: a mouse with no adjustable debounce should not
declare `ButtonDebounceMs`, and a device with no lighting should not declare any `Rgb*` field.

### Fields

| Field | Unit | Meaning |
| --- | --- | --- |
| `Dpi` | DPI | Target sensor resolution. Resolved to the **nearest stage** on the device's DPI ladder (ties go to the lower stage), so it does not need to match a stage exactly. |
| `ReportRateHz` | Hz | Polling / report rate. |
| `ButtonDebounceMs` | ms | Button debounce slider. Clamped to the slider's range (2–50). |
| `LiftOffDistanceMm` | mm | Lift-off distance slider. Clamped to the slider's range (2–3). |
| `RgbEnabled` | bool | Whether lighting starts on. Only honoured when `RgbEffect` is also set. |
| `RgbEffect` | key | Lighting effect key. Must be an effect this device can render — mice do not expose `Static` / `Wave`, and an unrenderable key falls back to the device's first visible effect. |
| `RgbColorHex` | `#RRGGBB` | Starting colour for every effect. |
| `RgbBrightness` | 0–100 | Starting brightness. |
| `RgbSpeed` | 0–100 | Starting animation speed (ignored by non-animated effects). |

## Current values

| Device | VID / PID | DPI | Report rate | Debounce | Lift-off | RGB | Effect | Colour | Brightness | Speed |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| BLAZE | 1241 / 41541 | 1200 | 1000 Hz | 8 ms | 2 mm | on | Breathing | `#7620EE` | 60 | 50 |
| PG TAIPAN | 9354 / — | 1200 | 1000 Hz | — | 2 mm | — | — | — | — | — |
| Rampage | 10473 / 12888 | — | — | — | — | on | Static | `#7620EE` | 60 | — |
| Raster | 14000 / 12385 | — | — | — | — | on | Static | `#7620EE` | 60 | — |
| T-Racer | 14000 / — | — | — | — | — | on | Static | `#7620EE` | 60 | — |
| Aress | 10473 / 12910 | — | — | — | — | on | Static | `#7620EE` | 60 | — |
| Cyrex | 10473 / 12914 | — | — | — | — | on | Static | `#7620EE` | 60 | — |
| T800 | 12625 / 20525 | — | — | — | — | on | Static | `#7620EE` | 60 | — |

`—` means the device declares no default for that field, either because it lacks the capability or
because the app's fallback is already the right answer.

Notes on the choices above:

- **Keyboards default to `Static`.** Most users pick one colour and leave it; an animated effect out
  of the box reads as noise. Mice default to `Breathing`, because `Static` is not in their effect
  list at all.
- **`#7620EE`** is the product's brand purple, so a device lights up on-brand before the user has
  touched anything.
- **PG TAIPAN declares no debounce and no lighting** — it exposes neither (`SupportedMouseSettings`
  has no `ButtonDebounce`, and it has no `SupportsRgb`).
- **The keyboards declare lighting only.** None of them has an adjustable DPI, a debounce slider
  or a lift-off slider, so their `Defaults` blocks carry the four `Rgb*` fields and nothing else.
  (T800 included — it has `SupportsRgb` and the same lighting defaults as the rest.)

## Button assignments

Button defaults are **not** configured here. Each remappable button's factory function (left click
is left click, wheel click is middle click, and so on) is defined in code per device layout, and
"Restore to default" in the Settings section restores exactly those. This keeps the physical button
map — which is a property of the hardware layout, not a tunable preference — in one place.

## Preset reset — what it restores

The presets list's row menu has a **Reset** entry (confirmed by a dialog first). It restores the
device to its defaults and saves that state **into** the preset, so the row afterwards holds the
defaults rather than nothing.

What it restores is driven by the device's declared capabilities, and the aspect list is exactly the
set a preset snapshot stores:

| Aspect | Restored from | Applies to |
| --- | --- | --- |
| Mouse sliders (debounce, lift-off) | this document's defaults | mice |
| Button assignments | the per-layout factory map (see above) | remap-capable devices |
| Audio levels (output, microphone) | each slider's own default (50%) | headsets |
| Lighting | this document's `Rgb*` defaults | any device with RGB |
| DPI ladder, active stage, polling rate | this document's defaults | DPI-capable devices |
| Key actuation (press / release depth, Rapid Trigger) | the board's own reported defaults | T800, Cyrex, Aress |

**Actuation is the one aspect whose defaults are not declared here.** The magnetic boards report
their own limits *and* their own defaults over `GetKeyActuation`, so the hardware is authoritative
about its travel and there is nothing for the catalog to say. Declaring `Actuation*` fields would
duplicate hardware truth in configuration and create a second answer to the same question. For
actuation the resolution order is therefore **live (the board's read) → stored (the active preset)
→ the board's reported defaults**, with no catalog tier — which is also what the Actuation section's
own "Restore defaults" button uses.

App-scoped preferences — scroll speed, double-click speed, sleep timer — are deliberately **not**
touched. They live outside any preset and are shared by every device, so resetting one preset must
not change them. The Settings section's own "Restore to default" does clear them; that is the
intended difference between the two.

### Known gap: keyboard key assignments are not reset

**Out of scope for now — to be closed later.**

`key_bindings` and `key_macro_bindings` are part of a preset snapshot, but the preset Reset does not
restore them. Every other aspect above has a defined default to restore to; a keyboard key map does
not — there is no default-layout mechanism anywhere in the codebase, and a key map is per-board.

So today, resetting a preset on a keyboard restores its lighting but leaves custom key assignments
and key-bound macros in place.

The likely meaning when this is picked up is "clear every custom key assignment so the board returns
to its factory layout", which `LocalStorageService.DeleteKeyBinding` can be built from — but that is
a design decision, not a bug fix, so it was left rather than guessed at.

Implementation: `DeviceDetailsViewModel.ResetPresetAspectsToDefaultsAsync`
(`src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Presets.cs`).

## Fallbacks

When a device declares no default for a field, the app uses:

| Field | Fallback |
| --- | --- |
| `Dpi` | the first stage on the device's DPI ladder |
| `ReportRateHz` | 1000 Hz |
| `ButtonDebounceMs` | 8 ms |
| `LiftOffDistanceMm` | 2 mm |
| `RgbEnabled` | on |
| `RgbEffect` | the device's first visible effect |
| `RgbColorHex` | `#7620EE` |
| `RgbBrightness` | 60 |
| `RgbSpeed` | 50 |

## Adding defaults for a new device

1. Add a `Defaults` block to that device's entry in
   `src/ProoveGaming.DeviceHost/appsettings.json`, declaring only the fields it supports.
2. Add a row to the [Current values](#current-values) table above.
3. No code change is required — the values flow through the existing catalog entry.

## When the fallback kicks in

For the DPI stage and report rate the app resolves a value in this order, on every device open:

1. **Live value from the DeviceHost** — what the hardware itself reports (e.g. after a physical DPI
   button press). Always wins.
2. **Stored value** — what the user saved for this device, or what the active preset holds.
3. **Catalog default** — this document's table.

Step 3 is a display/apply fallback only: it is **not** written to the settings store, because a row
the active preset's snapshot does not have would make "Save Preset" light up on a page the user
never touched. It is persisted the first time the user actually saves, and once when a device type's
default preset is first created.
