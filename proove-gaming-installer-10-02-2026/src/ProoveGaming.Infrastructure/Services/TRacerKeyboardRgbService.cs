using HidSharp;
using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

// T-Racer (VID 14000, PID 12370 wired) — same vendor family as Raster. Speaks the same
// SmartInfinite-adjacent protocol on its mi_01 raw HID interface (report ID 0x00, 65-byte
// reports). This service is a near-copy of RasterKeyboardRgbService so any per-firmware
// quirks discovered later stay isolated from Raster's tuning. Full protocol reference:
// see `docs/plans/raster-rgb/raster-rgb-plan.md`.
public class TRacerKeyboardRgbService(HidDevice device, ILogger? logger = null) : IDeviceRgbService
{
	private const int  ReportLen = 65;
	private const byte HidReportId  = 0x00;
	private const byte MagicByte    = 0xAA;

	private const byte CmdStartComm    = 0x10;
	private const byte CmdGetFuncInfo  = 0x14;
	private const byte CmdSetFuncInfo  = 0x15;

	private const byte ResponseSuccess = 0x55;

	private const int ChunkSize = 56;

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
		byte lightSwitch = effect == 0xED ? (byte)1 : (byte)0;

		_logger?.LogInformation("[LED] T-Racer SetLed r={R} g={G} b={B} appEffect=0x{E:X2} bri={Bri} spd={Sp} -> mode={M} mix={Mix} switch={Sw} kbBri={KBri} kbSpd={KSp}",
			r, g, b, effect, brightness, speed, lightMode, lightMix, lightSwitch, kbBright, kbSpeed);

		if (!_device.TryOpen(out var stream))
		{
			_logger?.LogWarning("[LED] T-Racer HidSharp TryOpen failed on {Path}", _device.DevicePath);
			return false;
		}

		try
		{
			using (stream)
			{
				stream.ReadTimeout  = 500;
				stream.WriteTimeout = 500;

				if (await SendAndAwaitAckAsync(stream, CmdStartComm, 0, null, null) == null)
				{
					_logger?.LogWarning("[LED] T-Racer START_COMM failed, aborting");
					return false;
				}

				var block = await TryReadFeatureAreaAsync(stream);
				if (block == null)
				{
					_logger?.LogWarning("[LED] T-Racer GET_FUNCINFO failed - cannot safely write without current state, aborting");
					return false;
				}

				_logger?.LogInformation("[LED] T-Racer read block[0..10] = {B0:X2} {B1:X2} {B2:X2} {B3:X2} {B4:X2} {B5:X2} {B6:X2} {B7:X2} {B8:X2} {B9:X2} {B10:X2}",
					block[0], block[1], block[2], block[3], block[4], block[5], block[6], block[7], block[8], block[9], block[10]);

				MutateLightFields(block, lightSwitch, lightMode, kbBright, kbSpeed, lightMix, r, g, b);

				for (int offset = 0; offset < block.Length; offset += ChunkSize)
				{
					int len = Math.Min(ChunkSize, block.Length - offset);
					var chunk = new byte[len];
					Buffer.BlockCopy(block, offset, chunk, 0, len);
					if (await SendAndAwaitAckAsync(stream, CmdSetFuncInfo, (ushort)offset, chunk, null) == null)
					{
						_logger?.LogWarning("[LED] T-Racer SET_FUNCINFO ack failed at offset {Off}", offset);
						return false;
					}
				}
			}

			_logger?.LogInformation("[LED] T-Racer read-modify-write complete");
			return true;
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[LED] T-Racer HID exchange failed");
			return false;
		}
	}

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
			_logger?.LogWarning("[LED] T-Racer ack read timed out for cmd=0x{Cmd:X2} at addr={Addr}", cmd, addr);
			return null;
		}

		if (read < 9)
		{
			_logger?.LogWarning("[LED] T-Racer ack short read for cmd=0x{Cmd:X2}: got {Read} bytes", cmd, read);
			return null;
		}

		if (buf[1] != MagicByte || buf[2] != cmd || buf[8] != ResponseSuccess)
		{
			_logger?.LogWarning("[LED] T-Racer ack header mismatch cmd=0x{Cmd:X2} at addr={Addr}: got magic={B1:X2} echo={B2:X2} status={B8:X2}",
				cmd, addr, buf[1], buf[2], buf[8]);
			return null;
		}

		return buf;
	}

	private static void MutateLightFields(byte[] block, byte lightSwitch, byte lightMode, byte brightness, byte speed, byte lightMix, byte r, byte g, byte b)
	{
		byte speedByte = (byte)(6 - speed);

		block[1] = lightSwitch;
		block[2] = lightMode;
		block[3] = brightness;
		block[4] = speedByte;
		block[5] = lightMix;
		block[7] = r;
		block[8] = g;
		block[9] = b;

		block[11] = lightSwitch;
		block[12] = lightMode;
		block[13] = brightness;
		block[14] = speedByte;
		block[15] = lightMix;
		block[16] = 0;
		block[17] = r;
		block[18] = g;
		block[19] = b;

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
		0xED => ModeSteady,      // Off — lightSwitch=1 handles the actual off state
		_    => ModeSteady,
	};

	private static byte MapAppEffectToMixColor(byte appEffect) => appEffect switch
	{
		0x00 => 1,
		0x01 => 0,
		0x02 => 1,
		0x03 => 1,
		0x04 => 0,
		0x05 => 1,
		_    => 0,
	};

	private static byte ToKeyboardBrightness(byte brightness, byte effect)
	{
		if (effect == 0xED || brightness == 0) return 0;
		var level = (int)Math.Round(brightness / 255.0 * 100);
		return (byte)Math.Clamp(level, 1, 100);
	}

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
