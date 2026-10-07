# Fix QA bug batch — uninstall auth leak, actuation UX, notifications, section scroll

Six QA findings from the 2026-08-10 test pass, fixed as independent, individually shippable
steps. Screenshots live in the tracker tickets; each section below restates the observed
behaviour so this doc is self-contained.

## Goal

Close six QA bugs: (1) saved sign-in survives an uninstall that was told to delete user
data (and the profile-menu greeting clips long nicknames); (2) the actuation "Release
point" reads inverted against its own description; (3) the notifications panel shows one
hardcoded "Today" header regardless of date; (4) the notifications "delete all" button
does nothing; (5) the actuation section cannot scroll, clipping its bottom slider;
(6) the "All keys / Per-key" toggle renders in a fallback font instead of the design
pixel font.

## Scope

**In:** the six fixes above — Uninstaller data-deletion robustness, App UI/VM changes,
localization resource changes, one new group ViewModel, tests for the new notification
logic.

**Out:** persisting notifications across app restarts (they are in-memory today and stay
so); parsing `--quiet` in the uninstaller; removing the HKCU language key on uninstall;
server-side token validation at startup; the DEBUG-build mock uninstall path (noted in
Risks); any change to device protocols or firmware writes.

## Research facts (file:line, verified)

- Saved auth lives only in the `saved_auth` table of
  `%LocalAppData%\ProoveGaming\proovegaming.db`
  (`src/ProoveGaming.App/Services/LocalStorageService.cs:50-57`, DDL `:97-104`). A
  non-expired row sends the app straight to Home:
  `src/ProoveGaming.App/Views/MainWindow.axaml.cs:75-81`.
- The uninstaller checkbox is wired correctly
  (`src/ProoveGaming.Uninstaller/Views/WelcomeStepView.axaml.cs:23-24` →
  `Views/MainWindow.axaml.cs:87-91`), but:
  - the uninstaller always relaunches itself elevated (`Program.cs:42-79`, `Verb="runas"`),
    so `Environment.GetFolderPath(LocalApplicationData)` in
    `UninstallService.DeleteDataFolder` (`UninstallService.cs:71-84`) resolves to the
    **elevating admin's** profile whenever elevation crosses accounts — the interactive
    user's DB survives;
  - `DeleteUserDataFolder()` is the **last** stage (`UninstallService.cs:44-55`); a throw
    from the service-removal stage (`RunScCommandAsync` throws on any sc.exe failure other
    than 1060/1062, `UninstallService.cs:166-175`) aborts it through an `async void`
    handler.
- Greeting TextBlock (`src/ProoveGaming.App/Views/HomeView.axaml:249-257`) has no
  `TextTrimming`/`MaxWidth`; usable width inside the 204-px popup is 164 px.
- Release point: the wire value is an **absolute travel depth** in centi-mm, same range
  and coordinate as the press point (proto
  `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto:550-556`; Cyrex/Aress
  `UP` field written verbatim, `src/ProoveGaming.Infrastructure/Classes/SmartInfiniteTravelArea.cs:33`;
  T800 `TripOptionRelease`, `Classes/Ry5088Protocol.cs:41`). Deeper release depth ⇒ the
  key needs less upward travel ⇒ releases sooner — matching QA's observation. The UI hint
  claims the opposite ("How far a key must rise before it releases",
  `src/ProoveGaming.App/Resources/Strings.en.axaml:361`, `Strings.uk.axaml:347`). The
  vendor docs (`docs/knowledge-base/smart-infinite-protocol/magnetic-axis-travel.md:116-123`,
  `docs/knowledge-base/ry5088-magnetic-axis-protocol.md:60-63`) define release as a plain
  travel depth, never a rise distance. The VM applies no inversion
  (`ViewModels/DeviceDetails/DeviceDetailsViewModel.Actuation.cs:353-355`).
- Notifications are an in-memory `ObservableCollection<NotificationItemViewModel>` on
  `HomeViewModel` (`ViewModels/HomeViewModel.cs:94`), items carry only a preformatted
  `Time = DateTime.Now.ToString("HH:mm")` string (`:127`), derived props
  (`HasNoNotifications`/`UnreadNotificationsCount`/`HasUnreadNotifications`,
  `:96-99`) are re-raised manually. The "Today" divider is hardcoded XAML
  (`Views/MainWindow.axaml:358-373`); the trash button (`:313-323`) has **no Command**;
  no delete-all command exists anywhere. No `Notifications_Yesterday` resource exists.
- Actuation section root is a bare `StackPanel` with no ScrollViewer
  (`Views/DeviceDetails/Sections/ActuationSectionView.axaml:12`); every sibling section
  scrolls with a hidden bar, the closest analogue being
  `RgbLightingSectionView.axaml:97-109` (Hidden/Disabled, viewport `Width="348"`,
  `ClipToBounds="False"`, content `Width="336"`).
- The mode toggle already uses the pixel font via `Button.sectionModeButton`
  (`App.axaml`), but its labels are the only sentence-case pixel-font strings in the app
  (`Strings.uk.axaml:352-353` "Всі клавіші"/"Окремі клавіші"); Pixel Font7 lacks
  lowercase glyphs, so the text silently renders in the Inter fallback — that is the
  "wrong font" QA sees. Every other pixel-font label is ALL CAPS.

## Options considered

### Bug 1 — auth survives uninstall
- **A. Pass the interactive user's LocalAppData through elevation + harden ordering
  (recommended).** The pre-elevation process runs as the interactive user; capture the
  path there, forward it via a new CLI argument on the `runas` relaunch, prefer it inside
  `DeleteUserDataFolder`, and run user-data deletion early (right after app processes are
  killed) so no later failure can skip it. Pros: fixes both root causes, no new
  processes. Cons: touches the uninstaller launch contract (release-process relevant).
- **B. App-side self-heal** (App wipes `saved_auth` when it detects a fresh install).
  Rejected: the ticket's contract is that the uninstaller deletes the data; detecting
  "fresh install" reliably is guesswork, and the stale DB would still leak macros/presets.

### Bug 2 — release point reads inverted
- **A. Fix the copy to match the real semantics (recommended).** Reword the en/uk hint to
  state it is a depth: a **larger** value means the key releases with **less** upward
  travel. No numeric or wire change. Pros: stored values, presets, and firmware stay 1:1
  with the UI; zero migration. Cons: the slider still grows toward "releases sooner",
  which some users may find unintuitive.
- **B. Invert the slider mapping** (display rise-distance = range-max − depth). Rejected:
  displayed numbers would no longer match device/preset/DB values, existing saved presets
  would silently change meaning, and the vendor protocol has no rise-distance concept.

### Bug 3/4 — notifications grouping + delete all
- **A. Group ViewModels maintained beside the flat list (recommended).** Add a
  `Timestamp` to items, keep the flat `Notifications` list as the source of truth, and
  maintain an `ObservableCollection<NotificationGroupViewModel>` (header + items) that
  add/remove/clear paths update. XAML renders groups with the existing divider as the
  group header template (precedent: nested ItemsControl in
  `ButtonsSectionView.axaml:191-198`). Pros: cheap, testable, no behavioral change to
  badges. Cons: two collections to keep in sync (all mutations already flow through
  `AddNotification` / delete handlers, so the sync surface is three small methods).
- **B. Regenerate a grouped snapshot on every change** (rebuild groups list wholesale).
  Simpler sync but re-renders the entire panel on each notification; rejected as
  needless churn, though acceptable if A proves fiddly.

### Bug 5 — actuation scroll
- **A. Mirror `RgbLightingSectionView` (recommended):** hidden-bar ScrollViewer,
  Hidden/Disabled, `Width="348"`, `ClipToBounds="False"`. Matches every sibling section's
  design.
- **B. Visible `pgScrollViewer` bar** (as on Home). Rejected for consistency: no
  device-details section shows a bar today.

### Bug 6 — toggle font
- **A. Uppercase the two resource strings (recommended)** ("ALL KEYS"/"PER-KEY",
  "ВСІ КЛАВІШІ"/"ОКРЕМІ КЛАВІШІ") so Pixel Font7 renders them like every other label.
- **B. Apply `StringToUpperConverter` in XAML.** Works, but the strings come through
  `DynamicResource` (no binding to convert) — it would need restructuring for no gain.

## Recommended approach

Apply option A in each section above. The six fixes are independent; implement and
verify them in the order listed (uninstaller first, since it is the only one with
release-process implications and the longest QA loop).

## File-level change list

Ordered; tests for notifications land before the notification behavior change per the
bug-fix rule (the repro tests are written first and fail on current code).

1. `tests/ProoveGaming.App.Tests/NotificationGroupingTests.cs` — **new**; repro tests
   written first: grouping by calendar date (Today/Yesterday/dd.MM.yyyy headers, newest
   group first), delete-all clears items+groups and resets unread state. Fails to compile
   / fails until items 5–8 land.
2. `src/ProoveGaming.Uninstaller/Program.cs` — in the unelevated first instance, append
   `--user-local-appdata "<path>"` (new constant) to the `runas` relaunch arguments.
3. `src/ProoveGaming.Uninstaller/UninstallerConstants.cs` — add the argument-name
   constant.
4. `src/ProoveGaming.Uninstaller/UninstallService.cs` — parse the argument; in
   `DeleteUserDataFolder`, prefer the forwarded path for the LocalAppData delete (keep
   `GetFolderPath` as fallback and for ProgramData); move
   `if (deleteUserData) DeleteUserDataFolder();` to run immediately after
   `TryTerminateRunningAppProcesses`; wrap the service-removal stage so an sc.exe throw
   no longer aborts the remaining stages (still return a failure exit code).
5. `src/ProoveGaming.App/ViewModels/NotificationItemViewModel.cs` — add
   `DateTime Timestamp` (item keeps the preformatted `Time` string for display).
6. `src/ProoveGaming.App/ViewModels/NotificationGroupViewModel.cs` — **new**; header text
   + `ObservableCollection<NotificationItemViewModel>`.
7. `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` — stamp `Timestamp` in
   `AddNotification`; maintain `NotificationGroups`; add `DeleteAllNotificationsCommand`
   (unsubscribes item handlers, clears both collections, re-raises the three derived
   props); route the existing per-item delete through group maintenance; extract the
   header-selection logic (date → Today/Yesterday/formatted) into a testable static
   helper.
8. `src/ProoveGaming.App/Views/MainWindow.axaml` — bind the trash button to
   `DeleteAllNotificationsCommand`; replace the hardcoded divider + flat ItemsControl
   with an ItemsControl over `NotificationGroups` whose item template renders the
   existing divider (header text bound) plus the nested items ItemsControl (existing item
   template unchanged).
9. `src/ProoveGaming.App/Views/HomeView.axaml` — greeting TextBlock: add
   `TextTrimming="CharacterEllipsis"` and `ToolTip.Tip="{Binding GreetingText}"` so the
   full name stays reachable.
10. `src/ProoveGaming.App/Resources/Strings.en.axaml` — reword the release-point hint to
    depth semantics; uppercase `DeviceDetails_Actuation_ModeAllKeys` / `_ModePerKey`; add
    `Notifications_Yesterday`.
11. `src/ProoveGaming.App/Resources/Strings.uk.axaml` — same three changes in Ukrainian.
12. `src/ProoveGaming.App/Views/DeviceDetails/Sections/ActuationSectionView.axaml` — wrap
    the root StackPanel in the hidden-bar ScrollViewer exactly as
    `RgbLightingSectionView.axaml:97-109` (Hidden/Disabled, `Width="348"`,
    `ClipToBounds="False"`).

## Acceptance criteria

- [ ] Given a machine where the app has a saved sign-in, when the uninstaller runs with
  "Delete user profiles and settings" checked and elevation is granted (same or different
  admin account), then `%LocalAppData%\ProoveGaming` of the **interactive** user is gone
  after the uninstall completes.
- [ ] Given the same run, when the DeviceHost service removal fails (e.g. sc.exe error
  other than not-installed/not-started), then the user-data folder is still deleted and
  the uninstaller reports failure rather than silently succeeding.
- [ ] Given a fresh reinstall after such an uninstall, when the app starts, then it shows
  the Welcome/registration screen, not Home.
- [ ] Given a signed-in user whose nickname makes the greeting exceed the popup width,
  when the profile dropdown opens, then the greeting ends in an ellipsis inside the
  204-px panel and the full text appears as a tooltip.
- [ ] Given the actuation section for Cyrex/Aress/T800, when the user reads the
  release-point hint (en or uk), then it states that the value is a depth and that a
  larger value releases the key with less upward travel; slider values sent to the device
  are unchanged byte-for-byte from before this change.
- [ ] Given notifications created on different calendar dates (simulated in unit tests by
  stamping `Timestamp`), when the panel renders, then each date has its own divider —
  "Today", "Yesterday", or the formatted date — with newest date first, and every
  notification sits under its own date.
- [ ] Given at least one notification, when the trash (delete-all) button in the panel
  header is clicked, then the list empties, the empty state appears, and the bell badge
  disappears.
- [ ] Given the actuation section on a window at MinHeight (880), when the user scrolls
  the section, then the "Quick release" slider is reachable; no visible scrollbar is
  shown (matching the other device-details sections).
- [ ] Given the actuation section in en and uk, when the mode toggle renders, then "ALL
  KEYS / PER-KEY" ("ВСІ КЛАВІШІ / ОКРЕМІ КЛАВІШІ") display in the pixel font with no
  fallback-font mixing.

## Test strategy

All automated coverage goes in `tests/ProoveGaming.App.Tests` (xUnit, Avalonia headless
precedents: `DeviceCardConnectionTooltipTests.cs`, `CountrySelectViewTests.cs`).

- **Bug 3/4 (fix-\*, repro-first):** `NotificationGroupingTests.cs` is written before the
  VM change and must fail on current code:
  - header selection helper: today's date → Today key, yesterday → Yesterday key, older →
    formatted date; midnight boundary (23:59 vs 00:00) lands in different groups;
  - adding items out of order still yields newest-group-first, newest-item-first inside a
    group (existing `Insert(0, …)` behavior preserved);
  - delete-all: clears items and groups, `HasNoNotifications` true,
    `UnreadNotificationsCount` 0, `HasUnreadNotifications` false;
  - per-item delete removes an emptied group.
  If constructing `HomeViewModel` proves too heavy (ctor needs
  `IUsbHidDeviceService`, `SessionService`, `LocalStorageService`, `UserProfile`,
  `IServiceProvider`, `SystemNotificationService`, `ILogger`), the grouping/clearing
  logic is extracted so the tests target the helper + group VM directly — that extraction
  is part of item 7.
- **Bug 2:** copy-only; the existing `DeviceDetailsActuationViewModelTests.cs` must stay
  green untouched, proving values are unchanged.
- **Bugs 1, 5, 6 and the greeting trim — deliberately not auto-tested:** uninstaller
  elevation crosses processes and UAC (no test project exists for the Uninstaller and a
  headless test cannot elevate); the ScrollViewer wrap, resource casing, and
  `TextTrimming` are XAML-only with no logic to assert — they are covered by the QA
  checklist below.

## QA (manual test steps)

1. On a Windows account with a saved sign-in in the Proove Gaming app (sign in with
   "Remember me" once), close the app. Run the uninstaller, tick **Delete user profiles
   and settings**, confirm the UAC prompt, finish the uninstall. Expected: the folder
   `%LocalAppData%\ProoveGaming` no longer exists.
2. Repeat step 1 on a PC where the UAC prompt asks for a **different** admin account's
   password (standard user + separate admin). Expected: the folder is still deleted from
   the signed-in user's profile.
3. Install the app again and launch it. Expected: the Welcome/registration screen opens;
   you are NOT signed in automatically.
4. Sign in with a long nickname (20+ characters). Open the avatar menu in the top-right
   corner. Expected: the greeting "Hello, <nickname>" is cut with "…" inside the popup
   and hovering it shows the full name; nothing overflows the popup border.
5. Connect a Cyrex or Aress keyboard, open its settings → "СИЛА НАТИСКАННЯ". Read the
   "Точка відпускання" description. Expected: the text explains that a larger value
   makes the key release after less upward movement (matches actual behavior — verify by
   setting 3.5 mm and 0.5 mm and feeling the difference).
6. In the same section, shrink the app window to its minimum height. Expected: the
   section scrolls with the mouse wheel and the bottom "Швидке відпускання" slider is
   reachable; no scrollbar appears (same as the RGB section).
7. Still in the section, look at the "ВСІ КЛАВІШІ / ОКРЕМІ КЛАВІШІ" toggle in both
   Ukrainian and English. Expected: both labels use the same pixel (gaming) font as the
   "СТАНДАРТНІ НАЛАШТУВАННЯ" button — no ordinary font mixed in.
8. Connect and disconnect a device several times to generate notifications, open the
   bell panel. Expected: entries appear under a "Сьогодні / Today" divider.
9. If possible, leave the app running past midnight (or rely on the automated tests for
   date boundaries), reopen the panel. Expected: older entries move under
   "Вчора / Yesterday" or a date divider.
10. Click the trash icon in the notification panel header. Expected: all notifications
    disappear, the "All clear" empty state shows, and the red unread badge on the bell is
    gone.

## Review focus

1. `installer/uninstaller` — the elevation relaunch argument contract and the reordered
   deletion stages; verify a failed sc.exe no longer skips data deletion and exit codes
   still reflect failure.
2. `UI-thread affinity` — notification groups are mutated from device-event callbacks;
   confirm every mutation path already marshals to the UI thread (existing
   `AddNotification` callers) and the new clear path does too.
3. `localization` — all three resource changes land in **both** en and uk; no key used
   from XAML is missing in either file.
4. `null-safety` — the forwarded LocalAppData argument may be absent (old-style launch,
   quiet path): `DeleteUserDataFolder` must fall back rather than throw.

## Performance & quality

No hot paths. Notification grouping runs per notification-add (rare, UI thread) and must
stay allocation-light but needs no pooling. The uninstaller change adds one string
argument to an existing process launch. The ScrollViewer wrap adds standard Avalonia
virtualization-free scrolling over ~10 controls — negligible. No shared singleton state
is touched beyond `HomeViewModel`'s existing UI-thread collections; no new sync-over-async
anywhere (uninstall stages stay async).

## Risks

- **Uninstaller is release-critical**: it ships embedded in the installer; a regression
  here surfaces only in installed builds. Mitigate with the manual QA steps 1–3 on a real
  install, not `dotnet run`.
- DEBUG builds of the uninstaller mock the whole uninstall
  (`Views/MainWindow.axaml.cs:79-82`) — QA verifying on a Debug build would see the bug
  "persist". QA must use a Release-published installer.
- Reordering deletion stages means user data is removed even when a later stage fails and
  the product stays partially installed. This is the intended trade-off (the checkbox is
  an explicit user instruction), but reviewers should confirm.
- The grouped-notifications XAML lives in the `x:CompileBindings="False"` overlay —
  binding typos compile silently; verify at runtime.
- Uppercasing the toggle labels changes visible copy; if the design later wants
  sentence case, the pixel font itself lacks the glyphs — that becomes a font-asset task,
  not a resource change.

## Dated notes

- *2026-08-18 (PG-494 review round).* Four follow-ups from the branch review:
  - *USB priority, disconnect guard.* Suppressing `HasBluetooth` while a cable is live had
    disarmed `HandleUsbHidDeviceFoundAsync`'s guard — pulling the cable from a board still
    working over Bluetooth marked its card disconnected and raised a false notification. The
    card now carries `BluetoothLinkUp` (the link's REAL state, never suppressed by the cable)
    next to the display flag; the guard reads it and resurfaces the Bluetooth icon when the
    cable goes. The whole rule set is extracted as
    `HomeViewModel.ResolveTransports` (internal static, same technique as
    `ShouldShowCharging`) and covered by `HomeViewModelTransportPriorityTests` — including
    the cable-unplug-while-BT-alive scenario.
  - *Rampage overlay contrast.* `KeyOverlayArtDim` moved out of the ViewModel's hardcoded
    name check into the device catalog where every other per-board presentation value lives:
    a new `key_overlay_art_dim` proto field (37) flows DeviceHost appsettings
    (`"KeyOverlayArtDim": 0.35` on Rampage) → `DeviceCatalogEntry` → card → details VM. An
    older DeviceHost sends 0 → no scrim.
  - *Charging icon.* The Smart Infinite `0x1F` charge-state byte reads `is 1 or 2` now:
    the RY5088 sheet documents 2 as "fully charged", which still means the cable is in and
    feeding the device, and the wired path keeps its ⚡ for as long as the cable is live —
    dropping the icon on 2 read as "charging interrupted" rather than "done".
  - *Style naming.* `pgSectionScroll` was named for all sections but carried Presets'
    header offset (74) — renamed `pgPresetsScroll` so the next section with a scrollbar
    doesn't inherit a foreign margin (CLAUDE.md "name by role").

- *2026-08-18 (PG-494, card hover jitter).* The Home card's hover lift
  (`translateY(-10px)`) was applied to the card CONTROL, which moves its hit-test area
  with the visuals — a pointer entering across the bottom edge ended up outside the
  lifted card, exited, dropped it, re-entered, and the card oscillated. `DeviceCardBase`
  now lifts the card's CONTENT (`SetLift`) while the control itself keeps a transparent
  background (stable hit rect) and `ClipToBounds=false` (the lifted content renders above
  the control's bounds); the `TransformOperationsTransition` moved from the UserControl to
  each card's root element in all five card views. Verified by pixel measurement: card top
  180 resting, 170 hovered, and a steady 170 across frames with the pointer parked in the
  bottom strip — the jitter case.

- *2026-08-18 (PG-494, Rampage overlay contrast).* The Rampage artwork's keycaps are
  saturated violet-blue even with `ShowBacklight=False` (its physical caps are), so the
  `#6357FF` hover/selection boxes on Key Assignments / Macros blended into the board. The
  interactive overlay's art layer now binds `DimOpacity` to a per-board
  `KeyOverlayArtDim` (`0.35` for Rampage — tuned down from an initial 0.55 per user
  feedback — `0` for the already-dark boards), reusing the
  scrim the home card uses for disconnected boards. Verified live: the hover box reads
  clearly on the dimmed board and the legends stay legible.
  *Superseded value (2026-08-19):* QA reported the 0.35 board as too dark on Key
  Assignments / Macros ("клавіатура дуже темна"). Retuned to `0.15` after a side-by-side
  render of the real split artwork with mock `#6357FF` boxes at 0 / 0.15 / 0.25 / 0.35:
  at 0.15 the caps read close to the artwork's natural brightness while the selected
  (0.4-fill) and hover (0.2-fill + border) boxes still separate from the caps; at 0 the
  box fills all but vanish into them — the scrim stays, only thinner.

- *2026-08-18 (PG-494, USB priority).* `HomeViewModel.FillConnectionCard`'s comment
  declared "wired > bluetooth > dongle" but the code did the opposite
  (`HasWire = hasWired && !bluetooth`) — a Bluetooth report, live or the OS stack's
  well-documented stale ghost, kept the card off USB after the user switched the device
  to cable. Now a live wired data interface wins unconditionally, and
  `FillBluetoothCard` only accepts a Bluetooth "connected" when no cable is live (the
  same rule `ApplyBluetoothIdentity` already carried). Verified live: a Rampage switched
  BT→USB flips its card to the cable icon.

- *2026-08-18 (PG-494, charging icon).* The Smart Infinite `0x1F` battery reply carries
  the charge state in its second payload byte — `[9]`, with `[7]`=0x55 status and
  `[8]`=level — while `ParseBatteryResponse` read the flag from `[3]` (the frame's
  addr-hi, always 0), so a keyboard charging over its 2.4G link never showed the ⚡.
  Measured on a charging Rampage (log dump `06-1F-00-00-00-00-00-55-55-01-…`); scoped to
  replies echoing `0x1F` so other families' parsing is byte-for-byte unchanged. The
  wired path's ⚡ (IsCharging = hasWired) was verified live at the same time.

- *2026-08-17 (PG-589, macros/presets list overlap).* The keyboard-device section panel in
  `DeviceDetailsView.axaml` hosted its sections with `Margin="42,16,42,12"` — a 12px bottom
  inset against a chrome whose bottom band starts at y=714 (42 from the panel's 756 bottom),
  so long lists (Macros, Presets) scrolled their rows 30px over the band's diagonal
  ornament. The mouse panel already used bottom 42 and never showed the defect. Fixed by
  aligning the keyboard panel's bottom margin to 42: the scroll viewport now ends exactly
  where the band begins, for every keyboard section at once. *Superseded detail (2026-08-17,
  PG-600…603):* the Macros and Presets list ScrollViewers later gained their own
  `Margin="0,0,0,12"`, so those two viewports now end 12px above the band — one shared edge
  for the clip line, the scrollbar track bottom and a fully-scrolled last row; the panel
  margin itself stays 42.

- *2026-08-17 (PG-589 item 3).* The card kebab menu (`DotsOverlay` in `SingleCard` /
  `DoubleCard` / `InactiveDeviceCard` / `InactiveDoubleCard`) sized itself with a fixed
  `Width="204"` measured off the en strings — the uk "Змінити профіль" row rendered flush
  against the right edge. All four copies now use `MinWidth="204"` with the chamfered
  background rebuilt as a stretching `16,*,16` grid (same construction as the macroTab
  fix), so the widest localized row sizes the menu and 204 only floors it. This is the
  CLAUDE.md "Localization-safe sizing" rule applied to the menu.
  *Superseded detail (2026-08-19):* the `MinWidth` change alone only fixed the double
  cards. In `SingleCard` and `InactiveDeviceCard` the overlay sat inside the content
  grid's 42px right column (`ColumnDefinitions="42,*,42"`), whose measure constraint
  collapsed the natural width back to the 204 floor — the uk "Змінити профіль" row still
  overhung the background on mouse cards. Fixed by hoisting `DotsOverlay` out of that
  column to a direct child of the card root (same alignment and `Margin="0,60,20,0"`),
  matching where `DoubleCard` / `InactiveDoubleCard` already host it.

- *2026-08-17 (PG-589 item 1).* The HomeView card-grid ScrollViewer this plan's review
  covered as "outside this plan" regressed the cards' hover lift: `translateY(-10px)`
  (DeviceCardBase) rendered a first-row card above the viewport, and the ScrollViewer's
  `ScrollContentPresenter` clips at the viewport edge regardless of the surrounding
  `ClipToBounds="False"`. With the whole 24px top inset moved OUT of the scrollable
  content (the redlines refactor), the clip line sat exactly on the cards' top edge and
  the rising card was visibly cut. Fixed in `HomeView.axaml` by splitting the inset:
  ScrollViewer outer top margin 24 → 14 plus `Margin="0,10,0,0"` on the ItemsControl
  (inside the clip), so the lift has 10px of headroom; the vertical ScrollBar gained the
  same 10 in its top margin to keep the track at the redline 24. Extent and viewport grow
  by the same 10px, so the no-scroll fit and bar appearance are unchanged.

- *2026-08-17 (PG-600, PG-601, PG-602, PG-603 — visible section scrollbars).* This
  supersedes the "no device-details section shows a bar" rationale under "Options
  considered → Bug 5 → B": QA asked for visible scroll indicators, so the long
  device-details lists (Presets, Macros) and both lists in `KeyAssignmentDialog` now show
  the shared 4px `pgScrollViewer` bar (`AllowAutoHide="False"`) instead of a hidden one.
  To make room without shrinking rows, the section viewports widened to 348 with the bar
  in the extra 12px: the section host cells in `DeviceDetailsView.axaml` extend 12px
  further right, and `pgSectionScroll` / `pgMacrosScroll` styles in `App.axaml` start the
  track at the list's first row. The actuation section itself keeps its hidden bar — Bug
  5's own fix is unchanged.

- *2026-08-17 (PG-605 — Profile localization).* `ProfileViewModel` composed "Joined:" and
  the empty-email fallback from hardcoded en strings. Both now come from resources
  (`ProfileView_Joined_Format`, reusing `ProfileView_EditEmail_Placeholder`), and the VM
  re-raises the computed properties on `LocalizationManager.LanguageChanged` since
  VM-computed strings don't ride `DynamicResource`. The unused
  `HomeView_LogoutConfirm_CancelButtonWidth` resource (en/uk) was removed with it.

- *2026-09-30 (QA - Aress keycap smears).* The legend-strip pass recorded in Round 2 left
  two retouch marks on the Aress keycap raster: a dark smear across the **A** cap and
  light/dark bands across the top of the **;** cap, both visible on every Aress settings
  tab and on the home card. The keycaps themselves are identical 1u renders, so each
  damaged cap was replaced with its right-hand neighbour (S, and the apostrophe key),
  cloned at exactly one key pitch - 164 raster px (82.164 viewBox units / the .501 image
  scale) - and feathered 5px at the tile border, which falls in the dark gap between caps.
  Nothing outside those two 157x156 tiles changed, and the hotspot tables are untouched
  since no key moved. Both embedded copies were fixed: `PG_Aress_keys.svg` and
  `Card/PG_Aress_card.svg` (they carry the same raster). The delivered
  `PG_Aress_Keyboard_SVG_black.svg` is referenced by nothing and was left as the untouched
  record of what shipped. Re-encoding also reclaimed the Round-2 nit about the bloated
  strip encode: the embedded PNG is ~1.0 MB again, down from ~2.2 MB. Each SVG carries an
  XML comment above its `keys-image` describing the repair, so a future re-split from a
  delivered file knows it has to be redone.

## Open questions

1. Bug 2: product may instead want the slider direction inverted (option B) despite the
   preset/DB mismatch it creates — confirm the copy-only fix is acceptable before
   implementation. **Default if no answer: copy-only fix (option A).**
2. Bug 1: is cross-account elevation actually the QA machine's setup? The fix is correct
   either way (ordering hardening covers the same-account case), but confirming would
   validate the diagnosis.
3. Notifications date format for "older than yesterday" — proposal: culture-formatted
   short date (`d` pattern for the active UI culture). Any design preference?

## Review findings

### Round 1 — Ship: yes

#### Summary
The working tree implements all six QA fixes from this plan plus the earlier in-session
HomeView card-grid fix (shared `pgScrollViewer` style, 16px card gap, home ScrollViewer)
that predates this plan. Solution builds clean; 719/719 App tests pass including the 7 new
grouping tests.

#### Plan conformance
Plan: docs/plans/fix-qa-bug-batch/fix-qa-bug-batch-plan.md
- Implemented as planned: all 12 file-level items, in the planned order; repro-first tests
  target the extracted static helpers exactly as the Test strategy allowed.
- Deviates from plan: none within the plan's scope.
- Outside this plan (not scope drift): `App.axaml`, `Views/HomeView.axaml` (ItemSpacing/
  ScrollViewer), `Views/DeviceDetails/Sections/MacroRecorderStepsView.axaml` belong to the
  earlier HomeView display fix requested and runtime-verified before this plan existed.
  Reviewed here anyway — the `ScrollViewer.pgScrollViewer ScrollBar:vertical` selector shape
  matches the proven `AgreementTermsView.axaml` precedent, so the restyle is sound; the
  Create Macro dialog's steps scrollbar should still get one manual look since it changed
  from scoped styles to the shared class.
- Accepted limitation (per plan): delete-all's unread-badge reset is manual-QA-only (the
  grouping tests drive static helpers because HomeViewModel's ctor needs the full service
  graph) — residual risk is low; the command body is four straight-line statements.
- Accepted limitation (per plan): DEBUG uninstaller builds mock the whole uninstall —
  residual risk: QA on a Debug build will wrongly reproduce the bug. Release-publish only.
- Acceptance criteria: all met in code; the three uninstaller criteria are verifiable only
  on an installed Release build (as the plan itself states).

#### Code
- none — no Blockers.
- [x] **S1. Date headers go stale across midnight until a new notification arrives** —
  `src/ProoveGaming.App/ViewModels/NotificationGroupViewModel.cs:52-63` — Should-fix
  **Problem / why it matters:** headers are recomputed only inside `Insert` when a new
  group is created. Leave the app running overnight with no device events after 00:00:
  opening the panel at 09:00 still shows yesterday's items under "Today"/"Сьогодні" — the
  exact class of bug ticket 3 complains about, in a narrower window.
  **Fix:** when `HomeViewModel.IsNotificationsOpen` transitions to true, re-run
  `group.Header = GroupHeaderFor(group.Date)` over `NotificationGroups` (one cheap loop on
  the UI thread; `Header` already raises change notifications).
- [ ] **S2. Verify uninstaller changes with /security-review before release** — Should-fix
  (process). The diff touches an elevation path: the new `--user-local-appdata` argument
  points an admin-elevated process at a directory to delete. Exposure is bounded — the code
  only ever deletes the `ProoveGaming` subfolder of the given path
  (`UninstallService.cs:DeleteDataFolder`), and the pre-existing install-path argument
  already allowed recursive deletion of an arbitrary directory, so this does not widen the
  surface materially — but the repo's review policy asks for the built-in `/security-review`
  on any installer/uninstaller diff. Run it on this branch before the next installer
  release.

#### Tests
- none beyond the plan-accepted gap noted above; the 7 new tests exercise real behavior
  (header kinds, midnight boundary, out-of-order dates, rollover relabel, group removal),
  not defaults. Reminder: CI (`dotnet-ci.yml`) does not run `tests/` — keep running them
  locally.

#### Nits
- A hand-typed `ProoveGaming.Uninstaller.exe --uninstall` (no path) now misparses the
  appended `--user-local-appdata` flag as the install path in
  `UninstallService.TryGetInstallPathFromArgs` (`UninstallService.cs:135-146`). Harmless in
  practice (the registered UninstallString always carries the path,
  `MainWindow.Installation.cs:713-723`), but skipping values that start with `--` would
  make it robust.
- `groups.FirstOrDefault(g => g.Items.Remove(item))` mutates inside a predicate
  (`NotificationGroupViewModel.cs:69`) — correct, but a `foreach` with an explicit remove
  would read more honestly.
- The `catch { serviceRemovalFailed = true; }` in `UninstallService.RunAsync:56-63` drops
  the exception detail entirely; the uninstaller has no logging infrastructure, so this is
  acceptable today, but a `Debug.WriteLine` would cost nothing.
- Card hover glow/shadow now clips at the home ScrollViewer's viewport edges
  (`HomeView.axaml:194-200`) — inherent to making the list scrollable; only visible during
  hover at the extreme top/bottom rows.
- Group headers (like notification titles before this change) are localized eagerly and do
  not re-render on a live language switch — consistent with the panel's existing behavior,
  pre-existing limitation.

#### What's done well
- The uninstaller fix addresses both root causes (profile mismatch and stage ordering)
  with a minimal, arg-compatible contract change, and failure still reports exit 1 into an
  existing graceful UI path (`Views/MainWindow.axaml.cs:112-120`) instead of the previous
  unhandled `async void` throw.
- Grouping logic lives in static helpers driven by both production and tests — the
  midnight/rollover cases are genuinely covered, not mocked away.
- Release-point fix correctly avoids touching stored values — `DeviceDetailsActuationViewModelTests`
  pass untouched, proving wire compatibility.
- Every `AddNotification` call site already marshals via `Dispatcher.UIThread`
  (`HomeViewModel.cs:335,437,563,568,887`), so the new group mutations inherit correct
  UI-thread affinity with no extra work.

#### Verdict
Ship: yes — no blockers; S1 is a small, contained follow-up and S2 is a process step
before the next installer release. First: 1) S1 header refresh on panel open, 2) run
`/security-review` on the uninstaller diff, 3) one manual pass over the Create Macro
dialog scrollbar and QA steps 1–3 on a Release-published installer.

### Round 2 — Ship: yes

#### Summary
Reviews the full PR #93 delta: 9 commits, 34 files, +1497/−292. Since Round 1 the branch
gained: fixes for the external PR-review findings (M1 service-removal orphan, M2
cross-account shortcuts/registry via forwarded SID and shell-folder paths, minors m1–m8 and
m12); the Aress artwork rework (T800-style viewBox band crop with shifted glow/legend
paths, its own measured hotspot table `AressArtworkLayout` so Key Assignments / Macros draw
the Aress board, baked legends stripped from the keycap raster, FN glyph added to the
legend layer); three actuation behaviour fixes (Discard restores the preset snapshot or,
for a rows-less preset, the board's at-open probe state — narrowly scoped so a plain preset
switch still writes nothing; edit-back-to-saved clears the Save Preset flag via the shared
recompute; per-key hint no longer wraps in uk); actuation section typography/compaction
(16px titles at LineHeight 22, one-line hints in en+uk, uniform 16px spacers, no font-weight
jump on the mode toggle); the 668px double card with centred card grid; guest-macros no
longer migrate on sign-in (registration only); and wrapped two-row nav labels.

#### Plan conformance
Plan: docs/plans/fix-qa-bug-batch/fix-qa-bug-batch-plan.md
- Round 1 items: S1 closed (`[x]`, RelabelAll + panel-open refresh + test). S2 remains open
  — carried below. Nits m2/m3/m8-equivalents from the external review are closed on this
  branch; the Round 1 nit list is otherwise unchanged.
- Scope grew well beyond the original six QA tickets, but every addition traces to an
  explicit user request or the external PR review — no silent drift. The plan document
  covers only the original batch; this journal is the record for the rest.
- Verification: final tree passes 726 App + 594 Infrastructure tests; App and Uninstaller
  build clean (0 warnings in Uninstaller). Aress crop, hotspot registration and legend
  cleanup were verified by offline renders; HomeView packing by live screenshots.
- Accepted deviation: actuation titles are now 16px against the 18px used by sibling
  sections — an explicit user decision for vertical fit (do not "fix" back without asking).

#### Code
- none — no Blockers.
- [ ] **S2 (carried). Run `/security-review` on the uninstaller before the next installer
  release** — the elevation-path surface grew again in Round 2: the elevated process now
  also opens `HKEY_USERS\<forwarded SID>` and deletes shortcuts at forwarded shell-folder
  paths (`UninstallService.cs`). Exposure remains bounded (fixed subkey path, fixed .lnk
  names, fully-qualified-path validation on folder args), but this is exactly what the
  built-in review is for.
- [ ] **S3. Manual visual pass over surfaces changed without live verification** — the
  grouped notifications panel (date dividers, delete-all), the Agreement step scrollbar
  (now the shared pgScrollViewer with hidden repeat buttons), the Create Macro steps list
  scrollbar, and the final actuation section (16px titles, one-line hints, uk per-key
  mode). All are XAML/resource-only and build clean, but none could be exercised against a
  live dialog/board from this session's environment.

#### Tests
- none missing for the new logic: grouping (8), actuation preset flows (+3 incl. the
  probe-state Discard and the switch counter-guard), overlay gates (+4 for Aress's own
  table). Reminder from Round 1 stands: CI does not run `tests/` (external review m11) —
  keep running locally until the workflow is switched to the slnx.

#### Nits
- `PG_Aress_keys.svg`'s embedded PNG grew ~1.1 MB → ~2.2 MB after the legend-strip
  re-encode (System.Drawing's PNG encoder) — an optipng/zopfli pass would reclaim most of
  it if asset size starts to matter.
- `ActuationSectionView.axaml`'s scroll-shell wrapper tags still sit at content depth
  (external review m6's cosmetic half) — readability only.
- External review m9 (release depth is not clamped to press depth; the shortened hint
  still implies larger = sooner) and m10 (notification Title/Subtitle don't re-localize on
  live language switch) remain open product/UX questions, deliberately untouched.

#### What's done well
- The actuation Discard semantics landed narrowly: the "no-opinion presets must not reset
  a tuned board" invariant kept its guard test while the Discard path got its own
  probe-state restore — the pair of tests pins both sides of the trade-off.
- The Aress artwork chain (crop → shifted paths → measured table → overlay switch → legend
  strip → FN glyph) kept every derived artifact consistent, with generated-data sanity
  tests guarding the table against silent regeneration drift.
- Dirty-flag recompute reuse (`StagePresetDirtyOnly`) fixed the stuck Save Preset button
  with one call-site change instead of a parallel mechanism.

#### Verdict
Ship: yes — no blockers; S2 and S3 are pre-release process steps, not code changes.
First: 1) run `/security-review` on this branch, 2) S3 visual pass (QA checklist in this
plan plus notifications/Agreement/macro-dialog scrollbars), 3) decide external m9/m11.
