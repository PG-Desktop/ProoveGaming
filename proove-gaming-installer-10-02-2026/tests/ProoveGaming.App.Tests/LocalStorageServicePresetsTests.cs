using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// Covers the presets CRUD and snapshot round-trip added for the "save current settings
// to the selected preset / apply preset on switch / reset preset" flow. Follows the same
// in-memory pattern as LocalStorageServiceSettingsTests.
//
// Presets are scoped per (owner, device type — vendor/product), NOT per physical device —
// two identical units of the same model share one preset list. The snapshot content a
// preset holds (RGB / button bindings / settings / key bindings) reuses the *live* per-device
// tables (device_rgb, button_bindings, ...), which stay scoped by device_key exactly as
// before — SavePresetSnapshot / LoadPresetSnapshotToDevice still take a deviceKey for that
// reason, even though InsertPreset / GetPresets / CreateDefaultPresetIfNotExists do not.
public class LocalStorageServicePresetsTests : IDisposable
{
    private static LocalStorageService NewInMemory() =>
        new("Data Source=:memory:");

    // Only the uid-backfill tests use the file store; see the comment on that section for
    // why they cannot run in-memory. Harmless for the in-memory tests: the file is never
    // created unless a test asks for it.
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"pg-presetuid-{Guid.NewGuid():N}.db");

    private LocalStorageService NewFileStore() => new($"Data Source={_dbPath}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); }
        catch (IOException) { /* handle may still be releasing */ }
    }

    private const int Vid = 0x04D9;
    private const int Pid = 0xA23F;
    private const int OtherPid = 0xA240;
    private const string DeviceKey = "";

    private static string GuestId() => Guid.NewGuid().ToString("D");

    private static PresetRecord NewPreset(string name = "P1", string desc = "") =>
        new() { Name = name, Description = desc };

    private static RgbEffectRecord NewEffect(string key, string hex, double b = 60, double s = 40, bool rainbow = false) =>
        new() { EffectKey = key, Hex = hex, Brightness = b, Speed = s, IsRainbow = rainbow };

    // ── Preset CRUD ──────────────────────────────────────────────────────────

    [Fact]
    public void InsertPreset_ReturnsId_AndGetPresetsReturnsRow()
    {
        using var storage = NewInMemory();
        var owner = GuestId();

        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
            NewPreset("First", "hello"));

        Assert.True(id > 0);
        var rows = storage.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid);
        Assert.Single(rows);
        Assert.Equal(id, rows[0].Id);
        Assert.Equal("First", rows[0].Name);
        Assert.Equal("hello", rows[0].Description);
    }

    [Fact]
    public void InsertPreset_MultipleForSameDeviceType_AreReturnedByInsertOrder()
    {
        using var storage = NewInMemory();
        var owner = GuestId();

        storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset("A"));
        storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset("B"));
        storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset("C"));

        var rows = storage.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid);
        Assert.Equal(new[] { "A", "B", "C" }, rows.Select(r => r.Name).ToArray());
    }

    // Regression guard for the old per-physical-unit isolation this table used to have
    // (formerly InsertPreset_DifferentDeviceKeys_DoNotCollide): presets are scoped by device
    // TYPE only now, so two physical units of the same model (same vendor/product) reading the
    // list see the identical set of rows — there is no device_key parameter left to diverge on.
    [Fact]
    public void GetPresets_TwoPhysicalUnitsOfSameModel_SeeTheIdenticalSharedList()
    {
        using var storage = NewInMemory();
        var owner = GuestId();

        storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset("Shared A"));
        storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset("Shared B"));

        // "Asking as unit A" and "asking as unit B" are the same call — that's the point.
        var asUnitA = storage.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid);
        var asUnitB = storage.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid);

        Assert.Equal(2, asUnitA.Count);
        Assert.Equal(asUnitA.Select(p => p.Id), asUnitB.Select(p => p.Id));
    }

    [Fact]
    public void InsertPreset_DifferentOwners_DoNotCollide()
    {
        using var storage = NewInMemory();
        var guest = GuestId();
        var user = "42";

        storage.InsertPreset(LocalStorageService.OwnerKindGuest, guest, Vid, Pid, NewPreset("guest"));
        storage.InsertPreset(LocalStorageService.OwnerKindUser,  user,  Vid, Pid, NewPreset("user"));

        Assert.Equal("guest", storage.GetPresets(LocalStorageService.OwnerKindGuest, guest, Vid, Pid).Single().Name);
        Assert.Equal("user",  storage.GetPresets(LocalStorageService.OwnerKindUser,  user,  Vid, Pid).Single().Name);
    }

    [Fact]
    public void UpdatePreset_ChangesNameAndDescription()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
            NewPreset("Original", "old"));

        storage.UpdatePreset(id, "Renamed", "new desc");

        var row = storage.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid).Single();
        Assert.Equal("Renamed", row.Name);
        Assert.Equal("new desc", row.Description);
    }

    [Fact]
    public void DeletePreset_RemovesRow()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset("gone"));

        storage.DeletePreset(id);

        Assert.Empty(storage.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid));
    }

    // ── IsDefault persistence + default-guard + PromotePresetToDefault invariant ─

    [Fact]
    public void InsertPreset_PersistsIsDefault_AndGetPresetsReadsItBack()
    {
        using var storage = NewInMemory();
        var owner = GuestId();

        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
            new PresetRecord { Name = "D", IsDefault = true });

        var row = storage.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid).Single();
        Assert.Equal(id, row.Id);
        Assert.True(row.IsDefault);
    }

    [Fact]
    public void DeletePreset_OnDefaultRow_IsNoOp()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
            new PresetRecord { Name = "Default", IsDefault = true });

        storage.DeletePreset(id);

        // Row must survive — the storage-side guard mirrors the VM guard.
        Assert.Single(storage.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid));
    }

    [Fact]
    public void PromotePresetToDefault_PromotesTarget_AndDemotesSiblings()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var a = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
            new PresetRecord { Name = "A", IsDefault = true });
        var b = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
            new PresetRecord { Name = "B", IsDefault = true }); // deliberately overlapping
        var c = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
            new PresetRecord { Name = "C" });

        storage.PromotePresetToDefault(c);

        var rows = storage.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid);
        var byId = rows.ToDictionary(r => r.Id);
        Assert.False(byId[a].IsDefault);
        Assert.False(byId[b].IsDefault);
        Assert.True(byId[c].IsDefault);
        // Exactly one default per (owner, device type).
        Assert.Equal(1, rows.Count(r => r.IsDefault));
    }

    [Fact]
    public void PromotePresetToDefault_LeavesOtherDeviceTypesUntouched()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        // A different device TYPE (different product id) — not just a different physical unit
        // of the same model, since presets no longer isolate by physical unit at all.
        var otherDefault = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, OtherPid,
            new PresetRecord { Name = "OD", IsDefault = true });
        var target = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
            new PresetRecord { Name = "T" });

        storage.PromotePresetToDefault(target);

        var otherRow = storage.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, OtherPid).Single(r => r.Id == otherDefault);
        Assert.True(otherRow.IsDefault); // demote only touched the target's own (owner, device type) scope
    }

    // ── SavePresetSnapshot round-trip ────────────────────────────────────────

    [Fact]
    public void SavePresetSnapshot_CapturesRgbState_AndReloadRestoresIt()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());

        // Seed live device state.
        storage.SaveRgbState(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey,
            isEnabled: true, selectedEffect: "Neon",
            effects: new[] { NewEffect("Neon", "#00FF00", b: 55, s: 33, rainbow: false) });

        storage.SavePresetSnapshot(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        // Overwrite the live device state with something different.
        storage.SaveRgbState(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey,
            isEnabled: false, selectedEffect: "Static",
            effects: new[] { NewEffect("Static", "#FF0000", b: 10, s: 10) });

        // Loading the snapshot back must revert the live state.
        storage.LoadPresetSnapshotToDevice(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        var restored = storage.GetRgbState(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        Assert.NotNull(restored);
        Assert.True(restored!.IsEnabled);
        Assert.Equal("Neon", restored.SelectedEffect);
        Assert.Single(restored.Effects);
        Assert.Equal("Neon", restored.Effects[0].EffectKey);
        Assert.Equal("#00FF00", restored.Effects[0].Hex);
        Assert.Equal(55, restored.Effects[0].Brightness);
        Assert.Equal(33, restored.Effects[0].Speed);
    }

    [Fact]
    public void SavePresetSnapshot_CapturesButtonBindings_AndReloadRestoresThem()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());

        storage.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, button: 1, action: 7, macroId: null);
        storage.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, button: 4, action: 9, macroId: 42);

        storage.SavePresetSnapshot(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        // Live edits after the snapshot must not leak into the loaded state.
        storage.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, button: 1, action: 99, macroId: null);
        storage.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, button: 5, action: 3, macroId: null);

        storage.LoadPresetSnapshotToDevice(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        var restored = storage.GetButtonBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey)
            .OrderBy(b => b.Button)
            .ToList();
        Assert.Equal(2, restored.Count);
        Assert.Equal((1, 7, (int?)null), restored[0]);
        Assert.Equal((4, 9, (int?)42),   restored[1]);
    }

    [Fact]
    public void SavePresetSnapshot_ReplacesPriorSnapshotForSamePreset()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());

        // First snapshot: enabled + Neon selected, plus a button-1 binding.
        storage.SaveRgbState(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey,
            isEnabled: true, selectedEffect: "Neon",
            effects: new[] { NewEffect("Neon", "#00FF00") });
        storage.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 1, 5, null);
        storage.SavePresetSnapshot(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        // Change live state to something distinguishable and re-snapshot.
        storage.SaveRgbState(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey,
            isEnabled: false, selectedEffect: "Static",
            effects: new[] { NewEffect("Static", "#FF00FF") });
        // Replace the old button-1 binding and add a new button-2 one.
        storage.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 1, 99, null);
        storage.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 2, 6, 12);
        storage.SavePresetSnapshot(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        // Load the snapshot back — must reflect the SECOND state (head record + button rows).
        storage.LoadPresetSnapshotToDevice(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        var rgb = storage.GetRgbState(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        Assert.NotNull(rgb);
        Assert.False(rgb!.IsEnabled);
        Assert.Equal("Static", rgb.SelectedEffect);
        // The second snapshot must include the Static effect. Neon may or may not still be
        // there depending on whether the caller cleared the effect table before writing —
        // SaveRgbState upserts per effect_key, it doesn't wipe non-mentioned effects.
        Assert.Contains(rgb.Effects, e => e.EffectKey == "Static" && e.Hex == "#FF00FF");

        var bindings = storage.GetButtonBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey)
            .OrderBy(b => b.Button)
            .ToList();
        Assert.Equal(2, bindings.Count);
        // Button-1 was overwritten by the second snapshot; button-2 is new.
        Assert.Equal((1, 99, (int?)null), bindings[0]);
        Assert.Equal((2, 6,  (int?)12),   bindings[1]);
    }

    // ── LoadPresetSnapshotToDevice edge cases ────────────────────────────────

    [Fact]
    public void LoadPresetSnapshotToDevice_WipesExistingDeviceRowsBeforeCopying()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());

        // Snapshot with a single binding on button 4.
        storage.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 4, 9, null);
        storage.SavePresetSnapshot(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        // Live state has a different binding on a different button.
        storage.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 1, 3, null);
        // Deliberately keep the button-4 row too — Load must still wipe it before insert.

        storage.LoadPresetSnapshotToDevice(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        var live = storage.GetButtonBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        Assert.Single(live);
        Assert.Equal((4, 9, (int?)null), live[0]);
    }

    [Fact]
    public void LoadPresetSnapshotToDevice_WithNoSnapshot_ClearsDeviceState()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());

        storage.SaveRgbState(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey,
            isEnabled: true, selectedEffect: "Neon",
            effects: new[] { NewEffect("Neon", "#00FF00") });
        storage.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 1, 7, null);

        // Preset holds no snapshot; loading it must wipe the live state so the sections
        // fall back to defaults (this is the "empty preset" / post-Reset code path).
        storage.LoadPresetSnapshotToDevice(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        Assert.Null(storage.GetRgbState(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
        Assert.Empty(storage.GetButtonBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
    }

    // Snapshot content is loaded onto whichever physical unit (device_key) the caller names —
    // that part of the flow is unaffected by the preset-list rescope, and stays covered here.
    [Fact]
    public void LoadPresetSnapshotToDevice_LeavesOtherPhysicalUnitsUntouched()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());

        // Live state on both units.
        storage.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, "unit-a", 1, 7, null);
        storage.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, "unit-b", 1, 8, null);

        // Take a snapshot on unit-a, load it back on unit-a.
        storage.SavePresetSnapshot(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, "unit-a");
        storage.LoadPresetSnapshotToDevice(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, "unit-a");

        var b = storage.GetButtonBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, "unit-b");
        Assert.Single(b);
        Assert.Equal((1, 8, (int?)null), b[0]);
    }

    // ── ResetPresetSnapshot ──────────────────────────────────────────────────

    [Fact]
    public void ResetPresetSnapshot_WipesSnapshot_KeepsPresetRow()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
            NewPreset("keep-me", "still here"));

        storage.SaveRgbState(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey,
            isEnabled: true, selectedEffect: "Neon",
            effects: new[] { NewEffect("Neon", "#00FF00") });
        storage.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 1, 7, null);
        storage.SavePresetSnapshot(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        storage.ResetPresetSnapshot(id);

        // The presets row must stay put — reset only clears the snapshot.
        var row = storage.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid).SingleOrDefault();
        Assert.NotNull(row);
        Assert.Equal("keep-me", row!.Name);
        Assert.Equal("still here", row.Description);

        // Loading the (now empty) snapshot must clear the live state.
        storage.LoadPresetSnapshotToDevice(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        Assert.Null(storage.GetRgbState(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
        Assert.Empty(storage.GetButtonBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
    }

    [Fact]
    public void ResetPresetSnapshot_LeavesOtherPresetsSnapshotsAlone()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var keep = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset("keep"));
        var wipe = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset("wipe"));

        storage.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 1, 7, null);
        storage.SavePresetSnapshot(keep, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        storage.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 1, 8, null);
        storage.SavePresetSnapshot(wipe, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        storage.ResetPresetSnapshot(wipe);

        // "keep" snapshot must still round-trip cleanly.
        storage.LoadPresetSnapshotToDevice(keep, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        var live = storage.GetButtonBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        Assert.Single(live);
        Assert.Equal((1, 7, (int?)null), live[0]);
    }

    // ── DeletePreset cascade ─────────────────────────────────────────────────

    [Fact]
    public void DeletePreset_CascadesPresetSnapshotRows()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset("gone"));

        storage.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 1, 7, null);
        storage.SavePresetSnapshot(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        storage.DeletePreset(id);

        // Snapshot rows must be gone too — loading them back into a fresh device state
        // should leave the device tables empty. Reuse a new device key so the current
        // rows don't confuse the assertion.
        storage.LoadPresetSnapshotToDevice(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, "unit-fresh");
        Assert.Empty(storage.GetButtonBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, "unit-fresh"));
        Assert.Null(storage.GetRgbState(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, "unit-fresh"));
    }

    // ── ClearData cascade for guest presets ──────────────────────────────────

    [Fact]
    public void ClearData_CascadesGuestPresetsAndSnapshots_LeavesUserRowsIntact()
    {
        using var storage = NewInMemory();
        var guest = GuestId();
        var user = "77";

        var guestPreset = storage.InsertPreset(LocalStorageService.OwnerKindGuest, guest, Vid, Pid, NewPreset("guest"));
        var userPreset  = storage.InsertPreset(LocalStorageService.OwnerKindUser,  user,  Vid, Pid, NewPreset("user"));

        // Snapshot both.
        storage.SetButtonBinding(LocalStorageService.OwnerKindGuest, guest, Vid, Pid, DeviceKey, 1, 7, null);
        storage.SavePresetSnapshot(guestPreset, LocalStorageService.OwnerKindGuest, guest, Vid, Pid, DeviceKey);

        storage.SetButtonBinding(LocalStorageService.OwnerKindUser, user, Vid, Pid, DeviceKey, 2, 9, null);
        storage.SavePresetSnapshot(userPreset, LocalStorageService.OwnerKindUser, user, Vid, Pid, DeviceKey);

        storage.ClearData();

        // Guest preset + snapshot gone.
        Assert.Empty(storage.GetPresets(LocalStorageService.OwnerKindGuest, guest, Vid, Pid));

        // User preset + snapshot still round-trips.
        Assert.Single(storage.GetPresets(LocalStorageService.OwnerKindUser, user, Vid, Pid));
        storage.LoadPresetSnapshotToDevice(userPreset, LocalStorageService.OwnerKindUser, user, Vid, Pid, DeviceKey);
        var live = storage.GetButtonBindings(LocalStorageService.OwnerKindUser, user, Vid, Pid, DeviceKey);
        Assert.Single(live);
        Assert.Equal((2, 9, (int?)null), live[0]);
    }

    // ── PresetMatchesLiveState ────────────────────────────────────────────────
    // Drives the live-apply feature's dirty comparison: live per-device state vs. the selected
    // preset's own saved snapshot, across all five table pairs (RGB head, RGB effects, button
    // bindings, device settings, key bindings).

    [Fact]
    public void PresetMatchesLiveState_ReturnsTrue_WhenLiveStateEqualsSnapshot()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());

        storage.SaveRgbState(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey,
            isEnabled: true, selectedEffect: "Neon",
            effects: new[] { NewEffect("Neon", "#00FF00") });
        storage.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 1, 7, null);
        storage.SetDeviceSetting(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, "dpi_stage", 3);
        storage.SetKeyBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 0x04, 0x10, 0, 0x05);

        storage.SavePresetSnapshot(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        Assert.True(storage.PresetMatchesLiveState(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
    }

    // ── Key→macro bindings in the snapshot ───────────────────────────────────
    // key_macro_bindings was the one piece of keyboard config a preset did not own, so it
    // survived every preset switch and every Discard — and the revert then re-pushed the very
    // binding the user had just discarded.

    private static List<(int MatrixIndex, int MacroId)> Bindings(LocalStorageService s, string owner) =>
        s.GetKeyMacroBindings(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

    [Fact]
    public void SnapshotRoundTrip_RestoresKeyMacroBindings()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());

        storage.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 33, 101);
        storage.SavePresetSnapshot(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        // Repoint the live binding at a different macro, then revert.
        storage.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 33, 102);
        storage.LoadPresetSnapshotToDevice(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        var restored = Assert.Single(Bindings(storage, owner));
        Assert.Equal((33, 101), restored);
    }

    // The case that makes Discard actually discard: a preset saved with NO bindings must clear a
    // binding added afterwards. An implementation that skips the wipe when the snapshot is empty
    // passes every other test here and fails this one.
    [Fact]
    public void RestoringAPresetWithNoBindings_ClearsLiveBindings()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());

        storage.SavePresetSnapshot(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        storage.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 33, 101);

        storage.LoadPresetSnapshotToDevice(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        Assert.Empty(Bindings(storage, owner));
    }

    [Fact]
    public void PresetMatchesLiveState_ReturnsFalse_WhenKeyMacroBindingDiffers()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());

        storage.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 33, 101);
        storage.SavePresetSnapshot(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        Assert.True(storage.PresetMatchesLiveState(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));

        // Repointed at another macro.
        storage.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 33, 102);
        Assert.False(storage.PresetMatchesLiveState(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));

        // Reverted by hand — the dirty flag must clear again, not latch.
        storage.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 33, 101);
        Assert.True(storage.PresetMatchesLiveState(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));

        // Removed entirely — the snapshot still has it, so this is a difference too.
        storage.DeleteKeyMacroBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 33);
        Assert.False(storage.PresetMatchesLiveState(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
    }

    [Fact]
    public void CopyPresetSnapshot_CopiesKeyMacroBindings_AndTheTwoAreIndependent()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var src = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset("Src"));
        var dst = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset("Dst"));

        storage.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 33, 101);
        storage.SavePresetSnapshot(src, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        storage.CopyPresetSnapshot(src, dst);

        // Re-save the source with a different binding; the copy must not follow.
        storage.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 33, 102);
        storage.SavePresetSnapshot(src, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        storage.LoadPresetSnapshotToDevice(dst, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        Assert.Equal((33, 101), Assert.Single(Bindings(storage, owner)));

        storage.LoadPresetSnapshotToDevice(src, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        Assert.Equal((33, 102), Assert.Single(Bindings(storage, owner)));
    }

    [Fact]
    public void ResetPresetSnapshot_ClearsKeyMacroBindings()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());

        storage.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 33, 101);
        storage.SavePresetSnapshot(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        storage.ResetPresetSnapshot(id);

        storage.LoadPresetSnapshotToDevice(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        Assert.Empty(Bindings(storage, owner));
    }

    // The live tables are scoped by device; the snapshot tables are not. Saving and restoring one
    // device's preset must not reach into another device's bindings.
    [Fact]
    public void SnapshotDoesNotTouchAnotherDevicesBindings()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());

        storage.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 33, 101);
        storage.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, owner, Vid, OtherPid, DeviceKey, 44, 202);

        storage.SavePresetSnapshot(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        storage.LoadPresetSnapshotToDevice(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        var other = storage.GetKeyMacroBindings(LocalStorageService.OwnerKindGuest, owner, Vid, OtherPid, DeviceKey);
        Assert.Equal((44, 202), Assert.Single(other));
    }

    [Fact]
    public void PresetMatchesLiveState_ReturnsFalse_WhenRgbHeadDiffers()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());

        storage.SaveRgbState(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey,
            isEnabled: true, selectedEffect: "Neon", effects: new[] { NewEffect("Neon", "#00FF00") });
        storage.SavePresetSnapshot(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        Assert.True(storage.PresetMatchesLiveState(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));

        storage.SaveRgbState(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey,
            isEnabled: false, selectedEffect: "Neon", effects: new[] { NewEffect("Neon", "#00FF00") });
        Assert.False(storage.PresetMatchesLiveState(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
    }

    [Fact]
    public void PresetMatchesLiveState_ReturnsFalse_WhenRgbEffectDiffers()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());

        storage.SaveRgbState(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey,
            isEnabled: true, selectedEffect: "Neon", effects: new[] { NewEffect("Neon", "#00FF00") });
        storage.SavePresetSnapshot(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        Assert.True(storage.PresetMatchesLiveState(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));

        storage.SaveRgbState(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey,
            isEnabled: true, selectedEffect: "Neon", effects: new[] { NewEffect("Neon", "#FF0000") });
        Assert.False(storage.PresetMatchesLiveState(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
    }

    [Fact]
    public void PresetMatchesLiveState_ReturnsFalse_WhenButtonBindingDiffers()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());

        storage.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 1, 7, null);
        storage.SavePresetSnapshot(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        Assert.True(storage.PresetMatchesLiveState(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));

        storage.SetButtonBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 1, 9, null);
        Assert.False(storage.PresetMatchesLiveState(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
    }

    [Fact]
    public void PresetMatchesLiveState_ReturnsFalse_WhenDeviceSettingDiffers()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());

        storage.SetDeviceSetting(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, "dpi_stage", 3);
        storage.SavePresetSnapshot(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        Assert.True(storage.PresetMatchesLiveState(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));

        storage.SetDeviceSetting(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, "dpi_stage", 5);
        Assert.False(storage.PresetMatchesLiveState(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
    }

    [Fact]
    public void PresetMatchesLiveState_ReturnsFalse_WhenKeyBindingDiffers()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());

        storage.SetKeyBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 0x04, 0x10, 0, 0x05);
        storage.SavePresetSnapshot(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        Assert.True(storage.PresetMatchesLiveState(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));

        storage.SetKeyBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 0x04, 0x10, 0, 0x06);
        Assert.False(storage.PresetMatchesLiveState(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
    }

    // The exact 800 -> 1200 -> 800 scenario from the plan's acceptance criteria, at the storage
    // layer: changing a value away from the saved preset and then back must read as "matches"
    // again, not stay permanently dirty.
    [Fact]
    public void PresetMatchesLiveState_ReturnsTrue_AfterChangingThenRevertingAValue()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());

        storage.SetDeviceSetting(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, "dpi_stage", 800);
        storage.SavePresetSnapshot(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        Assert.True(storage.PresetMatchesLiveState(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));

        storage.SetDeviceSetting(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, "dpi_stage", 1200);
        Assert.False(storage.PresetMatchesLiveState(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));

        storage.SetDeviceSetting(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, "dpi_stage", 800);
        Assert.True(storage.PresetMatchesLiveState(id, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
    }

    // The demote in PromotePresetToDefault used to filter on the target row's device_key, so on a
    // database still holding legacy rows it demoted only the rows sharing that key. Normalisation
    // makes every key '' at startup, but the scope filter must match GetPresets regardless.
    [Fact]
    public void PromotePresetToDefault_DemotesEveryPresetOfTheSameDeviceType()
    {
        using var storage = NewInMemory();
        var owner = GuestId();

        var a = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
            new PresetRecord { Name = "A", IsDefault = true });
        var b = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
            new PresetRecord { Name = "B", IsDefault = true });
        var c = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
            new PresetRecord { Name = "C" });

        storage.PromotePresetToDefault(c);

        var rows = storage.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid);
        Assert.Equal(1, rows.Count(r => r.IsDefault));
        Assert.True(rows.Single(r => r.Id == c).IsDefault);
    }

    // ── uid + server_updated_at (docs/plans/add-api-v2-sync) ─────────────────
    // Sync identity: every row carries a client-generated GUID uid from insert (or from the
    // one-time constructor backfill for databases that predate the columns), immutable across
    // the guest → user rekey; server_updated_at is the server's opaque echo, written only by
    // sync code. The legacy-DB cases need a FILE database for the same reason
    // LocalStorageServiceMacroScopeTests uses one: the migration only does observable work
    // against an already-populated database, which ":memory:" can never be at construction.

    [Fact]
    public void InsertPreset_AssignsLowercase36CharUid_AndNoServerUpdatedAt()
    {
        using var storage = NewInMemory();
        var owner = GuestId();

        var record = NewPreset();
        storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, record);

        var row = storage.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid).Single();
        Assert.Equal(36, row.Uid.Length);
        Assert.True(Guid.TryParseExact(row.Uid, "D", out _));
        Assert.Equal(row.Uid, row.Uid.ToLowerInvariant());
        Assert.Null(row.ServerUpdatedAt);
        // The record passed to InsertPreset is stamped with the same uid the row got.
        Assert.Equal(row.Uid, record.Uid);
    }

    [Fact]
    public void InsertPreset_GeneratedUids_AreDistinctAcrossInserts()
    {
        using var storage = NewInMemory();
        var owner = GuestId();

        for (var i = 0; i < 5; i++)
            storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset($"P{i}"));

        var uids = storage.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid)
            .Select(r => r.Uid)
            .ToList();
        Assert.Equal(5, uids.Count);
        Assert.All(uids, u => Assert.NotEqual(string.Empty, u));
        Assert.Equal(5, uids.Distinct().Count());
    }

    // A non-empty Uid on the record wins over generation — the seam sync tests pin known
    // uids through.
    [Fact]
    public void InsertPreset_NonEmptyUidOnTheRecord_IsHonoured()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        const string pinned = "11111111-2222-3333-4444-555555555555";

        storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid,
            new PresetRecord { Name = "pinned", Uid = pinned });

        Assert.Equal(pinned, storage.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid).Single().Uid);
    }

    // server_updated_at is opaque by contract (the API's format has no timezone), so it must
    // round-trip verbatim — a deliberately non-ISO string proves nothing parses it.
    [Fact]
    public void SetPresetServerUpdatedAt_RoundTripsTheOpaqueString()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var id = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());

        storage.SetPresetServerUpdatedAt(id, "2026-08-25 17:03:09");

        var row = storage.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid).Single();
        Assert.Equal("2026-08-25 17:03:09", row.ServerUpdatedAt);
    }

    // The presets table as the previous app version created it: no uid, no server_updated_at.
    // Raw SQL because the current schema block would create the NEW shape — the point is to
    // hand the constructor a database it must migrate.
    private void CreateLegacyPresetsDb(string owner, params (string Name, string Desc)[] rows)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE presets (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    owner_kind  TEXT NOT NULL,
                    owner_id    TEXT NOT NULL,
                    vendor_id   INTEGER NOT NULL,
                    product_id  INTEGER NOT NULL,
                    device_key  TEXT NOT NULL DEFAULT '',
                    name        TEXT NOT NULL,
                    description TEXT NOT NULL DEFAULT '',
                    created_at  TEXT NOT NULL,
                    is_default  INTEGER NOT NULL DEFAULT 0
                );
                """;
            create.ExecuteNonQuery();
        }

        foreach (var (name, desc) in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO presets (owner_kind, owner_id, vendor_id, product_id, device_key, name, description, created_at)
                VALUES ($ok, $oi, $v, $p, '', $n, $d, $at);
                """;
            insert.Parameters.AddWithValue("$ok", LocalStorageService.OwnerKindGuest);
            insert.Parameters.AddWithValue("$oi", owner);
            insert.Parameters.AddWithValue("$v", Vid);
            insert.Parameters.AddWithValue("$p", Pid);
            insert.Parameters.AddWithValue("$n", name);
            insert.Parameters.AddWithValue("$d", desc);
            insert.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
        }
    }

    [Fact]
    public void LegacyDb_PresetsWithoutUidColumns_GetDistinctUids_AndDataIsUntouched()
    {
        var owner = GuestId();
        CreateLegacyPresetsDb(owner, ("A", "da"), ("B", "db"), ("C", ""));

        using var storage = NewFileStore();

        var rows = storage.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid);
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.True(Guid.TryParseExact(r.Uid, "D", out _)));
        Assert.Equal(3, rows.Select(r => r.Uid).Distinct().Count());
        Assert.All(rows, r => Assert.Null(r.ServerUpdatedAt));
        // Existing data untouched: names, descriptions and insert order all survive.
        Assert.Equal(new[] { "A", "B", "C" }, rows.Select(r => r.Name).ToArray());
        Assert.Equal(new[] { "da", "db", "" }, rows.Select(r => r.Description).ToArray());
    }

    // The backfill assigns each uid exactly once — a reopen must find nothing to do, or the
    // "stable identity" contract is broken and the server would see every preset as new.
    [Fact]
    public void LegacyDb_BackfilledUids_AreStableAcrossReopens()
    {
        var owner = GuestId();
        CreateLegacyPresetsDb(owner, ("A", ""), ("B", ""));

        List<string> first;
        using (var storage = NewFileStore())
            first = storage.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid)
                .Select(r => r.Uid).ToList();

        using (var reopened = NewFileStore())
        {
            var second = reopened.GetPresets(LocalStorageService.OwnerKindGuest, owner, Vid, Pid)
                .Select(r => r.Uid).ToList();
            Assert.Equal(first, second);
        }
    }

    [Fact]
    public async Task MigrateGuestToUserAsync_PreservesPresetUids()
    {
        using var storage = NewInMemory();
        var guestSession = Guid.NewGuid();
        var guestId = guestSession.ToString("D");

        storage.InsertPreset(LocalStorageService.OwnerKindGuest, guestId, Vid, Pid, NewPreset("A"));
        storage.InsertPreset(LocalStorageService.OwnerKindGuest, guestId, Vid, Pid, NewPreset("B"));
        var before = storage.GetPresets(LocalStorageService.OwnerKindGuest, guestId, Vid, Pid)
            .ToDictionary(r => r.Name, r => r.Uid);

        await storage.MigrateGuestToUserAsync(guestSession, 42);

        var after = storage.GetPresets(LocalStorageService.OwnerKindUser, "42", Vid, Pid);
        Assert.Equal(2, after.Count);
        Assert.All(after, r => Assert.Equal(before[r.Name], r.Uid));
        Assert.Empty(storage.GetPresets(LocalStorageService.OwnerKindGuest, guestId, Vid, Pid));
    }
}
