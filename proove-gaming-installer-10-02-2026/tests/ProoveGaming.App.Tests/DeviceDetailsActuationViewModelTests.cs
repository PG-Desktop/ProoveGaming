using System;
using System.Globalization;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using ProoveGaming.App.Models.Keyboards;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// The Actuation section's ViewModel logic: device-reported limits drive the sliders, values snap
// to the device step, the RT toggle gates the RT sliders, writes are debounced to one per settle,
// and a board whose probe says "unsupported" hides the section and is never written to.
//
// Devices are set via the plain Device property and the actuation load awaited directly (all
// fakes complete synchronously), NOT via SetDevice: its other fire-and-forget chains outlive the
// test on thread-pool threads and can steal Dispatcher.UIThread's first-touch affinity from the
// RunJobs()-based tests elsewhere in this assembly (see AssemblyInfo.cs).
public class DeviceDetailsActuationViewModelTests
{
	private const int CyrexVid = 10473;
	private const int CyrexPid = 12914;

	// Long enough for the 400 ms debounce to have fired even on a busy CI runner.
	private const int DebounceSettleMs = 800;

	// Polls for a debounced write to LAND instead of a fixed sleep — the same deflake the
	// preset change-then-revert tests got in #96. The fixed 800 ms is calibrated for a quiet
	// machine; a loaded CI runner can take longer and fail the run on timing alone
	// (run 31708127628, RapidSliderChanges; run 32113162948, ModeSwitchDuringInFlightWrite).
	// Two shapes are safe to poll: pure test-ENDING waits, and waits for a GATE-HELD write —
	// the fake blocks inside the write, so once it lands in the list nothing after it can run.
	// Where the sleep also gives time to steps AFTER the write lands (cache update, failure
	// re-read), the fixed sleep stays — a poll would return early and race those follow-ups.
	// Waits for a debounced edit to be CONSUMED, not merely for time to pass.
	//
	// Used AFTER the fixed settle, never instead of it — the same rule the preset tests' waiters
	// follow: the sleep keeps the full settle window for the steps around it, and the poll only
	// extends it when the runner is slow. The fixed sleep alone is a bet on wall-clock margin that
	// CI has now lost twice.
	private static void WaitUntilActuationEditSettled(DeviceDetailsViewModel vm, int timeoutMs = 5000)
	{
		var deadline = Environment.TickCount64 + timeoutMs;
		while (vm.HasPendingActuationEditForTests && Environment.TickCount64 < deadline)
			Thread.Sleep(25);
	}

	private static void WaitForWrites<T>(List<T> writes, int count = 1, int timeoutMs = 5000)
	{
		var deadline = Environment.TickCount64 + timeoutMs;
		while (writes.Count < count && Environment.TickCount64 < deadline)
			Thread.Sleep(50);
	}

	private sealed class FakeOwnerProvider(string kind, string id) : ICurrentOwnerProvider
	{
		private readonly SettingsOwner _owner = new(kind, id);
		public SettingsOwner? GetCurrent() => _owner;
	}

	private sealed class FakeHid : IUsbHidDeviceService
	{
		public KeyActuationInfo? ActuationInfo { get; set; }
		public bool ActuationWriteSucceeds { get; set; } = true;
		public int GetActuationCalls { get; private set; }
		public List<(int Press, int Release, bool Rt, int QuickPress, int QuickRelease)> ActuationWrites { get; } = [];

		public Task<KeyActuationInfo?> GetKeyActuationAsync(int vendorId, int productId)
		{
			GetActuationCalls++;
			return Task.FromResult(ActuationInfo);
		}

		public Task<bool> SetKeyActuationAsync(int vendorId, int productId, int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm)
		{
			ActuationWrites.Add((pressCentiMm, releaseCentiMm, rapidTrigger, quickPressCentiMm, quickReleaseCentiMm));
			return Task.FromResult(ActuationWriteSucceeds);
		}

		public List<(int[] Slots, int Press, int Release, bool Rt, int QuickPress, int QuickRelease)> PerKeyActuationWrites { get; } = [];

		// When set, per-key writes await this gate instead of completing synchronously — lets a
		// test hold a write "in flight" while it mutates the VM (the B1 regression scenario).
		public TaskCompletionSource<bool>? PerKeyWriteGate { get; set; }

		public Task<bool> SetPerKeyActuationAsync(int vendorId, int productId, IReadOnlyCollection<int> slots, int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm)
		{
			PerKeyActuationWrites.Add(([.. slots], pressCentiMm, releaseCentiMm, rapidTrigger, quickPressCentiMm, quickReleaseCentiMm));
			return PerKeyWriteGate?.Task ?? Task.FromResult(ActuationWriteSucceeds);
		}

		public List<HidDeviceResponse> GetDevices() => [];
		public Task StartCommunication() => Task.CompletedTask;
		public bool CheckIfAllDevicesAreDisconnected() => true;
		public void StopCommunication() { }
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
		public Task<bool> SetKeyMappingAsync(int vendorId, int productId, byte matrixIndex, byte type, byte code1, byte code2) => Task.FromResult(true);
		public Task<(IReadOnlyDictionary<byte, byte> ScancodeToSlot, IReadOnlyDictionary<byte, byte> UserOverrides)?> ProbeKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
			=> Task.FromResult<(IReadOnlyDictionary<byte, byte>, IReadOnlyDictionary<byte, byte>)?>(null);
		public Task<IReadOnlyDictionary<byte, (byte Type, byte Code1, byte Code2)>?> ReadKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
			=> Task.FromResult<IReadOnlyDictionary<byte, (byte, byte, byte)>?>(null);
		public bool TryGetLastDpiStage(int vendorId, int productId, string deviceKey, out int stageIndex) { stageIndex = 0; return false; }
		public bool TryGetLastReportRate(int vendorId, int productId, string deviceKey, out int hz) { hz = 0; return false; }

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

	// A Cyrex-shaped probe result: press range 0.20-3.50 mm, RT range 0.01-2.50 mm, step 0.01 mm.
	private static KeyActuationInfo CyrexInfo() => new(
		Supported: true,
		PressCentiMm: 200, ReleaseCentiMm: 200, RapidTrigger: false,
		QuickPressCentiMm: 10, QuickReleaseCentiMm: 10,
		PressMinCentiMm: 20, PressMaxCentiMm: 350,
		RtMinCentiMm: 1, RtMaxCentiMm: 250,
		StepMicrometres: 10,
		DefaultPressCentiMm: 200, DefaultReleaseCentiMm: 200, DefaultRapidTrigger: false,
		DefaultQuickPressCentiMm: 10, DefaultQuickReleaseCentiMm: 10);

	private static (DeviceDetailsViewModel Vm, FakeHid Hid) NewVm()
	{
		var hid = new FakeHid();
		var vm = new DeviceDetailsViewModel(
			usbHidDeviceService: hid,
			localStorage: new LocalStorageService("Data Source=:memory:"),
			settings: null,
			ownerProvider: new FakeOwnerProvider(LocalStorageService.OwnerKindGuest, Guid.NewGuid().ToString("D")));
		return (vm, hid);
	}

	private static SingleCardViewModel CyrexCard(bool supportsActuation = true) => new()
	{
		Name = "Cyrex",
		DeviceTypeLabel = "Wired Keyboard",
		VendorId = CyrexVid,
		ProductId = CyrexPid,
		SettingsVendorId = CyrexVid,
		SettingsProductId = CyrexPid,
		DeviceKey = "",
		SettingsDeviceKey = "",
		SupportsActuation = supportsActuation,
	};

	[Fact]
	public void LoadFromDevice_LoadsValuesAndLimitsFromTheProbe()
	{
		var (vm, hid) = NewVm();
		hid.ActuationInfo = CyrexInfo();

		vm.Device = CyrexCard();
		vm.LoadActuationFromDeviceAsync().GetAwaiter().GetResult();

		Assert.True(vm.IsActuationSupported);
		Assert.True(vm.IsActuationNavVisible);
		Assert.Equal(1, hid.GetActuationCalls);
		Assert.Equal(2.00, vm.ActuationPressMm, 3);
		Assert.Equal("2.00 mm", vm.ActuationPressDisplay);
		Assert.False(vm.ActuationRapidTrigger);
		// 200 within 20..350 -> (200-20)/(350-20) ≈ 54.5%.
		Assert.Equal((200 - 20) / 330.0 * 100.0, vm.ActuationPressPct, 3);
		// Loading device state is not a user edit — nothing may be written back.
		Assert.Empty(hid.ActuationWrites);
	}

	[Fact]
	public void ProbeSaysUnsupported_HidesTheSectionAndNeverWrites()
	{
		var (vm, hid) = NewVm();
		hid.ActuationInfo = CyrexInfo() with { Supported = false };

		vm.Device = CyrexCard();
		vm.LoadActuationFromDeviceAsync().GetAwaiter().GetResult();

		Assert.False(vm.IsActuationSupported);
		Assert.False(vm.IsActuationNavVisible);
		Assert.Empty(hid.ActuationWrites);
	}

	[Fact]
	public void CardWithoutActuation_NeverProbes()
	{
		var (vm, hid) = NewVm();
		hid.ActuationInfo = CyrexInfo();

		vm.Device = CyrexCard(supportsActuation: false);
		vm.LoadActuationFromDeviceAsync().GetAwaiter().GetResult();

		Assert.Equal(0, hid.GetActuationCalls);
		Assert.False(vm.IsActuationNavVisible);
	}

	[Fact]
	public void RtToggle_GatesTheRtControls()
	{
		var (vm, hid) = NewVm();
		hid.ActuationInfo = CyrexInfo();
		vm.Device = CyrexCard();
		vm.LoadActuationFromDeviceAsync().GetAwaiter().GetResult();

		Assert.False(vm.AreRtControlsEnabled);
		vm.ActuationRapidTrigger = true;
		Assert.True(vm.AreRtControlsEnabled);
	}

	// Thread.Sleep, not await Task.Delay: Dispatcher.UIThread has hard first-touch thread affinity,
	// and an async test's post-await continuation resumes on an arbitrary thread-pool thread —
	// which can steal dispatcher ownership out from under the RunJobs()-based tests elsewhere in
	// this assembly. Same rule DeviceDetailsPresetApplyTests documents for its debounce tests.
	[Fact]
	public void RapidSliderChanges_CoalesceIntoOneWriteWithTheFinalValues()
	{
		var (vm, hid) = NewVm();
		hid.ActuationInfo = CyrexInfo();
		vm.Device = CyrexCard();
		vm.LoadActuationFromDeviceAsync().GetAwaiter().GetResult();

		// A drag: many intermediate positions, ending at 1.00 mm (pct for 100 in 20..350).
		for (int i = 1; i <= 10; i++)
			vm.SetActuationPressFromFraction(i / 100.0 * 2.4242);
		vm.SetActuationPressFromFraction((100 - 20) / 330.0);

		WaitForWrites(hid.ActuationWrites);

		var write = Assert.Single(hid.ActuationWrites);
		Assert.Equal(100, write.Press);
		Assert.Equal(200, write.Release);
		Assert.False(write.Rt);
	}

	[Fact]
	public void SliderFullRight_ClampsToTheDeviceMaximum()
	{
		var (vm, hid) = NewVm();
		hid.ActuationInfo = CyrexInfo();
		vm.Device = CyrexCard();
		vm.LoadActuationFromDeviceAsync().GetAwaiter().GetResult();

		vm.SetActuationPressFromFraction(1.0);

		Assert.Equal(3.50, vm.ActuationPressMm, 3);
		WaitForWrites(hid.ActuationWrites);
		Assert.Equal(350, Assert.Single(hid.ActuationWrites).Press);
	}

	[Fact]
	public void RtToggle_WritesTheRtStateToTheDevice()
	{
		var (vm, hid) = NewVm();
		hid.ActuationInfo = CyrexInfo();
		vm.Device = CyrexCard();
		vm.LoadActuationFromDeviceAsync().GetAwaiter().GetResult();

		vm.ActuationRapidTrigger = true;

		WaitForWrites(hid.ActuationWrites);
		var write = Assert.Single(hid.ActuationWrites);
		Assert.True(write.Rt);
		Assert.Equal(10, write.QuickPress);
	}

	// ── Per-key mode ─────────────────────────────────────────────────────────────
	// The key layout is built via LoadKeyLayoutForDevice (synchronous, catalog-driven — same
	// pattern as the Cyrex key-assignment tests) so the overlay keys exist to click.

	private static KeyActuationInfo CyrexInfoWithPerKey(DeviceDetailsViewModel vm)
	{
		var perKey = new Dictionary<int, KeyActuationKeyValues>();
		foreach (var key in vm.PhysicalKeys)
			perKey[key.MatrixIndex] = new KeyActuationKeyValues(200, 200, false, 10, 10);
		return CyrexInfo() with { PerKeyValues = perKey };
	}

	private static (DeviceDetailsViewModel Vm, FakeHid Hid) NewPerKeyVm()
	{
		var (vm, hid) = NewVm();
		vm.Device = CyrexCard();
		vm.LoadKeyLayoutForDevice();
		hid.ActuationInfo = CyrexInfoWithPerKey(vm);
		vm.LoadActuationFromDeviceAsync().GetAwaiter().GetResult();
		vm.ActiveSection = NavSection.Actuation;
		return (vm, hid);
	}

	private static KeyAssignmentOptionViewModel Key(DeviceDetailsViewModel vm, string label) =>
		vm.PhysicalKeys.First(k => k.KeyLabel == label);

	[Fact]
	public void PerKeyMode_UnavailableWhenTheHostSendsNoPerKeyMap()
	{
		// An older DeviceHost leaves the map empty — the toggle must not render, and All-keys
		// editing stays exactly as before.
		var (vm, hid) = NewVm();
		vm.Device = CyrexCard();
		vm.LoadKeyLayoutForDevice();
		hid.ActuationInfo = CyrexInfo();
		vm.LoadActuationFromDeviceAsync().GetAwaiter().GetResult();

		Assert.True(vm.IsActuationSupported);
		Assert.False(vm.IsActuationPerKeyAvailable);
	}

	[Fact]
	public void PerKeyMode_AvailableWhenTheHostSendsThePerKeyMap()
	{
		var (vm, _) = NewPerKeyVm();

		Assert.True(vm.IsActuationPerKeyAvailable);
		Assert.False(vm.ActuationPerKeyMode);
	}

	[Fact]
	public void KeyClicks_MoveTheSingleSelection()
	{
		var (vm, _) = NewPerKeyVm();
		vm.ActuationPerKeyMode = true;

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "W"));
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "A"));

		// Single-select: clicking A moved the selection off W — only one key at a time.
		Assert.False(Key(vm, "W").IsSelected);
		Assert.True(Key(vm, "A").IsSelected);
		Assert.True(vm.HasActuationSelection);
		// The radio consumers' selection stays untouched — this is the Actuation branch.
		Assert.Null(vm.SelectedPhysicalKey);

		// Re-clicking the selected key steps back to the neutral no-selection state.
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "A"));

		Assert.False(Key(vm, "A").IsSelected);
		Assert.False(vm.HasActuationSelection);
	}

	[Fact]
	public void SelectedKeyLabel_FollowsTheSelection()
	{
		// The test host resolves no resources (GetString returns the key), so the label's exact
		// text is not assertable here — only its empty/non-empty lifecycle.
		var (vm, _) = NewPerKeyVm();
		vm.ActuationPerKeyMode = true;
		Assert.Equal(string.Empty, vm.ActuationSelectedKeysLabel);

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "W"));
		Assert.NotEqual(string.Empty, vm.ActuationSelectedKeysLabel);

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "W"));
		Assert.Equal(string.Empty, vm.ActuationSelectedKeysLabel);
	}

	[Fact]
	public void EmptySelection_DisablesTheControlsAndNothingIsEverWritten()
	{
		var (vm, hid) = NewPerKeyVm();
		vm.ActuationPerKeyMode = true;

		Assert.False(vm.AreActuationControlsEnabled);
		vm.SetActuationPressFromFraction(0.5);

		Thread.Sleep(DebounceSettleMs);
		Assert.Empty(hid.ActuationWrites);
		Assert.Empty(hid.PerKeyActuationWrites);
	}

	[Fact]
	public void PerKeyEdits_CoalesceIntoOnePerKeyWriteTargetingTheSelectedKey()
	{
		var (vm, hid) = NewPerKeyVm();
		vm.ActuationPerKeyMode = true;
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "W"));
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "A"));

		for (int i = 1; i <= 10; i++)
			vm.SetActuationPressFromFraction(i / 100.0 * 2.4242);
		vm.SetActuationPressFromFraction((100 - 20) / 330.0);

		WaitForWrites(hid.PerKeyActuationWrites);

		var write = Assert.Single(hid.PerKeyActuationWrites);
		Assert.Equal(100, write.Press);
		// The selection moved from W to A — the write targets A alone.
		Assert.Equal(new int[] { Key(vm, "A").MatrixIndex }, write.Slots);
		// The global write path is never used in Per-key mode.
		Assert.Empty(hid.ActuationWrites);
	}

	[Fact]
	public void SelectingASecondKey_FlushesThePendingEditToTheFirstKey()
	{
		var (vm, hid) = NewPerKeyVm();
		vm.ActuationPerKeyMode = true;
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "W"));

		// The edit is still inside its debounce window when the selection moves to A — it must
		// commit against W, not silently retarget to A.
		vm.SetActuationPressFromFraction((100 - 20) / 330.0); // 1.00 mm
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "A"));

		WaitForWrites(hid.PerKeyActuationWrites);
		var write = Assert.Single(hid.PerKeyActuationWrites);
		Assert.Equal(new int[] { Key(vm, "W").MatrixIndex }, write.Slots);
		Assert.Equal(100, write.Press);
	}

	[Fact]
	public void EmptySpaceDeselect_FlushesThePendingEditToTheDeselectedKey()
	{
		var (vm, hid) = NewPerKeyVm();
		vm.ActuationPerKeyMode = true;
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "W"));

		// Same flush discipline as a selection move (review round 4 B1): the edit is still in
		// its debounce window when the empty-space click clears the selection — it must commit
		// against W, not be dropped because nothing is selected when the debounce fires.
		vm.SetActuationPressFromFraction((100 - 20) / 330.0); // 1.00 mm
		vm.DeselectPhysicalKeyCommand.Execute(null);

		Assert.False(vm.HasActuationSelection);
		WaitForWrites(hid.PerKeyActuationWrites);
		var write = Assert.Single(hid.PerKeyActuationWrites);
		Assert.Equal(new int[] { Key(vm, "W").MatrixIndex }, write.Slots);
		Assert.Equal(100, write.Press);
	}

	[Fact]
	public void ClickingAKey_ShowsItsOwnValuesWithoutWriting()
	{
		var (vm, hid) = NewPerKeyVm();
		var q = Key(vm, "Q");
		hid.ActuationInfo = hid.ActuationInfo! with
		{
			PerKeyValues = new Dictionary<int, KeyActuationKeyValues>(hid.ActuationInfo!.PerKeyValues)
			{
				[q.MatrixIndex] = new KeyActuationKeyValues(50, 45, true, 3, 4),
			},
		};
		vm.LoadActuationFromDeviceAsync().GetAwaiter().GetResult();
		vm.ActiveSection = NavSection.Actuation;
		vm.ActuationPerKeyMode = true;

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Q"));

		Assert.Equal(0.50, vm.ActuationPressMm, 3);
		Assert.True(vm.ActuationRapidTrigger);
		Thread.Sleep(DebounceSettleMs);
		// Reflecting the device state is not an edit — nothing may be written back.
		Assert.Empty(hid.PerKeyActuationWrites);
		Assert.Empty(hid.ActuationWrites);
	}

	[Fact]
	public void SuccessfulPerKeyWrite_UpdatesTheLocalCacheTheNextClickReadsFrom()
	{
		var (vm, hid) = NewPerKeyVm();
		vm.ActuationPerKeyMode = true;
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Q"));

		vm.SetActuationPressFromFraction((100 - 20) / 330.0); // 1.00 mm
		Thread.Sleep(DebounceSettleMs);
		WaitForWrites(hid.PerKeyActuationWrites);
		Assert.Single(hid.PerKeyActuationWrites);

		// Deselect and re-select: the sliders must show the value just written, from the local
		// cache — no device round-trip happened in between (still one GetKeyActuation call).
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Q"));
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "W"));
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Q"));

		Assert.Equal(1.00, vm.ActuationPressMm, 3);
		Assert.Equal(1, hid.GetActuationCalls);
	}

	[Fact]
	public void FailedPerKeyWrite_RaisesTheBannerAndRereadsFromTheDevice()
	{
		var (vm, hid) = NewPerKeyVm();
		vm.ActuationPerKeyMode = true;
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Q"));
		hid.ActuationWriteSucceeds = false;

		vm.SetActuationPressFromFraction((100 - 20) / 330.0);
		Thread.Sleep(DebounceSettleMs);
		WaitForWrites(hid.PerKeyActuationWrites);

		Assert.Single(hid.PerKeyActuationWrites);
		Assert.True(vm.ActuationWriteFailed);
		// The re-read is what snaps the sliders back to device truth.
		Assert.Equal(2, hid.GetActuationCalls);
		Assert.Equal(2.00, vm.ActuationPressMm, 3);
	}

	// ── B1 regression: a flush during an in-flight write must keep the edit's target ──
	// The follow-up write is a snapshot taken when the edit left its debounce window; without
	// that, switching to All keys (or another section) while a write was on the wire made the
	// follow-up re-read the NEW mode and flatten the whole board with a single-key edit.

	[Fact]
	public void ModeSwitchDuringInFlightWrite_KeepsThePendingEditTargetedAtItsKeys()
	{
		var (vm, hid) = NewPerKeyVm();
		vm.ActuationPerKeyMode = true;
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "W"));

		var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		hid.PerKeyWriteGate = gate;
		vm.SetActuationPressFromFraction((100 - 20) / 330.0); // 1.00 mm
		// Polled, not slept (run 32113162948 lost this race on CI): the gate holds write #1, so
		// once it lands in the list the state is stable — nothing after it can run yet.
		WaitForWrites(hid.PerKeyActuationWrites);
		Assert.Single(hid.PerKeyActuationWrites);

		vm.SetActuationPressFromFraction((120 - 20) / 330.0); // second edit, still in its debounce window
		vm.ActuationPerKeyMode = false;                       // flush fires while #1 is in flight

		gate.SetResult(true);
		WaitForWrites(hid.PerKeyActuationWrites, 2);

		// The flushed edit stays a per-key write to W with the edit's own value — the global
		// path never fires, so the rest of the board keeps its per-key setup.
		Assert.Empty(hid.ActuationWrites);
		Assert.Equal(2, hid.PerKeyActuationWrites.Count);
		Assert.Equal(new int[] { Key(vm, "W").MatrixIndex }, hid.PerKeyActuationWrites[1].Slots);
		Assert.Equal(120, hid.PerKeyActuationWrites[1].Press);
	}

	[Fact]
	public void SectionSwitchDuringInFlightWrite_NeverIssuesAGlobalWrite()
	{
		var (vm, hid) = NewPerKeyVm();
		vm.ActuationPerKeyMode = true;
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "W"));

		var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		hid.PerKeyWriteGate = gate;
		vm.SetActuationPressFromFraction((100 - 20) / 330.0);
		WaitForWrites(hid.PerKeyActuationWrites);             // gate-held: stable once landed
		Assert.Single(hid.PerKeyActuationWrites);

		vm.SetActuationPressFromFraction((120 - 20) / 330.0);
		vm.ActiveSection = NavSection.Presets;                // forces Per-key mode off mid-flight

		gate.SetResult(true);
		WaitForWrites(hid.PerKeyActuationWrites, 2);

		Assert.Empty(hid.ActuationWrites);
		Assert.Equal(2, hid.PerKeyActuationWrites.Count);
		Assert.Equal(new int[] { Key(vm, "W").MatrixIndex }, hid.PerKeyActuationWrites[1].Slots);
	}

	// ── M1 regression: queued writes with different targets must all survive ──
	// Blanket latest-wins was safe while every write hit the whole board; with per-key targets,
	// replacing the queued write dropped the OTHER key's edit without a trace — no banner, no log,
	// and the sliders honestly showed the value that never got written.

	[Fact]
	public void EditsToDifferentKeys_QueuedBehindAnInFlightWrite_AllReachTheDevice()
	{
		var (vm, hid) = NewPerKeyVm();
		vm.ActuationPerKeyMode = true;
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Q"));

		var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		hid.PerKeyWriteGate = gate;
		vm.SetActuationPressFromFraction((100 - 20) / 330.0); // 1.00 mm
		WaitForWrites(hid.PerKeyActuationWrites);             // write #1 (Q) in flight, held by the gate
		Assert.Single(hid.PerKeyActuationWrites);

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "W"));
		vm.SetActuationPressFromFraction((120 - 20) / 330.0);
		Thread.Sleep(DebounceSettleMs);                       // request(W) queued

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "A"));
		vm.SetActuationPressFromFraction((140 - 20) / 330.0);
		Thread.Sleep(DebounceSettleMs);                       // request(A) queued — must not evict W

		gate.SetResult(true);
		WaitForWrites(hid.PerKeyActuationWrites, 3);

		Assert.Equal(3, hid.PerKeyActuationWrites.Count);
		Assert.Equal(new int[] { Key(vm, "Q").MatrixIndex }, hid.PerKeyActuationWrites[0].Slots);
		Assert.Equal(new int[] { Key(vm, "W").MatrixIndex }, hid.PerKeyActuationWrites[1].Slots);
		Assert.Equal(new int[] { Key(vm, "A").MatrixIndex }, hid.PerKeyActuationWrites[2].Slots);
		Assert.Equal(120, hid.PerKeyActuationWrites[1].Press);
		Assert.Equal(140, hid.PerKeyActuationWrites[2].Press);
	}

	[Fact]
	public void RepeatedEditsOfTheSameKey_StillCoalesceIntoOneQueuedWrite()
	{
		var (vm, hid) = NewPerKeyVm();
		vm.ActuationPerKeyMode = true;
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Q"));

		var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		hid.PerKeyWriteGate = gate;
		vm.SetActuationPressFromFraction((100 - 20) / 330.0);
		WaitForWrites(hid.PerKeyActuationWrites);             // write #1 (Q) in flight, gate-held
		Assert.Single(hid.PerKeyActuationWrites);

		// Three settled edits of the same key while #1 is on the wire: latest wins, one write.
		for (int press = 110; press <= 130; press += 10)
		{
			vm.SetActuationPressFromFraction((press - 20) / 330.0);
			Thread.Sleep(DebounceSettleMs);
			WaitUntilActuationEditSettled(vm);
		}

		// The gate must not open until the LAST edit has been consumed by the debounce. Releasing
		// while one is still pending lets it fire after the queued write has gone out, producing a
		// third write — which is exactly what this test exists to rule out.
		gate.SetResult(true);
		WaitForWrites(hid.PerKeyActuationWrites, 2);

		Assert.Equal(2, hid.PerKeyActuationWrites.Count);
		Assert.Equal(new int[] { Key(vm, "Q").MatrixIndex }, hid.PerKeyActuationWrites[1].Slots);
		Assert.Equal(130, hid.PerKeyActuationWrites[1].Press);
	}

	// ── S1 regression: the radio and actuation selections share IsSelected ──

	[Fact]
	public void EnteringPerKeyMode_ClearsTheRadioGhostHighlight_AndLeavingRestoresIt()
	{
		var (vm, _) = NewPerKeyVm();
		// Radio-select W first (mode off -> the Key-Assignments/Macros radio path).
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "W"));
		Assert.True(Key(vm, "W").IsSelected);
		Assert.Same(Key(vm, "W"), vm.SelectedPhysicalKey);

		vm.ActuationPerKeyMode = true;
		Assert.False(Key(vm, "W").IsSelected); // no ghost on the actuation overlay

		vm.ActuationPerKeyMode = false;
		Assert.True(Key(vm, "W").IsSelected);  // radio highlight restored
		Assert.Same(Key(vm, "W"), vm.SelectedPhysicalKey);
	}

	[Fact]
	public void LeavingTheActuationSection_DropsPerKeyModeAndSelection()
	{
		var (vm, _) = NewPerKeyVm();
		vm.ActuationPerKeyMode = true;
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "W"));

		vm.ActiveSection = NavSection.Presets;

		Assert.False(vm.ActuationPerKeyMode);
		Assert.False(vm.HasActuationSelection);
		Assert.False(Key(vm, "W").IsSelected);
		// Back in Key Assignments, a click radio-selects again.
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "W"));
		Assert.Same(Key(vm, "W"), vm.SelectedPhysicalKey);
	}

	// The actuation displays render "2.00 mm" on every machine, not "2,00 mm" on comma-decimal
	// locales. Without this, a revert to a plain interpolation passes for anyone on en-US and fails
	// only for colleagues on uk-UA / de-DE — which is exactly how it shipped: LoadFromDevice above
	// failed on a comma-decimal machine while passing everywhere it was authored.
	[Theory]
	[InlineData("uk-UA")]
	[InlineData("de-DE")]
	[InlineData("fr-FR")]
	[InlineData("en-US")]
	public void ActuationDisplays_UseAnInvariantDecimalPoint(string culture)
	{
		var original = CultureInfo.CurrentCulture;
		try
		{
			CultureInfo.CurrentCulture = new CultureInfo(culture);

			var (vm, hid) = NewVm();
			hid.ActuationInfo = CyrexInfo();
			vm.Device = CyrexCard();
			vm.LoadActuationFromDeviceAsync().GetAwaiter().GetResult();

			Assert.Equal("2.00 mm", vm.ActuationPressDisplay);
			Assert.Equal("2.00 mm", vm.ActuationReleaseDisplay);
			Assert.DoesNotContain(",", vm.ActuationQuickPressDisplay);
			Assert.DoesNotContain(",", vm.ActuationQuickReleaseDisplay);
		}
		finally
		{
			// Restored on every path: CurrentCulture is ambient, and leaking it would make whichever
			// test xUnit runs next on this thread fail for reasons that have nothing to do with it.
			CultureInfo.CurrentCulture = original;
		}
	}

	// ── Key hover tooltip ────────────────────────────────────────────────────────
	// The overlay's per-key tooltip shows the key's press actuation under its name; on a device
	// without actuation the value line is null and the tooltip collapses to the name alone.

	[Fact]
	public void KeyTooltip_ShowsThePerKeyPressValue()
	{
		var (vm, hid) = NewVm();
		vm.Device = CyrexCard();
		vm.LoadKeyLayoutForDevice();

		var key = vm.PhysicalKeys.First();
		var perKey = new Dictionary<int, KeyActuationKeyValues>();
		foreach (var k in vm.PhysicalKeys)
			perKey[k.MatrixIndex] = new KeyActuationKeyValues(200, 200, false, 10, 10);
		perKey[key.MatrixIndex] = new KeyActuationKeyValues(208, 200, false, 10, 10);
		hid.ActuationInfo = CyrexInfo() with { PerKeyValues = perKey };
		vm.LoadActuationFromDeviceAsync().GetAwaiter().GetResult();

		Assert.Equal("2.08", vm.GetKeyActuationTooltip(key));
	}

	[Fact]
	public void KeyTooltip_FallsBackToTheUniformValue_WithoutAPerKeyMap()
	{
		var (vm, hid) = NewVm();
		vm.Device = CyrexCard();
		vm.LoadKeyLayoutForDevice();
		hid.ActuationInfo = CyrexInfo();
		vm.LoadActuationFromDeviceAsync().GetAwaiter().GetResult();

		Assert.Equal("2.00", vm.GetKeyActuationTooltip(vm.PhysicalKeys.First()));
	}

	[Fact]
	public void KeyTooltip_IsNull_WhenActuationIsUnsupported()
	{
		var (vm, hid) = NewVm();
		vm.Device = CyrexCard();
		vm.LoadKeyLayoutForDevice();
		hid.ActuationInfo = CyrexInfo() with { Supported = false };
		vm.LoadActuationFromDeviceAsync().GetAwaiter().GetResult();

		Assert.Null(vm.GetKeyActuationTooltip(vm.PhysicalKeys.First()));
	}
}
