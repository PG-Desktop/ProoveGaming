namespace ProoveGaming.Common.Localization;

/// <summary>Resource keys for the text rendered inside the DatePickerView calendar popup.
/// Listed explicitly rather than composed at call sites so a key can be grepped from the
/// resource dictionaries it must exist in.</summary>
public static class DatePickerResourceKeys
{
	/// <summary>Weekday column headers, Sunday-first to match the calendar grid.</summary>
	public static readonly string[] DayShort =
	{
		"DatePicker_DayShort_Su",
		"DatePicker_DayShort_Mo",
		"DatePicker_DayShort_Tu",
		"DatePicker_DayShort_We",
		"DatePicker_DayShort_Th",
		"DatePicker_DayShort_Fr",
		"DatePicker_DayShort_Sa",
	};

	/// <summary>Abbreviated month names for the month grid; index 0 is January.</summary>
	public static readonly string[] MonthShort =
	{
		"DatePicker_MonthShort_1",
		"DatePicker_MonthShort_2",
		"DatePicker_MonthShort_3",
		"DatePicker_MonthShort_4",
		"DatePicker_MonthShort_5",
		"DatePicker_MonthShort_6",
		"DatePicker_MonthShort_7",
		"DatePicker_MonthShort_8",
		"DatePicker_MonthShort_9",
		"DatePicker_MonthShort_10",
		"DatePicker_MonthShort_11",
		"DatePicker_MonthShort_12",
	};

	/// <summary>Full month names for the popup header label; index 0 is January. Must be the
	/// nominative form — the header shows the month standalone, not as part of a date.</summary>
	public static readonly string[] MonthFull =
	{
		"DatePicker_MonthFull_1",
		"DatePicker_MonthFull_2",
		"DatePicker_MonthFull_3",
		"DatePicker_MonthFull_4",
		"DatePicker_MonthFull_5",
		"DatePicker_MonthFull_6",
		"DatePicker_MonthFull_7",
		"DatePicker_MonthFull_8",
		"DatePicker_MonthFull_9",
		"DatePicker_MonthFull_10",
		"DatePicker_MonthFull_11",
		"DatePicker_MonthFull_12",
	};
}
