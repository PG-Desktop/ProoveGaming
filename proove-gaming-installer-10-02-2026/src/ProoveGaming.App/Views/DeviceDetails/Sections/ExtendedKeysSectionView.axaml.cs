using Avalonia.Controls;
using ProoveGaming.App.ViewModels;

namespace ProoveGaming.App.Views.DeviceDetailsSections;

// Bindings only, bar the slider: every action is a ViewModel command, and the key picks arrive
// through the shared keyboard overlay (SelectPhysicalKey's Extended Keys branch) and the Key
// Assignment dialog's pick purpose, not through this view.
internal partial class ExtendedKeysSectionView : UserControl
{
	public ExtendedKeysSectionView()
	{
		InitializeComponent();
	}

	// Pointer handling lives inside PercentSliderControl; this forwards its 0..1 fraction to the
	// ViewModel, which snaps it to whole milliseconds. UI-only until Confirm — no device write per tick.
	private void OnTimeFractionChanged(object? sender, double fraction)
	{
		if (DataContext is DeviceDetailsViewModel vm)
			vm.SetExtendedKeyEditorTimeFromFraction(fraction);
	}
}
