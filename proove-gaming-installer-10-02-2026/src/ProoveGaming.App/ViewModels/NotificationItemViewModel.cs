using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;

namespace ProoveGaming.App.ViewModels;

public partial class NotificationItemViewModel : ViewModelBase
{
    [ObservableProperty]
    public partial bool IsRead { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Subtitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Time { get; set; } = string.Empty;

    // Full creation moment backing the date grouping; Time above stays the displayed "HH:mm".
    public DateTime Timestamp { get; set; }

    // Row id in the notifications table, 0 while unpersisted (no owner at creation time, or
    // the queued insert has not landed yet).
    public int StorageId { get; set; }

    // Set by the delete paths the moment the item leaves the bell. Plain flag, no
    // PropertyChanged: the persist continuation reads it to decide between assigning
    // StorageId and deleting the just-created row (deleted-before-persisted race).
    public bool IsDeleted { get; set; }

    // NotificationTypes slug carried for persistence (the pre-owner flush rebuilds a record
    // from this item) and the server push; not bound — the icon fields already render it.
    public string Type { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIconData))]
    public partial string IconData { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIconUri))]
    public partial string IconUri { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double IconWidth { get; set; } = 22;

    [ObservableProperty]
    public partial double IconHeight { get; set; } = 12;

    public bool HasIconData => !string.IsNullOrEmpty(IconData) && string.IsNullOrEmpty(IconUri);
    public bool HasIconUri => !string.IsNullOrEmpty(IconUri);

    public event Action<NotificationItemViewModel>? DeleteRequested;

    public IRelayCommand DeleteCommand { get; }
    public IRelayCommand MarkAsReadCommand { get; }
    public IRelayCommand MarkAsUnreadCommand { get; }

    public NotificationItemViewModel()
    {
        DeleteCommand = new RelayCommand(() => DeleteRequested?.Invoke(this));
        MarkAsReadCommand = new RelayCommand(() => IsRead = true);
        MarkAsUnreadCommand = new RelayCommand(() => IsRead = false);
    }
}
