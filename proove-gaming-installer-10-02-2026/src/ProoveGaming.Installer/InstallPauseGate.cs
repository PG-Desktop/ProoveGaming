namespace ProoveGaming.Installer;

// Async-awaitable pause gate for the install pipeline, replacing a blocking
// ManualResetEventSlim.Wait. The old gate could only be opened from a UI-thread click handler,
// so a UI-thread continuation waiting on it froze the very thread able to release it — a
// permanent deadlock when the Cancel modal opened after the last per-file gate check. Every
// wait here is an await: the UI thread returns to the dispatcher and the modal's buttons stay
// live. RunContinuationsAsynchronously keeps Resume() and cancellation from inline-running
// install continuations inside a button click handler.
internal sealed class InstallPauseGate
{
	private readonly Lock _lock = new();
	private TaskCompletionSource? _pauseSource;

	// Idempotent: reopening the modal while already paused keeps the existing waiters parked.
	public void Pause()
	{
		lock (_lock)
		{
			_pauseSource ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		}
	}

	// Idempotent: resuming an open gate is a no-op.
	public void Resume()
	{
		TaskCompletionSource? pauseSource;
		lock (_lock)
		{
			pauseSource = _pauseSource;
			_pauseSource = null;
		}

		pauseSource?.TrySetResult();
	}

	// Completes immediately while the gate is open; otherwise completes when Resume() runs or
	// throws OperationCanceledException on cancellation, so "confirm cancel" releases waiters
	// even if Resume() is never called.
	public Task WaitWhilePausedAsync(CancellationToken cancellationToken)
	{
		Task? pauseTask;
		lock (_lock)
		{
			pauseTask = _pauseSource?.Task;
		}

		return pauseTask is null ? Task.CompletedTask : pauseTask.WaitAsync(cancellationToken);
	}
}
