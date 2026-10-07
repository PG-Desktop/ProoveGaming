using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// Storage for the Extended Keys of the Smart Infinite magnetic boards: device_extended_keys holds
// the live rows the UI edits and preset_extended_keys the snapshot a preset carries, one wide row
// per record for all four modes. Two rules pinned here: a pair is stored with its lower slot first
// whatever order it arrived in (so the same two keys are one row and the dirty comparison cannot be
// fooled by pick order), and — unlike actuation — an EMPTY snapshot is a real opinion that empties
// the live rows on apply.
public class LocalStorageServiceExtendedKeysTests
{
    private static LocalStorageService NewInMemory() => new("Data Source=:memory:");

    private const int Vid = 10473;
    private const int Pid = 12914;        // Cyrex
    private const int OtherModelPid = 12910;
    private const string DeviceKey = "";
    private const string Guest = LocalStorageService.OwnerKindGuest;

    private static string GuestId() => Guid.NewGuid().ToString("D");

    private static PresetRecord NewPreset(string name = "P1") => new() { Name = name };

    // Already in the canonical (mode, slot1, slot2) order the reads return, so a round trip can be
    // compared as a whole; LiveRows_ReadInCanonicalOrder covers the sorting itself.
    private static ExtendedKeyRecord[] Rows() =>
    [
        ExtendedKeyRecord.RapidSnap(16, 17),
        ExtendedKeyRecord.RapidSnap(31, 33),
    ];

    private static System.Collections.Generic.List<ExtendedKeyRecord> Live(LocalStorageService s, string owner, int pid = Pid) =>
        s.GetDeviceExtendedKeys(Guest, owner, Vid, pid, DeviceKey);

    private static void SetLive(LocalStorageService s, string owner, System.Collections.Generic.IReadOnlyList<ExtendedKeyRecord> rows, int pid = Pid) =>
        s.ReplaceDeviceExtendedKeys(Guest, owner, Vid, pid, DeviceKey, rows);

    // ── Live rows ────────────────────────────────────────────────────────────

    [Fact]
    public void LiveRows_RoundTrip_EveryColumn()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        // A future-mode row exercises every column the wide table has.
        var modTap = new ExtendedKeyRecord(ExtendedKeyMode.ModTap, 30, ExtendedKeyRecord.NoSlot, 0, 150,
            new ExtendedKeyCodeRecord(0x10, 0x01, 0x00), new ExtendedKeyCodeRecord(0x10, 0x00, 0x29));
        var socd = new ExtendedKeyRecord(ExtendedKeyMode.Socd, 4, 7, 3, 0, ExtendedKeyCodeRecord.None, ExtendedKeyCodeRecord.None);
        // A Toggle row: one output in hold_key, tap_key stored and read back as "none".
        var toggle = ExtendedKeyRecord.Toggle(20, 100, new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A));

        SetLive(storage, owner, [ExtendedKeyRecord.RapidSnap(31, 33), modTap, socd, toggle]);

        var rows = Live(storage, owner);
        Assert.Equal(4, rows.Count);
        Assert.Contains(ExtendedKeyRecord.RapidSnap(31, 33), rows);
        Assert.Contains(modTap, rows);
        Assert.Contains(socd, rows);
        Assert.Contains(toggle, rows);
        Assert.Equal(ExtendedKeyCodeRecord.None, rows.Single(r => r.Mode == ExtendedKeyMode.Toggle).TapKey);
    }

    [Fact]
    public void LiveRows_NormalisePairOrder_OnWriteAndRead()
    {
        using var storage = NewInMemory();
        var owner = GuestId();

        // Picked D first, then A — stored and read back as (31, 33).
        storage.ReplaceDeviceExtendedKeys(Guest, owner, Vid, Pid, DeviceKey,
            [new ExtendedKeyRecord(ExtendedKeyMode.RapidSnap, 33, 31, 0, 0, ExtendedKeyCodeRecord.None, ExtendedKeyCodeRecord.None)]);

        var row = Assert.Single(Live(storage, owner));
        Assert.Equal(31, row.Slot1);
        Assert.Equal(33, row.Slot2);
    }

    // An SOCD absolute priority names a record position (Key 1 = slot1), so storing a reversed pair
    // must flip it along with the slots — on the live rows and on the preset snapshot alike.
    [Fact]
    public void ReversedSocdPair_FlipsTheAbsolutePriorityWithTheSlots_OnBothTables()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, NewPreset());
        var reversedKey1Wins = new ExtendedKeyRecord(ExtendedKeyMode.Socd, 19, 18, SocdPriority.Key1, 0, ExtendedKeyCodeRecord.None, ExtendedKeyCodeRecord.None);
        var reversedMutual = new ExtendedKeyRecord(ExtendedKeyMode.Socd, 33, 31, SocdPriority.MutualCancel, 0, ExtendedKeyCodeRecord.None, ExtendedKeyCodeRecord.None);
        var expected = new[]
        {
            ExtendedKeyRecord.Socd(18, 19, SocdPriority.Key2),
            ExtendedKeyRecord.Socd(31, 33, SocdPriority.MutualCancel),
        };

        SetLive(storage, owner, [reversedKey1Wins, reversedMutual]);
        storage.InsertPresetExtendedKeys(presetId, [reversedKey1Wins, reversedMutual]);

        Assert.Equal(expected, Live(storage, owner));
        Assert.Equal(expected, storage.GetPresetExtendedKeys(presetId));
    }

    [Fact]
    public void LiveRows_ReadInCanonicalOrder()
    {
        using var storage = NewInMemory();
        var owner = GuestId();

        SetLive(storage, owner, [ExtendedKeyRecord.RapidSnap(50, 52), ExtendedKeyRecord.RapidSnap(31, 33)]);

        Assert.Equal([31, 50], Live(storage, owner).Select(r => r.Slot1).ToArray());
    }

    [Fact]
    public void LiveRows_ReplaceOverwritesTheWholeSet()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        SetLive(storage, owner, Rows());

        SetLive(storage, owner, [ExtendedKeyRecord.RapidSnap(31, 33)]);

        var row = Assert.Single(Live(storage, owner));
        Assert.Equal(31, row.Slot1);

        SetLive(storage, owner, []);
        Assert.Empty(Live(storage, owner));
    }

    [Fact]
    public void LiveRows_AreScopedByOwnerAndDevice()
    {
        using var storage = NewInMemory();
        var mine = GuestId();
        var theirs = GuestId();

        SetLive(storage, mine, Rows());

        Assert.Empty(Live(storage, theirs));
        Assert.Empty(Live(storage, mine, pid: OtherModelPid));
        Assert.Equal(2, Live(storage, mine).Count);
    }

    // ── Preset snapshot ──────────────────────────────────────────────────────

    [Fact]
    public void SavePresetSnapshot_CapturesExtendedKeys_AndLoadRestoresThem()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, NewPreset());
        SetLive(storage, owner, Rows());

        storage.SavePresetSnapshot(presetId, Guest, owner, Vid, Pid, DeviceKey);
        Assert.Equal(2, storage.GetPresetExtendedKeys(presetId).Count);

        // Live state drifts, then the preset is re-applied.
        SetLive(storage, owner, [ExtendedKeyRecord.RapidSnap(1, 2)]);
        storage.LoadPresetSnapshotToDevice(presetId, Guest, owner, Vid, Pid, DeviceKey);

        Assert.Equal(Rows(), Live(storage, owner));
    }

    // The difference from actuation: an empty snapshot is "no extended keys", so applying it
    // empties the live rows — which is what removes the board's records on the next push.
    [Fact]
    public void LoadPresetSnapshot_WithNoExtendedKeys_EmptiesTheLiveRows()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, NewPreset());
        storage.SavePresetSnapshot(presetId, Guest, owner, Vid, Pid, DeviceKey);
        Assert.Empty(storage.GetPresetExtendedKeys(presetId));

        SetLive(storage, owner, Rows());
        storage.LoadPresetSnapshotToDevice(presetId, Guest, owner, Vid, Pid, DeviceKey);

        Assert.Empty(Live(storage, owner));
    }

    [Fact]
    public void SavePresetSnapshot_ReplacesThePriorSnapshot()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, NewPreset());
        SetLive(storage, owner, Rows());
        storage.SavePresetSnapshot(presetId, Guest, owner, Vid, Pid, DeviceKey);

        SetLive(storage, owner, [ExtendedKeyRecord.RapidSnap(1, 2)]);
        storage.SavePresetSnapshot(presetId, Guest, owner, Vid, Pid, DeviceKey);

        var row = Assert.Single(storage.GetPresetExtendedKeys(presetId));
        Assert.Equal(1, row.Slot1);
    }

    [Fact]
    public void DeletingAPreset_CascadesItsExtendedKeys()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var keep = storage.InsertPreset(Guest, owner, Vid, Pid, NewPreset("keep"));
        var drop = storage.InsertPreset(Guest, owner, Vid, Pid, NewPreset("drop"));
        SetLive(storage, owner, Rows());
        storage.SavePresetSnapshot(keep, Guest, owner, Vid, Pid, DeviceKey);
        storage.SavePresetSnapshot(drop, Guest, owner, Vid, Pid, DeviceKey);

        storage.DeletePreset(drop);

        Assert.Empty(storage.GetPresetExtendedKeys(drop));
        Assert.Equal(2, storage.GetPresetExtendedKeys(keep).Count);
    }

    [Fact]
    public void ResetPresetSnapshot_ClearsExtendedKeys()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, NewPreset());
        SetLive(storage, owner, Rows());
        storage.SavePresetSnapshot(presetId, Guest, owner, Vid, Pid, DeviceKey);

        storage.ResetPresetSnapshot(presetId);

        Assert.Empty(storage.GetPresetExtendedKeys(presetId));
    }

    [Fact]
    public void CopyPresetSnapshot_CopiesExtendedKeys_AndTheTwoAreIndependent()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var source = storage.InsertPreset(Guest, owner, Vid, Pid, NewPreset("src"));
        var copy = storage.InsertPreset(Guest, owner, Vid, Pid, NewPreset("copy"));
        SetLive(storage, owner, Rows());
        storage.SavePresetSnapshot(source, Guest, owner, Vid, Pid, DeviceKey);

        storage.CopyPresetSnapshot(source, copy);
        Assert.Equal(2, storage.GetPresetExtendedKeys(copy).Count);

        storage.ResetPresetSnapshot(source);
        Assert.Equal(2, storage.GetPresetExtendedKeys(copy).Count);
    }

    // ── Dirty comparison ─────────────────────────────────────────────────────

    [Fact]
    public void PresetMatchesLiveState_FalseWhenARecordIsAddedOrRemoved()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, NewPreset());
        SetLive(storage, owner, [ExtendedKeyRecord.RapidSnap(31, 33)]);
        storage.SavePresetSnapshot(presetId, Guest, owner, Vid, Pid, DeviceKey);
        Assert.True(storage.PresetMatchesLiveState(presetId, Guest, owner, Vid, Pid, DeviceKey));

        SetLive(storage, owner, Rows());
        Assert.False(storage.PresetMatchesLiveState(presetId, Guest, owner, Vid, Pid, DeviceKey));

        SetLive(storage, owner, []);
        Assert.False(storage.PresetMatchesLiveState(presetId, Guest, owner, Vid, Pid, DeviceKey));
    }

    // The same pair picked in the other order is the same record — no phantom dirtiness.
    [Fact]
    public void PresetMatchesLiveState_TrueWhenOnlyThePickOrderDiffers()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, NewPreset());
        SetLive(storage, owner, [ExtendedKeyRecord.RapidSnap(31, 33)]);
        storage.SavePresetSnapshot(presetId, Guest, owner, Vid, Pid, DeviceKey);

        storage.ReplaceDeviceExtendedKeys(Guest, owner, Vid, Pid, DeviceKey,
            [new ExtendedKeyRecord(ExtendedKeyMode.RapidSnap, 33, 31, 0, 0, ExtendedKeyCodeRecord.None, ExtendedKeyCodeRecord.None)]);

        Assert.True(storage.PresetMatchesLiveState(presetId, Guest, owner, Vid, Pid, DeviceKey));
    }

    [Fact]
    public void BackfillPresetSnapshotFromLive_IncludesExtendedKeys()
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, NewPreset());
        SetLive(storage, owner, Rows());

        storage.BackfillPresetSnapshotFromLive(presetId, Guest, owner, Vid, Pid, DeviceKey);

        Assert.Equal(2, storage.GetPresetExtendedKeys(presetId).Count);
        Assert.True(storage.PresetMatchesLiveState(presetId, Guest, owner, Vid, Pid, DeviceKey));
    }

    // ── Owner migration / clear ──────────────────────────────────────────────

    [Fact]
    public async Task GuestToUserMigration_CarriesExtendedKeys()
    {
        using var storage = NewInMemory();
        var guest = Guid.NewGuid();
        const int customer = 99;
        SetLive(storage, guest.ToString("D"), Rows());

        await storage.MigrateGuestToUserAsync(guest, customer);

        Assert.Empty(Live(storage, guest.ToString("D")));
        var migrated = storage.GetDeviceExtendedKeys(
            LocalStorageService.OwnerKindUser, customer.ToString(CultureInfo.InvariantCulture), Vid, Pid, DeviceKey);
        Assert.Equal(Rows(), migrated);
    }

    [Fact]
    public void ClearData_RemovesGuestExtendedKeys_LiveAndSnapshot()
    {
        using var storage = NewInMemory();
        var guest = GuestId();
        var presetId = storage.InsertPreset(Guest, guest, Vid, Pid, NewPreset());
        SetLive(storage, guest, Rows());
        storage.SavePresetSnapshot(presetId, Guest, guest, Vid, Pid, DeviceKey);
        Assert.NotEmpty(storage.GetPresetExtendedKeys(presetId));

        storage.ClearData();

        Assert.Empty(Live(storage, guest));
        Assert.Empty(storage.GetPresetExtendedKeys(presetId));
    }
}
