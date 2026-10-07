using ProoveGaming.App.Models;
using Xunit;

namespace ProoveGaming.App.Tests;

// Catalog wiring for the keycap legend layer.
//
// Deliberately does NOT assert on the geometry itself: Avalonia's Geometry.Parse and AssetLoader
// both need platform services (IPlatformRenderInterface, IAssetLoader) that a bare xUnit process
// does not have — RgbKeyboardArt.Glow returns null here too, and has since long before this layer
// existed. Adding Avalonia.Headless to reach them is a new dependency and out of scope for this
// change, so the geometry is validated where it is produced instead:
// docs/plans/tint-keyboard-legends/extract-legends.py walks each generated path and fails if it
// falls outside the artwork viewBox or collapses, which is the failure an artwork re-export causes.
//
// What is worth asserting here is the wiring, because a board silently missing its legend asset
// degrades to "legends never light up" with no error anywhere.
public class KeyboardLegendAssetTests
{
	public static TheoryData<string> BoardsWithLegends() =>
		new() { "Raster", "Rampage", "Aress", "Cyrex", "T-Racer" };

	[Theory]
	[MemberData(nameof(BoardsWithLegends))]
	public void BoardWithSeparableLegends_PointsAtItsLegendAsset(string board)
	{
		var art = RgbKeyboardArtCatalog.For(board);

		Assert.NotNull(art);
		Assert.Equal($"/Assets/Keyboards/Legend/{board}.path", art!.LegendPathAsset);
	}

	// The unlit layer is what keeps legends readable on Key Assignments and Macros, where the board
	// is a click target rather than a lighting preview. An unparseable colour silently falls back to
	// white, so assert the configured value is real instead of trusting the fallback.
	[Theory]
	[MemberData(nameof(BoardsWithLegends))]
	public void UnlitLegendColour_Parses(string board)
	{
		var art = RgbKeyboardArtCatalog.For(board)!;

		Assert.True(Avalonia.Media.Color.TryParse(art.UnlitLegendColor, out _),
			$"{board}: UnlitLegendColor '{art.UnlitLegendColor}' does not parse, so its unlit " +
			$"legends would silently fall back to white");
	}

	// One pinned colour per board. Most keep the colour their artwork fixed the legends at, so the
	// unlit rendering on the click-target pages looks exactly as it did before they were extracted;
	// Rampage does not, because its blue-violet keycaps swallowed #0f53ff (PG-688). Both cases are
	// pinned for the same reason — a board that drifts to a colour its own keycaps hide goes
	// unreadable in silence, and nothing here throws when it does.
	[Theory]
	[InlineData("Raster", "#0f53ff")]
	[InlineData("Rampage", "#00FF3C")]
	[InlineData("Aress", "#e63c3c")]
	[InlineData("Cyrex", "#e63c3c")]
	[InlineData("T-Racer", "#0f53ff")]
	public void UnlitLegendColour_IsPinnedPerBoard(string board, string expected)
	{
		Assert.Equal(expected, RgbKeyboardArtCatalog.For(board)!.UnlitLegendColor);
	}

	// T800's artwork has no #text-solid group, so its legends stay baked into the keycap image. It
	// has to remain renderable rather than throwing or being quietly forgotten.
	[Fact]
	public void T800_HasNoLegendAsset()
	{
		var art = RgbKeyboardArtCatalog.For("T800");

		Assert.NotNull(art);
		Assert.Null(art!.LegendPathAsset);
	}

	// Fails if a board joins the catalog without a decision about its legends either way.
	[Theory]
	[InlineData("Raster")]
	[InlineData("Rampage")]
	[InlineData("Aress")]
	[InlineData("Cyrex")]
	[InlineData("T800")]
	[InlineData("T-Racer")]
	public void EveryCatalogBoard_Resolves(string board)
	{
		Assert.NotNull(RgbKeyboardArtCatalog.For(board));
	}
}
