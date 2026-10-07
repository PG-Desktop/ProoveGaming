using ProoveGaming.Common.Classes;

namespace ProoveGaming.Installer;

internal static class InstallerConstants
{
	public const string EmbeddedPayloadResourceName = $"{InstallLayout.AppDataFolderName}.Installer.Payload.app.zip";
	public const string EmbeddedUninstallerResourceName = $"{InstallLayout.AppDataFolderName}.Installer.Uninstaller.win-x64.exe";
	public const string PayloadFolderName = "AppPayload";
	public const string UninstallerStagingFolderName = "UninstallerPayload";
	public const string UninstallerSourceFileName = "ProoveGaming.Uninstaller.win-x64.exe";
	// Lists the runtime files stripped from host\ at build time because they are byte-identical to
	// the App's own copy. The installer copies each one back after extraction, so the host directory
	// on disk ends up exactly as a plain self-contained publish would have left it. Written by the
	// DedupeSharedRuntime task in ProoveGaming.Installer.csproj.
	public const string SharedRuntimeManifestName = "shared-runtime.manifest";
	// Process name (no extension) used to wait for the service to actually exit before copying over it.
	public const string DeviceHostProcessName = $"{InstallLayout.AppDataFolderName}.DeviceHost";
	// Same, for the desktop app — it holds its own exe and DLLs open while it runs.
	public const string AppProcessName = $"{InstallLayout.AppDataFolderName}.App";
	public const string DeviceHostServiceDisplayName = "Proove Gaming Device Host";
	public const string DeviceHostServiceDescription = "Hosts and manages Proove Gaming device integrations.";
	public const string PublisherName = "Proove Gaming";
	public const uint DwmwaWindowCornerPreference = 33;
	public const uint DwmcpDoNotRound = 1;
}
