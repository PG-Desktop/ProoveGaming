using HidSharp;
using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

// Aress is a magnetic wired keyboard speaking the SmartInfinite v1.4 protocol. It shares the
// wire format with Rampage, but the firmware has per-effect quirks (starting with an inverted
// light_level for Steady On / Static), so it lives in its own service to avoid regressing
// Rampage while we tune Aress behaviour.
//
// App effect byte -> (light_mode, light_RGB):
//   0x01 = Breathing        -> mode=9  (Breathing), RGB=0 (monochrome, custom colour)
//   0x00 = RainbowBreathing -> mode=9  (Breathing), RGB=1 (multicolour)
//   0x02 = Neon             -> mode=2  (Neon),      RGB=1 (multicolour flow)
//   0x03 = ColorCycle       -> mode=8  (Spectrum),  RGB=1 (multicolour)
//   0x04 = Static           -> mode=10 (Steady On), RGB=0 (monochrome, custom colour)
//   0x05 = Wave             -> mode=0  (Wave),      RGB=1 (multicolour)
//   0xED = Off              -> mode=10 (Steady On), brightness=0
public class AressKeyboardRgbService(HidDevice device, ILogger? logger = null) : IDeviceRgbService
{
	private const byte CmdStart = 0x10;
	private const byte CmdEnd = 0x11;
	private const byte CmdSetGeneralInfo = 0x15;
	private const byte CmdSyncLighting = 0x1D;

	private const byte ModeWave = 0;
	private const byte ModeNeon = 2;
	private const byte ModeSpectrum = 8;
	private const byte ModeBreathing = 9;
	private const byte ModeSteadyOn = 10;

	// light_colour indices: 7 = White (used when RGB=0 the field is ignored), 8 = Multicolour.
	private const byte ColourIndexWhite      = 7;
	private const byte ColourIndexMulticolor = 8;

	private readonly HidDevice _device = device ?? throw new ArgumentNullException(nameof(device));
	private readonly ILogger? _logger = logger;

	public async Task<bool> SetLedColorAsync(byte r, byte g, byte b, byte effect, byte brightness, byte speed)
	{
		var (lightMode, lightRgb) = MapAppEffectToProtocol(effect);
		byte kbBrightness         = ToKeyboardBrightness(brightness, effect);
		byte kbSpeed              = ToKeyboardSpeed(speed);

		_logger?.LogInformation("[LED] Aress SetLed r={R} g={G} b={B} appEffect=0x{E:X2} bri={Bri} spd={Sp} -> mode={M} rgb={Rgb} kbBri={KBri} kbSpd={KSp}",
			r, g, b, effect, brightness, speed, lightMode, lightRgb, kbBrightness, kbSpeed);

		// One session per vendor interface: key mapping brackets its work in the same 0x10 … 0x11 pair
		// on this very path, and a closing 0x11 ends the session for the interface, not just for the
		// sender. Without a shared gate an RGB write landing mid-read strands the reader's remaining
		// packets, which surfaces to the user as "could not read the layout".
		// Bounded, not an open-ended wait: an unbounded AcquireAsync cannot be abandoned, so a gate
		// holder that wedges queues every later write for the life of the process.
		using var gate = await DeviceGate.TryAcquireAsync(_device.DevicePath, DeviceGate.ForegroundWriteTimeout);
		if (gate is null)
		{
			_logger?.LogWarning("[LED] Aress gave up waiting for the device gate on {Path}", _device.DevicePath);
			return false;
		}

		if (!_device.TryOpen(out var stream))
		{
			_logger?.LogWarning("[LED] Aress HidSharp TryOpen failed on {Path}", _device.DevicePath);
			return false;
		}

		try
		{
			using (stream)
			{
				stream.ReadTimeout  = 500;
				stream.WriteTimeout = 500;

				var block = BuildFeatureArea(lightMode, lightRgb, r, g, b, kbBrightness, kbSpeed);

				// SetGeneralInfo carries at most 56 data bytes per packet (8-byte header + 56-byte
				// payload), so the 64-byte feature area is sent as a 56-byte head + 8-byte tail.
				var head = block.AsSpan(0, 56).ToArray();
				var tail = block.AsSpan(56, 8).ToArray();

				var sequence = new (byte Cmd, ushort Addr, byte[]? Data, int DelayMs)[]
				{
					(CmdStart,          0,  null, 10),
					(CmdSetGeneralInfo, 0,  head, 5),
					(CmdSetGeneralInfo, 56, tail, 5),
					(CmdSyncLighting,   0,  null, 10),
					(CmdEnd,            0,  null, 0),
				};

				foreach (var (cmd, addr, data, delayMs) in sequence)
				{
					await SendCommandAsync(stream, cmd, addr, data);
					if (delayMs > 0) Thread.Sleep(delayMs);
				}
			}

			_logger?.LogInformation("[LED] Aress packet sequence complete");
			return true;
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[LED] Aress HID write failed");
			return false;
		}
	}

	private static (byte lightMode, byte lightRgb) MapAppEffectToProtocol(byte appEffect) => appEffect switch
	{
		0x01 => (ModeBreathing, (byte)0),
		0x00 => (ModeBreathing, (byte)1),
		0x02 => (ModeNeon,      (byte)1),
		0x03 => (ModeSpectrum,  (byte)1),
		0x04 => (ModeSteadyOn,  (byte)0),
		0x05 => (ModeWave,      (byte)1),
		0xED => (ModeSteadyOn,  (byte)0),
		_    => (ModeSteadyOn,  (byte)0),
	};

	// App sends 0-255 brightness. Aress reads light_level as a small discrete 0-4 scale (the
	// same 5 steps exposed on the keyboard via Fn+↑/Fn+↓); mapping the slider into the doc's
	// 0-99 range lands on values the firmware reads non-monotonically. Off (0xED) forces 0.
	// Any non-zero slider value must produce at least level 1 — otherwise the ~12.5% rounding
	// boundary makes low slider positions read as "backlight off".
	private static byte ToKeyboardBrightness(byte brightness, byte effect)
	{
		if (effect == 0xED || brightness == 0) return 0;
		var level = (int)Math.Round(brightness / 255.0 * 4);
		return (byte)Math.Clamp(level, 1, 4);
	}

	// App sends 1-7 speed (higher = faster); the SmartInfinite light_speed field is inverted
	// (0 = fastest, 4 = slowest).
	private static byte ToKeyboardSpeed(byte speed)
	{
		if (speed == 0) return 2;
		return (byte)Math.Clamp(4 - (int)Math.Round((speed - 1) / 6.0 * 4), 0, 4);
	}

	private static byte[] BuildFeatureArea(byte lightMode, byte lightRgb, byte r, byte g, byte b, byte brightness, byte speed)
	{
		var block = new byte[64];
		block[0]  = 0;              // profile_index
		block[1]  = lightMode;      // light_mode
		block[2]  = brightness;     // light_level (0-4 — see ToKeyboardBrightness)
		block[3]  = speed;          // light_speed (0-4)
		block[4]  = 0;              // light_trend (forward)
		block[5]  = lightRgb;       // light_RGB: 0 = monochrome, 1 = multicolour
		block[6]  = r;
		block[7]  = g;
		block[8]  = b;
		block[9]  = lightRgb == 1 ? ColourIndexMulticolor : ColourIndexWhite;

		// bytes 15-24: per-connection OFF_LED / SLEEP timers pinned to 0xFFFF so animated modes
		// don't auto-blank while the app is running.
		for (int i = 15; i <= 24; i++) block[i] = 0xFF;

		return block;
	}

	// Framing is shared across the SmartInfinite family — see RampageProtocol.
	// WARNING: the bytes on the wire CHANGED. The copy that used to live here computed
	// CRC16/Modbus into bytes 5-6; RampageProtocol computes the additive checksum the vendor
	// captures actually show, so those two bytes differ on every non-empty command. RGB worked
	// with the wrong CRC, which suggests the firmware ignores the field — but that is an
	// inference, not a verified fact, and this device is already shipped. See Risk 1a and QA
	// step 11 in docs/plans/rampage-key-assignments/.
	private static async Task SendCommandAsync(HidStream stream, byte command, ushort addr = 0, byte[]? data = null)
		=> await stream.WriteAsync(RampageProtocol.BuildCommand(command, addr, data));
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
