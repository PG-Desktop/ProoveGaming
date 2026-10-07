using System.Collections.Generic;
using System.Linq;

namespace ProoveGaming.App.Models.Keyboards;

/// <summary>
/// Keycap bounds for the <b>Cyrex</b> artwork, in that artwork's viewBox coordinates, keyed by
/// <b>matrix slot</b>.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="SmartInfinite65Geometry"/>, which places keys on an idealised 65% grid, these
/// bounds are measured from the artwork itself — so key overlays can be drawn directly on the
/// rendered board and stay registered to the real keycaps at any scale.
/// </para>
/// <para>
/// Derived by connected-component labelling of the alpha channel of the keycap layer in
/// <c>Assets/Keyboards/PG_Cyrex_keys.svg</c>, then mapped into viewBox space by that image's own
/// placement transform, <c>translate(16.895 17.69) scale(.983)</c>. The pass finds 68 components
/// against the board's 67 remappable slots; the extra one is FN, which is not remappable and
/// deliberately gets no overlay — the same relationship <see cref="RasterArtworkLayout"/> has.
/// Components were assigned to slots by nearest centroid after normalising both sets, with a worst
/// residual of 0.016 of the board's width.
/// </para>
/// <para>
/// These numbers are tied to that specific artwork revision. Replacing the keys SVG invalidates
/// every entry here, and nothing detects that automatically — regenerate by re-running the
/// labelling pass rather than hand-editing, and keep the catalog-coverage test as the guard.
/// </para>
/// <para>
/// Aress has its own equivalent table (<see cref="AressArtworkLayout"/>), measured from its own
/// (cropped) artwork the same way.
/// </para>
/// </remarks>
public static class CyrexArtworkLayout
{
	/// <summary>Artwork viewBox width — the bounds below are in these units.</summary>
	public const double ViewBoxWidth = 1472.469;

	/// <summary>Artwork viewBox height.</summary>
	public const double ViewBoxHeight = 480.666;

	/// <summary>Slot → measured keycap bounds. FN and the unused placeholder slots are absent.</summary>
	public static IReadOnlyDictionary<byte, RasterKeyBounds> BySlot { get; } =
		new Dictionary<byte, RasterKeyBounds>
		{
			// ── Row 0 ──
			[0] = new(16.895, 17.690, 84.538, 84.538),
			[1] = new(107.331, 17.690, 83.555, 84.538),
			[2] = new(197.767, 17.690, 83.555, 84.538),
			[3] = new(288.203, 17.690, 83.555, 84.538),
			[4] = new(378.639, 17.690, 83.555, 84.538),
			[5] = new(469.075, 17.690, 83.555, 84.538),
			[6] = new(559.511, 17.690, 83.555, 84.538),
			[7] = new(648.964, 17.690, 83.555, 84.538),
			[8] = new(739.400, 17.690, 83.555, 84.538),
			[9] = new(829.836, 17.690, 83.555, 84.538),
			[10] = new(920.272, 17.690, 83.555, 84.538),
			[11] = new(1010.708, 17.690, 83.555, 84.538),
			[12] = new(1100.161, 17.690, 83.555, 84.538),
			[13] = new(1190.597, 17.690, 174.974, 84.538),
			[14] = new(1371.469, 17.690, 83.555, 84.538),
			// ── Row 1 ──
			[15] = new(16.895, 108.126, 129.756, 84.538),
			[16] = new(152.549, 108.126, 83.555, 84.538),
			[17] = new(242.985, 108.126, 83.555, 84.538),
			[18] = new(333.421, 108.126, 83.555, 84.538),
			[19] = new(423.857, 108.126, 83.555, 84.538),
			[20] = new(514.293, 108.126, 83.555, 84.538),
			[21] = new(604.729, 108.126, 83.555, 84.538),
			[22] = new(694.182, 108.126, 83.555, 84.538),
			[23] = new(784.618, 108.126, 83.555, 84.538),
			[24] = new(875.054, 108.126, 83.555, 84.538),
			[25] = new(965.490, 108.126, 83.555, 84.538),
			[26] = new(1055.926, 108.126, 83.555, 84.538),
			[27] = new(1145.379, 108.126, 83.555, 84.538),
			[28] = new(1235.815, 108.126, 129.756, 84.538),
			[29] = new(1371.469, 108.126, 83.555, 84.538),
			// ── Row 2 ──
			[30] = new(16.895, 198.562, 151.382, 84.538),
			[31] = new(174.175, 198.562, 83.555, 84.538),
			[32] = new(264.611, 198.562, 83.555, 84.538),
			[33] = new(355.047, 198.562, 83.555, 84.538),
			[34] = new(445.483, 198.562, 83.555, 84.538),
			[35] = new(535.919, 198.562, 83.555, 84.538),
			[36] = new(625.372, 198.562, 83.555, 84.538),
			[37] = new(715.808, 198.562, 83.555, 84.538),
			[38] = new(806.244, 198.562, 83.555, 84.538),
			[39] = new(896.680, 198.562, 83.555, 84.538),
			[40] = new(987.116, 198.562, 83.555, 84.538),
			[41] = new(1076.569, 198.562, 83.555, 84.538),
			[43] = new(1167.988, 198.562, 197.583, 84.538),
			[44] = new(1371.469, 198.562, 83.555, 84.538),
			// ── Row 3 ──
			[45] = new(16.895, 288.998, 197.583, 84.538),
			[47] = new(221.359, 288.998, 83.555, 84.538),
			[48] = new(311.795, 288.998, 83.555, 84.538),
			[49] = new(402.231, 288.998, 83.555, 84.538),
			[50] = new(492.667, 288.998, 83.555, 84.538),
			[51] = new(583.103, 288.998, 83.555, 84.538),
			[52] = new(672.556, 288.998, 83.555, 84.538),
			[53] = new(762.992, 288.998, 83.555, 84.538),
			[54] = new(853.428, 288.998, 83.555, 84.538),
			[55] = new(943.864, 288.998, 83.555, 84.538),
			[56] = new(1034.300, 288.998, 83.555, 84.538),
			[57] = new(1123.753, 288.998, 152.365, 84.538),
			[58] = new(1281.033, 288.998, 84.538, 84.538),
			[59] = new(1371.469, 288.998, 83.555, 84.538),
			// ── Row 4 ──
			[60] = new(16.895, 379.434, 107.147, 83.555),
			[61] = new(129.940, 379.434, 107.147, 83.555),
			[62] = new(242.985, 379.434, 107.147, 83.555),
			[66] = new(357.013, 379.434, 561.293, 83.555),
			[69] = new(922.238, 379.434, 83.555, 83.555),
			[71] = new(1101.144, 379.434, 84.538, 83.555),
			[72] = new(1191.580, 379.434, 84.538, 83.555),
			[73] = new(1281.033, 379.434, 84.538, 83.555),
			[74] = new(1371.469, 379.434, 83.555, 83.555),
		};

	/// <summary>
	/// Joins this table against a catalog the same way <see cref="SmartInfinite65Geometry.LayoutFor"/>
	/// does, so the two models stay keyed by slot rather than by key name.
	/// </summary>
	public static IReadOnlyDictionary<string, RasterKeyBounds> LayoutFor(IReadOnlyList<PhysicalKey> catalog) =>
		catalog
			.Where(k => BySlot.ContainsKey(k.MatrixIndex))
			.ToDictionary(k => k.DevKeyName, k => BySlot[k.MatrixIndex]);
}
