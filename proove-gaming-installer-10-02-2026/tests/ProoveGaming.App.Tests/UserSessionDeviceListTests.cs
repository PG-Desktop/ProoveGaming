using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// The guest half of docs/plans/fix-duplicate-device-cards (Round 1 findings S2 + N1). Until the fix
// pass this class had no tests at all, so nothing pinned the two properties HomeViewModel now leans
// on:
//
//   * UpsertDevice REPLACES the record carrying an id in place — same id, same slot — instead of the
//     old RemoveDevice-then-AddDevice pair, which minted a fresh id and moved the card to the tail.
//     Guest GetDevices() has no ORDER BY (rowid = Flush insertion order), so the old pair reshuffled
//     a guest's card list on the next restart every time a transport switch re-persisted a card.
//   * Every mutation and every snapshot goes through one gate. PersistDeviceCard is reached from the
//     gRPC discovery handler, i.e. off the UI thread, while FlushSession snapshots the same list;
//     an unsynchronised List<T> compacting under a concurrent ToArray can duplicate or drop an
//     element, which is a guest flush that writes a card twice or loses one.
//
// Each test notes what the pre-fix AddDevice/RemoveDevice pair did.
public class UserSessionDeviceListTests
{
    private static DeviceRecord Card(string name, string deviceKey = "", int id = 0) => new()
    {
        Id = id,
        Name = name,
        DeviceTypeLabel = "Wireless Mouse",
        CardType = "single",
        VendorId = 0x28E9,
        ProductId = 0x3252,
        DeviceKey = deviceKey
    };

    private static UserSession NewSession() => new(Guid.NewGuid());

    // ── UpsertDevice: replace in place ───────────────────────────────────────

    [Fact]
    public void UpsertDevice_OnAFreshRecord_AppendsAndMintsTheNextId()
    {
        var session = NewSession();

        var first = session.UpsertDevice(Card("PG Taipan"));
        var second = session.UpsertDevice(Card("RAMPAGE"));

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal(new[] { "PG Taipan", "RAMPAGE" }, session.SnapshotDevices().Select(d => d.Name).ToArray());
    }

    // N1, and the reason the re-persist is safe on the guest path: the card keeps the id it already
    // handed to DeviceCardViewModel.StorageId, so a later ⋮ delete still aims at the right record.
    // Pre-fix (RemoveDevice + AddDevice): the returned id was a brand new one.
    [Fact]
    public void UpsertDevice_WithAnExistingId_KeepsTheId()
    {
        var session = NewSession();
        var id = session.UpsertDevice(Card("RAMPAGE", "C1"));

        var returned = session.UpsertDevice(Card("RAMPAGE", deviceKey: "", id: id));

        Assert.Equal(id, returned);
        var stored = Assert.Single(session.SnapshotDevices());
        Assert.Equal(id, stored.Id);
        Assert.Equal(string.Empty, stored.DeviceKey); // the newly persisted transport won
    }

    // THE ORDERING FIX. A RAMPAGE re-persisted after a transport switch must stay where it was in
    // the guest list, because Flush rewrites the rows in list order and GetDevices() reads them back
    // by rowid.
    // Pre-fix: RemoveDevice compacted the list and AddDevice appended, so the middle card jumped to
    // the end and the user's cards silently reordered on the next launch.
    [Fact]
    public void UpsertDevice_WithAnExistingId_ReplacesInPlace_KeepingTheSlot()
    {
        var session = NewSession();
        session.UpsertDevice(Card("First"));
        var middleId = session.UpsertDevice(Card("RAMPAGE", "C1"));
        session.UpsertDevice(Card("Last"));

        session.UpsertDevice(Card("RAMPAGE", deviceKey: "", id: middleId));

        Assert.Equal(
            new[] { "First", "RAMPAGE", "Last" },
            session.SnapshotDevices().Select(d => d.Name).ToArray());
        Assert.Equal(3, session.SnapshotDevices().Length);
    }

    // The record handed in is the one stored — the upsert must not merge fields into the old
    // instance, or a card that lost a transport would keep the stale routing identity.
    [Fact]
    public void UpsertDevice_WithAnExistingId_StoresTheSuppliedInstance()
    {
        var session = NewSession();
        var id = session.UpsertDevice(Card("RAMPAGE", "C1"));
        var replacement = Card("RAMPAGE", deviceKey: "", id: id);

        session.UpsertDevice(replacement);

        Assert.Same(replacement, Assert.Single(session.SnapshotDevices()));
    }

    // N1's second half: the id sequence must not churn. Pre-fix every re-persist burned an id, so a
    // guest who switched transports a few times handed out ever-growing StorageIds for a two-card
    // list — harmless in itself, but it is what made the "same id, same slot" contract untestable.
    [Fact]
    public void UpsertDevice_RepeatedOnOneCard_DoesNotAdvanceTheIdSequence()
    {
        var session = NewSession();
        var id = session.UpsertDevice(Card("RAMPAGE", "C1"));

        for (var i = 0; i < 5; i++)
            session.UpsertDevice(Card("RAMPAGE", deviceKey: $"T{i}", id: id));

        // The next genuinely new card takes the id straight after the first one.
        Assert.Equal(id + 1, session.UpsertDevice(Card("PG Taipan")));
    }

    // The guarded case in the doc comment: an id that belongs to another owner's store (a card
    // carried over from a signed-in stretch) must not be honoured as a session id. HomeViewModel
    // zeroes StorageId on the guest transition, so this is belt-and-braces — but if that guard ever
    // moves, the record must be appended under a fresh id rather than silently keeping a foreign one.
    [Fact]
    public void UpsertDevice_WithAnIdThatIsNotInTheSession_AppendsUnderAFreshId()
    {
        var session = NewSession();
        session.UpsertDevice(Card("PG Taipan"));

        var returned = session.UpsertDevice(Card("RAMPAGE", id: 4242));

        Assert.Equal(2, returned);
        Assert.Equal(2, session.SnapshotDevices().Length);
        Assert.DoesNotContain(session.SnapshotDevices(), d => d.Id == 4242);
    }

    // ── Seeding from stored rows ─────────────────────────────────────────────

    // A restored guest session is rebuilt from its rows, and a card re-persisted right after the
    // restore must still land on its own record rather than appending a duplicate.
    [Fact]
    public void RestoredSession_UpsertsOntoTheRestoredRows_AndContinuesTheIdSequence()
    {
        var restored = new UserSession(Guid.NewGuid(), new List<DeviceRecord>
        {
            Card("PG Taipan", "A", id: 7),
            Card("RAMPAGE", "C1", id: 9)
        });

        restored.UpsertDevice(Card("RAMPAGE", deviceKey: "", id: 9));

        Assert.Equal(2, restored.SnapshotDevices().Length);
        Assert.Equal(new[] { "PG Taipan", "RAMPAGE" }, restored.SnapshotDevices().Select(d => d.Name).ToArray());
        Assert.Equal(10, restored.UpsertDevice(Card("New Card")));
    }

    // ── RemoveDevice ─────────────────────────────────────────────────────────

    [Fact]
    public void RemoveDevice_DropsOnlyTheMatchingRecord()
    {
        var session = NewSession();
        session.UpsertDevice(Card("First"));
        var doomed = session.UpsertDevice(Card("Doomed"));
        session.UpsertDevice(Card("Last"));

        session.RemoveDevice(doomed);

        Assert.Equal(new[] { "First", "Last" }, session.SnapshotDevices().Select(d => d.Name).ToArray());
    }

    [Fact]
    public void RemoveDevice_WithAnUnknownId_ChangesNothing()
    {
        var session = NewSession();
        session.UpsertDevice(Card("First"));

        session.RemoveDevice(999);

        Assert.Single(session.SnapshotDevices());
    }

    // ── SnapshotDevices ──────────────────────────────────────────────────────

    // FlushSession queues the snapshot and writes it later on the storage executor, so the array
    // must describe the list as of the call and never move under the queued write.
    [Fact]
    public void SnapshotDevices_IsStable_AgainstLaterMutations()
    {
        var session = NewSession();
        var id = session.UpsertDevice(Card("First"));

        var snapshot = session.SnapshotDevices();
        session.UpsertDevice(Card("Second"));
        session.RemoveDevice(id);

        Assert.Equal(new[] { "First" }, snapshot.Select(d => d.Name).ToArray());
    }

    [Fact]
    public void SnapshotDevices_ReturnsANewArrayEachCall()
    {
        var session = NewSession();
        session.UpsertDevice(Card("First"));

        Assert.NotSame(session.SnapshotDevices(), session.SnapshotDevices());
    }

    // Deliberately shallow: Flush reads the records' fields, and the cards' own view models are the
    // only writers of those fields. Pinned so a future "make it a deep copy" change is a decision
    // rather than an accident.
    [Fact]
    public void SnapshotDevices_SharesTheRecordInstances()
    {
        var session = NewSession();
        var record = Card("First");
        session.UpsertDevice(record);

        Assert.Same(record, Assert.Single(session.SnapshotDevices()));
    }

    // ── S2: the gate itself ──────────────────────────────────────────────────

    // The race the fix closes. PersistDeviceCard runs on the gRPC discovery thread while the UI
    // thread flushes, so appends and snapshots genuinely interleave.
    // Pre-fix (a bare List<DeviceRecord> mutated from both): concurrent Add compacts/reallocates the
    // backing array, which drops elements, writes nulls, or throws outright — and a ToArray crossing
    // that window yields a device list that is not any state the session was ever in.
    [Fact]
    public async Task ConcurrentUpsertsAndSnapshots_LoseNoRecordsAndTearNoSnapshot()
    {
        const int writers = 4;
        const int perWriter = 100;
        var session = NewSession();

        using var done = new CancellationTokenSource();

        var reader = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                var snapshot = session.SnapshotDevices();
                // A torn snapshot shows the same record twice — the compaction hazard in S2. A
                // dropped element shows up as a null slot, which dereferencing Id surfaces here.
                Assert.Equal(snapshot.Length, snapshot.Select(d => d.Id).Distinct().Count());
            }
        });

        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            for (var i = 0; i < perWriter; i++)
                session.UpsertDevice(Card($"Card {w}-{i}"));
        })));

        done.Cancel();
        await reader;

        var final = session.SnapshotDevices();
        Assert.Equal(writers * perWriter, final.Length);
        Assert.Equal(final.Length, final.Select(d => d.Id).Distinct().Count());
    }

    // The other half: replacing in place and removing, concurrently with the flush snapshot. An
    // in-place replace never shifts the list, so the invariant is stronger — every snapshot holds
    // exactly the cards that were never removed.
    [Fact]
    public async Task ConcurrentReplacementsAndRemovals_KeepEverySnapshotConsistent()
    {
        var session = NewSession();
        var keptIds = Enumerable.Range(0, 20).Select(i => session.UpsertDevice(Card($"Kept {i}"))).ToArray();
        var doomedIds = Enumerable.Range(0, 20).Select(i => session.UpsertDevice(Card($"Doomed {i}"))).ToArray();

        using var done = new CancellationTokenSource();

        var reader = Task.Run(() =>
        {
            while (!done.IsCancellationRequested)
            {
                var snapshot = session.SnapshotDevices();
                var ids = snapshot.Select(d => d.Id).ToArray();
                Assert.Equal(ids.Length, ids.Distinct().Count());
                // Nothing removes a kept card, so no interleaving may make one disappear.
                Assert.All(keptIds, id => Assert.Contains(id, ids));
            }
        });

        await Task.WhenAll(
            Task.Run(() =>
            {
                for (var round = 0; round < 100; round++)
                    foreach (var id in keptIds)
                        session.UpsertDevice(Card($"Kept {id} r{round}", id: id));
            }),
            Task.Run(() =>
            {
                foreach (var id in doomedIds)
                    session.RemoveDevice(id);
            }));

        done.Cancel();
        await reader;

        var final = session.SnapshotDevices();
        Assert.Equal(keptIds.Length, final.Length);
        Assert.Equal(keptIds.OrderBy(i => i), final.Select(d => d.Id).OrderBy(i => i));
    }
}
