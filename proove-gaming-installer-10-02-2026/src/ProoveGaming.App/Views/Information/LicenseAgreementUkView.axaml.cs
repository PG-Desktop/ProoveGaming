using Avalonia.Controls;
using Avalonia.Interactivity;
using ProoveGaming.Common.Controls;

namespace ProoveGaming.App.Views.Information;

public partial class LicenseAgreementUkView : UserControl
{
	public LicenseAgreementUkView()
	{
		InitializeComponent();
	}

	private void OnEmailLinkClicked(object? sender, RoutedEventArgs e) =>
		LegalEmailLink.OpenFrom(sender);
}
