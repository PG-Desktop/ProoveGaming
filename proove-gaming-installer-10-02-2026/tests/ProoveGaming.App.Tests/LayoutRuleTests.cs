using System.Linq;
using Avalonia;
using ProoveGaming.App.Models;
using ProoveGaming.App.ViewModels;
using ProoveGaming.App.Views;
using Xunit;

namespace ProoveGaming.App.Tests;

// Sizing rules whose failure modes are entirely silent — nothing throws, the picture just ends up
// somewhere it should not be, the UI becomes unreadable, or the window sits a little off the screen.
// All of them are pure arithmetic, so they are cheap to pin and there is no excuse for leaving them
// to be found by eye.
public class LayoutRuleTests
{
	// ── Root scale (small screens, 150-200 % display scaling) ────────────────────────────────
	//
	// The window asks for a 1200x880 layout. At 200 % a 1920x1080 screen offers only 960x540 of
	// layout space, so the window cannot fit its own minimum and the title bar — with minimise and
	// close — ended up off-screen. The UI scales down uniformly instead of clipping, because there
	// is nothing here to reflow: DeviceDetailsView pins 26 blocks to a fixed height and the cards
	// position icons by absolute Canvas coordinates.

	[Fact]
	public void AWindowAtOrAboveTheDesignSizeIsNeverScaled()
	{
		Assert.Equal(1.0, MainWindow.ResolveRootScale(1200, 880));
		Assert.Equal(1.0, MainWindow.ResolveRootScale(1440, 900));
		Assert.Equal(1.0, MainWindow.ResolveRootScale(2560, 1392));
	}

	// Scaling UP would blow the layout past the size it was drawn for on a large monitor. Above the
	// design size the layout stretches on its own, exactly as it did before any of this existed.
	[Fact]
	public void AVeryLargeWindowIsStillNotScaledUp()
	{
		Assert.Equal(1.0, MainWindow.ResolveRootScale(3840, 2160));
	}

	// The case this exists for. 1920x1080 at 200 % leaves ~960x492 of usable layout space.
	[Fact]
	public void AWindowSmallerThanTheDesignIsScaledDownToFit()
	{
		var scale = MainWindow.ResolveRootScale(960, 492);

		Assert.True(scale < 1.0, $"expected a reduction, got {scale}");
		Assert.True(960 / scale >= 1200 - 0.5,
			"the layout must still receive its full design width once scaled");
	}

	// Uniform: the smaller of the two ratios wins, or the layout distorts.
	[Fact]
	public void TheTighterDimensionDecidesTheScale()
	{
		// Roomy width, cramped height — the height must win.
		var scale = MainWindow.ResolveRootScale(2400, 440);

		Assert.Equal(440 / 880.0, scale, 3);
	}

	// A pathologically small window must not scale the UI into illegibility.
	[Theory]
	[InlineData(200, 100)]
	[InlineData(1, 1)]
	public void TheScaleNeverFallsBelowTheReadableFloor(double width, double height)
	{
		Assert.Equal(0.5, MainWindow.ResolveRootScale(width, height));
	}

	// A window with no size yet (measured before layout) must not produce a zero or negative scale.
	[Theory]
	[InlineData(0, 0)]
	[InlineData(-100, 500)]
	public void ADegenerateSizeFallsBackToUnscaled(double width, double height)
	{
		Assert.Equal(1.0, MainWindow.ResolveRootScale(width, height));
	}

	// ── Usable client size (the OS frame around a BorderOnly window) ─────────────────────────
	//
	// Width/Height on a Window are the CLIENT size, and WindowDecorations="BorderOnly" keeps a real
	// OS frame around it — 16x9 logical pixels on Windows 11. Clamping the client size straight to
	// the working area therefore left the window that much too big for the screen: right border off
	// the edge, bottom under the taskbar, resize grips with them (PG-683).

	[Fact]
	public void TheOsFrameIsPaidForOutOfTheWorkingArea()
	{
		// 1280x720 with a taskbar, and the frame this window actually has.
		var usable = MainWindow.ResolveUsableClientSize(new Size(1280, 672), new Size(16, 9));

		Assert.Equal(1264, usable.Width);
		Assert.Equal(663, usable.Height);
	}

	// The window is only resizable because it keeps a frame, so a zero overhead means "we could not
	// measure it", not "there is none" — and must leave the old, unadjusted behaviour intact.
	[Fact]
	public void AnUnmeasurableFrameLeavesTheWorkingAreaAlone()
	{
		var usable = MainWindow.ResolveUsableClientSize(new Size(1280, 672), default);

		Assert.Equal(1280, usable.Width);
		Assert.Equal(672, usable.Height);
	}

	// A degenerate working area must not hand back a negative size — it is assigned to MinWidth.
	[Fact]
	public void AWorkingAreaSmallerThanItsOwnFrameStillYieldsAPositiveSize()
	{
		var usable = MainWindow.ResolveUsableClientSize(new Size(8, 4), new Size(16, 9));

		Assert.True(usable.Width > 0, $"expected a positive width, got {usable.Width}");
		Assert.True(usable.Height > 0, $"expected a positive height, got {usable.Height}");
	}

	// ── The draggable minimum ────────────────────────────────────────────────────────────────
	//
	// A minimum that stays at the design size stops a window on a smaller screen from shrinking to
	// fit it. A minimum set to what the screen offers stops the window reaching even that, because
	// the platform enforces the minimum using a larger frame than this window actually has — that
	// was the last 10 physical px of the app sitting under the taskbar at 150 % (PG-683).

	[Fact]
	public void AScreenThatStillHoldsTheDesignLeavesTheMinimumAlone()
	{
		Assert.Equal(1200, MainWindow.ResolveMinimumExtent(1200, 1904, 1200));
		Assert.Equal(880, MainWindow.ResolveMinimumExtent(880, 880, 880));
	}

	// The case it exists for: 1920x1080 at 150 % leaves 1265x664 of usable client area, under the
	// 1200x880 minimum. The floor drops to the readable size, NOT to the 664 the screen offers —
	// a minimum equal to the target is what the window then cannot reach.
	[Fact]
	public void AScreenTooSmallForTheDesignDropsTheMinimumToTheReadableFloor()
	{
		Assert.Equal(440, MainWindow.ResolveMinimumExtent(880, 664, 880));
		Assert.True(MainWindow.ResolveMinimumExtent(880, 664, 880) < 664,
			"the minimum must sit clear of the size the window is being asked to take");
	}

	// Never above what the screen can show, even when that is below the readable floor: the value
	// is assigned to MinHeight, and a minimum larger than the screen is the original bug.
	[Fact]
	public void AScreenSmallerThanTheReadableFloorStillGetsAReachableMinimum()
	{
		Assert.Equal(300, MainWindow.ResolveMinimumExtent(880, 300, 880));
	}

	// It only ever comes down. Moving back to a roomy screen must not re-raise the floor under a
	// window the user has since sized small.
	[Fact]
	public void TheMinimumNeverGoesBackUp()
	{
		var lowered = MainWindow.ResolveMinimumExtent(880, 664, 880);

		Assert.Equal(lowered, MainWindow.ResolveMinimumExtent(lowered, 2000, 880));
	}

	// ── Hotspot canvas (the labelled mouse diagram) ──────────────────────────────────────────
	//
	// The labels were laid out against a 1236-wide canvas, but the panel they sit in is the layout's
	// `*` column — around 600 near the design window width. Pinned at 1236 and centred, the canvas
	// hung ~318px off each edge and the outermost labels were clipped away: "Side Forward" rendered
	// as "e Forward", with half a bound macro's name gone with it (PG-683 follow-up).

	[Fact]
	public void APanelNarrowerThanTheDesignShrinksTheHotspotCanvasToIt()
	{
		Assert.Equal(600, DeviceDetailsViewModel.ResolveHotspotCanvasWidth(600, 1236));
	}

	// Never wider than the design: past 1236 the labels would drift away from the device.
	[Fact]
	public void ARoomyPanelStillCapsTheHotspotCanvasAtTheDesignWidth()
	{
		Assert.Equal(1236, DeviceDetailsViewModel.ResolveHotspotCanvasWidth(1800, 1236));
	}

	// A hidden section still raises SizeChanged, at zero. That is "not laid out", not "no room" —
	// taking it literally would collapse the canvas and move every label onto the device.
	[Theory]
	[InlineData(0)]
	[InlineData(-50)]
	[InlineData(double.NaN)]
	public void AnUnlaidOutPanelKeepsTheWidthAlreadyMeasured(double reported)
	{
		Assert.Equal(742, DeviceDetailsViewModel.ResolveHotspotCanvasWidth(reported, 742));
	}

	// Pulling the anchors inward is not enough: the label box grows with the bound macro's name and
	// a left-side label is laid out leftwards from its anchor, so a long name walks it off the edge.
	[Fact]
	public void ALabelWiderThanItsAnchorAllowsIsSlidBackOntoTheCanvas()
	{
		Assert.Equal(0, DevicePartHotspotViewModel.ClampButtonX(-80, 180, 600));
		Assert.Equal(420, DevicePartHotspotViewModel.ClampButtonX(500, 180, 600));
	}

	// A label that already fits is left exactly where the layout put it.
	[Fact]
	public void ALabelThatFitsIsNotMoved()
	{
		Assert.Equal(137, DevicePartHotspotViewModel.ClampButtonX(137, 180, 600));
	}

	// Wider than the whole canvas: pinned to the left edge, so it loses its right end — where the
	// unassign ✕ sits — rather than its beginning, which is the text.
	[Fact]
	public void ALabelWiderThanTheCanvasStartsAtItsLeftEdge()
	{
		Assert.Equal(0, DevicePartHotspotViewModel.ClampButtonX(-200, 700, 600));
	}

	// The invariant the canvas squeeze is actually for, with the real catalog coordinates. The
	// anchor compresses toward the centre while the marker only re-centres, so the gap the design
	// left between them shrinks with the canvas — and under a narrow enough canvas a left-side
	// anchor crosses its own marker, putting the label box on the ring it labels and sending the
	// connector backwards. TAIPAN's side buttons sit 40px from their labels against BLAZE's 104,
	// so they cross at any canvas under ~923 — including the 808 the Macros panel gets at the
	// DEFAULT 1440 window (PR #144 M2). Isolated tests of the helpers all passed while that was
	// broken, which is why this one works through the view model.

	public static TheoryData<string, double, double, HotspotLabelSide> HotspotGeometry => new()
	{
		// device/part, ButtonX, TargetX, side — straight out of DeviceHost appsettings.
		{ "BLAZE side-forward", 421, 525, HotspotLabelSide.Left },
		{ "BLAZE side-back", 421, 525, HotspotLabelSide.Left },
		{ "BLAZE left-click", 421, 575, HotspotLabelSide.Left },
		{ "TAIPAN side-forward", 460, 500, HotspotLabelSide.Left },
		{ "TAIPAN side-back", 460, 500, HotspotLabelSide.Left },
		{ "TAIPAN left-click", 460, 554, HotspotLabelSide.Left },
	};

	[Theory]
	[MemberData(nameof(HotspotGeometry))]
	public void ALabelNeverLandsOnTheMarkerItPointsAt(string part, double buttonX, double targetX, HotspotLabelSide side)
	{
		// 568 is the narrowest panel at the 1200 design window, 808 what the Macros panel gets at
		// the default 1440, 1236 the design canvas where nothing is squeezed at all.
		foreach (var canvas in new double[] { 568, 792, 808, 923, 1228, 1236 })
		{
			var hotspot = new DevicePartHotspotViewModel
			{
				Id = part,
				Label = "Side Forward",
				Description = string.Empty,
				ButtonPoint = new Point(buttonX, 300),
				TargetPoint = new Point(targetX, 300),
				LabelSide = side,
			};
			hotspot.SetCanvasWidth(canvas);

			var boxRight = hotspot.ButtonX + hotspot.ButtonWidth;
			var markerLeft = hotspot.TargetX;

			Assert.True(boxRight <= markerLeft,
				$"{part} at canvas {canvas}: the label box ends at {boxRight:0.#} but its marker " +
				$"starts at {markerLeft:0.#} — the label is sitting on the ring it labels");
		}
	}

	// ── Device artwork rasterisation ─────────────────────────────────────────────────────────
	//
	// The device-only bitmap used a fixed 934x1040 canvas with the artwork drawn at the top-left.
	// Those constants suit BLAZE, which fills them to within a pixel. A landscape headset landed in
	// the top ~67 % with 340 px of dead space beneath, so the picture stopped occupying its own SVG
	// viewBox — and the glow geometry, which lives in that viewBox, rendered off the device.

	[Fact]
	public void ALandscapeArtworkKeepsItsAspect()
	{
		// Starlight.
		var (w, h) = DeviceDetailsViewModel.AspectPreservingSize(959.76f, 719.76f);

		Assert.Equal(959.76 / 719.76, (double)w / h, 2);
	}

	[Fact]
	public void APortraitArtworkKeepsItsAspect()
	{
		// Cosmo is very slightly taller than wide.
		var (w, h) = DeviceDetailsViewModel.AspectPreservingSize(2360f, 2399f);

		Assert.Equal(2360.0 / 2399.0, (double)w / h, 2);
	}

	// The long side carries the resolution, whichever side that is — otherwise a wide artwork would
	// be rendered at a fraction of the detail a tall one gets.
	[Fact]
	public void TheLongSideCarriesTheResolution()
	{
		var (landscapeW, landscapeH) = DeviceDetailsViewModel.AspectPreservingSize(2000f, 1000f);
		var (portraitW, portraitH) = DeviceDetailsViewModel.AspectPreservingSize(1000f, 2000f);

		Assert.True(landscapeW > landscapeH);
		Assert.True(portraitH > portraitW);
		Assert.Equal(landscapeW, portraitH);
	}

	// A degenerate picture must not produce a zero-sized bitmap, which SKBitmap rejects.
	[Theory]
	[InlineData(1000f, 1f)]
	[InlineData(1f, 1000f)]
	public void AnExtremeAspectStillProducesADrawableBitmap(float width, float height)
	{
		var (w, h) = DeviceDetailsViewModel.AspectPreservingSize(width, height);

		Assert.True(w >= 1);
		Assert.True(h >= 1);
	}

	// ── Glow catalog ─────────────────────────────────────────────────────────────────────────

	[Theory]
	[InlineData("Cosmo")]
	[InlineData("Starlight")]
	public void BothHeadsetsHaveGlowArt(string device)
	{
		var art = DeviceGlowArtCatalog.For(device);

		Assert.NotNull(art);
		Assert.True(art!.CanvasWidth > 0 && art.CanvasHeight > 0);
		Assert.NotEmpty(art.LayersBehind);
		Assert.NotEmpty(art.LayersOver);
	}

	// The split is the fix: the crisp core goes UNDER the artwork and the blurred halo OVER it,
	// matching the source SVG's own draw order (rgb-solid, photo, rgb-blur). Putting both behind
	// leaves a thin line under the device, because the photo is opaque where the glow sits.
	[Theory]
	[InlineData("Cosmo")]
	[InlineData("Starlight")]
	public void TheUnderLayerIsCrispAndTheOverLayerIsBlurred(string device)
	{
		var art = DeviceGlowArtCatalog.For(device)!;

		Assert.All(art.LayersBehind, l => Assert.Equal(0, l.BlurRadius));
		Assert.All(art.LayersOver, l => Assert.True(l.BlurRadius > 0));
	}

	// Every layer must actually be drawable, and widened — an empty geometry renders nothing at all
	// and would look exactly like the feature being switched off.
	[Theory]
	[InlineData("Cosmo")]
	[InlineData("Starlight")]
	public void EveryLayerHasGeometryAndWidth(string device)
	{
		var art = DeviceGlowArtCatalog.For(device)!;

		foreach (var layer in art.LayersBehind.Concat(art.LayersOver))
		{
			Assert.StartsWith("M", layer.Geometry);
			Assert.True(layer.StrokeThickness > 0);
		}
	}

	// A device with no glow art must resolve to null rather than borrowing another device's shape.
	[Theory]
	[InlineData("BLAZE")]
	[InlineData("PG TAIPAN")]
	[InlineData("")]
	[InlineData(null)]
	public void ADeviceWithoutGlowArtGetsNone(string? device)
	{
		Assert.Null(DeviceGlowArtCatalog.For(device));
	}

	// The artwork paths the prewarm must leave alone. If one drifts from the catalog rows, the
	// rasterizer re-frames that artwork again and the glow silently goes back off the device.
	[Fact]
	public void EveryGlowArtworkPathIsAVectorUnderHeadphones()
	{
		Assert.NotEmpty(DeviceGlowArtCatalog.ArtworkPaths);
		Assert.All(DeviceGlowArtCatalog.ArtworkPaths, p =>
		{
			Assert.StartsWith("/Assets/Headphones/", p);
			Assert.EndsWith("-Vector.svg", p);
		});
	}
}
