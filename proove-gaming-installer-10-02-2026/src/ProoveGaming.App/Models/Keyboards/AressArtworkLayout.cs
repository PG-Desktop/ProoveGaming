using System.Collections.Generic;
using System.Linq;

namespace ProoveGaming.App.Models.Keyboards;

/// <summary>
/// Keycap bounds for the <b>Aress</b> artwork, in that artwork's cropped viewBox coordinates,
/// keyed by <b>matrix slot</b>.
/// </summary>
/// <remarks>
/// <para>
/// Derived the same way as <see cref="CyrexArtworkLayout"/>: connected-component labelling of the
/// alpha channel of the keycap layer in <c>Assets/Keyboards/PG_Aress_keys.svg</c> (its embedded
/// raster, placement <c>translate(0 499.299) scale(.501)</c>), mapped into the cropped viewBox
/// <c>30.2 536.8 1510.5 487.3</c>. The pass finds 68 components against the board's 67 remappable
/// slots; the extra one is FN, dropped like everywhere else.
/// </para>
/// <para>
/// The artwork does not depict the board's navigation column exactly: it draws four navigation
/// keys stacked beside rows 0-1 (legends HOME / PGUP / DEL / PGDN), while the physical board has
/// one full-height navigation key per row 0-3 (Insert / PageUp / Delete / PageDown). The three
/// keys whose legends match are assigned to their real slots; the leftover HOME keycap carries
/// slot 14 (Insert) — the only mismatch between a drawn legend and the key it operates. That is
/// the accepted trade-off for showing Aress's own artwork instead of borrowing Cyrex's.
/// </para>
/// <para>
/// These numbers are tied to that specific artwork revision AND to the viewBox crop recorded in
/// the Aress entry of the App's RgbKeyboardArtCatalog — recropping the SVGs or replacing the keys
/// layer invalidates every entry here. Regenerate by re-running the labelling pass rather than
/// hand-editing.
/// </para>
/// </remarks>
public static class AressArtworkLayout
{
	/// <summary>Artwork viewBox width (post-crop) — the bounds below are in these units.</summary>
	public const double ViewBoxWidth = 1510.5;

	/// <summary>Artwork viewBox height (post-crop).</summary>
	public const double ViewBoxHeight = 487.3;

	/// <summary>Slot → measured keycap bounds. FN and the unused placeholder slots are absent.</summary>
	public static IReadOnlyDictionary<byte, RasterKeyBounds> BySlot { get; } =
		new Dictionary<byte, RasterKeyBounds>
		{
			// ── Row 0 (nav column: HOME keycap carries Insert, PGUP carries PageUp) ──
			[0] = new(40.942, 40.655, 78.156, 78.156),
			[1] = new(123.607, 40.655, 77.655, 77.655),
			[2] = new(205.771, 41.156, 78.156, 77.154),
			[3] = new(288.436, 40.655, 78.156, 78.156),
			[4] = new(370.6, 40.655, 78.156, 78.156),
			[5] = new(453.766, 40.655, 78.657, 78.156),
			[6] = new(536.431, 40.655, 78.156, 78.156),
			[7] = new(618.595, 40.655, 78.156, 78.156),
			[8] = new(701.761, 40.655, 78.156, 78.156),
			[9] = new(783.925, 40.655, 78.156, 78.156),
			[10] = new(866.089, 40.655, 78.156, 78.156),
			[11] = new(948.754, 40.655, 78.156, 78.156),
			[12] = new(1031.419, 40.655, 78.657, 78.156),
			[13] = new(1113.583, 40.655, 160.821, 78.156),
			[14] = new(1308.472, 40.655, 79.659, 78.156),
			[29] = new(1391.638, 40.655, 79.158, 78.156),
			// ── Row 1 (nav column: DEL / PGDN keycaps carry Delete / PageDown) ──
			[15] = new(40.942, 123.32, 118.737, 77.655),
			[16] = new(164.689, 123.821, 78.156, 77.154),
			[17] = new(246.853, 123.821, 78.156, 77.154),
			[18] = new(330.019, 123.821, 78.156, 77.154),
			[19] = new(412.183, 123.821, 78.156, 77.154),
			[20] = new(495.349, 123.821, 78.657, 77.154),
			[21] = new(577.513, 122.819, 78.657, 78.156),
			[22] = new(659.677, 122.819, 78.657, 78.156),
			[23] = new(742.342, 122.819, 78.156, 78.156),
			[24] = new(825.508, 123.821, 78.657, 77.154),
			[25] = new(907.672, 123.32, 78.657, 77.655),
			[26] = new(989.836, 123.821, 78.657, 77.154),
			[27] = new(1073.503, 122.819, 77.154, 78.156),
			[28] = new(1155.667, 122.819, 118.737, 78.156),
			[44] = new(1308.472, 122.819, 79.659, 78.156),
			[59] = new(1391.638, 122.819, 79.158, 78.156),
			// ── Row 2 ──
			[30] = new(40.942, 205.985, 139.278, 78.156),
			[31] = new(185.23, 205.985, 78.657, 78.156),
			[32] = new(267.394, 205.985, 78.657, 78.156),
			[33] = new(350.059, 205.985, 78.156, 78.156),
			[34] = new(432.223, 205.985, 78.156, 78.156),
			[35] = new(515.389, 205.985, 78.156, 78.156),
			[36] = new(597.553, 205.985, 78.156, 78.156),
			[37] = new(679.717, 205.985, 78.156, 78.156),
			[38] = new(762.883, 205.985, 78.156, 78.156),
			[39] = new(845.047, 205.985, 78.657, 78.156),
			[40] = new(928.714, 205.985, 78.156, 78.156),
			[41] = new(1010.878, 205.985, 78.156, 78.156),
			[43] = new(1094.044, 205.985, 180.36, 78.156),
			// ── Row 3 ──
			[45] = new(40.942, 288.149, 180.861, 78.156),
			[47] = new(225.811, 288.149, 78.156, 78.156),
			[48] = new(307.975, 288.149, 78.657, 78.156),
			[49] = new(391.141, 288.149, 78.657, 78.156),
			[50] = new(473.806, 288.149, 78.156, 78.156),
			[51] = new(556.972, 288.149, 78.156, 78.156),
			[52] = new(639.136, 288.149, 78.657, 78.156),
			[53] = new(721.3, 288.149, 78.657, 78.156),
			[54] = new(804.967, 288.149, 78.156, 78.156),
			[55] = new(887.131, 288.149, 78.156, 78.156),
			[56] = new(969.295, 288.149, 78.156, 78.156),
			[57] = new(1052.461, 288.149, 221.943, 78.156),
			[58] = new(1308.472, 288.149, 79.659, 78.156),
			// ── Row 4 ──
			[60] = new(40.942, 370.313, 98.697, 78.156),
			[61] = new(143.647, 370.313, 99.198, 78.156),
			[62] = new(246.853, 370.313, 99.198, 78.156),
			[66] = new(350.059, 370.313, 512.022, 78.156),
			[69] = new(866.089, 370.313, 99.198, 78.156),
			[71] = new(1073.503, 370.313, 100.2, 78.156),
			[72] = new(1225.306, 370.313, 79.158, 78.156),
			[73] = new(1308.472, 370.313, 79.659, 78.156),
			[74] = new(1391.638, 370.313, 79.158, 78.156),
		};

	/// <summary>
	/// Joins this table against a catalog the same way <see cref="CyrexArtworkLayout.LayoutFor"/>
	/// does, so the two models stay keyed by slot rather than by key name.
	/// </summary>
	public static IReadOnlyDictionary<string, RasterKeyBounds> LayoutFor(IReadOnlyList<PhysicalKey> catalog) =>
		catalog
			.Where(k => BySlot.ContainsKey(k.MatrixIndex))
			.ToDictionary(k => k.DevKeyName, k => BySlot[k.MatrixIndex]);
}
