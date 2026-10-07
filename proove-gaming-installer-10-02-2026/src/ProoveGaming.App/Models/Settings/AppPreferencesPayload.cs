namespace ProoveGaming.App.Models.Settings;

public sealed record AppPreferencesPayload(
    bool ScrollOneScreenAtATime,
    double ScrollLines,
    double DoubleClickSpeed,
    double SleepTimer,
    double GenericLiftOffDistance);
