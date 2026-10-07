using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ProoveGaming.Installer.Views;

internal partial class InstallingStepView : UserControl
{
    public InstallingStepView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
