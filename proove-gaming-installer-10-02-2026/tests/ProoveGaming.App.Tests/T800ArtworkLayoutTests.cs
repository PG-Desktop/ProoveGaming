using System.Linq;
using ProoveGaming.App.Models.Keyboards;
using Xunit;

namespace ProoveGaming.App.Tests;

// Measured keycap bounds for the T800 artwork
// (docs/plans/add-t800-key-assignments/measure-t800-keycaps.py).
//
// The bounds are in the artwork's CROPPED viewBox space — the T800 assets were re-cropped to
// "172 672 1742 600", so the generator subtracts that origin from the keycap layer's own placement
// transform. Forgetting that step puts every overlay 172 units to the right, which still renders
// and still looks like a keyboard; the viewBox containment assertions below are what catches it.
public class T800ArtworkLayoutTests
{
	[Fact]
	public void ViewBox_MatchesTheCroppedArtwork()
	{
		Assert.Equal(1742.0, T800ArtworkLayout.ViewBoxWidth);
		Assert.Equal(600.0, T800ArtworkLayout.ViewBoxHeight);
	}

	[Fact]
	public void EveryRemappableKey_HasBounds()
	{
		var layout = T800ArtworkLayout.LayoutFor(T800KeyCatalog.Keys);

		Assert.Equal(T800KeyCatalog.Keys.Count, layout.Count);
		Assert.All(T800KeyCatalog.Keys, k => Assert.True(layout.ContainsKey(k.DevKeyName),
			$"{k.DevKeyName} (slot {k.MatrixIndex}) has no measured bounds"));
	}

	// FN is not remappable and must get no overlay — a clickable FN would let the user break every
	// FN+X hardware shortcut on the board.
	[Fact]
	public void Fn_HasNoBounds()
	{
		Assert.False(T800ArtworkLayout.BySlot.ContainsKey(T800KeyCatalog.FnMatrixIndex));
	}

	[Fact]
	public void EveryBound_LiesInsideTheViewBox()
	{
		Assert.All(T800ArtworkLayout.BySlot, entry =>
		{
			var (slot, b) = (entry.Key, entry.Value);
			Assert.True(b.X >= 0, $"slot {slot}: X {b.X} is negative");
			Assert.True(b.Y >= 0, $"slot {slot}: Y {b.Y} is negative");
			Assert.True(b.X + b.Width <= T800ArtworkLayout.ViewBoxWidth,
				$"slot {slot}: right edge {b.X + b.Width} exceeds {T800ArtworkLayout.ViewBoxWidth}");
			Assert.True(b.Y + b.Height <= T800ArtworkLayout.ViewBoxHeight,
				$"slot {slot}: bottom edge {b.Y + b.Height} exceeds {T800ArtworkLayout.ViewBoxHeight}");
		});
	}

	[Fact]
	public void EveryBound_HasRealSize()
	{
		Assert.All(T800ArtworkLayout.BySlot, entry =>
		{
			Assert.True(entry.Value.Width > 0, $"slot {entry.Key} has no width");
			Assert.True(entry.Value.Height > 0, $"slot {entry.Key} has no height");
		});
	}

	// Keycaps must not overlap: overlapping hit boxes make one key unclickable, and the symptom is
	// "clicking that key selects its neighbour" rather than an error.
	[Fact]
	public void Bounds_DoNotOverlap()
	{
		var all = T800ArtworkLayout.BySlot.ToList();

		for (int i = 0; i < all.Count; i++)
		for (int j = i + 1; j < all.Count; j++)
		{
			var (a, b) = (all[i].Value, all[j].Value);
			bool overlaps = a.X < b.X + b.Width && b.X < a.X + a.Width &&
			                a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

			Assert.False(overlaps,
				$"slots {all[i].Key} and {all[j].Key} overlap");
		}
	}

	// Row structure is the cross-check that verified the slot assignment in the first place: the
	// artwork's keycap rows and the firmware's factory table agree on 15/15/14/14/10.
	[Fact]
	public void Rows_MatchTheFactoryTable()
	{
		var counts = T800ArtworkLayout.BySlot.Keys
			.GroupBy(slot => slot % 6)
			.OrderBy(g => g.Key)
			.ToDictionary(g => g.Key, g => g.Count());

		Assert.Equal(15, counts[1]);
		Assert.Equal(15, counts[2]);
		Assert.Equal(14, counts[3]);
		Assert.Equal(14, counts[4]);
		Assert.Equal(9, counts[5]);   // 10 in the table, minus FN
	}

	// Within a visual row the matrix is column-major, so ascending slot must run left to right.
	// If the generator's zip ever pairs a row against the wrong slot run, this catches it.
	[Fact]
	public void WithinEachRow_AscendingSlotRunsLeftToRight()
	{
		foreach (var row in T800ArtworkLayout.BySlot.GroupBy(e => e.Key % 6))
		{
			var ordered = row.OrderBy(e => e.Key).Select(e => e.Value.X).ToList();

			for (int i = 1; i < ordered.Count; i++)
				Assert.True(ordered[i] > ordered[i - 1],
					$"row {row.Key}: bounds are not left-to-right in slot order at index {i}");
		}
	}
}
