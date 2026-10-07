using System.Collections.Generic;

namespace ProoveGaming.App.Models.Keyboards;

/// <summary>
/// Pixel-space bounds for each Cyrex physical key, derived from the shared 65% geometry.
/// </summary>
/// <remarks>
/// Cyrex and Aress are the same physical board — identical 75-slot matrix, identical 68 keys, FN in
/// the same slot — differing only in what three navigation keys are labelled. The geometry therefore
/// lives once in <see cref="SmartInfinite65Geometry"/>, keyed by matrix slot, and each model's layout
/// is the join of its own catalog against it. That keeps the two models in step by construction.
/// </remarks>
public static class CyrexVisualLayout
{
	public const double UnitSize = SmartInfinite65Geometry.UnitSize;
	public const double CanvasWidth = SmartInfinite65Geometry.CanvasWidth;
	public const double CanvasHeight = SmartInfinite65Geometry.CanvasHeight;

	public static IReadOnlyDictionary<string, RasterKeyBounds> Layout { get; } =
		SmartInfinite65Geometry.LayoutFor(CyrexKeyCatalog.Keys);
}
