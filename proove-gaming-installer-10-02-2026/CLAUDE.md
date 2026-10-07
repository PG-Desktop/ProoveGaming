# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Stack

.NET 10, Avalonia (Windows desktop). Solution file: `src/ProveGaming.slnx` (note spelling: `ProveGaming`, single "o"). Windows target framework is centrally defined in `src/Directory.Build.props` (`net10.0-windows10.0.26100.0`). Nullable reference types are enabled.

## Common commands

Run from repository root.

Solution-wide:

- Restore: `dotnet restore ./src/ProveGaming.slnx`
- Build: `dotnet build ./src/ProveGaming.slnx`
- Test (all): `dotnet test ./src/ProveGaming.slnx`

Scoped:

- Build App only: `dotnet build ./src/ProoveGaming.App/ProoveGaming.App.csproj -c Release`
- Build installer only: `dotnet build ./src/ProoveGaming.Installer/ProoveGaming.Installer.csproj -c Release`
- Build uninstaller only: `dotnet build ./src/ProoveGaming.Uninstaller/ProoveGaming.Uninstaller.csproj -c Release`
- Publish single-EXE installer: `dotnet publish ./src/ProoveGaming.Installer/ProoveGaming.Installer.csproj -c Release`
  - Output: `./src/ProoveGaming.Installer/bin/Release/net10.0/win-x64/publish/ProoveGaming.Installer.exe`

Tests:

- Single test project: `dotnet test ./tests/ProoveGaming.App.Tests/ProoveGaming.App.Tests.csproj`
- Single test class or method (xUnit): `dotnet test ./tests/ProoveGaming.App.Tests/ProoveGaming.App.Tests.csproj --filter "FullyQualifiedName~SessionServiceTests"`
- Framework is **xUnit** (v2). Tests live under `tests/`, **not** under `src/`, but are included in `src/ProveGaming.slnx`.

CI note: `.github/workflows/dotnet-ci.yml` builds `src/ProoveGaming.App/ProoveGaming.App.csproj`, then runs `dotnet test ./src/ProveGaming.slnx --configuration Release` on `windows-latest` — the full test suite (all `tests/` projects) runs in CI, in **Release**. A test that passes locally in Debug can still fail there: verify timing-sensitive changes with a Release slnx test run before pushing.

### Match the verification to the change

A full `dotnet build ./src/ProveGaming.slnx` plus `dotnet test` takes minutes. Spend it when the change can actually break something, not by reflex:

- **Documentation only** — anything under `docs/`, any `*.md`, or a plan update: **run nothing.** No build, no tests. There is nothing to compile and nothing to regress.
- **Comments only inside source files** — no statement, signature, attribute or XAML changed: **at most a scoped build of the touched project** (`dotnet build ./src/<Project>/<Project>.csproj`), never the full solution and never the test suite. The scoped build is only there for the two ways a comment really can break a build: an unterminated `/* … */`, and malformed XML in a `///` doc comment. If neither is possible for the edit you made, skip it.
- **Anything else** — code, XAML, `.csproj`, `.slnx`, `appsettings.json`, resource files (`.axaml` strings) — build and run the **affected** tests as usual, and the full suite when both sides of a seam are touched.

The rule is about what the edit *can* affect, not about how many files it touched: a one-word rename inside a string resource is a functional change, and a 200-line comment rewrite is not. When a change mixes documentation and code, it is a code change — verify it accordingly.

## Repo layout at a glance

- `src/` — all production projects, plus two developer consoles that are in the slnx but never shipped: `ProoveGaming.Device.Testing` (hardware protocol probes for the supported devices) and `ProoveGaming.DeviceHost.Checker` (polls the DeviceHost's gRPC endpoint to confirm it is reachable).
- `tests/` — xUnit test projects (`ProoveGaming.App.Tests`, `ProoveGaming.Infrastructure.Tests`, `ProoveGaming.Installer.Tests`; the last covers both the installer and the uninstaller).
- `docs/` — protocol references (device / LED / gRPC), plans under `docs/plans/<slug>/`.
- `.github/workflows/` — `dotnet-ci.yml` (PR/push CI), `release.yml` (manual `installer-release`, publishes the single-EXE installer as a zipped asset on a GitHub pre-release).
- `.claude/skills/` — project-scoped Claude Code skills (`/architect`, `/coder`, `/reviewer`, `/test-writer`, `/orchestrator`).
- `.claude/agents/` — subagents for direct dispatch (`orchestrator`).

## Documentation rules

These rules are mandatory for every contribution.

- **English only.** All documentation in this repository is written in **English** — plans under `docs/plans/`, protocol references, READMEs, code comments meant as docs, and any Markdown added anywhere. Do not commit documentation in any other language.
- **Glossary (`docs/CONTEXT.md`).** Every domain / project term must be described briefly in `docs/CONTEXT.md`. Whenever a new term appears (a device model, a protocol concept, a service name, an acronym, a subsystem), you **must** add a short one-line definition for it to `docs/CONTEXT.md` in the same change. If the term is already there, leave it; never introduce a term without recording it.
- **Knowledge base (`docs/knowledge-base/`).** Any documentation that arrives in a language other than English must be **translated into English** and placed under `docs/knowledge-base/`. The same folder holds any documentation Claude fetches from the internet (specs, vendor protocol docs, articles) — save the relevant English version there so it is versioned with the repo rather than re-fetched.
- **Keep existing plans true (`docs/plans/`).** A plan is not a write-once record: it is the standing description of how a feature is meant to work, and `/reviewer` reviews against it. So **whenever a change touches behaviour, UI, or files an existing plan describes, update that plan in the same change.** Before finishing any non-trivial change, grep `docs/plans/` for the files you edited and for the feature's name, and reconcile what you find.
  - **What to update.** The **File-level change list**, **Acceptance criteria**, **QA steps**, and any prose whose vocabulary the change invalidates (a control renamed from "tile" to "option", a flow that gained a step, a capability that moved). If an acceptance criterion is no longer how the feature behaves, correct it — a criterion that silently stops matching reality is worse than none, because the next review passes it by reading the wrong contract.
  - **Never rewrite the review journal.** `## Review findings` is a dated record of what was reviewed and decided at the time. Leave its wording alone even when it is stale — falsifying history to match the present destroys the reason it exists. Correct the live sections and, where it helps, add a short dated note saying what superseded what.
  - **When the change has no owning plan.** If the work is substantial and no plan describes it, either write one (`/architect`) or add a dated note to the closest existing plan pointing at the change. Do not leave a feature's plan quietly describing a UI or behaviour that no longer ships.
  - **Why.** `/coder`, `/reviewer` and `/test-writer` all read plans as the statement of intent. A plan that has drifted from the code sends every later round chasing the wrong contract, and the drift is invisible until someone trusts it.

## Coding conventions

- **Remove the cause; don't build machinery around it.** Before hardening, guarding, retrying or
  verifying something, ask whether the thing being guarded should exist at all. A layer of careful
  management wrapped around a mechanism that was never needed is *worse* than the mechanism: it is
  more code, it hides the real problem, and every later reader assumes the mechanism is load-bearing
  because of all the machinery defending it. Prefer deleting the source of a failure over containing
  its consequences.
  - **Symptoms you are doing this:** a new shared abstraction whose only job is to clean up after
    another part of our own code; a "verify it actually worked" pass over a write we control; a
    second guard because the first one failed for reasons nobody established; a user-facing error
    string explaining a state the product should not be able to reach.
  - **This app is a desktop utility for mice and keyboards.** Don't *introduce* a design that needs
    a reboot, a safe-mode step, a registry repair, or manual user cleanup to install, uninstall,
    reinstall or recover. If a file is locked, close whatever holds it; if that cannot be done,
    leave it and **say so in the log** — deferring the work to the next boot is not the answer,
    because a scheduled deletion condemns a *path* and will take whatever occupies it later.
    (Known exception, not a licence for more: `sc delete` on a hung service is marked-for-deletion
    until reboot, which Windows gives no way around. Where such a limit is genuinely imposed by the
    OS, document it rather than building around it.)
  - **When a guard fails and you cannot explain why, do not stack a second guard on it.** Find out
    why, or remove what made the guard necessary. Unexplained failure plus more defence is how a
    small bug becomes permanent.
- **No vertical token alignment.** Use a single space around `=` (and other operators); do **not** pad with extra spaces to line up `=`, values, or comments into columns. Column alignment is a Terraform/HCL habit, not idiomatic C# — `dotnet format`/Roslyn won't preserve it, and it creates diff churn (one longer identifier forces re-padding the whole block). This applies to fields, properties, locals, and object initializers alike. (Markdown tables are fine to align.)
- **Match the surrounding file.** Follow the existing style, naming, and comment density of the file you're editing rather than introducing a new one.
- **Prefer async.** When an async API exists (file, network, gRPC, DB, `JsonSerializer`), use it (`ReadAllTextAsync`, `SerializeAsync`, `FileOptions.Asynchronous`, …) instead of the sync variant, propagate `async`/`await` up the call chain, and flow a `CancellationToken` through. Never block on async with `.Result` / `.Wait()` / `.GetAwaiter().GetResult()`. Note you can't `await` under `lock`/`Lock` — use `SemaphoreSlim.WaitAsync` when a critical section awaits.
- **Never freeze the app / never block the UI thread.** No change may make the app stutter or hang. The UI (Avalonia dispatcher) thread must never run blocking I/O, synchronous waits, long CPU loops, or `Task.Wait()`/`.Result`/`Thread.Sleep` — offload real work to `Task.Run`/background services and marshal only the result back via `Dispatcher.UIThread`. Keep per-keystroke, per-frame, and binding/`PropertyChanged` handlers cheap and allocation-light, and avoid re-entrant update storms (a handler that writes a bound property which re-invokes the same handler). If a UI action needs meaningful work, it must stay responsive (show progress / run async), not spin the dispatcher. Treat any visible cursor/typing hitch as a bug to fix, not accept.
- **Visibility.** In the Installer/Uninstaller, make window/view classes (e.g. `MainWindow`) `internal` by default; expose to tests via `InternalsVisibleTo`, don't make them `public`.
- **Don't duplicate UI.** If a component or style is reused more than once across views/projects, extract it — shared controls into `ProoveGaming.Common`, shared styles into a central `ResourceDictionary` (e.g. App-level styles) rather than per-view copies.
  - **The trigger is the second copy, not the third.** When adding a style, grep for a distinctive setter from it (a hex colour, a `Height`) before writing it inline. If a near-identical block already exists in another view, extract instead of copying — a per-view copy is only justified when the style is genuinely unique to that view.
  - **Where it goes.** Styles used by more than one App view live in `src/ProoveGaming.App/App.axaml` under `Application.Styles`; styles shared between App and Installer live in `ProoveGaming.Common`. Never leave the same rule in two view files.
  - **Name by role, not by location.** `sectionActionButton`, not `dpiRecommendationsButton` — a name tied to one call site invites the next person to copy it for their own section.
  - **Keep the variant, share the rest.** When copies differ in one or two setters, put the common setters in the shared class and apply both classes at the call site (`Classes="sectionActionButton rgbRandomButton"`), with the variant style holding only what actually differs. Do not fork the whole style over one colour.
  - **Restate what the default theme also sets.** Fluent styles `/template/ ContentPresenter#PART_ContentPresenter` on `:pointerover`, so a hover rule that sets only `Background` lets the theme repaint `BorderBrush` — the visible symptom is a border vanishing on hover. Re-state `BorderBrush`/`BorderThickness` in the hover rule.
  - When extracting, check the copies for drift first: divergent values are usually an unintended bug in one of them, not a deliberate variant. Say which value you standardised on and why.
- **SVG templates → Canvas.** When given an SVG as a visual reference for a frame/panel, transcribe it as an explicit Avalonia `Canvas` with `Path`/`Rectangle` and the raw path data; use the `<Svg>` control only as a last resort. Map SVG fills separately — `fill="black" fill-opacity="0.8"` → `Fill="Black" Opacity="0.8"`, not a consolidated hex-alpha.
- **Emphasis is never colour-only.** When emphasizing text, pair colour with a second cue (bold/italic/weight/underline) so it reads reliably.
- **Localization — every change ships both languages.** User-facing strings come from language-specific resource files (en/uk), not runtime string translation/transformation. No magic strings for localization keys or language persistence — define constants in shared/common and use them from both App and Installer. The uninstaller resolves language from a persisted registry preference (debug builds may override; fall back to a default if missing).
  - **A change that adds or renames user-facing text is not done until BOTH `Strings.en.axaml` and `Strings.uk.axaml` have the key.** Adding only `en` is not "half done" — it ships a bug, because the uk build silently falls back to the English string and nobody sees an error. The same applies to the Installer and Uninstaller, which have their own resource pairs.
  - **Watch the value→key converters, not just the resource files.** Text that reaches the UI from `appsettings.json` (device types, capability labels) is mapped to a key by a converter such as `DeviceTypeLabelConverter`. Those converters end in `_ => null` and return the RAW English string for anything unmapped, so adding a new catalog value without extending the converter produces untranslated UI with no build error and no exception. Adding a device type means: catalog value → converter case → `en` key → `uk` key. Missing the last three is invisible until a Ukrainian user looks at the card (`Wireless Headset` shipped this way).
  - **Never hardcode the language.** `LanguageResourceParityTests` pins en/uk key parity for every resource pair; if it fails, a key was added to one language only.
- **Localization-safe sizing: never a fixed `Width` on a control that renders localized text.** Buttons, tabs, chips and labels size from their content — `Padding` for the inset (side padding of 9–16px depending on the control), plus `MinWidth` when the design names a minimum — never a hardcoded `Width` measured off the en string. The uk strings are routinely 30–50% wider ("CANCEL" → "СКАСУВАТИ"), and a fixed width pushes them flush against the border. Reference pattern: the shared `cancelDelete`/`confirmDelete` dialog buttons (`App.axaml` — `Padding="16,0"` + `MinWidth="105"`, sizing lives in the style so call sites don't need to repeat it — a local `Width` would still override the style, so don't add one) and the `macroTab` parallelogram tabs (15px slant columns + a stretching middle, label `Margin="24,0"`). When adding such a control, verify the layout with the longer of the two languages, not just en.

> The installer's finer visual specs (exact disabled-button colours, agreement-step bold numbering, welcome-area sizing, folder-checkbox styling, etc.) live in `src/.github/copilot-instructions.md` — check there before touching installer UI.

## Architecture — the big picture

The product is split across three runtime processes plus supporting libraries:

1. **`ProoveGaming.App`** — the user-facing Avalonia desktop app.
2. **`ProoveGaming.DeviceHost`** — a Windows Service that owns HID/Bluetooth device I/O and exposes it over gRPC.
3. **`ProoveGaming.Installer` / `ProoveGaming.Uninstaller`** — dedicated Avalonia-based install/uninstall executables.

They communicate as follows:

- **App → DeviceHost via gRPC** over the named pipe `\\.\pipe\ProoveGaming.DeviceHost` (one constant, `DeviceHostPipe.PipeName` in `ProoveGaming.GrpcDependencies`; every client gets its channel from `DeviceHostPipe.CreateChannel`). The pipe's ACL admits SYSTEM, Administrators and interactive users only. A **Release** App also verifies that the pipe's server is the installed `ProoveGamingDeviceHost` service, so it will not talk to a DeviceHost started from the console; Debug builds relax that check. See `docs/plans/migrate-devicehost-named-pipe/`.
- **App → backend HTTP API** for auth/session (`ApiSettings.UsersApiBaseUrl` in the same file).
- Contracts live in **`ProoveGaming.GrpcDependencies/Protos/deviceservice.proto`** — proto changes belong there and both sides regenerate off it.

### `ProoveGaming.App` internals

- **DI is wired by hand** in `App.axaml.cs::BuildServices()` using `Microsoft.Extensions.DependencyInjection`, not via Avalonia locators. `App.Services` exposes the container statically for XAML/code that can't inject. When adding a service, register it there — don't rely on convention scanning.
- **Single-instance enforcement**: `Program.cs` takes a named `Mutex` (`AppInstanceConstants.SingleInstanceMutexName`). A second launch signals the running instance via a named `EventWaitHandle` (`AppInstanceConstants.SingleInstanceActivationEventName`), which restores the window from tray. Long-lived background loop is in `App.InitializeSingleInstanceActivation`.
- **Session lifecycle**: `LocalStorageService` persists session state and "Remember me" auth. On startup, `App.OnFrameworkInitializationCompleted` reads it and either clears expired data, restores an active guest `SessionService`, or rehydrates `UserProfile` from a saved token. Never construct these services ad-hoc — go through DI so the same singletons are used.
- **Tray behavior**: window close hides to tray via `TrayIcon`; `SessionService.GoToTray(...)` and `IUsbHidDeviceService.CheckIfAllDevicesAreDisconnected()` are coupled here — changing tray flow means touching both.
- **MVVM layout**: `Views/` (Avalonia `.axaml`), `ViewModels/`, `Services/`, `Models/`. `ViewLocator.cs` resolves ViewModels to Views by naming convention. Some ViewModels are singletons, some transient — check `BuildServices()` before adding a new one.
- **Localization**: `LocalizationManager` (initialized from system culture) — user-facing strings go through it, not string literals. Resources are per-project (App and Installer each have their own `LocalizationManager` and `Resources/`).

### `ProoveGaming.DeviceHost` internals

- ASP.NET Core `WebApplication` with **gRPC** endpoint (`DeviceService`) and a `Worker` hosted service.
- Registered as a Windows Service under the name **`ProoveGamingDeviceHost`** (`AddWindowsService`). Development runs it as a normal console process.
- Logging is dual: `AddConsole` + **Serilog** driven from `appsettings.json` (`Serilog:*` section). Debug level is auto-enabled when environment is `Development`.
- Device discovery services (`HidDeviceDiscoveryService`, `BluetoothDeviceDiscoveryService`, `DeviceInfoProvider`) are **singletons** that keep in-memory device state — resetting them mid-session drops connections. `HidDeviceStateFactory` is transient by contrast.
- Allowed device filter: `AllowedDevices` section in `appsettings.json` bound via `IOptions<AllowedDevices>`.
- **Settings are embedded, not shipped.** Both `appsettings.json` files (App and DeviceHost) are compiled into their assemblies as embedded resources; an install has no settings file to read or edit. Change them in the repository and rebuild. The DeviceHost swaps the host builder's file sources for the embedded copies (`Helpers/EmbeddedAppSettings.cs`), so environment variables still override them.

### `ProoveGaming.Infrastructure`

Shared HID / device abstractions (`Interfaces/`, `Services/`, `Classes/`, `CustomEvents/`) used by both App and DeviceHost. Cross-cutting device logic goes here; **App-only or Host-only code does not**.

### `ProoveGaming.Common`

Shared Avalonia UI plumbing consumed by App and Installer: `Styles/`, `Templates/`, `Layouts/`, `Localization/`, `Controls/`, `Assets/`. Anything reused between the two Avalonia entry points lives here.

### Installer / Uninstaller

- `ProoveGaming.Installer` is itself an Avalonia app with a multi-step wizard (`Views/`), published as a **single-file Windows EXE** (`win-x64`) via `dotnet publish -c Release`.
- Release build **embeds** the App payload and a built `ProoveGaming.Uninstaller.exe` into the installer, and the uninstaller is copied to the install dir on install. Touching installer/uninstaller code has release-process implications — call it out in PRs.
- Windows uninstall registration points at the standalone `ProoveGaming.Uninstaller.exe`, so it works both from OS Apps removal and by running the EXE directly.
- The installer auto-elevates on Windows (UAC) when admin rights are required.
- **Self-contained**: App, Installer, and Uninstaller all run directly from their EXE with no separately-installed .NET runtime.
- Installer/uninstaller UI files live under a `Views/` folder — keep that structure. License/legal text files live **outside** `Templates/` (in `Content/` or `Assets/`).
- Desktop and Start Menu shortcuts are optional installer checkboxes, enabled by default.
- The uninstaller is a dedicated UI-driven flow with three steps: confirmation (cancel/uninstall) → progress (deleting files) → completion (finish).

## Release path

- Regular CI (`dotnet-ci.yml`) runs on push/PR to `main` / `dev`: it builds the App csproj, runs the **full** test suite (`dotnet test ./src/ProveGaming.slnx`, Release), and fails on any High or Critical vulnerable package (direct or transitive). The job runs with a read-only token.
- Distribution is currently manual via the `installer-release` workflow: **Actions → installer-release → Run workflow**. It publishes on `windows-latest`, validates that `ProoveGaming.Installer.exe` was produced, zips it, and attaches it to a **GitHub pre-release**. There is one release per calendar day (tag `installer-<MM-DD-YYYY>`, title = the date); each run that day adds an incrementing asset `proove-gaming-installer-<MM-DD-YYYY>.<N>.zip` to that same release. Release assets are used instead of Actions artifacts to avoid the artifact-storage quota. This is treated as a temporary testing distribution path — see `README.md`.

## Project-scoped Claude skills

Five skills live in `.claude/skills/` and are the preferred way to structure work in this repo:

- `/architect` — writes a plan under `docs/plans/<slug>/<slug>-plan.md`; does not edit code.
- `/coder` — implements the plan; runs `dotnet build ./src/ProveGaming.slnx`.
- `/reviewer` — reviews the full branch delta vs `main`, categorizes findings, gives ship/no-ship.
- `/test-writer` — mirrors existing xUnit style in `tests/`, runs `dotnet test`.
- `/orchestrator` — drives a planned change through a fixed `coder → test-writer → reviewer → coder → test-writer` pipeline; coordinates the role skills, never edits code itself, and hands the result back to a human developer for final review and merge. A matching subagent lives at `.claude/agents/orchestrator.md` for direct dispatch.

Prefer these over ad-hoc work when the request fits a role. Plans always live in their own subfolder under `docs/plans/` — never as loose files.
