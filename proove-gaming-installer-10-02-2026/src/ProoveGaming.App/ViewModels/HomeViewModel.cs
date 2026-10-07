using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProoveGaming.App.Classes;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using ProoveGaming.GrpcDependencies.Device;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ProoveGaming.App.ViewModels;

public partial class HomeViewModel : ViewModelBase, IOwnerScopedState
{
    private readonly IUsbHidDeviceService _usbHidDeviceService;
    private readonly SessionService _session;
    private readonly LocalStorageService _localStorage;
    private readonly IServiceProvider _services;
    private readonly UserProfile _user;
    private readonly ILogger<HomeViewModel> _logger;
    private readonly SystemNotificationService _systemNotifications;
    private readonly ICurrentOwnerProvider _ownerProvider;

    // Storage owner for devices and notifications: the guest session or the signed-in user.
    // Null on the welcome screen (nothing to persist under yet).
    private SettingsOwner? CurrentOwner => _ownerProvider.GetCurrent();
    private readonly AudioMuteMonitor? _audioMuteMonitor;
    private static readonly TimeSpan BatteryRetryDelay = TimeSpan.FromSeconds(3);
    private const int BatteryRetryCount = 3;
    // A battery does not move faster than this, and each tick is one gRPC call per battery device.
    private static readonly TimeSpan BatteryPollInterval = TimeSpan.FromSeconds(60);
    private DispatcherTimer? _batteryPollTimer;
    private bool _batteryPolling;

    private readonly SemaphoreSlim _eventGate = new(1, 1);

    private readonly ConcurrentDictionary<(int vid, int pid), byte> _knownNoBattery = new();

    // Consecutive polls that came back with no reading, per device. A single miss is NOT evidence
    // that a battery is gone — see the clearing branch in PollBatteriesAsync for why -1 is
    // ambiguous — so the indicator is only cleared once a second poll agrees. A known battery push
    // (OnBatteryChanged) resets the streak too: it is a successful reading by another name, and
    // without the reset two misses SEPARATED by a push would still count as consecutive. Plain
    // Dictionary rather than Concurrent: both writers run on the UI thread (the poll directly, the
    // push inside its Dispatcher.Post), and overlapping poll ticks are prevented by _batteryPolling.
    private readonly Dictionary<(int vid, int pid, string key), int> _batteryMissStreak = new();

    // Two ticks = two minutes at the current poll interval. Long enough that a device gate held
    // through a whole key-table transfer cannot clear a healthy device's indicator, short enough
    // that a device with no push channel does not show a stale level for long. TAIPAN does not wait
    // for this at all: its absent-reply push clears the indicator immediately.
    private const int BatteryMissesBeforeClearing = 2;

    // The lowest band already announced per device (20 or 10; no entry = none). Keyed by the
    // SETTINGS identity (see LowBatteryIdentity), NOT by card instance, and never cleared by a
    // null reading: a sleeping mouse blanks its level on every doze, and per-card state armed a
    // fresh toast on every wake-up — the "toasts at random moments" report (QA 2026-08-20).
    // Cleared only by a real recharge (LowBatteryDecision's re-arm) or by the card leaving for
    // good.
    private readonly Dictionary<(int vid, int pid, string key), int> _lowBatteryAnnouncedBand = new();

    // The stable identity the announcement state lives under. The SETTINGS identity, not the
    // routing identity (VendorId/ProductId/DeviceKey): ApplyDeviceIdentity rewrites those on every
    // transport switch of a merged-transport product (TAIPAN dongle↔Bluetooth) and on a receiver
    // port move (the Container ID is port-derived for devices with no USB serial), and a key that
    // changes loses the "already announced" entry — one extra toast per new identity, the same
    // symptom this map exists to stop (PR #117 M1). The VENDOR is part of that: RAMPAGE's receiver
    // enumerates under vendor 1389 against the board's 10473, so keying on the routing vendor
    // re-armed the toast on a cable↔dongle switch too. _batteryMissStreak deliberately keeps the
    // routing key: a miss streak is a property of the specific link, not of the product.
    private static (int Vid, int Pid, string Key) LowBatteryIdentity(DeviceCardViewModel vm) =>
        (vm.SettingsVendorId, vm.SettingsProductId, vm.SettingsDeviceKey);

    // Merges a stored announcement band onto a card: both the card's persisted mirror and the
    // identity-keyed map take the LOWER band (10 suppresses both bands), so a restored row, the
    // sign-in adopt pass and a second row sharing one settings identity can only ever make the
    // guard stricter, never re-arm it. UI-thread only, like every other writer of the map.
    private void SeedLowBatteryBand(DeviceCardViewModel vm, int storedBand)
    {
        if (storedBand <= 0) return;

        if (vm.LowBatteryAnnouncedBand == 0 || storedBand < vm.LowBatteryAnnouncedBand)
            vm.LowBatteryAnnouncedBand = storedBand;

        var identity = LowBatteryIdentity(vm);
        if (!_lowBatteryAnnouncedBand.TryGetValue(identity, out var known) || storedBand < known)
            _lowBatteryAnnouncedBand[identity] = storedBand;
    }

    private DeviceCardViewModel? _pendingDeleteCard;
    public DeviceCardViewModel? PendingDeleteCard
    {
        get => _pendingDeleteCard;
        private set => SetProperty(ref _pendingDeleteCard, value);
    }

    private bool _isNotificationsOpen;
    public bool IsNotificationsOpen
    {
        get => _isNotificationsOpen;
        private set
        {
            // Re-label date groups on open: headers are computed when a group is created, so
            // without this a panel opened after midnight still shows yesterday's items under
            // "Today" until the next notification arrives.
            if (SetProperty(ref _isNotificationsOpen, value) && value)
                RefreshNotificationGroupHeaders();
        }
    }

    private void RefreshNotificationGroupHeaders() =>
        NotificationGroupViewModel.RelabelAll(NotificationGroups, GroupHeaderFor);

    public IRelayCommand ConfirmDeleteCommand { get; }
    public IRelayCommand CancelDeleteCommand { get; }
    public IRelayCommand ToggleNotificationsCommand { get; }
    public IRelayCommand CloseNotificationsCommand { get; }
    public IRelayCommand MarkAllNotificationsReadCommand { get; }
    public IRelayCommand DeleteAllNotificationsCommand { get; }
    public IRelayCommand LogoutCommand { get; }
    public IRelayCommand ConfirmLogoutCommand { get; }
    public IRelayCommand CancelLogoutCommand { get; }

    public event Action? LogoutRequested;

    private bool _isLogoutConfirmOpen;
    public bool IsLogoutConfirmOpen
    {
        get => _isLogoutConfirmOpen;
        private set => SetProperty(ref _isLogoutConfirmOpen, value);
    }

    // ── Contact Us dialog ─────────────────────────────────────────────────────
    // Opened from the profile/guest dropdowns; rendered by MainWindow's DialogOverlay like the
    // logout dialog. Every open starts a fresh request — topic and message are cleared on open
    // (QA 2026-08-21) — while the email keeps its last value or pre-fills from the signed-in
    // profile.
    public IRelayCommand CancelContactUsCommand { get; }
    public IAsyncRelayCommand SendContactUsCommand { get; }

    internal const int ContactUsTopicMin = 3;
    internal const int ContactUsTopicMax = 100;
    internal const int ContactUsMessageMin = 10;
    internal const int ContactUsMessageMax = 2000;

    // Same shape the auth flows use (WelcomeViewModel / ProfileViewModel).
    private static readonly System.Text.RegularExpressions.Regex ContactUsEmailRegex =
        new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly NotificationIcon ContactUsSentIcon = NotificationIcon.ForType(NotificationTypes.Contact);

    private bool _isContactUsOpen;
    public bool IsContactUsOpen
    {
        get => _isContactUsOpen;
        private set => SetProperty(ref _isContactUsOpen, value);
    }

    private string _contactUsEmail = string.Empty;
    public string ContactUsEmail
    {
        get => _contactUsEmail;
        set { if (SetProperty(ref _contactUsEmail, value)) OnContactUsFieldChanged(); }
    }

    private string _contactUsTopic = string.Empty;
    public string ContactUsTopic
    {
        get => _contactUsTopic;
        set { if (SetProperty(ref _contactUsTopic, value)) OnContactUsFieldChanged(); }
    }

    private string _contactUsMessage = string.Empty;
    public string ContactUsMessage
    {
        get => _contactUsMessage;
        set { if (SetProperty(ref _contactUsMessage, value)) OnContactUsFieldChanged(); }
    }

    public string ContactUsTopicCounter => $"{_contactUsTopic.Length}/{ContactUsTopicMax}";
    public string ContactUsMessageCounter => $"{_contactUsMessage.Length}/{ContactUsMessageMax}";

    // Drive the pgInputHost "filled" class: a field that holds content keeps the accent
    // border even unfocused (the filled-state mockup).
    public bool ContactUsEmailFilled => !string.IsNullOrEmpty(_contactUsEmail);
    public bool ContactUsTopicFilled => !string.IsNullOrEmpty(_contactUsTopic);
    public bool ContactUsMessageFilled => !string.IsNullOrEmpty(_contactUsMessage);

    private string? _contactUsError;
    public string? ContactUsError
    {
        get => _contactUsError;
        private set
        {
            if (SetProperty(ref _contactUsError, value))
                OnPropertyChanged(nameof(HasContactUsError));
        }
    }

    public bool HasContactUsError => !string.IsNullOrEmpty(_contactUsError);

    // Set for the duration of the send so a second click cannot double-post. Not
    // AsyncRelayCommand.IsRunning: that is still true inside the method's finally block, so a
    // notification raised there would latch SEND disabled after a failed send.
    private bool _contactUsSending;

    /// <summary>Drives the sending overlay: dimmed content + the 4-frame pixel loader.</summary>
    public bool IsContactUsSending => _contactUsSending;

    // The loader mockup ships four discrete frames at 250 ms each; the send loops them for
    // the real request duration with a minimum of one full revolution (1 s) so a fast
    // response doesn't flash. A hung server is bounded by ContactUsSendTimeout.
    private const int ContactUsSpinnerFrameMs = 250;
    private const int ContactUsSpinnerMinFrames = 4;

    // Bounds the inert-dialog window on a black-holed connection. The timeout surfaces
    // through AuthApiService's TaskCanceledException → IsNetworkError path, i.e. the Failed
    // branch of DecideContactUsOutcome.
    private static readonly TimeSpan ContactUsSendTimeout = TimeSpan.FromSeconds(15);

    private int _contactUsSpinnerFrame;
    private int ContactUsSpinnerFrame
    {
        set
        {
            if (_contactUsSpinnerFrame == value) return;
            _contactUsSpinnerFrame = value;
            OnPropertyChanged(nameof(ContactUsSpinnerFrame0));
            OnPropertyChanged(nameof(ContactUsSpinnerFrame1));
            OnPropertyChanged(nameof(ContactUsSpinnerFrame2));
            OnPropertyChanged(nameof(ContactUsSpinnerFrame3));
        }
    }

    public bool ContactUsSpinnerFrame0 => _contactUsSpinnerFrame == 0;
    public bool ContactUsSpinnerFrame1 => _contactUsSpinnerFrame == 1;
    public bool ContactUsSpinnerFrame2 => _contactUsSpinnerFrame == 2;
    public bool ContactUsSpinnerFrame3 => _contactUsSpinnerFrame == 3;

    // SEND is bound to this, so it greys out while the request runs and while the form is
    // invalid — the mockup's disabled state.
    public bool CanSendContactUs =>
        !_contactUsSending && ContactUsFormValid(_contactUsEmail, _contactUsTopic, _contactUsMessage);

    /// <summary>
    /// The SEND gate: a well-formed email plus a topic and message inside the contract's
    /// 3–100 / 10–2000 ranges (v2.0 §20). The minimums used to be "non-blank", which left
    /// TOPIC_TOO_SHORT / MESSAGE_TOO_SHORT genuinely reachable — the server rejected those
    /// sends anyway, so gating them here turns the codes into a defence-in-depth net instead
    /// of a user-visible round-trip. Static, like <see cref="DeriveHidTransports"/> — the
    /// constructor needs the full service graph, so tests drive the rule directly.
    /// </summary>
    internal static bool ContactUsFormValid(string email, string topic, string message) =>
        ContactUsEmailRegex.IsMatch(email) &&
        topic.Trim().Length >= ContactUsTopicMin && topic.Length <= ContactUsTopicMax &&
        message.Trim().Length >= ContactUsMessageMin && message.Length <= ContactUsMessageMax;

    /// <summary>
    /// What the email field holds when the dialog opens: a non-empty draft always wins (the user
    /// may have deliberately typed a different reply address), otherwise the owner's profile email
    /// pre-fills. Static like <see cref="ContactUsFormValid"/> so tests drive the rule directly —
    /// the guard itself was the defect surface twice (review R1 S3, PR #118 m2).
    /// </summary>
    internal static string ContactUsEmailOnOpen(string currentDraft, string? profileEmail) =>
        string.IsNullOrEmpty(currentDraft) && !string.IsNullOrEmpty(profileEmail)
            ? profileEmail
            : currentDraft;

    /// <summary>
    /// The ✕/CANCEL gate (review R1 S2): the whole dialog is inert while the send loader runs,
    /// so a close mid-"send" cannot end in the success toast. Static per PR #118 m3 so the gate
    /// has a regression test.
    /// </summary>
    internal static bool ContactUsCanCancel(bool sending) => !sending;

    /// <summary>What a completed send meant for the user — the two UI branches.</summary>
    internal enum ContactUsOutcome { Delivered, Failed }

    /// <summary>
    /// Maps the send's <see cref="ApiResponse"/> onto the two outcome branches: a 2xx success
    /// envelope is a delivery, everything else (network error, timeout, 404, 5xx, a 2xx whose
    /// body is not the JSON envelope, a well-formed success:false) is a real failure the user
    /// must see and retry. The api/contact route is part of API v2.0, so "the backend answered
    /// garbage" no longer degrades to an "accepted" white lie (docs/plans/add-api-v2-sync).
    /// Static like <see cref="ContactUsFormValid"/> so tests drive the rule directly.
    /// </summary>
    internal static ContactUsOutcome DecideContactUsOutcome(ApiResponse response) =>
        response.Success ? ContactUsOutcome.Delivered : ContactUsOutcome.Failed;

    /// <summary>
    /// Runs <paramref name="send"/> under a timeout owned by this caller and never throws for
    /// that timeout: it comes back as a network-error response, so the dialog shows its Failed
    /// branch instead of spinning forever. The caller's own cancellation still propagates.
    /// Needed because ApiHttpClient lets every caller-token cancellation propagate
    /// (docs/plans/fix-repository-audit-findings, A9), and this timeout IS a caller token.
    /// Static so tests drive it directly.
    /// </summary>
    internal static async Task<ApiResponse> SendWithTimeoutAsync(
        Func<CancellationToken, Task<ApiResponse>> send, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await send(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ApiResponse { Success = false, IsNetworkError = true, ErrorCode = ClientErrorCodes.Timeout };
        }
    }

    private void OnContactUsFieldChanged()
    {
        // A failed send leaves ContactUs_SendFailed on screen with the draft intact; the next
        // edit is the retry starting, so the stale error clears (mirrors the reset on open).
        ContactUsError = null;
        OnPropertyChanged(nameof(ContactUsTopicCounter));
        OnPropertyChanged(nameof(ContactUsMessageCounter));
        OnPropertyChanged(nameof(ContactUsEmailFilled));
        OnPropertyChanged(nameof(ContactUsTopicFilled));
        OnPropertyChanged(nameof(ContactUsMessageFilled));
        OnPropertyChanged(nameof(CanSendContactUs));
    }

    public void OpenContactUs()
    {
        ContactUsEmail = ContactUsEmailOnOpen(_contactUsEmail, _user.Email);
        ContactUsTopic = string.Empty;
        ContactUsMessage = string.Empty;
        ContactUsError = null;
        IsContactUsOpen = true;
    }

    private async Task SendContactUsAsync(CancellationToken ct)
    {
        if (_contactUsSending || !ContactUsFormValid(_contactUsEmail, _contactUsTopic, _contactUsMessage))
            return;

        _contactUsSending = true;
        OnPropertyChanged(nameof(IsContactUsSending));
        OnPropertyChanged(nameof(CanSendContactUs));
        try
        {
            var topic = _contactUsTopic.Trim();
            var message = _contactUsMessage.Trim();

            // Nothing cancels the command interactively today (the dialog is inert during the
            // send — review R1 S2), but the plumbing is real: ContactUsSendTimeout bounds a
            // black-holed connection, surfacing as the Failed branch (see SendWithTimeoutAsync).
            // Bearer token only when a session exists — guests post anonymously.
            var authApi = _services.GetRequiredService<IAuthApiService>();
            var email = _contactUsEmail;
            var sessionToken = string.IsNullOrEmpty(_user.SessionToken) ? null : _user.SessionToken;
            var sendTask = SendWithTimeoutAsync(
                token => authApi.ContactUsAsync(email, topic, message, sessionToken, token),
                ContactUsSendTimeout,
                ct);

            // The 4-frame pixel loader runs for the real request duration, minimum one full
            // revolution so a fast response doesn't flash.
            var frame = 0;
            do
            {
                ContactUsSpinnerFrame = frame % 4;
                await Task.Delay(ContactUsSpinnerFrameMs);
                frame++;
            }
            while (!sendTask.IsCompleted || frame < ContactUsSpinnerMinFrames);

            // Every transport failure, this method's own timeout included, comes back as a
            // response with IsNetworkError set.
            var response = await sendTask;
            var outcome = DecideContactUsOutcome(response);

            if (outcome == ContactUsOutcome.Failed)
            {
                // Dialog stays open with the draft intact; the line under the form carries the
                // reason (already rendered by MainWindow). ContactUsErrors picks the wording
                // from the server's error_code / HTTP 429, so an anti-spam refusal no longer
                // tells the user to "try again" straight back into the same limit. Cleared on
                // the next edit.
                ContactUsError = LocalizationManager.GetString(ContactUsErrors.ResolveKey(response));
                return;
            }

            IsContactUsOpen = false;
            ContactUsTopic = string.Empty;
            ContactUsMessage = string.Empty;
            ContactUsError = null;

            // Two surfaces, like the other flows: the transient green-check toast
            // (MainWindow's NotificationGrid — the mockup's chamfered 432px bar) and a bell
            // entry that keeps the confirmation in the history. Only one success wording
            // exists: "sent", because reaching this branch means the backend acknowledged
            // the message (docs/plans/add-api-v2-sync retired the degraded "accepted" mode).
            _services.GetService<NotificationService>()?.Show(
                LocalizationManager.GetString("ContactUs_DeliveredToast"));
            AddNotification(
                LocalizationManager.GetString("ContactUs_DeliveredTitle"), topic, ContactUsSentIcon);
        }
        finally
        {
            _contactUsSending = false;
            ContactUsSpinnerFrame = 0;
            OnPropertyChanged(nameof(IsContactUsSending));
            OnPropertyChanged(nameof(CanSendContactUs));
        }
    }

    public bool IsGuest => _session.IsGuest;

    private Avalonia.Media.Imaging.Bitmap? _avatarImage;
    public Avalonia.Media.Imaging.Bitmap? AvatarImage
    {
        get => _avatarImage;
        set
        {
            if (SetProperty(ref _avatarImage, value))
                OnPropertyChanged(nameof(HasAvatar));
        }
    }
    public bool HasAvatar => AvatarImage != null;

    public string GreetingText =>
        string.IsNullOrEmpty(_user.DisplayName)
            ? LocalizationManager.GetString("HomeView_Greeting_Default")
            : string.Format(LocalizationManager.GetString("HomeView_Greeting_Format"), _user.DisplayName);

    public ObservableCollection<ViewModelBase> Devices { get; } = new();

    // The "connect your first device" tile. Held here rather than looked up in Devices, because it
    // is only IN Devices while there are no device cards — see SyncAddDeviceCard.
    private AddDeviceCardViewModel? _addDeviceCard;
    // 1 while a SyncAddDeviceCard is already posted, so a burst of collection changes (the revive
    // pass inserts several cards in a row) costs one dispatcher job, not one per change.
    private int _addDeviceCardSyncPending;

    public ObservableCollection<NotificationItemViewModel> Notifications { get; } = new();

    // Date-grouped mirror of Notifications, maintained alongside it by AddNotification and the
    // delete paths; the panel renders these so each calendar day gets its own divider.
    public ObservableCollection<NotificationGroupViewModel> NotificationGroups { get; } = new();

    public bool HasNoNotifications => Notifications.Count == 0;

    public int UnreadNotificationsCount => Notifications.Count(n => !n.IsRead);
    public bool HasUnreadNotifications => UnreadNotificationsCount > 0;

    // The visuals live in NotificationTypes (one table serves this class and the pull's
    // type→icon resolution, docs/plans/add-api-v21-notifications-sync); the Type slug rides
    // along so AddNotification can persist and push it without a parameter at every call
    // site — the icon a caller picks IS the entry's category.
    private readonly record struct NotificationIcon(string Data, string Uri, double Width, double Height, string Type)
    {
        public static NotificationIcon ForType(string type)
        {
            var visual = NotificationTypes.VisualFor(type);
            return new(visual.IconData, visual.IconUri, visual.IconWidth, visual.IconHeight, type);
        }
    }

    // Deliberately iconless (the pre-existing look of untyped entries); typed as Info so a
    // pulled copy on another machine renders the Info glyph rather than nothing.
    private static readonly NotificationIcon DefaultIcon = new(string.Empty, string.Empty, 0, 0, NotificationTypes.Info);

    private static readonly NotificationIcon DeviceConnectedIcon = NotificationIcon.ForType(NotificationTypes.DeviceConnected);

    private static readonly NotificationIcon DeviceDisconnectedIcon = NotificationIcon.ForType(NotificationTypes.DeviceDisconnected);

    private static readonly NotificationIcon LowBatteryIcon = NotificationIcon.ForType(NotificationTypes.LowBattery);

    private static readonly NotificationIcon AppUpdateIcon = NotificationIcon.ForType(NotificationTypes.AppUpdate);

    // Guest session-expiry warning (docs/plans/add-guest-session-expiry-warning). The Session
    // type maps to the shipped Info glyph — ProoveGaming.Common has no clock or timer asset.
    private static readonly NotificationIcon SessionExpiringIcon = NotificationIcon.ForType(NotificationTypes.Session);

    // Test seam (InternalsVisibleTo): NotificationIcon is private, so external callers cannot
    // spell the optional parameter -- this overload drives the same path with the default icon.
    internal void AddNotification(string title, string subtitle) => AddNotification(title, subtitle, null);

    void IOwnerScopedState.AddNotification(string title, string subtitle) => AddNotification(title, subtitle, null);

    private void AddNotification(string title, string subtitle, NotificationIcon? icon = null)
    {
        var now = DateTime.Now;
        var iconUsed = icon ?? DefaultIcon;
        var notification = new NotificationItemViewModel
        {
            Title = title,
            Subtitle = subtitle,
            Timestamp = now,
            Time = now.ToString("HH:mm"),
            IconData = iconUsed.Data,
            IconUri = iconUsed.Uri,
            IconWidth = iconUsed.Width,
            IconHeight = iconUsed.Height,
            Type = iconUsed.Type,
        };

        // Visible in the bell immediately (StorageId = 0 until the queued insert lands); the
        // row insert itself runs on the storage executor, off this (UI) thread.
        //
        // With NO owner there is nothing to key a row by, so the entry stays memory-only for now.
        // If no owner has resolved yet in this process — the Welcome-screen launch, where discovery
        // starts before sign-in — LoadNotificationsForOwner flushes it to the FIRST owner that
        // resolves. Once any owner has existed (after a sign-out, say) a no-owner entry stays
        // memory-only for good, so it can never leak into the NEXT account. See _ownerEverResolved.
        InsertNotification(notification);
        RaiseNotificationCountsChanged();

        if (CurrentOwner is { } owner)
        {
            // This entry was attributed to an owner directly, which PROVES an owner has resolved —
            // so nothing raised before it can still be waiting to be flushed. Setting the latch here
            // is what makes the capture filter in LoadNotificationsForOwner correct by construction:
            // without it the filter's premise would hold only because no owner-present AddNotification
            // happens to precede the first reload, which is an ordering accident in App.axaml.cs
            // (UpdateCheckService.Start runs after MainWindow resolution) rather than a rule this
            // class enforces. Cheap and idempotent — a plain bool set on the dispatcher.
            _ownerEverResolved = true;
            LastNotificationPersist = PersistNotificationAsync(notification, owner.Kind, owner.Id, new NotificationRecord
            {
                Title = title,
                Subtitle = subtitle,
                IconData = iconUsed.Data,
                IconUri = iconUsed.Uri,
                IconWidth = iconUsed.Width,
                IconHeight = iconUsed.Height,
                CreatedAt = now,
                Type = iconUsed.Type,
            });
        }
    }

    // Test seam: the persist chain of the most recent AddNotification (completed when none is
    // pending). Fire-and-forget in production; the threading tests await it to observe the
    // StorageId continuation.
    internal Task LastNotificationPersist { get; private set; } = Task.CompletedTask;

    // The completion continuation resumes on the captured (UI) context and only touches
    // non-notifying state (StorageId) or enqueues follow-up writes -- no PropertyChanged storm.
    // Two races are closed explicitly: deleted-before-persisted (delete the just-created row
    // instead of leaving an orphan) and read-toggled-before-persisted (the row was inserted
    // unread; push the toggle now that the row id exists).
    private async Task PersistNotificationAsync(
        NotificationItemViewModel notification, string ownerKind, string ownerId, NotificationRecord record)
    {
        int id;
        try
        {
            id = await _localStorage.RunOnStorageAsync(
                () => _localStorage.AddNotification(ownerKind, ownerId, record));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist notification '{Title}'.", record.Title);
            return;
        }

        if (notification.IsDeleted)
        {
            EnqueueStorageWrite(() => _localStorage.DeleteNotification(id));
            return;
        }

        notification.StorageId = id;
        if (notification.IsRead)
            EnqueueStorageWrite(() => _localStorage.SetNotificationRead(id, true));
    }

    // Fire-and-forget enqueue for ordering-sensitive writes from sync handlers: FIFO on the
    // storage executor preserves write order without awaiting; a fault is logged, never thrown
    // (and never breaks the chain -- see RunOnStorageAsync).
    private void EnqueueStorageWrite(Action write)
    {
        _ = _localStorage.RunOnStorageAsync(write).ContinueWith(
            t => _logger.LogError(t.Exception, "Queued storage write failed."),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // Shared by the live-add and the storage-load paths: hooks the item's handlers and puts it at
    // the top of the flat list and its date group.
    private void InsertNotification(NotificationItemViewModel notification)
    {
        notification.PropertyChanged += OnNotificationItemPropertyChanged;
        notification.DeleteRequested += n =>
        {
            n.PropertyChanged -= OnNotificationItemPropertyChanged;
            // Mark first: if the persist insert is still queued (StorageId == 0), its
            // continuation sees the mark and deletes the just-created row -- no orphan rows.
            n.IsDeleted = true;
            Notifications.Remove(n);
            NotificationGroupViewModel.Remove(NotificationGroups, n);
            if (n.StorageId > 0)
            {
                var id = n.StorageId;
                EnqueueStorageWrite(() => _localStorage.DeleteNotification(id));
            }
            RaiseNotificationCountsChanged();
        };
        Notifications.Insert(0, notification);
        NotificationGroupViewModel.Insert(NotificationGroups, notification, GroupHeaderFor);
    }

    private void RaiseNotificationCountsChanged()
    {
        OnPropertyChanged(nameof(HasNoNotifications));
        OnPropertyChanged(nameof(UnreadNotificationsCount));
        OnPropertyChanged(nameof(HasUnreadNotifications));
    }

    // Bumped by every LoadNotificationsForOwner; a reload whose generation is stale (a newer
    // owner switch happened meanwhile) must not insert the previous owner's rows. Written and
    // read on the calling (UI) thread only -- the await below resumes on the captured context.
    private int _notificationsLoadGeneration;

    // Test seam mirroring OpenCoreLoaded: completes when the most recent owner reload has
    // finished inserting (or gone stale).
    internal Task NotificationsLoaded { get; private set; } = Task.CompletedTask;

    // Rebuilds the bell from the current owner's stored history. Runs at construction and on every
    // owner change. The clear is synchronous -- an owner switch never shows the previous owner's
    // bell, not even for a frame -- while the read runs through the storage executor (so it
    // observes any writes queued for that owner) and inserts on the calling context if the
    // generation is still current. Callers stay sync (constructor, PropertyChanged handlers);
    // async callers and tests can await NotificationsLoaded.
    private void LoadNotificationsForOwner()
    {
        // Read once: CurrentOwner queries the provider on every access, and the flush decision, the
        // latch and the reload below must all be talking about the same owner.
        var owner = CurrentOwner;

        // Captured BEFORE the clear below throws them away. StorageId == 0 on its own only means
        // "not yet persisted" — the insert is queued on the storage executor and the id arrives with
        // its continuation — so it is the LATCH, not the id, that makes this capture safe: while
        // _ownerEverResolved is false no AddNotification has ever seen an owner, and every unsaved
        // item therefore belongs to nobody. That is the Welcome-screen launch, where
        // HomeView.OnLoaded starts discovery before the user has signed in. Reversed because
        // Notifications is newest-first while the flush must insert oldest-first, so the reload
        // below reads them back in the order they happened.
        var pendingFlush = owner is not null && !_ownerEverResolved
            ? Notifications.Where(n => n.StorageId == 0).Reverse().ToList()
            : null;
        if (owner is not null)
            _ownerEverResolved = true;

        foreach (var n in Notifications)
            n.PropertyChanged -= OnNotificationItemPropertyChanged;
        Notifications.Clear();
        NotificationGroups.Clear();
        RaiseNotificationCountsChanged();

        var generation = ++_notificationsLoadGeneration;
        NotificationsLoaded = owner is { } resolved
            ? LoadNotificationsCoreAsync(resolved.Kind, resolved.Id, generation, pendingFlush)
            : Task.CompletedTask;
    }

    // One-shot latch for the pre-owner notification flush: "has any owner ever existed in this
    // process?". Set from two places, and it needs both — here, when a reload resolves an owner, and
    // in AddNotification when an entry is attributed to one directly. It is what stops the flush
    // running in the other direction: an entry raised AFTER a sign-out (the session-expired toast)
    // belongs to nobody and must never land in the next account that signs in on this machine.
    //
    // Written on the dispatcher only. Every AddNotification call site is already there — it mutates
    // the bound Notifications collection, so the discovery paths reach it through
    // Dispatcher.UIThread.InvokeAsync/Post — and LoadNotificationsForOwner runs on the same thread
    // as the _notificationsLoadGeneration counter beside it. The invariant is single-threaded, not
    // merely visible, so volatile would buy nothing.
    private bool _ownerEverResolved;

    // Persists entries raised before any owner existed, attributing them to the first owner that
    // resolves. Awaited by the reload it precedes, so the ordering is explicit rather than
    // incidental; each insert itself runs on the storage executor via PersistNotificationAsync.
    private async Task FlushPreOwnerNotificationsAsync(
        IReadOnlyList<NotificationItemViewModel> pending, string ownerKind, string ownerId)
    {
        foreach (var item in pending)
        {
            await PersistNotificationAsync(item, ownerKind, ownerId, new NotificationRecord
            {
                Title = item.Title,
                Subtitle = item.Subtitle,
                IconData = item.IconData,
                IconUri = item.IconUri,
                IconWidth = item.IconWidth,
                IconHeight = item.IconHeight,
                CreatedAt = item.Timestamp,
                Type = item.Type,
            });
        }
    }

    private async Task LoadNotificationsCoreAsync(
        string ownerKind, string ownerId, int generation,
        IReadOnlyList<NotificationItemViewModel>? pendingFlush)
    {
        // Before the read, never on the caller's stack: the flush inserts and the GetNotifications
        // below both ride the storage executor, whose FIFO order guarantees the read observes them.
        // That is what makes each flushed entry come back exactly once, carrying a real StorageId,
        // instead of appearing twice — once as the in-memory item, once as its freshly read row.
        if (pendingFlush is { Count: > 0 })
            await FlushPreOwnerNotificationsAsync(pendingFlush, ownerKind, ownerId);

        var records = await _localStorage.RunOnStorageAsync(
            () => _localStorage.GetNotifications(ownerKind, ownerId));
        // Null only when the executor dropped the read because teardown began (see the
        // _disposed drop-guard in RunOnStorageAsync<T>) -- nothing left to show.
        if (records is null) return;
        // Resumed on the captured (UI) context; a newer owner switch owns the bell now.
        if (generation != _notificationsLoadGeneration) return;

        // The store returns newest-first; InsertNotification places each at the top, so feed
        // it oldest-first to end up newest-first again.
        for (var i = records.Count - 1; i >= 0; i--)
        {
            var record = records[i];
            // IsRead is set before the PropertyChanged hook attaches (InsertNotification runs
            // after), so loading a read item does not echo a write back into storage.
            var item = new NotificationItemViewModel
            {
                StorageId = record.Id,
                Title = record.Title,
                Subtitle = record.Subtitle,
                Timestamp = record.CreatedAt,
                Time = record.CreatedAt.ToString("HH:mm"),
                IconData = record.IconData,
                IconUri = record.IconUri,
                IconWidth = record.IconWidth,
                IconHeight = record.IconHeight,
                IsRead = record.IsRead,
                Type = record.Type,
            };
            InsertNotification(item);
        }

        RaiseNotificationCountsChanged();
    }

    private static string GroupHeaderFor(DateTime date) =>
        NotificationGroupViewModel.GetHeaderKind(date, DateTime.Today) switch
        {
            NotificationDateHeader.Today => LocalizationManager.GetString("Notifications_Today"),
            NotificationDateHeader.Yesterday => LocalizationManager.GetString("Notifications_Yesterday"),
            _ => date.ToString("d", System.Globalization.CultureInfo.CurrentUICulture),
        };

    // True while MarkAllNotificationsReadCommand loops IsRead = true over the whole bell: the
    // per-item handler below must not turn that into N queued UPDATEs when a single batched
    // SetAllNotificationsRead follows.
    private bool _suppressNotificationReadWrites;

    private void OnNotificationItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NotificationItemViewModel.IsRead))
        {
            // Enqueue, never await -- this is a PropertyChanged handler. FIFO keeps the last
            // toggle as the final persisted state. An unpersisted item (StorageId == 0) is
            // covered by its persist continuation, which re-checks IsRead once the row exists.
            if (!_suppressNotificationReadWrites
                && sender is NotificationItemViewModel { StorageId: > 0 } item)
            {
                var (id, isRead) = (item.StorageId, item.IsRead);
                EnqueueStorageWrite(() => _localStorage.SetNotificationRead(id, isRead));
            }
            OnPropertyChanged(nameof(UnreadNotificationsCount));
            OnPropertyChanged(nameof(HasUnreadNotifications));
        }
    }

    public HomeViewModel(
        IUsbHidDeviceService usbDeviceService,
        SessionService session,
        LocalStorageService localStorage,
        UserProfile user,
        IServiceProvider services,
        SystemNotificationService systemNotifications,
        ICurrentOwnerProvider ownerProvider,
        ILogger<HomeViewModel> logger,
        AudioMuteMonitor? audioMuteMonitor = null,
        ISettingsSyncService? settingsSync = null)
    {
        _usbHidDeviceService = usbDeviceService ?? throw new ArgumentNullException(nameof(usbDeviceService));
        _session = session;
        _localStorage = localStorage;
        _user = user;
        _services = services;
        _systemNotifications = systemNotifications;
        _ownerProvider = ownerProvider;
        _audioMuteMonitor = audioMuteMonitor;
        _logger = logger;

        // The notifications poll merged server bell rows (docs/plans/add-api-v21-notifications-
        // sync): reload the bell for the signed-in owner. Raised on a pool thread; the reload
        // touches bound collections, so marshal. Owner-guarded on the dispatcher — a guest (or
        // signed-out) owner has no server rows and keeps its bell untouched. Singleton VM on a
        // singleton service: no unsubscribe needed (the LanguageChanged convention below).
        if (settingsSync is not null)
        {
            settingsSync.NotificationsPulled += () => PostToUi(() =>
            {
                if (CurrentOwner is { Kind: LocalStorageService.OwnerKindUser })
                    LoadNotificationsForOwner();
            });
        }

        _user.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UserProfile.DisplayName))
                OnPropertyChanged(nameof(GreetingText));

            // Signing in moves the settings owner from guest (or nothing) to the customer id.
            if (e.PropertyName == nameof(UserProfile.CustomerId))
                RestoreConnectedDevicesForNewOwner();
        };

        // VM-computed strings don't ride DynamicResource, so a language switch has to re-raise
        // them explicitly (pattern: ProfileViewModel / DeviceDetailsViewModel). Static event on a
        // singleton VM — no unsubscribe needed. LanguageChanged fires synchronously on the UI
        // thread (the Settings dropdown click, or startup init before this VM exists), so the
        // handler may walk Devices / NotificationGroups directly; it only re-raises change
        // notifications and recomputes headers — no property writes that could re-enter it.
        LocalizationManager.LanguageChanged += OnLanguageChanged;

        // SessionService assigns SessionId BEFORE raising IsGuest, so by the time this runs the
        // owner is fully formed — see StartSession / RestoreSession / EndSession.
        _session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionService.IsGuest))
                RestoreConnectedDevicesForNewOwner();
        };

        ConfirmDeleteCommand = new RelayCommand(ExecuteConfirmDelete);
        CancelDeleteCommand = new RelayCommand(() => PendingDeleteCard = null);
        ToggleNotificationsCommand = new RelayCommand(() => IsNotificationsOpen = !IsNotificationsOpen);
        CloseNotificationsCommand = new RelayCommand(() => IsNotificationsOpen = false);
        MarkAllNotificationsReadCommand = new RelayCommand(() =>
        {
            _suppressNotificationReadWrites = true;
            try
            {
                foreach (var n in Notifications)
                    n.IsRead = true;
            }
            finally { _suppressNotificationReadWrites = false; }
            // One batched UPDATE for the whole owner instead of one commit per item -- the
            // audit's worst measured path. Unpersisted items are covered too: their queued
            // inserts precede this write in the FIFO queue.
            if (CurrentOwner is { } batchOwner)
                EnqueueStorageWrite(() => _localStorage.SetAllNotificationsRead(batchOwner.Kind, batchOwner.Id));
        });
        DeleteAllNotificationsCommand = new RelayCommand(() =>
        {
            foreach (var n in Notifications)
            {
                n.PropertyChanged -= OnNotificationItemPropertyChanged;
                // Unpersisted items get their just-created row deleted by the persist
                // continuation; persisted ones fall under the owner-wide delete below,
                // which FIFO orders after every pending insert.
                n.IsDeleted = true;
            }
            Notifications.Clear();
            NotificationGroups.Clear();
            if (CurrentOwner is { } owner)
                EnqueueStorageWrite(() => _localStorage.DeleteAllNotifications(owner.Kind, owner.Id));
            RaiseNotificationCountsChanged();
        });
        LogoutCommand = new RelayCommand(() => IsLogoutConfirmOpen = true);
        CancelLogoutCommand = new RelayCommand(() => IsLogoutConfirmOpen = false);
        // No-op while the send loader runs (review R1 S2): the ✕ stays reachable in the frame
        // column, but a close mid-send would still end in the confirmation toast — so the
        // whole dialog is inert for the duration of the request (bounded by ContactUsSendTimeout).
        CancelContactUsCommand = new RelayCommand(() =>
        {
            if (ContactUsCanCancel(_contactUsSending)) IsContactUsOpen = false;
        });
        SendContactUsCommand = new AsyncRelayCommand(SendContactUsAsync);
        ConfirmLogoutCommand = new RelayCommand(() =>
        {
            IsLogoutConfirmOpen = false;
            LogoutRequested?.Invoke();
        });

        _session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SessionService.IsGuest)) return;
            OnPropertyChanged(nameof(IsGuest));
            if (_session.IsGuest)
            {
                // Any card on screen right now came from a previous owner context — a restored
                // session precedes this VM's construction, so its cards never see this event —
                // and its StorageId points into that owner's store. Reset them so the register
                // below adopts every card into the new guest session; left stale, the register
                // skips them (it only takes StorageId == 0) and a delete would aim a session
                // remove at a foreign row id.
                foreach (var card in Devices.OfType<DeviceCardViewModel>())
                    card.StorageId = 0;
                RegisterExistingCardsInSession();
                // The bell follows the owner: a fresh guest must not inherit the previous
                // owner's history (and a restored guest gets its own back).
                LoadNotificationsForOwner();
            }
        };

        // Update-available announcements (docs/plans/contact-us-and-update-check): the poll
        // lives in UpdateCheckService — deliberately NOT in this VM's Start()/Stop(), which
        // run on every Home load/unload including minimise-to-tray — and raises the event via
        // Dispatcher.UIThread.Post, so the handler is already on the UI thread. Singleton to
        // singleton, like LanguageChanged: no unsubscribe needed. GetService, not
        // GetRequiredService — the unit-test graphs build this VM without the service.
        // LatestKnownUpdate is re-checked at subscription as belt-and-braces for an
        // announcement raised before this VM existed (Start() runs after MainWindow
        // resolution, so that cannot happen today).
        if (_services.GetService<UpdateCheckService>() is { } updateCheck)
        {
            updateCheck.UpdateAvailable += OnUpdateAvailable;
            if (updateCheck.LatestKnownUpdate is { } known)
                OnUpdateAvailable(known);
        }

        // Guest session-expiry warning (docs/plans/add-guest-session-expiry-warning). Subscribed
        // AND asked, because unlike UpdateCheckService the warning really can become due before
        // this VM exists: App.OnFrameworkInitializationCompleted restores the session before it
        // resolves MainWindow, so a session restored already inside the warning window raises
        // SessionExpiringSoon with nobody listening. Singleton to singleton — no unsubscribe.
        _session.SessionExpiringSoon += OnSessionExpiringSoon;
        if (_session.ExpiryWarningDue)
            OnSessionExpiringSoon();

        Devices.CollectionChanged += OnDevicesCollectionChanged;
        LoadDevices();
        LoadNotificationsForOwner();
    }

    // Everything on the Home screen that is built in code rather than bound via DynamicResource:
    // the greeting, each card's type label (converter binding) and connection tooltip, and the
    // "Today"/"Yesterday" notification group headers (baked in at insertion by GroupHeaderFor).
    private void OnLanguageChanged()
    {
        OnPropertyChanged(nameof(GreetingText));
        foreach (var card in Devices.OfType<DeviceCardViewModel>())
            card.RefreshLocalizedText();
        RefreshNotificationGroupHeaders();
    }

    // Bell + in-app toast + Windows system notification — the low-battery pattern
    // (CheckLowBattery); the system toast covers the tray-resident case where nobody sees
    // the in-app surfaces. Already on the UI thread: UpdateCheckService posts the event.
    private void OnUpdateAvailable(AppUpdateInfo update)
    {
        var title = LocalizationManager.GetString("AppUpdate_AvailableTitle");
        var body = string.Format(
            LocalizationManager.GetString("AppUpdate_AvailableBody_Format"), update.Version);
        AddNotification(title, body, AppUpdateIcon);
        _services.GetService<NotificationService>()?.Show(body);
        _systemNotifications.Show(title, body);
    }

    // Bell entry only — deliberately NOT the three-surface pattern of OnUpdateAvailable above.
    // Product decided (docs/plans/add-guest-session-expiry-warning, Scope → Out) that this warning
    // is one row in the notification list: no in-app banner, no Windows toast. Do not "complete
    // the pattern" by adding them.
    private void OnSessionExpiringSoon()
    {
        _session.AcknowledgeExpiryWarning();
        // Signed-in accounts have their own 30-day token expiry; this warning is guests only.
        if (!_session.IsGuest) return;
        LastSessionExpiryAnnouncement = AnnounceSessionExpiringAsync();
    }

    // Test seam: the flag round-trip plus the announcement of the most recent warning
    // (completed when none is in flight; completion means the claim has resolved and any
    // bell entry has been handed to PostToUi). Fire-and-forget in production.
    internal Task LastSessionExpiryAnnouncement { get; private set; } = Task.CompletedTask;

    // The warning path below marshals to the UI thread through this seam rather than calling
    // Dispatcher.UIThread directly — DeviceDetailsViewModel's convention. In the app it IS that
    // Post; tests replace it with an inline invoke, because the process-wide Dispatcher's
    // first-touch thread affinity makes RunJobs-based draining racy against background
    // continuations from earlier tests — the exact trap AssemblyInfo.cs documents. Deliberately
    // ONLY the guest-expiry-warning path routes through it: the other Dispatcher.UIThread calls
    // in this class carry load-bearing semantics of their own (awaited hops whose completion is
    // observed, CheckAccess fast paths) and converting them wholesale is out of scope — see
    // docs/plans/add-api-v2-conformance, Review findings → Round 1 → S3.
    internal Action<Action> PostToUi { get; set; } = action => Dispatcher.UIThread.Post(action);

    private async Task AnnounceSessionExpiringAsync()
    {
        bool claimed;
        try
        {
            // Through the storage executor rather than touching SQLite inline: the caller is the
            // dispatcher, and the warning must not stall it. Check and write in ONE item, not two
            // awaited hops, because this handler legitimately runs twice for the same warning —
            // the constructor's latch check, plus the event that SessionService dispatched before
            // the constructor subscribed. Executor items never interleave, so the second call's
            // read cannot overtake the first call's write. The flag is written BEFORE the
            // announcement, so a crash between the two costs a missed warning and never a
            // duplicated one (plan → Risks). After IsShutDown this yields false and nothing is
            // announced, which is the right answer during teardown.
            claimed = await _localStorage.RunOnStorageAsync(() =>
            {
                if (_localStorage.GetSessionExpiryWarningSent()) return false;
                // Claim only on a row actually updated: with no app_session row the UPDATE is a
                // silent no-op, and announcing on an unwritten flag is the double warning this
                // guard exists to prevent.
                return _localStorage.SetSessionExpiryWarningSent() > 0;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record the guest session expiry warning.");
            return;
        }

        if (!claimed) return;

        var hours = WarningLeadHours(AppSettings.SessionExpiryWarningMinutes);
        var title = LocalizationManager.GetString("Notification_GuestSessionExpiringTitle");
        var body = string.Format(
            LocalizationManager.GetString("Notification_GuestSessionExpiringBody_Format"), hours);
        try
        {
            PostToUi(() => AddNotification(title, body, SessionExpiringIcon));
        }
        catch (Exception ex)
        {
            // This method is fire-and-forget, so a fault here would otherwise land on a task
            // nobody observes. In production PostToUi only enqueues; in tests the seam runs the
            // entry inline, and this catch keeps an AddNotification fault from escaping. Logged
            // here rather than via a ContinueWith at the call site so the returned task keeps
            // its shape for LastSessionExpiryAnnouncement.
            _logger.LogError(ex, "Failed to add the guest session expiry warning notification.");
        }
    }

    // Whole hours of lead time, so a non-default SessionExpiryWarningMinutes still reads
    // correctly; anything under an hour rounds up to 1 rather than announcing "0".
    internal static int WarningLeadHours(int minutes) => Math.Max(1, minutes / 60);

    public void LoadDevices()
    {
        if (_session.CurrentSession != null)
        {
            foreach (var record in _session.CurrentSession.SnapshotDevices())
                Devices.Add(RecordToViewModel(record));
        }
        else if (CurrentOwner is { Kind: LocalStorageService.OwnerKindUser } owner)
        {
            // A signed-in launch has no session; the cards live under the user owner instead.
            foreach (var record in _localStorage.GetDevices(owner.Kind, owner.Id))
                Devices.Add(RecordToViewModel(record));
        }

        _addDeviceCard = _services.GetRequiredService<AddDeviceCardViewModel>();
        _addDeviceCard.DeviceAdded += OnDeviceAdded;
        SyncAddDeviceCard();
    }

    // Posted, never inline: changing the collection from inside its own change notification throws.
    private void OnDevicesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Interlocked.Exchange(ref _addDeviceCardSyncPending, 1) == 1) return;
        Dispatcher.UIThread.Post(() =>
        {
            Volatile.Write(ref _addDeviceCardSyncPending, 0);
            SyncAddDeviceCard();
        });
    }

    // The add tile is shown only while the grid holds no device card at all — connected or not. It
    // is an empty-state prompt, not an action (AddDevice does nothing; see AddDeviceCardViewModel).
    // Removed from Devices rather than hidden: an invisible item would still take a WrapPanel slot
    // and its spacing. Every insert site already falls back to Devices.Count when the tile is
    // absent, and it is always re-added last.
    private void SyncAddDeviceCard()
    {
        if (_addDeviceCard is null) return;

        var show = !Devices.OfType<DeviceCardViewModel>().Any();
        var shown = Devices.Contains(_addDeviceCard);

        if (show && !shown)
            Devices.Add(_addDeviceCard);
        else if (!show && shown)
            Devices.Remove(_addDeviceCard);
    }

    /// <summary>
    /// Re-anchors owner-scoped state after a sign-in or sign-up replaced the owner context
    /// (AuthSessionCoordinator calls this once the old session is torn down). The on-screen cards are adopted
    /// into the user's store — the mirror of <see cref="RegisterExistingCardsInSession"/>, which
    /// does the same when the owner becomes a guest — and the user's stored offline cards that are
    /// not on screen come back. The bell reloads to the user's history.
    /// </summary>
    public void ReloadOwnerScopedState()
    {
        if (CurrentOwner is { Kind: LocalStorageService.OwnerKindUser } owner)
        {
            // Read BEFORE the adopt loop writes anything. The revive pass below asks which of this
            // owner's stored cards were missing from the screen when we arrived; asking it after
            // the adopt loop would mean classifying rows the adopt loop had itself just deleted and
            // reinserted, i.e. resting the answer on a side effect of the storage layer rather than
            // on a stated rule. This read REPLACES the post-adopt one — still one GetDevices per
            // invocation, not two.
            var storedBeforeAdopt = _localStorage.GetDevices(owner.Kind, owner.Id);

            // Materialised once, outside the revive loop: rebuilding it per record would turn the
            // pass into O(cards × rows) allocations.
            var onScreen = Devices.OfType<DeviceCardViewModel>()
                .Select(CardKey.From)
                .ToList();

            // Seed the low-battery guard from this owner's stored rows BEFORE the adopt loop
            // rewrites them: AddDevice is delete+insert, and a card discovery created from
            // scratch (device connected while the user was still on Welcome) carries a zero
            // band mirror — adopting it verbatim overwrote the band the stored row held and
            // re-armed the launch toast the persistence exists to suppress (PR #125 m1).
            // Matching reuses IsRepresentedOnScreen so the seed and the adopt can never
            // disagree about which row a card swallows.
            foreach (var card in Devices.OfType<DeviceCardViewModel>())
            {
                var key = new[] { CardKey.From(card) };
                foreach (var record in storedBeforeAdopt)
                {
                    if (record.LowBatteryAnnouncedBand > 0 && IsRepresentedOnScreen(record, key))
                        SeedLowBatteryBand(card, record.LowBatteryAnnouncedBand);
                }
            }

            // AddDevice upserts on the card's identity, so adopting is idempotent across repeated
            // sign-ins — and adopting a merged-transport card drops any row this owner still holds
            // for it under another transport's routing identity.
            foreach (var card in Devices.OfType<DeviceCardViewModel>().ToList())
            {
                card.StorageId = 0;
                PersistDeviceCard(card);
            }

            // IsRepresentedOnScreen, not a raw tuple compare: a merged-transport card on screen must
            // swallow the stored row it was persisted under over a DIFFERENT transport, or that row
            // comes back as a second, permanently disconnected card.
            var addCard = Devices.OfType<AddDeviceCardViewModel>().FirstOrDefault();
            foreach (var record in storedBeforeAdopt)
            {
                if (IsRepresentedOnScreen(record, onScreen))
                    continue;
                var vm = RecordToViewModel(record);
                var insertIndex = addCard != null ? Devices.IndexOf(addCard) : Devices.Count;
                Devices.Insert(insertIndex, vm);
            }
        }

        ResetContactUsOwnerState();
        LoadNotificationsForOwner();
    }

    /// <summary>
    /// Drops owner-scoped view state when the current owner signs off — logout, an invalidated
    /// token, or guest-session expiry. Disconnected cards are that owner's stored list and must
    /// not stay visible to (or be adopted and persisted by) whoever signs in next on this
    /// machine; connected cards describe hardware physically present right now, so they stay —
    /// discovery, not the departing owner, is their source of truth. The bell reloads for the
    /// now-absent owner, i.e. empties.
    /// </summary>
    public void ClearOwnerScopedState()
    {
        foreach (var card in Devices.OfType<DeviceCardViewModel>().Where(c => !c.Connected).ToList())
        {
            Devices.Remove(card);
            _lowBatteryAnnouncedBand.Remove(LowBatteryIdentity(card));
            _batteryMissStreak.Remove((card.VendorId, card.ProductId, card.DeviceKey ?? string.Empty));
        }

        ResetContactUsOwnerState();

        LoadNotificationsForOwner();
    }

    // The Contact Us draft's email belongs to an owner: OpenContactUs only pre-fills an EMPTY
    // field, so EVERY owner change must drop the draft — sign-off (review R1 S3) and equally the
    // mirror transition guest → signed-in (PR #118 m2), or the next owner opens the dialog with
    // the previous owner's address instead of their own profile email.
    private void ResetContactUsOwnerState()
    {
        ContactUsEmail = string.Empty;
        ContactUsError = null;
    }

    // Subscribed in Start, released in Stop — as a PAIR of methods rather than loose lines in each,
    // so a future subscription cannot be added to one side and forgotten on the other.
    //
    // That is not hypothetical: BatteryChanged was added to Start and missed in Stop. HomeViewModel
    // is a singleton and Start/Stop run on every Home load/unload — minimise-to-tray included — so
    // the handler accumulated one copy per visit for the life of the process. Nothing looked wrong,
    // because re-applying the same value to the same card is idempotent; the cost was one extra
    // dispatcher post and one extra full walk of Devices per accumulated copy, per push, forever.
    private void SubscribeDeviceEvents()
    {
        _usbHidDeviceService.DeviceFound += UsbHidDeviceService_DeviceFound;
        _usbHidDeviceService.NotifyError += UsbHidDeviceService_NotifyError;
        // The DeviceHost pushes battery the moment the hardware reports it; without this the card
        // waits for the next 60 s poll to show a mouse that was just switched back on.
        _usbHidDeviceService.BatteryChanged += OnBatteryChanged;
        _usbHidDeviceService.HostAvailabilityChanged += OnHostAvailabilityChanged;
    }

    private void UnsubscribeDeviceEvents()
    {
        _usbHidDeviceService.DeviceFound -= UsbHidDeviceService_DeviceFound;
        _usbHidDeviceService.NotifyError -= UsbHidDeviceService_NotifyError;
        _usbHidDeviceService.BatteryChanged -= OnBatteryChanged;
        _usbHidDeviceService.HostAvailabilityChanged -= OnHostAvailabilityChanged;
    }

    public void Start()
    {
        SubscribeDeviceEvents();
        _ = _usbHidDeviceService.StartCommunication();

        // Keeps every connected device's battery level current for the life of the session. Started
        // here (on the UI thread) rather than from an async continuation, so the timer has correct
        // dispatcher affinity. Overlapping ticks are prevented by _batteryPolling.
        _batteryPollTimer = new DispatcherTimer { Interval = BatteryPollInterval };
        _batteryPollTimer.Tick += (_, _) => _ = PollBatteriesGuardedAsync();
        _batteryPollTimer.Start();
    }

    // A poll can outlast its 60 s interval on a slow device; without this the ticks would stack.
    private async Task PollBatteriesGuardedAsync()
    {
        if (_batteryPolling) return;
        _batteryPolling = true;
        try { await PollBatteriesAsync(); }
        finally { _batteryPolling = false; }
    }

    public void Stop()
    {
        UnsubscribeDeviceEvents();
        _usbHidDeviceService.StopCommunication();

        // Paired with Start: without this the poll keeps querying devices after communication has
        // been torn down, and every tick logs a failure.
        _batteryPollTimer?.Stop();
        _batteryPollTimer = null;

        // Same reason: the mute poller resolves COM endpoints every two seconds, and Stop() runs on
        // minimise-to-tray as well as teardown. Track() is not called again while hidden, so without
        // this it polled forever for icons nobody can see.
        _audioMuteMonitor?.Stop();
    }

    private async void UsbHidDeviceService_DeviceFound(object? sender, SubscribeOnDevicesResponse response)
    {
        await _eventGate.WaitAsync();
        try
        {
            if (response.SenderType == SenderType.Usb || response.SenderType == SenderType.Dongle)
            {
                await HandleUsbHidDeviceFoundAsync(response.HidEntries.ToList());
            }
            if (response.SenderType == SenderType.Bluetooth)
            {
                await HandleBluetoothDeviceFoundAsync(response.BluetoothEntry);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling device event ({SenderType})", response.SenderType);
        }
        finally
        {
            _eventGate.Release();
        }
    }

    private async Task HandleUsbHidDeviceFoundAsync(List<HidDeviceResponse> devicesModels)
    {
        var allCards = Devices.OfType<DeviceCardViewModel>().ToList();

        // A card is gone only when NO reported device matches it. A merged-transport card matches
        // on name alone, so it survives losing one transport while another is still connected.
        foreach (var card in allCards.Where(x => x.Connected && !devicesModels.Any(dm =>
            string.Equals(dm.ClientName.ToUpper(), x.Name, StringComparison.OrdinalIgnoreCase) &&
            (x.MergeTransports || string.IsNullOrEmpty(x.DeviceKey) ||
             string.Equals(dm.DeviceKey, x.DeviceKey, StringComparison.OrdinalIgnoreCase)))))
        {
            if (card.BluetoothLinkUp)
            {
                card.HasWire = false;
                card.HasDongle = false;
                card.IsCharging = false;
                // With the cable gone the display suppression (wired > bluetooth, PG-494) no
                // longer applies — resurface the still-live Bluetooth link so the card doesn't
                // read as disconnected while the device keeps working.
                card.HasBluetooth = true;
            }
            else
            {
                card.MakeInactive();
                var disconnected = card;
                await Dispatcher.UIThread.InvokeAsync(() => AddNotification(LocalizationManager.GetString("Notification_DeviceDisconnected"), disconnected.DisplayName, DeviceDisconnectedIcon));
            }
        }

        // Group per PHYSICAL unit — (ClientName, DeviceKey) — so two identical mice (same name,
        // different Container ID) each get their own card instead of collapsing into one. A
        // merged-transport product (one device reachable by cable / dongle / BT, e.g. TAIPAN)
        // drops the key so its transports collapse into a single card.
        var perClient = devicesModels
            .Where(dm => !string.IsNullOrEmpty(dm.ClientName))
            .GroupBy(dm => (
                Name: dm.ClientName.ToUpper(),
                Key: dm.CatalogEntry?.MergeTransports == true ? string.Empty : (dm.DeviceKey ?? string.Empty)));

        foreach (var clientGroup in perClient)
        {
            string clientName = clientGroup.Key.Name;
            bool hasWired = false, hasDongle = false, hasBluetoothHid = false;
            int batteryVid = 0, batteryPid = 0;
            // The live transport the card routes commands to (wired preferred over dongle).
            string routingKey = string.Empty;
            DeviceCatalogEntry? bestEntry = null;

            foreach (var dm in clientGroup)
            {
                var entry = dm.CatalogEntry;
                if (entry == null) continue;

                int.TryParse(dm.VendorId, out int vid);
                int.TryParse(dm.ProductId, out int pid);

                var connection = ParseConnectionType(entry.ConnectionType);
                if (connection == DeviceConnectionType.Wired)
                {
                    hasWired = true;
                    bestEntry = entry;
                    batteryVid = vid;
                    batteryPid = pid;
                    routingKey = dm.DeviceKey ?? string.Empty;
                }
                else if (connection == DeviceConnectionType.Bluetooth)
                {
                    // A Bluetooth-LE mouse enumerates through the HID stack, not the OS Bluetooth
                    // stack, so it arrives here rather than in FillBluetoothCard — which is the only
                    // other place HasBluetooth is set. Before this branch existed the entry matched
                    // neither of the two conditions above and was dropped silently, so a mouse
                    // switched to Bluetooth still displayed as "dongle" whenever its receiver
                    // happened to be plugged in.
                    hasBluetoothHid = true;

                    // Outranks the dongle for routing as well as for display, hence overwriting a
                    // dongle-chosen bestEntry rather than only filling an empty one: the entry
                    // carries the LIVE transport's capabilities, and Bluetooth declares
                    // SupportsConfiguration=false where the dongle's value would let the UI offer
                    // writes on a link that is not carrying them.
                    if (!hasWired)
                    {
                        bestEntry = entry;
                        batteryVid = vid;
                        batteryPid = pid;
                        routingKey = dm.DeviceKey ?? string.Empty;
                    }
                }
                else if (connection == DeviceConnectionType.Dongle2_4G)
                {
                    hasDongle = true;
                    if (bestEntry == null)
                    {
                        bestEntry = entry;
                        batteryVid = vid;
                        batteryPid = pid;
                        routingKey = dm.DeviceKey ?? string.Empty;
                    }
                }
            }

            if (bestEntry == null) continue;

            var mergeTransports = bestEntry.MergeTransports;

            // Match the existing card for THIS physical unit. A merged-transport product is one
            // card per product, so it matches on name alone — that keeps the same card across a
            // cable↔dongle switch. Otherwise: exact (name, key), then a legacy card with the same
            // name but no key yet, then a DISCONNECTED card of the same model (see below).
            var found = mergeTransports
                ? allCards.FirstOrDefault(x =>
                    string.Equals(x.Name, clientName, StringComparison.OrdinalIgnoreCase))
                : allCards.FirstOrDefault(x =>
                    string.Equals(x.Name, clientName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.DeviceKey, routingKey, StringComparison.OrdinalIgnoreCase))
                    ?? allCards.FirstOrDefault(x =>
                    string.Equals(x.Name, clientName, StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrEmpty(x.DeviceKey))
                    // Port move. deviceKey is the Windows Container ID, which for a device with no
                    // USB serial number — neither BLAZE nor TAIPAN reports one — is derived from the
                    // port, so the SAME mouse in another port arrives with a different key and used
                    // to appear as a brand-new device. A merged-transport product never hit this
                    // because it ignores the key entirely.
                    //
                    // Reuse a card of this model that is currently DISCONNECTED: that is precisely
                    // the unplug-here/replug-there case. A unit that is still connected keeps its own
                    // card, so two identical mice plugged in at once are still two cards — which is
                    // the whole reason this product is not merge-transport.
                    //
                    // Ambiguous only when the user owns several identical units AND swaps which one
                    // is plugged in. With no serial number in the hardware those units are genuinely
                    // indistinguishable, so no rule can do better; adopting the idle card matches
                    // what a user moving one mouse expects.
                    ?? allCards.FirstOrDefault(x =>
                    string.Equals(x.Name, clientName, StringComparison.OrdinalIgnoreCase) &&
                    !x.Connected);

            // The card-independent half of the rule; the Bluetooth half is applied per card below,
            // because whether a card is Bluetooth-live is only settled by FillConnectionCard.
            bool hasHidBatterySource = (hasWired || hasDongle) && batteryVid > 0;

            if (found != null)
            {
                var wasConnected = found.Connected;
                // Captured before FillConnectionCard, which restamps the card's name from the
                // catalog, and ApplyDeviceIdentity, which restamps the routing identity.
                var identityBefore = CardIdentity(found);
                var settingsBefore = SettingsIdentityOf(found);

                FillConnectionCard(found, bestEntry, hasWired, hasDongle, hasBluetoothHid);
                ApplyDeviceIdentity(found, bestEntry, batteryVid, batteryPid, routingKey);
                HealSettingsVendor(bestEntry);
                if (ShouldPollHidBattery(found, hasHidBatterySource)) _ = RefreshBatteryAsync(found, batteryVid, batteryPid);

                // Only when either identity actually moved — a card adopted under one transport and
                // now live on another, a mouse replugged into a different port, or a restored card
                // whose stored settings_* columns discovery just corrected. The settings triple must
                // be compared separately: a merged-transport card's CardIdentity is (NAME, 0, 0, "")
                // by construction and blind to the pin ApplyDeviceIdentity stamps, so without this a
                // healed two-transport row kept settings_* = 0 forever. Discovery is a recurring
                // poll, so persisting unconditionally here would be one storage write per device per
                // tick; both steady-state tuples compare equal and write nothing.
                if (CardIdentity(found) != identityBefore || SettingsIdentityOf(found) != settingsBefore)
                    PersistDeviceCard(found);

                if (!wasConnected && found.Connected)
                {
                    var reconnected = found;
                    await Dispatcher.UIThread.InvokeAsync(() => AddNotification(LocalizationManager.GetString("Notification_DeviceConnected"), reconnected.DisplayName, DeviceConnectedIcon));
                    ReapplyOnConnect(found);
                }
            }
            else
            {
                var deviceCard = (DeviceCardViewModel)_services.GetRequiredService(ParseCardSize(bestEntry.CardType));

                FillConnectionCard(deviceCard, bestEntry, hasWired, hasDongle, hasBluetoothHid);
                ApplyDeviceIdentity(deviceCard, bestEntry, batteryVid, batteryPid, routingKey);
                HealSettingsVendor(bestEntry);
                if (ShouldPollHidBattery(deviceCard, hasHidBatterySource)) _ = RefreshBatteryAsync(deviceCard, batteryVid, batteryPid);
                WireDelete(deviceCard);
                PersistDeviceCard(deviceCard);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var addCard = Devices.OfType<AddDeviceCardViewModel>().FirstOrDefault();
                    var insertIndex = addCard != null ? Devices.IndexOf(addCard) : Devices.Count;
                    Devices.Insert(insertIndex, deviceCard);
                    AddNotification(LocalizationManager.GetString("Notification_NewDeviceConnected"), deviceCard.DisplayName, DeviceConnectedIcon);
                });
                ReapplyOnConnect(deviceCard);
            }
        }

        await TrackAudioCardsAsync();
    }

    // Re-points the mute poller at the current card set. Called from BOTH discovery paths: a
    // Bluetooth headset would otherwise never get indicators, because only the USB path tracked.
    // Cards without audio are ignored, so this costs nothing on a machine of mice and keyboards.
    private Task TrackAudioCardsAsync() =>
        Dispatcher.UIThread.InvokeAsync(() =>
            _audioMuteMonitor?.Track(Devices.OfType<DeviceCardViewModel>())).GetTask();

    // The single definition of "is this the same device?" — shared by the revive pass in
    // ReloadOwnerScopedState, the re-persist test on the discovery tick and (through DeviceRecord)
    // the storage dedupe in LocalStorageService.AddDevice. A merged-transport product is one card
    // per product, matched on name alone (see the discovery matcher above), so its routing identity
    // — which changes with the live transport — plays no part; every other card stays keyed per
    // physical unit. The name is case-folded because discovery upper-cases the client name and
    // compares case-insensitively while SQLite's `name = $name` does not: leaving the two rules
    // apart is a second, latent source of duplicate rows.
    internal static (string Name, int VendorId, int ProductId, string DeviceKey) CardIdentity(
        string? name, int vendorId, int productId, string? deviceKey, bool mergeTransports) =>
        mergeTransports
            ? ((name ?? string.Empty).ToUpperInvariant(), 0, 0, string.Empty)
            : ((name ?? string.Empty).ToUpperInvariant(), vendorId, productId, deviceKey ?? string.Empty);

    private static (string Name, int VendorId, int ProductId, string DeviceKey) CardIdentity(DeviceCardViewModel card) =>
        CardIdentity(card.Name, card.VendorId, card.ProductId, card.DeviceKey, card.MergeTransports);

    // The persisted settings_* triple, compared around the identity-stamping calls above so a
    // discovery tick re-persists a card whose stored bucket the stamp just corrected. Kept separate
    // from CardIdentity on purpose: identity answers "is this the same device?", and for a merged
    // product it deliberately ignores everything a transport switch changes — which is exactly why
    // it cannot double as the persist trigger for the settings columns.
    private static (int VendorId, int ProductId, string DeviceKey) SettingsIdentityOf(DeviceCardViewModel card) =>
        (card.SettingsVendorId, card.SettingsProductId, card.SettingsDeviceKey ?? string.Empty);

    // The part of an on-screen card the swallow rule needs, so the revive pass can materialise its
    // set once instead of dragging view-model references (and their change notifications) through it.
    internal readonly record struct CardKey(
        string Name, int VendorId, int ProductId, string DeviceKey, bool MergeTransports)
    {
        internal static CardKey From(DeviceCardViewModel card) => new(
            card.Name ?? string.Empty, card.VendorId, card.ProductId,
            card.DeviceKey ?? string.Empty, card.MergeTransports);
    }

    /// <summary>
    /// "Is this stored row already represented by a card on screen?" — the read-side twin of
    /// <see cref="LocalStorageService.AddDevice"/>'s delete predicate, stated once so the two rules
    /// cannot drift apart. A MERGED card swallows every stored row carrying the same name
    /// (case-folded), whatever transport identity that row was written under; any other card
    /// swallows only the exact (name, vendorId, productId, deviceKey) tuple, which is what keeps two
    /// identical physical units apart. Deliberately NOT folded into <see cref="CardIdentity"/>:
    /// identity answers "is this the same device?" symmetrically, while the swallow rule is
    /// asymmetric — a merged card swallows a non-merged row, never the reverse.
    /// </summary>
    internal static bool IsRepresentedOnScreen(DeviceRecord record, IReadOnlyCollection<CardKey> onScreen)
    {
        var name = record.Name ?? string.Empty;
        var key = record.DeviceKey ?? string.Empty;
        foreach (var card in onScreen)
        {
            if (!string.Equals(card.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (card.MergeTransports)
                return true;
            if (card.VendorId == record.VendorId
                && card.ProductId == record.ProductId
                && string.Equals(card.DeviceKey, key, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // ---- Settings-vendor heal (docs/plans/fix-settings-bucket-identity) ------
    // Products whose alternate settings buckets this session has already asked storage to collapse,
    // as (alternate vendor, pinned vendor, pinned product). The migration is idempotent on its own
    // -- after a pass no row remains under the alternate vendor -- so this set is purely an
    // optimisation: without it every discovery tick would open the database to ask a question whose
    // answer is almost always "nothing to do". The tick is a hot-ish path and must never touch
    // storage to decide.
    //
    // Written from the gRPC discovery thread, which is the same hazard UserSession.Devices
    // documents, so every access takes the gate. Not confined to the dispatcher like
    // _lowBatteryAnnouncedBand: this runs per tick, and a Post per tick is exactly the dispatcher
    // traffic the tick is supposed to avoid.
    private readonly HashSet<(int FromVendorId, int ToVendorId, int ProductId)> _settingsVendorRekeyed = [];
    private readonly Lock _settingsVendorRekeyGate = new();

    /// <summary>
    /// The re-key passes a catalog entry calls for — one per transport vendor that is not the
    /// product's pinned settings vendor. Pure, so the decision can be tested without the device and
    /// session service graph <see cref="HomeViewModel"/>'s constructor needs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pin is read from the ENTRY and from nothing else. A card's
    /// <see cref="DeviceCardViewModel.SettingsVendorId"/> is not always a catalog pin: both
    /// <see cref="ApplyDeviceIdentity"/> and <see cref="RecordToViewModel"/> legitimately fill it
    /// from a FALLBACK — the live routing vendor when the entry reports 0, and a legacy
    /// <c>devices</c> row's stored routing pair. Re-keying towards a fallback would move rows in the
    /// WRONG DIRECTION (a legacy dongle-last RAMPAGE would migrate its canonical 10473 rows into
    /// 1389), silently and with no undo. Reading the entry makes that unrepresentable, and makes
    /// "an older DeviceHost runs no migration" structural rather than incidental.
    /// </para>
    /// <para>
    /// Returns an empty list — and allocates nothing — for every device that needs no heal: a
    /// non-merged product, an older DeviceHost that sends no pin or no transport list, and a merged
    /// product whose transports all share one vendor.
    /// </para>
    /// </remarks>
    internal static IReadOnlyList<(int FromVendorId, int ToVendorId, int ProductId)> PendingVendorRekeys(
        DeviceCatalogEntry entry)
    {
        // 0 is the "identity unknown" sentinel on the wire; re-keying to or from it would address
        // every pre-scoping row in the database rather than this product's.
        if (!entry.MergeTransports
            || entry.SettingsVendorId <= 0
            || entry.SettingsProductId <= 0
            || entry.TransportVendorIds.Count == 0)
            return [];

        List<(int FromVendorId, int ToVendorId, int ProductId)>? passes = null;
        foreach (var alternateVendorId in entry.TransportVendorIds)
        {
            if (alternateVendorId <= 0 || alternateVendorId == entry.SettingsVendorId)
                continue;

            (passes ??= []).Add((alternateVendorId, entry.SettingsVendorId, entry.SettingsProductId));
        }
        return passes ?? (IReadOnlyList<(int, int, int)>)[];
    }

    // Collapses the settings buckets a merged product owns under a vendor other than its pinned one.
    // Driven by transport_vendor_ids rather than by the LIVE vendor, so a user who has moved
    // permanently to the cable still heals their dongle-era rows -- with the live vendor alone the
    // heal would only ever fire while the device sat on the transport that caused the split.
    private void HealSettingsVendor(DeviceCatalogEntry entry)
    {
        foreach (var pass in PendingVendorRekeys(entry))
        {
            bool firstAttempt;
            lock (_settingsVendorRekeyGate)
                firstAttempt = _settingsVendorRekeyed.Add(pass);
            if (!firstAttempt)
                continue;

            // On the storage executor, never on the discovery thread and never on the dispatcher:
            // the pass is one transaction over every settings table plus a database snapshot.
            // Reserved in the set BEFORE the enqueue so the ticks between enqueue and completion
            // cannot queue the same pass twice, and RELEASED again if the write faults: the set is
            // an optimisation, not the correctness guard, so a failed pass must stay retryable by a
            // later tick in the same session rather than only by the next launch.
            var (fromVendorId, toVendorId, productId) = pass;
            _ = _localStorage.RunOnStorageAsync(() =>
                    _localStorage.RekeySettingsVendor(fromVendorId, toVendorId, productId))
                .ContinueWith(
                    t =>
                    {
                        _logger.LogError(t.Exception,
                            "Settings-vendor re-key {From}->{To} for product {Product} failed; a later tick retries.",
                            fromVendorId, toVendorId, productId);
                        lock (_settingsVendorRekeyGate)
                            _settingsVendorRekeyed.Remove(pass);
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }
    }

    // Sets the card's two identities from the live transport that won the wired-over-dongle pick:
    //   routing  — where device COMMANDS go; follows whichever transport is connected now.
    //   settings — which bucket SETTINGS persist under. For a merged-transport product all three
    //              parts are pinned per product (catalog SettingsVendorId + SettingsProductId +
    //              empty key), so switching cable↔dongle↔Bluetooth keeps the saved RGB / buttons.
    //              Otherwise they mirror routing.
    private static void ApplyDeviceIdentity(
        DeviceCardViewModel card, DeviceCatalogEntry entry, int vendorId, int productId, string routingKey)
    {
        card.VendorId = vendorId;
        card.ProductId = productId;
        card.DeviceKey = routingKey;

        card.MergeTransports = entry.MergeTransports;
        // The vendor is pinned for the same reason the product id is: a merged product's transports
        // do not all enumerate under the product's own vendor (RAMPAGE's receiver is 1389, the board
        // 10473), so the LIVE vendor gave that one device a second settings bucket. 0 on the entry
        // means an older DeviceHost that does not send the pin — fall back to the live vendor, which
        // is exactly what this did before the field existed.
        card.SettingsVendorId = entry.MergeTransports && entry.SettingsVendorId > 0
            ? entry.SettingsVendorId
            : vendorId;
        card.SettingsProductId = entry.MergeTransports ? entry.SettingsProductId : productId;

        // Routing identity always follows the port the device is on now. The SETTINGS identity must
        // NOT: it is the key every saved preset, binding and RGB row is stored under, so rewriting it
        // on a port move would strand all of them under the old key and the user's configuration
        // would silently vanish.
        //
        // So a card that already owns a settings key keeps it for life, and only adopts one when it
        // has none (a fresh card, or a legacy card restored from storage without a key). Combined
        // with the disconnected-card reuse above, this is what makes a mouse keep its settings when
        // it moves to another USB port — without needing to rewrite any stored rows.
        if (entry.MergeTransports)
            card.SettingsDeviceKey = string.Empty;
        else if (string.IsNullOrEmpty(card.SettingsDeviceKey))
            card.SettingsDeviceKey = routingKey;
    }

    // On (re)connect, restore what the user last put on the device — button remaps, DPI table and
    // stage, report rate, debounce / lift-off AND lighting — in ONE hardware transaction.
    //
    // This was two calls until 2026-08-17 (a lighting push plus a preset apply). The preset apply
    // already carries the stored lighting, so the pair sent the same LED bytes twice in a burst, and
    // BLAZE intermittently came up from a cold start with its buttons dead as a result. The single
    // entry point falls back to a lighting-only push for the devices the preset path does not serve
    // — keyboards, transports that cannot carry configuration, a device on its default preset — so
    // no device lost its restore. See DeviceDetailsViewModel.ReapplyOnConnectAsync.
    //
    // resetWhenUnset is set only by the owner-change sweep: a device the NEW account has never
    // configured must not keep the previous account's lighting. An ordinary reconnect leaves it
    // false, so a never-configured device still comes up on its factory lighting as before.
    private void ReapplyOnConnect(DeviceCardViewModel card, bool resetWhenUnset = false)
    {
        if (card.VendorId <= 0)
            return;

        var details = _services.GetRequiredService<DeviceDetailsViewModel>();
        _ = details.ReapplyOnConnectAsync(card, resetWhenUnset);
    }

    // The owner a connect-time restore was last driven for. Null until one resolves.
    private SettingsOwner? _lastRestoredOwner;

    // Sign-out (owner → null) leaves nothing to key a restore off, but the transition is still
    // recorded by the caller so the NEXT sign-in reads as a change. An unchanged owner is ignored
    // because IsGuest and CustomerId can both fire for a single sign-in, and a redundant sweep is a
    // second hardware write per device — which is exactly what BLAZE's firmware does not tolerate.
    internal static bool ShouldRestoreForOwner(SettingsOwner? owner, SettingsOwner? lastRestored) =>
        owner is not null && !owner.Equals(lastRestored);

    // The last owner the sweep actually restored for. Deliberately NOT the same field as
    // _lastRestoredOwner, which sign-out resets to null so the next sign-in reads as a change: this
    // one answers a different question — WHOSE colours may be sitting on the board right now — and a
    // sign-out does not take the departing account's lighting off the hardware, so it must not clear
    // this. Null only until the first owner of the process has been restored for.
    private SettingsOwner? _lastLitOwner;

    // Whether the sweep may RESET lighting the new owner has never configured, rather than only
    // restoring what it has.
    //
    // True on a genuine account-to-account switch and nothing else. The FIRST owner of a session
    // (launch with "Remember me", guest start, a sign-in from the Welcome screen) is not a switch —
    // no other account's colours are on the board to take off it — and resetting there would
    // overwrite a never-configured device's FACTORY lighting with the app's defaults on every single
    // launch. Signing out and back into the SAME account is not a switch either: the board is
    // already wearing that account's own lighting. See DeviceDetailsViewModel.ReapplyPersistedRgbAsync.
    internal static bool ShouldResetUnconfiguredLighting(SettingsOwner? owner, SettingsOwner? lastLit) =>
        owner is not null && lastLit is not null && !Nullable.Equals(owner, lastLit);

    // Test seam (InternalsVisibleTo ProoveGaming.App.Tests), like PostToUi. Both "who was the sweep
    // last driven for" fields are written only by the sweep itself, so seeding them is the only way
    // a test can drive the sweep from a chosen transition; the getter is what makes a sign-out's
    // "clears the restored owner, keeps the lit one" observable.
    internal (SettingsOwner? Restored, SettingsOwner? Lit) OwnerSweepState
    {
        get => (_lastRestoredOwner, _lastLitOwner);
        set => (_lastRestoredOwner, _lastLitOwner) = value;
    }

    // Re-drives the connect-time restore for everything already connected, when the settings owner
    // changes.
    //
    // ReapplyOnConnectAsync keys every read off the owner, so it returns without doing anything at
    // all while the owner is null — and it only ever runs on the connect TRANSITION, which by then
    // has passed. On any launch that does not auto-restore an owner (first run, after a sign-out, an
    // expired "Remember me" or an expired guest session) the DeviceHost is already streaming devices
    // while the user is still on the Welcome screen: the cards appear, the restore silently no-ops,
    // and the device keeps whatever lighting and configuration it powered on with until the user
    // happens to open its details page — which is what made a BLAZE's RGB change only on the first
    // click of its card.
    //
    // Firing on the owner transition instead closes that window: guest start, sign-in and session
    // restore all land here, and the devices get the same restore they would have got had the owner
    // been known when they connected.
    // Internal rather than private so a test can drive the sweep itself, not just the predicates and
    // the marking helper it calls — see OwnerSweepState for what that buys.
    internal void RestoreConnectedDevicesForNewOwner()
    {
        var owner = _services.GetService<ICurrentOwnerProvider>()?.GetCurrent();
        var restore = ShouldRestoreForOwner(owner, _lastRestoredOwner);
        // A sign-out (owner → null) restores nothing, but it IS an owner change and the details view
        // model's per-owner caches must not outlive it — hence a test of its own rather than reusing
        // `restore`. Both singletons, so this resolves the one instance ReapplyOnConnect uses.
        var ownerChanged = !Nullable.Equals(owner, _lastRestoredOwner);
        // Resolved BEFORE _lastLitOwner is overwritten below — it compares the incoming owner
        // against the one whose lighting is on the hardware.
        var resetUnconfigured = ShouldResetUnconfiguredLighting(owner, _lastLitOwner);
        _lastRestoredOwner = owner;

        if (ownerChanged)
            _services.GetRequiredService<DeviceDetailsViewModel>().OnOwnerChanged(owner);

        if (!restore) return;

        _lastLitOwner = owner;

        var connected = Devices.OfType<DeviceCardViewModel>().Where(c => c.Connected).ToList();

        // The sweep deliberately writes NO pending-apply mark for the incoming owner. It did between
        // 14be823 and 2026-09-12, and that destroyed data: the mark's consumers mean "a preset
        // arrived from the server and has never been loaded here", so they answer it with
        // LoadPresetSnapshotToDevice, which overwrites the live rows with the snapshot. On an
        // ordinary account switch the incoming account's applied-but-unsaved state is legitimate
        // (DeviceDetailsViewModel.Presets.cs says so where it refuses to read the snapshot instead),
        // so spending the mark there silently threw away its DPI, colours and remaps
        // (docs/plans/add-cyrex-extended-keys, review round 4, M1).
        //
        // What still carries a switch: extended keys by the self-heal that runs on every page open,
        // lighting by the per-card restore below, and keyboard remaps / actuation by the accepted
        // limitation in docs/plans/fix-extended-keys-self-heal. The mark stays what it always was —
        // the sign-in pull's own mechanism (SettingsSyncService), consumed once by the next page open.

        // resetWhenUnset only on an account-to-account switch: a device the new owner has never
        // configured is reset to the app's default lighting instead of keeping the previous
        // account's colours. On the first owner of the session there is no previous account, so a
        // never-configured device keeps its factory lighting — the ordinary launch behaviour.
        // See DeviceDetailsViewModel.ReapplyPersistedRgbAsync.
        foreach (var card in connected)
            ReapplyOnConnect(card, resetWhenUnset: resetUnconfigured);
    }

    private async Task HandleBluetoothDeviceFoundAsync(BluetoothDeviceResponse deviceModel)
    {
        if (string.IsNullOrEmpty(deviceModel.ClientName))
        {
            return;
        }

        var entry = deviceModel.CatalogEntry;
        if (entry == null) return;

        var found = Devices.OfType<DeviceCardViewModel>()
            .FirstOrDefault(x => string.Equals(x.Name, deviceModel.ClientName.ToUpper(), StringComparison.OrdinalIgnoreCase));

        if (found != null)
        {
            var wasConnected = found.Connected;
            var wasBluetoothLive = found.HasBluetooth;
            // Same re-persist rule as the HID discovery path: FillBluetoothCard restamps the name
            // from the catalog and (through ApplyBluetoothIdentity) both identities, and this path
            // previously never persisted an existing card at all — so a restored card whose stored
            // settings_* columns the Bluetooth stamp corrected kept the stale bucket forever.
            // Steady-state Bluetooth events restamp identical values and write nothing.
            var identityBefore = CardIdentity(found);
            var settingsBefore = SettingsIdentityOf(found);
            FillBluetoothCard(found, entry, deviceModel.IsConnected, deviceModel.DeviceId);
            if (CardIdentity(found) != identityBefore || SettingsIdentityOf(found) != settingsBefore)
                PersistDeviceCard(found);
            if (deviceModel.IsConnected && deviceModel.BatteryLevel > 0)
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    found.BatteryLevel = deviceModel.BatteryLevel;
                    found.HasBattery = true;
                });
            else if (ShouldInvalidateBatteryOnTransportSwitch(
                wasBluetoothLive, found.HasBluetooth, deviceModel.BatteryLevel))
            {
                var stale = found;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    stale.BatteryLevel = null;
                    stale.HasBattery = false;
                });
            }

            if (!deviceModel.IsConnected && !found.HasWire && !found.HasDongle)
            {
                found.HasBattery = false;
                found.BatteryLevel = null;
                found.IsCharging = false;
            }

            if (wasConnected && !found.Connected)
            {
                var disconnected = found;
                await Dispatcher.UIThread.InvokeAsync(() => AddNotification(LocalizationManager.GetString("Notification_DeviceDisconnected"), disconnected.DisplayName, DeviceDisconnectedIcon));
            }
            else if (!wasConnected && found.Connected)
            {
                var reconnected = found;
                await Dispatcher.UIThread.InvokeAsync(() => AddNotification(LocalizationManager.GetString("Notification_DeviceConnected"), reconnected.DisplayName, DeviceConnectedIcon));
                // Same restore the USB path does on reconnect — a board whose lighting the App had
                // set must come back lit over Bluetooth too. Self-gating (known identity, an enabled
                // saved state), so headsets and unknown cards are no-ops. In practice this resolves
                // to the lighting-only fallback today: the boards that reach it are keyboards, and
                // TAIPAN over Bluetooth cannot carry configuration.
                ReapplyOnConnect(found);
            }
        }
        else
        {
            if (!deviceModel.IsConnected) return;

            var deviceCard = (DeviceCardViewModel)_services.GetRequiredService(ParseCardSize(entry.CardType));
            FillBluetoothCard(deviceCard, entry, deviceModel.IsConnected, deviceModel.DeviceId);
            if (deviceModel.BatteryLevel > 0)
            {
                deviceCard.BatteryLevel = deviceModel.BatteryLevel;
                deviceCard.HasBattery = true;
            }
            WireDelete(deviceCard);
            PersistDeviceCard(deviceCard);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var addCard = Devices.OfType<AddDeviceCardViewModel>().FirstOrDefault();
                var insertIndex = addCard != null ? Devices.IndexOf(addCard) : Devices.Count;
                Devices.Insert(insertIndex, deviceCard);
                AddNotification(LocalizationManager.GetString("Notification_NewDeviceConnected"), deviceCard.DisplayName, DeviceConnectedIcon);
            });
            ReapplyOnConnect(deviceCard);
        }

        await TrackAudioCardsAsync();
    }

    /// <summary>
    /// Whether this card's battery may be read over HID. A Bluetooth-live card must NOT be: its
    /// level comes from the GATT Battery Service, which is the mouse's own percentage and the one
    /// Windows shows in the tray.
    /// </summary>
    /// <remarks>
    /// Both sources exist at once whenever the 2.4G receiver is still plugged in while the mouse is
    /// switched to Bluetooth — the receiver enumerates regardless of whether any mouse is talking
    /// through it. Polling then answered from the TAIPAN protocol cache, whose curve treats raw 985
    /// as full while real hardware pushes up to 1008, so it clamps to 100 %. The card showed a
    /// permanent 100 % over the accurate GATT value (2026-08-13). `PollBatteriesAsync` already
    /// excluded Bluetooth cards for this reason; the connect-time acquire did not.
    ///
    /// SupportsBattery is checked here for the same reason, and it is the half that was missed
    /// (2026-08-14): `FillConnectionCard` correctly sets `HasBattery = false` for a transport whose
    /// catalog row declares no usable battery — TAIPAN on its cable, where the reading is charge
    /// voltage — and then this acquire ran anyway, got ~100 % from the DeviceHost, and set
    /// `HasBattery = true` straight back over it. The indicator lit at 100 % and, because
    /// `PollBatteriesAsync` DOES filter on SupportsBattery, nothing ever refreshed it away again.
    /// Both filters must agree; the connect-time acquire is the one that shows the first value.
    /// </remarks>
    /// <summary>
    /// Whether a card's existing battery reading must be discarded because its transport just became
    /// Bluetooth and no GATT level has arrived yet.
    /// </summary>
    /// <remarks>
    /// The two sources are computed differently. Over the dongle the level is DERIVED from a raw
    /// value through <c>TaipanStateReport.PercentFromRaw</c>; over Bluetooth it is the mouse's own
    /// GATT percentage — the number Windows puts in the tray. Compared properly (2026-08-26, dongle
    /// and Bluetooth read minutes apart at the same charge) they agree to within a point, so this is
    /// NOT about the curve being wrong; it is about a reading from one transport outliving the
    /// transport it came from.
    ///
    /// A mouse is only ever on ONE link, so the two figures cannot be sampled at the same instant —
    /// which is exactly how an earlier comparison here paired a dongle raw value against a GATT
    /// percentage logged during a different session and concluded, wrongly, that they differed by
    /// ~40 points. Any future calibration needs readings taken minutes apart on each transport, not
    /// whatever the log happened to hold.
    ///
    /// On switching to Bluetooth the dongle-derived figure therefore becomes wrong, and it used to
    /// survive: <c>ShouldPollHidBattery</c> and <c>ApplyBatteryPush</c> both correctly stop touching a
    /// Bluetooth-live card, so nothing refreshed the stale value away — the App showed 100 % beside a
    /// tray showing 56 %. The first event on a Bluetooth connect carries <c>BatteryLevel = -1</c>
    /// (the level is read separately, moments later), so "not yet known" is the normal first state
    /// and cannot be left showing the old source's answer.
    ///
    /// Gated on the TRANSITION rather than on every event with no level, so a steady Bluetooth card
    /// does not flicker each time a connection-state push arrives without a reading. Clearing to
    /// unknown is deliberate: a wrong indicator is worse than none, because the user cannot tell it
    /// apart from a real reading — the same rule the Cosmo battery note applies.
    /// </remarks>
    internal static bool ShouldInvalidateBatteryOnTransportSwitch(
        bool wasBluetoothLive, bool isBluetoothLiveNow, int incomingLevel) =>
        isBluetoothLiveNow && !wasBluetoothLive && incomingLevel <= 0;

    internal static bool ShouldPollHidBattery(DeviceCardViewModel card, bool hasHidBatterySource) =>
        hasHidBatterySource && !card.HasBluetooth && card.SupportsBattery;

    /// <summary>Whether a card should report itself as charging.</summary>
    /// <remarks>
    /// A cable means charging, full stop. This was briefly gated on <c>SupportsBattery</c> as well, to
    /// keep the icon off BLAZE — a cable-only mouse with no battery in it — but that flag means "the
    /// level is readable on THIS transport", not "the device has a battery". TAIPAN on its cable
    /// declares it false because the level cannot be read there, so the gate also took the icon away
    /// from a mouse that genuinely was charging. Reverted deliberately on 2026-08-17: showing it on a
    /// device with nothing to charge is the smaller wrong than hiding it on one that is.
    ///
    /// Telling those two apart needs a device-level "has a battery at all" capability, distinct from
    /// the per-transport one, which the catalog does not carry today.
    ///
    /// Bluetooth still excludes charging: the cable is what charges, and a card can light both
    /// transports at once.
    /// </remarks>
    internal static bool ShouldShowCharging(DeviceCardViewModel card, bool hasWired, bool bluetooth) =>
        hasWired && !bluetooth;

    /// <summary>
    /// The PG-494 transport-priority rules — wired > bluetooth > dongle — extracted for tests
    /// (same technique as <see cref="ShouldShowCharging"/>).
    /// </summary>
    /// <remarks>
    /// <c>BluetoothLinkUp</c> is the link's REAL state: OR-ed with this snapshot's HID evidence
    /// and never suppressed by the cable, because <c>HandleUsbHidDeviceFoundAsync</c>'s guard
    /// reads it to keep a Bluetooth-alive card from being marked disconnected when the cable is
    /// pulled. <c>Bluetooth</c> is the DISPLAY flag: a live wired data interface is the strongest
    /// evidence of the physical switch position, while the OS Bluetooth stack is known to keep
    /// reporting a stale "connected" (see BluetoothDeviceDiscoveryService's Gatt.IsConnected
    /// notes), so the cable wins the icon. The dongle ranks last because its USB receiver
    /// enumerates whenever it is plugged in, whether or not a device is talking through it — its
    /// presence is NOT evidence of a live link.
    /// </remarks>
    internal static (bool Wire, bool Bluetooth, bool Dongle, bool BluetoothLinkUp) ResolveTransports(
        bool bluetoothLinkUp, bool hasBluetoothHid, bool hasWired, bool hasDongle)
    {
        var linkUp = bluetoothLinkUp || hasBluetoothHid;
        var bluetooth = linkUp && !hasWired;
        var dongle = hasDongle && !hasWired && !bluetooth;
        return (hasWired, bluetooth, dongle, linkUp);
    }

    /// <summary>
    /// Applies a battery change the DeviceHost pushed, so the card updates as soon as the hardware
    /// reports rather than on the next 60 s poll.
    /// </summary>
    /// <remarks>
    /// Matched on (VID, PID, DeviceKey) — the routing identity the card was stamped with — so with
    /// two identical mice connected the push lands on the right one. A card whose live transport is
    /// Bluetooth is skipped: its level comes from GATT, and this is the HID/dongle path (the same
    /// rule ShouldPollHidBattery applies).
    ///
    /// Known=false clears the indicator: for TAIPAN that is the dongle reporting no mouse behind it.
    /// </remarks>
    internal static void ApplyBatteryPush(DeviceCardViewModel card, bool known, int percent, bool isCharging)
    {
        if (card.HasBluetooth || !card.SupportsBattery) return;

        if (!known)
        {
            card.BatteryLevel = null;
            card.HasBattery = false;
            // IsCharging has to be cleared too, and it is NOT covered by HasBattery: the indicator
            // is shown when `HasBattery || IsCharging`, and BatteryIconKind tests IsCharging first —
            // so a stale `true` survives this clear and renders a charging icon with an empty label
            // for a device we have no reading from. Falls back to HasWire rather than plain false
            // because on a cable the charging IS real, reading or not.
            card.IsCharging = card.HasWire;
            return;
        }

        card.BatteryLevel = percent;
        card.HasBattery = true;
        card.IsCharging = isCharging || card.HasWire;
    }

    /// <summary>
    /// Finds the card a pushed battery change belongs to, matching the routing identity the card was
    /// stamped with.
    /// </summary>
    /// <remarks>
    /// Device key first, so two identical mice connected at once each get their own push. An empty
    /// key means the DeviceHost could not read a Container ID, and only then does it fall back to
    /// VID/PID — the same single-device fallback used elsewhere here. A non-empty key that matches
    /// nothing deliberately matches NOTHING rather than falling back, or one mouse's reading would
    /// land on the other's card.
    /// </remarks>
    internal static DeviceCardViewModel? MatchBatteryCard(
        IEnumerable<DeviceCardViewModel> cards, int vendorId, int productId, string? deviceKey)
    {
        var key = deviceKey ?? string.Empty;
        var candidates = cards as IReadOnlyCollection<DeviceCardViewModel> ?? [.. cards];

        var card = candidates.FirstOrDefault(c =>
            c.VendorId == vendorId &&
            c.ProductId == productId &&
            string.Equals(c.DeviceKey ?? string.Empty, key, StringComparison.OrdinalIgnoreCase));

        if (card is not null || !string.IsNullOrEmpty(key)) return card;

        return candidates.FirstOrDefault(c => c.VendorId == vendorId && c.ProductId == productId);
    }

    private void OnBatteryChanged(object? sender, BatteryChange e)
    {
        // EVERYTHING runs on the UI thread, the lookup included. This handler is raised from the
        // gRPC stream-reading thread, and Devices is an ObservableCollection the UI thread mutates
        // (PersistDeviceCard, the delete path) — enumerating it from another thread while it is
        // being added to throws "Collection was modified".
        //
        // That throw would not stay local: it would unwind ReadServerResponses, whose task-level
        // catch reports the error and then ENDS the read loop, leaving the App receiving no device
        // events at all — connect, disconnect, DPI stage, report rate — until it is restarted. A
        // battery push arrives exactly when a mouse links up, which is the same moment cards are
        // being added, so the window is real rather than theoretical.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var card = MatchBatteryCard(Devices.OfType<DeviceCardViewModel>(),
                    e.VendorId, e.ProductId, e.DeviceKey);

                if (card is null) return;

                // A known push proves the battery readable RIGHT NOW, so the poll's miss streak
                // restarts — otherwise "poll missed → push restored the level → poll missed again"
                // counts two non-consecutive misses and clears a level the push just showed.
                // Keyed exactly like the poll's identity (the card's own key, not the event's,
                // which can be empty on the single-device fallback).
                if (e.Known)
                    _batteryMissStreak.Remove((card.VendorId, card.ProductId, card.DeviceKey ?? string.Empty));

                ApplyBatteryPush(card, e.Known, e.Percent, e.IsCharging);
            }
            catch (Exception ex)
            {
                // Nothing above should throw, but an unhandled exception on the dispatcher takes the
                // whole App down — too high a price for a battery indicator.
                _logger.LogDebug(ex, "Failed to apply a pushed battery change");
            }
        });
    }

    // Re-reads the battery of every connected device that has one, so the level shown is current
    // rather than whatever was read when the device connected.
    //
    // This is needed because RefreshBatteryAsync below is an ACQUIRE-with-retry, not a refresh: it
    // returns the moment a level exists, and it only runs on a device-list change. Without this
    // poll a card kept its connect-time percentage for the whole session — a headset or mouse could
    // run flat while the app still showed the level it had when it was plugged in.
    //
    // 60 s because a battery does not move faster than that; the low-battery notification is driven
    // off BatteryLevel changes and is guarded by _lowBatteryAnnouncedBand, so repeated polls cannot
    // spam.
    /// <summary>
    /// Whether this many consecutive polls with no reading is enough to clear the indicator.
    /// </summary>
    /// <remarks>
    /// The policy, not the bookkeeping: a single miss must never clear, because -1 from the
    /// DeviceHost covers temporary causes as well as real absence — a busy device gate during a key
    /// write, an unanswered HID read, a value outside 1-100, a null gRPC client. Clearing on one
    /// would blank a healthy mouse's indicator mid-edit.
    /// </remarks>
    internal static bool ShouldClearAfterMisses(int consecutiveMisses) =>
        consecutiveMisses >= BatteryMissesBeforeClearing;

    private async Task PollBatteriesAsync()
    {
        // Bluetooth-live cards are excluded: their level comes from GATT (UpdateBatteryAsync in
        // the DeviceHost), and now that such cards carry a real VID/PID the HID poll would
        // otherwise write un-gated battery frames at the device's Bluetooth HID interfaces,
        // interleaving with RGB write sessions there.
        //
        // Filtered on SupportsBattery (the capability) and NOT on HasBattery (whether a level is
        // currently known). A card with no level is precisely the one that needs polling: since the
        // poll started clearing the indicator for a mouse switched off behind its dongle, requiring
        // HasBattery here meant the card removed itself from the poll that would have brought it
        // back, and the level never returned when the mouse was switched on again.
        var cards = Devices.OfType<DeviceCardViewModel>()
            .Where(c => c.Connected && !c.HasBluetooth && c.SupportsBattery)
            .ToArray();

        foreach (var card in cards)
        {
            if (_knownNoBattery.ContainsKey((card.VendorId, card.ProductId))) continue;

            try
            {
                var (battery, isCharging) = await _usbHidDeviceService.GetBatteryLevelAsync(
                    card.VendorId, card.ProductId, card.DeviceKey);

                var identity = (card.VendorId, card.ProductId, card.DeviceKey ?? string.Empty);

                if (battery < 0)
                {
                    // -1 does NOT mean "this device has no battery". It is what the App sees for
                    // every unsuccessful answer, and several of those are purely temporary:
                    // HidDeviceDiscoveryService.TryGetBatteryLevel returns it when the device gate
                    // is held (a key-table write over 2.4G holds it for seconds — its own comment
                    // calls skipping the tick harmless), when the HID read does not answer, and when
                    // the parsed value falls outside 1-100. DeviceService's catch-all and a null
                    // gRPC client produce it too.
                    //
                    // So a single miss is not evidence of anything. Clearing on one would blank the
                    // indicator of a healthy mouse while the user is editing its key assignments —
                    // the same defect this branch set out to fix, only inverted. Require a second
                    // poll to agree before believing it.
                    //
                    // TAIPAN does not depend on this path: when its dongle reports no mouse behind
                    // it, the DeviceHost pushes known=false and the indicator clears at once. This
                    // is the backstop for devices with no push channel, and for a missed push.
                    _batteryMissStreak.TryGetValue(identity, out var misses);
                    misses++;
                    _batteryMissStreak[identity] = misses;

                    if (!ShouldClearAfterMisses(misses)) continue;

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        card.BatteryLevel = null;
                        card.HasBattery = false;
                        // Same reason as ApplyBatteryPush's clear: the indicator survives on
                        // IsCharging alone, so leaving it set shows a charging icon with no level.
                        card.IsCharging = card.HasWire;
                    });
                    continue;
                }

                _batteryMissStreak.Remove(identity);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    card.BatteryLevel = battery;
                    card.HasBattery = true;
                    card.IsCharging = isCharging || card.HasWire;
                });
            }
            catch (Exception ex)
            {
                // A failed poll leaves the previous reading in place; the next tick retries.
                _logger.LogDebug(ex, "Battery poll failed for VID={VID} PID={PID}", card.VendorId, card.ProductId);
            }
        }
    }

    private async Task RefreshBatteryAsync(DeviceCardViewModel card, int vendorId, int productId)
    {
        if (_knownNoBattery.ContainsKey((vendorId, productId))) return;

        for (int attempt = 0; attempt < BatteryRetryCount; attempt++)
        {
            try
            {
                if (attempt > 0) await Task.Delay(BatteryRetryDelay);
                // Acquire-only: stop retrying once a level exists. Keeping it CURRENT is
                // PollBatteriesAsync's job, above.
                if (card.BatteryLevel != null) return;
                var (battery, isCharging) = await _usbHidDeviceService.GetBatteryLevelAsync(vendorId, productId, card.DeviceKey);
                if (battery >= 0)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        card.BatteryLevel = battery;
                        card.HasBattery = true;
                        card.IsCharging = isCharging || card.HasWire;
                    });
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Battery query attempt {Attempt} failed for VID={VID} PID={PID}",
                    attempt, vendorId, productId);
            }
        }

        // Only devices that do NOT declare a battery are written off permanently. This list exists
        // to stop pointless retries against hardware that has no battery at all — but for a device
        // whose catalog transport declares one, "no reading" is a TEMPORARY state: TAIPAN's dongle
        // answers with a zero reading whenever the mouse is switched off, out of range or flat. A
        // mouse that happened to be off when its card first connected would otherwise be blacklisted
        // for the life of the process and never show a level again, however long it ran afterwards.
        if (card.SupportsBattery)
        {
            _logger.LogDebug("No battery reading yet for VID={VID} PID={PID} after {Attempts} attempts; " +
                "the catalog says this transport has one, so it will be retried by the poll",
                vendorId, productId, BatteryRetryCount);
            return;
        }

        _knownNoBattery.TryAdd((vendorId, productId), 0);
        _logger.LogInformation("Marking VID={VID} PID={PID} as no-battery after {Attempts} failed attempts",
            vendorId, productId, BatteryRetryCount);
    }

    private void FillConnectionCard(DeviceCardViewModel card, DeviceCatalogEntry entry, bool hasWired, bool hasDongle,
        bool hasBluetoothHid = false)
    {
        // An old DeviceHost sends 0 for the whole Defaults block, which is indistinguishable from a
        // device that genuinely declares none — the App just quietly restores to a different value.
        // Logging what actually arrived turns that into something visible instead of a wrong number
        // the user has to spot by eye. See docs/device-defaults.md.
        _logger.LogInformation(
            "[Catalog] {Name}: defaultDpi={Dpi} defaultRateHz={Rate} defaultDebounceMs={Debounce} " +
            "defaultLiftOffMm={LiftOff} defaultRgbEffect='{Effect}' configUnsupported={NoConfig} " +
            "audioSettings=[{Audio}] audioTarget='{AudioTarget}' batteryUnsupported={NoBattery} " +
            "(0/''/false/empty = not sent by the DeviceHost — check it was rebuilt and restarted)",
            entry.Name, entry.DefaultDpi, entry.DefaultReportRateHz, entry.DefaultButtonDebounceMs,
            entry.DefaultLiftOffDistanceMm, entry.DefaultRgbEffect, entry.ConfigurationUnsupported,
            string.Join(',', entry.SupportedAudioSettings), entry.AudioVolumeTarget, entry.BatteryUnsupported);

        card.Name = entry.DeviceName ?? entry.Name;
        card.CatalogDisplayName = string.IsNullOrEmpty(entry.DisplayName) ? null : entry.DisplayName;
        card.DeviceTypeLabel = entry.DeviceType ?? string.Empty;
        card.ImagePath = string.IsNullOrEmpty(entry.ImagePath) ? null : entry.ImagePath;
        card.RgbImagePath = string.IsNullOrEmpty(entry.RgbImagePath) ? null : entry.RgbImagePath;
        // Fire-and-forget: rasterize this device's glow-pass-through bitmap as soon as it's known,
        // not when the user opens its details — DeviceDetailsViewModel.InitDeviceOnlySvg only ever
        // reads this cache now, it never populates it, so Details would otherwise show the raw-Svg
        // fallback forever for a device this never ran for.
        _ = DeviceDetailsViewModel.PrewarmDeviceOnlyBitmapAsync(card.RgbImagePath);
        // Same treatment for a keyboard's split artwork: parse the chassis/keycap SVGs now, so
        // Details renders the assembled board on its first frame instead of decoding on open.
        _ = RgbKeyboardArtCatalog.PrewarmAsync(card.Name);
        card.SupportsRgb = entry.SupportsRgb;
        card.SupportsButtonRemap = entry.SupportsButtonRemap;
        card.SupportsMacros = entry.SupportsMacros;
        card.SupportsActuation = entry.SupportsActuation;
        card.SupportsExtendedKeys = entry.SupportsExtendedKeys;
        card.SupportedMouseSettings = [.. entry.SupportedMouseSettings];
        card.SupportedAudioSettings = [.. entry.SupportedAudioSettings];
        card.AudioVolumeTarget = AudioVolumeTargetParser.Parse(entry.AudioVolumeTarget);
        card.SupportsBattery = !entry.BatteryUnsupported;
        card.DpiStages = [.. entry.DpiStages];
        card.DpiMin = entry.DpiMin;
        card.DpiMax = entry.DpiMax;
        card.DpiStep = entry.DpiStep;
        card.DpiSlotMin = entry.DpiSlotMin;
        card.DpiSlotMax = entry.DpiSlotMax;
        card.ImageScale = entry.ImageScale > 0 ? entry.ImageScale : 1.0;
        card.KeyOverlayArtDim = entry.KeyOverlayArtDim;
        card.MacroMaxDelayMs = entry.MacroMaxDelayMs;
        card.MacroMaxRepetitions = entry.MacroMaxRepetitions;
        card.Defaults = ToDeviceDefaults(entry);
        // bestEntry is chosen with a wired preference and otherwise describes the transport the
        // device is actually on, so this is the capability of the LIVE connection.
        card.SupportsConfiguration = !entry.ConfigurationUnsupported;
        card.Hotspots = [.. entry.Hotspots.Select(h =>
            new DeviceHotspotInfo(h.Id, h.ButtonX, h.ButtonY, h.TargetX, h.TargetY, h.LabelSide, h.Disabled))];
        // Bluetooth knowledge can arrive from the OS Bluetooth stack via FillBluetoothCard
        // (recorded on card.BluetoothLinkUp), or — for a BLE mouse like TAIPAN — through HID
        // discovery as hasBluetoothHid. ResolveTransports OR-s them into the link's real state
        // and derives the display flags from it (wired > bluetooth > dongle); the full rule set
        // and its rationale live on that method.
        var (wire, bluetooth, dongle, linkUp) =
            ResolveTransports(card.BluetoothLinkUp, hasBluetoothHid, hasWired, hasDongle);
        card.BluetoothLinkUp = linkUp;
        card.HasBluetooth = bluetooth;
        card.HasWire = wire;
        card.HasDongle = dongle;
        // Set AFTER card.SupportsBattery is filled from the catalog entry above — the rule reads it.
        card.IsCharging = ShouldShowCharging(card, hasWired, bluetooth);
        if (!bluetooth)
        {
            // Clearing the level must also clear the flag. HasBattery=true with a null level is the
            // one state that renders as the red "critical" icon showing a dash — BatteryText falls
            // through to "-" and BatteryIconKind falls past every level comparison to Critical. The
            // old line asserted a battery from the mere PRESENCE of a dongle, so switching a mouse
            // off (or off Bluetooth onto an idle receiver) left the card claiming a critical battery
            // it had never read.
            //
            // The indicator is turned on by the first real reading instead — the DeviceHost's push
            // (OnBatteryChanged) or the 60 s poll. A device that genuinely has no battery therefore
            // shows nothing at all, which is what it should show.
            card.BatteryLevel = null;
            card.HasBattery = false;
        }
    }

    // bluetoothDeviceId: for a CLASSIC Bluetooth device this is the Windows Container ID, which is
    // also what its audio endpoints report — see the classic path in BluetoothDeviceDiscoveryService.
    private void FillBluetoothCard(DeviceCardViewModel card, DeviceCatalogEntry entry, bool isConnected,
        string? bluetoothDeviceId = null)
    {
        card.Name = entry.DeviceName ?? entry.Name;
        card.CatalogDisplayName = string.IsNullOrEmpty(entry.DisplayName) ? null : entry.DisplayName;
        card.DeviceTypeLabel = entry.DeviceType ?? string.Empty;
        card.ImagePath = string.IsNullOrEmpty(entry.ImagePath) ? null : entry.ImagePath;
        card.RgbImagePath = string.IsNullOrEmpty(entry.RgbImagePath) ? null : entry.RgbImagePath;
        _ = DeviceDetailsViewModel.PrewarmDeviceOnlyBitmapAsync(card.RgbImagePath);
        _ = RgbKeyboardArtCatalog.PrewarmAsync(card.Name);
        // Belongs with ImagePath: the scale compensates for padding baked into THAT artwork, so a
        // card that gets one without the other renders the image at the wrong size. This path had
        // ImagePath and not ImageScale, which is why a Bluetooth-connected headset ignored the
        // catalog value entirely while the same device over its dongle honoured it.
        card.ImageScale = entry.ImageScale > 0 ? entry.ImageScale : 1.0;
        card.SupportsRgb = entry.SupportsRgb;
        card.SupportsButtonRemap = entry.SupportsButtonRemap;
        card.SupportsMacros = entry.SupportsMacros;
        card.SupportsActuation = entry.SupportsActuation;
        card.SupportsExtendedKeys = entry.SupportsExtendedKeys;
        card.SupportedMouseSettings = [.. entry.SupportedMouseSettings];
        card.SupportedAudioSettings = [.. entry.SupportedAudioSettings];
        card.AudioVolumeTarget = AudioVolumeTargetParser.Parse(entry.AudioVolumeTarget);
        card.SupportsBattery = !entry.BatteryUnsupported;
        card.KeyOverlayArtDim = entry.KeyOverlayArtDim;

        // A headset's audio endpoints live under a different container per transport, so the audio
        // identity has to follow the live one. Set only while Bluetooth is actually connected, and
        // CLEARED otherwise: leaving it set would point the sliders at the Bluetooth endpoints after
        // the headset moved back to its dongle, where they are NOTPRESENT and nothing would happen.
        //
        // Gated on the device having audio at all, so a BLE mouse or keyboard — whose DeviceId is a
        // BLE id and not a container — never acquires a meaningless one.
        if (card.SupportedAudioSettings.Count > 0)
        {
            card.AudioContainerId = isConnected && !string.IsNullOrEmpty(bluetoothDeviceId)
                ? bluetoothDeviceId
                : string.Empty;
        }

        card.MacroMaxDelayMs = entry.MacroMaxDelayMs;
        card.MacroMaxRepetitions = entry.MacroMaxRepetitions;
        card.Defaults = ToDeviceDefaults(entry);
        // Only claim the Bluetooth transport's capability when Bluetooth is actually the live
        // connection. A merged-transport product is discovered on every transport it is paired for,
        // so a paired-but-disconnected Bluetooth report would otherwise overwrite the capability of
        // the connection the device is really using — which is how a TAIPAN sitting on its dongle
        // ended up marked configurable at app start.
        // USB priority (PG-494): with the cable's data interface enumerated, an OS-stack
        // "connected" is stale — Windows keeps reporting the board's Bluetooth as connected
        // while the cable carries the traffic (ApplyBluetoothIdentity's note; it already
        // self-guards on HasWire). The old unconditional flip is what kept a card on
        // Bluetooth after the user switched the device to USB. The ordering risk is small:
        // a genuine cable→Bluetooth switch drops the wired interface first (its unplug
        // triggers a HID snapshot that clears HasWire) and the slower GATT connect lands
        // after it.
        // The real link state is recorded unsuppressed (see BluetoothLinkUp's doc); only the
        // DISPLAY flag below defers to a live cable.
        card.BluetoothLinkUp = isConnected;
        var bluetoothLive = isConnected && !card.HasWire;
        if (bluetoothLive)
        {
            card.SupportsConfiguration = !entry.ConfigurationUnsupported;
            ApplyBluetoothIdentity(card, entry);
            HealSettingsVendor(entry);
        }
        card.HasBluetooth = bluetoothLive;
        if (bluetoothLive)
        {
            card.HasDongle = false;
        }
        else
        {
            // Bluetooth zeroed these while it was the live transport, and nothing else recomputes
            // them until the next HID event — so without this the card goes dark the moment
            // Bluetooth drops, even though the cable or receiver is still carrying the device.
            // Reproduced on Cosmo: switch the headset from Bluetooth to its 2.4G dongle and the
            // card went inactive while audio kept playing, recovering only on a power cycle.
            RestoreHidTransports(card);
        }
    }

    // Re-derives the wired / dongle flags from what the DeviceHost currently enumerates over HID.
    //
    // Needed only because FillBluetoothCard asserts exclusivity: while Bluetooth is live it writes
    // false into both, which is deliberate — an idle 2.4G receiver enumerates whether or not
    // anything is talking through it, so "a dongle is present" is not evidence of a live link (see
    // the priority comment in FillConnectionCard). That assertion has no counterpart on the way
    // back out, and Cosmo is the first device where both transports are genuinely live at once.
    //
    // Derived the same way the HID path derives them, from the catalog entry's connection type, so
    // the two cannot disagree about what a transport is.
    private void RestoreHidTransports(DeviceCardViewModel card)
    {
        if (_usbHidDeviceService is null || string.IsNullOrEmpty(card.Name)) return;

        // An empty enumeration is also what GetDevices returns when the DeviceHost cannot be
        // reached at all, and the two are indistinguishable from here. Applying it would clear
        // HasWire on a device whose cable is still plugged in — the exact "card went dark while
        // audio kept playing" failure this method exists to prevent, only triggered by the host
        // dying instead of by Bluetooth dropping. A stale flag self-corrects on the next HID
        // event; a wrongly cleared one leaves the card inactive until a power cycle.
        var enumerated = _usbHidDeviceService.GetDevices();
        if (enumerated.Count == 0) return;

        var (hasWired, hasDongle) = DeriveHidTransports(
            enumerated.Select(d => (d.ClientName, d.CatalogEntry?.ConnectionType)),
            card.Name);

        card.HasWire = hasWired;
        card.HasDongle = hasDongle;
    }

    /// <summary>Which HID transports the DeviceHost currently enumerates for one card.</summary>
    /// <remarks>
    /// Static and data-only so the rule is testable: HomeViewModel's constructor needs the whole
    /// device/session service graph, which is why the other rules in this file are shaped the same
    /// way. Applies the HID path's own precedence — the cable is the active data path whenever it is
    /// present, so a receiver alongside it does not claim the card.
    /// </remarks>
    internal static (bool HasWire, bool HasDongle) DeriveHidTransports(
        IEnumerable<(string? ClientName, string? ConnectionType)> devices, string? cardName)
    {
        if (string.IsNullOrEmpty(cardName)) return (false, false);

        var hasWired = false;
        var hasDongle = false;

        foreach (var (clientName, connectionType) in devices)
        {
            if (!string.Equals(clientName, cardName, StringComparison.OrdinalIgnoreCase))
                continue;

            switch (ParseConnectionType(connectionType))
            {
                case DeviceConnectionType.Wired:
                    hasWired = true;
                    break;
                case DeviceConnectionType.Dongle2_4G:
                    hasDongle = true;
                    break;
            }
        }

        return (hasWired, hasDongle && !hasWired);
    }

    // Stamps the identities a Bluetooth-only discovery cannot learn from the OS: the Bluetooth
    // stack reports no VID/PID (BluetoothDeviceResponse carries none), so the values come from the
    // catalog entry — without them the card cannot address device commands (SetDeviceLedColor went
    // out as VID=0/PID=0 and the DeviceHost dropped it). Follows the ApplyDeviceIdentity rules:
    // routing identity follows the live transport, a merged product's settings bucket is pinned to
    // the product-wide id with an empty key, and a non-merged card's SettingsDeviceKey stays
    // whatever it owns (Bluetooth has no Container ID to offer). An entry from an older DeviceHost
    // carries 0/0 and leaves the card exactly as before these fields existed. A card whose live
    // transport is wired / 2.4G keeps that identity: Windows can report the board's Bluetooth as
    // connected while the cable is carrying the traffic, and re-stamping would route writes at the
    // Bluetooth interface (which does not carry them) and repoint the settings bucket the details
    // page reads.
    internal static void ApplyBluetoothIdentity(DeviceCardViewModel card, DeviceCatalogEntry entry)
    {
        if (entry.VendorId <= 0 || entry.ProductId <= 0)
            return;

        if (card.HasWire || card.HasDongle)
            return;

        card.VendorId = entry.VendorId;
        card.ProductId = entry.ProductId;
        card.MergeTransports = entry.MergeTransports;
        // Same pin, same fallback as ApplyDeviceIdentity. It matters most here: COSMO's and
        // STARLIGHT's Bluetooth links both enumerate under vendor 1494 while the products are 3468,
        // so without the pin a headset's Bluetooth settings landed in a bucket its dongle never read.
        card.SettingsVendorId = entry.MergeTransports && entry.SettingsVendorId > 0
            ? entry.SettingsVendorId
            : entry.VendorId;
        card.SettingsProductId = entry.MergeTransports ? entry.SettingsProductId : entry.ProductId;
        if (entry.MergeTransports)
            card.SettingsDeviceKey = string.Empty;
    }

    // The catalog carries the per-device defaults as flat scalars because proto has no optional
    // primitives here: 0 (and "" for the strings) means "unset". Translate that back into nulls so
    // the details VM can tell "this device has no default for that aspect" from a real value.
    // RgbEnabled is a plain bool in the contract and so cannot express unset on its own — it is only
    // meaningful when the device actually declares a default effect.
    private static DeviceDefaults ToDeviceDefaults(DeviceCatalogEntry entry)
    {
        var effect = string.IsNullOrEmpty(entry.DefaultRgbEffect) ? null : entry.DefaultRgbEffect;
        return new DeviceDefaults(
            Dpi: entry.DefaultDpi > 0 ? entry.DefaultDpi : null,
            ReportRateHz: entry.DefaultReportRateHz > 0 ? entry.DefaultReportRateHz : null,
            ButtonDebounceMs: entry.DefaultButtonDebounceMs > 0 ? entry.DefaultButtonDebounceMs : null,
            LiftOffDistanceMm: entry.DefaultLiftOffDistanceMm > 0 ? entry.DefaultLiftOffDistanceMm : null,
            RgbEnabled: effect is null ? null : entry.DefaultRgbEnabled,
            RgbEffect: effect,
            RgbColorHex: string.IsNullOrEmpty(entry.DefaultRgbColorHex) ? null : entry.DefaultRgbColorHex,
            RgbBrightness: entry.DefaultRgbBrightness > 0 ? entry.DefaultRgbBrightness : null,
            RgbSpeed: entry.DefaultRgbSpeed > 0 ? entry.DefaultRgbSpeed : null);
    }

    /// <summary>
    /// The settings bucket a stored <c>devices</c> row addresses — what a card restored from disk
    /// must read and write BEFORE discovery reports in, since it has no catalog entry until then.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deriving this from the routing identity instead was a real defect, not a cosmetic one: a
    /// restored card is deliberately clickable before discovery (see the pre-discovery open in
    /// <see cref="RecordToViewModel"/>), and opening it WRITES — <c>SetDeviceAsync</c> persists the
    /// resolved RGB state and <c>LoadPresetsForDeviceAsync</c> seeds a default preset and its
    /// snapshot — so the rows landed under a pair no discovered card would ever address again (a
    /// dongle-last RAMPAGE wrote to <c>(1389, 49271, "")</c>).
    /// </para>
    /// <para>
    /// <c>0</c> / <c>""</c> is a row written before the settings columns existed. Those fall back to
    /// the old derivation, so a legacy row keeps exactly the meaning it had and a device that has
    /// data still finds it; <see cref="ApplyDeviceIdentity"/> corrects either case — including the
    /// merged-product pinning — as soon as the device reports in.
    /// </para>
    /// </remarks>
    internal static (int VendorId, int ProductId, string DeviceKey) SettingsIdentityFor(DeviceRecord record) => (
        record.SettingsVendorId > 0 ? record.SettingsVendorId : record.VendorId,
        record.SettingsProductId > 0 ? record.SettingsProductId : record.ProductId,
        record.SettingsDeviceKey ?? string.Empty);

    private ViewModelBase RecordToViewModel(DeviceRecord record)
    {
        DeviceCardViewModel vm;
        if (record.CardType == "double")
        {
            var dvm = _services.GetRequiredService<DoubleCardViewModel>();
            dvm.ConnectionIcon = record.ConnectionIcon;
            vm = dvm;
        }
        else
        {
            vm = _services.GetRequiredService<SingleCardViewModel>();
        }

        // Restored cards are the THIRD fill path, alongside FillConnectionCard and
        // FillBluetoothCard, and the only one that runs for a device that is switched off: nothing
        // rediscovers it, so a scale not restored here stays 1.0 for the life of the card and the
        // headset renders visibly smaller than its neighbours. Same expression as the other two.
        vm.ImageScale = record.ImageScale > 0 ? record.ImageScale : 1.0;
        vm.StorageId = record.Id;
        vm.VendorId = record.VendorId;
        vm.ProductId = record.ProductId;
        // The stored routing key comes back so the card keeps the identity it was persisted
        // under — re-adopting it later must hit the same upsert row, not mint a keyless duplicate.
        // Discovery re-stamps it when the device reports in.
        vm.DeviceKey = record.DeviceKey;
        // Restored BEFORE discovery reports in, because it is what decides which upsert key the
        // card is stored under: without it an offline merged-transport card would be re-persisted
        // under its routing identity and duplicate itself again.
        vm.MergeTransports = record.MergeTransports;
        // The bucket this card was PERSISTED under, not one derived from the routing identity
        // above — see SettingsIdentityFor for why that distinction is load-bearing.
        (vm.SettingsVendorId, vm.SettingsProductId, vm.SettingsDeviceKey) = SettingsIdentityFor(record);
        vm.Name = record.Name;
        vm.DeviceTypeLabel = record.DeviceTypeLabel;
        vm.ImagePath = record.ImagePath;
        vm.BatteryLevel = record.BatteryLevel;
        // Seed the announcement guard BEFORE WireDelete's initial check, or the restored card's
        // first live reading re-toasts a band already announced last session — one toast per app
        // launch with a sub-20 % mouse ("random moments", QA 2026-08-31).
        SeedLowBatteryBand(vm, record.LowBatteryAnnouncedBand);
        // A restored keyboard card is clickable straight from startup, before discovery reports
        // in — prewarm its split artwork by the stored name so a first click still opens ready.
        _ = RgbKeyboardArtCatalog.PrewarmAsync(record.Name);
        WireDelete(vm);
        return vm;
    }

    // Needs revisiting: unreachable today, since AddDeviceCardViewModel never raises DeviceAdded.
    // Kept on purpose until the manual add flow is either finished or removed
    // (docs/plans/fix-repository-audit-findings, D6).
    private void OnDeviceAdded(DeviceRecord record)
    {
        if (_session.CurrentSession != null)
        {
            record.Id = _session.CurrentSession.UpsertDevice(record);
            // Flushed now, not at exit: a manual add that only reached disk on a clean shutdown
            // was lost to any crash or kill in between.
            FlushSession();
        }
        else if (CurrentOwner is { Kind: LocalStorageService.OwnerKindUser } owner)
        {
            record.Id = _localStorage.AddDevice(owner.Kind, owner.Id, record);
        }

        var card = RecordToViewModel(record);

        var addCard = Devices.OfType<AddDeviceCardViewModel>().FirstOrDefault();
        if (addCard != null)
            Devices.Remove(addCard);

        Devices.Add(card);

        if (addCard != null)
            Devices.Add(addCard);

        AddNotification(LocalizationManager.GetString("Notification_NewDeviceConnected"), record.Name, DeviceConnectedIcon);
    }

    private void RegisterExistingCardsInSession()
    {
        if (_session.CurrentSession == null) return;
        foreach (var card in Devices.OfType<DeviceCardViewModel>().Where(c => c.StorageId == 0))
        {
            PersistDeviceCard(card);
        }
    }

    // Stores the card under whoever owns state right now: the guest session (rewritten whole via
    // Flush, as before) or the signed-in user (write-through row). No owner — welcome screen —
    // stores nothing.
    private void PersistDeviceCard(DeviceCardViewModel card)
    {
        var record = new DeviceRecord
        {
            // The row this card already owns, so AddDevice replaces it in place instead of
            // leaving an orphan when the routing identity below has moved since the last persist.
            Id = card.StorageId,
            Name = card.Name,
            DeviceTypeLabel = card.DeviceTypeLabel,
            ImagePath = card.ImagePath,
            ImageScale = card.ImageScale,
            BatteryLevel = card.BatteryLevel,
            VendorId = card.VendorId,
            ProductId = card.ProductId,
            DeviceKey = card.DeviceKey ?? string.Empty,
            MergeTransports = card.MergeTransports,
            // The bucket this card's settings actually live in, so a restore before discovery
            // reads and writes the same rows a discovered card would.
            SettingsVendorId = card.SettingsVendorId,
            SettingsProductId = card.SettingsProductId,
            SettingsDeviceKey = card.SettingsDeviceKey ?? string.Empty,
            // Read off the card, not the band map: this runs on the discovery thread while the
            // map is UI-thread-only; the card field is its torn-free mirror.
            LowBatteryAnnouncedBand = card.LowBatteryAnnouncedBand,
            ConnectionIcon = (card as DoubleCardViewModel)?.ConnectionIcon,
            CardType = card is DoubleCardViewModel ? "double" : "single"
        };

        if (_session.CurrentSession != null)
        {
            // Upsert, not append: a card that already owns a session record must replace it, or a
            // re-persist (an identity that moved since the card was adopted) leaves the guest flush
            // writing the same card twice. UserSession does it as one locked mutation, which also
            // keeps this path safe when discovery calls it off the UI thread.
            card.StorageId = _session.CurrentSession.UpsertDevice(record);
            FlushSession();
        }
        else if (CurrentOwner is { Kind: LocalStorageService.OwnerKindUser } owner)
        {
            // Needs revisiting: a synchronous SQLite write, and on the UI thread when called from
            // ReloadOwnerScopedState at sign-in (once per card). Left synchronous on purpose, because
            // the returned id becomes StorageId, which later deletes and battery-band writes read
            // inside the owner-scoped adopt/revive logic. Moving it to the storage executor needs a
            // design, not a swap (docs/plans/fix-repository-audit-findings, A4b).
            card.StorageId = _localStorage.AddDevice(owner.Kind, owner.Id, record);
        }
    }

    private void FlushSession()
    {
        // Snapshot at enqueue time, write on the executor: the queued flush records exactly the
        // device list as of this call (FIFO orders it after the upsert bookkeeping that preceded
        // it) and never enumerates a list another thread may still mutate. The snapshot is NOT
        // necessarily taken on the UI thread — discovery persists a card from the gRPC handler's
        // thread — which is why it goes through UserSession's gate rather than ToArray here.
        if (_session.CurrentSession is { } session)
        {
            var devices = session.SnapshotDevices();
            EnqueueStorageWrite(() => _localStorage.Flush(devices));
        }
    }

    private void WireDelete(DeviceCardViewModel vm)
    {
        vm.DeleteRequested += () => PendingDeleteCard = vm;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null &&
                Array.IndexOf(LowBatteryTriggerProperties, e.PropertyName) >= 0)
                CheckLowBatteryOnUIThread(vm);
        };
        CheckLowBatteryOnUIThread(vm);
    }

    // Every card property that gates the announcement re-runs the check when it flips —
    // unplugging the charger at 14% changes no level, a device that reconnects at the same
    // percentage raises no BatteryLevel change at all, and the live reading then lands as a
    // HasBattery flip. Internal so the test suite pins the composition: dropping an entry does
    // not fail any rule test, it just silently kills that trigger's acceptance criterion
    // (PR #117 m1).
    internal static readonly string[] LowBatteryTriggerProperties =
    [
        nameof(DeviceCardViewModel.BatteryLevel),
        nameof(DeviceCardViewModel.IsCharging),
        nameof(DeviceCardViewModel.Connected),
        nameof(DeviceCardViewModel.HasBattery),
    ];

    // The Bluetooth found path flips the transport flags (and WireDelete's initial call runs) on
    // the gRPC handler thread, while the 60 s poll runs on the UI thread — so without this
    // marshal the two would write _lowBatteryAnnouncedBand concurrently. Same invariant
    // _batteryMissStreak documents: every writer of the map runs on the UI thread (review R1 S2).
    private void CheckLowBatteryOnUIThread(DeviceCardViewModel vm)
    {
        if (Dispatcher.UIThread.CheckAccess())
            CheckLowBattery(vm);
        else
            Dispatcher.UIThread.Post(() => CheckLowBattery(vm));
    }

    // ── Low battery announcements ─────────────────────────────────────────────
    // Two bands, one toast each per discharge: the first time the level reaches 20 %, and the
    // first time it reaches 10 % (QA 2026-08-20 — anything more fired "at random moments").
    internal const int LowBatteryBand = 20;
    internal const int CriticalBatteryBand = 10;

    // Re-arming needs the level back ABOVE the top band by a margin — a genuine recharge — so a
    // reading jittering 19↔21 cannot re-announce on every wobble.
    internal const int LowBatteryRearmLevel = LowBatteryBand + 5;

    /// <summary>
    /// The announcement decision for one battery reading. <c>Announce</c> is the band to announce
    /// now (<see cref="LowBatteryBand"/> or <see cref="CriticalBatteryBand"/>) or null;
    /// <c>Rearm</c> asks the caller to forget the announced state. <paramref name="announcedBand"/>
    /// is the lowest band already announced this discharge (0 = none).
    /// </summary>
    /// <remarks>
    /// A null reading decides nothing: it is a missed poll or a sleeping device, not a recharge,
    /// so the announced state must survive it. Dropping straight past both bands announces only
    /// the lower one — the user gets one toast, not a backlog. No announcement while charging
    /// (the battery is not draining), for a disconnected card, or without a live reading
    /// (<paramref name="hasLiveReading"/> — a restored card carries a stored snapshot that can be
    /// hours stale, and on the Bluetooth found path the transports flip BEFORE the fresh value
    /// lands, so "connected" alone would toast the snapshot; review R1 S1). The state is kept in
    /// all three cases, and the IsCharging / Connected / HasBattery subscriptions re-run the
    /// decision when they change. Static, like <see cref="DeriveHidTransports"/>: the
    /// constructor needs the full service graph, so tests drive the rule directly.
    /// </remarks>
    internal static (bool Rearm, int? Announce) LowBatteryDecision(
        int? level, bool connected, bool isCharging, bool hasLiveReading, int announcedBand)
    {
        if (level is not { } lvl) return (false, null);
        // A high reading re-arms only when it is trustworthy: a stored snapshot (no live reading)
        // and a charge-time value (TAIPAN's cable readings derive from charge voltage, not charge
        // level) both HOLD the state instead — re-arming on them let the next live sub-band
        // reading toast again ("random moments", QA 2026-08-31). The genuine re-arm lands with
        // the first live, off-charger reading after the recharge.
        if (lvl >= LowBatteryRearmLevel) return (hasLiveReading && !isCharging, null);
        // 21–24 is the hysteresis zone: above the band, but not provably recharged — hold state.
        if (lvl > LowBatteryBand || !connected || isCharging || !hasLiveReading)
            return (false, null);

        var band = lvl <= CriticalBatteryBand ? CriticalBatteryBand : LowBatteryBand;
        return (false, announcedBand != 0 && announcedBand <= band ? null : band);
    }

    private void CheckLowBattery(DeviceCardViewModel vm)
    {
        var identity = LowBatteryIdentity(vm);
        _lowBatteryAnnouncedBand.TryGetValue(identity, out var announced);

        // HasBattery is "a live reading produced this level": RecordToViewModel restores the
        // stored level without setting it, and every read/push path sets it alongside the value.
        var (rearm, band) = LowBatteryDecision(
            vm.BatteryLevel, vm.Connected, vm.IsCharging, vm.HasBattery, announced);
        if (rearm)
        {
            if (_lowBatteryAnnouncedBand.Remove(identity))
            {
                vm.LowBatteryAnnouncedBand = 0;
                PersistLowBatteryBand(vm);
            }
            return;
        }
        if (band is null) return;

        _lowBatteryAnnouncedBand[identity] = band.Value;
        vm.LowBatteryAnnouncedBand = band.Value;
        PersistLowBatteryBand(vm);
        var pct = vm.BatteryLevel!.Value;
        Dispatcher.UIThread.Post(() => AddNotification(LocalizationManager.GetString("Notification_LowBattery"), $"{vm.DisplayName} ({pct}%)", LowBatteryIcon));
        _systemNotifications.Show(LocalizationManager.GetString("Notification_LowBattery"), $"{vm.DisplayName} ({pct}%)");
    }

    // The persisted half of the announcement guard: without it the map dies with the process and
    // every app launch with a sub-20 % mouse toasted again. Runs on the UI thread (CheckLowBattery's
    // affinity), so the signed-in write goes through the storage executor rather than running
    // AddDevice-style synchronous SQLite on the dispatcher; the guest path only mutates the
    // in-memory session record and lets the (queued) flush write it. A card with no storage row
    // yet loses nothing: PersistDeviceCard reads the band off the card when it eventually runs.
    private void PersistLowBatteryBand(DeviceCardViewModel vm)
    {
        if (vm.StorageId <= 0) return;

        if (_session.CurrentSession is { } session)
        {
            session.SetDeviceLowBatteryBand(vm.StorageId, vm.LowBatteryAnnouncedBand);
            FlushSession();
        }
        else if (CurrentOwner is { Kind: LocalStorageService.OwnerKindUser } owner)
        {
            var (id, bandToStore) = (vm.StorageId, vm.LowBatteryAnnouncedBand);
            EnqueueStorageWrite(() => _localStorage.SetDeviceLowBatteryAnnouncedBand(owner.Kind, owner.Id, id, bandToStore));
        }
    }

    private void ExecuteConfirmDelete()
    {
        if (_pendingDeleteCard == null) return;
        var vm = _pendingDeleteCard;
        PendingDeleteCard = null;
        Devices.Remove(vm);
        _lowBatteryAnnouncedBand.Remove(LowBatteryIdentity(vm));
        // Without this the streak entry outlives the card: a re-added device would inherit the
        // old count and could clear its indicator one miss early.
        _batteryMissStreak.Remove((vm.VendorId, vm.ProductId, vm.DeviceKey ?? string.Empty));
        if (vm.StorageId > 0)
        {
            if (_session.CurrentSession != null)
            {
                _session.CurrentSession.RemoveDevice(vm.StorageId);
                FlushSession();
            }
            else if (CurrentOwner is { Kind: LocalStorageService.OwnerKindUser } owner)
            {
                // A UI command: the delete runs on the storage executor, not inline
                // (docs/plans/fix-repository-audit-findings, A4). Values captured now, since the
                // owner can change before the queued write runs.
                var (kind, ownerId, storageId) = (owner.Kind, owner.Id, vm.StorageId);
                EnqueueStorageWrite(() => _localStorage.DeleteDevice(kind, ownerId, storageId));
            }
        }
    }

    private void UsbHidDeviceService_NotifyError(object? sender, string errorMessage)
    {
        _logger.LogWarning("UsbHidDeviceService notified error: {Message}", errorMessage);
    }

    // The DeviceHost stopped or started answering.
    //
    // Device presence reaches the App only as a push, so when the service goes down the LAST push
    // stands unchallenged and every card keeps showing as connected indefinitely — there is no
    // timer anywhere that would contradict it. Nothing is maintaining that state any more, so the
    // cards must stop claiming it.
    //
    // Nothing is shown to the user about the host itself, and the per-device "disconnected"
    // notifications are deliberately NOT raised here: one toast per card the moment a service
    // stops is exactly the announcement we have chosen not to make. The cards simply go inactive,
    // the same as any unplugged device.
    //
    // The recovery direction needs no work: the host answers a fresh subscribe with the current USB
    // and Bluetooth device lists, so the normal DeviceFound path lights the cards back up.
    private void OnHostAvailabilityChanged(object? sender, bool available)
    {
        if (available) return;

        _logger.LogWarning("DeviceHost stopped answering — marking every connected card inactive");
        _ = Dispatcher.UIThread.InvokeAsync(() => DeactivateCards(Devices.OfType<DeviceCardViewModel>()));
    }

    /// <summary>Marks every currently-connected card inactive, as when its device is unplugged.</summary>
    /// <remarks>
    /// Static and collection-only so the rule is testable without HomeViewModel's whole
    /// device/session service graph, like the other rules in this file. Filtered on Connected so an
    /// already-inactive card raises no redundant PropertyChanged, and materialised before the walk
    /// because MakeInactive drives change notifications out to bound views mid-enumeration.
    /// </remarks>
    internal static void DeactivateCards(IEnumerable<DeviceCardViewModel> cards)
    {
        foreach (var card in cards.Where(c => c.Connected).ToList())
            card.MakeInactive();
    }

    // String → CLR Type for DI lookup. The catalog carries "single"/"double" as
    // strings (server doesn't know about App view-model types); we resolve here.
    private static Type ParseCardSize(string? cardType) =>
        string.Equals(cardType, "double", StringComparison.OrdinalIgnoreCase)
            ? typeof(DoubleCardViewModel)
            : typeof(SingleCardViewModel);

    private static DeviceConnectionType ParseConnectionType(string? value) =>
        value switch
        {
            "Dongle2_4G" => DeviceConnectionType.Dongle2_4G,
            "Bluetooth" => DeviceConnectionType.Bluetooth,
            _ => DeviceConnectionType.Wired,
        };
}
