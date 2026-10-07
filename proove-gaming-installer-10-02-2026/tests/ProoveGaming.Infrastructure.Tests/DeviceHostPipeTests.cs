using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using ProoveGaming.DeviceHost.Helpers;
using ProoveGaming.GrpcDependencies;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.Infrastructure.Tests;

// Pins the App ↔ DeviceHost named-pipe transport (docs/plans/migrate-devicehost-named-pipe): who may
// connect (the pipe ACL), that a missing host fails fast instead of hanging, and that a client
// requiring the real service refuses any other pipe server. Each test uses its own pipe name, so it
// never meets an installed DeviceHost.
[SupportedOSPlatform("windows")]
public class DeviceHostPipeTests
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Interactive = new(WellKnownSidType.InteractiveSid, null);
    private static readonly SecurityIdentifier Network = new(WellKnownSidType.NetworkSid, null);

    // ── The ACL ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PipeSecurity_AdmitsOnlySystemAdministratorsInteractiveUsersAndTheHostsOwnAccount()
    {
        var security = DeviceHostPipeSecurity.Build();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToArray();
        using var self = WindowsIdentity.GetCurrent();

        Assert.True(security.AreAccessRulesProtected);
        var expected = new[] { LocalSystem, Administrators, Interactive, Network, self.User! }.Distinct().Count();
        Assert.Equal(expected, rules.Select(r => r.IdentityReference).Distinct().Count());
        Assert.Contains(rules, r => r.IdentityReference.Equals(LocalSystem) && r.AccessControlType == AccessControlType.Allow && r.PipeAccessRights.HasFlag(PipeAccessRights.FullControl));
        Assert.Contains(rules, r => r.IdentityReference.Equals(Administrators) && r.AccessControlType == AccessControlType.Allow);
        Assert.Contains(rules, r => r.IdentityReference.Equals(Interactive) && r.AccessControlType == AccessControlType.Allow);
        Assert.Contains(rules, r => r.IdentityReference.Equals(Network) && r.AccessControlType == AccessControlType.Deny);
    }

    [Fact]
    public void PipeSecurity_GivesTheHostsOwnAccountFullControl()
    {
        // Needed to create every pipe instance after the first (CreateNewInstance). Without it a
        // console-run DeviceHost, running as the developer, failed to start.
        using var self = WindowsIdentity.GetCurrent();
        var rules = DeviceHostPipeSecurity.Build().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>();

        Assert.Contains(rules, r => r.IdentityReference.Equals(self.User) && r.AccessControlType == AccessControlType.Allow && r.PipeAccessRights.HasFlag(PipeAccessRights.FullControl));
    }

    [Theory]
    [InlineData(WellKnownSidType.WorldSid)]
    [InlineData(WellKnownSidType.BuiltinUsersSid)]
    [InlineData(WellKnownSidType.AuthenticatedUserSid)]
    [InlineData(WellKnownSidType.ServiceSid)]
    public void PipeSecurity_GrantsNothingToBroadGroups(WellKnownSidType group)
    {
        var sid = new SecurityIdentifier(group, null);
        var rules = DeviceHostPipeSecurity.Build().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>();

        Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(sid) && r.AccessControlType == AccessControlType.Allow);
    }

    [Theory]
    [InlineData(WellKnownSidType.InteractiveSid)]
    [InlineData(WellKnownSidType.BuiltinAdministratorsSid)]
    public void PipeSecurity_LetsNoConnectingGroupCreateInstancesOrRewriteTheAcl(WellKnownSidType group)
    {
        // CreateNewInstance is what would let another process serve the live pipe name next to the
        // host; ChangePermissions / TakeOwnership would let it rewrite this ACL.
        const PipeAccessRights Forbidden = PipeAccessRights.CreateNewInstance | PipeAccessRights.ChangePermissions | PipeAccessRights.TakeOwnership;
        var sid = new SecurityIdentifier(group, null);
        var rules = DeviceHostPipeSecurity.Build().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>();

        Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(sid) && r.AccessControlType == AccessControlType.Allow && (r.PipeAccessRights & Forbidden) != 0);
    }

    // ── The transport ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Call_OverThePipe_ReachesTheServer()
    {
        var pipeName = NewPipeName();
        await using var server = await StartServerAsync(pipeName);
        using var channel = DeviceHostPipe.CreateChannel(pipeName, requireServiceHost: false);

        var response = await new Device.DeviceClient(channel).GetDevicesAsync(new GetDevicesRequest { RequestId = 7 }, deadline: DateTime.UtcNow.AddSeconds(10));

        Assert.Equal(7, response.RequestId);
    }

    [Fact]
    public async Task DisposingTheChannel_ClosesItsPipeConnection()
    {
        // The factory hands the channel its own handler, which GrpcChannel disposes only when told
        // to (DisposeHttpClient); otherwise every re-created channel left a live connection behind.
        var pipeName = NewPipeName();
        await using var server = await StartServerAsync(pipeName);
        var channel = DeviceHostPipe.CreateChannel(pipeName, requireServiceHost: false);
        await new Device.DeviceClient(channel).GetDevicesAsync(new GetDevicesRequest(), deadline: DateTime.UtcNow.AddSeconds(10));
        Assert.Equal(1, server.OpenConnections);

        channel.Dispose();

        var stopwatch = Stopwatch.StartNew();
        while (server.OpenConnections != 0 && stopwatch.Elapsed < TimeSpan.FromSeconds(10))
        {
            await Task.Delay(50);
        }
        Assert.Equal(0, server.OpenConnections);
    }

    [Fact]
    public async Task NoServer_FailsFast_InsteadOfWaitingForever()
    {
        // NamedPipeClientStream.ConnectAsync without a timeout waits indefinitely for the pipe to
        // appear; the factory bounds it at ConnectTimeout, well inside this call's deadline.
        using var channel = DeviceHostPipe.CreateChannel(NewPipeName(), requireServiceHost: false);
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<RpcException>(() =>
            new Device.DeviceClient(channel).GetDevicesAsync(new GetDevicesRequest(), deadline: DateTime.UtcNow.AddSeconds(20)).ResponseAsync);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
    }

    [Fact]
    public async Task ServerThatIsNotTheService_IsRefused_WhenTheServiceIsRequired()
    {
        // This test process is not the SCM's ProoveGamingDeviceHost, whether or not that service is
        // installed on the machine running the tests.
        var pipeName = NewPipeName();
        await using var server = await StartServerAsync(pipeName);
        using var channel = DeviceHostPipe.CreateChannel(pipeName, requireServiceHost: true);

        var failure = await Assert.ThrowsAsync<RpcException>(() =>
            new Device.DeviceClient(channel).GetDevicesAsync(new GetDevicesRequest(), deadline: DateTime.UtcNow.AddSeconds(10)).ResponseAsync);

        Assert.Equal(StatusCode.Unavailable, failure.StatusCode);
        Assert.Equal(0, server.Calls);
    }

    // ── The decision rule ───────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(4242u, 4242u, true)]
    [InlineData(4242u, 1111u, false)]
    [InlineData(4242u, 0u, false)]
    [InlineData(0u, 0u, false)]
    [InlineData(0u, 4242u, false)]
    public void IsTrustedServer_OnlyKnownAndEqualIds(uint serverPid, uint servicePid, bool expected)
    {
        Assert.Equal(expected, DeviceHostPipe.IsTrustedServer(serverPid, servicePid));
    }

    [Fact]
    public void ConnectTimeout_StaysBelowTheShortestAppDeadline()
    {
        // The App's shortest unary deadline is 5 s (UsbHidDeviceService.GetDevices); a connect
        // bound at or above it would turn "host absent" into DeadlineExceeded.
        Assert.True(DeviceHostPipe.ConnectTimeout < TimeSpan.FromSeconds(5));
    }

    private static string NewPipeName() => $"PgTest.{Guid.NewGuid():N}";

    private static async Task<TestServer> StartServerAsync(string pipeName)
    {
        var builder = WebApplication.CreateSlimBuilder();
        // The production ACL, exactly as the host builds it. The test's client runs as the same
        // account as its server, which the ACL grants full control, so this also works on a CI runner
        // that does not log on as INTERACTIVE.
        builder.WebHost.UseNamedPipes(options =>
        {
            options.CurrentUserOnly = false;
            options.PipeSecurity = DeviceHostPipeSecurity.Build();
        });
        var counter = new CallCounter();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenNamedPipe(pipeName, listen =>
        {
            listen.Protocols = HttpProtocols.Http2;
            listen.Use(next => async connection =>
            {
                Interlocked.Increment(ref counter.OpenConnections);
                try
                {
                    await next(connection);
                }
                finally
                {
                    Interlocked.Decrement(ref counter.OpenConnections);
                }
            });
        }));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(counter);

        var app = builder.Build();
        app.MapGrpcService<CountingDeviceService>();
        await app.StartAsync();
        return new TestServer(app);
    }

    private sealed class CallCounter
    {
        public int Calls;
        public int OpenConnections;
    }

    private sealed class CountingDeviceService(CallCounter counter) : Device.DeviceBase
    {
        public override Task<BatchDevicesResponse> GetDevices(GetDevicesRequest request, ServerCallContext context)
        {
            Interlocked.Increment(ref counter.Calls);
            return Task.FromResult(new BatchDevicesResponse { RequestId = request.RequestId });
        }
    }

    private sealed class TestServer(WebApplication app) : IAsyncDisposable
    {
        public int Calls => app.Services.GetRequiredService<CallCounter>().Calls;

        public int OpenConnections => Volatile.Read(ref app.Services.GetRequiredService<CallCounter>().OpenConnections);

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
