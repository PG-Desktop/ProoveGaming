using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using HidSharp;
using Microsoft.Win32.SafeHandles;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;
using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.DeviceHost.Services;

// Watches every X43/BLAZE mouse for physical DPI-button presses and raises StageChanged.
//
// The active DPI stage is delivered by the firmware on a vendor HID collection
// (MI_01 Col04, usagePage 0xFF03 / usage 0x00F0) as a 7-byte report
// [0x05, profile, reportRate, dpi_stage, ledEffect, 0, 0] — dpi_stage at offset 3.
// It is read with ReadFile (no window needed), so this lives in DeviceHost rather
// than the App: always-on, single owner of the collection, and ready to carry
// button-click statistics later.
//
// Multi-device: enumerates ALL Col04 interfaces and runs one reader per physical
// unit, keyed by device path — so two identical mice are read independently and each
// event carries its own path.
public sealed class DpiStageMonitor : BackgroundService
{
	public readonly record struct StageEvent(int VendorId, int ProductId, string DevicePath, int Stage, string DeviceKey);
	public readonly record struct RateEvent(int VendorId, int ProductId, string DevicePath, int Hz, string DeviceKey);

	public event Action<StageEvent>? StageChanged;
	public event Action<RateEvent>? ReportRateChanged;

	private readonly ILogger<DpiStageMonitor> _logger;
	private readonly IDeviceRgbServiceFactory _rgbFactory;
	private readonly IHidDeviceProvider _hidDeviceProvider;

	// Signalled when the HID device list changes, so the loop picks up a newly-connected mouse at
	// once and can idle (no periodic enumeration) while nothing we monitor is plugged in.
	private readonly SemaphoreSlim _deviceListChanged = new(0, 1);

	// Active readers keyed by device interface path.
	private readonly ConcurrentDictionary<string, ReaderState> _readers = new(StringComparer.OrdinalIgnoreCase);

	// Non-catalog Col04 paths already logged as skipped, so a foreign device is diagnosable
	// without per-sweep noise (same pattern as the factory's _loggedMissingConfig).
	private readonly ConcurrentDictionary<string, bool> _loggedSkippedPaths = new(StringComparer.OrdinalIgnoreCase);

	private static readonly Regex VidPidRegex =
		new(@"vid_(?<vid>[0-9a-f]{4})&pid_(?<pid>[0-9a-f]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private sealed class ReaderState
	{
		public required CancellationTokenSource Cts { get; init; }
		// Stable per-physical-device key (Container ID), resolved once when the reader starts so
		// each event and cache-sync routes to the right unit among two identical mice. "" = unknown.
		public string DeviceKey { get; init; } = string.Empty;
		public SafeFileHandle? Handle { get; set; }
		public int LastStage { get; set; } = -1;
		public int LastRateHz { get; set; } = -1;
	}

	public DpiStageMonitor(ILogger<DpiStageMonitor> logger, IDeviceRgbServiceFactory rgbFactory, IHidDeviceProvider hidDeviceProvider)
	{
		_logger = logger;
		_rgbFactory = rgbFactory;
		_hidDeviceProvider = hidDeviceProvider;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		if (!OperatingSystem.IsWindows()) return;

		// Wake the loop the moment the HID device list changes so a freshly-connected mouse is
		// picked up without waiting out the idle interval. Stored in a local so sub/unsub use the
		// same delegate instance.
		EventHandler<DeviceListChangedEventArgs> onDeviceListChanged = (_, _) =>
		{
			if (_deviceListChanged.CurrentCount == 0)
			{
				try { _deviceListChanged.Release(); } catch (SemaphoreFullException) { }
			}
		};
		_hidDeviceProvider.SubscribeToDeviceListChanged(onDeviceListChanged);

		_logger.LogInformation("[DPI] DpiStageMonitor started");
		try
		{
			// Enumerate HID interfaces once at startup; afterwards ONLY on a device plug/unplug
			// (signalled) or a slow safety-net interval. Running SetupDiGetClassDevs every 2s churns
			// the Windows device stack and grows DeviceAssociationService memory unbounded (crash).
			try { RefreshReaders(); }
			catch (Exception ex) { _logger.LogWarning(ex, "[DPI] refresh failed"); }
			var lastEnumerate = DateTime.UtcNow;

			while (!stoppingToken.IsCancellationRequested)
			{
				// Poll each open reader for its current state. The DPI button broadcasts a 0x05
				// report on change, but the polling-rate button does NOT — so without this poll a
				// rate change made on the device is invisible until the app resubscribes. This is a
				// cheap 0x80 on the already-open handle (no enumeration); the reply is de-duplicated
				// (LastStage/LastRateHz), so it is silent unless something actually changed.
				foreach (var (path, st) in _readers)
				{
					var (vid, pid) = ParseVidPid(path);
					if (vid != 0) QueryDeviceState(vid, pid, st.DeviceKey, 0);
				}

				// While a monitored device is connected, poll every 2s for the rate button. While
				// nothing is connected, idle until the HID list changes or a long fallback elapses.
				var interval = _readers.IsEmpty ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(2);
				bool deviceListChanged;
				try { deviceListChanged = await _deviceListChanged.WaitAsync(interval, stoppingToken); }
				catch (OperationCanceledException) { break; }

				// Re-enumerate only on a real device-list change, or at most every ~30s as a safety
				// net for a reader that exited transiently — never on the 2s state poll.
				if (deviceListChanged || DateTime.UtcNow - lastEnumerate >= TimeSpan.FromSeconds(30))
				{
					try { RefreshReaders(); }
					catch (Exception ex) { _logger.LogWarning(ex, "[DPI] refresh failed"); }
					lastEnumerate = DateTime.UtcNow;
				}
			}
		}
		finally
		{
			_hidDeviceProvider.UnsubscribeFromDeviceListChanged(onDeviceListChanged);

			// Stop all readers on shutdown. A reader that exits concurrently disposes its own CTS
			// right after removing itself from _readers, so Cancel() can race that disposal —
			// benign, the loop is already gone.
			foreach (var reader in _readers.Values)
			{
				try { reader.Cts.Cancel(); } catch (ObjectDisposedException) { }
				try { if (reader.Handle is { IsInvalid: false }) CancelIoEx(reader.Handle, IntPtr.Zero); } catch { }
			}
		}
	}

	// Start a reader for any Col04 path that is present but not yet being read; readers
	// for unplugged devices exit on their own (ReadFile fails) and remove themselves.
	private void RefreshReaders()
	{
		var present = EnumerateCol04Paths();

		foreach (var path in present)
		{
			if (_readers.ContainsKey(path)) continue;

			var (vid, pid) = ParseVidPid(path);
			if (vid == 0) continue;

			// Only catalog mice get readers. Any vendor's device can expose an &mi_01&col04
			// interface (seen on a Logitech unit, VID 056D), and its reader would open a
			// 0-byte-read handle, exit, and be restarted by every safety-net sweep — a new
			// thread, an undisposed CTS and a QueryDeviceState round-trip each time, forever.
			if (!_rgbFactory.IsCatalogMouse(vid, pid))
			{
				if (_loggedSkippedPaths.TryAdd(path, true))
					_logger.LogDebug("[DPI] skip non-catalog Col04 device VID={V:X4} PID={P:X4} path={Path}", vid, pid, path);
				continue;
			}

			var cts = new CancellationTokenSource();
			var state = new ReaderState { Cts = cts, DeviceKey = DeviceContainerId.TryGet(path) ?? string.Empty };
			if (!_readers.TryAdd(path, state))
			{
				// Lost the add race with a concurrent RefreshReaders (ExecuteAsync's loop vs
				// RequestStateRefresh from a gRPC thread) — the winner's reader owns the path, so
				// dispose the CTS we just created instead of leaking it.
				cts.Dispose();
				continue;
			}

			var thread = new Thread(() => ReaderLoop(path, vid, pid, state, cts.Token))
			{
				IsBackground = true,
				Name = $"X43DpiReader:{pid:X4}",
			};
			_logger.LogInformation("[DPI] start reader VID={V:X4} PID={P:X4} path={Path}", vid, pid, path);
			thread.Start();
		}
	}

	private void ReaderLoop(string path, int vid, int pid, ReaderState state, CancellationToken token)
	{
		try
		{
			while (!token.IsCancellationRequested)
			{
				var handle = OpenShared(path);
				if (handle == null || handle.IsInvalid)
				{
					// Device gone (or busy) — stop reading this path so RefreshReaders
					// can restart it later if it reappears.
					break;
				}

				state.Handle = handle;
				var buf = new byte[32];

				// The firmware only pushes a 0x05 state report on CHANGE (DPI button
				// press) — never spontaneously on connect. So as soon as we own the
				// Col04 read pipe, ask the device to broadcast its current state by
				// sending command 0x80; the reply arrives here as a 0x05 report and is
				// raised like any stage change. This is how the active stage becomes
				// known at startup without the user touching the button. The reply is
				// buffered on this just-opened handle, so a brief delay is enough for
				// the read loop below to be pumping. See docs/mouse/blaze/dpi-stage.md.
				QueryDeviceState(vid, pid, state.DeviceKey, 150);

				using (handle)
				{
					while (!token.IsCancellationRequested)
					{
						if (ReadFile(handle, buf, (uint)buf.Length, out uint read, IntPtr.Zero))
						{
							// A 0-byte "success" means the handle can't actually deliver data (e.g. the
							// device is owned elsewhere and we opened a no-access handle). Without this
							// guard the loop spins at 100% CPU; break to reopen after the backoff below.
							if (read == 0) break;

							// The 0x05 vendor report carries both the report rate (byte 2)
							// and the active DPI stage (byte 3). A physical DPI-button or
							// polling-button press (and the 0x80 startup query) sends it,
							// so we track and raise both independently. Parsing/validity
							// lives in the unit-tested X43StateReport.
							if (X43StateReport.TryParse(buf.AsSpan(0, (int)read), out int stage, out int hz, out int ledEffect))
							{
								if (stage >= 0 && stage != state.LastStage)
								{
									state.LastStage = stage;
									_logger.LogInformation("[DPI] PID={P:X4} stage={S} path={Path}", pid, stage, path);
									StageChanged?.Invoke(new StageEvent(vid, pid, path, stage, state.DeviceKey));
								}

								if (hz != 0 && hz != state.LastRateHz)
								{
									state.LastRateHz = hz;
									_logger.LogInformation("[RATE] PID={P:X4} raw=0x{R:X2} hz={Hz} path={Path}", pid, buf[2], hz, path);
									ReportRateChanged?.Invoke(new RateEvent(vid, pid, path, hz, state.DeviceKey));
								}

								// Align the device's own service cache (param_0) with reality
								// so a later UI report-rate write preserves the real DPI stage
								// and LED effect. buf: [2]=rate, [3]=stage, [4]=ledEffect.
								if (ledEffect >= 0)
									(_rgbFactory.CreateForDevice(vid, pid, state.DeviceKey) as IMouseRgbService)?.SyncStateFromDevice(buf[2], buf[3], buf[4]);
							}
						}
						else break; // handle closed / device removed
					}
				}
				if (token.IsCancellationRequested) break;
				Thread.Sleep(1000); // brief backoff before trying to reopen
			}
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "[DPI] reader loop error path={Path}", path);
		}
		finally
		{
			_readers.TryRemove(path, out _);
			// Once removed from _readers this loop is the CTS's last owner; shutdown guards its
			// Cancel() against racing this disposal.
			try { state.Cts.Dispose(); } catch { }
			_logger.LogInformation("[DPI] reader stopped PID={P:X4} path={Path}", pid, path);
		}
	}

	// Forces a fresh read of the current DPI stage for every connected device: resets
	// each reader's dedupe state so the next 0x05 report is re-raised (even if the stage
	// hasn't changed), then asks the devices to broadcast. Called when a UI client
	// subscribes so it immediately learns the active stage instead of showing the
	// default. Also ensures readers exist first, in case a client attaches before the
	// periodic refresh has started them.
	public void RequestStateRefresh()
	{
		try { RefreshReaders(); } catch (Exception ex) { _logger.LogWarning(ex, "[DPI] refresh (on-demand) failed"); }

		foreach (var (path, state) in _readers)
		{
			state.LastStage = -1;  // re-raise the next report even if unchanged
			state.LastRateHz = -1;
			var (vid, pid) = ParseVidPid(path);
			if (vid != 0) QueryDeviceState(vid, pid, state.DeviceKey, 0);
		}
	}

	// Fire-and-forget: send command 0x80 so the device pushes its current 0x05 state
	// report. Uses the RGB service (the owner of the command collection) rather than
	// opening a second handle here. delayMs lets the caller wait for the Col04 read
	// loop to be pumping before the reply lands.
	private void QueryDeviceState(int vid, int pid, string deviceKey, int delayMs)
	{
		_ = Task.Run(async () =>
		{
			try
			{
				if (delayMs > 0) await Task.Delay(delayMs);
				var svc = _rgbFactory.CreateForDevice(vid, pid, deviceKey) as IMouseRgbService;
				if (svc != null) await svc.RequestCurrentStateAsync();
			}
			catch (Exception ex)
			{
				_logger.LogDebug(ex, "[DPI] state query failed VID={V:X4} PID={P:X4}", vid, pid);
			}
		});
	}

	private static (int vid, int pid) ParseVidPid(string path)
	{
		var m = VidPidRegex.Match(path);
		if (!m.Success) return (0, 0);
		return (Convert.ToInt32(m.Groups["vid"].Value, 16), Convert.ToInt32(m.Groups["pid"].Value, 16));
	}

	private static SafeFileHandle? OpenShared(string path)
	{
		// GENERIC_READ only. A 0-access handle can't ReadFile the report data — it "succeeds"
		// with 0 bytes and would spin the reader loop — so a failed open returns null instead,
		// letting the reader exit and RefreshReaders retry the real open on its next pass.
		var h = CreateFile(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
			IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
		return h.IsInvalid ? null : h;
	}

	// Enumerate all HID interface paths that are the vendor DPI collection (MI_01 Col04).
	private static List<string> EnumerateCol04Paths()
	{
		var result = new List<string>();
		var guid = HidGuid;
		var set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
		if (set == INVALID_HANDLE_VALUE) return result;
		try
		{
			var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
			for (uint i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, i, ref ifData); i++)
			{
				uint reqSize = 0;
				SetupDiGetDeviceInterfaceDetail(set, ref ifData, IntPtr.Zero, 0, ref reqSize, IntPtr.Zero);
				if (reqSize == 0) continue;

				var detail = Marshal.AllocHGlobal((int)reqSize);
				try
				{
					Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
					if (SetupDiGetDeviceInterfaceDetail(set, ref ifData, detail, reqSize, ref reqSize, IntPtr.Zero))
					{
						var path = Marshal.PtrToStringUni(detail + 4);
						if (path != null && path.Contains("&mi_01&col04", StringComparison.OrdinalIgnoreCase))
							result.Add(path);
					}
				}
				finally { Marshal.FreeHGlobal(detail); }
			}
		}
		finally { SetupDiDestroyDeviceInfoList(set); }
		return result;
	}

	// ── P/Invoke ──────────────────────────────────────────────────────────────
	private static readonly Guid HidGuid = new("4d1e55b2-f16f-11cf-88cb-001111000030");
	private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
	private const uint GENERIC_READ = 0x80000000;
	private const uint FILE_SHARE_READ = 0x00000001;
	private const uint FILE_SHARE_WRITE = 0x00000002;
	private const uint OPEN_EXISTING = 3;
	private const uint DIGCF_PRESENT = 0x02;
	private const uint DIGCF_DEVICEINTERFACE = 0x10;

	[StructLayout(LayoutKind.Sequential)]
	private struct SP_DEVICE_INTERFACE_DATA
	{
		public uint cbSize;
		public Guid InterfaceClassGuid;
		public uint Flags;
		public IntPtr Reserved;
	}

	[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

	[DllImport("setupapi.dll", SetLastError = true)]
	private static extern bool SetupDiEnumDeviceInterfaces(IntPtr devInfo, IntPtr devInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

	[DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr devInfo, ref SP_DEVICE_INTERFACE_DATA ifData, IntPtr detail, uint detailSize, ref uint reqSize, IntPtr devInfoData);

	[DllImport("setupapi.dll", SetLastError = true)]
	private static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfo);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr template);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool ReadFile(SafeFileHandle handle, byte[] buffer, uint toRead, out uint read, IntPtr overlapped);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
}
