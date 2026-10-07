using System.Collections.Concurrent;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using Svg.Skia;

namespace ProoveGaming.Common.Controls;

public partial class SvgImageView : UserControl
{
	// Rasterizing an SVG costs a parse + Skia render + PNG encode/decode round-trip, and the
	// same icon is used at the same size all over the app — plus every SourceUri/Width/Height
	// assignment on one instance re-triggers a load. Cache the result per (uri, size, tint);
	// Avalonia bitmaps are immutable and safe to share between Image controls.
	private static readonly ConcurrentDictionary<string, Bitmap?> _bitmapCache = new();

	public static readonly StyledProperty<string?> SourceUriProperty =
		AvaloniaProperty.Register<SvgImageView, string?>(nameof(SourceUri));

	public static readonly StyledProperty<Stretch> StretchProperty =
		AvaloniaProperty.Register<SvgImageView, Stretch>(nameof(Stretch), Stretch.Uniform);

	public static readonly StyledProperty<Color?> TintColorProperty =
		AvaloniaProperty.Register<SvgImageView, Color?>(nameof(TintColor));

	static SvgImageView()
	{
		SourceUriProperty.Changed.AddClassHandler<SvgImageView>((x, _) => x.LoadSvg());
		StretchProperty.Changed.AddClassHandler<SvgImageView>((x, _) => x.GetImage().Stretch = x.Stretch);
		TintColorProperty.Changed.AddClassHandler<SvgImageView>((x, _) => x.LoadSvg());
		WidthProperty.Changed.AddClassHandler<SvgImageView>((x, _) => x.LoadSvg());
		HeightProperty.Changed.AddClassHandler<SvgImageView>((x, _) => x.LoadSvg());
	}

	public string? SourceUri
	{
		get => GetValue(SourceUriProperty);
		set => SetValue(SourceUriProperty, value);
	}

	public Stretch Stretch
	{
		get => GetValue(StretchProperty);
		set => SetValue(StretchProperty, value);
	}

	public Color? TintColor
	{
		get => GetValue(TintColorProperty);
		set => SetValue(TintColorProperty, value);
	}

	public SvgImageView()
	{
		InitializeComponent();
		GetImage().Stretch = Stretch;
		LoadSvg();
	}

	private void InitializeComponent()
	{
		AvaloniaXamlLoader.Load(this);
	}

	private void LoadSvg()
	{
		var image = GetImage();
		if (string.IsNullOrWhiteSpace(SourceUri))
		{
			image.Source = null;
			return;
		}

		var cacheKey = string.Create(CultureInfo.InvariantCulture,
			$"{SourceUri}|{Width}|{Height}|{TintColor?.ToUInt32() ?? 0}");
		var sourceUri = SourceUri;
		image.Source = _bitmapCache.GetOrAdd(cacheKey, _ => Rasterize(sourceUri));
	}

	private Bitmap? Rasterize(string sourceUri)
	{
		try
		{
			using var stream = AssetLoader.Open(new Uri(sourceUri));
			var svg = new SKSvg();
			svg.Load(stream);
			if (svg.Picture is null) return null;

			var bounds = svg.Picture.CullRect;
			var svgWidth = bounds.Width;
			var svgHeight = bounds.Height;

			var targetWidth = double.IsNaN(Width) || Width <= 0 ? svgWidth : Width;
			var targetHeight = double.IsNaN(Height) || Height <= 0 ? svgHeight : Height;

			var scaleX = (float)(targetWidth / svgWidth);
			var scaleY = (float)(targetHeight / svgHeight);

			var width = Math.Max(1, (int)Math.Ceiling(targetWidth));
			var height = Math.Max(1, (int)Math.Ceiling(targetHeight));

			using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
			using var canvas = new SKCanvas(bitmap);
			canvas.Clear(SKColors.Transparent);
			canvas.Scale(scaleX, scaleY);
			if (TintColor is { } tint)
			{
				using var paint = new SKPaint
				{
					ColorFilter = SKColorFilter.CreateBlendMode(
						new SKColor(tint.R, tint.G, tint.B, tint.A),
						SKBlendMode.SrcIn)
				};
				canvas.DrawPicture(svg.Picture, paint);
			}
			else
			{
				canvas.DrawPicture(svg.Picture);
			}
			canvas.Flush();

			using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
			if (encoded is null) return null;

			using var ms = encoded.AsStream();
			return new Bitmap(ms);
		}
		catch
		{
			return null;
		}
	}

	private Image GetImage() => this.FindControl<Image>("SvgImage")!;
}
