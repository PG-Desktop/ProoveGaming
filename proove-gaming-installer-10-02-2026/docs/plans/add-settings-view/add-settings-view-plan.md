# Add Settings view (gear icon → application settings page)

## Goal

Give the App a real Settings page, opened from the gear icon in the Home header (currently an
inert placeholder), rendered in the main content region with the app's bevelled black-panel
design language. The page's single working control for now is the **Application Language**
dropdown (English / Українська): selecting a language switches the running UI live through the
existing `LocalizationManager` and persists the choice to the same registry preference the
Installer and Uninstaller already share, so all three executables stay in sync.

## Scope

**In:**

- New `SettingsView` in `ProoveGaming.App`, shown/hidden by `MainWindow`'s existing
  `AppStep` + `UpdateStepUi()` navigation, with a ProfileView-style back button returning Home.
- Gear button (`SettingsButton` in `HomeView.axaml`) becomes functional (cursor, click, event).
- Language dropdown reusing the shared `ProoveGaming.Common.Controls.LanguageSelectView`
  (the same control the Installer's Welcome step uses), wired to the App `LocalizationManager`.
- Language persistence via the existing `LanguagePreferenceStore` (registry
  `HKCU\Software\ProoveGaming\Settings`, value `AppLanguage`) — already wired inside
  `LocalizationManager.SetLanguage`; restore-on-startup extended to DEBUG builds.
- Endonym display names in the shared dropdown ("Ukrainian" → "Українська").
- New localized strings (`SettingsView_Title`, `SettingsView_LanguageLabel`) in en/uk.
- xUnit tests for the static language list and the language-code normalization rule.

**Out (explicit non-goals):**

- **Color Theme section — omitted entirely for now.** The mock shows three theme cards
  ("Follow OS Theme" / "Light Theme" / "Dark Theme"); the user has stated theme selection is
  not needed yet. Rendering the cards statically would need three preview bitmap assets we do
  not have and would advertise a capability that does nothing, so the section is not built at
  all. The decoded card geometry is preserved below (§ "Decoded design spec") so a future
  `add-theme-selection` plan can transcribe it without the SVG.
- Search / Help header buttons — remain placeholders.
- Any additional settings (startup behaviour, tray options, …).
- No theme infrastructure, no restart-to-apply flow.

## Findings from the codebase (what already exists — do not reinvent)

These determine the whole shape of the change; `/coder` should read them first:

1. **Language dropdown control already exists and is shared.**
   `src/ProoveGaming.Common/Controls/LanguageSelectView.axaml(.cs)` — 600×56 bordered box,
   1px `#6357FF` stroke, flag + name + chevron header, dark dropdown overlay with hover and
   check mark, `SetSelectedLanguage(code)` + `LanguageSelected` event. The Installer's
   Welcome step uses it exactly this way (`src/ProoveGaming.Installer/Views/WelcomeStepView.axaml.cs`):
   `SetSelectedLanguage(LocalizationManager.CurrentLanguageCode)` then
   `LanguageSelected += code => LocalizationManager.SetLanguage(code)`. The mock's dropdown
   (599×55 box, 1px `#6357FF`, flag 24×16, chevron `#BEBCEA`) is this control to within a pixel.
   Flag assets exist: `src/ProoveGaming.Common/Assets/Icons/icon-american.png` / `icon-ukraine.png`.
2. **Persistence already exists and is already shared.**
   `src/ProoveGaming.Common/Localization/LanguagePreferenceStore.cs` reads/writes
   `HKCU\Software\ProoveGaming\Settings\AppLanguage`; `LocalizationConstants` holds the `en`/`uk`
   codes. The Installer writes it on language pick, the Uninstaller reads it, and the **App's**
   `LocalizationManager.SetLanguage` (`src/ProoveGaming.App/LocalizationManager.cs`) already
   persists through it on every change, and `InitializeFromSystemCulture` restores it — **in
   Release builds only** (the `#if DEBUG` branch is an empty scaffold). Nothing new is invented;
   constants are already centralized per the repo rule.
3. **Live language switch already works.** `LocalizationManager.SetLanguage` swaps the merged
   `ResourceInclude` (`Resources/Strings.en.axaml` / `Strings.uk.axaml`) at runtime; every view
   binds text via `{DynamicResource}` so the whole UI re-renders immediately, and the
   `LanguageChanged` event covers the non-DynamicResource consumers (tray menu, ProfileViewModel,
   DeviceDetailsViewModel, and — since the Round 1 B1 fix, 2026-08-21 — HomeViewModel's greeting,
   card labels/tooltips and notification date headers). **The switch is live — no restart.** `Strings.en.axaml` is merged in
   `App.axaml` as the baseline, which is why `SetLanguage`'s early-return on `en` at startup is safe.
4. **Navigation pattern.** `MainWindow.axaml` hosts all page views as named siblings
   (`HomeView`, `ProfileView`, `DeviceDetailsView`) toggled by `IsVisible`;
   `MainWindow.axaml.cs` keeps a private `enum AppStep`, and `UpdateStepUi()` +
   `GetLayoutProfile(step)` apply visibility and margins. Views raise plain C# events from
   code-behind (`ProfileRequested`, `BackRequested`) that `MainWindow` subscribes in the ctor
   and unsubscribes in `OnClosed`. Settings must join this pattern, not invent a new one.
5. **The frame is per-view, not host-level.** ProfileView and DeviceDetailsView each draw their
   own bevelled rails as explicit Canvas/Path/Rectangle. There is **no** shared frame at the
   MainWindow level for these pages, so SettingsView draws its own rails from the decoded mock
   geometry below (per the repo's SVG→Canvas rule). The mock's outer 24px offsets are
   `MainWindow`'s `RootLayoutGrid` margin, not part of the view.
6. **Back affordance.** ProfileView's header row (80px): `NavBackButton` (56×56
   `CutCornerFrameGeometry` hover plate + white arrow path) + page title in `AppPixelFont` 36.
   The mock crop shows no header, but back navigation is a hard requirement — Settings reuses
   the ProfileView header pattern verbatim (deviation from the crop, resolved in favour of the
   app-wide pattern).
7. **Gear button today.** `HomeView.axaml` `SettingsButton` has no `Click`, no `Command`, no
   `Cursor="Hand"` — deliberately, per the comment above `HelpButton` ("like SearchButton and
   SettingsButton this is still a placeholder… omit the hand cursor so the header only advertises
   clickability where something actually happens"). That comment must be reworded when the gear
   becomes functional.

## Decoded design spec (source of truth — the SVG is not available to /coder)

Figma frame 1440×804. All mock coordinates below are in that frame; the view-local transcription
already subtracts the host's 24px `RootLayoutGrid` margin (mock x−24 / y−24), so paths can be
pasted into the view as written.

### Panel + rails (Black, Opacity 0.7 — mock `fill="black" fill-opacity="0.7"`, mapped per repo rule as `Fill="Black" Opacity="0.7"`, NOT a hex-alpha)

> **2026-08-31 — Superseded geometry (two rounds).** Round 1: an updated mock ("Profile -
> Settings", Desktop – 152) narrowed the panel to **752 wide, centered** (rails included:
> 42 + 668 + 42) instead of spanning the content width. Round 2 (full-frame SVG, 776×804
> artboard): the panel is now **740 tall** with **symmetric 21.65×21.31 chamfers on all
> four corners** — the top-only left chamfer, the right rail's 54px stagger and the
> bottom notched cap below no longer ship. Opacity moved back to **0.8** per the mock's
> `fill-opacity` (superseding the deliberate 0.7). The 600px content column stays centered;
> its top margin is 54 (label top y=78 against panel top y=24 in the mock). The mock also
> shows a **Color Theme** section (Follow OS / Light / Dark cards below the language
> dropdown) — NOT implemented: the app has no theming support, so those cards are a
> separate feature, not part of this frame fix.

Layout container: `Grid ColumnDefinitions="42,*,42"`, `Height="432"`, `VerticalAlignment="Top"`,
spanning the full content width (1392 at the 1440 design window).

**Left rail** (Canvas 42×432, column 0) — mock x=24..66, y=24..456:

| Piece | Mock | View-local path/rect |
|---|---|---|
| Top chamfer (45° cut) | diagonal (24, 45.306) → (45.4766, 24) | `Path Data="M0 21.306 L21.4766 0 L42 0 V21.306 Z"` |
| Body | rect x=24 y=45.31 w=42 h=322.79 | `Rectangle` at (0, 21.306), 42×322.792 |
| Bottom notched cap | `M46.5906 442.597 L46.5906 390.676 L24 368.098 H66 V456 H60 L46.5906 442.597 Z` | `Path Data="M22.5906 418.597 L22.5906 366.676 L0 344.098 H42 V432 H36 L22.5906 418.597 Z"` |

(Same bevel family as DeviceDetailsView's left rail `M22.5906 742.598 …` — this is the house style.)

**Main content panel** (column 1) — mock rect 1308×432 at (66, 24):
`Rectangle Fill="Black" Opacity="0.7"` filling the star column (1308 wide at design width). Content
sits directly on this panel.

**Right rail** (Canvas 42×432, column 2) — mock x=1374..1416; note it deliberately **starts 54px
below the panel top** (staggered design):

| Piece | Mock | View-local path/rect |
|---|---|---|
| Top triangle (45° edge) | `M1374 119.367 L1374 78 L1416 119.357 Z` | `Path Data="M0 95.367 L0 54 L42 95.357 Z"` |
| Body | rect x=1374 y=119.367 w=42 h=315.318 | `Rectangle` at (0, 95.367), 42×315.318 |
| Bottom chamfer | `M1416 434.686 L1394.35 456 H1374 V434.686 Z` | `Path Data="M42 410.686 L20.354 432 H0 V410.686 Z"` |

### Content (inside the panel)

A single 600px-wide column, **horizontally centered in the panel** (mock: content x=420..1020,
panel x=66..1374 — both centered on x=720). Offsets below are from the **panel top**:

1. **"Application Language" section label** — white, regular app base font (Segoe UI), ~18px;
   mock cap-height top y≈70.4 / baseline y≈83 → label top ≈**46** below panel top.
   `{DynamicResource SettingsView_LanguageLabel}`, `FontSize="18"`, `Foreground="White"`. No
   fixed `Width` (localized text).
2. **Language dropdown** — mock box (420.5, 100.5) 599×55, stroke `#6357FF` 1px → top ≈**76**
   below panel top (≈12px gap under the label). This is `commonControls:LanguageSelectView`
   as-is (600×56 incl. stroke). Mock innards, for the record — flag 24×16 rx=2 at 16px inset,
   name 13px after flag (baseline y=135), chevron `M992 131.4 L986 125.4 L987.4 124 L992 128.6
   L996.6 124 L998 125.4 Z` fill `#BEBCEA` opacity 0.7 at ~27px right inset. **Accepted
   deviations** (reuse beats a near-duplicate, per the "second copy" rule): the shared control
   renders the flag PNG 24px wide with `Stretch="Uniform"`, an 8px flag→text gap, and its 24×24
   `Property-down.svg` chevron — identical to the Installer's approved Welcome step.
   - Dropdown rows: flag + name per language; selecting a row switches the app language
     immediately and persists it (all built into the control + `LocalizationManager`).
   - **Language names are endonyms** — shown in their own language regardless of current UI
     language: "English", "Українська". This is the deliberate convention (a user stuck in a
     language they can't read must still find their own), so the names are fixed strings in the
     shared control's `Languages` list, **not** localization resources. Requires the one-word
     change "Ukrainian" → "Українська" in `LanguageSelectView.Languages` (visible in the
     Installer's Welcome step too — flagged under Risks).
3. **Color Theme section** — omitted (see Scope). Preserved geometry for the future plan:
   header "Color Theme" white ~17–18px baseline y≈207 at x=420; three cards at y=224, each
   189.333 wide with 16px gaps (x = 420 / 625.334 / 830.666), card = 120px image preview +
   56px footer strip (White, opacity 0.05) holding an 18×18 radio (rx=9, 2px stroke) at 19px
   left inset (cy=363) and a ~16px white label 17px after the radio. Selected card: 2px
   `#6357FF` border at (421, 225) 187.333×174; selected radio: `#6357FF` stroke + 6px-radius
   `#6357FF` filled inner circle. Unselected radios: stroke `#BEBCEA` at 30% opacity.

### Page header (app pattern, not in the mock crop)

Above the panel, ProfileView's header pattern: `Grid RowDefinitions="80,*"`, row 0 =
`NavBackButton` (56×56, `CutCornerFrameGeometry` hover plate `#4D6357FF`, white back-arrow path
`M5.1 12L12.5667 19.4667L10.6667 21.3333L0 10.6667L10.6667 0L12.5667 1.86667L5.1 9.33333H21.3333V12H5.1Z`)
+ title `{DynamicResource SettingsView_Title}` in `AppPixelFont` 36 / LineHeight 32 / Weight 250,
16px spacing. Panel grid carries `Margin="0,12,0,0"` like ProfileView's frame.

## Options considered

1. **Reuse shared `LanguageSelectView` + per-view frame canvas (recommended).** The dropdown,
   flags, persistence and live switching all exist and are QA-approved in the Installer; the
   Settings view adds only frame geometry, header, navigation and two strings. Trade-off: the
   dropdown's fine metrics (flag gap 8 vs mock 13, chevron asset vs mock path) follow the shared
   control, not the mock — an accepted, stated deviation.
2. **Transcribe the mock's dropdown as a new Settings-specific control.** Pixel-faithful to the
   mock, but creates a second near-identical language dropdown in the codebase — exactly the
   "second copy" CLAUDE.md forbids — and forks future fixes. Rejected.
3. **Settings as a modal dialog (like Contact Us) instead of a page.** Cheaper (no AppStep), but
   the mock is clearly a full main-region page with the rail frame, and settings will grow
   (theme section is already designed). Rejected.
4. **ViewModel vs code-behind.** A `SettingsViewModel` would hold… nothing: the page has no
   state beyond `LocalizationManager.CurrentLanguageCode`, which the shared control and the
   static manager already carry. Follow the `AgreementTermsView` precedent (code-behind
   `BackRequested` event, no VM). If the theme section lands later, a VM can be introduced then.

## Recommended approach

Option 1 + 4: a new `SettingsView` UserControl (no ViewModel) drawing the decoded frame,
hosting the shared `LanguageSelectView`, raising `BackRequested`; `HomeView` raises a new
`SettingsRequested` from the gear; `MainWindow` gains `AppStep.Settings` wired identically to
`Profile`. `LocalizationManager.InitializeFromSystemCulture`'s DEBUG branch is aligned with
Release (restore persisted preference, else system culture) so persistence is verifiable in dev
builds — the commented-out force-uk override line stays available.

## File-level change list

Order matters only in that tests land in the same change (new-feature coverage, non-negotiable).

1. `src/ProoveGaming.App/Views/SettingsView.axaml` — **create**: header row (back + title) and
   the 42/*/42 bevelled panel (Height 432, top-anchored) per the decoded geometry; centered
   600px content column with `SettingsView_LanguageLabel` and `commonControls:LanguageSelectView`.
2. `src/ProoveGaming.App/Views/SettingsView.axaml.cs` — **create**: `public event Action? BackRequested`;
   back-button click raises it; `OnLoaded` calls `SetSelectedLanguage(LocalizationManager.CurrentLanguageCode)`;
   subscribes `LanguageSelected += code => LocalizationManager.SetLanguage(code)` once in the ctor
   (static manager + singleton view — no leak; mirror WelcomeStepView).
3. `src/ProoveGaming.App/Views/HomeView.axaml` — **edit**: `SettingsButton` gains
   `Cursor="Hand"` and `Click="OnSettingsButtonClick"`; reword the placeholder comment above
   `HelpButton` so it names only the still-inert buttons (Search, Help).
4. `src/ProoveGaming.App/Views/HomeView.axaml.cs` — **edit**: add `public event Action? SettingsRequested;`
   and `OnSettingsButtonClick` raising it (pattern: `ProfileRequested`).
5. `src/ProoveGaming.App/Views/MainWindow.axaml` — **edit**: add
   `<views:SettingsView x:Name="SettingsView" IsVisible="False" />` beside `ProfileView`.
6. `src/ProoveGaming.App/Views/MainWindow.Controls.cs` — **edit**: add `GetSettingsView()` accessor.
7. `src/ProoveGaming.App/Views/MainWindow.axaml.cs` — **edit**: add `AppStep.Settings`;
   subscribe `GetHomeView().SettingsRequested += OnSettingsRequested` and
   `GetSettingsView().BackRequested += OnSettingsBack` in the ctor, unsubscribe in `OnClosed`;
   `OnSettingsRequested` → `_currentStep = AppStep.Settings; UpdateStepUi();`, `OnSettingsBack`
   → back to `AppStep.Home`; `UpdateStepUi()` toggles `GetSettingsView().IsVisible` and adds
   `and not AppStep.Settings` to the `GetStepLayoutView()` condition; `GetLayoutProfile` puts
   `AppStep.Settings` in the `Profile or DeviceDetails` arm (`RootMargin 24,0,24,24`).
8. `src/ProoveGaming.App/LocalizationManager.cs` — **edit**: `InitializeFromSystemCulture`
   DEBUG branch behaves like Release (persisted preference → system culture fallback), keeping
   the commented force-uk override; update the surrounding comment.
9. `src/ProoveGaming.App/Resources/Strings.en.axaml` — **edit**: add
   `SettingsView_Title` = "Settings", `SettingsView_LanguageLabel` = "Application Language".
10. `src/ProoveGaming.App/Resources/Strings.uk.axaml` — **edit**: add
    `SettingsView_Title` = "Налаштування", `SettingsView_LanguageLabel` = "Мова застосунку".
11. `src/ProoveGaming.Common/Controls/LanguageSelectView.axaml.cs` — **edit**: `Languages[1]`
    `DisplayName` "Ukrainian" → "Українська" (endonym convention; also visible in the Installer).
12. `tests/ProoveGaming.App.Tests/LanguageSelectViewTests.cs` — **create**: static-list tests
    (see Test strategy; pattern: `CountrySelectViewTests`).
13. `tests/ProoveGaming.App.Tests/LanguagePreferenceStoreTests.cs` — **create**:
    `NormalizeLanguage` mapping tests.
14. `docs/CONTEXT.md` — **edited by this plan**: added `LanguagePreferenceStore`,
    `LanguageSelectView`, `Settings view` glossary entries.
15. `docs/plans/fix-profile-and-auth-validation/fix-profile-and-auth-validation-plan.md` —
    **edit**: add a short dated note that `SettingsButton` is no longer a placeholder as of this
    plan (do **not** rewrite that plan's Review findings journal).

Round 1 fixes (2026-08-21) added on top of the list above:

16. `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` — **edit** (B1): ctor subscribes
    `LocalizationManager.LanguageChanged`; `OnLanguageChanged` re-raises `GreetingText`,
    refreshes every card's localized strings and re-labels the notification group headers.
17. `src/ProoveGaming.App/ViewModels/DeviceCardViewModel.cs` — **edit** (B1): new
    `RefreshLocalizedText()` re-raising `DeviceTypeLabel` and `ConnectionTooltip`.
18. `src/ProoveGaming.App/App.axaml` — **edit** (S1): the shared `NavBackBg` hover styles live
    here now, beside the other header `*Bg` styles.
19. `src/ProoveGaming.App/Views/ProfileView.axaml` — **edit** (S1): per-view `NavBackBg` styles
    removed (also removed from `SettingsView.axaml`, item 1).

No new NuGet packages, no new projects, no proto/`Directory.Build.props` changes. Item 11 touches
a control the Installer renders — flagged (release-process note: none beyond normal Installer
rebuild; no payload/registration change).

## Acceptance criteria

- [ ] Given the app on the Home screen, when the user clicks the gear icon in the header, the
      main content region switches to the Settings page (header "Settings" + back arrow, black
      bevelled panel with left/right rails per the decoded geometry) — no new window, no overlay.
- [ ] The gear button shows `Cursor="Hand"` and its hover plate, consistent with the other
      functional header buttons; Search and Help remain inert placeholders.
- [ ] Given the Settings page, when the user clicks the back arrow, the app returns to Home with
      device cards intact (no reload artifacts), and the gear can reopen Settings repeatedly.
- [ ] The language dropdown shows the current UI language with its flag; opening it lists exactly
      two rows — "English" (US flag) and "Українська" (UA flag), each name in its own language
      regardless of the current UI language, with the current one check-marked.
- [ ] Given the UI in English, when the user selects "Українська", every visible string in the
      window (Home header tabs, Settings title, section label, tray menu) switches to Ukrainian
      **immediately, without restart**, and the dropdown header now shows "Українська".
- [ ] After selecting a language, registry value `HKCU\Software\ProoveGaming\Settings\AppLanguage`
      equals `uk` (resp. `en`) — the same value the Installer/Uninstaller read.
- [ ] Given Ukrainian was selected, when the app is closed and relaunched, the whole UI starts in
      Ukrainian and reopening Settings shows "Українська" selected — in both Release **and**
      Debug builds (per the `LocalizationManager` DEBUG-branch fix).
- [ ] The en↔uk round trip works: switching back to English restores the English UI live and
      persists `en`.
- [ ] No control on the Settings page has a fixed `Width` carrying localized text; with the UI in
      Ukrainian, "Налаштування" and "Мова застосунку" render un-clipped.
- [ ] The Installer's Welcome-step language dropdown now shows "Українська" instead of
      "Ukrainian" (side effect of the shared endonym change) and still switches/persists.
- [ ] No Color Theme section is rendered anywhere on the Settings page.
- [ ] `dotnet build ./src/ProveGaming.slnx` and `dotnet test ./src/ProveGaming.slnx` pass.

## Test strategy

New feature (`add-*`) — the following tests are required coverage in
`tests/ProoveGaming.App.Tests` (xUnit v2; App.Tests already references `ProoveGaming.Common` —
see `CountrySelectViewTests`, which is the exact pattern: exercise the static members, never
instantiate the UserControl, since that needs Avalonia bootstrap):

`LanguageSelectViewTests.cs`:
- `Languages_HasExactlyEnglishAndUkrainian` — two entries, codes `LocalizationConstants.EnglishLanguageCode`
  then `UkrainianLanguageCode`, in that order (order defines the dropdown).
- `Languages_DisplayNamesAreEndonyms` — "English" and "Українська" (guards the convention and
  the regression back to "Ukrainian").
- `Languages_FlagIconUrisAreNonEmptyAndDistinct` — both `avares://ProoveGaming.Common/...` URIs
  present and different.

`LanguagePreferenceStoreTests.cs`:
- `NormalizeLanguage_MapsUkCaseInsensitively` — `"uk"`, `"UK"`, `"Uk"` → `uk`.
- `NormalizeLanguage_MapsEnAndUnknownToEnglish` — `"en"`, `"EN"`, `"de"`, `""` → `en`
  (documents the everything-else-is-English fallback the whole persistence chain relies on).

Deliberately **not** covered by automated tests, and why:
- Registry read/write round trip (`TryGetLanguage`/`SetLanguage`) — it targets the real
  `HKCU\Software\ProoveGaming\Settings`, and tests must not mutate the developer's actual app
  preference; the pure mapping rule is tested, the I/O is covered by QA steps 5–7.
- The live resource swap, view switching and dropdown interaction — require a running Avalonia
  application lifetime (same exclusion every view in this repo makes); covered by QA.
- `MainWindow` step wiring — code-behind event plumbing with no extractable rule.

## QA (manual test steps)

1. Launch the app (English OS locale, no prior language preference). On the Home screen, hover
   the gear icon in the top-right header. Expected: the cursor becomes a hand and the button
   shows its hover highlight.
2. Click the gear icon. Expected: the main area switches to a Settings page — "Settings" title
   with a back arrow at the top-left, a wide dark panel with angled side rails, a section label
   "Application Language" and a bordered dropdown showing a US flag + "English".
3. Click the back arrow. Expected: the Home screen returns with the device cards exactly as
   before. Click the gear again — Settings reopens.
4. Open the language dropdown. Expected: two rows — "English" with a US flag and "Українська"
   with a Ukrainian flag; "English" is check-marked. The names stay in their own language no
   matter which UI language is active.
5. Select "Українська". Expected: the entire UI switches to Ukrainian immediately (window title,
   Home tabs, Settings title "Налаштування", label "Мова застосунку") — no restart, no flicker
   beyond the text swap; the dropdown header now shows the Ukrainian flag + "Українська".
6. Close the app fully (tray icon → exit, not just the ✕). Relaunch. Expected: the app starts in
   Ukrainian; opening Settings shows "Українська" selected in the dropdown.
7. Switch back to "English". Expected: the UI returns to English immediately; after another full
   restart the app starts in English.
8. While the UI is in Ukrainian, look at the Settings page texts. Expected: "Налаштування" and
   "Мова застосунку" are fully visible, nothing clipped or pushed against a border.
9. (Installer spot-check) Run the installer to its first screen. Expected: its language dropdown
   lists "English" and "Українська" (previously "Ukrainian"); picking one still switches the
   installer language, and the choice made in the app (steps 5–7) is what the installer offers
   as the pre-selected language, and vice versa.
10. Resize the app window to its minimum (1200 wide). Expected: the Settings panel shrinks with
    the window, rails stay attached to the panel's sides, the dropdown stays centered and usable.

## Review focus

1. `localization` — endonym names, new en/uk keys, no fixed widths on localized text, live-switch
   propagation, DEBUG-branch restore change.
2. `installer/uninstaller` — the shared `LanguageSelectView` DisplayName change renders in the
   Installer's Welcome step; confirm nothing else in Installer/Uninstaller keys off the string
   "Ukrainian".
3. `UI-thread affinity` — language switch does registry I/O + resource-dictionary swap on the UI
   thread (existing pattern; sub-millisecond, but confirm nothing new/slow was added to the click path).
4. `resource disposal` — event pairs: `SettingsRequested`/`BackRequested` subscribed in
   `MainWindow` ctor must be unsubscribed in `OnClosed`, like every sibling event there.

## Performance & quality

- **No hot paths.** Settings is a cold page; the language switch is a user-initiated one-off.
- The registry write (`LanguagePreferenceStore.SetLanguage`) and the merged-dictionary swap run
  synchronously on the UI thread — this is the established, QA-approved behaviour of
  `LocalizationManager.SetLanguage` (Installer does the same); both are sub-millisecond and are
  not being made worse. Do not add new I/O to the selection path.
- `SettingsView` is constructed once in `MainWindow.axaml` and `IsVisible`-toggled like every
  other page — no per-navigation allocation. Its `LanguageSelected` subscription targets the
  static `LocalizationManager` and lives as long as the window: no leak, nothing to dispose.
- `LanguageSelectView` already detaches its window-level pointer handler when the dropdown
  closes — no new subscription management needed.
- Shared singleton state (device discovery, etc.) is untouched.

## Risks

- **Installer visual change rides along.** "Українська" appears in the Installer without an
  installer-focused QA pass in this change; the string is longer than "Ukrainian" but the control
  sizes rows by content (no fixed width), so risk is low. QA step 9 covers it.
- **DEBUG initialization change** alters dev-build startup language (was: always English, now:
  persisted/system). A developer with `uk` persisted will suddenly start in Ukrainian — that is
  the point (dev/QA parity), but it may surprise; the comment in `LocalizationManager` must say so.
- **Panel is fixed-height (432) and top-anchored** per the mock; at the maximum window height the
  space below is empty background. That matches the design (the theme section will fill it later)
  — reviewers should not "fix" it by stretching.
- **Geometry drift**: the rails' opacity is 0.7 per this mock while ProfileView/DeviceDetails use
  `#CC000000` (0.8). Follow the mock (0.7) — this echoes the "too dark" QA fix in commit 152db5b;
  do not normalize to 0.8.
- The staggered right rail (starts 54px down) is intentional mock geometry, not an error.

## Open questions

- Ukrainian wording: plan proposes "Налаштування" (title) and "Мова застосунку" (label). If the
  product has an agreed glossary preferring "Мова додатку" / "Мова інтерфейсу", swap before
  implementation — a one-line resource edit.
- Should the gear icon show a pressed/selected state while Settings is open (the Home header is
  not visible on the Settings page, so presumably moot)? Assumed: no.

## Review findings

### Round 1 — Ship: no

Reviewed 2026-08-21 against the uncommitted working tree on base `be81a7f` (clean base; the
whole feature is uncommitted, so the surface is `git status` + `git diff` + untracked files —
all 16 entries reviewed, nothing out of scope). Verification run: the two new test classes
(`dotnet test … --filter LanguageSelectViewTests|LanguagePreferenceStoreTests`) — **10/10 pass**,
and that run compiled `ProoveGaming.Common`, `ProoveGaming.GrpcDependencies`, `ProoveGaming.App`
and the test project cleanly. Note the CI caveat: `dotnet-ci.yml` builds only the App csproj, so
these tests only run locally / via the slnx.

#### Summary

Adds the Settings page: `SettingsView` (header + decoded bevelled panel + shared
`LanguageSelectView`), gear-button wiring in `HomeView`, `AppStep.Settings` navigation in
`MainWindow`, DEBUG-branch language restore in `LocalizationManager`, the "Ukrainian" →
"Українська" endonym change in the shared control (Installer-visible), two new resource keys
(en/uk), two new test classes, and the doc updates (CONTEXT.md glossary, superseding note in the
fix-profile-and-auth-validation plan).

#### Plan conformance

Plan: `docs/plans/add-settings-view/add-settings-view-plan.md`
- Implemented as planned: all 15 file-level items are present and match the spec — decoded rail
  geometry is transcribed exactly (left rail chamfer/body/notched cap, staggered right rail,
  `Fill="Black" Opacity="0.7"` not hex-alpha), 600px centered content column with the 46/12px
  offsets, ProfileView header pattern, `AppStep.Settings` wired symmetrically (subscribe in ctor
  / unsubscribe in `OnClosed`), `GetStepLayoutView()` condition and `GetLayoutProfile` arm both
  extended, DEBUG branch now shares the Release restore path with the force-uk override kept
  commented, endonym change with explanatory comment, both resource files, both test files,
  CONTEXT.md entries (items 14) and the dated superseding note (item 15).
- Deviates from plan: none found. No scope drift — every changed/untracked file maps to a plan item.
- Missing / partial: the plan's own acceptance criterion "every visible string in the window
  switches immediately" is not fully met — see **B1**. The plan's Findings §3 asserted
  `LanguageChanged` "covers the non-DynamicResource consumers (tray menu, ProfileViewModel,
  DeviceDetailsViewModel)"; that list was incomplete — `HomeViewModel` and `DeviceCardViewModel`
  also build strings in code and subscribe to nothing. Runtime language switching did not exist
  in the App before this change, so this is the first change that exposes it.
- Accepted limitation (per plan): Installer shows "Українська" without a dedicated Installer QA
  pass this round — residual risk low (rows size by content; verified nothing in
  Installer/Uninstaller string-matches "Ukrainian"; `AgreementStepView` keys off the language
  *code*). Fixed-height top-anchored panel and 0.7 rail opacity are per-plan — do not "fix".

#### Code

- [x] **B1. Live language switch leaves Home's code-built strings in the old language** —
  `src/ProoveGaming.App/ViewModels/HomeViewModel.cs:315` (`GreetingText`), plus
  `DeviceCardViewModel.cs:37/335` and `HomeViewModel.cs:450` — Blocker
  **Problem:** `LocalizationManager.LanguageChanged` has exactly three subscribers (tray menu in
  `App.axaml.cs:256`, `ProfileViewModel.cs:110`, `DeviceDetailsViewModel.cs:149`). Nothing on the
  Home side listens, and these Home strings are built in code, not `{DynamicResource}`:
  - `HomeViewModel.GreetingText` — computed getter over `GetString("HomeView_Greeting_*")`;
    `OnPropertyChanged(nameof(GreetingText))` is raised only on user-name change (line 493).
  - Device-card type labels — `{Binding DeviceTypeLabel, Converter=DeviceTypeLabelConverter}` in
    `SingleCard`/`DoubleCard`/`InactiveDeviceCard`/`InactiveDoubleCard`; converters re-run only
    when the binding re-evaluates, and `DeviceTypeLabel` is a plain `required` property that
    never raises.
  - `DeviceCardViewModel.ConnectionTooltip` — re-raised only via `[NotifyPropertyChangedFor]` on
    transport/battery changes.
  - Notification date headers — `GroupHeaderFor` bakes "Today"/"Yesterday" into
    `NotificationGroupViewModel.Header` at insertion time.
  **Why it blocks:** the acceptance criterion "every visible string in the window … switches to
  Ukrainian immediately" fails on the very next click: UI in English on Home → gear → select
  "Українська" → back (QA steps 3+5). The whole window is Ukrainian except the always-visible
  greeting "Hello, Taras" and every device card's "Wired Keyboard" label, which stay English
  until a device event or restart. Persistent, on the primary screen, right after the feature's
  one working control is used.
  **Solution options:**
  - Option A (correct): in `HomeViewModel`'s ctor (it is a singleton — no leak), subscribe
    `LocalizationManager.LanguageChanged` and: raise `OnPropertyChanged(nameof(GreetingText))`;
    for each `DeviceCardViewModel` in `Devices` raise its `ConnectionTooltip` +
    `DeviceTypeLabel` change notifications (give `DeviceTypeLabel` a raising setter or add a
    `RefreshLocalizedText()` helper, mirroring `RgbEffectSettingsViewModel.RefreshDisplayName`);
    re-run `RelabelAll` on `NotificationGroups` with `GroupHeaderFor` (the helper already exists
    for date rollover). Pattern precedent: `DeviceDetailsViewModel.OnLanguageChanged` (line 1341).
  - Option B (narrow): fix only `GreetingText` and the card type labels (the two always-visible
    artifacts) and accept tooltip/date-header staleness with a dated plan note — cheaper, but the
    acceptance criterion must then be reworded to name the accepted exceptions.
  **Fixed 2026-08-21 (Option A):** `HomeViewModel` (singleton) subscribes
  `LocalizationManager.LanguageChanged` in its ctor; the handler re-raises `GreetingText`, calls a
  new `DeviceCardViewModel.RefreshLocalizedText()` (re-raises `DeviceTypeLabel` +
  `ConnectionTooltip`) per card, and re-runs `RefreshNotificationGroupHeaders()`
  (`RelabelAll` + `GroupHeaderFor`). Handler is notification-only (no property writes → no
  re-entrancy) and `LanguageChanged` fires synchronously on the UI thread.
- [x] **S1. Second copy of the NavBack header block and its hover style** —
  `src/ProoveGaming.App/Views/SettingsView.axaml:10-16, 26-44` — Should-fix
  **Problem / why it matters:** the `Path#NavBackBg` visibility styles and the 56×56
  back-button + pixel-font title block are now byte-for-byte duplicates of
  `ProfileView.axaml:26-30, 94-115`. CLAUDE.md's trigger is the second copy, and the sibling
  header-button hover styles already live centrally (`App.axaml:467-489`), so the precedent for
  hoisting exists. The plan did direct "reuses the ProfileView header pattern verbatim", so this
  is a plan-endorsed copy — flagged because the repo rule outranks it and a third copy is
  predictable (any future page with a back button).
  **Fix:** move the two `NavBackBg` styles to `App.axaml` beside the other `*Bg` hover styles
  (delete both per-view copies), and/or extract the header row into a small shared control or a
  `ControlTemplate` with a title parameter. Acceptable as an immediate follow-up; no behaviour
  change involved.
  **Fixed 2026-08-21:** both `NavBackBg` style pairs deleted from `ProfileView.axaml` /
  `SettingsView.axaml` and hoisted once into `App.axaml` beside the other header `*Bg` hover
  styles. The 56×56 button + title markup itself stays per-view for now (the two differ in
  command wiring: Command binding vs code-behind Click) — extract a shared header control if a
  third page ever needs it.

#### Tests

- [x] The two planned test classes exist, match the `CountrySelectViewTests` pattern (statics
  only, no Avalonia bootstrap), and pass (10/10 locally). Coverage matches the Test strategy;
  the strategy's exclusions (registry I/O, live swap, step wiring) hold. If B1 Option A adds a
  notification-header relabel, `NotificationGroupViewModel.Insert`/`RelabelAll` is already
  testable with a stub `headerFor` — worth one test asserting headers are recomputed on demand.
  *Closed 2026-08-21 (post-B1 fix pass): added the suggested relabel-on-demand coverage —
  `NotificationGroupingTests.RelabelAll_LanguageSwitch_RewritesEveryHeaderWithTheNewFormatter`
  (en headers rewritten in place by a uk formatter stub, dates untouched) and
  `RelabelAll_RaisesHeaderChangeNotifications` (relabel reaches a live-bound panel) — plus a new
  `DeviceCardLanguageRefreshTests` class pinning `DeviceCardViewModel.RefreshLocalizedText()`:
  raises exactly `DeviceTypeLabel` then `ConnectionTooltip`, mutates no state (the handler's
  no-re-entrancy guarantee), and is repeatable. `HomeViewModel.OnLanguageChanged` itself stays
  uncovered by design — the ctor needs the full service graph and the static `LanguageChanged`
  subscription would leak across tests; both halves it delegates to are now tested directly.
  Full suite via slnx: Infrastructure 767/767, App 929/929, 0 failed.*

#### Nits

- Placeholder comment grammar: "like SearchButton this is still a placeholder, and **those**
  deliberately omit…" — plural remnant now that the set shrank to one named button —
  `src/ProoveGaming.App/Views/HomeView.axaml:96-98`. *Fixed 2026-08-21: "those" → "both"
  (Search + Help).*
- The DEBUG force-uk override comment says "force Ukrainian regardless of preference" but
  doesn't warn that `SetLanguage` **persists** the forced value to the shared registry key —
  a dev who uncomments it for a session silently rewrites the preference the Installer,
  Uninstaller and Release builds read — `src/ProoveGaming.App/LocalizationManager.cs:24-27`.
  *Fixed 2026-08-21: comment now warns about the persist and says how to undo it.*
- Pre-existing, not this change: `DeviceCardViewModel.StatusText` returns hardcoded English
  "NO CONNECTION" (`DeviceCardViewModel.cs:349`) — a localization gap worth its own ticket if
  B1 is being fixed in that file anyway. *Left open 2026-08-21: needs new en/uk resource keys
  and an agreed Ukrainian wording — not a zero-risk comment fix; ticket it separately.*

#### What's done well

- The decoded mock geometry is transcribed exactly as specified — including the deliberately
  preserved oddities (0.7 opacity, staggered right rail, the mock's own 95.357/95.367 seam) with
  comments pointing back at the plan so nobody "fixes" them.
- Event wiring is fully symmetric: both new subscriptions (`SettingsRequested`, `BackRequested`)
  are unsubscribed in `OnClosed`; `UpdateStepUi` assigns every view's visibility absolutely, so
  all step transitions in/out of Settings are covered with no leak and no orphan view.
- The endonym change is commented at the point of use, guarded by a dedicated test, and verified
  side-effect-free in the Installer (nothing string-matches "Ukrainian"; `AgreementStepView`
  switches on the code).
- The DEBUG/Release unification removed the `#else` duplication instead of adding a second copy
  of the restore logic; missing/garbage registry values fall through cleanly
  (`TryGetLanguage` → false → system culture; `NormalizeLanguage` → `en`).
- Docs discipline: glossary entries, the superseding note in the older plan (journal left
  untouched), and stale comment reworded in the same change.

#### Verdict

Ship: no — B1 is a real, immediately user-visible failure of this plan's own acceptance
criterion ("every visible string … switches immediately"), hit on the first round-trip through
the feature's only control; everything else is in good shape. First: (1) fix B1 (Option A
preferred — one subscription in the singleton `HomeViewModel`), (2) fold the `NavBackBg` styles
into `App.axaml` per S1, (3) reword the two comments from the Nits while in those files.

### Round 2 — Ship: yes

Reviewed 2026-08-21, post-fix-pass. Surface: branch `features/contact-us`, clean working tree;
delta vs `main` = commits `be81a7f` (Contact Us — reviewed and closed under
`docs/plans/add-contact-us-dialog/`'s own Round 1; spot-verified here that all three of its
fixes are in the commit: `ScrollViewer.AllowAutoHide` on the Message field, the
`CancelContactUsCommand` sending-gate, the owner-scoped email reset) and `4c7d99c` (this plan,
including the Round 1 fix pass). The committed content is byte-identical to the tree that was
build-verified (`Build succeeded, 0 Errors`) and test-verified (Infrastructure 767/767,
App 929/929) at the end of the fix pass.

#### Plan conformance

- Round 1 items verified closed in code, not just ticked: **B1** — `HomeViewModel.cs:506`
  subscribes `LocalizationManager.LanguageChanged` (an `Action?` static event raised
  synchronously on the UI thread from `SetLanguage`); the handler (`:579`) is notification-only
  (`GreetingText` re-raise, `RefreshLocalizedText()` per card, `RefreshNotificationGroupHeaders()`
  → the pre-existing `RelabelAll` at `:99`). All three card subclasses derive from
  `DeviceCardViewModel` and hold no extra `GetString` strings, so the base-class refresh covers
  them. **S1** — `NavBackBg` style pair now lives once in `App.axaml:491-497`; both per-view
  copies removed with pointer comments; no other `NavBackBg` consumers exist. Nits 1–2 fixed as
  journaled; nit 3 (StatusText) correctly left open.
- No new scope drift: every file in both commits maps to its plan.

#### Code

- [ ] **S1. Persisted notification titles stay in the pre-switch language** —
  `src/ProoveGaming.App/ViewModels/HomeViewModel.cs:352-380` (`AddNotification`) +
  `LocalStorageService.cs:2712` — Should-fix
  **Problem / why it matters:** notification titles are localized at *insertion* time
  (`GetString("Notification_DeviceDisconnected")` etc.) and persisted to SQLite as the resolved
  string (`NotificationRecord.Title`). After a live language switch, the bell panel shows the new
  language's group headers ("СЬОГОДНІ") over old-language row titles ("Device disconnected") —
  a mixed-language panel; and rows persisted in previous sessions render in whatever language was
  active when they were written. Round 1's B1 fix covered the headers but titles are baked data.
  This limitation predates the branch (titles were always persisted resolved) — runtime switching
  merely exposes it.
  **Fix:**
  - Option A (correct, forward-only): store the resource *key* in `NotificationRecord` (new
    column, old rows keep their resolved string as fallback) and resolve `Title` at render/relabel
    time; extend `OnLanguageChanged` to re-raise row titles. Pre-existing rows stay baked — that
    is unavoidable.
  - Option B (accept + document): treat notification rows as historical records (their subtitles
    are device names — untranslatable anyway), add a dated note here and reword nothing — the
    acceptance criterion's enumeration ("header tabs, Settings title, section label, tray menu")
    already scopes to chrome. Acceptable close given switch frequency; ticket Option A if product
    cares.

#### Tests

- The five fix-pass tests are genuine: `DeviceCardLanguageRefreshTests` pins the exact
  raise-set/order, the no-mutation guarantee behind the handler's no-re-entrancy claim, and
  repeatability; `NotificationGroupingTests.RelabelAll_LanguageSwitch_*` drives the real
  `RelabelAll` with a uk formatter over a fixed clock (deterministic) and asserts
  `PropertyChanged` reaches a live-bound panel. No coverage gap warrants a new item; the CI
  caveat (dotnet-ci.yml never runs `tests/`) stands from Round 1.

#### Nits

- `LanguageSelectView` dropdown state survives leaving the page: `OnWindowPointerPressed`
  closes via `CloseDropdown()` (no detach), and Avalonia `Button` marks `PointerPressed` handled,
  so clicking the Settings back button with the dropdown open neither closes it nor detaches the
  window handler — Settings reopens with the dropdown already open, and repeated open/close
  cycles can stack duplicate (idempotent) window handlers —
  `src/ProoveGaming.Common/Controls/LanguageSelectView.axaml.cs:67-100`. Pre-existing shared-
  control behaviour (the Installer never hides the view, so it was invisible there); cosmetic;
  own ticket, shared with the Installer.

#### What's done well

- The fix-pass handler discipline: `OnLanguageChanged` provably raises-only (now pinned by a
  test), documents its threading assumption at the subscription site, and reuses the existing
  relabel machinery instead of duplicating header logic.
- The DEBUG force-uk override now carries the registry-persistence warning and a `//return;`
  escape so an uncommented override composes correctly with the restore path either way.

#### Verdict

Ship: yes — no blockers; S1 is a pre-existing data-format limitation newly exposed by live
switching, closeable with a dated doc note (Option B) or ticketed for key-based storage
(Option A). First: (1) decide S1 Option A vs B and record it here, (2) ticket the shared
`LanguageSelectView` dropdown-state nit, (3) hand to QA with the plan's manual steps
(steps 5–7 and 9 are the ones automation cannot reach).

### Round 3 (branch `bugfix/PG-642`, commit adb927b "PG-623") — Ship: yes

#### Summary
Implements the 2026-08-31 superseded-geometry note: the panel becomes 752×740, centered,
with symmetric 21.65×21.31 chamfers on all four corners at opacity 0.8, and the content
column's top margin moves 46→54. The same commit also carries an unrelated one-line
Contact Us dialog fix in `MainWindow.axaml` (the send-failure error text gained a 12px
bottom margin so it no longer sits flush against the CANCEL/SEND row — QA 2026-08-31);
that change has no owning plan and is recorded here for traceability.

#### Plan conformance
Plan: this document (Panel + rails section, 2026-08-31 note).
- Implemented as planned: geometry verified arithmetically (21.3145 + 697.371 + 21.3145 =
  740 exactly on both rails; mirrored right-rail paths are exact reflections), fills mapped
  per the repo SVG rule (`Fill="Black" Opacity="0.8"`, not hex-alpha), 600px column with
  `Margin="0,54,0,0"`.
- Accepted limitation (per plan): the mock's Color Theme section is deliberately not built
  (no theming support exists) — residual risk: QA comparing against the full mock will see
  the empty lower half; the plan note names this so it reads as pending, not missing.

#### Code
- none (no Blockers, no Should-fix). Checked: the fixed 740px height clips its bottom
  chamfer at extreme minimum window sizes (880px window minus app chrome and the 80px view
  header leaves less than 752px), matching the pre-existing behaviour of DeviceDetailsView's
  fixed 756px panels — house style, not a regression; `RootScaler` handles the
  smaller-than-minimum case. Opacity 0.8 supersedes the earlier deliberate 0.7 ("too dark"
  QA echo) — intentional, follows the newer mock, worth one QA glance.

#### Tests
- [ ] none required — pure XAML geometry; the repo's declared strategy covers view-level
  markup via manual QA (no headless pointer/render harness).

#### Nits
- The Contact Us margin rider would have been cleaner as its own commit — it is invisible
  under a "PG-623 settings" message when bisecting.

#### What's done well
- The mock decode is written into the XAML comment (artboard size, corner geometry, what
  was removed, why 0.8) so the next mock revision can be diffed against stated intent.

#### Verdict
Ship: yes — geometry matches the mock exactly; no functional surface. First: (1) QA glance
at the 0.8 panel darkness against the old "too dark" complaint, (2) QA the panel at the
minimum window size for the chamfer clip.
