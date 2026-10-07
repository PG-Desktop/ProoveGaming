using System;
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ProoveGaming.App.Models;
using ProoveGaming.App.ViewModels;

namespace ProoveGaming.App.Views;

public partial class WelcomeView : UserControl
{
	public event Action<LegalDocument>? AgreementTermsRequested;
	public event Action? GuestContinueRequested;
	public event Action? SignInSucceeded;
	public event Action<IReadOnlyList<string>>? SignUpSucceeded;

	private WelcomeViewModel? _vm;
	private WelcomeViewModel Vm => _vm!;

	private Path[] _attemptSlots = [];

	public WelcomeView()
	{
		InitializeComponent();

		_attemptSlots =
		[
			AttemptSlot1, AttemptSlot2, AttemptSlot3, AttemptSlot4, AttemptSlot5,
			AttemptSlot6, AttemptSlot7, AttemptSlot8, AttemptSlot9, AttemptSlot10
		];
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (_vm != null)
		{
			_vm.AgreementTermsRequested -= ForwardAgreementTermsRequested;
			_vm.GuestContinueRequested -= ForwardGuestContinueRequested;
			_vm.SignInSucceeded -= ForwardSignInSucceeded;
			_vm.SignUpSucceeded -= ForwardSignUpSucceeded;
			_vm.PropertyChanged -= OnVmPropertyChanged;
		}
		_vm = DataContext as WelcomeViewModel;
		if (_vm != null)
		{
			_vm.AgreementTermsRequested += ForwardAgreementTermsRequested;
			_vm.GuestContinueRequested += ForwardGuestContinueRequested;
			_vm.SignInSucceeded += ForwardSignInSucceeded;
			_vm.SignUpSucceeded += ForwardSignUpSucceeded;
			_vm.PropertyChanged += OnVmPropertyChanged;

			SyncAttemptsLayout();
			SyncAttemptSlots();
		}
	}

	private void SyncAttemptsLayout()
	{
		if (_vm == null) return;
		SignInEmailBorder.Margin = _vm.AttemptsBlockVisible
			? new Thickness(0)
			: new Thickness(0, 100, 0, 0);
	}

	private void SyncAttemptSlots()
	{
		if (_vm == null) return;
		for (var i = 0; i < _attemptSlots.Length; i++)
			_attemptSlots[i].Fill = i < _vm.UsedAttempts ? AttemptUsedBrush : AttemptFreeBrush;
	}

	protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
	{
		if (_vm != null)
		{
			_vm.PropertyChanged -= OnVmPropertyChanged;
			_vm.Dispose();
		}
		base.OnDetachedFromVisualTree(e);
	}

	private void ForwardAgreementTermsRequested(LegalDocument document) => AgreementTermsRequested?.Invoke(document);
	private void ForwardGuestContinueRequested() => GuestContinueRequested?.Invoke();
	private void ForwardSignInSucceeded() => SignInSucceeded?.Invoke();
	private void ForwardSignUpSucceeded(IReadOnlyList<string> nicknames) => SignUpSucceeded?.Invoke(nicknames);

	// ── Tab parallelogram sizing ──────────────────────────────────────────
	// The tab is a parallelogram with a 15px slant on each side. The polygon
	// points need to grow with the button's actual width so each tab fits its
	// localized label without being cropped.
	private const double TabSlant = 15;

	private void OnSignInTabButtonSizeChanged(object? sender, SizeChangedEventArgs e) =>
		UpdateTabPolygons(e.NewSize.Width, SignInTabBg, SignInTabHoverBg);

	private void OnSignUpTabButtonSizeChanged(object? sender, SizeChangedEventArgs e) =>
		UpdateTabPolygons(e.NewSize.Width, SignUpTabBg, SignUpTabHoverBg);

	private static void UpdateTabPolygons(double width, Polygon background, Polygon hover)
	{
		if (width <= 0) return;
		var points = new Avalonia.Collections.AvaloniaList<Point>
		{
			new(0, 56),
			new(TabSlant, 0),
			new(width, 0),
			new(width - TabSlant, 56),
		};
		background.Points = points;
		hover.Points = new Avalonia.Collections.AvaloniaList<Point>(points);
	}

	// ── Tab hover (pure visual) ───────────────────────────────────────────

	private void OnSignInTabPointerEntered(object? sender, PointerEventArgs e)
	{
		if (!Vm.IsSignIn)
		{
			SignInTabHoverBg.IsVisible = true;
			SignInTabText.Foreground = HoverTextBrush;
		}
	}

	private void OnSignInTabPointerExited(object? sender, PointerEventArgs e)
	{
		SignInTabHoverBg.IsVisible = false;
		SignInTabText.Foreground = DefaultTextBrush;
	}

	private void OnSignUpTabPointerEntered(object? sender, PointerEventArgs e)
	{
		if (Vm.IsSignIn)
		{
			SignUpTabHoverBg.IsVisible = true;
			SignUpTabText.Foreground = HoverTextBrush;
		}
	}

	private void OnSignUpTabPointerExited(object? sender, PointerEventArgs e)
	{
		SignUpTabHoverBg.IsVisible = false;
		SignUpTabText.Foreground = DefaultTextBrush;
	}

	// ── Focus delegators ──────────────────────────────────────────────────

    private void OnEmailLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender == SignInEmailTextBox)
        {
            Vm.ValidateSignInEmailCommand.Execute(null);
            UpdateBorder(SignInEmailBorder, Vm.SignInEmailError || Vm.SignInCredentialsError, SignInEmailTextBox.Text);
        }
        else
        {
            Vm.ValidateSignUpEmailCommand.Execute(null);
            UpdateBorder(SignUpEmailBorder, Vm.SignUpEmailError, SignUpEmailTextBox.Text);
        }
    }

    private void OnEmailGotFocus(object? sender, RoutedEventArgs e)
    {
        if (sender == SignInEmailTextBox)
        {
            Vm.ClearSignInEmailErrorCommand.Execute(null);
            Vm.ClearSignInPasswordErrorCommand.Execute(null);
            SetBorderFocused(SignInEmailBorder);
        }
        else
        {
            Vm.ClearSignUpEmailErrorCommand.Execute(null);
            SetBorderFocused(SignUpEmailBorder);
        }
    }

    private void OnSignUpPasswordLostFocus(object? sender, RoutedEventArgs e)
    {
        Vm.ValidateSignUpPasswordCommand.Execute(null);
        UpdateBorder(SignUpPasswordBorder, Vm.SignUpPasswordError, SignUpPasswordTextBox.Text);
    }

    private void OnSignUpPasswordGotFocus(object? sender, RoutedEventArgs e)
    {
        Vm.ClearSignUpPasswordErrorCommand.Execute(null);
        SetBorderFocused(SignUpPasswordBorder);
    }

    private void OnSignInPasswordGotFocus(object? sender, RoutedEventArgs e)
    {
        Vm.ClearSignInPasswordErrorCommand.Execute(null);
        SetBorderFocused(SignInPasswordBorder);
    }

    private void OnSignInPasswordLostFocus(object? sender, RoutedEventArgs e)
    {
        UpdateBorder(SignInPasswordBorder, Vm.SignInCredentialsError, SignInPasswordTextBox.Text);
        UpdateBorder(SignInEmailBorder, Vm.SignInEmailError || Vm.SignInCredentialsError, SignInEmailTextBox.Text);
    }

	public void ShowSignUpTab() => Vm.SignUpTabCommand.Execute(null);

	// ── VM property changed ───────────────────────────────────────────────

	private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		switch (e.PropertyName)
		{
			case nameof(WelcomeViewModel.SignInEmailError):
				UpdateBorder(SignInEmailBorder, Vm.SignInEmailError || Vm.SignInCredentialsError, SignInEmailTextBox.Text);
				break;
			case nameof(WelcomeViewModel.SignInCredentialsError):
				UpdateBorder(SignInPasswordBorder, Vm.SignInCredentialsError, SignInPasswordTextBox.Text);
				UpdateBorder(SignInEmailBorder, Vm.SignInEmailError || Vm.SignInCredentialsError, SignInEmailTextBox.Text);
				break;
			case nameof(WelcomeViewModel.SignUpEmailError):
				UpdateBorder(SignUpEmailBorder, Vm.SignUpEmailError, SignUpEmailTextBox.Text);
				break;
			case nameof(WelcomeViewModel.SignUpPasswordError):
				UpdateBorder(SignUpPasswordBorder, Vm.SignUpPasswordError, SignUpPasswordTextBox.Text);
				break;
			case nameof(WelcomeViewModel.AttemptsBlockVisible):
				SyncAttemptsLayout();
				break;
			case nameof(WelcomeViewModel.IsSignInPasswordVisible):
				SignInPasswordTextBox.PasswordChar = Vm.IsSignInPasswordVisible ? '\0' : '•';
				SignInVisibilityOffIcon.IsVisible = !Vm.IsSignInPasswordVisible;
				SignInVisibilityOnIcon.IsVisible = Vm.IsSignInPasswordVisible;
				break;
			case nameof(WelcomeViewModel.IsSignUpPasswordVisible):
				SignUpPasswordTextBox.PasswordChar = Vm.IsSignUpPasswordVisible ? '\0' : '•';
				SignUpVisibilityOffIcon.IsVisible = !Vm.IsSignUpPasswordVisible;
				SignUpVisibilityOnIcon.IsVisible = Vm.IsSignUpPasswordVisible;
				break;
			case nameof(WelcomeViewModel.UsedAttempts):
				SyncAttemptSlots();
				break;
		}
	}

	private static void UpdateBorder(Border border, bool hasError, string? text = null)
	{
		if (hasError)
		{
			border.BorderBrush = ErrorBrush;
			border.BorderThickness = new Thickness(2);
		}
		else
		{
			border.BorderBrush = !string.IsNullOrEmpty(text) ? FocusBorderBrush : NormalBorderBrush;
			border.BorderThickness = new Thickness(1);
		}
	}

	private void OnSignInEmailTextChanged(object? sender, TextChangedEventArgs e) =>
		UpdateBorderOnTextChanged(SignInEmailBorder, SignInEmailTextBox);

	private void OnSignInPasswordTextChanged(object? sender, TextChangedEventArgs e) =>
		UpdateBorderOnTextChanged(SignInPasswordBorder, SignInPasswordTextBox);

	private void OnSignUpEmailTextChanged(object? sender, TextChangedEventArgs e) =>
		UpdateBorderOnTextChanged(SignUpEmailBorder, SignUpEmailTextBox);

	private void OnSignUpPasswordTextChanged(object? sender, TextChangedEventArgs e) =>
		UpdateBorderOnTextChanged(SignUpPasswordBorder, SignUpPasswordTextBox);

	private static void UpdateBorderOnTextChanged(Border border, TextBox textBox)
	{
		if (border.BorderBrush == ErrorBrush) return;
		if (!string.IsNullOrEmpty(textBox.Text))
			border.BorderBrush = FocusBorderBrush;
		else if (!textBox.IsFocused)
			border.BorderBrush = NormalBorderBrush;
	}

    private static void SetBorderFocused(Border border)
    {
        border.BorderBrush = FocusBorderBrush;
        border.BorderThickness = new Thickness(2);
    }

    // ── Brushes ───────────────────────────────────────────────────────────

    private static readonly IBrush HoverTextBrush = new SolidColorBrush(Color.Parse("#786DFF"));
    private static readonly IBrush DefaultTextBrush = Brushes.White;
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#FF383C"));
    private static readonly IBrush NormalBorderBrush = new SolidColorBrush(Color.Parse("#4DBEBCEA"));
    private static readonly IBrush FocusBorderBrush = new SolidColorBrush(Color.Parse("#6357FF"));
    private static readonly IBrush HoverBorderBrush = new SolidColorBrush(Color.Parse("#786DFF"));
    private static readonly IBrush AttemptUsedBrush = new SolidColorBrush(Color.Parse("#FF383C"));
    private static readonly IBrush AttemptFreeBrush = new SolidColorBrush(Color.Parse("#33BEBCEA"));

    private void OnBorderPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Border border && border.BorderBrush != ErrorBrush)
            border.BorderBrush = HoverBorderBrush;
    }

    private void OnBorderPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Border border && border.BorderBrush == HoverBorderBrush)
        {
            var textBox = GetTextBoxForBorder(border);
            border.BorderBrush = textBox?.IsFocused == true || !string.IsNullOrEmpty(textBox?.Text)
                ? FocusBorderBrush
                : NormalBorderBrush;
        }
    }

    private TextBox? GetTextBoxForBorder(Border border)
    {
        if (border == SignInEmailBorder) return SignInEmailTextBox;
        if (border == SignInPasswordBorder) return SignInPasswordTextBox;
        if (border == SignUpEmailBorder) return SignUpEmailTextBox;
        if (border == SignUpPasswordBorder) return SignUpPasswordTextBox;
        return null;
    }
}
