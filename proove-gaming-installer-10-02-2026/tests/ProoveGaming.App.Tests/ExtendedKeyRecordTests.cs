using ProoveGaming.App.Models;
using Xunit;

namespace ProoveGaming.App.Tests;

// The App's Extended Keys model (docs/plans/add-cyrex-extended-keys, docs/plans/add-extended-key-socd,
// docs/plans/add-extended-key-toggle): the canonical pair form every write path funnels through, the
// one field that must travel with the slot swap — an SOCD absolute priority names a record position,
// not a physical key — and the single-key shape the Toggle factory produces.
public class ExtendedKeyRecordTests
{
	private static ExtendedKeyRecord Socd(int slot1, int slot2, int priority) =>
		new(ExtendedKeyMode.Socd, slot1, slot2, priority, 0, ExtendedKeyCodeRecord.None, ExtendedKeyCodeRecord.None);

	[Theory]
	[InlineData(SocdPriority.Key1, SocdPriority.Key2)]
	[InlineData(SocdPriority.Key2, SocdPriority.Key1)]
	[InlineData(SocdPriority.LastInput, SocdPriority.LastInput)]
	[InlineData(SocdPriority.MutualCancel, SocdPriority.MutualCancel)]
	public void Normalized_ReversedSocdPair_SwapsTheSlots_AndFlipsOnlyAnAbsolutePriority(int picked, int stored)
	{
		var normalized = Socd(19, 18, picked).Normalized();

		Assert.Equal(18, normalized.Slot1);
		Assert.Equal(19, normalized.Slot2);
		Assert.Equal(stored, normalized.Priority);
	}

	[Theory]
	[InlineData(SocdPriority.Key1)]
	[InlineData(SocdPriority.Key2)]
	public void Normalized_OrderedSocdPair_IsUnchanged(int priority)
	{
		var record = Socd(18, 19, priority);

		Assert.Equal(record, record.Normalized());
	}

	[Fact]
	public void Normalized_RapidSnap_SwapsSlotsAndLeavesPriorityAlone()
	{
		var normalized = new ExtendedKeyRecord(ExtendedKeyMode.RapidSnap, 33, 31, 0, 0, ExtendedKeyCodeRecord.None, ExtendedKeyCodeRecord.None).Normalized();

		Assert.Equal(ExtendedKeyRecord.RapidSnap(31, 33), normalized);
		Assert.Equal(0, normalized.Priority);
	}

	// The factory takes the priority in the caller's Key 1 / Key 2 terms: "Key 1 = R wins" picked
	// as (R, E) and "Key 2 = R wins" picked as (E, R) are the same record.
	[Fact]
	public void SocdFactory_PriorityRefersToTheKeysAsPassed()
	{
		Assert.Equal(ExtendedKeyRecord.Socd(18, 19, SocdPriority.Key2), ExtendedKeyRecord.Socd(19, 18, SocdPriority.Key1));
		Assert.Equal(ExtendedKeyRecord.Socd(18, 19, SocdPriority.LastInput), ExtendedKeyRecord.Socd(19, 18, SocdPriority.LastInput));
	}

	[Fact]
	public void SocdWinningSlot_NamesTheFavouredSlot_OrNoSlot()
	{
		Assert.Equal(18, Socd(18, 19, SocdPriority.Key1).SocdWinningSlot);
		Assert.Equal(19, Socd(18, 19, SocdPriority.Key2).SocdWinningSlot);
		Assert.Equal(ExtendedKeyRecord.NoSlot, Socd(18, 19, SocdPriority.LastInput).SocdWinningSlot);
		Assert.Equal(ExtendedKeyRecord.NoSlot, Socd(18, 19, SocdPriority.MutualCancel).SocdWinningSlot);
		Assert.Equal(ExtendedKeyRecord.NoSlot, ExtendedKeyRecord.RapidSnap(18, 19).SocdWinningSlot);
	}

	[Fact]
	public void SocdPriority_DefinesExactlyTheFourFirmwareValues()
	{
		Assert.True(SocdPriority.IsDefined(0));
		Assert.True(SocdPriority.IsDefined(3));
		Assert.False(SocdPriority.IsDefined(-1));
		Assert.False(SocdPriority.IsDefined(4));
		Assert.False(SocdPriority.IsAbsolute(SocdPriority.LastInput));
		Assert.False(SocdPriority.IsAbsolute(SocdPriority.MutualCancel));
		Assert.True(SocdPriority.IsAbsolute(SocdPriority.Key1));
		Assert.True(SocdPriority.IsAbsolute(SocdPriority.Key2));
	}

	// NormalizeSet orders by mode first, so SOCD (0x83) rows precede Rapid Snap (0x84) rows whatever
	// order the caller had them in — the shape both the device-confirmed set and the stored rows share.
	[Fact]
	public void NormalizeSet_OrdersSocdBeforeRapidSnap_AndNormalisesEachPair()
	{
		var set = ExtendedKeyRecord.NormalizeSet(
		[
			ExtendedKeyRecord.RapidSnap(31, 33),
			Socd(41, 40, SocdPriority.Key1),
			Socd(18, 19, SocdPriority.LastInput),
		]);

		Assert.Equal(
		[
			Socd(18, 19, SocdPriority.LastInput),
			Socd(40, 41, SocdPriority.Key2),
			ExtendedKeyRecord.RapidSnap(31, 33),
		], set);
	}

	// ── Toggle (docs/plans/add-extended-key-toggle) ───────────────────────────

	// The toggled output rides HoldKey; there is no second slot, no priority and no tap — and being
	// single-key, the record has no partner and normalisation leaves it alone.
	[Fact]
	public void ToggleFactory_ProducesASingleKeyRecord_WithTheOutputInHoldKey()
	{
		var w = new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A);
		var record = ExtendedKeyRecord.Toggle(20, 100, w);

		Assert.Equal(new ExtendedKeyRecord(ExtendedKeyMode.Toggle, 20, ExtendedKeyRecord.NoSlot, 0, 100, w, ExtendedKeyCodeRecord.None), record);
		Assert.False(record.IsPair);
		Assert.True(ExtendedKeyMode.IsSingleKey(record.Mode));
		Assert.False(ExtendedKeyMode.IsPair(record.Mode));
		Assert.True(record.ContainsSlot(20));
		Assert.False(record.ContainsSlot(ExtendedKeyRecord.NoSlot));
		Assert.Equal(ExtendedKeyRecord.NoSlot, record.PartnerOf(20));
		Assert.Equal(ExtendedKeyRecord.NoSlot, record.SocdWinningSlot);
		Assert.Equal(record, record.Normalized());
		Assert.Equal(6, ExtendedKeyRecord.ToggleRecordBytes);
	}

	// Mode order is the TYPE byte order: MT (0x81), TGL (0x82), SOCD (0x83), RS (0x84); within a mode
	// by slot1 — the shape a device-confirmed set and the stored rows are compared in.
	[Fact]
	public void NormalizeSet_OrdersToggleBetweenModTapAndSocd()
	{
		var w = new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A);
		var toggleT = ExtendedKeyRecord.Toggle(20, 100, w);
		var toggleQ = ExtendedKeyRecord.Toggle(16, 500, w);
		var modTap = ExtendedKeyRecord.ModTap(30, 150, new ExtendedKeyCodeRecord(0x10, 0x01, 0x00), new ExtendedKeyCodeRecord(0x10, 0x00, 0x29));

		var set = ExtendedKeyRecord.NormalizeSet([ExtendedKeyRecord.RapidSnap(31, 33), toggleT, Socd(18, 19, SocdPriority.LastInput), toggleQ, modTap]);

		Assert.Equal([modTap, toggleQ, toggleT, Socd(18, 19, SocdPriority.LastInput), ExtendedKeyRecord.RapidSnap(31, 33)], set);
	}
}
