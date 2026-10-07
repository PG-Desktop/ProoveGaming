using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace ProoveGaming.App.Tests;

// The Macros-page macro row lost its mouse assignment gesture in PR #104: the shared row was
// switched to inspection bindings for both device types, so on a mouse clicking a macro row
// stopped assigning it to the selected button (QA report 2026-08-31, BLAZE). The ViewModel
// path is fully covered by DeviceDetailsMacroAssignmentTests, but the regression lived purely
// in the view wiring — so these pins read the AXAML itself, the same technique
// BluetoothModeBannerResourcesTests uses. See docs/plans/fix-mouse-macro-list-assignment.
public class MacrosSectionRowBindingTests
{
	private static string ViewText()
	{
		var path = Path.Combine(AppContext.BaseDirectory, "MacrosSectionView.axaml");
		Assert.True(File.Exists(path),
			$"MacrosSectionView.axaml was not copied to the test output ({path}). " +
			"Check the <None Include=...> items in ProoveGaming.App.Tests.csproj.");
		return File.ReadAllText(path);
	}

	// One regex per row variant: the assign/inspect bindings must sit on the SAME RadioButton,
	// not merely somewhere in the file — otherwise a row that mixes one variant's command with
	// the other's checked-state would still pass three independent Contains checks.
	private static Match RowWith(string text, string commandBinding) =>
		Regex.Match(text, $@"<RadioButton\b(?:(?!</RadioButton)[\s\S])*?{Regex.Escape(commandBinding)}");

	// The whole <RadioButton>…</RadioButton> region carrying the given binding — for pins that
	// must land INSIDE the owning row past its attributes (the ✕ sits in the row's content,
	// after the Command attribute RowWith stops at). Safe because rows never nest RadioButtons
	// (the kebab is the extracted MacroRowKebabView).
	private static Match FullRowWith(string text, string commandBinding) =>
		Regex.Match(text,
			$@"<RadioButton\b(?:(?!</RadioButton>)[\s\S])*?{Regex.Escape(commandBinding)}(?:(?!</RadioButton>)[\s\S])*</RadioButton>");

	[Fact]
	public void MouseRow_AssignsOnClick_AndMirrorsTheSelectedButtonBinding()
	{
		var text = ViewText();

		var row = RowWith(text, @"Command=""{Binding AssignCommand}""");
		Assert.True(row.Success,
			"No macro row binds AssignCommand — on a mouse, clicking a macro in the list " +
			"no longer assigns it to the selected button.");
		Assert.Contains(@"IsChecked=""{Binding IsSelected, Mode=TwoWay}""", row.Value);
	}

	[Fact]
	public void MouseRow_CarriesTheUnassignCross_WiredToTheSharedResetDispatcher()
	{
		var text = ViewText();

		// Scoped to the mouse row (PR #125 m2 / plan Round 1 S1): file-wide Contains would stay
		// green if the ✕ migrated into the keyboard row or out of any row entirely.
		var row = FullRowWith(text, @"Command=""{Binding AssignCommand}""");
		Assert.True(row.Success, "no macro row binds AssignCommand — see MouseRow_AssignsOnClick.");
		Assert.Contains("ResetSelectedMacroTargetCommand", row.Value);
		Assert.Contains("CanResetSelectedMacroTarget", row.Value);
		// The ✕ chrome is the shared style; its presence in the row is what QA clicks.
		Assert.Contains(@"Classes=""macroUnassign""", row.Value);
	}

	[Fact]
	public void KeyboardRow_KeepsInspectionSemantics()
	{
		var text = ViewText();

		var row = RowWith(text, @"Command=""{Binding InspectCommand}""");
		Assert.True(row.Success, "The keyboard inspection row is gone from the macro list.");
		Assert.Contains(@"IsChecked=""{Binding IsInspected, Mode=TwoWay}""", row.Value);
		Assert.Contains(@"GroupName=""MacrosInspect""", row.Value);
	}
}
