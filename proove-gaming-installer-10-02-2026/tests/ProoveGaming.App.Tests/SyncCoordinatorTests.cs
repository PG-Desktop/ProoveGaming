using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// The live-sync coordinator (docs/plans/add-api-v2-live-sync): the REAL SyncCoordinator is
// driven against a real LocalStorageService and a recording fake ISyncApiService — HTTP is
// the only boundary faked, mirroring SettingsSyncServicePullTests. The internal constructor's
// injectable debounce keeps the tests timing-insensitive (short window + generous polling on
// the CurrentPassTask seam), the same approach as the existing debounce tests.
//
// Construction starts nothing: in production the first pass arrives as the SyncCompleted the
// sign-in pull raises, which is what keeps the coordinator's tombstone deletes strictly after
// the pull's merge. So most tests seed storage first and then drive the pass under test with
// an explicit coordinator.Signal() — the same entry point the two event subscriptions use.
// Tests whose subject IS a signal (an edit, a raised SyncCompleted) drive it that way instead.
public class SyncCoordinatorTests : IDisposable
{
	private const string User = LocalStorageService.OwnerKindUser;
	private const string OwnerId = "42";
	private const int CustomerId = 42;
	private const int Vid = 0x04D9;
	private const int Pid = 0xA23F;
	private const string NoKey = "";
	// A real unit's Container ID, the key every UI apply path passes (DeviceDetailsViewModel
	// .Presets) — as opposed to NoKey, which is what the sign-in pull writes for the type.
	private const string UnitKey = "unit-a";

	private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(100);
	// Several debounce windows — long enough that a restore-triggered pass (a bug) would
	// have fired and called the API before the "nothing further happened" assert runs.
	private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(500);
	// The 429 cooldown, injected like the debounce so the rate-limit tests never wait out the
	// production 60 s. Correctness does not depend on its length: CurrentPassTask completes
	// only after the loop has finished waiting AND run its single retry, so the tests observe
	// a settled state rather than racing a timer.
	private static readonly TimeSpan RateLimitCooldown = TimeSpan.FromMilliseconds(60);

	private readonly LocalStorageService _storage = new("Data Source=:memory:");
	private readonly RecordingSyncApi _api = new();
	private readonly FakeSettingsSyncService _settingsSync = new();
	private readonly CapturingLogger<SyncCoordinator> _log = new();

	public void Dispose() => _storage.Dispose();

	private SyncCoordinator NewCoordinator(string token = "token-1", TimeSpan? cooldown = null) =>
		new(_api, _storage, new UserProfile { CustomerId = CustomerId, SessionToken = token },
			new PresetConfigurationBuilder(_storage), _settingsSync, _log, Debounce,
			cooldown ?? RateLimitCooldown);

	// ── Seeding ──────────────────────────────────────────────────────────────
	// Seeding happens BEFORE the coordinator exists, so the events the mutators raise have
	// no subscriber and cannot start a premature pass.

	private MacroRecord SeedUserMacro(string name = "M1", string? serverUpdatedAt = null)
	{
		var macro = new MacroRecord
		{
			Name = name,
			RepeatCount = 1,
			Events = { new MacroEventRecord { KeyCode = 0x04, IsPress = true, DelayMs = 5 } },
		};
		_storage.SaveMacro(User, OwnerId, Vid, Pid, NoKey, macro);
		if (serverUpdatedAt is not null)
			_storage.SetMacroServerUpdatedAt(macro.Id, serverUpdatedAt);
		return macro;
	}

	private PresetRecord SeedUserPreset(string name = "P1", string? serverUpdatedAt = null)
	{
		var preset = new PresetRecord { Name = name };
		_storage.InsertPreset(User, OwnerId, Vid, Pid, preset);
		if (serverUpdatedAt is not null)
			_storage.SetPresetServerUpdatedAt(preset.Id, serverUpdatedAt);
		return preset;
	}

	// A synced preset the pass owes nothing for — used by device-state tests so the preset
	// step stays silent and the endpoint log isolates the devices/* traffic.
	private PresetRecord SeedCleanSyncedPreset(string name = "P-CLEAN")
	{
		var preset = SeedUserPreset(name, "p-ts");
		_storage.ClearPresetDirty(preset.Id, 1);
		return preset;
	}

	// ── Awaiting the background pass ─────────────────────────────────────────

	private static async Task WaitUntilAsync(Func<bool> condition, string what)
	{
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (!condition())
		{
			if (DateTime.UtcNow > deadline)
				throw new TimeoutException($"Timed out waiting for {what}.");
			await Task.Delay(10);
		}
	}

	// Polls the CurrentPassTask seam until a pass (distinct from `previous`, when given) has
	// started, then awaits it — by contract it completes only after any queued follow-up
	// pass and never faults.
	private static async Task<Task> WaitForPassAsync(SyncCoordinator coordinator, Task? previous = null)
	{
		Task? pass = null;
		await WaitUntilAsync(() =>
		{
			pass = coordinator.CurrentPassTask;
			return pass is not null && !ReferenceEquals(pass, previous);
		}, "a sync pass to start");
		await pass!;
		return pass!;
	}

	// ── Response helpers ─────────────────────────────────────────────────────

	private static MacroCreateResponse MacroOk(string uid, string updatedAt) => new()
	{
		Success = true, StatusCode = 200,
		Macro = new SyncRecordDto { Uid = uid, UpdatedAt = updatedAt },
	};

	private static PresetCreateResponse PresetOk(string uid, string updatedAt) => new()
	{
		Success = true, StatusCode = 200,
		Preset = new SyncRecordDto { Uid = uid, UpdatedAt = updatedAt },
	};

	private static T Err<T>(string code, int status = 409) where T : ApiResponse, new() =>
		new() { Success = false, StatusCode = status, ErrorCode = code };

	// HTTP 429. retryAfter null is the header-less case, where the coordinator falls back to
	// its injected cooldown.
	private static T RateLimited<T>(TimeSpan? retryAfter = null) where T : ApiResponse, new() =>
		new() { Success = false, StatusCode = 429, RetryAfter = retryAfter };

	// A 2xx whose body did not bind — what the PHP router answers for a route it does not
	// have (ApiHttpClient.ReadResponseAsync), i.e. "the server may well have done nothing".
	private static T Unparseable2xx<T>() where T : ApiResponse, new() =>
		new() { Success = false, StatusCode = 200, MalformedPayload = true };

	// ── Debounce ─────────────────────────────────────────────────────────────

	// A burst of edits collapses into ONE pass and one create call — carrying the LAST
	// content, because the pass reads storage when it runs, not when it was signalled.
	[Fact]
	public async Task EditBurst_CollapsesIntoOneCreateCarryingTheLastContent()
	{
		_api.MacroCreateResponse = req => Task.FromResult(MacroOk(req.Uid, "srv-1"));
		var macro = SeedUserMacro("EDIT-0");

		using var coordinator = NewCoordinator();
		for (var i = 1; i <= 4; i++)
		{
			macro.Name = $"EDIT-{i}";
			_storage.SaveMacro(User, OwnerId, Vid, Pid, NoKey, macro);
		}

		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "macros/create" }, _api.EndpointsSnapshot());
		Assert.Equal("EDIT-4", Assert.Single(_api.MacroCreateRequests).Name);
		Assert.Empty(_storage.GetDirtyMacros(User, OwnerId));
		Assert.Equal("srv-1", Assert.Single(_storage.GetMacros(User, OwnerId, Vid, Pid, NoKey)).ServerUpdatedAt);
	}

	// ── Single-flight + rev guard (acceptance: edit-during-push) ─────────────

	// While a pass is parked inside its first HTTP call, an edit's signal must not start a
	// second concurrent pass — it requests one follow-up. The stale clear then misses (the
	// edit bumped local_rev), and the follow-up pushes the NEWER content as an update using
	// the updated_at the create just stored.
	[Fact]
	public async Task EditWhilePushInFlight_SingleFlightFollowUpResendsTheNewerContent()
	{
		using var gate = new SemaphoreSlim(0);
		var firstCallParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var creates = 0;
		_api.MacroCreateResponse = async req =>
		{
			if (Interlocked.Increment(ref creates) == 1)
			{
				firstCallParked.SetResult();
				await gate.WaitAsync();
			}
			return MacroOk(req.Uid, "srv-1");
		};
		_api.MacroUpdateResponse = req => Task.FromResult(MacroOk(req.Uid, "srv-2"));
		var macro = SeedUserMacro("OLD");

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await firstCallParked.Task;

		// The pass has already read "OLD" and is parked on macros/create — edit now.
		macro.Name = "NEW";
		_storage.SaveMacro(User, OwnerId, Vid, Pid, NoKey, macro);
		// Let the edit's debounce elapse so its signal lands while the pass is running.
		await Task.Delay(Settle);

		// Single-flight: nothing else was issued while the first call is parked.
		Assert.Equal(new[] { "macros/create" }, _api.EndpointsSnapshot());

		gate.Release();
		await WaitUntilAsync(() => _api.EndpointsSnapshot().Contains("macros/update"), "the follow-up pass's update");
		await WaitUntilAsync(() => coordinator.CurrentPassTask is { IsCompleted: true }, "the pass loop to finish");

		Assert.Equal(new[] { "macros/create", "macros/update" }, _api.EndpointsSnapshot());
		// The stale create carried the pre-edit content; the follow-up update carried the
		// newer content and the updated_at echoed by the create.
		Assert.Equal("OLD", Assert.Single(_api.MacroCreateRequests).Name);
		var update = Assert.Single(_api.MacroUpdateRequests);
		Assert.Equal("NEW", update.Name);
		Assert.Equal("srv-1", update.UpdatedAt);
		Assert.Empty(_storage.GetDirtyMacros(User, OwnerId));
		Assert.Equal("srv-2", Assert.Single(_storage.GetMacros(User, OwnerId, Vid, Pid, NoKey)).ServerUpdatedAt);
	}

	// ── Full pass ordering ───────────────────────────────────────────────────

	[Fact]
	public async Task FullPass_PushesInPlanOrderWithStoredTimestampsAndWriteBacks()
	{
		var m1 = SeedUserMacro("M-DIRTY", "m1-ts");
		var m2 = SeedUserMacro("M-DELETED", "m2-ts");
		_storage.DeleteMacro(m2.Id);
		var p1 = SeedUserPreset("P-DIRTY", "p1-ts");
		var p2 = SeedUserPreset("P-DELETED", "p2-ts");
		_storage.DeletePreset(p2.Id);
		_storage.SetAppPreference(User, OwnerId, SettingsRepository.PrefScrollLines, 5);
		_storage.SetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaPreferencesDirty, "1");
		_storage.SetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaPreferencesUpdatedAt, "pref-ts-0");
		_storage.SetAppliedPreset(User, OwnerId, Vid, Pid, NoKey, p1.Id);
		_storage.SetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaDeviceStateUpdatedAt(Vid, Pid), "dev-ts-0");

		_api.MacroUpdateResponse = req => Task.FromResult(MacroOk(req.Uid, "m1-ts-new"));
		_api.PresetUpdateResponse = req => Task.FromResult(PresetOk(req.Uid, "p1-ts-new"));
		_api.PreferencesUpdateResponse = () => Task.FromResult(new PreferencesUpdateResponse
		{
			Success = true, StatusCode = 200, UpdatedAt = "pref-ts-1",
		});
		_api.DeviceStateUpdateResponse = () => Task.FromResult(new DeviceStateUpdateResponse
		{
			Success = true, StatusCode = 200, UpdatedAt = "dev-ts-1",
		});

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		// The plan's order: macro content, macro tombstones, preset content, preset
		// tombstones, preferences, device state.
		Assert.Equal(
			new[] { "macros/update", "macros/delete", "presets/update", "presets/delete", "preferences/update", "devices/update" },
			_api.EndpointsSnapshot());

		// Every call carried its stored updated_at.
		var macroUpdate = Assert.Single(_api.MacroUpdateRequests);
		Assert.Equal((m1.Uid, "m1-ts"), (macroUpdate.Uid, macroUpdate.UpdatedAt));
		Assert.Equal((m2.Uid, "m2-ts"), Assert.Single(_api.MacroDeleteRequests));
		var presetUpdate = Assert.Single(_api.PresetUpdateRequests);
		Assert.Equal((p1.Uid, "p1-ts", "P-DIRTY"), (presetUpdate.Uid, presetUpdate.UpdatedAt, presetUpdate.Name));
		Assert.NotNull(presetUpdate.Configuration);
		Assert.Equal((p2.Uid, "p2-ts"), Assert.Single(_api.PresetDeleteRequests));
		var preferences = Assert.Single(_api.PreferencesUpdateCalls);
		Assert.Equal("pref-ts-0", preferences.UpdatedAt);
		Assert.Equal(5d, (double)preferences.Preferences[SettingsRepository.PrefScrollLines]);
		Assert.Equal((Vid, Pid, p1.Uid, "dev-ts-0"), Assert.Single(_api.DeviceStateUpdateCalls));

		// Write-backs: rows clean with refreshed timestamps, tombstones gone, sync_meta
		// dirty keys consumed and updated_at keys refreshed.
		Assert.Empty(_storage.GetDirtyMacros(User, OwnerId));
		Assert.Empty(_storage.GetDirtyPresets(User, OwnerId));
		Assert.Equal("m1-ts-new", Assert.Single(_storage.GetMacros(User, OwnerId, Vid, Pid, NoKey)).ServerUpdatedAt);
		Assert.Equal("p1-ts-new", Assert.Single(_storage.GetPresets(User, OwnerId, Vid, Pid)).ServerUpdatedAt);
		Assert.Empty(_storage.GetSyncTombstones(User, OwnerId, LocalStorageService.TombstoneKindMacro));
		Assert.Empty(_storage.GetSyncTombstones(User, OwnerId, LocalStorageService.TombstoneKindPreset));
		Assert.Null(_storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaPreferencesDirty));
		Assert.Equal("pref-ts-1", _storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaPreferencesUpdatedAt));
		Assert.Null(_storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaDeviceStateDirty(Vid, Pid)));
		Assert.Equal("dev-ts-1", _storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaDeviceStateUpdatedAt(Vid, Pid)));

		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
	}

	// ── Preset configuration payload (docs/plans/add-extended-keys-api-sync) ─

	// The client half of "extended keys reach the server": a preset whose snapshot holds them pushes
	// them inside presets/update's configuration blob, which the server stores opaquely. The pair
	// mode's hold_key / tap_key travel as JSON null; the single-key mode keeps its two objects.
	[Fact]
	public async Task PresetUpdate_SnapshotWithExtendedKeys_CarriesThemInTheConfiguration()
	{
		var preset = SeedUserPreset("P-KEYS", "p-ts");
		_storage.ReplaceDeviceExtendedKeys(User, OwnerId, Vid, Pid, UnitKey,
			[ExtendedKeyRecord.RapidSnap(31, 33),
			 ExtendedKeyRecord.ModTap(30, 150, new ExtendedKeyCodeRecord(0x10, 0x01, 0x00), new ExtendedKeyCodeRecord(0x10, 0x00, 0x29))]);
		_storage.SavePresetSnapshot(preset.Id, User, OwnerId, Vid, Pid, UnitKey);
		_api.PresetUpdateResponse = req => Task.FromResult(PresetOk(req.Uid, "p-ts-new"));

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		var request = Assert.Single(_api.PresetUpdateRequests);
		// The DTO holds the builder's anonymous object; the wire shape is what matters.
		var configuration = JsonSerializer.SerializeToElement(request.Configuration);
		var items = configuration.GetProperty("extended_keys").EnumerateArray().ToList();
		Assert.Equal(2, items.Count);

		var pair = items.Single(i => i.GetProperty("mode").GetInt32() == ExtendedKeyMode.RapidSnap);
		Assert.Equal(31, pair.GetProperty("slot1").GetInt32());
		Assert.Equal(33, pair.GetProperty("slot2").GetInt32());
		Assert.Equal(JsonValueKind.Null, pair.GetProperty("hold_key").ValueKind);
		Assert.Equal(JsonValueKind.Null, pair.GetProperty("tap_key").ValueKind);

		var modTap = items.Single(i => i.GetProperty("mode").GetInt32() == ExtendedKeyMode.ModTap);
		Assert.Equal(30, modTap.GetProperty("slot1").GetInt32());
		Assert.Equal(150, modTap.GetProperty("time_ms").GetInt32());
		Assert.Equal(0x01, modTap.GetProperty("hold_key").GetProperty("code1").GetInt32());
		Assert.Equal(0x29, modTap.GetProperty("tap_key").GetProperty("code2").GetInt32());

		Assert.Empty(_storage.GetDirtyPresets(User, OwnerId));
	}

	// ── Create vs update selection ───────────────────────────────────────────

	[Fact]
	public async Task Pass_SelectsCreateForUnsyncedAndUpdateForSyncedRows()
	{
		var unsynced = SeedUserMacro("M-NEW");
		var synced = SeedUserMacro("M-SYNCED", "s-ts");

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "macros/create", "macros/update" }, _api.EndpointsSnapshot());
		Assert.Equal(unsynced.Uid, Assert.Single(_api.MacroCreateRequests).Uid);
		var update = Assert.Single(_api.MacroUpdateRequests);
		Assert.Equal((synced.Uid, "s-ts"), (update.Uid, update.UpdatedAt));
	}

	// ── ALREADY_EXISTS → get → update fall-through ───────────────────────────

	// An interrupted earlier push created the record: the create's ALREADY_EXISTS answer
	// converges by fetching the server's updated_at and updating — never by giving up.
	[Fact]
	public async Task MacroCreate_AlreadyExists_FallsThroughToGetThenUpdate()
	{
		var macro = SeedUserMacro("M1");
		_api.MacroCreateResponse = _ => Task.FromResult(Err<MacroCreateResponse>("MACRO_UID_ALREADY_EXISTS"));
		_api.MacroGetResponse = uid => Task.FromResult(new MacroGetResponse
		{
			Success = true, StatusCode = 200,
			Macro = new MacroPullDto { Uid = uid, UpdatedAt = "srv-cur" },
		});
		_api.MacroUpdateResponse = req => Task.FromResult(MacroOk(req.Uid, "srv-new"));

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "macros/create", "macros/get", "macros/update" }, _api.EndpointsSnapshot());
		Assert.Equal("srv-cur", Assert.Single(_api.MacroUpdateRequests).UpdatedAt);
		Assert.Empty(_storage.GetDirtyMacros(User, OwnerId));
		Assert.Equal("srv-new", Assert.Single(_storage.GetMacros(User, OwnerId, Vid, Pid, NoKey)).ServerUpdatedAt);
	}

	// ── Conflict retry paths (client wins, ONE refresh-retry per item) ───────

	[Fact]
	public async Task MacroUpdate_Conflict_RefreshesViaGetAndResendsExactlyOnce()
	{
		var macro = SeedUserMacro("M1", "stale-ts");
		var updates = 0;
		_api.MacroUpdateResponse = req => Task.FromResult(++updates == 1
			? Err<MacroCreateResponse>("MACRO_CONFLICT")
			: MacroOk(req.Uid, "srv-2"));
		_api.MacroGetResponse = uid => Task.FromResult(new MacroGetResponse
		{
			Success = true, StatusCode = 200,
			Macro = new MacroPullDto { Uid = uid, UpdatedAt = "fresh-ts" },
		});

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "macros/update", "macros/get", "macros/update" }, _api.EndpointsSnapshot());
		Assert.Equal(new[] { "stale-ts", "fresh-ts" }, _api.MacroUpdateRequests.Select(r => r.UpdatedAt).ToArray());
		Assert.Empty(_storage.GetDirtyMacros(User, OwnerId));
		Assert.Equal("srv-2", Assert.Single(_storage.GetMacros(User, OwnerId, Vid, Pid, NoKey)).ServerUpdatedAt);
	}

	[Fact]
	public async Task MacroUpdate_ConflictTwice_GivesUpLeavingTheRowDirty()
	{
		SeedUserMacro("M1", "stale-ts");
		_api.MacroUpdateResponse = _ => Task.FromResult(Err<MacroCreateResponse>("MACRO_CONFLICT"));
		_api.MacroGetResponse = uid => Task.FromResult(new MacroGetResponse
		{
			Success = true, StatusCode = 200,
			Macro = new MacroPullDto { Uid = uid, UpdatedAt = "fresh-ts" },
		});

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		// Exactly one refresh-retry — never a third attempt in the same pass.
		Assert.Equal(new[] { "macros/update", "macros/get", "macros/update" }, _api.EndpointsSnapshot());
		Assert.True(Assert.Single(_storage.GetDirtyMacros(User, OwnerId)).Macro.IsDirty);
		Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("conflicted twice"));
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Error);
	}

	[Fact]
	public async Task PresetUpdate_ConflictTwice_GivesUpLeavingTheRowDirty()
	{
		SeedUserPreset("P1", "stale-ts");
		_api.PresetUpdateResponse = _ => Task.FromResult(Err<PresetCreateResponse>("PRESET_CONFLICT"));
		_api.PresetGetResponse = uid => Task.FromResult(new PresetGetResponse
		{
			Success = true, StatusCode = 200,
			Preset = new PresetPullDto { Uid = uid, UpdatedAt = "fresh-ts" },
		});

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "presets/update", "presets/get", "presets/update" }, _api.EndpointsSnapshot());
		Assert.Equal(new[] { "stale-ts", "fresh-ts" }, _api.PresetUpdateRequests.Select(r => r.UpdatedAt).ToArray());
		Assert.True(Assert.Single(_storage.GetDirtyPresets(User, OwnerId)).Preset.IsDirty);
		Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("conflicted twice"));
	}

	[Fact]
	public async Task PreferencesUpdate_Conflict_RefreshesResendsOnceAndStoresTheNewTimestamp()
	{
		_storage.SetAppPreference(User, OwnerId, SettingsRepository.PrefScrollLines, 5);
		_storage.SetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaPreferencesDirty, "1");
		_storage.SetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaPreferencesUpdatedAt, "p-0");
		var updates = 0;
		_api.PreferencesUpdateResponse = () => Task.FromResult(++updates == 1
			? Err<PreferencesUpdateResponse>("PREFERENCES_CONFLICT")
			: new PreferencesUpdateResponse { Success = true, StatusCode = 200, UpdatedAt = "p-2" });
		_api.PreferencesGetResponse = () => Task.FromResult(new PreferencesGetResponse
		{
			Success = true, StatusCode = 200, UpdatedAt = "p-fresh",
		});

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "preferences/update", "preferences/get", "preferences/update" }, _api.EndpointsSnapshot());
		Assert.Equal(new[] { "p-0", "p-fresh" }, _api.PreferencesUpdateCalls.Select(c => c.UpdatedAt).ToArray());
		Assert.Null(_storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaPreferencesDirty));
		Assert.Equal("p-2", _storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaPreferencesUpdatedAt));
	}

	[Fact]
	public async Task PreferencesUpdate_ConflictTwice_GivesUpLeavingTheDirtyFlagSet()
	{
		_storage.SetAppPreference(User, OwnerId, SettingsRepository.PrefScrollLines, 5);
		_storage.SetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaPreferencesDirty, "1");
		_api.PreferencesUpdateResponse = () => Task.FromResult(Err<PreferencesUpdateResponse>("PREFERENCES_CONFLICT"));
		_api.PreferencesGetResponse = () => Task.FromResult(new PreferencesGetResponse
		{
			Success = true, StatusCode = 200, UpdatedAt = "p-fresh",
		});

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "preferences/update", "preferences/get", "preferences/update" }, _api.EndpointsSnapshot());
		Assert.Equal("1", _storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaPreferencesDirty));
		Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("conflicted twice"));

		// Failure never touched the flag (clear-after-success), and nothing raised a fresh
		// signal: no retry storm.
		await Task.Delay(Settle);
		Assert.Equal(3, _api.EndpointsSnapshot().Length);
	}

	[Fact]
	public async Task DeviceStateUpdate_ConflictTwice_GivesUpLeavingTheDirtyKeySet()
	{
		var preset = SeedCleanSyncedPreset();
		_storage.SetAppliedPreset(User, OwnerId, Vid, Pid, NoKey, preset.Id);
		_storage.SetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaDeviceStateUpdatedAt(Vid, Pid), "d-0");
		_api.DeviceStateUpdateResponse = () => Task.FromResult(Err<DeviceStateUpdateResponse>("DEVICE_STATE_CONFLICT"));
		_api.DeviceStateGetResponse = (_, _) => Task.FromResult(new DeviceStateGetResponse
		{
			Success = true, StatusCode = 200, UpdatedAt = "d-fresh",
		});

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "devices/update", "devices/get", "devices/update" }, _api.EndpointsSnapshot());
		Assert.Equal(new[] { "d-0", "d-fresh" }, _api.DeviceStateUpdateCalls.Select(c => c.UpdatedAt).ToArray());
		// The mark (a generation value) survived the failed push untouched.
		Assert.NotNull(_storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaDeviceStateDirty(Vid, Pid)));
		Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("conflicted twice"));

		await Task.Delay(Settle);
		Assert.Equal(3, _api.EndpointsSnapshot().Length);
	}

	// ── sync_meta generation-guarded clear-after-success ─────────────────────

	// The dirty flag persists across the push and is cleared only after success (guarded by
	// the generation value read at pass start); a plain rejection leaves it set — and raises
	// no event, so the failure cannot loop pass → fail → signal.
	[Fact]
	public async Task PreferencesUpdate_Rejected_LeavesTheDirtyFlagSetWithoutSignalingANewPass()
	{
		_storage.SetAppPreference(User, OwnerId, SettingsRepository.PrefScrollLines, 5);
		_storage.SetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaPreferencesDirty, "1");
		_api.PreferencesUpdateResponse = () => Task.FromResult(Err<PreferencesUpdateResponse>("VALIDATION_ERROR", 422));

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "preferences/update" }, _api.EndpointsSnapshot());
		Assert.Equal("1", _storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaPreferencesDirty));
		Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("preferences/update rejected"));

		await Task.Delay(Settle);
		Assert.Single(_api.EndpointsSnapshot());
	}

	// The guard itself: a save landing while preferences/update is in flight writes a NEWER
	// generation into the flag, so the clear-after-success (guarded by the value read at pass
	// start) must miss and leave the newer mark owed to the next pass. The re-mark goes
	// through SetSyncMeta (no event) to isolate the guard from the follow-up pass a real
	// MarkSyncDirty signal would also start.
	[Fact]
	public async Task PreferencesReMarkedDuringPush_StaleClearMissesAndTheNewerMarkSurvives()
	{
		_storage.SetAppPreference(User, OwnerId, SettingsRepository.PrefScrollLines, 5);
		_storage.SetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaPreferencesDirty, "gen-old");
		_api.PreferencesUpdateResponse = () =>
		{
			// Runs while the pass is inside the HTTP call — after it read "gen-old",
			// before its clear.
			_storage.SetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaPreferencesDirty, "gen-new");
			return Task.FromResult(new PreferencesUpdateResponse
			{
				Success = true, StatusCode = 200, UpdatedAt = "p-1",
			});
		};

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "preferences/update" }, _api.EndpointsSnapshot());
		// The push itself succeeded and stored its timestamp…
		Assert.Equal("p-1", _storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaPreferencesUpdatedAt));
		// …but the stale clear missed: the newer mark still awaits the next pass.
		Assert.Equal("gen-new", _storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaPreferencesDirty));
	}

	// ── Tombstone lifecycle ──────────────────────────────────────────────────

	// *_NOT_FOUND is the same outcome as a successful delete: the record is gone either way,
	// so the tombstone is consumed without noise. This is also what disposes of a tombstone
	// whose record another machine already deleted.
	[Fact]
	public async Task MacroTombstone_NotFound_RemovesTheTombstoneSilently()
	{
		var macro = SeedUserMacro("M1", "m-ts");
		_storage.DeleteMacro(macro.Id);
		_api.MacroDeleteResponse = (_, _) => Task.FromResult(Err<ApiResponse>("MACRO_NOT_FOUND", 404));

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "macros/delete" }, _api.EndpointsSnapshot());
		Assert.Empty(_storage.GetSyncTombstones(User, OwnerId, LocalStorageService.TombstoneKindMacro));
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
	}

	// The server keeps its default (contract §16.5); retrying forever would never succeed,
	// so the tombstone is dropped with a warning and local state stays deleted.
	[Fact]
	public async Task PresetTombstone_DefaultPresetCannotBeDeleted_DropsTheTombstoneWithAWarning()
	{
		var preset = SeedUserPreset("P1", "p-ts");
		_storage.DeletePreset(preset.Id);
		_api.PresetDeleteResponse = (_, _) => Task.FromResult(Err<ApiResponse>("DEFAULT_PRESET_CANNOT_BE_DELETED"));

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "presets/delete" }, _api.EndpointsSnapshot());
		Assert.Empty(_storage.GetSyncTombstones(User, OwnerId, LocalStorageService.TombstoneKindPreset));
		Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("dropping the tombstone"));
	}

	[Fact]
	public async Task MacroTombstone_Conflict_RetriesTheDeleteOnceWithTheRefreshedTimestamp()
	{
		var macro = SeedUserMacro("M1", "t-0");
		_storage.DeleteMacro(macro.Id);
		var deletes = 0;
		_api.MacroDeleteResponse = (_, _) => Task.FromResult(++deletes == 1
			? Err<ApiResponse>("MACRO_CONFLICT")
			: new ApiResponse { Success = true, StatusCode = 200 });
		_api.MacroGetResponse = uid => Task.FromResult(new MacroGetResponse
		{
			Success = true, StatusCode = 200,
			Macro = new MacroPullDto { Uid = uid, UpdatedAt = "t-fresh" },
		});

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "macros/delete", "macros/get", "macros/delete" }, _api.EndpointsSnapshot());
		Assert.Equal(new[] { "t-0", "t-fresh" }, _api.MacroDeleteRequests.Select(r => r.UpdatedAt).ToArray());
		Assert.Empty(_storage.GetSyncTombstones(User, OwnerId, LocalStorageService.TombstoneKindMacro));
	}

	// ── Abort paths ──────────────────────────────────────────────────────────

	// A network error dooms every remaining call: the pass stops at the failed endpoint and
	// ALL dirty state persists for the next signal — nothing is half-cleared.
	[Fact]
	public async Task NetworkError_AbortsThePassPreservingAllDirtyState()
	{
		SeedUserMacro("M1", "m-ts");
		SeedUserPreset("P1", "p-ts");
		_api.MacroUpdateResponse = _ => Task.FromResult(new MacroCreateResponse { IsNetworkError = true });

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "macros/update" }, _api.EndpointsSnapshot());
		Assert.True(Assert.Single(_storage.GetDirtyMacros(User, OwnerId)).Macro.IsDirty);
		Assert.True(Assert.Single(_storage.GetDirtyPresets(User, OwnerId)).Preset.IsDirty);
		Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("aborted at macros/update"));
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Error);
	}

	// docs/plans/add-global-token-expiry: a token-rejection envelope aborts the same way — a
	// doomed retry with the same dead token would only re-trip the app-wide rejection latch.
	[Fact]
	public async Task TokenRejected_AbortsThePassPreservingDirtyState()
	{
		SeedUserMacro("M1", "m-ts");
		_api.MacroUpdateResponse = _ => Task.FromResult(Err<MacroCreateResponse>(AuthErrorCodes.InvalidOrExpiredToken, 401));

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "macros/update" }, _api.EndpointsSnapshot());
		Assert.True(Assert.Single(_storage.GetDirtyMacros(User, OwnerId)).Macro.IsDirty);
		Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("session token rejected"));
	}

	// ── Rate limiting (429) ────────────────────────────────────────

	// docs/plans/improve-api-error-surfacing, criteria 9 and 10. A 429 aborts the pass like a
	// network error, but this loop is the app's only automated repeat-caller, so it also sits
	// out a cooldown instead of retrying straight into the same limit. The retry is
	// single-shot: a server still limiting afterwards stops the loop rather than spinning.
	//
	// The pass task completes only after the cooldown wait and the retry, so the assertions
	// below observe a finished loop — no polling against a timer.
	[Fact]
	public async Task RateLimited_AbortsThePassPreservingDirtyStateAndRetriesExactlyOnce()
	{
		SeedUserMacro("M1", "m-ts");
		SeedUserPreset("P1", "p-ts");
		_api.MacroUpdateResponse = _ => Task.FromResult(RateLimited<MacroCreateResponse>());

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		// One aborted pass, then exactly one post-cooldown retry — and nothing after it, because
		// the retry was limited again. The preset step is never reached either time: a 429
		// dooms every remaining call in the pass, exactly like a network error.
		Assert.Equal(new[] { "macros/update", "macros/update" }, _api.EndpointsSnapshot());
		Assert.True(Assert.Single(_storage.GetDirtyMacros(User, OwnerId)).Macro.IsDirty);
		Assert.True(Assert.Single(_storage.GetDirtyPresets(User, OwnerId)).Preset.IsDirty);
		Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("rate limited"));
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Error);

		// Criterion 10's "no unbounded loop": several more cooldowns' worth of time passes and
		// the API is not touched again — the coordinator is waiting for the next signal.
		await Task.Delay(Settle);
		Assert.Equal(2, _api.EndpointsSnapshot().Length);
	}

	// The other half of criterion 10: when the limit has lifted by the time the cooldown
	// elapses, the single retry converges and the dirty state clears — a rate-limited server
	// must not strand the edit until the user happens to change something else.
	[Fact]
	public async Task RateLimited_CooldownElapses_TheSingleRetryPassConverges()
	{
		var macro = SeedUserMacro("M1", "m-ts");
		var attempts = 0;
		_api.MacroUpdateResponse = req => Task.FromResult(
			Interlocked.Increment(ref attempts) == 1
				? RateLimited<MacroCreateResponse>()
				: MacroOk(req.Uid, "m-ts-new"));

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "macros/update", "macros/update" }, _api.EndpointsSnapshot());
		Assert.Empty(_storage.GetDirtyMacros(User, OwnerId));
	}

	// The server's Retry-After wins over the client's default when it sent one. Asserted
	// through the log rather than by timing the wait: the injected default here is two orders
	// of magnitude larger than the header, so the logged cooldown says unambiguously which one
	// the coordinator armed.
	[Fact]
	public async Task RateLimited_RetryAfterFromTheServer_IsPreferredOverTheDefaultCooldown()
	{
		var retryAfter = TimeSpan.FromMilliseconds(150);
		SeedUserMacro("M1", "m-ts");
		_api.MacroUpdateResponse = _ => Task.FromResult(RateLimited<MacroCreateResponse>(retryAfter));

		using var coordinator = NewCoordinator(cooldown: TimeSpan.FromSeconds(30));
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Contains(_log.Entries, e =>
			e.Level == LogLevel.Warning && e.Message.Contains($"cooling down for {retryAfter}"));
	}

	// A 429 reaching a LATER step arms the cooldown the same way — the arm lives in the shared
	// Aborts predicate, not at one call site, so every endpoint in the pass is covered.
	[Fact]
	public async Task RateLimited_AtALaterStep_AbortsAndRetriesTheWholePass()
	{
		SeedUserPreset("P1", "p-ts");
		_api.PresetUpdateResponse = _ => Task.FromResult(RateLimited<PresetCreateResponse>());

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "presets/update", "presets/update" }, _api.EndpointsSnapshot());
		Assert.True(Assert.Single(_storage.GetDirtyPresets(User, OwnerId)).Preset.IsDirty);
		Assert.Contains(_log.Entries, e =>
			e.Level == LogLevel.Warning && e.Message.Contains("aborted at presets/update"));
	}

	// REGRESSION, review R1 B1. The cooldown used to be armed, read, and then thrown away once
	// the single automatic retry had been spent: TakeRateLimitCooldown cleared _rateLimitedUntil
	// unconditionally, but the caller only honoured the value when !cooldownRetryUsed. Control
	// then fell into the follow-up branch, which started a whole pass with ZERO delay against a
	// server that had just answered 429 — breaking acceptance criterion 9 ("no further API call
	// is issued until the cooldown elapses") in exactly the situation that matters, a user
	// editing while the limit is in force.
	//
	// The signal is raised from INSIDE the rate-limited handler, so it lands while the pass is
	// running and is still pending when the loop reaches its decision. The assertion is on the
	// GAP between consecutive calls rather than on a count: before the fix the third call
	// followed the second immediately, so the gap is what actually distinguishes fixed from
	// broken. Margin is generous (a quarter of the cooldown) because the failing value is ~0 ms
	// against a 400 ms floor — there is no realistic scheduling jitter between those.
	[Fact]
	public async Task RateLimited_SignalDuringThePass_StillWaitsOutTheCooldownBeforeCallingAgain()
	{
		var cooldown = TimeSpan.FromMilliseconds(400);
		var callTimes = new List<long>();
		var clock = System.Diagnostics.Stopwatch.StartNew();
		SyncCoordinator? coordinator = null;
		var signalled = 0;

		SeedUserMacro("M1", "m-ts");
		_api.MacroUpdateResponse = _ =>
		{
			lock (callTimes)
				callTimes.Add(clock.ElapsedMilliseconds);
			// Exactly once, and only from the first pass: an edit arriving mid-pass is the
			// scenario under test. Signal takes _gate, and passes run outside it, so this
			// cannot deadlock.
			if (Interlocked.Increment(ref signalled) == 1)
				coordinator!.Signal();
			return Task.FromResult(RateLimited<MacroCreateResponse>());
		};

		coordinator = NewCoordinator(cooldown: cooldown);
		using (coordinator)
		{
			coordinator.Signal();
			await WaitForPassAsync(coordinator);

			long[] times;
			lock (callTimes)
				times = callTimes.ToArray();

			// Three attempts: the original pass, the automatic post-cooldown retry, and the
			// pass the mid-flight signal earned. The exact count is secondary — the point is
			// that EVERY gap respects the cooldown.
			Assert.True(times.Length >= 3, $"expected at least 3 attempts, saw {times.Length}");

			var floor = cooldown.TotalMilliseconds * 0.75;
			for (var n = 1; n < times.Length; n++)
			{
				var gap = times[n] - times[n - 1];
				Assert.True(
					gap >= floor,
					$"call {n + 1} came {gap} ms after call {n}; the {cooldown.TotalMilliseconds} ms cooldown was not respected");
			}

			Assert.True(Assert.Single(_storage.GetDirtyMacros(User, OwnerId)).Macro.IsDirty);
		}
	}

	// REGRESSION, review R2 m1 — the OTHER exit of the same B1 decision. When the single automatic
	// retry is limited again, the loop exits with _rateLimitedUntil freshly armed and nothing left
	// to consume it inside that run: the wait must therefore sit at the TOP of the loop body, so
	// the FIRST iteration of the next run — a debounced signal landing seconds later, i.e. a user
	// still editing — sits the marker out instead of calling the server inside the window it just
	// named. Before the fix the wait ran at the bottom of the body, gating only iterations within
	// one run; the post-exit signal's pass called the API after nothing but the debounce, a steady
	// one request per ~3 s against a server answering 429.
	//
	// The scenario is RateLimited_AbortsThePassPreservingDirtyStateAndRetriesExactlyOnce run to its
	// end, then one Signal(). Asserted as a GAP, like the B1 test above, because that is what
	// distinguishes fixed from broken: the call count is the same either way, but pre-fix the third
	// call trailed the second by only the debounce (~100 ms here) against a 400 ms cooldown.
	[Fact]
	public async Task RateLimited_SignalAfterTheLoopExits_StillWaitsOutTheArmedCooldown()
	{
		var cooldown = TimeSpan.FromMilliseconds(400);
		var callTimes = new List<long>();
		var clock = System.Diagnostics.Stopwatch.StartNew();
		SeedUserMacro("M1", "m-ts");
		_api.MacroUpdateResponse = _ =>
		{
			lock (callTimes)
				callTimes.Add(clock.ElapsedMilliseconds);
			return Task.FromResult(RateLimited<MacroCreateResponse>());
		};

		using var coordinator = NewCoordinator(cooldown: cooldown);
		coordinator.Signal();
		var firstRun = await WaitForPassAsync(coordinator);

		// The first run is over: one aborted pass, one automatic retry, both limited — the loop
		// has exited with the marker armed by the second 429 and no wait behind it.
		Assert.Equal(2, _api.EndpointsSnapshot().Length);

		// The post-exit signal: a user edit debounced in AFTER the coordinator went idle.
		coordinator.Signal();
		await WaitForPassAsync(coordinator, previous: firstRun);

		long[] times;
		lock (callTimes)
			times = [.. callTimes];

		// The new run made its own pass and single retry (limited again), so at least four calls
		// total — but the count is secondary. The finding is the 2→3 gap: call 3 is the first API
		// call of the NEW run, and it must not land inside the cooldown call 2 armed. The later
		// gaps ride the same top-of-loop wait, so they are held to the same floor.
		Assert.True(times.Length >= 3, $"expected at least 3 attempts, saw {times.Length}");
		var floor = cooldown.TotalMilliseconds * 0.75;
		for (var n = 1; n < times.Length; n++)
		{
			var gap = times[n] - times[n - 1];
			Assert.True(
				gap >= floor,
				$"call {n + 1} came {gap} ms after call {n}; the {cooldown.TotalMilliseconds} ms cooldown did not survive the loop exit");
		}

		Assert.True(Assert.Single(_storage.GetDirtyMacros(User, OwnerId)).Macro.IsDirty);
	}


	// ── Cooldown selection (pure) ───────────────────────────────────────

	// The plan's Risks section calls clamping at both ends "required, not optional", and the
	// ceiling is the one branch that cannot be reached by waiting a cooldown out in a test
	// (it is five minutes). SelectCooldown is pure and internal precisely so it can be
	// asserted directly — review R1 S3.
	//
	// milliseconds are used rather than TimeSpan literals so the theory stays table-shaped;
	// -1 stands for "no Retry-After header at all".
	[Theory]
	// No header: the caller's own default wins.
	[InlineData(-1, 60_000, 60_000)]
	// A non-positive header is not a usable wait. ApiHttpClient already floors a stale
	// HTTP-date at zero, so zero is the shape a past date actually arrives in.
	[InlineData(0, 60_000, 60_000)]
	// In range: the server knows better than the client default, in both directions.
	[InlineData(30_000, 60_000, 30_000)]
	[InlineData(120_000, 60_000, 120_000)]
	// Exactly at the ceiling is still the server's value, not the clamp.
	[InlineData(300_000, 60_000, 300_000)]
	// Over the ceiling: capped, so one absurd header cannot stop sync for hours.
	[InlineData(300_001, 60_000, 300_000)]
	[InlineData(86_400_000, 60_000, 300_000)]
	public void SelectCooldown_ClampsAtBothEnds(int retryAfterMs, int fallbackMs, int expectedMs)
	{
		var retryAfter = retryAfterMs < 0 ? (TimeSpan?)null : TimeSpan.FromMilliseconds(retryAfterMs);

		var selected = SyncCoordinator.SelectCooldown(retryAfter, TimeSpan.FromMilliseconds(fallbackMs));

		Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), selected);
	}

	// A negative wait should never reach here (ApiHttpClient clamps first), but if a caller
	// ever hands one over it must not become a negative Task.Delay — which throws.
	[Fact]
	public void SelectCooldown_NegativeRetryAfter_FallsBackInsteadOfGoingNegative()
	{
		var selected = SyncCoordinator.SelectCooldown(TimeSpan.FromSeconds(-30), TimeSpan.FromSeconds(60));

		Assert.Equal(TimeSpan.FromSeconds(60), selected);
	}


	// ── Empty-token idle (guest / signed out mid-debounce) ───────────────────

	[Fact]
	public async Task EmptyToken_PassIdlesWithoutApiCallsAndWithoutWarnings()
	{
		using var coordinator = NewCoordinator(token: "");
		SeedUserMacro("M1"); // raises the signal that starts the (idling) pass

		await WaitForPassAsync(coordinator);

		Assert.Empty(_api.EndpointsSnapshot());
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
		// The row stays owed to the server for whenever a session appears.
		Assert.True(Assert.Single(_storage.GetDirtyMacros(User, OwnerId)).Macro.IsDirty);
	}

	// ── Reconcile trigger (coder deviation 3: ISettingsSyncService.SyncCompleted) ─

	// An interrupted upgrade push leaves rows with server_updated_at NULL and NO dirty flag
	// or pending signal (markDirty: false is exactly that shape). SyncCompleted — raised
	// after every push/pull — must trigger a pass that enumerates them as unsynced.
	[Fact]
	public async Task SyncCompleted_TriggersAReconcilePassThatCreatesUnsyncedRows()
	{
		_api.MacroCreateResponse = req => Task.FromResult(MacroOk(req.Uid, "srv-1"));

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		var initialPass = await WaitForPassAsync(coordinator); // a first pass: nothing owed
		Assert.Empty(_api.EndpointsSnapshot());

		var macro = new MacroRecord { Name = "LEFT-BEHIND", RepeatCount = 1 };
		_storage.SaveMacro(User, OwnerId, Vid, Pid, NoKey, macro, markDirty: false); // unsynced, silent

		_settingsSync.RaiseSyncCompleted();
		await WaitForPassAsync(coordinator, previous: initialPass);

		Assert.Equal(new[] { "macros/create" }, _api.EndpointsSnapshot());
		Assert.Equal(macro.Uid, Assert.Single(_api.MacroCreateRequests).Uid);
		Assert.Equal("srv-1", Assert.Single(_storage.GetMacros(User, OwnerId, Vid, Pid, NoKey)).ServerUpdatedAt);
	}

	// ── Device state: ACTIVE_PRESET_NOT_FOUND and the mid-pass re-dirty ──────

	// The dirty key stays consumed: the retry mechanism is the preset's own create re-marking
	// the type (next test), not a blind devices/update loop.
	[Fact]
	public async Task DeviceState_ActivePresetNotFound_LeavesTheDirtyKeyClearedWithoutRetry()
	{
		var preset = SeedCleanSyncedPreset();
		_storage.SetAppliedPreset(User, OwnerId, Vid, Pid, NoKey, preset.Id);
		_api.DeviceStateUpdateResponse = () => Task.FromResult(Err<DeviceStateUpdateResponse>("ACTIVE_PRESET_NOT_FOUND", 404));

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "devices/update" }, _api.EndpointsSnapshot());
		Assert.Null(_storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaDeviceStateDirty(Vid, Pid)));
		Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("ACTIVE_PRESET_NOT_FOUND"));

		await Task.Delay(Settle);
		Assert.Single(_api.EndpointsSnapshot());
	}

	// A successful presets/create re-marks the type's device-state key mid-pass, so the SAME
	// pass pushes the now-resolvable active preset — the applied row here has no dirty key of
	// its own (the ACTIVE_PRESET_NOT_FOUND aftermath shape).
	[Fact]
	public async Task PresetCreate_ReDirtiesTheDeviceStateKeySoTheSamePassPushesIt()
	{
		var preset = SeedUserPreset("P-NEW"); // unsynced → create path
		_storage.SetAppliedPreset(User, OwnerId, Vid, Pid, NoKey, preset.Id, markDirty: false);
		_api.PresetCreateResponse = req => Task.FromResult(PresetOk(req.Uid, "p-srv"));
		_api.DeviceStateUpdateResponse = () => Task.FromResult(new DeviceStateUpdateResponse
		{
			Success = true, StatusCode = 200, UpdatedAt = "d-1",
		});

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "presets/create", "devices/update" }, _api.EndpointsSnapshot());
		Assert.Equal((Vid, Pid, preset.Uid, (string?)null), Assert.Single(_api.DeviceStateUpdateCalls));
		Assert.Null(_storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaDeviceStateDirty(Vid, Pid)));
		Assert.Equal("d-1", _storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaDeviceStateUpdatedAt(Vid, Pid)));
	}

	// ── Device state: mixed scopes (PR #120 review, M1 + m2) ────────────────

	// The sign-in pull writes the cloud's applied preset for the whole device type under
	// device_key = ''. When the user then applies another preset on a connected unit, the UI
	// upserts a SECOND row under that unit's Container ID and the '' row survives, stale —
	// and GetAppliedPresets orders '' first. The collapse must therefore pick the UNIT-keyed
	// row (this machine's explicit choice); picking the first one sent the pulled, stale
	// preset instead, so after the first pull "apply preset" never reached the server again.
	[Fact]
	public async Task DeviceState_PulledSharedRowAndUserAppliedUnitRow_PushesTheUnitKeyedPreset()
	{
		var pulled = SeedCleanSyncedPreset("P-PULLED");
		var applied = SeedCleanSyncedPreset("P-APPLIED");
		// Pull-shaped: unit-agnostic scope, markDirty: false (pulled rows ARE server content).
		_storage.SetAppliedPreset(User, OwnerId, Vid, Pid, NoKey, pulled.Id, markDirty: false);
		// The user's own choice on the connected unit — this is what must reach the server.
		_storage.SetAppliedPreset(User, OwnerId, Vid, Pid, UnitKey, applied.Id);
		_api.DeviceStateUpdateResponse = () => Task.FromResult(new DeviceStateUpdateResponse
		{
			Success = true, StatusCode = 200, UpdatedAt = "d-1",
		});

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "devices/update" }, _api.EndpointsSnapshot());
		Assert.Equal((Vid, Pid, applied.Uid, (string?)null), Assert.Single(_api.DeviceStateUpdateCalls));
		Assert.Null(_storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaDeviceStateDirty(Vid, Pid)));
		Assert.Equal("d-1", _storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaDeviceStateUpdatedAt(Vid, Pid)));
		// Dropping the other row is not silent — the same warning the upgrade push logs.
		Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning
			&& e.Message.Contains("disagree") && e.Message.Contains(applied.Uid));
	}

	// The fallback half of the same rule: a type no unit has applied a preset to has only the
	// pulled '' row, and that row is what gets pushed — and since nothing disagrees, silently.
	[Fact]
	public async Task DeviceState_OnlyTheSharedScopeRow_PushesItAsTheFallbackWithoutWarning()
	{
		var pulled = SeedCleanSyncedPreset("P-PULLED");
		_storage.SetAppliedPreset(User, OwnerId, Vid, Pid, NoKey, pulled.Id);

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "devices/update" }, _api.EndpointsSnapshot());
		Assert.Equal((Vid, Pid, pulled.Uid, (string?)null), Assert.Single(_api.DeviceStateUpdateCalls));
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
	}

	// Two units that agree with the pulled row are not a disagreement: the warning must stay
	// quiet, or it would fire on every pass for the ordinary single-preset setup.
	[Fact]
	public async Task DeviceState_AllRowsNameTheSamePreset_PushesItWithoutWarning()
	{
		var applied = SeedCleanSyncedPreset("P-APPLIED");
		_storage.SetAppliedPreset(User, OwnerId, Vid, Pid, NoKey, applied.Id, markDirty: false);
		_storage.SetAppliedPreset(User, OwnerId, Vid, Pid, UnitKey, applied.Id);
		_storage.SetAppliedPreset(User, OwnerId, Vid, Pid, "unit-b", applied.Id);

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal((Vid, Pid, applied.Uid, (string?)null), Assert.Single(_api.DeviceStateUpdateCalls));
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
	}

	// ── Unparseable 2xx on the UPDATE paths (PR #120 review, m1) ────────────

	// A 2xx that did not bind is the PHP router's answer to a missing route: the server may
	// have done nothing. On an update path the row already carries a server_updated_at, so
	// clearing is_dirty would drop it out of the dirty selection FOREVER — the edit would be
	// lost silently. The mark stays, the retry costs one update next pass.
	[Fact]
	public async Task MacroUpdate_Unparseable2xx_LeavesTheRowDirtyAndWarns()
	{
		SeedUserMacro("M1", "m-ts");
		_api.MacroUpdateResponse = _ => Task.FromResult(Unparseable2xx<MacroCreateResponse>());

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "macros/update" }, _api.EndpointsSnapshot());
		// Still owed to the server: dirty, with the timestamp it was pushed against.
		Assert.True(Assert.Single(_storage.GetDirtyMacros(User, OwnerId)).Macro.IsDirty);
		Assert.Equal("m-ts", Assert.Single(_storage.GetMacros(User, OwnerId, Vid, Pid, NoKey)).ServerUpdatedAt);
		Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("unparseable 2xx"));

		// Nothing re-signalled, so the retry waits for the next pass — no storm.
		await Task.Delay(Settle);
		Assert.Single(_api.EndpointsSnapshot());
	}

	[Fact]
	public async Task PresetUpdate_Unparseable2xx_LeavesTheRowDirtyAndWarns()
	{
		SeedUserPreset("P1", "p-ts");
		_api.PresetUpdateResponse = _ => Task.FromResult(Unparseable2xx<PresetCreateResponse>());

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "presets/update" }, _api.EndpointsSnapshot());
		Assert.True(Assert.Single(_storage.GetDirtyPresets(User, OwnerId)).Preset.IsDirty);
		Assert.Equal("p-ts", Assert.Single(_storage.GetPresets(User, OwnerId, Vid, Pid)).ServerUpdatedAt);
		Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("unparseable 2xx"));
	}

	// The sync_meta twin: nothing ever re-marks preferences except a save, so an unconfirmed
	// 2xx that consumed the flag would strand the saved values just as permanently.
	[Fact]
	public async Task PreferencesUpdate_Unparseable2xx_LeavesTheDirtyFlagSet()
	{
		_storage.SetAppPreference(User, OwnerId, SettingsRepository.PrefScrollLines, 5);
		_storage.SetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaPreferencesDirty, "gen-1");
		_api.PreferencesUpdateResponse = () => Task.FromResult(Unparseable2xx<PreferencesUpdateResponse>());

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "preferences/update" }, _api.EndpointsSnapshot());
		Assert.Equal("gen-1", _storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaPreferencesDirty));
		Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("unparseable 2xx"));
	}

	[Fact]
	public async Task DeviceStateUpdate_Unparseable2xx_LeavesTheDirtyMarkSet()
	{
		var preset = SeedCleanSyncedPreset();
		_storage.SetAppliedPreset(User, OwnerId, Vid, Pid, UnitKey, preset.Id);
		_api.DeviceStateUpdateResponse = () => Task.FromResult(Unparseable2xx<DeviceStateUpdateResponse>());

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "devices/update" }, _api.EndpointsSnapshot());
		Assert.NotNull(_storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaDeviceStateDirty(Vid, Pid)));
		Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("unparseable 2xx"));
	}

	// The CREATE path deliberately keeps the tolerant rule: clearing is_dirty there is
	// harmless because server_updated_at stays NULL, which is itself a dirty-selection
	// criterion — the row is re-enumerated next pass either way, so nothing warns.
	[Fact]
	public async Task MacroCreate_Unparseable2xx_KeepsTheRowOwedWithoutWarning()
	{
		var macro = SeedUserMacro("M-NEW");
		_api.MacroCreateResponse = _ => Task.FromResult(Unparseable2xx<MacroCreateResponse>());

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "macros/create" }, _api.EndpointsSnapshot());
		Assert.Null(Assert.Single(_storage.GetMacros(User, OwnerId, Vid, Pid, NoKey)).ServerUpdatedAt);
		// Unsynced rows are enumerated by server_updated_at IS NULL, dirty flag or not.
		Assert.Equal(macro.Uid, Assert.Single(_storage.GetDirtyMacros(User, OwnerId)).Macro.Uid);
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
	}

	// ── Preferences: dirty flag with nothing behind it (PR #120 review, m6) ─

	// MarkSyncDirty for preferences comes from SettingsRepository, which writes the rows — so
	// "flag set, zero rows" is a leftover, not a state to keep re-reading. The flag is
	// consumed and the pass carries on to device state; the alternative (leave it) made every
	// later pass do two pointless storage reads forever.
	[Fact]
	public async Task Preferences_DirtyFlagWithNoSavedRows_ConsumesTheFlagAndContinuesThePass()
	{
		_storage.SetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaPreferencesDirty, "gen-1");
		var preset = SeedCleanSyncedPreset();
		_storage.SetAppliedPreset(User, OwnerId, Vid, Pid, UnitKey, preset.Id);

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		// No rows to send, so no call — but the pass did not stop either.
		Assert.Equal(new[] { "devices/update" }, _api.EndpointsSnapshot());
		Assert.Null(_storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaPreferencesDirty));
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
	}

	// The other way a storage read comes back empty: teardown. RunOnStorageAsync yields
	// default(T) once Dispose has begun, and every step treats that as "stop" — so a pass
	// caught by app exit makes no further calls instead of pushing default-shaped state.
	// (Which step aborts depends on where teardown lands; the pass-level contract is what a
	// test can observe deterministically.)
	[Fact]
	public async Task StorageTornDownMidPass_StopsThePassAtTheNextRead()
	{
		// Its own storage: this test disposes it, and the fixture's Dispose must not run
		// LocalStorageService.Dispose twice (the second AcquireLock hits a disposed lock).
		var storage = new LocalStorageService("Data Source=:memory:");
		var macro = new MacroRecord
		{
			Name = "M1",
			RepeatCount = 1,
			Events = { new MacroEventRecord { KeyCode = 0x04, IsPress = true, DelayMs = 5 } },
		};
		storage.SaveMacro(User, OwnerId, Vid, Pid, NoKey, macro);
		storage.SetMacroServerUpdatedAt(macro.Id, "m-ts");
		storage.SetAppPreference(User, OwnerId, SettingsRepository.PrefScrollLines, 5);
		storage.SetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaPreferencesDirty, "gen-1");

		using var gate = new SemaphoreSlim(0);
		var parked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_api.MacroUpdateResponse = async req =>
		{
			parked.SetResult();
			await gate.WaitAsync();
			return MacroOk(req.Uid, "m-ts-new");
		};

		using var coordinator = new SyncCoordinator(
			_api, storage, new UserProfile { CustomerId = CustomerId, SessionToken = "token-1" },
			new PresetConfigurationBuilder(storage), _settingsSync, _log, Debounce);
		coordinator.Signal();
		await parked.Task;

		// App exit while the pass is parked in its first HTTP call.
		storage.Dispose();
		gate.Release();
		await WaitForPassAsync(coordinator);

		// Nothing after the parked call: no preferences/update, no devices/update.
		Assert.Equal(new[] { "macros/update" }, _api.EndpointsSnapshot());
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Error);
	}

	// ── Disposal (container teardown at app exit) ────────────────────────────

	// Dispose unsubscribes both events and kills the debounce, so a save landing after
	// teardown must neither throw into the mutator (the event's contract) nor start a pass.
	// The exact Signal-vs-Dispose interleaving inside Signal is not deterministically
	// reachable from a test; this pins the observable contract around it.
	[Fact]
	public async Task MutationAfterDispose_DoesNotThrowAndStartsNoPass()
	{
		var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator); // a first pass: nothing owed
		Assert.Empty(_api.EndpointsSnapshot());

		coordinator.Dispose();
		SeedUserMacro("M-AFTER-DISPOSE"); // the raise must be a no-op, not a crash

		await Task.Delay(Settle);
		Assert.Empty(_api.EndpointsSnapshot());
		// The row still owes its push to the next session's coordinator.
		Assert.True(Assert.Single(_storage.GetDirtyMacros(User, OwnerId)).Macro.IsDirty);
	}

	// ── Device roster (docs/plans/add-api-v21-device-roster) ─────────────────
	// The pass's new LAST step pushes the deviceroster.* intent marks the devices-table
	// mutators wrote: .add → devices/add, .remove → devices/remove, with generation-guarded
	// clears and DEVICE_ALREADY_EXISTS / DEVICE_NOT_FOUND consumed like success.

	private static DeviceRecord RosterMouse(string name = "PG Taipan") => new()
	{
		Name = name,
		DeviceTypeLabel = "Wireless Mouse",
		CardType = "single",
		VendorId = Vid,
		ProductId = Pid,
	};

	private string? AddMark() =>
		_storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaDeviceRosterAdd(Vid, Pid));

	private string? RemoveMark() =>
		_storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaDeviceRosterRemove(Vid, Pid));

	// The roster runs strictly after device state — the plan's pass order — and carries the
	// card's settings identity plus its display name.
	[Fact]
	public async Task RosterStep_RunsAfterDeviceStateAndSendsIdentityAndName()
	{
		var preset = SeedCleanSyncedPreset();
		_storage.SetAppliedPreset(User, OwnerId, Vid, Pid, UnitKey, preset.Id);
		_storage.AddDevice(User, OwnerId, RosterMouse());
		_api.DeviceStateUpdateResponse = () => Task.FromResult(new DeviceStateUpdateResponse
		{
			Success = true, StatusCode = 200, UpdatedAt = "dev-ts-1",
		});

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "devices/update", "devices/add" }, _api.EndpointsSnapshot());
		Assert.Equal((Vid, Pid, "PG Taipan"), Assert.Single(_api.DeviceAddCalls));
		Assert.Null(AddMark());
	}

	// Idempotent success: the model is already on the account's roster (a second machine
	// registered it first) — the mark is consumed exactly like success, no error noise.
	[Fact]
	public async Task RosterAdd_DeviceAlreadyExists_ClearsTheMarkWithoutErrorLogs()
	{
		_storage.AddDevice(User, OwnerId, RosterMouse());
		_api.DeviceAddResponse = (_, _, _) => Task.FromResult(Err<ApiResponse>("DEVICE_ALREADY_EXISTS"));

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Null(AddMark());
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Error);
	}

	[Fact]
	public async Task RosterRemove_Success_ClearsTheMark()
	{
		var id = _storage.AddDevice(User, OwnerId, RosterMouse());
		_storage.DeleteDevice(User, OwnerId, id);
		Assert.NotNull(RemoveMark());

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal((Vid, Pid), Assert.Single(_api.DeviceRemoveCalls));
		Assert.Empty(_api.DeviceAddCalls);
		Assert.Null(RemoveMark());
	}

	[Fact]
	public async Task RosterRemove_DeviceNotFound_ClearsTheMarkWithoutErrorLogs()
	{
		var id = _storage.AddDevice(User, OwnerId, RosterMouse());
		_storage.DeleteDevice(User, OwnerId, id);
		_api.DeviceRemoveResponse = (_, _) => Task.FromResult(Err<ApiResponse>("DEVICE_NOT_FOUND", 404));

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Null(RemoveMark());
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Error);
	}

	// A validation rejection can never succeed by resending the same payload — the mark is
	// dropped with a warning; the next card mutation re-marks.
	[Fact]
	public async Task RosterAdd_ValidationRejection_DropsTheMarkWithAWarning()
	{
		_storage.AddDevice(User, OwnerId, RosterMouse());
		_api.DeviceAddResponse = (_, _, _) => Task.FromResult(Err<ApiResponse>("NAME_TOO_LONG", 400));

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Null(AddMark());
		Assert.Contains(_log.Entries,
			e => e.Level == LogLevel.Warning && e.Message.Contains("dropping the mark"));
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Error);
	}

	// An unparseable 2xx is the PHP router's answer for a route it does not have — the
	// server may well have done nothing, so the mark survives for the next pass
	// (ConfirmedSuccess discipline, same as every update path).
	[Fact]
	public async Task RosterAdd_Unparseable2xx_LeavesTheMarkSet()
	{
		_storage.AddDevice(User, OwnerId, RosterMouse());
		_api.DeviceAddResponse = (_, _, _) => Task.FromResult(new ApiResponse
		{
			Success = false, StatusCode = 200, MalformedPayload = true,
		});

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.NotNull(AddMark());
		Assert.Contains(_log.Entries,
			e => e.Level == LogLevel.Warning && e.Message.Contains("unparseable 2xx"));
	}

	[Fact]
	public async Task RosterAdd_NetworkError_AbortsAndPreservesTheMark()
	{
		_storage.AddDevice(User, OwnerId, RosterMouse());
		var markBefore = AddMark();
		_api.DeviceAddResponse = (_, _, _) => Task.FromResult(new ApiResponse { IsNetworkError = true });

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		// The mark survives verbatim — the next signal retries it.
		Assert.Equal(markBefore, AddMark());
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Error);
	}

	// A mark whose card vanished between the mark and the pass (its delete swapped the
	// intent to .remove, which this pass's snapshot predates): skipped without clearing —
	// nothing is pushed for it and no card name can be resolved.
	[Fact]
	public async Task RosterAdd_NoCardForTheMarkedPair_SkipsWithoutCallingOrClearing()
	{
		_storage.MarkSyncDirty(User, OwnerId,
			LocalStorageService.SyncMetaDeviceRosterAdd(Vid, Pid), signal: false);

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Empty(_api.DeviceAddCalls);
		Assert.NotNull(AddMark());
	}

	// ── Notifications (docs/plans/add-api-v21-notifications-sync) ────────────
	// The pass's new LAST step: delete_all → read_all → tombstones → dirty rows, with the
	// no-retry rule on notifications/add (no uid idempotence — a repeated add mints a
	// duplicate server row) and keep-on-failure batch marks.

	private NotificationRecord SeedUserNotification(
		string title = "N1", long? serverId = null, bool read = false)
	{
		var record = new NotificationRecord
		{
			Title = title,
			Subtitle = "PG Taipan",
			CreatedAt = DateTime.Now,
			Type = "info",
			ServerId = serverId,
			IsRead = read,
		};
		// A synced row arrives pull-shaped (clean); a local one lands dirty.
		record.Id = _storage.AddNotification(User, OwnerId, record, markDirty: serverId is null);
		if (serverId is not null && read)
			_storage.SetNotificationRead(record.Id, true); // marks dirty — the live toggle
		return record;
	}

	private NotificationRecord StoredNotification(string title) =>
		Assert.Single(_storage.GetNotifications(User, OwnerId), n => n.Title == title);

	// delete_all before read_all: DeleteAllNotifications drops the read_all mark it
	// subsumes, so both marks can only coexist when mark-all-read happened AFTER the
	// delete-all — the server must replay them in that same order, or the read_all would
	// apply to the pre-delete rows the user already discarded.
	[Fact]
	public async Task NotificationsStep_DeleteAllRunsBeforeReadAll()
	{
		_storage.DeleteAllNotifications(User, OwnerId);
		_storage.SetAllNotificationsRead(User, OwnerId);

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(new[] { "notifications/delete_all", "notifications/read_all" }, _api.EndpointsSnapshot());
		Assert.Null(_storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaNotificationsDeleteAll));
		Assert.Null(_storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaNotificationsReadAll));
	}

	// The full intra-step order, plus the step's place after the roster: batch mark →
	// tombstone → dirty add (whose row mark-all-read made read, hence the follow-up read).
	[Fact]
	public async Task NotificationsStep_RunsAfterRosterInOrderReadAllTombstonesDirty()
	{
		_storage.AddDevice(User, OwnerId, RosterMouse());
		var synced = SeedUserNotification("SYNCED", serverId: 7);
		_storage.DeleteNotification(synced.Id); // tombstone uid "7"
		SeedUserNotification("LOCAL");
		_storage.SetAllNotificationsRead(User, OwnerId);
		_api.NotificationAddResponse = _ => Task.FromResult(new NotificationAddResponse
		{
			Success = true, StatusCode = 200, Notification = new NotificationDto { Id = 501 },
		});

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal(
			new[] { "devices/add", "notifications/read_all", "notifications/delete", "notifications/add", "notifications/read" },
			_api.EndpointsSnapshot());
		Assert.Equal(7, Assert.Single(_api.NotificationDeleteCalls));
		Assert.Empty(_storage.GetSyncTombstones(User, OwnerId, LocalStorageService.TombstoneKindNotification));
	}

	[Fact]
	public async Task NotificationAdd_Success_StoresServerIdClearsDirtyAndSkipsReadForUnread()
	{
		SeedUserNotification("LOCAL");
		_api.NotificationAddResponse = _ => Task.FromResult(new NotificationAddResponse
		{
			Success = true, StatusCode = 200, Notification = new NotificationDto { Id = 501 },
		});

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		var request = Assert.Single(_api.NotificationAddRequests);
		Assert.Equal(("LOCAL", "PG Taipan", "info"), (request.Title, request.Message, request.Type));
		Assert.Empty(_api.NotificationReadCalls);
		var row = StoredNotification("LOCAL");
		Assert.Equal(501, row.ServerId);
		Assert.False(row.IsDirty);
	}

	// No uid idempotence on add: any confirmed-but-unmappable outcome (validation reject,
	// unparseable 2xx, success without an id) clears the dirty flag and leaves the row
	// local-only — retrying would mint a duplicate server row per pass.
	[Fact]
	public async Task NotificationAdd_NoServerIdInResponse_ClearsDirtyLeavesLocalOnlyAndNeverRetries()
	{
		SeedUserNotification("LOCAL");
		// The default add response: confirmed success, no echoed notification.

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		var first = await WaitForPassAsync(coordinator);

		var row = StoredNotification("LOCAL");
		Assert.Null(row.ServerId);
		Assert.False(row.IsDirty);
		Assert.Contains(_log.Entries,
			e => e.Level == LogLevel.Warning && e.Message.Contains("stays local-only"));

		coordinator.Signal();
		await WaitForPassAsync(coordinator, previous: first);
		Assert.Single(_api.NotificationAddRequests);
	}

	[Fact]
	public async Task NotificationStatePush_SelectsReadOrUnreadByTheRowsFlag()
	{
		var row = SeedUserNotification("SYNCED", serverId: 9);
		_storage.SetNotificationRead(row.Id, true);
		_storage.SetNotificationRead(row.Id, false);

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Equal((9L, false), Assert.Single(_api.NotificationReadCalls));
		Assert.False(StoredNotification("SYNCED").IsDirty);
	}

	// Deleted from another machine: the local mirror-delete must not tombstone (the server
	// already has nothing to delete) and must not log above Information.
	[Fact]
	public async Task NotificationStatePush_NotFound_RemovesTheLocalRowWithoutTombstone()
	{
		var row = SeedUserNotification("SYNCED", serverId: 9);
		_storage.SetNotificationRead(row.Id, true);
		_api.NotificationReadResponse = (_, _) => Task.FromResult(Err<ApiResponse>("NOTIFICATION_NOT_FOUND", 404));

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.Empty(_storage.GetNotifications(User, OwnerId));
		Assert.Empty(_storage.GetSyncTombstones(User, OwnerId, LocalStorageService.TombstoneKindNotification));
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
	}

	// The rev guard end to end: a toggle landing while the push is in flight keeps the row
	// dirty, and the follow-up pass pushes the newer state.
	[Fact]
	public async Task NotificationStatePush_MidFlightReToggle_KeepsDirtyAndPushesTheNewerState()
	{
		var row = SeedUserNotification("SYNCED", serverId: 9);
		_storage.SetNotificationRead(row.Id, true);
		var toggled = 0;
		_api.NotificationReadResponse = (_, _) =>
		{
			// Exactly once: the user un-reads the entry while its read push is in flight.
			if (Interlocked.Increment(ref toggled) == 1)
				_storage.SetNotificationRead(row.Id, false);
			return Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });
		};

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		var first = await WaitForPassAsync(coordinator);

		// The toggle's signal lands as a folded follow-up or a fresh debounced pass,
		// depending on where the debounce fired relative to the first pass's end — wait for
		// whichever until the unread push shows up, then the row must be clean: the first
		// pass's rev-guarded clear missed (stale rev) and the second pass consumed the
		// newer state.
		await WaitUntilAsync(
			() => _api.EndpointsSnapshot().Count(e => e == "notifications/unread") == 1,
			"the re-toggled state to be pushed");
		var last = coordinator.CurrentPassTask;
		if (last is not null && !ReferenceEquals(last, first))
			await last;
		Assert.Equal(new[] { (9L, true), (9L, false) }, _api.NotificationReadCalls);
		Assert.False(StoredNotification("SYNCED").IsDirty);
	}

	// Losing a delete_all mark while the server kept its rows would let the next list merge
	// resurrect the whole deleted bell — a non-abort failure must leave the mark.
	[Fact]
	public async Task NotificationDeleteAll_Rejected_LeavesTheMarkForTheNextPass()
	{
		_storage.DeleteAllNotifications(User, OwnerId);
		_api.NotificationDeleteAllResponse = () => Task.FromResult(Err<ApiResponse>("SERVER_ERROR", 500));

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.NotNull(_storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaNotificationsDeleteAll));
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Error);
	}

	// PR #127, m5: the user deletes a never-synced entry while its notifications/add is in
	// flight. The minted server id must get a tombstone (else the next merge resurrects the
	// deleted entry), the tombstone's signal must drive its server-side delete, and no read
	// follow-up may target the vanished row.
	[Fact]
	public async Task NotificationAdd_RowDeletedMidFlight_TombstonesTheMintedIdAndDeletesItServerSide()
	{
		var row = SeedUserNotification("LOCAL");
		var addParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource<NotificationAddResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
		_api.NotificationAddResponse = _ =>
		{
			addParked.TrySetResult();
			return release.Task;
		};

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await addParked.Task;

		// Never-synced: the local delete leaves no tombstone of its own.
		_storage.DeleteNotification(row.Id);
		Assert.Empty(_storage.GetSyncTombstones(User, OwnerId, LocalStorageService.TombstoneKindNotification));

		release.SetResult(new NotificationAddResponse
		{
			Success = true, StatusCode = 200, Notification = new NotificationDto { Id = 501 },
		});

		await WaitUntilAsync(
			() => _api.EndpointsSnapshot().Contains("notifications/delete"),
			"the minted server id to be deleted server-side");
		var last = coordinator.CurrentPassTask;
		if (last is not null)
			await last;
		Assert.Equal(501, Assert.Single(_api.NotificationDeleteCalls));
		Assert.Empty(_api.NotificationReadCalls);
		Assert.Empty(_storage.GetSyncTombstones(User, OwnerId, LocalStorageService.TombstoneKindNotification));
		Assert.Empty(_storage.GetNotifications(User, OwnerId));
	}

	[Fact]
	public async Task NotificationAdd_NetworkError_AbortsAndPreservesTheDirtyRow()
	{
		SeedUserNotification("LOCAL");
		_api.NotificationAddResponse = _ => Task.FromResult(new NotificationAddResponse { IsNetworkError = true });

		using var coordinator = NewCoordinator();
		coordinator.Signal();
		await WaitForPassAsync(coordinator);

		Assert.True(StoredNotification("LOCAL").IsDirty);
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Error);
	}

	// ── Test doubles ─────────────────────────────────────────────────────────

	// Records every call in arrival order (the pass is sequential by design) and answers
	// from swappable factories; defaults answer plain success. Adds and snapshots are
	// lock-guarded because tests poll the endpoint log while a pass runs on the pool.
	private sealed class RecordingSyncApi : ISyncApiService
	{
		private readonly object _sync = new();
		private readonly List<string> _endpoints = [];

		public List<MacroCreateRequest> MacroCreateRequests { get; } = [];
		public List<MacroUpdateRequest> MacroUpdateRequests { get; } = [];
		public List<(string Uid, string? UpdatedAt)> MacroDeleteRequests { get; } = [];
		public List<PresetCreateRequest> PresetCreateRequests { get; } = [];
		public List<PresetUpdateRequest> PresetUpdateRequests { get; } = [];
		public List<(string Uid, string? UpdatedAt)> PresetDeleteRequests { get; } = [];
		public List<(Dictionary<string, object> Preferences, string? UpdatedAt)> PreferencesUpdateCalls { get; } = [];
		public List<(int VendorId, int ProductId, string? ActivePresetUid, string? UpdatedAt)> DeviceStateUpdateCalls { get; } = [];

		public string[] EndpointsSnapshot()
		{
			lock (_sync)
				return [.. _endpoints];
		}

		private void Record(string endpoint, Action? also = null)
		{
			lock (_sync)
			{
				_endpoints.Add(endpoint);
				also?.Invoke();
			}
		}

		public Func<MacroCreateRequest, Task<MacroCreateResponse>> MacroCreateResponse { get; set; } =
			_ => Task.FromResult(new MacroCreateResponse { Success = true, StatusCode = 200 });

		public Func<MacroUpdateRequest, Task<MacroCreateResponse>> MacroUpdateResponse { get; set; } =
			_ => Task.FromResult(new MacroCreateResponse { Success = true, StatusCode = 200 });

		public Func<string, string?, Task<ApiResponse>> MacroDeleteResponse { get; set; } =
			(_, _) => Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });

		public Func<string, Task<MacroGetResponse>> MacroGetResponse { get; set; } =
			_ => Task.FromResult(new MacroGetResponse { Success = true, StatusCode = 200 });

		public Func<PresetCreateRequest, Task<PresetCreateResponse>> PresetCreateResponse { get; set; } =
			_ => Task.FromResult(new PresetCreateResponse { Success = true, StatusCode = 200 });

		public Func<PresetUpdateRequest, Task<PresetCreateResponse>> PresetUpdateResponse { get; set; } =
			_ => Task.FromResult(new PresetCreateResponse { Success = true, StatusCode = 200 });

		public Func<string, string?, Task<ApiResponse>> PresetDeleteResponse { get; set; } =
			(_, _) => Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });

		public Func<string, Task<PresetGetResponse>> PresetGetResponse { get; set; } =
			_ => Task.FromResult(new PresetGetResponse { Success = true, StatusCode = 200 });

		public Func<Task<PreferencesUpdateResponse>> PreferencesUpdateResponse { get; set; } =
			() => Task.FromResult(new PreferencesUpdateResponse { Success = true, StatusCode = 200 });

		public Func<Task<PreferencesGetResponse>> PreferencesGetResponse { get; set; } =
			() => Task.FromResult(new PreferencesGetResponse { Success = true, StatusCode = 200 });

		public Func<Task<DeviceStateUpdateResponse>> DeviceStateUpdateResponse { get; set; } =
			() => Task.FromResult(new DeviceStateUpdateResponse { Success = true, StatusCode = 200 });

		public Func<int, int, Task<DeviceStateGetResponse>> DeviceStateGetResponse { get; set; } =
			(_, _) => Task.FromResult(new DeviceStateGetResponse { Success = true, StatusCode = 200 });

		public Task<MacroCreateResponse> CreateMacroAsync(string token, MacroCreateRequest request, CancellationToken ct = default)
		{
			Record("macros/create", () => MacroCreateRequests.Add(request));
			return MacroCreateResponse(request);
		}

		public Task<MacroCreateResponse> UpdateMacroAsync(string token, MacroUpdateRequest request, CancellationToken ct = default)
		{
			Record("macros/update", () => MacroUpdateRequests.Add(request));
			return MacroUpdateResponse(request);
		}

		public Task<ApiResponse> DeleteMacroAsync(string token, string uid, string? updatedAt, CancellationToken ct = default)
		{
			Record("macros/delete", () => MacroDeleteRequests.Add((uid, updatedAt)));
			return MacroDeleteResponse(uid, updatedAt);
		}

		public Task<MacroGetResponse> GetMacroAsync(string token, string uid, CancellationToken ct = default)
		{
			Record("macros/get");
			return MacroGetResponse(uid);
		}

		public Task<PresetCreateResponse> CreatePresetAsync(string token, PresetCreateRequest request, CancellationToken ct = default)
		{
			Record("presets/create", () => PresetCreateRequests.Add(request));
			return PresetCreateResponse(request);
		}

		public Task<PresetCreateResponse> UpdatePresetAsync(string token, PresetUpdateRequest request, CancellationToken ct = default)
		{
			Record("presets/update", () => PresetUpdateRequests.Add(request));
			return PresetUpdateResponse(request);
		}

		public Task<ApiResponse> DeletePresetAsync(string token, string uid, string? updatedAt, CancellationToken ct = default)
		{
			Record("presets/delete", () => PresetDeleteRequests.Add((uid, updatedAt)));
			return PresetDeleteResponse(uid, updatedAt);
		}

		public Task<PresetGetResponse> GetPresetAsync(string token, string uid, CancellationToken ct = default)
		{
			Record("presets/get");
			return PresetGetResponse(uid);
		}

		public Task<PreferencesUpdateResponse> UpdatePreferencesAsync(string token, Dictionary<string, object> preferences, string? updatedAt = null, CancellationToken ct = default)
		{
			Record("preferences/update", () => PreferencesUpdateCalls.Add((preferences, updatedAt)));
			return PreferencesUpdateResponse();
		}

		public Task<PreferencesGetResponse> GetPreferencesAsync(string token, CancellationToken ct = default)
		{
			Record("preferences/get");
			return PreferencesGetResponse();
		}

		public Task<DeviceStateUpdateResponse> UpdateDeviceStateAsync(string token, int vendorId, int productId, string? activePresetUid, string? updatedAt = null, CancellationToken ct = default)
		{
			Record("devices/update", () => DeviceStateUpdateCalls.Add((vendorId, productId, activePresetUid, updatedAt)));
			return DeviceStateUpdateResponse();
		}

		public Task<DeviceStateGetResponse> GetDeviceStateAsync(string token, int vendorId, int productId, CancellationToken ct = default)
		{
			Record("devices/get");
			return DeviceStateGetResponse(vendorId, productId);
		}

		// Pull-side list endpoints — the coordinator never calls them; recorded so an
		// accidental call shows up in an endpoint-order assertion.
		public Task<MacroListResponse> ListMacrosAsync(string token, CancellationToken ct = default)
		{
			Record("macros/list");
			return Task.FromResult(new MacroListResponse { Success = true, StatusCode = 200, Macros = [] });
		}

		public Task<PresetListResponse> ListPresetsAsync(string token, CancellationToken ct = default)
		{
			Record("presets/list");
			return Task.FromResult(new PresetListResponse { Success = true, StatusCode = 200, Presets = [] });
		}

		// Device roster (docs/plans/add-api-v21-device-roster) — the coordinator's last step.
		public List<(int VendorId, int ProductId, string Name)> DeviceAddCalls { get; } = [];
		public List<(int VendorId, int ProductId)> DeviceRemoveCalls { get; } = [];

		public Func<int, int, string, Task<ApiResponse>> DeviceAddResponse { get; set; } =
			(_, _, _) => Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });

		public Func<int, int, Task<ApiResponse>> DeviceRemoveResponse { get; set; } =
			(_, _) => Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });

		public Task<ApiResponse> AddDeviceAsync(string token, int vendorId, int productId, string name, CancellationToken ct = default)
		{
			Record("devices/add", () => DeviceAddCalls.Add((vendorId, productId, name)));
			return DeviceAddResponse(vendorId, productId, name);
		}

		public Task<DeviceListResponse> ListDevicesAsync(string token, CancellationToken ct = default)
		{
			Record("devices/list");
			// Devices deliberately null: the coordinator never calls this (pull-side), and a
			// null list makes the pull's roster step a no-op.
			return Task.FromResult(new DeviceListResponse { Success = true, StatusCode = 200 });
		}

		public Task<ApiResponse> RemoveDeviceAsync(string token, int vendorId, int productId, CancellationToken ct = default)
		{
			Record("devices/remove", () => DeviceRemoveCalls.Add((vendorId, productId)));
			return DeviceRemoveResponse(vendorId, productId);
		}

		// Notifications (docs/plans/add-api-v21-notifications-sync) — the pass's final step.
		public List<NotificationAddRequest> NotificationAddRequests { get; } = [];
		public List<(long ServerId, bool IsRead)> NotificationReadCalls { get; } = [];
		public List<long> NotificationDeleteCalls { get; } = [];

		public Func<NotificationAddRequest, Task<NotificationAddResponse>> NotificationAddResponse { get; set; } =
			_ => Task.FromResult(new NotificationAddResponse { Success = true, StatusCode = 200 });

		public Func<long, bool, Task<ApiResponse>> NotificationReadResponse { get; set; } =
			(_, _) => Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });

		public Func<long, Task<ApiResponse>> NotificationDeleteResponse { get; set; } =
			_ => Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });

		public Func<Task<ApiResponse>> NotificationReadAllResponse { get; set; } =
			() => Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });

		public Func<Task<ApiResponse>> NotificationDeleteAllResponse { get; set; } =
			() => Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });

		public Task<NotificationAddResponse> AddNotificationAsync(string token, NotificationAddRequest request, CancellationToken ct = default)
		{
			Record("notifications/add", () => NotificationAddRequests.Add(request));
			return NotificationAddResponse(request);
		}

		public Task<NotificationListResponse> ListNotificationsAsync(string token, CancellationToken ct = default)
		{
			Record("notifications/list");
			// Notifications deliberately null: the coordinator never calls this (pull-side),
			// and a null list makes the pull's merge a no-op.
			return Task.FromResult(new NotificationListResponse { Success = true, StatusCode = 200 });
		}

		public Task<ApiResponse> ReadNotificationAsync(string token, long notificationId, CancellationToken ct = default)
		{
			Record("notifications/read", () => NotificationReadCalls.Add((notificationId, true)));
			return NotificationReadResponse(notificationId, true);
		}

		public Task<ApiResponse> UnreadNotificationAsync(string token, long notificationId, CancellationToken ct = default)
		{
			Record("notifications/unread", () => NotificationReadCalls.Add((notificationId, false)));
			return NotificationReadResponse(notificationId, false);
		}

		public Task<ApiResponse> DeleteNotificationAsync(string token, long notificationId, CancellationToken ct = default)
		{
			Record("notifications/delete", () => NotificationDeleteCalls.Add(notificationId));
			return NotificationDeleteResponse(notificationId);
		}

		public Task<ApiResponse> ReadAllNotificationsAsync(string token, CancellationToken ct = default)
		{
			Record("notifications/read_all");
			return NotificationReadAllResponse();
		}

		public Task<ApiResponse> DeleteAllNotificationsAsync(string token, CancellationToken ct = default)
		{
			Record("notifications/delete_all");
			return NotificationDeleteAllResponse();
		}
	}

	// ISettingsSyncService with a raisable SyncCompleted — the coordinator's reconcile
	// trigger; the pull/push pipelines themselves are out of scope here.
	private sealed class FakeSettingsSyncService : ISettingsSyncService
	{
		public event Action? PullCompletedWithChanges;
		public event Action? SyncCompleted;
		// Never raised here — explicit no-op accessors avoid CS0067 (declared but never used).
		public event Action? NotificationsPulled { add { } remove { } }

		public Task PullTask => Task.CompletedTask;

		public Task OnGuestUpgradedAsync(int customerId, CancellationToken ct = default) => Task.CompletedTask;

		public Task OnSignedInAsync(int customerId, CancellationToken ct = default) => Task.CompletedTask;

		public void RaiseSyncCompleted() => SyncCompleted?.Invoke();

		// Keeps the compiler honest about the unused interface event.
		public void RaisePullCompletedWithChanges() => PullCompletedWithChanges?.Invoke();
	}

	// Captures level + rendered message (same double as SettingsSyncServicePullTests) so
	// tests can assert what was — and was not — logged.
	private sealed class CapturingLogger<T> : ILogger<T>
	{
		public List<(LogLevel Level, string Message)> Entries { get; } = [];

		public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

		public bool IsEnabled(LogLevel logLevel) => true;

		public void Log<TState>(
			LogLevel logLevel, EventId eventId, TState state, Exception? exception,
			Func<TState, Exception?, string> formatter)
		{
			lock (Entries)
				Entries.Add((logLevel, formatter(state, exception)));
		}
	}
}
