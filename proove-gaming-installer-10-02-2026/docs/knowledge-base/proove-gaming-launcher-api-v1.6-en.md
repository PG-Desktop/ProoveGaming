# Proove Gaming Launcher API — v1.6

English translation of `Proove Gaming Launcher API v1.6.docx` (source in Ukrainian). Raw text extract kept alongside as `proove-gaming-launcher-api-v1.6-raw.txt`.

## Document info

| Property           | Value                          |
| ------------------ | ------------------------------ |
| Project            | Proove Gaming Launcher         |
| API version        | 1.6                            |
| Protocol           | HTTPS                          |
| Format             | JSON                           |
| Authentication     | Bearer Token                   |
| Encoding           | UTF-8                          |

## Base URL

```
https://proovegaming.com/index.php?route=
```

- All requests over HTTPS.
- Request body: `application/json`
- Response: `application/json`

## Authentication

Protected endpoints require a Bearer Token.

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

| Code | Meaning                |
| ---- | ---------------------- |
| 200  | Success                |
| 201  | Created                |
| 400  | Validation error       |
| 401  | Unauthorized           |
| 403  | Forbidden              |
| 404  | Not found              |
| 409  | Conflict               |
| 500  | Internal server error  |

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

Multiple errors:

```json
{
  "success": false,
  "errors": [
    { "error_code": "INVALID_EMAIL", "field": "email", "message": "Invalid email" },
    { "error_code": "PASSWORD_TOO_SHORT", "field": "password", "message": "Minimum 8 characters" }
  ]
}
```

Some endpoints (login, avatar upload, forgotten password, reset password, delete account, notifications) return a single top-level error:

```json
{ "success": false, "error_code": "…", "message": "…" }
```

---

## 1. Check Email — `POST api/check_email`

Validates email availability and password strength before registration. On success, API generates up to 5 nickname suggestions.

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
- Uniqueness is verified before returning suggestions.
- On conflict, another nickname is generated automatically.
- Max nickname length: 20 characters.
- Same generator used everywhere nicknames are generated.

## 2. Check Nickname — `POST api/check_nickname`

Checks nickname availability.

**Auth**: none.

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
  "suggestions": ["PlayerNova25", "PlayerPro", "PlayerGG", "PlayerPlay", "PlayerX"]
}
```

**Validation rules**

- Min length: 3.
- Max length: 20.
- Latin letters only.
- Digits allowed.
- Underscore `_` allowed.
- No spaces.
- Must be unique.

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

| Field              | Type    | Required | Description                     |
| ------------------ | ------- | -------- | ------------------------------- |
| `email`            | string  | yes      | Customer email                  |
| `password`         | string  | yes      | Customer password               |
| `nickname`         | string  | yes      | Unique nickname                 |
| `agree_terms`      | boolean | yes      | Must be `true`                  |
| `agree_newsletter` | boolean | no       | Newsletter subscription opt-in  |

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

**Error codes**: all check_email/check_nickname codes, plus `TERMS_REQUIRED` (400).

**Registration flow**

```
check_email → email valid → password valid → nickname suggestions generated →
user picks nickname → check_nickname → nickname available → register
```

## 4. Login — `POST api/login`

Authenticates the customer and returns a Session Token.

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

**Errors** (single top-level `error_code`)

- `MISSING_CREDENTIALS` — HTTP 400
- `INVALID_CREDENTIALS` — HTTP 401
- `CUSTOMER_NOT_FOUND`

Token must be sent on every protected request. Lifetime: **30 days**.

## 5. Profile — `POST api/profile`

Returns the customer's full profile.

**Auth**: Bearer Token.

**Request**: `{}`

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

**Errors**: `TOKEN_REQUIRED` (401), `INVALID_OR_EXPIRED_TOKEN` (401).

## 6. Update Profile — `POST api/profile/update`

Updates customer profile fields.

**Auth**: Bearer Token.

**Editable fields**: `email`, `nickname`, `firstname`, `lastname`, `telephone`, `country`, `date_birth`.

**Success — HTTP 200**

```json
{ "success": true, "message": "Profile updated successfully" }
```

Errors: standard email/nickname validation codes (400), `EMAIL_ALREADY_EXISTS` (409). Nickname validated with same rules as registration; if it belongs to another customer the request is rejected.

## 7. Upload Avatar — `POST api/profile/avatar`

Uploads or replaces the profile avatar.

**Auth**: Bearer Token. `Content-Type: multipart/form-data`, field name `avatar`.

**Allowed types**: JPG, JPEG, PNG, WEBP. **Max size**: 2 MB.

**Success — HTTP 200**

```json
{
  "success": true,
  "message": "Avatar uploaded successfully",
  "avatar": "https://proovegaming.com/image/profile/avatar_15_123456789.png"
}
```

Server side: validates token → validates image → deletes previous avatar (if any) → saves under `image/profile/` → updates `customer.avatar` → returns the new URL.

**Errors**: `AVATAR_REQUIRED` (400), `INVALID_FILE_TYPE` (400), `FILE_TOO_LARGE` (400), `UPLOAD_FAILED` (500), `UPLOAD_ERROR`, `TOKEN_REQUIRED`, `INVALID_OR_EXPIRED_TOKEN`.

## 8. Notifications — `POST api/profile/notifications`

Returns notification settings.

**Auth**: Bearer Token.

**Request**: `{}`

**Success — HTTP 200**

```json
{ "success": true, "notifications": { "newsletter": 1 } }
```

`1 = enabled, 0 = disabled`. Errors: `TOKEN_REQUIRED`, `INVALID_OR_EXPIRED_TOKEN`.

## 9. Update Notifications — `POST api/profile/notifications_update`

Updates notification settings.

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

Validates a nickname during profile edit. Unlike `api/check_nickname`, this endpoint ignores the current customer's own nickname.

**Auth**: Bearer Token.

**Request**

```json
{ "nickname": "PlayerNova" }
```

Success and conflict shapes match `api/check_nickname` (409 returns `suggestions[]`). Validation rules identical to registration; uniqueness check is case-insensitive.

## 11. Password Change Link — `POST api/profile/password_link`

Returns a secure web link to change the password (customer identified by the current Session Token — no email input).

**Auth**: Bearer Token.

**Request**: `{}`

**Success — HTTP 200**

```json
{
  "success": true,
  "password_change_url": "https://proovegaming.com/index.php?route=account/reset_password&code=RESET_CODE",
  "expires_in_minutes": 15
}
```

**Flow**

```
Launcher → user clicks "Change password" → launcher opens the reset page on the site →
customer enters account email → site sends reset email → customer opens reset link →
customer sets new password on the site → all Session Tokens invalidated →
customer signs into launcher again
```

## 12. Delete Account — `POST api/profile/delete`

Permanently deletes the customer account. **Cannot be undone.**

**Auth**: Bearer Token.

**Request**

```json
{ "password": "CurrentPassword123", "confirm": true }
```

| Field      | Type    | Required | Description               |
| ---------- | ------- | -------- | ------------------------- |
| `password` | string  | yes      | Current customer password |
| `confirm`  | boolean | yes      | Must be `true`            |

**Success — HTTP 200**

```json
{ "success": true, "message": "Account deleted successfully" }
```

Server side: validates token → validates password → validates confirmation → deletes avatar → invalidates all Session Tokens → permanently deletes the customer record.

**Errors**: `CONFIRMATION_REQUIRED` (400), `PASSWORD_REQUIRED` (400), `INVALID_PASSWORD` (401), `ACCOUNT_DELETE_FAILED`, `TOKEN_REQUIRED`, `INVALID_OR_EXPIRED_TOKEN`.

## 13. Logout — `POST api/logout`

Invalidates the current Session Token.

**Auth**: Bearer Token. Request: `{}`.

**Success — HTTP 200**

```json
{ "success": true, "message": "Logout successful" }
```

Errors: `TOKEN_REQUIRED`, `INVALID_OR_EXPIRED_TOKEN`.

## 14. Forgotten Password — `POST api/forgotten`

Requests a password reset email.

**Auth**: none.

**Request**

```json
{ "email": "user@example.com" }
```

**Success — HTTP 200**

```json
{ "success": true, "message": "Password reset email sent" }
```

Server validates email → generates and stores a Reset Code → stores expiry → sends the reset email.

**Reset Code lifetime**: 15 minutes.

Reset URL:

```
https://proovegaming.com/index.php?route=account/reset_password&code=RESET_CODE
```

**Errors**: `EMAIL_REQUIRED`, `INVALID_EMAIL` (400), `EMAIL_NOT_FOUND` (404), `MAIL_ERROR` (500).

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

After a successful reset the API updates the password, deletes the Reset Code and its expiry, and invalidates all active Session Tokens — the customer must sign in again.

**Errors**: `RESET_CODE_REQUIRED`, `PASSWORD_REQUIRED`, `PASSWORD_TOO_SHORT`, `PASSWORD_NO_UPPERCASE`, `PASSWORD_NO_NUMBER`, `PASSWORD_HAS_SPACES`, `INVALID_OR_EXPIRED_RESET_CODE` (400).

---

## Security notes

- Session Tokens are generated by `POST api/login`, stored server-side as `customer_token`, expire after 30 days, and are cleaned up automatically once expired.
- The **nickname generator** is a single shared component used by `check_email`, `check_nickname`, and `profile/check_nickname` — guaranteeing consistent suggestions across the platform.
- Avatar files live in `image/profile/`; the previous file is automatically deleted on new upload.
- Password change is completed on the **website**, not the launcher: launcher only opens the reset page, and successful reset invalidates all Session Tokens (customer must re-login).
- Account deletion is permanent: avatar removed, tokens invalidated, customer record deleted with no recovery.

## API summary

| Endpoint                                | Auth         |
| --------------------------------------- | ------------ |
| `POST api/check_email`                  | no           |
| `POST api/check_nickname`               | no           |
| `POST api/register`                     | no           |
| `POST api/login`                        | no           |
| `POST api/profile`                      | Bearer Token |
| `POST api/profile/update`               | Bearer Token |
| `POST api/profile/avatar`               | Bearer Token |
| `POST api/profile/notifications`        | Bearer Token |
| `POST api/profile/notifications_update` | Bearer Token |
| `POST api/profile/check_nickname`       | Bearer Token |
| `POST api/profile/password_link`        | Bearer Token |
| `POST api/profile/delete`               | Bearer Token |
| `POST api/logout`                       | Bearer Token |
| `POST api/forgotten`                    | no           |
| `POST api/reset_password`               | no           |

## Change history

- **v1.0** — registration, login.
- **v1.1** — forgotten password, reset password.
- **v1.2** — profile, logout.
- **v1.3** — update profile, upload avatar.
- **v1.4** — notifications API, update notification settings, profile nickname validation.
- **v1.5** — password change link, delete account, update email, update nickname, extended profile API.
- **v1.6** — unified nickname generator, automatic suggestions in register/profile, generation after email validation, improved registration flow, extended security notes.
