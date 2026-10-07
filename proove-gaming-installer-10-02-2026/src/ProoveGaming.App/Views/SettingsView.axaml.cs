using System;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ProoveGaming.App.Views;

public partial class SettingsView : UserControl
{
	public event Action? BackRequested;

	public SettingsView()
	{
		InitializeComponent();
		NavBackButton.Click += OnBackClick;
		// Static manager + singleton view: the subscription lives as long as the window,
		// nothing to unsubscribe (mirrors the Installer's WelcomeStepView).
		LanguageSelect.LanguageSelected += code => LocalizationManager.SetLanguage(code);
	}

	protected override void OnLoaded(RoutedEventArgs e)
	{
		base.OnLoaded(e);
		LanguageSelect.SetSelectedLanguage(LocalizationManager.CurrentLanguageCode);
	}

	private void OnBackClick(object? sender, RoutedEventArgs e)
	{
		BackRequested?.Invoke();
	}
}
