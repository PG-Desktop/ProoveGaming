# Plan: Make the Change Password flow match the Forgot Password flow

## Goal

Replace the current signed-in "Change password" behaviour — Profile → Security →
"Change password" calls `POST api/profile/password_link` and launches an external
browser at the returned `password_change_url` — with the same in-app flow the
Welcome screen's Forgot Password feature already uses: an in-app email step that
calls `POST api/forgotten`, followed by a "Check your email" confirmation. The
actual password change still happens on the website via the link in the reset
email. This is exactly the revised flow the v1.7 API doc prescribes for §11
(`docs/knowledge-base/proove-gaming-launcher-api-v1.7-en.md`, "Flow (v1.7
revised)"): *launcher shows an email input dialog → user enters the account
email → launcher calls POST /api/forgotten → API sends reset email → user sets
new password on the site*.

*(2026-08-25: the other half of §11 — the launcher receiving `INVALID_OR_EXPIRED_TOKEN`
after a password change and offering re-login — is now honored globally, not just on
`ProfileViewModel`'s own calls: every token-bearing request flows through
`ApiHttpClient`, whose `TokenRejected` event signs the user out to Sign In with a
localized "session expired" notification. See `docs/plans/add-global-token-expiry/`.)*

## Scope

**In:**

- A new **Change Password dialog** in `ProfileView`, opened by the existing
  Security → "Change password" row, using the established Profile popup pattern
  (same 620×308 framed `Popup` as the Edit Email / Delete Account dialogs).
  A single email-input state mirroring the Welcome forgot-password input step:
  email field pre-filled with the signed-in user's `User.Email` (read-only —
  see the 2026-08-17 note below), a
  "Send reset link" primary button, Cancel, the sidebar close button, inline
  field/API errors, busy state on submit. On success the dialog closes and the
  standard side notification (`WelcomeView_Notification_ResetLinkSent` — the
  same one the Welcome forgot flow shows) confirms the send.
  *(2026-08-14: an in-dialog "Check your email" confirmation state was built
  first and then removed per user feedback — the side notification alone is
  the confirmation, exactly as on the Welcome forgot-password flow.)*
  *(2026-08-17: the email field was made fully inert per user feedback —
  `IsReadOnly` + `Focusable="False"` + `IsHitTestVisible="False"` on the
  TextBox, so it takes no focus, caret or clicks. The dialog only confirms the
  account's address; the reset link can go nowhere else. Not
  `IsEnabled="False"`, whose disabled style would dim the address. The
  ViewModel's format validation and empty-email CanExecute gate stay as
  safety nets and remain covered by the same tests.)*
- Re-point `ProfileViewModel.ChangePassword` from
  `GetPasswordChangeLinkAsync` + `Process.Start(url)` to opening the dialog;
  submit calls the existing `IAuthApiService.ForgottenPasswordAsync(email)`.
- Extract the forgot-password error-code mapping currently private in
  `WelcomeViewModel.MapForgotError` into a small shared helper so both
  ViewModels resolve `EMAIL_REQUIRED` / `INVALID_EMAIL` / `EMAIL_NOT_FOUND` /
  `MAIL_ERROR` / unknown identically, against the **existing**
  `WelcomeView_Forgot_Error_*` resource strings (no duplicate error copy).
- Remove the now-dead `password_link` client surface:
  `IAuthApiService.GetPasswordChangeLinkAsync`, its `AuthApiService`
  implementation, the `PasswordChangeLinkResponse` DTO, the
  `ProfileView_Notification_ChangePasswordFailed` resource string (en/uk), and
  the corresponding members of every test fake implementing `IAuthApiService`.
- Reset the new dialog state in `ProfileViewModel.ClearLocalState` (same rule
  as the existing Edit Email / Delete Account dialogs: a session-token bounce
  must not leave a stale overlay covering the Welcome screen).
- English + Ukrainian localization for the new dialog labels.
- Unit tests for the new ViewModel branches (see Test strategy).

**Out:**

- **In-app reset-code / new-password entry.** Unchanged from the forgot
  password plan: the password change completes on the website;
  `api/reset_password` stays unused by the launcher.
- **Changes to the Welcome forgot-password UX.** Its behaviour is untouched;
  only the private error mapper moves to a shared helper (same keys, same
  branches, existing tests must stay green).
- **Sign-out-on-send.** We do not proactively sign the user out after sending
  the reset email. Server-side token invalidation happens only when the user
  actually completes the reset on the website, and the existing
  `INVALID_OR_EXPIRED_TOKEN` → `SessionTokenInvalid` bounce (already wired in
  `ProfileViewModel`, see the auto-save comment referencing v1.7 §11) handles
  it on the next protected call. No new code needed.
- **Server-side changes.** `api/forgotten` is used as documented; the backend's
  `password_link` endpoint keeps existing but the launcher stops calling it.

## Options considered

### A. Which endpoint backs the signed-in change-password action

- **A1 — `POST api/forgotten` with an email input dialog (recommended).**
  Identical flow, error surface, and confirmation UX as the Welcome forgot
  password feature — which is precisely what was requested — and it is the
  flow the v1.7 doc's §11 diagram now prescribes for the launcher. Cons: the
  user can technically type a different account's email (same as on the
  Welcome screen); the server answers `EMAIL_NOT_FOUND` for unknown addresses.
- **A2 — Keep `password_link`, add only an in-app confirmation step.** The
  launcher would still open a browser at `password_change_url`. Cons: not
  "the same flow as forgotten password" (no email step, no check-your-email
  state), contradicts the v1.7 flow diagram, keeps the browser hand-off the
  request is asking to remove.

**Pick A1.**

### B. Where the change-password UI lives

- **B1 — Popup dialog in `ProfileView`, same frame as Edit Email / Delete
  Account (recommended).** The Security section already opens its destructive
  sibling (Delete account) as a framed popup with a field, inline error, and
  Cancel/Confirm buttons; the Edit Email popup even has the exact email field
  with the envelope icon to mirror. Zero navigation changes.
- **B2 — Navigate to the Welcome screen's forgot-password panel.** Reuses the
  existing panel verbatim. Cons: leaving the Profile for an auth-screen panel
  while signed in is a jarring navigation (the Welcome view is the signed-out
  surface); `WelcomeViewModel` state is entangled with sign-in/sign-up tabs.
- **B3 — Inline expansion under the "Change password" row.** No dialog. Cons:
  no precedent in the Security section, pushes the Delete account link around,
  and diverges from how every other Profile edit works (all are popups).

**Pick B1.**

### C. What happens to the dead `password_link` client code

- **C1 — Remove it (recommended).** `GetPasswordChangeLinkAsync`,
  `PasswordChangeLinkResponse`, and the failure-notification string become
  unreachable once the command stops calling them. Dead API surface invites
  accidental reuse and every `IAuthApiService` fake has to stub it forever.
  Re-adding later is trivial from git history if the backend flow reverts.
- **C2 — Keep it unused.** Smaller diff, keeps parity with the doc's §11
  response shape. Cons: permanent dead code plus a dead localization key in
  two languages; the repo convention (see the forgot-password plan's removal
  of the browser-open path) is to delete superseded paths.

**Pick C1.**

### D. Error copy for the new dialog

- **D1 — Reuse the existing `WelcomeView_Forgot_Error_*` strings via a shared
  mapper (recommended).** The messages ("We couldn't find an account with that
  email.", "Couldn't send the email. Please try again.", generic) are
  context-free and already translated. Cons: the key prefix says
  `WelcomeView_` while `ProfileView` now also consumes them — a naming smell,
  but renaming the keys would churn four resource/view files for zero user
  benefit. Not worth it now; note it and move on.
- **D2 — Duplicate the three messages under `ProfileView_ChangePassword_*`
  keys.** Clean naming, but two copies of identical copy in two languages is
  exactly the drift CLAUDE.md's don't-duplicate rule exists to prevent.

**Pick D1.**

## Recommended approach

Open a two-state popup dialog from the existing `ChangePasswordCommand` and
drive it with forgot-password-identical logic on `ProfileViewModel`, submitting
through the already-implemented `ForgottenPasswordAsync`.

### State model (added to `ProfileViewModel`)

```
[ObservableProperty] private bool _isChangePasswordOpen;        // popup visibility
[ObservableProperty]
[NotifyCanExecuteChangedFor(nameof(SendChangePasswordResetCommand))]
private string _changePasswordEmail = string.Empty;
[ObservableProperty]
[NotifyCanExecuteChangedFor(nameof(SendChangePasswordResetCommand))]
private bool _isChangePasswordSubmitting;
[ObservableProperty] private bool _changePasswordEmailError;    // client-side format check
[ObservableProperty] private string _changePasswordApiError = string.Empty;
```

### Command flow

```
[RelayCommand]
private void ChangePassword()          // existing command name — the Security row binding stays untouched
{
    ChangePasswordEmail = User.Email;
    ChangePasswordEmailError = false;
    ChangePasswordApiError = string.Empty;
    IsChangePasswordSubmitting = false;
    IsChangePasswordOpen = true;
}

[RelayCommand]
private void CancelChangePassword()    // Cancel button and sidebar close
{
    IsChangePasswordOpen = false;
    ChangePasswordEmailError = false;
    ChangePasswordApiError = string.Empty;
}

[RelayCommand(CanExecute = nameof(CanSendChangePasswordReset))]
private async Task SendChangePasswordResetAsync()
{
    ChangePasswordApiError = string.Empty;
    if (!EmailRegex.IsMatch(ChangePasswordEmail.Trim()))   // ProfileViewModel's existing EmailRegex
    {
        ChangePasswordEmailError = true;
        return;
    }
    ChangePasswordEmailError = false;
    IsChangePasswordSubmitting = true;
    try
    {
        ApiResponse response;
        try { response = await _authApi.ForgottenPasswordAsync(ChangePasswordEmail.Trim()); }
        catch
        {
            ChangePasswordApiError = LocalizationManager.GetString("WelcomeView_Forgot_Error_Generic");
            return;
        }
        if (response.Success)
        {
            IsChangePasswordOpen = false;
            _notification.Show(LocalizationManager.GetString("WelcomeView_Notification_ResetLinkSent"));
            return;
        }
        var (isFieldError, message) = ForgottenPasswordErrors.Resolve(response);
        ChangePasswordEmailError = isFieldError;
        ChangePasswordApiError = message;
    }
    finally { IsChangePasswordSubmitting = false; }
}

private bool CanSendChangePasswordReset() =>
    !IsChangePasswordSubmitting && !string.IsNullOrEmpty(ChangePasswordEmail);
```

Because the existing `ChangePassword` `[RelayCommand]` method is repurposed (async
Task → void), the generated `ChangePasswordCommand` keeps its name and the
`ProfileView.axaml` Security-row binding does not change.

`ForgottenPasswordAsync` needs no session token, so the dialog has no
`SessionTokenInvalid` branch — the token-bounce path in the old command dies
with `GetPasswordChangeLinkAsync`.

### Shared error mapper

New `src/ProoveGaming.App/Services/ForgottenPasswordErrors.cs`:

```
internal static class ForgottenPasswordErrors
{
    // Returns (isEmailFieldError, localizedMessage). Field errors additionally
    // highlight the email input; the message renders inline below it either way.
    public static (bool IsEmailFieldError, string Message) Resolve(ApiResponse response) { ... }
}
```

Branch-for-branch the body of today's `WelcomeViewModel.MapForgotError`
(`EMAIL_REQUIRED`/`INVALID_EMAIL` → field error + `WelcomeView_Error_Email`;
`EMAIL_NOT_FOUND` → `WelcomeView_Forgot_Error_NotFound`; `MAIL_ERROR` →
`WelcomeView_Forgot_Error_Mail`; anything else →
`WelcomeView_Forgot_Error_Generic`). `WelcomeViewModel` switches to calling it;
`MapForgotError` is deleted. `WelcomeViewModelForgotPasswordTests` guard this
refactor and must stay green unmodified (except any fake-interface fallout from
the `GetPasswordChangeLinkAsync` removal).

### Dialog layout (ProfileView.axaml)

A fourth `Popup` after the Delete Account one, gated by `IsChangePasswordOpen`,
copying the 620×308 canvas frame, `ColumnDefinitions="42,536,42"` content grid,
and sidebar close button (bound to `CancelChangePasswordCommand`) from the Edit
Email popup. Content column:

- Title `ProfileView_ChangePassword_Title` (pixel font, 28px — as siblings).
- Subtitle `ProfileView_ChangePassword_Subtitle`.
- Email field: the Edit Email dialog's `nicknameField` border + envelope icon
  + `profileField` TextBox, `Text="{Binding ChangePasswordEmail}"`,
  `IsReadOnly="True"` + `Focusable="False"` + `IsHitTestVisible="False"`
  (2026-08-17 — confirm-only and fully inert, see Scope note),
  `Classes.error="{Binding ChangePasswordEmailError}"`, placeholder
  `ProfileView_EditEmail_Placeholder` (reused — same field role).
- Inline error TextBlock bound to `ChangePasswordApiError` (`#FF383C`, 12px,
  visible when non-empty) plus the field-error highlight, mirroring siblings.
- Buttons right-aligned: secondary `Button_Cancel` →
  `CancelChangePasswordCommand`; primary `ProfileView_ChangePassword_Send` →
  `SendChangePasswordResetCommand` (disabled while submitting via CanExecute).

Success closes the popup; the side notification is the confirmation. *(The
in-dialog confirmation state was removed 2026-08-14 — see the dated note in
Scope.)*

### Localization keys to add (en + uk)

| Key | en | uk |
|-----|----|----|
| `ProfileView_ChangePassword_Title` | Change password | Зміна пароля |
| `ProfileView_ChangePassword_Subtitle` | We'll email you a link to set a new password. Confirm the address below and we'll send it. | Ми надішлемо на пошту посилання для встановлення нового пароля. Підтвердьте адресу нижче, і ми його відправимо. |
| `ProfileView_ChangePassword_Send` | SEND RESET LINK | НАДІСЛАТИ ПОСИЛАННЯ |

Titles are sentence case, matching the sibling Profile dialog titles
(`ProfileView_DeleteAccount_Title` = "Delete account"), not uppercase — the
pixel display font provides the visual weight.

Removed key (both languages): `ProfileView_Notification_ChangePasswordFailed`.
Reused keys: `ProfileView_EditEmail_Placeholder`, `Button_Cancel`,
`WelcomeView_Notification_ResetLinkSent` (success side notification),
`WelcomeView_Error_Email`,
`WelcomeView_Forgot_Error_NotFound` / `_Mail` / `_Generic`.
*(`ProfileView_ChangePassword_Sent_Title` and `Button_Close` existed briefly
for the removed confirmation state and were deleted with it on 2026-08-14.)*

## File-level change list

Ordered so the characterization guard (existing forgot-password tests) is in
place before behaviour moves — see Test strategy.

1. **`src/ProoveGaming.App/Services/ForgottenPasswordErrors.cs`** — new; shared
   error-code → (field-error, localized message) mapper extracted from
   `WelcomeViewModel.MapForgotError`.
2. **`src/ProoveGaming.App/ViewModels/WelcomeViewModel.cs`** — delete
   `MapForgotError`, call `ForgottenPasswordErrors.Resolve` instead (behaviour
   identical; existing tests stay green).
3. **`src/ProoveGaming.App/ViewModels/ProfileViewModel.cs`** — replace the
   `ChangePassword` command body with the dialog-open logic; add the state
   model, `CancelChangePassword`, and `SendChangePasswordResetAsync`; reset the
   new state in `ClearLocalState`; drop the dead token-bounce branch that
   belonged to `GetPasswordChangeLinkAsync`.
4. **`src/ProoveGaming.App/Views/ProfileView.axaml`** — add the Change Password
   popup (two states) after the Delete Account popup; Security row markup is
   unchanged.
4a. **`src/ProoveGaming.App/Views/MainWindow.axaml.cs`** — wire
   `IsChangePasswordOpen` into `OnProfileViewModelPropertyChanged` and the
   `RefreshDialogOverlay` blur gate, as every other Profile popup is. *(Added
   2026-08-14 during implementation — the original list missed that Profile
   popups get their 12-radius background blur from `MainWindow`.)*
5. **`src/ProoveGaming.App/Services/IAuthApiService.cs`** — remove
   `GetPasswordChangeLinkAsync`.
6. **`src/ProoveGaming.App/Services/AuthApiService.cs`** — remove the
   `GetPasswordChangeLinkAsync` implementation.
7. **`PasswordChangeLinkResponse` DTO** (locate under
   `src/ProoveGaming.App/Models/` or `Services/` — wherever it is declared) —
   delete.
8. **`src/ProoveGaming.App/Resources/Strings.en.axaml`** and
   **`Strings.uk.axaml`** — add the three new keys, remove
   `ProfileView_Notification_ChangePasswordFailed`.
9. **`tests/ProoveGaming.App.Tests/ProfileViewModelChangePasswordTests.cs`** —
   new; covers the dialog ViewModel branches (see Test strategy).
10. **Existing test fakes implementing `IAuthApiService`**
    (`ProfileViewModelEditEmailTests.cs`, `ProfileViewModelEditNicknameTests.cs`,
    `ProfileViewModelClearLocalStateTests.cs`,
    `WelcomeViewModelForgotPasswordTests.cs`, and any others a grep for
    `GetPasswordChangeLinkAsync` finds) — drop the removed member.
11. **`tests/ProoveGaming.App.Tests/ProfileViewModelClearLocalStateTests.cs`** —
    extend to assert the change-password dialog state is reset.
12. **`docs/plans/launcher-api-v1.6-gaps/launcher-api-v1.6-gaps.md`** — dated
    note on the §11 `password_link` row: the launcher no longer calls it; the
    Security row now drives the in-app `api/forgotten` flow.
13. **`docs/plans/add-forgot-password/add-forgot-password-plan.md`** — dated
    note in Scope: "Change-password when signed in" is no longer out of scope
    elsewhere — it now reuses this flow; link to this plan.
14. **`docs/CONTEXT.md`** — add/adjust glossary entries: **Change Password
    flow** (signed-in Security action → in-app email dialog → `api/forgotten`
    → website reset; `password_link` no longer used by the launcher).

## Acceptance criteria

- **AC1** Given a signed-in user on Profile → Security, when they click
  "Change password", an in-app dialog opens with the email field pre-filled
  with the account email; no browser launches and no network call has been
  made yet (`api/profile/password_link` is never called — the client method no
  longer exists).
- **AC2** The email field is fully inert: it takes no focus, caret or clicks,
  and the shown address stays the account email — while remaining as readable
  as the other dialog fields (no disabled dimming). At the ViewModel level
  (safety net, exercised by the unit tests directly) an invalid-format email
  skips the API call and sets the inline field error, and an empty email
  disables the send command. *(2026-08-17: rephrased — the field was editable
  before.)*
- **AC3** Given a valid email and the API returns `success: true`, the dialog
  closes and the side notification with the "reset link sent" text (the same
  `WelcomeView_Notification_ResetLinkSent` the Welcome forgot flow shows)
  appears. *(2026-08-14: replaces the earlier in-dialog "Check your email"
  state.)*
- **AC4** Given the API returns `EMAIL_NOT_FOUND`, the dialog stays open and
  shows the localized "We couldn't find an account…" message
  (same string the Welcome forgot-password panel shows for that code).
- **AC5** Given the API returns `MAIL_ERROR` or throws (network failure), the
  dialog stays open, shows the localized mail/generic error, and
  the send button is re-enabled (`IsChangePasswordSubmitting == false`).
- **AC6** Given the dialog was closed (by Cancel, the sidebar close button, or
  a successful send), reopening it starts fresh with the account email
  pre-filled again and no stale error text.
- **AC7** Given the dialog is open, when `ClearLocalState` runs
  (session-token bounce / sign-out), the dialog state is fully reset so the
  Welcome screen is not covered by a stale overlay.
- **AC8** The Welcome forgot-password flow behaves exactly as before the
  change: all existing `WelcomeViewModelForgotPasswordTests` pass without
  assertion changes.
- **AC9** `GetPasswordChangeLinkAsync`, `PasswordChangeLinkResponse`, and
  `ProfileView_Notification_ChangePasswordFailed` no longer exist anywhere in
  `src/` (grep returns nothing).
- **AC10** Both language files contain the three new keys (`Title`, `Subtitle`,
  `Send`) with non-empty values; switching language while the dialog is open
  updates its labels live.
- **AC11** `dotnet build ./src/ProveGaming.slnx` succeeds and
  `dotnet test ./src/ProveGaming.slnx` is green.

## Test strategy

Kind of change: **behaviour change** (`refactor-*`) to an existing feature.
The touched code splits in two:

- **The forgot-password error mapping being extracted** — already covered by
  `tests/ProoveGaming.App.Tests/WelcomeViewModelForgotPasswordTests.cs`. These
  are the characterization tests for the refactor: they exist before the
  change and must stay green through it (change list orders the extraction
  first). No new characterization needed.
- **The old `ChangePassword` browser-launch path** — has no tests and is being
  deleted wholesale, not preserved; characterizing a removed behaviour would
  be dead-on-arrival. Instead the replacement behaviour gets full `add-*`
  style coverage below, including a structural regression (AC9) that the old
  path is gone.

Framework: xUnit v2. New file
`tests/ProoveGaming.App.Tests/ProfileViewModelChangePasswordTests.cs`,
mirroring the setup in `ProfileViewModelEditEmailTests.cs` (hand-rolled
`RecordingAuthApi : IAuthApiService` — now recording `ForgottenPasswordAsync`
calls with a settable `ApiResponse` result and a throw switch —
`LocalStorageService("Data Source=:memory:")`, `UserProfile` with
`Email = "user@example.com"`, real `NotificationService`).

Tests to add:

1. `ChangePassword_OpensDialogPrefilledWithAccountEmail_NoApiCall` (AC1).
2. `SendReset_InvalidEmailFormat_SetsFieldErrorAndSkipsApi` (AC2).
3. `SendReset_EmptyEmail_CommandDisabled` (AC2 negative, via CanExecute).
4. `SendReset_Success_ClosesDialogAndShowsNotification` (AC3).
5. `SendReset_EmailNotFound_StaysOpenWithApiError` (AC4).
6. `SendReset_MailError_StaysOpenWithApiError` (AC5).
7. `SendReset_ApiThrows_SetsGenericErrorAndClearsBusy` (AC5).
8. `ReopenAfterSuccess_StartsWithAccountEmailPrefill` (AC6).
9. `ClearLocalState_ResetsChangePasswordDialogState` (AC7 — added to the
   existing `ProfileViewModelClearLocalStateTests.cs`).

Deliberately **not** covered by automated tests:

- Avalonia popup layout / visual state switching — manual QA below.
- Localization key resolution and live language switching — QA step.
- Real HTTP round-trip against the backend — manual QA.

## QA (manual test steps)

1. Launch the app and sign in with a real account. Open Profile (avatar) →
   Security tab. Expected: the "Change password" row and the red "DELETE
   ACCOUNT" link are visible.
2. Click "Change password". Expected: a dark framed dialog opens inside the
   app (no browser window opens) titled "Change password", with the account's
   email already filled into the email field, a "SEND RESET LINK" button and
   a "CANCEL" button.
3. Click on the email field and try to type. Expected: the field takes no
   focus and shows no caret; the address does not change and stays fully
   readable (not dimmed).
4. Click "SEND RESET LINK". Expected: the
   button briefly shows a busy/disabled state, then the dialog closes and a
   side notification appears saying the password reset link was sent — the
   same notification the sign-in screen's forgot-password flow shows.
5. Open the reset email in a browser, follow the link, and set a new password
   on the website.
6. Return to the app and edit any Account field (e.g. first name), or wait
   for the next background profile save. Expected: the app detects the
   invalidated session and returns to the sign-in screen.
7. Sign in with the new password. Expected: sign-in succeeds.
8. Reopen the Change Password dialog, replace the email with an address that
   has no account (e.g. `no-such-user@example.com`), and send. Expected: the
   dialog stays open and shows "We couldn't find an account with that email."
9. Disconnect from the internet and send again with a valid email. Expected:
   "Something went wrong. Please try again." appears and the send button is
   re-enabled.
10. With the dialog open, switch the app language (Profile → language
    selector) between English and Ukrainian. Expected: the dialog title,
    subtitle, buttons, and any visible error switch language immediately.

## Review focus

- `null-safety` — `User.Email` may be null/empty on prefill; `ApiResponse`
  error unpacking in the shared mapper.
- `async/await` — no sync-over-async on the send command; busy flag correct
  across success / error / throw; CanExecute reacts to both busy and text.
- `localization` — all new labels via resource keys; reused keys actually
  exist in both language files; removed key is gone from both.
- `UI-thread affinity` — the send command runs on the dispatcher via the
  awaited API call's captured context; no `ConfigureAwait(false)` in ViewModel
  code (repo convention).

## Performance & quality

None. One-shot user action (dialog open + one POST); no hot paths, no shared
mutable state beyond the ViewModel's own observables, no allocation-sensitive
code. The removed `Process.Start` was the only out-of-process interaction.

## Risks

- **Email enumeration surface.** As on the Welcome screen, `EMAIL_NOT_FOUND`
  reveals whether an address is registered. This is the API's documented
  contract (v1.7 §14); we surface it unchanged.
- **User edits the email to a different account's address.** The reset email
  then goes to that other account (which is how `api/forgotten` works from the
  Welcome screen too). Sending requires access to that inbox to do anything,
  so the risk profile equals the existing forgot-password feature. Accepted;
  the input stays editable to match the requested "same flow" and the v1.7 §11
  diagram ("user enters the account email").
- **Signed-in session keeps working after the email is sent.** Tokens are only
  invalidated when the reset is completed on the website; until then the app
  stays signed in. This matches the doc; QA step 7 verifies the bounce after
  completion.
- **Interface removal fallout.** Dropping `GetPasswordChangeLinkAsync` breaks
  every `IAuthApiService` fake until updated — the build catches all of them;
  the change list calls out the known four files plus a grep.

## Open questions

- None. The v1.7 doc pins the endpoint, error codes, and the revised §11 flow;
  the UI is a direct port of existing Profile dialog patterns and the existing
  forgot-password logic.

## Review findings

### Round 1 — Ship: yes

#### Plan conformance

Plan: `docs/plans/refactor-change-password-flow/refactor-change-password-flow-plan.md`
- Implemented as planned: shared `ForgottenPasswordErrors` mapper (extraction guarded by
  the pre-existing `WelcomeViewModelForgotPasswordTests`, all green unmodified); dialog
  state model + `ChangePassword` / `CancelChangePassword` / `SendChangePasswordResetAsync`
  on `ProfileViewModel`; two-state popup in `ProfileView.axaml` mirroring the sibling
  dialog frame; full removal of `GetPasswordChangeLinkAsync` / `PasswordChangeLinkResponse`
  / `ProfileView_Notification_ChangePasswordFailed` (grep of `src/` returns nothing);
  en+uk keys added; `ClearLocalState` reset + test; 9-case
  `ProfileViewModelChangePasswordTests`; all four doc updates (gaps doc, forgot plan,
  CONTEXT.md, this plan).
- Deviates from plan: two deviations, both already reconciled into the plan with dated
  notes — `MainWindow.axaml.cs` blur-gate wiring (item 4a, missed by the original list)
  and sentence-case dialog titles matching siblings. Verified both correct in code.
- Missing / partial: none.
- Accepted limitation (per plan): email enumeration via `EMAIL_NOT_FOUND` and the editable
  email field — residual risk unchanged from the existing Welcome forgot-password feature.
- Acceptance criteria: AC1–AC9, AC11 verified against code/tests; AC10 (live language
  switch) is `DynamicResource`-based and left to QA step 11 as planned.

#### Code

- [ ] **S1. Inline errors don't clear while the user retypes the email** —
  `src/ProoveGaming.App/ViewModels/ProfileViewModel.cs` (change-password state block,
  no `OnChangePasswordEmailChanged` partial) — Should-fix
  **Problem / why it matters:** both sibling dialogs in the same view clear their inline
  error on every keystroke (`OnEditNicknameTextChanged` at `ProfileViewModel.cs:465`,
  `OnEditEmailTextChanged` at `ProfileViewModel.cs:678`). In the new dialog,
  `ChangePasswordEmailError` (red border + "Please enter a valid email address.") and
  `ChangePasswordApiError` persist while the user edits the address and only clear on the
  next Send click — the user fixes the address and still stares at a red error until they
  resubmit. Inconsistent with the two dialogs one popup away.
  **Fix:** add
  `partial void OnChangePasswordEmailChanged(string value) { ChangePasswordEmailError = false; ChangePasswordApiError = string.Empty; }`
  next to the state block, mirroring the siblings. (Note: the Welcome forgot panel does
  not auto-clear either — strict "same flow" parity argues for leaving it; in-view
  consistency argues for adding it. In-view consistency wins: the user experiences this
  dialog next to Edit Email, not next to the Welcome panel.)

#### Tests

- [ ] If S1 is applied, add one case to `ProfileViewModelChangePasswordTests`:
  after a failed send sets the field/API error, mutating `ChangePasswordEmail` clears
  both — otherwise no gaps; the 9 cases cover every ViewModel branch including the
  server-side `INVALID_EMAIL` → field-error path the plan didn't explicitly list.

#### Nits

- Welcome's forgot flow shows a success notification (`WelcomeView_Notification_ResetLinkSent`)
  on send; the Profile dialog relies on its Sent state alone — `ProfileViewModel.cs`
  (`SendChangePasswordResetAsync`). Arguably correct (the dialog is the confirmation);
  flagging only so the asymmetry is a decision, not an accident.
- The plan's file-level change list uses `4a.` inside a numbered Markdown list, which
  renders oddly — plan doc only, no code impact.
- The VM's inner `try/catch` around `ForgottenPasswordAsync` is defensive dead code:
  `AuthApiService.PostAsync` (`AuthApiService.cs:110`) catches all transport exceptions
  and returns `IsNetworkError` responses, which route to the same generic message via the
  mapper's default branch. Harmless, and it mirrors the Welcome flow's identical pattern —
  keep for symmetry.

#### What's done well

- The error-mapper extraction is behaviour-identical and lands before the feature change,
  with the existing forgot-password tests acting as the characterization guard the plan
  demanded.
- The `MainWindow` blur-gate wiring was caught during implementation even though the plan
  missed it — without it the new popup would have been the only Profile dialog without the
  backdrop blur.
- Dead API surface (`password_link` method, DTO, notification string, four test-fake stubs)
  removed completely — grep-clean, no orphaned localization keys.
- `ClearLocalState` reset guards the PR #64 regression class for the new dialog, with
  assertions added to the existing regression test rather than a parallel one.

#### Verdict

Ship: yes — no blockers; S1 is a small in-view consistency polish that can land as a
follow-up or a one-line fix now. First: 1) S1 + its test case, 2) run QA steps 4–7
against the real backend (the reset email round-trip is untestable locally).

### Round 2 — Ship: yes

Scope of this round: the 2026-08-14 post-review rework that removed the in-dialog
"Check your email" confirmation state in favour of the side notification (user request),
plus verification that Round 1 findings were handled.

#### Plan conformance

- Implemented as planned (per the dated plan updates): success now closes the dialog and
  shows `WelcomeView_Notification_ResetLinkSent` — the same notification the Welcome
  forgot flow uses (`ProfileViewModel.SendChangePasswordResetAsync`); `IsChangePasswordSent`
  removed everywhere (grep-clean in `src/` and `tests/`); the now-unused
  `ProfileView_ChangePassword_Sent_Title` and `Button_Close` keys deleted from both
  language files; the popup is single-state; plan Scope / state model / layout /
  localization table / AC3, AC6, AC10 / QA steps all reconciled with dated notes; the
  Round 1 journal untouched.
- Deviates from plan: none.
- Missing / partial: none for the rework itself; Round 1 S1 remains open (below).
- Acceptance criteria: AC3 (close + notification) covered by
  `SendReset_Success_ClosesDialogAndShowsNotification` asserting both
  `IsChangePasswordOpen == false` and `NotificationService.IsVisible == true`;
  AC6 re-verified via `ReopenAfterSuccess_StartsWithAccountEmailPrefill`; error-path ACs
  now assert the dialog stays *open* (matching the reworded AC4/AC5). 795/795 tests green.

#### Code

- [ ] **S1 (carried from Round 1). Inline errors don't clear while the user retypes the
  email** — unchanged in this round; still the only open finding. The fix and its test
  case are as specified in Round 1.

#### Tests

- none new. The renamed/adjusted cases genuinely exercise the new behaviour (the success
  test asserts the notification via the real `NotificationService`, not a stub flag).

#### Nits

- Round 1's nit about the missing success notification is resolved by this rework — the
  asymmetry with the Welcome flow no longer exists.

#### What's done well

- The rework deleted its own dead weight in the same pass (two localization keys in both
  languages, the `IsChangePasswordSent` state, the confirmation panel) — no orphans left
  for the next cleanup.

#### Verdict

Ship: yes — the rework is complete and consistent; S1 remains an optional one-line
polish. First: 1) S1 + test if desired, 2) QA steps 3–4 (error inline + success
notification) against the real backend.
