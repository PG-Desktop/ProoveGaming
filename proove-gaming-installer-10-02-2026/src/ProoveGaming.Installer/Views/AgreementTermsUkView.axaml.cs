using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ProoveGaming.Common.Controls;

namespace ProoveGaming.Installer.Views;

internal partial class AgreementTermsUkView : UserControl
{
    public AgreementTermsUkView()
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
