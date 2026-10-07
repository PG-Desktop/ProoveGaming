using ProoveGaming.Common.Classes;
using ProoveGaming.GrpcDependencies;
using Xunit;

namespace ProoveGaming.App.Tests;

// The App verifies its pipe server by asking the Service Control Manager for the process of the
// DeviceHostPipe.ServiceName service (docs/plans/migrate-devicehost-named-pipe). That name has to be
// the one the installer registers; if the two drifted, every shipped App would refuse the real host
// and show no devices. The DeviceHost itself registers DeviceHostPipe.ServiceName, so it cannot drift.
public class DeviceHostServiceNameTests
{
    [Fact]
    public void PipeVerification_UsesTheServiceNameTheInstallerRegisters()
    {
        Assert.Equal(InstallLayout.DeviceHostServiceName, DeviceHostPipe.ServiceName);
    }
}
