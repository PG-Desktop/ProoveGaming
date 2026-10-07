using Avalonia;
using Avalonia.Controls;
using ProoveGaming.App.ViewModels;

namespace ProoveGaming.App.Views.DeviceDetailsSections;

internal partial class DeviceImageWithHotspotsView : UserControl
{
	public DeviceImageWithHotspotsView()
	{
		InitializeComponent();

		// The hotspot canvas is sized to the room THIS control has, so this control is what reports
		// it. The width used to come from the hosting panels instead, which are not the same thing:
		// the Buttons panel spans two columns and insets its content by a margin that changes with
		// the details panel, so the canvas came out ~16-420px wider than the view it had to fit
		// (PR #144 m1).
		SizeChanged += (_, e) => Report(e.NewSize.Width);

		// And a panel that is shown again without the window being resized raises no SizeChanged at
		// all: Avalonia skips arrange for a hidden control, so its Bounds never change and nothing
		// is reported. Presets -> Buttons -> Macros therefore left the canvas at the width of the
		// Buttons panel and brought the PG-683 clipping straight back (PR #144 M1). Each instance
		// reports its own width as it appears, which is also what keeps the two pages independent.
		PropertyChanged += (_, e) =>
		{
			if (e.Property == IsVisibleProperty && e.NewValue is true)
				Report(Bounds.Width);
		};
	}

	private void Report(double width)
	{
		if (DataContext is DeviceDetailsViewModel vm)
			vm.SyncHotspotCanvasWidth(width);
	}
}
