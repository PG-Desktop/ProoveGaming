using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProoveGaming.App.Models;
using ProoveGaming.GrpcDependencies.Device;

namespace ProoveGaming.App.Services;

// The one-shot upgrade push (docs/plans/add-api-v2-sync): when a guest registers a new
// account, their freshly migrated local data is pushed to it — macros first (presets
// reference them by uid), then presets, preferences and device state. Best-effort by
// contract: the push runs on the thread pool, never blocks the sign-up UI, and never
// surfaces a failure anywhere but the log — an aborted push converges later because
// creates are idempotent by uid (*_UID_ALREADY_EXISTS counts as success).
//
// Its mirror, the sign-in pull (docs/plans/add-api-v2-pull): when a user signs into an
// existing account (or a remembered session restores), the account's cloud data is pulled
// into the local database in the same macros → presets → preferences → device-state order.
// The merge is strictly additive — a server record whose uid already exists locally is
// skipped, never overwritten — so the pull can ship before two-way sync without clobbering
// local edits that were never pushed. Same best-effort background contract as the push.
public sealed class SettingsSyncService : ISettingsSyncService, IDisposable
{
    // internal, not private: SyncCoordinator's create paths classify the same two codes
    // (docs/plans/add-api-v2-live-sync) — one spelling, shared like SettingsRepository's
    // preference-key constants.
    internal const string MacroUidAlreadyExists = "MACRO_UID_ALREADY_EXISTS";
    internal const string PresetUidAlreadyExists = "PRESET_UID_ALREADY_EXISTS";

    // Returned by the pull's insert delegates instead of a row id when the uid turned out to
    // carry a pending tombstone. Distinct from 0, which is what RunOnStorageAsync yields once
    // teardown has begun and means "stop pulling" rather than "skip this one".
    private const int TombstonedSkip = -1;

    // How often the signed-in bell re-lists notifications/list while the app runs — the only
    // way server-originated entries (announcements, another machine's pushes) arrive without
    // a re-login. One cheap list call per tick; idle when no user token is present.
    private static readonly TimeSpan DefaultNotificationPollInterval = TimeSpan.FromMinutes(15);

    // Hard ceiling on one tick's network time. The tick rides the PullTask chain that
    // DeviceDetails' preset gate and the coordinator await, and the shared HttpClient's
    // default timeout is ~100 s — without this cap a tick hung on a dead link would stall
    // those consumers for the full window (PR #127 review, m4).
    private static readonly TimeSpan DefaultNotificationPollTimeout = TimeSpan.FromSeconds(20);

    private readonly ISyncApiService _syncApi;
    private readonly LocalStorageService _storage;
    private readonly UserProfile _user;
    private readonly PresetConfigurationBuilder _configurationBuilder;
    private readonly PresetConfigurationApplier _configurationApplier;
    private readonly IDeviceCatalogResolver _catalogResolver;
    private readonly ILogger<SettingsSyncService> _logger;
    private readonly TimeSpan _notificationPollInterval;
    private readonly TimeSpan _notificationPollTimeout;

    // Cancelled by Dispose (container teardown at app exit) so the poll loop's parked delay
    // unblocks instead of holding a pool thread.
    private readonly CancellationTokenSource _pollCts = new();

    // Serialises the read-then-replace of the PullTask chain: with the notification poll the
    // chain gained a second writer (the loop's pool thread) alongside OnSignedInAsync, and an
    // unguarded swap could drop one link from the awaitable property.
    private readonly object _pullChainGate = new();

    public SettingsSyncService(
        ISyncApiService syncApi,
        LocalStorageService storage,
        UserProfile user,
        PresetConfigurationBuilder configurationBuilder,
        PresetConfigurationApplier configurationApplier,
        IDeviceCatalogResolver catalogResolver,
        ILogger<SettingsSyncService> logger)
        : this(syncApi, storage, user, configurationBuilder, configurationApplier, catalogResolver, logger,
            DefaultNotificationPollInterval, DefaultNotificationPollTimeout)
    {
    }

    // Test seam (InternalsVisibleTo ProoveGaming.App.Tests): a short poll interval/timeout
    // keeps the poll tests timing-insensitive, the SyncCoordinator debounce approach.
    internal SettingsSyncService(
        ISyncApiService syncApi,
        LocalStorageService storage,
        UserProfile user,
        PresetConfigurationBuilder configurationBuilder,
        PresetConfigurationApplier configurationApplier,
        IDeviceCatalogResolver catalogResolver,
        ILogger<SettingsSyncService> logger,
        TimeSpan notificationPollInterval,
        TimeSpan? notificationPollTimeout = null)
    {
        _syncApi = syncApi;
        _storage = storage;
        _user = user;
        _configurationBuilder = configurationBuilder;
        _configurationApplier = configurationApplier;
        _catalogResolver = catalogResolver;
        _logger = logger;
        _notificationPollInterval = notificationPollInterval;
        _notificationPollTimeout = notificationPollTimeout ?? DefaultNotificationPollTimeout;

        _ = Task.Run(() => PollNotificationsLoopAsync(_pollCts.Token), CancellationToken.None);
    }

    public event Action? PullCompletedWithChanges;

    public event Action? SyncCompleted;

    // Raised (on a pool thread) after a notifications/list merge — sign-in pull or periodic
    // poll — changed local rows; HomeViewModel reloads the bell off it.
    public event Action? NotificationsPulled;

    // Each tick runs one notifications/list merge, chained on the PullTask so a poll can
    // never interleave with a sign-in pull's own merge for the same owner. Idles (no API
    // calls) while no user is signed in; exits only when Dispose cancels it.
    private async Task PollNotificationsLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await Task.Delay(_notificationPollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var customerId = _user.CustomerId;
            var token = _user.SessionToken;
            if (customerId <= 0 || string.IsNullOrEmpty(token))
                continue;

            var ownerId = customerId.ToString(CultureInfo.InvariantCulture);
            Task poll;
            lock (_pullChainGate)
            {
                // Task.Run, not a direct call: the tick's synchronous head must not run
                // under the gate — the same shape OnSignedInAsync's link uses.
                var previous = PullTask;
                poll = Task.Run(() => RunPollTickAsync(previous, ownerId, token, ct), CancellationToken.None);
                PullTask = poll;
            }
            await poll;
        }
    }

    // Never faults (PullTask's contract); a teardown-cancelled or timed-out tick just ends.
    // The linked timeout caps the tick's network time so the PullTask chain stays promptly
    // awaitable on a dead link (PR #127 review, m4).
    private async Task RunPollTickAsync(Task previous, string ownerId, string token, CancellationToken ct)
    {
        await previous;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_notificationPollTimeout);
            await PullNotificationsAsync(ownerId, token, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!ct.IsCancellationRequested)
                _logger.LogWarning("Notification poll tick for owner {OwnerId} timed out; retrying next tick.", ownerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Notification poll for owner {OwnerId} failed.", ownerId);
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _pollCts.Cancel();
        _pollCts.Dispose();
    }

    // Test seam (InternalsVisibleTo ProoveGaming.App.Tests): the background push started by
    // the last OnGuestUpgradedAsync call, awaitable so tests observe its completion — same
    // pattern as HomeViewModel.NotificationsLoaded. Never faults (PushAsync owns every
    // exception); an app exit mid-push simply abandons it, which is the accepted best-effort
    // trade-off.
    internal Task PushTask { get; private set; } = Task.CompletedTask;

    // The pull's twin of PushTask — the background pull started by the last OnSignedInAsync
    // call, awaitable so tests observe its completion. Same never-faults contract. Public
    // via ISettingsSyncService: DeviceDetailsViewModel awaits it before seeding a default
    // preset for a signed-in user (docs/plans/fix-duplicate-default-preset). Each new pull
    // chains after the previous one inside its own Task.Run, so awaiting the property always
    // awaits the whole current chain.
    public Task PullTask { get; private set; } = Task.CompletedTask;

    // Reads nothing but the in-memory token before returning: the storage reads and HTTP
    // calls all happen on the pool inside PushTask, so the caller (the sign-up flow's UI
    // transition) is never held up. FIFO executor ordering guarantees the push sees
    // post-migration rows — its storage items are enqueued after MigrateGuestToUserAsync's.
    public Task OnGuestUpgradedAsync(int customerId, CancellationToken ct = default)
    {
        var token = _user.SessionToken;
        if (customerId <= 0 || string.IsNullOrEmpty(token))
        {
            _logger.LogWarning(
                "Upgrade push skipped: customer {CustomerId} has no session token.", customerId);
            return Task.CompletedTask;
        }

        // A second guest→register cycle in one session replaces the seam's task; the previous
        // push keeps running to completion on the pool, it just stops being observable here.
        if (!PushTask.IsCompleted)
        {
            _logger.LogInformation(
                "Upgrade push for customer {CustomerId} scheduled while a previous push is still running.",
                customerId);
        }

        PushTask = Task.Run(() => PushAsync(customerId, token, ct), CancellationToken.None);
        return Task.CompletedTask;
    }

    // Same synchronous-return contract as OnGuestUpgradedAsync: only the in-memory token is
    // read before returning; every HTTP call and storage access runs on the pool inside
    // PullTask, so neither the sign-in transition nor app startup is ever held up.
    public Task OnSignedInAsync(int customerId, CancellationToken ct = default)
    {
        var token = _user.SessionToken;
        if (customerId <= 0 || string.IsNullOrEmpty(token))
        {
            _logger.LogWarning(
                "Sign-in pull skipped: customer {CustomerId} has no session token.", customerId);
            return Task.CompletedTask;
        }

        // A sign-out/sign-in cycle while a pull is in flight chains the new pull after the
        // old one (whose stale token makes every remaining call abort) instead of racing it:
        // two concurrent pulls for the same owner each read their uid map up front, and the
        // loser of a same-uid insert would die on the unique index and log at Error despite
        // being benign. PullAsync never faults, so the chain cannot be poisoned. The swap
        // runs under _pullChainGate — the notification poll is the chain's second writer.
        Task previousPull;
        lock (_pullChainGate)
        {
            previousPull = PullTask;
            PullTask = Task.Run(async () =>
            {
                await previousPull;
                await PullAsync(customerId, token, ct);
            }, CancellationToken.None);
        }
        if (!previousPull.IsCompleted)
        {
            _logger.LogInformation(
                "Sign-in pull for customer {CustomerId} scheduled while a previous pull is still running.",
                customerId);
        }
        return Task.CompletedTask;
    }

    private async Task PushAsync(int customerId, string token, CancellationToken ct)
    {
        try
        {
            await PushCoreAsync(customerId, token, ct);
        }
        catch (Exception ex)
        {
            // Best-effort by contract: nothing may propagate to awaiters of PushTask.
            _logger.LogError(ex, "Upgrade push for customer {CustomerId} failed.", customerId);
        }

        RaiseSyncCompleted();
    }

    // The reconcile trigger for SyncCoordinator: rows this push or pull failed to sync
    // (server_updated_at still NULL) are picked up by the coordinator's next pass
    // (docs/plans/add-api-v2-live-sync). Raised whether or not the pipeline succeeded —
    // an aborted run is exactly when a reconcile matters.
    private void RaiseSyncCompleted()
    {
        try
        {
            SyncCompleted?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SyncCompleted subscriber failed.");
        }
    }

    private async Task PushCoreAsync(int customerId, string token, CancellationToken ct)
    {
        var ownerId = customerId.ToString(CultureInfo.InvariantCulture);

        // Storage reads return default(T) once teardown has begun (LocalStorageService
        // .IsShutDown) — every null check below treats that as "stop pushing", the same
        // best-effort abandonment as an app exit mid-push.
        var devices = await _storage.RunOnStorageAsync(() =>
            _storage.GetDevices(LocalStorageService.OwnerKindUser, ownerId));
        if (devices is null || devices.Count == 0)
            return;

        // The SETTINGS bucket of each stored card, not its routing pair: every table this push
        // then reads (presets, macros, device_applied_preset) is keyed by the settings identity,
        // and for a merged-transport product the routing pair follows whichever transport was last
        // persisted — a dongle-last RAMPAGE's row says (1389, 49271) while its rows live under
        // (10473, 12888), so the routing pair names no bucket at all and the push found nothing.
        var deviceTypes = new List<(int VendorId, int ProductId)>();
        var seenDeviceTypes = new HashSet<(int VendorId, int ProductId)>();
        foreach (var device in devices)
        {
            var type = SettingsBucketOf(device);
            if (seenDeviceTypes.Add(type))
                deviceTypes.Add(type);
        }

        // 1. Macros first — preset configurations reference them by uid.
        var macroUidsById = new Dictionary<int, string>();
        foreach (var (vendorId, productId) in deviceTypes)
        {
            var deviceKeys = await _storage.RunOnStorageAsync(() =>
                _storage.GetMacroDeviceKeys(LocalStorageService.OwnerKindUser, ownerId, vendorId, productId));
            if (deviceKeys is null)
                return;

            foreach (var deviceKey in deviceKeys)
            {
                var macros = await _storage.RunOnStorageAsync(() =>
                    _storage.GetMacros(LocalStorageService.OwnerKindUser, ownerId, vendorId, productId, deviceKey));
                if (macros is null)
                    return;

                foreach (var macro in macros)
                {
                    // Registered regardless of the create's outcome: the uid IS the macro's
                    // identity, and a preset that references a macro the server rejected is
                    // no worse than one referencing a macro a later retry will create.
                    // TryAdd doubles as the visited set: GetMacros unions the unit-agnostic
                    // '' scope into every unit key, so the same macro can surface once per
                    // key — each id is pushed exactly once.
                    if (!macroUidsById.TryAdd(macro.Id, macro.Uid))
                        continue;

                    var response = await _syncApi.CreateMacroAsync(token, BuildMacroRequest(macro, vendorId, productId), ct);
                    if (Aborts(response, "Upgrade push", "macros/create"))
                        return;
                    if (Succeeded(response, MacroUidAlreadyExists))
                    {
                        if (response.Macro?.UpdatedAt is { Length: > 0 } updatedAt)
                            await _storage.RunOnStorageAsync(() => _storage.SetMacroServerUpdatedAt(macro.Id, updatedAt));
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Upgrade push: macros/create rejected macro {Uid}: {Message}",
                            macro.Uid, response.Message ?? response.ErrorCode);
                    }
                }
            }
        }

        // 2. Presets, with their full serialized configuration.
        var presetsById = new Dictionary<int, (string Uid, string Name)>();
        foreach (var (vendorId, productId) in deviceTypes)
        {
            var presets = await _storage.RunOnStorageAsync(() =>
                _storage.GetPresets(LocalStorageService.OwnerKindUser, ownerId, vendorId, productId));
            if (presets is null)
                return;

            foreach (var preset in presets)
            {
                presetsById[preset.Id] = (preset.Uid, preset.Name);

                var configuration = await _storage.RunOnStorageAsync(() =>
                    _configurationBuilder.Build(preset.Id, macroUidsById));
                if (configuration is null)
                    return;

                var response = await _syncApi.CreatePresetAsync(token, new PresetCreateRequest
                {
                    Uid = preset.Uid,
                    VendorId = vendorId,
                    ProductId = productId,
                    Name = preset.Name,
                    Description = preset.Description,
                    IsDefault = preset.IsDefault,
                    Configuration = configuration
                }, ct);
                if (Aborts(response, "Upgrade push", "presets/create"))
                    return;
                if (Succeeded(response, PresetUidAlreadyExists))
                {
                    if (response.Preset?.UpdatedAt is { Length: > 0 } updatedAt)
                        await _storage.RunOnStorageAsync(() => _storage.SetPresetServerUpdatedAt(preset.Id, updatedAt));
                }
                else
                {
                    _logger.LogWarning(
                        "Upgrade push: presets/create rejected preset {Uid}: {Message}",
                        preset.Uid, response.Message ?? response.ErrorCode);
                }
            }
        }

        // 3. Preferences — one update carrying the five documented keys, only when the owner
        //    has saved any of them (a fresh account's first write, so no updated_at).
        var preferences = await _storage.RunOnStorageAsync(() => ReadPreferences(_storage, ownerId));
        if (preferences is not null)
        {
            var response = await _syncApi.UpdatePreferencesAsync(token, preferences, ct: ct);
            if (Aborts(response, "Upgrade push", "preferences/update"))
                return;
            if (!Succeeded(response, alreadyExistsCode: null))
            {
                _logger.LogWarning(
                    "Upgrade push: preferences/update rejected: {Message}",
                    response.Message ?? response.ErrorCode);
            }
        }

        // 4. Device state, collapsed to one row per device type: API v2.0's devices/update
        //    keys on VID/PID only, so per-unit applied presets cannot be represented
        //    (plan Open question 4 — accepted lossy collapse).
        foreach (var (vendorId, productId) in deviceTypes)
        {
            var applied = await _storage.RunOnStorageAsync(() =>
                _storage.GetAppliedPresets(LocalStorageService.OwnerKindUser, ownerId, vendorId, productId));
            if (applied is null)
                return;
            if (applied.Count == 0)
                continue;

            var chosen = ChooseAppliedPresetRow(applied);
            if (!presetsById.TryGetValue(chosen.PresetId, out var chosenPreset))
            {
                _logger.LogWarning(
                    "Upgrade push: applied preset {PresetId} for device {VendorId:X4}:{ProductId:X4} has no local row; skipping devices/update.",
                    chosen.PresetId, vendorId, productId);
                continue;
            }

            if (AppliedPresetRowsDisagree(applied, chosen.PresetId))
            {
                _logger.LogWarning(
                    "Upgrade push: units of device {VendorId:X4}:{ProductId:X4} disagree on the applied preset; pushing preset {PresetUid} ('{PresetName}') and dropping the rest (API v2.0 has no per-unit device state).",
                    vendorId, productId, chosenPreset.Uid, chosenPreset.Name);
            }

            var response = await _syncApi.UpdateDeviceStateAsync(token, vendorId, productId, chosenPreset.Uid, ct: ct);
            if (Aborts(response, "Upgrade push", "devices/update"))
                return;
            if (!Succeeded(response, alreadyExistsCode: null))
            {
                _logger.LogWarning(
                    "Upgrade push: devices/update rejected for {VendorId:X4}:{ProductId:X4}: {Message}",
                    vendorId, productId, response.Message ?? response.ErrorCode);
            }
        }
    }

    private async Task PullAsync(int customerId, string token, CancellationToken ct)
    {
        var wroteAnything = false;
        try
        {
            wroteAnything = await PullCoreAsync(customerId, token, ct) > 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Our own cancellation (teardown): API calls now propagate it rather than reporting a
            // network error (docs/plans/fix-repository-audit-findings, A9). Not a failure.
            _logger.LogDebug("Sign-in pull for customer {CustomerId} cancelled.", customerId);
        }
        catch (Exception ex)
        {
            // Best-effort by contract: nothing may propagate to awaiters of PullTask.
            _logger.LogError(ex, "Sign-in pull for customer {CustomerId} failed.", customerId);
        }

        // Outside the pipeline's try so a faulting subscriber can't read as a pull failure;
        // raised only when at least one local record was written — the UI has nothing to
        // reload otherwise.
        if (wroteAnything)
        {
            try
            {
                PullCompletedWithChanges?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sign-in pull: PullCompletedWithChanges subscriber failed.");
            }
        }

        RaiseSyncCompleted();
    }

    // Returns the number of local records written. Order matters: macros before presets (the
    // applier resolves macro_uid references against the macro map), presets before device
    // state (active_preset_uid must resolve to a local preset).
    private async Task<int> PullCoreAsync(int customerId, string token, CancellationToken ct)
    {
        var ownerId = customerId.ToString(CultureInfo.InvariantCulture);
        var written = 0;

        // Pending tombstones, snapshotted BEFORE the first network call — this is the ordering
        // that makes the merge safe. A tombstone is a local delete the server has not confirmed
        // yet, so it still lists the uid and re-inserting it would resurrect the record. The
        // snapshot is taken first because it must not be able to MISS a tombstone: every list
        // response this pull acts on was built after this moment, so a tombstone dropped later
        // (the coordinator's pass confirming the delete) belongs to a uid this snapshot already
        // holds. Reading it after the list call instead would let that same drop slip through.
        // Deletes made DURING the pull are the other direction and are caught by the point read
        // at each insert below.
        var tombstoned = await _storage.RunOnStorageAsync(() =>
            (Macros: ReadTombstonedUids(_storage, ownerId, LocalStorageService.TombstoneKindMacro),
                Presets: ReadTombstonedUids(_storage, ownerId, LocalStorageService.TombstoneKindPreset)));
        if (tombstoned.Macros is null || tombstoned.Presets is null)
            return written;

        // The server keys everything by the pair the App pushed, which for a merged-transport
        // product may be an ALTERNATE vendor a pre-fix release labelled it with (RAMPAGE on its
        // dongle: 1389). Inserting under that raw pair would land the rows in a bucket no page
        // reads — the details page reads the canonical settings bucket — so every local write
        // this pull makes routes its pair through this alias first, built from the local devices
        // rows (there is no catalog RPC to ask instead). A pair the alias does not know is used
        // unchanged, which is what an unrecognised device does today. Built before the first
        // insert so macros, presets and device state all use the same mapping.
        var canonicalBucketByServerPair = await BuildSettingsBucketAliasAsync(ownerId);
        if (canonicalBucketByServerPair is null)
            return written;

        // 1. Macros. One bulk uid read, then dictionary checks per pulled item; the map grows
        //    with each insert so the preset step resolves both old and new macros.
        var macroList = await _syncApi.ListMacrosAsync(token, ct);
        if (Aborts(macroList, "Sign-in pull", "macros/list"))
            return written;

        // Storage reads return default(T) once teardown has begun (LocalStorageService
        // .IsShutDown) — every null/zero check below treats that as "stop pulling", the same
        // best-effort abandonment as the push.
        var macroIdsByUid = await _storage.RunOnStorageAsync(() =>
            _storage.GetMacroIdsByUid(LocalStorageService.OwnerKindUser, ownerId));
        if (macroIdsByUid is null)
            return written;

        if (macroList.Macros is { } pulledMacros)
        {
            foreach (var dto in pulledMacros)
            {
                if (dto?.Uid is not { Length: > 0 } uid
                    || dto.Name is not { Length: > 0 } name
                    || dto.VendorId is not int vendorId
                    || dto.ProductId is not int productId)
                {
                    _logger.LogWarning(
                        "Sign-in pull: skipping macros/list item that did not bind (uid '{Uid}').", dto?.Uid);
                    continue;
                }

                // Canonicalized so the macro lands in the bucket the device pages read — see the
                // alias note above step 1.
                if (canonicalBucketByServerPair.TryGetValue((vendorId, productId), out var canonicalMacroBucket))
                    (vendorId, productId) = canonicalMacroBucket;

                // Additive merge: a uid already present locally is never touched — convergence
                // of diverged records is phase 2 (docs/plans/add-api-v2-live-sync) — and a
                // tombstoned uid is a pending local delete, not a missing record. Skipping it
                // here also spares the macros/get fallback below.
                if (macroIdsByUid.ContainsKey(uid) || tombstoned.Macros.Contains(uid))
                    continue;

                var events = dto.Events;
                var updatedAt = dto.UpdatedAt;
                if (events is null)
                {
                    // The list did not embed the events (plan Open question 1) — fall back to
                    // a per-uid get.
                    var get = await _syncApi.GetMacroAsync(token, uid, ct);
                    if (Aborts(get, "Sign-in pull", "macros/get"))
                        return written;
                    events = get.Macro?.Events;
                    updatedAt ??= get.Macro?.UpdatedAt;
                    if (events is null)
                    {
                        _logger.LogWarning(
                            "Sign-in pull: macros/get returned no events for macro {Uid}; skipping.", uid);
                        continue;
                    }
                }

                // An out-of-enum repeat mode falls back to play-count rather than storing a
                // value no device composer has a case for.
                var mode = (MacroRepeatMode)(dto.RepeatMode ?? 0);
                if (!Enum.IsDefined(mode))
                    mode = MacroRepeatMode.MacroPlayCount;

                var record = new MacroRecord
                {
                    Uid = uid,
                    Name = name,
                    RepeatCount = dto.RepeatCount ?? 1,
                    Mode = mode,
                    ServerUpdatedAt = updatedAt,
                };
                foreach (var e in events)
                {
                    // An out-of-range key code is skipped rather than wrapped by the byte
                    // cast — the same range check the applier's GetByte applies.
                    if (e is null || e.KeyCode is < byte.MinValue or > byte.MaxValue)
                        continue;
                    record.Events.Add(new MacroEventRecord
                    {
                        KeyCode = (byte)e.KeyCode,
                        IsPress = e.IsPress,
                        DelayMs = e.DelayMs,
                    });
                }

                // device_key = '': the server has no per-unit scope (API v2.0 keys macros on
                // VID/PID alone), so pulled macros land in the unit-agnostic scope — the same
                // collapse the push accepts in the other direction. GetMacros unions this
                // scope into every unit-keyed read, so the macros are visible to each unit.
                // markDirty: false — pulled rows ARE server content; marking them would
                // push them straight back (docs/plans/add-api-v2-live-sync).
                //
                // Second half of the tombstone guard: the snapshot above cannot know about a
                // delete the user makes WHILE this pull runs, so the insert re-checks inside
                // its own storage item. Both halves are needed — the snapshot covers
                // tombstones dropped mid-pull, this covers tombstones created mid-pull.
                var macroId = await _storage.RunOnStorageAsync(() =>
                    _storage.ExistsSyncTombstone(LocalStorageService.OwnerKindUser, ownerId, LocalStorageService.TombstoneKindMacro, uid)
                        ? TombstonedSkip
                        : _storage.SaveMacro(LocalStorageService.OwnerKindUser, ownerId, vendorId, productId, string.Empty, record, markDirty: false));
                if (macroId == 0)
                    return written;
                if (macroId == TombstonedSkip)
                    continue;
                macroIdsByUid[uid] = macroId;
                written++;
            }
        }

        // 2. Presets, hydrated through PresetConfigurationApplier inside the same storage
        //    item as their insert.
        var presetList = await _syncApi.ListPresetsAsync(token, ct);
        if (Aborts(presetList, "Sign-in pull", "presets/list"))
            return written;

        var presetIdsByUid = await _storage.RunOnStorageAsync(() =>
            _storage.GetPresetIdsByUid(LocalStorageService.OwnerKindUser, ownerId));
        if (presetIdsByUid is null)
            return written;

        var deviceTypes = new List<(int VendorId, int ProductId)>();
        var seenDeviceTypes = new HashSet<(int VendorId, int ProductId)>();
        var hasDefaultByType = new Dictionary<(int VendorId, int ProductId), bool>();

        if (presetList.Presets is { } pulledPresets)
        {
            foreach (var dto in pulledPresets)
            {
                if (dto?.Uid is not { Length: > 0 } uid
                    || dto.Name is not { Length: > 0 } name
                    || dto.VendorId is not int vendorId
                    || dto.ProductId is not int productId)
                {
                    _logger.LogWarning(
                        "Sign-in pull: skipping presets/list item that did not bind (uid '{Uid}').", dto?.Uid);
                    continue;
                }

                // Every pulled device type gets a devices/get in step 4, whether or not this
                // particular preset was inserted. Recorded RAW: step 4 must query the server
                // under the pair the server keys the state by, and canonicalizes on its own.
                if (seenDeviceTypes.Add((vendorId, productId)))
                    deviceTypes.Add((vendorId, productId));

                // Canonicalized for every LOCAL read and write below — the insert, the default
                // arbitration's GetPresets and its hasDefaultByType key — so the preset is
                // visible to the page and SetAppliedPreset in step 4 references a preset that
                // exists in the canonical scope. See the alias note above step 1.
                if (canonicalBucketByServerPair.TryGetValue((vendorId, productId), out var canonicalPresetBucket))
                    (vendorId, productId) = canonicalPresetBucket;

                // Same additive merge and pending-delete guard as the macro loop.
                if (presetIdsByUid.ContainsKey(uid) || tombstoned.Presets.Contains(uid))
                    continue;

                var configuration = dto.Configuration;
                var updatedAt = dto.UpdatedAt;
                var description = dto.Description;
                var isDefault = dto.IsDefault;
                if (configuration is not { ValueKind: JsonValueKind.Object })
                {
                    // The list did not embed the configuration — fall back to a per-uid get.
                    var get = await _syncApi.GetPresetAsync(token, uid, ct);
                    if (Aborts(get, "Sign-in pull", "presets/get"))
                        return written;
                    configuration = get.Preset?.Configuration;
                    updatedAt ??= get.Preset?.UpdatedAt;
                    description ??= get.Preset?.Description;
                    isDefault ??= get.Preset?.IsDefault;
                    if (configuration is not { ValueKind: JsonValueKind.Object })
                    {
                        _logger.LogWarning(
                            "Sign-in pull: presets/get returned no configuration for preset {Uid}; skipping.", uid);
                        continue;
                    }
                }

                // Inserted with IsDefault=false regardless of the server flag — the local
                // invariant (exactly one default per (owner, vid, pid)) is arbitrated below.
                var record = new PresetRecord
                {
                    Uid = uid,
                    Name = name,
                    Description = description ?? string.Empty,
                    IsDefault = false,
                    ServerUpdatedAt = updatedAt,
                };

                var appliedConfiguration = configuration.Value;
                var presetId = await _storage.RunOnStorageAsync(() =>
                {
                    // Re-checked next to the insert for a delete made mid-pull — see the
                    // macro insert above.
                    if (_storage.ExistsSyncTombstone(LocalStorageService.OwnerKindUser, ownerId, LocalStorageService.TombstoneKindPreset, uid))
                        return TombstonedSkip;
                    // markDirty: false — pulled rows ARE server content (see SaveMacro above).
                    var id = _storage.InsertPreset(LocalStorageService.OwnerKindUser, ownerId, vendorId, productId, record, markDirty: false);
                    _configurationApplier.Apply(id, appliedConfiguration, macroIdsByUid);
                    return id;
                });
                if (presetId == 0)
                    return written;
                if (presetId == TombstonedSkip)
                    continue;
                presetIdsByUid[uid] = presetId;
                written++;

                if (isDefault == true)
                {
                    if (!hasDefaultByType.TryGetValue((vendorId, productId), out var hasDefault))
                    {
                        var locals = await _storage.RunOnStorageAsync(() =>
                            _storage.GetPresets(LocalStorageService.OwnerKindUser, ownerId, vendorId, productId));
                        if (locals is null)
                            return written;
                        PresetRecord? localDefault = null;
                        foreach (var local in locals)
                        {
                            if (local.IsDefault)
                            {
                                localDefault = local;
                                break;
                            }
                        }
                        hasDefault = localDefault is not null;

                        if (localDefault is { ServerUpdatedAt: null, IsDirty: true })
                        {
                            // A local-born, never-pushed local default here is
                            // CreateDefaultPresetIfNotExists racing this pull: it seeded over
                            // a scope that was only "empty" because the pull had not landed
                            // yet. The server's default supersedes it — the seeded snapshot is
                            // a copy of live device state that still exists in the device_*
                            // tables, and the server has nothing to tombstone. Both halves of
                            // the predicate matter: a NULL stamp alone also matches rows the
                            // server DOES hold (create acked without an echoed updated_at;
                            // pull-inserted rows when the server omits updated_at) — those
                            // are born clean, so IsDirty excludes them. The same guard re-runs
                            // inside the delete's own transaction: if the coordinator pushed
                            // the row in the meantime it is synced data, the delete refuses,
                            // and the local-wins rule below keeps it.
                            var superseded = await _storage.RunOnStorageAsync(() =>
                                _storage.DeleteNeverSyncedPreset(localDefault.Id));
                            if (superseded)
                            {
                                hasDefault = false;
                                _logger.LogInformation(
                                    "Sign-in pull: never-synced local default preset {LocalId} superseded by server default {Uid} for device {VendorId:X4}:{ProductId:X4}.",
                                    localDefault.Id, uid, vendorId, productId);
                            }
                        }
                        hasDefaultByType[(vendorId, productId)] = hasDefault;
                    }

                    if (hasDefault)
                    {
                        // The local default wins — the Launcher governs the default flag
                        // (contract §16.5) and this machine's choice is already in place.
                        _logger.LogDebug(
                            "Sign-in pull: preset {Uid} is the server default but device {VendorId:X4}:{ProductId:X4} already has a local default; keeping the local one.",
                            uid, vendorId, productId);
                    }
                    else
                    {
                        // markDirty: false — the flag being applied IS the server's state;
                        // re-pushing it would be an echo (docs/plans/add-api-v2-live-sync).
                        await _storage.RunOnStorageAsync(() => _storage.PromotePresetToDefault(presetId, markDirty: false));
                        hasDefaultByType[(vendorId, productId)] = true;
                    }
                }
            }
        }

        // 3. Preferences — written only when the owner never saved any of the five known
        //    keys, so a local change can never be overwritten by the cloud copy.
        var preferencesResponse = await _syncApi.GetPreferencesAsync(token, ct);
        if (Aborts(preferencesResponse, "Sign-in pull", "preferences/get"))
            return written;
        if (preferencesResponse.Preferences is { ValueKind: JsonValueKind.Object } serverPreferences)
        {
            written += await _storage.RunOnStorageAsync(() => ApplyServerPreferences(ownerId, serverPreferences));
        }
        // Anything else — the contract's documented "preferences": [] empty-array quirk, or
        // an absent field — means "no preferences on the server": not an error, nothing to do.

        // 4. Device state, one devices/get per device type seen among the pulled presets. The
        //    server call goes out under the RAW pair (that is what the server keys the state by);
        //    the local write is canonicalized like every other insert — see the alias note above
        //    step 1.
        foreach (var (serverVendorId, serverProductId) in deviceTypes)
        {
            var (vendorId, productId) =
                canonicalBucketByServerPair.TryGetValue((serverVendorId, serverProductId), out var canonical)
                    ? canonical
                    : (serverVendorId, serverProductId);

            var state = await _syncApi.GetDeviceStateAsync(token, serverVendorId, serverProductId, ct);
            if (Aborts(state, "Sign-in pull", "devices/get"))
                return written;
            if (state.ActivePresetUid is not { Length: > 0 } activeUid)
                continue;
            if (!presetIdsByUid.TryGetValue(activeUid, out var activePresetId))
            {
                _logger.LogWarning(
                    "Sign-in pull: devices/get for {VendorId:X4}:{ProductId:X4} names active preset {Uid} which does not exist locally; skipping.",
                    serverVendorId, serverProductId, activeUid);
                continue;
            }

            // device_key = '' (the unit-agnostic row) and only when NO local applied row
            // exists for the type — a unit this machine already applied a preset to keeps its
            // choice. The actual hardware application happens through the existing
            // reapply-preset-on-connect flow when the device connects: GetAppliedPreset falls
            // back to the '' row for a unit key that has no row of its own.
            var wrote = await _storage.RunOnStorageAsync(() =>
            {
                if (_storage.GetAppliedPresets(LocalStorageService.OwnerKindUser, ownerId, vendorId, productId).Count > 0)
                    return false;
                // markDirty: false — this applied preset IS the server's device state.
                _storage.SetAppliedPreset(LocalStorageService.OwnerKindUser, ownerId, vendorId, productId, string.Empty, activePresetId, markDirty: false);
                // The applied row names the preset but nothing has loaded its snapshot into a
                // unit's live rows or pushed it to the hardware — the pull holds no unit. The mark
                // hands that off to the first path that does, once
                // (docs/plans/add-extended-keys-api-sync). Written in the same delegate as the row
                // it belongs to, so the two cannot disagree; the value names the adopted preset so
                // a mark that outlives the user's next choice is recognised as stale.
                _storage.SetSyncMeta(LocalStorageService.OwnerKindUser, ownerId,
                    LocalStorageService.SyncMetaDeviceStatePendingApply(vendorId, productId),
                    LocalStorageService.SyncMetaPendingApplyValue(activePresetId));
                return true;
            });
            if (wrote)
                written++;
        }

        // 5. Device roster (docs/plans/add-api-v21-device-roster).
        written += await PullDeviceRosterAsync(ownerId, token, canonicalBucketByServerPair, ct);

        // 6. Notifications (docs/plans/add-api-v21-notifications-sync) — the same merge the
        //    periodic poll runs; changed rows also raise NotificationsPulled so the bell
        //    reloads without waiting for PullCompletedWithChanges' full owner reload.
        written += await PullNotificationsAsync(ownerId, token, ct);

        return written;
    }

    // notifications/list, merged by SERVER id: unknown ids become local rows (bell icon
    // resolved from the type slug — the server carries no icon fields), known ids adopt the
    // server's read flag unless the local row is dirty (a local toggle in flight wins until
    // its push settles), and a non-dirty local row whose id the full list no longer carries
    // is deleted — deletions follow the account (PR #127 review, M2). Snapshots — pending
    // notification tombstones and the two batch
    // marks — are read BEFORE the list call, the tombstone-snapshot ordering every merge in
    // this class uses (add-api-v21-device-roster review round 1, S1): a mark or tombstone
    // cleared by a concurrent pass mid-merge belongs to state the list response can still
    // carry. A pending delete_all skips the whole merge (everything listed is about to be
    // deleted); a pending read_all suppresses read-state adoption (the local batch is newer
    // than every server flag the list carries). Raises NotificationsPulled when rows
    // changed. Best-effort — a non-success list skips with a log line, never fails the
    // caller.
    private async Task<int> PullNotificationsAsync(string ownerId, string token, CancellationToken ct)
    {
        const string user = LocalStorageService.OwnerKindUser;

        var snapshot = await _storage.RunOnStorageAsync(() =>
            (Tombstoned: ReadTombstonedUids(_storage, ownerId, LocalStorageService.TombstoneKindNotification),
                DeleteAllPending: _storage.GetSyncMeta(user, ownerId, LocalStorageService.SyncMetaNotificationsDeleteAll) is not null,
                ReadAllPending: _storage.GetSyncMeta(user, ownerId, LocalStorageService.SyncMetaNotificationsReadAll) is not null));
        if (snapshot.Tombstoned is null)
            return 0;
        if (snapshot.DeleteAllPending)
            return 0;

        var locals = await _storage.RunOnStorageAsync(() => _storage.GetNotifications(user, ownerId));
        if (locals is null)
            return 0;
        var localByServerId = new Dictionary<long, NotificationRecord>();
        foreach (var local in locals)
        {
            if (local.ServerId is { } sid)
                localByServerId[sid] = local;
        }

        var list = await _syncApi.ListNotificationsAsync(token, ct);
        if (!Succeeded(list, alreadyExistsCode: null))
        {
            _logger.LogWarning(
                "Notifications pull: notifications/list unavailable ({Message}); skipping the merge.",
                list.Message ?? list.ErrorCode);
            return 0;
        }
        if (list.Notifications is not { } items)
            return 0;

        var changed = 0;
        var toInsert = new List<NotificationRecord>();
        // The FULL list's ids, for the deletion sweep below: absence from the whole list is
        // an unambiguous "deleted server-side" even when the server outgrew the cap and the
        // merge loop only walks the newest slice.
        var listedServerIds = new HashSet<long>();
        // Only the server's newest MaxNotificationsPerOwner items take part in the merge
        // (the list is newest-first, contract §21.2). The beyond-cap TAIL is the account's
        // oldest history: local rows can never cover it once the server outgrows the cap,
        // and treating it as insertable put those old entries at the TOP of the bell (local
        // order is by row id, and a late insert gets the highest id) while the trim evicted
        // the genuinely recent rows — which the next poll then re-inserted, reshuffling the
        // bell every tick (review round 1, B1). A local row whose server id lies beyond the
        // newest cap merely misses read-flag adoption; it is about to age out anyway.
        var considered = 0;
        foreach (var dto in items)
        {
            if (dto?.Id is not { } serverId || dto.Title is not { Length: > 0 } title)
            {
                _logger.LogWarning(
                    "Notifications pull: skipping notifications/list item that did not bind (id '{Id}').", dto?.Id);
                continue;
            }
            listedServerIds.Add(serverId);
            if (considered >= LocalStorageService.MaxNotificationsPerOwner)
                continue;
            considered++;
            if (snapshot.Tombstoned.Contains(serverId.ToString(CultureInfo.InvariantCulture)))
                continue;

            var isRead = dto.IsRead ?? false;
            if (localByServerId.TryGetValue(serverId, out var local))
            {
                if (!local.IsDirty && !snapshot.ReadAllPending && local.IsRead != isRead)
                {
                    // markDirty: false — the flag being applied IS the server's state.
                    var localId = local.Id;
                    await _storage.RunOnStorageAsync(() =>
                        _storage.SetNotificationRead(localId, isRead, markDirty: false));
                    local.IsRead = isRead;
                    changed++;
                }
                continue;
            }

            var visual = NotificationTypes.VisualFor(dto.Type);
            toInsert.Add(new NotificationRecord
            {
                Title = title,
                Subtitle = dto.Message ?? string.Empty,
                IconData = visual.IconData,
                IconUri = visual.IconUri,
                IconWidth = visual.IconWidth,
                IconHeight = visual.IconHeight,
                CreatedAt = ParseServerTimestamp(dto.CreatedAt),
                // A pending read_all outranks the listed flag the same way it gates
                // adoption: the batch will mark this server row read the moment it pushes,
                // and inserting it unread would re-light the bell the user just cleared
                // (PR #127 review, m7).
                IsRead = isRead || snapshot.ReadAllPending,
                ServerId = serverId,
                Type = dto.Type ?? string.Empty,
            });
        }

        // Deletion sweep (PR #127 review, M2 — "deletions follow the account", QA step 4):
        // a SNAPSHOT-time local row whose server id the full list no longer carries was
        // deleted on another machine (or by delete_all pushed from one) — mirror it.
        // Dirty rows are skipped: a pending local read toggle's push will meet
        // NOTIFICATION_NOT_FOUND and take the row down through that path instead. Rows
        // without a server id are local-only and never touched. tombstone: false — the
        // server row is already gone; no signal is raised (DeleteNotification only raises
        // when it tombstones).
        foreach (var local in locals)
        {
            if (local.ServerId is not { } localServerId
                || local.IsDirty
                || listedServerIds.Contains(localServerId))
            {
                continue;
            }
            var localId = local.Id;
            await _storage.RunOnStorageAsync(() =>
                _storage.DeleteNotification(localId, tombstone: false));
            changed++;
        }

        // toInsert holds at most MaxNotificationsPerOwner items (the considered bound
        // above) in newest-first list order; insert them OLDEST first so each lands above
        // the previous one, and AddNotification's own trim keeps the table at the cap —
        // new server entries still land on a full bell.
        for (var i = toInsert.Count - 1; i >= 0; i--)
        {
            var record = toInsert[i];
            // markDirty: false — the row IS the server's list; pushing it back would echo.
            var id = await _storage.RunOnStorageAsync(() =>
                _storage.AddNotification(user, ownerId, record, markDirty: false));
            if (id == 0)
                return changed;
            changed++;
        }

        if (changed > 0)
        {
            try
            {
                NotificationsPulled?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "NotificationsPulled subscriber failed.");
            }
        }
        return changed;
    }

    // Contract §21 timestamps are "yyyy-MM-dd HH:mm:ss" with no timezone; parsed
    // best-effort for the bell's date grouping — an unparseable value groups under "now"
    // rather than dropping the entry.
    private static DateTime ParseServerTimestamp(string? value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : DateTime.Now;

    // devices/list, merged two ways: hydrate an offline card for a server model this machine
    // has never seen (catalog lookup through the DeviceHost — the entry carries the visuals
    // the server row lacks), and mark a roster add for a local card the server does not know
    // yet, the reconcile that backfills pre-v2.1 accounts (signal: false — the coordinator
    // pass follows SyncCompleted). A pair with a pending .remove mark is never hydrated: the
    // pull must not resurrect a delete in flight. Best-effort like the rest of the pull — a
    // non-success list skips the step with a log line, never fails the pull.
    private async Task<int> PullDeviceRosterAsync(
        string ownerId,
        string token,
        Dictionary<(int VendorId, int ProductId), (int VendorId, int ProductId)> canonicalBucketByServerPair,
        CancellationToken ct)
    {
        // Pending .remove marks and the local card set, snapshotted BEFORE the list call —
        // the same ordering PullCoreAsync's tombstone snapshot uses, and for the same reason
        // (review round 1, S1): a coordinator pass overlapping this pull can confirm a
        // pending devices/remove and clear its mark mid-pull, but the list response this
        // step merges was built before that delete landed and still names the pair. Read
        // after the list, the cleared mark is invisible and the deleted model is hydrated
        // back; snapshotted first, the mark is in the snapshot whenever the list can still
        // list the pair.
        var devices = await _storage.RunOnStorageAsync(() =>
            _storage.GetDevices(LocalStorageService.OwnerKindUser, ownerId));
        if (devices is null)
            return 0;
        var localBuckets = new HashSet<(int VendorId, int ProductId)>();
        foreach (var device in devices)
        {
            var bucket = SettingsBucketOf(device);
            if (bucket.VendorId > 0 && bucket.ProductId > 0)
                localBuckets.Add(bucket);
        }

        var rosterMeta = await _storage.RunOnStorageAsync(() =>
            _storage.GetSyncMetaByPrefix(LocalStorageService.OwnerKindUser, ownerId, LocalStorageService.SyncMetaDeviceRosterPrefix));
        if (rosterMeta is null)
            return 0;
        var removePending = new HashSet<(int VendorId, int ProductId)>();
        foreach (var (key, _) in rosterMeta)
        {
            if (key.EndsWith(LocalStorageService.SyncMetaRosterRemoveSuffix, StringComparison.Ordinal)
                && SyncCoordinator.TryParseVidPidKey(key, LocalStorageService.SyncMetaDeviceRosterPrefix,
                    LocalStorageService.SyncMetaRosterRemoveSuffix, out var removeVid, out var removePid))
            {
                removePending.Add((removeVid, removePid));
            }
        }

        var list = await _syncApi.ListDevicesAsync(token, ct);
        if (!Succeeded(list, alreadyExistsCode: null))
        {
            _logger.LogWarning(
                "Sign-in pull: devices/list unavailable ({Message}); skipping the roster step.",
                list.Message ?? list.ErrorCode);
            return 0;
        }
        if (list.Devices is not { } items)
            return 0;

        var serverBuckets = new HashSet<(int VendorId, int ProductId)>();
        var written = 0;
        foreach (var dto in items)
        {
            if (dto?.VendorId is not int serverVid || dto.ProductId is not int serverPid
                || serverVid <= 0 || serverPid <= 0)
            {
                _logger.LogWarning(
                    "Sign-in pull: skipping devices/list item that did not bind (name '{Name}').", dto?.Name);
                continue;
            }

            // Canonicalized like every other pulled pair — see the alias note above step 1.
            (int VendorId, int ProductId) pair = canonicalBucketByServerPair.TryGetValue((serverVid, serverPid), out var canonical)
                ? canonical
                : (serverVid, serverPid);
            serverBuckets.Add(pair);

            if (localBuckets.Contains(pair) || removePending.Contains(pair))
                continue;

            var entry = await _catalogResolver.ResolveAsync(pair.VendorId, pair.ProductId, ct);
            if (entry is null)
            {
                _logger.LogInformation(
                    "Sign-in pull: server roster lists {VendorId:X4}:{ProductId:X4} but the catalog does not know it; skipping.",
                    pair.VendorId, pair.ProductId);
                continue;
            }

            var name = dto.Name is { Length: > 0 } serverName ? serverName : (entry.DeviceName ?? entry.Name);
            var record = DeviceCatalogResolver.BuildOfflineCardRecord(entry, pair.VendorId, pair.ProductId, name);
            // markDirty: false — the row IS the server's roster; re-adding it would be an echo.
            var id = await _storage.RunOnStorageAsync(() =>
                _storage.AddDevice(LocalStorageService.OwnerKindUser, ownerId, record, markDirty: false));
            if (id == 0)
                return written;
            localBuckets.Add(pair);
            written++;
        }

        foreach (var bucket in localBuckets)
        {
            if (!serverBuckets.Contains(bucket))
            {
                await _storage.RunOnStorageAsync(() =>
                    _storage.MarkSyncDirty(LocalStorageService.OwnerKindUser, ownerId,
                        LocalStorageService.SyncMetaDeviceRosterAdd(bucket.VendorId, bucket.ProductId), signal: false));
            }
        }
        return written;
    }

    // Runs inside a RunOnStorageAsync delegate. Returns the number of preference rows
    // written: zero when the owner already has any of the five known rows (the additive
    // policy — local state wins) or when the server object carries no known key. Unknown
    // keys are ignored for forward compatibility.
    private int ApplyServerPreferences(string ownerId, JsonElement serverPreferences)
    {
        if (ReadPreferences(_storage, ownerId) is not null)
            return 0;

        var written = 0;
        foreach (var property in serverPreferences.EnumerateObject())
        {
            if (!IsKnownPreferenceKey(property.Name))
                continue;

            // The wire carries numbers and booleans (ScrollOneScreenAtATime) — booleans map
            // to 1/0, the same encoding the push and SettingsRepository use.
            double value;
            if (property.Value.ValueKind == JsonValueKind.Number)
                value = property.Value.GetDouble();
            else if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                value = property.Value.GetBoolean() ? 1 : 0;
            else
                continue;

            _storage.SetAppPreference(LocalStorageService.OwnerKindUser, ownerId, property.Name, value);
            written++;
        }
        return written;
    }

    private static bool IsKnownPreferenceKey(string key) =>
        key is SettingsRepository.PrefSleepTimer
            or SettingsRepository.PrefScrollLines
            or SettingsRepository.PrefDoubleClickSpeed
            or SettingsRepository.PrefGenericLiftOffDistance
            or SettingsRepository.PrefScrollOneScreen;

    // internal static: SyncCoordinator builds identical create bodies for its reconcile
    // creates — one place owns the record → request mapping.
    internal static MacroCreateRequest BuildMacroRequest(MacroRecord macro, int vendorId, int productId)
    {
        var events = new List<MacroEventDto>(macro.Events.Count);
        foreach (var e in macro.Events)
            events.Add(new MacroEventDto { DelayMs = e.DelayMs, IsPress = e.IsPress, KeyCode = e.KeyCode });

        return new MacroCreateRequest
        {
            Uid = macro.Uid,
            VendorId = vendorId,
            ProductId = productId,
            Name = macro.Name,
            RepeatCount = macro.RepeatCount,
            RepeatMode = (int)macro.Mode,
            Events = events
        };
    }

    // The collapse rule both push paths share: API v2.0's devices/update keys on VID/PID
    // alone, so the per-unit applied-preset rows must become one. A row under a real unit
    // key is an explicit choice the user made on this machine; the '' row is what the
    // sign-in pull writes for the whole type, and it goes stale the moment the user applies
    // something else. So a unit-keyed row always wins and '' is only the fallback for a type
    // no unit has applied a preset to. Rows arrive ordered by device_key ('' first, then
    // unit keys in ordinal order), which makes the winner deterministic when several units
    // are present. Callers check `applied.Count > 0` first — an empty list means the type has
    // no applied preset at all, which is "nothing to push", not a collapse. internal static:
    // SyncCoordinator's device-state pass collapses the same rows
    // (docs/plans/add-api-v2-live-sync).
    /// <summary>
    /// Maps every pair a pre-fix release could have labelled this owner's device state with onto
    /// the settings bucket that device's rows now live in, built from the local <c>devices</c>
    /// table. Each stored card contributes its ROUTING pair (what the old push sent) and its own
    /// settings pair (identity, so a canonical pair stays canonical); a legacy row whose settings
    /// columns are unset contributes nothing beyond itself.
    /// </summary>
    /// <remarks>
    /// This is the whole of Open question 1's answer (b): the backend still keys presets, macros
    /// and device state by whatever <c>(vendor_id, product_id)</c> it was given, and reconciling
    /// what it already holds is explicitly out of scope. Locally, EVERY pull insert routes its
    /// pair through this alias — presets and macros cannot duplicate (they are matched by
    /// <c>uid</c>) but inserted under a raw alternate pair they would be invisible to the page
    /// reading the canonical bucket, and <c>device_applied_preset</c> could genuinely re-split.
    /// Returns null when storage has begun teardown, matching every other read on this path.
    /// </remarks>
    private async Task<Dictionary<(int VendorId, int ProductId), (int VendorId, int ProductId)>?>
        BuildSettingsBucketAliasAsync(string ownerId)
    {
        var devices = await _storage.RunOnStorageAsync(() =>
            _storage.GetDevices(LocalStorageService.OwnerKindUser, ownerId));
        if (devices is null)
            return null;

        var alias = new Dictionary<(int VendorId, int ProductId), (int VendorId, int ProductId)>();
        foreach (var device in devices)
        {
            var bucket = SettingsBucketOf(device);
            if (bucket.VendorId <= 0 || bucket.ProductId <= 0)
                continue;

            // Identity first and unconditionally: a canonical pair must never be rewritten by
            // another card's routing entry.
            alias[bucket] = bucket;
            if (device.VendorId > 0 && device.ProductId > 0)
                alias.TryAdd((device.VendorId, device.ProductId), bucket);
        }
        return alias;
    }

    /// <summary>
    /// The settings bucket a stored <c>devices</c> row addresses — the pair every per-device table
    /// is keyed by. Falls back to the routing pair for a row written before the settings columns
    /// existed (0 / 0), which is exactly the behaviour this code had before they did.
    /// </summary>
    internal static (int VendorId, int ProductId) SettingsBucketOf(DeviceRecord device) => (
        device.SettingsVendorId > 0 ? device.SettingsVendorId : device.VendorId,
        device.SettingsProductId > 0 ? device.SettingsProductId : device.ProductId);

    internal static (string DeviceKey, int PresetId) ChooseAppliedPresetRow(List<(string DeviceKey, int PresetId)> applied)
    {
        foreach (var row in applied)
        {
            if (row.DeviceKey.Length > 0)
                return row;
        }
        return applied[0];
    }

    // True when any row names a different preset than the collapse chose — i.e. the push is
    // about to drop someone's applied preset. Worth a warning in both push paths.
    internal static bool AppliedPresetRowsDisagree(List<(string DeviceKey, int PresetId)> applied, int chosenPresetId)
    {
        foreach (var row in applied)
        {
            if (row.PresetId != chosenPresetId)
                return true;
        }
        return false;
    }

    // Runs inside a RunOnStorageAsync delegate: the uids of one kind's pending tombstones, as
    // a set the merge loops probe per pulled item.
    private static HashSet<string> ReadTombstonedUids(LocalStorageService storage, string ownerId, string kind)
    {
        var tombstones = storage.GetSyncTombstones(LocalStorageService.OwnerKindUser, ownerId, kind);
        var uids = new HashSet<string>(tombstones.Count, StringComparer.Ordinal);
        foreach (var tombstone in tombstones)
            uids.Add(tombstone.Uid);
        return uids;
    }

    // Runs inside a RunOnStorageAsync delegate. Returns null when the owner never saved any
    // of the five preference rows — the push then sends nothing rather than inventing a
    // server record of defaults. internal static so SyncCoordinator's preferences pass
    // builds the identical payload.
    internal static Dictionary<string, object>? ReadPreferences(LocalStorageService storage, string ownerId)
    {
        const string kind = LocalStorageService.OwnerKindUser;
        var scrollOne = storage.GetAppPreference(kind, ownerId, SettingsRepository.PrefScrollOneScreen);
        var scrollLines = storage.GetAppPreference(kind, ownerId, SettingsRepository.PrefScrollLines);
        var doubleClick = storage.GetAppPreference(kind, ownerId, SettingsRepository.PrefDoubleClickSpeed);
        var sleep = storage.GetAppPreference(kind, ownerId, SettingsRepository.PrefSleepTimer);
        var lift = storage.GetAppPreference(kind, ownerId, SettingsRepository.PrefGenericLiftOffDistance);

        if (scrollOne is null && scrollLines is null && doubleClick is null && sleep is null && lift is null)
            return null;

        // Missing rows fall back to the same defaults LoadAppPreferencesAsync applies, so
        // the server record matches what the app actually behaves like.
        return new Dictionary<string, object>
        {
            [SettingsRepository.PrefSleepTimer] = sleep ?? 120,
            [SettingsRepository.PrefScrollLines] = scrollLines ?? 3,
            [SettingsRepository.PrefDoubleClickSpeed] = doubleClick ?? 550,
            [SettingsRepository.PrefGenericLiftOffDistance] = lift ?? 0,
            [SettingsRepository.PrefScrollOneScreen] = (scrollOne ?? 0) != 0,
        };
    }

    // A network error means the server is unreachable — every later call would just burn its
    // own timeout, so the whole remaining pipeline (push or pull) aborts (the additive/
    // idempotent-by-uid semantics let a future pass converge). A token rejection aborts the
    // same way: the server has invalidated the session, so every remaining item would carry
    // the same dead token — and a doomed response landing after a quick re-sign-in would
    // re-trip ApiHttpClient's rejection latch and sign the user out of the new session.
    private bool Aborts(ApiResponse response, string operation, string endpoint)
    {
        if (response.IsNetworkError)
        {
            _logger.LogWarning(
                "{Operation} aborted at {Endpoint}: {Message}", operation, endpoint, response.Message);
            return true;
        }
        if (HasErrorCode(response, AuthErrorCodes.TokenRequired)
            || HasErrorCode(response, AuthErrorCodes.InvalidOrExpiredToken))
        {
            _logger.LogWarning(
                "{Operation} aborted at {Endpoint}: session token rejected", operation, endpoint);
            return true;
        }
        // HTTP 429 (contract v2.0): the server is refusing calls, so every remaining item in
        // this pipeline would just spend another rejection. Abort like the two cases above —
        // dirty state persists and a later pass converges. Only SyncCoordinator arms a
        // cooldown off this; the pull runs once per sign-in, so there is nothing to throttle.
        if (response.IsRateLimited)
        {
            _logger.LogWarning(
                "{Operation} aborted at {Endpoint}: rate limited (retry after {RetryAfter})",
                operation, endpoint, response.RetryAfter);
            return true;
        }
        return false;
    }

    // Success for one pushed item: the envelope's success flag; a 2xx whose body failed to
    // bind (the PHP backend's shape quirks — counted as success WITHOUT an updated_at
    // write-back); or the endpoint's *_UID_ALREADY_EXISTS code, which means the record is
    // already on the server (idempotent retry semantics). internal static: SyncCoordinator
    // applies the same envelope semantics (docs/plans/add-api-v2-live-sync).
    internal static bool Succeeded(ApiResponse response, string? alreadyExistsCode)
    {
        if (response.Success)
            return true;
        if (response.StatusCode is >= 200 and < 300 && response.MalformedPayload)
            return true;
        return alreadyExistsCode is not null && HasErrorCode(response, alreadyExistsCode);
    }

    // The strict twin of Succeeded, for the paths where "success" clears a dirty mark on a
    // record the server already knows about. A 2xx whose body did not bind is exactly what
    // ApiHttpClient.ReadResponseAsync documents as the PHP router's answer to a route that
    // does not exist — the server may well have done nothing. Succeeded tolerates that on a
    // create path, where the record keeps `server_updated_at NULL` and the next pass
    // re-enumerates it; on an update path it would clear `is_dirty` while
    // `server_updated_at` stays set, dropping the row out of the dirty selection for good
    // and losing the edit silently (docs/plans/add-api-v2-live-sync).
    internal static bool ConfirmedSuccess(ApiResponse response) => response.Success;

    internal static bool HasErrorCode(ApiResponse response, string code)
    {
        if (response.ErrorCode == code)
            return true;
        if (response.Errors is { } errors)
        {
            foreach (var error in errors)
            {
                if (error?.ErrorCode == code)
                    return true;
            }
        }
        return false;
    }
}
