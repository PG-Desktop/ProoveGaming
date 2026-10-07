using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using HidSharp;
using Microsoft.Win32.SafeHandles;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;
using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.DeviceHost.Services;

// Listens on every TAIPAN (Telink) vendor collection for the reports the mouse PUSHES —
// battery (0x48) and state (0x49) — and caches the latest battery per unit.
//
// Why listen instead of query: the 2.4G dongle does not relay host→mouse frames (confirmed on
// hardware — see the correction below), and the mouse also volunteers
// a 0x48 battery report on every 2.4G link establishment: dongle plug-in, USB unplug, physical
// mode-switch flip, wake from sleep. Holding the read pipe open and caching the push is therefore
// a battery mechanism over the dongle. GetBatteryLevel answers from this cache.
//
// CORRECTED 2026-08-07 — battery can also be REQUESTED, and that is now the primary path.
//
// The vendor confirmed battery is request/response: send a request and the device replies once; it
// does not repeat without a new request. Verified on hardware — `cmd 0x48` with zero parameters gets
// exactly one reply per request, on the same collection.
//
// The earlier "queries go unanswered" conclusion came from sending the battery query as `0x08`
// (TaipanDongleProbe.BatteryCmd). This protocol reuses ONE opcode for both directions, so the
// request is `0x48`, the same byte as the reply. One wrong byte had hardened into a belief that the
// dongle ignored host->mouse traffic entirely.
//
// Why this matters to a user: passively, battery only appeared after a link-establishment event, so
// a mouse that had been connected for hours showed whatever it reported when it last reconnected —
// or nothing at all on a fresh app start. The request is issued on every refresh pass, so the value
// is current and appears immediately on connect.
//
// The request needs its own handle: the reader opens GENERIC_READ only (see OpenShared), and the
// reply needs no special handling because the reader loop already parses and caches 0x48.
//
// Interface paths (from hardware enumeration):
//   wired  PID 0xFB01 → mi_02&col02 (in=33/out=33)
//   dongle PID 0x8373 → mi_00&col04 (in=33/out=33)
// Same reader-per-path model as DpiStageMonitor: one thread per present interface, readers
// remove themselves when the device disappears and are restarted by the periodic refresh.
public sealed class TaipanStateMonitor : BackgroundService
{
	private const int TelinkVid = 0x248A;

	// The transports this monitor holds readers for, paired with the vendor collection each one
	// exposes. PIDs come from the CATALOG (appsettings `AllowedDevices` → `Transports`), never from
	// constants here: which product IDs a device enumerates under is configuration, and a second
	// copy in code silently stops matching the moment the catalog is edited.
	//
	// The collection suffix stays in code on purpose — `mi_02&col02` / `mi_00&col04` is a hardware
	// fact about where the vendor interface sits on each transport, discovered by enumeration, and
	// the catalog has no field for it.
	//
	// Bluetooth is deliberately NOT tracked: over BLE the mouse exposes the standard GATT Battery
	// Service, which the App reads through the Bluetooth stack and which Windows shows in its tray.
	// That is the mouse's own percentage, so there is nothing for this monitor to add.
	private static readonly (string ConnectionType, string CollectionSuffix)[] TrackedTransports =
	[
		("Wired", "&mi_02&col02"),
		("Dongle2_4G", "&mi_00&col04"),
	];

	// Resolved once on first use rather than in the constructor: the catalog is bound from options
	// during startup, and reading it too early would cache an empty list for the process lifetime.
	// A benign race can compute it twice; both callers get the same values.
	private IReadOnlyList<(int Pid, string CollectionSuffix)>? _trackedInterfaces;

	private IReadOnlyList<(int Pid, string CollectionSuffix)> TrackedInterfaces =>
		_trackedInterfaces ??= ResolveTrackedInterfaces();

	private IReadOnlyList<(int Pid, string CollectionSuffix)> ResolveTrackedInterfaces()
	{
		var resolved = new List<(int Pid, string CollectionSuffix)>();

		foreach (var (connectionType, suffix) in TrackedTransports)
		{
			foreach (var map in _deviceInfoProvider.DevicesMaps)
			{
				if (!string.Equals(map.ConnectionType, connectionType, StringComparison.OrdinalIgnoreCase))
					continue;
				if (!int.TryParse(map.VendorId, out var vid) || vid != TelinkVid)
					continue;
				if (!int.TryParse(map.ProductId, out var pid) || pid <= 0)
					continue;

				if (!resolved.Any(r => r.Pid == pid))
					resolved.Add((pid, suffix));
			}
		}

		if (resolved.Count == 0)
			_logger.LogWarning("[Taipan] No wired/dongle transport found in the device catalog for VID={Vid:X4}; " +
				"this monitor will track nothing. Check AllowedDevices → Transports.", TelinkVid);
		else
			_logger.LogInformation("[Taipan] Tracking {Count} transport(s) from the catalog: {Pids}",
				resolved.Count, string.Join(", ", resolved.Select(r => $"0x{r.Pid:X4}")));

		return resolved;
	}

	public readonly record struct BatteryReading(int Percent, bool UsbConnected, DateTime TimestampUtc);

	// Same shape as DpiStageMonitor's event so DeviceService can stream both families through the
	// one DpiStageChange contract. There is no rate counterpart: TAIPAN has no polling-rate button,
	// so the rate only ever changes because the app wrote it.
	public readonly record struct StageEvent(int VendorId, int ProductId, string DevicePath, int Stage, string DeviceKey);

	// Raised when the mouse pushes a 0x49 state report — i.e. the user pressed the physical DPI
	// button. This is the only live source of the active stage over the dongle, which relays the
	// mouse's pushes but not host queries.
	public event Action<StageEvent>? StageChanged;

	// Raised when a unit's battery reading changes, or stops being available. The App otherwise only
	// learns about battery on its own 60 s poll, so switching a mouse on took up to a minute to show
	// a level the DeviceHost already had in hand. Known=false carries the absent case (dongle
	// answering with a zero reading), so the indicator clears just as promptly as it fills.
	public readonly record struct BatteryEvent(int VendorId, int ProductId, string DeviceKey,
		bool Known, int Percent, bool IsCharging);

	public event Action<BatteryEvent>? BatteryChanged;

	private readonly ILogger<TaipanStateMonitor> _logger;
	private readonly IDeviceRgbServiceFactory _rgbFactory;
	private readonly IHidDeviceProvider _hidDeviceProvider;
	private readonly IDeviceInfoProvider _deviceInfoProvider;

	// Signalled when the HID device list changes, so a newly-connected TAIPAN is picked up at once
	// and the loop can idle (no periodic enumeration) while nothing is plugged in.
	private readonly SemaphoreSlim _deviceListChanged = new(0, 1);

	// How many gRPC clients are currently streaming device events — i.e. how many App instances
	// could actually display a battery value. Incremented/decremented by DeviceService around its
	// event subscriptions.
	private int _consumers;

	// Bounds the battery request rate by TIME rather than by how often the loop happens to wake —
	// it also wakes on every HID device-list change, which on a busy machine is far more often than
	// its 30 s fallback. Shared by the periodic pass and the on-demand path, each asking for its own
	// spacing over the one clock, so the two cannot double up.
	//
	// A RequestThrottle rather than a timestamp field because the claim has to be atomic: the App
	// fires a battery acquire per card, so several gRPC calls arrive together and a read-then-write
	// would let them all through the throttle at once.
	private readonly RequestThrottle _batteryRequests = new();

	// Cancels work started outside ExecuteAsync's own token — the on-demand refresh in particular,
	// which awaits DeviceGate and would otherwise wait unbounded and survive service shutdown.
	private readonly CancellationTokenSource _lifetimeCts = new();

	// TAIPAN is a battery-powered wireless mouse, and every request is a radio frame INTO it. A
	// 30-second cadence is ~2,880 frames a day, and this service is a Windows service that starts
	// with the machine and runs whether or not the App is open — so most of those frames would be
	// refreshing a number nobody is looking at. What that costs the mouse's own power management
	// (does an incoming frame wake it from deep sleep, and at what current?) has never been
	// measured, because until 2026-08-07 we believed the dongle relayed nothing host->mouse at all.
	//
	// So the polling is bounded on both axes: only while a client is attached, and no more often
	// than this. A battery percentage does not meaningfully change inside five minutes.
	//
	// Nothing is lost when no client is attached: the mouse still volunteers a 0x48 push on every
	// 2.4G link establishment, and DeviceService triggers an immediate refresh when a client
	// connects (RequestStateRefresh), so an App that opens after hours of silence gets a current
	// value straight away rather than waiting for the next tick.
	private static readonly TimeSpan BatteryRequestInterval = TimeSpan.FromMinutes(5);

	// Outer deadline for the battery request's blocking driver calls. Generous next to a HID write
	// that normally returns in single-digit milliseconds — the point is to bound a hang, not to
	// abort slow-but-working hardware.
	private static readonly TimeSpan WriteDeadline = TimeSpan.FromMilliseconds(500);

	// Latest battery per (vid, pid, deviceKey). A merged-transport product pushes under whichever
	// transport is live, so lookups fall back to the newest reading for the vendor id.
	private readonly ConcurrentDictionary<(int Vid, int Pid, string Key), BatteryReading> _battery = new();

	// Active readers keyed by device interface path.
	private readonly ConcurrentDictionary<string, ReaderState> _readers = new(StringComparer.OrdinalIgnoreCase);

	private static readonly Regex VidPidRegex =
		new(@"vid_(?<vid>[0-9a-f]{4})&pid_(?<pid>[0-9a-f]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

	private sealed class ReaderState
	{
		public required CancellationTokenSource Cts { get; init; }
		public string DeviceKey { get; init; } = string.Empty;
		public SafeFileHandle? Handle { get; set; }
		// De-dupe so a repeated push doesn't re-raise; -1 = nothing seen yet.
		public int LastStage { get; set; } = -1;
	}

	public TaipanStateMonitor(ILogger<TaipanStateMonitor> logger, IDeviceRgbServiceFactory rgbFactory,
		IHidDeviceProvider hidDeviceProvider, IDeviceInfoProvider deviceInfoProvider)
	{
		_logger = logger;
		_rgbFactory = rgbFactory;
		_hidDeviceProvider = hidDeviceProvider;
		_deviceInfoProvider = deviceInfoProvider;
	}

	// Latest cached battery for a unit. Exact (vid,pid,key) match first; otherwise the newest
	// reading for the vendor id — a merged-transport mouse pushes on whichever transport is live,
	// while callers query with the routing identity, so the identities legitimately differ.
	public bool TryGetBattery(int vendorId, int productId, string? deviceKey, out BatteryReading reading)
	{
		if (_battery.TryGetValue((vendorId, productId, deviceKey ?? string.Empty), out reading))
			return true;

		// The fallback below answers across device KEYS (a port move changes the container ID, so an
		// exact match can miss for a mouse we are still reading). It must not answer across
		// TRANSPORTS: asked about a transport this monitor does not watch, the only honest answer is
		// "no reading", not the newest reading from a different one.
		//
		// Concretely, this is the Bluetooth bug (2026-08-13): with the dongle still plugged in and
		// the mouse switched to BLE, a battery request for PID 0x8266 fell through to the newest
		// dongle reading. That reading is computed with the vendor's protocol curve, whose 985 "full"
		// point is below what real hardware pushes (1008 seen), so it clamps to 100 % — the App
		// showed a permanent 100 % while Windows' own tray, reading GATT, showed 93 %. Two different
		// sources on two different scales, one of them answering for a transport it knew nothing
		// about.
		if (!TrackedInterfaces.Any(t => t.Pid == productId))
		{
			reading = default;
			return false;
		}

		var newest = _battery.Where(kv => kv.Key.Vid == vendorId)
			.OrderByDescending(kv => kv.Value.TimestampUtc)
			.Select(kv => kv.Value)
			.FirstOrDefault();

		reading = newest;
		return newest.TimestampUtc != default;
	}

	// Cancelled here rather than in Dispose so an in-flight on-demand refresh stops when the host is
	// told to stop, not when the object is finally collected.
	public override async Task StopAsync(CancellationToken cancellationToken)
	{
		_lifetimeCts.Cancel();
		await base.StopAsync(cancellationToken);
	}

	public override void Dispose()
	{
		_lifetimeCts.Cancel();
		_lifetimeCts.Dispose();
		base.Dispose();
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		if (!OperatingSystem.IsWindows()) return;

		// The loop notices devices arriving and refreshes battery. Wake on the HID list change
		// (instant pick-up) and otherwise idle on a long fallback instead of enumerating every 2s.
		// Stored in a local so sub/unsub use the same delegate instance.
		EventHandler<DeviceListChangedEventArgs> onDeviceListChanged = (_, _) =>
		{
			if (_deviceListChanged.CurrentCount == 0)
			{
				try { _deviceListChanged.Release(); } catch (SemaphoreFullException) { }
			}
		};
		_hidDeviceProvider.SubscribeToDeviceListChanged(onDeviceListChanged);

		_logger.LogInformation("[Taipan] state monitor started");
		try
		{
			while (!stoppingToken.IsCancellationRequested)
			{
				try { RefreshReaders(); }
				catch (Exception ex) { _logger.LogWarning(ex, "[Taipan] reader refresh failed"); }

				// Ask every connected unit for its battery — but only while an App is attached to
				// read the answer, and at most once per BatteryRequestInterval. Replies land in the
				// reader loop, which already parses and caches them, so nothing here waits.
				if (HasConsumer && _batteryRequests.TryAcquire(BatteryRequestInterval))
				{
					try { await RequestBatteryFromAllAsync(stoppingToken); }
					catch (OperationCanceledException) { break; }
					catch (Exception ex) { _logger.LogDebug(ex, "[Taipan] battery request pass failed"); }
				}

				try { await _deviceListChanged.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken); }
				catch (OperationCanceledException) { break; }
			}
		}
		finally
		{
			_hidDeviceProvider.UnsubscribeFromDeviceListChanged(onDeviceListChanged);

			foreach (var reader in _readers.Values)
			{
				reader.Cts.Cancel();
				try { if (reader.Handle is { IsInvalid: false }) CancelIoEx(reader.Handle, IntPtr.Zero); } catch { }
			}
		}
	}

	// Reports each connected unit's active DPI stage so a freshly-subscribed client shows the mouse's
	// state instead of its own last-known value.
	//
	// Prefers the stage the host KNOWS (from a 0x49 push or a stage the app set), which is a fact.
	// Only when nothing is known does it fall back to the MsConfig bootstrap guess — that value is
	// off by one whenever the app, rather than the button, last set the stage, so it is reported for
	// display but never stored as the known stage. The app asserts its persisted stage on attach,
	// which is what makes the known branch the normal one. Cable only; over the dongle there is
	// nothing to read and the display corrects itself on the first button press.
	//
	// 2026-07-30: a retry loop was added here as a fix for "TAIPAN preset apply does nothing until
	// the DPI button is pressed" and reverted — it didn't help (confirming the cause isn't read
	// timing) and added perceptible lag by holding TelinkMouseService's write lock in longer chunks,
	// which the App's own apply calls queue behind. See docs/mouse/taipan/protocol.md Open item 7.
	// Called by DeviceService when a gRPC client starts / stops streaming device events. Battery
	// polling only runs while at least one client is attached — see BatteryRequestInterval.
	public void AddConsumer()
	{
		// Zero-crossings are logged so a consumer-count desync (battery silently frozen for the
		// rest of the session) is diagnosable from the log instead of leaving no trace at all.
		if (Interlocked.Increment(ref _consumers) == 1)
			_logger.LogDebug("[Taipan] first consumer attached — battery polling enabled");
	}

	public void RemoveConsumer()
	{
		// Clamp at zero rather than trusting pairing: an unbalanced decrement would make the count
		// negative and disable battery polling for the rest of the process, which is a silent
		// failure that would be very hard to trace back here.
		var count = Interlocked.Decrement(ref _consumers);
		if (count < 0)
		{
			Interlocked.Exchange(ref _consumers, 0);
			_logger.LogDebug("[Taipan] unpaired RemoveConsumer clamped at zero");
		}
		else if (count == 0)
		{
			_logger.LogDebug("[Taipan] last consumer detached — battery polling disabled");
		}
	}

	private bool HasConsumer => Volatile.Read(ref _consumers) > 0;

	// Mirrors DpiStageMonitor.RequestStateRefresh.
	public void RequestStateRefresh()
	{
		try { RefreshReaders(); }
		catch (Exception ex) { _logger.LogWarning(ex, "[Taipan] refresh (on-demand) failed"); }

		// A client just attached, so the cached battery value may be hours old. Ask once now — this
		// is what lets the periodic poll be as slow as it is without the App showing a stale number
		// when it opens.
		_ = Task.Run(async () =>
		{
			try { await RequestBatteryFromAllAsync(CancellationToken.None); }
			catch (Exception ex) { _logger.LogDebug(ex, "[Taipan] on-demand battery request failed"); }
		});

		foreach (var (path, state) in _readers)
		{
			var (vid, pid) = ParseVidPid(path);
			if (vid == 0) continue;

			_ = Task.Run(async () =>
			{
				try
				{
					if (_rgbFactory.CreateForDevice(vid, pid, state.DeviceKey) is not TelinkMouseService service)
						return;

					var known = service.KnownDpiStage;
					var stage = known != TaipanMsConfig.UnknownStage
						? known
						: await service.TryInferActiveDpiStageAsync() ?? TaipanMsConfig.UnknownStage;

					if (stage == TaipanMsConfig.UnknownStage) return;

					state.LastStage = stage;
					StageChanged?.Invoke(new StageEvent(vid, pid, path, stage, state.DeviceKey));
				}
				catch (Exception ex)
				{
					_logger.LogDebug(ex, "[Taipan][DPI] stage refresh failed PID={P:X4}", pid);
				}
			});
		}
	}

	private void RefreshReaders()
	{
		foreach (var path in EnumerateTaipanVendorPaths())
		{
			if (_readers.ContainsKey(path)) continue;

			var (vid, pid) = ParseVidPid(path);
			if (vid == 0) continue;

			var cts = new CancellationTokenSource();
			var state = new ReaderState { Cts = cts, DeviceKey = DeviceContainerId.TryGet(path) ?? string.Empty };
			if (!_readers.TryAdd(path, state)) continue;

			var thread = new Thread(() => ReaderLoop(path, vid, pid, state, cts.Token))
			{
				IsBackground = true,
				Name = $"TaipanReader:{pid:X4}",
			};
			_logger.LogInformation("[Taipan] start reader VID={V:X4} PID={P:X4} path={Path}", vid, pid, path);
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
				if (handle == null || handle.IsInvalid) break;

				state.Handle = handle;
				var buf = new byte[TaipanStateReport.Length + 1];

				using (handle)
				{
					while (!token.IsCancellationRequested)
					{
						if (!ReadFile(handle, buf, (uint)buf.Length, out uint read, IntPtr.Zero))
							break; // handle closed / device removed

						// A 0-byte "success" means the handle can't actually deliver data (e.g. the
						// device is owned elsewhere and we opened a no-access handle). Without this
						// guard the loop spins at 100% CPU; break to reopen after the backoff below.
						if (read == 0) break;

						var span = buf.AsSpan(0, (int)read);
						if (TaipanStateReport.TryParseBattery(span, out int raw, out bool usb))
						{
							int percent = TaipanStateReport.PercentFromRaw(raw);
							var key = (vid, pid, state.DeviceKey);
							var changed = !_battery.TryGetValue(key, out var previous)
								|| previous.Percent != percent
								|| previous.UsbConnected != usb;

							_battery[key] = new BatteryReading(percent, usb, DateTime.UtcNow);

							// Only on a real change: the mouse re-pushes the same level on every link
							// establishment, and an unchanged value is not news for the UI.
							if (changed)
								BatteryChanged?.Invoke(new BatteryEvent(vid, pid, state.DeviceKey, true, percent, usb));
							// Raw is logged unconverted: the "raw x 4 = millivolts" reading was our own
							// inference and the vendor has never confirmed the unit. Their percentage
							// curve (T6) works on the raw value directly, so mV is not needed and
							// printing an unverified one only invites it to be trusted.
							_logger.LogInformation("[Taipan] battery push raw={Raw} => {Pct}% usb={Usb} PID={P:X4}",
								raw, percent, usb, pid);
						}
						else if (TaipanStateReport.IsMouseAbsentReply(span))
						{
							// The dongle answered for a mouse that is not there — switched off, out
							// of range, or flat. Drop the cached level rather than keep serving it:
							// the receiver stays plugged in and enumerated, so without this the App
							// showed the last percentage the mouse ever reported, indefinitely.
							if (_battery.TryRemove((vid, pid, state.DeviceKey), out _))
							{
								BatteryChanged?.Invoke(new BatteryEvent(vid, pid, state.DeviceKey, false, 0, false));
								_logger.LogInformation(
									"[Taipan] mouse not present behind the dongle (battery reply carried no reading); " +
									"cached level dropped PID={P:X4}", pid);
							}
						}
						else if (TaipanStateReport.TryParseState(span, out int profile, out int stage, out byte rate))
						{
							_logger.LogDebug("[Taipan] state push profile={Pr} stage={St} para3=0x{Rt:X2} PID={P:X4}",
								profile, stage, rate, pid);

							// ONLY the DPI stage is taken from this push. TAIPAN has no polling-rate
							// button, so the rate can never change on the device — and Para3 is a
							// constant (0x01 in every capture), not the live rate. Decoding it as one
							// snapped the UI back to 1000 Hz on every DPI-button press.
							if (stage != state.LastStage)
							{
								state.LastStage = stage;
								_logger.LogInformation("[Taipan][DPI] stage={Stage} PID={P:X4}", stage, pid);
								StageChanged?.Invoke(new StageEvent(vid, pid, path, stage, state.DeviceKey));
							}

							// Keep the device service's live stage aligned. MsConfig's own copy of the
							// stage lags reality (the device flushes it on its own schedule), so without
							// this a later MsConfig write — e.g. a polling-rate change — would write the
							// stale stage back and snap the mouse to a different DPI.
							(_rgbFactory.CreateForDevice(vid, pid, state.DeviceKey) as IMouseRgbService)
								?.SyncStateFromDevice(rate, (byte)stage, 0);
						}
					}
				}
				if (token.IsCancellationRequested) break;
				Thread.Sleep(1000); // brief backoff before trying to reopen
			}
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "[Taipan] reader loop error path={Path}", path);
		}
		finally
		{
			_readers.TryRemove(path, out _);
			_logger.LogInformation("[Taipan] reader stopped PID={P:X4} path={Path}", pid, path);
		}
	}

	// All present TAIPAN vendor-collection paths (wired mi_02&col02, dongle mi_00&col04).
	// Instance, not static: the paths it matches are built from catalog-resolved PIDs.
	private List<string> EnumerateTaipanVendorPaths()
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
						if (path == null || !path.Contains("vid_248a", StringComparison.OrdinalIgnoreCase))
							continue;
						// Built from the catalog PIDs rather than literals, so a catalog edit moves
						// this filter with it instead of leaving it quietly matching nothing.
						foreach (var (pid, suffix) in TrackedInterfaces)
						{
							if (path.Contains($"pid_{pid:x4}{suffix}", StringComparison.OrdinalIgnoreCase))
							{
								result.Add(path);
								break;
							}
						}
					}
				}
				finally { Marshal.FreeHGlobal(detail); }
			}
		}
		finally { SetupDiDestroyDeviceInfoList(set); }
		return result;
	}

	private static (int vid, int pid) ParseVidPid(string path)
	{
		var m = VidPidRegex.Match(path);
		if (!m.Success) return (0, 0);
		return (Convert.ToInt32(m.Groups["vid"].Value, 16), Convert.ToInt32(m.Groups["pid"].Value, 16));
	}

	// Minimum spacing for on-demand requests, so a chatty client cannot turn every gRPC call into a
	// radio frame at the mouse. Shares _lastBatteryRequestUtc with the periodic pass, so the two
	// paths cannot double up — whichever asks most recently sets the clock for both.
	private static readonly TimeSpan OnDemandRequestSpacing = TimeSpan.FromSeconds(20);

	/// <summary>
	/// Asks the connected units for their battery now, if one has not been asked recently.
	/// Fire-and-forget: the reply lands on the reader thread, which caches it and raises
	/// <see cref="BatteryChanged"/>.
	/// </summary>
	/// <remarks>
	/// Called when a client actually asks for a level (DeviceService.GetBatteryLevel), which is the
	/// event that means someone is looking at it. The periodic pass alone was not enough for the
	/// mouse-absent case: the dongle only reveals that no mouse is behind it IN RESPONSE TO A
	/// REQUEST, so with requests spaced five minutes apart a mouse switched off could keep its last
	/// percentage on screen for that long (observed 2026-08-17 — the App still showed a level two
	/// minutes after switch-off, and the log carried no absent-reply detection at all because no
	/// request had gone out).
	///
	/// This keeps the rule the five-minute interval was introduced for — never poll a battery-powered
	/// mouse just because the service is running — while tying the traffic to real demand instead of
	/// a fixed clock. When the mouse is off, the request wakes nothing; when it is on, this costs one
	/// frame per client poll at most.
	/// </remarks>
	public void RequestBatteryRefresh(int vendorId)
	{
		if (vendorId != TelinkVid) return;
		if (!_batteryRequests.TryAcquire(OnDemandRequestSpacing)) return;

		// The service's lifetime token, NOT CancellationToken.None: RequestBatteryFromAllAsync
		// awaits DeviceGate, whose wait is unbounded, so with None a request that arrived while a
		// configuration write held the gate would keep waiting after the host had been told to stop.
		//
		// Deliberately NOT gated on HasConsumer, unlike the periodic pass. That flag counts clients
		// streaming device events; this runs because a client just ASKED for a battery level, which
		// is a stronger statement that someone is looking at one — and a client may legitimately
		// query without subscribing to the stream.
		var token = _lifetimeCts.Token;

		_ = Task.Run(async () =>
		{
			try { await RequestBatteryFromAllAsync(token); }
			catch (OperationCanceledException) { }
			catch (Exception ex) { _logger.LogDebug(ex, "[Taipan] on-demand battery request failed"); }
		}, token);
	}

	// Sends the documented battery request to every device we currently hold a reader for. The reply
	// arrives asynchronously on the reader thread and is cached there, so nothing here waits on it.
	private async Task RequestBatteryFromAllAsync(CancellationToken cancellationToken)
	{
		foreach (var (path, _) in _readers)
		{
			cancellationToken.ThrowIfCancellationRequested();
			await RequestBatteryAsync(path, cancellationToken);
		}
	}

	// One battery request (cmd 0x48, no parameters). Opens its own write handle because the reader's
	// is GENERIC_READ only. Failures are debug-level and non-fatal — a unit that has just gone away
	// is the ordinary case, and the passive push path still covers link-establishment updates.
	//
	// Takes the shared DeviceGate for the path first. This interface is also where TelinkMouseService
	// writes configuration, and a config write is a sequenced multi-frame flash transaction: 0xD1
	// erases the sector, 0xD2 chunks fill it, ValidBankID and the 0xD3 commit close it. Dropping a
	// battery frame between those would interleave a foreign command into a half-written flash bank,
	// on the one transport (the cable) where those writes actually land. Battery is refreshed every
	// 30 seconds for the life of the process, so "the windows are small" is not a defence.
	private async Task RequestBatteryAsync(string path, CancellationToken cancellationToken)
	{
		try
		{
			using var deviceGate = await DeviceGate.AcquireAsync(path, cancellationToken);

			// CreateFile and WriteFile are blocking P/Invokes with no timeout and no overlapped
			// mode, and they run INSIDE the device gate. A driver that never returns — which this
			// family has already been seen to do on the read side, see TelinkMouseService's
			// ReadTableAsync deadline — would pin the gate for the life of the process, and every
			// later TAIPAN write (button remap, DPI table, polling rate) would queue behind it
			// forever. So race them against a hard outer deadline: a stuck call keeps blocking in
			// the background, but this method, and the gate, move on.
			//
			// The task OWNS the handle and disposes it itself. Disposing it here would close a
			// handle that an abandoned, still-running WriteFile is using — the fix would introduce
			// a worse bug than the one it closes. An abandoned handle leaks until its thread
			// unblocks, which is the accepted cost of not blocking forever.
			var write = Task.Run(() =>
			{
				using var handle = CreateFile(path, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
					IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

				if (handle.IsInvalid) return false;

				var frame = TaipanFrame.Build(TaipanStateReport.BatteryRequest, 0, 0, 0, 0, ReadOnlySpan<byte>.Empty);
				return WriteFile(handle, frame, (uint)frame.Length, out _, IntPtr.Zero);
			}, CancellationToken.None);

			// The token goes on the WAIT, not on Task.Run: cancellation cannot interrupt a blocking
			// P/Invoke that has already started, so passing it to Task.Run would only prevent the
			// task ever starting.
			if (!await write.WaitAsync(WriteDeadline, cancellationToken))
				_logger.LogDebug("[Taipan] battery request write failed for {Path}", path);
		}
		catch (TimeoutException)
		{
			// The write is wedged in the driver. Worth a warning rather than debug: it is the
			// signal that the transport is unhealthy, and it is exactly the condition this
			// deadline exists to survive.
			_logger.LogWarning("[Taipan] battery request timed out after {Ms}ms for {Path}",
				WriteDeadline.TotalMilliseconds, path);
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger.LogDebug(ex, "[Taipan] battery request failed for {Path}", path);
		}
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

	// ── P/Invoke ──────────────────────────────────────────────────────────────
	private static readonly Guid HidGuid = new("4d1e55b2-f16f-11cf-88cb-001111000030");
	private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
	private const uint GENERIC_READ = 0x80000000;
	private const uint GENERIC_WRITE = 0x40000000;
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
	private static extern bool WriteFile(SafeFileHandle handle, byte[] buffer, uint toWrite, out uint written, IntPtr overlapped);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);
}
