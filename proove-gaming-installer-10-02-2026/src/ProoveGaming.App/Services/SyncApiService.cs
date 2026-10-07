using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ProoveGaming.App.Services;

public sealed class SyncApiService : ISyncApiService
{
    private readonly ApiHttpClient _api;

    public SyncApiService(ApiHttpClient api)
    {
        _api = api;
    }

    public Task<MacroCreateResponse> CreateMacroAsync(string token, MacroCreateRequest request, CancellationToken ct = default) =>
        _api.PostAsync<MacroCreateResponse>("api/macros/create", request, token, ct);

    public Task<PresetCreateResponse> CreatePresetAsync(string token, PresetCreateRequest request, CancellationToken ct = default) =>
        _api.PostAsync<PresetCreateResponse>("api/presets/create", request, token, ct);

    public Task<MacroCreateResponse> UpdateMacroAsync(string token, MacroUpdateRequest request, CancellationToken ct = default) =>
        _api.PostAsync<MacroCreateResponse>("api/macros/update", request, token, ct);

    public Task<ApiResponse> DeleteMacroAsync(string token, string uid, string? updatedAt, CancellationToken ct = default) =>
        _api.PostAsync<ApiResponse>("api/macros/delete", new DeleteByUidRequest { Uid = uid, UpdatedAt = updatedAt }, token, ct);

    public Task<PresetCreateResponse> UpdatePresetAsync(string token, PresetUpdateRequest request, CancellationToken ct = default) =>
        _api.PostAsync<PresetCreateResponse>("api/presets/update", request, token, ct);

    public Task<ApiResponse> DeletePresetAsync(string token, string uid, string? updatedAt, CancellationToken ct = default) =>
        _api.PostAsync<ApiResponse>("api/presets/delete", new DeleteByUidRequest { Uid = uid, UpdatedAt = updatedAt }, token, ct);

    public Task<PreferencesUpdateResponse> UpdatePreferencesAsync(string token, Dictionary<string, object> preferences, string? updatedAt = null, CancellationToken ct = default) =>
        _api.PostAsync<PreferencesUpdateResponse>("api/preferences/update", new PreferencesUpdateRequest
        {
            Preferences = preferences,
            UpdatedAt = updatedAt
        }, token, ct);

    public Task<DeviceStateUpdateResponse> UpdateDeviceStateAsync(string token, int vendorId, int productId, string? activePresetUid, string? updatedAt = null, CancellationToken ct = default) =>
        _api.PostAsync<DeviceStateUpdateResponse>("api/devices/update", new DeviceStateUpdateRequest
        {
            VendorId = vendorId,
            ProductId = productId,
            ActivePresetUid = activePresetUid,
            UpdatedAt = updatedAt
        }, token, ct);

    // The list/get calls with a body-less contract still send "{}" — the PHP backend rejects
    // an absent body as INVALID_JSON.
    private static readonly object EmptyBody = new();

    public Task<MacroListResponse> ListMacrosAsync(string token, CancellationToken ct = default) =>
        _api.PostAsync<MacroListResponse>("api/macros/list", EmptyBody, token, ct);

    public Task<MacroGetResponse> GetMacroAsync(string token, string uid, CancellationToken ct = default) =>
        _api.PostAsync<MacroGetResponse>("api/macros/get", new UidRequest { Uid = uid }, token, ct);

    public Task<PresetListResponse> ListPresetsAsync(string token, CancellationToken ct = default) =>
        _api.PostAsync<PresetListResponse>("api/presets/list", EmptyBody, token, ct);

    public Task<PresetGetResponse> GetPresetAsync(string token, string uid, CancellationToken ct = default) =>
        _api.PostAsync<PresetGetResponse>("api/presets/get", new UidRequest { Uid = uid }, token, ct);

    public Task<PreferencesGetResponse> GetPreferencesAsync(string token, CancellationToken ct = default) =>
        _api.PostAsync<PreferencesGetResponse>("api/preferences/get", EmptyBody, token, ct);

    public Task<DeviceStateGetResponse> GetDeviceStateAsync(string token, int vendorId, int productId, CancellationToken ct = default) =>
        _api.PostAsync<DeviceStateGetResponse>("api/devices/get", new DeviceStateGetRequest
        {
            VendorId = vendorId,
            ProductId = productId
        }, token, ct);

    public Task<ApiResponse> AddDeviceAsync(string token, int vendorId, int productId, string name, CancellationToken ct = default) =>
        _api.PostAsync<ApiResponse>("api/devices/add", new DeviceAddRequest
        {
            VendorId = vendorId,
            ProductId = productId,
            Name = name
        }, token, ct);

    public Task<DeviceListResponse> ListDevicesAsync(string token, CancellationToken ct = default) =>
        _api.PostAsync<DeviceListResponse>("api/devices/list", EmptyBody, token, ct);

    public Task<ApiResponse> RemoveDeviceAsync(string token, int vendorId, int productId, CancellationToken ct = default) =>
        _api.PostAsync<ApiResponse>("api/devices/remove", new DeviceRemoveRequest
        {
            VendorId = vendorId,
            ProductId = productId
        }, token, ct);

    public Task<NotificationAddResponse> AddNotificationAsync(string token, NotificationAddRequest request, CancellationToken ct = default) =>
        _api.PostAsync<NotificationAddResponse>("api/notifications/add", request, token, ct);

    public Task<NotificationListResponse> ListNotificationsAsync(string token, CancellationToken ct = default) =>
        _api.PostAsync<NotificationListResponse>("api/notifications/list", EmptyBody, token, ct);

    public Task<ApiResponse> ReadNotificationAsync(string token, long notificationId, CancellationToken ct = default) =>
        _api.PostAsync<ApiResponse>("api/notifications/read", new NotificationIdRequest { NotificationId = notificationId }, token, ct);

    public Task<ApiResponse> UnreadNotificationAsync(string token, long notificationId, CancellationToken ct = default) =>
        _api.PostAsync<ApiResponse>("api/notifications/unread", new NotificationIdRequest { NotificationId = notificationId }, token, ct);

    public Task<ApiResponse> DeleteNotificationAsync(string token, long notificationId, CancellationToken ct = default) =>
        _api.PostAsync<ApiResponse>("api/notifications/delete", new NotificationIdRequest { NotificationId = notificationId }, token, ct);

    public Task<ApiResponse> ReadAllNotificationsAsync(string token, CancellationToken ct = default) =>
        _api.PostAsync<ApiResponse>("api/notifications/read_all", EmptyBody, token, ct);

    public Task<ApiResponse> DeleteAllNotificationsAsync(string token, CancellationToken ct = default) =>
        _api.PostAsync<ApiResponse>("api/notifications/delete_all", EmptyBody, token, ct);
}
