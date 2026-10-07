using System;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// DeviceGate serialises writers that reach the same HID interface through DIFFERENT APIs: the
// device services use HidSharp's DevicePath, while the DeviceHost monitors use the path returned by
// SetupAPI. If those two strings normalise to different keys the gate still acquires, still
// releases, and still serialises nothing — a silent no-op that looks like protection. That failure
// mode is invisible at runtime, so it is pinned here.
public class DeviceGateTests
{
	// Both forms of the SAME TAIPAN vendor collection, as captured from hardware.
	private const string HidSharpPath =
		@"\\?\hid#vid_248a&pid_8373&mi_00&col04#7&27cd5bf0&0&0003#{4d1e55b2-f16f-11cf-88cb-001111000030}";
	private const string SetupApiPath =
		@"\\?\HID#VID_248A&PID_8373&MI_00&COL04#7&27CD5BF0&0&0003#{4D1E55B2-F16F-11CF-88CB-001111000030}";

	[Fact]
	public void Normalize_TreatsHidSharpAndSetupApiPathsAsTheSameDevice()
	{
		// These differ only in CASE, which the dictionary's comparer already handles. Asserted with
		// the same comparer the gate uses, so this pins the pairing end to end.
		Assert.Equal(
			DeviceGate.Normalize(HidSharpPath),
			DeviceGate.Normalize(SetupApiPath),
			StringComparer.OrdinalIgnoreCase);
	}

	// The variations that case-insensitivity does NOT cover, and which therefore actually exercise
	// Normalize. Each is a form the same interface can be spelled in depending on which API produced
	// it: with or without the \\?\ device prefix, and with or without the trailing interface-class
	// GUID (constant for HID, so it distinguishes nothing).
	[Theory]
	[InlineData(@"hid#vid_248a&pid_8373&mi_00&col04#7&27cd5bf0&0&0003#{4d1e55b2-f16f-11cf-88cb-001111000030}")]
	[InlineData(@"\\.\hid#vid_248a&pid_8373&mi_00&col04#7&27cd5bf0&0&0003#{4d1e55b2-f16f-11cf-88cb-001111000030}")]
	[InlineData(@"\\?\hid#vid_248a&pid_8373&mi_00&col04#7&27cd5bf0&0&0003")]
	[InlineData(@"hid#vid_248a&pid_8373&mi_00&col04#7&27cd5bf0&0&0003")]
	[InlineData(@"  \\?\hid#vid_248a&pid_8373&mi_00&col04#7&27cd5bf0&0&0003#{4d1e55b2-f16f-11cf-88cb-001111000030}  ")]
	public void Normalize_CollapsesPathSpellingsOntoOneKey(string variant)
	{
		Assert.Equal(
			DeviceGate.Normalize(HidSharpPath),
			DeviceGate.Normalize(variant),
			StringComparer.OrdinalIgnoreCase);
	}

	[Fact]
	public void Normalize_KeepsDistinctCollectionsApart()
	{
		// col04 is the vendor config collection; col01 is the mouse. Merging them would serialise
		// unrelated traffic — the opposite mistake, and a performance one rather than a safety one,
		// but still wrong.
		var col04 = DeviceGate.Normalize(HidSharpPath);
		var col01 = DeviceGate.Normalize(HidSharpPath.Replace("col04", "col01"));

		Assert.NotEqual(col04, col01, StringComparer.OrdinalIgnoreCase);
	}

	[Fact]
	public void Normalize_KeepsDistinctDevicesApart()
	{
		var dongle = DeviceGate.Normalize(HidSharpPath);
		var wired = DeviceGate.Normalize(HidSharpPath.Replace("pid_8373", "pid_fb01"));

		Assert.NotEqual(dongle, wired, StringComparer.OrdinalIgnoreCase);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void Normalize_HandlesMissingPaths(string? path)
	{
		Assert.Equal(string.Empty, DeviceGate.Normalize(path));
	}

	[Fact]
	public async Task AcquireAsync_SerialisesTwoCallersOnTheSamePath()
	{
		var path = $@"\\?\hid#test_serialise_{Guid.NewGuid():N}#{{4d1e55b2-f16f-11cf-88cb-001111000030}}";

		using var first = await DeviceGate.AcquireAsync(path);

		var secondAcquired = false;
		var second = Task.Run(async () =>
		{
			using var held = await DeviceGate.AcquireAsync(path);
			secondAcquired = true;
		});

		// The second caller must still be waiting while the first holds the gate.
		var finishedEarly = await Task.WhenAny(second, Task.Delay(200)) == second;
		Assert.False(finishedEarly);
		Assert.False(secondAcquired);

		first.Dispose();

		await second.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.True(secondAcquired);
	}

	[Fact]
	public async Task AcquireAsync_DoesNotBlockDifferentPaths()
	{
		var a = $@"\\?\hid#test_a_{Guid.NewGuid():N}#{{4d1e55b2-f16f-11cf-88cb-001111000030}}";
		var b = $@"\\?\hid#test_b_{Guid.NewGuid():N}#{{4d1e55b2-f16f-11cf-88cb-001111000030}}";

		using var heldA = await DeviceGate.AcquireAsync(a);

		// Must not deadlock or time out: a gate that serialises everything would stall the whole
		// DeviceHost whenever any one device was being written to.
		using var heldB = await DeviceGate.AcquireAsync(b).WaitAsync(TimeSpan.FromSeconds(5));
	}

	[Fact]
	public async Task Waiting_IsCancellable()
	{
		var path = NewPath();
		using var held = await DeviceGate.AcquireAsync(path);

		using var cts = new CancellationTokenSource();
		var waiting = DeviceGate.AcquireAsync(path, cts.Token);
		cts.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
	}

	[Fact]
	public async Task ACancelledWaiter_DoesNotConsumeTheGate()
	{
		var path = NewPath();
		using var cts = new CancellationTokenSource();

		var held = await DeviceGate.AcquireAsync(path);
		var cancelled = DeviceGate.AcquireAsync(path, cts.Token);

		cts.Cancel();
		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

		held.Dispose();

		// The cancelled waiter must not have taken the permit on its way out — if it had, the gate
		// would be permanently closed and every later write to this device would hang.
		var next = DeviceGate.AcquireAsync(path);
		var entered = await Task.WhenAny(next, Task.Delay(500));
		Assert.Same(next, entered);
		(await next).Dispose();
	}

	// Serialisation under contention: every caller must observe the critical section empty on entry.
	[Fact]
	public async Task ConcurrentCallers_NeverOverlap()
	{
		var path = NewPath();
		int inside = 0;
		int maxObserved = 0;

		await Task.WhenAll(Enumerable.Range(0, 24).Select(async _ =>
		{
			using var gate = await DeviceGate.AcquireAsync(path);
			var now = Interlocked.Increment(ref inside);
			InterlockedMax(ref maxObserved, now);
			await Task.Delay(5);
			Interlocked.Decrement(ref inside);
		}));

		Assert.Equal(1, maxObserved);
	}

	private static void InterlockedMax(ref int target, int value)
	{
		int seen;
		while (value > (seen = Volatile.Read(ref target)))
		{
			if (Interlocked.CompareExchange(ref target, value, seen) == seen) return;
		}
	}

	private static string NewPath() =>
		$@"\\?\hid#test_{Guid.NewGuid():N}#{{4d1e55b2-f16f-11cf-88cb-001111000030}}";

	// TryAcquire is the battery poll's entry: it must YIELD to a held gate (a config transfer in
	// progress) rather than queue behind it, and it must still take a free gate normally.
	[Fact]
	public async Task TryAcquire_YieldsWhenThePathIsBusy()
	{
		var path = NewPath();
		using var held = await DeviceGate.AcquireAsync(path);

		using var attempt = DeviceGate.TryAcquire(path, TimeSpan.FromMilliseconds(100));

		Assert.Null(attempt);
	}

	[Fact]
	public async Task TryAcquire_TakesAFreeGate_AndItsDisposeReleases()
	{
		var path = NewPath();

		var taken = DeviceGate.TryAcquire(path, TimeSpan.Zero);
		Assert.NotNull(taken);

		// While TryAcquire holds the gate, an async waiter must queue — proving it took the SAME
		// permit AcquireAsync uses, not a parallel one.
		var waiter = DeviceGate.AcquireAsync(path);
		var finishedEarly = await Task.WhenAny(waiter, Task.Delay(200)) == waiter;
		Assert.False(finishedEarly);

		taken!.Dispose();
		(await waiter.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
	}

	[Fact]
	public async Task Releaser_DoubleDispose_DoesNotHandBackAnExtraPermit()
	{
		var path = $@"\\?\hid#test_double_{Guid.NewGuid():N}#{{4d1e55b2-f16f-11cf-88cb-001111000030}}";

		var held = await DeviceGate.AcquireAsync(path);
		held.Dispose();
		held.Dispose(); // must be a no-op, not a second Release

		// If the double dispose had leaked a permit, two callers could hold the gate at once.
		using var first = await DeviceGate.AcquireAsync(path);

		var second = DeviceGate.AcquireAsync(path);
		var finishedEarly = await Task.WhenAny(second, Task.Delay(200)) == second;

		Assert.False(finishedEarly);

		first.Dispose();
		(await second).Dispose();
	}

	// ── TryAcquireAsync ───────────────────────────────────────────────────────────────────────
	//
	// Added 2026-09-02 for the four keyboard RGB services, which took the unbounded AcquireAsync with
	// no CancellationToken. That wait cannot be abandoned: a holder that wedges — a device that stops
	// answering mid-transfer — queued every later LED write for the life of the process, and those
	// waiters outlived service shutdown because nothing could cancel them (2026-08-21 audit, C8).
	//
	// The services themselves cannot be tested: they take a HidSharp HidDevice, which has no public
	// constructor, so every RGB test in this suite pins the static packet builders instead. This
	// covers the primitive they now call.

	[Fact]
	public async Task TryAcquireAsync_YieldsWhenThePathStaysBusy()
	{
		var path = NewPath();
		using var held = await DeviceGate.AcquireAsync(path);

		using var attempt = await DeviceGate.TryAcquireAsync(path, TimeSpan.FromMilliseconds(100));

		Assert.Null(attempt);
	}

	[Fact]
	public async Task TryAcquireAsync_TakesAFreeGate_AndItsDisposeReleases()
	{
		var path = NewPath();

		var taken = await DeviceGate.TryAcquireAsync(path, TimeSpan.FromSeconds(5));
		Assert.NotNull(taken);

		taken!.Dispose();

		// Releasing must actually hand the permit back, or the first LED write to a device would be
		// the last one that ever succeeded on it.
		using var next = await DeviceGate.TryAcquireAsync(path, TimeSpan.FromSeconds(5));
		Assert.NotNull(next);
	}

	// It must WAIT for the timeout, not fail fast. An LED write has to be able to sit behind an
	// ordinary key-table transfer; one that gave up instantly would look broken every time the user
	// changed a colour while anything else was talking to the keyboard.
	[Fact]
	public async Task TryAcquireAsync_WaitsForAGateThatIsReleasedInTime()
	{
		var path = NewPath();
		var held = await DeviceGate.AcquireAsync(path);

		var waiter = DeviceGate.TryAcquireAsync(path, TimeSpan.FromSeconds(5));

		// Still waiting while the gate is held.
		Assert.NotSame(waiter, await Task.WhenAny(waiter, Task.Delay(150)));

		held.Dispose();

		var taken = await waiter.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.NotNull(taken);
		taken!.Dispose();
	}

	// The critical one. A timed-out waiter that took the permit on its way out would close the gate
	// permanently — every later write to that device would hang, which is a worse outcome than the
	// pile-up the timeout was added to prevent.
	[Fact]
	public async Task TryAcquireAsync_ATimedOutWaiterDoesNotConsumeTheGate()
	{
		var path = NewPath();
		var held = await DeviceGate.AcquireAsync(path);

		var timedOut = await DeviceGate.TryAcquireAsync(path, TimeSpan.FromMilliseconds(100));
		Assert.Null(timedOut);

		held.Dispose();

		var next = DeviceGate.AcquireAsync(path);
		Assert.Same(next, await Task.WhenAny(next, Task.Delay(500)));
		(await next).Dispose();
	}

	[Fact]
	public async Task TryAcquireAsync_IsCancellable()
	{
		var path = NewPath();
		using var held = await DeviceGate.AcquireAsync(path);

		using var cts = new CancellationTokenSource();
		var waiting = DeviceGate.TryAcquireAsync(path, TimeSpan.FromSeconds(30), cts.Token);
		cts.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
	}

	[Fact]
	public async Task TryAcquireAsync_StillSerialisesTwoCallers()
	{
		var path = NewPath();

		using var first = await DeviceGate.TryAcquireAsync(path, TimeSpan.FromSeconds(5));
		Assert.NotNull(first);

		// A timed acquire is still an acquire: it must exclude the next caller exactly as
		// AcquireAsync does, or the gate would protect nothing for the services that use it.
		var second = DeviceGate.TryAcquireAsync(path, TimeSpan.FromMilliseconds(100));
		Assert.Null(await second);
	}
}
