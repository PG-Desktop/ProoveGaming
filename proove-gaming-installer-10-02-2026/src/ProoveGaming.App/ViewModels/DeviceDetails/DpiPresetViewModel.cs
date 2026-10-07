using System;
using CommunityToolkit.Mvvm.Input;

namespace ProoveGaming.App.ViewModels;

public partial class DpiPresetViewModel : ViewModelBase
{
	private bool _isSelected;
	private int _dpiValue;
	private int _stageIndex;
	private bool _canRemove;

	// Positional, and RE-ASSIGNED when a point above this one is removed: the device's stage table
	// is positional too, so the rows must renumber rather than keep a stable id. Everything that
	// caches a stage index (the persisted active stage, _assertedDpiStages) is re-clamped by the
	// remove path for the same reason.
	public int StageIndex
	{
		get => _stageIndex;
		internal set
		{
			if (SetProperty(ref _stageIndex, value))
				OnPropertyChanged(nameof(SlotNumber));
		}
	}

	// 1-based, for the customization block's header — the user counts points from 1, not 0.
	public int SlotNumber => StageIndex + 1;

	public int DpiValue
	{
		get => _dpiValue;
		internal set
		{
			if (SetProperty(ref _dpiValue, value))
				OnPropertyChanged(nameof(Label));
		}
	}

	// False on the last surviving point: the device's stage table cannot be emptied (both firmwares
	// clamp the count to at least one), so the row simply stops offering its delete button.
	public bool CanRemove
	{
		get => _canRemove;
		internal set => SetProperty(ref _canRemove, value);
	}

	// Fixed for the life of the row: the stage colours come from the device's palette, never from
	// persistence. They are display-only, and on TAIPAN they are fixed in the mouse's firmware, so
	// a stored colour is meaningless — restoring one only resurrects an older palette.
	public byte R { get; }
	public byte G { get; }
	public byte B { get; }

	public string HexColor => $"#{R:X2}{G:X2}{B:X2}";
	public string Label    => $"{DpiValue}";

	// Whether the row accepts clicks. False on devices where the active stage is hardware-driven
	// (the toggle then only mirrors the physical DPI button); true where the protocol can set the
	// active stage. See DeviceCardViewModel.SupportsDpiStageSwitch.
	public bool IsInteractive { get; }

	public IRelayCommand SelectCommand { get; }

	public bool IsSelected
	{
		get => _isSelected;
		set => SetProperty(ref _isSelected, value);
	}

	public DpiPresetViewModel(int stageIndex, int dpiValue, byte r, byte g, byte b,
		Action<DpiPresetViewModel> onActivate, bool isInteractive = false)
	{
		StageIndex = stageIndex;
		_dpiValue = dpiValue;
		R = r; G = g; B = b;
		IsInteractive = isInteractive;
		SelectCommand = new RelayCommand(() => onActivate(this));
	}

	// Restores the preset's DPI value from a persisted snapshot. The colour is deliberately NOT
	// restored — see R/G/B above.
	internal void UpdateFromPersisted(int dpiValue) => DpiValue = dpiValue;
}
