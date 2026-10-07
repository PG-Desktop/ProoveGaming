namespace ProoveGaming.Infrastructure.Classes;

/// <summary>
/// One key's assignment as the firmware stores it: three bytes, where <see cref="Type"/> decides how
/// the other two are read. For a plain keyboard key (<c>0x10</c>) <see cref="CodeLow"/> is the
/// modifier bitmask and <see cref="CodeHigh"/> the USB HID usage code — which is why modifier keys
/// such as Left Shift are <c>{0x10, 0x02, 0x00}</c> and cannot be identified by usage code alone.
/// </summary>
public readonly record struct KeyMatrixRecord(byte Type, byte CodeLow, byte CodeHigh)
{
	/// <summary>Plain HID keyboard key.</summary>
	public const byte TypeKeyboardKey = 0x10;

	/// <summary>Custom function (FN modifier, macro trigger, lighting controls, …).</summary>
	public const byte TypeCustomFunction = 0x60;

	/// <summary>All three bytes zero — a slot the firmware has never written.</summary>
	public bool IsZero => Type == 0 && CodeLow == 0 && CodeHigh == 0;

	/// <summary>
	/// A keyboard record that names no key (<c>{0x10, 0x00, 0x00}</c>) — a matrix position the
	/// physical board does not use. Distinct from <see cref="IsZero"/>: on Cyrex all 75 slots carry
	/// a real record, and it is these 7 placeholders that make the difference between the 75-slot
	/// matrix and the 68 keys the firmware reports.
	/// </summary>
	public bool IsUnusedSlot => Type == TypeKeyboardKey && CodeLow == 0 && CodeHigh == 0;

	/// <summary>True for the FN key record.</summary>
	public bool IsFn => Type == TypeCustomFunction && CodeLow == 0x01;

	public override string ToString() => $"{{0x{Type:X2}, 0x{CodeLow:X2}, 0x{CodeHigh:X2}}}";
}
