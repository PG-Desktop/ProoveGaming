using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Interfaces;

/// <summary>
/// How one Extended Keys write ended. <see cref="Verified"/> — the board read back exactly what was
/// asked; <see cref="Rejected"/> — the request was refused (out-of-range or blocked slot, an
/// unmanaged mode, an overflowing area) or the board twice answered with different bytes. Both are
/// final; a transport failure is a <c>null</c> result and is the only thing worth retrying. A single
/// read-back mismatch is neither: it means the board is torn, so the same call lays the set down
/// once more against a fresh snapshot before it reports anything.
/// </summary>
public enum ExtendedKeysWriteResult { Verified, Rejected }

/// <summary>
/// How one Extended Keys read ended. <see cref="Read"/> — the board answered and its state is here.
/// <see cref="Unsupported"/> — the board answered and has no advanced-key area this build can use: a
/// verdict about the hardware, final until the device changes. <see cref="Failed"/> — the board could
/// not be talked to: a fact about the transport, and it must never be reported as a verdict, because
/// the App hides the whole section on "unsupported" — and with it the one control that repairs a
/// board (DEFAULT SETTINGS).
/// <see cref="Failed"/> is first so that a default-constructed result is the harmless one rather
/// than a <see cref="Read"/> carrying no state.
/// </summary>
public enum ExtendedKeysReadStatus { Failed, Unsupported, Read }

/// <summary>
/// The result of one read: <see cref="State"/> is non-null exactly when <see cref="Status"/> is
/// <see cref="ExtendedKeysReadStatus.Read"/>.
/// </summary>
public readonly record struct ExtendedKeysReadResult(ExtendedKeysReadStatus Status, ExtendedKeysState? State)
{
	public static ExtendedKeysReadResult Unsupported => new(ExtendedKeysReadStatus.Unsupported, null);

	public static ExtendedKeysReadResult Failed => new(ExtendedKeysReadStatus.Failed, null);

	public static ExtendedKeysReadResult Read(ExtendedKeysState state) => new(ExtendedKeysReadStatus.Read, state);
}

/// <summary>
/// The Extended Keys (vendor: "advanced keys") of a Smart Infinite magnetic keyboard — Rapid Snap,
/// Instant Release, Mod-Tap and Toggle, all stored in one region and written as one set.
/// </summary>
public interface IKeyboardExtendedKeysService
{
	/// <summary>
	/// Reads the managed records, the area size and the blocked slots.
	/// <see cref="ExtendedKeysReadStatus.Unsupported"/> when the board has no advanced-key area or
	/// does not accept matrix writes — no write must ever be attempted for it;
	/// <see cref="ExtendedKeysReadStatus.Failed"/> when the board could not be read, which says
	/// nothing about the hardware and must be retried rather than shown as a verdict.
	/// <b>May write:</b> an area that does not parse against the matrix pointers is repaired in
	/// place before the read answers — see the implementation for why that is the only recovery. Such
	/// a read is flagged <see cref="ExtendedKeysState.Repaired"/>, because what it reports is what
	/// survived the reset and a caller must not save that as the user's state.
	/// </summary>
	Task<ExtendedKeysReadResult> ReadAsync(CancellationToken cancellationToken = default);

	/// <summary>
	/// Replaces the board's managed records with <paramref name="desired"/> — the <b>full</b> set, an
	/// empty list meaning "none". Records the App does not manage are preserved byte-for-byte and
	/// re-pointed. Verified by reading both regions back.
	/// </summary>
	Task<ExtendedKeysWriteResult?> WriteAsync(IReadOnlyList<ExtendedKeyEntry> desired, CancellationToken cancellationToken = default);
}

public interface IDeviceExtendedKeysServiceFactory
{
	/// <summary>
	/// Returns null if the device is not a keyboard with an advanced-key area, or the required HID
	/// interface is not currently enumerated.
	/// </summary>
	IKeyboardExtendedKeysService? CreateForDevice(int vendorId, int productId);
}
