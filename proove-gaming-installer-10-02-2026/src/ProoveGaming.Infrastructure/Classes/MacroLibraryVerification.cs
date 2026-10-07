namespace ProoveGaming.Infrastructure.Classes;

/// <summary>What a read-back of the packed macro library says about the device.</summary>
public enum MacroVerifyOutcome
{
	/// <summary>Every byte matches: the device is holding what was sent.</summary>
	Match,

	/// <summary>
	/// The region is entirely zero. The write was acknowledged and discarded — the same behaviour
	/// Rampage shows for key-matrix writes. A firmware question, not a packet-format one.
	/// </summary>
	AllZero,

	/// <summary>
	/// The region holds something, but not what was sent: the write lands, though not where or how
	/// intended. <see cref="MacroVerifyResult.FirstDivergence"/> says where to start looking.
	/// </summary>
	Divergent,
}

/// <param name="Outcome">Which of the three cases this is.</param>
/// <param name="FirstDivergence">
/// Index of the first differing byte, or <c>-1</c> when <paramref name="Outcome"/> is
/// <see cref="MacroVerifyOutcome.Match"/>.
/// </param>
public readonly record struct MacroVerifyResult(MacroVerifyOutcome Outcome, int FirstDivergence);

/// <summary>
/// Compares a packed macro library against what the device reports holding.
/// </summary>
/// <remarks>
/// Pure, and deliberately separate from <c>SmartInfiniteMacroService</c>, which cannot be unit
/// tested because it takes a concrete <c>HidSharp.HidDevice</c>. This is the one part of that
/// service's behaviour worth asserting without hardware: distinguishing "the device kept nothing"
/// from "the device kept the wrong thing" is what turns a silent non-playing key into a specific
/// question, and it is the branch's headline finding on Cyrex firmware <c>0x0002</c>.
/// </remarks>
public static class MacroLibraryVerification
{
	public static MacroVerifyResult Classify(ReadOnlySpan<byte> sent, ReadOnlySpan<byte> readBack)
	{
		if (sent.SequenceEqual(readBack))
			return new MacroVerifyResult(MacroVerifyOutcome.Match, -1);

		var firstDiff = 0;
		var limit = Math.Min(sent.Length, readBack.Length);
		while (firstDiff < limit && sent[firstDiff] == readBack[firstDiff]) firstDiff++;

		// An empty read-back counts as all-zero: nothing came back and nothing is stored. Testing
		// IndexOfAnyExcept rather than trimming keeps this allocation-free and correct for length 0.
		var outcome = readBack.IndexOfAnyExcept((byte)0) < 0
			? MacroVerifyOutcome.AllZero
			: MacroVerifyOutcome.Divergent;

		return new MacroVerifyResult(outcome, firstDiff);
	}
}
