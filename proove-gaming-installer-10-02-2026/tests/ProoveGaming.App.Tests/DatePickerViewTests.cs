using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using ProoveGaming.Common.Controls;
using ProoveGaming.Common.Localization;
using Xunit;

namespace ProoveGaming.App.Tests;

// Guards the pure grid arithmetic behind the calendar — we don't instantiate the UserControl
// (that would need Avalonia bootstrap); just exercise the static layout helpers.
public class DatePickerViewTests
{
    [Fact]
    public void Grid_HasSixDateRows()
    {
        // A 5-row (35-cell) grid cannot fit a month that spans 6 calendar weeks.
        Assert.Equal(42, DatePickerView.GridCellCount);
    }

    [Fact]
    public void GridStartDate_IsSundayOnOrBeforeTheFirst()
    {
        for (var year = 1900; year <= 2100; year++)
        {
            for (var month = 1; month <= 12; month++)
            {
                var start = DatePickerView.GridStartDate(year, month);
                var first = new DateOnly(year, month, 1);
                Assert.Equal(DayOfWeek.Sunday, start.DayOfWeek);
                Assert.True(start <= first);
                Assert.True(first.DayNumber - start.DayNumber < 7);
            }
        }
    }

    [Fact]
    public void EveryDayOfEveryMonth_LandsInTheGrid()
    {
        // Regression guard: with 5 rows, months starting on a Friday/Saturday lost their last
        // days entirely, making dates like 31 October 2005 impossible to pick.
        for (var year = 1900; year <= 2100; year++)
        {
            for (var month = 1; month <= 12; month++)
            {
                var start = DatePickerView.GridStartDate(year, month);
                var daysInMonth = DateTime.DaysInMonth(year, month);
                var lastOfMonth = new DateOnly(year, month, daysInMonth);
                var lastCellIndex = lastOfMonth.DayNumber - start.DayNumber;
                Assert.InRange(lastCellIndex, 0, DatePickerView.GridCellCount - 1);
            }
        }
    }

    [Theory]
    [InlineData(2005, 10, 31)] // Saturday 1st + 31 days = 37 cells — the reported failure case
    [InlineData(2024, 3, 31)]
    [InlineData(2024, 6, 30)]
    [InlineData(2026, 5, 31)]
    public void SixWeekMonths_LastDayIsReachable(int year, int month, int day)
    {
        var start = DatePickerView.GridStartDate(year, month);
        var index = new DateOnly(year, month, day).DayNumber - start.DayNumber;
        Assert.True(index >= 35, "expected a month that overflows a 5-row grid");
        Assert.InRange(index, 0, DatePickerView.GridCellCount - 1);
    }

    [Theory]
    [InlineData(2026, 2016)]
    [InlineData(2016, 2016)]
    [InlineData(2015, 2004)]
    [InlineData(1990, 1980)]
    public void YearPageStart_SnapsToTwelveYearPages(int year, int expectedStart)
    {
        Assert.Equal(expectedStart, DatePickerView.YearPageStart(year));
    }

    [Fact]
    public void YearPageStart_PageAlwaysContainsTheYear()
    {
        for (var year = 1900; year <= 2100; year++)
        {
            var start = DatePickerView.YearPageStart(year);
            Assert.InRange(year, start, start + 11);
        }
    }

    [Theory]
    [InlineData(2001, 3, 9, "09/03/2001")]
    [InlineData(1999, 12, 31, "31/12/1999")]
    [InlineData(2026, 1, 1, "01/01/2026")]
    public void DefaultDisplayFormat_IsDayMonthFourDigitYear(int year, int month, int day, string expected)
    {
        // The field used to render MM/dd/yy, which reads as a different date entirely for any
        // day <= 12 — 09/03/2001 and 03/09/01 are both plausible to a reader.
        var date = new DateOnly(year, month, day);

        Assert.Equal(expected, date.ToString(DatePickerView.DefaultDisplayFormat, CultureInfo.InvariantCulture));
    }
}

// The popup's weekday and month labels are resolved from the application resource dictionaries at
// render time. These guard the key tables and the dictionaries that have to define them — a key
// missing from Strings.uk.axaml would silently fall back to English at runtime.
public class DatePickerResourceKeyTests
{
    public static IEnumerable<object[]> KeyTables() =>
    [
        [nameof(DatePickerResourceKeys.DayShort), DatePickerResourceKeys.DayShort, 7],
        [nameof(DatePickerResourceKeys.MonthShort), DatePickerResourceKeys.MonthShort, 12],
        [nameof(DatePickerResourceKeys.MonthFull), DatePickerResourceKeys.MonthFull, 12],
    ];

    [Theory]
    [MemberData(nameof(KeyTables))]
    public void KeyTable_HasExpectedLengthAndNoBlankEntries(string name, string[] keys, int expectedLength)
    {
        Assert.Equal(expectedLength, keys.Length);
        Assert.All(keys, key => Assert.False(string.IsNullOrWhiteSpace(key), $"{name} has a blank key"));
    }

    [Fact]
    public void KeyTables_AreCollectivelyDistinct()
    {
        var all = DatePickerResourceKeys.DayShort
            .Concat(DatePickerResourceKeys.MonthShort)
            .Concat(DatePickerResourceKeys.MonthFull)
            .ToArray();

        Assert.Equal(all.Length, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("en")]
    [InlineData("uk")]
    public void EveryKey_IsDefinedInTheResourceDictionary(string languageCode)
    {
        var defined = ReadResourceKeys(languageCode);

        var missing = DatePickerResourceKeys.DayShort
            .Concat(DatePickerResourceKeys.MonthShort)
            .Concat(DatePickerResourceKeys.MonthFull)
            .Where(key => !defined.Contains(key))
            .ToArray();

        Assert.True(missing.Length == 0, $"Strings.{languageCode}.axaml is missing: {string.Join(", ", missing)}");
    }

    private static HashSet<string> ReadResourceKeys(string languageCode)
    {
        var relativePath = Path.Combine("src", "ProoveGaming.App", "Resources", $"Strings.{languageCode}.axaml");

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, relativePath)))
            directory = directory.Parent;

        Assert.True(directory != null, $"could not locate {relativePath} above {AppContext.BaseDirectory}");

        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(Path.Combine(directory!.FullName, relativePath));

        return document.Root!
            .Elements()
            .Select(element => element.Attribute(x + "Key")?.Value)
            .Where(key => key != null)
            .ToHashSet(StringComparer.Ordinal)!;
    }
}
