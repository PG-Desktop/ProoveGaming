using Avalonia.Threading;
using System;

namespace ProoveGaming.App;

public partial class SingleCard : DeviceCardBase
{
    private DispatcherTimer? _tooltipHideTimer;

    public SingleCard()
    {
        InitializeComponent();
        WireDotsMenu(DotsButton, DotsOverlay);

        InfoIconArea.PointerEntered += (_, _) => ShowInfoTooltip();
        InfoIconArea.PointerExited  += (_, _) => ScheduleHideInfoTooltip();
        InfoTooltip.PointerEntered  += (_, _) => ShowInfoTooltip();
        InfoTooltip.PointerExited   += (_, _) => ScheduleHideInfoTooltip();
    }

    private void ShowInfoTooltip()
    {
        _tooltipHideTimer?.Stop();
        InfoTooltip.IsVisible = true;
    }

    private void ScheduleHideInfoTooltip()
    {
        _tooltipHideTimer?.Stop();
        _tooltipHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _tooltipHideTimer.Tick += (_, _) =>
        {
            _tooltipHideTimer?.Stop();
            InfoTooltip.IsVisible = false;
        };
        _tooltipHideTimer.Start();
    }
}
