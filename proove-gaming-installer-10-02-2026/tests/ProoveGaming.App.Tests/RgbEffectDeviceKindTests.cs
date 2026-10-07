using System.Collections.Generic;
using ProoveGaming.App;
using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// Whether an effect's brightness slider is worth offering depends on the DEVICE, not on the effect:
// BLAZE's Wave is protocol 0x04, where byte 3 gates whether the effect renders rather than setting a
// level, so anything the user picked would be silently discarded. The effect catalog stays
// device-agnostic and DeviceDetailsViewModel.RecomputeVisibleRgbEffects pushes the capability in.
public class RgbEffectDeviceKindTests
{
	private static RgbEffectSettingsViewModel Wave() => new(
		"Wave",
		"DeviceDetails_Rgb_Effect_Wave",
		hasColorPicker: false,
		hasBrightness: true,
		hasSpeed: true,
		_ => { });

	private static RgbEffectSettingsViewModel Static() => new(
		"Static",
		"DeviceDetails_Rgb_Effect_Static",
		hasColorPicker: true,
		hasBrightness: true,
		hasSpeed: false,
		_ => { });

	[Fact]
	public void SetBrightnessSupported_False_HidesTheSlider()
	{
		var effect = Wave();

		effect.SetBrightnessSupported(false);

		Assert.False(effect.HasBrightness);
	}

	// The same view-model instance is reused as the user moves between devices, so the capability has
	// to come back, not just go away once.
	[Fact]
	public void SetBrightnessSupported_MovesBothWays()
	{
		var effect = Wave();

		effect.SetBrightnessSupported(false);
		effect.SetBrightnessSupported(true);

		Assert.True(effect.HasBrightness);
	}

	// A device cannot add a control the effect does not have — the declared capability is the ceiling.
	[Fact]
	public void SetBrightnessSupported_True_CannotAddBrightnessToAnEffectWithoutIt()
	{
		var noBrightness = new RgbEffectSettingsViewModel(
			"Off", "DeviceDetails_Rgb_Effect_Static",
			hasColorPicker: false, hasBrightness: false, hasSpeed: false, _ => { });

		noBrightness.SetBrightnessSupported(true);

		Assert.False(noBrightness.HasBrightness);
	}

	// The radio row is a fixed 56px unless it carries the expandable class, which binds to this.
	// Losing brightness must NOT collapse the row while the effect still has a speed slider — that
	// would clip the mouse's Wave speed control out of view.
	[Fact]
	public void HasExpandableContent_StaysTrueWhenOnlySpeedRemains()
	{
		var effect = Wave();

		effect.SetBrightnessSupported(false);

		Assert.False(effect.HasBrightness);
		Assert.False(effect.HasColorPicker);
		Assert.True(effect.HasSpeed);
		Assert.True(effect.HasExpandableContent);
	}

	[Fact]
	public void HasExpandableContent_IsFalseWhenNothingIsLeftToReveal()
	{
		var effect = Static();   // colour picker + brightness, no speed

		effect.SetBrightnessSupported(false);

		// The colour picker alone still justifies expanding.
		Assert.True(effect.HasExpandableContent);

		var bare = new RgbEffectSettingsViewModel(
			"Off", "DeviceDetails_Rgb_Effect_Static",
			hasColorPicker: false, hasBrightness: false, hasSpeed: false, _ => { });

		Assert.False(bare.HasExpandableContent);
	}

	// HasBrightness drives a visibility binding and feeds the row's expandable class, so both have to
	// NOTIFY — as plain auto-properties the slider would stay on screen until the panel was rebuilt.
	[Fact]
	public void SetBrightnessSupported_RaisesPropertyChangedForBothBoundProperties()
	{
		var effect = Wave();
		var changed = new List<string?>();
		effect.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

		effect.SetBrightnessSupported(false);

		Assert.Contains(nameof(RgbEffectSettingsViewModel.HasBrightness), changed);
		Assert.Contains(nameof(RgbEffectSettingsViewModel.HasExpandableContent), changed);
	}

	// The name is fixed per effect now — a mouse gets the Wave entry itself rather than a ColorCycle
	// entry wearing Wave's label, so nothing rewrites it and a language switch still resolves it.
	[Fact]
	public void DisplayName_ResolvesFromItsOwnKey()
	{
		Assert.Equal(LocalizationManager.GetString("DeviceDetails_Rgb_Effect_Wave"), Wave().DisplayName);
	}
}
