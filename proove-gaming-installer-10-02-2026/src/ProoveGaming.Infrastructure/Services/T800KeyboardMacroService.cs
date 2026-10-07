using HidSharp;
using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

/// <summary>
/// Macro-library upload over the Rongyuan RY5088 vendor protocol — T800.
/// </summary>
/// <remarks>
/// <para>
/// Unlike Raster and Smart Infinite, which pack the whole library into one offset-table region,
/// this firmware stores each macro as its own 256-byte per-slot blob
/// (<see cref="Ry5088MacroEncoder"/>), written with <see cref="Ry5088Protocol.CmdSetMacro"/>
/// (<c>0x0B</c>) in 56-byte pages and read back with <see cref="Ry5088Protocol.CmdGetMacro"/>
/// (<c>0x8B</c>) in 64-byte pages. Key slots then point into the library with
/// <see cref="Ry5088Protocol.TypeMacro"/> records — note code1 = play type, code2 = slot, the
/// mirror image of the other two families.
/// </para>
/// <para>
/// An upload rewrites all <see cref="Ry5088MacroEncoder.LibrarySlots"/> App-managed slots:
/// provided entries get their blob, the rest get the 4-byte empty blob so a deleted macro
/// cannot linger on the board. Used slots are verified by read-back — a library the board does
/// not hold must fail the upload, or key slots get bound into nothing (the same rule
/// <see cref="SmartInfiniteMacroService"/> enforces). Empty-slot clears are written blind: a
/// stale blob nothing points to is harmless, and verifying 16 × 4 reads on every library
/// resync is not.
/// </para>
/// <para>
/// The firmware does not ack feature writes and a rejected command leaves
/// <c>GetFeature</c> returning the driver's cached copy of the request, so every reply goes
/// through the same echo-detection the key-mapping service uses. A read-back that
/// <b>differs</b> from what was sent is final (<c>Rejected</c>, no retry — the device answered
/// and disagreed); a transfer that never completed is transient and retried.
/// </para>
/// </remarks>
public class T800KeyboardMacroService : IKeyboardMacroService
{
	// Windows can hold a closing HID handle for tens of milliseconds, so the first exchange on a
	// fresh handle can fail with ERROR_INVALID_FUNCTION. Same rationale as the key-mapping service.
	private const int MaxAttempts = 3;
	private const int RetryDelayMs = 100;

	// Same pacing the key-mapping service measured on this firmware: a beat between packets, a
	// longer settle between the last write and the verify read.
	private const int InterPacketDelayMs = 20;
	private const int PostWriteSettleMs = 300;

	private readonly HidDevice _device;
	private readonly ILogger? _logger;
	private readonly string _tag;

	public T800KeyboardMacroService(HidDevice device, string modelName, ILogger? logger = null)
	{
		_device = device ?? throw new ArgumentNullException(nameof(device));
		_logger = logger;
		_tag = $"MACRO {modelName}";
	}

	/// <summary>How one upload attempt ended, distinguishing "answered and refused" from "did not
	/// answer": a non-null outcome is final, a null one is transient and worth retrying.</summary>
	private enum UploadOutcome { Verified, Rejected }

	public async Task<bool> UploadMacrosAsync(IReadOnlyList<MacroLibraryEntry> entries, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(entries);

		_logger?.LogInformation("[{Tag}] UploadMacros count={Count} on {Path}",
			_tag, entries.Count, _device.DevicePath);

		// Shared with the key-mapping, actuation and RGB services: the RY5088 interface has no
		// reply addressing, so an ungated read can be handed another service's reply mid-way.
		using var gate = await DeviceGate.AcquireAsync(_device.DevicePath, cancellationToken).ConfigureAwait(false);
		try
		{
			return await Task.Run(() => UploadWithRetry(entries, cancellationToken),
				CancellationToken.None).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[{Tag}] macro upload failed on {Path}", _tag, _device.DevicePath);
			return false;
		}
	}

	private bool UploadWithRetry(IReadOnlyList<MacroLibraryEntry> entries, CancellationToken cancellationToken)
	{
		var blobs = EncodeLibrary(entries);

		for (int attempt = 1; attempt <= MaxAttempts; attempt++)
		{
			// Between attempts, not mid-exchange: retrying against a wedged device is exactly what
			// the App's deadline is trying to stop.
			if (cancellationToken.IsCancellationRequested) return false;

			if (attempt > 1)
			{
				_logger?.LogInformation("[{Tag}] upload retry {Attempt}/{Max}", _tag, attempt, MaxAttempts);
				Thread.Sleep(RetryDelayMs);
			}

			try
			{
				var outcome = UploadOnce(blobs, Math.Min(entries.Count, blobs.Length), cancellationToken);
				if (outcome == UploadOutcome.Verified) return true;
				// The device answered and disagreed — final, not retried: re-running a ~100-packet
				// write against a firmware that answered wrong only holds the device gate hostage.
				if (outcome == UploadOutcome.Rejected) return false;
			}
			catch (IOException ex)
			{
				_logger?.LogWarning("[{Tag}] upload I/O error on {Path}: {Msg}", _tag, _device.DevicePath, ex.Message);
			}
			catch (TimeoutException)
			{
				_logger?.LogWarning("[{Tag}] upload timed out on {Path}", _tag, _device.DevicePath);
			}
		}

		return false;
	}

	// One write session, one settle, one verify session. Null = transient (a handle or transfer
	// never completed — the retry loop runs it again); a non-null outcome is the device's answer.
	private UploadOutcome? UploadOnce(byte[][] blobs, int usedSlots, CancellationToken cancellationToken)
	{
		if (!WriteLibrary(blobs, cancellationToken)) return null;

		if (cancellationToken.IsCancellationRequested) return null;
		Thread.Sleep(PostWriteSettleMs);

		return VerifyLibrary(blobs, usedSlots, cancellationToken);
	}

	/// <summary>
	/// One blob per App-managed slot: provided entries encoded, the rest the empty blob. The
	/// encoder has no logger — surface anything it left out, so a macro that plays back short is
	/// explained rather than mysterious.
	/// </summary>
	private byte[][] EncodeLibrary(IReadOnlyList<MacroLibraryEntry> entries)
	{
		var blobs = new byte[Ry5088MacroEncoder.LibrarySlots][];
		var used = Math.Min(entries.Count, blobs.Length);

		var droppedEvents = 0;
		var truncatedMacros = 0;
		for (int slot = 0; slot < blobs.Length; slot++)
		{
			if (slot >= used)
			{
				blobs[slot] = Ry5088MacroEncoder.EmptyBlob();
				continue;
			}

			var packed = Ry5088MacroEncoder.Encode(entries[slot]);
			blobs[slot] = packed.Blob;
			droppedEvents += packed.DroppedEvents;
			if (packed.Truncated) truncatedMacros++;
		}

		if (entries.Count > used)
			_logger?.LogWarning("[{Tag}] {Skipped} macro(s) past the {Slots}-slot library skipped",
				_tag, entries.Count - used, blobs.Length);

		if (droppedEvents > 0 || truncatedMacros > 0)
			_logger?.LogWarning(
				"[{Tag}] library did not fit entirely: {Dropped} unsupported event(s) dropped, {Trunc} macro(s) truncated at {Cap} events",
				_tag, droppedEvents, truncatedMacros, Ry5088MacroEncoder.MaxEventsPerMacro);

		return blobs;
	}

	private bool WriteLibrary(byte[][] blobs, CancellationToken cancellationToken)
	{
		if (!_device.TryOpen(out var stream))
		{
			_logger?.LogWarning("[{Tag}] TryOpen failed on {Path}", _tag, _device.DevicePath);
			return false;
		}

		using (stream)
		{
			for (byte slot = 0; slot < blobs.Length; slot++)
			{
				var blob = blobs[slot];
				var pages = (blob.Length + Ry5088Protocol.MacroWritePageBytes - 1) / Ry5088Protocol.MacroWritePageBytes;

				for (byte page = 0; page < pages; page++)
				{
					// Between packets, per the interface's cancellation contract: a single HID
					// exchange is at most one stream-timeout long.
					if (cancellationToken.IsCancellationRequested)
					{
						_logger?.LogInformation("[{Tag}] macro write cancelled at slot {Slot} page {Page}",
							_tag, slot, page);
						return false;
					}

					var offset = page * Ry5088Protocol.MacroWritePageBytes;
					var length = Math.Min(Ry5088Protocol.MacroWritePageBytes, blob.Length - offset);
					stream.SetFeature(Ry5088Protocol.BuildSetMacroPage(
						slot, page, blob.AsSpan(offset, length), end: page == pages - 1));
					Thread.Sleep(InterPacketDelayMs);
				}
			}
		}

		return true;
	}

	/// <summary>
	/// Reads every used slot back and compares it to what was sent. <b>Authoritative:</b> a
	/// mismatch fails the upload as <see cref="UploadOutcome.Rejected"/> — final, not retried —
	/// so key slots are never bound into a library the board does not hold. A read that never
	/// completed is null (transient). Runs on a fresh handle, opened after the writing one closed.
	/// </summary>
	private UploadOutcome? VerifyLibrary(byte[][] blobs, int usedSlots, CancellationToken cancellationToken)
	{
		if (usedSlots == 0)
		{
			// Nothing to verify: the upload was pure clears, which are written blind by design.
			_logger?.LogInformation("[{Tag}] library cleared ({Slots} empty slots written)", _tag, blobs.Length);
			return UploadOutcome.Verified;
		}

		if (!_device.TryOpen(out var stream))
		{
			_logger?.LogWarning("[{Tag}] TryOpen for verify failed on {Path}", _tag, _device.DevicePath);
			return null;
		}

		using (stream)
		{
			var reply = new byte[Ry5088Protocol.ReportLength];

			for (byte slot = 0; slot < usedSlots; slot++)
			{
				var blob = blobs[slot];
				var pages = (blob.Length + Ry5088Protocol.MacroReadPageBytes - 1) / Ry5088Protocol.MacroReadPageBytes;

				for (byte page = 0; page < pages; page++)
				{
					if (cancellationToken.IsCancellationRequested)
					{
						_logger?.LogInformation("[{Tag}] macro verify cancelled at slot {Slot} page {Page}",
							_tag, slot, page);
						return null;
					}

					var request = Ry5088Protocol.BuildGetMacroPage(slot, page);
					stream.SetFeature(request);
					Thread.Sleep(InterPacketDelayMs);

					Array.Clear(reply);
					stream.GetFeature(reply);

					// A rejected command leaves GetFeature returning the driver's cached copy of
					// the request — indistinguishable from a reply except by comparing the two.
					if (IsEcho(request, reply))
					{
						_logger?.LogWarning("[{Tag}] slot {Slot} page {Page} came back as our own request — command rejected",
							_tag, slot, page);
						return null;
					}

					var offset = page * Ry5088Protocol.MacroReadPageBytes;
					var length = Math.Min(Ry5088Protocol.MacroReadPageBytes, blob.Length - offset);
					for (int i = 0; i < length; i++)
					{
						if (reply[1 + i] == blob[offset + i]) continue;

						// The compared window is at most one 64-byte page per side — small enough
						// to log whole, and it shows the shape of the corruption.
						_logger?.LogError(
							"[{Tag}] slot {Slot} read back DIFFERENT, first divergence at byte {Index}: sent={Sent} read={Read}",
							_tag, slot, offset + i,
							Convert.ToHexString(blob.AsSpan(offset, length)),
							Convert.ToHexString(reply.AsSpan(1, length)));
						return UploadOutcome.Rejected;
					}
				}
			}
		}

		_logger?.LogInformation("[{Tag}] macro library verified: {Used} slot(s) read back identical", _tag, usedSlots);
		return UploadOutcome.Verified;
	}

	private static bool IsEcho(byte[] request, byte[] reply)
	{
		for (int i = 1; i <= 8; i++)
			if (request[i] != reply[i]) return false;
		return true;
	}
}
