using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// The serialized storage executor's contract (see the comment block in LocalStorageService):
// strict FIFO, completion-representing tasks, pool-thread execution, fault isolation, and the
// drain barrier — plus the two ordering guarantees the rest of the plan leans on: migration
// runs off the calling thread and strictly after every guest write enqueued before it.
public class LocalStorageServiceExecutorTests
{
	private const int Vid = 0x04D9;
	private const int Pid = 0xA23F;
	private const string DeviceKey = "";

	[Fact]
	public async Task Items_RunInEnqueueOrder_AcrossActionFuncAndAsyncShapes()
	{
		using var store = new LocalStorageService("Data Source=:memory:");
		var order = new ConcurrentQueue<int>();

		var t1 = store.RunOnStorageAsync(() => order.Enqueue(1));
		var t2 = store.RunOnStorageAsync(() => { order.Enqueue(2); return 2; });
		var t3 = store.RunOnStorageAsync(async () =>
		{
			// The async item must hold its queue slot across its own await: item 4 may not
			// start until this delegate's INNER task has completed.
			order.Enqueue(3);
			await Task.Delay(20);
			order.Enqueue(-3);
		});
		var t4 = store.RunOnStorageAsync(() => order.Enqueue(4));

		await Task.WhenAll(t1, t2, t3, t4);

		Assert.Equal(new[] { 1, 2, 3, -3, 4 }, order.ToArray());
	}

	[Fact]
	public async Task ReturnedTask_CompletesOnlyAfterTheDelegateRan_NotOnEnqueue()
	{
		using var store = new LocalStorageService("Data Source=:memory:");
		using var gate = new ManualResetEventSlim(false);

		var blocked = store.RunOnStorageAsync(() => gate.Wait());
		var behind = store.RunOnStorageAsync(() => { });

		// Both are enqueued; neither may report completion while the first delegate blocks.
		await Task.Delay(50);
		Assert.False(blocked.IsCompleted);
		Assert.False(behind.IsCompleted);

		gate.Set();
		await behind;
		Assert.True(blocked.IsCompletedSuccessfully);
	}

	[Fact]
	public async Task Items_RunOffTheCallingThread()
	{
		using var store = new LocalStorageService("Data Source=:memory:");
		var callingThreadId = Environment.CurrentManagedThreadId;

		var ranOn = await store.RunOnStorageAsync(() => Environment.CurrentManagedThreadId);

		Assert.NotEqual(callingThreadId, ranOn);
	}

	[Fact]
	public async Task FaultedItem_FaultsItsOwnTask_AndLaterItemsStillRun()
	{
		using var store = new LocalStorageService("Data Source=:memory:");

		// Typed as Action explicitly: a throwing lambda is otherwise convertible to both the
		// Action and Func<Task> overloads.
		Action boom = () => throw new InvalidOperationException("boom");
		var faulted = store.RunOnStorageAsync(boom);
		var survived = store.RunOnStorageAsync(() => 42);

		Assert.Equal(42, await survived);
		await Assert.ThrowsAsync<InvalidOperationException>(() => faulted);
	}

	[Fact]
	public async Task DrainStorageQueue_IsABarrierOverEverythingEnqueuedSoFar()
	{
		using var store = new LocalStorageService("Data Source=:memory:");
		var ran = new List<int>();
		using var gate = new ManualResetEventSlim(false);

		_ = store.RunOnStorageAsync(() => { gate.Wait(); ran.Add(1); });
		_ = store.RunOnStorageAsync(() => ran.Add(2));
		var drain = store.DrainStorageQueueAsync();
		Assert.False(drain.IsCompleted);

		gate.Set();
		await drain;

		Assert.Equal(new[] { 1, 2 }, ran);
	}

	[Fact]
	public async Task DrainStorageQueue_DoesNotFault_WhenAnItemDid()
	{
		using var store = new LocalStorageService("Data Source=:memory:");

		Action boom = () => throw new InvalidOperationException("boom");
		var faulted = store.RunOnStorageAsync(boom);
		await store.DrainStorageQueueAsync();

		Assert.True(faulted.IsFaulted);
	}

	// The reproducing test for C5: the old MigrateGuestToUserAsync fired the storage seam and
	// entered the transaction synchronously on whatever thread awaited it — the UI thread in the
	// app. Same pre-await-window reasoning as DeviceDetailsSetDeviceThreadingTests.
	[Fact]
	public async Task MigrateGuestToUser_TouchesNoStorageOnTheCallingThread_BeforeTheFirstAwait()
	{
		using var store = new LocalStorageService("Data Source=:memory:");
		var guestSession = Guid.NewGuid();
		store.SetDeviceSetting(LocalStorageService.OwnerKindGuest, guestSession.ToString("D"),
			Vid, Pid, DeviceKey, "pollingRate.hz", 1000);

		var accesses = new ConcurrentBag<int>();
		store.OnBeforeStorageAccess = () => accesses.Add(Environment.CurrentManagedThreadId);
		var callingThreadId = Environment.CurrentManagedThreadId;

		var migration = store.MigrateGuestToUserAsync(guestSession, 42);
		var duringPrologue = accesses.ToArray();

		await migration;

		Assert.DoesNotContain(callingThreadId, duringPrologue);
		Assert.NotEmpty(accesses);
	}

	// The FIFO ordering that makes the migration safe without an explicit drain: a guest write
	// still sitting in the queue when sign-in starts must land BEFORE the rekey, so it is
	// visible under the user owner afterwards and no guest-owned orphan row survives.
	[Fact]
	public async Task GuestWriteEnqueuedBeforeMigration_LandsBeforeTheRekey()
	{
		using var store = new LocalStorageService("Data Source=:memory:");
		var guestSession = Guid.NewGuid();
		var guestId = guestSession.ToString("D");
		using var gate = new ManualResetEventSlim(false);

		// Hold the queue so the guest write is provably still pending when migration enqueues.
		_ = store.RunOnStorageAsync(() => gate.Wait());
		_ = store.RunOnStorageAsync(() => store.SetDeviceSetting(
			LocalStorageService.OwnerKindGuest, guestId, Vid, Pid, DeviceKey, "dpi.activeStage", 3));
		var migration = store.MigrateGuestToUserAsync(guestSession, 42);

		gate.Set();
		await migration;

		var userSettings = store.GetDeviceSettings(LocalStorageService.OwnerKindUser, "42", Vid, Pid, DeviceKey);
		Assert.Equal(3, userSettings["dpi.activeStage"]);
		var guestSettings = store.GetDeviceSettings(LocalStorageService.OwnerKindGuest, guestId, Vid, Pid, DeviceKey);
		Assert.Empty(guestSettings);
	}
}
