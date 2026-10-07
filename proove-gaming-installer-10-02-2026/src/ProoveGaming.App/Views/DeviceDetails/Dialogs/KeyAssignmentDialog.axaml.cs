using Avalonia.Controls;

namespace ProoveGaming.App.Views.DeviceDetailsDialogs;

// The per-key assignment dialog (Key Assignment Dialog). All state and commands live on the
// DeviceDetails view model the host binds as DataContext; the view is bindings only.
internal partial class KeyAssignmentDialog : UserControl
{
	public KeyAssignmentDialog()
	{
		InitializeComponent();
	}
}
