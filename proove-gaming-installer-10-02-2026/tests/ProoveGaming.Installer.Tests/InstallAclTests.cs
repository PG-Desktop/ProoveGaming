using System;
using System.IO;
using System.Runtime.Versioning;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using ProoveGaming.Common.Classes;
using Xunit;

namespace ProoveGaming.Installer.Tests;

// Pins the locked-down permissions on the install folder and the payload staging folder
// (docs/plans/fix-repository-audit-findings, S3 and S4). A custom install folder used to inherit
// its parent's ACL, which on most data drives lets any user modify the LocalSystem service's DLLs.
[SupportedOSPlatform("windows")]
public class InstallAclTests
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);
    private static readonly SecurityIdentifier AuthenticatedUsers = new(WellKnownSidType.AuthenticatedUserSid, null);

    [Fact]
    public void ProtectedSecurity_InheritsNothingFromTheParent()
    {
        var security = InstallAcl.BuildProtectedSecurity(allowUsersReadAndExecute: true);

        Assert.True(security.AreAccessRulesProtected);
    }

    [Fact]
    public void ProtectedSecurity_GivesFullControlOnlyToAdministratorsAndSystem()
    {
        var rules = Rules(InstallAcl.BuildProtectedSecurity(allowUsersReadAndExecute: true));

        Assert.Contains(rules, r => r.IdentityReference.Equals(Administrators) && r.FileSystemRights.HasFlag(FileSystemRights.FullControl));
        Assert.Contains(rules, r => r.IdentityReference.Equals(LocalSystem) && r.FileSystemRights.HasFlag(FileSystemRights.FullControl));
        Assert.All(rules, r => Assert.Equal(AccessControlType.Allow, r.AccessControlType));
        Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(AuthenticatedUsers));
    }

    [Fact]
    public void ProtectedSecurity_ForTheInstallFolder_LetsUsersReadAndRunButNotWrite()
    {
        var usersRule = Assert.Single(Rules(InstallAcl.BuildProtectedSecurity(allowUsersReadAndExecute: true)), r => r.IdentityReference.Equals(Users));

        // Any single bit of these is enough to plant or replace a file the LocalSystem service loads.
        // (Not HasFlag: Write and Modify are multi-bit masks, so HasFlag would miss e.g. WriteData
        // alone or Delete alone.)
        const FileSystemRights anyChange = FileSystemRights.Write | FileSystemRights.Delete |
            FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        Assert.Equal(FileSystemRights.ReadAndExecute, usersRule.FileSystemRights & ~FileSystemRights.Synchronize);
        Assert.Equal((FileSystemRights)0, usersRule.FileSystemRights & anyChange);
    }

    [Fact]
    public void ProtectedSecurity_ForStaging_GivesUsersNothing()
    {
        Assert.DoesNotContain(Rules(InstallAcl.BuildProtectedSecurity(allowUsersReadAndExecute: false)), r => r.IdentityReference.Equals(Users));
    }

    [Fact]
    public void ProtectedSecurity_IsInheritedByEverythingWrittenLater()
    {
        // The payload is copied in AFTER the folder is protected, so the rules have to flow down.
        Assert.All(Rules(InstallAcl.BuildProtectedSecurity(allowUsersReadAndExecute: true)), r =>
            Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, r.InheritanceFlags));
    }

    [Fact]
    public void SetOwnerOnly_ChangesTheOwner_AndLeavesTheDaclExactlyAsItWas()
    {
        // The property that keeps installed EXEs launchable: MakeAdministratorsOwner must write the
        // owner and nothing else. Runs on every machine: any process may make itself the owner of
        // its own file, so the real code path is exercised without elevation (review round 2).
        var folder = Path.Combine(Path.GetTempPath(), "PgAclTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, InstallLayout.AppExeName);
            File.WriteAllText(file, "x");
            var daclBefore = new FileInfo(file).GetAccessControl(AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            using var identity = WindowsIdentity.GetCurrent();

            InstallAcl.SetOwnerOnly(file, identity.User!);

            var after = new FileInfo(file).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            Assert.Equal(daclBefore, after.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            Assert.Equal(identity.User, after.GetOwner(typeof(SecurityIdentifier)));
            Assert.Equal("x", File.ReadAllText(file));
        }
        finally
        {
            TestFolders.DeleteTree(folder);
        }
    }

    [Fact]
    public void MarkerFile_MadeAdministratorsOwned_PassesTheUninstallersOwnerCheck()
    {
        // Assigning Administrators as owner needs an elevated token, which CI has (windows-latest
        // runs as an administrator with UAC off, the exact configuration this guards against: there
        // an elevated process's new files would otherwise belong to the installing account). On a
        // normal developer run the assertion cannot be made, so the test only runs elevated.
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            return;
        }

        var folder = Path.Combine(Path.GetTempPath(), "PgAclTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var marker = Path.Combine(folder, InstallLayout.UninstallerExeName);
            File.WriteAllText(marker, string.Empty);

            InstallAcl.MakeAdministratorsOwner(marker);

            Assert.True(InstallSignature.IsOwnedByTrustedPrincipal(marker));
        }
        finally
        {
            TestFolders.DeleteTree(folder);
        }
    }

    [Fact]
    public void CreatedDirectory_CarriesTheProtectedDaclFromBirth()
    {
        var path = Path.Combine(Path.GetTempPath(), "PgAclTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var directory = InstallAcl.CreateProtectedDirectory(path, allowUsersReadAndExecute: true);

            var security = directory.GetAccessControl();
            var rules = Rules(security);
            Assert.True(security.AreAccessRulesProtected);
            Assert.DoesNotContain(rules, r => r.IsInherited);
            Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(AuthenticatedUsers));
        }
        finally
        {
            TestFolders.DeleteTree(path);
        }
    }

    // ── The install folder itself (security review 2026-09-24) ─────────────────────────────

    [Fact]
    public void TryCreateNew_OnAFreshPath_CreatesItWithTheProtectedDacl()
    {
        var path = FreshPath();
        try
        {
            Assert.True(InstallAcl.TryCreateNewProtectedDirectory(path, allowUsersReadAndExecute: true));

            var security = new DirectoryInfo(path).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            Assert.DoesNotContain(Rules(security), r => r.IsInherited);
            Assert.DoesNotContain(Rules(security), r => r.IdentityReference.Equals(AuthenticatedUsers));
        }
        finally
        {
            TestFolders.DeleteTree(path);
        }
    }

    [Fact]
    public void TryCreateNew_WhenSomethingIsAlreadyThere_ReturnsFalse_AndLeavesItUntouched()
    {
        // "Already exists" is the case the installer must refuse: a folder someone else created may
        // be theirs, or held open with rights granted before it was locked.
        var path = FreshPath();
        Directory.CreateDirectory(path);
        try
        {
            var before = DaclOf(path);

            Assert.False(InstallAcl.TryCreateNewProtectedDirectory(path, allowUsersReadAndExecute: true));

            Assert.Equal(before, DaclOf(path));
        }
        finally
        {
            TestFolders.DeleteTree(path);
        }
    }

    [Fact]
    public void ProtectExistingTree_RefusesAJunctionInside_BeforeChangingAnything()
    {
        var root = FreshPath();
        var elsewhere = FreshPath();
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(elsewhere);
        try
        {
            TestFolders.CreateJunction(Path.Combine(root, InstallLayout.DeviceHostFolderName), elsewhere);
            var before = DaclOf(root);

            Assert.Throws<UnsafeInstallFolderException>(() => InstallAcl.ProtectExistingTree(root, allowUsersReadAndExecute: true));

            Assert.Equal(before, DaclOf(root));
        }
        finally
        {
            TestFolders.DeleteTree(root);
            TestFolders.DeleteTree(elsewhere);
        }
    }

    [Fact]
    public void ProtectExistingTree_RefusesARootThatIsItselfAJunction()
    {
        var link = FreshPath();
        var elsewhere = FreshPath();
        Directory.CreateDirectory(elsewhere);
        try
        {
            TestFolders.CreateJunction(link, elsewhere);

            Assert.Throws<UnsafeInstallFolderException>(() => InstallAcl.ProtectExistingTree(link, allowUsersReadAndExecute: true));
        }
        finally
        {
            TestFolders.DeleteTree(link);
            TestFolders.DeleteTree(elsewhere);
        }
    }

    [Fact]
    public void ProtectExistingTree_TakesBackOwnershipOfEverythingAlreadyInside()
    {
        // The finding: protecting only the root left an entry a user had created theirs, and an
        // owner can always rewrite its DACL. Making Administrators the owner needs an elevated
        // token, which CI has (see MarkerFile_MadeAdministratorsOwned_…); a normal developer run
        // skips the assertion.
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            return;
        }

        var root = FreshPath();
        var host = Path.Combine(root, InstallLayout.DeviceHostFolderName);
        var dll = Path.Combine(host, "Planted.dll");
        Directory.CreateDirectory(host);
        File.WriteAllText(dll, "x");
        var hostSecurity = new DirectoryInfo(host).GetAccessControl();
        hostSecurity.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(host).SetAccessControl(hostSecurity);
        try
        {
            InstallAcl.ProtectExistingTree(root, allowUsersReadAndExecute: true);

            AssertAdministratorsOwnedWithInheritedRulesOnly(new DirectoryInfo(host).GetAccessControl(), host);
            AssertAdministratorsOwnedWithInheritedRulesOnly(new FileInfo(dll).GetAccessControl(), dll);
        }
        finally
        {
            TestFolders.DeleteTree(root);
        }
    }

    private static void AssertAdministratorsOwnedWithInheritedRulesOnly(FileSystemSecurity security, string path)
    {
        Assert.Equal(Administrators, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.False(security.AreAccessRulesProtected);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            Assert.True(rule.IsInherited, $"explicit rule for {rule.IdentityReference} survived on {path}");
        }
    }

    private static string FreshPath()
    {
        var parent = Path.Combine(Path.GetTempPath(), "PgAclTests");
        Directory.CreateDirectory(parent);
        return Path.Combine(parent, Guid.NewGuid().ToString("N"));
    }

    private static string DaclOf(string path) =>
        new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    private static FileSystemAccessRule[] Rules(DirectorySecurity security) =>
        security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .ToArray();
}
