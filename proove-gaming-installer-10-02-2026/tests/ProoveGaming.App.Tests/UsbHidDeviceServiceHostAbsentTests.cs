using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using ProoveGaming.App.Services;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// What the App does when the DeviceHost is not there -- stopped, crashed, or its Windows service
// uninstalled outright. Nothing on the gRPC surface may throw out of the service: the App is
// expected to sit in the tray for days, and the host is a separate process that the user, an
// installer, or Windows Update can stop at any moment.
//
// A dead host produces RpcException(Unavailable) on EVERY call, so that is what the fake client
// raises. The contract each method advertises is its documented "no answer" value -- false for a
// write, -1 for a battery level, null for a read that must not be mistaken for an empty result --
// never an exception, because the call sites read a thrown exception as an aborted operation.
//
// Note what is deliberately NOT asserted here: nothing user-facing. Losing the host raises no
// toast, dialog or card message today, by design -- NotifyError is logged and nothing more. These
// tests pin that the App keeps working and keeps retrying, not that it announces anything.
//
// Two calls were genuinely unguarded before this suite existed:
//
//   * GetDevices threw. It is called from UsbHidDeviceService_DeviceFound, which does catch, so it
//     never crashed the app -- it aborted one Bluetooth card update, invisibly.
//   * GetDevices also carried NO DEADLINE, which is the more serious half. GrpcChannel disables the
//     underlying HttpClient timeout and expects per-call deadlines, so against a host that is
//     unreachable rather than cleanly refused this blocking call waits forever -- on the thread
//     holding HomeViewModel's _eventGate, stalling every later device event behind it. A stopped
//     service refuses the connection and fails fast, which is why the hang never showed up in
//     testing; a service mid-start or a half-open localhost connection does not.
//   * GetBatteryLevelAsync threw. Both of its call sites happen to wrap it, so the bug was latent,
//     but the interface documents -1 and a third caller would have inherited the exception.
public class UsbHidDeviceServiceHostAbsentTests
{
	private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

	// Every unary RPC answers the way a stopped or uninstalled DeviceHost does, and records the
	// CallOptions it was handed so the deadline can be asserted. The generated client routes the
	// (request, headers, deadline, token) overload through the (request, CallOptions) one, so
	// overriding only the latter covers both.
	private sealed class DeadHostDeviceClient : Device.DeviceClient
	{
		private readonly ConcurrentQueue<(string Method, DateTime? Deadline)> _calls = new();

		public IReadOnlyList<(string Method, DateTime? Deadline)> Calls => _calls.ToArray();

		public bool SubscribeThrows { get; init; }

		// The subscribe write succeeds. Worth modelling because a real HTTP/2 client-stream write is
		// buffered and can complete before the server has acknowledged anything, so a host that is
		// already gone can still let SubscribeOnDevices and WriteAsync through and fail only at the
		// first read.
		public bool WriteSucceeds { get; init; }

		// The response stream stays open instead of faulting -- a healthy subscription, which is
		// what a deliberate StopCommunication has to be told apart from.
		public bool StreamStaysOpen { get; init; }

		// How long the client half takes to close. AwaitServerStreaming awaits CompleteAsync in its
		// inner finally, so this holds a cancelled attempt short of its OUTER finally for a known
		// interval -- which is what makes the abandoned-attempt race deterministic instead of a
		// coin flip. See StopThenStart_DoesNotBlankTheCards.
		public TimeSpan CompleteDelay { get; init; }

		public int SubscribeAttempts;

		private void Record(string method, CallOptions options) => _calls.Enqueue((method, options.Deadline));

		private static RpcException HostUnreachable() => new(new Status(
			StatusCode.Unavailable,
			"failed to connect to all addresses; last error: connection refused"));

		private static AsyncUnaryCall<T> Faulted<T>() => new(
			Task.FromException<T>(HostUnreachable()),
			Task.FromResult(new Metadata()),
			() => new Status(StatusCode.Unavailable, "unavailable"),
			() => new Metadata(),
			() => { });

		public override BatchDevicesResponse GetDevices(GetDevicesRequest request, CallOptions options)
		{
			Record("GetDevices", options);
			throw HostUnreachable();
		}

		public override AsyncUnaryCall<BatchDevicesResponse> GetDevicesAsync(GetDevicesRequest request, CallOptions options)
		{
			Record("GetDevices", options);
			return Faulted<BatchDevicesResponse>();
		}

		public override GetBatteryLevelResponse GetBatteryLevel(GetBatteryLevelRequest request, CallOptions options)
		{
			Record("GetBatteryLevel", options);
			throw HostUnreachable();
		}

		public override AsyncUnaryCall<GetBatteryLevelResponse> GetBatteryLevelAsync(GetBatteryLevelRequest request, CallOptions options)
		{
			Record("GetBatteryLevel", options);
			return Faulted<GetBatteryLevelResponse>();
		}

		public override GetKeyActuationResponse GetKeyActuation(GetKeyActuationRequest request, CallOptions options)
		{
			Record("GetKeyActuation", options);
			throw HostUnreachable();
		}

		public override AsyncUnaryCall<GetKeyActuationResponse> GetKeyActuationAsync(GetKeyActuationRequest request, CallOptions options)
		{
			Record("GetKeyActuation", options);
			return Faulted<GetKeyActuationResponse>();
		}

		public override ProbeKeyMatrixResponse ProbeKeyMatrix(ProbeKeyMatrixRequest request, CallOptions options)
		{
			Record("ProbeKeyMatrix", options);
			throw HostUnreachable();
		}

		public override AsyncUnaryCall<ProbeKeyMatrixResponse> ProbeKeyMatrixAsync(ProbeKeyMatrixRequest request, CallOptions options)
		{
			Record("ProbeKeyMatrix", options);
			return Faulted<ProbeKeyMatrixResponse>();
		}

		public override ReadKeyMatrixResponse ReadKeyMatrix(ReadKeyMatrixRequest request, CallOptions options)
		{
			Record("ReadKeyMatrix", options);
			throw HostUnreachable();
		}

		public override AsyncUnaryCall<ReadKeyMatrixResponse> ReadKeyMatrixAsync(ReadKeyMatrixRequest request, CallOptions options)
		{
			Record("ReadKeyMatrix", options);
			return Faulted<ReadKeyMatrixResponse>();
		}

		public override ApplyDeviceConfigurationResponse ApplyDeviceConfiguration(ApplyDeviceConfigurationRequest request, CallOptions options)
		{
			Record("ApplyDeviceConfiguration", options);
			throw HostUnreachable();
		}

		public override AsyncUnaryCall<ApplyDeviceConfigurationResponse> ApplyDeviceConfigurationAsync(ApplyDeviceConfigurationRequest request, CallOptions options)
		{
			Record("ApplyDeviceConfiguration", options);
			return Faulted<ApplyDeviceConfigurationResponse>();
		}

		public override SetActiveDpiStageResponse SetActiveDpiStage(SetActiveDpiStageRequest request, CallOptions options)
		{
			Record("SetActiveDpiStage", options);
			throw HostUnreachable();
		}

		public override AsyncUnaryCall<SetActiveDpiStageResponse> SetActiveDpiStageAsync(SetActiveDpiStageRequest request, CallOptions options)
		{
			Record("SetActiveDpiStage", options);
			return Faulted<SetActiveDpiStageResponse>();
		}

		public override SetButtonBindingsResponse SetButtonBindings(SetButtonBindingsRequest request, CallOptions options)
		{
			Record("SetButtonBindings", options);
			throw HostUnreachable();
		}

		public override AsyncUnaryCall<SetButtonBindingsResponse> SetButtonBindingsAsync(SetButtonBindingsRequest request, CallOptions options)
		{
			Record("SetButtonBindings", options);
			return Faulted<SetButtonBindingsResponse>();
		}

		public override SetDeviceLedColorResponse SetDeviceLedColor(SetDeviceLedColorRequest request, CallOptions options)
		{
			Record("SetDeviceLedColor", options);
			throw HostUnreachable();
		}

		public override AsyncUnaryCall<SetDeviceLedColorResponse> SetDeviceLedColorAsync(SetDeviceLedColorRequest request, CallOptions options)
		{
			Record("SetDeviceLedColor", options);
			return Faulted<SetDeviceLedColorResponse>();
		}

		public override SetDpiStagesResponse SetDpiStages(SetDpiStagesRequest request, CallOptions options)
		{
			Record("SetDpiStages", options);
			throw HostUnreachable();
		}

		public override AsyncUnaryCall<SetDpiStagesResponse> SetDpiStagesAsync(SetDpiStagesRequest request, CallOptions options)
		{
			Record("SetDpiStages", options);
			return Faulted<SetDpiStagesResponse>();
		}

		public override SetKeyActuationResponse SetKeyActuation(SetKeyActuationRequest request, CallOptions options)
		{
			Record("SetKeyActuation", options);
			throw HostUnreachable();
		}

		public override AsyncUnaryCall<SetKeyActuationResponse> SetKeyActuationAsync(SetKeyActuationRequest request, CallOptions options)
		{
			Record("SetKeyActuation", options);
			return Faulted<SetKeyActuationResponse>();
		}

		public override SetKeyMappingResponse SetKeyMapping(SetKeyMappingRequest request, CallOptions options)
		{
			Record("SetKeyMapping", options);
			throw HostUnreachable();
		}

		public override AsyncUnaryCall<SetKeyMappingResponse> SetKeyMappingAsync(SetKeyMappingRequest request, CallOptions options)
		{
			Record("SetKeyMapping", options);
			return Faulted<SetKeyMappingResponse>();
		}

		public override SetMacrosResponse SetMacros(SetMacrosRequest request, CallOptions options)
		{
			Record("SetMacros", options);
			throw HostUnreachable();
		}

		public override AsyncUnaryCall<SetMacrosResponse> SetMacrosAsync(SetMacrosRequest request, CallOptions options)
		{
			Record("SetMacros", options);
			return Faulted<SetMacrosResponse>();
		}

		public override SetPerKeyActuationResponse SetPerKeyActuation(SetPerKeyActuationRequest request, CallOptions options)
		{
			Record("SetPerKeyActuation", options);
			throw HostUnreachable();
		}

		public override AsyncUnaryCall<SetPerKeyActuationResponse> SetPerKeyActuationAsync(SetPerKeyActuationRequest request, CallOptions options)
		{
			Record("SetPerKeyActuation", options);
			return Faulted<SetPerKeyActuationResponse>();
		}

		public override SetReportRateResponse SetReportRate(SetReportRateRequest request, CallOptions options)
		{
			Record("SetReportRate", options);
			throw HostUnreachable();
		}

		public override AsyncUnaryCall<SetReportRateResponse> SetReportRateAsync(SetReportRateRequest request, CallOptions options)
		{
			Record("SetReportRate", options);
			return Faulted<SetReportRateResponse>();
		}

		// The stream half. A host that is gone makes the very first SubscribeOnDevices throw, before
		// any reader exists -- the one path that is not a faulted task.
		public override AsyncDuplexStreamingCall<SubscribeOnDevicesRequest, SubscribeOnDevicesResponse> SubscribeOnDevices(
			Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default)
		{
			Interlocked.Increment(ref SubscribeAttempts);
			if (SubscribeThrows) throw HostUnreachable();
			return new AsyncDuplexStreamingCall<SubscribeOnDevicesRequest, SubscribeOnDevicesResponse>(
				new DeadWriter(WriteSucceeds, CompleteDelay),
				new DeadReader(StreamStaysOpen),
				Task.FromResult(new Metadata()),
				() => new Status(StatusCode.Unavailable, "unavailable"),
				() => new Metadata(),
				() => { });
		}

		private sealed class DeadWriter(bool succeeds, TimeSpan completeDelay) : IClientStreamWriter<SubscribeOnDevicesRequest>
		{
			public WriteOptions? WriteOptions { get; set; }

			public Task WriteAsync(SubscribeOnDevicesRequest message) =>
				succeeds ? Task.CompletedTask : Task.FromException(HostUnreachable());

			public async Task CompleteAsync()
			{
				if (completeDelay > TimeSpan.Zero) await Task.Delay(completeDelay);
				if (!succeeds) throw HostUnreachable();
			}
		}

		private sealed class DeadReader(bool staysOpen) : IAsyncStreamReader<SubscribeOnDevicesResponse>
		{
			public SubscribeOnDevicesResponse Current => new();

			public async Task<bool> MoveNext(CancellationToken cancellationToken)
			{
				if (!staysOpen) throw HostUnreachable();
				// A healthy but idle subscription: nothing arrives until the attempt is cancelled.
				await Task.Delay(Timeout.Infinite, cancellationToken);
				return false;
			}
		}
	}

	// Task.Delay(1) rather than Task.CompletedTask, which is what the other suites use. Against this
	// client EVERY failure path completes synchronously -- SubscribeOnDevices throws outright, and
	// the faulted tasks the stream returns are already faulted when awaited -- so with an
	// already-completed retry delay the whole reconnect loop runs without ever yielding. It then
	// re-reads the plain (non-volatile) _reconnecting field in a tight loop and never observes
	// StopCommunication's write, spinning a core until the test host is killed. Production is not
	// exposed to this: the real delay is Task.Delay(3000), which always yields.
	private static UsbHidDeviceService CreateService(DeadHostDeviceClient client) =>
		new(client) { RetryDelayAsync = _ => Task.Delay(1) };

	// ---- The two that used to throw -----------------------------------------------------------

	[Fact]
	public void GetDevices_HostUnreachable_ReturnsEmptyInsteadOfThrowing()
	{
		var service = CreateService(new DeadHostDeviceClient());

		var devices = service.GetDevices();

		Assert.Empty(devices);
	}

	// The hang fix, and the reason this is a test rather than a comment: without a deadline the
	// blocking call never returns against an unreachable host, because GrpcChannel turns the
	// HttpClient timeout off. Asserting the deadline is PRESENT is the only way to pin that from a
	// unit test -- reproducing the infinite wait would hang the runner, which is the bug itself.
	[Fact]
	public void GetDevices_AlwaysCarriesADeadline()
	{
		var client = new DeadHostDeviceClient();
		var service = CreateService(client);

		service.GetDevices();

		var call = Assert.Single(client.Calls);
		Assert.Equal("GetDevices", call.Method);
		Assert.NotNull(call.Deadline);
	}

	[Fact]
	public async Task GetBatteryLevel_HostUnreachable_ReportsNoReadingInsteadOfThrowing()
	{
		var service = CreateService(new DeadHostDeviceClient());

		var (battery, isCharging) = await service.GetBatteryLevelAsync(1234, 5678, "key");

		Assert.Equal(-1, battery);
		Assert.False(isCharging);
	}

	// ---- The whole surface --------------------------------------------------------------------

	// The sweep. Every member of IUsbHidDeviceService against a dead host in one test, because the
	// property being pinned is "none of them throws" -- a per-method test would say the same thing
	// seventeen times and still miss the eighteenth when someone adds it.
	[Fact]
	public async Task EveryCallSurvivesAnAbsentHost()
	{
		IUsbHidDeviceService service = CreateService(new DeadHostDeviceClient());

		Assert.Empty(service.GetDevices());
		Assert.False(service.CheckIfAllDevicesAreDisconnected());
		Assert.False(service.TryGetLastDpiStage(1, 2, "k", out _));
		Assert.False(service.TryGetLastReportRate(1, 2, "k", out _));

		Assert.Equal((-1, false), await service.GetBatteryLevelAsync(1, 2, "k"));

		// Writes report failure, so the caller can tell that nothing was applied.
		Assert.False(await service.SetDeviceLedColorAsync(1, 2, "k", "Mouse", 1, 2, 3, 0, 100, 5));
		Assert.False(await service.SetDpiStagesAsync(1, 2, "k", [(800, 255, 0, 0)]));
		Assert.False(await service.SetActiveDpiStageAsync(1, 2, "k", 0));
		Assert.False(await service.SetReportRateAsync(1, 2, "k", 1000));
		Assert.False(await service.SetButtonBindingsAsync(1, 2, "k",
			[(MouseButton.ButtonLeft, MouseButtonAction.LeftClick, null)]));
		Assert.False(await service.ApplyDeviceConfigurationAsync(
			1, 2, "k", (1, 2, 3, 0, 100, 5), null, null, null, null, null));
		Assert.False(await service.SetMacrosAsync(1, 2, "k", []));
		Assert.False(await service.SetKeyMappingAsync(1, 2, 0, 0, 0, 0));
		Assert.False(await service.SetKeyActuationAsync(1, 2, 200, 100, false, 0, 0));
		Assert.False(await service.SetPerKeyActuationAsync(1, 2, [0], 200, 100, false, 0, 0));

		// Reads report "no answer". Null rather than empty where the difference matters: an empty
		// key matrix would be read as a board with no keys.
		Assert.Null(await service.ProbeKeyMatrixAsync(1, 2));
		Assert.Null(await service.ReadKeyMatrixAsync(1, 2));
		Assert.Null(await service.GetKeyActuationAsync(1, 2));
	}

	// Before the host has ever been reached the client is still null, which is a different branch
	// from "the client exists and the call fails". Both must be silent.
	[Fact]
	public async Task EveryCallSurvivesBeforeTheHostHasEverBeenReached()
	{
		// The public constructor builds its own channel and would need appsettings.json; this
		// reaches the same state by never letting a client be assigned.
		IUsbHidDeviceService service = new UsbHidDeviceService(client: null!);

		Assert.Empty(service.GetDevices());
		Assert.Equal((-1, false), await service.GetBatteryLevelAsync(1, 2, "k"));
		Assert.False(await service.SetDeviceLedColorAsync(1, 2, "k", "Mouse", 1, 2, 3, 0, 100, 5));
		Assert.Null(await service.GetKeyActuationAsync(1, 2));
	}

	// ---- The subscribe loop -------------------------------------------------------------------

	// The App calls StartCommunication fire-and-forget on every Home load. With no host it must keep
	// retrying (so the App recovers by itself when the service is started again) without faulting
	// the task -- a faulted fire-and-forget task surfaces later as an unobserved exception.
	[Fact]
	public async Task StartCommunication_HostNeverAnswers_RetriesAndStopsCleanly()
	{
		var client = new DeadHostDeviceClient { SubscribeThrows = true };
		var service = CreateService(client);

		var startTask = service.StartCommunication();

		var deadline = DateTime.UtcNow + TestTimeout;
		while (Volatile.Read(ref client.SubscribeAttempts) < 3)
		{
			Assert.True(DateTime.UtcNow < deadline,
				"the App stopped trying to reach the DeviceHost -- it would never recover when the service is restarted");
			await Task.Delay(10);
		}

		service.StopCommunication();

		var completed = await Task.WhenAny(startTask, Task.Delay(TestTimeout));
		Assert.True(completed == startTask, "StartCommunication did not return after StopCommunication");
		await startTask; // rethrows if the loop faulted rather than exiting quietly
	}

	// The same loop when the subscribe call succeeds but the stream is dead underneath -- what a
	// host killed mid-session looks like, as opposed to one that was already gone.
	[Fact]
	public async Task StartCommunication_StreamDeadUnderneath_RetriesAndStopsCleanly()
	{
		var client = new DeadHostDeviceClient();
		var service = CreateService(client);
		var errors = new ConcurrentQueue<string>();
		service.NotifyError += (_, message) => errors.Enqueue(message);

		var startTask = service.StartCommunication();

		var deadline = DateTime.UtcNow + TestTimeout;
		while (Volatile.Read(ref client.SubscribeAttempts) < 3)
		{
			Assert.True(DateTime.UtcNow < deadline, "a dead stream did not lead to another subscribe attempt");
			await Task.Delay(10);
		}

		service.StopCommunication();
		var completed = await Task.WhenAny(startTask, Task.Delay(TestTimeout));
		Assert.True(completed == startTask, "StartCommunication did not return after StopCommunication");
		await startTask;

		// Reported to the log, not to the user: HomeViewModel's NotifyError handler only writes a
		// warning. Asserting it fires keeps an unreachable host diagnosable without putting anything
		// on screen.
		Assert.NotEmpty(errors);
	}

	// ---- Host availability --------------------------------------------------------------------

	// A launch that never reaches the host raises NOTHING, and keeps raising nothing however long it
	// retries. Two separate reasons, both deliberate:
	//
	//   * "Unavailable" is the starting state, so a first failed attempt is not a transition. There
	//     is also nothing to act on -- cards are only ever created from host pushes, so a launch
	//     that never reached the host has no card claiming to be connected.
	//   * The reconnect loop retries every 3 s for as long as the service is down. A level signal
	//     rather than an edge would re-blank every card indefinitely, forever.
	//
	// The case the user actually hit -- a host that WAS answering and then stopped -- is a real
	// transition and is covered by HostDyingMidSession_ReportsAvailableThenUnavailable below.
	[Fact]
	public async Task HostNeverReachable_RaisesNoTransitionHoweverLongItRetries()
	{
		var client = new DeadHostDeviceClient { SubscribeThrows = true };
		var service = CreateService(client);
		var transitions = new ConcurrentQueue<bool>();
		service.HostAvailabilityChanged += (_, available) => transitions.Enqueue(available);

		var startTask = service.StartCommunication();
		await WaitForAsync(() => Volatile.Read(ref client.SubscribeAttempts) >= 5,
			"not enough retry rounds to show the signal is edge-triggered");
		await StopAsync(service, startTask);

		Assert.Empty(transitions);
	}

	// The recovery direction. True is raised when a subscription is re-established; the App acts on
	// nothing here, because the host answers a fresh subscribe with the current device lists and the
	// ordinary DeviceFound path lights the cards back up.
	[Fact]
	public async Task HostComingBack_RaisesAvailable()
	{
		var client = new DeadHostDeviceClient { WriteSucceeds = true, StreamStaysOpen = true };
		var service = CreateService(client);
		var transitions = new ConcurrentQueue<bool>();
		service.HostAvailabilityChanged += (_, available) => transitions.Enqueue(available);

		var startTask = service.StartCommunication();
		await WaitForAsync(() => !transitions.IsEmpty, "a successful subscribe raised no availability transition");

		Assert.True(transitions.First());

		await StopAsync(service, startTask);
	}

	// Leaving Home and minimising to tray both call StopCommunication. That is not the host going
	// away, and treating it as such would blank every card on the way out -- wrong, and visible on
	// the next restore.
	[Fact]
	public async Task DeliberateStop_DoesNotReportTheHostAsUnavailable()
	{
		var client = new DeadHostDeviceClient { WriteSucceeds = true, StreamStaysOpen = true };
		var service = CreateService(client);
		var transitions = new ConcurrentQueue<bool>();
		service.HostAvailabilityChanged += (_, available) => transitions.Enqueue(available);

		var startTask = service.StartCommunication();
		await WaitForAsync(() => !transitions.IsEmpty, "the subscription never came up");
		await StopAsync(service, startTask);

		// Give any stray transition time to land before concluding that none did.
		await Task.Delay(50);

		Assert.Equal(new[] { true }, transitions.ToArray());
	}

	// A host killed mid-session, which is the shape actually reported: the subscription was healthy,
	// then the service stopped.
	[Fact]
	public async Task HostDyingMidSession_ReportsAvailableThenUnavailable()
	{
		var client = new DeadHostDeviceClient { WriteSucceeds = true };
		var service = CreateService(client);
		var transitions = new ConcurrentQueue<bool>();
		service.HostAvailabilityChanged += (_, available) => transitions.Enqueue(available);

		var startTask = service.StartCommunication();
		await WaitForAsync(() => transitions.Count >= 2, "a subscription that died raised no unavailable transition");
		await StopAsync(service, startTask);

		var seen = transitions.ToArray();
		Assert.True(seen[0], "the subscribe succeeded, so the host should first be reported available");
		Assert.False(seen[1], "the stream died, so the host should then be reported unavailable");
	}

	// Round 2 B1. The regression test for the worst bug this whole line of work produced: a routine
	// Home -> Device Details -> Home navigation could blank every card.
	//
	// RestartCommunication ABANDONS AwaitServerStreaming -- when the completion task wins the
	// Task.WhenAny, the streaming task is never awaited and keeps unwinding on its own. Gating the
	// "host unavailable" signal on the shared _reconnecting flag was therefore not enough: the next
	// Start sets that flag back to true, so the abandoned attempt reaches its finally, reads true,
	// and reports the host as gone when nothing of the sort happened. HomeViewModel, freshly
	// re-subscribed by the same Start, then deactivated every card -- and if the new subscription's
	// device push had already landed, nothing repopulated them until the user replugged a device.
	//
	// HomeView.OnUnloaded/OnLoaded call Stop/Start on every Home load and unload, so this was not a
	// rare interleaving; it was a normal navigation. CompleteDelay is what makes it deterministic
	// here rather than a coin flip.
	[Fact]
	public async Task StopThenStart_DoesNotBlankTheCards()
	{
		var client = new DeadHostDeviceClient
		{
			WriteSucceeds = true,
			StreamStaysOpen = true,
			// Holds the cancelled attempt in its inner finally until well after the restart below.
			CompleteDelay = TimeSpan.FromMilliseconds(200),
		};
		var service = CreateService(client);
		var transitions = new ConcurrentQueue<bool>();
		service.HostAvailabilityChanged += (_, available) => transitions.Enqueue(available);

		// A healthy subscription, as when Home is showing connected cards.
		var first = service.StartCommunication();
		await WaitForAsync(() => !transitions.IsEmpty, "the subscription never came up");

		// Leaving Home. Awaiting the task matters: it guarantees IsRunning is back to false, so the
		// restart below really restarts rather than no-opping on the guard and passing vacuously.
		await StopAsync(service, first);

		// Returning to Home, while the previous attempt is still unwinding.
		var second = service.StartCommunication();
		try
		{
			// Longer than CompleteDelay, so the abandoned attempt has definitely reached its outer
			// finally by the time this is asserted.
			await Task.Delay(400);

			Assert.DoesNotContain(false, transitions);
		}
		finally
		{
			await StopAsync(service, second);
		}
	}

	private static async Task WaitForAsync(Func<bool> condition, string because)
	{
		var deadline = DateTime.UtcNow + TestTimeout;
		while (!condition())
		{
			Assert.True(DateTime.UtcNow < deadline, because);
			await Task.Delay(10);
		}
	}

	private static async Task StopAsync(UsbHidDeviceService service, Task startTask)
	{
		service.StopCommunication();
		var completed = await Task.WhenAny(startTask, Task.Delay(TestTimeout));
		Assert.True(completed == startTask, "StartCommunication did not return after StopCommunication");
		await startTask;
	}
}
