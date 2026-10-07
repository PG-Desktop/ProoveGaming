using HidSharp;
using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

// Raster (VID 14000, PID 12385) — SmartInfinite-adjacent protocol on the raw HID interface
// at mi_01 (report ID 0x00, 65-byte reports). Reverse-engineered from the vendor's own dist
// bundle (chunks/app/page-*.js `setFuncInfo` / `getFuncInfo`).
//
// Wire packet layout (65 bytes total, wired USB):
//   byte[0]     = 0x00     (HID report ID)
//   byte[1]     = 0xAA     (Raster magic)
//   byte[2]     = command  (0x10 START_COMM, 0x14 GET_FUNCINFO, 0x15 SET_FUNCINFO)
//   byte[3..4]  = address / offset into the 64-byte feature area (little-endian)
//   byte[5]     = length (of payload for SET, or requested response size for GET)
//   byte[6..8]  = reserved (0, 0, 0 on write; on GET response byte[8] = 0x55 on success)
//   byte[9..]   = payload data
//
// Raster's firmware requires that a SET_FUNCINFO chunk write preserves the surrounding 64
// bytes it isn't touching. Sending a fresh zeroed block flips matrixScreenLightSwitch=0,
// wipes the sleep timers, and clobbers user preferences (win-lock, WASD, six-key mode,
// snapTap, LCD) — the observable result is a dark keyboard.
//
// The vendor tool always reads the current state via GET_FUNCINFO first, mutates only the
// fields the UI changed, and writes it back. We do the same here.
public class RasterKeyboardRgbService(HidDevice device, ILogger? logger = null) : IDeviceRgbService
{
	private const int  ReportLen = 65;
	private const byte HidReportId  = 0x00;
	private const byte MagicByte    = 0xAA;

	private const byte CmdStartComm    = 0x10;
	private const byte CmdGetFuncInfo  = 0x14;
	private const byte CmdSetFuncInfo  = 0x15;

	private const byte ResponseSuccess = 0x55;

	// Wired USB chunk size. The vendor picks from [56, 24][deviceMode] — the 24-byte value
	// is for the 2.4G dongle, which we don't try to drive here.
	private const int ChunkSize = 56;

	// Mode indices from the vendor's per-keyboard JSON configs (chunks/*.js). The `value`
	// field is 1-indexed — `lang:"224","value":4` = Steady. Same numbering across every
	// SmartInfinite-family keyboard in the vendor bundle (K98, QK75, KA65, DM100, T68-D).
	private const byte ModeStreaming = 1;   // 221 流光溢彩
	private const byte ModeColorful  = 2;   // 222 五彩缤纷
	private const byte ModeBreathing = 3;   // 223 呼吸
	private const byte ModeSteady    = 4;   // 224 常亮

	private readonly HidDevice _device = device ?? throw new ArgumentNullException(nameof(device));
	private readonly ILogger? _logger = logger;

	public async Task<bool> SetLedColorAsync(byte r, byte g, byte b, byte effect, byte brightness, byte speed)
	{
		byte lightMode   = MapAppEffectToMode(effect);
		byte lightMix    = MapAppEffectToMixColor(effect);
		byte kbBright    = ToKeyboardBrightness(brightness, effect);
		byte kbSpeed     = ToKeyboardSpeed(speed);
		// Raster's lightSwitch is INVERTED — the vendor JS sets `lightSwitch: e ? 0 : 1`.
		// So 0 = ON (backlight lit), 1 = OFF. Every other keyboard in this codebase uses the
		// opposite convention, which is what tripped us up for so long.
		byte lightSwitch = effect == 0xED ? (byte)1 : (byte)0;

		_logger?.LogInformation("[LED] Raster SetLed r={R} g={G} b={B} appEffect=0x{E:X2} bri={Bri} spd={Sp} -> mode={M} mix={Mix} switch={Sw} kbBri={KBri} kbSpd={KSp}",
			r, g, b, effect, brightness, speed, lightMode, lightMix, lightSwitch, kbBright, kbSpeed);

		if (!_device.TryOpen(out var stream))
		{
			_logger?.LogWarning("[LED] Raster HidSharp TryOpen failed on {Path}", _device.DevicePath);
			return false;
		}

		try
		{
			using (stream)
			{
				stream.ReadTimeout  = 500;
				stream.WriteTimeout = 500;

				// Every command the firmware receives gets an ack response. If we don't consume
				// it, the next read returns the previous command's response instead of the one
				// we actually want — the classic "stale response poisoning the read stream" bug.
				if (await SendAndAwaitAckAsync(stream, CmdStartComm, 0, null, null) == null)
				{
					_logger?.LogWarning("[LED] Raster START_COMM failed, aborting");
					return false;
				}

				var block = await TryReadFeatureAreaAsync(stream);
				if (block == null)
				{
					_logger?.LogWarning("[LED] Raster GET_FUNCINFO failed - cannot safely write without current state, aborting");
					return false;
				}

				LogBlock("read", block);

				MutateLightFields(block, lightSwitch, lightMode, kbBright, kbSpeed, lightMix, r, g, b);

				for (int offset = 0; offset < block.Length; offset += ChunkSize)
				{
					int len = Math.Min(ChunkSize, block.Length - offset);
					var chunk = new byte[len];
					Buffer.BlockCopy(block, offset, chunk, 0, len);
					if (await SendAndAwaitAckAsync(stream, CmdSetFuncInfo, (ushort)offset, chunk, null) == null)
					{
						_logger?.LogWarning("[LED] Raster SET_FUNCINFO ack failed at offset {Off}", offset);
						return false;
					}
				}

				// Read the block back so the log shows what the firmware RETAINED, not just what
				// we asked for. Needed to tell "field rejected or clamped" apart from "field
				// accepted but this effect ignores it" — the two are indistinguishable from the
				// write side, since SET_FUNCINFO acks either way. Costs two extra HID
				// round-trips, so it only runs when someone is actually reading the log.
				if (_logger?.IsEnabled(LogLevel.Debug) == true)
				{
					var verify = await TryReadFeatureAreaAsync(stream);
					if (verify != null) LogBlock("verify", verify);
				}
			}

			_logger?.LogInformation("[LED] Raster read-modify-write complete");
			return true;
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[LED] Raster HID exchange failed");
			return false;
		}
	}

	// Dumps the main-light header plus the raw block. The raw dump matters because the bytes
	// past the light sub-blocks are not decoded here: the vendor ships two getFuncInfo layouts
	// selected by protocol version, and they disagree from offset 29 on (matrixScreenLight* in
	// one, sixKeys / winLock / wasd in the other). Which one Raster speaks is not established,
	// so those bytes are printed unlabelled rather than named wrongly.
	//
	// Debug level: this runs on the slider-debounce path, so it is off in normal operation.
	private void LogBlock(string tag, byte[] block)
	{
		// Checked up front, not left to the logger: the hex dump below is built eagerly as an
		// argument, so without this it allocates a 128-character string on every colour change
		// even with Debug off.
		if (_logger?.IsEnabled(LogLevel.Debug) != true) return;

		_logger.LogDebug(
			"[LED] Raster {Tag} main sw={Sw:X2} mode={Mode:X2} bri={Bri:X2} spd={Spd:X2} mix={Mix:X2} colorIdx={Ci:X2} rgb={R:X2}{G:X2}{B:X2} customIdx={Cu:X2}",
			tag, block[1], block[2], block[3], block[4], block[5], block[6], block[7], block[8], block[9], block[10]);

		_logger.LogDebug("[LED] Raster {Tag} full = {Hex}", tag, Convert.ToHexString(block));
	}

	// Reads the current 64-byte funcInfo block via GET_FUNCINFO chunks. Returns null if any
	// chunk fails validation — the caller must NOT write a partial or fabricated block.
	private async Task<byte[]?> TryReadFeatureAreaAsync(HidStream stream)
	{
		var block = new byte[64];

		for (int offset = 0; offset < block.Length; offset += ChunkSize)
		{
			int len = Math.Min(ChunkSize, block.Length - offset);
			var ack = await SendAndAwaitAckAsync(stream, CmdGetFuncInfo, (ushort)offset, data: null, explicitLen: (byte)len);
			if (ack == null) return null;

			Buffer.BlockCopy(ack, 9, block, offset, len);
		}

		return block;
	}

	// Sends a command and consumes its ack response. Returns the response buffer (or null on
	// timeout / header mismatch). Keeping the read paired with the write is critical — every
	// firmware command emits an ack that must be drained before the next request.
	private async Task<byte[]?> SendAndAwaitAckAsync(HidStream stream, byte cmd, ushort addr, byte[]? data, byte? explicitLen)
	{
		await SendCommandAsync(stream, cmd, addr, data, explicitLen);

		var buf = new byte[ReportLen];
		int read;
		try
		{
			read = stream.Read(buf);
		}
		catch (TimeoutException)
		{
			_logger?.LogWarning("[LED] Raster ack read timed out for cmd=0x{Cmd:X2} at addr={Addr}", cmd, addr);
			return null;
		}

		if (read < 9)
		{
			_logger?.LogWarning("[LED] Raster ack short read for cmd=0x{Cmd:X2}: got {Read} bytes", cmd, read);
			return null;
		}

		if (buf[1] != MagicByte || buf[2] != cmd || buf[8] != ResponseSuccess)
		{
			_logger?.LogWarning("[LED] Raster ack header mismatch cmd=0x{Cmd:X2} at addr={Addr}: got magic={B1:X2} echo={B2:X2} status={B8:X2}",
				cmd, addr, buf[1], buf[2], buf[8]);
			return null;
		}

		return buf;
	}

	// Overwrites the main-light fields plus mirrors them into the LOGO and side-light
	// sub-blocks so the volume-button LED and any case-edge strip follow the same effect
	// and colour as the main backlight. Every other byte (colorIndex, customIndex, matrix
	// screen, misc settings, sleep timers, LCD, snapTap) stays as read from the keyboard.
	// internal for tests (see ProoveGaming.Infrastructure.csproj): which bytes this leaves ALONE
	// is as load-bearing as the ones it writes, and only a test can hold that still.
	internal static void MutateLightFields(byte[] block, byte lightSwitch, byte lightMode, byte brightness, byte speed, byte lightMix, byte r, byte g, byte b)
	{
		byte speedByte = (byte)(6 - speed);  // firmware stores inverted speed

		// Main backlight (bytes 1..10)
		block[1] = lightSwitch;
		block[2] = lightMode;
		block[3] = brightness;
		block[4] = speedByte;
		block[5] = lightMix;
		// byte[6] = lightColorIndex — PRESERVED from read. On this vendor family the field
		// selects a preset colour (0-6 = Red..Purple, 7 = White for custom R/G/B, 8 =
		// Multicolour). Overwriting it to 0 makes the firmware use the Red preset and
		// ignore R/G/B, which visibly blanked the keyboard. Writing 7 here was tried and did
		// NOT make the keys follow the picker, so which value selects custom R/G/B on this
		// firmware is still unknown — see docs/plans/raster-rgb/raster-rgb-plan.md.
		block[7] = r;
		block[8] = g;
		block[9] = b;
		// byte[10] = lightCustomIndex — PRESERVED (only meaningful for user modes >= 253).

		// LOGO light (bytes 11..19) — full mirror of the main channel so the volume-button
		// LED animates in the same effect and colour as the main backlight. The firmware
		// runs each channel on its own independent phase timer, so identical `speed` values
		// don't guarantee perfectly locked animation — a slight visible drift on slow
		// effects like Breathing is a firmware-level constraint, not a bug we can fix
		// from the protocol side.
		block[11] = lightSwitch;
		block[12] = lightMode;
		block[13] = brightness;
		block[14] = speedByte;
		block[15] = lightMix;
		block[16] = 0;   // logoLightColorIndex — safe to zero; firmware uses R/G/B when mix=0
		block[17] = r;
		block[18] = g;
		block[19] = b;

		// Side light (bytes 20..28) — same mirror. Silently ignored on hardware without a
		// side strip.
		block[20] = lightSwitch;
		block[21] = lightMode;
		block[22] = brightness;
		block[23] = speedByte;
		block[24] = lightMix;
		block[25] = 0;
		block[26] = r;
		block[27] = g;
		block[28] = b;
	}

	private static byte MapAppEffectToMode(byte appEffect) => appEffect switch
	{
		0x00 => ModeBreathing,   // RainbowBreathing (mix=1)
		0x01 => ModeBreathing,   // Breathing        (mix=0)
		0x02 => ModeColorful,    // Neon
		0x03 => ModeStreaming,   // ColorCycle
		0x04 => ModeSteady,      // Static
		0x05 => ModeStreaming,   // Wave
		0xED => ModeSteady,      // Off — lightSwitch=0 handles the actual off state
		_    => ModeSteady,
	};

	// lightMixColor: 0 = monochrome (use R/G/B), 1 = multicolour flow.
	private static byte MapAppEffectToMixColor(byte appEffect) => appEffect switch
	{
		0x00 => 1, // RainbowBreathing
		0x01 => 0, // Breathing
		0x02 => 1, // Neon
		0x03 => 1, // ColorCycle
		0x04 => 0, // Static
		0x05 => 1, // Wave
		_    => 0,
	};

	// App sends 0-255 brightness. Vendor JS uses `Math.round(maxBrightness/100 * percent)`,
	// so 0..100 is the natural scale here. Reading `lightMaxBrightness` from the device
	// info block would let us clamp precisely — not implemented yet.
	private static byte ToKeyboardBrightness(byte brightness, byte effect)
	{
		if (effect == 0xED || brightness == 0) return 0;
		var level = (int)Math.Round(brightness / 255.0 * 100);
		return (byte)Math.Clamp(level, 1, 100);
	}

	// App speed is 1-7 (higher = faster). Firmware stores `6 - speed`, done at BuildFeatureArea
	// time — this returns the app's semantic scaled to 0..6.
	private static byte ToKeyboardSpeed(byte speed)
	{
		if (speed == 0) return 3;
		return (byte)Math.Clamp((int)Math.Round((speed - 1) / 6.0 * 6), 0, 6);
	}

	private static async Task SendCommandAsync(HidStream stream, byte command, ushort addr = 0, byte[]? data = null, byte? explicitLen = null)
	{
		var report = new byte[ReportLen];
		report[0] = HidReportId;
		report[1] = MagicByte;
		report[2] = command;
		report[3] = (byte)(addr & 0xFF);
		report[4] = (byte)((addr >> 8) & 0xFF);
		report[5] = explicitLen ?? (byte)(data?.Length ?? 0);
		// bytes 6..8 stay zero on write
		if (data != null && data.Length > 0)
			Buffer.BlockCopy(data, 0, report, 9, data.Length);

		await stream.WriteAsync(report);
	}

	// ── Mouse-only surface (unsupported on a keyboard) ───────────────────────────
	// IMouseRgbService is implemented across the whole family for factory-dispatch reasons;
	// the mouse-specific methods below are inherited-only-because-of-the-interface and have
	// no keyboard meaning. All return false / are no-ops.
	public Task<bool> SetDpiStagesAsync(DpiStageParams[] stages) => Task.FromResult(false);
	public Task<bool> SetActiveDpiStageAsync(byte stageIndex) => Task.FromResult(false);
	public Task<bool> SetReportRateAsync(int hz) => Task.FromResult(false);
	public Task<bool> RequestCurrentStateAsync() => Task.FromResult(false);
	public Task<bool> SetButtonBindingsAsync(System.Collections.Generic.IReadOnlyList<ProoveGaming.Infrastructure.Classes.ButtonBinding> bindings) => Task.FromResult(false);

	public void SyncStateFromDevice(byte reportRateRaw, byte dpiStage, byte ledEffect) { }
}
