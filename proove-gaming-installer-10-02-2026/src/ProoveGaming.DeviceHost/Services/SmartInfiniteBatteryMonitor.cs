using System.Collections.Concurrent;
using HidSharp;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.DeviceHost.Services;

// Polls battery and charge state over the 2.4G dongle for Smart Infinite keyboards (Rampage
// today) and pushes changes through the existing BatteryChange contract, so the card's ⚡
// indicator follows the USB charging cable in seconds instead of waiting for the App's 60 s poll.
//
// Why polling: a charge-only cable enumerates no wired interface, so the only signal that the
// cable moved is the charge flag in the 0x1F battery reply — and Smart Infinite boards don't
// volunteer battery reports, 0x1F is request/response (measured 2026-08-17 and daily since).
// There is nothing to listen to, so this is the CosmoStateMonitor shape: poll while a tracked
// device is present, park on the device-list signal while none is.
//
// Lightweight-query constraint (plan, 2026-09-02): every tick must stay inside process memory
// except the single HID exchange. Presence comes from the discovery singleton's in-memory HID
// list (IsHidDevicePresent), the read is TryGetBatteryLevel's open-by-cached-path + one write +
// one read, and nothing here may ever call DeviceInformation / the Device Association Framework,
// WMI, or BluetoothDeviceDiscoveryService — DAF enumeration is the DeviceHost's known heavy path
// (docs/audits/2026-09-02-device-association-memory-audit.md).
public sealed class SmartInfiniteBatteryMonitor : BackgroundService
{
	// Cadence while a tracked keyboard is enumerated. 3 s keeps cable-in/cable-out latency under
	// ~10 s with the two-poll charge debounce, at ~1200 radio wakes/hour — see the plan's Risks
	// for the fallback knobs (raise to 5 s, or fast-then-back-off) if QA observes battery drain.
	// Internal for the ComputeCadence tests.
	internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

	// Fallback cadence while NO tracked device is present, no gRPC client is subscribed, or the
	// present device has stopped answering (see FailedTicksBeforeBackoff). The loop normally
	// waits on the HID device-list signal in the absent state; this only covers a change
	// notification that never arrives. Same signal-plus-safety-net shape as CosmoStateMonitor.
	internal static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(30);

	// Review round 1, S1: the 2.4G receiver stays enumerated with the keyboard switched OFF, so
	// presence alone would keep the 3 s cadence while every read fails — and with no valid
	// reading the path cache never populates, so each failing tick is a full candidate scan
	// with up-to-2 s blocking timeouts and a dozen log lines. After this many consecutive
	// no-reading ticks on a present device the loop falls back to IdleInterval; any valid
	// reading or a device-list change restores the fast cadence. Five ticks (~15 s) rather
	// than two so a multi-second key-table/macro write holding the DeviceGate over a few ticks
	// never triggers the backoff.
	internal const int FailedTicksBeforeBackoff = 5;

	// How fresh a cached reading has to be for DeviceService.GetBatteryLevel to answer from it
	// instead of performing its own HID exchange. Just over three poll ticks, so a couple of
	// gate-yielded polls don't push the App back to the slow path.
	public static readonly TimeSpan CacheFreshness = TimeSpan.FromSeconds(10);

	// A single 64-byte reply missing the charge byte must not flicker ⚡ off, so a charge-flag
	// transition has to hold for two consecutive polls (~3-6 s) — the Cosmo two-poll rule.
	// Level changes are NOT debounced: a wrong percentage for one poll is invisible, a
	// flickering icon is not.
	internal const int ChargeConfirmationsRequired = 2;

	private const string DongleConnectionType = "Dongle2_4G";

	// A catalog row this monitor polls: a 2.4G dongle transport with a declared, usable battery
	// command. Resolved from the expanded DeviceMap rows, never from VID constants in code.
	internal readonly record struct TrackedDevice(int VendorId, int ProductId, string Name);

	// The confirmed reading plus the charge-flip debounce for one device. HasReading=false is
	// the "nothing valid seen yet" state; PendingCount=0 means no flip is being debounced.
	internal readonly record struct DebounceState(
		bool HasReading, int Percent, bool IsCharging, bool PendingCharging, int PendingCount);

	// Reuses TaipanStateMonitor's event shape so DeviceService streams both families through the
	// one SendBatteryChangedAsync path. DeviceKey stays empty: FindCardForBatteryPush falls back
	// to VID/PID, and Rampage is a merged-transport card matched by name/ids.
	public event Action<TaipanStateMonitor.BatteryEvent>? BatteryChanged;

	private readonly ILogger<SmartInfiniteBatteryMonitor> _logger;
	private readonly IHidDeviceProvider _hidDeviceProvider;
	private readonly IHidDeviceDiscoveryService _hidDeviceDiscoveryService;
	private readonly IDeviceInfoProvider _deviceInfoProvider;

	// Signalled when the HID device list changes, so a freshly-plugged dongle is picked up at
	// once and the loop can idle (no periodic work) while none is present.
	private readonly SemaphoreSlim _deviceListChanged = new(0, 1);

	// (vid, pid) -> debounce state + when the last VALID reading landed. Written by the poll
	// loop, read from gRPC handler threads via TryGetBattery.
	private readonly ConcurrentDictionary<(int Vid, int Pid), (DebounceState State, DateTime TimestampUtc)> _state = new();

	// How many gRPC clients are currently streaming device events — i.e. how many App instances
	// could actually display a reading. Incremented/decremented by DeviceService around its
	// event subscriptions, exactly like TaipanStateMonitor's pair (review round 1, S2): the
	// DeviceHost is a Windows Service, so with the App closed a poll would wake the keyboard's
	// radio for readings nobody consumes.
	private int _consumers;

	// Consecutive polls where a tracked device was present but no valid reading arrived. Only
	// the loop thread touches it. See FailedTicksBeforeBackoff.
	private int _consecutiveFailures;

	// Resolved once on first use rather than in the constructor: the catalog is bound from
	// options during startup, and reading it too early would cache an empty list for the process
	// lifetime. A benign race can compute it twice; both callers get the same values.
	private IReadOnlyList<TrackedDevice>? _trackedDevices;

	private IReadOnlyList<TrackedDevice> TrackedDevices => _trackedDevices ??= ResolveTracked();

	public SmartInfiniteBatteryMonitor(ILogger<SmartInfiniteBatteryMonitor> logger,
		IHidDeviceProvider hidDeviceProvider,
		IHidDeviceDiscoveryService hidDeviceDiscoveryService,
		IDeviceInfoProvider deviceInfoProvider)
	{
		_logger = logger;
		_hidDeviceProvider = hidDeviceProvider;
		_hidDeviceDiscoveryService = hidDeviceDiscoveryService;
		_deviceInfoProvider = deviceInfoProvider;
	}

	// Called by DeviceService when a gRPC client starts / stops streaming device events. The
	// poll only runs while at least one client is attached — mirroring TaipanStateMonitor's
	// consumer gating, zero-crossings logged so a count desync is diagnosable from the log.
	public void AddConsumer()
	{
		if (Interlocked.Increment(ref _consumers) == 1)
			_logger.LogDebug("[SmartInfinite] first consumer attached — battery polling enabled");
	}

	public void RemoveConsumer()
	{
		// Clamp at zero rather than trusting pairing: an unbalanced decrement would disable
		// polling for the rest of the process — a silent failure that would be very hard to
		// trace back here.
		var count = Interlocked.Decrement(ref _consumers);
		if (count < 0)
		{
			Interlocked.Exchange(ref _consumers, 0);
			_logger.LogDebug("[SmartInfinite] unpaired RemoveConsumer clamped at zero");
		}
		else if (count == 0)
		{
			_logger.LogDebug("[SmartInfinite] last consumer detached — battery polling disabled");
		}
	}

	private bool HasConsumer => Volatile.Read(ref _consumers) > 0;

	// The monitor's confirmed reading for a unit, if one landed within maxAge. Lets a gRPC
	// handler answer without repeating the HID exchange this loop already performs every few
	// seconds — mirroring the Taipan (TryGetBattery) and Cosmo (TryGetLastReport) branches.
	public bool TryGetBattery(int vendorId, int productId, TimeSpan maxAge,
		out (int Percent, bool IsCharging) reading)
	{
		if (_state.TryGetValue((vendorId, productId), out var cached)
			&& cached.State.HasReading
			&& DateTime.UtcNow - cached.TimestampUtc <= maxAge)
		{
			reading = (cached.State.Percent, cached.State.IsCharging);
			return true;
		}

		reading = default;
		return false;
	}

	// Every expanded catalog row with a 2.4G dongle transport, a usable battery and a declared
	// battery command — today exactly Rampage's dongle row (VID 1389 / PID 49271, command 31).
	// Taipan (no BatteryCommand), Cosmo (ditto), wired and Bluetooth rows fall out naturally,
	// which is the guard that this poll can never touch a device another monitor owns — pinned
	// by SmartInfiniteBatteryMonitorTests against the real catalog.
	internal static List<TrackedDevice> ResolveTrackedDevices(IEnumerable<DeviceMap> maps)
	{
		var resolved = new List<TrackedDevice>();

		foreach (var map in maps)
		{
			if (!string.Equals(map.ConnectionType, DongleConnectionType, StringComparison.OrdinalIgnoreCase))
				continue;
			if (!map.SupportsBattery || map.BatteryCommand is null)
				continue;
			if (!int.TryParse(map.VendorId, out var vid) || vid <= 0)
				continue;
			if (!int.TryParse(map.ProductId, out var pid) || pid <= 0)
				continue;

			if (!resolved.Any(d => d.VendorId == vid && d.ProductId == pid))
				resolved.Add(new TrackedDevice(vid, pid, map.Name));
		}

		return resolved;
	}

	// The debounce table, pure so the tests can pin it:
	// - an invalid reading (-1: gate yielded, transient read failure, device gone mid-tick) is
	//   NO EVIDENCE — no push, cache kept, flip counter reset. Clearing on absence stays with
	//   the App's existing logic (disconnect events, poll-miss streak); this monitor only
	//   fast-forwards good readings.
	// - the first-ever valid reading pushes immediately: there is no previous value to disagree
	//   with, so waiting would delay the card for no benefit.
	// - a level change with a valid reading pushes immediately.
	// - a charge-flag transition pushes only after ChargeConfirmationsRequired consecutive
	//   agreeing reads; a flip that reverts after one read produces no push.
	internal static (DebounceState Next, bool Push) ComputePush(DebounceState previous, int percent, bool isCharging)
	{
		if (percent is < 1 or > 100)
			return (previous with { PendingCharging = false, PendingCount = 0 }, false);

		if (!previous.HasReading)
			return (new DebounceState(true, percent, isCharging, false, 0), true);

		var next = previous;
		var push = false;

		if (percent != previous.Percent)
		{
			next = next with { Percent = percent };
			push = true;
		}

		if (isCharging == previous.IsCharging)
		{
			next = next with { PendingCharging = false, PendingCount = 0 };
		}
		else
		{
			var count = previous.PendingCount > 0 && previous.PendingCharging == isCharging
				? previous.PendingCount + 1
				: 1;

			if (count >= ChargeConfirmationsRequired)
			{
				next = next with { IsCharging = isCharging, PendingCharging = false, PendingCount = 0 };
				push = true;
			}
			else
			{
				next = next with { PendingCharging = isCharging, PendingCount = count };
			}
		}

		return (next, push);
	}

	// The backoff rule (review round 1, S1), pure so the tests can pin it: a valid reading or
	// an absent device resets the failure count; a present device that keeps yielding nothing
	// (keyboard switched off behind an enumerated receiver, a wedged interface) slows the loop
	// to IdleInterval after FailedTicksBeforeBackoff consecutive misses, instead of running a
	// full failing candidate scan every 3 s forever. The device-list signal resets the count in
	// the loop itself, since a list change is external evidence worth a fast retry.
	internal static (int NextConsecutiveFailures, TimeSpan NextWait) ComputeCadence(
		bool devicePresent, bool gotValidReading, int consecutiveFailures)
	{
		if (!devicePresent)
			return (0, IdleInterval);

		if (gotValidReading)
			return (0, PollInterval);

		var failures = consecutiveFailures + 1;
		return (failures, failures >= FailedTicksBeforeBackoff ? IdleInterval : PollInterval);
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		// Wake the loop the moment the HID device list changes, so a freshly-plugged dongle is
		// picked up at once rather than after the idle fallback. Stored in a local so subscribe
		// and unsubscribe use the same delegate instance.
		EventHandler<DeviceListChangedEventArgs> onDeviceListChanged = (_, _) =>
		{
			if (_deviceListChanged.CurrentCount == 0)
			{
				try { _deviceListChanged.Release(); } catch (SemaphoreFullException) { }
			}
		};
		_hidDeviceProvider.SubscribeToDeviceListChanged(onDeviceListChanged);

		_logger.LogInformation("[SmartInfinite] battery monitor started");
		try
		{
			while (!stoppingToken.IsCancellationRequested)
			{
				TimeSpan interval;

				// Zero consumers = nobody can see a reading: pushes have no stream to land on
				// and GetBatteryLevel is not being called, so a poll would wake the keyboard's
				// radio for nothing (the DeviceHost runs whether or not the App is open). Same
				// consumer gating as TaipanStateMonitor's battery pass.
				if (!HasConsumer)
				{
					_consecutiveFailures = 0;
					interval = IdleInterval;
				}
				else
				{
					bool devicePresent;
					bool gotValidReading;
					try
					{
						(devicePresent, gotValidReading) = Poll();
					}
					catch (Exception ex)
					{
						// A monitor must never take the host down; a failed poll simply leaves
						// the last confirmed state in place, and an unreadable list idles
						// rather than spins.
						_logger.LogDebug(ex, "[SmartInfinite] battery poll failed");
						(devicePresent, gotValidReading) = (false, false);
					}

					(_consecutiveFailures, interval) = ComputeCadence(devicePresent, gotValidReading, _consecutiveFailures);

					// Exactly once per entry into the backed-off state (the count keeps
					// growing past the threshold), so the log explains the cadence change
					// without repeating itself every 30 s.
					if (_consecutiveFailures == FailedTicksBeforeBackoff)
						_logger.LogInformation(
							"[SmartInfinite] no valid reading for {Ticks} consecutive polls (keyboard off behind the receiver?) — backing off to {Idle}s",
							FailedTicksBeforeBackoff, IdleInterval.TotalSeconds);
				}

				try
				{
					// A device-list change is external evidence the world moved (dongle
					// replugged, wired interface appeared): wake at once AND retry at the fast
					// cadence even if reads were failing.
					if (await _deviceListChanged.WaitAsync(interval, stoppingToken))
						_consecutiveFailures = 0;
				}
				catch (OperationCanceledException) { return; }
			}
		}
		finally
		{
			_hidDeviceProvider.UnsubscribeFromDeviceListChanged(onDeviceListChanged);
		}
	}

	// Returns whether any tracked device is currently enumerated and whether any of them
	// produced a valid reading this pass — the two inputs ComputeCadence turns into the loop's
	// next wait. There is nothing to poll for while none is present.
	private (bool AnyPresent, bool AnyValidReading) Poll()
	{
		var anyPresent = false;
		var anyValidReading = false;

		foreach (var device in TrackedDevices)
		{
			var key = (device.VendorId, device.ProductId);

			// Presence is answered from the discovery singleton's in-memory HID list — never
			// from an enumeration API (the lightweight-query constraint).
			if (!_hidDeviceDiscoveryService.IsHidDevicePresent(device.VendorId, device.ProductId))
			{
				// Dongle unplugged: drop the state so a later re-plug starts clean (the first
				// reading after it pushes immediately) rather than debouncing against a stale
				// pre-unplug value.
				_state.TryRemove(key, out _);
				continue;
			}

			anyPresent = true;

			// One gated HID open-by-cached-path + write + read (the path cache inside
			// TryGetBatteryLevel skips the candidate scan at steady state). -1 covers both a
			// yielded DeviceGate and a transient read failure; ComputePush treats it as no
			// evidence either way.
			var (percent, isCharging) = _hidDeviceDiscoveryService.TryGetBatteryLevel(device.VendorId, device.ProductId);

			var valid = percent is >= 1 and <= 100;
			if (valid) anyValidReading = true;

			var known = _state.TryGetValue(key, out var cached);
			var (next, push) = ComputePush(known ? cached.State : default, percent, isCharging);
			var timestamp = valid
				? DateTime.UtcNow
				: known ? cached.TimestampUtc : default;
			_state[key] = (next, timestamp);

			if (push)
			{
				_logger.LogInformation("[SmartInfinite] battery {Percent}% charging={Charging} VID={Vid} PID={Pid} — pushing",
					next.Percent, next.IsCharging, device.VendorId, device.ProductId);
				BatteryChanged?.Invoke(new TaipanStateMonitor.BatteryEvent(
					device.VendorId, device.ProductId, string.Empty, true, next.Percent, next.IsCharging));
			}
		}

		return (anyPresent, anyValidReading);
	}

	private IReadOnlyList<TrackedDevice> ResolveTracked()
	{
		var resolved = ResolveTrackedDevices(_deviceInfoProvider.DevicesMaps);

		if (resolved.Count == 0)
			_logger.LogInformation("[SmartInfinite] no dongle battery keyboards in the catalog; monitor stays idle");
		else
			_logger.LogInformation("[SmartInfinite] tracking {Count} device(s) from the catalog: {Devices}",
				resolved.Count, string.Join(", ", resolved.Select(d => $"{d.Name} VID={d.VendorId} PID={d.ProductId}")));

		return resolved;
	}
}
