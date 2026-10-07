using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Interfaces;

/// <summary>
/// Global actuation (key press force) settings of a magnetic keyboard — actuation point, release
/// point and Rapid Trigger. The keyboard's flash is the source of truth: there is no host-side
/// persistence on this path.
/// </summary>
public interface IKeyboardActuationService
{
	/// <summary>
	/// Reads the current values and the device's adjustable ranges. Null when the device does not
	/// support actuation (mechanical board, incoherent geometry, or the probe read failed) — in
	/// which case no write must ever be attempted.
	/// </summary>
	Task<KeyboardActuationState?> ReadAsync(CancellationToken cancellationToken = default);

	/// <summary>Applies one set of values to every key and verifies the write took.</summary>
	Task<bool> WriteAsync(KeyboardActuationValues values, CancellationToken cancellationToken = default);

	/// <summary>
	/// Applies one set of values to the listed slots only, leaving every other key untouched, and
	/// verifies the write took. Slots outside the device's table are ignored; an empty list is a
	/// no-op that reports failure.
	/// </summary>
	Task<bool> WriteKeysAsync(IReadOnlyCollection<int> slots, KeyboardActuationValues values, CancellationToken cancellationToken = default);
}

public interface IDeviceActuationServiceFactory
{
	/// <summary>
	/// Returns null if the device isn't a magnetic keyboard with actuation support, or the required
	/// HID interface isn't currently enumerated.
	/// </summary>
	IKeyboardActuationService? CreateForDevice(int vendorId, int productId);
}
