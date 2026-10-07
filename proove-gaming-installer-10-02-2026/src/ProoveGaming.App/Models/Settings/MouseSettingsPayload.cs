namespace ProoveGaming.App.Models.Settings;

public sealed record MouseSettingsPayload(byte ButtonDebounce, byte LiftOffDistance);

// The two headset audio levels, 0-100. Persisted as device_settings rows like the mouse settings
// above, so preset snapshots pick them up with no extra plumbing.
//
// NULLABLE on purpose: a missing row means "no value stored", which is not the same as 0. Filling a
// missing key with default(int) would tell the caller the microphone is muted at zero, and the
// caller would then apply that to the endpoint. Only the keys actually present are populated.
public sealed record AudioSettingsPayload(int? OutputVolume, int? MicrophoneVolume);
