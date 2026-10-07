using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Models;
using ProoveGaming.App.Models.Settings;

namespace ProoveGaming.App.Services;

// Typed façade over LocalStorageService for the current owner (guest UUID or
// customer id) and physical device (vid/pid + deviceKey). Every method resolves the
// owner on the calling thread and then routes its storage body through the serialized
// storage executor (LocalStorageService.RunOnStorageAsync), so no SQLite work runs on the
// caller -- the returned task completes when the rows are read/written, and queue FIFO
// guarantees a load enqueued after a save of the same key observes it. Delegates to owner- and
// device-scoped SQL rows: RGB uses the dedicated device_rgb / device_rgb_effect
// tables; DPI presets, active stage, polling rate, and mouse settings are stored as
// keyed rows in device_settings; app preferences are 5 keyed rows in app_preferences
// (app-scoped, not per device).
//
// Teardown: once LocalStorageService.IsShutDown is set, the executor drops delegates and
// completes reads with default(T). Every Load* short-circuits to its null result up front on
// IsShutDown (same shape as the owner-null guard) so nothing is enqueued against a disposing
// connection and the "no data" answer is this repository's explicit contract rather than a
// side effect of the drop-guard. Callers already treat null as "nothing saved" (the fresh-
// install path), which is the safe behaviour during exit.
public sealed class SettingsRepository : ISettingsRepository
{
    // Key prefixes used inside device_settings for the RGB-adjacent state that used to
    // live in JSON blobs. Kept as constants so a typo can't silently split rows.
    private const string KeyDpiStagePrefix    = "dpi.stage.";
    // internal, not private: the preset-snapshot backfill in DeviceDetailsViewModel needs to name
    // the same two keys, and duplicating the literals there is how they silently drift apart.
    internal const string KeyDpiActiveStage    = "dpi.activeStage";
    internal const string KeyPollingRateHz     = "pollingRate.hz";
    private const string KeyMouseButtonDebounce = "mouse.buttonDebounce";
    private const string KeyMouseLiftOff        = "mouse.liftOffDistance";
    private const string KeyAudioOutputVolume   = "audio.outputVolume";
    private const string KeyAudioMicVolume      = "audio.micVolume";

    // app_preferences keys — one row per field of AppPreferencesPayload. internal, not
    // private: API v2.0's preferences/update carries these exact names as its payload keys,
    // so SettingsSyncService reads the rows by the same constants instead of re-spelling them.
    internal const string PrefScrollOneScreen        = "ScrollOneScreenAtATime";
    internal const string PrefScrollLines            = "ScrollLines";
    internal const string PrefDoubleClickSpeed       = "DoubleClickSpeed";
    internal const string PrefSleepTimer             = "SleepTimer";
    internal const string PrefGenericLiftOffDistance = "GenericLiftOffDistance";

    private readonly LocalStorageService _storage;
    private readonly ICurrentOwnerProvider _ownerProvider;

    // Queued-but-unfinished SAVE writes per device scope. The preset dirty check pairs a
    // synchronous read with a queued corrective recompute, and needs to know whether THIS
    // device still has staged writes in flight — the executor-wide idle probe lit SAVE PRESET
    // off unrelated queue traffic (a notification insert, another device's rows). Zero counts
    // are left in place; the map is bounded by the number of device scopes ever written.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(int Vid, int Pid, string Key), int> _pendingDeviceWrites = new();

    public SettingsRepository(LocalStorageService storage, ICurrentOwnerProvider ownerProvider)
    {
        _storage = storage;
        _ownerProvider = ownerProvider;
    }

    public bool HasPendingDeviceWrites(int vid, int pid, string deviceKey) =>
        _pendingDeviceWrites.TryGetValue((vid, pid, deviceKey ?? string.Empty), out var count) && count > 0;

    // Enqueues a device-scoped save on the storage executor with its scope counted as pending
    // until the write lands (or faults — the decrement runs either way). Every Save*Async that
    // writes rows PresetMatchesLiveState compares must route through this, or the dirty check
    // stops seeing its write as pending.
    private Task RunDeviceWrite(int vid, int pid, string deviceKey, Action work)
    {
        var scope = (vid, pid, deviceKey ?? string.Empty);
        _pendingDeviceWrites.AddOrUpdate(scope, 1, static (_, n) => n + 1);
        var write = _storage.RunOnStorageAsync(work);
        write.ContinueWith(
            _ => _pendingDeviceWrites.AddOrUpdate(scope, 0, static (_, n) => n - 1),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return write;
    }

    // ── RGB ──────────────────────────────────────────────────────────────────

    public Task<RgbDeviceSettings?> LoadRgbAsync(int vid, int pid, string deviceKey, CancellationToken ct = default)
    {
        var owner = _ownerProvider.GetCurrent();
        if (owner is null || _storage.IsShutDown) return Task.FromResult<RgbDeviceSettings?>(null);
        var (kind, id) = (owner.Value.Kind, owner.Value.Id);

        return _storage.RunOnStorageAsync(() =>
        {
            var record = _storage.GetRgbState(kind, id, vid, pid, deviceKey);
            if (record is null) return (RgbDeviceSettings?)null;

            var effects = new Dictionary<string, PerEffectSettings>(record.Effects.Count);
            foreach (var e in record.Effects)
                effects[e.EffectKey] = new PerEffectSettings(e.Hex, e.Brightness, e.Speed, e.IsRainbow);

            return new RgbDeviceSettings(record.IsEnabled, record.SelectedEffect, effects);
        });
    }

    public Task SaveRgbAsync(int vid, int pid, string deviceKey, RgbDeviceSettings state, CancellationToken ct = default)
    {
        var owner = _ownerProvider.GetCurrent();
        if (owner is null) return Task.CompletedTask;
        var (kind, id) = (owner.Value.Kind, owner.Value.Id);

        var effects = state.Effects
            .Select(kv => new RgbEffectRecord
            {
                EffectKey = kv.Key,
                Hex = kv.Value.Hex,
                Brightness = kv.Value.Brightness,
                Speed = kv.Value.Speed,
                IsRainbow = kv.Value.IsRainbow,
            })
            .ToList();

        return RunDeviceWrite(vid, pid, deviceKey, () => _storage.SaveRgbState(kind, id, vid, pid, deviceKey,
            state.IsEnabled, state.SelectedEffectKey, effects));
    }

    // ── DPI presets ──────────────────────────────────────────────────────────
    // Stored across device_settings rows keyed dpi.stage.{i}.{value|r|g|b}.

    public Task<DpiPresetsSettings?> LoadDpiPresetsAsync(int vid, int pid, string deviceKey, CancellationToken ct = default)
    {
        var owner = _ownerProvider.GetCurrent();
        if (owner is null || _storage.IsShutDown) return Task.FromResult<DpiPresetsSettings?>(null);
        var (kind, id) = (owner.Value.Kind, owner.Value.Id);

        return _storage.RunOnStorageAsync(() =>
        {
            var all = _storage.GetDeviceSettings(kind, id, vid, pid, deviceKey);
            var stages = new List<DpiPresetSetting>();
            var indices = new SortedSet<int>();
            foreach (var key in all.Keys)
            {
                if (!key.StartsWith(KeyDpiStagePrefix)) continue;
                var rest = key.AsSpan(KeyDpiStagePrefix.Length);
                var dot = rest.IndexOf('.');
                if (dot <= 0) continue;
                if (int.TryParse(rest[..dot], NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    indices.Add(i);
            }
            foreach (var i in indices)
            {
                if (!all.TryGetValue($"{KeyDpiStagePrefix}{i}.value", out var v)) continue;
                all.TryGetValue($"{KeyDpiStagePrefix}{i}.r", out var r);
                all.TryGetValue($"{KeyDpiStagePrefix}{i}.g", out var g);
                all.TryGetValue($"{KeyDpiStagePrefix}{i}.b", out var b);
                stages.Add(new DpiPresetSetting(i, (int)v, (byte)r, (byte)g, (byte)b));
            }

            return stages.Count == 0 ? null : new DpiPresetsSettings(stages);
        });
    }

    public Task SaveDpiPresetsAsync(int vid, int pid, string deviceKey, DpiPresetsSettings state, CancellationToken ct = default)
    {
        var owner = _ownerProvider.GetCurrent();
        if (owner is null) return Task.CompletedTask;
        var (kind, id) = (owner.Value.Kind, owner.Value.Id);

        // One batched transaction (one commit fsync) instead of 4xN auto-committed upserts. It
        // REPLACES the dpi.stage. family rather than upserting into it: the ladder can shrink, and
        // rows left behind by a longer one would be read back as points the user deleted.
        var values = new List<(string Key, double Value)>(state.Stages.Count * 4);
        foreach (var stage in state.Stages)
        {
            values.Add(($"{KeyDpiStagePrefix}{stage.StageIndex}.value", stage.DpiValue));
            values.Add(($"{KeyDpiStagePrefix}{stage.StageIndex}.r", stage.R));
            values.Add(($"{KeyDpiStagePrefix}{stage.StageIndex}.g", stage.G));
            values.Add(($"{KeyDpiStagePrefix}{stage.StageIndex}.b", stage.B));
        }
        return RunDeviceWrite(vid, pid, deviceKey, () => _storage.ReplaceDeviceSettingsWithPrefix(
            kind, id, vid, pid, deviceKey, KeyDpiStagePrefix, values));
    }

    // ── Active DPI stage ─────────────────────────────────────────────────────

    public Task<int?> LoadActiveDpiStageAsync(int vid, int pid, string deviceKey, CancellationToken ct = default)
    {
        var owner = _ownerProvider.GetCurrent();
        if (owner is null || _storage.IsShutDown) return Task.FromResult<int?>(null);
        var (kind, id) = (owner.Value.Kind, owner.Value.Id);

        return _storage.RunOnStorageAsync(() =>
        {
            var all = _storage.GetDeviceSettings(kind, id, vid, pid, deviceKey);
            return all.TryGetValue(KeyDpiActiveStage, out var v) ? (int?)(int)v : null;
        });
    }

    public Task SaveActiveDpiStageAsync(int vid, int pid, string deviceKey, int stageIndex, CancellationToken ct = default)
    {
        var owner = _ownerProvider.GetCurrent();
        if (owner is null) return Task.CompletedTask;
        var (kind, id) = (owner.Value.Kind, owner.Value.Id);

        return RunDeviceWrite(vid, pid, deviceKey, () =>
            _storage.SetDeviceSetting(kind, id, vid, pid, deviceKey, KeyDpiActiveStage, stageIndex));
    }

    // ── Polling rate ─────────────────────────────────────────────────────────

    public Task<int?> LoadPollingRateAsync(int vid, int pid, string deviceKey, CancellationToken ct = default)
    {
        var owner = _ownerProvider.GetCurrent();
        if (owner is null || _storage.IsShutDown) return Task.FromResult<int?>(null);
        var (kind, id) = (owner.Value.Kind, owner.Value.Id);

        return _storage.RunOnStorageAsync(() =>
        {
            var all = _storage.GetDeviceSettings(kind, id, vid, pid, deviceKey);
            return all.TryGetValue(KeyPollingRateHz, out var v) ? (int?)(int)v : null;
        });
    }

    public Task SavePollingRateAsync(int vid, int pid, string deviceKey, int hz, CancellationToken ct = default)
    {
        var owner = _ownerProvider.GetCurrent();
        if (owner is null) return Task.CompletedTask;
        var (kind, id) = (owner.Value.Kind, owner.Value.Id);

        return RunDeviceWrite(vid, pid, deviceKey, () =>
            _storage.SetDeviceSetting(kind, id, vid, pid, deviceKey, KeyPollingRateHz, hz));
    }

    // ── Mouse settings ───────────────────────────────────────────────────────

    public Task<MouseSettingsPayload?> LoadMouseSettingsAsync(int vid, int pid, string deviceKey, CancellationToken ct = default)
    {
        var owner = _ownerProvider.GetCurrent();
        if (owner is null || _storage.IsShutDown) return Task.FromResult<MouseSettingsPayload?>(null);
        var (kind, id) = (owner.Value.Kind, owner.Value.Id);

        return _storage.RunOnStorageAsync(() =>
        {
            var all = _storage.GetDeviceSettings(kind, id, vid, pid, deviceKey);
            var hasDebounce = all.TryGetValue(KeyMouseButtonDebounce, out var debounce);
            var hasLift = all.TryGetValue(KeyMouseLiftOff, out var lift);
            if (!hasDebounce && !hasLift) return (MouseSettingsPayload?)null;
            return new MouseSettingsPayload((byte)debounce, (byte)lift);
        });
    }

    public Task SaveMouseSettingsAsync(int vid, int pid, string deviceKey, MouseSettingsPayload payload, CancellationToken ct = default)
    {
        var owner = _ownerProvider.GetCurrent();
        if (owner is null) return Task.CompletedTask;
        var (kind, id) = (owner.Value.Kind, owner.Value.Id);

        return RunDeviceWrite(vid, pid, deviceKey, () => _storage.SetDeviceSettings(kind, id, vid, pid, deviceKey,
        [
            (KeyMouseButtonDebounce, payload.ButtonDebounce),
            (KeyMouseLiftOff, payload.LiftOffDistance),
        ]));
    }

    // ── Audio settings ───────────────────────────────────────────────────────

    public Task<AudioSettingsPayload?> LoadAudioSettingsAsync(int vid, int pid, string deviceKey, CancellationToken ct = default)
    {
        var owner = _ownerProvider.GetCurrent();
        if (owner is null || _storage.IsShutDown) return Task.FromResult<AudioSettingsPayload?>(null);
        var (kind, id) = (owner.Value.Kind, owner.Value.Id);

        return _storage.RunOnStorageAsync(() =>
        {
            var all = _storage.GetDeviceSettings(kind, id, vid, pid, deviceKey);
            var hasOutput = all.TryGetValue(KeyAudioOutputVolume, out var output);
            var hasMic = all.TryGetValue(KeyAudioMicVolume, out var mic);
            if (!hasOutput && !hasMic) return (AudioSettingsPayload?)null;
            // Each field is filled only when its key exists. Casting an absent value would yield 0 --
            // indistinguishable from a genuinely muted level, and the caller would apply it.
            return new AudioSettingsPayload(
                hasOutput ? (int)output : null,
                hasMic ? (int)mic : null);
        });
    }

    public Task SaveAudioSettingsAsync(int vid, int pid, string deviceKey, AudioSettingsPayload payload, CancellationToken ct = default)
    {
        var owner = _ownerProvider.GetCurrent();
        if (owner is null) return Task.CompletedTask;
        var (kind, id) = (owner.Value.Kind, owner.Value.Id);

        // Only write the fields that carry a value; a null means "this device has no such level"
        // and must not be persisted as 0, which is what the load side would then read back.
        var values = new List<(string Key, double Value)>(2);
        if (payload.OutputVolume is { } output)
            values.Add((KeyAudioOutputVolume, output));
        if (payload.MicrophoneVolume is { } mic)
            values.Add((KeyAudioMicVolume, mic));
        if (values.Count == 0) return Task.CompletedTask;

        return RunDeviceWrite(vid, pid, deviceKey, () => _storage.SetDeviceSettings(kind, id, vid, pid, deviceKey, values));
    }

    // ── App preferences ──────────────────────────────────────────────────────

    public Task<AppPreferencesPayload?> LoadAppPreferencesAsync(CancellationToken ct = default)
    {
        var owner = _ownerProvider.GetCurrent();
        if (owner is null || _storage.IsShutDown) return Task.FromResult<AppPreferencesPayload?>(null);
        var (kind, id) = (owner.Value.Kind, owner.Value.Id);

        return _storage.RunOnStorageAsync(() =>
        {
            var scrollOne = _storage.GetAppPreference(kind, id, PrefScrollOneScreen);
            var scrollLines = _storage.GetAppPreference(kind, id, PrefScrollLines);
            var doubleClick = _storage.GetAppPreference(kind, id, PrefDoubleClickSpeed);
            var sleep = _storage.GetAppPreference(kind, id, PrefSleepTimer);
            var lift = _storage.GetAppPreference(kind, id, PrefGenericLiftOffDistance);

            if (scrollOne is null && scrollLines is null && doubleClick is null && sleep is null && lift is null)
                return (AppPreferencesPayload?)null;

            return new AppPreferencesPayload(
                (scrollOne ?? 0) != 0,
                scrollLines ?? 3,
                doubleClick ?? 550,
                sleep ?? 120,
                lift ?? 0);
        });
    }

    public Task SaveAppPreferencesAsync(AppPreferencesPayload payload, CancellationToken ct = default)
    {
        var owner = _ownerProvider.GetCurrent();
        if (owner is null) return Task.CompletedTask;
        var (kind, id) = (owner.Value.Kind, owner.Value.Id);

        return _storage.RunOnStorageAsync(() =>
        {
            _storage.SetAppPreference(kind, id, PrefScrollOneScreen, payload.ScrollOneScreenAtATime ? 1 : 0);
            _storage.SetAppPreference(kind, id, PrefScrollLines, payload.ScrollLines);
            _storage.SetAppPreference(kind, id, PrefDoubleClickSpeed, payload.DoubleClickSpeed);
            _storage.SetAppPreference(kind, id, PrefSleepTimer, payload.SleepTimer);
            _storage.SetAppPreference(kind, id, PrefGenericLiftOffDistance, payload.GenericLiftOffDistance);
            // Live sync (docs/plans/add-api-v2-live-sync): flag the preferences for the next
            // coordinator pass. No-op for guest owners; raises SyncRelevantDataChanged.
            _storage.MarkSyncDirty(kind, id, LocalStorageService.SyncMetaPreferencesDirty);
        });
    }
}
