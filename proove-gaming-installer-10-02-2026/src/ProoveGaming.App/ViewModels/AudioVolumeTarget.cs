using System;

namespace ProoveGaming.App.ViewModels;

// Where a device's audio level is applied. Carried per device in the catalog rather than assumed,
// because the two are not interchangeable: Cosmo's buttons and sliders move the OS volume of its
// own endpoints, while another headset may hold its level in firmware and leave the OS alone.
public enum AudioVolumeTarget
{
	// The device's own Windows render/capture endpoints (Core Audio). What Cosmo does.
	OperatingSystem,
	// A level held on the device itself, written over its vendor protocol. No device implements
	// this yet — see docs/plans/add-cosmo-headset.
	Device,
}

public static class AudioVolumeTargetParser
{
	// "" (an older DeviceHost, or a device that declares nothing) reads as OperatingSystem, so a
	// catalog that says nothing behaves like every device does today.
	public static AudioVolumeTarget Parse(string? value)
		=> string.Equals(value, nameof(AudioVolumeTarget.Device), StringComparison.OrdinalIgnoreCase)
			? AudioVolumeTarget.Device
			: AudioVolumeTarget.OperatingSystem;
}
