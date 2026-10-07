# Macros Become Per-Physical-Device

> **2026-08-25 schema note:** the `macros` table gained `uid` (client-generated GUID,
> unique, assigned at insert and backfilled for existing rows; the device-duplication
> migration below gives each copy its own uid) and `server_updated_at` (the server's
> timestamp echoed verbatim; NULL = never synced) for API v2.0 sync — see
> [../add-api-v2-sync/add-api-v2-sync-plan.md](../add-api-v2-sync/add-api-v2-sync-plan.md).
> Nothing else in this plan's contract changed.

> **2026-08-25 schema note (live sync):** the `macros` table further gained `is_dirty` and
> `local_rev` (mutators mark user-owned rows dirty and bump the rev; a push clears the flag
> rev-guarded), and deleting a synced user macro now writes a row into the new
> `sync_tombstones` table in the same transaction — see
> [../add-api-v2-live-sync/add-api-v2-live-sync-plan.md](../add-api-v2-live-sync/add-api-v2-live-sync-plan.md).
> The migration behavior described here is unchanged, but **read-side scoping is not**: the
> [sign-in pull](../add-api-v2-pull/add-api-v2-pull-plan.md) writes cloud macros under the
> unit-agnostic `device_key = ''` scope, because API v2.0 keys macros on VID/PID alone and
> has no per-unit key. `GetMacros` therefore unions the `''` scope into every unit-keyed
> read, so a device's Macros page lists its own macros **plus** the account's pulled ones
> for the same model. Macros recorded on a unit are still written under that unit's key and
> are still invisible to other units — what changed is that the shared scope is now also
> visible, not that per-unit macros leak.

## Relationship to `add-per-device-macros` (main, #70)

**These two plans specify the same feature.** They were written independently and both landed:
`../add-per-device-macros/add-per-device-macros-plan.md` came in with #70 on `main`, this one on
`features/PG-391-cyrexaress`. The schema, the `GetMacros`/`SaveMacro` signatures and the
load-in-`SetDevice` change are identical — the merge kept one copy of each.

**They differ on exactly one decision: what happens to macros recorded before scoping existed.**

| | Pre-scoping macros |
|---|---|
| `add-per-device-macros` (#70) | *Fresh start* — `ClearLegacySharedMacros()` deletes every macro plus its events and bindings. |
| **This plan (kept)** | Each macro follows the device that binds it, duplicated when several do; only unreferenced macros are dropped. |

The merge resolved in favour of this plan because the user was asked directly and chose
"migrate to the device they are bound to; drop unbound ones". `ClearLegacySharedMacros` is gone;
`MigrateMacrosToDevices` is what runs. Everything else from #70's plan — the preset snapshot
seeding, `TableExists`, the bool-returning `AddColumnIfMissing` — is untouched and still in place.

## Goal

A macro today belongs to an *owner*, not a device: `macros` is keyed only by
`(owner_kind, owner_id)`, so one library is shown on every device's Macros page. Record a
mouse macro and it appears on a keyboard, where it cannot be meaningfully bound. Make a macro
belong to the physical unit it was recorded on — the same `(owner_kind, owner_id, vendor_id,
product_id, device_key)` key every other per-device table already uses — so a macro recorded
on one unit never shows up on another. (Since API v2.0 sync landed, a unit's Macros page also
lists the account's pulled macros, which live in the shared `device_key = ''` scope — see the
2026-08-25 schema note above.)

This is a **behaviour change with a data migration**, not a new feature, so the migration and
its characterization tests carry more of the risk than the UI work.

Companion plan: [../add-cyrex-aress-parity/add-cyrex-aress-parity-plan.md](../add-cyrex-aress-parity/add-cyrex-aress-parity-plan.md).
That plan's Phase 2 (macros for Cyrex/Aress) inherits whatever scoping lands here, so **this
plan should land first**.

## Scope

**In**

- Device columns on `macros`, keyed like every other per-device table.
- A one-time migration: macros currently bound to a device follow that device; unbound macros
  are deleted.
- `LocalStorageService` macro API becomes device-scoped.
- App: the macro library loads per device instead of once per app session.
- Guest → user re-key and guest cleanup stay correct for the new shape.
- Preset references to macros stay valid.

**Out**

- Any change to how a macro is *recorded* or *encoded* (the recorder, `RasterMacroEncoder`,
  the `SetMacros` RPC).
- Sharing or copying a macro between devices from the UI. Per-unit scoping makes that a real
  feature request; it is not this plan.
- Macros for Cyrex/Aress — see the companion plan.
- The unrelated open items on `main`: the inaccurate upload-failure log, CI not running tests,
  and the Raster physical-keys RGB colour path.

## Options considered

### 1. What "per device" is keyed on

The user has chosen **per physical unit**. Recorded here with the trade-off it accepts:

| Option | Trade-off |
|---|---|
| **A. Per physical unit** — `(owner, vendor_id, product_id, device_key)` | **Chosen (user decision).** Identical to `device_rgb` / `button_bindings` / `key_macro_bindings`, so the key shape, the guest re-key and the call-site conventions are already established. Cost: someone with two identical keyboards re-records per unit. |
| B. Per model — `(owner, vendor_id, product_id)` | Kinder to duplicate hardware, but diverges from every other settings table and would need its own re-key path. |
| C. Per device type — keyboards vs mice | Stops the cross-type pollution that motivated this with no re-recording, but a user with two different keyboards still sees irrelevant macros. |

### 2. A macro bound on two devices at once

Today nothing stops one macro being bound to a mouse button **and** a keyboard key —
`button_bindings.macro_id` and `key_macro_bindings.macro_id` are independent. One row cannot
belong to two units.

| Option | Trade-off |
|---|---|
| **A. Duplicate per device on migration** | **Chosen.** Every existing binding keeps working, which is the only thing the user can actually observe. Costs: ids diverge, `macro_events` must be copied per duplicate, and each binding must be repointed at its own copy. Editing one copy no longer changes the other — which is the correct semantics *after* this change, but is a silent behaviour change for anyone who had relied on the shared row. |
| B. Keep one row, assign it to one device, drop the other binding | Simpler migration, but silently breaks a working binding — exactly the "app and firmware disagree with no error" class of defect this codebase has already been bitten by twice. Rejected. |

### 3. Migration idempotency

The migration deletes rows, so running it twice must be harmless.

| Option | Trade-off |
|---|---|
| **A. Sentinel-driven — migrate only rows with `vendor_id = 0`** | **Chosen.** After a successful pass no row has `vendor_id = 0` (migrated rows carry real ids, unbound rows are gone), so the pass is naturally self-limiting with no extra state. It also degrades safely if interrupted: a partial run leaves the remaining rows still marked. |
| B. `PRAGMA user_version` / a `schema_meta` row | Explicit and conventional, but introduces a versioning mechanism this repo does not have, and a mismatch between the marker and reality (restored DB, manual edit) silently skips the migration. |

**Hard constraint, non-negotiable:** the schema change is additive via the existing
`AddColumnIfMissing` (`LocalStorageService.cs:252`). **No `DROP TABLE` in `EnsureSchema()`** —
that block runs on every construction and doing exactly this wiped user data in this codebase
(see [../raster-svg-effects/raster-svg-effects-plan.md](../raster-svg-effects/raster-svg-effects-plan.md),
Review findings Round 3 **B1**).

## Recommended approach

Additive columns, a sentinel-driven one-time migration that duplicates multi-bound macros, and
a device-scoped macro API. Then move the App's library load onto the device-switch path.

### Schema

Three columns on `macros`, added with `AddColumnIfMissing`:

```
vendor_id  INTEGER NOT NULL DEFAULT 0
product_id INTEGER NOT NULL DEFAULT 0
device_key TEXT    NOT NULL DEFAULT ''
```

`vendor_id = 0` is the "not yet assigned to a device" sentinel — no real device reports vendor
id 0. Fresh databases never see it: `SaveMacro` always writes real values.

### Migration (`MigrateMacrosToDevices`, one transaction, called from `EnsureSchema` after the
column additions)

For every macro with `vendor_id = 0`:

1. Collect its distinct owning devices from **three** reference sources:
   - `button_bindings` where `macro_id` matches (mouse bindings),
   - `key_macro_bindings` where `macro_id` matches (keyboard key bindings),
   - `preset_button_bindings` joined through `presets` to get the preset's device.
2. **No owning device** → delete the macro and its `macro_events` (the accepted data loss).
3. **Exactly one** → stamp that device onto the row. Nothing else changes; the id is stable, so
   every existing reference keeps working.
4. **More than one** → keep the original row stamped with the first device, and for each
   additional device insert a copy (new id, same name/repeat fields, `macro_events` copied with
   ordinals preserved), then repoint that device's `button_bindings` / `key_macro_bindings` /
   `preset_button_bindings` rows at the new id.

`preset_button_bindings` is included deliberately: it holds `macro_id` with **no** foreign key,
and `DeleteMacro` does not clear it, so a preset can already outlive its macro today. Treating a
preset reference as an owning reference stops this migration widening a wart that already exists.

### API

```
GetMacros(ownerKind, ownerId, vendorId, productId, deviceKey)
SaveMacro(ownerKind, ownerId, vendorId, productId, deviceKey, MacroRecord)
DeleteMacro(id)                        // unchanged — id is still globally unique
```

Call sites pass `Device.SettingsProductId` / `Device.SettingsDeviceKey`, **not** raw
`ProductId` / `DeviceKey`, matching every other device-scoped call so a merged-transport device
(wired ↔ dongle ↔ Bluetooth) keeps one settings bucket.

### App side

`DeviceDetailsViewModel` is a **singleton** (`App.axaml.cs:197`) reused through `SetDevice`, and
`LoadMacroLibrary()` is called once from the **constructor** (`DeviceDetailsViewModel.cs:138`).
That is the crux of the change:

- Move the `LoadMacroLibrary()` call out of the constructor and into `SetDevice`, alongside
  `LoadButtonBindings` / `LoadRgbStateForCurrentDevice` / `LoadKeyLayoutForDevice`.
- `LoadMacroLibrary` clears `Macros` first, then loads that device's rows.
- Order matters: it must run **before** `LoadButtonBindings`, because that method resolves
  `_buttonMacros` by looking macro ids up in `Macros` — load the library after it and every
  bound button silently loses its macro.
- `Macros.CollectionChanged` already drives `SyncMacrosesGroupOptions`, `ApplyMacroSearch` and
  `RebuildMacroAssignmentTargets`, so the clear-and-reload keeps the catalog and the keyboard
  macro targets in step for free. Verify it does not fire `NotifyPresetChanged` and mark the
  freshly-opened device dirty.
- Reset the recorder / Create-Macro dialog state on device switch, or a half-recorded macro
  carries across devices and saves against the wrong one.

## File-level change list

### Edited

| File | Intent |
|---|---|
| `src/ProoveGaming.App/Services/LocalStorageService.cs` | Add the three columns via `AddColumnIfMissing`; add `MigrateMacrosToDevices`; device-scope `GetMacros` / `SaveMacro`; extend the `macros` guest cleanup and `ReKeyAsync` comment to state why no dedupe is needed (id remains the PK, so guest and user rows cannot collide). |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs` | Remove `LoadMacroLibrary()` from the constructor; call it from `SetDevice` before `LoadButtonBindings`. |
| `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.Buttons.cs` | `LoadMacroLibrary` clears `Macros` and passes the device key parts; `SaveCreateMacro` passes them to `SaveMacro`; reset recorder/dialog state on device switch. |
| `docs/CONTEXT.md` | Glossary: **Macro (device-scoped)** — mandatory per CLAUDE.md, and update the `key_macro_bindings` / `Macro library` entries which currently imply an owner-wide library. |

### Created

| File | Intent |
|---|---|
| `tests/ProoveGaming.App.Tests/LocalStorageServiceMacroScopeTests.cs` | Temp-**file** database tests: characterization of today's behaviour, then the migration cases (unbound dropped, single-device stamped, multi-device duplicated, preset-referenced kept, idempotent on re-open). |
| `tests/ProoveGaming.App.Tests/DeviceDetailsMacroLibraryScopeTests.cs` | The library reloads on `SetDevice` and shows only that device's macros; bound buttons keep their macro across the reload. |

## Acceptance criteria

- [ ] Given macros recorded on a mouse and a keyboard, when the keyboard's details page is
      opened, then its Macros list shows **only** the keyboard's macros.
- [ ] Given a macro recorded on device A, when device B's details page is opened and then A's
      again, then B never showed A's macro and A still shows it.
- [ ] Given a macro is created while device A is open, when the row is inspected in SQLite,
      then its `vendor_id` / `product_id` / `device_key` equal A's `VendorId` /
      `SettingsProductId` / `SettingsDeviceKey`.
- [ ] Given a merged-transport device on its dongle transport, when a macro is created and the
      device is reconnected over cable, then the macro is still listed (settings bucket
      unchanged).
- [ ] **Migration — unbound:** given a pre-migration database with a macro bound to nothing,
      when the App opens, then the macro and its `macro_events` rows are gone.
- [ ] **Migration — single device:** given a macro bound to one mouse button, when the App
      opens, then the macro's id is unchanged, it carries that mouse's device columns, and the
      binding still resolves to it.
- [ ] **Migration — two devices:** given one macro bound to both a mouse button and a keyboard
      key, when the App opens, then two macro rows exist with the same name and event list, each
      carrying one device's columns, and **both** bindings resolve to their own row.
- [ ] **Migration — preset reference:** given a macro referenced only by
      `preset_button_bindings` (bound to no live button), when the App opens, then the macro
      survives, carries the preset's device columns, and the preset row points at it.
- [ ] **Migration — idempotent:** given the App opens twice on the same database, then the
      second open changes no macro rows and deletes nothing.
- [ ] Given a guest with device-scoped macros signs in, when the re-key runs, then those macros
      appear under the user id with their device columns and bindings intact.
- [ ] Given a bound button and a macro, when the device is switched away and back, then the
      button still shows its macro name (library reload ordering is correct).
- [ ] `dotnet build ./src/ProveGaming.slnx` — 0 errors; `dotnet test ./src/ProveGaming.slnx` —
      all green.

## Test strategy

This is a `migrate-*` change, so **characterization tests land before the functional change.**
The touched feature does have tests — `tests/ProoveGaming.App.Tests/DeviceDetailsMacroAssignmentTests.cs`
and `DeviceDetailsKeyboardMacroBindingTests.cs` — and they must stay green; both construct their
storage with `"Data Source=:memory:"` and call `GetMacros`-adjacent paths indirectly, so the API
signature change will ripple into them.

**`tests/ProoveGaming.App.Tests` — must be added (non-negotiable)**

Migration cases, all against a **temp-file** database, in `LocalStorageServiceMacroScopeTests`:

1. Characterization first: a macro saved pre-change is visible to an owner-wide read. Written
   against current code so it fails once the API is device-scoped, then updated in the same
   commit as the change — its purpose is to prove the test exercises the path being altered.
2. Unbound macro is deleted, and its `macro_events` go with it.
3. Single-device macro keeps its id and gains the right device columns.
4. Multi-device macro is duplicated; both bindings resolve; event lists match; ordinals preserved.
5. Preset-only reference survives and the preset still points at it.
6. Re-opening the database twice is a no-op (idempotency).
7. Guest → user re-key preserves device columns.

Library-scoping cases in `DeviceDetailsMacroLibraryScopeTests`:

8. `SetDevice(A)` then `SetDevice(B)` — `Macros` contains only B's.
9. A button bound to a macro still resolves after a device switch (proves load ordering).

**Why a file database, not `:memory:`** — every existing `LocalStorageService` test uses
`"Data Source=:memory:"`, where the schema block runs once against an empty database and the
connection lives for the test. That **cannot observe** what the schema block does to an
already-populated database, which is exactly how a `DROP TABLE` regression stayed green across
466 tests. A migration is precisely a "schema block meets existing data" change, so it must be
tested by constructing a `LocalStorageService`, disposing it, and constructing a second one over
the same file. Pattern to copy:
`tests/ProoveGaming.App.Tests/LocalStorageServiceKeyMacroBindingTests.cs`.

**Deliberately not automated**

- The Macros page's visual refresh on device switch (Avalonia render tests are not set up).
- Real HID macro upload — no device in CI; covered by QA below.

**CI reality:** `.github/workflows/dotnet-ci.yml` runs on `ubuntu-latest` and points
`dotnet test` at the App csproj, which references no test SDK — so it passes having run **zero**
tests, and both test projects are Windows-only TFMs. Nothing in CI protects this migration. It
must be verified locally, and that is a standing risk for a change that deletes user rows.

## QA (manual test steps)

Requires at least two devices — ideally a mouse and a keyboard — and, for the migration steps,
an app profile that already has macros recorded.

**Part A — migration (do this first, on an existing profile with macros)**

1. Before updating, note down every macro you have and which button or key each one is assigned
   to. Take a screenshot of the Macros list.
2. Install/run the new build and open each device in turn.
   **Expected:** every macro that was assigned to a button or key is still there, on that
   device, and still assigned. Macros that were **not** assigned to anything are gone — that is
   intentional.
3. Press each previously-assigned button/key.
   **Expected:** it still plays what it played before.
4. Close and reopen the app twice.
   **Expected:** nothing else disappears. The macro list is identical each time.

**Part B — per-device behaviour**

5. Open the mouse, create a macro named "MouseOnly", assign it to a side button.
6. Go back and open the keyboard. Open its Macros page.
   **Expected:** "MouseOnly" is **not** listed.
7. On the keyboard, create a macro named "KeyOnly" and assign it to a key.
8. Return to the mouse's Macros page.
   **Expected:** "KeyOnly" is **not** listed; "MouseOnly" still is, still assigned.
9. Press the mouse button and the keyboard key.
   **Expected:** each plays its own macro.
10. Close the app, reopen it, and check both devices.
    **Expected:** each device still shows its own macro, still assigned, and neither shows
    the other's. (Signed in: each page may additionally list macros pulled from the account
    — those live in the shared `device_key = ''` scope and appear on every unit of the
    model.)
11. Start recording a macro on the mouse but do **not** save; navigate back and open the
    keyboard, then open its Macros page.
    **Expected:** no half-finished macro appears, and nothing is saved to the keyboard.
12. If you have a device with more than one connection (cable and dongle): create a macro on one
    connection, then reconnect via the other and open the device.
    **Expected:** the macro is still there.

## Review focus

- `null-safety` — new columns and the migration read nullable `macro_id` values from three
  tables; a null must not become a device id of 0.
- `thread-safety` — the migration runs inside `EnsureSchema` on the constructor thread while
  `_lock` guards every other accessor; confirm it cannot deadlock against itself.
- `async/await` — the App's library load moves onto the `SetDevice` path, which already mixes
  synchronous SQLite reads with fire-and-forget device work; do not turn a sync read into a
  blocking wait on the UI thread.
- `resource disposal` — the migration uses an explicit transaction; every command needs
  disposing and the transaction needs committing on exactly one path.

## Performance & quality

- **No hot paths touched.** Macro load is a per-device-open SQLite read of a handful of rows,
  not a per-frame or per-poll operation.
- **Startup cost:** the migration runs once inside `EnsureSchema`, in a single transaction, over
  a table that holds tens of rows in practice. Acceptable. It must remain a single transaction —
  a partial migration that deletes events but not macros would leave macros that play nothing.
- **Existing convention retained:** `LocalStorageService` accessors are synchronous under
  `_lock.Wait()`. Do not introduce an async accessor for macros alone; matching the file matters
  more here than the ideal.
- **Allocation:** the duplicate path copies event lists in memory. Bounded by the 60-events cap
  the encoder enforces, so no concern.

## Risks

- **The migration deletes rows.** An unbound macro someone was about to assign is gone with no
  undo. Accepted by the user, but it is the single highest-consequence line in this plan, and CI
  will not catch a mistake in it (see Test strategy).
- **A macro can be referenced from three places**, one of which (`preset_button_bindings`) has no
  foreign key and is not cleaned up by `DeleteMacro`. If a fourth reference source is added later
  and this migration is not updated, macros will be deleted out from under it.
- **Library load ordering.** `LoadButtonBindings` resolves `_buttonMacros` against `Macros`. Get
  the order wrong and every bound button silently loses its macro name and its device push — a
  quiet failure, not a crash.
- **Singleton view-model state.** `DeviceDetailsViewModel` is reused across devices; anything
  macro-related held in a field rather than reloaded per device (recorder buffer, editing id,
  search text, selection) will bleed between devices. Enumerate and reset them.
- **Preset semantics drift.** Presets are already per-device, so a preset's `macro_id` will
  point at a macro on the same device and stays valid. This holds only while presets remain
  per-device; if presets ever become shareable across devices, macros must be revisited.

## Open questions

1. **Should the UI offer "copy this macro to another device"?** Per-unit scoping makes
   re-recording the only path today. Out of scope here, but it is the obvious follow-up request
   and worth deciding before users hit it.
2. **Should `DeleteMacro` also clear `preset_button_bindings.macro_id`?** It does not today, so
   dangling preset references already exist. Fixing it is a small, separate correctness change —
   do it here or log it?

## Review findings

_Filled in by /reviewer._
