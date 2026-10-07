using System.Collections.Generic;
using System.Linq;

namespace ProoveGaming.App.Models.Keyboards;

/// <summary>
/// Keycap bounds for the <b>T800</b> artwork, in that artwork's viewBox coordinates, keyed by
/// <b>matrix slot</b>.
/// </summary>
/// <remarks>
/// <para>
/// Measured from the artwork itself rather than placed on an idealised grid, so key overlays can be
/// drawn directly on the rendered board and stay registered to the real keycaps at any scale — the
/// same approach as <see cref="CyrexArtworkLayout"/> and <see cref="TRacerArtworkLayout"/>.
/// </para>
/// <para>
/// Derived by connected-component labelling of the alpha channel of the keycap layer in
/// <c>Assets/Keyboards/PG_T801_keys.svg</c>, then mapped into viewBox space by that image's own
/// placement transform <c>translate(216.208 687.954)</c> <b>less the artwork's crop origin</b>
/// (172, 672). The pass finds 68 keycaps in rows of 15/15/14/14/10, which matches the firmware's
/// factory key table exactly — that agreement between two independent sources is what verifies the
/// slot assignment. FN is excluded: it is not remappable and gets no overlay.
/// </para>
/// <para>
/// Slots are joined by walking each measured row left to right against the factory table. The
/// matrix is column-major, so within a visual row left-to-right is ascending slot number, which is
/// why the join is a plain zip rather than a nearest-centroid match.
/// </para>
/// <para>
/// These numbers are tied to that specific artwork revision. Replacing the keys SVG invalidates
/// every entry here, and nothing detects that automatically — regenerate with
/// <c>docs/plans/add-t800-key-assignments/measure-t800-keycaps.py</c> rather than hand-editing.
/// </para>
/// </remarks>
public static class T800ArtworkLayout
{
	/// <summary>Artwork viewBox width — the bounds below are in these units.</summary>
	public const double ViewBoxWidth = 1742.0;

	/// <summary>Artwork viewBox height.</summary>
	public const double ViewBoxHeight = 600.0;

	/// <summary>Slot → measured keycap bounds. FN is absent.</summary>
	public static IReadOnlyDictionary<byte, RasterKeyBounds> BySlot { get; } =
		new Dictionary<byte, RasterKeyBounds>
		{
			// ── Row 1 ──
			[1] = new(66.195, 32.922, 94.943, 98.816),
			[7] = new(171.132, 31.924, 96.942, 95.821),
			[13] = new(273.071, 32.922, 96.942, 94.823),
			[19] = new(376.010, 31.924, 94.943, 95.821),
			[25] = new(478.948, 31.924, 94.943, 95.821),
			[31] = new(579.888, 31.924, 94.943, 97.818),
			[37] = new(681.827, 31.924, 96.942, 99.814),
			[43] = new(784.765, 31.924, 96.942, 99.814),
			[49] = new(886.704, 31.924, 95.943, 98.816),
			[55] = new(989.643, 31.924, 96.942, 99.814),
			[61] = new(1091.582, 31.924, 97.941, 99.814),
			[67] = new(1194.520, 31.924, 96.942, 98.816),
			[73] = new(1297.458, 31.924, 98.941, 98.816),
			[79] = new(1399.397, 31.924, 197.882, 99.814),
			[85] = new(1601.277, 30.926, 97.941, 100.812),
			// ── Row 2 ──
			[2] = new(64.196, 137.727, 146.912, 95.821),
			[8] = new(218.104, 137.727, 97.941, 96.819),
			[14] = new(321.042, 137.727, 97.941, 97.818),
			[20] = new(423.981, 136.729, 96.942, 97.818),
			[26] = new(525.920, 137.727, 95.943, 96.819),
			[32] = new(628.858, 137.727, 95.943, 96.819),
			[38] = new(729.798, 136.729, 96.942, 97.818),
			[44] = new(831.737, 136.729, 99.940, 98.816),
			[50] = new(935.675, 137.727, 96.942, 97.818),
			[56] = new(1036.614, 137.727, 96.942, 96.819),
			[62] = new(1139.553, 137.727, 95.943, 96.819),
			[68] = new(1241.492, 137.727, 96.942, 96.819),
			[74] = new(1344.430, 137.727, 97.941, 95.821),
			[80] = new(1449.368, 134.732, 148.911, 99.814),
			[86] = new(1603.276, 138.725, 94.943, 97.818),
			// ── Row 3 ──
			[3] = new(66.195, 240.535, 171.897, 95.821),
			[9] = new(244.088, 239.537, 98.941, 97.818),
			[15] = new(348.026, 240.535, 95.943, 93.825),
			[21] = new(449.965, 240.535, 97.941, 93.825),
			[27] = new(552.904, 239.537, 96.942, 94.823),
			[33] = new(654.843, 239.537, 95.943, 94.823),
			[39] = new(755.782, 239.537, 97.941, 95.821),
			[45] = new(858.721, 240.535, 97.941, 93.825),
			[51] = new(961.659, 239.537, 98.941, 94.823),
			[57] = new(1064.598, 240.535, 95.943, 93.825),
			[63] = new(1166.537, 240.535, 96.942, 91.829),
			[69] = new(1270.475, 240.535, 95.943, 90.831),
			[81] = new(1373.413, 238.539, 226.864, 94.823),
			[87] = new(1604.275, 240.535, 94.943, 93.825),
			// ── Row 4 ──
			[4] = new(66.195, 342.345, 215.871, 99.814),
			[16] = new(290.061, 343.343, 99.940, 95.821),
			[22] = new(396.997, 342.345, 93.944, 95.821),
			[28] = new(498.936, 341.347, 93.944, 95.821),
			[34] = new(600.875, 341.347, 93.944, 93.825),
			[40] = new(702.814, 341.347, 95.943, 93.825),
			[46] = new(805.753, 340.349, 97.941, 94.823),
			[52] = new(909.690, 341.347, 95.943, 94.823),
			[58] = new(1013.628, 340.349, 95.943, 95.821),
			[64] = new(1116.567, 341.347, 97.941, 94.823),
			[70] = new(1220.504, 340.349, 95.943, 96.819),
			[76] = new(1326.441, 340.349, 169.898, 97.818),
			[82] = new(1503.335, 342.345, 97.941, 93.825),
			[88] = new(1607.273, 342.345, 91.945, 92.827),
			// ── Row 5 ──
			[5] = new(66.195, 445.153, 120.928, 96.819),
			[11] = new(193.119, 444.155, 122.926, 97.818),
			[17] = new(323.041, 443.157, 122.926, 98.816),
			[41] = new(449.965, 444.155, 635.620, 95.821),
			[59] = new(1090.582, 444.155, 96.942, 97.818),
			[71] = new(1291.462, 444.155, 102.938, 97.818),
			[77] = new(1398.398, 444.155, 97.941, 97.818),
			[83] = new(1501.337, 443.157, 97.941, 99.814),
			[89] = new(1605.274, 443.157, 94.943, 99.814),
		};

	/// <summary>
	/// Joins this table against a catalog by matrix slot, the same way
	/// <see cref="CyrexArtworkLayout.LayoutFor"/> does.
	/// </summary>
	public static IReadOnlyDictionary<string, RasterKeyBounds> LayoutFor(IReadOnlyList<PhysicalKey> catalog) =>
		catalog
			.Where(k => BySlot.ContainsKey(k.MatrixIndex))
			.ToDictionary(k => k.DevKeyName, k => BySlot[k.MatrixIndex]);
}
