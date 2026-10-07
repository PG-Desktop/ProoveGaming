using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using ProoveGaming.App.Models;

namespace ProoveGaming.App.Views.DeviceDetailsSections;

/// <summary>
/// Renders a keyboard from its split artwork: chassis, an unlit black bed, an optional
/// tinted backlight, then the keycaps on top. Takes its data as properties rather than off
/// the DataContext so the same control serves the device-details pages (which bind a
/// <c>DeviceDetailsViewModel</c>) and the home-screen card (which binds a card view model).
/// </summary>
internal partial class RgbKeyboardView : UserControl
{
	/// <summary>Split artwork to draw. Null renders nothing.</summary>
	public static readonly StyledProperty<RgbKeyboardArt?> ArtProperty =
		AvaloniaProperty.Register<RgbKeyboardView, RgbKeyboardArt?>(nameof(Art));

	/// <summary>Backlight fill. Ignored when <see cref="ShowBacklight"/> is false.</summary>
	public static readonly StyledProperty<IBrush?> GlowBrushProperty =
		AvaloniaProperty.Register<RgbKeyboardView, IBrush?>(nameof(GlowBrush));

	/// <summary>
	/// Whether the tinted backlight layer is drawn. False renders the board unlit — the black
	/// bed still shows between the keycaps — which is what Key Assignments and the home card
	/// want: the same keyboard as the RGB section, just not lit.
	/// </summary>
	public static readonly StyledProperty<bool> ShowBacklightProperty =
		AvaloniaProperty.Register<RgbKeyboardView, bool>(nameof(ShowBacklight), defaultValue: true);

	public RgbKeyboardArt? Art
	{
		get => GetValue(ArtProperty);
		set => SetValue(ArtProperty, value);
	}

	public IBrush? GlowBrush
	{
		get => GetValue(GlowBrushProperty);
		set => SetValue(GlowBrushProperty, value);
	}

	public bool ShowBacklight
	{
		get => GetValue(ShowBacklightProperty);
		set => SetValue(ShowBacklightProperty, value);
	}

	/// <summary>
	/// How strongly to fade the board out, 0 (untouched) to 1 (fully blacked out). Drawn as a black
	/// layer over the artwork rather than applied as <see cref="Visual.Opacity"/>.
	/// </summary>
	/// <remarks>
	/// Setting <c>Opacity</c> on this control does <b>not</b> fade it: the layers are
	/// <c>Avalonia.Svg.Skia</c> <c>Svg</c> controls, which paint an <c>SKPicture</c> through their
	/// own draw operation and ignore the opacity of an ancestor. The home card hit this — boards
	/// drawn through this control stayed at full brightness while disconnected, while boards drawn
	/// through the raster <c>SvgImageView</c> faded correctly. Compositing a scrim is equivalent on
	/// a black background (<c>0.3 x art</c> either way) and works regardless of how a layer paints.
	/// </remarks>
	public static readonly StyledProperty<double> DimOpacityProperty =
		AvaloniaProperty.Register<RgbKeyboardView, double>(nameof(DimOpacity), defaultValue: 0d);

	public double DimOpacity
	{
		get => GetValue(DimOpacityProperty);
		set => SetValue(DimOpacityProperty, value);
	}

	public RgbKeyboardView()
	{
		InitializeComponent();
	}

	// The bed / backlight / legend layers are plain Paths, which paint the instant their Data
	// binding resolves — a frame or two before the chassis and keycap Svg layers finish, which
	// on a device open flashed bare glow blocks with no board under them. Hiding just those
	// layers for one dispatcher turn lets the Svg layers land first, and the tint drops in on
	// top of an already-assembled board. Opacity, not IsVisible: three of these layers bind
	// IsVisible to ShowBacklight and a local write would clobber the binding — and unlike the
	// Svg layers (see DimOpacity's remarks) plain Paths do respect Opacity.
	private void HideTintLayersForOneTurn()
	{
		Avalonia.Controls.Shapes.Path[] layers = [UnlitBed, Backlight, LegendHalo, LegendGlyph, LegendUnlit];
		foreach (var layer in layers)
			layer.Opacity = 0;

		Avalonia.Threading.Dispatcher.UIThread.Post(() =>
		{
			foreach (var layer in layers)
				layer.Opacity = 1;
		}, Avalonia.Threading.DispatcherPriority.Background);
	}

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);

		if (change.Property != ArtProperty) return;

		if (change.NewValue is RgbKeyboardArt art)
		{
			// Prewarmed art (the normal case — HomeViewModel loads it when the card appears)
			// assigns synchronously below, so the whole board composes in the same frame and no
			// gating is needed. The tint layers only wait a turn on a prewarm MISS, where the
			// chassis genuinely has to decode first.
			if (art.BaseSource is null)
				HideTintLayersForOneTurn();
			_ = ApplySourcesAsync(art);
		}
		else
		{
			ChassisSvg.SvgSource = null;
			KeycapsSvg.SvgSource = null;
		}
	}

	private async System.Threading.Tasks.Task ApplySourcesAsync(RgbKeyboardArt art)
	{
		// Completed already when the prewarm ran; otherwise loads now, parsing off the UI thread.
		await art.EnsureSourcesAsync();

		// The board may have changed while a missed load was in flight; its own apply owns these.
		if (!ReferenceEquals(Art, art)) return;

		ChassisSvg.SvgSource = art.BaseSource;
		KeycapsSvg.SvgSource = art.KeysSource;
	}
}
