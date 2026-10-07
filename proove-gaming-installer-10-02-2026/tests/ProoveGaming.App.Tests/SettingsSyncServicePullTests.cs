using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// The sign-in pull (docs/plans/add-api-v2-pull): the REAL SettingsSyncService is driven
// against a real LocalStorageService and a recording fake ISyncApiService — HTTP is the only
// boundary faked, mirroring SettingsSyncServiceTests. Tests await PullTask (the internal
// seam, the pull's twin of PushTask) to observe the background pull.
public class SettingsSyncServicePullTests : IDisposable
{
	private const int CustomerId = 42;
	private const string OwnerId = "42";
	private const string User = LocalStorageService.OwnerKindUser;
	private const int Vid = 0x04D9;
	private const int Pid = 0xA23F;
	private const string NoKey = "";

	private const string MacroUid1 = "aaaaaaaa-1111-1111-1111-111111111111";
	private const string MacroUid2 = "bbbbbbbb-2222-2222-2222-222222222222";
	private const string PresetUid1 = "cccccccc-3333-3333-3333-333333333333";

	private readonly LocalStorageService _storage = new("Data Source=:memory:");
	private readonly RecordingSyncApi _api = new();
	private readonly FakeCatalogResolver _catalogResolver = new();
	private readonly CapturingLogger<SettingsSyncService> _log = new();

	public void Dispose() => _storage.Dispose();

	private SettingsSyncService NewService(string token = "token-1") =>
		new(_api, _storage,
			new UserProfile { CustomerId = CustomerId, SessionToken = token },
			new PresetConfigurationBuilder(_storage), new PresetConfigurationApplier(_storage),
			_catalogResolver, _log);

	private async Task RunPullAsync(SettingsSyncService service)
	{
		await service.OnSignedInAsync(CustomerId);
		await service.PullTask;
	}

	// A well-formed macros/list item with its events embedded (no macros/get fallback).
	private static MacroPullDto MacroDto(string uid, string name, string? updatedAt = "2026-08-25 09:00:00") => new()
	{
		Uid = uid,
		VendorId = Vid,
		ProductId = Pid,
		Name = name,
		RepeatCount = 2,
		RepeatMode = (int)MacroRepeatMode.MacroPlayCount,
		Events =
		[
			new MacroEventDto { KeyCode = 0x04, IsPress = true, DelayMs = 10 },
			new MacroEventDto { KeyCode = 0x04, IsPress = false, DelayMs = 20 },
		],
		UpdatedAt = updatedAt,
	};

	private static PresetPullDto PresetDto(
		string uid, string name, JsonElement? configuration, bool? isDefault = null,
		string? description = "desc", string? updatedAt = "2026-08-25 09:00:01") => new()
	{
		Uid = uid,
		VendorId = Vid,
		ProductId = Pid,
		Name = name,
		Description = description,
		IsDefault = isDefault,
		Configuration = configuration,
		UpdatedAt = updatedAt,
	};

	// A configuration in the exact shape PresetConfigurationBuilder serializes, with macro
	// references by uid — the applier resolves them against the macros the pull inserted.
	private static JsonElement Config(string? macroUid = null) => JsonSerializer.SerializeToElement(new
	{
		rgb = new
		{
			is_enabled = true,
			selected_effect = "Neon",
			effects = new object[]
			{
				new { effect_key = "Neon", hex = "#00FF00", brightness = 55d, speed = 33d, is_rainbow = false },
			},
		},
		settings = new Dictionary<string, double> { ["dpi_stage"] = 3 },
		actuation = new object[]
		{
			new { slot = 3, press_centi_mm = 150, release_centi_mm = 160, rapid_trigger = true, quick_press_centi_mm = 20, quick_release_centi_mm = 25 },
		},
		key_bindings = new object[]
		{
			new { phys_scancode = 4, type_byte = 16, bitmask = 0, target_code = 5 },
		},
		button_bindings = new object[]
		{
			new { button = 4, action = 9, macro_uid = macroUid },
		},
		key_macro_bindings = macroUid is null
			? Array.Empty<object>()
			: [new { matrix_index = 33, macro_uid = macroUid }],
	});

	// The same shape with an extended_keys array (docs/plans/add-extended-keys-api-sync): one record
	// of each of the four modes — the pair modes carrying the null hold_key / tap_key
	// PresetConfigurationBuilder emits for them — plus one malformed item the applier must drop
	// without losing the rest.
	private static JsonElement ConfigWithExtendedKeys() => JsonSerializer.SerializeToElement(new
	{
		extended_keys = new object[]
		{
			new { mode = ExtendedKeyMode.RapidSnap, slot1 = 31, slot2 = 33, priority = 0, time_ms = 0, hold_key = (object?)null, tap_key = (object?)null },
			new { mode = ExtendedKeyMode.Socd, slot1 = 18, slot2 = 19, priority = SocdPriority.Key2, time_ms = 0, hold_key = (object?)null, tap_key = (object?)null },
			new
			{
				mode = ExtendedKeyMode.ModTap, slot1 = 30, slot2 = -1, priority = 0, time_ms = 150,
				hold_key = new { type_byte = 0x10, code1 = 0x01, code2 = 0x00 },
				tap_key = new { type_byte = 0x10, code1 = 0x00, code2 = 0x29 },
			},
			new
			{
				mode = ExtendedKeyMode.Toggle, slot1 = 20, slot2 = -1, priority = 0, time_ms = 100,
				hold_key = new { type_byte = 0x10, code1 = 0x00, code2 = 0x1A },
				tap_key = (object?)null,
			},
			// Malformed: no slot1, which is the row's primary key with preset_id.
			new { mode = ExtendedKeyMode.ModTap, time_ms = 200 },
		},
	});

	private static JsonElement FullPreferences() => JsonSerializer.SerializeToElement(new Dictionary<string, object>
	{
		[SettingsRepository.PrefSleepTimer] = 60,
		[SettingsRepository.PrefScrollLines] = 5,
		[SettingsRepository.PrefDoubleClickSpeed] = 400,
		[SettingsRepository.PrefGenericLiftOffDistance] = 1,
		[SettingsRepository.PrefScrollOneScreen] = true,
	});

	// ── Golden path + ordering ───────────────────────────────────────────────

	[Fact]
	public async Task Pull_GoldenPath_InsertsMacrosPresetPreferencesAndDeviceStateInOrder()
	{
		_api.MacroListResponse = () => Task.FromResult(new MacroListResponse
		{
			Success = true, StatusCode = 200,
			Macros = [MacroDto(MacroUid1, "M1"), MacroDto(MacroUid2, "M2")],
		});
		_api.PresetListResponse = () => Task.FromResult(new PresetListResponse
		{
			Success = true, StatusCode = 200,
			Presets = [PresetDto(PresetUid1, "P1", Config(MacroUid1))],
		});
		_api.PreferencesGetResponse = () => Task.FromResult(new PreferencesGetResponse
		{
			Success = true, StatusCode = 200, Preferences = FullPreferences(),
		});
		_api.DeviceStateGetResponse = (_, _) => Task.FromResult(new DeviceStateGetResponse
		{
			Success = true, StatusCode = 200, ActivePresetUid = PresetUid1,
		});

		var service = NewService();
		var changesRaised = 0;
		service.PullCompletedWithChanges += () => Interlocked.Increment(ref changesRaised);

		await RunPullAsync(service);

		// Order is the pull's contract: presets resolve macro uids, device state resolves
		// preset uids. Everything embedded, so no per-uid get fallbacks fire.
		Assert.Equal(new[] { "macros/list", "presets/list", "preferences/get", "devices/get", "devices/list", "notifications/list" }, _api.Endpoints);
		Assert.Equal((Vid, Pid), Assert.Single(_api.DeviceGetRequests));

		// Macros land in the unit-agnostic '' scope with the server uid and updated_at.
		var macros = _storage.GetMacros(User, OwnerId, Vid, Pid, NoKey);
		Assert.Equal(new[] { MacroUid1, MacroUid2 }, macros.Select(m => m.Uid).ToArray());
		var m1 = macros[0];
		Assert.Equal("M1", m1.Name);
		Assert.Equal(2, m1.RepeatCount);
		Assert.Equal(MacroRepeatMode.MacroPlayCount, m1.Mode);
		Assert.Equal("2026-08-25 09:00:00", m1.ServerUpdatedAt);
		Assert.Equal(2, m1.Events.Count);
		Assert.Equal((byte)0x04, m1.Events[0].KeyCode);
		Assert.True(m1.Events[0].IsPress);
		Assert.Equal(10, m1.Events[0].DelayMs);

		// The preset row carries the server identity and metadata.
		var preset = Assert.Single(_storage.GetPresets(User, OwnerId, Vid, Pid));
		Assert.Equal(PresetUid1, preset.Uid);
		Assert.Equal("P1", preset.Name);
		Assert.Equal("desc", preset.Description);
		Assert.Equal("2026-08-25 09:00:01", preset.ServerUpdatedAt);

		// Its snapshot tables are hydrated, with macro references resolved to the local id
		// of the macro this same pull inserted.
		var rgb = _storage.GetPresetRgbState(preset.Id);
		Assert.NotNull(rgb);
		Assert.True(rgb!.IsEnabled);
		Assert.Equal("Neon", rgb.SelectedEffect);
		var effect = Assert.Single(rgb.Effects);
		Assert.Equal("#00FF00", effect.Hex);
		Assert.Equal(3d, _storage.GetPresetSettings(preset.Id)["dpi_stage"]);
		var actuation = Assert.Single(_storage.GetPresetActuation(preset.Id));
		Assert.Equal(3, actuation.Slot);
		Assert.Equal(150, actuation.PressCentiMm);
		var keyBinding = Assert.Single(_storage.GetPresetKeyBindings(preset.Id));
		Assert.Equal((byte)4, keyBinding.PhysScancode);
		var buttonBinding = Assert.Single(_storage.GetPresetButtonBindings(preset.Id));
		Assert.Equal(m1.Id, buttonBinding.MacroId);
		var keyMacroBinding = Assert.Single(_storage.GetPresetKeyMacroBindings(preset.Id));
		Assert.Equal((33, m1.Id), keyMacroBinding);

		// Preferences: the owner had zero rows, so the five known keys are written
		// (ScrollOneScreenAtATime's boolean maps to 1).
		Assert.Equal(60d, _storage.GetAppPreference(User, OwnerId, SettingsRepository.PrefSleepTimer));
		Assert.Equal(5d, _storage.GetAppPreference(User, OwnerId, SettingsRepository.PrefScrollLines));
		Assert.Equal(400d, _storage.GetAppPreference(User, OwnerId, SettingsRepository.PrefDoubleClickSpeed));
		Assert.Equal(1d, _storage.GetAppPreference(User, OwnerId, SettingsRepository.PrefGenericLiftOffDistance));
		Assert.Equal(1d, _storage.GetAppPreference(User, OwnerId, SettingsRepository.PrefScrollOneScreen));

		// Device state: no local applied row existed, so the unit-agnostic row is written.
		Assert.Equal((NoKey, preset.Id), Assert.Single(_storage.GetAppliedPresets(User, OwnerId, Vid, Pid)));

		Assert.Equal(1, changesRaised);
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
	}

	// ── Additive merge: uid collision never touches the local row ────────────

	[Fact]
	public async Task Pull_UidAlreadyPresentLocally_LeavesLocalRowsUntouchedAndRaisesNoEvent()
	{
		var localMacro = new MacroRecord
		{
			Uid = MacroUid1, Name = "LOCAL-M", RepeatCount = 7, Mode = MacroRepeatMode.MacroPlayCount,
			Events = { new MacroEventRecord { KeyCode = 0x05, IsPress = true, DelayMs = 1 } },
		};
		_storage.SaveMacro(User, OwnerId, Vid, Pid, NoKey, localMacro);
		var localPreset = new PresetRecord { Uid = PresetUid1, Name = "LOCAL-P" };
		_storage.InsertPreset(User, OwnerId, Vid, Pid, localPreset);
		_storage.SetKeyBinding(User, OwnerId, Vid, Pid, NoKey, 0x04, 0x10, 0, 0x05);
		_storage.SavePresetSnapshot(localPreset.Id, User, OwnerId, Vid, Pid, NoKey);
		var builder = new PresetConfigurationBuilder(_storage);
		var snapshotBefore = JsonSerializer.Serialize(builder.Build(localPreset.Id, new Dictionary<int, string>()));

		// The server has records with the SAME uids but different content.
		_api.MacroListResponse = () => Task.FromResult(new MacroListResponse
		{
			Success = true, StatusCode = 200, Macros = [MacroDto(MacroUid1, "SERVER-M")],
		});
		_api.PresetListResponse = () => Task.FromResult(new PresetListResponse
		{
			Success = true, StatusCode = 200, Presets = [PresetDto(PresetUid1, "SERVER-P", Config())],
		});

		var service = NewService();
		var changesRaised = 0;
		service.PullCompletedWithChanges += () => Interlocked.Increment(ref changesRaised);

		await RunPullAsync(service);

		// Both collided uids skip before any fallback get; the type still gets a devices/get.
		Assert.Equal(new[] { "macros/list", "presets/list", "preferences/get", "devices/get", "devices/list", "notifications/list" }, _api.Endpoints);

		var macro = Assert.Single(_storage.GetMacros(User, OwnerId, Vid, Pid, NoKey));
		Assert.Equal("LOCAL-M", macro.Name);
		Assert.Equal(7, macro.RepeatCount);
		Assert.Null(macro.ServerUpdatedAt);

		var preset = Assert.Single(_storage.GetPresets(User, OwnerId, Vid, Pid));
		Assert.Equal("LOCAL-P", preset.Name);
		Assert.Null(preset.ServerUpdatedAt);
		// Byte-for-byte: the snapshot serializes identically before and after the pull.
		Assert.Equal(snapshotBefore,
			JsonSerializer.Serialize(builder.Build(preset.Id, new Dictionary<int, string>())));

		// Nothing was written, so the reload event never fires.
		Assert.Equal(0, changesRaised);
	}

	// ── Pending tombstones: the pull must not resurrect local deletes ────────

	// A synced row deleted locally leaves a tombstone until the coordinator pushes the
	// delete — and that tombstone pass runs AFTER this pull (SyncCompleted is its trigger),
	// so the server still lists the uid. Re-inserting it would resurrect the deleted record
	// permanently: the coordinator would then delete it server-side and drop the tombstone,
	// leaving the local copy clean, synced-looking and never reconciled again.
	[Fact]
	public async Task Pull_UidWithPendingTombstone_SkipsTheItemAndKeepsTheTombstone()
	{
		var macro = new MacroRecord
		{
			Uid = MacroUid1, Name = "M-DELETED", RepeatCount = 1,
			Events = { new MacroEventRecord { KeyCode = 0x04, IsPress = true, DelayMs = 1 } },
		};
		_storage.SaveMacro(User, OwnerId, Vid, Pid, NoKey, macro);
		_storage.SetMacroServerUpdatedAt(macro.Id, "m-ts");
		_storage.DeleteMacro(macro.Id);
		var preset = new PresetRecord { Uid = PresetUid1, Name = "P-DELETED" };
		_storage.InsertPreset(User, OwnerId, Vid, Pid, preset);
		_storage.SetPresetServerUpdatedAt(preset.Id, "p-ts");
		_storage.DeletePreset(preset.Id);

		// The server still lists both records — their deletes have not been pushed yet.
		_api.MacroListResponse = () => Task.FromResult(new MacroListResponse
		{
			Success = true, StatusCode = 200, Macros = [MacroDto(MacroUid1, "SERVER-M")],
		});
		_api.PresetListResponse = () => Task.FromResult(new PresetListResponse
		{
			Success = true, StatusCode = 200, Presets = [PresetDto(PresetUid1, "SERVER-P", Config())],
		});

		var service = NewService();
		await RunPullAsync(service);

		// The rows stay deleted, and the tombstones stay pending for the coordinator's
		// delete push.
		Assert.Empty(_storage.GetMacros(User, OwnerId, Vid, Pid, NoKey));
		Assert.Empty(_storage.GetPresets(User, OwnerId, Vid, Pid));
		Assert.Equal(MacroUid1, Assert.Single(
			_storage.GetSyncTombstones(User, OwnerId, LocalStorageService.TombstoneKindMacro)).Uid);
		Assert.Equal(PresetUid1, Assert.Single(
			_storage.GetSyncTombstones(User, OwnerId, LocalStorageService.TombstoneKindPreset)).Uid);
	}

	// The interleaved case (PR #120 review, M2). Ordering alone is no longer the whole defence:
	// the coordinator stopped self-signalling from its constructor, so a reconcile now arrives
	// via SyncCompleted after this pull — but a pass triggered by a concurrent local edit can
	// still push a delete and drop the tombstone while this pull holds a list response captured
	// before that delete reached the server. What keeps the record dead is the tombstone
	// snapshot taken before the first list call. The deleted records must still not come back:
	// a re-inserted row is markDirty:false with a server_updated_at, i.e. "clean and synced",
	// so it would never be pushed, deleted or reconciled again.
	[Fact]
	public async Task Pull_TombstoneRemovedWhileTheListCallIsInFlight_DoesNotResurrectTheRecord()
	{
		var macro = new MacroRecord
		{
			Uid = MacroUid1, Name = "M-DELETED", RepeatCount = 1,
			Events = { new MacroEventRecord { KeyCode = 0x04, IsPress = true, DelayMs = 1 } },
		};
		_storage.SaveMacro(User, OwnerId, Vid, Pid, NoKey, macro);
		_storage.SetMacroServerUpdatedAt(macro.Id, "m-ts");
		_storage.DeleteMacro(macro.Id);
		var preset = new PresetRecord { Uid = PresetUid1, Name = "P-DELETED" };
		_storage.InsertPreset(User, OwnerId, Vid, Pid, preset);
		_storage.SetPresetServerUpdatedAt(preset.Id, "p-ts");
		_storage.DeletePreset(preset.Id);

		// The server's list was built before the deletes landed, so it still names both uids.
		using var gate = new SemaphoreSlim(0);
		var listCallParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_api.MacroListResponse = async () =>
		{
			listCallParked.SetResult();
			await gate.WaitAsync();
			return new MacroListResponse
			{
				Success = true, StatusCode = 200, Macros = [MacroDto(MacroUid1, "SERVER-M")],
			};
		};
		_api.PresetListResponse = () => Task.FromResult(new PresetListResponse
		{
			Success = true, StatusCode = 200, Presets = [PresetDto(PresetUid1, "SERVER-P", Config())],
		});

		var service = NewService();
		await service.OnSignedInAsync(CustomerId);
		await listCallParked.Task;

		// The concurrent coordinator pass: both deletes confirmed server-side, so it drops
		// the tombstones (SyncCoordinator.RemoveTombstoneAsync) — while this pull is parked.
		_storage.RemoveSyncTombstone(User, OwnerId, LocalStorageService.TombstoneKindMacro, MacroUid1);
		_storage.RemoveSyncTombstone(User, OwnerId, LocalStorageService.TombstoneKindPreset, PresetUid1);

		gate.Release();
		await service.PullTask;

		// The user deleted these records and the server has already agreed; the stale list
		// must not put them back.
		Assert.Empty(_storage.GetMacros(User, OwnerId, Vid, Pid, NoKey));
		Assert.Empty(_storage.GetPresets(User, OwnerId, Vid, Pid));
	}

	// ── Default-flag arbitration ─────────────────────────────────────────────

	[Fact]
	public async Task Pull_ServerDefaultWhenLocalDefaultExists_KeepsTheLocalDefault()
	{
		var localDefault = new PresetRecord { Name = "LOCAL-DEFAULT", IsDefault = true };
		_storage.InsertPreset(User, OwnerId, Vid, Pid, localDefault);
		// Synced: local-wins applies only to a default the server has confirmed — a
		// never-synced local default is superseded instead
		// (docs/plans/fix-duplicate-default-preset).
		_storage.SetPresetServerUpdatedAt(localDefault.Id, "2026-08-28 10:00:00");
		_api.PresetListResponse = () => Task.FromResult(new PresetListResponse
		{
			Success = true, StatusCode = 200,
			Presets = [PresetDto(PresetUid1, "SERVER-P", Config(), isDefault: true)],
		});

		await RunPullAsync(NewService());

		var presets = _storage.GetPresets(User, OwnerId, Vid, Pid);
		Assert.Equal(2, presets.Count);
		// Exactly one default, and it is the local one — the server flag was dropped.
		var theDefault = Assert.Single(presets, p => p.IsDefault);
		Assert.Equal("LOCAL-DEFAULT", theDefault.Name);
	}

	[Fact]
	public async Task Pull_ServerDefaultWhenTypeHasNoLocalDefault_HonorsTheServerFlag()
	{
		_api.PresetListResponse = () => Task.FromResult(new PresetListResponse
		{
			Success = true, StatusCode = 200,
			Presets = [PresetDto(PresetUid1, "SERVER-P", Config(), isDefault: true)],
		});

		await RunPullAsync(NewService());

		var preset = Assert.Single(_storage.GetPresets(User, OwnerId, Vid, Pid));
		Assert.True(preset.IsDefault);
	}

	// THE BUG (docs/plans/fix-duplicate-default-preset): CreateDefaultPresetIfNotExists seeded
	// a default over a scope that was only "empty" because this pull had not landed yet. The
	// seeded row carries a fresh uid the pull's dedup cannot connect to the server's default,
	// so pre-fix BOTH rows survived — and the coordinator then pushed the seed, minting one
	// server-side duplicate default per DB wipe. A never-synced (server_updated_at IS NULL)
	// local default is superseded: the row and its snapshot are deleted with NO tombstone
	// (the server has nothing to remove), and the server's preset carries the flag.
	[Fact]
	public async Task Pull_ServerDefault_SupersedesTheNeverSyncedSeededLocalDefault()
	{
		// Seed-shaped local default: exactly what CreateDefaultPresetIfNotExists mints
		// (is_default = 1, fresh uid, server_updated_at NULL), plus a snapshot copied off
		// live device state — the preset_rgb rows prove the cascade below.
		var (localId, seeded) = _storage.CreateDefaultPresetIfNotExists(
			User, OwnerId, Vid, Pid, "За замовчуванням");
		Assert.True(seeded);
		_storage.InsertPresetRgbState(localId, isEnabled: true, "Neon",
			[new RgbEffectRecord { EffectKey = "Neon", Hex = "#FF0000", Brightness = 50, Speed = 50, IsRainbow = false }]);

		_api.PresetListResponse = () => Task.FromResult(new PresetListResponse
		{
			Success = true, StatusCode = 200,
			Presets = [PresetDto(PresetUid1, "Default", Config(), isDefault: true)],
		});

		await RunPullAsync(NewService());

		// The scope ends with exactly ONE preset — the server's, carrying the default flag.
		var preset = Assert.Single(_storage.GetPresets(User, OwnerId, Vid, Pid));
		Assert.Equal(PresetUid1, preset.Uid);
		Assert.True(preset.IsDefault);

		// The seeded row's snapshot cascaded with it.
		Assert.Null(_storage.GetPresetRgbState(localId));

		// Never synced ⇒ nothing to remove server-side ⇒ no tombstone.
		Assert.Empty(_storage.GetSyncTombstones(User, OwnerId, LocalStorageService.TombstoneKindPreset));
	}

	// ── DeleteNeverSyncedPreset guard (driven directly) ──────────────────────

	[Fact]
	public void DeleteNeverSyncedPreset_NeverSyncedRow_DeletesRowAndSnapshotWithoutTombstone()
	{
		var preset = new PresetRecord { Name = "SEEDED", IsDefault = true };
		_storage.InsertPreset(User, OwnerId, Vid, Pid, preset);
		_storage.InsertPresetRgbState(preset.Id, isEnabled: true, "Neon",
			[new RgbEffectRecord { EffectKey = "Neon", Hex = "#FF0000", Brightness = 50, Speed = 50, IsRainbow = false }]);

		Assert.True(_storage.DeleteNeverSyncedPreset(preset.Id));

		Assert.Empty(_storage.GetPresets(User, OwnerId, Vid, Pid));
		Assert.Null(_storage.GetPresetRgbState(preset.Id));
		Assert.Empty(_storage.GetSyncTombstones(User, OwnerId, LocalStorageService.TombstoneKindPreset));
	}

	// The race the in-transaction guard exists for: the coordinator's push synced the row
	// between the pull's arbitration read and the delete. The row is server data now — the
	// delete must refuse and leave it (the pull's local-wins rule then keeps it).
	[Fact]
	public void DeleteNeverSyncedPreset_RowSyncedBetweenArbitrationAndDelete_RefusesAndKeepsTheRow()
	{
		var preset = new PresetRecord { Name = "SEEDED", IsDefault = true };
		_storage.InsertPreset(User, OwnerId, Vid, Pid, preset);
		_storage.SetPresetServerUpdatedAt(preset.Id, "2026-08-28 10:00:00");

		Assert.False(_storage.DeleteNeverSyncedPreset(preset.Id));

		var kept = Assert.Single(_storage.GetPresets(User, OwnerId, Vid, Pid));
		Assert.Equal(preset.Id, kept.Id);
		Assert.True(kept.IsDefault);
	}

	// server_updated_at IS NULL alone also matches rows the server DOES hold: a coordinator
	// create acked without an echoed updated_at clears dirty but leaves the stamp NULL, and
	// a pull-inserted row is born clean with a NULL stamp when the server omits updated_at.
	// Both shapes are clean (is_dirty = 0) — the guard's is_dirty = 1 half must refuse them.
	[Fact]
	public void DeleteNeverSyncedPreset_CleanRowWithNullServerUpdatedAt_RefusesAndKeepsTheRow()
	{
		// markDirty: false mints exactly the pull-inserted shape: clean, stamp NULL.
		var preset = new PresetRecord { Name = "SERVER-SHAPED", IsDefault = true };
		_storage.InsertPreset(User, OwnerId, Vid, Pid, preset, markDirty: false);

		Assert.False(_storage.DeleteNeverSyncedPreset(preset.Id));

		var kept = Assert.Single(_storage.GetPresets(User, OwnerId, Vid, Pid));
		Assert.Equal(preset.Id, kept.Id);
	}

	// ── Preferences zero-rows gate ───────────────────────────────────────────

	// The zero-rows → write direction is the golden path; this is the blocking direction:
	// one existing local row means the cloud copy must not touch ANY preference.
	[Fact]
	public async Task Pull_OwnerHasAPreferenceRow_WritesNoPreferences()
	{
		_storage.SetAppPreference(User, OwnerId, SettingsRepository.PrefScrollLines, 7);
		_api.PreferencesGetResponse = () => Task.FromResult(new PreferencesGetResponse
		{
			Success = true, StatusCode = 200, Preferences = FullPreferences(),
		});

		await RunPullAsync(NewService());

		Assert.Equal(7d, _storage.GetAppPreference(User, OwnerId, SettingsRepository.PrefScrollLines));
		Assert.Null(_storage.GetAppPreference(User, OwnerId, SettingsRepository.PrefSleepTimer));
		Assert.Null(_storage.GetAppPreference(User, OwnerId, SettingsRepository.PrefDoubleClickSpeed));
		Assert.Null(_storage.GetAppPreference(User, OwnerId, SettingsRepository.PrefGenericLiftOffDistance));
		Assert.Null(_storage.GetAppPreference(User, OwnerId, SettingsRepository.PrefScrollOneScreen));
	}

	// ── "preferences": [] quirk ──────────────────────────────────────────────

	// The contract's documented empty-array quirk means "no preferences on the server":
	// nothing is written, nothing throws, and nothing is logged at Warning or above.
	[Fact]
	public async Task Pull_PreferencesEmptyArrayQuirk_IsToleratedSilently()
	{
		_api.PreferencesGetResponse = () => Task.FromResult(new PreferencesGetResponse
		{
			Success = true, StatusCode = 200,
			Preferences = JsonSerializer.SerializeToElement(Array.Empty<object>()),
		});

		await RunPullAsync(NewService());

		Assert.Null(_storage.GetAppPreference(User, OwnerId, SettingsRepository.PrefScrollLines));
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
	}

	// ── Applied-preset gate ──────────────────────────────────────────────────

	// The write direction is the golden path; here ANY existing local applied row (even a
	// unit-specific one) blocks the pulled active preset from being applied.
	[Fact]
	public async Task Pull_LocalAppliedRowExists_LeavesAppliedPresetAloneAndSetsNoPendingApplyMark()
	{
		var localPreset = new PresetRecord { Name = "LOCAL-P" };
		_storage.InsertPreset(User, OwnerId, Vid, Pid, localPreset);
		_storage.SetAppliedPreset(User, OwnerId, Vid, Pid, "unit-a", localPreset.Id);
		_api.PresetListResponse = () => Task.FromResult(new PresetListResponse
		{
			Success = true, StatusCode = 200, Presets = [PresetDto(PresetUid1, "SERVER-P", Config())],
		});
		_api.DeviceStateGetResponse = (_, _) => Task.FromResult(new DeviceStateGetResponse
		{
			Success = true, StatusCode = 200, ActivePresetUid = PresetUid1,
		});

		await RunPullAsync(NewService());

		Assert.Equal(("unit-a", localPreset.Id), Assert.Single(_storage.GetAppliedPresets(User, OwnerId, Vid, Pid)));
		// Neither half of the write happened: nothing was adopted, so nothing is waiting to be
		// materialized (docs/plans/add-extended-keys-api-sync).
		Assert.Null(_storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaDeviceStatePendingApply(Vid, Pid)));
	}

	// ── Extended keys through the pull (docs/plans/add-extended-keys-api-sync) ──

	// A pulled preset whose configuration carries extended_keys lands preset_extended_keys rows —
	// all four modes, with the pair modes' null codes read back as "no output" — and one malformed
	// item is skipped rather than failing the preset or the pull.
	[Fact]
	public async Task Pull_ConfigurationWithExtendedKeys_LandsARowPerModeAndSkipsTheMalformedItem()
	{
		_api.PresetListResponse = () => Task.FromResult(new PresetListResponse
		{
			Success = true, StatusCode = 200, Presets = [PresetDto(PresetUid1, "P1", ConfigWithExtendedKeys())],
		});

		await RunPullAsync(NewService());

		var preset = Assert.Single(_storage.GetPresets(User, OwnerId, Vid, Pid));
		var rows = _storage.GetPresetExtendedKeys(preset.Id);
		Assert.Equal(4, rows.Count);
		Assert.Contains(ExtendedKeyRecord.RapidSnap(31, 33), rows);
		Assert.Contains(ExtendedKeyRecord.Socd(18, 19, SocdPriority.Key2), rows);
		Assert.Contains(ExtendedKeyRecord.ModTap(30, 150, new ExtendedKeyCodeRecord(0x10, 0x01, 0x00), new ExtendedKeyCodeRecord(0x10, 0x00, 0x29)), rows);
		Assert.Contains(ExtendedKeyRecord.Toggle(20, 100, new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A)), rows);
		// The dropped item is the only one that would have carried a 200 ms threshold.
		Assert.DoesNotContain(rows, r => r.TimeMs == 200);
		// A pair's null codes are "none", never something invented from the null.
		foreach (var pair in rows.Where(r => r.IsPair))
		{
			Assert.Equal(ExtendedKeyCodeRecord.None, pair.HoldKey);
			Assert.Equal(ExtendedKeyCodeRecord.None, pair.TapKey);
		}
	}

	// The applied row alone never reaches the board: the pull holds no unit, so it also leaves the
	// pending-apply mark the first path WITH a unit consumes (the details page, or a mouse connect).
	// The mark must not read as a dirty device state — that would push it straight back out.
	[Fact]
	public async Task Pull_DeviceStateNamesAPulledPreset_WritesTheAppliedRowAndThePendingApplyMark()
	{
		_api.PresetListResponse = () => Task.FromResult(new PresetListResponse
		{
			Success = true, StatusCode = 200, Presets = [PresetDto(PresetUid1, "P1", ConfigWithExtendedKeys())],
		});
		_api.DeviceStateGetResponse = (_, _) => Task.FromResult(new DeviceStateGetResponse
		{
			Success = true, StatusCode = 200, ActivePresetUid = PresetUid1,
		});

		await RunPullAsync(NewService());

		var preset = Assert.Single(_storage.GetPresets(User, OwnerId, Vid, Pid));
		Assert.Equal((NoKey, preset.Id), Assert.Single(_storage.GetAppliedPresets(User, OwnerId, Vid, Pid)));
		// The mark names the adopted preset, so a consumer can tell it from one the user has since
		// replaced by hand (review round 1, S3).
		Assert.Equal(LocalStorageService.SyncMetaPendingApplyValue(preset.Id),
			_storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaDeviceStatePendingApply(Vid, Pid)));
		Assert.Null(_storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaDeviceStateDirty(Vid, Pid)));
	}

	// ── Unit-keyed visibility of pulled '' rows (review round 1, B1) ─────────

	// Pulled macros and the pulled applied-preset row land in the unit-agnostic '' scope;
	// unit-keyed readers must surface them, or on hardware with a readable Container ID the
	// pulled macro library is invisible and the cloud active preset never auto-applies.
	[Fact]
	public async Task Pull_SharedScopeRows_AreVisibleToUnitKeyedReaders()
	{
		// A macro this unit already had — the union must add the pulled '' macros to it,
		// not replace it, and must not leak it to other units.
		var unitMacro = new MacroRecord
		{
			Uid = MacroUid2, Name = "UNIT-M", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount,
			Events = { new MacroEventRecord { KeyCode = 0x05, IsPress = true, DelayMs = 1 } },
		};
		_storage.SaveMacro(User, OwnerId, Vid, Pid, "unit-a", unitMacro);

		_api.MacroListResponse = () => Task.FromResult(new MacroListResponse
		{
			Success = true, StatusCode = 200, Macros = [MacroDto(MacroUid1, "M1")],
		});
		_api.PresetListResponse = () => Task.FromResult(new PresetListResponse
		{
			Success = true, StatusCode = 200, Presets = [PresetDto(PresetUid1, "P1", Config(MacroUid1))],
		});
		_api.DeviceStateGetResponse = (_, _) => Task.FromResult(new DeviceStateGetResponse
		{
			Success = true, StatusCode = 200, ActivePresetUid = PresetUid1,
		});

		await RunPullAsync(NewService());

		// The unit's library is the union of its own scope and the pulled '' scope...
		Assert.Equal(new[] { MacroUid2, MacroUid1 },
			_storage.GetMacros(User, OwnerId, Vid, Pid, "unit-a").Select(m => m.Uid).ToArray());
		// ...while per-unit isolation holds: another unit sees only the '' macros.
		Assert.Equal(new[] { MacroUid1 },
			_storage.GetMacros(User, OwnerId, Vid, Pid, "unit-b").Select(m => m.Uid).ToArray());

		// The connect path resolves the pulled '' applied row for any unit key...
		var pulled = Assert.Single(_storage.GetPresets(User, OwnerId, Vid, Pid));
		Assert.Equal(pulled.Id, _storage.GetAppliedPreset(User, OwnerId, Vid, Pid, "unit-a"));

		// ...until a unit applies its own choice — the exact-key row then wins there while
		// the '' row keeps serving every other unit.
		var local = new PresetRecord { Name = "UNIT-P" };
		_storage.InsertPreset(User, OwnerId, Vid, Pid, local);
		_storage.SetAppliedPreset(User, OwnerId, Vid, Pid, "unit-a", local.Id);
		Assert.Equal(local.Id, _storage.GetAppliedPreset(User, OwnerId, Vid, Pid, "unit-a"));
		Assert.Equal(pulled.Id, _storage.GetAppliedPreset(User, OwnerId, Vid, Pid, "unit-b"));
	}

	// ── Server-value tolerance (review round 1 nits) ─────────────────────────

	// An event whose key code cannot fit a byte is skipped, not wrapped: a server value of
	// 300 must not silently become key 44 in the stored macro, and a negative value must
	// not underflow either. In-range events around it still land.
	[Fact]
	public async Task Pull_OutOfRangeKeyCode_SkipsTheEventInsteadOfWrapping()
	{
		var dto = MacroDto(MacroUid1, "M1");
		dto.Events =
		[
			new MacroEventDto { KeyCode = 300, IsPress = true, DelayMs = 10 },
			new MacroEventDto { KeyCode = -1, IsPress = true, DelayMs = 10 },
			new MacroEventDto { KeyCode = 0x04, IsPress = true, DelayMs = 10 },
		];
		_api.MacroListResponse = () => Task.FromResult(new MacroListResponse
		{
			Success = true, StatusCode = 200, Macros = [dto],
		});

		await RunPullAsync(NewService());

		var macro = Assert.Single(_storage.GetMacros(User, OwnerId, Vid, Pid, NoKey));
		Assert.Equal((byte)0x04, Assert.Single(macro.Events).KeyCode);
	}

	// An out-of-enum repeat mode (the server's accepted range is undocumented — plan Open
	// question 3) falls back to play-count, while a valid non-default mode in the same
	// pull is preserved as-is.
	[Fact]
	public async Task Pull_OutOfEnumRepeatMode_FallsBackToPlayCountWithoutFlatteningValidModes()
	{
		var invalid = MacroDto(MacroUid1, "M-INVALID");
		invalid.RepeatMode = 99;
		var valid = MacroDto(MacroUid2, "M-VALID");
		valid.RepeatMode = (int)MacroRepeatMode.MacroWhileHeld;
		_api.MacroListResponse = () => Task.FromResult(new MacroListResponse
		{
			Success = true, StatusCode = 200, Macros = [invalid, valid],
		});

		await RunPullAsync(NewService());

		var macros = _storage.GetMacros(User, OwnerId, Vid, Pid, NoKey);
		Assert.Equal(MacroRepeatMode.MacroPlayCount, macros.Single(m => m.Uid == MacroUid1).Mode);
		Assert.Equal(MacroRepeatMode.MacroWhileHeld, macros.Single(m => m.Uid == MacroUid2).Mode);
	}

	// ── Network-error abort ──────────────────────────────────────────────────

	[Fact]
	public async Task Pull_NetworkErrorOnFirstCall_AbortsWithoutFurtherCallsOrThrow()
	{
		_api.MacroListResponse = () => Task.FromResult(new MacroListResponse { IsNetworkError = true });

		var service = NewService();
		// Completing both awaits without throwing IS the "never surfaces" assertion.
		await service.OnSignedInAsync(CustomerId);
		await service.PullTask;

		Assert.Equal(new[] { "macros/list" }, _api.Endpoints);
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Error);
	}

	// ── Token-rejection abort ────────────────────────────────────────────────

	// docs/plans/add-global-token-expiry: the shared Aborts helper gates pulls too — a
	// token-rejection envelope stops the pipeline at the rejected call, warns, and never
	// throws or logs at Error.
	[Fact]
	public async Task Pull_TokenRejectedOnFirstCall_AbortsWithoutFurtherCalls()
	{
		_api.MacroListResponse = () => Task.FromResult(new MacroListResponse
		{
			Success = false, StatusCode = 401,
			ErrorCode = AuthErrorCodes.InvalidOrExpiredToken,
		});

		await RunPullAsync(NewService());

		Assert.Equal(new[] { "macros/list" }, _api.Endpoints);
		Assert.Contains(_log.Entries,
			e => e.Level == LogLevel.Warning && e.Message.Contains("aborted at macros/list"));
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Error);
	}

	// ── Synchronous-return criterion ─────────────────────────────────────────

	// OnSignedInAsync must complete without waiting for any storage read or HTTP call: the
	// fake blocks the first API call on a gate, and the method has already returned — with
	// the pull visibly still in flight — before the gate opens.
	[Fact]
	public async Task OnSignedInAsync_ReturnsWhileTheFirstApiCallIsStillBlocked()
	{
		using var gate = new SemaphoreSlim(0);
		_api.MacroListResponse = async () =>
		{
			await gate.WaitAsync();
			return new MacroListResponse { Success = true, StatusCode = 200, Macros = [] };
		};

		var service = NewService();
		var returned = service.OnSignedInAsync(CustomerId);

		Assert.True(returned.IsCompleted);
		// The pull cannot have finished: its first call is parked on the gate.
		Assert.False(service.PullTask.IsCompleted);

		gate.Release();
		await service.PullTask;
		Assert.Equal(new[] { "macros/list", "presets/list", "preferences/get", "devices/list", "notifications/list" }, _api.Endpoints);
	}

	// A sign-out/re-sign-in while a pull is in flight chains the second pull after the
	// first (review round 1 nit) instead of racing it: with the first pull parked on its
	// opening HTTP call, a second OnSignedInAsync schedules but issues nothing — and once
	// released, the endpoint log shows two complete, non-interleaved pipelines with
	// nothing logged at Error.
	[Fact]
	public async Task OnSignedInAsync_PullStillInFlight_ChainsTheNewPullInsteadOfRacingIt()
	{
		using var gate = new SemaphoreSlim(0);
		var firstCallParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var calls = 0;
		_api.MacroListResponse = async () =>
		{
			if (Interlocked.Increment(ref calls) == 1)
			{
				firstCallParked.SetResult();
				await gate.WaitAsync();
			}
			return new MacroListResponse { Success = true, StatusCode = 200, Macros = [] };
		};

		var service = NewService();
		await service.OnSignedInAsync(CustomerId);
		await firstCallParked.Task;

		await service.OnSignedInAsync(CustomerId);
		// The second pull is scheduled, not started: the only call so far is the parked one.
		Assert.Equal(new[] { "macros/list" }, _api.Endpoints);
		Assert.Contains(_log.Entries, e =>
			e.Level == LogLevel.Information && e.Message.Contains("previous pull is still running"));

		gate.Release();
		await service.PullTask;

		Assert.Equal(
			new[]
			{
				"macros/list", "presets/list", "preferences/get", "devices/list", "notifications/list",
				"macros/list", "presets/list", "preferences/get", "devices/list", "notifications/list",
			},
			_api.Endpoints);
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Error);
	}

	// ── Guest / no-token no-op ───────────────────────────────────────────────

	// A guest session (or any state without a session token) never pulls: the method exits
	// before scheduling anything (PullTask stays completed) and no API call is made.
	[Fact]
	public async Task OnSignedInAsync_EmptySessionToken_MakesZeroApiCalls()
	{
		var service = NewService(token: "");

		await service.OnSignedInAsync(CustomerId);

		Assert.True(service.PullTask.IsCompleted);
		Assert.Empty(_api.Endpoints);
		Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("skipped"));
	}

	// ── Per-item binding failure ─────────────────────────────────────────────

	// An item that fails DTO binding (null name / null uid) is skipped with a warning while
	// the rest of the list — and the rest of the pipeline — proceeds.
	[Fact]
	public async Task Pull_ItemThatFailsBinding_IsSkippedWhileTheRestProceeds()
	{
		_api.MacroListResponse = () => Task.FromResult(new MacroListResponse
		{
			Success = true, StatusCode = 200,
			Macros = [new MacroPullDto { Uid = "no-name-uid", VendorId = Vid, ProductId = Pid }, MacroDto(MacroUid1, "M1")],
		});
		_api.PresetListResponse = () => Task.FromResult(new PresetListResponse
		{
			Success = true, StatusCode = 200,
			Presets = [new PresetPullDto { Name = "NO-UID", VendorId = Vid, ProductId = Pid }, PresetDto(PresetUid1, "P1", Config())],
		});

		await RunPullAsync(NewService());

		Assert.Equal(new[] { "macros/list", "presets/list", "preferences/get", "devices/get", "devices/list", "notifications/list" }, _api.Endpoints);
		Assert.Equal(MacroUid1, Assert.Single(_storage.GetMacros(User, OwnerId, Vid, Pid, NoKey)).Uid);
		Assert.Equal(PresetUid1, Assert.Single(_storage.GetPresets(User, OwnerId, Vid, Pid)).Uid);
		Assert.Equal(2, _log.Entries.Count(
			e => e.Level == LogLevel.Warning && e.Message.Contains("did not bind")));
	}

	// ── Missing-payload fallback to per-uid get ──────────────────────────────

	// The list shapes are undocumented (plan Open question 1): a macro without events and a
	// preset without configuration each trigger a per-uid get, whose payload and metadata
	// fill in what the list omitted.
	[Fact]
	public async Task Pull_ListWithoutPayload_FallsBackToPerUidGet()
	{
		_api.MacroListResponse = () => Task.FromResult(new MacroListResponse
		{
			Success = true, StatusCode = 200,
			Macros = [new MacroPullDto { Uid = MacroUid1, VendorId = Vid, ProductId = Pid, Name = "M1" }],
		});
		_api.MacroGetResponse = uid => Task.FromResult(new MacroGetResponse
		{
			Success = true, StatusCode = 200, Macro = MacroDto(uid, "M1"),
		});
		_api.PresetListResponse = () => Task.FromResult(new PresetListResponse
		{
			Success = true, StatusCode = 200,
			Presets = [PresetDto(PresetUid1, "P1", configuration: null, description: null, updatedAt: null)],
		});
		_api.PresetGetResponse = uid => Task.FromResult(new PresetGetResponse
		{
			Success = true, StatusCode = 200,
			Preset = PresetDto(uid, "P1", Config(MacroUid1), description: "from-get", updatedAt: "2026-08-25 09:00:02"),
		});

		await RunPullAsync(NewService());

		Assert.Equal(
			new[] { "macros/list", "macros/get", "presets/list", "presets/get", "preferences/get", "devices/get", "devices/list", "notifications/list" },
			_api.Endpoints);
		Assert.Equal(MacroUid1, Assert.Single(_api.MacroGetUids));
		Assert.Equal(PresetUid1, Assert.Single(_api.PresetGetUids));

		var macro = Assert.Single(_storage.GetMacros(User, OwnerId, Vid, Pid, NoKey));
		Assert.Equal(2, macro.Events.Count);
		Assert.Equal("2026-08-25 09:00:00", macro.ServerUpdatedAt);

		var preset = Assert.Single(_storage.GetPresets(User, OwnerId, Vid, Pid));
		Assert.Equal("from-get", preset.Description);
		Assert.Equal("2026-08-25 09:00:02", preset.ServerUpdatedAt);
		// The get's configuration was applied, macro reference resolved.
		Assert.Equal(macro.Id, Assert.Single(_storage.GetPresetButtonBindings(preset.Id)).MacroId);
	}

	// ── Device roster step (docs/plans/add-api-v21-device-roster) ────────────
	// The pull's final step: devices/list, hydrating an offline card for a server-rostered
	// model this machine has never seen (catalog lookup through IDeviceCatalogResolver) and
	// reconcile-marking local cards the server list lacks. Best-effort — a non-success list
	// skips the step, never fails the pull.

	private static DeviceRecord UserMouse(string name = "PG Taipan") => new()
	{
		Name = name,
		DeviceTypeLabel = "Wireless Mouse",
		CardType = "single",
		VendorId = Vid,
		ProductId = Pid,
	};

	private static DeviceListResponse RosterList(params DeviceListItemDto?[] items) => new()
	{
		Success = true, StatusCode = 200, Devices = [.. items],
	};

	[Fact]
	public async Task Pull_RosterListsUnknownModel_HydratesAnOfflineCardFromTheCatalog()
	{
		_api.DeviceListResponse = () => Task.FromResult(RosterList(
			new DeviceListItemDto { VendorId = Vid, ProductId = Pid, Name = "PG Taipan" }));
		_catalogResolver.Response = (_, _) => new DeviceCatalogEntry
		{
			Name = "PG TAIPAN",
			DeviceType = "Wireless Mouse",
			ImagePath = "avares://ProoveGaming.App/Assets/Devices/taipan.svg",
			ImageScale = 1.2,
			// 6 of the 10 catalog models are double-cards, and an offline card never meets
			// the discovery refill — the catalog's card type must survive hydration
			// (PR #127, m3).
			CardType = "double",
		};
		var service = NewService();
		var changesRaised = 0;
		service.PullCompletedWithChanges += () => Interlocked.Increment(ref changesRaised);
		var syncSignals = 0;
		_storage.SyncRelevantDataChanged += () => Interlocked.Increment(ref syncSignals);

		await RunPullAsync(service);

		Assert.Equal((Vid, Pid), Assert.Single(_catalogResolver.Lookups));
		var row = Assert.Single(_storage.GetDevices(User, OwnerId));
		// The server's name wins over the catalog's; visuals come from the catalog entry.
		Assert.Equal("PG Taipan", row.Name);
		Assert.Equal("Wireless Mouse", row.DeviceTypeLabel);
		Assert.Equal("avares://ProoveGaming.App/Assets/Devices/taipan.svg", row.ImagePath);
		Assert.Equal(1.2, row.ImageScale);
		Assert.Equal("double", row.CardType);
		Assert.Equal("", row.DeviceKey);
		Assert.Equal((Vid, Pid), (row.SettingsVendorId, row.SettingsProductId));
		// The row IS the server's roster: hydrating it must not re-mark it for a push.
		Assert.Equal(0, syncSignals);
		Assert.Null(_storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaDeviceRosterAdd(Vid, Pid)));
		// A local record was written, so the UI reload event fired.
		Assert.Equal(1, changesRaised);
	}

	// DeviceHost down or a model this build's catalog does not know: skip with a log line,
	// the rest of the pull is unaffected.
	[Fact]
	public async Task Pull_RosterModelUnknownToTheCatalog_SkipsWithoutFailingThePull()
	{
		_api.DeviceListResponse = () => Task.FromResult(RosterList(
			new DeviceListItemDto { VendorId = Vid, ProductId = Pid, Name = "PG Taipan" }));
		// _catalogResolver default: every lookup answers null.

		await RunPullAsync(NewService());

		Assert.Equal((Vid, Pid), Assert.Single(_catalogResolver.Lookups));
		Assert.Empty(_storage.GetDevices(User, OwnerId));
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Error);
	}

	// The reconcile that backfills pre-v2.1 accounts: a local card the server list lacks
	// gets an .add mark, silently — the coordinator pass that follows SyncCompleted pushes it.
	[Fact]
	public async Task Pull_LocalCardAbsentFromServerList_GetsAnAddMarkWithoutSignalling()
	{
		_storage.AddDevice(User, OwnerId, UserMouse());
		// Isolate the reconcile from the mark the seed itself wrote.
		_storage.RemoveSyncMeta(User, OwnerId, LocalStorageService.SyncMetaDeviceRosterAdd(Vid, Pid));
		_api.DeviceListResponse = () => Task.FromResult(RosterList());
		var syncSignals = 0;
		_storage.SyncRelevantDataChanged += () => Interlocked.Increment(ref syncSignals);

		await RunPullAsync(NewService());

		Assert.NotNull(_storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaDeviceRosterAdd(Vid, Pid)));
		Assert.Equal(0, syncSignals);
	}

	// The roster twin of the tombstone-skip rule: a pair with a pending .remove mark is a
	// delete in flight — the pull must not resurrect it, and the mark must survive for the
	// coordinator's remove push.
	[Fact]
	public async Task Pull_PendingRemoveMark_SuppressesHydrationAndSurvives()
	{
		var id = _storage.AddDevice(User, OwnerId, UserMouse());
		_storage.DeleteDevice(User, OwnerId, id);
		var markBefore = _storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaDeviceRosterRemove(Vid, Pid));
		Assert.NotNull(markBefore);
		_api.DeviceListResponse = () => Task.FromResult(RosterList(
			new DeviceListItemDto { VendorId = Vid, ProductId = Pid, Name = "PG Taipan" }));
		_catalogResolver.Response = (_, _) => new DeviceCatalogEntry { Name = "PG TAIPAN" };

		await RunPullAsync(NewService());

		Assert.Empty(_catalogResolver.Lookups);
		Assert.Empty(_storage.GetDevices(User, OwnerId));
		Assert.Equal(markBefore, _storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaDeviceRosterRemove(Vid, Pid)));
	}

	// Review round 1, S1: a coordinator pass overlapping this pull can confirm the pending
	// devices/remove and clear its mark while the pull is parked on the devices/list call —
	// and the list response still names the pair, because it was built before the server-side
	// delete landed. The pre-list snapshot is what keeps the deleted model out; a mark read
	// taken after the list call misses the clear and hydrates the model back (with the next
	// sign-in's reconcile then re-registering it server-side). Same interleaving shape as
	// OnSignedInAsync_ReturnsWhileTheFirstApiCallIsStillBlocked.
	[Fact]
	public async Task Pull_RemoveMarkClearedWhileDeviceListIsParked_StillDoesNotResurrectTheModel()
	{
		var id = _storage.AddDevice(User, OwnerId, UserMouse());
		_storage.DeleteDevice(User, OwnerId, id); // last card: writes the pending .remove mark
		using var gate = new SemaphoreSlim(0);
		var listParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_api.DeviceListResponse = async () =>
		{
			listParked.TrySetResult();
			await gate.WaitAsync();
			return RosterList(new DeviceListItemDto { VendorId = Vid, ProductId = Pid, Name = "PG Taipan" });
		};
		_catalogResolver.Response = (_, _) => new DeviceCatalogEntry { Name = "PG TAIPAN" };

		var service = NewService();
		await service.OnSignedInAsync(CustomerId);
		await listParked.Task;

		// The overlapping pass confirms the delete server-side and clears the mark mid-pull.
		_storage.RemoveSyncMeta(User, OwnerId, LocalStorageService.SyncMetaDeviceRosterRemove(Vid, Pid));

		gate.Release();
		await service.PullTask;

		Assert.Empty(_catalogResolver.Lookups);
		Assert.Empty(_storage.GetDevices(User, OwnerId));
	}

	[Fact]
	public async Task Pull_DeviceListNonSuccess_SkipsTheRosterStepWithoutFailingThePull()
	{
		_api.DeviceListResponse = () => Task.FromResult(new DeviceListResponse
		{
			Success = false, StatusCode = 500, ErrorCode = "SERVER_ERROR",
		});

		await RunPullAsync(NewService());

		Assert.Empty(_catalogResolver.Lookups);
		Assert.Contains(_log.Entries,
			e => e.Level == LogLevel.Warning && e.Message.Contains("skipping the roster step"));
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Error);
	}

	// An item that fails DTO binding is skipped with a warning while its well-formed
	// neighbours proceed — the same per-item tolerance every other list merge has.
	[Fact]
	public async Task Pull_RosterItemThatFailsBinding_IsSkippedWhileTheRestHydrates()
	{
		_api.DeviceListResponse = () => Task.FromResult(RosterList(
			new DeviceListItemDto { VendorId = null, ProductId = Pid, Name = "no-vendor" },
			null,
			new DeviceListItemDto { VendorId = Vid, ProductId = Pid, Name = "PG Taipan" }));
		_catalogResolver.Response = (_, _) => new DeviceCatalogEntry { Name = "PG TAIPAN", DeviceType = "Wireless Mouse" };

		await RunPullAsync(NewService());

		Assert.Equal((Vid, Pid), Assert.Single(_catalogResolver.Lookups));
		Assert.Equal("PG Taipan", Assert.Single(_storage.GetDevices(User, OwnerId)).Name);
		Assert.Contains(_log.Entries,
			e => e.Level == LogLevel.Warning && e.Message.Contains("did not bind"));
	}

	// ── Notifications merge (docs/plans/add-api-v21-notifications-sync) ──────
	// The pull's final step and the periodic poll share PullNotificationsAsync: a
	// server-id-keyed merge with the type→icon resolution, dirty-local-wins adoption, and
	// pre-list snapshots of the tombstones and batch marks.

	private static NotificationDto NotifDto(
		long id, string title, string? type = "info", bool isRead = false,
		string? createdAt = "2026-08-28 16:48:25") => new()
	{
		Id = id,
		Title = title,
		Message = $"{title} message",
		Type = type,
		IsRead = isRead,
		CreatedAt = createdAt,
	};

	private static NotificationListResponse NotifList(params NotificationDto?[] items) => new()
	{
		Success = true, StatusCode = 200, Notifications = [.. items],
	};

	private NotificationRecord SeedSyncedBell(long serverId, string title = "SYNCED", bool read = false)
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
		record.Id = _storage.AddNotification(User, OwnerId, record, markDirty: false);
		return record;
	}

	[Fact]
	public async Task Pull_NotificationsListNewIds_InsertsWithTypeIconsAndRaisesNotificationsPulled()
	{
		_api.NotificationListResponse = () => Task.FromResult(NotifList(
			NotifDto(2, "Battery low", type: "low_battery"),
			NotifDto(1, "Older entry", type: "made_up_by_the_backend", isRead: true)));
		var service = NewService();
		var pulledRaised = 0;
		service.NotificationsPulled += () => Interlocked.Increment(ref pulledRaised);
		var syncSignals = 0;
		_storage.SyncRelevantDataChanged += () => Interlocked.Increment(ref syncSignals);

		await RunPullAsync(service);

		// Newest-first order survives the merge (the list is newest-first, inserts are
		// oldest-first so row ids ascend chronologically).
		var rows = _storage.GetNotifications(User, OwnerId);
		Assert.Equal(new[] { "Battery low", "Older entry" }, rows.Select(r => r.Title).ToArray());
		var newest = rows[0];
		Assert.Equal(2, newest.ServerId);
		Assert.Equal("Battery low message", newest.Subtitle);
		Assert.Equal("low_battery", newest.Type);
		Assert.Equal("avares://ProoveGaming.Common/Assets/Icons/Low-Battery.svg", newest.IconUri);
		Assert.Equal(new DateTime(2026, 8, 28, 16, 48, 25), newest.CreatedAt);
		// An unknown backend slug falls back to the Info glyph rather than no icon.
		Assert.Equal("avares://ProoveGaming.Common/Assets/Icons/Info.svg", rows[1].IconUri);
		Assert.True(rows[1].IsRead);
		// Pulled rows are the server's state: never dirty, never signalled.
		Assert.Empty(_storage.GetDirtyNotifications(User, OwnerId));
		Assert.Equal(0, syncSignals);
		Assert.Equal(1, pulledRaised);
	}

	[Fact]
	public async Task Pull_KnownServerIdNotDirty_AdoptsTheServerReadFlag()
	{
		SeedSyncedBell(5, read: false);
		_api.NotificationListResponse = () => Task.FromResult(NotifList(
			NotifDto(5, "SYNCED", isRead: true)));

		await RunPullAsync(NewService());

		var row = Assert.Single(_storage.GetNotifications(User, OwnerId));
		Assert.True(row.IsRead);
		Assert.Empty(_storage.GetDirtyNotifications(User, OwnerId));
	}

	// A local toggle in flight wins until its push settles — the merge must not overwrite it.
	[Fact]
	public async Task Pull_KnownServerIdDirty_LeavesTheLocalState()
	{
		var row = SeedSyncedBell(5, read: false);
		_storage.SetNotificationRead(row.Id, true); // dirty: the local intent
		_api.NotificationListResponse = () => Task.FromResult(NotifList(
			NotifDto(5, "SYNCED", isRead: false)));

		await RunPullAsync(NewService());

		var stored = Assert.Single(_storage.GetNotifications(User, OwnerId));
		Assert.True(stored.IsRead);
		Assert.True(stored.IsDirty);
	}

	// The tombstone-skip rule: a pending local delete is not a missing record, and the
	// tombstone must survive for the coordinator's delete push.
	[Fact]
	public async Task Pull_TombstonedServerId_IsNotResurrected()
	{
		var row = SeedSyncedBell(6);
		_storage.DeleteNotification(row.Id); // tombstone uid "6"
		_api.NotificationListResponse = () => Task.FromResult(NotifList(
			NotifDto(6, "SYNCED")));

		await RunPullAsync(NewService());

		Assert.Empty(_storage.GetNotifications(User, OwnerId));
		Assert.Single(_storage.GetSyncTombstones(User, OwnerId, LocalStorageService.TombstoneKindNotification));
	}

	// A pending delete_all is about to empty the account's list — merging anything from it
	// would resurrect entries the user just deleted; the step skips before even listing.
	[Fact]
	public async Task Pull_PendingDeleteAll_SkipsTheWholeMergeWithoutListing()
	{
		_storage.DeleteAllNotifications(User, OwnerId);
		_api.NotificationListResponse = () => Task.FromResult(NotifList(NotifDto(1, "GHOST")));

		await RunPullAsync(NewService());

		Assert.DoesNotContain("notifications/list", _api.Endpoints);
		Assert.Empty(_storage.GetNotifications(User, OwnerId));
		Assert.NotNull(_storage.GetSyncMeta(User, OwnerId, LocalStorageService.SyncMetaNotificationsDeleteAll));
	}

	// Mark-all-read writes no per-row dirty flags, so without this guard a merge landing
	// before the read_all push would adopt the server's stale unread flags and visually
	// un-read the whole bell.
	[Fact]
	public async Task Pull_PendingReadAll_SuppressesReadStateAdoption()
	{
		SeedSyncedBell(5, read: false);
		_storage.SetAllNotificationsRead(User, OwnerId); // rows read + read_all mark, no per-row dirty
		_api.NotificationListResponse = () => Task.FromResult(NotifList(
			NotifDto(5, "SYNCED", isRead: false)));

		await RunPullAsync(NewService());

		Assert.True(Assert.Single(_storage.GetNotifications(User, OwnerId)).IsRead);
	}

	// New server entries must land even on a full bell — the newest MaxNotificationsPerOwner
	// win and the trim evicts the chronologically oldest.
	[Fact]
	public async Task Pull_ServerListBeyondTheCap_KeepsTheNewestCapEntries()
	{
		var items = new List<NotificationDto?>();
		for (var i = 105; i >= 1; i--)
			items.Add(NotifDto(i, $"N{i}"));
		_api.NotificationListResponse = () => Task.FromResult(new NotificationListResponse
		{
			Success = true, StatusCode = 200, Notifications = items,
		});

		await RunPullAsync(NewService());

		var rows = _storage.GetNotifications(User, OwnerId);
		Assert.Equal(100, rows.Count);
		Assert.Equal("N105", rows[0].Title);
		Assert.Equal("N6", rows[^1].Title);
	}

	// Review round 1, B1: once the server outgrows the cap, the list's beyond-cap TAIL is
	// the account's oldest history and local rows can never cover it — treating it as
	// insertable put months-old entries at the TOP of the bell (a late insert gets the
	// highest row id) while the trim evicted the genuinely recent rows, which the next poll
	// then re-inserted: a reshuffle on every tick. The merge must consider only the newest
	// MaxNotificationsPerOwner items, so a bell already mirroring the server's newest cap is
	// left byte-for-byte alone — including on a second, idempotent merge.
	[Fact]
	public async Task Pull_ServerOutgrewTheCap_LeavesAFullMirroredBellAloneOnEveryMerge()
	{
		// Local bell at the cap, mirroring the server's newest 100 (ids 101..200; inserted
		// oldest first so row ids ascend chronologically — newest on top).
		for (var serverId = 101; serverId <= 200; serverId++)
			SeedSyncedBell(serverId, title: $"N{serverId}");
		// The server's list: those same newest 100, then a 5-entry beyond-cap tail of the
		// account's oldest history.
		var items = new List<NotificationDto?>();
		for (var serverId = 200; serverId >= 96; serverId--)
			items.Add(NotifDto(serverId, $"N{serverId}"));
		_api.NotificationListResponse = () => Task.FromResult(new NotificationListResponse
		{
			Success = true, StatusCode = 200, Notifications = items,
		});
		var service = NewService();
		var pulledRaised = 0;
		service.NotificationsPulled += () => Interlocked.Increment(ref pulledRaised);

		await RunPullAsync(service);

		var rows = _storage.GetNotifications(User, OwnerId);
		Assert.Equal(100, rows.Count);
		Assert.Equal(200, rows[0].ServerId);
		Assert.Equal(101, rows[^1].ServerId);
		Assert.Equal(0, pulledRaised);

		// The churn half: a second merge over the same list must also be a no-op.
		await RunPullAsync(NewService());
		rows = _storage.GetNotifications(User, OwnerId);
		Assert.Equal(200, rows[0].ServerId);
		Assert.Equal(101, rows[^1].ServerId);
	}

	// PR #127, M2 — deletions follow the account: a snapshot-time local row whose server id
	// the full list no longer carries was deleted on another machine; the merge mirrors it.
	// Local-only rows (no server id) are never touched.
	[Fact]
	public async Task Pull_ServerNoLongerListsASyncedRow_DeletesItLocallyKeepingTheRest()
	{
		SeedSyncedBell(5, title: "GONE");
		SeedSyncedBell(6, title: "KEPT");
		_storage.AddNotification(User, OwnerId, new NotificationRecord
		{
			Title = "LOCAL-ONLY", CreatedAt = DateTime.Now, Type = "info",
		});
		_api.NotificationListResponse = () => Task.FromResult(NotifList(NotifDto(6, "KEPT")));
		var service = NewService();
		var pulledRaised = 0;
		service.NotificationsPulled += () => Interlocked.Increment(ref pulledRaised);

		await RunPullAsync(service);

		var rows = _storage.GetNotifications(User, OwnerId);
		Assert.Equal(new[] { "LOCAL-ONLY", "KEPT" }, rows.Select(r => r.Title).ToArray());
		Assert.Empty(_storage.GetSyncTombstones(User, OwnerId, LocalStorageService.TombstoneKindNotification));
		Assert.Equal(1, pulledRaised);
	}

	// The plan's QA step 4 shape: delete-all pushed from machine A empties the account's
	// list; machine B's next merge empties its bell too.
	[Fact]
	public async Task Pull_EmptyList_EmptiesTheBellOfSyncedRows()
	{
		SeedSyncedBell(5);
		SeedSyncedBell(6, title: "SYNCED-2");
		_api.NotificationListResponse = () => Task.FromResult(NotifList());

		await RunPullAsync(NewService());

		Assert.Empty(_storage.GetNotifications(User, OwnerId));
	}

	// A dirty row absent from the list is a local intent still owed to the server — its own
	// push meets NOTIFICATION_NOT_FOUND and takes it down through that path, not the sweep.
	[Fact]
	public async Task Pull_DirtySyncedRowAbsentFromTheList_SurvivesTheSweep()
	{
		var row = SeedSyncedBell(5);
		_storage.SetNotificationRead(row.Id, true); // dirty
		_api.NotificationListResponse = () => Task.FromResult(NotifList());

		await RunPullAsync(NewService());

		Assert.Equal(5, Assert.Single(_storage.GetNotifications(User, OwnerId)).ServerId);
	}

	// PR #127, m7: a server entry arriving while read_all is pending inserts as READ — the
	// pending batch will mark it read server-side anyway, and inserting it unread would
	// re-light the bell the user just cleared.
	[Fact]
	public async Task Pull_PendingReadAll_InsertsNewRowsAsRead()
	{
		_storage.SetAllNotificationsRead(User, OwnerId); // the read_all mark, bell empty
		_api.NotificationListResponse = () => Task.FromResult(NotifList(
			NotifDto(9, "Arrived mid-batch", isRead: false)));

		await RunPullAsync(NewService());

		Assert.True(Assert.Single(_storage.GetNotifications(User, OwnerId)).IsRead);
	}

	[Fact]
	public async Task Pull_NotificationListNonSuccess_SkipsTheMergeWithoutFailingThePull()
	{
		_api.NotificationListResponse = () => Task.FromResult(new NotificationListResponse
		{
			Success = false, StatusCode = 500, ErrorCode = "SERVER_ERROR",
		});

		await RunPullAsync(NewService());

		Assert.Empty(_storage.GetNotifications(User, OwnerId));
		Assert.Contains(_log.Entries,
			e => e.Level == LogLevel.Warning && e.Message.Contains("skipping the merge"));
		Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Error);
	}

	// ── Notification poll ────────────────────────────────────────────────────
	// The 15-minute loop, run here at test cadence via the internal interval seam. Each
	// tick is the same PullNotificationsAsync merge, chained on PullTask.

	private SettingsSyncService NewPollingService(TimeSpan interval, string token = "token-1", TimeSpan? timeout = null) =>
		new(_api, _storage,
			new UserProfile { CustomerId = CustomerId, SessionToken = token },
			new PresetConfigurationBuilder(_storage), new PresetConfigurationApplier(_storage),
			_catalogResolver, _log, interval, timeout);

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

	[Fact]
	public async Task NotificationPoll_SignedIn_MergesOnTickAndRaisesNotificationsPulled()
	{
		_api.NotificationListResponse = () => Task.FromResult(NotifList(
			NotifDto(1, "Announcement", type: "app_update")));
		var pulledRaised = 0;

		using var service = NewPollingService(TimeSpan.FromMilliseconds(100));
		service.NotificationsPulled += () => Interlocked.Increment(ref pulledRaised);

		// No OnSignedInAsync: the poll alone must deliver the entry.
		await WaitUntilAsync(
			() => _storage.GetNotifications(User, OwnerId).Count == 1,
			"the poll to merge the server entry");
		Assert.Equal("Announcement", Assert.Single(_storage.GetNotifications(User, OwnerId)).Title);
		await WaitUntilAsync(() => Volatile.Read(ref pulledRaised) >= 1, "NotificationsPulled to be raised");
	}

	// PR #127, m4: the tick's own timeout cuts a hung notifications/list off, so the
	// PullTask chain (awaited by DeviceDetails' preset gate and the coordinator) stays
	// promptly awaitable on a dead link — observable as ticks that keep coming instead of
	// one parked forever on the HttpClient's ~100 s default.
	[Fact]
	public async Task NotificationPoll_HungListCall_IsCutOffByTheTickTimeout()
	{
		// A list that never answers; only the tick's linked cancellation can end the call.
		// Counted in the factory (Interlocked) — the fake's Endpoints list is not safe to
		// enumerate while the poll thread appends to it.
		var listCalls = 0;
		_api.NotificationListResponse = () =>
		{
			Interlocked.Increment(ref listCalls);
			return new TaskCompletionSource<NotificationListResponse>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
		};

		using var service = NewPollingService(
			TimeSpan.FromMilliseconds(50), timeout: TimeSpan.FromMilliseconds(150));

		await WaitUntilAsync(
			() => Volatile.Read(ref listCalls) >= 2,
			"a second tick after the first hung call was cut off");
	}

	[Fact]
	public async Task NotificationPoll_NoToken_MakesNoApiCalls()
	{
		using var service = NewPollingService(TimeSpan.FromMilliseconds(50), token: "");

		// Several ticks' worth of time: an idle poll must not have touched the API.
		await Task.Delay(500);
		Assert.Empty(_api.Endpoints);
	}

	// ── Test doubles ─────────────────────────────────────────────────────────

	// Records every call in arrival order (the pull is sequential by design) and answers
	// from swappable factories; defaults answer "empty account".
	private sealed class RecordingSyncApi : ISyncApiService
	{
		public List<string> Endpoints { get; } = [];
		public List<string> MacroGetUids { get; } = [];
		public List<string> PresetGetUids { get; } = [];
		public List<(int VendorId, int ProductId)> DeviceGetRequests { get; } = [];

		public Func<Task<MacroListResponse>> MacroListResponse { get; set; } =
			() => Task.FromResult(new MacroListResponse { Success = true, StatusCode = 200, Macros = [] });

		public Func<string, Task<MacroGetResponse>> MacroGetResponse { get; set; } =
			_ => Task.FromResult(new MacroGetResponse { Success = true, StatusCode = 200 });

		public Func<Task<PresetListResponse>> PresetListResponse { get; set; } =
			() => Task.FromResult(new PresetListResponse { Success = true, StatusCode = 200, Presets = [] });

		public Func<string, Task<PresetGetResponse>> PresetGetResponse { get; set; } =
			_ => Task.FromResult(new PresetGetResponse { Success = true, StatusCode = 200 });

		public Func<Task<PreferencesGetResponse>> PreferencesGetResponse { get; set; } =
			() => Task.FromResult(new PreferencesGetResponse { Success = true, StatusCode = 200 });

		public Func<int, int, Task<DeviceStateGetResponse>> DeviceStateGetResponse { get; set; } =
			(_, _) => Task.FromResult(new DeviceStateGetResponse { Success = true, StatusCode = 200 });

		public Task<MacroListResponse> ListMacrosAsync(string token, CancellationToken ct = default)
		{
			Endpoints.Add("macros/list");
			return MacroListResponse();
		}

		public Task<MacroGetResponse> GetMacroAsync(string token, string uid, CancellationToken ct = default)
		{
			Endpoints.Add("macros/get");
			MacroGetUids.Add(uid);
			return MacroGetResponse(uid);
		}

		public Task<PresetListResponse> ListPresetsAsync(string token, CancellationToken ct = default)
		{
			Endpoints.Add("presets/list");
			return PresetListResponse();
		}

		public Task<PresetGetResponse> GetPresetAsync(string token, string uid, CancellationToken ct = default)
		{
			Endpoints.Add("presets/get");
			PresetGetUids.Add(uid);
			return PresetGetResponse(uid);
		}

		public Task<PreferencesGetResponse> GetPreferencesAsync(string token, CancellationToken ct = default)
		{
			Endpoints.Add("preferences/get");
			return PreferencesGetResponse();
		}

		public Task<DeviceStateGetResponse> GetDeviceStateAsync(string token, int vendorId, int productId, CancellationToken ct = default)
		{
			Endpoints.Add("devices/get");
			DeviceGetRequests.Add((vendorId, productId));
			return DeviceStateGetResponse(vendorId, productId);
		}

		// Push endpoints — the pull tests never reach them; recorded so an accidental call
		// shows up in an endpoint-order assertion.
		public Task<MacroCreateResponse> CreateMacroAsync(string token, MacroCreateRequest request, CancellationToken ct = default)
		{
			Endpoints.Add("macros/create");
			return Task.FromResult(new MacroCreateResponse { Success = true, StatusCode = 200 });
		}

		public Task<PresetCreateResponse> CreatePresetAsync(string token, PresetCreateRequest request, CancellationToken ct = default)
		{
			Endpoints.Add("presets/create");
			return Task.FromResult(new PresetCreateResponse { Success = true, StatusCode = 200 });
		}

		public Task<PreferencesUpdateResponse> UpdatePreferencesAsync(string token, Dictionary<string, object> preferences, string? updatedAt = null, CancellationToken ct = default)
		{
			Endpoints.Add("preferences/update");
			return Task.FromResult(new PreferencesUpdateResponse { Success = true, StatusCode = 200 });
		}

		public Task<DeviceStateUpdateResponse> UpdateDeviceStateAsync(string token, int vendorId, int productId, string? activePresetUid, string? updatedAt = null, CancellationToken ct = default)
		{
			Endpoints.Add("devices/update");
			return Task.FromResult(new DeviceStateUpdateResponse { Success = true, StatusCode = 200 });
		}

		// Live-sync endpoints (docs/plans/add-api-v2-live-sync) — never reached by a pull.
		public Task<MacroCreateResponse> UpdateMacroAsync(string token, MacroUpdateRequest request, CancellationToken ct = default)
		{
			Endpoints.Add("macros/update");
			return Task.FromResult(new MacroCreateResponse { Success = true, StatusCode = 200 });
		}

		public Task<ApiResponse> DeleteMacroAsync(string token, string uid, string? updatedAt, CancellationToken ct = default)
		{
			Endpoints.Add("macros/delete");
			return Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });
		}

		public Task<PresetCreateResponse> UpdatePresetAsync(string token, PresetUpdateRequest request, CancellationToken ct = default)
		{
			Endpoints.Add("presets/update");
			return Task.FromResult(new PresetCreateResponse { Success = true, StatusCode = 200 });
		}

		public Task<ApiResponse> DeletePresetAsync(string token, string uid, string? updatedAt, CancellationToken ct = default)
		{
			Endpoints.Add("presets/delete");
			return Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });
		}

		// Device roster (docs/plans/add-api-v21-device-roster). The list default answers
		// with a null Devices payload, which makes the pull's roster step a no-op — tests
		// of the step swap in a real list.
		public Func<Task<DeviceListResponse>> DeviceListResponse { get; set; } =
			() => Task.FromResult(new DeviceListResponse { Success = true, StatusCode = 200 });

		public Task<DeviceListResponse> ListDevicesAsync(string token, CancellationToken ct = default)
		{
			Endpoints.Add("devices/list");
			return DeviceListResponse();
		}

		public Task<ApiResponse> AddDeviceAsync(string token, int vendorId, int productId, string name, CancellationToken ct = default)
		{
			Endpoints.Add("devices/add");
			return Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });
		}

		public Task<ApiResponse> RemoveDeviceAsync(string token, int vendorId, int productId, CancellationToken ct = default)
		{
			Endpoints.Add("devices/remove");
			return Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });
		}

		// Notifications (docs/plans/add-api-v21-notifications-sync). The list default answers
		// with a null Notifications payload, which makes the pull's merge a no-op — tests of
		// the merge swap in a real list.
		public Func<Task<NotificationListResponse>> NotificationListResponse { get; set; } =
			() => Task.FromResult(new NotificationListResponse { Success = true, StatusCode = 200 });

		public Task<NotificationListResponse> ListNotificationsAsync(string token, CancellationToken ct = default)
		{
			Endpoints.Add("notifications/list");
			// WaitAsync: a parked response must still honor the caller's cancellation, the
			// way the real ApiHttpClient does — the poll-timeout test depends on it.
			return NotificationListResponse().WaitAsync(ct);
		}

		public Task<NotificationAddResponse> AddNotificationAsync(string token, NotificationAddRequest request, CancellationToken ct = default)
		{
			Endpoints.Add("notifications/add");
			return Task.FromResult(new NotificationAddResponse { Success = true, StatusCode = 200 });
		}

		public Task<ApiResponse> ReadNotificationAsync(string token, long notificationId, CancellationToken ct = default)
		{
			Endpoints.Add("notifications/read");
			return Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });
		}

		public Task<ApiResponse> UnreadNotificationAsync(string token, long notificationId, CancellationToken ct = default)
		{
			Endpoints.Add("notifications/unread");
			return Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });
		}

		public Task<ApiResponse> DeleteNotificationAsync(string token, long notificationId, CancellationToken ct = default)
		{
			Endpoints.Add("notifications/delete");
			return Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });
		}

		public Task<ApiResponse> ReadAllNotificationsAsync(string token, CancellationToken ct = default)
		{
			Endpoints.Add("notifications/read_all");
			return Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });
		}

		public Task<ApiResponse> DeleteAllNotificationsAsync(string token, CancellationToken ct = default)
		{
			Endpoints.Add("notifications/delete_all");
			return Task.FromResult(new ApiResponse { Success = true, StatusCode = 200 });
		}
	}

	// Catalog lookups for the roster step's hydration (docs/plans/add-api-v21-device-roster):
	// answers from a swappable factory; the default "unknown model" makes hydration a no-op.
	private sealed class FakeCatalogResolver : IDeviceCatalogResolver
	{
		public List<(int VendorId, int ProductId)> Lookups { get; } = [];

		public Func<int, int, ProoveGaming.GrpcDependencies.Device.DeviceCatalogEntry?> Response { get; set; } =
			(_, _) => null;

		public Task<ProoveGaming.GrpcDependencies.Device.DeviceCatalogEntry?> ResolveAsync(int vendorId, int productId, CancellationToken ct = default)
		{
			Lookups.Add((vendorId, productId));
			return Task.FromResult(Response(vendorId, productId));
		}
	}

	// Captures level + rendered message (same double as SettingsSyncServiceTests) so tests
	// can assert what was — and was not — logged.
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
