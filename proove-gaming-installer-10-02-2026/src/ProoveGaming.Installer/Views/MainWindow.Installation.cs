using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.Versioning;
using Microsoft.Win32;
using ProoveGaming.Common.Classes;

namespace ProoveGaming.Installer.Views;

internal partial class MainWindow
{
	private CancellationTokenSource? _installCancellationTokenSource;
	private InstallPauseGate? _installPauseGate;
	private bool _installInProgress;
	private bool _closeAfterInstallCancellation;
	private string? _activeInstallPath;

	// Captured before anything is touched so a cancellation can undo exactly what THIS run created
	// and nothing more. Re-installing over an existing install must not tear down the previous one.
	private bool _installFolderPreexisted;
	private bool _servicePreexisted;

	// Staging folder the embedded payload is unpacked into (see StagingFolderNamePrefix); deleted on
	// every exit path so a cancelled or repeated install doesn't leave hundreds of MB behind.
	private string? _stagingDirectory;

	private async Task RunInstallAsync(string installPath)
	{
		_installInProgress = true;
		_closeAfterInstallCancellation = false;
		_activeInstallPath = installPath;
		_installCancellationTokenSource?.Dispose();
		_installCancellationTokenSource = new CancellationTokenSource();
		_installPauseGate = new InstallPauseGate();

		var cancellationToken = _installCancellationTokenSource.Token;

		GetFolderBackButton().IsEnabled = false;
		GetFolderInstallButton().IsEnabled = false;
		GetInstallPathTextBox().IsEnabled = false;
		GetCreateDesktopShortcutCheckBox().IsEnabled = false;
		GetCreateStartMenuShortcutCheckBox().IsEnabled = false;
		GetStatusText().Text = LocalizationManager.GetString("Status_Installing");
		GetInstallProgressView().SetProgress(0);

		try
		{
#if DEBUG
			await RunMockInstallAsync();
			return;
#endif
			await WaitIfInstallPausedOrCancelledAsync(cancellationToken);

			// Snapshot what already exists BEFORE creating anything, so rollback can distinguish
			// "we made this" from "this was already the user's installation".
			_installFolderPreexisted = Directory.Exists(installPath);
			_servicePreexisted = OperatingSystem.IsWindows() && await DeviceHostServiceExistsAsync();

			if (OperatingSystem.IsWindows())
			{
				_installFolderPreexisted = await Task.Run(
					() => PrepareInstallFolder(installPath, _installFolderPreexisted, InstallSignature.IsProoveGamingInstall),
					cancellationToken);
			}
			else if (!_installFolderPreexisted)
			{
				Directory.CreateDirectory(installPath);
			}

			var stagingDir = await PreparePayloadAsync();
			_stagingDirectory = stagingDir;
			if (stagingDir is null)
			{
				ShowNativeMessageBox(
					LocalizationManager.GetString("Message_PayloadNotFound"),
					LocalizationManager.GetString("Message_ErrorTitle"));
				return;
			}

			// Re-installing over an existing install copies onto files the running service holds
			// open, so it must be stopped BEFORE the copy — not later with the rest of the service
			// configuration. Clearing the reboot-delete schedule has to happen before the copy too,
			// or the files we are about to write are already condemned.
			if (OperatingSystem.IsWindows())
			{
				GetStatusText().Text = LocalizationManager.GetString("Status_ClosingApp");
				await CloseRunningAppAsync(cancellationToken);

				GetStatusText().Text = LocalizationManager.GetString("Status_StoppingDeviceHostService");
				await StopDeviceHostServiceAsync(cancellationToken);
				StopLingeringUninstallCleanup(installPath);
				ClearPendingRebootDeletions(installPath);
			}

			// Immediately before the copy, so no payload file ever sits in a folder a standard user can
			// modify: every DLL in host\ is loaded by a LocalSystem service. The whole tree, not just
			// the root: an entry a user created earlier would otherwise stay theirs. Refuses links.
			// Inheritable, so the copied files pick the rules up as they are written. See InstallAcl.
			await Task.Run(() =>
			{
				if (OperatingSystem.IsWindows())
				{
					InstallAcl.ProtectExistingTree(installPath, allowUsersReadAndExecute: true);
				}
			}, cancellationToken);

			GetStatusText().Text = LocalizationManager.GetString("Status_CopyingFiles");
			GetInstallProgressView().SetProgress(0.3);
			await CopyDirectoryAsync(stagingDir, installPath, cancellationToken);
			GetInstallProgressView().SetProgress(0.7);
			await WaitIfInstallPausedOrCancelledAsync(cancellationToken);

			var appExePath = ResolveInstalledAppExecutablePath(installPath);
			if (string.IsNullOrWhiteSpace(appExePath))
			{
				ShowNativeMessageBox(
					string.Format(LocalizationManager.GetString("Message_AppNotFound"), Path.Combine(installPath, InstallLayout.AppExeName)),
					LocalizationManager.GetString("Message_ErrorTitle"));
				return;
			}

			if (OperatingSystem.IsWindows())
			{
				GetStatusText().Text = LocalizationManager.GetString("Status_ConfiguringDeviceHostService");
				GetInstallProgressView().SetProgress(0.85);
				await WaitIfInstallPausedOrCancelledAsync(cancellationToken);
				await ConfigureOsIntegrationAsync(installPath, appExePath);
			}

			GetInstallProgressView().SetProgress(1.0);

			_currentStep = InstallerStep.Complete;
			UpdateStepUi();
		}
		catch (OperationCanceledException)
		{
			// Rollback stops the service (up to a 15s wait for it to exit) and deletes the install
			// folder, so it is far from instant. Without this the screen keeps claiming "Copying
			// application files..." behind a stalled bar until the window abruptly closes.
			GetStatusText().Text = LocalizationManager.GetString("Status_CancellingInstall");
			GetInstallingCancelButton().IsEnabled = false;

			await RollbackInstallAsync(installPath);

			if (_closeAfterInstallCancellation)
			{
				Close();
				return;
			}

			_currentStep = InstallerStep.Folder;
			UpdateStepUi();
		}
		catch (UnsafeInstallFolderException)
		{
			_currentStep = InstallerStep.Folder;
			UpdateStepUi();
			ShowNativeMessageBox(
				LocalizationManager.GetString("Message_InstallFolderUnsafe"),
				LocalizationManager.GetString("Message_ErrorTitle"));
		}
		catch (UnauthorizedAccessException)
		{
			_currentStep = InstallerStep.Folder;
			UpdateStepUi();
			ShowNativeMessageBox(
				LocalizationManager.GetString("Message_AccessDenied"),
				LocalizationManager.GetString("Message_ErrorTitle"));
		}
		catch (ServiceCommandException ex)
		{
			// sc.exe's own text is written in the console OEM code page and renders as mojibake, so
			// report the Win32 code in our own localised message instead of passing it through.
			_currentStep = InstallerStep.Folder;
			UpdateStepUi();
			ShowNativeMessageBox(
				string.Format(LocalizationManager.GetString("Message_ServiceConfigureFailed"), ex.ExitCode),
				LocalizationManager.GetString("Message_ErrorTitle"));
		}
		catch (Exception ex)
		{
			_currentStep = InstallerStep.Folder;
			UpdateStepUi();
			ShowNativeMessageBox(
				string.Format(LocalizationManager.GetString("Message_InstallFailedDetails"), ex.Message),
				LocalizationManager.GetString("Message_ErrorTitle"));
		}
		finally
		{
			GetFolderBackButton().IsEnabled = _currentStep == InstallerStep.Folder;
			GetFolderInstallButton().IsEnabled = _currentStep == InstallerStep.Folder;
			GetInstallPathTextBox().IsEnabled = true;
			GetCreateDesktopShortcutCheckBox().IsEnabled = true;
			GetCreateStartMenuShortcutCheckBox().IsEnabled = true;
			_installInProgress = false;
			_activeInstallPath = null;
			_installCancellationTokenSource?.Dispose();
			_installCancellationTokenSource = null;
			_installPauseGate = null;
			await CleanupStagingDirectoryAsync();
		}
	}

	// Both cancellation checks matter: the leading one makes an already-cancelled install throw
	// before any work, and the trailing one decides the cancel-vs-resume race — ConfirmCancelModal
	// cancels the token and then resumes the gate, so a waiter can observe the resume first and
	// must still throw here (cancellation wins).
	private async Task WaitIfInstallPausedOrCancelledAsync(CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		if (_installPauseGate is { } pauseGate)
		{
			await pauseGate.WaitWhilePausedAsync(cancellationToken);
		}

		cancellationToken.ThrowIfCancellationRequested();
	}

	// Undoes everything THIS run created, and deliberately nothing else.
	//
	// The previous version always deleted the install folder and the service, which meant cancelling
	// a RE-install destroyed the user's existing, working installation — strictly worse than never
	// having pressed Install. The pre-install snapshot is what makes the distinction possible.
	private async Task RollbackInstallAsync(string installPath)
	{
		try
		{
			if (OperatingSystem.IsWindows())
			{
				// Stop whatever we may have started and wait for the exe to release its files,
				// otherwise the directory delete below fails on a locked binary.
				await StopDeviceHostServiceAsync(CancellationToken.None);

				if (!_servicePreexisted)
				{
					await ServiceControl.RunScAsync($"delete \"{InstallLayout.DeviceHostServiceName}\"");
				}

				if (!_installFolderPreexisted)
				{
					TryRemoveWindowsShortcut(Environment.SpecialFolder.DesktopDirectory);
					TryRemoveWindowsShortcut(Environment.SpecialFolder.Programs);
					Registry.LocalMachine.DeleteSubKeyTree(InstallLayout.UninstallRegistryKeyPath, throwOnMissingSubKey: false);
				}
			}

			if (!_installFolderPreexisted && Directory.Exists(installPath))
			{
				await Task.Run(() => Directory.Delete(installPath, recursive: true));
			}
		}
		catch
		{
			// best-effort rollback only
		}
	}

	// True when the Device Host service is already registered. Uses sc.exe's exit code rather than
	// its output, which is localised.
	[SupportedOSPlatform("windows")]
	private static async Task<bool> DeviceHostServiceExistsAsync()
	{
		var result = await ServiceControl.RunScAsync($"query \"{InstallLayout.DeviceHostServiceName}\"");
		return result.ExitCode != ServiceControl.ErrorServiceDoesNotExist;
	}

	// Removes the unpacked payload (see StagingFolderNamePrefix). Only ever deletes our own extraction
	// folder — the alternative payload source is a folder next to the installer, which must survive.
	//
	// Off the UI thread: this deletes the whole extracted self-contained payload, which is large
	// enough that a synchronous delete visibly freezes the window on its way out.
	private Task CleanupStagingDirectoryAsync()
	{
		var stagingDirectory = _stagingDirectory;
		_stagingDirectory = null;

		if (string.IsNullOrEmpty(stagingDirectory) || !Directory.Exists(stagingDirectory))
		{
			return Task.CompletedTask;
		}

		var fullStagingPath = Path.GetFullPath(stagingDirectory);
		var isOwnExtractionFolder =
			string.Equals(Path.GetDirectoryName(fullStagingPath), Path.GetFullPath(StagingParent), StringComparison.OrdinalIgnoreCase) &&
			Path.GetFileName(fullStagingPath).StartsWith(StagingFolderNamePrefix, StringComparison.OrdinalIgnoreCase);
		if (!isOwnExtractionFolder)
		{
			return Task.CompletedTask;
		}

		return Task.Run(() =>
		{
			try
			{
				Directory.Delete(stagingDirectory, recursive: true);
			}
			catch
			{
				// Temp cleanup is never worth failing or delaying the install for.
			}
		});
	}

	private static void TryRemoveWindowsShortcut(Environment.SpecialFolder specialFolder)
	{
		var shortcutPath = Path.Combine(Environment.GetFolderPath(specialFolder), $"{InstallLayout.ProductName}.lnk");
		if (!File.Exists(shortcutPath))
		{
			return;
		}

		try
		{
			File.Delete(shortcutPath);
		}
		catch
		{
			// best-effort rollback only
		}
	}

#if DEBUG
	private async Task RunMockInstallAsync()
	{
		GetStatusText().Text = LocalizationManager.GetString("Status_CopyingFiles");
		GetInstallProgressView().SetProgress(0.25);
		await Task.Delay(400);
		GetInstallProgressView().SetProgress(0.60);
		await Task.Delay(400);
		GetInstallProgressView().SetProgress(1.0);
		await Task.Delay(200);
		_currentStep = InstallerStep.Complete;
		UpdateStepUi();
	}
#endif

	[SupportedOSPlatform("windows")]
	private async Task ConfigureOsIntegrationAsync(string installPath, string appExePath)
	{
		await InstallOrUpdateDeviceHostServiceAsync(installPath);

		// Off the UI thread too: this writes the embedded uninstaller exe out of the single-file
		// bundle with a synchronous copy, which stalls the window during the configuring step.
		var uninstallerPath = await Task.Run(() => PrepareUninstallerExecutable(installPath));
		if (string.IsNullOrWhiteSpace(uninstallerPath) || !File.Exists(uninstallerPath))
		{
			throw new FileNotFoundException("Dedicated uninstaller executable was not prepared.", Path.Combine(installPath, InstallLayout.UninstallerExeName));
		}

		// The uninstaller only removes a folder whose marker files are owned by Administrators (see
		// InstallSignature). Stated explicitly rather than left to the elevated token's default
		// owner, which is the installing account when UAC is disabled.
		await Task.Run(() =>
		{
			foreach (var marker in new[]
			{
				Path.Combine(installPath, InstallLayout.AppExeName),
				uninstallerPath,
				Path.Combine(installPath, InstallLayout.DeviceHostFolderName, InstallLayout.DeviceHostExeName)
			})
			{
				if (File.Exists(marker))
				{
					InstallAcl.MakeAdministratorsOwner(marker);
				}
			}
		});

		if (GetCreateDesktopShortcutCheckBox().IsChecked == true)
		{
			var desktopFolder = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
			var desktopShortcutPath = Path.Combine(desktopFolder, $"{InstallLayout.ProductName}.lnk");
			CreateWindowsShortcut(desktopShortcutPath, appExePath, installPath);
		}

		if (GetCreateStartMenuShortcutCheckBox().IsChecked == true)
		{
			var startMenuFolder = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
			var startMenuShortcutPath = Path.Combine(startMenuFolder, $"{InstallLayout.ProductName}.lnk");
			CreateWindowsShortcut(startMenuShortcutPath, appExePath, installPath);
		}

		RegisterWindowsUninstallEntry(installPath, appExePath, uninstallerPath);
	}

	private static string PrepareUninstallerExecutable(string installPath)
	{
		var uninstallerPath = Path.Combine(installPath, InstallLayout.UninstallerExeName);

		if (TryExtractEmbeddedUninstaller(uninstallerPath))
		{
			return uninstallerPath;
		}

		var stagedUninstallerPath = Path.Combine(AppContext.BaseDirectory, InstallerConstants.UninstallerStagingFolderName, InstallerConstants.UninstallerSourceFileName);
		if (File.Exists(stagedUninstallerPath))
		{
			File.Copy(stagedUninstallerPath, uninstallerPath, overwrite: true);
			return uninstallerPath;
		}

		return string.Empty;
	}

	private static bool TryExtractEmbeddedUninstaller(string targetPath)
	{
		var assembly = typeof(MainWindow).Assembly;
		using var stream = assembly.GetManifestResourceStream(InstallerConstants.EmbeddedUninstallerResourceName);
		if (stream is null)
		{
			return false;
		}

		using var fileStream = File.Create(targetPath);
		stream.CopyTo(fileStream);
		return true;
	}

	[SupportedOSPlatform("windows")]
	private static async Task InstallOrUpdateDeviceHostServiceAsync(string installPath)
	{
		var hostExecutablePath = Path.Combine(installPath, InstallLayout.DeviceHostFolderName, InstallLayout.DeviceHostExeName);
		if (!File.Exists(hostExecutablePath))
		{
			throw new FileNotFoundException("Device host executable was not found in installation payload.", hostExecutablePath);
		}

		var quotedHostPath = $"\"{hostExecutablePath}\"";
		var createArgs = $"create \"{InstallLayout.DeviceHostServiceName}\" binPath= {quotedHostPath} start= auto DisplayName= \"{InstallerConstants.DeviceHostServiceDisplayName}\"";

		await RunScOrThrowAsync(createArgs, ServiceControl.ErrorServiceExists);
		await RunScOrThrowAsync($"config \"{InstallLayout.DeviceHostServiceName}\" binPath= {quotedHostPath} start= auto");
		await RunScOrThrowAsync($"description \"{InstallLayout.DeviceHostServiceName}\" \"{InstallerConstants.DeviceHostServiceDescription}\"");
		await RunScOrThrowAsync($"failure \"{InstallLayout.DeviceHostServiceName}\" reset= 86400 actions= restart/5000/restart/5000/restart/5000");
		await RunScOrThrowAsync($"failureflag \"{InstallLayout.DeviceHostServiceName}\" 1");
		await RunScOrThrowAsync($"start \"{InstallLayout.DeviceHostServiceName}\"", ServiceControl.ErrorServiceAlreadyRunning);
	}

	// Every decision is made on sc.exe's exit code; its text is localised mojibake (see
	// ServiceControl) and is carried only for diagnostics. The user-facing message is built from
	// the code instead.
	[SupportedOSPlatform("windows")]
	private static async Task RunScOrThrowAsync(string arguments, int toleratedExitCode = 0)
	{
		var result = await ServiceControl.RunScAsync(arguments);
		if (result.ExitCode != 0 && result.ExitCode != toleratedExitCode)
		{
			throw new ServiceCommandException(result.ExitCode, $"sc.exe {arguments} failed with exit code {result.ExitCode}: {result.Output}");
		}
	}

	// Carries the Win32 code so the UI can show a readable, localised message instead of sc.exe's
	// undecodable output.
	private sealed class ServiceCommandException(int exitCode, string message) : InvalidOperationException(message)
	{
		public int ExitCode { get; } = exitCode;
	}


	// Stops a previous uninstall's DETACHED self-delete process before we write a single file.
	//
	// The uninstaller cannot delete its own EXE while running, so it leaves behind a PowerShell (or
	// cmd) loop that keeps trying to remove the install folder for up to ~2 minutes. That loop
	// deletes A PATH; it cannot tell the old install from a new one. Uninstall, reinstall straight
	// away, and its next pass deletes part of what we just copied - an app missing arbitrary files
	// that then dies on whichever native DLL it lost. Diagnosed in
	// docs/support/missing-files-crash-analysis.md (cause 0).
	//
	// Two independent stops, because either alone leaves a window:
	//   * removing the marker makes the loop exit on its NEXT iteration - but not one already
	//     inside Remove-Item;
	//   * killing the process stops that in-flight pass - but only if we can identify it.
	// The pid comes from the marker the uninstaller wrote, so this targets that exact process
	// rather than guessing from command lines.
	[SupportedOSPlatform("windows")]
	private static void StopLingeringUninstallCleanup(string installPath)
	{
		try
		{
			if (!UninstallHandoff.TryRead(installPath, out var pid, out var startedUtc))
			{
				return;
			}

			using var process = Process.GetProcessById(pid);
			if (!UninstallHandoff.ShouldStopSelfDeleteProcess(
					process.ProcessName, process.StartTime.ToUniversalTime(), startedUtc, DateTime.UtcNow))
			{
				return;
			}

			// entireProcessTree: false, matching CloseRunningAppAsync below and for the same reason
			// - a recorded id can only ever be a hint, so a wrong guess must cost one process, not a
			// whole tree. The cmd fallback's children (attrib / del / ping) are short-lived and exit
			// on their own once the loop stops.
			process.Kill(entireProcessTree: false);
			process.WaitForExit(5000);
		}
		catch (ArgumentException)
		{
			// No process with that id - already exited, which is the common case and the good one.
		}
		catch (InvalidOperationException)
		{
		}
		catch (Win32Exception)
		{
		}
		finally
		{
			// Outside the early returns on purpose: dropping the marker is the half of the fix that
			// works even when the process cannot be identified or stopped, because a surviving loop
			// re-reads it every iteration and exits once it is gone. Removing a marker no loop owns
			// is harmless.
			UninstallHandoff.Remove(installPath);
		}
	}

	// Drops any reboot-scheduled deletion that targets the folder we are about to install into.
	//
	// KEPT FOR UNINSTALLERS ALREADY ON USERS' MACHINES. Our uninstaller no longer schedules anything
	// for reboot deletion — that mechanism was removed on 2026-09-15 because it condemns a PATH and
	// so destroyed the next install into the same folder (see UninstallService's class comment and
	// docs/support/missing-files-crash-analysis.md, cause 3). But every copy shipped before that
	// still does it, and the uninstaller a user runs is the OLD one: it lives in the install folder
	// being replaced, so it has already written its entries by the time this installer starts.
	// This stays until those builds are gone from the field.
	//
	// MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT) records the paths in the registry for Windows to
	// delete at the next boot. That schedule survives everything — including a re-install. A user
	// who uninstalls and then re-installs WITHOUT rebooting gets a working app that quietly loses
	// its files (the uninstaller exe among them) at the next restart, leaving an install that can no
	// longer be removed. Clearing our own entries here is what breaks that cycle.
	//
	// Entries are (source, destination) pairs in a REG_MULTI_SZ; an empty destination means delete.
	// Only pairs pointing inside the install folder are removed — other applications' pending
	// operations live in the same value and must survive untouched.
	[SupportedOSPlatform("windows")]
	private static void ClearPendingRebootDeletions(string installPath)
	{
		const string sessionManagerKeyPath = @"SYSTEM\CurrentControlSet\Control\Session Manager";
		const string pendingOperationsValue = "PendingFileRenameOperations";

		try
		{
			using var sessionManagerKey = Registry.LocalMachine.OpenSubKey(sessionManagerKeyPath, writable: true);
			if (sessionManagerKey?.GetValue(pendingOperationsValue) is not string[] entries || entries.Length == 0)
			{
				return;
			}

			var fullInstallPath = Path.GetFullPath(installPath).TrimEnd(Path.DirectorySeparatorChar);
			var kept = new List<string>(entries.Length);

			for (var i = 0; i < entries.Length; i += 2)
			{
				var source = entries[i];
				var destination = i + 1 < entries.Length ? entries[i + 1] : string.Empty;

				if (PendingOperationTargets(source, fullInstallPath))
				{
					continue;
				}

				kept.Add(source);
				kept.Add(destination);
			}

			if (kept.Count == entries.Length)
			{
				return;
			}

			if (kept.Count == 0)
			{
				sessionManagerKey.DeleteValue(pendingOperationsValue, throwOnMissingValue: false);
			}
			else
			{
				sessionManagerKey.SetValue(pendingOperationsValue, kept.ToArray(), RegistryValueKind.MultiString);
			}
		}
		catch (Exception ex)
		{
			// Still best effort — a stale schedule is worth clearing, but never at the cost of the
			// install. It is no longer SILENT, though. For anyone uninstalling with a build from
			// before 2026-09-15 this guard is the only thing standing between them and an install
			// that deletes itself at the next boot, and it has already failed once in the field for
			// reasons nobody could establish, because this catch left nothing behind to establish
			// them from. The next failure leaves a trace.
			Debug.WriteLine($"clearing reboot-scheduled deletions failed: {ex}");
			try
			{
				File.AppendAllText(
					Path.Combine(Path.GetTempPath(), "proove-install.log"),
					$"{DateTime.Now:yyyy-MM-dd HH:mm:ss} clearing reboot-scheduled deletions failed: {ex}{Environment.NewLine}");
			}
			catch
			{
				// best-effort only
			}
		}
	}

	// Pending-operation paths use the NT device prefix (\??\C:\...), so it is stripped before the
	// comparison against the install folder.
	private static bool PendingOperationTargets(string entry, string fullInstallPath)
	{
		const string ntPathPrefix = @"\??\";

		if (string.IsNullOrWhiteSpace(entry))
		{
			return false;
		}

		var path = entry.StartsWith(ntPathPrefix, StringComparison.Ordinal)
			? entry[ntPathPrefix.Length..]
			: entry;
		path = path.TrimEnd(Path.DirectorySeparatorChar);

		return path.Equals(fullInstallPath, StringComparison.OrdinalIgnoreCase) ||
			path.StartsWith(fullInstallPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
	}

	// Stops the Device Host service and waits for its process to actually exit.
	//
	// Re-installing over an existing install fails without this: the running service holds
	// ProoveGaming.DeviceHost.exe open, so copying the payload over it throws. Waiting on the
	// PROCESS rather than sc.exe's status text is deliberate — the status words are localised, but a
	// missing process is unambiguous and is exactly the condition the file copy needs.
	[SupportedOSPlatform("windows")]
	private static async Task StopDeviceHostServiceAsync(CancellationToken cancellationToken)
	{
		var stopResult = await ServiceControl.RunScAsync($"stop \"{InstallLayout.DeviceHostServiceName}\"");

		// Nothing to stop is a perfectly good outcome — first install, or already stopped.
		if (stopResult.ExitCode is ServiceControl.ErrorServiceDoesNotExist or ServiceControl.ErrorServiceNotActive)
		{
			return;
		}

		var deadline = DateTime.UtcNow.AddSeconds(ServiceStopTimeoutSeconds);
		while (DateTime.UtcNow < deadline)
		{
			cancellationToken.ThrowIfCancellationRequested();

			var hostProcesses = Process.GetProcessesByName(InstallerConstants.DeviceHostProcessName);
			try
			{
				if (hostProcesses.Length == 0) return;
			}
			finally
			{
				foreach (var hostProcess in hostProcesses) hostProcess.Dispose();
			}

			await Task.Delay(ServiceStopPollMs, cancellationToken);
		}

		// Fall through on timeout: the copy may still succeed, and if it doesn't the copy's own
		// error is the more accurate thing to surface.
	}

	// Closes the running desktop app so its files can be replaced.
	//
	// The app keeps ProoveGaming.App.exe and its DLLs open, so a re-install fails on the copy while
	// it is running — the same reason the Device Host service is stopped first. Asking politely is
	// tried first, but it is NOT the mechanism: closing the app's window hides it to the tray and
	// leaves the process alive, so the kill is what actually frees the files. Settings are persisted
	// as they change, so ending the process does not discard user data.
	[SupportedOSPlatform("windows")]
	private static async Task CloseRunningAppAsync(CancellationToken cancellationToken)
	{
		var processes = GetOwnedAppProcesses();
		if (processes.Count == 0) return;

		try
		{
			TryEach(processes, process => process.CloseMainWindow());

			if (await WaitForExitAsync(processes, AppCloseGraceSeconds, cancellationToken))
			{
				return;
			}

			// entireProcessTree: false is deliberate. The app opens the account pages with
			// UseShellExecute, so the user's BROWSER can be a child of it — a tree kill would take
			// their browser (and its open tabs) down with the app. Only the app itself holds the
			// files being replaced, so its children are none of the installer's business.
			TryEach(processes, process => process.Kill(entireProcessTree: false));
			await WaitForExitAsync(processes, AppKillWaitSeconds, cancellationToken);

			// Fall through on timeout: as with the service, the copy's own error is the more accurate
			// thing to surface if the files really are still locked.
		}
		finally
		{
			foreach (var process in processes) process.Dispose();
		}
	}

	// The app processes this installer may act on.
	//
	// Scoped to the CURRENT Windows session: the installer runs elevated, which is enough privilege
	// to kill another signed-in user's processes, and closing someone else's app because this user
	// is installing would be indefensible. Fast User Switching makes that a real configuration, not
	// a hypothetical one.
	//
	// The handles are held for the whole close-and-kill sequence rather than re-enumerated. That is
	// what makes the "is it still running?" check safe: an open handle stops Windows from recycling
	// the PID, so a process that exits after the check can never be confused with a new, unrelated
	// process occupying the same PID by the time Kill runs.
	[SupportedOSPlatform("windows")]
	private static List<Process> GetOwnedAppProcesses()
	{
		var owned = new List<Process>();
		var currentSessionId = GetCurrentSessionId();

		foreach (var process in Process.GetProcessesByName(InstallerConstants.AppProcessName))
		{
			var isOwned = false;
			try
			{
				isOwned = !process.HasExited && process.SessionId == currentSessionId;
			}
			catch
			{
				// Unreadable means exited or out of reach — either way, not ours to touch.
			}

			if (isOwned) owned.Add(process);
			else process.Dispose();
		}

		return owned;
	}

	[SupportedOSPlatform("windows")]
	private static int GetCurrentSessionId()
	{
		using var current = Process.GetCurrentProcess();
		return current.SessionId;
	}

	// Every action here races the process's own exit, which is normal rather than exceptional —
	// and the HasExited guard is what keeps a already-closed app from being killed again.
	[SupportedOSPlatform("windows")]
	private static void TryEach(List<Process> processes, Action<Process> action)
	{
		foreach (var process in processes)
		{
			try { if (!process.HasExited) action(process); }
			catch { }
		}
	}

	// True when every tracked process is gone before the timeout.
	[SupportedOSPlatform("windows")]
	private static async Task<bool> WaitForExitAsync(List<Process> processes, int timeoutSeconds, CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
		while (DateTime.UtcNow < deadline)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (AllExited(processes)) return true;

			await Task.Delay(ServiceStopPollMs, cancellationToken);
		}

		return AllExited(processes);
	}

	[SupportedOSPlatform("windows")]
	private static bool AllExited(List<Process> processes)
	{
		foreach (var process in processes)
		{
			// An unreadable process is treated as gone: it cannot be holding the files open.
			try { if (!process.HasExited) return false; }
			catch { }
		}

		return true;
	}

	private const int ServiceStopTimeoutSeconds = 15;
	private const int ServiceStopPollMs = 250;

	// Short: the app hides to the tray instead of exiting, so this grace period almost always
	// elapses in full. Long enough for a genuine shutdown, short enough not to stall the install.
	private const int AppCloseGraceSeconds = 3;
	private const int AppKillWaitSeconds = 5;

	// Per-machine, in HKLM: the install is machine-wide, so every user sees it in Settings → Apps,
	// and only administrators can change the InstallLocation the elevated uninstaller trusts. The
	// per-user HKCU entry earlier builds wrote is removed so the Apps list does not show it twice.
	[SupportedOSPlatform("windows")]
	private static void RegisterWindowsUninstallEntry(string installPath, string appExePath, string uninstallerPath)
	{
		try
		{
			Registry.CurrentUser.DeleteSubKeyTree(InstallLayout.UninstallRegistryKeyPath, throwOnMissingSubKey: false);
		}
		catch
		{
			// Best effort: a leftover legacy entry is a duplicate line in Settings → Apps, never worth
			// failing an install that is otherwise complete.
		}

		using var uninstallKey = Registry.LocalMachine.CreateSubKey(InstallLayout.UninstallRegistryKeyPath, writable: true);
		if (uninstallKey is null)
		{
			return;
		}

		var uninstallArgs = $"{InstallLayout.UninstallArgument} \"{installPath}\"";

		uninstallKey.SetValue("DisplayName", InstallLayout.ProductName, RegistryValueKind.String);
		uninstallKey.SetValue("Publisher", InstallerConstants.PublisherName, RegistryValueKind.String);
		uninstallKey.SetValue("DisplayVersion", "1.0.0", RegistryValueKind.String);
		uninstallKey.SetValue("InstallLocation", installPath, RegistryValueKind.String);
		uninstallKey.SetValue("DisplayIcon", appExePath, RegistryValueKind.String);
		uninstallKey.SetValue("NoModify", 1, RegistryValueKind.DWord);
		uninstallKey.SetValue("NoRepair", 1, RegistryValueKind.DWord);
		uninstallKey.SetValue("UninstallString", $"\"{uninstallerPath}\" {uninstallArgs}", RegistryValueKind.String);
		uninstallKey.SetValue("QuietUninstallString", $"\"{uninstallerPath}\" {uninstallArgs} --quiet", RegistryValueKind.String);
	}

	[SupportedOSPlatform("windows")]
	private static void CreateWindowsShortcut(string shortcutPath, string targetPath, string workingDirectory)
	{
		var shellType = Type.GetTypeFromProgID("WScript.Shell");
		if (shellType is null)
		{
			return;
		}

		dynamic shell = Activator.CreateInstance(shellType)!;
		dynamic shortcut = shell.CreateShortcut(shortcutPath);
		shortcut.TargetPath = targetPath;
		shortcut.WorkingDirectory = workingDirectory;
		shortcut.IconLocation = targetPath;
		shortcut.Save();
	}

	// Only an existing Proove Gaming install is reused as it is (ProtectExistingTree re-protects it
	// before the copy). Any other folder, which validation admits only when it is empty, is removed
	// and created again by this process with the protected DACL already on it: a folder someone else
	// created could still be owned by them, or held open with rights granted before it was locked.
	// Returns whether the folder still counts as pre-existing for rollback: false once it has been
	// recreated here, so a cancelled install removes it (it was empty). Pinned by
	// InstallFolderPreparationTests.
	[SupportedOSPlatform("windows")]
	internal static bool PrepareInstallFolder(string installPath, bool preexisted, Func<string, bool> isProoveGamingInstall)
	{
		if (preexisted && isProoveGamingInstall(installPath))
		{
			return true;
		}

		if (preexisted)
		{
			// Non-recursive on purpose: it fails, stopping the install, if the folder is no longer
			// empty or someone holds it open.
			Directory.Delete(installPath);
		}

		var parent = Path.GetDirectoryName(Path.GetFullPath(installPath));
		if (!string.IsNullOrEmpty(parent))
		{
			Directory.CreateDirectory(parent);
		}

		// False means something re-appeared at the path between the delete and here: not ours.
		if (!InstallAcl.TryCreateNewProtectedDirectory(installPath, allowUsersReadAndExecute: true))
		{
			throw new UnsafeInstallFolderException(installPath);
		}

		return false;
	}

	private async Task CopyDirectoryAsync(string sourcePath, string targetPath, CancellationToken cancellationToken)
	{
		await Task.Run(async () =>
		{
			Directory.CreateDirectory(targetPath);

			foreach (var sourceFilePath in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
			{
				await WaitIfInstallPausedOrCancelledAsync(cancellationToken);

				var relativePath = Path.GetRelativePath(sourcePath, sourceFilePath);
				var targetFilePath = Path.Combine(targetPath, relativePath);
				var targetDirectory = Path.GetDirectoryName(targetFilePath);
				if (!string.IsNullOrEmpty(targetDirectory))
				{
					Directory.CreateDirectory(targetDirectory);
				}

				// Replaced, never truncated in place: a new file takes the protected folder's inherited
				// rules, while an overwritten one would keep the owner and rules it had. The delete also
				// fails, stopping the install, if another process still holds the old file open.
				File.Delete(targetFilePath);
				await using var sourceStream = File.Open(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
				await using var targetStream = new FileStream(targetFilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
				await sourceStream.CopyToAsync(targetStream, cancellationToken);
			}
		}, cancellationToken);
	}

	private static async Task<string?> PreparePayloadAsync()
	{
		var embeddedPayload = await ExtractEmbeddedPayloadAsync();
		if (embeddedPayload is not null)
		{
			return embeddedPayload;
		}

		var folderPayload = Path.Combine(AppContext.BaseDirectory, InstallerConstants.PayloadFolderName);
		return Directory.Exists(folderPayload) ? folderPayload : null;
	}

	// Where the embedded payload is unpacked before it is copied into the install folder.
	//
	// NOT %TEMP%. This process is elevated, but %TEMP% belongs to the user, so any process running as
	// that user could swap a host\ DLL between extraction and copy, and the LocalSystem service would
	// load it. Each extraction gets a fresh folder created with an administrators-only ACL DIRECTLY
	// under %ProgramData%, where a standard user cannot rename or replace an entry they do not own.
	// Directly matters: under a shared parent a user could pre-create that parent, own it, and so
	// rename the protected folder away and put their own in its place.
	private static string StagingFolderNamePrefix => $"{InstallLayout.AppDataFolderName}Installer.";

	private static string StagingParent => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

	// Older than any install could take: a younger folder may belong to another installer window
	// that is extracting right now.
	private static readonly TimeSpan AbandonedStagingAge = TimeSpan.FromHours(1);

	// Removes staging folders a previous run could not clean up because it never reached its finally
	// block (killed from Task Manager, crashed, power lost). Unlike %TEMP%, where staging used to
	// live, nothing else ever purges %ProgramData%, so without this each such run would leave the
	// whole payload behind for good. Best effort: never worth failing an install over.
	[SupportedOSPlatform("windows")]
	private static void RemoveAbandonedStagingFolders()
	{
		try
		{
			foreach (var folder in Directory.EnumerateDirectories(StagingParent, StagingFolderNamePrefix + "*"))
			{
				try
				{
					var info = new DirectoryInfo(folder);
					if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
						!IsSweepableStagingFolder(info.Name, info.CreationTimeUtc, DateTime.UtcNow, InstallSignature.IsOwnedByTrustedPrincipal(folder)))
					{
						continue;
					}

					Directory.Delete(folder, recursive: true);
				}
				catch
				{
					// best-effort only
				}
			}
		}
		catch
		{
			// best-effort only
		}
	}

	// This runs elevated inside %ProgramData%, where any user can create folders, so a folder is
	// swept only when it is provably ours and provably abandoned: exactly the prefix plus a
	// 32-hex-digit GUID, owned by Administrators or SYSTEM (a standard user cannot create one with
	// that owner), and older than AbandonedStagingAge.
	internal static bool IsSweepableStagingFolder(string folderName, DateTime createdUtc, DateTime nowUtc, bool ownerIsTrusted)
	{
		var prefix = StagingFolderNamePrefix;
		if (!ownerIsTrusted ||
			folderName.Length != prefix.Length + 32 ||
			!folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
			!Guid.TryParseExact(folderName.AsSpan(prefix.Length), "N", out _))
		{
			return false;
		}

		return nowUtc - createdUtc > AbandonedStagingAge;
	}

	// Runs entirely on a background thread. ZipFile.ExtractToDirectory is synchronous and unpacks the
	// whole self-contained payload (hundreds of MB), so on the UI thread it froze the window for the
	// entire extraction — the Cancel button appeared dead until it finished. Task.Run keeps the UI
	// responsive so the cancel dialog opens immediately.
	private static Task<string?> ExtractEmbeddedPayloadAsync() => Task.Run<string?>(async () =>
	{
		var assembly = typeof(MainWindow).Assembly;
		await using var payloadStream = assembly.GetManifestResourceStream(InstallerConstants.EmbeddedPayloadResourceName);
		if (payloadStream is null)
		{
			return null;
		}

		var extractionPath = Path.Combine(StagingParent, StagingFolderNamePrefix + Guid.NewGuid().ToString("N"));
		if (OperatingSystem.IsWindows())
		{
			RemoveAbandonedStagingFolders();
			InstallAcl.CreateProtectedDirectory(extractionPath, allowUsersReadAndExecute: false);
		}
		else
		{
			Directory.CreateDirectory(extractionPath);
		}

		var zipPath = Path.Combine(extractionPath, "app.zip");
		await using (var targetZipStream = File.Create(zipPath))
		{
			await payloadStream.CopyToAsync(targetZipStream);
		}

		ZipFile.ExtractToDirectory(zipPath, extractionPath, overwriteFiles: true);
		File.Delete(zipPath);

		RestoreSharedRuntime(extractionPath);

		return extractionPath;
	});

	// Puts back the runtime files the build stripped from host\ because the App directory already
	// carried a byte-identical copy — 338 files and 107 MB, which is 51 MB of the download.
	//
	// Runs on the extracted payload in temp, BEFORE it is copied to the install directory, so every
	// later step sees a complete, ordinary self-contained host\ and nothing else in the installer
	// needs to know this happened.
	//
	// Failures throw rather than warn: a host\ missing one runtime file produces a service that will
	// not start, and diagnosing that after the fact is far worse than failing the install here.
	private static void RestoreSharedRuntime(string payloadRoot)
	{
		var hostDirectory = Path.Combine(payloadRoot, InstallLayout.DeviceHostFolderName);
		var manifestPath = Path.Combine(hostDirectory, InstallerConstants.SharedRuntimeManifestName);

		// A payload built before this optimisation carries no manifest and needs no restoring.
		if (!File.Exists(manifestPath))
		{
			return;
		}

		foreach (var line in File.ReadAllLines(manifestPath))
		{
			var fileName = line.Trim();
			if (fileName.Length == 0)
			{
				continue;
			}

			// The manifest holds bare file names by construction. Anything else — a directory
			// separator, a drive, a "..' segment — means the payload is not the one we built, and
			// honouring it would let an altered archive write outside host\.
			if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal))
			{
				throw new InvalidDataException(
					$"Shared-runtime manifest entry '{fileName}' is not a plain file name.");
			}

			var source = Path.Combine(payloadRoot, fileName);
			if (!File.Exists(source))
			{
				throw new FileNotFoundException(
					$"Shared-runtime file '{fileName}' is listed in the manifest but missing from the payload.",
					source);
			}

			File.Copy(source, Path.Combine(hostDirectory, fileName), overwrite: true);
		}

		File.Delete(manifestPath);
	}

	private static string? ResolveInstalledAppExecutablePath(string installPath)
	{
		var expectedPath = Path.Combine(installPath, InstallLayout.AppExeName);
		if (File.Exists(expectedPath))
		{
			return expectedPath;
		}

		if (!Directory.Exists(installPath))
		{
			return null;
		}

		var fallbackPath = Directory
			.EnumerateFiles(installPath, "*.exe", SearchOption.AllDirectories)
			.FirstOrDefault(path =>
			{
				var fileName = Path.GetFileName(path);
				return !string.Equals(fileName, InstallLayout.UninstallerExeName, StringComparison.OrdinalIgnoreCase) &&
					   !string.Equals(fileName, "ProoveGaming.Installer.exe", StringComparison.OrdinalIgnoreCase);
			});

		return fallbackPath;
	}

	// Also fully off the UI thread: reading the embedded archive's directory means pulling the
	// resource out of the single-file exe, which is slow enough to stall the window on the folder
	// step. The folder-payload branch already used Task.Run; this puts both paths on the same footing.
	private static Task<long> GetRequiredInstallSizeBytesAsync() => Task.Run(() =>
	{
		var assembly = typeof(MainWindow).Assembly;
		using var payloadStream = assembly.GetManifestResourceStream(InstallerConstants.EmbeddedPayloadResourceName);
		if (payloadStream is not null)
		{
			using var zipArchive = new ZipArchive(payloadStream, ZipArchiveMode.Read, leaveOpen: false);
			return zipArchive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).Sum(e => e.Length);
		}

		var folderPayload = Path.Combine(AppContext.BaseDirectory, InstallerConstants.PayloadFolderName);
		if (Directory.Exists(folderPayload))
		{
			return Directory.EnumerateFiles(folderPayload, "*", SearchOption.AllDirectories)
				.Select(path => new FileInfo(path).Length)
				.Sum();
		}

		return 0L;
	});

	private static long GetAvailableBytesForInstallPath(string installPath)
	{
		try
		{
			var rootPath = Path.GetPathRoot(Path.GetFullPath(installPath));
			if (string.IsNullOrWhiteSpace(rootPath))
			{
				return -1;
			}

			var driveInfo = new DriveInfo(rootPath);
			return driveInfo.AvailableFreeSpace;
		}
		catch
		{
			return -1;
		}
	}

	private static string FormatSize(long bytes)
	{
		var size = bytes;
		string[] units = ["B", "KB", "MB", "GB", "TB"];
		var unitIndex = 0;
		double value = size;

		while (value >= 1024 && unitIndex < units.Length - 1)
		{
			value /= 1024;
			unitIndex++;
		}

		return string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1}", value, units[unitIndex]);
	}
}
