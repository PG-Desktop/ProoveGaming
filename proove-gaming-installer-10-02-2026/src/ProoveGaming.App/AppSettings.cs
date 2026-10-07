using System;
using Microsoft.Extensions.Configuration;

namespace ProoveGaming.App;

public static class AppSettings
{
    public static bool MinimizeToTrayEnabled { get; set; } = true;
    public static int SessionTimeHours { get; set; } = 48;

    // Lead time for the guest session-expiry warning (docs/plans/add-guest-session-expiry-warning).
    // Minutes rather than hours so QA can produce a warning without a 45-hour wait: with
    // SessionTimeHours = 1 and 45 minutes here, the warning lands ~15 minutes in. A value at or
    // beyond the whole session disables the warning — see SessionService.StartWarningTimer.
    public static int SessionExpiryWarningMinutes { get; set; } = 180;

    // From the embedded appsettings.json, like ApiSettings (see ConfigurationManager): an install
    // ships no settings file, so a value is changed in the repository only.
    public static void Load()
    {
        using var settings = ConfigurationManager.OpenEmbeddedSettings();
        var configuration = new ConfigurationBuilder()
            .AddJsonStream(settings)
            .Build();

        MinimizeToTrayEnabled = configuration.GetValue<bool>("MinimizeToTrayEnabled", MinimizeToTrayEnabled);
        SessionTimeHours = configuration.GetValue<int>("SessionTimeHours", SessionTimeHours);
        SessionExpiryWarningMinutes = configuration.GetValue<int>("SessionExpiryWarningMinutes", SessionExpiryWarningMinutes);
    }
}
