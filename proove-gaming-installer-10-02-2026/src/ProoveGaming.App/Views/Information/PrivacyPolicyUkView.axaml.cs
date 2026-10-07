using Avalonia.Controls;
using Avalonia.Interactivity;
using ProoveGaming.Common.Controls;

namespace ProoveGaming.App.Views.Information;

public partial class PrivacyPolicyUkView : UserControl
{
	public PrivacyPolicyUkView()
	{
		InitializeComponent();
	}

	private void OnEmailLinkClicked(object? sender, RoutedEventArgs e) =>
		LegalEmailLink.OpenFrom(sender);
}
