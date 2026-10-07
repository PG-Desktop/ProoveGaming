using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using ProoveGaming.Common.Classes;
using ProoveGaming.Uninstaller;
using Xunit;

namespace ProoveGaming.Installer.Tests;

// Pins that the uninstaller's delete walk never follows a junction
// (docs/plans/fix-repository-audit-findings S11, review round 1 Tests item 2). The walk runs with
// administrator rights over folders a standard user can write to — %LocalAppData%\ProoveGaming for
// "delete my data" above all — and SearchOption.AllDirectories, which it used before, descends into
// junctions: one planted link would have aimed the delete at any folder on the disk.
[SupportedOSPlatform("windows")]
public class UninstallDeleteWalkTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PgDeleteWalkTests", Guid.NewGuid().ToString("N"));

    public UninstallDeleteWalkTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        TestFolders.DeleteTree(_root);
    }

    [Fact]
    public void JunctionInsideTheTree_IsRemovedAsALink_AndItsTargetSurvives()
    {
        var victim = Path.Combine(_root, "Victim");
        Directory.CreateDirectory(victim);
        var victimFile = Path.Combine(victim, "precious.txt");
        File.WriteAllText(victimFile, "keep me");

        var data = Path.Combine(_root, "ProoveGaming");
        Directory.CreateDirectory(Path.Combine(data, "nested"));
        File.WriteAllText(Path.Combine(data, "settings.db"), "x");
        TestFolders.CreateJunction(Path.Combine(data, "nested", "link"), victim);

        UninstallService.TryDeleteDirectoryContents(data);

        Assert.True(File.Exists(victimFile), "the junction's target must not be touched");
        Assert.Empty(Directory.GetFileSystemEntries(data));
    }

    [Fact]
    public void OrdinaryTree_IsEmptied()
    {
        var data = Path.Combine(_root, "ProoveGaming");
        Directory.CreateDirectory(Path.Combine(data, "a", "b"));
        File.WriteAllText(Path.Combine(data, "a", "b", "file.txt"), "x");
        var readOnly = Path.Combine(data, "readonly.txt");
        File.WriteAllText(readOnly, "x");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        UninstallService.TryDeleteDirectoryContents(data);

        Assert.Empty(Directory.GetFileSystemEntries(data));
    }

    [Fact]
    public void MissingFolder_DoesNotThrow()
    {
        UninstallService.TryDeleteDirectoryContents(Path.Combine(_root, "DoesNotExist"));
    }

    // ── The folder the walk is GIVEN (review round 3, B1) ────────────────────────────────────
    //
    // A standard user can replace %LocalAppData%\ProoveGaming with a junction, and anyone can
    // create %ProgramData%\ProoveGaming as one. Listing a junction lists its target, so the walk
    // used to empty the target with administrator rights.

    [Fact]
    public void JunctionAsTheStartingFolder_ItsTargetIsNotTouched()
    {
        var victim = Path.Combine(_root, "Victim");
        Directory.CreateDirectory(victim);
        var victimFile = Path.Combine(victim, "precious.txt");
        File.WriteAllText(victimFile, "keep me");
        var junction = Path.Combine(_root, "ProoveGaming");
        TestFolders.CreateJunction(junction, victim);

        UninstallService.TryDeleteDirectoryContents(junction);

        Assert.True(File.Exists(victimFile), "the junction's target must not be emptied");
    }

    [Fact]
    public void DeleteDataFolder_WhenTheDataFolderIsAJunction_RemovesOnlyTheLink()
    {
        var victim = Path.Combine(_root, "Victim");
        Directory.CreateDirectory(victim);
        var victimFile = Path.Combine(victim, "precious.txt");
        File.WriteAllText(victimFile, "keep me");
        var baseFolder = Path.Combine(_root, "LocalAppData");
        Directory.CreateDirectory(baseFolder);
        var dataFolder = Path.Combine(baseFolder, InstallLayout.AppDataFolderName);
        TestFolders.CreateJunction(dataFolder, victim);

        UninstallService.DeleteDataFolder(baseFolder, requireTrustedOwner: false);

        Assert.True(File.Exists(victimFile), "the junction's target must not be emptied");
        Assert.False(Directory.Exists(dataFolder), "the link itself should be gone");
    }

    [Fact]
    public void DeleteDataFolder_RequiringATrustedOwner_LeavesAUserCreatedFolderAlone()
    {
        // %ProgramData%\ProoveGaming: the old DeviceHost created it as SYSTEM, so a folder of that
        // name owned by anyone else was never ours. The owner is set explicitly so the result does
        // not depend on whether the test runs elevated.
        var baseFolder = Path.Combine(_root, "ProgramData");
        var dataFolder = Path.Combine(baseFolder, InstallLayout.AppDataFolderName);
        Directory.CreateDirectory(dataFolder);
        var someonesFile = Path.Combine(dataFolder, "notes.txt");
        File.WriteAllText(someonesFile, "x");
        using var identity = WindowsIdentity.GetCurrent();
        var directory = new DirectoryInfo(dataFolder);
        var security = directory.GetAccessControl(AccessControlSections.Owner);
        security.SetOwner(identity.User!);
        directory.SetAccessControl(security);

        UninstallService.DeleteDataFolder(baseFolder, requireTrustedOwner: true);

        Assert.True(File.Exists(someonesFile));
    }

    [Fact]
    public void DeleteDataFolder_OrdinaryUserDataFolder_IsRemoved()
    {
        var baseFolder = Path.Combine(_root, "LocalAppData");
        var dataFolder = Path.Combine(baseFolder, InstallLayout.AppDataFolderName);
        Directory.CreateDirectory(Path.Combine(dataFolder, "avatars"));
        File.WriteAllText(Path.Combine(dataFolder, "proove.db"), "x");
        File.WriteAllText(Path.Combine(dataFolder, "avatars", "me.png"), "x");

        UninstallService.DeleteDataFolder(baseFolder, requireTrustedOwner: false);

        Assert.False(Directory.Exists(dataFolder));
    }
}
