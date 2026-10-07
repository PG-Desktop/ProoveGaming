using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// The C17 contract: HomeViewModel's notification paths never run SQLite on the calling (UI)
// thread. AddNotification shows the item immediately and queues its row insert, the StorageId
// continuation closes the deleted/read-toggled-before-persisted races, and mark-all-read is one
// batched write instead of one commit per item. Harness mirrors HomeViewModelPersistenceTests;
// thread assertions mirror DeviceDetailsSetDeviceThreadingTests.
public class HomeViewModelNotificationThreadingTests
{
	private const int CustomerId = 42;
	private const string UserId = "42";

	private sealed class FakeHid : IUsbHidDeviceService
	{
		public List<HidDeviceResponse> GetDevices() => [];
		public Task StartCommunication() => Task.CompletedTask;
		public void StopCommunication() { }
		public bool CheckIfAllDevicesAreDisconnected() => true;
		public Task<(int batteryValue, bool isCharging)> GetBatteryLevelAsync(int vendorId, int productId, string deviceKey) => Task.FromResult((0, false));
		public Task<bool> SetDeviceLedColorAsync(int vendorId, int productId, string deviceKey, string deviceType, byte r, byte g, byte b, byte effect, byte brightness, byte speed) => Task.FromResult(true);
		public Task<bool> SetDpiStagesAsync(int vendorId, int productId, string deviceKey, IReadOnlyList<(int dpi, byte r, byte g, byte b)> stages) => Task.FromResult(true);
		public Task<bool> SetActiveDpiStageAsync(int vendorId, int productId, string deviceKey, byte stageIndex) => Task.FromResult(true);
		public Task<bool> SetReportRateAsync(int vendorId, int productId, string deviceKey, int hz) => Task.FromResult(true);
		public Task<bool> SetButtonBindingsAsync(int vendorId, int productId, string deviceKey, IReadOnlyList<(MouseButton button, MouseButtonAction action, MacroDefinition? macro)> bindings) => Task.FromResult(true);
		public Task<bool> ApplyDeviceConfigurationAsync(
			int vendorId, int productId, string deviceKey,
			(byte r, byte g, byte b, byte effect, byte brightness, byte speed)? led,
			IReadOnlyList<(MouseButton button, MouseButtonAction action, MacroDefinition? macro)>? bindings,
			(byte debounce, byte liftOff)? mouseSettings,
			IReadOnlyList<(int dpi, byte r, byte g, byte b)>? dpiStages,
			int? activeStage,
			int? reportRateHz)
			=> Task.FromResult(true);
		public Task<bool> SetMacrosAsync(int vendorId, int productId, string deviceKey, IReadOnlyList<(int Index, MacroRepeatMode Mode, IReadOnlyList<MacroEvent> Events, int RepeatCount)> entries) => Task.FromResult(true);
		public bool TryGetLastDpiStage(int vendorId, int productId, string deviceKey, out int stageIndex) { stageIndex = 0; return false; }
		public bool TryGetLastReportRate(int vendorId, int productId, string deviceKey, out int hz) { hz = 0; return false; }
		public Task<KeyActuationInfo?> GetKeyActuationAsync(int vendorId, int productId) => Task.FromResult<KeyActuationInfo?>(null);
		public Task<bool> SetKeyActuationAsync(int vendorId, int productId, int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm) => Task.FromResult(false);
		public Task<bool> SetPerKeyActuationAsync(int vendorId, int productId, IReadOnlyCollection<int> slots, int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm) => Task.FromResult(false);
		public Task<IReadOnlyDictionary<byte, (byte Type, byte Code1, byte Code2)>?> ReadKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
			=> Task.FromResult<IReadOnlyDictionary<byte, (byte, byte, byte)>?>(null);
		public Task<(IReadOnlyDictionary<byte, byte> ScancodeToSlot, IReadOnlyDictionary<byte, byte> UserOverrides)?> ProbeKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
			=> Task.FromResult<(IReadOnlyDictionary<byte, byte>, IReadOnlyDictionary<byte, byte>)?>(null);
		public Task<bool> SetKeyMappingAsync(int vendorId, int productId, byte matrixIndex, byte type, byte code1, byte code2) => Task.FromResult(false);

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

	private sealed record Harness(
		HomeViewModel Vm,
		LocalStorageService Storage,
		SessionService Session,
		UserProfile User);

	private static Harness NewVm(LocalStorageService? storage = null)
	{
		storage ??= new LocalStorageService("Data Source=:memory:");
		var session = new SessionService(a => a());
		var user = new UserProfile { CustomerId = CustomerId, SessionToken = "token" };

		var services = new ServiceCollection();
		services.AddTransient<AddDeviceCardViewModel>();
		services.AddTransient<SingleCardViewModel>();
		services.AddTransient<DoubleCardViewModel>();

		var vm = new HomeViewModel(
			new FakeHid(),
			session,
			storage,
			user,
			services.BuildServiceProvider(),
			new SystemNotificationService(NullLogger<SystemNotificationService>.Instance),
			new CurrentOwnerProvider(session, user),
			NullLogger<HomeViewModel>.Instance);

		return new Harness(vm, storage, session, user);
	}

	private static NotificationRecord Bell(string title, bool isRead = false) => new()
	{
		Title = title,
		Subtitle = "PG Taipan",
		CreatedAt = DateTime.Now,
		IsRead = isRead
	};

	// The reproducing test for C17: the old AddNotification ran the INSERT+trim transaction
	// inline before returning — on the calling thread inside the pre-await window asserted here
	// (see DeviceDetailsSetDeviceThreadingTests for why thread ids are only meaningful there).
	// The item itself must be visible in the bell before any storage work completes.
	[Fact]
	public async Task AddNotification_SynchronousPart_TouchesNoStorage_AndShowsTheItemImmediately()
	{
		var h = NewVm();
		using (h.Storage)
		{
			await h.Vm.NotificationsLoaded;
			var accesses = new ConcurrentBag<int>();
			h.Storage.OnBeforeStorageAccess = () => accesses.Add(Environment.CurrentManagedThreadId);
			var callingThreadId = Environment.CurrentManagedThreadId;

			h.Vm.AddNotification("Device connected", "PG Taipan");
			var duringPrologue = accesses.ToArray();
			var shownImmediately = h.Vm.Notifications.Count;

			await h.Vm.LastNotificationPersist;

			Assert.Equal(1, shownImmediately);
			Assert.DoesNotContain(callingThreadId, duringPrologue);
			Assert.NotEmpty(accesses);
		}
	}

	[Fact]
	public async Task AddNotification_AssignsStorageId_OnceThePersistLands()
	{
		var h = NewVm();
		using (h.Storage)
		{
			await h.Vm.NotificationsLoaded;

			h.Vm.AddNotification("Device connected", "PG Taipan");
			var item = Assert.Single(h.Vm.Notifications);
			Assert.Equal(0, item.StorageId);

			await h.Vm.LastNotificationPersist;

			Assert.True(item.StorageId > 0);
			var stored = Assert.Single(h.Storage.GetNotifications(LocalStorageService.OwnerKindUser, UserId));
			Assert.Equal(stored.Id, item.StorageId);
		}
	}

	// The deleted-before-persisted race: the row insert is still queued when the user deletes
	// the item, so the delete handler cannot aim at a row id — the persist continuation must
	// remove the just-created row instead. Zero rows may survive.
	[Fact]
	public async Task DeletingANotification_BeforeItsPersistLands_LeavesNoOrphanRow()
	{
		var h = NewVm();
		using (h.Storage)
		{
			await h.Vm.NotificationsLoaded;
			using var gate = new ManualResetEventSlim(false);
			_ = h.Storage.RunOnStorageAsync(() => gate.Wait());

			h.Vm.AddNotification("Device connected", "PG Taipan");
			var item = Assert.Single(h.Vm.Notifications);
			Assert.Equal(0, item.StorageId);
			item.DeleteCommand.Execute(null);
			Assert.Empty(h.Vm.Notifications);

			gate.Set();
			await h.Vm.LastNotificationPersist;
			await h.Storage.DrainStorageQueueAsync();

			Assert.Empty(h.Storage.GetNotifications(LocalStorageService.OwnerKindUser, UserId));
		}
	}

	// The read-toggled-before-persisted race: the row is inserted unread; the continuation
	// re-checks IsRead once the row id exists and pushes the toggle.
	[Fact]
	public async Task MarkingRead_BeforeThePersistLands_StillPersistsTheReadState()
	{
		var h = NewVm();
		using (h.Storage)
		{
			await h.Vm.NotificationsLoaded;
			using var gate = new ManualResetEventSlim(false);
			_ = h.Storage.RunOnStorageAsync(() => gate.Wait());

			h.Vm.AddNotification("Device connected", "PG Taipan");
			var item = Assert.Single(h.Vm.Notifications);
			item.IsRead = true;

			gate.Set();
			await h.Vm.LastNotificationPersist;
			await h.Storage.DrainStorageQueueAsync();

			var stored = Assert.Single(h.Storage.GetNotifications(LocalStorageService.OwnerKindUser, UserId));
			Assert.True(stored.IsRead);
		}
	}

	// Mark-all must issue exactly ONE storage write (the batched UPDATE), not one commit per
	// item — the audit's worst measured path at 100 items.
	[Fact]
	public async Task MarkAllRead_IssuesASingleBatchedWrite_AndPersists()
	{
		var storage = new LocalStorageService("Data Source=:memory:");
		for (var i = 0; i < 5; i++)
			storage.AddNotification(LocalStorageService.OwnerKindUser, UserId, Bell($"N{i}"));
		var h = NewVm(storage);
		using (h.Storage)
		{
			await h.Vm.NotificationsLoaded;
			Assert.Equal(5, h.Vm.Notifications.Count);

			var accessCount = 0;
			h.Storage.OnBeforeStorageAccess = () => Interlocked.Increment(ref accessCount);

			h.Vm.MarkAllNotificationsReadCommand.Execute(null);
			await h.Storage.DrainStorageQueueAsync();

			Assert.Equal(1, accessCount);
			Assert.All(h.Vm.Notifications, n => Assert.True(n.IsRead));
			h.Storage.OnBeforeStorageAccess = null;
			Assert.All(
				h.Storage.GetNotifications(LocalStorageService.OwnerKindUser, UserId),
				n => Assert.True(n.IsRead));
		}
	}

	// An owner switch mid-load must never show the previous owner's bell: the synchronous clear
	// runs immediately, and a read that completes after a newer switch (stale generation) is
	// discarded instead of inserting the wrong owner's rows.
	[Fact]
	public async Task OwnerSwitchMidLoad_NeverShowsTheStaleOwnersNotifications()
	{
		var storage = new LocalStorageService("Data Source=:memory:");
		storage.AddNotification(LocalStorageService.OwnerKindUser, "77", Bell("Other owner's secret"));
		storage.AddNotification(LocalStorageService.OwnerKindUser, UserId, Bell("Mine"));
		var h = NewVm(storage);
		using (h.Storage)
		{
			await h.Vm.NotificationsLoaded;
			Assert.Equal("Mine", Assert.Single(h.Vm.Notifications).Title);

			using var gate = new ManualResetEventSlim(false);
			_ = h.Storage.RunOnStorageAsync(() => gate.Wait());

			// Switch to 77 (its read parks behind the gate), then straight back to 42: the 77
			// read completes stale and must be dropped rather than inserted.
			h.User.CustomerId = 77;
			h.Vm.ReloadOwnerScopedState();
			var staleLoad = h.Vm.NotificationsLoaded;
			h.User.CustomerId = CustomerId;
			h.Vm.ReloadOwnerScopedState();
			var currentLoad = h.Vm.NotificationsLoaded;

			gate.Set();
			await staleLoad;
			await currentLoad;

			Assert.Equal("Mine", Assert.Single(h.Vm.Notifications).Title);
		}
	}

	// FlushSession snapshots on the caller and enqueues the whole-table rewrite: becoming a
	// guest adopts the on-screen card with zero storage work on the calling thread, and the
	// row is on disk once the queue drains.
	[Fact]
	public async Task GuestFlush_RunsOffTheCallingThread_AndLandsOnDrain()
	{
		var h = NewVm();
		using (h.Storage)
		{
			await h.Vm.NotificationsLoaded;
			var card = new SingleCardViewModel { Name = "Live Mouse", DeviceTypeLabel = "Wired Mouse", VendorId = 1, ProductId = 2 };
			h.Vm.Devices.Insert(0, card);

			var accesses = new ConcurrentBag<int>();
			h.Storage.OnBeforeStorageAccess = () => accesses.Add(Environment.CurrentManagedThreadId);
			var callingThreadId = Environment.CurrentManagedThreadId;

			h.User.CustomerId = 0; // logout cleared the profile
			h.Session.StartSession(); // adopts the card into the fresh guest session, queues the flush
			var duringPrologue = accesses.ToArray();

			await h.Storage.DrainStorageQueueAsync();
			h.Storage.OnBeforeStorageAccess = null;

			Assert.DoesNotContain(callingThreadId, duringPrologue);
			var guestRow = Assert.Single(h.Storage.GetDevices());
			Assert.Equal("Live Mouse", guestRow.Name);
		}
	}
}
