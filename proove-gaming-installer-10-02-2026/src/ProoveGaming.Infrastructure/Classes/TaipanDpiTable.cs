using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Infrastructure.Classes;

// Encodes the TAIPAN (Telink) DPI stage table — command 0x02 write / 0x42 read.
//
// Layout (from the vendor's worked full-table write):
//   [0]                    max stage count
//   [1 + i*5 .. +5]        stage i: [enable, X_L, X_H, Y_L, Y_H]  (little-endian, 8 stages)
//
// DPI values are stored as a register = CPI / 50: the vendor example's stage values
// (0x0009, 0x0012, 0x002A, 0x0056, 0x008F, 0x018F) are all exact ×50 multiples as DPI, and the
// app's stage set encodes cleanly the same way (800→0x0010 … 12000→0x00F0). The UI carries one
// value per stage, so the same register is written to both the X and Y axes.
//
// The per-axis "double" flag and per-stage RGB in the older address-table doc are NOT part of the
// current-firmware layout above (see the Conflicts section of docs/mouse/taipan/protocol.md);
// per-stage indicator colours live in the separate DPI-colour table (0x0C) and are display-only.
//
// Pure/testable — the register conversion and byte layout are the whole DPI write path.
public static class TaipanDpiTable
{
	public const byte DpiCommand = 0x02;
	public const byte ReadCommand = 0x42;

	public const int MaxStages = 8;
	public const int BytesPerStage = 5;
	public const int StageCountOffset = 0;
	public const int StagesOffset = 1;
	public const int Length = StagesOffset + MaxStages * BytesPerStage; // 41

	// Firmware stores CPI in 50-DPI units.
	public const int CpiUnit = 50;

	private const byte StageEnabled = 0x01;
	private const byte StageDisabled = 0x00;

	// CPI → the firmware's register value (CPI / 50), clamped to the 16-bit field.
	public static ushort DpiRegister(int cpi)
	{
		var units = (int)Math.Round(cpi / (double)CpiUnit, MidpointRounding.AwayFromZero);
		return (ushort)Math.Clamp(units, 0, ushort.MaxValue);
	}

	// Inverse of DpiRegister — decodes a register read back from the device (0x42) into CPI.
	public static int CpiFromRegister(ushort register) => register * CpiUnit;

	// Builds the complete 41-byte DPI table. The whole table is always emitted because the write's
	// first (0xD1) packet erases the sector — stages beyond the supplied set are written disabled
	// and zeroed rather than left to chance.
	public static byte[] BuildTable(IReadOnlyList<DpiStageParams> stages)
	{
		var table = new byte[Length];
		var written = Math.Min(stages?.Count ?? 0, MaxStages);
		table[StageCountOffset] = (byte)Math.Clamp(written, 0, MaxStages);

		for (var i = 0; i < MaxStages; i++)
		{
			var offset = StagesOffset + i * BytesPerStage;
			if (i >= written)
			{
				table[offset] = StageDisabled;
				continue;
			}

			var register = DpiRegister(stages![i].DpiValue);
			var lo = (byte)(register & 0xFF);
			var hi = (byte)(register >> 8);

			table[offset] = StageEnabled;
			table[offset + 1] = lo; // X
			table[offset + 2] = hi;
			table[offset + 3] = lo; // Y mirrors X — the UI carries one value per stage
			table[offset + 4] = hi;
		}

		return table;
	}
}
