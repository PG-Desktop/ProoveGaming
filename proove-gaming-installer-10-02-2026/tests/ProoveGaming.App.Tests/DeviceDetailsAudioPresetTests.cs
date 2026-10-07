using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// Audio levels are preset state: changing one must enable Save Preset, and the saved preset must
// carry the levels so switching presets restores them.
//
// The other half of this is what must NOT happen. Audio goes to the operating system's endpoints,
// not into the device's configuration frame, so an audio edit must not trigger a device write —
// routing it through StageChangeAndApply would fire an all-null ApplyDeviceConfiguration at
// Cosmo's undocumented HID interface on every slider move.
public class DeviceDetailsAudioPresetTests
{
	private const int Vid = 0x0D8C;
	private const int Pid = 0x0334;
	private const string ContainerId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

	private sealed class FakeOwnerProvider(string kind, string id) : ICurrentOwnerProvider
	{
		private readonly SettingsOwner _owner = new(kind, id);
		public SettingsOwner? GetCurrent() => _owner;
	}

	private sealed class CountingHid : IUsbHidDeviceService
	{
		public int ApplyConfigCallCount { get; private set; }

		public Task<bool> ApplyDeviceConfigurationAsync(
			int vendorId, int productId, string deviceKey,
			(byte r, byte g, byte b, byte effect, byte brightness, byte speed)? led,
			IReadOnlyList<(MouseButton button, MouseButtonAction action, MacroDefinition? macro)>? bindings,
			(byte debounce, byte liftOff)? mouseSettings,
			IReadOnlyList<(int dpi, byte r, byte g, byte b)>? dpiStages,
			int? activeStage, int? reportRateHz)
		{
			ApplyConfigCallCount++;
			return Task.FromResult(true);
		}

		public Task<bool> SetButtonBindingsAsync(int vendorId, int productId, string deviceKey, IReadOnlyList<(MouseButton button, MouseButtonAction action, MacroDefinition? macro)> bindings) => Task.FromResult(true);
		public List<HidDeviceResponse> GetDevices() => [];
		public Task StartCommunication() => Task.CompletedTask;
		public bool CheckIfAllDevicesAreDisconnected() => true;
		public void StopCommunication() { }
		public Task<(int batteryValue, bool isCharging)> GetBatteryLevelAsync(int vendorId, int productId, string deviceKey) => Task.FromResult((0, false));
		public Task<bool> SetDeviceLedColorAsync(int vendorId, int productId, string deviceKey, string deviceType, byte r, byte g, byte b, byte effect, byte brightness, byte speed) => Task.FromResult(true);
		// Actuation arrived on main while this branch was in flight; the headset never uses it.
		public Task<KeyActuationInfo?> GetKeyActuationAsync(int vendorId, int productId) =>
			Task.FromResult<KeyActuationInfo?>(null);
		public Task<bool> SetKeyActuationAsync(int vendorId, int productId, int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm) =>
			Task.FromResult(false);
		public Task<bool> SetPerKeyActuationAsync(int vendorId, int productId, IReadOnlyCollection<int> slots, int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm) =>
			Task.FromResult(false);
		public Task<bool> SetDpiStagesAsync(int vendorId, int productId, string deviceKey, IReadOnlyList<(int dpi, byte r, byte g, byte b)> stages) => Task.FromResult(true);
		public Task<bool> SetActiveDpiStageAsync(int vendorId, int productId, string deviceKey, byte stageIndex) => Task.FromResult(true);
		public Task<bool> SetReportRateAsync(int vendorId, int productId, string deviceKey, int hz) => Task.FromResult(true);
		public bool TryGetLastDpiStage(int vendorId, int productId, string deviceKey, out int stageIndex) { stageIndex = 0; return false; }
		public bool TryGetLastReportRate(int vendorId, int productId, string deviceKey, out int hz) { hz = 0; return false; }
		public Task<bool> SetKeyMappingAsync(int vendorId, int productId, byte matrixIndex, byte type, byte code1, byte code2) => Task.FromResult(true);
		public Task<(IReadOnlyDictionary<byte, byte> ScancodeToSlot, IReadOnlyDictionary<byte, byte> UserOverrides)?> ProbeKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
			=> Task.FromResult<(IReadOnlyDictionary<byte, byte>, IReadOnlyDictionary<byte, byte>)?>(null);
		public Task<IReadOnlyDictionary<byte, (byte Type, byte Code1, byte Code2)>?> ReadKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
			=> Task.FromResult<IReadOnlyDictionary<byte, (byte, byte, byte)>?>(null);
		public Task<bool> SetMacrosAsync(int vendorId, int productId, string deviceKey,
			IReadOnlyList<(int Index, MacroRepeatMode Mode, IReadOnlyList<MacroEvent> Events, int RepeatCount)> entries)
			=> Task.FromResult(true);

public Task<ExtendedKeysInfo?> GetExtendedKeysAsync(int vendorId, int productId) => Task.FromResult<ExtendedKeysInfo?>(null);
public Task<bool> SetExtendedKeysAsync(int vendorId, int productId, IReadOnlyList<ProoveGaming.App.Models.ExtendedKeyRecord> entries) => Task.FromResult(false);

#pragma warning disable CS0067 // Required by the interface, unused here.
		public event EventHandler<SubscribeOnDevicesResponse>? DeviceFound;
		public event EventHandler<string>? NotifyError;
		public event EventHandler<bool>? HostAvailabilityChanged;
		public event EventHandler<DpiStageChange>? DpiStageChanged;
		public event EventHandler<ReportRateChange>? ReportRateChanged;
		public event EventHandler<BatteryChange>? BatteryChanged;
#pragma warning restore CS0067
	}

	private sealed class FakeAudioEndpoints : IAudioEndpointService
	{
		public Task<int?> GetOutputVolumeAsync(string containerId, CancellationToken ct = default) => Task.FromResult<int?>(null);
		public Task<int?> GetMicrophoneVolumeAsync(string containerId, CancellationToken ct = default) => Task.FromResult<int?>(null);
		public Task SetOutputVolumeAsync(string containerId, int percent, CancellationToken ct = default) => Task.CompletedTask;
		public Task SetMicrophoneVolumeAsync(string containerId, int percent, CancellationToken ct = default) => Task.CompletedTask;
		public Task<bool?> GetOutputMutedAsync(string containerId, CancellationToken ct = default) => Task.FromResult<bool?>(false);
		public Task<bool?> GetMicrophoneMutedAsync(string containerId, CancellationToken ct = default) => Task.FromResult<bool?>(false);
		public Task<AudioEndpointState> GetOutputStateAsync(string containerId, CancellationToken ct = default)
			=> Task.FromResult(new AudioEndpointState(null, false));
		public Task<AudioEndpointState> GetMicrophoneStateAsync(string containerId, CancellationToken ct = default)
			=> Task.FromResult(new AudioEndpointState(null, false));
	}

	private static (DeviceDetailsViewModel Vm, CountingHid Hid) NewVm()
	{
		var owner = Guid.NewGuid().ToString("D");
		var store = new LocalStorageService("Data Source=:memory:");
		var ownerProvider = new FakeOwnerProvider(LocalStorageService.OwnerKindGuest, owner);
		var hid = new CountingHid();
		var vm = new DeviceDetailsViewModel(
			usbHidDeviceService: hid,
			localStorage: store,
			settings: new SettingsRepository(store, ownerProvider),
			ownerProvider: ownerProvider,
			audioEndpoints: new FakeAudioEndpoints());
		return (vm, hid);
	}

	private static SingleCardViewModel Cosmo() => new()
	{
		Name = "Cosmo",
		DeviceTypeLabel = "Wireless Headset",
		VendorId = Vid,
		ProductId = Pid,
		SettingsVendorId = Vid,
		SettingsProductId = Pid,
		DeviceKey = ContainerId,
		SettingsDeviceKey = ContainerId,
		SupportedAudioSettings = ["OutputVolume", "MicrophoneVolume"],
		HasDongle = true,
	};

	[Fact]
	public async Task ChangingAVolume_EnablesSavePreset()
	{
		var (vm, _) = NewVm();
		await vm.SetDeviceAsync(Cosmo());
		await vm.RebuildAudioSettingsAsync();

		Assert.NotNull(vm.SelectedPreset);
		Assert.False(vm.HasUnsavedPresetChanges);

		vm.OutputVolumeSetting!.Value = 33;
		await vm.ApplyAudioSettingsAsync();

		Assert.True(vm.HasUnsavedPresetChanges);
		Assert.True(vm.SavePresetCommand.CanExecute(null));
	}

	// The guard that keeps unsolicited writes off Cosmo's vendor HID channel.
	[Fact]
	public async Task ChangingAVolume_DoesNotWriteToTheDevice()
	{
		var (vm, hid) = NewVm();
		await vm.SetDeviceAsync(Cosmo());
		await vm.RebuildAudioSettingsAsync();

		var before = hid.ApplyConfigCallCount;

		vm.OutputVolumeSetting!.Value = 44;
		vm.MicrophoneVolumeSetting!.Value = 12;
		await vm.ApplyAudioSettingsAsync();

		Assert.Equal(before, hid.ApplyConfigCallCount);
	}

	// Audio persists into device_settings, which is what the preset snapshot copies — so saving a
	// preset captures the levels, and SELECTING that preset again restores them.
	//
	// This drives SelectPresetCommand, the real path: it runs ApplyPresetToDeviceAsync ->
	// LoadPresetSnapshotToDevice, which overwrites the live device_settings rows from the snapshot
	// before the sliders are rebuilt. An earlier version of this test called
	// RebuildAudioSettingsAsync(forcePersisted: true) directly, which only re-reads the LIVE rows —
	// so it asserted the drifted values and proved the opposite of its own name.
	[Fact]
	public async Task ReselectingASavedPreset_RestoresTheVolumesItWasSavedWith()
	{
		var (vm, _) = NewVm();
		await vm.SetDeviceAsync(Cosmo());
		await vm.RebuildAudioSettingsAsync();

		var preset = vm.SelectedPreset;
		Assert.NotNull(preset);

		vm.OutputVolumeSetting!.Value = 25;
		vm.MicrophoneVolumeSetting!.Value = 70;
		await vm.ApplyAudioSettingsAsync();
		Assert.True(vm.HasUnsavedPresetChanges, "audio edit should have marked the preset dirty");
		Assert.True(vm.SavePresetCommand.CanExecute(null), "Save Preset should be enabled");

		// SavePresetCommand only OPENS the save dialog; ConfirmSavePreset is what writes the
		// snapshot. Driving both is what the user does, and skipping the confirm silently saved
		// nothing — which is how the first version of this test ended up asserting the drifted
		// values instead of the saved ones.
		vm.SavePresetCommand.Execute(null);
		await vm.ConfirmSavePresetCommand.ExecuteAsync(null);
		Assert.False(vm.HasUnsavedPresetChanges, "saving should have cleared the dirty flag");

		// Drift away from what the preset holds.
		vm.OutputVolumeSetting!.Value = 90;
		vm.MicrophoneVolumeSetting!.Value = 90;
		await vm.ApplyAudioSettingsAsync();

		// Re-select the preset: this is what a user clicking it in the dropdown does.
		await vm.SelectPresetCommand.ExecuteAsync(preset);
		await WaitForAsync(() => vm.OutputVolumeSetting!.Value == 25);

		Assert.Equal(25, vm.OutputVolumeSetting!.Value);
		Assert.Equal(70, vm.MicrophoneVolumeSetting!.Value);
	}

	// SelectPresetCommand is an async RelayCommand, so the apply continues past Execute. Polls
	// rather than sleeping a fixed time so the test is not timing-fragile.
	private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
	{
		var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
		while (DateTime.UtcNow < deadline)
		{
			if (condition()) return;
			await Task.Delay(25);
		}
	}
}
