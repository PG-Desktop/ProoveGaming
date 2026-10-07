using System.Linq;
using ProoveGaming.App.Models.Keyboards;
using Xunit;

namespace ProoveGaming.App.Tests;

// The T800 key catalog is generated from the device's own factory table
// (docs/plans/add-t800-key-assignments/decode-t800-matrix.py). These tests pin the properties a
// regeneration must preserve — a wrong matrix slot does not fail at runtime, it silently remaps the
// wrong key, and the only place that can be caught cheaply is here.
public class T800KeyCatalogTests
{
	[Fact]
	public void Catalog_HasEveryRemappableKey()
	{
		// 68 real keys in the factory table, minus FN.
		Assert.Equal(67, T800KeyCatalog.Keys.Count);
	}

	[Fact]
	public void DevKeyNames_AreUnique()
	{
		var duplicates = T800KeyCatalog.Keys
			.GroupBy(k => k.DevKeyName)
			.Where(g => g.Count() > 1)
			.Select(g => g.Key)
			.ToList();

		Assert.Empty(duplicates);
	}

	// Two keys sharing a slot means one of them silently overwrites the other on write.
	[Fact]
	public void MatrixSlots_AreUnique()
	{
		var duplicates = T800KeyCatalog.Keys
			.GroupBy(k => k.MatrixIndex)
			.Where(g => g.Count() > 1)
			.Select(g => g.Key)
			.ToList();

		Assert.Empty(duplicates);
	}

	[Fact]
	public void MatrixSlots_AreInsideTheMatrix()
	{
		Assert.All(T800KeyCatalog.Keys, k => Assert.InRange(k.MatrixIndex, 0, T800KeyCatalog.MatrixSlots - 1));
	}

	// FN is slot 65 and must stay out of the catalog: remapping it would break every FN+X hardware
	// shortcut on the board. Same reason Cyrex excludes its slot 70.
	[Fact]
	public void Fn_IsNotRemappable()
	{
		Assert.DoesNotContain(T800KeyCatalog.Keys, k => k.MatrixIndex == T800KeyCatalog.FnMatrixIndex);
	}

	// This board stores modifiers as ordinary HID usages 0xE0-0xE7, NOT as Cyrex's
	// modifier-bitmask-with-zero-usage form. If a regeneration ever starts populating ModifierMask,
	// the eight modifier keys stop matching anything the firmware reports.
	[Theory]
	[InlineData("LeftCtrl", 0xE0)]
	[InlineData("LeftShift", 0xE1)]
	[InlineData("LeftAlt", 0xE2)]
	[InlineData("LeftWin", 0xE3)]
	[InlineData("RightCtrl", 0xE4)]
	[InlineData("RightShift", 0xE5)]
	[InlineData("RightAlt", 0xE6)]
	public void Modifiers_CarryTheirOwnUsageCodeAndNoMask(string devKeyName, byte expectedUsage)
	{
		var key = T800KeyCatalog.Keys.Single(k => k.DevKeyName == devKeyName);

		Assert.Equal(expectedUsage, key.HidUsageCode);
		Assert.Equal(0, key.ModifierMask);
	}

	[Fact]
	public void NoKey_UsesAModifierMask()
	{
		Assert.All(T800KeyCatalog.Keys, k => Assert.Equal(0, k.ModifierMask));
	}

	// The matrix is column-major on a 6-row grid, and the resulting row counts are the cross-check
	// that verified the whole decode: they match the keycaps measured from the artwork exactly.
	// Row 0 is the function row this 65% board does not have.
	[Fact]
	public void RowStructure_MatchesTheArtwork()
	{
		var counts = T800KeyCatalog.Keys
			.GroupBy(k => k.MatrixIndex % 6)
			.OrderBy(g => g.Key)
			.ToDictionary(g => g.Key, g => g.Count());

		// Row 5 is 9 here rather than the table's 10 because FN sits in it and is excluded.
		Assert.Equal(new[] { 1, 2, 3, 4, 5 }, counts.Keys.ToArray());
		Assert.Equal(15, counts[1]);
		Assert.Equal(15, counts[2]);
		Assert.Equal(14, counts[3]);
		Assert.Equal(14, counts[4]);
		Assert.Equal(9, counts[5]);
	}

	[Fact]
	public void EveryKey_HasALabelAndCategory()
	{
		Assert.All(T800KeyCatalog.Keys, k =>
		{
			Assert.False(string.IsNullOrWhiteSpace(k.KeyLabel));
			Assert.False(string.IsNullOrWhiteSpace(k.Category));
		});
	}

	[Theory]
	[InlineData(12625, 20525, true)]
	[InlineData(10473, 12914, false)]
	public void IsT800_MatchesOnVendorAndProduct(int vid, int pid, bool expected)
	{
		Assert.Equal(expected, T800KeyCatalog.IsT800(vid, pid));
	}

	[Theory]
	[InlineData("T800", true)]
	[InlineData("t800", true)]
	[InlineData("Cyrex", false)]
	[InlineData(null, false)]
	public void IsT800_MatchesOnName(string? name, bool expected)
	{
		Assert.Equal(expected, T800KeyCatalog.IsT800(name));
	}
}
