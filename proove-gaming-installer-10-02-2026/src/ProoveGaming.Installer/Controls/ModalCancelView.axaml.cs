using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace ProoveGaming.Installer.Controls;

internal partial class ModalCancelView : UserControl
{
	public static readonly StyledProperty<ICommand?> PrimaryCommandProperty =
		AvaloniaProperty.Register<ModalCancelView, ICommand?>(nameof(PrimaryCommand));

	public static readonly StyledProperty<ICommand?> SecondaryCommandProperty =
		AvaloniaProperty.Register<ModalCancelView, ICommand?>(nameof(SecondaryCommand));

	public static readonly StyledProperty<ICommand?> CloseCommandProperty =
		AvaloniaProperty.Register<ModalCancelView, ICommand?>(nameof(CloseCommand));

	public ICommand? PrimaryCommand
	{
		get => GetValue(PrimaryCommandProperty);
		set => SetValue(PrimaryCommandProperty, value);
	}

	public ICommand? SecondaryCommand
	{
		get => GetValue(SecondaryCommandProperty);
		set => SetValue(SecondaryCommandProperty, value);
	}

	public ICommand? CloseCommand
	{
		get => GetValue(CloseCommandProperty);
		set => SetValue(CloseCommandProperty, value);
	}

	public ModalCancelView()
	{
		InitializeComponent();
		ApplyBindings();
	}

	static ModalCancelView()
	{
		PrimaryCommandProperty.Changed.AddClassHandler<ModalCancelView>((x, _) => x.ApplyBindings());
		SecondaryCommandProperty.Changed.AddClassHandler<ModalCancelView>((x, _) => x.ApplyBindings());
		CloseCommandProperty.Changed.AddClassHandler<ModalCancelView>((x, _) => x.ApplyBindings());
	}

	private void InitializeComponent()
	{
		AvaloniaXamlLoader.Load(this);
	}

	private void ApplyBindings()
	{
		this.FindControl<Button>("PrimaryButton")!.Command = PrimaryCommand;
		this.FindControl<Button>("SecondaryButton")!.Command = SecondaryCommand;
		this.FindControl<Button>("CloseButton")!.Command = CloseCommand;
	}
}
