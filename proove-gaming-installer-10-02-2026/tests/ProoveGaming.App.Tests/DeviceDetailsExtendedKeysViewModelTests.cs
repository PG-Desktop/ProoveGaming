using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.App.ViewModels;
using ProoveGaming.App.Models.Keyboards;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;

namespace ProoveGaming.App.Tests;

// The Extended Keys section's ViewModel logic (docs/plans/add-cyrex-extended-keys): the tri-state
// nav visibility, the Rapid Snap add editor and its refusals, the write path (one SetExtendedKeys
// with the FULL set, failure → banner + reload), the preset push's equal-set skip, and the two seams
// into Key Assignments — a key inside a record blocks the dialog, and the slot push skips its slots.
//
// Same harness as the actuation tests: Device set via the plain property, the key layout built with
// LoadKeyLayoutForDevice, the probe awaited directly. Localised strings resolve to their keys here
// (no Avalonia app), so messages are asserted for presence, not wording.
public class DeviceDetailsExtendedKeysViewModelTests
{
	private const int CyrexVid = 10473;
	private const int CyrexPid = 12914;
	private const int SlotA = 31;
	private const int SlotD = 33;
	// The free key between them — the one the refusals must leave alone.
	private const int SlotS = 32;
	private const string DeviceKey = "";

	private sealed class FakeOwnerProvider(string kind, string id) : ICurrentOwnerProvider
	{
		private readonly SettingsOwner _owner = new(kind, id);
		public SettingsOwner? GetCurrent() => _owner;
	}

	private sealed class FakeHid : IUsbHidDeviceService
	{
		public ExtendedKeysInfo? Info { get; set; }
		public bool WriteSucceeds { get; set; } = true;
		public int GetExtendedKeysCalls { get; private set; }
		public List<IReadOnlyList<ExtendedKeyRecord>> ExtendedKeysWrites { get; } = [];
		public Dictionary<byte, (byte Type, byte Code1, byte Code2)>? Matrix { get; set; }
		public List<(byte MatrixIndex, byte Type, byte Code1, byte Code2)> KeyWrites { get; } = [];

		// Test seam for the failed-write case: the probe signals that it was entered (ProbeEntered)
		// and then parks on ProbeGate. The reload a refused write triggers is the only place the
		// narrower, banner-preserving reset is observable — once the reload returns, its own probe
		// has put everything back and "what survived the reset" is no longer answerable.
		public TaskCompletionSource? ProbeEntered { get; set; }
		public Task? ProbeGate { get; set; }

		public async Task<ExtendedKeysInfo?> GetExtendedKeysAsync(int vendorId, int productId)
		{
			GetExtendedKeysCalls++;
			ProbeEntered?.TrySetResult();
			if (ProbeGate is { } gate) await gate;
			return Info;
		}

		// What the board reports on the read AFTER a refused write, when set: a write that tore the
		// two-region layout leaves a board the next read cannot parse, so the host resets the advanced
		// keys to answer at all and says so with Repaired (review round 4, m3). Left null by every
		// case that does not care, which keeps the previous set in place as before.
		public ExtendedKeysInfo? InfoAfterFailedWrite { get; set; }

		// The write's own park seam, twin of ProbeEntered / ProbeGate above. A real SetExtendedKeys is
		// a 1-2 s flash write, and the preset-race case (review round 4, m2) is only reachable while
		// one is genuinely in flight.
		public TaskCompletionSource? WriteEntered { get; set; }
		public Task? WriteGate { get; set; }

		public async Task<bool> SetExtendedKeysAsync(int vendorId, int productId, IReadOnlyList<ExtendedKeyRecord> entries)
		{
			ExtendedKeysWrites.Add(entries.ToList());
			// Read BEFORE the entry signal: the test clears the gate as soon as it is told the write
			// started, and a gate read after that would be null and the write would never park.
			var gate = WriteGate;
			WriteEntered?.TrySetResult();
			if (gate is not null) await gate;
			// The fake board keeps what it accepted, so a failure-reload sees the previous set.
			if (WriteSucceeds && Info is not null)
				Info = Info with { Entries = entries.ToList() };
			else if (!WriteSucceeds && InfoAfterFailedWrite is { } torn)
				Info = torn;
			return WriteSucceeds;
		}

		public Task<IReadOnlyDictionary<byte, (byte Type, byte Code1, byte Code2)>?> ReadKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
			=> Task.FromResult<IReadOnlyDictionary<byte, (byte, byte, byte)>?>(Matrix is null ? null : new Dictionary<byte, (byte, byte, byte)>(Matrix));

		public Task<bool> SetKeyMappingAsync(int vendorId, int productId, byte matrixIndex, byte type, byte code1, byte code2)
		{
			KeyWrites.Add((matrixIndex, type, code1, code2));
			if (Matrix is not null) Matrix[matrixIndex] = (type, code1, code2);
			return Task.FromResult(true);
		}

		public Task<KeyActuationInfo?> GetKeyActuationAsync(int vendorId, int productId) => Task.FromResult<KeyActuationInfo?>(null);
		public Task<bool> SetKeyActuationAsync(int vendorId, int productId, int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm) => Task.FromResult(false);
		public Task<bool> SetPerKeyActuationAsync(int vendorId, int productId, IReadOnlyCollection<int> slots, int pressCentiMm, int releaseCentiMm, bool rapidTrigger, int quickPressCentiMm, int quickReleaseCentiMm) => Task.FromResult(false);
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
		// The macro-library upload, and whether it succeeds. Failing it is what parks the resync at
		// its early return with the previous slot set still in place (review round 3, S6).
		public bool SetMacrosSucceeds { get; set; } = true;
		public int SetMacrosCalls { get; private set; }
		public Task<bool> SetMacrosAsync(int vendorId, int productId, string deviceKey, IReadOnlyList<(int Index, MacroRepeatMode Mode, IReadOnlyList<MacroEvent> Events, int RepeatCount)> entries)
		{
			SetMacrosCalls++;
			return Task.FromResult(SetMacrosSucceeds);
		}
		public Task<(IReadOnlyDictionary<byte, byte> ScancodeToSlot, IReadOnlyDictionary<byte, byte> UserOverrides)?> ProbeKeyMatrixAsync(int vendorId, int productId, int maxSlot = 128)
			=> Task.FromResult<(IReadOnlyDictionary<byte, byte>, IReadOnlyDictionary<byte, byte>)?>(null);
		public bool TryGetLastDpiStage(int vendorId, int productId, string deviceKey, out int stageIndex) { stageIndex = 0; return false; }
		public bool TryGetLastReportRate(int vendorId, int productId, string deviceKey, out int hz) { hz = 0; return false; }

#pragma warning disable CS0067 // events required by the interface but unused in tests
		public event EventHandler<SubscribeOnDevicesResponse>? DeviceFound;
		public event EventHandler<DpiStageChange>? DpiStageChanged;
		public event EventHandler<ReportRateChange>? ReportRateChanged;
		public event EventHandler<BatteryChange>? BatteryChanged;
		public event EventHandler<string>? NotifyError;
		public event EventHandler<bool>? HostAvailabilityChanged;
#pragma warning restore CS0067
	}

	private static ExtendedKeysInfo Info(params ExtendedKeyRecord[] entries) =>
		new(Supported: true, Entries: entries, AreaBytes: 1024, FreeBytes: 1024 - entries.Length * 8, BlockedSlots: []);

	private static (DeviceDetailsViewModel Vm, LocalStorageService Store, FakeHid Hid, string Owner) NewVm()
	{
		var owner = Guid.NewGuid().ToString("D");
		var store = new LocalStorageService("Data Source=:memory:");
		var hid = new FakeHid();
		var vm = new DeviceDetailsViewModel(
			usbHidDeviceService: hid,
			localStorage: store,
			settings: null,
			ownerProvider: new FakeOwnerProvider(LocalStorageService.OwnerKindGuest, owner));
		vm.PostToUi = action => action();
		return (vm, store, hid, owner);
	}

	// supportsMacros is off by default because almost nothing here needs it; the macro-resync cases
	// (review round 3, S6) turn it on, since IsMacrosSupported gates ResyncKeyMacroBindingsAsync.
	private static SingleCardViewModel CyrexCard(bool supportsExtendedKeys = true, bool supportsMacros = false) => new()
	{
		Name = "Cyrex",
		DeviceTypeLabel = "Wired Keyboard",
		VendorId = CyrexVid,
		ProductId = CyrexPid,
		SettingsVendorId = CyrexVid,
		SettingsProductId = CyrexPid,
		DeviceKey = DeviceKey,
		SettingsDeviceKey = DeviceKey,
		SupportsExtendedKeys = supportsExtendedKeys,
		SupportsMacros = supportsMacros,
	};

	// Device + key layout + the probe, without SetDevice's unrelated side effects (SQLite-backed
	// presets, button bindings, persisted state).
	private static async Task Open(
		DeviceDetailsViewModel vm, FakeHid hid, ExtendedKeysInfo? info,
		bool supportsExtendedKeys = true, bool supportsMacros = false)
	{
		hid.Info = info;
		vm.Device = CyrexCard(supportsExtendedKeys, supportsMacros);
		vm.LoadKeyLayoutForDevice();
		await vm.LoadExtendedKeysFromDeviceAsync();
	}

	private static KeyAssignmentOptionViewModel Key(DeviceDetailsViewModel vm, string name) =>
		vm.PhysicalKeys.Single(k => k.DevKeyName == name);

	private static async Task AddPair(DeviceDetailsViewModel vm, string key1, string key2)
	{
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartRapidSnapEditorCommand.Execute(null);
		vm.SelectExtendedKey(Key(vm, key1));
		vm.SelectExtendedKey(Key(vm, key2));
		await vm.ConfirmExtendedKeyCommand.ExecuteAsync(null);
	}

	private static List<ExtendedKeyRecord> Live(LocalStorageService s, string owner) =>
		s.GetDeviceExtendedKeys(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey);

	private const int SlotE = 18;
	private const int SlotR = 19;
	// The literal the other cases spell as 16 (SlotW, its partner, is declared with the Toggle set).
	private const int SlotQ = 16;

	private static void PickSocdPriority(DeviceDetailsViewModel vm, int priority) =>
		vm.SocdPriorityOptions.Single(o => o.Priority == priority).IsSelected = true;

	// ── SOCD (docs/plans/add-extended-key-socd) ───────────────────────────────

	[Fact]
	public async Task Socd_EditorOpensWithLastInputPreselected_AndConfirmWritesPriorityZero()
	{
		var (vm, store, hid, owner) = NewVm();
		await Open(vm, hid, Info());
		vm.ActiveSection = NavSection.ExtendedKeys;

		vm.StartSocdEditorCommand.Execute(null);
		Assert.True(vm.IsExtendedKeyEditorOpen);
		Assert.True(vm.IsExtendedKeyEditorSocd);
		Assert.Equal(SocdPriority.LastInput, vm.SocdPriority);
		Assert.Equal(4, vm.SocdPriorityOptions.Count);
		Assert.True(vm.SocdPriorityOptions[0].IsSelected);
		Assert.False(vm.CanConfirmExtendedKey);

		vm.SelectExtendedKey(Key(vm, "E"));
		vm.SelectExtendedKey(Key(vm, "R"));
		await vm.ConfirmExtendedKeyCommand.ExecuteAsync(null);

		var write = Assert.Single(hid.ExtendedKeysWrites);
		Assert.Equal([ExtendedKeyRecord.Socd(SlotE, SlotR, SocdPriority.LastInput)], write);
		Assert.Equal(write, Live(store, owner));
		var row = Assert.Single(vm.ExtendedKeys);
		Assert.Equal("E", row.Key1Label);
		Assert.Equal("R", row.Key2Label);
		Assert.True(row.HasParameterText);
		Assert.False(row.HasKey1Badge);
		Assert.False(row.HasKey2Badge);
		Assert.False(vm.IsExtendedKeyEditorOpen);
	}

	[Fact]
	public async Task Socd_AbsolutePriorityFollowsThePhysicalKey_WhenThePairIsNormalised()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info());
		vm.ActiveSection = NavSection.ExtendedKeys;

		// Key 1 = R (the higher slot), Key 2 = E; "Key 1 wins" must survive the slot swap as "Key 2 wins".
		vm.StartSocdEditorCommand.Execute(null);
		vm.SelectExtendedKey(Key(vm, "R"));
		vm.SelectExtendedKey(Key(vm, "E"));
		PickSocdPriority(vm, SocdPriority.Key1);
		Assert.Equal(SocdPriority.Key1, vm.SocdPriority);
		Assert.Equal("R", vm.SocdPriorityOptions[2].KeyLabel);
		Assert.Equal("E", vm.SocdPriorityOptions[3].KeyLabel);
		await vm.ConfirmExtendedKeyCommand.ExecuteAsync(null);

		var record = Assert.Single(Assert.Single(hid.ExtendedKeysWrites));
		Assert.Equal(new ExtendedKeyRecord(ExtendedKeyMode.Socd, SlotE, SlotR, SocdPriority.Key2, 0, ExtendedKeyCodeRecord.None, ExtendedKeyCodeRecord.None), record);
		var row = Assert.Single(vm.ExtendedKeys);
		Assert.False(row.HasParameterText);
		Assert.False(row.HasKey1Badge);
		Assert.True(row.HasKey2Badge);
	}

	[Fact]
	public async Task Socd_PriorityResetsToLastInput_OnEveryEditorOpen()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info());
		vm.ActiveSection = NavSection.ExtendedKeys;

		vm.StartSocdEditorCommand.Execute(null);
		PickSocdPriority(vm, SocdPriority.MutualCancel);
		Assert.Equal(SocdPriority.MutualCancel, vm.SocdPriority);
		Assert.False(vm.SocdPriorityOptions[0].IsSelected);
		vm.CancelExtendedKeyEditorCommand.Execute(null);

		vm.StartSocdEditorCommand.Execute(null);
		Assert.Equal(SocdPriority.LastInput, vm.SocdPriority);
		Assert.True(vm.SocdPriorityOptions[0].IsSelected);
		Assert.False(vm.SocdPriorityOptions[1].IsSelected);
	}

	[Fact]
	public async Task Socd_AKeyInARapidSnapPairIsRefused_AndTheReverse()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info(ExtendedKeyRecord.RapidSnap(SlotA, SlotD), ExtendedKeyRecord.Socd(SlotE, SlotR, SocdPriority.LastInput)));
		vm.ActiveSection = NavSection.ExtendedKeys;

		// A is in the RS pair: the SOCD editor refuses it.
		vm.StartSocdEditorCommand.Execute(null);
		await AssertRefused(vm, hid, Key(vm, "A"));
		vm.CancelExtendedKeyEditorCommand.Execute(null);

		// E is in the SOCD pair: the RS editor refuses it.
		vm.StartRapidSnapEditorCommand.Execute(null);
		await AssertRefused(vm, hid, Key(vm, "E"));
	}

	[Fact]
	public async Task Socd_PresetPushCarriesThePriority_AndSkipsAnEqualSet()
	{
		var (vm, store, hid, owner) = NewVm();
		await Open(vm, hid, Info(ExtendedKeyRecord.Socd(SlotE, SlotR, SocdPriority.LastInput)));

		// The preset holds the same pair with a different priority: that is a different record.
		store.ReplaceDeviceExtendedKeys(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
			[ExtendedKeyRecord.Socd(SlotE, SlotR, SocdPriority.Key2)]);
		Assert.True(await vm.ApplyStoredExtendedKeysToDeviceAsync());
		Assert.Equal([ExtendedKeyRecord.Socd(SlotE, SlotR, SocdPriority.Key2)], Assert.Single(hid.ExtendedKeysWrites));
		var row = Assert.Single(vm.ExtendedKeys);
		Assert.True(row.HasKey2Badge);

		// Board now matches priority and all: no second write.
		Assert.False(await vm.ApplyStoredExtendedKeysToDeviceAsync());
		Assert.Single(hid.ExtendedKeysWrites);
	}

	[Fact]
	public async Task Socd_OptionCards_NameTheFavouredKey_OnceItIsPicked()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info());
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartSocdEditorCommand.Execute(null);

		var options = vm.SocdPriorityOptions;
		Assert.Equal([0, 1, 2, 3], options.Select(o => o.Priority).ToArray());
		// Rule-based cards carry no key line; the absolute ones fall back to the "Key 1" / "Key 2" text.
		Assert.False(options[0].HasKeyLabel);
		Assert.False(options[1].HasKeyLabel);
		Assert.True(options[2].HasKeyLabel);
		Assert.True(options[3].HasKeyLabel);
		Assert.NotEqual(options[2].KeyLabel, options[3].KeyLabel);
		Assert.All(options, o => Assert.False(string.IsNullOrEmpty(o.Label)));
		Assert.All(options, o => Assert.False(string.IsNullOrEmpty(o.Tooltip)));
		var fallback1 = options[2].KeyLabel;

		vm.SelectExtendedKey(Key(vm, "E"));
		Assert.Equal("E", options[2].KeyLabel);
		Assert.NotEqual("E", options[3].KeyLabel);

		vm.SelectExtendedKey(Key(vm, "R"));
		Assert.Equal("R", options[3].KeyLabel);

		// Clearing Key 1 returns its card to the fallback text.
		vm.ClearExtendedKeyEditorKey1Command.Execute(null);
		Assert.Equal(fallback1, options[2].KeyLabel);
		Assert.Equal("R", options[3].KeyLabel);
	}

	[Fact]
	public async Task Socd_RowsFromTheBoard_FormatEachPriority()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info(
			ExtendedKeyRecord.Socd(SlotA, SlotD, SocdPriority.MutualCancel),
			ExtendedKeyRecord.Socd(SlotE, SlotR, SocdPriority.Key1)));

		Assert.Equal(2, vm.ExtendedKeys.Count);
		var mutual = vm.ExtendedKeys.Single(r => r.Record.Slot1 == SlotA);
		Assert.True(mutual.HasParameterText);
		Assert.False(mutual.HasKey1Badge);
		Assert.False(mutual.HasKey2Badge);
		var absolute = vm.ExtendedKeys.Single(r => r.Record.Slot1 == SlotE);
		Assert.False(absolute.HasParameterText);
		Assert.True(absolute.HasKey1Badge);
		Assert.False(absolute.HasKey2Badge);
		Assert.NotEqual(string.Empty, absolute.Key1BadgeText);
		// Every key of both pairs is lit and protected, whatever the mode.
		Assert.True(Key(vm, "E").IsExtendedKeyHighlighted);
		Assert.True(vm.IsExtendedKeySlot(SlotR));
		Assert.NotNull(vm.GetExtendedKeyTooltip(Key(vm, "R")));
	}

	// ── Nav visibility (the tri-state probe) ──────────────────────────────────

	[Fact]
	public async Task Nav_HiddenWithoutTheCatalogFlag_RegardlessOfTheProbe()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info(), supportsExtendedKeys: false);

		Assert.False(vm.IsExtendedKeysNavVisible);
		Assert.Equal(0, hid.GetExtendedKeysCalls);
	}

	[Fact]
	public async Task Nav_StaysVisibleAndPending_WhenTheRpcFails()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, info: null);

		Assert.True(vm.IsExtendedKeysNavVisible);
		Assert.True(vm.IsExtendedKeysProbePending);
		Assert.False(vm.IsExtendedKeysSupported);
		// Nothing is ever written while the probe is pending.
		await vm.ApplyStoredExtendedKeysToDeviceAsync();
		Assert.Empty(hid.ExtendedKeysWrites);
	}

	[Fact]
	public async Task Nav_HidesAndLeavesTheSection_OnAHardNoFromTheDevice()
	{
		var (vm, _, hid, _) = NewVm();
		vm.Device = CyrexCard();
		vm.LoadKeyLayoutForDevice();
		vm.ActiveSection = NavSection.ExtendedKeys;
		hid.Info = new ExtendedKeysInfo(false, [], 0, 0, []);

		await vm.LoadExtendedKeysFromDeviceAsync();

		Assert.False(vm.IsExtendedKeysNavVisible);
		Assert.NotEqual(NavSection.ExtendedKeys, vm.ActiveSection);
		Assert.Empty(hid.ExtendedKeysWrites);
	}

	[Fact]
	public async Task Nav_VisibleAndSupported_CountsInTheKeyboardNav_AndShowsThePairModes()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info());

		Assert.True(vm.IsExtendedKeysNavVisible);
		Assert.True(vm.IsExtendedKeysSupported);
		Assert.True(vm.IsRapidSnapModeAvailable);
		Assert.True(vm.IsSocdModeAvailable);
		Assert.True(vm.IsModTapModeAvailable);
		Assert.True(vm.IsToggleModeAvailable);
		Assert.True(vm.HasNoExtendedKeys);
	}

	// ── Load → rows ───────────────────────────────────────────────────────────

	[Fact]
	public async Task Load_BuildsARowPerRecord_WithLabelsBadgeAndHighlights_AndPersistsNothing()
	{
		var (vm, store, hid, owner) = NewVm();
		await Open(vm, hid, Info(ExtendedKeyRecord.RapidSnap(SlotA, SlotD)));

		var row = Assert.Single(vm.ExtendedKeys);
		Assert.Equal("A", row.Key1Label);
		Assert.Equal("D", row.Key2Label);
		Assert.True(row.HasKey2);
		Assert.False(row.HasParameterText);
		Assert.NotEqual(string.Empty, row.BadgeText);
		Assert.True(Key(vm, "A").IsExtendedKeyHighlighted);
		Assert.True(Key(vm, "D").IsExtendedKeyHighlighted);
		Assert.False(Key(vm, "S").IsExtendedKeyHighlighted);
		Assert.True(vm.IsExtendedKeySlot(SlotA));
		Assert.False(vm.IsExtendedKeySlot(32));
		// A device read is not a user edit — the live rows stay empty (no phantom Save Preset).
		Assert.Empty(Live(store, owner));
	}

	// ── The add editor ────────────────────────────────────────────────────────

	[Fact]
	public async Task AddFlow_PicksKey1ThenKey2_ConfirmWritesTheFullSetOnce_AndClosesTheEditor()
	{
		var (vm, store, hid, owner) = NewVm();
		await Open(vm, hid, Info(ExtendedKeyRecord.RapidSnap(16, 17)));
		vm.ActiveSection = NavSection.ExtendedKeys;

		vm.StartRapidSnapEditorCommand.Execute(null);
		Assert.True(vm.IsExtendedKeyEditorOpen);
		Assert.False(vm.IsExtendedKeyModeListVisible);
		Assert.True(vm.IsExtendedKeyEditorKey1Active);
		Assert.False(vm.CanConfirmExtendedKey);

		vm.SelectExtendedKey(Key(vm, "A"));
		Assert.Equal("A", vm.ExtendedKeyEditorKey1Label);
		Assert.True(vm.IsExtendedKeyEditorKey2Active);
		Assert.True(Key(vm, "A").IsSelected);
		Assert.False(vm.CanConfirmExtendedKey);

		vm.SelectExtendedKey(Key(vm, "D"));
		Assert.Equal("D", vm.ExtendedKeyEditorKey2Label);
		Assert.True(vm.CanConfirmExtendedKey);

		await vm.ConfirmExtendedKeyCommand.ExecuteAsync(null);

		// One call, carrying the existing record AND the new one, in canonical order.
		var write = Assert.Single(hid.ExtendedKeysWrites);
		Assert.Equal([ExtendedKeyRecord.RapidSnap(16, 17), ExtendedKeyRecord.RapidSnap(SlotA, SlotD)], write);
		Assert.False(vm.IsExtendedKeyEditorOpen);
		Assert.Equal(2, vm.ExtendedKeys.Count);
		Assert.True(Key(vm, "A").IsExtendedKeyHighlighted);
		Assert.False(Key(vm, "A").IsSelected);
		Assert.Equal(write, Live(store, owner));
	}

	[Fact]
	public async Task AddFlow_PickOrderDoesNotMatter_TheRecordIsNormalised()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info());

		await AddPair(vm, "D", "A");

		Assert.Equal([ExtendedKeyRecord.RapidSnap(SlotA, SlotD)], Assert.Single(hid.ExtendedKeysWrites));
	}

	[Fact]
	public async Task Cancel_ClosesTheEditorWithoutWriting_AndClearsThePicks()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info());
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartRapidSnapEditorCommand.Execute(null);
		vm.SelectExtendedKey(Key(vm, "A"));

		vm.CancelExtendedKeyEditorCommand.Execute(null);

		Assert.False(vm.IsExtendedKeyEditorOpen);
		Assert.False(Key(vm, "A").IsSelected);
		Assert.Empty(hid.ExtendedKeysWrites);
	}

	[Fact]
	public async Task Editor_LeavingTheSection_CancelsIt()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info());
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartRapidSnapEditorCommand.Execute(null);
		vm.SelectExtendedKey(Key(vm, "A"));

		vm.ActiveSection = NavSection.Presets;

		Assert.False(vm.IsExtendedKeyEditorOpen);
		Assert.False(Key(vm, "A").IsSelected);
		Assert.Empty(hid.ExtendedKeysWrites);
	}

	// An apply that reaches the board supersedes an edit in progress: the set about to land is not
	// the one the open editor was composed against.
	[Fact]
	public async Task Editor_AnApplyThatWritesTheBoard_CancelsIt()
	{
		var (vm, store, hid, owner) = NewVm();
		await Open(vm, hid, Info());
		// Rows the board does not have, so the apply has something to write.
		store.ReplaceDeviceExtendedKeys(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
			[ExtendedKeyRecord.RapidSnap(SlotA, SlotD)]);
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartRapidSnapEditorCommand.Execute(null);
		vm.SelectExtendedKey(Key(vm, "A"));

		Assert.True(await vm.ApplyStoredExtendedKeysToDeviceAsync());

		Assert.False(vm.IsExtendedKeyEditorOpen);
	}

	// ...and one that does not write leaves it alone. This method is also the self-heal, which runs
	// at the end of EVERY open of an extended-keys board and in the overwhelmingly common case finds
	// the board already right and writes nothing. The cancel used to be its first statement, before
	// every guard, so anything the user started as soon as the probe landed was closed again with no
	// write to show for it (review round 4, m5). The preset path is unaffected: it reloads the key
	// layout first, and ResetKeyLayoutState cancels the editor there.
	[Fact]
	public async Task Editor_AnApplyThatWritesNothing_LeavesItOpen()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info());
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartRapidSnapEditorCommand.Execute(null);
		vm.SelectExtendedKey(Key(vm, "A"));

		Assert.False(await vm.ApplyStoredExtendedKeysToDeviceAsync());

		Assert.True(vm.IsExtendedKeyEditorOpen);
		Assert.True(Key(vm, "A").IsSelected);
		Assert.Empty(hid.ExtendedKeysWrites);
	}

	[Fact]
	public async Task Editor_ClickingTheActiveChipsOwnKey_ClearsIt()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info());
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartRapidSnapEditorCommand.Execute(null);
		vm.SelectExtendedKey(Key(vm, "A"));
		vm.ActivateExtendedKeyEditorKey1Command.Execute(null);

		vm.SelectExtendedKey(Key(vm, "A"));

		Assert.Null(vm.ExtendedKeyEditorKey1Label);
		Assert.False(Key(vm, "A").IsSelected);
		Assert.True(vm.IsExtendedKeyEditorKey1Active);
	}

	// ── Refusals: a message, no pick, no write ────────────────────────────────

	private static async Task AssertRefused(DeviceDetailsViewModel vm, FakeHid hid, KeyAssignmentOptionViewModel key)
	{
		vm.SelectExtendedKey(key);

		Assert.True(vm.HasExtendedKeyEditorMessage);
		Assert.False(string.IsNullOrEmpty(vm.ExtendedKeyEditorMessage));
		Assert.Null(vm.ExtendedKeyEditorKey2Label);
		Assert.False(key.IsSelected);
		Assert.False(vm.CanConfirmExtendedKey);
		await vm.ConfirmExtendedKeyCommand.ExecuteAsync(null);
		Assert.Empty(hid.ExtendedKeysWrites);
	}

	[Fact]
	public async Task Refuses_TheSameKeyForBothChips()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info());
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartRapidSnapEditorCommand.Execute(null);
		vm.SelectExtendedKey(Key(vm, "A"));

		// Key 2 is the active chip now; A is the OTHER chip's key.
		vm.SelectExtendedKey(Key(vm, "A"));

		Assert.True(vm.HasExtendedKeyEditorMessage);
		Assert.Null(vm.ExtendedKeyEditorKey2Label);
		Assert.False(vm.CanConfirmExtendedKey);
		// The first pick survives the refusal.
		Assert.Equal("A", vm.ExtendedKeyEditorKey1Label);
		Assert.True(Key(vm, "A").IsSelected);
		await vm.ConfirmExtendedKeyCommand.ExecuteAsync(null);
		Assert.Empty(hid.ExtendedKeysWrites);
	}

	[Fact]
	public async Task Refuses_AKeyAlreadyInARecord()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info(ExtendedKeyRecord.RapidSnap(SlotA, SlotD)));
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartRapidSnapEditorCommand.Execute(null);
		vm.SelectExtendedKey(Key(vm, "S"));

		await AssertRefused(vm, hid, Key(vm, "D"));
	}

	[Fact]
	public async Task Refuses_ASlotOwnedByAnUnmanagedRecord()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info() with { BlockedSlots = [16] });   // Q
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartRapidSnapEditorCommand.Execute(null);
		vm.SelectExtendedKey(Key(vm, "A"));

		await AssertRefused(vm, hid, Key(vm, "Q"));
	}

	[Fact]
	public async Task Refuses_ARemappedKey_FromThePersistedOverride()
	{
		var (vm, store, hid, owner) = NewVm();
		hid.Info = Info();
		vm.Device = CyrexCard();
		// Q remapped to W, staged the way the immediate-write family stores it (slot-keyed row).
		store.SetKeyBinding(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey, 16, 0x10, 0, 0x1A);
		vm.LoadKeyLayoutForDevice();
		await vm.LoadExtendedKeysFromDeviceAsync();
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartRapidSnapEditorCommand.Execute(null);
		vm.SelectExtendedKey(Key(vm, "A"));

		await AssertRefused(vm, hid, Key(vm, "Q"));
	}

	[Fact]
	public async Task Refuses_ARemappedKey_FromTheDeviceMirror()
	{
		var (vm, _, hid, _) = NewVm();
		hid.Matrix = new Dictionary<byte, (byte, byte, byte)> { [16] = (0x10, 0x00, 0x1A) };   // Q types W
		await Open(vm, hid, Info());
		await vm.ProbeAndUpdateMatrixMapAsync();
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartRapidSnapEditorCommand.Execute(null);
		vm.SelectExtendedKey(Key(vm, "A"));

		await AssertRefused(vm, hid, Key(vm, "Q"));
	}

	// ── Delete / Default Settings / failure ───────────────────────────────────

	[Fact]
	public async Task Delete_WritesTheSetWithoutThatRecord_AndClearsItsHighlights()
	{
		var (vm, store, hid, owner) = NewVm();
		await Open(vm, hid, Info(ExtendedKeyRecord.RapidSnap(SlotA, SlotD), ExtendedKeyRecord.RapidSnap(16, 17)));

		var row = vm.ExtendedKeys.Single(r => r.Record.Slot1 == SlotA);
		await vm.DeleteExtendedKeyCommand.ExecuteAsync(row);

		Assert.Equal([ExtendedKeyRecord.RapidSnap(16, 17)], Assert.Single(hid.ExtendedKeysWrites));
		Assert.Single(vm.ExtendedKeys);
		Assert.False(Key(vm, "A").IsExtendedKeyHighlighted);
		Assert.True(Key(vm, "Q").IsExtendedKeyHighlighted);
		Assert.Equal([ExtendedKeyRecord.RapidSnap(16, 17)], Live(store, owner));
	}

	[Fact]
	public async Task DefaultSettings_AsksFirst_ThenWritesTheEmptySetOnce()
	{
		var (vm, store, hid, owner) = NewVm();
		await Open(vm, hid, Info(ExtendedKeyRecord.RapidSnap(SlotA, SlotD)));

		vm.RestoreExtendedKeysDefaultsCommand.Execute(null);
		Assert.True(vm.IsResetExtendedKeysOpen);
		Assert.Empty(hid.ExtendedKeysWrites);

		vm.CancelResetExtendedKeysCommand.Execute(null);
		Assert.False(vm.IsResetExtendedKeysOpen);
		Assert.Empty(hid.ExtendedKeysWrites);

		vm.RestoreExtendedKeysDefaultsCommand.Execute(null);
		await vm.ConfirmResetExtendedKeysCommand.ExecuteAsync(null);

		Assert.False(vm.IsResetExtendedKeysOpen);
		Assert.Empty(Assert.Single(hid.ExtendedKeysWrites));
		Assert.True(vm.HasNoExtendedKeys);
		Assert.False(Key(vm, "A").IsExtendedKeyHighlighted);
		Assert.Empty(Live(store, owner));
	}

	[Fact]
	public async Task FailedWrite_RaisesTheBanner_ReloadsFromTheDevice_AndPersistsTheTruth()
	{
		var (vm, store, hid, owner) = NewVm();
		await Open(vm, hid, Info(ExtendedKeyRecord.RapidSnap(16, 17)));
		hid.WriteSucceeds = false;

		await AddPair(vm, "A", "D");

		Assert.Single(hid.ExtendedKeysWrites);
		Assert.True(vm.ExtendedKeysWriteFailed);
		Assert.Equal(2, hid.GetExtendedKeysCalls);
		// The list shows what the board holds, not what was refused; the editor stays open.
		Assert.Equal([ExtendedKeyRecord.RapidSnap(16, 17)], vm.ExtendedKeys.Select(r => r.Record));
		Assert.False(Key(vm, "A").IsExtendedKeyHighlighted);
		Assert.True(vm.IsExtendedKeyEditorOpen);
		Assert.Equal([ExtendedKeyRecord.RapidSnap(16, 17)], Live(store, owner));

		// The next accepted write clears the banner.
		hid.WriteSucceeds = true;
		await vm.ConfirmExtendedKeyCommand.ExecuteAsync(null);
		Assert.False(vm.ExtendedKeysWriteFailed);
	}

	// The reload a refused write triggers is the ONE caller that keeps the old, narrower reset
	// (clearWriteFailure: false) — the device-open path now drops everything through
	// ResetExtendedKeysState instead (review round 3, S8). The difference is only visible while the
	// reload's probe is in flight, so it is parked here: widening that branch to the full reset
	// would take the banner down, empty the list and cancel the editor mid-write, and every
	// assertion made after the reload returns would still pass.
	[Fact]
	public async Task FailedWrite_KeepsTheBannerAndTheRecordSetWhileItsReloadIsInFlight()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info(ExtendedKeyRecord.RapidSnap(SlotQ, SlotW)));
		hid.WriteSucceeds = false;

		// Armed after the open, so what parks is the RELOAD's probe and not the open's.
		var probeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource();
		hid.ProbeEntered = probeEntered;
		hid.ProbeGate = release.Task;

		var add = AddPair(vm, "A", "D");
		await probeEntered.Task;

		Assert.True(vm.ExtendedKeysWriteFailed);
		Assert.Equal([ExtendedKeyRecord.RapidSnap(SlotQ, SlotW)], vm.ExtendedKeys.Select(r => r.Record));
		Assert.True(Key(vm, "Q").IsExtendedKeyHighlighted);
		Assert.False(Key(vm, "A").IsExtendedKeyHighlighted);
		Assert.True(vm.IsExtendedKeyEditorOpen);

		release.SetResult();
		await add;

		Assert.True(vm.ExtendedKeysWriteFailed);
		Assert.Equal([ExtendedKeyRecord.RapidSnap(SlotQ, SlotW)], vm.ExtendedKeys.Select(r => r.Record));
		Assert.True(vm.IsExtendedKeyEditorOpen);
	}

	// The exception to the case above, and the one path that reaches it (review round 4, m3(b)): one
	// lost or NAK'd packet during a write leaves the board's two regions disagreeing, and the read
	// that follows cannot parse it — so the host RESETS every advanced key to answer and flags the
	// response `repaired`. Its entries are what survived that reset, not a configuration anybody
	// chose. Persisting them would copy the loss into the owner's rows, where the next Save Preset
	// syncs it to the server and no re-read can undo it.
	//
	// Both halves matter and they pull in opposite directions: the SECTION must show the repaired
	// board, because that is the truth about the hardware and the banner is there to explain the
	// disagreement, while the ROWS must keep the user's set, because that is what the next open
	// pushes back. A fix that simply skipped the re-read would pass the second assert and fail the
	// first.
	[Fact]
	public async Task FailedWrite_WhoseReReadWasRepaired_ShowsTheBoardButKeepsTheOwnersRows()
	{
		var (vm, store, hid, owner) = NewVm();
		await Open(vm, hid, Info(ExtendedKeyRecord.RapidSnap(SlotQ, SlotW)));
		// The owner's own rows: the configuration the next open has to push back onto the board.
		store.ReplaceDeviceExtendedKeys(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
			[ExtendedKeyRecord.RapidSnap(SlotQ, SlotW)]);
		hid.WriteSucceeds = false;
		// The refused write tore the board; the read that follows had to reset it to answer, and comes
		// back empty AND flagged.
		hid.InfoAfterFailedWrite = new ExtendedKeysInfo(
			Supported: true, Entries: [], AreaBytes: 1024, FreeBytes: 1024, BlockedSlots: []) { Repaired = true };

		await AddPair(vm, "A", "D");

		Assert.True(vm.ExtendedKeysWriteFailed);
		// The section renders the repaired board — nothing is hidden from the user.
		Assert.Empty(vm.ExtendedKeys);
		Assert.True(vm.HasNoExtendedKeys);
		Assert.False(Key(vm, "Q").IsExtendedKeyHighlighted);
		// ...and the owner's rows are untouched by it, pair and all.
		Assert.Equal([ExtendedKeyRecord.RapidSnap(SlotQ, SlotW)], Live(store, owner));

		// The flag is not sticky: the next write the board ACCEPTS is a set the App chose, so it is
		// the owner's state again and persists as normal.
		hid.WriteSucceeds = true;
		await AddPair(vm, "A", "D");
		Assert.Equal([ExtendedKeyRecord.RapidSnap(SlotA, SlotD)], Live(store, owner));
	}

	[Fact]
	public async Task StorageFull_DisablesTheRsRow_AndTheEditorWillNotOpen()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info() with { FreeBytes = 4 });
		vm.ActiveSection = NavSection.ExtendedKeys;

		Assert.True(vm.IsExtendedKeyStorageFull);
		Assert.False(vm.CanAddRapidSnap);
		vm.StartRapidSnapEditorCommand.Execute(null);
		Assert.False(vm.IsExtendedKeyEditorOpen);
	}

	// ── Key Assignments / Macros seams ────────────────────────────────────────

	[Fact]
	public async Task KeyAssignments_ClickingAKeyInARecord_ShowsTheBlockedMessage_AndOpensNoDialog()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info(ExtendedKeyRecord.RapidSnap(SlotA, SlotD)));
		vm.ActiveSection = NavSection.Buttons;

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "A"));

		Assert.False(vm.IsKeyAssignmentDialogOpen);
		Assert.Null(vm.SelectedPhysicalKey);
		Assert.True(vm.HasExtendedKeyBlockedMessage);
		Assert.False(string.IsNullOrEmpty(vm.ExtendedKeyBlockedMessage));

		// A free key still opens the dialog as before.
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "S"));
		Assert.True(vm.IsKeyAssignmentDialogOpen);
	}

	[Fact]
	public async Task Macros_ClickingAKeyInARecord_IsBlockedTheSameWay()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info(ExtendedKeyRecord.RapidSnap(SlotA, SlotD)));
		vm.ActiveSection = NavSection.Macros;

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "D"));

		Assert.False(vm.IsKeyAssignmentDialogOpen);
		Assert.True(vm.HasExtendedKeyBlockedMessage);
	}

	// ── The probe-pending gate (review round 3, S8) ───────────────────────────
	//
	// The cross-device window the gate exists for is in DeviceDetailsPulledPresetApplyTests (it
	// needs two boards and a real SetDeviceAsync). These two pin its EDGES on one board: which
	// keyboards it may refuse at all, and which it must never refuse.

	// The gate keys on the CATALOG flag as well as the verdict. A board that never claimed advanced
	// keys leaves _extendedKeysSupported null forever, so keying on the verdict alone would lock Key
	// Assignments on every such keyboard for the whole session.
	[Fact]
	public async Task KeyAssignments_OnABoardWhoseCatalogDoesNotClaimExtendedKeys_IsNotRefusedByTheProbeGate()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, info: null, supportsExtendedKeys: false);
		vm.ActiveSection = NavSection.Buttons;

		Assert.False(vm.IsExtendedKeyProtectionUnknown);

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "A"));

		Assert.True(vm.IsKeyAssignmentDialogOpen);
		Assert.Same(Key(vm, "A"), vm.SelectedPhysicalKey);
		Assert.False(vm.HasExtendedKeyBlockedMessage);
	}

	// The contrast, and a deliberate behaviour change rather than a side effect (implementation
	// notes, round 3): a board that DOES claim them whose probe RPC failed — an old DeviceHost's
	// UNIMPLEMENTED — keeps a null verdict for the rest of the open, and an unknown board is exactly
	// as unsafe to write as an unprobed one. A reopen retries, which is what the Extended Keys
	// section itself already does.
	[Fact]
	public async Task KeyAssignments_OnABoardWhoseProbeRpcFailed_StaysRefusedForTheRestOfTheOpen()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, info: null);
		vm.ActiveSection = NavSection.Buttons;

		Assert.True(vm.IsExtendedKeyProtectionUnknown);

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "A"));

		Assert.False(vm.IsKeyAssignmentDialogOpen);
		Assert.Null(vm.SelectedPhysicalKey);
		Assert.Equal("ExtendedKeys_Blocked_ProbePending", vm.ExtendedKeyBlockedMessage);
		Assert.Empty(hid.KeyWrites);
	}

	// ── The hover tip's unassign ✕ (review round 3, S7) ───────────────────────
	//
	// The click path above is only one of the two ways a key record can be written into a slot.
	// The other is UnassignMacroFromKey — the ✕ inside a macro-bound key's hover tip — whose reset
	// writes the key's OWN record into the slot. On a slot that holds an extended-key record that
	// write lands on the record's {0x8x, offset} pointer and orphans it: the section still lists
	// the record and paints the key, and the board no longer plays it.
	//
	// The state needs a slot carrying BOTH a key_macro_bindings row and a record. The App's own UI
	// cannot make one (the click path is refused long before a macro can be bound there), so it is
	// seeded the way a pulled preset leaves it — the row straight into SQLite, the record straight
	// onto the fake board — which is exactly the reachability the review named.

	private static MacroViewModel BindMacroToSlot(
		DeviceDetailsViewModel vm, LocalStorageService store, string owner, int slot, string name = "M1")
	{
		var macroId = store.SaveMacro(
			LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
			new MacroRecord { Name = name, RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount });
		store.SetKeyMacroBinding(
			LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey, slot, macroId);
		// The library the binding points into: BoundMacroForKey resolves the row's macro id against
		// this collection, which SetDeviceAsync would have filled.
		var macro = new MacroViewModel { Id = macroId, Name = name };
		vm.Macros.Add(macro);
		return macro;
	}

	private static List<(int MatrixIndex, int MacroId)> Bindings(LocalStorageService s, string owner) =>
		s.GetKeyMacroBindings(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey);

	// The damaging path, asserted on the WRITE. A message-only assertion would not have failed
	// before the fix in the way that matters: what orphans the record is the 3-byte key record
	// reaching slot 31, not the missing banner.
	[Fact]
	public async Task UnassignMacroFromKey_OnASlotThatAlsoHoldsARecord_WritesNoKeyRecord_AndKeepsTheBinding()
	{
		var (vm, store, hid, owner) = NewVm();
		var macro = BindMacroToSlot(vm, store, owner, SlotA);
		await Open(vm, hid, Info(ExtendedKeyRecord.RapidSnap(SlotA, SlotD)));
		vm.ActiveSection = NavSection.Macros;

		// The tip shows on hover, so its ✕ acts on a key that is not the selected one — and the
		// refusal must leave that selection where the user put it.
		var elsewhere = Key(vm, "S");
		vm.SelectPhysicalKeyCommand.Execute(elsewhere);
		var keyA = Key(vm, "A");
		Assert.True(vm.KeyHasBoundMacro(keyA));

		await vm.UnassignMacroFromKeyCommand.ExecuteAsync(keyA);

		// Nothing reached the board: slot 31 still holds the record's pointer.
		Assert.Empty(hid.KeyWrites);
		// And nothing was dropped in SQLite either — the binding row is the preset's, not ours to
		// delete on a write we refused to make.
		Assert.Equal([(SlotA, macro.Id)], Bindings(store, owner));
		Assert.True(vm.KeyHasBoundMacro(keyA));
		Assert.Same(elsewhere, vm.SelectedPhysicalKey);
		// The record's own verdict names it (ExtendedKeys_Blocked_Format), not the probe's.
		Assert.Equal("ExtendedKeys_Blocked_Format", vm.ExtendedKeyBlockedMessage);
		// The record itself is untouched, so the section still has something to play.
		Assert.Equal([ExtendedKeyRecord.RapidSnap(SlotA, SlotD)], vm.ExtendedKeys.Select(r => r.Record));
	}

	// The same click one step earlier, while this open's probe is still in flight. The ORDER of the
	// two guards is what this pins: inside the window IsExtendedKeyProtectedSlot is not merely
	// unavailable, it is wrong — the record set is null, so slot 31 reads as free and the named
	// check below would wave the write straight through.
	[Fact]
	public async Task UnassignMacroFromKey_DuringTheProbeWindow_SaysTheBoardIsStillBeingRead_AndWritesNothing()
	{
		var (vm, store, hid, owner) = NewVm();
		var macro = BindMacroToSlot(vm, store, owner, SlotA);

		var probeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource();
		hid.ProbeEntered = probeEntered;
		hid.ProbeGate = release.Task;
		hid.Info = Info(ExtendedKeyRecord.RapidSnap(SlotA, SlotD));

		// Open() unrolled, so the board can be parked mid-probe: the key layout exists, the
		// advanced-key read has not answered.
		vm.Device = CyrexCard();
		vm.LoadKeyLayoutForDevice();
		var load = vm.LoadExtendedKeysFromDeviceAsync();
		await probeEntered.Task;
		vm.ActiveSection = NavSection.Macros;

		var keyA = Key(vm, "A");
		Assert.True(vm.IsExtendedKeyProtectionUnknown);
		Assert.False(vm.IsExtendedKeyProtectedSlot(SlotA), "the window's premise: the slot reads as free while the set is null");
		Assert.True(vm.KeyHasBoundMacro(keyA));

		await vm.UnassignMacroFromKeyCommand.ExecuteAsync(keyA);

		Assert.Equal("ExtendedKeys_Blocked_ProbePending", vm.ExtendedKeyBlockedMessage);
		Assert.Empty(hid.KeyWrites);
		Assert.Equal([(SlotA, macro.Id)], Bindings(store, owner));
		Assert.Null(vm.SelectedPhysicalKey);

		// Released, the verdict arrives and the same ✕ is refused by the NAMED check instead — so
		// the probe gate is a window over the open, not a second lock on the key.
		release.SetResult();
		await load;
		Assert.False(vm.IsExtendedKeyProtectionUnknown);

		await vm.UnassignMacroFromKeyCommand.ExecuteAsync(keyA);

		Assert.Equal("ExtendedKeys_Blocked_Format", vm.ExtendedKeyBlockedMessage);
		Assert.Empty(hid.KeyWrites);
		Assert.Equal([(SlotA, macro.Id)], Bindings(store, owner));
	}

	// The regression guard: the new refusal must be about the SLOT, not about the board. A key with
	// a macro and no record on a keyboard that holds records elsewhere unassigns exactly as before —
	// device write, binding row dropped, selection moved onto it.
	[Fact]
	public async Task UnassignMacroFromKey_OnAnOrdinaryBoundKey_StillUnassigns_WithRecordsElsewhereOnTheBoard()
	{
		var (vm, store, hid, owner) = NewVm();
		BindMacroToSlot(vm, store, owner, SlotS);
		await Open(vm, hid, Info(ExtendedKeyRecord.RapidSnap(SlotA, SlotD)));
		vm.ActiveSection = NavSection.Macros;

		var keyS = Key(vm, "S");
		Assert.True(vm.KeyHasBoundMacro(keyS));

		await vm.UnassignMacroFromKeyCommand.ExecuteAsync(keyS);

		var write = Assert.Single(hid.KeyWrites);
		Assert.Equal(((byte)SlotS, keyS.TypeByte, keyS.Bitmask, keyS.ScanCode), write);
		Assert.False(vm.KeyHasBoundMacro(keyS));
		Assert.Empty(Bindings(store, owner));
		Assert.Same(keyS, vm.SelectedPhysicalKey);
		Assert.False(vm.HasExtendedKeyBlockedMessage);
	}

	// ── The tip's half of S7 ──────────────────────────────────────────────────
	//
	// IsExtendedKeyWriteBlocked is the one question the view asks: the command names a different
	// reason per case because it has a banner to fill, the tip only has to decide whether to draw
	// the ✕ at all.

	[Fact]
	public async Task IsExtendedKeyWriteBlocked_IsTrueForAKeyInARecord_AndFalseForAFreeOne()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info(ExtendedKeyRecord.RapidSnap(SlotA, SlotD)));

		Assert.True(vm.IsExtendedKeyWriteBlocked(Key(vm, "A")));
		Assert.True(vm.IsExtendedKeyWriteBlocked(Key(vm, "D")));
		Assert.False(vm.IsExtendedKeyWriteBlocked(Key(vm, "S")));
	}

	// Both the other two answers the predicate owes: a slot the host preserves but does not manage
	// is just as unwritable, and a verdict that has not arrived blocks every key — but only on a
	// board that claims advanced keys, or the ✕ would vanish on every other keyboard.
	[Fact]
	public async Task IsExtendedKeyWriteBlocked_CoversTheUnmanagedSlotAndTheProbeWindow_ButNotOtherBoards()
	{
		var (blocked, _, blockedHid, _) = NewVm();
		await Open(blocked, blockedHid, Info() with { BlockedSlots = [SlotQ] });
		Assert.True(blocked.IsExtendedKeyWriteBlocked(Key(blocked, "Q")));
		Assert.False(blocked.IsExtendedKeyWriteBlocked(Key(blocked, "S")));

		var (unread, _, unreadHid, _) = NewVm();
		await Open(unread, unreadHid, info: null);
		Assert.True(unread.IsExtendedKeyWriteBlocked(Key(unread, "S")));

		var (plain, _, plainHid, _) = NewVm();
		await Open(plain, plainHid, info: null, supportsExtendedKeys: false);
		Assert.False(plain.IsExtendedKeyWriteBlocked(Key(plain, "S")));
	}

	// The view wiring itself. ShowKeyTip decides the ✕'s visibility imperatively and this project
	// has no Avalonia headless harness (see KeyboardLegendAssetTests), so the composition is pinned
	// by reading the code-behind copied into the test output — the same technique
	// LanguageResourceParityTests uses for DeviceTypeLabelConverter.cs and
	// MacrosSectionRowBindingTests for the macro row. Without it the predicate above could be
	// correct and unconsulted, which is exactly the state S7 found.
	[Fact]
	public void HoverTip_WithholdsTheUnassignCross_ForAKeyTheCommandWouldRefuse()
	{
		var path = Path.Combine(AppContext.BaseDirectory, "RasterKeyboardOverlayView.axaml.cs.txt");
		Assert.True(File.Exists(path),
			$"RasterKeyboardOverlayView.axaml.cs was not copied to the test output ({path}). " +
			"Check the <None Include=...> items in ProoveGaming.App.Tests.csproj.");

		// The whole assignment, not the file: a file-wide Contains would stay green if the call
		// moved into some unrelated branch of the same method.
		var showUnassign = Regex.Match(File.ReadAllText(path), @"var showUnassign\s*=[\s\S]*?;");
		Assert.True(showUnassign.Success,
			"RasterKeyboardOverlayView no longer computes a 'showUnassign' local — the tip's ✕ gate moved.");
		Assert.Contains("KeyHasBoundMacro(key)", showUnassign.Value);
		Assert.Contains("!_boundVm.IsExtendedKeyWriteBlocked(key)", showUnassign.Value);
	}

	[Fact]
	public async Task Tooltip_NamesTheModeForAKeyInARecord_AndIsNullOtherwise()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info(ExtendedKeyRecord.RapidSnap(SlotA, SlotD)));

		Assert.False(string.IsNullOrEmpty(vm.GetExtendedKeyTooltip(Key(vm, "A"))));
		Assert.False(string.IsNullOrEmpty(vm.GetExtendedKeyTooltip(Key(vm, "D"))));
		Assert.Null(vm.GetExtendedKeyTooltip(Key(vm, "S")));
	}

	// ── The macro resync's stale-slot loop (review round 3, S6) ───────────────
	//
	// The binding loop skips a slot inside a record, so that slot never enters `nowApplied`. The
	// stale loop then reads every slot in `_appliedMacroSlots` that is missing from `nowApplied` as
	// "the macro left" and writes the key's OWN record into it — over the record's {0x8x, offset}
	// pointer. The section still lists the record and still paints the key; the board no longer
	// plays it.
	//
	// Reaching that state needs `_appliedMacroSlots` to still name the slot when the stale loop runs
	// AFTER the record is on the board, and `_appliedMacroSlots` is private with no test seam — so
	// both cases below build it the only way the App can, by driving the real resync more than once
	// around a real extended-keys push, in the order ApplyCurrentStateToDeviceCoreAsync uses
	// (key push → resync → actuation → extended-keys push → key push → resync).
	//
	// Asserted on the WRITE, not on a flag: what orphans the record is the 3-byte key record
	// reaching slot 31. Each case also keeps a genuinely stale, UNPROTECTED slot in play, so a
	// `.Where` that simply stopped the loop from ever firing would fail them too.

	// The ordinary preset apply, with no failure anywhere — see the test-notes finding: pass 1
	// re-applies the binding and so re-seeds `_appliedMacroSlots` with the very slot the
	// extended-keys push is about to protect, which makes the window wider than round 3 recorded.
	[Fact]
	public async Task MacroResync_AfterTheExtendedKeysPushClaimedABoundSlot_WritesNoKeyRecordIntoIt()
	{
		var (vm, store, hid, owner) = NewVm();
		// A is the slot the incoming preset's record will claim; S is an ordinary bound key whose
		// binding the same preset drops, and whose stale clear must still happen.
		var onA = BindMacroToSlot(vm, store, owner, SlotA, "OnA");
		BindMacroToSlot(vm, store, owner, SlotS, "OnS");
		await Open(vm, hid, Info(), supportsMacros: true);

		// Pass 1, board still free of records: both bindings are applied, so both slots enter
		// _appliedMacroSlots.
		await vm.ResyncKeyMacroBindingsAsync();
		Assert.Contains(hid.KeyWrites, w => w.MatrixIndex == SlotA);
		Assert.Contains(hid.KeyWrites, w => w.MatrixIndex == SlotS);

		// The extended-keys push, between the two resyncs: the preset's record lands on the board
		// and slot 31 becomes a pointer. The App's own UI could not have bound a macro there, but
		// the API intake validates key_macro_bindings and extended_keys independently, so a pulled
		// preset can carry both (the reachability S7 was raised on).
		store.ReplaceDeviceExtendedKeys(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
			[ExtendedKeyRecord.RapidSnap(SlotA, SlotD)]);
		Assert.True(await vm.ApplyStoredExtendedKeysToDeviceAsync());
		// ...and the preset drops S's binding, which is what makes S legitimately stale below.
		store.DeleteKeyMacroBinding(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey, SlotS);

		hid.KeyWrites.Clear();
		await vm.ResyncKeyMacroBindingsAsync();

		// The pointer survived...
		Assert.DoesNotContain(hid.KeyWrites, w => w.MatrixIndex == SlotA);
		// ...the record is still the board's and still the section's...
		Assert.Equal([ExtendedKeyRecord.RapidSnap(SlotA, SlotD)], hid.Info!.Entries);
		Assert.Equal([ExtendedKeyRecord.RapidSnap(SlotA, SlotD)], vm.ExtendedKeys.Select(r => r.Record));
		// ...and the binding row A still carries was not deleted either, so nothing about it moved.
		Assert.Contains(Bindings(store, owner), b => b == (SlotA, onA.Id));
		// The loop itself still does its job on the slot that really did lose its macro.
		var cleared = Assert.Single(hid.KeyWrites, w => w.MatrixIndex == SlotS);
		var keyS = Key(vm, "S");
		Assert.Equal(((byte)SlotS, keyS.TypeByte, keyS.Bitmask, keyS.ScanCode), cleared);
	}

	// The route the review documented: pass 1 returns early on the MacroUploadFailed path, so
	// `_appliedMacroSlots = nowApplied` never runs and the OLD set survives into pass 2. Here the
	// preset removed the binding as well, so nothing re-applies the slot — pass 1's early return is
	// the only thing keeping slot 31 in the set.
	[Fact]
	public async Task MacroResync_AfterAFailedLibraryUpload_StillWritesNoKeyRecordIntoARecordsSlot()
	{
		var (vm, store, hid, owner) = NewVm();
		BindMacroToSlot(vm, store, owner, SlotA, "OnA");
		BindMacroToSlot(vm, store, owner, SlotS, "OnS");
		await Open(vm, hid, Info(), supportsMacros: true);

		// The previous preset's resync, which is what seeds the slot set.
		await vm.ResyncKeyMacroBindingsAsync();
		Assert.False(vm.MacroUploadFailed);

		// The incoming preset carries neither binding.
		store.DeleteKeyMacroBinding(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey, SlotA);
		store.DeleteKeyMacroBinding(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey, SlotS);

		// Pass 1: the library upload fails, so the resync returns before the stale loop AND before
		// _appliedMacroSlots is replaced.
		hid.SetMacrosSucceeds = false;
		hid.KeyWrites.Clear();
		await vm.ResyncKeyMacroBindingsAsync();
		Assert.True(vm.MacroUploadFailed);
		Assert.Empty(hid.KeyWrites);

		// The extended-keys push installs the record while that stale set is still in place.
		store.ReplaceDeviceExtendedKeys(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
			[ExtendedKeyRecord.RapidSnap(SlotA, SlotD)]);
		Assert.True(await vm.ApplyStoredExtendedKeysToDeviceAsync());

		// Pass 2: the upload succeeds this time, so the stale loop runs — after the record is on
		// the board.
		hid.SetMacrosSucceeds = true;
		hid.KeyWrites.Clear();
		await vm.ResyncKeyMacroBindingsAsync();

		Assert.False(vm.MacroUploadFailed);
		Assert.DoesNotContain(hid.KeyWrites, w => w.MatrixIndex == SlotA);
		Assert.Equal([ExtendedKeyRecord.RapidSnap(SlotA, SlotD)], hid.Info!.Entries);
		// S is outside every record, so its stale clear is still owed and still made.
		Assert.Contains(hid.KeyWrites, w => w.MatrixIndex == SlotS);
	}

	// The skip set has two independent halves, and this pins both plus the case neither covers.
	//
	// Rewritten in review round 4 for M2. It used to assert that a board carrying 0x84 pointers on A
	// and D, with a probe reporting no record, had BOTH keys "restored" by the push — the very write
	// the reviewer showed orphans a record nobody has read yet, and for an unmanaged DKS/END loses it
	// for good. That is now refused: a slot whose board mirror carries a 0x80..0x85 TYPE byte is
	// skipped whatever the rows say, because the mirror is the one source that does not depend on a
	// probe having landed. The legitimate restore it used to stand for still happens, via the write
	// path rather than the mirror — WriteExtendedKeysAsync nulls _assignments when a record comes
	// off, so the next push re-reads the matrix and finds the factory triplet, not a pointer.
	[Fact]
	public async Task SlotKeyPush_SkipsEverySlotHoldingARecordPointer_ButStillRestoresPlainSlots()
	{
		var (vm, _, hid, _) = NewVm();
		// The board holds pointers on A and D, exactly as after a Rapid Snap write.
		hid.Matrix = new Dictionary<byte, (byte, byte, byte)>
		{
			[SlotA] = (0x84, 0x00, 0x00),
			[SlotD] = (0x84, 0x00, 0x00),
		};
		await Open(vm, hid, Info(ExtendedKeyRecord.RapidSnap(SlotA, SlotD)));

		await vm.ApplyKeyOverridesToDeviceAsync();
		Assert.DoesNotContain(hid.KeyWrites, w => w.MatrixIndex is SlotA or SlotD);

		// The same pointers with an EMPTY probe — the unread board of M2, and what a failed probe
		// leaves behind. The rows claim nothing, so the old skip set is empty and only the mirror can
		// answer. It must, or this push orphans whatever the pointers lead to.
		var (vm2, _, hid2, _) = NewVm();
		hid2.Matrix = new Dictionary<byte, (byte, byte, byte)>
		{
			[SlotA] = (0x84, 0x00, 0x00),
			[SlotD] = (0x84, 0x00, 0x00),
		};
		await Open(vm2, hid2, Info());

		await vm2.ApplyKeyOverridesToDeviceAsync();
		Assert.DoesNotContain(hid2.KeyWrites, w => w.MatrixIndex is SlotA or SlotD);

		// And the skip stays scoped to record pointers rather than becoming a blanket "never touch a
		// slot that differs": S carries a plain keyboard record nothing claims, and the push puts the
		// key's own record back exactly as before.
		var (vm3, _, hid3, _) = NewVm();
		hid3.Matrix = new Dictionary<byte, (byte, byte, byte)>
		{
			[SlotS] = (0x10, 0x00, 0x3E),
		};
		await Open(vm3, hid3, Info());

		await vm3.ApplyKeyOverridesToDeviceAsync();
		Assert.Contains(hid3.KeyWrites, w => w.MatrixIndex == SlotS);
	}

	// ── Preset push ───────────────────────────────────────────────────────────

	[Fact]
	public async Task PresetPush_WritesWhenTheStoredSetDiffers_AndIsSilentWhenItMatches()
	{
		var (vm, store, hid, owner) = NewVm();
		await Open(vm, hid, Info(ExtendedKeyRecord.RapidSnap(SlotA, SlotD)));

		// Board holds A/D; the preset says "none".
		store.ReplaceDeviceExtendedKeys(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey, []);
		Assert.True(await vm.ApplyStoredExtendedKeysToDeviceAsync());
		Assert.Empty(Assert.Single(hid.ExtendedKeysWrites));
		Assert.True(vm.HasNoExtendedKeys);

		// Board now matches: no second write.
		Assert.False(await vm.ApplyStoredExtendedKeysToDeviceAsync());
		Assert.Single(hid.ExtendedKeysWrites);

		// The preset gains the pair, picked in the other order — written once, normalised.
		store.ReplaceDeviceExtendedKeys(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
			[new ExtendedKeyRecord(ExtendedKeyMode.RapidSnap, SlotD, SlotA, 0, 0, ExtendedKeyCodeRecord.None, ExtendedKeyCodeRecord.None)]);
		Assert.True(await vm.ApplyStoredExtendedKeysToDeviceAsync());
		Assert.Equal(2, hid.ExtendedKeysWrites.Count);
		Assert.Equal([ExtendedKeyRecord.RapidSnap(SlotA, SlotD)], hid.ExtendedKeysWrites[1]);
		Assert.True(Key(vm, "A").IsExtendedKeyHighlighted);
	}

	// Superseded on 2026-09-11 (docs/plans/fix-extended-keys-self-heal). This case used to assert the
	// opposite of its second half: a failed preset push wrote the BOARD's records into the owner's
	// live rows, so the preset read dirty and the divergence was visible.
	//
	// That reasoning holds for one owner and breaks for several. The board is shared hardware, and
	// since the Extended Keys work it can be wearing ANOTHER account's records — after an account
	// switch it usually is. Copying them into this owner's rows imports a stranger's configuration
	// into the account and stages it dirty, one Save Preset away from syncing it to their cloud. The
	// honest channel for "the board does not hold what we wanted" is the write-failed banner, which
	// is set either way; the owner's data is not that channel.
	//
	// A user-initiated edit that fails still persists — see the case below — because there the board
	// and the owner are the same story.
	[Fact]
	public async Task PresetPush_NeitherPersistsOnSuccess_NorImportsTheBoardsRecordsOnFailure()
	{
		var (vm, store, hid, owner) = NewVm();
		await Open(vm, hid, Info(ExtendedKeyRecord.RapidSnap(SlotA, SlotD)));
		store.ReplaceDeviceExtendedKeys(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey, []);
		hid.WriteSucceeds = false;

		Assert.False(await vm.ApplyStoredExtendedKeysToDeviceAsync());

		// The failure is reported...
		Assert.True(vm.ExtendedKeysWriteFailed);
		// ...the section shows what the board really holds...
		Assert.Equal([ExtendedKeyRecord.RapidSnap(SlotA, SlotD)], vm.ExtendedKeys.Select(r => r.Record));
		// ...and the owner's own rows are untouched by it.
		Assert.Empty(Live(store, owner));
	}

	// Review round 4, m2. A real SetExtendedKeys is a 1-2 s flash write, and the user can reach the
	// Presets dropdown inside that window. The two operations used to be unordered, and the end state
	// was wrong in both halves at once: the preset's own extended-keys push got `false` from the
	// in-flight guard — the same answer as "the board already matches", so no banner and no re-run of
	// the key push — and then the user's write completed and persisted the PRE-switch set into the
	// live rows, on top of the snapshot the apply had just loaded for B. B counted as applied while
	// the board and the rows both carried A's records, and B read dirty.
	//
	// Running the three user-initiated writes under _presetApplyGate puts the whole apply — snapshot
	// load included — behind the write, which is the only ordering with a coherent end state. Making
	// the preset push merely WAIT for the write would not: LoadPresetSnapshotToDevice overwrites the
	// live rows long before the apply reaches its extended-keys push.
	[Fact]
	public async Task Race_APresetApplyDuringAnInFlightWrite_EndsOnTheAppliedPresetsSet()
	{
		var (vm, store, hid, owner) = NewVm();
		var aRecords = new List<ExtendedKeyRecord> { ExtendedKeyRecord.RapidSnap(SlotA, SlotD) };
		var bRecords = new List<ExtendedKeyRecord>
		{
			ExtendedKeyRecord.Toggle(SlotQ, 100, new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A)),
		};
		await Open(vm, hid, Info([.. aRecords]));

		// Preset B holds a different set in its snapshot; the live rows go back to what is actually on
		// the board, which is A's set.
		store.ReplaceDeviceExtendedKeys(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey, bRecords);
		var bId = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid,
			new PresetRecord { Name = "B" });
		store.SavePresetSnapshot(bId, LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey);
		store.ReplaceDeviceExtendedKeys(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey, aRecords);
		var b = new PresetViewModel { Id = bId, Name = "B" };
		vm.Presets.Add(b);
		await store.DrainStorageQueueAsync();

		// The user presses the trash on A's pair. The write parks mid-flight.
		var writeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var release = new TaskCompletionSource();
		hid.WriteEntered = writeEntered;
		hid.WriteGate = release.Task;
		var delete = vm.DeleteExtendedKeyCommand.ExecuteAsync(Assert.Single(vm.ExtendedKeys));
		await writeEntered.Task;
		// Only the FIRST write parks; the apply's own must be allowed to complete.
		hid.WriteEntered = null;
		hid.WriteGate = null;

		// ...and switches preset while it is still in flight.
		var apply = vm.SelectPresetCommand.ExecuteAsync(b);

		// Nothing of the apply may have happened yet — not even the snapshot load, which is the step
		// that used to land on top of the write. A fixed wait because this asserts an ABSENCE, which
		// polling cannot establish: every store here is in-memory, so the whole apply runs in
		// milliseconds once it is allowed to, and under the unordered code it had already finished.
		await Task.Delay(250);
		Assert.False(apply.IsCompleted);
		Assert.Equal(aRecords, Live(store, owner));

		release.SetResult();
		await delete;
		await apply;
		await store.DrainStorageQueueAsync();

		// B's set is what the board ended up wearing, what the rows hold, and what the section shows.
		Assert.Equal(bRecords, hid.ExtendedKeysWrites[^1]);
		Assert.Equal(bRecords, hid.Info!.Entries);
		Assert.Equal(bRecords, Live(store, owner));
		Assert.Equal(bRecords, vm.ExtendedKeys.Select(r => r.Record));
		// The trash really did run — the empty set went out first, so the end state is not B's write
		// having simply pre-empted a delete that never happened.
		Assert.Equal(2, hid.ExtendedKeysWrites.Count);
		Assert.Empty(hid.ExtendedKeysWrites[0]);
	}

	// ── Blocked (unmanaged) slots — review round 1, S1 ────────────────────────

	[Fact]
	public async Task SlotKeyPush_AlsoSkipsSlotsOwnedByAnUnmanagedRecord()
	{
		var (vm, _, hid, _) = NewVm();
		// Q points at a DKS record the host preserves but does not manage.
		hid.Matrix = new Dictionary<byte, (byte, byte, byte)> { [16] = (0x80, 0x00, 0x00) };
		await Open(vm, hid, Info() with { BlockedSlots = [16] });

		await vm.ApplyKeyOverridesToDeviceAsync();

		Assert.DoesNotContain(hid.KeyWrites, w => w.MatrixIndex == 16);
		Assert.True(vm.IsExtendedKeyProtectedSlot(16));
		Assert.False(vm.IsExtendedKeySlot(16));
	}

	[Fact]
	public async Task KeyAssignments_ClickingAKeyOwnedByAnUnmanagedRecord_IsBlockedTheSameWay()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info() with { BlockedSlots = [16] });
		vm.ActiveSection = NavSection.Buttons;

		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "Q"));

		Assert.False(vm.IsKeyAssignmentDialogOpen);
		Assert.True(vm.HasExtendedKeyBlockedMessage);
		Assert.False(string.IsNullOrEmpty(vm.ExtendedKeyBlockedMessage));
		// No managed record names the key, so the tip has nothing to say about it.
		Assert.Null(vm.GetExtendedKeyTooltip(Key(vm, "Q")));
	}

	[Fact]
	public async Task EditorHint_SwitchesToTheConfirmHint_OnceBothChipsAreFilled()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info());
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartRapidSnapEditorCommand.Execute(null);
		var pickHint = vm.ExtendedKeyEditorHint;

		vm.SelectExtendedKey(Key(vm, "A"));
		Assert.NotEqual(string.Empty, vm.ExtendedKeyEditorHint);
		vm.SelectExtendedKey(Key(vm, "D"));

		Assert.NotEqual(pickHint, vm.ExtendedKeyEditorHint);
		Assert.NotEqual(string.Empty, vm.ExtendedKeyEditorHint);
	}

	// ── The macro-bound refusal — review round 1, Tests ───────────────────────

	[Fact]
	public async Task Refuses_AMacroBoundKey()
	{
		var (vm, store, hid, owner) = NewVm();
		// Q plays library macro 7: the binding row plus the library entry BoundMacroForKey resolves.
		store.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey, 16, 7);
		vm.Macros.Add(new MacroViewModel { Id = 7, Name = "M1" });
		await Open(vm, hid, Info());
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartRapidSnapEditorCommand.Execute(null);
		vm.SelectExtendedKey(Key(vm, "A"));

		await AssertRefused(vm, hid, Key(vm, "Q"));
	}

	// ── Mod-Tap (docs/plans/add-extended-key-mod-tap) ─────────────────────────

	private const int SlotCaps = 30;
	private static readonly ExtendedKeyCodeRecord LeftCtrl = new(0x10, 0x01, 0x00);
	private static readonly ExtendedKeyCodeRecord Esc = new(0x10, 0x00, 0x29);
	private static readonly ExtendedKeyCodeRecord Caps = new(0x10, 0x00, 0x39);

	private static KeyAssignmentOptionViewModel PickerKey(DeviceDetailsViewModel vm, string name) =>
		vm.RemapTargetKeys.Single(k => k.DevKeyName == name);

	// The acceptance criterion's flow: Caps Lock on the overlay, Hold → Left Ctrl and Tap → Esc through
	// the dialog's pick purpose, the slider at 150 ms.
	private static async Task AddCapsModTap(DeviceDetailsViewModel vm, int timeMs = 150)
	{
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartModTapEditorCommand.Execute(null);
		vm.SelectExtendedKey(Key(vm, "CapsLock"));
		vm.PickExtendedKeyHoldCommand.Execute(null);
		vm.PickRemapTargetCommand.Execute(PickerKey(vm, "LeftCtrl"));
		vm.PickExtendedKeyTapCommand.Execute(null);
		vm.PickRemapTargetCommand.Execute(PickerKey(vm, "Escape"));
		vm.ExtendedKeyEditorTimeMs = timeMs;
		await vm.ConfirmExtendedKeyCommand.ExecuteAsync(null);
	}

	[Fact]
	public async Task ModTap_RowIsAvailable_AndTheEditorOpensAsSingleKeyAtTheVendorDefaults()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info());
		vm.ActiveSection = NavSection.ExtendedKeys;

		Assert.True(vm.IsModTapModeAvailable);
		Assert.True(vm.CanAddModTap);

		vm.StartModTapEditorCommand.Execute(null);

		Assert.True(vm.IsExtendedKeyEditorOpen);
		Assert.True(vm.IsExtendedKeyEditorSingleKey);
		Assert.False(vm.IsExtendedKeyEditorPair);
		Assert.False(vm.IsExtendedKeyEditorSocd);
		Assert.True(vm.ShowExtendedKeyEditorTapChip);
		Assert.True(vm.IsExtendedKeyEditorPhysicalActive);
		Assert.Equal(ExtendedKeyTime.DefaultMs, vm.ExtendedKeyEditorTimeMs);
		Assert.Equal(10.0, vm.ExtendedKeyEditorTimePct);
		Assert.False(vm.HasExtendedKeyEditorHold);
		Assert.False(vm.HasExtendedKeyEditorTap);
		Assert.False(vm.CanConfirmExtendedKey);
		Assert.False(string.IsNullOrEmpty(vm.ExtendedKeyEditorHint));
		Assert.False(string.IsNullOrEmpty(vm.ExtendedKeyEditorTimeHint));
	}

	[Fact]
	public async Task ModTap_PhysicalKeyPreFillsHold_AndIsDisabledUntilCleared()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info());
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartModTapEditorCommand.Execute(null);

		vm.SelectExtendedKey(Key(vm, "CapsLock"));

		Assert.Equal("CAPS", vm.ExtendedKeyEditorKey1Label);
		Assert.True(Key(vm, "CapsLock").IsSelected);
		Assert.True(vm.HasExtendedKeyEditorHold);
		Assert.Equal("CAPS", vm.ExtendedKeyEditorHoldLabel);
		Assert.False(vm.HasExtendedKeyEditorTap);
		Assert.False(vm.IsExtendedKeyEditorPhysicalActive);
		// Hold is set but Tap is not: no Confirm yet.
		Assert.False(vm.CanConfirmExtendedKey);

		// A different key while the chip is filled is refused with a message, not swapped in.
		vm.SelectExtendedKey(Key(vm, "A"));
		Assert.True(vm.HasExtendedKeyEditorMessage);
		Assert.Equal("CAPS", vm.ExtendedKeyEditorKey1Label);
		Assert.False(Key(vm, "A").IsSelected);

		// Clearing the chip drops the pre-filled Hold with it and re-arms the chip.
		vm.ClearExtendedKeyEditorKey1Command.Execute(null);
		Assert.Null(vm.ExtendedKeyEditorKey1Label);
		Assert.False(vm.HasExtendedKeyEditorHold);
		Assert.True(vm.IsExtendedKeyEditorPhysicalActive);
		Assert.False(Key(vm, "CapsLock").IsSelected);

		// Clicking the filled chip's own key clears it too (as in the pair editor).
		vm.SelectExtendedKey(Key(vm, "A"));
		Assert.Equal("A", vm.ExtendedKeyEditorHoldLabel);
		vm.SelectExtendedKey(Key(vm, "A"));
		Assert.Null(vm.ExtendedKeyEditorKey1Label);
		Assert.False(vm.HasExtendedKeyEditorHold);
		Assert.Empty(hid.ExtendedKeysWrites);
	}

	[Fact]
	public async Task ModTap_AUserPickedHoldSurvivesClearingThePhysicalKey_ButAPreFillDoesNot()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info());
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartModTapEditorCommand.Execute(null);

		vm.SelectExtendedKey(Key(vm, "CapsLock"));
		vm.PickExtendedKeyHoldCommand.Execute(null);
		vm.PickRemapTargetCommand.Execute(PickerKey(vm, "LeftCtrl"));
		Assert.Equal("CTRL (L)", vm.ExtendedKeyEditorHoldLabel);

		vm.ClearExtendedKeyEditorKey1Command.Execute(null);
		Assert.Equal("CTRL (L)", vm.ExtendedKeyEditorHoldLabel);

		// A new physical key does not overwrite what the user chose.
		vm.SelectExtendedKey(Key(vm, "A"));
		Assert.Equal("CTRL (L)", vm.ExtendedKeyEditorHoldLabel);
		Assert.Empty(hid.ExtendedKeysWrites);
	}

	[Fact]
	public async Task ModTap_PicksThroughTheDialog_ConfirmWritesTheRecord_AndTheRowShowsIt()
	{
		var (vm, store, hid, owner) = NewVm();
		await Open(vm, hid, Info());
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartModTapEditorCommand.Execute(null);
		vm.SelectExtendedKey(Key(vm, "CapsLock"));

		// Hold: the dialog opens in pick purpose (Keys only), and the pick lands in the chip.
		vm.PickExtendedKeyHoldCommand.Execute(null);
		Assert.True(vm.IsKeyAssignmentDialogOpen);
		Assert.True(vm.IsKeyDialogPickPurpose);
		Assert.False(vm.IsKeyDialogMacrosTabVisible);
		Assert.False(vm.IsKeyDialogSystemTabVisible);
		// The current output (the Caps pre-fill) is highlighted on the picker.
		Assert.Same(PickerKey(vm, "CapsLock"), vm.RemapTargetKeys.Single(k => k.IsSelected));
		vm.PickRemapTargetCommand.Execute(PickerKey(vm, "LeftCtrl"));
		Assert.False(vm.IsKeyAssignmentDialogOpen);
		Assert.Equal("CTRL (L)", vm.ExtendedKeyEditorHoldLabel);
		Assert.False(vm.CanConfirmExtendedKey);

		vm.PickExtendedKeyTapCommand.Execute(null);
		vm.PickRemapTargetCommand.Execute(PickerKey(vm, "Escape"));
		Assert.Equal("ESC", vm.ExtendedKeyEditorTapLabel);
		Assert.True(vm.CanConfirmExtendedKey);

		// Picking is not remapping: no key write, no remap toast, nothing staged.
		Assert.Empty(hid.KeyWrites);
		Assert.False(vm.IsRemapToastVisible);
		Assert.Empty(store.GetKeyBindings(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey));

		vm.ExtendedKeyEditorTimeMs = 150;
		await vm.ConfirmExtendedKeyCommand.ExecuteAsync(null);

		var write = Assert.Single(hid.ExtendedKeysWrites);
		Assert.Equal([ExtendedKeyRecord.ModTap(SlotCaps, 150, LeftCtrl, Esc)], write);
		Assert.Equal(write, Live(store, owner));
		Assert.False(vm.IsExtendedKeyEditorOpen);

		var row = Assert.Single(vm.ExtendedKeys);
		Assert.Equal("CAPS", row.Key1Label);
		Assert.Null(row.Key2Label);
		Assert.False(row.HasKey2);
		Assert.Equal("CTRL (L)", row.HoldLabel);
		Assert.Equal("ESC", row.TapLabel);
		Assert.True(row.HasHoldLabel);
		Assert.True(row.HasTapLabel);
		// The "150 ms" text itself is a localized format, which resolves to its key in this harness.
		Assert.True(row.HasParameterText);
		Assert.True(Key(vm, "CapsLock").IsExtendedKeyHighlighted);
		Assert.True(vm.IsExtendedKeySlot(SlotCaps));
		Assert.NotNull(vm.GetExtendedKeyTooltip(Key(vm, "CapsLock")));
	}

	[Fact]
	public async Task ModTap_TimeSliderSnapsToWholeMilliseconds_AndClampsToTheRange()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info());
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartModTapEditorCommand.Execute(null);

		vm.SetExtendedKeyEditorTimeFromFraction(0.2614);
		Assert.Equal(261, vm.ExtendedKeyEditorTimeMs);
		Assert.Equal(26.1, vm.ExtendedKeyEditorTimePct, 3);
		Assert.False(string.IsNullOrEmpty(vm.ExtendedKeyEditorTimeText));
		Assert.False(string.IsNullOrEmpty(vm.ExtendedKeyEditorTimeHint));

		vm.SetExtendedKeyEditorTimeFromFraction(1.7);
		Assert.Equal(ExtendedKeyTime.MaxMs, vm.ExtendedKeyEditorTimeMs);
		vm.SetExtendedKeyEditorTimeFromFraction(-0.3);
		Assert.Equal(ExtendedKeyTime.MinMs, vm.ExtendedKeyEditorTimeMs);
		vm.ExtendedKeyEditorTimeMs = 5000;
		Assert.Equal(ExtendedKeyTime.MaxMs, vm.ExtendedKeyEditorTimeMs);

		// A fresh editor is back at the default; the slider is UI-only until Confirm.
		vm.CancelExtendedKeyEditorCommand.Execute(null);
		vm.StartModTapEditorCommand.Execute(null);
		Assert.Equal(ExtendedKeyTime.DefaultMs, vm.ExtendedKeyEditorTimeMs);
		Assert.Empty(hid.ExtendedKeysWrites);
	}

	[Fact]
	public async Task ModTap_AKeyInAnotherRecordIsRefused_AndTheReverse()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info(
			ExtendedKeyRecord.RapidSnap(SlotA, SlotD),
			ExtendedKeyRecord.ModTap(SlotCaps, 150, LeftCtrl, Esc)));
		vm.ActiveSection = NavSection.ExtendedKeys;

		// A is in the RS pair: the MT editor refuses it as the physical key.
		vm.StartModTapEditorCommand.Execute(null);
		await AssertRefused(vm, hid, Key(vm, "A"));
		Assert.Null(vm.ExtendedKeyEditorKey1Label);
		vm.CancelExtendedKeyEditorCommand.Execute(null);

		// Caps Lock is the MT key: the RS editor refuses it.
		vm.StartRapidSnapEditorCommand.Execute(null);
		await AssertRefused(vm, hid, Key(vm, "CapsLock"));
		vm.CancelExtendedKeyEditorCommand.Execute(null);

		// And Key Assignments blocks it like any other record's key.
		vm.ActiveSection = NavSection.Buttons;
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "CapsLock"));
		Assert.False(vm.IsKeyAssignmentDialogOpen);
		Assert.True(vm.HasExtendedKeyBlockedMessage);
	}

	[Fact]
	public async Task ModTap_CancellingTheEditorClosesAnOpenPick_AndALatePickCannotLand()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info());
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartModTapEditorCommand.Execute(null);
		vm.SelectExtendedKey(Key(vm, "CapsLock"));
		vm.PickExtendedKeyTapCommand.Execute(null);
		Assert.True(vm.IsKeyAssignmentDialogOpen);

		vm.CancelExtendedKeyEditorCommand.Execute(null);

		Assert.False(vm.IsKeyAssignmentDialogOpen);
		Assert.True(vm.IsKeyDialogRemapPurpose);
		Assert.False(vm.IsExtendedKeyEditorOpen);

		// The next editor starts clean, and a pick delivered through the (now remap-purpose, closed)
		// dialog command changes nothing in it.
		vm.StartModTapEditorCommand.Execute(null);
		vm.PickRemapTargetCommand.Execute(PickerKey(vm, "Escape"));
		Assert.False(vm.HasExtendedKeyEditorTap);
		Assert.False(vm.HasExtendedKeyEditorHold);
		Assert.Empty(hid.ExtendedKeysWrites);
	}

	[Fact]
	public async Task ModTap_PresetPushCarriesTimeAndBothOutputs_AndSkipsAnEqualSet()
	{
		var (vm, store, hid, owner) = NewVm();
		await Open(vm, hid, Info(ExtendedKeyRecord.ModTap(SlotCaps, 100, Caps, Esc)));

		// Same physical key, different threshold and hold output: a different record.
		store.ReplaceDeviceExtendedKeys(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
			[ExtendedKeyRecord.ModTap(SlotCaps, 400, LeftCtrl, Esc)]);
		Assert.True(await vm.ApplyStoredExtendedKeysToDeviceAsync());
		Assert.Equal([ExtendedKeyRecord.ModTap(SlotCaps, 400, LeftCtrl, Esc)], Assert.Single(hid.ExtendedKeysWrites));
		var row = Assert.Single(vm.ExtendedKeys);
		Assert.Equal("CTRL (L)", row.HoldLabel);
		Assert.Equal(400, row.Record.TimeMs);
		Assert.True(row.HasParameterText);

		// Board now matches time and outputs: no second write.
		Assert.False(await vm.ApplyStoredExtendedKeysToDeviceAsync());
		Assert.Single(hid.ExtendedKeysWrites);
	}

	// ── Toggle (docs/plans/add-extended-key-toggle) ───────────────────────────

	private const int SlotT = 20;
	private const int SlotW = 17;
	private static readonly ExtendedKeyCodeRecord W = new(0x10, 0x00, 0x1A);

	// The acceptance criterion's flow: T on the overlay, the toggled key → W through the picker, 100 ms.
	private static async Task AddTToggleW(DeviceDetailsViewModel vm, int timeMs = ExtendedKeyTime.DefaultMs)
	{
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartToggleEditorCommand.Execute(null);
		vm.SelectExtendedKey(Key(vm, "T"));
		vm.PickExtendedKeyHoldCommand.Execute(null);
		vm.PickRemapTargetCommand.Execute(PickerKey(vm, "W"));
		vm.ExtendedKeyEditorTimeMs = timeMs;
		await vm.ConfirmExtendedKeyCommand.ExecuteAsync(null);
	}

	[Fact]
	public async Task Toggle_RowIsAvailable_AndTheEditorOpensAsSingleKeyWithOneOutputChip()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info());
		vm.ActiveSection = NavSection.ExtendedKeys;

		Assert.True(vm.IsToggleModeAvailable);
		Assert.True(vm.CanAddToggle);

		vm.StartToggleEditorCommand.Execute(null);

		Assert.True(vm.IsExtendedKeyEditorOpen);
		Assert.True(vm.IsExtendedKeyEditorSingleKey);
		Assert.False(vm.IsExtendedKeyEditorPair);
		Assert.False(vm.IsExtendedKeyEditorSocd);
		// One output chip, stretched over the Tap chip's column too.
		Assert.False(vm.ShowExtendedKeyEditorTapChip);
		Assert.Equal(3, vm.ExtendedKeyEditorOutputChipSpan);
		Assert.False(string.IsNullOrEmpty(vm.ExtendedKeyEditorOutputCaption));
		Assert.True(vm.IsExtendedKeyEditorPhysicalActive);
		Assert.Equal(ExtendedKeyTime.DefaultMs, vm.ExtendedKeyEditorTimeMs);
		Assert.Equal(10.0, vm.ExtendedKeyEditorTimePct);
		Assert.False(vm.HasExtendedKeyEditorHold);
		Assert.False(vm.CanConfirmExtendedKey);
		Assert.False(string.IsNullOrEmpty(vm.ExtendedKeyEditorTimeHint));

		// The Mod-Tap editor still shows both chips at their half width.
		vm.CancelExtendedKeyEditorCommand.Execute(null);
		vm.StartModTapEditorCommand.Execute(null);
		Assert.True(vm.ShowExtendedKeyEditorTapChip);
		Assert.Equal(1, vm.ExtendedKeyEditorOutputChipSpan);
	}

	[Fact]
	public async Task Toggle_PhysicalKeyPreFillsTheToggledKey_SoConfirmNeedsNoTap()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info());
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartToggleEditorCommand.Execute(null);

		vm.SelectExtendedKey(Key(vm, "T"));

		// "T toggles T" is the one-click record; there is no Tap chip to wait for.
		Assert.Equal("T", vm.ExtendedKeyEditorKey1Label);
		Assert.Equal("T", vm.ExtendedKeyEditorHoldLabel);
		Assert.False(vm.HasExtendedKeyEditorTap);
		Assert.True(vm.CanConfirmExtendedKey);

		// The Tap picker is not reachable in this mode: the command is a no-op, no dialog opens.
		vm.PickExtendedKeyTapCommand.Execute(null);
		Assert.False(vm.IsKeyAssignmentDialogOpen);
		Assert.False(vm.HasExtendedKeyEditorTap);

		// Clearing the physical chip drops the pre-fill and disables Confirm again.
		vm.ClearExtendedKeyEditorKey1Command.Execute(null);
		Assert.False(vm.HasExtendedKeyEditorHold);
		Assert.False(vm.CanConfirmExtendedKey);
		Assert.Empty(hid.ExtendedKeysWrites);
	}

	[Fact]
	public async Task Toggle_TToggledW_ConfirmWritesTheRecord_AndTheRowShowsIt()
	{
		var (vm, store, hid, owner) = NewVm();
		await Open(vm, hid, Info());
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartToggleEditorCommand.Execute(null);
		vm.SelectExtendedKey(Key(vm, "T"));

		// The toggled-key chip opens the picker (Keys only) with the T pre-fill highlighted; W lands in it.
		vm.PickExtendedKeyHoldCommand.Execute(null);
		Assert.True(vm.IsKeyAssignmentDialogOpen);
		Assert.True(vm.IsKeyDialogPickPurpose);
		Assert.False(vm.IsKeyDialogMacrosTabVisible);
		Assert.False(vm.IsKeyDialogSystemTabVisible);
		Assert.Same(PickerKey(vm, "T"), vm.RemapTargetKeys.Single(k => k.IsSelected));
		vm.PickRemapTargetCommand.Execute(PickerKey(vm, "W"));
		Assert.False(vm.IsKeyAssignmentDialogOpen);
		Assert.Equal("W", vm.ExtendedKeyEditorHoldLabel);
		Assert.True(vm.CanConfirmExtendedKey);
		Assert.Empty(hid.KeyWrites);
		Assert.False(vm.IsRemapToastVisible);

		await vm.ConfirmExtendedKeyCommand.ExecuteAsync(null);

		// The acceptance criterion's payload: {Toggle, slot1 = T, time_ms = 100, hold_key = W}, no tap, no slot2.
		var write = Assert.Single(hid.ExtendedKeysWrites);
		Assert.Equal([ExtendedKeyRecord.Toggle(SlotT, 100, W)], write);
		Assert.Equal(ExtendedKeyCodeRecord.None, write[0].TapKey);
		Assert.Equal(ExtendedKeyRecord.NoSlot, write[0].Slot2);
		Assert.Equal(write, Live(store, owner));
		Assert.False(vm.IsExtendedKeyEditorOpen);

		// Row "T [TGL] W  100 ms": one key chip, one output chip, no tap chip.
		var row = Assert.Single(vm.ExtendedKeys);
		Assert.Equal("T", row.Key1Label);
		Assert.False(row.HasKey2);
		Assert.Equal("W", row.HoldLabel);
		Assert.True(row.HasHoldLabel);
		Assert.False(row.HasTapLabel);
		Assert.True(row.HasParameterText);
		Assert.False(string.IsNullOrEmpty(row.BadgeText));
		// The badge carries the mode's own colour (the design's blue), not MT's red.
		Assert.Equal(Color.Parse(ExtendedKeyRowViewModel.ToggleBadgeHex), Assert.IsType<ImmutableSolidColorBrush>(row.BadgeBrush).Color);
		Assert.NotEqual(Color.Parse(ExtendedKeyRowViewModel.ModTapBadgeHex), Assert.IsType<ImmutableSolidColorBrush>(row.BadgeBrush).Color);
		// Only the physical key joins the record: T is highlighted and protected, W is neither.
		Assert.True(Key(vm, "T").IsExtendedKeyHighlighted);
		Assert.False(Key(vm, "W").IsExtendedKeyHighlighted);
		Assert.True(vm.IsExtendedKeySlot(SlotT));
		Assert.False(vm.IsExtendedKeySlot(SlotW));
		Assert.NotNull(vm.GetExtendedKeyTooltip(Key(vm, "T")));
		Assert.Null(vm.GetExtendedKeyTooltip(Key(vm, "W")));
	}

	[Fact]
	public async Task Toggle_AKeyInAnotherRecordIsRefused_AndTheReverse()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info(
			ExtendedKeyRecord.RapidSnap(SlotA, SlotD),
			ExtendedKeyRecord.ModTap(SlotCaps, 150, LeftCtrl, Esc),
			ExtendedKeyRecord.Toggle(SlotT, 100, W)));
		vm.ActiveSection = NavSection.ExtendedKeys;

		// A (RS) and Caps Lock (MT) are refused as the Toggle's physical key.
		vm.StartToggleEditorCommand.Execute(null);
		await AssertRefused(vm, hid, Key(vm, "A"));
		await AssertRefused(vm, hid, Key(vm, "CapsLock"));
		Assert.Null(vm.ExtendedKeyEditorKey1Label);
		vm.CancelExtendedKeyEditorCommand.Execute(null);

		// T (TGL) is refused by the RS and MT editors; W, the toggled output, is free.
		vm.StartRapidSnapEditorCommand.Execute(null);
		await AssertRefused(vm, hid, Key(vm, "T"));
		vm.SelectExtendedKey(Key(vm, "W"));
		Assert.Equal("W", vm.ExtendedKeyEditorKey1Label);
		vm.CancelExtendedKeyEditorCommand.Execute(null);
		vm.StartModTapEditorCommand.Execute(null);
		await AssertRefused(vm, hid, Key(vm, "T"));
		vm.CancelExtendedKeyEditorCommand.Execute(null);

		// And Key Assignments blocks T like any other record's key.
		vm.ActiveSection = NavSection.Buttons;
		vm.SelectPhysicalKeyCommand.Execute(Key(vm, "T"));
		Assert.False(vm.IsKeyAssignmentDialogOpen);
		Assert.True(vm.HasExtendedKeyBlockedMessage);
	}

	[Fact]
	public async Task Toggle_PresetPushCarriesTimeAndOutput_AndSkipsAnEqualSet()
	{
		var (vm, store, hid, owner) = NewVm();
		await Open(vm, hid, Info(ExtendedKeyRecord.Toggle(SlotT, 100, new ExtendedKeyCodeRecord(0x10, 0x00, 0x17))));

		// Same physical key, different threshold and output: a different record.
		store.ReplaceDeviceExtendedKeys(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
			[ExtendedKeyRecord.Toggle(SlotT, 500, W)]);
		Assert.True(await vm.ApplyStoredExtendedKeysToDeviceAsync());
		Assert.Equal([ExtendedKeyRecord.Toggle(SlotT, 500, W)], Assert.Single(hid.ExtendedKeysWrites));
		var row = Assert.Single(vm.ExtendedKeys);
		Assert.Equal("W", row.HoldLabel);
		Assert.Equal(500, row.Record.TimeMs);
		Assert.True(row.HasParameterText);

		// Board now matches time and output: no second write.
		Assert.False(await vm.ApplyStoredExtendedKeysToDeviceAsync());
		Assert.Single(hid.ExtendedKeysWrites);
	}

	[Fact]
	public async Task Toggle_DeleteLeavesTheBoardEmpty_AndFreesTheSixBytes()
	{
		var (vm, store, hid, owner) = NewVm();
		await Open(vm, hid, Info());
		await AddTToggleW(vm);
		Assert.Single(vm.ExtendedKeys);

		await vm.DeleteExtendedKeyCommand.ExecuteAsync(vm.ExtendedKeys[0]);

		Assert.Equal(2, hid.ExtendedKeysWrites.Count);
		Assert.Empty(hid.ExtendedKeysWrites[1]);
		Assert.Empty(vm.ExtendedKeys);
		Assert.Empty(Live(store, owner));
		Assert.False(Key(vm, "T").IsExtendedKeyHighlighted);
		Assert.False(vm.IsExtendedKeySlot(SlotT));
	}

	[Fact]
	public async Task Toggle_EditorCaptionAndTimeHint_DifferFromModTaps_AndFollowTheMode()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info());
		vm.ActiveSection = NavSection.ExtendedKeys;

		vm.StartModTapEditorCommand.Execute(null);
		var modTapCaption = vm.ExtendedKeyEditorOutputCaption;
		var modTapHint = vm.ExtendedKeyEditorTimeHint;
		vm.CancelExtendedKeyEditorCommand.Execute(null);

		vm.StartToggleEditorCommand.Execute(null);
		Assert.NotEqual(modTapCaption, vm.ExtendedKeyEditorOutputCaption);
		Assert.NotEqual(modTapHint, vm.ExtendedKeyEditorTimeHint);
		Assert.False(string.IsNullOrEmpty(vm.ExtendedKeyEditorOutputCaption));
		// (The "{0} ms" substitution itself is not observable here — GetString returns the key.)

		// Back in the MT editor the MT wording returns.
		vm.CancelExtendedKeyEditorCommand.Execute(null);
		vm.StartModTapEditorCommand.Execute(null);
		Assert.Equal(modTapCaption, vm.ExtendedKeyEditorOutputCaption);
		Assert.Equal(modTapHint, vm.ExtendedKeyEditorTimeHint);
	}

	[Fact]
	public async Task Toggle_CancellingTheEditorClosesAnOpenPick_AndALatePickCannotLand()
	{
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info());
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartToggleEditorCommand.Execute(null);
		vm.SelectExtendedKey(Key(vm, "T"));
		vm.PickExtendedKeyHoldCommand.Execute(null);
		Assert.True(vm.IsKeyAssignmentDialogOpen);
		Assert.True(vm.IsKeyDialogPickPurpose);

		vm.CancelExtendedKeyEditorCommand.Execute(null);

		Assert.False(vm.IsKeyAssignmentDialogOpen);
		Assert.True(vm.IsKeyDialogRemapPurpose);
		Assert.False(vm.IsExtendedKeyEditorOpen);

		// The next Toggle editor starts clean; a pick delivered through the closed dialog changes nothing.
		vm.StartToggleEditorCommand.Execute(null);
		vm.PickRemapTargetCommand.Execute(PickerKey(vm, "W"));
		Assert.False(vm.HasExtendedKeyEditorHold);
		Assert.Null(vm.ExtendedKeyEditorKey1Label);
		Assert.Empty(hid.ExtendedKeysWrites);
		Assert.Empty(hid.KeyWrites);
	}

	[Fact]
	public async Task Toggle_AConsumerCommandDeliveredAsThePick_IsRefusedAndKeepsTheChip()
	{
		// The picker hides the System tab in pick purpose; a command row reaching the pick anyway (the
		// callback's null-safety guard) is refused like FN — the record has no code type for it.
		var (vm, _, hid, _) = NewVm();
		await Open(vm, hid, Info());
		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartToggleEditorCommand.Execute(null);
		vm.SelectExtendedKey(Key(vm, "T"));
		vm.PickExtendedKeyHoldCommand.Execute(null);

		vm.PickRemapTargetCommand.Execute(vm.SystemCommandTargets.First(t => t.DevKeyName == "Command:VolumeUp"));

		Assert.False(vm.IsKeyAssignmentDialogOpen);
		Assert.True(vm.HasExtendedKeyEditorMessage);
		Assert.Equal("T", vm.ExtendedKeyEditorHoldLabel);
		Assert.True(vm.CanConfirmExtendedKey);

		await vm.ConfirmExtendedKeyCommand.ExecuteAsync(null);
		var write = Assert.Single(hid.ExtendedKeysWrites);
		Assert.Equal((byte)0x10, (byte)Assert.Single(write).HoldKey.TypeByte);
	}

	// ── The persist goes through the storage executor (review round 3, S9) ────
	//
	// ReplaceDeviceExtendedKeys takes the storage semaphore with a NON-async Wait() and then runs a
	// DELETE plus one INSERT per record inside a transaction, all on the calling thread — and every
	// caller of PersistExtendedKeysStateAsync is on the Avalonia dispatcher. Confirm / trash /
	// DEFAULT SETTINGS therefore parked the window behind whatever the executor was in the middle
	// of. The observable difference between "inline" and "queued" is exactly this: with the
	// executor stalled, an inline write finishes anyway and a queued one cannot.

	private static Task StallStorage(LocalStorageService store, TaskCompletionSource release)
	{
		var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_ = store.RunOnStorageAsync(() => { running.TrySetResult(); release.Task.Wait(); });
		return running.Task;
	}

	[Fact]
	public async Task Persist_IsQueuedOnTheStorageExecutor_AndTheRowsStillLandWhenItRuns()
	{
		var (vm, store, hid, owner) = NewVm();
		await Open(vm, hid, Info());

		var release = new TaskCompletionSource();
		await StallStorage(store, release);

		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartRapidSnapEditorCommand.Execute(null);
		vm.SelectExtendedKey(Key(vm, "A"));
		vm.SelectExtendedKey(Key(vm, "D"));
		var confirm = vm.ConfirmExtendedKeyCommand.ExecuteAsync(null);

		// The board was told first — the HID write precedes the persist and does not queue...
		Assert.Single(hid.ExtendedKeysWrites);
		// ...but the SQLite write sits behind the stalled executor, so the command is still running
		// and nothing has been stored. Inline, both of these would already be done.
		Assert.False(confirm.IsCompleted);
		Assert.Empty(Live(store, owner));

		release.SetResult();
		await confirm;

		Assert.Equal([ExtendedKeyRecord.RapidSnap(SlotA, SlotD)], Live(store, owner));
		Assert.Equal([ExtendedKeyRecord.RapidSnap(SlotA, SlotD)], vm.ExtendedKeys.Select(r => r.Record));
		Assert.False(vm.ExtendedKeysWriteFailed);
	}

	// The control for the case below, and the ordinary outcome in its own right: the edit's rows
	// land and Save Preset lights up for the preset that is open.
	[Fact]
	public async Task Persist_OnTheOpenBoard_StoresTheRowsAndMarksItsPresetDirty()
	{
		var (vm, store, hid, owner) = NewVm();
		await Open(vm, hid, Info());
		await SelectAFreshPreset(vm, store, owner);
		Assert.False(vm.HasUnsavedPresetChanges);

		await AddPair(vm, "A", "D");

		Assert.Equal([ExtendedKeyRecord.RapidSnap(SlotA, SlotD)], Live(store, owner));
		Assert.True(vm.HasUnsavedPresetChanges);
	}

	// The switch the re-check after the await exists for. The rows are addressed to the pair the
	// call captured, so they still land on the Cyrex — but the dirty flag belongs to whatever page
	// is open NOW, and lighting Save Preset on the Aress for the Cyrex's write invites the user to
	// save one board's edit into the other board's preset.
	//
	// The Aress is given live rows the selected preset's snapshot does not have, so a stale
	// StagePresetDirtyOnly running against the new board really would report dirty — without that
	// the assertion below would hold for the wrong reason (an Aress with nothing on it matches an
	// empty snapshot, and the flag would stay dark whether the re-check existed or not).
	[Fact]
	public async Task Persist_ADeviceSwitchWhileTheWriteIsQueued_StoresTheRows_ButDoesNotDirtyTheNewBoardsPreset()
	{
		var (vm, store, hid, owner) = NewVm();
		await Open(vm, hid, Info());
		await SelectAFreshPreset(vm, store, owner);
		Assert.False(vm.HasUnsavedPresetChanges);

		store.ReplaceDeviceExtendedKeys(LocalStorageService.OwnerKindGuest, owner, AressVid, AressPid, DeviceKey,
			[ExtendedKeyRecord.Toggle(SlotQ, 100, new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A))]);
		await store.DrainStorageQueueAsync();

		var release = new TaskCompletionSource();
		await StallStorage(store, release);

		vm.ActiveSection = NavSection.ExtendedKeys;
		vm.StartRapidSnapEditorCommand.Execute(null);
		vm.SelectExtendedKey(Key(vm, "A"));
		vm.SelectExtendedKey(Key(vm, "D"));
		var confirm = vm.ConfirmExtendedKeyCommand.ExecuteAsync(null);
		Assert.False(confirm.IsCompleted);

		// The user clicks the other keyboard while the write is still in the queue.
		vm.Device = AressCard();

		release.SetResult();
		await confirm;

		// The write itself still stands — it was addressed to the Cyrex before the switch.
		Assert.Equal([ExtendedKeyRecord.RapidSnap(SlotA, SlotD)], Live(store, owner));
		// But Save Preset stays dark on the board that is now on screen.
		Assert.False(vm.HasUnsavedPresetChanges);
	}

	// ── Rows naming a slot this board has no key on (review round 3, S10) ─────
	//
	// SetExtendedKeys is a WHOLE-SET request: one record on a slot the board's matrix does not have
	// makes the host refuse every record the user really configured — permanently and invisibly,
	// because the preset push does not persist and so keeps the poisoned row. The intake cannot
	// catch it (it does not know which board the preset will land on); the App can, from the
	// per-model key catalog behind PhysicalKeys.

	// Past the end of the Cyrex's 75-slot factory table.
	private const int SlotPastTheMatrix = 96;
	// Inside the table but naming no key — {0x10, 0x00, 0x00}. See CyrexKeyCatalogTests' UnusedSlots.
	private const int SlotUnusedPlaceholder = 42;

	[Fact]
	public async Task PresetPush_DropsRowsNamingSlotsThisBoardHasNoKeyOn_AndSendsTheRest()
	{
		var (vm, store, hid, owner) = NewVm();
		await Open(vm, hid, Info());

		store.ReplaceDeviceExtendedKeys(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
		[
			ExtendedKeyRecord.RapidSnap(SlotA, SlotD),
			// Slot1 past the end of the matrix.
			ExtendedKeyRecord.Toggle(SlotPastTheMatrix, 100, new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A)),
			// Slot1 is a real key, slot2 an in-range placeholder — stricter than a range check, and
			// a record there could never have fired.
			ExtendedKeyRecord.RapidSnap(SlotS, SlotUnusedPlaceholder),
		]);

		Assert.True(await vm.ApplyStoredExtendedKeysToDeviceAsync());

		// Only the record this keyboard actually has both keys for reached the board.
		Assert.Equal([ExtendedKeyRecord.RapidSnap(SlotA, SlotD)], Assert.Single(hid.ExtendedKeysWrites));
		Assert.Equal([ExtendedKeyRecord.RapidSnap(SlotA, SlotD)], vm.ExtendedKeys.Select(r => r.Record));
		// The drop is for this board's wire only: the rows stay in storage, so plugging in the
		// keyboard they belong to still gets them.
		Assert.Equal(3, Live(store, owner).Count);
	}

	// The reason the filter runs BEFORE the "already matches" skip. With it after, every open of
	// this board would find the stored set different from the board's, write the kept records, and
	// find them different again next time — a rewrite loop of flash writes for no change.
	[Fact]
	public async Task PresetPush_ASetDifferingOnlyByADroppedRow_WritesNothing_OnThisOpenOrTheNext()
	{
		var (vm, store, hid, owner) = NewVm();
		await Open(vm, hid, Info(ExtendedKeyRecord.RapidSnap(SlotA, SlotD)));

		store.ReplaceDeviceExtendedKeys(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
		[
			ExtendedKeyRecord.RapidSnap(SlotA, SlotD),
			ExtendedKeyRecord.Toggle(SlotPastTheMatrix, 100, new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A)),
		]);

		Assert.False(await vm.ApplyStoredExtendedKeysToDeviceAsync());
		Assert.Empty(hid.ExtendedKeysWrites);

		// The next open of the same board: still nothing.
		await vm.LoadExtendedKeysFromDeviceAsync();
		Assert.False(await vm.ApplyStoredExtendedKeysToDeviceAsync());
		Assert.Empty(hid.ExtendedKeysWrites);
	}

	// The board-side twin of the off-catalog row above (review round 4, m1). A slot the host reports
	// BLOCKED already carries a record it preserves but will not write over — a vendor-app DKS, or a
	// managed type this build cannot re-encode — so an owner row landing there is refused, and
	// SetExtendedKeys being a whole-set request takes every other record down with it, on every open,
	// invisibly. Same treatment as an off-catalog row: dropped from the wire, kept in storage, because
	// the vendor app can free the slot again and the next open would then push it as normal.
	[Fact]
	public async Task PresetPush_DropsARowLandingOnABlockedSlot_AndSendsTheRest()
	{
		var (vm, store, hid, owner) = NewVm();
		// Q is owned by an unmanaged record; S is free, and is the second slot of the row below, so
		// this also pins that a PAIR is judged on BOTH of its slots and not just the one it is keyed by.
		await Open(vm, hid, Info() with { BlockedSlots = [SlotQ, SlotS] });

		store.ReplaceDeviceExtendedKeys(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey,
		[
			ExtendedKeyRecord.RapidSnap(SlotA, SlotD),
			// Slot1 is blocked.
			ExtendedKeyRecord.Toggle(SlotQ, 100, new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A)),
			// Slot1 is a free key, slot2 is blocked.
			ExtendedKeyRecord.RapidSnap(SlotE, SlotS),
		]);

		Assert.True(await vm.ApplyStoredExtendedKeysToDeviceAsync());

		// Only the record whose keys this board will actually take reached it.
		Assert.Equal([ExtendedKeyRecord.RapidSnap(SlotA, SlotD)], Assert.Single(hid.ExtendedKeysWrites));
		Assert.Equal([ExtendedKeyRecord.RapidSnap(SlotA, SlotD)], vm.ExtendedKeys.Select(r => r.Record));
		// The drop is for this wire only — the rows survive, so freeing the slot restores them.
		Assert.Equal(3, Live(store, owner).Count);
	}

	// The safety valve. PhysicalKeys is filled by LoadKeyLayoutForDevice, which is a different step
	// from the probe — so a board whose layout has not been built yet has an EMPTY catalog, and a
	// filter that believed it would read "this board has no slots" and drop every record the user
	// owns. The filter must be a no-op there, off-board rows included.
	[Fact]
	public async Task PresetPush_WhileTheKeyLayoutIsNotBuiltYet_DropsNothing()
	{
		var (vm, store, hid, owner) = NewVm();
		hid.Info = Info();
		vm.Device = CyrexCard();
		// Deliberately no LoadKeyLayoutForDevice.
		await vm.LoadExtendedKeysFromDeviceAsync();
		Assert.Empty(vm.PhysicalKeys);

		var stored = new List<ExtendedKeyRecord>
		{
			ExtendedKeyRecord.RapidSnap(SlotA, SlotD),
			ExtendedKeyRecord.Toggle(SlotPastTheMatrix, 100, new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A)),
		};
		store.ReplaceDeviceExtendedKeys(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid, DeviceKey, stored);

		Assert.True(await vm.ApplyStoredExtendedKeysToDeviceAsync());

		Assert.Equal(ExtendedKeyRecord.NormalizeSet(stored), Assert.Single(hid.ExtendedKeysWrites));
	}

	// ── Helpers for the two sections above ────────────────────────────────────

	// The second extended-keys keyboard. Same vendor as the Cyrex — they really do share one — so
	// everything about "which board" is decided on the product id.
	private const int AressVid = 10473;
	private const int AressPid = 12910;

	private static SingleCardViewModel AressCard() => new()
	{
		Name = "Aress",
		DeviceTypeLabel = "Wired Keyboard",
		VendorId = AressVid,
		ProductId = AressPid,
		SettingsVendorId = AressVid,
		SettingsProductId = AressPid,
		DeviceKey = DeviceKey,
		SettingsDeviceKey = DeviceKey,
		SupportsExtendedKeys = true,
	};

	// A preset row with an empty snapshot, selected the way the dropdown does it. StagePresetDirtyOnly
	// is a no-op without one, so the dirty flag is unobservable until this has run.
	private static async Task SelectAFreshPreset(
		DeviceDetailsViewModel vm, LocalStorageService store, string owner, string name = "A")
	{
		var id = store.InsertPreset(LocalStorageService.OwnerKindGuest, owner, CyrexVid, CyrexPid,
			new PresetRecord { Name = name });
		var row = new PresetViewModel { Id = id, Name = name };
		vm.Presets.Add(row);
		await vm.SelectPresetCommand.ExecuteAsync(row);
		await store.DrainStorageQueueAsync();
	}
}
