using System;
using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// Regression cover for the "silent DPI change" defect: a MsConfig write (polling rate, lift-off)
// was committing an INFERRED active DPI stage to the device.
//
// The inference is TaipanMsConfig.LiveDpiStage's "+1", which is only correct when the FIRMWARE last
// wrote byte 0x11 (it lags one physical button press). When the app wrote the byte it is already
// exact, so advancing it overshoots and the next unrelated setting write moves the user's DPI.
//
// The contract these tests lock down: only a stage the host actually KNOWS — from the 0x49 push or
// from its own write — may reach the device. When nothing is known the block's own byte is left
// exactly as it was read, never replaced with a guess.
public class TaipanStageProvenanceTests
{
	// The vendor's worked full-write block, used as a realistic read-back baseline.
	private static byte[] VendorBlock() =>
	[
		0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x01, 0x03, 0x01,
		0xFF, 0x01, 0x02, 0x04, 0x08, 0x01, 0x00, 0x00, 0x00,
	];

	// ── ResolveStageForWrite ──────────────────────────────────────────────────

	// THE REPRODUCTION. Device is on stage 3 because the user picked it in the UI, so flash holds
	// exactly 3. Before the fix a re-subscribe inferred 4 and cached it, and this write committed 4 —
	// silently moving the mouse to 4800 DPI. A known stage must be written verbatim.
	[Fact]
	public void ResolveStageForWrite_WritesTheKnownStageVerbatim()
	{
		var block = VendorBlock();
		block[TaipanMsConfig.DpiSwitchOffset] = 3;

		TaipanMsConfig.ResolveStageForWrite(block, knownStage: 3);

		Assert.Equal(3, block[TaipanMsConfig.DpiSwitchOffset]);
	}

	// The other half of the fix: with no known stage there is nothing trustworthy to write, so the
	// byte that was read back is left untouched. Replacing it with LiveDpiStage's guess is exactly
	// what caused the defect.
	[Fact]
	public void ResolveStageForWrite_LeavesTheByteUntouchedWhenTheStageIsUnknown()
	{
		var block = VendorBlock();
		block[TaipanMsConfig.DpiSwitchOffset] = 2;

		TaipanMsConfig.ResolveStageForWrite(block, TaipanMsConfig.UnknownStage);

		Assert.Equal(2, block[TaipanMsConfig.DpiSwitchOffset]);
	}

	[Fact]
	public void ResolveStageForWrite_TouchesNothingButDpiSwitch()
	{
		var block = VendorBlock();
		var original = VendorBlock();

		TaipanMsConfig.ResolveStageForWrite(block, knownStage: 5);

		for (var i = 0; i < original.Length; i++)
		{
			if (i == TaipanMsConfig.DpiSwitchOffset) continue;
			Assert.Equal(original[i], block[i]);
		}
	}

	[Theory]
	[InlineData(99, 7)]
	[InlineData(-5, 0)]
	public void ResolveStageForWrite_ClampsAKnownStageToTheDeviceRange(int knownStage, byte expected)
	{
		var block = VendorBlock();

		TaipanMsConfig.ResolveStageForWrite(block, knownStage);

		Assert.Equal(expected, block[TaipanMsConfig.DpiSwitchOffset]);
	}

	// ── ResolveSourceBlock ────────────────────────────────────────────────────
	// The block a write is built from. Committing an erased sector resets every setting at once, so
	// an implausible read must fall back to the last good block rather than be patched and written.

	[Fact]
	public void ResolveSourceBlock_PrefersAPlausibleRead()
	{
		var read = VendorBlock();
		var cached = VendorBlock();
		cached[TaipanMsConfig.PollingRateOffset] = 0x08;

		Assert.Same(read, TaipanMsConfig.ResolveSourceBlock(read, cached));
	}

	[Fact]
	public void ResolveSourceBlock_FallsBackToTheLastCommittedBlockWhenTheReadIsErased()
	{
		var erased = new byte[TaipanMsConfig.Length];
		var cached = VendorBlock();

		Assert.Same(cached, TaipanMsConfig.ResolveSourceBlock(erased, cached));
	}

	[Fact]
	public void ResolveSourceBlock_FallsBackWhenThereWasNoReplyAtAll()
	{
		var cached = VendorBlock();

		Assert.Same(cached, TaipanMsConfig.ResolveSourceBlock(null, cached));
	}

	// Nothing usable and nothing cached: the caller must skip the write, never fabricate a block.
	[Fact]
	public void ResolveSourceBlock_ReturnsNullWhenNothingIsUsable()
	{
		Assert.Null(TaipanMsConfig.ResolveSourceBlock(null, null));
		Assert.Null(TaipanMsConfig.ResolveSourceBlock(new byte[TaipanMsConfig.Length], null));
	}

	// ── IsFactoryDefaultFallbackSafe ──────────────────────────────────────────
	// When ResolveSourceBlock returns null, the caller may fall back to FactoryDefaultBlock instead
	// of skipping the write outright (cold-start over the dongle, which never answers reads). That
	// block carries the VENDOR'S OWN example DPI-stage byte, which ResolveStageForWrite leaves
	// untouched whenever the live stage is unknown — so falling back is only safe when the write
	// itself is about to overwrite the stage, or the host already has a real stage to use instead.
	// This is the same "never commit a guessed stage" contract ResolveStageForWrite enforces above,
	// applied one level up to the decision of whether to use the fallback block at all.

	[Fact]
	public void IsFactoryDefaultFallbackSafe_SafeWhenThisWriteSetsTheStageItself()
	{
		// e.g. SetActiveDpiStageAsync — the patch callback overwrites DpiSwitch regardless of what
		// the fallback block started with, so an unknown live stage doesn't matter.
		Assert.True(TaipanMsConfig.IsFactoryDefaultFallbackSafe(patchSetsStage: true, liveDpiStage: TaipanMsConfig.UnknownStage));
	}

	[Fact]
	public void IsFactoryDefaultFallbackSafe_SafeWhenTheHostAlreadyKnowsTheLiveStage()
	{
		// e.g. a polling-rate change after the device has pushed a real stage (or the app asserted
		// its persisted one) — ResolveStageForWrite will write that known stage over the fallback's.
		Assert.True(TaipanMsConfig.IsFactoryDefaultFallbackSafe(patchSetsStage: false, liveDpiStage: 3));
	}

	[Fact]
	public void IsFactoryDefaultFallbackSafe_UnsafeWhenNeitherIsTrue()
	{
		// THE REPRODUCTION: a report-rate-only write, cold start, no known stage. Falling back here
		// would leave FactoryDefaultBlock's own example stage (0x00) in the write untouched —
		// silently moving the device's real DPI on an unrelated setting change. The caller must
		// skip the write instead.
		Assert.False(TaipanMsConfig.IsFactoryDefaultFallbackSafe(patchSetsStage: false, liveDpiStage: TaipanMsConfig.UnknownStage));
	}
}
