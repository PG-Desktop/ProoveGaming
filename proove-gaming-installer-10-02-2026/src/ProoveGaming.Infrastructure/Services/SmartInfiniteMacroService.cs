using HidSharp;
using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

/// <summary>
/// Macro-library upload over the Smart Infinite vendor protocol — Cyrex, Aress and Rampage.
/// </summary>
/// <remarks>
/// <para>
/// The library is packed by <see cref="SmartInfiniteMacroEncoder"/> into the buffer the vendor's
/// own driver builds (<c>formatHongRect</c>): sized to the macro space Device Info reports, a
/// 20-slot offset table, and modifier events in the hot-key form. It is written with
/// <see cref="SmartInfiniteProtocol.CmdWriteMacroInfo"/> (<c>0x1C</c>) — the command the vendor
/// app actually sends on this firmware line — and read back with
/// <see cref="SmartInfiniteProtocol.CmdReadMacroInfo"/> (<c>0x1B</c>). Key slots then point into
/// the library with <see cref="SmartInfiniteProtocol.TypeMacroStopMode"/> /
/// <see cref="SmartInfiniteProtocol.TypeMacroPlayCount"/> records.
/// </para>
/// <para>
/// An earlier revision wrote <c>0x2D</c> transfers of a 2048-byte reserve and bound keys with
/// <c>0x60</c>, per the V3.1 workbook — a different firmware line's numbering. Every "confirmed"
/// observation on that path was ack-only; the all-zero read-back in
/// <c>docs/plans/add-cyrex-aress-parity/macro-investigation-result.md</c> is what a wrong-command
/// write looks like. See <c>docs/knowledge-base/smart-infinite-protocol/macro-format-gamingkeyboard2.md</c>.
/// </para>
/// <para>
/// <b>The read-back verify is authoritative and runs in its own session.</b> With the read command
/// finally matching the write, "acked but not stored" is detectable — and a library the board does
/// not hold must fail the upload, or key slots get bound into nothing. The verify opens a
/// <b>fresh handle</b> on purpose: this family stages writes in volatile RAM that reads back
/// correctly within the same session without being committed (the measured <c>0x18</c> behaviour —
/// see <i>Staging buffer</i> in <c>docs/CONTEXT.md</c>), so a same-session read could only confirm
/// the staging copy, not storage.
/// </para>
/// <para>
/// A deterministic refusal — the board reporting <b>0</b> bytes of macro space, or the read-back
/// differing from what was sent — is reported once, not retried: <see cref="SmartInfiniteTransport.WithRetry{T}"/>
/// exists for handles Windows has not finished releasing, and re-running a 20-packet write against
/// a firmware that answered and disagreed only holds the device gate hostage.
/// </para>
/// </remarks>
public class SmartInfiniteMacroService : IKeyboardMacroService
{
	// The firmware needs a moment after a write before the store reads back — same settle the
	// key-mapping and actuation services use.
	private const int PostWriteSettleMs = 80;

	// Divergence log window: enough context around the first differing byte to see the shape of
	// the corruption without dumping the whole region (~2 KB of hex per side) at Error level.
	private const int DivergenceLogWindow = 32;

	private readonly string _devicePath;
	private readonly ILogger? _logger;
	private readonly string _tag;
	private readonly SmartInfiniteTransport _transport;

	public SmartInfiniteMacroService(HidDevice device, string tag, ILogger? logger = null,
		int maxPayload = SmartInfiniteProtocol.MaxPayloadLength)
	{
		ArgumentNullException.ThrowIfNull(device);
		_devicePath = device.DevicePath;
		_tag = string.IsNullOrWhiteSpace(tag) ? "SmartInfinite" : tag;
		_logger = logger;
		_transport = new SmartInfiniteTransport(device, _tag, logger, maxPayload);
	}

	/// <summary>Test seam: run the service against a scripted transport instead of real HID.</summary>
	internal SmartInfiniteMacroService(SmartInfiniteTransport transport, string tag, ILogger? logger = null)
	{
		_transport = transport ?? throw new ArgumentNullException(nameof(transport));
		_devicePath = transport.DevicePath;
		_tag = string.IsNullOrWhiteSpace(tag) ? "SmartInfinite" : tag;
		_logger = logger;
	}

	/// <summary>How one upload attempt ended, distinguishing "answered and refused" from "did not
	/// answer": a non-null outcome is final, a null one is transient and worth retrying.</summary>
	private enum UploadOutcome { Verified, Rejected }

	public async Task<bool> UploadMacrosAsync(IReadOnlyList<MacroLibraryEntry> entries, CancellationToken cancellationToken = default)
	{
		if (entries == null) throw new ArgumentNullException(nameof(entries));

		_logger?.LogInformation("[{Tag}] UploadMacros count={Count} on {Path}",
			_tag, entries.Count, _devicePath);

		// Shared with the key-mapping and RGB services: interleaved exchanges on one interface
		// corrupt each other's address-matched reply loops, and an RGB 0x15 write zeroes the key
		// staging buffer — so every service on this family must serialise through the same gate.
		using var gate = await DeviceGate.AcquireAsync(_devicePath, cancellationToken).ConfigureAwait(false);
		try
		{
			var outcome = await Task.Run(() =>
				_transport.WithRetry(() => UploadOnce(entries, cancellationToken),
					nameof(UploadMacrosAsync), cancellationToken), CancellationToken.None).ConfigureAwait(false);

			// WithRetry returns null both when every attempt failed transiently and when the token
			// fired mid-run; cancellation is the caller's business, not an upload failure to be
			// reported as false, so it is re-raised as such here.
			if (outcome is null)
				cancellationToken.ThrowIfCancellationRequested();

			return outcome == UploadOutcome.Verified;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[{Tag}] macro upload failed on {Path}", _tag, _devicePath);
			return false;
		}
	}

	// One write session, one settle, one verify session. Null = transient (a handle or transfer
	// never completed — WithRetry runs it again); a non-null outcome is the device's answer.
	private UploadOutcome? UploadOnce(IReadOnlyList<MacroLibraryEntry> entries, CancellationToken cancellationToken)
	{
		var written = _transport.OnHandle<WriteSessionResult?>(io => WriteSession(io, entries, cancellationToken));
		if (written is null) return null;
		if (written.Value.Refused) return UploadOutcome.Rejected;

		if (cancellationToken.IsCancellationRequested) return null;
		Thread.Sleep(PostWriteSettleMs);

		// The verify runs on a FRESH handle, never the writing one. This family stages writes in
		// volatile RAM that reads back correctly inside the same session without being committed
		// (the measured 0x18 staging-buffer behaviour) — a same-session verify would pass on a
		// board that stored nothing, bind keys into an empty library, and log "verified" while
		// doing it. Do not collapse the two sessions back into one.
		var packed = written.Value.Packed;
		return _transport.OnHandle<UploadOutcome?>(io => VerifySession(io, packed, cancellationToken));
	}

	private readonly record struct WriteSessionResult(bool Refused, SmartInfiniteMacroEncoder.PackResult Packed);

	private WriteSessionResult? WriteSession(ISmartInfiniteIo io, IReadOnlyList<MacroLibraryEntry> entries, CancellationToken cancellationToken)
	{
		var info = _transport.ReadDeviceInfo(io);
		if (info == null)
		{
			_logger?.LogWarning("[{Tag}] refusing to upload: Device Info could not be read", _tag);
			return null;
		}
		_logger?.LogInformation("[{Tag}] device info: {Info}", _tag, info);

		if (info.MacroBytes <= 0)
		{
			// The board answered and has no macro region — a fact, not a glitch; retrying it only
			// re-reads the same identity block under the gate.
			_logger?.LogError(
				"[{Tag}] refusing to upload: the firmware reports {Bytes} bytes of macro space. " +
				"Writes would be acknowledged and discarded. Device info: {Info}",
				_tag, info.MacroBytes, info);
			return new WriteSessionResult(Refused: true, default);
		}

		var packed = SmartInfiniteMacroEncoder.EncodePacked(entries, info.MacroBytes);
		_logger?.LogInformation("[{Tag}] packed library dataLen={Len} of the {Space}-byte macro region",
			_tag, packed.DataLen, info.MacroBytes);

		// The encoder has no logger — surface anything it left out, so a macro that plays
		// back short is explained rather than mysterious.
		if (packed.HasDiscards)
			_logger?.LogWarning(
				"[{Tag}] library did not fit entirely: {Mouse} mouse event(s) dropped, {Trunc} macro(s) truncated at {Cap} events, {Skipped} macro(s) skipped",
				_tag, packed.DroppedMouseEvents, packed.TruncatedMacros,
				SmartInfiniteMacroEncoder.MaxEventsPerMacro, packed.SkippedMacros);

		// The whole formatted region, exactly as the vendor app's setHongRect sends it — the
		// header's length field IS the region size, and the tail past the last block is the
		// natural "rest is empty" state.
		if (!_transport.WriteTable(io, SmartInfiniteProtocol.CmdWriteMacroInfo,
			packed.Buffer, packed.Buffer.Length, cancellationToken))
			return null;

		return new WriteSessionResult(Refused: false, packed);
	}

	/// <summary>
	/// Reads the occupied part of the region back with <see cref="SmartInfiniteProtocol.CmdReadMacroInfo"/>
	/// and compares it to what was sent. <b>Authoritative:</b> a mismatch fails the upload as
	/// <see cref="UploadOutcome.Rejected"/> — final, not retried — so key slots are never bound
	/// into a library the board does not hold. A read that never completed is null (transient).
	/// </summary>
	private UploadOutcome? VerifySession(ISmartInfiniteIo io, SmartInfiniteMacroEncoder.PackResult packed, CancellationToken cancellationToken)
	{
		var dataLen = packed.DataLen;
		var readBack = _transport.ReadTable(io, SmartInfiniteProtocol.CmdReadMacroInfo,
			dataLen, dataLen, cancellationToken);
		if (readBack == null)
		{
			_logger?.LogWarning(
				"[{Tag}] could not read the macro library back (cmd 0x{Cmd:X2}); retrying the upload",
				_tag, SmartInfiniteProtocol.CmdReadMacroInfo);
			return null;
		}

		var sent = packed.Buffer.AsSpan(0, dataLen);
		var read = readBack.AsSpan(0, dataLen);
		var verdict = MacroLibraryVerification.Classify(sent, read);
		if (verdict.Outcome == MacroVerifyOutcome.Match)
		{
			_logger?.LogInformation(
				"[{Tag}] macro library verified on a fresh handle: {Bytes} bytes read back identical",
				_tag, dataLen);
			return UploadOutcome.Verified;
		}

		// A bounded window around the first divergence tells the investigation what shape the
		// corruption has; the full ~2x2 KB hex dump only appears at Debug, where someone asked.
		int windowStart = Math.Max(0, verdict.FirstDivergence - DivergenceLogWindow / 4);
		int windowLen = Math.Min(DivergenceLogWindow, dataLen - windowStart);
		_logger?.LogError(
			"[{Tag}] macro library read back DIFFERENT on a fresh handle, first divergence at byte {Index}. " +
			"{Verdict} window@{Start}: sent={SentWindow} read={ReadWindow}",
			_tag, verdict.FirstDivergence,
			verdict.Outcome == MacroVerifyOutcome.AllZero
				? "The region is entirely ZERO — the write was acknowledged and discarded; record this log for the factory."
				: "The region holds something other than what was sent.",
			windowStart,
			Convert.ToHexString(sent.Slice(windowStart, windowLen)),
			Convert.ToHexString(read.Slice(windowStart, windowLen)));

		if (_logger?.IsEnabled(LogLevel.Debug) == true)
			_logger.LogDebug("[{Tag}] full macro region dump: sent={Sent} read={Read}",
				_tag, Convert.ToHexString(sent), Convert.ToHexString(read));

		return UploadOutcome.Rejected;
	}
}
