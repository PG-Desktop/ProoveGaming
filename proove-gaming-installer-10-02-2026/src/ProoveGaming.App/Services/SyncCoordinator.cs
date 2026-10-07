using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProoveGaming.App.Models;

namespace ProoveGaming.App.Services;

// The live-sync coordinator (docs/plans/add-api-v2-live-sync): keeps the server following a
// signed-in user's edits. LocalStorageService's mutators mark rows/keys dirty and raise
// SyncRelevantDataChanged; this singleton debounces those signals (3 s, restart-on-signal)
// and runs one push pass over everything owed to the server — dirty/unsynced macros and
// presets, delete tombstones, preferences and per-type device state — with the umbrella's
// conflict policy (client wins, one refresh-retry per item). ISettingsSyncService.
// SyncCompleted signals the same pass and is also the startup reconcile — raised after the
// sign-in pull and after the upgrade push, it is what converges an interrupted push (rows
// left with server_updated_at NULL are enumerated as unsynced). Everything runs on the pool,
// storage only via RunOnStorageAsync, failures are log-only: sync never blocks or surfaces
// in the UI.
public sealed class SyncCoordinator : IDisposable
{
    private const string MacroConflict = "MACRO_CONFLICT";
    private const string MacroNotFound = "MACRO_NOT_FOUND";
    private const string PresetConflict = "PRESET_CONFLICT";
    private const string PresetNotFound = "PRESET_NOT_FOUND";
    private const string DefaultPresetCannotBeDeleted = "DEFAULT_PRESET_CANNOT_BE_DELETED";
    private const string PreferencesConflict = "PREFERENCES_CONFLICT";
    private const string DeviceStateConflict = "DEVICE_STATE_CONFLICT";
    private const string ActivePresetNotFound = "ACTIVE_PRESET_NOT_FOUND";
    private const string DeviceAlreadyExists = "DEVICE_ALREADY_EXISTS";
    private const string DeviceNotFound = "DEVICE_NOT_FOUND";
    private const string NotificationNotFound = "NOTIFICATION_NOT_FOUND";

    // Server-side limits from contract §21.1; enforced client-side because the server only
    // names the error, and a rejected add is dropped rather than retried.
    private const int NotificationTitleMaxLength = 255;
    private const int NotificationTypeMaxLength = 50;

    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromSeconds(3);

    private readonly ISyncApiService _syncApi;
    private readonly LocalStorageService _storage;
    private readonly UserProfile _user;
    private readonly PresetConfigurationBuilder _configurationBuilder;
    private readonly ISettingsSyncService _settingsSync;
    private readonly ILogger<SyncCoordinator> _logger;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _rateLimitCooldown;

    // Fallback when a 429 carries no Retry-After: the contract's tightest documented window.
    private static readonly TimeSpan DefaultRateLimitCooldown = TimeSpan.FromSeconds(60);

    // Upper bound on any cooldown, Retry-After included: a server (or a skewed clock) asking
    // for hours must not silently stop sync for hours.
    private static readonly TimeSpan MaxRateLimitCooldown = TimeSpan.FromMinutes(5);

    // Guards the debounce/single-flight state below. Passes themselves run OUTSIDE the
    // gate; it is only ever held for field flips, so subscribers (mutator threads) never
    // wait on a pass.
    private readonly object _gate = new();
    private CancellationTokenSource? _debounceCts;
    private bool _passRunning;
    private bool _followUpRequested;
    private bool _disposed;
    private DateTimeOffset? _rateLimitedUntil;

    // Cancelled by Dispose so a pass sitting out a rate-limit cooldown at app exit unblocks
    // instead of holding the pool thread for the rest of the wait.
    private readonly CancellationTokenSource _lifetimeCts = new();

    public SyncCoordinator(
        ISyncApiService syncApi,
        LocalStorageService storage,
        UserProfile user,
        PresetConfigurationBuilder configurationBuilder,
        ISettingsSyncService settingsSync,
        ILogger<SyncCoordinator> logger)
        : this(syncApi, storage, user, configurationBuilder, settingsSync, logger, DefaultDebounce, DefaultRateLimitCooldown)
    {
    }

    // Test seam (InternalsVisibleTo ProoveGaming.App.Tests): a short debounce keeps the
    // tests timing-insensitive, same approach as the existing debounce tests.
    internal SyncCoordinator(
        ISyncApiService syncApi,
        LocalStorageService storage,
        UserProfile user,
        PresetConfigurationBuilder configurationBuilder,
        ISettingsSyncService settingsSync,
        ILogger<SyncCoordinator> logger,
        TimeSpan debounce)
        : this(syncApi, storage, user, configurationBuilder, settingsSync, logger, debounce, DefaultRateLimitCooldown)
    {
    }

    internal SyncCoordinator(
        ISyncApiService syncApi,
        LocalStorageService storage,
        UserProfile user,
        PresetConfigurationBuilder configurationBuilder,
        ISettingsSyncService settingsSync,
        ILogger<SyncCoordinator> logger,
        TimeSpan debounce,
        TimeSpan rateLimitCooldown)
    {
        _rateLimitCooldown = rateLimitCooldown;
        _syncApi = syncApi;
        _storage = storage;
        _user = user;
        _configurationBuilder = configurationBuilder;
        _settingsSync = settingsSync;
        _logger = logger;
        _debounce = debounce;

        _storage.SyncRelevantDataChanged += Signal;
        _settingsSync.SyncCompleted += Signal;

        // Deliberately no self-signal here. A session restored before construction ("Remember
        // me" startup) is exactly the case where App.axaml.cs then starts the sign-in pull, and
        // SettingsSyncService raises SyncCompleted on EVERY exit from it — success, exception
        // or network abort — so the reconcile arrives through the subscription above instead.
        // Signalling from the constructor would run this pass CONCURRENTLY with that pull, and
        // the pass deletes tombstoned records server-side and drops their tombstones: the pull
        // would then be merging a list built before those deletes against local state that has
        // already forgotten them. Ordering the pass after the pull is what removes that window
        // (docs/plans/add-api-v2-live-sync); the pull's own tombstone snapshot covers the rest.
    }

    // Test seam: the pass loop started by the last signal, awaitable so tests observe its
    // completion (the SettingsSyncService.PushTask pattern). Completes only after any
    // queued follow-up pass has run too; never faults.
    internal Task? CurrentPassTask { get; private set; }

    // Restart-on-signal debounce, like the app-preferences save debounce: every signal
    // cancels the pending delay and starts a new one, so a burst of edits collapses into
    // one pass ~3 s after the last of them. Cheap and non-blocking — safe to call from
    // storage mutators and event handlers.
    //
    // In production the only callers are the two event subscriptions above. internal
    // (InternalsVisibleTo ProoveGaming.App.Tests) so tests can drive a pass explicitly
    // rather than depend on a constructor side effect — which is what the ordering
    // guarantee above removed.
    internal void Signal()
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed)
                return;
            // Cancel strictly before Dispose: the pending Task.Delay observes the canceled
            // token synchronously, so disposing the source afterwards cannot fault it.
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            var cts = new CancellationTokenSource();
            _debounceCts = cts;
            // Token captured under the gate: Dispose (container teardown at app exit)
            // cancels+disposes _debounceCts, and reading .Token off a disposed source
            // throws — synchronously into the mutator that raised this event. Capturing
            // post-disposal is impossible here; a Dispose AFTER the capture is safe, since
            // it cancels first and the latched-canceled token just exits the delay.
            token = cts.Token;
        }
        _ = DebounceThenStartPassAsync(token);
    }

    private async Task DebounceThenStartPassAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(_debounce, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        StartPass();
    }

    // Single-flight: at most one pass at a time; a signal that lands while one is running
    // requests exactly one follow-up pass (a second, third, ... signal folds into the same
    // request), which the running loop executes before releasing the flag — so there is no
    // window where "pass running" is false but a signal has been dropped.
    private void StartPass()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_passRunning)
            {
                _followUpRequested = true;
                return;
            }
            _passRunning = true;
            CurrentPassTask = Task.Run(RunPassLoopAsync);
        }
    }

    private async Task RunPassLoopAsync()
    {
        // cooldownRetryUsed bounds the UNSIGNALLED retry only: after a 429 the loop waits out
        // the cooldown and retries once by itself, and a pass limited again then stops and
        // waits for the next signal — so a persistently limited server cannot become a
        // wait-retry spin. A signalled follow-up is always honoured, but never early:
        // WaitOutRateLimitCooldownAsync gates EVERY iteration, whichever reason triggered it,
        // so no API call is issued while a cooldown is still running. The wait sits at the TOP
        // of the body on purpose: a second 429 exits the loop with _rateLimitedUntil still
        // armed, and the marker deliberately survives that exit so the first iteration of the
        // NEXT run — a debounced signal landing seconds later — sits it out instead of hitting
        // the server inside the window it just named. Dirty state survives either way.
        var cooldownRetryUsed = false;
        while (true)
        {
            if (!await WaitOutRateLimitCooldownAsync())
                return;

            try
            {
                await RunPassAsync();
            }
            catch (Exception ex)
            {
                // Best-effort by contract: dirty state persists, the next signal retries.
                _logger.LogError(ex, "Sync pass failed.");
            }

            lock (_gate)
            {
                var rateLimited = _rateLimitedUntil is not null;
                var retryUnsignalled = rateLimited && !cooldownRetryUsed;
                if (_disposed || !(_followUpRequested || retryUnsignalled))
                {
                    _passRunning = false;
                    return;
                }
                // No signal behind this iteration means it is the automatic retry, so it
                // spends the single-shot budget. A signalled pass leaves the budget alone:
                // the user asked for it, and it still waits out the cooldown above.
                if (!_followUpRequested)
                    cooldownRetryUsed = true;
                _followUpRequested = false;
            }
        }
    }

    // One full push pass. Token/customer are read once at pass start: an empty token
    // (signed out mid-debounce, guest session) idles without API calls and without logs
    // above Debug — sign-out needs no unsubscription choreography.
    private async Task RunPassAsync()
    {
        // A pass never overlaps a sign-in pull that is already running: the constructor
        // orders the startup pass after the pull (see the no-self-signal note there), and
        // this await extends the same rule to mid-pull signals — a mutator edit landing
        // while the pull hydrates would otherwise let this pass push a seeded default the
        // pull is about to supersede (docs/plans/fix-duplicate-default-preset). Safe to
        // await: PullTask never faults and completes on every exit including network
        // aborts, and the pull never waits on a pass. A pull STARTING mid-pass (re-sign-in)
        // is the accepted bounded residual recorded in that plan's Risks.
        await _settingsSync.PullTask;

        var token = _user.SessionToken;
        var customerId = _user.CustomerId;
        if (customerId <= 0 || string.IsNullOrEmpty(token))
        {
            _logger.LogDebug("Sync pass idle: no signed-in session.");
            return;
        }
        var ownerId = customerId.ToString(CultureInfo.InvariantCulture);

        // Pass order per the plan: macros before presets (configurations reference macro
        // uids), content before tombstones of the next kind, preferences, device state
        // (its active_preset_uid must exist server-side — preset creates re-dirty it), the
        // device roster (docs/plans/add-api-v21-device-roster), then notifications
        // (docs/plans/add-api-v21-notifications-sync). Every helper but the last returns
        // false to abort the whole pass: a network error or token rejection dooms every
        // remaining call, and dirty state persists for the next signal. Storage reads
        // returning null mean teardown has begun — same abort. The notifications step is
        // last and so has nothing left to abort.
        if (!await PushMacrosAsync(ownerId, token))
            return;
        if (!await PushMacroTombstonesAsync(ownerId, token))
            return;
        if (!await PushPresetsAsync(ownerId, token))
            return;
        if (!await PushPresetTombstonesAsync(ownerId, token))
            return;
        if (!await PushPreferencesAsync(ownerId, token))
            return;
        if (!await PushDeviceStateAsync(ownerId, token))
            return;
        if (!await PushDeviceRosterAsync(ownerId, token))
            return;
        await PushNotificationsAsync(ownerId, token);
    }

    // ── Macros ───────────────────────────────────────────────────────────────

    private async Task<bool> PushMacrosAsync(string ownerId, string token)
    {
        var dirty = await _storage.RunOnStorageAsync(() =>
            _storage.GetDirtyMacros(LocalStorageService.OwnerKindUser, ownerId));
        if (dirty is null)
            return false;

        foreach (var (macro, vendorId, productId) in dirty)
        {
            if (macro.ServerUpdatedAt is null)
            {
                var create = await _syncApi.CreateMacroAsync(token, SettingsSyncService.BuildMacroRequest(macro, vendorId, productId));
                if (Aborts(create, "macros/create"))
                    return false;
                if (SettingsSyncService.HasErrorCode(create, SettingsSyncService.MacroUidAlreadyExists))
                {
                    // An interrupted earlier push created it — converge by updating with
                    // the server's current updated_at.
                    var get = await _syncApi.GetMacroAsync(token, macro.Uid);
                    if (Aborts(get, "macros/get"))
                        return false;
                    if (get.Macro?.UpdatedAt is not { Length: > 0 } current)
                    {
                        _logger.LogWarning(
                            "Sync pass: macros/get returned no updated_at for macro {Uid}; leaving it dirty.", macro.Uid);
                        continue;
                    }
                    if (!await PushMacroUpdateAsync(macro, vendorId, productId, current, token))
                        return false;
                }
                else if (SettingsSyncService.Succeeded(create, alreadyExistsCode: null))
                {
                    await StoreMacroResultAsync(macro, create.Macro?.UpdatedAt);
                }
                else
                {
                    _logger.LogWarning(
                        "Sync pass: macros/create rejected macro {Uid}: {Message}",
                        macro.Uid, create.Message ?? create.ErrorCode);
                }
            }
            else if (!await PushMacroUpdateAsync(macro, vendorId, productId, macro.ServerUpdatedAt, token))
            {
                return false;
            }
        }
        return true;
    }

    // Sends macros/update with the given updated_at; on MACRO_CONFLICT refreshes via
    // macros/get and resends exactly once (client wins). Returns false only when the pass
    // must abort — per-item rejections log and leave the row dirty for the next pass.
    private async Task<bool> PushMacroUpdateAsync(MacroRecord macro, int vendorId, int productId, string updatedAt, string token)
    {
        var response = await _syncApi.UpdateMacroAsync(token, BuildMacroUpdateRequest(macro, vendorId, productId, updatedAt));
        if (Aborts(response, "macros/update"))
            return false;

        if (SettingsSyncService.HasErrorCode(response, MacroConflict))
        {
            var get = await _syncApi.GetMacroAsync(token, macro.Uid);
            if (Aborts(get, "macros/get"))
                return false;
            if (get.Macro?.UpdatedAt is not { Length: > 0 } refreshed)
            {
                _logger.LogWarning(
                    "Sync pass: macros/get after MACRO_CONFLICT returned no updated_at for {Uid}; leaving it dirty.", macro.Uid);
                return true;
            }
            response = await _syncApi.UpdateMacroAsync(token, BuildMacroUpdateRequest(macro, vendorId, productId, refreshed));
            if (Aborts(response, "macros/update"))
                return false;
            if (SettingsSyncService.HasErrorCode(response, MacroConflict))
            {
                _logger.LogWarning("Sync pass: macros/update conflicted twice for {Uid}; leaving it dirty.", macro.Uid);
                return true;
            }
        }

        // ConfirmedSuccess, not Succeeded: this row already has a server_updated_at, so
        // clearing is_dirty on an unparseable 2xx would drop it out of the dirty selection
        // for good. Leaving it dirty costs one retried update next pass.
        if (SettingsSyncService.ConfirmedSuccess(response))
            await StoreMacroResultAsync(macro, response.Macro?.UpdatedAt);
        else if (SettingsSyncService.Succeeded(response, alreadyExistsCode: null))
            _logger.LogWarning(
                "Sync pass: macros/update for macro {Uid} returned an unparseable 2xx body; leaving it dirty.", macro.Uid);
        else
            _logger.LogWarning(
                "Sync pass: macros/update rejected macro {Uid}: {Message}",
                macro.Uid, response.Message ?? response.ErrorCode);
        return true;
    }

    // Write-back after a confirmed push: store the echoed updated_at (when the envelope
    // carried one) and clear the dirty flag rev-guarded — a row re-edited while the push was
    // in flight bumped local_rev, so the clear misses and the follow-up pass re-pushes it.
    private Task StoreMacroResultAsync(MacroRecord macro, string? updatedAt) =>
        _storage.RunOnStorageAsync(() =>
        {
            if (updatedAt is { Length: > 0 })
                _storage.SetMacroServerUpdatedAt(macro.Id, updatedAt);
            _storage.ClearMacroDirty(macro.Id, macro.LocalRev);
        });

    private static MacroUpdateRequest BuildMacroUpdateRequest(MacroRecord macro, int vendorId, int productId, string updatedAt)
    {
        var events = new List<MacroEventDto>(macro.Events.Count);
        foreach (var e in macro.Events)
            events.Add(new MacroEventDto { DelayMs = e.DelayMs, IsPress = e.IsPress, KeyCode = e.KeyCode });

        return new MacroUpdateRequest
        {
            Uid = macro.Uid,
            VendorId = vendorId,
            ProductId = productId,
            Name = macro.Name,
            RepeatCount = macro.RepeatCount,
            RepeatMode = (int)macro.Mode,
            Events = events,
            UpdatedAt = updatedAt
        };
    }

    private async Task<bool> PushMacroTombstonesAsync(string ownerId, string token)
    {
        var tombstones = await _storage.RunOnStorageAsync(() =>
            _storage.GetSyncTombstones(LocalStorageService.OwnerKindUser, ownerId, LocalStorageService.TombstoneKindMacro));
        if (tombstones is null)
            return false;

        foreach (var (uid, _, _, serverUpdatedAt) in tombstones)
        {
            var response = await _syncApi.DeleteMacroAsync(token, uid, serverUpdatedAt);
            if (Aborts(response, "macros/delete"))
                return false;

            if (SettingsSyncService.HasErrorCode(response, MacroConflict))
            {
                var get = await _syncApi.GetMacroAsync(token, uid);
                if (Aborts(get, "macros/get"))
                    return false;
                if (get.Macro?.UpdatedAt is not { Length: > 0 } refreshed)
                {
                    // Deleted server-side between the two calls — goal already met.
                    if (SettingsSyncService.HasErrorCode(get, MacroNotFound))
                        await RemoveTombstoneAsync(ownerId, LocalStorageService.TombstoneKindMacro, uid);
                    else
                        _logger.LogWarning(
                            "Sync pass: macros/get after MACRO_CONFLICT returned no updated_at for {Uid}; keeping the tombstone.", uid);
                    continue;
                }
                response = await _syncApi.DeleteMacroAsync(token, uid, refreshed);
                if (Aborts(response, "macros/delete"))
                    return false;
                if (SettingsSyncService.HasErrorCode(response, MacroConflict))
                {
                    _logger.LogWarning("Sync pass: macros/delete conflicted twice for {Uid}; keeping the tombstone.", uid);
                    continue;
                }
            }

            // MACRO_NOT_FOUND is the same outcome as a successful delete: the record is gone.
            if (SettingsSyncService.Succeeded(response, alreadyExistsCode: null)
                || SettingsSyncService.HasErrorCode(response, MacroNotFound))
            {
                await RemoveTombstoneAsync(ownerId, LocalStorageService.TombstoneKindMacro, uid);
            }
            else
            {
                _logger.LogWarning(
                    "Sync pass: macros/delete rejected macro {Uid}: {Message}",
                    uid, response.Message ?? response.ErrorCode);
            }
        }
        return true;
    }

    // ── Presets ──────────────────────────────────────────────────────────────

    private async Task<bool> PushPresetsAsync(string ownerId, string token)
    {
        var dirty = await _storage.RunOnStorageAsync(() =>
            _storage.GetDirtyPresets(LocalStorageService.OwnerKindUser, ownerId));
        if (dirty is null)
            return false;
        if (dirty.Count == 0)
            return true;

        // One bulk uid read inverted to the id → uid map the configuration builder needs.
        var macroIdsByUid = await _storage.RunOnStorageAsync(() =>
            _storage.GetMacroIdsByUid(LocalStorageService.OwnerKindUser, ownerId));
        if (macroIdsByUid is null)
            return false;
        var macroUidsById = new Dictionary<int, string>(macroIdsByUid.Count);
        foreach (var (uid, id) in macroIdsByUid)
            macroUidsById[id] = uid;

        foreach (var (preset, vendorId, productId) in dirty)
        {
            var configuration = await _storage.RunOnStorageAsync(() =>
                _configurationBuilder.Build(preset.Id, macroUidsById));
            if (configuration is null)
                return false;

            if (preset.ServerUpdatedAt is null)
            {
                var create = await _syncApi.CreatePresetAsync(token, new PresetCreateRequest
                {
                    Uid = preset.Uid,
                    VendorId = vendorId,
                    ProductId = productId,
                    Name = preset.Name,
                    Description = preset.Description,
                    IsDefault = preset.IsDefault,
                    Configuration = configuration
                });
                if (Aborts(create, "presets/create"))
                    return false;
                if (SettingsSyncService.HasErrorCode(create, SettingsSyncService.PresetUidAlreadyExists))
                {
                    var get = await _syncApi.GetPresetAsync(token, preset.Uid);
                    if (Aborts(get, "presets/get"))
                        return false;
                    if (get.Preset?.UpdatedAt is not { Length: > 0 } current)
                    {
                        _logger.LogWarning(
                            "Sync pass: presets/get returned no updated_at for preset {Uid}; leaving it dirty.", preset.Uid);
                        continue;
                    }
                    if (!await PushPresetUpdateAsync(preset, vendorId, productId, configuration, current, token))
                        return false;
                }
                else if (SettingsSyncService.Succeeded(create, alreadyExistsCode: null))
                {
                    var echoedUpdatedAt = create.Preset?.UpdatedAt;
                    await _storage.RunOnStorageAsync(() =>
                    {
                        if (echoedUpdatedAt is { Length: > 0 })
                            _storage.SetPresetServerUpdatedAt(preset.Id, echoedUpdatedAt);
                        _storage.ClearPresetDirty(preset.Id, preset.LocalRev);
                        // A devices/update for this type may have been dropped earlier with
                        // ACTIVE_PRESET_NOT_FOUND (the preset did not exist server-side
                        // yet). It does now — re-mark the type so this pass's device-state
                        // step pushes it. Deliberately signal: false (no event): the mark
                        // is consumed later in THIS pass, no fresh signal wanted.
                        _storage.MarkSyncDirty(LocalStorageService.OwnerKindUser, ownerId,
                            LocalStorageService.SyncMetaDeviceStateDirty(vendorId, productId), signal: false);
                    });
                }
                else
                {
                    _logger.LogWarning(
                        "Sync pass: presets/create rejected preset {Uid}: {Message}",
                        preset.Uid, create.Message ?? create.ErrorCode);
                }
            }
            else if (!await PushPresetUpdateAsync(preset, vendorId, productId, configuration, preset.ServerUpdatedAt, token))
            {
                return false;
            }
        }
        return true;
    }

    // PushMacroUpdateAsync's preset twin: update, one refresh-retry on PRESET_CONFLICT.
    private async Task<bool> PushPresetUpdateAsync(PresetRecord preset, int vendorId, int productId,
        Dictionary<string, object?> configuration, string updatedAt, string token)
    {
        var response = await _syncApi.UpdatePresetAsync(token, BuildPresetUpdateRequest(preset, vendorId, productId, configuration, updatedAt));
        if (Aborts(response, "presets/update"))
            return false;

        if (SettingsSyncService.HasErrorCode(response, PresetConflict))
        {
            var get = await _syncApi.GetPresetAsync(token, preset.Uid);
            if (Aborts(get, "presets/get"))
                return false;
            if (get.Preset?.UpdatedAt is not { Length: > 0 } refreshed)
            {
                _logger.LogWarning(
                    "Sync pass: presets/get after PRESET_CONFLICT returned no updated_at for {Uid}; leaving it dirty.", preset.Uid);
                return true;
            }
            response = await _syncApi.UpdatePresetAsync(token, BuildPresetUpdateRequest(preset, vendorId, productId, configuration, refreshed));
            if (Aborts(response, "presets/update"))
                return false;
            if (SettingsSyncService.HasErrorCode(response, PresetConflict))
            {
                _logger.LogWarning("Sync pass: presets/update conflicted twice for {Uid}; leaving it dirty.", preset.Uid);
                return true;
            }
        }

        // ConfirmedSuccess — see PushMacroUpdateAsync: an unparseable 2xx must not clear
        // dirty on a row that already carries a server_updated_at.
        if (SettingsSyncService.ConfirmedSuccess(response))
        {
            var echoedUpdatedAt = response.Preset?.UpdatedAt;
            await _storage.RunOnStorageAsync(() =>
            {
                if (echoedUpdatedAt is { Length: > 0 })
                    _storage.SetPresetServerUpdatedAt(preset.Id, echoedUpdatedAt);
                _storage.ClearPresetDirty(preset.Id, preset.LocalRev);
            });
        }
        else if (SettingsSyncService.Succeeded(response, alreadyExistsCode: null))
        {
            _logger.LogWarning(
                "Sync pass: presets/update for preset {Uid} returned an unparseable 2xx body; leaving it dirty.", preset.Uid);
        }
        else
        {
            _logger.LogWarning(
                "Sync pass: presets/update rejected preset {Uid}: {Message}",
                preset.Uid, response.Message ?? response.ErrorCode);
        }
        return true;
    }

    private static PresetUpdateRequest BuildPresetUpdateRequest(PresetRecord preset, int vendorId, int productId,
        Dictionary<string, object?> configuration, string updatedAt) => new()
    {
        Uid = preset.Uid,
        VendorId = vendorId,
        ProductId = productId,
        Name = preset.Name,
        Description = preset.Description,
        IsDefault = preset.IsDefault,
        Configuration = configuration,
        UpdatedAt = updatedAt
    };

    private async Task<bool> PushPresetTombstonesAsync(string ownerId, string token)
    {
        var tombstones = await _storage.RunOnStorageAsync(() =>
            _storage.GetSyncTombstones(LocalStorageService.OwnerKindUser, ownerId, LocalStorageService.TombstoneKindPreset));
        if (tombstones is null)
            return false;

        foreach (var (uid, _, _, serverUpdatedAt) in tombstones)
        {
            var response = await _syncApi.DeletePresetAsync(token, uid, serverUpdatedAt);
            if (Aborts(response, "presets/delete"))
                return false;

            if (SettingsSyncService.HasErrorCode(response, PresetConflict))
            {
                var get = await _syncApi.GetPresetAsync(token, uid);
                if (Aborts(get, "presets/get"))
                    return false;
                if (get.Preset?.UpdatedAt is not { Length: > 0 } refreshed)
                {
                    if (SettingsSyncService.HasErrorCode(get, PresetNotFound))
                        await RemoveTombstoneAsync(ownerId, LocalStorageService.TombstoneKindPreset, uid);
                    else
                        _logger.LogWarning(
                            "Sync pass: presets/get after PRESET_CONFLICT returned no updated_at for {Uid}; keeping the tombstone.", uid);
                    continue;
                }
                response = await _syncApi.DeletePresetAsync(token, uid, refreshed);
                if (Aborts(response, "presets/delete"))
                    return false;
                if (SettingsSyncService.HasErrorCode(response, PresetConflict))
                {
                    _logger.LogWarning("Sync pass: presets/delete conflicted twice for {Uid}; keeping the tombstone.", uid);
                    continue;
                }
            }

            if (SettingsSyncService.HasErrorCode(response, DefaultPresetCannotBeDeleted))
            {
                // Local already deleted; the server keeps its default (contract §16.5 lets
                // only the Launcher reassign it) — the next default change pushes over it.
                _logger.LogWarning(
                    "Sync pass: server refused to delete preset {Uid} (DEFAULT_PRESET_CANNOT_BE_DELETED); dropping the tombstone.", uid);
                await RemoveTombstoneAsync(ownerId, LocalStorageService.TombstoneKindPreset, uid);
            }
            else if (SettingsSyncService.Succeeded(response, alreadyExistsCode: null)
                || SettingsSyncService.HasErrorCode(response, PresetNotFound))
            {
                await RemoveTombstoneAsync(ownerId, LocalStorageService.TombstoneKindPreset, uid);
            }
            else
            {
                _logger.LogWarning(
                    "Sync pass: presets/delete rejected preset {Uid}: {Message}",
                    uid, response.Message ?? response.ErrorCode);
            }
        }
        return true;
    }

    private Task RemoveTombstoneAsync(string ownerId, string kind, string uid) =>
        _storage.RunOnStorageAsync(() =>
            _storage.RemoveSyncTombstone(LocalStorageService.OwnerKindUser, ownerId, kind, uid));

    // ── Preferences ──────────────────────────────────────────────────────────

    private async Task<bool> PushPreferencesAsync(string ownerId, string token)
    {
        const string user = LocalStorageService.OwnerKindUser;
        var dirtyFlag = await _storage.RunOnStorageAsync(() =>
            _storage.GetSyncMeta(user, ownerId, LocalStorageService.SyncMetaPreferencesDirty));
        if (dirtyFlag is null)
            return true;

        // The flag's value is a generation (MarkSyncDirty). It stays put across the HTTP
        // call, and success clears it only while it still holds the value read here — a
        // save landing mid-flight wrote a newer generation, so the clear misses and the
        // follow-up pass pushes the newer values. Failure paths just leave the flag alone:
        // no restore choreography, so an app exit mid-push cannot lose the mark. This is
        // the sync_meta equivalent of the rows' local_rev guard.
        // Read: true distinguishes the two ways Preferences comes back null. Teardown makes
        // RunOnStorageAsync yield default(T) — Read false — and aborts the pass like every
        // other storage read; a genuine read that found none of the five rows leaves a dirty
        // flag with nothing behind it, so consume the flag instead of re-reading storage
        // twice on every later pass.
        var state = await _storage.RunOnStorageAsync(() =>
            (Read: true,
                Preferences: SettingsSyncService.ReadPreferences(_storage, ownerId),
                UpdatedAt: _storage.GetSyncMeta(user, ownerId, LocalStorageService.SyncMetaPreferencesUpdatedAt)));
        if (!state.Read)
            return false;
        if (state.Preferences is null)
        {
            await _storage.RunOnStorageAsync(() =>
                _storage.RemoveSyncMetaIfValue(user, ownerId, LocalStorageService.SyncMetaPreferencesDirty, dirtyFlag));
            return true;
        }

        var response = await _syncApi.UpdatePreferencesAsync(token, state.Preferences, state.UpdatedAt);
        if (Aborts(response, "preferences/update"))
            return false;

        if (SettingsSyncService.HasErrorCode(response, PreferencesConflict))
        {
            var get = await _syncApi.GetPreferencesAsync(token);
            if (Aborts(get, "preferences/get"))
                return false;
            if (get.UpdatedAt is not { Length: > 0 } refreshed)
            {
                _logger.LogWarning("Sync pass: preferences/get after PREFERENCES_CONFLICT returned no updated_at; retrying next pass.");
                return true;
            }
            response = await _syncApi.UpdatePreferencesAsync(token, state.Preferences, refreshed);
            if (Aborts(response, "preferences/update"))
                return false;
            if (SettingsSyncService.HasErrorCode(response, PreferencesConflict))
            {
                _logger.LogWarning("Sync pass: preferences/update conflicted twice; retrying next pass.");
                return true;
            }
        }

        // ConfirmedSuccess — see PushMacroUpdateAsync: clearing the dirty flag on an
        // unparseable 2xx would strand the saved values, since nothing re-marks them.
        if (SettingsSyncService.ConfirmedSuccess(response))
        {
            var newUpdatedAt = response.UpdatedAt;
            await _storage.RunOnStorageAsync(() =>
            {
                if (newUpdatedAt is { Length: > 0 })
                    _storage.SetSyncMeta(user, ownerId, LocalStorageService.SyncMetaPreferencesUpdatedAt, newUpdatedAt);
                _storage.RemoveSyncMetaIfValue(user, ownerId, LocalStorageService.SyncMetaPreferencesDirty, dirtyFlag);
            });
            return true;
        }

        if (SettingsSyncService.Succeeded(response, alreadyExistsCode: null))
        {
            _logger.LogWarning(
                "Sync pass: preferences/update returned an unparseable 2xx body; leaving the dirty flag set.");
            return true;
        }

        _logger.LogWarning(
            "Sync pass: preferences/update rejected: {Message}", response.Message ?? response.ErrorCode);
        return true;
    }

    // ── Device state ─────────────────────────────────────────────────────────

    // Returns false to abort the pass like the Push*Async steps above (a network error,
    // token rejection or teardown read dooms the roster step that follows); true means the
    // roster step still runs.
    private async Task<bool> PushDeviceStateAsync(string ownerId, string token)
    {
        const string user = LocalStorageService.OwnerKindUser;
        var meta = await _storage.RunOnStorageAsync(() =>
            _storage.GetSyncMetaByPrefix(user, ownerId, LocalStorageService.SyncMetaDeviceStatePrefix));
        if (meta is null)
            return false;

        foreach (var (key, dirtyGeneration) in meta)
        {
            if (!key.EndsWith(LocalStorageService.SyncMetaDirtySuffix, StringComparison.Ordinal))
                continue;
            if (!TryParseVidPidKey(key, LocalStorageService.SyncMetaDeviceStatePrefix, LocalStorageService.SyncMetaDirtySuffix, out var vendorId, out var productId))
            {
                _logger.LogWarning("Sync pass: unrecognized sync_meta key '{Key}'; skipping.", key);
                continue;
            }
            var updatedAtKey = LocalStorageService.SyncMetaDeviceStateUpdatedAt(vendorId, productId);

            // Generation-guarded clear-after-success — same reasoning as the preferences
            // pass. Paths that consume the mark deliberately (pushed, unpushable, or
            // retried through the presets/create re-mark instead) clear it guarded; failure
            // paths leave it alone for the next pass.
            var state = await _storage.RunOnStorageAsync(() =>
                (Applied: _storage.GetAppliedPresets(user, ownerId, vendorId, productId),
                    Presets: _storage.GetPresets(user, ownerId, vendorId, productId),
                    UpdatedAt: _storage.GetSyncMeta(user, ownerId, updatedAtKey)));
            if (state.Applied is null)
                return false;
            if (state.Applied.Count == 0)
            {
                // Nothing applied for the type — nothing to push; consume the mark.
                await ClearDeviceStateDirtyAsync(ownerId, key, dirtyGeneration);
                continue;
            }

            // Collapse rule from the upgrade push (ChooseAppliedPresetRow): a row written
            // under a real unit key is this machine's explicit choice and wins; the '' row —
            // what the sign-in pull writes for the whole type — is only the fallback for a
            // type no unit has applied a preset to. API v2.0 has no per-unit device state.
            var chosenPresetId = SettingsSyncService.ChooseAppliedPresetRow(state.Applied).PresetId;
            string? activeUid = null;
            foreach (var preset in state.Presets)
            {
                if (preset.Id == chosenPresetId)
                {
                    activeUid = preset.Uid;
                    break;
                }
            }
            if (activeUid is null)
            {
                _logger.LogWarning(
                    "Sync pass: applied preset {PresetId} for device {VendorId:X4}:{ProductId:X4} has no local row; skipping devices/update.",
                    chosenPresetId, vendorId, productId);
                await ClearDeviceStateDirtyAsync(ownerId, key, dirtyGeneration);
                continue;
            }

            if (SettingsSyncService.AppliedPresetRowsDisagree(state.Applied, chosenPresetId))
            {
                _logger.LogWarning(
                    "Sync pass: units of device {VendorId:X4}:{ProductId:X4} disagree on the applied preset; pushing preset {PresetUid} and dropping the rest (API v2.0 has no per-unit device state).",
                    vendorId, productId, activeUid);
            }

            var response = await _syncApi.UpdateDeviceStateAsync(token, vendorId, productId, activeUid, state.UpdatedAt);
            if (Aborts(response, "devices/update"))
                return false;

            if (SettingsSyncService.HasErrorCode(response, DeviceStateConflict))
            {
                var get = await _syncApi.GetDeviceStateAsync(token, vendorId, productId);
                if (Aborts(get, "devices/get"))
                    return false;
                if (get.UpdatedAt is not { Length: > 0 } refreshed)
                {
                    _logger.LogWarning(
                        "Sync pass: devices/get after DEVICE_STATE_CONFLICT returned no updated_at for {VendorId:X4}:{ProductId:X4}; retrying next pass.",
                        vendorId, productId);
                    continue;
                }
                response = await _syncApi.UpdateDeviceStateAsync(token, vendorId, productId, activeUid, refreshed);
                if (Aborts(response, "devices/update"))
                    return false;
                if (SettingsSyncService.HasErrorCode(response, DeviceStateConflict))
                {
                    _logger.LogWarning(
                        "Sync pass: devices/update conflicted twice for {VendorId:X4}:{ProductId:X4}; retrying next pass.",
                        vendorId, productId);
                    continue;
                }
            }

            if (SettingsSyncService.HasErrorCode(response, ActivePresetNotFound))
            {
                // The preset's own create is still pending or rejected. Consume the mark: a
                // successful presets/create re-marks the type (see PushPresetsAsync), which
                // is what retries this — not a blind loop here.
                _logger.LogWarning(
                    "Sync pass: devices/update for {VendorId:X4}:{ProductId:X4} rejected (ACTIVE_PRESET_NOT_FOUND) — preset {Uid} is not on the server yet.",
                    vendorId, productId, activeUid);
                await ClearDeviceStateDirtyAsync(ownerId, key, dirtyGeneration);
                continue;
            }

            // ConfirmedSuccess — see PushMacroUpdateAsync: clearing the dirty mark on an
            // unparseable 2xx would strand the applied preset, since nothing re-marks it.
            if (SettingsSyncService.ConfirmedSuccess(response))
            {
                var newUpdatedAt = response.UpdatedAt;
                await _storage.RunOnStorageAsync(() =>
                {
                    if (newUpdatedAt is { Length: > 0 })
                        _storage.SetSyncMeta(user, ownerId, updatedAtKey, newUpdatedAt);
                    _storage.RemoveSyncMetaIfValue(user, ownerId, key, dirtyGeneration);
                });
            }
            else if (SettingsSyncService.Succeeded(response, alreadyExistsCode: null))
            {
                _logger.LogWarning(
                    "Sync pass: devices/update for {VendorId:X4}:{ProductId:X4} returned an unparseable 2xx body; leaving the dirty mark set.",
                    vendorId, productId);
            }
            else
            {
                _logger.LogWarning(
                    "Sync pass: devices/update rejected for {VendorId:X4}:{ProductId:X4}: {Message}",
                    vendorId, productId, response.Message ?? response.ErrorCode);
            }
        }
        return true;
    }

    private Task ClearDeviceStateDirtyAsync(string ownerId, string dirtyKey, string generation) =>
        _storage.RunOnStorageAsync(() =>
            _storage.RemoveSyncMetaIfValue(LocalStorageService.OwnerKindUser, ownerId, dirtyKey, generation));

    // ── Device roster (docs/plans/add-api-v21-device-roster) ─────────────────
    // Pushes the deviceroster.* intent marks the devices-table mutators wrote: .add →
    // devices/add (the model joins the account's roster), .remove → devices/remove.
    // Membership is idempotent — DEVICE_ALREADY_EXISTS / DEVICE_NOT_FOUND consume the mark
    // like success. Validation rejections also consume it (resending the same payload can
    // never succeed; the next card mutation re-marks); an unparseable 2xx leaves it set,
    // same ConfirmedSuccess discipline as every other update path.
    private async Task<bool> PushDeviceRosterAsync(string ownerId, string token)
    {
        const string user = LocalStorageService.OwnerKindUser;
        var meta = await _storage.RunOnStorageAsync(() =>
            _storage.GetSyncMetaByPrefix(user, ownerId, LocalStorageService.SyncMetaDeviceRosterPrefix));
        if (meta is null)
            return false;

        // The cards are only needed for .add names; read once, lazily indexable by bucket.
        List<DeviceRecord>? devices = null;

        foreach (var (key, generation) in meta)
        {
            var isAdd = key.EndsWith(LocalStorageService.SyncMetaRosterAddSuffix, StringComparison.Ordinal);
            var isRemove = !isAdd && key.EndsWith(LocalStorageService.SyncMetaRosterRemoveSuffix, StringComparison.Ordinal);
            if (!isAdd && !isRemove)
            {
                _logger.LogWarning("Sync pass: unrecognized sync_meta key '{Key}'; skipping.", key);
                continue;
            }
            var suffix = isAdd ? LocalStorageService.SyncMetaRosterAddSuffix : LocalStorageService.SyncMetaRosterRemoveSuffix;
            if (!TryParseVidPidKey(key, LocalStorageService.SyncMetaDeviceRosterPrefix, suffix, out var vendorId, out var productId))
            {
                _logger.LogWarning("Sync pass: unrecognized sync_meta key '{Key}'; skipping.", key);
                continue;
            }

            ApiResponse response;
            string endpoint;
            if (isAdd)
            {
                devices ??= await _storage.RunOnStorageAsync(() => _storage.GetDevices(user, ownerId));
                if (devices is null)
                    return false;
                var name = FindRosterName(devices, vendorId, productId);
                if (name is null)
                {
                    // The card vanished between the mark and this pass — its delete swapped
                    // the intent to .remove (which this snapshot predates); the next pass
                    // pushes that. Leave the mark for the swap to have consumed.
                    _logger.LogDebug(
                        "Sync pass: no card for roster add {VendorId:X4}:{ProductId:X4}; skipping.",
                        vendorId, productId);
                    continue;
                }
                endpoint = "devices/add";
                response = await _syncApi.AddDeviceAsync(token, vendorId, productId, name);
            }
            else
            {
                endpoint = "devices/remove";
                response = await _syncApi.RemoveDeviceAsync(token, vendorId, productId);
            }

            if (Aborts(response, endpoint))
                return false;

            var idempotentSuccess = SettingsSyncService.HasErrorCode(
                response, isAdd ? DeviceAlreadyExists : DeviceNotFound);
            if (SettingsSyncService.ConfirmedSuccess(response) || idempotentSuccess)
            {
                await _storage.RunOnStorageAsync(() =>
                    _storage.RemoveSyncMetaIfValue(user, ownerId, key, generation));
            }
            else if (SettingsSyncService.Succeeded(response, alreadyExistsCode: null))
            {
                _logger.LogWarning(
                    "Sync pass: {Endpoint} for {VendorId:X4}:{ProductId:X4} returned an unparseable 2xx body; leaving the mark set.",
                    endpoint, vendorId, productId);
            }
            else
            {
                // A validation rejection (NAME_TOO_LONG, INVALID_VENDOR_ID, …): the same
                // payload can never succeed, so the mark is consumed rather than retried
                // forever — the next mutation of the card re-marks it.
                _logger.LogWarning(
                    "Sync pass: {Endpoint} rejected for {VendorId:X4}:{ProductId:X4}: {Message}; dropping the mark.",
                    endpoint, vendorId, productId, response.Message ?? response.ErrorCode);
                await _storage.RunOnStorageAsync(() =>
                    _storage.RemoveSyncMetaIfValue(user, ownerId, key, generation));
            }
        }
        return true;
    }

    // ── Notifications (docs/plans/add-api-v21-notifications-sync) ────────────
    // Step order inside the step: delete_all → read_all → tombstones → dirty rows. The two
    // batch marks go first because they subsume per-row state (the mutators already dropped
    // the subsumed marks locally; pushing the batch first keeps the server walking the same
    // order). Notifications have no updated_at conflicts — the only mutable field is a
    // boolean read flag, and every call is idempotent per server id.
    private async Task PushNotificationsAsync(string ownerId, string token)
    {
        const string user = LocalStorageService.OwnerKindUser;

        // 1. delete_all. A non-abort failure LEAVES the mark: the local bell is already
        //    empty, and dropping the mark while the server keeps its rows would let the next
        //    list merge resurrect every one of them.
        var deleteAll = await _storage.RunOnStorageAsync(() =>
            (Read: true, Value: _storage.GetSyncMeta(user, ownerId, LocalStorageService.SyncMetaNotificationsDeleteAll)));
        if (!deleteAll.Read)
            return;
        if (deleteAll.Value is { } deleteAllGeneration)
        {
            var response = await _syncApi.DeleteAllNotificationsAsync(token);
            if (Aborts(response, "notifications/delete_all"))
                return;
            if (SettingsSyncService.ConfirmedSuccess(response))
            {
                await _storage.RunOnStorageAsync(() =>
                    _storage.RemoveSyncMetaIfValue(user, ownerId, LocalStorageService.SyncMetaNotificationsDeleteAll, deleteAllGeneration));
            }
            else
            {
                _logger.LogWarning(
                    "Sync pass: notifications/delete_all did not confirm ({Message}); leaving the mark set.",
                    response.Message ?? response.ErrorCode);
            }
        }

        // 2. read_all — same keep-on-failure rule: the local rows are already read, and a
        //    dropped mark would leave the server copies unread for the next machine.
        var readAll = await _storage.RunOnStorageAsync(() =>
            (Read: true, Value: _storage.GetSyncMeta(user, ownerId, LocalStorageService.SyncMetaNotificationsReadAll)));
        if (!readAll.Read)
            return;
        if (readAll.Value is { } readAllGeneration)
        {
            var response = await _syncApi.ReadAllNotificationsAsync(token);
            if (Aborts(response, "notifications/read_all"))
                return;
            if (SettingsSyncService.ConfirmedSuccess(response))
            {
                await _storage.RunOnStorageAsync(() =>
                    _storage.RemoveSyncMetaIfValue(user, ownerId, LocalStorageService.SyncMetaNotificationsReadAll, readAllGeneration));
            }
            else
            {
                _logger.LogWarning(
                    "Sync pass: notifications/read_all did not confirm ({Message}); leaving the mark set.",
                    response.Message ?? response.ErrorCode);
            }
        }

        // 3. Tombstones → notifications/delete. NOT_FOUND is the same outcome as success.
        var tombstones = await _storage.RunOnStorageAsync(() =>
            _storage.GetSyncTombstones(user, ownerId, LocalStorageService.TombstoneKindNotification));
        if (tombstones is null)
            return;
        foreach (var (uid, _, _, _) in tombstones)
        {
            if (!long.TryParse(uid, NumberStyles.Integer, CultureInfo.InvariantCulture, out var serverId))
            {
                _logger.LogWarning("Sync pass: unparseable notification tombstone uid '{Uid}'; dropping it.", uid);
                await _storage.RunOnStorageAsync(() =>
                    _storage.RemoveSyncTombstone(user, ownerId, LocalStorageService.TombstoneKindNotification, uid));
                continue;
            }
            var response = await _syncApi.DeleteNotificationAsync(token, serverId);
            if (Aborts(response, "notifications/delete"))
                return;
            if (SettingsSyncService.ConfirmedSuccess(response)
                || SettingsSyncService.HasErrorCode(response, NotificationNotFound))
            {
                await _storage.RunOnStorageAsync(() =>
                    _storage.RemoveSyncTombstone(user, ownerId, LocalStorageService.TombstoneKindNotification, uid));
            }
            else
            {
                _logger.LogWarning(
                    "Sync pass: notifications/delete rejected for server id {ServerId}: {Message}; retrying next pass.",
                    serverId, response.Message ?? response.ErrorCode);
            }
        }

        // 4. Dirty rows: never-synced entries via notifications/add (+ a read follow-up when
        //    already read locally), synced ones via read/unread per the row's flag.
        var dirty = await _storage.RunOnStorageAsync(() =>
            _storage.GetDirtyNotifications(user, ownerId));
        if (dirty is null)
            return;
        foreach (var row in dirty)
        {
            if (row.ServerId is not { } serverId)
            {
                if (!await PushNotificationAddAsync(ownerId, token, row))
                    return;
                continue;
            }

            var response = row.IsRead
                ? await _syncApi.ReadNotificationAsync(token, serverId)
                : await _syncApi.UnreadNotificationAsync(token, serverId);
            var endpoint = row.IsRead ? "notifications/read" : "notifications/unread";
            if (Aborts(response, endpoint))
                return;
            if (SettingsSyncService.HasErrorCode(response, NotificationNotFound))
            {
                // Deleted from another machine: mirror it locally. tombstone: false — the
                // server already has nothing to delete.
                _logger.LogInformation(
                    "Sync pass: {Endpoint} answered NOTIFICATION_NOT_FOUND for server id {ServerId}; removing the local row.",
                    endpoint, serverId);
                await _storage.RunOnStorageAsync(() => _storage.DeleteNotification(row.Id, tombstone: false));
            }
            else if (SettingsSyncService.ConfirmedSuccess(response))
            {
                await _storage.RunOnStorageAsync(() => _storage.ClearNotificationDirty(row.Id, row.LocalRev));
            }
            else if (SettingsSyncService.Succeeded(response, alreadyExistsCode: null))
            {
                _logger.LogWarning(
                    "Sync pass: {Endpoint} returned an unparseable 2xx body for server id {ServerId}; leaving the row dirty.",
                    endpoint, serverId);
            }
            else
            {
                _logger.LogWarning(
                    "Sync pass: {Endpoint} rejected for server id {ServerId}: {Message}; dropping the mark.",
                    endpoint, serverId, response.Message ?? response.ErrorCode);
                await _storage.RunOnStorageAsync(() => _storage.ClearNotificationDirty(row.Id, row.LocalRev));
            }
        }
    }

    // notifications/add for one never-synced row. Unlike macros/presets there is NO uid
    // idempotence — a repeated add mints a duplicate server row — so every non-abort outcome
    // that cannot bind a server id clears the dirty flag and leaves the row local-only
    // (ServerId stays NULL, nothing re-enumerates it): one unsynced entry is recoverable,
    // a duplicate per pass is not. Returns false only on a pass abort.
    private async Task<bool> PushNotificationAddAsync(string ownerId, string token, NotificationRecord row)
    {
        var title = row.Title.Length > NotificationTitleMaxLength
            ? row.Title[..NotificationTitleMaxLength]
            : row.Title;
        var type = row.Type.Length > NotificationTypeMaxLength
            ? row.Type[..NotificationTypeMaxLength]
            : row.Type;
        var response = await _syncApi.AddNotificationAsync(token, new NotificationAddRequest
        {
            Title = title,
            // message is required by the contract; a subtitle-less entry repeats the title.
            Message = row.Subtitle.Length > 0 ? row.Subtitle : title,
            Type = type,
        });
        if (Aborts(response, "notifications/add"))
            return false;

        if (SettingsSyncService.ConfirmedSuccess(response) && response.Notification?.Id is { } serverId)
        {
            var adopted = await _storage.RunOnStorageAsync(() =>
            {
                // The row can be gone by now — the user deleted it while the add was in
                // flight. The server copy just minted must go too: tombstone it so the
                // next pass's delete step takes it down, instead of a later merge
                // resurrecting the deleted entry (PR #127 review, m5).
                if (!_storage.SetNotificationServerId(row.Id, serverId))
                {
                    _storage.WriteNotificationTombstone(LocalStorageService.OwnerKindUser, ownerId, serverId);
                    return (bool?)false;
                }
                _storage.ClearNotificationDirty(row.Id, row.LocalRev);
                return (bool?)true;
            });
            if (adopted is not true)
                return true;
            if (row.IsRead)
            {
                var read = await _syncApi.ReadNotificationAsync(token, serverId);
                if (Aborts(read, "notifications/read"))
                    return false;
                if (!SettingsSyncService.ConfirmedSuccess(read))
                {
                    // Best-effort: the entry is on the server, only its read flag lagged;
                    // the next local toggle re-pushes it.
                    _logger.LogWarning(
                        "Sync pass: read follow-up after notifications/add did not confirm for server id {ServerId}.",
                        serverId);
                }
            }
            return true;
        }

        _logger.LogWarning(
            "Sync pass: notifications/add did not yield a server id for row {Id} ({Message}); the entry stays local-only.",
            row.Id, response.Message ?? response.ErrorCode);
        await _storage.RunOnStorageAsync(() => _storage.ClearNotificationDirty(row.Id, row.LocalRev));
        return true;
    }

    // The name devices/add carries: the display name of the owner's card whose settings
    // identity matches the marked pair, defensively truncated (the server names only
    // NAME_TOO_LONG, not its limit). Null when no such card remains.
    private static string? FindRosterName(List<DeviceRecord> devices, int vendorId, int productId)
    {
        foreach (var device in devices)
        {
            if (SettingsSyncService.SettingsBucketOf(device) == (vendorId, productId))
                return device.Name.Length > 255 ? device.Name[..255] : device.Name;
        }
        return null;
    }

    // "{prefix}{vid}:{pid}{suffix}" → (vid, pid); decimal, matching the key builders.
    // Shared by the device-state ("devicestate.…dirty") and roster ("deviceroster.…add/.remove")
    // enumerations; internal because the pull's roster step parses the same keys.
    internal static bool TryParseVidPidKey(string key, string prefix, string suffix, out int vendorId, out int productId)
    {
        vendorId = 0;
        productId = 0;
        if (key.Length <= prefix.Length + suffix.Length)
            return false;
        var span = key.AsSpan(prefix.Length, key.Length - prefix.Length - suffix.Length);
        var colon = span.IndexOf(':');
        if (colon <= 0)
            return false;
        return int.TryParse(span[..colon], NumberStyles.Integer, CultureInfo.InvariantCulture, out vendorId)
            && int.TryParse(span[(colon + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out productId);
    }

    // ── Shared plumbing ──────────────────────────────────────────────────────

    // Mirrors SettingsSyncService.Aborts: a network error or a token rejection dooms every
    // remaining call, so the whole pass stops; dirty state persists for the next signal.
    private bool Aborts(ApiResponse response, string endpoint)
    {
        if (response.IsNetworkError)
        {
            _logger.LogWarning("Sync pass aborted at {Endpoint}: {Message}", endpoint, response.Message);
            return true;
        }
        if (SettingsSyncService.HasErrorCode(response, AuthErrorCodes.TokenRequired)
            || SettingsSyncService.HasErrorCode(response, AuthErrorCodes.InvalidOrExpiredToken))
        {
            _logger.LogWarning("Sync pass aborted at {Endpoint}: session token rejected", endpoint);
            return true;
        }
        if (response.IsRateLimited)
        {
            // Arm the cooldown before aborting: this loop is the only automated repeat-caller
            // in the app, so it is the one that must stop hammering a limited server.
            var cooldown = SelectCooldown(response.RetryAfter, _rateLimitCooldown);
            lock (_gate)
            {
                _rateLimitedUntil = DateTimeOffset.UtcNow + cooldown;
            }
            _logger.LogWarning(
                "Sync pass aborted at {Endpoint}: rate limited, cooling down for {Cooldown}", endpoint, cooldown);
            return true;
        }
        return false;
    }

    // How long a 429 parks the pass loop. The server's Retry-After wins when it sent a usable
    // one, clamped at both ends: a missing header (or a non-positive one — ApiHttpClient
    // already floors a stale HTTP-date at zero) falls back to the contract's tightest window,
    // and an absurd value is capped so one bad header cannot stop sync for hours. Pure and
    // internal so the clamp is asserted directly instead of by waiting one out.
    internal static TimeSpan SelectCooldown(TimeSpan? retryAfter, TimeSpan fallback)
    {
        if (retryAfter is not { } wait || wait <= TimeSpan.Zero)
            return fallback;
        return wait > MaxRateLimitCooldown ? MaxRateLimitCooldown : wait;
    }

    // Sits out whatever cooldown the last pass armed, before the next pass runs — whatever
    // triggered that pass. Consuming and clearing happens under the same gate the pass-loop
    // flags use, so an Aborts on the pool thread cannot race the loop; the WAIT itself is
    // outside the gate, since holding it across a Task.Delay would block every mutator thread
    // that signals. Returns false when the coordinator was disposed — the caller must stop,
    // and _passRunning has already been cleared.
    private async Task<bool> WaitOutRateLimitCooldownAsync()
    {
        TimeSpan remaining;
        CancellationToken lifetime;
        lock (_gate)
        {
            if (_disposed)
            {
                _passRunning = false;
                return false;
            }
            if (_rateLimitedUntil is not { } until)
                return true;
            _rateLimitedUntil = null;
            remaining = until - DateTimeOffset.UtcNow;
            // Token captured under the gate, like Signal's: Dispose sets _disposed and
            // cancels+disposes the source under this same gate, so a token read here is either
            // still live or already canceled — never read off a disposed source (which would
            // throw straight into the loop and strand _passRunning).
            lifetime = _lifetimeCts.Token;
        }

        if (remaining <= TimeSpan.Zero)
            return true;

        try
        {
            await Task.Delay(remaining, lifetime);
        }
        catch (OperationCanceledException)
        {
            lock (_gate)
            {
                _passRunning = false;
            }
            return false;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                _passRunning = false;
                return false;
            }
        }
        return true;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
            // Same ordering as above: cancel first, so a cooldown wait observes the canceled
            // token synchronously and disposing the source cannot fault it.
            _lifetimeCts.Cancel();
            _lifetimeCts.Dispose();
        }
        // Outside the gate — event unsubscription needs no state, and a mutator raising the
        // event concurrently must not deadlock against it. A pass already running finishes
        // on the pool; its storage reads yield null once LocalStorageService shuts down and
        // the pass aborts, the same post-teardown contract the push and pull follow.
        _storage.SyncRelevantDataChanged -= Signal;
        _settingsSync.SyncCompleted -= Signal;
    }
}
