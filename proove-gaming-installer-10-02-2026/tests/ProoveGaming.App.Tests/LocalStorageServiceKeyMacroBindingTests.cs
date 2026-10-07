using System;
using System.IO;
using Microsoft.Data.Sqlite;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// key_macro_bindings is the only record of which library macro a keyboard key plays — the device
// blob holds event lists with no ids, so a lost row means the key silently stops working while
// firmware still holds the old binding.
//
// These tests use a temp FILE database on purpose. Every other suite here uses
// "Data Source=:memory:", where the schema is created once and lives as long as the single
// connection; that cannot observe anything the schema block does to an already-populated
// database. A DROP TABLE added to that block emptied this table on every app start and all
// in-memory tests stayed green.
public class LocalStorageServiceKeyMacroBindingTests : IDisposable
{
	private const int Vid = 14000;
	private const int Pid = 12385;
	private const string DeviceKey = "";
	private const string Owner = "owner-1";

	private readonly string _dbPath =
		Path.Combine(Path.GetTempPath(), $"pg-keymacro-{Guid.NewGuid():N}.db");

	private LocalStorageService NewStore() => new($"Data Source={_dbPath}");

	public void Dispose()
	{
		try { if (File.Exists(_dbPath)) File.Delete(_dbPath); }
		catch (IOException) { /* the connection may still be releasing the handle */ }
	}

	[Fact]
	public void Binding_SurvivesReopeningTheDatabase()
	{
		using (var store = NewStore())
		{
			store.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, Owner, Vid, Pid, DeviceKey,
				matrixIndex: 33, macroId: 101);
		}

		// Second construction re-runs EnsureSchema over a populated database — the exact path an
		// app restart takes.
		using (var reopened = NewStore())
		{
			var row = Assert.Single(reopened.GetKeyMacroBindings(
				LocalStorageService.OwnerKindGuest, Owner, Vid, Pid, DeviceKey));
			Assert.Equal(33, row.MatrixIndex);
			Assert.Equal(101, row.MacroId);
		}
	}

	[Fact]
	public void Binding_UpsertsOnSameKeyAndDeletesIndependently()
	{
		using var store = NewStore();

		store.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, Owner, Vid, Pid, DeviceKey, 33, 101);
		store.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, Owner, Vid, Pid, DeviceKey, 34, 102);

		// Same key, different macro — replaces rather than duplicating (PK covers matrix_index).
		store.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, Owner, Vid, Pid, DeviceKey, 33, 103);

		var rows = store.GetKeyMacroBindings(LocalStorageService.OwnerKindGuest, Owner, Vid, Pid, DeviceKey);
		Assert.Equal(2, rows.Count);
		Assert.Equal(103, Assert.Single(rows, r => r.MatrixIndex == 33).MacroId);

		store.DeleteKeyMacroBinding(LocalStorageService.OwnerKindGuest, Owner, Vid, Pid, DeviceKey, 33);

		var remaining = Assert.Single(store.GetKeyMacroBindings(
			LocalStorageService.OwnerKindGuest, Owner, Vid, Pid, DeviceKey));
		Assert.Equal(34, remaining.MatrixIndex);
	}

	// Deleting a macro must clear every key pointing at it, on every device and owner — otherwise
	// a key keeps a slot index that now refers to whatever macro shifted into that position.
	[Fact]
	public void DeleteBindingsForMacro_ClearsEveryKeyPointingAtIt()
	{
		using var store = NewStore();

		store.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, Owner, Vid, Pid, DeviceKey, 33, 101);
		store.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, Owner, Vid, Pid, DeviceKey, 34, 101);
		store.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, Owner, Vid, Pid, DeviceKey, 35, 102);

		store.DeleteKeyMacroBindingsForMacro(101);

		var remaining = Assert.Single(store.GetKeyMacroBindings(
			LocalStorageService.OwnerKindGuest, Owner, Vid, Pid, DeviceKey));
		Assert.Equal(102, remaining.MacroId);
	}

	// Bindings are scoped per owner and per device; one must not leak into another's view.
	[Fact]
	public void Bindings_AreScopedByOwnerAndDevice()
	{
		using var store = NewStore();

		store.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, Owner, Vid, Pid, DeviceKey, 33, 101);
		store.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, "other-owner", Vid, Pid, DeviceKey, 33, 999);
		store.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, Owner, Vid, 12290, DeviceKey, 33, 888);

		var mine = Assert.Single(store.GetKeyMacroBindings(
			LocalStorageService.OwnerKindGuest, Owner, Vid, Pid, DeviceKey));
		Assert.Equal(101, mine.MacroId);
	}

	// MIGRATION GUARD. Making key_macro_bindings preset-scoped means LoadPresetSnapshotToDevice
	// now wipes the live rows and copies the snapshot back. Every database that predates this
	// change has presets but no preset_key_macro_bindings rows — so without a one-time seed, the
	// user's first preset switch after upgrading deletes every macro binding they have and
	// restores nothing. Silent, permanent, and only reproducible on a real upgraded database.
	//
	// Simulated by building a current database, dropping the snapshot table, then re-opening:
	// EnsureSchema sees the table missing and seeds it from the live bindings.
	[Fact]
	public void UpgradingADatabaseWithBindings_SeedsExistingPresets_SoASwitchDoesNotWipeThem()
	{
		int presetId;
		using (var store = NewStore())
		{
			presetId = store.InsertPreset(LocalStorageService.OwnerKindGuest, Owner, Vid, Pid,
				new PresetRecord { Name = "P1" });
			store.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, Owner, Vid, Pid, DeviceKey, 33, 101);
			store.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, Owner, Vid, Pid, DeviceKey, 35, 102);
		}

		// Roll the schema back to its pre-change shape.
		using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
		{
			connection.Open();
			using var cmd = connection.CreateCommand();
			cmd.CommandText = "DROP TABLE preset_key_macro_bindings;";
			cmd.ExecuteNonQuery();
		}

		using (var store = NewStore())
		{
			// The upgrade path runs in the constructor. Applying the preset must now be a no-op
			// for macro bindings rather than a wipe.
			store.LoadPresetSnapshotToDevice(presetId, LocalStorageService.OwnerKindGuest, Owner, Vid, Pid, DeviceKey);

			var bindings = store.GetKeyMacroBindings(LocalStorageService.OwnerKindGuest, Owner, Vid, Pid, DeviceKey);
			Assert.Equal(2, bindings.Count);
			Assert.Contains((33, 101), bindings);
			Assert.Contains((35, 102), bindings);
		}
	}
}
