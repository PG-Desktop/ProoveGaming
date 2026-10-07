using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ProoveGaming.App.ViewModels;

namespace ProoveGaming.App.Views.DeviceDetailsSections;

internal partial class DpiSectionView : UserControl
{
	public DpiSectionView()
	{
		InitializeComponent();
	}

	private void OnLiftOffPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (sender is not Grid grid) return;
		e.Pointer.Capture(grid);
		UpdateLiftOffFromPointer(grid, e.GetPosition(grid));
		e.Handled = true;
	}

	private void OnLiftOffPointerMoved(object? sender, PointerEventArgs e)
	{
		if (sender is not Grid grid) return;
		if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;
		UpdateLiftOffFromPointer(grid, e.GetPosition(grid));
		e.Handled = true;
	}

	private void UpdateLiftOffFromPointer(Grid grid, Point position)
	{
		var width = grid.Bounds.Width;
		if (width <= 0) return;
		if (DataContext is DeviceDetailsViewModel vm)
			vm.LiftOffSetting?.SetSliderFromPct(Math.Clamp(position.X / width, 0.0, 1.0));
	}

	private void OnCustomDpiFractionChanged(object? sender, double fraction)
	{
		if (DataContext is DeviceDetailsViewModel vm)
			vm.SetCustomDpiFraction(fraction);
	}

	// The drag is over — this is where the value is offered to the mouse. Dragging itself only
	// previews, so a pause mid-drag can no longer be mistaken for letting go.
	private void OnCustomDpiDragCompleted(object? sender, EventArgs e)
	{
		if (DataContext is DeviceDetailsViewModel vm)
			vm.CommitCustomDpiDrag();
	}

	// The box the user is editing, held only while it has focus — see OnTopLevelPointerPressed.
	private TextBox? _customDpiInput;

	// Records which point the box is editing, so a commit cannot be redirected onto a different row
	// by a selection change that happens in the same gesture (see BeginCustomDpiEdit).
	//
	// Also starts watching the window for a press outside the box. The box commits on Enter and on
	// LostFocus, but most of this section is Borders, TextBlocks and Paths, and Avalonia does not
	// move focus to those — so clicking "somewhere else" usually raised no LostFocus at all and the
	// typed value just sat there uncommitted. Same outside-click contract the device card's kebab
	// menu uses (DeviceCardBase.SubscribeToTopLevel), and subscribed only while focused so there is
	// no window-wide handler running the rest of the time.
	private void OnCustomDpiGotFocus(object? sender, RoutedEventArgs e)
	{
		if (DataContext is DeviceDetailsViewModel vm)
			vm.BeginCustomDpiEdit();

		_customDpiInput = sender as TextBox;
		TopLevel.GetTopLevel(this)?.AddHandler(PointerPressedEvent, OnTopLevelPointerPressed, RoutingStrategies.Tunnel);
	}

	// Tunnelling, so the press is seen before a child marks it handled — the point rows, the
	// sliders and the section buttons all handle their own pointer input.
	private void OnTopLevelPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (_customDpiInput is not { } box) return;

		// A press inside the box itself is just the user moving the caret.
		var source = e.Source as Visual;
		while (source != null)
		{
			if (ReferenceEquals(source, box)) return;
			source = source.GetVisualParent();
		}

		// Applied in place. The caret deliberately stays: Avalonia offers no supported way to drop
		// focus without handing it to some other control, and the box keeping focus is harmless —
		// the row being edited is held until focus really does leave (BeginCustomDpiEdit), so a
		// second commit from the same session still lands on the right point.
		CommitCustomDpi(box);
	}

	private void StopWatchingForOutsideClick()
	{
		TopLevel.GetTopLevel(this)?.RemoveHandler(PointerPressedEvent, OnTopLevelPointerPressed);
		_customDpiInput = null;
	}

	// The view can be torn down (device closed, section swapped) while the box still holds focus,
	// which would otherwise leave the handler attached to the window.
	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		StopWatchingForOutsideClick();
		base.OnDetachedFromVisualTree(e);
	}

	// Digits only, in two layers. Refusing the keystroke keeps a rejected character from ever
	// appearing, which is what typing feels like; the sanitizer below is what actually guarantees
	// the rule, because paste, drag-drop and IME composition each reach the box by a different route
	// and none of them raise TextInput.
	private void OnCustomDpiTextInput(object? sender, TextInputEventArgs e)
	{
		if (!string.IsNullOrEmpty(e.Text) && !e.Text.All(char.IsAsciiDigit))
			e.Handled = true;
	}

	// Re-entrancy guard: rewriting Text below raises TextChanged again.
	private bool _sanitizingCustomDpi;

	private void OnCustomDpiTextChanged(object? sender, TextChangedEventArgs e)
	{
		if (_sanitizingCustomDpi || sender is not TextBox box) return;

		var text = box.Text;
		var cleaned = DeviceDetailsViewModel.KeepDigitsOnly(text);
		if (cleaned == text) return;

		// Keep the caret where the user left it, counted in DIGITS rather than characters — the
		// stripped characters sat somewhere in between, so a raw index would drift.
		var caret = Math.Clamp(box.CaretIndex, 0, text?.Length ?? 0);
		var digitsBeforeCaret = text is null ? 0 : text.Take(caret).Count(char.IsAsciiDigit);

		_sanitizingCustomDpi = true;
		box.Text = cleaned;
		box.CaretIndex = Math.Clamp(digitsBeforeCaret, 0, cleaned.Length);
		_sanitizingCustomDpi = false;
	}

	private void CommitCustomDpi(TextBox box)
	{
		if (DataContext is not DeviceDetailsViewModel vm) return;
		vm.CommitCustomDpiText(box.Text);
		box.Text = vm.CustomDpiText;
	}

	// Enter commits; the value is NOT taken per keystroke, which would push a half-typed number to
	// the mouse. The box is then rewritten from the view model, so what the user sees is the
	// quantized value that was actually stored.
	private void OnCustomDpiKeyDown(object? sender, KeyEventArgs e)
	{
		if (e.Key != Key.Enter) return;
		if (sender is TextBox box)
		{
			CommitCustomDpi(box);
			e.Handled = true;
		}
	}

	// Losing focus applies the value exactly as Enter does — tabbing away or clicking a focusable
	// control both land here. A click on the section's own chrome does not (nothing there takes
	// focus), which is what OnTopLevelPointerPressed covers.
	private void OnCustomDpiLostFocus(object? sender, RoutedEventArgs e)
	{
		StopWatchingForOutsideClick();
		if (sender is TextBox box)
			CommitCustomDpi(box);
		(DataContext as DeviceDetailsViewModel)?.EndCustomDpiEdit();
	}
}
