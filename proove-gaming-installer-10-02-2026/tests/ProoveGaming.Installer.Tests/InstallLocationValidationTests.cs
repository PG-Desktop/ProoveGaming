using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ProoveGaming.Common.Classes;
using ProoveGaming.Installer.Views;
using Xunit;

namespace ProoveGaming.Installer.Tests;

// Pins which folders the installer accepts (docs/plans/fix-repository-audit-findings, S3 and review
// round 1 B1). The chosen folder gets an administrators-only ACL and hosts a LocalSystem service, so
// it must be new, empty, or one of our installs — including a DAMAGED one: reinstalling over it is
// how a broken install is repaired, so refusing it would strand the user.
//
// "One of our installs" is InstallSignature's call, pinned in InstallSignatureTests; here it is
// injected so the folder rules are tested on their own.
[SupportedOSPlatform("windows")]
public class InstallLocationValidationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PgLocationTests", Guid.NewGuid().ToString("N"));

    public InstallLocationValidationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        TestFolders.DeleteTree(_root);
    }

    [Fact]
    public void NewFolder_IsAccepted()
    {
        Assert.Null(MainWindow.GetInstallLocationErrorKey(Path.Combine(_root, "ProoveGaming"), _ => false));
    }

    [Fact]
    public void EmptyExistingFolder_IsAccepted()
    {
        var empty = Path.Combine(_root, "Empty");
        Directory.CreateDirectory(empty);

        Assert.Null(MainWindow.GetInstallLocationErrorKey(empty, _ => false));
    }

    [Fact]
    public void ExistingInstall_IsAccepted()
    {
        // Reinstalling over a previous install is the normal upgrade path.
        var install = TestFolders.CreateInstallLayout(Path.Combine(_root, "PG"));

        Assert.Null(MainWindow.GetInstallLocationErrorKey(install, _ => true));
    }

    [Fact]
    public void DamagedInstall_IsAccepted_SoAReinstallCanRepairIt()
    {
        // Review round 1 B1: AV quarantined the App EXE. The folder is not empty and no longer
        // complete, but it is still ours, and a reinstall over it is the repair.
        var install = TestFolders.CreateInstallLayout(Path.Combine(_root, "PG"));
        File.Delete(Path.Combine(install, InstallLayout.AppExeName));

        Assert.Null(MainWindow.GetInstallLocationErrorKey(install, path => path == install));
    }

    [Fact]
    public void FolderWithOtherFiles_IsRejected()
    {
        var foreign = Path.Combine(_root, "Photos");
        Directory.CreateDirectory(foreign);
        File.WriteAllText(Path.Combine(foreign, "holiday.jpg"), "x");

        Assert.Equal("Message_InstallFolderNotEmpty", MainWindow.GetInstallLocationErrorKey(foreign, _ => false));
    }

    [Fact]
    public void UnreadableFolder_ReportsAnInvalidFolder_NotANonEmptyOne()
    {
        var locked = Path.Combine(_root, "Locked");
        Directory.CreateDirectory(locked);
        using var identity = WindowsIdentity.GetCurrent();
        var directory = new DirectoryInfo(locked);
        var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
        directory.SetAccessControl(security);

        Assert.Equal("Message_InvalidInstallPath", MainWindow.GetInstallLocationErrorKey(locked, _ => false));
    }

    [Theory]
    [InlineData(Environment.SpecialFolder.DesktopDirectory)]
    [InlineData(Environment.SpecialFolder.ProgramFiles)]
    [InlineData(Environment.SpecialFolder.Windows)]
    public void SystemOrProfileFolder_IsRejected(Environment.SpecialFolder folder)
    {
        Assert.Equal("Message_InstallPathNotAllowed", MainWindow.GetInstallLocationErrorKey(Environment.GetFolderPath(folder), _ => true));
    }

    [Fact]
    public void DriveRoot_IsRejected()
    {
        Assert.Equal("Message_InstallPathNotAllowed", MainWindow.GetInstallLocationErrorKey(Path.GetPathRoot(_root)!, _ => true));
    }
}
