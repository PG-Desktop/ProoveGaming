using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ProoveGaming.App.ViewModels;

namespace ProoveGaming.App.Views.DeviceDetailsSections;

internal partial class ButtonsSectionView : UserControl
{
	private DeviceDetailsViewModel? _vm;

	public ButtonsSectionView()
	{
		InitializeComponent();
		DataContextChanged += OnDataContextChanged;
	}

	private void OnDataContextChanged(object? sender, EventArgs e)
	{
		if (_vm != null)
			_vm.ScrollToAssignedRequested -= OnScrollToAssignedRequested;

		_vm = DataContext as DeviceDetailsViewModel;

		if (_vm != null)
			_vm.ScrollToAssignedRequested += OnScrollToAssignedRequested;
	}

	// Scroll the currently-checked action option into view. Deferred to Background so the
	// group's Expander has laid out its (possibly just-expanded) content first.
	private void OnScrollToAssignedRequested()
	{
		Dispatcher.UIThread.Post(() =>
		{
			var selected = this.GetVisualDescendants()
				.OfType<RadioButton>()
				.FirstOrDefault(r => r.Classes.Contains("buttonsOptionRadio") && r.IsChecked == true);
			selected?.BringIntoView();
		}, DispatcherPriority.Background);
	}
}
