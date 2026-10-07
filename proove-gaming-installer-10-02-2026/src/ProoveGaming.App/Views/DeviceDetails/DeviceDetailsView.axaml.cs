using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ProoveGaming.App.ViewModels;

namespace ProoveGaming.App.Views;

public partial class DeviceDetailsView : UserControl
{
    public event Action? BackRequested;

    public DeviceDetailsView()
    {
        InitializeComponent();
        this.FindControl<Button>("BackButton")!.Click += OnBackClick;
    }

    private void OnBackClick(object? sender, RoutedEventArgs e) => BackRequested?.Invoke();

    // Bubbling handler on the keyboard panels: a click on a key button never arrives here (the
    // Button marks PointerPressed handled), so anything that does is empty space — clear the
    // key selection.
    private void OnKeyboardPanelPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (DataContext is DeviceDetailsViewModel vm)
            vm.DeselectPhysicalKeyCommand.Execute(null);
    }
}
