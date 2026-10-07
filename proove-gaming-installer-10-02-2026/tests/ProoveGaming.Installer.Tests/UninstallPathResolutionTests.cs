using ProoveGaming.Uninstaller;
using Xunit;

namespace ProoveGaming.Installer.Tests;

// Pins where the elevated uninstaller takes its install folder from
// (docs/plans/fix-repository-audit-findings, S1/S2). Only the HKLM entry and the folder the
// uninstaller EXE runs from are trusted; the command line is written by whoever launched the
// process and may only CHOOSE between those two. Previously a "--uninstall <path>" argument or the
// user-writable HKCU entry named the folder outright, and it was deleted with administrator rights.
public class UninstallPathResolutionTests
{
    private const string Registered = @"C:\Program Files\ProoveGaming";
    private const string ExecutableFolder = @"D:\Games\ProoveGaming";

    [Fact]
    public void RequestedPathNamingAnUntrustedFolder_IsIgnored()
    {
        var resolved = UninstallService.ResolveInstallPath(@"C:\Users\victim\Documents", Registered, ExecutableFolder);

        Assert.Equal(Registered, resolved);
    }

    [Fact]
    public void RequestedPathNamingAnUntrustedFolder_WithNoRegistration_FallsBackToTheExecutableFolder()
    {
        var resolved = UninstallService.ResolveInstallPath(@"C:\Windows", registeredPath: null, ExecutableFolder);

        Assert.Equal(ExecutableFolder, resolved);
    }

    [Fact]
    public void RequestedPathNamingAnUntrustedFolder_WithNoTrustedSource_ResolvesToNothing()
    {
        Assert.Null(UninstallService.ResolveInstallPath(@"C:\Windows", registeredPath: null, executableFolder: null));
    }

    [Fact]
    public void NoRequestedPath_PrefersTheRegistration()
    {
        Assert.Equal(Registered, UninstallService.ResolveInstallPath(requestedPath: null, Registered, ExecutableFolder));
    }

    [Fact]
    public void NoRegistration_UsesTheExecutableFolder()
    {
        // A per-user install from before 2026-09-24 has no HKLM entry. The uninstaller that
        // Settings → Apps runs lives in its own install folder, which an administrator created.
        Assert.Equal(ExecutableFolder, UninstallService.ResolveInstallPath(requestedPath: null, registeredPath: null, ExecutableFolder));
    }

    [Theory]
    [InlineData(@"c:\program files\proovegaming")]
    [InlineData(@"C:\Program Files\ProoveGaming\")]
    [InlineData("C:/Program Files/ProoveGaming")]
    public void RequestedPathMatchingTheRegistration_InAnyForm_SelectsIt(string requested)
    {
        Assert.Equal(Registered, UninstallService.ResolveInstallPath(requested, Registered, ExecutableFolder));
    }

    [Fact]
    public void RequestedPathMatchingTheExecutableFolder_SelectsIt()
    {
        // An older install's own uninstaller, run from that older folder after a reinstall elsewhere
        // moved the registration: it removes the folder it was asked for, not the newer install.
        Assert.Equal(ExecutableFolder, UninstallService.ResolveInstallPath(ExecutableFolder, Registered, ExecutableFolder));
    }

    // ---- ResolveVerifiedInstallPath: resolution AND the install check, before any side effect ----
    //
    // RunAsync acts on nothing but this method's result, and on a null result it returns before a
    // process is killed or sc.exe runs (review round 1, Tests item 1).

    [Fact]
    public void Verified_RegisteredFolderThatIsOurs_IsReturned()
    {
        var resolved = UninstallService.ResolveVerifiedInstallPath(null, Registered, ExecutableFolder, _ => true, out var refusal);

        Assert.Equal(Registered, resolved);
        Assert.Null(refusal);
    }

    [Fact]
    public void Verified_RegisteredFolderThatIsNotOurs_IsRefusedWithAReason()
    {
        // e.g. InstallLocation pointing at another application's folder.
        var resolved = UninstallService.ResolveVerifiedInstallPath(null, Registered, ExecutableFolder, _ => false, out var refusal);

        Assert.Null(resolved);
        Assert.Contains("refusing to delete", refusal);
        Assert.Contains(Registered, refusal);
    }

    [Fact]
    public void Verified_NoTrustedSource_IsRefusedWithAReason()
    {
        var resolved = UninstallService.ResolveVerifiedInstallPath(@"C:\Windows", null, null, _ => true, out var refusal);

        Assert.Null(resolved);
        Assert.Contains("no registered install", refusal);
    }

    [Fact]
    public void Verified_TheInstallCheckRunsOnTheResolvedFolder_NotOnTheRequestedOne()
    {
        string? checkedPath = null;

        UninstallService.ResolveVerifiedInstallPath(@"C:\Users\victim\Documents", Registered, ExecutableFolder, path =>
        {
            checkedPath = path;
            return true;
        }, out _);

        Assert.Equal(Registered, checkedPath);
    }
}
