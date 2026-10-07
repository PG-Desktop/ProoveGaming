# Plan: add-information-page

> **Note (2026-09-24):** the sign-in, registration, sign-out and guest-expiry logic this plan places in `MainWindow.axaml.cs` now lives in `src/ProoveGaming.App/Services/AuthSessionCoordinator.cs` ([`refactor-auth-session-flow`](../refactor-auth-session-flow/refactor-auth-session-flow-plan.md)). Same statements, same order; `MainWindow` keeps the navigation. Name map: `OnSignInSucceeded` → `CompleteSignInStateAsync` + `FinishSignInAsync`; `OnNicknameChosen` → `RegisterAsync` + `FinishRegistrationAsync`; `OnLogoutRequested` → `LogoutAsync`; `SignOutToFreshSignIn` → `SignOut`; `OnTokenRejected`'s check → `IsCurrentToken`; `OnSessionExpired` → `HandleGuestSessionExpired`; `OnGuestContinueRequested` → `StartGuestSession`; `ClearSignedInState`, `PersistAuthIfTokenPresent` and `EnqueueStorageWrite` moved as they were.

> **2026-09-03 — superseded in part by
> [../add-information-sections/add-information-sections-plan.md](../add-information-sections/add-information-sections-plan.md):**
> the "empty content area" and "empty octagon" descriptions below reflect Phase 1 as
> shipped. Phase 2 filled the octagon with three tabs (Privacy Policy, Terms of Service,
> License Agreement) and the central panel with the corresponding localized legal
> documents. The navigation contract described here (popup entry points, back to Home)
> is unchanged.

## Goal

Add an **Information page** to the App, opened from the "Information" item in HomeView's
dropdown popups (both the signed-in `ProfilePopup` and the guest `GuestPopup` carry the
item today, and neither has a click handler — the row is currently dead). The page reuses
the Profile page's visual chrome — header row with the chamfered back button and pixel-font
title, plus the octagonal/chamfered dark frame — but ships with an **empty content area**:
the actual content is out of scope and will be designed later. The deliverable of this
change is the carried-over visual shell and working navigation in and out of the page.

## Scope

**In:**

- New `InformationView` (App project) replicating ProfileView's page chrome: header
  (back button + localized title) and the dark chamfered frame (left octagon panel
  background, left/right chamfered sidebars, central content rectangle).
- Navigation: HomeView popup "Information" click (both popups) → Information page;
  back button → Home. New `AppStep.Information` in MainWindow's code-behind navigation.
- Localization: new `InformationView_Title` key in **both** `Strings.en.axaml` and
  `Strings.uk.axaml` (the popup row's `HomeView_Menu_Information` key already exists
  in both languages).

**Out:**

- Any actual page content (sections, tabs, text) — arrives with a future plan.
- The octagon panel's tab items (ACCOUNT/SECURITY equivalents): only the empty octagonal
  background is carried over so the frame silhouette matches Profile; tab highlights,
  icons and labels are content and come later.
- A dedicated `InformationViewModel` — nothing to bind yet (see Options).
- Installer/Uninstaller — untouched. No new NuGet packages, no `.slnx` or
  `Directory.Build.props` changes.

## Options considered

### Option A — View-only page, code-behind `BackRequested` event (SettingsView pattern)

`InformationView : UserControl` with `public event Action? BackRequested;` raised from the
back button's `Click` handler, exactly like `SettingsView.axaml.cs`. No view model, no DI
registration. MainWindow wires the event and flips `AppStep`.

- **Pros:** smallest possible change; matches an existing, reviewed pattern for a page
  with no state; nothing to register in `BuildServices()`; trivially testable by eye.
- **Cons:** when real content arrives, a view model will have to be introduced then
  (a follow-up plan will need to add DI + `DataContext` wiring).

### Option B — Full MVVM from day one (ProfileView pattern)

Create `InformationViewModel` with a `NavigateBackCommand` + `BackRequested` event,
register it as a singleton in `App.axaml.cs::BuildServices()`, set `DataContext` in
MainWindow's constructor, mirror ProfileView's VM-event plumbing.

- **Pros:** future content lands into a VM that already exists.
- **Cons:** speculative — an empty page has zero bindable state, so the VM would be an
  empty shell reviewed twice (now, and again rewritten when content is designed). Violates
  the repo's "no premature abstraction" rule.

**Also considered and rejected:** extracting the Profile frame into a shared
`ProoveGaming.Common` control now. The frame markup exists in exactly one place
(ProfileView); this change creates the *second* copy, which per the don't-duplicate-UI
rule is the extraction trigger — but the Information page's final content may reshape its
frame (different column widths, no tab panel), so extracting before the second consumer's
real needs are known risks building the wrong abstraction. Instead the plan copies the
frame verbatim and leaves a dated note here; if the Information content lands and the
frame is still identical, the follow-up plan must extract it. This is a conscious,
documented deferral, not an oversight — flagged for the reviewer below.

## Recommended approach

**Option A.** The page is deliberately empty; the only behaviour is navigation, which the
codebase already does uniformly through view events consumed by `MainWindow` code-behind
(`MainWindowViewModel.cs` is intentionally empty — navigation is not VM-driven in this
app). Follow `SettingsView` for the wiring and `ProfileView` for the visuals.

Concretely:

1. **`InformationView.axaml`** — copy from `ProfileView.axaml`:
   - the header `Grid` (rows `80,*`): `NavBackButton` (keep the exact
     `x:Name="NavBackButton"` / `x:Name="NavBackBg"` so the shared hover styles in
     `App.axaml` — `Path#NavBackBg` / `Button#NavBackButton:pointerover` — apply with no
     new styles) and the title `TextBlock` bound to `{DynamicResource InformationView_Title}`
     with the same font stack (`AppPixelFont`, 36/32, weight 250, white);
   - the frame `Grid` (columns `160,4,42,668,42`, `Margin="0,12,0,0"`,
     `UseLayoutRounding="False"`): the octagonal `#CC000000` background path in column 0
     (background path only — no highlight rectangles, no icons/labels, no click overlay
     buttons), both chamfered sidebar column groups, and the central `#CC000000`
     content rectangle;
   - **no** content stack panels, popups, or `UserControl.Styles` from ProfileView —
     none of ProfileView's field/avatar/nickname styles are needed by an empty page.
2. **`InformationView.axaml.cs`** — `public event Action? BackRequested;`, back button
   `Click` → invoke, mirroring `SettingsView.axaml.cs` (no `OnLoaded` logic needed).
3. **HomeView** — `public event Action? InformationRequested;` in
   `HomeView.axaml.cs`; one shared handler `OnMenuInformationClick` that closes **both**
   popups (same shape as `OnMenuContactUsClick`) and raises the event; attach
   `Click="OnMenuInformationClick"` to the Information button in **both** `ProfilePopup`
   and `GuestPopup` in `HomeView.axaml`. (`profileMenuItem` already sets `Cursor="Hand"`.)
4. **MainWindow** —
   - `AppStep` enum: add `Information`;
   - `MainWindow.axaml`: `<views:InformationView x:Name="InformationView" IsVisible="False" />`
     next to the other step views inside the content host grid;
   - `MainWindow.Controls.cs`: `GetInformationView()` accessor;
   - constructor: `GetHomeView().InformationRequested += OnInformationRequested;` and
     `GetInformationView().BackRequested += OnInformationBack;`;
   - the unsubscribe block (`OnClosed`/teardown around lines 485–494): matching `-=` for both;
   - `OnInformationRequested()` → `_currentStep = AppStep.Information; UpdateStepUi();`
     `OnInformationBack()` → `_currentStep = AppStep.Home; UpdateStepUi();`;
   - `UpdateStepUi()`: `GetInformationView().IsVisible = _currentStep == AppStep.Information;`
     and add `and not AppStep.Information` to the `StepLayoutView` visibility expression;
   - `GetLayoutProfile()`: add `AppStep.Information` to the existing
     `AppStep.Profile or AppStep.DeviceDetails or AppStep.Settings` arm (same
     `RootMargin 24,0,24,24` profile — the page shares Profile's layout).
5. **Localization** — `InformationView_Title`: en `Information`, uk `Інформація`,
   added to both App resource files (the parity test enforces the pair).

## File-level change list

| File | Action | Intent |
|---|---|---|
| `src/ProoveGaming.App/Views/InformationView.axaml` | create | Page chrome copied from ProfileView: header (back + title) and empty chamfered frame. |
| `src/ProoveGaming.App/Views/InformationView.axaml.cs` | create | `BackRequested` event raised from the back button (SettingsView pattern). |
| `src/ProoveGaming.App/Views/HomeView.axaml` | edit | Attach `Click="OnMenuInformationClick"` to the Information rows in ProfilePopup **and** GuestPopup. |
| `src/ProoveGaming.App/Views/HomeView.axaml.cs` | edit | Add `InformationRequested` event + `OnMenuInformationClick` handler closing both popups. |
| `src/ProoveGaming.App/Views/MainWindow.axaml` | edit | Host the new view alongside the other step views (`IsVisible="False"`). |
| `src/ProoveGaming.App/Views/MainWindow.Controls.cs` | edit | Add `GetInformationView()` accessor. |
| `src/ProoveGaming.App/Views/MainWindow.axaml.cs` | edit | `AppStep.Information`; subscribe/unsubscribe the two events; step handlers; `UpdateStepUi()` + `GetLayoutProfile()` entries. |
| `src/ProoveGaming.App/Resources/Strings.en.axaml` | edit | Add `InformationView_Title` = `Information`. |
| `src/ProoveGaming.App/Resources/Strings.uk.axaml` | edit | Add `InformationView_Title` = `Інформація`. |
| `docs/CONTEXT.md` | edit | Add one-line glossary entry for **Information page**. |

## Acceptance criteria

- [ ] Given a **signed-in** user on Home, when the avatar/profile button is clicked and
      then the "Information" row in the dropdown is clicked → the dropdown closes and the
      Information page is shown (Home's card grid is no longer visible).
- [ ] Given a **guest** on Home, when the guest button is clicked and then the
      "Information" row in the guest dropdown is clicked → the dropdown closes and the
      same Information page is shown.
- [ ] Given the Information page is open, its header shows the chamfered back button on
      the left and the localized title ("Information" / "Інформація" per app language)
      in the pixel font, visually matching the Profile page header.
- [ ] Given the Information page is open, the dark chamfered frame (left octagon panel,
      two chamfered sidebars, central panel) renders in the same position and size as on
      the Profile page, and the central area is empty — no fields, tabs, or text.
- [ ] Given the Information page is open, hovering the back button shows the same hover
      plate as on Profile/Settings, and clicking it returns to Home with the device cards
      intact (no re-login, no device list reset).
- [ ] Given the app language is switched in Settings and the Information page is opened
      afterwards → the title renders in the selected language (DynamicResource, no restart).
- [ ] `LanguageResourceParityTests` passes (the new key exists in both en and uk).
- [ ] Navigating Home → Information → Home repeatedly causes no growth in event
      subscriptions (subscribe once in the constructor, unsubscribe in teardown — same
      lifecycle as the existing step views).

## Test strategy

Kind: **new feature (`add-*`)** — the tests below must be added, not optional.

- `tests/ProoveGaming.App.Tests` (xUnit v2):
  - **`LanguageResourceParityTests`** — already pins en/uk parity for every App resource
    pair; no new test needed, but it must stay green after the key is added (it is the
    automated guard for the localization criterion).
  - **New `InformationViewResourcesTests`** (mirror the style of
    `BluetoothModeBannerResourcesTests`, which asserts on resource content without
    spinning up Avalonia): assert `Strings.en.axaml` and `Strings.uk.axaml` both contain
    `InformationView_Title`, and that `InformationView.axaml` references
    `{DynamicResource InformationView_Title}` and `{StaticResource CutCornerFrameGeometry}`
    for the back plate (guards against a hardcoded title string or a forked geometry).
    If XAML-content assertions prove impractical in the existing test style, the resource
    key assertions alone are the minimum.
- **Deliberately not covered by automated tests:** the navigation wiring itself
  (`AppStep` flips, popup close, `IsVisible` toggles). It lives in `MainWindow` /
  `HomeView` code-behind, which requires a running Avalonia visual tree and window; no
  existing test in the suite instantiates MainWindow, and this change does not introduce
  the harness to start. That behaviour is covered by the manual QA checklist below —
  same trade-off every existing step view made.

## QA (manual test steps)

1. Launch the app and sign in with a registered account. Expected: the Home screen with
   device cards is shown.
2. Click the avatar button in the top-right corner. Expected: the profile dropdown opens.
3. Click "Information" in the dropdown. Expected: the dropdown closes and an Information
   page opens — same dark framed panel style as the Profile page, with a back arrow and
   the title "Information", and an empty panel (no content inside the frame).
4. Hover the back arrow button. Expected: a violet hover plate appears behind the arrow,
   the same as on the Profile page.
5. Click the back arrow. Expected: you return to the Home screen; device cards are still
   there and the app does not freeze or flicker.
6. Repeat steps 2–5 three times in a row. Expected: identical behaviour every time.
7. Log out, continue as Guest, and click the guest button in the top-right corner.
   Expected: the guest dropdown opens.
8. Click "Information" in the guest dropdown. Expected: the same Information page opens;
   the back arrow returns to Home.
9. Go to Settings, switch the app language to Ukrainian, and open the Information page
   again (via either dropdown). Expected: the page title reads "Інформація"; the menu row
   reads "Інформація" too.
10. On the Information page, resize and maximize the window. Expected: the framed panel
    stays centered and intact, nothing overlaps or clips.

## Review focus

1. `localization` — new key present in **both** en and uk; title via `DynamicResource`;
   no fixed `Width` on anything holding localized text.
2. `resource disposal` — event subscribe/unsubscribe symmetry in `MainWindow`
   (constructor `+=` must have the matching `-=` in the teardown block) and no leaked
   handlers in `HomeView`.
3. `UI-thread affinity` — navigation handlers are trivial and synchronous; nothing may
   block in them.

## Performance & quality

**No hot paths.** The page is a static shell shown on explicit navigation; no per-frame
work, no allocations beyond one-time control construction (the view is created once with
MainWindow and toggled via `IsVisible`, like every other step view). No async work is
introduced, so no sync-over-async risk. No shared singleton state is touched — the change
does not go near the device-discovery services.

One quality note: this change intentionally creates the **second copy** of the Profile
frame markup (see Options). The copy must be verbatim (same path data, colors, column
widths) so a future extraction into `ProoveGaming.Common` is mechanical. The reviewer
should diff the frame block of `InformationView.axaml` against `ProfileView.axaml` and
flag any drift.

## Risks

- **Frame drift.** Hand-copying the chamfer path data invites a one-digit typo that shows
  as a subtly broken silhouette. Mitigation: copy-paste the exact blocks, and the QA
  side-by-side comparison with Profile (step 3).
- **Asymmetric event wiring.** Forgetting the `-=` in MainWindow's teardown, or forgetting
  the GuestPopup's click attachment (the guest path is easy to miss since testing usually
  happens signed-in). Both are called out in Acceptance criteria and QA.
- **`StepLayoutView` visibility expression.** `UpdateStepUi()` hides the step-frame layout
  with an explicit `is not … and not …` chain; missing the new step there would render
  the welcome frame *behind* the Information page. Covered by QA step 3 (any bleed-through
  is visible).
- **Future content may not fit this frame.** If the designed Information content needs a
  different layout, the copied frame gets reworked — accepted, since the user explicitly
  asked to carry the Profile visuals now.

## Open questions

None — both were resolved with the user on 2026-09-03:

1. ~~Should the left octagon panel hold tabs?~~ **Decided: ship the empty octagon
   background** so the silhouette matches Profile; tab items arrive with the content
   design.
2. ~~Other entry points (help `?` button, Settings)?~~ **Decided: only the two Home
   popups** (ProfilePopup and GuestPopup) open the page in this change.

## Review findings

### Round 1 (2026-09-03) — Ship: yes

**Review surface note:** the working tree sits on `features/texts-update`, which also
carries three earlier commits vs `main`/`origin/main` (`9aaf5ea` headset-bluetooth-lock
removal, `b85a34b` PG-599, `01f61ad` PR #130 review fixes). Those belong to other plans
(`remove-headset-bluetooth-lock`, `fix-card-status-icons-centering`,
`add-keyboard-charge-push`) and already went through their own PR review round; this round
reviews the **uncommitted** add-information-page delta only (9 modified + 4 untracked
files), which is exactly this plan's scope.

#### Plan conformance
- Implemented as planned: all 10 File-level change list items are present and match the
  Recommended approach — new `InformationView.axaml`/`.axaml.cs` (Option A, view-only,
  SettingsView back-event pattern), both popup click attachments, `InformationRequested`
  event + shared close-both-popups handler, `AppStep.Information` with symmetric
  subscribe (ctor) / unsubscribe (`OnClosed`), `UpdateStepUi()` incl. the
  `StepLayoutView` guard, layout-profile arm joined to Profile/DeviceDetails/Settings,
  both resource keys, glossary entry.
- Test strategy delivered: `InformationViewResourcesTests` (2 facts + 1 theory×2) went in
  with the full XAML-content assertions — the "if impractical" fallback was not needed,
  since the csproj already copies view files into test output. `LanguageResourceParityTests`
  green.
- Deviates from plan: none found. Frame markup was diffed block-by-block against
  `ProfileView.axaml` — octagon path, both sidebar chamfers (21.3145 rows), the
  `160,4,42,668,42` columns, `Margin="0,12,0,0"` and `UseLayoutRounding="False"` are
  verbatim; no drift.
- Accepted limitation (per plan): navigation wiring has no automated tests (no Avalonia
  headless harness in the suite) — residual risk is a silent wiring regression, mitigated
  by the QA checklist; and the second copy of the Profile frame exists pending extraction
  when the content lands.
- Acceptance criteria: all code-verifiable ones hold (both entry points wired, shared
  hover styles picked up via `NavBackButton`/`NavBackBg` names, DynamicResource title,
  parity test green, subscriptions one-shot in ctor with matching teardown). Visual
  criteria (frame position/size vs Profile, hover plate, resize) remain for manual QA.
- Seam checked beyond the plan: every `_currentStep` consumer in `MainWindow.axaml.cs` was
  walked — `SignOutToFreshSignIn` (session expiry/token rejection) sends any step,
  including Information, to Welcome, same as Profile/Settings; no other step-dependent
  logic (tray, recorder keys, dialogs) needed the new member.

#### Code
- none

#### Tests
- none — required coverage from the Test strategy is in and passing (1563/1563 full
  `ProoveGaming.App.Tests` run in Debug). CI note: the `dotnet-ci.yml` PR job runs the
  full slnx suite in Release; these tests are file-content assertions with no timing
  sensitivity, so no Release-only risk is expected.

#### Nits
- `src/ProoveGaming.App/Views/InformationView.axaml.cs` uses tab indentation while its
  sibling `HomeView.axaml.cs`/`ProfileView.axaml.cs` use spaces — it does mirror its
  stated template `SettingsView.axaml.cs` (tabs), and both styles already coexist in
  `Views/`, so this is cosmetic only.

#### What's done well
- The guest path was not forgotten: both popups share one handler that closes whichever
  dropdown is up, exactly mirroring the existing `OnMenuContactUsClick` shape.
- Subscribe/unsubscribe symmetry in `MainWindow` is complete on the first try (ctor
  `+=` at lines 116/121, `OnClosed` `-=` at lines 488/498) — the classic leak this
  checklist exists for did not happen.
- The new tests close the parity-test blind spot (key missing from *both* languages) and
  pin the shared `CutCornerFrameGeometry`, guarding the exact regressions this kind of
  copy-paste page invites.

#### Verdict
Ship: yes — no blockers, no should-fixes; the diff maps 1:1 to the plan and the only
open items are the manual QA pass and the planned frame extraction when real content
arrives. First: run the plan's QA checklist (steps 3–5 and 8 cover the two entry points
and the visual match).
