using System.Collections.Concurrent;
using System.Linq;
using HidSharp;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;
using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.DeviceHost.Services;

// Tracks whether the PG Cosmo HEADSET is switched on, as opposed to whether its dongle is plugged
// in. Those are different things, and only the dongle is visible to USB enumeration: the receiver
// stays enumerated with the headset off, so without this the App's card claims "connected" for a
// headset sitting in a drawer.
//
// The state comes from byte 19 of vendor report 0xBC (0x03 = on). That report is not pushed — the
// dongle volunteers nothing — so it has to be polled with a GET_REPORT control-transfer READ. No
// host->device write is involved; see CosmoStateReport for the decoding and its provenance.
//
// Polling, not a reader thread: unlike TAIPAN (which pushes and is therefore listened to), there is
// nothing to listen for here. One control read per interface every couple of seconds is cheap, and
// the loop idles entirely while no Cosmo is present.
public sealed class CosmoStateMonitor : BackgroundService
{
	// Slow enough to be free, fast enough that flipping the headset's switch feels immediate. Only
	// while a dongle is actually plugged in — see ExecuteAsync.
	private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

	// Fallback cadence while NO dongle is present. The loop normally waits on the HID device-list
	// signal in that state; this only covers a change notification that never arrives. Same
	// signal-plus-safety-net shape DpiStageMonitor uses.
	private static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(30);

	// Byte 19 was seen to flicker 0x0F -> 0x03 -> 0x0F once during a power-off (the dongle retrying
	// its link). A single reading is therefore not trusted to flip the card: the new value has to
	// hold for two consecutive polls, which costs ~2 s of latency and removes the false reconnect.
	private const int ConfirmationsRequired = 2;

	private readonly ILogger<CosmoStateMonitor> _logger;
	private readonly IHidDeviceProvider _hidDeviceProvider;

	// Signalled when the HID device list changes, so a freshly-plugged dongle is picked up at once
	// and the loop can idle (no periodic read) while none is present.
	private readonly SemaphoreSlim _deviceListChanged = new(0, 1);

	// deviceKey -> confirmed power state. Keyed by Container ID so two headsets stay separate.
	private readonly ConcurrentDictionary<string, bool> _isOn = new(StringComparer.OrdinalIgnoreCase);
	// deviceKey -> the last full report, so callers can answer from this cache instead of doing
	// their own blocking control-transfer read. Written by the poll loop, read from gRPC threads.
	private readonly ConcurrentDictionary<string, CosmoStateReport> _lastReport = new(StringComparer.OrdinalIgnoreCase);
	// deviceKey -> (candidate value, how many consecutive polls have agreed).
	private readonly ConcurrentDictionary<string, (bool Value, int Count)> _pending = new(StringComparer.OrdinalIgnoreCase);

	// Raised when a headset's confirmed power state changes, so the host can re-publish the device
	// list and the card follows.
	public event Action? PowerStateChanged;

	public CosmoStateMonitor(ILogger<CosmoStateMonitor> logger, IHidDeviceProvider hidDeviceProvider)
	{
		_logger = logger;
		_hidDeviceProvider = hidDeviceProvider;
	}

	// True when this device may be presented to the App as connected. Anything that is not a Cosmo
	// always is; a Cosmo only once a poll has CONFIRMED its headset is on.
	//
	// Unknown state (nothing polled yet) counts as NOT presentable. It used to count as on, so a
	// freshly plugged dongle showed at once — but 0x0D8C:0x0334 is a stock C-Media audio id, so a
	// third-party headset or a Cosmo dongle with its headset off was published as a connected Cosmo
	// and hidden again by the first poll: the card blinked "connected" for about a second. The poll
	// is woken by the same device-list change, so a real headset appears one control read later.
	public bool IsPresentable(int vendorId, int productId, string? deviceKey)
	{
		if (!CosmoStateReader.Matches(vendorId, productId))
			return true;

		if (_isOn.TryGetValue(deviceKey ?? string.Empty, out var on))
			return on;

		// Single-device fallback, as in TryGetLastReport. The caller's key comes from
		// HidDeviceStateFactory, which reads it ONCE per path and caches it for the session, while
		// this monitor re-reads it on every poll — so one failed read on either side leaves the two
		// disagreeing for good. Now that a miss HIDES the device, that would keep a switched-on
		// headset off the screen until a restart; with one headset present its state is the answer.
		return _isOn.Count == 1 && _isOn.Values.First();
	}

	// HidDeviceState carries the ids as DECIMAL strings (HidDeviceStateFactory does .ToString() on
	// the numeric ids), so this overload parses rather than making every call site do it. An
	// unparseable id is simply not a Cosmo and is always presentable.
	public bool IsPresentable(string? vendorId, string? productId, string? deviceKey)
		=> !int.TryParse(vendorId, out var vid)
			|| !int.TryParse(productId, out var pid)
			|| IsPresentable(vid, pid, deviceKey);

	// The most recent report for this unit, or null if none has been polled yet. Lets a gRPC handler
	// answer without repeating the device read this loop already performs every two seconds — a
	// blocking control transfer has no business running on a request thread.
	public CosmoStateReport? TryGetLastReport(string? deviceKey)
	{
		if (_lastReport.TryGetValue(deviceKey ?? string.Empty, out var report))
			return report;

		// Single-device fallback, matching the rest of the host: an empty or unknown key answers
		// from the only headset present rather than reporting nothing.
		return _lastReport.Count == 1 ? _lastReport.Values.First() : null;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		// Wake the loop the moment the HID device list changes, so a freshly-plugged dongle is
		// picked up at once rather than after the idle fallback. Stored in a local so subscribe and
		// unsubscribe use the same delegate instance.
		EventHandler<DeviceListChangedEventArgs> onDeviceListChanged = (_, _) =>
		{
			if (_deviceListChanged.CurrentCount == 0)
			{
				try { _deviceListChanged.Release(); } catch (SemaphoreFullException) { }
			}
		};
		_hidDeviceProvider.SubscribeToDeviceListChanged(onDeviceListChanged);

		try
		{
			// Lives outside the loop so an exception keeps the PREVIOUS cadence: a transient
			// enumeration race (a device vanishing between list and open throws right when the
			// user flips the headset's switch) used to read as "no dongle" and drop the poll to
			// the 30 s idle interval with the dongle still plugged in — a single failed tick then
			// cost up to 30 s of stale power state instead of 2 (PR #130 review, m3).
			var donglePresent = false;
			while (!stoppingToken.IsCancellationRequested)
			{
				try
				{
					donglePresent = Poll();
				}
				catch (Exception ex)
				{
					// A monitor must never take the host down; a failed poll simply leaves the last
					// confirmed state — and the last cadence — in place.
					_logger.LogDebug(ex, "[Cosmo] power-state poll failed");
				}

				// The headset's switch is invisible to USB — the dongle stays enumerated whether the
				// headset is on or off — so while one IS plugged in there is no event to wait for and
				// the state genuinely has to be polled. While none is plugged in there is nothing to
				// read at all, and the old loop still ran this every 2 s for the life of the service
				// on machines that have never seen a Cosmo. Wait on the device-list signal instead.
				var interval = donglePresent ? PollInterval : IdleInterval;
				try { await _deviceListChanged.WaitAsync(interval, stoppingToken); }
				catch (OperationCanceledException) { return; }
			}
		}
		finally
		{
			_hidDeviceProvider.UnsubscribeFromDeviceListChanged(onDeviceListChanged);
		}
	}

	// Returns whether a Cosmo dongle is currently plugged in, which is what decides the loop's
	// cadence — there is nothing to poll for while none is.
	private bool Poll()
	{
		var devices = DeviceList.Local
			.GetHidDevices(CosmoStateReader.VendorId, CosmoStateReader.ProductId)
			.ToArray();

		if (devices.Length == 0)
		{
			// Dongle unplugged: drop the state so a later re-plug starts clean rather than
			// inheriting a stale "off" that would hide the device.
			if (!_isOn.IsEmpty || !_lastReport.IsEmpty)
			{
				_isOn.Clear();
				_pending.Clear();
				_lastReport.Clear();
			}
			return false;
		}

		var changed = false;

		foreach (var device in devices)
		{
			var path = device.DevicePath;
			if (string.IsNullOrEmpty(path)) continue;

			var state = CosmoStateReader.TryRead(path);
			if (state is null) continue;

			var key = DeviceContainerId.TryGet(path) ?? string.Empty;
			_lastReport[key] = state.Value;
			if (Confirm(key, state.Value.IsHeadsetOn))
				changed = true;
		}

		if (changed)
			PowerStateChanged?.Invoke();

		return true;
	}

	// Returns true when the CONFIRMED state actually changed.
	private bool Confirm(string deviceKey, bool observed)
	{
		var known = _isOn.TryGetValue(deviceKey, out var current);
		if (known && current == observed)
		{
			_pending.TryRemove(deviceKey, out _);
			return false;
		}

		var pending = _pending.TryGetValue(deviceKey, out var p) && p.Value == observed
			? (observed, p.Count + 1)
			: (observed, 1);

		// The first reading for a device is adopted immediately: there is no previous value to
		// disagree with, so waiting would just delay the card by one poll for no benefit.
		if (pending.Item2 >= ConfirmationsRequired || !known)
		{
			_pending.TryRemove(deviceKey, out _);
			_isOn[deviceKey] = observed;
			_logger.LogInformation("[Cosmo] headset {State} (deviceKey={Key})",
				observed ? "ON" : "OFF", string.IsNullOrEmpty(deviceKey) ? "<none>" : deviceKey);
			return true;
		}

		_pending[deviceKey] = pending;
		return false;
	}
}
