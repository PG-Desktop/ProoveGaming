using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// Locks down the 128-byte BLAZE / X43 macro upload format. This encoding had several
// hardware-driven bugs (delay scale, minimum-1, top-of-range cap, macro type byte), so the
// byte layout is pinned here. Baseline: docs/mouse/blaze/macros.md.
public class X43MacroEncoderTests
{
	private static MacroDefinition Macro(int repeat, params MacroEvent[] events) =>
		new("test", repeat, MacroRepeatMode.PlayCount, events);

	private static MacroEvent Ev(byte code, bool press, int delayMs) =>
		new(code, press, delayMs);

	[Fact]
	public void Encode_ReturnsExactly128Bytes()
	{
		Assert.Equal(128, X43MacroEncoder.Encode(Macro(1)).Length);
	}

	// Repeat count occupies bytes [0,1] little-endian, clamped to 1..65535 (0/negative -> 1).
	[Theory]
	[InlineData(1, 0x01, 0x00)]
	[InlineData(0, 0x01, 0x00)]      // clamped up to 1
	[InlineData(-5, 0x01, 0x00)]     // clamped up to 1
	[InlineData(300, 0x2C, 0x01)]    // 0x012C little-endian
	[InlineData(65535, 0xFF, 0xFF)]
	[InlineData(70000, 0xFF, 0xFF)]  // clamped down to 65535
	public void Encode_WritesRepeatCountLittleEndian(int repeat, byte lo, byte hi)
	{
		var buf = X43MacroEncoder.Encode(Macro(repeat));
		Assert.Equal(lo, buf[0]);
		Assert.Equal(hi, buf[1]);
	}

	[Fact]
	public void Encode_EmptyMacro_IsRepeatThenAllZero()
	{
		var buf = X43MacroEncoder.Encode(Macro(1));
		Assert.Equal(0x01, buf[0]);
		Assert.Equal(0x00, buf[1]);
		for (var i = 2; i < 128; i++)
			Assert.Equal(0, buf[i]);
	}

	// A full event stream: press A (0-delay -> min 1 unit), then release A after 100 ms (10 units).
	// attr = press(0x00)/release(0x80) OR delay-units; code follows. Then the 00 00 terminator.
	[Fact]
	public void Encode_WritesEventBytesThenTerminator()
	{
		var buf = X43MacroEncoder.Encode(Macro(1,
			Ev(0x04, press: true, delayMs: 0),
			Ev(0x04, press: false, delayMs: 100)));

		// repeat
		Assert.Equal(new byte[] { 0x01, 0x00 }, buf[0..2]);
		// event 0: press, delay clamped to min 1 -> attr 0x01, code 0x04
		// event 1: release, 100 ms -> 10 units -> attr 0x80|0x0A = 0x8A, code 0x04
		Assert.Equal(new byte[] { 0x01, 0x04, 0x8A, 0x04 }, buf[2..6]);
		// terminator
		Assert.Equal(new byte[] { 0x00, 0x00 }, buf[6..8]);
	}

	// A press event's attr low-7-bits carry the delay in 10 ms units: round-to-nearest
	// (ties away from zero), a floor of 1, and a ceiling of MaxDelayUnits (100).
	[Theory]
	[InlineData(0, 1)]      // floor
	[InlineData(4, 1)]      // rounds to 0 -> floored to 1
	[InlineData(5, 1)]      // 0.5 rounds away to 1
	[InlineData(14, 1)]     // rounds to 1
	[InlineData(15, 2)]     // 1.5 rounds away to 2
	[InlineData(25, 3)]     // 2.5 rounds away to 3
	[InlineData(50, 5)]
	[InlineData(1000, 100)]
	[InlineData(2000, 100)] // ceiling
	public void Encode_DelayUnits_AreRoundedFlooredAndCapped(int delayMs, byte expectedUnits)
	{
		var buf = X43MacroEncoder.Encode(Macro(1, Ev(0x04, press: true, delayMs: delayMs)));
		// press event -> attr bit 7 clear, so attr == delay units
		Assert.Equal(expectedUnits, buf[2]);
	}

	[Fact]
	public void Encode_ReleaseEvent_SetsHighBitOnAttr()
	{
		var buf = X43MacroEncoder.Encode(Macro(1, Ev(0x04, press: false, delayMs: 50)));
		// 50 ms -> 5 units, release -> 0x80|0x05 = 0x85
		Assert.Equal(0x85, buf[2]);
	}

	[Fact]
	public void MaxEvents_IsSixtyTwo()
	{
		// 128-byte buffer − 2 repeat − 2 terminator = 124 bytes / 2 per event.
		Assert.Equal(62, X43MacroEncoder.MaxEvents);
	}

	// Events are dropped once the buffer is full, and the 00 00 terminator slot is never
	// overwritten. Exactly MaxEvents fit ([2..125]); the rest are ignored.
	[Fact]
	public void Encode_OverflowingEvents_AreTruncatedAndTerminatorPreserved()
	{
		var events = new MacroEvent[X43MacroEncoder.MaxEvents + 20];
		for (var i = 0; i < events.Length; i++)
			events[i] = Ev(0x04, press: true, delayMs: 10);

		var buf = X43MacroEncoder.Encode(Macro(1, events));

		Assert.Equal(128, buf.Length);
		// last accepted event pair sits at [124,125]; the terminator [126,127] stays zero
		var lastEventOffset = 2 + (X43MacroEncoder.MaxEvents - 1) * 2;
		Assert.Equal(124, lastEventOffset);
		Assert.NotEqual(0, buf[lastEventOffset]);
		Assert.Equal(0, buf[126]);
		Assert.Equal(0, buf[127]);
	}

	[Theory]
	[InlineData(MacroRepeatMode.PlayCount, 0x01)]
	[InlineData(MacroRepeatMode.LoopForever, 0x01)]
	[InlineData(MacroRepeatMode.WhileHeld, 0x02)]
	public void TypeByte_MapsMode(MacroRepeatMode mode, byte expected)
	{
		Assert.Equal(expected, X43MacroEncoder.TypeByte(mode));
	}
}
