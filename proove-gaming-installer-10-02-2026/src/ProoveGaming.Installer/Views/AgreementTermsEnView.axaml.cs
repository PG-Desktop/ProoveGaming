using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ProoveGaming.Common.Controls;

namespace ProoveGaming.Installer.Views;

internal partial class AgreementTermsEnView : UserControl
{
    public AgreementTermsEnView()
    {
        InitializeComponent();
    }

    private void OnEmailLinkClicked(object? sender, RoutedEventArgs e) =>
        LegalEmailLink.OpenFrom(sender);

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
