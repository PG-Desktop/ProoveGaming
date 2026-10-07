using Avalonia;
using Avalonia.Layout;
using System;
using System.Globalization;

namespace ProoveGaming.App.ViewModels;

public enum HotspotLabelSide
{
	Left,
	Right
}

public sealed class DevicePartHotspotViewModel : ViewModelBase
{
	private bool _isSelected;
	private double _canvasWidth = DesignCanvasWidth;
	private const double MarkerRadius = 16;
	private const double ButtonLineSpacing = 12;
	private const double ConnectorPointRadius = 2;
	private const double SingleLineHeight = 32;
	private const double MacroLineHeight = 60;
	// The macro sub-line adds the unassign (✕) button (28px octagon hover target) and its spacing
	// to the row width.
	private const double MacroRowExtraWidth = 36;

	public required string Id { get; init; }
	public string? LabelKey { get; init; }
	public string? DescriptionKey { get; init; }

	// False for a button the device can't remap — left click, which the firmware keeps fixed. The
	// hotspot still draws (it labels the part) but its button is disabled and greyed, so it no
	// longer opens an assignment panel that has nothing to offer. Kept in step with
	// DeviceDetailsViewModel.HotspotIdToButton, which decides the same thing for the device write.
	public bool IsAssignable { get; init; } = true;
	public required Point ButtonPoint { get; init; }
	public required Point TargetPoint { get; init; }
	public HotspotLabelSide LabelSide { get; init; } = HotspotLabelSide.Left;

	// Grows to a two-line hotspot (button label + assigned-macro row) on the Macros page when a
	// macro is bound to this button; otherwise the single-line label height.
	public double ButtonHeight => ShowAssignedMacro ? MacroLineHeight : SingleLineHeight;

	private string _label = string.Empty;
	public required string Label
	{
		get => _label;
		set
		{
			if (SetProperty(ref _label, value))
				RaiseGeometryChanged();
		}
	}

	private string? _description;
	public string? Description
	{
		get => _description;
		set => SetProperty(ref _description, value);
	}

	// Macros page only: the macro currently bound to this button, shown on a second line with an
	// unassign (✕) control. Set by DeviceDetailsViewModel.RefreshHotspotMacroBadges.
	private string? _assignedMacroName;
	public string? AssignedMacroName
	{
		get => _assignedMacroName;
		set
		{
			if (SetProperty(ref _assignedMacroName, value))
			{
				OnPropertyChanged(nameof(AssignedMacroDisplayName));
				RaiseGeometryChanged();
			}
		}
	}

	// A long macro name would stretch the hotspot, so the second line shows at most this many
	// characters, ellipsised (e.g. "Quick Unique Action 1" → "Quick Unique...").
	private const int MaxAssignedMacroNameChars = 12;
	public string? AssignedMacroDisplayName => AssignedMacroName is { Length: > MaxAssignedMacroNameChars }
		? AssignedMacroName[..MaxAssignedMacroNameChars].TrimEnd() + "..."
		: AssignedMacroName;

	// True only when the Macros section is active AND this button is bound to a macro; drives the
	// second line / two-line sizing. The Buttons page keeps every hotspot single-line.
	private bool _showAssignedMacro;
	public bool ShowAssignedMacro
	{
		get => _showAssignedMacro;
		set
		{
			if (SetProperty(ref _showAssignedMacro, value))
			{
				OnPropertyChanged(nameof(ButtonHeight));
				OnPropertyChanged(nameof(LabelAlignment));
				RaiseGeometryChanged();
			}
		}
	}

	// The two-line (macro-assigned) hotspot left-aligns its button label so it lines up with the
	// macro name below it; a single-line hotspot keeps its centred label.
	public HorizontalAlignment LabelAlignment => ShowAssignedMacro ? HorizontalAlignment.Left : HorizontalAlignment.Center;

	public double ButtonWidth
	{
		get
		{
			var labelWidth = LabelWidthEstimator.Estimate(Label);
			if (!ShowAssignedMacro)
				return labelWidth + 24;

			var macroRowWidth = LabelWidthEstimator.Estimate(AssignedMacroDisplayName ?? string.Empty) + MacroRowExtraWidth;
			return Math.Max(labelWidth, macroRowWidth) + 24;
		}
	}

	public string ConnectorPathData => string.Create(
		CultureInfo.InvariantCulture,
		$"M {ConnectorStartX:0.###},{ButtonPoint.Y:0.###} L {ConnectorMidX:0.###},{ButtonPoint.Y:0.###} L {ConnectorTargetBorderX:0.###},{ConnectorTargetBorderY:0.###}");

	public double ButtonX => ClampButtonX(
		LabelSide == HotspotLabelSide.Left
			? DisplayButtonPointX - ButtonWidth - ButtonLineSpacing
			: DisplayButtonPointX + ButtonLineSpacing,
		ButtonWidth,
		_canvasWidth);

	/// <summary>Keeps a hotspot's label box inside the canvas it is drawn on.</summary>
	/// <remarks>
	/// Pulling the anchors inward (see <see cref="TransformX"/>) is not enough on its own, because
	/// the box itself grows with its content: <see cref="ButtonWidth"/> is measured from the bound
	/// macro's name, and a left-side label is laid out leftwards from its anchor. A long enough name
	/// therefore walks the box off the canvas, where the panel clips it — which is how a label read
	/// as "e Forward" with half the macro name missing. Sliding it back is the only option that
	/// keeps the text: the box cannot shrink without truncating what it is there to show.
	///
	/// A box wider than the whole canvas is pinned to the left edge rather than given a negative
	/// position, so it loses its right end — where the ✕ sits — instead of its beginning.
	/// </remarks>
	internal static double ClampButtonX(double x, double width, double canvasWidth) =>
		width >= canvasWidth ? 0 : Math.Clamp(x, 0, canvasWidth - width);
	public double ButtonY => ButtonPoint.Y - (ButtonHeight / 2);

	public double TargetX => DisplayTargetPointX - MarkerRadius;
	public double TargetY => TargetPoint.Y - MarkerRadius;
	public double ConnectorPointX => ConnectorStartX - ConnectorPointRadius;
	public double ConnectorPointY => ButtonPoint.Y - ConnectorPointRadius;
	public double ConnectorPointSize => ConnectorPointRadius * 2;

	private double DisplayButtonPointX =>
		ResolveAnchorX(TransformX(ButtonPoint.X), DisplayTargetPointX, LabelSide);

	/// <summary>Keeps a label's anchor on its own side of the marker it points at.</summary>
	/// <remarks>
	/// The anchor is squeezed toward the centre while the marker only re-centres, so the gap the
	/// design left between them shrinks with the canvas — and once the canvas is narrow enough the
	/// anchor crosses its own marker. The label box then sits ON the ring it labels and the
	/// connector runs backwards from it. TAIPAN is the device that shows it: its side buttons are
	/// drawn 40px from their labels against BLAZE's 104, so they cross at any canvas under ~923 —
	/// which includes 808, what the Macros panel gets at the DEFAULT 1440 window. BLAZE still has
	/// room there, which is why a live check on one mouse missed it (PR #144 M2).
	///
	/// The gap kept here is the marker's own radius plus twice the line spacing: enough that the
	/// connector still leaves the box before it reaches the ring.
	/// </remarks>
	internal static double ResolveAnchorX(double anchor, double marker, HotspotLabelSide side) =>
		side == HotspotLabelSide.Left
			? Math.Min(anchor, marker - MarkerRadius - (ButtonLineSpacing * 2))
			: Math.Max(anchor, marker + MarkerRadius + (ButtonLineSpacing * 2));

	// Translated, never squeezed. A target point marks a physical button ON the artwork, and the
	// artwork is a fixed DeviceImageWidth box that only ever re-centres — it does not narrow with
	// the canvas. Running it through TransformX pulled the markers off the buttons they point at:
	// the two side-button rings slid inward onto the body of the mouse. Only the LABEL anchors
	// compress, because only they need room that the panel no longer has.
	private double DisplayTargetPointX => RecentreX(TargetPoint.X);
	private double ConnectorStartX => LabelSide == HotspotLabelSide.Left
		? ButtonX + ButtonWidth + ButtonLineSpacing
		: ButtonX - ButtonLineSpacing;
	private double ConnectorMidX => (ConnectorStartX + ConnectorTargetBorderX) / 2.0;
	private double ConnectorTargetBorderX => GetConnectorTargetBorderPoint().x;
	private double ConnectorTargetBorderY => GetConnectorTargetBorderPoint().y;

	public bool IsSelected
	{
		get => _isSelected;
		set
		{
			if (SetProperty(ref _isSelected, value))
			{
				OnPropertyChanged(nameof(ConnectorColor));
				OnPropertyChanged(nameof(MarkerStrokeColor));
				OnPropertyChanged(nameof(ButtonBackgroundColor));
			}
		}
	}

	// Macros-page inspection: true while this button is bound to the macro checked in the macro
	// list. Lights the hotspot with the selected-look accents WITHOUT becoming the selected
	// hotspot — IsSelected stays the Buttons flow's state alone.
	private bool _isMacroBindingHighlighted;
	public bool IsMacroBindingHighlighted
	{
		get => _isMacroBindingHighlighted;
		set
		{
			if (SetProperty(ref _isMacroBindingHighlighted, value))
			{
				OnPropertyChanged(nameof(MarkerStrokeColor));
				OnPropertyChanged(nameof(ButtonBackgroundColor));
			}
		}
	}

	private bool IsAccented => IsSelected || IsMacroBindingHighlighted;

	public string ConnectorColor => IsSelected ? "#6357FF" : "#6357FF";
	public string MarkerStrokeColor => IsAccented ? "#6357FF" : "#806357FF";

	// The disabled colours are set here rather than in a `:disabled` style: the button binds
	// Background directly, and a local value outranks a style setter in Avalonia, so a style
	// would never take effect.
	public string ButtonBackgroundColor => !IsAssignable
		? "#171520"
		: IsAccented ? "#2E2471" : "#1E162E";

	public string ButtonLabelColor => IsAssignable ? "#FFFFFF" : "#6B6880";

	// Re-raises the button/connector geometry that derives from the label, macro row, width or
	// height, so the hotspot re-lays-out when its size changes (e.g. a macro badge appears).
	private void RaiseGeometryChanged()
	{
		OnPropertyChanged(nameof(ButtonWidth));
		OnPropertyChanged(nameof(ButtonX));
		OnPropertyChanged(nameof(ButtonY));
		OnPropertyChanged(nameof(ConnectorPathData));
		OnPropertyChanged(nameof(ConnectorPointX));
	}

	public void SetCanvasWidth(double canvasWidth)
	{
		if (Math.Abs(_canvasWidth - canvasWidth) < 0.01)
		{
			return;
		}

		_canvasWidth = canvasWidth;
		OnPropertyChanged(nameof(ConnectorPathData));
		OnPropertyChanged(nameof(ButtonX));
		OnPropertyChanged(nameof(TargetX));
		OnPropertyChanged(nameof(ConnectorPointX));
	}

	/// <summary>The canvas width the hotspot coordinates in the device catalog were laid out against.</summary>
	internal const double DesignCanvasWidth = 1236;
	private const double DesignCenterX = DesignCanvasWidth / 2;

	// Label anchors: squeezed toward the centre so the boxes hanging off them keep their room.
	private double TransformX(double x)
	{
		if (_canvasWidth >= DesignCanvasWidth)
		{
			return x;
		}

		var scale = _canvasWidth / DesignCanvasWidth;
		return ((x - DesignCenterX) * scale) + (_canvasWidth / 2);
	}

	// Artwork-space points: moved with the artwork's centre and nothing more.
	private double RecentreX(double x)
	{
		if (_canvasWidth >= DesignCanvasWidth)
		{
			return x;
		}

		return x - DesignCenterX + (_canvasWidth / 2);
	}

	private (double x, double y) GetConnectorTargetBorderPoint()
	{
		var dx = DisplayButtonPointX - DisplayTargetPointX;
		var dy = ButtonPoint.Y - TargetPoint.Y;
		var length = Math.Sqrt((dx * dx) + (dy * dy));

		if (length < 0.001)
		{
			return (DisplayTargetPointX, TargetPoint.Y);
		}

		return (
			DisplayTargetPointX + (dx / length) * MarkerRadius,
			TargetPoint.Y + (dy / length) * MarkerRadius);
	}
}
