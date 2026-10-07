using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// Macros are per physical device. These tests cover the one-time migration that assigns
// pre-scoping macros to the device that references them.
//
// A temp FILE database is used deliberately. Every other LocalStorageService suite uses
// "Data Source=:memory:", where the schema block runs once against an empty database and the
// connection lives for the test — which cannot observe what that block does to an
// already-populated database. A migration is exactly that case, and an earlier DROP TABLE
// regression in this file stayed green across the whole in-memory suite.
public class LocalStorageServiceMacroScopeTests : IDisposable
{
	private const string Owner = "owner-1";
	private const int MouseVid = 0x04D9;
	private const int MousePid = 0xA23F;
	private const int KbVid = 14000;
	private const int KbPid = 12385;
	private const string NoKey = "";

	private readonly string _dbPath =
		Path.Combine(Path.GetTempPath(), $"pg-macroscope-{Guid.NewGuid():N}.db");

	private LocalStorageService NewStore() => new($"Data Source={_dbPath}");

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		try { if (File.Exists(_dbPath)) File.Delete(_dbPath); }
		catch (IOException) { /* handle may still be releasing */ }
	}

	private static MacroRecord Macro(string name) => new()
	{
		Name = name,
		RepeatCount = 1,
		Mode = MacroRepeatMode.MacroPlayCount,
		Events =
		{
			new MacroEventRecord { KeyCode = 0x04, IsPress = true, DelayMs = 10 },
			new MacroEventRecord { KeyCode = 0x04, IsPress = false, DelayMs = 20 },
		},
	};

	// Writes a macro with no device columns — the shape a pre-migration database holds. Done with
	// raw SQL because the public API cannot produce an unscoped row any more, which is the point.
	private int InsertLegacyMacro(string name)
	{
		using var connection = new SqliteConnection($"Data Source={_dbPath}");
		connection.Open();

		int id;
		using (var cmd = connection.CreateCommand())
		{
			cmd.CommandText = """
				INSERT INTO macros (owner_kind, owner_id, vendor_id, product_id, device_key, name, repeat_count, repeat_mode, created_at)
				VALUES ($ok, $oi, 0, 0, '', $n, 1, 0, $at);
				SELECT last_insert_rowid();
				""";
			cmd.Parameters.AddWithValue("$ok", LocalStorageService.OwnerKindGuest);
			cmd.Parameters.AddWithValue("$oi", Owner);
			cmd.Parameters.AddWithValue("$n", name);
			cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
			id = Convert.ToInt32(cmd.ExecuteScalar());
		}

		using (var ev = connection.CreateCommand())
		{
			ev.CommandText = """
				INSERT INTO macro_events (macro_id, ordinal, key_code, is_press, delay_ms)
				VALUES ($id, 0, 4, 1, 10), ($id, 1, 4, 0, 20);
				""";
			ev.Parameters.AddWithValue("$id", id);
			ev.ExecuteNonQuery();
		}

		return id;
	}

	// Bindings are written with raw SQL for the same reason as the macro itself: constructing a
	// LocalStorageService runs the migration, so the whole pre-migration state — macro AND the
	// references that own it — has to exist before the first construction. Writing the binding
	// through the API would trigger a pass that correctly culls the not-yet-referenced macro.
	private void InsertLegacyButtonBinding(int vendorId, int productId, int button, int macroId)
	{
		using var connection = new SqliteConnection($"Data Source={_dbPath}");
		connection.Open();
		using var cmd = connection.CreateCommand();
		cmd.CommandText = """
			INSERT INTO button_bindings (owner_kind, owner_id, vendor_id, product_id, device_key, button, action, macro_id)
			VALUES ($ok, $oi, $v, $p, '', $btn, $act, $mid);
			""";
		cmd.Parameters.AddWithValue("$ok", LocalStorageService.OwnerKindGuest);
		cmd.Parameters.AddWithValue("$oi", Owner);
		cmd.Parameters.AddWithValue("$v", vendorId);
		cmd.Parameters.AddWithValue("$p", productId);
		cmd.Parameters.AddWithValue("$btn", button);
		cmd.Parameters.AddWithValue("$act", (int)MouseButtonAction.Macro);
		cmd.Parameters.AddWithValue("$mid", macroId);
		cmd.ExecuteNonQuery();
	}

	private void InsertLegacyKeyMacroBinding(int vendorId, int productId, int matrixIndex, int macroId)
	{
		using var connection = new SqliteConnection($"Data Source={_dbPath}");
		connection.Open();
		using var cmd = connection.CreateCommand();
		cmd.CommandText = """
			INSERT INTO key_macro_bindings (owner_kind, owner_id, vendor_id, product_id, device_key, matrix_index, macro_id)
			VALUES ($ok, $oi, $v, $p, '', $mi, $mid);
			""";
		cmd.Parameters.AddWithValue("$ok", LocalStorageService.OwnerKindGuest);
		cmd.Parameters.AddWithValue("$oi", Owner);
		cmd.Parameters.AddWithValue("$v", vendorId);
		cmd.Parameters.AddWithValue("$p", productId);
		cmd.Parameters.AddWithValue("$mi", matrixIndex);
		cmd.Parameters.AddWithValue("$mid", macroId);
		cmd.ExecuteNonQuery();
	}

	private int CountMacroRows()
	{
		using var connection = new SqliteConnection($"Data Source={_dbPath}");
		connection.Open();
		using var cmd = connection.CreateCommand();
		cmd.CommandText = "SELECT COUNT(*) FROM macros";
		return Convert.ToInt32(cmd.ExecuteScalar());
	}

	private int CountEventRows(int macroId)
	{
		using var connection = new SqliteConnection($"Data Source={_dbPath}");
		connection.Open();
		using var cmd = connection.CreateCommand();
		cmd.CommandText = "SELECT COUNT(*) FROM macro_events WHERE macro_id = $id";
		cmd.Parameters.AddWithValue("$id", macroId);
		return Convert.ToInt32(cmd.ExecuteScalar());
	}

	// ── Normal per-device behaviour ──────────────────────────────────────────

	[Fact]
	public void Macros_AreScopedToTheDeviceTheyWereSavedOn()
	{
		using var store = NewStore();

		store.SaveMacro(LocalStorageService.OwnerKindGuest, Owner, MouseVid, MousePid, NoKey, Macro("MouseOnly"));
		store.SaveMacro(LocalStorageService.OwnerKindGuest, Owner, KbVid, KbPid, NoKey, Macro("KeyOnly"));

		var mouse = store.GetMacros(LocalStorageService.OwnerKindGuest, Owner, MouseVid, MousePid, NoKey);
		var keyboard = store.GetMacros(LocalStorageService.OwnerKindGuest, Owner, KbVid, KbPid, NoKey);

		Assert.Equal("MouseOnly", Assert.Single(mouse).Name);
		Assert.Equal("KeyOnly", Assert.Single(keyboard).Name);
	}

	[Fact]
	public void Macro_SurvivesReopeningTheDatabase()
	{
		int id;
		using (var store = NewStore())
			id = store.SaveMacro(LocalStorageService.OwnerKindGuest, Owner, KbVid, KbPid, NoKey, Macro("Kept"));

		using (var reopened = NewStore())
		{
			var row = Assert.Single(reopened.GetMacros(LocalStorageService.OwnerKindGuest, Owner, KbVid, KbPid, NoKey));
			Assert.Equal(id, row.Id);
			Assert.Equal(2, row.Events.Count);
		}
	}

	// Updating must not be able to move a macro to whichever device is open at the time.
	[Fact]
	public void SaveMacro_Update_DoesNotRehomeTheMacro()
	{
		using var store = NewStore();
		var id = store.SaveMacro(LocalStorageService.OwnerKindGuest, Owner, KbVid, KbPid, NoKey, Macro("Edited"));

		var edit = Macro("Edited twice");
		edit.Id = id;
		store.SaveMacro(LocalStorageService.OwnerKindGuest, Owner, MouseVid, MousePid, NoKey, edit);

		Assert.Empty(store.GetMacros(LocalStorageService.OwnerKindGuest, Owner, MouseVid, MousePid, NoKey));
		var still = Assert.Single(store.GetMacros(LocalStorageService.OwnerKindGuest, Owner, KbVid, KbPid, NoKey));
		Assert.Equal("Edited twice", still.Name);
	}

	// ── Migration ───────────────────────────────────────────────────────────

	[Fact]
	public void Migration_UnboundMacro_IsDeletedWithItsEvents()
	{
		using (var store = NewStore()) { /* create schema */ }

		var id = InsertLegacyMacro("Orphan");
		Assert.Equal(1, CountMacroRows());

		using (var reopened = NewStore())
			Assert.Empty(reopened.GetMacros(LocalStorageService.OwnerKindGuest, Owner, MouseVid, MousePid, NoKey));

		Assert.Equal(0, CountMacroRows());
		Assert.Equal(0, CountEventRows(id));
	}

	[Fact]
	public void Migration_MacroBoundToOneDevice_KeepsItsIdAndGainsThatDevice()
	{
		using (var store = NewStore()) { /* create schema */ }
		var id = InsertLegacyMacro("Bound");

		InsertLegacyButtonBinding(MouseVid, MousePid, (int)MouseButton.ButtonRight, id);

		using (var reopened = NewStore())
		{
			var row = Assert.Single(reopened.GetMacros(LocalStorageService.OwnerKindGuest, Owner, MouseVid, MousePid, NoKey));
			Assert.Equal(id, row.Id);   // id preserved, so the binding still resolves
			Assert.Equal(2, row.Events.Count);

			var binding = Assert.Single(reopened.GetButtonBindings(
				LocalStorageService.OwnerKindGuest, Owner, MouseVid, MousePid, NoKey));
			Assert.Equal(id, binding.MacroId);
		}
	}

	// The case one row cannot express: both bindings must keep working, so the macro is duplicated
	// and each device's binding is repointed at its own copy.
	[Fact]
	public void Migration_MacroBoundToTwoDevices_IsDuplicatedAndBothBindingsResolve()
	{
		using (var store = NewStore()) { /* create schema */ }
		var id = InsertLegacyMacro("Shared");

		InsertLegacyButtonBinding(MouseVid, MousePid, (int)MouseButton.ButtonRight, id);
		InsertLegacyKeyMacroBinding(KbVid, KbPid, 33, id);

		using (var reopened = NewStore())
		{
			var mouse = Assert.Single(reopened.GetMacros(LocalStorageService.OwnerKindGuest, Owner, MouseVid, MousePid, NoKey));
			var keyboard = Assert.Single(reopened.GetMacros(LocalStorageService.OwnerKindGuest, Owner, KbVid, KbPid, NoKey));

			Assert.NotEqual(mouse.Id, keyboard.Id);
			Assert.Equal("Shared", mouse.Name);
			Assert.Equal("Shared", keyboard.Name);
			// Events are copied, ordinals preserved, so playback order survives.
			Assert.Equal(2, mouse.Events.Count);
			Assert.Equal(2, keyboard.Events.Count);
			Assert.True(mouse.Events[0].IsPress);
			Assert.True(keyboard.Events[0].IsPress);

			var binding = Assert.Single(reopened.GetButtonBindings(
				LocalStorageService.OwnerKindGuest, Owner, MouseVid, MousePid, NoKey));
			Assert.Equal(mouse.Id, binding.MacroId);

			var keyBinding = Assert.Single(reopened.GetKeyMacroBindings(
				LocalStorageService.OwnerKindGuest, Owner, KbVid, KbPid, NoKey));
			Assert.Equal(keyboard.Id, keyBinding.MacroId);
		}

		Assert.Equal(2, CountMacroRows());
	}

	// preset_button_bindings holds macro_id with no foreign key, so a preset-only reference is the
	// third way a macro can be owned. Dropping those would delete macros a saved preset still needs.
	[Fact]
	public void Migration_MacroReferencedOnlyByAPreset_Survives()
	{
		int presetId;
		using (var store = NewStore())
			presetId = store.InsertPreset(LocalStorageService.OwnerKindGuest, Owner, MouseVid, MousePid,
				new PresetRecord { Name = "P1" });

		var id = InsertLegacyMacro("PresetOnly");

		using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
		{
			connection.Open();
			using var cmd = connection.CreateCommand();
			cmd.CommandText = """
				INSERT INTO preset_button_bindings (preset_id, button, action, macro_id)
				VALUES ($pid, $btn, $act, $mid);
				""";
			cmd.Parameters.AddWithValue("$pid", presetId);
			cmd.Parameters.AddWithValue("$btn", (int)MouseButton.ButtonRight);
			cmd.Parameters.AddWithValue("$act", (int)MouseButtonAction.Macro);
			cmd.Parameters.AddWithValue("$mid", id);
			cmd.ExecuteNonQuery();
		}

		using (var reopened = NewStore())
		{
			var row = Assert.Single(reopened.GetMacros(LocalStorageService.OwnerKindGuest, Owner, MouseVid, MousePid, NoKey));
			Assert.Equal(id, row.Id);
			Assert.Equal("PresetOnly", row.Name);
		}
	}

	// The migration deletes rows, so a second pass must be a no-op rather than a second cull.
	[Fact]
	public void Migration_IsIdempotentAcrossRepeatedOpens()
	{
		using (var store = NewStore()) { /* create schema */ }
		var id = InsertLegacyMacro("Bound");

		InsertLegacyButtonBinding(MouseVid, MousePid, (int)MouseButton.ButtonRight, id);

		using (var first = NewStore())
			Assert.Single(first.GetMacros(LocalStorageService.OwnerKindGuest, Owner, MouseVid, MousePid, NoKey));

		var afterFirst = CountMacroRows();

		using (var second = NewStore())
			Assert.Single(second.GetMacros(LocalStorageService.OwnerKindGuest, Owner, MouseVid, MousePid, NoKey));
		using (var third = NewStore())
			Assert.Single(third.GetMacros(LocalStorageService.OwnerKindGuest, Owner, MouseVid, MousePid, NoKey));

		Assert.Equal(afterFirst, CountMacroRows());
		Assert.Equal(2, CountEventRows(id));
	}

	// A device-scoped macro created after the migration must not be touched by a later pass.
	[Fact]
	public void Migration_LeavesAlreadyScopedMacrosAlone()
	{
		int id;
		using (var store = NewStore())
			id = store.SaveMacro(LocalStorageService.OwnerKindGuest, Owner, KbVid, KbPid, NoKey, Macro("Scoped"));

		using (var reopened = NewStore())
		{
			var row = Assert.Single(reopened.GetMacros(LocalStorageService.OwnerKindGuest, Owner, KbVid, KbPid, NoKey));
			Assert.Equal(id, row.Id);
		}

		Assert.Equal(1, CountMacroRows());
	}

	// ── uid + server_updated_at (docs/plans/add-api-v2-sync) ─────────────────
	// The macro-side counterpart of the uid coverage in LocalStorageServicePresetsTests:
	// generation at insert, uniqueness, the legacy-DB backfill, survival of the guest → user
	// rekey, and the opaque server_updated_at round-trip. The file database this class
	// already uses is exactly what the backfill cases need.

	[Fact]
	public void SaveMacro_AssignsLowercase36CharUid_AndNoServerUpdatedAt()
	{
		using var store = NewStore();

		var record = Macro("M1");
		store.SaveMacro(LocalStorageService.OwnerKindGuest, Owner, MouseVid, MousePid, NoKey, record);

		var row = Assert.Single(store.GetMacros(LocalStorageService.OwnerKindGuest, Owner, MouseVid, MousePid, NoKey));
		Assert.Equal(36, row.Uid.Length);
		Assert.True(Guid.TryParseExact(row.Uid, "D", out _));
		Assert.Equal(row.Uid, row.Uid.ToLowerInvariant());
		Assert.Null(row.ServerUpdatedAt);
		// The record passed to SaveMacro is stamped with the same uid the row got.
		Assert.Equal(row.Uid, record.Uid);
	}

	[Fact]
	public void SaveMacro_GeneratedUids_AreDistinctAcrossInserts()
	{
		using var store = NewStore();

		for (var i = 0; i < 5; i++)
			store.SaveMacro(LocalStorageService.OwnerKindGuest, Owner, MouseVid, MousePid, NoKey, Macro($"M{i}"));

		var uids = store.GetMacros(LocalStorageService.OwnerKindGuest, Owner, MouseVid, MousePid, NoKey)
			.Select(m => m.Uid)
			.ToList();
		Assert.Equal(5, uids.Count);
		Assert.All(uids, u => Assert.NotEqual(string.Empty, u));
		Assert.Equal(5, uids.Distinct().Count());
	}

	// The uid is the macro's identity for the server: an edit must not mint a new one.
	[Fact]
	public void SaveMacro_Update_KeepsTheUid()
	{
		using var store = NewStore();
		var id = store.SaveMacro(LocalStorageService.OwnerKindGuest, Owner, KbVid, KbPid, NoKey, Macro("Edited"));
		var uid = Assert.Single(store.GetMacros(LocalStorageService.OwnerKindGuest, Owner, KbVid, KbPid, NoKey)).Uid;

		var edit = Macro("Edited twice");
		edit.Id = id;
		store.SaveMacro(LocalStorageService.OwnerKindGuest, Owner, KbVid, KbPid, NoKey, edit);

		Assert.Equal(uid, Assert.Single(store.GetMacros(LocalStorageService.OwnerKindGuest, Owner, KbVid, KbPid, NoKey)).Uid);
	}

	// server_updated_at is opaque by contract (no timezone in the API's format) — verbatim
	// round-trip, never parsed.
	[Fact]
	public void SetMacroServerUpdatedAt_RoundTripsTheOpaqueString()
	{
		using var store = NewStore();
		var id = store.SaveMacro(LocalStorageService.OwnerKindGuest, Owner, MouseVid, MousePid, NoKey, Macro("Synced"));

		store.SetMacroServerUpdatedAt(id, "2026-08-25 17:03:09");

		var row = Assert.Single(store.GetMacros(LocalStorageService.OwnerKindGuest, Owner, MouseVid, MousePid, NoKey));
		Assert.Equal("2026-08-25 17:03:09", row.ServerUpdatedAt);
	}

	// The macros/macro_events tables as the previous app version created them: device-scoped
	// already (real vendor ids, so MigrateMacrosToDevices leaves the rows alone) but with no
	// uid / server_updated_at columns. Raw SQL for the same reason as InsertLegacyMacro: the
	// current schema block would create the NEW shape.
	private void CreateLegacyMacrosDb(params string[] names)
	{
		using var connection = new SqliteConnection($"Data Source={_dbPath}");
		connection.Open();
		using (var create = connection.CreateCommand())
		{
			create.CommandText = """
				CREATE TABLE macros (
					id           INTEGER PRIMARY KEY AUTOINCREMENT,
					owner_kind   TEXT NOT NULL DEFAULT '',
					owner_id     TEXT NOT NULL DEFAULT '',
					vendor_id    INTEGER NOT NULL DEFAULT 0,
					product_id   INTEGER NOT NULL DEFAULT 0,
					device_key   TEXT NOT NULL DEFAULT '',
					name         TEXT NOT NULL,
					repeat_count INTEGER NOT NULL DEFAULT 1,
					repeat_mode  INTEGER NOT NULL DEFAULT 0,
					created_at   TEXT NOT NULL
				);
				CREATE TABLE macro_events (
					id       INTEGER PRIMARY KEY AUTOINCREMENT,
					macro_id INTEGER NOT NULL,
					ordinal  INTEGER NOT NULL,
					key_code INTEGER NOT NULL,
					is_press INTEGER NOT NULL,
					delay_ms INTEGER NOT NULL
				);
				""";
			create.ExecuteNonQuery();
		}

		foreach (var name in names)
		{
			int id;
			using (var insert = connection.CreateCommand())
			{
				insert.CommandText = """
					INSERT INTO macros (owner_kind, owner_id, vendor_id, product_id, device_key, name, repeat_count, repeat_mode, created_at)
					VALUES ($ok, $oi, $v, $p, '', $n, 1, 0, $at);
					SELECT last_insert_rowid();
					""";
				insert.Parameters.AddWithValue("$ok", LocalStorageService.OwnerKindGuest);
				insert.Parameters.AddWithValue("$oi", Owner);
				insert.Parameters.AddWithValue("$v", KbVid);
				insert.Parameters.AddWithValue("$p", KbPid);
				insert.Parameters.AddWithValue("$n", name);
				insert.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
				id = Convert.ToInt32(insert.ExecuteScalar());
			}

			using (var ev = connection.CreateCommand())
			{
				ev.CommandText = """
					INSERT INTO macro_events (macro_id, ordinal, key_code, is_press, delay_ms)
					VALUES ($id, 0, 4, 1, 10), ($id, 1, 4, 0, 20);
					""";
				ev.Parameters.AddWithValue("$id", id);
				ev.ExecuteNonQuery();
			}
		}
	}

	[Fact]
	public void LegacyDb_MacrosWithoutUidColumns_GetDistinctUids_AndDataIsUntouched()
	{
		CreateLegacyMacrosDb("A", "B", "C");

		using var store = NewStore();

		var rows = store.GetMacros(LocalStorageService.OwnerKindGuest, Owner, KbVid, KbPid, NoKey);
		Assert.Equal(3, rows.Count);
		Assert.All(rows, m => Assert.True(Guid.TryParseExact(m.Uid, "D", out _)));
		Assert.Equal(3, rows.Select(m => m.Uid).Distinct().Count());
		Assert.All(rows, m => Assert.Null(m.ServerUpdatedAt));
		// Existing data untouched: names, order and events (with ordinals) all survive.
		Assert.Equal(new[] { "A", "B", "C" }, rows.Select(m => m.Name).ToArray());
		Assert.All(rows, m =>
		{
			Assert.Equal(2, m.Events.Count);
			Assert.True(m.Events[0].IsPress);
			Assert.False(m.Events[1].IsPress);
		});
	}

	[Fact]
	public void LegacyDb_BackfilledMacroUids_AreStableAcrossReopens()
	{
		CreateLegacyMacrosDb("A", "B");

		List<string> first;
		using (var store = NewStore())
			first = store.GetMacros(LocalStorageService.OwnerKindGuest, Owner, KbVid, KbPid, NoKey)
				.Select(m => m.Uid).ToList();

		using (var reopened = NewStore())
		{
			var second = reopened.GetMacros(LocalStorageService.OwnerKindGuest, Owner, KbVid, KbPid, NoKey)
				.Select(m => m.Uid).ToList();
			Assert.Equal(first, second);
		}
	}

	[Fact]
	public async Task MigrateGuestToUserAsync_PreservesMacroUids()
	{
		using var store = NewStore();
		var guestSession = Guid.NewGuid();
		var guestId = guestSession.ToString("D");

		store.SaveMacro(LocalStorageService.OwnerKindGuest, guestId, KbVid, KbPid, NoKey, Macro("A"));
		store.SaveMacro(LocalStorageService.OwnerKindGuest, guestId, KbVid, KbPid, NoKey, Macro("B"));
		var before = store.GetMacros(LocalStorageService.OwnerKindGuest, guestId, KbVid, KbPid, NoKey)
			.ToDictionary(m => m.Name, m => m.Uid);

		await store.MigrateGuestToUserAsync(guestSession, 42);

		var after = store.GetMacros(LocalStorageService.OwnerKindUser, "42", KbVid, KbPid, NoKey);
		Assert.Equal(2, after.Count);
		Assert.All(after, m => Assert.Equal(before[m.Name], m.Uid));
		Assert.Empty(store.GetMacros(LocalStorageService.OwnerKindGuest, guestId, KbVid, KbPid, NoKey));
	}
}
