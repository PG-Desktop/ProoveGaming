using CommunityToolkit.Mvvm.Input;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Svg.Skia;
using Avalonia.Threading;
using ProoveGaming.App.Models;
using ProoveGaming.App.Models.Settings;
using ProoveGaming.App.Services;
using SkiaSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ProoveGaming.App.ViewModels;

public partial class DeviceDetailsViewModel
{
	// ── RGB backing fields ────────────────────────────────────────────────────
	private bool _isRgbEnabled = true;
	private bool _suppressAutoApply;
	private CancellationTokenSource? _sliderDebounceCts;
	private static readonly Random _rng = new();
	// Rasterized device-only bitmaps are keyed by RgbImagePath and cached process-wide (a handful
	// of small bundled SVGs, never invalidated). Populated by HomeViewModel via
	// PrewarmDeviceOnlyBitmapAsync as soon as each device card is known, so opening a device's
	// details reads a synchronously-ready entry instead of decoding on the spot. Concurrent, not
	// Dictionary: multiple devices can prewarm on background threads at once (PrewarmDeviceOnlyBitmapAsync
	// writes off the UI thread), while InitDeviceOnlySvg reads from the UI thread.
	private static readonly ConcurrentDictionary<string, Bitmap> s_deviceOnlyBitmapCache = new();
	// Raised (off the UI thread — from inside PrewarmDeviceOnlyBitmapAsync's background decode)
	// whenever a bitmap finishes caching, so an already-open Details page that hit a cache miss on
	// SetDevice (and fell back to the raw Svg) can still pick up the real bitmap once it's ready,
	// instead of being stuck on the fallback for the rest of that viewing session. Subscribed once
	// in the constructor — this VM is a DI singleton, same lifetime as the event itself.
	private static event Action<string>? DeviceOnlyBitmapCached;
	private RgbEffectSettingsViewModel? _selectedEffect;
	private IBrush _animatedGlowBrush = Brushes.Transparent;
	private double _hueCycleAngle;
	private DispatcherTimer? _hueCycleTimer;
	private DispatcherTimer? _animationTimer;
	private double _animationPhase;
	private Bitmap? _deviceOnlyBitmap;

	// In-session cache so that flipping between opened devices reflects instantly without a
	// DB round-trip — the persisted store is still the source of truth. Keyed by device_key
	// too, so two physically distinct units sharing the same vid/pid don't collide.
	//
	// The OWNER is part of the key. This view model is a DI singleton that outlives every sign-in
	// and sign-out, and the page open resolves cache-first and then persists what it resolved under
	// the CURRENT owner — so an owner-less key meant account B's page open wrote account A's colours
	// into B's rows, and going back to A wrote B's over A's. With the owner in the key an entry can
	// never be read under a different account, whichever path led there.
	private readonly Dictionary<(string ownerKind, string ownerId, int vid, int pid, string key), RgbDeviceState> _rgbStateByDevice = new();

	// The one place the cache key's shape is defined.
	private static (string ownerKind, string ownerId, int vid, int pid, string key) RgbCacheKey(
		SettingsOwner owner, DeviceCardViewModel device) =>
		(owner.Kind, owner.Id, device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey);

	// Cache probe that is a no-op without an owner (welcome screen, no session): there is nothing to
	// key an entry by, and the storage read the callers fall through to already returns null there.
	private RgbDeviceState? GetCachedRgbState(SettingsOwner? owner, DeviceCardViewModel device) =>
		owner is not null && _rgbStateByDevice.TryGetValue(RgbCacheKey(owner.Value, device), out var state)
			? state
			: null;

	// The account whose RGB state the page is currently SHOWING — recorded by ApplyResolvedRgbState,
	// which is the one moment the page starts displaying a specific account's colours, and cleared by
	// ClearDeviceForExit. Every persist snapshots those displayed properties, so this, not the owner
	// current when the snapshot is taken, is the account a snapshot actually describes: a sign-out
	// does not leave the details page, so without it the next account's first open flushes the
	// departing account's still-displayed colours into its own rows. Null means the page has not
	// displayed anyone's state yet, so there is nothing that could belong to another account.
	private SettingsOwner? _displayedRgbOwner;

	// Drops every entry that does not belong to `owner` — all of them when there is none. Bounds the
	// cache across a session's account switches and guarantees no other account's state is still
	// addressable; the owner in the key is what makes it correct, this only makes it tidy.
	private void PurgeRgbCacheForOtherOwners(SettingsOwner? owner)
	{
		var stale = _rgbStateByDevice.Keys
			.Where(k => owner is null || k.ownerKind != owner.Value.Kind || k.ownerId != owner.Value.Id)
			.ToList();
		foreach (var key in stale)
			_rgbStateByDevice.Remove(key);
	}

	// Some effects (Static) are only meaningful on certain device families. Rather than owning
	// separate effect lists per device, we filter the shared RgbEffects into this view.
	private IReadOnlyList<RgbEffectSettingsViewModel> _visibleRgbEffects = [];
	public IReadOnlyList<RgbEffectSettingsViewModel> VisibleRgbEffects
	{
		get => _visibleRgbEffects;
		private set
		{
			if (SetProperty(ref _visibleRgbEffects, value))
				OnPropertyChanged(nameof(HasVisibleRgbEffects));
		}
	}

	// True when the current device has at least one usable RGB effect. Drives the Random button's
	// visibility so it isn't shown (and can't pick) when there are no effects to choose from.
	public bool HasVisibleRgbEffects => _visibleRgbEffects.Count > 0;

	// Effects a mouse does not offer.
	//
	// Static is a keyboard idea. ColorCycle is too, now: on a single-LED mouse the firmware's
	// protocol-0x04 effect is a travelling gradient, which the App calls Wave and offers to mice
	// under that name — so a mouse has no "colour cycle" to show (X43LedEncoder.MapEffect sends
	// Wave to 0x04). Keyboards keep both, and they are separate effects there.
	private static readonly HashSet<string> KeyboardOnlyEffectKeys = new(StringComparer.Ordinal)
	{
		"Static",
		"ColorCycle",
	};

	// Effects whose brightness a MOUSE ignores, so the slider must not be offered there. Wave is
	// protocol 0x04 on BLAZE, where byte 3 is not a level but a gate on whether the effect renders
	// at all — the composer pins it to the OEM's value (X43LedEncoder.ResolveBrightness), so
	// anything the user set would be silently discarded. Keyboards drive their own controllers and
	// honour it normally.
	private static readonly HashSet<string> MouseEffectsWithoutBrightness = new(StringComparer.Ordinal)
	{
		"Wave",
	};

	private void RecomputeVisibleRgbEffects()
	{
		var isKeyboard = Device?.DeviceTypeLabel?.Contains("keyboard", StringComparison.OrdinalIgnoreCase) == true;
		VisibleRgbEffects = isKeyboard
			? RgbEffects
			: RgbEffects.Where(e => !KeyboardOnlyEffectKeys.Contains(e.EffectKey)).ToList();

		// Capability is a property of the effect ON THIS DEVICE, not of the effect, so it is decided
		// here rather than declared in the catalog. Applied to every effect so switching back to a
		// keyboard restores the slider.
		foreach (var effect in RgbEffects)
			effect.SetBrightnessSupported(isKeyboard || !MouseEffectsWithoutBrightness.Contains(effect.EffectKey));
	}

	private sealed record PerEffectState(string Hex, double Brightness, double Speed, bool IsRainbow);

	private sealed class RgbDeviceState
	{
		public bool IsEnabled { get; set; } = true;
		public string SelectedEffectKey { get; set; } = string.Empty;
		public Dictionary<string, PerEffectState> Effects { get; } = new();
	}

	// ── Properties ───────────────────────────────────────────────────────────
	public bool IsRgbSupported => Device?.SupportsRgb ?? false;

	// ── Decorative glow ──────────────────────────────────────────────────────
	//
	// A glow the App draws for its own sake, on a device with no host-controllable lighting. The
	// headsets have RGB in the hardware but nothing we can address, so their catalog entries keep
	// SupportsRgb: false and they get no RGB section, no colour picker, and no write path — this is
	// purely the details page animating artwork the designer already drew for it.
	//
	// Deliberately a SEPARATE gate from IsRgbSupported rather than a widening of it. Everything
	// downstream of IsRgbSupported means "the user can change the lighting on this device", and
	// making that true for a headset would offer controls that cannot do anything.
	private DeviceGlowArt? GlowArt => DeviceGlowArtCatalog.For(Device?.Name);

	public bool HasDecorativeGlow => GlowArt is not null;

	// Shown only once the artwork it sits on is actually there. The glow is plain vector geometry
	// and paints on the first frame; the artwork is a multi-megabyte SVG with an embedded photo, so
	// without this gate the effect appeared alone against the background and the headset faded in
	// behind it afterwards. Gating on the decoded bitmap means both arrive together.
	public bool IsDecorativeGlowVisible => HasDecorativeGlow && HasDeviceOnlyBitmap;
	public double GlowCanvasWidth => GlowArt?.CanvasWidth ?? 0;
	public double GlowCanvasHeight => GlowArt?.CanvasHeight ?? 0;
	public IReadOnlyList<DeviceGlowLayer> GlowLayersBehind => GlowArt?.LayersBehind ?? [];
	public IReadOnlyList<DeviceGlowLayer> GlowLayersOver => GlowArt?.LayersOver ?? [];

	public Bitmap? DeviceOnlyBitmap    => _deviceOnlyBitmap;
	public bool    HasDeviceOnlyBitmap => _deviceOnlyBitmap != null;
	public bool    HasNoDeviceOnlyBitmap => _deviceOnlyBitmap == null;

	// Use the transparent-background vector image for any RGB-capable device so the animated glow is visible
	public string? DeviceDetailImagePath =>
		!string.IsNullOrEmpty(Device?.RgbImagePath)
			? Device.RgbImagePath
			: Device?.ImagePath;

	// Cosmo's artwork is a PNG, and the <Svg> control renders nothing for one. These pick the right
	// control for the resolved path (which may be the RGB vector even when ImagePath is raster), so
	// a raster device shows its image instead of an empty frame.
	public bool IsRasterDeviceImage =>
		DeviceDetailImagePath?.EndsWith(".png", StringComparison.OrdinalIgnoreCase) == true
		|| DeviceDetailImagePath?.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) == true
		|| DeviceDetailImagePath?.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) == true;

	public string? DeviceDetailImageUri =>
		string.IsNullOrEmpty(DeviceDetailImagePath) ? null : $"avares://ProoveGaming.App{DeviceDetailImagePath}";

	// The vector branch: no rasterized RGB bitmap yet AND the path is actually vector.
	public bool HasVectorDeviceImage => HasNoDeviceOnlyBitmap && !IsRasterDeviceImage;

	public bool IsRgbEnabled
	{
		get => _isRgbEnabled;
		set
		{
			if (SetProperty(ref _isRgbEnabled, value))
			{
				if (!value)
				{
					StopSolidHueCycleTimer();
					StopAnimationTimer();
					// Stopping the timers only halts future ticks; the brush still holds
					// whatever was painted last, so clear it or the board stays lit.
					UpdateGlowBrush();
				}
				else
				{
					StartEffectAnimation(_selectedEffect);
				}
				SaveRgbStateForCurrentDevice();
			}
		}
	}

	public RgbEffectSettingsViewModel ActiveRgbEffect =>
		RgbEffects.FirstOrDefault(e => e.IsSelected)
		?? VisibleRgbEffects.FirstOrDefault()
		?? RgbEffects[0];

	public IBrush AnimatedGlowBrush
	{
		get => _animatedGlowBrush;
		private set => SetProperty(ref _animatedGlowBrush, value);
	}

	// Drops the cached device-only bitmap and notifies bindings. DeviceDetailsViewModel is a
	// singleton reused across every device the user opens, so without this a device with no RGB
	// image (e.g. TAIPAN) would keep showing whatever the PREVIOUS device (e.g. BLAZE) last
	// rendered — InitDeviceOnlySvg only runs when the new device HAS an image, so the field is
	// otherwise never cleared on a switch.
	//
	// Called from MainWindow's PerformExitDeviceDetails (leaving the view, back to Home), NOT from
	// SetDevice — clearing on entry would flash the old device's image, then blank, then the new
	// one; clearing on exit (while Home is showing, invisible) means the next SetDevice starts from
	// a clean slate with no flash.
	internal void ClearDeviceOnlyBitmap()
	{
		_deviceOnlyBitmap = null;
		OnPropertyChanged(nameof(DeviceOnlyBitmap));
		OnPropertyChanged(nameof(HasDeviceOnlyBitmap));
		OnPropertyChanged(nameof(HasNoDeviceOnlyBitmap));
		OnPropertyChanged(nameof(HasVectorDeviceImage));
		OnPropertyChanged(nameof(IsDecorativeGlowVisible));
	}

	// Whether this artwork belongs to a device that draws its own glow layers (see
	// DeviceGlowArtCatalog). Matched on the path because the prewarm is static and has no device.
	private static bool UsesDecorativeGlowArtwork(string rgbImagePath) =>
		DeviceGlowArtCatalog.ArtworkPaths.Contains(rgbImagePath, StringComparer.OrdinalIgnoreCase);

	// A bitmap the artwork fills exactly, at BLAZE-comparable resolution on the long side.
	//
	// Internal because this is the rule that broke: the fixed 934x1040 canvas letterboxed a
	// landscape headset into its top ~67 %, so the picture stopped occupying its own viewBox and
	// the glow — whose geometry IS in that viewBox — rendered off the device. Nothing throws when
	// it is wrong; the artwork simply sits somewhere else. See LayoutRuleTests.
	internal static (int Width, int Height) AspectPreservingSize(float width, float height)
	{
		const int longSide = 1040;
		return width >= height
			? (longSide, Math.Max(1, (int)Math.Round(longSide * height / width)))
			: (Math.Max(1, (int)Math.Round(longSide * width / height)), longSide);
	}

	// ── SVG device-only bitmap (transparent background for glow pass-through) ─
	// Read-only against the cache — this method never decodes or writes to it. HomeViewModel
	// pre-warms every listed device's bitmap as soon as its RgbImagePath is known (see
	// PrewarmDeviceOnlyBitmapAsync below), well before the user can click into its details, so by
	// the time SetDevice runs here the entry should already exist.
	private void InitDeviceOnlySvg(string rgbImagePath)
	{
		if (s_deviceOnlyBitmapCache.TryGetValue(rgbImagePath, out var cached))
		{
			_deviceOnlyBitmap = cached;
			OnPropertyChanged(nameof(DeviceOnlyBitmap));
			OnPropertyChanged(nameof(HasDeviceOnlyBitmap));
			OnPropertyChanged(nameof(HasNoDeviceOnlyBitmap));
			OnPropertyChanged(nameof(HasVectorDeviceImage));
		OnPropertyChanged(nameof(IsDecorativeGlowVisible));
			return;
		}

		// Not cached (shouldn't normally happen — see above). Clear to the raw-Svg fallback
		// (HasNoDeviceOnlyBitmap) rather than decode here; the view falls back to
		// <Svg Path="{Binding DeviceDetailImagePath}"> until a later prewarm catches up.
		ClearDeviceOnlyBitmap();
	}

	// Decodes and caches the device-only bitmap for `rgbImagePath` without touching any instance's
	// current state — safe to call for every device in the list regardless of which one (if any) is
	// currently shown in Device Details. Called from HomeViewModel as soon as a device card's
	// RgbImagePath is known (on load and on every connect/refresh). No-op if already cached.
	internal static async Task PrewarmDeviceOnlyBitmapAsync(string? rgbImagePath)
	{
		if (string.IsNullOrEmpty(rgbImagePath) || s_deviceOnlyBitmapCache.ContainsKey(rgbImagePath))
			return;



		// AssetLoader requires the UI thread; HomeViewModel calls this from the UI thread.
		string svgText;
		try
		{
			var assetUri = new Uri($"avares://ProoveGaming.App{rgbImagePath}");
			using var stream = AssetLoader.Open(assetUri);
			using var reader = new StreamReader(stream);
			svgText = await reader.ReadToEndAsync();
		}
		catch { return; }

		// Strip the body glow fill so the animated glow shows through.
		// Handles: literal hex placeholder (e.g. #FF0000 in Blaze-Vector.svg)
		// and gradient URL refs (e.g. fill="url(#paint...)") in other variants.
		svgText = svgText.Replace("fill=\"#FF0000\"", "fill=\"none\"");
		svgText = Regex.Replace(svgText, @"fill=""url\(#paint[^)]+\)""", "fill=\"none\"");

		await Task.Run(() =>
		{
			try
			{
				var src = SvgSource.LoadFromSvg(svgText);
				if (src?.Picture is not { } pic || pic.CullRect.Width <= 0) return;

				// The 934x1040 canvas is BLAZE's: its artwork fills it to within a pixel, and
				// the picture is drawn at the TOP-LEFT with no centring. That is fine for a device
				// shaped like the canvas and wrong for anything else — a landscape headset lands in
				// the top ~67 % with 340 px of dead space beneath, so the picture stops occupying
				// its own SVG viewBox. For a device whose glow WE draw, that misalignment is
				// visible: the glow geometry lives in the viewBox, so the two must correspond.
				//
				// So those devices get a canvas matching their own aspect instead. The long side
				// keeps BLAZE's resolution; nothing is letterboxed, so viewBox and bitmap agree.
				var (renderW, renderH) = UsesDecorativeGlowArtwork(rgbImagePath)
					? AspectPreservingSize(pic.CullRect.Width, pic.CullRect.Height)
					: (934, 1040);

				var scale = Math.Min((float)renderW / pic.CullRect.Width,
				                     (float)renderH / pic.CullRect.Height);
				using var bmp = new SKBitmap(renderW, renderH);
				using (var canvas = new SKCanvas(bmp))
				{
					canvas.Clear(SKColors.Transparent);
					canvas.Scale(scale);
					canvas.DrawPicture(pic);
				}
				using var ms = new MemoryStream();
				bmp.Encode(ms, SKEncodedImageFormat.Png, 100);
				ms.Seek(0, SeekOrigin.Begin);
				var avBitmap = new Bitmap(ms);
				s_deviceOnlyBitmapCache[rgbImagePath] = avBitmap;
				DeviceOnlyBitmapCached?.Invoke(rgbImagePath);
			}
			catch { }
		});
	}

	// Test seam (InternalsVisibleTo ProoveGaming.App.Tests): triggers the same notification
	// PrewarmDeviceOnlyBitmapAsync raises after a real decode, so the constructor-subscription →
	// handler → dispatcher-marshal → Device-match guard path is exercisable without a real SVG
	// asset. Constructing an actual Avalonia Bitmap needs a platform render interface this test
	// project doesn't initialize, so only the "wrong device" no-op side of OnDeviceOnlyBitmapCached
	// is covered this way — the cache-hit assignment itself is not.
	internal static void RaiseDeviceOnlyBitmapCachedForTests(string rgbImagePath) =>
		DeviceOnlyBitmapCached?.Invoke(rgbImagePath);

	// Picks up a bitmap that finished decoding after InitDeviceOnlySvg already hit a cache miss for
	// the currently-open device (see DeviceOnlyBitmapCached). No-op for any other path — most calls
	// are for whichever OTHER device just got prewarmed while this one is open. Runs off the UI
	// thread (raised from PrewarmDeviceOnlyBitmapAsync's background decode), so marshal before
	// touching instance state.
	private void OnDeviceOnlyBitmapCached(string rgbImagePath)
	{
		Dispatcher.UIThread.Post(() =>
		{
			if (Device?.RgbImagePath != rgbImagePath) return;
			if (!s_deviceOnlyBitmapCache.TryGetValue(rgbImagePath, out var bitmap)) return;

			_deviceOnlyBitmap = bitmap;
			OnPropertyChanged(nameof(DeviceOnlyBitmap));
			OnPropertyChanged(nameof(HasDeviceOnlyBitmap));
			OnPropertyChanged(nameof(HasNoDeviceOnlyBitmap));
			OnPropertyChanged(nameof(HasVectorDeviceImage));
		OnPropertyChanged(nameof(IsDecorativeGlowVisible));
		});
	}

	// ── Effect selection & events ─────────────────────────────────────────────
	private void SelectEffect(RgbEffectSettingsViewModel selected)
	{
		StopSolidHueCycleTimer();
		_selectedEffect = selected;
		StopAnimationTimer();

		foreach (var effect in RgbEffects)
			effect.IsSelected = ReferenceEquals(effect, selected);
		OnPropertyChanged(nameof(ActiveRgbEffect));

		if (IsRgbEnabled)
			StartEffectAnimation(selected);

		if (!_suppressAutoApply)
			StageRgbSettings();
	}

	private void OnEffectColorApplied(RgbEffectSettingsViewModel effect)
	{
		// Applying may have toggled the gradient (rainbow) swatch, which changes which
		// animation runs for Breathing — re-arm it for the active effect.
		if (IsRgbEnabled && ReferenceEquals(effect, ActiveRgbEffect))
		{
			StopSolidHueCycleTimer();
			StopAnimationTimer();
			StartEffectAnimation(effect);
		}
		else if (_hueCycleTimer == null && _animationTimer == null)
		{
			UpdateGlowBrush();
		}

		if (!_suppressAutoApply)
			StageRgbSettings();
	}

	// Brightness/Speed sliders fire a PropertyChanged per drag step — debounce so the device
	// only gets written ~300ms after the user stops moving the slider.
	private void OnRgbEffectPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (_suppressAutoApply) return;
		if (sender is not RgbEffectSettingsViewModel effect || !ReferenceEquals(effect, ActiveRgbEffect)) return;

		if (e.PropertyName is nameof(RgbEffectSettingsViewModel.SelectedHexColor) or nameof(RgbEffectSettingsViewModel.Brightness))
		{
			if (_hueCycleTimer == null && _animationTimer == null)
				UpdateGlowBrush();
		}

		if (e.PropertyName is nameof(RgbEffectSettingsViewModel.Brightness) or nameof(RgbEffectSettingsViewModel.Speed))
		{
			_sliderDebounceCts?.Cancel();
			var cts = new CancellationTokenSource();
			_sliderDebounceCts = cts;
			_ = DebounceApplyRgbSettingsAsync(cts.Token);
		}
	}

	private async Task DebounceApplyRgbSettingsAsync(CancellationToken token)
	{
		try { await Task.Delay(300, token); }
		catch (TaskCanceledException) { return; }
		StageRgbSettings();
	}

	// RGB edits stage into the preset: persist the selection, mark the preset dirty, and
	// (debounced, via StageChangeAndApply inside SaveRgbStateForCurrentDevice) push the LED
	// change to the device — see ApplyCurrentStateToDeviceAsync. The app-side glow animation
	// still previews the effect immediately while editing, ahead of the debounce settling.
	private void StageRgbSettings() => SaveRgbStateForCurrentDevice();

	// Pushes the current RGB selection to the hardware, honouring the on/off state. Used by the
	// full-state device push, not per edit.
	//
	// MUST stay a pure device write. This runs INSIDE ApplyCurrentStateToDeviceAsync, so anything
	// here that stages (SaveRgbStateForCurrentDevice, StageChangeAndApply, or any helper that ends
	// in one) schedules the very debounced apply that called it. That is not hypothetical: it used
	// to route through an ApplyRgbSettingsAsync whose synchronous prefix staged, and on keyboards —
	// the only branch that pushes RGB this way — it rewrote the LED config every ~250ms forever,
	// restarting the hardware effect before it could run and stopping only when the App exited.
	// Persisting is fine (PersistRgbStateForCurrentDevice); staging is not.
	//
	// BuildLedConfig already encodes the off state (effect 0xED, brightness 0), so the on/off
	// branch lives there rather than in a separate toggle path.
	private Task PushRgbToDeviceAsync() => WriteRgbToDeviceAsync();

	// The one place the LED config actually reaches the hardware. No persistence, no staging.
	private Task WriteRgbToDeviceAsync()
	{
		if (_usbHidDeviceService == null || Device == null || !IsDeviceConfigurable)
			return Task.CompletedTask;

		var (r, g, b, effect, brightness, speed) = BuildLedConfig();
		return _usbHidDeviceService.SetDeviceLedColorAsync(
			Device.VendorId, Device.ProductId, Device.DeviceKey, Device.DeviceTypeLabel,
			r, g, b, effect, brightness, speed);
	}

	// The current RGB selection as the wire values (colour + app effect byte + brightness/speed) for
	// the unified device-configuration apply and for WriteRgbToDeviceAsync. Off (RGB disabled) maps
	// to effect 0xED + brightness 0 — confirmed to persist across USB reconnect.
	internal (byte r, byte g, byte b, byte effect, byte brightness, byte speed) BuildLedConfig()
	{
		if (!IsRgbEnabled)
			return (0, 0, 0, 0xED, 0, 3);

		var selected = ActiveRgbEffect;
		Color.TryParse(selected.SelectedHexColor, out var color);
		byte effectByte = selected.SupportsRainbow && selected.IsRainbow
			? MapEffectByte("RainbowBreathing")
			: MapEffectByte(selected.EffectKey);
		byte brightness = ToProtocolBrightness(selected.Brightness);
		byte speed = selected.HasSpeed ? ToProtocolSpeed(selected.Speed) : (byte)3;
		return (color.R, color.G, color.B, effectByte, brightness, speed);
	}

	// ── Commands ──────────────────────────────────────────────────────────────
	// Re-applies a device's PERSISTED RGB (if enabled) to the hardware, without touching the
	// current UI device. Called when a device (re)connects so a previously-lit LED comes back
	// instead of staying dark after a reconnect. No-op if nothing is saved or RGB was turned off.
	// Takes the card because it needs BOTH of its identities: the saved state is read under the
	// stable settings identity, while the write goes to the live transport (routing identity).
	//
	// resetWhenUnset is for the OWNER-CHANGE sweep only. On a plain reconnect "nothing stored" means
	// leave the board alone — a never-configured device keeps its factory lighting, which is the
	// long-standing behaviour. On an account switch it means the opposite: the new account has never
	// configured this device, so the board must not be left wearing the previous account's colours,
	// and the app's defaults are pushed instead.
	public Task ReapplyPersistedRgbAsync(DeviceCardViewModel card, bool resetWhenUnset = false)
	{
		ArgumentNullException.ThrowIfNull(card);

		if (_usbHidDeviceService == null || _localStorage == null || _ownerProvider == null)
			return Task.CompletedTask;

		var owner = _ownerProvider.GetCurrent();
		if (owner is null) return Task.CompletedTask;

		var record = _localStorage.GetRgbState(
			owner.Value.Kind, owner.Value.Id, card.SettingsVendorId, card.SettingsProductId, card.SettingsDeviceKey);
		if (record is null)
			return resetWhenUnset ? WriteDefaultRgbToDeviceAsync(card) : Task.CompletedTask;
		// An account that turned lighting OFF on this device is as much "these are not my colours"
		// as one that never configured it: on an account switch the board must go dark rather than
		// keep burning the previous account's lighting. On a plain reconnect it is left alone, same
		// as the branch above — the device powered on however it powered on, and off is not a state
		// worth a write nobody asked for.
		if (!record.IsEnabled)
			return resetWhenUnset ? WriteRgbOffToDeviceAsync(card) : Task.CompletedTask;

		var effect = record.Effects.FirstOrDefault(e => e.EffectKey == record.SelectedEffect)
					 ?? record.Effects.FirstOrDefault();
		if (effect is null)
			return Task.CompletedTask;

		Color.TryParse(effect.Hex, out var color);
		var isRainbow = effect.IsRainbow && record.SelectedEffect == "Breathing";
		byte effectByte = isRainbow ? MapEffectByte("RainbowBreathing") : MapEffectByte(record.SelectedEffect);
		byte brightness = ToProtocolBrightness(effect.Brightness);
		byte speed = ToProtocolSpeed(effect.Speed);

		return _usbHidDeviceService.SetDeviceLedColorAsync(
			card.VendorId, card.ProductId, card.DeviceKey ?? string.Empty, card.DeviceTypeLabel ?? string.Empty,
			color.R, color.G, color.B, effectByte, brightness, speed);
	}

	// The device's out-of-box lighting, pushed to a card that is NOT the one open — so everything is
	// resolved from the card's own catalog defaults, never from Device or RgbEffects (this view model
	// is a singleton bound to whichever device the user has open; see ReapplyPersistedRgbAsync).
	// Mirrors what ApplyRgbDefaults shows on the page for the same device.
	private Task WriteDefaultRgbToDeviceAsync(DeviceCardViewModel card)
	{
		if (_usbHidDeviceService is null) return Task.CompletedTask;

		var defaults = card.Defaults;
		if (defaults.RgbEnabled == false)
			return WriteRgbOffToDeviceAsync(card);

		// Fall back rather than take the parse failure's default(Color): a malformed catalog hex
		// would otherwise send pure black with the effect still running, which reads as broken
		// hardware. ApplyRgbDefaults passes the hex through as a string and has no such failure
		// mode, so falling back keeps the page and the board showing the same thing.
		if (!Color.TryParse(defaults.RgbColorHex ?? FallbackRgbColorHex, out var color))
			Color.TryParse(FallbackRgbColorHex, out color);
		var brightness = ToProtocolBrightness(defaults.RgbBrightness ?? FallbackRgbBrightness);
		var speed = ToProtocolSpeed(defaults.RgbSpeed ?? FallbackRgbSpeed);

		// The effect must be one this device can render: a keyboard-only key (Static / ColorCycle) is
		// filtered out of a mouse's list on the page, so falling back the same way here keeps the two
		// agreeing. Breathing is the first effect every device offers.
		var isKeyboard = card.DeviceTypeLabel?.Contains("keyboard", StringComparison.OrdinalIgnoreCase) == true;
		var effectKey = defaults.RgbEffect;
		if (string.IsNullOrEmpty(effectKey) || (!isKeyboard && KeyboardOnlyEffectKeys.Contains(effectKey)))
			effectKey = isKeyboard ? "Static" : "Breathing";

		return _usbHidDeviceService.SetDeviceLedColorAsync(
			card.VendorId, card.ProductId, card.DeviceKey ?? string.Empty, card.DeviceTypeLabel ?? string.Empty,
			color.R, color.G, color.B, MapEffectByte(effectKey), brightness, speed);
	}

	// Lighting off on a card that is NOT the one open. Same encoding BuildLedConfig uses for the
	// page's own off state — effect 0xED, brightness 0 — kept in one place so the "off" the sweep
	// pushes and the "off" the user toggles cannot drift apart.
	private Task WriteRgbOffToDeviceAsync(DeviceCardViewModel card)
	{
		if (_usbHidDeviceService is null) return Task.CompletedTask;

		return _usbHidDeviceService.SetDeviceLedColorAsync(
			card.VendorId, card.ProductId, card.DeviceKey ?? string.Empty, card.DeviceTypeLabel ?? string.Empty,
			0, 0, 0, 0xED, 0, 3);
	}

	[RelayCommand]
	private Task ApplyRgbPresetAsync(string preset)
	{
		if (preset != "Random")
			return Task.CompletedTask;

		// Pick only from the effects available on THIS device (VisibleRgbEffects), not the full
		// catalog — otherwise a mouse could land on a keyboard-only effect (Static/Wave).
		var effects = VisibleRgbEffects;
		if (effects.Count == 0)
			return Task.CompletedTask;

		_suppressAutoApply = true;
		try
		{
			var selected = effects[_rng.Next(effects.Count)];
			SelectEffect(selected);

			if (selected.HasColorPicker)
			{
				var colors = RgbEffectSettingsViewModel.DefaultPresetColors;
				var randomColor = colors[_rng.Next(colors.Count)];
				selected.SelectPresetColorCommand.Execute(randomColor);
				selected.ApplyPickerCommand.Execute(null);
			}

			selected.Brightness = 60 + _rng.Next(41);
			if (selected.HasSpeed)
				selected.Speed = 30 + _rng.Next(51);
		}
		finally
		{
			_suppressAutoApply = false;
		}

		StageRgbSettings();
		return Task.CompletedTask;
	}

	// Sweep direction for the Wave gradient. A keyboard's Wave travels along the key rows,
	// so it has to run left-to-right; the mouse artwork is viewed nose-to-tail and keeps the
	// original top-to-bottom sweep. Neon does not use these — its physical effect sweeps
	// down the board on every device, so its timer pins a vertical axis of its own.
	private RelativePoint GlowGradientStart => IsKeyboardDevice
		? new RelativePoint(0, 0.5, RelativeUnit.Relative)
		: new RelativePoint(0.5, 0, RelativeUnit.Relative);

	private RelativePoint GlowGradientEnd => IsKeyboardDevice
		? new RelativePoint(1, 0.5, RelativeUnit.Relative)
		: new RelativePoint(0.5, 1, RelativeUnit.Relative);

	// ── Glow brush ────────────────────────────────────────────────────────────

	/// <summary>
	/// Single write point for <see cref="AnimatedGlowBrush"/>. The lighting toggle is
	/// authoritative: while it is off the preview stays dark no matter what asks to paint.
	/// Without this the animation ticks wrote the brush directly, so a colour or brightness
	/// change could light the keyboard back up with lighting switched off.
	/// </summary>
	private void SetGlow(IBrush brush) =>
		AnimatedGlowBrush = IsRgbEnabled ? brush : Brushes.Transparent;

	private void UpdateGlowBrush()
	{
		if (!IsRgbEnabled) { SetGlow(Brushes.Transparent); return; }
		var effect = _selectedEffect ?? ActiveRgbEffect;
		var fillHex = WithBrightness(effect.SelectedHexColor, effect.Brightness / 100.0);
		SetGlow(Color.TryParse(fillHex, out var c) ? new SolidColorBrush(c) : Brushes.Transparent);
	}

	// ── Animation timers ──────────────────────────────────────────────────────
	private void StartEffectAnimation(RgbEffectSettingsViewModel? effect)
	{
		// A device with no lighting has nothing to animate. Without this the 33 ms glow timers ran
		// for every device — including the Cosmo headset, which has no RGB at all — burning UI-thread
		// ticks to paint a glow behind artwork that should not have one. Stop rather than skip: the
		// VM is a singleton reused across devices, so a timer started for the previous device would
		// otherwise keep running against this one.
		// A decorative-glow device runs the travelling gradient and nothing else: there is no
		// selected effect to consult (no RGB section exists for it), so the Wave animation is
		// started directly and never varies.
		if (!IsRgbSupported)
		{
			StopSolidHueCycleTimer();
			StopAnimationTimer();

			if (HasDecorativeGlow)
			{
				StartRainbowGradientTimer();
				return;
			}

			UpdateGlowBrush();
			return;
		}

		// Breathing with the gradient swatch selected animates like the old RainbowBreathing.
		if (effect is { EffectKey: "Breathing", IsRainbow: true })
		{
			StartRainbowBreathingTimer();
			return;
		}

		// Neon and ColorCycle previews match the PHYSICAL effects, not their names' first
		// guess (QA on a live Rampage, corroborated by the vendor's mode table — see
		// docs/knowledge-base/smart-infinite-protocol/backlight-modes.md): the boards'
		// Neon (mode 2, has a direction/trend) is a flowing rainbow sweeping down the board,
		// while Spectrum (mode 8, no trend and no colour choice) cycles the whole board
		// through the spectrum in unison. The two previews used to be swapped, so ColorCycle
		// also duplicated Wave's animation. Keyboards only: on a mouse Neon is a single LED
		// (see EasenMouseService), for which a palette sweep IS a solid hue cycle, so it keeps
		// that preview (PR #101 m2). ColorCycle is not offered to mice at all — the mouse's
		// protocol-0x04 effect is Wave, which has its own gradient preview below.
		switch (effect?.EffectKey)
		{
			case "Breathing":        StartBreathingTimer();        break;
			case "RainbowBreathing": StartRainbowBreathingTimer(); break;
			case "Neon":
				if (IsKeyboardDevice) StartRainbowGradientTimer();
				else StartSolidHueCycleTimer();
				break;
			// Keyboard-only (see KeyboardOnlyEffectKeys): the spectrum moving in unison, which a
			// solid hue cycle previews exactly.
			case "ColorCycle":       StartSolidHueCycleTimer();    break;
			// Same visual as keyboard Neon today, but kept on its own timer so it can diverge.
			case "Wave":             StartWaveTimer();             break;
			default: UpdateGlowBrush(); break;
		}
	}

	private void StartBreathingTimer()
	{
		_animationPhase = -Math.PI / 2;
		_animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
		_animationTimer.Tick += (_, _) =>
		{
			var e = _selectedEffect ?? ActiveRgbEffect;
			var period = Math.Max(400, 5000 - 46 * e.Speed);
			_animationPhase += Math.PI * 2 * 33 / period;
			var t = 0.5 + 0.5 * Math.Sin(_animationPhase);
			var hex = WithBrightness(e.SelectedHexColor, (0.05 + 0.95 * t) * (e.Brightness / 100.0));
			SetGlow(Color.TryParse(hex, out var c) ? new SolidColorBrush(c) : Brushes.Transparent);
		};
		_animationTimer.Start();
	}

	private void StartRainbowBreathingTimer()
	{
		_animationPhase = 0;
		_hueCycleAngle = 0;
		_animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
		_animationTimer.Tick += (_, _) =>
		{
			var e = _selectedEffect ?? ActiveRgbEffect;
			var period = Math.Max(400, 5000 - 46 * e.Speed);
			_animationPhase += Math.PI * 2 * 33 / period;

			// Advance hue by 60° each breath (6 hues: red → yellow → green → cyan → blue → magenta → red …)
			_hueCycleAngle = ((int)(_animationPhase / (Math.PI * 2)) * 60.0) % 360;

			// t: 0 = dark, 1 = bright; starts dark so colour appears gradually
			var t = 0.5 - 0.5 * Math.Cos(_animationPhase);
			var hex = HsvToHex(_hueCycleAngle, 1.0, (0.05 + 0.95 * t) * (e.Brightness / 100.0));
			SetGlow(Color.TryParse(hex, out var c) ? new SolidColorBrush(c) : Brushes.Transparent);
		};
		_animationTimer.Start();
	}

	private void StartSolidHueCycleTimer()
	{
		_hueCycleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
		_hueCycleTimer.Tick += (_, _) =>
		{
			var e = _selectedEffect ?? ActiveRgbEffect;
			// Step scaled to 33 ms so the visible speed matches the 150 ms origin (×0.22)
			_hueCycleAngle = (_hueCycleAngle + (0.5 + e.Speed * 0.09) * 0.22) % 360;
			var hex = HsvToHex(_hueCycleAngle, 1.0, e.Brightness / 100.0);
			SetGlow(Color.TryParse(hex, out var c) ? new SolidColorBrush(c) : Brushes.Transparent);
		};
		_hueCycleTimer.Start();
	}

	private void StopSolidHueCycleTimer()
	{
		_hueCycleTimer?.Stop();
		_hueCycleTimer = null;
	}

	private void StartRainbowGradientTimer()
	{
		_animationPhase = 0;
		_animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
		_animationTimer.Tick += (_, _) =>
		{
			var e = _selectedEffect ?? ActiveRgbEffect;
			_animationPhase = (_animationPhase + 0.5 + e.Speed * 0.09) % 360;
			var v = e.Brightness / 100.0;

			// 7 stops spanning a full 360° hue cycle — first and last share the same hue
			// so the rainbow tiles seamlessly as the phase advances each frame.
			//
			// The phase is SUBTRACTED, not added: a given hue sits where
			// `phase - i*step == hue`, i.e. at `i = (phase - hue)/step`, so a rising phase
			// pushes it toward higher stops — left to right, matching the hardware. Adding
			// it runs the rainbow backwards.
			const int segments = 6;
			var stops = new GradientStops();
			for (int i = 0; i <= segments; i++)
			{
				var hue = ((_animationPhase - i * 360.0 / segments) % 360 + 360) % 360;
				var hex = HsvToHex(hue, 1.0, v);
				if (Color.TryParse(hex, out var c))
					stops.Add(new GradientStop(c, (double)i / segments));
			}
			// The physical Neon sweeps DOWN the board (QA on a live Rampage), not along the
			// key rows like Wave — so this gradient is pinned top-to-bottom on every device
			// rather than taking the shared keyboard-horizontal GlowGradientStart/End.
			SetGlow(new LinearGradientBrush
			{
				StartPoint    = new RelativePoint(0.5, 0, RelativeUnit.Relative),
				EndPoint      = new RelativePoint(0.5, 1, RelativeUnit.Relative),
				GradientStops = stops,
			});
		};
		_animationTimer.Start();
	}

	// Near-copy of StartRainbowGradientTimer (Neon), differing in sweep axis: Wave travels
	// along the key rows (GlowGradientStart/End — horizontal on keyboards), Neon down the
	// board. Two methods rather than a parameter so each hardware effect has its own place
	// to diverge in.
	private void StartWaveTimer()
	{
		_animationPhase = 0;
		_animationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
		_animationTimer.Tick += (_, _) =>
		{
			var e = _selectedEffect ?? ActiveRgbEffect;
			_animationPhase = (_animationPhase + 0.5 + e.Speed * 0.09) % 360;
			// Full brightness when the effect offers no slider on this device — the mouse's Wave is
			// the case (the firmware ignores the level, see RgbEffectSettingsViewModel.ApplyDeviceKind).
			// Dimming the preview by a level the hardware discards is exactly the mismatch that made
			// the preview and the mouse disagree.
			var v = e.HasBrightness ? e.Brightness / 100.0 : 1.0;

			// 7 stops spanning a full 360° hue cycle — first and last share the same hue
			// so the rainbow tiles seamlessly as the phase advances each frame.
			//
			// The phase is SUBTRACTED, not added: a given hue sits where
			// `phase - i*step == hue`, i.e. at `i = (phase - hue)/step`, so a rising phase
			// pushes it toward higher stops — left to right, matching the hardware. Adding
			// it runs the rainbow backwards.
			const int segments = 6;
			var stops = new GradientStops();
			for (int i = 0; i <= segments; i++)
			{
				var hue = ((_animationPhase - i * 360.0 / segments) % 360 + 360) % 360;
				var hex = HsvToHex(hue, 1.0, v);
				if (Color.TryParse(hex, out var c))
					stops.Add(new GradientStop(c, (double)i / segments));
			}
			SetGlow(new LinearGradientBrush
			{
				StartPoint    = GlowGradientStart,
				EndPoint      = GlowGradientEnd,
				GradientStops = stops,
			});
		};
		_animationTimer.Start();
	}

	private void StopAnimationTimer()
	{
		_animationTimer?.Stop();
		_animationTimer = null;
		UpdateGlowBrush();
	}

	// ── Tray pause/resume (audit C2) ───────────────────────────────────────
	// Hiding the window to the tray does not change the Device Details view's IsVisible, so the
	// glow timers would keep repainting a brush nobody can see 30 times a second. App's tray
	// handlers bracket Hide()/Show() with this pair; both run on the UI thread.
	internal void PauseGlowAnimation()
	{
		StopSolidHueCycleTimer();
		StopAnimationTimer();
	}

	// No-op unless the page still shows a device with lighting on — restoring from the tray
	// while on Home is the common case — and when a glow timer is already live (a restore whose
	// hide never paused), so a double-resume can never stack timers.
	internal void ResumeGlowAnimation()
	{
		if (Device is null || !IsRgbEnabled) return;
		if (_hueCycleTimer is not null || _animationTimer is not null) return;
		StartEffectAnimation(_selectedEffect);
	}

	// Test seam (the file's *ForTests convention): whether any glow timer is armed.
	internal bool HasActiveGlowTimersForTests => _animationTimer is not null || _hueCycleTimer is not null;

	// ── Color / protocol helpers ──────────────────────────────────────────────
	// The preview must show the PICKED colour, only dimmer — a plain linear scale of the
	// channels (HSV value scaling with hue and saturation untouched). An earlier version
	// round-tripped through HSV with a +0.3 saturation boost to make the glow punchier, which
	// falsified every colour and mangled achromatic ones outright: white has no hue, so the
	// boost re-saturated it towards red and the board rendered brown (PG-381).
	private static string WithBrightness(string hexColor, double brightness)
	{
		if (!Color.TryParse(hexColor, out var c)) return hexColor;
		var k = Math.Clamp(brightness, 0, 1);
		return $"#{(int)Math.Round(c.R * k):X2}{(int)Math.Round(c.G * k):X2}{(int)Math.Round(c.B * k):X2}";
	}

	private static string HsvToHex(double h, double s, double v)
	{
		var hi = (int)(h / 60) % 6;
		var f  = h / 60 - Math.Floor(h / 60);
		var p  = v * (1 - s);
		var q  = v * (1 - f * s);
		var t  = v * (1 - (1 - f) * s);
		var (r, g, b) = hi switch
		{
			0 => (v, t, p),
			1 => (q, v, p),
			2 => (p, v, t),
			3 => (p, q, v),
			4 => (t, p, v),
			_ => (v, p, q)
		};
		return $"#{(int)(r * 255):X2}{(int)(g * 255):X2}{(int)(b * 255):X2}";
	}

	// App effect key → protocol byte
	private static byte MapEffectByte(string effectKey) => effectKey switch
	{
		"Breathing"        => 0x01,
		"RainbowBreathing" => 0x00,
		"Neon"             => 0x02,
		"ColorCycle"       => 0x03,
		"Static"           => 0x04,
		"Wave"             => 0x05,
		_                  => 0x01,
	};

	// Brightness: 0-100% → 0x00-0xFF
	private static byte ToProtocolBrightness(double percent) =>
		(byte)Math.Clamp((int)Math.Round(Math.Clamp(percent, 0, 100) / 100.0 * 255), 0, 255);

	// Speed: 0-100% → 1-7
	private static byte ToProtocolSpeed(double percent) =>
		(byte)Math.Clamp((int)Math.Round(1 + Math.Clamp(percent, 0, 100) / 100.0 * 6), 1, 7);

	// Persist the current RGB UI state AND stage it into the active preset. The two halves are
	// split because they are not always wanted together: staging marks the preset dirty and
	// schedules a device write, which is right for a user edit but wrong for the housekeeping
	// callers that only need the state written down (see PersistRgbStateForCurrentDevice).
	private void SaveRgbStateForCurrentDevice()
	{
		if (Device is null) return;

		PersistRgbStateForCurrentDevice();

		// Led only: an RGB edit has no business re-committing the key matrix or the DPI table to the
		// device's flash. See DeviceApplyAspects.
		StageChangeAndApply(DeviceApplyAspects.Led);
	}

	// Snapshot the current RGB UI state to the settings store (goes through the owner-scoped
	// SettingsRepository). Also caches an in-session copy so switching between opened
	// devices restores state instantly. No staging, no device write.
	//
	// device_rgb is what ReapplyPersistedRgbAsync reads to restore a board's lighting after a
	// reconnect, and what SavePresetSnapshot copies into a preset — so it must be written whenever
	// the App resolves a device's RGB state, not only when the user edits it. Before this existed,
	// the only thing populating it for an untouched keyboard was a side effect of the RGB push
	// path, which is why removing that side effect (to break the apply→stage→apply loop) left a
	// board the App had lit with nothing persisted.
	private void PersistRgbStateForCurrentDevice()
	{
		// Fire-and-forget enqueue: SaveRgbAsync routes through the storage executor, and queue
		// FIFO guarantees a SavePresetSnapshot enqueued after this call observes the rows.
		_ = BuildRgbPersistWrite()?.Invoke();
	}

	// SetDeviceAsync's variant: same snapshot + cache update on the calling thread, but the SQLite
	// write runs off it. Returns the WRITE-COMPLETION task (the executor task behind SaveRgbAsync,
	// not an enqueue acknowledgement): awaiting it guarantees the device_rgb rows are in place —
	// the seed-branch of LoadPresetsForDeviceAsync relies on that as its rows-in-place barrier.
	private Task PersistRgbStateForCurrentDeviceAsync()
	{
		return BuildRgbPersistWrite() is { } write ? write() : Task.CompletedTask;
	}

	// Sync half of the RGB persist: snapshots the UI state, updates the in-session cache, and
	// returns the storage write as a deferred call (null when there is nothing to write).
	// The write is queue-backed: SaveRgbAsync enqueues on the storage executor and its task
	// completes when the rows are on disk. Callers that snapshot straight afterwards
	// (PersistDisplayedDeviceState) rely on queue FIFO — the snapshot is enqueued after this
	// write — not on synchronous completion, which no longer exists.
	private Func<Task>? BuildRgbPersistWrite()
	{
		if (Device is null) return null;
		if (!IsDeviceConfigurable) return null;

		// A snapshot describes the account whose state the page is displaying. When the owner has moved
		// on since — a sign-out or a session expiry leaves the page open on the departing account's
		// card — this snapshot is that account's, and neither the storage rows nor the in-session cache
		// of the arriving one may receive it. Both halves are refused here rather than in the returned
		// write, because the cache entry below is filed on THIS call: by the time the write runs the
		// wrong owner would already own a cached copy of another account's colours.
		var currentOwner = _ownerProvider?.GetCurrent();
		if (_displayedRgbOwner is { } displayedOwner && !Nullable.Equals(displayedOwner, currentOwner))
			return null;

		var state = new RgbDeviceState
		{
			IsEnabled = _isRgbEnabled,
			SelectedEffectKey = ActiveRgbEffect.EffectKey,
		};
		foreach (var e in RgbEffects)
			state.Effects[e.EffectKey] = new PerEffectState(e.SelectedHexColor, e.Brightness, e.Speed, e.IsRainbow);

		// Cached under the CURRENT owner only. Without one the snapshot is dropped rather than cached
		// under a sentinel a later account could read — the storage write below is skipped for the
		// same reason (SettingsRepository.SaveRgbAsync returns early with no owner).
		if (currentOwner is { } cacheOwner)
			_rgbStateByDevice[RgbCacheKey(cacheOwner, Device)] = state;

		// Persist so the chosen effect + colours survive an App restart (loaded in LoadRgbStateForCurrentDeviceAsync).
		if (_settings == null) return null;

		var effects = new Dictionary<string, PerEffectSettings>(RgbEffects.Count);
		foreach (var e in RgbEffects)
			effects[e.EffectKey] = new PerEffectSettings(e.SelectedHexColor, e.Brightness, e.Speed, e.IsRainbow);

		var payload = new RgbDeviceSettings(_isRgbEnabled, ActiveRgbEffect.EffectKey, effects);
		var (vid, pid, key) = (Device.SettingsVendorId, Device.SettingsProductId, Device.SettingsDeviceKey);

		// The payload is snapshotted HERE but the owner it lands under is resolved by
		// SettingsRepository.SaveRgbAsync when the write actually runs — so a write held and invoked
		// after a switch would file this account's colours in the arriving account's rows. The
		// displayed-owner check above is what catches the flush shapes the app actually has (both
		// callers invoke immediately, on this thread); this covers a caller that genuinely defers.
		return () => Nullable.Equals(currentOwner, _ownerProvider?.GetCurrent())
			? _settings.SaveRgbAsync(vid, pid, key, payload)
			: Task.CompletedTask;
	}

	// SetDeviceAsync's variant — resolves the device's RGB state (in-session cache → persisted →
	// defaults) with the SQLite read off the calling thread, then applies it. The suppressed
	// region is fully synchronous: suppression spanning an await would let a user edit made
	// during the gap skip its device write. Generation, not just ReferenceEquals: see
	// LoadMacroLibraryAsync.
	private async Task LoadRgbStateForCurrentDeviceAsync(int generation)
	{
		var device = Device;
		if (device is null) return;

		// Cancel any pending debounced RGB write from the previous device's edits BEFORE the first
		// await — it applies to whatever Device points at when it fires, which is already this one.
		_sliderDebounceCts?.Cancel();
		StopSolidHueCycleTimer();
		StopAnimationTimer();

		// Owner resolved on the calling thread and BEFORE the cache probe — the entry is per
		// (owner, device), so this open can only ever see its own account's state. The background
		// block touches storage only.
		var owner = _ownerProvider?.GetCurrent();
		var state = GetCachedRgbState(owner, device);
		if (state is null)
		{
			var record = owner is null || _localStorage is null
				? null
				: await Task.Run(() => _localStorage.GetRgbState(
					owner.Value.Kind, owner.Value.Id,
					device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey));

			// Another open may have taken over while the read was in flight.
			if (generation != _setDeviceGeneration || !ReferenceEquals(Device, device)) return;

			// So may an account switch — and the record belongs to the owner resolved above, so
			// caching or showing it now would put the previous account's lighting on the new one's
			// page. The transition re-drives the page itself (RestoreConnectedDevicesForNewOwner).
			if (!Nullable.Equals(owner, _ownerProvider?.GetCurrent())) return;

			if (record is not null && owner is { } readOwner)
			{
				state = ToRgbDeviceState(record);
				_rgbStateByDevice[RgbCacheKey(readOwner, device)] = state;
			}
		}

		ApplyResolvedRgbState(owner, state);
	}

	// The preset apply's variant: same resolution (in-session cache → persisted → defaults), run
	// inline on the calling thread. The apply targets one captured device and its callers push to
	// the hardware right after, so it must complete synchronously — which is also what the test
	// suite's command-driven flows rely on.
	private void LoadRgbStateForCurrentDevice()
	{
		var device = Device;
		if (device is null) return;

		_sliderDebounceCts?.Cancel();
		StopSolidHueCycleTimer();
		StopAnimationTimer();

		// Owner before the probe, for the same reason as the async variant above.
		var owner = _ownerProvider?.GetCurrent();
		var state = GetCachedRgbState(owner, device);
		if (state is null)
		{
			var record = owner is null || _localStorage is null
				? null
				: _localStorage.GetRgbState(
					owner.Value.Kind, owner.Value.Id,
					device.SettingsVendorId, device.SettingsProductId, device.SettingsDeviceKey);

			if (record is not null && owner is { } readOwner)
			{
				state = ToRgbDeviceState(record);
				_rgbStateByDevice[RgbCacheKey(readOwner, device)] = state;
			}
		}

		ApplyResolvedRgbState(owner, state);
	}

	// Restoring values would otherwise fire debounced RGB writes back to the device, hence the
	// suppression; the whole region is synchronous — suppression spanning an await would let a
	// user edit made during the gap skip its device write.
	private void ApplyResolvedRgbState(SettingsOwner? owner, RgbDeviceState? state)
	{
		// The page starts displaying THIS account's colours here — including the defaults branch, which
		// is as much "the state resolved for this account" as a stored record is. Every later persist
		// snapshots what this applied, so it may only ever land under this owner (BuildRgbPersistWrite).
		_displayedRgbOwner = owner;

		_suppressAutoApply = true;
		try
		{
			if (state is not null)
				ApplyRgbState(state);
			else
				ApplyRgbDefaults();

			OnPropertyChanged(nameof(ActiveRgbEffect));
			OnPropertyChanged(nameof(IsRgbEnabled));

			if (_isRgbEnabled)
				StartEffectAnimation(_selectedEffect);
			else
				UpdateGlowBrush();
		}
		finally
		{
			_suppressAutoApply = false;
		}
	}

	// Builds an in-session RGB state from the persisted record.
	private static RgbDeviceState ToRgbDeviceState(RgbStateRecord record)
	{
		var state = new RgbDeviceState
		{
			IsEnabled = record.IsEnabled,
			SelectedEffectKey = record.SelectedEffect,
		};
		foreach (var e in record.Effects)
			state.Effects[e.EffectKey] = new PerEffectState(e.Hex, e.Brightness, e.Speed, e.IsRainbow);
		return state;
	}

	private void ApplyRgbState(RgbDeviceState state)
	{
		_isRgbEnabled = state.IsEnabled;

		// Resolve the saved selection ONCE, into the key this device actually presents. Testing each
		// effect against a legacy alias instead let TWO effects match a legacy record — the renamed
		// one and the one still carrying the old name — which left ActiveRgbEffect (first match in
		// catalog order) and _selectedEffect (last match) pointing at different effects, and made
		// every save re-persist the legacy key so the migration never finished.
		var selectedKey = PresentedEffectKey(state.SelectedEffectKey);

		// Cleared first so the selection is decided entirely by THIS device's state: the field is
		// reused across devices, and the fallback below only fires when nothing matched.
		_selectedEffect = null;

		foreach (var e in RgbEffects)
		{
			if (state.Effects.TryGetValue(e.EffectKey, out var pe) ||
				(LegacyEffectKey(e.EffectKey) is { } legacy && state.Effects.TryGetValue(legacy, out pe)))
			{
				e.SelectedHexColor = pe.Hex;
				e.Brightness       = pe.Brightness;
				e.Speed            = pe.Speed;
				e.IsRainbow        = pe.IsRainbow;
			}
			e.IsSelected = e.EffectKey == selectedKey;
			if (e.IsSelected) _selectedEffect = e;
		}

		_selectedEffect ??= RgbEffects[0];
	}

	// A mouse presented the firmware's protocol-0x04 effect as "ColorCycle" until 2026-08-17; it is
	// "Wave" now, and mice no longer offer a ColorCycle at all. Keyboards are untouched — there
	// ColorCycle is a real, separate effect.
	private const string LegacyMouseWaveKey = "ColorCycle";

	// Translates a key read from STORAGE into the one this device presents, so a BLAZE that had the
	// effect selected comes back on Wave instead of on an effect it does not offer.
	private string PresentedEffectKey(string savedKey) =>
		!IsKeyboardDevice && savedKey == LegacyMouseWaveKey ? "Wave" : savedKey;

	// The key an effect's per-effect settings may have been persisted under before the rename, or
	// null when it has no legacy name — so the ordinary lookup is not repeated for every effect.
	private string? LegacyEffectKey(string effectKey) =>
		!IsKeyboardDevice && effectKey == "Wave" ? LegacyMouseWaveKey : null;

	// The device's out-of-box lighting, from its catalog config (see docs/device-defaults.md).
	// A device that declares no lighting default keeps the built-in values below, which are what
	// this method always used.
	private const string FallbackRgbColorHex = "#7620EE";
	private const double FallbackRgbBrightness = 60;
	private const double FallbackRgbSpeed = 50;

	private void ApplyRgbDefaults()
	{
		var defaults = CurrentDefaults;
		_isRgbEnabled = defaults.RgbEnabled ?? true;

		foreach (var e in RgbEffects)
		{
			e.SelectedHexColor = defaults.RgbColorHex ?? FallbackRgbColorHex;
			e.Brightness       = defaults.RgbBrightness ?? FallbackRgbBrightness;
			e.Speed            = defaults.RgbSpeed ?? FallbackRgbSpeed;
			e.IsSelected       = false;
			e.IsRainbow        = false;
		}

		// The configured effect must be one this device can actually render — VisibleRgbEffects
		// filters Static/Wave out for mice, so a mis-configured key would leave nothing selected.
		// Falling back to the first visible effect keeps the section usable either way.
		var defaultEffect =
			VisibleRgbEffects.FirstOrDefault(e => e.EffectKey == defaults.RgbEffect)
			?? VisibleRgbEffects.FirstOrDefault()
			?? RgbEffects[0];
		defaultEffect.IsSelected = true;
		_selectedEffect = defaultEffect;
	}
}
