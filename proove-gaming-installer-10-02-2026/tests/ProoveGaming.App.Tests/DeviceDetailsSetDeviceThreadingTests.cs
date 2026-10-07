using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// The Home→Details navigation contract: SetDeviceAsync's synchronous prologue never touches
// storage (that synchronous SQLite burst was the visible navigation stall), and a run made stale
// by a newer open must not contribute rows to the newer device's collections.
public class DeviceDetailsSetDeviceThreadingTests
{
	private const int MouseVid = 0x04D9;
	private const int MousePid = 0xA23F;
	private const int KbVid = 14000;
	private const int KbPid = 12385;
	private const string NoKey = "";

	private sealed class FakeOwnerProvider(string kind, string id) : ICurrentOwnerProvider
	{
		private readonly SettingsOwner _owner = new(kind, id);
		public SettingsOwner? GetCurrent() => _owner;
	}

	private sealed class FakeHid : IUsbHidDeviceService
	{
		public Task<bool> SetButtonBindingsAsync(int vendorId, int productId, string deviceKey, IReadOnlyList<(MouseButton button, MouseButtonAction action, MacroDefinition? macro)> bindings)
			=> Task.FromResult(true);
		public Task<bool> ApplyDeviceConfigurationAsync(
			int vendorId, int productId, string deviceKey,
			(byte r, byte g, byte b, byte effect, byte brightness, byte speed)? led,
			IReadOnlyList<(MouseButton button, MouseButtonAction action, MacroDefinition? macro)>? bindings,
			(byte debounce, byte liftOff)? mouseSettings,
			IReadOnlyList<(int dpi, byte r, byte g, byte b)>? dpiStages,
			int? activeStage,
			int? reportRateHz)
			=> Task.FromResult(true);
		public Task<bool> SetMacrosAsync(int vendorId, int productId, string deviceKey, IReadOnlyList<(int Index, MacroRepeatMode Mode, IReadOnlyList<MacroEvent> Events, int RepeatCount)> entries)
			=> Task.FromResult(true);
		public Task<bool> SetKeyMappingAsync(int vendorId, int productId, byte matrixIndex, byte type, byte code1, byte code2)
			=> Task.FromResult(true);
		public List<HidDeviceResponse> GetDevices() => [];
		public Task StartCommunication() => Task.CompletedTask;
		public bool CheckIfAllDevicesAreDisconnected() => true;
		public Task<KeyActuationInfo?> GetKeyActuationAsync(int vendorId, int productId) =>
			Task.FromResult<KeyActuationInfo?>(null);
		public Task<bool> SetKeyActuationAsync(int vendorId, int productId, int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm) =>
			Task.FromResult(false);
		public Task<bool> SetPerKeyActuationAsync(int vendorId, int productId, IReadOnlyCollection<int> slots, int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm) =>
			Task.FromResult(false);
		public void StopCommunication() { }
		public Task<(int batteryValue, bool isCharging)> GetBatteryLevelAsync(int vendorId, int productId, string deviceKey) => Task.FromResult((0, false));
		public Task<bool> SetDeviceLedColorAsync(int vendorId, int productId, string deviceKey, string deviceType, byte r, byte g, byte b, byte effect, byte brightness, byte speed) => Task.FromResult(true);
		public Task<bool> SetDpiStagesAsync(int vendorId, int productId, string deviceKey, IReadOnlyList<(int dpi, byte r, byte g, byte b)> stages) => Task.FromResult(true);
		public Task<bool> SetActiveDpiStageAsync(int vendorId, int productId, string deviceKey, byte stageIndex) => Task.FromResult(true);
		public Task<bool> SetReportRateAsync(int vendorId, int productId, string deviceKey, int hz) => Task.FromResult(true);
		public bool TryGetLastDpiStage(int vendorId, int productId, string deviceKey, out int stageIndex) { stageIndex = 0; return false; }
		public bool TryGetLastReportRate(int vendorId, int productId, string deviceKey, out int hz) { hz = 0; return false; }
		public Task<(IReadOnlyDictionary<byte, byte> ScancodeToSlot, IReadOnlyDictionary<byte, byte> UserOverrides)?> ProbeKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
			=> Task.FromResult<(IReadOnlyDictionary<byte, byte>, IReadOnlyDictionary<byte, byte>)?>(null);
		public Task<IReadOnlyDictionary<byte, (byte Type, byte Code1, byte Code2)>?> ReadKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
			=> Task.FromResult<IReadOnlyDictionary<byte, (byte, byte, byte)>?>(null);

public Task<ExtendedKeysInfo?> GetExtendedKeysAsync(int vendorId, int productId) => Task.FromResult<ExtendedKeysInfo?>(null);
public Task<bool> SetExtendedKeysAsync(int vendorId, int productId, IReadOnlyList<ProoveGaming.App.Models.ExtendedKeyRecord> entries) => Task.FromResult(false);

#pragma warning disable CS0067 // events required by the interface but unused in tests
		public event EventHandler<SubscribeOnDevicesResponse>? DeviceFound;
		public event EventHandler<DpiStageChange>? DpiStageChanged;
		public event EventHandler<ReportRateChange>? ReportRateChanged;
		public event EventHandler<BatteryChange>? BatteryChanged;
		public event EventHandler<string>? NotifyError;
		public event EventHandler<bool>? HostAvailabilityChanged;
#pragma warning restore CS0067
	}

	private static (DeviceDetailsViewModel Vm, LocalStorageService Store, string Owner) NewVm()
	{
		var owner = Guid.NewGuid().ToString("D");
		var store = new LocalStorageService("Data Source=:memory:");
		var vm = new DeviceDetailsViewModel(
			usbHidDeviceService: new FakeHid(),
			localStorage: store,
			settings: null,
			ownerProvider: new FakeOwnerProvider(LocalStorageService.OwnerKindGuest, owner));
		return (vm, store, owner);
	}

	private static SingleCardViewModel Mouse() => new()
	{
		Name = "Test Mouse",
		DeviceTypeLabel = "Wired Mouse",
		VendorId = MouseVid,
		ProductId = MousePid,
		SettingsVendorId = MouseVid,
		SettingsProductId = MousePid,
		DeviceKey = NoKey,
		SettingsDeviceKey = NoKey,
		SupportsButtonRemap = true,
		SupportsMacros = true,
	};

	private static DoubleCardViewModel Keyboard() => new()
	{
		Name = "Raster",
		DeviceTypeLabel = "Wireless Keyboard",
		VendorId = KbVid,
		ProductId = KbPid,
		SettingsVendorId = KbVid,
		SettingsProductId = KbPid,
		DeviceKey = NoKey,
		SettingsDeviceKey = NoKey,
		SupportsMacros = true,
	};

	// The reproducing test for the navigation stall: on the old synchronous SetDevice every
	// storage access ran on the calling (UI) thread before control returned, which is exactly the
	// pre-await window asserted here. The calling thread cannot be lent to the thread pool while
	// it is still executing this test body, so any access recorded with its id before the await
	// can only have come from the synchronous prologue. (After the await the thread returns to
	// the pool and may legitimately pick up Task.Run work, so thread ids are only meaningful in
	// the pre-await window.)
	[Fact]
	public async Task SetDeviceAsync_SynchronousPrologue_TouchesNoStorage()
	{
		var (vm, store, _) = NewVm();
		using (store)
		{
			var accesses = new ConcurrentBag<int>();
			store.OnBeforeStorageAccess = () => accesses.Add(Environment.CurrentManagedThreadId);
			var callingThreadId = Environment.CurrentManagedThreadId;

			var load = vm.SetDeviceAsync(Mouse());
			var duringPrologue = accesses.ToArray();

			await load;

			Assert.DoesNotContain(callingThreadId, duringPrologue);
			Assert.NotEmpty(accesses);
		}
	}

	// A stale run must not contribute rows to the newer device's collections. In the app both
	// loader chains' continuations run on the Avalonia dispatcher, interleaving only at awaits —
	// the pumping context below reproduces that serialization, without which the two chains
	// would race each other on collections the design never claims are thread-safe.
	[Fact]
	public async Task SetDeviceAsync_SecondDeviceWins_WhenSwitchingBeforeFirstLoadCompletes()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, MouseVid, MousePid, NoKey,
				new MacroRecord { Name = "MouseMacro", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, KbVid, KbPid, NoKey,
				new MacroRecord { Name = "KbMacro", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });

			var previous = SynchronizationContext.Current;
			var pump = new PumpingSyncContext();
			SynchronizationContext.SetSynchronizationContext(pump);
			Task done;
			try
			{
				var first = vm.SetDeviceAsync(Mouse());
				var second = vm.SetDeviceAsync(Keyboard());
				done = Task.WhenAll(first, second);
				pump.PumpUntil(done);
			}
			finally
			{
				SynchronizationContext.SetSynchronizationContext(previous);
			}
			await done;

			Assert.Equal("Raster", vm.Device?.Name);
			var macro = Assert.Single(vm.Macros);
			Assert.Equal("KbMacro", macro.Name);
		}
	}

	// The S1 review finding: reopening the SAME card while its previous load is still in flight
	// (a double-click) must not let the stale chain's macros continuation append rows after the
	// newer chain's clear. `ReferenceEquals(Device, device)` alone cannot catch this — both
	// chains hold the same card instance — so the loaders must also check the chain generation.
	[Fact]
	public async Task SetDeviceAsync_ReopeningTheSameCard_DoesNotDuplicateMacros()
	{
		var (vm, store, owner) = NewVm();
		using (store)
		{
			store.SaveMacro(
				LocalStorageService.OwnerKindGuest, owner, MouseVid, MousePid, NoKey,
				new MacroRecord { Name = "MouseMacro", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });

			var card = Mouse();
			var previous = SynchronizationContext.Current;
			var pump = new PumpingSyncContext();
			SynchronizationContext.SetSynchronizationContext(pump);
			Task done;
			try
			{
				var first = vm.SetDeviceAsync(card);
				var second = vm.SetDeviceAsync(card);
				done = Task.WhenAll(first, second);
				pump.PumpUntil(done);
			}
			finally
			{
				SynchronizationContext.SetSynchronizationContext(previous);
			}
			await done;

			var macro = Assert.Single(vm.Macros);
			Assert.Equal("MouseMacro", macro.Name);
		}
	}

	// A minimal stand-in for the dispatcher: continuations post here and are pumped one at a
	// time on the test thread, so chains interleave at awaits instead of running concurrently.
	private sealed class PumpingSyncContext : SynchronizationContext
	{
		private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

		public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
		public override void Send(SendOrPostCallback d, object? state) => d(state);

		public void PumpUntil(Task done, int timeoutMs = 10_000)
		{
			var deadline = Environment.TickCount64 + timeoutMs;
			while (!done.IsCompleted)
			{
				if (Environment.TickCount64 > deadline)
					throw new TimeoutException("The loader chains did not complete within the pump timeout.");
				if (_queue.TryTake(out var item, 25))
					item.Callback(item.State);
			}
		}
	}
}
