using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Markup.Xaml;

namespace ProoveGaming.Common.Layouts;

public partial class StepFrameLayoutView : UserControl
{
	public static readonly StyledProperty<object?> StepContentProperty =
		AvaloniaProperty.Register<StepFrameLayoutView, object?>(nameof(StepContent));

	static StepFrameLayoutView()
	{
		StepContentProperty.Changed.AddClassHandler<StepFrameLayoutView>((x, _) => x.ApplyContent());
	}

	public object? StepContent
	{
		get => GetValue(StepContentProperty);
		set => SetValue(StepContentProperty, value);
	}

	private ContentPresenter? _stepContentPresenter;

	public StepFrameLayoutView()
	{
		InitializeComponent();
		_stepContentPresenter = this.FindControl<ContentPresenter>("StepContentPresenter");
		ApplyContent();
	}

	private void InitializeComponent()
	{
		AvaloniaXamlLoader.Load(this);
	}

	private void ApplyContent()
	{
		if (_stepContentPresenter is not null)
		{
			_stepContentPresenter.Content = StepContent;
		}
	}
}
