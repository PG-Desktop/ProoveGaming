using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ProoveGaming.Common.Controls;

namespace ProoveGaming.Uninstaller.Views;

internal partial class ProgressStepView : UserControl
{
	public ProgressStepView()
	{
		AvaloniaXamlLoader.Load(this);
	}

	public void SetStatus(string text)
	{
		this.FindControl<TextBlock>("ProgressStatusText")!.Text = text;
	}

	public void SetProgress(double value)
	{
		this.FindControl<ProgressWithPercentageView>("UninstallProgressView")!.SetProgress(value);
	}
}
