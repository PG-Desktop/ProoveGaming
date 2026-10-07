using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using ProoveGaming.DeviceHost.Helpers;
using ProoveGaming.GrpcDependencies;
using ProoveGaming.DeviceHost;
using ProoveGaming.DeviceHost.Services;
using ProoveGaming.Infrastructure.Interfaces;
using ProoveGaming.Infrastructure.Services;
using Serilog;

// A Windows service starts in C:\Windows\System32. Resolving relative paths against the host's own
// folder is what puts the Serilog file sink's "logs/log.txt" in <install>\host\logs. That folder
// inherits the install folder's administrators-only ACL, so no standard user can pre-create it or
// plant a link where this LocalSystem process writes. (It used to be %TEMP%\Logs, which for
// LocalSystem is C:\Windows\Temp\Logs — a folder any user can create first.)
Directory.SetCurrentDirectory(AppContext.BaseDirectory);

var builder = WebApplication.CreateBuilder(args);
// Before anything reads configuration (Serilog, AllowedDevices, the service options).
builder.Configuration.UseEmbeddedAppSettings(builder.Environment.EnvironmentName);

builder.Services.AddGrpc(options =>
{
	options.EnableDetailedErrors = builder.Environment.IsDevelopment();
});

builder.Services.AddHostedService<Worker>();

// DpiStageMonitor is one instance shared as both a background service (runs the
// per-device Col04 readers) and a singleton DeviceService injects to push changes.
builder.Services.AddSingleton<DpiStageMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DpiStageMonitor>());

// Same dual registration: TaipanStateMonitor runs the TAIPAN vendor-collection readers and is
// the battery cache DeviceService answers GetBatteryLevel from (Telink pushes battery on
// link-up; it cannot be queried over the dongle).
builder.Services.AddSingleton<TaipanStateMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TaipanStateMonitor>());

// Same dual registration again: CosmoStateMonitor polls the headset's power state (its dongle
// stays enumerated whether or not the headset is switched on) and DeviceService uses it to decide
// which devices the App is shown.
builder.Services.AddSingleton<CosmoStateMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<CosmoStateMonitor>());

// Same dual registration: SmartInfiniteBatteryMonitor polls battery/charge over the 2.4G dongle
// for Smart Infinite keyboards (Rampage) and is the cache DeviceService answers GetBatteryLevel
// from and the source of its BatteryChange pushes.
builder.Services.AddSingleton<SmartInfiniteBatteryMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SmartInfiniteBatteryMonitor>());

builder.Services.AddWindowsService(options =>
{
	options.ServiceName = DeviceHostPipe.ServiceName;
});

// gRPC over the DeviceHost pipe only; there is no TCP endpoint any more
// (docs/plans/migrate-devicehost-named-pipe). CurrentUserOnly defaults to true, which for a
// LocalSystem service would admit SYSTEM alone and lock the App out; the pipe ACL decides instead.
builder.WebHost.UseNamedPipes(options =>
{
	options.CurrentUserOnly = false;
	options.PipeSecurity = DeviceHostPipeSecurity.Build();
});
builder.WebHost.ConfigureKestrel(kestrel =>
	kestrel.ListenNamedPipe(DeviceHostPipe.PipeName, listen => listen.Protocols = HttpProtocols.Http2));

builder.Services.AddLogging(logging =>
{
	logging.AddConsole();

	if (builder.Environment.IsDevelopment())
	{
		logging.SetMinimumLevel(LogLevel.Debug);
	}

	logging.AddSerilog();
});

builder.Host.UseSerilog((context, services, configuration) => configuration
	.ReadFrom.Configuration(context.Configuration)
	.ReadFrom.Services(services));

builder.Services.AddSingleton<IHidDeviceProvider, HidDeviceProvider>();
builder.Services.AddTransient<IHidDeviceStateFactory, HidDeviceStateFactory>();
builder.Services.AddSingleton<IHidDeviceDiscoveryService, HidDeviceDiscoveryService>();
builder.Services.AddSingleton<IDeviceRgbServiceFactory, DeviceRgbServiceFactory>();
builder.Services.AddSingleton<IDeviceKeyMappingServiceFactory, DeviceKeyMappingServiceFactory>();
builder.Services.AddSingleton<IDeviceKeyboardMacroServiceFactory, DeviceKeyboardMacroServiceFactory>();
builder.Services.AddSingleton<IDeviceActuationServiceFactory, DeviceActuationServiceFactory>();
builder.Services.AddSingleton<IDeviceExtendedKeysServiceFactory, DeviceExtendedKeysServiceFactory>();
// DeviceInfoProvider should be singleton as it keeps bluetooth devices states
builder.Services.AddSingleton<IDeviceInfoProvider, DeviceInfoProvider>();
builder.Services.AddSingleton<IBluetoothDeviceDiscoveryService, BluetoothDeviceDiscoveryService>();
builder.Services.AddSingleton<HidDeviceMapRegistry>();

builder.Configuration.AddEnvironmentVariables();
builder.Services.Configure<AllowedDevices>(builder.Configuration.GetSection("AllowedDevices"));

var app = builder.Build();

app.MapGrpcService<DeviceService>();

var logger = app.Services.GetRequiredService<ILogger<Program>>();

AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
	var ex = (Exception)e.ExceptionObject;
	logger?.LogCritical(ex, "CurrentDomain_UnhandledException occurred with message: {Message}", ex?.Message);
};

await app.RunAsync();