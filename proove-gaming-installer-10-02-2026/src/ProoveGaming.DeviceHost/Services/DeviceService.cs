using Grpc.Core;
using ProoveGaming.DeviceHost.Helpers;
using ProoveGaming.GrpcDependencies.Device;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.CustomEvents;
using ProoveGaming.Infrastructure.Interfaces;
using ProoveGaming.Infrastructure.Services;
// ButtonBinding and MouseButtonAction exist in both the gRPC contract and Infrastructure;
// alias both sides to disambiguate.
using InfraButtonBinding = ProoveGaming.Infrastructure.Classes.ButtonBinding;
using InfraExtendedKeyCode = ProoveGaming.Infrastructure.Classes.ExtendedKeyCode;
using ProtoExtendedKeyCode = ProoveGaming.GrpcDependencies.Device.ExtendedKeyCode;

namespace ProoveGaming.DeviceHost.Services;

public class DeviceService(ILogger<DeviceService> logger,
	IHidDeviceDiscoveryService hidDeviceDiscoveryService,
	IBluetoothDeviceDiscoveryService bluetoothDeviceDiscoveryService,
	IDeviceRgbServiceFactory deviceRgbServiceFactory,
	DpiStageMonitor dpiStageMonitor,
	TaipanStateMonitor taipanStateMonitor,
	CosmoStateMonitor cosmoStateMonitor,
	SmartInfiniteBatteryMonitor smartInfiniteBatteryMonitor,
	IDeviceKeyMappingServiceFactory deviceKeyMappingServiceFactory,
	IDeviceKeyboardMacroServiceFactory deviceKeyboardMacroServiceFactory,
	IDeviceActuationServiceFactory deviceActuationServiceFactory,
	IDeviceExtendedKeysServiceFactory deviceExtendedKeysServiceFactory,
	HidDeviceMapRegistry registry) : Device.DeviceBase
{
	private readonly ILogger<DeviceService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
	private readonly IHidDeviceDiscoveryService _hidDeviceDiscoveryService = hidDeviceDiscoveryService ?? throw new ArgumentNullException(nameof(hidDeviceDiscoveryService));
	private readonly IBluetoothDeviceDiscoveryService _bluetoothDeviceDiscoveryService = bluetoothDeviceDiscoveryService ?? throw new ArgumentNullException(nameof(bluetoothDeviceDiscoveryService));
	private readonly IDeviceRgbServiceFactory _deviceRgbServiceFactory = deviceRgbServiceFactory ?? throw new ArgumentNullException(nameof(deviceRgbServiceFactory));
	private readonly DpiStageMonitor _dpiStageMonitor = dpiStageMonitor ?? throw new ArgumentNullException(nameof(dpiStageMonitor));
	private readonly TaipanStateMonitor _taipanStateMonitor = taipanStateMonitor ?? throw new ArgumentNullException(nameof(taipanStateMonitor));
	private readonly CosmoStateMonitor _cosmoStateMonitor = cosmoStateMonitor ?? throw new ArgumentNullException(nameof(cosmoStateMonitor));
	private readonly SmartInfiniteBatteryMonitor _smartInfiniteBatteryMonitor = smartInfiniteBatteryMonitor ?? throw new ArgumentNullException(nameof(smartInfiniteBatteryMonitor));
	private readonly IDeviceKeyMappingServiceFactory _deviceKeyMappingServiceFactory = deviceKeyMappingServiceFactory ?? throw new ArgumentNullException(nameof(deviceKeyMappingServiceFactory));
	private readonly IDeviceKeyboardMacroServiceFactory _deviceKeyboardMacroServiceFactory = deviceKeyboardMacroServiceFactory ?? throw new ArgumentNullException(nameof(deviceKeyboardMacroServiceFactory));
	private readonly IDeviceActuationServiceFactory _deviceActuationServiceFactory = deviceActuationServiceFactory ?? throw new ArgumentNullException(nameof(deviceActuationServiceFactory));
	private readonly IDeviceExtendedKeysServiceFactory _deviceExtendedKeysServiceFactory = deviceExtendedKeysServiceFactory ?? throw new ArgumentNullException(nameof(deviceExtendedKeysServiceFactory));
	private readonly HidDeviceMapRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
	private IServerStreamWriter<SubscribeOnDevicesResponse>? _serverStreamWriter;
	// gRPC stream writers allow only one in-flight write; serialise all pushes.
	private readonly SemaphoreSlim _streamWriteLock = new(1, 1);

	#region Communication Methods

	public override Task<GetBatteryLevelResponse> GetBatteryLevel(GetBatteryLevelRequest request, ServerCallContext context)
	{
		try
		{
			// A transport whose catalog row declares no usable battery is answered as "no battery"
			// before any source is consulted. Enforced HERE rather than only in the App so the flag
			// holds for every client, and so a reading the catalog calls unusable can never leave
			// this process.
			//
			// TAIPAN's cable is the case: there the mouse reports charge VOLTAGE, which rises while
			// charging and pins the percentage at 100 % (measured raw 1002 on the cable against
			// 979-982 at rest minutes earlier). Its wired transport therefore sets
			// SupportsBattery: false — see docs/mouse/taipan/protocol.md.
			var catalogEntry = _registry.FindCatalogEntryByPidVid(
				request.VendorId.ToString(), request.ProductId.ToString());

			if (catalogEntry is { BatteryUnsupported: true })
			{
				return Task.FromResult(new GetBatteryLevelResponse { Success = false });
			}

			// A client asking for a level is the signal that someone is looking at one, so take the
			// opportunity to refresh it. Throttled inside the monitor and a no-op for anything that
			// is not a Telink mouse.
			//
			// This answer still comes from the CACHE — the request is fire-and-forget and its reply
			// arrives milliseconds later on the reader thread, which pushes it to the client through
			// BatteryChanged. Without it the mouse-absent case could not be noticed promptly: the
			// dongle only reveals that no mouse is behind it in response to a request, so a mouse
			// switched off kept its last percentage on screen until the periodic five-minute pass.
			_taipanStateMonitor.RequestBatteryRefresh(request.VendorId);

			// Telink mice (TAIPAN) can't be queried for battery — the dongle doesn't relay the
			// query, and the mouse pushes its level on link-up instead. The monitor caches those
			// pushes; a cached reading answers here without touching the device.
			if (_taipanStateMonitor.TryGetBattery(request.VendorId, request.ProductId, request.DeviceKey, out var pushed))
			{
				return Task.FromResult(new GetBatteryLevelResponse
				{
					Success = true,
					BatteryValue = pushed.Percent,
					IsCharging = pushed.UsbConnected,
				});
			}

			// The Cosmo headset's dongle pushes nothing, but it DOES answer a GET_REPORT
			// control-transfer read on its vendor report — so its battery is fetched here, on
			// demand, with no write to the device. A headset that is switched off reports no
			// percentage (the dongle keeps serving a stale one), which correctly reads as unknown.
			// Answered from CosmoStateMonitor's cache, NOT by reading the device here: this handler is
			// synchronous, and TryReadCosmoState performs a blocking HID control transfer that would
			// hold a gRPC thread-pool thread for the USB timeout if the dongle stopped responding.
			// The monitor already polls this exact report every two seconds.
			if (CosmoStateReader.Matches(request.VendorId, request.ProductId)
				&& _cosmoStateMonitor.TryGetLastReport(request.DeviceKey) is { } cosmo)
			{
				return Task.FromResult(new GetBatteryLevelResponse
				{
					Success = cosmo.BatteryPercent is not null,
					BatteryValue = cosmo.BatteryPercent ?? 0,
					IsCharging = false,
				});
			}

			// Smart Infinite dongle keyboards (Rampage) are already polled every few seconds by
			// SmartInfiniteBatteryMonitor, so a fresh cached reading answers here without a
			// second HID exchange on top of the monitor's cadence — the same cache-answering
			// shape as the Taipan and Cosmo branches above. A stale cache (device just arrived,
			// several gate-yielded ticks) falls through to the live read below.
			if (_smartInfiniteBatteryMonitor.TryGetBattery(request.VendorId, request.ProductId,
				SmartInfiniteBatteryMonitor.CacheFreshness, out var monitored))
			{
				return Task.FromResult(new GetBatteryLevelResponse
				{
					Success = true,
					BatteryValue = monitored.Percent,
					IsCharging = monitored.IsCharging,
				});
			}

			var (batteryValue, isCharging) = _hidDeviceDiscoveryService.TryGetBatteryLevel(request.VendorId, request.ProductId);
			return Task.FromResult(new GetBatteryLevelResponse
			{
				Success = batteryValue >= 0,
				BatteryValue = batteryValue >= 0 ? batteryValue : 0,
				IsCharging = isCharging,
			});
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception in {Operation}", nameof(GetBatteryLevel));
			return Task.FromResult(new GetBatteryLevelResponse { Success = false });
		}
	}

	// Catalog lookup with no live device behind it: the App's sign-in pull hydrates offline cards
	// for server-rostered models this machine has never seen (docs/plans/add-api-v21-device-roster).
	public override Task<GetDeviceCatalogEntryResponse> GetDeviceCatalogEntry(GetDeviceCatalogEntryRequest request, ServerCallContext context)
	{
		try
		{
			var entry = _registry.FindCatalogEntryByPidVid(
				request.VendorId.ToString(), request.ProductId.ToString());
			var response = new GetDeviceCatalogEntryResponse { Found = entry != null };
			if (entry != null)
				response.CatalogEntry = entry;
			return Task.FromResult(response);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception in {Operation}", nameof(GetDeviceCatalogEntry));
			return Task.FromResult(new GetDeviceCatalogEntryResponse { Found = false });
		}
	}

	// Finds the Cosmo interface belonging to this physical unit and reads its vendor status report,
	// which carries whether the HEADSET (not the dongle) is switched on. Matched by Container ID
	// like every other per-device lookup, so a second headset cannot answer for the first; an empty
	// key falls back to the first match, the same single-device behaviour used elsewhere here.
	//
	// Not wired to battery: the payload carries no battery level (see CosmoStateReport). Kept
	// because the power-state field is confirmed and is what the connected indicator will use.
	internal CosmoStateReport? TryReadCosmoState(string deviceKey)
	{
		try
		{
			var candidates = HidSharp.DeviceList.Local
				.GetHidDevices(CosmoStateReader.VendorId, CosmoStateReader.ProductId);

			var device = string.IsNullOrEmpty(deviceKey)
				? candidates.FirstOrDefault()
				: candidates.FirstOrDefault(d => DeviceContainerId.TryGet(d.DevicePath) == deviceKey);

			return device?.DevicePath is { } path ? CosmoStateReader.TryRead(path) : null;
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "[Cosmo] could not read the vendor status report");
			return null;
		}
	}

	public override async Task<SetDeviceLedColorResponse> SetDeviceLedColor(SetDeviceLedColorRequest request, ServerCallContext context)
	{
		try
		{
			var device = _deviceRgbServiceFactory.CreateForDevice(request.VendorId, request.ProductId, request.DeviceKey);
			if (device == null)
			{
				_logger.LogWarning("[LED] no RGB-capable device for VID={VID} PID={PID} deviceType={Type}",
					request.VendorId, request.ProductId, request.DeviceType);
				return new SetDeviceLedColorResponse { Success = false };
			}

			var effect = (byte)Math.Clamp(request.Effect, 0, 255);
			var brightness = (byte)Math.Clamp(request.Brightness, 0, 255);
			var speed = (byte)Math.Clamp(request.Speed, 0, 255);

			var ok = await device.SetLedColorAsync(
				(byte)request.R, (byte)request.G, (byte)request.B,
				effect, brightness, speed);

			return new SetDeviceLedColorResponse { Success = ok };
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception in {Operation}", nameof(SetDeviceLedColor));
			return new SetDeviceLedColorResponse { Success = false };
		}
	}

	public override async Task<SetDpiStagesResponse> SetDpiStages(SetDpiStagesRequest request, ServerCallContext context)
	{
		try
		{
			var device = _deviceRgbServiceFactory.CreateForDevice(request.VendorId, request.ProductId, request.DeviceKey) as IMouseRgbService;
			if (device == null)
			{
				_logger.LogWarning("[DPI] no mouse device for VID={VID} PID={PID}", request.VendorId, request.ProductId);
				return new SetDpiStagesResponse { Success = false };
			}

			var stages = request.Stages.Select(s => new DpiStageParams(
				(ushort)Math.Clamp(s.DpiValue, 0, 65535),
				(byte)Math.Clamp(s.R, 0, 255),
				(byte)Math.Clamp(s.G, 0, 255),
				(byte)Math.Clamp(s.B, 0, 255))).ToArray();

			var ok = await device.SetDpiStagesAsync(stages);
			return new SetDpiStagesResponse { Success = ok };
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception in {Operation}", nameof(SetDpiStages));
			return new SetDpiStagesResponse { Success = false };
		}
	}

	public override async Task<SetActiveDpiStageResponse> SetActiveDpiStage(SetActiveDpiStageRequest request, ServerCallContext context)
	{
		try
		{
			var device = _deviceRgbServiceFactory.CreateForDevice(request.VendorId, request.ProductId, request.DeviceKey) as IMouseRgbService;
			if (device == null)
			{
				_logger.LogWarning("[DPI] no mouse device for VID={VID} PID={PID}", request.VendorId, request.ProductId);
				return new SetActiveDpiStageResponse { Success = false };
			}

			var ok = await device.SetActiveDpiStageAsync((byte)Math.Clamp(request.StageIndex, 0, 7));
			return new SetActiveDpiStageResponse { Success = ok };
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception in {Operation}", nameof(SetActiveDpiStage));
			return new SetActiveDpiStageResponse { Success = false };
		}
	}

	public override async Task<SetReportRateResponse> SetReportRate(SetReportRateRequest request, ServerCallContext context)
	{
		try
		{
			var device = _deviceRgbServiceFactory.CreateForDevice(request.VendorId, request.ProductId, request.DeviceKey) as IMouseRgbService;
			if (device == null)
			{
				_logger.LogWarning("[ReportRate] no mouse device for VID={VID} PID={PID}", request.VendorId, request.ProductId);
				return new SetReportRateResponse { Success = false };
			}

			var ok = await device.SetReportRateAsync(request.Hz);
			return new SetReportRateResponse { Success = ok };
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception in {Operation}", nameof(SetReportRate));
			return new SetReportRateResponse { Success = false };
		}
	}

	public override async Task<SetButtonBindingsResponse> SetButtonBindings(SetButtonBindingsRequest request, ServerCallContext context)
	{
		try
		{
			var bindings = ProtoInput.MapBindings(request.Bindings, out var inputError);
			if (bindings == null)
			{
				_logger.LogWarning("[Buttons] rejected request for VID={VID} PID={PID}: {Reason}", request.VendorId, request.ProductId, inputError);
				return new SetButtonBindingsResponse { Success = false };
			}

			var device = _deviceRgbServiceFactory.CreateForDevice(request.VendorId, request.ProductId, request.DeviceKey) as IMouseRgbService;
			if (device == null)
			{
				_logger.LogWarning("[Buttons] no mouse device for VID={VID} PID={PID}", request.VendorId, request.ProductId);
				return new SetButtonBindingsResponse { Success = false };
			}

			// Write the device only. The App (SQLite) is the source of truth for bindings and
			// persists them itself; the host no longer keeps its own copy.
			var ok = await device.SetButtonBindingsAsync(bindings);
			if (!ok)
				_logger.LogWarning("[Buttons] device write failed for VID={VID} PID={PID}", request.VendorId, request.ProductId);

			return new SetButtonBindingsResponse { Success = ok };
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception in {Operation}", nameof(SetButtonBindings));
			return new SetButtonBindingsResponse { Success = false };
		}
	}

	public override async Task<SetMacrosResponse> SetMacros(SetMacrosRequest request, ServerCallContext context)
	{
		try
		{
			var service = _deviceKeyboardMacroServiceFactory.CreateForDevice(request.VendorId, request.ProductId);
			if (service == null)
			{
				_logger.LogWarning("[MACRO] no macro service for VID={VID} PID={PID}",
					request.VendorId, request.ProductId);
				return new SetMacrosResponse { Success = false };
			}

			var entries = ProtoInput.MapLibraryEntries(request.Entries, out var inputError);
			if (entries == null)
			{
				_logger.LogWarning("[MACRO] rejected request for VID={VID} PID={PID}: {Reason}",
					request.VendorId, request.ProductId, inputError);
				return new SetMacrosResponse { Success = false };
			}

			var ok = await service.UploadMacrosAsync(entries, context.CancellationToken);
			return new SetMacrosResponse { Success = ok };
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception in {Operation}", nameof(SetMacros));
			return new SetMacrosResponse { Success = false };
		}
	}

	// Applies a mouse's full desired configuration in one coordinated device write. Each aspect is
	// optional (unset message / empty list / absent scalar → not touched). Mouse-only: keyboards use
	// the per-aspect RGB / key-mapping paths.
	public override async Task<ApplyDeviceConfigurationResponse> ApplyDeviceConfiguration(ApplyDeviceConfigurationRequest request, ServerCallContext context)
	{
		try
		{
			List<InfraButtonBinding>? buttons = null;
			if (request.Bindings.Count > 0 && (buttons = ProtoInput.MapBindings(request.Bindings, out var inputError)) == null)
			{
				_logger.LogWarning("[Apply] rejected request for VID={VID} PID={PID}: {Reason}", request.VendorId, request.ProductId, inputError);
				return new ApplyDeviceConfigurationResponse { Success = false };
			}

			var device = _deviceRgbServiceFactory.CreateForDevice(request.VendorId, request.ProductId, request.DeviceKey) as IMouseRgbService;
			if (device == null)
			{
				_logger.LogWarning("[Apply] no mouse device for VID={VID} PID={PID}", request.VendorId, request.ProductId);
				return new ApplyDeviceConfigurationResponse { Success = false };
			}

			var config = new DeviceConfiguration
			{
				Led = request.Led is { } led
					? new LedConfig(
						(byte)Math.Clamp(led.R, 0, 255), (byte)Math.Clamp(led.G, 0, 255), (byte)Math.Clamp(led.B, 0, 255),
						(byte)led.Effect, (byte)Math.Clamp(led.Brightness, 0, 255), (byte)Math.Clamp(led.Speed, 0, 255))
					: null,
				Buttons = buttons,
				Settings = request.MouseSettings is { } ms
					? new MouseSettings((byte)Math.Clamp(ms.BtnDebounceMs, 0, 255), (byte)Math.Clamp(ms.LiftOffMm, 0, 255))
					: null,
				DpiStages = request.DpiStages.Count > 0
					? request.DpiStages.Select(s => new DpiStageParams(
						(ushort)Math.Clamp(s.DpiValue, 0, 65535),
						(byte)Math.Clamp(s.R, 0, 255), (byte)Math.Clamp(s.G, 0, 255), (byte)Math.Clamp(s.B, 0, 255))).ToArray()
					: null,
				ActiveStage = request.HasActiveStage ? (byte)Math.Clamp(request.ActiveStage, 0, 255) : null,
				ReportRateHz = request.HasReportRateHz ? request.ReportRateHz : null,
			};

			var ok = await device.ApplyConfigurationAsync(config);
			if (!ok)
				_logger.LogWarning("[Apply] device write failed for VID={VID} PID={PID}", request.VendorId, request.ProductId);
			return new ApplyDeviceConfigurationResponse { Success = ok };
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception in {Operation}", nameof(ApplyDeviceConfiguration));
			return new ApplyDeviceConfigurationResponse { Success = false };
		}
	}

	public override async Task<ProbeKeyMatrixResponse> ProbeKeyMatrix(ProbeKeyMatrixRequest request, ServerCallContext context)
	{
		try
		{
			var service = _deviceKeyMappingServiceFactory.CreateForDevice(request.VendorId, request.ProductId);
			if (service == null)
			{
				return new ProbeKeyMatrixResponse { Success = false };
			}

			var maxSlot = request.MaxSlot <= 0 ? 128 : request.MaxSlot;
			var defaults = await service.ProbeDefaultMatrixAsync(maxSlot, context.CancellationToken);
			var overrides = await service.ProbeUserOverridesAsync(maxSlot, context.CancellationToken);
			if (defaults == null)
			{
				return new ProbeKeyMatrixResponse { Success = false };
			}

			var response = new ProbeKeyMatrixResponse { Success = true };
			foreach (var kv in defaults)
				response.ScancodeToSlot[kv.Key] = kv.Value;
			if (overrides != null)
			{
				foreach (var kv in overrides)
					response.UserOverrides[kv.Key] = kv.Value;
			}
			return response;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception in {Operation}", nameof(ProbeKeyMatrix));
			return new ProbeKeyMatrixResponse { Success = false };
		}
	}

	public override async Task<ReadKeyMatrixResponse> ReadKeyMatrix(ReadKeyMatrixRequest request, ServerCallContext context)
	{
		try
		{
			var service = _deviceKeyMappingServiceFactory.CreateForDevice(request.VendorId, request.ProductId);
			if (service == null)
			{
				return new ReadKeyMatrixResponse { Success = false };
			}

			var maxSlot = request.MaxSlot <= 0 ? 128 : request.MaxSlot;
			var records = await service.ReadKeyMatrixAsync(maxSlot, context.CancellationToken);
			if (records == null)
			{
				return new ReadKeyMatrixResponse { Success = false };
			}

			var response = new ReadKeyMatrixResponse { Success = true };
			foreach (var kv in records)
			{
				response.Records[kv.Key] = new KeyMatrixRecordProto
				{
					Type = kv.Value.Type,
					Code1 = kv.Value.CodeLow,
					Code2 = kv.Value.CodeHigh,
				};
			}
			return response;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception in {Operation}", nameof(ReadKeyMatrix));
			return new ReadKeyMatrixResponse { Success = false };
		}
	}

	public override async Task<SetKeyMappingResponse> SetKeyMapping(SetKeyMappingRequest request, ServerCallContext context)
	{
		try
		{
			var service = _deviceKeyMappingServiceFactory.CreateForDevice(request.VendorId, request.ProductId);
			if (service == null)
			{
				_logger.LogWarning("[KEY] no key-mapping service for VID={VID} PID={PID}",
					request.VendorId, request.ProductId);
				return new SetKeyMappingResponse { Success = false };
			}

			var matrixIndex = (byte)Math.Clamp(request.MatrixIndex, 0, 255);
			var typeByte = (byte)Math.Clamp(request.TypeByte, 0, 255);
			var code1 = (byte)Math.Clamp(request.Code1, 0, 255);
			var code2 = (byte)Math.Clamp(request.Code2, 0, 255);

			var ok = await service.SetKeyMappingAsync(matrixIndex, typeByte, code1, code2, context.CancellationToken);
			return new SetKeyMappingResponse { Success = ok };
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception in {Operation}", nameof(SetKeyMapping));
			return new SetKeyMappingResponse { Success = false };
		}
	}

	public override async Task<GetKeyActuationResponse> GetKeyActuation(GetKeyActuationRequest request, ServerCallContext context)
	{
		try
		{
			var service = _deviceActuationServiceFactory.CreateForDevice(request.VendorId, request.ProductId);
			if (service == null)
			{
				return new GetKeyActuationResponse { Success = true, Supported = false };
			}

			var state = await service.ReadAsync(context.CancellationToken);
			if (state == null)
			{
				// The probe failed or the board has no magnetic geometry — per the safety gate this
				// is "unsupported", and the App must never call SetKeyActuation for it.
				return new GetKeyActuationResponse { Success = true, Supported = false };
			}

			var response = new GetKeyActuationResponse
			{
				Success = true,
				Supported = true,
				Values = ToProto(state.Values),
				PressMinCentiMm = (uint)state.Limits.PressMinCentiMm,
				PressMaxCentiMm = (uint)state.Limits.PressMaxCentiMm,
				RtMinCentiMm = (uint)state.Limits.RtMinCentiMm,
				RtMaxCentiMm = (uint)state.Limits.RtMaxCentiMm,
				StepMicrometres = (uint)state.Limits.StepMicrometres,
				Defaults = ToProto(state.Limits.Defaults),
			};
			foreach (var (slot, values) in state.PerKeyValues)
				response.PerKeyValues[slot] = ToProto(values);
			return response;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception in {Operation}", nameof(GetKeyActuation));
			return new GetKeyActuationResponse { Success = false };
		}
	}

	public override async Task<SetKeyActuationResponse> SetKeyActuation(SetKeyActuationRequest request, ServerCallContext context)
	{
		try
		{
			var service = _deviceActuationServiceFactory.CreateForDevice(request.VendorId, request.ProductId);
			if (service == null || request.Values == null)
			{
				_logger.LogWarning("[ACT] no actuation service for VID={VID} PID={PID}",
					request.VendorId, request.ProductId);
				return new SetKeyActuationResponse { Success = false };
			}

			var ok = await service.WriteAsync(new KeyboardActuationValues(
				PressCentiMm: (int)request.Values.PressCentiMm,
				ReleaseCentiMm: (int)request.Values.ReleaseCentiMm,
				RapidTrigger: request.Values.RapidTrigger,
				QuickPressCentiMm: (int)request.Values.QuickPressCentiMm,
				QuickReleaseCentiMm: (int)request.Values.QuickReleaseCentiMm),
				context.CancellationToken);
			return new SetKeyActuationResponse { Success = ok };
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception in {Operation}", nameof(SetKeyActuation));
			return new SetKeyActuationResponse { Success = false };
		}
	}

	public override async Task<SetPerKeyActuationResponse> SetPerKeyActuation(SetPerKeyActuationRequest request, ServerCallContext context)
	{
		try
		{
			var service = _deviceActuationServiceFactory.CreateForDevice(request.VendorId, request.ProductId);
			if (service == null || request.Values == null || request.Slots.Count == 0)
			{
				_logger.LogWarning("[ACT] per-key write refused for VID={VID} PID={PID}: service={HasService} values={HasValues} slots={Slots}",
					request.VendorId, request.ProductId, service != null, request.Values != null, request.Slots.Count);
				return new SetPerKeyActuationResponse { Success = false };
			}

			var ok = await service.WriteKeysAsync(request.Slots, new KeyboardActuationValues(
				PressCentiMm: (int)request.Values.PressCentiMm,
				ReleaseCentiMm: (int)request.Values.ReleaseCentiMm,
				RapidTrigger: request.Values.RapidTrigger,
				QuickPressCentiMm: (int)request.Values.QuickPressCentiMm,
				QuickReleaseCentiMm: (int)request.Values.QuickReleaseCentiMm),
				context.CancellationToken);
			return new SetPerKeyActuationResponse { Success = ok };
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception in {Operation}", nameof(SetPerKeyActuation));
			return new SetPerKeyActuationResponse { Success = false };
		}
	}

	private static KeyActuationValues ToProto(KeyboardActuationValues values) => new()
	{
		PressCentiMm = (uint)values.PressCentiMm,
		ReleaseCentiMm = (uint)values.ReleaseCentiMm,
		RapidTrigger = values.RapidTrigger,
		QuickPressCentiMm = (uint)values.QuickPressCentiMm,
		QuickReleaseCentiMm = (uint)values.QuickReleaseCentiMm,
	};

	public override async Task<GetExtendedKeysResponse> GetExtendedKeys(GetExtendedKeysRequest request, ServerCallContext context)
	{
		try
		{
			var service = _deviceExtendedKeysServiceFactory.CreateForDevice(request.VendorId, request.ProductId);
			if (service == null)
			{
				return new GetExtendedKeysResponse { Success = true, Supported = false };
			}

			var read = await service.ReadAsync(context.CancellationToken);
			if (read.Status == ExtendedKeysReadStatus.Unsupported)
			{
				// The board answered and has no advanced-key area (or no remappable keys): a verdict,
				// and the App must never call SetExtendedKeys for it.
				return new GetExtendedKeysResponse { Success = true, Supported = false };
			}

			if (read.State == null)
			{
				// The board could not be read. That is a fact about the transport and says nothing
				// about the hardware, so it must not travel as supported=false: the App hides the
				// section on that and early-returns from every preset push for the rest of the
				// session, over what may be one bad cable moment (review S2).
				_logger.LogWarning("[EXT] read failed for VID={VID} PID={PID} — reporting a failure, not a verdict",
					request.VendorId, request.ProductId);
				return new GetExtendedKeysResponse { Success = false };
			}

			var state = read.State;
			var response = new GetExtendedKeysResponse
			{
				Success = true,
				Supported = true,
				AreaBytes = state.AreaBytes,
				FreeBytes = state.FreeBytes,
				// The read had to reset this board to answer at all: what follows is the leftovers of
				// that reset, not a configuration, and the App must refuse to persist it (review m3b).
				Repaired = state.Repaired,
			};
			foreach (var entry in state.Entries)
				response.Entries.Add(ToProto(entry));
			response.BlockedSlots.AddRange(state.BlockedSlots);
			return response;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception in {Operation}", nameof(GetExtendedKeys));
			return new GetExtendedKeysResponse { Success = false };
		}
	}

	public override async Task<SetExtendedKeysResponse> SetExtendedKeys(SetExtendedKeysRequest request, ServerCallContext context)
	{
		try
		{
			var service = _deviceExtendedKeysServiceFactory.CreateForDevice(request.VendorId, request.ProductId);
			if (service == null)
			{
				_logger.LogWarning("[EXT] no extended-keys service for VID={VID} PID={PID}",
					request.VendorId, request.ProductId);
				return new SetExtendedKeysResponse { Success = false };
			}

			var desired = new List<ExtendedKeyEntry>(request.Entries.Count);
			foreach (var entry in request.Entries)
			{
				// An unknown mode is refused here rather than cast into the enum: the service would
				// reject it anyway, but a bogus value must not reach the encoder switch — and the
				// range check comes first, so 0x184 cannot truncate into a valid 0x84.
				if (entry.Mode is < 0 or > byte.MaxValue || !Enum.IsDefined(typeof(ExtendedKeyMode), (byte)entry.Mode))
				{
					_logger.LogWarning("[EXT] refusing entry with unknown mode 0x{Mode:X2}", entry.Mode);
					return new SetExtendedKeysResponse { Success = false };
				}

				// Same rule for the two output codes: every field of a key record is a byte on the
				// wire, and a value past that must be refused, not truncated (review S5).
				if (!TryFromProto(entry.HoldKey, out var holdKey) || !TryFromProto(entry.TapKey, out var tapKey))
				{
					_logger.LogWarning("[EXT] refusing entry on slot {Slot}: an output code is outside a byte", entry.Slot1);
					return new SetExtendedKeysResponse { Success = false };
				}

				desired.Add(new ExtendedKeyEntry(
					(ExtendedKeyMode)(byte)entry.Mode,
					entry.Slot1,
					entry.Slot2,
					entry.Priority,
					entry.TimeMs,
					holdKey,
					tapKey));
			}

			var result = await service.WriteAsync(desired, context.CancellationToken);
			return new SetExtendedKeysResponse { Success = result == ExtendedKeysWriteResult.Verified };
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception in {Operation}", nameof(SetExtendedKeys));
			return new SetExtendedKeysResponse { Success = false };
		}
	}

	private static ExtendedKey ToProto(ExtendedKeyEntry entry) => new()
	{
		Mode = (byte)entry.Mode,
		Slot1 = entry.Slot1,
		Slot2 = entry.Slot2,
		Priority = entry.Priority,
		TimeMs = entry.TimeMs,
		HoldKey = ToProto(entry.HoldKey),
		TapKey = ToProto(entry.TapKey),
	};

	private static ProtoExtendedKeyCode ToProto(InfraExtendedKeyCode code) => new()
	{
		TypeByte = code.TypeByte,
		Code1 = code.Code1,
		Code2 = code.Code2,
	};

	// A missing sub-message (an older client, or a pair mode that carries no codes) is "none". A field
	// outside a byte is refused rather than cast: code2 = 260 would truncate to 4 — a key nobody
	// configured, written to the board and reported back as success.
	private static bool TryFromProto(ProtoExtendedKeyCode? code, out InfraExtendedKeyCode result)
	{
		result = InfraExtendedKeyCode.None;
		if (code is null) return true;

		if (code.TypeByte is < 0 or > byte.MaxValue
			|| code.Code1 is < 0 or > byte.MaxValue
			|| code.Code2 is < 0 or > byte.MaxValue)
			return false;

		result = new InfraExtendedKeyCode((byte)code.TypeByte, (byte)code.Code1, (byte)code.Code2);
		return true;
	}

	public override async Task<BatchDevicesResponse> GetDevices(GetDevicesRequest request, ServerCallContext context)
	{
		try
		{
			var message = new BatchDevicesResponse
			{
				RequestId = request.RequestId
			};
			// PresentableDevices, not a bare GroupByDevice: this RPC is the fourth publication path
			// and was the only one still reporting a switched-off Cosmo as connected. The rule for
			// "what the client is shown" has to be the same on every path, or the first caller of
			// this RPC inherits a bug whose cause lives in a different method.
			var toSend = PresentableDevices(_hidDeviceDiscoveryService.GetClientUsbDevicesList()).ToArray();

			foreach (var hidDeviceState in toSend)
			{
				message.HidEntries.Add(MapHidWithCatalog(hidDeviceState));
			}

			return message;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception occurred in {Operation}", nameof(GetDevices));
			throw new RpcException(new Status(StatusCode.Internal, "Server internal error occurred"));
		}
	}

	public override async Task SubscribeOnDevices(
		IAsyncStreamReader<SubscribeOnDevicesRequest> requestStream,
		IServerStreamWriter<SubscribeOnDevicesResponse> responseStream, ServerCallContext context)
	{
		try
		{
			// here handle client requests, not used now 
			var readTask = Task.Run(async () =>
			{
				await foreach (var request in requestStream.ReadAllAsync())
				{
					await HandleClientHandshakeRequestAsync(requestStream, responseStream);
				}
			}, context.CancellationToken);

			// TAIPAN battery polling sends radio frames into a battery-powered mouse, so it runs
			// only while a client is actually attached to display the value. Paired with
			// RemoveConsumer in the finally below — and the pairing must be STRUCTURAL, i.e. sit
			// outside the early-exit condition inside the try: a call that skipped AddConsumer but
			// still ran the unconditional finally would steal another stream's increment and
			// silence battery polling for the rest of the process.
			_taipanStateMonitor.AddConsumer();
			_smartInfiniteBatteryMonitor.AddConsumer();

			// here send events to client
			try
			{
				if (!context.CancellationToken.IsCancellationRequested && !readTask.IsCompleted)
				{
					_serverStreamWriter = responseStream;
					// subscribe to hardware updates
					_hidDeviceDiscoveryService.HidDeviceListUsbConnected += HidDeviceDiscoveryService_DeviceListChanged;
					_bluetoothDeviceDiscoveryService.DeviceConnectionChanged += BluetoothDeviceDiscoveryService_DeviceConnectionChanged;
					_dpiStageMonitor.StageChanged += DpiStageMonitor_StageChanged;
					_dpiStageMonitor.ReportRateChanged += DpiStageMonitor_ReportRateChanged;
					_taipanStateMonitor.StageChanged += TaipanStateMonitor_StageChanged;
					_taipanStateMonitor.BatteryChanged += TaipanStateMonitor_BatteryChanged;
					_smartInfiniteBatteryMonitor.BatteryChanged += SmartInfiniteBatteryMonitor_BatteryChanged;
					_cosmoStateMonitor.PowerStateChanged += CosmoStateMonitor_PowerStateChanged;

					// send current usb devices states to client
					var hidToSend = PresentableDevices(_hidDeviceDiscoveryService.GetClientUsbDevicesList())
						.ToArray();
					await SendHidDevicesChanged(responseStream, 0, hidToSend);

					// send current bluetooth devices states to client
					var btToSend = await _bluetoothDeviceDiscoveryService.GetBluetoothDevicesAsync();
					foreach (var bt in btToSend)
					{
						await SendBluetoothDevicesChanged(responseStream, 0, bt);
					}

					// Ask each mouse to report its current active DPI stage so this fresh
					// client sees the real value instead of the UI default. The device
					// only notifies on change, and returns zeros to feature reads, so we
					// trigger a 0x80 query whose 0x05 reply flows back through
					// DpiStageMonitor_StageChanged. See docs/mouse/blaze/dpi-stage.md.
					_dpiStageMonitor.RequestStateRefresh();

					// TAIPAN answers no live state query (0x49 and every other read-form command in
					// 0x44..0x4F go unanswered; 0x06/0x08 only echo or return battery), so its stage
					// is recovered from MsConfig with the trailing-stage correction instead.
					_taipanStateMonitor.RequestStateRefresh();

					// keep alive until client disconnects so hardware events keep flowing
					await readTask;
				}
			}
			finally
			{
				// unsubscribe from hardware updates
				_hidDeviceDiscoveryService.HidDeviceListUsbConnected -= HidDeviceDiscoveryService_DeviceListChanged;
				_bluetoothDeviceDiscoveryService.DeviceConnectionChanged -= BluetoothDeviceDiscoveryService_DeviceConnectionChanged;
				_dpiStageMonitor.StageChanged -= DpiStageMonitor_StageChanged;
				_dpiStageMonitor.ReportRateChanged -= DpiStageMonitor_ReportRateChanged;
				_taipanStateMonitor.StageChanged -= TaipanStateMonitor_StageChanged;
				_taipanStateMonitor.BatteryChanged -= TaipanStateMonitor_BatteryChanged;
				_smartInfiniteBatteryMonitor.BatteryChanged -= SmartInfiniteBatteryMonitor_BatteryChanged;
				_cosmoStateMonitor.PowerStateChanged -= CosmoStateMonitor_PowerStateChanged;
				_taipanStateMonitor.RemoveConsumer();
				_smartInfiniteBatteryMonitor.RemoveConsumer();
				_serverStreamWriter = null;
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception occurred in {Operation}", nameof(SubscribeOnDevices));
		}
	}

	#endregion

	#region Private Methods

	private async Task HandleClientHandshakeRequestAsync(IAsyncStreamReader<SubscribeOnDevicesRequest> requestStream,
		IServerStreamWriter<SubscribeOnDevicesResponse> responseStream)
	{
		// not used now, example
		/*
        await responseStream.WriteAsync(new DevicesHandshakeResponse
        {
            RequestId = request.RequestId,
            //Entries = null
        });
        */
	}

	private async Task SendHidDevicesChanged(
		IServerStreamWriter<SubscribeOnDevicesResponse> responseStream,
		int requestId,
		IEnumerable<HidDeviceState> hidDevices)
	{
		ArgumentNullException.ThrowIfNull(responseStream, nameof(responseStream));
		ArgumentNullException.ThrowIfNull(hidDevices, nameof(hidDevices));

		try
		{
			var message = new SubscribeOnDevicesResponse
			{
				RequestId = requestId,
				SenderType = SenderType.Usb
			};

			foreach (var hidDeviceState in hidDevices)
			{
				message.HidEntries.Add(MapHidWithCatalog(hidDeviceState));
			}

			await responseStream.WriteAsync(message);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception occurred in {Operation}", nameof(SendHidDevicesChanged));
		}
	}

	private async Task SendBluetoothDevicesChanged(IServerStreamWriter<SubscribeOnDevicesResponse> responseStream,
		int requestId,
		BluetoothDeviceState bluetoothDevice)
	{
		ArgumentNullException.ThrowIfNull(responseStream);
		ArgumentNullException.ThrowIfNull(bluetoothDevice);

		try
		{
			var message = new SubscribeOnDevicesResponse
			{
				RequestId = requestId,
				SenderType = SenderType.Bluetooth,
				BluetoothEntry = MapBluetoothWithCatalog(bluetoothDevice)
			};

			await responseStream.WriteAsync(message);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception occurred in {Operation}", nameof(SendBluetoothDevicesChanged));
		}
	}

	private HidDeviceResponse MapHidWithCatalog(HidDeviceState hidDeviceState)
	{
		var msg = DevicesHelper.Map(hidDeviceState);

		// Prefer exact PID/VID match so a device like PG TAIPAN dongle (PID 33651)
		// gets ConnectionType=Dongle2_4G rather than the wired entry resolved by name.
		var entry = _registry.FindCatalogEntryByPidVid(hidDeviceState.VendorId, hidDeviceState.ProductId)
			?? _registry.FindCatalogEntry(hidDeviceState.ClientName, hidDeviceState.ProductName);

		if (entry != null)
			msg.CatalogEntry = entry;

		return msg;
	}

	private BluetoothDeviceResponse MapBluetoothWithCatalog(BluetoothDeviceState bluetoothDevice)
	{
		var msg = DevicesHelper.Map(bluetoothDevice);

		var entry = _registry.FindBluetoothCatalogEntry(bluetoothDevice.ClientName);
		if (entry != null)
			msg.CatalogEntry = entry;

		return msg;
	}

	private void DpiStageMonitor_StageChanged(DpiStageMonitor.StageEvent e) =>
		_ = SendDpiStageChangeAsync(e.VendorId, e.ProductId, e.DevicePath, e.Stage, e.DeviceKey);

	// Pushes a battery change to the connected client the moment the DeviceHost sees it, instead of
	// leaving the App to discover it on its own poll. Fire-and-forget for the same reason the stage
	// handler is: this runs on the monitor's reader thread and must not block it on a stream write.
	private void TaipanStateMonitor_BatteryChanged(TaipanStateMonitor.BatteryEvent e)
	{
		var writer = _serverStreamWriter;
		if (writer is null) return;

		_ = SendBatteryChangedAsync(writer, e);
	}

	// The Smart Infinite battery monitor emits the same event shape, so its pushes ride the
	// same fire-and-forget stream write. Runs on the monitor's poll-loop thread — must not block.
	private void SmartInfiniteBatteryMonitor_BatteryChanged(TaipanStateMonitor.BatteryEvent e)
	{
		var writer = _serverStreamWriter;
		if (writer is null) return;

		_ = SendBatteryChangedAsync(writer, e);
	}

	private async Task SendBatteryChangedAsync(IServerStreamWriter<SubscribeOnDevicesResponse> writer,
		TaipanStateMonitor.BatteryEvent e)
	{
		try
		{
			await _streamWriteLock.WaitAsync();
			try
			{
				await writer.WriteAsync(new SubscribeOnDevicesResponse
				{
					RequestId = 0,
					SenderType = SenderType.Usb,
					BatteryChange = new BatteryChange
					{
						VendorId = e.VendorId,
						ProductId = e.ProductId,
						DeviceKey = e.DeviceKey ?? string.Empty,
						Known = e.Known,
						Percent = e.Percent,
						IsCharging = e.IsCharging,
					},
				});
			}
			finally
			{
				_streamWriteLock.Release();
			}
		}
		catch (Exception ex)
		{
			// A client that went away mid-push is ordinary; the next subscribe re-syncs.
			_logger.LogDebug(ex, "Failed to push a battery change to the client");
		}
	}

	// TAIPAN reports its active stage by pushing a 0x49 state report when the physical DPI button
	// is pressed (the dongle relays it), so it feeds the same client contract as the BLAZE monitor.
	private void TaipanStateMonitor_StageChanged(TaipanStateMonitor.StageEvent e) =>
		_ = SendDpiStageChangeAsync(e.VendorId, e.ProductId, e.DevicePath, e.Stage, e.DeviceKey);

	private async Task SendDpiStageChangeAsync(int vendorId, int productId, string devicePath, int stage, string? deviceKey)
	{
		var writer = _serverStreamWriter;
		if (writer == null) return;

		await _streamWriteLock.WaitAsync();
		try
		{
			await writer.WriteAsync(new SubscribeOnDevicesResponse
			{
				SenderType = SenderType.Usb,
				DpiStageChange = new DpiStageChange
				{
					VendorId = vendorId,
					ProductId = productId,
					DevicePath = devicePath,
					StageIndex = stage,
					DeviceKey = deviceKey ?? string.Empty,
				},
			});
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception occurred in {Operation}", nameof(SendDpiStageChangeAsync));
		}
		finally
		{
			_streamWriteLock.Release();
		}
	}

	private void DpiStageMonitor_ReportRateChanged(DpiStageMonitor.RateEvent e) =>
		_ = SendReportRateChangeAsync(e.VendorId, e.ProductId, e.DevicePath, e.Hz, e.DeviceKey);

	private async Task SendReportRateChangeAsync(int vendorId, int productId, string devicePath, int hz, string? deviceKey)
	{
		var writer = _serverStreamWriter;
		if (writer == null) return;

		await _streamWriteLock.WaitAsync();
		try
		{
			await writer.WriteAsync(new SubscribeOnDevicesResponse
			{
				SenderType = SenderType.Usb,
				ReportRateChange = new ReportRateChange
				{
					VendorId = vendorId,
					ProductId = productId,
					DevicePath = devicePath,
					Hz = hz,
					DeviceKey = deviceKey ?? string.Empty,
				},
			});
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception occurred in {Operation}", nameof(SendReportRateChangeAsync));
		}
		finally
		{
			_streamWriteLock.Release();
		}
	}

	private async void HidDeviceDiscoveryService_DeviceListChanged(object? sender, HidDeviceListChangedEventArgs e)
	{
		try
		{
			// Devices come and go whether or not a client is listening, and _serverStreamWriter is
			// cleared on unsubscribe — so capture it once and skip when there is nobody to tell.
			// Without this the send threw a NullReferenceException that the catch below swallowed.
			var writer = _serverStreamWriter;
			if (writer == null) return;

			var toSend = PresentableDevices(e.HidDevices).ToArray();
			await SendHidDevicesChanged(writer, 0, toSend);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception occurred in {Operation}", nameof(HidDeviceDiscoveryService_DeviceListChanged));
		}
	}

	// The device list the App is allowed to see. Everything enumerated is presentable EXCEPT a
	// Cosmo whose headset is not confirmed on: its dongle stays enumerated regardless, so without
	// this the card would claim the headset is connected while it sits switched off in a drawer.
	// "Not yet read" is hidden too — see CosmoStateMonitor.IsPresentable.
	//
	// Filtering here rather than in discovery keeps the rule where "what the client sees" is
	// decided, and leaves HID enumeration meaning exactly what it says.
	private IEnumerable<HidDeviceState> PresentableDevices(IEnumerable<HidDeviceState> devices)
		=> devices
			.Where(d => _cosmoStateMonitor.IsPresentable(d.VendorId, d.ProductId, d.DeviceKey))
			.GroupByDevice();

	// The headset was switched on or off. Nothing in USB enumeration changed, so the App is only
	// told by re-publishing the list through the same path a plug/unplug uses.
	private async void CosmoStateMonitor_PowerStateChanged()
	{
		try
		{
			var writer = _serverStreamWriter;
			if (writer == null) return;

			var toSend = PresentableDevices(_hidDeviceDiscoveryService.GetClientUsbDevicesList()).ToArray();
			await SendHidDevicesChanged(writer, 0, toSend);
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception occurred in {Operation}", nameof(CosmoStateMonitor_PowerStateChanged));
		}
	}

	private async void BluetoothDeviceDiscoveryService_DeviceConnectionChanged(object? sender, BluetoothDeviceConnectionChangedEventArgs e)
	{
		try
		{
			var writer = _serverStreamWriter;
			if (writer == null) return;

			await SendBluetoothDevicesChanged(writer, 0, DevicesHelper.Map(e));
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Exception occurred in {Operation}", nameof(BluetoothDeviceDiscoveryService_DeviceConnectionChanged));
		}
	}

	#endregion
}
