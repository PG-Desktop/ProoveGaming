using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using ProoveGaming.App.ViewModels;

namespace ProoveGaming.App.Views;

public partial class ChooseNicknameView : UserControl
{
	public event Action? BackRequested;
	public event Action<string>? NicknameChosen;

	private ChooseNicknameViewModel? Vm { get; set; }

	public ChooseNicknameView()
	{
		InitializeComponent();
	}

	protected override void OnDataContextChanged(EventArgs e)
	{
		base.OnDataContextChanged(e);
		if (Vm != null)
		{
			Vm.BackRequested -= ForwardBackRequested;
			Vm.NicknameChosen -= ForwardNicknameChosen;
			Vm.PropertyChanged -= OnVmPropertyChanged;
		}
		Vm = DataContext as ChooseNicknameViewModel;
		if (Vm != null)
		{
			Vm.BackRequested += ForwardBackRequested;
			Vm.NicknameChosen += ForwardNicknameChosen;
			Vm.PropertyChanged += OnVmPropertyChanged;
		}
	}

	private void ForwardBackRequested() => BackRequested?.Invoke();
	private void ForwardNicknameChosen(string nickname) => NicknameChosen?.Invoke(nickname);

	private void OnSuggestionClick(object? sender, RoutedEventArgs e)
	{
		// Picking a chip fills the field but leaves keyboard focus on the Button, so the next
		// Backspace lands on the chip and the text looks undeletable. Deferred so the command's
		// binding write happens first, then the caret goes to the end of the applied text.
		// Uses the generated NicknameTextBox field, not FindControl, so an XAML rename breaks
		// the build instead of silently doing nothing.
		Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
		{
			NicknameTextBox.Focus();
			NicknameTextBox.CaretIndex = NicknameTextBox.Text?.Length ?? 0;
		}, Avalonia.Threading.DispatcherPriority.Background);
	}

	private void OnNicknameGotFocus(object? sender, RoutedEventArgs e)
	{
		Vm?.ClearNicknameErrorCommand.Execute(null);
		if (Vm?.NicknameSuccess != true)
		{
			NicknameBorder.BorderBrush = NormalBorderBrush;
			NicknameBorder.BorderThickness = new Thickness(1);
		}
	}

	private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
	{
		switch (e.PropertyName)
		{
			case nameof(ChooseNicknameViewModel.NicknameError):
			case nameof(ChooseNicknameViewModel.NicknameSuccess):
				UpdateNicknameBorder();
				break;
		}
	}

	private void UpdateNicknameBorder()
	{
		if (Vm?.NicknameError == true)
		{
			NicknameBorder.BorderBrush = ErrorBrush;
			NicknameBorder.BorderThickness = new Thickness(2);
			NicknameBorder.BoxShadow = default;
		}
		else if (Vm?.NicknameSuccess == true)
		{
			NicknameBorder.BorderBrush = SuccessBrush;
			NicknameBorder.BorderThickness = new Thickness(2);
			NicknameBorder.BoxShadow = SuccessShadow;
		}
		else
		{
			NicknameBorder.BorderBrush = NormalBorderBrush;
			NicknameBorder.BorderThickness = new Thickness(1);
			NicknameBorder.BoxShadow = default;
		}
	}

	private static readonly IBrush NormalBorderBrush = new SolidColorBrush(Color.Parse("#4DBEBCEA"));
	private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#FF383C"));
	private static readonly IBrush SuccessBrush = new SolidColorBrush(Color.Parse("#00CE4F"));
	private static readonly BoxShadows SuccessShadow = BoxShadows.Parse("0 2 8 0 #1A000000");
}
