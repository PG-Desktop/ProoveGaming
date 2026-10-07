using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace ProoveGaming.Uninstaller.Views;

internal partial class MainWindow : Window
{
	private readonly WelcomeStepView _welcomeStep;
	private readonly ProgressStepView _progressStep;
	private readonly CompleteStepView _completeStep;

	private UninstallStep _currentStep = UninstallStep.Welcome;

	public MainWindow()
	{
		InitializeComponent();

		_welcomeStep = this.FindControl<WelcomeStepView>("WelcomeStep")!;
		_progressStep = this.FindControl<ProgressStepView>("ProgressStep")!;
		_completeStep = this.FindControl<CompleteStepView>("CompleteStep")!;

		_welcomeStep.CancelRequested += OnCancelRequested;
		_welcomeStep.UninstallRequested += OnUninstallRequested;
		_completeStep.FinishRequested += OnFinishRequested;

		UpdateStepUi();
	}

	private void InitializeComponent()
	{
		AvaloniaXamlLoader.Load(this);
	}

	protected override void OnClosed(EventArgs e)
	{
		_welcomeStep.CancelRequested -= OnCancelRequested;
		_welcomeStep.UninstallRequested -= OnUninstallRequested;
		_completeStep.FinishRequested -= OnFinishRequested;
		base.OnClosed(e);
	}

	private void OnCancelRequested(object? sender, EventArgs e)
	{
		Close();
	}

	private void OnCloseWindowClick(object? sender, RoutedEventArgs e)
	{
		Close();
	}

	private void OnMinimizeClick(object? sender, RoutedEventArgs e)
	{
		WindowState = WindowState.Minimized;
	}

	private void OnMaximizeClick(object? sender, RoutedEventArgs e)
	{
		WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
	}

	private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		BeginMoveDrag(e);
	}

	private async void OnUninstallRequested(object? sender, bool deleteUserData)
	{
		_currentStep = UninstallStep.Progress;
		UpdateStepUi();
		await RunUninstallAsync(deleteUserData);
	}

	private async Task RunUninstallAsync(bool deleteUserData)
	{
		#if DEBUG
		await RunMockUninstallAsync();
		return;
		#endif

		_progressStep.SetStatus(LocalizationManager.GetString("Step2_Subtitle_StoppingService"));
		_progressStep.SetProgress(0.1);

		var exitCode = await Task.Run(() =>
			UninstallService.RunAsync(
				LaunchContext.StartupArgs,
				deleteUserData,
				stage =>
				{
					Dispatcher.UIThread.Post(() =>
					{
						switch (stage)
						{
							case UninstallService.UninstallStage.StoppingDeviceHostService:
								_progressStep.SetStatus(LocalizationManager.GetString("Step2_Subtitle_StoppingService"));
								_progressStep.SetProgress(0.2);
								break;
							case UninstallService.UninstallStage.RemovingDeviceHostService:
								_progressStep.SetStatus(LocalizationManager.GetString("Step2_Subtitle_RemovingService"));
								_progressStep.SetProgress(0.5);
								break;
							case UninstallService.UninstallStage.RemovingFiles:
								_progressStep.SetStatus(LocalizationManager.GetString("Step2_Subtitle_RemovingFiles"));
								_progressStep.SetProgress(0.75);
								break;
						}
					});
				}));
		var succeeded = exitCode == 0;

		await Dispatcher.UIThread.InvokeAsync(() =>
		{
			_progressStep.SetProgress(1.0);
			if (exitCode == UninstallService.RefusedExitCode)
			{
				_completeStep.SetRefused();
			}
			else
			{
				_completeStep.SetResult(succeeded);
			}
			_currentStep = UninstallStep.Complete;
			UpdateStepUi();
		});
	}

	#if DEBUG
	private async Task RunMockUninstallAsync()
	{
		_progressStep.SetStatus(LocalizationManager.GetString("Step2_Subtitle_StoppingService"));
		_progressStep.SetProgress(0.2);
		await Task.Delay(350);

		_progressStep.SetStatus(LocalizationManager.GetString("Step2_Subtitle_RemovingService"));
		_progressStep.SetProgress(0.5);
		await Task.Delay(350);

		_progressStep.SetStatus(LocalizationManager.GetString("Step2_Subtitle_RemovingFiles"));
		_progressStep.SetProgress(0.75);
		await Task.Delay(350);

		_progressStep.SetProgress(1.0);
		_completeStep.SetResult(true);
		_currentStep = UninstallStep.Complete;
		UpdateStepUi();
	}
	#endif

	private void OnFinishRequested(object? sender, EventArgs e)
	{
		Close();
	}

	private void UpdateStepUi()
	{
		_welcomeStep.IsVisible = _currentStep == UninstallStep.Welcome;
		_progressStep.IsVisible = _currentStep == UninstallStep.Progress;
		_completeStep.IsVisible = _currentStep == UninstallStep.Complete;
	}
}
