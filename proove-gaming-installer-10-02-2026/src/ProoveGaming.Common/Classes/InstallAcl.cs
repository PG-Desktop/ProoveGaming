using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ProoveGaming.Common.Classes;

/// <summary>
/// The locked-down permissions the installer puts on every folder an elevated process or the
/// LocalSystem DeviceHost later trusts.
/// </summary>
/// <remarks>
/// <para>
/// A custom install folder inherits its parent's ACL, and on a data drive or <c>C:\Games</c> that
/// usually lets Authenticated Users modify. The DeviceHost EXE and every DLL it loads run as
/// LocalSystem from that folder, so an inherited "Modify" is a privilege escalation. Program Files
/// is safe only because of its own ACL; this gives every install location the same guarantee
/// (docs/plans/fix-repository-audit-findings S3).
/// </para>
/// <para>
/// The DACL is protected, so nothing is inherited from the parent: Administrators and SYSTEM get
/// full control, and optionally Users get read and execute. Everything is inheritable, so files
/// written into the folder later get the same rules.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class InstallAcl
{
	private const InheritanceFlags InheritToEverything = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

	private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
	private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
	private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

	public static DirectorySecurity BuildProtectedSecurity(bool allowUsersReadAndExecute)
	{
		var security = new DirectorySecurity();
		security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
		security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, InheritToEverything, PropagationFlags.None, AccessControlType.Allow));
		security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, InheritToEverything, PropagationFlags.None, AccessControlType.Allow));

		if (allowUsersReadAndExecute)
		{
			security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, InheritToEverything, PropagationFlags.None, AccessControlType.Allow));
		}

		return security;
	}

	/// <summary>
	/// Creates a directory that carries the protected DACL from the moment it exists, so there is
	/// no window in which a user-level process can open it for writing. Its name must be fresh (a
	/// new GUID): the underlying API leaves an already-existing directory untouched.
	/// </summary>
	public static DirectoryInfo CreateProtectedDirectory(string path, bool allowUsersReadAndExecute)
	{
		var directory = new DirectoryInfo(path);
		directory.Create(BuildProtectedSecurity(allowUsersReadAndExecute));
		return directory;
	}

	/// <summary>
	/// Makes Administrators the owner of a file the uninstaller treats as proof of an install (see
	/// <see cref="InstallSignature"/>). Only the owner is written; the file keeps its inherited DACL.
	/// Needed because an elevated process's new files are owned by Administrators only while UAC is
	/// on: with UAC disabled they belong to the installing account, which InstallSignature does not
	/// trust, and the install could then not be uninstalled. Requires an elevated caller.
	/// </summary>
	public static void MakeAdministratorsOwner(string filePath) => SetOwnerOnly(filePath, Administrators);

	// A FRESH FileSecurity carrying only an owner: only the owner section is written. Writing the
	// DACL section too, even an empty one, would leave the file readable by nobody, which for an
	// installed EXE means an App no user can start. Pinned by InstallAclTests without elevation
	// (any process may make itself the owner of its own file).
	internal static void SetOwnerOnly(string filePath, SecurityIdentifier owner)
	{
		var security = new FileSecurity();
		security.SetOwner(owner);
		new FileInfo(filePath).SetAccessControl(security);
	}

	/// <summary>
	/// Creates <paramref name="path"/> with the protected DACL from the moment it exists, and
	/// returns false, touching nothing, when something is already there. Unlike
	/// <see cref="CreateProtectedDirectory"/>, the result is proof that THIS process created the
	/// folder, so no other principal can own it or hold a handle opened before it was protected.
	/// The parent must exist.
	/// </summary>
	public static bool TryCreateNewProtectedDirectory(string path, bool allowUsersReadAndExecute)
	{
		var descriptor = BuildProtectedSecurity(allowUsersReadAndExecute).GetSecurityDescriptorBinaryForm();
		var buffer = Marshal.AllocHGlobal(descriptor.Length);
		try
		{
			Marshal.Copy(descriptor, 0, buffer, descriptor.Length);
			var attributes = new SecurityAttributes
			{
				Length = Marshal.SizeOf<SecurityAttributes>(),
				SecurityDescriptor = buffer,
				InheritHandle = false,
			};
			if (CreateDirectoryW(path, ref attributes))
			{
				return true;
			}

			var error = Marshal.GetLastWin32Error();
			return error == ErrorAlreadyExists ? false : throw new Win32Exception(error);
		}
		finally
		{
			Marshal.FreeHGlobal(buffer);
		}
	}

	/// <summary>
	/// Protects an existing install folder AND everything already inside it, then refuses it if it
	/// contains a link. Protecting only the root is not enough: an entry a standard user created
	/// earlier stays owned by them, and an owner can always rewrite its DACL, even after the root
	/// is locked. So every entry is made Administrators-owned and reset to inherit the root's rules
	/// only. The walk never follows a reparse point; meeting one throws
	/// <see cref="UnsafeInstallFolderException"/>, because the payload would otherwise be written
	/// wherever the link points. Requires an elevated caller.
	/// </summary>
	public static void ProtectExistingTree(string path, bool allowUsersReadAndExecute)
	{
		var root = new DirectoryInfo(path);
		// Refused before anything changes, so a rejected folder is left exactly as it was. The reset
		// below checks again: the tree is only closed to other principals once the root is locked.
		ThrowIfContainsLink(root);

		var security = BuildProtectedSecurity(allowUsersReadAndExecute);
		security.SetOwner(Administrators);
		root.SetAccessControl(security);

		ResetChildren(root);
	}

	private static void ThrowIfContainsLink(DirectoryInfo directory)
	{
		if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
		{
			throw new UnsafeInstallFolderException(directory.FullName);
		}

		foreach (var entry in directory.EnumerateFileSystemInfos())
		{
			if (entry is DirectoryInfo child)
			{
				ThrowIfContainsLink(child);
			}
			else if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
			{
				throw new UnsafeInstallFolderException(entry.FullName);
			}
		}
	}

	private static void ResetChildren(DirectoryInfo directory)
	{
		foreach (var entry in directory.EnumerateFileSystemInfos())
		{
			if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
			{
				throw new UnsafeInstallFolderException(entry.FullName);
			}

			if (entry is DirectoryInfo child)
			{
				child.SetAccessControl(InheritedOnlyDirectorySecurity());
				ResetChildren(child);
			}
			else
			{
				((FileInfo)entry).SetAccessControl(InheritedOnlyFileSecurity());
			}
		}
	}

	// No explicit rules and an unprotected DACL: Windows then fills the DACL from the parent's
	// inheritable rules alone, which drops anything a previous owner granted themselves.
	private static DirectorySecurity InheritedOnlyDirectorySecurity()
	{
		var security = new DirectorySecurity();
		security.SetOwner(Administrators);
		security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
		return security;
	}

	private static FileSecurity InheritedOnlyFileSecurity()
	{
		var security = new FileSecurity();
		security.SetOwner(Administrators);
		security.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
		return security;
	}

	private const int ErrorAlreadyExists = 183;

	[StructLayout(LayoutKind.Sequential)]
	private struct SecurityAttributes
	{
		public int Length;
		public IntPtr SecurityDescriptor;
		[MarshalAs(UnmanagedType.Bool)]
		public bool InheritHandle;
	}

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CreateDirectoryW(string path, ref SecurityAttributes securityAttributes);
}

/// <summary>
/// The install folder cannot be trusted: it contains a link (junction or symbolic link) or is one,
/// so the payload could land outside the folder the service will trust, or something appeared at
/// the path after the installer removed it. The install stops; nothing is copied.
/// </summary>
public sealed class UnsafeInstallFolderException(string path)
	: IOException($"The install folder was not used, because it contains a link or changed during the installation: {path}")
{
	public string Path { get; } = path;
}
