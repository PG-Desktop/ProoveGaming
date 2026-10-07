using System.Security.AccessControl;
using System.Security.Principal;

namespace ProoveGaming.Common.Classes;

/// <summary>
/// Decides whether a folder is a Proove Gaming install before anything acts on it with
/// administrator rights.
/// </summary>
/// <remarks>
/// <para>
/// The uninstaller deletes its install folder recursively while elevated, and the installer locks a
/// reused folder down and copies into it, so the folder must be proven to be ours. A path is only a
/// claim. The proof is PROVENANCE: the folder, and at least one of our files in it
/// (<see cref="InstallLayout.AppExeName"/>, <see cref="InstallLayout.UninstallerExeName"/>, or the
/// DeviceHost EXE under <see cref="InstallLayout.DeviceHostFolderName"/>), are owned by
/// Administrators, SYSTEM or TrustedInstaller. Our elevated installer creates them that way, and a
/// standard user cannot: a folder or file they create, including a copy of one of our EXEs (they
/// are readable), is owned by them. The folder must also not be a place no install should ever be
/// (a drive root, a Windows, Program Files, ProgramData or profile folder) or a junction or symbolic
/// link, whose recursive delete could reach somewhere else entirely.
/// </para>
/// <para>
/// ONE marker is enough, deliberately. Antivirus quarantines single unsigned files and leaves the
/// rest (docs/support/missing-files-crash-analysis.md §1), an unfinished self-delete leaves the
/// uninstaller behind, and an interrupted uninstall may already have removed host\. All of those are
/// still our installs, and they must stay repairable by a reinstall and removable by an uninstall;
/// requiring every file would strand them (docs/plans/fix-repository-audit-findings, review round 1 B1).
/// </para>
/// </remarks>
public static class InstallSignature
{
	public static bool IsProoveGamingInstall(string? path) => IsProoveGamingInstall(path, IsTrustedByProvenance);

	// Owned by a principal a standard user cannot become, OR inside Program Files, where only
	// administrators can create anything at all. The second half covers installs made by builds
	// before 2026-09-24 on a machine with UAC disabled: there the elevated installer's files were
	// owned by the installing account, and without it an in-place upgrade of the default install
	// would be refused.
	private static bool IsTrustedByProvenance(string path) =>
		IsOwnedByTrustedPrincipal(path) || IsInsideProgramFiles(path);

	/// <summary>
	/// True when <paramref name="path"/> is strictly inside <c>%ProgramFiles%</c> or
	/// <c>%ProgramFiles(x86)%</c> (not the folder itself). Standard users cannot create, rename or
	/// write anything there, so what is there was put there by an administrator.
	/// </summary>
	internal static bool IsInsideProgramFiles(string path)
	{
		string fullPath;
		try
		{
			fullPath = Normalise(path);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}

		foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
		{
			var programFiles = Environment.GetFolderPath(folder);
			if (!string.IsNullOrEmpty(programFiles) &&
				fullPath.StartsWith(Normalise(programFiles) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}

		return false;
	}

	// isTrustedOwner is the provenance test, injectable so the layout rules can be tested whether or
	// not the test process is elevated.
	internal static bool IsProoveGamingInstall(string? path, Func<string, bool> isTrustedOwner)
	{
		if (!IsAcceptableInstallLocation(path))
		{
			return false;
		}

		var fullPath = Normalise(path!);
		if (!Directory.Exists(fullPath) || !isTrustedOwner(fullPath))
		{
			return false;
		}

		var hostDirectory = Path.Combine(fullPath, InstallLayout.DeviceHostFolderName);
		var hostMarker = IsReparsePoint(hostDirectory) ? null : Path.Combine(hostDirectory, InstallLayout.DeviceHostExeName);

		return IsTrustedMarker(Path.Combine(fullPath, InstallLayout.AppExeName), isTrustedOwner) ||
			IsTrustedMarker(Path.Combine(fullPath, InstallLayout.UninstallerExeName), isTrustedOwner) ||
			(hostMarker is not null && IsTrustedMarker(hostMarker, isTrustedOwner));
	}

	/// <summary>
	/// True when <paramref name="path"/> (a file or a directory) is owned by Administrators, SYSTEM
	/// or TrustedInstaller — none of which a standard user can make themselves the owner as. False
	/// when the path is missing or its owner cannot be read.
	/// </summary>
	public static bool IsOwnedByTrustedPrincipal(string path)
	{
		if (!OperatingSystem.IsWindows())
		{
			return false;
		}

		try
		{
			FileSystemSecurity security = Directory.Exists(path)
				? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
				: new FileInfo(path).GetAccessControl(AccessControlSections.Owner);

			if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner)
			{
				return false;
			}

			return owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
				owner.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
				owner.Value == TrustedInstallerSid;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
		{
			return false;
		}
	}

	// NT SERVICE\TrustedInstaller: owns Windows' own files, and anything a servicing stack wrote.
	private const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

	private static bool IsTrustedMarker(string markerPath, Func<string, bool> isTrustedOwner) =>
		File.Exists(markerPath) && isTrustedOwner(markerPath);

	/// <summary>
	/// True when <paramref name="path"/> may hold an install at all, whether or not one is there
	/// yet: fully qualified, not a drive root, not a well-known system or profile folder, and not a
	/// reparse point.
	/// </summary>
	public static bool IsAcceptableInstallLocation(string? path)
	{
		if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
		{
			return false;
		}

		string fullPath;
		try
		{
			fullPath = Normalise(path);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}

		var root = Path.GetPathRoot(fullPath);
		if (string.IsNullOrEmpty(root) || string.Equals(Path.TrimEndingDirectorySeparator(root), fullPath, StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		foreach (var protectedFolder in ProtectedFolders())
		{
			if (string.Equals(protectedFolder, fullPath, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
		}

		return !IsReparsePoint(fullPath);
	}

	private static IEnumerable<string> ProtectedFolders()
	{
		Environment.SpecialFolder[] folders =
		[
			Environment.SpecialFolder.Windows,
			Environment.SpecialFolder.System,
			Environment.SpecialFolder.SystemX86,
			Environment.SpecialFolder.ProgramFiles,
			Environment.SpecialFolder.ProgramFilesX86,
			Environment.SpecialFolder.CommonProgramFiles,
			Environment.SpecialFolder.CommonProgramFilesX86,
			Environment.SpecialFolder.CommonApplicationData,
			Environment.SpecialFolder.CommonDesktopDirectory,
			Environment.SpecialFolder.CommonStartMenu,
			Environment.SpecialFolder.CommonPrograms,
			Environment.SpecialFolder.UserProfile,
			Environment.SpecialFolder.DesktopDirectory,
			Environment.SpecialFolder.MyDocuments,
			Environment.SpecialFolder.ApplicationData,
			Environment.SpecialFolder.LocalApplicationData,
			Environment.SpecialFolder.StartMenu,
			Environment.SpecialFolder.Programs,
		];

		foreach (var folder in folders)
		{
			var folderPath = Environment.GetFolderPath(folder);
			if (!string.IsNullOrEmpty(folderPath))
			{
				yield return Normalise(folderPath);
			}
		}

		// The parent of every profile (C:\Users) has no SpecialFolder of its own.
		var profilesRoot = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
		if (!string.IsNullOrEmpty(profilesRoot))
		{
			yield return Normalise(profilesRoot);
		}
	}

	private static bool IsReparsePoint(string path)
	{
		try
		{
			return Directory.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Unreadable is not evidence of safety.
			return true;
		}
	}

	private static string Normalise(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
