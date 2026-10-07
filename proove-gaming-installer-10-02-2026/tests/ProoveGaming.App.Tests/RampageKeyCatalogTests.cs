using System.Collections.Generic;
using System.Linq;
using ProoveGaming.App.Models.Keyboards;
using Xunit;

namespace ProoveGaming.App.Tests;

// The Rampage catalog is 86 rows hand-transcribed from the vendor's factory key_matrix_tab
// (docs/knowledge-base/rampage-keyboard-protocol/keyboard_user_flash.c). A single wrong matrix
// index silently remaps the wrong physical key, and a wrong HID code silently assigns the wrong
// target — neither is visible without hardware. FactoryTable below is an independent transcription
// of the same vendor file, so these tests are a genuine cross-check rather than a restatement of
// the catalog.
public class RampageKeyCatalogTests
{
	// Slots the vendor table leaves as {0x10, 0x00, 0x00} — real positions on the board with no
	// factory assignment.
	private static readonly HashSet<byte> EmptySlots = [60, 65, 77, 83, 84, 85, 87, 88];

	// Slot 79 is {0x60, 0x00, 0x00} (a blank custom-function slot); slot 90 is FN.
	private const byte BlankCustomSlot = 79;
	private const byte FnSlot = 90;

	[Fact]
	public void Keys_ContainsEightySixRemappableEntries()
	{
		Assert.Equal(86, RampageKeyCatalog.Keys.Count);
	}

	[Fact]
	public void Keys_HaveUniqueMatrixIndicesWithinTheVendorRange()
	{
		var indices = RampageKeyCatalog.Keys.Select(k => k.MatrixIndex).ToList();

		Assert.Equal(indices.Count, indices.Distinct().Count());
		Assert.All(indices, i => Assert.InRange(i, (byte)0, (byte)95));
	}

	[Fact]
	public void Keys_HaveUniqueDevKeyNames()
	{
		var names = RampageKeyCatalog.Keys.Select(k => k.DevKeyName).ToList();

		Assert.Equal(names.Count, names.Distinct().Count());
	}

	[Fact]
	public void Keys_OmitEmptySlotsTheBlankCustomSlotAndFn()
	{
		var present = RampageKeyCatalog.Keys.Select(k => k.MatrixIndex).ToHashSet();

		foreach (var empty in EmptySlots)
			Assert.DoesNotContain(empty, present);

		Assert.DoesNotContain(BlankCustomSlot, present);
		// Remapping FN would break every FN+X hardware shortcut.
		Assert.DoesNotContain(FnSlot, present);
	}

	[Fact]
	public void Keys_CoverEveryAssignableSlotInTheVendorTable()
	{
		var present = RampageKeyCatalog.Keys.Select(k => k.MatrixIndex).ToHashSet();

		var expected = FactoryTable
			.Where(kv => kv.Key != BlankCustomSlot && kv.Key != FnSlot && !EmptySlots.Contains(kv.Key))
			.Select(kv => kv.Key)
			.ToHashSet();

		Assert.Equal(expected.OrderBy(x => x), present.OrderBy(x => x));
	}

	// The test that actually catches a typo: every catalog row must match the vendor table byte
	// for byte at its own matrix index.
	[Fact]
	public void Keys_MatchTheVendorFactoryTableExactly()
	{
		foreach (var key in RampageKeyCatalog.Keys)
		{
			var (type, codeLow, codeHigh) = FactoryTable[key.MatrixIndex];

			Assert.Equal(0x10, type); // every remappable factory entry is a plain keyboard record
			Assert.True(key.ModifierMask == codeLow,
				$"{key.DevKeyName} (slot {key.MatrixIndex}): modifier mask 0x{key.ModifierMask:X2} != vendor 0x{codeLow:X2}");
			Assert.True(key.HidUsageCode == codeHigh,
				$"{key.DevKeyName} (slot {key.MatrixIndex}): HID code 0x{key.HidUsageCode:X2} != vendor 0x{codeHigh:X2}");
		}
	}

	// Modifier keys carry a 0x00 usage code, so identity is the (mask, usage) pair. If two rows
	// shared a pair the target-list highlight would be ambiguous and pick the wrong row.
	[Fact]
	public void Keys_HaveUniqueModifierAndUsageCodePairs()
	{
		var pairs = RampageKeyCatalog.Keys.Select(k => (k.ModifierMask, k.HidUsageCode)).ToList();

		Assert.Equal(pairs.Count, pairs.Distinct().Count());
	}

	[Fact]
	public void ModifierKeys_CarryTheirMaskAndNoUsageCode()
	{
		var modifiers = new Dictionary<string, byte>
		{
			["LeftCtrl"] = 0x01,
			["LeftShift"] = 0x02,
			["LeftAlt"] = 0x04,
			["LeftWin"] = 0x08,
			["RightCtrl"] = 0x10,
			["RightShift"] = 0x20,
			["RightAlt"] = 0x40,
		};

		foreach (var (name, mask) in modifiers)
		{
			var key = RampageKeyCatalog.Keys.Single(k => k.DevKeyName == name);
			Assert.Equal(mask, key.ModifierMask);
			Assert.Equal(0x00, key.HidUsageCode);
		}
	}

	[Fact]
	public void OrdinaryKeys_CarryAUsageCodeAndNoModifierMask()
	{
		var ordinary = RampageKeyCatalog.Keys.Where(k => k.ModifierMask == 0).ToList();

		Assert.All(ordinary, k => Assert.NotEqual(0x00, k.HidUsageCode));
	}

	[Fact]
	public void EveryKeyHasVisualBounds()
	{
		foreach (var key in RampageKeyCatalog.Keys)
		{
			Assert.True(RampageVisualLayout.Layout.ContainsKey(key.DevKeyName),
				$"{key.DevKeyName} has no entry in RampageVisualLayout.");
		}
	}

	[Fact]
	public void VisualLayoutHasNoOrphanedEntries()
	{
		var names = RampageKeyCatalog.Keys.Select(k => k.DevKeyName).ToHashSet();

		foreach (var name in RampageVisualLayout.Layout.Keys)
			Assert.True(names.Contains(name), $"{name} is in the layout but not in the catalog.");
	}

	[Fact]
	public void EveryKeyFitsInsideTheCanvas()
	{
		foreach (var (name, b) in RampageVisualLayout.Layout)
		{
			Assert.True(b.X >= 0 && b.Y >= 0, $"{name} has a negative origin.");
			Assert.True(b.Width > 0 && b.Height > 0, $"{name} has a non-positive size.");
			Assert.True(b.X + b.Width <= RampageVisualLayout.CanvasWidth,
				$"{name} overflows the canvas width.");
			Assert.True(b.Y + b.Height <= RampageVisualLayout.CanvasHeight,
				$"{name} overflows the canvas height.");
		}
	}

	[Fact]
	public void KeysDoNotOverlapVisually()
	{
		var entries = RampageVisualLayout.Layout.ToList();

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
	public void IsRampage_MatchesTheVendorIdentifiers()
	{
		Assert.True(RampageKeyCatalog.IsRampage(10473, 12888));
		Assert.False(RampageKeyCatalog.IsRampage(10473, 12889));
		Assert.False(RampageKeyCatalog.IsRampage(14000, 12385)); // Raster
	}

	[Fact]
	public void IsRampage_FallsBackToNameForBluetoothDevices()
	{
		// A Bluetooth Rampage arrives without VID/PID, so the name is the only identity available.
		Assert.True(RampageKeyCatalog.IsRampage("Rampage"));
		Assert.True(RampageKeyCatalog.IsRampage("Wireless Keyboard RAMPAGE"));
		Assert.False(RampageKeyCatalog.IsRampage("Raster"));
		Assert.False(RampageKeyCatalog.IsRampage((string?)null));
	}

	// Independent transcription of key_matrix_tab from
	// docs/knowledge-base/rampage-keyboard-protocol/keyboard_user_flash.c.
	// slot -> (TYPE, CODE_L, CODE_H).
	private static readonly Dictionary<byte, (byte Type, byte CodeLow, byte CodeHigh)> FactoryTable = new()
	{
		// R0
		[0] = (0x10, 0x00, 0x29), [1] = (0x10, 0x00, 0x3A), [2] = (0x10, 0x00, 0x3B),
		[3] = (0x10, 0x00, 0x3C), [4] = (0x10, 0x00, 0x3D), [5] = (0x10, 0x00, 0x3E),
		[6] = (0x10, 0x00, 0x3F), [7] = (0x10, 0x00, 0x40), [8] = (0x10, 0x00, 0x41),
		[9] = (0x10, 0x00, 0x42), [10] = (0x10, 0x00, 0x43), [11] = (0x10, 0x00, 0x44),
		[12] = (0x10, 0x00, 0x45), [13] = (0x10, 0x00, 0x46), [14] = (0x10, 0x00, 0x47),
		[15] = (0x10, 0x00, 0x48),

		// R1
		[16] = (0x10, 0x00, 0x35), [17] = (0x10, 0x00, 0x1E), [18] = (0x10, 0x00, 0x1F),
		[19] = (0x10, 0x00, 0x20), [20] = (0x10, 0x00, 0x21), [21] = (0x10, 0x00, 0x22),
		[22] = (0x10, 0x00, 0x23), [23] = (0x10, 0x00, 0x24), [24] = (0x10, 0x00, 0x25),
		[25] = (0x10, 0x00, 0x26), [26] = (0x10, 0x00, 0x27), [27] = (0x10, 0x00, 0x2D),
		[28] = (0x10, 0x00, 0x2E), [29] = (0x10, 0x00, 0x2A), [30] = (0x10, 0x00, 0x49),
		[31] = (0x10, 0x00, 0x4A),

		// R2
		[32] = (0x10, 0x00, 0x2B), [33] = (0x10, 0x00, 0x14), [34] = (0x10, 0x00, 0x1A),
		[35] = (0x10, 0x00, 0x08), [36] = (0x10, 0x00, 0x15), [37] = (0x10, 0x00, 0x17),
		[38] = (0x10, 0x00, 0x1C), [39] = (0x10, 0x00, 0x18), [40] = (0x10, 0x00, 0x0C),
		[41] = (0x10, 0x00, 0x12), [42] = (0x10, 0x00, 0x13), [43] = (0x10, 0x00, 0x2F),
		[44] = (0x10, 0x00, 0x30), [45] = (0x10, 0x00, 0x31), [46] = (0x10, 0x00, 0x4C),
		[47] = (0x10, 0x00, 0x4D),

		// R3
		[48] = (0x10, 0x00, 0x39), [49] = (0x10, 0x00, 0x04), [50] = (0x10, 0x00, 0x16),
		[51] = (0x10, 0x00, 0x07), [52] = (0x10, 0x00, 0x09), [53] = (0x10, 0x00, 0x0A),
		[54] = (0x10, 0x00, 0x0B), [55] = (0x10, 0x00, 0x0D), [56] = (0x10, 0x00, 0x0E),
		[57] = (0x10, 0x00, 0x0F), [58] = (0x10, 0x00, 0x33), [59] = (0x10, 0x00, 0x34),
		[60] = (0x10, 0x00, 0x00), [61] = (0x10, 0x00, 0x28), [62] = (0x10, 0x00, 0x4B),
		[63] = (0x10, 0x00, 0x4E),

		// R4
		[64] = (0x10, 0x02, 0x00), [65] = (0x10, 0x00, 0x00), [66] = (0x10, 0x00, 0x1D),
		[67] = (0x10, 0x00, 0x1B), [68] = (0x10, 0x00, 0x06), [69] = (0x10, 0x00, 0x19),
		[70] = (0x10, 0x00, 0x05), [71] = (0x10, 0x00, 0x11), [72] = (0x10, 0x00, 0x10),
		[73] = (0x10, 0x00, 0x36), [74] = (0x10, 0x00, 0x37), [75] = (0x10, 0x00, 0x38),
		[76] = (0x10, 0x20, 0x00), [77] = (0x10, 0x00, 0x00), [78] = (0x10, 0x00, 0x52),
		[79] = (0x60, 0x00, 0x00),

		// R5
		[80] = (0x10, 0x01, 0x00), [81] = (0x10, 0x08, 0x00), [82] = (0x10, 0x04, 0x00),
		[83] = (0x10, 0x00, 0x00), [84] = (0x10, 0x00, 0x00), [85] = (0x10, 0x00, 0x00),
		[86] = (0x10, 0x00, 0x2C), [87] = (0x10, 0x00, 0x00), [88] = (0x10, 0x00, 0x00),
		[89] = (0x10, 0x40, 0x00), [90] = (0x60, 0x01, 0x00), [91] = (0x10, 0x00, 0x65),
		[92] = (0x10, 0x10, 0x00), [93] = (0x10, 0x00, 0x50), [94] = (0x10, 0x00, 0x51),
		[95] = (0x10, 0x00, 0x4F),
	};
}
