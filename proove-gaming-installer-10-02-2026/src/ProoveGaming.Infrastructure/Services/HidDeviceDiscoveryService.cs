using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using HidSharp;
using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.CustomEvents;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

public class HidDeviceDiscoveryService : IHidDeviceDiscoveryService
{
	#region Private Fields

	private readonly ILogger<IHidDeviceDiscoveryService> _logger;
	private readonly IHidDeviceProvider _hidDeviceProvider;
	private readonly IHidDeviceStateFactory _hidDeviceStateFactory;
	private readonly IDeviceInfoProvider _deviceInfoProvider;
	private readonly Lock _lockObj = new();
	private List<HidDevice> _hidDevices = new List<HidDevice>();
	private List<HidDeviceState> _dtos = new List<HidDeviceState>();

	// How long a battery read waits for the shared device gate before giving up this tick. Long
	// enough to ride out an RGB write (a fraction of a second), short enough that a multi-second
	// 2.4G key-table transfer makes the poll yield instead of stacking blocked threads.
	private static readonly TimeSpan BatteryGateTimeout = TimeSpan.FromSeconds(2);

	// The device path that last produced a valid battery reading per (vid, pid). At the
	// monitor's seconds cadence rescanning every candidate interface (two failing opens, ~8 log
	// lines per read) is unacceptable noise and waste, so the next read tries this path alone
	// and only falls back to the full scan when it stops answering. Hit from the monitor loop
	// and gRPC threads concurrently.
	private readonly ConcurrentDictionary<(int Vid, int Pid), string> _lastGoodBatteryPath = new();

	// Final resolved output-report-ID list per interface path (null = the catalog's preferred
	// report ID is absent on that interface, so the interface is skipped). The descriptor is as
	// stable as the path, and re-parsing it on every poll tick was pure gen0 churn (review
	// round 1 nit); an entry is dropped together with its invalidated path above.
	private readonly ConcurrentDictionary<string, List<byte>?> _reportIdsByPath = new(StringComparer.OrdinalIgnoreCase);

	#endregion

	#region Public Members

	public HidDeviceDiscoveryService(ILogger<IHidDeviceDiscoveryService> logger,
		IHidDeviceProvider hidDeviceProvider,
		IDeviceInfoProvider deviceInfoProvider,
		IHidDeviceStateFactory hidDeviceStateFactory)
	{
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_hidDeviceProvider = hidDeviceProvider ?? throw new ArgumentNullException(nameof(hidDeviceProvider));
		_hidDeviceStateFactory = hidDeviceStateFactory ?? throw new ArgumentNullException(nameof(hidDeviceStateFactory));
		_deviceInfoProvider = deviceInfoProvider ?? throw new ArgumentNullException(nameof(deviceInfoProvider));
		IsRunning = false;
	}

	public event EventHandler<HidDeviceListChangedEventArgs>? HidDeviceListUsbConnected;
	public event EventHandler<HidDeviceListChangedEventArgs>? HidDeviceListBluetoothAdapterConnected;

	public void Start()
	{
		try
		{
			if (!IsRunning)
			{
				IsRunning = true;
				_hidDeviceProvider.SubscribeToDeviceListChanged(Local_Changed);
				RecognizeDevices();
			}
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Exception occurred in {Operation}", nameof(HidDeviceDiscoveryService.Start));
			throw;
		}
	}

	public void Stop()
	{
		try
		{
			if (IsRunning)
			{
				IsRunning = false;
				_hidDeviceProvider.UnsubscribeFromDeviceListChanged(Local_Changed);
				// Under the lock: the battery monitor's presence/candidate lookups enumerate
				// these lists from another thread, and a Clear mid-enumeration throws.
				lock (_lockObj)
				{
					_hidDevices.Clear();
					_dtos.Clear();
				}
			}
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Exception occurred in {Operation}", nameof(HidDeviceDiscoveryService.Stop));
			throw;
		}
	}

	public bool IsRunning { get; private set; }

	public IReadOnlyList<HidDeviceState> GetClientUsbDevicesList()
	{
		var fullList = CloneList();
		return FilterUsbDevices(fullList).ToList();
	}

	public (int batteryValue, bool isCharging) TryGetBatteryLevel(int vendorId, int productId)
	{
		var (command, preferredReportId, isDirectScale) = GetBatteryConfig(vendorId, productId);
		var cacheKey = (vendorId, productId);

		// Fast path: the interface that answered last time almost certainly answers again, so
		// try it alone before rescanning every candidate. The per-step lines go to Debug on this
		// path (quiet) so the steady state is one Information line per read — the parse-result
		// line in ParseBatteryResponse, which field debugging depends on.
		if (_lastGoodBatteryPath.TryGetValue(cacheKey, out var cachedPath))
		{
			var cachedDevice = FindCandidateByPath(vendorId, productId, cachedPath);
			if (cachedDevice is not null)
			{
				var (battery, isCharging, gateBusy) = TryReadDevice(cachedDevice, command, preferredReportId,
					isDirectScale, quiet: true);
				if (battery >= 0)
					return (battery, isCharging);

				// A held gate is not a dead path — a key/RGB/macro write owns the interface for
				// the moment. Keep the cache and skip this tick; the next poll retries.
				if (gateBusy)
					return (-1, false);
			}

			// The cached path disappeared or stopped answering: forget it (and its resolved
			// report IDs — a replugged interface may describe itself differently) and rescan.
			_lastGoodBatteryPath.TryRemove(cacheKey, out _);
			_reportIdsByPath.TryRemove(cachedPath, out _);
		}

		var candidates = GetCandidates(vendorId, productId);
		LogDiscoveryHeader(candidates, vendorId, productId, command, preferredReportId, isDirectScale);

		foreach (var device in candidates)
		{
			var (battery, isCharging, _) = TryReadDevice(device, command, preferredReportId,
				isDirectScale, quiet: false);
			if (battery >= 0)
			{
				_lastGoodBatteryPath[cacheKey] = device.DevicePath;
				return (battery, isCharging);
			}
		}

		_logger?.LogWarning("[Battery] no valid battery reading obtained");
		return (-1, false);
	}

	public bool IsHidDevicePresent(int vendorId, int productId)
	{
		lock (_lockObj)
		{
			foreach (var device in _hidDevices)
			{
				if (device.VendorID == vendorId && device.ProductID == productId)
					return true;
			}
			return false;
		}
	}

	// One attempt against one interface: gate, write the battery command, read and parse the
	// reply. battery is -1 on any failure; gateBusy distinguishes "another service holds the
	// DeviceGate" (skip the tick, the path is fine) from a genuinely failed read (invalidate the
	// cached path). quiet demotes the per-step discovery/response lines to Debug for the cached
	// fast path; warnings and the parse-result line keep their levels.
	private (int battery, bool isCharging, bool gateBusy) TryReadDevice(HidDevice device, byte command,
		byte? preferredReportId, bool isDirectScale, bool quiet)
	{
		var level = quiet ? LogLevel.Debug : LogLevel.Information;
		int outLen = device.GetMaxOutputReportLength();
		int inLen = device.GetMaxInputReportLength();
		_logger?.Log(level, "[Battery] trying path={Path} outLen={Out} inLen={In}",
			device.DevicePath, outLen, inLen);

		// A battery exchange writes to the same vendor interface the key/RGB/macro services
		// hold the DeviceGate for, and landing mid table-transfer splices both exchanges — on
		// 2.4G a key write takes whole seconds over the radio while battery is polled every few
		// seconds. Yield rather than queue: skipping this tick costs nothing, the next poll
		// retries.
		using var gate = DeviceGate.TryAcquire(device.DevicePath, BatteryGateTimeout);
		if (gate is null)
		{
			_logger?.Log(level, "[Battery] path={Path} busy (device gate held) — skipping this attempt",
				device.DevicePath);
			return (-1, false, true);
		}

		byte[]? response = TryReadBattery(device, outLen, inLen, command, preferredReportId, quiet);
		if (response == null) return (-1, false, false);

		LogResponse(response, quiet);
		var (battery, isCharging) = ParseBatteryResponse(response, isDirectScale);

		if (battery is >= 1 and <= 100)
			return (battery, isCharging, false);

		_logger?.LogWarning("[Battery] battery={B} is out of range 1-100", battery);
		return (-1, false, false);
	}

	private List<HidDevice> GetCandidates(int vendorId, int productId)
	{
		lock (_lockObj)
		{
			return _hidDevices
				.Where(d => d.VendorID == vendorId && d.ProductID == productId)
				.ToList();
		}
	}

	private HidDevice? FindCandidateByPath(int vendorId, int productId, string devicePath)
	{
		lock (_lockObj)
		{
			return _hidDevices.FirstOrDefault(d => d.VendorID == vendorId && d.ProductID == productId
				&& string.Equals(d.DevicePath, devicePath, StringComparison.OrdinalIgnoreCase));
		}
	}

	private (byte command, byte? preferredReportId, bool isDirectScale) GetBatteryConfig(int vendorId, int productId)
	{
		var config = _deviceInfoProvider.DevicesMaps
			.FirstOrDefault(m => !m.HasBluetooth &&
				m.VendorId == vendorId.ToString() &&
				m.ProductId == productId.ToString());
		byte command = config?.BatteryCommand ?? 0x30;
		byte? preferredReportId = config?.BatteryReportId;
		bool isDirectScale = config?.BatteryDirectScale ?? (command != 0x30);
		return (command, preferredReportId, isDirectScale);
	}

	private void LogDiscoveryHeader(List<HidDevice> candidates, int vendorId, int productId,
		byte command, byte? preferredReportId, bool isDirectScale)
	{
		_logger?.LogInformation("[Battery] found {Count} HID interfaces for VID={VID} PID={PID} command=0x{Cmd:X2} preferredReportId={Rid} directScale={Ds}",
			candidates.Count, vendorId, productId, command,
			preferredReportId.HasValue ? $"0x{preferredReportId.Value:X2}" : "none", isDirectScale);

		for (int i = 0; i < candidates.Count; i++)
			_logger?.LogInformation("[Battery] candidate[{I}] outLen={Out} inLen={In} path={Path}",
				i, candidates[i].GetMaxOutputReportLength(), candidates[i].GetMaxInputReportLength(), candidates[i].DevicePath);
	}

	private void LogResponse(byte[] response, bool quiet)
	{
		var level = quiet ? LogLevel.Debug : LogLevel.Information;
		if (_logger?.IsEnabled(level) != true) return;

		_logger.Log(level, "[Battery] response ({Len} bytes): {Hex}",
			response.Length, BitConverter.ToString(response));
		_logger.Log(level, "[Battery] non-zero bytes: {Summary}",
			string.Join(", ", Enumerable.Range(0, response.Length)
				.Where(i => response[i] != 0)
				.Select(i => $"[{i:D2}]=0x{response[i]:X2}={response[i]}")));
	}

	private (int battery, bool isCharging) ParseBatteryResponse(byte[] response, bool isDirectScale)
	{
		int batteryIdx = response.Length >= 64 ? 8 : 14;
		int rawByte = response[batteryIdx];
		int battery = isDirectScale
			? rawByte
			: (int)Math.Round(rawByte * 100.0 / 15.0);

		// The Smart Infinite 0x1F battery reply (the reply echoes its command at [1]) carries
		// the charge state in the SECOND payload byte, not at [3] (which is the frame's addr-hi
		// and always zero — the flag read there never fired). Measured on a charging Rampage
		// over its 2.4G receiver (2026-08-17): 06-1F-00-00-00-00-00-55-55-01-… = status 0x55 at
		// [7], battery 85 at [8], charge flag at [9]. The sibling RY5088 sheet documents the
		// same position as 0 idle / 1 charging / 2 fully charged. Both 1 and 2 count as
		// charging: "fully charged" still means the cable is in and feeding the device, and the
		// wired path keeps its ⚡ for as long as the cable is live — dropping the icon on 2 read
		// as "charging interrupted" instead of "done".
		bool isCharging = response.Length >= 64 && response[1] == 0x1F
			? response[9] is 1 or 2
			: response[response.Length >= 64 ? 3 : 13] != 0;

		_logger?.LogInformation("[Battery] batteryIdx={BI} rawByte={Raw} battery={B} isDirectScale={D} charging={C}",
			batteryIdx, rawByte, battery, isDirectScale, isCharging);

		return (battery, isCharging);
	}

	private byte[]? TryReadBattery(HidDevice device, int outLen, int inLen, byte command, byte? preferredReportId, bool quiet)
	{
		var reportIds = ResolveReportIds(device, preferredReportId, quiet);
		if (reportIds == null || reportIds.Count == 0) return null;

		int cmdLen = Math.Max(outLen, 33);
		int respLen = Math.Max(inLen, 33);
		int win32BufSize = Math.Max(Math.Max(outLen, inLen), 65);
		string? win32Path = TryGetDevicePath(device);

		foreach (var reportId in reportIds)
		{
			var response = TryReadViaHidSharp(device, reportId, command, cmdLen, respLen, quiet);
			if (response == null && win32Path != null)
			{
				_logger?.Log(quiet ? LogLevel.Debug : LogLevel.Information,
					"[Battery] trying Win32 fallback reportId=0x{Id:X2} path={Path}", reportId, win32Path);
				response = TryReadBatteryWin32(win32Path, reportId, command, win32BufSize, quiet);
			}
			if (response != null) return response;
		}
		return null;
	}

	// Returns the report IDs to try (preferred first), or null if a preferred ID
	// was specified but this interface doesn't expose it.
	private List<byte>? ResolveReportIds(HidDevice device, byte? preferredReportId, bool quiet)
	{
		// The preferred ID is catalog config per (vid, pid) and the path implies both, so the
		// final ordered list is cacheable per path as-is.
		var path = device.DevicePath;
		if (path is not null && _reportIdsByPath.TryGetValue(path, out var resolved))
			return resolved;

		List<byte> ids;
		try
		{
			var desc = device.GetReportDescriptor();
			ids = desc.DeviceItems
				.SelectMany(d => d.OutputReports)
				.Select(r => (byte)r.ReportID)
				.Distinct()
				.OrderBy(id => id)
				.ToList();
			if (ids.Count == 0) ids = [0x00];
			_logger?.Log(quiet ? LogLevel.Debug : LogLevel.Information,
				"[Battery] {Path} output report IDs: [{Ids}]",
				device.DevicePath, string.Join(", ", ids.Select(id => $"0x{id:X2}")));
		}
		catch
		{
			ids = [0x00];
		}

		resolved = !preferredReportId.HasValue
			? ids
			: !ids.Contains(preferredReportId.Value)
				? null
				: ids
					.OrderBy(id => id == preferredReportId.Value ? 0 : 1)
					.ThenBy(id => id)
					.ToList();

		if (path is not null)
			_reportIdsByPath[path] = resolved;
		return resolved;
	}

	private byte[]? TryReadViaHidSharp(HidDevice device, byte reportId, byte command, int cmdLen, int respLen, bool quiet)
	{
		var level = quiet ? LogLevel.Debug : LogLevel.Information;
		try
		{
			using var stream = device.Open();
			stream.WriteTimeout = 2000;
			stream.ReadTimeout = 1000;

			var cmd = new byte[cmdLen];
			cmd[0] = reportId;
			cmd[1] = command;
			stream.Write(cmd);
			_logger?.Log(level, "[Battery] reportId=0x{Id:X2} write accepted on {Path}", reportId, device.DevicePath);

			var buf = new byte[respLen];
			int read = stream.Read(buf);
			_logger?.Log(level, "[Battery] reportId=0x{Id:X2} read {N} bytes: {Hex}",
				reportId, read, BitConverter.ToString(buf, 0, Math.Min(read, 20)));
			return read >= 15 ? buf : null;
		}
		catch (Exception ex)
		{
			_logger?.LogWarning("[Battery] HidSharp reportId=0x{Id:X2} on {Path}: {Msg}",
				reportId, device.DevicePath, ex.Message);
			return null;
		}
	}

	private static string? TryGetDevicePath(HidDevice device)
	{
		try { return device.GetFileSystemName(); } catch { return null; }
	}

	private byte[]? TryReadBatteryWin32(string devicePath, byte reportId, byte command, int bufSize, bool quiet)
	{
		var level = quiet ? LogLevel.Debug : LogLevel.Information;
		try
		{
			using var handle = NativeMethods.CreateFile(
				devicePath,
				NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
				NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
				IntPtr.Zero,
				NativeMethods.OPEN_EXISTING,
				0,
				IntPtr.Zero);

			if (handle.IsInvalid)
			{
				_logger?.LogWarning("[Battery] Win32 open failed path={Path} err={Err}", devicePath, Marshal.GetLastWin32Error());
				return null;
			}

			var cmd = new byte[bufSize];
			cmd[0] = reportId;
			cmd[1] = command;

			if (!NativeMethods.WriteFile(handle, cmd, (uint)cmd.Length, out _, IntPtr.Zero))
			{
				_logger?.LogWarning("[Battery] Win32 WriteFile failed path={Path} err={Err}", devicePath, Marshal.GetLastWin32Error());
				return null;
			}
			_logger?.Log(level, "[Battery] Win32 WriteFile accepted path={Path}", devicePath);

			var response = new byte[bufSize];
			byte[]? result = null;
			var readTask = Task.Run(() =>
			{
				if (NativeMethods.ReadFile(handle, response, (uint)response.Length, out uint bytesRead, IntPtr.Zero) && bytesRead >= 15)
					result = response;
			});

			if (!readTask.Wait(1000))
			{
				NativeMethods.CancelIoEx(handle, IntPtr.Zero);
				readTask.Wait(200);
				_logger?.LogWarning("[Battery] Win32 ReadFile timed out path={Path}", devicePath);
				return null;
			}

			if (result != null)
				_logger?.Log(level, "[Battery] Win32 ReadFile succeeded path={Path}: {Hex}",
					devicePath, BitConverter.ToString(result, 0, Math.Min(20, result.Length)));
			return result;
		}
		catch (Exception ex)
		{
			_logger?.LogWarning(ex, "[Battery] Win32 fallback exception path={Path}", devicePath);
			return null;
		}
	}

	private static class NativeMethods
	{
		public const uint GENERIC_READ = 0x80000000;
		public const uint GENERIC_WRITE = 0x40000000;
		public const uint FILE_SHARE_READ = 0x00000001;
		public const uint FILE_SHARE_WRITE = 0x00000002;
		public const uint OPEN_EXISTING = 3;

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		public static extern SafeFileHandle CreateFile(
			string lpFileName, uint dwDesiredAccess, uint dwShareMode,
			IntPtr lpSecurityAttributes, uint dwCreationDisposition,
			uint dwFlagsAndAttributes, IntPtr hTemplateFile);

		[DllImport("kernel32.dll", SetLastError = true)]
		public static extern bool WriteFile(SafeFileHandle hFile, byte[] lpBuffer,
			uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

		[DllImport("kernel32.dll", SetLastError = true)]
		public static extern bool ReadFile(SafeFileHandle hFile, byte[] lpBuffer,
			uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

		[DllImport("kernel32.dll", SetLastError = true)]
		public static extern bool CancelIoEx(SafeFileHandle hFile, IntPtr lpOverlapped);
	}

	#endregion

	#region Private Methods

	private void Local_Changed(object? sender, DeviceListChangedEventArgs e)
	{
		try
		{
			RecognizeDevices();
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "Exception occurred in {Operation}", nameof(Local_Changed));
		}
	}

	private void RecognizeDevices()
	{
		lock (_lockObj)
		{
			_hidDevices = _hidDeviceProvider.GetDevices();

			_dtos.Clear();
			foreach (var device in _hidDevices)
			{
				if (IsUsbConnected(device))
				{
					HidDeviceState item = _hidDeviceStateFactory.Create(device);
					_dtos.Add(item);
				}
			}
		}

		var fullList = CloneList();
		WriteLogs(fullList);
		var usbDevices = FilterUsbDevices(fullList);
		var bluetoothAdapters = FilterBluetoothAdapters(fullList);
		OnUsbDeviceConnected(usbDevices);
		OnBluetoothAdapterConnected(bluetoothAdapters);
	}

	private void OnUsbDeviceConnected(IEnumerable<HidDeviceState> usbDevices)
	{
		var args = new HidDeviceListChangedEventArgs();
		args.HidDevices.AddRange(usbDevices ?? Array.Empty<HidDeviceState>());
		HidDeviceListUsbConnected?.Invoke(this, args);
	}

	private void OnBluetoothAdapterConnected(IEnumerable<HidDeviceState> bluetoothAdapters)
	{
		var args = new HidDeviceListChangedEventArgs();
		args.HidDevices.AddRange(bluetoothAdapters ?? Array.Empty<HidDeviceState>());
		HidDeviceListBluetoothAdapterConnected?.Invoke(this, args);
	}

	private List<HidDeviceState> CloneList()
	{
		lock (_lockObj)
		{
			var brandNewList = new List<HidDeviceState>();

			if (_dtos != null)
			{
				foreach (var dto in _dtos)
				{
					brandNewList.Add(_hidDeviceStateFactory.Clone(dto));
				}
			}

			return brandNewList;
		}
	}

	private IEnumerable<HidDeviceState> FilterUsbDevices(IEnumerable<HidDeviceState> list)
	{
		var newList = list.Where(x => _deviceInfoProvider.DevicesMaps
			.Any(m => !m.HasBluetooth &&
				string.Equals(m.ProductId, x.ProductId) &&
				string.Equals(m.VendorId, x.VendorId) &&
				(string.IsNullOrEmpty(m.ProductName) ||
				 string.Equals(m.ProductName, x.ProductName, StringComparison.OrdinalIgnoreCase))))
			.ToList();
		foreach (var device in newList)
		{
			device.ClientName = _deviceInfoProvider.GetClientName(device.ProductId, device.VendorId, device.ProductName);
		}
		return newList;
	}

	private static bool IsUsbConnected(Device device)
	{
		try
		{
			using (var stream = device.Open())
			{
				return stream != null;
			}
		}
		catch
		{
			return false;
		}
	}

	private IReadOnlyList<HidDeviceState> FilterBluetoothAdapters(IReadOnlyList<HidDeviceState> list)
	{
		var newList = list.Where(x => _deviceInfoProvider.DevicesMaps
			.Any(m => m.HasBluetooth &&
				string.Equals(m.ProductId, x.ProductId) &&
				string.Equals(m.VendorId, x.VendorId)))
			.ToList();
		foreach (var device in newList)
		{
			device.ClientName = _deviceInfoProvider.GetClientName(device.ProductId, device.VendorId, device.ProductName);
		}
		return newList;
	}

	// Debug, not Information. This runs on EVERY device-list change and emits one line per HID
	// device on the machine — so an unrelated USB plug on a busy desktop writes dozens of lines
	// nobody asked for, and the DeviceHost's daily log file grows with activity that has nothing to
	// do with us. It is a diagnostic dump, which is what Debug is for; the events that matter
	// (a catalogued device connecting) are logged at Information elsewhere.
	private void WriteLogs(IEnumerable<HidDeviceState> list)
	{
		// Checked up front so the per-device interpolation and the catalog scan are skipped
		// entirely at the default level, rather than formatted and then dropped by the sink.
		if (list == null || _logger?.IsEnabled(LogLevel.Debug) != true || !list.Any())
		{
			return;
		}

		foreach (var device in list)
		{
			var allowed = _deviceInfoProvider.DevicesMaps.Any(m =>
				string.Equals(m.ProductId, device.ProductId) &&
				string.Equals(m.VendorId, device.VendorId));
			_logger?.LogDebug("HID device found, PID: {ProductId}, VID: {VendorId}, productName: {ProductName}, friendlyName: {FriendlyName}, allowed: {Allowed}", device.ProductId, device.VendorId, device.ProductName, device.FriendlyName, allowed);
		}
	}

	#endregion
}
