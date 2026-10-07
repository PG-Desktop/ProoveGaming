using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;
using ProoveGaming.Common.Localization;

namespace ProoveGaming.Common.Controls;

public partial class DatePickerView : UserControl
{
	/// <summary>Format used to render the selected date in the header field. Fixed DD/MM/YYYY —
	/// the field is a plain date, not a culture-formatted one.</summary>
	internal const string DefaultDisplayFormat = "dd'/'MM'/'yyyy";

	/// <summary>Placeholder text shown in the header when no date is selected yet.</summary>
	public static readonly StyledProperty<string?> PlaceholderProperty =
		AvaloniaProperty.Register<DatePickerView, string?>(nameof(Placeholder));

	public event Action<DateOnly>? DateSelected;

	// What the grid below the header is currently selecting.
	private enum PickerMode { Days, Months, Years }

	private DateOnly? _committedDate;
	private DateOnly? _pendingDate;
	private int _viewYear;
	private int _viewMonth;
	private PickerMode _mode = PickerMode.Days;
	private int _yearPageStart;
	private string _displayFormat = DefaultDisplayFormat;
	private readonly List<Button> _dayCells = new();
	private readonly List<Button> _periodCells = new();
	private readonly List<TextBlock> _dayOfWeekLabels = new();
	private readonly Popup _popup;
	private readonly Border _container;
	private readonly Grid _calendarBlock;
	private readonly Grid _periodBlock;
	private readonly TextBlock _headerLabel;
	private readonly TextBlock _selectedText;
	private static readonly IBrush _selectedBorderBrush = new ImmutableSolidColorBrush(Color.Parse("#6357FF"));
	private static readonly IBrush _placeholderBrush = new ImmutableSolidColorBrush(Color.Parse("#B2BEBCEA"));
	// One theme instance shared by every cell — ControlTheme is immutable once built.
	private static readonly ControlTheme _flatButtonTheme = BuildFlatButtonTheme();
	// Used when a resource key is missing (incomplete translation, headless test): keeps the
	// pre-localization behaviour of rendering invariant English rather than a raw key.
	private static readonly string[] _dayOfWeekFallbacks = { "SU", "MO", "TU", "WE", "TH", "FR", "SA" };
	private static readonly string[] _monthShortFallbacks = UpperCased(CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedMonthNames);
	private static readonly string[] _monthFullFallbacks = UpperCased(CultureInfo.InvariantCulture.DateTimeFormat.MonthNames);

	// 6 date rows × 7 columns = 42 slots. 6 rows are mandatory: a month whose 1st falls late
	// in the week spans 6 calendar weeks (e.g. October 2005), and a 5-row grid silently drops
	// its trailing days — which for a date-of-birth field means unreachable dates.
	private const int DateRows = 6;
	private const int DateCells = DateRows * 7;
	// Month / year grid is 3 columns × 4 rows.
	private const int PeriodCells = 12;

	static DatePickerView()
	{
		PlaceholderProperty.Changed.AddClassHandler<DatePickerView>((x, _) =>
		{
			if (!x._committedDate.HasValue) x.UpdateHeader();
		});
	}

	public DatePickerView()
	{
		InitializeComponent();

		var headerButton = this.FindControl<Button>("DateHeaderButton")!;
		headerButton.Theme = _flatButtonTheme;
		_popup = this.FindControl<Popup>("DatePickerPopup")!;
		_calendarBlock = this.FindControl<Grid>("CalendarBlock")!;
		_periodBlock = this.FindControl<Grid>("PeriodBlock")!;
		_headerLabel = this.FindControl<TextBlock>("HeaderLabel")!;
		_selectedText = this.FindControl<TextBlock>("DateSelectedText")!;
		_container = this.FindControl<Border>("DateContainer")!;
		var arrowDown = this.FindControl<Control>("DateArrowDownIcon")!;
		var arrowUp = this.FindControl<Control>("DateArrowUpIcon")!;
		var modeBtn = this.FindControl<Button>("ModeToggleButton")!;
		var prevBtn = this.FindControl<Button>("PrevMonthButton")!;
		var nextBtn = this.FindControl<Button>("NextMonthButton")!;

		BuildDayOfWeekHeader();
		BuildDayCells();
		BuildPeriodCells();

		var today = DateOnly.FromDateTime(DateTime.Today);
		SetViewMonth(today.Year, today.Month);
		UpdatePopupHeaderLabel();
		UpdateHeader();

		headerButton.Click += (_, _) =>
		{
			if (_popup.IsOpen)
			{
				_popup.IsOpen = false;
			}
			else
			{
				_pendingDate = _committedDate;
				var seed = _committedDate ?? DateOnly.FromDateTime(DateTime.Today);
				_mode = PickerMode.Days;
				SetViewMonth(seed.Year, seed.Month);
				RenderCurrentMode();
				_popup.IsOpen = true;
			}
		};

		_popup.Opened += (_, _) =>
		{
			_container.Classes.Add("open");
			arrowDown.IsVisible = false;
			arrowUp.IsVisible = true;
		};
		_popup.Closed += (_, _) =>
		{
			_container.Classes.Remove("open");
			arrowDown.IsVisible = true;
			arrowUp.IsVisible = false;
		};

		// Days → Months → Years → Days. Without this the only way to reach a distant year
		// would be one month-step at a time.
		modeBtn.Click += (_, _) =>
		{
			_mode = _mode switch
			{
				PickerMode.Days => PickerMode.Months,
				PickerMode.Months => PickerMode.Years,
				_ => PickerMode.Days
			};
			RenderCurrentMode();
		};

		prevBtn.Click += (_, _) => StepPage(-1);
		nextBtn.Click += (_, _) => StepPage(1);
	}

	/// <summary>Placeholder text shown in the header when no date is selected yet.</summary>
	public string? Placeholder
	{
		get => GetValue(PlaceholderProperty);
		set => SetValue(PlaceholderProperty, value);
	}

	/// <summary>Set the .NET format string used to render the selected date in the header
	/// (defaults to <see cref="DefaultDisplayFormat"/>).</summary>
	public void SetDisplayFormat(string format)
	{
		_displayFormat = format;
		UpdateHeader();
	}

	public void SetSelectedDate(DateOnly? date)
	{
		_committedDate = date;
		if (date.HasValue)
		{
			SetViewMonth(date.Value.Year, date.Value.Month);
			UpdatePopupHeaderLabel();
		}
		UpdateHeader();
	}

	// Keeps the year page in sync with the visible month so switching modes lands on a page
	// that actually contains the current view.
	private void SetViewMonth(int year, int month)
	{
		_viewYear = year;
		_viewMonth = month;
		_yearPageStart = YearPageStart(year);
	}

	/// <summary>First year of the 12-year page that contains <paramref name="year"/>.</summary>
	internal static int YearPageStart(int year) => year - Modulo(year, PeriodCells);

	/// <summary>Date rendered in the grid's first cell for a given view month — the Sunday on
	/// or before the 1st.</summary>
	internal static DateOnly GridStartDate(int year, int month)
	{
		var firstOfMonth = new DateOnly(year, month, 1);
		return firstOfMonth.AddDays(-(int)firstOfMonth.DayOfWeek); // Sun=0, Mon=1, ..., Sat=6
	}

	/// <summary>Number of day cells the grid renders.</summary>
	internal static int GridCellCount => DateCells;

	private static int Modulo(int value, int divisor) => ((value % divisor) + divisor) % divisor;

	private static string[] UpperCased(string[] names)
	{
		var result = new string[PeriodCells];
		for (var i = 0; i < PeriodCells; i++) result[i] = names[i].ToUpperInvariant();
		return result;
	}

	// Resource values are stored already uppercased so translators keep control of casing —
	// Ukrainian and English do not upper-case identically.
	private static string DayOfWeekName(int index) =>
		ResourceStrings.Get(DatePickerResourceKeys.DayShort[index], _dayOfWeekFallbacks[index]);

	private static string MonthShortName(int index) =>
		ResourceStrings.Get(DatePickerResourceKeys.MonthShort[index], _monthShortFallbacks[index]);

	private static string MonthFullName(int index) =>
		ResourceStrings.Get(DatePickerResourceKeys.MonthFull[index], _monthFullFallbacks[index]);

	// One arrow click moves by whatever the current grid is paging over.
	private void StepPage(int direction)
	{
		switch (_mode)
		{
			case PickerMode.Days:
				var month = _viewMonth + direction;
				var year = _viewYear;
				if (month < 1) { month = 12; year--; }
				else if (month > 12) { month = 1; year++; }
				SetViewMonth(year, month);
				break;
			case PickerMode.Months:
				SetViewMonth(_viewYear + direction, _viewMonth);
				break;
			default:
				_yearPageStart += direction * PeriodCells;
				break;
		}
		RenderCurrentMode();
	}

	private void RenderCurrentMode()
	{
		var isDays = _mode == PickerMode.Days;
		_calendarBlock.IsVisible = isDays;
		_periodBlock.IsVisible = !isDays;
		RefreshDayOfWeekHeader();
		UpdatePopupHeaderLabel();
		if (isDays) RenderDays();
		else RenderPeriod();
	}

	private void BuildDayOfWeekHeader()
	{
		// Sunday-first, two-letter uppercase (per design SVG).
		for (var i = 0; i < 7; i++)
		{
			var tb = new TextBlock
			{
				FontFamily = new FontFamily("Segoe UI"),
				FontWeight = FontWeight.SemiBold,
				FontSize = 16,
				LineHeight = 24,
				Foreground = _placeholderBrush,
				HorizontalAlignment = HorizontalAlignment.Center,
				VerticalAlignment = VerticalAlignment.Center
			};
			Grid.SetRow(tb, 0);
			Grid.SetColumn(tb, i);
			_calendarBlock.Children.Add(tb);
			_dayOfWeekLabels.Add(tb);
		}
		RefreshDayOfWeekHeader();
	}

	// Re-read on every render rather than once at construction so the labels follow the
	// application language without the control needing to observe a host-owned change event.
	private void RefreshDayOfWeekHeader()
	{
		for (var i = 0; i < _dayOfWeekLabels.Count; i++)
			_dayOfWeekLabels[i].Text = DayOfWeekName(i);
	}

	private void BuildDayCells()
	{
		for (var i = 0; i < DateCells; i++)
		{
			var row = 1 + (i / 7);
			var col = i % 7;
			var btn = CreateCellButton("dayCell");
			var index = i;
			btn.Click += (_, _) => OnDayCellClicked(index);
			Grid.SetRow(btn, row);
			Grid.SetColumn(btn, col);
			_calendarBlock.Children.Add(btn);
			_dayCells.Add(btn);
		}
	}

	private void BuildPeriodCells()
	{
		for (var i = 0; i < PeriodCells; i++)
		{
			var btn = CreateCellButton("periodCell");
			var index = i;
			btn.Click += (_, _) => OnPeriodCellClicked(index);
			Grid.SetRow(btn, i / 3);
			Grid.SetColumn(btn, i % 3);
			_periodBlock.Children.Add(btn);
			_periodCells.Add(btn);
		}
	}

	private static Button CreateCellButton(string className) => new()
	{
		Classes = { className },
		Theme = _flatButtonTheme,
		Content = new TextBlock
		{
			FontFamily = new FontFamily("Segoe UI"),
			FontSize = 16,
			LineHeight = 24,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
		}
	};

	private void RenderDays()
	{
		var startDate = GridStartDate(_viewYear, _viewMonth);
		var today = DateOnly.FromDateTime(DateTime.Today);

		for (var i = 0; i < DateCells; i++)
		{
			var date = startDate.AddDays(i);
			var cell = _dayCells[i];
			var inCurrentMonth = date.Month == _viewMonth && date.Year == _viewYear;
			var tb = (TextBlock)cell.Content!;
			tb.Text = date.Day.ToString(CultureInfo.InvariantCulture);
			cell.Tag = date;
			cell.Classes.Remove("outside");
			if (!inCurrentMonth) cell.Classes.Add("outside");

			var isSelected = _pendingDate.HasValue && _pendingDate.Value == date;
			var isToday = inCurrentMonth && date == today;
			ApplyCellState(cell, isSelected, isToday);
		}
	}

	private void RenderPeriod()
	{
		var today = DateOnly.FromDateTime(DateTime.Today);

		for (var i = 0; i < PeriodCells; i++)
		{
			var cell = _periodCells[i];
			var tb = (TextBlock)cell.Content!;
			bool isSelected, isToday;

			if (_mode == PickerMode.Months)
			{
				var month = i + 1;
				tb.Text = MonthShortName(i);
				cell.Tag = month;
				isSelected = _pendingDate is { } p && p.Year == _viewYear && p.Month == month;
				isToday = _viewYear == today.Year && month == today.Month;
			}
			else
			{
				var year = _yearPageStart + i;
				tb.Text = year.ToString(CultureInfo.InvariantCulture);
				cell.Tag = year;
				isSelected = _pendingDate is { } p && p.Year == year;
				isToday = year == today.Year;
			}

			ApplyCellState(cell, isSelected, isToday);
		}
	}

	private static void ApplyCellState(Button cell, bool isSelected, bool isToday)
	{
		cell.Classes.Remove("selected");
		cell.Classes.Remove("today");

		if (isSelected)
		{
			// Selected wins over today: solid purple fill (Background via style), no border.
			cell.Classes.Add("selected");
			cell.BorderBrush = Brushes.Transparent;
		}
		else if (isToday)
		{
			// Today (unselected): purple 1 px border only, no fill.
			cell.Classes.Add("today");
			cell.BorderBrush = _selectedBorderBrush;
		}
		else
		{
			cell.BorderBrush = Brushes.Transparent;
		}
	}

	private void OnDayCellClicked(int cellIndex)
	{
		var cell = _dayCells[cellIndex];
		if (cell.Tag is not DateOnly date) return;
		// Jump to the target month if the user picked a leading/trailing day from a neighbouring month.
		if (date.Year != _viewYear || date.Month != _viewMonth)
		{
			SetViewMonth(date.Year, date.Month);
			UpdatePopupHeaderLabel();
		}
		_pendingDate = date;
		_committedDate = date;
		RenderDays();
		UpdateHeader();
		DateSelected?.Invoke(date);
		_popup.IsOpen = false;
	}

	// Picking a year drills down to its months; picking a month drills down to its days.
	private void OnPeriodCellClicked(int cellIndex)
	{
		if (_periodCells[cellIndex].Tag is not int value) return;
		if (_mode == PickerMode.Months)
		{
			SetViewMonth(_viewYear, value);
			_mode = PickerMode.Days;
		}
		else
		{
			SetViewMonth(value, _viewMonth);
			_mode = PickerMode.Months;
		}
		RenderCurrentMode();
	}

	private void UpdatePopupHeaderLabel()
	{
		_headerLabel.Text = _mode switch
		{
			PickerMode.Days => $"{MonthFullName(_viewMonth - 1)} {_viewYear.ToString(CultureInfo.InvariantCulture)}",
			PickerMode.Months => _viewYear.ToString(CultureInfo.InvariantCulture),
			_ => $"{_yearPageStart} - {_yearPageStart + PeriodCells - 1}"
		};
	}

	private void UpdateHeader()
	{
		var hasDate = _committedDate.HasValue;

		_selectedText.Text = hasDate
			? _committedDate!.Value.ToString(_displayFormat, CultureInfo.InvariantCulture)
			: Placeholder ?? string.Empty;
		_selectedText.Foreground = hasDate ? Brushes.White : _placeholderBrush;

		// A filled field carries the same purple border as the sibling profile text fields.
		_container.Classes.Remove("filled");
		if (hasDate) _container.Classes.Add("filled");
	}

	private static ControlTheme BuildFlatButtonTheme()
	{
		return new ControlTheme(typeof(Button))
		{
			Setters =
			{
				new Setter(Button.TemplateProperty, new FuncControlTemplate<Button>((btn, ns) =>
					new Border
					{
						Name = "PART_ContainerBorder",
						[!Border.BackgroundProperty] = btn[!Button.BackgroundProperty],
						[!Border.BorderBrushProperty] = btn[!Button.BorderBrushProperty],
						[!Border.BorderThicknessProperty] = btn[!Button.BorderThicknessProperty],
						Child = new ContentPresenter
						{
							Name = "PART_ContentPresenter",
							[!ContentPresenter.ContentProperty] = btn[!Button.ContentProperty],
							[!ContentPresenter.PaddingProperty] = btn[!Button.PaddingProperty],
							[!ContentPresenter.HorizontalContentAlignmentProperty] = btn[!Button.HorizontalContentAlignmentProperty],
							[!ContentPresenter.VerticalContentAlignmentProperty] = btn[!Button.VerticalContentAlignmentProperty],
						}
					}))
			}
		};
	}

	private void InitializeComponent()
	{
		AvaloniaXamlLoader.Load(this);
	}
}
