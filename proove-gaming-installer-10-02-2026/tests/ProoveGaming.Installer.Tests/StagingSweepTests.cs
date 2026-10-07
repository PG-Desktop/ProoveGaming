using System;
using ProoveGaming.Installer.Views;
using Xunit;

namespace ProoveGaming.Installer.Tests;

// Pins which leftover payload staging folders the installer removes at start
// (docs/plans/fix-repository-audit-findings, review round 1 S1). Staging moved from %TEMP%, which
// Windows cleans up, to %ProgramData%, which nothing ever cleans, so an installer killed mid-install
// would otherwise leave ~250 MB behind for good. The sweep runs elevated in %ProgramData%, so every
// condition below is a way to be sure a folder is really one of ours and really abandoned.
public class StagingSweepTests
{
    private const string Guid32 = "0123456789abcdef0123456789abcdef";
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void OldAdministratorOwnedStagingFolder_IsSwept()
    {
        Assert.True(MainWindow.IsSweepableStagingFolder($"ProoveGamingInstaller.{Guid32}", Now.AddHours(-3), Now, ownerIsTrusted: true));
    }

    [Fact]
    public void RecentStagingFolder_IsKept()
    {
        // Could belong to another installer window that is extracting right now.
        Assert.False(MainWindow.IsSweepableStagingFolder($"ProoveGamingInstaller.{Guid32}", Now.AddMinutes(-10), Now, ownerIsTrusted: true));
    }

    [Fact]
    public void UserOwnedFolder_IsKept()
    {
        // A standard user can create a folder with this name in %ProgramData%, but not one owned by
        // Administrators — so ownership is what proves the installer made it.
        Assert.False(MainWindow.IsSweepableStagingFolder($"ProoveGamingInstaller.{Guid32}", Now.AddDays(-2), Now, ownerIsTrusted: false));
    }

    [Theory]
    [InlineData("ProoveGamingInstaller")]
    [InlineData("ProoveGamingInstaller.")]
    [InlineData("ProoveGamingInstaller.not-a-guid")]
    [InlineData("ProoveGamingInstaller.0123456789abcdef0123456789abcdeg")]
    [InlineData("ProoveGamingInstaller.0123456789abcdef0123456789abcdef0")]
    [InlineData("ProoveGaming")]
    public void FolderWhoseNameIsNotExactlyPrefixPlusGuid_IsKept(string name)
    {
        Assert.False(MainWindow.IsSweepableStagingFolder(name, Now.AddDays(-2), Now, ownerIsTrusted: true));
    }
}
