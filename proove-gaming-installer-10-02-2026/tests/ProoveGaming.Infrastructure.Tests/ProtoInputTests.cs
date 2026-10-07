using System;
using System.Linq;
using ProoveGaming.DeviceHost.Helpers;
using ProoveGaming.GrpcDependencies.Device;
using Xunit;
using InfraMouseAction = ProoveGaming.Infrastructure.Classes.MouseButtonAction;
using InfraMouseButton = ProoveGaming.Infrastructure.Classes.MouseButton;

namespace ProoveGaming.Infrastructure.Tests;

// Pins how the DeviceHost normalises gRPC input before it reaches a device
// (docs/plans/fix-repository-audit-findings, S9). The host runs as LocalSystem and any local process
// can call it, so the request is untrusted. Before this, SetMacros clamped key codes but the
// SetButtonBindings / ApplyDeviceConfiguration path cast them unclamped, and no path checked enum
// values: 300 became key code 44, and an undefined button was written as whatever its number meant.
public class ProtoInputTests
{
	private static MacroEvent Event(uint keyCode, uint delayMs = 10) => new() { KeyCode = keyCode, IsPress = true, DelayMs = delayMs };

	[Fact]
	public void BindingMacroKeyCode_IsClamped_NotWrapped()
	{
		var macro = new MacroDefinition { Name = "m", RepeatCount = 1, Mode = MacroRepeatMode.MacroPlayCount };
		macro.Events.Add(Event(300));
		var binding = new ButtonBinding { Button = MouseButton.ButtonForward, Action = MouseButtonAction.Macro, Macro = macro };

		var mapped = ProtoInput.MapBindings(new[] { binding }, out var error);

		Assert.Null(error);
		Assert.Equal(255, Assert.Single(mapped!).Macro!.Events.Single().KeyCode);
	}

	[Fact]
	public void ValidBinding_MapsByValue()
	{
		var binding = new ButtonBinding { Button = MouseButton.ButtonBack, Action = MouseButtonAction.PlayPause };

		var mapped = Assert.Single(ProtoInput.MapBindings(new[] { binding }, out _)!);

		Assert.Equal(InfraMouseButton.Back, mapped.Button);
		Assert.Equal(InfraMouseAction.PlayPause, mapped.Action);
		Assert.Null(mapped.Macro);
	}

	[Fact]
	public void UndefinedButton_RejectsTheWholeRequest()
	{
		var bindings = new[]
		{
			new ButtonBinding { Button = MouseButton.ButtonLeft, Action = MouseButtonAction.LeftClick },
			new ButtonBinding { Button = (MouseButton)999, Action = MouseButtonAction.LeftClick },
		};

		Assert.Null(ProtoInput.MapBindings(bindings, out var error));
		Assert.Contains("999", error);
	}

	[Fact]
	public void UndefinedAction_RejectsTheWholeRequest()
	{
		var bindings = new[] { new ButtonBinding { Button = MouseButton.ButtonLeft, Action = (MouseButtonAction)4242 } };

		Assert.Null(ProtoInput.MapBindings(bindings, out _));
	}

	[Fact]
	public void MacroLongerThanTheCeiling_IsRejected()
	{
		var entry = new MacroLibraryEntry { Index = 0, Mode = MacroRepeatMode.MacroPlayCount, RepeatCount = 1 };
		for (var i = 0; i <= ProtoInput.MaxEventsPerMacro; i++)
			entry.Events.Add(Event(4));

		Assert.Null(ProtoInput.MapLibraryEntries(new[] { entry }, out var error));
		Assert.Contains("events", error);
	}

	[Fact]
	public void MacroAtTheCeiling_IsAccepted()
	{
		var entry = new MacroLibraryEntry { Index = 0, Mode = MacroRepeatMode.MacroPlayCount, RepeatCount = 1 };
		for (var i = 0; i < ProtoInput.MaxEventsPerMacro; i++)
			entry.Events.Add(Event(4));

		Assert.NotNull(ProtoInput.MapLibraryEntries(new[] { entry }, out _));
	}

	[Fact]
	public void LibraryEntry_ZeroRepeatCount_BecomesOne_AndHugeDelayStaysPositive()
	{
		// repeat_count 0 is an old client that predates the field.
		var entry = new MacroLibraryEntry { Index = 3, Mode = MacroRepeatMode.MacroPlayCount, RepeatCount = 0 };
		entry.Events.Add(Event(4, delayMs: uint.MaxValue));

		var mapped = Assert.Single(ProtoInput.MapLibraryEntries(new[] { entry }, out _)!);

		Assert.Equal(1, mapped.RepeatCount);
		Assert.Equal(3, mapped.Index);
		Assert.True(mapped.Events.Single().DelayMs > 0);
	}

	[Fact]
	public void UndefinedMacroMode_IsRejected()
	{
		var entry = new MacroLibraryEntry { Index = 0, Mode = (MacroRepeatMode)77, RepeatCount = 1 };

		Assert.Null(ProtoInput.MapLibraryEntries(new[] { entry }, out _));
	}

	// ── Proto ↔ Infrastructure enum parity (review round 2) ─────────────────────────────────
	//
	// ProtoInput maps these by numeric value and REJECTS a value the Infrastructure enum does not
	// define. A value added to the proto without its Infrastructure twin would therefore turn that
	// remap into a silent Success = false, with only a host log line to show for it.

	[Theory]
	[InlineData(typeof(MouseButton), typeof(InfraMouseButton))]
	[InlineData(typeof(MouseButtonAction), typeof(InfraMouseAction))]
	[InlineData(typeof(MacroRepeatMode), typeof(ProoveGaming.Infrastructure.Classes.MacroRepeatMode))]
	public void ProtoEnum_AndInfrastructureEnum_DefineTheSameValues(Type proto, Type infrastructure)
	{
		var protoValues = Enum.GetValues(proto).Cast<object>().Select(Convert.ToInt32).OrderBy(v => v).ToArray();
		var infraValues = Enum.GetValues(infrastructure).Cast<object>().Select(Convert.ToInt32).OrderBy(v => v).ToArray();

		Assert.Equal(protoValues, infraValues);
	}
}
