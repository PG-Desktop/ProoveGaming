using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace ProoveGaming.Uninstaller.Views;

internal partial class CompleteStepView : UserControl
{
	public event EventHandler? FinishRequested;

	public CompleteStepView()
	{
		AvaloniaXamlLoader.Load(this);
		this.FindControl<Button>("FinishButton")!.Click += OnFinishClick;
	}

	public void SetResult(bool succeeded)
	{
		var title = this.FindControl<TextBlock>("CompleteTitleText")!;
		var message = this.FindControl<TextBlock>("CompleteMessageText")!;

		if (succeeded)
		{
			title.Text = LocalizationManager.GetString("Step3_Title_Success");
			//message.Text = LocalizationManager.GetString("Step3_Subtitle_Success");
		}
		else
		{
			title.Text = LocalizationManager.GetString("Step3_Title_Failed");
			message.Text = LocalizationManager.GetString("Step3_Subtitle_Failed");
		}
	}

	// Nothing was deleted because the folder could not be proven to be a Proove Gaming install
	// (UninstallService.RefusedExitCode). Says what to do next instead of a generic failure.
	public void SetRefused()
	{
		var title = this.FindControl<TextBlock>("CompleteTitleText")!;
		var message = this.FindControl<TextBlock>("CompleteMessageText")!;

		title.Text = LocalizationManager.GetString("Step3_Title_Refused");
		message.Text = LocalizationManager.GetString("Step3_Subtitle_Refused");
	}

	private void OnFinishClick(object? sender, RoutedEventArgs e) => FinishRequested?.Invoke(this, EventArgs.Empty);
}
