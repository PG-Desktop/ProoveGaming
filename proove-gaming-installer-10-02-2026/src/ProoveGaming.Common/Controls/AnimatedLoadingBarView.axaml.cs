using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;
using Svg.Skia;
using System.Text;
using System.Xml.Linq;

namespace ProoveGaming.Common.Controls;

public partial class AnimatedLoadingBarView : UserControl
{
    private const string DefaultLoadingSvgUri = "avares://ProoveGaming.Common/Assets/loading.svg";

    private readonly DispatcherTimer _loadingTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private string? _loadingSvgTemplate;
    private int _segmentCount = 40;
    private int _loadingFrame;
    private CancellationTokenSource? _progressSequenceCts;

    public bool IsIndeterminate { get; private set; } = true;

    public AnimatedLoadingBarView()
    {
        InitializeComponent();

        _loadingSvgTemplate = LoadAssetText(DefaultLoadingSvgUri);
        _segmentCount = GetSegmentCount(_loadingSvgTemplate);
        RenderLoadingFrame(0);

        _loadingTimer.Tick += (_, _) => AdvanceLoadingAnimation();
        _loadingTimer.Start();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        _progressSequenceCts?.Cancel();
        _progressSequenceCts?.Dispose();
        _progressSequenceCts = null;
        _loadingTimer.Stop();
        base.OnUnloaded(e);
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void AdvanceLoadingAnimation()
    {
        if (!IsIndeterminate)
        {
            return;
        }

        _loadingFrame = (_loadingFrame + 1) % Math.Max(1, _segmentCount + 1);
        RenderLoadingFrame(_loadingFrame);
    }

    // Call this to switch the bar into determinate mode and set explicit progress.
    // Example: SetProgress(0.5) fills about 50% of segments.
    public void SetProgress(double value)
    {
        IsIndeterminate = false;
        _loadingTimer.Stop();

        var normalized = Math.Clamp(value, 0, 1);
        var filledSegments = (int)Math.Round(normalized * _segmentCount);
        RenderLoadingFrame(filledSegments);
    }

    // Call this to return back to animated indeterminate mode.
    public void SetIndeterminate()
    {
        if (IsIndeterminate)
        {
            return;
        }

        IsIndeterminate = true;
        _loadingFrame = 0;
        _loadingTimer.Start();
    }

    public void StartProgressSequence(TimeSpan duration, Action? onCompleted = null)
    {
        _progressSequenceCts?.Cancel();
        _progressSequenceCts?.Dispose();
        _progressSequenceCts = new CancellationTokenSource();

        var token = _progressSequenceCts.Token;
        _ = RunProgressSequenceAsync(duration, onCompleted, token);
    }

    private void RenderLoadingFrame(int filledSegments)
    {
        if (string.IsNullOrWhiteSpace(_loadingSvgTemplate))
        {
            return;
        }

        var image = this.FindControl<Image>("LoadingImage");
        if (image is null)
        {
            return;
        }

        try
        {
            var frameSvg = BuildLoadingSvgFrame(_loadingSvgTemplate, filledSegments);
            var bitmap = RenderSvg(frameSvg);
            if (bitmap is not null)
            {
                image.Source = bitmap;
            }
        }
        catch
        {
        }
    }

    private static string BuildLoadingSvgFrame(string svgTemplate, int filledSegments)
    {
        var xdoc = XDocument.Parse(svgTemplate);
        var paths = xdoc.Descendants().Where(node => node.Name.LocalName == "path").ToList();

        for (var i = 0; i < paths.Count; i++)
        {
            var isActive = i < filledSegments;
            paths[i].SetAttributeValue("fill", isActive ? "#6357FF" : "#BEBCEA");
            paths[i].SetAttributeValue("fill-opacity", isActive ? "1" : "0.2");
        }

        return xdoc.ToString(SaveOptions.DisableFormatting);
    }

    private static int GetSegmentCount(string? svgTemplate)
    {
        if (string.IsNullOrWhiteSpace(svgTemplate))
        {
            return 14;
        }

        try
        {
            var xdoc = XDocument.Parse(svgTemplate);
            var count = xdoc.Descendants().Count(node => node.Name.LocalName == "path");
            return Math.Max(1, count);
        }
        catch
        {
            return 14;
        }
    }

    private static string? LoadAssetText(string assetUri)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(assetUri));
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? RenderSvg(string svgContent)
    {
        var svg = new SKSvg();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svgContent));
        svg.Load(stream);
        if (svg.Picture is null)
        {
            return null;
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
            return null;
        }

        using var ms = encoded.AsStream();
        return new Bitmap(ms);
    }

    private async Task RunProgressSequenceAsync(TimeSpan duration, Action? onCompleted, CancellationToken token)
    {
        var safeDuration = duration <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : duration;
        var totalSteps = Math.Max(1, _segmentCount);
        var stepDelay = TimeSpan.FromMilliseconds(Math.Max(1, safeDuration.TotalMilliseconds / totalSteps));

        SetProgress(0);

        for (var step = 1; step <= totalSteps; step++)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            SetProgress((double)step / totalSteps);
            try
            {
                await Task.Delay(stepDelay, token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }

        if (!token.IsCancellationRequested)
        {
            onCompleted?.Invoke();
        }
    }
}
