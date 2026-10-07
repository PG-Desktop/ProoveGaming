# Proove Gaming Launcher API — v2.0

English translation of the source docx delivered 2026-08-25. Note on naming: the file was
named `API 1.8_.docx`, but the document titles itself **"Proove Gaming Launcher API v2.0"**
and its changelog ends at a v2.0 entry — v2.0 is the version this translation uses. The
source mixes Ukrainian prose with English endpoint/JSON content; the prose is translated
here, all endpoint names, JSON bodies, field names and error codes are kept verbatim.

Diff vs v1.7: five whole new endpoint families — **Presets** (`api/presets/*`), **Macros**
(`api/macros/*`), **Preferences** (`api/preferences/*`), **Device State** (`api/devices/*`)
and **Contact Us** (`api/contact`) — plus `updated_at`-based optimistic conflict handling
across the four sync families and a 429 status code. The v1.7 account/profile endpoints are
unchanged in shape; their sections below are the translated v2.0 text, which matches v1.7's
content (see `proove-gaming-launcher-api-v1.7-en.md` for the fuller per-endpoint tables).

## Document info

| Property        | Value                     |
| --------------- | ------------------------- |
| Project         | Proove Gaming Launcher    |
| Protocol        | HTTPS                     |
| Format          | JSON                      |
| Authentication  | Bearer Token              |
| Encoding        | UTF-8                     |

## Base URL

```
https://proovegaming.com/index.php?route=
```

- All requests must be sent over HTTPS.
- Request body: `application/json`
- Response: `application/json`

## Authentication

Protected endpoints require Bearer Token authentication.

```
Authorization: Bearer {access_token}
```

Example:

```
Authorization: Bearer 79c5666282e64a2564645a10e579872072109498
```

### Public endpoints (no auth)

- `POST api/check_email`
- `POST api/check_nickname`
- `POST api/register`
- `POST api/login`
- `POST api/forgotten`
- `POST api/reset_password`
- `POST api/contact`

### Protected endpoints (Bearer Token required)

- `POST api/profile`
- `POST api/profile/update`
- `POST api/profile/avatar`
- `POST api/profile/notifications`
- `POST api/profile/notifications_update`
- `POST api/profile/check_nickname`
- `POST api/profile/password_link`
- `POST api/profile/delete`
- `POST api/logout`
- `POST api/presets/create`
- `POST api/presets/list`
- `POST api/presets/get`
- `POST api/presets/update`
- `POST api/presets/delete`
- `POST api/macros/create`
- `POST api/macros/list`
- `POST api/macros/get`
- `POST api/macros/update`
- `POST api/macros/delete`
- `POST api/preferences/get`
- `POST api/preferences/update`
- `POST api/devices/get`
- `POST api/devices/update`

## HTTP status codes

| Code | Meaning               |
| ---- | --------------------- |
| 200  | Success               |
| 201  | Created               |
| 400  | Validation Error      |
| 401  | Unauthorized          |
| 403  | Forbidden             |
| 404  | Not Found             |
| 409  | Conflict              |
| 429  | Too Many Requests     |
| 500  | Internal Server Error |

## Validation error format

All validation errors are returned in a single structure:

```json
{
  "success": false,
  "errors": [
    { "error_code": "EMAIL_REQUIRED", "field": "email", "message": "Email required" }
  ]
}
```

The API may return several validation errors at once:

```json
{
  "success": false,
  "errors": [
    { "error_code": "INVALID_EMAIL", "field": "email", "message": "Invalid email" },
    { "error_code": "PASSWORD_TOO_SHORT", "field": "password", "message": "Minimum 8 characters" }
  ]
}
```

---

## 1. Check Email — `POST api/check_email`

Checks the email's availability and the password's validity before registration. When the
check succeeds, the API automatically generates nickname suggestions.

**Auth**: none.

**Request**

```json
{ "email": "player@example.com", "password": "TestPass123" }
```

**Success — HTTP 200**

```json
{
  "success": true,
  "message": "Email and password are valid",
  "nicknames": ["PlayerNova", "PlayerPro", "PlayerX", "PlayerGG", "PlayerPlay"]
}
```

**Validation**: `EMAIL_REQUIRED` (email missing), `INVALID_EMAIL` (bad format),
`EMAIL_ALREADY_EXISTS` (already registered), `PASSWORD_REQUIRED`, `PASSWORD_TOO_SHORT`
(minimum 8 characters), `PASSWORD_NO_UPPERCASE` (at least one uppercase letter required),
`PASSWORD_NO_NUMBER` (at least one digit required), `PASSWORD_HAS_SPACES` (spaces
forbidden).

**Nickname generation**: base is the email's local part; unsupported characters are
stripped; uniqueness is verified before returning; a different variant is generated
automatically on conflict; maximum nickname length is 20; the same nickname generation
engine serves all nickname endpoints.

## 2. Check Nickname — `POST api/check_nickname`

Checks a nickname's availability.

**Auth**: none.

**Request**

```json
{ "nickname": "PlayerNova" }
```

**Success — HTTP 200**

```json
{ "success": true, "message": "Nickname is available", "nickname": "PlayerNova" }
```

**Nickname already exists — HTTP 409**

```json
{
  "success": false,
  "errors": [
    { "error_code": "NICKNAME_ALREADY_EXISTS", "field": "nickname", "message": "Nickname already exists" }
  ],
  "suggestions": ["PlayerNova25", "PlayerPro", "PlayerGG", "PlayerPlay", "PlayerX"]
}
```

**Validation rules**: minimum length 3; maximum length 20; Latin letters only; digits
allowed; underscore (`_`) allowed; spaces forbidden; the nickname must be unique.

## 3. Register — `POST api/register`

Creates a new customer account.

**Auth**: none.

**Request**

```json
{
  "email": "player@example.com",
  "password": "TestPass123",
  "nickname": "PlayerNova",
  "agree_terms": true,
  "agree_newsletter": false
}
```

| Field              | Type    | Required | Description             |
| ------------------ | ------- | -------- | ----------------------- |
| `email`            | string  | yes      | Customer email          |
| `password`         | string  | yes      | Customer password       |
| `nickname`         | string  | yes      | Unique nickname         |
| `agree_terms`      | boolean | yes      | Must be `true`          |
| `agree_newsletter` | boolean | no       | Subscribe to newsletter |

**Success — HTTP 201**

```json
{
  "success": true,
  "message": "Registration successful",
  "customer": {
    "customer_id": 15,
    "email": "player@example.com",
    "nickname": "PlayerNova",
    "newsletter": 0
  }
}
```

**Error codes**: `EMAIL_REQUIRED`, `INVALID_EMAIL`, `EMAIL_ALREADY_EXISTS`,
`PASSWORD_REQUIRED`, `PASSWORD_TOO_SHORT`, `PASSWORD_NO_UPPERCASE`, `PASSWORD_NO_NUMBER`,
`PASSWORD_HAS_SPACES`, `NICKNAME_REQUIRED`, `NICKNAME_TOO_SHORT`, `NICKNAME_TOO_LONG`,
`NICKNAME_HAS_SPACES`, `NICKNAME_INVALID_CHARACTERS`, `NICKNAME_ALREADY_EXISTS`,
`TERMS_REQUIRED`.

**Registration flow**

```
check_email → email valid → password valid → nickname suggestions generated →
user selects nickname → check_nickname → nickname available → register
```

## 4. Login — `POST api/login`

Authorizes a customer and returns a Session Token.

**Auth**: none.

**Request**

```json
{ "email": "player@example.com", "password": "TestPass123" }
```

**Success — HTTP 200**

```json
{
  "success": true,
  "message": "Login successful",
  "token": "USER_SESSION_TOKEN",
  "token_expires_in_days": 30,
  "customer": {
    "customer_id": 15,
    "email": "player@example.com",
    "nickname": "PlayerNova",
    "firstname": "John",
    "lastname": "Doe",
    "newsletter": 0
  }
}
```

**Errors**: `MISSING_CREDENTIALS` (email and password are required), `INVALID_CREDENTIALS`
(wrong email or password), `CUSTOMER_NOT_FOUND` (customer not found).

The Session Token must be sent with every protected request:

```
Authorization: Bearer USER_SESSION_TOKEN
```

Token lifetime: **30 days**.

## 5. Profile — `POST api/profile`

Returns the full customer profile.

**Auth**: Bearer Token. Body: `{}`.

**Success — HTTP 200**

```json
{
  "success": true,
  "customer": {
    "customer_id": 15,
    "email": "player@example.com",
    "nickname": "PlayerNova",
    "firstname": "John",
    "lastname": "Doe",
    "telephone": "+380501112233",
    "country": "Ukraine",
    "date_birth": "1995-10-15",
    "avatar": "https://proovegaming.com/image/profile/avatar_15_123456789.png",
    "newsletter": 0,
    "date_joined": "2026-06-09 13:33:03"
  }
}
```

**Authentication errors**: `TOKEN_REQUIRED`, `INVALID_OR_EXPIRED_TOKEN`.

Returned fields: `customer_id` (internal customer ID), `email`, `nickname` (public
nickname), `firstname`, `lastname`, `telephone`, `country`, `date_birth`, `avatar` (avatar
URL), `newsletter` (newsletter status), `date_joined` (registration date).

## 6. Update Profile — `POST api/profile/update`

Updates the customer profile information.

**Auth**: Bearer Token. Editable fields: `email`, `nickname`, `firstname`, `lastname`,
`telephone`, `country`, `date_birth`.

**Request**

```json
{
  "email": "player@example.com",
  "nickname": "PlayerNova",
  "firstname": "John",
  "lastname": "Doe",
  "telephone": "+380501112233",
  "country": "Ukraine",
  "date_birth": "1995-10-15"
}
```

**Success — HTTP 200**

```json
{ "success": true, "message": "Profile updated successfully" }
```

**Validation / conflict**: `INVALID_EMAIL`, `EMAIL_ALREADY_EXISTS`,
`NICKNAME_ALREADY_EXISTS`. During a profile update the nickname is validated by the same
rules as during registration; if the nickname belongs to another customer, the request is
rejected.

## 7. Upload Avatar — `POST api/profile/avatar`

Uploads or replaces the customer's profile avatar.

**Auth**: Bearer Token. Content-Type: `multipart/form-data`, field `avatar` = image file.
Supported file types: JPG, JPEG, PNG, WEBP. Maximum file size: 2 MB.

**Success — HTTP 200**

```json
{
  "success": true,
  "message": "Avatar uploaded successfully",
  "avatar": "https://proovegaming.com/image/profile/avatar_15_123456789.png"
}
```

**Upload process**: validates the Session Token; validates the uploaded image; deletes the
previous avatar if one exists; stores the new avatar under `image/profile/`; updates
`customer.avatar`; returns the new avatar URL.

**Error codes**: `TOKEN_REQUIRED`, `INVALID_OR_EXPIRED_TOKEN`, `AVATAR_REQUIRED`,
`UPLOAD_ERROR`, `FILE_TOO_LARGE`, `INVALID_FILE_TYPE`, `UPLOAD_FAILED`.

## 8. Notifications — `POST api/profile/notifications`

Returns the customer's notification settings.

**Auth**: Bearer Token. Body: `{}`.

**Success — HTTP 200**

```json
{ "success": true, "notifications": { "newsletter": 1 } }
```

`1 = enabled, 0 = disabled`. Errors: `TOKEN_REQUIRED`, `INVALID_OR_EXPIRED_TOKEN`.

## 9. Update Notifications — `POST api/profile/notifications_update`

Updates the customer's notification preferences.

**Auth**: Bearer Token.

**Request**

```json
{ "newsletter": 1 }
```

**Success — HTTP 200**

```json
{
  "success": true,
  "message": "Notification settings updated successfully",
  "notifications": { "newsletter": 1 }
}
```

Errors: `TOKEN_REQUIRED`, `INVALID_OR_EXPIRED_TOKEN`.

## 10. Check Nickname (Profile) — `POST api/profile/check_nickname`

Checks a nickname's availability while editing the profile. Unlike `api/check_nickname`,
this endpoint ignores the current customer's own nickname.

**Auth**: Bearer Token.

**Request**

```json
{ "nickname": "PlayerNova" }
```

**Success — HTTP 200**

```json
{ "success": true, "message": "Nickname is available", "nickname": "PlayerNova" }
```

**Validation rules**: minimum 3 characters; maximum 20 characters; Latin characters only;
digits allowed; underscore allowed; spaces are not allowed; the uniqueness check is
case-insensitive.

**Error codes**: `TOKEN_REQUIRED`, `INVALID_OR_EXPIRED_TOKEN`, `NICKNAME_REQUIRED`,
`NICKNAME_TOO_SHORT`, `NICKNAME_TOO_LONG`, `NICKNAME_HAS_SPACES`,
`NICKNAME_INVALID_CHARACTERS`, `NICKNAME_ALREADY_EXISTS`.

## 11. Password Change Link — `POST api/profile/password_link`

Returns a secure web link for changing the customer's password. The customer is identified
by the current Session Token.

**Auth**: Bearer Token. Body: `{}`.

**Success — HTTP 200**

```json
{
  "success": true,
  "password_change_url": "https://proovegaming.com/index.php?route=account/reset_password&code=RESET_CODE",
  "expires_in_minutes": 15
}
```

**Launcher flow**

```
App → user clicks "Change password" → app shows an email input dialog →
user enters the account email → app calls POST /api/forgotten →
API sends a password reset email → user opens the reset link from the email →
user sets the new password on the website → all Session Tokens become invalid →
app receives INVALID_OR_EXPIRED_TOKEN → app prompts the user to sign in again
```

## 12. Delete Account — `POST api/profile/delete`

Permanently deletes the customer account. The operation cannot be undone.

**Auth**: Bearer Token.

**Request**

```json
{ "password": "CurrentPassword123", "confirm": true }
```

**Success — HTTP 200**

```json
{ "success": true, "message": "Account deleted successfully" }
```

**Account deletion process**: validates the Session Token; validates the customer's
password; validates the deletion confirmation; deletes the customer avatar if one exists;
deletes all Session Tokens; deletes all `customer_presets`; deletes all `customer_macros`;
deletes `customer_preferences`; deletes `customer_device_state`; permanently deletes the
customer account. After deletion, login is no longer possible, all Session Tokens become
invalid, and the account cannot be restored.

**Error codes**: `TOKEN_REQUIRED`, `INVALID_OR_EXPIRED_TOKEN`, `PASSWORD_REQUIRED`,
`INVALID_PASSWORD`, `CONFIRMATION_REQUIRED`, `ACCOUNT_DELETE_FAILED`.

## 13. Logout — `POST api/logout`

Invalidates the current Session Token.

**Auth**: Bearer Token. Body: `{}`.

**Success — HTTP 200**

```json
{ "success": true, "message": "Logout successful" }
```

Errors: `TOKEN_REQUIRED`, `INVALID_OR_EXPIRED_TOKEN`.

## 14. Forgotten Password — `POST api/forgotten`

Sends a password reset email.

**Auth**: none.

**Request**

```json
{ "email": "user@example.com" }
```

**Success — HTTP 200**

```json
{ "success": true, "message": "Password reset email sent" }
```

**Password recovery process**: validates the email; generates a Reset Code; stores the
Reset Code; stores the expiration time; sends the password reset email. Reset Code
lifetime: 15 minutes.

```
https://proovegaming.com/index.php?route=account/reset_password&code=RESET_CODE
```

**Error codes**: `EMAIL_REQUIRED`, `INVALID_EMAIL`, `EMAIL_NOT_FOUND`, `MAIL_ERROR`.

## 15. Reset Password — `POST api/reset_password`

Changes the password using a Reset Code.

**Auth**: none.

**Request**

```json
{ "code": "RESET_CODE", "new_password": "NewPass123" }
```

**Success — HTTP 200**

```json
{
  "success": true,
  "message": "Password has been reset successfully",
  "customer": { "customer_id": 15, "email": "user@example.com" }
}
```

**Password reset process**: updates the customer's password; deletes the Reset Code;
deletes the Reset Code expiration; deletes all Session Tokens; the customer must log in
again.

**Error codes**: `RESET_CODE_REQUIRED`, `PASSWORD_REQUIRED`, `PASSWORD_TOO_SHORT`,
`PASSWORD_NO_UPPERCASE`, `PASSWORD_NO_NUMBER`, `PASSWORD_HAS_SPACES`,
`INVALID_OR_EXPIRED_RESET_CODE`.

## 16. Presets

Stores and synchronizes device presets across Launcher installations. All endpoints
require a Bearer Token.

### 16.1 Create Preset — `POST api/presets/create`

> **`configuration` is opaque** (verified 2026-09-10 against the live server): a preset was
> created with extra sections, read back with `presets/get` and `presets/list`, and both reads
> echoed the object byte-for-byte. The server validates only that the field is present
> (`CONFIGURATION_REQUIRED`), so the Launcher's own extensions ride along safely —
> `key_macro_bindings` (per-key macro references) and `extended_keys` (Rapid Snap / SOCD /
> Mod-Tap / Toggle records), neither of which appears in the example below.

**Request**

```json
{
  "uid": "11111111-1111-4111-8111-111111111111",
  "vendor_id": 1241,
  "product_id": 41541,
  "name": "FPS",
  "description": "Low DPI, red lighting",
  "is_default": true,
  "configuration": {
    "rgb": {
      "effects": [],
      "is_enabled": true,
      "selected_effect": "breathing"
    },
    "settings": {
      "pollingRate.hz": 1000,
      "dpi.activeStage": 1
    },
    "actuation": [],
    "key_bindings": [],
    "button_bindings": []
  }
}
```

**Success — HTTP 201**

```json
{
  "success": true,
  "message": "Preset created successfully",
  "preset": {
    "uid": "11111111-1111-4111-8111-111111111111",
    "vendor_id": 1241,
    "product_id": 41541,
    "name": "FPS",
    "is_default": true,
    "created_at": "2026-08-18 13:39:47",
    "updated_at": "2026-08-18 13:39:47"
  }
}
```

**Error codes**: `PRESET_UID_ALREADY_EXISTS`, `UID_REQUIRED`, `VENDOR_ID_REQUIRED`,
`PRODUCT_ID_REQUIRED`, `NAME_REQUIRED`, `CONFIGURATION_REQUIRED`.

### 16.2 List Presets — `POST api/presets/list`

**Request**

```json
{ "vendor_id": 1241, "product_id": 41541 }
```

`vendor_id`/`product_id` may be used as a filter. If there are no matching presets,
`presets: []` is returned.

### 16.3 Get Preset — `POST api/presets/get`

```json
{ "uid": "11111111-1111-4111-8111-111111111111" }
```

**Error codes**: `PRESET_NOT_FOUND`, `INVALID_UID`.

### 16.4 Update Preset — `POST api/presets/update`

Updates must send the current `updated_at`. If the server record has already changed, the
API returns `PRESET_CONFLICT` together with the current server version.

**Error codes**: `PRESET_CONFLICT`, `PRESET_NOT_FOUND`, `INVALID_UID`.

### 16.5 Delete Preset — `POST api/presets/delete`

The default preset cannot be deleted.

**Error codes**: `DEFAULT_PRESET_CANNOT_BE_DELETED`, `PRESET_CONFLICT`,
`PRESET_NOT_FOUND`, `INVALID_UID`.

The Launcher governs which preset is the default; the API must not automatically assign a
different default preset.

## 17. Macros

Stores and synchronizes device macros. All endpoints require a Bearer Token.

### 17.1 Create Macro — `POST api/macros/create`

```json
{
  "uid": "33333333-3333-4333-8333-333333333333",
  "vendor_id": 1241,
  "product_id": 41541,
  "name": "Reload burst",
  "repeat_count": 1,
  "repeat_mode": 0,
  "events": [
    { "delay_ms": 20, "is_press": true, "key_code": 15 },
    { "delay_ms": 50, "is_press": false, "key_code": 15 }
  ]
}
```

**Error codes**: `MACRO_UID_ALREADY_EXISTS`, `UID_REQUIRED`, `VENDOR_ID_REQUIRED`,
`PRODUCT_ID_REQUIRED`, `NAME_REQUIRED`, `INVALID_REPEAT_COUNT`, `INVALID_REPEAT_MODE`,
`EVENTS_REQUIRED`.

### 17.2 List Macros — `POST api/macros/list`

### 17.3 Get Macro — `POST api/macros/get`

**Error codes**: `MACRO_NOT_FOUND`, `INVALID_UID`.

### 17.4 Update Macro — `POST api/macros/update`

`updated_at` is used for concurrent-change handling.

**Error codes**: `MACRO_CONFLICT`, `MACRO_NOT_FOUND`, `INVALID_UID`.

### 17.5 Delete Macro — `POST api/macros/delete`

Delete also uses the current `updated_at` for conflict protection.

## 18. Preferences

Stores the user's general preferences.

### 18.1 Get Preferences — `POST api/preferences/get`

```json
{
  "success": true,
  "preferences": [],
  "created_at": null,
  "updated_at": null
}
```

If preferences have not been created yet, the API returns an empty array and null
timestamps.

### 18.2 Update Preferences — `POST api/preferences/update`

```json
{
  "preferences": {
    "SleepTimer": 5,
    "ScrollLines": 3,
    "DoubleClickSpeed": 500,
    "GenericLiftOffDistance": 1,
    "ScrollOneScreenAtATime": false
  }
}
```

After the first save the API returns `created_at` and `updated_at`. Subsequent updates
must send the current `updated_at`.

**Error codes**: `PREFERENCES_CONFLICT`.

## 19. Device State

Stores the active preset for a specific `vendor_id`/`product_id`.

### 19.1 Get Device State — `POST api/devices/get`

```json
{ "vendor_id": 1241, "product_id": 41541 }
```

If the state has not been created yet, `active_preset_uid`, `created_at` and `updated_at`
may be null.

### 19.2 Update Device State — `POST api/devices/update`

```json
{
  "vendor_id": 1241,
  "product_id": 41541,
  "active_preset_uid": "11111111-1111-4111-8111-111111111111",
  "updated_at": "2026-08-19 16:08:03"
}
```

`active_preset_uid` may be null to clear the active preset.

**Error codes**: `ACTIVE_PRESET_NOT_FOUND`, `DEVICE_STATE_CONFLICT`.

## 20. Contact Us — `POST api/contact`

Public endpoint for sending messages to the support service. No Bearer Token required.

**Request**

```json
{
  "email": "user@example.com",
  "topic": "Device problem",
  "message": "My device settings are not being saved correctly."
}
```

**Validation**: `email` — required, must be a valid email; `topic` — required, 3–100
characters; `message` — required, 10–2000 characters.

**Rate limits**: no more than 1 successful message per 60 seconds per email or IP; no more
than 5 successful messages per 24 hours per email or IP; identical `topic` + `message`
pairs are blocked as duplicates for 10 minutes.

**Success — HTTP 200**

```json
{ "success": true, "message": "Message sent successfully" }
```

**Error codes**: `INVALID_JSON`, `EMAIL_REQUIRED`, `INVALID_EMAIL`, `TOPIC_REQUIRED`,
`TOPIC_TOO_SHORT`, `TOPIC_TOO_LONG`, `MESSAGE_REQUIRED`, `MESSAGE_TOO_SHORT`,
`MESSAGE_TOO_LONG`, `RATE_LIMIT_EXCEEDED`, `DAILY_LIMIT_EXCEEDED`, `DUPLICATE_MESSAGE`,
`MAIL_ERROR`.

After successful validation the API sends an email to the support service. The email
includes the user's email, IP, topic and message.

## Security notes

- **Session Token** — generated by `POST api/login`, lifetime 30 days, sent as a Bearer
  Token. Expired/invalid tokens get no access to protected endpoints.
- **Password security** — after a successful password change all Session Tokens are
  deleted and the customer must log in again.
- **Optimistic conflict handling** — Presets, Macros, Preferences and Device State use
  `updated_at`. If a client sends a stale version, the API returns a conflict error along
  with the current server version.
- **Contact Us anti-spam** — Contact Us has a cooldown, a daily limit and duplicate
  protection.

## API summary

| Endpoint                              | Authentication |
| ------------------------------------- | -------------- |
| `POST api/check_email`                | No             |
| `POST api/check_nickname`             | No             |
| `POST api/register`                   | No             |
| `POST api/login`                      | No             |
| `POST api/profile`                    | Bearer Token   |
| `POST api/profile/update`             | Bearer Token   |
| `POST api/profile/avatar`             | Bearer Token   |
| `POST api/profile/notifications`      | Bearer Token   |
| `POST api/profile/notifications_update` | Bearer Token |
| `POST api/profile/check_nickname`     | Bearer Token   |
| `POST api/profile/password_link`      | Bearer Token   |
| `POST api/profile/delete`             | Bearer Token   |
| `POST api/logout`                     | Bearer Token   |
| `POST api/forgotten`                  | No             |
| `POST api/reset_password`             | No             |
| `POST api/presets/create`             | Bearer Token   |
| `POST api/presets/list`               | Bearer Token   |
| `POST api/presets/get`                | Bearer Token   |
| `POST api/presets/update`             | Bearer Token   |
| `POST api/presets/delete`             | Bearer Token   |
| `POST api/macros/create`              | Bearer Token   |
| `POST api/macros/list`                | Bearer Token   |
| `POST api/macros/get`                 | Bearer Token   |
| `POST api/macros/update`              | Bearer Token   |
| `POST api/macros/delete`              | Bearer Token   |
| `POST api/preferences/get`            | Bearer Token   |
| `POST api/preferences/update`         | Bearer Token   |
| `POST api/devices/get`                | Bearer Token   |
| `POST api/devices/update`             | Bearer Token   |
| `POST api/contact`                    | No             |

## Changelog (from doc)

- **v1.0** — Registration API; Login API.
- **v1.1** — Forgotten Password; Reset Password.
- **v1.2** — Profile API; Logout API.
- **v1.3** — Profile Update; Avatar Upload.
- **v1.4** — Notifications API; Notification Settings Update; Profile Nickname Validation.
- **v1.5** — Password Change Link; Account Deletion; Email Update; Nickname Update;
  Extended Profile API.
- **v1.6** — Unified Nickname Generator; Automatic Nickname Suggestions; Nickname
  Suggestions in Registration; Nickname Suggestions in Profile; Automatic Nickname
  Generation after Email Validation; Improved Registration Flow; Extended Security Notes.
- **v2.0** — Presets API; Macros API; Preferences API; Device State API; `updated_at`
  conflict handling; Contact Us API; Contact Us validation / rate limit / duplicate
  protection / email delivery.
