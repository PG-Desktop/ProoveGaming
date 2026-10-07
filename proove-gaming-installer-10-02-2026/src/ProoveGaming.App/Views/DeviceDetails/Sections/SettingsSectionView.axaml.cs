using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using ProoveGaming.App.ViewModels;

namespace ProoveGaming.App.Views.DeviceDetailsSections;

internal partial class SettingsSectionView : UserControl
{
	public SettingsSectionView()
	{
		InitializeComponent();
	}

	// ── Device-specific slider handlers ──────────────────────────────────────
	private void OnButtonDebouncePointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (sender is not Grid grid) return;
		e.Pointer.Capture(grid);
		UpdateDeviceSliderFromPointer(grid, e.GetPosition(grid), vm => vm.ButtonDenounceSetting);
		e.Handled = true;
	}

	private void OnButtonDebouncePointerMoved(object? sender, PointerEventArgs e)
	{
		if (sender is not Grid grid) return;
		if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;
		UpdateDeviceSliderFromPointer(grid, e.GetPosition(grid), vm => vm.ButtonDenounceSetting);
		e.Handled = true;
	}

	private void UpdateDeviceSliderFromPointer(Grid grid, Point position,
		Func<DeviceDetailsViewModel, MouseSettingViewModel?> getSetting)
	{
		var width = grid.Bounds.Width;
		if (width <= 0) return;
		if (DataContext is DeviceDetailsViewModel vm)
			getSetting(vm)?.SetSliderFromPct(Math.Clamp(position.X / width, 0.0, 1.0));
	}

	// ── Generic slider handlers ───────────────────────────────────────────────
	private void OnScrollLinesPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (sender is not Grid grid) return;
		e.Pointer.Capture(grid);
		UpdateScrollLinesFromPointer(grid, e.GetPosition(grid));
		e.Handled = true;
	}

	private void OnScrollLinesPointerMoved(object? sender, PointerEventArgs e)
	{
		if (sender is not Grid grid) return;
		if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;
		UpdateScrollLinesFromPointer(grid, e.GetPosition(grid));
		e.Handled = true;
	}

	private void UpdateScrollLinesFromPointer(Grid grid, Point position)
	{
		var width = grid.Bounds.Width;
		if (width <= 0) return;
		var value = Math.Clamp(position.X / width * 99.0 + 1.0, 1, 100);
		if (DataContext is DeviceDetailsViewModel vm)
			vm.ScrollLines = value;
	}

	private void OnDoubleClickSpeedPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (sender is not Grid grid) return;
		e.Pointer.Capture(grid);
		UpdateDoubleClickSpeedFromPointer(grid, e.GetPosition(grid));
		e.Handled = true;
	}

	private void OnDoubleClickSpeedPointerMoved(object? sender, PointerEventArgs e)
	{
		if (sender is not Grid grid) return;
		if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;
		UpdateDoubleClickSpeedFromPointer(grid, e.GetPosition(grid));
		e.Handled = true;
	}

	private void UpdateDoubleClickSpeedFromPointer(Grid grid, Point position)
	{
		var width = grid.Bounds.Width;
		if (width <= 0) return;
		var value = Math.Clamp(position.X / width * 800.0 + 100.0, 100, 900);
		if (DataContext is DeviceDetailsViewModel vm)
			vm.DoubleClickSpeed = value;
	}

	private void OnSleepTimerPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (sender is not Grid grid) return;
		e.Pointer.Capture(grid);
		UpdateSleepTimerFromPointer(grid, e.GetPosition(grid));
		e.Handled = true;
	}

	private void OnSleepTimerPointerMoved(object? sender, PointerEventArgs e)
	{
		if (sender is not Grid grid) return;
		if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;
		UpdateSleepTimerFromPointer(grid, e.GetPosition(grid));
		e.Handled = true;
	}

	private void UpdateSleepTimerFromPointer(Grid grid, Point position)
	{
		var width = grid.Bounds.Width;
		if (width <= 0) return;
		var value = Math.Clamp(position.X / width * 270.0 + 30.0, 30, 300);
		if (DataContext is DeviceDetailsViewModel vm)
			vm.SleepTimer = value;
	}
}
