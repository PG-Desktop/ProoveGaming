using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// The one part of SmartInfiniteMacroService's behaviour that can be asserted without hardware.
// The service itself takes a concrete HidSharp.HidDevice and opens a concrete HidStream, neither
// substitutable, so the verdict logic was extracted here rather than left untestable inside it.
//
// What it decides matters: "the device kept nothing" and "the device kept the wrong thing" send the
// next investigator to opposite places — a firmware question versus an addressing question — and
// on Cyrex firmware 0x0002 this is what established that the macro write is acked and discarded.
public class MacroLibraryVerificationTests
{
	private static byte[] Library() => Convert.FromHexString(
		"AA551E00010000000A00040000000A00178150001701DC00178164001701");

	[Fact]
	public void IdenticalReadBack_IsAMatch()
	{
		var sent = Library();

		var verdict = MacroLibraryVerification.Classify(sent, sent);

		Assert.Equal(MacroVerifyOutcome.Match, verdict.Outcome);
		Assert.Equal(-1, verdict.FirstDivergence);
	}

	// The observed Cyrex result: the write is acked packet by packet and the region stays empty.
	[Fact]
	public void AllZeroReadBack_IsReportedAsDiscarded()
	{
		var sent = Library();

		var verdict = MacroLibraryVerification.Classify(sent, new byte[sent.Length]);

		Assert.Equal(MacroVerifyOutcome.AllZero, verdict.Outcome);
		Assert.Equal(0, verdict.FirstDivergence);
	}

	// A single non-zero byte is enough to mean something was stored, which is a different problem
	// from nothing being stored — so it must not be lumped in with the all-zero case.
	[Fact]
	public void OneNonZeroByte_IsDivergentNotAllZero()
	{
		var sent = Library();
		var readBack = new byte[sent.Length];
		readBack[^1] = 0x01;

		var verdict = MacroLibraryVerification.Classify(sent, readBack);

		Assert.Equal(MacroVerifyOutcome.Divergent, verdict.Outcome);
	}

	// The index is what tells the reader where to look, so it has to point at the real first
	// difference rather than at the start of the buffer.
	[Fact]
	public void Divergence_ReportsTheFirstDifferingByte()
	{
		var sent = Library();
		var readBack = (byte[])sent.Clone();
		readBack[12] ^= 0xFF;

		var verdict = MacroLibraryVerification.Classify(sent, readBack);

		Assert.Equal(MacroVerifyOutcome.Divergent, verdict.Outcome);
		Assert.Equal(12, verdict.FirstDivergence);
	}

	// A truncated reply is still "nothing came back", not a partial match to be puzzled over.
	[Fact]
	public void EmptyReadBack_CountsAsAllZero()
	{
		var verdict = MacroLibraryVerification.Classify(Library(), []);

		Assert.Equal(MacroVerifyOutcome.AllZero, verdict.Outcome);
		Assert.Equal(0, verdict.FirstDivergence);
	}

	// A read-back that matches as far as it goes but stops short has diverged at the cut-off, and
	// the scan must not walk past the shorter buffer.
	[Fact]
	public void ShortReadBack_DivergesAtTheTruncationPoint()
	{
		var sent = Library();
		var readBack = sent[..10];

		var verdict = MacroLibraryVerification.Classify(sent, readBack);

		Assert.Equal(MacroVerifyOutcome.Divergent, verdict.Outcome);
		Assert.Equal(10, verdict.FirstDivergence);
	}
}
