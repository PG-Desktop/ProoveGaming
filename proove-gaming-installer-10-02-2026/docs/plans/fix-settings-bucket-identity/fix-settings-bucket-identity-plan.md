# Fix: one settings bucket per physical device

Status: planned (2026-08-27). Closes Follow-ups 1 and 2 of
[`../fix-rgb-owner-isolation/fix-rgb-owner-isolation-plan.md`](../fix-rgb-owner-isolation/fix-rgb-owner-isolation-plan.md).

## Goal

Every per-device settings row the App stores — `device_rgb`, `device_rgb_effect`, `device_settings`,
`device_actuation`, `button_bindings`, `key_bindings`, `key_macro_bindings`, `device_applied_preset`,
plus the `presets` / `macros` scoping columns — is addressed by
`(owner_kind, owner_id, card.VendorId, card.SettingsProductId, card.SettingsDeviceKey)`. The product
id in that tuple is deliberately pinned per product for a merged-transport device
(`DeviceCatalogEntry.settings_product_id`), so cable ↔ dongle ↔ Bluetooth share one bucket. **The
vendor id is not pinned**: `HomeViewModel.ApplyDeviceIdentity` overwrites `card.VendorId` with the
live transport's vendor on every discovery tick. Three shipped products enumerate at least one
transport under a different vendor than the product itself — Rampage's 2.4 GHz receiver is vendor
`1389` while the board is `10473`, and Cosmo's and Starlight's Bluetooth links are both vendor `1494`
against a product vendor of `3468` — so the same physical device owns **two** settings buckets and
the user watches RGB, DPI, actuation, key remaps and presets "disappear" when the transport changes.
`HidDeviceMapRegistry` already acknowledges this in a comment ("a documented catalog limitation").

A second, compounding defect creates a **third** bucket: `HomeViewModel.RecordToViewModel` restores a
card from storage with `SettingsProductId = record.ProductId` (the stored *routing* product id) and
`SettingsDeviceKey = ""`. Such a card is deliberately clickable before discovery reports in, and
opening it does not merely read an empty bucket — `SetDeviceAsync` persists the resolved RGB state
(`DeviceDetailsViewModel.cs:1176`) and `LoadPresetsForDeviceAsync` seeds a default preset and its
snapshot (`DeviceDetailsViewModel.Presets.cs:1746-1774`) under that wrong pair. For a Rampage last
seen on its dongle the junk lands at `(1389, 49271, '')` — a pair no discovered card will ever
address again.

The goal is that one physical device has exactly one settings bucket for the life of the install,
regardless of which transport it is on and regardless of whether the user opens its page before or
after discovery — and that configuration already stored under the split buckets is carried over
rather than stranded.

## Scope

**In**

- A pinned **settings vendor id** in the device catalog, mirroring the existing settings product id,
  carried on `DeviceCatalogEntry` and stamped onto the card.
- Swapping every settings read/write from `card.VendorId` (routing) to `card.SettingsVendorId`.
- Persisting the full settings identity (vendor, product, device key) on the `devices` row so a
  restored card addresses the bucket it was actually persisted under, before discovery reports in.
- A guarded, transactional re-key of existing rows in every table listed in the Goal, from the
  alternate vendor to the pinned one.
- The DeviceHost-side catalog computation and the additive proto fields it needs.
- Reproduction tests first (this is a `fix-*` plan), plus migration tests on a file-backed store.
- Doc updates: `docs/CONTEXT.md` glossary, the `fix-rgb-owner-isolation` Follow-ups.

**Out**

- Routing identity. Device commands must keep going out under the live transport's VID/PID; nothing
  in this plan changes how a write reaches the hardware.
- The `devices` table's own keying and the duplicate-card heal (`fix-duplicate-device-cards`).
- Server-side (API v2.0) device identity. The backend keys presets, macros and device state by
  `(vendor_id, product_id)`; this plan re-labels only the **local** rows and addresses the
  consequences defensively (see Open question 1). Reconciling what the server already holds under an
  alternate vendor is explicitly not attempted here.
- Cleaning up the junk rows defect 2 has already written. See "Recommended approach → 6".
- Any change to how presets are scoped (still per device *type*, `device_key` vestigial).

## Background: what the data actually looks like

From `src/ProoveGaming.DeviceHost/appsettings.json`, five products set `MergeTransports: true`
(Taipan, Rampage, T-Racer, Cosmo, Starlight). Three of them declare a per-transport `VendorId`
override, and those three — and only those three — are the split products:

| Product | Product vendor | Pinned settings pid | Transport with its own vendor | Bucket that splits off |
|---|---|---|---|---|
| Rampage | `10473` | `12888` (wired) | Dongle2_4G → `1389` | `(1389, 12888, '')` |
| Cosmo | `3468` | `820` (dongle) | Bluetooth → `1494` | `(1494, 820, '')` |
| Starlight | `3468` | `877` (dongle) | Bluetooth → `1494` | `(1494, 877, '')` |

Two facts make the migration tractable, and both should be checked by `/coder` before writing the
re-key:

1. **The product id is already pinned on every transport.** `ResolveSettingsProductId` returns the
   shared id whenever `MergeTransports` is set, without looking at vendor ids. So a Rampage on its
   dongle already writes to product `12888`, not `49271`. The split is therefore **vendor-only**, and
   re-keying is a `vendor_id` rewrite over a small, enumerable set of `(alt vendor, pinned pid)`
   pairs — not a general re-identification.
2. **Nothing else keys on the settings vendor.** `presets.uid` / `macros.uid` are the sync identity
   and are untouched by a vendor rewrite, and the `preset_*` snapshot tables key off `preset_id`
   only, so a preset's snapshot follows it for free.

The one hard constraint: `deviceservice.proto` exposes **no catalog list/get RPC** (20 RPCs, none
catalog-oriented; the catalog arrives embedded per discovered device on `HidDeviceResponse` /
`BluetoothDeviceResponse`). The App therefore cannot learn `1389 → 10473` at startup. It can only
learn it (a) while the device is present, or (b) from something already written to its own database.
That rules out the repo's usual "one-shot migration in `EnsureSchema`" shape and is the single
biggest driver of the recommended design.

## Options considered

### Option A — leave storage alone, union the transports on the read side

Model it on `GetMacros`, which already unions `device_key IN ($dk, '')`: teach each read to also look
under the product's other vendor ids and merge what it finds; keep writing under the live vendor.

- **Cheaper?** Only at first glance. It is a `device_key` union in *one* method today; here it would
  be a cross-vendor union in ~20 read methods across 9 tables, and each one needs a precedence rule
  when both sides have a row.
- Writes still have to pick one vendor, so **the split never heals** — it becomes a permanent
  invariant every future storage method must remember, and the first one that forgets silently
  reintroduces the bug.
- `presets` is scoped per device *type*: unioning it shows the user two preset lists concatenated,
  including two rows named "Default", and the "exactly one default per `(owner, vid, pid)`" invariant
  that `CreateDefaultPresetIfNotExists` and the sync pull both rely on becomes unenforceable.
- The read side would still need the alternate-vendor list, i.e. the same catalog change as Option B,
  so it does not even avoid the proto work.
- **Verdict: rejected.** More total code than the re-key, permanent rather than one-time, and it
  makes a load-bearing invariant unenforceable.

### Option B — pin a settings vendor id in the catalog and re-key existing rows (recommended)

Mirror `settings_product_id` with a `settings_vendor_id` computed from the same "primary" catalog row,
carry the product's full set of transport vendor ids alongside it, stamp both onto the card, switch
every settings call site to the pinned vendor, and re-key the alternate-vendor rows into the pinned
bucket the first time any transport of that product is seen.

- Heals the data; the invariant is enforced in one place (`ApplyDeviceIdentity`) rather than at every
  read.
- Costs an additive proto change, a mechanical swap at ~60 call sites, and a migration that touches
  every settings table — which is where the risk is concentrated.
- The migration must be discovery-triggered rather than an `EnsureSchema` one-shot, because of the
  missing catalog RPC. It stays transactional and idempotent.

### Option C — pin the settings vendor id, migrate nothing

Ship the Option B identity fix and accept that rows under the alternate vendor are orphaned: users
who configured a Rampage over its dongle, or a Cosmo/Starlight over Bluetooth, lose *that* copy of the
configuration once, at upgrade.

- By far the cheapest and the only option with **zero risk of destroying data** — it strands rows, it
  never deletes or overwrites them, and a later plan could still adopt them.
- But the affected users are exactly the users the bug hit hardest: the ones who noticed the split are
  the ones with configuration in both buckets. Telling them the fix costs them one of the two is a bad
  trade for a keyboard's full key-remap and macro set.
- **Verdict: rejected as the shipping plan, retained as the documented fallback.** If review judges
  the migration too risky to run unattended, dropping step 5 below yields Option C exactly, with no
  other change — the phases are ordered so that is a one-commit retreat.

## Recommended approach

Option B, implemented in two ordered phases in one branch.

### Sequencing: defect 2 first, and it is a genuine prerequisite

Defect 2 (`RecordToViewModel`) is fixed **first**, and not merely because it is smaller:

- It is the only one of the two that actively **manufactures new rows** in wrong buckets on every
  pre-discovery page open. Running defect 1's re-key while that is still happening means migrating a
  moving target.
- Its junk lands at the *routing* product id (`(1389, 49271, '')` for a dongle-last Rampage), which is
  a **different** wrong bucket from the one defect 1 creates (`(1389, 12888, '')`). The re-key maps
  `(alt vendor, pinned pid)` only, so junk rows are outside its reach by construction — but only if we
  stop creating them, otherwise every future release accumulates more.
- Its fix — persisting the settings identity on the `devices` row — is what gives the App an
  **offline** record of which bucket a device uses. Phase 2's sync-side canonicalization (Open
  question 1) and any future `EnsureSchema`-time repair both need that, and it is the only source
  available given there is no catalog RPC.

It does **not** fall out of defect 1: even with a perfectly pinned settings vendor, a card restored
from storage has no catalog entry and would still guess its product id from the routing id.

### Phase 1 — a restored card addresses the bucket it was persisted under

1. **`devices` gains three columns** — `settings_vendor_id INTEGER NOT NULL DEFAULT 0`,
   `settings_product_id INTEGER NOT NULL DEFAULT 0`, `settings_device_key TEXT NOT NULL DEFAULT ''`
   — added additively through `AddColumnIfMissing`, exactly like `merge_transports`. `0` / `''` means
   "written before this shipped"; a restored row carrying zeros falls back to today's behaviour
   (`SettingsProductId = record.ProductId`, `SettingsDeviceKey = ""`), so no legacy row changes
   meaning. `DeviceRecord` gains the matching properties, so both storage paths carry them: the
   signed-in path (`LocalStorageService.AddDevice`) and the guest path (`UserSession.UpsertDevice`
   holds `DeviceRecord` instances and flushes through the same table).

2. **`DeviceCardViewModel` gains `SettingsVendorId`**, and `ApplyDeviceIdentity` /
   `ApplyBluetoothIdentity` set it to the live vendor in this phase — i.e. Phase 1 changes no
   behaviour for a connected device. It exists so `PersistDeviceCard` and `RecordToViewModel` have a
   third field to round-trip, and Phase 2 changes only what value goes into it.

3. **`RecordToViewModel` restores the settings identity** instead of deriving it, keeping the
   pre-discovery click (the deliberate behaviour documented at `HomeViewModel.cs:2051`) but pointing
   it at the right rows. The invariant at `HomeViewModel.cs:1213-1224` — a card that owns a settings
   key keeps it for life — must keep holding: `ApplyDeviceIdentity` still only *adopts* a key when the
   card has none, and a restored card now arrives with one, so discovery must not overwrite it.

### Phase 2 — the settings vendor id is pinned per product

4. **Catalog and contract.** `DeviceCatalogEntry` gains two additive fields:
   `int32 settings_vendor_id = 37` (the vendor a merged product persists under — same value on every
   entry of the product, equal to the entry's own vendor when not merged) and
   `repeated int32 transport_vendor_ids = 38` (every distinct vendor id the product's transports
   enumerate under). `HidDeviceMapRegistry` computes both in its constructor from the *same* `primary`
   row that already pins `settings_product_id` (wired → any non-Bluetooth → first), so the two pins
   can never disagree about which transport is canonical. `0` / empty on the wire means an older
   DeviceHost, and the App falls back to the live vendor — today's behaviour exactly.

   `transport_vendor_ids` is what makes the heal converge. With `settings_vendor_id` alone, a Rampage
   on its dongle heals (live `1389` ≠ pinned `10473`), but a user who moved permanently to the cable
   never triggers it and their dongle-era rows stay orphaned forever. With the full list, *any*
   connection of the product heals *every* alternate bucket.

5. **The App keys settings by `SettingsVendorId`.** `ApplyDeviceIdentity` and `ApplyBluetoothIdentity`
   stamp it from the entry; every settings read/write swaps `device.VendorId` → `device.SettingsVendorId`.
   This is the bulk of the mechanical work — the six `DeviceDetailsViewModel.*.cs` files, the
   `SettingsRepository` call sites, the RGB cache key, and the two services that build the bucket from
   stored `devices` rows (`SettingsSyncService`, `SyncCoordinator`) which must read the new
   `settings_vendor_id` column so a push is labelled canonically. `ReapplyOnConnect`'s `card.VendorId <= 0`
   guard stays on the **routing** vendor: it gates a hardware write, not a storage read.

6. **The re-key migration** (`LocalStorageService.RekeySettingsVendor`), triggered from
   `HomeViewModel` at identity resolution rather than from `EnsureSchema` — there is no catalog RPC,
   so construction time has no way to know `1389 → 10473`. It keeps every other property of the repo's
   migration convention:

   - **Transactional.** `HealDuplicateDeviceRows` was reviewed for opening its own transaction because
     `EnsureSchema` opens none; this one is called from a live app and must be atomic for a stronger
     reason — a crash mid-pass with half the tables moved would leave a device split *across* buckets,
     which is worse than the bug. One `BeginTransaction`, all tables, one `Commit`. It runs on the
     storage executor (`RunOnStorageAsync`), never on the UI thread, and takes the service lock like
     every other public storage method.
   - **Idempotent by construction**, like `MigrateMacrosToDevices`: after a pass no row remains under
     the alternate vendor, so a re-run finds nothing. An in-memory `HashSet<(fromVendor, toVendor, pid)>`
     on `HomeViewModel` keeps it from being re-attempted on every discovery tick within a session; that
     set is an optimisation, not the correctness guard. *(2026-08-28, round 2 — m4: that property now
     holds within a session too — the pass is removed from the set again when the queued write faults,
     so a later tick retries it instead of the retry waiting for the next launch.)*
   - **All owners, not just the current one.** The vendor mapping is a property of the hardware, not of
     an account. Re-keying only the signed-in owner would leave a guest's or another account's rows
     split, to be healed only if that account happens to be active while the device is connected. See
     Open question 2.
   - **Scope of a pass:** for a product whose pinned vendor is `V` and pinned product id `P`, every
     `(altVendor, P)` where `altVendor ∈ transport_vendor_ids`, `altVendor ≠ V`. Nothing else is
     touched. Rows whose product cannot be resolved — a device that is not present, a vendor the
     catalog does not name, and every junk row defect 2 already wrote at a routing product id — are
     **left exactly where they are**. They are not deleted: we cannot distinguish a junk row seeded
     from defaults from one the user edited on a pre-discovery page, and stranding an unreachable row
     is recoverable while deleting it is not.
   - **Reconciliation — canonical wins per row.** Both buckets can be populated (configured on cable
     *and* on the dongle). For each of the seven PK-keyed tables plus `device_applied_preset`:
     `INSERT OR IGNORE INTO t (…) SELECT … FROM t WHERE vendor_id = alt`, then
     `DELETE FROM t WHERE vendor_id = alt AND product_id = P`. Effect: a row that exists under the
     canonical vendor keeps its value; a row that exists **only** under the alternate vendor is
     adopted. Nothing that has no counterpart is ever lost. "Keep the newest" was considered and is
     **not implementable** — none of these tables carries a timestamp (only `presets` and `macros` do)
     — and "keep the one with more data" is not well defined for a per-key table. Canonical-wins is
     the rule a user can predict: *what you see on the cable is what you keep*.
   - **`presets` / `macros`** have surrogate `id` keys and a unique `uid`, so they cannot collide:
     re-key by `UPDATE … SET vendor_id = V WHERE vendor_id = alt AND product_id = P`. Two extra rules:
     (a) if the canonical scope already has an `is_default` preset, the migrated presets are demoted to
     `is_default = 0`, because "exactly one default per `(owner, vid, pid)`" is relied on by
     `CreateDefaultPresetIfNotExists` and by the sync pull's default arbitration; (b) the migrated rows
     are **not** marked dirty — their content did not change, only the local label, and mass-dirtying
     would push the whole library on the next sync pass. The user may end with two presets named
     "Default" in one list; that is visible but harmless, and preferable to deleting one.
   - **`sync_meta` and `sync_tombstones`** also carry the vendor. `SyncMetaDeviceStateDirty/UpdatedAt`
     encode it into the key string (`devicestate.<vid>:<pid>.dirty`) — re-key those keys with the same
     canonical-wins rule so the next device-state push is labelled with the pinned vendor;
     `sync_tombstones.vendor_id` is a plain column `UPDATE`.
   - **A one-shot backup before the first re-key of a session.** Copy the database file next to itself
     (`proovegaming.db.pre-vendor-rekey.bak`, written once, never overwritten) before the first pass
     runs. It is a few hundred KB and it is the difference between "the migration had a bug" and "the
     user's configuration is gone".

### What we are not doing, and why

The App does not learn the catalog until a device connects, so a user who never reconnects the
affected device never heals — their rows sit in the alternate bucket, exactly as today. That is
acceptable: the bug is only observable when the device is present.

## File-level change list

**Phase 1 — restored-card identity**

- `src/ProoveGaming.App/Models/DeviceRecord.cs` — add `SettingsVendorId`, `SettingsProductId`,
  `SettingsDeviceKey`, each documented as "the bucket this card's settings live in", with `0`/`''`
  meaning a row written before the columns existed.
- `src/ProoveGaming.App/Services/LocalStorageService.cs` — declare the three columns in the `devices`
  `CREATE TABLE`; add three `AddColumnIfMissing` calls next to the `merge_transports` one; extend the
  four statements that name the `devices` column list (guest read ~`:3696`, guest flush insert
  ~`:3745`, user read ~`:3776`, `AddDevice` insert ~`:3869`) to carry them.
- `src/ProoveGaming.App/ViewModels/DeviceCardViewModel.cs` — add `SettingsVendorId` beside
  `SettingsProductId` / `SettingsDeviceKey`, with a comment stating it is the settings vendor and that
  `VendorId` remains the routing vendor.
- `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` — `PersistDeviceCard` maps the three settings
  fields into `DeviceRecord`; `RecordToViewModel` restores them (replacing
  `vm.SettingsProductId = record.ProductId` / `vm.SettingsDeviceKey = string.Empty`) with the
  legacy-zero fallback; `ApplyDeviceIdentity` and `ApplyBluetoothIdentity` set
  `SettingsVendorId = <live vendor>` for now, and must not overwrite a restored card's
  `SettingsDeviceKey`.
  *(Extended 2026-08-28, round 2 — M1: the discovery-tick persist trigger compares the settings
  triple (`SettingsVendorId`, `SettingsProductId`, `SettingsDeviceKey`) as well as `CardIdentity`.
  A merged card's `CardIdentity` is `(NAME, 0, 0, "")` by construction and blind to the settings
  columns, so an existing merged row — exactly the rows `HealDuplicateDeviceRows` produces — never
  had its `settings_*` columns written back after `ApplyDeviceIdentity` pinned them, and a restored
  card kept falling back to the routing pair every launch. The Bluetooth existing-card path
  (`HandleBluetoothDeviceFoundAsync`) gained the same capture-and-persist, having previously never
  persisted an existing card at all. Steady state stays write-free: both tuples compare equal
  between ticks.)*

**Phase 2 — pinned settings vendor**

- `src/ProoveGaming.GrpcDependencies/Protos/deviceservice.proto` — add `int32 settings_vendor_id = 38`
  and `repeated int32 transport_vendor_ids = 39` to `DeviceCatalogEntry`, each with the
  "0/empty = unset (older DeviceHost)" comment the neighbouring fields use. **gRPC contract change;
  additive and backward compatible — both sides regenerate off this file.**
  *(Implemented 2026-08-27 at 38/39, not the 37/38 this plan first named: `key_overlay_art_dim`
  had taken 37 by the time `/coder` ran. Field numbers are the wire contract, so the unreleased
  side moved.)*
- `src/ProoveGaming.DeviceHost/Services/HidDeviceMapRegistry.cs` — add
  `_settingsVendorIdByName` and `_transportVendorIdsByName`, filled in the same constructor loop that
  pins `_settingsProductIdByName` and from the same `primary` row; populate both new entry fields in
  `ToCatalogEntry`; **replace** the "documented catalog limitation" comment at `:30-35` with what now
  actually happens.
- `src/ProoveGaming.App/ViewModels/HomeViewModel.cs` — `ApplyDeviceIdentity` /
  `ApplyBluetoothIdentity` stamp `SettingsVendorId` from the entry (falling back to the live vendor
  when the entry reports `0`); `LowBatteryIdentity` (`:76`) switches to the settings vendor so its
  in-memory dedupe key stops changing with the transport; new private `HealSettingsVendorAsync(card, entry)`
  invoked after identity is stamped, guarded by the per-session `HashSet`, dispatched through the
  storage executor.
- `src/ProoveGaming.App/Services/LocalStorageService.cs` — new `RekeySettingsVendor(int fromVendorId,
  int toVendorId, int productId)` covering the seven PK-keyed bucket tables, `device_applied_preset`,
  `presets`, `macros`, `sync_meta` device-state keys and `sync_tombstones`, in one transaction, for all
  owners; plus the one-shot pre-migration file backup helper. Documented in the same voice as
  `HealDuplicateDeviceRows` — why it is not in `EnsureSchema`, why it opens its own transaction, why it
  is idempotent.
- `src/ProoveGaming.App/ViewModels/DeviceDetails/DeviceDetailsViewModel.cs`,
  `.Rgb.cs`, `.Presets.cs`, `.Buttons.cs`, `.KeyAssignments.cs`, `.Actuation.cs`, `.Settings.cs`,
  `.Audio.cs` — swap `device.VendorId` → `device.SettingsVendorId` at every storage call site and in
  every destructured identity tuple, including the RGB cache key (`Rgb.cs:66`). Purely mechanical; the
  risk is a **missed** site, not a wrong one.
- `src/ProoveGaming.App/Services/SettingsSyncService.cs` — read the settings identity from the
  `devices` row (new columns, falling back to `vendor_id` / `product_id` when `0`) so pushes are
  labelled with the pinned pair rather than whichever transport was last persisted, and canonicalize
  the pull's applied-preset write through that same alias (Open question 1, answer (b)).
  *(Extended 2026-08-28, round 2 — m2: the pull canonicalizes EVERY local insert through the alias
  — macros and presets as well as the applied-preset write. Uid-matching prevents duplicates but
  not invisibility: a preset pulled under a raw alternate pair landed in a bucket no page reads,
  and `SetAppliedPreset` then referenced a preset id absent from the canonical scope. The alias is
  built once, before step 1, and shared by all four steps; the raw server pair is still what the
  `devices/get` calls go out under.)*
  *(Implemented 2026-08-27. Two divergences from this list. **(a)** The push reads the settings
  PRODUCT id as well as the vendor, not the vendor alone: mixing a pinned vendor with a routing
  product id yields `(10473, 49271)`, a pair that names no bucket at all, which is strictly worse
  than the pre-fix behaviour. **(b)** `SyncCoordinator.cs` needed **no change** — it takes its
  vendor/product from the `presets` / `macros` rows themselves and from the `sync_meta`
  device-state keys, all of which the re-key already moves; it never reads the `devices` table.)*

**Docs**

- `docs/CONTEXT.md` — add **settings bucket**, **settings identity**, **settings vendor id**, and
  **transport vendor ids**; extend the `merge_transports` entry to say the vendor is now pinned too.
- `docs/plans/fix-rgb-owner-isolation/fix-rgb-owner-isolation-plan.md` — mark Follow-ups 1 and 2 as
  closed by this plan with a dated note; do **not** touch its `## Review findings` journal.
- `docs/plans/fix-duplicate-device-cards/` and `docs/plans/add-per-device-identity/` — `/coder` must
  grep both for `SettingsProductId` / "settings bucket" and reconcile any prose the pinned vendor
  invalidates.

## Acceptance criteria

1. Given a Rampage configured (RGB colour, one key remap, one DPI/actuation value) while connected by
   cable, when the user unplugs it and connects the 2.4 GHz dongle, then the details page shows the
   same RGB colour, the same remap and the same value — and the reverse direction holds too.
2. Given the same device, when the user switches transport, then no new row appears under vendor
   `1389`: `SELECT DISTINCT vendor_id FROM device_rgb` returns `10473` only for that product.
3. Given a database written by the current release that holds Rampage rows under **both** `1389` and
   `10473` for product `12888`, when the App next sees the Rampage on any transport, then every row
   formerly under `1389` is either present under `10473` (where `10473` had no row for that key) or
   dropped (where it had), no `1389` row for product `12888` remains, and the total row count per table
   never exceeds the pre-migration count.
4. Given a database that holds Rampage rows under `1389` only, when the App next sees the Rampage **on
   the cable** (i.e. the live vendor already equals the pinned one), then those rows are still migrated
   — the heal is driven by `transport_vendor_ids`, not by the live transport.
5. Given a migration pass that fails part-way (simulate by making one statement throw), then the
   database is unchanged — no table is left half-migrated — and the backup file exists.
6. Given the App is restarted after a successful pass, then the pass does not run again destructively:
   a second invocation is a no-op and leaves every row byte-identical.
7. Given a Rampage last seen on its dongle and the App just launched, when the user clicks its restored
   card **before** discovery reports in, then the page reads the same bucket a discovered card would,
   and the rows `SetDeviceAsync` / `LoadPresetsForDeviceAsync` write land under
   `(10473, 12888, '')` — not under `(1389, 49271, '')`.
8. Given a card restored from a legacy `devices` row (settings columns `0`/`''`), when it is restored,
   then it behaves exactly as it does today (settings product id from the routing product id, empty
   settings key) until discovery corrects it — no crash, no empty page for a device that has data.
9. Given a non-merged device (a single-transport mouse), when anything in this change runs, then its
   settings vendor equals its routing vendor and no re-key is ever attempted for it.
10. Given a Cosmo and a Starlight both paired over Bluetooth (both live vendor `1494`), when each is
    opened, then they address `(3468, 820, '')` and `(3468, 877, '')` respectively — they do not
    collapse into one bucket.
11. Given an older DeviceHost that sends `settings_vendor_id = 0` and no `transport_vendor_ids`, when
    the App runs against it, then every card falls back to the live vendor and no migration runs —
    i.e. today's behaviour, no exception.
12. Given the migration ran while signed in as user A, then user B's and the guest's rows for the same
    product are migrated in the same pass (subject to the ruling on Open question 2).

## Test strategy

`fix-*` plan: **the reproduction tests are written first and must fail on the current code.** Prove
that with a detached worktree at `HEAD`, the way this repo does it:

```
git worktree add --detach ../proove3-prefix HEAD
```

copy the new test files in, run
`dotnet test ../proove3-prefix/tests/ProoveGaming.App.Tests/ProoveGaming.App.Tests.csproj -c Release`,
record the failures in the plan's Review findings, then remove the worktree. Release, not Debug — CI
runs Release and this suite has timing-sensitive neighbours.

**Reproduction tests (must fail pre-fix)**

- `tests/ProoveGaming.App.Tests/HomeViewModelSettingsIdentityTests.cs`
  - `MergedProduct_OnAlternateVendorTransport_KeepsOneSettingsBucket` — stamp a card from a Rampage
    dongle entry, then from a Rampage wired entry; assert the settings identity is identical across
    both. Fails today (`VendorId` differs).
  - `RestoredCard_AddressesTheBucketItWasPersistedUnder` — round-trip a `DeviceRecord` through
    `RecordToViewModel`; assert the settings identity matches what was persisted, not the routing
    product id. Fails today (`SettingsProductId == record.ProductId`, `SettingsDeviceKey == ""`).
- `tests/ProoveGaming.App.Tests/LocalStorageServiceSettingsVendorRekeyTests.cs` — **file-backed**
  store (`:memory:` cannot be pre-populated before construction; mirror
  `LocalStorageServiceDeviceIdentityTests.cs:39-47`, including `SqliteConnection.ClearAllPools()` in
  `Dispose`):
  - `SplitBucket_IsCollapsed_AcrossEverySettingsTable` — seed a row in all nine tables under the
    alternate vendor, migrate, assert each landed under the canonical one.
  - `RowPresentUnderBothVendors_KeepsTheCanonicalValue` — the reconciliation rule, per table.
  - `RowPresentOnlyUnderTheAlternateVendor_IsAdopted`.
  - `Rekey_IsIdempotent_AcrossRepeatedRuns` — model on
    `LocalStorageServiceMacroScopeTests.Migration_IsIdempotentAcrossRepeatedOpens`.
  - `Rekey_DoesNotTouchAnotherProduct_NorTheJunkRoutingProductBucket`.
  - `Rekey_MigratesEveryOwner_NotOnlyTheCurrentOne`.
  - `MigratedPresets_AreDemoted_WhenTheCanonicalScopeAlreadyHasADefault`.
  - `MigratedPresetsAndMacros_KeepTheirUid_AndAreNotMarkedDirty` — protects the sync path
    (uid-matching is what stops the pull duplicating them).
  - `Rekey_FailureMidPass_LeavesTheDatabaseUnchanged`.
  - `Rekey_MovesTheDeviceStateSyncMetaKeys_AndTombstoneVendor`.
- `tests/ProoveGaming.App.Tests/LocalStorageServiceOwnerScopedPersistenceTests.cs` (extend) —
  `AddDevice_RoundTripsTheSettingsIdentity`, and `LegacyRow_WithoutSettingsColumns_RestoresAsBefore`.

**Regression coverage that must stay green.** The existing bucket-keying suites are the safety net for
the ~60-site swap and must not be weakened: `LocalStorageServiceSettingsTests`,
`LocalStorageServicePresetsTests`, `LocalStorageServiceActuationTests`,
`LocalStorageServiceKeyMacroBindingTests`, `LocalStorageServiceMacroScopeTests`,
`LocalStorageServiceDeviceIdentityTests`, `HomeViewModelBluetoothIdentityTests`,
`DeviceDetailsRgbOwnerIsolationTests`. `HomeViewModelBluetoothIdentityTests.MergedTransportEntry_PinsTheSharedSettingsBucket`
gains a vendor assertion.

**Deliberately not automated**

- The DeviceHost-side catalog computation against the real `appsettings.json` — covered by QA, since
  the registry is constructed from configuration and the App tests do not load it. If
  `tests/ProoveGaming.Infrastructure.Tests` can construct a `HidDeviceMapRegistry` over a stub
  `IDeviceInfoProvider`, add one test there asserting Rampage's three transports all report
  `settings_vendor_id = 10473` and `transport_vendor_ids = {10473, 1389}`; that is the cheapest
  insurance available for the pin.
- Real transport switching (unplug cable → plug dongle) — hardware only, QA step 2.
- The end-to-end sync interaction with a live backend — QA step 7 and Open question 1.

## QA (manual test steps)

Requires a Rampage keyboard with **both** its USB cable and its 2.4 GHz dongle. Steps 6–7 need a real
account; steps 1–5 work signed in or as a guest.

1. Install the build under test over an existing installation (do **not** wipe app data — the point is
   to verify existing settings survive). Launch the app and sign in as you normally would.
2. Connect the Rampage **by cable**. Open its card. Set the lighting to a colour you will recognise
   (e.g. bright green), remap one key to something obvious, and change one actuation or DPI value.
   Leave the page.
3. Unplug the cable and connect the **2.4 GHz dongle**. Wait for the card to show as connected, open it.
   *Expected:* the same green lighting, the same remapped key, the same actuation/DPI value. Before this
   fix the page came up with defaults.
4. On the dongle, change the lighting to a different colour (e.g. red). Go back, switch to the cable
   again, open the card. *Expected:* red — the change followed the device, it did not stay behind on the
   dongle.
5. Open the Presets section on each transport in turn. *Expected:* the same preset list both times,
   with the same preset shown as current.
6. **Upgrade check, if you have an existing install that was configured over the dongle.** Before
   installing, note what the dongle-connected Rampage looks like (lighting, one remap). After installing
   and connecting it once on **either** transport, open the card on the **cable**. *Expected:* the
   dongle-era settings are there. If you had configured it on both transports with conflicting values,
   the **cable** values win and the dongle-only extras are kept.
7. Quit the app while the Rampage is connected, then relaunch and — **before** the keyboard shows as
   connected — click its card immediately. *Expected:* the page shows your real lighting and presets
   (not an empty/default page), and after the keyboard finishes connecting the page still shows the same
   thing; nothing resets a moment later.
8. Repeat steps 2–5 with a Cosmo or Starlight headset, switching between its dongle and Bluetooth.
   *Expected:* volume/audio settings and presets are the same on both links, and the two headsets never
   show each other's settings.
9. Sanity: connect any single-transport mouse (e.g. Taipan on cable only, or Blaze). *Expected:* nothing
   about it changed — its settings are exactly where they were.
10. Report anything that looks like configuration **loss** immediately, with the app closed, so the
    database can be recovered from the `.pre-vendor-rekey.bak` file the app writes before migrating.

## Review focus

1. `thread-safety` — the migration runs from a discovery callback (not the UI thread) against the
   singleton `LocalStorageService` while the details page may be reading the same tables. Confirm it
   goes through the storage executor / service lock, and that the per-session guard set is not mutated
   from two threads.
2. `null-safety` — the `0`/`''` sentinels for legacy `devices` rows and for an older DeviceHost's
   catalog entry must all fall back to today's behaviour, never to vendor `0` as a real key.
3. `gRPC contract` — the two new `DeviceCatalogEntry` fields are additive, numbered past the highest
   existing tag, and an old DeviceHost paired with a new App still works.
4. `UI-thread affinity` — no part of the re-key, the backup copy, or the identity round-trip may run on
   the dispatcher.

## Performance & quality

- **Not a hot path.** The re-key runs at most once per product per session, and in practice once per
  install. It is bounded by a single user's rows (tens to low hundreds) and is one transaction.
- **The discovery tick is a hot-ish path** and must stay so: the guard is an in-memory `HashSet` probe
  and an integer comparison; storage is touched only when the probe says a heal is actually pending.
  Do not open the database on every tick to ask.
- **The backup copy is file I/O** — once, off the UI thread, and only immediately before the first pass.
- **Shared state:** the per-session guard set lives on the `HomeViewModel` singleton and is written from
  the gRPC discovery thread — the same hazard `UserSession.Devices` documents. Use the same locking
  discipline, or confine the mutation to the dispatcher.
- No allocation-sensitive code is touched. The call-site swap changes which `int` is read, nothing more.

## Risks

- **This touches every settings table, and a wrong migration loses user configuration.** `device_rgb`,
  `device_rgb_effect`, `device_settings`, `device_actuation`, `button_bindings`, `key_bindings`,
  `key_macro_bindings`, `device_applied_preset`, `presets`, `macros`, `sync_meta`, `sync_tombstones` —
  a mis-scoped `DELETE` (one missing `AND product_id = P`) wipes a user's entire configuration for every
  device under that vendor, silently, with no undo. This is the single reason for the pre-migration
  backup file, for the all-tables-one-transaction rule, and for the per-table reconciliation tests.
  `/reviewer` should read every `DELETE` and `UPDATE` in `RekeySettingsVendor` against its `WHERE`
  clause individually.
- **A missed call site in the ~60-site swap** leaves one aspect (say, actuation) still keyed by the
  routing vendor. It will not fail to compile — both properties are `int` — so it can only be caught by
  review and by the existing per-table tests. Grep for `\.VendorId` under `ViewModels/DeviceDetails/`
  after the change and justify every remaining hit.
- **The server still labels this device with the alternate vendor.** Presets and macros are matched by
  `uid` on pull, so they will not duplicate; but the sign-in pull's per-device-type step can write a
  `device_applied_preset` row back under the alternate vendor, re-splitting that one table. See Open
  question 1.
- **Two presets named "Default"** can appear in a migrated list. Cosmetic, but users will report it.
- **The heal never runs for a device the user never reconnects**, so a support report of "still split"
  is expected and is not necessarily a bug — ask whether the device has been connected since upgrading.
- **Phase 1 stops new junk rows but does not remove the old ones.** A database that has already been
  written to by a pre-discovery page open keeps unreachable rows at a routing product id forever. This
  is deliberate (they are indistinguishable from edited data) and should be stated in the PR.
- **Proto/DeviceHost coupling.** The App must tolerate an older DeviceHost; an installed pair is
  upgraded together, but a developer running a stale DeviceHost locally will see the fallback path and
  should not mistake it for the fix not working.

## Open questions

> **2026-08-27 — all four answered by the product owner; none block `/coder` any more.**
> **Q1 → (b)**: canonicalize on the pull side via the new `devices.settings_vendor_id` alias,
> limited to the applied-preset write (the architect's own recommendation).
> **Q2 → all owners**, since the vendor mapping is a property of the hardware, not of an account.
> **Q3 → canonical-wins confirmed**: what the wired/primary transport holds is what is kept; a row
> existing only under the alternate key is adopted, and nothing without a counterpart is deleted.
> **Q4 → yes, a one-shot backup** of `proovegaming.db`, written once next to the original before the
> first pass and never overwritten. Nothing in the app deletes it — decide a retention story only if
> it ever becomes a support problem.
> Sequencing decision unchanged: defect 2 lands first as a prerequisite, then the re-key.
> This plan is queued behind [fix-owner-scoped-state-persistence](../fix-owner-scoped-state-persistence/fix-owner-scoped-state-persistence-plan.md),
> which is concurrently rewriting `AddDevice` / the heal migration / `ReloadOwnerScopedState` — the
> same code this plan builds on. Do not start `/coder` until that plan has landed.

> **2026-08-28 (round 2)** — Q1's answer (b) was extended from "limited to the applied-preset
> write" to every local insert the pull makes: see the dated note on the `SettingsSyncService.cs`
> entry in the File-level change list, and Round 2 below.

1. **Sync residual (blocks the `SettingsSyncService` / `SyncCoordinator` part of the change).** The API
   v2.0 endpoints key presets, macros and device state by `(vendor_id, product_id)`. After a local
   re-key, the server still holds this device's state under the alternate vendor. Choose one:
   (a) accept it and let the pull's `devices/get` for the alternate type write an orphan
   `device_applied_preset` row; (b) canonicalize on the pull side using the new
   `devices.settings_vendor_id` column as an offline alias, so an alternate-vendor pull is written into
   the canonical bucket; (c) descope to a follow-up plan and document the residual. Recommendation: (b),
   limited to the applied-preset write, which is the only place the pull re-splits a table.
2. **Whose rows does a pass migrate?** Recommendation: all owners, because the vendor mapping is a
   property of the hardware. This does mean a pass run while signed in as A rewrites B's and the guest's
   rows for that product. Confirm that is acceptable, or restrict to the current owner and accept that
   other accounts heal only when they are next active with the device connected.
3. **Reconciliation rule.** Canonical-wins is recommended and "keep the newest" is not implementable
   (no timestamps on these tables). Confirm the product decision: *what you see on the wired/primary
   transport is what you keep*.
4. **The backup file.** Confirm the name/location (`%AppData%` next to `proovegaming.db`), that it is
   written once and never overwritten, and whether anything should ever delete it.

## Review findings

### Round 1 — 2026-08-27 — Ship: yes (with 4 Should-fix)

Reviewed against the whole working-tree delta, restricted to this plan's files. The tree also
carries `fix-owner-scoped-state-persistence`, `improve-api-error-surfacing`, contact-us,
forgot-password and profile work; those were excluded and are not assessed here.

Verified in Release (the App and DeviceHost are running from Debug output, so Debug builds fail on
file locks — not a finding): App 0 errors, DeviceHost 0 errors, App.Tests **1373/1373**,
Infrastructure.Tests **827/827**.

#### Plan conformance

- **Implemented as planned:** every File-level change list item. Phase 1 (three `devices` columns,
  `DeviceRecord` / `DeviceCardViewModel` fields, `RecordToViewModel` restore, `PersistDeviceCard`
  round-trip) and Phase 2 (proto fields, registry pins, `ApplyDeviceIdentity` /
  `ApplyBluetoothIdentity` stamping, the ~74-site call-site swap, `RekeySettingsVendor`, the
  one-shot backup, the pull-side canonicalization) are all present. Docs updated as required:
  `docs/CONTEXT.md` gained the four terms and the extended `merge_transports` entry;
  `fix-rgb-owner-isolation` Follow-ups 1 and 2 are closed with a dated note that leaves its
  `## Review findings` journal untouched; `fix-duplicate-device-cards` prose corrected in place.
- **Deviations, all three surfaced by `/coder` and all three ruled CORRECT** — see the rulings
  below. The plan's own File-level list has already been amended to match, so plan and code agree.
- **Missing / partial:** the plan's `RestoredCard_AddressesTheBucketItWasPersistedUnder` test was
  not written, because `RecordToViewModel` is not reachable from a test. See T1.
- **Accepted limitation (per plan):** a device the user never reconnects never heals; junk rows at
  a routing product id are stranded rather than deleted. Both are deliberate and correctly
  implemented. Residual risk: support will see "still split" reports — the plan already says to ask
  whether the device has been connected since upgrading.

#### Rulings on the three flagged items

**(a) Proto tags 38/39 instead of the plan's 37/38 — CORRECT.** `key_overlay_art_dim` holds 37 on
this branch, so 37 was genuinely unavailable. I enumerated `DeviceCatalogEntry`: tags are 1–39
contiguous, no duplicates, no `reserved` ranges, so 38/39 were the next free numbers. The change
stays additive and backward compatible in both directions — an older DeviceHost simply never sets
them and the App reads `0` / empty, which every consumer treats as the documented fallback; a newer
DeviceHost against an older App has its two extra fields ignored as unknown fields. This is the
same "the unreleased side moves" precedent `supported_audio_settings = 32` already set in this file,
and the coder followed it including the explanatory comment.

**(b) `SyncCoordinator.cs` needed no change — CORRECT, the plan's file list was wrong.** Verified
independently: `SyncCoordinator` never calls `_storage.GetDevices` and never touches the `devices`
table. Its vendor/product pairs come from two places, both of which the re-key already moves — the
`presets` / `macros` rows themselves (`GetDirtyPresets` / `GetDirtyMacros`, whose scope columns the
migration rewrites) and `TryParseDeviceStateDirtyKey` over the `sync_meta` device-state keys (which
the migration re-keys). Every `devices` string in that file is either an API path (`devices/get`,
`devices/update`) or a log message. Leaving it untouched is right; changing it would have been the
error.

**(c) Push side also reads the settings PRODUCT id — CORRECT and in scope, not scope creep.** The
plan's wording named only the vendor, but the plan's *intent* ("so pushes and pulls are labelled
with the pinned pair rather than whichever transport was last persisted") requires both. Everything
`PushCoreAsync` does with that pair — `GetMacroDeviceKeys`, `GetMacros`, `GetPresets`,
`GetAppliedPresets` — is keyed by the full settings identity. Feeding it a pinned vendor with a
routing product id yields `(10473, 49271)` for a dongle-last Rampage: a pair nothing has ever
written to, so the push would find zero presets and zero macros and silently stop syncing that
device — strictly worse than the pre-fix `(1389, 49271)`, which at least matched the junk bucket.
Reading both is the only self-consistent option. `SettingsBucketOf` implements the `0` fallback
correctly.

#### The re-key SQL, statement by statement

`LocalStorageService.RekeySettingsVendor` (`:1084-1223`). Every `DELETE` and `UPDATE` read against
its own `WHERE`, as the plan's Risks section demands. **All eleven are correctly scoped; I found no
mis-scoped statement.**

| # | Statement | Scope | Verdict |
|---|---|---|---|
| 1 | `INSERT OR IGNORE INTO {table} … SELECT … FROM {table}` (×8, `:1122-1124`) | `vendor_id = $from AND product_id = $pid` | correct |
| 2 | `DELETE FROM {table}` (×8, `:1126`) | `vendor_id = $from AND product_id = $pid` | correct |
| 3 | `UPDATE presets SET is_default = 0` (`:1147-1156`) | `vendor_id = $from AND product_id = $pid AND is_default = 1 AND EXISTS(… owner-correlated …)` | correct |
| 4 | `UPDATE presets SET vendor_id = $to` (`:1173`) | `vendor_id = $from AND product_id = $pid` | correct |
| 5 | `UPDATE macros SET vendor_id = $to` (`:1173`) | `vendor_id = $from AND product_id = $pid` | correct |
| 6 | `INSERT OR IGNORE INTO sync_meta … SELECT` (×2, `:1195-1196`) | `key = $fromKey` | correct — see below |
| 7 | `DELETE FROM sync_meta` (×2, `:1198`) | `key = $fromKey` | correct — see below |
| 8 | `UPDATE sync_tombstones SET vendor_id = $to` (`:1212`) | `vendor_id = $from AND product_id = $pid` | correct |

Detail worth recording, because these are the places a future edit could go wrong:

- **The INSERT/DELETE pair shares one predicate string, written once per statement in the same
  `CommandText`.** That is what makes "the DELETE is scoped more loosely than the INSERT" — the
  single mistake that would destroy a configuration — impossible to introduce without editing both
  halves of the same literal. Good structural choice, not just a correct one.
- **The interpolation is safe.** `{table}`, `{keyTail}` and `{payload}` come from
  `SettingsBucketTables` / `SettingsBucketSurrogateKeyTables`, both `private static readonly` arrays
  of compile-time literals. No caller-supplied string reaches the SQL text; `$from` / `$to` / `$pid`
  are bound parameters throughout. No injection surface.
- **Column lists check out against the schema, table by table.** I verified each of the eight
  against its `CREATE TABLE`: `device_rgb` 7 cols, `device_rgb_effect` 10, `device_settings` 7,
  `device_actuation` 11, `button_bindings` 8, `key_bindings` 9, `key_macro_bindings` 7,
  `device_applied_preset` 6 — each equals five key columns + `KeyTail` + `Payload`, and the `$to`
  substitution sits in the `vendor_id` position in both lists. `key_macro_bindings` is the one to
  watch: a pre-existing database may still carry the vestigial `mode NOT NULL DEFAULT 0` column, and
  the INSERT correctly omits it, exactly as `SetKeyMacroBinding` already does.
- **The self-referencing INSERT is safe.** Each statement inserts into the table it selects from,
  but the SELECT filters `vendor_id = $from` while the inserted rows carry `vendor_id = $to`, so it
  can never read its own output.
- **`sync_meta` scopes by an encoded key string, and that is sufficient.** `$fromKey` is
  `devicestate.<vendor>:<product>.dirty` / `.updated_at`, built through the same
  `SyncMetaDeviceStateDirty` / `…UpdatedAt` helpers the writers use. The pair is unambiguously
  encoded with a `:` separator, so `key = $fromKey` is exactly as narrow as
  `vendor_id = $from AND product_id = $pid` would be on a column-shaped table. Reusing the helpers
  rather than re-forming the string is what keeps it that way.
- **Nothing is owner-scoped, deliberately** (Open question 2 → all owners), and `sync_meta`'s
  per-owner canonical-wins still works because the INSERT carries `owner_kind` / `owner_id` from the
  source row.
- **Ordering is right:** the preset demotion runs *before* the vendor move, while `vendor_id = $from`
  still identifies the incoming rows. Reversing those two would silently demote nothing.
- **`is_dirty` / `local_rev` are untouched and there are no triggers on any of these tables** (I
  checked: zero `CREATE TRIGGER` in the file), so a migration genuinely cannot queue the user's whole
  library for a server push.

#### Test-fixture audit (the bulk repair)

99 tests failed after the call-site swap and were repaired by a bulk edit. I checked all 19 files
for silent weakening. **Clean — the repair only added lines.**

- `git diff` over the 19 fixture files: **0 deleted lines in every one of them**. A weakened
  assertion would have had to show up as a `-` line; there are none.
- Every added line in 18 of the 19 files is a `SettingsVendorId = <same value as VendorId>,`
  initializer entry — 36 of them. That is the correct repair: those fixtures describe
  single-transport devices, whose settings vendor genuinely equals their routing vendor.
- The 19th, `HomeViewModelBluetoothIdentityTests.cs`, is the only one with substantive additions,
  and they **strengthen** rather than weaken: `entry.VendorId = 1494` plus
  `Assert.Equal(10473, card.SettingsVendorId)` and `Assert.Equal(1494, card.VendorId)`. That is the
  vendor assertion the plan asked for, and setting the entry's live vendor to a *different* value
  first is what makes it meaningful rather than tautological.
- Across all of `tests/`, only 9 lines were deleted, and every one belongs to the other concurrent
  plans (an `AuthErrorCodes` comment, a contact-us helper signature, `DeleteDevice` signature
  changes from `fix-owner-scoped-state-persistence`). None is from this change.

#### The ~74-site call-site swap

No missed site. I grepped `\.VendorId` under `ViewModels/DeviceDetails/` and justified every
remaining hit: all are routing pairs of the form `X.VendorId, X.ProductId` feeding
`IUsbHidDeviceService` (hardware writes, `TryGetLastDpiStage`, `ProbeKeyMatrixAsync`,
`ApplyDeviceConfigurationAsync`), catalog identification (`RampageKeyCatalog.IsRampage` and
friends), or a `DeviceStateChanged` event comparison. A wider sweep for any
`_localStorage.` / `_settings.` / `_storage.` call still receiving a bare `.VendorId` anywhere in
the App returns **nothing**. Two sites the regex could not have caught were found and fixed by hand
— the interpolated `AssertedStageIdentity` cache key (`DeviceDetailsViewModel.cs:716`) and the
`vid` local in `LoadPersistedDeviceStateAsync` that served both identities — which is exactly the
class of miss the plan warned about.

#### Review-focus checks

- **thread-safety** — the guard set is a `HashSet` behind a `Lock`, every access inside
  `lock (_settingsVendorRekeyGate)`, no `await` in the critical section, and the `Add` result is
  copied out before use. Correct, and the choice of a lock over dispatcher confinement is justified
  in a comment (a `Post` per discovery tick is the traffic the tick exists to avoid).
- **null-safety** — the `0` / `''` sentinels are honoured at every hop: `RecordToViewModel`
  (`> 0 ? … : record.VendorId`), `SettingsBucketOf`, `ApplyDeviceIdentity` /
  `ApplyBluetoothIdentity` (`entry.SettingsVendorId > 0 ? … : liveVendor`),
  `RekeySettingsVendor`'s `<= 0` guard, `HealSettingsVendor`'s `<= 0` guard, and the registry's
  `vid > 0` filter on the transport list. Vendor `0` never becomes a real key. One residual hole in
  how the *pin* is sourced — see S1.
- **gRPC contract** — additive, past the highest tag, both fallbacks exercised. See ruling (a).
- **UI-thread affinity** — the re-key, the backup copy and the identity round-trip all run through
  `EnqueueStorageWrite` → `RunOnStorageAsync` → `TaskScheduler.Default`. Nothing reaches the
  dispatcher. `ReapplyOnConnect`'s `card.VendorId <= 0` guard correctly stayed on the **routing**
  vendor, as the plan required.

#### Code

- **Blockers — none.**

- [x] **S1. `HealSettingsVendor` takes the pin from the CARD, not the ENTRY, so a fallback value can drive a re-key backwards** — `src/ProoveGaming.App/ViewModels/HomeViewModel.cs:1361-1362` — Should-fix

  **Problem:** the heal reads `card.SettingsVendorId` / `card.SettingsProductId` as the *canonical*
  target. Those two properties are not always a catalog pin — both `ApplyDeviceIdentity` and
  `RecordToViewModel` legitimately fill them from a **fallback**: the live routing vendor when the
  entry reports `0`, and the stored routing pair for a legacy `devices` row. The heal cannot tell a
  pin from a fallback, and it re-keys *towards* whatever it is handed.

  **Why it matters:** in the Bluetooth path, `HealSettingsVendor(card, entry)` is called
  unconditionally after `ApplyBluetoothIdentity(card, entry)`, and `ApplyBluetoothIdentity`
  early-returns without stamping when `entry.VendorId <= 0 || entry.ProductId <= 0`. A card restored
  from a legacy row therefore still carries its stored routing pair — `(1389, 49271)` for a
  dongle-last Rampage — while `entry.MergeTransports` and `entry.TransportVendorIds` are both
  populated. The heal then runs `RekeySettingsVendor(from: 10473, to: 1389, product: 49271)`: the
  **wrong direction**, moving any canonical-vendor rows at that product id into the alternate
  bucket. It is narrow — it needs an entry with a zero routing identity, or a catalog whose primary
  row has an unparseable `VendorId` — and today's shipping catalog cannot produce it, which the new
  Infrastructure tests pin. But the failure is silent, destructive and has no undo, which is
  precisely the class of risk this plan exists to eliminate, and the guard is two lines.

  **Solution options:**
  - Option A (correct): source the pin from the entry and require it to be a real pin —
    `if (entry.SettingsVendorId <= 0 || entry.SettingsProductId <= 0) return;` then use
    `entry.SettingsVendorId` / `entry.SettingsProductId` as the target throughout. The heal then
    only ever runs on catalog authority, never on a fallback, and the "older DeviceHost means no
    migration" rule becomes structural rather than incidental.
  - Option B (minimal): keep reading the card but gate the whole method on
    `entry.SettingsVendorId > 0 && card.SettingsVendorId == entry.SettingsVendorId`.

- [x] **S2. A failed backup copy is never retried, and a partial `.bak` is treated as a good one** — `src/ProoveGaming.App/Services/LocalStorageService.cs:1238-1261` — Should-fix

  **Problem:** `BackupDatabaseOnce` sets `_settingsVendorBackupTaken = true` *before* attempting the
  copy (`:1242`), and swallows everything. Two consequences. (1) If `File.Copy` throws — an
  antivirus or indexer lock on the database file is the realistic case on Windows — no backup exists
  and none will be attempted again this process, yet every subsequent `RekeySettingsVendor` call
  still runs and still rewrites twelve tables. (2) If `File.Copy` throws *part-way*, Windows leaves a
  truncated `.bak` behind; the next process sees `File.Exists(backup)` and returns early, so a
  corrupt backup is preserved forever and looks like a valid one. That is worse than no backup,
  because QA step 10 tells the user to recover from exactly that file.

  **Fix:** set the flag only after a successful copy, and copy to a temp name then rename into place
  so a partial file can never be mistaken for a complete one. If a copy attempt fails, either leave
  the flag clear so the next pass retries, or skip the migration for this session — the current
  middle ground (migrate without a backup) is the one option that takes the risk while discarding
  the mitigation.

- [x] **S3. The backup is a raw file copy, correct only because the journal mode happens to be the default** — `src/ProoveGaming.App/Services/LocalStorageService.cs:1255` — Should-fix

  **Problem:** `File.Copy` of the main database file captures a consistent snapshot only under
  SQLite's default rollback journal. The constructor sets `PRAGMA foreign_keys = ON` and nothing
  else, so that holds today. But the day anyone adds `PRAGMA journal_mode = WAL` — a natural change
  for a UI app with a background storage executor, and one nothing here forbids — recently committed
  pages live in the `-wal` sidecar and this copy silently becomes a stale or torn snapshot. The
  failure would stay invisible until someone actually needed the backup.

  **Fix:** use `VACUUM INTO '<path>'` (one statement, transactionally consistent under any journal
  mode) or `SqliteConnection.BackupDatabase`, rather than a file copy. Either is small now and
  removes a trap that is very hard to find later. At minimum, comment the `foreign_keys` pragma to
  say the backup depends on the journal mode staying default.

- [x] **S4. `RecordToViewModel` and `HealSettingsVendor` are unreachable from tests, so two of the plan's own behaviours are unguarded** — `src/ProoveGaming.App/ViewModels/HomeViewModel.cs:2200` and `:1356` — Should-fix

  **Problem / why it matters:** both are private instance methods on a view-model whose constructor
  needs the full device/session graph, so the plan's
  `RestoredCard_AddressesTheBucketItWasPersistedUnder` could not be written, and the
  `transport_vendor_ids`-driven trigger — the actual mechanism behind acceptance criterion 4 — has
  no test at all. The storage half is well covered; the mapping and the trigger are not. This file
  already solves exactly this problem four times: `CardIdentity`, `IsRepresentedOnScreen`,
  `ShouldRestoreForOwner` and `ApplyBluetoothIdentity` are all `internal static` precisely so tests
  can drive them.

  **Fix:** extract the restore fallback into an `internal static` helper (e.g.
  `SettingsIdentityFor(DeviceRecord)`), and make the heal's decision a pure `internal static`
  function returning the passes to run (e.g.
  `PendingVendorRekeys(DeviceCatalogEntry, int pinnedVendorId, int pinnedProductId)`), leaving
  `HealSettingsVendor` as a thin dispatcher. Doing this also makes S1 directly testable, which is
  the strongest argument for it.

#### Tests

- [x] **T1. No test drives the restore mapping** — cover `RecordToViewModel`'s settings-identity
  fallback (a populated row restores its bucket; a legacy `0`/`0`/`''` row falls back to the routing
  pair) once S4's helper exists — `tests/ProoveGaming.App.Tests/HomeViewModelSettingsIdentityTests.cs`
- [x] **T2. No test drives the heal trigger** — cover that a merged entry yields one pass per
  alternate vendor, that the pinned vendor is skipped, that an empty `transport_vendor_ids` yields
  no pass, and that the per-session guard suppresses a second attempt — same file, once S4's helper
  exists. This is the mechanism behind acceptance criterion 4, currently proven only at the storage
  layer.
- [x] **T3. S1's wrong-direction case has no regression test** — once S1 is fixed, assert that an
  entry with `settings_vendor_id = 0`, and a card whose pin came from a fallback, produce **no**
  re-key pass at all.

#### Nits

- `RekeySettingsVendor`'s return value double-counts an adopted row (once for the INSERT, once for
  the DELETE). Documented as "a diagnostic, not a row count" and the tests rely only on
  zero/non-zero, so this is fine — but a caller that ever logs it will log a number with no natural
  meaning. — `LocalStorageService.cs:1036-1039`
- `BuildSettingsBucketAliasAsync` issues one extra `GetDevices` per sign-in pull even when no alias
  is needed. Negligible at sign-in frequency. — `SettingsSyncService.cs:684`
- On a first sign-in on a fresh machine the `devices` table is empty, so the alias is empty and the
  applied-preset write lands under the server's pair. Correct per the documented fallback and
  self-healing on the next pull once a card is persisted — worth knowing when reading a support log.

#### What's done well

- **The migration's structure is the review's own best argument.** Putting the table list in one
  `private static readonly` array with an explicit key-tail/payload split means the eight
  INSERT/DELETE pairs cannot drift apart, and putting both halves in one `CommandText` behind one
  shared predicate makes the plan's headline risk — a `DELETE` scoped more loosely than its
  `INSERT` — structurally hard to introduce. Better than eight correct hand-written statements.
- Demotion ordered before the vendor move, with an owner-correlated `EXISTS`. Easy to get backwards;
  it is right, and the comment says why.
- Not marking migrated presets/macros dirty, and preserving `uid`. Both load-bearing for sync, both
  explained at the point of decision.
- Comments explain *why* rather than *what* throughout — the "why not in `EnsureSchema`" paragraph
  in particular saves the next reader the whole catalog-RPC investigation.
- `Rekey_FailureMidPass_LeavesTheDatabaseUnchanged` induces the failure by dropping a table so the
  throw lands *after* eight tables have moved inside the open transaction. A real atomicity test
  rather than a decorative one.
- The Infrastructure tests assert the pins against the **real shipping `appsettings.json`**. The plan
  had written this off as "deliberately not automated"; finding that the existing suite already loads
  the catalog and using it is the highest-value test in this change, because a config edit is the
  most likely way the pin regresses.
- Both plan-list divergences were reported rather than absorbed silently, and the plan was corrected
  in the same change. That is what keeps plans trustworthy.

#### Verdict

**Ship: yes** — advisory only; the final review and the merge decision are the developer's. No
blockers: all eleven migration statements are correctly scoped, the fixture repair added lines and
weakened nothing, and both suites are green in Release at 1373 and 827. The four Should-fix items
concern the *margins* of a change whose core is sound — S1 is the only one with a data-loss
scenario, and it is not reachable with the shipping catalog.

First: **1)** S1, the wrong-direction heal guard — two lines, and it closes the last path by which
this change could move data the wrong way. **2)** S2, the backup flag and the partial-`.bak` window,
since QA step 10 tells users to rely on that file. **3)** S4 with T1/T2, extract the two helpers so
the restore mapping and the heal trigger stop being untestable.

### Round 2 — 2026-08-28 — fix pass (`/coder`)

The round-2 PR review raised one Major and two Minors against this plan; all three are fixed.

- **M1 (Major, CONFIRMED)** — the settings identity of an EXISTING merged-transport card was never
  written back to its `devices` row. The discovery-tick persist fired only on a `CardIdentity`
  change, and a merged card's `CardIdentity` is `(NAME, 0, 0, "")` by construction — blind to the
  `SettingsVendorId` / `SettingsProductId` that `ApplyDeviceIdentity` had just stamped. Exactly the
  users `HealDuplicateDeviceRows` marks `merge_transports = 1` (two transports in history — the
  original bug's trigger population) therefore kept `settings_* = 0` forever: the restored card fell
  back to the routing pair each launch, pre-discovery page opens kept manufacturing junk buckets
  (acceptance criterion 7 broken), and two Bluetooth-last headsets — COSMO and STARLIGHT both route
  as `(1494, 10)` — could collapse into one bucket before discovery (criterion 10 broken).
  **Fix (the review's Option A):** capture the settings triple next to `identityBefore` and persist
  when EITHER tuple changed, via a new `SettingsIdentityOf(card)` helper beside `CardIdentity`; the
  Bluetooth existing-card path gained the identical capture-and-persist (it previously never
  persisted an existing card at all). Steady state stays write-free — the pinned triple is stable
  between ticks, which is the property the guard exists for. The trigger-level test and the
  COSMO/STARLIGHT no-collapse test are the round-2 `/test-writer`'s items (review finding m3).
- **m2 (fixed as strictly safer)** — the pull canonicalized only device state; presets and macros
  were inserted under the raw server pair, invisible to the page reading the canonical bucket, and
  `SetAppliedPreset` could then reference a preset id absent from the canonical scope. The
  `canonicalBucketByServerPair` alias is now built once above step 1 and every local insert routes
  its pair through it; the `devices/get` network calls still go out under the raw server pair.
- **m4 (CONFIRMED)** — `_settingsVendorRekeyed.Add(pass)` ran before the fire-and-forget storage
  write, so a faulted re-key silenced the retry for the whole session despite the set being
  documented as "an optimisation, not the correctness guard". The pass is now removed from the set
  in an `OnlyOnFaulted` continuation, so a later tick retries within the session; the reserve-first
  ordering is kept so overlapping ticks cannot enqueue the same pass twice.

Verified in Release: App build 0 errors, `ProoveGaming.App.Tests` green (counts recorded in the PR
conversation). New tests deliberately left to `/test-writer` per the pipeline.

### Round 2 — 2026-08-28 — test pass (`/test-writer`)

Closes review finding **m3** — the persist-trigger gap that let M1 ship: the pure helpers
(`SettingsIdentityFor`, `PendingVendorRekeys`) were covered and the storage round-trip was covered,
but nothing asserted that the discovery tick actually ISSUES the write-back. New suite
`tests/ProoveGaming.App.Tests/HomeViewModelSettingsPersistTriggerTests.cs` drives a real
`HomeViewModel` (the `HomeViewModelPersistenceTests` harness) through the private per-sender
discovery handlers via reflection — the only production entry point is an async-void event handler
serialized by a private gate, so completion is otherwise unobservable; the file's header records
the synchronous-window reasoning that replaces a `Dispatcher.RunJobs` pump (which the process-wide
dispatcher's first-touch thread affinity makes unsafe in the sequential suite).

- `RestoredMergedRow_WithLegacySettingsColumns_IsRewrittenWithThePinnedPair_OnTheFirstTick` — the
  M1 reproduction (acceptance criterion 7 on the restored-card path). **Proven pre-fix:** run in a
  detached worktree at `0183857` (before the round-2 fixes) it fails exactly at the row assertion —
  the in-memory card is stamped `(10473, 12888, "")` while the row keeps `(0, 0, "")`.
- `SteadyState_ASecondIdenticalTick_IssuesNoStorageWrite` — the guard's reason to exist: a second
  identical tick performs zero storage accesses (asserted through `OnBeforeStorageAccess`).
- `BluetoothTick_RewritesARestoredMergedRow_WhoseSettingsTripleItCorrected` — the Bluetooth
  existing-card persist plus its own steady state; fails pre-fix the same way.
- `CosmoAndStarlight_RestoredFromBluetoothRows_AreRewrittenIntoSeparateBuckets` — acceptance
  criterion 10 on the restored-card path (both headsets route as `(1494, 10)`); fails pre-fix with
  both rows still at `(0, 0, "")`.

Full `ProoveGaming.App.Tests` in Release: **1422/1422** (round-2 baseline 1417 + these four and the
`improve-api-error-surfacing` m1 regression test).
