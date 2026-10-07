using System.Collections.ObjectModel;

namespace ProoveGaming.Infrastructure.Classes;

/// <summary>
/// One global actuation configuration for a magnetic keyboard. All travel values are in
/// <b>hundredths of a millimetre</b> (2.00 mm = 200) — the unit the gRPC contract carries, chosen
/// because T800 speaks it natively and Cyrex/Aress convert by their device-reported step.
/// </summary>
public sealed record KeyboardActuationValues(
	int PressCentiMm,
	int ReleaseCentiMm,
	bool RapidTrigger,
	int QuickPressCentiMm,
	int QuickReleaseCentiMm);

/// <summary>
/// The adjustable ranges and factory defaults a keyboard reports (or, for T800, the documented
/// constants). Travel fields are in hundredths of a millimetre; <see cref="StepMicrometres"/> is
/// the device's value granularity (Cyrex: 10 µm = 0.01 mm).
/// </summary>
public sealed record KeyboardActuationLimits(
	int PressMinCentiMm,
	int PressMaxCentiMm,
	int RtMinCentiMm,
	int RtMaxCentiMm,
	int StepMicrometres,
	KeyboardActuationValues Defaults);

/// <summary>Current values plus the ranges they can move in — the read-path result.</summary>
public sealed record KeyboardActuationState(
	KeyboardActuationValues Values,
	KeyboardActuationLimits Limits)
{
	/// <summary>
	/// Current values per populated slot (matrix/travel slot → values), read from the same table the
	/// global <see cref="Values"/> are reduced from. Empty when the family service predates per-key
	/// support — consumers treat that as "per-key editing unavailable", never as "no keys".
	/// </summary>
	public IReadOnlyDictionary<int, KeyboardActuationValues> PerKeyValues { get; init; } =
		ReadOnlyDictionary<int, KeyboardActuationValues>.Empty;
}
