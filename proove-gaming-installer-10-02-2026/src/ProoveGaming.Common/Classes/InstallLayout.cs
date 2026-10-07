namespace ProoveGaming.Common.Classes;

/// <summary>
/// The on-disk and registry layout the installer writes and the uninstaller reads back. One copy,
/// because the two sides drifting apart is invisible until an uninstall misses what the install
/// created.
/// </summary>
public static class InstallLayout
{
	public const string AppDataFolderName = "ProoveGaming";
	public const string AppExeName = $"{AppDataFolderName}.App.exe";
	public const string UninstallerExeName = $"{AppDataFolderName}.Uninstaller.exe";
	public const string DeviceHostFolderName = "host";
	public const string DeviceHostExeName = $"{AppDataFolderName}.DeviceHost.exe";
	public const string DeviceHostServiceName = "ProoveGamingDeviceHost";
	public const string ProductName = "Proove Gaming";

	// Opened under HKEY_LOCAL_MACHINE: the install is per-machine (Program Files, a LocalSystem
	// service), and HKLM is writable only by administrators, so the elevated uninstaller can trust
	// what it reads there. Builds before 2026-09-24 wrote the same subkey under HKEY_CURRENT_USER,
	// which any user-level process can rewrite; that copy is only ever deleted, never read.
	public const string UninstallRegistryKeyPath = $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AppDataFolderName}";

	// Passed by the registered UninstallString ahead of the install folder.
	public const string UninstallArgument = "--uninstall";
}
