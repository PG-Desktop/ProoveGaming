using System;
using System.Linq;
using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// Locks down the TAIPAN button map encoder. Because the first write packet (0xD1) erases the whole
// flash sector, BuildTable must always re-emit the full factory table with only the requested
// slots overlaid — the wheel pair and the DPI/rate buttons must never drift, TAIPAN's analogue of
// the BLAZE "fixed slots must not drift" guard. Baseline: docs/mouse/taipan/protocol.md.
public class TaipanButtonMapTests
{
	[Theory]
	// Standard mouse buttons.
	[InlineData(MouseButtonAction.LeftClick, 0x81, 0x00, 0x00, 0x00)]
	[InlineData(MouseButtonAction.RightClick, 0x82, 0x00, 0x00, 0x00)]
	[InlineData(MouseButtonAction.WheelClick, 0x83, 0x00, 0x00, 0x00)]
	[InlineData(MouseButtonAction.Forward, 0x85, 0x00, 0x00, 0x00)]
	[InlineData(MouseButtonAction.Back, 0x84, 0x00, 0x00, 0x00)]
	// Consumer keys (function 0x8E, usage little-endian into Alpha1/Alpha2).
	[InlineData(MouseButtonAction.PlayPause, 0x8E, 0x00, 0xCD, 0x00)]
	[InlineData(MouseButtonAction.NextTrack, 0x8E, 0x00, 0xB5, 0x00)]
	[InlineData(MouseButtonAction.PreviousTrack, 0x8E, 0x00, 0xB6, 0x00)]
	[InlineData(MouseButtonAction.Stop, 0x8E, 0x00, 0xB7, 0x00)]
	[InlineData(MouseButtonAction.Mute, 0x8E, 0x00, 0xE2, 0x00)]
	[InlineData(MouseButtonAction.VolumeUp, 0x8E, 0x00, 0xE9, 0x00)]
	[InlineData(MouseButtonAction.VolumeDown, 0x8E, 0x00, 0xEA, 0x00)]
	[InlineData(MouseButtonAction.MediaPlayer, 0x8E, 0x00, 0x83, 0x01)]
	[InlineData(MouseButtonAction.WwwHome, 0x8E, 0x00, 0x23, 0x02)]
	[InlineData(MouseButtonAction.WwwSearch, 0x8E, 0x00, 0x21, 0x02)]
	// System combos (function 0x8F, Win-key modifier 0x08 + a HID key code).
	[InlineData(MouseButtonAction.MyComputer, 0x8F, 0x08, 0x08, 0x00)]
	[InlineData(MouseButtonAction.Desktop, 0x8F, 0x08, 0x07, 0x00)]
	public void EncodeAction_ProducesExactEntry(MouseButtonAction action, byte fn, byte modify, byte a1, byte a2)
	{
		Assert.Equal(new byte[] { fn, modify, a1, a2 }, TaipanButtonMap.EncodeAction(action));
	}

	[Fact]
	public void EncodeAction_Unspecified_ReturnsNull()
	{
		Assert.Null(TaipanButtonMap.EncodeAction(MouseButtonAction.Unspecified));
	}

	[Fact]
	public void EncodeAction_Macro_ReturnsNull()
	{
		// Macro binds to a device macro-slot at write time (function 0x91), not a plain entry.
		Assert.Null(TaipanButtonMap.EncodeAction(MouseButtonAction.Macro));
	}

	// The "added an action, forgot to encode it" guard: every action except the two handled
	// elsewhere must encode to a 4-byte entry.
	[Fact]
	public void EncodeAction_CoversEveryActionExceptUnspecifiedAndMacro()
	{
		foreach (var action in Enum.GetValues<MouseButtonAction>())
		{
			if (action is MouseButtonAction.Unspecified or MouseButtonAction.Macro)
				continue;

			var entry = TaipanButtonMap.EncodeAction(action);
			Assert.NotNull(entry);
			Assert.Equal(4, entry!.Length);
		}
	}

	[Theory]
	[InlineData(MouseButton.Left, 1)]
	[InlineData(MouseButton.Right, 2)]
	[InlineData(MouseButton.Wheel, 3)]
	[InlineData(MouseButton.Forward, 4)]
	[InlineData(MouseButton.Back, 5)]
	[InlineData(MouseButton.Unspecified, -1)]
	public void ButtonToIndex_MapsSharedButtons(MouseButton button, int expected)
	{
		Assert.Equal(expected, TaipanButtonMap.ButtonToIndex(button));
	}

	[Fact]
	public void CreateDefault_ReturnsFullTableLength()
	{
		Assert.Equal(TaipanButtonMap.TableLength, TaipanButtonMap.CreateDefault().Length);
	}

	[Fact]
	public void BuildTable_NoBindings_MatchesFactoryDefault()
	{
		var t = TaipanButtonMap.BuildTable(Array.Empty<ButtonBinding>());

		// Header: max button-event count 0x000A.
		Assert.Equal(0x0A, t[0]);
		Assert.Equal(0x00, t[1]);

		AssertEntry(t, 1, 0x81, 0x00, 0x00, 0x00); // Left
		AssertEntry(t, 2, 0x82, 0x00, 0x00, 0x00); // Right
		AssertEntry(t, 3, 0x83, 0x00, 0x00, 0x00); // Middle
		AssertEntry(t, 4, 0x85, 0x00, 0x00, 0x00); // B5 / Forward
		AssertEntry(t, 5, 0x84, 0x00, 0x00, 0x00); // B4 / Back
		AssertEntry(t, 6, 0x88, 0x00, 0x00, 0x00); // DPI cycle
		AssertEntry(t, 7, 0x97, 0x01, 0x00, 0x00); // Polling-rate cycle
		// Absolute slot numbers, deliberately NOT via WheelUpIndex/WheelDownIndex: asserting through
		// the constants makes this test agree with whatever the constants say, so it stayed green
		// while the wheel pair was reversed. Hardware order is slot 23 = DOWN, slot 24 = UP.
		AssertEntry(t, 23, 0x8C, 0x00, 0x00, 0x00); // Wheel down
		AssertEntry(t, 24, 0x8B, 0x00, 0x00, 0x00); // Wheel up
	}

	[Fact]
	public void BuildTable_OverlaysOnlyRequestedSlot_LeavesFixedAndWheelUntouched()
	{
		var bindings = new[]
		{
			new ButtonBinding(MouseButton.Forward, MouseButtonAction.VolumeUp),
		};

		var t = TaipanButtonMap.BuildTable(bindings);

		// Forward → table index 4 → Volume Up consumer entry.
		AssertEntry(t, 4, 0x8E, 0x00, 0xE9, 0x00);

		// Everything else stays factory: the DPI/rate buttons and both wheel entries.
		AssertEntry(t, 6, 0x88, 0x00, 0x00, 0x00);
		AssertEntry(t, 7, 0x97, 0x01, 0x00, 0x00);
		// Absolute slots again — this is the regression that mattered in practice: remapping ONE
		// button rewrites the whole table, so a reversed wheel pair inverted scrolling on every
		// binding change the user made.
		AssertEntry(t, 23, 0x8C, 0x00, 0x00, 0x00); // Wheel down
		AssertEntry(t, 24, 0x8B, 0x00, 0x00, 0x00); // Wheel up

		// And the untouched sibling remappable slots keep their defaults.
		AssertEntry(t, 1, 0x81, 0x00, 0x00, 0x00);
		AssertEntry(t, 5, 0x84, 0x00, 0x00, 0x00);
	}

	[Fact]
	public void BuildTable_UnspecifiedBinding_KeepsFactoryDefault()
	{
		var bindings = new[]
		{
			new ButtonBinding(MouseButton.Right, MouseButtonAction.Unspecified),
		};

		var t = TaipanButtonMap.BuildTable(bindings);
		AssertEntry(t, 2, 0x82, 0x00, 0x00, 0x00); // Right stays factory
	}

	// Macro bind entries (function 0x91): BtModify bit7 = infinite, bit6 = interrupt,
	// bits0-4 = slot; BtAlpha1 = loop count for PlayCount.
	[Fact]
	public void EncodeMacroEntry_PlayCount_CarriesSlotAndLoopCount()
	{
		Assert.Equal(new byte[] { 0x91, 0x03, 0x05, 0x00 },
			TaipanButtonMap.EncodeMacroEntry(3, MacroRepeatMode.PlayCount, 5));
	}

	[Fact]
	public void EncodeMacroEntry_LoopForever_SetsInfiniteAndInterruptBits()
	{
		Assert.Equal(new byte[] { 0x91, 0xC3, 0x00, 0x00 },
			TaipanButtonMap.EncodeMacroEntry(3, MacroRepeatMode.LoopForever, 0));
	}

	[Fact]
	public void EncodeMacroEntry_WhileHeld_MapsToToggleApproximation()
	{
		// TAIPAN documents no hold-to-repeat; WhileHeld maps to the same infinite+interrupt
		// toggle as LoopForever (see the add-taipan-macros plan).
		Assert.Equal(TaipanButtonMap.EncodeMacroEntry(2, MacroRepeatMode.LoopForever, 0),
			TaipanButtonMap.EncodeMacroEntry(2, MacroRepeatMode.WhileHeld, 0));
	}

	[Fact]
	public void EncodeMacroEntry_PlayCount_ClampsRepeatToAtLeastOnce()
	{
		Assert.Equal(new byte[] { 0x91, 0x00, 0x01, 0x00 },
			TaipanButtonMap.EncodeMacroEntry(0, MacroRepeatMode.PlayCount, 0));
	}

	[Theory]
	[InlineData(-1)]
	[InlineData(20)]
	public void EncodeMacroEntry_RejectsSlotOutOfRange(int slot)
	{
		Assert.Throws<ArgumentOutOfRangeException>(() =>
			TaipanButtonMap.EncodeMacroEntry(slot, MacroRepeatMode.PlayCount, 1));
	}

	private static void AssertEntry(byte[] table, int index, byte fn, byte modify, byte a1, byte a2)
	{
		int o = TaipanButtonMap.EntryOffset(index);
		Assert.Equal(new byte[] { fn, modify, a1, a2 }, table.Skip(o).Take(4).ToArray());
	}
}
