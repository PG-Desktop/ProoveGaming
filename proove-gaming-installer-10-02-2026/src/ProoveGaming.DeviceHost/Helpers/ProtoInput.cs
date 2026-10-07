using InfraButtonBinding = ProoveGaming.Infrastructure.Classes.ButtonBinding;
using InfraMacro = ProoveGaming.Infrastructure.Classes.MacroDefinition;
using InfraMacroEvent = ProoveGaming.Infrastructure.Classes.MacroEvent;
using InfraMacroLibraryEntry = ProoveGaming.Infrastructure.Classes.MacroLibraryEntry;
using InfraMacroMode = ProoveGaming.Infrastructure.Classes.MacroRepeatMode;
using InfraMouseAction = ProoveGaming.Infrastructure.Classes.MouseButtonAction;
using InfraMouseButton = ProoveGaming.Infrastructure.Classes.MouseButton;
using ProtoButtonBinding = ProoveGaming.GrpcDependencies.Device.ButtonBinding;
using ProtoMacro = ProoveGaming.GrpcDependencies.Device.MacroDefinition;
using ProtoMacroEvent = ProoveGaming.GrpcDependencies.Device.MacroEvent;
using ProtoMacroLibraryEntry = ProoveGaming.GrpcDependencies.Device.MacroLibraryEntry;

namespace ProoveGaming.DeviceHost.Helpers;

/// <summary>
/// Turns gRPC request data into the Infrastructure types the device services write, treating it as
/// untrusted: the DeviceHost runs as LocalSystem and any local process can call it
/// (docs/plans/fix-repository-audit-findings, S9). Every path that maps a macro or a button binding
/// goes through here, so a value is normalised the same way whichever RPC carried it.
/// </summary>
/// <remarks>
/// Rules: key codes are clamped to a byte (they are one on every device protocol) and delays to a
/// non-negative int; a button, action or repeat mode the Infrastructure enums do not define
/// rejects the WHOLE request, since writing a guessed value into a device is worse than writing
/// nothing; a macro longer than <see cref="MaxEventsPerMacro"/> rejects it too.
/// </remarks>
internal static class ProtoInput
{
	/// <summary>
	/// Far above any macro a device can store (the keyboard encoders keep 60 events), so it never
	/// refuses a real macro. It bounds what one request can make this process allocate and walk.
	/// </summary>
	internal const int MaxEventsPerMacro = 256;

	/// <summary>Null, with <paramref name="error"/> set, when any binding or its macro is invalid.</summary>
	internal static List<InfraButtonBinding>? MapBindings(IReadOnlyList<ProtoButtonBinding> bindings, out string? error)
	{
		var mapped = new List<InfraButtonBinding>(bindings.Count);
		foreach (var binding in bindings)
		{
			var button = (InfraMouseButton)(int)binding.Button;
			var action = (InfraMouseAction)(int)binding.Action;
			if (!Enum.IsDefined(button) || !Enum.IsDefined(action))
			{
				error = $"undefined button {(int)binding.Button} or action {(int)binding.Action}";
				return null;
			}

			InfraMacro? macro = null;
			if (binding.Macro is { } protoMacro && (macro = MapMacro(protoMacro, out error)) is null)
			{
				return null;
			}

			mapped.Add(new InfraButtonBinding(button, action, macro));
		}

		error = null;
		return mapped;
	}

	/// <summary>Null, with <paramref name="error"/> set, when any entry is invalid.</summary>
	internal static List<InfraMacroLibraryEntry>? MapLibraryEntries(IReadOnlyList<ProtoMacroLibraryEntry> entries, out string? error)
	{
		var mapped = new List<InfraMacroLibraryEntry>(entries.Count);
		foreach (var entry in entries)
		{
			var mode = (InfraMacroMode)(int)entry.Mode;
			var events = MapEvents(entry.Events, out error);
			if (events is null)
			{
				return null;
			}

			if (!Enum.IsDefined(mode))
			{
				error = $"undefined macro mode {(int)entry.Mode}";
				return null;
			}

			// repeat_count 0 is an old client that predates the field — clamp to 1, never trust it.
			mapped.Add(new InfraMacroLibraryEntry((int)Math.Min(entry.Index, int.MaxValue), mode, events,
				(int)Math.Clamp(entry.RepeatCount, 1u, 65535u)));
		}

		error = null;
		return mapped;
	}

	private static InfraMacro? MapMacro(ProtoMacro macro, out string? error)
	{
		var mode = (InfraMacroMode)(int)macro.Mode;
		if (!Enum.IsDefined(mode))
		{
			error = $"undefined macro mode {(int)macro.Mode}";
			return null;
		}

		var events = MapEvents(macro.Events, out error);
		return events is null
			? null
			: new InfraMacro(macro.Name, (int)Math.Min(macro.RepeatCount, 65535u), mode, events);
	}

	private static List<InfraMacroEvent>? MapEvents(IReadOnlyList<ProtoMacroEvent> events, out string? error)
	{
		if (events.Count > MaxEventsPerMacro)
		{
			error = $"macro has {events.Count} events (limit {MaxEventsPerMacro})";
			return null;
		}

		var mapped = new List<InfraMacroEvent>(events.Count);
		foreach (var e in events)
		{
			mapped.Add(new InfraMacroEvent((byte)Math.Min(e.KeyCode, byte.MaxValue), e.IsPress, (int)Math.Min(e.DelayMs, int.MaxValue)));
		}

		error = null;
		return mapped;
	}
}
