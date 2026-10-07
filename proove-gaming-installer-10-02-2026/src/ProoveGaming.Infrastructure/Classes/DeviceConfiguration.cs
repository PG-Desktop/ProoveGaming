using ProoveGaming.Infrastructure.Services;

namespace ProoveGaming.Infrastructure.Classes;

// The full desired configuration of a mouse, applied in one coordinated device write
// (IMouseRgbService.ApplyConfigurationAsync). Each member is optional: null / empty means
// "leave that aspect at the device's current value". Assembled by the DeviceHost from an
// ApplyDeviceConfigurationRequest.
public sealed record DeviceConfiguration
{
	// RGB colour + effect; null = don't touch the LED.
	public LedConfig? Led { get; init; }

	// Full desired set for the remappable buttons; null = don't touch bindings.
	public IReadOnlyList<ButtonBinding>? Buttons { get; init; }

	// Debounce / lift-off; null = don't touch mouse settings.
	public MouseSettings? Settings { get; init; }

	// DPI table; null/empty = don't touch the DPI stages.
	public DpiStageParams[]? DpiStages { get; init; }

	// Active DPI stage index; null = don't touch.
	public byte? ActiveStage { get; init; }

	// Report (polling) rate in Hz; null = don't touch.
	public int? ReportRateHz { get; init; }
}

public readonly record struct LedConfig(byte R, byte G, byte B, byte Effect, byte Brightness, byte Speed);

public readonly record struct MouseSettings(byte Debounce, byte LiftOff);
