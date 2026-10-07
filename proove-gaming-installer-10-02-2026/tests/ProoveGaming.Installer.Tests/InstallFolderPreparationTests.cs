using System;
using System.IO;
using System.Runtime.Versioning;
using ProoveGaming.Common.Classes;
using ProoveGaming.Installer.Views;
using Xunit;

namespace ProoveGaming.Installer.Tests;

// Pins how the installer takes hold of the install folder before copying (security review
// 2026-09-24, docs/plans/fix-repository-audit-findings S3 · note). A folder someone else created
// could still be theirs, or held open with rights granted before it was locked, so only an existing
// Proove Gaming install is reused; any other folder is created fresh by the installer. Runs
// unelevated: creating a protected folder needs no administrator rights.
[SupportedOSPlatform("windows")]
public class InstallFolderPreparationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "PgPrepareTests", Guid.NewGuid().ToString("N"));

    public InstallFolderPreparationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        TestFolders.DeleteTree(_root);
    }

    [Fact]
    public void ExistingInstall_IsReusedAsItIs()
    {
        var install = Path.Combine(_root, "ProoveGaming");
        Directory.CreateDirectory(install);
        var existing = Path.Combine(install, InstallLayout.AppExeName);
        File.WriteAllText(existing, "x");

        var stillPreexisting = MainWindow.PrepareInstallFolder(install, preexisted: true, _ => true);

        Assert.True(stillPreexisting);
        Assert.True(File.Exists(existing));
    }

    [Fact]
    public void PreexistingEmptyFolder_IsReplacedByOneTheInstallerCreated()
    {
        var folder = Path.Combine(_root, "ProoveGaming");
        Directory.CreateDirectory(folder);
        Assert.False(new DirectoryInfo(folder).GetAccessControl().AreAccessRulesProtected);

        var stillPreexisting = MainWindow.PrepareInstallFolder(folder, preexisted: true, _ => false);

        // Recreated with the protected DACL from birth, and now counted as ours for rollback.
        Assert.False(stillPreexisting);
        Assert.True(new DirectoryInfo(folder).GetAccessControl().AreAccessRulesProtected);
    }

    [Fact]
    public void PreexistingFolderThatIsNoLongerEmpty_StopsTheInstall_AndIsLeftAlone()
    {
        // Validation admitted it empty; something was added before the install started.
        var folder = Path.Combine(_root, "ProoveGaming");
        Directory.CreateDirectory(folder);
        var added = Path.Combine(folder, "added.txt");
        File.WriteAllText(added, "x");

        Assert.ThrowsAny<IOException>(() => MainWindow.PrepareInstallFolder(folder, preexisted: true, _ => false));

        Assert.True(File.Exists(added));
    }

    [Fact]
    public void NewPath_IsCreatedProtected_IncludingAMissingParent()
    {
        var folder = Path.Combine(_root, "Games", "ProoveGaming");

        var stillPreexisting = MainWindow.PrepareInstallFolder(folder, preexisted: false, _ => false);

        Assert.False(stillPreexisting);
        Assert.True(new DirectoryInfo(folder).GetAccessControl().AreAccessRulesProtected);
    }

    [Fact]
    public void SomethingAlreadyAtThePath_WhenCreating_StopsTheInstall()
    {
        // The folder appeared after the existence check (or after the delete above): it is not
        // provably ours, so it is refused rather than used.
        var folder = Path.Combine(_root, "ProoveGaming");
        Directory.CreateDirectory(folder);

        Assert.Throws<UnsafeInstallFolderException>(() => MainWindow.PrepareInstallFolder(folder, preexisted: false, _ => false));
        Assert.False(new DirectoryInfo(folder).GetAccessControl().AreAccessRulesProtected);
    }
}
