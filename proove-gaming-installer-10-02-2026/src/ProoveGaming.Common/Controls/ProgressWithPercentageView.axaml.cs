using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ProoveGaming.Common.Controls;

public partial class ProgressWithPercentageView : UserControl
{
	private TextBlock? _percentText;
	private AnimatedLoadingBarView? _loadingBar;

	public ProgressWithPercentageView()
	{
		InitializeComponent();
		_percentText = this.FindControl<TextBlock>("PercentText");
		_loadingBar = this.FindControl<AnimatedLoadingBarView>("LoadingBar");
	}

	private void InitializeComponent()
	{
		AvaloniaXamlLoader.Load(this);
	}

	public void SetProgress(double value)
	{
		var normalized = Math.Clamp(value, 0, 1);
		var percent = (int)Math.Round(normalized * 100);

		if (_percentText is not null)
		{
			_percentText.Text = $"{percent}%";
		}

		_loadingBar?.SetProgress(normalized);
	}
}
