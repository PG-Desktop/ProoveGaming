namespace ProoveGaming.Infrastructure.Classes;

/// <summary>
/// The decoded 64-byte Device Info block (<c>0x12</c>) of a Smart Infinite keyboard.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="KeyCount"/> is the field that decides whether key remapping is available at all, and
/// it is not a documentation detail — it is measured. Cyrex reports 68 and accepts key-matrix writes;
/// Rampage reports 0 and discards them while still accepting configuration writes on the same
/// interface. Reading this block first is therefore the difference between remapping a keyboard and
/// silently failing on one.
/// </para>
/// <para>
/// The count cross-checks against the table itself: Cyrex declares a 75-slot matrix of which 7 slots
/// are unused placeholders, and 75 − 7 = 68 exactly.
/// </para>
/// <para>
/// The magnetic-axis fields (bytes 7, 16-46) describe the key travel area — slot count, step unit,
/// per-key record size, adjustable ranges and factory defaults. Extracted from the vendor's
/// Keyborder-v3 driver app (see
/// <c>docs/knowledge-base/smart-infinite-protocol/magnetic-axis-travel.md</c>); a mechanical board
/// leaves them zero, which <see cref="SmartInfiniteTravelArea.ValidateGeometry"/> treats as
/// "no actuation support".
/// </para>
/// <para>
/// Byte 8 is the advanced-key storage area size in 64-byte units (<c>advancedKeyStorageAreasSize</c>
/// in the vendor app); <see cref="AdvancedKeyAreaBytes"/> is that value × 64 and zero on a board
/// without the feature. See
/// <c>docs/knowledge-base/smart-infinite-protocol/advanced-keys-rapid-snap.md</c>.
/// </para>
/// </remarks>
public sealed record SmartInfiniteDeviceInfo(
	bool IsValid,
	int ProductId,
	int MatrixSlots,
	int MacroBytes,
	int KeyCount,
	int DeviceType,
	int ProductVersion,
	int MaxBrightness,
	int MaxSpeed,
	int TravelSlots,
	int TravelRecordBytes,
	int TravelStepMicrometres,
	int PressMaxSteps,
	int PressMinSteps,
	int RtMaxSteps,
	int RtMinSteps,
	int DefaultPressSteps,
	int DefaultReleaseSteps,
	bool DefaultRapidTrigger,
	int DefaultQuickPressSteps,
	int DefaultQuickReleaseSteps,
	int AdvancedKeyAreaBytes)
{
	/// <summary>Bytes the key matrix occupies on this model (<see cref="MatrixSlots"/> × 3).</summary>
	public int MatrixBytes => MatrixSlots * SmartInfiniteProtocol.MatrixSlotSize;

	/// <summary>Bytes the key travel area occupies (<see cref="TravelSlots"/> × <see cref="TravelRecordBytes"/>).</summary>
	public int TravelAreaBytes => TravelSlots * TravelRecordBytes;

	/// <summary>
	/// True when the board has an advanced-key storage area (<see cref="AdvancedKeyAreaBytes"/> &gt; 0)
	/// and accepts key-matrix writes — the gate for the Extended Keys feature. A board that fails it
	/// reports "unsupported" and is never written to.
	/// </summary>
	public bool SupportsExtendedKeys => SupportsKeyRemap && AdvancedKeyAreaBytes > 0;

	/// <summary>
	/// True when the firmware reports remappable keys. False means the feature is not enabled in
	/// this build and writes will be acknowledged but discarded.
	/// </summary>
	public bool SupportsKeyRemap => IsValid && KeyCount > 0 && MatrixSlots > 0;

	/// <summary>Human-readable transport summary from the device-type field.</summary>
	public string DeviceTypeLabel => DeviceType switch
	{
		0 => "USB",
		1 => "USB + Bluetooth",
		2 => "USB + 2.4G",
		3 => "USB + Bluetooth + 2.4G",
		_ => $"unknown ({DeviceType})",
	};

	/// <summary>
	/// Decodes the block. Returns a record with <see cref="IsValid"/> false when the
	/// <c>0xAA 0xBB</c> marker is absent, which means the layout is not the one documented here and
	/// nothing below should be trusted.
	/// </summary>
	public static SmartInfiniteDeviceInfo Parse(ReadOnlySpan<byte> block)
	{
		if (block.Length < 16)
			return new SmartInfiniteDeviceInfo(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, 0, 0, 0);

		bool valid = block[0] == 0xAA && block[1] == 0xBB;
		bool hasMagnetic = block.Length >= 47;

		// Byte 16: bit 7 selects the step factor (0 -> 10 um = 0.01 mm, 1 -> 1 um = 0.001 mm), the
		// low 7 bits are the multiplier. Cyrex reports 0x01 -> 10 um.
		int stepMicrometres = hasMagnetic
			? (block[16] & 0x7F) * ((block[16] & 0x80) != 0 ? 1 : 10)
			: 0;

		// Byte 44 is the per-key record size in the travel area; the vendor app maps 0 to 10 (the
		// no-top-dead-zone layout) and takes anything else as reported (12 = has a top dead zone).
		int travelRecordBytes = hasMagnetic
			? (block[44] == 0 ? 10 : block[44])
			: 0;

		return new SmartInfiniteDeviceInfo(
			IsValid: valid,
			ProductId: block[2] | (block[3] << 8),
			MatrixSlots: block[4],
			MacroBytes: block[5] * 256,
			KeyCount: block[6],
			DeviceType: block[9],
			ProductVersion: block[10] | (block[11] << 8),
			MaxBrightness: block[12],
			MaxSpeed: block[13],
			TravelSlots: hasMagnetic ? block[7] : 0,
			TravelRecordBytes: travelRecordBytes,
			TravelStepMicrometres: stepMicrometres,
			PressMaxSteps: hasMagnetic ? block[19] | (block[20] << 8) : 0,
			PressMinSteps: hasMagnetic ? block[21] | (block[22] << 8) : 0,
			RtMaxSteps: hasMagnetic ? block[23] | (block[24] << 8) : 0,
			RtMinSteps: hasMagnetic ? block[25] | (block[26] << 8) : 0,
			DefaultPressSteps: hasMagnetic ? block[29] | (block[30] << 8) : 0,
			DefaultReleaseSteps: hasMagnetic ? block[31] | (block[32] << 8) : 0,
			DefaultRapidTrigger: hasMagnetic && block[33] != 0,
			DefaultQuickPressSteps: hasMagnetic ? block[37] | (block[38] << 8) : 0,
			DefaultQuickReleaseSteps: hasMagnetic ? block[39] | (block[40] << 8) : 0,
			AdvancedKeyAreaBytes: block[8] * 64);
	}

	public override string ToString() =>
		IsValid
			? $"productId=0x{ProductId:X4} matrix={MatrixSlots} slots ({MatrixBytes} B) macro={MacroBytes} B " +
			  $"keys={KeyCount} transport={DeviceTypeLabel} fw=0x{ProductVersion:X4} maxBri={MaxBrightness} maxSpd={MaxSpeed} " +
			  $"travel={TravelSlots}x{TravelRecordBytes} B step={TravelStepMicrometres} um advKeys={AdvancedKeyAreaBytes} B"
			: "invalid (missing 0xAA 0xBB marker)";
}
