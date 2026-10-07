using HidSharp;
using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

/// <summary>
/// Raster (VID 0x36B0, PID 0x3061) key-remap protocol — diagnostic pass on mi_01.
/// </summary>
/// <remarks>
/// <para>Config interface: <c>mi_01</c> (out=65, in=65, unnumbered report, usagePage
/// <c>0xFF60</c>). This is the interface the vendor's Next.js webapp filters for; earlier
/// tests produced real command-echo responses (unlike <c>mi_02&amp;col05</c> which only
/// returns periodic battery pushes).</para>
/// <para>Packet layout on the wire:
/// <c>[0x00 reportId, 0xAA vendor tag, cmd, addr_lo, addr_hi, len, 0, 0, 0, type, code1, code2, pad…]</c>.
/// The <c>0xAA</c> tag matches the vendor webapp's <c>REPORT_ID</c> constant and is required
/// on this interface (without it firmware ignores the packet).</para>
/// <para>Diagnostics added in this iteration:</para>
/// <list type="number">
///   <item><c>CMD_GET_FUNCINFO (0x14)</c> — reads active profile. If it's not 0, our writes
///     with <c>SetUserKey0 (0x1B)</c> land in a non-active slot.</item>
///   <item><c>CMD_GET_DEFAULT_0 (0x16)</c> — reads the factory-default key at the target
///     slot. Verifies the matrix index corresponds to the physical key we think it does.</item>
///   <item>Type byte changed from <c>0x10</c> (which firmware silently normalized to
///     <c>0x0F</c>) to <c>0x0F</c> directly, matching what firmware echoes.</item>
/// </list>
/// </remarks>
public class RasterKeyboardKeyMappingService(HidDevice device, ILogger? logger = null) : IKeyboardKeyMappingService
{
	private const int BufferLen = 65; // 64-byte vendor payload + 1-byte OS reportId prefix

	private const byte VendorTag = 0xAA;
	private const byte CmdStartComm = 0x10;
	private const byte CmdStopComm = 0x11;
	private const byte CmdGetFuncInfo = 0x14;
	private const byte CmdGetDefault0 = 0x16;
	private const byte CmdGetUserKey0 = 0x1A;
	private const byte CmdSetUserKey0 = 0x1B;

	// Type byte for a plain HID keyboard record. Confirmed via GetDefault read: the factory
	// mapping for any keyboard-type slot returns type=0x10 (e.g. default idx=44 = 10-00-30 for
	// ']'; default idx=0 = 10-00-29 for ESC). Type 0x0F was disabling the key (no output).
	private const byte TypeKeyboardKey = 0x10;

	private const byte MatrixSlotSize = 3;

	// If the device stops answering mid-probe, stop iterating slots after this many
	// consecutive null/short reads. At 500 ms per-slot ReadTimeout this caps the worst-case
	// hang on a stuck device at ~2.5 s instead of the ~64 s a full 128-slot probe would take.
	private const int MaxConsecutiveReadFailures = 5;

	private readonly HidDevice _device = device ?? throw new ArgumentNullException(nameof(device));
	private readonly ILogger? _logger = logger;

	public async Task<IReadOnlyDictionary<byte, byte>?> ProbeDefaultMatrixAsync(int maxSlot = 128, CancellationToken cancellationToken = default)
	{
		_logger?.LogInformation("[KEY] Raster probe default matrix, maxSlot={Max}", maxSlot);

		if (!_device.TryOpen(out var stream))
		{
			_logger?.LogWarning("[KEY] Raster HidSharp TryOpen failed on {Path}", _device.DevicePath);
			return null;
		}

		var map = new Dictionary<byte, byte>();

		try
		{
			using (stream)
			{
				stream.WriteTimeout = 500;
				stream.ReadTimeout = 500;

				// Session start. If the handshake itself doesn't come back, the device is
				// unresponsive on this interface — bail before iterating any slots.
				await stream.WriteAsync(BuildCommand(CmdStartComm));
				if ((await DrainSilentAsync(stream)).Buffer == null)
				{
					_logger?.LogWarning("[KEY] Raster start-comm ack timed out — aborting default-matrix probe");
					return null;
				}

				var consecutiveFailures = 0;
				for (int slot = 0; slot < maxSlot && slot <= byte.MaxValue; slot++)
				{
					if (cancellationToken.IsCancellationRequested) return null;
					await stream.WriteAsync(BuildAddressedRead(CmdGetDefault0, (byte)slot));
					var (response, bytesRead) = await DrainSilentAsync(stream);
					if (response == null || bytesRead < 12)
					{
						if (++consecutiveFailures >= MaxConsecutiveReadFailures)
						{
							_logger?.LogWarning(
								"[KEY] Raster device stopped responding at slot={Slot} ({N} consecutive failed reads) — aborting default-matrix probe",
								slot, consecutiveFailures);
							break;
						}
						continue;
					}
					consecutiveFailures = 0;

					// Response format after the 0xAA vendor tag echo (byte 1) and cmd echo (byte 2):
					// byte 8 = 0x55 status marker
					// byte 9 = type
					// byte 10 = code1 (modifier bitmask for keyboard records)
					// byte 11 = code2 (HID scancode for keyboard records)
					var status = response[8];
					var type = response[9];
					var code1 = response[10];
					var code2 = response[11];

					if (status != 0x55) continue;
					if (type != TypeKeyboardKey) continue;   // only keyboard-type slots
					if (code1 != 0) continue;                // exclude modifier-combined defaults
					if (code2 == 0) continue;                // empty slot

					// First slot wins if a scancode ever appears twice (shouldn't happen for defaults,
					// but be defensive against firmware quirks).
					map.TryAdd(code2, (byte)slot);
				}

				// Session end.
				await stream.WriteAsync(BuildCommand(CmdStopComm));
				_ = await DrainSilentAsync(stream);
			}

			_logger?.LogInformation("[KEY] Raster probe complete: {Count} slots mapped", map.Count);
			return map;
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[KEY] Raster probe failed");
			return null;
		}
	}

	public async Task<IReadOnlyDictionary<byte, byte>?> ProbeUserOverridesAsync(int maxSlot = 128, CancellationToken cancellationToken = default)
	{
		_logger?.LogInformation("[KEY] Raster probe user overrides, maxSlot={Max}", maxSlot);

		if (!_device.TryOpen(out var stream))
		{
			_logger?.LogWarning("[KEY] Raster HidSharp TryOpen failed on {Path}", _device.DevicePath);
			return null;
		}

		var map = new Dictionary<byte, byte>();

		try
		{
			using (stream)
			{
				stream.WriteTimeout = 500;
				stream.ReadTimeout = 500;

				await stream.WriteAsync(BuildCommand(CmdStartComm));
				if ((await DrainSilentAsync(stream)).Buffer == null)
				{
					_logger?.LogWarning("[KEY] Raster start-comm ack timed out — aborting user-overrides probe");
					return null;
				}

				var consecutiveFailures = 0;
				for (int slot = 0; slot < maxSlot && slot <= byte.MaxValue; slot++)
				{
					if (cancellationToken.IsCancellationRequested) return null;
					await stream.WriteAsync(BuildAddressedRead(CmdGetUserKey0, (byte)slot));
					var (response, bytesRead) = await DrainSilentAsync(stream);
					if (response == null || bytesRead < 12)
					{
						if (++consecutiveFailures >= MaxConsecutiveReadFailures)
						{
							_logger?.LogWarning(
								"[KEY] Raster device stopped responding at slot={Slot} ({N} consecutive failed reads) — aborting user-overrides probe",
								slot, consecutiveFailures);
							break;
						}
						continue;
					}
					consecutiveFailures = 0;

					var status = response[8];
					var type = response[9];
					var code1 = response[10];
					var code2 = response[11];

					if (status != 0x55) continue;
					// Skip slots with no user override (firmware reports type=0x0F for empty).
					if (type != TypeKeyboardKey) continue;
					if (code1 != 0) continue;
					if (code2 == 0) continue;

					map[(byte)slot] = code2;
				}

				await stream.WriteAsync(BuildCommand(CmdStopComm));
				_ = await DrainSilentAsync(stream);
			}

			_logger?.LogInformation("[KEY] Raster user-overrides probe complete: {Count} slots with active override",
				map.Count);
			return map;
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[KEY] Raster user-overrides probe failed");
			return null;
		}
	}


	/// <summary>
	/// Reads the <b>base</b> matrix as full records. On this firmware the base matrix is the factory
	/// table — user remaps live in the separate override layer that
	/// <see cref="ProbeUserOverridesAsync"/> reads — so this returns the factory state, not
	/// necessarily what a key currently emits. The Smart Infinite family is where a single read is
	/// the whole truth.
	/// </summary>
	public async Task<IReadOnlyDictionary<byte, KeyMatrixRecord>?> ReadKeyMatrixAsync(int maxSlot = 128, CancellationToken cancellationToken = default)
	{
		_logger?.LogInformation("[KEY] Raster read base key matrix, maxSlot={Max}", maxSlot);

		if (!_device.TryOpen(out var stream))
		{
			_logger?.LogWarning("[KEY] Raster HidSharp TryOpen failed on {Path}", _device.DevicePath);
			return null;
		}

		var map = new Dictionary<byte, KeyMatrixRecord>();

		try
		{
			using (stream)
			{
				stream.WriteTimeout = 500;
				stream.ReadTimeout = 500;

				await stream.WriteAsync(BuildCommand(CmdStartComm));
				if ((await DrainSilentAsync(stream)).Buffer == null)
				{
					_logger?.LogWarning("[KEY] Raster start-comm ack timed out — aborting key-matrix read");
					return null;
				}

				var consecutiveFailures = 0;
				for (int slot = 0; slot < maxSlot && slot <= byte.MaxValue; slot++)
				{
					if (cancellationToken.IsCancellationRequested) return null;
					await stream.WriteAsync(BuildAddressedRead(CmdGetDefault0, (byte)slot));
					var (response, bytesRead) = await DrainSilentAsync(stream);
					if (response == null || bytesRead < 12)
					{
						if (++consecutiveFailures >= MaxConsecutiveReadFailures) break;
						continue;
					}
					consecutiveFailures = 0;

					if (response[8] != 0x55) continue;

					var record = new KeyMatrixRecord(response[9], response[10], response[11]);
					if (record.IsZero) continue;

					map[(byte)slot] = record;
				}

				await stream.WriteAsync(BuildCommand(CmdStopComm));
				_ = await DrainSilentAsync(stream);
			}

			_logger?.LogInformation("[KEY] Raster key-matrix read complete: {Count} slots", map.Count);
			return map;
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[KEY] Raster key-matrix read failed");
			return null;
		}
	}

	private static async Task<(byte[]? Buffer, int BytesRead)> DrainSilentAsync(HidStream stream)
	{
		try
		{
			var response = new byte[BufferLen];
			var bytesRead = await stream.ReadAsync(response);
			return (response, bytesRead);
		}
		catch
		{
			return (null, 0);
		}
	}

	public async Task<bool> SetKeyMappingAsync(byte matrixIndex, byte type, byte code1, byte code2, CancellationToken cancellationToken = default)
	{
		// Keyboard records: firmware normalizes whatever we send to 0x10, so keep forcing it —
		// otherwise the read-back diagnostics report a spurious mismatch.
		//
		// Macro bindings are a DIFFERENT record kind (0x60 play, 0x61 while-held) and must pass
		// through untouched. Forcing 0x10 on them wrote a keyboard record whose scancode was the
		// macro slot index — slot 0 meaning scancode 0 — so the key produced nothing at all.
		var effectiveType = RasterMacroEncoder.IsMacroBindingType(type) ? type : TypeKeyboardKey;

		_logger?.LogInformation(
			"[KEY] Raster SetKeyMapping matrixIndex={Idx} type=0x{T:X2} (caller-type=0x{OT:X2}) code1=0x{C1:X2} code2=0x{C2:X2}",
			matrixIndex, effectiveType, type, code1, code2);

		if (!_device.TryOpen(out var stream))
		{
			_logger?.LogWarning("[KEY] Raster HidSharp TryOpen failed on {Path}", _device.DevicePath);
			return false;
		}

		try
		{
			using (stream)
			{
				stream.WriteTimeout = 500;
				stream.ReadTimeout = 500;

				// Session start.
				await WriteBufferAsync(stream, BuildCommand(CmdStartComm), "start");
				await DrainResponseAsync(stream, "start-ack");

				// Diagnostic 1 — active profile.
				await WriteBufferAsync(stream, BuildCommand(CmdGetFuncInfo), "get-funcinfo");
				await DrainResponseAsync(stream, "funcinfo");

				// Diagnostic 2 — factory-default record for target slot. Reveals whether the
				// matrix index actually corresponds to the physical key we think it does.
				await WriteBufferAsync(stream, BuildAddressedRead(CmdGetDefault0, matrixIndex), $"get-default idx={matrixIndex}");
				await DrainResponseAsync(stream, $"default idx={matrixIndex}");

				// Current user override (before write).
				await WriteBufferAsync(stream, BuildAddressedRead(CmdGetUserKey0, matrixIndex), $"get-before idx={matrixIndex}");
				await DrainResponseAsync(stream, $"get-before idx={matrixIndex}");

				// Write the new record with type=0x0F.
				var setRequest = BuildSetUserKey(matrixIndex, effectiveType, code1, code2);
				_logger?.LogInformation("[KEY] Raster set-write bytes: {Hex}",
					BitConverter.ToString(setRequest, 0, Math.Min(setRequest.Length, 16)));
				await stream.WriteAsync(setRequest);
				await DrainResponseAsync(stream, "set-ack");

				// Read it back.
				await WriteBufferAsync(stream, BuildAddressedRead(CmdGetUserKey0, matrixIndex), $"get-after idx={matrixIndex}");
				await DrainResponseAsync(stream, $"get-after idx={matrixIndex}");

				// Session end.
				await WriteBufferAsync(stream, BuildCommand(CmdStopComm), "stop");
				await DrainResponseAsync(stream, "stop-ack");
			}

			return true;
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[KEY] Raster HID write failed");
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

	private static byte[] BuildAddressedRead(byte command, byte matrixIndex)
	{
		ushort address = (ushort)(matrixIndex * MatrixSlotSize);
		var buf = new byte[BufferLen];
		buf[1] = VendorTag;
		buf[2] = command;
		buf[3] = (byte)(address & 0xFF);
		buf[4] = (byte)((address >> 8) & 0xFF);
		buf[5] = MatrixSlotSize;
		return buf;
	}

	private static byte[] BuildSetUserKey(byte matrixIndex, byte type, byte code1, byte code2)
	{
		ushort address = (ushort)(matrixIndex * MatrixSlotSize);
		var buf = new byte[BufferLen];
		buf[1] = VendorTag;
		buf[2] = CmdSetUserKey0;
		buf[3] = (byte)(address & 0xFF);
		buf[4] = (byte)((address >> 8) & 0xFF);
		buf[5] = MatrixSlotSize;
		// bytes 6..8 padding
		buf[9] = type;
		buf[10] = code1;
		buf[11] = code2;
		return buf;
	}

	private async Task WriteBufferAsync(HidStream stream, byte[] buffer, string tag)
	{
		try
		{
			await stream.WriteAsync(buffer);
		}
		catch (Exception ex)
		{
			_logger?.LogWarning("[KEY] Raster {Tag} write failed: {Msg}", tag, ex.Message);
		}
	}

	private async Task DrainResponseAsync(HidStream stream, string tag)
	{
		try
		{
			var response = new byte[BufferLen];
			var read = await stream.ReadAsync(response);
			var previewLen = Math.Min(read, 16);
			_logger?.LogInformation("[KEY] Raster {Tag} response ({N} bytes): {Hex}",
				tag, read, BitConverter.ToString(response, 0, previewLen));
		}
		catch (Exception ex)
		{
			_logger?.LogWarning("[KEY] Raster {Tag} response read failed: {Msg}", tag, ex.Message);
		}
	}
}
