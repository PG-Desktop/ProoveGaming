using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.Common.Classes;
using Xunit;

namespace ProoveGaming.Installer.Tests;

// Pins the process launching shared by the elevated installer and uninstaller
// (docs/plans/fix-repository-audit-findings, S5 and A2).
public class ServiceControlTests
{
    [Theory]
    [InlineData("sc.exe")]
    [InlineData("cmd.exe")]
    public void SystemToolPath_IsRootedInSystem32(string exe)
    {
        // Started by bare name, CreateProcess searches the EXE's own folder first — for an installer
        // that is Downloads, where a planted sc.exe would run as administrator.
        var path = SystemTool.PathOf(exe);

        Assert.True(Path.IsPathFullyQualified(path));
        Assert.Equal(Environment.SystemDirectory, Path.GetDirectoryName(path), ignoreCase: true);
    }

    [Fact]
    public void PowerShellPath_IsWindowsPowerShellUnderSystem32()
    {
        var path = SystemTool.PowerShellPath;

        Assert.StartsWith(Environment.SystemDirectory, path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(@"WindowsPowerShell\v1.0\powershell.exe", path, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task RunCaptured_ReturnsTheExitCode()
    {
        var result = await ServiceControl.RunCapturedAsync(SystemTool.PathOf("cmd.exe"), "/c exit 1060", CancellationToken.None);

        Assert.Equal(ServiceControl.ErrorServiceDoesNotExist, result.ExitCode);
    }

    [Fact]
    public async Task RunCaptured_ChildFloodingStderr_DoesNotDeadlock()
    {
        // ~200 KB to stderr, far past the pipe buffer. The runner this replaced read stdout to the end
        // BEFORE stderr: the child blocked writing stderr, never exited, and stdout never ended.
        var flood = "/c for /l %i in (1,1,4000) do @echo 0123456789012345678901234567890123456789 1>&2";

        var run = ServiceControl.RunCapturedAsync(SystemTool.PathOf("cmd.exe"), flood, CancellationToken.None);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(60)));

        Assert.Same(run, finished);
        var result = await run;
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Output.Length > 64 * 1024);
    }

    [Fact]
    public void ScRunner_UsesTheRootedScExe()
    {
        // sc.exe must exist where the runner looks, or every install would fail on the service step.
        Assert.True(File.Exists(SystemTool.PathOf("sc.exe")));
    }
}
