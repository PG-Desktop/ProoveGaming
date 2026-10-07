using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Styling;

namespace ProoveGaming.Common.Controls;

public partial class CountrySelectView : UserControl
{
	/// <summary>Placeholder text shown in the header when no country is selected yet.</summary>
	public static readonly StyledProperty<string?> PlaceholderProperty =
		AvaloniaProperty.Register<CountrySelectView, string?>(nameof(Placeholder));

	// ISO 3166-1 alpha-2 codes excluded per product decision.
	private static readonly HashSet<string> ExcludedRegions = new(StringComparer.OrdinalIgnoreCase)
	{
		"RU", // Russia
		"BY", // Belarus
		"KP", // North Korea (DPRK)
	};

	// Enumerate every country the OS knows about (via specific cultures → RegionInfo),
	// drop the excluded set, sort alphabetically by English name.
	public static readonly IReadOnlyList<string> Countries = BuildCountries();

	// Reverse lookup: normalize any known synonym (ISO 2-letter, ISO 3-letter, English name)
	// back to the exact string we display, so a value round-tripped through a backend that
	// stores it differently still selects the right row.
	private static readonly Dictionary<string, string> AliasToDisplayName = BuildAliasMap();

	// One theme instance shared by every row — ControlTheme is immutable once built, and the
	// list is ~250 rows long.
	private static readonly ControlTheme _flatButtonTheme = BuildFlatButtonTheme();
	private static readonly IBrush _hoverBrush = new ImmutableSolidColorBrush(Color.Parse("#1E1A4C"));
	private static readonly IBrush _selectedBrush = new ImmutableSolidColorBrush(Color.Parse("#336357FF"));
	private static readonly IBrush _placeholderBrush = new ImmutableSolidColorBrush(Color.Parse("#B2BEBCEA"));

	public event Action<string>? CountrySelected;

	private int _selectedIndex = -1;
	private readonly List<Button> _itemButtons = new();
	private readonly List<Grid> _itemGrids = new();
	private readonly StackPanel _itemsStack;
	private readonly Popup _popup;
	private readonly Border _container;
	private readonly TextBlock _selectedText;
	// A single check icon, re-parented into whichever row is selected. Rendering one per row
	// would rasterize the same SVG ~250 times for an indicator only one row ever shows.
	private SvgImageView? _checkIcon;
	private int _checkIconRow = -1;
	private bool _itemsBuilt;

	static CountrySelectView()
	{
		PlaceholderProperty.Changed.AddClassHandler<CountrySelectView>((x, _) =>
		{
			if (x._selectedIndex < 0) x.UpdateHeader();
		});
	}

	public CountrySelectView()
	{
		InitializeComponent();

		var headerButton = this.FindControl<Button>("CountryHeaderButton")!;
		headerButton.Theme = _flatButtonTheme;
		_popup = this.FindControl<Popup>("CountryDropdownPopup")!;
		_itemsStack = this.FindControl<StackPanel>("CountryItemsStack")!;
		_selectedText = this.FindControl<TextBlock>("CountrySelectedText")!;
		_container = this.FindControl<Border>("CountryContainer")!;
		var arrowDown = this.FindControl<Control>("CountryArrowDownIcon")!;
		var arrowUp = this.FindControl<Control>("CountryArrowUpIcon")!;

		UpdateHeader();

		headerButton.Click += (_, _) =>
		{
			_popup.IsOpen = !_popup.IsOpen;
		};

		_popup.Opened += (_, _) =>
		{
			_container.Classes.Add("open");
			arrowDown.IsVisible = false;
			arrowUp.IsVisible = true;
			EnsureItemsBuilt();
			RefreshItemHighlights();
			// Scroll the selected item into view once the popup has laid out.
			Avalonia.Threading.Dispatcher.UIThread.Post(() =>
			{
				if (_selectedIndex >= 0 && _selectedIndex < _itemButtons.Count)
					_itemButtons[_selectedIndex].BringIntoView();
			}, Avalonia.Threading.DispatcherPriority.Background);
		};

		_popup.Closed += (_, _) =>
		{
			_container.Classes.Remove("open");
			arrowDown.IsVisible = true;
			arrowUp.IsVisible = false;
		};
	}

	/// <summary>Placeholder text shown in the header when no country is selected yet.</summary>
	public string? Placeholder
	{
		get => GetValue(PlaceholderProperty);
		set => SetValue(PlaceholderProperty, value);
	}

	public void SetSelectedCountry(string? country)
	{
		_selectedIndex = string.IsNullOrEmpty(country) ? -1 : ResolveCountryIndex(country);
		UpdateHeader();
		RefreshItemHighlights();
	}

	// Resolve an incoming country string to an index in Countries. Tries the exact display
	// name first, then the alias map (ISO 2/3-letter codes, English name lookup). Returns
	// -1 if nothing matches — the caller shows placeholder in that case.
	internal static int ResolveCountryIndex(string country)
	{
		for (var i = 0; i < Countries.Count; i++)
		{
			if (string.Equals(Countries[i], country, StringComparison.OrdinalIgnoreCase))
				return i;
		}
		if (AliasToDisplayName.TryGetValue(country.Trim(), out var displayName))
		{
			for (var i = 0; i < Countries.Count; i++)
			{
				if (string.Equals(Countries[i], displayName, StringComparison.OrdinalIgnoreCase))
					return i;
			}
		}
		return -1;
	}

	// ~250 rows of controls are only worth building once the user actually opens the dropdown;
	// ProfileView itself is constructed at window startup whether or not it is ever shown.
	private void EnsureItemsBuilt()
	{
		if (_itemsBuilt) return;
		_itemsBuilt = true;

		_checkIcon = new SvgImageView
		{
			SourceUri = "avares://ProoveGaming.Common/Assets/Icons/Check.svg",
			Width = 24,
			Height = 24,
			Stretch = Stretch.Uniform,
			Opacity = 0.7,
			VerticalAlignment = VerticalAlignment.Center
		};
		Grid.SetColumn(_checkIcon, 1);

		for (var i = 0; i < Countries.Count; i++)
		{
			var index = i;

			var label = new TextBlock
			{
				Text = Countries[i],
				Foreground = Brushes.White,
				VerticalAlignment = VerticalAlignment.Center
			};
			Grid.SetColumn(label, 0);

			var grid = new Grid
			{
				ColumnDefinitions = new ColumnDefinitions("*,Auto"),
				VerticalAlignment = VerticalAlignment.Center
			};
			grid.Children.Add(label);

			var btn = new Button
			{
				Content = grid,
				Background = Brushes.Transparent,
				BorderThickness = new Thickness(0),
				HorizontalAlignment = HorizontalAlignment.Stretch,
				HorizontalContentAlignment = HorizontalAlignment.Stretch,
				VerticalContentAlignment = VerticalAlignment.Center,
				Padding = new Thickness(8, 0),
				Height = 36,
				Cursor = new Cursor(StandardCursorType.Hand),
				Theme = _flatButtonTheme
			};

			btn.Click += (_, _) => OnItemSelected(index);
			btn.PointerEntered += (_, _) =>
			{
				if (index != _selectedIndex)
					btn.Background = _hoverBrush;
			};
			btn.PointerExited += (_, _) =>
			{
				if (index != _selectedIndex)
					btn.Background = Brushes.Transparent;
			};
			_itemButtons.Add(btn);
			_itemGrids.Add(grid);
			_itemsStack.Children.Add(btn);
		}
	}

	private static ControlTheme BuildFlatButtonTheme()
	{
		return new ControlTheme(typeof(Button))
		{
			Setters =
			{
				new Setter(Button.TemplateProperty, new FuncControlTemplate<Button>((btn, ns) =>
					new ContentPresenter
					{
						Name = "PART_ContentPresenter",
						[!ContentPresenter.BackgroundProperty] = btn[!!Button.BackgroundProperty],
						[!ContentPresenter.ContentProperty] = btn[!!Button.ContentProperty],
						[!ContentPresenter.PaddingProperty] = btn[!!Button.PaddingProperty],
						[!ContentPresenter.HorizontalContentAlignmentProperty] = btn[!!Button.HorizontalContentAlignmentProperty],
						[!ContentPresenter.VerticalContentAlignmentProperty] = btn[!!Button.VerticalContentAlignmentProperty],
					}))
			}
		};
	}

	private void RefreshItemHighlights()
	{
		for (var i = 0; i < _itemButtons.Count; i++)
		{
			_itemButtons[i].Background = i == _selectedIndex ? _selectedBrush : Brushes.Transparent;
		}
		MoveCheckIcon(_selectedIndex);
	}

	private void MoveCheckIcon(int index)
	{
		if (_checkIcon == null || index == _checkIconRow) return;
		if (_checkIconRow >= 0 && _checkIconRow < _itemGrids.Count)
			_itemGrids[_checkIconRow].Children.Remove(_checkIcon);
		if (index >= 0 && index < _itemGrids.Count)
			_itemGrids[index].Children.Add(_checkIcon);
		_checkIconRow = index >= 0 && index < _itemGrids.Count ? index : -1;
	}

	private void OnItemSelected(int index)
	{
		_selectedIndex = index;
		_popup.IsOpen = false;
		UpdateHeader();
		CountrySelected?.Invoke(Countries[index]);
	}

	private void UpdateHeader()
	{
		var hasCountry = _selectedIndex >= 0;

		_selectedText.Text = hasCountry ? Countries[_selectedIndex] : Placeholder ?? string.Empty;
		_selectedText.Foreground = hasCountry ? Brushes.White : _placeholderBrush;

		// A filled field carries the same purple border as the sibling profile text fields.
		_container.Classes.Remove("filled");
		if (hasCountry) _container.Classes.Add("filled");
	}

	private static IReadOnlyList<string> BuildCountries()
	{
		var byCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
		{
			RegionInfo region;
			try { region = new RegionInfo(culture.Name); }
			catch { continue; }
			if (ExcludedRegions.Contains(region.TwoLetterISORegionName)) continue;
			// EnglishName is stable per region; dedupe by ISO code so we don't get "United Kingdom" twice
			// from en-GB and cy-GB, etc.
			byCode[region.TwoLetterISORegionName] = region.EnglishName;
		}
		return byCode.Values
			.OrderBy(n => n, StringComparer.Ordinal)
			.ToArray();
	}

	private static Dictionary<string, string> BuildAliasMap()
	{
		var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
		{
			RegionInfo region;
			try { region = new RegionInfo(culture.Name); }
			catch { continue; }
			if (ExcludedRegions.Contains(region.TwoLetterISORegionName)) continue;
			var display = region.EnglishName;
			// Every alias points at the display name so callers get the canonical spelling back.
			map[region.EnglishName] = display;
			map[region.TwoLetterISORegionName] = display;
			map[region.ThreeLetterISORegionName] = display;
		}
		return map;
	}

	private void InitializeComponent()
	{
		AvaloniaXamlLoader.Load(this);
	}
}
