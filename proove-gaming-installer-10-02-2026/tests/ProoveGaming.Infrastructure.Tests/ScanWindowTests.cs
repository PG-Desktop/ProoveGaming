using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Infrastructure.Tests;

// The duty-cycle guard of the LE scan window.
//
// The scan used to run continuously until every tracked device reconnected — which can be forever
// (a device left powered off, or a stale ghost pairing that can never reconnect). While any scan
// runs, Windows' Device Association Service accumulates state for every rotating random address
// in radio range and never returns it: a tester machine reached ~8.8 GB of DAS memory. The fix
// bounds each scan to a window whose close is scheduled at start; this rule decides whether a
// scheduled close may actually stop the scan. Getting it wrong does not throw — a close that runs
// when it should not silently kills a window opened later (reconnection goes dark until the next
// sweep), and one that never runs silently restores the unbounded scan. Nothing else in the suite
// would notice either. See docs/plans/fix-das-memory-growth/.
public class ScanWindowTests
{
	[Fact]
	public void MatchingGenerationOfAStartedWatcher_Closes()
	{
		// The golden path: the window this close was scheduled for is still the current one and
		// the watcher is still scanning — the window has run its course.
		Assert.True(BluetoothDeviceDiscoveryService.ShouldCloseScanWindow(
			scheduledGeneration: 3, currentGeneration: 3, watcherStarted: true));
	}

	[Fact]
	public void StaleGeneration_DoesNotClose()
	{
		// The race the generation exists for: the scan stopped (reconnect) and a NEW window was
		// opened before this close fired. Its generation is stale — stopping now would kill the
		// new window ScanWindow-early and leave reconnection dark until the next sweep.
		Assert.False(BluetoothDeviceDiscoveryService.ShouldCloseScanWindow(
			scheduledGeneration: 3, currentGeneration: 4, watcherStarted: true));
	}

	[Fact]
	public void WatcherNoLongerStarted_DoesNotClose()
	{
		// Already stopped by a reconnect or torn down by shutdown — nothing left to close, and
		// calling Stop() on a non-started watcher is exactly the churn the status guard forbids.
		Assert.False(BluetoothDeviceDiscoveryService.ShouldCloseScanWindow(
			scheduledGeneration: 3, currentGeneration: 3, watcherStarted: false));
	}
}
