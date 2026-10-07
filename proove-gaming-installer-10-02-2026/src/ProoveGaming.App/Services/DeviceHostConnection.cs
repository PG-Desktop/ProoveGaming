using Grpc.Net.Client;
using ProoveGaming.GrpcDependencies;

namespace ProoveGaming.App.Services;

// The App's channel to the DeviceHost, shared by UsbHidDeviceService and DeviceCatalogResolver so
// the verification rule lives in one place (docs/plans/migrate-devicehost-named-pipe).
internal static class DeviceHostConnection
{
    // A shipped App talks only to the installed ProoveGamingDeviceHost service (see
    // DeviceHostPipe). A developer runs the host as a console process the Service Control Manager
    // does not know, so Debug builds accept any server on the pipe.
#if DEBUG
    private const bool RequireServiceHost = false;
#else
    private const bool RequireServiceHost = true;
#endif

    public static GrpcChannel CreateChannel() => DeviceHostPipe.CreateChannel(RequireServiceHost);
}
