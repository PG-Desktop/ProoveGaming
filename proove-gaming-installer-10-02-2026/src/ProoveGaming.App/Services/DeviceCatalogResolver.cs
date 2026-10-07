using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using ProoveGaming.App.Models;
using ProoveGaming.GrpcDependencies.Device;

namespace ProoveGaming.App.Services;

// Catalog lookup by VID/PID with no live device behind it (docs/plans/add-api-v21-device-roster):
// the sign-in pull hydrates an offline card for a device model the server's roster lists but this
// machine has never seen, and the catalog (type label, artwork, merge rule) lives only in the
// DeviceHost. Injected as an interface so the pull tests fake it without gRPC.
public interface IDeviceCatalogResolver
{
    // Null on any failure — DeviceHost down, model unknown to this build, transport error.
    // Never throws into the pull except the caller's own cancellation; null means "skip
    // this model with a log line".
    Task<DeviceCatalogEntry?> ResolveAsync(int vendorId, int productId, CancellationToken ct = default);
}

public sealed class DeviceCatalogResolver : IDeviceCatalogResolver, IDisposable
{
    private readonly ILogger<DeviceCatalogResolver> _logger;
    private readonly object _gate = new();
    private GrpcChannel? _channel;
    private Device.DeviceClient? _client;
    private bool _disposed;

    public DeviceCatalogResolver(ILogger<DeviceCatalogResolver> logger)
    {
        _logger = logger;
    }

    public async Task<DeviceCatalogEntry?> ResolveAsync(int vendorId, int productId, CancellationToken ct = default)
    {
        try
        {
            Device.DeviceClient? client;
            lock (_gate)
            {
                if (_disposed)
                    return null;
                // Own channel rather than UsbHidDeviceService's private one: gRPC channels
                // multiplex, and this lazy singleton channel serves the rare hydration
                // lookups without coupling to the streaming service's reconnect lifecycle.
                // Created INSIDE the try: a channel that cannot be built must degrade to a null
                // lookup, not throw into the pull (PR #127 review, m6).
                if (_client is null)
                {
                    _channel = DeviceHostConnection.CreateChannel();
                    _client = new Device.DeviceClient(_channel);
                }
                client = _client;
            }
            if (client is null)
                return null;

            var response = await client.GetDeviceCatalogEntryAsync(
                new GetDeviceCatalogEntryRequest { VendorId = vendorId, ProductId = productId },
                cancellationToken: ct);
            return response.Found ? response.CatalogEntry : null;
        }
        catch (OperationCanceledException)
        {
            // The caller's cancellation is not a lookup failure — teardown must propagate,
            // not continue the pull with a "model unknown" answer (PR #127 review, m6).
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Catalog lookup for {VendorId:X4}:{ProductId:X4} failed; the model stays unhydrated.",
                vendorId, productId);
            return null;
        }
    }

    // The offline-card row the pull persists for a hydrated model: catalog visuals + the
    // server's name, disconnected and unit-less (device_key '') — discovery adopts and
    // refills the card through its normal no-key/disconnected matching when the device
    // first connects. Field mapping mirrors FillConnectionCard / PersistDeviceCard.
    public static DeviceRecord BuildOfflineCardRecord(DeviceCatalogEntry entry, int vendorId, int productId, string name)
    {
        return new DeviceRecord
        {
            Name = name,
            DeviceTypeLabel = entry.DeviceType ?? string.Empty,
            ImagePath = string.IsNullOrEmpty(entry.ImagePath) ? null : entry.ImagePath,
            ImageScale = entry.ImageScale > 0 ? entry.ImageScale : 1.0,
            BatteryLevel = null,
            ConnectionIcon = null,
            // The catalog's card type, not a "single" default: 6 of the 10 catalog models
            // are double-cards, and a hydrated offline card never meets the discovery
            // refill that would correct it (PR #127 review, m3).
            CardType = string.IsNullOrEmpty(entry.CardType) ? "single" : entry.CardType,
            VendorId = vendorId,
            ProductId = productId,
            DeviceKey = string.Empty,
            MergeTransports = entry.MergeTransports,
            // The pair the server rosters by IS the settings identity the App pushed, so it
            // doubles as the hydrated card's settings bucket.
            SettingsVendorId = vendorId,
            SettingsProductId = productId,
            SettingsDeviceKey = string.Empty
        };
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _channel?.Dispose();
            _channel = null;
            _client = null;
        }
    }
}
