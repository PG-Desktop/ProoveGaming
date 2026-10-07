using System;
using System.IO;
using System.Text;
using ProoveGaming.Common.Classes;
using Xunit;

namespace ProoveGaming.Installer.Tests;

// Pins the uninstaller -> installer handoff (docs/support/missing-files-crash-analysis.md, cause 0).
//
// The uninstaller leaves a detached process deleting the install folder for up to ~2 minutes. That
// loop deletes a PATH and cannot tell the old install from a reinstalled one, so without this
// marker a quick reinstall gets partially deleted. Three properties carry the whole fix, and each
// has a test here because breaking any one of them silently restores the bug:
//
//   1. both processes must derive the SAME marker path from the same install folder;
//   2. the marker must NOT live inside the install folder (the loop would delete its own marker
//      partway through a legitimate cleanup and stop early);
//   3. Remove() must make TryRead() fail — that is the signal the loop polls.
public class UninstallHandoffTests
{
    private const string InstallPath = @"C:\Program Files\ProoveGaming";

    [Fact]
    public void MarkerPath_IsStableAcrossCalls()
    {
        // The uninstaller writes it in one process and the installer reads it in another, so a
        // per-process hash (string.GetHashCode is randomised in .NET Core) would silently never
        // match and the installer would never find the process to stop.
        Assert.Equal(UninstallHandoff.GetMarkerPath(InstallPath), UninstallHandoff.GetMarkerPath(InstallPath));
    }

    [Theory]
    [InlineData(@"C:\Program Files\ProoveGaming\")]
    [InlineData(@"c:\program files\proovegaming")]
    public void MarkerPath_IgnoresTrailingSeparatorAndCase(string variant)
    {
        // Windows paths are case-insensitive and the installer's text box happily yields a trailing
        // backslash, so these must all name the same marker.
        Assert.Equal(UninstallHandoff.GetMarkerPath(InstallPath), UninstallHandoff.GetMarkerPath(variant));
    }

    [Fact]
    public void MarkerPath_DiffersPerInstallFolder()
    {
        Assert.NotEqual(
            UninstallHandoff.GetMarkerPath(InstallPath),
            UninstallHandoff.GetMarkerPath(@"D:\Games\ProoveGaming"));
    }

    [Fact]
    public void MarkerPath_IsOutsideTheInstallFolder()
    {
        // The load-bearing one. Inside the install folder, the self-delete loop's own recursive
        // delete would remove the marker mid-cleanup; the loop would then stop early and leave an
        // uninstall unfinished — trading this bug for a worse one.
        var marker = UninstallHandoff.GetMarkerPath(InstallPath);

        Assert.False(marker.StartsWith(InstallPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MarkerPath_IsProfileIndependent()
    {
        // NOT %TEMP%. The uninstaller, its detached loop and the installer elevate independently, so
        // a profile-relative location silently breaks the handoff whenever the elevated processes
        // land in different profiles — and the failure mode is "the safety net quietly does
        // nothing", which is the hardest kind to notice.
        var marker = UninstallHandoff.GetMarkerPath(InstallPath);

        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            marker,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(marker.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(@"C:\Program Files\..\Program Files\ProoveGaming")]
    [InlineData("C:/Program Files/ProoveGaming")]
    public void MarkerPath_ResolvesEquivalentPathForms(string variant)
    {
        // The two sides receive the install path by different routes — a text box in the installer,
        // the registry in the uninstaller. Forward slashes or a ".." segment must not produce a
        // different marker, or neither side ever finds the other's.
        Assert.Equal(UninstallHandoff.GetMarkerPath(InstallPath), UninstallHandoff.GetMarkerPath(variant));
    }

    [Fact]
    public void WriteThenRead_RoundTripsThePid()
    {
        var installPath = NewTempInstallPath();
        try
        {
            UninstallHandoff.Write(installPath, selfDeleteProcessId: 4242);

            Assert.True(UninstallHandoff.TryRead(installPath, out var pid, out var startedUtc));
            Assert.Equal(4242, pid);
            // Round-tripped as ISO-8601 round-trip ("o"), so the installer's pid-reuse age check
            // has a real timestamp to compare against rather than default.
            Assert.NotEqual(default, startedUtc);
            Assert.True(Math.Abs((DateTime.UtcNow - startedUtc).TotalMinutes) < 5);
        }
        finally
        {
            UninstallHandoff.Remove(installPath);
        }
    }

    [Fact]
    public void Remove_MakesTryReadFail()
    {
        // This is the signal the detached loop polls: no marker means "an installer owns this
        // folder now, stop deleting".
        var installPath = NewTempInstallPath();
        UninstallHandoff.Write(installPath, selfDeleteProcessId: 99);
        Assert.True(UninstallHandoff.TryRead(installPath, out _, out _));

        UninstallHandoff.Remove(installPath);

        Assert.False(UninstallHandoff.TryRead(installPath, out _, out _));
    }

    [Fact]
    public void TryRead_NoMarker_ReturnsFalse()
    {
        // The overwhelmingly common path: a normal install with no prior uninstall pending. It must
        // not throw, and must not report a pid the installer would then hunt for.
        Assert.False(UninstallHandoff.TryRead(NewTempInstallPath(), out var pid, out _));
        Assert.Equal(0, pid);
    }

    [Fact]
    public void Remove_WhenNoMarkerExists_DoesNotThrow()
    {
        // The installer calls Remove unconditionally in a finally block.
        UninstallHandoff.Remove(NewTempInstallPath());
    }

    [Fact]
    public void TryRead_PidZero_ReturnsFalse()
    {
        // The uninstaller writes the marker BEFORE it knows the pid, so the loop can never observe
        // a missing marker and quit immediately. That placeholder must not be read back as a real
        // process for the installer to kill.
        var installPath = NewTempInstallPath();
        try
        {
            UninstallHandoff.Write(installPath, selfDeleteProcessId: 0);

            Assert.False(UninstallHandoff.TryRead(installPath, out var pid, out _));
            Assert.Equal(0, pid);
        }
        finally
        {
            UninstallHandoff.Remove(installPath);
        }
    }


    // ---- ShouldStopSelfDeleteProcess -------------------------------------------------------
    //
    // The installer runs ELEVATED and kills a process identified only by an id read out of a file.
    // Every case below is a way that identification can be wrong; getting any of them wrong means
    // killing something the user was using. This is where review round 3's blocker lived: the
    // original check had a lower age bound only, so every process started AFTER the marker passed.

    private static readonly DateTime MarkerTime = new(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ShouldStop_TheGenuineCase_IsTrue()
    {
        Assert.True(UninstallHandoff.ShouldStopSelfDeleteProcess(
            "powershell", MarkerTime.AddSeconds(-2), MarkerTime, MarkerTime.AddSeconds(30)));
    }

    [Theory]
    [InlineData("cmd")]
    [InlineData("POWERSHELL")]
    public void ShouldStop_AcceptsEitherShell_CaseInsensitively(string name)
    {
        Assert.True(UninstallHandoff.ShouldStopSelfDeleteProcess(
            name, MarkerTime, MarkerTime, MarkerTime.AddSeconds(5)));
    }

    [Theory]
    [InlineData("devenv")]
    [InlineData("explorer")]
    [InlineData("pwsh")]
    public void ShouldStop_NonShellProcess_IsFalse(string name)
    {
        // pwsh is excluded on purpose: the uninstaller launches windows powershell.exe, so a
        // PowerShell 7 session is somebody else's.
        Assert.False(UninstallHandoff.ShouldStopSelfDeleteProcess(
            name, MarkerTime, MarkerTime, MarkerTime.AddSeconds(5)));
    }

    [Fact]
    public void ShouldStop_ProcessStartedLongAfterTheMarker_IsFalse()
    {
        // THE BLOCKER. A stale marker survives a reboot that interrupted the loop; days later the id
        // is recycled onto an unrelated shell. Under the old lower-bound-only check this returned
        // true and the elevated installer killed that session.
        Assert.False(UninstallHandoff.ShouldStopSelfDeleteProcess(
            "powershell", MarkerTime.AddMinutes(20), MarkerTime, MarkerTime.AddMinutes(21)));
    }

    [Fact]
    public void ShouldStop_ProcessStartedBeforeTheMarker_IsFalse()
    {
        Assert.False(UninstallHandoff.ShouldStopSelfDeleteProcess(
            "powershell", MarkerTime.AddMinutes(-10), MarkerTime, MarkerTime.AddSeconds(5)));
    }

    [Fact]
    public void ShouldStop_MarkerOlderThanMaxAge_IsFalse()
    {
        // Debris from an interrupted self-delete. By now any id it names is untrustworthy, whatever
        // the process looks like.
        Assert.False(UninstallHandoff.ShouldStopSelfDeleteProcess(
            "powershell",
            MarkerTime,
            MarkerTime,
            MarkerTime + UninstallHandoff.MarkerMaxAge + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void ShouldStop_MarkerWithNoTimestamp_IsFalse()
    {
        // Previously `startedUtc == default` SKIPPED the age check, making the least trustworthy
        // marker the most permissive input. It must now be the safest.
        Assert.False(UninstallHandoff.ShouldStopSelfDeleteProcess(
            "powershell", MarkerTime, default, MarkerTime.AddSeconds(5)));
    }

    // ---- the emitted self-delete scripts ---------------------------------------------------

    [Fact]
    public void PowerShellSelfDelete_ChecksTheMarkerBeforeDeleting()
    {
        var command = ProoveGaming.Uninstaller.UninstallService.BuildSelfDeleteCommand(
            @"C:\PG\ProoveGaming.Uninstaller.exe", @"C:\PG", MarkerPath, processId: 1234);

        var script = Encoding.Unicode.GetString(
            Convert.FromBase64String(command.Split("-EncodedCommand ")[1]));

        // The guard has to be the first thing in the loop body: an installer that has started
        // copying removes the marker, and the very next pass must exit rather than delete what it
        // just wrote.
        var loopStart = script.IndexOf("for (", StringComparison.Ordinal);
        var guard = script.IndexOf($"if (-not (Test-Path -LiteralPath '{MarkerPath}')) {{ break }}", StringComparison.Ordinal);
        var firstDelete = script.IndexOf("Remove-Item", StringComparison.Ordinal);
        var folderDelete = script.IndexOf("[System.IO.Directory]::Delete(", StringComparison.Ordinal);

        Assert.True(guard > loopStart, "marker guard must be inside the retry loop");
        Assert.True(guard < firstDelete, "marker guard must run before any delete");
        Assert.True(guard < folderDelete, "marker guard must run before the install folder is deleted");
    }

    [Fact]
    public void CmdSelfDelete_ChecksTheMarkerBeforeDeleting()
    {
        // The fallback used when powershell.exe cannot be started. It had no equivalent check when
        // round 3 was written, so the fix only covered the primary path.
        var command = ProoveGaming.Uninstaller.UninstallService.BuildLegacySelfDeleteCommand(
            @"C:\PG\ProoveGaming.Uninstaller.exe", @"C:\PG", MarkerPath);

        var guard = command.IndexOf($"if not exist \"{MarkerPath}\" exit /b 0", StringComparison.Ordinal);
        var firstDelete = command.IndexOf("del /f /q", StringComparison.Ordinal);

        Assert.True(guard >= 0, "cmd fallback must check the marker");
        Assert.True(guard < firstDelete, "marker guard must run before any delete");
    }

    private const string MarkerPath = @"C:\ProgramData\ProoveGaming.uninstall.deadbeef.marker";

    [Fact]
    public void TryRead_MarkerWithUnparseableTimestamp_LeavesStartedUtcAtDefault()
    {
        // `startedUtc == default` is how ShouldStopSelfDeleteProcess recognises "no usable
        // timestamp" and refuses to kill. Normalising a FAILED parse to UTC would shift default off
        // zero on any machine west of UTC and quietly defeat that guard.
        var installPath = NewTempInstallPath();
        try
        {
            File.WriteAllText(
                UninstallHandoff.GetMarkerPath(installPath),
                "pid=4242" + Environment.NewLine + "startedUtc=not-a-date");

            Assert.True(UninstallHandoff.TryRead(installPath, out var pid, out var startedUtc));
            Assert.Equal(4242, pid);
            Assert.Equal(default, startedUtc);
            Assert.False(UninstallHandoff.ShouldStopSelfDeleteProcess(
                "powershell", DateTime.UtcNow, startedUtc, DateTime.UtcNow));
        }
        finally
        {
            UninstallHandoff.Remove(installPath);
        }
    }

    // ---- failed marker write must NOT disable the cleanup ----------------------------------
    //
    // Round 4's blocker: Write swallowed failures and returned void, so a marker that never landed
    // left the guarded loop breaking on its first pass — a silently skipped uninstall. The contract
    // is now: Write reports failure, and the caller emits the UNGUARDED script so cleanup still
    // happens and only the race protection is lost.

    [Fact]
    public void Write_ReturnsTrue_WhenTheMarkerLands()
    {
        var installPath = NewTempInstallPath();
        try
        {
            Assert.True(UninstallHandoff.Write(installPath, selfDeleteProcessId: 7));
        }
        finally
        {
            UninstallHandoff.Remove(installPath);
        }
    }

    [Fact]
    public void Write_ReturnsFalse_WhenTheMarkerCannotBeWritten()
    {
        // A directory standing where the marker file needs to go: File.WriteAllText then throws
        // UnauthorizedAccessException, which is the same exception a hardened %ProgramData% ACL or
        // endpoint protection produces. Simulating it this way keeps the test hermetic.
        var installPath = NewTempInstallPath();
        var markerPath = UninstallHandoff.GetMarkerPath(installPath);
        Directory.CreateDirectory(markerPath);
        try
        {
            Assert.False(UninstallHandoff.Write(installPath, selfDeleteProcessId: 7));
        }
        finally
        {
            Directory.Delete(markerPath);
        }
    }

    [Fact]
    public void PowerShellSelfDelete_WithNoMarker_OmitsTheGuardSoCleanupStillRuns()
    {
        var command = ProoveGaming.Uninstaller.UninstallService.BuildSelfDeleteCommand(
            @"C:\PG\ProoveGaming.Uninstaller.exe", @"C:\PG", markerPath: null, processId: 1234);

        var script = Encoding.Unicode.GetString(
            Convert.FromBase64String(command.Split("-EncodedCommand ")[1]));

        // No guard - otherwise the loop would exit before deleting anything.
        Assert.DoesNotContain("Test-Path -LiteralPath ''", script, StringComparison.Ordinal);
        Assert.DoesNotContain(".marker", script, StringComparison.Ordinal);
        // ...but the deletion itself must still be there.
        Assert.Contains(@"[System.IO.Directory]::Delete('C:\PG', $true)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PowerShellSelfDelete_NeverRecursesThroughAJunction()
    {
        // docs/plans/fix-repository-audit-findings S11. Windows PowerShell 5.1's Remove-Item -Recurse
        // and Get-ChildItem -Recurse descend INTO a junction, and this script runs elevated, so one
        // junction in the install folder aimed the delete anywhere on the disk. The folder is now
        // removed with .NET's Directory.Delete (a link is deleted as a link) and the attribute pass
        // skips reparse points instead of walking into them.
        var command = ProoveGaming.Uninstaller.UninstallService.BuildSelfDeleteCommand(
            @"C:\PG\ProoveGaming.Uninstaller.exe", @"C:\PG", MarkerPath, processId: 1234);

        var script = Encoding.Unicode.GetString(
            Convert.FromBase64String(command.Split("-EncodedCommand ")[1]));

        Assert.DoesNotContain("-Recurse", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[System.IO.FileAttributes]::ReparsePoint) { continue }", script, StringComparison.Ordinal);
    }

    [Fact]
    public void CmdSelfDelete_WithNoMarker_OmitsTheGuardSoCleanupStillRuns()
    {
        var command = ProoveGaming.Uninstaller.UninstallService.BuildLegacySelfDeleteCommand(
            @"C:\PG\ProoveGaming.Uninstaller.exe", @"C:\PG", markerPath: null);

        Assert.DoesNotContain(".marker", command, StringComparison.Ordinal);
        Assert.Contains(@"rd /s /q ""C:\PG""", command, StringComparison.Ordinal);
    }

    // A unique folder per test so runs cannot collide over one marker; never created on disk,
    // because only the marker path is derived from it.
    private static string NewTempInstallPath() =>
        Path.Combine(@"C:\ProoveGamingTests", Guid.NewGuid().ToString("N"));
}
