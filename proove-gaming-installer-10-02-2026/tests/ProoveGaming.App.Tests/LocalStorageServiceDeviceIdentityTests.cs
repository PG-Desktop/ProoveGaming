using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// Storage half of docs/plans/fix-duplicate-device-cards: the devices row is keyed by the card's
// IDENTITY, the same rule the device list matches on, instead of by its routing identity.
//
// The defect these pin (field report, RAMPAGE keyboard): the routing identity of a merged-transport
// product changes with the live transport — the 2.4 GHz receiver enumerates as 1389/49271 with the
// receiver's Container ID, the board's own Bluetooth as 10473/12288 with no key — so persisting the
// same physical keyboard under both left TWO rows, and the row that was not on screen came back as
// a permanently disconnected second card.
//
// Each test says what the pre-fix code did; the reproduction ones fail against it.
public class LocalStorageServiceDeviceIdentityTests : IDisposable
{
    private const string User = LocalStorageService.OwnerKindUser;
    private const string UserId = "42";
    private const string OtherUserId = "77";

    // RAMPAGE as seen over each of its two transports — the two identities that used to
    // produce two rows for one keyboard.
    private const int DongleVid = 1389;
    private const int DonglePid = 49271;
    private const string DongleKey = "C1";
    private const int BluetoothVid = 10473;
    private const int BluetoothPid = 12288;
    private const string NoKey = "";

    private static LocalStorageService NewInMemory() => new("Data Source=:memory:");

    // Only the heal-migration tests use the file store: the migration only does observable work
    // against an already-populated database, which ":memory:" can never be at construction (same
    // reasoning as LocalStorageServicePresetsTests' uid-backfill section).
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), $"pg-deviceidentity-{Guid.NewGuid():N}.db");

    private LocalStorageService NewFileStore() => new($"Data Source={_dbPath}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); }
        catch (IOException) { /* handle may still be releasing */ }
    }

    private static DeviceRecord Card(
        string name,
        int vendorId,
        int productId,
        string deviceKey = NoKey,
        bool mergeTransports = false,
        int id = 0) => new()
        {
            Id = id,
            Name = name,
            DeviceTypeLabel = "Keyboard",
            CardType = "single",
            VendorId = vendorId,
            ProductId = productId,
            DeviceKey = deviceKey,
            MergeTransports = mergeTransports
        };

    // ── AddDevice: identity-keyed upsert ─────────────────────────────────────

    // THE REPRODUCTION. One keyboard, persisted first over its dongle and then over Bluetooth —
    // exactly what a transport switch (or a sign-out/sign-in on the other transport) does.
    // Pre-fix: the delete was keyed on (owner, vendor_id, product_id, device_key, name), which the
    // second identity never matched, so the table held TWO rows and the device list showed two
    // cards — one connected, one permanently "NO CONNECTION".
    [Fact]
    public void MergedCard_PersistedUnderBothTransportIdentities_LeavesOneRow()
    {
        using var storage = NewInMemory();

        storage.AddDevice(User, UserId, Card("RAMPAGE", DongleVid, DonglePid, DongleKey, mergeTransports: true));
        storage.AddDevice(User, UserId, Card("RAMPAGE", BluetoothVid, BluetoothPid, NoKey, mergeTransports: true));

        var row = Assert.Single(storage.GetDevices(User, UserId));
        Assert.Equal("RAMPAGE", row.Name);
        Assert.True(row.MergeTransports);
        // The surviving row describes the transport persisted last — the one the device is on now.
        Assert.Equal(BluetoothVid, row.VendorId);
        Assert.Equal(BluetoothPid, row.ProductId);
    }

    // The case the fix must NOT break (PR #112 M2): two identical physical mice share vendor,
    // product and name and are told apart only by their Container ID. They are not merged-transport,
    // so the old tuple key still applies and both rows stay.
    [Fact]
    public void NonMergedTwoUnitsOfOneModel_KeepSeparateRows()
    {
        using var storage = NewInMemory();

        storage.AddDevice(User, UserId, Card("PG Taipan", 0x28E9, 0x3252, "A"));
        storage.AddDevice(User, UserId, Card("PG Taipan", 0x28E9, 0x3252, "B"));

        var rows = storage.GetDevices(User, UserId);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "A", "B" }, rows.Select(r => r.DeviceKey).OrderBy(k => k).ToArray());
        Assert.All(rows, r => Assert.False(r.MergeTransports));
    }

    // Delete-by-StorageId: a non-merged mouse moved to another USB port keeps its vendor/product
    // and name but gets a new Container ID, so the tuple delete misses its old row. Passing the
    // row the card already owns replaces it in place.
    // Pre-fix: AddDevice ignored device.Id entirely, so the port move left the old row behind —
    // TWO rows for one mouse, the stale one surfacing as a disconnected card.
    [Fact]
    public void RePersistingByStorageId_AfterTheKeyMoved_ReplacesTheRowInsteadOfOrphaningIt()
    {
        using var storage = NewInMemory();
        var firstId = storage.AddDevice(User, UserId, Card("PG Taipan", 0x28E9, 0x3252, "PORT-1"));

        var secondId = storage.AddDevice(
            User, UserId, Card("PG Taipan", 0x28E9, 0x3252, "PORT-2", id: firstId));

        var row = Assert.Single(storage.GetDevices(User, UserId));
        Assert.Equal("PORT-2", row.DeviceKey);
        Assert.Equal(secondId, row.Id);
        Assert.NotEqual(firstId, secondId);
    }

    // The delete-by-id is owner-scoped, so one owner's StorageId can never reach into another's
    // rows — ids are only devices-table ids while a user owns the card; under a guest session they
    // come from UserSession's own counter.
    [Fact]
    public void RePersistingByStorageId_NeverTouchesAnotherOwnersRow()
    {
        using var storage = NewInMemory();
        var otherId = storage.AddDevice(User, OtherUserId, Card("PG Taipan", 0x28E9, 0x3252, "A"));

        storage.AddDevice(User, UserId, Card("Some Mouse", 1, 2, "X", id: otherId));

        var kept = Assert.Single(storage.GetDevices(User, OtherUserId));
        Assert.Equal(otherId, kept.Id);
        Assert.Equal("PG Taipan", kept.Name);
    }

    // Discovery upper-cases the client name and matches case-insensitively while SQLite's
    // `name = $name` does not, so the same card arriving under a different casing was a second,
    // latent source of duplicates. Same transport identity here, so the CASING is the only thing
    // that differs.
    // Pre-fix: the case-sensitive delete missed, leaving TWO rows.
    [Fact]
    public void MergedCard_NameComparesCaseInsensitively_LeavingOneRow()
    {
        using var storage = NewInMemory();

        storage.AddDevice(User, UserId, Card("RAMPAGE", DongleVid, DonglePid, DongleKey, mergeTransports: true));
        storage.AddDevice(User, UserId, Card("Rampage", DongleVid, DonglePid, DongleKey, mergeTransports: true));

        var row = Assert.Single(storage.GetDevices(User, UserId));
        Assert.Equal("Rampage", row.Name);
    }

    // A merged card's dedupe is scoped to its owner: collapsing by name alone must not reach
    // across accounts.
    [Fact]
    public void MergedCard_DedupeIsScopedToItsOwner()
    {
        using var storage = NewInMemory();
        storage.AddDevice(User, OtherUserId, Card("RAMPAGE", DongleVid, DonglePid, DongleKey, mergeTransports: true));

        storage.AddDevice(User, UserId, Card("RAMPAGE", BluetoothVid, BluetoothPid, NoKey, mergeTransports: true));

        Assert.Single(storage.GetDevices(User, UserId));
        Assert.Single(storage.GetDevices(User, OtherUserId));
    }

    // ── Guest round-trip ─────────────────────────────────────────────────────

    // The guest list is rewritten whole by Flush rather than upserted, so the flag has to survive
    // that path too: a guest's restored RAMPAGE card must know it is merged-transport BEFORE
    // discovery reports in, or its first re-persist duplicates it again.
    [Fact]
    public void GuestFlush_RoundTripsTheMergeTransportsFlag()
    {
        using var storage = NewInMemory();

        storage.Flush(new List<DeviceRecord>
        {
            Card("RAMPAGE", DongleVid, DonglePid, DongleKey, mergeTransports: true),
            Card("PG Taipan", 0x28E9, 0x3252, "A")
        });

        var rows = storage.GetDevices();
        Assert.Equal(2, rows.Count);
        Assert.True(rows.Single(r => r.Name == "RAMPAGE").MergeTransports);
        Assert.False(rows.Single(r => r.Name == "PG Taipan").MergeTransports);
    }

    // ── Low-battery announced band (docs/plans/fix-low-battery-notifications, 2026-08-31) ────

    // The announced-band state was process-local, so every app launch with a sub-20 % mouse
    // toasted again ("random moments", QA 2026-08-31). It now rides the devices row through
    // every persistence path: the signed-in write-through upsert, the targeted update the
    // announcement itself writes, and the whole-list guest flush.
    [Fact]
    public void AddDevice_RoundTripsTheLowBatteryAnnouncedBand()
    {
        using var storage = NewInMemory();

        var card = Card("PG Taipan", 0x28E9, 0x3252, "A");
        card.LowBatteryAnnouncedBand = 20;
        storage.AddDevice(User, UserId, card);

        Assert.Equal(20, Assert.Single(storage.GetDevices(User, UserId)).LowBatteryAnnouncedBand);
    }

    [Fact]
    public void SetDeviceLowBatteryAnnouncedBand_UpdatesTheStoredRow()
    {
        using var storage = NewInMemory();

        var id = storage.AddDevice(User, UserId, Card("PG Taipan", 0x28E9, 0x3252, "A"));
        storage.SetDeviceLowBatteryAnnouncedBand(User, UserId, id, 10);

        Assert.Equal(10, Assert.Single(storage.GetDevices(User, UserId)).LowBatteryAnnouncedBand);

        // Owner-scoped like DeleteDevice: a mismatched owner updates nothing.
        storage.SetDeviceLowBatteryAnnouncedBand(User, OtherUserId, id, 20);
        Assert.Equal(10, Assert.Single(storage.GetDevices(User, UserId)).LowBatteryAnnouncedBand);

        // Re-arm clears it the same way.
        storage.SetDeviceLowBatteryAnnouncedBand(User, UserId, id, 0);
        Assert.Equal(0, Assert.Single(storage.GetDevices(User, UserId)).LowBatteryAnnouncedBand);
    }

    [Fact]
    public void GuestFlush_RoundTripsTheLowBatteryAnnouncedBand()
    {
        using var storage = NewInMemory();

        var card = Card("PG Taipan", 0x28E9, 0x3252, "A");
        card.LowBatteryAnnouncedBand = 10;
        storage.Flush(new List<DeviceRecord> { card });

        Assert.Equal(10, Assert.Single(storage.GetDevices()).LowBatteryAnnouncedBand);
    }

    // ── Heal migration for databases written before the column existed ───────

    // The devices table as the previous app version created it — everything the current schema has
    // EXCEPT merge_transports. Raw SQL because the current schema block would create the NEW shape,
    // and the point is to hand the constructor a database it must heal.
    private void CreateLegacyDevicesDb(params (string OwnerId, string Name, int Vid, int Pid, string Key)[] rows)
    {
        using var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE devices (
                    id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                    owner_kind         TEXT NOT NULL DEFAULT 'guest',
                    owner_id           TEXT NOT NULL DEFAULT '',
                    name               TEXT NOT NULL,
                    device_type_label  TEXT NOT NULL,
                    image_path         TEXT,
                    battery_level      INTEGER,
                    connection_icon    TEXT,
                    card_type          TEXT NOT NULL DEFAULT 'single',
                    vendor_id          INTEGER NOT NULL DEFAULT 0,
                    product_id         INTEGER NOT NULL DEFAULT 0,
                    device_key         TEXT NOT NULL DEFAULT ''
                );
                """;
            create.ExecuteNonQuery();
        }

        foreach (var (ownerId, name, vid, pid, key) in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO devices (owner_kind, owner_id, name, device_type_label, card_type, vendor_id, product_id, device_key)
                VALUES ($ok, $oi, $n, 'Keyboard', 'single', $v, $p, $k);
                """;
            insert.Parameters.AddWithValue("$ok", User);
            insert.Parameters.AddWithValue("$oi", ownerId);
            insert.Parameters.AddWithValue("$n", name);
            insert.Parameters.AddWithValue("$v", vid);
            insert.Parameters.AddWithValue("$p", pid);
            insert.Parameters.AddWithValue("$k", key);
            insert.ExecuteNonQuery();
        }
    }

    // The reporter's database: two RAMPAGE rows under one account, one per transport. On the first
    // launch after the fix the extra row is gone with no manual cleanup, and the survivor carries
    // the flag so it can never split again.
    // Pre-fix: nothing pruned stale device rows at all — both rows stayed and the ghost card
    // returned on every sign-in.
    [Fact]
    public void LegacyDb_RampageShapedPair_CollapsesToTheHighestIdWithTheFlagSet()
    {
        CreateLegacyDevicesDb(
            (UserId, "RAMPAGE", DongleVid, DonglePid, DongleKey),
            (UserId, "RAMPAGE", BluetoothVid, BluetoothPid, NoKey),
            // Another account that owns a single RAMPAGE row — nothing to collapse there.
            (OtherUserId, "RAMPAGE", DongleVid, DonglePid, DongleKey));

        using var storage = NewFileStore();

        var row = Assert.Single(storage.GetDevices(User, UserId));
        Assert.Equal(2, row.Id); // MAX(id) of the group — the transport persisted most recently
        Assert.Equal(BluetoothVid, row.VendorId);
        Assert.True(row.MergeTransports);

        // The other owner is untouched, flag included: a lone row is not evidence of anything.
        var other = Assert.Single(storage.GetDevices(User, OtherUserId));
        Assert.Equal(3, other.Id);
        Assert.False(other.MergeTransports);
    }

    // The case the heuristic must refuse to touch: two identical physical units share one
    // (vendor_id, product_id) and differ only by Container ID, so their group holds a single pair
    // and is never collapsed. This is the whole reason the migration is not "same name = same card".
    [Fact]
    public void LegacyDb_TwoIdenticalUnits_AreBothPreserved()
    {
        CreateLegacyDevicesDb(
            (UserId, "PG Taipan", 0x28E9, 0x3252, "A"),
            (UserId, "PG Taipan", 0x28E9, 0x3252, "B"));

        using var storage = NewFileStore();

        var rows = storage.GetDevices(User, UserId);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "A", "B" }, rows.Select(r => r.DeviceKey).OrderBy(k => k).ToArray());
        Assert.All(rows, r => Assert.False(r.MergeTransports));
    }

    // THE REPRODUCTION (D2, fix-owner-scoped-state-persistence). The heuristic's stated safety
    // argument — "two identical units share one pair, so their group has a single pair and is never
    // touched" — holds only for a group of exactly two. Add ONE extra transport row for one of the
    // units and the group has two distinct pairs, so the flag+delete collapsed BOTH units into a
    // single card: the second physical mouse the user owns vanished, irreversibly, on one launch.
    // The bail-out leaves any group that PROVES two units exist (two rows sharing a pair, differing
    // in device_key) entirely alone.
    // Pre-fix: one row survived out of three.
    [Fact]
    public void LegacyDb_TwoUnitsPlusAnExtraTransportRow_KeepsBothUnits()
    {
        CreateLegacyDevicesDb(
            (UserId, "PG Taipan", 0x28E9, 0x3252, "UNIT-A"),
            (UserId, "PG Taipan", 0x28E9, 0x3252, "UNIT-B"),
            // Unit A also seen over a second transport — a different (vendor, product) pair, which
            // is what used to drag the whole group into the collapse.
            (UserId, "PG Taipan", 0x248A, 0x0001, "UNIT-A"));

        using var storage = NewFileStore();

        var rows = storage.GetDevices(User, UserId);
        Assert.Equal(3, rows.Count);
        // Not merely undeleted — untouched: an un-flagged row is one AddDevice can still collapse
        // correctly later, under catalog authority, when the device next connects.
        Assert.All(rows, r => Assert.False(r.MergeTransports));
    }

    // The guard is a bail-out, not a blanket stop: the reported RAMPAGE shape has no repeated
    // (vendor_id, product_id) pair, so nothing there proves a second physical unit and the shipped
    // collapse still happens. This is the inertness half of the D2 fix — without it the bail-out
    // could silently disable the migration the previous plan shipped.
    [Fact]
    public void LegacyDb_BailOut_IsInertForAGroupWithNoRepeatedPair()
    {
        CreateLegacyDevicesDb(
            (UserId, "RAMPAGE", DongleVid, DonglePid, DongleKey),
            (UserId, "RAMPAGE", BluetoothVid, BluetoothPid, NoKey),
            // A third transport, distinct pair again, distinct key — still no proof of two units.
            (UserId, "RAMPAGE", 9999, 8888, "WIRED"));

        using var storage = NewFileStore();

        var row = Assert.Single(storage.GetDevices(User, UserId));
        Assert.Equal(3, row.Id); // MAX(id) of the group
        Assert.True(row.MergeTransports);
    }

    // The bail-out is scoped to the (owner, name) group that holds the evidence: another owner's
    // two-unit group must not shield THIS owner's genuine duplicate from the collapse.
    [Fact]
    public void LegacyDb_BailOut_IsScopedToTheGroupHoldingTheEvidence()
    {
        CreateLegacyDevicesDb(
            // Owner 77 owns two identical units of a model that happens to share the name.
            (OtherUserId, "RAMPAGE", 0x28E9, 0x3252, "UNIT-A"),
            (OtherUserId, "RAMPAGE", 0x28E9, 0x3252, "UNIT-B"),
            // Owner 42's own group is the plain two-transport duplicate and must still collapse.
            (UserId, "RAMPAGE", DongleVid, DonglePid, DongleKey),
            (UserId, "RAMPAGE", BluetoothVid, BluetoothPid, NoKey));

        using var storage = NewFileStore();

        var collapsed = Assert.Single(storage.GetDevices(User, UserId));
        Assert.True(collapsed.MergeTransports);
        Assert.Equal(2, storage.GetDevices(User, OtherUserId).Count);
    }

    // The AC that keeps the guard honest on old data: in a database written before device_key
    // existed every key is '', so no two rows can differ by it and the bail-out can never fire.
    // The heal must therefore produce exactly what the previous build produced.
    [Fact]
    public void LegacyDb_WithNoDeviceKeys_HealsExactlyAsBefore()
    {
        CreateLegacyDevicesDb(
            (UserId, "RAMPAGE", DongleVid, DonglePid, NoKey),
            (UserId, "RAMPAGE", BluetoothVid, BluetoothPid, NoKey),
            (UserId, "PG Taipan", 0x28E9, 0x3252, NoKey));

        using var storage = NewFileStore();

        var rows = storage.GetDevices(User, UserId);
        Assert.Equal(2, rows.Count);
        var rampage = rows.Single(r => r.Name == "RAMPAGE");
        Assert.Equal(2, rampage.Id);
        Assert.True(rampage.MergeTransports);
        // A lone row is not evidence of anything and stays unflagged.
        Assert.False(rows.Single(r => r.Name == "PG Taipan").MergeTransports);
    }

    // A same-transport port move (same pair, different key) is likewise not the migration's
    // business — going forward the StorageId delete handles it, and retroactively guessing would
    // risk collapsing the two-units case.
    [Fact]
    public void LegacyDb_SameProductDifferentKey_IsNotCollapsed()
    {
        CreateLegacyDevicesDb(
            (UserId, "PG Taipan", 0x28E9, 0x3252, "PORT-1"),
            (UserId, "PG Taipan", 0x28E9, 0x3252, "PORT-2"));

        using var storage = NewFileStore();

        Assert.Equal(2, storage.GetDevices(User, UserId).Count);
    }

    // The heal is guarded on the ALTER that adds the column, so it runs on exactly one
    // construction. Proved with rows written AFTER the heal that the heuristic would happily
    // collapse if it ever ran again: two different products that happen to share a name, kept
    // apart by the non-merged tuple key.
    [Fact]
    public void LegacyDb_Heal_RunsOnlyOnce_AndDoesNotDeleteOnLaterConstructions()
    {
        CreateLegacyDevicesDb(
            (UserId, "RAMPAGE", DongleVid, DonglePid, DongleKey),
            (UserId, "RAMPAGE", BluetoothVid, BluetoothPid, NoKey));

        using (var first = NewFileStore())
        {
            Assert.Single(first.GetDevices(User, UserId)); // healed
            first.AddDevice(User, UserId, Card("Combo", 1, 2, "A"));
            first.AddDevice(User, UserId, Card("Combo", 3, 4, "B"));
            Assert.Equal(3, first.GetDevices(User, UserId).Count);
        }

        using var reopened = NewFileStore();

        // Two distinct (vendor, product) pairs under one name — the exact shape the heal collapses.
        // Both survive, so the migration did not run a second time.
        var rows = reopened.GetDevices(User, UserId);
        Assert.Equal(3, rows.Count);
        Assert.Equal(2, rows.Count(r => r.Name == "Combo"));
        Assert.All(rows.Where(r => r.Name == "Combo"), r => Assert.False(r.MergeTransports));
    }

    // A database created fresh by the current schema already has the column, so the heal never
    // runs on it — a first launch on a clean machine must not walk the table at all.
    [Fact]
    public void FreshDb_IsNotHealed_AndKeepsSameNameDistinctProductsApart()
    {
        using (var first = NewFileStore())
        {
            first.AddDevice(User, UserId, Card("Combo", 1, 2, "A"));
            first.AddDevice(User, UserId, Card("Combo", 3, 4, "B"));
        }

        using var reopened = NewFileStore();

        Assert.Equal(2, reopened.GetDevices(User, UserId).Count);
    }

    // ── The identity rule itself ─────────────────────────────────────────────

    // One definition shared by the revive pass, the discovery-tick re-persist test and (through
    // DeviceRecord) the storage dedupe — so a merged card compares equal across transports while
    // every other card stays keyed per physical unit.
    [Fact]
    public void CardIdentity_MergedCard_IgnoresTheRoutingIdentity()
    {
        var dongle = ViewModels.HomeViewModel.CardIdentity("RAMPAGE", DongleVid, DonglePid, DongleKey, true);
        var bluetooth = ViewModels.HomeViewModel.CardIdentity("RAMPAGE", BluetoothVid, BluetoothPid, NoKey, true);

        Assert.Equal(dongle, bluetooth);
    }

    [Fact]
    public void CardIdentity_NonMergedCards_StayApartByDeviceKey()
    {
        var unitA = ViewModels.HomeViewModel.CardIdentity("PG Taipan", 0x28E9, 0x3252, "A", false);
        var unitB = ViewModels.HomeViewModel.CardIdentity("PG Taipan", 0x28E9, 0x3252, "B", false);

        Assert.NotEqual(unitA, unitB);
    }

    // A merged card is keyed on its NAME, so the name is the one thing that still moves its
    // identity. That is why the discovery tick captures the identity before FillConnectionCard —
    // which restamps the name from the catalog — rather than after: capturing afterwards would
    // compare the new name against itself and miss the very change the re-persist exists for.
    [Fact]
    public void CardIdentity_MergedCard_StillMovesWithTheName()
    {
        Assert.NotEqual(
            ViewModels.HomeViewModel.CardIdentity("RAMPAGE", DongleVid, DonglePid, DongleKey, true),
            ViewModels.HomeViewModel.CardIdentity("TAIPAN", DongleVid, DonglePid, DongleKey, true));
    }

    [Fact]
    public void CardIdentity_FoldsNameCase_AndTreatsANullKeyAsEmpty()
    {
        Assert.Equal(
            ViewModels.HomeViewModel.CardIdentity("RAMPAGE", 1, 2, "", false),
            ViewModels.HomeViewModel.CardIdentity("Rampage", 1, 2, null, false));
    }
}
