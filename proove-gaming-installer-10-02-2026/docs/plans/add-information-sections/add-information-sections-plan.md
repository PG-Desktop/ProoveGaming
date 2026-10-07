# Plan: add-information-sections

Phase 2 of the Information page. Phase 1 (`../add-information-page/add-information-page-plan.md`)
shipped the empty page shell; this plan fills it with content.

## Goal

Fill the Information page with its real content: three legal documents — **Privacy
Policy**, **Terms of Service**, and **License and Sublicense Agreement** — selectable via
three tabs on the page's left octagon panel (which Phase 1 deliberately shipped empty),
in that order. Each document exists in English and Ukrainian (six source docx files from
the client, extracted to [samples/](samples/)); the displayed text must follow the app
language and switch immediately on a runtime language change, like every other localized
surface.

## Scope

**In:**

- Three tabs in the Information page's left octagon panel (extending it from the empty
  background to a 3-item version of ProfileView's ACCOUNT/SECURITY tab pattern), order:
  1. Privacy Policy, 2. Terms of Service, 3. License Agreement.
- Six content views (3 documents × en/uk) transcribed from the extracted texts in
  [samples/](samples/), rendered in a scrollable panel in the page's central area.
- Language switching: tab labels via `DynamicResource` keys (both resource files), the
  document body swapped on `LocalizationManager.LanguageChanged`.
- Shared text styles for the document body in `App.axaml`.
- Tests per the strategy below; updates to the Phase 1 plan and `docs/CONTEXT.md`.

**In (added 2026-09-03, reversing the original Out — see Follow-up section):**

- Replacing the **registration** agreement placeholder: `AgreementTermsView` now hosts
  the real per-language documents via the shared `LegalDocumentContent` factory, opened
  from three separate sign-up links (`WelcomeView`).
- Replacing the **Installer's** agreement content: `AgreementTermsEnView`/`UkView`
  regenerated from `samples/privacy-policy.{en,uk}.txt` (step header title unchanged).

**Out:**

- Moving the Installer's `agreement*` text styles into `ProoveGaming.Common` — see
  Options; deferred (the follow-up added one installer-local style,
  `agreementDocTitleText`, instead).
- New NuGet packages, `.slnx` edits, any Uninstaller change.

## Source material

The six client docx files were extracted to plain text (one line per paragraph) and are
versioned in [samples/](samples/) as the transcription source of truth:

| Tab | English | Ukrainian |
|---|---|---|
| 1. Privacy Policy | `privacy-policy.en.txt` | `privacy-policy.uk.txt` |
| 2. Terms of Service | `terms-of-service.en.txt` | `terms-of-service.uk.txt` |
| 3. License Agreement | `license-agreement.en.txt` | `license-agreement.uk.txt` |

Structure is uniform: line 1 = document title, line 2 = "Proove Gaming Application —
Effective Date: 28.08.2026", then paragraphs, numbered headings (`1. …`), and `●` bullet
lines. Formatting carried from the docx (2026-09-03): the standalone company-registration line is *italic* in the Privacy Policy and License Agreement documents (both languages; Terms of Service has no italics) - the generated views set FontStyle="Italic" on that run. **Known source quirk:** the en/uk Privacy Policy numbering jumps from section 11
to 13 (no 12) — transcribe as-is; fidelity to the legal source beats cosmetic renumbering
(flagged to the user; if legal supplies a corrected file, re-extract).

Decisions made with the user (2026-09-03): tab 2 is labeled **Terms of Service** (matches
the document title, not the "Terms and Conditions" wording from the original request);
tab 3 uses the short label **License Agreement** (the full "License and Sublicense
Agreement" title remains as the document's own first line).

## Options considered

### Option A — per-language XAML content views (Installer precedent)

Six `UserControl`s under `Views/Information/` (`PrivacyPolicyEnView`, `PrivacyPolicyUkView`,
`TermsOfServiceEnView`, `TermsOfServiceUkView`, `LicenseAgreementEnView`,
`LicenseAgreementUkView`), each a single `TextBlock` of classed `Run`s — exactly the
Installer's `AgreementTermsEnView`/`AgreementTermsUkView` pattern, swapped into a
`ContentControl` by (selected tab, current language).

- **Pros:** the repo's existing pattern for exactly this content type; formatting is
  explicit per line (no heuristics near legal text); malformed markup fails the build;
  reviewable by diffing view text against `samples/`.
- **Cons:** ~1,000 lines of mechanical transcription XAML; a future legal-text update
  means editing XAML rather than dropping in a text file.

### Option B — plain-text assets + runtime formatter

Ship the six `.txt` files as `avares://` assets; a small formatter turns lines into
styled runs (line 1 → title, `^\d+\.` → heading, `●` → bullet, else paragraph).

- **Pros:** content stays byte-identical to the extraction; updates are file drops.
- **Cons:** introduces parsing/heuristics that can silently mis-style a legal line (a
  paragraph starting with "10.5 mm…" would render as a heading); a new mini-framework for
  one page; no precedent in the repo.

**Recommended: Option A.** Legal text wants deterministic, reviewable formatting; the
installer already proved the pattern. The style classes are defined fresh in `App.axaml`
(`legalDocTitle` / `legalDocHeading` / `legalDocText` + root class) rather than moved out
of the Installer's `App.axaml`: consolidating the installer's `agreement*` styles into
`ProoveGaming.Common` is the "correct" long-term move but drags installer code (and its
release-process risk) into an App-only change — deferred deliberately and noted in Out.

### Sub-decision — selection state: code-behind vs view model

Phase 1 made the view code-behind-only (SettingsView pattern). The new state (selected
tab, language-driven content) is still view-local UI state with no service dependencies,
so it stays in `InformationView.axaml.cs` — mirroring the Installer's
`AgreementStepView.UpdateAgreementText()` — rather than introducing a DI-registered
view model for three booleans. If the page ever gains data-driven content, a VM comes
with that change.

## Recommended approach

1. **Tab panel (`InformationView.axaml`).** Extend the octagon to three tabs by
   replicating ProfileView's per-tab geometry (each tab: 8px cap strips at x28/w104 +
   48px body at x20/w120, `#336357FF`) with a third zone:
   - (2026-09-03: the panel was later widened 160 -> 200 so the uk labels keep side padding; the geometry below scales accordingly - labels 160 wide, caps 144, columns "200,4,42,668,42".) Octagon path grows from height 164 to **232**:
     `M20,0 L140,0 L160,20 L160,212 L140,232 L20,232 L0,144… ` →
     `M20,0 L140,0 L160,20 L160,212 L140,232 L20,232 L0,212 L0,20 Z`; canvas `160×232`.
   - Highlight zones: tab 1 y 16–80 (strips at 16 and 72, body 24–72), tab 2 y 84–148
     (84 / 92–140 / 140), tab 3 y 152–216 (152 / 160–208 / 208) — same 4px inter-tab gap
     and 16px outer insets as Profile.
   - Each tab's three highlight `Rectangle`s are wrapped in a named group (nested
     `Canvas` per tab: `Tab1Highlight`/`Tab2Highlight`/`Tab3Highlight`) so code-behind
     toggles one `IsVisible` per tab instead of nine rectangles.
   - Each tab pairs an **icon + label** (icon 20px above the text, 6px spacing — updated
     2026-09-03, initially shipped text-only until the designer's SVGs arrived): a
     `Border` per zone (`Canvas.Top` = zone top, height 64, width 120 at x 20) containing
     a centered `StackPanel` with the icon `Path` and a `TextBlock`, Segoe UI SemiBold
     12/16, white, `TextWrapping="Wrap"`, `TextAlignment="Center"` — the group-centering
     trick Profile uses for the two-line uk ACCOUNT label. Icons: Privacy Policy =
     designer's shield-info SVG, Terms of Service = designer's gavel SVG, License
     Agreement = a stand-in Material "description" (document) icon until the designer
     delivers the real one (swap the path data in place). Icon fills switch between
     Profile's active `#6357FF` and inactive `#80BEBCEA` brushes with the selection,
     driven from `UpdateDocument()`.
   - Three transparent overlay `Button`s (`Classes="linkButton" Cursor="Hand"`) at
     y 0–82, 82–150, 150–232 (zone boundaries at the midpoints of the 4px gaps), with
     `Click` handlers.
2. **Content area (`InformationView.axaml`).** In the frame grid's column 3 (over the
   `#CC000000` background rectangle): a `ScrollViewer` (`Classes="pgScrollViewer"`,
   `AllowAutoHide="False"`, `VerticalScrollBarVisibility="Visible"`) hosting
   `ContentControl x:Name="DocumentHost"`. (Amended 2026-09-03 to the design mock,
   superseding the original "vertical margin 24" wording: viewport margins are **48
   top / 24 bottom** — the bar starts on the first text line — the text softly fades
   out at the bottom via a gradient `OpacityMask` whose depth (7%) stays shallower
   than the content's 64px bottom padding so the last line reads at full opacity at
   full scroll; `DocumentHost` Margin="24,0,20,64".)
3. **Content views.** `Views/Information/<Doc><Lang>View.axaml` + trivial `.axaml.cs`
   (InitializeComponent only, like the Installer's). Body = one `TextBlock
   Classes="legalDocRoot"` of `Run`s. Transcription rules from `samples/*.txt`:
   line 1 → `legalDocTitle`; line 2 → `legalDocText` (the effective-date line);
   `^\d+\.` lines → `legalDocHeading`; `●` lines → `legalDocText` kept on their own line
   (single `LineBreak` between bullets); other lines → `legalDocText` paragraphs
   (`LineBreak`+`LineBreak` between paragraphs, single `LineBreak` after a heading).
   XML-escape `&`, `<`, `"`; the uk texts' `’`/`«»` characters are XML-safe as-is.
   (2026-09-03: email addresses inside a line are extracted into an `InlineUIContainer`
   holding a `Button Classes="legalDocEmailLink"` wired to `LegalEmailLink.OpenFrom`,
   and the standalone registration line carries `FontStyle="Italic"` per the docx —
   both pinned by tests, so a re-extraction that loses them fails the build.)
4. **Styles (`App.axaml`).** Updated 2026-09-03 to the designer's content mock (SVG):
   `TextBlock.legalDocRoot` (wrap, `BaseAppFont`, 16/24, body `#B3BEBCEA` — #BEBCEA at
   70%); `Run.legalDocTitle` (SemiBold 20, white); `Run.legalDocHeading` (SemiBold 18,
   white); `Run.legalDocDate` (regular 16, white — the effective-date line is white in
   the mock); `Run.legalDocText` (regular, inherits the dim body colour). Weight/size +
   colour together satisfy the "never colour-only" emphasis rule. Per the user's
   direction, the mock's other elements were deliberately **not** adopted: no Table of
   Contents block and no removed title — the text stays 1:1 with the .docx sources; the
   mock's bottom backdrop-blur fade was skipped (no faithful Avalonia equivalent over
   the translucent panel).
5. **Code-behind (`InformationView.axaml.cs`).** A `LegalDocument` field (PrivacyPolicy
   default; shared enum in `Models/` since the sign-up follow-up), `Click` handlers per
   tab, and `UpdateDocument()`: `(section, LocalizationManager.CurrentLanguageCode)` →
   content view (via the shared `LegalDocumentContent` factory) into
   `DocumentHost.Content` (no caching; a one-time layout of a ~16 KB text block on
   explicit tab click is not a hot path), toggle the three highlight groups, reset
   scroll offset to 0. Subscribe `LocalizationManager.LanguageChanged += UpdateDocument`
   in the constructor — static event on a singleton view, no unsubscribe, per the
   documented convention (`HomeViewModel` line ~799). Per-visit reset (tab 1, top) is a
   public `ResetToFirstSection()` called from `MainWindow.OnInformationRequested()` —
   **not** `OnLoaded`, which fires on visual-tree attachment rather than on `IsVisible`
   and therefore runs only once per window (corrected 2026-09-03, PR #131 review M1);
   the constructor calls it once so the host is never empty.
6. **Localization.** Keys `InformationView_Tab_PrivacyPolicy` ("PRIVACY POLICY" /
   «ПОЛІТИКА КОНФІДЕНЦІЙНОСТІ»), `InformationView_Tab_TermsOfService` ("TERMS OF
   SERVICE" / «УМОВИ НАДАННЯ ПОСЛУГ»), `InformationView_Tab_LicenseAgreement` ("LICENSE
   AGREEMENT" / «ЛІЦЕНЗІЙНА УГОДА») in **both** App resource files.
7. **Docs.** Phase 1 plan gets a dated note under its octagon-tabs decision (superseded:
   tabs now exist, via this plan); `docs/CONTEXT.md`'s **Information page** entry is
   updated to describe the three sections.

## File-level change list

| File | Action | Intent |
|---|---|---|
| `src/ProoveGaming.App/Views/InformationView.axaml` | edit | 3-tab octagon (232px canvas, named highlight groups, overlay buttons) + ScrollViewer/ContentControl in the central panel. |
| `src/ProoveGaming.App/Views/InformationView.axaml.cs` | edit | Section state, tab click handlers, `UpdateDocument()` (content swap + highlight + scroll reset), `LanguageChanged` subscription, `OnLoaded` reset. |
| `src/ProoveGaming.App/Views/Information/PrivacyPolicyEnView.axaml` (+`.axaml.cs`) | create | en Privacy Policy body transcribed from `samples/privacy-policy.en.txt`. |
| `src/ProoveGaming.App/Views/Information/PrivacyPolicyUkView.axaml` (+`.axaml.cs`) | create | uk Privacy Policy body from `samples/privacy-policy.uk.txt`. |
| `src/ProoveGaming.App/Views/Information/TermsOfServiceEnView.axaml` (+`.axaml.cs`) | create | en Terms of Service body from `samples/terms-of-service.en.txt`. |
| `src/ProoveGaming.App/Views/Information/TermsOfServiceUkView.axaml` (+`.axaml.cs`) | create | uk Terms of Service body from `samples/terms-of-service.uk.txt`. |
| `src/ProoveGaming.App/Views/Information/LicenseAgreementEnView.axaml` (+`.axaml.cs`) | create | en License and Sublicense Agreement body from `samples/license-agreement.en.txt`. |
| `src/ProoveGaming.App/Views/Information/LicenseAgreementUkView.axaml` (+`.axaml.cs`) | create | uk License and Sublicense Agreement body from `samples/license-agreement.uk.txt`. |
| `src/ProoveGaming.App/App.axaml` | edit | `legalDocRoot` / `legalDocTitle` / `legalDocHeading` / `legalDocText` styles (shared by the six content views). |
| `src/ProoveGaming.App/Resources/Strings.en.axaml` | edit | Three `InformationView_Tab_*` keys. |
| `src/ProoveGaming.App/Resources/Strings.uk.axaml` | edit | The same three keys in Ukrainian. |
| `tests/ProoveGaming.App.Tests/InformationViewResourcesTests.cs` | edit | Extend per the Test strategy (tab keys, content-view fidelity checks). |
| `tests/ProoveGaming.App.Tests/ProoveGaming.App.Tests.csproj` | edit | Copy the six content `.axaml` files (and the updated `InformationView.axaml`) into test output. |
| `docs/plans/add-information-page/add-information-page-plan.md` | edit | Dated note: octagon tabs + content landed via this plan. |
| `docs/CONTEXT.md` | edit | Update the **Information page** glossary entry (three legal sections, no longer empty). |

Added by the 2026-09-03 follow-ups (sign-up links, installer, PR #131 review fixes):

| File | Action | Intent |
|---|---|---|
| `src/ProoveGaming.App/Models/LegalDocument.cs` | create | Shared enum of the three documents. |
| `src/ProoveGaming.App/Views/Information/LegalDocumentContent.cs` | create | Factory: document + current language → content view; step-title resource key. |
| `src/ProoveGaming.App/Views/AgreementTermsView.axaml` (+`.axaml.cs`) | edit | Placeholder EULA removed; `ShowDocument(LegalDocument)` sets normal-case title (`AgreementTermsView_Title_*`) + hosts the content view. |
| `src/ProoveGaming.App/Views/WelcomeView.axaml` (+`.axaml.cs`) | edit | Three separate agreement links; parameterized `AgreementTermsRequested`. |
| `src/ProoveGaming.App/ViewModels/WelcomeViewModel.cs` | edit | `OpenPrivacyPolicy/OpenTermsOfService/OpenLicenseAgreement` commands. |
| `src/ProoveGaming.App/Views/MainWindow.axaml.cs` | edit | Pass the document to `ShowDocument`; call `InformationView.ResetToFirstSection()` per visit. |
| `src/ProoveGaming.Installer/Views/AgreementTermsEnView.axaml` / `UkView.axaml` | edit | Real Privacy Policy content (step header title unchanged). |
| `src/ProoveGaming.Installer/App.axaml` | edit | `agreementDocTitleText` style (20 SemiBold) so the doc title sits above the 18px headings, mirroring the App. |

Added by the Round 2 / PR #133 follow-ups (2026-09-03):

| File | Action | Intent |
|---|---|---|
| `src/ProoveGaming.Common/Controls/LegalEmailLink.cs` | create | Opens the mail client for the inline email link Buttons (shared by App and Installer). |
| `src/ProoveGaming.Common/Styles/SharedLegalDocumentStyles.axaml` | create | The `legalDocEmailLink` Button style (accent + underline + baseline box), included by both App.axaml files. |
| `src/ProoveGaming.Installer/Views/AgreementStepView.axaml` | edit | Fixed-width content host so the long document's scroll extent is measured correctly. |
| `tests/ProoveGaming.App.Tests/InformationViewResourcesTests.cs` | edit | `EmailRendersAsLink` + `RegistrationLineItalicMatchesTheDocx` guards. |

## Acceptance criteria

- [ ] Given the Information page is opened (from either the profile or the guest
      dropdown), the left octagon panel shows three tabs top-to-bottom: Privacy Policy,
      Terms of Service, License Agreement — and the Privacy Policy document is already
      displayed in the central panel with its tab highlighted.
- [ ] Given app language en, the tab labels read PRIVACY POLICY / TERMS OF SERVICE /
      LICENSE AGREEMENT; given uk, ПОЛІТИКА КОНФІДЕНЦІЙНОСТІ / УМОВИ НАДАННЯ ПОСЛУГ /
      ЛІЦЕНЗІЙНА УГОДА, with no label clipped at the 120px tab width (labels wrap).
- [ ] When a tab is clicked, its highlight moves to that tab, the central panel shows the
      corresponding document from the top (scroll reset), and the document's own title
      line matches the section (e.g. "End User License and Sublicense Agreement" /
      «Ліцензійна та субліцензійна угода кінцевого користувача» for tab 3).
- [ ] Each tab shows its icon above the label; the selected tab's icon is violet
      (#6357FF) and the other two are dimmed (#80BEBCEA), following the selection as
      tabs are clicked (added 2026-09-03).
- [ ] When the app language is switched in Settings and the user returns to the
      Information page (or is on it), the displayed document body is in the new language
      and the tab labels follow — no restart, no stale mixed-language state.
- [ ] Each document is scrollable with the persistent 4px design scrollbar; scrolling to
      the bottom shows the document's final section (16. Contact Us / 15. Contact for the
      respective docs) — i.e. no truncated transcription.
- [ ] The rendered en Privacy Policy keeps the source's section numbering including the
      11→13 jump (fidelity to the client document).
- [ ] Back button behavior from Phase 1 is unchanged (returns to Home).
- [ ] `LanguageResourceParityTests` and the extended `InformationViewResourcesTests` pass.
- [ ] (2026-09-03) On the sign-up form, each of the three agreement links opens its own
      document with a normal-case step title matching that document; Back returns to the
      sign-up form.
- [ ] (2026-09-03) The installer's agreement step shows the real Privacy Policy in the
      selected language (en/uk), ending with the contact section; the step header still
      reads "Customer Privacy Policy" and the scroll-to-bottom gate still enables Next.
- [ ] (2026-09-03) Leaving the Information page and reopening it lands on the Privacy
      Policy tab, scrolled to the top, regardless of the tab/scroll left behind.
- [ ] (2026-09-03) Every support@proovegaming.com occurrence in the documents renders as
      an accent underlined link; primary click / Enter / Space opens the mail client,
      right and middle click do nothing — in all three documents, both locales, on the
      Information page, the sign-up agreement step and the installer agreement step.
      The document's final line reads at full opacity at full scroll at any window height.

## Test strategy

Kind: **new feature (`add-*`)** — mandatory coverage, extending
`tests/ProoveGaming.App.Tests/InformationViewResourcesTests.cs` (file-content assertion
style; no Avalonia harness exists):

- **Tab label keys** — theory over both resource files × three `InformationView_Tab_*`
  keys: present with non-empty text (parity test guards en↔uk symmetry; this guards
  both-missing).
- **Content view language fidelity** — for each of the six content views (copied to test
  output via csproj): the file contains its document's exact title line from `samples/`
  ("Privacy Policy" / «Політика конфіденційності», "Terms of Service" / «Умови надання
  послуг», "End User License and Sublicense Agreement" / «Ліцензійна та субліцензійна
  угода кінцевого користувача») — catching a swapped-language or copy-pasted-wrong-doc
  transcription.
- **Transcription completeness (tail guard)** — each content view contains its document's
  final-section marker (e.g. "support@proovegaming.com" occurrences, or the last heading
  text per file) — a truncated transcription is the likeliest real failure.
- **InformationView wiring** — `InformationView.axaml` references all three
  `{DynamicResource InformationView_Tab_*}` keys.
- **(2026-09-03) Sign-up links** — the three `WelcomeView_Terms_*` link keys exist in
  both languages and `WelcomeView.axaml` binds three distinct commands.
- **(2026-09-03) Email links + italics** — `EmailRendersAsLink` pins that every content
  view renders the support address as a `legalDocEmailLink` Button (never as plain run
  text), and `RegistrationLineItalicMatchesTheDocx` pins the docx italics. The mailto
  launch and mouse-button behaviour are untestable without a TopLevel — QA step 9.
- **(2026-09-03) Installer views** — `AgreementTermsEnView`/`UkView` carry the Privacy
  Policy title (`agreementDocTitleText`) and its final section marker (title+tail guard,
  file-content assertions — no reference to the Installer project needed).

**Deliberately not covered:** tab click → content swap, highlight toggling, scroll reset,
and `LanguageChanged` re-render — code-behind against a live visual tree; no headless
harness in the suite (same accepted gap as Phase 1's navigation wiring). Covered by
manual QA below.

## QA (manual test steps)

1. Launch the app (English language), sign in, click the avatar → "Information".
   Expected: the Information page opens; the left panel shows three tabs — PRIVACY
   POLICY, TERMS OF SERVICE, LICENSE AGREEMENT; the first tab is highlighted and the
   panel shows the "Privacy Policy" document.
2. Scroll the document to the bottom. Expected: it ends with section "16. Contact Us"
   and the support@proovegaming.com address; the thin scrollbar is visible the whole time.
3. Click TERMS OF SERVICE. Expected: the highlight moves, the panel shows "Terms of
   Service" from the top; the gavel icon above the label turns violet and the other two
   tab icons are dimmed.
4. Click LICENSE AGREEMENT. Expected: the panel shows "End User License and Sublicense
   Agreement" from the top.
5. Go back (arrow), open Settings, switch the language to Ukrainian, return to Home and
   open the Information page again. Expected: tabs read ПОЛІТИКА КОНФІДЕНЦІЙНОСТІ /
   УМОВИ НАДАННЯ ПОСЛУГ / ЛІЦЕНЗІЙНА УГОДА (no clipped labels), and the displayed
   document is «Політика конфіденційності» in Ukrainian.
6. Click each tab in Ukrainian. Expected: each document displays in Ukrainian, each
   opens scrolled to the top.
7. Log out, continue as Guest, open Information from the guest dropdown. Expected: same
   three tabs and documents work.
8. Resize and maximize the window on each tab. Expected: the document area stretches,
   text re-wraps, nothing overlaps the frame, and after scrolling to the bottom the
   final contact line is fully readable (not dimmed by the fade) at every size.
9. Click the underlined support@proovegaming.com address (left button). Expected: the
   default mail client opens a new message to that address. Right-click and middle-click
   on the address do nothing. Repeat once on the sign-up agreement document and once in
   the installer agreement step.

## Review focus

1. `localization` — three new keys in both files; content view ↔ language pairing
   correct; no fixed widths on wrapping labels.
2. `UI-thread affinity` — `UpdateDocument()` is synchronous view construction on click;
   nothing may do I/O or long work in it.
3. `resource disposal` — the `LanguageChanged` subscription follows the
   singleton-no-unsubscribe convention and is subscribed exactly once (constructor, not
   `OnLoaded` — resubscribing on every visit would leak handlers).
4. `null-safety` — `DocumentHost` and highlight-group lookups are generated fields, not
   `FindControl` bangs.

## Performance & quality

**No hot paths.** Content views are built on explicit user action (tab click / language
switch) — a one-time layout of a large TextBlock, no per-frame or polling work, no
timers, no async. No caching of content views (installer precedent; measured cost is a
single layout pass) — revisit only if QA sees a perceptible hitch on tab switch, in which
case a 6-slot lazy cache in the view is the fix. No shared singleton state is touched.
Quality: the six content views are mechanical transcriptions — the reviewer verifies them
against `samples/*.txt`, not by proofreading legal prose.

## Risks

- **Transcription fidelity.** A dropped paragraph in a legal document is a real product
  risk and invisible in a quick glance. Mitigation: transcribe mechanically from
  `samples/` (which are versioned), the tail-guard tests, and QA steps 2–4.
- **Octagon geometry drift.** The 232px path and three-zone arithmetic are hand-derived
  from Profile's 164px two-zone panel; an off-by-a-few-px shows as misaligned highlights.
  Mitigation: the zone table in the approach section; QA visually compares with Profile.
- **Long uk labels.** «ПОЛІТИКА КОНФІДЕНЦІЙНОСТІ» is one 16-character word — it must fit
  120px at 12px SemiBold on its own line. If it clips, the fallback is FontSize 11 for
  the tab labels (both languages, so the tabs stay uniform).
- **Language switched while on the page.** `LanguageChanged` fires synchronously on the
  UI thread while the page may be invisible; `UpdateDocument()` runs regardless — cheap
  and keeps the invisible page consistent, but the coder must not touch layout-dependent
  state (e.g. scroll `Extent`) in the handler beyond resetting `Offset`.

## Open questions

None — the two label questions were resolved with the user on 2026-09-03 (tab 2 "Terms
of Service" matching the document title; tab 3 short "License Agreement"). The Privacy
Policy 11→13 numbering gap is transcribed as-is and flagged; if legal supplies a fixed
document, re-extract `samples/` and update the views.

## Follow-up: sign-up links reuse (2026-09-03)

After Round 1, the same documents were wired into the **sign-up agreement sentence**
(user request): the WelcomeView checkbox now renders three separate links — Terms of
Service, Privacy Policy, License Agreement (`WelcomeView_Terms_TermsOfService` /
`_Comma` / `_PrivacyPolicy` keys added, `WelcomeView_Terms_Link` removed) — each raising
`WelcomeViewModel.AgreementTermsRequested` with a `LegalDocument` value (new shared enum
in `Models/`). `AgreementTermsView` lost its hardcoded placeholder EULA and
`AgreementTermsView_Title` key: `ShowDocument(LegalDocument)` (called from MainWindow's
`OnAgreementTermsRequested`) sets the title from the `AgreementTermsView_Title_*`
resources (normal case — initially reused the uppercase `InformationView_Tab_*` keys,
corrected per PR #131 review m1)
and hosts the per-language content view resolved by the new shared factory
`Views/Information/LegalDocumentContent` — which `InformationView.UpdateDocument()` now
also uses (its private enum was replaced by `LegalDocument`). No `LanguageChanged`
subscription in AgreementTermsView: content resolves at click time and the language
cannot change while the step is up. Tests: `SignUpLinkKeyIsPresentWithText` (en/uk × 3)
and `SignUpLinksOpenDistinctDocuments` added; full suite 1583/1583 green.

**Installer (same day):** the installer's agreement step content
(`ProoveGaming.Installer/Views/AgreementTermsEnView.axaml` / `AgreementTermsUkView.axaml`)
was regenerated from `samples/privacy-policy.{en,uk}.txt` — replacing the placeholder
EULA with the real Privacy Policy (39 lines / 15 headings per language, matching the
App's views) using the installer's existing `agreementTitleText` / `agreementBoldText` /
`agreementRegularText` classes. The step header title (`Step2_Header_Title`, "Customer
Privacy Policy") was deliberately left unchanged per the user. Release note: this diff
touches the Installer project — it ships inside the single-EXE installer payload, so it
lands with the next `installer-release` run.

**Installer scroll fix (2026-09-03, found by the user):** with the long document, the
agreement box's scroll extent came up ~2 lines short and the final "16. Contact Us"
paragraph could never scroll into view (the scroll gate still enabled Next at the
truncated extent). Cause: the auto-width `AgreementContentHost` let the wrapped text
measure and arrange at slightly different widths. Fix: fixed `Width="576"` +
`Margin="0,0,0,16"` on the ContentControl in `AgreementStepView.axaml` — verified by
driving the rebuilt installer to the agreement step and screenshotting the fully
scrolled document. The App's Information page was verified unaffected (stretch
viewport, en, default + maximized).

## Review findings

### Round 1 (2026-09-03) — Ship: yes

**Review surface note:** same branch state as the Phase 1 review
(`../add-information-page/add-information-page-plan.md`, Round 1): three committed
commits belong to other, already-reviewed plans and are out of scope; the Phase 1 files
(HomeView, MainWindow.*) are unchanged since that round. This round reviews the Phase 2
delta: `App.axaml`, the two resource files, `InformationView.axaml`/`.axaml.cs`
(reworked), the 12 files under `Views/Information/`, the extended test class + csproj,
and the doc updates.

#### Plan conformance
- Implemented as planned: all 15 File-level change list items. Octagon geometry matches
  the plan's zone table exactly (verified arithmetically: zones 16–80 / 84–148 / 152–216,
  octagon corners at y 20/212, canvas 232; overlays 0–82 / 82–150 / 150–232). Tab order
  and labels match the user's confirmed decisions (Terms of Service, short License
  Agreement). Styles, keys, `LanguageChanged`-in-constructor, `OnLoaded` reset, and the
  scroll reset are all as specified.
- Transcription fidelity: content views verified mechanically — run count equals source
  line count (e.g. `LicenseAgreementUkView` 40/40), heading counts identical per language
  pair (15/18/16), tails intact through the final contact section, the Privacy Policy
  11→13 numbering gap preserved, UTF-8 Ukrainian text and `&quot;` escaping correct.
  The six views were script-generated from `samples/*.txt`, which removes the dropped-
  paragraph risk the plan called out.
- Deviates from plan: none found.
- Accepted limitation (per plan): no automated coverage of tab click → swap → highlight →
  scroll reset (no headless harness) — residual risk is a silent wiring regression,
  covered by QA steps 3–6. Content views are re-created per switch (no cache) — accepted,
  with the 6-slot cache named as the fallback if QA sees a hitch.
- Acceptance criteria: all code-verifiable ones hold (tests pin tab keys in both
  languages, all three `DynamicResource` label bindings, per-document title + tail
  markers). Visual criteria (label fit, highlight alignment, scrollbar) remain manual QA.

#### Code
- none — Review focus walked: (1) localization: keys in both files, content↔language
  pairing in `UpdateDocument`'s switch is correct, labels wrap inside the design's 120px
  column rather than sizing off the en string; (2) UI-thread: `UpdateDocument` is
  synchronous control construction only, no I/O; (3) the `LanguageChanged` subscription
  is in the constructor (once per singleton view), not `OnLoaded` — no resubscribe leak;
  (4) null-safety: all lookups are generated `x:Name` fields, no `FindControl` bangs.

#### Tests
- none — 1576/1576 green including the 13 new assertions; the fidelity theory
  (`ContentViewCarriesItsDocument`) genuinely exercises the transcription (title run +
  last heading per file), not just file existence.

#### Nits
- Clicking the already-active tab does nothing (`SelectSection` early-returns), so it
  does not re-scroll to top — consistent with Profile's tabs; noted only so QA does not
  file it as a bug. — `src/ProoveGaming.App/Views/InformationView.axaml.cs:49`

#### What's done well
- Script-generating the six content views from the versioned `samples/*.txt` turned the
  riskiest part (legal-text fidelity) into a verifiable mechanical step instead of
  hand-transcription.
- The tail-guard tests pin exactly the failure mode a reviewer cannot eyeball — a
  truncated or language-swapped document.
- The tab panel reuses Profile's zone arithmetic rather than inventing a new layout, so
  the two pages will stay visually consistent.

#### Verdict
Ship: yes — no blockers, no should-fixes; the diff maps 1:1 to the plan and all
automated checks pass. First: (1) QA step 5 — verify «ПОЛІТИКА КОНФІДЕНЦІЙНОСТІ» fits
the 120px tab (16 uppercase characters at 12px SemiBold is borderline; the plan's
fallback is FontSize 11 for all tab labels); (2) QA steps 3–6 for the untestable
click/highlight/scroll wiring; (3) side-by-side visual check of the octagon against
Profile's.

### Round 2 (2026-09-03) — Ship: yes

**Review surface:** PR #131 was squash-merged into `main`; this round covers the
outstanding tip-to-tip delta on the recreated branch (commits `f9b30ec..90dc62d` plus
the `origin/main` merge `55838e2`): the installer agreement scroll-extent fix, the
Information viewport insets (48 top / 24 bottom) + bottom fade mask, the 200px tab
panel, inline mailto email links (shared `LegalEmailLink` in Common, baseline-aligned
via a negative-bottom-margin inline container), the italic registration line, and the
reworked `EmailRendersAsLink` test. Verified on the merged build: full suite
1593/1593 green, App + Installer build clean, visual smoke of the Information page
(wider uk tabs, insets, italic, fade, per-visit reset) all intact.

#### Plan conformance
- The plan's live sections were kept up to date change-by-change (styles, geometry
  note, italic note, installer scroll fix, follow-up journal), with one drift — see S4.
- Deviates from plan (user-driven, documented): tab panel 200px wide vs Profile's 160
  — the two pages' silhouettes no longer match; accepted per the user's "make it wider"
  instruction, noted in the approach's dated geometry note.

#### Code
- [x] **S1. The document's last line can never be read at full opacity** —
  `src/ProoveGaming.App/Views/InformationView.axaml` (DocumentScrollViewer OpacityMask
  + DocumentHost margin) — Should-fix
  **Problem:** the fade mask turns transparent over the viewport's last 15% (~100px at
  a 900px window), but the scrolled content's bottom padding is only 48px — so at full
  scroll the final line (the support-email paragraph) sits ~48px above the viewport
  bottom, inside the fade, at roughly half opacity, permanently.
  **Why it matters:** the very line users scroll down for (the contact email) is the
  one line that can never be shown fully opaque; the in-XAML comment ("the content's
  bottom padding lets the document's last line scroll up out of the faded zone")
  promises otherwise.
  **Fix:** match the fade depth to the padding — e.g. mask stops `0.93 → 1` (~48px at
  the default window) with the existing 48px content padding, or raise the padding to
  ~110px and keep 0.85. Percent-based masks scale with window height, so pick the pair
  that stays ≥ padding at the largest supported window.
- [x] **S2. Sign-up agreement box likely has the installer's unreachable-tail bug** —
  `src/ProoveGaming.App/Views/AgreementTermsView.axaml` (600×460 box, auto-width
  `DocumentHost`) — Should-fix (PLAUSIBLE)
  **Problem:** the installer's identically-shaped box (fixed 460 ScrollViewer +
  auto-width ContentControl + full-length document) measured its scroll extent ~2
  lines short, making the final paragraph unreachable; it was fixed with a fixed-width
  content host (`f9b30ec`). The App's sign-up agreement box has the same geometry and
  did NOT get the fix; only the Information page (stretch viewport) was verified
  unaffected.
  **To confirm:** sign-up form → click any of the three links → scroll to the bottom —
  check the document's last paragraph is fully reachable.
  **Fix if it reproduces:** same as the installer — fixed `Width` (≈540 for the 600px
  box with 16/20 side margins) + a small bottom inset on the ContentControl.
- [x] **S3. Email links open the mail client on any pointer button** —
  `Views/Information/*.axaml` + installer views (`PointerPressed="OnEmailLinkPressed"`)
  — Should-fix
  **Problem:** `PointerPressed` fires for right and middle clicks too, so
  right-clicking the address launches the mail client — links elsewhere in the app
  (`linkButton` Buttons) only react to the primary button.
  **Fix:** in the shared handler(s), gate on
  `e.GetCurrentPoint((Control)sender!).Properties.IsLeftButtonPressed` before calling
  `LegalEmailLink.Open` (one-line change in the 8 generated code-behinds, or move the
  check into `LegalEmailLink.Open` by passing the event args).
- [x] **S4. Plan approach step 2 describes the old viewport geometry** —
  `docs/plans/add-information-sections/add-information-sections-plan.md` (Recommended
  approach, step 2) — Should-fix (docs)
  **Problem:** step 2 still says "vertical margin 24" and text inset "24,0,20,0"; the
  shipped geometry is 48 top / 24 bottom viewport margins, `DocumentHost`
  Margin="24,0,20,48", plus the gradient fade — only the style step (4) and the dated
  notes were updated.
  **Fix:** correct step 2 with a dated amendment like the others.

#### Tests
- none failing — 1593/1593 on the merged tree. Coverage note: the email-link tests are
  file-content assertions; the mailto launch itself (`LegalEmailLink.Open`) is
  untestable without a TopLevel and stays manual (right-click behaviour of S3
  included).

#### Nits
- The `-4` bottom margin on the inline link containers encodes the 16px font's
  ascent/descent split; nothing in the generated files says so — worth a comment in
  the generator or the plan when next regenerated.
- The fade mask also dims the scrollbar's bottom few pixels (the bar lives inside the
  masked ScrollViewer) — invisible at current depths, becomes noticeable if S1 deepens
  the mask; if so, exclude the bar by masking the ContentPresenter instead.

#### What's done well
- Every visual tweak in this round was verified against the running app with
  screenshots (including a 2× zoom for the baseline/clipping iterations) — the three
  failed alignment attempts were caught in-session instead of shipping.
- The installer scroll bug was reproduced, root-caused to the measure/arrange width
  mismatch, and the fix re-verified end-to-end through the wizard.
- The squash-merge conflict resolution took the branch versions wholesale and was
  validated by a full build+test+visual pass rather than assumed.

#### Verdict
Ship: yes — no blockers; S1–S3 are small, well-scoped follow-ups and S4 is a doc
amendment. First: (1) S2 — confirm/fix the sign-up agreement box tail; (2) S1 — match
fade depth to the content padding; (3) S3 — left-button gate on the email links. Note:
the old PR is merged and closed — these commits need a **new PR** from
`features/texts-update` to `main`.
