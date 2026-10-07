using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.Windows.Input;

namespace ProoveGaming.App.Controls;

internal partial class ToggleSwitchControl : UserControl
{
    public static readonly StyledProperty<bool> IsCheckedProperty =
        AvaloniaProperty.Register<ToggleSwitchControl, bool>(nameof(IsChecked), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<ICommand?> CommandProperty =
        AvaloniaProperty.Register<ToggleSwitchControl, ICommand?>(nameof(Command));

    public bool IsChecked
    {
        get => GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    public ICommand? Command
    {
        get => GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public ToggleSwitchControl()
    {
        InitializeComponent();
        // Tunnel phase fires before the inner ToggleButton processes PointerReleased.
        // When a Command is bound, mark handled to prevent the ToggleButton from self-toggling.
        AddHandler(PointerReleasedEvent, OnPointerReleasedInterceptor, RoutingStrategies.Tunnel);
        // Same for the keyboard path: Enter clicks on KeyDown and Space arms the click on
        // KeyDown, so intercepting KeyDown covers both before the ToggleButton self-toggles.
        AddHandler(KeyDownEvent, OnKeyDownInterceptor, RoutingStrategies.Tunnel);
    }

    private void OnPointerReleasedInterceptor(object? sender, PointerReleasedEventArgs e)
    {
        var cmd = Command;
        if (cmd is null) return;

        e.Handled = true;
        if (cmd.CanExecute(null))
            cmd.Execute(null);
    }

    private void OnKeyDownInterceptor(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space && e.Key != Key.Enter) return;

        var cmd = Command;
        if (cmd is null) return;

        e.Handled = true;
        if (cmd.CanExecute(null))
            cmd.Execute(null);
    }
}