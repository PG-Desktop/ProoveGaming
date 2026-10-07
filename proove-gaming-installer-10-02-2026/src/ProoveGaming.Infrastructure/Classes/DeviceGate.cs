using System.Collections.Concurrent;

namespace ProoveGaming.Infrastructure.Classes;

/// <summary>
/// Serialises access to one HID device path across <b>every</b> service in the process that talks
/// to it, regardless of device family.
/// </summary>
/// <remarks>
/// <para>
/// Several device families expose a single vendor interface that more than one service writes to,
/// and on all of them an interleaved frame corrupts the exchange in progress. The failure is the
/// same shape every time — two writers, one interface, no coordination — so it is held here once
/// rather than reimplemented per family. This type replaced the former per-family
/// <c>Ry5088DeviceGate</c> and <c>SmartInfiniteDeviceGate</c> copies, whose own remarks asked for
/// exactly that once a third family needed the same protection; TAIPAN was the third.
/// </para>
/// <para>
/// The three known hazards, kept concrete because they are what justify the cost — and because
/// each was diagnosed on hardware rather than reasoned about:
/// </para>
/// <para>
/// <b>RY5088 (T800).</b> The lighting and key-matrix services open the same <c>mi_02</c> feature
/// interface, and every exchange on it is a <c>SetFeature</c> followed by a <c>GetFeature</c>. That
/// pair is <b>not atomic</b> against another handle: between sending a request and reading the
/// reply, another thread can send its own, and <c>GetFeature</c> then returns the wrong answer.
/// Concretely, a key-matrix read is eight request/reply pairs, and an RGB write landing between two
/// of them hands the reader an LED reply, which is copied into the matrix as a page of garbage.
/// During a key <i>write</i> it is worse — the write is verified by reading the slot back, a stolen
/// reply fails that verification, and the retry writes again. Every accepted write sets
/// <c>FlashEnable</c>, so one user action can become three flash writes; the vendor notes for this
/// family warn that the config endpoint stops answering until re-enumeration when writes come too
/// fast.
/// </para>
/// <para>
/// <b>Smart Infinite (Cyrex / Aress / Rampage).</b> Replies are matched by echoed command +
/// address, and concurrent operations reuse the same addresses, so one exchange's reply satisfies
/// the other's check and both proceed on spliced data. Worse, an RGB <c>0x15</c> write is
/// documented to zero the key-assignment staging buffer, so an ungated RGB push landing inside a
/// key or macro table transfer silently voids it. (The <c>0x10</c>/<c>0x11</c> session bracket the
/// original gate guarded is no longer sent — the factory's 2026-08 clarifications dropped it — but
/// the interleaving hazard is unchanged.) Key mapping and all three RGB services open the same
/// 64/64 vendor interface, so the gate has to be shared between them rather than owned by any one.
/// </para>
/// <para>
/// <b>TAIPAN (Telink).</b> A configuration write is a sequenced multi-frame flash transaction:
/// <c>0xD1</c> header, which erases the sector, then <c>0xD2</c> chunks, then <c>ValidBankID</c>,
/// then the <c>0xD3</c> commit. <c>TaipanStateMonitor</c> writes a battery request to that same
/// collection on every refresh pass, so without this gate a request can land between the erase and
/// the commit — on the cable, which is the one transport where those writes actually take effect.
/// </para>
/// <para>
/// Held statically because the same DevicePath is shared across service families with differing
/// lifetimes — mouse and keyboard RGB services are cached by their factory for the process
/// lifetime, while key-mapping services are built fresh per call — so only a static gate reaches
/// them all; an instance field on any one service would serialise nothing across families. The
/// gates are process-lifetime and never disposed: the key set is
/// bounded by the device paths the machine has enumerated, and a released-then-recreated semaphore
/// would reopen exactly the window this type exists to close.
/// </para>
/// </remarks>
public static class DeviceGate
{
	private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// How long a user-initiated device write may wait for the gate before giving up.
	/// </summary>
	/// <remarks>
	/// Long enough to sit behind an ordinary configuration transfer — a key table or an actuation
	/// table is seconds of request/reply pairs, and an RGB write that gave up on those would look
	/// broken to the user. Short enough that a holder which never releases costs one failed write
	/// instead of a permanent queue. Single value rather than one per service so the four RGB
	/// services cannot drift apart.
	/// </remarks>
	public static readonly TimeSpan ForegroundWriteTimeout = TimeSpan.FromSeconds(5);

	/// <summary>
	/// Waits for exclusive access to <paramref name="devicePath"/>. Dispose the result — normally via
	/// <c>using</c> — to release it.
	/// </summary>
	public static async Task<IDisposable> AcquireAsync(string devicePath, CancellationToken cancellationToken = default)
	{
		var gate = For(devicePath);
		await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
		return new Releaser(gate);
	}

	/// <summary>
	/// Takes the gate if it becomes free within <paramref name="timeout"/>; returns <c>null</c>
	/// when the path stays busy. For low-priority housekeeping — the battery poll — that must
	/// yield to a configuration transfer in progress rather than queue behind it: a battery level
	/// a minute stale is fine, a key-table transfer spliced by a battery frame is not.
	/// </summary>
	public static IDisposable? TryAcquire(string devicePath, TimeSpan timeout)
	{
		var gate = For(devicePath);
		return gate.Wait(timeout) ? new Releaser(gate) : null;
	}

	/// <summary>
	/// The async form of <see cref="TryAcquire"/>: takes the gate if it becomes free within
	/// <paramref name="timeout"/>, otherwise returns <c>null</c> without ever blocking a thread.
	/// </summary>
	/// <remarks>
	/// This exists because <see cref="AcquireAsync"/> with neither a timeout nor a token cannot be
	/// abandoned: a holder that wedges (a device that stops answering mid-transfer) leaves every
	/// later waiter queued for the life of the process, and those waiters survive service shutdown
	/// because nothing can cancel them. A bounded wait turns that into a failed write the caller
	/// can report, which is the correct outcome — an LED write nobody can perform is not worth an
	/// un-cancellable queue slot.
	/// </remarks>
	public static async Task<IDisposable?> TryAcquireAsync(
		string devicePath, TimeSpan timeout, CancellationToken cancellationToken = default)
	{
		var gate = For(devicePath);
		return await gate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false)
			? new Releaser(gate)
			: null;
	}

	private static SemaphoreSlim For(string devicePath) =>
		Gates.GetOrAdd(Normalize(devicePath), _ => new SemaphoreSlim(1, 1));

	/// <summary>
	/// Reduces a device interface path to a stable key.
	/// </summary>
	/// <remarks>
	/// Callers reach the same interface through different APIs — HidSharp's <c>DevicePath</c> in the
	/// device services, SetupAPI's <c>SetupDiGetDeviceInterfaceDetail</c> in the DeviceHost monitors —
	/// and a gate whose keys disagree silently serialises nothing while looking correct. Case is
	/// already handled by the dictionary's comparer; this additionally drops the <c>\\?\</c> prefix
	/// and the trailing interface-class GUID, neither of which distinguishes one interface from
	/// another. The collection segment (<c>&amp;col04</c>) is inside the retained part, so distinct
	/// collections on one device still get distinct gates.
	/// </remarks>
	internal static string Normalize(string? devicePath)
	{
		if (string.IsNullOrWhiteSpace(devicePath)) return string.Empty;

		var path = devicePath.Trim();

		if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
			path = path[4..];
		else if (path.StartsWith(@"\\.\", StringComparison.Ordinal))
			path = path[4..];

		var guid = path.LastIndexOf("#{", StringComparison.Ordinal);
		if (guid > 0) path = path[..guid];

		return path;
	}

	private sealed class Releaser(SemaphoreSlim gate) : IDisposable
	{
		private SemaphoreSlim? _gate = gate;

		public void Dispose()
		{
			// Guard against a double dispose releasing a permit the caller no longer holds, which would
			// let two exchanges run at once — the exact failure this type prevents.
			var taken = Interlocked.Exchange(ref _gate, null);
			taken?.Release();
		}
	}
}
