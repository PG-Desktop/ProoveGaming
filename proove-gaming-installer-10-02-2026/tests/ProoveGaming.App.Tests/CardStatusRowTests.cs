using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace ProoveGaming.App.Tests;

// The wide (double) keyboard cards rendered their status icons off-center (ticket 2026-09-02):
// DoubleCard.axaml laid the row out at fixed Canvas coordinates tuned for the widest
// "icon + separator + battery + percentage" combination, so any shorter visible set sat left
// of the card's content center. SingleCard already had the correct layout — a centered
// StackPanel that re-centers whatever is visible — so the fix extracts that row into the
// shared CardStatusRowView and hosts it from both cards. These pins read the AXAML itself,
// the same technique MacrosSectionRowBindingTests uses, because the bug lives purely in the
// view markup. See docs/plans/fix-card-status-icons-centering.
public class CardStatusRowTests
{
	private static string ViewText(string fileName)
	{
		var path = Path.Combine(AppContext.BaseDirectory, fileName);
		Assert.True(File.Exists(path),
			$"{fileName} was not copied to the test output ({path}). " +
			"Check the <None Include=...> items in ProoveGaming.App.Tests.csproj.");
		return File.ReadAllText(path);
	}

	[Fact]
	public void DoubleCard_HostsTheSharedRowAcrossTheContentBand_NotFixedCoordinates()
	{
		var text = ViewText("DoubleCard.axaml");

		// The host element must span the full 42..646 content band so the control's internal
		// centering lands the visible icon set on the band's center — same-element match, not
		// three independent Contains, so a stray Canvas.Left elsewhere can't satisfy it.
		var host = Regex.Match(text,
			@"<views:CardStatusRowView\b(?:(?!/>)[\s\S])*?Canvas\.Left=""42""(?:(?!/>)[\s\S])*?Width=""604""");
		Assert.True(host.Success,
			"DoubleCard does not host CardStatusRowView across the 604-wide content band — " +
			"the status icons are not centered as a group.");

		// The bug: transport icons pinned at fixed coordinates (all three at Left 294/295),
		// with a hand-placed separator stroke and battery glyphs beside them.
		Assert.DoesNotContain(@"Canvas.Left=""294""", text);
		Assert.DoesNotContain(@"Canvas.Left=""295""", text);
		Assert.DoesNotContain("M320 268V284", text);

		// The tooltip hover-rectangle workaround at 288/262 goes with the row: the shared
		// control carries the tooltip itself.
		Assert.DoesNotContain(@"Canvas.Left=""288""", text);
	}

	[Fact]
	public void SingleCard_HostsTheSharedRow_WithoutAnInlineCopy()
	{
		var text = ViewText("SingleCard.axaml");

		Assert.Contains("<views:CardStatusRowView", text);
		// The separator gating bindings only ever lived in the inline row — any of them left
		// in the file means the row was re-inlined next to the shared control.
		Assert.DoesNotContain("HasDongleSeparator", text);
		Assert.DoesNotContain("HasBluetoothSeparator", text);
	}

	[Fact]
	public void SharedRow_KeepsTheLoadBearingWiring()
	{
		var text = ViewText("CardStatusRowView.axaml");

		// Centering is the point of the extraction.
		Assert.Contains(@"HorizontalAlignment=""Center""", text);

		// The connection tooltip rides on the row itself (Background=Transparent keeps the
		// panel hit-testable), replacing DoubleCard's invisible hover rectangle.
		Assert.Contains(@"ToolTip.Tip=""{Binding ConnectionTooltip}""", text);
		Assert.Contains(@"Background=""Transparent""", text);

		// DynamicResource, not a VM string: the label must follow a runtime language switch
		// (the raw "NO CONNECTION" here shipped untranslated once before).
		Assert.Contains("{DynamicResource Card_NoConnection}", text);

		// The statusText style lives with its only consumer (the NO CONNECTION label above),
		// inside this control's own UserControl.Styles — the host cards no longer carry it.
		// Without it the Classes="statusText" reference resolves to nothing and the label
		// silently drops its colour; no build error would flag that.
		Assert.Contains(@"Selector=""TextBlock.statusText""", text);

		// The audio-mute block stays gated as a whole — ungated it would show the unmuted
		// pair on every mouse and keyboard.
		Assert.Contains(@"IsVisible=""{Binding HasAudioIndicators}""", text);
	}
}
