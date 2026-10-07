using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Interfaces;

/// <summary>
/// One instance per physical keyboard. Implementations encode the vendor's key-matrix wire
/// protocol for one device family (Raster uses the QMK-like "user key" protocol; see
/// <c>RasterKeyboardKeyMappingService</c>).
/// </summary>
/// <remarks>
/// Cancellation contract: the token is the gRPC call's — when the App's deadline expires, the
/// host must stop and release the per-device gate instead of grinding through retries against a
/// wedged device while every later operation queues behind it. Implementations observe it
/// between packets and between retry attempts (a single HID exchange is at most one
/// stream-timeout long), and a write abandoned mid-transfer is safe on every supported family:
/// the firmware only stores a table when the full declared length arrives.
/// </remarks>
public interface IKeyboardKeyMappingService
{
	/// <summary>
	/// Overwrite a single key in the keyboard's user-key matrix for profile 0. Each key is
	/// described by 3 bytes: <c>type</c>, <c>code1</c>, <c>code2</c>.
	/// </summary>
	/// <param name="matrixIndex">Flat matrix index (memory offset = 3 * matrixIndex).</param>
	/// <param name="type">Key type byte: 0x10=HID keyboard, 0x30=mouse/wheel, 0x50=media.</param>
	/// <param name="code1">Type 0x10: modifier bitmask; type 0x50: consumer index.</param>
	/// <param name="code2">Type 0x10: HID scancode (e.g. 0x04=A, 0x29=Esc).</param>
	Task<bool> SetKeyMappingAsync(byte matrixIndex, byte type, byte code1, byte code2, CancellationToken cancellationToken = default);

	/// <summary>
	/// Probes the factory-default key matrix by iterating slots 0..<paramref name="maxSlot"/>-1
	/// with GetDefault0 (0x16). Returns a map <c>HID scancode → matrix slot</c> for slots that
	/// carry plain keyboard records (type=0x10, code1=0). Slots with media / Fn / mouse records
	/// or empty slots are excluded.
	/// </summary>
	Task<IReadOnlyDictionary<byte, byte>?> ProbeDefaultMatrixAsync(int maxSlot = 128, CancellationToken cancellationToken = default);

	/// <summary>
	/// Probes the current user-key overrides by iterating slots 0..<paramref name="maxSlot"/>-1
	/// with GetUserKey0 (0x1A). Returns a map <c>slot → override code2</c> for slots with an
	/// active override (type=0x10, code1=0). Empty slots (type=0x0F, "no override") are excluded.
	/// This is the truth source for the current keyboard state after the user has done a manual
	/// factory reset — do not rely on local storage instead.
	/// </summary>
	Task<IReadOnlyDictionary<byte, byte>?> ProbeUserOverridesAsync(int maxSlot = 128, CancellationToken cancellationToken = default);

	/// <summary>
	/// Reads the device's key matrix as <c>slot → {type, code1, code2}</c>. Returns <c>null</c> if
	/// the read failed — callers must treat that as "do not write", never as "the table is empty".
	/// </summary>
	/// <remarks>
	/// The slot-keyed successor to <see cref="ProbeDefaultMatrixAsync"/> /
	/// <see cref="ProbeUserOverridesAsync"/>, and the read path new code should use. It keeps the
	/// whole record rather than just the HID usage code, which the scancode-keyed pair cannot
	/// express: modifier keys carry their identity in <c>code1</c> with a <c>0x00</c> usage code and
	/// are therefore invisible to a scancode-keyed map. Raster and T-Racer still expose the older
	/// pair because their firmware has a genuine factory-vs-override split this single read does not
	/// model; the Smart Infinite family implements only this one.
	/// </remarks>
	Task<IReadOnlyDictionary<byte, KeyMatrixRecord>?> ReadKeyMatrixAsync(int maxSlot = 128, CancellationToken cancellationToken = default);
}
