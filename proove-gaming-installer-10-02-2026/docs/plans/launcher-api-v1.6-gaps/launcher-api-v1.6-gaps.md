# Launcher API — implementation gap analysis

Progress log for aligning the App with the Proove Gaming Launcher API. Now tracks both:

- [`docs/knowledge-base/proove-gaming-launcher-api-v1.6-en.md`](../../knowledge-base/proove-gaming-launcher-api-v1.6-en.md) — original reference the gap analysis was seeded from.
- [`docs/knowledge-base/proove-gaming-launcher-api-v1.7-en.md`](../../knowledge-base/proove-gaming-launcher-api-v1.7-en.md) — current spec. v1.7 is an English rewrite of v1.6 with a small tweak to the §11 password-change flow diagram; no functional endpoint/DTO changes.

Source of truth for the current client:

- `src/ProoveGaming.App/Services/AuthApiService.cs`
- `src/ProoveGaming.App/Services/IAuthApiService.cs`
- `src/ProoveGaming.App/Services/ApiResponse.cs`
- `src/ProoveGaming.App/Services/CustomerDto.cs`
- `src/ProoveGaming.App/Services/ApiErrorMessages.cs`
- `src/ProoveGaming.App/ViewModels/WelcomeViewModel.cs`
- `src/ProoveGaming.App/ViewModels/ChooseNicknameViewModel.cs`
- `src/ProoveGaming.App/ViewModels/ProfileViewModel.cs`
- `src/ProoveGaming.App/Views/MainWindow.axaml.cs`

---

## Done

### Endpoints wired

| Endpoint | Method on `IAuthApiService` | Where it's used |
| --- | --- | --- |
| `POST api/profile/check_nickname` | `CheckProfileNicknameAsync(token, nickname)` | `ProfileViewModel.ConfirmEditNickname` — validates before `profile/update`; 409 swaps the pool with server-provided `suggestions[]`. |
| `POST api/profile/password_link` | `GetPasswordChangeLinkAsync(token)` | `ProfileViewModel.ChangePassword` — bound to the Security → "Change password" row. Opens the returned URL via `Process.Start(UseShellExecute=true)`. Token errors bounce via `SessionTokenInvalid`; other failures surface as a notification. |
| `POST api/profile/delete` | `DeleteAccountAsync(token, password)` | `ProfileViewModel.ConfirmDeleteAccount` — behind a confirmation popup opened from the Security → "DELETE ACCOUNT" link. Success closes the popup and fires `SessionTokenInvalid` (matches the server-side token wipe described in §12). Error-code mapping: `INVALID_PASSWORD`, `PASSWORD_REQUIRED`, token errors, generic fallback. |
| `POST api/forgotten` | `ForgottenPasswordAsync(email)` (already existed) | New in-app forgot-password flow in `WelcomeViewModel` (`EnterForgotPassword` / `SendPasswordResetAsync` / `BackToSignIn`) replacing the old `Process.Start` link. Plan: [`docs/plans/add-forgot-password/`](../add-forgot-password/add-forgot-password-plan.md). |

> **2026-08-14 — `password_link` no longer called.** The Security → "Change password" row now
> opens an in-app dialog that posts to `api/forgotten` (the v1.7 §11 revised flow), matching
> the Welcome forgot-password UX. `GetPasswordChangeLinkAsync`, `PasswordChangeLinkResponse`,
> and `ProfileView_Notification_ChangePasswordFailed` were removed from the client. Plan:
> [`docs/plans/refactor-change-password-flow/`](../refactor-change-password-flow/refactor-change-password-flow-plan.md).

### DTO shapes

- `ApiResponse.Nicknames` (JSON `"nicknames"`) — populated by `POST api/check_email`; drives the register-flow nickname chooser.
- `ApiResponse.Suggestions` (JSON `"suggestions"`) — populated by 409 responses of both `api/check_nickname` and `api/profile/check_nickname`; drives the profile nickname edit's suggestion chips.
- `PasswordChangeLinkResponse { password_change_url, expires_in_minutes }` — returned by `api/profile/password_link`. *(Removed 2026-08-14 with the change-password flow rework — see note above.)*

### Nickname suggestions in UI

- **Register flow** (`ChooseNicknameViewModel`): the local `SuggestionPool` fallback was removed; the pool is seeded once from `check_email` (via `WelcomeViewModel.SignUpSucceeded → MainWindow.OnSignUpSucceeded → SetSuggestionsFromServer`). Refresh reshuffles 3 of 5 without a network call.
- **Profile edit flow** (`ProfileViewModel`): the hardcoded local pool was removed; `OpenEditNickname` fires a background `CheckNicknameAsync(User.DisplayName)` (public endpoint — returns 409 with 5 alternatives because the user's own nickname is registered). `profile/check_nickname` runs on Confirm to catch cases where the picked nickname is taken. The "Recommended nicknames" row is hidden until suggestions arrive (`HasNicknameSuggestions`).

### Local nickname validation on Confirm (profile edit)

- `ProfileViewModel.ValidateNicknameLocal` mirrors v1.7 §10 rules (Latin letters / digits / underscore, 3–20 chars, no spaces, non-empty) and runs before `CheckProfileNicknameAsync`. On failure it sets `EditNicknameError` to the localized `Auth_Error_Nickname*` string via `ApiErrorMessages.Resolve(code)` and short-circuits — no wasted round-trip.
- Inline red error text (`Foreground="#FF383C"`) rendered below the nickname field; auto-clears on the next keystroke via `OnEditNicknameTextChanged`.
- Field border turns red via a new `Border.nicknameField.error` style, toggled by `Classes.error="{Binding HasEditNicknameError}"`.

### Forgot-password flow

- In-app three-panel WelcomeView (sign-in / sign-up / forgot) with two forgot sub-states (email input, confirmation).
- Error-code mapping covering `EMAIL_REQUIRED`, `INVALID_EMAIL`, `EMAIL_NOT_FOUND`, `MAIL_ERROR` + generic fallback.
- 9 xUnit cases in `WelcomeViewModelForgotPasswordTests`.
- Tabs (SIGN IN / SIGN UP) hide while the forgot panel is showing.

### Security section UI (profile)

- "Change password" row (600×56, padlock + label + chevron) — `Command="{Binding ChangePasswordCommand}"`. *(Since 2026-08-14 the command opens the in-app change-password dialog instead of the browser — see note above.)*
- "DELETE ACCOUNT" red pixel-font link — opens a confirmation popup (620×308) with a password re-entry field, inline error text, and Cancel / DELETE buttons. Confirm calls `DeleteAccountAsync`; success bounces to Welcome via `SessionTokenInvalid`.
- Delete-account popup wired into `MainWindow.RefreshDialogOverlay` (`IsDeleteAccountOpen` case + `isDeleteAccountPopup` in the `dialogOpen` gate) so it gets the same 12-radius background blur as the other popups.
- Localized strings: `ProfileView_Security_ChangePassword`, `ProfileView_Security_DeleteAccount`, `ProfileView_DeleteAccount_Title` / `_Subtitle` / `_PasswordPlaceholder` / `_Confirm` / `_Error_InvalidPassword` / `_Error_PasswordRequired` / `_Error_Generic`, `ProfileView_Notification_ChangePasswordFailed`.

### Session-token invalidation on protected calls (v1.7 §11 "next request" pattern)

All protected API paths now surface `INVALID_OR_EXPIRED_TOKEN` / `TOKEN_REQUIRED` via `ProfileViewModel.SessionTokenInvalid`, which `MainWindow` handles by clearing local state and switching back to the Welcome screen:

- `GetProfileAsync`, `GetPasswordChangeLinkAsync`, `DeleteAccountAsync`, `UploadAvatarAsync`, and `UpdateProfileAsync` (via `HandleProfileUpdateError`) already checked.
- **Fixed**: `ScheduleProfileSave` — the debounced auto-save previously ignored the response; it now inspects the token codes and marshals the bounce onto the UI thread.
- **Fixed**: `MainWindow.OnProfileRequested` now `await`s `LoadFromServerAsync()` so navigating back into Profile is itself a token probe — the natural path a user takes after changing their password on the website.

### Profile view state reset on session boundaries

- `ProfileViewModel.ClearLocalState` (fires on logout, session-token invalidation, and post-delete-account) now also resets `SelectedSection = ProfileSection.Account` and `HoveredSection = null`. `ProfileViewModel` is a singleton, so without this the previous user's last-viewed tab (e.g. Security) leaked into a fresh sign-in / re-registration. In-session Home ↔ Profile navigation still remembers the last tab.

### Country selection (profile Account tab)

- New shared control `ProoveGaming.Common/Controls/CountrySelectView.axaml(.cs)`, modeled after `LanguageSelectView`: same header border + globe icon + up/down arrow, `#6357FF`-bordered dropdown with drop-shadow, `#1A6357FF` hover + `#336357FF` selected-row highlight, Check icon on the selected entry.
- Country list built from `CultureInfo.GetCultures(CultureTypes.SpecificCultures)` → `RegionInfo.EnglishName`, deduped by `TwoLetterISORegionName`, alphabetically sorted. `ExcludedRegions = { "RU", "BY", "KP" }` drops Russia, Belarus, and North Korea per product decision.
- Row controls are built lazily on the first `popup.Opened` (`EnsureItemsBuilt`), not in the constructor — `ProfileView` itself is constructed with `MainWindow`, so an eager build cost every launch ~250 rows of controls for a view most sessions never open. The selected-row check mark is a single `SvgImageView` re-parented into the selected row rather than one instance per row.
- Dropdown uses a `ScrollViewer MaxHeight="240"` (~190 entries) and scrolls the current selection into view on open.
- Public API: `Countries`, `SetSelectedCountry(string?)`, `Placeholder` (styled property), `event CountrySelected`.
- `ProfileView.axaml` replaces the old placeholder Country border with the control and binds `Placeholder="{DynamicResource ProfileView_Placeholder_Country}"` so it follows a runtime language switch; `ProfileView.axaml.cs` listens to `User.PropertyChanged` to push `User.Country` back into the control when it changes server-side (`LoadFromServerAsync`), and writes selections back into `User.Country` — which fires the existing debounced `ScheduleProfileSave` → `api/profile/update`.

### Date of birth picker (profile Account tab)

- New shared control `ProoveGaming.Common/Controls/DatePickerView.axaml(.cs)`, replaces the placeholder Date-of-Birth text field. Header row (55 px, right-anchored) uses the same border/hover pattern as the Country field: calendar icon, formatted date text, chevron with a `#4D6357FF` chamfered-octagon (kebab-menu) hover indicator scoped to the arrow area only.
- Popup: chamfered 312×344 `Path` panel (top-right + bottom-left 16 px cuts per design SVG), placed with `Placement="BottomEdgeAlignedRight"`, `IsLightDismissEnabled="True"`, `WindowManagerAddShadowHint="False"`. Popup shadow matches the HomeView profile-icon popup exactly: `DropShadowEffect Color="#6357FF" BlurRadius="40" Opacity="0.3"` on an outer padded Panel (with compensating `HorizontalOffset` / `VerticalOffset` on the Popup so the visible shape aligns with the field).
- Header inside popup: mode-dependent label in Pixel Font7 250-weight 18 px `#6357FF` uppercase (`MMMM yyyy` in day mode, `yyyy` in month mode, `2016 - 2027` in year mode), plus mode-toggle / prev / next buttons (each 24×24 with the same chamfered `#4D6357FF` hover as the presets kebab menu).
- Mode toggle cycles Days → Months → Years → Days. Month mode shows a 3×4 grid of `JAN…DEC`, year mode a 3×4 grid of a 12-year page; picking a year drills into its months, picking a month into its days. The prev/next arrows page by month / year / 12-year block depending on the mode. Without this the only way to reach a 1990 birth year would be ~430 month steps.
- Calendar block: 7-column grid, Sunday-first two-letter uppercase day headers (`SU MO TU WE TH FR SA`, Segoe UI SemiBold 16 / line-height 24 / `#B2BEBCEA`), and **6** date rows of 40×40 cells. Six rows are mandatory, not cosmetic: a month whose 1st falls on a Friday/Saturday spans 6 calendar weeks (e.g. October 2005), and a 5-row grid renders no cell at all for its trailing days. In-month numbers render white / Regular; outside-month numbers render `#4DBEBCEA` / Regular; on hover the cell fills with `#4D6357FF` and the number switches to SemiBold and forces white even for outside-month cells. Today's cell (when unselected and in the current-month view) gets a 1 px `#6357FF` border + SemiBold white number. The selected cell gets a solid `#6357FF` fill + SemiBold white number, no border, and keeps that solid fill on hover (doesn't dim to `#4D6357FF`); selected wins over today when they coincide. Selection is committed on click — sets `_committedDate`, updates the field text, fires `DateSelected`, and closes the popup.
- Custom flat-button theme (`BuildFlatButtonTheme`) binds `Background`/`BorderBrush`/`BorderThickness` from the Button through a wrapping `Border` so pseudo-class style setters (hover, selected) actually paint. `BorderBrush` for the selected cell is written directly on the button in `RenderDays` to guarantee the previously-selected outline always clears when the pending date changes.
- Public API: `event DateSelected`, `SetSelectedDate(DateOnly?)`, `Placeholder` (styled property), `SetDisplayFormat(string)`. Default format is `MM/dd/yy`.
- `ProfileView.axaml` swaps the DoB placeholder for the control and binds `Placeholder="{DynamicResource ProfileView_Placeholder_DateOfBirth}"`; `ProfileView.axaml.cs` subscribes to `DateSelected` → writes `User.DateOfBirth`, and re-syncs the control on `User.PropertyChanged`. `User.DateOfBirth` is already debounced through `ScheduleProfileSave` → `api/profile/update`.

### Password validation loosened

- Client-side sign-up regex relaxed from strict (uppercase + digit + special + no spaces + ASCII + ≥8) to `^[\x21-\x7E]{8,}$` (printable ASCII, ≥8, no spaces). Server error codes drive per-field feedback via `ApiErrorMessages`. Rationale: server-issued reset passwords (e.g. `c1a41b0441`, all-lowercase-alphanumeric) were being rejected client-side even though the server considers them valid.
- Sign-up hint copy dropped the "special character" clause in both `Strings.en.axaml` and `Strings.uk.axaml` (spec never required it).

### Profile follow-ups (post-#66)

- **Notifications section removed from the profile nav.** The nav panel had three entries (Account / Security / Notifications) but the Notifications one opened an empty `StackPanel` — there is no notifications endpoint wired, so it was a dead tab. Removed the nav item, its highlight/icon/label/click overlay, the empty content panel, `ProfileSection.Notifications`, `IsNotificationsSelected` / `IsNotificationsHighlighted` / `NotificationsIconBrush`, `SelectNotificationsCommand`, and the `ProfileView_Nav_Notifications` strings (en + uk). Nav octagon resized 232 → 164 px so the two remaining rows keep the original symmetric 16 px top/bottom padding; hit areas rebalanced to 82 px each. See "Still open" — the endpoints are unchanged, but the UI that lands them now has to be built rather than filled in.
- **Email-change verification step removed** — see the backend-gap section below for the full rationale.
- **Change-email Confirm is disabled until the address actually differs.** The dialog opens pre-filled with the current address, so `ConfirmEditEmailCommand.CanExecute` now returns `HasEmailChange` (non-empty **and** not equal to `User.Email`, trimmed + `OrdinalIgnoreCase` — a capitalisation-only edit is the same mailbox and isn't worth a request). Re-evaluated on both `EditEmailText` and `User.Email` changes, so it disables itself again after a successful update.
- **Nickname suggestion row restyled to match the registration step.** The profile dialog and `ChooseNicknameView` had drifted copies of a `Button.suggestionButton` class (different font size, padding, hover behaviour; the profile chips also carried a padlock icon and a hand-rolled refresh `Path`). Both now use one app-level definition in `App.axaml` (`Application.Styles`) plus new `Path.suggestionDivider` / `TextBlock.suggestionDividerLabel` classes; the local `<UserControl.Styles>` block in `ChooseNicknameView.axaml` is gone. Standardised on the registration look, keeping `Cursor="Hand"` from the profile copy (registration lacked it — a clickable chip should show a hand). One deliberate deviation: `MaxWidth="160"` on the profile chips, because that popup's content column is 536 px vs registration's 599 and three max-length (20-char) nicknames would otherwise spill outside the frame. No ViewModel changes — the suggestion logic was already shared-shaped.
- **Sign-out paths now land on a clean Sign In form.** `MainWindow.OnSessionTokenInvalid` (fired by delete-account *and* by any rejected token) cleared profile state but never touched `WelcomeViewModel`, so the deleted account's email stayed in the sign-in field and the tab was left wherever the user had it. It now calls `_welcomeViewModel.PrepareForFreshSignIn()` — the same call the logout path already made — which empties sign-in / sign-up / forgot fields, clears error flags, resets the attempt counter and lockout, and selects the Sign In tab.

### Glossary + docs

- `docs/CONTEXT.md` gained: **Bearer Token**, **Launcher API**, **Nickname generator**, **Reset Code**, **Session Token**, **Password reset flow**.
- v1.6 and v1.7 docs attached under `docs/knowledge-base/` (raw + English MD each).

---

## Still open

### Endpoints not yet implemented

| Endpoint | Purpose |
| --- | --- |
| `POST api/profile/notifications` | Read notification settings (newsletter flag). **Declined 2026-08-27 — not a gap to close.** |
| `POST api/profile/notifications_update` | Update notification settings. **Declined 2026-08-27 — not a gap to close.** |
| `/api/presets`, `/api/macros`, `/api/preferences`, `/api/devices/{vid}-{pid}` | Store presets and device settings per account. **Not in the v1.7 spec — proposed by us**, see [`docs/api/settings-sync-api.md`](../../api/settings-sync-api.md). Sent to the backend team; awaiting their answer on the approach, including whether REST verbs work on a stack whose existing endpoints are all `POST api/...`. |

Each also needs a UI / ViewModel flow — `ProfileViewModel` has no notifications toggle, and as of the post-#66 cleanup there is no Notifications nav section either (it was an empty tab; see Done). Wiring these endpoints now means re-adding the nav entry — restore the third octagon row (panel 164 → 232 px, three 77 px hit areas) along with `ProfileSection.Notifications` and its highlight / icon / label bindings.

### DTO shapes not yet added

- ~~`NotificationsResponse { notifications: { newsletter: int } }`.~~ **Declined 2026-08-27**
  — built on `features/newapi`, then removed with the toggle; see
  [`add-newsletter-toggle`](../add-newsletter-toggle/add-newsletter-toggle-plan.md).
- ~~Request DTO for `notifications_update`.~~ **Declined 2026-08-27** — same.

### `ApiErrorMessages.cs` coverage gaps

Codes documented in v1.6/v1.7 but still not mapped in the shared map — user sees generic fallback text:

- **Login**: `MISSING_CREDENTIALS`, `INVALID_CREDENTIALS`, `CUSTOMER_NOT_FOUND`
- **Token**: `TOKEN_REQUIRED`, `INVALID_OR_EXPIRED_TOKEN`
- **Reset**: `RESET_CODE_REQUIRED`, `INVALID_OR_EXPIRED_RESET_CODE`
- **Avatar**: `AVATAR_REQUIRED`, `INVALID_FILE_TYPE`, `FILE_TOO_LARGE`, `UPLOAD_FAILED`, `UPLOAD_ERROR`
- **Delete account**: `CONFIRMATION_REQUIRED`, `ACCOUNT_DELETE_FAILED`

Notes:

- `INVALID_PASSWORD` and `PASSWORD_REQUIRED` are handled inline in `ProfileViewModel.ConfirmDeleteAccount` (specific to the delete popup UI) rather than via the shared map.
- Forgot-password codes (`EMAIL_NOT_FOUND`, `MAIL_ERROR`) are handled inline in `WelcomeViewModel.MapForgotError` — could be centralized when we do this pass.

### Backend gap — email-change verification

The App used to carry a full email-change verification UI (`Change email` popup → 6-digit code entry step → resend timer → confirm). The v1.6/v1.7 spec describes no endpoint to power it, so the client accepted a hard-coded `"111111"` and then called `POST api/profile/update` with the new email — the UI *looked* verified while the server never emailed a code and never checked one.

**That mock step has since been removed.** The dialog is now single-step and matches the spec surface: local format validation, then `POST api/profile/update`, with `INVALID_EMAIL` / `EMAIL_ALREADY_EXISTS` surfaced inline next to the field (`ProfileViewModel.ConfirmEditEmail`). The backend gap below is still open — it is what a real verified flow would need.

To make it real end-to-end, the backend needs to add **two protected endpoints** and stop accepting a bare `email` field in `profile/update`:

1. **`POST api/profile/email/request_change`** — Bearer Token required.
   - Request: `{ "email": "new@example.com" }`
   - Behaviour: validates the new email (`INVALID_EMAIL`, `EMAIL_ALREADY_EXISTS`); generates a 6-digit code with a short TTL (spec should say — Reset Code today is 15 min, same magnitude is fine); stores it against the customer + pending new email; sends it to the **new** address so the user proves control of it (industry-standard flow; the reset-password flow already models this).
   - Success 200: `{ "success": true, "message": "Verification code sent" }`
   - Errors: `EMAIL_REQUIRED`, `INVALID_EMAIL` (400), `EMAIL_ALREADY_EXISTS` (409), `MAIL_ERROR` (500), `TOKEN_REQUIRED` / `INVALID_OR_EXPIRED_TOKEN` (401).
   - Optional: `resend_available_in_seconds` field so the launcher can size the resend countdown (right now the client hardcodes 59 s).

2. **`POST api/profile/email/confirm_change`** — Bearer Token required.
   - Request: `{ "code": "123456" }` (or `{ "code": "...", "email": "new@example.com" }` if the backend prefers not to hold pending state per session).
   - Behaviour: validates the code + associated pending email, updates `customer.email`, expires the code, invalidates other pending change requests. Optional: invalidate other Session Tokens like the password-reset flow does, so anyone with the old address can't stay signed in.
   - Success 200: `{ "success": true, "message": "Email updated successfully", "customer": { "customer_id": ..., "email": "new@example.com" } }`
   - Errors: `CODE_REQUIRED`, `INVALID_OR_EXPIRED_CODE` (400), `EMAIL_ALREADY_EXISTS` (409 — if someone else grabbed the address between request and confirm), `TOKEN_REQUIRED` / `INVALID_OR_EXPIRED_TOKEN` (401).

Related change to §6:

3. **Remove `email` from the `api/profile/update` editable-fields list.** Once (1) + (2) exist, `profile/update` should reject an `email` field (or silently ignore it) so the only path that can change email is the verified one. The current spec at §6 lists `email` as editable, which is what makes the client's mock-verification acceptable today — that mismatch is the actual root cause.

Client-side follow-up once the endpoints ship:

- Re-introduce the code-entry step (title/subtitle/6-digit boxes/resend timer were deleted along with the mock — rebuild from the design, don't resurrect the fake).
- Call `POST api/profile/email/request_change` from `ConfirmEditEmail` instead of `profile/update`, then confirm with `POST api/profile/email/confirm_change`.
- Drop `email` from the `UpdateProfileAsync` payload in `ProfileViewModel.ConfirmEditNickname` and `ScheduleProfileSave` (already never intended to change email, but it currently does re-send `User.Email` — harmless if server ignores it, but nicer to omit).

### Semantic / behavioural gaps

- **`UpdateProfileAsync` overwrites blanks.** `AuthApiService.cs` still coerces every `null` field to `string.Empty`, so editing a single field can blank out others server-side. Confirm expected semantics with the backend, then either send only changed fields or omit empties.
- **Client-side avatar validation missing.** Spec: 2 MB max, JPG / JPEG / PNG / WEBP only. `UploadAvatarAsync` ships whatever stream it gets; a client-side pre-check would surface `FILE_TOO_LARGE` / `INVALID_FILE_TYPE` without a round-trip.
- **HttpClient lifecycle / timeout.** `AuthApiService` still does `new HttpClient()` in the constructor with the 100 s default timeout, no retry, no `IHttpClientFactory`. Not a spec item — flagged for hardening.
- **`POST api/reset_password`** response is parsed as `ApiResponse<CustomerDto>` even though the spec says all Session Tokens are invalidated on reset. Call sites should force a re-login rather than trust any previously stored token.
- **Notification toggle absent from profile UI.** Registration collects `agree_newsletter` (`WelcomeViewModel`) but there is no ongoing UI to view or change it later — and the placeholder Notifications tab that used to imply one has been removed.

### Naming / minor

- `AvatarUploadResponse.AvatarUrl` maps `"avatar"` — matches spec.
- `ApiResponse<T>.Data` maps `"customer"` — fine for current call sites, but a notifications response (`{ notifications: { newsletter: int } }`) will need its own DTO rather than reusing `ApiResponse<T>`.

---

## Suggested next steps (prioritised)

1. **Backend**: add `api/profile/email/request_change` + `api/profile/email/confirm_change`, remove `email` from `profile/update`; then rebuild the verification-step UI against those endpoints.
2. Add DTOs + `IAuthApiService` methods for the two remaining endpoints (`api/profile/notifications` + `api/profile/notifications_update`), then re-add the Notifications nav section and build the newsletter toggle UI inside it.
3. Extend `ApiErrorMessages` map to cover login/token/reset/avatar/delete codes; migrate the inline `MapForgotError` and `ConfirmDeleteAccount` error switches onto the shared map.
4. Fix `UpdateProfileAsync` blank-overwrite semantics.
5. Add client-side avatar pre-validation (size + MIME).

---

## Review findings

### Round 1 — Ship: yes — PR #66 (features/profile-datepicker-country)

#### Code
- [x] `DatePickerView.SetDisplayFormat` XML docstring says "defaults to `dd'/'MM'/'yyyy`" but the actual field default is `MM'/'dd'/'yy` — `src/ProoveGaming.Common/Controls/DatePickerView.axaml.cs:110-111` — Should-fix — *fixed: docstring updated to `MM'/'dd'/'yy`.*
- [x] Plan section "Date of birth picker" claims `ScrollViewer MaxHeight="360"` but the code uses `240` — actually the plan sentence belongs under Country selection and the country ScrollViewer is `240` too — `src/ProoveGaming.Common/Controls/CountrySelectView.axaml:115` vs plan text above — Should-fix — *fixed: plan text updated to `240` (code is correct).*
- [x] Dead resource key `DatePicker_Save` (never referenced) and fossil comment `<!-- SELECT DATE removed — header label is now populated dynamically -->` — `src/ProoveGaming.App/Resources/Strings.en.axaml:79-80` and `Strings.uk.axaml:68-69` — Should-fix — *fixed: both removed from en + uk resource files.*
- [ ] Scope drift: PR converts 4 `PlaceholderText` → `Watermark` sites in `ProfileView.axaml` (First Name, Last Name, Phone, nickname edit, email edit) but leaves 7 sibling sites using `PlaceholderText` (`WelcomeView.axaml` x5, `ChooseNicknameView.axaml`, dialogs). Either extend the change to those files or split it into its own PR — `src/ProoveGaming.App/Views/ProfileView.axaml` — Should-fix — *deferred: keep as-is per author decision; sibling files stay on `PlaceholderText`.*
- [x] `ClearLocalState` clears code digits but not `IsCodeInvalid`, `IsResendAvailable`, `_resendSecondsLeft`, and doesn't cancel `_resendTimerCts`. If session invalidation catches the user mid-email-verification step, a stale countdown `Task` runs to completion and the red "invalid code" state persists into the next open of the dialog — `src/ProoveGaming.App/ViewModels/ProfileViewModel.cs:233-256` — Should-fix — *fixed: `ClearLocalState` now cancels `_resendTimerCts`, resets `_resendSecondsLeft`, `IsCodeInvalid`, `IsResendAvailable`, and re-notifies `ResendTimerText`.*
- [x] `CountrySelectView.SetSelectedCountry` matches by `RegionInfo.EnglishName` only — if the server ever returns a different spelling (localized name, ISO code, alternate name), the selection silently clears. Add a fallback (log or tolerant match) or make the round-trip contract explicit — `src/ProoveGaming.Common/Controls/CountrySelectView.axaml.cs:91-112` — Should-fix — *fixed: added `ResolveCountryIndex` (internal static) with `AliasToDisplayName` map covering `EnglishName` + `TwoLetterISORegionName` + `ThreeLetterISORegionName`; `SetSelectedCountry` now routes through it.*
- [ ] `_pendingDate` is now redundant — `OnDayCellClicked` sets both `_pendingDate` and `_committedDate` in the same instruction and closes the popup before anything else can mutate `_pendingDate`. Drop the field or restore the two-step pending → confirm flow — `src/ProoveGaming.Common/Controls/DatePickerView.axaml.cs:21-22, 217-218` — Nit
- [x] `CountrySelectView` allocates a fresh `SolidColorBrush(#1E1A4C)` on every `PointerEntered` — cache as `static readonly IBrush` — `src/ProoveGaming.Common/Controls/CountrySelectView.axaml.cs:167` — Nit — *fixed in round 2 (M3-C).*
- [x] `DatePickerView.OnDayCellClicked` runs `FindControl<Popup>("DatePickerPopup")` on every click; capture the popup reference in the constructor once — `src/ProoveGaming.Common/Controls/DatePickerView.axaml.cs:222-223` — Nit — *fixed in round 2: popup, calendar/period grids, header and field text blocks are all cached in the constructor.*

#### Tests (round 1)
- [x] `ProfileViewModel.ClearLocalState` — regression guard for the PR-64 review bug this PR fixes: set `IsEditNicknameOpen = IsEditEmailOpen = IsEmailVerificationStep = IsDeleteAccountOpen = true` and populate `EditNicknameText`, `EditNicknameError`, `EditEmailText`, `DeleteAccountPassword`, `DeleteAccountError`, `IsDeleteAccountSubmitting`, `CodeDigit1..6`; call `ClearLocalState()`; assert every one is reset. — *added `ProfileViewModelClearLocalStateTests` (5 tests). Later trimmed to 4 when the mock verification step (and with it `IsEmailVerificationStep` / `CodeDigit1..6`) was removed; `EditEmailError` took their place in the transient-text test.*
- [x] `CountrySelectView.BuildCountries` (extract to internal method or expose via `static IReadOnlyList<string> Countries`) — assert list is non-empty, sorted, contains e.g. "Ukraine" and "United States", excludes RU / BY / KP by ISO region name. — *added `CountrySelectViewTests` covering the static `Countries` list + `ResolveCountryIndex` (16 test cases, incl. ISO 2/3-letter alias lookups).*
- [x] `AuthErrorCodes` — pinning test that spot-checks a handful of constants against their string values (`TokenRequired == "TOKEN_REQUIRED"`, `NicknameAlreadyExists == "NICKNAME_ALREADY_EXISTS"`, etc.) so a rename can't silently break the server contract. — *added `AuthErrorCodesTests` (14 pinned constants).*

### Round 2 — PR #66 review comment (M1–M4)

- [x] **M1 — the calendar could not reach the last days of a 6-week month.** The grid was a fixed 5 rows / 35 cells starting from the Sunday on or before the 1st, so any month with `leadingBlanks + daysInMonth > 35` (2–3 months a year, e.g. October 2005, March 2025) rendered no cell for its trailing days — unpickable, with no indication they existed. With the text input removed there was no fallback path. — Blocker — *fixed via option A: `DateRows = 6` (42 cells), `CalendarBlock` `RowDefinitions="40,40,40,40,40,40,40"`, popup panel 304 → 344 px (outer padded panel 364 → 404, chamfer path updated). The 6th row shows next-month days styled `.outside`, as the existing style already handled.*
- [x] **M2 — `ModeToggleButton` was a dead control and there was no year navigation.** The button was declared with hover + hand cursor but had no handler, and the old `dd/MM/yyyy` `TextBox` was removed in the same branch — reaching a 1990 birth year meant ~433 prev-month clicks. — Blocker — *fixed via option A: the toggle now cycles Days → Months → Years → Days. A new `PeriodBlock` (3×4 grid, `periodCell` style mirroring `dayCell`) renders `JAN…DEC` or a 12-year page; picking a year drills to its months, a month to its days. Prev/next page by month / year / 12 years per mode, and the header label follows (`MMMM yyyy` / `yyyy` / `2016 - 2027`).*
- [x] **M3 — the 248-country list was materialised at app startup on the UI thread with ~750 SVG rasterizations.** `ProfileView` is declared in `MainWindow.axaml` (`IsVisible="False"`), so `CountrySelectView`'s constructor ran before sign-in and built every row, each with its own `ControlTheme` and its own `SvgImageView` check icon whose `SourceUri`/`Width`/`Height` assignments each re-ran the full parse → Skia render → PNG encode/decode cycle. — Blocker — *fixed via A + B + C + D: (A) one shared check icon re-parented into the selected row; (B) `EnsureItemsBuilt()` defers all row construction to the first `popup.Opened`; (C) `ControlTheme` and the `#1E1A4C` / `#336357FF` / `#B2BEBCEA` brushes are `static readonly` (same for `DatePickerView`'s per-cell theme, previously one instance per cell); (D) `SvgImageView` now caches rasterized bitmaps per `(uri, width, height, tint)`, which also removes the triple-load-per-instance waste everywhere else in the app.*
- [x] **M4 — Country / Date-of-Birth placeholders stopped following a runtime language switch.** Both were read once via `LocalizationManager.GetString` in the `ProfileView` constructor, and `ProfileView` is never re-created (navigation toggles `IsVisible`), so switching EN → UK left exactly those two fields in the old language while their siblings updated. — Blocker (localization regression) — *fixed via option A: `Placeholder` is now a `StyledProperty<string?>` on both controls, bound with `{DynamicResource ProfileView_Placeholder_Country}` / `_DateOfBirth` in `ProfileView.axaml`; the `SetPlaceholder` methods and their `FindControl` wiring in the constructor are gone.*

#### Tests (round 2)
- [x] `DatePickerViewTests` — 6 tests over the extracted static helpers `GridStartDate` / `GridCellCount` / `YearPageStart`: grid start is always the Sunday on or before the 1st, every day of every month from 1900–2100 lands inside the 42 cells (the M1 regression guard), the four known 6-week months land past index 35, and year pages always contain their year.

#### Tests (post-#66 follow-ups)
- [x] `ProfileViewModelEditEmailTests` — 12 cases over the now-single-step change-email flow: malformed address sets the inline error and skips the API; a valid address trims, calls `UpdateProfileAsync`, updates `User.Email` and closes the dialog; `EMAIL_ALREADY_EXISTS` keeps the dialog open with a field-level error instead of a toast; a guest session (no token) updates locally with no request; typing clears a previous error; and `HasEmailChange` / `CanExecute` are false for untouched, whitespace-only, capitalisation-only and empty input, true once the address differs, and false again after `User.Email` catches up.
- [ ] `MainWindow.OnSessionTokenInvalid` calling `PrepareForFreshSignIn` is **not** covered — `MainWindow` is a `Window` and `tests/` has no headless Avalonia harness. Verified by build only. Worth revisiting if a headless test host is ever added.
