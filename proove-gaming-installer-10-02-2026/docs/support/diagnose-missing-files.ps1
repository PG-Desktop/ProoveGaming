# Proove Gaming - diagnose "files went missing / app will not start"
#
# Symptom this is for:
#   System.DllNotFoundException: Unable to load DLL 'libSkiaSharp' ... (0x8007007E)
#   and/or the uninstaller and other files disappearing from the install folder.
#
# It does NOT guess. It checks the four things that can produce that symptom and prints which one
# actually applies on THIS machine:
#
#   1. Are the files really gone, or present but unreadable?
#   2. Is the install folder cloud-synced (OneDrive), leaving files as placeholders?
#   3. Did Defender (or another AV) quarantine anything?
#   4. Is a reboot-scheduled deletion still pointing at the install folder?
#
# READ-ONLY. It changes nothing. Run it in a normal PowerShell; some checks report
# "needs admin" if not elevated, which is fine - the rest still run.
#
#   powershell -ExecutionPolicy Bypass -File diagnose-missing-files.ps1
#   powershell -ExecutionPolicy Bypass -File diagnose-missing-files.ps1 -InstallPath "D:\Games\ProoveGaming"

param(
    [string]$InstallPath = ""
)

$ErrorActionPreference = 'Continue'

function Section($t) {
    Write-Host ""
    Write-Host ("=" * 78)
    Write-Host $t
    Write-Host ("=" * 78)
}

# -- Locate the install ------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($InstallPath)) {
    $candidates = @(
        (Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'ProoveGaming'),
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'ProoveGaming'),
        (Join-Path $env:LOCALAPPDATA 'ProoveGaming')
    )
    $InstallPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

Section "INSTALL LOCATION"
if ([string]::IsNullOrWhiteSpace($InstallPath) -or -not (Test-Path $InstallPath)) {
    Write-Host "  Install folder not found. Re-run with -InstallPath '<folder holding ProoveGaming.App.exe>'."
    Write-Host "  Looked in: Program Files\ProoveGaming, Desktop\ProoveGaming, LocalAppData\ProoveGaming"
    return
}
Write-Host "  $InstallPath"

# A Desktop / Documents install is the single biggest risk factor, because those folders are what
# OneDrive "Back up your folders" redirects and de-hydrates.
$desktop = [Environment]::GetFolderPath('Desktop')
$docs = [Environment]::GetFolderPath('MyDocuments')
if ($InstallPath -like "$desktop*" -or $InstallPath -like "$docs*") {
    Write-Host "  WARNING: this is inside your Desktop/Documents folder." -ForegroundColor Yellow
    Write-Host "           Those are exactly the folders OneDrive backs up and can turn into" -ForegroundColor Yellow
    Write-Host "           cloud-only placeholders. Program Files is the supported location." -ForegroundColor Yellow
}
if ($env:OneDrive -and $InstallPath -like "$env:OneDrive*") {
    Write-Host "  WARNING: this path is INSIDE the OneDrive folder ($env:OneDrive)." -ForegroundColor Red
}

# -- 1. Are the critical files there? ----------------------------------------------------------
Section "1. CRITICAL FILES"

# Native DLLs the app cannot start without. libSkiaSharp is the one in the crash log; it imports
# only Windows system DLLs (verified), so "not found" means the FILE is gone, never a missing
# VC++ runtime.
$critical = @(
    @{ Name = 'ProoveGaming.App.exe'; Size = 0        },
    @{ Name = 'libSkiaSharp.dll';     Size = 11628896 },
    @{ Name = 'av_libglesv2.dll';     Size = 5394096  },
    @{ Name = 'e_sqlite3.dll';        Size = 1978880  }
)

$missing = @()
foreach ($c in $critical) {
    $p = Join-Path $InstallPath $c.Name
    if (-not (Test-Path $p)) {
        Write-Host ("  MISSING   {0}" -f $c.Name) -ForegroundColor Red
        $missing += $c.Name
        continue
    }
    $fi = Get-Item $p -Force
    $note = ""
    if ($c.Size -gt 0 -and $fi.Length -ne $c.Size) {
        $note = " (expected $($c.Size) bytes - TRUNCATED OR REPLACED)"
    }
    Write-Host ("  present   {0,-24} {1,12:N0} bytes{2}" -f $c.Name, $fi.Length, $note)

    # Can it actually be opened? A cloud placeholder or a locked file exists but will not load.
    try {
        $fs = [System.IO.File]::Open($p, 'Open', 'Read', 'Read')
        $fs.Close()
    } catch {
        Write-Host ("            ^ EXISTS BUT CANNOT BE READ: {0}" -f $_.Exception.Message) -ForegroundColor Red
    }
}

$total = (Get-ChildItem $InstallPath -Recurse -File -Force -ErrorAction SilentlyContinue).Count
Write-Host ("  total files in install folder: {0}   (a complete App payload is ~421)" -f $total)

# -- 2. Cloud placeholders ---------------------------------------------------------------------
Section "2. CLOUD / ONEDRIVE PLACEHOLDERS"

# FILE_ATTRIBUTE_OFFLINE (0x1000) and RECALL_ON_DATA_ACCESS (0x400000) mark a file whose contents
# live in the cloud. LoadLibrary on one of those fails when it cannot be re-hydrated - which
# surfaces as exactly the 0x8007007E in the crash log, with the file still visible in Explorer.
$offline = @()
foreach ($f in Get-ChildItem $InstallPath -Recurse -File -Force -ErrorAction SilentlyContinue) {
    $a = [int]$f.Attributes
    if (($a -band 0x1000) -or ($a -band 0x400000)) { $offline += $f.FullName }
}
if ($offline.Count -gt 0) {
    Write-Host ("  {0} file(s) are CLOUD-ONLY placeholders - this is very likely your cause." -f $offline.Count) -ForegroundColor Red
    $offline | Select-Object -First 10 | ForEach-Object { Write-Host "    $_" }
    Write-Host "  FIX: right-click the folder -> 'Always keep on this device', or move the install"
    Write-Host "       out of the synced folder (Program Files)."
} else {
    Write-Host "  None. No cloud placeholders in the install folder."
}

$od = Get-Process OneDrive -ErrorAction SilentlyContinue
Write-Host ("  OneDrive running: {0}" -f $(if ($od) { "yes" } else { "no" }))

# -- 3. Antivirus ------------------------------------------------------------------------------
Section "3. ANTIVIRUS / QUARANTINE"

# Nothing we ship is code-signed, and libSkiaSharp.dll is a large unsigned native binary - a
# well-known false-positive target. The uninstaller is an unsigned single-file self-extracting
# EXE, which trips "packer" heuristics harder still.
try {
    $av = Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction Stop
    foreach ($p in $av) { Write-Host ("  installed AV: {0}" -f $p.displayName) }
} catch {
    Write-Host "  (could not enumerate AV products)"
}

try {
    $threats = Get-MpThreatDetection -ErrorAction Stop | Sort-Object InitialDetectionTime -Descending
    $ours = $threats | Where-Object { $_.Resources -match 'Proove|SkiaSharp' }
    if ($ours) {
        Write-Host "  DEFENDER HAS ACTED ON OUR FILES - this is your cause:" -ForegroundColor Red
        $ours | Select-Object -First 10 | ForEach-Object {
            Write-Host ("    {0}  {1}" -f $_.InitialDetectionTime, ($_.Resources -join '; '))
        }
    } elseif ($threats) {
        Write-Host ("  Defender has {0} detection(s) recorded, none matching our files." -f $threats.Count)
    } else {
        Write-Host "  Defender: no detections recorded."
    }
} catch {
    Write-Host "  (Defender history unavailable - needs admin, or a third-party AV replaced it)"
}

# Defender writes 1116 (detected) / 1117 (acted) here; a third-party AV will not.
try {
    $ev = Get-WinEvent -LogName 'Microsoft-Windows-Windows Defender/Operational' -MaxEvents 200 -ErrorAction Stop |
          Where-Object { $_.Id -in 1116, 1117 -and $_.Message -match 'Proove|Skia' }
    if ($ev) {
        Write-Host "  Defender event log entries for our files:" -ForegroundColor Red
        $ev | Select-Object -First 5 | ForEach-Object { Write-Host ("    {0}  id={1}" -f $_.TimeCreated, $_.Id) }
    }
} catch { }

Write-Host ""
Write-Host "  NOTE: none of our binaries are code-signed. If a third-party AV is listed above,"
Write-Host "        check ITS quarantine log too - this script can only read Defender's."

# -- 4. Reboot-scheduled deletions -------------------------------------------------------------
Section "4. PENDING REBOOT DELETIONS"

# The uninstaller falls back to MoveFileEx(DELAY_UNTIL_REBOOT) for files in use. If a schedule
# survives and the app is reinstalled to the same folder without rebooting, Windows deletes the
# NEW files at the next boot. The installer clears these, but only for the folder being installed
# into - a reinstall to a DIFFERENT folder leaves the old schedule live.
try {
    $pfro = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager' -Name PendingFileRenameOperations -ErrorAction Stop).PendingFileRenameOperations
    $hits = $pfro | Where-Object { $_ -match 'Proove' }
    if ($hits) {
        Write-Host "  A reboot-scheduled deletion still targets our files:" -ForegroundColor Red
        $hits | Select-Object -First 10 | ForEach-Object { Write-Host "    $_" }
        Write-Host "  FIX: REBOOT FIRST, then reinstall. Installing before the reboot means Windows"
        Write-Host "       deletes the fresh files at the next restart."
    } else {
        Write-Host "  None targeting our files."
    }
} catch {
    Write-Host "  No pending operations recorded (or needs admin to read)."
}


# -- 5. Lingering uninstall self-delete ---------------------------------------------------------
Section "5. LINGERING UNINSTALL SELF-DELETE (most likely cause)"

# The uninstaller spawns a DETACHED powershell/cmd that keeps trying to delete the install folder
# for up to ~2 minutes. It stops only when the folder is gone. Reinstall inside that window and it
# deletes part of the NEW install - which is what "some files are missing" looks like.
$selfDelete = @()
try {
    $procs = Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='cmd.exe'" -ErrorAction Stop
    foreach ($pr in $procs) {
        $cl = $pr.CommandLine
        if (-not $cl) { continue }
        # Exclude THIS script's own shell: its command line carries -InstallPath, which otherwise
        # matches every time and reports a false positive. (Caught by smoke-testing this check.)
        if ($pr.ProcessId -eq $PID) { continue }
        if ($cl -like '*diagnose-missing-files*') { continue }
        $hit = $false
        if ($cl -like "*$InstallPath*") { $hit = $true }
        # The PowerShell variant is -EncodedCommand, so the path is base64'd - decode and re-check.
        if (-not $hit -and $cl -match '-EncodedCommand\s+([A-Za-z0-9+/=]+)') {
            try {
                $dec = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($Matches[1]))
                if ($dec -like "*$InstallPath*" -or $dec -like "*ProoveGaming*") { $hit = $true }
            } catch { }
        }
        if ($hit) { $selfDelete += $pr }
    }
} catch {
    Write-Host "  (could not enumerate processes)"
}

if ($selfDelete.Count -gt 0) {
    Write-Host "  FOUND a live process still trying to delete this folder:" -ForegroundColor Red
    foreach ($pr in $selfDelete) { Write-Host ("    pid {0}  {1}" -f $pr.ProcessId, $pr.Name) }
    Write-Host "  This is almost certainly your cause." -ForegroundColor Red
    Write-Host "  FIX NOW: end those processes, then reinstall."
    Write-Host "  AVOID:   after uninstalling, wait until the install folder disappears before"
    Write-Host "           reinstalling. Do not uninstall and immediately reinstall."
} else {
    Write-Host "  None running right now."
    Write-Host "  NOTE: this process exits on its own, so a clean result here does NOT clear it -"
    Write-Host "        it only means it is not running at this moment. If the install broke right"
    Write-Host "        after an uninstall+reinstall, treat this as the prime suspect anyway."
}

# -- Verdict -----------------------------------------------------------------------------------
Section "WHAT TO DO"
if ($selfDelete.Count -gt 0) {
    Write-Host "  -> A lingering uninstall self-delete process is running. End it, then reinstall."
    Write-Host "     Root cause is ours; see docs/support/missing-files-crash-analysis.md cause 0."
} elseif ($offline.Count -gt 0) {
    Write-Host "  -> Cloud placeholders found. Install to Program Files, not a synced folder."
} elseif ($missing.Count -gt 0) {
    Write-Host ("  -> {0} critical file(s) are GONE: {1}" -f $missing.Count, ($missing -join ', '))
    Write-Host "     Nothing here removes files on its own, so something external took them."
    Write-Host "     Check section 3 (and your third-party AV's own quarantine), then reinstall"
    Write-Host "     with the install folder added as an AV exclusion."
} else {
    Write-Host "  -> All critical files present and readable. If the app still fails to start,"
    Write-Host "     send this whole output plus the Windows Event Viewer .NET Runtime entry."
}
Write-Host ""
