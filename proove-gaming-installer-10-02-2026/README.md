# proove-gaming

Proove Gaming is an Avalonia-based desktop solution targeting **.NET 10**.

## Solution overview

Main projects in `src/`:

- `ProoveGaming.App` — the desktop application
- `ProoveGaming.Installer` — custom installer (Avalonia UI)
- `ProoveGaming.Uninstaller` — dedicated uninstaller executable
- `ProoveGaming.Common` — shared UI/layout/localization components
- `ProoveGaming.Infrastructure` — infrastructure layer
- `ProoveGaming.DeviceHost` — device host service
- `ProoveGaming.GrpcDependencies` — gRPC dependency layer
- `ProoveGaming.Device.Testing` — developer console of hardware protocol probes (not shipped)
- `ProoveGaming.DeviceHost.Checker` — developer console that polls the DeviceHost's gRPC endpoint (not shipped)

Test projects in `tests/` (xUnit, all included in `src/ProveGaming.slnx`):

- `ProoveGaming.App.Tests`
- `ProoveGaming.Infrastructure.Tests` (also covers the DeviceHost)
- `ProoveGaming.Installer.Tests` (installer and uninstaller)

How the pieces fit together:

- **App → DeviceHost** over gRPC on the named pipe `\\.\pipe\ProoveGaming.DeviceHost`, which only SYSTEM, Administrators and signed-in users can open. The DeviceHost is a `LocalSystem` Windows service that owns all HID and Bluetooth device I/O; the contract is `ProoveGaming.GrpcDependencies/Protos/deviceservice.proto`. A Release App only talks to the installed service, so when running the DeviceHost from source, use a Debug build of the App.
- **App → backend** over HTTPS for sign-in, profile and settings sync.
- **Installer / Uninstaller**: separate Avalonia executables. The installer embeds the App, the DeviceHost and the uninstaller.

See `CLAUDE.md` for the architecture in more detail.

Installer UI files are organized under:

- `ProoveGaming.Installer/Views/`

## Local development

Run from repository root:

- Restore solution:
  - `dotnet restore ./src/ProveGaming.slnx`
- Build solution:
  - `dotnet build ./src/ProveGaming.slnx`

### Test

- All tests: `dotnet test ./src/ProveGaming.slnx`
- One project: `dotnet test ./tests/ProoveGaming.App.Tests/ProoveGaming.App.Tests.csproj`
- One class or method: `dotnet test ./tests/ProoveGaming.App.Tests/ProoveGaming.App.Tests.csproj --filter "FullyQualifiedName~SessionServiceTests"`

CI runs the suite in **Release**; check timing-sensitive changes with `-c Release` before pushing.

### Build app only

- `dotnet build ./src/ProoveGaming.App/ProoveGaming.App.csproj -c Release`

### Build installer only

- `dotnet build ./src/ProoveGaming.Installer/ProoveGaming.Installer.csproj -c Release`

### Build uninstaller only

- `dotnet build ./src/ProoveGaming.Uninstaller/ProoveGaming.Uninstaller.csproj -c Release`

## Installer publishing (single EXE)

The installer is configured to produce a **single Windows EXE** in Release publish.

Command:

- `dotnet publish ./src/ProoveGaming.Installer/ProoveGaming.Installer.csproj -c Release`

Output:

- `./src/ProoveGaming.Installer/bin/Release/net10.0/win-x64/publish/ProoveGaming.Installer.exe`

During Release build/publish, installer payload is prepared from `ProoveGaming.App` and embedded into installer.
During Release build/publish, a dedicated `ProoveGaming.Uninstaller.exe` is also produced and embedded into the installer, then copied to the install directory as `ProoveGaming.Uninstaller.exe`.

## Testing release via GitHub Actions (GitHub Releases)

For testing builds, use the manual GitHub Actions workflow (`installer-release`). It publishes the installer as an asset on a **GitHub pre-release** (not an Actions artifact), so builds are not bound by the Actions artifact-storage quota.

Steps:

1. Open repository in GitHub: `wezom-company/proove-gaming`
2. Go to **Actions** tab
3. Select workflow: **installer-release**
4. Click **Run workflow** (branch: `main` or required branch)
5. Wait until workflow completes
6. Go to the **Releases** section and open today's pre-release (tagged `installer-<MM-DD-YYYY>`)
7. Download the desired asset and run the extracted installer:
   - `proove-gaming-installer-<MM-DD-YYYY>.<N>.zip` → `ProoveGaming.Installer.exe`

Notes:

- This is currently a temporary testing distribution path; builds are marked **pre-release** while in the dev phase.
- There is **one release per calendar day**, tagged `installer-<MM-DD-YYYY>` and titled with the date. Each run that day attaches a new asset `proove-gaming-installer-<MM-DD-YYYY>.<N>.zip`, where `<N>` increments (`.1`, `.2`, …) — so a single dated version holds all of that day's builds.
- Each asset is a ZIP containing the single-file installer EXE from the Release publish.
- The daily release is tagged at the commit of the **first** build that day; later builds add assets to the same release.

## Installer features

Current installer supports:

- Multi-step custom setup UI (welcome, agreement, install folder, progress, completion)
- Install folder selection
- Disk space check before install
- Optional checkboxes (enabled by default):
  - Desktop shortcut
  - Start Menu shortcut
- Final-step checkbox to run app after install (enabled by default)
- Windows uninstall registration using a dedicated uninstaller executable (`ProoveGaming.Uninstaller.exe`)
  - Registered **per machine** (`HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall\ProoveGaming`), so every user of the PC sees it in Settings → Apps. Builds before 2026-09-24 registered per user (HKCU); the new installer removes that old entry.
- Dedicated uninstaller works both from OS Apps removal and by directly running `ProoveGaming.Uninstaller.exe` from the install folder
- Auto-elevation prompt on Windows (UAC) when admin rights are required

Security rules the installer and uninstaller enforce (they run with administrator rights, so nothing a standard user can write is trusted):

- The install folder must be new, empty, or an existing Proove Gaming install, and never a drive root or a Windows, Program Files, ProgramData or user-profile folder.
- A custom install folder is created fresh by the installer, with its locked-down permissions on it from the moment it exists (an existing empty folder is replaced). Before any file is copied, everything already inside is made Administrators-owned, and the install stops if the folder contains a link (junction or symbolic link).
- The install folder gets its own permissions: administrators and SYSTEM have full control, users can only read and run files. This makes a custom location (for example `D:\Games\ProoveGaming`) as safe as Program Files for the `LocalSystem` DeviceHost service that runs from it.
- The payload is unpacked into a fresh administrators-only folder under `%ProgramData%` (not `%TEMP%`) before it is copied.
- Windows tools (`sc.exe`, `powershell.exe`, `cmd.exe`) are always started from `System32` by full path.
- The uninstaller only removes a folder named by the per-machine uninstall entry, or the folder it runs from, and only after checking it is really a Proove Gaming install. The folder, and at least one of `ProoveGaming.App.exe`, `ProoveGaming.Uninstaller.exe` or `host\ProoveGaming.DeviceHost.exe` in it, must be owned by Administrators or SYSTEM, or sit inside Program Files; a standard user can fake neither. Otherwise it deletes nothing, shows "Nothing Was Removed", and logs why in `%TEMP%\proove-uninstall.log`.
- A damaged install (for example an EXE quarantined by antivirus) still counts as an install, so it can be repaired by reinstalling into the same folder or removed by uninstalling.
- Payload staging folders left behind by an installer that was killed mid-install are removed the next time the installer runs (only folders the installer itself created, more than an hour old).

## Logs

- **DeviceHost service:** `<install folder>\host\logs\log<date>.txt` (for a default install, `C:\Program Files\ProoveGaming\host\logs\`). Daily files, 7 kept. When running the host from source, the same `logs\` folder sits next to the built `ProoveGaming.DeviceHost.exe`.
- **Uninstaller:** `%TEMP%\proove-uninstall.log` of the account that approved the UAC prompt.
- **App:** `%LocalAppData%\ProoveGaming\Logs\app-<date>.txt`. Daily files, 7 kept, 10 MB each at most. Never contains passwords or sign-in tokens. Removed by the uninstaller's "delete my data" option.
- **Installer:** `%TEMP%\proove-install.log` (written only when clearing reboot-scheduled deletions left by an old uninstaller fails).

## CI

`.github/workflows/dotnet-ci.yml` runs on every push and pull request to `main` and `dev`, on `windows-latest`:

1. Builds the App.
2. Runs the full test suite (`dotnet test ./src/ProveGaming.slnx --configuration Release`).
3. Fails if any direct or transitive package has a High or Critical vulnerability advisory.

The job runs with a read-only repository token.
