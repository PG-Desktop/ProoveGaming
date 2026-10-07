# "Files go missing / app will not start" — what can cause it

**Date:** 2026-09-10
**Reported by:** tester — the uninstaller and other files disappear from time to time, and the app
then fails to start.
**Crash:** `System.TypeInitializationException` -> `System.DllNotFoundException: Unable to load DLL
'libSkiaSharp' or one of its dependencies (0x8007007E)`, at `Avalonia.Skia.SkiaPlatform.Initialize`.
**Install path in the report:** `C:\Users\user\Desktop\ProoveGaming`.

## What the crash actually tells us

`0x8007007E` is `ERROR_MOD_NOT_FOUND`. That message is ambiguous by default — it fires both when the
DLL is missing *and* when one of **its** dependencies is missing. So the first job was to rule the
second one out.

`libSkiaSharp.dll` (SkiaSharp 3.119.4, x64, 11,628,896 bytes) was disassembled from our own payload.
Its **entire import table** is:

```
FONTSUB.dll   KERNEL32.dll   USER32.dll   ole32.dll
```

All four are Windows system DLLs present on every Windows install. It is statically linked against
the CRT and needs **no VC++ redistributable**.

**Therefore: a missing Visual C++ runtime is ruled out. The file itself is not there (or cannot be
read) at load time.** Combined with the tester also losing the uninstaller, the real problem is not
a crash — it is **files being removed from the install folder after a successful install**.

## What we ship, and why it is exposed

| Fact | Consequence |
| --- | --- |
| **Nothing is code-signed.** No `signtool`, certificate or signing step exists in any `.csproj` or in CI. | Every binary is unsigned and has zero reputation with SmartScreen and AV. |
| The **App** publishes `--self-contained` but **not** single-file (`ProoveGaming.Installer.csproj` line 135). | The install folder holds ~421 loose files, including large unsigned **native** DLLs (`libSkiaSharp.dll` 11 MB, `av_libglesv2.dll` 5 MB, `e_sqlite3.dll` 2 MB). Each can be quarantined individually, leaving everything else in place — which is exactly the reported symptom. |
| The **Uninstaller** publishes `SelfContained` + `PublishSingleFile`. | An unsigned single-file self-extracting EXE is one of the strongest "packer" heuristics an AV has. That it is specifically the uninstaller the tester loses fits this. |

`libSkiaSharp.dll` in particular is a long-standing false-positive magnet: a very large unsigned
native binary that no AV vendor has reputation data for.

## The three plausible causes, and how to tell them apart

### 0. The uninstaller's detached self-delete loop — MOST LIKELY, and it is our bug

**This was found by challenging the "reboot between uninstall and reinstall" advice below, and it
supersedes it. A reboot does not fix this one.**

`UninstallService.ScheduleSelfDelete` spawns a **detached PowerShell** (falling back to `cmd.exe`)
that outlives the uninstaller:

```powershell
$pidToWait=<uninstaller pid>
while (Get-Process -Id $pidToWait) { Start-Sleep -Milliseconds 500 }
for ($i = 0; $i -lt 240; $i++) {
  try {
    ... Remove-Item -LiteralPath '<installPath>' -Recurse -Force -ErrorAction Stop
    if (-not (Test-Path -LiteralPath '<installPath>')) { break }
  } catch { Start-Sleep -Milliseconds 500 }
}
```

It **only stops when the install folder no longer exists.** If any file is still locked — the
DeviceHost service, the app, or simply an AV scanning 421 freshly-written files — the delete keeps
failing and the loop keeps retrying for **240 iterations, at least ~2 minutes**, longer because each
recursive delete attempt over a large folder takes real time. The `cmd.exe` fallback loops 60 times
at ~1 s each.

**The race:** the loop deletes *a path*. It has no idea whether the folder at that path is the old
install or a **new one**. So:

1. User uninstalls. Something holds a file open, so the delete fails and the loop keeps running.
2. User reinstalls to the same folder straight away — which is exactly what a tester does.
3. The installer copies 421 files in.
4. The still-running loop takes its next pass and deletes what it can.

If step 4 lands **during** step 3, the result is a **partially deleted install** — some files gone,
most present. That is precisely the reported symptom, and it explains the partial loss better than
antivirus does, because AV quarantines specific detections rather than an arbitrary slice of a
folder. The app then dies on whichever native DLL was caught, which is usually the biggest and
slowest-to-copy one: `libSkiaSharp.dll`.

**Nothing guards against it.** The installer stops the app and the DeviceHost service before copying
(`CloseRunningAppAsync`, `StopDeviceHostServiceAsync`) and clears the reboot schedule — but it never
looks for a lingering self-delete process. `ClearPendingRebootDeletions` does not help here either:
this mechanism is a live process, not a registry entry.

**Confirm:** while the install is broken, or right after an uninstall, run
`Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='cmd.exe'" | Select-Object ProcessId, CommandLine`
and look for one whose command line references the install path. The PowerShell one is
`-EncodedCommand`, so decode it with
`[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('<blob>'))`.

**Workaround on builds before the fix:** after uninstalling, **wait for the install folder to disappear** before
reinstalling — or confirm no `powershell.exe` / `cmd.exe` is still holding that path. Rebooting also
works, but only because it kills the loop; it is not the mechanism.

**FIXED 2026-09-10.** Both mitigations shipped together, because either alone leaves a window:
removing the marker stops the loop on its *next* iteration but not one already inside `Remove-Item`;
killing the process stops that in-flight pass but only if we can identify it.

| Piece | Where |
| --- | --- |
| `UninstallHandoff` — marker path (SHA-256 of the fully-resolved install path, in `%ProgramData%`), write/read/remove, and the `ShouldStopSelfDeleteProcess` guard | `src/ProoveGaming.Common/Classes/UninstallHandoff.cs` |
| Uninstaller writes the marker with the self-delete pid; both the PowerShell and `cmd` loops exit as soon as it is gone, and delete it on the way out | `src/ProoveGaming.Uninstaller/UninstallService.cs` |
| Installer stops the pid named in the marker, then removes the marker, before copying a single file | `MainWindow.Installation.cs` — `StopLingeringUninstallCleanup`, called alongside the existing app/service stops |
| 26 tests pinning the handoff contract, the kill guard and both emitted scripts | `tests/ProoveGaming.Installer.Tests/UninstallHandoffTests.cs` |

Three design points worth keeping:

- **The marker lives outside the install folder.** Inside, the loop's own recursive delete would
  remove its own marker partway through a legitimate cleanup, the loop would stop early, and an
  uninstall that just needed another retry would leave files behind — trading this bug for a worse
  one. It is in `%ProgramData%` — **not** `%TEMP%`, which is profile-relative: the uninstaller, its
  detached loop and the installer each elevate independently, so a standard user elevating with two
  different administrator accounts would get two different temp folders and the handoff would
  silently never meet. It is keyed by a SHA-256 of the **fully resolved** install path (not
  `string.GetHashCode`, which is randomised per process, and via `Path.GetFullPath` so that
  `C:/Games/PG` and `C:\Games\PG` agree).
- **A marker that cannot be written degrades to the old behaviour, not to a broken one.** `Write`
  reports failure and the uninstaller then emits the loop **unguarded**. A guard whose marker is
  missing would break on its first pass and delete nothing, so a failed best-effort write would
  otherwise turn into an uninstall that silently leaves the whole folder behind. Losing the race
  protection is the intended degradation; losing the cleanup is not.
- **The marker is written before the self-delete process starts**, with the pid filled in
  afterwards, so the loop can never observe a missing marker and quit immediately. `TryRead` treats
  pid 0 as "nothing to stop".
- **The kill is guarded against pid reuse on three independent signals** (`ShouldStopSelfDeleteProcess`):
  the marker must be younger than 30 minutes, the process must look like the self-delete shell
  (`powershell`/`cmd`), and it must have started within a bounded window **either side** of the
  marker's timestamp. A lower bound alone would admit every process started later, which is exactly
  the stale-marker case — a marker outlives an interrupted loop, Windows recycles the id, and the
  elevated installer would kill an unrelated shell. A marker with no parseable timestamp now stops
  the kill rather than skipping the check. The kill uses `entireProcessTree: false`, matching the
  documented convention elsewhere in that file: a recorded id is a hint, so a wrong guess must cost
  one process rather than a tree.

Verified by decoding the emitted `-EncodedCommand` and running it: with no marker present the loop
exits in **44 ms** instead of grinding for ~2 minutes, and never touches the path.

### 1. Antivirus quarantine — also plausible

Fits every detail: intermittent ("time to time"), post-install, removes individual files while the
rest of the install survives, and targets exactly the two file shapes AV dislikes most (a large
unsigned native DLL and an unsigned self-extracting EXE).

AV normally lets the installer's write succeed and quarantines **afterwards**, asynchronously — so
the install reports success and the files vanish minutes or hours later. That is why the installer
never reports an error.

**Confirm:** section 3 of the diagnostic script. Defender's history is readable via
`Get-MpThreatDetection` and event IDs 1116/1117; a third-party AV needs its own quarantine log
checked by hand.

### 2. OneDrive placeholders — very likely given the path

The tester installed to **`C:\Users\user\Desktop\ProoveGaming`**. Desktop is one of the folders
OneDrive's "Back up your folders" (Known Folder Move) redirects and syncs.

With Files On-Demand, OneDrive can **dehydrate** a file to a cloud-only placeholder. The file still
appears in Explorer at full size, but its contents are not on disk. `LoadLibrary` on a placeholder
that cannot be re-hydrated fails with **exactly `0x8007007E`** — the same error, with the file
apparently present. OneDrive can also move or remove files during sync conflicts.

**Confirm:** section 2 of the script checks `FILE_ATTRIBUTE_OFFLINE` (0x1000) and
`RECALL_ON_DATA_ACCESS` (0x400000) on every file in the install folder.

This is worth checking **first**, because it is free to rule out and the path makes it likely.

### 3. Reboot-scheduled deletion — **THIS ONE FIRED. Mechanism removed 2026-09-15.**

> **Update 2026-09-15.** A second report — same crash, same DLLs — was traced here. The audit below
> called this cause "sound"; it was looking at the installer's guard, and the bug was in the
> uninstaller.
>
> **Evidence.** Install 2026-09-14 16:52:57, all 394 root files written and byte-identical to the
> payload. Install directory `LastWriteTime` 2026-09-15 **00:01:37** — the early-boot window, not
> install time. Missing afterwards: `libSkiaSharp.dll`, `libHarfBuzzSharp.dll`, `av_libglesv2.dll`,
> `ProoveGaming.Uninstaller.exe`, nothing else. `PendingFileRenameOperations` absent (consumed by
> that boot). Defender: zero detections, only AV, realtime on. The install was complete and correct;
> the boot removed the files.
>
> **Root cause.** `RemoveInstallDirectory` called `ScheduleDeleteOnReboot(installPath)` after its
> delete passes. That call enumerated what was **still on disk**, so it condemned only the files the
> passes could not remove. `MOVEFILE_DELAY_UNTIL_REBOOT` condemns a *path*, so those condemnations
> outlived the files and were inherited by whatever occupied the paths at the next boot: a fresh
> install.
>
> **Why it was always the same four files.** The uninstaller is itself an Avalonia app, published
> single-file with `IncludeNativeLibrariesForSelfExtract=false`
> (`ProoveGaming.Uninstaller.csproj`), so its native dependencies are **not** bundled into the EXE —
> its publish output is exactly four files:
>
> ```
> ProoveGaming.Uninstaller.exe   libSkiaSharp.dll   libHarfBuzzSharp.dll   av_libglesv2.dll
> ```
>
> Only the EXE is copied into the install folder. At run time it therefore loads those three native
> DLLs **out of the install folder** — the App's copies — and holds them open for as long as its
> window is up. Those three plus its own running EXE are the only files the delete passes can never
> remove, so they are the only ones ever scheduled. They are exactly the four that vanished, and the
> incident report's "nothing else" is the confirmation: had the whole folder been condemned, the
> boot would have wiped the entire fresh install rather than four files.
>
> This is deterministic, not a race: `Directory.Delete(installPath, recursive: true)` can never
> succeed while the uninstaller runs from inside that folder, so the `catch` branch is the **normal**
> path and every uninstall condemned those four paths. The detached self-delete loop then removed
> all four seconds later, exactly as designed — leaving four live condemnations and nothing to
> delete.
>
> **The load-bearing fact for anyone touching the uninstall flow:** the uninstaller locks the App's
> Skia/HarfBuzz/ANGLE DLLs simply by running. That is why the recursive delete always fails.
>
> **Fix: the mechanism is gone.** `MoveFileEx`, `SchedulePathDeleteOnReboot`, `ScheduleDeleteOnReboot`
> and the `scheduleOnRebootOnFailure` plumbing were deleted from `UninstallService`. A desktop
> utility for mice and keyboards should never need a restart to finish uninstalling. Transient locks
> are already handled by the detached self-delete loop, which retries the folder for ~2 minutes —
> that retry is what actually cleared the lock in this incident, seconds after the schedule was
> written. A file that still will not delete is now simply left in place.
>
> `DeleteUserDataFolder` had refused to schedule user data since long before this, with a comment
> giving exactly this reasoning. The install folder never got the same treatment.
>
> **Accepted limitation.** Where the DeviceHost service hangs (`sc stop` fails, e.g. FAILED 1053),
> `ProoveGaming.DeviceHost.exe` keeps running until the next boot and holds `host\` open for longer
> than the self-delete loop lives — 240 retries over ~2 minutes for the PowerShell loop, 60 over
> ~1 minute for the `cmd` fallback. That folder is now left on disk permanently, with no entry in
> Apps &amp; features; previously the reboot schedule removed it. This is the deliberate trade: a
> visible leftover folder instead of a standing order that can delete a future install. The leftover
> paths are listed in `%TEMP%\proove-uninstall.log`. Note the hung-service case *already* depended on
> a reboot before this change — `sc delete` only marks a running service for deletion — so it is not
> a new restart dependency, it is an existing one that the file cleanup no longer papers over.
>
> **`ClearPendingRebootDeletions` in the installer stays**: the uninstaller a user runs is the OLD
> one, sitting in the folder being replaced, so every build already in the field still writes these
> entries. It can be removed once those are gone.
>
> ### Residual risk — not closed by this change
>
> **Everyone currently running a build from before 2026-09-15 is still exposed.** Their uninstall
> path is the old code, so the sequence *uninstall (old build) → install new build without
> rebooting → reboot* is protected by `ClearPendingRebootDeletions` **alone** — and that guard
> already failed once in the field, for reasons that cannot be established because the registry
> value was consumed by the boot that used it and the guard's `catch` recorded nothing.
>
> The catch now logs to `%TEMP%\proove-install.log`, so a second occurrence leaves evidence. That is
> mitigation, not a fix: the removal of the cause only protects uninstalls performed by builds that
> already contain it. Two things worth doing before treating this as closed:
>
> - reproduce on a VM (old build → uninstall → install new build, no reboot) and read
>   `PendingFileRenameOperations` before and after the guard runs;
> - check `PendingFileRenameOperations2` — Session Manager also processes it, and the guard reads
>   only the first value.

#### The original 2026-09-10 audit, kept as written

The uninstaller cannot delete files that are in use, so it falls back to
`MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)`, which records paths in
`HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\PendingFileRenameOperations`. That schedule
survives a reinstall — so uninstall, reinstall, then reboot, and Windows deletes the **new** files.

We already handle this: `ClearPendingRebootDeletions` in `MainWindow.Installation.cs`. It was
audited during this investigation and is **sound**:

- called before the file copy (line 84), so the new files are not already condemned;
- the installer always elevates in Release (`Program.cs`: `requireElevation = !Debugger.IsAttached`),
  so it has the HKLM write access it needs;
- the `REG_MULTI_SZ` (source, destination) pairing was **empirically verified** — .NET's
  `Microsoft.Win32.Registry` preserves the embedded empty strings that mark a delete entry, so the
  `i += 2` walk does not misalign. (This was a real suspicion; it is not a bug.)

**The remaining gap:** it only clears entries pointing at *the folder being installed into*. Uninstall
from one folder, reinstall to a **different** folder, then reboot, and the old schedule still fires.
That does not explain this report (same folder), but it is a real edge.

**Confirm:** section 4 of the script.

## Ruled out

- **Missing VC++ runtime** — proven above by the import table.
- **A partial/failed install.** `CopyDirectoryAsync` lets exceptions propagate to the install error
  handlers, so a copy that fails would surface as a visible install failure, not a silent one.
- ~~**Our own code deleting files.** The only deletion path we own is the pending-rename mechanism
  above, and it audits clean.~~ — **wrong, corrected 2026-09-15.** It was our own code: the
  uninstaller condemned every file in the install folder on every uninstall. See cause 3. The audit
  checked whether the installer *cleared* the schedule correctly and never asked whether the
  uninstaller should have been *writing* it.

## How to diagnose it

```
powershell -ExecutionPolicy Bypass -File docs\support\diagnose-missing-files.ps1
powershell -ExecutionPolicy Bypass -File docs\support\diagnose-missing-files.ps1 -InstallPath "C:\Users\user\Desktop\ProoveGaming"
```

Read-only, changes nothing, and finishes with a verdict naming which cause applies. Ask the tester
to run it **while the install is broken** — after a reinstall the evidence is gone.

## Recommendations

**Immediate, no code change**

1. **Install to `Program Files`, not the Desktop.** It is already the installer's default; the
   tester overrode it. Program Files is not OneDrive-synced and is better protected.
2. Add the install folder as an **AV exclusion** on test machines, and check the AV's quarantine
   before reinstalling.
3. If a reinstall is needed after an uninstall, **wait until the install folder actually
   disappears** first (see cause 0). Rebooting also works, but only because it kills the
   lingering self-delete process — waiting is the real requirement.

**Worth doing in the product**

4. **Code-sign the binaries.** This is the actual fix for cause 1, and nothing else removes it — an
   unsigned installer plus unsigned native DLLs will keep tripping AV and SmartScreen for every
   user, not just this tester. It needs an OV/EV certificate and a signing step in `release.yml`.
   *This is a purchasing decision, so it is raised here rather than assumed.*
5. **Verify the install.** The installer currently copies and never checks. Writing a manifest
   (relative path + size, or hash) at install time and verifying it at App startup would turn a
   `DllNotFoundException` crash into "installation is damaged — repair?".
6. **Fail gracefully.** `ProoveGaming.App.Program.Main` has no `try`/`catch`, so a missing native DLL
   produces the raw .NET crash dialog the tester pasted. Catching startup failure and naming the
   missing file would save a round trip every time this happens.
7. **Warn on risky install paths.** The installer validates the path already; adding a warning when
   it is under a OneDrive-synced folder (Desktop/Documents, or `%OneDrive%`) would prevent cause 2
   entirely.
8. ~~Kill any lingering self-delete process before copying~~ — **done 2026-09-10**, see cause 0.
9. ~~**Widen the pending-delete clear** to drop *any* entry naming our app, not only ones inside the
   folder being installed into.~~ — **dropped 2026-09-15.** Obsolete: the uninstaller no longer
   writes these entries at all (cause 3), so there is nothing new to widen the clear against.
   Widening a destructive, elevated HKLM write by app *name* would also have let any path merely
   containing "ProoveGaming" drag another vendor's pending operation out of a shared value.

Items 4-7 are not implemented — they are proposals, listed so the trade-offs are visible.
