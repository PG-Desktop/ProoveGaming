using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace ProoveGaming.App.Controls;

/// <summary>
/// The pointer-driven section slider used across device-details pages: a flat track whose fill and
/// thumb follow <see cref="Percent"/> (0..100), raising <see cref="FractionChanged"/> (0..1 of the
/// track) on press and drag. The two scales are deliberately distinct types of the same gesture:
/// Percent feeds the width converters, the fraction is what a consumer converts into its own units
/// and snaps — mixing them up clamps silently, which is why the event name says "fraction".
/// </summary>
internal partial class PercentSliderControl : UserControl
{
	public static readonly StyledProperty<double> PercentProperty =
		AvaloniaProperty.Register<PercentSliderControl, double>(nameof(Percent));

	public double Percent
	{
		get => GetValue(PercentProperty);
		set => SetValue(PercentProperty, value);
	}

	/// <summary>Raised with the pointer position as 0..1 of the track width, on press and drag.</summary>
	public event EventHandler<double>? FractionChanged;

	/// <summary>
	/// Raised once the drag ends — the pointer is released, or capture is lost. A consumer whose
	/// change is expensive (a device write, a flash commit) applies it HERE and treats
	/// <see cref="FractionChanged"/> as a live preview; otherwise a slow drag applies every value it
	/// passes over, because a pause mid-drag is indistinguishable from letting go.
	/// </summary>
	public event EventHandler? DragCompleted;

	// True between press and release/capture-loss, so DragCompleted fires once per drag and never
	// for a release the slider did not start.
	private bool _dragging;

	public PercentSliderControl()
	{
		InitializeComponent();
	}

	protected override void OnPointerPressed(PointerPressedEventArgs e)
	{
		base.OnPointerPressed(e);
		_dragging = true;
		e.Pointer.Capture(this);
		RaiseFromPointer(e.GetPosition(this));
		e.Handled = true;
	}

	protected override void OnPointerMoved(PointerEventArgs e)
	{
		base.OnPointerMoved(e);
		if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
		RaiseFromPointer(e.GetPosition(this));
		e.Handled = true;
	}

	protected override void OnPointerReleased(PointerReleasedEventArgs e)
	{
		base.OnPointerReleased(e);
		if (!_dragging) return;

		_dragging = false;
		e.Pointer.Capture(null);
		DragCompleted?.Invoke(this, EventArgs.Empty);
		e.Handled = true;
	}

	// Capture can be taken away without a release — another control grabbing the pointer, the window
	// losing activation mid-drag. The drag is over either way, and a consumer waiting only on
	// PointerReleased would never apply the value the user left the thumb on.
	protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
	{
		base.OnPointerCaptureLost(e);
		if (!_dragging) return;

		_dragging = false;
		DragCompleted?.Invoke(this, EventArgs.Empty);
	}

	private void RaiseFromPointer(Point position)
	{
		var width = Bounds.Width;
		if (width <= 0) return;
		FractionChanged?.Invoke(this, Math.Clamp(position.X / width, 0.0, 1.0));
	}
}
