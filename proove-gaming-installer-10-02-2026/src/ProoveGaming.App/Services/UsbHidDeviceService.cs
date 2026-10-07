using Grpc.Core;
using Grpc.Net.Client;
using ProoveGaming.App.Models;
using ProoveGaming.GrpcDependencies.Device;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ProoveGaming.App.Services;

public class UsbHidDeviceService : IUsbHidDeviceService, IDisposable
{
    private CancellationTokenSource? _cts;
    private TaskCompletionSource? _tcs;
    private GrpcChannel? _channel;
    private Device.DeviceClient? _client;
    private bool _reconnecting;
    private readonly bool _ownsChannel = true;

    // Whether the DeviceHost is answering, as 0/1 so the transition can be claimed atomically —
    // the stream attempt runs on a thread-pool thread while StopCommunication can run on the UI one.
    //
    // Needed because "no devices" and "no host" are otherwise indistinguishable to the App. Device
    // presence only ever arrives as a push, so with the service stopped the LAST push stands
    // forever and every card keeps showing as connected: the App has no timer that would ever
    // contradict it. This is the signal that says the pushes themselves are no longer trustworthy.
    private int _hostAvailable;

    // Test seam: lets unit tests drive the reconnect loop without real 3 s waits.
    internal Func<CancellationToken, Task> RetryDelayAsync = static token => Task.Delay(3000, token);

    // Latest active DPI stage / report rate (Hz) per (vendorId, productId, deviceKey), as
    // reported by the DeviceHost — keyed per physical unit so two identical mice don't share.
    private readonly ConcurrentDictionary<(int, int, string), int> _lastDpiStage = new();
    private readonly ConcurrentDictionary<(int, int, string), int> _lastReportRate = new();

    public bool IsRunning { get; private set; }

    public UsbHidDeviceService()
    {
    }

    // Test seam: injects a scripted client so the stream lifecycle is unit-testable.
    // With this constructor StartCommunication skips channel creation/disposal; the
    // release path through the public constructor is unchanged.
    internal UsbHidDeviceService(Device.DeviceClient client)
    {
        _client = client;
        _ownsChannel = false;
    }

    // The only SYNCHRONOUS call on this interface, and the only one that used to throw when the
    // DeviceHost was gone. Both mattered together: it runs inside UsbHidDeviceService_DeviceFound
    // while that handler holds _eventGate, so a call that does not return stalls every later device
    // event behind it.
    //
    // The deadline is the part that fixes a hang. GrpcChannel disables the underlying HttpClient
    // timeout and expects callers to set deadlines, so with none this waits FOREVER against a host
    // that is unreachable rather than cleanly refused — a service mid-start, a stopped service whose
    // localhost connection is half-open. A stopped host that refuses the connection fails fast and
    // never showed the problem, which is why it survived this long.
    //
    // Empty is the honest answer when the host cannot be reached: the App knows about HID devices
    // only through the host, so it enumerates nothing. Callers that must not act on that distinction
    // check for empty themselves (see RestoreHidTransports).
    public List<HidDeviceResponse> GetDevices()
    {
        if (_client == null) return [];
        try
        {
            return _client.GetDevices(new GetDevicesRequest(), deadline: DateTime.UtcNow.AddSeconds(5))
                ?.HidEntries.ToList() ?? [];
        }
        catch (RpcException ex)
        {
            NotifyError?.Invoke(this, $"GetDevices RPC failed: {ex.Status.Detail}");
            return [];
        }
        catch (Exception ex)
        {
            NotifyError?.Invoke(this, $"GetDevices failed: {ex.Message}");
            return [];
        }
    }

    public async Task<(int batteryValue, bool isCharging)> GetBatteryLevelAsync(int vendorId, int productId, string deviceKey)
    {
        if (_client == null)
            return (-1, false);
        try
        {
            var response = await _client.GetBatteryLevelAsync(new GetBatteryLevelRequest
            {
                VendorId = vendorId,
                ProductId = productId,
                DeviceKey = deviceKey ?? string.Empty,
            }, deadline: DateTime.UtcNow.AddSeconds(20));
            return response.Success ? (response.BatteryValue, response.IsCharging) : (-1, false);
        }
        catch (RpcException ex)
        {
            NotifyError?.Invoke(this, $"GetBatteryLevel RPC failed: {ex.Status.Detail}");
            return (-1, false);
        }
        catch (Exception ex)
        {
            NotifyError?.Invoke(this, $"GetBatteryLevel failed: {ex.Message}");
            return (-1, false);
        }
    }

    public async Task<bool> SetDeviceLedColorAsync(int vendorId, int productId, string deviceKey, string deviceType, byte r, byte g, byte b, byte effect, byte brightness, byte speed)
    {
        if (_client == null)
            return false;
        try
        {
            var response = await _client.SetDeviceLedColorAsync(new SetDeviceLedColorRequest
            {
                VendorId = vendorId,
                ProductId = productId,
                DeviceKey = deviceKey ?? string.Empty,
                DeviceType = deviceType ?? string.Empty,
                R = r,
                G = g,
                B = b,
                Effect = effect,
                Brightness = brightness,
                Speed = speed,
            });
            return response.Success;
        }
        catch (RpcException ex)
        {
            NotifyError?.Invoke(this, $"SetDeviceLedColor RPC failed: {ex.Status.Detail}");
            return false;
        }
        catch (Exception ex)
        {
            NotifyError?.Invoke(this, $"SetDeviceLedColor failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SetDpiStagesAsync(int vendorId, int productId, string deviceKey, IReadOnlyList<(int dpi, byte r, byte g, byte b)> stages)
    {
        if (_client == null) return false;
        try
        {
            var req = new SetDpiStagesRequest { VendorId = vendorId, ProductId = productId, DeviceKey = deviceKey ?? string.Empty };
            foreach (var (dpi, r, g, b) in stages)
                req.Stages.Add(new DpiStage { DpiValue = dpi, R = r, G = g, B = b });
            var response = await _client.SetDpiStagesAsync(req);
            return response.Success;
        }
        catch (RpcException ex)
        {
            NotifyError?.Invoke(this, $"SetDpiStages RPC failed: {ex.Status.Detail}");
            return false;
        }
        catch (Exception ex)
        {
            NotifyError?.Invoke(this, $"SetDpiStages failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SetActiveDpiStageAsync(int vendorId, int productId, string deviceKey, byte stageIndex)
    {
        if (_client == null) return false;
        try
        {
            var response = await _client.SetActiveDpiStageAsync(new SetActiveDpiStageRequest
            {
                VendorId = vendorId,
                ProductId = productId,
                DeviceKey = deviceKey ?? string.Empty,
                StageIndex = stageIndex,
            });
            // _lastDpiStage otherwise only updates from device-pushed changes (ReadServerResponses),
            // so an App-initiated write here (e.g. reverting to a preset's saved stage on Discard)
            // would leave it holding a stale, earlier physical-button value — and the next
            // LoadPersistedDeviceStateAsync trusts THIS cache over the freshly-correct persisted
            // value whenever it has any entry at all, showing the stale stage on reopen.
            if (response.Success)
                _lastDpiStage[(vendorId, productId, deviceKey ?? string.Empty)] = stageIndex;
            return response.Success;
        }
        catch (RpcException ex)
        {
            NotifyError?.Invoke(this, $"SetActiveDpiStage RPC failed: {ex.Status.Detail}");
            return false;
        }
        catch (Exception ex)
        {
            NotifyError?.Invoke(this, $"SetActiveDpiStage failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SetReportRateAsync(int vendorId, int productId, string deviceKey, int hz)
    {
        if (_client == null) return false;
        try
        {
            var response = await _client.SetReportRateAsync(new SetReportRateRequest
            {
                VendorId = vendorId,
                ProductId = productId,
                DeviceKey = deviceKey ?? string.Empty,
                Hz = hz,
            });
            // Mirrors SetActiveDpiStageAsync: keep _lastReportRate from going stale after an
            // App-initiated write (see that method's comment for why it matters on reopen).
            if (response.Success)
                _lastReportRate[(vendorId, productId, deviceKey ?? string.Empty)] = hz;
            return response.Success;
        }
        catch (RpcException ex)
        {
            NotifyError?.Invoke(this, $"SetReportRate RPC failed: {ex.Status.Detail}");
            return false;
        }
        catch (Exception ex)
        {
            NotifyError?.Invoke(this, $"SetReportRate failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SetButtonBindingsAsync(int vendorId, int productId, string deviceKey, IReadOnlyList<(MouseButton button, MouseButtonAction action, MacroDefinition? macro)> bindings)
    {
        if (_client == null) return false;
        try
        {
            var req = new SetButtonBindingsRequest { VendorId = vendorId, ProductId = productId, DeviceKey = deviceKey ?? string.Empty };
            foreach (var (button, action, macro) in bindings)
            {
                var binding = new ButtonBinding { Button = button, Action = action };
                if (macro != null) binding.Macro = macro;
                req.Bindings.Add(binding);
            }
            var response = await _client.SetButtonBindingsAsync(req);
            return response.Success;
        }
        catch (RpcException ex)
        {
            NotifyError?.Invoke(this, $"SetButtonBindings RPC failed: {ex.Status.Detail}");
            return false;
        }
        catch (Exception ex)
        {
            NotifyError?.Invoke(this, $"SetButtonBindings failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> ApplyDeviceConfigurationAsync(
        int vendorId, int productId, string deviceKey,
        (byte r, byte g, byte b, byte effect, byte brightness, byte speed)? led,
        IReadOnlyList<(MouseButton button, MouseButtonAction action, MacroDefinition? macro)>? bindings,
        (byte debounce, byte liftOff)? mouseSettings,
        IReadOnlyList<(int dpi, byte r, byte g, byte b)>? dpiStages,
        int? activeStage,
        int? reportRateHz)
    {
        if (_client == null) return false;
        try
        {
            var req = new ApplyDeviceConfigurationRequest { VendorId = vendorId, ProductId = productId, DeviceKey = deviceKey ?? string.Empty };

            if (led is { } l)
                req.Led = new DeviceLedConfig { R = l.r, G = l.g, B = l.b, Effect = l.effect, Brightness = l.brightness, Speed = l.speed };
            if (bindings != null)
                foreach (var (button, action, macro) in bindings)
                {
                    var binding = new ButtonBinding { Button = button, Action = action };
                    if (macro != null) binding.Macro = macro;
                    req.Bindings.Add(binding);
                }
            if (mouseSettings is { } ms)
                req.MouseSettings = new DeviceMouseSettings { BtnDebounceMs = ms.debounce, LiftOffMm = ms.liftOff };
            if (dpiStages != null)
                foreach (var (dpi, r, g, b) in dpiStages)
                    req.DpiStages.Add(new DpiStage { DpiValue = dpi, R = r, G = g, B = b });
            if (activeStage is { } stage) req.ActiveStage = stage;
            if (reportRateHz is { } hz) req.ReportRateHz = hz;

            var response = await _client.ApplyDeviceConfigurationAsync(req);
            // Same reasoning as SetActiveDpiStageAsync/SetReportRateAsync: this is the coordinated
            // write Save Preset, preset switch, and the unsaved-changes Discard revert all go
            // through, so it's the path most likely to otherwise leave _lastDpiStage/_lastReportRate
            // stale relative to what was actually just written.
            if (response.Success)
            {
                var key = (vendorId, productId, deviceKey ?? string.Empty);
                if (activeStage is { } appliedStage) _lastDpiStage[key] = appliedStage;
                if (reportRateHz is { } appliedHz) _lastReportRate[key] = appliedHz;
            }
            return response.Success;
        }
        catch (RpcException ex)
        {
            NotifyError?.Invoke(this, $"ApplyDeviceConfiguration RPC failed: {ex.Status.Detail}");
            return false;
        }
        catch (Exception ex)
        {
            NotifyError?.Invoke(this, $"ApplyDeviceConfiguration failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SetMacrosAsync(
        int vendorId, int productId, string deviceKey,
        IReadOnlyList<(int Index, MacroRepeatMode Mode, IReadOnlyList<MacroEvent> Events, int RepeatCount)> entries)
    {
        if (_client == null)
            return false;

        try
        {
            var request = new SetMacrosRequest
            {
                VendorId = vendorId,
                ProductId = productId,
                DeviceKey = deviceKey ?? string.Empty,
            };

            foreach (var (index, mode, events, repeatCount) in entries)
            {
                var entry = new MacroLibraryEntry
                {
                    Index = (uint)index,
                    Mode = mode,
                    RepeatCount = (uint)Math.Clamp(repeatCount, 1, 65535),
                };
                foreach (var e in events)
                    entry.Events.Add(new MacroEvent
                    {
                        KeyCode = e.KeyCode,
                        IsPress = e.IsPress,
                        DelayMs = e.DelayMs,
                    });
                request.Entries.Add(entry);
            }

            // Deadline per PR #67 review (M1): the upload is ~40 chunked HID exchanges with
            // retries on the host; without a deadline a wedged host hangs the App forever.
            var response = await _client.SetMacrosAsync(request, deadline: DateTime.UtcNow.AddSeconds(60));
            // A clean RPC carrying "the device refused" is just as much an error as a failed RPC —
            // since the upload verify became authoritative this is the branch a firmware that
            // discards the library actually takes, and it must not stay log-invisible.
            if (!response.Success)
                NotifyError?.Invoke(this, "SetMacros failed: the device did not store the macro library");
            return response.Success;
        }
        catch (RpcException ex)
        {
            NotifyError?.Invoke(this, $"SetMacros RPC failed: {ex.Status.Detail}");
            return false;
        }
        catch (Exception ex)
        {
            NotifyError?.Invoke(this, $"SetMacros failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SetKeyMappingAsync(int vendorId, int productId, byte matrixIndex, byte type, byte code1, byte code2)
    {
        if (_client == null)
            return false;
        try
        {
            var response = await _client.SetKeyMappingAsync(new SetKeyMappingRequest
            {
                VendorId = vendorId,
                ProductId = productId,
                MatrixIndex = matrixIndex,
                TypeByte = type,
                Code1 = code1,
                Code2 = code2,
            }, deadline: DateTime.UtcNow.AddSeconds(20));
            return response.Success;
        }
        catch (RpcException ex)
        {
            NotifyError?.Invoke(this, $"SetKeyMapping RPC failed: {ex.Status.Detail}");
            return false;
        }
        catch (Exception ex)
        {
            NotifyError?.Invoke(this, $"SetKeyMapping failed: {ex.Message}");
            return false;
        }
    }

    public async Task<(IReadOnlyDictionary<byte, byte> ScancodeToSlot, IReadOnlyDictionary<byte, byte> UserOverrides)?>
        ProbeKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
    {
        if (_client == null)
            return null;
        try
        {
            var response = await _client.ProbeKeyMatrixAsync(new ProbeKeyMatrixRequest
            {
                VendorId = vendorId,
                ProductId = productId,
                MaxSlot = maxSlot,
            }, deadline: DateTime.UtcNow.AddSeconds(20));
            if (!response.Success) return null;

            var scancodeToSlot = new Dictionary<byte, byte>(response.ScancodeToSlot.Count);
            foreach (var kv in response.ScancodeToSlot)
                scancodeToSlot[(byte)kv.Key] = (byte)kv.Value;

            var userOverrides = new Dictionary<byte, byte>(response.UserOverrides.Count);
            foreach (var kv in response.UserOverrides)
                userOverrides[(byte)kv.Key] = (byte)kv.Value;

            return (scancodeToSlot, userOverrides);
        }
        catch (RpcException ex)
        {
            NotifyError?.Invoke(this, $"ProbeKeyMatrix RPC failed: {ex.Status.Detail}");
            return null;
        }
        catch (Exception ex)
        {
            NotifyError?.Invoke(this, $"ProbeKeyMatrix failed: {ex.Message}");
            return null;
        }
    }

    public async Task<IReadOnlyDictionary<byte, (byte Type, byte Code1, byte Code2)>?>
        ReadKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
    {
        if (_client == null)
            return null;
        try
        {
            var response = await _client.ReadKeyMatrixAsync(new ReadKeyMatrixRequest
            {
                VendorId = vendorId,
                ProductId = productId,
                MaxSlot = maxSlot,
            }, deadline: DateTime.UtcNow.AddSeconds(20));
            if (!response.Success) return null;

            var records = new Dictionary<byte, (byte, byte, byte)>(response.Records.Count);
            foreach (var kv in response.Records)
                records[(byte)kv.Key] = ((byte)kv.Value.Type, (byte)kv.Value.Code1, (byte)kv.Value.Code2);

            return records;
        }
        catch (RpcException ex)
        {
            NotifyError?.Invoke(this, $"ReadKeyMatrix RPC failed: {ex.Status.Detail}");
            return null;
        }
        catch (Exception ex)
        {
            NotifyError?.Invoke(this, $"ReadKeyMatrix failed: {ex.Message}");
            return null;
        }
    }

    public async Task<KeyActuationInfo?> GetKeyActuationAsync(int vendorId, int productId)
    {
        if (_client == null)
            return null;
        try
        {
            var response = await _client.GetKeyActuationAsync(new GetKeyActuationRequest
            {
                VendorId = vendorId,
                ProductId = productId,
            }, deadline: DateTime.UtcNow.AddSeconds(20));
            if (!response.Success) return null;
            if (!response.Supported || response.Values == null || response.Defaults == null)
                return new KeyActuationInfo(false, 0, 0, false, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, 0, 0);

            var perKey = new Dictionary<int, KeyActuationKeyValues>(response.PerKeyValues.Count);
            foreach (var (slot, values) in response.PerKeyValues)
                perKey[slot] = new KeyActuationKeyValues(
                    PressCentiMm: (int)values.PressCentiMm,
                    ReleaseCentiMm: (int)values.ReleaseCentiMm,
                    RapidTrigger: values.RapidTrigger,
                    QuickPressCentiMm: (int)values.QuickPressCentiMm,
                    QuickReleaseCentiMm: (int)values.QuickReleaseCentiMm);

            return new KeyActuationInfo(
                Supported: true,
                PressCentiMm: (int)response.Values.PressCentiMm,
                ReleaseCentiMm: (int)response.Values.ReleaseCentiMm,
                RapidTrigger: response.Values.RapidTrigger,
                QuickPressCentiMm: (int)response.Values.QuickPressCentiMm,
                QuickReleaseCentiMm: (int)response.Values.QuickReleaseCentiMm,
                PressMinCentiMm: (int)response.PressMinCentiMm,
                PressMaxCentiMm: (int)response.PressMaxCentiMm,
                RtMinCentiMm: (int)response.RtMinCentiMm,
                RtMaxCentiMm: (int)response.RtMaxCentiMm,
                StepMicrometres: (int)response.StepMicrometres,
                DefaultPressCentiMm: (int)response.Defaults.PressCentiMm,
                DefaultReleaseCentiMm: (int)response.Defaults.ReleaseCentiMm,
                DefaultRapidTrigger: response.Defaults.RapidTrigger,
                DefaultQuickPressCentiMm: (int)response.Defaults.QuickPressCentiMm,
                DefaultQuickReleaseCentiMm: (int)response.Defaults.QuickReleaseCentiMm)
            {
                PerKeyValues = perKey,
            };
        }
        catch (RpcException ex)
        {
            NotifyError?.Invoke(this, $"GetKeyActuation RPC failed: {ex.Status.Detail}");
            return null;
        }
        catch (Exception ex)
        {
            NotifyError?.Invoke(this, $"GetKeyActuation failed: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> SetKeyActuationAsync(
        int vendorId, int productId,
        int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm)
    {
        if (_client == null)
            return false;
        try
        {
            var response = await _client.SetKeyActuationAsync(new SetKeyActuationRequest
            {
                VendorId = vendorId,
                ProductId = productId,
                Values = new KeyActuationValues
                {
                    PressCentiMm = (uint)pressCentiMm,
                    ReleaseCentiMm = (uint)releaseCentiMm,
                    RapidTrigger = rapidTrigger,
                    QuickPressCentiMm = (uint)quickPressCentiMm,
                    QuickReleaseCentiMm = (uint)quickReleaseCentiMm,
                },
            }, deadline: DateTime.UtcNow.AddSeconds(30));
            // A clean RPC carrying "the device refused" is just as much an error as a failed RPC —
            // with the authoritative read-back verify this is the branch an unsupported 0x39
            // actually takes, and it must not stay log-invisible.
            if (!response.Success)
                NotifyError?.Invoke(this, "SetKeyActuation failed: the device did not accept the actuation write");
            return response.Success;
        }
        catch (RpcException ex)
        {
            NotifyError?.Invoke(this, $"SetKeyActuation RPC failed: {ex.Status.Detail}");
            return false;
        }
        catch (Exception ex)
        {
            NotifyError?.Invoke(this, $"SetKeyActuation failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> SetPerKeyActuationAsync(
        int vendorId, int productId, IReadOnlyCollection<int> slots,
        int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm)
    {
        if (_client == null || slots.Count == 0)
            return false;
        try
        {
            var request = new SetPerKeyActuationRequest
            {
                VendorId = vendorId,
                ProductId = productId,
                Values = new KeyActuationValues
                {
                    PressCentiMm = (uint)pressCentiMm,
                    ReleaseCentiMm = (uint)releaseCentiMm,
                    RapidTrigger = rapidTrigger,
                    QuickPressCentiMm = (uint)quickPressCentiMm,
                    QuickReleaseCentiMm = (uint)quickReleaseCentiMm,
                },
            };
            request.Slots.AddRange(slots);

            var response = await _client.SetPerKeyActuationAsync(request, deadline: DateTime.UtcNow.AddSeconds(30));
            // Same rule as the global write: a clean RPC carrying "the device refused" must not
            // stay log-invisible — the read-back verify is what rejects an unsupported write.
            if (!response.Success)
                NotifyError?.Invoke(this, "SetPerKeyActuation failed: the device did not accept the actuation write");
            return response.Success;
        }
        catch (RpcException ex)
        {
            NotifyError?.Invoke(this, $"SetPerKeyActuation RPC failed: {ex.Status.Detail}");
            return false;
        }
        catch (Exception ex)
        {
            NotifyError?.Invoke(this, $"SetPerKeyActuation failed: {ex.Message}");
            return false;
        }
    }

    public async Task<ExtendedKeysInfo?> GetExtendedKeysAsync(int vendorId, int productId)
    {
        if (_client == null)
            return null;
        try
        {
            var response = await _client.GetExtendedKeysAsync(new GetExtendedKeysRequest
            {
                VendorId = vendorId,
                ProductId = productId,
            }, deadline: DateTime.UtcNow.AddSeconds(20));
            if (!response.Success) return null;
            if (!response.Supported)
                return new ExtendedKeysInfo(false, [], 0, 0, []);

            var entries = new List<ExtendedKeyRecord>(response.Entries.Count);
            foreach (var entry in response.Entries)
                entries.Add(FromProto(entry));

            return new ExtendedKeysInfo(
                Supported: true,
                Entries: entries,
                AreaBytes: response.AreaBytes,
                FreeBytes: response.FreeBytes,
                BlockedSlots: [.. response.BlockedSlots])
            {
                Repaired = response.Repaired,
            };
        }
        catch (RpcException ex)
        {
            // UNIMPLEMENTED from an older DeviceHost lands here too: null keeps the section in its
            // loading state rather than hiding it or, worse, offering an editor that cannot write.
            NotifyError?.Invoke(this, $"GetExtendedKeys RPC failed: {ex.Status.Detail}");
            return null;
        }
        catch (Exception ex)
        {
            NotifyError?.Invoke(this, $"GetExtendedKeys failed: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> SetExtendedKeysAsync(int vendorId, int productId, IReadOnlyList<ExtendedKeyRecord> entries)
    {
        if (_client == null)
            return false;
        try
        {
            var request = new SetExtendedKeysRequest
            {
                VendorId = vendorId,
                ProductId = productId,
            };
            foreach (var entry in entries)
                request.Entries.Add(ToProto(entry));

            var response = await _client.SetExtendedKeysAsync(request, deadline: DateTime.UtcNow.AddSeconds(30));
            // A clean RPC carrying "the device refused" is as much an error as a failed RPC — the
            // host's refusal (blocked slot, overflow) or a verify mismatch must not stay log-invisible.
            if (!response.Success)
                NotifyError?.Invoke(this, "SetExtendedKeys failed: the device did not accept the extended-keys write");
            return response.Success;
        }
        catch (RpcException ex)
        {
            NotifyError?.Invoke(this, $"SetExtendedKeys RPC failed: {ex.Status.Detail}");
            return false;
        }
        catch (Exception ex)
        {
            NotifyError?.Invoke(this, $"SetExtendedKeys failed: {ex.Message}");
            return false;
        }
    }

    private static ExtendedKeyRecord FromProto(ExtendedKey entry) => new(
        entry.Mode, entry.Slot1, entry.Slot2, entry.Priority, entry.TimeMs,
        FromProto(entry.HoldKey), FromProto(entry.TapKey));

    private static ExtendedKeyCodeRecord FromProto(ExtendedKeyCode? code) =>
        code is null ? ExtendedKeyCodeRecord.None : new ExtendedKeyCodeRecord(code.TypeByte, code.Code1, code.Code2);

    private static ExtendedKey ToProto(ExtendedKeyRecord entry) => new()
    {
        Mode = entry.Mode,
        Slot1 = entry.Slot1,
        Slot2 = entry.Slot2,
        Priority = entry.Priority,
        TimeMs = entry.TimeMs,
        HoldKey = new ExtendedKeyCode { TypeByte = entry.HoldKey.TypeByte, Code1 = entry.HoldKey.Code1, Code2 = entry.HoldKey.Code2 },
        TapKey = new ExtendedKeyCode { TypeByte = entry.TapKey.TypeByte, Code1 = entry.TapKey.Code1, Code2 = entry.TapKey.Code2 },
    };


    public bool CheckIfAllDevicesAreDisconnected()
    {
        //TODO: implement check for all devices are disconnected
        return false;
    }

    public bool TryGetLastDpiStage(int vendorId, int productId, string deviceKey, out int stageIndex)
        => _lastDpiStage.TryGetValue((vendorId, productId, deviceKey ?? string.Empty), out stageIndex);

    public bool TryGetLastReportRate(int vendorId, int productId, string deviceKey, out int hz)
        => _lastReportRate.TryGetValue((vendorId, productId, deviceKey ?? string.Empty), out hz);

    public async Task StartCommunication()
    {
        if(IsRunning)
        {
            return;
        }

        IsRunning = true;

        try
        {
            if (_ownsChannel)
            {
                _channel?.Dispose();
                _channel = DeviceHostConnection.CreateChannel();
                _client = new Device.DeviceClient(_channel);
            }

            _reconnecting = true;
            bool success = false;

            while (_reconnecting && !success)
            {
                success = await RestartCommunication();

                if (!success && _reconnecting)
                {
                    try
                    {
                        await RetryDelayAsync(_cts?.Token ?? CancellationToken.None);
                    }
                    catch (OperationCanceledException)
                    {
                        // StopCommunication cancelled the wait; the loop exits on _reconnecting.
                    }
                }
            }
        }
        catch (Exception ex)
        {
            NotifyError?.Invoke(this, ex.Message);
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task<bool> RestartCommunication()
    {
        if (_cts != null) 
        {
            _cts.Cancel();
            _cts.Dispose();
        }

        if(_tcs != null)
        {
            _tcs.TrySetResult();
            _tcs = null;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _tcs = new TaskCompletionSource();
        var completionTask = _tcs.Task;

        var serverStreaming = AwaitServerStreaming(token);
        var resultingTask = await Task.WhenAny(completionTask, serverStreaming);

        if (resultingTask == completionTask)
        {
            return true;
        }
        else 
        {
            return false;
        }
    }

    public void StopCommunication() 
    {
        _reconnecting = false;
        _cts?.Cancel();
        _tcs?.TrySetResult();
    }

    public event EventHandler<SubscribeOnDevicesResponse>? DeviceFound;
    public event EventHandler<DpiStageChange>? DpiStageChanged;

    /// <summary>
    /// Raised when the DeviceHost sees a device's battery reading change, or stop being available.
    /// </summary>
    /// <remarks>
    /// The App otherwise learns about battery only on its own 60 s poll, so a mouse switched back on
    /// took up to a minute to show a level the host already had. TAIPAN pushes a battery report on
    /// link-up, which the host logs immediately — this carries that straight through.
    /// </remarks>
    public event EventHandler<BatteryChange>? BatteryChanged;
    public event EventHandler<ReportRateChange>? ReportRateChanged;
    public event EventHandler<string>? NotifyError;

    /// <summary>
    /// Raised when the DeviceHost starts or stops answering, on the TRANSITION only.
    /// </summary>
    /// <remarks>
    /// False means device state is no longer being maintained — the service was stopped, crashed or
    /// uninstalled — and anything the App is showing about live devices is now only a memory of the
    /// last push. True means a subscription was re-established; the host replies to a fresh
    /// subscribe with the current USB and Bluetooth device lists, so cards repopulate on their own
    /// and this carries no payload.
    ///
    /// Transition-only, because the reconnect loop retries every 3 s for as long as the host is
    /// away: a level signal would re-raise indefinitely.
    /// </remarks>
    public event EventHandler<bool>? HostAvailabilityChanged;

    private void SetHostAvailable(bool available)
    {
        var updated = available ? 1 : 0;
        if (Interlocked.Exchange(ref _hostAvailable, updated) == updated) return;
        HostAvailabilityChanged?.Invoke(this, available);
    }

    private async Task AwaitServerStreaming(CancellationToken cancellationToken)
    {
        try
        {
            if(_client == null)
            {
                throw new Exception("Grpc client is not initialized.");
            }

            using var call = _client.SubscribeOnDevices();

            try
            {
                await call.RequestStream.WriteAsync(new SubscribeOnDevicesRequest
                {
                    RequestId = 0,
                });

                // The subscribe is through, so the host is there. The device lists it sends in
                // reply are what bring the cards back, which is why this carries no payload.
                SetHostAvailable(true);

                // Returns when the stream ends, faults, or the attempt token is cancelled --
                // exactly the signal RestartCommunication's WhenAny needs to schedule a retry.
                await ReadServerResponses(call, cancellationToken);
            }
            finally
            {
                // Best-effort close of the client half: after a host death this throws and
                // must not mask the real outcome; on a deliberate stop it ends the call politely.
                try
                {
                    await call.RequestStream.CompleteAsync();
                }
                catch
                {
                    // The call is already broken or cancelled — nothing left to close.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Deliberate stop (StopCommunication or the next attempt) — not an error.
        }
        catch (RpcException rpcEx) when (cancellationToken.IsCancellationRequested && rpcEx.StatusCode == StatusCode.Cancelled)
        {
            // Deliberate stop surfacing through the gRPC call — not an error.
        }
        catch (RpcException rpcEx)
        {
            NotifyError?.Invoke(this, rpcEx.Message);
        }
        catch (Exception ex)
        {
            NotifyError?.Invoke(this, ex.Message);
        }
        finally
        {
            // The attempt is over however it ended, so nothing is maintaining device state now --
            // unless this attempt was retired on purpose, in which case it has nothing to say.
            //
            // The token is what decides that, and _reconnecting alone is NOT enough. This method is
            // ABANDONED rather than awaited: when the completion task wins RestartCommunication's
            // Task.WhenAny, the streaming task keeps unwinding on its own. _reconnecting is shared
            // and the NEXT StartCommunication sets it back to true, so a retired attempt could reach
            // this line, read true, and report a host loss that never happened. Home load/unload
            // calls Stop then Start on every navigation, so that was an ordinary trip to a device's
            // details page blanking every card (review R2 B1).
            //
            // The attempt's own token is cancelled by BOTH StopCommunication and the next
            // RestartCommunication, so a retired attempt is silent either way, while a genuine
            // stream fault -- where nothing cancelled anything -- still reports.
            if (_reconnecting && !cancellationToken.IsCancellationRequested)
                SetHostAvailable(false);
        }
    }

    async Task ReadServerResponses(AsyncDuplexStreamingCall<SubscribeOnDevicesRequest, SubscribeOnDevicesResponse> call, CancellationToken token)
    {
        while (await call.ResponseStream.MoveNext(token))
        {
            var current = call.ResponseStream.Current;

            if (current != null)
            {
                if (current.DpiStageChange != null)
                {
                    var change = current.DpiStageChange;
                    _lastDpiStage[(change.VendorId, change.ProductId, change.DeviceKey ?? string.Empty)] = change.StageIndex;
                    DpiStageChanged?.Invoke(this, change);
                }
                else if (current.BatteryChange != null)
                {
                    BatteryChanged?.Invoke(this, current.BatteryChange);
                }
                else if (current.ReportRateChange != null)
                {
                    var change = current.ReportRateChange;
                    _lastReportRate[(change.VendorId, change.ProductId, change.DeviceKey ?? string.Empty)] = change.Hz;
                    ReportRateChanged?.Invoke(this, change);
                }
                else
                    DeviceFound?.Invoke(this, current);
            }
        }
    }

    public void Dispose() 
    {
        _cts?.Dispose();
        _channel?.Dispose();
    }
}
