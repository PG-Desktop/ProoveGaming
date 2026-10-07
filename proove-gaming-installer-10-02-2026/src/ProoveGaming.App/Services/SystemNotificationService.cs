using Microsoft.Extensions.Logging;
#if WINDOWS
using Microsoft.Toolkit.Uwp.Notifications;
using System;
#endif

namespace ProoveGaming.App.Services;

public class SystemNotificationService
{
    private readonly ILogger<SystemNotificationService> _logger;

    public SystemNotificationService(ILogger<SystemNotificationService> logger)
    {
        _logger = logger;
    }

    public void Show(string title, string subtitle)
    {
#if WINDOWS
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(subtitle)
                .Show();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show system toast: {Title}", title);
        }
#else
        _logger.LogDebug("System toast not supported on this platform: {Title}", title);
#endif
    }
}
