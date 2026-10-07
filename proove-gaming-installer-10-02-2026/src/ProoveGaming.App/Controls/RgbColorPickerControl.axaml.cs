using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ProoveGaming.App.ViewModels;

namespace ProoveGaming.App.Controls;

internal partial class RgbColorPickerControl : UserControl
{
	// Hue strip dimensions — matches popup inner content width (248 container - 16 padding each side)
	private const int HueStripW = 216;
	private const int HueStripH = 22;

	// Color plane dimensions
	private const int PlaneW = 216;
	private const int PlaneH = 160;

	public RgbColorPickerControl()
	{
		InitializeComponent();
		DataContextChanged += OnDataContextChanged;
	}

	private RgbEffectSettingsViewModel? _vm;

	private void OnDataContextChanged(object? sender, EventArgs e)
	{
		if (_vm is not null)
			_vm.PropertyChanged -= OnVmPropertyChanged;
		_vm = DataContext as RgbEffectSettingsViewModel;
		if (_vm is not null)
			_vm.PropertyChanged += OnVmPropertyChanged;
	}

	private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
	{
		if (e.PropertyName == nameof(RgbEffectSettingsViewModel.PickerHueNormalized))
			RefreshColorPlane(_vm!.PickerHueNormalized * 360.0);
	}

	// ── Popup border clip ─────────────────────────────────────────────────────

	private void OnPickerPopupSizeChanged(object? sender, SizeChangedEventArgs e)
	{
		if (sender is not Border border) return;
		ApplyChamferClip(border, e.NewSize.Width, e.NewSize.Height);
	}

	private static void ApplyChamferClip(Border border, double w, double h)
	{
		const double cut = 16.0;
		if (w <= 0 || h <= 0) return;
		var geo = new StreamGeometry();
		using var ctx = geo.Open();
		ctx.BeginFigure(new Point(0, 0), true);
		ctx.LineTo(new Point(w - cut, 0));
		ctx.LineTo(new Point(w, cut));
		ctx.LineTo(new Point(w, h));
		ctx.LineTo(new Point(cut, h));
		ctx.LineTo(new Point(0, h - cut));
		ctx.EndFigure(true);
		border.Clip = geo;
	}

	// ── Bitmap rendering ─────────────────────────────────────────────────────

	private void OnHueStripImageLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
	{
		if (sender is not Image img) return;
		img.Source = RenderHueStrip(HueStripW, HueStripH);
	}

	private void OnColorPlaneImageLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
	{
		if (sender is not Image img) return;
		var hue = (_vm?.PickerHueNormalized ?? 0) * 360.0;
		img.Source = RenderColorPlane(PlaneW, PlaneH, hue);
	}

	private static WriteableBitmap RenderHueStrip(int w, int h)
	{
		var stops = new (float offset, float hue, float s, float v)[]
		{
			(0f,    0f,   1f, 1f),
			(0.13f, 60f,  1f, 1f),
			(0.27f, 120f, 1f, 1f),
			(0.42f, 150f, 1f, 1f),
			(0.57f, 195f, 1f, 1f),
			(0.69f, 240f, 1f, 1f),
			(0.81f, 270f, 1f, 1f),
			(0.91f, 315f, 1f, 1f),
			(1f,    360f, 1f, 1f),
		};

		var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
		using var fb = bmp.Lock();
		int stride = fb.RowBytes;
		var row = new byte[stride];
		for (int x = 0; x < w; x++)
		{
			float t = x / (float)(w - 1);
			var (r, g, b) = InterpolateHueStops(stops, t);
			int col = x * 4;
			row[col] = b;
			row[col + 1] = g;
			row[col + 2] = r;
			row[col + 3] = 255;
		}
		for (int y = 0; y < h; y++)
			Marshal.Copy(row, 0, fb.Address + y * stride, stride);
		return bmp;
	}

	private WriteableBitmap RenderColorPlane(int w, int h, double hue)
	{
		var bmp = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
		using var fb = bmp.Lock();
		int stride = fb.RowBytes;
		var row = new byte[stride];
		for (int y = 0; y < h; y++)
		{
			float value = 1f - y / (float)(h - 1);
			for (int x = 0; x < w; x++)
			{
				float sat = x / (float)(w - 1);
				var (r, g, b) = HsvToRgbBytes(hue, sat, value);
				int col = x * 4;
				row[col] = b;
				row[col + 1] = g;
				row[col + 2] = r;
				row[col + 3] = 255;
			}
			Marshal.Copy(row, 0, fb.Address + y * stride, stride);
		}
		return bmp;
	}

	private static (byte r, byte g, byte b) InterpolateHueStops(
		(float offset, float hue, float s, float v)[] stops, float t)
	{
		for (int i = 0; i < stops.Length - 1; i++)
		{
			var a = stops[i];
			var b = stops[i + 1];
			if (t >= a.offset && t <= b.offset)
			{
				float local = (t - a.offset) / (b.offset - a.offset);
				float hue = a.hue + local * (b.hue - a.hue);
				return HsvToRgbBytes(hue, 1f, 1f);
			}
		}
		return HsvToRgbBytes(stops[^1].hue, 1f, 1f);
	}

	private static (byte r, byte g, byte b) HsvToRgbBytes(double h, double s, double v)
	{
		h = (h % 360 + 360) % 360;
		s = Math.Clamp(s, 0, 1);
		v = Math.Clamp(v, 0, 1);
		double c = v * s;
		double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
		double m = v - c;
		double r1, g1, b1;
		if (h < 60) (r1, g1, b1) = (c, x, 0);
		else if (h < 120) (r1, g1, b1) = (x, c, 0);
		else if (h < 180) (r1, g1, b1) = (0, c, x);
		else if (h < 240) (r1, g1, b1) = (0, x, c);
		else if (h < 300) (r1, g1, b1) = (x, 0, c);
		else (r1, g1, b1) = (c, 0, x);
		return ((byte)Math.Round((r1 + m) * 255),
				(byte)Math.Round((g1 + m) * 255),
				(byte)Math.Round((b1 + m) * 255));
	}

	private void RefreshColorPlane(double hue)
	{
		if (this.FindControl<Image>("PickerColorPlaneImage") is Image img)
			img.Source = RenderColorPlane(PlaneW, PlaneH, hue);
	}

	// ── Brightness slider ─────────────────────────────────────────────────────

	private void OnBrightnessSliderPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (sender is not Grid grid) return;
		e.Pointer.Capture(grid);
		UpdateBrightnessFromPointer(grid, e.GetPosition(grid));
		e.Handled = true;
	}

	private void OnBrightnessSliderPointerMoved(object? sender, PointerEventArgs e)
	{
		if (sender is not Grid grid) return;
		if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;
		UpdateBrightnessFromPointer(grid, e.GetPosition(grid));
		e.Handled = true;
	}

	private void UpdateBrightnessFromPointer(Grid grid, Point position)
	{
		var width = grid.Bounds.Width;
		if (width <= 0) return;
		var pct = Math.Clamp(position.X / width * 100.0, 0, 100);
		if (_vm is not null)
			_vm.Brightness = pct;
	}

	// ── Speed slider ──────────────────────────────────────────────────────────

	private void OnSpeedSliderPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (sender is not Grid grid) return;
		e.Pointer.Capture(grid);
		UpdateSpeedFromPointer(grid, e.GetPosition(grid));
		e.Handled = true;
	}

	private void OnSpeedSliderPointerMoved(object? sender, PointerEventArgs e)
	{
		if (sender is not Grid grid) return;
		if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;
		UpdateSpeedFromPointer(grid, e.GetPosition(grid));
		e.Handled = true;
	}

	private void UpdateSpeedFromPointer(Grid grid, Point position)
	{
		var width = grid.Bounds.Width;
		if (width <= 0) return;
		var pct = Math.Clamp(position.X / width * 100.0, 0, 100);
		if (_vm is not null)
			_vm.Speed = pct;
	}

	// ── Hue strip ─────────────────────────────────────────────────────────────

	private void OnHueStripPointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (sender is not Grid grid) return;
		e.Pointer.Capture(grid);
		UpdateHueFromPointer(grid, e.GetPosition(grid));
		e.Handled = true;
	}

	private void OnHueStripPointerMoved(object? sender, PointerEventArgs e)
	{
		if (sender is not Grid grid) return;
		if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;
		UpdateHueFromPointer(grid, e.GetPosition(grid));
		e.Handled = true;
	}

	private void UpdateHueFromPointer(Grid grid, Point position)
	{
		var width = grid.Bounds.Width;
		if (width <= 0) return;
		var x = Math.Clamp(position.X / width, 0, 1);
		if (_vm is not null)
		{
			_vm.UpdateHueFromStripPosition(x);
			RefreshColorPlane(x * 360.0);
		}
	}

	// ── Color plane ───────────────────────────────────────────────────────────

	private void OnColorPlanePointerPressed(object? sender, PointerPressedEventArgs e)
	{
		if (sender is not Grid grid) return;
		e.Pointer.Capture(grid);
		UpdateColorPlaneFromPointer(grid, e.GetPosition(grid));
		e.Handled = true;
	}

	private void OnColorPlanePointerMoved(object? sender, PointerEventArgs e)
	{
		if (sender is not Grid grid) return;
		if (!e.GetCurrentPoint(grid).Properties.IsLeftButtonPressed) return;
		UpdateColorPlaneFromPointer(grid, e.GetPosition(grid));
		e.Handled = true;
	}

	private void UpdateColorPlaneFromPointer(Grid grid, Point position)
	{
		var width = grid.Bounds.Width;
		var height = grid.Bounds.Height;
		if (width <= 0 || height <= 0) return;
		var x = Math.Clamp(position.X / width, 0, 1);
		var y = Math.Clamp(position.Y / height, 0, 1);
		_vm?.UpdateColorFromPlanePosition(x, y);
	}
}
