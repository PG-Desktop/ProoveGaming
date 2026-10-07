namespace ProoveGaming.App.Services;

// Notification type slugs and their bell visuals (docs/plans/add-api-v21-notifications-sync).
// The slug is pushed as api/notifications `type` and mapped back to an icon when a row is
// pulled on another machine — the server carries no icon fields. This table is the single
// source of truth for the bell's icons: HomeViewModel's NotificationIcon constants are built
// from it, so a pulled row and a locally raised one render identically. Unknown or empty
// types (a backend-invented slug, a pre-sync row) fall back to the Info glyph.
public static class NotificationTypes
{
    public const string Info = "info";
    public const string DeviceConnected = "device_connected";
    public const string DeviceDisconnected = "device_disconnected";
    public const string LowBattery = "low_battery";
    public const string AppUpdate = "app_update";
    public const string Session = "session";
    public const string Contact = "contact";

    public readonly record struct NotificationVisual(string IconData, string IconUri, double IconWidth, double IconHeight);

    // Download-arrow glyph for the "Update available" entry — an inline path because
    // ProoveGaming.Common ships no SVG asset for it (docs/plans/contact-us-and-update-check).
    private const string AppUpdateGlyph =
        "M8 12L3 7L4.4 5.6L7 8.2V0H9V8.2L11.6 5.6L13 7L8 12ZM2 16C1.45 16 0.979167 15.8042 0.5875 15.4125C0.195833 15.0208 0 14.55 0 14V11H2V14H14V11H16V14C16 14.55 15.8042 15.0208 15.4125 15.4125C15.0208 15.8042 14.55 16 14 16H2Z";

    // Envelope glyph for the Contact Us delivery entry — inline for the same reason.
    private const string ContactGlyph =
        "M2 16C1.45 16 0.979167 15.8042 0.5875 15.4125C0.195833 15.0208 0 14.55 0 14V2C0 1.45 0.195833 0.979167 0.5875 0.5875C0.979167 0.195833 1.45 0 2 0H18C18.55 0 19.0208 0.195833 19.4125 0.5875C19.8042 0.979167 20 1.45 20 2V14C20 14.55 19.8042 15.0208 19.4125 15.4125C19.0208 15.8042 18.55 16 18 16H2ZM10 9L2 4V14H18V4L10 9ZM10 7L18 2H2L10 7ZM2 4V2V14V4Z";

    private const string InfoIconUri = "avares://ProoveGaming.Common/Assets/Icons/Info.svg";

    public static NotificationVisual VisualFor(string? type) => type switch
    {
        DeviceConnected => new(string.Empty, "avares://ProoveGaming.Common/Assets/Icons/Device-Connected.svg", 18, 18),
        DeviceDisconnected => new(string.Empty, "avares://ProoveGaming.Common/Assets/Icons/Device-Disconnected.svg", 22, 22),
        LowBattery => new(string.Empty, "avares://ProoveGaming.Common/Assets/Icons/Low-Battery.svg", 22, 12),
        AppUpdate => new(AppUpdateGlyph, string.Empty, 16, 16),
        Contact => new(ContactGlyph, string.Empty, 20, 16),
        // Session reuses the Info glyph deliberately (docs/plans/add-guest-session-expiry-warning:
        // there is no clock/timer asset), and it is also the unknown-type fallback.
        _ => new(string.Empty, InfoIconUri, 22, 22),
    };
}
