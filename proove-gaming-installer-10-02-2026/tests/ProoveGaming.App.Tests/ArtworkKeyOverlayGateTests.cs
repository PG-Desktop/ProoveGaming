using System.Linq;
using System.Threading.Tasks;
using ProoveGaming.App.Models;
using ProoveGaming.App.ViewModels;
using ProoveGaming.App.Models.Keyboards;
using Xunit;

namespace ProoveGaming.App.Tests;

// Which boards draw their key overlays on the rendered artwork, and which fall back to the abstract
// canvas. This gate is load-bearing and was previously untested: keying it on "has split artwork"
// rather than "has measured bounds for THIS artwork" routed Cyrex and Aress through Raster's keycap
// coordinates, and the whole suite stayed green because the Cyrex tests assert view-model
// properties while the gating decision is consumed imperatively by the overlay view.
public class ArtworkKeyOverlayGateTests
{
	private static async Task<DeviceDetailsViewModel> VmFor(string name, int vendorId, int productId, string typeLabel = "Wired Keyboard")
	{
		var vm = new DeviceDetailsViewModel();
		await vm.SetDeviceAsync(new DoubleCardViewModel
		{
			Name = name,
			DeviceTypeLabel = typeLabel,
			VendorId = vendorId,
			ProductId = productId,
			SettingsVendorId = vendorId,
			SettingsProductId = productId,
			DeviceKey = string.Empty,
			SettingsDeviceKey = string.Empty,
		});
		return vm;
	}

	private static Task<DeviceDetailsViewModel> Raster() => VmFor("Raster", 14000, 12385, "Wireless Keyboard");
	private static Task<DeviceDetailsViewModel> Cyrex() => VmFor("Cyrex", 10473, 12914);
	private static Task<DeviceDetailsViewModel> Aress() => VmFor("Aress", 10473, 12910);
	private static Task<DeviceDetailsViewModel> TRacer() => VmFor("T-Racer", 14000, 12370, "Wireless Keyboard");
	private static Task<DeviceDetailsViewModel> Rampage() => VmFor("Rampage", 10473, 12888);
	private static Task<DeviceDetailsViewModel> T800() => VmFor("T800", 12625, 20525);

	[Fact]
	public async Task Raster_UsesArtworkOverlays()
	{
		var vm = await Raster();
		Assert.True(vm.UseArtworkKeyOverlays);
		Assert.Equal(RasterArtworkLayout.ViewBoxWidth, vm.ArtworkViewBoxWidth);
		Assert.Equal(RasterArtworkLayout.ViewBoxHeight, vm.ArtworkViewBoxHeight);
	}

	[Fact]
	public async Task Cyrex_UsesArtworkOverlays()
	{
		var vm = await Cyrex();
		Assert.True(vm.UseArtworkKeyOverlays);
		Assert.Equal(CyrexArtworkLayout.ViewBoxWidth, vm.ArtworkViewBoxWidth);
		Assert.Equal(CyrexArtworkLayout.ViewBoxHeight, vm.ArtworkViewBoxHeight);
	}

	// Aress draws its own artwork with its own measured table (AressArtworkLayout) — it used to
	// borrow Cyrex's because its artwork had no table; the borrow ended when one was measured.
	[Fact]
	public async Task Aress_UsesItsOwnArtworkAndTable()
	{
		var vm = await Aress();
		Assert.True(vm.UseArtworkKeyOverlays);
		Assert.Equal(AressArtworkLayout.ViewBoxWidth, vm.ArtworkViewBoxWidth);
		Assert.Equal(AressArtworkLayout.ViewBoxHeight, vm.ArtworkViewBoxHeight);
	}

	// KeyOverlayArt and KeyboardArt both resolve to the Aress artwork now — the overlay, the RGB
	// preview and the home card show the same board the box shipped with.
	[Fact]
	public async Task Aress_OverlayDrawsItsOwnArtwork()
	{
		var vm = await Aress();

		Assert.Equal(RgbKeyboardArtCatalog.For("Aress"), vm.KeyOverlayArt);
		Assert.Equal(vm.KeyboardArt, vm.KeyOverlayArt);
	}

	// The table is keyed by matrix slot, so the join must land on Aress's own key names — every
	// remappable key gets bounds, including the navigation keys whose drawn legends differ from
	// the physical board.
	[Fact]
	public async Task Aress_BoundsAreKeyedByItsOwnCatalogNames()
	{
		var vm = await Aress();

		Assert.All(AressKeyCatalog.Keys.Where(k => AressArtworkLayout.BySlot.ContainsKey(k.MatrixIndex)),
			k => Assert.True(vm.ArtworkKeyBounds.ContainsKey(k.DevKeyName),
				$"no overlay bounds for Aress key '{k.DevKeyName}' (slot {k.MatrixIndex})"));

		Assert.Equal(AressKeyCatalog.Keys.Count, vm.ArtworkKeyBounds.Count);
	}

	[Fact]
	public async Task TRacer_UsesArtworkOverlays()
	{
		var vm = await TRacer();
		Assert.True(vm.UseArtworkKeyOverlays);
		Assert.Equal(TRacerArtworkLayout.ViewBoxWidth, vm.ArtworkViewBoxWidth);
		Assert.Equal(TRacerArtworkLayout.ViewBoxHeight, vm.ArtworkViewBoxHeight);
	}

	[Fact]
	public async Task Rampage_UsesArtworkOverlays()
	{
		var vm = await Rampage();
		Assert.True(vm.UseArtworkKeyOverlays);
		Assert.Equal(RampageArtworkLayout.ViewBoxWidth, vm.ArtworkViewBoxWidth);
		Assert.Equal(RampageArtworkLayout.ViewBoxHeight, vm.ArtworkViewBoxHeight);
	}

	// Rampage's table is measured from its own artwork and keyed by its own catalog names, like
	// T-Racer's — no slot join, so every remappable key must resolve bounds directly.
	[Fact]
	public async Task Rampage_BoundsCoverEveryCatalogKey()
	{
		var vm = await Rampage();

		Assert.All(RampageKeyCatalog.Keys,
			k => Assert.True(vm.ArtworkKeyBounds.ContainsKey(k.DevKeyName),
				$"no overlay bounds for Rampage key '{k.DevKeyName}' (slot {k.MatrixIndex})"));
		Assert.Equal(RampageKeyCatalog.Keys.Count, vm.ArtworkKeyBounds.Count);
	}

	// The failure this phase can most plausibly introduce: a board resolving another board's table.
	// T-Racer is the most exposed to it — same vendor family as Raster and nearly the same layout,
	// so borrowing Raster's coordinates would look almost right and be wrong by a keycap.
	[Fact]
	public async Task EachBoard_ResolvesItsOwnArtworkTable()
	{
		var raster = await Raster();
		var cyrex = await Cyrex();
		var tracer = await TRacer();

		Assert.NotEqual(raster.ArtworkViewBoxWidth, cyrex.ArtworkViewBoxWidth);
		Assert.NotEqual(raster.ArtworkViewBoxWidth, tracer.ArtworkViewBoxWidth);
		Assert.NotEqual(cyrex.ArtworkViewBoxWidth, tracer.ArtworkViewBoxWidth);

		// Raster's table is keyed by its own catalog, Cyrex's by its own — so each board's bounds
		// dictionary must cover its own key names, not the other's.
		Assert.All(CyrexKeyCatalog.Keys.Where(k => CyrexArtworkLayout.BySlot.ContainsKey(k.MatrixIndex)),
			k => Assert.True(cyrex.ArtworkKeyBounds.ContainsKey(k.DevKeyName)));
		Assert.All(RasterKeyCatalog.Keys.Where(k => RasterArtworkLayout.Layout.ContainsKey(k.DevKeyName)),
			k => Assert.True(raster.ArtworkKeyBounds.ContainsKey(k.DevKeyName)));
		Assert.All(TRacerKeyCatalog.Keys,
			k => Assert.True(tracer.ArtworkKeyBounds.ContainsKey(k.DevKeyName)));
	}

	// The overlay view sizes the art layer and the button canvas from the same stage dimensions, so
	// a catalog Width/Height that disagrees with the table's viewBox scales every overlay off the
	// keycaps it was measured against — with no other symptom to notice it by.
	[Theory]
	[InlineData("Raster", 14000, 12385)]
	[InlineData("T-Racer", 14000, 12370)]
	[InlineData("Cyrex", 10473, 12914)]
	[InlineData("Aress", 10473, 12910)]
	[InlineData("Rampage", 10473, 12888)]
	[InlineData("T800", 12625, 20525)]
	public async Task ArtworkDimensions_MatchTheMeasuredTable(string name, int vendorId, int productId)
	{
		var vm = await VmFor(name, vendorId, productId);
		var art = vm.KeyOverlayArt;

		Assert.NotNull(art);
		Assert.Equal(vm.ArtworkViewBoxWidth, art!.Width);
		Assert.Equal(vm.ArtworkViewBoxHeight, art.Height);
	}

	// T800 is a third protocol family (RY5088) with its own catalog and its own measured table.
	[Fact]
	public async Task T800_UsesItsOwnArtworkOverlays()
	{
		var vm = await T800();

		Assert.True(vm.UseArtworkKeyOverlays);
		Assert.Equal(T800ArtworkLayout.ViewBoxWidth, vm.ArtworkViewBoxWidth);
		Assert.Equal(T800ArtworkLayout.ViewBoxHeight, vm.ArtworkViewBoxHeight);
		Assert.Equal(RgbKeyboardArtCatalog.For("T800"), vm.KeyOverlayArt);
		Assert.Equal(vm.KeyboardArt, vm.KeyOverlayArt);
	}

	[Fact]
	public async Task T800_BoundsCoverItsWholeCatalog()
	{
		var vm = await T800();

		Assert.Equal(T800KeyCatalog.Keys.Count, vm.ArtworkKeyBounds.Count);
		Assert.All(T800KeyCatalog.Keys,
			k => Assert.True(vm.ArtworkKeyBounds.ContainsKey(k.DevKeyName),
				$"no overlay bounds for T800 key '{k.DevKeyName}' (slot {k.MatrixIndex})"));
	}

	// Generated-data sanity: a mis-run labelling pass shows up as a slot in the table that no
	// catalog key claims, or a remappable key with no bounds.
	[Fact]
	public void CyrexArtworkLayout_CoversEveryCatalogKeyAndNothingElse()
	{
		var catalogSlots = CyrexKeyCatalog.Keys.Select(k => k.MatrixIndex).ToHashSet();

		Assert.Equal(catalogSlots.Count, CyrexArtworkLayout.BySlot.Count);
		Assert.All(CyrexArtworkLayout.BySlot.Keys, slot => Assert.Contains(slot, catalogSlots));

		// FN is not remappable and must not have an overlay.
		Assert.DoesNotContain(SmartInfinite65Geometry.FnMatrixIndex, CyrexArtworkLayout.BySlot.Keys);
	}

	// Every measured rect must sit inside the artwork it was measured from — a transform applied
	// wrongly shows up here rather than as silently offset overlays on screen.
	[Fact]
	public void CyrexArtworkLayout_BoundsLieWithinTheViewBox()
	{
		Assert.All(CyrexArtworkLayout.BySlot.Values, b =>
		{
			Assert.True(b.X >= 0 && b.Y >= 0, $"negative origin: {b.X},{b.Y}");
			Assert.True(b.X + b.Width <= CyrexArtworkLayout.ViewBoxWidth, $"overflows width: {b.X + b.Width}");
			Assert.True(b.Y + b.Height <= CyrexArtworkLayout.ViewBoxHeight, $"overflows height: {b.Y + b.Height}");
			Assert.True(b.Width > 0 && b.Height > 0, "zero-sized keycap");
		});
	}

	// Generated-data sanity for Aress, mirroring Cyrex's: the labelling pass found 68 components,
	// FN was dropped, and every remaining slot must belong to the catalog.
	[Fact]
	public void AressArtworkLayout_CoversEveryCatalogKeyAndNothingElse()
	{
		var catalogSlots = AressKeyCatalog.Keys.Select(k => k.MatrixIndex).ToHashSet();

		Assert.Equal(catalogSlots.Count, AressArtworkLayout.BySlot.Count);
		Assert.All(AressArtworkLayout.BySlot.Keys, slot => Assert.Contains(slot, catalogSlots));

		Assert.DoesNotContain(SmartInfinite65Geometry.FnMatrixIndex, AressArtworkLayout.BySlot.Keys);
	}

	[Fact]
	public void AressArtworkLayout_BoundsLieWithinTheViewBox()
	{
		Assert.All(AressArtworkLayout.BySlot.Values, b =>
		{
			Assert.True(b.X >= 0 && b.Y >= 0, $"negative origin: {b.X},{b.Y}");
			Assert.True(b.X + b.Width <= AressArtworkLayout.ViewBoxWidth, $"overflows width: {b.X + b.Width}");
			Assert.True(b.Y + b.Height <= AressArtworkLayout.ViewBoxHeight, $"overflows height: {b.Y + b.Height}");
			Assert.True(b.Width > 0 && b.Height > 0, "zero-sized keycap");
		});
	}

	// Generated-data sanity for Rampage: a stale regeneration (renamed catalog entry, re-exported
	// artwork) shows up as a missing or orphaned name rather than as offset overlays on screen.
	[Fact]
	public void RampageArtworkLayout_CoversEveryCatalogKeyAndNothingElse()
	{
		var catalogNames = RampageKeyCatalog.Keys.Select(k => k.DevKeyName).ToHashSet();

		Assert.Equal(catalogNames.Count, RampageArtworkLayout.Layout.Count);
		Assert.All(RampageArtworkLayout.Layout.Keys, name => Assert.Contains(name, catalogNames));
	}

	[Fact]
	public void RampageArtworkLayout_BoundsLieWithinTheViewBox()
	{
		Assert.All(RampageArtworkLayout.Layout.Values, b =>
		{
			Assert.True(b.X >= 0 && b.Y >= 0, $"negative origin: {b.X},{b.Y}");
			Assert.True(b.X + b.Width <= RampageArtworkLayout.ViewBoxWidth, $"overflows width: {b.X + b.Width}");
			Assert.True(b.Y + b.Height <= RampageArtworkLayout.ViewBoxHeight, $"overflows height: {b.Y + b.Height}");
			Assert.True(b.Width > 0 && b.Height > 0, "zero-sized keycap");
		});
	}
}
