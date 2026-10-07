using System;
using System.Collections.Generic;

namespace ProoveGaming.App.Models;

/// <summary>One drawn layer of a device's decorative glow.</summary>
/// <param name="Geometry">Path data in the artwork's own coordinate space.</param>
/// <param name="BlurRadius">Blur for this layer, in that same space. 0 draws it crisp.</param>
/// <param name="StrokeThickness">
/// Widens the band by stroking its own outline, so it thickens symmetrically and stays registered
/// with the artwork. Scaling the geometry instead would move it off the ear cup, and raising the
/// blur alone spreads the bloom without making the line itself read wider. Expressed as a fraction
/// of canvas width upstream, so both products widen by the same visual amount despite one being a
/// 960-wide space and the other 2360.
/// </param>
public sealed record DeviceGlowLayer(string Geometry, double BlurRadius, double StrokeThickness);

/// <summary>The animated glow for a device whose details page shows one.</summary>
/// <param name="CanvasWidth">Width of the artwork's coordinate space (its SVG viewBox).</param>
/// <param name="CanvasHeight">Height of that space.</param>
/// <param name="LayersBehind">Drawn UNDER the artwork — light spilling out past the device.</param>
/// <param name="LayersOver">Drawn OVER the artwork — the bloom lying on the device itself.</param>
public sealed record DeviceGlowArt(
	double CanvasWidth,
	double CanvasHeight,
	IReadOnlyList<DeviceGlowLayer> LayersBehind,
	IReadOnlyList<DeviceGlowLayer> LayersOver);

/// <summary>
/// Glow outlines for devices that show a decorative animated glow on their details page.
/// </summary>
/// <remarks>
/// Lifted verbatim from each device's artwork SVG, and the artwork shipped as <c>RgbImagePath</c>
/// has those same paths REMOVED — Avalonia renders SVG but does not run the CSS custom property or
/// the animation the file declares, so the SVG's own copy would be a frozen fill.
///
/// The SPLIT matters and is taken from the source file's draw order, not guessed. In both SVGs the
/// elements appear as: <c>rgb-solid</c>, then the headset photo, then <c>rgb-blur</c>. So the crisp
/// core belongs BEHIND the device — light escaping around the ear cup — and the blurred halo belongs
/// ON TOP of it, lying over the picture. Putting both behind leaves only a thin line visible under
/// the device, because the photo is opaque where the glow actually sits.
///
/// Only the over-layer is blurred; the SVG applies its filter to <c>rgb-blur</c> alone. The radius
/// is that filter's stdDeviation (15 Cosmo, 20 Starlight) scaled up, because Avalonia's
/// BlurEffect.Radius is not the same unit.
///
/// Decorative only: Cosmo and Starlight keep <c>SupportsRgb: false</c>, so there is no RGB section,
/// no colour picker, and nothing is ever written to the hardware.
/// </remarks>
public static class DeviceGlowArtCatalog
{
	private static readonly Dictionary<string, DeviceGlowArt> Map =
		new(StringComparer.OrdinalIgnoreCase)
		{
			["Cosmo"] = new(
				CanvasWidth: 2360,
				CanvasHeight: 2399,
				LayersBehind:
				[
					new("M1531.035,1991.538c3.143,2.687-13.246,13.362-17.139,13.921-16.023,2.304-60.766-15.159-73.76-25.98-64.071-53.355,25.908-189.554,61.289-240.479,56.368-81.132,128.903-159.407,201.519-226.301,46.117-42.483,94.858-82.433,149.944-112.925,1.404,1.915-4.985,36.625-7.095,39.865-2.077,3.189-44.802,31.202-52.652,37.31-51.75,40.263-106.031,92.954-151.171,140.706-58.017,61.373-187.023,211.189-196.336,293.46-6.623,58.504,36.817,72.418,85.401,80.422h0Z",
						BlurRadius: 0, StrokeThickness: 35),
					new("M1746.436,1979.8c-.439-4.391,4.116-7.759,7.447-9.526,10.616-5.631,32.599-9.572,45.546-14.429,70.07-26.283,139.499-65.184,179.72-130.15,54.421-87.901,63.726-205.448,57.214-306.533l11.906-23.15c5.668,62.79,1.069,126.38-10.605,188.282-9.196,48.76-23.814,97.685-49.733,140.187-51.176,83.915-150.831,127.547-241.494,155.319h0Z",
						BlurRadius: 0, StrokeThickness: 35),
				],
				LayersOver:
				[
					new("M1540.559,1985.323c-10.117,15.533-21.902,21.323-27.496,23.528-29.636,11.678-62.146-7.686-78.52-25.228-9.746-10.442-14.607-21.085-16.724-26.362-18.859-47.002,10.805-102.487,51.307-179.15,6.386-12.087,13.895-25.345,22.677-39.402,0,0,14.611-23.385,31.181-45.354,27.317-36.217,49.257-53.577,92.693-99.213,51.925-54.554,46.909-57.418,80.22-86.74,24.41-21.487,46.151-36.715,74.268-56.409,33.024-23.132,61.615-41.045,82.488-53.575,4.966,18.982,1.813,30.748-1.984,37.984-8.851,16.87-25.927,18.132-58.394,45.354-4.537,3.805-7.938,6.977-14.74,13.323-45.615,42.553-76.638,84.737-99.78,102.898-9.767,7.665-22.677,17.575-22.677,17.575-9.242,7.094-8.139,6.166-9.638,7.37-3.617,2.907-18.453,15.078-34.964,36.988-5.712,7.579-9.501,13.508-10.39,14.886-31.028,48.093-138.146,147.785-137.764,209.48.044,7.109.567,11.905.567,11.906,3.012,27.626,15.689,34.941,17.008,60.094.494,9.427-1.194,10.157.567,14.173,6.648,15.159,36.005,16.863,60.095,15.874h0Z",
						BlurRadius: 45, StrokeThickness: 35),
					new("M1746.071,1980.22c.796-2.522,2.379-6.871,5.386-11.622,13.597-21.485,40.844-26.754,49.323-28.63,50.992-11.281,132.713-56.812,175.181-115.937,11.561-16.095,18.289-30.52,22.961-40.535,14.1-30.229,21.963-60.511,28.063-112.252,4.525-38.384,8.265-90.149,6.047-152.504,5.921-9.701,11.843-19.402,17.764-29.102,8.175,42.381,15.937,113.006-3.402,195.213-4.98,21.171-19.87,77.594-58.33,140.198-1.166,1.899-16.468,26.688-31.245,44.904-59.54,73.401-159.282,100.165-211.748,110.268h0Z",
						BlurRadius: 45, StrokeThickness: 35),
				]),
			["Starlight"] = new(
				CanvasWidth: 959.76,
				CanvasHeight: 719.76,
				LayersBehind:
				[
					new("M646.811,396.063c-1.496,1.11-2.992,2.22-4.488,3.331-13.254,8.935-30.296,22.534-46.205,42.142-12.513,15.422-20.803,30.221-25.89,40.63-2.222,4.548-1.03,2.522-5.764,12.85-13.102,28.59-21.086,41.603-24.661,54.709-.302,1.108-1.806,6.735-4.063,14.457-1.069,3.657-1.955,6.577-2.457,8.22-.972-2.209-2.24-5.667-2.74-10.016-1.108-9.631,2.664-13.932,13.606-39.874,3.092-7.33,5.097-12.454,9.26-20.031,2.975-5.415,5.503-9.263,6.425-10.677,12.92-19.818,17.595-32.814,31.559-53.008,22.989-33.247,38.643-38.986,45.071-40.819,4.263-1.216,7.915-1.697,10.346-1.913h0Z",
						BlurRadius: 0, StrokeThickness: 14),
				],
				LayersOver:
				[
					new("M641.802,397.535l-1.131.674c-20.128,8.072-34.653,24.996-46.93,42.201-11.973,16.779-22.16,34.82-31.8,53.029-9.18,17.34-18.274,35.954-24.058,54.743-1.36,4.418-2.913,10.099-3.17,14.619-.012.208-.116.697-.367.77.029-.548-.05-1.126,0-1.671,1.794-19.926,12.218-40.955,21.068-58.664,10.977-21.966,23.029-43.883,37.202-63.95,10.921-15.462,25.973-32.994,43.951-40.093,1.663-.657,3.475-1.387,5.235-1.658h0Z",
						BlurRadius: 60, StrokeThickness: 14),
				]),

		};

	/// <summary>
	/// Artwork paths belonging to devices that draw their own glow layers.
	/// </summary>
	/// <remarks>
	/// Used by the device-only bitmap prewarm to leave these alone: that rasterizer re-frames the
	/// artwork into a fixed portrait canvas, which breaks the correspondence between the picture and
	/// the glow geometry (both live in the SVG's viewBox). See PrewarmDeviceOnlyBitmapAsync.
	/// </remarks>
	public static readonly IReadOnlyList<string> ArtworkPaths =
	[
		"/Assets/Headphones/Cosmo-Vector.svg",
		"/Assets/Headphones/Starlight-Vector.svg",
	];

	/// <summary>The glow art for a device name, or null when it has none.</summary>
	public static DeviceGlowArt? For(string? deviceName) =>
		!string.IsNullOrWhiteSpace(deviceName) && Map.TryGetValue(deviceName.Trim(), out var art)
			? art
			: null;
}
