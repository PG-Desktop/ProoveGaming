using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using ProoveGaming.App.ViewModels;
using ProoveGaming.App.Models.Keyboards;

namespace ProoveGaming.App.Views.DeviceDetailsSections;

internal partial class RasterKeyboardOverlayView : UserControl
{
	/// <summary>
	/// When true, the overlay renders <see cref="DeviceDetailsViewModel.RemapTargetKeys"/>
	/// instead of <see cref="DeviceDetailsViewModel.PhysicalKeys"/> — the Key Assignment
	/// Dialog's KEYS tab, where clicking a key picks a remap target rather than selecting a
	/// physical key.
	/// </summary>
	public static readonly StyledProperty<bool> IsTargetPickerProperty =
		AvaloniaProperty.Register<RasterKeyboardOverlayView, bool>(nameof(IsTargetPicker));

	public bool IsTargetPicker
	{
		get => GetValue(IsTargetPickerProperty);
		set => SetValue(IsTargetPickerProperty, value);
	}

	// ── Hover tooltip geometry ────────────────────────────────────────────────
	// Fractions of the stage width, each derived from the design's pixel value over the 628px
	// host width: the tip draws in canvas units inside the shared Viewbox, and the boards'
	// artwork viewBoxes differ by ~18%, so a hardcoded pixel size would render a different
	// tip per board.
	// The design is a black chip: 14px side padding, 12px text, 8px gap to the keycap, 24px glow.
	private const double TipFontSizeRatio = 0.0194;      // ≈ 12 px
	private const double TipPaddingXRatio = 0.0223;      // 14 px
	private const double TipPaddingYRatio = 0.0127;      // 8 px
	private const double TipLineSpacingRatio = 0.0032;   // 2 px between name and value
	private const double TipKeyGapRatio = 0.0127;        // 8 px, tip to keycap
	private const double TipGlowBlurRatio = 0.0382;      // 24 px
	// Proportional line box (the app-wide TextBlock style pins an absolute 24, authored for
	// 16px screen text — inherited into canvas space it clips the glyphs).
	private const double TipLineHeightRatio = 1.25;
	// The unassign ✕ on a macro-bound key's tip: the 20px the old on-key badge used (it has to
	// sit inside a chip only a little taller than the 12px text), and the badge's 8px gap
	// between the name and the ✕.
	private const double TipUnassignSizeRatio = 0.03185; // 20 px
	private const double TipUnassignGapRatio = 0.0127;   // 8 px, name to ✕

	private DeviceDetailsViewModel? _boundVm;
	private ObservableCollection<KeyAssignmentOptionViewModel>? _boundKeys;

	// Track per-key subscriptions so we can detach cleanly on rebuild.
	private readonly Dictionary<KeyAssignmentOptionViewModel, Button> _buttonByKey = new();

	// The key the visible tip belongs to (the ✕ acts on it), and the grace timer that keeps the
	// tip alive while the pointer travels from the key into it — hiding on the key's PointerExited
	// alone would dismiss the ✕ before it could ever be clicked.
	private KeyAssignmentOptionViewModel? _tipKey;
	private Avalonia.Threading.DispatcherTimer? _tipHideTimer;

	public RasterKeyboardOverlayView()
	{
		InitializeComponent();
		DataContextChanged += OnDataContextChanged;
		// Self-subscription (handler and target are the same object — no leak, no unsubscribe):
		// this is what turns a dirty, hidden overlay into a built one the moment a layout pass
		// first includes it. LayoutUpdated may well fire on every layout pass of the window
		// regardless of this control's visibility (it registers with the root's LayoutManager) —
		// the guard-first handler makes hidden-time firings a two-boolean no-op either way.
		LayoutUpdated += OnLayoutUpdated;
		// Same no-leak self-subscriptions: the tip's ✕ and the enter/exit pair that keeps the
		// tip open while the pointer is over it (see ScheduleTipHide).
		TipUnassign.Click += OnTipUnassignClick;
		TipPanel.PointerEntered += OnTipPointerEntered;
		TipPanel.PointerExited += OnTipPointerExited;
		// A press on the tip's chrome (a near-miss of the ✕) must not bubble to the keyboard
		// panel's empty-space handler and silently drop the key selection — the tip is not empty
		// space. The ✕ itself never reaches this: Button handles its own PointerPressed, so its
		// Click is unaffected. Only relevant while the tip is hit-testable at all (showUnassign).
		TipPanel.PointerPressed += (_, e) => e.Handled = true;
	}

	private void OnDataContextChanged(object? sender, EventArgs e) => Rebind();

	protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
	{
		base.OnPropertyChanged(change);

		// XAML may set the mode before or after the DataContext lands — re-resolve either way.
		if (change.Property == IsTargetPickerProperty)
			Rebind();
	}

	private void Rebind()
	{
		if (_boundKeys is not null)
			_boundKeys.CollectionChanged -= OnKeysChanged;

		_boundVm = DataContext as DeviceDetailsViewModel;
		_boundKeys = _boundVm is null
			? null
			: IsTargetPicker ? _boundVm.RemapTargetKeys : _boundVm.PhysicalKeys;
		if (_boundKeys is null) return;

		_boundKeys.CollectionChanged += OnKeysChanged;
		QueueRebuild();
	}

	private void OnKeysChanged(object? sender, NotifyCollectionChangedEventArgs e) => QueueRebuild();

	// LoadKeyLayoutForDevice repopulates the bound collection with one Clear and ~a hundred Adds,
	// each raising CollectionChanged. Rebuilding inline on every event made that O(n²) — each Add
	// re-created every button built so far, and with four live overlay instances a keyboard's
	// Details-open spent ~2 s in this class alone ([NavPerf] prologue=1913ms on Rampage).
	// Coalescing to one posted rebuild per mutation batch is a single O(n) pass that runs after
	// the storm settles — at Default (= Normal) priority, which is above Render, so it still
	// lands before the next frame and a visible keyboard never paints blank.
	//
	// On top of that, a rebuild only happens while the overlay is EFFECTIVELY visible — none of
	// the four instances is on screen when Details opens (the initial section is Presets), so an
	// invisible overlay just marks itself dirty and OnLayoutUpdated builds it on the first layout
	// pass that shows it. IsEffectivelyVisible, not IsVisible: three of the four instances are
	// hidden by ancestor panels (section switches, the dialog), not by their own flag.
	private bool _rebuildQueued;
	private bool _rebuildDirty;

	private void QueueRebuild()
	{
		if (!IsEffectivelyVisible)
		{
			MarkDirtyWhileHidden();
			return;
		}

		if (_rebuildQueued) return;
		_rebuildQueued = true;
		Avalonia.Threading.Dispatcher.UIThread.Post(() =>
		{
			_rebuildQueued = false;
			// Hidden between the queue and the tick (section switched away): stay dirty and
			// let OnLayoutUpdated pick it up when the overlay next shows.
			if (!IsEffectivelyVisible)
			{
				MarkDirtyWhileHidden();
				return;
			}
			RebuildKeyboard();
		});
	}

	// Going dirty while hidden also drops the stale canvas immediately: its buttons belong to the
	// previous key set, and their VMs would otherwise stay pinned (and PropertyChanged-subscribed)
	// through _buttonByKey until the eventual rebuild. First show of the section then briefly
	// hosts an empty canvas rather than another board's leftovers.
	private void MarkDirtyWhileHidden()
	{
		_rebuildDirty = true;

		if (_buttonByKey.Count == 0) return;
		foreach (var kv in _buttonByKey)
			kv.Key.PropertyChanged -= OnKeyPropertyChanged;
		_buttonByKey.Clear();
		KeyboardCanvas.Children.Clear();
		HideTipNow();
	}

	// Guard-first O(1): this fires on every layout pass that includes the control.
	private void OnLayoutUpdated(object? sender, EventArgs e)
	{
		if (!_rebuildDirty || !IsEffectivelyVisible) return;
		_rebuildDirty = false;
		QueueRebuild();
	}

	private void RebuildKeyboard()
	{
		// A completed rebuild satisfies any dirty mark set earlier in the same batch — without
		// this, OnLayoutUpdated would schedule one more (idempotent but wasted) rebuild.
		_rebuildDirty = false;

		if (_boundVm is null || _boundKeys is null) return;

		foreach (var kv in _buttonByKey)
			kv.Key.PropertyChanged -= OnKeyPropertyChanged;

		_buttonByKey.Clear();
		KeyboardCanvas.Children.Clear();

		// Boards with measured artwork bounds draw their overlays on the rendered keyboard, sized to
		// that artwork's own viewBox. Everything else falls back to the bare canvas its abstract
		// layout was authored against, at the size the view model reports — the supported boards
		// differ in size, so neither the layout nor the size can be hardcoded here.
		var useArtwork = _boundVm.UseArtworkKeyOverlays;
		var artBounds = _boundVm.ArtworkKeyBounds;
		var stageWidth = useArtwork ? _boundVm.ArtworkViewBoxWidth : _boundVm.OverlayCanvasWidth;
		var stageHeight = useArtwork ? _boundVm.ArtworkViewBoxHeight : _boundVm.OverlayCanvasHeight;

		Stage.Width = stageWidth;
		Stage.Height = stageHeight;
		KeyboardCanvas.Width = stageWidth;
		KeyboardCanvas.Height = stageHeight;
		ArtLayer.IsVisible = useArtwork;
		ArtLayer.Width = stageWidth;
		ArtLayer.Height = stageHeight;
		TipLayer.Width = stageWidth;
		TipLayer.Height = stageHeight;
		// A rebuild replaces the buttons the tip was anchored to — never leave it floating.
		HideTipNow();

		foreach (var key in _boundKeys)
		{
			// Direct Button with explicit Canvas.Left/Top from the key's own bounds — no
			// ItemsControl container, no visual-tree indirection, no chance of a container
			// picking up a neighbour's DataContext.
			// Artwork bounds are measured per keycap; the abstract layout's are unit-grid.
			var art = useArtwork && artBounds.TryGetValue(key.DevKeyName, out var b) ? b : null;
			if (useArtwork && art is null)
				continue;   // key the artwork doesn't draw (e.g. FN) — no overlay for it

			var button = new Button
			{
				Width = art?.Width ?? key.PixelWidth,
				Height = art?.Height ?? key.PixelHeight,
				DataContext = key,
				Command = key.SelectCommand,
				CommandParameter = key,
				// Pointer-only: the keyboard path into this surface is the PHYSICAL keyboard
				// (type-to-remap), so keyboard focus on a tile serves nothing — and a focused
				// tile is a landmine: Space/Enter "click" it, and a press leaking through the
				// key-dialog handlers (e.g. during the in-flight write, review round 4 S1)
				// silently reopens the dialog.
				Focusable = false,
				// Over the artwork the overlay is just the highlight box — the keycap legends
				// are already drawn in the SVG, so a label here would double them up. The bare
				// canvas fallback still needs its own text, since nothing else names the keys.
				Content = art is null
					? new TextBlock
					{
						Text = key.KeyLabel,
						TextWrapping = Avalonia.Media.TextWrapping.Wrap,
						TextAlignment = Avalonia.Media.TextAlignment.Center,
						VerticalAlignment = VerticalAlignment.Center,
					}
					: null,
			};
			button.Classes.Add("rasterKey");
			if (key.IsSelected) button.Classes.Add("selected");
			if (key.IsMacroBindingHighlighted) button.Classes.Add("macroBound");
			if (key.IsExtendedKeyHighlighted) button.Classes.Add("extendedKey");

			// Hover tip wiring — section pages only. The dialog's target picker goes without:
			// the tip names the key you would pick, but the dialog's hint already says that,
			// and the chip floating over the dialog chrome reads as clutter there. Buttons
			// never outlive a rebuild (the canvas is cleared and they
			// are recreated), so these handlers need no explicit detach — same as Command above.
			if (!IsTargetPicker)
			{
				button.PointerEntered += OnKeyPointerEntered;
				button.PointerExited += OnKeyPointerExited;
			}

			Canvas.SetLeft(button, art?.X ?? key.CanvasX);
			Canvas.SetTop(button, art?.Y ?? key.CanvasY);

			key.PropertyChanged += OnKeyPropertyChanged;
			_buttonByKey[key] = button;
			KeyboardCanvas.Children.Add(button);
		}
	}

	private void OnKeyPointerEntered(object? sender, PointerEventArgs e)
	{
		if (sender is not Button button) return;
		if (button.DataContext is not KeyAssignmentOptionViewModel key) return;
		CancelTipHide();
		ShowKeyTip(key, button);
	}

	// Never hides directly: the pointer may be on its way INTO the tip (to its ✕), and the
	// key's exit fires the moment it leaves the keycap. The grace timer gives it time to
	// arrive; OnTipPointerEntered cancels the hide when it does.
	private void OnKeyPointerExited(object? sender, PointerEventArgs e) =>
		ScheduleTipHide();

	private void OnTipPointerEntered(object? sender, PointerEventArgs e) => CancelTipHide();

	private void OnTipPointerExited(object? sender, PointerEventArgs e) => ScheduleTipHide();

	private void ScheduleTipHide()
	{
		if (_tipHideTimer is null)
		{
			_tipHideTimer = new Avalonia.Threading.DispatcherTimer
			{
				Interval = TimeSpan.FromMilliseconds(150),
			};
			// IsPointerOver re-check: enter/exit ordering between the key button and the tip
			// is an implementation detail of the input pipeline — if an exit's schedule ever
			// lands after the tip's enter, the timer must still not pull the tip out from
			// under the pointer.
			_tipHideTimer.Tick += (_, _) =>
			{
				_tipHideTimer!.Stop();
				if (!TipPanel.IsPointerOver)
					HideTipNow();
			};
		}

		_tipHideTimer.Stop();
		_tipHideTimer.Start();
	}

	private void CancelTipHide() => _tipHideTimer?.Stop();

	private void HideTipNow()
	{
		CancelTipHide();
		TipPanel.IsVisible = false;
		// Back to fully click-through, so a hidden tip can never block key hover/clicks.
		TipLayer.IsHitTestVisible = false;
		_tipKey = null;
	}

	private void OnTipUnassignClick(object? sender, RoutedEventArgs e)
	{
		if (_boundVm is null || _tipKey is null) return;
		var key = _tipKey;
		// The tip names a binding that is about to drop — stale the moment the command runs.
		HideTipNow();
		_boundVm.UnassignMacroFromKeyCommand.Execute(key);
	}

	/// <summary>
	/// Places the hover tooltip above the key: its name, and — when the device supports
	/// actuation — the key's press actuation underneath. Without actuation the value line
	/// collapses and the tip is just the shorter name chip. A macro-bound key's tip also
	/// carries the unassign ✕ (and only then is the tip hit-testable at all).
	/// </summary>
	private void ShowKeyTip(KeyAssignmentOptionViewModel key, Button button)
	{
		if (_boundVm is null) return;

		var stageWidth = Stage.Width;
		if (double.IsNaN(stageWidth) || stageWidth <= 0) return;

		var fontSize = stageWidth * TipFontSizeRatio;
		var lineHeight = fontSize * TipLineHeightRatio;
		var padX = stageWidth * TipPaddingXRatio;
		var padY = stageWidth * TipPaddingYRatio;
		var spacing = stageWidth * TipLineSpacingRatio;
		var keyGap = stageWidth * TipKeyGapRatio;

		// A key inside an extended-key record names the record ("Rapid Snap · D"); a remapped key
		// is named by what it now resolves to (the target key or the bound macro's name) — the
		// keycap legend alone would claim the key still types its own letter. An untouched key
		// keeps its own label.
		var label = _boundVm.GetExtendedKeyTooltip(key) ?? _boundVm.GetKeyRemapTooltip(key) ?? key.KeyLabel;
		TipLabel.Text = label;
		TipLabel.FontSize = fontSize;
		TipLabel.LineHeight = lineHeight;

		var actuation = _boundVm.GetKeyActuationTooltip(key);
		TipValue.IsVisible = actuation is not null;
		TipValue.Text = actuation;
		TipValue.FontSize = fontSize;
		TipValue.LineHeight = lineHeight;

		// Macro-bound keys get the unassign ✕ beside the name, and with it a hit-testable
		// tip (plus the gap filler bridging chip and keycap) so the ✕ is reachable. A plain
		// tip stays fully click-through, exactly as before the ✕ existed.
		//
		// Only on the Macros / Key Assignments sections: the Actuation page renders this
		// overlay too (Per-key mode), but its selection is a different model riding the same
		// IsSelected flags — an ✕ there would unassign from a page that has no business
		// doing so, and its command's selection move would desync the actuation selection
		// (UnassignMacroFromKey carries the matching guard).
		//
		// And not for a key the unassign would refuse: a slot that carries both a macro binding
		// and an extended-key record (only a pulled preset can make one) would have its record
		// orphaned by the reset, so the command bounces it — showing the ✕ there advertises an
		// action that will only raise the blocked banner. Same while this open's advanced-key
		// probe is still running, when no verdict about the slot exists yet (review round 3, S7).
		var showUnassign = _boundVm.KeyHasBoundMacro(key) &&
			(_boundVm.IsMacrosSection || _boundVm.IsButtonsSection) &&
			!_boundVm.IsExtendedKeyWriteBlocked(key);
		var unassignSize = stageWidth * TipUnassignSizeRatio;
		var unassignGap = stageWidth * TipUnassignGapRatio;
		TipUnassign.IsVisible = showUnassign;
		if (showUnassign)
		{
			TipUnassign.Width = unassignSize;
			TipUnassign.Height = unassignSize;
			TipUnassign.Margin = new Thickness(unassignGap, 0, 0, 0);
			// The same in-flight gate every unassign affordance carries (PR #81 review M1),
			// evaluated at show time: it only ever gates the key whose assignment write is
			// still running, i.e. the selected one. The command re-checks on click, so a
			// stale-enabled ✕ still cannot race the write — this just keeps the button's
			// look honest for the common hover-right-after-assigning case.
			TipUnassign.IsEnabled =
				!ReferenceEquals(key, _boundVm.SelectedPhysicalKey) ||
				_boundVm.CanResetSelectedMacroTarget;
		}

		TipBody.Margin = new Thickness(padX, padY);
		TipContent.Spacing = spacing;
		TipGapFiller.Height = keyGap;
		if (TipChrome.Effect is Avalonia.Media.DropShadowEffect glow)
			glow.BlurRadius = stageWidth * TipGlowBlurRatio;

		// Centred over the keycap, sitting above it. The two estimates split the work: the
		// AVERAGE estimate centres (a few pixels off just nudges the chip off-centre), the
		// UPPER-BOUND estimate feeds the right-edge clamp, which is the one consumer that
		// cannot absorb an underestimate. The ✕ is exact, not estimated, so it joins both.
		var textHeight = lineHeight + (actuation is null ? 0 : spacing + lineHeight);
		var chipHeight = (padY * 2) + Math.Max(textHeight, showUnassign ? unassignSize : 0);
		var unassignWidth = showUnassign ? unassignGap + unassignSize : 0;
		var tipWidth = (padX * 2) + unassignWidth + Math.Max(
			LabelWidthEstimator.Estimate(label, fontSize),
			actuation is null ? 0 : LabelWidthEstimator.Estimate(actuation, fontSize));
		var tipMaxWidth = (padX * 2) + unassignWidth + Math.Max(
			LabelWidthEstimator.EstimateUpperBound(label, fontSize),
			actuation is null ? 0 : LabelWidthEstimator.EstimateUpperBound(actuation, fontSize));

		var left = Canvas.GetLeft(button) + (button.Width / 2) - (tipWidth / 2);
		left = Math.Clamp(left, 0, Math.Max(0, stageWidth - tipMaxWidth));

		// Always above the key, even past the stage top on the ESC / function rows — the
		// design shows exactly that. What used to clip it there was the Viewbox's own
		// default clip, disabled in the markup (see the comment on it); the hosts all
		// centre the keyboard in a panel with ample headroom, so nothing else cuts it.
		// The chip lands where it always has; the transparent gap filler under it spans
		// down to the keycap so a hit-testable tip and the key form one hover region.
		Canvas.SetLeft(TipPanel, left);
		Canvas.SetTop(TipPanel, Canvas.GetTop(button) - keyGap - chipHeight);
		_tipKey = key;
		TipLayer.IsHitTestVisible = showUnassign;
		TipPanel.IsVisible = true;
	}

	private void OnKeyPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		if (sender is not KeyAssignmentOptionViewModel key) return;
		if (!_buttonByKey.TryGetValue(key, out var button)) return;

		if (e.PropertyName == nameof(KeyAssignmentOptionViewModel.IsSelected))
			SetButtonClass(button, "selected", key.IsSelected);
		else if (e.PropertyName == nameof(KeyAssignmentOptionViewModel.IsMacroBindingHighlighted))
			SetButtonClass(button, "macroBound", key.IsMacroBindingHighlighted);
		else if (e.PropertyName == nameof(KeyAssignmentOptionViewModel.IsExtendedKeyHighlighted))
			SetButtonClass(button, "extendedKey", key.IsExtendedKeyHighlighted);
	}

	private static void SetButtonClass(Button button, string name, bool present)
	{
		if (present)
		{
			if (!button.Classes.Contains(name)) button.Classes.Add(name);
		}
		else
		{
			button.Classes.Remove(name);
		}
	}
}
