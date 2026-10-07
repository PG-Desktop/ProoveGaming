# Fix repository audit findings (security, dead code, architecture)

**Date:** 2026-09-24
**Kind:** mixed. Security items are `fix-*` (a failing reproduction test comes first). Dead-code items are `remove-*`. Pattern items are `refactor-*` (characterization tests come first where none exist).
**Source:** a full-repository compliance, architecture and security review of `main` @ `3d0e3a6`. Evidence for every finding is cited inline as `file:line` against that commit.

Related, not duplicated here:
[`docs/audits/2026-08-21-performance-memory-audit.md`](../../audits/2026-08-21-performance-memory-audit.md) (performance/leaks),
[`docs/audits/2026-08-21-api-needs.md`](../../audits/2026-08-21-api-needs.md) (backend gaps),
[`../ui-thread-storage-async/ui-thread-storage-async-plan.md`](../ui-thread-storage-async/ui-thread-storage-async-plan.md) (A4 closes its residue).

---

## Goal

Close the security holes, remove the dead code and fix the pattern violations found in the review, in an order that ships the highest-risk fixes first. The worst issues are in the installer and uninstaller. Both run elevated, but they take their inputs from places a standard (medium-integrity) process can write: `HKCU`, the command line, `%TEMP%`, the directory the EXE was launched from, and any custom install folder. That lets a non-admin process turn a normal install or uninstall into an admin-level file delete or a planted binary that runs as `LocalSystem`. The App side has smaller issues: the session token is stored in plaintext, configuration is loaded from the current working directory, and raw English or server text reaches the UI. The dead code is small but real. One whole feature chain (host-side button-binding persistence) no longer does anything, and while it exists it makes the `LocalSystem` service write into a folder any user can pre-create.

## Scope

**In:** every finding in the register below, grouped into five workstreams (phases). Phases 1–4 are ready for `/coder`. Phase 5 lists the findings that need a decision from the user (see **Open questions**) before any code is written.

**Out:**
- Performance and memory items already tracked in the 2026-08-21 and 2026-09-02 audits.
- Backend or API work (server-side lockout, token refresh), which is in `api-needs`.
- Splitting the god classes (`LocalStorageService` 5.7k lines, `HomeViewModel` 2.9k lines, `DeviceDetailsViewModel` ~12k across partials). This plan records them (A6) but does not refactor them, because that is not a fix and would dwarf everything else here.
- The deliberate synchronous HID I/O inside `Task.Run` in the Infrastructure device services (`Thread.Sleep` in `T800KeyboardActuationService.cs`, `SmartInfinite*`, and others). HidSharp streams are synchronous, and the calls are already off the UI thread and the gRPC dispatcher. It was reviewed and accepted, so it is not a finding.

---

## Findings register

Severity: **H** = exploitable across a privilege boundary, or data loss. **M** = real risk that needs a precondition. **L** = hygiene or defence in depth.

### Security

| ID | Sev | Finding | Evidence |
|----|-----|---------|----------|
| S1 | **H** | The elevated uninstaller recursively deletes a folder whose path comes from the command line (`TryGetInstallPathFromArgs`) or from `HKCU\…\Uninstall\ProoveGaming\InstallLocation`. Both are writable at medium integrity. Nothing checks that the folder is actually a Proove Gaming install. A user-level process can rewrite `InstallLocation` (for example to another application's folder). The next legitimate uninstall then deletes it with admin rights, through `Directory.Delete` and the elevated PowerShell self-delete loop. | `src/ProoveGaming.Uninstaller/UninstallService.cs:50-59`, `:291-319`, `:384-393`, `:538-569` |
| S2 | **H** | A per-machine install (Program Files, `LocalSystem` service, UAC) is registered for uninstall under **HKCU** of whichever account elevated. This causes S1. It also forced the SID-forwarding machinery (`--user-sid`, `Registry.Users\<sid>` lookup) whose only job is to find the entry again. | `src/ProoveGaming.Installer/Views/MainWindow.Installation.cs:799`, `src/ProoveGaming.Installer/InstallerConstants.cs` (`UninstallRegistryKeyPath`), `src/ProoveGaming.Uninstaller/UninstallService.cs:291-319`, `:373-381` |
| S3 | **M** | Any fully-qualified install path is accepted, and no ACL is applied afterwards. A custom location (for example `C:\Games\PG`) inherits its parent's ACL, where Authenticated Users can usually modify. `host\ProoveGaming.DeviceHost.exe` is then a `LocalSystem` service binary in a user-writable folder, which is a local privilege escalation (LPE). The same writable folder lets a user plant a junction that the PowerShell 5.1 `Remove-Item -Recurse` in the self-delete loop follows (S11). | `src/ProoveGaming.Installer/Views/MainWindow.axaml.cs:343-370`, no `SetAccessControl`/`DirectorySecurity` anywhere in `src/` |
| S4 | **M** | The elevated installer extracts the payload into the user's `%TEMP%\ProoveGamingInstaller\<guid>` and copies it into the install folder later. A same-user medium-integrity process can swap `host\*.dll` or `*.exe` in that window, and the swapped files then run as `LocalSystem`. This is a time-of-check-to-time-of-use (TOCTOU) binary plant. | `src/ProoveGaming.Installer/Views/MainWindow.Installation.cs:876-896`, `:92` |
| S5 | **M** | Elevated processes start `sc.exe`, `powershell.exe` and `cmd.exe` by bare name with `UseShellExecute = false`. `CreateProcess` searches the application directory first, so an `sc.exe` dropped next to a downloaded installer (in Downloads) runs as admin. This is [[Binary planting]]. | `src/ProoveGaming.Installer/Views/MainWindow.Installation.cs:414-426`, `src/ProoveGaming.Uninstaller/UninstallService.cs:246-258`, `:486-490` |
| S6 | **M** | The App reads `appsettings.json` from `Directory.GetCurrentDirectory()`, and reads user-secrets in Release builds too. Launched with an attacker-chosen working directory, it loads that folder's `appsettings.json`, and `UsersApiBaseUrl` can then point sign-in (email and password) at any server. Launched from a different working directory, it crashes (`optional: false`). | `src/ProoveGaming.App/ConfigurationManager.cs:20-22`, `src/ProoveGaming.App/ProoveGaming.App.csproj:20,40` |
| S7 | **M** | The "Remember me" bearer token is stored in plaintext (`saved_auth.token`) in the SQLite database under `%LocalAppData%`. Any same-user process, backup or profile sync can read it. | `src/ProoveGaming.App/Services/LocalStorageService.cs:304`, `:5302-5341` |
| S8 | **M** | The DeviceHost (`LocalSystem`) serves unauthenticated plaintext HTTP/2 without TLS (h2c) on the fixed TCP port `localhost:5000`. Any local process or user session can call `SetMacros`/`SetButtonBindings`, which persistently programs a mouse button to type arbitrary keystrokes. A process that binds `:5000` before the service starts denies service to the host and impersonates it to the App. Browsers cannot speak h2c, so this is not reachable from web pages. | `src/ProoveGaming.DeviceHost/appsettings.json` (`Kestrel:Endpoints`), `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto:11-101` |
| S8 · note | — | *2026-09-24:* implemented by [`migrate-devicehost-named-pipe`](../migrate-devicehost-named-pipe/migrate-devicehost-named-pipe-plan.md). gRPC now runs over the ACL'd named pipe `\\.\pipe\ProoveGaming.DeviceHost`, and a Release App only talks to a pipe served by the installed service. This also resolves **A10**, because the endpoint is now one constant. | — |
| S3 · note | — | *2026-09-24, from `/security-review`:* the first S3 fix protected only the install folder's ROOT, and only after extraction. An entry a standard user created in a fresh custom folder before that (for example `host\` or a junction in its place) stayed theirs, and the LocalSystem service then ran from it. Fixed: (1) any folder that is not an existing Proove Gaming install is removed and created again by the installer with the protected DACL already on it (`InstallAcl.TryCreateNewProtectedDirectory`, which fails if anything is already at the path); (2) right before the copy, `InstallAcl.ProtectExistingTree` refuses any link in the tree and makes every entry Administrators-owned, inheriting the root's rules only; (3) files are replaced (delete, then create new), never truncated in place. Tests in `InstallAclTests`. Residual, recorded: an attacker who controls an ANCESTOR of a custom location (for example a user-created `D:\Games`) can still rename it; the default Program Files location has neither issue. A pre-existing EMPTY folder is recreated, so a cancelled install now removes it. *(Round 5 fixes: `PrepareInstallFolder` is static and pinned by `InstallFolderPreparationTests`.)* | — |
| S6 · note | — | *2026-09-24, requester request:* both `appsettings.json` files (App and DeviceHost, plus the DeviceHost's `appsettings.Development.json`) are now embedded resources, not shipped files, so an install has no settings file to read or edit. App: `ConfigurationManager` and `AppSettings` both read the embedded stream only (`ConfigurationManager.OpenEmbeddedSettings`; Round 5 B1) (the working-directory and next-to-EXE cases of S6 both disappear). DeviceHost: `EmbeddedAppSettings` swaps the host builder's file sources for the embedded copies in place, so environment variables still override them. Change the files in the repository. Tests: `ConfigurationManagerTests`, `EmbeddedAppSettingsTests`. | — |
| D · note | — | *2026-09-24, requester request:* the Firmware section (a non-functional placeholder) is hidden, not removed: `DeviceDetailsViewModel.IsFirmwareNavVisible` is one constant that hides both nav entries, makes the section unreachable and drops it from the nav counts. Pinned in `DeviceDetailsNavVisibilityTests`. | — |
| S9 | L | gRPC input is normalised inconsistently. `SetMacros` clamps `KeyCode`, but `MapMacro` (used by `SetButtonBindings` and `ApplyDeviceConfiguration`) casts `(byte)e.KeyCode` unclamped. Every enum is cast without `Enum.IsDefined`. Event counts per macro are unbounded (limited only by the 4 MB message cap). | `src/ProoveGaming.DeviceHost/Services/DeviceService.cs:330-334`, `:373-377`, `:392-400`, `:420-422` |
| S10 | L | Raw transport and server text is shown to users: `HttpRequestException.Message` (host names, socket errors) and the server's own `message` field. This leaks information, and it is not localized (see A8). | `src/ProoveGaming.App/Services/ApiHttpClient.cs:73-76`, `src/ProoveGaming.App/ViewModels/WelcomeViewModel.cs:253,297`, `ProfileViewModel.cs:731,852,863,1014,1031`, `ChooseNicknameViewModel.cs:121` |
| S11 | L | The self-delete PowerShell 5.1 `Remove-Item -Recurse` follows directory junctions inside the install folder. This is only reachable when S3 holds, so it is fixed by S3 plus the S1 signature check. The plan adds a reparse-point guard as defence in depth. | `src/ProoveGaming.Uninstaller/UninstallService.cs:562` |
| S12 | L | CI has no `permissions:` block (the default token scope applies) and no vulnerable-dependency audit. The release pipeline ships unsigned EXEs that auto-elevate, so UAC shows "Unknown publisher", which makes S1/S5 social engineering easy. Code signing needs a certificate (Phase 5). | `.github/workflows/dotnet-ci.yml`, `.github/workflows/release.yml` |
| S13 | L | The DeviceHost (`LocalSystem`) writes Serilog to `%TEMP%/Logs/log.txt`, which resolves to `C:\Windows\Temp\Logs`. Standard users can create folders in `C:\Windows\Temp`. A pre-created `Logs` folder containing a link named `log<date>.txt` makes SYSTEM append to a file the user chose. The generic folder name also collides with other software. | `src/ProoveGaming.DeviceHost/appsettings.json` (`Serilog:WriteTo[1].Args.path`) |

### Dead code and unused dependencies

| ID | Finding | Evidence |
|----|---------|----------|
| D1 | The host-side button-binding persistence chain is dead. `ButtonBindingStore.SaveAsync` has **no caller** (`DeviceService` says "the host no longer keeps its own copy"), yet the `GetButtonBindings` RPC still serves that never-written store. The App wrapper `IUsbHidDeviceService.GetButtonBindingsAsync` has no production caller either (the App reads bindings from SQLite). The store also makes a SYSTEM process write `%ProgramData%\ProoveGaming\button-bindings.json`. | `src/ProoveGaming.DeviceHost/Services/ButtonBindingStore.cs`, `DeviceService.cs:35,50,344-345,451-472`, `Program.cs:66-67`, `src/ProoveGaming.App/Services/IUsbHidDeviceService.cs:35`, `UsbHidDeviceService.cs:403-425`, proto `rpc GetButtonBindings` |
| D2 | `IUsbHidDeviceService.SetMouseSettingsAsync` has no production caller. It was superseded by `ApplyDeviceConfigurationAsync` (`refactor-unify-device-writes`, `refactor-preset-scoped-device-writes`). Its only consumer is the host `SetMouseSettings` RPC. | `IUsbHidDeviceService.cs:22`, `UsbHidDeviceService.cs`, `DeviceService.cs:233-256`, proto `rpc SetMouseSettings` |
| D3 | `IAuthApiService.ResetPasswordAsync` has no production caller, and in-app reset is explicitly out of scope. The only references are test fakes. | `src/ProoveGaming.App/Services/IAuthApiService.cs:15`, `AuthApiService.cs:43`; `docs/plans/add-forgot-password/add-forgot-password-plan.md:44-47` |
| D4 | `IHidDeviceDiscoveryService.RefreshConnectivity()` always returns `false` and has no callers. | `src/ProoveGaming.Infrastructure/Interfaces/IHidDeviceDiscoveryService.cs:16`, `Services/HidDeviceDiscoveryService.cs:111-114` |
| D5 | Unused packages ship in the payload. `HidApi.Net` and `Tmds.DBus` (a Linux D-Bus client) are referenced by Infrastructure, and no source file uses either. `AvaloniaUI.DiagnosticsSupport` ships in Release, although the comment above it claims a condition removes it (there is none, and `AttachDeveloperTools` is `#if DEBUG`). | `src/ProoveGaming.Infrastructure/ProoveGaming.Infrastructure.csproj:17,21`, `src/ProoveGaming.App/ProoveGaming.App.csproj:34-35` |

### Architecture and pattern compliance

| ID | Finding | Evidence |
|----|---------|----------|
| A1 | **Layering.** 18 App-only keyboard catalog, layout and geometry classes live in `ProoveGaming.Common`, which CLAUDE.md defines as UI plumbing shared between App and Installer. Their only consumers are `ProoveGaming.App` and its tests, yet they ship inside the installer and uninstaller. | `src/ProoveGaming.Common/Classes/{Aress,Cyrex,Rampage,Raster,T800,TRacer}*.cs`, `SmartInfinite65Geometry.cs` |
| A2 | **Duplication between Installer and Uninstaller.** The same constants are declared twice under different names (`UninstallArgument` vs `UninstallerCommandArgument`, `DeviceHostServiceName`, `UninstallRegistryKeyPath`, exe names), and two near-identical `RunScCommandAsync` implementations exist. Both read stdout to the end *before* stderr, which can deadlock if stderr fills its pipe buffer. CLAUDE.md requires shared constants in Common. | `src/ProoveGaming.Installer/InstallerConstants.cs`, `src/ProoveGaming.Uninstaller/UninstallerConstants.cs`, `MainWindow.Installation.cs:414-440`, `UninstallService.cs:246-290` |
| A3 | **The App has no persistent logs in Release.** The only provider is `AddDebug()`, so every `_logger.LogWarning/LogError` in the App (sync aborts, host-absent, apply failures) goes nowhere on a user's machine. This matches api-needs #8 ("exceptions vanish in Release"). | `src/ProoveGaming.App/App.axaml.cs:203` |
| A4 | **Sync SQLite on the UI thread (residue of `ui-thread-storage-async`).** `WelcomeViewModel` (`GetLastSignIn`, `GetLockoutUntil`, `SaveLastSignIn`, `SaveLockoutUntil`, `ClearLockout`), `MainWindow.axaml.cs` (`GetLockoutUntil`, `SaveAuth`, `ClearSavedAuth`, `InitializeSession`), and `HomeViewModel` (`OnDeviceAdded`→`AddDevice`, `PersistDeviceCard`→`AddDevice`, `ExecuteConfirmDelete`→`DeleteDevice`) call the synchronous storage API directly, each of which takes `_lock.Wait()`. | `WelcomeViewModel.cs:37,45,60,247,445,484`, `MainWindow.axaml.cs:93,470,618,668,787`, `HomeViewModel.cs:2569,2637,2808`, `LocalStorageService.cs:109-113` |
| A5 | **Auth and session flow lives in `MainWindow` code-behind.** That is 1,151 lines, with five `async void` handlers doing login-after-signup, logout and token persistence. None of it is testable, and it violates the MVVM layout in CLAUDE.md. | `src/ProoveGaming.App/Views/MainWindow.axaml.cs:446,650,709,745,787,838,888` |
| A5 · note | *2026-09-24:* implemented by [`refactor-auth-session-flow`](../refactor-auth-session-flow/refactor-auth-session-flow-plan.md). The state transitions now live in `Services/AuthSessionCoordinator.cs`, `MainWindow` keeps the navigation, and `AuthSessionCoordinatorTests` pins the orderings. | — |
| A6 | **God classes** (recorded only, see Scope). `LocalStorageService.cs` has 5,763 lines, `HomeViewModel.cs` 2,867, `BluetoothDeviceDiscoveryService.cs` 2,379. | line counts |
| A7 | **Structured logging.** 36 log calls use interpolated strings (`$"…"`), which defeats Serilog message templates and allocates even when the level is off: 28 in `DeviceService.cs`, 4 in `HidDeviceDiscoveryService.cs`, 3 in `BluetoothDeviceDiscoveryService.cs`, 1 in `DeviceHost/Program.cs`. `DeviceService` also null-conditionals (`_logger?.`) a logger that DI guarantees is non-null. | `git grep -nE 'Log\w+\(\s*(ex,\s*)?\$"'` |
| A8 | **Localization violations.** Hardcoded English fallbacks reach the UI (`"Network error. Please check your connection."`, `"Server error. Please try again."`, `"Failed to update profile."`), as do `ApiHttpClient`'s English `Message` strings. The literal `Text="Desktop"` in the Save Preset dialog is never translated. | `WelcomeViewModel.cs:253,297`, `ProfileViewModel.cs:852,863`, `ApiHttpClient.cs:71,79,116`, `src/ProoveGaming.App/Views/DeviceDetails/Dialogs/SavePresetDialog.axaml:182` |
| A9 | **Cancellation is misreported.** `ApiHttpClient.SendAsync` catches every `TaskCanceledException`, including the *caller's* cancellation, and returns `IsNetworkError = true` / "Request timed out". A cancelled sync pass or navigation therefore looks like a network failure. `UpdateCheckService.cs:113-117` in the same codebase already uses the correct `when (ct.IsCancellationRequested) throw;` pattern. | `src/ProoveGaming.App/Services/ApiHttpClient.cs:69-72` |
| A10 | **Magic endpoint in three places.** `http://localhost:5000` is hardcoded in the App config, the Host config and a `const` in `ProoveGaming.DeviceHost.Checker`. They drift silently. This is resolved together with S8. | `src/ProoveGaming.App/appsettings.json`, `src/ProoveGaming.DeviceHost/appsettings.json`, `src/ProoveGaming.DeviceHost.Checker/Program.cs:9` |
| A11 | **Update check has no action.** `download_url` is required by `TryParseManifest` but never used. The announcement tells the user an update exists and gives no way to get it. Nothing validates the URL scheme either, which matters the moment someone wires it to a launcher. | `src/ProoveGaming.App/Services/UpdateCheckService.cs:141-149`, `Models/AppUpdateInfo.cs:13-14` |
| A12 | **No central package management.** Avalonia `12.1.0` is repeated in 4 csproj files, and `Microsoft.Toolkit.Uwp.Notifications 7.1.3` is an archived package. `System.Drawing.Common` is pinned in the App with no comment explaining why (it overrides a vulnerable transitive version from the toolkit, so it must stay). | `src/**/*.csproj` |
| A14 | **README drift.** The root `README.md` omits `ProoveGaming.Device.Testing`, `ProoveGaming.DeviceHost.Checker` and all three test projects. It has no test commands, no description of the App ↔ DeviceHost (gRPC) ↔ backend split, and no log locations for support. Its "Verification" section is a stale one-off ("Build succeeds (warnings only)"). Once Phase 1 lands, its "Installer features" section will also be wrong: registration becomes machine-wide and install folders get protected permissions. `src/README.md`, `tests/README.md` and `docs/README.md` are still template placeholders pointing at folders that do not exist (`src/Proove-Gaming/`, `tests/Proove-Gaming.Tests/`, `docs/architecture.md`). | `README.md`, `src/README.md`, `tests/README.md`, `docs/README.md` |
| A13 | **CLAUDE.md drift.** It lists two test projects (there are three, including `ProoveGaming.Installer.Tests`). "Release path" says CI "only builds+tests the App csproj", which contradicts its own CI note and `dotnet-ci.yml`. `ProoveGaming.Device.Testing` and `ProoveGaming.DeviceHost.Checker` are in the `.slnx` but undocumented. | `CLAUDE.md` |

Reviewed and **not** findings: `async void` handlers in `DeviceService.cs:1160-1215` (all wrapped in try/catch); the PowerShell and cmd escaping in `BuildSelfDeleteCommand` (correct `'`→`''` and base64 `-EncodedCommand`); the quoted service `binPath` (no unquoted-path vulnerability); `RestoreSharedRuntime` (already rejects non-bare file names); `GetDeviceCatalogEntry` (used by `DeviceCatalogResolver`); `ProtectedData`/`TypeNameHandling`/`BinaryFormatter` (not used); the HTTPS base URL (no certificate-validation override anywhere).

---

## Options considered

**1. One big-bang PR for everything.** It lands every finding at once. But it mixes installer changes that affect the release process with App refactors, a gRPC transport change and dead-code deletions that touch 22 test fakes. It cannot be reviewed well and cannot be bisected, and one blocked decision (S8) would hold back the H-severity fixes.

**2. Phased workstreams inside one plan, ordered by risk (recommended).** Each phase is independently shippable and has its own acceptance criteria. Phase 1 fixes the elevated installer and uninstaller trust boundary, Phase 2 the App and host security items, Phase 3 the dead code, Phase 4 the patterns, and Phase 5 holds the items that need decisions. One plan keeps a single findings register, so `/reviewer` has one contract to check against.

**3. One sibling plan folder per finding.** This gives maximum isolation, but the 20+ tiny plans would each restate context, and cross-cutting fixes (S1+S2+A2 all touch `UninstallService` and `InstallerConstants`) would conflict.

For each individual design choice:
- **S1/S2.** (a) *Validate the HKCU path.* The value still comes from a writable source, so validation becomes the only line of defence. (b) *Move to HKLM and add an [[Install signature]] check (recommended).* This removes the cause, because HKLM is admin-only, and deletes the SID-forwarding registry machinery. The signature check is defence in depth against stale or legacy values.
- **S3.** (a) *Restrict installs to Program Files.* Simple, but it removes a user-facing feature. (b) *Apply a protected ACL to the install root after copy (recommended).* Administrators and SYSTEM get full control, Users get read and execute, and inheritance is broken. Every install location becomes as safe as Program Files.
- **S4.** (a) *Extract directly into the install folder.* That breaks the existing rollback, which depends on a staging copy. (b) *Create the staging folder with an admin-only ACL before extracting (recommended).* It keeps the flow unchanged.
- **S7.** (a) *DPAPI CurrentUser via `ProtectedData` (recommended)*, bound to the Windows user and transparent to the user. (b) *Windows Credential Manager.* Heavier P/Invoke for the same guarantee.
- **S8.** (a) *gRPC over a named pipe with a `PipeSecurity` ACL* (Kestrel `ListenNamedPipe`, .NET 8+). It fixes authentication, port squatting and A10 together, but it is a transport change on both sides. (b) *Keep TCP and add a per-boot secret in an ACL'd file.* It stops other users but not same-user processes, and it adds machinery. (c) *Accept and document.* This is a Phase 5 decision; option (a) is recommended.

## Recommended approach

Option 2. The phases are ordered by severity, and each phase is one PR. Within a phase, reproduction and characterization tests land before the change.

**Phase 1: elevated installer and uninstaller trust boundary** (S1, S2, S3, S4, S5, S11, S13, plus A2 because it rewrites the same code). ⚠ **Release-process impact:** it changes the uninstall registration hive, the service log path and the install-folder ACL.
1. **A2 first**, as a pure refactor. Move the shared constants into `src/ProoveGaming.Common/Classes/InstallLayout.cs` (exe names, host folder, service name, registry key path, CLI argument names) and the one `sc.exe` runner into `src/ProoveGaming.Common/Classes/ServiceControl.cs`. The runner reads stdout and stderr concurrently, starts `%SystemRoot%\System32\sc.exe` by absolute path (fixing S5 for `sc`), and returns the exit code. Installer and Uninstaller keep their own tolerated-code policies as parameters.
2. **S5 (rest).** The PowerShell and cmd self-delete processes start from `Environment.SystemDirectory` (`WindowsPowerShell\v1.0\powershell.exe`, `cmd.exe`) through one helper, `SystemTool.PathOf(string exe)`, in the same Common file.
3. **S2.** The installer writes the uninstall entry to `HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\ProoveGaming` (64-bit view) and deletes any legacy HKCU entry it finds. The uninstaller reads HKLM only. The `--user-sid` HKU lookup and HKU delete are removed, while the forwarded `--user-local-appdata`/`--user-desktop`/`--user-programs` arguments stay, because user data and shortcuts really are per-user. Legacy installs are the one fallback: if HKLM has no entry, the uninstaller may use the running EXE's own folder (`TryGetInstallPathFromCurrentExecutable`), which is trustworthy because an admin installed it there. It never uses the HKCU value.
4. **S1.** *(Rule revised 2026-09-24 after review round 1, B1.)* `InstallSignature.IsProoveGamingInstall(string path)` (Common) returns true when the path is an acceptable location (fully qualified; not a drive root; not `%WINDIR%`, `%ProgramFiles%`, `%ProgramFiles(x86)%`, a user-profile root or `%ProgramData%` themselves), is not a reparse point, and passes a **provenance** test: the folder, and at least one marker file in it (`ProoveGaming.App.exe`, `ProoveGaming.Uninstaller.exe`, or `host\ProoveGaming.DeviceHost.exe` with `host\` not a link), are owned by Administrators, SYSTEM or TrustedInstaller (`InstallSignature.IsOwnedByTrustedPrincipal`), **or** sit inside `%ProgramFiles%` / `%ProgramFiles(x86)%`, where only administrators can write (`IsInsideProgramFiles`). A standard user cannot create a folder or file with that owner, and a copy of one of our EXEs they make is owned by them. The location half covers installs made by older builds with UAC disabled, whose files are owned by the installing account. One marker suffices on purpose: an AV quarantine, an unfinished self-delete or an interrupted uninstall leaves a damaged install that must stay repairable (reinstall) and removable (uninstall). The original rule (\"both EXEs present\") stranded those installs. `UninstallService.ResolveVerifiedInstallPath` combines resolution and this check; `RunAsync` touches nothing until it returns a folder. A command-line `--install-path` is only a hint that must *equal* (after normalisation) the HKLM value or the current EXE's folder. On refusal `RunAsync` returns `RefusedExitCode` (2), logs the reason, and the uninstaller shows a localized \"Nothing Was Removed\" screen telling the user to reinstall, then uninstall.
5. **S3.** Before the copy (so no payload file ever sits in a writable folder), the installer applies a protected DACL to the install root: `BUILTIN\Administrators` and `SYSTEM` full control, `BUILTIN\Users` read and execute, inheritance from the parent disabled, and everything inheritable so the copied files pick it up. The owner becomes Administrators, because an owner can always rewrite a DACL. It uses `FileSystemAclExtensions.SetAccessControl` (in-box, no new package). *(Added during implementation, 2026-09-24.)* The folder-step validation (`MainWindow.GetInstallLocationErrorKey`) now also requires the install folder to be new, empty, or an existing Proove Gaming install, and never a drive root or a system or profile folder (`InstallSignature.IsAcceptableInstallLocation`). Locking down a typed path such as the user's Desktop would break it, and files already in a foreign folder would load into the service. New en/uk keys: `Message_InstallPathNotAllowed`, `Message_InstallFolderNotEmpty`.
6. **S4.** `ExtractEmbeddedPayloadAsync` extracts into a fresh `%ProgramData%\ProoveGamingInstaller.<guid>`, created with the admin-only DACL in place (`InstallAcl.CreateProtectedDirectory`, i.e. `FileSystemAclExtensions.Create`, not ACL'd afterwards). It sits *directly* under `%ProgramData%`: under a user-writable parent (including `%TEMP%` or a shared `ProoveGamingInstaller\` parent a user could pre-create), the parent's owner could rename the protected folder away and substitute their own. `CleanupStagingDirectoryAsync` deletes only a folder with that prefix directly under `%ProgramData%`.
7. **S11.** The PowerShell loop clears attributes with a recursive `Clear-Attributes` function that skips reparse points, and removes the folder with `[System.IO.Directory]::Delete($path, $true)`, which deletes a junction as a link. `Remove-Item -Recurse` and `Get-ChildItem -Recurse` are gone from the script. *(Extended during implementation, 2026-09-24.)* The C# delete walk (`UninstallService.TryDeleteDirectoryContents`, used for the install folder, `host\` and the user-data folders) no longer enumerates with `SearchOption.AllDirectories`, which also follows junctions. It walks the tree by hand and removes links as links. This matters most for `%LocalAppData%\ProoveGaming`, which a standard user can always write to. The redundant second delete pass in `RemoveInstallDirectory` was removed. *(Review round 3, B1.)* The walk also refuses the folder it is **given** when that folder is itself a link: listing a junction lists its target, so a `%LocalAppData%\ProoveGaming` replaced by a junction would otherwise have had its target emptied. The link is then removed as a link. `%ProgramData%\ProoveGaming`, which nothing writes after Phase 3, is emptied only when it is owned by Administrators or SYSTEM, as the old DeviceHost left it; a folder of that name owned by anyone else is left in place and logged.
8. **S13.** *(Changed during implementation, 2026-09-24; supersedes the `%ProgramData%\ProoveGaming\Logs` design.)* Serilog's path becomes the relative `logs/log.txt`, and the DeviceHost's `Program.cs` sets its working directory to `AppContext.BaseDirectory` first (a Windows service starts in `System32`). The log therefore lands in `<install>\host\logs\`, inside the protected install folder, where no standard user can pre-create a folder or plant a link. A fixed `%ProgramData%\ProoveGaming` path can be pre-created by any user, and re-owning it afterwards would still leave that user's planted children inside, so it would have needed extra machinery for a weaker result. The uninstaller removes the logs with the install folder. The audits that cite `%TEMP%/Logs` get a dated note only.
9. **A14 (installer part).** In `README.md` "Installer features", state that uninstall registration is machine-wide (visible to every user in Settings → Apps), that the install folder gets admin-only write permissions, and that the uninstaller refuses to delete a folder that is not a Proove Gaming install. Add a short "Logs" section giving the DeviceHost log path.

**Phase 2: App and host security** (S6, S7, S9, S10, A8, A9).
1. **S6.** `ConfigurationManager` uses `AppContext.BaseDirectory` and adds user-secrets only `#if DEBUG`. The `UserSecretsId` and `UserSecrets` package move into a Debug-only `ItemGroup`/`PropertyGroup`.
2. **S7.** *(Storage shape changed during implementation, 2026-09-24.)* `LocalStorageService.SaveAuth` stores `dpapi1:` + base64 of `ProtectedData.Protect(UTF8(token), entropy: \"ProoveGaming.saved_auth.v1\", CurrentUser)` in the **existing** `token` column; there is no new column and no schema migration. `GetSavedAuth` decrypts a prefixed value. A value without the prefix is legacy plaintext from an older build: it is returned and rewritten protected in the same call. A value that no longer decrypts (profile restored on another machine) is treated as \"no saved auth\" and the row is cleared. The plan's original shape (a `token_protected` column plus blanking `token`) was dropped: the prefix gives the same guarantee without a second column whose two states must be kept consistent. *(Review round 2, B1.)* If DPAPI itself fails (for example the profile's master key is unavailable), `SaveAuth` does not persist the sign-in and clears any earlier saved one, so a previous account can never be restored in its place. A legacy row whose upgrade fails is returned as is and retried on the next read. Neither path throws: startup and sign-in must never fail over "Remember me". ⚠ **New NuGet:** `System.Security.Cryptography.ProtectedData` 10.0.10 in `ProoveGaming.App`.
3. **A9.** `ApiHttpClient.SendAsync` adds `catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }` before the timeout mapping. Every caller that passes a real token was enumerated. The sync push, pull, pass and notification poll already contain cancellation (catch-alls; the poll's \"timed out\" log now actually fires). **Contact Us** relied on \"ContactUsAsync never throws\" for its own 15 s linked timeout, so `HomeViewModel.SendContactUsAsync` now maps an `OperationCanceledException` whose own token is not cancelled back to the same Failed outcome (`ClientErrorCodes.Timeout`).
4. **S10 + A8.** `ApiHttpClient` stops putting English or exception text into `Message` for transport failures. It sets `ErrorCode` to client-side codes in a new `ClientErrorCodes` class (`CLIENT_NETWORK_ERROR`, `CLIENT_TIMEOUT`, `CLIENT_INVALID_RESPONSE`), kept out of `AuthErrorCodes`, which is the server's catalog and is pinned against the spec. A new `ApiErrorMessages.Describe(response, fallbackKey)` is the one rule for text no field message covers: rate limit, then client code, then the caller's localized fallback. The server's `message` is never shown. Every display site uses it: the plan's list, plus the registration toast in `MainWindow.axaml.cs`, which the audit had missed. `SavePresetDialog.axaml:182` reuses the existing `Presets_Item_App` key (the same \"Desktop\" label the Presets section shows) instead of a new key. New en/uk keys: `Api_Error_Network`, `Api_Error_Timeout`, `Api_Error_Generic`, `ProfileView_UpdateProfile_Error_Generic`, `Auth_Error_RegistrationFailed`. Behaviour superseded: `fix-profile-and-auth-validation` (review M2) had an unmapped server code show the server's text inline. It is still shown inline, now as the localized generic message; its test was updated accordingly.
5. **S9.** `src/ProoveGaming.DeviceHost/Helpers/ProtoInput.cs` (internal, pure, so it is testable without a device) clamps key codes to a byte and delays to a non-negative int, rejects the whole request on an undefined button, action or repeat mode (a warning log names the value, and the response is `Success = false`), and rejects a macro longer than 256 events. That ceiling sits far above any real macro (the keyboard encoders keep 60) and only bounds what one request can make the host allocate. `SetMacros`, `SetButtonBindings` and `ApplyDeviceConfiguration` all route through it; the old unclamped `MapMacro` is deleted.

**Phase 3: dead code and dependencies** (D1–D5).
- D1: delete `ButtonBindingStore.cs` and its registration, remove the `GetButtonBindings` RPC and its messages from the proto (mark the removed field numbers `reserved` if any message is kept), remove `GetButtonBindingsAsync` from `IUsbHidDeviceService`, `UsbHidDeviceService` and every test fake. The uninstaller keeps deleting `%ProgramData%\ProoveGaming`, which covers legacy `button-bindings.json`.
- D2: remove `SetMouseSettingsAsync` (App), the `SetMouseSettings` RPC (host and proto) and test-fake members. *(Extended during implementation, 2026-09-24.)* The RPC was the only caller of `IMouseRgbService.SetMouseParamAsync`, so that interface member and its six implementations (Easen and Telink mice; stubs on the Aress, Cyrex, Raster and T-Racer keyboards) go too. Nothing cascades: `TaipanMsConfig.PatchLiftOffDistance` is still used by TAIPAN's unified apply, and Easen's read-before-write lives in `ApplyConfigurationAsync`, which is what the factory comment now names.
- D3: remove `ResetPasswordAsync` from `IAuthApiService`, `AuthApiService` and fakes.
- D4: remove `RefreshConnectivity` from the interface and implementation.
- D5: remove the `HidApi.Net` and `Tmds.DBus` references (no package depends on either; checked in `project.assets.json`). Put `AvaloniaUI.DiagnosticsSupport` under `Condition=\"'$(Configuration)' == 'Debug'\"`; its only use, `AttachDeveloperTools`, is already `#if DEBUG`. Not to be confused with `Tmds.DBus.Protocol.dll`, which remains in the App output: it is a dependency of Avalonia's own `Avalonia.FreeDesktop` (Linux desktop integration) and not ours to remove.

**Phase 4: patterns** (A1, A3, A4, A7, A11-scheme, A12-comment, A13).
- A1: move the 18 keyboard catalog, layout and geometry files from `ProoveGaming.Common/Classes/` to `src/ProoveGaming.App/Models/Keyboards/` (namespace `ProoveGaming.App.Models.Keyboards`), with `git mv` so history follows, and update usings in App and tests. This is a pure move with no logic change. *(2026-09-24.)* Three partially qualified references (`Common.Classes.XKeyCatalog` in `DeviceCardViewModel`) became `Models.Keyboards.…`. The four generator scripts under `docs/plans/` (`measure-rampage-artwork.py`, `decode-t800-matrix.py`, `measure-t800-keycaps.py`, `split-tracer-artwork.py`) write these files, so their output paths **and emitted namespace** were updated too. Run unchanged, they would have regenerated a duplicate type in Common. Live plan prose was repointed; review journals keep the old paths as history.
- A3: add file logging to the App at `%LocalAppData%\ProoveGaming\Logs\app-<date>.txt`, rolling daily, 7 files, 10 MB cap, using the same Serilog stack as the host, alongside the existing `AddDebug`. `buffered: true` with a 1 s `flushToDiskInterval`, so a log call on the UI thread appends to memory rather than waiting on the disk. Registered with `dispose: true`, so disposing the container on exit flushes the tail. Minimum level Information in Release, Debug in Debug builds. ⚠ **New NuGet** for the App: `Serilog.Extensions.Logging` 10.0.0, `Serilog.Sinks.File` 7.0.0 (the host's versions). No tokens, passwords or emails are logged; verified by searching every App log call and template placeholder.
- A4: *(Partly done, 2026-09-24; the rest is scoped as A4b below.)* Writes that have no result dependency now run on the storage executor: `WelcomeViewModel` (`SaveLastSignIn`, `SaveLockoutUntil`, both `ClearLockout`s), `MainWindow` (`SaveAuth` via `PersistAuthIfTokenPresent`, both `ClearSavedAuth`s, `InitializeSession`), and `HomeViewModel.ExecuteConfirmDelete` (`DeleteDevice`). Values are captured at enqueue time, and FIFO order plus the drain on exit preserve the old ordering. **Kept synchronous on purpose:** the constructor reads `WelcomeViewModel` (`GetLastSignIn`, `GetLockoutUntil`) and `MainWindow` (`GetLockoutUntil`). They decide the first screen; read late, a sign-in could start before the lockout applied. Add a dated note to `ui-thread-storage-async`.\n- A4b *(deferred, needs its own plan)*: `HomeViewModel.PersistDeviceCard` → `AddDevice` runs on the UI thread when called from `ReloadOwnerScopedState` (sign-in); from device discovery it is already off the UI thread. `ReloadOwnerScopedState` also reads `GetDevices` synchronously. The returned row id becomes `card.StorageId`, which later deletes and battery-band writes read, and it sits inside the adopt/revive logic that `fix-owner-scoped-state-persistence` had to get exactly right. Making those writes asynchronous changes **when** `StorageId` becomes valid, so it needs a design, not a swap.\n- D6 *(found during A4; dead code, not yet removed)*: `AddDeviceCardViewModel.DeviceAdded` is never raised (the compiler already warns, CS0067), so `HomeViewModel.OnDeviceAdded`, which the audit listed under A4, is unreachable. Remove the event, its subscription and the handler in a follow-up.
- A7: convert the interpolated log calls to message templates; after Phase 3 there were 34, all converted. Drop `?.` on the DI-injected logger in `DeviceService`. The one `MethodBase.GetCurrentMethod()` site, inside async `SubscribeOnDevices`, logged the compiler-generated `MoveNext`; it now names the method via `nameof`, and the unused `using System.Reflection` went with it.
- A11: `TryParseManifest` also rejects a non-`https` `download_url`. Wiring the action is Phase 5.
- A12: add a one-line comment above the `System.Drawing.Common` pin explaining why it exists.
- A13: fix the three CLAUDE.md drifts and document `Device.Testing` and `DeviceHost.Checker` in \"Repo layout\" (verified not part of the installer payload). The `/reviewer` skill (`.claude/skills/reviewer/SKILL.md`) repeated the same wrong claim that CI runs no `tests/` projects, and was corrected too.
- A14 (rest): bring `README.md` up to date. The project list gains `Device.Testing` (hardware probe console, not shipped) and `DeviceHost.Checker` (gRPC reachability console, not shipped). Add a three-line architecture summary (App → DeviceHost over gRPC; App → backend HTTPS API; Installer/Uninstaller). Add test commands (`dotnet test ./src/ProveGaming.slnx`, plus the single-project and `--filter` forms) and the three test projects. Add the App log path (A3) to the "Logs" section. Replace "Verification" with a pointer to CI, and state that CI runs the full suite in Release. Rewrite `src/README.md`, `tests/README.md` and `docs/README.md` as short, accurate indexes: the projects in `src/`; the test projects and the rule that tests live in `tests/`; the `docs/` subfolders (`plans/`, `audits/`, `knowledge-base/`, `mouse/`, `headset/`, `support/`, `CONTEXT.md`). Keep them in English, with no duplication of CLAUDE.md beyond a pointer.

**Phase 5: decisions** *(2026-09-24)*. S8 + A10 → [`migrate-devicehost-named-pipe`](../migrate-devicehost-named-pipe/migrate-devicehost-named-pipe-plan.md). A5 → [`refactor-auth-session-flow`](../refactor-auth-session-flow/refactor-auth-session-flow-plan.md). A11: leave as is. A4b and D6: the requester chose to leave the add-device code as it is, with comments at `AddDeviceCardViewModel.DeviceAdded`, `HomeViewModel.OnDeviceAdded` and the `AddDevice` call in `HomeViewModel.PersistDeviceCard` saying it must be revisited. S12 code signing: deferred on 2026-09-24, because a certificate and signing tool are coming but are not available yet. A12 central package management: declined on 2026-09-24, because the solution is an MVP in one repository and does not need it yet.

---

## File-level change list

Ordered as `/coder` should apply them. `T:` marks test files, which land before the change they pin.

### Phase 1
- T: `tests/ProoveGaming.Installer.Tests/InstallSignatureTests.cs`: **new**. Reproduces S1 (not-ours folders rejected: user-owned, marker copied in by a user, no marker, drive root, junction, `%WINDIR%`), accepts complete *and* damaged installs (review round 1 B1), and pins the real owner check without depending on elevation.
- T: `tests/ProoveGaming.Installer.Tests/UninstallPathResolutionTests.cs`: **new**. a requested path that names neither trusted source is ignored; HKCU is never consulted. The seam is the pure `UninstallService.ResolveInstallPath(requested, registered, executableFolder)`, so no test touches the real registry or service.
- T: `tests/ProoveGaming.Installer.Tests/ServiceControlTests.cs`: **new**. `SystemTool.PathOf("sc.exe")` is rooted at `Environment.SystemDirectory`; the runner drains stderr larger than 64 KB without hanging (driven through `cmd.exe /c` echo loops).
- T: `tests/ProoveGaming.Installer.Tests/UninstallHandoffTests.cs`: **edit**. The self-delete command contains the reparse-point guard and a rooted `powershell.exe`.
- `src/ProoveGaming.Common/Classes/InstallLayout.cs`: **new**. Shared install constants and CLI argument names (A2).
- `src/ProoveGaming.Common/Classes/ServiceControl.cs`: **new**. `SystemTool.PathOf`, a single `sc.exe` runner with concurrent stream reads (A2, S5).
- `src/ProoveGaming.Common/Classes/InstallSignature.cs`: **new**. `IsProoveGamingInstall` (provenance plus one marker), `IsAcceptableInstallLocation`, `IsOwnedByTrustedPrincipal` (S1, review round 1 B1).\n- `src/ProoveGaming.Uninstaller/Views/CompleteStepView.axaml(.cs)`, `MainWindow.axaml.cs`, `Resources/Strings.en.axaml`, `Strings.uk.axaml`: **edit**. The refused outcome and its en/uk message; the message block now wraps (review round 1 S2).\n- T: `tests/ProoveGaming.Installer.Tests/UninstallDeleteWalkTests.cs`, `StagingSweepTests.cs`: **new**. Junction-safe delete walk; which abandoned staging folders are swept (review round 1 Tests, S1).
- `src/ProoveGaming.Common/Classes/InstallAcl.cs`: **new**. Builds the protected DACL; `CreateProtectedDirectory` (staging) and `ProtectExistingDirectory` (install root) (S3, S4); `MakeAdministratorsOwner`, which the installer applies to the three marker files after the copy so the uninstaller's provenance check holds even with UAC disabled (review round 1 B1).
- `src/ProoveGaming.Installer/InstallerConstants.cs`: **edit**. Keep only installer-specific constants and reference `InstallLayout` for the rest; the registry path becomes the HKLM key.
- `src/ProoveGaming.Uninstaller/UninstallerConstants.cs`: **edit**. Same as above; drop `UserSidArgument`.
- `src/ProoveGaming.Installer/Views/MainWindow.Installation.cs`: **edit**. Use `ServiceControl`; write the uninstall entry to HKLM and delete the legacy HKCU entry; ACL the install root before the copy; create the staging folder protected, directly under `%ProgramData%`; delete the local `RunScCommandAsync`.
- `src/ProoveGaming.Uninstaller/Program.cs`: **edit**. Stop forwarding `--user-sid`; keep the other forwarded folders. (The Installer's `Program.cs` never forwarded it and is unchanged.)
- `src/ProoveGaming.Uninstaller/UninstallService.cs`: **edit**. Trusted-path resolution (HKLM, then the EXE folder; args only as a matching hint); the signature gate before any delete; remove the HKU lookup and delete; rooted `powershell.exe`/`cmd.exe`; reparse guard in `BuildSelfDeleteCommand`; delete the local `RunScCommandAsync`.
- `src/ProoveGaming.DeviceHost/appsettings.json`: **edit**. Serilog path `logs/log.txt` (relative; resolves to `<install>\host\logs`) (S13).
- `src/ProoveGaming.DeviceHost/Program.cs`: **edit**. `Directory.SetCurrentDirectory(AppContext.BaseDirectory)` before the host is built, so relative paths resolve against the host folder instead of `System32` (S13).
- `src/ProoveGaming.Installer/Views/MainWindow.axaml.cs`, `src/ProoveGaming.Installer/Resources/Strings.en.axaml`, `Strings.uk.axaml`: **edit**. Install-location validation and its two messages (S3).
- T: `tests/ProoveGaming.Installer.Tests/InstallAclTests.cs`, `InstallLocationValidationTests.cs`, `TestFolders.cs`: **new**. DACL shape, folder rules, shared disk fixtures (junctions via `mklink /J`, ACL-safe cleanup).
- `docs/audits/2026-09-02-device-association-memory-audit.md`, `docs/audits/2026-09-08-das-dongle-incident.md`: **edit**. Append a dated note only: log path moved to `<install>\host\logs` on 2026-09-24. The original lines stay untouched.
- `README.md`: **edit**. "Installer features" reflects machine-wide registration, protected install-folder permissions and the uninstaller's install-signature refusal; new "Logs" section with the DeviceHost log path (A14, installer part).
- `src/.github/copilot-instructions.md`: **read-check only**. It has installer UI specs; confirm nothing there references HKCU or the log path, and edit only if it does.

### Phase 2
- T: `tests/ProoveGaming.App.Tests/ConfigurationManagerTests.cs`: **new**. With the working directory set to an empty temp folder, `ConfigurationManager` still loads the App's own `appsettings.json` (a non-parallel xUnit collection, because the working directory is process-global).
- T: `tests/ProoveGaming.App.Tests/LocalStorageServiceSessionTests.cs`: **edit**. On a temp-file database: `SaveAuth` leaves no plaintext token on disk (raw SQLite read); round-trip works; a legacy plaintext row still signs in and is rewritten protected; an undecryptable value yields null and clears the row.
- T: `tests/ProoveGaming.App.Tests/ApiHttpClientCancellationTests.cs`: **new**. Caller-cancelled requests throw `OperationCanceledException`; a handler timeout without caller cancellation still yields `IsNetworkError` with `ErrorCode = TIMEOUT`; `HttpRequestException` yields `NETWORK_ERROR` with no exception text in `Message`.
- T: `tests/ProoveGaming.App.Tests/ApiErrorMessagesDescribeTests.cs`: **new**. Each client code maps to its key, rate limit wins, and the server's message is never returned. Plus `ProfileViewModelEditNicknameTests.cs`: **edit**. The network and unmapped-server-code cases now assert localized keys.
- T: `tests/ProoveGaming.Infrastructure.Tests/ProtoInputTests.cs`: **new**. Key code 300 is clamped (not wrapped) on the bindings path; an undefined button, action or mode rejects the request; the event ceiling holds at the boundary; repeat count 0 becomes 1; a huge delay stays positive.
- `src/ProoveGaming.App/ConfigurationManager.cs`: **edit**. `AppContext.BaseDirectory`; user-secrets under `#if DEBUG`.
- `src/ProoveGaming.App/ProoveGaming.App.csproj`: **edit**. Debug-only `UserSecretsId` and user-secrets package; add `System.Security.Cryptography.ProtectedData`.
- `src/ProoveGaming.App/Services/LocalStorageService.cs`: **edit**. DPAPI-protected, `dpapi1:`-prefixed token in the existing column; legacy plaintext is upgraded on read; an undecryptable value is cleared.
- `src/ProoveGaming.App/Services/ApiHttpClient.cs`: **edit**. Rethrow caller cancellation; client error codes instead of English or exception text.
- `src/ProoveGaming.App/Services/ApiHttpClient.cs` (new `ClientErrorCodes` class), `ApiErrorMessages.cs` (`Describe`): **edit**. `AuthErrorCodes.cs` is unchanged: it is the server's catalog.\n- `src/ProoveGaming.App/ViewModels/HomeViewModel.cs`: **edit**. Contact Us maps its own timeout's cancellation to the Failed outcome (A9).
- `src/ProoveGaming.App/ViewModels/WelcomeViewModel.cs`, `ProfileViewModel.cs`, `ChooseNicknameViewModel.cs`, `src/ProoveGaming.App/Views/MainWindow.axaml.cs`: **edit**. Replace hardcoded English and raw `result.Message` with `ApiErrorMessages.Describe`.
- `src/ProoveGaming.App/Views/DeviceDetails/Dialogs/SavePresetDialog.axaml`: **edit**. Bind the existing `Presets_Item_App` key.
- `src/ProoveGaming.App/Resources/Strings.en.axaml`, `Strings.uk.axaml`: **edit**. Add every new key in both.
- `src/ProoveGaming.DeviceHost/Helpers/ProtoInput.cs`: **new**; `src/ProoveGaming.DeviceHost/Services/DeviceService.cs`: **edit**. Route macros, bindings and apply through `ProtoInput`; delete `MapMacro`.

### Phase 3
- T: no new tests; the existing suites must stay green after deletion.
- `src/ProoveGaming.DeviceHost/Services/ButtonBindingStore.cs`: **delete**.
- `src/ProoveGaming.DeviceHost/Program.cs`: **edit**. Drop the `IButtonBindingStore` registration.
- `src/ProoveGaming.DeviceHost/Services/DeviceService.cs`: **edit**. Drop the store dependency and the `GetButtonBindings` and `SetMouseSettings` overrides.
- `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto`: **edit**. Remove `rpc GetButtonBindings`, `rpc SetMouseSettings` and their request/response messages if nothing else uses them. ⚠ gRPC contract change; App and Host ship together in one installer, so there is no version skew.
- `src/ProoveGaming.App/Services/IUsbHidDeviceService.cs`, `UsbHidDeviceService.cs`: **edit**. Remove `GetButtonBindingsAsync` and `SetMouseSettingsAsync`.
- `src/ProoveGaming.App/Services/IAuthApiService.cs`, `AuthApiService.cs`: **edit**. Remove `ResetPasswordAsync`.
- `src/ProoveGaming.Infrastructure/Interfaces/IMouseRgbService.cs`, `Services/{Easen,Telink}MouseService.cs`, `Services/{Aress,Cyrex,Raster,TRacer}KeyboardRgbService.cs`: **edit**. Remove `SetMouseParamAsync` (D2 extension). `Services/DeviceRgbServiceFactory.cs`: **edit**. Comment now names `ApplyConfigurationAsync`.
- `src/ProoveGaming.Uninstaller/UninstallService.cs`: **edit**, comment only. `%ProgramData%\ProoveGaming` is now described as legacy cleanup; the deletion itself is unchanged.
- `docs/plans/add-button-remapping/add-button-remapping-plan.md`, `docs/plans/pg-130-local-settings-persistence/pg-130-local-settings-persistence-plan.md`: **edit**. Dated supersession notes; their design text is left as the record.
- `src/ProoveGaming.Infrastructure/Interfaces/IHidDeviceDiscoveryService.cs`, `Services/HidDeviceDiscoveryService.cs`: **edit**. Remove `RefreshConnectivity`.
- `src/ProoveGaming.Infrastructure/ProoveGaming.Infrastructure.csproj`: **edit**. Remove `HidApi.Net` and `Tmds.DBus`.
- `src/ProoveGaming.App/ProoveGaming.App.csproj`: **edit**. Make `AvaloniaUI.DiagnosticsSupport` Debug-only.
- `tests/ProoveGaming.App.Tests/**`: **edit**. Remove the deleted members from every fake (the 22 files that reference `GetButtonBindingsAsync`, plus the `ResetPasswordAsync` and `SetMouseSettingsAsync` fakes, and `UsbHidDeviceServiceHostAbsentTests` cases for the removed RPCs).
- `tests/ProoveGaming.Infrastructure.Tests/**`: **edit**. Remove `RefreshConnectivity` from any fake discovery service.
- `docs/CONTEXT.md`: **edit**. Remove or adjust glossary lines that describe `ButtonBindingStore` or host-owned bindings, if any.

### Phase 4
- `src/ProoveGaming.Common/Classes/{Aress,Cyrex,Rampage,Raster,T800,TRacer}*.cs`, `SmartInfinite65Geometry.cs` → `src/ProoveGaming.App/Models/Keyboards/`: **move**, namespace update (A1).
- `src/ProoveGaming.App/**`, `tests/ProoveGaming.App.Tests/**`: **edit**. Update usings for the moved types.
- `src/ProoveGaming.App/App.axaml.cs`, `ProoveGaming.App.csproj`: **edit**. Serilog file sink (A3).
- `src/ProoveGaming.App/ViewModels/WelcomeViewModel.cs`, `Views/MainWindow.axaml.cs`, `ViewModels/HomeViewModel.cs`: **edit**. Move the listed sync storage calls off the UI thread (A4).
- T: `tests/ProoveGaming.App.Tests/*ThreadingTests.cs`: **edit or add**. Use the existing `OnBeforeStorageAccess` seam to assert that sign-in, lockout, device add and device delete never touch storage on the UI thread.
- `docs/plans/ui-thread-storage-async/ui-thread-storage-async-plan.md`: **edit**. Add a dated note linking A4.
- `src/ProoveGaming.DeviceHost/Services/DeviceService.cs`, `DeviceHost/Program.cs`, `Infrastructure/Services/HidDeviceDiscoveryService.cs`, `BluetoothDeviceDiscoveryService.cs`: **edit**. Message-template logging (A7).
- `src/ProoveGaming.App/Services/UpdateCheckService.cs` + T: `tests/ProoveGaming.App.Tests/UpdateCheckServiceTests.cs`: **edit**. Reject non-https `download_url` (A11).
- `src/ProoveGaming.App/ProoveGaming.App.csproj`: **edit**. Comment the `System.Drawing.Common` pin (A12).
- `CLAUDE.md`: **edit**. Test project list, release-path CI sentence, auxiliary projects (A13).
- `README.md`: **edit**. Full project list incl. `Device.Testing`/`DeviceHost.Checker`, architecture summary, test commands and the three test projects, App log path in "Logs", "Verification" replaced by a CI pointer (A14).
- `src/README.md`, `tests/README.md`, `docs/README.md`: **rewrite**. Replace the template placeholders with short, accurate indexes of what each folder actually contains (A14).
- `.github/workflows/dotnet-ci.yml`: **edit**. Add `permissions: contents: read` and a \"Vulnerable packages\" step after Test (which restores the whole slnx). It runs `dotnet list ./src/ProveGaming.slnx package --vulnerable --include-transitive --format json`, parses the JSON in pwsh (the text table is for people and changes between SDK versions), and fails on any High or Critical advisory (S12, non-signing part). Verified locally: 12 projects scanned, none vulnerable today.

### Glossary (this change)
- `docs/CONTEXT.md`: **edit**. Add [[Binary planting]], [[DPAPI]], [[Install signature]], [[Medium integrity]].

---

## Acceptance criteria

### Phase 1
- [ ] Given the folder the uninstaller resolves is not ours (user-owned, or containing none of our marker files as admin-owned files), when the uninstaller runs, then that folder and its contents are unchanged, no process is killed, no `sc.exe` or self-delete process is started, the exit code is `2`, `proove-uninstall.log` contains \"refusing to delete\", and the completion screen shows \"Nothing Was Removed\" (en and uk).\n- [ ] Given an install from which antivirus removed `ProoveGaming.App.exe` (or the DeviceHost EXE), when the user reinstalls into the same folder, then the folder step accepts it and the install completes; and when the user instead uninstalls, then the folder is removed.
- [ ] Given the uninstaller is launched with `--install-path C:\SomeOtherFolder` while HKLM names the real install, when it runs, then only the HKLM folder is touched.
- [ ] Given a path that is a drive root, `%WINDIR%`, `%ProgramFiles%`, a user-profile root, `%ProgramData%`, or a junction, when `InstallSignature.IsProoveGamingInstall` is called, then it returns `false`, even when the expected exe files are present behind the junction.
- [ ] Given a fresh install, when it completes, then `HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\ProoveGaming` exists with `InstallLocation`, `UninstallString` and `DisplayName`, and no `HKCU\…\Uninstall\ProoveGaming` key exists.
- [ ] Given a machine with a legacy HKCU entry, when the new installer runs, then the HKCU entry is deleted and the HKLM entry is written.
- [ ] Given an install into a custom folder under a parent that grants Authenticated Users Modify, when the install completes, then `icacls <install>` shows inheritance disabled, Administrators and SYSTEM with (F), Users with (RX), and no Authenticated Users write ACE, and the same holds for `host\`.
- [ ] Given the installer is extracting, when a standard-user process tries to create, overwrite or rename anything in `%ProgramData%\ProoveGamingInstaller.<guid>`, then it gets access denied.
- [ ] Given `sc.exe`, `powershell.exe` and `cmd.exe` files placed next to the installer and uninstaller EXEs, when an install or uninstall runs, then none of those planted files is executed (verified with Process Monitor: every process image path is under `C:\Windows\System32`).
- [ ] Given `sc.exe` writes more than 64 KB to stderr (test double), when `ServiceControl` runs it, then the call completes and returns the exit code (no deadlock).
- [ ] Given the DeviceHost service has run once, then its log is at `<install>\host\logs\log<date>.txt`, that folder grants Users read-only access, and nothing is written under `C:\Windows\Temp\Logs`.
- [ ] Given a folder that contains unrelated files, a drive root, or the user's Desktop is typed as the install folder, when the folder step validates it, then Install stays disabled and the localized not-empty / not-allowed message is shown (en and uk).
- [ ] Given a junction inside `%LocalAppData%\ProoveGaming` pointing at another folder, when the user uninstalls with delete-my-data ticked, then the junction is removed and the target folder's contents are untouched.
- [ ] `README.md` Installer features names machine-wide uninstall registration and protected install-folder permissions, and a Logs section gives `<install>\host\logs` for the DeviceHost.
- [ ] `InstallerConstants` and `UninstallerConstants` no longer declare any value that also exists in `InstallLayout`, and only one `sc.exe` runner exists in `src/`.

### Phase 2
- [ ] Given the App is started from a shell whose current directory contains a different `appsettings.json`, when it starts, then it uses the `appsettings.json` next to `ProoveGaming.App.exe` (verified by the sign-in request host).
- [ ] Given a Release build, then `ProoveGaming.App.dll` contains no `UserSecretsIdAttribute` and the publish output contains no `Microsoft.Extensions.Configuration.UserSecrets.dll`.
- [ ] Given a signed-in user with "Remember me", when the SQLite file is opened with any SQLite browser, then `saved_auth` contains no column whose value equals the bearer token, and restarting the App still restores the session.
- [ ] Given a database written by the previous version (plaintext `token`), when the new App starts, then the user stays signed in and, afterwards, the `token` column holds a `dpapi1:`-prefixed value that does not contain the token.
- [ ] Given an API call cancelled by its caller, then the call throws `OperationCanceledException`, and no \"Request timed out\" or network-error UI appears. Given the Contact Us send exceeds its own 15 s timeout, then the dialog shows its localized send-failed line, as before.
- [ ] Given the network is unplugged, when the user signs in, then the error shown is the localized network-error string in the current language (en or uk), with no English text in uk and no exception text.
- [ ] Given uk language, when the Save Preset dialog opens, then the \"Desktop\" label is shown in Ukrainian (\"Робочий стіл\").
- [ ] `LanguageResourceParityTests` passes.
- [ ] Given a gRPC `ApplyDeviceConfiguration` with `key_code = 300`, `button = 999`, or a macro with 10,000 events, then the response is `Success = false` (enum or count) or the key code is clamped to 255, and no out-of-range value reaches `IMouseRgbService`.

### Phase 3
- [ ] `git grep -nwE 'ButtonBindingStore|GetButtonBindingsAsync|SetMouseSettings|SetMouseSettingsAsync|SetMouseParamAsync|ResetPasswordAsync|RefreshConnectivity|HidApi|Tmds'` over `src/` and `tests/` returns no hits (`docs/` history excepted). `LocalStorageService.GetButtonBindings`, the App's live SQLite read, is a different member and stays.
- [ ] Given a fresh install and 10 minutes of use including button remaps, then `%ProgramData%\ProoveGaming\button-bindings.json` does not exist.
- [ ] Given a Release publish, then the payload contains no `HidApi.Net.dll`, `Tmds.DBus.dll` or Avalonia diagnostics assemblies (`Tmds.DBus.Protocol.dll` is Avalonia's own dependency and expected).
- [ ] Button remapping, presets and sign-in behave exactly as before (QA below).

### Phase 4
- [ ] `src/ProoveGaming.Common/Classes/` contains only `UninstallHandoff.cs` plus the Phase 1 installer classes, and the Installer publish no longer contains the keyboard catalog types (`AressKeyCatalog` is not found in `ProoveGaming.Common.dll`).
- [ ] Given a Release App run that hits a logged warning (for example with the DeviceHost stopped), then `%LocalAppData%\ProoveGaming\Logs\app-<date>.txt` contains that warning, and no file there contains the bearer token, a password or an email.
- [ ] Given the storage-access seam, when the user signs in, then no storage access happens inline during the sign-in call; the last-sign-in write lands after the executor runs (pinned by `WelcomeViewModelStorageThreadingTests`). Lockout, saved-auth and device-delete writes are queued the same way. Device *add* on sign-in (`PersistDeviceCard` from `ReloadOwnerScopedState`) is out of scope here; see A4b.
- [ ] `git grep -nE 'Log\w+\(\s*(ex,\s*)?\$"' -- src ':!src/ProoveGaming.Device.Testing'` returns no hits.
- [ ] Given a manifest with `download_url: "http://…"`, then `TryParseManifest` returns null.
- [ ] `README.md` lists every project in `src/ProveGaming.slnx` and every test project, shows how to run all tests, one project and one class (`--filter`), and gives both log locations. `src/README.md`, `tests/README.md` and `docs/README.md` reference only folders and files that exist (`git ls-files` check on every path they name).
- [ ] CI fails when a High or Critical vulnerable package is introduced, and the workflow runs with `contents: read`.
- [ ] `dotnet build ./src/ProveGaming.slnx` and `dotnet test ./src/ProveGaming.slnx -c Release` pass after every phase.

---

## Test strategy

- **Phase 1 (`fix-*`, write the reproductions first).** Run in `tests/ProoveGaming.Installer.Tests` (already references both Installer and Uninstaller, with `InternalsVisibleTo`). `InstallSignatureTests` and `UninstallPathResolutionTests` must **fail on current code**: today there is no signature gate, and the HKCU value is honoured. They use temp folders, `Directory.CreateSymbolicLink` or `mklink /J` for junction cases, and the pure `ResolveInstallPath` function, so they never touch the real registry or run `sc.exe` against the developer's installed service. `ServiceControlTests` covers rooted paths and the stderr-flood deadlock. ACL behaviour is tested by applying `InstallAcl` to a temp folder and reading back `GetAccessControl()`; this needs no admin rights for folders the test owns. **Not automated:** HKLM writes, real `sc create`, UAC and the full elevated uninstall. Those require admin and a clean VM, so they are covered by QA steps 1–6.
- **Phase 2 (`fix-*`).** Run in `tests/ProoveGaming.App.Tests`. `LocalStorageServiceSessionTests` (existing) gains the plaintext-absent, migration and undecryptable cases, which fail first. `ApiHttpClientCancellationTests` uses the existing `HttpMessageHandler` seam. `ConfigurationManagerTests` runs in a `[Collection("ProcessState")]` with parallelisation disabled, because the working directory is process-global. `DeviceServiceInputValidationTests` in `tests/ProoveGaming.Infrastructure.Tests` (which has `InternalsVisibleTo` from DeviceHost) uses a fake `IDeviceRgbServiceFactory` that records the `DeviceConfiguration` it receives. `LanguageResourceParityTests` must stay green.
- **Phase 3 (`remove-*`).** No new tests. The existing suite is the safety net and must stay green after the fakes are trimmed. The characterization already exists (`UsbHidDeviceServiceHostAbsentTests`, button and preset VM tests).
- **Phase 4 (`refactor-*`).** A1 and A7 are behaviour-neutral and covered by the existing suites. A4 extends the existing threading tests (`DeviceDetailsSetDeviceThreadingTests`, `HomeViewModelNotificationThreadingTests` pattern) **before** moving the call sites, so the tests fail first on the current UI-thread access. A11 extends `UpdateCheckServiceTests`. A3 (file sink) is not unit-tested; it is covered by the acceptance criterion and a QA step.
- Timing-sensitive tests (A4 threading) must be verified with a **Release** slnx test run, per CLAUDE.md.

## QA (manual test steps)

Run on a clean Windows 11 VM with a standard (non-admin) user account and a separate admin account.

1. Download the new installer to the Downloads folder and put any small program renamed `sc.exe` in the same folder. Run the installer and approve UAC. **Expected:** installation completes normally, and the renamed program never starts.
2. During installation, choose a custom folder such as `C:\Games\Proove`. After installing, right-click the folder → Properties → Security. **Expected:** ordinary users can only read and run files there; they cannot modify, write or delete.
2a. On the folder step, type the path of your Desktop, then `C:\`, then a folder that already holds your own files. **Expected:** each time a message under the box explains the folder can't be used (in the installer's current language) and Install stays disabled. A new or empty folder is accepted.
3. Open Settings → Apps → Installed apps. **Expected:** "Proove Gaming" is listed, including when you sign in as a different user on the same PC.
4. Launch the app, connect a supported mouse, and remap a button. Close and reopen the app. **Expected:** the remap is still shown and still works on the device.
5. Sign in with "Remember me" ticked, close the app from the tray, reopen it. **Expected:** you are still signed in.
6. Uninstall from Settings → Apps. **Expected:** the app, its folder and its Start menu/desktop shortcuts are removed, and Proove Gaming disappears from the Apps list. Other folders on the PC are untouched.
7. Reinstall. Before uninstalling again, ask the developer to run the "tampered uninstall location" check (they point the old per-user uninstall entry at a test folder containing dummy files), then uninstall. **Expected:** the test folder and its files are still there after the uninstall.
8. Switch the app language to Ukrainian. Disconnect the network and try to sign in. **Expected:** the error message is in Ukrainian, with no English words and no technical error text.
9. Still in Ukrainian, open a device → Save Preset. **Expected:** every label in the dialog, including the one next to the monitor icon, is in Ukrainian and fits its space.
10. Start the app from a Command Prompt whose current folder is some unrelated folder (for example `cd C:\Users\Public` and then run the app's full path). **Expected:** the app starts normally and sign-in works.
11. With the app running, stop the "Proove Gaming Device Host" service from the Services console, wait 10 seconds, and start it again. **Expected:** the app recovers device cards without freezing. A log file for today exists under `%LocalAppData%\ProoveGaming\Logs` and mentions the host being unavailable.
12. Throughout steps 4–11, type quickly in the sign-in and nickname fields and switch between device tabs. **Expected:** no stutter or freeze.

## Review focus

`installer/uninstaller`, `localization`, `gRPC contract`, `UI-thread affinity`

## Performance & quality

- **Hot paths:** none are introduced. `ProtoInput` validation runs once per configuration RPC, not per frame. The ACL work runs once per install. DPAPI runs once per sign-in or startup.
- **UI thread:** A4 removes UI-thread SQLite access. DPAPI calls must run inside the existing storage executor (they sit in `SaveAuth`/`GetSavedAuth`), never inline on the UI thread. The App file sink must be Serilog's default buffered (non-blocking) file sink, with no `Flush` on each call.
- **Thread-safety:** `LocalStorageService` keeps its single `_lock` funnel (`AcquireLock`) for the new column; no new lock is added.
- **Resource disposal:** the App Serilog logger is disposed in the existing shutdown path (`App` exit, after the storage drain) so the last lines flush.
- **Allocation:** A7 reduces allocations (message templates skip formatting when the level is off).

## Risks

- **Release-process impact (Phase 1).** Changing the uninstall hive means an older installed build is found through its legacy HKCU entry only by the *new* installer, which then migrates it. The *old* uninstaller of an existing install still works as before. Test upgrade from the current production build explicitly.
- **ACL too strict.** If the App ever writes into its install folder at runtime, the protected DACL breaks it. `/coder` must grep for writes relative to `AppContext.BaseDirectory` in App and Host first. Known host data goes to `%ProgramData%`, App data to `%LocalAppData%`.
- **DPAPI roaming.** A profile restored on another machine cannot decrypt the token. The designed behaviour is "signed out, sign in again", which is acceptable for "Remember me".
- **Cancellation rethrow (A9).** Any caller that passes a real token and does not expect `OperationCanceledException` would now fault. Every caller must be enumerated before the change.
- **Test-fake churn (Phase 3).** 22+ test files implement `IUsbHidDeviceService`. The deletion is mechanical but wide, so keep it a separate PR from Phase 2.
- **Proto removal (Phase 3).** Safe only because App and Host are always installed together. If a side-by-side or partial-update scenario is ever introduced, removed RPCs must be `reserved`, not reused.
- **Signature check false negative.** *(Revised 2026-09-24, review round 1 B1.)* A damaged install passes as long as one admin-owned marker remains. What still fails: a folder in which *every* marker is gone (for example only locked native DLLs left by an unfinished self-delete). The uninstaller then shows \"Nothing Was Removed\", and the installer rejects the folder as not empty, so the user picks another folder or deletes the leftovers. Such a folder holds no executable of ours, so it is inert.\n- **Legacy per-user entry in another account's hive.** When a different administrator approves UAC than the one who installed an older build, the new installer cannot reach the interactive user's legacy HKCU uninstall entry, so it may remain in that user's Settings → Apps next to the new per-machine entry. Running it still works: it resolves to its own folder.\n- **Abandoned staging folders.** A killed installer leaves its `%ProgramData%\ProoveGamingInstaller.<guid>` folder behind; the next installer run removes any such folder that is admin-owned and more than an hour old (review round 1 S1).

## Open questions

1. **S8 transport:** switch the App↔DeviceHost gRPC to a named pipe with an ACL (recommended; also fixes port-5000 squatting and A10), keep TCP and accept the risk, or keep TCP plus a secret file? If you approve the pipe, it becomes a separate `migrate-devicehost-named-pipe` plan. **Decided 2026-09-24: named pipe with an ACL.** Planned in [`migrate-devicehost-named-pipe`](../migrate-devicehost-named-pipe/migrate-devicehost-named-pipe-plan.md), which also resolves A10.
2. **S12 code signing:** is an Authenticode certificate available (EV or OV)? Without one, UAC keeps showing "Unknown publisher" for the auto-elevating installer. **Answered 2026-09-24:** a certificate and signing tool are expected soon but are not available now. Deferred; wire signing into `release.yml` once they arrive.
3. **A5:** do you want the auth/session flow extracted from `MainWindow` code-behind into a testable service now (its own `refactor-auth-session-flow` plan with characterization tests first), or left until the next feature touches it? **Decided 2026-09-24: refactor now, in this branch.** Planned in [`refactor-auth-session-flow`](../refactor-auth-session-flow/refactor-auth-session-flow-plan.md), with characterization tests first.
4. **A11:** should the update announcement open `download_url` in the browser (https-only), or should the dead field be dropped until a self-update feature exists? **Decided 2026-09-24: leave as is.** `download_url` stays validated (absolute https, Phase 4) and unused until a self-update feature exists.
5. **A12:** adopt central package management (`src/Directory.Packages.props`)? It changes every csproj and adds a props file, so it needs your sign-off per the architect rules. **Answered 2026-09-24:** not now. The solution is an MVP in a single repository; revisit if it grows.
6. **Install location (S3):** confirm that keeping custom install folders with a protected ACL is preferred over restricting installs to Program Files. **Decided 2026-09-24 (Phase 1): keep custom folders, with the protected ACL.**

## Review findings

_Filled in by /reviewer._

### Round 1 — Ship: no

Reviewed 2026-09-24: branch `fix/repository-audit-phase1`, commit `a2b4563` ("phaze 1 finished. testing") against `main`, 29 files, working tree clean. Scope was Phase 1 only (S1, S2, S3, S4, S5, S11, S13, A2, A14 installer part).

#### Plan conformance
- **Implemented as planned:** S1 (trusted-source path resolution plus signature gate before any side effect, `UninstallService.cs:48-68`), S2 (HKLM registration, legacy HKCU deleted, SID forwarding removed), S4 (staging directly under `%ProgramData%`, protected from creation), S5 (`SystemTool`, used for `sc`, `powershell` and `cmd`), S11 (PowerShell and C# walks no longer follow junctions), A2 (`InstallLayout`, one `ServiceControl` runner, concurrent stream reads), A14 installer part (README).
- **Deviates from plan, and the plan was updated in the same change (acceptable):** S13 logs to `<install>\host\logs` instead of `%ProgramData%\ProoveGaming\Logs`. S3 gained install-location validation. S11 was extended to `TryDeleteDirectoryContents`. The uninstaller now decides on `sc.exe` exit codes rather than text; this is behaviour-equivalent (codes 1060 and 1062) and is folded into A2.
- **Missing / partial:** acceptance criteria that need elevation or a VM (HKLM contents, `icacls`, Process Monitor, a real uninstall) are unverified. The plan routes them to QA steps 1–7, and they must run before release. Several criteria have no automated test (see Tests).
- **Accepted limitation (per plan):** "a partially-deleted install fails the signature check … a reinstall followed by an uninstall recovers." **The recovery this relies on is broken by S3's new location rule; see B1.** Residual risk: users stranded with a broken install.
- **Accepted by the coder, not by the plan:** a legacy per-user uninstall entry in a *different* account's hive than the one approving UAC is no longer reachable, so it can linger in that user's Apps list. Low impact: running it still works, because it resolves to its own folder. Record it in Risks.

#### Code
- [x] **B1. A broken or partly-removed install can be neither reinstalled nor uninstalled** — `src/ProoveGaming.Installer/Views/MainWindow.axaml.cs` (`GetInstallLocationErrorKey`) + `src/ProoveGaming.Common/Classes/InstallSignature.cs:23-37` + `src/ProoveGaming.Uninstaller/UninstallService.cs:61-65` — Blocker
  **Problem:** The installer now accepts a non-empty folder only if `InstallSignature.IsProoveGamingInstall` passes, which requires both `ProoveGaming.App.exe` and `host\ProoveGaming.DeviceHost.exe`. The uninstaller refuses on the same check. Any install missing either EXE is therefore refused by both tools.
  **Why it blocks:** That state is not hypothetical. It is exactly what the field has produced, and what our own code produces by design:
  1. `docs/support/missing-files-crash-analysis.md` §1: AV quarantines individual unsigned files and leaves the rest in place. If it takes `ProoveGaming.App.exe` or the DeviceHost EXE from `C:\Program Files\ProoveGaming`, reinstalling there (the pre-filled default) shows "This folder already contains other files". Uninstalling from Settings → Apps fails with "Uninstall could not be completed", and the reason is only in a temp log.
  2. A normal uninstall whose self-delete loop cannot finish (a lock held past ~2 minutes) leaves the uninstaller EXE and three DLLs. `UninstallService.cs` calls this "a visible, harmless, fixable state", and the fix was always "reinstall". Reinstall is now refused.
  3. An uninstall interrupted after `TryHandleDeviceHostServiceAsync` deletes `host\` leaves the App EXE but no host EXE, so the retry is refused too.

  In every case the only way out is a manual admin delete under Program Files, which CLAUDE.md forbids ("don't introduce a design that needs … manual user cleanup to … reinstall or recover").

  **Solution options:**
  - **A (correct): decide "ours" by provenance, not by which files survived.** A standard user cannot create a folder owned by `BUILTIN\Administrators`, `SYSTEM` or `TrustedInstaller`. Our elevated installer's folders always are. Add an owner check to `InstallSignature` and accept a non-empty folder when it passes the location check, is not a reparse point, is admin/SYSTEM-owned, and contains at least one marker (`ProoveGaming.App.exe`, `ProoveGaming.Uninstaller.exe`, or `host\`). Use the same relaxed check in the uninstaller for the HKLM-registered path, which is already admin-only. It still rejects the attack case (a user-owned folder, or one with no markers), and the payload copy plus `ProtectExistingDirectory` then repair it.
  - **B (quick):** in the installer only, also accept a non-empty folder that equals the HKLM `InstallLocation`, or contains `ProoveGaming.Uninstaller.exe` (always the last file to go). This fixes causes 1 and 2 for reinstall, but leaves the uninstaller refusing. In that case show a localized uninstaller message ("reinstall Proove Gaming, then uninstall again") instead of the generic failure.
  - In both options, add the missing-EXE cases to `InstallLocationValidationTests` and `InstallSignatureTests` first. Today no test covers a partial install, which is how this slipped through.
- [x] **S1. Staging folders now leak into `%ProgramData%` forever when the installer dies mid-install** — `src/ProoveGaming.Installer/Views/MainWindow.Installation.cs` (`ExtractEmbeddedPayloadAsync`, `CleanupStagingDirectoryAsync`) — Should-fix
  **Problem / why it matters:** Cleanup runs only in `RunInstallAsync`'s `finally`. If the installer is killed during install (Task Manager, the installer-freeze case C1 in the 2026-08-21 audit, a crash, power loss), the ~250 MB extraction is orphaned. Before, it sat in `%TEMP%`, which Storage Sense and Disk Cleanup purge. Now it sits in `%ProgramData%\ProoveGamingInstaller.<guid>`, which nothing ever purges, and each failed run adds another.
  **Fix:** at installer start (off the UI thread), delete every `ProoveGamingInstaller.*` folder directly under `%ProgramData%` whose owner is Administrators or SYSTEM. A standard user cannot create one with that owner, so the sweep cannot be tricked into deleting user data. Skip any folder whose name is not the prefix plus a 32-hex GUID.
- [x] **S2. The uninstaller's refusal is silent to the user** — `src/ProoveGaming.Uninstaller/Views/MainWindow.axaml.cs:113` (`succeeded = exitCode == 0`) — Should-fix
  **Problem / why it matters:** A refusal (not our install, no registered install) and a genuine failure both show "Uninstall could not be completed". The reason is written only to `%TEMP%\proove-uninstall.log` of the elevated account, which a user will not find. After B1 is fixed this is rarer, but it remains the only feedback on the new safety path.
  **Fix:** return a distinct exit code for "refused" (for example 2) from `UninstallService.RunAsync`, and map it to a localized en/uk message naming the next step (reinstall, then uninstall) and the log path.

#### Tests
- [x] **No test pins the ordering "signature check before any side effect" in `UninstallService.RunAsync`.** A refactor that moves the gate below `TryTerminateRunningAppProcesses` or the `sc` calls would pass every test. Extract a pure `internal static string? ResolveVerifiedInstallPath(...)` (resolution plus signature, returning the refusal reason), call it from `RunAsync`, and test it with temp folders — `tests/ProoveGaming.Installer.Tests/UninstallPathResolutionTests.cs`.
- [x] **The junction-safety of `TryDeleteDirectoryContents` is untested.** This is the S11 fix for elevated "delete my data". Make it `internal` and add a test: a tree containing a junction to a sibling folder is emptied, the junction is removed, and the sibling's files survive (`TestFolders.CreateJunction` already exists) — new `tests/ProoveGaming.Installer.Tests/UninstallDeleteWalkTests.cs`.
- [x] **Partial-install cases** (App EXE missing, host EXE missing, only the uninstaller EXE left) for both the installer location rule and the uninstaller check. These must be written first for B1 — `InstallLocationValidationTests.cs`, `InstallSignatureTests.cs`.
- [x] `ProtectedSecurity_ForTheInstallFolder_LetsUsersReadAndRunButNotWrite` asserts `!HasFlag(FileSystemRights.Write)`. `Write` is a multi-bit mask, so a rule granting only `WriteData` or `Delete` would still pass. Assert `(rights & (Write | Delete | DeleteSubdirectoriesAndFiles | ChangePermissions | TakeOwnership)) == 0` — `InstallAclTests.cs`.

#### Nits
- `GetInstallLocationErrorKey` maps an `IOException` / `UnauthorizedAccessException` while listing the folder to "This folder already contains other files", which is wrong for an unreadable folder. Use a separate key, or `Message_InvalidInstallPath` — `MainWindow.axaml.cs`.
- `RegisterWindowsUninstallEntry` deletes the legacy HKCU key outside any try/catch, so an unexpected registry exception fails the install at its last step, after the service is configured. Wrap it best-effort, like the uninstaller does — `MainWindow.Installation.cs`.
- `PowerShellSelfDelete_ChecksTheMarkerBeforeDeleting` finds "the first delete" via `IndexOf("Remove-Item")`, which is now only the uninstaller-EXE delete. Also assert that the guard precedes `[System.IO.Directory]::Delete` — `UninstallHandoffTests.cs`.
- README "Installer: `%TEMP%\proove-install.log` (written only when a step fails)". It is written only when clearing old reboot-scheduled deletions fails; say that, or drop the line — `README.md`.
- `docs/CONTEXT.md` has `Install signature` but not `InstallAcl` / `ServiceControl` / `InstallLayout`. The glossary lists comparable helpers (for example `ApiHttpClient`), so add one line each.

#### What's done well
- The trust model is right and explained where it lives. The command line may only *choose between* trusted sources (`ResolveInstallPath`), and the gate runs before a single process is killed. Keep that ordering.
- Staging directly under `%ProgramData%` with the DACL applied at creation, instead of a protected child under a user-creatable parent, closes the rename-and-substitute race. That subtlety is easy to lose in a later "tidy-up".
- Moving logs into the protected install folder removes the S13 cause rather than defending a pre-creatable path. This is the CLAUDE.md "remove the cause" rule applied well.
- The C# delete walk now removes junctions as links. It covers the elevated "delete my data" path, which the plan had not identified.
- Unifying on `sc.exe` exit codes removes a latent localized-Windows fragility from the uninstaller.

#### Verdict
Ship: no. The security design is sound and the full Release suite is green, but B1 turns two documented real-world states (AV quarantine, an unfinished self-delete) into installs that can be neither repaired nor removed without a manual admin delete. First: (1) B1 option A with the partial-install tests first, (2) S1 staging sweep, (3) the RunAsync-ordering and delete-walk tests. Because these projects run elevated and ship an embedded payload, also run the built-in `/security-review` on this branch before merge, and run QA steps 1–7 on a clean VM.

_Coder, 2026-09-24 — all Round 1 items addressed (uncommitted on `fix/repository-audit-phase1`):_
- _B1: option A as proposed, tightened: the marker FILE must itself be admin-owned, not just the folder, so a user-made copy of one of our EXEs never counts (`InstallSignature.IsProoveGamingInstall` / `IsOwnedByTrustedPrincipal`). Partial-install tests were written first and failed to compile against the old API._
- _S1: sweep runs inside the existing background extraction step; a folder must be exactly the prefix plus a GUID, admin/SYSTEM-owned, not a link, and older than 1 hour (the age guard protects a second installer window extracting at the same time)._
- _S2: exit code 2 → "Nothing Was Removed" screen (en/uk). `CompleteMessageText` gained `TextWrapping="Wrap"`, needed for the longer message._
- _Nits: all five fixed (unreadable folder → `Message_InvalidInstallPath`; legacy HKCU delete best-effort; handoff test also orders the guard before `Directory::Delete`; README installer-log line; glossary gained `InstallAcl`, `InstallLayout`, `ServiceControl` and the revised `Install signature`)._
- _Found while fixing B1 and fixed with it: an elevated process's new files are owned by `BUILTIN\Administrators` only while UAC is on. With UAC disabled they belong to the installing account, so the provenance check would have refused to uninstall every such install. The installer now sets Administrators as owner of `ProoveGaming.App.exe`, `ProoveGaming.Uninstaller.exe` and `host\ProoveGaming.DeviceHost.exe` explicitly (`InstallAcl.MakeAdministratorsOwner`); the folder already got it from `ProtectExistingDirectory`. Pinned by `InstallAclTests.MarkerFile_MadeAdministratorsOwned_PassesTheUninstallersOwnerCheck` (runs when elevated, as on CI). Older installs made with UAC off are only covered once reinstalled with this build._
- _Also from the B1 fix: installs made by builds before this one with UAC disabled are owned by the installing account, so the owner rule alone would have refused an in-place upgrade of the default install. Anything strictly inside `%ProgramFiles%` / `%ProgramFiles(x86)%` now counts as trusted by location, since only administrators can write there (`InstallSignature.IsInsideProgramFiles`; a look-alike sibling such as `Program Files Evil` does not count). A UAC-off install from an older build in a custom folder is still refused in place; its own older uninstaller still removes it, so it is not stranded._
- _Not changed, flagged for a later round: the existing `Step3_Subtitle_Failed` text still says files "may be deleted after reboot", which has been untrue since reboot deletion was removed (2026-09-15). It predates this branch and is outside Phase 1._

### Round 2 — Ship: no

Reviewed 2026-09-24: branch `fix/repository-audit` (renamed from `fix/repository-audit-phase1`), uncommitted working tree on top of `a2b4563`. The tree has 41 entries (34 modified, 7 untracked) and nothing staged. Scope: **Phase 2** (S6, S7, S9, S10, A8, A9) plus a **re-review of the Round 1 fixes** to Phase 1.

#### Plan conformance
- **Round 1 fixes: verified closed.** B1 is now provenance-based: `InstallSignature.IsProoveGamingInstall` requires the folder plus at least one admin-owned marker (or a location inside Program Files), with partial-install tests. S1: the abandoned-staging sweep requires exactly the prefix plus a GUID, a trusted owner, no reparse point and an age over 1 h. S2: exit code 2 maps to the localized "Nothing Was Removed" screen. All four Tests items and all five Nits are done. The coder's extra UAC-off ownership step (`InstallAcl.MakeAdministratorsOwner`) was checked by the reviewer with a .NET 10 probe: a fresh `FileSecurity` carrying only an owner persists **only** the owner, and the file's DACL is byte-identical before and after, so installed EXEs stay readable.
- **Phase 2 implemented as planned:** S6 (`ConfigurationManager` reads from `AppContext.BaseDirectory`; user-secrets are Debug-only in code and csproj; the Release outputs of both TFMs contain no `UserSecrets` DLL and no `UserSecretsIdAttribute`, verified), S9 (`Helpers/ProtoInput.cs`; proto and Infrastructure enum values match exactly: `MouseButton` 0–5, `MouseButtonAction` 0–18, `MacroRepeatMode` 0–2), S10/A8 (no raw `Message`, English fallback or `ex.Message` is left in any ViewModel or View; verified by search), A9.
- **Deviations, already recorded in the plan (acceptable):** S7 stores a `dpapi1:` prefix in the existing column instead of a new column. Client codes live in `ClientErrorCodes`, not the server catalog. `Presets_Item_App` is reused. The registration toast site was added. Contact Us maps its own timeout back to Failed. The `fix-profile-and-auth-validation` M2 test now asserts localized text.
- **Partial, deferred by the plan:** the plan's Performance section says DPAPI must run inside the storage executor, never inline on the UI thread. `SaveAuth` still runs inline from `MainWindow.PersistAuthIfTokenPresent`, and `GetSavedAuth` inline at startup. The cost is about a millisecond, and A4 (Phase 4) is the item that moves these off the UI thread, so this is accepted until then.

#### Code
- [x] **B1. A DPAPI failure now breaks sign-in, and can stop the App from starting at all** — `src/ProoveGaming.App/Services/LocalStorageService.cs` (`SaveAuth`, `GetSavedAuth` → `ProtectSavedToken`) — Blocker
  **Problem:** `ProtectedData.Protect` can throw `CryptographicException`, for example when the user profile's DPAPI master key is unavailable (a temporary or corrupted profile, or a roaming profile whose key cannot be reached). Nothing on either path catches it, and neither path could throw before this change.
  **Why it blocks:** likelihood is low, but the outcomes are severe, and the repo rule grades a new runtime crash path as a Blocker:
  1. *Startup:* a legacy plaintext row makes `GetSavedAuth` call `ProtectSavedToken` to upgrade it. `App.axaml.cs:66` calls it directly from `OnFrameworkInitializationCompleted`, which is not a dispatcher callback, so `AppResilience` does not catch it. The App fails to start, **on every launch**, because the row that triggers the upgrade is never rewritten.
  2. *Sign-in:* `MainWindow.OnSignInSucceeded` (`MainWindow.axaml.cs:650-680`) calls `PersistAuthIfTokenPresent` after `_session.EndSession()`. The throw is marked handled by `AppResilience`, but it skips `ReloadOwnerScopedState`, the navigation to Home, the profile load and the sign-in pull. The user is signed in but left on the Welcome screen with no message.
  **Solution options:**
  - **A (correct):** treat "cannot protect" as "cannot remember", never as a failure of the operation. In `SaveAuth`, catch `CryptographicException` from `ProtectSavedToken` and skip the write. Sign-in proceeds, and only "Remember me" is lost for that session; add a Debug trace, since the service has no logger. In `GetSavedAuth`'s legacy upgrade, catch it around the re-protect, return the token, and leave the row for the next attempt. The plaintext row then stays until DPAPI works, which is no worse than before this change.
  - **B (quick):** wrap both call sites (`App.axaml.cs:66`, `MainWindow.PersistAuthIfTokenPresent`) in try/catch. This is weaker: every future caller has to remember it.
- [x] ~~S9 enum-parity risk~~: checked and not a finding (see Plan conformance). A parity test is listed under Tests, because S9 now rejects what it used to pass through.

#### Tests
- [x] **Contact Us own-timeout mapping is untested** (`HomeViewModel.SendContactUsAsync`, the `catch (OperationCanceledException) when (!ct.IsCancellationRequested)` block). It is the one caller that depended on the old "never throws" contract, and removing that catch would pass every current test and hang the dialog on its spinner. `ContactUsSendTimeout` is a `static readonly` 15 s. Make it an internal settable seam (like the sync services' `_notificationPollTimeout`), then test that a fake `ContactUsAsync` which never completes yields `ContactUsError == "ContactUs_SendFailed"` with the dialog open — `tests/ProoveGaming.App.Tests/HomeViewModelContactUsTests.cs`.
- [x] **`MakeAdministratorsOwner` has only an elevation-gated test, which passes vacuously on developer machines** (this session is not elevated). Add a non-elevated test that repeats the reviewer's probe: a fresh `FileSecurity` with only `SetOwner(current user)` applied to a temp file leaves `GetSecurityDescriptorSddlForm(AccessControlSections.Access)` unchanged. That pins "only the owner is written" on every machine, which is the property protecting installed EXEs from an empty DACL — `tests/ProoveGaming.Installer.Tests/InstallAclTests.cs`.
- [x] **Proto ↔ Infrastructure enum parity** for `MouseButton`, `MouseButtonAction` and `MacroRepeatMode`. The values match today, but `ProtoInput` now *rejects* a value the Infrastructure enum lacks. A proto value added without its Infrastructure twin would make that remap fail with only a host log line. Add a reflection test comparing `Enum.GetValues` of each pair — `tests/ProoveGaming.Infrastructure.Tests/ProtoInputTests.cs`.
- [x] **B1 paths**, after the fix: `SaveAuth` when protection throws (sign-in data not persisted, no exception), and a legacy row whose upgrade fails (token still returned). Forcing a real DPAPI failure is impractical, so route `ProtectSavedToken` through an internal seam — `LocalStorageServiceSessionTests.cs`.

#### Nits
- `SettingsSyncService.PullAsync` (`catch (Exception ex)` → `LogError("Sign-in pull … failed")`): now that caller cancellation propagates (A9), a pull cancelled by its own token is logged at Error. Add `catch (OperationCanceledException) when (ct.IsCancellationRequested)` at Debug before it, matching `RunPollTickAsync` — `SettingsSyncService.cs:467-477`.
- `ProtectSavedToken` / `UnprotectSavedToken` throw `PlatformNotSupportedException` off Windows. That is correct for the product, but the App's `net10.0` TFM would throw on sign-in if ever run elsewhere. A one-line comment on the guard saying it is intentional would stop someone "fixing" it with a plaintext fallback — `LocalStorageService.cs`.

#### What's done well
- **The A9 caller audit.** It found that Contact Us built its timeout as a linked caller token and depended on "never throws", and fixed it where the timeout is owned rather than weakening `ApiHttpClient`. It also confirmed the sync push, pull, pass and poll already contain cancellation, and `SyncCoordinator` passes no token at all.
- Keeping `ClientErrorCodes` out of `AuthErrorCodes` respects the exhaustive server-spec test instead of weakening it.
- The `dpapi1:` prefix, with in-place legacy upgrade and a clear-on-undecryptable path, removes a schema migration and a two-column consistency problem.
- `ProtoInput` is pure, lives in one place and is tested at the boundaries. The deleted `MapMacro` was the unclamped path.
- Reusing `Presets_Item_App` for the Save Preset label avoids a duplicate resource.

#### Verdict
Ship: no. Phase 2 is correct and complete against the plan, and the Round 1 fixes hold up. The one Blocker is new: an unguarded DPAPI failure can leave sign-in stranded or stop the App from launching at all. It is rare, but it is a new crash path, and fix A is about ten lines. First: (1) B1 option A, with its seam and tests, (2) the Contact Us timeout test, (3) the non-elevated owner-only test and the enum-parity test. Because the branch still carries elevated installer and uninstaller changes, run `/security-review` before merge, plus QA steps 1–12 on a clean VM.

_Coder, 2026-09-24 — all Round 2 items addressed (uncommitted on `fix/repository-audit`):_
- _B1: option A. `SaveAuth` catches `CryptographicException`, skips remembering, and **clears** any earlier saved sign-in. Merely skipping the write would have left a previous account's row for the next launch to restore. The legacy upgrade moved into `TryUpgradeLegacyToken`, which on failure returns the token and leaves the row to retry. Both go through an instance seam, `LocalStorageService.ProtectToken`, in the same style as `OnBeforeStorageAccess`._
- _Contact Us: instead of a HomeViewModel harness (it needs a full fake device service), the timeout rule moved into `HomeViewModel.SendWithTimeoutAsync`, an internal static like `DecideContactUsOutcome`. The dialog awaits it; tests cover own-timeout → Failed with `ContactUs_SendFailed`, caller cancellation → propagates, and in-time answer → passed through._
- _Owner-only test: `InstallAcl.MakeAdministratorsOwner` now delegates to `internal SetOwnerOnly(path, owner)`, and the new test drives that real code with the current user as owner, so no elevation is needed. It asserts the DACL SDDL is unchanged. Installer-side shared code; no behaviour change._
- _Enum parity: a reflection theory over the three proto/Infrastructure pairs, in `ProtoInputTests`._
- _Nits: both fixed (a cancelled sign-in pull logs at Debug; comment on the intentional Windows-only guard)._
- _Verification: full Release suite green (Installer 123, Infrastructure 1127, App 1836 + 1 skipped)._

### Round 3 — Ship: no

Reviewed 2026-09-24: branch `fix/repository-audit`. The Round 2 fixes are committed in `d1acd1b` ("phaze 2 finished"). **Phase 3** (D1–D5, including the `SetMouseParamAsync` extension) sits in the working tree: 51 modified files, plus `ButtonBindingStore.cs` staged for deletion.

#### Plan conformance
- **Round 2 fixes: verified closed.**
  - B1: `SaveAuth` catches `CryptographicException`, skips persisting and clears the earlier row. That `ClearSavedAuth` call runs before `SaveAuth` takes `_lock`, so the non-reentrant semaphore is never taken twice. `TryUpgradeLegacyToken` leaves the row for a retry. Both go through the `ProtectToken` seam, with tests.
  - `SendWithTimeoutAsync` is extracted and covered: own timeout → Failed, caller cancel → propagates, in-time answer → passed through.
  - `SetOwnerOnly` is tested on every machine without elevation.
  - Enum parity is pinned.
  - Both nits are done.
- **Phase 3 implemented as planned:**
  - D1: store, registration, RPC, messages, App wrapper and fakes are gone. `LocalStorageService.GetButtonBindings`, the live SQLite read, is correctly kept.
  - D2: the RPC, messages, App wrapper and fakes are gone.
  - D3, D4.
  - D5: `project.assets.json` confirms nothing depends on `HidApi.Net` or `Tmds.DBus`. `Tmds.DBus.Protocol` comes from `Avalonia.FreeDesktop`, as the plan now says. `AvaloniaUI.DiagnosticsSupport` is Debug-only, and the Debug build still compiles `AttachDeveloperTools`.
  - The acceptance grep returns nothing over `src/` and `tests/`.
- **Deviation, recorded in the plan (acceptable):** removing `SetMouseParamAsync` from `IMouseRgbService` and its six implementations. It was verified to orphan nothing: `PatchLiftOffDistance` is still used at `TelinkMouseService.cs:767`, and the factory's read interface still serves `ApplyConfigurationAsync`.
- **gRPC contract:** two RPCs and four messages were removed, both sides were regenerated, and no kept message lost a field, so no `reserved` is needed. This is safe only because App and Host ship in one installer, as the plan's Risks already records.
- **Plan claim not yet true:** Phase 1's S11 ("deletes never follow junctions"), repeated in `README.md`, does not hold for the **root** of a delete. See B1.

#### Code
- [x] **B1. "Delete my data" follows a junction at the root of the data folder, so the elevated uninstaller empties the junction's target** — `src/ProoveGaming.Uninstaller/UninstallService.cs` (`DeleteDataFolder` → `TryDeleteDirectoryContents`; also `TryHandleDeviceHostServiceAsync`'s `host\` walk) — Blocker
  **Problem:** `TryDeleteDirectoryContents` refuses to descend into a *child* reparse point but never checks the directory it is given. `Directory.GetFileSystemEntries(junction)` lists the junction's **target**, so every file there is deleted.
  **Why it blocks:** **reproduced by the reviewer** with a .NET 10 probe mirroring the walk exactly: a junction `…\ProoveGaming` → `Victim\`, then the walk, then `Victim\precious.txt` was gone. Concrete attack: a medium-integrity process replaces `%LocalAppData%\ProoveGaming`, which the user always owns, with a junction to an admin-only folder, for example another app under `C:\Program Files`. Anyone can equally *create* `%ProgramData%\ProoveGaming` as a junction, since users may create folders in `%ProgramData%`. The next time the user uninstalls with "delete my data" and approves UAC, the elevated uninstaller empties that target. It predates this branch (the old `SearchOption.AllDirectories` walk followed it too), but S11 set out to close exactly this class and the branch now documents it as closed. After Phase 3, nothing legitimately writes `%ProgramData%\ProoveGaming` at all.
  **Solution options:**
  - **A (correct):** in `TryDeleteDirectoryContents`, check the given directory first. If it is a reparse point, enumerate nothing; the callers' `TryDeleteDirectory` then removes the link as a link. This covers `%LocalAppData%\ProoveGaming`, `%ProgramData%\ProoveGaming` and the legacy-install `host\` case in one place. For `%ProgramData%\ProoveGaming`, additionally require `InstallSignature.IsOwnedByTrustedPrincipal`: the old DeviceHost created it as SYSTEM, while a folder a user pre-created there was never ours and should be left alone and logged.
  - **B (quick):** the reparse check in `DeleteDataFolder` only. This leaves the `host\` walk exposed for legacy custom-folder installs.

#### Tests
- [x] **Root-junction cases** for B1, which the Round 1 test (`JunctionInsideTheTree_…`) does not cover: `TryDeleteDirectoryContents(junctionToVictim)` leaves the victim's files intact, and the junction itself can then be removed — `tests/ProoveGaming.Installer.Tests/UninstallDeleteWalkTests.cs`.
- [x] `%ProgramData%\ProoveGaming` owned by an untrusted principal is skipped. Extract the decision (`reparse? trusted owner?`) as a pure internal predicate, in the style of `IsSweepableStagingFolder`, and test it. The real delete path needs elevation and a real `%ProgramData%` — `UninstallDeleteWalkTests.cs`.

#### Nits
- `DeviceService.cs` keeps three aliases that nothing uses after D1: `ProtoButtonBinding`, `ProtoMouseButton`, `ProtoMouseAction`. Only `GetButtonBindings` used them — `src/ProoveGaming.DeviceHost/Services/DeviceService.cs` (the `using … =` block).

#### What's done well
- The D2 extension stopped at the right place: the reviewer checked that `PatchLiftOffDistance` and the factory's read interface still have live callers, so nothing further cascades.
- `project.assets.json` was consulted before removing the packages, and `Tmds.DBus.Protocol` was correctly identified as Avalonia's, not ours.
- The earlier plans (`add-button-remapping`, `pg-130`) got dated supersession notes instead of rewrites, as CLAUDE.md asks.
- The mechanical fake cleanup across 30 test files was shape-checked before any regex ran, so it removed exactly 47 members and nothing else.

#### Verdict
Ship: no. Phase 3 is complete, clean and faithful to the plan, and the Round 2 fixes hold. The single Blocker is an elevated-delete hole the branch claims to have closed but only closed one level down, now reproduced. First: (1) B1 option A, (2) its two tests, (3) the alias nit. Then run `/security-review` over the whole branch (it carries elevated installer and uninstaller changes) and QA steps 1–12 on a clean VM before merge.

_Coder, 2026-09-24 — all Round 3 items addressed (uncommitted on `fix/repository-audit`):_
- _B1: option A. `TryDeleteDirectoryContents` returns immediately when the folder it is given is a reparse point or unreadable; the caller's non-recursive `TryDeleteDirectory` then removes the link as a link. One check covers `%LocalAppData%\ProoveGaming`, `%ProgramData%\ProoveGaming` and the legacy `host\` walk. `DeleteDataFolder(baseFolder, requireTrustedOwner)` is now internal; the `%ProgramData%` call passes `true` and leaves an untrusted-owner folder in place with a log line._
- _Tests (`UninstallDeleteWalkTests`, +4): a junction as the starting folder leaves its target intact; `DeleteDataFolder` on a junction removes only the link; a user-owned folder is left alone when a trusted owner is required (owner set explicitly, so it does not depend on elevation); an ordinary data folder is still removed. These drive the real `DeleteDataFolder` rather than an extracted predicate, since the owner can be set without elevation._
- _Nit: the three unused aliases were removed from `DeviceService.cs`._
- _Verification: Installer tests 127/127 (Release and Debug); Infrastructure 1127 and App 1836 + 1 skipped (Release). One solution-wide run hit a transient `NETSDK1047` restore race on the Uninstaller's `win-x64` publish (the same parallel-build race seen in Phase 2); a clean solution rebuild succeeded._

### Round 4 — Ship: yes

Reviewed 2026-09-24: branch `fix/repository-audit`. The Round 3 fixes are committed in `813aa12` ("phaze 3 finished"). **Phase 4** is in the working tree: 45 modified files, 18 staged renames (the A1 move, each with its namespace edit), and 1 untracked test (`WelcomeViewModelStorageThreadingTests.cs`).

#### Plan conformance
- **Round 3 fixes: verified closed.** `TryDeleteDirectoryContents` refuses a root that is a reparse point or unreadable. `DeleteDataFolder(…, requireTrustedOwner: true)` guards `%ProgramData%`. One interaction was re-checked: the owner check on a junction path reads the *target's* owner, but the root guard still refuses the walk, so the worst case is removing the link itself. There are four tests, and the unused aliases are gone.
- **Phase 4 implemented as planned:**
  - A1: 18 files moved with history. No string, reflection or XAML `using:` reference to the old namespace remains (verified by search). The four generator scripts were repointed, including the namespace they emit, and all compile.
  - A3: Serilog file sink, buffered, `dispose: true`; no credential appears in any App log template (verified).
  - A7: zero interpolated log calls remain. Argument order was checked against each template, and dropping `?.` from `DeviceService._logger` is safe because the constructor throws on null.
  - A11: absolute https only. `javascript:`, `file:`, relative and plain-http links are rejected, with tests.
  - A12, A13 (plus the `/reviewer` skill drift), A14.
  - CI: the JSON-based scan was run locally over 12 projects and finds nothing today. The script tolerates missing `vulnerabilities` / `transitivePackages` properties and fails closed if the JSON does not parse.
- **Seam checked, not a finding:** the App now ships its own `Serilog*.dll`, which the host also has. The installer's `DedupeSharedRuntime` compares by SHA-256, not name (`ProoveGaming.Installer.csproj`), so differing versions are never swapped between `App\` and `host\`.
- **Ordering checked, not a finding:** every synchronous reader of data that A4 now writes asynchronously (`GetSessionState`, `GetSavedAuth`, `GetLockoutUntil`, `GetLastSignIn`) runs only at start-up, and the exit path drains the queue first.
- **Deviation needing sign-off:** A4 was narrowed during implementation, and its acceptance criterion was rewritten to match. Device *add* on sign-in (A4b) stays on the UI thread, and the constructor reads stay synchronous. The reasons are recorded and sound (`StorageId` timing inside the owner-scoped adopt/revive logic; a late lockout read would let a sign-in start before it applied), but narrowing a criterion is the requester's decision, not the coder's. See S1.
- **New finding recorded by the coder, verified:** D6. `AddDeviceCardViewModel.DeviceAdded` is never raised (the compiler already warns, CS0067), so `HomeViewModel.OnDeviceAdded` is dead.

#### Code
- none.

#### Plan
- [x] **S1. Confirm the narrowed A4 scope, or schedule A4b and D6** — `docs/plans/fix-repository-audit-findings/fix-repository-audit-findings-plan.md` (Phase 4 A4 / A4b / D6, and the rewritten A4 acceptance criterion) — Should-fix
  **Problem / why it matters:** The criterion now accepts that `PersistDeviceCard` → `AddDevice` still runs on the UI thread during sign-in (once per card in `ReloadOwnerScopedState`). With many stored cards that is a synchronous SQLite write per card on the UI thread at sign-in, the kind of hitch CLAUDE.md treats as a bug. It is recorded honestly, but a scope cut should be agreed, not decided silently by the implementer.
  **Fix:** the requester either accepts A4b and D6 as follow-ups (give each its own plan: `refactor-owner-reload-storage-async`, `remove-dead-add-device-flow`) or asks for them in this branch. No code change is needed to close S1; the decision is.

#### Tests
- [ ] **`ExecuteConfirmDelete` now queues `DeleteDevice`, but nothing pins it.** Use the existing `HomeViewModelNotificationThreadingTests` harness (`NewVm`) with the same gate pattern as `WelcomeViewModelStorageThreadingTests`: queue a blocker, delete a signed-in user's card, assert no inline storage access, release the gate, and assert the row is gone — `tests/ProoveGaming.App.Tests/`.
- [ ] **The CI gate's failure branch has never run.** Only the "no vulnerable packages" path was exercised. Dry-run the step's script once against a hand-made JSON fixture containing one `High` entry and confirm it exits 1, then note it in the plan's QA. This is not a unit test; it is a one-time check of a script whose failure path would otherwise first run on a real advisory — `.github/workflows/dotnet-ci.yml`.
- MainWindow's four queued writes (`SaveAuth`, both `ClearSavedAuth`, `InitializeSession`) live in a View and are not unit-testable here. They are covered by the plan's QA steps 5 and 12. Accepted; noted so it is not mistaken for coverage.

#### Nits
- `WelcomeViewModel.EnqueueStorageWrite` discards the task, so a faulted write is only reported when the GC finalizes it (through `AppResilience`'s unobserved-task hook), while `HomeViewModel` and `MainWindow` log faults immediately. That is consistent in outcome, not in timing. A `ContinueWith(OnlyOnFaulted)` writing to `Debug`, or a logger, would match — `src/ProoveGaming.App/ViewModels/WelcomeViewModel.cs` (`EnqueueStorageWrite`).
- A lockout saved just before a hard kill (not a clean exit, so no drain) is now lost, where the synchronous write survived. The window is the executor's latency (milliseconds), and the lockout is a client-side convenience the server should enforce anyway (api-needs #15). Worth one sentence in the A4 note.

#### What's done well
- The coder stopped A4 at the boundary where it stopped being mechanical, and wrote down why, instead of forcing `PersistDeviceCard` async and silently changing when `StorageId` becomes valid.
- The A1 sweep caught generator scripts that would have recreated a duplicate type in Common. That is a runtime-free regression a build would never flag.
- The CI gate parses the SDK's JSON rather than scraping the table, and was run locally before being committed to the workflow.
- The CLAUDE.md fix extended to the `/reviewer` skill's own copy of the same wrong claim, so future reviews stop repeating it.

#### Verdict
Ship: yes, for Phase 4. There are no code defects, the Round 3 fixes hold, and the two seams most likely to hide trouble (shared Serilog DLLs, reordered storage writes) were checked and are safe. First: (1) the requester's decision on S1, (2) the queued-delete test, (3) the CI failure-branch dry run. For the **branch as a whole**, still outstanding before merge: `/security-review` (it carries elevated installer and uninstaller changes) and QA steps 1–12 on a clean VM, and Phase 5's open questions remain undecided.

_Requester decision, 2026-09-24, closing S1: leave the add-device code as it is (A4b stays synchronous, D6 stays in place) and mark it for revisiting. Comments were added at `AddDeviceCardViewModel.DeviceAdded`, `HomeViewModel.OnDeviceAdded` and the `AddDevice` call in `HomeViewModel.PersistDeviceCard`, each pointing here. They are comments only; a scoped App build passed._

### Round 5 — Ship: no

Reviewed on 2026-09-24. Scope: the uncommitted work after commit `5a2a9cc` ("fixed icon"). It has three parts.
1. **The `/security-review` finding** on the install folder (see the S3 · note row):
   - `InstallAcl.TryCreateNewProtectedDirectory` and `ProtectExistingTree`, plus the new `UnsafeInstallFolderException`;
   - `MainWindow.PrepareInstallFolder`;
   - replace-not-truncate in `CopyDirectoryAsync`;
   - the en/uk `Message_InstallFolderUnsafe`.
2. **Embedded appsettings** for the App (`ConfigurationManager`, csproj) and the DeviceHost (`EmbeddedAppSettings`, csproj, `Program.cs`).
3. **The hidden Firmware section:** `IsFirmwareNavVisible`, the nav counts, `IsSectionAvailable` and both nav buttons.

Tests and docs are covered too.

**Out of scope:** the NuGet bumps and the tray fix, which were committed earlier.

**Evidence:**
- Full Release suite at hand-off: 3,144 passed, 0 failed, 1 skipped.
- The republished payload contains no `appsettings*.json`, and both DLLs carry their embedded resource.
- A scratch probe confirmed the inherit-only reset works. Writing a fresh `DirectorySecurity`/`FileSecurity` with no rules and an unprotected DACL leaves only inherited ACEs: an explicit `Everyone:Modify` on a child was dropped, and no NULL DACL appears. This rules out the worst case for `ResetChildren`.

#### Plan conformance
There is no dedicated plan. This work is recorded by the dated S3 · note, S6 · note and D · note rows, which are accurate except where B1 shows otherwise.
- **Security fix, all three recommendations implemented:**
  - create fresh with the protected DACL;
  - walk and reset the tree before the copy, refusing links;
  - delete, then create new files.
- **Embedded settings:** implemented for both processes. The DeviceHost keeps source order, so environment variables still override the embedded values.
- **Firmware:** hidden behind one constant, which also closes the only other route in (`SelectSection` goes through `IsSectionAvailable`; no other code sets `ActiveSection = Firmware`).
- **Missing / partial:** the App's *second* settings reader was not moved (B1).
- **Recorded residual, correctly flagged in the S3 · note:** a custom location whose ancestor a standard user controls can still be renamed. The default location is unaffected.

#### Code
- [x] **B1. `AppSettings.Load` still reads `appsettings.json` from disk, so three repository settings now silently do nothing.** `src/ProoveGaming.App/AppSettings.cs:21`. Blocker.
  **Problem:** the App has two settings readers.
  - Only `ConfigurationManager` (`ApiSettings`) was moved to the embedded resource.
  - `AppSettings.Load()`, called from `Program.cs:24`, still does `AddJsonFile("appsettings.json", optional: true)` from `AppContext.BaseDirectory`, for:
    - `MinimizeToTrayEnabled`;
    - `SessionTimeHours`;
    - `SessionExpiryWarningMinutes`.
  - Now that the file is no longer shipped, `optional: true` quietly finds nothing and the code defaults apply.
  - The repository file sets `SessionTimeHours: 48` and `SessionExpiryWarningMinutes: 180`, which equal those defaults. That is the only reason nothing looks broken.

  **Why it blocks:** the contract of this change is "change the settings in the repository, and an install carries them".
  - For these keys that is false.
  - QA follows the guest-session plan, where the note added this round says "change `src/ProoveGaming.App/appsettings.json` in the repository and rebuild". They set `SessionTimeHours: 1`, rebuild, and still wait 48 hours, with no error anywhere.
  - The same applies to anyone turning off minimize-to-tray.
  - The App also still *reads* a file beside the EXE if one appears, which contradicts the `ConfigurationManager` comment and tests ("no file on disk is read at all"). An install folder is admin-only now, so that part is consistency rather than exposure.

  **Solution options:**
  - A (correct): read both from the one embedded document. Give the App one way to open it (for example an internal static `ConfigurationManager.OpenEmbeddedSettings()` that both use). Replace `AddJsonFile` in `AppSettings.Load` with `AddJsonStream`. Add a test that `AppSettings.Load()` picks up the embedded `SessionTimeHours`, for example by asserting it equals the value in the linked `appsettings.App.json`, and that a file beside the EXE is ignored.
  - B (quick): drop the three keys from `appsettings.json` and document that they are code constants. This is simpler, but it loses the QA lever the guest-session plan relies on.

- [x] **S1. `PrepareInstallFolder` has no test.** `src/ProoveGaming.Installer/Views/MainWindow.Installation.cs:852`. Should-fix.
  **Problem / why it matters:** this is the step that actually closes the finding. It has three branches:
  - reuse an existing install;
  - remove a pre-existing empty folder and recreate it fresh;
  - refuse when something reappears at the path.

  None of them is exercised: the tests cover only the `InstallAcl` building blocks. A later edit that, for example, dropped the `Directory.Delete` so a pre-existing folder was "reused", or checked `Directory.Exists` instead of `IsProoveGamingInstall`, would reopen the privilege escalation with every test green. All three branches can run unelevated, because `TryCreateNewProtectedDirectory` needs no elevation and `TestFolders.CreateInstallLayout` builds a fake install.
  **Fix:** make it an `internal static bool PrepareInstallFolder(string installPath, bool preexisted, Func<string, bool> isProoveGamingInstall)` that returns the new "pre-existed" value, following `GetInstallLocationErrorKey`'s injectable-predicate pattern. Pin these cases in `ProoveGaming.Installer.Tests`:
  - an existing install is left in place and returns true;
  - a pre-existing empty folder comes back as a new protected folder and returns false;
  - a non-empty non-install folder throws and is left in place;
  - a path that is already present when the fresh create runs throws `UnsafeInstallFolderException`.

#### Tests
- [ ] The elevated-only `ProtectExistingTree_TakesBackOwnershipOfEverythingAlreadyInside` has not run yet on any machine; it returned early on every local run. CI (elevated) is its first real run. Confirm the job shows it executed, not just passed.
- [x] See B1: `AppSettings.Load()` reads the embedded values. See S1: the three `PrepareInstallFolder` branches.

#### Nits
- The `UnsafeInstallFolderException` message always says "contains a link", but `PrepareInstallFolder` also throws it when something reappears at the path. It is not shown to users (the localized message covers both), but it is misleading in logs. — `src/ProoveGaming.Common/Classes/InstallAcl.cs:221`
- A pre-existing *empty* folder the user made is now removed and recreated, so `_installFolderPreexisted` flips to false and a cancelled install deletes it rather than leaving it. This is harmless (it was empty), but worth a line in the S3 · note so nobody reads it as a rollback bug. — `MainWindow.Installation.cs:852`
- `File.Delete` then `CreateNew` fails if another process holds the old file open with `FILE_SHARE_DELETE`, because the delete stays pending. That is intended for a stale attacker handle, but an antivirus scan of an old DLL at upgrade time would do the same and turn it into an intermittent "access denied". This is not reproduced; watch for it in upgrade QA, and if it shows up, add a short retry around the pair. — `MainWindow.Installation.cs:901`

#### What's done well
- **Proof of creation.** `TryCreateNewProtectedDirectory` fails if anything already exists at the path, which is the only way to know no other principal owns the folder or holds a handle to it. `Directory.Create(security)` could not give that guarantee.
- **Nothing changes before a refusal.** `ProtectExistingTree` looks for links across the whole tree *before* changing anything, so a rejected folder is left exactly as it was, and that is tested unelevated.
- **DeviceHost source order preserved.** `EmbeddedAppSettings` replaces the file sources in place, using memory sources (reloadable, unlike streams), so environment variables still override the embedded values, and a test pins it.
- **One switch for Firmware.** A single constant hides both nav entries, blocks `SelectSection` and fixes both backdrop counts, and the nav tests model it rather than hard-coding "Firmware".

#### Verdict
Ship: no. The install-folder fix and the Firmware hide are sound. The embedded-settings change is incomplete: `AppSettings.Load` still reads the file that no longer ships, so three settings silently fall back to defaults. First:
1. B1: move `AppSettings.Load` onto the embedded document, with a test.
2. S1: extract and test `PrepareInstallFolder`.
3. Confirm the elevated ACL test actually runs in CI.

Then rebuild the installer.

### Round 6 — Ship: yes

Reviewed on 2026-09-24. Scope: commit `55a1342` ("latest.testing"), which carries the Round 5 fixes along with the Round 5 work itself. The working tree adds only the staged journal ticks in this plan. Nothing else is uncommitted.

**Evidence:**
- Full Release suite after the fixes: 3,149 passed, 0 failed, 1 skipped. Installer tests went from 132 to 137 (the new `InstallFolderPreparationTests`), and App tests from 1,861 to 1,862 (the new `AppSettings.Load` test).
- The installer published at 23:08 from this code has no `appsettings*.json` in its payload.

#### Plan conformance
- **Round 5 B1, closed.** `AppSettings.Load` (`src/ProoveGaming.App/AppSettings.cs`) now reads `ConfigurationManager.OpenEmbeddedSettings()`, the same embedded document `ConfigurationManager` uses, so there is one opener and nothing is read from disk. `AppSettingsLoad_ReadsTheEmbeddedDocument_NotAFileNextToTheExe` plants `SessionTimeHours: 1` beside the test EXE and asserts the repository values win. It would fail against the old `AddJsonFile` code, and it restores the statics it touches.
- **Round 5 S1, closed.** `PrepareInstallFolder` (`MainWindow.Installation.cs:857`) is now `internal static`, with the install check injected, and the caller assigns its return value to `_installFolderPreexisted`. The five tests cover all three branches plus a missing parent and a reappearance, unelevated:
  - an existing install is reused;
  - a pre-existing empty folder is recreated protected;
  - a folder no longer empty stops the install and is left alone;
  - a new path, including a missing parent, is created protected;
  - something already at the path throws `UnsafeInstallFolderException`.
- **Round 5 nits:**
  - the exception message is corrected;
  - the rollback note is added to the S3 · note;
  - the antivirus retry is still a watch item, unchanged.
- **S3 · note and S6 · note:** accurate as amended.

#### Code
- none. One case was checked and found harmless: if `PrepareInstallFolder` throws after the non-recursive delete, `_installFolderPreexisted` keeps its earlier `true`. Rollback runs only on cancellation, and the folder it removed was empty, so nothing is left wrongly deleted or wrongly kept.

#### Tests
- [ ] Still open from Round 5: confirm the elevated CI run *executes* `ProtectExistingTree_TakesBackOwnershipOfEverythingAlreadyInside`. It returns early when not elevated, so a green run alone does not prove it ran. Look for its duration in the log, or temporarily make the non-admin branch skip visibly.

#### Nits
- `using System;` in `src/ProoveGaming.App/AppSettings.cs` is now unused, since `AppContext` is no longer referenced.
- Unchanged from Round 5: delete-then-create can fail intermittently if antivirus holds an old file open with share-delete during an upgrade. Watch for it in upgrade QA.

#### What's done well
- **One opener for both readers.** B1 was fixed at the source: the two App settings readers now share a single embedded-document opener, not a second copy of the loading code.
- **The extraction follows existing practice.** `PrepareInstallFolder` uses the same injectable-predicate pattern as `GetInstallLocationErrorKey`, so its tests read like the neighbouring `InstallLocationValidationTests`.

#### Verdict
Ship: yes. Round 5's blocker and should-fix are closed and tested, the suite is green, and the published installer matches this code. Before merge:
1. Run QA on the 23:08 installer:
   - a custom install location;
   - no settings files in the install folder;
   - sign-in works and devices appear;
   - Firmware is gone from the menu;
   - the tray close → restore → close cycle.
2. Confirm the elevated ACL test executes in CI.
3. Commit the staged journal ticks.
