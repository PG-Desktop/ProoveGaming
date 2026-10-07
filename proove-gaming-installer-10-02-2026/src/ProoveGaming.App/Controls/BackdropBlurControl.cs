using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ProoveGaming.App.Controls;

// Approximates a backdrop blur — which Avalonia has no native support for — for the app's static
// window wallpaper. It redraws the exact slice of the wallpaper that sits behind this control and
// blurs it, so the result stays ALIGNED with the real background instead of looking like a shifted
// copy: the slice is computed from this control's live position within the window every time it
// moves or resizes.
//
// The wallpaper is the same cached bitmap the window background uses (no extra file load), painted
// Stretch=Fill over the window — so the region behind this control maps to a proportional source
// rectangle of the bitmap. Place the control behind a translucent panel and inside a clipping
// parent; the blur's soft edge then falls under the panel's frame.
public class BackdropBlurControl : Control
{
    // Same asset and layout the real window background uses, so the redrawn slice matches it pixel
    // for pixel. Stretch=Fill maps the whole bitmap across the whole window.
    private static readonly Uri WallpaperUri = new("avares://ProoveGaming.Common/Assets/background.jpg");
    private static Bitmap? _wallpaper;
    private static bool _wallpaperLoadFailed;

    public static readonly StyledProperty<double> BlurRadiusProperty =
        AvaloniaProperty.Register<BackdropBlurControl, double>(nameof(BlurRadius), 24);

    public double BlurRadius
    {
        get => GetValue(BlurRadiusProperty);
        set => SetValue(BlurRadiusProperty, value);
    }

    private Point _lastOrigin = new(double.NaN, double.NaN);
    private Size _lastWindowSize;
    private Size _lastBounds;

    static BackdropBlurControl()
    {
        AffectsRender<BackdropBlurControl>(BlurRadiusProperty);
    }

    public BackdropBlurControl()
    {
        // The control never handles input — it is pure decoration behind the panel.
        IsHitTestVisible = false;
        // Re-slice whenever the control's position or the window size changes (window resize,
        // switching device, changing section). LayoutUpdated is cheap here because Render only runs
        // when the guarded values below actually differ.
        LayoutUpdated += (_, _) => InvalidateIfMoved();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Effect = new BlurEffect { Radius = BlurRadius };
        _lastOrigin = new Point(double.NaN, double.NaN);
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BlurRadiusProperty)
        {
            Effect = new BlurEffect { Radius = BlurRadius };
        }
    }

    public override void Render(DrawingContext context)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        var wallpaper = GetWallpaper();
        if (topLevel is null || wallpaper is null)
        {
            return;
        }

        var windowSize = topLevel.ClientSize;
        var bounds = Bounds.Size;
        if (windowSize.Width <= 0 || windowSize.Height <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        // This control's top-left in window coordinates — the anchor that keeps the slice aligned.
        var origin = this.TranslatePoint(default, topLevel) ?? default;

        _lastOrigin = origin;
        _lastWindowSize = windowSize;
        _lastBounds = bounds;

        // The window background is Stretch=Fill, so window space maps linearly onto bitmap pixels.
        var pixelSize = wallpaper.PixelSize;
        var sourceRect = new Rect(
            origin.X / windowSize.Width * pixelSize.Width,
            origin.Y / windowSize.Height * pixelSize.Height,
            bounds.Width / windowSize.Width * pixelSize.Width,
            bounds.Height / windowSize.Height * pixelSize.Height);

        context.DrawImage(wallpaper, sourceRect, new Rect(0, 0, bounds.Width, bounds.Height));
    }

    private void InvalidateIfMoved()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return;
        }

        var origin = this.TranslatePoint(default, topLevel) ?? default;
        if (origin != _lastOrigin || topLevel.ClientSize != _lastWindowSize || Bounds.Size != _lastBounds)
        {
            InvalidateVisual();
        }
    }

    private static Bitmap? GetWallpaper()
    {
        if (_wallpaper is not null || _wallpaperLoadFailed)
        {
            return _wallpaper;
        }

        try
        {
            using var stream = AssetLoader.Open(WallpaperUri);
            _wallpaper = new Bitmap(stream);
        }
        catch
        {
            // No wallpaper asset — degrade to no backdrop rather than throwing during render.
            _wallpaperLoadFailed = true;
        }

        return _wallpaper;
    }
}
