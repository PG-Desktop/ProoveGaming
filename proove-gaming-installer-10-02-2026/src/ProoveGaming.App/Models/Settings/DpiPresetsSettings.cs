using System.Collections.Generic;

namespace ProoveGaming.App.Models.Settings;

public sealed record DpiPresetSetting(int StageIndex, int DpiValue, byte R, byte G, byte B);

public sealed record DpiPresetsSettings(IReadOnlyList<DpiPresetSetting> Stages);
