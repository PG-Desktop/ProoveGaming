using System;
using System.Collections.Generic;
using System.Linq;
using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// Locks down the TAIPAN macro-slot encoding (command 0x05): 3-byte actions, bit7 = Make/Break,
// delays as their own 0x06 actions in 10 ms units, 0x00 end marker via zero padding.
// Baseline: docs/mouse/taipan/protocol.md §Macros.
public class TaipanMacroEncoderTests
{
	private static MacroDefinition Macro(params MacroEvent[] events) =>
		new("test", 1, MacroRepeatMode.PlayCount, events);

	[Fact]
	public void Encode_ReturnsExactlySlotLength()
	{
		var slot = TaipanMacroEncoder.Encode(Macro(new MacroEvent(0x04, true, 10)));
		Assert.Equal(TaipanMacroEncoder.SlotLength, slot.Length);
	}

	[Fact]
	public void Encode_KeyboardMacro_MatchesGoldenBytes()
	{
		// Press A after 50 ms, release A 50 ms later. DelayMs is the time since the PREVIOUS
		// event, so each delay action precedes its event — the 50 ms before the release is the
		// key's hold time. (Emitting delays after the event played inter-key pauses as held
		// keys; hardware showed "123" typing back as "1222333" via OS autorepeat.)
		var slot = TaipanMacroEncoder.Encode(Macro(
			new MacroEvent(0x04, true, 50),
			new MacroEvent(0x04, false, 50)));

		byte[] expected =
		[
			0x06, 0x05, 0x00, // delay 5 × 10 ms (before the press)
			0x84, 0x04, 0x00, // Make A (0x04 | 0x80)
			0x06, 0x05, 0x00, // delay 5 × 10 ms (hold time before the release)
			0x04, 0x04, 0x00, // Break A
			0x00, 0x00, 0x00, // end marker (zero padding)
		];
		Assert.Equal(expected, slot.Take(expected.Length).ToArray());
	}

	// Recorder mouse codes → type-0x01 action with the device bitmask; bit7 follows IsPress.
	[Theory]
	[InlineData(0xF0, 0x01)] // Left
	[InlineData(0xF1, 0x02)] // Right
	[InlineData(0xF2, 0x04)] // Middle
	[InlineData(0xF3, 0x08)] // B4 / Back
	[InlineData(0xF4, 0x10)] // B5 / Forward
	public void Encode_MouseButtons_UseBitmask(byte keyCode, byte bitmask)
	{
		var slot = TaipanMacroEncoder.Encode(Macro(
			new MacroEvent(keyCode, true, 0),
			new MacroEvent(keyCode, false, 0)));

		Assert.Equal(new byte[] { 0x81, bitmask, 0x00, 0x01, bitmask, 0x00 }, slot.Take(6).ToArray());
	}

	[Fact]
	public void Encode_ZeroDelay_EmitsNoDelayAction()
	{
		var slot = TaipanMacroEncoder.Encode(Macro(new MacroEvent(0x05, true, 0)));
		Assert.Equal(new byte[] { 0x84, 0x05, 0x00, 0x00 }, slot.Take(4).ToArray());
	}

	// 10 ms units, round-half-away: 4 ms → 0 units (dropped), 5 ms → 1 unit.
	[Fact]
	public void Encode_DelayQuantization_RoundsAtHalfUnit()
	{
		var below = TaipanMacroEncoder.Encode(Macro(new MacroEvent(0x04, true, 4)));
		Assert.Equal(0x84, below[0]); // key action directly, no preceding delay action

		var at = TaipanMacroEncoder.Encode(Macro(new MacroEvent(0x04, true, 5)));
		Assert.Equal(new byte[] { 0x06, 0x01, 0x00, 0x84 }, at.Take(4).ToArray());
	}

	[Fact]
	public void Encode_DelayCapsAt16Bit()
	{
		var slot = TaipanMacroEncoder.Encode(Macro(new MacroEvent(0x04, true, 10_000_000)));
		Assert.Equal(new byte[] { 0x06, 0xFF, 0xFF }, slot.Take(3).ToArray());
	}

	[Fact]
	public void Encode_DropsEventsBeyondMaxEvents_KeepsEndMarker()
	{
		// 33 delayed events: worst case 2 actions each; only 32 fit alongside the end marker.
		var events = Enumerable.Range(0, TaipanMacroEncoder.MaxEvents + 1)
			.Select(_ => new MacroEvent(0x04, true, 10))
			.ToArray();

		var slot = TaipanMacroEncoder.Encode(Macro(events));

		int encodedBytes = TaipanMacroEncoder.MaxEvents * 2 * TaipanMacroEncoder.ActionLength;
		Assert.Equal(TaipanMacroEncoder.SlotLength, slot.Length);
		Assert.NotEqual(0x00, slot[encodedBytes - TaipanMacroEncoder.ActionLength]); // last action present
		Assert.Equal(0x00, slot[encodedBytes]); // end marker right after the 32nd event
	}

	[Fact]
	public void MaxEvents_Is32()
	{
		Assert.Equal(32, TaipanMacroEncoder.MaxEvents);
	}
}
