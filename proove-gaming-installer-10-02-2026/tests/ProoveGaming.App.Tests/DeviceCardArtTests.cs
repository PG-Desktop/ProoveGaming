using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// Which card artwork a keyboard renders on the home screen. Two reasons put a board on the unlit
// layered path:
//
//   T-Racer                — necessity. Its vendor delivery was a single RGB-off file, so its
//                            CardSvg points at that, and drawing it raw shows the artwork's own
//                            #keys-solid glow while the details pages show the same board dark.
//   Cyrex, Aress, T800     — product decision. They do ship lit product shots, but the home card
//                            is shown unlit so it matches Key Assignments.
//
// The remaining boards keep their delivered lit product shot.
//
// Pinned because the two flags decide it silently: getting them wrong shows the wrong picture with
// no error, and a board added later inherits whichever default nobody checked.
public class DeviceCardArtTests
{
	private static DoubleCardViewModel Card(string name) => new() { Name = name, DeviceTypeLabel = "Wired Keyboard" };

	[Theory]
	[InlineData("T-Racer")]
	[InlineData("Cyrex")]
	[InlineData("Aress")]
	[InlineData("T800")]
	public void UnlitBoards_UseTheUnlitLayeredCardArt(string name)
	{
		var card = Card(name);

		Assert.True(card.HasKeyboardArt);
		Assert.True(card.UseUnlitCardArt);
		Assert.False(card.UseProductShotCardArt);
	}

	// The rest keep their delivered product shot.
	[Theory]
	[InlineData("Raster")]
	[InlineData("Rampage")]
	public void EveryOtherBoard_KeepsItsProductShot(string name)
	{
		var card = Card(name);

		Assert.True(card.HasKeyboardArt);
		Assert.False(card.UseUnlitCardArt);
		Assert.True(card.UseProductShotCardArt);
	}

	// A card whose name does not resolve in the artwork catalog falls back to the flat ImagePath,
	// so neither artwork branch may claim it — the layered control would otherwise be handed a null
	// Art. (It tolerates that, the same way the details view does, but the flags must still agree.)
	[Fact]
	public void UnknownBoard_ClaimsNeitherArtworkBranch()
	{
		var card = Card("DragonFang");

		Assert.False(card.HasKeyboardArt);
		Assert.False(card.UseUnlitCardArt);
		Assert.False(card.UseProductShotCardArt);
	}
}
