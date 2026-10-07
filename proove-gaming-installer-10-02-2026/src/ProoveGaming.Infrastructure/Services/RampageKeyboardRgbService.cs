using HidSharp;
using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

// App effect byte -> (light_mode, light_RGB):
//   0x01 = Breathing        -> mode=9  (Breathing), RGB=0 (monochrome, custom colour)
//   0x00 = RainbowBreathing -> mode=9  (Breathing), RGB=1 (multicolour)
//   0x02 = Neon             -> mode=2  (Neon),      RGB=1 (multicolour flow)
//   0x03 = ColorCycle       -> mode=8  (Spectrum),  RGB=1 (multicolour)
//   0x04 = Static           -> mode=10 (Steady On), RGB=0 (monochrome, custom colour)
//   0x05 = Wave             -> mode=0  (Wave),      RGB=1 (multicolour)
//   0xED = Off              -> mode=10 (Steady On), brightness=0
/// <remarks>
/// <paramref name="maxPayload"/> is the SetGeneralInfo chunk size for the transport this service
/// was resolved for: 56 over the cable, <see cref="RampageProtocol.Receiver24GPayloadLength"/>
/// over the 2.4G receiver — the receiver's native relay framing per C12. QA on a real Rampage
/// receiver saw a static white render cyan; that is consistent with the colour fields shifting
/// one byte in the wired 56-byte head (R reading a neighbouring 0x00), the same silent
/// misalignment the key-matrix transfers exhibit — inferred from the symptom, not captured on
/// the wire. If cyan survives this framing, the shift lives elsewhere: next step is the
/// rampage-24g probe with a white static write.
/// </remarks>
public class RampageKeyboardRgbService(
	HidDevice device, ILogger? logger = null, int maxPayload = RampageProtocol.MaxPayloadLength) : IDeviceRgbService
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
	private readonly int _maxPayload = ValidateMaxPayload(maxPayload);

	// Mirrors SmartInfiniteProtocol.EnumerateChunks' guard: a non-positive payload would spin
	// the chunk loop forever, and one past 56 cannot fit a packet.
	private static int ValidateMaxPayload(int maxPayload)
	{
		ArgumentOutOfRangeException.ThrowIfLessThan(maxPayload, 1);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPayload, RampageProtocol.MaxPayloadLength);
		return maxPayload;
	}

	public async Task<bool> SetLedColorAsync(byte r, byte g, byte b, byte effect, byte brightness, byte speed)
	{
		var (lightMode, lightRgb) = MapAppEffectToProtocol(effect);
		byte kbBrightness         = ToKeyboardBrightness(brightness, effect);
		byte kbSpeed              = ToKeyboardSpeed(speed);

		_logger?.LogInformation("[LED] Rampage SetLed r={R} g={G} b={B} appEffect=0x{E:X2} bri={Bri} spd={Sp} -> mode={M} rgb={Rgb} kbBri={KBri} kbSpd={KSp}",
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
			_logger?.LogWarning("[LED] Rampage gave up waiting for the device gate on {Path}", _device.DevicePath);
			return false;
		}

		if (!_device.TryOpen(out var stream))
		{
			_logger?.LogWarning("[LED] Rampage HidSharp TryOpen failed on {Path}", _device.DevicePath);
			return false;
		}

		try
		{
			using (stream)
			{
				stream.ReadTimeout  = 500;
				stream.WriteTimeout = 500;

				// The firmware appears to require a full 64-byte feature-area write for animated
				// modes to persist. Static (SteadyOn) survives a partial or zeroed write because it
				// doesn't tick the OFF_LED / SLEEP timers at bytes 15-24 — animated modes do, and
				// zeros there meant "turn LEDs off after 0 s". Timers are pinned to 0xFFFF (~18 h)
				// so the effect keeps looping while the app is running.
				var block = BuildFeatureArea(lightMode, lightRgb, r, g, b, kbBrightness, kbSpeed);

				// The 64-byte feature area is sent chunked at the transport's payload size:
				// 56 + 8 over the cable (the historical shape, unchanged), 24-byte packets over
				// the 2.4G receiver — its native relay framing (C12), which the key-matrix
				// transfers already require. See the class remarks for the QA symptom the
				// receiver framing fixes.
				//
				// (command, address, data, delayAfterMs) — one row per packet in the session.
				var sequence = new List<(byte Cmd, ushort Addr, byte[]? Data, int DelayMs)>
				{
					(CmdStart, 0, null, 10),
				};
				for (var offset = 0; offset < block.Length; offset += _maxPayload)
				{
					var chunk = block.AsSpan(offset, Math.Min(_maxPayload, block.Length - offset)).ToArray();
					sequence.Add((CmdSetGeneralInfo, (ushort)offset, chunk, 5));
				}
				sequence.Add((CmdSyncLighting, 0, null, 10));
				sequence.Add((CmdEnd, 0, null, 0));

				foreach (var (cmd, addr, data, delayMs) in sequence)
				{
					await SendCommandAsync(stream, cmd, addr, data);
					if (delayMs > 0) await Task.Delay(delayMs);
				}
			}

			_logger?.LogInformation("[LED] Rampage packet sequence complete");
			return true;
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[LED] Rampage HID write failed");
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

	// App sends 0-255 brightness. Same firmware quirk Aress established for this family:
	// light_level is a small discrete 0-4 scale (the 5 steps on Fn+↑/Fn+↓, and per C9 the
	// factory confirmed Rampage reports max brightness 4), and the doc's 0-99 range lands on
	// values the firmware reads non-monotonically — QA saw the board visibly dim at a 100%
	// slider (level 99) against the vendor app. Off (0xED) forces 0; any non-zero slider
	// value clamps to at least level 1 so low positions don't read as "backlight off".
	// internal for the mapping tests (InternalsVisibleTo) — pure arithmetic, no HID.
	internal static byte ToKeyboardBrightness(byte brightness, byte effect)
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

		// bytes 10-14: trigger_colour, reserved, win_flag, kb_6key_mode, f_wasd — all 0 (defaults).
		// bytes 15-24: per-connection OFF_LED / SLEEP timers. Zero here means "turn off after 0 s"
		// on animated modes, so we pin every timer to the max 16-bit value.
		for (int i = 15; i <= 24; i++) block[i] = 0xFF;

		// bytes 25-45: LOGO/side-lighting settings, magnetic-switch flags, polling rate. Leaving
		// those at 0 mirrors the user's original working SetStaticColor block; they're not part of
		// this RGB path.
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
}
