using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ProoveGaming.App.ViewModels;

/// <summary>
/// One row in the KeyAssignments UI. Used for both the target list (left panel) and each
/// clickable key overlay on the on-screen keyboard (right panel).
/// </summary>
public sealed class KeyAssignmentOptionViewModel : ObservableObject
{
	// Stable identifier that maps back to <c>RasterKeyCatalog.Keys[].DevKeyName</c>. Used to
	// persist selections in LocalStorage keyed by (vid, pid, DevKeyName).
	public required string DevKeyName { get; init; }

	// Human-readable label shown to the user (e.g. "A", "SHIFT (L)", "↑").
	public required string KeyLabel { get; init; }

	// Category label used for grouping the target list (Number Row, Alpha Row 1, …).
	public string? Category { get; init; }

	// The 3-byte matrix record we send to the firmware. On Raster / T-Racer MatrixIndex is settable
	// so the runtime probe (ProbeAndUpdateMatrixMapAsync) can overwrite the catalog default with
	// the real slot discovered on that keyboard's firmware. On Rampage the catalog index comes from
	// the vendor's factory table and is already exact, so nothing overwrites it.
	public byte MatrixIndex { get; set; }

	// The catalog's slot for this key, kept as the probe found it — never overwritten.
	//
	// Exists because the probe is fire-and-forget: a key_macro_bindings row written before it
	// lands is keyed by the catalog slot (T-Racer E = 45), while every later read and delete uses
	// the probed one (35). Code that has to find such a row must try both, and it cannot use
	// MatrixIndex as the second candidate — the probe overwrites that in place, so the two
	// expressions collapse to the same value and the guard silently does nothing.
	public byte CatalogMatrixIndex { get; init; }
	public byte TypeByte { get; init; } = 0x10; // HID keyboard record by default.
	// Modifier bitmask for type 0x10. Zero on Raster / T-Racer; on Rampage the modifier keys carry
	// their identity here with ScanCode 0x00, so a key is identified by the pair, never ScanCode
	// alone.
	public byte Bitmask { get; init; }
	public byte ScanCode { get; init; } // HID scancode for type 0x10.

	/// <summary>
	/// Library macro this target binds, or null for an ordinary key target. Macro targets
	/// carry the family's macro type byte in <see cref="TypeByte"/> with the record operands
	/// in <see cref="Bitmask"/> (code1) and <see cref="ScanCode"/> (code2) — and what those
	/// operands mean is per-family: Raster (<c>0x60</c>/<c>0x61</c>) and Smart Infinite
	/// (<c>0x50</c>/<c>0x51</c>) put the slot in code1 and the play mode / repeat count in
	/// code2; T800 (<c>0x09</c>) is the mirror image, play type in code1 and slot in code2.
	/// See "Macro type byte (key record)" in <c>docs/CONTEXT.md</c> for the per-family truth
	/// and its hardware evidence. Either way the operands are record bytes, not a scancode,
	/// so macro targets can never be matched by scancode the way key targets are.
	/// </summary>
	public int? MacroId { get; init; }

	public bool IsMacroTarget => MacroId is not null;

	// Canvas-space bounds for the interactive keyboard overlay (pixels).
	public double CanvasX { get; init; }
	public double CanvasY { get; init; }
	public double PixelWidth { get; init; }
	public double PixelHeight { get; init; }

	private bool _isSelected;
	public bool IsSelected
	{
		get => _isSelected;
		set => SetProperty(ref _isSelected, value);
	}

	// Macros-page inspection: true while this key is bound to the macro the user checked in the
	// macro list. Drives the overlay's "macroBound" highlight box. Kept apart from IsSelected,
	// which is already shared by the Key-Assignments radio and the Actuation per-key select —
	// a third meaning on that flag would bleed across the selection models.
	private bool _isMacroBindingHighlighted;
	public bool IsMacroBindingHighlighted
	{
		get => _isMacroBindingHighlighted;
		set => SetProperty(ref _isMacroBindingHighlighted, value);
	}

	// Extended Keys: true while this key belongs to a configured record (both keys of a Rapid Snap
	// pair). Drives the overlay's "extendedKey" highlight box on every section that renders the
	// overlay. A fourth flag rather than a reuse, for the same reason as the macro one above — the
	// editor's picks ride IsSelected and must not be confused with the configured set.
	private bool _isExtendedKeyHighlighted;
	public bool IsExtendedKeyHighlighted
	{
		get => _isExtendedKeyHighlighted;
		set => SetProperty(ref _isExtendedKeyHighlighted, value);
	}

	public IRelayCommand<KeyAssignmentOptionViewModel>? SelectCommand { get; init; }
}
