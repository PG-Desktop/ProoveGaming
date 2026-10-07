using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ProoveGaming.Common.Controls;
using SkiaSharp;
using Svg.Skia;

namespace ProoveGaming.Installer.Views;

internal partial class SplashStepView : UserControl
{
    public event EventHandler? SplashCompleted;

    private const string LogoSvgUri = "avares://ProoveGaming.Installer/Assets/splash-purple-logo.svg";

    public SplashStepView()
    {
        InitializeComponent();
        LoadSvgImage("LogoImage", LogoSvgUri);

        var loadingBar = this.FindControl<AnimatedLoadingBarView>("SplashLoadingBar");
        if (loadingBar is not null)
        {
            loadingBar.StartProgressSequence(TimeSpan.FromSeconds(5), () => SplashCompleted?.Invoke(this, EventArgs.Empty));
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void LoadSvgImage(string imageName, string svgUri)
    {
        var image = this.FindControl<Image>(imageName);
        if (image is null)
        {
            return;
        }

        try
        {
            using var stream = AssetLoader.Open(new Uri(svgUri));
            var svg = new SKSvg();
            svg.Load(stream);
            if (svg.Picture is null)
            {
                return;
            }

            var bounds = svg.Picture.CullRect;
            var width = Math.Max(1, (int)Math.Ceiling(bounds.Width));
            var height = Math.Max(1, (int)Math.Ceiling(bounds.Height));

            using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            canvas.DrawPicture(svg.Picture);
            canvas.Flush();

            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            if (encoded is null)
            {
                return;
            }

            using var ms = encoded.AsStream();
            image.Source = new Bitmap(ms);
        }
        catch
        {
            // Keep splash functional even if SVG cannot be rendered.
        }
    }

}
