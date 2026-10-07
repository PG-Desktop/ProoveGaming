using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.DeviceHost;

internal class Worker(ILogger<Worker> logger,
	IHidDeviceDiscoveryService hidDeviceDiscoveryService,
	IBluetoothDeviceDiscoveryService bluetoothDeviceDiscoveryService) : BackgroundService
{
	private readonly ILogger<Worker> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
	private readonly IHidDeviceDiscoveryService _hidDeviceDiscoveryService = hidDeviceDiscoveryService ?? throw new ArgumentNullException(nameof(hidDeviceDiscoveryService));
	private readonly IBluetoothDeviceDiscoveryService _bluetoothDeviceDiscoveryService = bluetoothDeviceDiscoveryService ?? throw new ArgumentNullException(nameof(bluetoothDeviceDiscoveryService));

	// The worker's real job is starting and stopping discovery in StartAsync / StopAsync; there is
	// no periodic work to do here. This used to log a heartbeat every 5 seconds for the lifetime of
	// the service, which wrote to the Serilog file sink forever and said nothing a startup line
	// does not. Idling on the cancellation token keeps the BackgroundService alive at zero cost.
	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		_logger.LogInformation("Device host worker started; discovery is running.");

		try
		{
			await Task.Delay(Timeout.Infinite, stoppingToken);
		}
		catch (OperationCanceledException)
		{
			// Normal shutdown.
		}
	}

	public override async Task StartAsync(CancellationToken cancellationToken)
	{
		_hidDeviceDiscoveryService.Start();

		// Bluetooth is OPTIONAL — a machine may have no adapter, or have it switched off — so its
		// startup must never decide whether this host runs. The service guards its own radio calls
		// as well; this is the outer belt, because anything thrown here aborts the hosted service
		// and with it the gRPC endpoint, and then wired devices stop working too. That is exactly
		// what happened with Bluetooth switched off: every device read as disconnected in the App,
		// cable-connected ones included.
		try
		{
			await _bluetoothDeviceDiscoveryService.StartDiscoveringIfNotStartedAsync(cancellationToken);
		}
		catch (OperationCanceledException)
		{
			throw;   // A real shutdown request; let the host handle it.
		}
		catch (Exception ex)
		{
			_logger?.LogWarning(ex, "Bluetooth discovery failed to start; continuing without Bluetooth.");
		}

		await base.StartAsync(cancellationToken);
	}

	public override Task StopAsync(CancellationToken cancellationToken)
	{
		try
		{
			_hidDeviceDiscoveryService?.Stop();
			_bluetoothDeviceDiscoveryService?.StopDiscovering();
		}
		catch (Exception) { }

		return base.StopAsync(cancellationToken);
	}
}
