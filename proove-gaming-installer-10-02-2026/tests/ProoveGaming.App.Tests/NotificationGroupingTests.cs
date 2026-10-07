using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ProoveGaming.App.ViewModels;
using Xunit;

namespace ProoveGaming.App.Tests;

// The notifications panel groups items by calendar date (Today / Yesterday / older dates),
// newest date first, newest item first within a date. The grouping lives in static helpers on
// NotificationGroupViewModel rather than in HomeViewModel because HomeViewModel's constructor
// needs the full device/session service graph — these tests drive the helpers directly with a
// stub header formatter, exactly as HomeViewModel does with its localized one.
public class NotificationGroupingTests
{
	private static readonly DateTime Today = new(2026, 8, 10, 15, 30, 0);

	private static NotificationItemViewModel Item(DateTime timestamp) =>
		new() { Timestamp = timestamp, Title = "title", Subtitle = "subtitle" };

	private static string HeaderFor(DateTime date) =>
		NotificationGroupViewModel.GetHeaderKind(date, Today.Date) switch
		{
			NotificationDateHeader.Today => "TODAY",
			NotificationDateHeader.Yesterday => "YESTERDAY",
			_ => date.ToString("dd.MM.yyyy"),
		};

	[Fact]
	public void HeaderKind_TodayYesterdayAndOlder()
	{
		Assert.Equal(NotificationDateHeader.Today, NotificationGroupViewModel.GetHeaderKind(Today, Today.Date));
		Assert.Equal(NotificationDateHeader.Yesterday, NotificationGroupViewModel.GetHeaderKind(Today.AddDays(-1), Today.Date));
		Assert.Equal(NotificationDateHeader.OlderDate, NotificationGroupViewModel.GetHeaderKind(Today.AddDays(-2), Today.Date));
	}

	[Fact]
	public void HeaderKind_MidnightBoundary_SplitsDates()
	{
		var justBeforeMidnight = new DateTime(2026, 8, 9, 23, 59, 0);
		var justAfterMidnight = new DateTime(2026, 8, 10, 0, 0, 0);

		Assert.Equal(NotificationDateHeader.Yesterday, NotificationGroupViewModel.GetHeaderKind(justBeforeMidnight, Today.Date));
		Assert.Equal(NotificationDateHeader.Today, NotificationGroupViewModel.GetHeaderKind(justAfterMidnight, Today.Date));
	}

	[Fact]
	public void Insert_SameDate_SharesOneGroup_NewestItemFirst()
	{
		var groups = new ObservableCollection<NotificationGroupViewModel>();
		var earlier = Item(Today.AddHours(-2));
		var later = Item(Today);

		NotificationGroupViewModel.Insert(groups, earlier, HeaderFor);
		NotificationGroupViewModel.Insert(groups, later, HeaderFor);

		var group = Assert.Single(groups);
		Assert.Equal("TODAY", group.Header);
		Assert.Equal(new[] { later, earlier }, group.Items.ToArray());
	}

	[Fact]
	public void Insert_OutOfOrderDates_YieldsNewestDateFirst()
	{
		var groups = new ObservableCollection<NotificationGroupViewModel>();
		NotificationGroupViewModel.Insert(groups, Item(Today.AddDays(-2)), HeaderFor);
		NotificationGroupViewModel.Insert(groups, Item(Today), HeaderFor);
		NotificationGroupViewModel.Insert(groups, Item(Today.AddDays(-1)), HeaderFor);

		Assert.Equal(
			new[] { Today.Date, Today.Date.AddDays(-1), Today.Date.AddDays(-2) },
			groups.Select(g => g.Date).ToArray());
		Assert.Equal(new[] { "TODAY", "YESTERDAY", "08.08.2026" }, groups.Select(g => g.Header).ToArray());
	}

	// RelabelAll is what the panel-open path runs: after midnight, a group created as "Today"
	// must re-read against the new current date even when no new notification has arrived.
	[Fact]
	public void RelabelAll_RecomputesHeadersAgainstCurrentDate()
	{
		var groups = new ObservableCollection<NotificationGroupViewModel>();
		NotificationGroupViewModel.Insert(groups, Item(Today.AddDays(-1)), d => "TODAY");

		Assert.Equal("TODAY", groups[0].Header);

		NotificationGroupViewModel.RelabelAll(groups, HeaderFor);

		Assert.Equal("YESTERDAY", groups[0].Header);
	}

	// The other on-demand relabel path: a live language switch. HomeViewModel's LanguageChanged
	// handler re-runs RelabelAll with the formatter now reading the new language's resources, so
	// headers baked in at insertion time ("Today"/"Yesterday" are NOT DynamicResource) must all be
	// rewritten in place — same dates, same items, no new notification required.
	[Fact]
	public void RelabelAll_LanguageSwitch_RewritesEveryHeaderWithTheNewFormatter()
	{
		var groups = new ObservableCollection<NotificationGroupViewModel>();
		NotificationGroupViewModel.Insert(groups, Item(Today), HeaderFor);
		NotificationGroupViewModel.Insert(groups, Item(Today.AddDays(-1)), HeaderFor);
		NotificationGroupViewModel.Insert(groups, Item(Today.AddDays(-2)), HeaderFor);
		Assert.Equal(new[] { "TODAY", "YESTERDAY", "08.08.2026" }, groups.Select(g => g.Header).ToArray());

		string UkrainianHeaderFor(DateTime date) =>
			NotificationGroupViewModel.GetHeaderKind(date, Today.Date) switch
			{
				NotificationDateHeader.Today => "СЬОГОДНІ",
				NotificationDateHeader.Yesterday => "ВЧОРА",
				_ => date.ToString("dd.MM.yyyy"),
			};

		NotificationGroupViewModel.RelabelAll(groups, UkrainianHeaderFor);

		Assert.Equal(new[] { "СЬОГОДНІ", "ВЧОРА", "08.08.2026" }, groups.Select(g => g.Header).ToArray());
		Assert.Equal(
			new[] { Today.Date, Today.Date.AddDays(-1), Today.Date.AddDays(-2) },
			groups.Select(g => g.Date).ToArray());
	}

	// A relabel updates a live, already-bound panel — the header must go out through
	// INotifyPropertyChanged, or the open panel keeps showing the old language until reopened.
	[Fact]
	public void RelabelAll_RaisesHeaderChangeNotifications()
	{
		var groups = new ObservableCollection<NotificationGroupViewModel>();
		NotificationGroupViewModel.Insert(groups, Item(Today), HeaderFor);
		var raised = new List<string?>();
		groups[0].PropertyChanged += (_, e) => raised.Add(e.PropertyName);

		NotificationGroupViewModel.RelabelAll(groups, d => "СЬОГОДНІ");

		Assert.Contains(nameof(NotificationGroupViewModel.Header), raised);
	}

	// When a date rollover creates a new group, every existing header is recomputed — the group
	// that was "Today" when its items arrived must read "Yesterday" once a newer day exists.
	[Fact]
	public void Insert_NewDay_RelabelsExistingGroups()
	{
		var groups = new ObservableCollection<NotificationGroupViewModel>();
		var yesterday = Today.AddDays(-1);
		NotificationGroupViewModel.Insert(groups, Item(yesterday), HeaderFor);

		// Simulates the app running across midnight: the first insert happened when
		// `yesterday` was still "today", so its header said TODAY at the time.
		groups[0].Header = "TODAY";

		NotificationGroupViewModel.Insert(groups, Item(Today), HeaderFor);

		Assert.Equal(new[] { "TODAY", "YESTERDAY" }, groups.Select(g => g.Header).ToArray());
	}

	[Fact]
	public void Remove_LastItemOfDate_DropsTheGroup()
	{
		var groups = new ObservableCollection<NotificationGroupViewModel>();
		var todayItem = Item(Today);
		var yesterdayItem = Item(Today.AddDays(-1));
		NotificationGroupViewModel.Insert(groups, todayItem, HeaderFor);
		NotificationGroupViewModel.Insert(groups, yesterdayItem, HeaderFor);

		NotificationGroupViewModel.Remove(groups, yesterdayItem);

		var group = Assert.Single(groups);
		Assert.Equal(Today.Date, group.Date);
		Assert.Equal(new[] { todayItem }, group.Items.ToArray());
	}

	[Fact]
	public void Remove_ItemNotLast_KeepsTheGroup()
	{
		var groups = new ObservableCollection<NotificationGroupViewModel>();
		var first = Item(Today.AddHours(-1));
		var second = Item(Today);
		NotificationGroupViewModel.Insert(groups, first, HeaderFor);
		NotificationGroupViewModel.Insert(groups, second, HeaderFor);

		NotificationGroupViewModel.Remove(groups, second);

		var group = Assert.Single(groups);
		Assert.Equal(new[] { first }, group.Items.ToArray());
	}
}
