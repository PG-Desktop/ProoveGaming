using CommunityToolkit.Mvvm.Input;
using Avalonia;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ProoveGaming.App.Models.Settings;
using ProoveGaming.App.Services;
using ProoveGaming.GrpcDependencies.Device;

namespace ProoveGaming.App.ViewModels;

public partial class DeviceDetailsViewModel : ViewModelBase, IDeviceDetailsExit
{
	// ── Constants ─────────────────────────────────────────────────────────────
	private const int NavButtonHeight = 64;
	private const int NavButtonSpacing = 4;
	private const int NavFrameVerticalInset = 20;

	// ── Services ──────────────────────────────────────────────────────────────
	private readonly IUsbHidDeviceService? _usbHidDeviceService;
	private readonly LocalStorageService? _localStorage;
	private readonly ISettingsRepository? _settings;
	private readonly ICurrentOwnerProvider? _ownerProvider;
	private readonly IAudioEndpointService? _audioEndpoints;
	private readonly ISettingsSyncService? _settingsSync;
	private readonly Microsoft.Extensions.Logging.ILogger<DeviceDetailsViewModel>? _logger;


	// ── Collections ───────────────────────────────────────────────────────────
	// Only confirmed-working effects (from hardware testing 2026-06-30):
	//   Breathing        → proto 0x02, colour picker visible (custom RGB)
	//   RainbowBreathing → proto 0x02 + colorSlot=7, no picker (auto-cycles all colours)
	//   Neon             → proto 0x03, no colour picker (built-in cycling)
	//   ColorCycle       → proto 0x04, no colour picker (built-in cycling)
	public IReadOnlyList<RgbEffectSettingsViewModel> RgbEffects { get; }
	// Rebuilt per device from its catalog DPI stages (see RebuildDpiPresets); the DPI view binds
	// to this collection, so replacing its items updates the UI when the shown device changes.
	public ObservableCollection<DpiPresetViewModel> DpiPresets { get; } = [];
	public IReadOnlyList<PollingRateOptionViewModel> PollingRateOptions { get; }
	public ObservableCollection<ButtonActionGroupViewModel> ButtonActionGroups { get; }
	public ObservableCollection<MacroViewModel> Macros { get; } = [];

	// ── Navigation backing fields ─────────────────────────────────────────────
	private DeviceCardViewModel? _device;
	private DevicePartHotspotViewModel? _selectedHotspot;
	private NavSection _activeSection = NavSection.Presets;

	// ── Constructor ───────────────────────────────────────────────────────────
	public DeviceDetailsViewModel(
		IUsbHidDeviceService? usbHidDeviceService = null,
		LocalStorageService? localStorage = null,
		ISettingsRepository? settings = null,
		ICurrentOwnerProvider? ownerProvider = null,
		IAudioEndpointService? audioEndpoints = null,
		ISettingsSyncService? settingsSync = null,
		Microsoft.Extensions.Logging.ILogger<DeviceDetailsViewModel>? logger = null)
	{
		_usbHidDeviceService = usbHidDeviceService;
		_localStorage = localStorage;
		_settings = settings;
		_ownerProvider = ownerProvider;
		_audioEndpoints = audioEndpoints;
		_settingsSync = settingsSync;
		_logger = logger;

		RgbEffects =
		[
			// The catalog describes the effects themselves and knows nothing about device families.
			// Which of them a given device offers, and which controls each one exposes there, is
			// decided by RecomputeVisibleRgbEffects — see KeyboardOnlyEffectKeys.
			new("Static",           "DeviceDetails_Rgb_Effect_Static",           hasColorPicker: true,  hasBrightness: true, hasSpeed: false, SelectEffect, onColorApplied: OnEffectColorApplied),
			new("Breathing",        "DeviceDetails_Rgb_Effect_Breathing",        hasColorPicker: true,  hasBrightness: true, hasSpeed: true,  SelectEffect, onColorApplied: OnEffectColorApplied),
			new("Neon",             "DeviceDetails_Rgb_Effect_NeonFlowing",      hasColorPicker: false, hasBrightness: true, hasSpeed: true,  SelectEffect, onColorApplied: OnEffectColorApplied),
			new("ColorCycle",       "DeviceDetails_Rgb_Effect_ColorCycle",       hasColorPicker: false, hasBrightness: true, hasSpeed: true,  SelectEffect, onColorApplied: OnEffectColorApplied),
			new("Wave",             "DeviceDetails_Rgb_Effect_Wave",             hasColorPicker: false, hasBrightness: true, hasSpeed: true,  SelectEffect, onColorApplied: OnEffectColorApplied),
		];
		_visibleRgbEffects = RgbEffects;
		RgbEffects.First(e => e.EffectKey == "Breathing").IsSelected = true;
		_selectedEffect = RgbEffects.First(e => e.EffectKey == "Breathing");

		foreach (var effect in RgbEffects)
			effect.PropertyChanged += OnRgbEffectPropertyChanged;

		UpdateGlowBrush();
		if (IsRgbEnabled)
			StartEffectAnimation(_selectedEffect);

		// DPI presets are device-specific (BLAZE vs Taipan differ), so they are built per device
		// in RebuildDpiPresets when a device is shown — not fixed here. Colours are display-only.
		// DefaultDpiStages (below) already carries the corrected BLAZE 800-12000 table.
		RebuildDpiPresets(null);

		PollingRateOptions =
		[
			new PollingRateOptionViewModel(125,  SelectPollingRate),
			new PollingRateOptionViewModel(250,  SelectPollingRate),
			new PollingRateOptionViewModel(500,  SelectPollingRate),
			new PollingRateOptionViewModel(1000, SelectPollingRate),
		];
		// Factory default is 1000 Hz (X43 spec sheet 3311).
		PollingRateOptions.First(o => o.Hz == FallbackPollingRateHz).IsSelected = true;

		_macrosesGroup = CreateGroup("DeviceDetails_Buttons_Group_Macroses", false, [], SelectButtonActionCommand);

		ButtonActionGroups =
		[
			// Standard mouse buttons — used to revert a remapped button to a normal function.
			CreateActionGroup("DeviceDetails_Buttons_Group_Standard", true,
			[
				("DeviceDetails_Buttons_Action_LeftClick",  MouseButtonAction.LeftClick),
				("DeviceDetails_Buttons_Action_RightClick", MouseButtonAction.RightClick),
				("DeviceDetails_Buttons_Action_WheelClick", MouseButtonAction.WheelClick),
				("DeviceDetails_Buttons_Action_Forward",    MouseButtonAction.Forward),
				("DeviceDetails_Buttons_Action_MouseBack",  MouseButtonAction.Back),
			]),
			// Recommended — multimedia keys.
			CreateActionGroup("DeviceDetails_Buttons_Group_Recommended", true,
			[
				("DeviceDetails_Buttons_Action_Player",     MouseButtonAction.MediaPlayer),
				("DeviceDetails_Buttons_Action_PlayPause",  MouseButtonAction.PlayPause),
				("DeviceDetails_Buttons_Action_Next",       MouseButtonAction.NextTrack),
				("DeviceDetails_Buttons_Action_Back",       MouseButtonAction.PreviousTrack),
				("DeviceDetails_Buttons_Action_Stop",       MouseButtonAction.Stop),
				("DeviceDetails_Buttons_Action_Mute",       MouseButtonAction.Mute),
				("DeviceDetails_Buttons_Action_VolumeUp",   MouseButtonAction.VolumeUp),
				("DeviceDetails_Buttons_Action_VolumeDown", MouseButtonAction.VolumeDown),
				("DeviceDetails_Buttons_Action_Home",       MouseButtonAction.WwwHome),
			]),
			_macrosesGroup,
			// System.
			CreateActionGroup("DeviceDetails_Buttons_Group_System", false,
			[
				("DeviceDetails_Buttons_Action_MyPc",    MouseButtonAction.MyComputer),
				("DeviceDetails_Buttons_Action_Desktop", MouseButtonAction.Desktop),
				("DeviceDetails_Buttons_Action_Search",  MouseButtonAction.WwwSearch),
			]),
		];

		Macros.CollectionChanged += (_, _) =>
		{
			SyncMacrosesGroupOptions();
			ApplyMacroSearch();
			// Keyboard macro targets are one row per library macro, and their device slot is
			// the macro's position in the library — so both have to follow every add/remove.
			RebuildMacroAssignmentTargets();
		};
		// The macro library is per-device now, so it's loaded in SetDevice (not here).

		LocalizationManager.LanguageChanged += OnLanguageChanged;

		// DeviceHost pushes physical DPI-button / polling-button changes (and the
		// startup state query) over the gRPC stream; highlight the matching stage /
		// rate for the currently-shown device (on the UI thread).
		if (_usbHidDeviceService != null)
		{
			_usbHidDeviceService.DpiStageChanged += OnDpiStageChanged;
			_usbHidDeviceService.ReportRateChanged += OnReportRateChanged;
		}

		// This VM is a DI singleton for the app's lifetime, same as the subscriptions above — so a
		// late-finishing image prewarm (see PrewarmDeviceOnlyBitmapAsync) can still reach whichever
		// device is open right now, if it happens to be the one that just finished decoding.
		DeviceOnlyBitmapCached += OnDeviceOnlyBitmapCached;
	}

	// The device-push handlers below marshal to the UI thread through this seam rather than
	// calling Dispatcher.UIThread.Post directly. In the app it IS that Post; tests replace it
	// with an inline invoke, because the process-wide Dispatcher's first-touch thread affinity
	// makes RunJobs-based draining racy against background continuations from earlier tests —
	// the exact trap AssemblyInfo.cs documents, and a real CI failure, not a theoretical one.
	internal Action<Action> PostToUi { get; set; } = action => Dispatcher.UIThread.Post(action);

	private void OnDpiStageChanged(object? sender, DpiStageChange e)
	{
		if (Device == null || e.VendorId != Device.VendorId || e.ProductId != Device.ProductId ||
			!string.Equals(e.DeviceKey ?? string.Empty, Device.DeviceKey ?? string.Empty, StringComparison.OrdinalIgnoreCase))
			return;

		var card = Device;
		PostToUi(() =>
		{
			HighlightActiveDpiStage(e.StageIndex);
			_dpiStageKnown = true;
			// Persist the device-driven stage change so reopening the app shows the
			// correct highlight before the DeviceHost pushes a fresh state. The event carries
			// the live transport's identity; settings belong to the card's stable one.
			if (_settings != null)
				_ = _settings.SaveActiveDpiStageAsync(card.SettingsVendorId, card.SettingsProductId, card.SettingsDeviceKey, e.StageIndex);

			// A physical DPI-button press changes the device the same way picking a stage in the
			// UI does (SelectDpiPreset), so it must mark the preset dirty the same way too —
			// otherwise the change sits live on the device/in device_settings with no way to Save
			// or Discard it back. BUT this event also fires as the DeviceHost's own confirmation of
			// a stage the APP just wrote (preset switch / Save Preset / AssertActiveDpiStageAsync) —
			// without this check, that echo raced _suppressPresetDirty (which is already cleared by
			// the time this Dispatcher.Post callback runs) and spuriously enabled Save Preset right
			// after switching presets. _assertedDpiStages holds exactly the stage the app itself last
			// wrote, so a push matching it is an echo, not a new physical action.
			if (AssertedStageIdentity(card) is { } identity
				&& _assertedDpiStages.TryGetValue(identity, out var assertedStage) && assertedStage == e.StageIndex)
				return;

			// Dirty only — NOT StageChangeAndApply. The press already changed the device, so there
			// is nothing to write, and the write-back was harmful: 250 ms after each press the whole
			// configuration went out, active stage included. On TAIPAN that is a config-mode session
			// that rewrites MsConfig, so a press landing during it was undone and the next one cycled
			// on from the stage the app had written — the highlight jumped around instead of walking
			// the points in order.
			//
			// The press is still a FACT about the device, so it is recorded as the known stage. That
			// used to happen as a side effect of the write-back (ApplyCurrentStateToDeviceAsync notes
			// what it wrote). Without it the echo guard above stayed on the preset's stage: cycling
			// back to it read as an echo and left the preset dirty, and re-opening the page asserted
			// a stage the device already held — a flash write on TAIPAN.
			NoteAssertedDpiStage(card, e.StageIndex);
			StagePresetDirtyOnly();
		});
	}

	private void OnReportRateChanged(object? sender, ReportRateChange e)
	{
		if (Device == null || e.VendorId != Device.VendorId || e.ProductId != Device.ProductId ||
			!string.Equals(e.DeviceKey ?? string.Empty, Device.DeviceKey ?? string.Empty, StringComparison.OrdinalIgnoreCase))
			return;

		var card = Device;
		PostToUi(() =>
		{
			HighlightPollingRate(e.Hz);
			_reportRateKnown = true;
			// Mirrors OnDpiStageChanged: persist the device-driven rate (SelectPollingRate does
			// the same on a UI click) and mark the preset dirty so a physical polling-button press
			// enables Save Preset / triggers the unsaved-changes prompt exactly like a UI change.
			if (_settings != null)
				_ = _settings.SavePollingRateAsync(card.SettingsVendorId, card.SettingsProductId, card.SettingsDeviceKey, e.Hz);

			// Same echo guard as OnDpiStageChanged: skip when this push just confirms a rate the app
			// itself last wrote (preset switch / Save Preset), rather than a genuine physical change.
			if (AssertedStageIdentity(card) is { } identity
				&& _assertedReportRates.TryGetValue(identity, out var assertedRate) && assertedRate == e.Hz)
				return;

			// Dirty only, for the same reason as OnDpiStageChanged: the device already changed
			// itself, and a full configuration write-back could undo a DPI press landing inside it.
			// Recorded as the known rate for the same reason the DPI handler notes its stage.
			NoteAssertedReportRate(card, e.Hz);
			StagePresetDirtyOnly();
		});
	}

	// ── Device & Navigation ───────────────────────────────────────────────────
	public DeviceCardViewModel? Device
	{
		get => _device;
		set
		{
			var previous = _device;
			if (SetProperty(ref _device, value))
			{
				// Watch the card's transport flags while its page is open: plugging a cable into
				// a Bluetooth-connected board must wake the page up (see OnDeviceCardTransportChanged).
				// The -= before += keeps the subscription single even if a future path ever swaps
				// the field without coming through here. Recovery arms from the CURRENT state, not
				// an event: a page opened on Bluetooth sees no HasBluetooth change.
				if (previous is not null)
					previous.PropertyChanged -= OnDeviceCardTransportChanged;
				if (value is not null)
				{
					value.PropertyChanged -= OnDeviceCardTransportChanged;
					value.PropertyChanged += OnDeviceCardTransportChanged;
				}
				_bluetoothRecoveryArmed = value?.HasBluetooth == true;

				RecomputeVisibleRgbEffects();
				OnPropertyChanged(nameof(IsKeyboardDevice));
				OnPropertyChanged(nameof(IsMouseDevice));
				OnPropertyChanged(nameof(HasInteractiveKeyboardOverlay));
				OnPropertyChanged(nameof(KeyboardArt));
				OnPropertyChanged(nameof(HasRgbKeyboardArt));
				// The key-overlay art layer binds this and not KeyboardArt — they diverge on Aress.
				OnPropertyChanged(nameof(KeyOverlayArt));
				OnPropertyChanged(nameof(KeyOverlayArtDim));
				// These three pick which keyboard image renders and are derived from
				// HasRgbKeyboardArt as well as the section, so they go stale on a device swap
				// unless raised here too. Presets is the default section, so opening a device
				// fires no section change: without this the flags kept the values they had
				// while Device was null and the flat white-gap artwork rendered until the user
				// clicked another nav item.
				OnPropertyChanged(nameof(ShowKeyboardOverlayInMacros));
				OnPropertyChanged(nameof(ShowKeyboardOverlayInActuation));
				OnPropertyChanged(nameof(ShowKeyboardOverlayInExtendedKeys));
				OnPropertyChanged(nameof(ShowLayeredKeyboardImage));
				OnPropertyChanged(nameof(ShowFlatKeyboardImage));
				OnPropertyChanged(nameof(MacrosContentPadding));
				OnPropertyChanged(nameof(IsStackNavLayout));
				OnPropertyChanged(nameof(IsButtonsNavVisible));
				OnPropertyChanged(nameof(IsKeyAssignmentNavVisible));
				OnPropertyChanged(nameof(IsDpiNavVisible));
				OnPropertyChanged(nameof(IsSettingsNavVisible));
				OnPropertyChanged(nameof(IsAudioNavVisible));
				OnPropertyChanged(nameof(IsActuationNavVisible));
				OnPropertyChanged(nameof(IsExtendedKeysNavVisible));

				OnPropertyChanged(nameof(IsRgbSupported));
				OnPropertyChanged(nameof(IsButtonRemapSupported));
				OnPropertyChanged(nameof(IsMacrosSupported));
				// The nav backdrop is sized from the button count, and both depend on the flags
				// above — without these the frame keeps the previous device's height.
				OnPropertyChanged(nameof(VisibleNavButtonsCount));
				OnPropertyChanged(nameof(ActionsMenuHeight));
				OnPropertyChanged(nameof(KeyboardNavButtonsCount));
				OnPropertyChanged(nameof(KeyboardActionsMenuHeight));
				OnPropertyChanged(nameof(DeviceDetailImagePath));
				OnPropertyChanged(nameof(DeviceDetailImageUri));
				OnPropertyChanged(nameof(IsRasterDeviceImage));
				OnPropertyChanged(nameof(HasVectorDeviceImage));
				OnPropertyChanged(nameof(HasDecorativeGlow));
				OnPropertyChanged(nameof(IsDecorativeGlowVisible));
				OnPropertyChanged(nameof(GlowCanvasWidth));
				OnPropertyChanged(nameof(GlowCanvasHeight));
				OnPropertyChanged(nameof(GlowLayersBehind));
				OnPropertyChanged(nameof(GlowLayersOver));
				OnPropertyChanged(nameof(DeviceImageWidth));
				OnPropertyChanged(nameof(DeviceImageHeight));
				OnPropertyChanged(nameof(PartHotspots));
				OnPropertyChanged(nameof(HasPartHotspots));
				OnPropertyChanged(nameof(IsButtonDetailsPanelVisible));
				OnPropertyChanged(nameof(IsSecondPanelVisible));
				OnPropertyChanged(nameof(ButtonsBackground3Margin));
				OnPropertyChanged(nameof(MenuHeaderTitle));
				SelectedHotspot = null;
				SyncHotspotCanvasWidth();
				RebuildDpiPresets(value);
				// Mirrors _dpiStageKnown: PollingRateOptions is a fixed list (not rebuilt per
				// device like DpiPresets), so its "known" flag is reset here instead.
				_reportRateKnown = false;
				ApplyKnownDeviceState();
			}
		}
	}

	// The DeviceHost pushes the active DPI stage / report rate once, when the app
	// subscribes — usually BEFORE the user opens this device (Device was null then, so
	// the push was dropped). So when a device is opened, apply the last values the host
	// reported for it; fall back to the UI defaults if none are known yet. Later
	// physical button presses update them live via OnDpiStageChanged / OnReportRateChanged.
	// See docs/mouse/blaze/dpi-stage.md.
	private void ApplyKnownDeviceState()
	{
		if (_device == null || _usbHidDeviceService == null) return;

		if (_usbHidDeviceService.TryGetLastDpiStage(_device.VendorId, _device.ProductId, _device.DeviceKey, out var stage))
		{
			HighlightActiveDpiStage(stage);
			_dpiStageKnown = true;
		}
		else
		{
			// No live value yet — show stage 0 as a placeholder, but do NOT mark the stage as
			// known: DpiPresets[0].IsSelected staying true here must not be read as "the device
			// is on stage 0" by a later device write (see _dpiStageKnown).
			HighlightActiveDpiStage(0);
		}

		if (_usbHidDeviceService.TryGetLastReportRate(_device.VendorId, _device.ProductId, _device.DeviceKey, out var hz))
		{
			HighlightPollingRate(hz);
			_reportRateKnown = true;
		}
		else
		{
			// No live value yet — deselect every option instead of leaving whichever one was
			// selected for the PREVIOUS device highlighted (PollingRateOptions is a fixed list
			// reused across devices, unlike DpiPresets). Do NOT mark the rate as known.
			foreach (var option in PollingRateOptions)
				option.IsSelected = false;
		}
	}

	// Display-only colours for the DPI stage indicators, assigned positionally to however many
	// stages the device declares (never written to the device).
	private static readonly (byte r, byte g, byte b)[] DpiStageColors =
	[
		(0x00, 0xFF, 0x00), (0xFF, 0xFF, 0x00), (0x00, 0xFF, 0xFF), (0xFF, 0x88, 0x00),
		(0xFF, 0x00, 0xFF), (0x00, 0x88, 0xFF), (0xFF, 0x00, 0x00), (0xFF, 0xFF, 0xFF),
	];

	// TAIPAN's stage LED colours are FIXED IN FIRMWARE — its DPI-colour table (command 0x0C/0x4C)
	// is not implemented on this hardware (every other read command answers; 0x4C never does), so
	// they can't be driven from the app. These mirror what the physical indicator actually shows,
	// stage for stage, so the swatches on screen match the mouse.
	private static readonly (byte r, byte g, byte b)[] TaipanDpiStageColors =
	[
		(0xFF, 0x00, 0x00), // 800   red
		(0x00, 0x00, 0xFF), // 1200  blue
		(0x8A, 0x00, 0xFF), // 2400  purple
		(0x00, 0xFF, 0x00), // 3600  green
		(0xFF, 0xFF, 0x00), // 4800  yellow
		(0x00, 0xFF, 0xFF), // 6000  aqua
		(0xAA, 0xFF, 0x00), // 12000 yellow-green
		// The 8th slot only exists once the user adds a point past the shipped seven, and what the
		// mouse's indicator actually shows there is unrecorded — the firmware owns it and the colour
		// table (0x0C) is not implemented on this hardware. White is a placeholder; correct it if a
		// unit is ever observed on an 8-stage ladder. It is display-only either way.
		(0xFF, 0xFF, 0xFF),
	];

	// Rendered box for the device image and its glow. The artwork assets are not framed
	// consistently (BLAZE's has padding baked in and renders visibly smaller than TAIPAN's at the
	// same box size), so each device's config may declare an optional ImageScale; unset means 1.0.
	// The glow scales with the image so it keeps hugging the device.
	private const double DeviceImageBaseWidth = 620;
	private const double DeviceImageBaseHeight = 460;

	private double DeviceImageScale => Device?.ImageScale ?? 1.0;

	public double DeviceImageWidth => DeviceImageBaseWidth * DeviceImageScale;
	public double DeviceImageHeight => DeviceImageBaseHeight * DeviceImageScale;

	// Fallback DPI stages (BLAZE) used when a device's catalog config declares none.
	private static readonly int[] DefaultDpiStages = [800, 1200, 2400, 3600, 4800, 6000, 12000];

	// True once the currently-selected DpiPresets row reflects a REAL known stage (persisted, a
	// confirmed host push, or the user's own click) rather than just RebuildDpiPresets' structural
	// default (index 0). A device write must never send an "active stage" derived from the
	// unknown/default selection — see ApplyCurrentStateToDeviceAsync in DeviceDetailsViewModel.Presets.cs.
	private bool _dpiStageKnown;

	// Same rule as _dpiStageKnown, for PollingRateOptions. PollingRateOptions is a fixed list built
	// once in the constructor (not rebuilt per device), so without this flag the selection from a
	// PREVIOUS device (or the constructor's 1000 Hz default) would look like a real, known value and
	// get written to a device the user never touched. Reset on every device change (Device setter).
	private bool _reportRateKnown;

	// ── Device defaults ───────────────────────────────────────────────────────
	// The single point that turns the catalog's per-device defaults into concrete values. Both reset
	// affordances (DPI "Recommendations", Settings "Restore to default") and the first-add seeding
	// read from here, so the buttons and docs/device-defaults.md cannot drift apart. A device whose
	// config omits a field keeps the VM's own built-in constant, so behaviour is unchanged for any
	// device with no Defaults block at all.
	// False when the transport the device is currently on cannot carry configuration — TAIPAN over
	// its 2.4G receiver or Bluetooth, where writes are accepted by the receiver and never applied.
	//
	// On such a transport the details page is READ-ONLY: nothing is pushed to the DeviceHost and
	// nothing is written to the database, so the values shown stay exactly what was persisted from
	// the last cable session. Writing anyway would record settings the hardware never received and
	// then present them as if it had.
	//
	// Guarded at the flow's entry points rather than at each of the ~35 individual write calls:
	// enumerating those is how one gets missed.
	internal bool IsDeviceConfigurable => Device?.SupportsConfiguration != false;

	private DeviceDefaults CurrentDefaults => Device?.Defaults ?? new DeviceDefaults();

	// The default DPI is configured as a VALUE, not a stage index, so a device with its own ladder
	// still lands somewhere sensible. Exact match wins; otherwise the closest stage; ties go to the
	// lower DPI (the safer of two equally-distant choices). Null when the device has no default or
	// no stages to pick from.
	internal int? ResolveDefaultDpiStageIndex()
	{
		if (CurrentDefaults.Dpi is not { } wanted || DpiPresets.Count == 0)
			return null;

		DpiPresetViewModel? best = null;
		var bestDistance = int.MaxValue;
		foreach (var preset in DpiPresets)
		{
			var distance = Math.Abs(preset.DpiValue - wanted);
			if (distance < bestDistance || (distance == bestDistance && best is not null && preset.DpiValue < best.DpiValue))
			{
				best = preset;
				bestDistance = distance;
			}
		}
		return best?.StageIndex;
	}

	// The catalog's report-rate default, resolved to a rate the UI can actually show.
	// PollingRateOptions is a FIXED list (125/250/500/1000) and HighlightPollingRate selects by
	// exact match, so an unlisted value would deselect every row: the section renders blank while
	// _reportRateKnown claims a value is known, and RestoreDpiSectionDefaultsAsync would persist it,
	// making the blank permanent across opens. Resolving to the nearest supported rate mirrors what
	// ResolveDefaultDpiStageIndex does for a DPI value that is not exactly on the ladder, so a
	// config author gets a sensible rate rather than an empty section and no error anywhere.
	internal int DefaultReportRateHz
	{
		get
		{
			if (CurrentDefaults.ReportRateHz is not { } wanted)
				return FallbackPollingRateHz;

			PollingRateOptionViewModel? best = null;
			var bestDistance = int.MaxValue;
			foreach (var option in PollingRateOptions)
			{
				var distance = Math.Abs(option.Hz - wanted);
				// Ties go to the lower rate: under-driving the device is the safer miss.
				if (distance < bestDistance || (distance == bestDistance && best is not null && option.Hz < best.Hz))
				{
					best = option;
					bestDistance = distance;
				}
			}
			return best?.Hz ?? FallbackPollingRateHz;
		}
	}

	// Rebuilds the DPI presets from the device's catalog DPI stages (or the default set when none
	// are configured), so BLAZE and Taipan show their own ranges. Called when the shown device
	// changes. Colours are display-only. Rows are clickable only where the device can set its
	// active stage (see SupportsDpiStageSwitch); elsewhere they mirror the physical DPI button.
	private void RebuildDpiPresets(DeviceCardViewModel? device)
	{
		_dpiStageKnown = false;
		IReadOnlyList<int> values = device?.DpiStages is { Count: > 0 } ? device.DpiStages : DefaultDpiStages;
		BuildDpiPresets(device, values, selectedIndex: 0);
	}

	// Builds the rows from an explicit ladder, which is what lets the ladder be user-defined: the
	// catalog rebuild above, a persisted ladder of a different length, and the add/remove commands
	// all come through here.
	//
	// Colours are POSITIONAL, not per value — TAIPAN's indicator colour belongs to the stage slot
	// and is fixed in its firmware — so removing a point has to re-colour every row below it. That
	// is why add/remove rebuild the list rather than mutate it in place.
	private void BuildDpiPresets(DeviceCardViewModel? device, IReadOnlyList<int> values, int selectedIndex)
	{
		var interactive = device?.SupportsDpiStageSwitch == true;

		// TAIPAN's indicator colours are fixed in firmware, so its swatches use the real ones.
		var palette = string.Equals(device?.Name, "PG TAIPAN", StringComparison.OrdinalIgnoreCase)
			? TaipanDpiStageColors
			: DpiStageColors;

		var slotMin = device?.DpiSlotMin ?? DeviceCardViewModel.FallbackDpiSlotMin;

		DpiPresets.Clear();
		for (int i = 0; i < values.Count; i++)
		{
			var (r, g, b) = palette[Math.Min(i, palette.Length - 1)];
			DpiPresets.Add(new DpiPresetViewModel(i, values[i], r, g, b, SelectDpiPreset, interactive)
			{
				CanRemove = values.Count > slotMin,
			});
		}
		if (DpiPresets.Count > 0)
			DpiPresets[Math.Clamp(selectedIndex, 0, DpiPresets.Count - 1)].IsSelected = true;

		RefreshDpiPointState();
		RebuildDpiPointCells();
	}

	// Loads persisted DPI presets, active stage, and polling rate for the current
	// device and reflects them in the UI. The live DeviceHost state (via
	// ApplyKnownDeviceState) still overrides the persisted values whenever it exists
	// so a hardware push always wins for the initial highlight.
	// forcePersisted: apply the persisted active DPI stage / report rate to the UI unconditionally,
	// even when the DeviceHost has a live value. Set on an explicit preset switch — the preset's
	// values must win there. On a plain device open it stays false, so the device's live stage (e.g.
	// after a physical DPI-button press) is honoured instead of a stale stored one.
	// assertStageToDevice: whether to let AssertActiveDpiStageAsync write the stage to the device.
	// A preset switch calls this and then immediately runs the full ApplyCurrentStateToDeviceAsync
	// apply — a stage write here would land against the OLD preset's still-cached DPI table and race
	// the correct write moments later. False on that path; true on a plain device open, where this is
	// the only write and the device must be nudged to match what the app persisted.
	private async Task LoadPersistedDeviceStateAsync(DeviceCardViewModel device, bool forcePersisted = false, bool assertStageToDevice = true)
	{
		if (_settings == null) return;

		// Live DeviceHost state is keyed by the transport the device reports on (routing identity);
		// persisted settings live under the card's stable settings identity. The two differ in the
		// VENDOR as well as the product id for a merged product whose transports do not share one
		// (RAMPAGE's receiver), so the settings half carries its own vendor local.
		var vid = device.VendorId;
		var pid = device.ProductId;
		var deviceKey = device.DeviceKey;
		var settingsVid = device.SettingsVendorId;
		var settingsPid = device.SettingsProductId;
		var settingsKey = device.SettingsDeviceKey;

		var presets = await _settings.LoadDpiPresetsAsync(settingsVid, settingsPid, settingsKey);
		if (!ReferenceEquals(Device, device)) return;
		if (presets is { Stages.Count: > 0 })
			ApplyPersistedDpiPresets(presets);
		else if (forcePersisted)
		{
			// No stored ladder means the CATALOG ladder — that is what a preset nobody edited the DPI
			// on has always represented. Rows only appear once the user edits a point or presses
			// RECOMMENDATIONS, so an untouched preset genuinely has none, and before the ladder was
			// user-defined this branch was unnecessary because the in-memory ladder always equalled
			// the catalog anyway.
			//
			// Without it, a Discard or a switch to such a preset left the ladder the user had just
			// abandoned standing in the UI, and the apply that follows pushed those values to the
			// mouse under the new preset's name — along with an active stage that could sit past the
			// end of the shorter table. forcePersisted is exactly "this preset's own value wins,
			// full stop", which is the case that has to be corrected; a plain device open has
			// already built the catalog ladder by this point.
			RebuildDpiPresets(device);
		}

		var storedStage = await _settings.LoadActiveDpiStageAsync(settingsVid, settingsPid, settingsKey);
		if (!ReferenceEquals(Device, device)) return;
		if (storedStage.HasValue &&
			(forcePersisted || _usbHidDeviceService?.TryGetLastDpiStage(vid, pid, deviceKey, out _) != true))
		{
			HighlightActiveDpiStage(storedStage.Value);
			_dpiStageKnown = true;
		}
		else if (forcePersisted)
		{
			// forcePersisted means "this preset's own value must win, full stop" — including when
			// that value is "no known stage" (e.g. a preset saved before the user ever picked one).
			// Leaving the previous preset's selection/_dpiStageKnown in place here would let
			// ApplyCurrentStateToDeviceAsync (called right after this, by the same reload) resurrect
			// an abandoned stage and push it as if it belonged to the preset just loaded — exactly
			// the bug this guards against.
			foreach (var p in DpiPresets) p.IsSelected = false;
			_dpiStageKnown = false;
		}

		// Nothing from the hardware, nothing stored, and (on a preset switch) nothing in the preset:
		// fall back to the device's catalog default rather than leaving the section on an unowned
		// stage-0 placeholder or blank. Runs on BOTH paths, and last, because the forcePersisted
		// branch above deliberately clears the selection — a preset apply is the final word on a
		// device open (EnsureDefaultPresetAppliedAsync), so a fallback applied only before it would
		// be wiped again before the user ever sees it.
		//
		// This does not re-open the B1 hole the clearing above guards. That guard exists to stop the
		// PREVIOUS preset's abandoned stage leaking into the one just loaded; a catalog default is
		// deterministic, belongs to the device being opened, and is used only when the DeviceHost has
		// reported no live stage of its own — a stage the user set with the physical button is a live
		// value and still wins.
		//
		// Not persisted HERE, on its own: a live row the selected preset's snapshot does not have is
		// what PresetMatchesLiveState reads as a difference, and Save Preset would light up on a page
		// the user never touched. BackfillSelectedPresetSettings, at the end of this method, writes
		// the resolved value to the live rows and the snapshot TOGETHER, which keeps them equal.
		var fallbackStage = !_dpiStageKnown ? ResolveDefaultDpiStageIndex() : null;
		if (fallbackStage is { } defaultStage)
		{
			HighlightActiveDpiStage(defaultStage);
			_dpiStageKnown = true;
		}

		if (assertStageToDevice)
			await AssertActiveDpiStageAsync(device, storedStage ?? fallbackStage);

		var storedHz = await _settings.LoadPollingRateAsync(settingsVid, settingsPid, settingsKey);
		if (!ReferenceEquals(Device, device)) return;
		if (storedHz.HasValue &&
			(forcePersisted || _usbHidDeviceService?.TryGetLastReportRate(vid, pid, deviceKey, out _) != true))
		{
			HighlightPollingRate(storedHz.Value);
			_reportRateKnown = true;
		}
		else if (forcePersisted)
		{
			// Mirrors the DPI-stage guard above, for the same reason.
			foreach (var o in PollingRateOptions) o.IsSelected = false;
			_reportRateKnown = false;
		}

		// Same fallback as the stage above, on both paths and for the same reasons. This is the case
		// that actually shows: ApplyKnownDeviceState deselects every rate option when the host has
		// reported none, so without this the Polling Rate section renders with nothing highlighted.
		if (!_reportRateKnown && device.SupportsDpiStageSwitch)
		{
			HighlightPollingRate(DefaultReportRateHz);
			_reportRateKnown = true;

			// The device write is gated on assertStageToDevice, and not just to avoid a redundant
			// write. On TAIPAN a rate-only MsConfig patch carries no DPI stage, and the host refuses
			// such a write at cold start when it has no stage it KNOWS
			// (TaipanMsConfig.IsFactoryDefaultFallbackSafe) — writing the vendor's example block
			// would silently move the user's real DPI. Nothing makes the stage known until either
			// the app asserts one or the mouse pushes 0x49 on a physical DPI-button press, which is
			// exactly why the rate appeared stuck until that button was pressed.
			//
			// assertStageToDevice: true is the plain-open path, where AssertActiveDpiStageAsync just
			// ran and left the host with a known stage, so a rate-only write lands. It is false on
			// the preset-apply path, where ApplyCurrentStateToDeviceAsync follows immediately and
			// sends stage + rate as ONE coordinated write — the shape TAIPAN accepts cold, and the
			// only shape BLAZE tolerates at all (see docs/mouse/blaze).
			//
			// Deduplicated through _assertedReportRates, the same cache the coordinated apply keeps.
			// The fallback is deliberately not persisted, so _reportRateKnown is false again on the
			// next open and this branch would otherwise re-run forever: on TAIPAN every re-open of
			// the page would mean another MsConfig commit, i.e. a flash erase+write, which is exactly
			// what AssertActiveDpiStageAsync avoids for the stage. Noting the value also stops the
			// DeviceHost's confirmation push for THIS write being read as a physical polling-button
			// press by OnReportRateChanged — which would call StageChangeAndApply and mark the preset
			// dirty on a page the user never touched.
			if (assertStageToDevice && _usbHidDeviceService != null && device.SupportsConfiguration
				&& !(AssertedStageIdentity(device) is { } rateIdentity
					&& _assertedReportRates.TryGetValue(rateIdentity, out var alreadySent)
					&& alreadySent == DefaultReportRateHz))
			{
				NoteAssertedReportRate(device, DefaultReportRateHz);
				await _usbHidDeviceService.SetReportRateAsync(vid, pid, deviceKey, DefaultReportRateHz);
			}
		}

		BackfillSelectedPresetSettings();
	}

	// Writes the stage / rate the section is actually showing into BOTH the live rows and the
	// selected preset's snapshot, for keys the snapshot does not have yet. Runs after resolution and
	// BEFORE the user can change anything, so it only ever records the value the preset was already
	// behaving as if it held (restoreUnsetToDefaults pushes exactly these to the hardware).
	//
	// Both sides, together, is the whole point. PresetMatchesLiveState compares ROWS, so a key
	// present on one side and absent on the other reads as a difference in either direction:
	//   - live row, no snapshot row  -> "change a setting and change it back" left Save Preset
	//     enabled, because the round trip created a live row the snapshot never had. That was the
	//     reported bug.
	//   - snapshot row, no live row  -> the mirror image, which is what writing only the snapshot
	//     produced: a freshly applied preset immediately looked dirty.
	// Writing both keeps them equal, and every later comparison is then a real value comparison.
	//
	// Never overwrites a value already stored in the preset — see
	// LocalStorageService.BackfillPresetSettings.
	// One backfill per (device, preset) per session. Opening a device runs TWO state loads that can
	// overlap — the plain one from SetDevice and the one inside EnsureDefaultPresetAppliedAsync's
	// preset apply — and each resolves the displayed stage / rate independently. Letting both write
	// meant the value stored depended on which finished last, which is how the DPI and polling rate
	// came to read differently from one visit to the next. The first one to run wins; it has the
	// same resolved values the section is showing.
	private readonly HashSet<string> _backfilledPresets = [];

	private void BackfillSelectedPresetSettings()
	{
		if (SelectedPreset is null || _localStorage is null || Device is null || _settings is null) return;
		if (!IsDeviceConfigurable) return;
		// Owner-scoped, like every other cache here: the dedup key carries it, and with no owner
		// there is nothing to back fill — every write below is owner-keyed and would no-op.
		if (AssertedStageIdentity(Device) is not { } identity) return;
		if (!_backfilledPresets.Add($"{identity}:{SelectedPreset.Id}")) return;
		// Same capability gate as PersistDisplayedDeviceState: a device that cannot switch DPI stages
		// has no stage or report rate to record.
		if (!Device.SupportsDpiStageSwitch) return;

		var values = new List<(string Key, double Value)>(2);
		if (_dpiStageKnown && DpiPresets.FirstOrDefault(p => p.IsSelected)?.StageIndex is { } stage)
			values.Add((SettingsRepository.KeyDpiActiveStage, stage));
		if (_reportRateKnown && PollingRateOptions.FirstOrDefault(o => o.IsSelected)?.Hz is { } hz)
			values.Add((SettingsRepository.KeyPollingRateHz, hz));

		// SettingsRepository routes its writes through the storage executor now, so the rows are
		// NOT in place when these calls return — the snapshot copy below is therefore enqueued on
		// the same queue (FIFO) instead of running inline, which restores the ordering this
		// method depends on.
		foreach (var (key, value) in values)
		{
			if (key == SettingsRepository.KeyDpiActiveStage)
				_ = _settings.SaveActiveDpiStageAsync(Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey, (int)value);
			else if (key == SettingsRepository.KeyPollingRateHz)
				_ = _settings.SavePollingRateAsync(Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey, (int)value);
		}

		// The per-device sliders have the same problem and are NOT in `values`: their keys
		// (mouse.buttonDebounce / mouse.liftOffDistance) only get a live row when the user first
		// moves a slider, and that single write creates rows for BOTH. Writing the displayed values
		// now means the row exists before any edit, so moving a slider and moving it back compares
		// equal instead of leaving a row the snapshot never had.
		if (_deviceSettings.Count > 0)
		{
			var debounce = (byte)Math.Clamp(Math.Round(ButtonDenounceSetting?.Value ?? 8), 0, 255);
			var liftOff = (byte)Math.Clamp(Math.Round(LiftOffSetting?.Value ?? 2), 2, 3);
			_ = _settings.SaveMouseSettingsAsync(Device.SettingsVendorId, Device.SettingsProductId,
				Device.SettingsDeviceKey, new MouseSettingsPayload(debounce, liftOff));
		}

		// Finally copy every live key the snapshot lacks. Generic rather than key-by-key: the DPI
		// ladder rows (dpi.stage.*) and anything added later live in the same table and would
		// otherwise reintroduce this bug one key at a time. Queued BEHIND the saves above so the
		// copy observes their rows.
		var owner = _ownerProvider?.GetCurrent();
		if (owner is not null)
		{
			var localStorage = _localStorage;
			var presetId = SelectedPreset.Id;
			var (ownerKind, ownerId) = (owner.Value.Kind, owner.Value.Id);
			var (vid, pid, deviceKey) = (Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey);
			_ = localStorage.RunOnStorageAsync(() => localStorage.BackfillPresetSnapshotFromLive(
				presetId, ownerKind, ownerId, vid, pid, deviceKey));
		}
	}

	// Last stage asserted onto each unit, keyed by settings identity, so re-opening a device page
	// doesn't re-write flash for a stage the device already holds.
	private readonly Dictionary<string, int> _assertedDpiStages = new(StringComparer.OrdinalIgnoreCase);

	// Owner-prefixed, for the same reason _rgbStateByDevice carries the owner: this view model is a
	// singleton that survives sign-out, and an assertion recorded for one account must not let the
	// next account's open skip a hardware write it genuinely needs. Null when there is no owner
	// (welcome screen) — the callers then treat the state as unknown and record nothing.
	private string? AssertedStageIdentity(DeviceCardViewModel device) =>
		_ownerProvider?.GetCurrent() is { } owner
			? $"{owner.Kind}:{owner.Id}:{device.SettingsVendorId}:{device.SettingsProductId}:{device.SettingsDeviceKey}"
			: null;

	// Records the stage the device is now known to hold, from either an assert or a UI selection.
	internal void NoteAssertedDpiStage(DeviceCardViewModel device, int stageIndex)
	{
		if (AssertedStageIdentity(device) is { } identity)
			_assertedDpiStages[identity] = stageIndex;
	}

	// Mirrors _assertedDpiStages for the report rate, so OnReportRateChanged can tell the
	// DeviceHost's confirmation of the app's own write apart from a genuine physical polling-button
	// press (see the echo guard there).
	private readonly Dictionary<string, int> _assertedReportRates = new(StringComparer.OrdinalIgnoreCase);

	internal void NoteAssertedReportRate(DeviceCardViewModel device, int hz)
	{
		if (AssertedStageIdentity(device) is { } identity)
			_assertedReportRates[identity] = hz;
	}

	// Drops every in-session device-state entry that belongs to some other account. Called from
	// HomeViewModel on the owner transition (sign-in, sign-out, guest start), on the UI thread —
	// the same thread every cache write above runs on, so nothing is mutated concurrently.
	//
	// This is housekeeping, not the correctness mechanism: the owner is part of every key, so a
	// missed call can leave entries behind but can never let one account read another's state.
	// Takes the owner rather than re-resolving it: the caller read it one line earlier to decide
	// there WAS a transition, and two reads of a provider that is mid-transition could disagree.
	//
	// And it is ALL the page does on a transition: an owner change never finds a device page open,
	// so there is nothing to re-drive (docs/plans/fix-account-switch-device-state, review round 1
	// S1). Every path that reaches a new owner passes through MainWindow.ClearSignedInState or
	// PerformExitDeviceDetails first, and both open with ClearDeviceForExit — so Device is already
	// null by the time the sweep runs. The board is carried by the pending-apply mark the sweep
	// writes, which the arriving account's next page open consumes.
	internal void OnOwnerChanged(SettingsOwner? owner)
	{
		PurgeRgbCacheForOtherOwners(owner);

		var prefix = owner is { } current ? $"{current.Kind}:{current.Id}:" : null;
		RemoveOtherOwners(_assertedDpiStages, prefix);
		RemoveOtherOwners(_assertedReportRates, prefix);
		_backfilledPresets.RemoveWhere(key => !BelongsToOwner(key, prefix));
	}

	private static void RemoveOtherOwners<TValue>(Dictionary<string, TValue> cache, string? ownerPrefix)
	{
		var stale = cache.Keys.Where(key => !BelongsToOwner(key, ownerPrefix)).ToList();
		foreach (var key in stale)
			cache.Remove(key);
	}

	// No owner means nothing belongs to it, so every entry is stale.
	//
	// Ordinal, so "same owner" means the same thing here as it does in the RGB cache key's tuple and
	// in the storage layer's own owner columns. The dictionaries themselves stay OrdinalIgnoreCase
	// for the device half of the key, which is pre-existing and unrelated.
	private static bool BelongsToOwner(string key, string? ownerPrefix) =>
		ownerPrefix is not null && key.StartsWith(ownerPrefix, StringComparison.Ordinal);

	// Writes the app's known active stage to the mouse when its details are opened.
	//
	// The host cannot ask the mouse which stage it is on, and MsConfig's own copy is ambiguous: our
	// write of stage S and a single button press landing on S+1 both leave the byte reading S. So
	// rather than guess, the app asserts what it has persisted — the device then matches what the
	// UI shows, and the host has a stage it KNOWS for the next polling / lift-off write. Without
	// this the host would fall back to leaving MsConfig's stale byte alone.
	//
	// Deliberate consequence: a DPI-button press made while the app was closed is overridden here.
	// Persisted settings winning on attach is how polling rate, DPI values and lift-off already
	// behave, and UI and device agreeing is the stronger guarantee.
	private async Task AssertActiveDpiStageAsync(DeviceCardViewModel device, int? storedStage)
	{
		if (_usbHidDeviceService == null || !device.SupportsDpiStageSwitch) return;
		if (device.SupportsConfiguration == false) return;

		// First run for this unit: nothing persisted, so adopt whatever the host reported (its
		// bootstrap read of the device) and persist it. From then on the app has a stage of its own.
		var stage = storedStage;
		if (!stage.HasValue)
		{
			if (!_usbHidDeviceService.TryGetLastDpiStage(device.VendorId, device.ProductId, device.DeviceKey, out var hostStage))
				return;

			stage = hostStage;
			if (_settings != null)
				await _settings.SaveActiveDpiStageAsync(device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey, hostStage);
		}

		if (AssertedStageIdentity(device) is { } identity
			&& _assertedDpiStages.TryGetValue(identity, out var asserted) && asserted == stage.Value) return;
		NoteAssertedDpiStage(device, stage.Value);

		// Cable-only on TAIPAN — the write no-ops over the dongle, where the physical button still
		// drives the stage and the UI tracks it from the device's push.
		await _usbHidDeviceService.SetActiveDpiStageAsync(
			device.VendorId, device.ProductId, device.DeviceKey, (byte)stage.Value);
	}

	// Restores persisted DPI VALUES only. Stage colours always come from the device palette — a
	// stored colour would just resurrect whatever palette was current when the user last saved.
	//
	// The persisted ladder also carries its own LENGTH, because the user can add and remove points:
	// a snapshot of five points must show five rows, not five values poured into the catalog's
	// seven. A differing length therefore rebuilds the list instead of updating it in place.
	private void ApplyPersistedDpiPresets(DpiPresetsSettings persisted)
	{
		var values = persisted.Stages.OrderBy(s => s.StageIndex).Select(s => s.DpiValue).ToList();
		if (values.Count == 0) return;

		if (values.Count != DpiPresets.Count)
		{
			var selected = DpiPresets.FirstOrDefault(p => p.IsSelected)?.StageIndex ?? 0;
			BuildDpiPresets(Device, values, selected);
			return;
		}

		for (int i = 0; i < values.Count; i++)
			DpiPresets[i].UpdateFromPersisted(values[i]);

		RefreshDpiPointState();
	}

	public NavSection ActiveSection
	{
		get => _activeSection;
		set
		{
			if (SetProperty(ref _activeSection, value))
			{
				// Leaving Actuation drops its Per-key mode (flushing any pending edit), so the
				// overlay's toggle-select branch can never leak into Key Assignments / Macros.
				if (value != NavSection.Actuation)
					ActuationPerKeyMode = false;

				// Leaving Extended Keys abandons an add in progress (its picks ride IsSelected, which
				// the clear below sweeps anyway); the blocked-key notice belongs to the page it was
				// raised on.
				if (value != NavSection.ExtendedKeys)
					CancelExtendedKeyEditor();
				HideExtendedKeyBlockedMessage();

				// Every section is entered with no key selected (PG-494): a key picked on Key
				// Assignments must not arrive on Macros still highlighted, with the header and
				// Reset already armed for it.
				ClearPhysicalKeySelection();

				OnPropertyChanged(nameof(IsPresetsSection));
				OnPropertyChanged(nameof(IsRgbLightingSection));
				OnPropertyChanged(nameof(IsButtonsSection));
				OnPropertyChanged(nameof(IsNotButtonsSection));
				OnPropertyChanged(nameof(IsMacrosSection));
				OnPropertyChanged(nameof(ShowKeyboardOverlayInMacros));
				OnPropertyChanged(nameof(ShowKeyboardOverlayInActuation));
				OnPropertyChanged(nameof(ShowKeyboardOverlayInExtendedKeys));
				OnPropertyChanged(nameof(ShowKeyboardBacklight));
				OnPropertyChanged(nameof(ShowLayeredKeyboardImage));
				OnPropertyChanged(nameof(ShowFlatKeyboardImage));
				OnPropertyChanged(nameof(IsDpiSection));
				OnPropertyChanged(nameof(IsSettingsSection));
				// Every section view binds its own Is*Section flag for visibility, so a flag missing
				// from this list can never become visible — the binding evaluates once at load, when
				// the page is on Presets, and is never re-read. That is exactly what left the Audio
				// section permanently blank.
				OnPropertyChanged(nameof(IsAudioSection));
				OnPropertyChanged(nameof(IsActuationSection));
				OnPropertyChanged(nameof(IsExtendedKeysSection));

				OnPropertyChanged(nameof(IsFirmwareSection));
				// Start/stop the endpoint volume poll with the Audio page's visibility.
				SyncVolumeTrackingTimer();
				OnPropertyChanged(nameof(IsSecondPanelVisible));
				OnPropertyChanged(nameof(IsButtonDetailsPanelVisible));
				OnPropertyChanged(nameof(AreHotspotsVisible));
				OnPropertyChanged(nameof(ButtonsBackground3Margin));
				OnPropertyChanged(nameof(MenuSectionLabel));
				OnPropertyChanged(nameof(MenuHeaderTitle));
				SyncHotspotCanvasWidth();
				// Entering/leaving the Macros section toggles the hotspot macro badges.
				RefreshHotspotMacroBadges();
				RefreshMacroListSelection();
				// Inspection is a Macros-page affordance: leaving the section drops the radio
				// and the key/hotspot highlights it produced.
				if (!IsMacrosSection)
					ClearMacroInspection();
			}
		}
	}

	// ── Section visibility ────────────────────────────────────────────────────
	public bool IsPresetsSection => ActiveSection == NavSection.Presets;
	public bool IsRgbLightingSection => ActiveSection == NavSection.RgbLighting;
	public bool IsButtonsSection => ActiveSection == NavSection.Buttons;
	public bool IsNotButtonsSection => ActiveSection != NavSection.Buttons;
	public bool IsMacrosSection => ActiveSection == NavSection.Macros;
	public bool IsDpiSection => ActiveSection == NavSection.Dpi;
	public bool IsSettingsSection => ActiveSection == NavSection.Settings;
	public bool IsAudioSection => ActiveSection == NavSection.Audio;
	public bool IsActuationSection => ActiveSection == NavSection.Actuation;
	public bool IsExtendedKeysSection => ActiveSection == NavSection.ExtendedKeys;

	public bool IsFirmwareSection => ActiveSection == NavSection.Firmware;

	// ── Device type ───────────────────────────────────────────────────────────
	public bool IsKeyboardDevice => Device?.DeviceTypeLabel.Contains("keyboard", StringComparison.OrdinalIgnoreCase) == true;
	public bool IsMouseDevice => Device?.DeviceTypeLabel.Contains("mouse", StringComparison.OrdinalIgnoreCase) == true;

	// Picks the details LAYOUT, not the sections: keyboards get the wide board frame, everything
	// else the single-image frame. Deliberately "not a keyboard" rather than "is a mouse" — the
	// layout is a shape, and a headset wants the same shape a mouse does. Gating it on IsMouseDevice
	// meant a headset rendered no nav frame and no content panel at all.
	public bool IsStackNavLayout => !IsKeyboardDevice;

	// ── Nav bar visibility ────────────────────────────────────────────────────
	// Driven by what the device can DO, not by what it is. The previous `!IsKeyboardDevice` reading
	// treated every non-keyboard as a mouse, so a headset inherited Buttons, DPI and Settings.
	public bool IsButtonsNavVisible => IsButtonRemapSupported;
	public bool IsKeyAssignmentNavVisible => IsKeyboardDevice;
	// Capability-driven, not `!IsKeyboardDevice`: main branched before that refactor and its side
	// of this conflict still reads the old way. Reverting would give the Cosmo headset the mouse
	// sections back, which is the whole reason the refactor exists.
	public bool IsDpiNavVisible => Device?.SupportsDpiStageSwitch == true;
	public bool IsSettingsNavVisible => Device?.SupportedMouseSettings.Count > 0;
	public bool IsAudioNavVisible => Device?.SupportedAudioSettings.Count > 0;
	// Catalog flag AND the live probe: the entry shows for a supported model while the first read
	// is still in flight (_actuationSupported null), and hides on a hard "no" from the device.
	public bool IsActuationNavVisible => IsKeyboardDevice && Device?.SupportsActuation == true
		&& _actuationSupported != false;
	// Same tri-state rule for Extended Keys: shown on the catalog flag while the probe is pending or
	// the RPC failed, hidden only on a hard "no" from the device.
	//
	// The rule is live, but no shipping model currently claims the capability: Cyrex and Aress carry
	// "SupportsExtendedKeys": false in the DeviceHost catalog until the feature is finished, so the
	// entry does not appear. That one flag is the whole switch — it also keeps the probe, the preset
	// apply, the self-heal write and the Key Assignments / Macros slot protection off the board, so
	// nothing of the unfinished feature reaches flash while its UI is away. Turn the feature back on
	// there, not here.
	public bool IsExtendedKeysNavVisible => IsKeyboardDevice && Device?.SupportsExtendedKeys == true
		&& _extendedKeysSupported != false;


	// Only Presets is shown for every device; each of the rest is bound to a flag in the view
	// (Firmware to IsFirmwareNavVisible), so all of them have to be counted here. Assuming RGB and
	// Macros were always present left the backdrop a row too tall on devices without them — an empty
	// strip under the last button on TAIPAN, which has no RGB.
	public int VisibleNavButtonsCount => 1
		+ (IsFirmwareNavVisible ? 1 : 0)
		+ (IsRgbSupported ? 1 : 0)
		+ (IsMacrosSupported ? 1 : 0)
		+ (IsButtonsNavVisible ? 1 : 0)
		+ (IsKeyAssignmentNavVisible ? 1 : 0)
		+ (IsDpiNavVisible ? 1 : 0)
		+ (IsSettingsNavVisible ? 1 : 0)
		+ (IsAudioNavVisible ? 1 : 0);

	// The keyboard nav backdrop (rectStack buttons: 64 high, 8 apart, 16 vertical margin) was a
	// fixed 384 for its fixed five buttons; the Actuation entry makes the count device-dependent,
	// so the height must be too — same reasoning as ActionsMenuHeight above for the mouse frame.
	public int KeyboardNavButtonsCount => 2
		+ (IsFirmwareNavVisible ? 1 : 0)
		+ (IsRgbSupported ? 1 : 0)
		+ (IsMacrosSupported ? 1 : 0)
		+ (IsActuationNavVisible ? 1 : 0)
		+ (IsExtendedKeysNavVisible ? 1 : 0);

	public int KeyboardActionsMenuHeight =>
		(KeyboardNavButtonsCount * 64) + (Math.Max(0, KeyboardNavButtonsCount - 1) * 8) + 32;

	public int ActionsMenuHeight
	{
		get
		{
			var gaps = Math.Max(0, VisibleNavButtonsCount - 1);
			return (VisibleNavButtonsCount * NavButtonHeight)
				+ (gaps * NavButtonSpacing)
				+ (NavFrameVerticalInset * 2);
		}
	}

	// ── Hotspots ──────────────────────────────────────────────────────────────
	public ObservableCollection<DevicePartHotspotViewModel> PartHotspots => Device?.PartHotspots ?? [];
	public bool HasPartHotspots => PartHotspots.Count > 0;
	public bool IsButtonDetailsPanelVisible => IsButtonsSection && (!HasPartHotspots || SelectedHotspot is not null);
	// The device-image hotspot overlay is shared by the Buttons and Macros pages.
	public bool AreHotspotsVisible => IsButtonsSection || IsMacrosSection;
	// The selected-button header's Reset is offered only for a selected, remappable button.
	public bool IsSelectedButtonResettable => IsButtonRemapSupported && SelectedHotspot is { IsAssignable: true };
	public bool IsSecondPanelVisible => IsNotButtonsSection;
	// Offsets from the start of the content column (x=164). The nav backdrop ends at 160, so 12
	// leaves the intended 16px gap between the two backgrounds; 436 clears the 420-wide button
	// detail panel by the same 16. Keyboards have no left detail panel on Key Assignments any
	// more (merged full-width layout), so they always take the 12px offset.
	public Thickness ButtonsBackground3Margin => IsButtonDetailsPanelVisible && !IsKeyboardDevice
		? new Thickness(436, 0, 0, 0)
		: new Thickness(12, 0, 0, 0);

	// The two device panels inset their section content differently at the top — 30 for the
	// mouse, 16 for the keyboard — so the Macros page's "+ NEW MACRO" button sat 14px higher
	// on a keyboard than on a mouse. Make up the difference here rather than changing the
	// keyboard panel's margin, which every other section is laid out against. Padding on the
	// ScrollViewer rather than a spacer element, so the content StackPanel's 8px Spacing
	// doesn't add to it.
	public Thickness MacrosContentPadding => IsKeyboardDevice
		? new Thickness(0, 14, 0, 0)
		: new Thickness(0);

	public DevicePartHotspotViewModel? SelectedHotspot
	{
		get => _selectedHotspot;
		set
		{
			if (SetProperty(ref _selectedHotspot, value))
			{
				OnPropertyChanged(nameof(SelectedHotspotLabel));
				NotifySelectedMacroTargetChanged();
				OnPropertyChanged(nameof(SelectedHotspotDescription));
				OnPropertyChanged(nameof(SelectedHotspotFullLabel));
				OnPropertyChanged(nameof(IsButtonDetailsPanelVisible));
				OnPropertyChanged(nameof(IsSecondPanelVisible));
				OnPropertyChanged(nameof(IsSelectedButtonResettable));
				OnPropertyChanged(nameof(CanResetSelectedButton));
				OnPropertyChanged(nameof(ButtonsBackground3Margin));
				OnPropertyChanged(nameof(MenuHeaderTitle));
			}
		}
	}

	public string? SelectedHotspotLabel => SelectedHotspot?.Label;
	public string? SelectedHotspotDescription => SelectedHotspot?.Description;
	public string? SelectedHotspotFullLabel => SelectedHotspot is null
		? null
		: string.IsNullOrEmpty(SelectedHotspot.Description)
			? SelectedHotspot.Label
			: $"{SelectedHotspot.Label} ({SelectedHotspot.Description})";

	// ── Section / header labels ───────────────────────────────────────────────
	public string MenuSectionLabel => IsButtonsSection
		? (IsKeyboardDevice
			? LocalizationManager.GetString("DeviceDetails_Section_KeyAssignment")
			: LocalizationManager.GetString("DeviceDetails_Section_ButtonSettings"))
		: ActiveSection.ToString().ToUpperInvariant();

	public string MenuHeaderTitle => IsButtonsSection
		? (IsKeyboardDevice
			? LocalizationManager.GetString("DeviceDetails_Header_KeyAssignment")
			: (SelectedHotspotLabel ?? LocalizationManager.GetString("DeviceDetails_Header_SelectMouseButton")))
		: Device?.DisplayName ?? string.Empty;

	// ── Commands ──────────────────────────────────────────────────────────────
	[RelayCommand]
	private void SelectSection(NavSection section)
	{
		if (!IsSectionAvailable(section)) return;
		ActiveSection = section;
	}

	[RelayCommand]
	private void SelectHotspot(DevicePartHotspotViewModel hotspot)
	{
		if (!HasPartHotspots || hotspot is null) return;

		foreach (var partHotspot in PartHotspots)
			partHotspot.IsSelected = ReferenceEquals(partHotspot, hotspot);

		SelectedHotspot = hotspot;
		ReflectHotspotAssignment(hotspot);
		RefreshMacroListSelection();
	}

	[RelayCommand]
	private void SelectProfile(string profileName) { }

	[RelayCommand]
	private void AddProfile() { }

	// The details page holds one card instance that HomeViewModel re-stamps in place on every
	// transport change, so a Bluetooth → USB switch reaches the open page only through this
	// handler. Bluetooth cannot carry the key-matrix read (the read-only-transport branch of
	// ReadSlotKeyedAssignmentsAsync sets the fallback banner and deliberately starts no self-heal
	// loop), so when a live cable/dongle appears the on-connect refresh must be re-run here —
	// without it the stale banner and factory-default key layout survive until the user leaves to
	// Home and reopens the page (QA 2026-08-31).
	//
	// Armed/disarmed rather than keyed on one flag's flip because HomeViewModel re-stamps the
	// flags one by one — HasBluetooth drops BEFORE HasWire rises (ApplyEntryToCard) — so at the
	// HasBluetooth event the card still looks disconnected. Arming while Bluetooth is (or
	// becomes) live and firing on whichever later event completes the "off Bluetooth, on a live
	// link" state makes the hook order-independent. It also covers the two-step recovery where
	// the Bluetooth link dies first and the cable lands seconds later. A Bluetooth drop with
	// NOTHING plugged stays a plain disconnect: armed, but no refresh until a real link appears.
	//
	// Written from two threads without synchronisation — the UI thread (Device setter) and the
	// gRPC event thread (this handler): deliberate. A torn bool is impossible, and the worst
	// interleaving costs one missed or one extra refresh, both absorbed by the slot-read
	// self-heal; a lock here would buy nothing (PR #124 m6).
	private bool _bluetoothRecoveryArmed;

	private void OnDeviceCardTransportChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName != nameof(DeviceCardViewModel.HasBluetooth)
			&& e.PropertyName != nameof(DeviceCardViewModel.HasWire)
			&& e.PropertyName != nameof(DeviceCardViewModel.HasDongle)) return;
		if (sender is not DeviceCardViewModel card || !ReferenceEquals(card, _device)) return;

		if (card.HasBluetooth)
		{
			_bluetoothRecoveryArmed = true;
			return;
		}
		if (!_bluetoothRecoveryArmed || (!card.HasWire && !card.HasDongle)) return;

		// A BT↔cable bounce could otherwise start a second refresh over one still in flight —
		// duplicate matrix reads and macro uploads, and a lost task for the test seam (PR #124
		// m5). The armed flag stays set, so the skipped switch is picked up by the next
		// transport event once the running refresh completes.
		if (TransportSwitchRefreshTask is { IsCompleted: false }) return;

		_bluetoothRecoveryArmed = false;
		TransportSwitchRefreshTask = RefreshAfterTransportRestampAsync();
	}

	private async Task RefreshAfterTransportRestampAsync()
	{
		// Best-effort ordering only: this fires mid-way through HomeViewModel's re-stamp
		// (transports land in FillConnectionCard, the routing VID/PID only in ApplyDeviceIdentity
		// right after), and the handler runs on the gRPC stream thread, where Task.Yield
		// re-queues to the THREAD POOL — it does not serialise against the re-stamp the way a
		// dispatcher yield would. In practice the few field writes of ApplyDeviceIdentity finish
		// first; if the race is ever lost, the read targets the stale Bluetooth identity, fails,
		// and the slot-read self-heal (now armed, since SupportsConfiguration is already
		// re-stamped) recovers within its ~1 s quick retry (PR #124 m1).
		await Task.Yield();
		await Quietly(RefreshFromDeviceAsync(), nameof(RefreshFromDeviceAsync));
	}

	// Test seam (InternalsVisibleTo ProoveGaming.App.Tests), like SlotReadSelfHealTask: the
	// refresh is fire-and-forget for the UI, but a test needs to await it to completion instead
	// of racing it.
	internal Task? TransportSwitchRefreshTask { get; private set; }

	// ── SetDevice ─────────────────────────────────────────────────────────────
	// Bumped by every SetDeviceAsync; a loader chain whose generation is no longer current is
	// stale (a newer open, or ClearDeviceForExit) and must stop rather than interleave its rows
	// with the newer run's.
	private int _setDeviceGeneration;

	// Completed once the open's VISIBLE-critical part (RGB + presets) has applied — the shell
	// waits on this (with a short timeout) before flipping the Details view visible, so the
	// first rendered frame carries the preset list and Current-preset selection instead of them
	// popping in behind the page's heavy first layout. Recreated per open; a stale chain
	// completes its own instance on exit so no waiter ever hangs on an abandoned run.
	private TaskCompletionSource? _openCoreLoaded;
	internal Task OpenCoreLoaded => _openCoreLoaded?.Task ?? Task.CompletedTask;

	// Everything up to the first await is the synchronous prologue: by the time control returns to
	// the caller (MainWindow shows the view right after), Device and the cheap UI state are set, so
	// the page renders immediately with the device's identity. The storage-backed loaders then run
	// with their SQLite work off the calling thread, in the same order the old synchronous body
	// used — that order is load-bearing (see the comments on each step).
	public async Task SetDeviceAsync(DeviceCardViewModel device)
	{
		// Flush the OUTGOING device's RGB state before switching. Housekeeping, not a user edit —
		// suppress the dirty/apply path, or merely opening a device page would mark its preset
		// unsaved and schedule a device write. Save/restore rather than a blind reset to false:
		// this can run on the UI thread inside an await gap of an in-flight
		// ApplyPresetToDeviceAsync, whose own suppression must survive it. The payload is captured
		// here, while Device still points at the outgoing device; the write itself is awaited
		// below, off the calling thread.
		var previousSuppressPresetDirty = _suppressPresetDirty;
		_suppressPresetDirty = true;
		Task outgoingRgbFlush;
		try
		{
			outgoingRgbFlush = PersistRgbStateForCurrentDeviceAsync();
			StageChangeAndApply();
		}
		finally { _suppressPresetDirty = previousSuppressPresetDirty; }

		var generation = ++_setDeviceGeneration;
		// The account this open belongs to, captured on the calling thread — see Stale() below.
		var openOwner = _ownerProvider?.GetCurrent();
		// RunContinuationsAsynchronously: the shell's visibility flip must queue as its own
		// dispatcher job, not run inline inside whichever chain continuation completes this.
		var coreLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		_openCoreLoaded = coreLoaded;

		Device = device;
		// Both track what the previous keyboard's firmware holds; meaningless for a different unit.
		_appliedMacroSlots = null;
		_appliedSlotOverrides = null;
		// And so does the extended-keys state — same kind of cache, same rule, but its own probe only
		// replaces it late in the follow-up chain below, long after this page is on screen wearing the
		// previous board's highlights and answering slot questions from the previous board's records.
		ResetExtendedKeysState();
		ApplyDefaultPartHotspots(device);
		SelectedHotspot = null;

		// Every device open starts fresh on Presets, regardless of which section was
		// active for the previous device — this VM is a DI singleton reused across
		// devices, so a section left selected on one device would otherwise bleed
		// into the next.
		ActiveSection = NavSection.Presets;

		SyncHotspotCanvasWidth();

		// Cleared in the prologue (the loaders clear again, idempotently) so the first visible
		// frame of the new device's page never shows the previous device's rows — for presets,
		// for the macro library, and for the button-binding maps the hotspot panel reads.
		// IsPresetsLoading keeps the "No presets yet" hint from claiming an empty library while
		// the loader is still on its way; LoadPresetsForDeviceAsync clears it.
		IsPresetsLoading = true;
		Presets.Clear();
		SelectedPreset = null;
		Macros.Clear();
		_buttonAssignments.Clear();
		_buttonMacros.Clear();

		// CanSavePreset now also depends on Device.SupportsConfiguration, and the two existing
		// NotifyCanExecuteChanged calls fire on SelectedPreset / HasUnsavedPresetChanges only — so
		// without this the button would keep the previous device's enabled state after a switch.
		SavePresetCommand.NotifyCanExecuteChanged();

		// Only the cheap reset here; the ~150 ms catalog build runs in the chain below (see
		// _keyLayoutPopulatePending) so a keyboard's click stays as light as a mouse's.
		ResetKeyLayoutState();
		_keyLayoutPopulatePending = true;

		if (!string.IsNullOrEmpty(device.RgbImagePath))
			InitDeviceOnlySvg(device.RgbImagePath);

		// An account switch landing mid-open makes the whole open stale, not just the step it
		// interrupted. Every loader below reads and WRITES under whatever owner is current when it
		// runs, so a chain that started under A must not carry on into B's rows: the RGB persist
		// right after the load would put this view model's now-stale state into B's device_rgb,
		// destroying what B had saved. Nothing is lost by stopping — the transition re-drives the
		// page itself (HomeViewModel.RestoreConnectedDevicesForNewOwner).
		bool Stale() => generation != _setDeviceGeneration
			|| !ReferenceEquals(Device, device)
			|| !Nullable.Equals(openOwner, _ownerProvider?.GetCurrent());

		// A run that stops before the visible-critical part is done must still release the
		// shell's wait — the newer open that made it stale has its own instance.
		bool StaleAndRelease()
		{
			if (!Stale()) return false;
			coreLoaded.TrySetResult();
			return true;
		}

		await outgoingRgbFlush;
		if (StaleAndRelease()) return;

		// RGB first: it is the most visible thing on the page (the keyboard art lights with it),
		// and having it resolve after the macro/binding loads left the board sitting unlit-dark
		// for the whole loading window before "switching on" (PR #100 QA). Nothing below depends
		// on it except the persist-before-presets invariant, which moves up with it.
		await LoadRgbStateForCurrentDeviceAsync(generation);
		if (StaleAndRelease()) return;

		// Write the INCOMING device's resolved RGB state down immediately. The load above settles it
		// from (in-session cache → persisted → defaults), but on a device whose RGB the user has
		// never touched that resolves to defaults that exist only in the ViewModel — device_rgb stays
		// empty while the App goes on to light the board from those very defaults
		// (EnsureDefaultPresetAppliedAsync below). ReapplyPersistedRgbAsync then finds nothing on a
		// reconnect and leaves the board on its factory lighting. The outgoing-device flush at the
		// top of this method deliberately does not cover this: it saves the device being left, not
		// the one being opened.
		//
		// Persist-only, never the stage-and-apply path: staging here would mark a preset dirty
		// and schedule a device write just for opening a page. Awaited before
		// LoadPresetsForDeviceAsync so the default preset's first snapshot captures these rows
		// rather than an empty device_rgb — PersistDisplayedDeviceState covers the same ground for
		// every later snapshot.
		await PersistRgbStateForCurrentDeviceAsync();
		if (StaleAndRelease()) return;

		// Presets next, macros/bindings after: the chain is ordered by what the user can SEE.
		// Presets is the section every open lands on, so its list and Current-preset selection
		// fill as early as the seed-snapshot invariant allows (right after the RGB persist above);
		// the macro library and button bindings are invisible until their sections are entered.
		// The seed branch's SavePresetSnapshot reads the live SQLite tables, not VM collections,
		// so it does not depend on the loaders below having run.
		await LoadPresetsForDeviceAsync(generation);

		// The page's visible-critical content is in place — the shell may flip the view now.
		coreLoaded.TrySetResult();
		if (Stale()) return;

		// Macros are per-device, so this loads THIS device's library — and it must run before
		// LoadButtonBindingsAsync, which resolves each bound button's macro_id against Macros.
		// Loading the library after it would leave every bound button without one.
		await LoadMacroLibraryAsync(generation);
		if (Stale()) return;

		await LoadButtonBindingsAsync(device, generation);
		if (Stale()) return;

		// Kicked fire-and-forget exactly as before, but from inside the chain — past the first
		// real yield — so their write-through storage reads run on the chain's continuation
		// context, never on the caller's synchronous prologue.
		_ = RebuildDeviceSettings();
		_ = RebuildAudioSettingsAsync();

		// The deferred key-layout build (see the prologue): after the flip so it never delays
		// the first frame, before the tail so the matrix probe finds the layout in place.
		if (_keyLayoutPopulatePending)
			PopulateKeyLayout();
		if (Stale()) return;

		// Follow-ups. These were fire-and-forget `_ =` kicks before this method went async, and
		// their storage completed synchronously, so in practice they ran to completion before
		// SetDevice returned. Awaiting them sequentially keeps SetDeviceAsync's task covering
		// everything a device open does (which tests rely on), while Quietly keeps a failure from
		// crashing the caller.
		//
		// Ordering: the storage-coupled trio (actuation → persisted state → prefs →
		// default-preset push) keeps its original relative order — EnsureDefaultPresetAppliedAsync
		// applies the selected snapshot over the live rows the earlier loads seed, and the
		// change-then-revert dirty comparison depends on that sequence. Only the matrix probe
		// (RefreshFromDeviceAsync — up to 128 gRPC→HID round-trips on a keyboard) moves to the
		// END: it is independent of the others, and putting the first-connect lighting push
		// behind it made a brand-new keyboard sit on factory lighting for the probe's duration.
		await Quietly(LoadActuationFromDeviceAsync(), nameof(LoadActuationFromDeviceAsync));
		// After the key layout (the rows label themselves from PhysicalKeys) and before the
		// default-preset push, which compares the preset's set against this device-confirmed one.
		await Quietly(LoadExtendedKeysFromDeviceAsync(), nameof(LoadExtendedKeysFromDeviceAsync));
		await Quietly(LoadPersistedDeviceStateAsync(device), nameof(LoadPersistedDeviceStateAsync));
		await Quietly(EnsureAppPreferencesLoadedAsync(), nameof(EnsureAppPreferencesLoadedAsync));
		// Sibling of EnsureDefaultPresetAppliedAsync — the other one-shot "give this unit the preset
		// it is supposed to be wearing" push — and deliberately just before it: materializing
		// records the preset as applied, which is exactly what makes the default-preset push below
		// a no-op. Its position in the chain is load-bearing and NOT free to move earlier: the apply
		// it runs needs the extended-keys probe (two steps up) and the macro library / key layout
		// (further up still) already in place, or it silently skips the pulled extended keys and
		// deletes the pulled macro bindings — see MaterializePulledPresetAsync's remarks.
		await Quietly(MaterializePulledPresetAsync(device, generation), nameof(MaterializePulledPresetAsync));
		await Quietly(EnsureDefaultPresetAppliedAsync(), nameof(EnsureDefaultPresetAppliedAsync));
		// The board must not end this open wearing somebody else's extended keys. The two pushes
		// above are one-shot — they fire for a pulled preset and for an account with no applied row —
		// and between them they miss the commonest case of all: the app restarted into a different
		// account, where nothing marks anything because the sweep's "who was last applied" field is
		// in-memory and a fresh process reads the restored session as the first owner it ever had.
		// Comparing the board against the signed-in owner's own rows answers that without anyone
		// having to enumerate the ways it can happen — a restart, a power cycle, another machine, or
		// the vendor app writing the board (docs/plans/fix-extended-keys-self-heal).
		//
		// Position is load-bearing: AFTER the probe two steps up, which is what supplies the board
		// side of the comparison, and AFTER both one-shot pushes, so that when either of them already
		// landed this owner's set the comparison finds them equal and writes nothing. The call itself
		// is a no-op whenever the board already matches.
		//
		// Skipped when a push earlier in THIS open already failed: the board has just refused the
		// very set this would send, so a second attempt would only fail again and re-probe twice.
		// The flag is not sticky across opens — the probe at the top of every open clears it — so a
		// board that refuses stays at one attempt per open rather than none ever again.
		//
		// And fenced by Stale() like every device-writing step above it: this one reads Device and
		// the signed-in owner for itself, so without the fence a chain whose page has already been
		// replaced would compare the NEW device's stored rows against the OLD device's probe result
		// (_extendedKeysOnDevice still holds it until the new open's own probe lands) and either
		// send the new board a flash commit it never needed or skip a heal it did.
		//
		// And when it DID write, the key push runs again for the same reason the preset push re-runs
		// it (ApplyCurrentStateToDeviceCoreAsync): a record the heal removed handed its slots back to
		// their factory triplets, so the owner's remap on those slots is in storage and no longer on
		// the board. Macro bindings are caught up by RefreshFromDeviceAsync at the end of the chain;
		// slot remaps are not, so without this the key types its factory code until the next open
		// (review round 4, m4).
		if (!Stale() && !ExtendedKeysWriteFailed
			&& await Quietly(ApplyStoredExtendedKeysToDeviceAsync(), nameof(ApplyStoredExtendedKeysToDeviceAsync)) == true
			&& !Stale())
			await Quietly(ApplyKeyOverridesToDeviceAsync(), nameof(ApplyKeyOverridesToDeviceAsync));
		// After the loads above settled the live rows: Save Preset reflects real divergence from
		// the open (see RecomputeUnsavedPresetChangesAsync), not just edits made this session.
		await Quietly(RecomputeUnsavedPresetChangesAsync(), nameof(RecomputeUnsavedPresetChangesAsync));
		await Quietly(RefreshFromDeviceAsync(), nameof(RefreshFromDeviceAsync));
	}

	// Near-parity with the fire-and-forget discard the SetDevice follow-ups had before
	// SetDeviceAsync: a failure never propagates to the caller, but unlike the old unobserved
	// `_ =` task it leaves a trace naming the operation — "the device somehow didn't get
	// configured" is debuggable only if something says which step fell over.
	private static async Task Quietly(Task task, string operation)
	{
		try { await task; }
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"SetDeviceAsync follow-up '{operation}' failed: {ex}");
		}
	}

	// The value-returning twin, for a step whose answer decides what runs next. A failure yields
	// default(T) — "nothing happened" — which is the safe reading for every such decision: the
	// follow-ups it gates are the ones that only exist because the step DID something.
	private static async Task<T?> Quietly<T>(Task<T> task, string operation)
	{
		try { return await task; }
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"SetDeviceAsync follow-up '{operation}' failed: {ex}");
			return default;
		}
	}

	// Called from MainWindow's PerformExitDeviceDetails, before the view is hidden. Clearing the
	// glow bitmap alone (ClearDeviceOnlyBitmap) wasn't enough to stop the next device's image from
	// briefly showing the previous one: Device stayed pointing at the OLD device the whole time the
	// user was back on Home, so DeviceDetailImagePath went straight from A's path to B's path on the
	// next SetDevice with no cleared state in between — and the bound Svg/Image controls, never
	// destroyed (only IsVisible-toggled), kept whatever picture they last rendered until THEIR OWN
	// load of B's path completed, which is what actually painted as "A, then a flash, then B" even
	// though the VM's own bitmap cache was already correct. Routing every device switch through a
	// null Device first forces those controls to genuinely clear before the next device loads, the
	// same way a fresh app launch (Device starts null) never shows a stale image.
	void IDeviceDetailsExit.ClearDeviceForExit() => ClearDeviceForExit();

	internal void ClearDeviceForExit()
	{
		// Leaving the page: stop polling the audio endpoints for a device nobody is looking at.
		StopVolumeTracking();
		// Same rule for the key-matrix self-heal loop — nulling Device below stops it on its next
		// tick anyway, but cancelling spares that last wait-and-check on a closed page.
		CancelSlotReadSelfHeal();
		// And the RGB glow timers (audit C2): the view is only IsVisible-toggled, never destroyed,
		// so without this the 33 ms DispatcherTimers kept animating a glow nobody can see for the
		// rest of the app's life — even in the tray. Re-entry restarts them with no extra wiring:
		// SetDeviceAsync → ApplyResolvedRgbState → StartEffectAnimation.
		StopSolidHueCycleTimer();
		StopAnimationTimer();
		ClearDeviceOnlyBitmap();
		// Nothing is on display any more, so nothing the page still holds belongs to an account — a
		// later persist must not be able to treat these leftover properties as some owner's state.
		_displayedRgbOwner = null;
		Device = null;
	}

	// On first connect (no preset ever pushed to this device), apply the selected default preset to
	// the hardware once and record it — so a fresh device reflects its default configuration. Devices
	// that already had a preset applied are left untouched (no unsolicited write on open).
	private async Task EnsureDefaultPresetAppliedAsync()
	{
		if (Device is null || _localStorage is null) return;
		var owner = _ownerProvider?.GetCurrent();
		if (owner is null) return;

		var applied = _localStorage.GetAppliedPreset(
			owner.Value.Kind, owner.Value.Id,
			Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey);
		if (applied is not null) return;

		var target = Presets.FirstOrDefault(p => p.IsDefault) ?? Presets.FirstOrDefault();
		if (target is null) return;

		// First connect (no preset ever applied to this device): load the DEFAULT preset's OWN
		// saved configuration onto the hardware, giving the device a known baseline — never read or
		// capture whatever the live rows happen to currently hold. ApplyPresetToDeviceAsync already
		// does exactly this (LoadPresetSnapshotToDevice reads target's snapshot into the live rows,
		// reloads the UI sections from them, and pushes) and records SetAppliedPreset itself.
		// Deliberately NOT preceded by SavePresetSnapshot(target.Id, ...): capturing live state INTO
		// the target's snapshot first is what silently overwrote it on every existing install's first
		// post-upgrade open (device_applied_preset starts empty for everyone, not just fresh
		// hardware) — the destructive step was that capture, not the load-and-push that follows it.
		// restoreUnsetToDefaults: false — this is a first-connect baseline push, not a user-driven
		// revert. If the default preset holds no DPI stage / report rate, they must stay unsent
		// rather than resetting a DPI the user set outside the app (the B1 regression).
		await ApplyPresetToDeviceAsync(target, restoreUnsetToDefaults: false);
	}

	// Decides the catalog default DPI stage / report rate a device being used for the very first
	// time should be seeded with, so "add the device" lands on factory-sane values rather than on
	// whatever the ladder's first rung happens to be. Consumed by LoadPresetsForDeviceAsync, gated
	// on the default preset having just been Seeded — i.e. this device type has no presets at all.
	//
	// Unlike a UI selection, a catalog default is a property of the device being opened, so seeding
	// it carries none of the B1 risk that keeps DpiPresets.IsSelected out of this path. The
	// live-value checks are still made: a stage or rate the DeviceHost has already read off the
	// hardware is a real fact and must not be overwritten by a default.
	//
	// Capture-only: everything here reads VM / live-device state and therefore runs on the calling
	// thread; the actual rows are written inside LoadPresetsForDeviceAsync's background block,
	// immediately before the fresh preset's snapshot so they are captured into it. Seeding any
	// later would be pointless: EnsureDefaultPresetAppliedAsync's LoadPresetSnapshotToDevice wipes
	// device_settings before copying the snapshot back.
	private (int? StageToSeed, int? RateToSeed) CaptureCatalogDefaultSeeds()
	{
		if (Device is null || _settings is null || !Device.SupportsDpiStageSwitch) return (null, null);
		if (!IsDeviceConfigurable) return (null, null);

		int? stageToSeed = null;
		if (_usbHidDeviceService?.TryGetLastDpiStage(Device.VendorId, Device.ProductId, Device.DeviceKey, out _) != true
			&& ResolveDefaultDpiStageIndex() is { } stage)
		{
			stageToSeed = stage;
		}

		int? rateToSeed = null;
		if (_usbHidDeviceService?.TryGetLastReportRate(Device.VendorId, Device.ProductId, Device.DeviceKey, out _) != true)
		{
			rateToSeed = DefaultReportRateHz;
		}

		return (stageToSeed, rateToSeed);
	}

	// Mirrors the nav bar exactly: a section that is not shown must not be reachable by any other
	// route either. Presets is on every device; Firmware follows IsFirmwareNavVisible; everything
	// else is a capability. The
	// keyboard case is the one asymmetry — a keyboard's Key Assignments page IS NavSection.Buttons,
	// so Buttons stays reachable for keyboards even though IsButtonsNavVisible (button remap) is off.
	private bool IsSectionAvailable(NavSection section) => section switch
	{
		NavSection.Presets => true,
		NavSection.Firmware => IsFirmwareNavVisible,
		NavSection.RgbLighting => IsRgbSupported,
		NavSection.Macros => IsMacrosSupported,
		// A keyboard's Key Assignments page IS NavSection.Buttons, so Buttons stays reachable for
		// keyboards even though IsButtonsNavVisible (mouse button remap) is false for them.
		NavSection.Buttons => IsButtonsNavVisible || IsKeyAssignmentNavVisible,
		NavSection.Dpi => IsDpiNavVisible,
		NavSection.Settings => IsSettingsNavVisible,
		NavSection.Audio => IsAudioNavVisible,
		NavSection.Actuation => IsActuationNavVisible,
		NavSection.ExtendedKeys => IsExtendedKeysNavVisible,
		_ => false,
	};

	// Builds the on-image button-remap hotspots from the device's catalog config (Hotspots). The
	// geometry is per device and now lives in appsettings, so adding a mouse is a config edit, not
	// a code change here. Label text is derived from the id (same convention as the resource keys)
	// and whether a hotspot is bindable comes from HotspotIdToButton — only the geometry differs
	// per device, so only that is carried in config.
	private static void ApplyDefaultPartHotspots(DeviceCardViewModel device)
	{
		device.PartHotspots.Clear();

		if (!device.DeviceTypeLabel.Contains("mouse", StringComparison.OrdinalIgnoreCase))
			return;

		foreach (var h in device.Hotspots)
		{
			var labelKey = HotspotLabelKey(h.Id);
			var descKey = HotspotDescriptionKey(h.Id);
			device.PartHotspots.Add(new DevicePartHotspotViewModel
			{
				Id = h.Id,
				// Disabled hotspots (e.g. left-click, fixed in firmware) label the part but their
				// button can't be bound. The flag is per device in config; the device write path
				// independently skips unbindable ids via HotspotIdToButton.
				IsAssignable = !h.Disabled,
				LabelKey = labelKey,
				DescriptionKey = descKey,
				Label = LocalizationManager.GetString(labelKey),
				Description = LocalizationManager.GetString(descKey),
				ButtonPoint = new Point(h.ButtonX, h.ButtonY),
				TargetPoint = new Point(h.TargetX, h.TargetY),
				LabelSide = string.Equals(h.LabelSide, "Right", StringComparison.OrdinalIgnoreCase)
					? HotspotLabelSide.Right
					: HotspotLabelSide.Left,
			});
		}
	}

	// Hotspot label / description resource keys, derived from the kebab-case id (e.g. "side-forward"
	// → "DeviceDetails_Hotspot_SideForward_Label"). Keeps config to geometry only.
	private static string HotspotLabelKey(string id) => $"DeviceDetails_Hotspot_{PascalizeHotspotId(id)}_Label";
	private static string HotspotDescriptionKey(string id) => $"DeviceDetails_Hotspot_{PascalizeHotspotId(id)}_Desc";

	private static string PascalizeHotspotId(string id) =>
		string.Concat(id.Split('-', StringSplitOptions.RemoveEmptyEntries)
			.Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

	/// <summary>The width the hotspot labels were laid out against.</summary>
	/// <remarks>One definition, shared with the hotspot view model that squeezes against it — two
	/// copies would let the cap and the centre of the squeeze drift apart silently.</remarks>
	public const double HotspotCanvasDesignWidth = DevicePartHotspotViewModel.DesignCanvasWidth;

	private double _hotspotCanvasWidth = HotspotCanvasDesignWidth;

	/// <summary>
	/// The coordinate space the hotspot canvas actually gets, never wider than the design.
	/// </summary>
	public double HotspotCanvasWidth
	{
		get => _hotspotCanvasWidth;
		private set => SetProperty(ref _hotspotCanvasWidth, value);
	}

	/// <summary>
	/// Reports how much room the hotspots have. Called from the hosting panel's SizeChanged with a
	/// real width, and with nothing from the places that only need the hotspots re-laid out.
	/// </summary>
	public void SyncHotspotCanvasWidth(double? actualWidth = null)
	{
		var width = ResolveHotspotCanvasWidth(actualWidth ?? _hotspotCanvasWidth, _hotspotCanvasWidth);
		HotspotCanvasWidth = width;

		foreach (var hotspot in PartHotspots)
			hotspot.SetCanvasWidth(width);
	}

	/// <summary>
	/// How wide the hotspot canvas may be, given the room its panel has.
	/// </summary>
	/// <remarks>
	/// This used to be pinned to the design width, which is why the canvas overhung its panel: the
	/// panel is the layout's `*` column, ~600 near the design window width, so a 1236 canvas centred
	/// in it put 318px off each edge and the outermost labels were clipped. A size of zero or less
	/// is a panel that is not laid out (a hidden section still raises SizeChanged) and must not be
	/// mistaken for "no room" — it keeps whatever the last real measurement was.
	/// </remarks>
	internal static double ResolveHotspotCanvasWidth(double available, double current) =>
		double.IsFinite(available) && available > 0
			? Math.Min(HotspotCanvasDesignWidth, available)
			: current;

	private void OnLanguageChanged()
	{
		foreach (var effect in RgbEffects)
			effect.RefreshDisplayName();

		foreach (var group in ButtonActionGroups)
		{
			if (group.TitleKey is not null)
				group.Title = LocalizationManager.GetString(group.TitleKey);

			foreach (var option in group.Options)
			{
				if (option.LabelKey is not null)
					option.Label = LocalizationManager.GetString(option.LabelKey);
			}
		}

		foreach (var hotspot in PartHotspots)
		{
			if (hotspot.LabelKey is not null)
				hotspot.Label = LocalizationManager.GetString(hotspot.LabelKey);
			if (hotspot.DescriptionKey is not null)
				hotspot.Description = LocalizationManager.GetString(hotspot.DescriptionKey);
		}

		OnPropertyChanged(nameof(MenuSectionLabel));
		OnPropertyChanged(nameof(MenuHeaderTitle));
		// Formats a resource, so it does not update itself: a {DynamicResource} binding re-reads on
		// a language switch but a computed property has to be told.
		OnPropertyChanged(nameof(FirmwareVersionText));
		// Resolved through LocalizationManager (the ordinal point names and the step caption), so
		// they do not re-read on a language switch by themselves.
		OnPropertyChanged(nameof(DpiPointTitle));
		OnPropertyChanged(nameof(DpiStepLabel));
		OnPropertyChanged(nameof(SelectedHotspotLabel));
		OnPropertyChanged(nameof(SelectedHotspotDescription));
		OnPropertyChanged(nameof(SelectedHotspotFullLabel));

		// Macro dialog strings resolved via LocalizationManager (not DynamicResource).
		OnPropertyChanged(nameof(MacroDialogTitle));
		OnPropertyChanged(nameof(MacroDialogActionLabel));
		OnPropertyChanged(nameof(MacroDeleteMessage));
		OnPropertyChanged(nameof(RecordButtonLabel));
		OnPropertyChanged(nameof(RecordedStepsCountLabel));

		// Presets header format string is also LocalizationManager-driven.
		OnPropertyChanged(nameof(PresetsCountLabel));
		OnPropertyChanged(nameof(PresetDialogTitle));
		OnPropertyChanged(nameof(PresetDialogSubtitle));
		OnPropertyChanged(nameof(PresetDialogActionLabel));
	}
}
