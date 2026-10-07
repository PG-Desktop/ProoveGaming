using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ProoveGaming.App.ViewModels;

namespace ProoveGaming.App.Views.DeviceDetailsSections;

internal partial class AudioSectionView : UserControl
{
	public AudioSectionView()
	{
		InitializeComponent();
	}

	private void OnOutputVolumePointerPressed(object? sender, PointerPressedEventArgs e)
		=> BeginDrag(sender, e, output: true);

	private void OnOutputVolumePointerMoved(object? sender, PointerEventArgs e)
		=> ContinueDrag(sender, e, output: true);

	private void OnMicrophoneVolumePointerPressed(object? sender, PointerPressedEventArgs e)
		=> BeginDrag(sender, e, output: false);

	private void OnMicrophoneVolumePointerMoved(object? sender, PointerEventArgs e)
		=> ContinueDrag(sender, e, output: false);

	private void BeginDrag(object? sender, PointerPressedEventArgs e, bool output)
	{
		if (sender is not Grid grid) return;
		e.Pointer.Capture(grid);
		UpdateFromPointer(grid, e.GetPosition(grid), output);
		e.Handled = true;
	}

	private void ContinueDrag(object? sender, PointerEventArgs e, bool output)
	{
		if (sender is not Grid grid) return;
		if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;
		UpdateFromPointer(grid, e.GetPosition(grid), output);
		e.Handled = true;
	}

	private void UpdateFromPointer(Grid grid, Point position, bool output)
	{
		var width = grid.Bounds.Width;
		if (width <= 0) return;
		if (DataContext is not DeviceDetailsViewModel vm) return;

		var setting = output ? vm.OutputVolumeSetting : vm.MicrophoneVolumeSetting;
		setting?.SetSliderFromPct(Math.Clamp(position.X / width, 0.0, 1.0));
	}
}
