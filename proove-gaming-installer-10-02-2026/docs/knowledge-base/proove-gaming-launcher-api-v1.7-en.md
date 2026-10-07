# Proove Gaming Launcher API — v1.7

Clean Markdown of `Proove Gaming Launcher API v1.7.docx` (source is already in English). Raw text extract kept alongside as `proove-gaming-launcher-api-v1.7-raw.txt`.

Diff vs v1.6: v1.7 is v1.6 rewritten natively in English. No new endpoints, no new request/response fields. The only substantive edit is the flow diagram under §11 (Password Change Link), which now shows the launcher collecting an email and calling `POST /api/forgotten` (rather than opening the reset page directly). No changelog entry — the v1.6 → v1.7 delta is documentation-quality only.

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

- All requests over HTTPS.
- Request body: `application/json`
- Response: `application/json`

## Authentication

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

## HTTP status codes

| Code | Meaning              |
| ---- | -------------------- |
| 200  | Success              |
| 201  | Created              |
| 400  | Validation error     |
| 401  | Unauthorized         |
| 403  | Forbidden            |
| 404  | Not found            |
| 409  | Conflict             |
| 500  | Internal server error|

## Error response format

All validation errors share this structure:

```json
{
  "success": false,
  "errors": [
    { "error_code": "EMAIL_REQUIRED", "field": "email", "message": "Email required" }
  ]
}
```

Some endpoints return a single top-level `error_code` (login, avatar upload, forgotten, reset, delete, notifications).

---

## 1. Check Email — `POST api/check_email`

Validates email + password before registration; on success returns up to 5 server-generated nickname suggestions.

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

**Error codes**: `EMAIL_REQUIRED` (400), `INVALID_EMAIL` (400), `EMAIL_ALREADY_EXISTS` (409), `PASSWORD_REQUIRED` (400), `PASSWORD_TOO_SHORT` (400), `PASSWORD_NO_UPPERCASE` (400), `PASSWORD_NO_NUMBER` (400), `PASSWORD_HAS_SPACES` (400).

### Nickname generation rules

- Base: local part of the email address.
- Unsupported characters are stripped.
- Uniqueness verified before returning.
- Regenerates on conflict.
- Max nickname length: 20.
- Same generator used by all nickname-related endpoints.

## 2. Check Nickname — `POST api/check_nickname`

**Auth**: none.

**Request**

```json
{ "nickname": "PlayerNova" }
```

**Success — HTTP 200**

```json
{ "success": true, "message": "Nickname is available", "nickname": "PlayerNova" }
```

**Conflict — HTTP 409** (up to 5 alternatives)

```json
{
  "success": false,
  "errors": [
    { "error_code": "NICKNAME_ALREADY_EXISTS", "field": "nickname", "message": "Nickname already exists" }
  ],
  "suggestions": ["PlayerNova25", "PlayerPro", "PlayerGG", "PlayerPlay", "PlayerX"]
}
```

**Validation rules**: min 3, max 20, Latin letters, digits, underscore allowed; no spaces; unique.

## 3. Register — `POST api/register`

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

| Field              | Type    | Required | Description                     |
| ------------------ | ------- | -------- | ------------------------------- |
| `email`            | string  | yes      | Customer email                  |
| `password`         | string  | yes      | Customer password               |
| `nickname`         | string  | yes      | Unique nickname                 |
| `agree_terms`      | boolean | yes      | Must be `true`                  |
| `agree_newsletter` | boolean | no       | Newsletter opt-in               |

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

**Error codes**: everything from check_email/check_nickname + `TERMS_REQUIRED` (400).

**Registration flow**

```
check_email → email valid → password valid → nickname suggestions generated →
user picks nickname → check_nickname → nickname available → register
```

## 4. Login — `POST api/login`

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

**Errors** (single top-level `error_code`): `MISSING_CREDENTIALS` (400), `INVALID_CREDENTIALS` (401), `CUSTOMER_NOT_FOUND`.

Token lifetime: **30 days**.

## 5. Profile — `POST api/profile`

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

Errors: `TOKEN_REQUIRED` (401), `INVALID_OR_EXPIRED_TOKEN` (401).

## 6. Update Profile — `POST api/profile/update`

**Auth**: Bearer Token. Editable fields: `email`, `nickname`, `firstname`, `lastname`, `telephone`, `country`, `date_birth`.

**Success — HTTP 200**

```json
{ "success": true, "message": "Profile updated successfully" }
```

Errors: standard field validation codes + `EMAIL_ALREADY_EXISTS` (409). Nickname uses the same rules as registration.

## 7. Upload Avatar — `POST api/profile/avatar`

**Auth**: Bearer Token. `multipart/form-data` field `avatar`. Allowed: JPG/JPEG/PNG/WEBP. Max: 2 MB.

**Success — HTTP 200**

```json
{
  "success": true,
  "message": "Avatar uploaded successfully",
  "avatar": "https://proovegaming.com/image/profile/avatar_15_123456789.png"
}
```

Errors: `AVATAR_REQUIRED` (400), `INVALID_FILE_TYPE` (400), `FILE_TOO_LARGE` (400), `UPLOAD_FAILED` (500), `UPLOAD_ERROR`, plus token errors.

## 8. Notifications — `POST api/profile/notifications`

**Auth**: Bearer Token. Body: `{}`.

```json
{ "success": true, "notifications": { "newsletter": 1 } }
```

`1 = enabled, 0 = disabled`.

## 9. Update Notifications — `POST api/profile/notifications_update`

**Auth**: Bearer Token.

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

## 10. Check Nickname (Profile) — `POST api/profile/check_nickname`

Validates a nickname during profile edit. Ignores the current customer's own nickname (unlike `api/check_nickname`).

**Auth**: Bearer Token.

**Request**

```json
{ "nickname": "PlayerNova" }
```

**Success — HTTP 200**

```json
{ "success": true, "message": "Nickname is available", "nickname": "PlayerNova" }
```

**Conflict — HTTP 409** (returns up to 5 alternatives)

```json
{
  "success": false,
  "errors": [
    { "error_code": "NICKNAME_ALREADY_EXISTS", "field": "nickname", "message": "Nickname already exists" }
  ],
  "suggestions": ["PlayerNova55", "PlayerPro", "PlayerGG", "PlayerPlay", "PlayerX"]
}
```

Validation rules identical to registration; uniqueness check is case-insensitive. Same nickname generator as `api/check_email` and `api/check_nickname`.

## 11. Password Change Link — `POST api/profile/password_link`

**Auth**: Bearer Token. Body: `{}`. Customer is identified via the current Session Token (no email input).

**Success — HTTP 200**

```json
{
  "success": true,
  "password_change_url": "https://proovegaming.com/index.php?route=account/reset_password&code=RESET_CODE",
  "expires_in_minutes": 15
}
```

**Flow (v1.7 revised)**

```
Launcher → user clicks "Change password" → launcher shows an email input dialog →
user enters the account email → launcher calls POST /api/forgotten →
API sends reset email → user opens reset link → user sets new password on the site →
all Session Tokens invalidated → on next protected call launcher receives
INVALID_OR_EXPIRED_TOKEN → launcher prompts sign-in again
```

## 12. Delete Account — `POST api/profile/delete`

**Auth**: Bearer Token. Permanently deletes the customer.

**Request**

```json
{ "password": "CurrentPassword123", "confirm": true }
```

**Success — HTTP 200**

```json
{ "success": true, "message": "Account deleted successfully" }
```

Errors: `CONFIRMATION_REQUIRED` (400), `PASSWORD_REQUIRED` (400), `INVALID_PASSWORD` (401), `ACCOUNT_DELETE_FAILED`, plus token errors.

## 13. Logout — `POST api/logout`

**Auth**: Bearer Token. Body: `{}`.

```json
{ "success": true, "message": "Logout successful" }
```

## 14. Forgotten Password — `POST api/forgotten`

**Auth**: none.

```json
{ "email": "user@example.com" }
```

**Success — HTTP 200**

```json
{ "success": true, "message": "Password reset email sent" }
```

Reset Code lifetime: 15 minutes. Reset URL:

```
https://proovegaming.com/index.php?route=account/reset_password&code=RESET_CODE
```

Errors: `EMAIL_REQUIRED`, `INVALID_EMAIL` (400), `EMAIL_NOT_FOUND` (404), `MAIL_ERROR` (500).

## 15. Reset Password — `POST api/reset_password`

**Auth**: none.

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

After success: password updated, Reset Code cleared, all Session Tokens invalidated — customer must sign in again.

Errors: `RESET_CODE_REQUIRED`, `PASSWORD_REQUIRED`, `PASSWORD_TOO_SHORT`, `PASSWORD_NO_UPPERCASE`, `PASSWORD_NO_NUMBER`, `PASSWORD_HAS_SPACES`, `INVALID_OR_EXPIRED_RESET_CODE` (400).

---

## Security notes

- Session Tokens generated by `POST api/login`, stored server-side as `customer_token`, expire after 30 days; expired tokens removed automatically.
- Unified Nickname Generator used by `check_email`, `check_nickname`, `profile/check_nickname` — guarantees consistent suggestions across the platform.
- Avatar files in `image/profile/`; old files auto-deleted on new upload.
- Password change happens on the **website**, not the launcher. Successful reset invalidates all Session Tokens.
- Account deletion is permanent.

## Changelog (from doc)

Changelog only lists v1.0 – v1.6 items (v1.7 has no dedicated entry — see the note at the top of this file).
