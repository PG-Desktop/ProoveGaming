using Avalonia;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace ProoveGaming.Installer;

internal static class Program
{
	[STAThread]
	public static void Main(string[] args)
	{
		LaunchContext.StartupArgs = args;

		var requireElevation = !Debugger.IsAttached;
#if DEBUG
		requireElevation = false;
#endif

		if (requireElevation && !EnsureElevated(LaunchContext.StartupArgs))
		{
			return;
		}

		try
		{
			BuildAvaloniaApp().StartWithClassicDesktopLifetime(LaunchContext.StartupArgs);
		}
		catch (Exception ex)
		{
			File.WriteAllText("installer-crash.log", ex.ToString());
			throw;
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

			var arguments = string.Join(" ", args.Select(QuoteArgument));
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
