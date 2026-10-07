using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ProoveGaming.App.Services;

public partial class NotificationService : ObservableObject
{
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private string _text = string.Empty;

    private CancellationTokenSource? _cts;

    public void Show(string text)
    {
        _cts?.Cancel();
        _cts?.Dispose();

        Text = text;
        IsVisible = true;

        var cts = new CancellationTokenSource();
        _cts = cts;
        Task.Delay(5000, cts.Token).ContinueWith(t =>
        {
            if (t.Status == TaskStatus.RanToCompletion)
                Dispatcher.UIThread.Post(() => IsVisible = false);
        });
    }

    [RelayCommand]
    public void Hide()
    {
        _cts?.Cancel();
        _cts = null;
        IsVisible = false;
    }
}
