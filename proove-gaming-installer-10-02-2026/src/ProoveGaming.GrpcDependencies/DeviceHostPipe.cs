using System.IO.Pipes;
using System.Runtime.InteropServices;
using Grpc.Net.Client;
using Microsoft.Win32.SafeHandles;

namespace ProoveGaming.GrpcDependencies;

/// <summary>
/// The one way to reach the DeviceHost: gRPC over the named pipe <c>\\.\pipe\ProoveGaming.DeviceHost</c>,
/// shared by the App, the host and the Checker so the endpoint exists exactly once
/// (docs/plans/migrate-devicehost-named-pipe).
/// </summary>
/// <remarks>
/// <para>
/// The host used to listen on TCP localhost:5000: unauthenticated, reachable by every local process
/// in every session, and impersonable by anything that bound the port first. The pipe's ACL (see the
/// DeviceHost's <c>DeviceHostPipeSecurity</c>) decides who may connect. This side decides who the App
/// is talking to: with <c>requireServiceHost</c>, the pipe's server process must be the process the
/// Service Control Manager reports for the <see cref="ServiceName"/> service. A process that creates
/// the pipe name first can then only deny service, never impersonate the host.
/// </para>
/// </remarks>
public static class DeviceHostPipe
{
	public const string PipeName = "ProoveGaming.DeviceHost";

	// The name the installer registers the service under (InstallLayout.DeviceHostServiceName, which
	// lives in ProoveGaming.Common, a project this one does not reference) and the one the DeviceHost
	// passes to AddWindowsService. A test pins that they agree.
	public const string ServiceName = "ProoveGamingDeviceHost";

	// Bounded on purpose: NamedPipeClientStream.ConnectAsync without a timeout waits INDEFINITELY for
	// the pipe to exist, which would turn "the host is not running" from an instant refusal (as on
	// TCP) into a hang. Well under every unary deadline the App sets (5 s and up).
	internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

	/// <summary>
	/// A channel to the DeviceHost. <paramref name="requireServiceHost"/> refuses any pipe server that
	/// is not the installed service; pass false only where a console-run host must work (Debug App
	/// builds, the Checker diagnostic).
	/// </summary>
	public static GrpcChannel CreateChannel(bool requireServiceHost) => CreateChannel(PipeName, requireServiceHost);

	internal static GrpcChannel CreateChannel(string pipeName, bool requireServiceHost) =>
		// The address is a placeholder HTTP/2 framing needs; ConnectCallback decides the real endpoint.
		GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
		{
			// The handler is the channel's alone. Without this, GrpcChannel.Dispose leaves a supplied
			// handler, and the live pipe connection in its pool, open until its idle timeout.
			DisposeHttpClient = true,
			HttpHandler = new SocketsHttpHandler
			{
				ConnectCallback = (_, cancellationToken) => ConnectAsync(pipeName, requireServiceHost, cancellationToken),
			},
		});

	private static async ValueTask<Stream> ConnectAsync(string pipeName, bool requireServiceHost, CancellationToken cancellationToken)
	{
		var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
		try
		{
			await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken).ConfigureAwait(false);

			if (requireServiceHost && OperatingSystem.IsWindows() &&
				!IsTrustedServer(GetServerProcessId(pipe), GetServiceProcessId(ServiceName)))
			{
				// Surfaces as RpcException(Unavailable), which callers already treat as "host absent".
				throw new IOException($"The '{pipeName}' pipe server is not the {ServiceName} service.");
			}

			return pipe;
		}
		catch
		{
			await pipe.DisposeAsync().ConfigureAwait(false);
			throw;
		}
	}

	/// <summary>
	/// True only when both ids are known and equal. Zero means unknown: the service is not running, or
	/// an id could not be read. Unknown is never trusted.
	/// </summary>
	internal static bool IsTrustedServer(uint serverProcessId, uint serviceProcessId) =>
		serverProcessId != 0 && serverProcessId == serviceProcessId;

	private static uint GetServerProcessId(NamedPipeClientStream pipe) =>
		GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId) ? processId : 0;

	// SERVICE_QUERY_STATUS is granted to interactive users by the default service DACL, so this works
	// from the unelevated App.
	private static uint GetServiceProcessId(string serviceName)
	{
		using var manager = OpenSCManagerW(null, null, ScManagerConnect);
		if (manager.IsInvalid)
		{
			return 0;
		}

		using var service = OpenServiceW(manager, serviceName, ServiceQueryStatus);
		if (service.IsInvalid)
		{
			return 0;
		}

		return QueryServiceStatusEx(service, ScStatusProcessInfo, out var status, (uint)Marshal.SizeOf<ServiceStatusProcess>(), out _)
			? status.ProcessId
			: 0;
	}

	private const uint ScManagerConnect = 0x0001;
	private const uint ServiceQueryStatus = 0x0004;
	private const int ScStatusProcessInfo = 0;

	[StructLayout(LayoutKind.Sequential)]
	private struct ServiceStatusProcess
	{
		public uint ServiceType;
		public uint CurrentState;
		public uint ControlsAccepted;
		public uint Win32ExitCode;
		public uint ServiceSpecificExitCode;
		public uint CheckPoint;
		public uint WaitHint;
		public uint ProcessId;
		public uint ServiceFlags;
	}

	private sealed class SafeServiceHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
	{
		protected override bool ReleaseHandle() => CloseServiceHandle(handle);
	}

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

	[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern SafeServiceHandle OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

	[DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern SafeServiceHandle OpenServiceW(SafeServiceHandle manager, string serviceName, uint desiredAccess);

	[DllImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool QueryServiceStatusEx(SafeServiceHandle service, int infoLevel, out ServiceStatusProcess buffer, uint bufferSize, out uint bytesNeeded);

	[DllImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool CloseServiceHandle(IntPtr handle);
}
