using System;
using CommunityToolkit.Mvvm.ComponentModel;
using ProoveGaming.App.Models;

namespace ProoveGaming.App.ViewModels;

/// <summary>
/// One card of the Instant Release (SOCD) editor's "Priority Settings" group. The two rule-based
/// priorities (last input, mutual cancel) carry a fixed label and tip; the two absolute ones name the
/// key they favour — the picked key's label while the editor has one, "Key 1" / "Key 2" until then.
/// Strings resolve at read time, like the rest of the section, so a refresh re-reads the language.
/// </summary>
public sealed class SocdPriorityOptionViewModel : ObservableObject
{
	private readonly Action<SocdPriorityOptionViewModel> _onSelected;
	private bool _isSelected;
	private string? _keyLabel;

	public SocdPriorityOptionViewModel(int priority, Action<SocdPriorityOptionViewModel> onSelected)
	{
		Priority = priority;
		_onSelected = onSelected;
	}

	/// <summary>A value of <see cref="SocdPriority"/>.</summary>
	public int Priority { get; }

	public bool IsAbsolute => SocdPriority.IsAbsolute(Priority);

	public string Label => LocalizationManager.GetString(Priority switch
	{
		SocdPriority.MutualCancel => "ExtendedKeys_Socd_MutualCancel",
		SocdPriority.Key1 or SocdPriority.Key2 => "ExtendedKeys_Socd_Absolute",
		_ => "ExtendedKeys_Socd_LastInput",
	});

	/// <summary>The info-icon text: the vendor's one-line description of the rule, naming the favoured key for the absolute ones.</summary>
	public string Tooltip => Priority switch
	{
		SocdPriority.MutualCancel => LocalizationManager.GetString("ExtendedKeys_Socd_MutualCancel_Tip"),
		SocdPriority.Key1 or SocdPriority.Key2 =>
			string.Format(LocalizationManager.GetString("ExtendedKeys_Socd_Absolute_Tip_Format"), KeyLabel),
		_ => LocalizationManager.GetString("ExtendedKeys_Socd_LastInput_Tip"),
	};

	/// <summary>The favoured key's label (absolute priorities only); null on the rule-based cards, which hides the line.</summary>
	public string? KeyLabel
	{
		get => _keyLabel;
		set
		{
			if (SetProperty(ref _keyLabel, value))
			{
				OnPropertyChanged(nameof(HasKeyLabel));
				OnPropertyChanged(nameof(Tooltip));
			}
		}
	}

	public bool HasKeyLabel => _keyLabel is not null;

	/// <summary>Two-way bound to the card's RadioButton; selecting a card tells the editor, which clears the others.</summary>
	public bool IsSelected
	{
		get => _isSelected;
		set
		{
			if (SetProperty(ref _isSelected, value) && value)
				_onSelected(this);
		}
	}

	/// <summary>Re-reads the localised texts (a language change, or a first render after construction).</summary>
	public void RefreshText()
	{
		OnPropertyChanged(nameof(Label));
		OnPropertyChanged(nameof(Tooltip));
	}
}
