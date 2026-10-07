using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ProoveGaming.App.ViewModels;

namespace ProoveGaming.App.Views;

public partial class ProfileView : UserControl
{
    public event Action? BackRequested;

    private ProfileViewModel? _vm;

    public ProfileView()
    {
        InitializeComponent();

        // Placeholders are bound to {DynamicResource} in the XAML so they follow a runtime
        // language switch like every other field on this card.
        var countrySelect = this.FindControl<ProoveGaming.Common.Controls.CountrySelectView>("CountrySelect");
        if (countrySelect != null)
            countrySelect.CountrySelected += OnCountrySelected;

        var datePicker = this.FindControl<ProoveGaming.Common.Controls.DatePickerView>("DateOfBirthPicker");
        if (datePicker != null)
            datePicker.DateSelected += OnDateOfBirthSelected;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm != null)
        {
            _vm.BackRequested -= OnVmBackRequested;
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.User.PropertyChanged -= OnUserPropertyChanged;
        }
        _vm = DataContext as ProfileViewModel;
        if (_vm != null)
        {
            _vm.BackRequested += OnVmBackRequested;
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.User.PropertyChanged += OnUserPropertyChanged;
            SyncCountrySelection();
            SyncDateOfBirthSelection();
        }
    }

    protected override void OnUnloaded(Avalonia.Interactivity.RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        if (_vm != null)
        {
            _vm.BackRequested -= OnVmBackRequested;
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.User.PropertyChanged -= OnUserPropertyChanged;
        }
    }

    private void OnUserPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Models.UserProfile.Country):
                SyncCountrySelection();
                break;
            case nameof(Models.UserProfile.DateOfBirth):
                SyncDateOfBirthSelection();
                break;
        }
    }

    private void SyncCountrySelection()
    {
        var countrySelect = this.FindControl<ProoveGaming.Common.Controls.CountrySelectView>("CountrySelect");
        countrySelect?.SetSelectedCountry(_vm?.User.Country);
    }

    private void SyncDateOfBirthSelection()
    {
        var picker = this.FindControl<ProoveGaming.Common.Controls.DatePickerView>("DateOfBirthPicker");
        picker?.SetSelectedDate(_vm?.User.DateOfBirth);
    }

    private void OnCountrySelected(string country)
    {
        if (_vm == null) return;
        if (string.Equals(_vm.User.Country, country, StringComparison.Ordinal)) return;
        _vm.User.Country = country;
    }

    private void OnDateOfBirthSelected(DateOnly date)
    {
        if (_vm == null) return;
        if (_vm.User.DateOfBirth == date) return;
        _vm.User.DateOfBirth = date;
    }

    private void OnVmBackRequested() => BackRequested?.Invoke();

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm == null) return;

        switch (e.PropertyName)
        {
            case nameof(ProfileViewModel.IsEditNicknameOpen):
                if (_vm.IsEditNicknameOpen)
                    Dispatcher.UIThread.InvokeAsync(
                        () => this.FindControl<TextBox>("NicknameTextBox")?.Focus(),
                        DispatcherPriority.Background);
                break;
            case nameof(ProfileViewModel.IsEditEmailOpen):
                if (_vm.IsEditEmailOpen)
                    Dispatcher.UIThread.InvokeAsync(
                        () => this.FindControl<TextBox>("EmailTextBox")?.Focus(),
                        DispatcherPriority.Background);
                break;
            case nameof(ProfileViewModel.IsNicknameFieldActive):
                if (_vm.IsNicknameFieldActive)
                    Dispatcher.UIThread.InvokeAsync(
                        () => this.FindControl<TextBox>("NicknameTextBox")?.Focus(),
                        DispatcherPriority.Background);
                break;
        }
    }

    private void OnSuggestionClick(object? sender, RoutedEventArgs e)
    {
        // Picking a chip fills the field but leaves keyboard focus on the Button, so the next
        // Backspace lands on the chip and the text looks undeletable. Deferred so the command's
        // binding write happens first, then the caret goes to the end of the applied text.
        // Uses the generated NicknameTextBox field, not FindControl, so an XAML rename breaks
        // the build instead of silently doing nothing.
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            NicknameTextBox.Focus();
            NicknameTextBox.CaretIndex = NicknameTextBox.Text?.Length ?? 0;
        }, DispatcherPriority.Background);
    }

    private void OnNicknameFieldBorderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm?.IsNicknameFieldActive == false)
            _vm.ActivateNicknameFieldCommand.Execute(null);
    }

    private void OnAccountPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_vm != null) _vm.HoveredSection = ProfileSection.Account;
    }

    private void OnSecurityPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_vm != null) _vm.HoveredSection = ProfileSection.Security;
    }

    private void OnNavItemPointerExited(object? sender, PointerEventArgs e)
    {
        if (_vm != null) _vm.HoveredSection = null;
    }

    private async void OnAvatarButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_vm == null) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;

        var imagesFilter = new FilePickerFileType(LocalizationManager.GetString("ProfileView_FilePicker_Images"))
        {
            Patterns = Constants.AllowedExtensions
        };

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationManager.GetString("ProfileView_FilePicker_Title"),
            AllowMultiple = false,
            FileTypeFilter = new[] { imagesFilter }
        });

        if (files.Count == 0) return;
        var uri = files[0].Path;
        if (!uri.IsAbsoluteUri || !uri.IsFile) return;
        await _vm.SetAvatarFromFileAsync(uri.LocalPath);
    }
}
