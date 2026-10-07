using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// The card artwork must fade when the device is disconnected, and must do so identically for both
// art paths — the product shot (SvgImageView) and the layered build (RgbKeyboardView).
//
// The two paths need DIFFERENT mechanisms, which is the whole reason this kept regressing:
//
//   * the raster path fades via Opacity (CardArtOpacity);
//   * the layered path CANNOT. Its chassis and keycap layers are Avalonia.Svg.Skia Svg controls,
//     which paint an SKPicture through their own draw operation and ignore an ancestor's opacity,
//     so it fades by compositing a black scrim instead (CardArtDimOpacity → RgbKeyboardView.DimOpacity).
//
// The two are equivalent on the card's near-black background — 0.3 x art either way — and the
// arithmetic relationship between them is asserted below so they cannot drift apart. Two earlier
// attempts at this bug failed silently: first a style selector that never matched, then an Opacity
// binding on a control that ignores it. Neither produced an error; the only symptom was T800 and
// T-Racer sitting bright next to faded boards.
public class DeviceCardArtOpacityTests
{
	private static DoubleCardViewModel Card(string name) =>
		new() { Name = name, DeviceTypeLabel = "Wired Keyboard" };

	[Fact]
	public void DisconnectedCard_FadesItsArtwork()
	{
		var card = Card("T800");

		Assert.False(card.Connected);
		Assert.Equal(DeviceCardViewModel.DisconnectedArtOpacity, card.CardArtOpacity);
	}

	[Theory]
	[InlineData("T800")]        // layered path — the one that was broken
	[InlineData("T-Racer")]     // layered path
	[InlineData("Cyrex")]       // layered path — moved there upstream
	[InlineData("Raster")]      // product-shot path
	[InlineData("Rampage")]     // product-shot path
	public void ConnectedCard_ShowsArtworkAtFullOpacity(string name)
	{
		var card = Card(name);
		card.HasWire = true;

		Assert.True(card.Connected);
		Assert.Equal(1.0, card.CardArtOpacity);
	}

	// The whole point of binding rather than styling: both art paths must fade by the same amount.
	// Previously the layered boards did not fade at all.
	[Theory]
	[InlineData("T800")]
	[InlineData("T-Racer")]
	[InlineData("Cyrex")]
	[InlineData("Raster")]
	[InlineData("Rampage")]
	public void EveryBoard_FadesTheSameWhenDisconnected(string name)
	{
		var card = Card(name);

		Assert.Equal(DeviceCardViewModel.DisconnectedArtOpacity, card.CardArtOpacity);
	}

	// The layered path fades by scrim, not by Opacity. These are the assertions that would have
	// caught the second attempt at this bug, where CardArtOpacity was bound to a control that
	// ignores it and the board stayed bright.
	[Theory]
	[InlineData("T800")]
	[InlineData("T-Racer")]
	public void LayeredBoard_DisconnectedGetsAScrim(string name)
	{
		var card = Card(name);

		Assert.True(card.UseUnlitCardArt, $"{name} must render through RgbKeyboardView for this to matter");
		Assert.True(card.CardArtDimOpacity > 0, "a disconnected layered board draws no scrim, so it will not fade");
	}

	[Theory]
	[InlineData("T800")]
	[InlineData("T-Racer")]
	public void LayeredBoard_ConnectedDrawsNoScrim(string name)
	{
		var card = Card(name);
		card.HasWire = true;

		Assert.Equal(0.0, card.CardArtDimOpacity);
	}

	// Scrim and opacity must stay two expressions of one value: compositing black at (1 - a) over
	// the art equals drawing the art at opacity a on a black background. If someone tunes one, this
	// forces them to tune the other.
	[Fact]
	public void ScrimAndOpacity_AreTheSameFadeExpressedTwoWays()
	{
		var card = Card("T800");

		Assert.Equal(1.0 - card.CardArtOpacity, card.CardArtDimOpacity);

		card.HasWire = true;
		Assert.Equal(1.0 - card.CardArtOpacity, card.CardArtDimOpacity);
	}

	[Fact]
	public void LayeredAndProductShotBoards_AgreeOnBothStates()
	{
		var layered = Card("T800");
		var productShot = Card("Raster");

		Assert.True(layered.UseUnlitCardArt);
		Assert.True(productShot.UseProductShotCardArt);
		Assert.Equal(productShot.CardArtOpacity, layered.CardArtOpacity);

		layered.HasWire = true;
		productShot.HasWire = true;
		Assert.Equal(productShot.CardArtOpacity, layered.CardArtOpacity);
	}

	// Opacity has to repaint when a transport drops, not only when the card is first built —
	// MakeInactive() is what runs on disconnect while the app is open.
	[Theory]
	[InlineData("T800")]
	[InlineData("Cyrex")]
	public void MakeInactive_RaisesChangeNotificationForOpacity(string name)
	{
		var card = Card(name);
		card.HasWire = true;

		var raised = false;
		var dimRaised = false;
		card.PropertyChanged += (_, e) =>
		{
			if (e.PropertyName == nameof(DeviceCardViewModel.CardArtOpacity)) raised = true;
			if (e.PropertyName == nameof(DeviceCardViewModel.CardArtDimOpacity)) dimRaised = true;
		};

		card.MakeInactive();

		Assert.True(raised, "CardArtOpacity did not notify — the card would stay bright until redrawn");
		Assert.True(dimRaised, "CardArtDimOpacity did not notify — the layered board would stay bright");
		Assert.Equal(DeviceCardViewModel.DisconnectedArtOpacity, card.CardArtOpacity);
	}

	[Theory]
	[InlineData("T800")]
	[InlineData("Cyrex")]
	public void ReconnectingOverAnyTransport_RestoresFullOpacity(string name)
	{
		var card = Card(name);

		card.HasDongle = true;
		Assert.Equal(1.0, card.CardArtOpacity);
		card.MakeInactive();

		card.HasBluetooth = true;
		Assert.Equal(1.0, card.CardArtOpacity);
	}
}
