using System;
using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// Locks down the BLAZE / X43 factory key matrix. Every button write rebuilds from this
// baseline and only overrides the remappable slots (1–4), so the fixed DPI/rate/LED/ACPI
// slots must stay byte-exact — if they drift, those hardware buttons silently break.
// Baseline: docs/mouse/blaze/key-matrix.md.
public class X43KeyMatrixTests
{
	// The capture-confirmed factory matrix, laid out one slot (4 bytes) per line.
	private static readonly byte[] FactorySnapshot =
	[
		0xF0, 0x00, 0x00, 0x02, // slot 0  Left click
		0xF1, 0x00, 0x00, 0x02, // slot 1  Right click
		0xF2, 0x00, 0x00, 0x02, // slot 2  Wheel click
		0xF4, 0x00, 0x00, 0x02, // slot 3  Forward (B5)
		0xF3, 0x00, 0x00, 0x02, // slot 4  Back (B4)
		0x03, 0x00, 0x00, 0x0B, // slot 5  DPI+ loop        (FIXED)
		0x03, 0x00, 0x00, 0x06, // slot 6  report-rate loop (FIXED)
		0x00, 0x00, 0x00, 0x0E, // slot 7  LED slide switch — mode 0x0E leaves the LED to software
		0x00, 0x00, 0x00, 0x02, // slot 8  null mouse button
		0x00, 0x00, 0x00, 0x02, // slot 9  null mouse button
		0x00, 0x00, 0x00, 0x02, // slot 10 null mouse button
		0x00, 0x00, 0x00, 0x02, // slot 11 null mouse button
		0x00, 0x00, 0x00, 0x02, // slot 12 null mouse button
		0x00, 0x00, 0x00, 0x02, // slot 13 null mouse button
		0x00, 0x01, 0x00, 0x04, // slot 14 ACPI             (FIXED)
		0x00, 0x02, 0x00, 0x04, // slot 15 ACPI             (FIXED)
	];

	[Fact]
	public void CreateDefault_ReturnsExactly64Bytes()
	{
		Assert.Equal(64, X43KeyMatrix.CreateDefault().Length);
	}

	[Fact]
	public void CreateDefault_MatchesFactorySnapshot()
	{
		Assert.Equal(FactorySnapshot, X43KeyMatrix.CreateDefault());
	}

	// The fixed hardware buttons — a regression here breaks DPI / report-rate / LED / ACPI.
	[Theory]
	[InlineData(5, 0x03, 0x00, 0x00, 0x0B)]  // DPI+ loop
	[InlineData(6, 0x03, 0x00, 0x00, 0x06)]  // report-rate loop
	[InlineData(7, 0x00, 0x00, 0x00, 0x0E)]  // LED slide switch (see CreateDefault: 0x0A loses RGB on reconnect)
	[InlineData(14, 0x00, 0x01, 0x00, 0x04)] // ACPI
	[InlineData(15, 0x00, 0x02, 0x00, 0x04)] // ACPI
	public void CreateDefault_FixedSlot_HasExactBytes(int slot, byte b0, byte b1, byte b2, byte b3)
	{
		var matrix = X43KeyMatrix.CreateDefault();
		var offset = X43KeyMatrix.SlotOffset(slot);

		Assert.Equal(new byte[] { b0, b1, b2, b3 }, matrix[offset..(offset + 4)]);
	}

	[Fact]
	public void CreateDefault_ReturnsFreshArrayEachCall()
	{
		// It's a rebuild-from-scratch baseline — callers mutate their copy, so two calls must
		// not share the same array.
		Assert.NotSame(X43KeyMatrix.CreateDefault(), X43KeyMatrix.CreateDefault());
	}

	// ── EncodeAction ────────────────────────────────────────────────────────────

	// The full action → 4-byte table (source: docs/x43_protocol_904a4f8.md §V).
	[Theory]
	[InlineData(MouseButtonAction.LeftClick,     0xF0, 0x00, 0x00, 0x02)]
	[InlineData(MouseButtonAction.RightClick,    0xF1, 0x00, 0x00, 0x02)]
	[InlineData(MouseButtonAction.WheelClick,    0xF2, 0x00, 0x00, 0x02)]
	[InlineData(MouseButtonAction.Forward,       0xF4, 0x00, 0x00, 0x02)]
	[InlineData(MouseButtonAction.Back,          0xF3, 0x00, 0x00, 0x02)]
	[InlineData(MouseButtonAction.PlayPause,     0xCD, 0x00, 0x00, 0x03)]
	[InlineData(MouseButtonAction.NextTrack,     0xB5, 0x00, 0x00, 0x03)]
	[InlineData(MouseButtonAction.PreviousTrack, 0xB6, 0x00, 0x00, 0x03)]
	[InlineData(MouseButtonAction.Stop,          0xB7, 0x00, 0x00, 0x03)]
	[InlineData(MouseButtonAction.Mute,          0xE2, 0x00, 0x00, 0x03)]
	[InlineData(MouseButtonAction.VolumeUp,      0xE9, 0x00, 0x00, 0x03)]
	[InlineData(MouseButtonAction.VolumeDown,    0xEA, 0x00, 0x00, 0x03)]
	[InlineData(MouseButtonAction.MediaPlayer,   0x83, 0x02, 0x00, 0x03)]
	[InlineData(MouseButtonAction.WwwHome,       0x23, 0x02, 0x00, 0x03)]
	[InlineData(MouseButtonAction.MyComputer,    0x08, 0xE3, 0x00, 0x01)]
	[InlineData(MouseButtonAction.Desktop,       0x07, 0xE3, 0x00, 0x01)]
	[InlineData(MouseButtonAction.WwwSearch,     0x21, 0x02, 0x00, 0x03)]
	public void EncodeAction_ProducesExactBytes(MouseButtonAction action, byte b0, byte b1, byte b2, byte b3)
	{
		Assert.Equal(new byte[] { b0, b1, b2, b3 }, X43KeyMatrix.EncodeAction(action));
	}

	[Fact]
	public void EncodeAction_Unspecified_ReturnsNull()
	{
		Assert.Null(X43KeyMatrix.EncodeAction(MouseButtonAction.Unspecified));
	}

	[Fact]
	public void EncodeAction_Macro_ReturnsNull()
	{
		// Macro is not a plain matrix entry — it is uploaded + bound separately (mode 0x05).
		Assert.Null(X43KeyMatrix.EncodeAction(MouseButtonAction.Macro));
	}

	// Completeness: catches "added an action but forgot to encode it" — which would otherwise
	// bind a button to a silent no-op. Every value except Unspecified and Macro must encode.
	[Fact]
	public void EncodeAction_EveryActionExceptUnspecifiedAndMacro_IsEncoded()
	{
		foreach (var action in Enum.GetValues<MouseButtonAction>())
		{
			if (action is MouseButtonAction.Unspecified or MouseButtonAction.Macro)
				continue;

			Assert.True(X43KeyMatrix.EncodeAction(action) is not null,
				$"MouseButtonAction.{action} has no key-matrix encoding — a bound button would silently no-op.");
		}
	}

	// Mode byte (entry[3]) by family: standard mouse = 0x02, multimedia = 0x03,
	// Win+E/Win+D system combos = 0x01, WWW Search = 0x03 (consumer usage).
	[Theory]
	[InlineData(MouseButtonAction.LeftClick,  0x02)]
	[InlineData(MouseButtonAction.Back,       0x02)]
	[InlineData(MouseButtonAction.PlayPause,  0x03)]
	[InlineData(MouseButtonAction.VolumeUp,   0x03)]
	[InlineData(MouseButtonAction.MediaPlayer,0x03)]
	[InlineData(MouseButtonAction.MyComputer, 0x01)]
	[InlineData(MouseButtonAction.Desktop,    0x01)]
	[InlineData(MouseButtonAction.WwwSearch,  0x03)]
	public void EncodeAction_ModeByte_MatchesFamily(MouseButtonAction action, byte expectedMode)
	{
		var entry = X43KeyMatrix.EncodeAction(action);
		Assert.NotNull(entry);
		Assert.Equal(expectedMode, entry![3]);
	}

	// ── ButtonToSlot ─────────────────────────────────────────────────────────────

	// Physical button -> this unit's writable slot index. A wrong index would remap the wrong
	// button; -1 means "this device doesn't expose it" and the caller must skip the write.
	[Theory]
	[InlineData(MouseButton.Left, 0)]
	[InlineData(MouseButton.Right, 1)]
	[InlineData(MouseButton.Wheel, 2)]
	[InlineData(MouseButton.Forward, 3)]
	[InlineData(MouseButton.Back, 4)]
	[InlineData(MouseButton.Unspecified, -1)]
	public void ButtonToSlot_MapsButton(MouseButton button, int expectedSlot)
	{
		Assert.Equal(expectedSlot, X43KeyMatrix.ButtonToSlot(button));
	}
}
