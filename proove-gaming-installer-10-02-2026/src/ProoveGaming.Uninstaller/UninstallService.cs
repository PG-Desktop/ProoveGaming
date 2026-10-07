using Microsoft.Win32;
using ProoveGaming.Common.Classes;
using System.Diagnostics;
using System.Text;
using System.Runtime.Versioning;

namespace ProoveGaming.Uninstaller;

// Nothing here defers work to a reboot, deliberately.
//
// This used to fall back to MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT) for files it could not delete.
// That API condemns a PATH, not a file: the next boot deletes whatever sits at that path by then.
//
// WHICH FILES IT CONDEMNED, AND WHY IT WAS ALWAYS THE SAME FOUR. The uninstaller is itself an
// Avalonia app, published single-file with IncludeNativeLibrariesForSelfExtract=false
// (ProoveGaming.Uninstaller.csproj), so its native dependencies are NOT bundled into the EXE. Only
// the EXE is copied into the install folder, so at run time it loads libSkiaSharp.dll,
// libHarfBuzzSharp.dll and av_libglesv2.dll out of the install folder — the App's copies — and
// holds them open for as long as its window is up. Those three plus its own running EXE are
// therefore the only files the delete passes below can never remove, on EVERY uninstall. They were
// the only ones scheduled, and they are exactly the four a user lost in the field.
//
// The detached self-delete loop then deleted all four seconds later, as it is designed to. The
// condemnations stayed. A reinstall into the same folder wrote fresh files at those paths, and the
// next boot deleted them: an app that ran perfectly until the user restarted, then died in Skia
// init with four files gone and the registry evidence consumed by the boot that used it.
//
// A mouse and keyboard utility has no business needing a restart to finish uninstalling, and the
// retry loop already covers the locks that actually occur. When a file still cannot be deleted, it
// is left in place and named in the log — never handed to Windows as a standing order against that
// path. See docs/support/missing-files-crash-analysis.md, cause 3.
internal static class UninstallService
{
	internal enum UninstallStage
	{
		StoppingDeviceHostService,
		RemovingDeviceHostService,
		RemovingFiles
	}

	public static async Task<int> RunAsync(string[] args, bool deleteUserData = false, Action<UninstallStage>? stageCallback = null)
	{
		if (!OperatingSystem.IsWindows())
		{
			return 1;
		}

		// Everything below runs with administrator rights and ends in a recursive delete, so the
		// folder must be proven to be ours first — before a single process is killed or sc.exe runs.
		var requestedPath = TryGetInstallPathFromArgs(args);
		var installPath = ResolveVerifiedInstallPath(
			requestedPath,
			TryGetInstallPathFromRegistry(),
			TryGetInstallPathFromCurrentExecutable(),
			InstallSignature.IsProoveGamingInstall,
			out var refusalReason);
		if (installPath is null)
		{
			LogMessage(refusalReason!);
			return RefusedExitCode;
		}

		if (!string.IsNullOrWhiteSpace(requestedPath) && !IsSamePath(requestedPath, installPath))
		{
			LogMessage($"ignored requested install path '{requestedPath}'; using the registered install '{installPath}'");
		}

		SetSafeWorkingDirectory();
		TryTerminateRunningAppProcesses(installPath);

		// User data goes first, straight after the owning processes are gone: it is an explicit
		// user instruction, so no later failure (service removal, locked install files) may
		// silently skip it — it used to run last and an sc.exe throw aborted it.
		if (deleteUserData)
		{
			DeleteUserDataFolder(TryGetForwardedPath(args, UninstallerConstants.UserLocalAppDataArgument));
		}

		var serviceRemoved = await TryHandleDeviceHostServiceAsync(installPath, stageCallback);

		stageCallback?.Invoke(UninstallStage.RemovingFiles);

		if (!serviceRemoved)
		{
			// The service could not be deregistered: keep the shortcuts, the uninstall registry
			// entry and the install directory (this uninstaller included) so the user has a
			// working retry point after a reboot — a launchable app and a visible entry — instead
			// of an orphaned service with no way to finish the job. The user-data deletion above
			// has already honoured the checkbox.
			return 1;
		}

		RemoveShortcuts(
			TryGetForwardedPath(args, UninstallerConstants.UserDesktopArgument),
			TryGetForwardedPath(args, UninstallerConstants.UserProgramsArgument));
		RemoveUninstallRegistryEntry();
		RemoveInstallDirectory(installPath);

		return 0;
	}

	/// <summary>
	/// Returned by <see cref="RunAsync"/> when it deleted nothing because no trusted install folder
	/// could be proven (see <see cref="ResolveVerifiedInstallPath"/>). Distinct from 1 (a genuine
	/// failure part-way through) so the UI can tell the user what to do next.
	/// </summary>
	internal const int RefusedExitCode = 2;

	/// <summary>
	/// The install folder <see cref="RunAsync"/> may act on, or null with the reason to log. Resolves
	/// from the trusted sources (<see cref="ResolveInstallPath"/>) and then requires the result to be
	/// one of our installs (<paramref name="isProoveGamingInstall"/>, i.e.
	/// <see cref="InstallSignature.IsProoveGamingInstall(string?)"/>). RunAsync touches nothing before
	/// this has returned a folder.
	/// </summary>
	internal static string? ResolveVerifiedInstallPath(
		string? requestedPath,
		string? registeredPath,
		string? executableFolder,
		Func<string, bool> isProoveGamingInstall,
		out string? refusalReason)
	{
		var installPath = ResolveInstallPath(requestedPath, registeredPath, executableFolder);
		if (string.IsNullOrWhiteSpace(installPath))
		{
			refusalReason = $"no registered install found; nothing was removed (requested: '{requestedPath}')";
			return null;
		}

		if (!isProoveGamingInstall(installPath))
		{
			refusalReason = $"refusing to delete '{installPath}': not a Proove Gaming install; nothing was removed";
			return null;
		}

		refusalReason = null;
		return installPath;
	}

	/// <summary>
	/// Picks the install folder to remove. Only two sources are trusted: the HKLM uninstall entry
	/// (writable by administrators only) and the folder this uninstaller EXE runs from (where an
	/// administrator installed it). The command line is written by whoever launched the process,
	/// so it may only choose between those two, never name a third folder.
	/// </summary>
	internal static string? ResolveInstallPath(string? requestedPath, string? registeredPath, string? executableFolder)
	{
		if (!string.IsNullOrWhiteSpace(requestedPath))
		{
			if (!string.IsNullOrWhiteSpace(registeredPath) && IsSamePath(requestedPath, registeredPath))
			{
				return registeredPath;
			}

			if (!string.IsNullOrWhiteSpace(executableFolder) && IsSamePath(requestedPath, executableFolder))
			{
				return executableFolder;
			}
		}

		return !string.IsNullOrWhiteSpace(registeredPath) ? registeredPath : executableFolder;
	}

	private static bool IsSamePath(string left, string right)
	{
		try
		{
			return string.Equals(
				Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
				Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
				StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
	}

	private static void DeleteUserDataFolder(string? userLocalAppDataPath)
	{
		// Per-user App data (SQLite DB incl. the saved sign-in, avatars) lives under
		// %LocalAppData%\ProoveGaming. This process runs elevated, so resolving the folder here
		// can land in the elevating admin's profile instead of the interactive user's — prefer
		// the path the pre-elevation instance captured and forwarded (see Program.EnsureElevated).
		var localAppData = string.IsNullOrWhiteSpace(userLocalAppDataPath)
			? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
			: userLocalAppDataPath;
		DeleteDataFolder(localAppData, requireTrustedOwner: false);

		// %ProgramData%\ProoveGaming is where DeviceHost builds before 2026-09-24 kept a
		// button-bindings.json (the host runs as LocalSystem, so it needed a machine-wide folder).
		// Nothing writes there any more; this removes what those builds left behind. Only when the
		// folder is provably theirs (created by the SYSTEM service): any user may create folders in
		// %ProgramData%, so a folder of this name owned by anyone else was never ours.
		DeleteDataFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), requireTrustedOwner: true);
	}

	private static string? TryGetForwardedValue(string[] args, string argumentName)
	{
		var argumentIndex = Array.FindIndex(args, arg => string.Equals(arg, argumentName, StringComparison.OrdinalIgnoreCase));
		if (argumentIndex < 0 || argumentIndex + 1 >= args.Length)
		{
			return null;
		}

		var value = args[argumentIndex + 1];
		return string.IsNullOrWhiteSpace(value) ? null : value;
	}

	// Forwarded folder arguments direct deletions performed with administrator rights, so
	// anything but a fully qualified path falls back to the elevated profile's own resolution
	// rather than being combined blindly.
	private static string? TryGetForwardedPath(string[] args, string argumentName)
	{
		var value = TryGetForwardedValue(args, argumentName);
		return value != null && Path.IsPathFullyQualified(value) ? value : null;
	}

	internal static void DeleteDataFolder(string baseFolder, bool requireTrustedOwner)
	{
		var dataPath = Path.Combine(baseFolder, InstallLayout.AppDataFolderName);

		if (!Directory.Exists(dataPath))
		{
			return;
		}

		if (requireTrustedOwner && !InstallSignature.IsOwnedByTrustedPrincipal(dataPath))
		{
			LogMessage($"left '{dataPath}' in place: not created by Proove Gaming");
			return;
		}

		// A plainly failed delete is safer here than a delayed one — see the note on this class.
		TryDeleteDirectoryContents(dataPath);
		TryDeleteDirectory(dataPath);
	}

	/// <summary>
	/// Stops and deregisters the DeviceHost service. Each sc.exe call is guarded separately:
	/// a hung service can fail "stop" (e.g. FAILED 1053), and "delete" must still run — sc
	/// marks a running service for deletion and the mark clears on reboot. Returns false only
	/// when the service could not be deregistered at all, so the caller can leave a retry
	/// point instead of orphaning a registered service whose binary is gone.
	/// </summary>
	private static async Task<bool> TryHandleDeviceHostServiceAsync(string installPath, Action<UninstallStage>? stageCallback)
	{
		if (!OperatingSystem.IsWindows())
		{
			return true;
		}

		stageCallback?.Invoke(UninstallStage.StoppingDeviceHostService);
		try
		{
			await RunScCommandAsync($"stop \"{InstallLayout.DeviceHostServiceName}\"", ServiceControl.ErrorServiceDoesNotExist, ServiceControl.ErrorServiceNotActive);
		}
		catch (Exception ex)
		{
			LogFailure("sc stop", ex);
		}

		stageCallback?.Invoke(UninstallStage.RemovingDeviceHostService);
		try
		{
			await RunScCommandAsync($"delete \"{InstallLayout.DeviceHostServiceName}\"", ServiceControl.ErrorServiceDoesNotExist);
		}
		catch (Exception ex)
		{
			LogFailure("sc delete", ex);
			return false;
		}

		var hostDirectoryPath = Path.Combine(installPath, InstallLayout.DeviceHostFolderName);
		if (Directory.Exists(hostDirectoryPath))
		{
			TryDeleteDirectoryContents(hostDirectoryPath);
			TryDeleteDirectory(hostDirectoryPath);
		}

		return true;
	}

	// The uninstaller has no logging infrastructure; the sc.exe exception text carries the exit
	// code and console output — the only clue to why service removal failed — so it is kept in
	// a best-effort temp log next to the Debug trace instead of being swallowed.
	private static void LogFailure(string operation, Exception exception) =>
		LogMessage($"{operation} failed: {exception}");

	private static void LogMessage(string message)
	{
		Debug.WriteLine(message);
		try
		{
			File.AppendAllText(
				Path.Combine(Path.GetTempPath(), "proove-uninstall.log"),
				$"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
		}
		catch
		{
			// best-effort only
		}
	}

	private static string? TryGetInstallPathFromArgs(string[] args)
	{
		var uninstallIndex = Array.FindIndex(args, arg => string.Equals(arg, InstallLayout.UninstallArgument, StringComparison.OrdinalIgnoreCase));
		if (uninstallIndex >= 0)
		{
			var pathIndex = uninstallIndex + 1;
			// Same "--" guard as the positional branch below: a hand-typed "--uninstall" with no
			// path must not swallow the forwarded per-user arguments as the install path.
			if (pathIndex >= args.Length || args[pathIndex].StartsWith("--", StringComparison.Ordinal))
			{
				return null;
			}

			return args[pathIndex];
		}

		if (args.Length == 0)
		{
			return null;
		}

		var firstArg = args[0];
		if (string.IsNullOrWhiteSpace(firstArg) || firstArg.StartsWith("--", StringComparison.Ordinal))
		{
			return null;
		}

		return firstArg;
	}

	// Decisions are made on sc.exe's exit code only; its text is localised (see ServiceControl).
	[SupportedOSPlatform("windows")]
	private static async Task RunScCommandAsync(string arguments, params int[] toleratedExitCodes)
	{
		var result = await ServiceControl.RunScAsync(arguments);
		if (result.ExitCode != 0 && Array.IndexOf(toleratedExitCodes, result.ExitCode) < 0)
		{
			throw new InvalidOperationException($"sc.exe {arguments} failed with exit code {result.ExitCode}: {result.Output}");
		}
	}

	// HKLM only. The per-user (HKCU) entry older builds wrote is never read: any user-level process
	// can rewrite it, and this process deletes whatever folder it names with administrator rights.
	private static string? TryGetInstallPathFromRegistry()
	{
		if (!OperatingSystem.IsWindows())
		{
			return null;
		}

		try
		{
			using var uninstallKey = Registry.LocalMachine.OpenSubKey(InstallLayout.UninstallRegistryKeyPath, writable: false);
			return uninstallKey?.GetValue("InstallLocation") as string;
		}
		catch (Exception ex)
		{
			LogFailure("install path lookup (HKLM)", ex);
			return null;
		}
	}

	private static string? TryGetInstallPathFromCurrentExecutable()
	{
		var currentExePath = Environment.ProcessPath;
		if (string.IsNullOrWhiteSpace(currentExePath))
		{
			return null;
		}

		var exeFileName = Path.GetFileName(currentExePath);
		if (!string.Equals(exeFileName, InstallLayout.UninstallerExeName, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		var installDirectory = Path.GetDirectoryName(currentExePath);
		if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
		{
			return null;
		}

		var appExePath = Path.Combine(installDirectory, InstallLayout.AppExeName);
		return File.Exists(appExePath) || File.Exists(Path.Combine(installDirectory, InstallLayout.UninstallerExeName))
			? installDirectory
			: null;
	}

	// Shortcut folders resolve to the elevating admin's profile when UAC crosses accounts, exactly
	// like the LocalAppData case in DeleteUserDataFolder — so both prefer the values the
	// pre-elevation instance forwarded (see Program.EnsureElevated).
	private static void RemoveShortcuts(string? userDesktopPath, string? userProgramsPath)
	{
		var desktop = userDesktopPath ?? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
		DeleteIfExists(Path.Combine(desktop, $"{InstallLayout.ProductName}.lnk"));

		var programs = userProgramsPath ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs);
		DeleteIfExists(Path.Combine(programs, $"{InstallLayout.ProductName}.lnk"));
	}

	private static void RemoveUninstallRegistryEntry()
	{
		if (!OperatingSystem.IsWindows())
		{
			return;
		}

		try
		{
			Registry.LocalMachine.DeleteSubKeyTree(InstallLayout.UninstallRegistryKeyPath, throwOnMissingSubKey: false);
		}
		catch (Exception ex)
		{
			LogFailure("uninstall key removal (HKLM)", ex);
		}

		// A per-user entry written by a build before 2026-09-24, if this account still has one. It is
		// only deleted, never read (see TryGetInstallPathFromRegistry).
		try
		{
			Registry.CurrentUser.DeleteSubKeyTree(InstallLayout.UninstallRegistryKeyPath, throwOnMissingSubKey: false);
		}
		catch (Exception ex)
		{
			LogFailure("legacy uninstall key removal (HKCU)", ex);
		}
	}

	private static void RemoveInstallDirectory(string installPath)
	{
		if (!Directory.Exists(installPath))
		{
			return;
		}

		try
		{
			Directory.Delete(installPath, recursive: true);
		}
		catch
		{
			// Always reached: the running uninstaller EXE lives in this folder, so the recursive
			// delete above can never succeed. This is the normal path, not an error path.
			//
			// A ScheduleDeleteOnReboot(installPath) call used to follow this pass. It
			// enumerated what was still on disk, so it condemned only the files the passes could not
			// remove — which, per the note on this class, is always the uninstaller's own EXE and
			// the three native DLLs it has loaded. The self-delete loop then removed all four, and
			// the condemnations outlived them.
			TryDeleteDirectoryContents(installPath);

			// What is still here is what this process cannot remove while it is running. Recorded
			// before handing over, because the detached loop outlives this process and the "Done"
			// screen is shown before its outcome is knowable — so this log is the only place a
			// leftover can be named at all. Expected content is the four files above; anything else
			// is a genuine leftover worth reading about.
			LogRemainingPaths(installPath);

			// Hands the rest to the detached loop: it outlives this process, so it can delete the
			// running uninstaller EXE and its loaded DLLs. It retries for ~2 minutes (PowerShell;
			// the cmd fallback gets ~1 minute), which is what clears these locks in practice — a
			// reboot was never needed for them.
			ScheduleSelfDelete(Path.Combine(installPath, InstallLayout.UninstallerExeName), installPath);
			TryDeleteDirectory(installPath);
		}
	}

	// Best-effort: an uninstall is never failed over a log write.
	private static void LogRemainingPaths(string installPath)
	{
		try
		{
			if (!Directory.Exists(installPath))
			{
				return;
			}

			var remaining = Directory.EnumerateFileSystemEntries(installPath, "*", SearchOption.AllDirectories).ToList();
			if (remaining.Count == 0)
			{
				return;
			}

			File.AppendAllText(
				Path.Combine(Path.GetTempPath(), "proove-uninstall.log"),
				$"{DateTime.Now:yyyy-MM-dd HH:mm:ss} left in place for the self-delete loop ({remaining.Count}):" +
				$"{Environment.NewLine}  {string.Join($"{Environment.NewLine}  ", remaining)}{Environment.NewLine}");
		}
		catch
		{
			// best-effort only
		}
	}

	private static void ScheduleSelfDelete(string uninstallerPath, string installPath)
	{
		if (!File.Exists(uninstallerPath))
		{
			return;
		}

		// The marker is what stops this detached loop from eating a LATER reinstall of the same
		// folder. It is written before the process starts, so the loop can never observe its
		// absence and quit immediately; the pid is filled in once we have it. See UninstallHandoff.
		//
		// If it could not be written (a hardened %ProgramData% ACL, endpoint protection, a full
		// disk), the loop must run UNGUARDED. A guard pointing at a marker that does not exist
		// breaks on the first pass and deletes nothing, which would turn a failed best-effort write
		// into an uninstall that silently leaves the whole install folder behind.
		var markerPath = UninstallHandoff.Write(installPath, selfDeleteProcessId: 0)
			? UninstallHandoff.GetMarkerPath(installPath)
			: null;

		var powerShellCommand = BuildSelfDeleteCommand(uninstallerPath, installPath, markerPath, Environment.ProcessId);
		var selfDeleteProcessId = TryStartSelfDeleteProcess(SystemTool.PowerShellPath, powerShellCommand);
		if (selfDeleteProcessId is null)
		{
			var cmdFallbackCommand = BuildLegacySelfDeleteCommand(uninstallerPath, installPath, markerPath);
			selfDeleteProcessId = TryStartSelfDeleteProcess(SystemTool.PathOf("cmd.exe"), cmdFallbackCommand);
		}

		// Skipped entirely when markerPath is null: there is no marker to name a pid in, and the
		// loop is already running unguarded.
		if (markerPath is not null)
		{
			if (selfDeleteProcessId is { } pid)
			{
				UninstallHandoff.Write(installPath, pid);
			}
			else
			{
				// Nothing is going to clean up, so leaving the marker would only make a later
				// installer hunt a process that never existed.
				UninstallHandoff.Remove(installPath);
			}
		}
	}

	// Returns the started process id, or null when it could not be started. The id is what lets a
	// later installer stop this exact process instead of guessing from command lines.
	private static int? TryStartSelfDeleteProcess(string fileName, string arguments)
	{
		try
		{
			using var process = Process.Start(new ProcessStartInfo
			{
				FileName = fileName,
				Arguments = arguments,
				WorkingDirectory = Path.GetTempPath(),
				CreateNoWindow = true,
				UseShellExecute = false,
				WindowStyle = ProcessWindowStyle.Hidden
			});

			return process?.Id;
		}
		catch
		{
			return null;
		}
	}

	// markerPath is null when the marker could not be written. The loop then runs UNGUARDED, exactly
	// as it did before the marker existed: a guard whose marker is missing would break on the first
	// pass and delete nothing, so a failed best-effort write would silently skip the whole uninstall.
	// Losing the race protection is the intended degradation; losing the cleanup is not.
	internal static string BuildSelfDeleteCommand(string uninstallerPath, string installPath, string? markerPath, int processId)
	{
		var escapedInstallPath = installPath.Replace("'", "''");
		var escapedUninstallerPath = uninstallerPath.Replace("'", "''");
		var escapedMarkerPath = markerPath?.Replace("'", "''");

		// The marker test is the FIRST thing each iteration does. An installer that has started
		// reinstalling into this folder removes the marker, so the very next pass exits instead of
		// deleting the files it just wrote. Without it this loop cannot tell a stale install from a
		// brand new one - see UninstallHandoff.
		var markerGuard = escapedMarkerPath is null
			? string.Empty
			: $"if (-not (Test-Path -LiteralPath '{escapedMarkerPath}')) {{ break }}; ";
		var markerCleanup = escapedMarkerPath is null
			? string.Empty
			: $"; Remove-Item -LiteralPath '{escapedMarkerPath}' -Force -ErrorAction SilentlyContinue";

		// The folder is emptied with .NET's Directory.Delete, not Remove-Item -Recurse. Windows
		// PowerShell 5.1's Remove-Item (and Get-ChildItem -Recurse) descends INTO a junction and
		// deletes the target's contents, and this runs with administrator rights, so one junction
		// inside the install folder would aim it anywhere on the disk. Directory.Delete removes a
		// junction or symbolic link as a link, and Clear-Attributes likewise never descends into one.
		const string clearAttributesFunction =
			"function Clear-Attributes($dir) { foreach ($entry in [System.IO.Directory]::EnumerateFileSystemEntries($dir)) { " +
			"$attributes = [System.IO.File]::GetAttributes($entry); " +
			"if ($attributes -band [System.IO.FileAttributes]::ReparsePoint) { continue }; " +
			"[System.IO.File]::SetAttributes($entry, [System.IO.FileAttributes]::Normal); " +
			"if ($attributes -band [System.IO.FileAttributes]::Directory) { Clear-Attributes $entry } } }; ";

		var script =
			clearAttributesFunction +
			$"$pidToWait={processId}; " +
			"while (Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 500 }; " +
			"for ($i = 0; $i -lt 240; $i++) { " +
			markerGuard +
			"try { " +
			$"if (Test-Path -LiteralPath '{escapedUninstallerPath}') {{ [System.IO.File]::SetAttributes('{escapedUninstallerPath}', [System.IO.FileAttributes]::Normal); Remove-Item -LiteralPath '{escapedUninstallerPath}' -Force -ErrorAction SilentlyContinue }}; " +
			$"if (Test-Path -LiteralPath '{escapedInstallPath}') {{ try {{ Clear-Attributes '{escapedInstallPath}' }} catch {{ }}; [System.IO.Directory]::Delete('{escapedInstallPath}', $true) }}; " +
			$"if (-not (Test-Path -LiteralPath '{escapedInstallPath}')) {{ break }} " +
			"} catch { Start-Sleep -Milliseconds 500 } " +
			"}" +
			markerCleanup;

		var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
		return $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encodedScript}";
	}

	// markerPath is null when the marker could not be written - see BuildSelfDeleteCommand.
	internal static string BuildLegacySelfDeleteCommand(string uninstallerPath, string installPath, string? markerPath)
	{
		var escapedInstallPath = installPath.Replace("\"", "\"\"");
		var escapedUninstallerPath = uninstallerPath.Replace("\"", "\"\"");
		var escapedMarkerPath = markerPath?.Replace("\"", "\"\"");

		// Same marker guard as the PowerShell version, and for the same reason: a reinstall removes
		// the marker, and this loop must not delete the files that reinstall just wrote.
		var markerGuard = escapedMarkerPath is null
			? string.Empty
			: $"if not exist \"{escapedMarkerPath}\" exit /b 0 & ";
		var markerCleanup = escapedMarkerPath is null
			? string.Empty
			: $"del /f /q \"{escapedMarkerPath}\" 2>nul & ";

		return $"/c for /l %i in (1,1,60) do (" +
			markerGuard +
			$"attrib -h -s -r \"{escapedUninstallerPath}\" 2>nul & " +
			$"del /f /q \"{escapedUninstallerPath}\" 2>nul & " +
			$"attrib -h -s -r \"{escapedInstallPath}\\*\" /s /d 2>nul & " +
			$"del /f /q \"{escapedInstallPath}\\*\" 2>nul & " +
			$"for /d %d in (\"{escapedInstallPath}\\*\") do rd /s /q \"%d\" 2>nul & " +
			$"rd /s /q \"{escapedInstallPath}\" 2>nul & " +
			$"if not exist \"{escapedInstallPath}\" ({markerCleanup}exit /b 0) & " +
			"ping 127.0.0.1 -n 2 > nul)";
	}

	private static void SetSafeWorkingDirectory()
	{
		try
		{
			Directory.SetCurrentDirectory(Path.GetTempPath());
		}
		catch
		{
			// best effort only
		}
	}

	private static void DeleteIfExists(string path)
	{
		if (!File.Exists(path))
		{
			return;
		}

		TryDeleteFile(path);
	}

	// A file that will not delete is left where it is. The detached self-delete loop retries the
	// whole folder for ~2 minutes afterwards, which is what actually clears a transient lock; past
	// that, leftover files are a visible, harmless, fixable state. They are not worth condemning the
	// path for — see the note on this class.
	private static void TryDeleteFile(string filePath)
	{
		try
		{
			File.SetAttributes(filePath, FileAttributes.Normal);
			File.Delete(filePath);
		}
		catch
		{
			// best-effort only
		}
	}

	private static void TryDeleteDirectory(string directoryPath)
	{
		try
		{
			Directory.Delete(directoryPath, recursive: false);
		}
		catch
		{
			// best-effort only
		}
	}

	// Walks the tree by hand so it can refuse to descend into a junction or symbolic link. This runs
	// with administrator rights over folders a standard user can write to (%LocalAppData%, and any
	// install folder from before the install ACL), and SearchOption.AllDirectories follows links: one
	// junction pointing at C:\Windows would have turned "delete my data" into deleting that. A link
	// is removed as a link (a non-recursive delete of a junction removes only the junction).
	//
	// That includes the folder it is GIVEN: listing a junction lists its target, so a
	// %LocalAppData%\ProoveGaming replaced by a junction would otherwise have its target emptied
	// (review round 3, B1). The caller's non-recursive TryDeleteDirectory then removes the link.
	internal static void TryDeleteDirectoryContents(string directoryPath)
	{
		if (IsReparsePointOrUnreadable(directoryPath))
		{
			return;
		}

		string[] entries;
		try
		{
			entries = Directory.GetFileSystemEntries(directoryPath);
		}
		catch
		{
			// best-effort only
			return;
		}

		foreach (var entry in entries)
		{
			FileAttributes attributes;
			try
			{
				attributes = File.GetAttributes(entry);
			}
			catch
			{
				continue;
			}

			if (!attributes.HasFlag(FileAttributes.Directory))
			{
				TryDeleteFile(entry);
				continue;
			}

			if (!attributes.HasFlag(FileAttributes.ReparsePoint))
			{
				TryDeleteDirectoryContents(entry);
			}

			TryDeleteDirectory(entry);
		}
	}

	// Unreadable is treated like a link: not evidence that walking it is safe.
	private static bool IsReparsePointOrUnreadable(string path)
	{
		try
		{
			return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
		}
		catch
		{
			return true;
		}
	}

	private static void TryTerminateRunningAppProcesses(string installPath)
	{
		if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(installPath))
		{
			return;
		}

		var normalizedInstallPath = Path.GetFullPath(installPath)
			.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

		var terminatedProcesses = new List<Process>();
		foreach (var process in Process.GetProcesses())
		{
			try
			{
				if (process.Id == Environment.ProcessId)
				{
					continue;
				}

				var processPath = process.MainModule?.FileName;
				if (string.IsNullOrWhiteSpace(processPath))
				{
					continue;
				}

				var normalizedProcessPath = Path.GetFullPath(processPath);
				if (!normalizedProcessPath.StartsWith(normalizedInstallPath, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				process.Kill(entireProcessTree: true);
				terminatedProcesses.Add(process);
			}
			catch
			{
				// best-effort only
			}
		}

		// Kill only requests termination. User data (incl. the app's always-open SQLite DB) is
		// deleted right after this method returns, so wait for the killed processes to actually
		// exit — otherwise the delete races the teardown and silently fails on the locked file.
		foreach (var process in terminatedProcesses)
		{
			try
			{
				process.WaitForExit(5000);
			}
			catch
			{
				// best-effort only
			}
			finally
			{
				process.Dispose();
			}
		}
	}
}
