using System.Collections.Generic;

namespace ProoveGaming.App.Models.Keyboards;

/// <summary>
/// Pixel-space bounds for each Aress physical key, derived from the shared 65% geometry.
/// </summary>
/// <remarks>
/// Aress and Cyrex are the same physical board, differing only in what three navigation keys are
/// labelled. The geometry lives once in <see cref="SmartInfinite65Geometry"/>, keyed by matrix slot,
/// and each model's layout is the join of its own catalog against it — so the two stay in step by
/// construction rather than by copy-paste.
/// </remarks>
public static class AressVisualLayout
{
	public const double UnitSize = SmartInfinite65Geometry.UnitSize;
	public const double CanvasWidth = SmartInfinite65Geometry.CanvasWidth;
	public const double CanvasHeight = SmartInfinite65Geometry.CanvasHeight;

	public static IReadOnlyDictionary<string, RasterKeyBounds> Layout { get; } =
		SmartInfinite65Geometry.LayoutFor(AressKeyCatalog.Keys);
}
