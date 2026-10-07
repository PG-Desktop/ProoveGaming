# Add the Contact Us dialog

> Written 2026-08-20 from the QA-provided SVG mockup (620×476 dialog, "CONTACT US").
> Clicking **Contact Us** in the profile menu or the guest menu must open this dialog;
> before this change both menu items did nothing.

## Behaviour (the contract)

- **Entry points:** the "Contact Us" item in the Home header's profile dropdown and in the
  guest dropdown. Clicking either closes its dropdown and opens the dialog over the
  window-level `DialogOverlay` (blurred background + tint), like every other Home dialog.
- **Dialog** (620×524, same bevelled-frame family as the logout/delete dialogs, close ✕ in
  the top-right frame column — sized per the filled-state mockup of 2026-08-20):
  - Title **"Contact us"** — sentence case, NOT all-caps: Pixel Font7's lowercase glyphs
    are distinct shapes, and the mockup's title is a capital C followed by lowercase
    (28px / weight 250 / line-height 100%, same treatment as the other dialog titles).
  - **Email** input (55px, mail icon). Pre-filled from the signed-in profile
    (`UserProfile.Email`) when the field is empty on open; always editable (guests type
    their own). White text.
  - **Topic** input (55px, bookmark icon), max **100** chars, live `n/100` counter below,
    right-aligned.
  - **Message** input (127px, chat icon, multiline), max **2000** chars, live `n/2000`
    counter below, right-aligned. Overflowing text scrolls with the app's design scrollbar:
    the shared `pgScrollViewer` styles are class-only selectors, so the class on the TextBox
    reaches the ScrollBar inside its template's ScrollViewer.
  - **Border states** (`pgInputHost`): idle empty = 30 % grey; focused or hovered = accent;
    and per the filled-state mockup a field that HOLDS content keeps the accent border even
    unfocused — the shared `pgInputHost.filled` class, toggled from the `ContactUs*Filled`
    bindings.
  - **CANCEL** (`secondaryButton`, min 104×39) closes without sending. Every open starts a
    fresh request: topic and message are cleared on open (QA 2026-08-21); only the email
    keeps its value (or pre-fills from the profile).
    **SEND** (`primaryButton`, min 92×40) is disabled until the form is valid: email
    matches the app's email regex, topic is 3–100 characters and message is 10–2000
    (`ContactUsFormValid` — static, test-driven). The minimums were "non-blank" until
    2026-08-27, when `docs/plans/improve-api-error-surfacing/` aligned them with the v2.0
    §20 contract so `TOPIC_TOO_SHORT` / `MESSAGE_TOO_SHORT` stop being reachable. The disabled visual is the existing
    `primaryButton:disabled` style (10 % accent fill, 30 % text), which matches the mockup.
  - ✕ behaves like CANCEL.
- **SEND (interim, no backend yet — QA 2026-08-21):** no API call and no error surface.
  SEND dims and blurs the form under a 70 % black scrim and plays the mockup's **4-frame
  pixel loader** (72×72, 24 px squares, #6357FF full/50 %) at 250 ms per frame for **2
  seconds**, then the dialog closes, topic and message clear, and two confirmations are
  raised: the transient green-check toast "Your message has been accepted"
  (`NotificationService` → MainWindow's existing chamfered 432 px `NotificationGrid`, the
  exact bar the mockup shows, 5 s auto-hide + ✕) and a bell entry ("Message accepted" with
  the topic) that keeps it in the history. The wording deliberately claims *acceptance*,
  not delivery (PR #118 M1, 2026-08-21): nothing leaves the machine until the backend route
  exists, so "sent successfully" returns only together with the real API call. The whole
  dialog is inert during the loader: the
  scrim swallows clicks on the form and buttons, and the ✕ (still reachable in the frame
  column) no-ops until the send finishes — a mid-"send" close would still end in the
  success toast (review R1 S2).
  - `IAuthApiService.ContactUsAsync` (POST `api/contact_us`, bearer token when a session
    exists) is kept as the ready client contract; wire it into `SendContactUsAsync` and
    restore the in-dialog error surface once the backend route exists. The
    `ContactUs_SendFailed` resource is already in place for that. **Superseded 2026-08-27**
    (`docs/plans/improve-api-error-surfacing/`): the failure line is no longer a single
    string — `ContactUsErrors.ResolveKey` picks per-code wording, with distinct messages for
    the rate-limit, daily-limit and duplicate refusals; `ContactUs_SendFailed` remains the
    generic fallback.

> **Superseded (2026-08-22):** the interim no-API send above is replaced by the real
> `api/contact_us` call — see
> [contact-us-and-update-check](../contact-us-and-update-check/contact-us-and-update-check-plan.md).
> The dialog now always posts: a 2xx success envelope closes with new "sent" wording
> (`ContactUs_Delivered*`); a missing route (404, or a 2xx non-envelope body) degrades to
> exactly the accepted behaviour described above; a transient failure keeps the dialog open
> with `ContactUs_SendFailed` and the draft intact. The loader runs for the real request
> duration (minimum one revolution, bounded by a 15 s timeout).

> **Superseded again (2026-08-25):** API v2.0 renamed the route to `api/contact` and the
> degraded "accepted" mode is retired — a 404 or malformed 2xx now shows the failure line,
> and the `ContactUs_Sent*` "accepted" strings are gone from both resource files. See
> [add-api-v2-sync](../add-api-v2-sync/add-api-v2-sync-plan.md).

## File-level change list

- `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` — `IsContactUsOpen`, the three field
  properties + counters, `ContactUsFormValid` (internal static, like `DeriveHidTransports`),
  `CanSendContactUs`, open/cancel/send commands, error + sending state.
- `src/ProoveGaming.App/Services/IAuthApiService.cs`, `AuthApiService.cs` —
  `ContactUsAsync(email, topic, message, token?)` → POST `api/contact_us`.
- `src/ProoveGaming.App/Views/MainWindow.axaml` — `ContactUsDialogPanel` (frame canvas
  transcribed from the SVG per the SVG→Canvas rule; content grid with the three inputs,
  counters, buttons, close ✕).
- `src/ProoveGaming.App/Views/MainWindow.axaml.cs` — overlay wiring
  (`IsContactUsOpen` in the HomeViewModel property filter, `RefreshDialogOverlay`, panel
  getter).
- `src/ProoveGaming.App/Views/HomeView.axaml` / `.axaml.cs` — Click handlers on both menu
  items (close dropdown, open dialog).
- `src/ProoveGaming.App/Resources/Strings.en.axaml`, `Strings.uk.axaml` — title, field
  placeholders, SEND, sent/failed notifications.
- `tests/ProoveGaming.App.Tests/HomeViewModelContactUsTests.cs` — drives
  `ContactUsFormValid` (valid case, bad email, blank/whitespace topic or message,
  over-limit topic/message).

## Acceptance criteria

- [ ] Profile menu → Contact Us → dialog opens (dropdown closed, background blurred);
  same from the guest menu.
- [ ] Signed-in user sees their email pre-filled; a guest sees the placeholder. This holds
  across owner changes in **both** directions: after sign-out AND after a guest signs in,
  the previous owner's draft email is dropped (`ResetContactUsOwnerState`, review R1 S3 +
  PR #118 m2), so the dialog never opens with another owner's address.
- [ ] SEND stays disabled until email is valid, topic is at least 3 characters and message
  at least 10; counters track typing and the inputs hard-stop at 100 / 2000.
- [ ] CANCEL and ✕ close without sending; reopening shows empty topic and message (the
  email keeps its value).
- [ ] SEND dims the form and cycles the 4-frame pixel loader for 2 s, then the dialog
  closes and the bell notification appears; the form is not clickable during the loader.
- [ ] uk strings fit: no fixed widths on buttons or labels (`MinWidth` + padding only).

## QA steps

1. Open from both menus; check title, icons, counters, disabled SEND.
2. Type an invalid email → SEND stays disabled; fix it and fill topic/message → enabled.
3. Paste >100 chars into Topic and >2000 into Message → input truncates, counters cap.
4. SEND → the form dims/blurs, the pixel loader cycles ~2 s, the dialog closes, the bell
   shows "Message accepted" with the topic; reopen → topic and message are empty again,
   email still filled.
5. Switch language to uk and re-check the dialog layout.
6. (PR #118 m1) Send with a ~100-char topic → in the bell panel the subtitle ends with an
   ellipsis inside the card; nothing runs under the hover delete button.
7. (PR #118 m2) Continue as guest → open Contact Us → type any email → CANCEL → sign in →
   open Contact Us → the field shows the account's email, not the guest draft.

## Review findings

### Round 1 (working tree, uncommitted) — Ship: yes

#### Summary
The whole Contact Us feature: dialog (620×524 bevelled frame, three inputs with counters
and filled-state accent borders), entry points in both Home dropdowns, form validation
gating SEND, the interim no-API send (2 s four-frame pixel loader over a scrim, then
close + green-check toast + bell entry), and the design scrollbar on the Message field.
Committed delta vs main is empty — everything under review is working-tree + the two
untracked files.

#### Plan conformance
Plan: this plan — kept live through every pivot (476→524 height, caps→sentence-case
title, draft-survives→clear-on-open, API send→interim loader), so code and contract match.
- Implemented as planned: all File-level change list items; all behaviour bullets.
- Deviates from plan: none found.
- Accepted limitation (per plan): no backend — SEND fakes success. Residual risk: users
  can "send" messages that go nowhere until `api/contact_us` lands; the plan owns the
  re-wiring note.

#### Code
- [x] **S1. The persistent-scrollbar fix may not take effect — TemplateBinding outranks
  the style** —
  *(fixed 2026-08-21: `ScrollViewer.AllowAutoHide="False"` set as an attached property on
  the Message TextBox — forwarded by the template like the Visibility one; the style block
  stays as fallback with its comment corrected. Needs the one visual check on overflow.)*
  `src/ProoveGaming.App/App.axaml:111-114` /
  `src/ProoveGaming.App/Views/MainWindow.axaml:762` — Should-fix (do first)
  **Problem / why it matters:** the Fluent TextBox template forwards the attached
  `ScrollViewer.*` properties onto `PART_ScrollViewer` via `TemplateBinding`, and
  template-set values carry Template priority, which beats a Style-priority setter
  targeting the same template child. If that holds in Avalonia 12.1 (it does in 11.x),
  the style's `AllowAutoHide=False` is silently overridden by the forwarded default
  (true) and the bar still auto-hides at idle — the exact "скролу не видно" complaint
  this change set out to fix. The bar's look (4 px width, thumb colours) is safe: those
  setters target the ScrollBar and are not template-bound.
  **Fix:** set the attached property at the call site —
  `ScrollViewer.AllowAutoHide="False"` on the Message TextBox, exactly like the
  `ScrollViewer.VerticalScrollBarVisibility="Auto"` line above it (the template forwards
  both). Keep or drop the style block; if kept, fix its comment. **Verify in the running
  app**: overflow the field, move the pointer away — the 4 px bar must stay.
- [x] **S2. ✕ during the 2 s loader closes the dialog but the send still "succeeds"** —
  *(fixed 2026-08-21 with the first option: `CancelContactUsCommand` no-ops while
  `_contactUsSending`; plan contract updated — the dialog is fully inert during the
  loader.)*
  `src/ProoveGaming.App/ViewModels/HomeViewModel.cs:529` (`CancelContactUsCommand`) +
  `:257` (`SendContactUsAsync`) — Should-fix
  **Problem / why it matters:** the scrim deliberately leaves the frame-column ✕
  clickable. Clicking it mid-loader closes the dialog, but the loop finishes anyway:
  drafts clear, the success toast and the bell entry fire — a close the user explicitly
  requested is reported as a successful send. Harmless while the send is fake; wrong the
  day the API call returns here.
  **Fix (pick one):** gate the cancel — `CancelContactUsCommand` no-ops while
  `_contactUsSending` (matches the acceptance criterion "the form is not clickable during
  the loader"); or check `if (!IsContactUsOpen) return;` after the loop before the
  clear/toast. The first is simpler and keeps ✕ semantics consistent.
- [x] **S3. The previous owner's email survives into the next owner's dialog** —
  *(fixed 2026-08-21: `ClearOwnerScopedState` resets `ContactUsEmail` and
  `ContactUsError` alongside the other owner-scoped state.)*
  `src/ProoveGaming.App/ViewModels/HomeViewModel.cs:147` (`_contactUsEmail`) + `:247`
  (`OpenContactUs` pre-fills only when empty) + `:625` (`ClearOwnerScopedState` does not
  reset it) — Should-fix
  **Problem / why it matters:** HomeViewModel is a singleton. User A signs in, opens
  Contact Us (email pre-fills), signs out; a guest (or user B) on the same machine opens
  Contact Us → user A's address is still in the field, because the pre-fill guard sees a
  non-empty value. A small privacy leak and a wrong default sender.
  **Fix:** reset `ContactUsEmail` (and `ContactUsError`) in `ClearOwnerScopedState`,
  alongside the other owner-scoped state that method already drops.

#### Tests
- `HomeViewModelContactUsTests` covers the SEND gate rule (valid, malformed email,
  blank, over-limit) — good. The instance paths (open-clears-draft, loader timing, S3's
  owner reset) need the full service graph and stay manual per the repo's accepted
  pattern; the QA steps above cover them.

#### Nits
- After S1 lands, reconcile the two comments that currently say the class "carries the
  setting" — `App.axaml:51-56` and `App.axaml:107-110`.
- The sending scrim shields only the body column; the 42 px frame columns stay live by
  design (the ✕). Noting so it reads as a decision, not an accident.
- `OnContactUsFieldChanged` raises both counters and all three `*Filled` flags on any
  field change — harmless churn, three of five are no-ops per keystroke.

#### What's done well
- The plan was reconciled at every pivot, so its contract genuinely matches the code —
  including the interim-send note that tells the next person exactly where to wire the
  real API call.
- Reuse over new chrome: the sent toast is the existing `NotificationGrid` (pixel-perfect
  match to the mockup), the input borders extend the shared `pgInputHost` with an opt-in
  `filled` class, and the loader frames are transcribed SVG→Canvas per the repo rule.
- `ContactUsFormValid` follows the established static-rule-plus-tests pattern
  (`DeriveHidTransports`, `LowBatteryDecision`), and the send latch avoids the
  `AsyncRelayCommand.IsRunning` finally-block trap the low-battery work documented.

#### Verdict
Ship: yes — no blockers; S1 needs a one-line fix and a live check before this merges,
since it is the very defect QA reported. First: (1) S1 attached property + visual check,
(2) S2 cancel gate, (3) S3 owner reset.

### Round 2 (external — PR #118 review by boikoserhii84, 2026-08-21)

The PR review confirmed four findings (full text lives in the PR comment; handles kept):

- [x] **M1. SEND claims delivery while no request is made** — resolved per the product
  decision (option A1, honest wording): `ContactUs_SentToast` → "Your message has been
  accepted" / «Ваше повідомлення прийнято», `ContactUs_SentTitle` → "Message accepted" /
  «Повідомлення прийнято»; behaviour otherwise unchanged (loader, close, bell entry). The
  interim comment in `SendContactUsAsync` now also records that the fixed `Task.Delay`
  must become the awaited real call with a `CancellationToken` when `api/contact_us` lands,
  and that "sent" wording returns only then. Contract section updated above.
- [x] **m1. Long topic overflowed the bell card** — `TextTrimming="CharacterEllipsis"`
  added to both the Title and Subtitle `TextBlock`s of the notification card
  (`MainWindow.axaml`), fixing every current and future notification; DB keeps the full
  topic (no pre-truncation — reviewer option A, option B not needed).
- [x] **m2. Guest-typed email survived sign-in** — the R1 S3 reset is now a shared
  `ResetContactUsOwnerState()` called from **both** owner transitions:
  `ClearOwnerScopedState` (sign-off) and `ReloadOwnerScopedState` (guest → signed-in).
  The open-time guard itself is now the static `ContactUsEmailOnOpen(currentDraft,
  profileEmail)` (reviewer option B), used by `OpenContactUs`.
- [x] **m3. No regression tests at the instance seam** — closed with reviewer option A
  (static rules in the file's established pattern): `ContactUsEmailOnOpen` and
  `ContactUsCanCancel(sending)` are now the code paths `OpenContactUs` /
  `CancelContactUsCommand` actually run, and `HomeViewModelContactUsTests` pins both
  (prefill-only-when-empty, draft kept, no-profile stays empty; cancel blocked while
  sending). Residual, accepted: the wiring itself (`ResetContactUsOwnerState` call sites,
  the R1 `LanguageChanged` subscription in the Settings plan) remains uncovered — option B
  (ctor seam for `HomeViewModel`) stays the ticket if that class of regression recurs.
