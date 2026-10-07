using Grpc.Core;
using ProoveGaming.GrpcDependencies;
using ProoveGaming.GrpcDependencies.Device;

namespace ProoveGaming.DeviceHost.Checker;

internal static class Program
{
	private static async Task<int> Main(string[] args)
	{
		using var cts = new CancellationTokenSource();

		Console.CancelKeyPress += (_, eventArgs) =>
		{
			eventArgs.Cancel = true;
			cts.Cancel();
		};

		Console.WriteLine($@"Checking DeviceHost at: \\.\pipe\{DeviceHostPipe.PipeName}");
		Console.WriteLine("Press Ctrl+C to stop.");

		// Any server on the pipe: this diagnostic must also work against a console-run DeviceHost.
		using var channel = DeviceHostPipe.CreateChannel(requireServiceHost: false);
		var client = new Device.DeviceClient(channel);

		var requestId = 1;
		var interval = TimeSpan.FromSeconds(3);

		while (!cts.IsCancellationRequested)
		{
			try
			{
				var response = await client.GetDevicesAsync(new GetDevicesRequest { RequestId = requestId++ }, cancellationToken: cts.Token);

				var totalDevices = response.HidEntries.Count + response.BluetoothEntries.Count;
				Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] OK: DeviceHost is reachable. Devices: {totalDevices}");
			}
			catch (OperationCanceledException) when (cts.IsCancellationRequested)
			{
				break;
			}
			catch (RpcException rpcEx)
			{
				Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] gRPC error: {rpcEx.StatusCode} - {rpcEx.Status.Detail}");
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] Failed to check DeviceHost: {ex.Message}");
			}

			try
			{
				await Task.Delay(interval, cts.Token);
			}
			catch (OperationCanceledException) when (cts.IsCancellationRequested)
			{
				break;
			}
		}

		return 0;
	}
}
