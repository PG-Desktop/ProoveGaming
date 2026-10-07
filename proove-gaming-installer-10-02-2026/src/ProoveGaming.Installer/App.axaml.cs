using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ProoveGaming.Common.Localization;
using ProoveGaming.Installer.Views;
using System.Diagnostics;

namespace ProoveGaming.Installer;

public partial class App : Application
{
	public override void Initialize()
	{
		AvaloniaXamlLoader.Load(this);
	}

	public override void OnFrameworkInitializationCompleted()
	{
		if (!Debugger.IsAttached && LanguagePreferenceStore.TryGetLanguage(out var persistedLanguageCode))
		{
			LocalizationManager.SetLanguage(persistedLanguageCode);
		}
		else
		{
			LocalizationManager.InitializeFromSystemCulture();
		}

		if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
		{
			desktop.MainWindow = new MainWindow();
		}

		base.OnFrameworkInitializationCompleted();
	}
}
