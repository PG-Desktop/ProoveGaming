using System.Collections.Generic;
using System.Linq;
using ProoveGaming.Infrastructure.Classes;
using Xunit;

namespace ProoveGaming.Infrastructure.Tests;

// The RY5088 (T800) macro blob, per the YC3123 doc's "Macro Table memory information" section
// (docs/knowledge-base/rongyuan-yc3123-gaming-protocol.md): repeat count uint16 LE, then 2-byte
// [code, attr] words — attr bit7 = make/break, bits 0-6 = inline delay or zero for an extended
// 16-bit delay word — closed by 0x00 0x00. The vendor's own "press A, release A" example is the
// golden frame below.
public class Ry5088MacroEncoderTests
{
	private static MacroLibraryEntry Entry(int repeatCount, params MacroEvent[] events) =>
		new(0, MacroRepeatMode.PlayCount, events, repeatCount);

	// ── Golden path ──────────────────────────────────────────────────────────────

	// The vendor example encodes "press A, 1 ms, release A, 1 ms" as 04 81 / 04 01. Our recorder
	// floor is 10 ms, so the same shape carries delay 10: press = 0x80|10, release = 10.
	[Fact]
	public void Encode_PressReleasePair_MatchesTheVendorShape()
	{
		var result = Ry5088MacroEncoder.Encode(Entry(1,
			new MacroEvent(0x04, IsPress: true, DelayMs: 10),
			new MacroEvent(0x04, IsPress: false, DelayMs: 10)));

		Assert.Equal(new byte[]
		{
			0x01, 0x00,             // repeat count 1
			0x04, 0x80 | 10,        // press A, 10 ms inline
			0x04, 10,               // release A, 10 ms inline
			0x00, 0x00,             // end marker
		}, result.Blob);
		Assert.False(result.HasDiscards);
	}

	// MacroEvent.DelayMs is the pause BEFORE the event (the recorder stopwatches the gap since
	// the previous step), while the format's attribute delay plays AFTER its action — so each
	// pause must land on the PREVIOUS action's word. "Press A, wait 1 s, press B" must hold the
	// second between A's release and B's press, not while B is held down.
	[Fact]
	public void Encode_ShiftsEachBeforePauseOntoThePrecedingAction()
	{
		var result = Ry5088MacroEncoder.Encode(Entry(1,
			new MacroEvent(0x04, IsPress: true, DelayMs: 0),     // A down (first step, no lead-in)
			new MacroEvent(0x04, IsPress: false, DelayMs: 10),   // A up after 10 ms
			new MacroEvent(0x05, IsPress: true, DelayMs: 1000),  // B down after the 1 s pause
			new MacroEvent(0x05, IsPress: false, DelayMs: 10))); // B up after 10 ms

		Assert.Equal(new byte[]
		{
			0x01, 0x00,             // repeat count 1
			0x04, 0x80 | 10,        // press A, then wait 10 ms (B's predecessor gap rides below)
			0x04, 0x00, 0xE8, 0x03, // release A, then wait 1000 ms (extended, LE)
			0x05, 0x80 | 10,        // press B, then wait 10 ms
			0x05, 10,               // release B, minimum trailing delay
			0x00, 0x00,             // end marker
		}, result.Blob);
	}

	[Theory]
	[InlineData(1, 0x01, 0x00)]
	[InlineData(3, 0x03, 0x00)]
	[InlineData(65535, 0xFF, 0xFF)]
	public void Encode_WritesTheRepeatCountLittleEndian(int repeat, byte lo, byte hi)
	{
		var result = Ry5088MacroEncoder.Encode(Entry(repeat, new MacroEvent(0x05, true, 10)));

		Assert.Equal(lo, result.Blob[0]);
		Assert.Equal(hi, result.Blob[1]);
	}

	// 0 comes from an old client that predates the proto field; a blob repeat of 0 would be a
	// macro that plays nothing.
	[Theory]
	[InlineData(0)]
	[InlineData(-5)]
	[InlineData(70000)]
	public void Encode_ClampsTheRepeatCountInto1To65535(int repeat)
	{
		var result = Ry5088MacroEncoder.Encode(Entry(repeat, new MacroEvent(0x05, true, 10)));

		var value = result.Blob[0] | (result.Blob[1] << 8);
		Assert.InRange(value, 1, 65535);
	}

	// ── Delay encoding ───────────────────────────────────────────────────────────

	// 127 ms is the last inline value; 128 ms must take the extended form, where attr bits 0-6
	// stay zero and the next word is the delay uint16 LE. The delay under test rides on the
	// SECOND event and lands on the first action's word (the before→after shift).
	[Fact]
	public void Encode_InlineDelayBoundary_127StaysInline128GoesExtended()
	{
		var inline = Ry5088MacroEncoder.Encode(Entry(1,
			new MacroEvent(0x04, true, 10), new MacroEvent(0x04, false, 127)));
		Assert.Equal(new byte[] { 0x01, 0x00, 0x04, 0x80 | 127, 0x04, 10, 0x00, 0x00 }, inline.Blob);

		var extended = Ry5088MacroEncoder.Encode(Entry(1,
			new MacroEvent(0x04, true, 10), new MacroEvent(0x04, false, 128)));
		Assert.Equal(new byte[] { 0x01, 0x00, 0x04, 0x80, 128, 0x00, 0x04, 10, 0x00, 0x00 }, extended.Blob);
	}

	[Fact]
	public void Encode_LongDelay_TakesTheExtendedWordLittleEndian()
	{
		var result = Ry5088MacroEncoder.Encode(Entry(1,
			new MacroEvent(0x04, false, 10), new MacroEvent(0x04, true, 500)));

		// release A, attr 0x00 (break, no inline delay), then the next event's 500 ms
		// lead-in = 0x01F4 LE; press A closes with the minimum trailing delay.
		Assert.Equal(new byte[] { 0x01, 0x00, 0x04, 0x00, 0xF4, 0x01, 0x04, 0x80 | 10, 0x00, 0x00 }, result.Blob);
	}

	// The last action has no successor, so its trailing delay is always the floor; a
	// sub-floor gap between two events is raised to the floor on the first action's word.
	[Theory]
	[InlineData(0)]
	[InlineData(-1)]
	[InlineData(5)]
	public void Encode_ClampsShortDelaysUpToTheRecorderFloor(int delayMs)
	{
		var result = Ry5088MacroEncoder.Encode(Entry(1,
			new MacroEvent(0x04, true, 10), new MacroEvent(0x04, false, delayMs)));

		Assert.Equal(0x80 | Ry5088MacroEncoder.DelayMinMs, result.Blob[3]);
	}

	[Fact]
	public void Encode_ClampsLongDelaysDownToTheRecorderCap()
	{
		var result = Ry5088MacroEncoder.Encode(Entry(1,
			new MacroEvent(0x04, true, 10), new MacroEvent(0x04, false, 30000)));

		var delay = result.Blob[4] | (result.Blob[5] << 8);
		Assert.Equal(Ry5088MacroEncoder.DelayMaxMs, delay);
	}

	// ── Filtering ────────────────────────────────────────────────────────────────

	// On this family a code outside 0x04..0xEF is not merely unsupported: 0x00 IS the end
	// marker, 0xF9/0xFA are motion/jump commands, 0xF0..0xF8 are mouse codes. None of them may
	// reach the blob.
	[Theory]
	[InlineData(0x00)]
	[InlineData(0x03)]
	[InlineData(0xF0)]
	[InlineData(0xF4)]
	[InlineData(0xF9)]
	[InlineData(0xFA)]
	[InlineData(0xFF)]
	public void Encode_DropsCodesOutsideTheKeyboardRange(byte code)
	{
		var result = Ry5088MacroEncoder.Encode(Entry(1,
			new MacroEvent(code, true, 10),
			new MacroEvent(0x04, true, 10)));

		Assert.Equal(1, result.DroppedEvents);
		Assert.Equal(0x04, result.Blob[2]);
	}

	[Theory]
	[InlineData(0x04)]
	[InlineData(0xE0)]   // modifiers are ordinary usages on this family, not a bitmask
	[InlineData(0xEF)]
	public void Encode_KeepsTheWholeKeyboardRange(byte code)
	{
		var result = Ry5088MacroEncoder.Encode(Entry(1, new MacroEvent(code, true, 10)));

		Assert.Equal(0, result.DroppedEvents);
		Assert.Equal(code, result.Blob[2]);
	}

	[Fact]
	public void Encode_TruncatesAtTheEventCapAndSaysSo()
	{
		var events = Enumerable.Range(0, Ry5088MacroEncoder.MaxEventsPerMacro + 5)
			.Select(_ => new MacroEvent(0x04, true, 10))
			.ToArray();

		var result = Ry5088MacroEncoder.Encode(Entry(1, events));

		Assert.True(result.Truncated);
		// repeat header + capped events (all inline) + terminator
		Assert.Equal(2 + Ry5088MacroEncoder.MaxEventsPerMacro * 2 + 2, result.Blob.Length);
	}

	[Fact]
	public void Encode_ExactlyAtTheCapIsNotReportedTruncated()
	{
		var events = Enumerable.Range(0, Ry5088MacroEncoder.MaxEventsPerMacro)
			.Select(_ => new MacroEvent(0x04, true, 10))
			.ToArray();

		Assert.False(Ry5088MacroEncoder.Encode(Entry(1, events)).Truncated);
	}

	// ── Sizing ───────────────────────────────────────────────────────────────────

	// The event cap is what guarantees a recorder-legal macro always fits the 256-byte slot:
	// worst case is every action but the last in the extended form (the last action's trailing
	// delay is always the inline floor), 2 + 59*4 + 2 + 2 = 242.
	[Fact]
	public void Encode_WorstCaseLegalMacro_FitsTheDeviceSlot()
	{
		var events = Enumerable.Range(0, Ry5088MacroEncoder.MaxEventsPerMacro)
			.Select(_ => new MacroEvent(0x04, true, Ry5088MacroEncoder.DelayMaxMs))
			.ToArray();

		var result = Ry5088MacroEncoder.Encode(Entry(1, events));

		Assert.Equal(242, result.Blob.Length);
		Assert.True(result.Blob.Length <= Ry5088Protocol.MacroBytes);
	}

	[Fact]
	public void Encode_EndsEveryBlobWithTheTerminator()
	{
		var result = Ry5088MacroEncoder.Encode(Entry(2,
			new MacroEvent(0x04, true, 10),
			new MacroEvent(0x04, false, 500)));

		Assert.Equal(0x00, result.Blob[^2]);
		Assert.Equal(0x00, result.Blob[^1]);
	}

	// ── Empty blobs ──────────────────────────────────────────────────────────────

	[Fact]
	public void Encode_NoEncodableEvents_YieldsTheEmptyBlob()
	{
		var result = Ry5088MacroEncoder.Encode(Entry(3, new MacroEvent(0xF0, true, 10)));

		Assert.Equal(Ry5088MacroEncoder.EmptyBlob(), result.Blob);
		Assert.Equal(1, result.DroppedEvents);
	}

	[Fact]
	public void EmptyBlob_IsRepeatOnePlusTerminator()
	{
		Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x00 }, Ry5088MacroEncoder.EmptyBlob());
	}

	[Fact]
	public void Encode_EmptyEntry_YieldsTheEmptyBlob()
	{
		var result = Ry5088MacroEncoder.Encode(new MacroLibraryEntry(0, MacroRepeatMode.PlayCount, new List<MacroEvent>()));

		Assert.Equal(Ry5088MacroEncoder.EmptyBlob(), result.Blob);
		Assert.False(result.HasDiscards);
	}
}
