# Fix: profile field validation, date picker localization, and password rules

> **Status update (2026-07-31): fix 5 (password rules) was reverted at the user's request and
> is no longer part of this change.** Testing showed the Launcher API still rejects
> letters-and-digits passwords with `PASSWORD_NO_UPPERCASE` from both `api/check_email` and
> `api/register`, so relaxing the client rule alone could not make such a password register —
> it only moved the rejection later in the flow. Everything relating to fix 5 below
> (Scope item 5, its change-list rows, acceptance criteria 13–15, its test strategy and QA
> steps 12–14) is retained for the record but **not implemented**. See
> [Round 1 follow-up](#round-1-follow-up--fix-5-reverted). Fixes 1–4 are unaffected and stand
> as written.
>
> **A sixth and seventh fix were added after the plan was written** — nickname suggestion chips
> silently dropping underscores (fix 6), and guest-mode header changes (fix 7). Both are
> described in Scope, Options considered, and the change list, but both landed after the Round 1
> review and are **not yet reviewed**.
>
> **Scope drift note.** This plan is titled for profile/auth validation, and fixes 6–7 sit
> outside that: fix 6 is a shared-control rendering bug and fix 7 is guest-mode header work in
> `HomeView`. They are recorded here because they were requested while this branch was open. If
> the branch is split before merge, fix 7 in particular belongs in its own plan.
>
> **Superseding note (2026-08-14).** A QA report against the nickname dialog ("after clicking a
> suggestion chip, or after switching to another app and back, the field cannot be erased")
> was fixed outside this plan. Root cause was app-wide, not dialog-specific: Avalonia renders
> each `Popup` as a separate non-activatable native window on Windows, and a deactivate→activate
> cycle of the main window restores keyboard focus into the window's own scope, so TextBoxes in
> any open popup silently stop receiving keys. Fixed by enabling
> `Win32PlatformOptions.OverlayPopups` in `Program.cs` (popups now render inside the main
> window / single focus scope) plus a `Click` handler on all six suggestion chips
> (`ProfileView`, `ChooseNicknameView`) that returns focus to the nickname TextBox with the
> caret at the end. The chips' behaviour described in fix 6 is otherwise unchanged. See the
> "Popup keyboard focus" change-list section and QA steps 19–22 for what this touches and how
> to regression-check it — `OverlayPopups` changes how **every** popup in the app is positioned,
> not just these dialogs.
>
> **Superseding note (2026-08-21).** `SettingsButton` in `HomeView`'s header is **no longer an
> inert placeholder**: the `add-settings-view` plan (`docs/plans/add-settings-view/`) wired it
> (`Cursor="Hand"` + `Click`) to open the new Settings page. Wherever this plan's prose or
> Review findings describe `SearchButton` *and* `SettingsButton` as the two placeholders, only
> `SearchButton` (and the guest `HelpButton`) still are. The findings journal below is left
> as written — it was accurate at review time.

## Goal

Five defects reported against the Profile and Welcome (auth) screens are fixed in one change:
the shared date picker still renders its calendar in hardcoded English and formats the selected
date as `MM/dd/yy`; the profile nickname dialog silently swallows the server's
`NICKNAME_ALREADY_EXISTS` conflict so a taken nickname looks accepted; the Date-of-Birth and
Country fields never show the blue "filled" border the sibling text fields show; the
forgot-password flow gives no toast confirmation when the reset link is sent; and the sign-up
password rule enforces an 8-character/uppercase/digit policy that product no longer wants.
Together these are the difference between "the profile screen looks finished" and "the profile
screen behaves like the rest of the app".

## Scope

**In scope**

1. `DatePickerView` day-of-week header, month names and popup header label come from the en/uk
   resource dictionaries; the selected-date field renders `DD/MM/YYYY`.
2. Nickname uniqueness (and every other nickname field error) from
   `api/profile/check_nickname` / `api/profile/update` is surfaced **inline in the nickname
   dialog**, not as a toast the dialog covers.
3. `DatePickerView` and `CountrySelectView` show the same filled/hover/idle border states as the
   other profile fields, via one shared style class instead of three copies.
4. A success toast after `SEND RESET LINK` succeeds.
5. ~~Sign-up password rule relaxed to "Latin letters and digits only, any length"; hint and error
   copy updated in en + uk.~~ **REVERTED — see the status note at the top.** The client rule,
   the hint and the error copy are all back to their pre-change values in both languages.
6. **Added 2026-07-31, after the plan was first written.** Nickname suggestion chips render an
   underscore-bearing nickname incorrectly: the chip drops the `_` and underlines the next
   character, so it disagrees with the field it fills in. Fixed for all six chips (three in
   `ChooseNicknameView`, three in the profile nickname dialog) via the shared
   `suggestionButton` style.
7. **Added 2026-07-31, after the plan was first written.** Guest-mode header in `HomeView`:
   a new help icon immediately right of search (guest-only), and the notification bell —
   previously hidden for guests — made visible in guest mode, with its unread badge.

**Out of scope**

- Any server-side change. The Launcher API keeps its own password policy (see **Risks**).
- The guest-session dead end in `ConfirmEditNickname` (a guest with no `SessionToken` clicks
  Confirm and nothing happens at all). Called out in **Risks**; not fixed here.
- Making toasts render above `Popup` overlays in general. Fix 2 sidesteps this for the nickname
  dialog; the same latent issue remains for other in-dialog `HandleProfileUpdateError` paths.
- Week-start day. The calendar grid stays Sunday-first (see **Open questions**).
- The nickname *rules* themselves (3–20 chars, `[A-Za-z0-9_]`) are unchanged — only the password
  rules change.

## Options considered

### Fix 1 — where the calendar strings come from

**Option A — culture-driven (`CultureInfo("uk").DateTimeFormat`).** Two-line change: pick a
`CultureInfo` from the persisted language code and read `AbbreviatedDayNames` /
`MonthNames`. Cheap, but it is exactly the "runtime string transformation" `CLAUDE.md` forbids,
gives no control over casing or the genitive/nominative month forms Ukrainian needs (`січня` vs
`січень`), and silently changes when the OS ICU data changes.

**Option B (recommended) — explicit resource keys.** 31 keys (7 day abbreviations, 12 month
abbreviations, 12 full month names) in `Strings.en.axaml` / `Strings.uk.axaml`, addressed through
a constants table in `ProoveGaming.Common`. Verbose but it is the repo's stated rule, translators
control casing directly, and a missing key is a visible, testable condition rather than a subtly
wrong word form.

**Option C — one delimited resource string per group** (`"SU,MO,TU,…"`). Three keys instead of
31, but it reintroduces parsing of a magic-format string and a translator can silently break the
control by dropping a comma. Rejected.

### Fix 3 — where the field border states live

**Option A — add a `.filled` rule inside each of the two controls.** Smallest diff, but it makes
a third and fourth copy of the same four border rules (`ProfileView.profileBox`,
`DateContainer`, `CountryContainer`) — precisely what `CLAUDE.md` says to extract on the *second*
copy. It also leaves the existing drift (`#4CBEBCEA` vs `#4DBEBCEA`) in place.

**Option B — a class in `App.axaml` `Application.Styles`.** Deduplicates, but a control living in
`ProoveGaming.Common` would then render unstyled in any host that forgets the include — a shared
control must not depend on its host for its own chrome.

**Option C (recommended) — a `<Styles>` file in `ProoveGaming.Common/Styles/`, included by both
the two Common controls (via `UserControl.Styles` → `StyleInclude`) and by `App.axaml`.** One
definition, each Common control stays self-contained, and `ProfileView` drops its private
`profileBox` copy. Matches the existing `SharedCheckboxStyles` / `SharedActionButtonStyles`
pattern. Cost: it touches `ProfileView.axaml`, which the reported bug did not name — a deliberate,
stated widening of the diff to satisfy the repo's no-duplicate-UI rule.

### Fix 4 — how the "reset link sent" confirmation is delivered

**Option A — inline text on the panel only.** Already exists (`IsForgotSent` swaps in a
"Check your email" panel), which is presumably why nobody added a toast. But the report asks for
the notification, and the panel swap is easy to miss.

**Option B (recommended) — keep the panel swap *and* fire the existing `NotificationService`
toast**, the same mechanism every other success in the app uses (`ProfileView_Notification_*`).
Requires injecting `NotificationService` into `WelcomeViewModel`; it is already a registered
singleton, so hand-wired DI needs no edit — only the test factory does.

## Recommended approach

Take Option B for fix 1, Option C for fix 3, Option B for fix 4. Fixes 2 and 5 have no real
design choice:

- **Fix 2** mirrors the pattern `ConfirmEditEmail` already uses: resolve field-level errors via
  `ApiErrorMessages.ResolveAll(...)`, filter to `AuthField.Nickname`, put the message in
  `EditNicknameError` (which the dialog already renders and which already turns the field border
  red through `Classes.error`), and fall through to `HandleProfileUpdateError` only for network,
  token and unrecognized failures. Applied to **both** the `check_nickname` response and the
  subsequent `profile/update` response, since either can reject the nickname.
- ~~**Fix 5** replaces the `^[\x21-\x7E]{8,}$` regex with `^[A-Za-z0-9]+$` and rewrites the hint /
  error copy in both languages.~~ **Reverted — see the status note at the top.**

### Fix 6 — how to stop the chips eating underscores

Diagnosis first: the chip and the text field bind to the *same* property, so a visible mismatch
can only come from the rendering layer. `TextBox.Text` renders the string literally; the Fluent
`Button` template presents string `Content` through a `ContentPresenter` with
`RecognizesAccessKey="True"`, which treats `_` as a mnemonic marker — it removes the underscore
and underlines the following character. The stored value was always correct; only the chip's
display lied. Nicknames legitimately contain `_` (server rule `^[A-Za-z0-9_]+$`), so any
generated suggestion with one is affected.

**Option A (recommended) — turn access-key recognition off for the chip class.** One style rule
on `Button.suggestionButton`'s `PART_ContentPresenter`. The class is already shared by both
views, so all six chips are covered by a single change, and any future chip inherits the fix.

**Option B — give each chip an explicit `TextBlock` child instead of string content.**
`TextBlock` does no access-key processing, so it also works, but it needs editing six call sites
and adds markup. Rejected as more churn for the same result.

### Fix 7 — enabling notifications for guests

The question worth answering before flipping the binding was whether the notification feed is
session-backed, because a bell that opens an always-empty panel for guests would be worse than no
bell. It is not: `HomeViewModel.AddNotification` is only ever called from device connect,
disconnect and low-battery events (`HomeViewModel.cs:275, 359, 377, 447, 452, 473, 610, 671`), the
`Notifications` collection is in-memory, and nothing on that path reads a session token or calls
the Launcher API. The only `IsGuest` handling in the ViewModel (`HomeViewModel.cs:194-197`)
re-registers device cards and does not touch notifications. So removing the `!IsGuest` visibility
gate is sufficient — a guest gets a working bell with a real feed, and no ViewModel change is
needed.

The help icon is transcribed as an explicit `Path` with the supplied path data per the repo's
SVG convention, inside the same 56×56 chamfered background the sibling icons use, sized
`Width="24" Height="24" Stretch="Uniform"` so the 23×27 artwork scales to the same 24px height
as its neighbours rather than rendering 3px taller.

## File-level change list

### Localization plumbing (fix 1)

| File | Intent |
| --- | --- |
| `src/ProoveGaming.Common/Localization/ResourceStrings.cs` | **New.** `Get(string key, string fallback)` — resolves a key from `Application.Current` resources, returns `fallback` when absent (so a missing key or a headless test never paints a raw key into the UI). |
| `src/ProoveGaming.Common/Localization/DatePickerResourceKeys.cs` | **New.** Three `static readonly string[]` tables — `DayShort` (7, Sunday-first), `MonthShort` (12), `MonthFull` (12) — holding the literal key names. No interpolation, no runtime key construction. |
| `src/ProoveGaming.App/LocalizationManager.cs` | `GetString` delegates to `ResourceStrings.Get(key, key)` so the lookup exists once. Public behaviour (returns the key when unresolved) is unchanged. |
| `src/ProoveGaming.App/Resources/Strings.en.axaml` | Add the 31 date-picker keys (values already uppercased: `SU`, `JAN`, `JANUARY`), plus `WelcomeView_Notification_ResetLinkSent`. ~~rewrite `WelcomeView_PasswordHint` and `WelcomeView_Error_Password`~~ — reverted. |
| `src/ProoveGaming.App/Resources/Strings.uk.axaml` | Same key set, Ukrainian values (`НД`/`ПН`…, `СІЧ`…, `СІЧЕНЬ`…), plus the reset-link toast. ~~and the two password strings~~ — reverted. |

### Date picker (fixes 1 + 3)

| File | Intent |
| --- | --- |
| `src/ProoveGaming.Common/Controls/DatePickerView.axaml.cs` | Default `_displayFormat` → `dd'/'MM'/'yyyy` exposed as `internal const string DefaultDisplayFormat` (testable, and fixes the docstring/behaviour mismatch); day-of-week header text moved out of the constructor into a `RefreshDayOfWeekHeader()` called from `RenderCurrentMode()`; `RenderPeriod` reads `DatePickerResourceKeys.MonthShort`; `UpdatePopupHeaderLabel` composes `"{MonthFull} {year}"` from resources instead of `ToString("MMMM yyyy", InvariantCulture).ToUpperInvariant()`; cache the container `Border` in a field and toggle its `filled` class from `UpdateHeader()`. |
| `src/ProoveGaming.Common/Controls/DatePickerView.axaml` | `StyleInclude` the shared field-box styles; put `Classes="pgFieldBox"` on `DateContainer`; delete the now-shared idle/hover rules, keeping only the local `#DateContainer.open` override (declared after the include so it still wins). |

### Country select (fix 3)

| File | Intent |
| --- | --- |
| `src/ProoveGaming.Common/Controls/CountrySelectView.axaml.cs` | Cache the container `Border`; toggle its `filled` class from `UpdateHeader()` on `_selectedIndex >= 0`. |
| `src/ProoveGaming.Common/Controls/CountrySelectView.axaml` | Same `StyleInclude` + `Classes="pgFieldBox"` treatment; local `#CountryContainer.open` override stays. Its idle border `#4DBEBCEA` is dropped in favour of the shared `#4CBEBCEA`. |

### Shared style (fix 3)

| File | Intent |
| --- | --- |
| `src/ProoveGaming.Common/Styles/SharedFieldBoxStyles.axaml` | **New.** `Border.pgFieldBox` (transparent background, `#4CBEBCEA` 1 px border), `.pgFieldBox.filled` (`#6357FF`), `.pgFieldBox:pointerover` (`#786DFF`) — declared in that order so hover wins over filled. |
| `src/ProoveGaming.App/App.axaml` | `StyleInclude` the new file next to the two existing shared style includes. |
| `src/ProoveGaming.App/Views/ProfileView.axaml` | Delete the four private `Border.profileBox*` styles; the First/Last-name and Phone borders switch to `Classes="pgFieldBox"` keeping their existing `Classes.filled="{Binding …}"` bindings. |

### Profile nickname (fix 2)

| File | Intent |
| --- | --- |
| `src/ProoveGaming.App/ViewModels/ProfileViewModel.cs` | In `ConfirmEditNickname`: apply server suggestions whenever the response carries them; resolve `AuthField.Nickname` messages from the `check_nickname` failure into `EditNicknameError` and return with the dialog open; do the same for a nickname-field failure from `UpdateProfileAsync`; keep `HandleProfileUpdateError` for network/token/unknown. |

### Reset link toast (fix 4)

| File | Intent |
| --- | --- |
| `src/ProoveGaming.App/ViewModels/WelcomeViewModel.cs` | Take `NotificationService` as a fourth constructor parameter; on a successful `ForgottenPasswordAsync` show `WelcomeView_Notification_ResetLinkSent` alongside setting `IsForgotSent`. |

### Nickname suggestion chips (fix 6)

| File | Intent |
| --- | --- |
| `src/ProoveGaming.App/App.axaml` | Add `Button.suggestionButton /template/ ContentPresenter#PART_ContentPresenter` → `RecognizesAccessKey="False"`, next to the existing `suggestionButton` styles, so chip labels render `_` literally instead of consuming it as an access key. Covers all six chips in `ChooseNicknameView` and `ProfileView`. |

### Popup keyboard focus (2026-08-14 follow-up)

| File | Intent |
| --- | --- |
| `src/ProoveGaming.App/Program.cs` | Register `Win32PlatformOptions { OverlayPopups = true }` (exposed as the internal `WindowsPlatformOptions` field so the test below can pin it). Popups render inside the main window — one HWND, one focus scope — so window reactivation restores focus to a TextBox inside an open popup instead of into the empty window scope. **App-wide positioning change:** any popup that would overflow the window is now flipped/slid inside it; see QA steps 19–22. |
| `src/ProoveGaming.App/Views/ProfileView.axaml` + `.axaml.cs` | `Click="OnSuggestionClick"` on the three profile-dialog chips; the handler defers via `DispatcherPriority.Background` and puts focus + end-of-text caret into the generated `NicknameTextBox` field (typo in the name is now a compile error, not a silent no-op). |
| `src/ProoveGaming.App/Views/ChooseNicknameView.axaml` + `.axaml.cs` | Same handler for the three registration-step chips. |
| `tests/ProoveGaming.App.Tests/ProgramPlatformOptionsTests.cs` | **New.** Pins `OverlayPopups = true` so a `Program.cs` refactor cannot silently drop the flag (CI builds the App project, so a compile-visible field plus this test is the only automated guard). |
| `docs/CONTEXT.md` | Glossary entry for **OverlayPopups**. |

### Guest-mode header (fix 7)

| File | Intent |
| --- | --- |
| `src/ProoveGaming.App/Views/HomeView.axaml` | Add `HelpButton` immediately after `SearchButton` (guest-only via `IsVisible="{Binding IsGuest}"`), mirroring the sibling icon structure; drop `IsVisible="{Binding !IsGuest}"` from `NotifButton` so the bell and unread badge show for guests. |
| `src/ProoveGaming.App/App.axaml` | Add the `Path#HelpBg` / `Button#HelpButton:pointerover Path#HelpBg` pair next to the existing Search/Notif/Settings pairs, so the chamfered background is hidden by default and revealed on hover like every other icon in the panel. **These hover rules live in `App.axaml`, not in `HomeView.axaml`** — a new icon added without them renders its background permanently on. |

### ~~Password rule (fix 5)~~ — REVERTED, not implemented

| File | Intent |
| --- | --- |
| ~~`src/ProoveGaming.App/ViewModels/WelcomeViewModel.cs`~~ | ~~`PasswordRegex` → `^[A-Za-z0-9]+$`~~ — reverted to `^[\x21-\x7E]{8,}$` with its original comment. The only change this file retains is the `NotificationService` injection for fix 4. |

### Tests

| File | Intent |
| --- | --- |
| `tests/ProoveGaming.App.Tests/ProfileViewModelEditNicknameTests.cs` | **New.** Bug-reproduction plus surrounding behaviour for the nickname dialog. |
| ~~`tests/ProoveGaming.App.Tests/WelcomeViewModelPasswordRuleTests.cs`~~ | ~~**New.** The new password character rule.~~ **Deleted with the fix-5 revert** — every case in it asserted the relaxed rule. |
| `tests/ProoveGaming.App.Tests/WelcomeViewModelForgotPasswordTests.cs` | Update `CreateVm` for the new constructor parameter; add the success-toast assertion. |
| `tests/ProoveGaming.App.Tests/DatePickerViewTests.cs` | Add the default-format and resource-key-table tests. |

### Docs

| File | Intent |
| --- | --- |
| `docs/CONTEXT.md` | Add `pgFieldBox`, `Filled field state`, `Password character rule`; extend the `DatePickerView` entry to mention its resource-key localization. |

## Acceptance criteria

> **Updated 2026-08-27** (`docs/plans/improve-api-error-surfacing/`): the inline profile
> auto-save (`ProfileViewModel.ScheduleProfileSave`) is no longer silent when a save fails.
> A rejection now raises a notification toast — the shared rate-limit wording on HTTP 429,
> `ProfileView_Notification_ProfileSaveFailed` otherwise — while the value the user typed
> stays on screen (no revert, so no re-entrant save storm). A token rejection keeps its
> existing session bounce and raises no toast, and a debounce cancellation is not a failure.

1. Given the app runs with English resources and a date of birth of 9 March 2001 is selected,
   when the profile Account tab is shown, then the Date-of-Birth field reads `09/03/2001`
   (not `03/09/01`).
2. Given the app runs with Ukrainian resources, when the Date-of-Birth calendar popup is opened,
   then the day-of-week header row, the month grid, and the popup header label are all Ukrainian
   text with no English word remaining anywhere in the popup.
3. Given any language, when the popup's mode toggle is stepped Days → Months → Years, then each
   grid renders in that language and the header label matches the grid it labels.
4. Given a signed-in user opens the nickname dialog and enters a nickname the server reports as
   `NICKNAME_ALREADY_EXISTS`, when Confirm is pressed, then the dialog stays open, the localized
   "This nickname is already taken." message appears under the field, the field border turns red,
   `User.DisplayName` is unchanged, and `api/profile/update` is never called.
5. Given the same conflict response carries `suggestions`, when Confirm is pressed, then the
   three suggestion chips are replaced by server-provided alternatives.
6. Given the nickname passes the availability check but `api/profile/update` returns a nickname
   field error, when Confirm is pressed, then that message appears inline in the dialog and the
   dialog stays open.
7. Given a network failure on either nickname request, when Confirm is pressed, then the toast
   path (`HandleProfileUpdateError`) is used and no inline field error is set — behaviour
   unchanged from today.
8. Given the profile Date-of-Birth field has a selected date, when the field is not hovered and
   the popup is closed, then its border is `#6357FF`; given no date is selected, the border is
   `#4CBEBCEA`; hovering either state gives `#786DFF`; opening the popup gives `#6357FF` at 2 px.
9. Criterion 8 holds identically for the Country field.
10. Given the First/Last-name and Phone fields, when the profile renders, then their idle,
    filled and hover borders are visually identical to before this change (the extraction is
    behaviour-preserving).
11. Given a valid email on the forgot-password step, when `SEND RESET LINK` returns success, then
    the "Check your email" panel is shown **and** the toast reading the localized reset-link-sent
    message appears at the top-right.
12. Given the same step returns any error, when `SEND RESET LINK` is pressed, then no success
    toast appears and the existing inline error behaviour is unchanged.
13. ~~Given the sign-up form, when the password is `abc`, `A1`, `7`, or `MyPassword2026`, then no
    password error is shown and the SIGN UP button submits.~~ **VOID — fix 5 reverted.**
14. ~~Given the sign-up form, when the password is `pass word`, `pass!word`, `пароль`, or empty,
    then the localized password error is shown and no request is sent.~~ **VOID — fix 5 reverted.**
15. Given the server rejects a client-valid password with `PASSWORD_TOO_SHORT` /
    `PASSWORD_NO_UPPERCASE` / `PASSWORD_NO_NUMBER`, when sign-up is submitted, then that server
    message is shown inline under the password field (existing `ApiErrorMessages` path still
    works). *Unaffected by the revert — this path was never modified.*
16. `dotnet build ./src/ProveGaming.slnx` and `dotnet test ./src/ProveGaming.slnx` both pass.
17. Given the server returns a nickname suggestion containing an underscore (e.g. `Sad_855`),
    when the registration nickname step or the profile nickname dialog renders the suggestion
    chips, then the chip label reads `Sad_855` — the underscore is visible and no character is
    underlined — and clicking it puts the identical string in the input field.
18. Given a suggestion with no underscore, when the chips render, then their appearance and
    behaviour are unchanged from before this change.
19. Given the app is in guest mode, when the home header renders, then the help icon appears
    immediately to the right of the search icon with the same 16px spacing as its neighbours,
    and is rendered at its natural artwork size (`viewBox 0 0 23 27`) so its height matches the
    bell and gear — those are drawn ~27 tall in 56×56 canvas coordinates. *(Corrected after
    Round 2 **S2**: this criterion previously claimed the neighbours were 24px. Only the search
    icon is 24; sizing the help icon to match it left it visibly undersized next to the bell and
    gear it actually sits between.)*
20. Given the app is in guest mode, when the pointer moves over the help icon, then the
    chamfered `#4D6357FF` background appears, and it disappears on pointer-out — identical to
    the search, notification and settings icons. When not hovered, no background is drawn.
21. Given the app is in guest mode, when the home header renders, then the notification bell is
    visible; connecting or disconnecting a device adds an entry to its panel and increments the
    unread badge, exactly as for a signed-in user.

## Test strategy

This is a `fix-*` change, so every defect gets a test that **fails on the current code first**.

**`tests/ProoveGaming.App.Tests/ProfileViewModelEditNicknameTests.cs` (new)** — a recording
`IAuthApiService` fake in the style of `ProfileViewModelEditEmailTests.RecordingAuthApi` (all
unused members `throw new NotSupportedException()`), with settable
`CheckProfileNicknameResult` / `UpdateProfileResult`:

- *Reproduction (fails today):* `check_nickname` returns
  `{ Success = false, Errors = [ { NICKNAME_ALREADY_EXISTS, field "nickname" } ] }` →
  `HasEditNicknameError` is true, `EditNicknameError` is the resolved message,
  `IsEditNicknameOpen` stays true, `UpdateProfileCalls == 0`, `User.DisplayName` unchanged.
- Conflict carrying `suggestions` → `NicknameSuggestions` contains the server values and
  `HasNicknameSuggestions` is true.
- `update` returns a nickname field error after a passing check → inline error, dialog open.
- Network error on `check_nickname` (`IsNetworkError = true`) → `EditNicknameError` stays empty
  (toast path), dialog open.
- Golden path: check passes, update succeeds → `User.DisplayName` updated, dialog closed,
  `EditNicknameError` empty.
- Local validation guards still short-circuit before any request: `ab` (too short), `a b`
  (spaces), 21 chars (too long), `наз` (invalid characters) → no API call.
- Confirming an unchanged nickname closes the dialog without any request.

~~**`tests/ProoveGaming.App.Tests/WelcomeViewModelPasswordRuleTests.cs` (new)**~~ — **VOID, file
deleted with the fix-5 revert.** It was written and did verify the relaxed rule (6 of its 15
cases failed against the old regex), but every case asserted behaviour that no longer exists.
The pre-existing password rule has no dedicated tests, which is the state this plan found and
returns to.

**`tests/ProoveGaming.App.Tests/WelcomeViewModelForgotPasswordTests.cs`** — `CreateVm` gains a
`NotificationService` it returns, so a new test can assert `notification.IsVisible` and
`notification.Text == "WelcomeView_Notification_ResetLinkSent"` after a successful send (in a
headless test `LocalizationManager.GetString` returns the key, which the existing tests in this
file already rely on). A companion test asserts the toast is **not** shown on `MAIL_ERROR`. All
existing tests in the file must stay green.

**`tests/ProoveGaming.App.Tests/DatePickerViewTests.cs`** —
- `DatePickerView.DefaultDisplayFormat` formats `2001-03-09` as `09/03/2001`.
- `DatePickerResourceKeys.DayShort/MonthShort/MonthFull` have lengths 7/12/12, contain no
  null-or-empty entry, and are collectively distinct.
- *Recommended:* a resource-completeness test that walks up from `AppContext.BaseDirectory` to
  the repo root, XML-parses `src/ProoveGaming.App/Resources/Strings.en.axaml` and
  `Strings.uk.axaml`, and asserts every key in the three tables has an `x:Key` in **both** files.
  If the path walk proves fragile in CI, drop to asserting the tables' shape only and note it —
  do not leave a flaky test in.

**Fix 6 (suggestion chips)** — no automated coverage is possible here. The defect lives entirely
in how the Fluent `Button` template renders string content; asserting it needs a realized visual
tree and a rendered text run, which the headless test project cannot produce without an Avalonia
headless harness (a new NuGet package). The ViewModel side is already correct and already tested —
`ProfileViewModelEditNicknameTests.ConfirmEditNickname_Conflict_RefreshesSuggestionChipsFromServer`
asserts the exact server strings reach `NicknameSuggestions` — so a test there would pass both
before and after the fix and prove nothing. Verified by QA step 16 instead.

**Deliberately not covered by automated tests**

- The rendered border colours of `pgFieldBox` (fix 3), the toast's z-order versus the dialog
  overlay (fix 2's motivation), chip access-key rendering (fix 6), and the guest-mode header —
  icon placement, hover state and bell visibility (fix 7). All need a live Avalonia visual tree
  on the UI thread; the test project is headless. Covered by QA steps 4–7, 9, 16 and 17–18.
  Fix 7 is pure XAML with no ViewModel change at all, so there is no seam a unit test could
  reach even in principle.
- The actual localized wording (only key presence is asserted; wording is a translation review).
- Real Launcher API responses — all API interaction is faked.

## QA (manual test steps)

> **Setting the app language.** The app has no in-app language switcher — it picks the language
> once at startup and keeps it for the session. To run these steps in Ukrainian, use a **Release**
> build and either run it on a Windows account whose display language is Ukrainian, **or** set
> this registry value before launching:
> `HKEY_CURRENT_USER\Software\ProoveGaming\Settings` → string value `AppLanguage` = `uk`.
> To go back to English, set that value to `en` (or delete it) and relaunch. Debug builds are
> always English and ignore the registry, so they cannot be used for steps 2, 3 and 15.

1. Launch the app in Ukrainian (see the note above) and sign in with a test account.
   Open **Profile**. *Expected:* the page loads on the Account tab.
2. Click the **Date of Birth** field. *Expected:* the calendar opens; the weekday row, the
   month/year header, and (after clicking the chevron next to the header) the month grid are all
   in Ukrainian — no English words anywhere in the popup.
3. Pick 9 March 2001. *Expected:* the popup closes and the field reads exactly `09/03/2001`.
4. Move the mouse away from the field. *Expected:* the field keeps a blue border (the same blue
   as the First Name field when it has text), not the faint grey border.
5. Hover the field. *Expected:* the border lightens to the same hover blue as the other fields.
6. Open the **Country** dropdown, pick any country, then move the mouse away. *Expected:* the
   Country field also keeps the blue border, and hovering lightens it — identical behaviour to
   step 4/5.
7. Check the **First Name**, **Last Name** and **Phone** fields still look exactly as they did
   before: faint border when empty, blue when filled, lighter blue on hover.
8. Click the pencil icon next to your nickname. Enter a nickname you know belongs to another
   account and press **CONFIRM**. *Expected:* the dialog stays open, a red message
   "This nickname is already taken." appears under the field, the field border turns red, and
   the suggestion chips refresh to new suggestions. Your nickname at the top of the page is
   unchanged.
9. In the same dialog, click one of the suggestion chips and press **CONFIRM**. *Expected:* the
   dialog closes, the nickname at the top updates, and a green success toast appears at the
   top-right.
10. Sign out. On the sign-in screen click **FORGOT PASSWORD**, enter a registered email, and
    press **SEND RESET LINK**. *Expected:* the screen switches to "Check your email" **and** a
    green toast confirming the link was sent appears at the top-right for a few seconds.
11. Repeat step 10 with an email that is not registered. *Expected:* the inline red
    "We couldn't find an account with that email." message appears and **no** green toast.
12. ~~Go to the **SIGN UP** tab. Type email + the password `abc`, tick the terms box.~~
    **SKIP — fix 5 was reverted; password behaviour is unchanged from the previous release and
    needs no verification in this round.**
13. ~~Change the password to `pass word`, then `pass!word`, then `пароль`.~~ **SKIP.**
14. ~~Set the password back to `abc` and press **SIGN UP**.~~ **SKIP.**
15. Relaunch in English (see the note above step 1) and repeat steps 2, 8 and 10. *Expected:* all
    the same behaviour with English text.
16. Register a brand-new account. On the **Enter Your Nickname** step, press the circular refresh
    button beside the suggestion chips until a suggestion containing an underscore appears (e.g.
    `Sad_855`). *Expected:* the chip shows the underscore, and no letter or digit in it is
    underlined. Click that chip. *Expected:* the input field above fills in with **exactly** the
    same text as the chip — character for character. Repeat the same check on the profile
    nickname dialog (pencil icon next to your nickname → refresh the chips there).
17. Log out and choose **ENTER AS GUEST**. Look at the icon row in the top-right of the home
    screen. *Expected:* a help icon (question mark in a speech bubble) sits immediately to the
    right of the magnifier, the same size as the other icons and evenly spaced with them. A bell
    icon is also present — before this change guests had no bell.
18. Hover each icon in that row in turn, including the new help icon. *Expected:* every one
    shows the same purple angular background on hover and loses it when the pointer leaves. No
    icon should show that background while un-hovered.

**Popup keyboard focus / `OverlayPopups` regression (2026-08-14 follow-up).** These steps exist
because `OverlayPopups` repositions **every** popup in the app inside the window bounds; run
them at the minimum window size (resize to 1200×880), where overflow is most likely.

19. Open the nickname dialog (pencil icon), click a suggestion chip. *Expected:* the field fills
    with the chip text and the caret sits at its end — pressing Backspace immediately deletes
    characters without clicking into the field first.
20. With the nickname dialog open, click another application's window, then click back on the
    dialog body (not the text field). *Expected:* the dialog is still open and Backspace/typing
    edit the field right away. Repeat for the New Preset dialog in Device Details.
21. In Profile, open the **Date of Birth** calendar and the **Country** dropdown with the window
    at 1200×880 and the field scrolled near the bottom edge. *Expected:* the popup is fully
    visible inside the window, aligned to its field (no clipped shadow, no popup floating away
    from the field). **Not yet verified as of 2026-08-14 — needs a signed-in account.**
22. On a connected **mouse** (BLAZE/TAIPAN), open Device Details → RGB Lighting → Static and
    click the colour swatch in the right-hand column. *Expected:* the picker opens fully inside
    the window (it may flip left of the swatch — that is acceptable), all controls reachable.
    **Not yet verified as of 2026-08-14 — needs a connected mouse; the keyboard-layout picker
    was verified.**
19. Still in guest mode, unplug a connected device and plug it back in. *Expected:* the bell's
    red unread badge appears with a count, and clicking the bell opens the panel listing the
    disconnect and reconnect entries — the same behaviour a signed-in user gets.

## Review focus

- `localization` — every new user-facing string has a key in **both** `Strings.en.axaml` and
  `Strings.uk.axaml`, keys are referenced through the `DatePickerResourceKeys` tables (no inline
  key literals), and no string is built by runtime transformation.
- `DI lifetimes` — `WelcomeViewModel` gains a constructor dependency; confirm
  `NotificationService` is the same singleton the `MainWindow` toast is wired to and that no new
  registration or lifetime change was introduced in `BuildServices()`.
- `async/await` — the reworked `ConfirmEditNickname` keeps both awaits, still returns early on
  every failure branch, and never blocks.
- `UI-thread affinity` — `NotificationService.Show` is called from the UI thread in the
  forgot-password path (the command runs on the dispatcher; verify no `Task.Run` was introduced).

## Performance & quality

No hot paths are touched — nothing here runs per-frame, per-poll, or on the LED/device path.

- The date picker's resource lookups (≤31 `TryGetResource` calls) happen on **popup open** and
  mode switch, not per render tick. Do not move them into a per-cell or per-frame path; the
  existing `RenderDays` / `RenderPeriod` cadence (one call per open/step) is the ceiling.
- `DatePickerResourceKeys` tables are `static readonly` — allocated once, never per lookup. Do
  not build key names with string interpolation at call sites.
- No new `IDisposable`, no new event subscriptions, no new shared mutable state, so no ownership
  or thread-safety questions arise. The one new field on each picker control (`Border` container)
  is a cached child reference with the control's lifetime.
- No sync-over-async anywhere in the changed paths.

## Risks

- **The client password rule is now looser than the server's.** The v1.7 spec (§1, §5) still
  returns `PASSWORD_TOO_SHORT` / `PASSWORD_NO_UPPERCASE` / `PASSWORD_NO_NUMBER`, so a user who
  types `abc` will pass the client gate and be rejected by the backend. This change makes the
  rejection *server-side and late* instead of *client-side and immediate*. The existing
  `ApiErrorMessages` mapping keeps those messages inline under the password field, so the user is
  not stuck — but the product owner should confirm the backend policy is being relaxed to match,
  otherwise sign-up gets measurably worse for short passwords. Flagged, not blocked.
- **The `profileBox` → `pgFieldBox` extraction touches a screen the bug report did not name.**
  It is behaviour-preserving by intent (acceptance criterion 10 exists to prove it), but a
  regression here would show up as wrong borders on First/Last name and Phone. Reviewer should
  eyeball those three fields.
- **Style ordering is load-bearing.** In `pgFieldBox`, `:pointerover` must be declared *after*
  `.filled`, and each control's local `.open` rule must come *after* the `StyleInclude`, or the
  blue-when-filled state will swallow the hover and open states. Easy to get wrong, invisible in
  a build.
- **Ukrainian month names must be nominative** (`СІЧЕНЬ`, not `СІЧНЯ`) since they appear
  standalone in the popup header. A translator working from the `MMMM` habit will supply the
  genitive form.
- **`ResourceStrings.Get` fallback masks missing keys.** Returning the English literal keeps the
  UI sane but means a forgotten `uk` key ships silently as English. The resource-completeness
  test in the test strategy is the guard; if it is dropped, this risk goes unmitigated.
- **The guest nickname dead end remains.** `ConfirmEditNickname` returns silently when
  `SessionToken` is empty. A guest pressing Confirm still gets no feedback. Out of scope here but
  worth a follow-up ticket.

## Open questions

1. **Week start.** The calendar is Sunday-first, and `DatePickerView.GridStartDate` (plus its
   tests) is built on that assumption. Ukrainian calendars conventionally start Monday. Changing
   it is a real change to the grid maths and its tests, so this plan keeps Sunday-first. Confirm
   that is acceptable, or file it separately.
2. **Backend password policy.** Is the server rule being relaxed to match (letters + digits, any
   length)? If not, see the first risk — the client change alone may be worse than the status quo.
3. **Reset-link toast wording.** Proposed: EN "Password reset link sent successfully" /
   UK "Посилання для скидання пароля надіслано". Confirm or supply the product copy.
4. **Password hint wording.** Proposed: EN "Latin letters and digits only." /
   UK "Лише латинські літери та цифри." Confirm or supply the product copy.

## Review findings

*Filled in by `/reviewer`.*

### Round 1 — Ship: no

Review surface: no commits on the branch — the whole delta is the uncommitted working tree
(14 modified files) plus 6 untracked paths. `git diff main...HEAD`, `git diff --staged` and
`git log main..HEAD` are all empty; the review covers `git diff` plus every untracked file,
reconciled against `git status --porcelain`.

#### Plan conformance

- **Implemented as planned:** all 5 fixes and every File-level change list item. Localization
  plumbing (`ResourceStrings`, `DatePickerResourceKeys`, `LocalizationManager` delegation),
  `DD/MM/YYYY` default via `DefaultDisplayFormat`, resource-driven calendar text, the
  `pgFieldBox` extraction across all three call sites, inline nickname field errors, the
  reset-link toast, and the relaxed password regex. Build clean (0 errors, no new warnings);
  646 tests green (343 Infrastructure + 303 App).
- **Deviates from plan:** none material. The plan proposed the resource-completeness test as
  "recommended with a fallback"; it was implemented in full and verified to fail on a deleted
  key, which is better than the plan required.
- **Accepted limitation (per plan):** guest-session dead end in `ConfirmEditNickname`
  (`ProfileViewModel.cs:573`) — residual risk in **S4**. Toast-behind-popup for non-field
  failures (criterion 7) — residual risk in **S2**.
- **Acceptance criteria:** 1, 4–7, 11–16 are met and test-backed. 8, 9, 10 are implemented but
  have **no** verification of any kind (see **T4**). 2 and 3 are implemented but **cannot be
  verified by QA as written** (see **S3**). Criterion 15 is unaffected by the diff and still
  holds.

#### Code

- [ ] **B1. Duplicate `WelcomeView_Error_Password` key added to the Ukrainian dictionary** — `src/ProoveGaming.App/Resources/Strings.uk.axaml:36` (and pre-existing `:391`) — Blocker
  **Problem:** This change added `WelcomeView_Error_Password` at line 36 on the belief it was
  absent from the uk file. It was already defined at what is now line 391 with the old wording
  ("Пароль не відповідає вимогам."). `Strings.uk.axaml` now contains 344 key declarations for
  343 distinct keys — exactly one duplicate, introduced here. `Strings.en.axaml` is clean
  (326/326).
  **Why it blocks:** `SetLanguage("uk")` merges this dictionary at runtime via `ResourceInclude`.
  Either (a) `ResourceDictionary` population throws on the duplicate key and the app fails when
  Ukrainian is selected — a startup crash on a Ukrainian-locale Release build; or (b) the
  last definition silently wins, so line 391's stale text ships and the password copy change
  is dead in Ukrainian while appearing correct in the diff. I could not execute the Ukrainian
  path to determine which (no runtime language switch — see **S3**), but both outcomes are
  unacceptable, so the severity does not depend on which one it is.
  **Solution options:**
  - Option A (correct): delete the added line 36 and edit the existing declaration at line 391
    in place to the new wording. Keeps one declaration per key and leaves the file's existing
    section ordering intact.
  - Option B: delete line 391 and keep line 36 next to `WelcomeView_PasswordHint`. Groups the
    two password strings together but moves a key out of its established section.
  Either way, add the uniqueness guard in **T1** so this class of mistake cannot recur.

- [ ] **S1. Password hint no longer describes the rule the server actually enforces** — `src/ProoveGaming.App/Resources/Strings.en.axaml:62,64` — Should-fix
  **Problem / why it matters:** the client gate is now "Latin letters and digits, any length"
  and the hint says exactly that. The Launcher API (v1.7 §1, §5) still rejects with
  `PASSWORD_TOO_SHORT` / `PASSWORD_NO_UPPERCASE` / `PASSWORD_NO_NUMBER`. A user who types
  `abc` is told by the hint that it is valid, the SIGN UP button enables, and the request is
  then rejected server-side. Before this change the client caught it instantly. The server
  message does render inline (that path is untouched), so this degrades rather than breaks —
  but it is a real regression in feedback latency for exactly the passwords the new rule
  newly admits. This is plan **Risk 1** and **Open question 2**, still unanswered.
  **Fix:** requires a product decision, not a code fix, and should be settled before merge.
  Either (a) confirm the backend policy is being relaxed to match, and ship as-is; or (b) keep
  the client rule as requested but restore the informative hint (e.g. "Latin letters and digits
  only. Your account may require 8+ characters with an uppercase letter and a number."), so the
  user is not actively misled while the two policies differ.

- [x] **S2. Network failure in the nickname dialog still produces an invisible toast** — `src/ProoveGaming.App/ViewModels/ProfileViewModel.cs:622-641` (`HandleNicknameFailure`) — Should-fix — *fixed via PR review finding **M2**, which showed the problem was wider than this: not just network failures but any rejection naming a field other than the nickname. See the PR review follow-up below.*
  **Problem / why it matters:** the fix correctly routes *field* errors inline, but the
  `IsNetworkError` branch still falls through to `HandleProfileUpdateError` →
  `_notification.Show(...)`. That toast is `MainWindow`'s `NotificationGrid`, which lives in the
  window content, while the nickname dialog is a `Popup` whose overlay `Panel` covers the whole
  window at `#BF0D0737`. Concrete scenario: user goes offline, opens the nickname dialog, types
  a name, clicks CONFIRM — the request fails, `EditNicknameError` stays empty by design, the
  dialog stays open, and the toast renders behind a 75%-opaque backdrop. The user sees nothing
  happen at all. This is the *same* no-feedback bug the change was written to fix, one branch
  away in the same method. Plan criterion 7 accepts it as "behaviour unchanged from today", and
  `ConfirmEditNickname_NetworkFailure_LeavesFieldErrorEmptyForTheToastPath` now locks that in.
  **Fix:** set `EditNicknameError = response.Message ?? <localized generic>` on the network
  branch too, so every non-token failure inside this dialog is visible where the user is
  looking; keep `HandleProfileUpdateError` only for the token-invalid case, which legitimately
  closes the dialog. Update the test to assert the inline message instead of its absence.

- [ ] **S3. QA cannot execute the localization steps — the app has no runtime language switch** — `src/ProoveGaming.App/LocalizationManager.cs:21-37`; plan QA steps 1, 2, 15 (line 270 ff.) — Should-fix
  **Problem / why it matters:** QA steps 1/2/15 ask the tester to run in Ukrainian and later to
  "switch the app to English". Neither is possible: `MainWindow.axaml:124`'s `LanguageSelector`
  is `IsVisible="False"` and never wired up, `SetLanguage` has no call site outside
  `LocalizationManager`, and in **Debug builds `InitializeFromSystemCulture` does nothing at
  all** — its entire body sits in the `#else` branch and the `SetLanguage(uk)` debug override
  on line 24 is commented out. So a Debug build is always English, and even the persisted
  registry preference is only read in Release. The QA section is meant to be pasted verbatim
  into a ticket; as written it sends a tester after a control that does not exist, which means
  acceptance criteria 2 and 3 — the entire point of fix 1 — would go unverified while looking
  verified.
  **Fix:** rewrite the QA steps to state the only mechanism that works: run a **Release** build
  on a machine whose UI culture is Ukrainian, or set
  `HKCU\Software\ProoveGaming\Settings\AppLanguage = uk` (per `LanguagePreferenceStore`) before
  launching, and drop the "switch the app to English" instruction in favour of relaunching with
  the value removed. Optionally uncomment line 24 for local Debug verification, but do not ship
  that uncommented.

- [ ] **S4. Guest session dead-ends silently on nickname confirm** — `src/ProoveGaming.App/ViewModels/ProfileViewModel.cs:573` — Should-fix
  **Problem / why it matters:** `if (string.IsNullOrEmpty(User.SessionToken)) return;` — a guest
  who opens the nickname dialog, types a valid name and presses CONFIRM gets no error, no
  toast, no close, and no change. Pre-existing and explicitly out of scope per the plan, but it
  now sits directly beside the feedback path this change improved, and a QA tester exercising
  step 8 in guest mode will file it as a regression of this work.
  **Fix:** either apply the local nickname change and close the dialog (as `ConfirmEditEmail`
  already does for guests at `ProfileViewModel.cs:680`), or set an explicit
  `EditNicknameError`. Track separately if it stays out of scope.

#### Tests

- [ ] **T1. Resource dictionaries have no key-uniqueness assertion** — this is what let **B1**
  through. `DatePickerResourceKeyTests.EveryKey_IsDefinedInTheResourceDictionary` checks
  *presence* only. Extend `ReadResourceKeys` to return the raw key list and assert
  `list.Count == list.Distinct().Count()` for both `Strings.en.axaml` and `Strings.uk.axaml`,
  naming the offenders — `tests/ProoveGaming.App.Tests/DatePickerViewTests.cs`. Cheap, and it
  guards all 326/343 keys, not just the 31 new ones.
- [ ] **T2. `ResourceStrings.Get` fallback path is untested** — new public API in
  `ProoveGaming.Common` whose whole job is deciding what shows when a key is missing. Add a
  direct test that `Get("Nonexistent_Key", "fallback") == "fallback"` with no Application
  running — `tests/ProoveGaming.App.Tests`.
- [ ] **T3. `SignUpButtonEnabled_ShortButValidPassword_IsEnabled` does not guard the change** —
  `tests/ProoveGaming.App.Tests/WelcomeViewModelPasswordRuleTests.cs:110`. `SignUpButtonEnabled`
  never consulted `PasswordRegex`, before or after, so this test passes identically on pre-fix
  code. It reads like new-rule coverage but is characterization. Keep it, but rename it to say
  so (e.g. `SignUpButtonEnabled_DoesNotGateOnPasswordRule`) so the next reader is not misled.
- [ ] **T4. Criteria 8, 9, 10 have no verification at all** — the `pgFieldBox` filled/hover/open
  borders and the removal of the inline `Background="Transparent"` from both picker containers
  (which is what makes `:pointerover` hit-test) are covered by neither tests nor an executed
  manual pass. The test project has no Avalonia headless harness and adding one needs a new
  NuGet package. The shared style **is** confirmed embedded in `ProoveGaming.Common.dll` and
  both `StyleInclude` paths compile, so the wiring is sound — but "renders correctly" is
  currently an assumption. QA steps 4–7 are the only gate; treat them as mandatory, not
  optional. Note also that `dotnet-ci.yml` builds only the App csproj, so **none** of these
  tests run in CI — local runs are the only enforcement.

#### Nits

- `Field = "nickname"` in the `NicknameTaken` fixture is decorative — `ApiErrorMessages` maps by
  `error_code` and ignores the server's `field` string — `tests/ProoveGaming.App.Tests/ProfileViewModelEditNicknameTests.cs:63`.
- `DatePickerResourceKeys` exposes `public static readonly string[]`, which callers can mutate
  element-wise — `src/ProoveGaming.Common/Localization/DatePickerResourceKeys.cs:10,22,40`.
- `RefreshDayOfWeekHeader()` runs on every `RenderCurrentMode()` to follow a runtime language
  change that cannot currently happen (see **S3**). Harmless — 7 lookups on popup open — and
  correct if a switcher is ever wired up — `src/ProoveGaming.Common/Controls/DatePickerView.axaml.cs:274`.

#### What's done well

- The `pgFieldBox` extraction resolved a real `#4CBEBCEA` / `#4DBEBCEA` drift instead of copying
  it into two more files, and putting the `StyleInclude` inside each control's own
  `UserControl.Styles` keeps the shared controls self-contained rather than dependent on the
  host remembering an app-level include. Style ordering (`.filled` before `:pointerover`, local
  `.open` after the include) is correct and commented as load-bearing — do not reorder.
- The reproduction ordering was actually executed, not assumed: reverting `ProfileViewModel.cs`
  failed exactly 2 of 11 nickname tests, and restoring the old regex failed exactly 6 of 15
  password tests. The two cases where reverting breaks compilation were reported as
  unverifiable rather than papered over.
- `ResourceStrings.Get(key, fallback)` falling back to the invariant-English literal keeps a
  missing translation rendering a real month name instead of a raw key.
- `HandleNicknameFailure` mirrors the existing `ConfirmEditEmail` field-error pattern rather
  than inventing a new one, and the comment explains *why* the toast is unusable here.

#### Verdict

Ship: no — **B1** is a one-line duplicate key that either crashes the Ukrainian path or
silently ships stale copy, and it was introduced by this change. Everything else is sound:
the five fixes are correctly implemented, tested, and reproduction-verified. **S1** needs a
product answer rather than code, and should not be merged past without one.
First: 1) fix **B1** and add the **T1** uniqueness guard, 2) get a decision on **S1**
(backend policy vs. informative hint), 3) fix **S3** so QA can actually verify criteria 2–3.

### Round 1 follow-up — fix 5 reverted

Outcome of the **S1** decision, recorded 2026-07-31.

**What happened.** The relaxed rule was tested against the live backend. A 6-character
lowercase password passed the new client gate, was sent, and came back rejected with
`PASSWORD_NO_UPPERCASE` rendered under the field. Spec confirms the rule is enforced on
**both** endpoints in the sign-up path — `api/check_email`
(`docs/knowledge-base/proove-gaming-launcher-api-v1.7-en.md:112`) and `api/register`
(same file `:193`, "everything from check_email/check_nickname"). The app calls `check_email`
at `WelcomeViewModel.cs:292` and `register` at `MainWindow.axaml.cs:334`, so suppressing the
first error would only have deferred the identical failure to the nickname step. **No
client-side change could make such a password register.** The user chose to revert fix 5
rather than ship a client rule that disagrees with the server.

**Reverted to pre-change state:**
- `src/ProoveGaming.App/ViewModels/WelcomeViewModel.cs:563` — `PasswordRegex` back to
  `^[\x21-\x7E]{8,}$` with its original comment. The file's only remaining change is the
  `NotificationService` injection for fix 4.
- `Strings.en.axaml` — `WelcomeView_PasswordHint` and `WelcomeView_Error_Password` restored.
- `Strings.uk.axaml` — both restored; `WelcomeView_Error_Password` is again a **single**
  declaration at its original position.
- `tests/ProoveGaming.App.Tests/WelcomeViewModelPasswordRuleTests.cs` — deleted.
- `docs/CONTEXT.md` — the `Password character rule` glossary entry removed.

**Finding status:**
- [x] **B1** (duplicate `WelcomeView_Error_Password` in `Strings.uk.axaml`) — **closed.**
  Resolved as a side effect of the revert: the added declaration is gone and the original
  restored in place. Verified — `Strings.en.axaml` 326/326 and `Strings.uk.axaml` 343/343
  declarations/distinct keys, no duplicates in either.
- [x] **S1** (hint vs. server policy) — **closed by reverting fix 5.** Client and server now
  state the same requirements again, so the misleading-hint problem cannot arise.
- [ ] **T1** (resource-key uniqueness assertion) — **still open and still worth doing.** The
  revert removed this instance of the bug but not the ability to reintroduce it; nothing in
  the build or test suite would catch a duplicate key today.
- [ ] **S2**, **S3**, **S4**, **T2**, **T3**, **T4** — unaffected by the revert, still open.
  (**T3** now refers to a deleted file and is void.)

**Carried forward for the backend team.** To deliver the original request ("letters and
digits, any case, any length") the policy must be relaxed on `api/check_email` **and**
`api/register` — and it is three rules, not one, since a short lowercase password trips them
in sequence: `PASSWORD_TOO_SHORT`, `PASSWORD_NO_UPPERCASE`, `PASSWORD_NO_NUMBER`. Keep
`PASSWORD_HAS_SPACES`. Once that ships, fix 5 can be re-applied from this plan as written.

### Round 1 follow-up — fix 6 added, S3 closed

Recorded 2026-07-31, after Round 1. Two changes to the branch since the review.

**Fix 6 added (scope growth after review).** A user-reported defect: the nickname suggestion
chip showed `Sad855` while the field it filled in showed `Sad_855`. Root cause is the Fluent
`Button` template presenting string `Content` through a `ContentPresenter` with
`RecognizesAccessKey="True"`, which consumes `_` as a mnemonic marker. Fixed with one style rule
on `Button.suggestionButton`'s `PART_ContentPresenter` in `src/ProoveGaming.App/App.axaml`,
covering all six chips across `ChooseNicknameView` and `ProfileView`. Full write-up in
**Options considered → Fix 6** and the change list above.
- This landed **after** Round 1 and has therefore **not been reviewed**. It needs a Round 2 pass
  before merge, however small it looks.
- No automated coverage is possible (see Test strategy); acceptance criteria **17–18** and QA
  step **16** are the only gates.
- Data was never affected — the stored value was always correct, only the chip's rendering. So
  there is no bad data to clean up from before the fix.

- [x] **S3** (QA cannot verify the localization criteria) — **closed.** The QA section now opens
  with an explicit note on setting the app language: Release build plus either a Ukrainian
  Windows display language or `HKCU\Software\ProoveGaming\Settings` → `AppLanguage` = `uk`, and
  states plainly that Debug builds are always English and cannot be used for steps 2, 3 and 15.
  Step 1 and step 15 now point at that note instead of implying a switcher exists.
- [ ] **T3** — void; the file it referred to was deleted with the fix-5 revert.
- [ ] **S2**, **S4**, **T1**, **T2**, **T4** — unchanged, still open.

**Verdict at that point:** scope is fixes 1–4 plus 6. Ship: still **no** — **T4** (border
rendering never verified by test or executed QA) is unchanged, and fix 6 is entirely unreviewed.
Build and suite are green: 631 tests (288 App, 343 Infrastructure), down from 646 because the 15
password-rule tests went with the fix-5 revert.

### Round 1 follow-up — fix 7 added (guest-mode header)

Recorded 2026-07-31, after Round 1. Second piece of post-review scope growth.

**What was asked and done.** Add a supplied help icon to the right of search in guest mode, and
show notifications in guest mode. Implemented in `HomeView.axaml` (new `HelpButton`; `!IsGuest`
gate removed from `NotifButton`) and `App.axaml` (hover-background style pair). Design notes in
**Options considered → Fix 7**; acceptance criteria **19–21**; QA steps **17–19**.

**A bug was introduced and fixed within this follow-up, worth recording as a trap.** The first
attempt added the icon without a hover rule, so its chamfered background rendered permanently
instead of on hover. The cause was a bad assumption: the icon-background hover rules are **not**
in `HomeView.axaml` next to the markup — they live in `App.axaml:317-340` as
`Path#<Name>Bg` / `Button#<Name>Button:pointerover Path#<Name>Bg` pairs, and a grep confined to
`HomeView.axaml` and its code-behind finds nothing and wrongly suggests the background is
always-on. **Any future icon added to this header needs its pair added in `App.axaml` or it will
render wrong.** This is called out in the change list too.

**Open decisions carried to review:**
- The help icon is **guest-only** (`IsVisible="{Binding IsGuest}"`), read from the request's
  wording ("in guest mode" applied to both asks). If it should also show for signed-in users,
  delete that one attribute. Unconfirmed.
- The help icon has **no `Command` or `Click` wired** — it is a button with hover feedback that
  does nothing. It carries `Cursor="Hand"` like its neighbours, so it currently advertises
  clickability it cannot deliver. Needs either an action or a decision to leave it inert.

- [ ] Fix 7 is **unreviewed**, like fix 6. Both need a Round 2 pass.

**Verdict now:** scope is fixes 1–4, 6 and 7. Ship: **no** — unchanged reasons (**T4**), plus
fixes 6 and 7 both unreviewed and both verifiable only by hand. Build clean; suite green at 631
tests (fix 7 adds no tests — it is pure XAML with no ViewModel seam).

### Round 2 — Ship: no

Review surface reconciled: the branch now carries **two commits** (`e41c967` "profile and sign
in fixes" = fixes 1–4 + the fix-5 revert; `e459bee` "fixed _" = fix 6) plus an **uncommitted**
working tree of three files (`App.axaml`, `HomeView.axaml`, this plan) = fix 7. `git diff
--staged` is empty. Union of `git diff main...HEAD` + `git diff` equals `git status --porcelain`.
Round 2 focuses on fixes 6 and 7, which Round 1 never saw; fixes 1–4 were re-checked for drift
and are unchanged from Round 1.

#### Plan conformance

- **Implemented as planned:** fix 6 exactly as designed (Option A — one style rule on
  `Button.suggestionButton`'s `PART_ContentPresenter`). Fix 7 both halves: `HelpButton` inserted
  immediately after `SearchButton`, `!IsGuest` gate removed from `NotifButton`, and the
  `Path#HelpBg` / `Button#HelpButton:pointerover` pair added in `App.axaml:323-328`.
- **Verified beyond the plan's claim — fix 7's notification path is genuinely complete, not just
  visible.** I traced the whole chain rather than trusting the bell's visibility:
  `ToggleNotificationsCommand` (`HomeViewModel.cs:177`) is an unguarded toggle;
  `MainWindow.axaml:201` binds the overlay's `DataContext` to `HomeView`'s and gates it only on
  `IsNotificationsOpen` — there is no guest check anywhere on that path. Guests get a real,
  working panel. The plan's premise (feed is device-driven, not session-backed) also holds:
  every `AddNotification` call site is a device connect/disconnect/battery event.
- **Fix 6 exposure is complete.** A repo-wide sweep for `Content="{Binding …}"` in XAML returns
  exactly the six suggestion chips (`ChooseNicknameView.axaml:103,108,113`;
  `ProfileView.axaml:546,551,556`). No other control renders user- or server-supplied text
  through an access-key-aware presenter, so there is no second instance of this bug lurking.
- **Deviates from plan:** acceptance criterion 19 asserts the help icon renders "at the same
  24px icon height … as its neighbours". That premise is wrong — see **S2**. The plan states a
  measurement that was never verified.
- **Acceptance criteria:** 17–18 (fix 6) and 21 (guest bell) are implemented and sound. 19–20
  (fix 7 visual) are implemented but 19 is written against an incorrect premise, and neither has
  been executed. All of 1–16 unchanged from Round 1.

#### Code

- [x] **S1. The help icon advertises clickability it does not have, breaking this header's own convention** — `src/ProoveGaming.App/Views/HomeView.axaml:96-99` — Should-fix — *fixed: took Option B — `Cursor="Hand"` removed, so the icon now matches `SearchButton` / `SettingsButton` and reads as the placeholder it is. A comment records why, so the next person does not "helpfully" add the cursor back. Option A (wiring a real action) remains available and still needs the product decision in **S3**; when it lands, restore `Cursor="Hand"` with it.*
  **Problem / why it matters:** `HelpButton` has `Cursor="Hand"` but no `Command` and no `Click`.
  Clicking it does nothing. That alone would be defensible — `SearchButton`
  (`HomeView.axaml:86`) and `SettingsButton` (`:131`) are also non-functional placeholders — but
  look at how those are written: **neither sets `Cursor`**. Across this entire header the
  convention is consistent and deliberate: the three wired buttons (`NotifButton:111`,
  `ProfileButton:143`, `GuestButton`) set `Cursor="Hand"`; the two placeholders do not.
  `HelpButton` is the only control in the panel that is inert *and* presents a hand cursor.
  **Concrete scenario:** a guest hovers the help icon, gets both the hand cursor and the purple
  hover background — the same feedback the working bell gives — clicks expecting help, and
  nothing happens. Nothing distinguishes it from a broken feature.
  **Solution options:**
  - Option A (correct): wire the intended action — the icon is a question-mark-in-bubble, so
    presumably a help/FAQ target. Needs the product decision the plan already records as open.
  - Option B (quick, honest until A lands): drop `Cursor="Hand"` from line 98 so the icon
    matches `SearchButton` / `SettingsButton` and reads as a placeholder rather than a broken
    button. One line, reversible when the command arrives.

- [x] **S2. The help icon is rendered ~10% smaller than the icons it sits between** — `src/ProoveGaming.App/Views/HomeView.axaml:104` — Should-fix — *fixed: took Option A — `Width="24" Height="24"` → `Width="23" Height="27"`, keeping `Stretch="Uniform"`, so the scale factor is 1.0 and the artwork renders at its authored ~22.7 × 26.7, matching the bell and gear. Acceptance criterion 19 corrected in the same change; it had asserted the neighbours were 24px, which would have had QA sign off on the undersized rendering.*
  **Problem / why it matters:** the icon is constrained to `Width="24" Height="24"
  Stretch="Uniform"`. The supplied artwork is natively ~22.7 × 26.7 (SVG `viewBox="0 0 23 27"`),
  so Uniform scales it by `24 / 26.7 ≈ 0.9` and it renders **20.4 × 24**. Its neighbours are not
  24 tall: `NotifBg`'s and `SettingsBg`'s icon paths are drawn directly in 56×56 canvas
  coordinates spanning roughly `14.6 → 41.4`, i.e. **~26.7 tall** (read off the path data, so
  approximate for the curve segments, but the magnitude is unambiguous). Only `SearchButton`
  is 24 — it is the outlier, and it was the one I sized against. The supplied artwork was
  authored at the same scale as the bell and gear; forcing it to 24 undoes that.
  **Concrete scenario:** in guest mode the header reads search(24) · help(24) · bell(26.7) ·
  gear(26.7); the new icon looks undersized next to the two icons immediately to its right.
  **Solution options:**
  - Option A (recommended): change line 104 to `Width="23" Height="27"`, keeping
    `Stretch="Uniform"`. Scale becomes 1.0, the artwork renders at its authored ~22.7 × 26.7 and
    matches the bell and gear.
  - Option B: drop `Width`/`Height`/`Stretch` and re-express the path in 56×56 canvas
    coordinates like `NotifBg`/`SettingsBg`. Most consistent with those two siblings, but it
    means hand-translating the path data — more risk than it is worth for the same result.
  Whichever is chosen, **acceptance criterion 19 must be corrected** — it currently asserts the
  neighbours are 24px, which would have a QA tester sign off on the wrong thing.

- [x] **S3. Help icon's guest-only visibility is still unconfirmed** — `src/ProoveGaming.App/Views/HomeView.axaml:99` — Should-fix — *closed: confirmed guest-only by the requester on 2026-07-31. `IsVisible="{Binding IsGuest}"` stays as written.*
  **Problem / why it matters:** `IsVisible="{Binding IsGuest}"` was inferred from the request's
  wording ("in guest mode" attached to both asks), not confirmed. If the intent was a help
  affordance for everyone, signed-in users silently lose it, and nothing in the code or tests
  would reveal the mistake. The binding itself is sound — `HomeView.axaml:9` declares
  `x:DataType="viewmodels:HomeViewModel"` so this is a compiled binding against a real property,
  and `IsGuest` raises `PropertyChanged` on session change (`HomeViewModel.cs:194-197`), so the
  icon appears and disappears correctly on guest → signed-in transitions.
  **Fix:** confirm with the requester. If it should be always-visible, delete line 99 — no other
  change needed.

#### Tests

- [ ] **T1 (carried from Round 1, still open).** Resource-key uniqueness assertion. Unchanged —
  nothing in the build or suite would catch a duplicate `x:Key` today, which is how the Round 1
  blocker got in. `tests/ProoveGaming.App.Tests/DatePickerViewTests.cs`.
- [ ] **T2 (carried, still open).** `ResourceStrings.Get` fallback path untested.
- [ ] **T4 (carried, still open).** Border rendering for `pgFieldBox` — no test, no executed QA.
- [ ] **T5 (new).** Fixes 6 and 7 are both untestable in this project and both remain **entirely
  unverified** — not by test, and not by an executed manual pass. Fix 6 additionally needs a
  server response that happens to contain `_` to observe at all. This is not a request for
  tests that cannot exist; it is a statement that two of the branch's five surviving fixes rest
  on reasoning alone. QA steps 16–19 are the only gate and must actually be run before merge.

#### Nits

- Commit `e459bee` is titled "fixed _", which conveys nothing to anyone reading `git log` later;
  the change is "stop suggestion chips consuming underscores as access keys". Worth a reword if
  the branch is rebased before merge.
- The branch now spans three unrelated concerns (profile/auth validation, a shared-control
  rendering bug, guest-mode header work). The plan's own scope-drift banner already says fix 7
  belongs in its own plan — acting on that at merge time would make the history readable.

#### What's done well

- Fix 6 is the right fix in the right place: one rule on the shared class rather than six
  call-site edits, so any future chip inherits it, with a comment explaining *why* the
  underscore vanishes — which is the non-obvious part a future reader would otherwise re-derive.
- The notification enablement was justified by checking the feed is device-driven rather than
  assuming; that check is what makes it a one-attribute change instead of a ViewModel refactor,
  and the reasoning is recorded in the plan.
- The `App.axaml:317-340` hover-pair trap (rules live away from the markup they affect) is
  written into the change list, so the next person adding a header icon will not repeat it.

#### Verdict

Ship: no — no blockers this round, and both new fixes are correct in substance: fix 6 is
complete and its exposure fully swept, and fix 7's notification path was verified end to end
rather than assumed. What holds it back is that **S1** and **S2** together mean the one visible
new element ships looking wrong and behaving inertly, and **T5** — nothing on this branch's last
two fixes has been observed working.
First: 1) decide **S1** (wire the icon, or drop `Cursor="Hand"`), 2) fix **S2** sizing and
correct acceptance criterion 19, 3) actually run QA steps 16–19 and 4–7, which are now the sole
verification for four of the branch's fixes.

### Round 3 — Ship: no

**No source changed since Round 2.** The branch is now fully committed — three commits
(`e41c967` fixes 1–4 + fix-5 revert, `e459bee` fix 6, `05f1c25` fix 7), clean working tree, no
staged or untracked files, so `git diff main...HEAD` is the entire surface. `05f1c25` contains
byte-for-byte what Round 2 reviewed (it bundled the fix-7 code together with Round 2's own
review text). **Every Round 2 finding is therefore still open and unaddressed** — this round
does not restate them; see Round 2 for the detail. What follows is a full-branch pass against
the committed state, which the previous rounds could not do because the work was uncommitted.

#### Plan conformance

Re-verified against HEAD rather than the working tree. All confirmed still true:

- **Round 1's blocker B1 has not regressed.** Duplicate-key scan of the committed dictionaries:
  `Strings.en.axaml` 326 declarations / 326 distinct, `Strings.uk.axaml` 343 / 343 — no
  duplicates in either. The fix survived two subsequent commits.
- **The fix-5 revert is complete in HEAD,** not just in the working tree:
  `WelcomeViewModel.cs:564` is back to `^[\x21-\x7E]{8,}$`; `Password character rule` is absent
  from `docs/CONTEXT.md`; `WelcomeViewModelPasswordRuleTests.cs` is absent from the HEAD tree.
  No orphan of the reverted fix remains.
- **Fix 4 survived the revert.** `WelcomeView_Notification_ResetLinkSent` is present in the
  committed `uk` dictionary — worth confirming explicitly, since the revert touched the same
  region of the same files and could plausibly have taken it along.
- **Committed HEAD builds and tests clean:** 0 errors, 631 tests green (288 App, 343
  Infrastructure).
- **Glossary delta is correct** for fixes 1 and 3 — `DatePicker resource keys`, `pgFieldBox`
  and `ResourceStrings` are all present and accurate. But see **S4**.

#### Code

- [ ] **S4. The header-icon hover trap is documented only in this plan, which is the one place a future contributor won't look** — `docs/CONTEXT.md`; pattern at `src/ProoveGaming.App/App.axaml:317-340` — Should-fix
  **Problem / why it matters:** fix 7 established (and the Round-2 journal records) that a
  header icon's hover background is driven by a `Path#<Name>Bg` +
  `Button#<Name>Button:pointerover Path#<Name>Bg` pair living in `App.axaml`, physically
  separated from the `HomeView.axaml` markup it controls. Adding an icon without its pair makes
  the background render permanently — which is exactly what happened during this branch, and it
  was found only because a human spotted it on screen. That knowledge currently lives in a
  plan document, and plans are per-change artefacts: the next person adding a header icon will
  read `HomeView.axaml`, grep it for `pointerover`, find nothing, and repeat the bug.
  `CLAUDE.md` makes the glossary the durable index for exactly this kind of repo-specific,
  non-obvious convention, and it is where a contributor actually looks.
  **Concrete scenario:** someone adds a "downloads" icon to this header next sprint, copies the
  `SearchButton` block, and ships an icon with a permanently-lit background — the identical
  defect, rediscovered the identical way.
  **Fix:** add a one-line glossary entry to `docs/CONTEXT.md`, e.g. *"**Header icon hover pair**
  — each home-header icon needs two styles in `App.axaml` (`Path#<Name>Bg` hidden by default,
  `Button#<Name>Button:pointerover Path#<Name>Bg` revealing it); they live there, not beside the
  markup in `HomeView.axaml`, so a new icon added without them renders its background always-on."*
  Optionally a second entry for the `suggestionButton` access-key rule from fix 6.

#### Tests

- [ ] **T1, T2, T4, T5** — all carried from Rounds 1–2, all still open, none addressed. **T5 is
  now the branch's defining risk:** fixes 6 and 7 remain unobserved by any means, and QA steps
  4–7 and 16–19 have not been run. Note again that `dotnet-ci.yml` builds only the App csproj,
  so the 631 green tests come from local runs only — CI would not have caught a test regression
  on this branch either.

#### Nits

- All three commit messages are uninformative — "profile and sign in fixes", "fixed _", "added
  buttons". After merge this is the permanent record of a branch that changed localization
  plumbing, a shared style system, nickname error handling and the guest header. Worth
  rewording on rebase.
- `05f1c25` bundles Round 2's review text into the same commit as the code that review
  criticises, so the commit contains both the finding and the unfixed subject of the finding.
- Ruled out, recorded so it is not re-investigated: the hidden `HelpButton` does **not** leave a
  phantom 16px gap for signed-in users. This `StackPanel` (`Spacing="16"`) already ships with a
  conditionally-hidden child in both modes — `ProfileButton` (`!IsGuest`) and `GuestButton`
  (`IsGuest`) — so the layout behaviour under a collapsed child is pre-existing and unchanged.

#### What's done well

- The branch is committed in three coherent slices that match the three logical concerns, which
  makes the scope drift easy to split at merge time if that is wanted.
- The Round-1 blocker fix held across two further commits without regressing — verified, not
  assumed.
- The fix-5 revert is genuinely complete: regex, both dictionaries, the test file and the
  glossary entry all returned to their pre-change state with nothing orphaned, which is the
  failure mode reverts usually have.

#### Verdict

Ship: no — unchanged from Round 2, and for unchanged reasons: **S1**, **S2** and **S3** are all
still open, so the one user-visible new element still ships undersized and inert. Nothing has
regressed and the committed state is verifiably clean, so this is a short list of small fixes
plus a manual pass, not rework.
First: 1) **S1** and **S2** (both one-line edits to `HomeView.axaml`, plus correcting acceptance
criterion 19), 2) run QA steps 4–7 and 16–19 — **T5**, the only verification four of these fixes
will ever get, 3) **S4**, so the hover-pair trap outlives this plan.

### Round 3 follow-up — external PR review (#75), M1–M2 addressed

External review by `boikoserhii84` on
[PR #75](https://github.com/wezom-company/proove-gaming/pull/75), 2026-07-31. It raised two
Major findings (**M1**, **M2**) and nine Minor (**m1–m9**). The requester scoped this round to
"up to M2", so **M1 and M2 are handled here and m1–m9 are deliberately left open** — they are
listed at the end so nothing is lost.

**M2 — fixed (code + tests).** Confirmed accurate on every point before acting, including the
part that criticised my own work:
- `ConfirmEditNickname` sends **all** profile fields to `api/profile/update`
  (`ProfileViewModel.cs:589-597`), so a rejection can name a field the user never touched in
  this dialog — a stored phone the server dislikes, say. The old `HandleNicknameFailure` only
  surfaced messages resolving to `AuthField.Nickname`, so that case fell through to the toast.
- No branch of `HandleProfileUpdateError` closes the dialog except the token path (via
  `SessionTokenInvalid` → `ClearLocalState`). The toast therefore rendered behind the overlay
  with the dialog still open — pressing CONFIRM did visibly nothing.
- **The comment I had written above the method was factually wrong** — it claimed the shared
  handler "shows the toast after close". There is no close. The comment was actively masking
  the bug for the next reader; it has been rewritten to state what actually happens.

Implemented **Option A**: `HandleNicknameFailure` now delegates to `HandleProfileUpdateError`
**only** for `InvalidOrExpiredToken` / `TokenRequired`, and puts everything else inline with the
precedence nickname-field message → any other resolved field message → `response.Message` →
new localized `ProfileView_EditNickname_Error_Generic` (added to both dictionaries; duplicate-key
scan re-run, 327/327 en and 344/344 uk, clean). No silent path remains inside the dialog.
This also resolves the reviewer's **m6** (stale error text surviving a second CONFIRM), since
every failure branch now overwrites `EditNicknameError`.

Tests — `tests/ProoveGaming.App.Tests/ProfileViewModelEditNicknameTests.cs`, now 14:
- `ConfirmEditNickname_NetworkFailure_LeavesFieldErrorEmptyForTheToastPath` **rewrote** to
  `…_ShowsTheFailureInlineInTheDialog`. The reviewer was right that it *pinned the defect*: it
  asserted the absence of feedback, so it would have passed forever while the bug stayed.
- Added `…_UpdateRejectsAnotherProfileField_ShowsThatErrorInline` — M2's exact scenario
  (`PHONE_INVALID` from `update`).
- Added `…_UnrecognizedCodeWithNoMessage_FallsBackToLocalizedGeneric`.
- Added `…_ExpiredToken_BouncesTheSessionInsteadOfShowingAFieldError` — guards that the one
  legitimate exit path stays an exit path.

**M1 — accepted, no code change (requester decision, 2026-07-31).** The finding is factually
right: `HelpButton` still shows the hover background via `App.axaml:323-328` with no command
behind it, and it is shown to guests — the users most likely to click a question mark. The
requester chose to **keep it as an inert placeholder**, which is consistent with `SearchButton`
and `SettingsButton` in the same header (both inert, both with hover backgrounds, both
pre-dating this branch). Residual risk, stated plainly rather than closed away: a guest can
click the icon and get nothing, and it is indistinguishable from a broken feature. It should be
wired before this header ships to real users. Round 2 **S1** stays fixed as it was (no
`Cursor="Hand"`); **S3** is now closed — guest-only confirmed.

**Left open from the PR review (m1–m9), out of scope this round:**
- **m1** — `pgFieldBox` duplicates the pre-existing `Border.pgInputHost` (`App.axaml:29-37`),
  four lines from where the new `StyleInclude` was added. Same three colours, differing only in
  trigger (`.filled` vs `:focus-within`) and one alpha unit (`#4C` vs `#4D`). The branch applied
  `CLAUDE.md`'s no-duplicate-UI rule inside the profile and missed the nearest neighbour; worse,
  it standardised on `#4CBEBCEA`, which is now the repo's only occurrence, while `#4DBEBCEA`
  appears 30+ times. **This is the most substantive of the minors** and partly undercuts the
  stated justification for widening the diff into `ProfileView.axaml`.
- **m2** — the help-icon height rationale is still wrong, in the opposite direction from Round 2.
  `SettingsButton`'s path *does* carry `Width="24" Height="24" Stretch="Uniform"`, so the gear is
  24, not ~27; only the bell is unconstrained. Actual row: search 24, help 27.0, bell 26.7,
  gear 24. My Round-2 **S2** fix made it match one neighbour instead of two, and acceptance
  criterion 19 is *still* wrong. Needs re-measuring and a decision on one row height.
- **m3** — resource-key uniqueness assertion (this plan's **T1**), open since Round 1.
- **m4** — `ResourceStrings.Get` fallback untested (**T2**).
- **m5** — header hover-pair trap not in `docs/CONTEXT.md` (**S4**).
- **m7** — `DatePickerResourceKeys` exposes mutable public arrays.
- **m8** — CI does not run `tests/`, so this branch's +367 test lines never execute in CI.
- **m9** — commit messages and mixed topics.

**Verdict after this round:** M2 closed, M1 accepted by decision, S3 closed. Ship: still **no** —
**m1** and **m2** are real and unaddressed, **T5** is unchanged (fixes 6 and 7 still never
observed running), and criterion 19 remains factually wrong after two attempts at it.
