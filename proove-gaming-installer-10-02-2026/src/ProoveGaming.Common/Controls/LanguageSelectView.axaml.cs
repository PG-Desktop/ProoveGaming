using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using ProoveGaming.Common.Localization;

namespace ProoveGaming.Common.Controls;

public partial class LanguageSelectView : UserControl
{
	public static readonly IReadOnlyList<LanguageOption> Languages =
	[
		new LanguageOption
		{
			Code = LocalizationConstants.EnglishLanguageCode,
			DisplayName = "English",
			FlagIconUri = "avares://ProoveGaming.Common/Assets/Icons/icon-american.png"
		},
		new LanguageOption
		{
			Code = LocalizationConstants.UkrainianLanguageCode,
			// Endonym by convention: a user stuck in a language they can't read must still
			// find their own, so names stay in their own language (not localization resources).
			DisplayName = "Українська",
			FlagIconUri = "avares://ProoveGaming.Common/Assets/Icons/icon-ukraine.png"
		},
	];

	public event Action<string>? LanguageSelected;

	private int _selectedIndex;
	private readonly List<Button> _itemButtons = [];
	private readonly List<SvgImageView> _checkIcons = [];
	private Action? _closeDropdown;

	public LanguageSelectView()
	{
		InitializeComponent();

		var headerButton = this.FindControl<Button>("LanguageHeaderButton")!;
		headerButton.Theme = BuildFlatButtonTheme();
		var overlay = this.FindControl<Panel>("LanguageDropdownOverlay")!;
		var dismissOverlay = this.FindControl<Border>("LanguageDismissOverlay")!;
		var container = this.FindControl<Border>("LanguageContainer")!;
		var stack = this.FindControl<StackPanel>("LanguageItemsStack")!;
		var arrowDown = this.FindControl<Control>("LanguageArrowDownIcon")!;
		var arrowUp = this.FindControl<Control>("LanguageArrowUpIcon")!;

		BuildItems(stack);
		UpdateHeader(_selectedIndex);

		void CloseDropdown()
		{
			overlay.IsVisible = false;
			dismissOverlay.IsVisible = false;
			container.BorderThickness = new Thickness(1);
			arrowDown.IsVisible = true;
			arrowUp.IsVisible = false;
		}

		void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
		{
			if (!overlay.IsVisible) return;
			var pos = e.GetPosition(overlay);
			var bounds = new Rect(overlay.Bounds.Size);
			if (!bounds.Contains(pos))
				CloseDropdown();
		}

		void CloseDropdownAndDetach()
		{
			CloseDropdown();
			var window = TopLevel.GetTopLevel(this);
			window?.RemoveHandler(PointerPressedEvent, OnWindowPointerPressed);
		}

		headerButton.Click += (_, _) =>
		{
			var opening = !overlay.IsVisible;
			overlay.IsVisible = opening;
			dismissOverlay.IsVisible = opening;
			container.BorderThickness = new Thickness(opening ? 2 : 1);
			arrowDown.IsVisible = !opening;
			arrowUp.IsVisible = opening;
			if (opening)
			{
				RefreshItemHighlights();
				var window = TopLevel.GetTopLevel(this);
				window?.AddHandler(PointerPressedEvent, OnWindowPointerPressed, handledEventsToo: false);
			}
		};

		dismissOverlay.PointerPressed += (_, _) => CloseDropdownAndDetach();
		_closeDropdown = CloseDropdownAndDetach;
	}

	public void SetSelectedLanguage(string languageCode)
	{
		var idx = -1;
		for (var i = 0; i < Languages.Count; i++)
		{
			if (string.Equals(Languages[i].Code, languageCode, StringComparison.OrdinalIgnoreCase))
			{
				idx = i;
				break;
			}
		}
		if (idx < 0) return;
		_selectedIndex = idx;
		UpdateHeader(idx);
		RefreshItemHighlights();
	}

	private void BuildItems(StackPanel stack)
	{
		for (var i = 0; i < Languages.Count; i++)
		{
			var lang = Languages[i];
			var index = i;

			var flag = new Image
			{
				Width = 24,
				Height = 24,
				Stretch = Stretch.Uniform,
				Margin = new Thickness(0, 0, 8, 0),
				VerticalAlignment = VerticalAlignment.Center,
				Source = new Bitmap(AssetLoader.Open(new Uri(lang.FlagIconUri)))
			};

			var label = new TextBlock
			{
				Text = lang.DisplayName,
				Foreground = Brushes.White,
				VerticalAlignment = VerticalAlignment.Center
			};

			var row = new StackPanel
			{
				Orientation = Orientation.Horizontal,
				VerticalAlignment = VerticalAlignment.Center,
				Children = { flag, label }
			};

			var check = new SvgImageView
			{
				SourceUri = "avares://ProoveGaming.Common/Assets/Icons/Check.svg",
				Width = 24,
				Height = 24,
				Stretch = Stretch.Uniform,
				Opacity = 0.7,
				VerticalAlignment = VerticalAlignment.Center,
				IsVisible = false
			};

			var grid = new Grid
			{
				ColumnDefinitions = new ColumnDefinitions("*,Auto"),
				VerticalAlignment = VerticalAlignment.Center
			};
			Grid.SetColumn(row, 0);
			Grid.SetColumn(check, 1);
			grid.Children.Add(row);
			grid.Children.Add(check);

			var btn = new Button
			{
				Content = grid,
				Background = Brushes.Transparent,
				BorderThickness = new Thickness(0),
				HorizontalAlignment = HorizontalAlignment.Stretch,
				HorizontalContentAlignment = HorizontalAlignment.Stretch,
				VerticalContentAlignment = VerticalAlignment.Center,
				Padding = new Thickness(8, 0),
				Height = 40,
				Cursor = new Cursor(StandardCursorType.Hand),
				Theme = BuildFlatButtonTheme()
			};

			btn.Click += (_, _) => OnItemSelected(index);
			btn.PointerEntered += (_, _) =>
			{
				if (index != _selectedIndex)
					btn.Background = new SolidColorBrush(Color.Parse("#1E1A4C"));
			};
			btn.PointerExited += (_, _) =>
			{
				if (index != _selectedIndex)
					btn.Background = Brushes.Transparent;
			};
			_itemButtons.Add(btn);
			_checkIcons.Add(check);
			stack.Children.Add(btn);
		}
	}

	private static ControlTheme BuildFlatButtonTheme()
	{
		return new ControlTheme(typeof(Button))
		{
			Setters =
			{
				new Setter(Button.TemplateProperty, new FuncControlTemplate<Button>((btn, ns) =>
					new Avalonia.Controls.Presenters.ContentPresenter
					{
						Name = "PART_ContentPresenter",
						[!Avalonia.Controls.Presenters.ContentPresenter.BackgroundProperty] = btn[!!Button.BackgroundProperty],
						[!Avalonia.Controls.Presenters.ContentPresenter.ContentProperty] = btn[!!Button.ContentProperty],
						[!Avalonia.Controls.Presenters.ContentPresenter.PaddingProperty] = btn[!!Button.PaddingProperty],
						[!Avalonia.Controls.Presenters.ContentPresenter.HorizontalContentAlignmentProperty] = btn[!!Button.HorizontalContentAlignmentProperty],
						[!Avalonia.Controls.Presenters.ContentPresenter.VerticalContentAlignmentProperty] = btn[!!Button.VerticalContentAlignmentProperty],
					}))
			}
		};
	}

	private void RefreshItemHighlights()
	{
		for (var i = 0; i < _itemButtons.Count; i++)
		{
			var isSelected = i == _selectedIndex;
			_itemButtons[i].Background = isSelected
				? new SolidColorBrush(Color.Parse("#336357FF"))
				: Brushes.Transparent;
			_checkIcons[i].IsVisible = isSelected;
		}
	}

	private void OnItemSelected(int index)
	{
		_selectedIndex = index;
		_closeDropdown?.Invoke();
		UpdateHeader(index);
		LanguageSelected?.Invoke(Languages[index].Code);
	}

	private void UpdateHeader(int index)
	{
		var selectedItem = this.FindControl<StackPanel>("LanguageSelectedItem")!;
		selectedItem.Children.Clear();

		var lang = Languages[index];
		selectedItem.Children.Add(new Image
		{
			Width = 24,
			Height = 24,
			Stretch = Stretch.Uniform,
			Margin = new Thickness(0, 0, 8, 0),
			VerticalAlignment = VerticalAlignment.Center,
			Source = new Bitmap(AssetLoader.Open(new Uri(lang.FlagIconUri)))
		});
		selectedItem.Children.Add(new TextBlock
		{
			Text = lang.DisplayName,
			Foreground = Brushes.White,
			VerticalAlignment = VerticalAlignment.Center
		});
	}

	private void InitializeComponent()
	{
		AvaloniaXamlLoader.Load(this);
	}
}
