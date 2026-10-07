using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace ProoveGaming.App.ViewModels;

public enum NotificationDateHeader { Today, Yesterday, OlderDate }

/// <summary>
/// One calendar day of notifications in the panel: the divider header plus that day's items,
/// newest first. The mutation helpers are static so HomeViewModel and the unit tests drive the
/// exact same logic — HomeViewModel's constructor needs the full service graph, so tests use the
/// helpers with a stub header formatter instead.
/// </summary>
public sealed class NotificationGroupViewModel : ViewModelBase
{
    private string _header = string.Empty;

    public DateTime Date { get; }

    public string Header
    {
        get => _header;
        set => SetProperty(ref _header, value);
    }

    public ObservableCollection<NotificationItemViewModel> Items { get; } = new();

    public NotificationGroupViewModel(DateTime date) => Date = date.Date;

    public static NotificationDateHeader GetHeaderKind(DateTime date, DateTime today)
    {
        if (date.Date == today.Date)
            return NotificationDateHeader.Today;
        if (date.Date == today.Date.AddDays(-1))
            return NotificationDateHeader.Yesterday;
        return NotificationDateHeader.OlderDate;
    }

    /// <summary>
    /// Inserts the item newest-first into the group matching its date, creating the group when
    /// needed. Creating a group re-labels every header via <paramref name="headerFor"/>: a date
    /// rollover while the app runs demotes the group that used to be "Today" to "Yesterday".
    /// </summary>
    public static void Insert(
        ObservableCollection<NotificationGroupViewModel> groups,
        NotificationItemViewModel item,
        Func<DateTime, string> headerFor)
    {
        var date = item.Timestamp.Date;
        var group = groups.FirstOrDefault(g => g.Date == date);
        if (group == null)
        {
            group = new NotificationGroupViewModel(date);
            var index = 0;
            while (index < groups.Count && groups[index].Date > date)
                index++;
            groups.Insert(index, group);
            RelabelAll(groups, headerFor);
        }

        group.Items.Insert(0, item);
    }

    /// <summary>
    /// Recomputes every group header against the current date. Called when a new group appears
    /// and every time the panel opens — a "Today" group must read "Yesterday" once midnight has
    /// passed, even if no new notification has arrived since.
    /// </summary>
    public static void RelabelAll(
        ObservableCollection<NotificationGroupViewModel> groups,
        Func<DateTime, string> headerFor)
    {
        foreach (var group in groups)
            group.Header = headerFor(group.Date);
    }

    /// <summary>Removes the item from its group, dropping the group once it holds nothing.</summary>
    public static void Remove(
        ObservableCollection<NotificationGroupViewModel> groups,
        NotificationItemViewModel item)
    {
        foreach (var group in groups)
        {
            if (!group.Items.Remove(item))
                continue;

            if (group.Items.Count == 0)
                groups.Remove(group);
            return;
        }
    }
}
