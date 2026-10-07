using Xunit;

// Avalonia's Dispatcher.UIThread is a process-wide singleton with hard thread affinity
// established by whichever thread touches it first. xUnit's default per-class parallelization
// runs different test classes on different threads, so any test that posts to (and drains via
// Dispatcher.UIThread.RunJobs()) the dispatcher — e.g. DeviceDetailsPresetApplyTests' physical
// DPI/polling-button tests — can race a different class for ownership and throw. The full suite
// still runs in about a second sequentially, so disabling parallelization here is the safe
// default rather than hand-picking which test classes are dispatcher-safe.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
