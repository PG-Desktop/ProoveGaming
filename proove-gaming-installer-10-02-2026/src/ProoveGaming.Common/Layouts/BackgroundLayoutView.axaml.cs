using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Controls.Presenters;

namespace ProoveGaming.Common.Layouts;

public partial class BackgroundLayoutView : UserControl
{
    public static readonly StyledProperty<object?> LayoutContentProperty =
        AvaloniaProperty.Register<BackgroundLayoutView, object?>(nameof(LayoutContent));

    static BackgroundLayoutView()
    {
        LayoutContentProperty.Changed.AddClassHandler<BackgroundLayoutView>((x, _) => x.ApplyContentVisuals());
    }

    public object? LayoutContent
    {
        get => GetValue(LayoutContentProperty);
        set => SetValue(LayoutContentProperty, value);
    }

    public BackgroundLayoutView()
    {
        InitializeComponent();
        ApplyContentVisuals();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void ApplyContentVisuals()
    {
        GetContentPresenter().Content = LayoutContent;
    }

    private ContentPresenter GetContentPresenter() => this.FindControl<ContentPresenter>("LayoutContentPresenter")!;
}
