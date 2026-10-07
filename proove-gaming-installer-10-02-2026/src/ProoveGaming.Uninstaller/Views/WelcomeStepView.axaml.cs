using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace ProoveGaming.Uninstaller.Views;

internal partial class WelcomeStepView : UserControl
{
	public event EventHandler? CancelRequested;
	public event EventHandler<bool>? UninstallRequested;

	public WelcomeStepView()
	{
		AvaloniaXamlLoader.Load(this);
		this.FindControl<Button>("CancelButton")!.Click += OnCancelClick;
		this.FindControl<Button>("UninstallButton")!.Click += OnUninstallClick;
	}

	private void OnCancelClick(object? sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);

	private void OnUninstallClick(object? sender, RoutedEventArgs e)
	{
		var deleteUserData = this.FindControl<CheckBox>("DeleteProfilesSettingsCheckBox")!.IsChecked == true;
		UninstallRequested?.Invoke(this, deleteUserData);
	}
}
