using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// The re-key half of docs/plans/fix-settings-bucket-identity: LocalStorageService
// .RekeySettingsVendor collapses the second settings bucket a merged-transport product owns when
// one of its transports enumerates under a vendor of its own.
//
// The defect these pin (field report, RAMPAGE keyboard): the board is vendor 10473, its 2.4 GHz
// receiver vendor 1389, and the settings PRODUCT id is already pinned at 12888 on both — so the
// same physical keyboard wrote RGB, DPI, actuation, remaps and presets into TWO buckets,
// (10473, 12888) and (1389, 12888), and the user watched the configuration disappear on a
// transport switch.
//
// The stakes are why this file is long. The plan's Risks section: "a mis-scoped DELETE (one
// missing AND product_id = P) wipes a user's entire configuration for every device under that
// vendor, silently, with no undo." Every table is therefore asserted individually, in both
// directions (moved / left alone), rather than through one representative table.
//
// File-backed, not ":memory:", for the same reason LocalStorageServiceDeviceIdentityTests is: the
// migration only does observable work against an already-populated database, and an in-memory
// store cannot be populated before construction.
public class LocalStorageServiceSettingsVendorRekeyTests : IDisposable
{
	private const string User = LocalStorageService.OwnerKindUser;
	private const string Guest = LocalStorageService.OwnerKindGuest;
	private const string UserId = "42";
	private const string OtherUserId = "77";
	private const string GuestId = "";

	// RAMPAGE. The product id is the SAME on both transports (ResolveSettingsProductId already
	// pins it); only the vendor splits, which is what makes this a vendor rewrite rather than a
	// general re-identification.
	private const int AltVid = 1389;
	private const int CanonVid = 10473;
	private const int Pid = 12888;

	// The bucket the OTHER defect wrote: a card restored before discovery derived its settings
	// product id from the stored ROUTING product id. Those rows are unreachable and deliberately
	// out of the re-key's scope — it maps (alt vendor, pinned pid) only.
	private const int JunkRoutingPid = 49271;

	// An unrelated product that happens to share the alternate vendor. Nothing about it may move.
	private const int OtherPid = 4711;

	private const string NoKey = "";
	private const string UnitKey = "container-id";

	private readonly string _dbPath =
		Path.Combine(Path.GetTempPath(), $"pg-vendorrekey-{Guid.NewGuid():N}.db");

	private LocalStorageService NewFileStore() => new($"Data Source={_dbPath}");

	private string BackupPath => _dbPath + ".pre-vendor-rekey.bak";

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		foreach (var path in new[] { _dbPath, BackupPath })
		{
			try { if (File.Exists(path)) File.Delete(path); }
			catch (IOException) { /* handle may still be releasing */ }
		}
	}

	// ---- Seeding: one row in every settings table, under one bucket --------

	// `marker` is what distinguishes the canonical copy of a row from the alternate copy of the
	// SAME key, so a canonical-wins assertion can name which one survived rather than merely
	// counting rows.
	private static void SeedEverySettingsTable(
		LocalStorageService store, string ownerKind, string ownerId,
		int vendorId, int productId, string deviceKey, string marker, double value)
	{
		store.SaveRgbState(ownerKind, ownerId, vendorId, productId, deviceKey,
			isEnabled: true, selectedEffect: marker,
			[new RgbEffectRecord { EffectKey = "Static", Hex = marker, Brightness = value, Speed = value }]);

		store.SetDeviceSetting(ownerKind, ownerId, vendorId, productId, deviceKey, "dpi.activeStage", value);

		store.ReplaceDeviceActuation(ownerKind, ownerId, vendorId, productId, deviceKey,
			[new ActuationRecord(Slot: 7, PressCentiMm: (int)value, ReleaseCentiMm: 20,
				RapidTrigger: true, QuickPressCentiMm: 5, QuickReleaseCentiMm: 5)]);

		store.SetButtonBinding(ownerKind, ownerId, vendorId, productId, deviceKey,
			button: 3, action: (int)value, macroId: null);

		store.SetKeyBinding(ownerKind, ownerId, vendorId, productId, deviceKey,
			physScancode: 0x1A, typeByte: 1, bitmask: 0, targetCode: (byte)value);

		store.SetKeyMacroBinding(ownerKind, ownerId, vendorId, productId, deviceKey,
			matrixIndex: 34, macroId: (int)value);

		store.SetAppliedPreset(ownerKind, ownerId, vendorId, productId, deviceKey,
			presetId: (int)value, markDirty: false);

		store.InsertPreset(ownerKind, ownerId, vendorId, productId,
			new PresetRecord { Name = marker, Uid = $"preset-{marker}" }, markDirty: false);

		store.SaveMacro(ownerKind, ownerId, vendorId, productId, deviceKey,
			new MacroRecord
			{
				Name = marker,
				Uid = $"macro-{marker}",
				Events = { new MacroEventRecord { KeyCode = 0x04, IsPress = true, DelayMs = 5 } },
			},
			markDirty: false);

		store.SetSyncMeta(ownerKind, ownerId,
			LocalStorageService.SyncMetaDeviceStateDirty(vendorId, productId), marker);
		store.SetSyncMeta(ownerKind, ownerId,
			LocalStorageService.SyncMetaDeviceStateUpdatedAt(vendorId, productId), marker);
	}

	// sync_tombstones has no public writer that takes a vendor directly (a tombstone is a
	// side-effect of DeletePreset / DeleteMacro), so the pending-delete case is seeded raw.
	private void SeedTombstone(string ownerKind, string ownerId, string uid, int vendorId, int productId)
	{
		using var connection = new SqliteConnection($"Data Source={_dbPath}");
		connection.Open();
		using var cmd = connection.CreateCommand();
		cmd.CommandText = """
			INSERT INTO sync_tombstones (owner_kind, owner_id, kind, uid, vendor_id, product_id, server_updated_at)
			VALUES ($ok, $oi, $k, $uid, $v, $p, 'ts');
			""";
		cmd.Parameters.AddWithValue("$ok", ownerKind);
		cmd.Parameters.AddWithValue("$oi", ownerId);
		cmd.Parameters.AddWithValue("$k", LocalStorageService.TombstoneKindPreset);
		cmd.Parameters.AddWithValue("$uid", uid);
		cmd.Parameters.AddWithValue("$v", vendorId);
		cmd.Parameters.AddWithValue("$p", productId);
		cmd.ExecuteNonQuery();
	}

	// Row counts straight from SQLite, so an assertion can say "this table lost rows" even for a
	// bucket no public reader addresses any more.
	private long CountRows(string table, string where)
	{
		using var connection = new SqliteConnection($"Data Source={_dbPath}");
		connection.Open();
		using var cmd = connection.CreateCommand();
		cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {where}";
		return Convert.ToInt64(cmd.ExecuteScalar());
	}

	// The whole database as a comparable snapshot, used by the rollback and idempotence tests to
	// assert "byte-identical" rather than "the rows I happened to think of are unchanged".
	private List<string> SnapshotEverySettingsRow(string? excludedTable = null)
	{
		var rows = new List<string>();
		using var connection = new SqliteConnection($"Data Source={_dbPath}");
		connection.Open();
		foreach (var table in new[]
		{
			"device_rgb", "device_rgb_effect", "device_settings", "device_actuation",
			"button_bindings", "key_bindings", "key_macro_bindings", "device_applied_preset",
			"presets", "macros", "sync_meta", "sync_tombstones",
		})
		{
			// Skipped before the read, not filtered after it: the rollback test DROPs a table to
			// induce the failure, so reading it would throw inside the assertion helper.
			if (table == excludedTable)
				continue;
			using var cmd = connection.CreateCommand();
			cmd.CommandText = $"SELECT * FROM {table}";
			using var reader = cmd.ExecuteReader();
			var columns = reader.FieldCount;
			var table_rows = new List<string>();
			while (reader.Read())
			{
				var cells = new string[columns];
				for (var i = 0; i < columns; i++)
					cells[i] = reader.IsDBNull(i) ? "<null>" : reader.GetValue(i).ToString() ?? "";
				table_rows.Add(string.Join("|", cells));
			}
			table_rows.Sort(StringComparer.Ordinal);
			rows.AddRange(table_rows.Select(r => $"{table}::{r}"));
		}
		return rows;
	}

	// ---- The reproduction: one physical device, two buckets ----------------

	// THE REPRODUCTION. Every settings table holds a row under the alternate vendor and nothing
	// under the canonical one — the database of a user who configured their RAMPAGE over its
	// dongle. After the pass every one of those rows must be readable under the canonical vendor,
	// which is the bucket the fixed App now addresses on every transport.
	//
	// Pre-fix there is no RekeySettingsVendor at all, so those rows stayed under 1389 forever and
	// a cable-connected board came up on defaults.
	[Fact]
	public void SplitBucket_IsCollapsed_AcrossEverySettingsTable()
	{
		using var store = NewFileStore();
		SeedEverySettingsTable(store, User, UserId, AltVid, Pid, NoKey, "dongle", 1);
		SeedTombstone(User, UserId, "tombstoned-uid", AltVid, Pid);

		store.RekeySettingsVendor(AltVid, CanonVid, Pid);

		// device_rgb + device_rgb_effect
		var rgb = store.GetRgbState(User, UserId, CanonVid, Pid, NoKey);
		Assert.NotNull(rgb);
		Assert.Equal("dongle", rgb!.SelectedEffect);
		Assert.Equal("dongle", Assert.Single(rgb.Effects).Hex);

		// device_settings
		Assert.Equal(1d, store.GetDeviceSettings(User, UserId, CanonVid, Pid, NoKey)["dpi.activeStage"]);

		// device_actuation
		Assert.Equal(1, Assert.Single(store.GetDeviceActuation(User, UserId, CanonVid, Pid, NoKey)).PressCentiMm);

		// button_bindings
		Assert.Equal(1, Assert.Single(store.GetButtonBindings(User, UserId, CanonVid, Pid, NoKey)).Action);

		// key_bindings
		Assert.Equal(1, Assert.Single(store.GetKeyBindings(User, UserId, CanonVid, Pid, NoKey)).TargetCode);

		// key_macro_bindings
		Assert.Equal(1, Assert.Single(store.GetKeyMacroBindings(User, UserId, CanonVid, Pid, NoKey)).MacroId);

		// device_applied_preset
		Assert.Equal(1, Assert.Single(store.GetAppliedPresets(User, UserId, CanonVid, Pid)).PresetId);

		// presets
		Assert.Equal("dongle", Assert.Single(store.GetPresets(User, UserId, CanonVid, Pid)).Name);

		// macros
		Assert.Equal("dongle", Assert.Single(store.GetMacros(User, UserId, CanonVid, Pid, NoKey)).Name);

		// sync_meta device-state keys
		Assert.Equal("dongle", store.GetSyncMeta(User, UserId,
			LocalStorageService.SyncMetaDeviceStateDirty(CanonVid, Pid)));
		Assert.Equal("dongle", store.GetSyncMeta(User, UserId,
			LocalStorageService.SyncMetaDeviceStateUpdatedAt(CanonVid, Pid)));

		// sync_tombstones
		Assert.Equal(CanonVid, Assert.Single(store.GetSyncTombstones(
			User, UserId, LocalStorageService.TombstoneKindPreset)).VendorId);
	}

	// The other half of the same pass: nothing may be LEFT under the alternate vendor, or the
	// device still owns two buckets and the next write picks whichever the code happens to name.
	[Fact]
	public void SplitBucket_LeavesNothingBehind_UnderTheAlternateVendor()
	{
		using var store = NewFileStore();
		SeedEverySettingsTable(store, User, UserId, AltVid, Pid, NoKey, "dongle", 1);
		SeedTombstone(User, UserId, "tombstoned-uid", AltVid, Pid);

		store.RekeySettingsVendor(AltVid, CanonVid, Pid);

		foreach (var table in new[]
		{
			"device_rgb", "device_rgb_effect", "device_settings", "device_actuation",
			"button_bindings", "key_bindings", "key_macro_bindings", "device_applied_preset",
			"presets", "macros", "sync_tombstones",
		})
		{
			Assert.Equal(0, CountRows(table, $"vendor_id = {AltVid} AND product_id = {Pid}"));
		}

		Assert.Null(store.GetSyncMeta(User, UserId,
			LocalStorageService.SyncMetaDeviceStateDirty(AltVid, Pid)));
		Assert.Null(store.GetSyncMeta(User, UserId,
			LocalStorageService.SyncMetaDeviceStateUpdatedAt(AltVid, Pid)));
	}

	// ---- Reconciliation: canonical wins, per row ---------------------------

	// Both buckets populated — the user configured the board on the cable AND on the dongle. The
	// product ruling (Open question 3): what the wired/primary transport holds is what is kept.
	// Asserted per table, because "canonical wins" implemented as "alternate wins" would still
	// leave exactly one row in every table and pass a count-only test.
	[Fact]
	public void RowPresentUnderBothVendors_KeepsTheCanonicalValue()
	{
		using var store = NewFileStore();
		SeedEverySettingsTable(store, User, UserId, AltVid, Pid, NoKey, "dongle", 1);
		SeedEverySettingsTable(store, User, UserId, CanonVid, Pid, NoKey, "cable", 2);

		store.RekeySettingsVendor(AltVid, CanonVid, Pid);

		var rgb = store.GetRgbState(User, UserId, CanonVid, Pid, NoKey);
		Assert.Equal("cable", rgb!.SelectedEffect);
		Assert.Equal("cable", Assert.Single(rgb.Effects).Hex);
		Assert.Equal(2d, store.GetDeviceSettings(User, UserId, CanonVid, Pid, NoKey)["dpi.activeStage"]);
		Assert.Equal(2, Assert.Single(store.GetDeviceActuation(User, UserId, CanonVid, Pid, NoKey)).PressCentiMm);
		Assert.Equal(2, Assert.Single(store.GetButtonBindings(User, UserId, CanonVid, Pid, NoKey)).Action);
		Assert.Equal(2, Assert.Single(store.GetKeyBindings(User, UserId, CanonVid, Pid, NoKey)).TargetCode);
		Assert.Equal(2, Assert.Single(store.GetKeyMacroBindings(User, UserId, CanonVid, Pid, NoKey)).MacroId);
		Assert.Equal(2, Assert.Single(store.GetAppliedPresets(User, UserId, CanonVid, Pid)).PresetId);
		Assert.Equal("cable", store.GetSyncMeta(User, UserId,
			LocalStorageService.SyncMetaDeviceStateDirty(CanonVid, Pid)));
	}

	// presets and macros have a surrogate id and a UNIQUE uid, so their scope columns are not a
	// key and the two copies cannot collide: BOTH survive the move. That is deliberate — losing a
	// preset the user made on one transport would be exactly the data loss this plan exists to
	// stop — and it is the one place where the collapsed bucket holds more rows than either side
	// did, which is why it is asserted apart from the canonical-wins tables above.
	[Fact]
	public void PresetsAndMacros_PresentUnderBothVendors_AreBothKept()
	{
		using var store = NewFileStore();
		SeedEverySettingsTable(store, User, UserId, AltVid, Pid, NoKey, "dongle", 1);
		SeedEverySettingsTable(store, User, UserId, CanonVid, Pid, NoKey, "cable", 2);

		store.RekeySettingsVendor(AltVid, CanonVid, Pid);

		Assert.Equal(
			["cable", "dongle"],
			store.GetPresets(User, UserId, CanonVid, Pid).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
		Assert.Equal(
			["cable", "dongle"],
			store.GetMacros(User, UserId, CanonVid, Pid, NoKey).Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal));
	}

	// A key that exists ONLY under the alternate vendor is adopted rather than dropped: the
	// INSERT OR IGNORE half of the rule. Seeded on a different key from the canonical row so the
	// two do not collide, which is what makes "adopted" distinguishable from "ignored".
	[Fact]
	public void RowPresentOnlyUnderTheAlternateVendor_IsAdopted()
	{
		using var store = NewFileStore();
		store.SetDeviceSetting(User, UserId, CanonVid, Pid, NoKey, "dpi.activeStage", 2);
		store.SetDeviceSetting(User, UserId, AltVid, Pid, NoKey, "dpi.activeStage", 1);
		// Only the alternate bucket has this one.
		store.SetDeviceSetting(User, UserId, AltVid, Pid, NoKey, "reportRateHz", 1000);
		store.SetButtonBinding(User, UserId, AltVid, Pid, NoKey, button: 9, action: 5, macroId: null);

		store.RekeySettingsVendor(AltVid, CanonVid, Pid);

		var settings = store.GetDeviceSettings(User, UserId, CanonVid, Pid, NoKey);
		Assert.Equal(2d, settings["dpi.activeStage"]);   // canonical kept
		Assert.Equal(1000d, settings["reportRateHz"]);   // alternate-only adopted
		Assert.Equal(5, Assert.Single(store.GetButtonBindings(User, UserId, CanonVid, Pid, NoKey)).Action);
	}

	// No table's row count may GROW for the PK-keyed tables: the pass moves and merges, it never
	// invents. (presets/macros are excluded by construction -- see the test above.)
	[Fact]
	public void Rekey_NeverIncreasesTheRowCount_OfAnyKeyedTable()
	{
		using var store = NewFileStore();
		SeedEverySettingsTable(store, User, UserId, AltVid, Pid, NoKey, "dongle", 1);
		SeedEverySettingsTable(store, User, UserId, CanonVid, Pid, NoKey, "cable", 2);

		var before = new[]
		{
			"device_rgb", "device_rgb_effect", "device_settings", "device_actuation",
			"button_bindings", "key_bindings", "key_macro_bindings", "device_applied_preset",
		}.ToDictionary(t => t, t => CountRows(t, "1 = 1"));

		store.RekeySettingsVendor(AltVid, CanonVid, Pid);

		foreach (var (table, count) in before)
			Assert.True(CountRows(table, "1 = 1") <= count, $"{table} grew");
	}

	// ---- Scope containment: what the pass must NOT touch --------------------

	// The plan's headline risk, stated as a test: one missing `AND product_id = P` on any DELETE
	// wipes every device under that vendor. Another product sharing the alternate vendor must come
	// out of the pass completely unchanged -- not moved, and above all not deleted.
	//
	// Also covers the junk bucket the OTHER defect wrote, at the ROUTING product id: those rows are
	// unreachable but they are NOT deleted, because a row seeded from defaults cannot be told apart
	// from one the user edited on a pre-discovery page.
	[Fact]
	public void Rekey_DoesNotTouchAnotherProduct_NorTheJunkRoutingProductBucket()
	{
		using var store = NewFileStore();
		SeedEverySettingsTable(store, User, UserId, AltVid, Pid, NoKey, "dongle", 1);
		SeedEverySettingsTable(store, User, UserId, AltVid, OtherPid, NoKey, "other-product", 3);
		SeedEverySettingsTable(store, User, UserId, AltVid, JunkRoutingPid, NoKey, "junk", 4);

		var otherBefore = SnapshotEverySettingsRow()
			.Where(r => r.Contains($"|{AltVid}|{OtherPid}|") || r.Contains($"|{AltVid}|{JunkRoutingPid}|"))
			.ToList();
		Assert.NotEmpty(otherBefore);

		store.RekeySettingsVendor(AltVid, CanonVid, Pid);

		// Byte-identical, not merely "still present".
		var otherAfter = SnapshotEverySettingsRow()
			.Where(r => r.Contains($"|{AltVid}|{OtherPid}|") || r.Contains($"|{AltVid}|{JunkRoutingPid}|"))
			.ToList();
		Assert.Equal(otherBefore, otherAfter);

		// And they are still readable through the public API under their original bucket.
		Assert.Equal(3d, store.GetDeviceSettings(User, UserId, AltVid, OtherPid, NoKey)["dpi.activeStage"]);
		Assert.Equal(4d, store.GetDeviceSettings(User, UserId, AltVid, JunkRoutingPid, NoKey)["dpi.activeStage"]);
	}

	// Vendor/product 0 is the "identity unknown" sentinel this file uses everywhere. A pass that
	// accepted it would address every pre-scoping row in the database with `WHERE vendor_id = 0`,
	// so the guard is the difference between a no-op and a catastrophe.
	[Theory]
	[InlineData(0, CanonVid, Pid)]
	[InlineData(AltVid, 0, Pid)]
	[InlineData(AltVid, CanonVid, 0)]
	[InlineData(-1, CanonVid, Pid)]
	// Same vendor on both sides: nothing to move, and the INSERT/DELETE pair would delete the rows
	// it had just declined to re-insert.
	[InlineData(CanonVid, CanonVid, Pid)]
	public void Rekey_WithAnUnusableIdentity_ChangesNothing(int fromVendorId, int toVendorId, int productId)
	{
		using var store = NewFileStore();
		SeedEverySettingsTable(store, User, UserId, AltVid, Pid, NoKey, "dongle", 1);
		SeedEverySettingsTable(store, User, UserId, CanonVid, Pid, NoKey, "cable", 2);
		var before = SnapshotEverySettingsRow();

		Assert.Equal(0, store.RekeySettingsVendor(fromVendorId, toVendorId, productId));

		Assert.Equal(before, SnapshotEverySettingsRow());
	}

	// device_key is a UNIT identity, not a transport one: two physical RAMPAGEs must stay apart
	// through the pass. A re-key that dropped device_key from the carried columns would collapse
	// them into one bucket and silently merge two keyboards' configurations.
	[Fact]
	public void Rekey_KeepsUnitsApart_ByDeviceKey()
	{
		using var store = NewFileStore();
		store.SetDeviceSetting(User, UserId, AltVid, Pid, NoKey, "dpi.activeStage", 1);
		store.SetDeviceSetting(User, UserId, AltVid, Pid, UnitKey, "dpi.activeStage", 2);

		store.RekeySettingsVendor(AltVid, CanonVid, Pid);

		Assert.Equal(1d, store.GetDeviceSettings(User, UserId, CanonVid, Pid, NoKey)["dpi.activeStage"]);
		Assert.Equal(2d, store.GetDeviceSettings(User, UserId, CanonVid, Pid, UnitKey)["dpi.activeStage"]);
	}

	// The vendor mapping is a property of the hardware, not of an account (Open question 2), so a
	// pass run while user A is signed in migrates user B's and the guest's rows for the same
	// product too. Otherwise those accounts stay split until they happen to be active with the
	// device connected.
	[Fact]
	public void Rekey_MigratesEveryOwner_NotOnlyTheCurrentOne()
	{
		using var store = NewFileStore();
		SeedEverySettingsTable(store, User, UserId, AltVid, Pid, NoKey, "a", 1);
		SeedEverySettingsTable(store, User, OtherUserId, AltVid, Pid, NoKey, "b", 2);
		SeedEverySettingsTable(store, Guest, GuestId, AltVid, Pid, NoKey, "g", 3);

		store.RekeySettingsVendor(AltVid, CanonVid, Pid);

		Assert.Equal("a", store.GetRgbState(User, UserId, CanonVid, Pid, NoKey)!.SelectedEffect);
		Assert.Equal("b", store.GetRgbState(User, OtherUserId, CanonVid, Pid, NoKey)!.SelectedEffect);
		Assert.Equal("g", store.GetRgbState(Guest, GuestId, CanonVid, Pid, NoKey)!.SelectedEffect);
		Assert.Equal(0, CountRows("device_rgb", $"vendor_id = {AltVid}"));
	}

	// ---- Idempotence, atomicity, backup ------------------------------------

	// Idempotent by construction: after a pass no row remains under the alternate vendor, so a
	// re-run matches nothing. Asserted as "byte-identical", not "still works" -- a second pass
	// that re-ran the INSERT/DELETE pair against the CANONICAL bucket would delete everything.
	[Fact]
	public void Rekey_IsIdempotent_AcrossRepeatedRuns()
	{
		using var store = NewFileStore();
		SeedEverySettingsTable(store, User, UserId, AltVid, Pid, NoKey, "dongle", 1);
		SeedEverySettingsTable(store, User, UserId, CanonVid, Pid, NoKey, "cable", 2);
		SeedTombstone(User, UserId, "tombstoned-uid", AltVid, Pid);

		Assert.True(store.RekeySettingsVendor(AltVid, CanonVid, Pid) > 0);
		var afterFirst = SnapshotEverySettingsRow();

		// 0 affected rows is the observable definition of "a no-op".
		Assert.Equal(0, store.RekeySettingsVendor(AltVid, CanonVid, Pid));
		Assert.Equal(afterFirst, SnapshotEverySettingsRow());

		// And across a restart, which is when the App would actually call it again.
		Assert.Equal(0, store.RekeySettingsVendor(AltVid, CanonVid, Pid));
		Assert.Equal(afterFirst, SnapshotEverySettingsRow());
	}

	// The pass is one transaction over every table. A failure part-way must leave the database
	// exactly as it was -- a half-migrated device split ACROSS two buckets is worse than the bug.
	//
	// The failure is induced the only way that reaches the middle of the pass without touching
	// production code: sync_meta's PK is (owner_kind, owner_id, key), so dropping the table makes
	// the sync_meta step throw AFTER all eight keyed tables and presets/macros have been moved
	// inside the open transaction. If the pass were not transactional, those moves would survive.
	[Fact]
	public void Rekey_FailureMidPass_LeavesTheDatabaseUnchanged()
	{
		using var store = NewFileStore();
		SeedEverySettingsTable(store, User, UserId, AltVid, Pid, NoKey, "dongle", 1);
		var before = SnapshotEverySettingsRowExcept("sync_meta");

		using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
		{
			connection.Open();
			using var drop = connection.CreateCommand();
			drop.CommandText = "DROP TABLE sync_meta;";
			drop.ExecuteNonQuery();
		}

		Assert.ThrowsAny<SqliteException>(() => store.RekeySettingsVendor(AltVid, CanonVid, Pid));

		// Every table the pass had already rewritten is back where it started.
		Assert.Equal(before, SnapshotEverySettingsRowExcept("sync_meta"));
		Assert.Equal(1, CountRows("device_rgb", $"vendor_id = {AltVid} AND product_id = {Pid}"));
		Assert.Equal(0, CountRows("device_rgb", $"vendor_id = {CanonVid} AND product_id = {Pid}"));

		// The backup was taken BEFORE the transaction, so a failed pass still leaves the user a
		// copy -- which is the entire point of writing it.
		Assert.True(File.Exists(BackupPath), "pre-migration backup missing after a failed pass");
	}

	private List<string> SnapshotEverySettingsRowExcept(string excludedTable) =>
		SnapshotEverySettingsRow(excludedTable);

	// The backup is the last line of defence against a bug in everything above, so it must exist
	// before the first pass touches a row.
	[Fact]
	public void Rekey_WritesThePreMigrationBackup_BeforeTheFirstPass()
	{
		using var store = NewFileStore();
		SeedEverySettingsTable(store, User, UserId, AltVid, Pid, NoKey, "dongle", 1);
		Assert.False(File.Exists(BackupPath));

		store.RekeySettingsVendor(AltVid, CanonVid, Pid);

		Assert.True(File.Exists(BackupPath));
	}

	// Written once and NEVER overwritten: a second pass (or a later app run) must not replace the
	// pre-migration copy with a post-migration one, or the backup silently stops being a backup.
	[Fact]
	public void Rekey_NeverOverwritesAnExistingBackup()
	{
		using (var store = NewFileStore())
		{
			SeedEverySettingsTable(store, User, UserId, AltVid, Pid, NoKey, "dongle", 1);
			store.RekeySettingsVendor(AltVid, CanonVid, Pid);
		}
		SqliteConnection.ClearAllPools();

		var originalBackup = File.ReadAllBytes(BackupPath);
		// The backup predates the migration, so it still holds the SPLIT database.
		Assert.NotEqual(0, CountBackupRows($"vendor_id = {AltVid} AND product_id = {Pid}"));

		// A fresh store is a fresh process as far as the once-per-process flag is concerned.
		using (var store = NewFileStore())
		{
			SeedEverySettingsTable(store, User, UserId, AltVid, Pid, NoKey, "again", 5);
			store.RekeySettingsVendor(AltVid, CanonVid, Pid);
		}
		SqliteConnection.ClearAllPools();

		Assert.Equal(originalBackup, File.ReadAllBytes(BackupPath));
	}

	private long CountBackupRows(string where)
	{
		using var connection = new SqliteConnection($"Data Source={BackupPath}");
		connection.Open();
		using var cmd = connection.CreateCommand();
		cmd.CommandText = $"SELECT COUNT(*) FROM device_rgb WHERE {where}";
		return Convert.ToInt64(cmd.ExecuteScalar());
	}

	// ---- Preset / macro sync invariants ------------------------------------

	// "Exactly one default per (owner, vendor, product)" is relied on by
	// CreateDefaultPresetIfNotExists and by the sync pull's default arbitration, so a migrated
	// default must be demoted when the canonical scope already owns one. Two rows named "Default"
	// is the accepted cosmetic cost; two rows FLAGGED default is a broken invariant.
	[Fact]
	public void MigratedPresets_AreDemoted_WhenTheCanonicalScopeAlreadyHasADefault()
	{
		using var store = NewFileStore();
		store.InsertPreset(User, UserId, CanonVid, Pid,
			new PresetRecord { Name = "Default", IsDefault = true, Uid = "canonical" }, markDirty: false);
		store.InsertPreset(User, UserId, AltVid, Pid,
			new PresetRecord { Name = "Default", IsDefault = true, Uid = "alternate" }, markDirty: false);

		store.RekeySettingsVendor(AltVid, CanonVid, Pid);

		var presets = store.GetPresets(User, UserId, CanonVid, Pid);
		Assert.Equal(2, presets.Count);
		Assert.Equal("canonical", Assert.Single(presets, p => p.IsDefault).Uid);
	}

	// Demotion is conditional: with NO default in the canonical scope the migrated one must keep
	// its flag, or the device arrives with no default preset at all.
	[Fact]
	public void MigratedDefaultPreset_IsKept_WhenTheCanonicalScopeHasNone()
	{
		using var store = NewFileStore();
		store.InsertPreset(User, UserId, CanonVid, Pid,
			new PresetRecord { Name = "Custom", IsDefault = false, Uid = "canonical" }, markDirty: false);
		store.InsertPreset(User, UserId, AltVid, Pid,
			new PresetRecord { Name = "Default", IsDefault = true, Uid = "alternate" }, markDirty: false);

		store.RekeySettingsVendor(AltVid, CanonVid, Pid);

		Assert.Equal("alternate",
			Assert.Single(store.GetPresets(User, UserId, CanonVid, Pid), p => p.IsDefault).Uid);
	}

	// Demotion is per OWNER: user B having a default must not demote user A's migrated one.
	[Fact]
	public void PresetDemotion_IsScopedPerOwner()
	{
		using var store = NewFileStore();
		store.InsertPreset(User, OtherUserId, CanonVid, Pid,
			new PresetRecord { Name = "Default", IsDefault = true, Uid = "b-canonical" }, markDirty: false);
		store.InsertPreset(User, UserId, AltVid, Pid,
			new PresetRecord { Name = "Default", IsDefault = true, Uid = "a-alternate" }, markDirty: false);

		store.RekeySettingsVendor(AltVid, CanonVid, Pid);

		// A had no canonical default of their own, so theirs survives.
		Assert.Equal("a-alternate",
			Assert.Single(store.GetPresets(User, UserId, CanonVid, Pid), p => p.IsDefault).Uid);
	}

	// uid is the sync identity: preserving it is what stops the next pull re-inserting these as
	// duplicates. And the rows must NOT be marked dirty -- their content did not change, only the
	// local label, and mass-dirtying would push the whole library on the next sync pass.
	[Fact]
	public void MigratedPresetsAndMacros_KeepTheirUid_AndAreNotMarkedDirty()
	{
		using var store = NewFileStore();
		SeedEverySettingsTable(store, User, UserId, AltVid, Pid, NoKey, "dongle", 1);

		store.RekeySettingsVendor(AltVid, CanonVid, Pid);

		var preset = Assert.Single(store.GetPresets(User, UserId, CanonVid, Pid));
		Assert.Equal("preset-dongle", preset.Uid);
		Assert.False(preset.IsDirty, "migrating a preset must not queue it for a server push");

		var macro = Assert.Single(store.GetMacros(User, UserId, CanonVid, Pid, NoKey));
		Assert.Equal("macro-dongle", macro.Uid);
		Assert.False(macro.IsDirty, "migrating a macro must not queue it for a server push");
	}
}
