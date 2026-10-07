using System;
using CommunityToolkit.Mvvm.Input;
using ProoveGaming.App.Models;

namespace ProoveGaming.App.ViewModels;

public partial class AddDeviceCardViewModel : ViewModelBase
{
    // Needs revisiting (kept on purpose, 2026-09-24): nothing raises this event, because the
    // AddDevice command below is empty, so the whole manual "add device" flow
    // (HomeViewModel.OnDeviceAdded) is unreachable today. Either finish the flow or remove it
    // together with its handler. See docs/plans/fix-repository-audit-findings, D6.
    public event Action<DeviceRecord>? DeviceAdded;

    [RelayCommand]
    private void AddDevice()
    {
    }
}
