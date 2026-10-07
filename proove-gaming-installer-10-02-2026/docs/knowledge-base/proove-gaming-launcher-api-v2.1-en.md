# Proove Gaming Launcher API — v2.1

English translation of the source docx (`API 2.1.docx`) delivered 2026-08-31. The source
mixes Ukrainian prose with English endpoint/JSON content; the prose is translated here, all
endpoint names, JSON bodies, field names and error codes are kept verbatim.

Diff vs v2.0 (per the document's own changelog):

- **Devices** gains a per-user device list: `api/devices/add`, `api/devices/list`,
  `api/devices/remove`. `devices/list` returns `active_preset_uid` alongside the device data
  (joined from `customer_device_state`).
- **Notifications Management** — a new endpoint family for the Launcher's bell list:
  `api/notifications/add`, `list`, `read`, `unread`, `delete`, `read_all`, `delete_all`.
  `notifications/list` returns `unread_count`.

Everything else (auth, profile, presets, macros, preferences, `devices/get`/`devices/update`,
contact) is unchanged in shape vs v2.0 — see `proove-gaming-launcher-api-v2.0-en.md` for
those sections. Only the new/changed sections are translated in full below.

## Document info

| Property       | Value                  |
| -------------- | ---------------------- |
| Project        | Proove Gaming Launcher |
| Protocol       | HTTPS                  |
| Format         | JSON                   |
| Authentication | Bearer Token           |
| Encoding       | UTF-8                  |
| Base URL       | `https://proovegaming.com/index.php?route=` |

## 19. Devices (v2.1 shape)

The API stores both the state of a specific device (`active_preset_uid`) and the user's
overall device list. All endpoints in this section require a Bearer Token.

### 19.1 Get device state — `POST api/devices/get` (unchanged vs v2.0)

Request:

```json
{
  "vendor_id": 1241,
  "product_id": 41541
}
```

Success:

```json
{
  "success": true,
  "device": {
    "vendor_id": 1241,
    "product_id": 41541,
    "active_preset_uid": null,
    "created_at": null,
    "updated_at": null
  }
}
```

If the state has never been created, `active_preset_uid`, `created_at` and `updated_at` may
be `null`.

### 19.2 Update device state — `POST api/devices/update` (unchanged vs v2.0)

Request:

```json
{
  "vendor_id": 1241,
  "product_id": 41541,
  "active_preset_uid": "11111111-1111-4111-8111-111111111111",
  "updated_at": "2026-08-19 16:08:03"
}
```

`active_preset_uid` may be `null` to clear the active preset. For an existing state the
client sends the current `updated_at`.

Error codes: `ACTIVE_PRESET_NOT_FOUND`, `UPDATED_AT_REQUIRED`, `INVALID_UPDATED_AT`,
`DEVICE_STATE_CONFLICT`.

### 19.3 Add a device to the user's list — `POST api/devices/add` (new in v2.1)

Request:

```json
{
  "vendor_id": 1241,
  "product_id": 41541,
  "name": "Proove Gaming Mouse"
}
```

Success:

```json
{
  "success": true,
  "message": "Device added successfully",
  "device": {
    "vendor_id": 1241,
    "product_id": 41541,
    "name": "Proove Gaming Mouse"
  }
}
```

Re-adding the same `vendor_id`/`product_id` for the user returns HTTP 409 with error code
`DEVICE_ALREADY_EXISTS`.

Error codes: `INVALID_VENDOR_ID`, `INVALID_PRODUCT_ID`, `NAME_REQUIRED`, `NAME_TOO_LONG`,
`DEVICE_ALREADY_EXISTS`.

### 19.4 List the user's devices — `POST api/devices/list` (new in v2.1)

Request: `{}`

Success:

```json
{
  "success": true,
  "devices": [
    {
      "vendor_id": 1241,
      "product_id": 41541,
      "name": "Proove Gaming Mouse",
      "active_preset_uid": null,
      "created_at": "2026-08-28 16:12:53",
      "updated_at": "2026-08-28 16:12:53"
    }
  ]
}
```

If the user has no stored devices, the API returns `devices: []`. `active_preset_uid` is
joined in from `customer_device_state`.

### 19.5 Remove a device from the user's list — `POST api/devices/remove` (new in v2.1)

Request:

```json
{
  "vendor_id": 1241,
  "product_id": 41541
}
```

Success:

```json
{
  "success": true,
  "message": "Device removed successfully"
}
```

If the device is not in the user's list, the API returns HTTP 404 with error code
`DEVICE_NOT_FOUND`.

Removing a device from `customer_devices` does **not** delete presets, macros or
`customer_device_state`.

## 21. Notifications Management (new in v2.1)

A separate API for the notifications list in the Launcher. Supports create, list,
read/unread, deleting a single notification, read all and delete all. All endpoints in this
section require a Bearer Token.

### 21.1 Add a notification — `POST api/notifications/add`

Request:

```json
{
  "title": "New firmware available",
  "message": "A new firmware version is available for your device.",
  "type": "firmware"
}
```

Success:

```json
{
  "success": true,
  "message": "Notification added successfully",
  "notification": {
    "id": 1,
    "title": "New firmware available",
    "message": "A new firmware version is available for your device.",
    "type": "firmware",
    "is_read": false,
    "created_at": "2026-08-28 16:48:25",
    "updated_at": "2026-08-28 16:48:25"
  }
}
```

Validation: `title` is required, max 255 characters; `message` is required; `type` is
optional, max 50 characters. Error codes: `TITLE_REQUIRED`, `TITLE_TOO_LONG`,
`MESSAGE_REQUIRED`, `TYPE_TOO_LONG`.

### 21.2 List notifications — `POST api/notifications/list`

Request: `{}`

Success:

```json
{
  "success": true,
  "notifications": [
    {
      "id": 1,
      "title": "New firmware available",
      "message": "A new firmware version is available for your device.",
      "type": "firmware",
      "is_read": false,
      "created_at": "2026-08-28 16:48:25",
      "updated_at": "2026-08-28 16:48:25"
    }
  ],
  "unread_count": 1
}
```

Notifications are returned newest to oldest. `unread_count` holds the number of
notifications with `is_read = false`.

### 21.3 Mark as read — `POST api/notifications/read`

Request:

```json
{ "notification_id": 1 }
```

Success:

```json
{
  "success": true,
  "message": "Notification marked as read",
  "notification": { "id": 1, "is_read": true }
}
```

For a nonexistent notification the API returns HTTP 404 and `NOTIFICATION_NOT_FOUND`.

### 21.4 Mark as unread — `POST api/notifications/unread`

Request:

```json
{ "notification_id": 1 }
```

Success:

```json
{
  "success": true,
  "message": "Notification marked as unread",
  "notification": { "id": 1, "is_read": false }
}
```

For a nonexistent notification the API returns HTTP 404 and `NOTIFICATION_NOT_FOUND`.

### 21.5 Delete a notification — `POST api/notifications/delete`

Request:

```json
{ "notification_id": 1 }
```

Success:

```json
{
  "success": true,
  "message": "Notification deleted successfully"
}
```

Deleting the same id again returns HTTP 404 and `NOTIFICATION_NOT_FOUND`.

### 21.6 Mark all as read — `POST api/notifications/read_all`

Request: `{}`

Success:

```json
{
  "success": true,
  "message": "All notifications marked as read"
}
```

Afterwards `notifications/list` returns `unread_count: 0` and `is_read` is `true` for all
notifications.

### 21.7 Delete all notifications — `POST api/notifications/delete_all`

Request: `{}`

Success:

```json
{
  "success": true,
  "message": "All notifications deleted successfully"
}
```

Afterwards `notifications/list` returns `notifications: []` and `unread_count: 0`.

### Shared Notifications error codes

`TOKEN_REQUIRED`, `INVALID_OR_EXPIRED_TOKEN`, `INVALID_JSON`, `INVALID_NOTIFICATION_ID`,
`NOTIFICATION_NOT_FOUND`.

## Changelog (translated)

- **v2.1**
  - Devices: added the user's overall device list (`add`/`list`/`remove`).
  - Devices list returns `active_preset_uid` together with the device data.
  - Notifications Management: `add`/`list`/`read`/`unread`/`delete`/`read_all`/`delete_all`.
  - Notifications list returns `unread_count`.
- v2.0 and earlier — see `proove-gaming-launcher-api-v2.0-en.md`.
