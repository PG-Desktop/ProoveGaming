using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// Storage for key actuation (press / release depth and Rapid Trigger on the magnetic boards).
// Actuation used to live only in the keyboard's flash; device_actuation holds the live rows the UI
// edits and preset_actuation is the snapshot a preset carries, following the same live-row/snapshot
// shape as every other preset-scoped section.
//
// The rule the whole design hangs on: a preset with NO actuation rows means "no opinion", not
// "zero". Every preset saved before actuation was preset-scoped is in that state, so an empty
// snapshot must stay empty through save, load and backfill — otherwise upgrading resets a board the
// user has tuned.
public class LocalStorageServiceActuationTests
{
    private static LocalStorageService NewInMemory() => new("Data Source=:memory:");

    private const int Vid = 10473;
    private const int Pid = 12914;        // Cyrex
    // A different model on the same vendor, used only to prove rows do not leak across devices.
    private const int OtherModelPid = 12910;
    private const string DeviceKey = "";

    private static string GuestId() => Guid.NewGuid().ToString("D");

    private static PresetRecord NewPreset(string name = "P1") => new() { Name = name };

    // A small per-key table: two slots that do NOT share values, so a round trip that flattens the
    // board is visible rather than silently plausible.
    private static List<ActuationRecord> PerKeyRows() =>
    [
        new ActuationRecord(3, 150, 160, true, 20, 25),
        new ActuationRecord(7, 240, 250, false, 10, 15),
    ];

    private static List<ActuationRecord> Live(LocalStorageService s, string owner, int pid = Pid) =>
        s.GetDeviceActuation(LocalStorageService.OwnerKindGuest, owner, Vid, pid, DeviceKey);

    private static void SetLive(LocalStorageService s, string owner, IReadOnlyList<ActuationRecord> rows, int pid = Pid) =>
        s.ReplaceDeviceActuation(LocalStorageService.OwnerKindGuest, owner, Vid, pid, DeviceKey, rows);

    // ── Live rows ────────────────────────────────────────────────────────────

    [Fact]
    public void LiveActuation_RoundTrips()
    {
        using var storage = NewInMemory();
        var owner = GuestId();

        SetLive(storage, owner, PerKeyRows());

        var rows = Live(storage, owner);
        Assert.Equal(2, rows.Count);
        var slot3 = rows.Single(r => r.Slot == 3);
        Assert.Equal(150, slot3.PressCentiMm);
        Assert.Equal(160, slot3.ReleaseCentiMm);
        Assert.True(slot3.RapidTrigger);
        Assert.Equal(20, slot3.QuickPressCentiMm);
        Assert.Equal(25, slot3.QuickReleaseCentiMm);
        // The other slot keeps its own values — no flattening.
        Assert.False(rows.Single(r => r.Slot == 7).RapidTrigger);
    }

    // Boards whose firmware reports no per-key map are written uniformly and stored as one row
    // under the all-keys sentinel; it must survive as a negative slot, not be confused with slot 0.
    [Fact]
    public void LiveActuation_StoresTheAllKeysSentinelRow()
    {
        using var storage = NewInMemory();
        var owner = GuestId();

        SetLive(storage, owner, [new ActuationRecord(ActuationRecord.AllKeysSlot, 200, 200, false, 10, 10)]);

        var row = Assert.Single(Live(storage, owner));
        Assert.Equal(ActuationRecord.AllKeysSlot, row.Slot);
        Assert.True(row.Slot < 0);
    }

    [Fact]
    public void LiveActuation_ReplaceOverwritesTheWholeSet()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        SetLive(storage, owner, PerKeyRows());

        // A narrower table must not leave the dropped slot behind, or the next preset save would
        // snapshot a slot the board no longer has.
        SetLive(storage, owner, [new ActuationRecord(3, 100, 100, false, 5, 5)]);

        var row = Assert.Single(Live(storage, owner));
        Assert.Equal(3, row.Slot);
        Assert.Equal(100, row.PressCentiMm);
    }

    [Fact]
    public void LiveActuation_IsScopedByOwnerAndDevice()
    {
        using var storage = NewInMemory();
        var mine = GuestId();
        var theirs = GuestId();

        SetLive(storage, mine, PerKeyRows());

        Assert.Empty(Live(storage, theirs));                 // another owner
        Assert.Empty(Live(storage, mine, pid: OtherModelPid));    // another model
        Assert.Equal(2, Live(storage, mine).Count);
    }

    // ── Preset snapshot ──────────────────────────────────────────────────────

    [Fact]
    public void SavePresetSnapshot_CapturesActuation_AndLoadRestoresIt()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var presetId = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());
        SetLive(storage, owner, PerKeyRows());

        storage.SavePresetSnapshot(presetId, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        Assert.Equal(2, storage.GetPresetActuation(presetId).Count);

        // Live state drifts, then the preset is re-applied.
        SetLive(storage, owner, [new ActuationRecord(3, 99, 99, false, 1, 1)]);
        storage.LoadPresetSnapshotToDevice(presetId, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        var rows = Live(storage, owner);
        Assert.Equal(2, rows.Count);
        Assert.Equal(150, rows.Single(r => r.Slot == 3).PressCentiMm);
        Assert.Equal(240, rows.Single(r => r.Slot == 7).PressCentiMm);
    }

    [Fact]
    public void SavePresetSnapshot_ReplacesTheWholePriorActuationSnapshot()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var presetId = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());

        SetLive(storage, owner, PerKeyRows());
        storage.SavePresetSnapshot(presetId, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        SetLive(storage, owner, [new ActuationRecord(3, 111, 112, false, 2, 3)]);
        storage.SavePresetSnapshot(presetId, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        var row = Assert.Single(storage.GetPresetActuation(presetId));
        Assert.Equal(111, row.PressCentiMm);
    }

    // The migration guard, at the storage level: a preset that never captured actuation restores an
    // EMPTY set. The view model reads that as "write nothing", which is what stops an upgrade from
    // resetting a tuned board.
    [Fact]
    public void LoadPresetSnapshot_WithNoActuationCaptured_LeavesNoActuationRows()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var presetId = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());
        // Saved while the device had no actuation rows at all — an old preset.
        storage.SavePresetSnapshot(presetId, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        Assert.Empty(storage.GetPresetActuation(presetId));

        SetLive(storage, owner, PerKeyRows());
        storage.LoadPresetSnapshotToDevice(presetId, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        Assert.Empty(Live(storage, owner));
    }

    [Fact]
    public void DeletingAPreset_CascadesItsActuationRows()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var keep = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset("keep"));
        var drop = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset("drop"));
        SetLive(storage, owner, PerKeyRows());
        storage.SavePresetSnapshot(keep, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        storage.SavePresetSnapshot(drop, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        storage.DeletePreset(drop);

        Assert.Empty(storage.GetPresetActuation(drop));
        Assert.Equal(2, storage.GetPresetActuation(keep).Count);
    }

    [Fact]
    public void ResetPresetSnapshot_ClearsActuation()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var presetId = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());
        SetLive(storage, owner, PerKeyRows());
        storage.SavePresetSnapshot(presetId, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        storage.ResetPresetSnapshot(presetId);

        Assert.Empty(storage.GetPresetActuation(presetId));
    }

    [Fact]
    public void CopyPresetSnapshot_CopiesActuation_AndTheTwoAreIndependent()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var source = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset("src"));
        var copy = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset("copy"));
        SetLive(storage, owner, PerKeyRows());
        storage.SavePresetSnapshot(source, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        storage.CopyPresetSnapshot(source, copy);
        Assert.Equal(2, storage.GetPresetActuation(copy).Count);

        storage.ResetPresetSnapshot(source);
        Assert.Equal(2, storage.GetPresetActuation(copy).Count);
    }

    // ── Dirty comparison ─────────────────────────────────────────────────────

    [Fact]
    public void PresetMatchesLiveState_ReturnsFalse_WhenActuationDiffers()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var presetId = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());
        SetLive(storage, owner, PerKeyRows());
        storage.SavePresetSnapshot(presetId, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);
        Assert.True(storage.PresetMatchesLiveState(presetId, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));

        SetLive(storage, owner, [new ActuationRecord(3, 151, 160, true, 20, 25), new ActuationRecord(7, 240, 250, false, 10, 15)]);

        Assert.False(storage.PresetMatchesLiveState(presetId, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
    }

    // The no-seeding decision in code form: with no actuation rows on either side the preset is
    // clean, which is why opening a device cannot light up Save Preset on a page nobody touched.
    [Fact]
    public void PresetMatchesLiveState_ReturnsTrue_WhenNeitherSideHasActuation()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var presetId = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());
        storage.SavePresetSnapshot(presetId, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        Assert.True(storage.PresetMatchesLiveState(presetId, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
    }

    // ── Backfill (the Gap C fix) ─────────────────────────────────────────────

    // BackfillPresetSnapshotFromLive covered five of the six snapshot tables: key macro bindings
    // were missing from its list, so a snapshot repaired through it came back without them while
    // PresetMatchesLiveState still compared them. Actuation joins the same list.
    [Fact]
    public void BackfillPresetSnapshotFromLive_AlsoBackfillsKeyMacroBindingsAndActuation()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var presetId = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, NewPreset());

        storage.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey, 12, 34);
        SetLive(storage, owner, PerKeyRows());

        storage.BackfillPresetSnapshotFromLive(presetId, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey);

        Assert.Equal(2, storage.GetPresetActuation(presetId).Count);
        // And the snapshot now agrees with live state, which is the whole point of the backfill.
        Assert.True(storage.PresetMatchesLiveState(presetId, LocalStorageService.OwnerKindGuest, owner, Vid, Pid, DeviceKey));
    }

    // ── Owner migration ──────────────────────────────────────────────────────

    [Fact]
    public async Task GuestToUserMigration_CarriesActuation()
    {
        using var storage = NewInMemory();
        var guest = Guid.NewGuid();
        const int customer = 99;
        SetLive(storage, guest.ToString("D"), PerKeyRows());

        await storage.MigrateGuestToUserAsync(guest, customer);

        Assert.Empty(Live(storage, guest.ToString("D")));
        var migrated = storage.GetDeviceActuation(
            LocalStorageService.OwnerKindUser, customer.ToString(CultureInfo.InvariantCulture), Vid, Pid, DeviceKey);
        Assert.Equal(2, migrated.Count);
        Assert.Equal(150, migrated.Single(r => r.Slot == 3).PressCentiMm);
    }

    [Fact]
    public void ClearData_RemovesGuestActuation_LiveAndSnapshot()
    {
        using var storage = NewInMemory();
        var guest = GuestId();
        var presetId = storage.InsertPreset(LocalStorageService.OwnerKindGuest, guest, Vid, Pid, NewPreset());
        SetLive(storage, guest, PerKeyRows());
        storage.SavePresetSnapshot(presetId, LocalStorageService.OwnerKindGuest, guest, Vid, Pid, DeviceKey);
        Assert.NotEmpty(storage.GetPresetActuation(presetId));

        storage.ClearData();

        Assert.Empty(Live(storage, guest));
        // The snapshot rows go with the preset, not just the live table.
        Assert.Empty(storage.GetPresetActuation(presetId));
    }
}
