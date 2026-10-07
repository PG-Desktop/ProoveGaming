using System.Diagnostics;
using System.Runtime.Versioning;

namespace ProoveGaming.Common.Classes;

/// <summary>
/// Absolute paths for the Windows tools the elevated installer and uninstaller start.
/// </summary>
/// <remarks>
/// Never start them by bare name. With <c>UseShellExecute = false</c>, <c>CreateProcess</c>
/// searches the directory the running EXE was loaded from BEFORE <c>System32</c>, and an installer
/// usually runs from the Downloads folder — so an <c>sc.exe</c> dropped next to it would run with
/// administrator rights (binary planting, docs/plans/fix-repository-audit-findings S5).
/// </remarks>
public static class SystemTool
{
	public static string PathOf(string exeName) => Path.Combine(Environment.SystemDirectory, exeName);

	public static string PowerShellPath =>
		Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
}

public readonly record struct ServiceCommandResult(int ExitCode, string Output);

/// <summary>
/// The one <c>sc.exe</c> runner shared by the installer and the uninstaller. It reports the exit
/// code and leaves the tolerate-or-throw policy to the caller, because the two sides legitimately
/// tolerate different failures.
/// </summary>
public static class ServiceControl
{
	// Win32 service error codes sc.exe returns as its exit code. These are the reliable signal:
	// sc.exe's TEXT is localised, so matching English phrases like "already exists" silently fails on
	// non-English Windows.
	public const int ErrorServiceAlreadyRunning = 1056;
	public const int ErrorServiceDoesNotExist = 1060;
	public const int ErrorServiceNotActive = 1062;
	public const int ErrorServiceExists = 1073;

	[SupportedOSPlatform("windows")]
	public static Task<ServiceCommandResult> RunScAsync(string arguments, CancellationToken cancellationToken = default) =>
		RunCapturedAsync(SystemTool.PathOf("sc.exe"), arguments, cancellationToken);

	// Both streams are drained concurrently. Reading stdout to the end before touching stderr
	// deadlocks as soon as the child fills the stderr pipe buffer: it blocks writing, never exits,
	// and stdout never ends.
	//
	// sc.exe writes in the console's OEM code page, which is not decoded here, so the text arrives as
	// mojibake on localised systems. It is kept for diagnostics only; decisions belong on ExitCode.
	internal static async Task<ServiceCommandResult> RunCapturedAsync(string fileName, string arguments, CancellationToken cancellationToken)
	{
		var processStartInfo = new ProcessStartInfo
		{
			FileName = fileName,
			Arguments = arguments,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};

		using var process = Process.Start(processStartInfo) ?? throw new InvalidOperationException($"Failed to start {Path.GetFileName(fileName)}.");

		var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
		var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
		await Task.WhenAll(outputTask, errorTask);
		await process.WaitForExitAsync(cancellationToken);

		var output = await outputTask;
		var error = await errorTask;
		var combinedOutput = string.Join(Environment.NewLine, new[] { output, error }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
		return new ServiceCommandResult(process.ExitCode, combinedOutput);
	}
}
