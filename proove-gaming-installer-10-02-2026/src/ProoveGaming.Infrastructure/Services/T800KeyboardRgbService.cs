using HidSharp;
using Microsoft.Extensions.Logging;
using ProoveGaming.Infrastructure.Classes;
using ProoveGaming.Infrastructure.Interfaces;

namespace ProoveGaming.Infrastructure.Services;

// T800 (VID 12625 / 0x3151, PID 20525 / 0x502D) — Rongyuan RY5088 magnetic-axis MCU.
//
// Protocol references:
//   docs/knowledge-base/ry5088-magnetic-axis-protocol.md      (command set, magnetic-axis extensions)
//   docs/knowledge-base/rongyuan-yc3123-gaming-protocol.md    (LED Param Table — effect/colour values)
//
// The vendor RGB channel is a **HID feature report** on the mi_02 vendor-defined interface,
// report ID 0x00 with 64 protocol bytes (65-byte HidSharp buffer). The spec states the USB
// control transfer explicitly — `21 09 00 03 02 00 40 00` = SET_REPORT, type 3 (Feature),
// report ID 0, interface 2, 64 bytes — so this is not a guess and there is no Output endpoint
// to write to instead.
//
// Wire packets:
//
//   SET_LEDPARAM (0x07) — seven parameters, so CS lands on protocol byte 8:
//     [0]=0x00 report ID, [1]=0x07, [2]=Effect, [3]=Speed, [4]=Bri, [5]=Option,
//     [6]=R, [7]=G, [8]=B, [9]=CS, [10..64]=0
//
//   SET_LEDONOFF (0x05) — six parameters, so CS lands on protocol byte 7:
//     [0]=0x00 report ID, [1]=0x05, [2]=MainOff, [3]=SideOff, [4..7]=0, [8]=CS, [9..64]=0
//
// Three things sank the previous attempt at this device (commit 620b852, "T800 rgb (not
// working)"), all of them now pinned down by the YC3123 LED Param Table:
//
//   1. Speed and Bri are 0..4 — not 0..100 (Raster's scale) and not 0..255. Sending 100 for
//      brightness is far outside the enum.
//   2. The Option byte is not a monochrome/multicolour flag. Its low nibble selects the COLOUR
//      SOURCE, and values 0..6 are hard-coded preset colours for which the firmware ignores the
//      R/G/B bytes outright. Option=0 — the previous default for Static and Breathing — means
//      "preset red", which is why the picker did nothing.
//   3. Effect values are the table's own codes (Static=0x01, Breathe=0x02, Neon=0x03, Wave=0x04),
//      not a sequential 1..6 invented to match the app's effect list.
//
// The firmware tracks on/off (vendor code `05 03 xx 04`) separately from the effect, so writing
// an effect while the light is off does not relight it. Every non-Off write is therefore preceded
// by an unconditional `0x05 00 00` re-enable; the firmware no-ops when already on.
//
// Unlike Raster there is no read-modify-write: RY5088 write commands are independent and carry
// their whole state in the packet. Also unlike Raster the writes are fire-and-forget — the
// firmware does not ack a SetFeature, so `true` here means "the report went out over the wire",
// not "the keyboard applied it".
public class T800KeyboardRgbService(HidDevice device, ILogger? logger = null) : IDeviceRgbService
{
	internal const int ReportLen = 65;
	internal const byte HidReportId = 0x00;

	// Command bytes (YC3123 spec command table; identical in the RY5088 doc).
	internal const byte CmdSetLedParam = 0x07;
	internal const byte CmdSetLedOnOff = 0x05;
	internal const byte CmdGetLedParam = 0x87;

	// LED on/off polarity — 0 lights the board, 1 darkens it.
	internal const byte LightOn = 0x00;
	internal const byte LightOff = 0x01;

	// Effect codes from the YC3123 LED Param Table. Only the ones the app can reach are named;
	// the firmware supports 0x00..0x19 (Ripple, Snake, Meteor, Fireworks, …).
	internal const byte EffectOff = 0x00;
	internal const byte EffectStatic = 0x01;
	internal const byte EffectBreathe = 0x02;
	internal const byte EffectNeon = 0x03;
	internal const byte EffectWave = 0x04;

	// Option low nibble — where the effect takes its colour from. 0..6 are preset colours that
	// make the firmware ignore R/G/B entirely, which is why only 7 and 8 are useful here.
	internal const byte ColourAp = 7;   // application-supplied R/G/B — follows the colour picker
	internal const byte ColourFull = 8; // firmware cycles the whole spectrum

	// Option high nibble — per-effect variant. Wave reads it as a direction; the effects this
	// service uses otherwise ignore it.
	internal const byte WaveDirectionRight = 0;

	// Brightness is a plain 0..4 per the LED Param Table.
	internal const byte MaxKeyboardBrightness = 4;

	// Speed is inverted on the wire: a low byte is fast. The range is the vendor-proven 5..1 — the
	// values the ROYUAN notes observed the vendor tool emitting.
	//
	// Wire 0 is deliberately NOT used. The YC3123 table calls the field 0..4, which would make 0 a
	// step faster than the vendor ever sends, and it was tried on the grounds that full slider felt
	// slow against the other boards. It is not verifiable from here: the firmware stores any speed
	// byte unclamped (0, 6 and 7 all read back unchanged), so "the device kept it" says nothing
	// about it animating. Until `dotnet run --project ... -- speed` shows 0 is genuinely faster
	// rather than inert, staying inside the range the vendor tool actually uses is the safe default
	// — an invalid speed that freezes the animation looks exactly like a slider that does nothing.
	internal const byte MaxSpeedLevel = 4;
	internal const byte SlowestWireSpeed = 5;

	// Colour bounds the firmware imposes: white is FA rather than FF, and anything under 0x08 is
	// unlit. NearWhiteChannel is the threshold at which a colour counts as "white" for that rule.
	internal const byte MaxChannel = 0xFA;
	internal const byte NearWhiteChannel = 0xFB;
	internal const byte MinVisibleChannel = 0x08;

	// App-side effect bytes (DeviceDetailsViewModel.Rgb.cs::MapEffectByte).
	internal const byte AppEffectRainbowBreathing = 0x00;
	internal const byte AppEffectBreathing = 0x01;
	internal const byte AppEffectNeon = 0x02;
	internal const byte AppEffectColorCycle = 0x03;
	internal const byte AppEffectStatic = 0x04;
	internal const byte AppEffectWave = 0x05;
	internal const byte AppEffectOff = 0xED;

	private readonly HidDevice _device = device ?? throw new ArgumentNullException(nameof(device));
	private readonly ILogger? _logger = logger;

	public async Task<bool> SetLedColorAsync(byte r, byte g, byte b, byte effect, byte brightness, byte speed)
	{
		byte kbEffect = MapAppEffectToEffect(effect);
		byte kbOption = MapAppEffectToOption(effect);
		byte kbBright = ToKeyboardBrightness(brightness, effect);
		byte kbSpeed = ToKeyboardSpeed(speed);
		var (kr, kg, kb) = ClampColour(r, g, b);

		_logger?.LogInformation("[LED] T800 SetLed r={R} g={G} b={B} appEffect=0x{E:X2} bri={Bri} spd={Sp} -> effect=0x{Eff:X2} option=0x{Opt:X2} kbBri={KBri} kbSpd={KSp} rgb={KR:X2}{KG:X2}{KB:X2}",
			r, g, b, effect, brightness, speed, kbEffect, kbOption, kbBright, kbSpeed, kr, kg, kb);

		// The key-matrix service opens this same mi_02 interface, and a SetFeature/GetFeature pair is
		// not atomic against another handle: an RGB write landing inside a matrix read hands the
		// reader an LED reply, which it copies in as a page of garbage. See DeviceGate.
		// Bounded, not an open-ended wait: an unbounded AcquireAsync cannot be abandoned, so a gate
		// holder that wedges queues every later write for the life of the process.
		using var gate = await DeviceGate
			.TryAcquireAsync(_device.DevicePath, DeviceGate.ForegroundWriteTimeout)
			.ConfigureAwait(false);
		if (gate is null)
		{
			_logger?.LogWarning("[LED] T800 gave up waiting for the device gate on {Path}", _device.DevicePath);
			return false;
		}

		if (!_device.TryOpen(out var stream))
		{
			_logger?.LogWarning("[LED] T800 HidSharp TryOpen failed on {Path}", _device.DevicePath);
			return false;
		}

		try
		{
			using (stream)
			{
				if (effect == AppEffectOff)
				{
					var offPacket = BuildOnOffPacket(mainOff: true, sideOff: true);
					_logger?.LogInformation("[LED] T800 SetFeature (off):   {Hex}", HexHead(offPacket));
					stream.SetFeature(offPacket);
				}
				else
				{
					// On/off is a separate firmware state variable and does not follow the effect
					// command, so an Off → non-Off transition needs this. Idempotent when already on.
					var onPacket = BuildOnOffPacket(mainOff: false, sideOff: false);
					var paramPacket = BuildLedParamPacket(kbEffect, kbSpeed, kbBright, kbOption, kr, kg, kb);
					_logger?.LogInformation("[LED] T800 SetFeature (on):    {Hex}", HexHead(onPacket));
					_logger?.LogInformation("[LED] T800 SetFeature (param): {Hex}", HexHead(paramPacket));
					stream.SetFeature(onPacket);
					stream.SetFeature(paramPacket);
				}

				LogReadBack(stream);
			}

			return true;
		}
		catch (Exception ex)
		{
			_logger?.LogError(ex, "[LED] T800 HID exchange failed");
			return false;
		}
	}

	// Reads the light state back with GET_LEDPARAM and logs it beside what we just wrote.
	//
	// A SetFeature that Windows accepts proves only that the report reached the device — the
	// firmware does not ack, so a packet it parsed and a packet it discarded look identical from
	// the write side. That ambiguity is exactly what made the previous attempt at this device
	// unfalsifiable. mi_02's feature report is bidirectional, so the retained state is readable,
	// and the comparison says which of the two happened.
	//
	// Logged at Information, not Debug, deliberately: the encoding is not verified on hardware yet
	// and DeviceHost does not run with Debug enabled, so a Debug line here is invisible exactly
	// when it is needed. Drop this to Debug (or remove it) once the board is confirmed working.
	private void LogReadBack(HidStream stream)
	{
		if (_logger is null) return;

		try
		{
			var request = new byte[ReportLen];
			request[1] = CmdGetLedParam;
			request[8] = Checksum(request, 1, 7);
			stream.SetFeature(request);

			var reply = new byte[ReportLen];
			stream.GetFeature(reply);

			// All-zero or all-0xFF means the firmware answered nothing useful, which on this device
			// most likely means it rejected the request — and the prime suspect is the checksum,
			// the one field in the packet no vendor document defines.
			var body = reply.AsSpan(1, 10);
			bool parsed = false;
			foreach (var x in body)
				if (x != 0x00 && x != 0xFF) { parsed = true; break; }

			_logger.LogInformation("[LED] T800 read-back:          {Hex}{Note}",
				HexHead(reply, 10),
				parsed ? string.Empty : "  (no usable reply - suspect the CS byte; see T800LedProbe)");
		}
		catch (Exception ex)
		{
			_logger.LogInformation("[LED] T800 read-back unavailable: {Msg}", ex.Message);
		}
	}

	// SET_LEDPARAM. Seven parameters, so CS sits at protocol byte 8 (buffer index 9).
	internal static byte[] BuildLedParamPacket(byte effect, byte speed, byte brightness, byte option, byte r, byte g, byte b)
	{
		var packet = new byte[ReportLen];
		packet[0] = HidReportId;
		packet[1] = CmdSetLedParam;
		packet[2] = effect;
		packet[3] = speed;
		packet[4] = brightness;
		packet[5] = option;
		packet[6] = r;
		packet[7] = g;
		packet[8] = b;
		packet[9] = Checksum(packet, 1, 8);
		return packet;
	}

	// SET_LEDONOFF. Six parameters, so CS sits at protocol byte 7 (buffer index 8).
	internal static byte[] BuildOnOffPacket(bool mainOff, bool sideOff)
	{
		var packet = new byte[ReportLen];
		packet[0] = HidReportId;
		packet[1] = CmdSetLedOnOff;
		packet[2] = mainOff ? LightOff : LightOn;
		packet[3] = sideOff ? LightOff : LightOn;
		// bytes [4..7] stay zero per the spec.
		packet[8] = Checksum(packet, 1, 7);
		return packet;
	}

	// One definition of the checksum for the whole RY5088 family — the key-matrix service needs the
	// identical rule, and two copies of a value the vendor documents do not define is exactly the
	// kind of drift that costs another hardware session. See Ry5088Protocol.Checksum for why it is
	// the one's complement and what a wrong value does (nothing visible, which is the problem).
	//
	//   Bit7 checksum — covers bytes 0..6, stored at byte 7 — SET_LEDONOFF and everything else
	//   Bit8 checksum — covers bytes 0..7, stored at byte 8 — SET_LEDPARAM and SET_SLEDPARAM
	//
	// Callers pass buffer positions inclusive; the CS byte itself is never part of the range.
	internal static byte Checksum(byte[] packet, int fromInclusive, int toInclusive) =>
		Ry5088Protocol.Checksum(packet, fromInclusive, toInclusive);

	// The app's six effects onto the firmware's table. The app has no direction/variant control,
	// so Wave always flows right; ColorCycle is Wave in full-colour mode, which is what the app's
	// label describes and what Raster does with the same effect (both ColorCycle and Wave map to
	// its streaming mode, differing only in the colour source).
	internal static byte MapAppEffectToEffect(byte appEffect) => appEffect switch
	{
		AppEffectRainbowBreathing => EffectBreathe,
		AppEffectBreathing => EffectBreathe,
		AppEffectNeon => EffectNeon,
		AppEffectColorCycle => EffectWave,
		AppEffectStatic => EffectStatic,
		AppEffectWave => EffectWave,
		AppEffectOff => EffectOff,
		_ => EffectStatic,
	};

	// Option = (Type << 4) | Colour. Colour is the load-bearing half: ColourAp (7) is what makes
	// the firmware read the R/G/B bytes, so every effect the user picks a colour for must use it.
	// The multicolour effects use ColourFull (8) instead and their R/G/B is ignored — Neon ignores
	// the colour field altogether per the table, so it gets ColourFull for consistency.
	internal static byte MapAppEffectToOption(byte appEffect) => appEffect switch
	{
		AppEffectRainbowBreathing => ColourFull,
		AppEffectBreathing => ColourAp,
		AppEffectNeon => ColourFull,
		AppEffectColorCycle => (byte)((WaveDirectionRight << 4) | ColourFull),
		AppEffectStatic => ColourAp,
		AppEffectWave => (byte)((WaveDirectionRight << 4) | ColourAp),
		_ => ColourAp,
	};

	// App sends 0-255; the firmware enum is 0..4 where 0 is dark. Anything non-zero keeps at
	// least level 1 so a low-but-nonzero slider never reads as "off".
	internal static byte ToKeyboardBrightness(byte brightness, byte effect)
	{
		if (effect == AppEffectOff || brightness == 0) return 0;
		var level = (int)Math.Round(brightness / 255.0 * MaxKeyboardBrightness);
		return (byte)Math.Clamp(level, 1, MaxKeyboardBrightness);
	}

	// App speed is 1-7 (higher = faster); the wire is inverted, so 5 is slowest and 0 is fastest.
	// Full slider therefore sends 0, not 1 — see MaxSpeedLevel for why the range runs to 0.
	internal static byte ToKeyboardSpeed(byte speed)
	{
		int level = speed == 0
			? MaxSpeedLevel / 2
			: (int)Math.Round((speed - 1) / 6.0 * MaxSpeedLevel);

		return (byte)(SlowestWireSpeed - Math.Clamp(level, 0, MaxSpeedLevel));
	}

	// Two firmware quirks from the ROYUAN notes, both scoped as narrowly as the evidence supports.
	//
	//   - Full white ships as FA FA FA rather than FF FF FF. Applied ONLY to near-white: a blanket
	//     per-channel cap at FA would repaint every saturated colour, and hardware read-back
	//     confirms the firmware stores a plain FF 00 00 unchanged, so there is nothing to fix there.
	//   - A colour whose channels are all below 0x08 renders unlit, which reads as a dead board
	//     rather than as a very dark colour. Brightness is a separate control, so lift the triplet
	//     to the visible floor instead of letting it vanish; scaling the whole triplet by one
	//     factor preserves the hue.
	internal static (byte R, byte G, byte B) ClampColour(byte r, byte g, byte b)
	{
		if (r >= NearWhiteChannel && g >= NearWhiteChannel && b >= NearWhiteChannel)
			return (MaxChannel, MaxChannel, MaxChannel);

		int peak = Math.Max(r, Math.Max(g, b));
		if (peak == 0 || peak >= MinVisibleChannel) return (r, g, b);

		double scale = (double)MinVisibleChannel / peak;
		return ((byte)Math.Round(r * scale), (byte)Math.Round(g * scale), (byte)Math.Round(b * scale));
	}

	// Hex dump of the leading protocol bytes, for comparison against a USB capture. Skips the
	// report-ID prefix at [0] since the wire only carries [1..N].
	private static string HexHead(byte[] packet, int count = 12)
	{
		int end = Math.Min(1 + count, packet.Length);
		var sb = new System.Text.StringBuilder(count * 3);
		for (int i = 1; i < end; i++)
		{
			if (i > 1) sb.Append(' ');
			sb.Append(packet[i].ToString("X2"));
		}
		return sb.ToString();
	}
}
