using Avalonia;
using Avalonia.Controls;
using ProoveGaming.App.ViewModels;

namespace ProoveGaming.App.Views.DeviceDetailsSections;

public partial class NavSectionButton : UserControl
{
	public static readonly StyledProperty<string?> IconSourceProperty =
		AvaloniaProperty.Register<NavSectionButton, string?>(nameof(IconSource));

	public static readonly StyledProperty<double> IconWidthProperty =
		AvaloniaProperty.Register<NavSectionButton, double>(nameof(IconWidth), 24);

	public static readonly StyledProperty<double> IconHeightProperty =
		AvaloniaProperty.Register<NavSectionButton, double>(nameof(IconHeight), 24);

	public static readonly StyledProperty<string?> LabelProperty =
		AvaloniaProperty.Register<NavSectionButton, string?>(nameof(Label));

	public static readonly StyledProperty<NavSection> SectionProperty =
		AvaloniaProperty.Register<NavSectionButton, NavSection>(nameof(Section));

	public static readonly StyledProperty<bool> IsActiveProperty =
		AvaloniaProperty.Register<NavSectionButton, bool>(nameof(IsActive));

	public string? IconSource
	{
		get => GetValue(IconSourceProperty);
		set => SetValue(IconSourceProperty, value);
	}

	public double IconWidth
	{
		get => GetValue(IconWidthProperty);
		set => SetValue(IconWidthProperty, value);
	}

	public double IconHeight
	{
		get => GetValue(IconHeightProperty);
		set => SetValue(IconHeightProperty, value);
	}

	public string? Label
	{
		get => GetValue(LabelProperty);
		set => SetValue(LabelProperty, value);
	}

	public NavSection Section
	{
		get => GetValue(SectionProperty);
		set => SetValue(SectionProperty, value);
	}

	public bool IsActive
	{
		get => GetValue(IsActiveProperty);
		set => SetValue(IsActiveProperty, value);
	}

	public NavSectionButton()
	{
		InitializeComponent();
	}
}
