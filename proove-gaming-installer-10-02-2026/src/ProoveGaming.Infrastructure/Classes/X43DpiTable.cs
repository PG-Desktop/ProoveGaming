using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Infrastructure.Classes;

// Encodes the DPI stage table into param_1 block 0 for the BLAZE / X43. The block holds the
// stage count at byte 4 and each stage's CPI as a little-endian ushort, stored TWICE (the
// firmware keeps a mirror copy): primary table at bytes 9.., mirror at bytes 25.., 8 stages
// of 2 bytes each. Only these bytes are touched — the caller passes the device's current
// block 0 so LED / other settings are preserved.
//
// Unused stages carry a VALID DPI, not zero. A zero entry makes the firmware reject the whole
// param_1 write, which silently took the LED (block 1) down with it — the cause of effects
// applying only intermittently. The OEM's own block 0 carries 2000 in the unused 8th slot
// (cap-06.08.26-for-blaze*.pcapng, bytes 23-24 = D0 07) while ours carried 00 00.
//
// Pure/testable — the offsets, the two-table mirror and the little-endian split are the
// critical DPI write path. Baseline: docs/mouse/blaze/dpi-stage.md.
public static class X43DpiTable
{
	public const int MaxStages = 8;
	public const int StageCountOffset = 4;
	public const int PrimaryTableOffset = 9;
	public const int MirrorTableOffset = 25;

	// DPI written into slots past the configured stage count. Never zero — see the note above.
	// The value is the OEM's own, taken from a capture where its writes applied reliably.
	public const ushort UnusedStageDpi = 2000;

	// Patches block0 in place: writes the stage count and both DPI tables. `stages` beyond
	// MaxStages are ignored; trailing slots get UnusedStageDpi.
	public static void ApplyStages(byte[] block0, IReadOnlyList<DpiStageParams> stages)
	{
		var written = Math.Min(stages.Count, MaxStages);
		block0[StageCountOffset] = (byte)Math.Clamp(stages.Count, 1, MaxStages);

		for (var i = 0; i < MaxStages; i++)
		{
			var dpi = i < written ? stages[i].DpiValue : UnusedStageDpi;
			var lo = (byte)(dpi & 0xFF);
			var hi = (byte)(dpi >> 8);
			block0[PrimaryTableOffset + i * 2] = lo;
			block0[PrimaryTableOffset + i * 2 + 1] = hi;
			block0[MirrorTableOffset + i * 2] = lo;
			block0[MirrorTableOffset + i * 2 + 1] = hi;
		}
	}
}
