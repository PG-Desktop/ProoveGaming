using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ProoveGaming.App.Models;
using ProoveGaming.App.Models.Settings;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// Characterization of the live-sync change tracking inside LocalStorageService
// (docs/plans/add-api-v2-live-sync): every listed mutator marks USER rows dirty
// (is_dirty = 1, local_rev + 1) and raises SyncRelevantDataChanged exactly once per call;
// guest rows and the sign-in pull's markDirty: false writes do none of it. Deletes write a
// tombstone in the same transaction as the local delete, but only for a user-owned row the
// server knows about (uid + server_updated_at NOT NULL). Dirty state is observed through
// GetDirtyMacros/GetDirtyPresets — the exact reads SyncCoordinator's pass uses — so what
// these tests pin is precisely what the coordinator will push.
public class LocalStorageServiceSyncTrackingTests : IDisposable
{
	private const string User = LocalStorageService.OwnerKindUser;
	private const string Guest = LocalStorageService.OwnerKindGuest;
	private const string UserId = "42";
	private const int Vid = 0x04D9;
	private const int Pid = 0xA23F;
	private const string NoKey = "";

	private static LocalStorageService NewInMemory() => new("Data Source=:memory:");

	// Only the legacy-migration test uses the file store: the migration only does observable
	// work against an already-populated database, which ":memory:" can never be at
	// construction (same reasoning as LocalStorageServicePresetsTests' uid-backfill section).
	private readonly string _dbPath =
		Path.Combine(Path.GetTempPath(), $"pg-synctracking-{Guid.NewGuid():N}.db");

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		try { if (File.Exists(_dbPath)) File.Delete(_dbPath); }
		catch (IOException) { /* handle may still be releasing */ }
	}

	private static string GuestId() => Guid.NewGuid().ToString("D");

	private static MacroRecord NewMacro(string name = "M1") => new()
	{
		Name = name,
		RepeatCount = 1,
		Events = { new MacroEventRecord { KeyCode = 0x04, IsPress = true, DelayMs = 5 } },
	};

	// Subscribes a counter to SyncRelevantDataChanged; the returned delegate reads it.
	// Interlocked because SettingsRepository raises from the storage executor's pool thread.
	private static Func<int> CountEvents(LocalStorageService storage)
	{
		var count = 0;
		storage.SyncRelevantDataChanged += () => Interlocked.Increment(ref count);
		return () => Volatile.Read(ref count);
	}

	// ── SaveMacro ────────────────────────────────────────────────────────────

	[Fact]
	public void SaveMacro_UserInsert_MarksDirtyBumpsRevAndRaisesOnce()
	{
		using var storage = NewInMemory();
		var events = CountEvents(storage);

		storage.SaveMacro(User, UserId, Vid, Pid, NoKey, NewMacro());

		Assert.Equal(1, events());
		var (row, vid, pid) = Assert.Single(storage.GetDirtyMacros(User, UserId));
		Assert.True(row.IsDirty);
		Assert.Equal(1, row.LocalRev);
		Assert.Equal((Vid, Pid), (vid, pid));
	}

	[Fact]
	public void SaveMacro_UserUpdate_BumpsRevAgainAndRaisesPerCall()
	{
		using var storage = NewInMemory();
		var macro = NewMacro();
		storage.SaveMacro(User, UserId, Vid, Pid, NoKey, macro);
		var events = CountEvents(storage);

		macro.Name = "M1-edited";
		storage.SaveMacro(User, UserId, Vid, Pid, NoKey, macro);

		Assert.Equal(1, events());
		var (row, _, _) = Assert.Single(storage.GetDirtyMacros(User, UserId));
		Assert.True(row.IsDirty);
		Assert.Equal(2, row.LocalRev);
		Assert.Equal("M1-edited", row.Name);
	}

	[Fact]
	public void SaveMacro_GuestRows_AreNeverMarkedAndNeverRaise()
	{
		using var storage = NewInMemory();
		var owner = GuestId();
		var events = CountEvents(storage);

		var macro = NewMacro();
		storage.SaveMacro(Guest, owner, Vid, Pid, NoKey, macro);
		macro.Name = "edited";
		storage.SaveMacro(Guest, owner, Vid, Pid, NoKey, macro);

		Assert.Equal(0, events());
		// The row surfaces as never-synced, but carries no dirty mark and no rev history.
		var (row, _, _) = Assert.Single(storage.GetDirtyMacros(Guest, owner));
		Assert.False(row.IsDirty);
		Assert.Equal(0, row.LocalRev);
	}

	// The sign-in pull's shape: markDirty: false plus a ServerUpdatedAt on the record. The
	// row must land clean AND silent — marking it would push server content straight back.
	[Fact]
	public void SaveMacro_PullShapedInsert_LandsCleanAndRaisesNothing()
	{
		using var storage = NewInMemory();
		var events = CountEvents(storage);

		var macro = NewMacro();
		macro.ServerUpdatedAt = "2026-08-25 09:00:00";
		storage.SaveMacro(User, UserId, Vid, Pid, NoKey, macro, markDirty: false);

		Assert.Equal(0, events());
		Assert.Empty(storage.GetDirtyMacros(User, UserId));
		Assert.Equal("2026-08-25 09:00:00",
			Assert.Single(storage.GetMacros(User, UserId, Vid, Pid, NoKey)).ServerUpdatedAt);
	}

	// ── InsertPreset / UpdatePreset ──────────────────────────────────────────

	[Fact]
	public void InsertPreset_UserRow_BornDirtyAtRevOneAndRaisesOnce()
	{
		using var storage = NewInMemory();
		var events = CountEvents(storage);

		storage.InsertPreset(User, UserId, Vid, Pid, new PresetRecord { Name = "P1" });

		Assert.Equal(1, events());
		var (row, _, _) = Assert.Single(storage.GetDirtyPresets(User, UserId));
		Assert.True(row.IsDirty);
		Assert.Equal(1, row.LocalRev);
	}

	[Fact]
	public void InsertPreset_GuestRow_LandsCleanAndSilent()
	{
		using var storage = NewInMemory();
		var owner = GuestId();
		var events = CountEvents(storage);

		storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "P1" });

		Assert.Equal(0, events());
		var (row, _, _) = Assert.Single(storage.GetDirtyPresets(Guest, owner));
		Assert.False(row.IsDirty);
		Assert.Equal(0, row.LocalRev);
	}

	[Fact]
	public void InsertPreset_PullShapedInsert_LandsCleanAndRaisesNothing()
	{
		using var storage = NewInMemory();
		var events = CountEvents(storage);

		storage.InsertPreset(User, UserId, Vid, Pid,
			new PresetRecord { Name = "P1", ServerUpdatedAt = "2026-08-25 09:00:01" }, markDirty: false);

		Assert.Equal(0, events());
		Assert.Empty(storage.GetDirtyPresets(User, UserId));
	}

	[Fact]
	public void UpdatePreset_UserRow_MarksDirtyBumpsRevAndRaisesOnce()
	{
		using var storage = NewInMemory();
		var id = storage.InsertPreset(User, UserId, Vid, Pid, new PresetRecord { Name = "P1" });
		var events = CountEvents(storage);

		storage.UpdatePreset(id, "P1-renamed", "new description");

		Assert.Equal(1, events());
		var (row, _, _) = Assert.Single(storage.GetDirtyPresets(User, UserId));
		Assert.True(row.IsDirty);
		Assert.Equal(2, row.LocalRev);
		Assert.Equal("P1-renamed", row.Name);
	}

	[Fact]
	public void UpdatePreset_GuestRow_DoesNotMarkOrRaise()
	{
		using var storage = NewInMemory();
		var owner = GuestId();
		var id = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "P1" });
		var events = CountEvents(storage);

		storage.UpdatePreset(id, "renamed", "");

		Assert.Equal(0, events());
		var (row, _, _) = Assert.Single(storage.GetDirtyPresets(Guest, owner));
		Assert.False(row.IsDirty);
		Assert.Equal(0, row.LocalRev);
	}

	// ── PromotePresetToDefault ───────────────────────────────────────────────

	// The default flip touches TWO rows (promoted + demoted previous default); both must be
	// marked, and the whole flip is one mutation — one event, not one per row.
	[Fact]
	public void PromotePresetToDefault_MarksBothFlippedRowsAndRaisesOnce()
	{
		using var storage = NewInMemory();
		var a = storage.InsertPreset(User, UserId, Vid, Pid, new PresetRecord { Name = "A", IsDefault = true });
		var b = storage.InsertPreset(User, UserId, Vid, Pid, new PresetRecord { Name = "B" });
		var events = CountEvents(storage);

		storage.PromotePresetToDefault(b);

		Assert.Equal(1, events());
		var rows = storage.GetDirtyPresets(User, UserId);
		var rowA = rows.Single(r => r.Preset.Id == a).Preset;
		var rowB = rows.Single(r => r.Preset.Id == b).Preset;
		Assert.True(rowA.IsDirty);
		Assert.Equal(2, rowA.LocalRev); // insert (1) + demote (2)
		Assert.False(rowA.IsDefault);
		Assert.True(rowB.IsDirty);
		Assert.Equal(2, rowB.LocalRev); // insert (1) + promote (2)
		Assert.True(rowB.IsDefault);
	}

	// Re-promoting the current default flips nothing, so nothing may be re-marked: a bumped
	// local_rev on an unchanged row would defeat the rev guard and re-push it forever.
	[Fact]
	public void PromotePresetToDefault_AlreadyDefault_BumpsNothingAndRaisesNothing()
	{
		using var storage = NewInMemory();
		var a = storage.InsertPreset(User, UserId, Vid, Pid, new PresetRecord { Name = "A", IsDefault = true });
		var events = CountEvents(storage);

		storage.PromotePresetToDefault(a);

		Assert.Equal(0, events());
		var (row, _, _) = Assert.Single(storage.GetDirtyPresets(User, UserId));
		Assert.Equal(1, row.LocalRev); // still just the insert
	}

	// The pull's default arbitration applies the server's flag without echoing it back.
	[Fact]
	public void PromotePresetToDefault_MarkDirtyFalse_AppliesTheFlagWithoutMarking()
	{
		using var storage = NewInMemory();
		var id = storage.InsertPreset(User, UserId, Vid, Pid,
			new PresetRecord { Name = "P1", ServerUpdatedAt = "2026-08-25 09:00:01" }, markDirty: false);
		var events = CountEvents(storage);

		storage.PromotePresetToDefault(id, markDirty: false);

		Assert.Equal(0, events());
		Assert.Empty(storage.GetDirtyPresets(User, UserId));
		Assert.True(Assert.Single(storage.GetPresets(User, UserId, Vid, Pid)).IsDefault);
	}

	// ── Snapshot mutators ────────────────────────────────────────────────────

	[Fact]
	public void SavePresetSnapshot_UserRow_MarksDirtyAndRaisesOnce()
	{
		using var storage = NewInMemory();
		var id = storage.InsertPreset(User, UserId, Vid, Pid, new PresetRecord { Name = "P1" });
		var events = CountEvents(storage);

		storage.SavePresetSnapshot(id, User, UserId, Vid, Pid, NoKey);

		Assert.Equal(1, events());
		var (row, _, _) = Assert.Single(storage.GetDirtyPresets(User, UserId));
		Assert.True(row.IsDirty);
		Assert.Equal(2, row.LocalRev);
	}

	[Fact]
	public void SavePresetSnapshot_GuestRow_DoesNotMarkOrRaise()
	{
		using var storage = NewInMemory();
		var owner = GuestId();
		var id = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "P1" });
		var events = CountEvents(storage);

		storage.SavePresetSnapshot(id, Guest, owner, Vid, Pid, NoKey);

		Assert.Equal(0, events());
		Assert.Equal(0, Assert.Single(storage.GetDirtyPresets(Guest, owner)).Preset.LocalRev);
	}

	[Fact]
	public void ResetPresetSnapshot_UserRow_MarksDirtyAndRaisesOnce()
	{
		using var storage = NewInMemory();
		var id = storage.InsertPreset(User, UserId, Vid, Pid, new PresetRecord { Name = "P1" });
		var events = CountEvents(storage);

		storage.ResetPresetSnapshot(id);

		Assert.Equal(1, events());
		var (row, _, _) = Assert.Single(storage.GetDirtyPresets(User, UserId));
		Assert.True(row.IsDirty);
		Assert.Equal(2, row.LocalRev);
	}

	[Fact]
	public void CopyPresetSnapshot_MarksOnlyTheDestinationPreset()
	{
		using var storage = NewInMemory();
		var src = storage.InsertPreset(User, UserId, Vid, Pid, new PresetRecord { Name = "SRC" });
		var dst = storage.InsertPreset(User, UserId, Vid, Pid, new PresetRecord { Name = "DST" });
		var events = CountEvents(storage);

		storage.CopyPresetSnapshot(src, dst);

		Assert.Equal(1, events());
		var rows = storage.GetDirtyPresets(User, UserId);
		Assert.Equal(1, rows.Single(r => r.Preset.Id == src).Preset.LocalRev); // untouched
		Assert.Equal(2, rows.Single(r => r.Preset.Id == dst).Preset.LocalRev); // marked
	}

	[Fact]
	public void BackfillPresetSnapshotFromLive_UserRow_MarksDirtyAndRaisesOnce()
	{
		using var storage = NewInMemory();
		var id = storage.InsertPreset(User, UserId, Vid, Pid, new PresetRecord { Name = "P1" });
		var events = CountEvents(storage);

		storage.BackfillPresetSnapshotFromLive(id, User, UserId, Vid, Pid, NoKey);

		Assert.Equal(1, events());
		var (row, _, _) = Assert.Single(storage.GetDirtyPresets(User, UserId));
		Assert.True(row.IsDirty);
		Assert.Equal(2, row.LocalRev);
	}

	// ── SetAppliedPreset (device-state dirty key) ────────────────────────────

	[Fact]
	public void SetAppliedPreset_UserRow_SetsDeviceStateDirtyKeyAndRaisesOnce()
	{
		using var storage = NewInMemory();
		var id = storage.InsertPreset(User, UserId, Vid, Pid, new PresetRecord { Name = "P1" });
		var events = CountEvents(storage);

		storage.SetAppliedPreset(User, UserId, Vid, Pid, NoKey, id);

		Assert.Equal(1, events());
		// The flag's value is a generation (clear-after-success guard), not a literal "1".
		Assert.NotNull(storage.GetSyncMeta(User, UserId,
			LocalStorageService.SyncMetaDeviceStateDirty(Vid, Pid)));
	}

	[Fact]
	public void SetAppliedPreset_GuestRow_WritesNoMetaAndRaisesNothing()
	{
		using var storage = NewInMemory();
		var owner = GuestId();
		var id = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "P1" });
		var events = CountEvents(storage);

		storage.SetAppliedPreset(Guest, owner, Vid, Pid, NoKey, id);

		Assert.Equal(0, events());
		Assert.Null(storage.GetSyncMeta(Guest, owner,
			LocalStorageService.SyncMetaDeviceStateDirty(Vid, Pid)));
	}

	[Fact]
	public void SetAppliedPreset_PullShaped_WritesNoMetaAndRaisesNothing()
	{
		using var storage = NewInMemory();
		var id = storage.InsertPreset(User, UserId, Vid, Pid,
			new PresetRecord { Name = "P1", ServerUpdatedAt = "ts" }, markDirty: false);
		var events = CountEvents(storage);

		storage.SetAppliedPreset(User, UserId, Vid, Pid, NoKey, id, markDirty: false);

		Assert.Equal(0, events());
		Assert.Null(storage.GetSyncMeta(User, UserId,
			LocalStorageService.SyncMetaDeviceStateDirty(Vid, Pid)));
		// The applied row itself still landed.
		Assert.Equal((NoKey, id), Assert.Single(storage.GetAppliedPresets(User, UserId, Vid, Pid)));
	}

	// ── Preferences save (SettingsRepository → MarkSyncDirty) ────────────────

	private sealed class FakeOwnerProvider : ICurrentOwnerProvider
	{
		public SettingsOwner? Current;
		public SettingsOwner? GetCurrent() => Current;
	}

	private static readonly AppPreferencesPayload SomePreferences =
		new(ScrollOneScreenAtATime: false, ScrollLines: 5, DoubleClickSpeed: 400, SleepTimer: 60, GenericLiftOffDistance: 1);

	[Fact]
	public async Task SaveAppPreferences_UserOwner_MarksPreferencesDirtyAndRaisesOnce()
	{
		using var storage = NewInMemory();
		var repo = new SettingsRepository(storage,
			new FakeOwnerProvider { Current = new SettingsOwner(User, UserId) });
		var events = CountEvents(storage);

		await repo.SaveAppPreferencesAsync(SomePreferences);

		Assert.Equal(1, events());
		// The flag's value is a generation (clear-after-success guard), not a literal "1".
		Assert.NotNull(storage.GetSyncMeta(User, UserId, LocalStorageService.SyncMetaPreferencesDirty));
	}

	[Fact]
	public async Task SaveAppPreferences_GuestOwner_WritesNoMetaAndRaisesNothing()
	{
		using var storage = NewInMemory();
		var owner = GuestId();
		var repo = new SettingsRepository(storage,
			new FakeOwnerProvider { Current = new SettingsOwner(Guest, owner) });
		var events = CountEvents(storage);

		await repo.SaveAppPreferencesAsync(SomePreferences);

		Assert.Equal(0, events());
		Assert.Null(storage.GetSyncMeta(Guest, owner, LocalStorageService.SyncMetaPreferencesDirty));
		// The rows themselves still saved — guests keep local preferences, they just never sync.
		Assert.Equal(5d, storage.GetAppPreference(Guest, owner, SettingsRepository.PrefScrollLines));
	}

	// ── Delete tombstones ────────────────────────────────────────────────────

	[Fact]
	public void DeleteMacro_UserSyncedRow_WritesTombstoneWithStoredTimestampAndRaisesOnce()
	{
		using var storage = NewInMemory();
		var macro = NewMacro();
		storage.SaveMacro(User, UserId, Vid, Pid, NoKey, macro);
		storage.SetMacroServerUpdatedAt(macro.Id, "2026-08-25 10:00:00");
		var events = CountEvents(storage);

		storage.DeleteMacro(macro.Id);

		Assert.Equal(1, events());
		Assert.Empty(storage.GetMacros(User, UserId, Vid, Pid, NoKey));
		var (uid, vid, pid, serverUpdatedAt) = Assert.Single(
			storage.GetSyncTombstones(User, UserId, LocalStorageService.TombstoneKindMacro));
		Assert.Equal(macro.Uid, uid);
		Assert.Equal((Vid, Pid), (vid, pid));
		Assert.Equal("2026-08-25 10:00:00", serverUpdatedAt);
	}

	// A never-synced row (server_updated_at IS NULL) has nothing remote to remove: the local
	// delete happens, but no tombstone and no signal — even the *_NOT_FOUND round-trip is
	// skipped (plan Risks: the coder implements the skip; this test pins it).
	[Fact]
	public void DeleteMacro_NeverSyncedUserRow_DeletesWithoutTombstoneOrEvent()
	{
		using var storage = NewInMemory();
		var macro = NewMacro();
		storage.SaveMacro(User, UserId, Vid, Pid, NoKey, macro);
		var events = CountEvents(storage);

		storage.DeleteMacro(macro.Id);

		Assert.Equal(0, events());
		Assert.Empty(storage.GetMacros(User, UserId, Vid, Pid, NoKey));
		Assert.Empty(storage.GetSyncTombstones(User, UserId, LocalStorageService.TombstoneKindMacro));
	}

	// Guest rows never tombstone, even in the (artificial) case of a server timestamp.
	[Fact]
	public void DeleteMacro_GuestRow_NeverTombstones()
	{
		using var storage = NewInMemory();
		var owner = GuestId();
		var macro = NewMacro();
		storage.SaveMacro(Guest, owner, Vid, Pid, NoKey, macro);
		storage.SetMacroServerUpdatedAt(macro.Id, "ts");
		var events = CountEvents(storage);

		storage.DeleteMacro(macro.Id);

		Assert.Equal(0, events());
		Assert.Empty(storage.GetSyncTombstones(Guest, owner, LocalStorageService.TombstoneKindMacro));
		Assert.Empty(storage.GetSyncTombstones(User, owner, LocalStorageService.TombstoneKindMacro));
	}

	[Fact]
	public void DeletePreset_UserSyncedRow_WritesTombstoneWithStoredTimestampAndRaisesOnce()
	{
		using var storage = NewInMemory();
		var preset = new PresetRecord { Name = "P1" };
		var id = storage.InsertPreset(User, UserId, Vid, Pid, preset);
		storage.SetPresetServerUpdatedAt(id, "2026-08-25 11:00:00");
		var events = CountEvents(storage);

		storage.DeletePreset(id);

		Assert.Equal(1, events());
		Assert.Empty(storage.GetPresets(User, UserId, Vid, Pid));
		var (uid, vid, pid, serverUpdatedAt) = Assert.Single(
			storage.GetSyncTombstones(User, UserId, LocalStorageService.TombstoneKindPreset));
		Assert.Equal(preset.Uid, uid);
		Assert.Equal((Vid, Pid), (vid, pid));
		Assert.Equal("2026-08-25 11:00:00", serverUpdatedAt);
	}

	[Fact]
	public void DeletePreset_NeverSyncedUserRow_DeletesWithoutTombstoneOrEvent()
	{
		using var storage = NewInMemory();
		var id = storage.InsertPreset(User, UserId, Vid, Pid, new PresetRecord { Name = "P1" });
		var events = CountEvents(storage);

		storage.DeletePreset(id);

		Assert.Equal(0, events());
		Assert.Empty(storage.GetPresets(User, UserId, Vid, Pid));
		Assert.Empty(storage.GetSyncTombstones(User, UserId, LocalStorageService.TombstoneKindPreset));
	}

	// ── Rev-guarded dirty clear ──────────────────────────────────────────────

	[Fact]
	public void ClearMacroDirty_MatchingRev_ClearsTheFlag()
	{
		using var storage = NewInMemory();
		var macro = NewMacro();
		storage.SaveMacro(User, UserId, Vid, Pid, NoKey, macro); // rev 1
		storage.SetMacroServerUpdatedAt(macro.Id, "ts");

		storage.ClearMacroDirty(macro.Id, 1);

		Assert.Empty(storage.GetDirtyMacros(User, UserId));
	}

	// The rev guard is what makes a mid-push edit survive: the push read rev 1, the edit
	// bumped to 2, so the push's clear must miss and leave the row dirty for the follow-up.
	[Fact]
	public void ClearMacroDirty_StaleRev_LeavesTheReEditedRowDirty()
	{
		using var storage = NewInMemory();
		var macro = NewMacro();
		storage.SaveMacro(User, UserId, Vid, Pid, NoKey, macro); // rev 1 — what the push read
		macro.Name = "edited-mid-push";
		storage.SaveMacro(User, UserId, Vid, Pid, NoKey, macro); // rev 2
		storage.SetMacroServerUpdatedAt(macro.Id, "ts");

		storage.ClearMacroDirty(macro.Id, 1); // the push's stale clear

		var (row, _, _) = Assert.Single(storage.GetDirtyMacros(User, UserId));
		Assert.True(row.IsDirty);
		Assert.Equal(2, row.LocalRev);
	}

	[Fact]
	public void ClearPresetDirty_StaleRevMissesAndCurrentRevClears()
	{
		using var storage = NewInMemory();
		var id = storage.InsertPreset(User, UserId, Vid, Pid, new PresetRecord { Name = "P1" }); // rev 1
		storage.UpdatePreset(id, "P1-edited", ""); // rev 2
		storage.SetPresetServerUpdatedAt(id, "ts");

		storage.ClearPresetDirty(id, 1); // stale — must miss
		Assert.True(Assert.Single(storage.GetDirtyPresets(User, UserId)).Preset.IsDirty);

		storage.ClearPresetDirty(id, 2); // current — clears
		Assert.Empty(storage.GetDirtyPresets(User, UserId));
	}

	// ── Legacy-DB migration ──────────────────────────────────────────────────

	// The presets/macros tables as the previous build (add-api-v2-sync, uid era) created
	// them: uid + server_updated_at present, no is_dirty/local_rev, no sync_tombstones or
	// sync_meta tables. Raw SQL because the current schema block would create the NEW shape —
	// the point is to hand the constructor a database it must migrate.
	private void CreateLegacyDb()
	{
		using var connection = new SqliteConnection($"Data Source={_dbPath}");
		connection.Open();
		using var create = connection.CreateCommand();
		create.CommandText = """
			CREATE TABLE presets (
				id                INTEGER PRIMARY KEY AUTOINCREMENT,
				owner_kind        TEXT NOT NULL,
				owner_id          TEXT NOT NULL,
				vendor_id         INTEGER NOT NULL,
				product_id        INTEGER NOT NULL,
				device_key        TEXT NOT NULL DEFAULT '',
				name              TEXT NOT NULL,
				description       TEXT NOT NULL DEFAULT '',
				created_at        TEXT NOT NULL,
				is_default        INTEGER NOT NULL DEFAULT 0,
				uid               TEXT NOT NULL DEFAULT '',
				server_updated_at TEXT
			);
			CREATE TABLE macros (
				id                INTEGER PRIMARY KEY AUTOINCREMENT,
				owner_kind        TEXT NOT NULL,
				owner_id          TEXT NOT NULL,
				vendor_id         INTEGER NOT NULL,
				product_id        INTEGER NOT NULL,
				device_key        TEXT NOT NULL DEFAULT '',
				name              TEXT NOT NULL,
				repeat_count      INTEGER NOT NULL DEFAULT 1,
				repeat_mode       INTEGER NOT NULL DEFAULT 0,
				created_at        TEXT NOT NULL,
				uid               TEXT NOT NULL DEFAULT '',
				server_updated_at TEXT
			);
			INSERT INTO presets (owner_kind, owner_id, vendor_id, product_id, name, description, created_at, is_default, uid, server_updated_at)
			VALUES ('user', '42', 1241, 41535, 'LEGACY-P', 'legacy desc', '2026-01-01T00:00:00Z', 1,
				'11111111-2222-3333-4444-555555555555', '2026-01-01 00:00:00');
			INSERT INTO macros (owner_kind, owner_id, vendor_id, product_id, name, repeat_count, repeat_mode, created_at, uid, server_updated_at)
			VALUES ('user', '42', 1241, 41535, 'LEGACY-M', 3, 0, '2026-01-01T00:00:00Z',
				'66666666-7777-8888-9999-aaaaaaaaaaaa', '2026-01-02 00:00:00');
			CREATE TABLE notifications (
				id          INTEGER PRIMARY KEY AUTOINCREMENT,
				owner_kind  TEXT NOT NULL,
				owner_id    TEXT NOT NULL,
				title       TEXT NOT NULL,
				subtitle    TEXT NOT NULL DEFAULT '',
				icon_data   TEXT NOT NULL DEFAULT '',
				icon_uri    TEXT NOT NULL DEFAULT '',
				icon_width  REAL NOT NULL DEFAULT 22,
				icon_height REAL NOT NULL DEFAULT 12,
				created_at  TEXT NOT NULL,
				is_read     INTEGER NOT NULL DEFAULT 0
			);
			INSERT INTO notifications (owner_kind, owner_id, title, subtitle, created_at, is_read)
			VALUES ('user', '42', 'LEGACY-N', 'legacy subtitle', '2026-01-03T00:00:00.0000000', 1);
			""";
		create.ExecuteNonQuery();
	}

	[Fact]
	public void LegacyDb_MigrationAddsColumnsAndTablesWithoutTouchingData()
	{
		CreateLegacyDb();

		using var storage = new LocalStorageService($"Data Source={_dbPath}");

		// Existing data is untouched...
		var preset = Assert.Single(storage.GetPresets(User, UserId, Vid, Pid));
		Assert.Equal("LEGACY-P", preset.Name);
		Assert.Equal("legacy desc", preset.Description);
		Assert.True(preset.IsDefault);
		Assert.Equal("11111111-2222-3333-4444-555555555555", preset.Uid);
		Assert.Equal("2026-01-01 00:00:00", preset.ServerUpdatedAt);
		var macro = Assert.Single(storage.GetMacros(User, UserId, Vid, Pid, NoKey));
		Assert.Equal("LEGACY-M", macro.Name);
		Assert.Equal(3, macro.RepeatCount);
		Assert.Equal("2026-01-02 00:00:00", macro.ServerUpdatedAt);

		// ...and nothing is retroactively dirty: synced legacy rows owe the server nothing.
		Assert.Empty(storage.GetDirtyPresets(User, UserId));
		Assert.Empty(storage.GetDirtyMacros(User, UserId));

		// The new tables exist and are empty/usable.
		Assert.Empty(storage.GetSyncTombstones(User, UserId, LocalStorageService.TombstoneKindPreset));
		Assert.Empty(storage.GetSyncTombstones(User, UserId, LocalStorageService.TombstoneKindMacro));
		storage.SetSyncMeta(User, UserId, LocalStorageService.SyncMetaPreferencesUpdatedAt, "ts");
		Assert.Equal("ts", storage.GetSyncMeta(User, UserId, LocalStorageService.SyncMetaPreferencesUpdatedAt));

		// A pre-sync notification row survives with the new columns' defaults — server_id
		// NULL AND is_dirty 0, so it renders as before and is never pushed
		// (docs/plans/add-api-v21-notifications-sync).
		var bell = Assert.Single(storage.GetNotifications(User, UserId));
		Assert.Equal("LEGACY-N", bell.Title);
		Assert.True(bell.IsRead);
		Assert.Null(bell.ServerId);
		Assert.Equal(string.Empty, bell.Type);
		Assert.Empty(storage.GetDirtyNotifications(User, UserId));

		// And the migrated columns are live: the first edit marks the legacy row.
		storage.UpdatePreset(preset.Id, "LEGACY-P-EDITED", preset.Description);
		var (dirty, _, _) = Assert.Single(storage.GetDirtyPresets(User, UserId));
		Assert.True(dirty.IsDirty);
		Assert.Equal(1, dirty.LocalRev);
	}

	// ── Device roster marks (docs/plans/add-api-v21-device-roster) ───────────
	// The devices-table mutators record roster intent in sync_meta: AddDevice writes
	// `deviceroster.<vid>:<pid>.add` (cancelling a pending .remove), DeleteDevice writes
	// `.remove` only when the last card of a settings identity goes (cancelling a pending
	// .add). Keys use the card's SETTINGS identity, falling back to the routing pair for
	// legacy rows. Guest rows and the pull's markDirty: false writes do none of it.

	private static DeviceRecord Mouse(string name = "PG Taipan", string deviceKey = "") => new()
	{
		Name = name,
		DeviceTypeLabel = "Wireless Mouse",
		CardType = "single",
		VendorId = Vid,
		ProductId = Pid,
		DeviceKey = deviceKey,
	};

	private static string AddKey(int vid, int pid) => LocalStorageService.SyncMetaDeviceRosterAdd(vid, pid);
	private static string RemoveKey(int vid, int pid) => LocalStorageService.SyncMetaDeviceRosterRemove(vid, pid);

	[Fact]
	public void AddDevice_UserRow_WritesAddMarkAndRaisesOnce()
	{
		using var storage = NewInMemory();
		var events = CountEvents(storage);

		storage.AddDevice(User, UserId, Mouse());

		Assert.Equal(1, events());
		// The flag's value is a generation (clear-after-success guard), not a literal "1".
		Assert.NotNull(storage.GetSyncMeta(User, UserId, AddKey(Vid, Pid)));
		Assert.Null(storage.GetSyncMeta(User, UserId, RemoveKey(Vid, Pid)));
	}

	// The mark is keyed by the settings identity — the pair the server rosters by — not the
	// routing pair, which for a merged-transport product follows whichever transport is live.
	[Fact]
	public void AddDevice_SettingsIdentitySet_KeysTheMarkByIt()
	{
		using var storage = NewInMemory();
		var rampage = Mouse("PG RAMPAGE");
		rampage.MergeTransports = true;
		rampage.VendorId = 1389;
		rampage.ProductId = 49271;
		rampage.SettingsVendorId = 10473;
		rampage.SettingsProductId = 12288;

		storage.AddDevice(User, UserId, rampage);

		Assert.NotNull(storage.GetSyncMeta(User, UserId, AddKey(10473, 12288)));
		Assert.Null(storage.GetSyncMeta(User, UserId, AddKey(1389, 49271)));
	}

	[Fact]
	public void DeleteDevice_LastCardOfModel_SwapsAddToRemoveAndRaisesOnce()
	{
		using var storage = NewInMemory();
		var id = storage.AddDevice(User, UserId, Mouse());
		var events = CountEvents(storage);

		storage.DeleteDevice(User, UserId, id);

		Assert.Equal(1, events());
		Assert.NotNull(storage.GetSyncMeta(User, UserId, RemoveKey(Vid, Pid)));
		// Last intent wins: the add the card's persist wrote is cancelled, not pushed.
		Assert.Null(storage.GetSyncMeta(User, UserId, AddKey(Vid, Pid)));
	}

	// The server rosters MODELS: with a second identical unit still stored, deleting one
	// card must not unregister the model.
	[Fact]
	public void DeleteDevice_SecondUnitOfModelRemains_WritesNoRemoveMark()
	{
		using var storage = NewInMemory();
		var idA = storage.AddDevice(User, UserId, Mouse(deviceKey: "container-a"));
		storage.AddDevice(User, UserId, Mouse(deviceKey: "container-b"));
		var events = CountEvents(storage);

		storage.DeleteDevice(User, UserId, idA);

		Assert.Equal(0, events());
		Assert.Null(storage.GetSyncMeta(User, UserId, RemoveKey(Vid, Pid)));
	}

	[Fact]
	public void AddDevice_AfterDelete_SwapsRemoveBackToAdd()
	{
		using var storage = NewInMemory();
		var id = storage.AddDevice(User, UserId, Mouse());
		storage.DeleteDevice(User, UserId, id);

		storage.AddDevice(User, UserId, Mouse());

		Assert.NotNull(storage.GetSyncMeta(User, UserId, AddKey(Vid, Pid)));
		Assert.Null(storage.GetSyncMeta(User, UserId, RemoveKey(Vid, Pid)));
	}

	[Fact]
	public void AddDevice_GuestRow_WritesNoMarksAndRaisesNothing()
	{
		using var storage = NewInMemory();
		var events = CountEvents(storage);

		var id = storage.AddDevice(Guest, "", Mouse());
		storage.DeleteDevice(Guest, "", id);

		Assert.Equal(0, events());
		Assert.Null(storage.GetSyncMeta(Guest, "", AddKey(Vid, Pid)));
		Assert.Null(storage.GetSyncMeta(Guest, "", RemoveKey(Vid, Pid)));
	}

	// The sign-in pull's hydration shape: the row IS the server's roster, so re-adding it
	// would be an echo. The row itself must still land.
	[Fact]
	public void AddDevice_PullShaped_WritesNoMarkAndRaisesNothing()
	{
		using var storage = NewInMemory();
		var events = CountEvents(storage);

		storage.AddDevice(User, UserId, Mouse(), markDirty: false);

		Assert.Equal(0, events());
		Assert.Null(storage.GetSyncMeta(User, UserId, AddKey(Vid, Pid)));
		Assert.Equal("PG Taipan", Assert.Single(storage.GetDevices(User, UserId)).Name);
	}

	// Sign-up migration: the migrated cards' models are owed to the new account's roster.
	// No signal — the upgrade push that follows ends in SyncCompleted, which triggers the
	// coordinator pass that consumes these.
	[Fact]
	public async Task MigrateGuestToUser_MarksMigratedModelsWithoutSignalling()
	{
		using var storage = NewInMemory();
		// Guest device rows carry owner_id = '' (session-scoped as a whole, matched by
		// owner_kind alone) — the same shape Flush(session) writes.
		storage.AddDevice(Guest, "", Mouse());
		var events = CountEvents(storage);

		await storage.MigrateGuestToUserAsync(Guid.NewGuid(), 42);

		Assert.Equal(0, events());
		Assert.NotNull(storage.GetSyncMeta(User, UserId, AddKey(Vid, Pid)));
		Assert.Equal("PG Taipan", Assert.Single(storage.GetDevices(User, UserId)).Name);
	}

	// ── Notification sync tracking (docs/plans/add-api-v21-notifications-sync) ─
	// The bell's mutators: user rows mark dirty (rev-bumped) and raise the sync event, the
	// batch commands record their read_all / delete_all intents, a synced delete tombstones
	// by SERVER id, and guest / pull-shaped writes do none of it.

	private static NotificationRecord Bell(string title = "N1", string type = "info") => new()
	{
		Title = title,
		Subtitle = "PG Taipan",
		CreatedAt = DateTime.Now,
		Type = type,
	};

	[Fact]
	public void AddNotification_UserRow_MarksDirtyStoresTypeAndRaisesOnce()
	{
		using var storage = NewInMemory();
		var events = CountEvents(storage);

		storage.AddNotification(User, UserId, Bell(type: "low_battery"));

		Assert.Equal(1, events());
		var row = Assert.Single(storage.GetDirtyNotifications(User, UserId));
		Assert.True(row.IsDirty);
		Assert.Equal(1, row.LocalRev);
		Assert.Equal("low_battery", row.Type);
		Assert.Null(row.ServerId);
	}

	[Fact]
	public void AddNotification_GuestRow_NeverMarkedAndNeverRaises()
	{
		using var storage = NewInMemory();
		var owner = GuestId();
		var events = CountEvents(storage);

		storage.AddNotification(Guest, owner, Bell());

		Assert.Equal(0, events());
		Assert.Empty(storage.GetDirtyNotifications(Guest, owner));
		// The row itself still landed — guests keep a local bell, it just never syncs.
		Assert.False(Assert.Single(storage.GetNotifications(Guest, owner)).IsDirty);
	}

	// The pull's shape: markDirty: false plus the server's id. The row must land clean AND
	// silent — marking it would push server content straight back.
	[Fact]
	public void AddNotification_PullShaped_LandsCleanWithServerIdAndRaisesNothing()
	{
		using var storage = NewInMemory();
		var events = CountEvents(storage);

		var record = Bell();
		record.ServerId = 501;
		storage.AddNotification(User, UserId, record, markDirty: false);

		Assert.Equal(0, events());
		Assert.Empty(storage.GetDirtyNotifications(User, UserId));
		Assert.Equal(501, Assert.Single(storage.GetNotifications(User, UserId)).ServerId);
	}

	[Fact]
	public void SetNotificationRead_UserRow_BumpsRevMarksAndRaisesOnce()
	{
		using var storage = NewInMemory();
		var id = storage.AddNotification(User, UserId, Bell());
		storage.ClearNotificationDirty(id, 1);
		var events = CountEvents(storage);

		storage.SetNotificationRead(id, true);

		Assert.Equal(1, events());
		var row = Assert.Single(storage.GetDirtyNotifications(User, UserId));
		Assert.True(row.IsRead);
		Assert.Equal(2, row.LocalRev);
	}

	// The pull's read-state adoption: applied without marking, so it can never echo back.
	[Fact]
	public void SetNotificationRead_PullShaped_AdoptsWithoutMarkingOrRaising()
	{
		using var storage = NewInMemory();
		var record = Bell();
		record.ServerId = 501;
		var id = storage.AddNotification(User, UserId, record, markDirty: false);
		var events = CountEvents(storage);

		storage.SetNotificationRead(id, true, markDirty: false);

		Assert.Equal(0, events());
		Assert.Empty(storage.GetDirtyNotifications(User, UserId));
		Assert.True(Assert.Single(storage.GetNotifications(User, UserId)).IsRead);
	}

	[Fact]
	public void SetNotificationRead_GuestRow_TogglesWithoutMarkingOrRaising()
	{
		using var storage = NewInMemory();
		var owner = GuestId();
		var id = storage.AddNotification(Guest, owner, Bell());
		var events = CountEvents(storage);

		storage.SetNotificationRead(id, true);

		Assert.Equal(0, events());
		Assert.Empty(storage.GetDirtyNotifications(Guest, owner));
		Assert.True(Assert.Single(storage.GetNotifications(Guest, owner)).IsRead);
	}

	// Mark-all-read deliberately writes NO per-row dirty flags — one read_all server call
	// covers the whole list — but records the batch intent and raises once.
	[Fact]
	public void SetAllNotificationsRead_User_WritesReadAllMarkWithoutPerRowDirtyAndRaisesOnce()
	{
		using var storage = NewInMemory();
		var id = storage.AddNotification(User, UserId, Bell());
		storage.ClearNotificationDirty(id, 1);
		var events = CountEvents(storage);

		storage.SetAllNotificationsRead(User, UserId);

		Assert.Equal(1, events());
		Assert.NotNull(storage.GetSyncMeta(User, UserId, LocalStorageService.SyncMetaNotificationsReadAll));
		Assert.Empty(storage.GetDirtyNotifications(User, UserId));
		Assert.True(Assert.Single(storage.GetNotifications(User, UserId)).IsRead);
	}

	[Fact]
	public void DeleteNotification_SyncedUserRow_TombstonesByServerIdAndRaisesOnce()
	{
		using var storage = NewInMemory();
		var record = Bell();
		record.ServerId = 501;
		var id = storage.AddNotification(User, UserId, record, markDirty: false);
		var events = CountEvents(storage);

		storage.DeleteNotification(id);

		Assert.Equal(1, events());
		Assert.Empty(storage.GetNotifications(User, UserId));
		var (uid, vid, pid, serverUpdatedAt) = Assert.Single(
			storage.GetSyncTombstones(User, UserId, LocalStorageService.TombstoneKindNotification));
		Assert.Equal("501", uid);
		Assert.Equal((0, 0), (vid, pid));
		Assert.Null(serverUpdatedAt);
	}

	// A row the server never saw needs no tombstone — there is nothing remote to remove.
	[Fact]
	public void DeleteNotification_NeverSyncedRow_DeletesWithoutTombstoneOrEvent()
	{
		using var storage = NewInMemory();
		var id = storage.AddNotification(User, UserId, Bell());
		var events = CountEvents(storage);

		storage.DeleteNotification(id);

		Assert.Equal(0, events());
		Assert.Empty(storage.GetNotifications(User, UserId));
		Assert.Empty(storage.GetSyncTombstones(User, UserId, LocalStorageService.TombstoneKindNotification));
	}

	// The coordinator's NOTIFICATION_NOT_FOUND cleanup: the server already lost the row, so
	// the local mirror-delete must not mint a tombstone for it.
	[Fact]
	public void DeleteNotification_TombstoneFalse_RemovesSyncedRowWithoutTombstone()
	{
		using var storage = NewInMemory();
		var record = Bell();
		record.ServerId = 501;
		var id = storage.AddNotification(User, UserId, record, markDirty: false);
		var events = CountEvents(storage);

		storage.DeleteNotification(id, tombstone: false);

		Assert.Equal(0, events());
		Assert.Empty(storage.GetNotifications(User, UserId));
		Assert.Empty(storage.GetSyncTombstones(User, UserId, LocalStorageService.TombstoneKindNotification));
	}

	// delete_all subsumes every pending per-row intent: the tombstones and the read_all mark
	// drop in the same transaction, and only the delete_all batch mark survives.
	[Fact]
	public void DeleteAllNotifications_User_WritesDeleteAllAndDropsSubsumedState()
	{
		using var storage = NewInMemory();
		var record = Bell();
		record.ServerId = 501;
		var id = storage.AddNotification(User, UserId, record, markDirty: false);
		storage.SetAllNotificationsRead(User, UserId);
		storage.DeleteNotification(id);
		var events = CountEvents(storage);

		storage.DeleteAllNotifications(User, UserId);

		Assert.Equal(1, events());
		Assert.Empty(storage.GetNotifications(User, UserId));
		Assert.NotNull(storage.GetSyncMeta(User, UserId, LocalStorageService.SyncMetaNotificationsDeleteAll));
		Assert.Null(storage.GetSyncMeta(User, UserId, LocalStorageService.SyncMetaNotificationsReadAll));
		Assert.Empty(storage.GetSyncTombstones(User, UserId, LocalStorageService.TombstoneKindNotification));
	}

	// The rev guard: a toggle landing while a push was in flight bumped local_rev, so the
	// stale clear misses and the row stays enumerated for the follow-up pass.
	[Fact]
	public void ClearNotificationDirty_RevGuard_LeavesReToggledRowDirty()
	{
		using var storage = NewInMemory();
		var id = storage.AddNotification(User, UserId, Bell()); // local_rev 1
		storage.SetNotificationRead(id, true);                  // local_rev 2

		storage.ClearNotificationDirty(id, localRev: 1);
		Assert.True(Assert.Single(storage.GetDirtyNotifications(User, UserId)).IsDirty);

		storage.ClearNotificationDirty(id, localRev: 2);
		Assert.Empty(storage.GetDirtyNotifications(User, UserId));
	}

	[Fact]
	public async Task MigrateGuestToUser_MarksMigratedNotificationsWithoutSignalling()
	{
		using var storage = NewInMemory();
		var guestId = Guid.NewGuid();
		storage.AddNotification(Guest, guestId.ToString("D"), Bell("Guest note"));
		var events = CountEvents(storage);

		await storage.MigrateGuestToUserAsync(guestId, 42);

		Assert.Equal(0, events());
		var row = Assert.Single(storage.GetDirtyNotifications(User, UserId));
		Assert.Equal("Guest note", row.Title);
		Assert.Null(row.ServerId);
	}

	// PR #127, m2: a pre-sync row (server_id NULL and '' type) is contractually local-only —
	// the read toggle, the bell's most common action, must not turn it into a push.
	[Fact]
	public void SetNotificationRead_PreSyncRow_TogglesWithoutMarkingOrRaising()
	{
		using var storage = NewInMemory();
		var id = storage.AddNotification(User, UserId, Bell(type: ""), markDirty: false);
		var events = CountEvents(storage);

		storage.SetNotificationRead(id, true);

		Assert.Equal(0, events());
		Assert.Empty(storage.GetDirtyNotifications(User, UserId));
		Assert.True(Assert.Single(storage.GetNotifications(User, UserId)).IsRead);
	}

	// The same gate on the sign-up migration: pre-sync guest rows move to the new account
	// unmarked; only sync-tracked (typed) rows are owed to the server.
	[Fact]
	public async Task MigrateGuestToUser_LeavesPreSyncRowsUnmarked()
	{
		using var storage = NewInMemory();
		var guestId = Guid.NewGuid();
		storage.AddNotification(Guest, guestId.ToString("D"), Bell("Pre-sync", type: ""));
		storage.AddNotification(Guest, guestId.ToString("D"), Bell("Typed", type: "info"));

		await storage.MigrateGuestToUserAsync(guestId, 42);

		Assert.Equal("Typed", Assert.Single(storage.GetDirtyNotifications(User, UserId)).Title);
		Assert.Equal(2, storage.GetNotifications(User, UserId).Count);
	}

	// PR #127, M1 (residual): the cap trim must never evict a dirty row — that is a push
	// the coordinator still owes, and nothing would re-enumerate it. The table may briefly
	// exceed the cap by the dirty count; the display read stays capped.
	[Fact]
	public void AddNotification_Trim_SparesDirtyRowsBeyondTheCap()
	{
		using var storage = NewInMemory();
		storage.AddNotification(User, UserId, Bell("OWED")); // dirty, lowest id
		for (var i = 0; i < 100; i++)
		{
			var record = Bell($"S{i}");
			record.ServerId = 1000 + i;
			storage.AddNotification(User, UserId, record, markDirty: false);
		}

		// The owed push survived the 100 pull-shaped inserts that would have trimmed it...
		Assert.Equal("OWED", Assert.Single(storage.GetDirtyNotifications(User, UserId)).Title);
		// ...while the display read stays at the cap (the dirty row sits below it by id).
		Assert.Equal(100, storage.GetNotifications(User, UserId).Count);
	}
}
