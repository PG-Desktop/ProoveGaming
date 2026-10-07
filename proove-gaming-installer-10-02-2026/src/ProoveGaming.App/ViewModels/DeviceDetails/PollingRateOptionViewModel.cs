using System;
using CommunityToolkit.Mvvm.Input;

namespace ProoveGaming.App.ViewModels;

public partial class PollingRateOptionViewModel : ViewModelBase
{
    private bool _isSelected;

    public int Hz { get; }
    public string Label { get; }
    public IRelayCommand SelectCommand { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public PollingRateOptionViewModel(int hz, Action<PollingRateOptionViewModel> onSelect)
    {
        Hz = hz;
        Label = $"{hz} Hz";
        SelectCommand = new RelayCommand(() => onSelect(this));
    }
}