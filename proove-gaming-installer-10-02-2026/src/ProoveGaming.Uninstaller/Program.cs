using Avalonia;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace ProoveGaming.Uninstaller;

internal static class Program
{
	[STAThread]
	public static int Main(string[] args)
	{
		LaunchContext.StartupArgs = args;

		var requireElevation = !Debugger.IsAttached;
#if DEBUG
		requireElevation = false;
#endif

		if (requireElevation && !EnsureElevated(args))
		{
			return 0;
		}

		try
		{
			BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
			return 0;
		}
		catch
		{
			return 1;
		}
	}

	public static AppBuilder BuildAvaloniaApp() =>
		AppBuilder.Configure<App>()
			.UsePlatformDetect()
			.WithInterFont()
			.LogToTrace();

	private static bool EnsureElevated(string[] args)
	{
		if (!OperatingSystem.IsWindows())
		{
			return true;
		}

		using var identity = WindowsIdentity.GetCurrent();
		var principal = new WindowsPrincipal(identity);
		if (principal.IsInRole(WindowsBuiltInRole.Administrator))
		{
			return true;
		}

		try
		{
			var currentExePath = Environment.ProcessPath;
			if (string.IsNullOrWhiteSpace(currentExePath))
			{
				return true;
			}

			// This pre-elevation instance runs as the interactive user; the elevated relaunch may
			// run under a different (admin) profile, where %LocalAppData% and the shell folders
			// point at the wrong user. Forward the per-user folders across the elevation: the data
			// folder and both shortcut folders. The uninstall registry entry is per-machine (HKLM),
			// so nothing about the user's hive needs forwarding.
			var relaunchArgs = args
				.Append(UninstallerConstants.UserLocalAppDataArgument)
				.Append(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
				.Append(UninstallerConstants.UserDesktopArgument)
				.Append(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory))
				.Append(UninstallerConstants.UserProgramsArgument)
				.Append(Environment.GetFolderPath(Environment.SpecialFolder.Programs));
			var arguments = string.Join(" ", relaunchArgs.Select(QuoteArgument));
			_ = Process.Start(new ProcessStartInfo
			{
				FileName = currentExePath,
				Arguments = arguments,
				UseShellExecute = true,
				Verb = "runas"
			});

			return false;
		}
		catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
		{
			return false;
		}
	}

	private static string QuoteArgument(string value)
	{
		if (string.IsNullOrEmpty(value))
		{
			return "\"\"";
		}

		return value.Contains(' ') || value.Contains('"')
			? $"\"{value.Replace("\"", "\\\"")}\""
			: value;
	}
}
