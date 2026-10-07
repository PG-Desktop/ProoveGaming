using System;
using System.Linq;
using ProoveGaming.App.Models.Keyboards;
using Xunit;

namespace ProoveGaming.App.Tests;

// The Cyrex catalog was generated from the device's own factory table rather than transcribed, and
// this is the test that keeps it honest: every entry is checked against those same bytes. A wrong
// matrix index silently remaps the wrong physical key and a wrong HID code silently assigns the
// wrong target — neither is visible without hardware.
public class CyrexKeyCatalogTests
{
	// Read from a real Cyrex over the vendor interface with command 0x16 (factory key matrix).
	// 75 slots x 3 bytes = 225.
	private const string FactoryTableHex =
		"10002910001E10001F10002010002110002210002310002410002510002610002710002D10002E10002A10004C" +
		"10002B10001410001A10000810001510001710001C10001810000C10001210001310002F10003010003110004B" +
		"10003910000410001610000710000910000A10000B10000D10000E10000F10003310003410000010002810004E" +
		"10020010000010001D10001B10000610001910000510001110001010003610003710003810200010005210004D" +
		"10010010080010040010000010000010000010002C10000010000010400060010010100010005010005110004F";

	private static byte[] Table => Convert.FromHexString(FactoryTableHex);

	// Slots the board does not use: a keyboard record naming no key, {0x10, 0x00, 0x00}.
	private static readonly int[] UnusedSlots = [42, 46, 63, 64, 65, 67, 68];

	private const int FnSlot = 70;
	private const int DeviceReportedKeyCount = 68;

	[Fact]
	public void FactoryTable_IsExactlyTheDeclaredMatrixSize()
	{
		Assert.Equal(CyrexKeyCatalog.MatrixSlots * 3, Table.Length);
		Assert.Equal(225, Table.Length);
	}

	// The device reports 68 keys in Device Info byte 6. The table must corroborate that independently:
	// 75 slots minus the 7 unused placeholders. This is what proved our decoding of that byte correct
	// and Rampage's zero a genuine anomaly rather than a misread.
	[Fact]
	public void RealKeyCount_MatchesWhatTheFirmwareReports()
	{
		int real = 0;
		for (int slot = 0; slot < CyrexKeyCatalog.MatrixSlots; slot++)
		{
			int o = slot * 3;
			bool unused = Table[o] == 0x10 && Table[o + 1] == 0x00 && Table[o + 2] == 0x00;
			if (!unused) real++;
		}

		Assert.Equal(DeviceReportedKeyCount, real);
	}

	[Fact]
	public void Keys_ContainsEveryRealKeyExceptFn()
	{
		Assert.Equal(DeviceReportedKeyCount - 1, CyrexKeyCatalog.Keys.Count);
		Assert.Equal(67, CyrexKeyCatalog.Keys.Count);
	}

	[Fact]
	public void Keys_HaveUniqueIndicesAndNamesWithinTheMatrix()
	{
		var indices = CyrexKeyCatalog.Keys.Select(k => k.MatrixIndex).ToList();
		var names = CyrexKeyCatalog.Keys.Select(k => k.DevKeyName).ToList();

		Assert.Equal(indices.Count, indices.Distinct().Count());
		Assert.Equal(names.Count, names.Distinct().Count());
		Assert.All(indices, i => Assert.InRange(i, (byte)0, (byte)(CyrexKeyCatalog.MatrixSlots - 1)));
	}

	[Fact]
	public void Keys_OmitUnusedSlotsAndFn()
	{
		var present = CyrexKeyCatalog.Keys.Select(k => (int)k.MatrixIndex).ToHashSet();

		foreach (var unused in UnusedSlots)
			Assert.DoesNotContain(unused, present);

		// Remapping FN would break every FN+X hardware shortcut.
		Assert.DoesNotContain(FnSlot, present);
		Assert.Equal(FnSlot, CyrexKeyCatalog.FnMatrixIndex);
	}

	// The test that actually catches a mistake: every catalog row must match the device byte for byte
	// at its own matrix index.
	[Fact]
	public void Keys_MatchTheDeviceFactoryTableExactly()
	{
		var table = Table;

		foreach (var key in CyrexKeyCatalog.Keys)
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

	// Modifier keys carry a 0x00 usage code, so identity is the (mask, usage) pair. A shared pair
	// would make the target-list highlight ambiguous and pick the wrong row.
	[Fact]
	public void Keys_HaveUniqueModifierAndUsagePairs()
	{
		var pairs = CyrexKeyCatalog.Keys.Select(k => (k.ModifierMask, k.HidUsageCode)).ToList();

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
			var key = CyrexKeyCatalog.Keys.Single(k => k.DevKeyName == name);
			Assert.Equal(mask, key.ModifierMask);
			Assert.Equal(0x00, key.HidUsageCode);
		}
	}

	[Fact]
	public void OrdinaryKeys_CarryAUsageCodeAndNoMask()
	{
		Assert.All(CyrexKeyCatalog.Keys.Where(k => k.ModifierMask == 0),
			k => Assert.NotEqual(0x00, k.HidUsageCode));
	}

	// Cyrex is a 65% board — there must be no function-row keys in the catalog.
	[Fact]
	public void Keys_ContainNoFunctionRow()
	{
		Assert.DoesNotContain(CyrexKeyCatalog.Keys, k => k.HidUsageCode is >= 0x3A and <= 0x45);
	}

	[Fact]
	public void EveryKeyHasVisualBounds()
	{
		foreach (var key in CyrexKeyCatalog.Keys)
			Assert.True(CyrexVisualLayout.Layout.ContainsKey(key.DevKeyName),
				$"{key.DevKeyName} has no entry in CyrexVisualLayout.");
	}

	[Fact]
	public void VisualLayoutHasNoOrphanedEntries()
	{
		var names = CyrexKeyCatalog.Keys.Select(k => k.DevKeyName).ToHashSet();

		foreach (var name in CyrexVisualLayout.Layout.Keys)
			Assert.True(names.Contains(name), $"{name} is in the layout but not in the catalog.");
	}

	[Fact]
	public void EveryKeyFitsInsideTheCanvas()
	{
		foreach (var (name, b) in CyrexVisualLayout.Layout)
		{
			Assert.True(b.X >= 0 && b.Y >= 0, $"{name} has a negative origin.");
			Assert.True(b.Width > 0 && b.Height > 0, $"{name} has a non-positive size.");
			Assert.True(b.X + b.Width <= CyrexVisualLayout.CanvasWidth, $"{name} overflows the canvas width.");
			Assert.True(b.Y + b.Height <= CyrexVisualLayout.CanvasHeight, $"{name} overflows the canvas height.");
		}
	}

	[Fact]
	public void KeysDoNotOverlapVisually()
	{
		var entries = CyrexVisualLayout.Layout.ToList();

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

	[Fact]
	public void IsCyrex_MatchesTheVendorIdentifiers()
	{
		Assert.True(CyrexKeyCatalog.IsCyrex(10473, 12914));
		Assert.False(CyrexKeyCatalog.IsCyrex(10473, 12888));  // Rampage
		Assert.False(CyrexKeyCatalog.IsCyrex(10473, 12910));  // Aress
		Assert.True(CyrexKeyCatalog.IsCyrex("Cyrex"));
		Assert.False(CyrexKeyCatalog.IsCyrex("Rampage"));
		Assert.False(CyrexKeyCatalog.IsCyrex((string?)null));
	}
}
