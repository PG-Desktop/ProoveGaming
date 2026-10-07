using Avalonia.Controls;
using Avalonia.Interactivity;
using ProoveGaming.App.ViewModels;

namespace ProoveGaming.App.Views.DeviceDetailsSections;

internal partial class ActuationSectionView : UserControl
{
	public ActuationSectionView()
	{
		InitializeComponent();
	}

	private void OnAllKeysModeClick(object? sender, RoutedEventArgs e)
	{
		if (DataContext is DeviceDetailsViewModel vm)
			vm.ActuationPerKeyMode = false;
	}

	private void OnPerKeyModeClick(object? sender, RoutedEventArgs e)
	{
		if (DataContext is DeviceDetailsViewModel vm)
			vm.ActuationPerKeyMode = true;
	}

	// Pointer handling lives inside PercentSliderControl; these forward its 0..1 fraction to the
	// ViewModel, where unit conversion, step snapping and the debounced device write happen.
	private void OnPressFractionChanged(object? sender, double fraction)
	{
		if (DataContext is DeviceDetailsViewModel vm)
			vm.SetActuationPressFromFraction(fraction);
	}

	private void OnReleaseFractionChanged(object? sender, double fraction)
	{
		if (DataContext is DeviceDetailsViewModel vm)
			vm.SetActuationReleaseFromFraction(fraction);
	}

	private void OnQuickPressFractionChanged(object? sender, double fraction)
	{
		if (DataContext is DeviceDetailsViewModel vm)
			vm.SetActuationQuickPressFromFraction(fraction);
	}

	private void OnQuickReleaseFractionChanged(object? sender, double fraction)
	{
		if (DataContext is DeviceDetailsViewModel vm)
			vm.SetActuationQuickReleaseFromFraction(fraction);
	}
}
