namespace ProoveGaming.App.ViewModels;

/// <summary>
/// Cheap per-character width estimate for text drawn on the device overlays.
/// </summary>
/// <remarks>
/// Both callers size a container from a string before that string has a TextBlock to measure —
/// the hotspot computes its own width (which its connector line is then drawn to) and the key
/// overlay's hover tip computes its chip width, and neither has a visual tree to ask. The
/// constants are calibrated for <see cref="BaselineFontSize"/>; a caller drawing at a
/// different size scales through the two-argument overload rather than re-calibrating.
/// </remarks>
internal static class LabelWidthEstimator
{
	// The hotspot label's font size, which the per-character constants below were measured at.
	internal const double BaselineFontSize = 16;

	private const double CharacterWidth = 8;
	private const double SpaceWidth = 4.5;

	// The widest glyph the app font draws at BaselineFontSize — 'W' and 'M' reach roughly this,
	// against the 8px AVERAGE above. Only callers that must never come out short use it; see
	// EstimateUpperBound.
	private const double WidestCharacterWidth = 14;

	internal static double Estimate(string label)
	{
		double width = 0;
		foreach (var c in label)
		{
			width += c == ' ' ? SpaceWidth : CharacterWidth;
		}

		return width;
	}

	/// <summary>The same estimate, scaled from <see cref="BaselineFontSize"/> to <paramref name="fontSize"/>.</summary>
	internal static double Estimate(string label, double fontSize) =>
		Estimate(label) * (fontSize / BaselineFontSize);

	/// <summary>
	/// A width the string cannot exceed, for callers that would break if the estimate came out
	/// short.
	/// </summary>
	/// <remarks>
	/// <see cref="Estimate(string)"/> averages a glyph, so it is fine for centring — being a few
	/// pixels out only shifts something slightly — but wrong for keeping content inside a hard
	/// edge: a name in capitals measures 30-40% wider than the average, and a container positioned
	/// from an underestimate hangs that much off the edge. This bounds every character by the
	/// widest glyph instead, so the true width is always less. It over-reserves for ordinary
	/// lowercase names, which costs only a container sitting further inside the edge than it had
	/// to — the safe direction.
	/// </remarks>
	internal static double EstimateUpperBound(string label, double fontSize) =>
		label.Length * WidestCharacterWidth * (fontSize / BaselineFontSize);
}
