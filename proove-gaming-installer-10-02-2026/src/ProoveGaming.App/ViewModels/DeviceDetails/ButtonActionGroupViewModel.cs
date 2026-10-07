using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProoveGaming.App.Models;
using ProoveGaming.GrpcDependencies.Device;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace ProoveGaming.App.ViewModels;

public sealed class MacroViewModel : ObservableObject
{
	// SQLite row id (0 until first saved). Lets us update/delete the library entry.
	public int Id { get; set; }

	private string _name = string.Empty;
	public required string Name
	{
		get => _name;
		set
		{
			if (SetProperty(ref _name, value))
				OnPropertyChanged(nameof(DisplayName));
		}
	}

	// The macro list row shows a truncated name so a long one doesn't overflow the row
	// (e.g. "some very long name 123" → "some very long name 1..."). Name stays the source of truth.
	private const int MaxDisplayNameChars = 21;
	public string DisplayName => Name.Length > MaxDisplayNameChars
		? Name[..MaxDisplayNameChars].TrimEnd() + "..."
		: Name;

	public int Repetitions { get; init; }
	public bool AutoRecordDelays { get; init; }
	public bool RepeatUntilReleased { get; init; }

	// The recorded steps (source of truth for upload + persistence).
	public IReadOnlyList<MacroEventRecord> Events { get; init; } = [];

	// How the firmware should repeat the macro; "repeat until released" wins over a count.
	public MacroRepeatMode Mode => RepeatUntilReleased
		? MacroRepeatMode.MacroWhileHeld
		: MacroRepeatMode.MacroPlayCount;

	public int RepeatCount => Repetitions > 0 ? Repetitions : 1;

	private bool _isSelected;
	public bool IsSelected
	{
		get => _isSelected;
		set => SetProperty(ref _isSelected, value);
	}

	// The Macros page's inspection radio: checked when this macro is the one whose bindings are
	// highlighted on the device image. Deliberately separate from IsSelected, which the Key
	// Assignment Dialog still uses to mean "bound to the dialog's key" and which
	// RefreshMacroListSelection sweeps on every key click — one flag serving both meanings would
	// let either surface overwrite the other.
	private bool _isInspected;
	public bool IsInspected
	{
		get => _isInspected;
		set => SetProperty(ref _isInspected, value);
	}

	// Hidden by the Macros search filter when the name doesn't match the query.
	private bool _isVisible = true;
	public bool IsVisible
	{
		get => _isVisible;
		set => SetProperty(ref _isVisible, value);
	}

	// Kebab-menu actions, wired by the owning DeviceDetailsViewModel.
	public IRelayCommand<MacroViewModel>? EditCommand { get; set; }
	public IRelayCommand<MacroViewModel>? RemoveCommand { get; set; }

	// Assigns this macro to the currently-selected hotspot (Macros page). Wired by the owning
	// DeviceDetailsViewModel; a no-op when no assignable button is selected.
	public IRelayCommand<MacroViewModel>? AssignCommand { get; set; }

	// Macros-page row click: marks this macro inspected and highlights the keys / buttons it is
	// bound to on the device image. Never assigns anything.
	public IRelayCommand<MacroViewModel>? InspectCommand { get; set; }

	public MacroRecord ToRecord() => new()
	{
		Id = Id,
		Name = Name,
		RepeatCount = RepeatCount,
		Mode = Mode,
		Events = [.. Events],
	};

	public static MacroViewModel FromRecord(MacroRecord record) => new()
	{
		Id = record.Id,
		Name = record.Name,
		Repetitions = record.RepeatCount,
		RepeatUntilReleased = record.Mode == MacroRepeatMode.MacroWhileHeld,
		Events = [.. record.Events],
	};
}

// One captured step in the macro recorder list (Create Macro dialog).
public sealed class RecordedStepViewModel : ObservableObject
{
	// True = keyboard key, false = mouse button.
	public required bool IsKeyboard { get; init; }
	public required string Label { get; init; }
	public required int DelayMs { get; init; }
	// Press (arrow down) vs release (arrow up).
	public required bool IsPress { get; init; }
	// 1-based click number this step belongs to — a press+its release share the same number.
	public required int Iteration { get; init; }
	public required IRelayCommand<RecordedStepViewModel> RemoveCommand { get; init; }

	public string DelayLabel => $"{DelayMs} ms";
	public string IterationLabel => $"{Iteration}.";
}

public sealed class ButtonActionOptionViewModel : ObservableObject
{
	public string? LabelKey { get; init; }
	public required string GroupName { get; init; }
	// The action this option assigns; Unspecified for non-binding options.
	public MouseButtonAction Action { get; init; } = MouseButtonAction.Unspecified;
	// Set only for macro options (Action == Macro): the library macro this option assigns.
	public MacroViewModel? Macro { get; init; }
	public required IRelayCommand<ButtonActionOptionViewModel> SelectCommand { get; init; }

	private string _label = string.Empty;
	public required string Label
	{
		get => _label;
		set => SetProperty(ref _label, value);
	}

	private bool _isSelected;
	public bool IsSelected
	{
		get => _isSelected;
		set => SetProperty(ref _isSelected, value);
	}

	// Hidden by the Buttons search filter when it doesn't match the query.
	private bool _isVisible = true;
	public bool IsVisible
	{
		get => _isVisible;
		set => SetProperty(ref _isVisible, value);
	}
}

public sealed class ButtonActionGroupViewModel : ObservableObject
{
	public string? TitleKey { get; init; }
	public ObservableCollection<ButtonActionOptionViewModel> Options { get; } = [];

	private string _title = string.Empty;
	public required string Title
	{
		get => _title;
		set => SetProperty(ref _title, value);
	}

	private bool _isExpanded;
	public bool IsExpanded
	{
		get => _isExpanded;
		set => SetProperty(ref _isExpanded, value);
	}

	// Hidden by the Buttons search filter when none of its options match the query.
	private bool _isVisible = true;
	public bool IsVisible
	{
		get => _isVisible;
		set => SetProperty(ref _isVisible, value);
	}

	public void SelectOption(ButtonActionOptionViewModel selected)
	{
		foreach (var option in Options)
		{
			option.IsSelected = ReferenceEquals(option, selected);
		}
	}
}
