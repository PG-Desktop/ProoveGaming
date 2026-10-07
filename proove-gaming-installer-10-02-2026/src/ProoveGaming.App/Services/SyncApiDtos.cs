using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ProoveGaming.App.Services;

// Request/response DTOs for the API v2.0 sync endpoints (presets/macros/preferences/devices),
// snake_case per the contract (docs/knowledge-base/proove-gaming-launcher-api-v2.0-en.md).
// The upgrade push (docs/plans/add-api-v2-sync) owns the create requests; the sign-in pull
// (docs/plans/add-api-v2-pull) owns the list/get responses; the live-sync coordinator
// (docs/plans/add-api-v2-live-sync) owns the update/delete requests — an update request is
// its create request plus the echoed updated_at the conflict check compares.

public class MacroCreateRequest
{
    [JsonPropertyName("uid")]
    public string Uid { get; set; } = string.Empty;

    [JsonPropertyName("vendor_id")]
    public int VendorId { get; set; }

    [JsonPropertyName("product_id")]
    public int ProductId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("repeat_count")]
    public int RepeatCount { get; set; }

    // Numeric value of the proto MacroRepeatMode enum; the server's accepted range is
    // undocumented (plan Open question 3), so the raw int travels unmapped.
    [JsonPropertyName("repeat_mode")]
    public int RepeatMode { get; set; }

    [JsonPropertyName("events")]
    public List<MacroEventDto> Events { get; set; } = [];
}

public sealed class MacroEventDto
{
    [JsonPropertyName("delay_ms")]
    public int DelayMs { get; set; }

    [JsonPropertyName("is_press")]
    public bool IsPress { get; set; }

    [JsonPropertyName("key_code")]
    public int KeyCode { get; set; }
}

// The update request reuses the full create shape (assumed full payload — plan Open
// question 2) plus the stored updated_at; UpdatedAt is omitted when null so a refresh-retry
// that failed to learn a timestamp still serializes a contract-valid body.
public sealed class MacroUpdateRequest : MacroCreateRequest
{
    [JsonPropertyName("updated_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpdatedAt { get; set; }
}

public class PresetCreateRequest
{
    [JsonPropertyName("uid")]
    public string Uid { get; set; } = string.Empty;

    [JsonPropertyName("vendor_id")]
    public int VendorId { get; set; }

    [JsonPropertyName("product_id")]
    public int ProductId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("is_default")]
    public bool IsDefault { get; set; }

    // The serialized snapshot built by PresetConfigurationBuilder — an opaque JSON object
    // from the server's point of view, so it stays untyped here.
    [JsonPropertyName("configuration")]
    public object? Configuration { get; set; }
}

public sealed class PresetUpdateRequest : PresetCreateRequest
{
    [JsonPropertyName("updated_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpdatedAt { get; set; }
}

// macros/delete and presets/delete: the uid plus the stored updated_at for conflict
// protection — omitted when the tombstone never learned one (contract tolerates it the
// same way it tolerates a first preferences/update without a timestamp).
public sealed class DeleteByUidRequest
{
    [JsonPropertyName("uid")]
    public string Uid { get; set; } = string.Empty;

    [JsonPropertyName("updated_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpdatedAt { get; set; }
}

public sealed class MacroCreateResponse : ApiResponse
{
    [JsonPropertyName("macro")]
    public SyncRecordDto? Macro { get; set; }
}

public sealed class PresetCreateResponse : ApiResponse
{
    [JsonPropertyName("preset")]
    public SyncRecordDto? Preset { get; set; }
}

// The slice of the server's echoed record the push cares about. Nullable throughout: an
// error response carries no record object at all, and the PHP backend can answer with a
// shape (e.g. an empty array) this fails to bind — neither may NRE the push loop.
public sealed class SyncRecordDto
{
    [JsonPropertyName("uid")]
    public string? Uid { get; set; }

    // Opaque server timestamp (no timezone in the contract's format) — stored verbatim in
    // server_updated_at, never parsed.
    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }
}

public sealed class PreferencesUpdateRequest
{
    [JsonPropertyName("preferences")]
    public Dictionary<string, object> Preferences { get; set; } = [];

    // Omitted on the account's first save (no server version to conflict with).
    [JsonPropertyName("updated_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpdatedAt { get; set; }
}

// preferences/update echoes the record's timestamps after a successful write; the
// coordinator stores updated_at in sync_meta for the next conflict check. Nullable like
// every response field — a 2xx that fails to bind is success without write-back.
public sealed class PreferencesUpdateResponse : ApiResponse
{
    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }
}

public sealed class DeviceStateUpdateRequest
{
    [JsonPropertyName("vendor_id")]
    public int VendorId { get; set; }

    [JsonPropertyName("product_id")]
    public int ProductId { get; set; }

    [JsonPropertyName("active_preset_uid")]
    public string? ActivePresetUid { get; set; }

    // Omitted before the state's first write; null ActivePresetUid stays serialized — the
    // contract gives it meaning ("clear the active preset").
    [JsonPropertyName("updated_at")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpdatedAt { get; set; }
}

// PreferencesUpdateResponse's device-state twin.
public sealed class DeviceStateUpdateResponse : ApiResponse
{
    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }
}

// ── Pull (docs/plans/add-api-v2-pull) ────────────────────────────────────────
// Every response field is nullable: the list shapes are undocumented (plan Open question 1)
// and the PHP backend's shape quirks are the norm — an item that binds to nulls is skipped by
// the pull pipeline with a log line, never thrown on.

public sealed class UidRequest
{
    [JsonPropertyName("uid")]
    public string Uid { get; set; } = string.Empty;
}

public sealed class DeviceStateGetRequest
{
    [JsonPropertyName("vendor_id")]
    public int VendorId { get; set; }

    [JsonPropertyName("product_id")]
    public int ProductId { get; set; }
}

public sealed class MacroListResponse : ApiResponse
{
    [JsonPropertyName("macros")]
    public List<MacroPullDto?>? Macros { get; set; }
}

public sealed class MacroGetResponse : ApiResponse
{
    [JsonPropertyName("macro")]
    public MacroPullDto? Macro { get; set; }
}

public sealed class PresetListResponse : ApiResponse
{
    [JsonPropertyName("presets")]
    public List<PresetPullDto?>? Presets { get; set; }
}

public sealed class PresetGetResponse : ApiResponse
{
    [JsonPropertyName("preset")]
    public PresetPullDto? Preset { get; set; }
}

public sealed class MacroPullDto
{
    [JsonPropertyName("uid")]
    public string? Uid { get; set; }

    [JsonPropertyName("vendor_id")]
    public int? VendorId { get; set; }

    [JsonPropertyName("product_id")]
    public int? ProductId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("repeat_count")]
    public int? RepeatCount { get; set; }

    [JsonPropertyName("repeat_mode")]
    public int? RepeatMode { get; set; }

    // Null when macros/list does not embed the events — the pull then falls back to a
    // per-uid macros/get (plan Open question 1).
    [JsonPropertyName("events")]
    public List<MacroEventDto?>? Events { get; set; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }
}

public sealed class PresetPullDto
{
    [JsonPropertyName("uid")]
    public string? Uid { get; set; }

    [JsonPropertyName("vendor_id")]
    public int? VendorId { get; set; }

    [JsonPropertyName("product_id")]
    public int? ProductId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("is_default")]
    public bool? IsDefault { get; set; }

    // The opaque configuration object PresetConfigurationApplier deserializes. JsonElement
    // (not a typed DTO) so unknown keys survive binding untouched; null when presets/list
    // does not embed it — the pull then falls back to a per-uid presets/get.
    [JsonPropertyName("configuration")]
    public JsonElement? Configuration { get; set; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }
}

public sealed class PreferencesGetResponse : ApiResponse
{
    // The contract's documented quirk: an account without saved preferences answers with
    // "preferences": [] (an array), otherwise an object of key/value pairs — a JsonElement
    // tolerates both without a converter.
    [JsonPropertyName("preferences")]
    public JsonElement? Preferences { get; set; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }
}

public sealed class DeviceStateGetResponse : ApiResponse
{
    // Null when the state has never been created (contract §19.1) — the pull then leaves
    // the local applied-preset rows alone.
    [JsonPropertyName("active_preset_uid")]
    public string? ActivePresetUid { get; set; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }
}

// ── Device roster (docs/plans/add-api-v21-device-roster) ─────────────────────
// API v2.1 §19.3–19.5: one server row per device model the customer owns, keyed by the
// pair the App pushes (the card's settings identity). No updated_at handling — membership
// is idempotent (DEVICE_ALREADY_EXISTS / DEVICE_NOT_FOUND count as success).

public sealed class DeviceAddRequest
{
    [JsonPropertyName("vendor_id")]
    public int VendorId { get; set; }

    [JsonPropertyName("product_id")]
    public int ProductId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public sealed class DeviceRemoveRequest
{
    [JsonPropertyName("vendor_id")]
    public int VendorId { get; set; }

    [JsonPropertyName("product_id")]
    public int ProductId { get; set; }
}

public sealed class DeviceListResponse : ApiResponse
{
    [JsonPropertyName("devices")]
    public List<DeviceListItemDto?>? Devices { get; set; }
}

public sealed class DeviceListItemDto
{
    [JsonPropertyName("vendor_id")]
    public int? VendorId { get; set; }

    [JsonPropertyName("product_id")]
    public int? ProductId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    // Joined in from customer_device_state (contract §19.4). Bound but unused for now —
    // the pull's per-type devices/get flow stays authoritative for applied presets.
    [JsonPropertyName("active_preset_uid")]
    public string? ActivePresetUid { get; set; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }
}

// ── Notifications Management (docs/plans/add-api-v21-notifications-sync) ─────
// API v2.1 §21: the account's bell list, keyed by the SERVER's row id. No updated_at
// conflict handling — the only mutable field is a boolean read flag, pushed idempotently.

public sealed class NotificationAddRequest
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

// read / unread / delete all take the same single-field body.
public sealed class NotificationIdRequest
{
    [JsonPropertyName("notification_id")]
    public long NotificationId { get; set; }
}

public sealed class NotificationAddResponse : ApiResponse
{
    [JsonPropertyName("notification")]
    public NotificationDto? Notification { get; set; }
}

public sealed class NotificationListResponse : ApiResponse
{
    [JsonPropertyName("notifications")]
    public List<NotificationDto?>? Notifications { get; set; }

    // Bound but deliberately unused: the local unread count derives from the loaded rows
    // (a divergence would only mean rows beyond the local 100-per-owner cap).
    [JsonPropertyName("unread_count")]
    public int? UnreadCount { get; set; }
}

public sealed class NotificationDto
{
    [JsonPropertyName("id")]
    public long? Id { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("is_read")]
    public bool? IsRead { get; set; }

    // Contract format "yyyy-MM-dd HH:mm:ss", no timezone — parsed best-effort for the
    // bell's date grouping, never compared.
    [JsonPropertyName("created_at")]
    public string? CreatedAt { get; set; }

    [JsonPropertyName("updated_at")]
    public string? UpdatedAt { get; set; }
}
