using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;

namespace ProoveGaming.App.Views.DeviceDetailsSections;

internal partial class MacroRowKebabView : UserControl
{
	public MacroRowKebabView()
	{
		InitializeComponent();
	}

	// The kebab menu is a Flyout of plain Buttons, which (unlike MenuItems) don't dismiss the
	// flyout when clicked — so Edit/Remove left the menu open until the user clicked away. Close the
	// hosting popup, but defer it: Button raises Click (this handler) BEFORE it runs its Command,
	// and closing now would detach the button and null its DataContext-bound Command so it never
	// fires. Posting the close lets the Edit/Remove command run first, then the menu dismisses.
	private void OnMacroMenuItemClick(object? sender, RoutedEventArgs e)
	{
		var popup = (sender as Control)?.FindLogicalAncestorOfType<Popup>();
		if (popup is not null)
			Dispatcher.UIThread.Post(popup.Close);
	}
}
