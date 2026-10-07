using InTheHand.Bluetooth;
using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.CustomEvents;
using ProoveGaming.Infrastructure.Interfaces;
using System.Collections.Concurrent;
using Windows.Devices.Bluetooth.Advertisement;

namespace ProoveGaming.Infrastructure.Services;

public class BluetoothDeviceDiscoveryService : IBluetoothDeviceDiscoveryService, IDisposable
{
    #region Private Fields

    // services
    private readonly ILogger<IBluetoothDeviceDiscoveryService> _logger;
    private readonly IDeviceInfoProvider _deviceInfoProvider;

    // managing
    // The WinRT advertisement watcher, created lazily on the first scan. It replaces InTheHand's
    // LE-scan request path, whose Windows implementation resolved a native BluetoothLEDevice
    // (with a blocking 7 s lookup) for EVERY advertisement in radio range before any subscriber
    // filtering could run — the unbounded memory climb of
    // docs/plans/fix-devicehost-ble-memory-leak/. Everything else in this service stays on
    // InTheHand; on Windows its backend is this same watcher underneath, so the two API surfaces
    // observe the same radio.
    private BluetoothLEAdvertisementWatcher? _watcher;

    // Immutable snapshot the Received handler matches advertisements against; rebuilt by
    // SyncScanState and swapped wholesale — volatile so the WinRT callback thread sees the
    // latest snapshot without a lock. A snapshot stale by milliseconds is harmless: the connect
    // path re-checks Gatt.IsConnected and _pendingConnections gates concurrency.
    private volatile BleAdvertisementMatcher? _matcher;

    // Serialises every watcher mutation (create/subscribe/Start/Stop/teardown). SyncScanState
    // runs concurrently from the periodic sweep, DeviceGattDisconnected (an InTheHand callback
    // thread), the connect continuation and StartDiscoveringCoreAsync, and TearDownWatcher races
    // all of them from Worker shutdown / Dispose. Unsynchronised, two racing creators could each
    // build and start a watcher with only one reachable through the field — the orphan keeps
    // scanning (and firing OnAdvertisementReceived) for the process lifetime with nothing able to
    // stop it. The mutation path is fully synchronous, so a plain lock is enough.
    private readonly object _watcherLock = new();

    // Set by TearDownWatcher (StopDiscovering / Dispose) so a late sweep pass or connect
    // continuation cannot resurrect a watcher after shutdown; cleared when discovery is
    // deliberately restarted. Guarded by _watcherLock.
    private bool _watcherStopped;

    // Identifies the scan window a scheduled close belongs to. Bumped on every successful watcher
    // start; a close task whose generation is stale belongs to a superseded window and must not
    // stop the one currently open. Guarded by _watcherLock.
    private int _scanWindowGeneration;

    private CancellationTokenSource? _periodicCheckCts;

    // Serialises StartDiscoveringIfNotStartedAsync. It used to have exactly one caller — Worker
    // startup, once — and its tail does a read-modify-write on _periodicCheckCts with no
    // synchronisation. OnBluetoothAvailabilityChanged now calls it too, from an async void handler
    // on a STATIC event that can fire repeatedly and re-enter while a previous call is still
    // awaiting. Two interleaved calls would each cancel the CTS the other had already replaced,
    // leaving one periodic sweep running against a token nobody holds — uncancellable by
    // StopDiscovering or Dispose, and duplicating every enumeration for the life of the process.
    //
    // A gate rather than a "started?" flag because the method is genuinely re-runnable: a radio that
    // comes back SHOULD re-enumerate. It just must not do so concurrently with itself.
    private readonly SemaphoreSlim _startGate = new(1, 1);

    // Whether enumerating paired devices has EVER succeeded. Distinguishes "there is nothing paired
    // to track" from "we could not look" — the sweep cadence below depends on the difference, and
    // treating them alike is what left a re-enabled radio unnoticed for up to five minutes.
    private volatile bool _pairedEnumerationEverSucceeded;

    // devices gets discovered and are kept here for the full life of the service
    private ConcurrentDictionary<string, BluetoothDevice> _trackedDevices = new ConcurrentDictionary<string, BluetoothDevice>();
    private ConcurrentDictionary<string, bool> _trackedStatuses = new ConcurrentDictionary<string, bool>();
    private ConcurrentDictionary<string, bool> _pendingConnections = new ConcurrentDictionary<string, bool>();

    // Connect-retry state per device id. _pendingConnections stops CONCURRENT attempts and nothing
    // else: the instant one finished, the next advertisement started another, and a BLE peripheral
    // advertises many times a second. A device we cannot hold a link to therefore produced
    // Gatt.ConnectAsync calls as fast as they could fail, for as long as it kept advertising —
    // every one of them resolving a native BluetoothLEDevice and creating a GattSession, which are
    // the objects Device Association Service brokers. That is what reached 12 GB overnight; see
    // docs/audits/2026-09-02-device-association-memory-audit.md (F1).
    private readonly ConcurrentDictionary<string, ConnectBackoff> _connectBackoff = new();
    #endregion

    // One device's connect-retry state: consecutive failures, and the earliest time another attempt
    // may run.
    private readonly record struct ConnectBackoff(int Failures, DateTime NextAttemptUtc);

    public event EventHandler<BluetoothDeviceConnectionChangedEventArgs>? DeviceConnectionChanged;
    
    public BluetoothDeviceDiscoveryService(
        ILogger<IBluetoothDeviceDiscoveryService> logger,
        IDeviceInfoProvider deviceInfoProvider)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _deviceInfoProvider = deviceInfoProvider ?? throw new ArgumentNullException(nameof(deviceInfoProvider));

        // Fires when the machine's Bluetooth radio is switched on or off. Subscribed in the
        // CONSTRUCTOR, not in StartDiscovering, because the case that needs it most is the one where
        // StartDiscovering found no radio at all: without this the service would sit idle until a
        // periodic sweep happened to notice, and the user would move the cursor over Bluetooth while
        // the App still showed nothing connected.
        Bluetooth.AvailabilityChanged += OnBluetoothAvailabilityChanged;
    }

    // async void is the documented exception for event handlers. Everything inside is guarded: this
    // runs on a callback from the Bluetooth stack, where an escaping exception has no caller to
    // catch it.
    private async void OnBluetoothAvailabilityChanged(object? sender, EventArgs e)
    {
        try
        {
            var available = await Bluetooth.GetAvailabilityAsync();
            if (!available)
            {
                // The radio is gone, so nothing we track is reachable — say so explicitly.
                //
                // This CANNOT be left to the periodic sweep, because the sweep asks
                // `device.Gatt.IsConnected`, and that property does not become false when the
                // adapter is switched off: measured 2026-08-14, fifteen seconds after this very
                // event fired, SyncScanState still concluded "every tracked device is
                // connected". It reports a stale cached value rather than throwing or returning
                // false, so RegisterDevice saw no state change and raised nothing, and the App kept
                // showing a Bluetooth mouse as connected with its radio off. A stale HasBluetooth
                // also suppresses the dongle (HasDongle = hasDongle && !hasWired && !bluetooth), so
                // the card could not fall back to the receiver either.
                //
                // The adapter-availability signal is the trustworthy one: it fired correctly on
                // every toggle in that session.
                _logger?.LogInformation("[BT] Adapter reported unavailable — disconnecting tracked devices.");
                MarkAllDisconnected();
                return;
            }

            _logger?.LogInformation("[BT] Adapter became available — re-running discovery.");
            await StartDiscoveringIfNotStartedAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[BT] Handling an availability change failed.");
        }
    }

    private bool DeviceNeedToTrack(BluetoothDevice bluetoothDevice)
    {
        if (bluetoothDevice == null || string.IsNullOrWhiteSpace(bluetoothDevice.Name))
        {
            return false;
        }

        // This used to upper-case the haystack and compare it against the raw catalog name with the
        // ordinal, case-SENSITIVE Contains. That worked, but only because of an invariant three
        // layers away: DeviceMapExpander sets `Name = device.Name?.ToUpper()`, so every DeviceMap
        // reaching here already carries an upper-cased name. Nothing at this call site said so, and
        // nothing would fail if the expander stopped doing it — the match would simply return
        // nothing for every mixed-case row, and a device that is never matched produces no error, no
        // log line and no exception. It just never appears.
        //
        // OrdinalIgnoreCase removes the hidden coupling instead of relying on it. The empty guard is
        // the part that fixes a real (if latent) defect: `Contains("")` is TRUE, so a catalog entry
        // with no Name at all — which the expander renders as `string.Empty` — would have matched
        // the first device examined and claimed it. The two sibling sites already had this guard;
        // this one did not.
        var config = _deviceInfoProvider.DevicesMaps.FirstOrDefault(bt =>
            bt.HasBluetooth && !string.IsNullOrEmpty(bt.Name) &&
            bluetoothDevice.Name.Contains(bt.Name, StringComparison.OrdinalIgnoreCase));

        // Remembered PER DEVICE ID, not on the catalog row.
        //
        // This used to write config.DeviceId — shared catalog state — which was wrong twice over. Set
        // once, the row kept the address from the FIRST pairing, and since a PG Taipan's BLE address
        // increments on every re-pair (measured D10B… -> D10C… -> D10D…) the lookup then matched
        // nothing and the event went out with an empty ClientName, which the App discards. Rewriting
        // it every call fixed that but made two units of one model fight over the row,
        // last-writer-wins, churning on every sweep.
        //
        // A device id maps to a catalog name; that is the fact, and it belongs in a per-id map. The
        // catalog stays immutable, which is what it should have been all along.
        if (config != null && !string.IsNullOrEmpty(config.Name))
        {
            try { _catalogNameByDeviceId[bluetoothDevice.Id] = config.Name; }
            catch { /* an id we cannot read is one we cannot cache; the name fallback still covers it */ }
        }

        return config != null;
    }

    public async Task StartDiscoveringIfNotStartedAsync(CancellationToken cancellationToken = default)
    {
        await _startGate.WaitAsync(cancellationToken);
        try
        {
            await StartDiscoveringCoreAsync(cancellationToken);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task StartDiscoveringCoreAsync(CancellationToken cancellationToken)
    {
        // A deliberate restart after StopDiscovering: let SyncScanState create a watcher again.
        lock (_watcherLock)
        {
            _watcherStopped = false;
        }

        // A restart — service start, or the radio coming back — is the largest change there is, so
        // neither idle escalation may be inherited across it: the first window and the first sweep
        // after it are both owed immediately.
        ResetIdleBackoff();

        // Enumerating paired devices throws outright when the machine's Bluetooth is switched off or
        // there is no adapter (InTheHand raises SecurityException from
        // ThrowSecurityExceptionOnMissingDeviceCapability). That is an ordinary state, not a fault:
        // plenty of users have no Bluetooth at all.
        //
        // It used to propagate, and because this method is awaited from Worker.StartAsync it aborted
        // the DeviceHost's startup entirely — so with Bluetooth off, EVERY device showed as
        // disconnected in the App, including a BLAZE on a cable that has nothing to do with
        // Bluetooth. One unavailable radio took down the whole host.
        //
        // Everything below this point must still run. In particular the periodic sweep at the end is
        // what notices the radio being switched ON later; skipping it would leave Bluetooth dead
        // until the service was restarted.
        try
        {
            var pairedDevices = await Bluetooth.GetPairedDevicesAsync();
            cancellationToken.ThrowIfCancellationRequested();
            _pairedEnumerationEverSucceeded = true;

            foreach (var device in pairedDevices)
            {
                if (DeviceNeedToTrack(device))
                {
                    RegisterDevice(device);
                    continue;
                }

                // Every other paired device on the machine came back as a live native object too.
                ReleaseUnretained(device);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogInformation(ex,
                "[BT] Bluetooth is unavailable at startup (adapter off or absent). " +
                "Continuing without it; the periodic sweep will pick it up if it is switched on.");
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000, cancellationToken);
            }
            catch (OperationCanceledException) { return; }

            foreach (var device in _trackedDevices.Values)
            {
                if (device.Gatt.IsConnected)
                    RegisterDevice(device);
            }
        }, cancellationToken);

        SyncScanState();

        // Periodic fallback: catches missed events when the BT adapter is toggled, and picks up a
        // device paired after the service started. Restarted here rather than early-returning when
        // a scan is already active — the previous code skipped it in that case.
        _periodicCheckCts?.Cancel();
        _periodicCheckCts = new CancellationTokenSource();
        _ = PeriodicReconnectCheckAsync(_periodicCheckCts.Token);
        _ = ClassicBluetoothPollAsync(_periodicCheckCts.Token);
    }

    // The LE scan exists for ONE job: a paired device powering on advertises, and the watcher's
    // Received handler is the only signal we get to re-establish GATT (there is no
    // "GattServerConnected" event). So it is needed exactly while a paired device we track is NOT
    // connected — not permanently.
    //
    // It used to be started once and left running for the entire lifetime of the service, keeping
    // the Bluetooth radio scanning and Windows' Device Association Framework busy even on machines
    // with no PG Bluetooth device paired at all. And even while needed, it now runs in bounded
    // windows (ScanWindow) rather than until reconnection: "needed" can be permanent — a device
    // left powered off, or a stale ghost pairing that can never reconnect — and a scan that never
    // stops is what drove Windows' Device Association Service to gigabytes (see ScanWindow). Each
    // start schedules its own close; the periodic sweep reopens the window while the need remains.
    //
    // Fails OPEN: if the connection state cannot be read, scan. Being wrong in that direction costs
    // battery; being wrong the other way means a device that never reconnects.
    //
    // Synchronous and void on purpose — starting the WinRT watcher is synchronous, and a false
    // Async/Task shape here is what let a throw escape a `_ =` discard once already (Round-1 S2).
    private void SyncScanState()
    {
        bool needScan;
        try
        {
            // Two independent reasons a disconnected device may still not need a scan, merged here
            // from PG-646 and the 2026-09-02 DAS audit. They address different halves of the same
            // "needed forever" problem and both are required:
            //
            //   ResolvedConnected  — the TREE's verdict, the same precedence RegisterDevice uses.
            //     This used to ask `!d.Gatt.IsConnected`, which reports whether OUR GATT client
            //     holds a link rather than whether the device is connected to the machine. For a
            //     BLE peripheral whose link Windows owns — an HID-over-GATT keyboard or mouse, i.e.
            //     the normal case — we may hold no session at all, so the predicate stayed true
            //     forever and the watcher never stopped, even for a device that was connected the
            //     whole time. See docs/audits/2026-09-02-device-association-memory-audit.md (F2).
            //
            //   IsExcludedAsStalePairing — a GHOST: a tracked entry that has not connected in 14+
            //     days and never will (a Taipan's BLE address increments on every re-pair, so the
            //     old entry lingers). Genuinely disconnected, so the tree agrees it is not
            //     connected; it must still not hold the scan open. See
            //     docs/plans/add-stale-pairing-detection/.
            //
            // No early break: IsExcludedAsStalePairing owns the exclusion-transition logging, so
            // every disconnected device gets evaluated each pass. A handful of devices at sweep
            // frequency — not a hot path. The per-device verdicts feed ResolveScanNeed rather than
            // being folded inline, because the veto is no longer purely per-device: a ghost only
            // yields to the scan-need computation while another paired, non-stale device exists
            // (see ResolveScanNeed's comment — PR #130 review, m4).
            var states = new List<(bool Paired, bool Connected, bool Stale)>();
            foreach (var device in _trackedDevices.Values)
            {
                var paired = device.IsPaired;
                var connected = paired && ResolvedConnected(device);
                var stale = paired && !connected && IsExcludedAsStalePairing(device);
                states.Add((paired, connected, stale));
            }
            needScan = ResolveScanNeed(states);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not read tracked connection state; keeping the LE scan on.");
            needScan = true;
        }

        // Rebuild the snapshot the Received handler matches against. ALL tracked devices are
        // included, not just the disconnected subset: a connected device does not advertise, and
        // the connect path re-checks Gatt.IsConnected anyway, so including everything is simpler
        // and safe.
        _matcher = BuildMatcher();

        // Every watcher mutation happens under _watcherLock, operating on a LOCAL so a concurrent
        // caller can never start an instance the field no longer holds — the orphaned-watcher race
        // described at the lock's declaration.
        lock (_watcherLock)
        {
            if (needScan)
            {
                // A late sweep pass or connect continuation must not resurrect the watcher after
                // StopDiscovering / Dispose tore it down.
                if (_watcherStopped)
                    return;

                var watcher = _watcher;

                // A watcher in any state but Started counts as not running, so the sweep restarts
                // the scan once the radio is back.
                if (watcher is { Status: BluetoothLEAdvertisementWatcherStatus.Started })
                    return;

                // The need is real but nothing has changed for a while, so this window is not owed
                // yet — see ScanReopenSchedule. Logged at Debug: on a machine whose device simply
                // is not coming back this is the steady state, and one line per sweep for it would
                // be pure noise in the file the sweep's real events have to be found in.
                if (DateTime.UtcNow < _nextScanWindowUtc)
                {
                    _logger?.LogDebug(
                        "[BT] LE scan window deferred — {Windows} window(s) have closed with no change; next at {Next:HH:mm:ss}Z.",
                        _consecutiveScanWindows, _nextScanWindowUtc);
                    return;
                }

                // An Aborted watcher (the radio vanished while it ran) is discarded rather than
                // restarted: whether Start() on an aborted instance recovers is undocumented in
                // WinRT, and if it threw, the catch below would swallow it and BLE reconnection
                // would be dead until a process restart. Unsubscribe, drop the instance and fall
                // through to the creation branch so a fresh watcher is started instead — behaviour
                // then never depends on that undocumented detail.
                if (watcher is { Status: BluetoothLEAdvertisementWatcherStatus.Aborted })
                {
                    watcher.Received -= OnAdvertisementReceived;
                    _watcher = null;
                    watcher = null;
                }

                try
                {
                    if (watcher == null)
                    {
                        watcher = new BluetoothLEAdvertisementWatcher
                        {
                            // Passive: no scan-request/response cycle per advertiser. Active scanning
                            // was the costly half of the DAS load this scan puts on Windows (see
                            // ScanWindow) and bought us only the scan response, where a LocalName
                            // usually travels — feeding the matcher's name fallback, which is only
                            // ever used for a tracked id that failed to parse as an address and which
                            // no PG device hits today. Such a device now falls back to the sweep and
                            // the tree poll; address matching, the path PG hardware actually takes,
                            // sees every advertisement either way. No AdvertisementFilter /
                            // SignalStrengthFilter: filtering is handler-side by design (a LocalName
                            // filter can silently drop the very frame that should wake a reconnect,
                            // and no filter can express "this Bluetooth address").
                            ScanningMode = BluetoothLEScanningMode.Passive,
                        };
                        watcher.Received += OnAdvertisementReceived;
                        _watcher = watcher;
                    }
                    watcher.Start();

                    // Start() can also fail by transitioning straight to Aborted without throwing
                    // (per the WinRT docs) — don't claim a scan started that never ran; the next
                    // sweep retries the same way it does after a throw.
                    if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Aborted)
                    {
                        _logger?.LogDebug("[BT] LE scan start aborted immediately (adapter off?); the next sweep retries.");
                    }
                    else
                    {
                        // Every start owns exactly one scheduled close; anything that legitimately
                        // stops or replaces the scan before then (reconnect, teardown, a reopen)
                        // makes that close a no-op via the generation/status guard. The epoch rides
                        // along so the close can tell whether anything changed while it was open.
                        _ = CloseScanWindowAfterDelayAsync(++_scanWindowGeneration, _idleEpoch);
                        _logger?.LogInformation("[BT] LE scan started — a tracked device is disconnected.");
                    }
                }
                catch (Exception ex)
                {
                    // Starting the watcher fails while the radio is off. That is expected, not an
                    // error, and it must not propagate: this method is awaited from the periodic
                    // sweep, and an exception here would abort the sweep exactly as the paired-device
                    // enumeration used to. The next sweep retries once the radio is back.
                    _logger?.LogDebug(ex, "[BT] Could not start the LE scan (adapter off?).");
                }
            }
            else if (_watcher is { Status: BluetoothLEAdvertisementWatcherStatus.Started } watcher)
            {
                try
                {
                    watcher.Stop();
                    _logger?.LogInformation("[BT] LE scan stopped — every tracked device is connected.");
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "[BT] Could not stop the LE scan.");
                }
            }
        }
    }

    // Closes the scan window this generation opened. Deliberately NOT cancellation-tracked: the
    // first SyncScanState of a discovery run executes before StartDiscoveringCoreAsync replaces
    // the periodic CTS, so a captured token would be the one about to be cancelled and that first
    // window would never close — while the sweep, seeing a Started watcher, would never schedule
    // another. The generation + status guard under _watcherLock is the sole authority instead:
    // after a teardown, a reconnect-stop or a newer window this task finds the guard false and
    // does nothing, and the worst a shutdown leaves behind is one ScanWindow-long delay that
    // no-ops. Fire-and-forget with the whole body guarded — no caller observes a throw.
    private async Task CloseScanWindowAfterDelayAsync(int generation, int openedEpoch)
    {
        try
        {
            await Task.Delay(ScanWindow);

            lock (_watcherLock)
            {
                var watcher = _watcher;
                var started = watcher is { Status: BluetoothLEAdvertisementWatcherStatus.Started };
                if (watcher is null || !ShouldCloseScanWindow(generation, _scanWindowGeneration, started))
                    return;

                try
                {
                    watcher.Stop();

                    // A window that spanned a connection-state change is not evidence that nothing
                    // is happening, so it is not charged: the reset that change performed stands,
                    // and the next window opens immediately. Leaving _nextScanWindowUtc alone is
                    // the whole action — ResetIdleBackoff already set it to MinValue.
                    if (!ShouldChargeQuietWindow(openedEpoch, _idleEpoch))
                    {
                        _logger?.LogInformation(
                            "[BT] LE scan window closed — a device connected or disconnected while it was open, " +
                            "so the next sweep reopens it in 0s.");
                        return;
                    }

                    // Nothing changed while it was open, so the next one costs more. Counted on the
                    // CLOSE rather than the open so the delay is measured from when the radio
                    // actually went quiet.
                    _consecutiveScanWindows++;
                    var reopenDelay = ScanReopenDelay(_consecutiveScanWindows);
                    _nextScanWindowUtc = DateTime.UtcNow + reopenDelay;

                    _logger?.LogInformation(
                        "[BT] LE scan window closed — the next sweep reopens it in {Delay}s if a tracked device is still disconnected.",
                        (int)reopenDelay.TotalSeconds);
                    return;
                }
                catch (Exception ex)
                {
                    // Review R1 S1: giving up here used to end the duty cycle — the sweep sees a
                    // Started watcher, never schedules another close, and the unbounded scan is
                    // back until a reconnect. Fall through to a RETRY of the same generation
                    // instead. Not a discard: unlike the sweep's Aborted branch this watcher is
                    // still Started, and dropping the field's only reference to a running watcher
                    // is exactly the unstoppable-orphan-scan race _watcherLock exists to prevent.
                    // The guard bounds the retries — a stop by reconnect, a teardown, or a newer
                    // window all end them on the next pass.
                    _logger?.LogDebug(ex, "[BT] Could not close the LE scan window; retrying in {Seconds}s.",
                        (int)ScanWindow.TotalSeconds);
                }
            }

            // The retry carries the ORIGINAL epoch: the window it is still trying to close is the
            // same one, so a change that happened meanwhile must still spare it from being charged.
            _ = CloseScanWindowAfterDelayAsync(generation, openedEpoch);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[BT] Closing the LE scan window failed.");
        }
    }

    // Whether a scheduled window close may stop the scan, extracted so the race is pinned by a
    // test rather than by reading the call site. Both inputs matter: a stale generation belongs
    // to a superseded window (stopping now would kill a window opened later), and a watcher that
    // is not Started was already stopped by a reconnect or teardown — nothing left to close.
    internal static bool ShouldCloseScanWindow(int scheduledGeneration, int currentGeneration, bool watcherStarted) =>
        scheduledGeneration == currentGeneration && watcherStarted;

    // Snapshots the tracked devices in the shape the Received handler needs: parsed addresses for
    // the O(1) match, plus a catalog-name fallback for any tracked id that is not the 12-hex-digit
    // address string Windows normally reports (mirrors DeviceNeedToTrack's catalog rule).
    private BleAdvertisementMatcher BuildMatcher()
    {
        var addresses = new Dictionary<ulong, string>();
        var fallbacks = new List<(string NameUpper, string DeviceId)>();

        foreach (var device in _trackedDevices.Values)
        {
            string id;
            string name;
            try
            {
                id = device.Id;
                name = device.Name;
            }
            catch
            {
                continue;   // A handle whose identity cannot be read cannot be matched either.
            }

            if (BluetoothAddressParser.TryParse(id, out var address))
            {
                addresses[address] = id;
            }
            else if (!string.IsNullOrWhiteSpace(name))
            {
                // Same case rule as DeviceNeedToTrack — see the note there.
                var config = _deviceInfoProvider.DevicesMaps
                    .FirstOrDefault(bt => bt.HasBluetooth && !string.IsNullOrEmpty(bt.Name) &&
                        name.Contains(bt.Name, StringComparison.OrdinalIgnoreCase));
                if (config != null)
                    fallbacks.Add((config.Name.ToUpperInvariant(), id));
            }
        }

        return new BleAdvertisementMatcher(addresses, fallbacks);
    }

    public void StopDiscovering()
    {
        _periodicCheckCts?.Cancel();
        TearDownWatcher();
    }

    // BluetoothLEAdvertisementWatcher is not IDisposable — stopped + unsubscribed is its clean end
    // state. _watcherStopped keeps a late sweep pass or connect continuation from recreating a
    // watcher after this ran; StartDiscoveringCoreAsync clears it, so a deliberately restarted
    // discovery still gets a fresh watcher (the field is nulled here for exactly that).
    private void TearDownWatcher()
    {
        lock (_watcherLock)
        {
            _watcherStopped = true;

            var watcher = _watcher;
            if (watcher == null)
                return;

            _watcher = null;

            try
            {
                if (watcher.Status == BluetoothLEAdvertisementWatcherStatus.Started)
                    watcher.Stop();
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[BT] Stopping the LE watcher failed.");
            }

            watcher.Received -= OnAdvertisementReceived;
        }
    }

    public async Task<IReadOnlyList<BluetoothDeviceState>> GetBluetoothDevicesAsync()
    {
        var results = new List<BluetoothDeviceState>();

        foreach (var map in _trackedDevices)
        {
            if (map.Value != null)
            {
                int battery = -1;
                // Same owner as the IsConnected above, or a live device hands the App a snapshot
                // with no battery level at all.
                if (ResolveConnected(LowEnergyTreeSaysConnected(map.Value),
                        map.Value.Gatt.IsConnected, map.Value.IsPaired))
                    battery = await ReadGattBatteryAsync(map.Value);
                results.Add(Map(map.Value, battery));
            }
        }

        // Classic devices are found through the device tree rather than the radio, so they are polled
        // here too: this is also the call the App makes on startup, before the first sweep has run,
        // and without it a headset already connected at launch would not appear until the next sweep.
        PollClassicBluetoothDevices();
        results.AddRange(_classicDevices.Values.Where(d => d.IsConnected));

        return results;
    }

    public void Dispose()
    {
        _periodicCheckCts?.Cancel();
        _periodicCheckCts?.Dispose();
        _startGate.Dispose();

        StopDiscovering();

        Bluetooth.AvailabilityChanged -= OnBluetoothAvailabilityChanged;

        // Unsubscribe from all device events
        foreach (var device in _trackedDevices.Values)
        {
            device.GattServerDisconnected -= DeviceGattDisconnected;
            device.Dispose();
        }

        _trackedDevices.Clear();
        _trackedStatuses.Clear();
        _pendingConnections.Clear();
        _connectBackoff.Clear();
    }

    #region Private Methods

    private void RegisterDevice(BluetoothDevice device)
    {
        if (device == null)
        {
            return;
        }

        if (!_trackedDevices.ContainsKey(device.Id))
        {
            _trackedDevices[device.Id] = device;
            device.GattServerDisconnected += DeviceGattDisconnected;
        }
        else if (!ReferenceEquals(_trackedDevices[device.Id], device))
        {
            // A new OS-level instance arrived for the same physical device (happens after
            // adapter toggle). Re-subscribe on the new instance so future disconnects fire.
            var old = _trackedDevices[device.Id];
            old.GattServerDisconnected -= DeviceGattDisconnected;
            _trackedDevices[device.Id] = device;
            device.GattServerDisconnected += DeviceGattDisconnected;

            // A fresh handle has not failed anything yet, so it starts with a clean retry budget —
            // otherwise the replacement inherits the dead instance's backoff and is throttled, or
            // retired, for failures that were never its own.
            _connectBackoff.TryRemove(device.Id, out _);

            // Dispose what we just replaced (August audit C23). It is unreachable the moment the
            // entry is overwritten — nothing else holds a device reference — so leaving it to
            // finalization only delays releasing the native BluetoothLEDevice and its GATT session.
            // Those are exactly the Device-Association-brokered objects this audit is about, and
            // this branch runs on every adapter toggle.
            try { old.Dispose(); } catch { /* already gone */ }
        }

        // The device tree wins when it has an opinion. Gatt.IsConnected reflects OUR GATT client,
        // not the OS link, and is wrong in both directions: stale `true` after the radio is switched
        // off, and stuck `false` on the dead instance left behind when a device is removed and
        // re-paired. Falling back to it only where the tree cannot answer keeps every non-BLE and
        // unresolvable case exactly as it was.
        var newStatus = ResolveConnected(
            LowEnergyTreeSaysConnected(device), device.Gatt.IsConnected, device.IsPaired);
        _trackedStatuses.TryGetValue(device.Id, out var oldStatus);

        if (oldStatus != newStatus)
        {
            _trackedStatuses[device.Id] = newStatus;
            // Fire immediately so the UI reflects connection state without waiting for battery
            RaiseDeviceConnectionChanged(device.Id, device.Name, newStatus, device.IsPaired, -1);

            // A transition in EITHER direction is a change, and the scan backoff only escalates
            // while there are none — see ScanReopenSchedule.
            ResetIdleBackoff();
            if (newStatus)
            {
                // Same reason as MarkConnected: a connection ends any staleness verdict.
                _scanNeedExcluded.TryRemove(device.Id, out _);
                _ = UpdateBatteryAsync(device);
            }
        }
    }

    // Releases a device the enumeration produced that we did not keep.
    //
    // Bluetooth.GetPairedDevicesAsync is not a cheap listing: InTheHand's Windows implementation
    // (verified against 4.0.44) enumerates DeviceInformation and then resolves a NATIVE
    // BluetoothLEDevice per entry via BluetoothLEDevice.FromIdAsync — for EVERY BLE-paired device
    // on the machine, PG or not — and disposes none of them. Those native objects are exactly what
    // Device Association Service brokers and holds state for.
    //
    // We tracked one or two of them and dropped the rest on the floor every ActiveSweepInterval,
    // leaving each one alive until the GC happened to collect a tiny managed wrapper — which, with
    // nothing else allocating in a 24/7 service, can be hours. A machine with a paired headset, a
    // phone and a watch therefore leaked three native device objects every 30 s, all night, and
    // none of it was visible in this process's own memory. Disposing is not an optimisation here;
    // it is the difference between borrowing the object and keeping it.
    //
    // Guarded by an identity check rather than "did we track it", so this stays correct even when
    // DeviceNeedToTrack threw before deciding: the ONLY instance that must survive is one the
    // tracking dictionary actually points at.
    private void ReleaseUnretained(BluetoothDevice? device)
    {
        if (device is null) return;

        try
        {
            if (_trackedDevices.TryGetValue(device.Id, out var tracked) && ReferenceEquals(tracked, device))
                return;

            device.Dispose();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "[BT] Releasing an untracked paired-device instance failed.");
        }
    }

    // Re-reads the battery of every device that is currently connected, at most once per
    // BatteryRefreshInterval. Fire-and-forget per device: UpdateBatteryAsync raises the changed
    // event itself, and one slow or unresponsive device must not hold up the others or the sweep.
    private void RefreshConnectedBatteries()
    {
        if (DateTime.UtcNow - _lastBatteryRefreshUtc < BatteryRefreshInterval)
            return;

        _lastBatteryRefreshUtc = DateTime.UtcNow;

        foreach (var device in _trackedDevices.Values)
        {
            try
            {
                // Reading Gatt.IsConnected can throw once the radio is gone; a device we cannot
                // ask is simply skipped — the sweep above is what marks it disconnected.
                if (device.Gatt.IsConnected)
                    _ = UpdateBatteryAsync(device);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[BT] Skipped a battery refresh for a device that could not be read.");
            }
        }
    }

    // Publishes a battery LEVEL. It must never assert a CONNECTION.
    //
    // Until 2026-08-26 this raised isConnected: true unconditionally whenever a read returned a
    // value. ReadGattBatteryAsync allows up to 5 s, so a read already in flight when the device was
    // marked disconnected — or released outright by ReleaseTrackedDevice — completed afterwards and
    // re-asserted connected for a device that was gone. That is visible in the captured log as
    // "marked disconnected" at 23:15:09 followed by "GATT battery 46% -> connected=true" at 23:15:12,
    // and it is the same resurrection the whole device-tree change exists to stop, surviving as a
    // race with a five-second window.
    //
    // Connection state has exactly one owner — the device tree, through PollLowEnergyPresence and
    // ResolveConnected. This path is a reader of that state, never a writer of it: a level for a
    // device the tree no longer calls connected is simply dropped.
    private async Task UpdateBatteryAsync(BluetoothDevice device)
    {
        int battery = await ReadGattBatteryAsync(device);

        string id;
        try { id = device.Id; }
        catch { return; }   // released and disposed while the read was in flight

        if (!ShouldPublishBattery(battery, _trackedDevices.ContainsKey(id), CurrentlyConnected(id)))
            return;

        string name;
        try { name = device.Name; }
        catch { name = string.Empty; }

        bool paired;
        try { paired = device.IsPaired; }
        catch { paired = true; }

        // Both sources share one dedup cache, so the GATT path must record what it published or the
        // two can suppress each other's readings.
        _lastPublishedBattery[id] = battery;
        RaiseDeviceConnectionChanged(id, name, true, paired, battery);
    }

    // Whether a completed battery read may be published, extracted so the race is pinned by a test
    // rather than by reading the call site. All three conditions matter: a non-reading (<= 0) says
    // nothing, an untracked device has been released, and a device the tree does not currently call
    // connected must not be revived by a stale reading.
    internal static bool ShouldPublishBattery(int battery, bool stillTracked, bool currentlyConnected) =>
        battery > 0 && stillTracked && currentlyConnected;

    private bool CurrentlyConnected(string id) =>
        _trackedStatuses.TryGetValue(id, out var connected) && connected;

    private async Task<int> ReadGattBatteryAsync(BluetoothDevice device)
    {
        try
        {
            var readTask = ReadGattBatteryInternalAsync(device);
            var winner = await Task.WhenAny(readTask, Task.Delay(5000));
            if (winner != readTask)
            {
                _logger?.LogWarning("[BT Battery] GATT battery read timed out for {Name}", device.Name);
                return -1;
            }
            return await readTask;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[BT Battery] GATT battery read failed for {Name}", device.Name);
            return -1;
        }
    }

    private async Task<int> ReadGattBatteryInternalAsync(BluetoothDevice device)
    {
        var service = await device.Gatt.GetPrimaryServiceAsync(BluetoothUuid.FromShortId(0x180F));
        if (service == null) return -1;

        var characteristic = await service.GetCharacteristicAsync(BluetoothUuid.FromShortId(0x2A19));
        if (characteristic == null) return -1;

        var bytes = await characteristic.ReadValueAsync();
        if (bytes != null && bytes.Length > 0)
        {
            _logger?.LogInformation("[BT Battery] GATT battery for {Name}: {Level}%", device.Name, bytes[0]);
            return bytes[0];
        }
        return -1;
    }

    // HOT PATH: fires for every advertisement from every BLE device in radio range while the
    // watcher runs. A non-match must cost one ulong dictionary lookup and nothing else — no WinRT
    // object is resolved, created or disposed here, which is the whole fix: InTheHand's handler
    // resolved a native device (blocking up to 7 s) per advertisement before we ever saw it.
    // Runs on a WinRT callback thread, so the whole body is guarded — an escaping exception has
    // no caller to catch it.
    private void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        try
        {
            var matcher = _matcher;
            if (matcher == null)
                return;

            // LocalName is read only when a fallback entry exists at all: args.Advertisement
            // materialises a WinRT projection and LocalName marshals an HSTRING into a new
            // string, and the normal case (every tracked id parsed to an address) must stay
            // allocation-free per advertisement.
            //
            // DELIBERATE TRADEOFF: unlike the old InTheHand handler, a no-match returns without
            // ever registering the device — no DeviceNeedToTrack/RegisterDevice per advertisement.
            // Creating a device from an advertisement is exactly the leak path this fix removed,
            // so a device NOT yet in _trackedDevices (a newly-paired second PG device, or a fresh
            // OS-level instance after an adapter toggle) is only picked up by the periodic sweep:
            // worst case one active-sweep interval (30 s), or one idle-sweep interval (5 min) only
            // when nothing was ever tracked.
            var localName = matcher.HasNameFallbacks ? args.Advertisement?.LocalName : null;
            if (!matcher.TryMatch(args.BluetoothAddress, localName, out var deviceId))
                return;

            // Connect via the ALREADY-TRACKED InTheHand instance — nothing is ever created from
            // the advertisement itself.
            if (!_trackedDevices.TryGetValue(deviceId, out var device))
                return;

            // A tracked device that the DEVICE TREE does not call connected is advertising to
            // re-establish a link. There is no "GattServerConnected" event, so we call ConnectAsync
            // explicitly to bridge the gap and then re-run RegisterDevice to fire IsConnected=true.
            //
            // The tree verdict, not Gatt.IsConnected, for the reason SyncScanState gives: asking our
            // own client whether it holds a link means a device Windows is already connected to
            // reads as needing a connect on EVERY advertisement, forever. Where the tree has no
            // opinion ResolveConnected still falls back to Gatt.IsConnected, so an unresolvable
            // device behaves exactly as before.
            bool needConnect;
            try
            {
                // Reading the state can throw when the radio disappears mid-callback.
                needConnect = !ResolvedConnected(device) && device.IsPaired;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[BT] Could not read state for an advertising tracked device.");
                return;
            }

            // Two independent brakes. _pendingConnections is the debounce — a device advertises many
            // times per second and TryAdd guarantees exactly one CONCURRENT attempt. The backoff is
            // what bounds CONSECUTIVE ones, which is the half that was missing.
            if (needConnect && ConnectAttemptAllowed(deviceId) && _pendingConnections.TryAdd(deviceId, true))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await device.Gatt.ConnectAsync();

                        // ConnectAsync returning is not the same as a link being up — it reports no
                        // result and the session can settle disconnected. Gatt.IsConnected is the
                        // wrong signal for "is the device connected" (see SyncScanState) but the
                        // right one HERE, because it answers the narrower question this attempt
                        // actually asked: did our client get a link? Treating a bare return as
                        // success would clear the backoff on every pass and restore the storm.
                        if (device.Gatt.IsConnected)
                        {
                            _connectBackoff.TryRemove(deviceId, out _);
                            RegisterDevice(device);

                            // Reconnected — if that was the last disconnected device we track, the
                            // scan has done its job and can stop until something drops again.
                            SyncScanState();
                        }
                        else
                        {
                            RecordConnectFailure(device, deviceId);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "GATT connect attempt for {Id}", deviceId);
                        RecordConnectFailure(device, deviceId);
                    }
                    finally
                    {
                        _pendingConnections.TryRemove(deviceId, out _);
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exception occurred in {Operation}", nameof(OnAdvertisementReceived));
        }
    }

    // Backoff schedule for failed GATT connects: 1 s, doubling, to a 60 s ceiling.
    private static readonly TimeSpan ConnectRetryInitialDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ConnectRetryMaxDelay = TimeSpan.FromSeconds(60);

    // Consecutive failures after which the INSTANCE is presumed dead and released rather than
    // retried forever. ReleaseTrackedDevice already documents why a dead InTheHand instance can
    // never recover on its own — its Gatt never reconnects — so the only thing that helps is
    // letting go and letting TriggerRediscoveryIfUnexplained build a fresh one from the paired
    // enumeration. Six failures is ~63 s of retrying on the schedule above: long enough to ride out
    // a device that is merely slow to answer, short enough that a genuinely dead handle does not
    // hold the radio open all night.
    private const int ConnectAttemptsBeforeRetire = 6;

    // Whether the backoff permits another connect attempt for this device right now.
    private bool ConnectAttemptAllowed(string deviceId) =>
        !_connectBackoff.TryGetValue(deviceId, out var backoff) || DateTime.UtcNow >= backoff.NextAttemptUtc;

    // Counts a failed connect, schedules the next attempt, and retires the instance once it has
    // failed often enough to be presumed dead.
    private void RecordConnectFailure(BluetoothDevice device, string deviceId)
    {
        var backoff = _connectBackoff.AddOrUpdate(
            deviceId,
            _ => new ConnectBackoff(1, DateTime.UtcNow + NextConnectDelay(1)),
            (_, previous) => new ConnectBackoff(
                previous.Failures + 1, DateTime.UtcNow + NextConnectDelay(previous.Failures + 1)));

        if (backoff.Failures < ConnectAttemptsBeforeRetire)
            return;

        _logger?.LogInformation(
            "[BT LE] {Id} failed {Count} consecutive GATT connects — releasing the instance so a fresh one can be enumerated.",
            deviceId, backoff.Failures);

        ReleaseTrackedDevice(device, deviceId);
    }

    // The delay before attempt N+1, given N consecutive failures. Extracted so the schedule is
    // pinned by a test rather than by reading the call site.
    internal static TimeSpan NextConnectDelay(int consecutiveFailures)
    {
        // Clamped before the shift: a caller passing 0 (or anything past the ceiling) must not
        // produce a negative shift or overflow the tick count into a delay that never elapses.
        var shift = Math.Clamp(consecutiveFailures - 1, 0, 16);
        var ticks = ConnectRetryInitialDelay.Ticks << shift;
        return ticks >= ConnectRetryMaxDelay.Ticks ? ConnectRetryMaxDelay : TimeSpan.FromTicks(ticks);
    }

    // Every tracked device, at once — used when the adapter itself is gone, which makes per-device
    // state meaningless. MarkDisconnected only raises on a transition, so calling this on every
    // sweep while the radio stays off costs one dictionary walk and notifies nothing.
    private void MarkAllDisconnected()
    {
        foreach (var device in _trackedDevices.Values)
            MarkDisconnected(device);
    }

    // Force a tracked device to "disconnected" when its state cannot be read at all. Mirrors
    // DeviceGattDisconnected, which handles the clean case where the device tells us it dropped.
    // Only raises on a TRANSITION, so a radio that stays off does not re-notify every sweep.
    private void MarkDisconnected(BluetoothDevice device, Exception? cause = null)
    {
        string id;
        try { id = device.Id; }
        catch { return; }   // Nothing usable to key on; the next sweep will retry.

        if (_trackedStatuses.TryGetValue(id, out var wasConnected) && !wasConnected)
            return;

        _trackedStatuses[id] = false;

        // The App clears its battery level on disconnect (FillBluetoothCard), so the next reading
        // after a reconnect must be published even when the charge has not moved. Without this the
        // dedup cache still held the pre-disconnect figure, ShouldPublishTreeBattery saw "unchanged"
        // and suppressed it, and the indicator stayed empty until a GATT read happened to succeed —
        // up to five minutes — after every ordinary off/on cycle.
        _lastPublishedBattery.TryRemove(id, out _);

        string name;
        try { name = device.Name; }
        catch { name = string.Empty; }

        bool paired;
        try { paired = device.IsPaired; }
        catch { paired = false; }

        _logger?.LogInformation(cause is null
            ? "[BT] {Name} marked disconnected."
            : "[BT] {Name} marked disconnected — its state could not be read (adapter off?).", name);

        RaiseDeviceConnectionChanged(id, name, false, paired, -1);

        // A device that just dropped is the case the scan exists for, so it gets the next window
        // immediately however long the previous device had been away. Only reached on a TRANSITION
        // (the guard above returns for a device that was already disconnected), so a radio left off
        // cannot keep re-arming the scan every sweep.
        ResetIdleBackoff();

        // ...and OPEN that window here rather than waiting for the sweep to come round.
        //
        // This path is how a tree-detected drop is published (the BLE presence poll calls it), and
        // it used to leave the scan to the next sweep — up to ActiveSweepInterval away. Now that the
        // sweep itself backs off while nothing changes, "the next sweep" could be minutes, so the
        // reconnect window would have been the thing paying for an enumeration bound that has
        // nothing to do with it. DeviceGattDisconnected — the clean-disconnect twin of this method
        // — has always done exactly this; the two paths now agree.
        SyncScanState();
    }

    // Runs on an InTheHand callback thread with no caller to observe an exception, so the whole
    // body is guarded — the Id/Name/IsPaired reads can throw once the radio is gone, and
    // SyncScanState is synchronous, so a throw from it would otherwise escape uncaught.
    private void DeviceGattDisconnected(object? sender, object e)
    {
        try
        {
            var device = sender as BluetoothDevice;

            if (device == null)
            {
                return;
            }

            _trackedStatuses[device.Id] = false;

            RaiseDeviceConnectionChanged(device.Id, device.Name, false, device.IsPaired, -1);

            // This path publishes the disconnect itself rather than going through MarkDisconnected,
            // so it owes the same backoff reset: a device that just dropped must get its window now,
            // not after whatever delay the previous device's silence had built up.
            ResetIdleBackoff();

            // Something we track just dropped, so the LE scan has to come back on — its advertisement
            // is the only way we learn the device is available again. Without this the scan would stay
            // off until the periodic sweep noticed, delaying reconnection by up to the sweep interval.
            SyncScanState();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exception occurred in {Operation}", nameof(DeviceGattDisconnected));
        }
    }

    // Cadence for the fallback sweep. Bluetooth.GetPairedDevicesAsync is a full device enumeration
    // (DeviceInformation under the hood — the Device Association Framework path), so it is not free.
    // While we track something, 30 s keeps a device that vanished WITHOUT a clean GATT disconnect
    // event from going unnoticed. With nothing tracked there is nothing to miss, and the sweep is
    // only there to notice a newly PAIRED device, which is a human action — minutes is plenty.
    private static readonly TimeSpan ActiveSweepInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleSweepInterval = TimeSpan.FromMinutes(5);

    // The active cadence, escalated by how many sweeps have run with NOTHING having changed.
    //
    // 30 s is the right interval for a machine where things are happening. It is the wrong one for
    // the state this service actually spends its life in — a device paired and quietly absent (or,
    // the 2026-09-08 incident, a TAIPAN paired over Bluetooth and in use on its dongle) — where it
    // means 2,880 full DeviceInformation enumerations a day that each learn exactly nothing. That
    // is the last unbounded thing we ask the Device Association Framework for, and unlike the scan
    // its cost was never measured; the point here is that it does not need measuring, because a
    // call whose answer cannot have changed does not need making.
    //
    // What the sweep is FOR, and what covers each job while it waits:
    //   - notice a device that dropped without an event -> the 3 s BLE presence poll (BLE) and the
    //     3 s classic poll (headsets) both see it far sooner than any sweep ever did;
    //   - discover a device paired since startup -> a newly paired device is a CONNECTED device, so
    //     TriggerRediscoveryIfUnexplained pulls the enumeration forward within 10 s (its own
    //     schedule). Only a device paired and then switched off before a sweep noticed waits, and
    //     it waits at most the ceiling below;
    //   - rebuild a dead InTheHand instance -> same rediscovery trigger, same 10 s.
    //   - notice the radio being switched off -> OnBluetoothAvailabilityChanged fires on the toggle,
    //     and the tree poll marks everything disconnected in ~6 s regardless.
    // Every one of those is either faster than the sweep or unaffected by it.
    private static readonly TimeSpan[] SweepIntervalSchedule =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
    ];

    // Extracted and pinned for the same reason as the other three schedules in this file.
    internal static TimeSpan SweepInterval(int consecutiveQuietSweeps) =>
        SweepIntervalSchedule[Math.Clamp(consecutiveQuietSweeps, 0, SweepIntervalSchedule.Length - 1)];

    // Sweeps run since the last connection-state change. Incremented by the sweep loop (one thread)
    // and zeroed by ResetIdleBackoff from whichever thread saw the transition — a tree poll, an
    // InTheHand callback, a connect continuation — so both sides go through Interlocked.
    //
    // The remaining race is benign and the schedule absorbs it: a transition landing inside the
    // delay zeroes the counter, and the increment that follows the delay takes it straight back to
    // 1. That is why the schedule's first two steps are both ActiveSweepInterval — a change is
    // never punished with an escalated interval on the very next pass.
    private int _consecutiveQuietSweeps;

    // The interval this sweep should wait, which is the schedule ONLY while the cheap local poll
    // that covers for it is demonstrably working.
    //
    // Fail-open on exactly one condition: the tree poll has never produced a snapshot
    // (_liveLowEnergyAddresses is null — the enumeration failed, or it has not run yet). In that
    // state nothing else is watching connection state at all, so the sweep is the only thing left
    // and it must keep its original cadence. Being wrong here costs enumerations; being wrong the
    // other way means a device whose state nobody is reading.
    private TimeSpan NextSweepInterval() =>
        _liveLowEnergyAddresses is null
            ? ActiveSweepInterval
            : SweepInterval(Volatile.Read(ref _consecutiveQuietSweeps));

    // How long each LE scan window stays open. The scan used to run continuously until every
    // tracked device reconnected — which can be forever: a device left powered off, or a stale
    // ghost pairing whose address changed on re-pair (both observed on tester machines). While
    // ANY scan runs, Windows' Device Association Service processes every advertisement in radio
    // range and accumulates state per rotating random address — memory it never returns until
    // the service restarts; a tester machine reached ~8.8 GB of DAS at ~64% CPU. Bounding the
    // scan to one window per sweep caps our share of that load at roughly ScanWindow /
    // ActiveSweepInterval duty; the sweep reopens the window while a tracked device is still
    // disconnected, and a GATT disconnect opens one immediately. A device powering on between
    // windows is still caught by the tree poll (~3-6 s, when Windows auto-reconnects it) or the
    // next window (≤ ActiveSweepInterval). See docs/plans/fix-das-memory-growth/.
    private static readonly TimeSpan ScanWindow = TimeSpan.FromSeconds(10);

    // How long to wait after a window CLOSES before another may open, by how many windows have
    // already closed with nothing having changed.
    //
    // Windowing alone bounded the scan's DUTY but not its LIFETIME: while any tracked device is
    // paired and not connected, the sweep reopens a window every ActiveSweepInterval, forever. The
    // 2026-09-02 audit read that as an improvement over the permanent scan and left it there. It is
    // not enough, because "paired and not connected" is the NORMAL, indefinite state of several
    // ordinary setups — a Bluetooth keyboard switched off for the night, and above all a TAIPAN
    // paired over Bluetooth but in use on its 2.4G dongle, which no amount of scanning can ever
    // reconnect while the switch is on the dongle. On those machines the service scanned 10 s in
    // every 30 s, 24/7, and Device Association Service accumulated per-advertiser state it never
    // returns until a machine that had run overnight had none left (QA, 2026-09-08).
    //
    // So the schedule escalates while NOTHING CHANGES and collapses the moment anything does. Any
    // connection-state transition — a device connecting, dropping, or being released — calls
    // ResetIdleBackoff, so the first window after a real event is always immediate and reconnection
    // behaves exactly as it did. Only the steady state of "still disconnected, still nothing to
    // see" pays, and it settles at 10 s of scanning per 5 minutes: a ~3 % duty instead of ~33 %.
    //
    // Waiting is safe because the WINDOW IS NOT THE RECONNECT PATH for anything PG ships. Windows
    // reconnects HID-over-GATT peripherals itself, and PollLowEnergyPresence sees that in the
    // device tree within ~3-6 s and calls MarkConnected (which also refreshes battery). The window
    // only accelerates OUR OWN GATT session; the worst this delays is a battery read, by at most
    // the ceiling below, on a device the App is already showing as connected.
    // Index 0 is the DEFINITION of "no quiet window has closed yet — nothing is owed", and the
    // production path never reads it: the close increments before it looks the delay up, so the
    // first charged window is index 1. Immediacy after a change comes from ResetIdleBackoff setting
    // _nextScanWindowUtc to MinValue, not from this entry. It is kept because a schedule that
    // starts at its second step reads as an off-by-one to everyone who meets it later.
    private static readonly TimeSpan[] ScanReopenSchedule =
    [
        TimeSpan.Zero,
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
    ];

    // The delay owed after this many consecutive closed windows. Extracted so the schedule is
    // pinned by a test rather than by reading the call site — same reason as RediscoveryDelay.
    internal static TimeSpan ScanReopenDelay(int consecutiveWindows) =>
        ScanReopenSchedule[Math.Clamp(consecutiveWindows, 0, ScanReopenSchedule.Length - 1)];

    // Windows closed since the last connection-state change, and the earliest time the next one may
    // open. Both guarded by _watcherLock, like every other watcher-lifecycle field.
    private int _consecutiveScanWindows;
    private DateTime _nextScanWindowUtc = DateTime.MinValue;

    // Identifies the run of "nothing has changed" a scan window belongs to. Bumped by every
    // ResetIdleBackoff; captured when a window opens and re-checked when it closes.
    //
    // Without it a reset landing INSIDE an open window was silently erased by that window's close.
    // The interleaving is ordinary, not exotic: a window is open, the tree poll sees a device drop
    // at +4 s, MarkDisconnected resets the backoff and calls SyncScanState — which returns at the
    // `Status: Started` check, correctly, because a window is already open — and then at +10 s the
    // close charged the window anyway and overwrote _nextScanWindowUtc with a 30 s delay. So the
    // window after a fresh disconnect was NOT immediate, which is precisely the invariant the whole
    // backoff rests on and precisely what this plan's own acceptance criterion promises.
    //
    // The sweep's schedule absorbs its version of this race by repeating its first step; the scan
    // cannot borrow that trick, because its first step is what makes the window immediate. So the
    // scan says it literally instead: a window that SPANNED a change is not evidence that nothing
    // is happening, and is not charged.
    private int _idleEpoch;

    // Whether a closing window counts as a quiet one. Extracted and pinned for the same reason as
    // ShouldCloseScanWindow: this is a race whose failure is invisible in every other test.
    internal static bool ShouldChargeQuietWindow(int openedEpoch, int currentEpoch) =>
        openedEpoch == currentEpoch;

    // Anything actually happened, so both idle escalations start over: the next scan window is owed
    // immediately, and the next sweep goes back to the active cadence. Called from every
    // connection-state TRANSITION (MarkConnected / MarkDisconnected / DeviceGattDisconnected /
    // RegisterDevice) and from a deliberate discovery restart — never from the sweep itself, which
    // is what the escalations exist to slow down.
    //
    // One hook for both on purpose: they answer the same question ("has anything changed?") and
    // splitting them would let one drift out of step with the other, which is precisely how a
    // bound stops being a bound.
    private void ResetIdleBackoff()
    {
        Interlocked.Exchange(ref _consecutiveQuietSweeps, 0);

        lock (_watcherLock)
        {
            _consecutiveScanWindows = 0;
            _nextScanWindowUtc = DateTime.MinValue;

            // Any window currently open now belongs to a superseded run of quiet, so its close
            // must not charge it — see _idleEpoch.
            _idleEpoch++;
        }
    }

    // How long a tracked, paired, tree-disconnected device may go without a connection before it
    // stops creating scan NEED. A stale ghost pairing — the old entry left behind when a device's
    // BLE address changed on re-pair (Taipan measured D104… -> D112…) — can never reconnect, and
    // one such entry kept the scan condition permanently true on a tester machine: windows
    // reopening every sweep, forever, for a device that cannot come back. Judged on Windows'
    // DEVPKEY_Bluetooth_LastConnectedTime (see DeviceNodeQuery), which is stamped on every
    // connect and disconnect, so "14 days silent" genuinely means 14 days without a link — a
    // holiday, not a ghost, stays inside it. The veto touches ONLY the need computation: a stale
    // device stays in the matcher, so a window opened for any other device still reconnects it
    // if it ever advertises again. See docs/plans/add-stale-pairing-detection/.
    private static readonly TimeSpan StalePairingThreshold = TimeSpan.FromDays(14);

    // How often a CONNECTED device's GATT battery is re-read. Until this existed the level was
    // fetched when a client attached and on a connection-state change, and never again — so a mouse
    // left connected over Bluetooth showed its connect-time percentage for the whole session while
    // the real battery drained. That is the same staleness PollBatteriesAsync prevents on the
    // wired/dongle side.
    //
    // Five minutes: a battery does not move faster than that, and this is a radio transaction with
    // a device running on that battery. Cheaper than the 2.4G case (the BLE link is already up and
    // Windows reads the same characteristic for its own tray), but not free, so it gets its own
    // clock.
    //
    // That clock is EVALUATED by the 3 s presence poll, not by the sweep. It used to ride the
    // sweep, which was fine at a flat 30 s and stopped being fine the moment the sweep began
    // escalating (SweepIntervalSchedule): a five-minute gate checked on a five-minute clock beats
    // at up to ten minutes, and it does so in the ordinary case — a connected mouse generates no
    // transitions, so "quiet" is exactly where a working device lives. A gate is only as accurate
    // as the clock that evaluates it.
    private static readonly TimeSpan BatteryRefreshInterval = TimeSpan.FromMinutes(5);
    private DateTime _lastBatteryRefreshUtc = DateTime.MinValue;

    private async Task PeriodicReconnectCheckAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // The idle cadence is only safe once we have actually looked and found nothing to
                // track. If enumeration has never succeeded — Bluetooth was off when the service
                // started — an empty tracking list means "unknown", not "nothing there", and the
                // sweep is the only thing that will discover the device when the radio returns.
                // Backing off to five minutes in exactly that state is what made a re-enabled
                // adapter appear broken.
                var idle = _trackedDevices.IsEmpty && _pairedEnumerationEverSucceeded;
                await Task.Delay(idle ? IdleSweepInterval : NextSweepInterval(), ct);

                // Counted BEFORE the body, so it means "sweeps run since the last connection-state
                // change". A change during this pass calls ResetIdleBackoff and zeroes it again,
                // which is the ordering we want: the sweep that observed the change does not count
                // as a quiet one.
                Interlocked.Increment(ref _consecutiveQuietSweeps);

                // Discovering NEWLY paired devices is isolated from re-checking the ones we already
                // track, because this is the call that fails when the adapter is switched off
                // (InTheHand throws ThrowSecurityExceptionOnMissingDeviceCapability). It used to sit
                // in the same try as everything below, so switching Bluetooth off in Windows threw
                // here every 30 s and the re-check never ran — a device connected over Bluetooth kept
                // showing as connected in the App indefinitely, because nothing ever told it
                // otherwise. Turning the radio off is exactly when that re-check matters most.
                //
                // That CALL's failure — not this block's — is also the sweep's radio-is-gone signal,
                // and deliberately so. Per-device state cannot serve as one: Gatt.IsConnected keeps
                // returning a stale `true` once the radio is off (measured 2026-08-14, see
                // OnBluetoothAvailabilityChanged). Nor is GetAvailabilityAsync used here — one call
                // deciding to disconnect every device on every 30 s pass is too much authority for
                // an API we have only ever seen behave on one machine, and if it were ever wrong the
                // App would show a working Bluetooth mouse as permanently absent. This throw is the
                // failure mode actually observed in the logs, and it comes from a call we have to
                // make anyway.
                //
                // The try therefore covers ONLY the enumeration call. It used to wrap the registration loop
                // below as well, which quietly made any single device's failure look like the radio
                // disappearing: reading Gatt.IsConnected / IsPaired / Name on one stale handle — a
                // paired headset left over from a sleep/wake, say — threw, set radioGone, and
                // disconnected every tracked device including a mouse that was working perfectly.
                // The `continue` then skipped the tracked-device loop, so nothing re-registered the
                // mouse, and because MarkDisconnected only raises on a transition the card stayed
                // "disconnected" for as long as the stale handle kept throwing once per sweep.
                //
                // That is precisely the "working Bluetooth mouse shown as permanently absent"
                // outcome the note above rejects GetAvailabilityAsync for. The enumeration failing
                // is a statement about the ADAPTER; one device failing is a statement about that
                // device.
                var radioGone = false;
                IReadOnlyList<BluetoothDevice> pairedDevices = [];

                try
                {
                    // Materialised inside the try so a lazily-thrown enumeration still counts as the
                    // call failing rather than escaping into the loop below.
                    pairedDevices = [.. await Bluetooth.GetPairedDevicesAsync()];
                    _pairedEnumerationEverSucceeded = true;
                }
                catch (Exception ex)
                {
                    radioGone = true;
                    _logger?.LogDebug(ex, "[BT] Paired-device enumeration unavailable (adapter off?)");
                }

                if (radioGone)
                {
                    // Nothing is reachable, and the per-device check below would wrongly confirm the
                    // opposite. Skip it and report the truth.
                    MarkAllDisconnected();
                    continue;
                }

                // Second, narrower radio-gone signal: a stack where a switched-off adapter yields
                // an EMPTY enumeration instead of throwing would sail past the catch above, and the
                // tracked loop below would then read Gatt.IsConnected's stale `true` forever. With
                // devices tracked but nothing enumerable, ask the radio directly — availability is
                // consulted only in this already-suspicious state and only its explicit "no"
                // disconnects, so the objection above (one call with the authority to disconnect
                // everything on every 30 s pass) still stands addressed.
                if (pairedDevices.Count == 0 && !_trackedDevices.IsEmpty)
                {
                    try
                    {
                        if (!await Bluetooth.GetAvailabilityAsync())
                        {
                            _logger?.LogDebug("[BT] Empty enumeration and radio reports unavailable — marking tracked devices disconnected.");
                            MarkAllDisconnected();
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        // Unknown is not "off": fall through to the per-device loop rather than
                        // disconnecting on a probe that itself failed.
                        _logger?.LogDebug(ex, "[BT] Availability probe failed during an empty-enumeration sweep.");
                    }
                }

                // Per-device guarded, for the same reason the tracked loop below is. Logged and
                // skipped rather than marked disconnected: a device found here may not be tracked
                // yet, and raising a disconnect for something that was never connected is noise. If
                // it IS tracked, the loop below evaluates it and marks it properly.
                foreach (var device in pairedDevices)
                {
                    try
                    {
                        if (DeviceNeedToTrack(device))
                        {
                            RegisterDevice(device);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "[BT] Skipped a paired device whose state could not be read.");
                    }

                    // Not ours (or unreadable): release the native object the enumeration resolved
                    // for it rather than leaving it to the GC. This runs every sweep, for every
                    // paired device on the machine — see ReleaseUnretained.
                    ReleaseUnretained(device);
                }

                // ALWAYS runs. This is what catches a device that dropped without firing
                // GattServerDisconnected — including the whole radio going away.
                foreach (var device in _trackedDevices.Values)
                {
                    try
                    {
                        RegisterDevice(device);
                    }
                    catch (Exception ex)
                    {
                        // A device whose state cannot be read is not a device we can claim is
                        // connected, so fail toward disconnected. Note this catch is NOT what
                        // handles the radio being switched off — Gatt.IsConnected returns a stale
                        // `true` there rather than throwing, which is why the ENUMERATION FAILURE
                        // above is used as the radio-is-gone signal instead. Per-device so one bad
                        // handle cannot abort the sweep.
                        MarkDisconnected(device, ex);
                    }
                }

                SyncScanState();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "[BT] Periodic reconnect check failed");
            }
        }
    }

    // clientName: supplied by the classic-Bluetooth path, which resolves the catalog itself by
    // Bluetooth VID/PID. GetClientName keys on a BLE device id and cannot answer for a device whose
    // identity is a Container ID.
    private void RaiseDeviceConnectionChanged(string deviceId,
        string deviceName,
        bool isConnected,
        bool isPaired,
        int batteryLevel,
        string? clientName = null)
    {
        try
        {
            var args = new BluetoothDeviceConnectionChangedEventArgs
            {
                DeviceId = deviceId,
                DeviceName = deviceName,
                IsConnected = isConnected,
                IsPaired = isPaired,
                Timestamp = DateTime.UtcNow,
                ClientName = clientName ?? ResolveClientName(deviceId, deviceName),
                BatteryLevel = batteryLevel,
            };

            // Logged for BOTH directions, deliberately. Until 2026-08-20 only disconnects were
            // logged (MarkDisconnected) while every connect went out silently from RegisterDevice,
            // so a status oscillating between the two showed up in the log as a lone "marked
            // disconnected" repeating every 30 s with no visible cause. ClientName is included
            // because the App matches its card by that string and drops the event when it is empty
            // — a device the host can read the battery of but cannot name never becomes a card.
            _logger.LogInformation(
                "[BT] connection changed: {Name} connected={Connected} paired={Paired} " +
                "clientName='{ClientName}' battery={Battery} id={DeviceId}",
                args.DeviceName, args.IsConnected, args.IsPaired, args.ClientName,
                args.BatteryLevel, args.DeviceId);

            DeviceConnectionChanged?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error raising DeviceConnectionChanged event for device {DeviceId}", deviceId);
        }
    }

    // The catalog name for a device, by id first and by NAME as a fallback.
    //
    // The id lookup reads DeviceMap.DeviceId, a cache DeviceNeedToTrack fills in. That cache can be
    // stale or unset — the BLE address changes on re-pair, and an event can be raised for a device
    // the tracking has not re-registered yet. An empty ClientName is not a cosmetic problem: the App
    // matches its card by that string and discards the event entirely, so the device silently never
    // appears. Falling back to the same name match DeviceNeedToTrack uses keeps a changed address
    // from costing us the identity.
    private string ResolveClientName(string deviceId, string? deviceName)
    {
        // 1. What this device was called when we last tracked it, keyed by its own id.
        if (!string.IsNullOrEmpty(deviceId) &&
            _catalogNameByDeviceId.TryGetValue(deviceId, out var remembered))
            return remembered;

        // 2. The legacy DeviceMap.DeviceId lookup, kept because other catalog paths may populate it.
        var byId = _deviceInfoProvider.GetClientName(deviceId);
        if (!string.IsNullOrEmpty(byId)) return byId;

        // 3. The device's own name. The only one of the three that survives an address change, and
        //    the reason a re-paired device can be identified at all before it is re-tracked.
        return MatchCatalogNameByDeviceName(_deviceInfoProvider.DevicesMaps, deviceName);
    }

    // The name match, extracted so the rule is testable without a device or a provider.
    //
    // An empty ClientName is not cosmetic: the App matches its card by that string and discards the
    // event outright, so a device that cannot be named silently never appears. That failure mode has
    // now bitten twice, which is why this is a pinned rule rather than an inline expression.
    internal static string MatchCatalogNameByDeviceName(IEnumerable<DeviceMap> maps, string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return string.Empty;

        // Same case rule as DeviceNeedToTrack — see the note there.
        return maps.FirstOrDefault(m =>
            m.HasBluetooth && !string.IsNullOrEmpty(m.Name) &&
            deviceName.Contains(m.Name, StringComparison.OrdinalIgnoreCase))?.Name
            ?? string.Empty;
    }

    private async Task<string> GetDeviceName(BluetoothDevice device)
    {
        try
        {
            // 1. Get the Generic Access service
            var service = await device.Gatt.GetPrimaryServiceAsync(GattServiceUuids.GenericAccess);

            if (service != null)
            {
                // 2. Get the Device Name characteristic
                var characteristic = await service.GetCharacteristicAsync(GattCharacteristicUuids.DeviceName);

                if (characteristic != null)
                {
                    // 3. Read and decode the value
                    var bytes = await characteristic.ReadValueAsync();
                    string gattDeviceName = System.Text.Encoding.UTF8.GetString(bytes);
                    return gattDeviceName;
                }
            }
        }
        catch(OperationCanceledException) 
        {
        
        }

        return string.Empty;
    }

    private BluetoothDeviceState Map(BluetoothDevice btDevice, int batteryLevel = -1)
    {
        return new BluetoothDeviceState
        {
            DeviceId = btDevice.Id,
            Name = btDevice.Name,
            // The device tree, exactly as RegisterDevice resolves it. This is the ONLY snapshot a
            // newly attached App client receives — everything after it is change events, and those
            // only fire on a transition. Leaving this on Gatt.IsConnected meant a device the tree
            // already called connected could hand the App a "disconnected" snapshot that no later
            // event would ever correct, because from the service's side nothing had changed.
            IsConnected = ResolveConnected(
                LowEnergyTreeSaysConnected(btDevice), btDevice.Gatt.IsConnected, btDevice.IsPaired),
            IsPaired = btDevice.IsPaired,
            ClientName = ResolveClientName(btDevice.Id, btDevice.Name),
            BatteryLevel = batteryLevel,
        };
    }

    // ── Classic Bluetooth (no BLE) ────────────────────────────────────────────────────────────
    //
    // Everything above this point is BLE: InTheHand's stack, GATT services, advertisement scanning.
    // A classic Bluetooth audio device is invisible to all of it — PG Cosmo has no BTHLE node
    // anywhere on the machine, so it never appears in the paired list and has no GATT battery
    // service to read (proven 2026-08-17, docs/plans/add-cosmo-bluetooth/).
    //
    // Windows does know it, through the device tree, so these are polled on the same sweep and
    // reported through the SAME event and the same BluetoothDeviceState. Everything downstream —
    // DeviceService, the gRPC contract, the App's Bluetooth card path — is unchanged and cannot tell
    // the two kinds apart, which is the point: a headset is a headset.
    //
    // Keyed by Container ID, the identity Windows groups a physical device's nodes under and the one
    // the App's audio layer already addresses endpoints by.
    private readonly ConcurrentDictionary<string, BluetoothDeviceState> _classicDevices = new();

    // Its own loop, deliberately NOT the BLE sweep's. That sweep runs every 30 s — and every five
    // minutes when idle — which is the right cadence for radio enumeration and far too slow for a
    // headset the user switches between Bluetooth and its dongle: the card lagged the hardware by up
    // to half a minute. This query is a local device-tree read with no radio involved, so it is cheap
    // enough to run often, and the transport switch is what the user actually watches.
    private static readonly TimeSpan ClassicPollInterval = TimeSpan.FromSeconds(3);

    // Consecutive polls a tracked BLE device has been missing before we believe it. See
    // PollLowEnergyPresence for why this exists and why classic does not need it.
    private const int LowEnergyMissesBeforeDisconnect = 2;
    private readonly ConcurrentDictionary<string, int> _lowEnergyMisses = new();

    // Consecutive polls a device's node has been ABSENT. Separate from _lowEnergyMisses because the
    // two lead to different actions: a miss marks disconnected, an absence releases the GATT link.
    private readonly ConcurrentDictionary<string, int> _lowEnergyAbsences = new();

    // The most recent successful device-tree snapshot: which BLE addresses are present AND report
    // IsConnected. Replaced wholesale, never mutated, so a reader always sees one consistent sweep.
    //
    // This is the source of truth for BLE liveness in preference to Gatt.IsConnected, which this
    // file already documents as unreliable in one direction (it returns a stale `true` once the
    // radio is off) and which turned out to be unreliable in the other too: after a device is
    // removed and re-paired in Windows, the tracked BluetoothDevice instance is dead and its
    // Gatt.IsConnected never becomes true again, so the card never came back until the App was
    // restarted. The tree answers both directions correctly and was measured doing so.
    private volatile HashSet<string>? _liveLowEnergyAddresses;

    // The tree's DEVPKEY_Bluetooth_LastConnectedTime per present low-energy address. Replaced
    // wholesale by the 3 s poll, never mutated — same discipline as _liveLowEnergyAddresses.
    // Null until the first successful poll, which the staleness rule reads as "no opinion".
    private volatile Dictionary<string, DateTime>? _lastConnectedByAddress;

    // Device ids currently vetoed out of the scan-need computation, kept ONLY so the exclusion
    // is logged on the transition rather than every sweep. Cleared when the device connects
    // (MarkConnected / RegisterDevice) or is released, so a later re-staleness logs again.
    private readonly ConcurrentDictionary<string, bool> _scanNeedExcluded = new();

    // device id -> catalog name. Replaces the old mutation of DeviceMap.DeviceId; see
    // DeviceNeedToTrack for why the catalog is the wrong place to keep this.
    private readonly ConcurrentDictionary<string, string> _catalogNameByDeviceId = new();

    // Last battery percentage published per device id, so a steady device does not re-announce the
    // same figure every 3 s.
    private readonly ConcurrentDictionary<string, int> _lastPublishedBattery = new();

    // Bounds the rediscovery the poll can trigger: one in flight at a time, and never more often
    // than the current step of the schedule below. GetPairedDevicesAsync is the expensive call the
    // 30 s sweep exists to ration, so pulling it forward must not turn into running it on the 3 s
    // clock.
    //
    // A flat cooldown was NOT a bound, which is the bug this schedule fixes. `unexplained` can stay
    // true forever — a device whose InTheHand name does not match the catalog, or whose id does not
    // parse to an address, is never resolved by the enumeration this triggers — so rediscovery ran
    // every 10 s for the life of the service, each pass a full DeviceInformation enumeration on the
    // Device Association Framework path. See
    // docs/audits/2026-09-02-device-association-memory-audit.md (F3).
    //
    // The first two steps keep a genuine re-pair recovering exactly as fast as it used to; the floor
    // only applies once the condition has demonstrably stopped resolving.
    private static readonly TimeSpan[] RediscoverySchedule =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(5),
    ];

    // Consecutive rediscoveries that have run without the condition clearing. Reset the moment a
    // poll finds nothing unexplained, so an ordinary re-pair never pays the backoff twice. Only ever
    // touched from PollLowEnergyPresence, i.e. the single 3 s loop thread — the same single-writer
    // property _lastRediscoveryUtc already relies on.
    private int _consecutiveRediscoveries;

    // The delay before the next rediscovery, given how many have already run without resolving.
    // Extracted so the schedule is pinned by a test rather than by reading the call site.
    internal static TimeSpan RediscoveryDelay(int consecutiveRediscoveries) =>
        RediscoverySchedule[Math.Clamp(consecutiveRediscoveries, 0, RediscoverySchedule.Length - 1)];

    // Matches the bound ReadGattBatteryAsync puts on its own radio call.
    private static readonly TimeSpan RediscoveryEnumerationTimeout = TimeSpan.FromSeconds(10);
    private DateTime _lastRediscoveryUtc = DateTime.MinValue;
    private int _rediscoveryInFlight;

    // Fast disconnect detection for BLE, from the local device tree.
    //
    // Until this existed a BLE device's disconnect was only ever noticed by the 30 s BLE sweep — and
    // five minutes if nothing else was tracked — because that sweep is the only thing that clears
    // the card's Bluetooth state. Switching a Bluetooth mouse off, or removing its pairing in
    // Windows settings, left the App showing it connected for well over twenty seconds.
    //
    // Measured 2026-08-20 on a PG Taipan, polling the tree every 2 s:
    //   switched OFF                    -> the node's IsConnected flipped True -> False
    //   pairing removed in Windows      -> the node DISAPPEARED outright
    // Both showed up immediately, so Windows was never the delay — the sweep cadence was. Both are
    // disconnect signals and this treats them alike: a device is live only while a present node
    // says IsConnected.
    //
    // Same reasoning as ClassicBluetoothPollAsync: a device-tree read is local and cheap, unlike
    // Bluetooth.GetPairedDevicesAsync, so it can run often. It rides that loop rather than opening a
    // third one.
    //
    // ONE-DIRECTIONAL. This only ever marks a device DISCONNECTED; reconnection stays with the
    // advertisement watcher, the GATT events and the sweep. That keeps a cheap local heuristic from
    // ever asserting a device is present, which is the direction that would put a device on screen
    // the App cannot actually talk to.
    private void PollLowEnergyPresence()
    {
        var nodes = DeviceNodeQuery.EnumerateLowEnergyNodes();

        // null means the enumeration itself failed, which is NOT evidence about any device. Treating
        // it as "nothing present" would disconnect every tracked device on one bad call — the same
        // over-reach PeriodicReconnectCheckAsync rejects GetAvailabilityAsync for.
        if (nodes is null) return;

        var live = LiveLowEnergyAddresses(nodes);
        var present = PresentLowEnergyAddresses(nodes);
        var batteryByAddress = LowEnergyBatteryByAddress(nodes);
        _liveLowEnergyAddresses = live;
        _lastConnectedByAddress = LowEnergyLastConnectedByAddress(nodes);

        foreach (var device in _trackedDevices.Values)
        {
            var address = TrackedBluetoothAddress(device);

            // No resolvable address means we cannot ask the question for this device, so we do not
            // answer it — in EITHER direction. Never act on a guess.
            if (address is null) continue;

            string id;
            try { id = device.Id; }
            catch { continue; }

            // GONE FROM WINDOWS. Not merely disconnected — unpaired, so it is not our device any
            // more. Marking it disconnected is not enough: the instance we hold keeps reporting
            // IsPaired=true, the advertisement watcher keeps calling ConnectAsync on it, and the
            // battery refresh keeps succeeding over the GATT link we are still holding open. Each of
            // those re-asserts "connected" within seconds, which is why a removed device's card
            // stayed active and flapped. Let go of it instead.
            if (!present.Contains(address))
            {
                // Confirmed over consecutive polls, exactly like the disconnect below. Release is
                // strictly MORE destructive — it drops the GATT link and disposes the instance — so
                // it cannot be the one path that acts on a single sample. A node can also briefly
                // vanish for reasons that are not an unpairing.
                var absences = _lowEnergyAbsences.AddOrUpdate(id, 1, (_, n) => n + 1);
                if (absences >= LowEnergyMissesBeforeDisconnect)
                    ReleaseTrackedDevice(device, id);
                continue;
            }

            _lowEnergyAbsences.TryRemove(id, out _);

            if (live.Contains(address))
            {
                _lowEnergyMisses.TryRemove(id, out _);

                // Reconnect, on the same 3 s clock as the disconnect. Removing a device in Windows
                // and pairing it again left the card offline until the App was restarted: the sweep
                // re-registers the device but decides connectedness from Gatt.IsConnected on an
                // instance that is now dead, so it never read as connected again.
                MarkConnected(device);
                PublishTreeBattery(device, id, batteryByAddress.GetValueOrDefault(address));
                continue;
            }

            // Confirmation before acting, which the classic path does not need: a BLE link is a
            // radio link and a single sweep can catch it mid-blip, whereas showing a working mouse
            // as absent is the worst failure mode this file has. Two polls is ~6 s — still five
            // times faster than the sweep it replaces, and no longer a coin flip on one sample.
            var misses = _lowEnergyMisses.AddOrUpdate(id, 1, (_, n) => n + 1);
            if (misses >= LowEnergyMissesBeforeDisconnect)
                MarkDisconnected(device);
        }

        TriggerRediscoveryIfUnexplained(nodes, live);

        // Rides the 3 s poll rather than the sweep, because the sweep's cadence is no longer a
        // clock — it escalates to five minutes while nothing changes (SweepIntervalSchedule), and
        // "nothing changes" is the normal state of a connected Bluetooth mouse sitting on a desk
        // all day. RefreshConnectedBatteries gates itself on BatteryRefreshInterval, and a gate is
        // only as accurate as the clock that EVALUATES it: left on the sweep, a refresh falling due
        // one second after a pass waited for the next one, so the five-minute interval beat at up
        // to ten. Here the gate is evaluated every 3 s and means exactly what it says.
        //
        // Cheap: for all but one call in a hundred this is a subtraction and a return.
        RefreshConnectedBatteries();
    }

    // The battery percentage Windows holds for each present low-energy node.
    //
    // This costs nothing: EnumerateLowEnergyNodes already reads DEVPKEY_Bluetooth_Battery on every
    // node, and the value was simply being discarded. It is the SAME property the Windows tray
    // shows, which is why it is worth preferring — the App and the tray can no longer disagree
    // because they are now reading one number.
    internal static Dictionary<string, int> LowEnergyBatteryByAddress(IEnumerable<DeviceNode> nodes)
    {
        var byAddress = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (node.BatteryPercent is not { } percent) continue;
            if (ClassicBluetoothDeviceResolver.TryParseAddress(node.InstanceId) is { } address)
                byAddress[address] = percent;
        }

        return byAddress;
    }

    // The tree's last-connected timestamp per address, for the stale-pairing veto. Same shape and
    // address parsing as the battery map above.
    internal static Dictionary<string, DateTime> LowEnergyLastConnectedByAddress(IEnumerable<DeviceNode> nodes)
    {
        var byAddress = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (node.LastConnectedUtc is not { } lastConnected) continue;
            if (ClassicBluetoothDeviceResolver.TryParseAddress(node.InstanceId) is { } address)
                byAddress[address] = lastConnected;
        }

        return byAddress;
    }

    // Publishes the tree's battery for a live device, on CHANGE only.
    //
    // Before this, a BLE device's level was refreshed solely by the 5-minute GATT read
    // (BatteryRefreshInterval), so the card lagged the Windows tray by up to five minutes — reported
    // as the App showing 44 % beside a tray showing 43 %, and only correcting when the details page
    // was opened. The tree gives the same figure on the 3 s presence clock for free.
    //
    // The GATT read is deliberately left in place: it is the source Windows itself populates this
    // property from, and it stays the fallback for a device whose node carries no battery at all.
    //
    // Change-only, because RaiseDeviceConnectionChanged is not free — it logs and crosses gRPC to the
    // App. A steady device therefore costs one dictionary lookup per poll and publishes nothing.
    private void PublishTreeBattery(BluetoothDevice device, string id, int treeBattery)
    {
        if (!ShouldPublishTreeBattery(treeBattery, _lastPublishedBattery.TryGetValue(id, out var last) ? last : null))
            return;

        _lastPublishedBattery[id] = treeBattery;

        string name;
        try { name = device.Name; }
        catch { name = string.Empty; }

        bool paired;
        try { paired = device.IsPaired; }
        catch { paired = true; }

        // connected: true is correct by construction — this only runs for an address the tree just
        // reported as live, and the tree is the single owner of connection state.
        RaiseDeviceConnectionChanged(id, name, true, paired, treeBattery);
    }

    // A reading is worth publishing when it is real and different from the last one we sent.
    // GetValueOrDefault yields 0 for a node with no battery property, which is not a reading.
    internal static bool ShouldPublishTreeBattery(int treeBattery, int? lastPublished) =>
        treeBattery > 0 && treeBattery != lastPublished;

    // Stop tracking a device Windows no longer has, and release the GATT link we are holding.
    //
    // Without the release the connection stays up inside this process: InTheHand keeps answering
    // battery reads over it, Gatt.IsConnected stays true, and the device can even keep a node in the
    // tree because WE are what is keeping it alive. Dropping the subscription and the instance is
    // what actually ends it.
    //
    // Re-pairing is not a problem for this: TriggerRediscoveryIfUnexplained sees the device live in
    // the tree, re-runs the paired enumeration and registers a FRESH instance — which is the one
    // thing that was never happening before, and the reason a re-added device needed an App restart.
    private void ReleaseTrackedDevice(BluetoothDevice device, string id)
    {
        // Tell the App first, while we still have a name to report.
        MarkDisconnected(device);

        _trackedDevices.TryRemove(id, out _);
        _trackedStatuses.TryRemove(id, out _);
        _lowEnergyMisses.TryRemove(id, out _);
        _lowEnergyAbsences.TryRemove(id, out _);
        _pendingConnections.TryRemove(id, out _);
        _lastPublishedBattery.TryRemove(id, out _);
        _connectBackoff.TryRemove(id, out _);
        _scanNeedExcluded.TryRemove(id, out _);

        try { device.GattServerDisconnected -= DeviceGattDisconnected; } catch { /* already gone */ }
        try { device.Gatt.Disconnect(); } catch { /* the link may already be down */ }
        try { device.Dispose(); } catch { /* nothing left to release */ }

        _logger?.LogInformation(
            "[BT LE] {Id} is no longer present in Windows — released and untracked.", id);

        // The scan need just changed: this device may have been the only reason the watcher was
        // running, and nothing else re-evaluates that after a release. Without it the radio kept
        // scanning for a device we had already let go of.
        SyncScanState();
    }

    // A live device the tracking cannot account for means the tracking is stale — so refresh it.
    //
    // The poll can only re-mark devices already in _trackedDevices; it cannot CREATE a
    // BluetoothDevice, and after a device is removed in Windows and paired again the instance we
    // hold is dead (its Gatt never reconnects) or the device was never tracked at all. Either way
    // the card stayed offline until the App was restarted, because the only thing that builds fresh
    // instances is the paired-device enumeration on the 30 s sweep.
    //
    // Rather than fabricate state from the tree, the cheap poll uses it as a TRIGGER: the tree can
    // see that one of our devices is live sooner than the sweep will, so it pulls the authoritative
    // enumeration forward instead of waiting for the next tick.
    private void TriggerRediscoveryIfUnexplained(IReadOnlyList<DeviceNode> nodes, HashSet<string> live)
    {
        // Nothing live is nothing unexplained, so this resets the backoff too — otherwise a device
        // going away entirely would leave the counter escalated and make the NEXT unexplained
        // device wait out a delay it never earned.
        if (live.Count == 0)
        {
            _consecutiveRediscoveries = 0;
            return;
        }

        // Only OUR devices count. Every other BLE peripheral on the machine is live and untracked by
        // design, and treating those as unexplained would re-enumerate forever on a 3 s clock.
        var unexplained = false;
        foreach (var node in nodes)
        {
            if (!node.IsConnected) continue;
            if (!IsCatalogBluetoothDevice(node.FriendlyName)) continue;

            var address = ClassicBluetoothDeviceResolver.TryParseAddress(node.InstanceId);
            if (address is null) continue;
            if (IsTrackedAsConnected(address)) continue;

            unexplained = true;
            break;
        }

        if (!unexplained)
        {
            // Resolved — the next unexplained device starts from the fast end of the schedule again.
            _consecutiveRediscoveries = 0;
            return;
        }

        if (DateTime.UtcNow - _lastRediscoveryUtc < RediscoveryDelay(_consecutiveRediscoveries)) return;
        if (Interlocked.Exchange(ref _rediscoveryInFlight, 1) == 1) return;

        _lastRediscoveryUtc = DateTime.UtcNow;
        _consecutiveRediscoveries++;
        _ = Task.Run(async () =>
        {
            try
            {
                _logger?.LogInformation(
                    "[BT LE] A catalogued device is live but not tracked as connected — refreshing paired devices.");

                // Bounded. _rediscoveryInFlight is cleared in the finally below, but an await that
                // never returns never reaches it — and this is precisely the call the sweep already
                // documents as the one that misbehaves when the adapter goes away. Unbounded, one
                // hang would silently disable reconnect-after-re-pair for the life of the process
                // with nothing in the log to say so.
                var enumeration = Bluetooth.GetPairedDevicesAsync();
                if (await Task.WhenAny(enumeration, Task.Delay(RediscoveryEnumerationTimeout)) != enumeration)
                {
                    _logger?.LogWarning(
                        "[BT LE] Paired-device enumeration did not return within {Seconds}s — abandoning this refresh.",
                        RediscoveryEnumerationTimeout.TotalSeconds);

                    // Abandoning the WAIT does not abandon the CALL: it still completes later and
                    // still hands back a live native object per paired device, with nobody left
                    // looking at the result. Release them when it does — see ReleaseUnretained.
                    _ = enumeration.ContinueWith(
                        completed =>
                        {
                            foreach (var device in completed.Result)
                                ReleaseUnretained(device);
                        },
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                    return;
                }

                foreach (var device in await enumeration)
                {
                    try
                    {
                        if (DeviceNeedToTrack(device))
                        {
                            RegisterDevice(device);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "[BT LE] Skipped a paired device during refresh.");
                    }

                    ReleaseUnretained(device);
                }
            }
            catch (Exception ex)
            {
                // The enumeration is the call that throws when the adapter is off. Nothing to do
                // but wait for the next poll; this must never take a device down.
                _logger?.LogDebug(ex, "[BT LE] Paired-device refresh failed.");
            }
            finally
            {
                Interlocked.Exchange(ref _rediscoveryInFlight, 0);
            }
        });
    }

    private bool IsTrackedAsConnected(string address)
    {
        foreach (var device in _trackedDevices.Values)
        {
            if (!string.Equals(TrackedBluetoothAddress(device), address, StringComparison.OrdinalIgnoreCase))
                continue;

            try { return _trackedStatuses.TryGetValue(device.Id, out var connected) && connected; }
            catch { return false; }
        }

        return false;
    }

    // Same rule DeviceNeedToTrack applies to an InTheHand device, against a device-tree name.
    private bool IsCatalogBluetoothDevice(string? friendlyName)
    {
        if (string.IsNullOrWhiteSpace(friendlyName)) return false;

        // Same case rule as DeviceNeedToTrack — see the note there.
        return _deviceInfoProvider.DevicesMaps.Any(m =>
            m.HasBluetooth && !string.IsNullOrEmpty(m.Name) &&
            friendlyName.Contains(m.Name, StringComparison.OrdinalIgnoreCase));
    }

    // Precedence for "is this device connected", extracted so the tri-state cannot be quietly
    // collapsed. The trap is that `treeSaysConnected` is bool? and a null means the tree has NO
    // OPINION — no successful enumeration yet, or an address we could not resolve. Reading null as
    // false would disconnect every tracked device on startup, before the first poll has run.
    internal static bool ResolveConnected(bool? treeSaysConnected, bool gattConnected, bool isPaired) =>
        treeSaysConnected ?? (gattConnected && isPaired);

    // ResolveConnected applied to a live device, so the scan trigger, the connect trigger and the
    // published state cannot drift apart by each assembling the precedence themselves. Reading the
    // device can throw once the radio is gone; that is left to the caller, because the two triggers
    // fail in opposite directions (SyncScanState scans, OnAdvertisementReceived gives up).
    private bool ResolvedConnected(BluetoothDevice device) =>
        ResolveConnected(LowEnergyTreeSaysConnected(device), device.Gatt.IsConnected, device.IsPaired);

    // The tree's verdict on a device, or null when it has none — no successful enumeration yet, or
    // an address we cannot resolve. Null means "no opinion", NOT "disconnected".
    private bool? LowEnergyTreeSaysConnected(BluetoothDevice device)
    {
        var live = _liveLowEnergyAddresses;
        if (live is null) return null;

        var address = TrackedBluetoothAddress(device);
        return address is null ? null : live.Contains(address);
    }

    // Whether this tracked device is a stale ghost pairing that must not keep the LE scan alive.
    // Owns the exclusion-transition logging as a side effect: the veto itself is re-evaluated
    // every sweep (cheap), but the log line fires once per transition into staleness — a support
    // case needs "it was excluded, here is when and why", not one line per 30 s forever.
    //
    // Every unreadable input fails toward NOT excluded: an id we cannot read, a tree with no
    // opinion, an address with no timestamp all keep the device counting toward scan need.
    // Being wrong in that direction costs scan windows (today's behaviour); being wrong the
    // other way silently kills reconnection for a real device.
    private bool IsExcludedAsStalePairing(BluetoothDevice device)
    {
        string id;
        try { id = device.Id; }
        catch { return false; }

        DateTime? lastConnected = null;
        if (_lastConnectedByAddress is { } byAddress &&
            TrackedBluetoothAddress(device) is { } address &&
            byAddress.TryGetValue(address, out var value))
            lastConnected = value;

        // The `last` capture makes the rule's "stale implies a timestamp" contract visible to the
        // compiler, so the log below needs no null-forgiveness.
        if (lastConnected is not { } last ||
            !IsStalePairing(last, LowEnergyTreeSaysConnected(device), DateTime.UtcNow, StalePairingThreshold))
        {
            _scanNeedExcluded.TryRemove(id, out _);
            return false;
        }

        if (_scanNeedExcluded.TryAdd(id, true))
        {
            string name;
            try { name = device.Name; }
            catch { name = string.Empty; }

            _logger?.LogInformation(
                "[BT] {Name} excluded from scan need — paired but last connected {Days} days ago (stale pairing). " +
                "It stays matched, so a scan window opened for any other device still reconnects it.",
                name, (int)(DateTime.UtcNow - last).TotalDays);
        }

        return true;
    }

    // The staleness rule, extracted so it is pinned by tests rather than by reading the call
    // site. All the fail-open directions matter: a null tree opinion is NOT "disconnected" (the
    // same tri-state trap ResolveConnected documents — collapsing it would veto every device on
    // startup, before the first poll), a null timestamp is a device we cannot judge, and the
    // comparison is strictly `>` so a value AT the threshold — or hours in the future, which the
    // local-time FILETIME storage makes possible (see DeviceNodeQuery) — still reads as fresh.
    internal static bool IsStalePairing(
        DateTime? lastConnectedUtc, bool? treeSaysConnected, DateTime nowUtc, TimeSpan threshold) =>
        treeSaysConnected == false && lastConnectedUtc is { } last && nowUtc - last > threshold;

    // Folds the per-device verdicts into the sweep's single "does the LE scan need to run" answer.
    // Extracted like IsStalePairing so the aggregation rule is pinned by tests, not by the call site.
    //
    // A disconnected paired device that is NOT stale needs the scan — that part is unchanged. The
    // ghost veto, however, only holds while at least one OTHER paired, non-stale device exists
    // (connected — the re-paired sibling the ghost case is really about — or disconnected, which
    // sets the need itself and reconnects the ghost with its window, per the exclusion log line's
    // own promise). When the vetoed ghost is the ONLY paired entry there is no such sibling, and
    // suppressing the need would silently kill reconnection for what may be a real device back
    // from a 15-day holiday, with nothing but a single long-past log line to explain it. The veto
    // yields — fail open, like every other unreadable input in this rule family (PR #130 review,
    // m4). The perpetual-scan cost this re-admits is confined to a machine whose sole tracked
    // pairing is 14+ days cold.
    internal static bool ResolveScanNeed(IEnumerable<(bool Paired, bool Connected, bool Stale)> devices)
    {
        var need = false;
        var anyPairedFresh = false;
        var anyVetoedGhost = false;
        foreach (var (paired, connected, stale) in devices)
        {
            if (!paired) continue;
            if (stale && !connected) { anyVetoedGhost = true; continue; }
            anyPairedFresh = true;
            if (!connected) need = true;
        }
        return need || (anyVetoedGhost && !anyPairedFresh);
    }

    // Mirror of MarkDisconnected. Only raises on a TRANSITION, so a device that stays connected
    // costs one set lookup per poll and notifies nothing.
    private void MarkConnected(BluetoothDevice device)
    {
        string id;
        try { id = device.Id; }
        catch { return; }

        if (_trackedStatuses.TryGetValue(id, out var wasConnected) && wasConnected)
            return;

        _trackedStatuses[id] = true;

        // Connected is the transition that ends a staleness verdict — clear it here rather than
        // waiting for the sweep, so a later RE-staleness logs its exclusion line again.
        _scanNeedExcluded.TryRemove(id, out _);

        string name;
        try { name = device.Name; }
        catch { name = string.Empty; }

        bool paired;
        try { paired = device.IsPaired; }
        catch { paired = true; }   // the tree says it is live, so pairing is not in doubt

        _logger?.LogInformation("[BT LE] {Name} marked connected from the device tree.", name);
        RaiseDeviceConnectionChanged(id, name, true, paired, -1);

        // Something changed, so the escalation starts over — see ScanReopenSchedule.
        ResetIdleBackoff();

        // Best-effort: the instance may be stale even though the link is up, and a failed battery
        // read must not undo a connection we have positive evidence for.
        try { _ = UpdateBatteryAsync(device); }
        catch (Exception ex) { _logger?.LogDebug(ex, "[BT LE] Battery refresh skipped after reconnect."); }
    }

    // The addresses currently backed by a PRESENT, CONNECTED low-energy node. A node that is absent
    // and a node reporting IsConnected=false are the same statement — the device is not reachable —
    // and both were measured on hardware (2026-08-20), so neither is treated as special.
    // Every address with a node AT ALL, connected or not. Distinct from LiveLowEnergyAddresses and
    // the distinction is the whole fix: measured on hardware, switching a device OFF leaves its node
    // in place with IsConnected=false, while REMOVING it in Windows deletes the node outright. Those
    // are different events and must not be collapsed —
    //   switched off -> still ours, keep tracking so it comes back when switched on;
    //   removed      -> not ours any more, let go of it entirely.
    internal static HashSet<string> PresentLowEnergyAddresses(IEnumerable<DeviceNode> nodes)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (ClassicBluetoothDeviceResolver.TryParseAddress(node.InstanceId) is { } address)
                present.Add(address);
        }

        return present;
    }

    internal static HashSet<string> LiveLowEnergyAddresses(IEnumerable<DeviceNode> nodes)
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            if (!node.IsConnected) continue;
            if (ClassicBluetoothDeviceResolver.TryParseAddress(node.InstanceId) is { } address)
                live.Add(address);
        }

        return live;
    }

    // The 48-bit address for a tracked device, or null when it cannot be determined.
    private static string? TrackedBluetoothAddress(BluetoothDevice device)
    {
        try { return NormalizeBluetoothAddress(device.Id); }
        catch { return null; }
    }

    // Pulled out of TrackedBluetoothAddress so the rule can be tested without an InTheHand device.
    //
    // TryParseAddress is reused rather than re-implemented because it already refuses the trap that
    // matters here: a service node's id embeds the Bluetooth base UUID, whose tail is also twelve
    // hex digits, so "the last twelve hex characters" silently returns 00805F9B34FB for everything —
    // which as a tracked address would match nothing and disconnect a working device every poll.
    internal static string? NormalizeBluetoothAddress(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (ClassicBluetoothDeviceResolver.TryParseAddress(id) is { } parsed) return parsed;

        // InTheHand commonly hands back a bare address, with or without separators. Accepted only
        // when the WHOLE id is one — anything longer is a path we have not taught this to read, and
        // guessing at it is how the base-UUID bug happens.
        var hex = new string([.. id.Where(Uri.IsHexDigit)]);
        return hex.Length == 12 ? hex.ToUpperInvariant() : null;
    }

    private async Task ClassicBluetoothPollAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ClassicPollInterval, ct);
                PollClassicBluetoothDevices();
                PollLowEnergyPresence();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[BT classic] poll loop iteration failed");
            }
        }
    }

    private void PollClassicBluetoothDevices()
    {
        try
        {
            var maps = _deviceInfoProvider.DevicesMaps;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // null means the tree could not be read, which says nothing about any device. This loop
            // disconnects everything it does not see, so treating a failed call as "nothing is
            // connected" would drop every classic headset on one bad CfgMgr32 call.
            var classicNodes = DeviceNodeQuery.EnumerateClassicBluetoothNodes();
            if (classicNodes is null) return;

            foreach (var device in ClassicBluetoothDeviceResolver.FromNodes(classicNodes))
            {
                // Only devices the catalog describes become cards. Every other paired headset,
                // speaker and serial link on the machine is enumerated too and must stay ignored.
                var map = ClassicBluetoothDeviceResolver.MatchCatalog(maps, device);
                if (map is null) continue;

                seen.Add(device.ContainerId);

                var state = new BluetoothDeviceState
                {
                    DeviceId = device.ContainerId,
                    Name = device.Name ?? map.Name,
                    // The node's own answer, never "it is enumerated". A paired device keeps its
                    // nodes while it is away, so presence alone showed the card permanently active.
                    IsConnected = device.IsConnected,
                    IsPaired = true,
                    ClientName = map.Name,
                    BatteryLevel = device.BatteryPercent ?? -1,
                };

                var previous = _classicDevices.TryGetValue(device.ContainerId, out var existing) ? existing : null;
                _classicDevices[device.ContainerId] = state;

                // Raise on a transition either way, or on a battery move, so a steady headset does
                // not push an event every sweep.
                if (previous is null || previous.IsConnected != state.IsConnected ||
                    previous.BatteryLevel != state.BatteryLevel)
                {
                    _logger.LogInformation("[BT classic] {Name} {State}, battery {Battery}",
                        state.Name,
                        state.IsConnected ? "connected" : "disconnected",
                        state.BatteryLevel < 0 ? "unknown" : state.BatteryLevel + "%");
                    RaiseDeviceConnectionChanged(state.DeviceId, state.Name, state.IsConnected, true,
                        state.BatteryLevel, state.ClientName);
                }
            }

            // A device that stops being enumerated altogether — unpaired, or the radio switched off.
            // Distinct from the disconnected-but-paired case handled above.
            foreach (var (containerId, state) in _classicDevices)
            {
                if (seen.Contains(containerId) || !state.IsConnected) continue;

                // REPLACED, not mutated. GetBluetoothDevicesAsync hands these instances straight to
                // its caller, so editing one in place lets a consumer watch a device flip mid-use —
                // DeviceService mapping the list into a gRPC response while this loop runs would
                // emit a device reported connected carrying battery -1, purely on interleaving.
                // Once published, an entry is never written to again.
                var gone = new BluetoothDeviceState
                {
                    DeviceId = state.DeviceId,
                    Name = state.Name,
                    IsConnected = false,
                    IsPaired = state.IsPaired,
                    ClientName = state.ClientName,
                    BatteryLevel = -1,
                };
                _classicDevices[containerId] = gone;

                _logger.LogInformation("[BT classic] {Name} no longer enumerated", gone.Name);
                RaiseDeviceConnectionChanged(containerId, gone.Name, false, true, -1, gone.ClientName);
            }
        }
        catch (Exception ex)
        {
            // Never let the device-tree query take the BLE sweep down with it.
            _logger.LogWarning(ex, "[BT classic] device-tree poll failed");
        }
    }

    #endregion
}
