using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// The Audio section: which sliders exist, where the level is applied, and that a device which
// cannot take an OS-level change is left alone rather than silently driven anyway.
//
// The real Core Audio calls are faked at the interface boundary — enumerating live endpoints is a
// QA concern, not a unit-test one (see the plan's "deliberately not automated").
public class DeviceDetailsAudioTests
{
	private const int Vid = 0x0D8C;
	private const int Pid = 0x0334;
	private const string ContainerId = "11111111-2222-3333-4444-555555555555";

	private sealed class FakeOwnerProvider(string kind, string id) : ICurrentOwnerProvider
	{
		private readonly SettingsOwner _owner = new(kind, id);
		public SettingsOwner? GetCurrent() => _owner;
	}

	// Records every call so a test can assert the endpoint was (or was NOT) driven.
	private sealed class FakeAudioEndpoints : IAudioEndpointService
	{
		public int? Output { get; set; }
		public int? Mic { get; set; }
		public bool OutputMuted { get; set; }
		public bool MicMuted { get; set; }
		public List<(string Container, int Percent)> OutputWrites { get; } = [];
		public List<(string Container, int Percent)> MicWrites { get; } = [];

		public Task<int?> GetOutputVolumeAsync(string containerId, CancellationToken ct = default)
			=> Task.FromResult(Output);
		public Task<int?> GetMicrophoneVolumeAsync(string containerId, CancellationToken ct = default)
			=> Task.FromResult(Mic);
		public Task SetOutputVolumeAsync(string containerId, int percent, CancellationToken ct = default)
		{
			OutputWrites.Add((containerId, percent));
			return Task.CompletedTask;
		}
		public Task SetMicrophoneVolumeAsync(string containerId, int percent, CancellationToken ct = default)
		{
			MicWrites.Add((containerId, percent));
			return Task.CompletedTask;
		}
		public Task<bool?> GetOutputMutedAsync(string containerId, CancellationToken ct = default)
			=> Task.FromResult<bool?>(OutputMuted);
		public Task<bool?> GetMicrophoneMutedAsync(string containerId, CancellationToken ct = default)
			=> Task.FromResult<bool?>(MicMuted);
		public Task<AudioEndpointState> GetOutputStateAsync(string containerId, CancellationToken ct = default)
			=> Task.FromResult(new AudioEndpointState(Output, OutputMuted));
		public Task<AudioEndpointState> GetMicrophoneStateAsync(string containerId, CancellationToken ct = default)
			=> Task.FromResult(new AudioEndpointState(Mic, MicMuted));
	}

	private static (DeviceDetailsViewModel Vm, FakeAudioEndpoints Audio, LocalStorageService Store) NewVm()
	{
		var owner = System.Guid.NewGuid().ToString("D");
		var store = new LocalStorageService("Data Source=:memory:");
		var ownerProvider = new FakeOwnerProvider(LocalStorageService.OwnerKindGuest, owner);
		var audio = new FakeAudioEndpoints();
		var vm = new DeviceDetailsViewModel(
			usbHidDeviceService: null,
			localStorage: store,
			settings: new SettingsRepository(store, ownerProvider),
			ownerProvider: ownerProvider,
			audioEndpoints: audio);
		return (vm, audio, store);
	}

	private static SingleCardViewModel Cosmo(AudioVolumeTarget target = AudioVolumeTarget.OperatingSystem) => new()
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
		AudioVolumeTarget = target,
		// HasAudioIndicators requires a live connection: a disconnected device shows no icons.
		HasDongle = true,
	};

	private static SingleCardViewModel Mouse() => new()
	{
		Name = "BLAZE",
		DeviceTypeLabel = "Wired Mouse",
		VendorId = 0x04D9,
		ProductId = 0xA245,
		SettingsVendorId = 0x04D9,
		SettingsProductId = 0xA245,
		DeviceKey = "",
		SettingsDeviceKey = "",
		SupportedMouseSettings = ["ButtonDebounce"],
	};

	// ── Slider construction ───────────────────────────────────────────────────

	[Fact]
	public async Task BuildsBothSliders_ForADeviceThatDeclaresAudio()
	{
		var (vm, _, _) = NewVm();
		vm.Device = Cosmo();

		await vm.RebuildAudioSettingsAsync();

		Assert.True(vm.HasOutputVolume);
		Assert.True(vm.HasMicrophoneVolume);
		Assert.True(vm.Device!.HasAudioIndicators);
	}

	[Fact]
	public async Task SlidersAreZeroToOneHundredPercent()
	{
		var (vm, _, _) = NewVm();
		vm.Device = Cosmo();

		await vm.RebuildAudioSettingsAsync();

		foreach (var slider in new[] { vm.OutputVolumeSetting!, vm.MicrophoneVolumeSetting! })
		{
			Assert.Equal(0, slider.Min);
			Assert.Equal(100, slider.Max);
			Assert.Equal("%", slider.Unit);
		}
	}

	// A device that declares no audio must build nothing — this is what keeps the Audio section and
	// the connectivity-bar mute icons off every mouse and keyboard.
	[Fact]
	public async Task BuildsNoSliders_ForADeviceWithoutAudio()
	{
		var (vm, _, _) = NewVm();
		vm.Device = Mouse();

		await vm.RebuildAudioSettingsAsync();

		Assert.False(vm.HasOutputVolume);
		Assert.False(vm.HasMicrophoneVolume);
		Assert.False(vm.Device!.HasAudioIndicators);
		Assert.Empty(vm.AudioSettings);
	}

	// ── Where the level is applied ────────────────────────────────────────────

	// The live endpoint is the truth on open: the user may have moved the Windows slider since we
	// last saw them, so the section has to agree with the volume mixer rather than with our DB.
	[Fact]
	public async Task ReadsTheLiveEndpointOnOpen_ForAnOperatingSystemTargetDevice()
	{
		var (vm, audio, _) = NewVm();
		audio.Output = 30;
		audio.Mic = 80;
		vm.Device = Cosmo();

		await vm.RebuildAudioSettingsAsync();

		Assert.Equal(30, vm.OutputVolumeSetting!.Value);
		Assert.Equal(80, vm.MicrophoneVolumeSetting!.Value);
	}

	// The Device target is declared but NOT implemented. It must be inert rather than silently
	// applying an OS-level change that the device was never meant to take.
	[Fact]
	public async Task DeviceTargetNeverTouchesTheOperatingSystemEndpoint()
	{
		var (vm, audio, _) = NewVm();
		audio.Output = 30;
		vm.Device = Cosmo(AudioVolumeTarget.Device);

		await vm.RebuildAudioSettingsAsync();
		vm.OutputVolumeSetting!.Value = 55;
		await vm.ApplyAudioSettingsAsync();

		Assert.Empty(audio.OutputWrites);
		Assert.Empty(audio.MicWrites);
	}

	[Fact]
	public async Task OperatingSystemTargetDrivesTheMatchingEndpoint()
	{
		var (vm, audio, _) = NewVm();
		vm.Device = Cosmo();
		await vm.RebuildAudioSettingsAsync();

		vm.OutputVolumeSetting!.Value = 30;
		vm.MicrophoneVolumeSetting!.Value = 80;
		await vm.ApplyAudioSettingsAsync();

		Assert.Equal((ContainerId, 30), audio.OutputWrites[^1]);
		Assert.Equal((ContainerId, 80), audio.MicWrites[^1]);
	}

	// ── Persistence ───────────────────────────────────────────────────────────

	[Fact]
	public async Task VolumesSurviveAReopen()
	{
		var (vm, audio, _) = NewVm();
		vm.Device = Cosmo();
		await vm.RebuildAudioSettingsAsync();

		vm.OutputVolumeSetting!.Value = 42;
		vm.MicrophoneVolumeSetting!.Value = 17;
		await vm.ApplyAudioSettingsAsync();

		// Reopen with no live endpoint reading available, so the persisted row is what fills them.
		audio.Output = null;
		audio.Mic = null;
		await vm.RebuildAudioSettingsAsync();

		Assert.Equal(42, vm.OutputVolumeSetting!.Value);
		Assert.Equal(17, vm.MicrophoneVolumeSetting!.Value);
	}

	// A preset apply must win over whatever the endpoint currently reads — that is the whole point
	// of applying a preset. Without forcePersisted the live value silently overrode the preset.
	[Fact]
	public async Task PresetApplyPrefersTheStoredVolumeOverTheLiveEndpoint()
	{
		var (vm, audio, _) = NewVm();
		vm.Device = Cosmo();
		await vm.RebuildAudioSettingsAsync();

		vm.OutputVolumeSetting!.Value = 25;
		await vm.ApplyAudioSettingsAsync();

		// The user then drags the Windows slider somewhere else entirely.
		audio.Output = 90;

		await vm.RebuildAudioSettingsAsync(forcePersisted: true);

		Assert.Equal(25, vm.OutputVolumeSetting!.Value);
		// ...and the stored level is pushed back out to the endpoint.
		Assert.Equal((ContainerId, 25), audio.OutputWrites[^1]);
	}

	// ── Disconnected device ───────────────────────────────────────────────────

	// No endpoint resolved (a null from every read) must not throw, and must not leave a stale
	// reading on screen. The service itself never falls back to the DEFAULT endpoint, which would
	// move the user's speakers instead of their headset.
	[Fact]
	public async Task DisconnectedHeadsetDoesNotThrow()
	{
		var (vm, audio, _) = NewVm();
		audio.Output = null;
		audio.Mic = null;
		vm.Device = Cosmo();

		await vm.RebuildAudioSettingsAsync();
		vm.OutputVolumeSetting!.Value = 60;
		await vm.ApplyAudioSettingsAsync();

		// Reaching here at all is the assertion: no endpoint resolved and nothing threw.
		Assert.Equal(60, vm.OutputVolumeSetting.Value);
	}
}
