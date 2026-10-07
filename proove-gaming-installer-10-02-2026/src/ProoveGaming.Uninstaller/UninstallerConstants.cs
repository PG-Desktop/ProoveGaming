namespace ProoveGaming.Uninstaller;

// Uninstaller-only values. Everything the installer also needs lives in
// ProoveGaming.Common.Classes.InstallLayout.
internal static class UninstallerConstants
{
	// Per-user folders the pre-elevation instance forwards to the elevated one (see
	// Program.EnsureElevated): the elevated process may run under a different profile.
	public const string UserLocalAppDataArgument = "--user-local-appdata";
	public const string UserDesktopArgument = "--user-desktop";
	public const string UserProgramsArgument = "--user-programs";
}
