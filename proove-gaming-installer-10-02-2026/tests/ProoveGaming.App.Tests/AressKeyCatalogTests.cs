using System;
using System.Linq;
using ProoveGaming.App.Models.Keyboards;
using Xunit;

namespace ProoveGaming.App.Tests;

// The Aress catalog was generated from the device's own factory table rather than transcribed, and
// this is the test that keeps it honest. It also pins the relationship to Cyrex: the two are the same
// physical board and their tables differ in exactly three navigation slots, so a change that makes
// them diverge anywhere else is a mistake in one of the generators.
public class AressKeyCatalogTests
{
	// Read from a real Aress over the vendor interface with command 0x16. 75 slots x 3 = 225 bytes.
	private const string FactoryTableHex =
		"10002910001E10001F10002010002110002210002310002410002510002610002710002D10002E10002A100049" +
		"10002B10001410001A10000810001510001710001C10001810000C10001210001310002F10003010003110004B" +
		"10003910000410001610000710000910000A10000B10000D10000E10000F10003310003410000010002810004C" +
		"10020010000010001D10001B10000610001910000510001110001010003610003710003810200010005210004E" +
		"10010010080010040010000010000010000010002C10000010000010400060010010100010005010005110004F";

	private static byte[] Table => Convert.FromHexString(FactoryTableHex);

	private static readonly int[] UnusedSlots = [42, 46, 63, 64, 65, 67, 68];

	private const int FnSlot = 70;
	private const int DeviceReportedKeyCount = 68;

	[Fact]
	public void FactoryTable_IsExactlyTheDeclaredMatrixSize()
	{
		Assert.Equal(AressKeyCatalog.MatrixSlots * 3, Table.Length);
		Assert.Equal(225, Table.Length);
	}

	// The device reports 68 keys in Device Info byte 6; the table must corroborate that
	// independently — 75 slots minus the 7 unused placeholders.
	[Fact]
	public void RealKeyCount_MatchesWhatTheFirmwareReports()
	{
		var table = Table;
		int real = 0;
		for (int slot = 0; slot < AressKeyCatalog.MatrixSlots; slot++)
		{
			int o = slot * 3;
			bool unused = table[o] == 0x10 && table[o + 1] == 0x00 && table[o + 2] == 0x00;
			if (!unused) real++;
		}

		Assert.Equal(DeviceReportedKeyCount, real);
	}

	[Fact]
	public void Keys_ContainsEveryRealKeyExceptFn()
	{
		Assert.Equal(DeviceReportedKeyCount - 1, AressKeyCatalog.Keys.Count);
		Assert.Equal(67, AressKeyCatalog.Keys.Count);
	}

	[Fact]
	public void Keys_HaveUniqueIndicesAndNamesWithinTheMatrix()
	{
		var indices = AressKeyCatalog.Keys.Select(k => k.MatrixIndex).ToList();
		var names = AressKeyCatalog.Keys.Select(k => k.DevKeyName).ToList();

		Assert.Equal(indices.Count, indices.Distinct().Count());
		Assert.Equal(names.Count, names.Distinct().Count());
		Assert.All(indices, i => Assert.InRange(i, (byte)0, (byte)(AressKeyCatalog.MatrixSlots - 1)));
	}

	[Fact]
	public void Keys_OmitUnusedSlotsAndFn()
	{
		var present = AressKeyCatalog.Keys.Select(k => (int)k.MatrixIndex).ToHashSet();

		foreach (var unused in UnusedSlots)
			Assert.DoesNotContain(unused, present);

		Assert.DoesNotContain(FnSlot, present);
		Assert.Equal(FnSlot, AressKeyCatalog.FnMatrixIndex);
	}

	// The test that actually catches a mistake.
	[Fact]
	public void Keys_MatchTheDeviceFactoryTableExactly()
	{
		var table = Table;

		foreach (var key in AressKeyCatalog.Keys)
		{
			int o = key.MatrixIndex * 3;
			byte type = table[o], code1 = table[o + 1], code2 = table[o + 2];

			Assert.True(type == 0x10,
				$"{key.DevKeyName} (slot {key.MatrixIndex}): device TYPE is 0x{type:X2}, expected 0x10");
			Assert.True(key.ModifierMask == code1,
				$"{key.DevKeyName} (slot {key.MatrixIndex}): mask 0x{key.ModifierMask:X2} != device 0x{code1:X2}");
			Assert.True(key.HidUsageCode == code2,
				$"{key.DevKeyName} (slot {key.MatrixIndex}): HID 0x{key.HidUsageCode:X2} != device 0x{code2:X2}");
		}
	}

	// Aress's navigation column is what distinguishes it from Cyrex.
	[Theory]
	[InlineData(14, "Insert", 0x49)]
	[InlineData(29, "PageUp", 0x4B)]
	[InlineData(44, "Delete", 0x4C)]
	[InlineData(59, "PageDown", 0x4E)]
	[InlineData(74, "Right", 0x4F)]
	public void NavigationColumn_IsAressOwn(int slot, string expectedName, int expectedHid)
	{
		var key = AressKeyCatalog.Keys.Single(k => k.MatrixIndex == slot);

		Assert.Equal(expectedName, key.DevKeyName);
		Assert.Equal(expectedHid, key.HidUsageCode);
	}

	// The two boards are the same hardware. Their tables must differ in exactly the three navigation
	// slots and nowhere else — if a fourth difference ever appears, one of the catalogs is wrong.
	[Fact]
	public void DiffersFromCyrex_InExactlyThreeNavigationSlots()
	{
		var aress = AressKeyCatalog.Keys.ToDictionary(k => k.MatrixIndex);
		var cyrex = CyrexKeyCatalog.Keys.ToDictionary(k => k.MatrixIndex);

		Assert.Equal(cyrex.Keys.OrderBy(k => k), aress.Keys.OrderBy(k => k));

		var differing = aress.Keys
			.Where(slot => aress[slot].HidUsageCode != cyrex[slot].HidUsageCode ||
			               aress[slot].ModifierMask != cyrex[slot].ModifierMask)
			.OrderBy(s => s)
			.ToArray();

		Assert.Equal(new byte[] { 14, 44, 59 }, differing);
	}

	[Fact]
	public void Keys_HaveUniqueModifierAndUsagePairs()
	{
		var pairs = AressKeyCatalog.Keys.Select(k => (k.ModifierMask, k.HidUsageCode)).ToList();

		Assert.Equal(pairs.Count, pairs.Distinct().Count());
	}

	[Fact]
	public void ModifierKeys_CarryTheirMaskAndNoUsageCode()
	{
		var expected = new (string Name, byte Mask)[]
		{
			("LeftCtrl", 0x01), ("LeftShift", 0x02), ("LeftAlt", 0x04), ("LeftWin", 0x08),
			("RightCtrl", 0x10), ("RightShift", 0x20), ("RightAlt", 0x40),
		};

		foreach (var (name, mask) in expected)
		{
			var key = AressKeyCatalog.Keys.Single(k => k.DevKeyName == name);
			Assert.Equal(mask, key.ModifierMask);
			Assert.Equal(0x00, key.HidUsageCode);
		}
	}

	[Fact]
	public void Keys_ContainNoFunctionRow()
	{
		Assert.DoesNotContain(AressKeyCatalog.Keys, k => k.HidUsageCode is >= 0x3A and <= 0x45);
	}

	[Fact]
	public void EveryKeyHasVisualBounds()
	{
		foreach (var key in AressKeyCatalog.Keys)
			Assert.True(AressVisualLayout.Layout.ContainsKey(key.DevKeyName),
				$"{key.DevKeyName} has no entry in AressVisualLayout.");
	}

	[Fact]
	public void VisualLayoutHasNoOrphanedEntries()
	{
		var names = AressKeyCatalog.Keys.Select(k => k.DevKeyName).ToHashSet();

		foreach (var name in AressVisualLayout.Layout.Keys)
			Assert.True(names.Contains(name), $"{name} is in the layout but not in the catalog.");
	}

	[Fact]
	public void EveryKeyFitsInsideTheCanvas()
	{
		foreach (var (name, b) in AressVisualLayout.Layout)
		{
			Assert.True(b.X >= 0 && b.Y >= 0, $"{name} has a negative origin.");
			Assert.True(b.Width > 0 && b.Height > 0, $"{name} has a non-positive size.");
			Assert.True(b.X + b.Width <= AressVisualLayout.CanvasWidth, $"{name} overflows the canvas width.");
			Assert.True(b.Y + b.Height <= AressVisualLayout.CanvasHeight, $"{name} overflows the canvas height.");
		}
	}

	[Fact]
	public void KeysDoNotOverlapVisually()
	{
		var entries = AressVisualLayout.Layout.ToList();

		for (int i = 0; i < entries.Count; i++)
		for (int j = i + 1; j < entries.Count; j++)
		{
			var (nameA, a) = (entries[i].Key, entries[i].Value);
			var (nameB, b) = (entries[j].Key, entries[j].Value);

			bool overlaps = a.X < b.X + b.Width && b.X < a.X + a.Width &&
			                a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

			Assert.False(overlaps, $"{nameA} overlaps {nameB}.");
		}
	}

	// The shared geometry must cover every slot both catalogs use, or a key silently loses its
	// overlay position.
	[Fact]
	public void SharedGeometryCoversBothCatalogs()
	{
		foreach (var key in AressKeyCatalog.Keys.Concat(CyrexKeyCatalog.Keys))
			Assert.True(SmartInfinite65Geometry.BySlot.ContainsKey(key.MatrixIndex),
				$"slot {key.MatrixIndex} ({key.DevKeyName}) has no shared geometry.");
	}

	[Fact]
	public void IsAress_MatchesTheVendorIdentifiers()
	{
		Assert.True(AressKeyCatalog.IsAress(10473, 12910));
		Assert.False(AressKeyCatalog.IsAress(10473, 12914)); // Cyrex
		Assert.False(AressKeyCatalog.IsAress(10473, 12888)); // Rampage
		Assert.True(AressKeyCatalog.IsAress("Aress"));
		Assert.False(AressKeyCatalog.IsAress("Cyrex"));
		Assert.False(AressKeyCatalog.IsAress((string?)null));
	}
}
