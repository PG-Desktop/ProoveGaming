using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;

namespace ProoveGaming.App.ViewModels;

public enum ProfileSection { Account, Security }

public partial class ProfileViewModel : ViewModelBase, IProfileSession
{
    public event Action? BackRequested;
    public event Action? SessionTokenInvalid;

    private static readonly IBrush ActiveIconBrush = new SolidColorBrush(Color.Parse("#6357FF"));
    private static readonly IBrush InactiveIconBrush = new SolidColorBrush(Color.FromArgb(128, 190, 188, 234));

    public UserProfile User { get; }

    public string JoinedText =>
        User.JoinedDate.HasValue
            ? string.Format(
                LocalizationManager.GetString("ProfileView_Joined_Format"),
                User.JoinedDate.Value.ToString("dd'/'MM'/'yyyy"))
            : string.Empty;

    // Empty email falls back to the same placeholder copy the Edit Email dialog uses.
    public string EmailDisplayText =>
        string.IsNullOrEmpty(User.Email)
            ? LocalizationManager.GetString("ProfileView_EditEmail_Placeholder")
            : User.Email;

    public string DateOfBirthText
    {
        get => User.DateOfBirth?.ToString("dd'/'MM'/'yyyy") ?? string.Empty;
        set
        {
            if (DateOnly.TryParseExact(value, "dd/MM/yyyy", null,
                    System.Globalization.DateTimeStyles.None, out var date))
                User.DateOfBirth = date;
            else if (string.IsNullOrWhiteSpace(value))
                User.DateOfBirth = null;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAccountSelected))]
    [NotifyPropertyChangedFor(nameof(IsSecuritySelected))]
    [NotifyPropertyChangedFor(nameof(IsAccountHighlighted))]
    [NotifyPropertyChangedFor(nameof(IsSecurityHighlighted))]
    [NotifyPropertyChangedFor(nameof(AccountIconBrush))]
    [NotifyPropertyChangedFor(nameof(SecurityIconBrush))]
    private ProfileSection _selectedSection = ProfileSection.Account;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAccountHighlighted))]
    [NotifyPropertyChangedFor(nameof(IsSecurityHighlighted))]
    private ProfileSection? _hoveredSection;

    public bool IsAccountSelected => SelectedSection == ProfileSection.Account;
    public bool IsSecuritySelected => SelectedSection == ProfileSection.Security;

    public bool IsAccountHighlighted => IsAccountSelected || HoveredSection == ProfileSection.Account;
    public bool IsSecurityHighlighted => IsSecuritySelected || HoveredSection == ProfileSection.Security;

    public IBrush AccountIconBrush => IsAccountSelected ? ActiveIconBrush : InactiveIconBrush;
    public IBrush SecurityIconBrush => IsSecuritySelected ? ActiveIconBrush : InactiveIconBrush;

    private readonly NotificationService _notification;
    private readonly IAuthApiService _authApi;
    private readonly LocalStorageService _localStorage;

    private const long MaxAvatarSizeBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan DefaultProfileSaveDebounce = TimeSpan.FromMilliseconds(800);
    private readonly TimeSpan _profileSaveDebounce;

    private static string AvatarsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ProoveGaming", "avatars");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvatar))]
    private Bitmap? _avatarImage;

    public bool HasAvatar => AvatarImage != null;

    private bool _suppressAutoSave;
    private CancellationTokenSource? _profileSaveCts;
    private static readonly HttpClient AvatarDownloadClient = new();

    public ProfileViewModel(UserProfile user, NotificationService notification, IAuthApiService authApi, LocalStorageService localStorage)
        : this(user, notification, authApi, localStorage, DefaultProfileSaveDebounce)
    {
    }

    // Test seam (InternalsVisibleTo ProoveGaming.App.Tests): a short debounce keeps the
    // auto-save tests timing-insensitive instead of waiting out the real 800 ms, the same
    // approach as SyncCoordinator's internal constructor.
    internal ProfileViewModel(
        UserProfile user,
        NotificationService notification,
        IAuthApiService authApi,
        LocalStorageService localStorage,
        TimeSpan profileSaveDebounce)
    {
        User = user;
        _profileSaveDebounce = profileSaveDebounce;
        _notification = notification;
        _authApi = authApi;
        _localStorage = localStorage;
        User.PropertyChanged += OnUserPropertyChanged;

        // VM-computed strings don't ride DynamicResource, so a language switch has to re-raise
        // them explicitly. Static event on a singleton VM — no unsubscribe needed.
        LocalizationManager.LanguageChanged += () =>
        {
            OnPropertyChanged(nameof(JoinedText));
            OnPropertyChanged(nameof(EmailDisplayText));
        };

        TryLoadExistingAvatar();
    }

    private void OnUserPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(UserProfile.JoinedDate):
                OnPropertyChanged(nameof(JoinedText));
                break;
            case nameof(UserProfile.DateOfBirth):
                OnPropertyChanged(nameof(DateOfBirthText));
                ScheduleProfileSave();
                break;
            case nameof(UserProfile.Email):
                OnPropertyChanged(nameof(EmailDisplayText));
                OnPropertyChanged(nameof(HasEmailChange));
                ConfirmEditEmailCommand.NotifyCanExecuteChanged();
                break;
            case nameof(UserProfile.DisplayName):
                if (!_suppressAutoSave && !string.IsNullOrEmpty(User.SessionToken))
                    try { _localStorage.UpdateSavedAuthNickname(User.DisplayName); } catch { }
                break;
            case nameof(UserProfile.FirstName):
            case nameof(UserProfile.LastName):
            case nameof(UserProfile.Phone):
            case nameof(UserProfile.Country):
                ScheduleProfileSave();
                break;
            case nameof(UserProfile.AvatarUrl):
                if (!_suppressAutoSave) _ = TryDownloadAvatarFromUrlAsync();
                break;
        }
    }

    public async Task LoadFromServerAsync()
    {
        if (string.IsNullOrEmpty(User.SessionToken)) return;

        var result = await _authApi.GetProfileAsync(User.SessionToken);
        if (!result.Success)
        {
            if (!result.IsNetworkError &&
                (result.ErrorCode == AuthErrorCodes.InvalidOrExpiredToken || result.ErrorCode == AuthErrorCodes.TokenRequired))
            {
                SessionTokenInvalid?.Invoke();
            }
            return;
        }
        if (result.Data == null) return;

        _suppressAutoSave = true;
        try
        {
            User.CustomerId = result.Data.CustomerId;
            User.Email = result.Data.Email;
            if (string.IsNullOrEmpty(User.DisplayName))
                User.DisplayName = result.Data.Nickname;
            User.FirstName = result.Data.FirstName ?? string.Empty;
            User.LastName = result.Data.LastName ?? string.Empty;
            User.Phone = result.Data.Telephone ?? string.Empty;
            User.Country = result.Data.Country ?? string.Empty;
            User.DateOfBirth = ParseDateOnly(result.Data.DateBirth);
            User.JoinedDate = ParseDateOnly(result.Data.DateJoined);
            User.AvatarUrl = result.Data.Avatar;
        }
        finally
        {
            _suppressAutoSave = false;
        }

        // If there's no local cached avatar but the server has one, pull it.
        if (AvatarImage == null && !string.IsNullOrEmpty(User.AvatarUrl))
            await TryDownloadAvatarFromUrlAsync();
    }

    private static DateOnly? ParseDateOnly(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var datePart = value.Length >= 10 ? value[..10] : value;
        return DateOnly.TryParseExact(datePart, "yyyy-MM-dd", null,
            System.Globalization.DateTimeStyles.None, out var d) ? d : (DateOnly?)null;
    }

    private void ScheduleProfileSave()
    {
        if (_suppressAutoSave) return;
        if (string.IsNullOrEmpty(User.SessionToken)) return;

        _profileSaveCts?.Cancel();
        var cts = new CancellationTokenSource();
        _profileSaveCts = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_profileSaveDebounce, cts.Token);
                if (cts.IsCancellationRequested) return;
                var result = await _authApi.UpdateProfileAsync(
                    User.SessionToken,
                    User.Email,
                    User.DisplayName,
                    User.FirstName,
                    User.LastName,
                    User.Phone,
                    User.Country,
                    User.DateOfBirth?.ToString("yyyy-MM-dd"));

                // The request itself is not cancellable, so a newer edit can supersede this
                // save while it is still in flight. Re-check before reporting: criterion 17
                // says a superseded save is not a failure, and without this the user would
                // get a toast for a save their next keystroke had already replaced.
                if (cts.IsCancellationRequested) return;

                if (result.Success) return;

                // Password-change-on-web wipes all Session Tokens server-side (v1.7 §11) —
                // the next auto-save is the "next request" that should bounce the user out.
                // The bounce is the whole message; no toast on top of it.
                if (!result.IsNetworkError
                    && (result.ErrorCode == AuthErrorCodes.InvalidOrExpiredToken || result.ErrorCode == AuthErrorCodes.TokenRequired))
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => SessionTokenInvalid?.Invoke());
                    return;
                }

                // Everything else used to be discarded, so a rejected edit looked saved. The
                // typed value deliberately stays on screen: reverting it would yank text out
                // from under a user still editing AND write a bound property from inside a
                // PropertyChanged handler, re-entering ScheduleProfileSave (the update storm
                // CLAUDE.md forbids). The toast reports the failure; the next edit retries.
                ReportProfileSaveFailure(result.IsRateLimited);
            }
            catch (OperationCanceledException)
            {
                // A superseded debounce is not a failure — a newer edit is already queued.
            }
            catch
            {
                ReportProfileSaveFailure(isRateLimited: false);
            }
        });
    }

    // One toast per failed save attempt. The debounce coalesces *scheduling* — a burst of
    // edits cancels every earlier delay, so a burst produces one attempt — and the
    // cancellation re-check after the await covers the rest: a save already in flight when a
    // newer edit supersedes it stays silent. Marshalled like ShowAvatarNotification, since
    // this runs on a pool thread.
    private void ReportProfileSaveFailure(bool isRateLimited)
    {
        var message = LocalizationManager.GetString(ProfileSaveFailureKey(isRateLimited));
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _notification.Show(message));
    }

    /// <summary>Which toast a failed auto-save shows: the shared 429 wording, or the generic one.</summary>
    internal static string ProfileSaveFailureKey(bool isRateLimited) =>
        isRateLimited ? "Api_Error_TooManyRequests" : "ProfileView_Notification_ProfileSaveFailed";

    public void ClearLocalState()
    {
        _profileSaveCts?.Cancel();
        _profileSaveCts = null;

        // Reset transient view state so a fresh sign-in / re-registration opens on Account,
        // not on whatever tab the previous user last touched.
        SelectedSection = ProfileSection.Account;
        HoveredSection = null;

        // Close any open profile dialog so a session-token bounce lands on Welcome cleanly
        // (the dialog backdrops are Window-wide and would otherwise cover the sign-in form).
        IsEditNicknameOpen = false;
        IsEditEmailOpen = false;
        IsDeleteAccountOpen = false;
        EditNicknameText = string.Empty;
        EditNicknameError = string.Empty;
        EditEmailText = string.Empty;
        EditEmailError = string.Empty;
        DeleteAccountPassword = string.Empty;
        DeleteAccountError = string.Empty;
        IsDeleteAccountSubmitting = false;
        IsChangePasswordOpen = false;
        ChangePasswordEmail = string.Empty;
        ChangePasswordEmailError = false;
        ChangePasswordApiError = string.Empty;
        IsChangePasswordSubmitting = false;

        _suppressAutoSave = true;
        try
        {
            User.CustomerId = 0;
            User.DisplayName = string.Empty;
            User.FirstName = string.Empty;
            User.LastName = string.Empty;
            User.Phone = string.Empty;
            User.Country = string.Empty;
            User.DateOfBirth = null;
            User.JoinedDate = null;
            User.Email = string.Empty;
            User.SessionToken = string.Empty;
            User.SessionTokenExpiresAt = null;
            User.AvatarUrl = null;
        }
        finally
        {
            _suppressAutoSave = false;
        }

        var previous = AvatarImage;
        AvatarImage = null;
        previous?.Dispose();

        try
        {
            if (Directory.Exists(AvatarsFolder))
                foreach (var f in Directory.EnumerateFiles(AvatarsFolder, "avatar.*"))
                    File.Delete(f);
        }
        catch
        {
            // Locked file or permission issue is non-fatal; cache will be overwritten on next pick.
        }
    }

    private async Task TryDownloadAvatarFromUrlAsync()
    {
        var url = User.AvatarUrl;
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            var bytes = await AvatarDownloadClient.GetByteArrayAsync(url);
            using var ms = new MemoryStream(bytes);
            var bitmap = new Bitmap(ms);
            var previous = AvatarImage;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                AvatarImage = bitmap;
                previous?.Dispose();
            });
        }
        catch
        {
            // Network/decode errors fall through; placeholder stays.
        }
    }

    private void TryLoadExistingAvatar()
    {
        try
        {
            if (!Directory.Exists(AvatarsFolder)) return;
            var existing = Directory.EnumerateFiles(AvatarsFolder, "avatar.*")
                .FirstOrDefault(f => Constants.AllowedAvatarExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
            if (existing == null) return;
            using var stream = File.OpenRead(existing);
            AvatarImage = new Bitmap(stream);
        }
        catch
        {
            // If the saved file is unreadable, fall back to the placeholder.
        }
    }

    public async Task<bool> SetAvatarFromFileAsync(string sourcePath)
    {
        var ext = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!Constants.AllowedAvatarExtensions.Contains(ext))
        {
            _notification.Show(LocalizationManager.GetString("ProfileView_Notification_AvatarLoadFailed"));
            return false;
        }

        var info = new FileInfo(sourcePath);
        if (!info.Exists)
        {
            _notification.Show(LocalizationManager.GetString("ProfileView_Notification_AvatarLoadFailed"));
            return false;
        }
        if (info.Length > MaxAvatarSizeBytes)
        {
            _notification.Show(LocalizationManager.GetString("ProfileView_Notification_AvatarTooLarge"));
            return false;
        }

        try
        {
            Directory.CreateDirectory(AvatarsFolder);

            foreach (var existing in Directory.EnumerateFiles(AvatarsFolder, "avatar.*"))
                File.Delete(existing);

            var destination = Path.Combine(AvatarsFolder, "avatar" + ext);
            await Task.Run(() => File.Copy(sourcePath, destination, overwrite: true));

            Bitmap bitmap;
            using (var stream = File.OpenRead(destination))
                bitmap = new Bitmap(stream);

            var previous = AvatarImage;
            AvatarImage = bitmap;
            previous?.Dispose();

            if (string.IsNullOrEmpty(User.SessionToken))
            {
                // No active session (e.g. guest): nothing to upload, confirm locally.
                _notification.Show(LocalizationManager.GetString("ProfileView_Notification_AvatarUpdated"));
            }
            else
            {
                // Signed in: defer the success notification until the server confirms the upload.
                _ = UploadAvatarToServerAsync(sourcePath, ext);
            }
            return true;
        }
        catch
        {
            _notification.Show(LocalizationManager.GetString("ProfileView_Notification_AvatarLoadFailed"));
            return false;
        }
    }

    private async Task UploadAvatarToServerAsync(string sourcePath, string extension)
    {
        if (string.IsNullOrEmpty(User.SessionToken)) return;

        var contentType = extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };

        AvatarUploadResponse? result;
        try
        {
            await using var stream = File.OpenRead(sourcePath);
            var fileName = "avatar" + extension;
            result = await _authApi.UploadAvatarAsync(User.SessionToken, stream, fileName, contentType);
        }
        catch
        {
            ShowAvatarNotification("ProfileView_Notification_AvatarUploadFailed");
            return;
        }

        if (result.Success && !string.IsNullOrEmpty(result.AvatarUrl))
        {
            _suppressAutoSave = true;
            try { User.AvatarUrl = result.AvatarUrl; }
            finally { _suppressAutoSave = false; }
            ShowAvatarNotification("ProfileView_Notification_AvatarUpdated");
            return;
        }

        // Auth failure — bubble up to the session-invalid handler so the user is signed out and bounced to Welcome.
        if (!result.IsNetworkError
            && (result.ErrorCode == AuthErrorCodes.InvalidOrExpiredToken || result.ErrorCode == AuthErrorCodes.TokenRequired))
        {
            SessionTokenInvalid?.Invoke();
            return;
        }

        ShowAvatarNotification(ResolveAvatarErrorKey(result));
    }

    /// <summary>
    /// Which toast a failed avatar upload shows (v2.0 §12). 429 is transport-level, so it is
    /// decided before any error_code arm — the endpoint can rate-limit without an envelope
    /// code. UPLOAD_ERROR and UPLOAD_FAILED both mean the server could not store the file and a
    /// retry may well work, so they share the existing retry wording rather than inventing two
    /// near-identical strings; AVATAR_REQUIRED means the multipart part never arrived — a
    /// client bug — and gets the same advice under its own key so it stays distinguishable in
    /// a support screenshot. Static and internal because the upload itself is fire-and-forget
    /// (see <see cref="SetAvatarFromFileAsync"/>): the mapping is the part worth pinning, so
    /// tests drive it directly rather than racing the background task.
    /// </summary>
    internal static string ResolveAvatarErrorKey(ApiResponse response)
    {
        if (response.IsRateLimited)
            return "Api_Error_TooManyRequests";

        return response.ErrorCode switch
        {
            AuthErrorCodes.FileTooLarge => "ProfileView_Notification_AvatarTooLarge",
            AuthErrorCodes.InvalidFileType => "ProfileView_Notification_AvatarInvalidType",
            AuthErrorCodes.UploadError or AuthErrorCodes.UploadFailed => "ProfileView_Notification_AvatarUploadFailed",
            AuthErrorCodes.AvatarRequired => "ProfileView_Notification_AvatarRequired",
            _ => "ProfileView_Notification_AvatarUploadFailed",
        };
    }

    private void ShowAvatarNotification(string resourceKey)
    {
        var message = LocalizationManager.GetString(resourceKey);
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _notification.Show(message));
    }

    [ObservableProperty]
    private bool _isEditNicknameOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PopupHeight))]
    private bool _isNicknameFieldActive;

    public double PopupHeight => IsNicknameFieldActive ? 439 : 308;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmEditNicknameCommand))]
    private string _editNicknameText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditNicknameError))]
    private string _editNicknameError = string.Empty;

    public bool HasEditNicknameError => !string.IsNullOrEmpty(EditNicknameError);

    partial void OnEditNicknameTextChanged(string value) => EditNicknameError = string.Empty;

    // Client-side mirror of v1.7 §10 nickname rules (Latin letters, digits, underscore; 3–20 chars; no spaces).
    // Runs on Confirm so we can surface a specific message before spending a round-trip on the server.
    private static readonly System.Text.RegularExpressions.Regex NicknameAllowedChars =
        new("^[A-Za-z0-9_]+$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex NicknameHasSpaceRegex =
        new(@"\s", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string? ValidateNicknameLocal(string nickname)
    {
        if (string.IsNullOrEmpty(nickname)) return AuthErrorCodes.NicknameRequired;
        if (NicknameHasSpaceRegex.IsMatch(nickname)) return AuthErrorCodes.NicknameHasSpaces;
        if (nickname.Length < 3) return AuthErrorCodes.NicknameTooShort;
        if (nickname.Length > 20) return AuthErrorCodes.NicknameTooLong;
        if (!NicknameAllowedChars.IsMatch(nickname)) return AuthErrorCodes.NicknameInvalidCharacters;
        return null;
    }

    // All shown suggestions come from the server's unified Nickname Generator (v1.7 spec §10);
    // the pool starts empty and stays empty until an API response fills it. The suggestion row
    // in the view is hidden while the pool is empty.
    private string[] _suggestionPool = Array.Empty<string>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNicknameSuggestions))]
    private string[] _nicknameSuggestions = { string.Empty, string.Empty, string.Empty };

    public bool HasNicknameSuggestions =>
        NicknameSuggestions.Any(s => !string.IsNullOrEmpty(s));

    [RelayCommand]
    private void RefreshSuggestions()
    {
        var pool = _suggestionPool.Where(s => !string.IsNullOrEmpty(s)).ToArray();
        var picked = pool.Length >= 3
            ? pool.OrderBy(_ => Guid.NewGuid()).Take(3).ToArray()
            : pool;

        NicknameSuggestions = new[]
        {
            picked.ElementAtOrDefault(0) ?? string.Empty,
            picked.ElementAtOrDefault(1) ?? string.Empty,
            picked.ElementAtOrDefault(2) ?? string.Empty,
        };
    }

    private void ApplyServerNicknameSuggestions(IReadOnlyList<string>? serverSuggestions)
    {
        _suggestionPool = serverSuggestions is { Count: > 0 }
            ? serverSuggestions.ToArray()
            : Array.Empty<string>();
        RefreshSuggestions();
    }

    // Bootstrap the pool from the public api/check_nickname endpoint using the user's current
    // nickname — since it's already registered to them the response is a 409 that carries 5
    // fresh server suggestions. profile/check_nickname wouldn't work here because it ignores
    // the caller's own nickname (spec §10) and would return 200 with no suggestions.
    private async Task LoadServerSuggestionsAsync()
    {
        var seed = User.DisplayName;
        if (string.IsNullOrWhiteSpace(seed)) return;

        try
        {
            var response = await _authApi.CheckNicknameAsync(seed);
            if (response.Suggestions is { Count: > 0 })
                ApplyServerNicknameSuggestions(response.Suggestions);
        }
        catch
        {
            // Offline / server error: leave the pool empty; the suggestions row stays hidden.
        }
    }

    [RelayCommand]
    private void OpenEditNickname()
    {
        EditNicknameText = User.DisplayName;
        // Open in the expanded (439) layout right away — the suggestions area now populates
        // from the server on open, so there's no reason to start collapsed and grow later.
        IsNicknameFieldActive = true;
        IsEditNicknameOpen = true;
        _ = LoadServerSuggestionsAsync();
    }

    [RelayCommand]
    private void CancelEditNickname()
    {
        IsNicknameFieldActive = false;
        IsEditNicknameOpen = false;
    }

    [RelayCommand(CanExecute = nameof(CanConfirmEditNickname))]
    private async Task ConfirmEditNickname()
    {
        var newNickname = EditNicknameText?.Trim() ?? string.Empty;

        var localCode = ValidateNicknameLocal(newNickname);
        if (localCode != null)
        {
            EditNicknameError = ApiErrorMessages.Resolve(localCode)?.Message
                ?? LocalizationManager.GetString("Auth_Error_NicknameRequired");
            return;
        }

        if (string.Equals(newNickname, User.DisplayName, StringComparison.Ordinal))
        {
            IsNicknameFieldActive = false;
            IsEditNicknameOpen = false;
            return;
        }

        if (string.IsNullOrEmpty(User.SessionToken))
            return;

        // v1.7 spec §10 — validate the nickname via api/profile/check_nickname first so a 409
        // response can surface the server-generated alternatives instead of a bare error.
        var check = await _authApi.CheckProfileNicknameAsync(User.SessionToken, newNickname);
        if (!check.Success)
        {
            // Only a 409 carries alternatives, so their presence is the signal to refresh the chips.
            if (check.Suggestions is { Count: > 0 })
                ApplyServerNicknameSuggestions(check.Suggestions);
            HandleNicknameFailure(check);
            return;
        }

        // api/profile/update wants all fields; we send the new nickname plus current values for everything else.
        var result = await _authApi.UpdateProfileAsync(
            User.SessionToken,
            User.Email,
            newNickname,
            User.FirstName,
            User.LastName,
            User.Phone,
            User.Country,
            User.DateOfBirth?.ToString("yyyy-MM-dd"));

        if (!result.Success)
        {
            HandleNicknameFailure(result);
            return;
        }

        _suppressAutoSave = true;
        try { User.DisplayName = newNickname; }
        finally { _suppressAutoSave = false; }
        try { _localStorage.UpdateSavedAuthNickname(newNickname); } catch { }

        IsNicknameFieldActive = false;
        IsEditNicknameOpen = false;
        _notification.Show(LocalizationManager.GetString("ProfileView_Notification_NicknameUpdated"));
    }

    private bool CanConfirmEditNickname() => !string.IsNullOrWhiteSpace(EditNicknameText);

    // Everything that can fail in this dialog has to surface *inside* it. The toast
    // HandleProfileUpdateError shows lives in MainWindow's content and renders behind this
    // popup's window-wide overlay, and no branch of it closes the dialog — so anything routed
    // there is simply invisible: the user presses CONFIRM and nothing happens at all.
    // The one genuine exception is a dead session token, where SessionTokenInvalid tears down
    // profile state and closes the dialog on its way out.
    private void HandleNicknameFailure(ApiResponse response)
    {
        if (!response.IsNetworkError
            && (response.ErrorCode == AuthErrorCodes.InvalidOrExpiredToken
                || response.ErrorCode == AuthErrorCodes.TokenRequired))
        {
            HandleProfileUpdateError(response);
            return;
        }

        // api/profile/update sends every profile field, not just the nickname, so a rejection can
        // name a field the user never touched here (a stored phone the server dislikes, say).
        // Prefer the nickname message, then any other resolved field message, then a localized
        // description of the failure — anything but silence, and never the server's English.
        var resolved = ApiErrorMessages.ResolveAll(response).ToList();
        EditNicknameError =
            resolved.Where(r => r.Field == AuthField.Nickname).Select(r => r.Message).FirstOrDefault()
            ?? resolved.Select(r => r.Message).FirstOrDefault()
            ?? ApiErrorMessages.Describe(response, "ProfileView_EditNickname_Error_Generic");
    }

    [RelayCommand]
    private void ActivateNicknameField() => IsNicknameFieldActive = true;

    [RelayCommand]
    private void SelectSuggestion(string suggestion) => EditNicknameText = suggestion;

    [ObservableProperty]
    private bool _isEditEmailOpen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmEditEmailCommand))]
    [NotifyPropertyChangedFor(nameof(HasEmailChange))]
    private string _editEmailText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditEmailError))]
    private string _editEmailError = string.Empty;

    public bool HasEditEmailError => !string.IsNullOrEmpty(EditEmailError);

    // The dialog opens pre-filled with the current address, so there is nothing to submit until the
    // user actually edits it — Confirm stays disabled while this is false. Case-insensitive: the
    // mailbox is the same either way, so a capitalisation-only edit isn't a change worth a request.
    public bool HasEmailChange =>
        !string.IsNullOrWhiteSpace(EditEmailText)
        && !string.Equals(EditEmailText.Trim(), User.Email?.Trim() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    partial void OnEditEmailTextChanged(string value) => EditEmailError = string.Empty;

    // v1.7 has no email-verification endpoint: api/profile/update takes the new address directly
    // and answers with INVALID_EMAIL / EMAIL_ALREADY_EXISTS. This local check only saves a
    // round-trip on obviously malformed input; the server stays the source of truth.
    private static readonly System.Text.RegularExpressions.Regex EmailRegex =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    [RelayCommand]
    private void OpenEditEmail()
    {
        EditEmailText = User.Email;
        EditEmailError = string.Empty;
        IsEditEmailOpen = true;
    }

    [RelayCommand]
    private void CancelEditEmail()
    {
        EditEmailError = string.Empty;
        IsEditEmailOpen = false;
    }

    [RelayCommand(CanExecute = nameof(CanConfirmEditEmail))]
    private async Task ConfirmEditEmail()
    {
        var newEmail = EditEmailText?.Trim() ?? string.Empty;

        if (!EmailRegex.IsMatch(newEmail))
        {
            EditEmailError = ApiErrorMessages.Resolve(AuthErrorCodes.InvalidEmail)?.Message
                ?? LocalizationManager.GetString("Auth_Error_InvalidEmail");
            return;
        }

        // Guest sessions have nothing to update server-side; keep the address locally.
        if (string.IsNullOrEmpty(User.SessionToken))
        {
            _suppressAutoSave = true;
            try { User.Email = newEmail; }
            finally { _suppressAutoSave = false; }

            IsEditEmailOpen = false;
            _notification.Show(LocalizationManager.GetString("ProfileView_Notification_EmailUpdated"));
            return;
        }

        var result = await _authApi.UpdateProfileAsync(
            User.SessionToken,
            newEmail,
            User.DisplayName,
            User.FirstName,
            User.LastName,
            User.Phone,
            User.Country,
            User.DateOfBirth?.ToString("yyyy-MM-dd"));

        if (!result.Success)
        {
            // Field-level codes (INVALID_EMAIL / EMAIL_ALREADY_EXISTS) belong in the dialog next to
            // the input; everything else (network, token, generic) goes through the shared handler.
            var fieldMessage = ApiErrorMessages.ResolveAll(result)
                .Where(r => r.Field == AuthField.Email)
                .Select(r => r.Message)
                .FirstOrDefault();
            if (!result.IsNetworkError && fieldMessage != null)
            {
                EditEmailError = fieldMessage;
                return;
            }

            HandleProfileUpdateError(result);
            return;
        }

        _suppressAutoSave = true;
        try { User.Email = newEmail; }
        finally { _suppressAutoSave = false; }

        EditEmailError = string.Empty;
        IsEditEmailOpen = false;
        _notification.Show(LocalizationManager.GetString("ProfileView_Notification_EmailUpdated"));
    }

    private bool CanConfirmEditEmail() => HasEmailChange;

    private void HandleProfileUpdateError(ApiResponse result)
    {
        if (result.IsNetworkError)
        {
            _notification.Show(ApiErrorMessages.Describe(result, "Api_Error_Network"));
            return;
        }

        if (result.ErrorCode == AuthErrorCodes.InvalidOrExpiredToken || result.ErrorCode == AuthErrorCodes.TokenRequired)
        {
            SessionTokenInvalid?.Invoke();
            return;
        }

        var firstResolved = ApiErrorMessages.ResolveAll(result).Select(r => r.Message).FirstOrDefault();
        _notification.Show(firstResolved ?? ApiErrorMessages.Describe(result, "ProfileView_UpdateProfile_Error_Generic"));
    }

    [RelayCommand]
    private void NavigateBack() => BackRequested?.Invoke();

    [RelayCommand]
    private void SelectAccount() => SelectedSection = ProfileSection.Account;

    [RelayCommand]
    private void SelectSecurity() => SelectedSection = ProfileSection.Security;

    // ── Change password (Security → v1.7 spec §11, revised flow) ──────────
    // Same flow as the Welcome forgot-password panel: an in-app email step that calls
    // POST api/forgotten; the password itself is changed on the website via the emailed
    // link. api/profile/password_link is no longer called by the launcher.

    [ObservableProperty]
    private bool _isChangePasswordOpen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendChangePasswordResetCommand))]
    private string _changePasswordEmail = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendChangePasswordResetCommand))]
    private bool _isChangePasswordSubmitting;

    [ObservableProperty]
    private bool _changePasswordEmailError;

    [ObservableProperty]
    private string _changePasswordApiError = string.Empty;

    [RelayCommand]
    private void ChangePassword()
    {
        ChangePasswordEmail = User.Email;
        ChangePasswordEmailError = false;
        ChangePasswordApiError = string.Empty;
        IsChangePasswordSubmitting = false;
        IsChangePasswordOpen = true;
    }

    [RelayCommand]
    private void CancelChangePassword()
    {
        IsChangePasswordOpen = false;
        ChangePasswordEmailError = false;
        ChangePasswordApiError = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanSendChangePasswordReset))]
    private async Task SendChangePasswordResetAsync()
    {
        ChangePasswordApiError = string.Empty;
        var email = ChangePasswordEmail.Trim();
        if (!EmailRegex.IsMatch(email))
        {
            ChangePasswordEmailError = true;
            return;
        }
        ChangePasswordEmailError = false;
        IsChangePasswordSubmitting = true;
        try
        {
            ApiResponse response;
            try
            {
                response = await _authApi.ForgottenPasswordAsync(email);
            }
            catch
            {
                ChangePasswordApiError = LocalizationManager.GetString("WelcomeView_Forgot_Error_Generic");
                return;
            }

            if (response.Success)
            {
                IsChangePasswordOpen = false;
                _notification.Show(LocalizationManager.GetString("WelcomeView_Notification_ResetLinkSent"));
                return;
            }

            var (isFieldError, message) = ForgottenPasswordErrors.Resolve(response);
            ChangePasswordEmailError = isFieldError;
            ChangePasswordApiError = message;
        }
        finally
        {
            IsChangePasswordSubmitting = false;
        }
    }

    private bool CanSendChangePasswordReset() =>
        !IsChangePasswordSubmitting && !string.IsNullOrEmpty(ChangePasswordEmail);

    // ── Delete account (Security → v1.7 spec §12) ─────────────────────────

    [ObservableProperty]
    private bool _isDeleteAccountOpen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmDeleteAccountCommand))]
    private string _deleteAccountPassword = string.Empty;

    [ObservableProperty]
    private string _deleteAccountError = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmDeleteAccountCommand))]
    private bool _isDeleteAccountSubmitting;

    [RelayCommand]
    private void OpenDeleteAccount()
    {
        DeleteAccountPassword = string.Empty;
        DeleteAccountError = string.Empty;
        IsDeleteAccountSubmitting = false;
        IsDeleteAccountOpen = true;
    }

    [RelayCommand]
    private void CancelDeleteAccount()
    {
        IsDeleteAccountOpen = false;
        DeleteAccountPassword = string.Empty;
        DeleteAccountError = string.Empty;
    }

    [RelayCommand(CanExecute = nameof(CanConfirmDeleteAccount))]
    private async Task ConfirmDeleteAccount()
    {
        if (string.IsNullOrEmpty(User.SessionToken)) return;

        DeleteAccountError = string.Empty;
        IsDeleteAccountSubmitting = true;
        try
        {
            var result = await _authApi.DeleteAccountAsync(User.SessionToken, DeleteAccountPassword);

            if (result.Success)
            {
                // Server invalidated the token as part of deletion — send the app back to Welcome via
                // the same path SessionTokenInvalid uses (ClearLocalState closes the dialog).
                SessionTokenInvalid?.Invoke();
                return;
            }

            if (result.IsNetworkError)
            {
                DeleteAccountError = ApiErrorMessages.Describe(result, "ProfileView_DeleteAccount_Error_Generic");
                return;
            }

            switch (RecognizedDeleteAccountCode(result))
            {
                case AuthErrorCodes.InvalidPassword:
                    DeleteAccountError = LocalizationManager.GetString("ProfileView_DeleteAccount_Error_InvalidPassword");
                    break;
                case AuthErrorCodes.PasswordRequired:
                    DeleteAccountError = LocalizationManager.GetString("ProfileView_DeleteAccount_Error_PasswordRequired");
                    break;
                case AuthErrorCodes.InvalidOrExpiredToken:
                case AuthErrorCodes.TokenRequired:
                    SessionTokenInvalid?.Invoke();
                    return;
                default:
                    DeleteAccountError = ApiErrorMessages.Describe(result, "ProfileView_DeleteAccount_Error_Generic");
                    break;
            }
        }
        finally
        {
            IsDeleteAccountSubmitting = false;
        }
    }

    // The recognized code is not necessarily errors[0]: an envelope like
    // [{UNKNOWN_CODE}, {INVALID_PASSWORD}] used to fall into the generic branch because only
    // the first entry was read. Scan top-level then every entry for a code this switch
    // handles, and keep the old first-entry read as the fallback so the default branch still
    // has something to report.
    private static string? RecognizedDeleteAccountCode(ApiResponse response)
    {
        if (IsHandledDeleteAccountCode(response.ErrorCode))
            return response.ErrorCode;
        if (response.Errors is { } errors)
        {
            foreach (var error in errors)
            {
                if (IsHandledDeleteAccountCode(error?.ErrorCode))
                    return error!.ErrorCode;
            }
        }
        return response.ErrorCode
            ?? (response.Errors is { Count: > 0 } ? response.Errors[0]?.ErrorCode : null);
    }

    private static bool IsHandledDeleteAccountCode(string? code) => code is
        AuthErrorCodes.InvalidPassword
        or AuthErrorCodes.PasswordRequired
        or AuthErrorCodes.InvalidOrExpiredToken
        or AuthErrorCodes.TokenRequired;

    private bool CanConfirmDeleteAccount() =>
        !IsDeleteAccountSubmitting && !string.IsNullOrEmpty(DeleteAccountPassword);
}
