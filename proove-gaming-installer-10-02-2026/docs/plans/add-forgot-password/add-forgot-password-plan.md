# Plan: Add in-app "Forgot password" flow

## Goal

Replace the current "Forgot Password?" link on the sign-in screen — which just
launches an external browser at `<UsersApiBaseUrl>account/forgotten` — with an
in-app flow that (1) prompts the user for their account email inside the
launcher, (2) calls the launcher API `POST api/forgotten` (already stubbed at
`IAuthApiService.ForgottenPasswordAsync`), and (3) shows a "Check your email"
confirmation. The actual password reset still happens on the website via the
link embedded in the reset email (per the API doc's *Password Security*
note: "Password changes are performed only on the website."). This aligns the
launcher with **v1.6** of the Proove Gaming Launcher API doc (saved at
`docs/knowledge-base/proove-gaming-launcher-api-v1.6-en.md`, section 14).

## Scope

**In:**

- New "Forgot password" step in the existing Welcome flow, reached from the
  `ForgotPasswordButton` on the sign-in tab (`Views/WelcomeView.axaml:302-316`).
  Two visual states inside the same step:
  1. **Email input** — a single email field, a "Send reset link" button, a
     "Back to sign in" link, and a busy indicator on submit.
  2. **Confirmation** — success message ("We've sent a password reset link to
     `<email>`. Open your email to continue."), plus a "Back to sign in"
     button. No timer / retry countdown in this pass.
- Wire `WelcomeViewModel.ForgotPassword` to open the new step **inside** the
  launcher instead of `Process.Start`-ing a URL. The URL open is removed.
- New `WelcomeViewModel.SendPasswordResetAsync` command that calls
  `_authApi.ForgottenPasswordAsync(email)`, maps the response (success /
  `INVALID_EMAIL` / `EMAIL_NOT_FOUND` / `MAIL_ERROR`) into inline error
  state, and on success flips the sub-view to the confirmation state.
- Email validation reusing the same regex/policy as sign-in
  (`EmailRegex` in `WelcomeViewModel.cs`). Prefill the field with
  `SignInEmail` if it is non-empty when the flow is entered.
- English + Ukrainian localization strings for the new labels, placeholder,
  and error messages (`Resources/Strings.en.axaml` and `Strings.uk.axaml`).
- Unit tests for the ViewModel branches (success, `INVALID_EMAIL`,
  `EMAIL_NOT_FOUND`, `MAIL_ERROR`, empty-email guard, back navigation).

**Out:**

- **In-app reset-code entry / new-password entry.** The API doc says the
  password change happens on the website. We do not surface
  `api/reset_password` in the App; the launcher never handles the reset
  code. Users complete the reset in a browser.
- **Change-password when signed in** (`POST api/profile/password_link`,
  section 11 of the API doc). Separate feature, separate plan; not in this
  change. *(2026-08-14: that feature now reuses this flow — the Profile →
  Security "Change password" action opens an in-app email dialog that posts
  to `api/forgotten`; see
  [`../refactor-change-password-flow/refactor-change-password-flow-plan.md`](../refactor-change-password-flow/refactor-change-password-flow-plan.md).)*
- **Rate limiting / lockout on the forgot-password button.** The API doc
  does not specify a rate-limit response for `api/forgotten`. We render
  whatever error the server returns; no client-side cooldown.
- **Deep-link handoff back to the launcher after reset.** Out of scope; the
  user re-opens the launcher and signs in with the new password. Session
  tokens are already invalidated server-side (per the doc).
- **Existing browser-open helper**. We keep it in the file for now
  (unused) only if another feature (`Terms of Service` link, etc.) still
  uses it; otherwise we delete the helper. Verify usage before removing.

## Options considered

### A. Where the "forgot password" UI lives

- **A1 — Extra sub-view inside `WelcomeView.axaml` (recommended).** Add a
  new `StackPanel` named `ForgotPasswordPanel` next to `SignInPanel` /
  `SignUpPanel`, gated by a new `WelcomeViewModel.IsForgotPassword`
  observable. Zero routing changes — the same view instance already
  swaps between sign-in and sign-up via `IsSignIn`. Pros: matches the
  existing pattern one-to-one, no new View file, no navigator changes,
  no `MainWindow` step to add. Cons: `WelcomeView.axaml` grows further
  (~85 more lines).
- **A2 — Dedicated `ForgotPasswordView` + `AppStep.ForgotPassword`.** A
  new step alongside `Welcome`, `AgreementTerms`, `ChooseNickname` in
  `MainWindow.axaml.cs`, wired through the same
  `_currentStep`/`UpdateStepUi` machinery. Pros: keeps `WelcomeView`
  small; opens the door for deep-linking later. Cons: three extra files
  (View, ViewModel, event plumbing) and a new step enum member for a
  two-visual-state feature; overkill for now.
- **A3 — Modal / dialog overlay.** Popup card over the sign-in form.
  Cons: no dialog primitive exists in the App today (all modals are
  inline panels — see `HomeViewModel.IsLogoutConfirmOpen`); adds a
  precedent we do not want to set without a design pass.

**Pick A1.** Cheapest, mirrors the existing sign-in/sign-up toggle,
zero DI or step-machine changes.

### B. Whether the App collects the email or hands off immediately

- **B1 — App collects the email, calls `api/forgotten` (recommended).**
  User stays in the launcher until the reset email arrives; only the
  actual reset happens in a browser. Uses the new API endpoint the doc
  documents specifically for the launcher.
- **B2 — Keep the current browser-open on click.** Skip the in-app step
  and just deep-link `…/account/forgotten`. Cons: contradicts the v1.6
  API doc (which introduced `api/forgotten` as a launcher-callable
  endpoint), and drops the user into a web page for something they
  could type once inside the launcher.

**Pick B1.**

### C. Where to route errors (409 / 400 / 404 / 500) on the confirmation

- **C1 — Inline error under the email input, matching sign-in style
  (recommended).** Reuse the existing `#FF383C` inline-error TextBlock
  pattern from `Views/WelcomeView.axaml:199-206`. One `ForgotApiError`
  observable, populated from the error-code → localized-string map.
- **C2 — Notification toast.** No, notifications are for background
  events; foreground validation errors sit next to the field.

**Pick C1.**

## Recommended approach

Add a third mutually-exclusive panel to `WelcomeView` (`ForgotPasswordPanel`)
and extend `WelcomeViewModel` with the forgot-password state and command.
The button already exists (`ForgotPasswordButton` at
`Views/WelcomeView.axaml:302`); it just needs its `Command` re-pointed at a
new "enter forgot state" command and its `Process.Start` behaviour removed.

### State model (added to `WelcomeViewModel`)

```
[ObservableProperty] private bool _isForgotPassword;          // panel visibility
[ObservableProperty] private bool _isForgotSent;              // confirmation state
[ObservableProperty] private string _forgotEmail = string.Empty;
[ObservableProperty] private bool _isForgotSubmitting;        // busy indicator
[ObservableProperty] private bool _forgotEmailError;          // client-side format check
[ObservableProperty] private string _forgotApiError = string.Empty; // localized server msg
```

Invariants:

- Exactly one of `IsSignIn`, `!IsSignIn && !IsForgotPassword` (sign-up),
  or `IsForgotPassword` is "true" for panel visibility. Entering forgot
  mode sets `IsSignIn = false, IsForgotPassword = true`; the "back"
  action does the reverse (`IsSignIn = true, IsForgotPassword = false`).
- `IsForgotSent` is false until a successful server response; on back
  navigation it is reset to false so re-entering the flow starts on the
  input state.
- `ForgotEmail` is seeded from `SignInEmail` on entry; entering "Back"
  doesn't discard the value on the sign-in tab.

### Command flow

```
[RelayCommand]
private void EnterForgotPassword()
{
    ForgotEmail = string.IsNullOrEmpty(SignInEmail) ? string.Empty : SignInEmail;
    ForgotEmailError = false;
    ForgotApiError = string.Empty;
    IsForgotSent = false;
    IsSignIn = false;
    IsForgotPassword = true;
}

[RelayCommand]
private void BackToSignIn()
{
    IsForgotPassword = false;
    IsForgotSent = false;
    IsSignIn = true;
}

[RelayCommand(CanExecute = nameof(CanSendPasswordReset))]
private async Task SendPasswordResetAsync()
{
    ForgotApiError = string.Empty;
    if (!EmailRegex.IsMatch(ForgotEmail))
    {
        ForgotEmailError = true;
        return;
    }
    ForgotEmailError = false;
    IsForgotSubmitting = true;
    try
    {
        var response = await _authApi.ForgottenPasswordAsync(ForgotEmail);
        if (response.Success) { IsForgotSent = true; return; }

        // Map API error codes → localized strings.
        var msg = MapForgotError(response);
        ForgotApiError = msg;
    }
    finally
    {
        IsForgotSubmitting = false;
    }
}

private bool CanSendPasswordReset() =>
    !IsForgotSubmitting && !string.IsNullOrEmpty(ForgotEmail);
```

`MapForgotError` reads `response.ErrorCode` (or the first entry of
`response.Errors`, matching how sign-in and sign-up already unpack
`ApiResponse` — see the existing error-mapping in `SignInAsync`).
**Updated 2026-08-27** (`docs/plans/improve-api-error-surfacing/`): the shipped
`ForgottenPasswordErrors.Resolve` no longer reads entry 0 only — it scans every
`errors[]` entry for the first code it recognizes, so a response like
`[{UNKNOWN_CODE}, {EMAIL_NOT_FOUND}]` shows the "email not found" message instead of the
generic one. The first-entry read remains the fallback when nothing is recognized. Recognized
codes per the v1.6 doc:

| API error_code            | Resource key                          | Behaviour               |
|---------------------------|---------------------------------------|-------------------------|
| `EMAIL_REQUIRED`          | `WelcomeView_Error_Email`             | mark field error        |
| `INVALID_EMAIL`           | `WelcomeView_Error_Email`             | mark field error        |
| `EMAIL_NOT_FOUND`         | `WelcomeView_Forgot_Error_NotFound`   | inline API error text   |
| `MAIL_ERROR`              | `WelcomeView_Forgot_Error_Mail`       | inline API error text   |
| *unknown / network fail*  | `WelcomeView_Forgot_Error_Generic`    | inline API error text   |

### Panel layout (WelcomeView.axaml)

Mirrors `SignInPanel` visually:

```
<StackPanel x:Name="ForgotPasswordPanel" IsVisible="{Binding IsForgotPassword}" VerticalAlignment="Top">

    <!-- INPUT STATE -->
    <StackPanel IsVisible="{Binding !IsForgotSent}">
        <TextBlock Text="{DynamicResource WelcomeView_Forgot_Title}" ... />
        <TextBlock Text="{DynamicResource WelcomeView_Forgot_Description}" ... />

        <Border x:Name="ForgotEmailBorder" ... >    <!-- same look as SignInEmailBorder -->
            <TextBox Text="{Binding ForgotEmail}"
                     PlaceholderText="{DynamicResource WelcomeView_Placeholder_Email}"
                     ... />
        </Border>

        <TextBlock Text="{DynamicResource WelcomeView_Error_Email}"
                   IsVisible="{Binding ForgotEmailError}"
                   Foreground="#FF383C" ... />

        <TextBlock Text="{Binding ForgotApiError}"
                   IsVisible="{Binding ForgotApiError, Converter={x:Static converters:StringConverters.IsNotNullOrEmpty}}"
                   Foreground="#FF383C" ... />

        <Button Content="{DynamicResource WelcomeView_Forgot_Submit}"
                Classes="primaryButton"
                Command="{Binding SendPasswordResetCommand}" />

        <Button Content="{DynamicResource WelcomeView_Forgot_BackToSignIn}"
                Classes="linkButton"
                Command="{Binding BackToSignInCommand}" />
    </StackPanel>

    <!-- CONFIRMATION STATE -->
    <StackPanel IsVisible="{Binding IsForgotSent}">
        <TextBlock Text="{DynamicResource WelcomeView_Forgot_Sent_Title}" ... />
        <TextBlock Text="{DynamicResource WelcomeView_Forgot_Sent_Description}" ... />
        <Button Content="{DynamicResource WelcomeView_Forgot_BackToSignIn}"
                Classes="primaryButton"
                Command="{Binding BackToSignInCommand}" />
    </StackPanel>
</StackPanel>
```

`SignInPanel` gains `IsVisible="{Binding IsSignIn}"` explicitly (currently
implied). `SignUpPanel` gains `IsVisible="{Binding !IsSignIn}"` alongside
`!IsForgotPassword` so it stays hidden during the forgot flow.

### Confirmation copy

- Title: `WelcomeView_Forgot_Sent_Title` — "Check your email"
- Body : `WelcomeView_Forgot_Sent_Description` — "We've sent a password
  reset link to your email address. Open your inbox and follow the link to
  set a new password. The link expires in 15 minutes." (15-minute number
  comes straight from the API doc, section 14.)

### Localization keys to add

| Key                                       | en                                                       | uk (Ukrainian)                                                              |
|-------------------------------------------|----------------------------------------------------------|-----------------------------------------------------------------------------|
| `WelcomeView_Forgot_Title`                | RESET PASSWORD                                           | СКИДАННЯ ПАРОЛЯ                                                             |
| `WelcomeView_Forgot_Description`          | Enter the email you used to sign up. We'll send you a link to reset your password. | Введіть електронну адресу, з якою ви реєструвались. Ми надішлемо посилання для скидання пароля. |
| `WelcomeView_Forgot_Submit`               | SEND RESET LINK                                          | НАДІСЛАТИ ПОСИЛАННЯ                                                         |
| `WelcomeView_Forgot_BackToSignIn`         | BACK TO SIGN IN                                          | ПОВЕРНУТИСЬ ДО ВХОДУ                                                        |
| `WelcomeView_Forgot_Error_NotFound`       | We couldn't find an account with that email.             | Обліковий запис із такою електронною адресою не знайдено.                   |
| `WelcomeView_Forgot_Error_Mail`           | Couldn't send the email. Please try again.               | Не вдалося надіслати листа. Спробуйте ще раз.                               |
| `WelcomeView_Forgot_Error_Generic`        | Something went wrong. Please try again.                  | Щось пішло не так. Спробуйте ще раз.                                        |
| `WelcomeView_Forgot_Sent_Title`           | Check your email                                         | Перевірте пошту                                                             |
| `WelcomeView_Forgot_Sent_Description`     | We've sent a password reset link to your email address. Open your inbox and follow the link to set a new password. The link expires in 15 minutes. | Ми надіслали посилання для скидання пароля на вашу пошту. Відкрийте пошту й перейдіть за посиланням, щоб задати новий пароль. Посилання діє 15 хвилин. |

Localization must go through resource files, no runtime string
transformation (per CLAUDE.md).

## File-level change list

### `src/ProoveGaming.App/ViewModels/WelcomeViewModel.cs`

- Add the observable state model above (`IsForgotPassword`, `IsForgotSent`,
  `ForgotEmail`, `IsForgotSubmitting`, `ForgotEmailError`,
  `ForgotApiError`).
- **Replace** the existing `ForgotPassword()` `[RelayCommand]` (which does
  `Process.Start(url)`) with `EnterForgotPassword()`.
- Add `[RelayCommand] BackToSignIn()`.
- Add `[RelayCommand(CanExecute = nameof(CanSendPasswordReset))]
  SendPasswordResetAsync()`.
- Add `MapForgotError(ApiResponse)` private helper — mirrors the existing
  code in `SignInAsync` that translates error codes into
  localized-resource lookups.
- Remove the `using System.Diagnostics` import if no other code needs it
  (currently the only user was `ForgotPassword()`).

### `src/ProoveGaming.App/Views/WelcomeView.axaml`

- Add `ForgotPasswordPanel` as described above.
- Make `SignInPanel` / `SignUpPanel` visibility explicitly gated by
  `IsSignIn` **and** `!IsForgotPassword` so the forgot panel replaces both.
- No changes to `ForgotPasswordButton` markup — its `Command` binding
  still resolves to `ForgotPasswordCommand`, which now points at
  `EnterForgotPassword()`. (Confirmed: `[RelayCommand] private void
  ForgotPassword()` generates `ForgotPasswordCommand`; renaming the
  method to `EnterForgotPassword` renames the generated command. Update
  the button binding to `EnterForgotPasswordCommand`.)

### `src/ProoveGaming.App/Resources/Strings.en.axaml` + `Strings.uk.axaml`

- Add the nine new keys listed in the Localization table above.
- No existing key changes.

### `docs/CONTEXT.md`

- Add glossary entries per project rule (every new domain term):
  - **Reset Code** — one-time server-generated token from
    `api/forgotten`, valid 15 minutes, consumed on the website's
    `account/reset_password` page.
  - **Password reset flow** — email entered in the launcher →
    `api/forgotten` → email link → website form → new password.
  - **`api/forgotten` / `api/reset_password`** — launcher/website
    endpoints (v1.6) that back the password-recovery flow.

### `docs/knowledge-base/proove-gaming-launcher-api-v1.6-en.md`

- Already saved during planning. No further changes.

### `tests/ProoveGaming.App.Tests/WelcomeViewModelForgotPasswordTests.cs` — **new**

- Cover the ViewModel branches (see Test strategy).

## Acceptance criteria

- **AC1** Given the launcher is on the sign-in tab, when the user clicks
  "Forgot password?", the app stays inside the launcher and shows the
  new email-input step (no browser is launched, no
  `System.Diagnostics.Process.Start` call fires).
- **AC2** Given the sign-in tab has `SignInEmail = "a@b.com"` when
  "Forgot password?" is clicked, `ForgotEmail` starts pre-filled with
  `a@b.com`.
- **AC3** Given the user enters `not-an-email` and clicks "Send reset
  link", the app **does not** call `api/forgotten`, and the inline
  "Invalid email" error appears under the email field.
- **AC4** Given the user submits a valid email and the API returns
  `success: true`, the panel switches to the confirmation state ("Check
  your email…") and the email is preserved in `ForgotEmail`.
- **AC5** Given the API returns `EMAIL_NOT_FOUND` (HTTP 404), the panel
  stays in the input state and shows the localized "We couldn't find an
  account…" message via `ForgotApiError`.
- **AC6** Given the API returns `MAIL_ERROR` (HTTP 500), the panel stays
  in the input state and shows the localized "Couldn't send the email…"
  message.
- **AC7** Given the API throws / times out (network failure), the panel
  shows the localized generic error and `IsForgotSubmitting` returns to
  `false` (button is re-enabled).
- **AC8** Given the confirmation state, when the user clicks "Back to
  sign in", the panel switches to the sign-in tab and `IsForgotSent`
  resets to `false` so a subsequent entry starts on the input state.
- **AC9** Both English and Ukrainian resource files contain the nine
  new keys with non-empty values, and switching language while the
  forgot-password panel is open updates all its labels live.
- **AC10** Building `dotnet build ./src/ProveGaming.slnx` succeeds with
  zero new warnings from `WelcomeViewModel.cs` / `WelcomeView.axaml`.

## Test strategy

Kind of change: **`add-*`** (new UI + ViewModel logic on top of an
already-tested API service). Automated coverage is non-negotiable for
the ViewModel error-mapping branches.

Framework: xUnit v2, mirroring existing `WelcomeViewModel`-adjacent
tests in `tests/ProoveGaming.App.Tests/`.

New file: `tests/ProoveGaming.App.Tests/WelcomeViewModelForgotPasswordTests.cs`.

Setup pattern: instantiate `WelcomeViewModel` with a fake `IAuthApiService`
(hand-rolled, mirroring the existing test doubles under
`tests/ProoveGaming.App.Tests/` — do not pull in Moq if it isn't already a
dependency), a real `UserProfile`, and an in-memory
`LocalStorageService("Data Source=:memory:")`. `ConfigurationManager`
gets a minimal stub-configured instance (base URL irrelevant to these
tests; the fake API captures calls directly).

Tests to add:

1. `EnterForgotPassword_FromSignInWithFilledEmail_PrefillsForgotEmail` (AC2).
2. `SendPasswordReset_EmptyEmail_ButtonDisabled_DoesNotCallApi` (AC3 negative).
3. `SendPasswordReset_InvalidEmailFormat_SetsFieldErrorAndSkipsApi` (AC3).
4. `SendPasswordReset_Success_FlipsToSentAndPreservesEmail` (AC4).
5. `SendPasswordReset_EmailNotFound_KeepsInputAndSetsApiError` (AC5).
6. `SendPasswordReset_MailError_KeepsInputAndSetsApiError` (AC6).
7. `SendPasswordReset_NetworkException_SetsGenericErrorAndReenablesButton`
   (AC7).
8. `BackToSignIn_FromConfirmationState_ResetsIsForgotSent` (AC8).
9. `IsForgotSubmitting_TogglesAroundApiCall_AllPaths` — verifies busy flag
   goes true → false whether the call succeeds, fails, or throws.

Deliberately **not** covered by automated tests:

- Avalonia layout of `ForgotPasswordPanel` visibility (three-panel
  mutual exclusion). Covered by the QA checklist below.
- Localization key resolution — asserted at build time by the resource
  system; a runtime test would just verify strings we already wrote.
- Real `HttpClient` round-trip against the staging backend — covered
  manually.

## QA (manual test steps)

1. **Launch the app.** Sign out if signed in so the Welcome screen is
   visible. Expected: the sign-in tab is shown with the "FORGOT PASSWORD"
   link under the password field.
2. **Type an email** into the sign-in email field (e.g. `x@y.com`), then
   click "FORGOT PASSWORD". Expected: the sign-in form is replaced by
   a "RESET PASSWORD" screen with the email pre-filled, a description,
   a "SEND RESET LINK" button and a "BACK TO SIGN IN" link. No browser
   window opens.
3. **Clear the email field, type an invalid string** like `abc` and
   click "SEND RESET LINK". Expected: no network call; a red inline
   error "Invalid email" appears under the field.
4. **Type a real registered email** and click "SEND RESET LINK".
   Expected: the button shows a busy state briefly, then the screen
   switches to a "Check your email" confirmation with the "The link
   expires in 15 minutes" note.
5. **Open your email in a browser**, click the reset link, complete the
   password change on the website, close the browser.
6. **In the app, click "BACK TO SIGN IN"** on the confirmation screen.
   Expected: the sign-in form reappears; the email field still holds
   what you typed before entering the forgot flow.
7. **Sign in with the new password.** Expected: sign-in succeeds; old
   sessions were invalidated by the reset (per the API doc), so any
   other launcher install must sign in again.
8. **Repeat step 4 with a non-registered email** (e.g.
   `no-such-user@example.com`). Expected: the screen stays on the input
   state and shows "We couldn't find an account with that email."
9. **Repeat step 4 while offline** (disable Wi-Fi). Expected: the
   screen shows "Something went wrong. Please try again.", the button
   is re-enabled.
10. **Switch app language to Ukrainian** while the forgot-password
    screen is open (Profile → Language). Expected: all labels
    (`Reset password`, `Send reset link`, error messages) switch to
    Ukrainian immediately.

## Review focus

- `null-safety` — `response.ErrorCode` / `response.Errors[0]` unpacking
  on the API mapper.
- `async/await` — no `.Result` / `.Wait()` on the network call; busy
  flag consistent across throws; button `CanExecute` reacts.
- `UI-thread affinity` — the `_authApi` call already returns on the
  captured context; verify no `ConfigureAwait(false)` slips into
  ViewModel code (the codebase intentionally omits it for VMs so
  bindings stay on the UI thread).
- `localization` — resource keys pinned in a single place, no magic
  strings in code.

## Performance & quality

None. This is a one-shot user action (email + button click), not a hot
path. No allocation-sensitive code, no shared state, no threading
concerns beyond the single async command.

## Risks

- **Enumeration attack surface.** `EMAIL_NOT_FOUND` (HTTP 404) tells
  the client whether an email is registered. That is the API's choice,
  not ours — we surface exactly what the doc specifies. If the backend
  later changes to a "we always report success" model, our client-side
  wording will need to soften. Note in the plan but do not preempt.
- **Localization drift.** The user might switch language while on the
  confirmation screen; the copy uses `DynamicResource`, so we rely on
  Avalonia's dynamic-resource propagation (already used across the
  Welcome screen). Verified visually per QA step 10.
- **Stale email pre-fill.** Users who sign in with account A, log out,
  then click "Forgot password" on the same session will see A's email
  pre-filled. Intended UX — matches sign-in remember-me behaviour.

## Open questions

- None. The API doc pins all error codes; UX is a direct port of the
  existing sign-in styling; no backend coordination needed beyond
  what the v1.6 doc already specifies.

## Review findings

<!-- filled in by /reviewer -->
