using System.Threading;
using System.Threading.Tasks;
using ProoveGaming.App.Models.Settings;

namespace ProoveGaming.App.Services;

// Typed façade over LocalStorageService for the current owner (guest UUID or
// customer id) and physical device (vid/pid + DeviceCardViewModel.DeviceKey, which
// disambiguates two identical units sharing the same vid/pid). The VM never touches
// raw SQL / JSON. Save methods no-op when no owner is available (pre-guest state);
// load methods return null.
public interface ISettingsRepository
{
    Task<RgbDeviceSettings?> LoadRgbAsync(int vid, int pid, string deviceKey, CancellationToken ct = default);
    Task SaveRgbAsync(int vid, int pid, string deviceKey, RgbDeviceSettings state, CancellationToken ct = default);

    Task<DpiPresetsSettings?> LoadDpiPresetsAsync(int vid, int pid, string deviceKey, CancellationToken ct = default);
    Task SaveDpiPresetsAsync(int vid, int pid, string deviceKey, DpiPresetsSettings state, CancellationToken ct = default);

    Task<int?> LoadActiveDpiStageAsync(int vid, int pid, string deviceKey, CancellationToken ct = default);
    Task SaveActiveDpiStageAsync(int vid, int pid, string deviceKey, int stageIndex, CancellationToken ct = default);

    Task<int?> LoadPollingRateAsync(int vid, int pid, string deviceKey, CancellationToken ct = default);
    Task SavePollingRateAsync(int vid, int pid, string deviceKey, int hz, CancellationToken ct = default);

    Task<MouseSettingsPayload?> LoadMouseSettingsAsync(int vid, int pid, string deviceKey, CancellationToken ct = default);
    Task SaveMouseSettingsAsync(int vid, int pid, string deviceKey, MouseSettingsPayload payload, CancellationToken ct = default);

    Task<AudioSettingsPayload?> LoadAudioSettingsAsync(int vid, int pid, string deviceKey, CancellationToken ct = default);
    Task SaveAudioSettingsAsync(int vid, int pid, string deviceKey, AudioSettingsPayload payload, CancellationToken ct = default);

    Task<AppPreferencesPayload?> LoadAppPreferencesAsync(CancellationToken ct = default);
    Task SaveAppPreferencesAsync(AppPreferencesPayload payload, CancellationToken ct = default);

    // True while a queued Save* write for THIS device scope has not landed yet. Lets the preset
    // dirty check decide whether its synchronous compare could have read pre-write rows without
    // being fooled by unrelated storage-queue traffic (notifications, another device).
    bool HasPendingDeviceWrites(int vid, int pid, string deviceKey);
}
