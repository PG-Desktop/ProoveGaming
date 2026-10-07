using System;
using System.Linq;
using System.Threading.Tasks;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// QA 2026-08-19: closing and reopening the app lost every notification and every previously-seen
// device card. Devices were persisted only inside the guest session (a signed-in user had no
// storage at all, and sign-in even wiped the table); notifications were memory-only. These tests
// pin the storage layer that fixes both: owner-scoped device rows and the notifications table.
public class LocalStorageServiceOwnerScopedPersistenceTests
{
    private const string UserId = "42";

    // Each test gets a fresh, in-memory DB (the connection lifetime IS the DB lifetime).
    private static LocalStorageService NewInMemory() =>
        new("Data Source=:memory:");

    private static DeviceRecord Mouse(string name = "PG Taipan") => new()
    {
        Name = name,
        DeviceTypeLabel = "Wireless Mouse",
        CardType = "single",
        VendorId = 0x28E9,
        ProductId = 0x3252,
        BatteryLevel = 55
    };

    private static NotificationRecord Bell(string title, DateTime? at = null) => new()
    {
        Title = title,
        Subtitle = "PG Taipan",
        IconUri = "avares://ProoveGaming.Common/Assets/Icons/Device-Connected.svg",
        IconWidth = 22,
        IconHeight = 22,
        CreatedAt = at ?? DateTime.Now
    };

    // ── User-owned devices ───────────────────────────────────────────────────

    [Fact]
    public void UserDevices_RoundTripThroughStorage()
    {
        using var storage = NewInMemory();

        var id = storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, Mouse());

        var restored = Assert.Single(storage.GetDevices(LocalStorageService.OwnerKindUser, UserId));
        Assert.Equal(id, restored.Id);
        Assert.Equal("PG Taipan", restored.Name);
        Assert.Equal(0x28E9, restored.VendorId);
        Assert.Equal(55, restored.BatteryLevel);
    }

    // Sign-in re-adopts every on-screen card, so storing the same device twice must replace,
    // not duplicate.
    [Fact]
    public void AddDevice_IsAnUpsertOnOwnerVendorProductKeyName()
    {
        using var storage = NewInMemory();

        storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, Mouse());
        var record = Mouse();
        record.BatteryLevel = 20;
        storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, record);

        var restored = Assert.Single(storage.GetDevices(LocalStorageService.OwnerKindUser, UserId));
        Assert.Equal(20, restored.BatteryLevel);
    }

    // PR #112 M2: two identical units of one model share VID/PID/name and differ only by their
    // Container ID — the upsert must keep them as two rows, not let the second erase the first.
    [Fact]
    public void AddDevice_KeepsTwoIdenticalUnitsApartByDeviceKey()
    {
        using var storage = NewInMemory();

        var unitA = Mouse();
        unitA.DeviceKey = "container-a";
        var unitB = Mouse();
        unitB.DeviceKey = "container-b";
        var idA = storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, unitA);
        storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, unitB);

        var rows = storage.GetDevices(LocalStorageService.OwnerKindUser, UserId);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "container-a", "container-b" }, rows.Select(r => r.DeviceKey).OrderBy(k => k));

        // Deleting unit A by its id leaves unit B's row alone — the "deleted card comes back"
        // half of the finding.
        storage.DeleteDevice(LocalStorageService.OwnerKindUser, UserId, idA);
        var survivor = Assert.Single(storage.GetDevices(LocalStorageService.OwnerKindUser, UserId));
        Assert.Equal("container-b", survivor.DeviceKey);
    }

    // PR #112 m2: sign-in's ClearSession must take the finished guest session's bell history with
    // it — those rows are keyed by that session's uuid, and no future owner could ever load them.
    [Fact]
    public void ClearSession_RemovesGuestNotifications()
    {
        using var storage = NewInMemory();
        var guestId = Guid.NewGuid().ToString("D");
        storage.InitializeSession(Guid.NewGuid());
        storage.AddNotification(LocalStorageService.OwnerKindGuest, guestId, Bell("Guest note"));
        storage.AddNotification(LocalStorageService.OwnerKindUser, UserId, Bell("User note"));

        storage.ClearSession();

        Assert.Empty(storage.GetNotifications(LocalStorageService.OwnerKindGuest, guestId));
        Assert.Single(storage.GetNotifications(LocalStorageService.OwnerKindUser, UserId));
    }

    // THE REPRODUCTION (D5, fix-owner-scoped-state-persistence). AddDevice's delete-by-id was made
    // owner-scoped on this branch while DeleteDevice was left keyed on the id alone — two delete
    // paths into one table disagreeing about the same invariant. A card's StorageId is only a
    // devices-table id while a USER owns it: under a guest session the ids come from UserSession's
    // own counter starting at 1, so a guest id collides with a real row by construction.
    // Pre-fix: the row belonging to the OTHER owner was deleted.
    [Fact]
    public void DeleteDevice_DoesNotTouchAnotherOwnersRow()
    {
        using var storage = NewInMemory();
        var foreignId = storage.AddDevice(LocalStorageService.OwnerKindUser, "77", Mouse("B's Mouse"));

        // Owner A asks to delete THEIR card, but hands over an id that is in fact B's — the guest
        // counter collision, or simply a stale StorageId carried across an owner change.
        storage.DeleteDevice(LocalStorageService.OwnerKindUser, UserId, foreignId);

        var survivor = Assert.Single(storage.GetDevices(LocalStorageService.OwnerKindUser, "77"));
        Assert.Equal(foreignId, survivor.Id);
        Assert.Equal("B's Mouse", survivor.Name);
    }

    // The same scoping seen from the guest side: a guest StorageId of 1 must not reach a user row
    // that happens to have id 1.
    [Fact]
    public void DeleteDevice_WithAGuestOwnerKey_LeavesUserRowsAlone()
    {
        using var storage = NewInMemory();
        var userRowId = storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, Mouse());

        storage.DeleteDevice(LocalStorageService.OwnerKindGuest, Guid.NewGuid().ToString("D"), userRowId);

        Assert.Single(storage.GetDevices(LocalStorageService.OwnerKindUser, UserId));
    }

    [Fact]
    public void DeleteDevice_RemovesTheRow()
    {
        using var storage = NewInMemory();
        var id = storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, Mouse());

        storage.DeleteDevice(LocalStorageService.OwnerKindUser, UserId, id);

        Assert.Empty(storage.GetDevices(LocalStorageService.OwnerKindUser, UserId));
    }

    // The QA bug's second half: sign-in calls ClearSession, which used to wipe the whole devices
    // table — including what the signing-in user had stored.
    [Fact]
    public void ClearSession_KeepsUserOwnedDevices()
    {
        using var storage = NewInMemory();
        storage.InitializeSession(Guid.NewGuid());
        storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, Mouse());

        storage.ClearSession();

        Assert.Single(storage.GetDevices(LocalStorageService.OwnerKindUser, UserId));
    }

    [Fact]
    public void ClearData_WipesGuestDevicesButKeepsUserDevices()
    {
        using var storage = NewInMemory();
        var session = new UserSession(Guid.NewGuid());
        session.Devices.Add(Mouse("Guest Mouse"));
        storage.Flush(session);
        storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, Mouse("User Mouse"));

        storage.ClearData();

        Assert.Empty(storage.GetDevices());
        var kept = Assert.Single(storage.GetDevices(LocalStorageService.OwnerKindUser, UserId));
        Assert.Equal("User Mouse", kept.Name);
    }

    // The guest flow reads and rewrites only its own rows: a user's cards must be invisible to the
    // session restore and must survive the session flush's delete-and-rewrite.
    [Fact]
    public void GuestFlushAndRestore_DoNotTouchUserRows()
    {
        using var storage = NewInMemory();
        storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, Mouse("User Mouse"));

        var session = new UserSession(Guid.NewGuid());
        session.Devices.Add(Mouse("Guest Mouse"));
        storage.Flush(session);

        var guestVisible = Assert.Single(storage.GetDevices());
        Assert.Equal("Guest Mouse", guestVisible.Name);
        var userVisible = Assert.Single(storage.GetDevices(LocalStorageService.OwnerKindUser, UserId));
        Assert.Equal("User Mouse", userVisible.Name);
    }

    // ── Notifications ────────────────────────────────────────────────────────

    [Fact]
    public void Notifications_RoundTripNewestFirst()
    {
        using var storage = NewInMemory();
        var owner = (LocalStorageService.OwnerKindUser, UserId);
        var first = Bell("Device connected", new DateTime(2026, 8, 18, 9, 30, 0));
        var second = Bell("Low battery", new DateTime(2026, 8, 19, 10, 0, 0));

        storage.AddNotification(owner.Item1, owner.Item2, first);
        storage.AddNotification(owner.Item1, owner.Item2, second);

        var restored = storage.GetNotifications(owner.Item1, owner.Item2);
        Assert.Equal(2, restored.Count);
        Assert.Equal("Low battery", restored[0].Title);
        Assert.Equal("Device connected", restored[1].Title);
        Assert.Equal(new DateTime(2026, 8, 18, 9, 30, 0), restored[1].CreatedAt);
        Assert.False(restored[0].IsRead);
    }

    [Fact]
    public void SetNotificationRead_Persists()
    {
        using var storage = NewInMemory();
        var id = storage.AddNotification(LocalStorageService.OwnerKindUser, UserId, Bell("Device connected"));

        storage.SetNotificationRead(id, true);

        var restored = Assert.Single(storage.GetNotifications(LocalStorageService.OwnerKindUser, UserId));
        Assert.True(restored.IsRead);
    }

    [Fact]
    public void DeleteNotification_And_DeleteAll_RemoveRows()
    {
        using var storage = NewInMemory();
        var id = storage.AddNotification(LocalStorageService.OwnerKindUser, UserId, Bell("One"));
        storage.AddNotification(LocalStorageService.OwnerKindUser, UserId, Bell("Two"));

        storage.DeleteNotification(id);
        Assert.Single(storage.GetNotifications(LocalStorageService.OwnerKindUser, UserId));

        storage.DeleteAllNotifications(LocalStorageService.OwnerKindUser, UserId);
        Assert.Empty(storage.GetNotifications(LocalStorageService.OwnerKindUser, UserId));
    }

    [Fact]
    public void Notifications_AreCappedAtOneHundredNewest()
    {
        using var storage = NewInMemory();
        for (var i = 1; i <= 105; i++)
            storage.AddNotification(LocalStorageService.OwnerKindUser, UserId, Bell($"n{i}"));

        var restored = storage.GetNotifications(LocalStorageService.OwnerKindUser, UserId);
        Assert.Equal(100, restored.Count);
        Assert.Equal("n105", restored[0].Title);
        Assert.Equal("n6", restored[^1].Title); // n1..n5 trimmed as the oldest
    }

    [Fact]
    public void ClearData_WipesGuestNotificationsButKeepsUserOnes()
    {
        using var storage = NewInMemory();
        var guestId = Guid.NewGuid().ToString("D");
        storage.AddNotification(LocalStorageService.OwnerKindGuest, guestId, Bell("Guest note"));
        storage.AddNotification(LocalStorageService.OwnerKindUser, UserId, Bell("User note"));

        storage.ClearData();

        Assert.Empty(storage.GetNotifications(LocalStorageService.OwnerKindGuest, guestId));
        Assert.Single(storage.GetNotifications(LocalStorageService.OwnerKindUser, UserId));
    }

    // ── Guest → user sign-up migration ───────────────────────────────────────

    [Fact]
    public async Task Migration_MovesGuestNotificationsAndDevicesToTheUser()
    {
        using var storage = NewInMemory();
        var guestUuid = Guid.NewGuid();
        storage.AddNotification(LocalStorageService.OwnerKindGuest, guestUuid.ToString("D"), Bell("From guest days"));
        var session = new UserSession(guestUuid);
        session.Devices.Add(Mouse());
        storage.Flush(session);

        await storage.MigrateGuestToUserAsync(guestUuid, 42);

        var note = Assert.Single(storage.GetNotifications(LocalStorageService.OwnerKindUser, UserId));
        Assert.Equal("From guest days", note.Title);
        var device = Assert.Single(storage.GetDevices(LocalStorageService.OwnerKindUser, UserId));
        Assert.Equal("PG Taipan", device.Name);
        Assert.Empty(storage.GetDevices()); // nothing left under guest
    }

    // Guest wins on a device the account already holds — same rule as every migrated table.
    [Fact]
    public async Task Migration_GuestDeviceReplacesCollidingUserDevice()
    {
        using var storage = NewInMemory();
        var stale = Mouse();
        stale.BatteryLevel = 5;
        storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, stale);

        var guestUuid = Guid.NewGuid();
        var session = new UserSession(guestUuid);
        session.Devices.Add(Mouse()); // BatteryLevel 55
        storage.Flush(session);

        await storage.MigrateGuestToUserAsync(guestUuid, 42);

        var merged = Assert.Single(storage.GetDevices(LocalStorageService.OwnerKindUser, UserId));
        Assert.Equal(55, merged.BatteryLevel);
    }

    // ── Settings identity on the devices row ─────────────────────────────────
    // docs/plans/fix-settings-bucket-identity, defect 2. A restored card is deliberately clickable
    // before discovery reports in, and opening it WRITES (SetDeviceAsync persists the resolved RGB
    // state; LoadPresetsForDeviceAsync seeds a default preset and its snapshot). Before these
    // columns existed the card had to guess its settings bucket from the stored ROUTING identity,
    // so those writes landed at a pair no discovered card would ever address again — a dongle-last
    // RAMPAGE wrote to (1389, 49271, ''). Storing the bucket is what makes the guess unnecessary.

    private static DeviceRecord RampageOnItsDongle() => new()
    {
        Name = "RAMPAGE",
        DeviceTypeLabel = "Wireless Keyboard",
        CardType = "single",
        MergeTransports = true,
        // Routing: what the receiver enumerates as.
        VendorId = 1389,
        ProductId = 49271,
        DeviceKey = "receiver-container-id",
        // Settings: pinned to the board, shared by every transport.
        SettingsVendorId = 10473,
        SettingsProductId = 12888,
        SettingsDeviceKey = "",
    };

    [Fact]
    public void AddDevice_RoundTripsTheSettingsIdentity()
    {
        using var storage = NewInMemory();

        storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, RampageOnItsDongle());

        var restored = Assert.Single(storage.GetDevices(LocalStorageService.OwnerKindUser, UserId));
        Assert.Equal(10473, restored.SettingsVendorId);
        Assert.Equal(12888, restored.SettingsProductId);
        Assert.Equal("", restored.SettingsDeviceKey);
        // The routing identity is stored SEPARATELY and still follows the live transport.
        Assert.Equal(1389, restored.VendorId);
        Assert.Equal(49271, restored.ProductId);
        Assert.Equal("receiver-container-id", restored.DeviceKey);
    }

    // A per-unit device carries a real settings device key, and it must survive the round-trip —
    // that key is what keeps two identical mice from sharing one bucket.
    [Fact]
    public void AddDevice_RoundTripsAPerUnitSettingsDeviceKey()
    {
        using var storage = NewInMemory();
        var mouse = Mouse();
        mouse.SettingsVendorId = mouse.VendorId;
        mouse.SettingsProductId = mouse.ProductId;
        mouse.SettingsDeviceKey = "unit-a";

        storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, mouse);

        var restored = Assert.Single(storage.GetDevices(LocalStorageService.OwnerKindUser, UserId));
        Assert.Equal("unit-a", restored.SettingsDeviceKey);
    }

    // The guest path holds DeviceRecords in UserSession and flushes through the same table, so it
    // must carry the settings identity too — otherwise a guest's cards lose their bucket on every
    // restart while a signed-in user's keep it.
    [Fact]
    public void GuestFlush_RoundTripsTheSettingsIdentity()
    {
        using var storage = NewInMemory();
        var session = new UserSession(Guid.NewGuid());
        session.Devices.Add(RampageOnItsDongle());

        storage.Flush(session);

        var restored = Assert.Single(storage.GetDevices());
        Assert.Equal(10473, restored.SettingsVendorId);
        Assert.Equal(12888, restored.SettingsProductId);
    }

    // A row written before the columns existed reads back as 0 / 0 / "" — the sentinel
    // RecordToViewModel maps to the pre-migration derivation, so a legacy card behaves exactly as
    // it did until discovery corrects it. Nothing may invent a vendor here: 0 is "unknown", and a
    // settings vendor of 0 would be a real key addressing every pre-scoping row.
    [Fact]
    public void LegacyRow_WithoutSettingsColumns_RestoresAsBefore()
    {
        using var storage = NewInMemory();
        // Mouse() leaves the three settings properties at their defaults, which is exactly what an
        // ALTER-TABLE-added column yields for a pre-existing row.
        storage.AddDevice(LocalStorageService.OwnerKindUser, UserId, Mouse());

        var restored = Assert.Single(storage.GetDevices(LocalStorageService.OwnerKindUser, UserId));
        Assert.Equal(0, restored.SettingsVendorId);
        Assert.Equal(0, restored.SettingsProductId);
        Assert.Equal("", restored.SettingsDeviceKey);
    }
}
