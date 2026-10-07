using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ProoveGaming.Common.Controls;

namespace ProoveGaming.Installer.Views;

internal partial class WelcomeStepView : UserControl
{
	public WelcomeStepView()
	{
		InitializeComponent();

		var languageSelect = this.FindControl<LanguageSelectView>("WelcomeLanguageSelect")!;
		languageSelect.SetSelectedLanguage(LocalizationManager.CurrentLanguageCode);
		languageSelect.LanguageSelected += code => LocalizationManager.SetLanguage(code);
	}

	private void InitializeComponent()
	{
		AvaloniaXamlLoader.Load(this);
	}
}
