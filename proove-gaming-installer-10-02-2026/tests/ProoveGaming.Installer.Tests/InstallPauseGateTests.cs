using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace ProoveGaming.Installer.Tests;

// Pins the deadlock-fix contract of InstallPauseGate (docs/plans/installer-cancel-deadlock/):
// unlike the ManualResetEventSlim it replaced, a single thread must be able to pause, wait and
// resume the gate by itself, and a cancelled token must release waiters even if Resume() never
// runs — the two properties MainWindow's UI-thread wait sites rely on.
public class InstallPauseGateTests
{
    // Every wait below is released by Resume() or cancellation; the timeout only turns a
    // regression (a wait that never completes) into a fast failure instead of a hung run.
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task WaitWhilePausedAsync_GateOpen_CompletesSynchronously()
    {
        var gate = new InstallPauseGate();

        var wait = gate.WaitWhilePausedAsync(CancellationToken.None);

        Assert.True(wait.IsCompletedSuccessfully);
        await wait;
    }

    // The deadlock repro: under the old blocking-wait semantics this single-threaded
    // pause, wait, resume sequence could never finish, because Wait() froze the only
    // thread able to open the gate.
    [Fact]
    public async Task WaitWhilePausedAsync_PausedThenResumedFromSameThread_Completes()
    {
        var gate = new InstallPauseGate();
        gate.Pause();

        var wait = gate.WaitWhilePausedAsync(CancellationToken.None);
        Assert.False(wait.IsCompleted);

        gate.Resume();

        await wait.WaitAsync(WaitTimeout);
    }

    [Fact]
    public async Task WaitWhilePausedAsync_CancelledWhilePaused_ThrowsOperationCanceled()
    {
        var gate = new InstallPauseGate();
        gate.Pause();
        using var cancellation = new CancellationTokenSource();
        var wait = gate.WaitWhilePausedAsync(cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(WaitTimeout));
    }

    [Fact]
    public async Task WaitWhilePausedAsync_TokenAlreadyCancelled_ThrowsImmediately()
    {
        var gate = new InstallPauseGate();
        gate.Pause();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var wait = gate.WaitWhilePausedAsync(cancellation.Token);

        // Already cancelled on return, without waiting for Resume() or a timer: the
        // returned task must be cancelled synchronously, not merely cancellable later.
        Assert.True(wait.IsCanceled);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
    }

    [Fact]
    public async Task Pause_IsIdempotent()
    {
        var gate = new InstallPauseGate();
        gate.Pause();
        var wait = gate.WaitWhilePausedAsync(CancellationToken.None);

        gate.Pause();

        Assert.False(wait.IsCompleted);
        gate.Resume();
        await wait.WaitAsync(WaitTimeout);
    }

    [Fact]
    public void Resume_WithoutPause_IsNoOp()
    {
        var gate = new InstallPauseGate();

        gate.Resume();

        Assert.True(gate.WaitWhilePausedAsync(CancellationToken.None).IsCompletedSuccessfully);
    }

    // Mirrors the real shape: the UI-thread continuation and the copy worker both park on the
    // same pause, and one "continue installation" click must release both.
    [Fact]
    public async Task Resume_ReleasesAllPendingWaiters()
    {
        var gate = new InstallPauseGate();
        gate.Pause();
        var uiWait = gate.WaitWhilePausedAsync(CancellationToken.None);
        var workerWait = gate.WaitWhilePausedAsync(CancellationToken.None);

        gate.Resume();

        await Task.WhenAll(uiWait, workerWait).WaitAsync(WaitTimeout);
    }

    [Fact]
    public async Task PauseAfterResume_BlocksNextWait()
    {
        var gate = new InstallPauseGate();
        gate.Pause();
        gate.Resume();

        gate.Pause();
        var wait = gate.WaitWhilePausedAsync(CancellationToken.None);

        Assert.False(wait.IsCompleted);
        gate.Resume();
        await wait.WaitAsync(WaitTimeout);
    }
}
