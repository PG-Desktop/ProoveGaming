using System.Linq;
using ProoveGaming.App.Models.Keyboards;
using Xunit;

namespace ProoveGaming.App.Tests;

// Generated-data sanity for TRacerArtworkLayout, which is produced by
// docs/plans/add-tracer-artwork/split-tracer-artwork.py from the keycap layer's alpha channel.
// The failures this guards against all ship as plausible-looking but wrong overlay positions
// rather than as an error, so they have to be caught here or by eye.
//
// Deliberately does NOT assert on the artwork itself: AssetLoader needs platform services a bare
// xUnit process does not have. The generation script validates the pixels; this validates the
// table it emitted against the catalog it has to line up with.
public class TRacerArtworkLayoutTests
{
	// Catches a labelling run that assigned a component to the wrong key, or a catalog that grew a
	// key the artwork does not draw.
	[Fact]
	public void CoversEveryCatalogKeyAndNothingElse()
	{
		var catalogNames = TRacerKeyCatalog.Keys.Select(k => k.DevKeyName).ToHashSet();

		Assert.Equal(catalogNames.Count, TRacerArtworkLayout.Layout.Count);
		Assert.All(TRacerArtworkLayout.Layout.Keys, name => Assert.Contains(name, catalogNames));
	}

	// FN is drawn by the artwork but is not remappable — it must get no overlay, the same
	// exclusion Raster makes, or clicking it would select a key that cannot be bound.
	[Fact]
	public void DoesNotContainFn()
	{
		Assert.DoesNotContain("Fn", TRacerArtworkLayout.Layout.Keys);
	}

	// The one key T-Racer has and Raster does not. The abstract layout carried an explicitly
	// unverified guess for its position; this table measures it.
	[Fact]
	public void PrintScreen_HasBounds()
	{
		Assert.True(TRacerArtworkLayout.Layout.ContainsKey("PrintScreen"));
	}

	// Every measured rect must sit inside the artwork it was measured from — a transform applied
	// wrongly shows up here rather than as silently offset overlays on screen.
	[Fact]
	public void BoundsLieWithinTheViewBox()
	{
		Assert.All(TRacerArtworkLayout.Layout, entry =>
		{
			var (name, b) = entry;
			Assert.True(b.X >= 0 && b.Y >= 0, $"{name}: negative origin: {b.X},{b.Y}");
			Assert.True(b.X + b.Width <= TRacerArtworkLayout.ViewBoxWidth,
				$"{name}: overflows width: {b.X + b.Width}");
			Assert.True(b.Y + b.Height <= TRacerArtworkLayout.ViewBoxHeight,
				$"{name}: overflows height: {b.Y + b.Height}");
			Assert.True(b.Width > 0 && b.Height > 0, $"{name}: zero-sized keycap");
		});
	}

	// The table is built by walking each row of measured components left to right against the
	// catalog. A dropped or doubled component shifts every key after it in that row, which leaves
	// two keys claiming overlapping ground while every other assertion here still passes.
	[Fact]
	public void KeysDoNotOverlap()
	{
		const double tolerance = 2.0;
		var keys = TRacerArtworkLayout.Layout.ToList();

		for (var i = 0; i < keys.Count; i++)
		{
			for (var j = i + 1; j < keys.Count; j++)
			{
				var (leftName, a) = keys[i];
				var (rightName, b) = keys[j];

				var overlapX = System.Math.Min(a.X + a.Width, b.X + b.Width) - System.Math.Max(a.X, b.X);
				var overlapY = System.Math.Min(a.Y + a.Height, b.Y + b.Height) - System.Math.Max(a.Y, b.Y);

				Assert.False(overlapX > tolerance && overlapY > tolerance,
					$"'{leftName}' and '{rightName}' overlap by {overlapX:F1}x{overlapY:F1} units");
			}
		}
	}
}
