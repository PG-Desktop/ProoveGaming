using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

public class LocalStorageServiceSettingsTests
{
    // Each test gets a fresh, in-memory DB (the connection lifetime IS the DB lifetime).
    private static LocalStorageService NewInMemory() =>
        new("Data Source=:memory:");

    private const int Vid = 0x04D9;
    private const int Pid = 0xA23F;
    private const string DeviceKey = "";

    private static string GuestId() => Guid.NewGuid().ToString("D");
    private static string UserId(int customer) => customer.ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void SetDeviceSetting_Insert_ThenRead_ReturnsSameValue()
    {
        using var storage = NewInMemory();
        var owner = GuestId();

        storage.SetDeviceSetting(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, "polling_rate", 500);

        var all = storage.GetDeviceSettings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        Assert.Equal(500, all["polling_rate"]);
    }

    [Fact]
    public void SetDeviceSetting_Upsert_OverwritesPreviousValue()
    {
        using var storage = NewInMemory();
        var owner = GuestId();

        storage.SetDeviceSetting(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, "polling_rate", 500);
        storage.SetDeviceSetting(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, "polling_rate", 1000);

        var all = storage.GetDeviceSettings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        Assert.Equal(1000, all["polling_rate"]);
    }

    [Fact]
    public void SetDeviceSetting_DifferentOwners_DoNotCollide()
    {
        using var storage = NewInMemory();
        var guest = GuestId();
        var user = UserId(42);

        storage.SetDeviceSetting(LocalStorageService.OwnerKindGuest, guest, Vid, Pid, DeviceKey, "polling_rate", 250);
        storage.SetDeviceSetting(LocalStorageService.OwnerKindUser, user, Vid, Pid, DeviceKey, "polling_rate", 1000);

        Assert.Equal(250,  storage.GetDeviceSettings(LocalStorageService.OwnerKindGuest, guest, Vid, Pid, DeviceKey)["polling_rate"]);
        Assert.Equal(1000, storage.GetDeviceSettings(LocalStorageService.OwnerKindUser, user, Vid, Pid, DeviceKey)["polling_rate"]);
    }

    [Fact]
    public void SetDeviceSetting_DifferentDeviceKeys_DoNotCollide()
    {
        using var storage = NewInMemory();
        var owner = GuestId();

        // Two physically distinct units sharing the same (vid, pid) — disambiguated by DeviceKey.
        storage.SetDeviceSetting(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, "unit-a", "polling_rate", 250);
        storage.SetDeviceSetting(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, "unit-b", "polling_rate", 1000);

        Assert.Equal(250,  storage.GetDeviceSettings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, "unit-a")["polling_rate"]);
        Assert.Equal(1000, storage.GetDeviceSettings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, "unit-b")["polling_rate"]);
    }

    [Fact]
    public void GetDeviceSettings_UnknownKey_ReturnsEmptyDictionary()
    {
        using var storage = NewInMemory();
        var all = storage.GetDeviceSettings(LocalStorageService.OwnerKindGuest, GuestId(), Vid, Pid, DeviceKey);
        Assert.Empty(all);
    }

    [Fact]
    public async Task MigrateGuestToUserAsync_MovesGuestRowsToUser()
    {
        using var storage = NewInMemory();
        var guest = Guid.NewGuid();
        const int customer = 99;

        storage.SetDeviceSetting(LocalStorageService.OwnerKindGuest, guest.ToString("D"), Vid, Pid, DeviceKey, "polling_rate", 500);
        storage.SetDeviceSetting(LocalStorageService.OwnerKindGuest, guest.ToString("D"), Vid, Pid, DeviceKey, "dpi.activeStage", 2);
        storage.SetAppPreference(LocalStorageService.OwnerKindGuest, guest.ToString("D"), "ScrollLines", 7);

        await storage.MigrateGuestToUserAsync(guest, customer);

        Assert.Empty(storage.GetDeviceSettings(LocalStorageService.OwnerKindGuest, guest.ToString("D"), Vid, Pid, DeviceKey));

        var userRows = storage.GetDeviceSettings(LocalStorageService.OwnerKindUser, UserId(customer), Vid, Pid, DeviceKey);
        Assert.Equal(500, userRows["polling_rate"]);
        Assert.Equal(2,   userRows["dpi.activeStage"]);
        Assert.Equal(7,   storage.GetAppPreference(LocalStorageService.OwnerKindUser, UserId(customer), "ScrollLines"));
    }

    [Fact]
    public async Task MigrateGuestToUserAsync_GuestWinsOnCollision()
    {
        using var storage = NewInMemory();
        var guest = Guid.NewGuid();
        const int customer = 42;

        // User had this row from a prior session with a different value.
        storage.SetDeviceSetting(LocalStorageService.OwnerKindUser, UserId(customer), Vid, Pid, DeviceKey, "polling_rate", 125);

        // Guest edited the same (vid, pid, device_key, polling_rate) during this session.
        storage.SetDeviceSetting(LocalStorageService.OwnerKindGuest, guest.ToString("D"), Vid, Pid, DeviceKey, "polling_rate", 1000);

        await storage.MigrateGuestToUserAsync(guest, customer);

        var rows = storage.GetDeviceSettings(LocalStorageService.OwnerKindUser, UserId(customer), Vid, Pid, DeviceKey);
        Assert.Equal(1000, rows["polling_rate"]);
    }

    [Fact]
    public async Task MigrateGuestToUserAsync_LeavesOtherUsersUntouched()
    {
        using var storage = NewInMemory();
        var guest = Guid.NewGuid();
        const int customer = 100;
        const int otherCustomer = 200;

        storage.SetDeviceSetting(LocalStorageService.OwnerKindUser, UserId(otherCustomer), Vid, Pid, DeviceKey, "polling_rate", 250);
        storage.SetDeviceSetting(LocalStorageService.OwnerKindGuest, guest.ToString("D"), Vid, Pid, DeviceKey, "polling_rate", 1000);

        await storage.MigrateGuestToUserAsync(guest, customer);

        var other = storage.GetDeviceSettings(LocalStorageService.OwnerKindUser, UserId(otherCustomer), Vid, Pid, DeviceKey);
        Assert.Equal(250, other["polling_rate"]);
    }

    [Fact]
    public async Task MigrateGuestToUserAsync_WithNoGuestRows_IsANoOp()
    {
        using var storage = NewInMemory();
        var exception = await Record.ExceptionAsync(() =>
            storage.MigrateGuestToUserAsync(Guid.NewGuid(), 7));
        Assert.Null(exception);
    }

    [Fact]
    public void ClearData_DeletesGuestRows_LeavesUserRows()
    {
        using var storage = NewInMemory();
        var guest = GuestId();
        var userId = UserId(55);

        storage.SetDeviceSetting(LocalStorageService.OwnerKindGuest, guest, Vid, Pid, DeviceKey, "polling_rate", 500);
        storage.SetDeviceSetting(LocalStorageService.OwnerKindUser, userId, Vid, Pid, DeviceKey, "polling_rate", 1000);
        storage.SetAppPreference(LocalStorageService.OwnerKindGuest, guest, "ScrollLines", 5);
        storage.SetAppPreference(LocalStorageService.OwnerKindUser, userId, "ScrollLines", 3);

        storage.ClearData();

        Assert.Empty(storage.GetDeviceSettings(LocalStorageService.OwnerKindGuest, guest, Vid, Pid, DeviceKey));
        Assert.Null(storage.GetAppPreference(LocalStorageService.OwnerKindGuest, guest, "ScrollLines"));

        Assert.Equal(1000, storage.GetDeviceSettings(LocalStorageService.OwnerKindUser, userId, Vid, Pid, DeviceKey)["polling_rate"]);
        Assert.Equal(3, storage.GetAppPreference(LocalStorageService.OwnerKindUser, userId, "ScrollLines"));
    }

    [Fact]
    public async Task MigrateGuestToUserAsync_MovesRgbRows()
    {
        using var storage = NewInMemory();
        var guest = Guid.NewGuid();
        const int customer = 33;

        storage.SaveRgbState(
            LocalStorageService.OwnerKindGuest, guest.ToString("D"), Vid, Pid, DeviceKey,
            isEnabled: true, selectedEffect: "Neon",
            effects: new[] { new RgbEffectRecord { EffectKey = "Neon", Hex = "#00FF00", Brightness = 60, Speed = 40 } });

        await storage.MigrateGuestToUserAsync(guest, customer);

        var migrated = storage.GetRgbState(LocalStorageService.OwnerKindUser, UserId(customer), Vid, Pid, DeviceKey);
        Assert.NotNull(migrated);
        Assert.Equal("Neon", migrated!.SelectedEffect);
        Assert.Single(migrated.Effects);
        Assert.Equal("#00FF00", migrated.Effects[0].Hex);

        Assert.Null(storage.GetRgbState(LocalStorageService.OwnerKindGuest, guest.ToString("D"), Vid, Pid, DeviceKey));
    }

    [Fact]
    public async Task MigrateGuestToUserAsync_MovesButtonBindings()
    {
        using var storage = NewInMemory();
        var guest = Guid.NewGuid();
        const int customer = 77;

        storage.SetButtonBinding(
            LocalStorageService.OwnerKindGuest, guest.ToString("D"), Vid, Pid, DeviceKey,
            button: 1, action: 2, macroId: null);

        await storage.MigrateGuestToUserAsync(guest, customer);

        var migrated = storage.GetButtonBindings(LocalStorageService.OwnerKindUser, UserId(customer), Vid, Pid, DeviceKey);
        Assert.Single(migrated);
        Assert.Equal((1, 2, (int?)null), migrated[0]);

        Assert.Empty(storage.GetButtonBindings(LocalStorageService.OwnerKindGuest, guest.ToString("D"), Vid, Pid, DeviceKey));
    }

    [Fact]
    public async Task MigrateGuestToUserAsync_MovesMacros()
    {
        using var storage = NewInMemory();
        var guest = Guid.NewGuid();
        const int customer = 88;

        var id = storage.SaveMacro(
            LocalStorageService.OwnerKindGuest, guest.ToString("D"), Vid, Pid, DeviceKey,
            new MacroRecord { Name = "Test", RepeatCount = 1, Mode = ProoveGaming.GrpcDependencies.Device.MacroRepeatMode.MacroPlayCount });

        await storage.MigrateGuestToUserAsync(guest, customer);

        // The device columns survive the re-key — only the owner moves.
        var migrated = storage.GetMacros(LocalStorageService.OwnerKindUser, UserId(customer), Vid, Pid, DeviceKey);
        Assert.Single(migrated);
        Assert.Equal(id, migrated[0].Id);

        Assert.Empty(storage.GetMacros(LocalStorageService.OwnerKindGuest, guest.ToString("D"), Vid, Pid, DeviceKey));
    }

    [Fact]
    public void GetMacros_IsScopedPerUnit_AndEmptyKeyIsSharedAcrossUnits()
    {
        using var storage = NewInMemory();
        var owner = LocalStorageService.OwnerKindUser;
        var oid = UserId(42);

        var unitId = storage.SaveMacro(owner, oid, Vid, Pid, "unit-a",
            new MacroRecord { Name = "A", RepeatCount = 1, Mode = ProoveGaming.GrpcDependencies.Device.MacroRepeatMode.MacroPlayCount });

        // Same owner, a different device (different product id or device key) must not see it.
        Assert.Empty(storage.GetMacros(owner, oid, Vid, Pid + 1, "unit-a"));
        Assert.Empty(storage.GetMacros(owner, oid, Vid, Pid, "other-key"));

        // The owning unit sees exactly its macro.
        var mine = storage.GetMacros(owner, oid, Vid, Pid, "unit-a");
        Assert.Single(mine);
        Assert.Equal(unitId, mine[0].Id);

        // The '' device_key is the unit-agnostic shared scope (the sign-in pull writes it):
        // every unit of the model sees those rows in addition to its own, but a different
        // model still sees nothing.
        var sharedId = storage.SaveMacro(owner, oid, Vid, Pid, DeviceKey,
            new MacroRecord { Name = "Shared", RepeatCount = 1, Mode = ProoveGaming.GrpcDependencies.Device.MacroRepeatMode.MacroPlayCount });
        Assert.Equal(new[] { unitId, sharedId },
            storage.GetMacros(owner, oid, Vid, Pid, "unit-a").Select(m => m.Id).ToArray());
        Assert.Equal(sharedId, Assert.Single(storage.GetMacros(owner, oid, Vid, Pid, "other-key")).Id);
        Assert.Empty(storage.GetMacros(owner, oid, Vid, Pid + 1, "unit-a"));
    }

    [Fact]
    public void SavePresetSnapshot_RoundTripsSettingsAndKeyBindings()
    {
        using var storage = NewInMemory();
        var owner = LocalStorageService.OwnerKindUser;
        var oid = UserId(7);

        storage.CreateDefaultPresetIfNotExists(owner, oid, Vid, Pid, "Default");
        var presetId = storage.GetPresets(owner, oid, Vid, Pid)[0].Id;

        // Live device state: a setting + a key override.
        storage.SetDeviceSetting(owner, oid, Vid, Pid, DeviceKey, "polling_rate", 500);
        storage.SetKeyBinding(owner, oid, Vid, Pid, DeviceKey, physScancode: 0x04, typeByte: 0x10, bitmask: 0, targetCode: 0x05);

        // Snapshot into the preset, then mutate the live state.
        storage.SavePresetSnapshot(presetId, owner, oid, Vid, Pid, DeviceKey);
        storage.SetDeviceSetting(owner, oid, Vid, Pid, DeviceKey, "polling_rate", 1000);
        storage.SetKeyBinding(owner, oid, Vid, Pid, DeviceKey, 0x04, 0x10, 0, 0x06);

        // Loading the preset back restores both.
        storage.LoadPresetSnapshotToDevice(presetId, owner, oid, Vid, Pid, DeviceKey);

        Assert.Equal(500, storage.GetDeviceSettings(owner, oid, Vid, Pid, DeviceKey)["polling_rate"]);
        var keys = storage.GetKeyBindings(owner, oid, Vid, Pid, DeviceKey);
        Assert.Single(keys);
        Assert.Equal((byte)0x04, keys[0].PhysScancode);
        Assert.Equal((byte)0x05, keys[0].TargetCode);
    }

    [Fact]
    public void KeyBindings_SetGetDelete_AndPerDeviceIsolation()
    {
        using var storage = NewInMemory();
        var owner = LocalStorageService.OwnerKindUser;
        var oid = UserId(9);

        storage.SetKeyBinding(owner, oid, Vid, Pid, DeviceKey, 0x04, 0x10, 0, 0x05);
        var mine = storage.GetKeyBindings(owner, oid, Vid, Pid, DeviceKey);
        Assert.Single(mine);
        Assert.Equal((byte)0x05, mine[0].TargetCode);

        // A different device does not see it.
        Assert.Empty(storage.GetKeyBindings(owner, oid, Vid, Pid + 1, DeviceKey));

        // Upsert overwrites the target.
        storage.SetKeyBinding(owner, oid, Vid, Pid, DeviceKey, 0x04, 0x10, 0, 0x06);
        Assert.Equal((byte)0x06, storage.GetKeyBindings(owner, oid, Vid, Pid, DeviceKey)[0].TargetCode);

        // Delete removes it.
        storage.DeleteKeyBinding(owner, oid, Vid, Pid, DeviceKey, 0x04);
        Assert.Empty(storage.GetKeyBindings(owner, oid, Vid, Pid, DeviceKey));
    }

    [Fact]
    public void AppliedPreset_SetGet_AndPerDeviceIsolation()
    {
        using var storage = NewInMemory();
        var owner = LocalStorageService.OwnerKindUser;
        var oid = UserId(11);

        Assert.Null(storage.GetAppliedPreset(owner, oid, Vid, Pid, DeviceKey));

        storage.SetAppliedPreset(owner, oid, Vid, Pid, DeviceKey, 42);
        Assert.Equal(42, storage.GetAppliedPreset(owner, oid, Vid, Pid, DeviceKey));

        // A different device is unaffected.
        Assert.Null(storage.GetAppliedPreset(owner, oid, Vid, Pid + 1, DeviceKey));

        // Upsert overwrites.
        storage.SetAppliedPreset(owner, oid, Vid, Pid, DeviceKey, 7);
        Assert.Equal(7, storage.GetAppliedPreset(owner, oid, Vid, Pid, DeviceKey));
    }

    // Reproduces the M3 review finding: preset_settings is a new table, so any preset that existed
    // before it shipped has an EMPTY snapshot — switching to it wipes device_settings and applies
    // catalog defaults to the hardware, reading as "the mouse reset itself." The fix seeds every
    // existing preset's snapshot from its device's CURRENT device_settings the first time the table
    // is created. A real pre-branch DB can't be constructed in-process (schema is additive-only), so
    // this drops preset_settings on an already-initialized file DB to make the next open see it as
    // "new," the same signal a genuine old DB would produce.
    [Fact]
    public void EnsureSchema_PresetSettingsMigration_SeedsExistingPresetsFromLiveDeviceSettings()
    {
        var dbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"provegaming-test-{Guid.NewGuid():N}.db");
        try
        {
            var owner = LocalStorageService.OwnerKindUser;
            var oid = UserId(55);
            int presetId;

            using (var storage = new LocalStorageService($"Data Source={dbPath}"))
            {
                storage.InsertPreset(owner, oid, Vid, Pid, new PresetRecord { Name = "Old" });
                presetId = storage.GetPresets(owner, oid, Vid, Pid)[0].Id;
                storage.SetDeviceSetting(owner, oid, Vid, Pid, DeviceKey, "polling_rate", 500);
            }

            using (var raw = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                raw.Open();
                using var cmd = raw.CreateCommand();
                cmd.CommandText = "DROP TABLE preset_settings;";
                cmd.ExecuteNonQuery();
            }

            using (var storage = new LocalStorageService($"Data Source={dbPath}"))
            {
                // Live state survives the migration untouched.
                Assert.Equal(500, storage.GetDeviceSettings(owner, oid, Vid, Pid, DeviceKey)["polling_rate"]);

                // The pre-existing preset now carries a snapshot seeded from that live state —
                // mutate live, then switch back to the preset and confirm it restores 500, not an
                // empty/default snapshot.
                storage.SetDeviceSetting(owner, oid, Vid, Pid, DeviceKey, "polling_rate", 1000);
                storage.LoadPresetSnapshotToDevice(presetId, owner, oid, Vid, Pid, DeviceKey);
                Assert.Equal(500, storage.GetDeviceSettings(owner, oid, Vid, Pid, DeviceKey)["polling_rate"]);
            }
        }
        finally
        {
            // Microsoft.Data.Sqlite pools connections by default, keeping the file handle open past
            // Dispose(); clear the pool first or the delete below fails with "file in use".
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (System.IO.File.Exists(dbPath)) System.IO.File.Delete(dbPath);
        }
    }
}
