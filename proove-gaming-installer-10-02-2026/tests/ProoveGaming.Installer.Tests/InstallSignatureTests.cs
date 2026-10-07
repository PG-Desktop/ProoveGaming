using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ProoveGaming.Common.Classes;
using Xunit;

namespace ProoveGaming.Installer.Tests;

// Pins the check the ELEVATED uninstaller (and the installer's folder step) must pass before acting
// on a folder (docs/plans/fix-repository-audit-findings, S1 and review round 1 B1).
//
// "Ours" is decided by PROVENANCE: the folder and at least one of our files in it are owned by
// Administrators / SYSTEM / TrustedInstaller, which a standard user cannot fake, plus the folder is
// an acceptable location and not a link. It is deliberately NOT "every file is still there":
// antivirus quarantines single unsigned files and an unfinished self-delete leaves a few behind,
// and both of those installs must stay repairable (reinstall) and removable (uninstall).
//
// Most cases inject the owner check so they do not depend on whether the test process is elevated
// (on CI it is, locally usually not). The real owner check is pinned separately.
[SupportedOSPlatform("windows")]
public class InstallSignatureTests : IDisposable
{
    private static readonly Func<string, bool> EverythingTrusted = _ => true;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "PgSignatureTests", Guid.NewGuid().ToString("N"));

    public InstallSignatureTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        TestFolders.DeleteTree(_root);
    }

    [Fact]
    public void CompleteInstall_IsAccepted()
    {
        var install = CreateInstall("PG");

        Assert.True(InstallSignature.IsProoveGamingInstall(install, EverythingTrusted));
    }

    [Fact]
    public void CompleteInstall_WithTrailingSeparator_IsAccepted()
    {
        var install = CreateInstall("PG");

        Assert.True(InstallSignature.IsProoveGamingInstall(install + Path.DirectorySeparatorChar, EverythingTrusted));
    }

    // ---- partial installs (review round 1, B1) --------------------------------------------------

    [Fact]
    public void AppExeQuarantined_IsStillAccepted()
    {
        // AV removed the App EXE and left everything else (docs/support/missing-files-crash-analysis.md §1).
        var install = CreateInstall("PG");
        File.Delete(Path.Combine(install, InstallLayout.AppExeName));

        Assert.True(InstallSignature.IsProoveGamingInstall(install, EverythingTrusted));
    }

    [Fact]
    public void DeviceHostExeQuarantined_IsStillAccepted()
    {
        var install = CreateInstall("PG");
        File.Delete(Path.Combine(install, InstallLayout.DeviceHostFolderName, InstallLayout.DeviceHostExeName));

        Assert.True(InstallSignature.IsProoveGamingInstall(install, EverythingTrusted));
    }

    [Fact]
    public void UninstallInterruptedAfterHostRemoval_IsStillAccepted()
    {
        // The uninstaller deletes host\ right after removing the service; a run stopped there must
        // be retryable.
        var install = CreateInstall("PG");
        Directory.Delete(Path.Combine(install, InstallLayout.DeviceHostFolderName), recursive: true);

        Assert.True(InstallSignature.IsProoveGamingInstall(install, EverythingTrusted));
    }

    [Fact]
    public void OnlyTheUninstallerLeft_IsStillAccepted()
    {
        // What an unfinished self-delete loop leaves: the uninstaller EXE (plus native DLLs).
        var install = Path.Combine(_root, "Leftovers");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, InstallLayout.UninstallerExeName), string.Empty);
        File.WriteAllText(Path.Combine(install, "libSkiaSharp.dll"), string.Empty);

        Assert.True(InstallSignature.IsProoveGamingInstall(install, EverythingTrusted));
    }

    // ---- provenance ---------------------------------------------------------------------------

    [Fact]
    public void FolderNotOwnedByAdministrators_IsRejected_EvenWithEveryFile()
    {
        // A folder a standard user created and filled with copies of our EXEs (they are readable).
        var install = CreateInstall("PG");

        Assert.False(InstallSignature.IsProoveGamingInstall(install, path => !SamePath(path, install)));
    }

    [Fact]
    public void MarkerCopiedInByAUser_DoesNotCount()
    {
        // Admin-owned folder, but the only "marker" is a file the user dropped there, so it is owned
        // by that user. Copying our uninstaller into some admin-created folder a user can write to
        // must not make that folder deletable with administrator rights.
        var folder = Path.Combine(_root, "VendorData");
        Directory.CreateDirectory(folder);
        var copiedMarker = Path.Combine(folder, InstallLayout.UninstallerExeName);
        File.WriteAllText(copiedMarker, string.Empty);

        Assert.False(InstallSignature.IsProoveGamingInstall(folder, path => !SamePath(path, copiedMarker)));
    }

    [Fact]
    public void AdministratorOwnedFolderWithoutAnyMarker_IsRejected()
    {
        // Another application's folder under Program Files is admin-owned too.
        var foreign = Path.Combine(_root, "SomeOtherApp");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "SomeOtherApp.exe"), string.Empty);

        Assert.False(InstallSignature.IsProoveGamingInstall(foreign, EverythingTrusted));
    }

    [Fact]
    public void RealOwnerCheck_UserOwnedFolder_IsNotTrusted()
    {
        // Sets the owner explicitly, so the result does not depend on whether the test runs elevated
        // (an elevated process's new folders are owned by Administrators).
        var folder = Path.Combine(_root, "UserOwned");
        Directory.CreateDirectory(folder);
        using var identity = WindowsIdentity.GetCurrent();
        var directory = new DirectoryInfo(folder);
        var security = directory.GetAccessControl(AccessControlSections.Owner);
        security.SetOwner(identity.User!);
        directory.SetAccessControl(security);

        Assert.False(InstallSignature.IsOwnedByTrustedPrincipal(folder));
    }

    [Fact]
    public void RealOwnerCheck_WindowsSystemFile_IsTrusted()
    {
        // Owned by TrustedInstaller on every supported Windows.
        Assert.True(InstallSignature.IsOwnedByTrustedPrincipal(Path.Combine(Environment.SystemDirectory, "kernel32.dll")));
    }

    [Theory]
    [InlineData(Environment.SpecialFolder.ProgramFiles)]
    [InlineData(Environment.SpecialFolder.ProgramFilesX86)]
    public void PathInsideProgramFiles_IsTrustedByLocation(Environment.SpecialFolder root)
    {
        // An install made with UAC disabled by a build before 2026-09-24 is owned by the installing
        // account, so ownership alone would refuse to upgrade or remove it. Only administrators can
        // write inside Program Files, which is proof enough.
        var programFiles = Environment.GetFolderPath(root);

        Assert.True(InstallSignature.IsInsideProgramFiles(Path.Combine(programFiles, InstallLayout.AppDataFolderName)));
        Assert.True(InstallSignature.IsInsideProgramFiles(Path.Combine(programFiles, InstallLayout.AppDataFolderName, InstallLayout.AppExeName)));
    }

    [Fact]
    public void ProgramFilesItself_AndLookAlikeSiblings_AreNotInsideProgramFiles()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        Assert.False(InstallSignature.IsInsideProgramFiles(programFiles));
        // "C:\Program Files Evil\..." shares the prefix but is a sibling a user could create.
        Assert.False(InstallSignature.IsInsideProgramFiles(programFiles + " Evil" + Path.DirectorySeparatorChar + "PG"));
        Assert.False(InstallSignature.IsInsideProgramFiles(Path.Combine(_root, "PG")));
    }

    [Fact]
    public void RealOwnerCheck_MissingPath_IsNotTrusted()
    {
        Assert.False(InstallSignature.IsOwnedByTrustedPrincipal(Path.Combine(_root, "DoesNotExist")));
    }

    // ---- location and links -------------------------------------------------------------------

    [Fact]
    public void MissingFolder_IsRejected()
    {
        Assert.False(InstallSignature.IsProoveGamingInstall(Path.Combine(_root, "DoesNotExist"), EverythingTrusted));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"relative\PG")]
    public void EmptyOrRelativePath_IsRejected(string? path)
    {
        Assert.False(InstallSignature.IsProoveGamingInstall(path, EverythingTrusted));
        Assert.False(InstallSignature.IsAcceptableInstallLocation(path));
    }

    [Fact]
    public void DriveRoot_IsRejected()
    {
        Assert.False(InstallSignature.IsAcceptableInstallLocation(Path.GetPathRoot(_root)!));
    }

    [Theory]
    [InlineData(Environment.SpecialFolder.Windows)]
    [InlineData(Environment.SpecialFolder.System)]
    [InlineData(Environment.SpecialFolder.ProgramFiles)]
    [InlineData(Environment.SpecialFolder.CommonApplicationData)]
    [InlineData(Environment.SpecialFolder.UserProfile)]
    [InlineData(Environment.SpecialFolder.DesktopDirectory)]
    [InlineData(Environment.SpecialFolder.LocalApplicationData)]
    public void WellKnownSystemAndProfileFolders_AreRejected(Environment.SpecialFolder folder)
    {
        Assert.False(InstallSignature.IsAcceptableInstallLocation(Environment.GetFolderPath(folder)));
    }

    [Fact]
    public void ProfilesRoot_IsRejected()
    {
        var profilesRoot = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))!;

        Assert.False(InstallSignature.IsAcceptableInstallLocation(profilesRoot));
    }

    [Fact]
    public void FolderUnderProgramFiles_IsAcceptable()
    {
        // The default install location. Only Program Files ITSELF is off limits.
        var defaultInstall = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), InstallLayout.AppDataFolderName);

        Assert.True(InstallSignature.IsAcceptableInstallLocation(defaultInstall));
    }

    [Fact]
    public void JunctionToARealInstall_IsRejected()
    {
        // Even when the expected files are visible THROUGH the link: a recursive delete of a junction
        // acts on its target, which can be anywhere on the disk.
        var target = CreateInstall("Target");
        var junction = Path.Combine(_root, "Junction");
        TestFolders.CreateJunction(junction, target);

        Assert.True(File.Exists(Path.Combine(junction, InstallLayout.AppExeName)));
        Assert.False(InstallSignature.IsProoveGamingInstall(junction, EverythingTrusted));
    }

    [Fact]
    public void HostJunction_IsNotAMarker()
    {
        // The only marker sits behind a junction called host\, which could point anywhere.
        var install = Path.Combine(_root, "PG");
        Directory.CreateDirectory(install);
        var elsewhere = Path.Combine(_root, "Elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, InstallLayout.DeviceHostExeName), string.Empty);
        TestFolders.CreateJunction(Path.Combine(install, InstallLayout.DeviceHostFolderName), elsewhere);

        Assert.False(InstallSignature.IsProoveGamingInstall(install, EverythingTrusted));
    }

    private string CreateInstall(string name)
    {
        var install = TestFolders.CreateInstallLayout(Path.Combine(_root, name));
        File.WriteAllText(Path.Combine(install, InstallLayout.UninstallerExeName), string.Empty);
        return install;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
}
