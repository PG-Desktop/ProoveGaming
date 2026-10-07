using HidSharp;
using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

/// <summary>
/// Macro-library upload for the Raster vendor family (VID 0x36B0) on <c>mi_01</c> — Raster and
/// T-Racer, which share this wire format. Ships the full packed 1096-byte buffer chunked over
/// <see cref="RasterMacroEncoder.ChunkSize"/>-byte writes framed with the vendor <c>0xAA</c> tag
/// and <c>CMD_SET_MACRODATA (0x2D)</c>. Wraps the sequence in <c>CMD_START_COMM (0x10)</c> /
/// <c>CMD_STOP_COMM (0x11)</c>, same handshake as <see cref="RasterKeyboardKeyMappingService"/>.
/// </summary>
/// <remarks>
/// One implementation for both models rather than a copy per keyboard, the same way
/// <see cref="SmartInfiniteMacroService"/> serves Cyrex and Aress. <paramref name="modelName"/>
/// only tags the logs. The copy-per-keyboard pattern used for the RGB and key-mapping services
/// is what let T-Racer's key mapping drift from Raster's and silently discard macro bindings;
/// the ack validation below is exactly the kind of hard-won logic a second copy would strand.
/// </remarks>
public class RasterKeyboardMacroService(HidDevice device, string modelName = "Raster", ILogger? logger = null)
	: IKeyboardMacroService
{
	private const int BufferLen = 65; // 64-byte vendor payload + 1-byte OS reportId prefix

	private const byte VendorTag = 0xAA;
	private const byte CmdStartComm = 0x10;
	private const byte CmdStopComm = 0x11;
	private const byte CmdSetMacroData = 0x2D;

	private const byte ResponseSuccess = 0x55;

	private readonly HidDevice _device = device ?? throw new ArgumentNullException(nameof(device));
	private readonly string _modelName = modelName;
	private readonly ILogger? _logger = logger;

	public async Task<bool> UploadMacrosAsync(IReadOnlyList<MacroLibraryEntry> entries, CancellationToken cancellationToken = default)
	{
		if (entries == null) throw new ArgumentNullException(nameof(entries));

		_logger?.LogInformation("[MACRO] {Model} UploadMacros count={Count}", _modelName, entries.Count);

		if (!_device.TryOpen(out var stream))
		{
			_logger?.LogWarning("[MACRO] {Model} HidSharp TryOpen failed on {Path}", _modelName, _device.DevicePath);
			return false;
		}

		try
		{
			using (stream)
			{
				stream.WriteTimeout = 500;
				stream.ReadTimeout = 500;

				var ok = await SendAndAwaitAckAsync(
					stream, CmdStartComm, BuildCommand(CmdStartComm), "start");

				if (ok)
				{
					var packed = RasterMacroEncoder.EncodePacked(entries);
					_logger?.LogInformation("[MACRO] {Model} packed library dataLen={Len} bytes", _modelName, packed.DataLen);

					// The encoder cannot log — surface anything it had to leave out, so a macro
					// that plays back short is explained rather than mysterious.
					if (packed.HasDiscards)
						_logger?.LogWarning(
							"[MACRO] {Model} library did not fit entirely: {Mouse} mouse event(s) dropped, {Trunc} macro(s) truncated at {Cap} events, {Skipped} macro(s) skipped for lack of space",
							_modelName, packed.DroppedMouseEvents, packed.TruncatedMacros,
							RasterMacroEncoder.MaxEventsPerMacro, packed.SkippedMacros);

					foreach (var (addrOffset, chunkLen, checksum, chunkBytes) in
						RasterMacroEncoder.SliceChunks(packed.Buffer, packed.DataLen))
					{
						// The client's deadline has expired; stop_comm below still runs so the
						// firmware is not left mid-session.
						if (cancellationToken.IsCancellationRequested) { ok = false; break; }

						var write = BuildSetMacroDataChunk(addrOffset, chunkLen, checksum, chunkBytes);
						if (!await SendAndAwaitAckAsync(stream, CmdSetMacroData, write,
							$"set-macro addr=0x{addrOffset:X4} len={chunkLen} cs=0x{checksum:X2}"))
						{
							ok = false;
							break;
						}
					}
				}

				// stop_comm is attempted even after a failure; without it the next open-session
				// on mi_01 can hang on the 500 ms read timeout because firmware is still in an
				// addressed session. Its own failure still fails the upload.
				if (!await SendAndAwaitAckAsync(stream, CmdStopComm, BuildCommand(CmdStopComm), "stop"))
					ok = false;

				if (!ok)
				{
					_logger?.LogWarning("[MACRO] {Model} upload failed - library was not stored", _modelName);
					return false;
				}
			}

			return true;
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[MACRO] {Model} HID upload failed", _modelName);
			return false;
		}
	}

	private static byte[] BuildCommand(byte command)
	{
		var buf = new byte[BufferLen];
		// buf[0] = 0x00 OS reportId prefix (unnumbered report)
		buf[1] = VendorTag;
		buf[2] = command;
		return buf;
	}

	private static byte[] BuildSetMacroDataChunk(ushort addrOffset, byte chunkLen, byte checksum, byte[] chunkBytes)
	{
		var buf = new byte[BufferLen];
		// buf[0] = 0x00 OS reportId prefix
		buf[1] = VendorTag;
		buf[2] = CmdSetMacroData;
		buf[3] = (byte)(addrOffset & 0xFF);
		buf[4] = (byte)((addrOffset >> 8) & 0xFF);
		buf[5] = chunkLen;
		buf[6] = checksum;
		// bytes 7, 8 padding

		// Data starts at buf[9], filling up to the 65-byte buffer.
		var writeLen = Math.Min(chunkBytes.Length, BufferLen - 9);
		Array.Copy(chunkBytes, 0, buf, 9, writeLen);
		return buf;
	}

	// Writes one framed command and validates its ack, mirroring
	// RasterKeyboardRgbService.SendAndAwaitAckAsync. The firmware acks every command, so the
	// read must stay paired with the write or the next read returns the previous command's
	// response. Returns false on a write failure, a read failure, a short read, or an ack whose
	// vendor tag / command echo / status byte does not match — a rejected chunk must never be
	// reported to the caller as stored.
	//
	// Ack shape confirmed against hardware logs, e.g. start:
	//   00-AA-10-00-00-00-00-00-55-...   [1]=vendor tag, [2]=command echo, [8]=0x55 success
	private async Task<bool> SendAndAwaitAckAsync(HidStream stream, byte expectedCmd, byte[] buffer, string tag)
	{
		try
		{
			await stream.WriteAsync(buffer);
		}
		catch (Exception ex)
		{
			_logger?.LogWarning("[MACRO] {Model} {Tag} write failed: {Msg}", _modelName, tag, ex.Message);
			return false;
		}

		var response = new byte[BufferLen];
		int read;
		try
		{
			read = await stream.ReadAsync(response);
		}
		catch (Exception ex)
		{
			_logger?.LogWarning("[MACRO] {Model} {Tag} response read failed: {Msg}", _modelName, tag, ex.Message);
			return false;
		}

		if (read < 9)
		{
			_logger?.LogWarning("[MACRO] {Model} {Tag} short ack: got {N} bytes", _modelName, tag, read);
			return false;
		}

		if (response[1] != VendorTag || response[2] != expectedCmd || response[8] != ResponseSuccess)
		{
			_logger?.LogWarning(
				"[MACRO] {Model} {Tag} ack mismatch: magic=0x{Magic:X2} echo=0x{Echo:X2} status=0x{Status:X2}",
				_modelName, tag, response[1], response[2], response[8]);
			return false;
		}

		_logger?.LogDebug("[MACRO] {Model} {Tag} ack ({N} bytes): {Hex}",
			_modelName, tag, read, BitConverter.ToString(response, 0, Math.Min(read, 16)));
		return true;
	}
}
