using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace ProoveGaming.App.Views.Dialogs;

// The shared confirm/cancel modal. ResetPresetDialog and DeletePresetDialog were byte-identical
// apart from two resource keys, two command bindings and one button width, so the chrome — the
// chamfered 620x220 canvas, the glow, the corner close button — lives here once.
//
// The DataContext stays whatever the host passes in (the DeviceDetails VM), so the Command
// properties bind to that VM as usual. The text and command properties below are set on the
// INSTANCE, which is what makes one control serve several confirmations.
internal partial class ConfirmDialog : UserControl
{
	public static readonly StyledProperty<string?> TitleProperty =
		AvaloniaProperty.Register<ConfirmDialog, string?>(nameof(Title));

	public static readonly StyledProperty<string?> BodyProperty =
		AvaloniaProperty.Register<ConfirmDialog, string?>(nameof(Body));

	public static readonly StyledProperty<string?> ConfirmTextProperty =
		AvaloniaProperty.Register<ConfirmDialog, string?>(nameof(ConfirmText));

	public static readonly StyledProperty<ICommand?> ConfirmCommandProperty =
		AvaloniaProperty.Register<ConfirmDialog, ICommand?>(nameof(ConfirmCommand));

	// Drives both the Cancel button and the corner close button — dismissing by either route must
	// clear the same pending state, which is why they are one property rather than two.
	public static readonly StyledProperty<ICommand?> CancelCommandProperty =
		AvaloniaProperty.Register<ConfirmDialog, ICommand?>(nameof(CancelCommand));

	// Destructive confirmations paint the confirm button red (the shared cancelDelete/confirmDelete
	// pair) instead of the default primary/secondary pair. A variant on this control rather than a
	// second copy of its chrome: the device-delete dialog in MainWindow already hand-rolled the
	// whole 620x196 canvas to get that one red button, and a third copy is what CLAUDE.md's
	// "keep the variant, share the rest" rule exists to prevent.
	public static readonly StyledProperty<bool> IsDestructiveProperty =
		AvaloniaProperty.Register<ConfirmDialog, bool>(nameof(IsDestructive));

	public string? Title
	{
		get => GetValue(TitleProperty);
		set => SetValue(TitleProperty, value);
	}

	public string? Body
	{
		get => GetValue(BodyProperty);
		set => SetValue(BodyProperty, value);
	}

	public string? ConfirmText
	{
		get => GetValue(ConfirmTextProperty);
		set => SetValue(ConfirmTextProperty, value);
	}

	public ICommand? ConfirmCommand
	{
		get => GetValue(ConfirmCommandProperty);
		set => SetValue(ConfirmCommandProperty, value);
	}

	public ICommand? CancelCommand
	{
		get => GetValue(CancelCommandProperty);
		set => SetValue(CancelCommandProperty, value);
	}

	public bool IsDestructive
	{
		get => GetValue(IsDestructiveProperty);
		set => SetValue(IsDestructiveProperty, value);
	}

	public ConfirmDialog()
	{
		InitializeComponent();

		// The content binds to THIS control's properties, not to the host's view-model. Binding via
		// $parent[ConfirmDialog] fails at load — a type being defined by x:Class is not resolvable
		// from inside its own file — so the content's DataContext is retargeted here instead.
		//
		// The control's OWN DataContext is left alone: the host still sets it to the view-model, and
		// that is what the ConfirmCommand / CancelCommand bindings on each instance resolve against.
		PART_Root.DataContext = this;
	}
}
