using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProoveGaming.App.Classes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ProoveGaming.App.ViewModels;

public abstract partial class DeviceCardViewModel : ViewModelBase
{
	public event Action? DeleteRequested;
	public event Action<DeviceCardViewModel>? OpenSettingsRequested;
	// ── Routing identity ──────────────────────────────────────────────────────
	// Which unit device COMMANDS go to: the live transport (cable / dongle) the card is currently
	// reachable through. These change when a merged-transport product switches transport.
	public int ProductId { get; set; }
	public int VendorId { get; set; }
	// Stable per-physical-device identity (Container ID) — distinguishes two identical units.
	// Empty for legacy/single-device or when the host couldn't resolve it.
	public string DeviceKey { get; set; } = string.Empty;

	// ── Settings identity ─────────────────────────────────────────────────────
	// Which bucket SETTINGS are persisted under. For a merged-transport product (TAIPAN, RAMPAGE)
	// all three are pinned per product so switching cable↔dongle↔Bluetooth — which changes the
	// routing identity above — keeps the saved RGB / buttons / settings. For every other device
	// they mirror the routing identity.
	//
	// SettingsVendorId is NOT the same as VendorId even for a merged product: three shipped
	// products enumerate a transport under a vendor of its own (RAMPAGE's 2.4G receiver is 1389
	// against the board's 10473; COSMO's and STARLIGHT's Bluetooth links are 1494 against 3468),
	// so keying settings by the live routing vendor gave one physical device two buckets. VendorId
	// stays the ROUTING vendor — device commands must go out under the live transport's id.
	public int SettingsVendorId { get; set; }
	public int SettingsProductId { get; set; }
	public string SettingsDeviceKey { get; set; } = string.Empty;

	// True when this product is one physical device reachable over several transports, so the card
	// must never be split by device key and stays connected while ANY transport is present.
	public bool MergeTransports { get; set; }
	public int StorageId { get; set; }
	public required string Name { get; set; }

	// What the user reads: the catalog's DisplayName when it has one (the "PG TAIPAN" mouse is shown
	// as TAIPAN MAX), otherwise Name. Name stays what everything else keys on: matching the live
	// device, the stored card row and the protocol choice.
	[ObservableProperty]
	public partial string? CatalogDisplayName { get; set; }

	public string DisplayName => string.IsNullOrEmpty(CatalogDisplayName) ? Name : CatalogDisplayName;

	partial void OnCatalogDisplayNameChanged(string? value) => OnPropertyChanged(nameof(DisplayName));
	public required string DeviceTypeLabel { get; set; }
	public bool SupportsRgb { get; set; }
	public bool SupportsButtonRemap { get; set; }
	public bool SupportsMacros { get; set; }
	// Magnetic-axis actuation (key press force) — true for T800 / Cyrex / Aress.
	public bool SupportsActuation { get; set; }
	// Extended Keys (Rapid Snap / SOCD / Mod-Tap / Toggle) — true for Cyrex / Aress.
	public bool SupportsExtendedKeys { get; set; }
	public ObservableCollection<DevicePartHotspotViewModel> PartHotspots { get; } = [];

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ImageUri))]
	[NotifyPropertyChangedFor(nameof(IsRasterImage))]
	public partial string? ImagePath { get; set; }

	[ObservableProperty]
	public partial string? RgbImagePath { get; set; }

	public List<string> SupportedMouseSettings { get; set; } = [];

	// Audio levels this device exposes ("OutputVolume", "MicrophoneVolume"); empty on anything that
	// is not a headset, which is what keeps the Audio section hidden everywhere else.
	//
	// Notifies rather than being a plain auto-property: the home card binds HasAudioIndicators
	// directly on this object, and the catalog fills this list AFTER the card is constructed. As a
	// silent setter the card evaluated the flag once (empty, false) and never again, so its audio
	// icons never appeared — while the details view worked, because swapping Device re-evaluates
	// every Device.* binding and hid the bug.
	private List<string> _supportedAudioSettings = [];
	public List<string> SupportedAudioSettings
	{
		get => _supportedAudioSettings;
		set
		{
			if (SetProperty(ref _supportedAudioSettings, value))
				OnPropertyChanged(nameof(HasAudioIndicators));
		}
	}

	// ── Audio silence indicators ─────────────────────────────────────────────
	// These live on the CARD, not on the details view-model, because the home card and the details
	// connectivity bar must show the same thing — and the card binds this object. Putting them on
	// the details VM made them unreachable from the card by construction.
	//
	// "Silent", not "muted": Windows tracks mute and level independently, so an endpoint at volume 0
	// is not muted and GetMute still reports false. The icon means "nothing will be heard", so it is
	// set by EITHER condition — see AudioMuteMonitor.IsSilent.
	//
	// Read from the Windows endpoint (Core Audio), so this reflects OS-level state. The headset's
	// own physical mic switch never reaches the host, so it does not move these.
	[ObservableProperty]
	public partial bool IsOutputSilent { get; set; }

	[ObservableProperty]
	public partial bool IsMicrophoneSilent { get; set; }

	// Gates the separator and both icons. Requires a live connection as well as audio support: a
	// disconnected device must show NO status icons at all, and "not muted" is otherwise true for a
	// headset that is switched off, which left the icons showing for a device that was not there.
	public bool HasAudioIndicators => SupportedAudioSettings.Count > 0 && Connected;

	// Where those levels are applied. Cosmo drives its own Windows endpoints; a headset holding its
	// level in firmware would say Device instead. Per-device because the two are not interchangeable.
	public AudioVolumeTarget AudioVolumeTarget { get; set; } = AudioVolumeTarget.OperatingSystem;

	// False when this device has no readable battery even though its transport implies one. Without
	// it a dongle device with no battery protocol shows an empty red "critical" indicator, which
	// reads as a flat battery rather than as "unknown". Defaults true so existing devices are
	// unaffected.
	public bool SupportsBattery { get; set; } = true;

	// Which Windows device container the AUDIO endpoints belong to, when that is not the routing
	// identity. Empty falls back to DeviceKey, so every device with a single transport — all of them
	// except a headset reachable two ways — is unaffected.
	//
	// It exists because a headset's endpoints live under a DIFFERENT container per transport: PG
	// Cosmo's are ccd4f06e-… over its USB dongle and 9ca05de4-… over Bluetooth (measured 2026-08-17).
	// IAudioEndpointService addresses endpoints by container and matches nothing when handed the
	// wrong one, so the volume sliders would silently drive an endpoint that is NOTPRESENT.
	private string _audioContainerId = string.Empty;
	public string AudioContainerId
	{
		get => string.IsNullOrEmpty(_audioContainerId) ? DeviceKey : _audioContainerId;
		set => _audioContainerId = value ?? string.Empty;
	}

	// Per-device DPI stage values from the catalog config (empty → the VM uses its default set).
	public IReadOnlyList<int> DpiStages { get; set; } = [];

	// Bounds for the user-editable DPI ladder, from the catalog config. Each setter takes the raw
	// catalog number and each getter resolves 0 (unset, or an older DeviceHost that does not send
	// these) to the built-in fallback — so the section always has a usable range and a non-zero
	// step to divide by. Both shipped mice declare 200/12000/50 and a 1-8 slot table; see
	// docs/plans/add-custom-dpi-values/.
	public const int FallbackDpiMin = 200;
	public const int FallbackDpiMax = 12000;
	public const int FallbackDpiStep = 50;
	public const int FallbackDpiSlotMin = 1;
	public const int FallbackDpiSlotMax = 8;

	private int _dpiMin;
	private int _dpiMax;
	private int _dpiStep;
	private int _dpiSlotMin;
	private int _dpiSlotMax;

	public int DpiMin
	{
		get => _dpiMin > 0 ? _dpiMin : FallbackDpiMin;
		set => _dpiMin = value;
	}

	public int DpiMax
	{
		get => _dpiMax > 0 ? _dpiMax : FallbackDpiMax;
		set => _dpiMax = value;
	}

	public int DpiStep
	{
		get => _dpiStep > 0 ? _dpiStep : FallbackDpiStep;
		set => _dpiStep = value;
	}

	public int DpiSlotMin
	{
		get => _dpiSlotMin > 0 ? _dpiSlotMin : FallbackDpiSlotMin;
		set => _dpiSlotMin = value;
	}

	public int DpiSlotMax
	{
		get => _dpiSlotMax > 0 ? _dpiSlotMax : FallbackDpiSlotMax;
		set => _dpiSlotMax = value;
	}

	// Render scale for the device image, from the catalog config. Compensates for artwork whose
	// canvas padding differs between devices; 1.0 unless the device's config sets it.
	//
	// Observable because the CARD binds to it too, not just the details view which reads it once
	// when a device is opened. A card is filled again on every transport change and reconnect, and
	// a plain auto-property would leave the already-bound ScaleTransform on whatever value the card
	// happened to hold when its view was first created.
	[ObservableProperty] private double _imageScale = 1.0;

	// Extra darkening scrim (0..1) for the Key Assignments / Macros overlay artwork, from the
	// catalog config. Non-zero on boards whose unlit keycap art is bright enough to swallow the
	// selection box (Rampage); 0 for the rest.
	public double KeyOverlayArtDim { get; set; }

	// Button-remap hotspot geometry from the catalog config, tuned per device. Empty → the details
	// view shows no hotspots. The details VM turns these into the on-image markers + connector lines.
	public IReadOnlyList<DeviceHotspotInfo> Hotspots { get; set; } = [];

	// Factory-sane starting values from the catalog config — applied when the device is first added
	// and restored by the DPI "Recommendations" / Settings "Restore to default" buttons. Every field
	// is optional; null means this device has no default for that aspect and the VM keeps its own
	// built-in constant. See docs/device-defaults.md.
	public DeviceDefaults Defaults { get; set; } = new();

	// False when configuration writes do not reach this device over the transport it is currently
	// connected by — TAIPAN on its 2.4G receiver or Bluetooth. The details view keeps Save Preset
	// disabled in that state rather than offering to save settings the device would never receive.
	// Defaults to true so any device whose catalog says nothing behaves exactly as before.
	//
	// Notifies rather than being a plain auto-property because IsBluetoothConfigLocked derives from
	// it and the details page binds that lock live: a transport switch re-stamps this flag on the
	// already-bound card, and a silent setter would leave the banner and section locks stale. Both
	// stamping paths (ApplyEntryToCard, FillBluetoothCard) set this BEFORE the transport flags, so
	// the lock computes from consistent state at each notification.
	private bool _supportsConfiguration = true;
	public bool SupportsConfiguration
	{
		get => _supportsConfiguration;
		set
		{
			if (SetProperty(ref _supportsConfiguration, value))
				OnPropertyChanged(nameof(IsBluetoothConfigLocked));
		}
	}

	// True while the live transport is Bluetooth AND that transport cannot carry configuration
	// (catalog SupportsConfiguration=false) — drives the BLUETOOTH MODE banner and the details
	// page's section locking. Headsets (Cosmo, Starlight) are configurable over Bluetooth (their
	// audio targets the OS endpoint), so they never lock; keyboards/mice keep their catalog false
	// and lock exactly as before (ticket 2026-09-02, superseding the uniform lock of 2026-08-30 —
	// see docs/plans/remove-headset-bluetooth-lock/remove-headset-bluetooth-lock-plan.md).
	public bool IsBluetoothConfigLocked => HasBluetooth && !SupportsConfiguration;

	// Per-device macro recorder limits from the catalog config (0 → the VM uses its default).
	// MacroMaxDelayMs bounds the longest recorded inter-event pause; MacroMaxRepetitions bounds
	// the macro repeat count the UI accepts.
	public int MacroMaxDelayMs { get; set; }
	public int MacroMaxRepetitions { get; set; }

	// True when the device's protocol can set the ACTIVE DPI stage, so the UI offers stage switching
	// as well as mirroring the physical DPI button. Every mouse family here implements it: BLAZE via
	// the OEM's dedicated 0x0B SetCurrentDPI (which touches only the stage, never LED state), TAIPAN
	// via the DpiSwitch byte of its MsConfig block. Keyboards never show the DPI section at all.
	//
	// On TAIPAN the write needs the cable (MsConfig can't be read back over the dongle); the
	// physical DPI button keeps working on every transport and the UI mirrors it either way.
	public bool SupportsDpiStageSwitch =>
		DeviceTypeLabel?.Contains("mouse", StringComparison.OrdinalIgnoreCase) == true;

	public string? ImageUri => string.IsNullOrEmpty(ImagePath) ? null : $"avares://ProoveGaming.App{ImagePath}";

	// Device artwork is SVG almost everywhere, but Cosmo ships a PNG. The two need different
	// controls: SvgImageView parses vector markup and renders nothing for a raster file, and
	// Avalonia's Bitmap decodes raster only and returns nothing for an SVG. Picking the control by
	// file extension is what keeps a device's card from silently rendering blank.
	public bool IsRasterImage =>
		ImagePath?.EndsWith(".png", StringComparison.OrdinalIgnoreCase) == true
		|| ImagePath?.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) == true
		|| ImagePath?.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) == true;

	/// <summary>
	/// Split chassis/keycap artwork for this device, or null if it only has the flat
	/// single-layer image. Lets the card render the same layered keyboard as device details
	/// — chassis, black bed between the keys, keycaps — instead of the flat SVG.
	/// </summary>
	public Models.RgbKeyboardArt? KeyboardArt => Models.RgbKeyboardArtCatalog.For(Name);

	public bool HasKeyboardArt => KeyboardArt is not null;

	/// <summary>
	/// True for a board whose card renders the layered build unlit (chassis, black bed, keycaps,
	/// unlit legends) — the same look as Key Assignments — instead of a lit product shot:
	/// T-Racer, Cyrex, Aress and T800.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Two reasons converge here. <b>T-Racer</b> is necessity: its vendor delivery was one RGB-off
	/// file, so its <see cref="Models.RgbKeyboardArt.CardSvg"/> drawn raw renders the artwork's own
	/// <c>#keys-solid</c> glow and reads as lit while the details pages show the same board dark.
	/// <b>Cyrex</b>, <b>Aress</b> and <b>T800</b> do ship lit <c>Card/PG_*_card.svg</c> product
	/// shots, but the product decision is to show them unlit on the home card, consistent with Key
	/// Assignments. The remaining boards keep their delivered product shot.
	/// </para>
	/// </remarks>
	public bool UseUnlitCardArt =>
		HasKeyboardArt && (
			Models.Keyboards.TRacerKeyCatalog.IsTRacer(Name) ||
			Models.Keyboards.CyrexKeyCatalog.IsCyrex(Name) ||
			Models.Keyboards.AressKeyCatalog.IsAress(Name) ||
			string.Equals(Name, "T800", StringComparison.OrdinalIgnoreCase));

	/// <summary>Card artwork drawn as the delivered product shot — every board except <see cref="UseUnlitCardArt"/>.</summary>
	public bool UseProductShotCardArt => HasKeyboardArt && !UseUnlitCardArt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Connected))]
    [NotifyPropertyChangedFor(nameof(CardArtOpacity))]
    [NotifyPropertyChangedFor(nameof(CardArtDimOpacity))]
    [NotifyPropertyChangedFor(nameof(HasDongleSeparator))]
    [NotifyPropertyChangedFor(nameof(HasBluetoothSeparator))]
    [NotifyPropertyChangedFor(nameof(HasAudioIndicators))]
    [NotifyPropertyChangedFor(nameof(ConnectionTooltip))]
    public partial bool HasWire { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Connected))]
    [NotifyPropertyChangedFor(nameof(CardArtOpacity))]
    [NotifyPropertyChangedFor(nameof(CardArtDimOpacity))]
    [NotifyPropertyChangedFor(nameof(HasDongleSeparator))]
    [NotifyPropertyChangedFor(nameof(HasBluetoothSeparator))]
    [NotifyPropertyChangedFor(nameof(HasAudioIndicators))]
    [NotifyPropertyChangedFor(nameof(ConnectionTooltip))]
    public partial bool HasDongle { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Connected))]
    [NotifyPropertyChangedFor(nameof(CardArtOpacity))]
    [NotifyPropertyChangedFor(nameof(CardArtDimOpacity))]
    [NotifyPropertyChangedFor(nameof(HasBluetoothSeparator))]
    [NotifyPropertyChangedFor(nameof(HasAudioIndicators))]
    [NotifyPropertyChangedFor(nameof(ConnectionTooltip))]
    [NotifyPropertyChangedFor(nameof(IsBluetoothConfigLocked))]
    public partial bool HasBluetooth { get; set; }

    /// <summary>
    /// Persisted mirror of the low-battery announcement guard (20 / 10 / 0 = none) — what
    /// HomeViewModel's UI-thread-only band map holds for this card's settings identity, kept on
    /// the card so persistence paths running off the UI thread (PersistDeviceCard from the gRPC
    /// discovery handler) can read it without touching the map. Written on the UI thread only;
    /// not bound to any view — no change notification.
    /// </summary>
    public int LowBatteryAnnouncedBand { get; set; }

    /// <summary>
    /// The REAL state of the Bluetooth link, independent of display. HasBluetooth is what the
    /// card shows and is suppressed while a cable is live (wired > bluetooth, PG-494);
    /// this flag keeps the underlying truth so losing the wired interface from a HID snapshot
    /// doesn't read as "device disconnected" for a card that is still on Bluetooth
    /// (HandleUsbHidDeviceFoundAsync's guard). Not bound to any view — no change notification.
    /// </summary>
    public bool BluetoothLinkUp { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatteryText))]
    [NotifyPropertyChangedFor(nameof(BatteryIconKind))]
    [NotifyPropertyChangedFor(nameof(HasBatteryIndicator))]
    [NotifyPropertyChangedFor(nameof(ConnectionTooltip))]
    public partial bool HasBattery { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatteryIconKind))]
    [NotifyPropertyChangedFor(nameof(HasBatteryIndicator))]
    public partial bool IsCharging { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatteryText))]
    [NotifyPropertyChangedFor(nameof(BatteryColor))]
    [NotifyPropertyChangedFor(nameof(BatteryIconKind))]
    [NotifyPropertyChangedFor(nameof(ConnectionTooltip))]
    public partial int? BatteryLevel { get; set; }

    [ObservableProperty]
    public partial string? FirmwareUpdateText { get; set; }
    [ObservableProperty]
    public partial string? FirmwareUpdateAction { get; set; }

    public BatteryIconKind BatteryIconKind =>
        IsCharging ? BatteryIconKind.Charging
        : !HasBattery ? BatteryIconKind.None
        : BatteryLevel == 100 ? BatteryIconKind.Full
        : BatteryLevel >= 40 ? BatteryIconKind.Normal
        : BatteryLevel >= 20 ? BatteryIconKind.Low
        : BatteryIconKind.Critical;

    public bool Connected => HasWire || HasDongle || HasBluetooth;

    /// <summary>
    /// Opacity of the device artwork on the card — full when connected, faded when not.
    /// </summary>
    /// <remarks>
    /// A bound value rather than a <c>Classes.dimmed</c> + style pair. The card's styles are full of
    /// selectors that silently never match (see the <c>UserControl.inactive</c> note in
    /// DoubleCard.axaml), and the layered-artwork rule was one of them: boards drawn through
    /// <c>RgbKeyboardView</c> — T-Racer and T800 — stayed at full brightness while disconnected,
    /// while the boards drawn through <c>SvgImageView</c> faded correctly. A style that fails to
    /// match produces no error and no warning, so the two art paths drifted apart unnoticed.
    /// Binding the property directly removes the indirection and makes both paths provably identical.
    /// </remarks>
    public double CardArtOpacity => Connected ? 1.0 : DisconnectedArtOpacity;

    /// <summary>Faded artwork opacity while disconnected — the 0.3 the mouse card also uses.</summary>
    public const double DisconnectedArtOpacity = 0.3;

    /// <summary>
    /// The same fade expressed as a black scrim, for artwork drawn through <c>RgbKeyboardView</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="CardArtOpacity"/> works for the raster art paths but has no effect on the layered
    /// one: its chassis and keycap layers are <c>Avalonia.Svg.Skia</c> <c>Svg</c> controls, which
    /// paint through their own draw operation and ignore an ancestor's opacity. That is why T800 and
    /// T-Racer stayed bright on the home card while every other board faded. Compositing black over
    /// the art gives the identical result on the card's near-black background — <c>0.3 x art</c>
    /// either way — so the two mechanisms differ only in how they get there.
    /// </remarks>
    public double CardArtDimOpacity => Connected ? 0.0 : 1.0 - DisconnectedArtOpacity;

    public bool HasBatteryIndicator => HasBattery || IsCharging;

    /// <summary>
    /// Hover text for the card's connection indicator: how the device is connected, plus its
    /// battery level on a second line when it reports one.
    /// </summary>
    /// <remarks>
    /// A card can light more than one transport icon at once (a TAIPAN shows cable and dongle
    /// together), so the transports are ranked rather than concatenated: the cable is the active
    /// data path whenever it is present, and the dongle relays only when there is no cable.
    /// Naming every lit icon would describe the indicator rather than the connection.
    ///
    /// The battery line is gated on an actual reading. A device that supports a battery but has
    /// not reported a level yet shows the transport alone, rather than "Battery level %" with a
    /// hole in it.
    /// </remarks>
    public string ConnectionTooltip
    {
        get
        {
            var transport = LocalizationManager.GetString(
                HasWire ? "Card_Connection_Cable"
                : HasDongle ? "Card_Connection_Dongle"
                : HasBluetooth ? "Card_Connection_Bluetooth"
                : "Card_Connection_None");

            if (!HasBattery || BatteryLevel is not { } level)
                return transport;

            return transport + Environment.NewLine
                + string.Format(LocalizationManager.GetString("Card_Connection_BatteryLevel"), level);
        }
    }

    public bool HasDongleSeparator => HasDongle && HasWire;
    public bool HasBluetoothSeparator => HasBluetooth && (HasWire || HasDongle);
    public string BatteryText =>
        BatteryLevel is not null ? $"{BatteryLevel}%" : HasBattery ? "-" : "";

    private static readonly IBrush LowBatteryBrush = new SolidColorBrush(Color.Parse("#F4C802"));
    private static readonly IBrush CriticalBatteryBrush = new SolidColorBrush(Color.Parse("#FF383C"));

    public IBrush BatteryColor =>
        BatteryLevel is null
            ? Brushes.Gray
            : BatteryLevel < 20
                ? CriticalBatteryBrush
                : BatteryLevel < 40
                    ? LowBatteryBrush
                    : Brushes.LimeGreen;

    /// <summary>
    /// Re-raises the card's code-built localized strings after a live language switch: the type
    /// label re-runs its DeviceTypeLabelConverter binding and the tooltip re-reads its resource
    /// strings. Driven by HomeViewModel's LanguageChanged subscription — the card itself holds no
    /// event subscriptions.
    /// </summary>
    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(DeviceTypeLabel));
        OnPropertyChanged(nameof(ConnectionTooltip));
    }

    public void MakeInactive()
    {
        HasWire = false;
        HasDongle = false;
        HasBluetooth = false;
        BluetoothLinkUp = false;
        HasBattery = false;
        BatteryLevel = null;
        IsCharging = false;
    }

    [RelayCommand]
    private void SwitchProfile() { }

    [RelayCommand]
    private void OpenSettings() => OpenSettingsRequested?.Invoke(this);

    [RelayCommand]
    private void Delete() => DeleteRequested?.Invoke();
}
