using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// The hot-path decision seam of the WinRT advertisement watcher. The central contract is the
// bug's contract: a foreign advertisement (an address we do not track) must resolve to NOTHING —
// on the pre-fix code every advertisement resolved a native device inside InTheHand, which is the
// unbounded memory climb this seam exists to forbid. The LocalName fallback only ever runs for a
// tracked device whose Id failed to parse as an address. See docs/plans/fix-devicehost-ble-memory-leak/.
public class BleAdvertisementMatcherTests
{
	private const ulong RampageAddress = 0xE0F1A2B3C4D5UL;
	private const string RampageDeviceId = "E0F1A2B3C4D5";

	private static BleAdvertisementMatcher AddressOnlyMatcher() => new(
		[new KeyValuePair<ulong, string>(RampageAddress, RampageDeviceId)],
		[]);

	[Fact]
	public void TryMatch_TrackedAddress_YieldsItsDeviceId()
	{
		var matcher = AddressOnlyMatcher();

		var matched = matcher.TryMatch(RampageAddress, localName: null, out var deviceId);

		Assert.True(matched);
		Assert.Equal(RampageDeviceId, deviceId);
	}

	[Fact]
	public void TryMatch_UnknownAddress_DoesNotMatch()
	{
		// The direct expression of the leak's contract: an advertisement from a device we do not
		// track (a passing phone) resolves to nothing.
		var matcher = AddressOnlyMatcher();

		var matched = matcher.TryMatch(0x123456789ABCUL, localName: "Someone's Phone", out var deviceId);

		Assert.False(matched);
		Assert.Equal(string.Empty, deviceId);
	}

	[Fact]
	public void TryMatch_EmptySnapshot_MatchesNothing()
	{
		var matcher = new BleAdvertisementMatcher([], []);

		Assert.False(matcher.TryMatch(RampageAddress, localName: "PG RAMPAGE", out _));
	}

	[Fact]
	public void TryMatch_LocalNameFallback_MatchesUnparseableTrackedDevice()
	{
		// A tracked id that failed to parse has only a name-fallback entry; a foreign address
		// carrying its catalog name in the advertisement must still wake the reconnect.
		var matcher = new BleAdvertisementMatcher([], [("RAMPAGE", "weird-id")]);

		var matched = matcher.TryMatch(0x123456789ABCUL, localName: "pg Rampage keyboard", out var deviceId);

		Assert.True(matched);
		Assert.Equal("weird-id", deviceId);
	}

	[Fact]
	public void TryMatch_LocalNameFallback_IgnoredWhenNameDoesNotContainCatalogName()
	{
		var matcher = new BleAdvertisementMatcher([], [("RAMPAGE", "weird-id")]);

		Assert.False(matcher.TryMatch(0x123456789ABCUL, localName: "Someone's Phone", out _));
	}

	[Fact]
	public void TryMatch_LocalNameFallback_IgnoredWhenLocalNameIsMissing()
	{
		// Most advertisement frames carry no LocalName section at all.
		var matcher = new BleAdvertisementMatcher([], [("RAMPAGE", "weird-id")]);

		Assert.False(matcher.TryMatch(0x123456789ABCUL, localName: null, out _));
		Assert.False(matcher.TryMatch(0x123456789ABCUL, localName: string.Empty, out _));
	}

	[Fact]
	public void TryMatch_NoFallbackEntries_LocalNameIsNeverConsulted()
	{
		// The normal case: every tracked id parsed, so an advertisement naming a tracked device
		// but coming from an unknown address must NOT match — address is the only identity.
		var matcher = AddressOnlyMatcher();

		Assert.False(matcher.TryMatch(0x123456789ABCUL, localName: "PG RAMPAGE", out _));
	}

	[Fact]
	public void Constructor_BlankFallbackNames_AreDroppedSoTheyNeverMatchEverything()
	{
		// A blank catalog name would Contains-match every advertised name; the constructor must
		// drop such entries outright.
		var matcher = new BleAdvertisementMatcher([], [("", "empty-id"), ("   ", "blank-id")]);

		Assert.False(matcher.TryMatch(0x123456789ABCUL, localName: "Someone's Phone", out _));
	}

	[Fact]
	public void TryMatch_AddressWins_OverNameFallback()
	{
		var matcher = new BleAdvertisementMatcher(
			[new KeyValuePair<ulong, string>(RampageAddress, RampageDeviceId)],
			[("RAMPAGE", "fallback-id")]);

		var matched = matcher.TryMatch(RampageAddress, localName: "PG RAMPAGE", out var deviceId);

		Assert.True(matched);
		Assert.Equal(RampageDeviceId, deviceId);
	}

	[Fact]
	public void HasNameFallbacks_AddressOnlySnapshot_IsFalse()
	{
		// The normal case (every tracked id parsed as an address): the watcher's Received handler
		// checks this BEFORE touching args.Advertisement, so the per-advertisement WinRT
		// projection + LocalName string marshal never happens for foreign advertisements.
		Assert.False(AddressOnlyMatcher().HasNameFallbacks);
		Assert.False(new BleAdvertisementMatcher([], []).HasNameFallbacks);
	}

	[Fact]
	public void HasNameFallbacks_WithFallbackEntry_IsTrue()
	{
		var matcher = new BleAdvertisementMatcher([], [("RAMPAGE", "weird-id")]);

		Assert.True(matcher.HasNameFallbacks);
	}

	[Fact]
	public void HasNameFallbacks_BlankFallbackNames_DoNotCount()
	{
		// Blank names are dropped at construction (they could never match anyway), so a snapshot
		// holding only those must report no fallbacks — otherwise the handler would marshal
		// LocalName on every advertisement for entries that cannot match anything.
		var matcher = new BleAdvertisementMatcher([], [("", "empty-id"), ("   ", "blank-id")]);

		Assert.False(matcher.HasNameFallbacks);
	}
}
