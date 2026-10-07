using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using ProoveGaming.App.Services;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// The SubscribeOnDevices stream lifecycle contract: when the DeviceHost dies mid-session the
// stream end/fault must surface to the reconnect loop so a new subscribe attempt follows
// within the retry delay, StopCommunication must end the loop promptly and quietly, and every
// duplex call must be disposed exactly once across repeated restarts.
// Plan: docs/plans/usbhid-stream-reconnect/usbhid-stream-reconnect-plan.md.
public class UsbHidDeviceServiceReconnectTests
{
	// Every await on the service races this timeout: the pre-fix code hangs on stream death,
	// so an unbounded await would deadlock the runner instead of failing the test.
	private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

	private abstract record Step;
	private sealed record Yield(SubscribeOnDevicesResponse Response) : Step;
	private sealed record End : Step;
	private sealed record Fault(Exception Error) : Step;
	private sealed record BlockUntilCancelled : Step;
	private sealed record BlockUntilCancelledThenRpcCancelled : Step;

	private sealed class ScriptedReader : IAsyncStreamReader<SubscribeOnDevicesResponse>
	{
		private readonly IReadOnlyList<Step> _script;
		private int _index = -1;

		public ScriptedReader(IReadOnlyList<Step> script) => _script = script;

		public SubscribeOnDevicesResponse Current { get; private set; } = new();

		public async Task<bool> MoveNext(CancellationToken cancellationToken)
		{
			var i = Interlocked.Increment(ref _index);
			var step = i < _script.Count ? _script[i] : new BlockUntilCancelled();
			switch (step)
			{
				case Yield yield:
					Current = yield.Response;
					return true;
				case End:
					return false;
				case Fault fault:
					throw fault.Error;
				case BlockUntilCancelledThenRpcCancelled:
					// Real gRPC most often surfaces a deliberate cancel as RpcException(Cancelled),
					// not OperationCanceledException - this step mirrors that failure mode.
					try
					{
						await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
					}
					catch (OperationCanceledException)
					{
						throw new RpcException(new Status(StatusCode.Cancelled, "call cancelled"));
					}
					return false;
				default:
					// Healthy idle stream: nothing arrives until the attempt token is cancelled.
					await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
					return false;
			}
		}
	}

	private sealed class FakeRequestWriter : IClientStreamWriter<SubscribeOnDevicesRequest>
	{
		private readonly Exception? _writeError;

		public FakeRequestWriter(Exception? writeError) => _writeError = writeError;

		public WriteOptions? WriteOptions { get; set; }

		public Task WriteAsync(SubscribeOnDevicesRequest message)
			=> _writeError == null ? Task.CompletedTask : Task.FromException(_writeError);

		public Task CompleteAsync() => Task.CompletedTask;
	}

	// One scripted SubscribeOnDevices attempt; the dispose action counts so tests can pin
	// "every call created is disposed exactly once".
	private sealed class ScriptedAttempt
	{
		private int _disposeCount;

		public ScriptedAttempt(IReadOnlyList<Step> script, Exception? writeError = null)
		{
			Call = new AsyncDuplexStreamingCall<SubscribeOnDevicesRequest, SubscribeOnDevicesResponse>(
				new FakeRequestWriter(writeError),
				new ScriptedReader(script),
				Task.FromResult(new Metadata()),
				() => Status.DefaultSuccess,
				() => new Metadata(),
				() => Interlocked.Increment(ref _disposeCount));
		}

		public AsyncDuplexStreamingCall<SubscribeOnDevicesRequest, SubscribeOnDevicesResponse> Call { get; }

		public int DisposeCount => Volatile.Read(ref _disposeCount);
	}

	private sealed class FakeDeviceClient : Device.DeviceClient
	{
		private readonly Func<int, ScriptedAttempt> _attemptFactory;
		private readonly List<ScriptedAttempt> _attempts = new();
		private int _attemptCount;

		public FakeDeviceClient(Func<int, ScriptedAttempt> attemptFactory) => _attemptFactory = attemptFactory;

		public int AttemptCount => Volatile.Read(ref _attemptCount);

		public IReadOnlyList<ScriptedAttempt> AttemptsSnapshot()
		{
			lock (_attempts)
				return _attempts.ToArray();
		}

		public override AsyncDuplexStreamingCall<SubscribeOnDevicesRequest, SubscribeOnDevicesResponse> SubscribeOnDevices(
			Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
		{
			var index = Interlocked.Increment(ref _attemptCount) - 1;
			var attempt = _attemptFactory(index);
			lock (_attempts)
				_attempts.Add(attempt);
			return attempt.Call;
		}
	}

	private static UsbHidDeviceService CreateService(FakeDeviceClient client, Func<CancellationToken, Task>? retryDelay = null)
		=> new(client) { RetryDelayAsync = retryDelay ?? (_ => Task.CompletedTask) };

	private static async Task WaitUntilAsync(Func<bool> condition, string because)
	{
		var deadline = DateTime.UtcNow + TestTimeout;
		while (!condition())
		{
			Assert.True(DateTime.UtcNow < deadline, because);
			await Task.Delay(10);
		}
	}

	private static async Task AwaitWithTimeout(Task task, string because)
	{
		var completed = await Task.WhenAny(task, Task.Delay(TestTimeout));
		Assert.True(completed == task, because);
		await task;
	}

	private static async Task<T> AwaitWithTimeout<T>(Task<T> task, string because)
	{
		var completed = await Task.WhenAny(task, Task.Delay(TestTimeout));
		Assert.True(completed == task, because);
		return await task;
	}

	private static async Task StopAndJoinAsync(UsbHidDeviceService service, Task startTask)
	{
		service.StopCommunication();
		await AwaitWithTimeout(startTask, "StartCommunication did not complete after StopCommunication");
	}

	// Repro for the bug (red on pre-fix code): the response stream ending must be followed by a
	// second SubscribeOnDevices call. Pre-fix, the keep-alive loop kept spinning and no second
	// call ever came.
	[Fact]
	public async Task StartCommunication_StreamEnds_Resubscribes()
	{
		var client = new FakeDeviceClient(index => index == 0
			? new ScriptedAttempt([new Yield(new SubscribeOnDevicesResponse()), new End()])
			: new ScriptedAttempt([new BlockUntilCancelled()]));
		var service = CreateService(client);

		var startTask = service.StartCommunication();
		try
		{
			await WaitUntilAsync(() => client.AttemptCount >= 2,
				"the stream ended but no second SubscribeOnDevices call was made - stream death is invisible to the reconnect loop");
		}
		finally
		{
			await StopAndJoinAsync(service, startTask);
		}
	}

	[Fact]
	public async Task StartCommunication_StreamFaults_ResubscribesAndRaisesNotifyErrorOnce()
	{
		var errors = new ConcurrentQueue<string>();
		var client = new FakeDeviceClient(index => index == 0
			? new ScriptedAttempt([new Fault(new RpcException(new Status(StatusCode.Unavailable, "host died")))])
			: new ScriptedAttempt([new BlockUntilCancelled()]));
		var service = CreateService(client);
		service.NotifyError += (_, message) => errors.Enqueue(message);

		var startTask = service.StartCommunication();
		try
		{
			await WaitUntilAsync(() => client.AttemptCount >= 2,
				"the stream faulted but no second SubscribeOnDevices call was made");
			Assert.Single(errors);
		}
		finally
		{
			await StopAndJoinAsync(service, startTask);
		}
	}

	[Fact]
	public async Task StartCommunication_ThreeStreamDeaths_ResubscribesEachTimeAndDisposesEveryCallOnce()
	{
		var client = new FakeDeviceClient(index => index < 3
			? new ScriptedAttempt([new End()])
			: new ScriptedAttempt([new BlockUntilCancelled()]));
		var service = CreateService(client);

		var startTask = service.StartCommunication();
		await WaitUntilAsync(() => client.AttemptCount >= 4,
			"three stream deaths must be followed by a fourth subscribe attempt");

		await StopAndJoinAsync(service, startTask);

		Assert.Equal(4, client.AttemptCount);
		// The last call unwinds asynchronously after StopCommunication, so poll for disposal.
		await WaitUntilAsync(
			() =>
			{
				var attempts = client.AttemptsSnapshot();
				if (attempts.Count != 4)
					return false;
				foreach (var attempt in attempts)
					if (attempt.DisposeCount != 1)
						return false;
				return true;
			},
			"every SubscribeOnDevices call must be disposed exactly once");
	}

	[Fact]
	public async Task StopCommunication_MidStream_CompletesStartQuietlyAndStopsSubscribing()
	{
		var errors = new ConcurrentQueue<string>();
		var client = new FakeDeviceClient(_ => new ScriptedAttempt([new BlockUntilCancelled()]));
		var service = CreateService(client);
		service.NotifyError += (_, message) => errors.Enqueue(message);

		var startTask = service.StartCommunication();
		await WaitUntilAsync(() => client.AttemptCount == 1, "the first subscribe attempt never happened");

		await StopAndJoinAsync(service, startTask);

		Assert.False(service.IsRunning);
		await WaitUntilAsync(() => client.AttemptsSnapshot()[0].DisposeCount == 1,
			"the in-flight call was not disposed after StopCommunication");
		Assert.Equal(1, client.AttemptCount);
		Assert.Empty(errors);
	}

	// Review findings Round 1, T1: the quiet-stop filter for RpcException(StatusCode.Cancelled)
	// under a cancelled token must stay silent, same as the OperationCanceledException path.
	[Fact]
	public async Task StopCommunication_MidStream_RpcCancelledSurface_CompletesStartQuietly()
	{
		var errors = new ConcurrentQueue<string>();
		var client = new FakeDeviceClient(_ => new ScriptedAttempt([new BlockUntilCancelledThenRpcCancelled()]));
		var service = CreateService(client);
		service.NotifyError += (_, message) => errors.Enqueue(message);

		var startTask = service.StartCommunication();
		await WaitUntilAsync(() => client.AttemptCount == 1, "the first subscribe attempt never happened");

		await StopAndJoinAsync(service, startTask);

		Assert.False(service.IsRunning);
		await WaitUntilAsync(() => client.AttemptsSnapshot()[0].DisposeCount == 1,
			"the in-flight call was not disposed after StopCommunication");
		// The catch filters run just after disposal; give the unwind a moment so a broken
		// filter reliably fails the assertion instead of racing it.
		await Task.Delay(50);
		Assert.Equal(1, client.AttemptCount);
		Assert.Empty(errors);
	}

	[Fact]
	public async Task StopCommunication_DuringRetryDelay_CompletesStartWithoutAnotherDelay()
	{
		var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var delayCalls = 0;
		Func<CancellationToken, Task> retryDelay = async token =>
		{
			Interlocked.Increment(ref delayCalls);
			delayEntered.TrySetResult();
			await Task.Delay(System.Threading.Timeout.Infinite, token);
		};
		// Host down: every subscribe attempt faults on the initial request write.
		var client = new FakeDeviceClient(_ => new ScriptedAttempt(
			[new End()],
			writeError: new RpcException(new Status(StatusCode.Unavailable, "host down"))));
		var service = CreateService(client, retryDelay);

		var startTask = service.StartCommunication();
		await AwaitWithTimeout(delayEntered.Task, "the retry-delay phase was never entered");

		var delayCallsBeforeStop = Volatile.Read(ref delayCalls);
		await StopAndJoinAsync(service, startTask);

		Assert.False(service.IsRunning);
		Assert.Equal(delayCallsBeforeStop, Volatile.Read(ref delayCalls));
	}

	[Fact]
	public async Task StartCommunication_AfterReconnect_DispatchesDpiStageChange()
	{
		var change = new DpiStageChange { VendorId = 1, ProductId = 2, DeviceKey = "unit-1", StageIndex = 3 };
		var raised = new TaskCompletionSource<DpiStageChange>(TaskCreationOptions.RunContinuationsAsynchronously);
		var client = new FakeDeviceClient(index => index == 0
			? new ScriptedAttempt([new End()])
			: new ScriptedAttempt([new Yield(new SubscribeOnDevicesResponse { DpiStageChange = change }), new BlockUntilCancelled()]));
		var service = CreateService(client);
		service.DpiStageChanged += (_, pushed) => raised.TrySetResult(pushed);

		var startTask = service.StartCommunication();
		try
		{
			var received = await AwaitWithTimeout(raised.Task,
				"a DpiStageChange pushed on the reconnected stream was never dispatched");
			Assert.Equal(3, received.StageIndex);
			Assert.True(service.TryGetLastDpiStage(1, 2, "unit-1", out var stageIndex));
			Assert.Equal(3, stageIndex);
		}
		finally
		{
			await StopAndJoinAsync(service, startTask);
		}
	}
}
