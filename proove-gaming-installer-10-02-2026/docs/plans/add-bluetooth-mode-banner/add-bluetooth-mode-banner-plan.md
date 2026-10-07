# Plan: add-bluetooth-mode-banner

> **Note (2026-09-24):** the Firmware section is hidden for now, because firmware update does not work yet. It is not removed: `DeviceDetailsViewModel.IsFirmwareNavVisible` (one constant) hides both nav entries and makes the section unreachable. Everything this plan says about the Firmware section applies again once that flag is switched back on.

> **Superseded in part, 2026-09-02:** the uniform "every Bluetooth device locks" product decision
> of 2026-08-30 is reversed for **headsets** (Cosmo, Starlight) — their audio targets the OS
> endpoint and works over Bluetooth, so they show no banner and no section locking. The condition
> everywhere is now `Device.IsBluetoothConfigLocked` (`HasBluetooth && !SupportsConfiguration`,
> catalog-driven); keyboards and mice keep `SupportsConfiguration: false` on their Bluetooth
> catalog rows and behave exactly as this plan describes. See
> `docs/plans/remove-headset-bluetooth-lock/remove-headset-bluetooth-lock-plan.md`. The live
> sections below are corrected where the old condition was stated; the review journal is not.

## Goal

Devices connected over Bluetooth cannot accept configuration writes — the details page is
effectively read-only in that state (see `IsDeviceConfigurable` /
`DeviceCardViewModel.SupportsConfiguration`), but today nothing on the page *says* so: sliders and
pickers look live and silently do nothing. Add the designed "BLUETOOTH MODE" info banner to the
device-preview panel of the details page, shown whenever the device's active transport is
Bluetooth, telling the user that settings are unavailable over Bluetooth and to connect via
dongle or USB cable to make changes.

Design reference: SVG block 628×84 provided in the request (background `#6357FF` at 10 % opacity,
warning-triangle icon + "BLUETOOTH MODE" title in `#6357FF`, body copy in `#BEBCEA` at 70 %).
Position per screenshot: top of the right device-preview panel, margins **40 left / 40 top /
40 right** measured from the panel's *content area* — i.e. inside the 42 px chrome rails, so
82 px from the panel's outer edge on each side (pixel-measured against the mock 2026-08-30) —
with the banner width capped at the SVG's 628 px, the same width the device images render at.

## Scope

**In:**
- A new shared banner control shown at the top of every right-side device-preview panel in
  `DeviceDetailsView.axaml` (mouse stack-nav layout, keyboard layout, Buttons section, keyboard
  Key-Assignments section).
- Visibility driven by `Device.IsBluetoothConfigLocked` (`HasBluetooth &&
  !SupportsConfiguration`) — *corrected 2026-09-02.* The 2026-08-30 product decision made
  Bluetooth mode read-only UI for **every** Bluetooth device via `Device.HasBluetooth` alone;
  that is superseded for headsets, whose Bluetooth rows are catalog-configurable and therefore
  never lock (see the note at the top and Options considered).
- **Every device-details section panel** (Presets, RGB Lighting, Buttons, Macros, DPI, Settings,
  Audio, Actuation, Firmware) is **disabled** while in Bluetooth mode: content half-opacity and
  non-interactive via a shared `bluetoothLocked` class + `IsEnabled` binding on each section's
  content; lists still scroll (the binding sits on the content, not the ScrollViewer). RGB keeps
  its finer mock-driven dimming (title stays bright).
- **The header preset controls too** (2026-08-31, per the header mock): the Current Preset
  dropdown (half opacity), SAVE PRESET and NEW PRESET (their shared `secondary`/`primaryButton`
  `:disabled` looks; the + icon dims via `TintColor`). The back button and page title stay
  active — leaving the page must always work. Disabled buttons everywhere stay *visible*: every
  themed button needs its `:disabled` presenter re-stated or the Fluent dark resources melt it
  into the panel (RANDOM LIGHTING, NEW MACRO shipped that way first).
- Two new localization keys in **both** `Strings.en.axaml` and `Strings.uk.axaml`.
- Glossary entry in `docs/CONTEXT.md`.

**Out:**
- ~~Disabling/greying the actual controls~~ — *superseded 2026-08-30 by the product decision
  above: the sections, header preset controls and their buttons ARE disabled in Bluetooth mode.*
  What stays out is the write-side logic itself — the guards (`IsDeviceConfigurable`,
  `SupportsConfiguration`) are unchanged.
- The home-screen device cards (`SingleCard` / `DoubleCard`) — the design places the banner on the
  details page only.
- Any DeviceHost / gRPC / catalog change.

## Options considered

1. **Inline `Border` copied into each of the four preview panels.**
   - − Four near-identical copies; the repo's "second copy triggers extraction" rule forbids this
     from the start.
2. **Shared `UserControl` (`BluetoothModeBannerView`) placed once per preview panel, like the
   existing `ConnectivityBarView`.** *(recommended)*
   - - One source of truth for markup, colours, and localization bindings; each call site is a
     single line. Exactly the pattern `ConnectivityBarView` already uses for the same panels.
   - − One more view file; negligible cost.
3. **Banner inside each section view (`PresetsSectionView`, `RgbLightingSectionView`, …).**
   - − Wrong layer: the design anchors the banner to the *preview* panel, not the 336-wide section
     column, and it would need N copies across sections.

Visibility condition options:

- **`Device.HasBluetooth` alone** *(product decision 2026-08-30; superseded for headsets
  2026-09-02)*: Bluetooth mode as uniformly read-only UI for every Bluetooth device. Deliberate
  consequence, decided with eyes open at the time: T-Racer's and Cosmo's Bluetooth transports
  were catalog-configurable (no `SupportsConfiguration: false`), yet they still showed the banner
  and locked their panels — uniform product behaviour won over per-transport capability. Cosmo's
  audio controls act on the OS endpoint and would have worked over Bluetooth; they locked anyway,
  which is exactly what the 2026-09-02 ticket reversed.
- **`Device.IsBluetoothConfigLocked` = `HasBluetooth && !SupportsConfiguration`** *(current —
  resurrected 2026-09-02)* — interim implementation on 2026-08-30, same day superseded by the
  product decision above and removed, then brought back by
  `docs/plans/remove-headset-bluetooth-lock/` (the computed property, its notifying
  `SupportsConfiguration` conversion, and `DeviceCardBluetoothConfigLockTests` all exist again).
  Keyboards/mice lock via `SupportsConfiguration: false` on their Bluetooth catalog rows; TAIPAN
  on its 2.4G dongle remains banner-free either way: unconfigurable but not Bluetooth.

## Recommended approach

Option 2. Condition since 2026-09-02: `Device.IsBluetoothConfigLocked` (was
`Device.HasBluetooth` alone from 2026-08-30 to 2026-09-02).

New view `src/ProoveGaming.App/Views/DeviceDetails/Sections/BluetoothModeBannerView.axaml`
(+ trivial code-behind), `x:DataType="viewmodels:DeviceDetailsViewModel"` like its sibling
section views:

- Root element inside the control: a `Grid`/`Panel` with
  `IsVisible="{Binding Device.IsBluetoothConfigLocked, FallbackValue=False}"` (was
  `Device.HasBluetooth` until 2026-09-02).
  **`FallbackValue=False` is required**: while `Device` is null the binding would otherwise leave
  `IsVisible` at its default `true`.
- Background: a `Rectangle` with `Fill="#6357FF" Opacity="0.1"` (fill and fill-opacity mapped
  separately per the SVG-transcription convention), stretching under the content.
- Row 1: warning-triangle icon — transcribed from the provided SVG as a `Path` with the raw data
  (`M12.834 25.4993L22.0007 9.66602L31.1673 25.4993H12.834Z…` including the exclamation bar/dot
  subpaths), `Fill="#6357FF"`, hosted in a small `Canvas`/`Viewbox` normalised to the glyph's
  ~18×16 box — followed by the title `TextBlock`:
  `Text="{DynamicResource DeviceDetails_BluetoothMode_Title}"`, `Foreground="#6357FF"`,
  `FontFamily="{DynamicResource BaseAppFont}"`, `FontSize="14"`, `FontWeight="SemiBold"`.
  Emphasis is not colour-only: icon + uppercase + weight carry it too.
- Row 2: body `TextBlock`:
  `Text="{DynamicResource DeviceDetails_BluetoothMode_Message}"`, `Foreground="#B2BEBCEA"`
  (= design's `#BEBCEA` at 70 %, the codebase's standing idiom for this colour),
  `FontSize="14"`, `LineHeight="20"`, `TextWrapping="Wrap"`.
- Sizing per design: content padding ≈ 12–13 px (icon/body left edge at x≈13 in the SVG),
  `MinHeight="84"`, `MaxWidth="628"` on the control root (the SVG's width — it caps and centers
  on wide panels); **no fixed `Width`/`Height`** so the uk copy can wrap taller
  (localization-safe sizing rule).
- The control is `IsHitTestVisible="False"` overall (it is informational and overlays the preview
  panel, like the chrome canvases around it).

Call sites in `src/ProoveGaming.App/Views/DeviceDetails/DeviceDetailsView.axaml` — add
`<detailSections:BluetoothModeBannerView VerticalAlignment="Top" Margin="82,40,82,0" />` (82 =
42 px chrome rail + the design's 40 px content inset; a plain 40 rendered near-full-bleed and was
corrected against the mock on 2026-08-30) to the four preview panels that already host
`ConnectivityBarView`:

1. Mouse stack-nav preview panel (Grid.Column 2, `IsStackNavLayout`, ~line 560).
2. Keyboard preview panel (Grid.Column 2, `IsKeyboardDevice`, ~line 638).
3. Buttons-section mouse panel (the `ButtonsBackground3Margin` panel hosting
   `DeviceImageWithHotspotsView`, ~line 457).
4. Keyboard Key-Assignments merged panel (~line 495). **Collision note:** this panel already
   floats the `KeyAssignmentReadFailed` warning banner at `Margin="42,30,42,0"`. Over Bluetooth a
   matrix read always fails, and the two banners said the same thing twice — so the read-failed
   warning is **suppressed while the transport is Bluetooth** (`KeyAssignmentReadFailed AND NOT
   Device.HasBluetooth` via `MultiBinding`/`BoolConverters.And`, with `FallbackValue=True` on the
   negated leg so a null `Device` keeps the pre-Bluetooth behaviour). Its "reconnect it" advice is
   also wrong over Bluetooth — only a cable/dongle helps, which is what the Bluetooth banner says.
   Both banners still share a top-aligned vertical `StackPanel` (each keeping its own margin) so
   the mutual exclusion can never regress into an overlap. (Decided 2026-08-30 after the first
   cut showed both banners together.)

Localization — both languages in the same change:

| Key | en | uk |
| --- | --- | --- |
| `DeviceDetails_BluetoothMode_Title` | `BLUETOOTH MODE` | `РЕЖИМ BLUETOOTH` |
| `DeviceDetails_BluetoothMode_Message` | `Device settings are unavailable while connected via Bluetooth. Connect your device using a wireless dongle or USB cable to make changes.` | `Налаштування пристрою недоступні під час підключення через Bluetooth. Підключіть пристрій через бездротовий адаптер або USB-кабель, щоб внести зміни.` |

The en message carries a deliberate `&#x0A;` before "a wireless dongle" — the design's line break,
requested explicitly (2026-08-30). On panels narrower than the 628 px cap the first line wraps
again and the banner grows below its 84 px minimum; accepted (PR #124 m4 flagged the fragility —
kept by design decision). The uk string wraps naturally: it is ~15 % longer and has no mock-driven
break of its own.

## File-level change list

| File | Action | Intent |
| --- | --- | --- |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/BluetoothModeBannerView.axaml` | create | The banner markup: 10 %-opacity `#6357FF` backdrop rectangle, warning-triangle `Path`, title + body `TextBlock`s bound to the two new resource keys; root gated on `Device.IsBluetoothConfigLocked` (was `Device.HasBluetooth` until 2026-09-02) with `FallbackValue=False`. |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/BluetoothModeBannerView.axaml.cs` | create | Trivial `UserControl` code-behind (`InitializeComponent`), mirroring `ConnectivityBarView.axaml.cs`. |
| `src/ProoveGaming.App/Views/DeviceDetails/DeviceDetailsView.axaml` | edit | Add the banner (VerticalAlignment Top, `Margin="82,40,82,0"`) to the four preview panels; in the Key-Assignments panel, stack it with the existing `KeyAssignmentReadFailed` banner so they cannot overlap. Also locks the header preset controls. |
| `src/ProoveGaming.App/Resources/Strings.en.axaml` | edit | Add `DeviceDetails_BluetoothMode_Title` and `DeviceDetails_BluetoothMode_Message`. |
| `src/ProoveGaming.App/Resources/Strings.uk.axaml` | edit | Add the same two keys with the Ukrainian copy. |
| `tests/ProoveGaming.App.Tests/BluetoothModeBannerResourcesTests.cs` | create | Asserts both resource files contain the two new keys (parity test only catches one-sided omissions; this pins presence). |
| `docs/CONTEXT.md` | edit | Add a one-line glossary entry for **Bluetooth mode banner**. |
| `src/ProoveGaming.App/App.axaml` | edit | Shared `:is(Control).bluetoothLocked:disabled` style (Opacity 0.5) used by every section panel; plus a `sectionActionButton:disabled` presenter rule (no fill, 10% outline, 30% label) — without it the theme's dark disabled resources melt the RANDOM LIGHTING / Recommendations / Default-settings buttons into the panel so they read as hidden. *(Added 2026-08-30/31.)* |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/RgbLightingSectionView.axaml` | edit | Disable the section content (`IsEnabled` on the content StackPanel) in Bluetooth mode, with the mock's finer per-control dimming (title stays bright). *(Added 2026-08-30.)* |
| `src/ProoveGaming.App/Views/DeviceDetails/Sections/{Presets,Buttons,Macros,Dpi,Settings,Audio,Actuation,Firmware}SectionView.axaml` | edit | Each section's content root gets `IsEnabled="{Binding !Device.IsBluetoothConfigLocked, FallbackValue=True}"` (was `!Device.HasBluetooth` until 2026-09-02); the `bluetoothLocked` dim class sits on the dimmable blocks only, with styled buttons OUTSIDE the opacity subtree so their absolute `:disabled` colours are not halved (restructured 2026-08-31 per PR #124 M1). Audio/Presets/Buttons keep the class on the root (no styled buttons inside); Actuation carries only `IsEnabled` (wired-only boards, PR #124 m8); Firmware's button reuses `sectionActionButton` + a fill-only variant class (PR #124 M2). |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs` | edit | Subscribe to the card's `PropertyChanged` in the `Device` setter; on a Bluetooth → live-cable/dongle switch re-run `RefreshFromDeviceAsync` so the key matrix is re-read in place (the read-only-transport banner branch starts no self-heal loop, so nothing else recovers it without reopening the page). Round 2 same day: the hook is armed-while-on-Bluetooth and fires on ANY transport-flag event, because `ApplyEntryToCard` drops `HasBluetooth` before raising `HasWire` (a flip-keyed hook saw "disconnected" and skipped); the read is also `Task.Yield`-deferred past the re-stamp batch, since the routing VID/PID land only in `ApplyDeviceIdentity` after the transports. Test seam `TransportSwitchRefreshTask`. *(Added 2026-08-31.)* |
| `tests/ProoveGaming.App.Tests/DeviceDetailsRampageKeyAssignmentTests.cs` | edit | Six transport-recovery tests: BT→USB switch re-reads the matrix and clears the banner in place (both event orders, incl. the real `ApplyEntryToCard` order); a BT drop with nothing plugged does not refresh; two-step recovery (BT drop, cable later); mid-session re-arm (cable→BT→cable); no refresh after page exit. *(Added 2026-08-31.)* |

## Acceptance criteria

- [ ] Given a device whose active transport is a Bluetooth link that cannot carry configuration
  (`IsBluetoothConfigLocked` true — e.g. TAIPAN paired over Bluetooth with no cable; since
  2026-09-02 headsets never satisfy this), when its details page is opened on any section that
  shows the device preview (Presets, RGB, Macros, DPI, Settings, Buttons, Key Assignments,
  Firmware), then the banner is visible at the top of the preview panel — 40 px below the panel
  top, 40 px inside the 42 px side rails (82 px from the panel edges), no wider than 628 px.
- [ ] Given a device connected by cable or 2.4 G dongle (Bluetooth not the active transport), when
  its details page is opened, then the banner is not visible and every section is interactive —
  including TAIPAN on its 2.4G dongle, whose transport is unconfigurable but not Bluetooth.
- [ ] Given any KEYBOARD or MOUSE on Bluetooth (Rampage, TAIPAN, T-Racer — *narrowed 2026-09-02:
  headsets no longer lock*), when any section is opened (Presets, RGB, Buttons/Key Assignments,
  Macros, DPI, Settings, Audio, Actuation, Firmware), then the section content is dimmed to half
  opacity and does not react to clicks, while its list can still be scrolled.
- [ ] Given a device on Bluetooth, the header's Current Preset dropdown, SAVE PRESET and
  NEW PRESET are visible but dimmed and non-interactive (dropdown does not open); the back
  button still leaves the page. Every disabled button remains clearly visible — none melts into
  the background.
- [ ] Given the RGB Lighting section on a Bluetooth device, the "Lighting Effect" title stays
  full-opacity while the toggle, RANDOM LIGHTING and effect rows are dimmed, per the mock.
- [ ] Given a locked device's details page open over Bluetooth, when a USB cable is plugged in
  (transport switches, `IsBluetoothConfigLocked` flips false), then the banner disappears without
  reopening the page — and reappears when the cable is pulled and the Bluetooth link resumes.
- [ ] Given the Key Assignments section open over Bluetooth (fallback banner state), when a USB
  cable is plugged in, then WITHOUT leaving the page the amber read-failed warning clears, the
  live matrix is re-read, and key remapping works; a Bluetooth drop with nothing plugged does
  not trigger a re-read.
- [ ] Given the app language set to Ukrainian, when the banner is shown, then both title and body
  render the uk strings, the body wraps, and no text is clipped by the banner bounds.
- [ ] Given a Bluetooth keyboard whose key-matrix read failed, when the Key Assignments section is
  open, then only the Bluetooth banner is shown — the amber read-failed warning is suppressed
  while the transport is Bluetooth, and reappears for a failed read on cable/dongle.
- [ ] Given no device set yet (`Device` null during navigation), the banner is not visible
  (FallbackValue) and no binding error crashes the page.
- [ ] `LanguageResourceParityTests` stays green (keys added to both languages).

## Test strategy

New feature (`add-*`) — required coverage in `tests/ProoveGaming.App.Tests`:

- **`BluetoothModeBannerResourcesTests`** (new, non-negotiable): both `Strings.en.axaml` and
  `Strings.uk.axaml` contain `DeviceDetails_BluetoothMode_Title` and
  `DeviceDetails_BluetoothMode_Message`, with non-empty values. Mirror the file-parsing style of
  `LanguageResourceParityTests`.
- `DeviceCardBluetoothConfigLockTests` — removed 2026-08-30 with the compound condition,
  **resurrected 2026-09-02** when `IsBluetoothConfigLocked` returned (see
  `docs/plans/remove-headset-bluetooth-lock/`).
- **Existing suites that must stay green:** `LanguageResourceParityTests` (en/uk parity),
  `HomeViewModelTransportPriorityTests` (the `HasBluetooth` display-flag semantics the banner
  relies on — no changes expected, they pin the contract).

Deliberately **not** covered by automated tests: the banner's visual placement, margins, wrapping,
and the XAML-only show/hide and section-dimming bindings (no Avalonia headless harness in the
repo) — exercised by the manual QA steps instead. The *recovery* half of the transport switch IS
covered: six VM-level tests in `DeviceDetailsRampageKeyAssignmentTests` (2026-08-31).

## QA (manual test steps)

1. Pair a Bluetooth-capable device (e.g. the TAIPAN mouse) with Windows over Bluetooth. Do not
   plug in its cable or dongle. Launch the Proove Gaming app.
   **Expected:** the device card shows the Bluetooth icon.
2. Open the device's details page.
   **Expected:** a purple-tinted banner appears at the top of the device-image panel:
   "BLUETOOTH MODE" title with a warning-triangle icon, and the message "Device settings are
   unavailable while connected via Bluetooth. Connect your device using a wireless dongle or USB
   cable to make changes." It sits 40 px below the panel's top edge, clearly inside the panel's
   darker side rails (not touching them), and is about as wide as the device image below it.
3. Click through every left-nav section (Presets, RGB Lighting, Buttons/Key Assignments, Macros,
   DPI, Settings, Firmware).
   **Expected:** the banner stays visible above the device image on every section; nothing
   overlaps it.
4. While the details page is open, connect the device with its USB cable.
   **Expected:** the banner disappears by itself within a few seconds; no restart or re-navigation
   needed.
4a. (Keyboard, e.g. Rampage) Open Key Assignments while on Bluetooth, then plug in the USB cable
   and stay on the page.
   **Expected:** within a few seconds the yellow "could not read layout" warning does NOT appear
   (or disappears), the keyboard picture shows the real current layout, and clicking a key +
   assigning works — without going back to Home and reopening the device.
5. Unplug the cable (Bluetooth link resumes).
   **Expected:** the banner comes back by itself.
6. Connect a device via 2.4 G dongle only and open its details page.
   **Expected:** no Bluetooth banner (including the TAIPAN mouse on its dongle).
6a. Connect any keyboard or mouse over Bluetooth (Rampage, T-Racer, TAIPAN — since 2026-09-02
   headsets show no banner and no dimming) and walk every left-nav section.
   **Expected:** each section's content is greyed to half opacity and does not react to clicks
   (buttons, sliders, toggles, list rows); long lists can still be scrolled. On RGB Lighting the
   "Lighting Effect" title stays bright. After connecting a cable/dongle, all sections are
   interactive again without reopening the page.
7. Switch the app language to Ukrainian (Profile → language) and repeat step 2.
   **Expected:** the banner shows "РЕЖИМ BLUETOOTH" and the Ukrainian message; the text wraps
   inside the banner and nothing is cut off.
8. (Keyboard with Bluetooth, e.g. Rampage over BT) Open Key Assignments while on Bluetooth.
   **Expected:** only the purple Bluetooth banner is shown; the yellow "could not read layout"
   warning does not appear. Then connect the keyboard by cable and unplug any working link so a
   read failure can occur (or reopen the page on a cable link where the read fails).
   **Expected:** on a non-Bluetooth link the yellow warning behaves as before.

## Review focus

- `localization` — both languages ship in the same change; no fixed widths on localized text.
- `null-safety` — `Device` can be null during navigation; `FallbackValue=False` on the visibility
  binding.

## Performance & quality

The banner and section locking are static XAML bound to existing observable properties — no
timers, no per-frame work, no allocations on a hot path. The transport-switch recovery hook
(2026-08-31) is the one piece that runs off the UI thread: the card's `PropertyChanged` fires on
the gRPC stream thread, so `RefreshFromDeviceAsync` executes on the thread pool there — scalar
INPC updates are marshalled by Avalonia's binding layer (the same convention `HomeViewModel`'s
card re-stamps rely on), the `PhysicalKeys` iteration snapshots the collection first, and a lost
identity race is absorbed by the slot-read self-heal. One refresh per switch, guarded against
re-entry; no sustained background work.

## Risks

- **Overlap with existing floating banners.** The Key-Assignments panel already floats the
  read-failed warning; the plan stacks them explicitly. Watch for any other absolutely-positioned
  overlay (remap toast is top-*right* and transient — acceptable).
- **`IsVisible` default-true on broken binding.** Forgetting `FallbackValue=False` would flash the
  banner on pages with no device; called out in the change list and acceptance criteria.
- **Panel-width variance.** The banner stretches between its 82-px side margins up to the 628-px
  cap, then centers; on narrow windows it shrinks with the panel and never crosses the 42-px
  chrome rails. (First cut used 40-px margins from the panel edge — that rendered near-full-bleed
  over the rails and was corrected after pixel-measuring the mock, 2026-08-30.)
- **Firmware section** shows the banner too; if firmware update *is* supported over Bluetooth for
  some device, the copy ("device settings are unavailable") is still accurate but slightly
  adjacent — see Open questions.

## Open questions

1. ~~Should the banner show for **all** Bluetooth-connected devices, or only when the catalog
   entry also declares `ConfigurationUnsupported`?~~ **Resolved 2026-08-30 (twice, finally by
   product decision): ALL Bluetooth devices** — uniform read-only Bluetooth mode, even for
   T-Racer/Cosmo whose Bluetooth transports are catalog-configurable. *Superseded 2026-09-02:
   reversed for headsets — the banner is now catalog-driven (`IsBluetoothConfigLocked`) and
   T-Racer's Bluetooth row gained `SupportsConfiguration: false` to keep the keyboard locked;
   see `docs/plans/remove-headset-bluetooth-lock/`.*
2. ~~Should the Firmware section be exempt?~~ **Resolved 2026-08-30:** no exemption — Firmware is
   disabled in Bluetooth mode like every other section.
3. ~~Should the other write-capable sections get the same disabled treatment as RGB Lighting?~~
   **Resolved 2026-08-30:** yes — every section panel locks in Bluetooth mode.
4. The keyboard key-overlay and mouse hotspots on the *preview* panel remain clickable over
   Bluetooth (their remap dialogs' writes are separately guarded); only the section panels lock.
   If design wants those locked too, that is a follow-up.

## Review findings

### Round 1 — Ship: yes

#### Plan conformance
Plan: `docs/plans/add-bluetooth-mode-banner/add-bluetooth-mode-banner-plan.md` (read in full).
- Implemented as planned: banner view + code-behind, four call sites at `Margin="82,40,82,0"`,
  en/uk resources (+ hard break in en per design), read-failed suppression via MultiBinding,
  section locking on all nine section views + shared `bluetoothLocked` style, header preset
  controls locked, disabled-button presenter re-states, BT→USB in-place recovery hook + seam,
  resource-key tests + three transport-switch tests, glossary entry.
- Deviates from plan: none in code. The plan itself has stale spots (S3 below) — the **Out**
  section still declares "Disabling/greying the actual controls" out of scope while the **In**
  section (correctly) includes it; the File-level change list row for `DeviceDetailsView.axaml`
  still says `Margin="40,40,40,0"`; **Performance & quality** still claims "no new threads /
  UI thread only" (the recovery refresh now runs off the gRPC event thread); **Test strategy**
  still lists "live show/hide on transport switch" as deliberately uncovered though three tests
  now cover the recovery half.
- Missing / partial: none.
- Accepted limitation (per plan): Cosmo's audio volume/mic lock over Bluetooth even though the
  OS-endpoint path works (Options considered / product decision) — residual risk: a Cosmo-BT
  user loses working volume control until they switch transports. Keyboard overlay / mouse
  hotspots stay clickable over Bluetooth (Open question 4) — residual risk: a remap attempted
  over BT on a catalog-configurable transport (T-Racer) still goes the old silent-guard path.

#### Code
- none (no Blockers)
- [x] **S1. `Task.Yield` ordering comment overstates its guarantee** —
  `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs:1102`
  (`RefreshAfterTransportRestampAsync`) — Should-fix
  **Problem:** The comment claims yielding "queues the read behind the in-flight re-stamp".
  That ordering only exists on a dispatcher/sync-context thread. `DeviceFound` is raised on the
  gRPC stream-reader thread (`UsbHidDeviceService.ReadServerResponses`,
  `src/ProoveGaming.App/Services/UsbHidDeviceService.cs:808`), where `Task.Yield` re-queues to
  the **thread pool** — the continuation can run on another core *in parallel with*
  `ApplyDeviceIdentity` (`HomeViewModel.cs:1330`), i.e. before the wired VID/PID land.
  **Why it matters:** lose the race and the first re-read targets the Bluetooth identity and
  fails; recovery then rides the slot-read self-heal (~1 s quick retry) because
  `SupportsConfiguration` is already true — so the user sees the amber banner ~1–3 s longer,
  not a broken state. Bounded, self-healing, and QA-verified working — but the comment
  documents a guarantee the code does not have, which this codebase treats as load-bearing.
  **Solution options:** A (honest + robust): reword the comment to "best-effort ordering;
  a lost race is absorbed by the slot-read self-heal", and optionally add one bounded re-try
  inside the refresh when the first read fails. B (structural): route the refresh through a
  `PostToUi`-style seam (see `HomeViewModel.PostToUi`, `HomeViewModel.cs:935`) so it serialises
  with UI-thread work — note this still does not order against the gRPC thread's re-stamp, so
  A's comment correction is needed either way.
- [x] **S2. Recovery refresh runs `RefreshFromDeviceAsync` off the UI thread** —
  `DeviceDetailsViewModel.cs:1085` (`OnDeviceCardTransportChanged`) → `KeyAssignments.cs:1133`
  (`ProbeAndUpdateMatrixMapAsync`) — Should-fix
  **Problem:** From `SetDeviceAsync` this method ran with UI-context continuations; the new
  hook runs it entirely on the gRPC/thread-pool context. Scalar INPC updates
  (`KeyAssignmentReadFailed`, `IsSelected` in `SyncAssignmentTargetSelection`) are marshalled
  by Avalonia's binding layer and match how `HomeViewModel` already mutates bound card
  properties on this same thread — but `ProbeAndUpdateMatrixMapAsync` also **enumerates
  `PhysicalKeys`** (an `ObservableCollection` the UI thread rebuilds in
  `LoadKeyLayoutForDevice`) with no synchronisation.
  **Failure scenario:** user switches section/preset (UI thread runs
  `ResetKeyLayoutState`/`PopulateKeyLayout`) at the exact moment the background refresh is
  iterating → `InvalidOperationException` (collection modified) → swallowed by `Quietly`
  (logged, refresh lost; the new device-open chain re-reads anyway). Low probability,
  self-limiting, no crash — hence Should-fix, not Blocker.
  **Fix:** marshal the refresh body to the UI thread via a seam that tests can inline
  (mirror `HomeViewModel.PostToUi`, `HomeViewModel.cs:926-935` — do not call
  `Dispatcher.UIThread` raw, per that comment's test-affinity rationale), or snapshot
  `PhysicalKeys.ToList()` before iterating in `ProbeAndUpdateMatrixMapAsync`.
- [x] **S3. Plan document staleness** — `docs/plans/add-bluetooth-mode-banner/…-plan.md:44-46,
  145, 258-262, 206-208` — Should-fix (docs-only; per CLAUDE.md run nothing)
  **Problem / why it matters:** four spots contradict the shipped behaviour (listed under Plan
  conformance above). The plan is the contract later rounds review against; the Out-section
  contradiction in particular says the opposite of the In-section.
  **Fix:** amend the Out bullet ("display-only" claim), correct the change-list margin to
  `82,40,82,0`, rewrite Performance & quality (recovery refresh runs off-UI; still no timers /
  hot paths), and trim the Test-strategy "not covered" line to visual placement only.
- [ ] **S4. TAIPAN-on-dongle inconsistency now user-visible** — behaviour gap, no single file —
  Should-fix (follow-up candidate, product call)
  **Problem / why it matters:** TAIPAN on its 2.4G receiver is equally unconfigurable
  (`SupportsConfiguration: false`) but shows **no** banner and **no** dimming — its controls
  look live and silently do nothing, which is exactly the state this feature eliminated for
  Bluetooth. Pre-existing behaviour, out of the user's request, but the contrast is now
  visible product inconsistency.
  **Fix:** product decision needed — either a transport-neutral "read-only link" banner keyed
  on `!SupportsConfiguration` with different copy, or an explicit note in the plan accepting
  the gap. Raise with design; do not silently extend scope.

#### Tests
- [x] Coverage is good for the recovery hook (both event orders + the disconnect non-trigger)
  and resource keys. Remaining gap (minor): nothing pins that the **read-failed suppression**
  MultiBinding keeps the amber banner hidden over Bluetooth — it is XAML-only, consistent with
  the declared strategy; cover via QA step 8. No action required unless regressions appear.
  *(/test-writer 2026-08-31: confirmed no Avalonia headless harness exists, so the XAML
  suppression stays on QA as declared. Added three further hook tests for behaviours the code
  comments claim but nothing pinned: two-step recovery (BT drop, cable seconds later),
  mid-session re-arm (cable → BT → cable), and no-refresh after page exit/unsubscribe —
  18/18 green.)*
- Evidence: full `ProoveGaming.App.Tests` run in **Release** (CI configuration): 1429/1429
  passed (2026-08-31). Scoped App build: 0 errors, warnings pre-existing.

#### Nits
- `_bluetoothRecoveryArmed` is written from both the UI thread (Device setter) and the gRPC
  event thread (handler) without synchronisation — benign for a bool (worst case one missed or
  extra refresh, absorbed by self-heal), but worth a one-line comment —
  `DeviceDetailsViewModel.cs:1083`.
- A second transport flip while a refresh is in flight overwrites `TransportSwitchRefreshTask`
  and lets two `RefreshFromDeviceAsync` runs overlap (duplicate matrix read / macro upload;
  device-side no-op) — `DeviceDetailsViewModel.cs:1097`.
- Test comment in `BluetoothToUsbSwitch_RereadsTheMatrixAndClearsTheBanner`
  ("capability re-stamped first, the display transport flags after") describes
  `ApplyEntryToCard`'s order but the test body sets `HasWire` *before* `HasBluetooth` — the
  real-order coverage lives in the second test; reword to "reverse order variant" —
  `tests/ProoveGaming.App.Tests/DeviceDetailsRampageKeyAssignmentTests.cs:330`.
- The banner view carries two stacked top-level comments (visibility + width cap) that could
  fold into one — `BluetoothModeBannerView.axaml:12-23`.

#### What's done well
- The Fluent `:disabled` presenter re-states (section buttons, macro add, preset dropdown) fix
  a whole class of "disabled button melts into the panel" bugs and are documented at each site
  with the shared-trap cross-reference — do not undo these when restyling.
- Moving the macro-add button's hardcoded content colours into styles
  (`MacrosSectionView.axaml:35-41`) is the correct Avalonia precedence fix, not a workaround.
- The armed/disarmed transport-recovery hook is order-independent, covers the two-step
  BT-drop-then-cable case, guards `ReferenceEquals(card, _device)` against stale cards, and
  ships with a test that reproduces the exact production event order that broke round 1.
- Localization discipline: both languages in one change, no fixed widths on localized text,
  explicit key-presence tests on top of the parity net.

#### Verdict
Ship: yes — no blockers; the two code Should-fixes are bounded, self-healing races with
worked-out fixes, and S3/S4 are documentation and product-decision follow-ups. First:
1. S3 (plan staleness — cheap, keeps the contract honest for the next round).
2. S1+S2 together (one comment rewrite + one marshal/snapshot in the same method family).
3. S4 (raise the TAIPAN-dongle banner question with design).

### Round 2 — external review, PR #124 comments (fixes applied 2026-08-31)

An external adversarial review landed as a PR #124 comment (2 Major, 8 minor). Resolution, by
its numbering — full problem statements live in the PR thread:

- [x] **M1 (double-dimming of absolute-coloured buttons under the panel `Opacity 0.5`)** —
  fixed via its Option B: the `bluetoothLocked` class moved off the section content roots onto
  the dimmable blocks only, so styled buttons (Recommendations, Default Settings, NEW MACRO,
  Check-for-updates) render their mock-absolute `:disabled` colours undimmed. `IsEnabled` stays
  on each root. Audio/Presets/Buttons keep the root class (no styled buttons inside); the shared
  style's comment now states the invariant.
- [x] **M2 (`firmwareButton` missing the `:disabled` presenter re-state)** — fixed via its
  Option B: the button is now `Classes="sectionActionButton firmwareButton"`, the local style
  reduced to the fill + hover difference; the `:disabled` re-state is inherited from the shared
  style.
- [x] **m1** — `RefreshAfterTransportRestampAsync` comment rewritten to best-effort ordering +
  self-heal absorption (matches Round 1 S1).
- [x] **m2** — `PhysicalKeys` iteration snapshots with `.ToList()` (matches Round 1 S2).
- [x] **m3** — the four stale plan spots corrected (matches Round 1 S3). Note: the PR review
  correctly observed the Round 1 journal referenced `HomeViewModel.PostToUi` — that seam does
  not exist in this repo's `HomeViewModel`; the S2 fix used the snapshot option instead.
- [x] **m5** — re-entry guard: no new refresh while `TransportSwitchRefreshTask` is in flight;
  the armed flag survives the skip so the next transport event retries.
- [x] **m6** — cross-thread write of `_bluetoothRecoveryArmed` documented as a deliberate,
  bounded race.
- [x] **m7** — test comment reworded to "reverse-order variant".
- [x] **m8** — `bluetoothLocked` removed from Actuation (wired-only boards; avoids future 0.4 ×
  0.5 multiplication), and a `sectionModeButton:disabled` presenter re-state added to App.axaml
  so a future Bluetooth actuation board cannot reproduce M2.
- [ ] **m4** — *kept, not fixed:* the en hard break is the design's explicit line break
  (requested 2026-08-30); fragility on sub-628 px panels accepted and now documented next to
  the localization table. Revisit only if design supplies a uk break.
