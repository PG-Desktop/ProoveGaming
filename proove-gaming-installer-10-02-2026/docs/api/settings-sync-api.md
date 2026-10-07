# Storing user presets and settings on the account — request to the backend team

Status: **idea / request for feedback.** Nothing here exists on the server yet, and nothing below is
final — we want your take on the shape before either side builds anything.

Related: [launcher API v1.7 spec](../knowledge-base/proove-gaming-launcher-api-v1.7-en.md).

## What we want

The desktop app lets a user configure their devices — lighting, DPI, button and key remaps, macros,
key actuation, audio levels — and save named **presets**. Today all of it lives only on the PC it was
made on.

We'd like it stored against the customer account, so that:

1. Someone who uses the app **without an account** and then registers or signs in keeps everything
   they configured.
2. Someone who signs in **on another PC** gets their presets back, and once their devices are
   plugged in the app applies them automatically — from the user's point of view, nothing changed.

## The model

The thing worth storing is a **preset**: a named configuration belonging to one customer and one
device model. A user typically has a handful per device ("FPS", "Work", "Quiet").

A preset record would carry ordinary columns for the parts you'd want to query or index —

| field | note |
| --- | --- |
| `id` | server-assigned |
| `uid` | client-generated GUID; the app creates presets offline, so it needs to name them before you see them |
| `customer_id` | from the bearer token, never from the request body |
| `vendor_id` / `product_id` | which device model this preset is for (decimal) |
| `name`, `description` | user-facing |
| `is_default` | exactly one per customer + device model |
| `created_at` / `updated_at` | |

— plus the actual device configuration as a **JSON field**:

```json
{
  "rgb": { "is_enabled": true, "selected_effect": "breathing", "effects": [] },
  "settings": { "dpi.activeStage": 1, "pollingRate.hz": 1000 },
  "button_bindings": [],
  "key_bindings": [],
  "actuation": []
}
```

That interior keeps changing as we add devices — two new setting types landed this year alone — and
none of it is ever queried server-side. So our preference is that you **store it opaquely** and don't
model it: adding a device capability then costs a client release instead of a backend migration. The
fields in the table above are the ones we'd expect to be real columns.

Two related things need storing alongside presets:

- **Macros** — recorded input sequences, scoped the same way (customer + device model), referenced by
  presets by `uid`. Same treatment: a few real columns, event list as JSON.
- **App preferences** — a handful of values not tied to any device (scroll behaviour, double-click
  speed). One record per customer.
- **Which preset is active** for a given device model, so a second PC restores the right one rather
  than leaving the hardware on whatever it was last set to.

Note presets belong to a device **model**, not to a physical unit — two identical mice share one
preset list. We would not send any per-machine hardware identifier: it differs per PC and per USB
port, so it is useless to sync and we'd rather not have it leave the machine.

## Endpoints

REST, using methods rather than verbs in the path:

| method | path | purpose |
| --- | --- | --- |
| `GET` | `/api/presets` | all presets for the customer; `?vendor_id=&product_id=` to filter to one device |
| `POST` | `/api/presets` | create |
| `GET` | `/api/presets/{uid}` | one preset |
| `PUT` | `/api/presets/{uid}` | update (name, default flag, configuration) |
| `DELETE` | `/api/presets/{uid}` | delete |
| `GET` `POST` `PUT` `DELETE` | `/api/macros[/{uid}]` | same shape as presets |
| `GET` `PUT` | `/api/preferences` | the app-level preferences record |
| `GET` `PUT` | `/api/devices/{vendor_id}-{product_id}` | per-device state, currently just which preset is active |

### Bodies

What we'd send and expect back. Responses keep the existing envelope (`success` / `message` /
`error_code` / `errors`) so error handling stays identical to the rest of the app — say if you'd
rather return bare resources for a REST-style API and we'll adapt.

**`POST /api/presets`** — create. `uid`, `vendor_id` and `product_id` are set once here and never
change afterwards.

```json
{
  "uid": "1f4c8b7e-6a21-4f0e-9c33-2b5d8e7a1c04",
  "vendor_id": 1241,
  "product_id": 41541,
  "name": "FPS",
  "description": "Low DPI, red lighting",
  "is_default": true,
  "configuration": {
    "rgb": { "is_enabled": true, "selected_effect": "breathing", "effects": [] },
    "settings": { "dpi.activeStage": 1, "pollingRate.hz": 1000 },
    "button_bindings": [],
    "key_bindings": [],
    "actuation": []
  }
}
```

Response — the stored record, so we pick up `id` and the server timestamps:

```json
{
  "success": true,
  "preset": {
    "id": 812,
    "uid": "1f4c8b7e-6a21-4f0e-9c33-2b5d8e7a1c04",
    "vendor_id": 1241,
    "product_id": 41541,
    "name": "FPS",
    "description": "Low DPI, red lighting",
    "is_default": true,
    "configuration": { },
    "created_at": "2026-07-02T18:03:11Z",
    "updated_at": "2026-07-02T18:03:11Z"
  }
}
```

**`PUT /api/presets/{uid}`** — update. Full replacement of the mutable fields; we always send all
four, so a missing key means "set it empty", not "leave it alone".

```json
{
  "name": "FPS",
  "description": "Low DPI, red lighting",
  "is_default": true,
  "configuration": { }
}
```

**`GET /api/presets?vendor_id=1241&product_id=41541`** — `{ "success": true, "presets": [ … ] }`,
each entry shaped like the create response. Without the query parameters, every preset the customer
has, across all devices.

**`DELETE /api/presets/{uid}`** — no body; `{ "success": true }`.

**`POST` / `PUT /api/macros[/{uid}]`** — same pattern, with the event list as the opaque part:

```json
{
  "uid": "9d2e4c77-...",
  "vendor_id": 1241,
  "product_id": 41541,
  "name": "Reload burst",
  "repeat_count": 1,
  "repeat_mode": 0,
  "events": [ { "key_code": 15, "is_press": true, "delay_ms": 20 } ]
}
```

**`PUT /api/preferences`** — one record per customer, no device involved:

```json
{
  "preferences": {
    "ScrollOneScreenAtATime": false,
    "ScrollLines": 3,
    "DoubleClickSpeed": 500,
    "SleepTimer": 5,
    "GenericLiftOffDistance": 1
  }
}
```

**`PUT /api/devices/1241-41541`** — per-device state; today only the active preset:

```json
{ "active_preset_uid": "1f4c8b7e-6a21-4f0e-9c33-2b5d8e7a1c04" }
```

`GET` returns the same object. A device the customer has never configured should come back as a
`404` or an empty record — your preference, we handle either.

**One thing to confirm before you build any of it:** the existing API is POST-only — `api/login`,
`api/profile/update`, `api/profile/delete` — which we assume comes from the `index.php?route=`
routing. The table above is the shape we'd prefer, but if proper verbs and path parameters are
awkward on your stack, say so and we'll take whatever fits it; consistency with the API you already
have matters more to us than REST purity.

Also, on an endpoint you already have: `api/profile/delete` should remove these records too, so
account deletion stays complete.

## Questions / next steps

1. Does storing the configuration as an opaque JSON field work for you, or would you rather model it
   properly server-side?
2. REST methods as above, or keep everything `POST api/...` to match the current API?
3. How should we handle the same account writing from two PCs? An `updated_at` comparison is probably
   enough, but if you have an existing pattern for this we'll follow it. The one thing we'd ask is
   that **the server never merges two versions itself** — hand us the conflict and the app resolves
   it, since merging presets needs product knowledge the server doesn't have.
4. Any limits we should design around — payload size, records per customer?
5. Do the bodies above look right, and do you want responses wrapped in the current
   `success`/`message` envelope or returned as bare resources?
6. Would this become part of the launcher API spec (a v1.8 section), or live as its own document?
7. What do you need from us to size the work — is this enough, or would you like a full
   field-by-field spec?
