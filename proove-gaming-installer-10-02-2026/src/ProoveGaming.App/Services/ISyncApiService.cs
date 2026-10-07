using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ProoveGaming.App.Services;

// The API v2.0 sync endpoints the upgrade push (docs/plans/add-api-v2-sync), the sign-in
// pull (docs/plans/add-api-v2-pull) and the live-sync coordinator
// (docs/plans/add-api-v2-live-sync) need. All POST with a Bearer token; like
// IAuthApiService, implementations never throw — every transport failure comes back as a
// response with IsNetworkError set.
public interface ISyncApiService
{
    Task<MacroCreateResponse> CreateMacroAsync(string token, MacroCreateRequest request, CancellationToken ct = default);

    Task<PresetCreateResponse> CreatePresetAsync(string token, PresetCreateRequest request, CancellationToken ct = default);

    // The update/delete quartet reuses the create responses' shape (the server echoes the
    // written record, whose updated_at the coordinator stores for the next conflict check).
    Task<MacroCreateResponse> UpdateMacroAsync(string token, MacroUpdateRequest request, CancellationToken ct = default);

    Task<ApiResponse> DeleteMacroAsync(string token, string uid, string? updatedAt, CancellationToken ct = default);

    Task<PresetCreateResponse> UpdatePresetAsync(string token, PresetUpdateRequest request, CancellationToken ct = default);

    Task<ApiResponse> DeletePresetAsync(string token, string uid, string? updatedAt, CancellationToken ct = default);

    // updatedAt null = first write for this account (the upgrade push) — the field is
    // omitted from the body; any later update sends the stored value for conflict handling.
    Task<PreferencesUpdateResponse> UpdatePreferencesAsync(string token, Dictionary<string, object> preferences, string? updatedAt = null, CancellationToken ct = default);

    Task<DeviceStateUpdateResponse> UpdateDeviceStateAsync(string token, int vendorId, int productId, string? activePresetUid, string? updatedAt = null, CancellationToken ct = default);

    // Pull side. The list calls send an empty body — no VID/PID filter, the pull wants the
    // account's records for every device type, including types with no local device card.
    Task<MacroListResponse> ListMacrosAsync(string token, CancellationToken ct = default);

    // Per-uid fallback when a list item arrives without its events (plan Open question 1).
    Task<MacroGetResponse> GetMacroAsync(string token, string uid, CancellationToken ct = default);

    Task<PresetListResponse> ListPresetsAsync(string token, CancellationToken ct = default);

    // Per-uid fallback when a list item arrives without its configuration.
    Task<PresetGetResponse> GetPresetAsync(string token, string uid, CancellationToken ct = default);

    Task<PreferencesGetResponse> GetPreferencesAsync(string token, CancellationToken ct = default);

    Task<DeviceStateGetResponse> GetDeviceStateAsync(string token, int vendorId, int productId, CancellationToken ct = default);

    // The v2.1 device roster (docs/plans/add-api-v21-device-roster). Membership calls are
    // idempotent: DEVICE_ALREADY_EXISTS / DEVICE_NOT_FOUND are success to their callers.
    Task<ApiResponse> AddDeviceAsync(string token, int vendorId, int productId, string name, CancellationToken ct = default);

    Task<DeviceListResponse> ListDevicesAsync(string token, CancellationToken ct = default);

    Task<ApiResponse> RemoveDeviceAsync(string token, int vendorId, int productId, CancellationToken ct = default);

    // The v2.1 Notifications Management family (docs/plans/add-api-v21-notifications-sync).
    // Server-id-keyed; NOTIFICATION_NOT_FOUND is success to the delete's caller and "the
    // server lost it" to the read/unread callers.
    Task<NotificationAddResponse> AddNotificationAsync(string token, NotificationAddRequest request, CancellationToken ct = default);

    Task<NotificationListResponse> ListNotificationsAsync(string token, CancellationToken ct = default);

    Task<ApiResponse> ReadNotificationAsync(string token, long notificationId, CancellationToken ct = default);

    Task<ApiResponse> UnreadNotificationAsync(string token, long notificationId, CancellationToken ct = default);

    Task<ApiResponse> DeleteNotificationAsync(string token, long notificationId, CancellationToken ct = default);

    Task<ApiResponse> ReadAllNotificationsAsync(string token, CancellationToken ct = default);

    Task<ApiResponse> DeleteAllNotificationsAsync(string token, CancellationToken ct = default);
}
