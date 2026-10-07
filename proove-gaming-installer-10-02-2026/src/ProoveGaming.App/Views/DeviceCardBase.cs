using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.VisualTree;

namespace ProoveGaming.App;

// Coordinates "only one open menu at a time" across a group of cards.
// Shared is fine for the single-list scenario; swap it for a per-group instance if needed.
internal sealed class CardMenuCoordinator
{
    public static readonly CardMenuCoordinator Shared = new();

    private DeviceCardBase? _openCard;

    public event Action<DeviceCardBase>? MenuOpened;

    public void NotifyOpened(DeviceCardBase card)
    {
        _openCard = card;
        MenuOpened?.Invoke(card);
    }

    public void NotifyClosed(DeviceCardBase card)
    {
        if (_openCard == card)
            _openCard = null;
    }

    public bool IsOpen(DeviceCardBase card) => _openCard == card;
    public bool HasOpenCard => _openCard != null;
}

public abstract class DeviceCardBase : UserControl
{
    private readonly CardMenuCoordinator _coordinator = CardMenuCoordinator.Shared;

    private Button? _dotsButton;
    private Control? _dotsOverlay;

    protected DeviceCardBase()
    {
        // The control's own (transparent) background is what keeps the hover hit-area at the
        // LAYOUT position: the lift below transforms only the content, and a transform on the
        // control itself used to move the hit-test rect with the visuals — a pointer entering
        // across the bottom edge then sat outside the lifted card, exited, dropped it,
        // re-entered, and the card visibly jittered up and down (PG-494).
        Background = Brushes.Transparent;
        // The lifted content renders 10px above this control's bounds — without this the
        // control clips it and the card's top vanished instead of rising.
        ClipToBounds = false;

        PointerEntered += (_, _) =>
        {
            if (!_coordinator.HasOpenCard)
                SetLift(true);
        };
        PointerExited += (_, _) =>
        {
            if (!_coordinator.IsOpen(this))
                SetLift(false);
        };
    }

    // Lifts the card's CONTENT, not the control — see the constructor note. The per-card
    // XAML declares the matching TransformOperationsTransition on the same root element.
    private void SetLift(bool lifted)
    {
        if (Content is Control content)
            content.RenderTransform = TransformOperations.Parse(lifted ? "translateY(-10px)" : "none");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _coordinator.MenuOpened += OnOtherMenuOpened;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _coordinator.MenuOpened -= OnOtherMenuOpened;
        if (_coordinator.IsOpen(this))
        {
            UnsubscribeFromTopLevel();
            _coordinator.NotifyClosed(this);
        }
    }

    private void OnOtherMenuOpened(DeviceCardBase sender)
    {
        if (sender != this)
            CloseMenu();
    }

    protected void WireDotsMenu(Button dotsButton, Control dotsOverlay)
    {
        _dotsButton = dotsButton;
        _dotsOverlay = dotsOverlay;

        dotsButton.Click += (_, _) =>
        {
            dotsOverlay.IsVisible = !dotsOverlay.IsVisible;
            if (dotsOverlay.IsVisible)
            {
                dotsButton.Classes.Add("open");
                SetLift(true);
                _coordinator.NotifyOpened(this);
                SubscribeToTopLevel();
            }
            else
            {
                UnsubscribeFromTopLevel();
                _coordinator.NotifyClosed(this);
                dotsButton.Classes.Remove("open");
                if (!IsPointerOver)
                    SetLift(false);
            }
        };
    }

    private void SubscribeToTopLevel()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        topLevel?.AddHandler(PointerPressedEvent, OnTopLevelPointerPressed, RoutingStrategies.Tunnel);
    }

    private void UnsubscribeFromTopLevel()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        topLevel?.RemoveHandler(PointerPressedEvent, OnTopLevelPointerPressed);
    }

    private void OnTopLevelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var source = e.Source as Visual;
        while (source != null)
        {
            if (source == _dotsButton || source == _dotsOverlay)
                return;
            source = source.GetVisualParent();
        }
        CloseMenu();
    }

    private void CloseMenu()
    {
        UnsubscribeFromTopLevel();
        if (_dotsOverlay != null) _dotsOverlay.IsVisible = false;
        if (_dotsButton != null) _dotsButton.Classes.Remove("open");
        _coordinator.NotifyClosed(this);
        if (!IsPointerOver)
            SetLift(false);
    }
}
