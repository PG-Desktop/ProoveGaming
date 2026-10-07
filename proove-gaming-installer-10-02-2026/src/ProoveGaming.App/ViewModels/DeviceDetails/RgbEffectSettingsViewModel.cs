using System;
using System.Collections.Generic;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;

namespace ProoveGaming.App.ViewModels;

/// <summary>
/// Per-effect settings: color picker state, preset colors, brightness and speed sliders.
/// Each effect that supports a color picker owns one of these instances.
/// </summary>
public class RgbEffectSettingsViewModel : ViewModelBase
{
	// ── Construction ─────────────────────────────────────────────────────────

	public static readonly IReadOnlyList<string> DefaultPresetColors =
	[
		"#FF0000",  // Red
		"#CC8800",  // Dark Yellow
		"#00CC44",  // Green
		"#00CCFF",  // Light Blue
		"#0033FF",  // Dark Blue
		"#8800FF",  // Purple
		"#FFEE00",  // Light Yellow
	];

	/// <param name="effectKey">Machine-readable key, e.g. "Static".</param>
	/// <param name="displayNameKey">Resource key for the human-readable label, e.g. "DeviceDetails_Rgb_Effect_Static".</param>
	/// <param name="hasColorPicker">Whether this effect exposes a color picker.</param>
	/// <param name="hasBrightness">Whether this effect exposes a brightness slider.</param>
	/// <param name="hasSpeed">Whether this effect exposes a speed slider.</param>
	/// <param name="selectEffect">Callback invoked when the user selects this effect.</param>
	/// <param name="presetColors">Optional override for the 7-color palette.</param>
	public RgbEffectSettingsViewModel(
		string effectKey,
		string displayNameKey,
		bool hasColorPicker,
		bool hasBrightness,
		bool hasSpeed,
		Action<RgbEffectSettingsViewModel> selectEffect,
		IReadOnlyList<string>? presetColors = null,
		Action<RgbEffectSettingsViewModel>? onColorApplied = null)
	{
		EffectKey = effectKey;
		_displayNameKey = displayNameKey;
		_displayName = LocalizationManager.GetString(displayNameKey);
		HasColorPicker = hasColorPicker;
		_declaredHasBrightness = hasBrightness;
		_hasBrightness = hasBrightness;
		HasSpeed = hasSpeed;
		PresetColors = presetColors ?? DefaultPresetColors;
		_onColorApplied = onColorApplied;

		if (TryParseHex(_selectedHexColor, out var initialColor))
		{
			var (h, s, v) = RgbToHsv(initialColor.R, initialColor.G, initialColor.B);
			_pickerSaturation = s;
			_pickerValue = v;
			_pickerHueDegrees = h;
			_pickerHueNormalized = h / 360.0;
			_pickerHueColor = HsvToHex(h, 1, 1);
		}

		SelectCommand = new RelayCommand(() => selectEffect(this));
		OpenPickerCommand = new RelayCommand(OpenPickerImpl);
		CancelPickerCommand = new RelayCommand(CancelPickerImpl);
		ApplyPickerCommand = new RelayCommand(ApplyPickerImpl);
		SelectPresetColorCommand = new RelayCommand<string>(SelectPresetColorImpl!);
		SelectGradientCommand = new RelayCommand(() => PickerIsRainbow = true);
	}

	// ── Identity ──────────────────────────────────────────────────────────────

	/// <summary>Machine-readable key (e.g. "Static", "Breathing").</summary>
	public string EffectKey { get; }

	// Held as a KEY, not a resolved string, so a language switch re-resolves through
	// RefreshDisplayName instead of freezing the label.
	private readonly string _displayNameKey;
	private string _displayName;

	/// <summary>Human-readable label shown in the UI, resolved from localization.</summary>
	public string DisplayName
	{
		get => _displayName;
		private set => SetProperty(ref _displayName, value);
	}

	/// <summary>Re-resolves DisplayName from the current language resources.</summary>
	public void RefreshDisplayName() => DisplayName = LocalizationManager.GetString(_displayNameKey);

	/// <summary>
	/// Sets whether the device currently being shown honours this effect's brightness. Whether the
	/// slider is worth offering depends on the hardware, not on the effect, so the decision is made
	/// by <c>DeviceDetailsViewModel.RecomputeVisibleRgbEffects</c> and pushed in here.
	/// </summary>
	/// <remarks>
	/// A <c>true</c> never overrides an effect that declares no brightness at all — the declared
	/// capability is the ceiling, and a device cannot add a control the effect does not have.
	/// </remarks>
	internal void SetBrightnessSupported(bool supported) =>
		HasBrightness = supported && _declaredHasBrightness;

	// ── Capabilities ─────────────────────────────────────────────────────────

	/// <summary>Whether this effect exposes a color picker.</summary>
	public bool HasColorPicker { get; }

	// What the effect itself offers, and what the device currently shown actually honours. Settable
	// (and notifying) because SetBrightnessSupported resolves it whenever the shown device changes —
	// the two bindings on it are a visibility and a style class.
	private readonly bool _declaredHasBrightness;
	private bool _hasBrightness;

	/// <summary>Whether this effect exposes a brightness slider on the device currently shown.</summary>
	public bool HasBrightness
	{
		get => _hasBrightness;
		private set
		{
			if (SetProperty(ref _hasBrightness, value))
				OnPropertyChanged(nameof(HasExpandableContent));
		}
	}

	public bool HasSpeed { get; }

	/// <summary>
	/// Whether selecting this effect reveals any controls, i.e. whether the radio row has to be
	/// allowed to grow past its fixed 56px height (the <c>rgbOptionRadioExpandable</c> class).
	/// </summary>
	/// <remarks>
	/// The row was bound straight to <see cref="HasBrightness"/> until 2026-08-17, which worked only
	/// because every effect with anything to expand also had brightness. Mouse Wave broke that — no
	/// brightness (the firmware ignores it) but still a speed slider — and binding the height to
	/// brightness alone clipped that slider out of view.
	/// </remarks>
	public bool HasExpandableContent => HasColorPicker || HasBrightness || HasSpeed;

	// ── Selection state ───────────────────────────────────────────────────────

	private bool _isSelected;

	public bool IsSelected
	{
		get => _isSelected;
		set => SetProperty(ref _isSelected, value);
	}

	// ── Preset colors ────────────────────────────────────────────────────────

	public IReadOnlyList<string> PresetColors { get; }

	// ── Selected color (committed) ────────────────────────────────────────────

	private string _selectedHexColor = "#7620EE";

	public string SelectedHexColor
	{
		get => _selectedHexColor;
		set => SetProperty(ref _selectedHexColor, value);
	}

	// ── Rainbow / gradient (all-colours) mode ─────────────────────────────────
	// Only Breathing offers it (the device plays "all colours" via colorSlot 7). Committed
	// value; the picker's working state is _pickerIsRainbow, committed on Apply.
	public bool SupportsRainbow => EffectKey == "Breathing";

	private bool _isRainbow;
	public bool IsRainbow
	{
		get => _isRainbow;
		set => SetProperty(ref _isRainbow, value);
	}

	private bool _pickerIsRainbow;
	public bool PickerIsRainbow
	{
		get => _pickerIsRainbow;
		private set
		{
			if (SetProperty(ref _pickerIsRainbow, value))
				OnPropertyChanged(nameof(PickerSelectedSwatch));
		}
	}

	// The solid swatch that should read as selected — empty while rainbow is chosen, so a
	// solid swatch and the gradient swatch are never both highlighted.
	public string PickerSelectedSwatch => _pickerIsRainbow ? string.Empty : _pickerHexColor;

	// ── Picker open state ────────────────────────────────────────────────────

	private bool _isPickerOpen;

	public bool IsPickerOpen
	{
		get => _isPickerOpen;
		set => SetProperty(ref _isPickerOpen, value);
	}

	// ── Picker working color (hex) ────────────────────────────────────────────

	private string _pickerHexColor = "#7620EE";

	public string PickerHexColor
	{
		get => _pickerHexColor;
		set => SetPickerHexColor(value, updateHue: true);
	}

	// Used internally by bitmap-pointer paths — skips hue recalculation to avoid feedback loops.
	internal void SetPickerHexColorFromPicker(string value) => SetPickerHexColor(value, updateHue: false);

	private void SetPickerHexColor(string value, bool updateHue)
	{
		if (SetProperty(ref _pickerHexColor, value, nameof(PickerHexColor)))
		{
			// Choosing a solid colour (hex entry or picker drag) leaves rainbow mode.
			PickerIsRainbow = false;
			OnPropertyChanged(nameof(PickerSelectedSwatch));
			if (updateHue && TryParseHex(value, out var color))
			{
				var (h, s, v) = RgbToHsv(color.R, color.G, color.B);
				PickerSaturation = s;
				PickerValue = v;
				if (s > 0.0001)
					SetHueDegrees(h);
			}
		}
	}

	// ── Hue state ────────────────────────────────────────────────────────────

	private double _pickerHueDegrees;
	private double _pickerHueNormalized;
	private string _pickerHueColor = "#FF0000";

	public double PickerHueNormalized
	{
		get => _pickerHueNormalized;
		set => SetProperty(ref _pickerHueNormalized, Math.Clamp(value, 0, 1));
	}

	public string PickerHueColor
	{
		get => _pickerHueColor;
		set
		{
			if (SetProperty(ref _pickerHueColor, value))
				OnPropertyChanged(nameof(PickerHueBrush));
		}
	}

	public IBrush PickerHueBrush => new SolidColorBrush(Color.Parse(PickerHueColor));

	internal void SetHueDegrees(double hue)
	{
		_pickerHueDegrees = Math.Clamp(hue, 0, 360);
		PickerHueNormalized = _pickerHueDegrees / 360.0;
		PickerHueColor = HsvToHex(_pickerHueDegrees, 1, 1);
	}

	internal double PickerHueDegrees => _pickerHueDegrees;

	// ── Saturation / Value ───────────────────────────────────────────────────

	private double _pickerSaturation = 1;
	private double _pickerValue = 1;

	public double PickerSaturation
	{
		get => _pickerSaturation;
		set => SetProperty(ref _pickerSaturation, Math.Clamp(value, 0, 1));
	}

	public double PickerValue
	{
		get => _pickerValue;
		set => SetProperty(ref _pickerValue, Math.Clamp(value, 0, 1));
	}

	// ── Sliders ──────────────────────────────────────────────────────────────

	private double _brightness = 60;
	private double _speed = 50;

	public double Brightness
	{
		get => _brightness;
		set => SetProperty(ref _brightness, Math.Clamp(value, 0, 100));
	}

	public double Speed
	{
		get => _speed;
		set => SetProperty(ref _speed, Math.Clamp(value, 0, 100));
	}

	// ── Commands ─────────────────────────────────────────────────────────────

	public IRelayCommand SelectCommand { get; }
	public IRelayCommand OpenPickerCommand { get; }
	public IRelayCommand CancelPickerCommand { get; }
	public IRelayCommand ApplyPickerCommand { get; }
	public IRelayCommand<string> SelectPresetColorCommand { get; }
	public IRelayCommand SelectGradientCommand { get; }

	private void OpenPickerImpl()
	{
		if (TryParseHex(SelectedHexColor, out var c))
		{
			var (h, s, v) = RgbToHsv(c.R, c.G, c.B);
			_pickerSaturation = s;
			_pickerValue = v;
			_pickerHueDegrees = h;
			_pickerHueNormalized = h / 360.0;
			_pickerHueColor = HsvToHex(h, 1, 1);
			OnPropertyChanged(nameof(PickerSaturation));
			OnPropertyChanged(nameof(PickerValue));
			OnPropertyChanged(nameof(PickerHueNormalized));
			OnPropertyChanged(nameof(PickerHueColor));
			OnPropertyChanged(nameof(PickerHueBrush));
		}
		SetProperty(ref _pickerHexColor, SelectedHexColor, nameof(PickerHexColor));
		PickerIsRainbow = IsRainbow;
		OnPropertyChanged(nameof(PickerSelectedSwatch));
		IsPickerOpen = true;
	}

	private void CancelPickerImpl()
	{
		PickerHexColor = SelectedHexColor;
		PickerIsRainbow = IsRainbow;
		IsPickerOpen = false;
	}

	private readonly Action<RgbEffectSettingsViewModel>? _onColorApplied;

	private void ApplyPickerImpl()
	{
		SelectedHexColor = PickerHexColor;
		IsRainbow = PickerIsRainbow;
		IsPickerOpen = false;
		_onColorApplied?.Invoke(this);
	}

	private void SelectPresetColorImpl(string colorHex)
	{
		if (TryParseHex(colorHex, out var color))
		{
			var (h, s, v) = RgbToHsv(color.R, color.G, color.B);
			_pickerSaturation = s;
			_pickerValue = v;
			_pickerHueDegrees = h;
			_pickerHueNormalized = h / 360.0;
			_pickerHueColor = HsvToHex(h, 1, 1);
			OnPropertyChanged(nameof(PickerSaturation));
			OnPropertyChanged(nameof(PickerValue));
			OnPropertyChanged(nameof(PickerHueNormalized));
			OnPropertyChanged(nameof(PickerHueColor));
			OnPropertyChanged(nameof(PickerHueBrush));
		}
		SetProperty(ref _pickerHexColor, colorHex, nameof(PickerHexColor));
		PickerIsRainbow = false;
		OnPropertyChanged(nameof(PickerSelectedSwatch));
	}

	// ── Picker pointer updates (called from control code-behind) ─────────────

	public void UpdateHueFromStripPosition(double normalizedX)
	{
		normalizedX = Math.Clamp(normalizedX, 0, 1);
		var hue = normalizedX * 360.0;
		_pickerHueDegrees = hue;
		PickerHueNormalized = normalizedX;
		PickerHueColor = HsvToHex(hue, 1, 1);
		SetPickerHexColorFromPicker(HsvToHex(hue, _pickerSaturation, _pickerValue));
	}

	public void UpdateColorFromPlanePosition(double normalizedX, double normalizedY)
	{
		normalizedX = Math.Clamp(normalizedX, 0, 1);
		normalizedY = Math.Clamp(normalizedY, 0, 1);
		PickerSaturation = normalizedX;
		PickerValue = 1.0 - normalizedY;
		SetPickerHexColorFromPicker(HsvToHex(_pickerHueDegrees, normalizedX, 1.0 - normalizedY));
	}

	// ── Color math helpers ────────────────────────────────────────────────────

	private static bool TryParseHex(string? text, out Color color)
	{
		try
		{
			if (string.IsNullOrWhiteSpace(text)) { color = default; return false; }
			color = Color.Parse(text);
			return true;
		}
		catch { color = default; return false; }
	}

	private static string HsvToHex(double h, double s, double v)
	{
		h = (h % 360 + 360) % 360;
		s = Math.Clamp(s, 0, 1);
		v = Math.Clamp(v, 0, 1);
		double c = v * s, x = c * (1 - Math.Abs((h / 60.0) % 2 - 1)), m = v - c;
		double r1, g1, b1;
		if (h < 60) (r1, g1, b1) = (c, x, 0);
		else if (h < 120) (r1, g1, b1) = (x, c, 0);
		else if (h < 180) (r1, g1, b1) = (0, c, x);
		else if (h < 240) (r1, g1, b1) = (0, x, c);
		else if (h < 300) (r1, g1, b1) = (x, 0, c);
		else (r1, g1, b1) = (c, 0, x);
		byte r = (byte)Math.Round((r1 + m) * 255);
		byte g = (byte)Math.Round((g1 + m) * 255);
		byte b = (byte)Math.Round((b1 + m) * 255);
		return $"#{r:X2}{g:X2}{b:X2}";
	}

	private static (double h, double s, double v) RgbToHsv(byte rByte, byte gByte, byte bByte)
	{
		double r = rByte / 255.0, g = gByte / 255.0, b = bByte / 255.0;
		double max = Math.Max(r, Math.Max(g, b));
		double min = Math.Min(r, Math.Min(g, b));
		double delta = max - min;
		double h = 0;
		if (delta > 0)
		{
			if (Math.Abs(max - r) < double.Epsilon) h = 60 * (((g - b) / delta) % 6);
			else if (Math.Abs(max - g) < double.Epsilon) h = 60 * (((b - r) / delta) + 2);
			else h = 60 * (((r - g) / delta) + 4);
		}
		if (h < 0) h += 360;
		return (h, max <= 0 ? 0 : delta / max, max);
	}
}
