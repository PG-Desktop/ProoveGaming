using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ProoveGaming.Installer.Views;

internal partial class FolderStepView : UserControl
{
    public FolderStepView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
