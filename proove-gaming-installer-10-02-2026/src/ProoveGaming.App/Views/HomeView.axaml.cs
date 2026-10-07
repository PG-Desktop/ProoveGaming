using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ProoveGaming.App.ViewModels;

namespace ProoveGaming.App.Views;

public partial class HomeView : UserControl
{
    public event Action? GuestRequested;
    public event Action? ProfileRequested;
    public event Action? InformationRequested;
    public event Action? SettingsRequested;
    public event Action? LogoutRequested;
    public event Action<DeviceCardViewModel>? DeviceDetailsRequested;

    private HomeViewModel? _vm;
    private bool _openSettingsSubscriptionsInitialized;

    public HomeView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
        {
            _vm.Devices.CollectionChanged -= OnDevicesCollectionChanged;
            foreach (var card in _vm.Devices.OfType<DeviceCardViewModel>())
            {
                UnsubscribeOpenSettings(card);
            }
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.LogoutRequested -= OnVmLogoutRequested;
        }

        _vm = DataContext as HomeViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.Devices.CollectionChanged += OnDevicesCollectionChanged;
            _vm.LogoutRequested += OnVmLogoutRequested;
            _openSettingsSubscriptionsInitialized = false;
        }
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _vm?.Start();
        RefreshOpenSettingsSubscriptions();
        this.FindControl<Button>("GamesTabButton")!.AddHandler(PointerEnteredEvent, OnGamesTabPointerEntered);
        this.FindControl<Button>("GamesTabButton")!.AddHandler(PointerExitedEvent, OnGamesTabPointerExited);
        this.FindControl<Button>("StatisticsTabButton")!.AddHandler(PointerEnteredEvent, OnStatisticsTabPointerEntered);
        this.FindControl<Button>("StatisticsTabButton")!.AddHandler(PointerExitedEvent, OnStatisticsTabPointerExited);
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        _vm?.Stop();
        if (_vm != null)
        {
            _vm.Devices.CollectionChanged -= OnDevicesCollectionChanged;
            foreach (var card in _vm.Devices.OfType<DeviceCardViewModel>())
            {
                UnsubscribeOpenSettings(card);
            }
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.LogoutRequested -= OnVmLogoutRequested;
        }
    }
    private void OnProfileButtonClick(object? sender, RoutedEventArgs e) => ProfilePopup.IsOpen = !ProfilePopup.IsOpen;

    private void OnSettingsButtonClick(object? sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

    private void OnProfileMenuProfileClick(object? sender, RoutedEventArgs e)
    {
        ProfilePopup.IsOpen = false;
        ProfileRequested?.Invoke();
    }

    private void OnVmLogoutRequested()
    {
        ProfilePopup.IsOpen = false;
        LogoutRequested?.Invoke();
    }

    // Shared by the profile and guest dropdowns: close whichever is up, then open the dialog.
    private void OnMenuContactUsClick(object? sender, RoutedEventArgs e)
    {
        ProfilePopup.IsOpen = false;
        GuestPopup.IsOpen = false;
        if (DataContext is HomeViewModel vm)
            vm.OpenContactUs();
    }

    // Shared by both dropdowns like Contact Us: close whichever is up, then navigate.
    private void OnMenuInformationClick(object? sender, RoutedEventArgs e)
    {
        ProfilePopup.IsOpen = false;
        GuestPopup.IsOpen = false;
        InformationRequested?.Invoke();
    }
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeViewModel.IsLogoutConfirmOpen))
        {
            // Logout opens via the profile dropdown — dismiss the dropdown so only the dialog shows.
            if (_vm?.IsLogoutConfirmOpen == true && ProfilePopup.IsOpen)
                ProfilePopup.IsOpen = false;
        }
    }

    private void SubscribeOpenSettings(DeviceCardViewModel card)
    {
        card.OpenSettingsRequested -= OnCardOpenSettingsRequested;
        card.OpenSettingsRequested += OnCardOpenSettingsRequested;
    }

    private void UnsubscribeOpenSettings(DeviceCardViewModel card)
    {
        card.OpenSettingsRequested -= OnCardOpenSettingsRequested;
    }

    private void RefreshOpenSettingsSubscriptions()
    {
        if (_vm is null)
        {
            return;
        }

        if (_openSettingsSubscriptionsInitialized)
        {
            return;
        }

        foreach (var card in _vm.Devices.OfType<DeviceCardViewModel>())
        {
            SubscribeOpenSettings(card);
        }

        _openSettingsSubscriptionsInitialized = true;
    }

    private void OnCardOpenSettingsRequested(DeviceCardViewModel card)
    {
        // The menu item is already hidden while disconnected; this keeps the command itself
        // from being a second way in if that binding ever changes.
        if (!card.Connected)
            return;

        DeviceDetailsRequested?.Invoke(card);
    }

    private void OnGuestButtonClick(object? sender, RoutedEventArgs e) =>
        GuestPopup.IsOpen = !GuestPopup.IsOpen;

    private void OnGuestMenuSignUpClick(object? sender, RoutedEventArgs e)
    {
        GuestPopup.IsOpen = false;
        GuestRequested?.Invoke();
    }

    private void OnDeviceCardTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: DeviceCardViewModel vm })
            return;

        // Don't navigate if the tap originated from within the dots menu button or overlay
        if (e.Source is StyledElement source && IsInsideDotsMenu(source))
            return;

        // A disconnected device has nothing to configure — its details page would show stale
        // settings and any write would fail, so the card is not a way in while it is offline.
        // The dots menu stays usable (Delete), which is why this guard is here and not on the
        // whole card.
        if (!vm.Connected)
            return;

        DeviceDetailsRequested?.Invoke(vm);
    }

    private void OnDevicesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (var removed in e.OldItems.OfType<DeviceCardViewModel>())
            {
                UnsubscribeOpenSettings(removed);
            }
        }

        if (e.NewItems != null)
        {
            foreach (var added in e.NewItems.OfType<DeviceCardViewModel>())
            {
                SubscribeOpenSettings(added);
            }
        }
    }

    private static bool IsInsideDotsMenu(StyledElement? source)
    {
        while (source != null)
        {
            if (source is Button { Name: "DotsButton" })
                return true;
            if (source is Control { Name: "DotsOverlay" })
                return true;
            source = source.Parent;
        }
        return false;
    }

    private void OnGamesTabPointerEntered(object? sender, PointerEventArgs e) => GamesHoverBg.IsVisible = true;
    private void OnGamesTabPointerExited(object? sender, PointerEventArgs e) => GamesHoverBg.IsVisible = false;
    private void OnStatisticsTabPointerEntered(object? sender, PointerEventArgs e) => StatisticsHoverBg.IsVisible = true;
    private void OnStatisticsTabPointerExited(object? sender, PointerEventArgs e) => StatisticsHoverBg.IsVisible = false;
}
