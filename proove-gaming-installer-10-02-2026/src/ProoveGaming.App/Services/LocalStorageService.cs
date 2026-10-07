using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using ProoveGaming.App.Models;
using ProoveGaming.GrpcDependencies.Device;

namespace ProoveGaming.App.Services;

public enum SessionState { None, Active, Expired }

public sealed class LocalStorageService : IDisposable
{
    // Row-owner "kinds" for owner-scoped tables. Kept as string constants instead of an
    // enum so they can go straight into a Sqlite parameter without cast.
    public const string OwnerKindGuest = "guest";
    public const string OwnerKindUser  = "user";

    // sync_tombstones.kind values and the well-known sync_meta keys the live-sync
    // coordinator reads and writes (docs/plans/add-api-v2-live-sync).
    public const string TombstoneKindPreset = "preset";
    public const string TombstoneKindMacro = "macro";
    // uid = the server's notification id as a string (notifications are server-id-keyed,
    // docs/plans/add-api-v21-notifications-sync); vendor/product carry 0.
    public const string TombstoneKindNotification = "notification";
    public const string SyncMetaPreferencesDirty = "preferences.dirty";
    public const string SyncMetaPreferencesUpdatedAt = "preferences.updated_at";
    // Batch intents for the bell (docs/plans/add-api-v21-notifications-sync): one
    // read_all / delete_all call instead of a per-row storm. Generation-valued like every
    // other dirty mark.
    public const string SyncMetaNotificationsReadAll = "notifications.read_all";
    public const string SyncMetaNotificationsDeleteAll = "notifications.delete_all";
    public const string SyncMetaDeviceStatePrefix = "devicestate.";
    public const string SyncMetaDirtySuffix = ".dirty";

    public static string SyncMetaDeviceStateDirty(int vendorId, int productId) =>
        $"{SyncMetaDeviceStatePrefix}{vendorId}:{productId}{SyncMetaDirtySuffix}";

    public static string SyncMetaDeviceStateUpdatedAt(int vendorId, int productId) =>
        $"{SyncMetaDeviceStatePrefix}{vendorId}:{productId}.updated_at";

    // Pending device apply (docs/plans/add-extended-keys-api-sync): the sign-in pull adopts the
    // cloud's applied preset by writing device_applied_preset, but that row alone never reaches
    // the board — the preset's snapshot still has to be copied into a UNIT's live rows and pushed,
    // and the pull knows no unit. This mark says "a pulled preset for this device type is waiting
    // to be materialized"; the first path that has a unit in hand (the details page's preset load,
    // or the connect-time reapply for a mouse) consumes it once and clears it. NOT a dirty mark:
    // PushDeviceStateAsync enumerates this same prefix but acts only on keys ending in .dirty.
    public const string SyncMetaPendingApplySuffix = ".pending_apply";

    // The mark's value is the LOCAL id of the preset the pull adopted, not a bare flag: a mark the
    // consumer declines (a read-only transport — see MaterializePulledPresetAsync) survives
    // indefinitely, and nothing else ever clears it, so a bare flag would later be spent against
    // whatever preset happens to be applied by then — reloading that preset's snapshot over the
    // live rows and discarding applied-but-unsaved state. Carrying the id lets the consumer tell
    // "still the preset the pull adopted" from "the user has since chosen something else".
    public static string SyncMetaPendingApplyValue(int presetId) =>
        presetId.ToString(CultureInfo.InvariantCulture);

    public static string SyncMetaDeviceStatePendingApply(int vendorId, int productId) =>
        $"{SyncMetaDeviceStatePrefix}{vendorId}:{productId}{SyncMetaPendingApplySuffix}";

    // Device-roster intent marks (docs/plans/add-api-v21-device-roster): written by the
    // devices-table mutators, pushed as devices/add | devices/remove by SyncCoordinator.
    // Keyed by the card's settings identity — the per-model pair the server rosters by.
    // Add and remove are separate keys so "last intent wins" is one upsert + one delete.
    public const string SyncMetaDeviceRosterPrefix = "deviceroster.";
    public const string SyncMetaRosterAddSuffix = ".add";
    public const string SyncMetaRosterRemoveSuffix = ".remove";

    public static string SyncMetaDeviceRosterAdd(int vendorId, int productId) =>
        $"{SyncMetaDeviceRosterPrefix}{vendorId}:{productId}{SyncMetaRosterAddSuffix}";

    public static string SyncMetaDeviceRosterRemove(int vendorId, int productId) =>
        $"{SyncMetaDeviceRosterPrefix}{vendorId}:{productId}{SyncMetaRosterRemoveSuffix}";

    // Raised (outside _lock, on the mutating thread) after a sync-relevant write to
    // user-owned state: macro/preset content or metadata changed, a delete tombstoned,
    // preferences or applied-preset state marked dirty, a device-roster intent recorded
    // (docs/plans/add-api-v21-device-roster). Guest-owned writes and the sign-in
    // pull's inserts (rows arriving WITH a server_updated_at) never raise it. Any FUTURE
    // mutator of user-owned synced state must mark its rows dirty the same way and raise
    // this event, or its changes silently never reach the server
    // (docs/plans/add-api-v2-live-sync). Subscribers must not throw.
    public event Action? SyncRelevantDataChanged;

    private void RaiseSyncRelevantDataChanged() => SyncRelevantDataChanged?.Invoke();

    private readonly SqliteConnection _connection;

    // Serialises every use of _connection. Microsoft.Data.Sqlite forbids concurrent access
    // to one connection, and here the App reads from a background thread (device reconnect
    // → GetRgbState) while the UI thread writes (debounced saves, MigrateGuestToUserAsync).
    // A SemaphoreSlim (not lock/Lock) is required because MigrateGuestToUserAsync awaits
    // inside the critical section.
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Test seam (InternalsVisibleTo ProoveGaming.App.Tests): observes every storage access —
    // the threading tests use it to assert which thread SetDeviceAsync touches storage on.
    // Instance, not static, so parallel test classes with their own stores don't cross-talk.
    internal Action? OnBeforeStorageAccess { get; set; }

    // Single funnel for taking _lock so the seam above sees every access. New call sites must
    // use this (or invoke the seam themselves, as MigrateGuestToUserAsync does for WaitAsync),
    // never a bare _lock.Wait().
    private void AcquireLock()
    {
        OnBeforeStorageAccess?.Invoke();
        _lock.Wait();
    }

    // ── Serialized background storage executor ───────────────────────────────────
    // One FIFO execution chain for the ordering-sensitive storage work the UI thread used to
    // run inline. Contract (callers depend on every clause):
    //   - Strict FIFO in enqueue order; items never run concurrently with each other.
    //   - The returned task completes only after the delegate has executed (or faulted) --
    //     it is a write barrier, not an enqueue acknowledgement.
    //   - Items run on pool threads, never the caller's thread.
    //   - A faulted item does not stop later items (fire-and-forget sites log the fault).
    //   - Enqueue itself is cheap and non-blocking (safe from PropertyChanged handlers).
    // Items still take _lock when they call the sync methods below, so they stay mutually
    // excluded against the pre-existing Task.Run readers (the SetDeviceAsync pattern in
    // DeviceDetailsViewModel), which keep their semantics unchanged.
    private readonly object _executorGate = new();
    private Task _executorTail = Task.CompletedTask;

    // Set by Dispose AFTER the drain: an item that reaches the front of the queue once
    // teardown has begun is dropped (reads yield default) instead of hitting a disposed
    // connection -- faulting here would rethrow through awaiters (AsyncRelayCommand) on a
    // pool thread. Same data loss as the pre-executor ObjectDisposedException, minus the
    // crash; the exit path drains before disposing, so real writes are unaffected.
    private volatile bool _disposed;

    // True once Dispose has begun tearing the executor down. Contract for readers: after this,
    // RunOnStorageAsync<T> completes with default(T) — null for the reference/nullable results
    // every repository read returns — WITHOUT running the delegate. Callers that would act on
    // that value (apply factory defaults, decide dirty state) must either check this first and
    // skip, or treat the null exactly like "nothing saved" (see SettingsRepository's loads and
    // HomeViewModel.LoadNotificationsCoreAsync). Deliberately not an exception: faulting would
    // rethrow through awaiters (AsyncRelayCommand) on a pool thread during shutdown.
    public bool IsShutDown => _disposed;

    public Task RunOnStorageAsync(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_executorGate)
        {
            var item = _executorTail.ContinueWith(
                _ => { if (!_disposed) work(); },
                CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
            _executorTail = ObserveForChain(item);
            return item;
        }
    }

    // After teardown begins (IsShutDown) the delegate is dropped and the task completes with
    // default(T) — see the contract on IsShutDown before adding a caller that acts on the result.
    public Task<T> RunOnStorageAsync<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_executorGate)
        {
            var item = _executorTail.ContinueWith(
                _ => _disposed ? default! : work(),
                CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default);
            _executorTail = ObserveForChain(item);
            return item;
        }
    }

    // For delegates that are themselves async (MigrateGuestToUserAsync's body): the chain
    // advances only when the INNER task completes, so later items still run strictly after it.
    public Task RunOnStorageAsync(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_executorGate)
        {
            var item = _executorTail.ContinueWith(
                _ => _disposed ? Task.CompletedTask : work(),
                CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
            _executorTail = ObserveForChain(item);
            return item;
        }
    }

    // Completes when everything enqueued so far has run. Never faults: each item owns its
    // exception (observable on the task RunOnStorageAsync returned for it).
    public Task DrainStorageQueueAsync()
    {
        lock (_executorGate) return _executorTail;
    }

    // The chain link that follows an item: completes when the item has finished, observing
    // (and discarding) its exception so one failed delegate never faults or blocks the items
    // enqueued after it.
    private static Task ObserveForChain(Task item) => item.ContinueWith(
        static t => _ = t.Exception,
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.DenyChildAttach,
        TaskScheduler.Default);

    public LocalStorageService() : this(BuildDefaultConnectionString()) { }

    // Overload for tests: pass e.g. "Data Source=:memory:" or a file path.
    public LocalStorageService(string connectionString)
    {
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        // Enable FK enforcement so ON DELETE CASCADE on the preset_* snapshot tables
        // actually fires. SQLite defaults it OFF; must be set per connection, so it lives
        // here rather than in the schema. Legacy DBs that predate the FKs get the manual
        // cascade path in DeletePreset instead — see the comment there.
        using (var pragma = _connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            pragma.ExecuteNonQuery();
        }
        EnsureSchema();
    }

    private static string BuildDefaultConnectionString()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ProoveGaming");
        Directory.CreateDirectory(folder);
        return $"Data Source={Path.Combine(folder, "proovegaming.db")}";
    }

    // Every per-device table also carries device_key: two physically distinct units can
    // share the same (vendor_id, product_id) (e.g. two identical BLAZE mice), so device_key
    // (see DeviceCardViewModel.DeviceKey) disambiguates them. It is orthogonal to the
    // owner_kind/owner_id scoping used for guest → user migration — both are part of the
    // natural key everywhere a device is involved. presets (and its preset_* snapshot children,
    // which key off preset_id) are the one exception: they're scoped per device TYPE, not per
    // physical unit, so units of the same model share one preset list.
    private void EnsureSchema()
    {
        // Checked BEFORE the CREATE TABLE IF NOT EXISTS batch below, so it reflects whether this DB
        // predates preset_settings — used after the batch to seed existing presets (see the M3 fix
        // note near SeedPresetSettingsFromLiveDeviceSettings).
        var presetSettingsIsNew = !TableExists("preset_settings");

        // Same reason, and the stakes are higher: unlike key_bindings, key_macro_bindings already
        // holds real user data. Once preset_key_macro_bindings exists, LoadPresetSnapshotToDevice
        // wipes the live rows and copies the snapshot back — so a DB whose presets have no
        // snapshot would lose every macro binding on its first preset switch.
        var presetKeyMacroBindingsIsNew = !TableExists("preset_key_macro_bindings");

        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS app_session (
                id           INTEGER PRIMARY KEY,
                session_uuid TEXT NOT NULL DEFAULT '',
                started_at   TEXT NOT NULL,
                -- One-shot latch for the guest expiry warning, so restarting inside the warning
                -- window does not re-notify on every launch. The row dies with the session
                -- (ClearData / ClearSession), so a new guest session starts with it clear.
                expiry_warning_sent INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS devices (
                id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                -- 'guest' rows are the session-scoped card list (rewritten whole by Flush);
                -- 'user' rows are a signed-in user's cards, written through per change.
                owner_kind         TEXT NOT NULL DEFAULT 'guest',
                owner_id           TEXT NOT NULL DEFAULT '',
                name               TEXT NOT NULL,
                device_type_label  TEXT NOT NULL,
                image_path         TEXT,
                -- Render scale for image_path, from the catalog. Persisted alongside the path for
                -- the same reason the path itself is: a card restored while its device is OFF is
                -- never touched by discovery, so anything not stored here is lost for that card.
                -- 0 means "unset" and the ViewModel reads it as 1.0.
                image_scale        REAL NOT NULL DEFAULT 0,
                battery_level      INTEGER,
                connection_icon    TEXT,
                card_type          TEXT NOT NULL DEFAULT 'single',
                vendor_id          INTEGER NOT NULL DEFAULT 0,
                product_id         INTEGER NOT NULL DEFAULT 0,
                -- Container ID — distinguishes two identical units of one model; '' when unknown.
                device_key         TEXT NOT NULL DEFAULT '',
                -- 1 when this product is one device reachable over several transports. Such a row
                -- is keyed by (owner_kind, owner_id, name) alone, because the three columns above
                -- change with the live transport — see AddDevice.
                merge_transports   INTEGER NOT NULL DEFAULT 0,
                -- The SETTINGS bucket this card's rows live in — (settings_vendor_id,
                -- settings_product_id, settings_device_key) is the tuple every per-device table is
                -- keyed by. Stored separately from the routing columns above because for a merged
                -- product they are pinned per product while the routing identity follows the live
                -- transport. 0 / '' = written before these columns existed; a restored card falls
                -- back to deriving them from vendor_id / product_id, i.e. to the old behaviour.
                settings_vendor_id  INTEGER NOT NULL DEFAULT 0,
                settings_product_id INTEGER NOT NULL DEFAULT 0,
                settings_device_key TEXT NOT NULL DEFAULT ''
            );
            CREATE TABLE IF NOT EXISTS saved_auth (
                id               INTEGER PRIMARY KEY,
                customer_id      INTEGER NOT NULL,
                email            TEXT NOT NULL,
                nickname         TEXT NOT NULL DEFAULT '',
                token            TEXT NOT NULL,
                token_expires_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS last_sign_in (
                id          INTEGER PRIMARY KEY,
                email       TEXT NOT NULL,
                remember_me INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS sign_in_lockout (
                id            INTEGER PRIMARY KEY,
                lockout_until TEXT NOT NULL
            );
            -- A macro belongs to the physical device it was recorded on, keyed the same way as
            -- every other per-device table. vendor_id = 0 is the "not yet assigned to a device"
            -- sentinel: no real device reports vendor 0, so MigrateMacrosToDevices uses it to
            -- find pre-scoping rows exactly once. Fresh databases never see it — SaveMacro
            -- always writes real values.
            CREATE TABLE IF NOT EXISTS macros (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                owner_kind   TEXT NOT NULL DEFAULT '',
                owner_id     TEXT NOT NULL DEFAULT '',
                vendor_id    INTEGER NOT NULL DEFAULT 0,
                product_id   INTEGER NOT NULL DEFAULT 0,
                device_key   TEXT NOT NULL DEFAULT '',
                name         TEXT NOT NULL,
                repeat_count INTEGER NOT NULL DEFAULT 1,
                repeat_mode  INTEGER NOT NULL DEFAULT 0,
                created_at   TEXT NOT NULL,
                -- Client-generated sync identity (API v2.0); '' only transiently, before the
                -- constructor backfill assigns a GUID. server_updated_at is the server's
                -- updated_at echoed verbatim (opaque string); NULL = never synced.
                uid               TEXT NOT NULL DEFAULT '',
                server_updated_at TEXT
            );
            CREATE TABLE IF NOT EXISTS macro_events (
                id       INTEGER PRIMARY KEY AUTOINCREMENT,
                macro_id INTEGER NOT NULL,
                ordinal  INTEGER NOT NULL,
                key_code INTEGER NOT NULL,
                is_press INTEGER NOT NULL,
                delay_ms INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS button_bindings (
                owner_kind TEXT NOT NULL,
                owner_id   TEXT NOT NULL,
                vendor_id  INTEGER NOT NULL,
                product_id INTEGER NOT NULL,
                device_key TEXT NOT NULL DEFAULT '',
                button     INTEGER NOT NULL,
                action     INTEGER NOT NULL,
                macro_id   INTEGER,
                PRIMARY KEY (owner_kind, owner_id, vendor_id, product_id, device_key, button)
            );
            DROP TABLE IF EXISTS button_macro_bindings;
            -- One row per keyboard physical key that carries a macro binding: which library
            -- macro the key plays, nothing more. How it plays is recomputed from the macro
            -- itself on every resync, so the macro stays the single source of truth for its own
            -- behaviour. Keyed by matrix_index rather than a button enum because a keyboard
            -- addresses its keys by matrix slot.
            -- An earlier shape carried a redundant `mode` column. It is deliberately NOT dropped
            -- here: this block runs on every construction, so dropping the table would empty it
            -- on every app start. A pre-existing database keeps the spare column, which is
            -- harmless — it was declared NOT NULL DEFAULT 0 and the INSERT below no longer names
            -- it, so the default satisfies the constraint.
            CREATE TABLE IF NOT EXISTS key_macro_bindings (
                owner_kind   TEXT NOT NULL,
                owner_id     TEXT NOT NULL,
                vendor_id    INTEGER NOT NULL,
                product_id   INTEGER NOT NULL,
                device_key   TEXT NOT NULL DEFAULT '',
                matrix_index INTEGER NOT NULL,
                macro_id     INTEGER NOT NULL,
                PRIMARY KEY (owner_kind, owner_id, vendor_id, product_id, device_key, matrix_index)
            );
            CREATE TABLE IF NOT EXISTS device_rgb (
                owner_kind      TEXT NOT NULL,
                owner_id        TEXT NOT NULL,
                vendor_id       INTEGER NOT NULL,
                product_id      INTEGER NOT NULL,
                device_key      TEXT NOT NULL DEFAULT '',
                is_enabled      INTEGER NOT NULL DEFAULT 1,
                selected_effect TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (owner_kind, owner_id, vendor_id, product_id, device_key)
            );
            CREATE TABLE IF NOT EXISTS device_rgb_effect (
                owner_kind TEXT NOT NULL,
                owner_id   TEXT NOT NULL,
                vendor_id  INTEGER NOT NULL,
                product_id INTEGER NOT NULL,
                device_key TEXT NOT NULL DEFAULT '',
                effect_key TEXT NOT NULL,
                hex        TEXT NOT NULL,
                brightness REAL NOT NULL,
                speed      REAL NOT NULL,
                is_rainbow INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (owner_kind, owner_id, vendor_id, product_id, device_key, effect_key)
            );
            CREATE TABLE IF NOT EXISTS device_settings (
                owner_kind TEXT NOT NULL,
                owner_id   TEXT NOT NULL,
                vendor_id  INTEGER NOT NULL,
                product_id INTEGER NOT NULL,
                device_key TEXT NOT NULL DEFAULT '',
                key        TEXT NOT NULL,
                value      REAL NOT NULL,
                PRIMARY KEY (owner_kind, owner_id, vendor_id, product_id, device_key, key)
            );
            CREATE TABLE IF NOT EXISTS app_preferences (
                owner_kind TEXT NOT NULL,
                owner_id   TEXT NOT NULL,
                key        TEXT NOT NULL,
                value      REAL NOT NULL,
                PRIMARY KEY (owner_kind, owner_id, key)
            );
            -- Presets store per-device saved snapshots (name + description). The bundled
            -- settings (DPI / RGB / macros / key bindings) reuse the existing per-device
            -- tables joined via preset_id in future work; the current table only holds the
            -- label pair the New Preset dialog collects.
            -- device_key is vestigial: presets used to be scoped per physical unit, now they're
            -- scoped per device type (owner_kind, owner_id, vendor_id, product_id) so units of the
            -- same model share one list. Column kept (always '') rather than dropped; nothing
            -- writes a real value to it anymore. No migration for pre-rescope DBs (dev-only data —
            -- delete the local proovegaming.db file if old per-unit presets look wrong/duplicated).
            CREATE TABLE IF NOT EXISTS presets (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                owner_kind  TEXT NOT NULL,
                owner_id    TEXT NOT NULL,
                vendor_id   INTEGER NOT NULL,
                product_id  INTEGER NOT NULL,
                device_key  TEXT NOT NULL DEFAULT '',
                name        TEXT NOT NULL,
                description TEXT NOT NULL DEFAULT '',
                created_at  TEXT NOT NULL,
                -- Client-generated sync identity (API v2.0); '' only transiently, before the
                -- constructor backfill assigns a GUID. server_updated_at is the server's
                -- updated_at echoed verbatim (opaque string); NULL = never synced.
                uid               TEXT NOT NULL DEFAULT '',
                server_updated_at TEXT
            );
            -- Preset snapshots: SAVE PRESET copies the current device_rgb / device_rgb_effect /
            -- button_bindings rows for the active (owner, device) into these tables, scoped by
            -- preset_id. Macros stay in the shared macros library and are referenced by
            -- preset_button_bindings.macro_id.
            -- selected_effect / effect_key are stored as TEXT to mirror the existing
            -- device_rgb / device_rgb_effect tables. Effect keys are the well-known
            -- identifiers baked into DeviceDetailsViewModel.Rgb.cs (Static / Breathing /
            -- Neon / ColorCycle / Wave); introducing a lookup table would require the
            -- device tables to migrate too and buys no cross-preset normalisation because
            -- the same key can carry different colour/brightness/speed per preset.
            -- FK ON DELETE CASCADE handles snapshot cleanup on preset delete for fresh
            -- installs (PRAGMA foreign_keys=ON is set in the constructor). DBs created
            -- before the FK was added still exist; DeletePreset does the same cascade
            -- manually so both paths converge.
            CREATE TABLE IF NOT EXISTS preset_rgb (
                preset_id       INTEGER PRIMARY KEY,
                is_enabled      INTEGER NOT NULL DEFAULT 1,
                selected_effect TEXT NOT NULL DEFAULT '',
                FOREIGN KEY (preset_id) REFERENCES presets(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS preset_rgb_effect (
                preset_id  INTEGER NOT NULL,
                effect_key TEXT NOT NULL,
                hex        TEXT NOT NULL,
                brightness REAL NOT NULL,
                speed      REAL NOT NULL,
                is_rainbow INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (preset_id, effect_key),
                FOREIGN KEY (preset_id) REFERENCES presets(id) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS preset_button_bindings (
                preset_id INTEGER NOT NULL,
                button    INTEGER NOT NULL,
                action    INTEGER NOT NULL,
                macro_id  INTEGER,
                PRIMARY KEY (preset_id, button),
                FOREIGN KEY (preset_id) REFERENCES presets(id) ON DELETE CASCADE
            );
            -- Snapshot of device_settings (debounce / lift-off / DPI table / active stage / report
            -- rate) for a preset, so "other settings" are preset-scoped like RGB and bindings.
            CREATE TABLE IF NOT EXISTS preset_settings (
                preset_id INTEGER NOT NULL,
                key       TEXT NOT NULL,
                value     REAL NOT NULL,
                PRIMARY KEY (preset_id, key),
                FOREIGN KEY (preset_id) REFERENCES presets(id) ON DELETE CASCADE
            );
            -- Keyboard key remap overrides (app-truth). One row per remapped physical key,
            -- keyed by its (catalog-stable) HID scancode; the target is the mapping to write.
            CREATE TABLE IF NOT EXISTS key_bindings (
                owner_kind    TEXT NOT NULL,
                owner_id      TEXT NOT NULL,
                vendor_id     INTEGER NOT NULL,
                product_id    INTEGER NOT NULL,
                device_key    TEXT NOT NULL DEFAULT '',
                phys_scancode INTEGER NOT NULL,
                type_byte     INTEGER NOT NULL,
                bitmask       INTEGER NOT NULL,
                target_code   INTEGER NOT NULL,
                PRIMARY KEY (owner_kind, owner_id, vendor_id, product_id, device_key, phys_scancode)
            );
            CREATE TABLE IF NOT EXISTS preset_key_bindings (
                preset_id     INTEGER NOT NULL,
                phys_scancode INTEGER NOT NULL,
                type_byte     INTEGER NOT NULL,
                bitmask       INTEGER NOT NULL,
                target_code   INTEGER NOT NULL,
                PRIMARY KEY (preset_id, phys_scancode),
                FOREIGN KEY (preset_id) REFERENCES presets(id) ON DELETE CASCADE
            );
            -- Snapshot counterpart of key_macro_bindings. Keyed by matrix slot rather than
            -- scancode, matching the live table: a macro binding names a firmware slot, not a HID
            -- key. No FK to macros — a snapshot may outlive the macro it points at, and
            -- ResyncKeyMacroBindingsAsync already drops a binding whose macro is gone.
            CREATE TABLE IF NOT EXISTS preset_key_macro_bindings (
                preset_id    INTEGER NOT NULL,
                matrix_index INTEGER NOT NULL,
                macro_id     INTEGER NOT NULL,
                PRIMARY KEY (preset_id, matrix_index),
                FOREIGN KEY (preset_id) REFERENCES presets(id) ON DELETE CASCADE
            );
            -- Key actuation (press/release depth, Rapid Trigger) for the magnetic boards, app-truth.
            -- slot >= 0 is a matrix slot; slot = AllKeysActuationSlot (-1) means the board was
            -- written uniformly and reports no per-key map. Actuation used to live only in the
            -- board's flash; it is stored here so a preset can carry it like every other section.
            CREATE TABLE IF NOT EXISTS device_actuation (
                owner_kind             TEXT NOT NULL,
                owner_id               TEXT NOT NULL,
                vendor_id              INTEGER NOT NULL,
                product_id             INTEGER NOT NULL,
                device_key             TEXT NOT NULL DEFAULT '',
                slot                   INTEGER NOT NULL,
                press_centi_mm         INTEGER NOT NULL,
                release_centi_mm       INTEGER NOT NULL,
                rapid_trigger          INTEGER NOT NULL,
                quick_press_centi_mm   INTEGER NOT NULL,
                quick_release_centi_mm INTEGER NOT NULL,
                PRIMARY KEY (owner_kind, owner_id, vendor_id, product_id, device_key, slot)
            );
            -- Snapshot counterpart of device_actuation, same slot convention. A preset with no rows
            -- here writes no actuation at all, which is what keeps presets created before actuation
            -- was preset-scoped from resetting a tuned board.
            CREATE TABLE IF NOT EXISTS preset_actuation (
                preset_id              INTEGER NOT NULL,
                slot                   INTEGER NOT NULL,
                press_centi_mm         INTEGER NOT NULL,
                release_centi_mm       INTEGER NOT NULL,
                rapid_trigger          INTEGER NOT NULL,
                quick_press_centi_mm   INTEGER NOT NULL,
                quick_release_centi_mm INTEGER NOT NULL,
                PRIMARY KEY (preset_id, slot),
                FOREIGN KEY (preset_id) REFERENCES presets(id) ON DELETE CASCADE
            );
            -- Extended Keys (Rapid Snap / SOCD / Mod-Tap / Toggle) of the Smart Infinite magnetic
            -- boards, app-truth mirror of the board's advanced-key records. One wide row per record
            -- serves all four modes (docs/plans/add-cyrex-extended-keys): mode is the firmware TYPE
            -- byte; slot1/slot2 are matrix slots with slot1 < slot2 for the pair modes and slot2 = -1
            -- for the single-key ones; priority is SOCD-only; time_ms is the MT/TGL threshold;
            -- hold_*/tap_* are the MT/TGL outputs as matrix records {type, code1, code2}. A slot
            -- belongs to at most one record, so slot1 alone keys the row.
            CREATE TABLE IF NOT EXISTS device_extended_keys (
                owner_kind  TEXT NOT NULL,
                owner_id    TEXT NOT NULL,
                vendor_id   INTEGER NOT NULL,
                product_id  INTEGER NOT NULL,
                device_key  TEXT NOT NULL DEFAULT '',
                mode        INTEGER NOT NULL,
                slot1       INTEGER NOT NULL,
                slot2       INTEGER NOT NULL,
                priority    INTEGER NOT NULL,
                time_ms     INTEGER NOT NULL,
                hold_type   INTEGER NOT NULL,
                hold_code1  INTEGER NOT NULL,
                hold_code2  INTEGER NOT NULL,
                tap_type    INTEGER NOT NULL,
                tap_code1   INTEGER NOT NULL,
                tap_code2   INTEGER NOT NULL,
                PRIMARY KEY (owner_kind, owner_id, vendor_id, product_id, device_key, slot1)
            );
            -- Snapshot counterpart of device_extended_keys. Unlike actuation an EMPTY snapshot is a
            -- real opinion ("no extended keys"): applying it removes the board's managed records.
            CREATE TABLE IF NOT EXISTS preset_extended_keys (
                preset_id   INTEGER NOT NULL,
                mode        INTEGER NOT NULL,
                slot1       INTEGER NOT NULL,
                slot2       INTEGER NOT NULL,
                priority    INTEGER NOT NULL,
                time_ms     INTEGER NOT NULL,
                hold_type   INTEGER NOT NULL,
                hold_code1  INTEGER NOT NULL,
                hold_code2  INTEGER NOT NULL,
                tap_type    INTEGER NOT NULL,
                tap_code1   INTEGER NOT NULL,
                tap_code2   INTEGER NOT NULL,
                PRIMARY KEY (preset_id, slot1),
                FOREIGN KEY (preset_id) REFERENCES presets(id) ON DELETE CASCADE
            );
            -- Tracks the last preset pushed to a physical device, so a fresh device gets its
            -- default preset applied once on connect and is not re-pushed on every open.
            CREATE TABLE IF NOT EXISTS device_applied_preset (
                owner_kind TEXT NOT NULL,
                owner_id   TEXT NOT NULL,
                vendor_id  INTEGER NOT NULL,
                product_id INTEGER NOT NULL,
                device_key TEXT NOT NULL DEFAULT '',
                preset_id  INTEGER NOT NULL,
                PRIMARY KEY (owner_kind, owner_id, vendor_id, product_id, device_key)
            );
            -- The bell's history, owner-scoped like every other persisted table. Title/subtitle are
            -- stored as displayed (already localized): entries are history, not live resources, so
            -- a later language switch leaves old rows in the language they happened in.
            CREATE TABLE IF NOT EXISTS notifications (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                owner_kind  TEXT NOT NULL,
                owner_id    TEXT NOT NULL,
                title       TEXT NOT NULL,
                subtitle    TEXT NOT NULL DEFAULT '',
                icon_data   TEXT NOT NULL DEFAULT '',
                icon_uri    TEXT NOT NULL DEFAULT '',
                icon_width  REAL NOT NULL DEFAULT 22,
                icon_height REAL NOT NULL DEFAULT 12,
                created_at  TEXT NOT NULL,
                is_read     INTEGER NOT NULL DEFAULT 0,
                -- Notifications sync (docs/plans/add-api-v21-notifications-sync): server_id is
                -- the api/notifications/* row id (NULL = never synced); type is the
                -- NotificationTypes slug; is_dirty/local_rev are the live-sync tracking pair.
                -- Pre-sync rows keep server_id NULL AND is_dirty 0, so they stay local-only.
                server_id   INTEGER,
                type        TEXT NOT NULL DEFAULT '',
                is_dirty    INTEGER NOT NULL DEFAULT 0,
                local_rev   INTEGER NOT NULL DEFAULT 0
            );
            -- Live sync (docs/plans/add-api-v2-live-sync): a deleted user-owned preset/macro
            -- the server knows about (server_updated_at was non-NULL) leaves a tombstone here
            -- until SyncCoordinator's server delete succeeds or answers *_NOT_FOUND. A row the
            -- server never saw is deleted without one — there is nothing remote to remove.
            CREATE TABLE IF NOT EXISTS sync_tombstones (
                owner_kind        TEXT NOT NULL,
                owner_id          TEXT NOT NULL,
                kind              TEXT NOT NULL,
                uid               TEXT NOT NULL,
                vendor_id         INTEGER NOT NULL,
                product_id        INTEGER NOT NULL,
                server_updated_at TEXT,
                PRIMARY KEY (owner_kind, owner_id, kind, uid)
            );
            -- Owner-scoped sync key/value state: preferences.dirty / preferences.updated_at
            -- and the per-device-type devicestate.<vid>:<pid>.dirty / .updated_at pairs — the
            -- dirty flags and echoed server timestamps for the two synced states that have no
            -- row of their own to carry them.
            CREATE TABLE IF NOT EXISTS sync_meta (
                owner_kind TEXT NOT NULL,
                owner_id   TEXT NOT NULL,
                key        TEXT NOT NULL,
                value      TEXT NOT NULL,
                PRIMARY KEY (owner_kind, owner_id, key)
            );
            """;
        cmd.ExecuteNonQuery();

        // Migration: devices became owner-scoped so a signed-in user's cards survive restarts
        // (previously the table was implicitly guest-session-scoped and sign-in wiped it whole).
        // Pre-existing rows default to guest ownership — exactly the semantics they had.
        AddColumnIfMissing("devices", "owner_kind", "TEXT NOT NULL DEFAULT 'guest'");
        AddColumnIfMissing("devices", "owner_id", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("devices", "device_key", "TEXT NOT NULL DEFAULT ''");
        // Migration: the device image's render scale. Older rows default to 0, which the ViewModel
        // maps to 1.0 — exactly the behaviour they had before the column existed.
        AddColumnIfMissing("devices", "image_scale", "REAL NOT NULL DEFAULT 0");

        // Migration: a merged-transport card's row is keyed by (owner, name), not by the routing
        // identity — see AddDevice. Databases written before the column existed can already hold
        // one row per transport for a single keyboard (the reported duplicate RAMPAGE card), so the
        // heal below runs exactly once, on the construction that adds the column.
        if (AddColumnIfMissing("devices", "merge_transports", "INTEGER NOT NULL DEFAULT 0"))
            HealDuplicateDeviceRows();

        // Migration: a card now records the settings bucket it was persisted under, so a card
        // restored before discovery reports in reads the rows it actually wrote instead of guessing
        // the bucket from its routing identity. Added additively; 0 / '' on a pre-existing row means
        // "unknown", which RecordToViewModel maps back to the pre-migration derivation.
        AddColumnIfMissing("devices", "settings_vendor_id", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("devices", "settings_product_id", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("devices", "settings_device_key", "TEXT NOT NULL DEFAULT ''");
        // 0 = nothing announced this discharge; see DeviceRecord.LowBatteryAnnouncedBand.
        AddColumnIfMissing("devices", "low_battery_announced_band", "INTEGER NOT NULL DEFAULT 0");

        // Migration: add is_rainbow to device_rgb_effect for DBs created before it existed.
        AddColumnIfMissing("device_rgb_effect", "is_rainbow", "INTEGER NOT NULL DEFAULT 0");
        // Migration: add is_default to presets. Older DBs will have all zeros; the first
        // load per device will elect (or create) a default via the ViewModel path.
        AddColumnIfMissing("presets", "is_default", "INTEGER NOT NULL DEFAULT 0");

        // Migration: macros became per-device. Columns are added additively (never a DROP —
        // this block runs on every construction), then the one-time pass below assigns each
        // pre-scoping macro to the device that binds it. Note this supersedes the "fresh start"
        // wipe that #70 shipped: dropping every pre-scoping macro was the cheaper option, but the
        // user chose to keep macros that a binding can attribute to a device and drop only the
        // unreferenced ones, so an existing library survives the upgrade.
        AddColumnIfMissing("macros", "vendor_id", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("macros", "product_id", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("macros", "device_key", "TEXT NOT NULL DEFAULT ''");

        // Migration: presets and macros carry a client-generated uid plus the server's echoed
        // updated_at for API v2.0 sync (docs/plans/add-api-v2-sync). The columns are added
        // BEFORE MigrateMacrosToDevices (whose CopyMacroForDevice names uid) and the backfill
        // runs after it, so rows that migration touches are covered too. Every step is
        // idempotent, so a re-run finds nothing to do; the unique indexes are created only
        // after the backfill so a legacy DB's ''-valued rows can never trip them.
        AddColumnIfMissing("presets", "uid", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("presets", "server_updated_at", "TEXT");
        AddColumnIfMissing("macros", "uid", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("macros", "server_updated_at", "TEXT");

        // Migration: live-sync dirty tracking (docs/plans/add-api-v2-live-sync). is_dirty
        // marks a user-owned row whose latest content the server has not confirmed;
        // local_rev increments on every mutation so a push can clear is_dirty only when the
        // row was not re-edited while the push was in flight (see ClearMacroDirty).
        AddColumnIfMissing("presets", "is_dirty", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("presets", "local_rev", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("macros", "is_dirty", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("macros", "local_rev", "INTEGER NOT NULL DEFAULT 0");

        // Migration: notifications sync (docs/plans/add-api-v21-notifications-sync). Defaults
        // make pre-sync rows local-only forever: server_id NULL and is_dirty 0 means nothing
        // enumerates them for a push, and '' type renders exactly as before.
        AddColumnIfMissing("notifications", "server_id", "INTEGER");
        AddColumnIfMissing("notifications", "type", "TEXT NOT NULL DEFAULT ''");
        AddColumnIfMissing("notifications", "is_dirty", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing("notifications", "local_rev", "INTEGER NOT NULL DEFAULT 0");

        // Migration: the guest session-expiry warning's once-per-session flag
        // (docs/plans/add-guest-session-expiry-warning). A DB written before it defaults to 0,
        // which is exactly right — an in-flight session simply gets its warning.
        AddColumnIfMissing("app_session", "expiry_warning_sent", "INTEGER NOT NULL DEFAULT 0");

        MigrateMacrosToDevices();

        BackfillMissingUids("presets");
        BackfillMissingUids("macros");
        using (var indexes = _connection.CreateCommand())
        {
            indexes.CommandText = """
                CREATE UNIQUE INDEX IF NOT EXISTS idx_presets_uid ON presets(uid);
                CREATE UNIQUE INDEX IF NOT EXISTS idx_macros_uid ON macros(uid);
                """;
            indexes.ExecuteNonQuery();
        }

        // Migration: preset_settings is a new table. Without this, every preset that existed before
        // it shipped has an EMPTY snapshot — switching to one wipes device_settings (DPI table,
        // report rate, lift-off) and applies each section's catalog default to the hardware, which
        // reads as "the mouse reset itself" even though the user only switched presets. Seed each
        // existing preset with its device's CURRENT device_settings once, so switching to an old
        // preset returns exactly what was there, not defaults. (No equivalent is needed for
        // preset_key_bindings: key_bindings is also new, so there is no prior data to seed from —
        // keyboard key remaps simply start unset, matching pre-branch behaviour where they were
        // never persisted at all.)
        if (presetSettingsIsNew)
            SeedPresetSettingsFromLiveDeviceSettings();

        if (presetKeyMacroBindingsIsNew)
            SeedPresetKeyMacroBindingsFromLiveBindings();
    }

    // One-time backfill for the preset_settings migration above. Every preset (regardless of owner
    // or device) is seeded from its own (owner, device) scope's current device_settings rows — so
    // presets sharing a device start from that device's live settings until each preset is next
    // saved with its own values.
    // One-time backfill for the preset_key_macro_bindings migration. Without it the first preset
    // switch after upgrading would wipe every existing key→macro binding: the restore deletes the
    // live rows for the scope and copies back from a snapshot that does not exist yet.
    //
    // Every preset for a device is seeded from that device's current bindings, so presets sharing
    // a device start identical until each is next saved — the same compromise
    // SeedPresetSettingsFromLiveDeviceSettings makes, and strictly better than losing the data.
    // Runs from EnsureSchema (constructor), which is outside _lock, so it must not take it.
    private void SeedPresetKeyMacroBindingsFromLiveBindings()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO preset_key_macro_bindings (preset_id, matrix_index, macro_id)
            SELECT p.id, kmb.matrix_index, kmb.macro_id
            FROM presets p
            JOIN key_macro_bindings kmb
                ON kmb.owner_kind = p.owner_kind AND kmb.owner_id = p.owner_id
               AND kmb.vendor_id = p.vendor_id AND kmb.product_id = p.product_id
               AND kmb.device_key = p.device_key;
            """;
        cmd.ExecuteNonQuery();
    }

    private void SeedPresetSettingsFromLiveDeviceSettings()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO preset_settings (preset_id, key, value)
            SELECT p.id, ds.key, ds.value
            FROM presets p
            JOIN device_settings ds
                ON ds.owner_kind = p.owner_kind AND ds.owner_id = p.owner_id
               AND ds.vendor_id = p.vendor_id AND ds.product_id = p.product_id
               AND ds.device_key = p.device_key;
            """;
        cmd.ExecuteNonQuery();
    }

    private bool TableExists(string tableName)
    {
        using var check = _connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $t";
        check.Parameters.AddWithValue("$t", tableName);
        return Convert.ToInt64(check.ExecuteScalar()) > 0;
    }

    private bool ColumnExists(string table, string column)
    {
        using var check = _connection.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $c";
        check.Parameters.AddWithValue("$c", column);
        return Convert.ToInt64(check.ExecuteScalar()) > 0;
    }

    // One-time backfill for the uid migration above: assigns a fresh GUID to every row that
    // predates the column. Row-by-row because SQLite cannot generate GUIDs in SQL; row counts
    // are tiny (a user's presets/macros), and one transaction keeps it a single commit.
    // Runs from EnsureSchema (constructor), which is outside _lock, so it must not take it.
    private void BackfillMissingUids(string table)
    {
        var pending = new List<int>();
        using (var find = _connection.CreateCommand())
        {
            find.CommandText = $"SELECT id FROM {table} WHERE uid = ''";
            using var reader = find.ExecuteReader();
            while (reader.Read())
                pending.Add(reader.GetInt32(0));
        }

        if (pending.Count == 0) return;

        using var transaction = _connection.BeginTransaction();
        foreach (var id in pending)
        {
            using var update = _connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = $"UPDATE {table} SET uid = $u WHERE id = $id";
            update.Parameters.AddWithValue("$u", Guid.NewGuid().ToString("D"));
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    /// <summary>
    /// Assigns every pre-scoping macro (<c>vendor_id = 0</c>) to the device that references it,
    /// duplicating the macro when more than one device does, and deleting it when none does.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Idempotent by construction rather than by a version marker: after a successful pass no row
    /// has <c>vendor_id = 0</c> — assigned rows carry real ids and unreferenced rows are gone — so
    /// re-running finds nothing. A pass interrupted part-way leaves the remainder still marked, so
    /// the next construction resumes rather than double-processing what already moved.
    /// </para>
    /// <para>
    /// Three tables reference a macro. <c>preset_button_bindings</c> is included deliberately: it
    /// holds <c>macro_id</c> with no foreign key and <see cref="DeleteMacro"/> does not clear it,
    /// so a preset can already outlive its macro. Treating a preset reference as owning keeps this
    /// migration from widening that.
    /// </para>
    /// </remarks>
    private void MigrateMacrosToDevices()
    {
        var pending = new List<int>();
        using (var find = _connection.CreateCommand())
        {
            find.CommandText = "SELECT id FROM macros WHERE vendor_id = 0";
            using var reader = find.ExecuteReader();
            while (reader.Read())
                pending.Add(reader.GetInt32(0));
        }

        if (pending.Count == 0) return;

        using var transaction = _connection.BeginTransaction();

        foreach (var macroId in pending)
        {
            var devices = OwningDevicesForMacro(transaction, macroId);

            if (devices.Count == 0)
            {
                DeleteMacroCore(transaction, macroId);
                continue;
            }

            // The first owner keeps the original row, so its id — and therefore every reference
            // to it — stays valid. Any further owners get their own copy.
            StampMacroDevice(transaction, macroId, devices[0]);

            for (int i = 1; i < devices.Count; i++)
            {
                var copyId = CopyMacroForDevice(transaction, macroId, devices[i]);
                RepointMacroReferences(transaction, macroId, devices[i], copyId);
            }
        }

        transaction.Commit();
    }

    // Distinct devices that reference this macro, across all three reference sources. Ordered so
    // the migration is deterministic: whichever device sorts first keeps the original row.
    private List<(int VendorId, int ProductId, string DeviceKey)> OwningDevicesForMacro(
        SqliteTransaction transaction, int macroId)
    {
        var result = new List<(int, int, string)>();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT DISTINCT vendor_id, product_id, device_key FROM (
                SELECT vendor_id, product_id, device_key FROM button_bindings WHERE macro_id = $m
                UNION
                SELECT vendor_id, product_id, device_key FROM key_macro_bindings WHERE macro_id = $m
                UNION
                SELECT p.vendor_id, p.product_id, p.device_key
                    FROM preset_button_bindings pb
                    JOIN presets p ON p.id = pb.preset_id
                    WHERE pb.macro_id = $m
            )
            ORDER BY vendor_id, product_id, device_key;
            """;
        cmd.Parameters.AddWithValue("$m", macroId);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            result.Add((reader.GetInt32(0), reader.GetInt32(1), reader.GetString(2)));
        return result;
    }

    private void StampMacroDevice(
        SqliteTransaction transaction, int macroId, (int VendorId, int ProductId, string DeviceKey) device)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "UPDATE macros SET vendor_id = $v, product_id = $p, device_key = $dk WHERE id = $id";
        cmd.Parameters.AddWithValue("$v", device.VendorId);
        cmd.Parameters.AddWithValue("$p", device.ProductId);
        cmd.Parameters.AddWithValue("$dk", device.DeviceKey);
        cmd.Parameters.AddWithValue("$id", macroId);
        cmd.ExecuteNonQuery();
    }

    // Duplicates the macro row and its events for another owning device, returning the new id.
    // Ordinals are carried across so playback order survives.
    private int CopyMacroForDevice(
        SqliteTransaction transaction, int macroId, (int VendorId, int ProductId, string DeviceKey) device)
    {
        int newId;
        using (var insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO macros (owner_kind, owner_id, vendor_id, product_id, device_key, name, repeat_count, repeat_mode, created_at, uid)
                SELECT owner_kind, owner_id, $v, $p, $dk, name, repeat_count, repeat_mode, created_at, $u
                    FROM macros WHERE id = $id;
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$v", device.VendorId);
            insert.Parameters.AddWithValue("$p", device.ProductId);
            insert.Parameters.AddWithValue("$dk", device.DeviceKey);
            insert.Parameters.AddWithValue("$id", macroId);
            // A copy is a new sync identity — carrying the source uid across would trip
            // idx_macros_uid (and lie to the server about which record this is).
            insert.Parameters.AddWithValue("$u", Guid.NewGuid().ToString("D"));
            newId = Convert.ToInt32(insert.ExecuteScalar());
        }

        using (var events = _connection.CreateCommand())
        {
            events.Transaction = transaction;
            events.CommandText = """
                INSERT INTO macro_events (macro_id, ordinal, key_code, is_press, delay_ms)
                SELECT $new, ordinal, key_code, is_press, delay_ms FROM macro_events WHERE macro_id = $old;
                """;
            events.Parameters.AddWithValue("$new", newId);
            events.Parameters.AddWithValue("$old", macroId);
            events.ExecuteNonQuery();
        }

        return newId;
    }

    // Points one device's references at its own copy, leaving the other devices' references on
    // the original row.
    private void RepointMacroReferences(
        SqliteTransaction transaction, int oldMacroId,
        (int VendorId, int ProductId, string DeviceKey) device, int newMacroId)
    {
        foreach (var sql in new[]
        {
            "UPDATE button_bindings SET macro_id = $new WHERE macro_id = $old AND vendor_id = $v AND product_id = $p AND device_key = $dk",
            "UPDATE key_macro_bindings SET macro_id = $new WHERE macro_id = $old AND vendor_id = $v AND product_id = $p AND device_key = $dk",
            """
            UPDATE preset_button_bindings SET macro_id = $new
            WHERE macro_id = $old AND preset_id IN (
                SELECT id FROM presets WHERE vendor_id = $v AND product_id = $p AND device_key = $dk
            )
            """,
        })
        {
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("$new", newMacroId);
            cmd.Parameters.AddWithValue("$old", oldMacroId);
            cmd.Parameters.AddWithValue("$v", device.VendorId);
            cmd.Parameters.AddWithValue("$p", device.ProductId);
            cmd.Parameters.AddWithValue("$dk", device.DeviceKey);
            cmd.ExecuteNonQuery();
        }
    }

    // Macro + events removal shared by the migration and DeleteMacro.
    private void DeleteMacroCore(SqliteTransaction transaction, int macroId)
    {
        using (var events = _connection.CreateCommand())
        {
            events.Transaction = transaction;
            events.CommandText = "DELETE FROM macro_events WHERE macro_id = $id";
            events.Parameters.AddWithValue("$id", macroId);
            events.ExecuteNonQuery();
        }

        using var macro = _connection.CreateCommand();
        macro.Transaction = transaction;
        macro.CommandText = "DELETE FROM macros WHERE id = $id";
        macro.Parameters.AddWithValue("$id", macroId);
        macro.ExecuteNonQuery();
    }

    // Adds a column when a pre-existing table lacks it (SQLite has no ADD COLUMN IF NOT EXISTS).
    // Returns true when it actually added the column (i.e. an older DB was migrated).
    private bool AddColumnIfMissing(string table, string column, string definition)
    {
        if (ColumnExists(table, column))
            return false;

        using var alter = _connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        alter.ExecuteNonQuery();
        return true;
    }

    // One-time heal for databases written before merge_transports existed: they hold one devices
    // row per transport for a merged product, and the extra row surfaces as a permanent
    // "NO CONNECTION" card that live discovery can never adopt (it matches merged products by name
    // alone). Run once, from EnsureSchema, on the construction that adds the column.
    //
    // The heuristic is deliberately narrower than "same name = same card": a group of rows sharing
    // (owner, name) is only collapsed when it holds TWO OR MORE DISTINCT (vendor_id, product_id)
    // pairs, which is the fingerprint of one device seen over two transports. The survivor is the
    // highest id: the transport persisted most recently.
    //
    // Two identical physical units share one pair and differ only by device_key (PR #112 M2), so a
    // group holding nothing but such units has a single pair and is never collapsed. That alone is
    // NOT enough, though: add one extra transport row for one of those units and the group has two
    // distinct pairs, so the rule above would collapse BOTH units into one card and flag it merged
    // — a silent, irreversible deletion of a device the user owns. The bail-out below closes that
    // case. We cannot simply ask whether the product is merged: deviceservice.proto exposes no
    // catalog enumeration, so that answer exists only for hardware connected right now, which at
    // migration time is nothing. The guess is therefore made conservative instead of correct.
    //
    // EnsureSchema opens no transaction of its own, so this one does: the flag UPDATE and the row
    // DELETE must commit together. Split across two auto-commits, a crash in between would leave
    // the flags set and the duplicates present, and the heal is guarded on the column having just
    // been added — it would never run again. Runs from the constructor, outside _lock.
    private void HealDuplicateDeviceRows()
    {
        using var transaction = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            UPDATE devices SET merge_transports = 1
            WHERE EXISTS (
                SELECT 1 FROM devices AS other
                WHERE other.owner_kind = devices.owner_kind
                  AND other.owner_id = devices.owner_id
                  AND other.name = devices.name COLLATE NOCASE
                  AND (other.vendor_id <> devices.vendor_id OR other.product_id <> devices.product_id)
            )
            -- Bail-out: a group holding two rows with the SAME (vendor_id, product_id) and
            -- DIFFERENT device_key is proof of two physical units of one model
            -- (add-per-device-identity). Such a group is left entirely alone -- not flagged, and
            -- therefore not deleted by the statement below, which only removes flagged rows. The
            -- cost is that a two-unit owner keeps a ghost row until that device next connects,
            -- when AddDevice collapses it under catalog authority.
            AND NOT EXISTS (
                SELECT 1 FROM devices AS a
                JOIN devices AS b
                  ON  a.owner_kind = b.owner_kind
                  AND a.owner_id = b.owner_id
                  AND a.name = b.name COLLATE NOCASE
                  AND a.vendor_id = b.vendor_id
                  AND a.product_id = b.product_id
                  AND a.device_key <> b.device_key
                WHERE a.owner_kind = devices.owner_kind
                  AND a.owner_id = devices.owner_id
                  AND a.name = devices.name COLLATE NOCASE
            );

            -- NOT IN is safe here only because MAX(id) over an INTEGER PRIMARY KEY can never be
            -- NULL; a nullable column in this subselect would make the whole predicate NULL and
            -- silently delete nothing.
            DELETE FROM devices
            WHERE merge_transports = 1
              AND id NOT IN (
                  SELECT MAX(id) FROM devices
                  GROUP BY owner_kind, owner_id, name COLLATE NOCASE
              );
            """;
        cmd.ExecuteNonQuery();
        transaction.Commit();
    }

    // -- Settings-vendor re-key (docs/plans/fix-settings-bucket-identity) ---------

    /// <summary>
    /// Every table keyed by the settings identity, paired with the columns an
    /// <c>INSERT ... SELECT</c> must carry BEYOND the five key columns
    /// (owner_kind, owner_id, vendor_id, product_id, device_key) all of them share.
    /// Stated once, as data, so no table can be re-keyed by a hand-written statement that forgot a
    /// column -- or, far worse, forgot a WHERE clause.
    /// </summary>
    private static readonly (string Table, string KeyTail, string Payload)[] SettingsBucketTables =
    [
        ("device_rgb", "", "is_enabled, selected_effect"),
        ("device_rgb_effect", "effect_key", "hex, brightness, speed, is_rainbow"),
        ("device_settings", "key", "value"),
        ("device_actuation", "slot",
            "press_centi_mm, release_centi_mm, rapid_trigger, quick_press_centi_mm, quick_release_centi_mm"),
        ("device_extended_keys", "slot1",
            "mode, slot2, priority, time_ms, hold_type, hold_code1, hold_code2, tap_type, tap_code1, tap_code2"),
        ("button_bindings", "button", "action, macro_id"),
        ("key_bindings", "phys_scancode", "type_byte, bitmask, target_code"),
        ("key_macro_bindings", "matrix_index", "macro_id"),
        ("device_applied_preset", "", "preset_id"),
    ];

    // Scope-column tables whose rows carry a surrogate id and a UNIQUE uid, so a vendor rewrite
    // cannot collide with an existing row and needs no reconciliation.
    private static readonly string[] SettingsBucketSurrogateKeyTables = ["presets", "macros"];

    // Set once per process, under _lock, by the backup helper below.
    private bool _settingsVendorBackupTaken;

    /// <summary>
    /// Moves every settings row of product <paramref name="productId"/> from
    /// <paramref name="fromVendorId"/> to <paramref name="toVendorId"/>, so a merged-transport
    /// product whose transports enumerate under different vendors ends up with ONE settings bucket.
    /// Returns the total rows the pass affected across every statement -- 0 exactly when the
    /// alternate bucket was already empty, which is what makes a second call observably a no-op.
    /// It is a diagnostic, not a row count: an adopted row is counted by both its INSERT and its
    /// DELETE.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why it is not in <see cref="EnsureSchema"/>.</b> Every other migration here is a one-shot
    /// at construction. This one cannot be: the mapping "vendor 1389 belongs to product 10473:12888"
    /// lives in the DeviceHost catalog, and <c>deviceservice.proto</c> exposes no catalog list/get
    /// RPC -- the catalog arrives embedded on a DISCOVERED device. Construction time therefore has
    /// no way to learn the pair, so <c>HomeViewModel</c> calls this when a device reports in instead.
    /// </para>
    /// <para>
    /// <b>Why it opens its own transaction.</b> The same reason <see cref="HealDuplicateDeviceRows"/>
    /// does, only stronger: this runs in a live app, and a crash with half the tables moved would
    /// leave one device split ACROSS two buckets -- worse than the bug being fixed. One
    /// <c>BeginTransaction</c>, every table, one <c>Commit</c>; anything thrown rolls the whole pass
    /// back through the <c>using</c>.
    /// </para>
    /// <para>
    /// <b>Why it is idempotent.</b> After a pass no row remains under the alternate vendor for this
    /// product, so a re-run matches nothing and changes nothing -- the same "a re-run finds nothing"
    /// shape as <c>MigrateMacrosToDevices</c>. The per-session guard set on <c>HomeViewModel</c> is
    /// an optimisation that keeps the discovery tick off the database, never the correctness guard.
    /// </para>
    /// <para>
    /// <b>All owners, not just the signed-in one.</b> Which vendor a transport enumerates under is a
    /// property of the hardware, not of an account. Re-keying only the current owner would leave a
    /// guest's or a second account's rows split until that account happened to be active with the
    /// device connected. No statement below is owner-scoped, and that is deliberate.
    /// </para>
    /// <para>
    /// <b>Reconciliation: canonical wins, per row.</b> Both buckets can hold a row for the same key
    /// (the user configured the device on the cable AND on the dongle). <c>INSERT OR IGNORE</c> then
    /// <c>DELETE</c> means: a key that exists under the canonical vendor keeps its canonical value; a
    /// key that exists only under the alternate vendor is adopted. Nothing without a counterpart is
    /// ever lost, and no table's row count can grow. "Keep the newest" is not implementable -- none
    /// of these tables carries a timestamp.
    /// </para>
    /// <para>
    /// <b>What is NOT touched.</b> Only <c>(fromVendorId, productId)</c>. Rows whose product cannot
    /// be resolved -- a device that is not present, a vendor the catalog does not name, and the junk
    /// rows an earlier release wrote at a ROUTING product id -- are left exactly where they are. They
    /// are not deleted: a row seeded from defaults is indistinguishable from one the user edited, and
    /// stranding an unreachable row is recoverable while deleting it is not.
    /// </para>
    /// </remarks>
    public int RekeySettingsVendor(int fromVendorId, int toVendorId, int productId)
    {
        // Vendor/product 0 is the "identity unknown" sentinel used all over this file and on the
        // wire. It must never become a re-key source or target: WHERE vendor_id = 0 would sweep up
        // every pre-scoping row in the database.
        if (fromVendorId <= 0 || toVendorId <= 0 || productId <= 0 || fromVendorId == toVendorId)
            return 0;

        AcquireLock();
        try
        {
            // Inside the lock but OUTSIDE the transaction: the copy must see a quiesced file, and a
            // failed backup must not roll a migration back (nor a rolled-back migration delete the
            // backup). No other writer can touch the connection while _lock is held.
            BackupDatabaseOnce();

            using var transaction = _connection.BeginTransaction();
            var moved = 0;

            foreach (var (table, keyTail, payload) in SettingsBucketTables)
            {
                // The five shared key columns, plus this table's own key tail. device_key is carried
                // across unchanged: the re-key moves a bucket between VENDORS, never between units.
                var keyColumns = string.IsNullOrEmpty(keyTail)
                    ? "owner_kind, owner_id, vendor_id, product_id, device_key"
                    : $"owner_kind, owner_id, vendor_id, product_id, device_key, {keyTail}";
                var selectColumns = string.IsNullOrEmpty(keyTail)
                    ? "owner_kind, owner_id, $to, product_id, device_key"
                    : $"owner_kind, owner_id, $to, product_id, device_key, {keyTail}";

                // Adopt: only rows whose key has no counterpart under the canonical vendor survive
                // the OR IGNORE. Then drop the alternate bucket. Both statements carry the SAME
                // "vendor_id = $from AND product_id = $pid" predicate -- the DELETE can never be
                // scoped more loosely than the INSERT that preceded it, which is the one mistake in
                // this method that would silently destroy a whole configuration.
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = $"""
                    INSERT OR IGNORE INTO {table} ({keyColumns}, {payload})
                    SELECT {selectColumns}, {payload}
                    FROM {table} WHERE vendor_id = $from AND product_id = $pid;

                    DELETE FROM {table} WHERE vendor_id = $from AND product_id = $pid;
                    """;
                cmd.Parameters.AddWithValue("$from", fromVendorId);
                cmd.Parameters.AddWithValue("$to", toVendorId);
                cmd.Parameters.AddWithValue("$pid", productId);
                moved += cmd.ExecuteNonQuery();
            }

            // presets / macros have a surrogate id and a UNIQUE uid, so their scope columns are not
            // a key and a plain UPDATE cannot collide -- they move rather than reconcile.
            //
            // Demotion runs BEFORE the move, while the two scopes are still distinguishable: if this
            // owner's canonical scope already owns a default preset, the incoming ones are demoted,
            // because "exactly one default per (owner, vendor, product)" is what
            // CreateDefaultPresetIfNotExists and the sync pull's default arbitration both rely on.
            // The user may end with two presets named "Default" in one list -- visible, harmless, and
            // preferable to deleting one.
            using (var demote = _connection.CreateCommand())
            {
                demote.Transaction = transaction;
                demote.CommandText = """
                    UPDATE presets SET is_default = 0
                    WHERE vendor_id = $from AND product_id = $pid AND is_default = 1
                      AND EXISTS (
                          SELECT 1 FROM presets AS canonical
                          WHERE canonical.owner_kind = presets.owner_kind
                            AND canonical.owner_id = presets.owner_id
                            AND canonical.vendor_id = $to
                            AND canonical.product_id = $pid
                            AND canonical.is_default = 1
                      );
                    """;
                demote.Parameters.AddWithValue("$from", fromVendorId);
                demote.Parameters.AddWithValue("$to", toVendorId);
                demote.Parameters.AddWithValue("$pid", productId);
                demote.ExecuteNonQuery();
            }

            // is_dirty / local_rev are deliberately NOT bumped: the rows' CONTENT did not change,
            // only the local label. Marking them would push the whole preset and macro library to
            // the server on the next sync pass. uid is untouched, which is what stops the pull from
            // re-inserting these as duplicates.
            foreach (var table in SettingsBucketSurrogateKeyTables)
            {
                using var move = _connection.CreateCommand();
                move.Transaction = transaction;
                move.CommandText =
                    $"UPDATE {table} SET vendor_id = $to WHERE vendor_id = $from AND product_id = $pid;";
                move.Parameters.AddWithValue("$from", fromVendorId);
                move.Parameters.AddWithValue("$to", toVendorId);
                move.Parameters.AddWithValue("$pid", productId);
                moved += move.ExecuteNonQuery();
            }

            // sync_meta encodes the device type INTO the key string, so it re-keys by key rather than
            // by column. Same canonical-wins rule; the keys are built through the same helpers the
            // writers use, so the format lives in exactly one place.
            var deviceStateKeys = new[]
            {
                (From: SyncMetaDeviceStateDirty(fromVendorId, productId),
                    To: SyncMetaDeviceStateDirty(toVendorId, productId)),
                (From: SyncMetaDeviceStateUpdatedAt(fromVendorId, productId),
                    To: SyncMetaDeviceStateUpdatedAt(toVendorId, productId)),
            };
            foreach (var (fromKey, toKey) in deviceStateKeys)
            {
                using var meta = _connection.CreateCommand();
                meta.Transaction = transaction;
                meta.CommandText = """
                    INSERT OR IGNORE INTO sync_meta (owner_kind, owner_id, key, value)
                    SELECT owner_kind, owner_id, $toKey, value FROM sync_meta WHERE key = $fromKey;

                    DELETE FROM sync_meta WHERE key = $fromKey;
                    """;
                meta.Parameters.AddWithValue("$fromKey", fromKey);
                meta.Parameters.AddWithValue("$toKey", toKey);
                moved += meta.ExecuteNonQuery();
            }

            // sync_tombstones keys on (owner_kind, owner_id, kind, uid) -- the vendor is a plain
            // payload column, so this cannot collide either. Leaving a pending delete under the
            // alternate vendor would strand it; the server is re-labelled by the next push anyway.
            using (var tombstones = _connection.CreateCommand())
            {
                tombstones.Transaction = transaction;
                tombstones.CommandText =
                    "UPDATE sync_tombstones SET vendor_id = $to WHERE vendor_id = $from AND product_id = $pid;";
                tombstones.Parameters.AddWithValue("$from", fromVendorId);
                tombstones.Parameters.AddWithValue("$to", toVendorId);
                tombstones.Parameters.AddWithValue("$pid", productId);
                moved += tombstones.ExecuteNonQuery();
            }

            transaction.Commit();
            return moved;
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Writes a snapshot of the database next to itself as <c>&lt;name&gt;.pre-vendor-rekey.bak</c>,
    /// once per process and only when no such file exists yet, immediately before the first re-key
    /// pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The re-key touches every settings table, and a mis-scoped statement would destroy a whole
    /// configuration with no undo. A few hundred KB is the difference between "the migration had a
    /// bug" and "the configuration is gone", and the QA script tells users to recover from this
    /// exact file. Never overwritten, so an already-migrated second run cannot replace the
    /// pre-migration copy with a post-migration one, and nothing in the app deletes it.
    /// </para>
    /// <para>
    /// <b><c>VACUUM INTO</c>, not <c>File.Copy</c>.</b> A raw file copy is a consistent snapshot only
    /// under SQLite's default rollback journal, which is what this connection happens to use — the
    /// constructor sets no <c>journal_mode</c>. The day anyone enables WAL, recently committed pages
    /// live in the <c>-wal</c> sidecar and a copy of the main file alone would silently be stale or
    /// torn, and nobody would find out until they actually needed the backup. <c>VACUUM INTO</c> is
    /// transactionally consistent whatever the journal mode. It cannot run inside a transaction,
    /// which is why the caller invokes this before <c>BeginTransaction</c>.
    /// </para>
    /// <para>
    /// <b>Staged then renamed.</b> An interrupted write must never leave a truncated file where the
    /// <c>File.Exists</c> check above would afterwards mistake it for a good backup — a corrupt
    /// backup that looks valid is worse than no backup at all. <c>VACUUM INTO</c> refuses an
    /// existing target, so the staging file is cleared first.
    /// </para>
    /// <para>
    /// <b>Best-effort, but the flag is only set on success.</b> A transient failure (an antivirus or
    /// indexer lock on the file) leaves the flag clear so the next pass tries again, rather than
    /// migrating unprotected for the rest of the process. The cases that can never succeed — an
    /// in-memory store, a backup that already exists — set it, so they stop asking.
    /// Called under <c>_lock</c>, so it never races another writer on this connection.
    /// </para>
    /// </remarks>
    private void BackupDatabaseOnce()
    {
        if (_settingsVendorBackupTaken)
            return;

        try
        {
            var path = _connection.DataSource;
            // ":memory:" and the anonymous temporary database have no file to snapshot.
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                _settingsVendorBackupTaken = true;
                return;
            }

            var backup = path + ".pre-vendor-rekey.bak";
            if (File.Exists(backup))
            {
                _settingsVendorBackupTaken = true;
                return;
            }

            var staging = backup + ".tmp";
            if (File.Exists(staging))
                File.Delete(staging);

            using (var vacuum = _connection.CreateCommand())
            {
                vacuum.CommandText = "VACUUM INTO $path;";
                vacuum.Parameters.AddWithValue("$path", staging);
                vacuum.ExecuteNonQuery();
            }

            File.Move(staging, backup);
            _settingsVendorBackupTaken = true;
        }
        catch (Exception)
        {
            // Deliberately swallowed -- see the remarks above.
        }
    }

    // ── RGB state (per owner, per physical device) ────────────────────────────────

    public RgbStateRecord? GetRgbState(string ownerKind, string ownerId, int vendorId, int productId, string deviceKey)
    {
        deviceKey ??= string.Empty;
        AcquireLock();
        try
        {
            RgbStateRecord? state = null;
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT is_enabled, selected_effect FROM device_rgb WHERE owner_kind = $ok AND owner_id = $oi AND vendor_id = $v AND product_id = $p AND device_key = $dk";
                cmd.Parameters.AddWithValue("$ok", ownerKind);
                cmd.Parameters.AddWithValue("$oi", ownerId);
                cmd.Parameters.AddWithValue("$v", vendorId);
                cmd.Parameters.AddWithValue("$p", productId);
                cmd.Parameters.AddWithValue("$dk", deviceKey);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                    state = new RgbStateRecord { IsEnabled = reader.GetInt32(0) != 0, SelectedEffect = reader.GetString(1) };
            }

            if (state == null) return null;

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT effect_key, hex, brightness, speed, is_rainbow FROM device_rgb_effect WHERE owner_kind = $ok AND owner_id = $oi AND vendor_id = $v AND product_id = $p AND device_key = $dk";
                cmd.Parameters.AddWithValue("$ok", ownerKind);
                cmd.Parameters.AddWithValue("$oi", ownerId);
                cmd.Parameters.AddWithValue("$v", vendorId);
                cmd.Parameters.AddWithValue("$p", productId);
                cmd.Parameters.AddWithValue("$dk", deviceKey);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    state.Effects.Add(new RgbEffectRecord
                    {
                        EffectKey = reader.GetString(0),
                        Hex = reader.GetString(1),
                        Brightness = reader.GetDouble(2),
                        Speed = reader.GetDouble(3),
                        IsRainbow = reader.GetInt32(4) != 0,
                    });
            }

            return state;
        }
        finally { _lock.Release(); }
    }

    public void SaveRgbState(string ownerKind, string ownerId, int vendorId, int productId, string deviceKey, bool isEnabled, string selectedEffect, IReadOnlyList<RgbEffectRecord> effects)
    {
        deviceKey ??= string.Empty;
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();

            using (var head = _connection.CreateCommand())
            {
                head.Transaction = transaction;
                head.CommandText = """
                    INSERT INTO device_rgb (owner_kind, owner_id, vendor_id, product_id, device_key, is_enabled, selected_effect)
                    VALUES ($ok, $oi, $v, $p, $dk, $e, $s)
                    ON CONFLICT(owner_kind, owner_id, vendor_id, product_id, device_key)
                        DO UPDATE SET is_enabled = excluded.is_enabled, selected_effect = excluded.selected_effect;
                    """;
                head.Parameters.AddWithValue("$ok", ownerKind);
                head.Parameters.AddWithValue("$oi", ownerId);
                head.Parameters.AddWithValue("$v", vendorId);
                head.Parameters.AddWithValue("$p", productId);
                head.Parameters.AddWithValue("$dk", deviceKey);
                head.Parameters.AddWithValue("$e", isEnabled ? 1 : 0);
                head.Parameters.AddWithValue("$s", selectedEffect);
                head.ExecuteNonQuery();
            }

            foreach (var e in effects)
            {
                using var ev = _connection.CreateCommand();
                ev.Transaction = transaction;
                ev.CommandText = """
                    INSERT INTO device_rgb_effect (owner_kind, owner_id, vendor_id, product_id, device_key, effect_key, hex, brightness, speed, is_rainbow)
                    VALUES ($ok, $oi, $v, $p, $dk, $k, $hex, $b, $sp, $rb)
                    ON CONFLICT(owner_kind, owner_id, vendor_id, product_id, device_key, effect_key)
                        DO UPDATE SET hex = excluded.hex, brightness = excluded.brightness,
                                      speed = excluded.speed, is_rainbow = excluded.is_rainbow;
                    """;
                ev.Parameters.AddWithValue("$ok", ownerKind);
                ev.Parameters.AddWithValue("$oi", ownerId);
                ev.Parameters.AddWithValue("$v", vendorId);
                ev.Parameters.AddWithValue("$p", productId);
                ev.Parameters.AddWithValue("$dk", deviceKey);
                ev.Parameters.AddWithValue("$k", e.EffectKey);
                ev.Parameters.AddWithValue("$hex", e.Hex);
                ev.Parameters.AddWithValue("$b", e.Brightness);
                ev.Parameters.AddWithValue("$sp", e.Speed);
                ev.Parameters.AddWithValue("$rb", e.IsRainbow ? 1 : 0);
                ev.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        finally { _lock.Release(); }
    }

    // ── Device settings (per owner, per physical device, key → value) ────────────

    public Dictionary<string, double> GetDeviceSettings(string ownerKind, string ownerId, int vendorId, int productId, string deviceKey)
    {
        AcquireLock();
        try
        {
            var result = new Dictionary<string, double>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM device_settings WHERE owner_kind = $ok AND owner_id = $oi AND vendor_id = $v AND product_id = $p AND device_key = $dk";
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$v", vendorId);
            cmd.Parameters.AddWithValue("$p", productId);
            cmd.Parameters.AddWithValue("$dk", deviceKey ?? string.Empty);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result[reader.GetString(0)] = reader.GetDouble(1);
            return result;
        }
        finally { _lock.Release(); }
    }

    public void SetDeviceSetting(string ownerKind, string ownerId, int vendorId, int productId, string deviceKey, string key, double value)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO device_settings (owner_kind, owner_id, vendor_id, product_id, device_key, key, value)
                VALUES ($ok, $oi, $v, $p, $dk, $k, $val)
                ON CONFLICT(owner_kind, owner_id, vendor_id, product_id, device_key, key) DO UPDATE SET value = excluded.value;
                """;
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$v", vendorId);
            cmd.Parameters.AddWithValue("$p", productId);
            cmd.Parameters.AddWithValue("$dk", deviceKey ?? string.Empty);
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$val", value);
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    // Batched sibling of SetDeviceSetting: every upsert in ONE transaction -- one commit
    // fsync instead of one per key. SaveDpiPresetsAsync writes its 4xN stage keys through
    // this so restore-defaults is a single commit.
    public void SetDeviceSettings(string ownerKind, string ownerId, int vendorId, int productId, string deviceKey, IReadOnlyList<(string Key, double Value)> values)
    {
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var (key, value) in values)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT INTO device_settings (owner_kind, owner_id, vendor_id, product_id, device_key, key, value)
                    VALUES ($ok, $oi, $v, $p, $dk, $k, $val)
                    ON CONFLICT(owner_kind, owner_id, vendor_id, product_id, device_key, key) DO UPDATE SET value = excluded.value;
                    """;
                cmd.Parameters.AddWithValue("$ok", ownerKind);
                cmd.Parameters.AddWithValue("$oi", ownerId);
                cmd.Parameters.AddWithValue("$v", vendorId);
                cmd.Parameters.AddWithValue("$p", productId);
                cmd.Parameters.AddWithValue("$dk", deviceKey ?? string.Empty);
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$val", value);
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        finally { _lock.Release(); }
    }

    // Replaces a whole key FAMILY in one transaction: every row whose key starts with `prefix` is
    // deleted, then `values` is inserted. Still one commit fsync, like SetDeviceSettings.
    //
    // It exists because the DPI ladder shrinks. Its keys are positional (dpi.stage.{i}.*) and the
    // upsert above only ever writes the indices it is given, so a 7-point ladder saved over by a
    // 5-point one left rows 5 and 6 behind — and LoadDpiPresetsAsync, which enumerates whatever
    // indices it finds, would then resurrect the two deleted points on the next open.
    public void ReplaceDeviceSettingsWithPrefix(string ownerKind, string ownerId, int vendorId, int productId, string deviceKey, string prefix, IReadOnlyList<(string Key, double Value)> values)
    {
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();

            using (var purge = _connection.CreateCommand())
            {
                purge.Transaction = transaction;
                // ESCAPE so a prefix containing '_' (a LIKE single-character wildcard) matches
                // literally -- "dpi.stage." has none today, but the next caller's might.
                purge.CommandText = """
                    DELETE FROM device_settings
                    WHERE owner_kind = $ok AND owner_id = $oi AND vendor_id = $v AND product_id = $p
                      AND device_key = $dk AND key LIKE $prefix ESCAPE '\';
                    """;
                purge.Parameters.AddWithValue("$ok", ownerKind);
                purge.Parameters.AddWithValue("$oi", ownerId);
                purge.Parameters.AddWithValue("$v", vendorId);
                purge.Parameters.AddWithValue("$p", productId);
                purge.Parameters.AddWithValue("$dk", deviceKey ?? string.Empty);
                purge.Parameters.AddWithValue("$prefix", EscapeLikePrefix(prefix) + "%");
                purge.ExecuteNonQuery();
            }

            foreach (var (key, value) in values)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT INTO device_settings (owner_kind, owner_id, vendor_id, product_id, device_key, key, value)
                    VALUES ($ok, $oi, $v, $p, $dk, $k, $val)
                    ON CONFLICT(owner_kind, owner_id, vendor_id, product_id, device_key, key) DO UPDATE SET value = excluded.value;
                    """;
                cmd.Parameters.AddWithValue("$ok", ownerKind);
                cmd.Parameters.AddWithValue("$oi", ownerId);
                cmd.Parameters.AddWithValue("$v", vendorId);
                cmd.Parameters.AddWithValue("$p", productId);
                cmd.Parameters.AddWithValue("$dk", deviceKey ?? string.Empty);
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$val", value);
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        finally { _lock.Release(); }
    }

    private static string EscapeLikePrefix(string prefix) =>
        prefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    // ── App preferences (per owner, key → value; app-scoped, not per device) ─────

    public double? GetAppPreference(string ownerKind, string ownerId, string key)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM app_preferences WHERE owner_kind = $ok AND owner_id = $oi AND key = $k";
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$k", key);
            var result = cmd.ExecuteScalar();
            return result is null or DBNull ? null : Convert.ToDouble(result, CultureInfo.InvariantCulture);
        }
        finally { _lock.Release(); }
    }

    public void SetAppPreference(string ownerKind, string ownerId, string key, double value)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO app_preferences (owner_kind, owner_id, key, value)
                VALUES ($ok, $oi, $k, $val)
                ON CONFLICT(owner_kind, owner_id, key) DO UPDATE SET value = excluded.value;
                """;
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$val", value);
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    // ── Button assignments (the App is the source of truth) ──────────────────────────
    // One row per remapped device button: the action, plus the library macro id when the
    // action is Macro. The DeviceHost only writes the device from what the App sends; it no
    // longer persists bindings, so this table is what the UI reflects and re-sends.

    public List<(int Button, int Action, int? MacroId)> GetButtonBindings(string ownerKind, string ownerId, int vendorId, int productId, string deviceKey)
    {
        AcquireLock();
        try
        {
            var result = new List<(int, int, int?)>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT button, action, macro_id FROM button_bindings WHERE owner_kind = $ok AND owner_id = $oi AND vendor_id = $v AND product_id = $p AND device_key = $dk";
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$v", vendorId);
            cmd.Parameters.AddWithValue("$p", productId);
            cmd.Parameters.AddWithValue("$dk", deviceKey ?? string.Empty);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int? macroId = reader.IsDBNull(2) ? null : reader.GetInt32(2);
                result.Add((reader.GetInt32(0), reader.GetInt32(1), macroId));
            }
            return result;
        }
        finally { _lock.Release(); }
    }

    // Removes any button rows pointing at a macro that's being deleted (across all devices
    // and owners), so no button is left bound to a macro that no longer exists.
    public void DeleteButtonBindingsForMacro(int macroId)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM button_bindings WHERE macro_id = $m";
            cmd.Parameters.AddWithValue("$m", macroId);
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    // ── Key actuation (per owner, per device, per slot) ──────────────────────────────
    // The board's flash used to be the only place these lived. They are mirrored here so a preset
    // can carry them: the live rows are what a snapshot copies, exactly as for every other section.

    public List<ActuationRecord> GetDeviceActuation(
        string ownerKind, string ownerId, int vendorId, int productId, string deviceKey)
    {
        AcquireLock();
        try
        {
            var result = new List<ActuationRecord>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT slot, press_centi_mm, release_centi_mm, rapid_trigger, quick_press_centi_mm, quick_release_centi_mm FROM device_actuation WHERE owner_kind = $ok AND owner_id = $oi AND vendor_id = $v AND product_id = $p AND device_key = $dk ORDER BY slot";
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$v", vendorId);
            cmd.Parameters.AddWithValue("$p", productId);
            cmd.Parameters.AddWithValue("$dk", deviceKey ?? string.Empty);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new ActuationRecord(
                    reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
                    reader.GetInt32(3) != 0, reader.GetInt32(4), reader.GetInt32(5)));
            }

            return result;
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Replaces this device's whole actuation row set. Whole-set rather than per-slot because the
    /// board is always written as a coherent table — a partial update could leave a slot behind
    /// that the device no longer has, and the next preset save would snapshot it.
    /// </summary>
    public void ReplaceDeviceActuation(
        string ownerKind, string ownerId, int vendorId, int productId, string deviceKey,
        IReadOnlyList<ActuationRecord> rows)
    {
        deviceKey ??= string.Empty;
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();

            using (var wipe = _connection.CreateCommand())
            {
                wipe.Transaction = transaction;
                wipe.CommandText = "DELETE FROM device_actuation WHERE owner_kind = $ok AND owner_id = $oi AND vendor_id = $v AND product_id = $p AND device_key = $dk";
                wipe.Parameters.AddWithValue("$ok", ownerKind);
                wipe.Parameters.AddWithValue("$oi", ownerId);
                wipe.Parameters.AddWithValue("$v", vendorId);
                wipe.Parameters.AddWithValue("$p", productId);
                wipe.Parameters.AddWithValue("$dk", deviceKey);
                wipe.ExecuteNonQuery();
            }

            foreach (var row in rows)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT INTO device_actuation (owner_kind, owner_id, vendor_id, product_id, device_key, slot, press_centi_mm, release_centi_mm, rapid_trigger, quick_press_centi_mm, quick_release_centi_mm)
                    VALUES ($ok, $oi, $v, $p, $dk, $s, $pr, $rl, $rt, $qp, $qr);
                    """;
                cmd.Parameters.AddWithValue("$ok", ownerKind);
                cmd.Parameters.AddWithValue("$oi", ownerId);
                cmd.Parameters.AddWithValue("$v", vendorId);
                cmd.Parameters.AddWithValue("$p", productId);
                cmd.Parameters.AddWithValue("$dk", deviceKey);
                cmd.Parameters.AddWithValue("$s", row.Slot);
                cmd.Parameters.AddWithValue("$pr", row.PressCentiMm);
                cmd.Parameters.AddWithValue("$rl", row.ReleaseCentiMm);
                cmd.Parameters.AddWithValue("$rt", row.RapidTrigger ? 1 : 0);
                cmd.Parameters.AddWithValue("$qp", row.QuickPressCentiMm);
                cmd.Parameters.AddWithValue("$qr", row.QuickReleaseCentiMm);
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        finally { _lock.Release(); }
    }

    /// <summary>This preset's stored actuation, empty for a preset saved before actuation was preset-scoped.</summary>
    public List<ActuationRecord> GetPresetActuation(int presetId)
    {
        AcquireLock();
        try
        {
            var result = new List<ActuationRecord>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT slot, press_centi_mm, release_centi_mm, rapid_trigger, quick_press_centi_mm, quick_release_centi_mm FROM preset_actuation WHERE preset_id = $id ORDER BY slot";
            cmd.Parameters.AddWithValue("$id", presetId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new ActuationRecord(
                    reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
                    reader.GetInt32(3) != 0, reader.GetInt32(4), reader.GetInt32(5)));
            }

            return result;
        }
        finally { _lock.Release(); }
    }

    // ── Extended Keys (per owner, per device, per record) ────────────────────────────
    // Mirror of the board's advanced-key records, so a preset can carry them and the preset key push
    // knows which slots to leave alone. Rows are stored normalised (pair slots ascending) and read in
    // (mode, slot1, slot2) order — the same canonical shape ExtendedKeyRecord.NormalizeSet produces.

    public List<ExtendedKeyRecord> GetDeviceExtendedKeys(
        string ownerKind, string ownerId, int vendorId, int productId, string deviceKey)
    {
        AcquireLock();
        try
        {
            var result = new List<ExtendedKeyRecord>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT mode, slot1, slot2, priority, time_ms, hold_type, hold_code1, hold_code2, tap_type, tap_code1, tap_code2 FROM device_extended_keys WHERE owner_kind = $ok AND owner_id = $oi AND vendor_id = $v AND product_id = $p AND device_key = $dk ORDER BY mode, slot1, slot2";
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$v", vendorId);
            cmd.Parameters.AddWithValue("$p", productId);
            cmd.Parameters.AddWithValue("$dk", deviceKey ?? string.Empty);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new ExtendedKeyRecord(
                    reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
                    new ExtendedKeyCodeRecord(reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7)),
                    new ExtendedKeyCodeRecord(reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10))));
            }

            return result;
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Replaces this device's whole extended-key row set. Whole-set because the board is written as
    /// one set too — a partial update could leave a record behind that the device no longer has.
    /// Pairs are normalised on the way in, so the same two keys in either order are one row.
    /// </summary>
    public void ReplaceDeviceExtendedKeys(
        string ownerKind, string ownerId, int vendorId, int productId, string deviceKey,
        IReadOnlyList<ExtendedKeyRecord> rows)
    {
        deviceKey ??= string.Empty;
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();

            using (var wipe = _connection.CreateCommand())
            {
                wipe.Transaction = transaction;
                wipe.CommandText = "DELETE FROM device_extended_keys WHERE owner_kind = $ok AND owner_id = $oi AND vendor_id = $v AND product_id = $p AND device_key = $dk";
                wipe.Parameters.AddWithValue("$ok", ownerKind);
                wipe.Parameters.AddWithValue("$oi", ownerId);
                wipe.Parameters.AddWithValue("$v", vendorId);
                wipe.Parameters.AddWithValue("$p", productId);
                wipe.Parameters.AddWithValue("$dk", deviceKey);
                wipe.ExecuteNonQuery();
            }

            foreach (var raw in rows)
            {
                var row = raw.Normalized();
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT INTO device_extended_keys (owner_kind, owner_id, vendor_id, product_id, device_key, mode, slot1, slot2, priority, time_ms, hold_type, hold_code1, hold_code2, tap_type, tap_code1, tap_code2)
                    VALUES ($ok, $oi, $v, $p, $dk, $m, $s1, $s2, $pr, $t, $ht, $h1, $h2, $tt, $t1, $t2);
                    """;
                cmd.Parameters.AddWithValue("$ok", ownerKind);
                cmd.Parameters.AddWithValue("$oi", ownerId);
                cmd.Parameters.AddWithValue("$v", vendorId);
                cmd.Parameters.AddWithValue("$p", productId);
                cmd.Parameters.AddWithValue("$dk", deviceKey);
                cmd.Parameters.AddWithValue("$m", row.Mode);
                cmd.Parameters.AddWithValue("$s1", row.Slot1);
                cmd.Parameters.AddWithValue("$s2", row.Slot2);
                cmd.Parameters.AddWithValue("$pr", row.Priority);
                cmd.Parameters.AddWithValue("$t", row.TimeMs);
                cmd.Parameters.AddWithValue("$ht", row.HoldKey.TypeByte);
                cmd.Parameters.AddWithValue("$h1", row.HoldKey.Code1);
                cmd.Parameters.AddWithValue("$h2", row.HoldKey.Code2);
                cmd.Parameters.AddWithValue("$tt", row.TapKey.TypeByte);
                cmd.Parameters.AddWithValue("$t1", row.TapKey.Code1);
                cmd.Parameters.AddWithValue("$t2", row.TapKey.Code2);
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        finally { _lock.Release(); }
    }

    /// <summary>This preset's stored extended keys; empty is a real "none", not "no opinion".</summary>
    public List<ExtendedKeyRecord> GetPresetExtendedKeys(int presetId)
    {
        AcquireLock();
        try
        {
            var result = new List<ExtendedKeyRecord>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT mode, slot1, slot2, priority, time_ms, hold_type, hold_code1, hold_code2, tap_type, tap_code1, tap_code2 FROM preset_extended_keys WHERE preset_id = $id ORDER BY mode, slot1, slot2";
            cmd.Parameters.AddWithValue("$id", presetId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new ExtendedKeyRecord(
                    reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4),
                    new ExtendedKeyCodeRecord(reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7)),
                    new ExtendedKeyCodeRecord(reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10))));
            }

            return result;
        }
        finally { _lock.Release(); }
    }

    // ── Preset snapshot reads (PresetConfigurationBuilder — API v2.0 sync) ───────────
    // Thin per-table getters over the preset_* snapshot tables, mirroring their live-table
    // counterparts (GetRgbState / GetDeviceSettings / GetButtonBindings / GetKeyBindings /
    // GetKeyMacroBindings) but keyed by preset_id alone.

    /// <summary>The preset's RGB snapshot (head row + per-effect rows), or null when it has none.</summary>
    public RgbStateRecord? GetPresetRgbState(int presetId)
    {
        AcquireLock();
        try
        {
            RgbStateRecord? state = null;
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT is_enabled, selected_effect FROM preset_rgb WHERE preset_id = $id";
                cmd.Parameters.AddWithValue("$id", presetId);
                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                    state = new RgbStateRecord { IsEnabled = reader.GetInt32(0) != 0, SelectedEffect = reader.GetString(1) };
            }

            if (state == null) return null;

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT effect_key, hex, brightness, speed, is_rainbow FROM preset_rgb_effect WHERE preset_id = $id ORDER BY effect_key";
                cmd.Parameters.AddWithValue("$id", presetId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    state.Effects.Add(new RgbEffectRecord
                    {
                        EffectKey = reader.GetString(0),
                        Hex = reader.GetString(1),
                        Brightness = reader.GetDouble(2),
                        Speed = reader.GetDouble(3),
                        IsRainbow = reader.GetInt32(4) != 0,
                    });
            }

            return state;
        }
        finally { _lock.Release(); }
    }

    public Dictionary<string, double> GetPresetSettings(int presetId)
    {
        AcquireLock();
        try
        {
            var result = new Dictionary<string, double>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM preset_settings WHERE preset_id = $id";
            cmd.Parameters.AddWithValue("$id", presetId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result[reader.GetString(0)] = reader.GetDouble(1);
            return result;
        }
        finally { _lock.Release(); }
    }

    public List<(int Button, int Action, int? MacroId)> GetPresetButtonBindings(int presetId)
    {
        AcquireLock();
        try
        {
            var result = new List<(int, int, int?)>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT button, action, macro_id FROM preset_button_bindings WHERE preset_id = $id ORDER BY button";
            cmd.Parameters.AddWithValue("$id", presetId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                int? macroId = reader.IsDBNull(2) ? null : reader.GetInt32(2);
                result.Add((reader.GetInt32(0), reader.GetInt32(1), macroId));
            }
            return result;
        }
        finally { _lock.Release(); }
    }

    public List<(byte PhysScancode, byte TypeByte, byte Bitmask, byte TargetCode)> GetPresetKeyBindings(int presetId)
    {
        AcquireLock();
        try
        {
            var result = new List<(byte, byte, byte, byte)>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT phys_scancode, type_byte, bitmask, target_code FROM preset_key_bindings WHERE preset_id = $id ORDER BY phys_scancode";
            cmd.Parameters.AddWithValue("$id", presetId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add(((byte)reader.GetInt32(0), (byte)reader.GetInt32(1), (byte)reader.GetInt32(2), (byte)reader.GetInt32(3)));
            return result;
        }
        finally { _lock.Release(); }
    }

    public List<(int MatrixIndex, int MacroId)> GetPresetKeyMacroBindings(int presetId)
    {
        AcquireLock();
        try
        {
            var result = new List<(int, int)>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT matrix_index, macro_id FROM preset_key_macro_bindings WHERE preset_id = $id ORDER BY matrix_index";
            cmd.Parameters.AddWithValue("$id", presetId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add((reader.GetInt32(0), reader.GetInt32(1)));
            return result;
        }
        finally { _lock.Release(); }
    }

    // ── Preset snapshot writes (PresetConfigurationApplier — API v2.0 pull) ──────────
    // Thin per-table inserts mirroring the reads above, for hydrating a freshly inserted
    // preset from a pulled configuration (docs/plans/add-api-v2-pull). Insert-only by
    // design: the pull is additive and only ever writes into a preset whose snapshot
    // tables are empty, so no wipe precedes these (SavePresetSnapshot stays the one
    // replace-from-live path).

    public void InsertPresetRgbState(int presetId, bool isEnabled, string selectedEffect, IReadOnlyList<RgbEffectRecord> effects)
    {
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();

            using (var head = _connection.CreateCommand())
            {
                head.Transaction = transaction;
                head.CommandText = "INSERT INTO preset_rgb (preset_id, is_enabled, selected_effect) VALUES ($id, $e, $s)";
                head.Parameters.AddWithValue("$id", presetId);
                head.Parameters.AddWithValue("$e", isEnabled ? 1 : 0);
                head.Parameters.AddWithValue("$s", selectedEffect ?? string.Empty);
                head.ExecuteNonQuery();
            }

            foreach (var effect in effects)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT INTO preset_rgb_effect (preset_id, effect_key, hex, brightness, speed, is_rainbow)
                    VALUES ($id, $k, $h, $b, $sp, $r)
                    """;
                cmd.Parameters.AddWithValue("$id", presetId);
                cmd.Parameters.AddWithValue("$k", effect.EffectKey);
                cmd.Parameters.AddWithValue("$h", effect.Hex);
                cmd.Parameters.AddWithValue("$b", effect.Brightness);
                cmd.Parameters.AddWithValue("$sp", effect.Speed);
                cmd.Parameters.AddWithValue("$r", effect.IsRainbow ? 1 : 0);
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        finally { _lock.Release(); }
    }

    public void InsertPresetSettings(int presetId, IReadOnlyList<(string Key, double Value)> values)
    {
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var (key, value) in values)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "INSERT INTO preset_settings (preset_id, key, value) VALUES ($id, $k, $v)";
                cmd.Parameters.AddWithValue("$id", presetId);
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$v", value);
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        finally { _lock.Release(); }
    }

    public void InsertPresetActuation(int presetId, IReadOnlyList<ActuationRecord> rows)
    {
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var row in rows)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT INTO preset_actuation (preset_id, slot, press_centi_mm, release_centi_mm, rapid_trigger, quick_press_centi_mm, quick_release_centi_mm)
                    VALUES ($id, $s, $pr, $re, $rt, $qp, $qr)
                    """;
                cmd.Parameters.AddWithValue("$id", presetId);
                cmd.Parameters.AddWithValue("$s", row.Slot);
                cmd.Parameters.AddWithValue("$pr", row.PressCentiMm);
                cmd.Parameters.AddWithValue("$re", row.ReleaseCentiMm);
                cmd.Parameters.AddWithValue("$rt", row.RapidTrigger ? 1 : 0);
                cmd.Parameters.AddWithValue("$qp", row.QuickPressCentiMm);
                cmd.Parameters.AddWithValue("$qr", row.QuickReleaseCentiMm);
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        finally { _lock.Release(); }
    }

    public void InsertPresetExtendedKeys(int presetId, IReadOnlyList<ExtendedKeyRecord> rows)
    {
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var raw in rows)
            {
                var row = raw.Normalized();
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT INTO preset_extended_keys (preset_id, mode, slot1, slot2, priority, time_ms, hold_type, hold_code1, hold_code2, tap_type, tap_code1, tap_code2)
                    VALUES ($id, $m, $s1, $s2, $pr, $t, $ht, $h1, $h2, $tt, $t1, $t2)
                    """;
                cmd.Parameters.AddWithValue("$id", presetId);
                cmd.Parameters.AddWithValue("$m", row.Mode);
                cmd.Parameters.AddWithValue("$s1", row.Slot1);
                cmd.Parameters.AddWithValue("$s2", row.Slot2);
                cmd.Parameters.AddWithValue("$pr", row.Priority);
                cmd.Parameters.AddWithValue("$t", row.TimeMs);
                cmd.Parameters.AddWithValue("$ht", row.HoldKey.TypeByte);
                cmd.Parameters.AddWithValue("$h1", row.HoldKey.Code1);
                cmd.Parameters.AddWithValue("$h2", row.HoldKey.Code2);
                cmd.Parameters.AddWithValue("$tt", row.TapKey.TypeByte);
                cmd.Parameters.AddWithValue("$t1", row.TapKey.Code1);
                cmd.Parameters.AddWithValue("$t2", row.TapKey.Code2);
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        finally { _lock.Release(); }
    }

    public void InsertPresetKeyBindings(int presetId, IReadOnlyList<(byte PhysScancode, byte TypeByte, byte Bitmask, byte TargetCode)> rows)
    {
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var (physScancode, typeByte, bitmask, targetCode) in rows)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT INTO preset_key_bindings (preset_id, phys_scancode, type_byte, bitmask, target_code)
                    VALUES ($id, $ps, $tb, $bm, $tc)
                    """;
                cmd.Parameters.AddWithValue("$id", presetId);
                cmd.Parameters.AddWithValue("$ps", (int)physScancode);
                cmd.Parameters.AddWithValue("$tb", (int)typeByte);
                cmd.Parameters.AddWithValue("$bm", (int)bitmask);
                cmd.Parameters.AddWithValue("$tc", (int)targetCode);
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        finally { _lock.Release(); }
    }

    public void InsertPresetButtonBindings(int presetId, IReadOnlyList<(int Button, int Action, int? MacroId)> rows)
    {
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var (button, action, macroId) in rows)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "INSERT INTO preset_button_bindings (preset_id, button, action, macro_id) VALUES ($id, $b, $a, $m)";
                cmd.Parameters.AddWithValue("$id", presetId);
                cmd.Parameters.AddWithValue("$b", button);
                cmd.Parameters.AddWithValue("$a", action);
                cmd.Parameters.AddWithValue("$m", (object?)macroId ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        finally { _lock.Release(); }
    }

    public void InsertPresetKeyMacroBindings(int presetId, IReadOnlyList<(int MatrixIndex, int MacroId)> rows)
    {
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var (matrixIndex, macroId) in rows)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "INSERT INTO preset_key_macro_bindings (preset_id, matrix_index, macro_id) VALUES ($id, $mi, $m)";
                cmd.Parameters.AddWithValue("$id", presetId);
                cmd.Parameters.AddWithValue("$mi", matrixIndex);
                cmd.Parameters.AddWithValue("$m", macroId);
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        finally { _lock.Release(); }
    }

    // ── Keyboard macro bindings (per owner, per physical key) ────────────────────────
    // One row per keyboard physical key currently bound to a library macro. The App is the
    // source of truth: the device blob holds only event lists, with no names, ids or modes,
    // so the binding has to be remembered here to survive a library edit or a reconnect.

    public List<(int MatrixIndex, int MacroId)> GetKeyMacroBindings(
        string ownerKind, string ownerId, int vendorId, int productId, string deviceKey)
    {
        AcquireLock();
        try
        {
            var result = new List<(int, int)>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT matrix_index, macro_id FROM key_macro_bindings WHERE owner_kind = $ok AND owner_id = $oi AND vendor_id = $v AND product_id = $p AND device_key = $dk";
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$v", vendorId);
            cmd.Parameters.AddWithValue("$p", productId);
            cmd.Parameters.AddWithValue("$dk", deviceKey ?? string.Empty);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add((reader.GetInt32(0), reader.GetInt32(1)));
            return result;
        }
        finally { _lock.Release(); }
    }

    public void SetKeyMacroBinding(
        string ownerKind, string ownerId, int vendorId, int productId, string deviceKey,
        int matrixIndex, int macroId)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO key_macro_bindings (owner_kind, owner_id, vendor_id, product_id, device_key, matrix_index, macro_id)
                VALUES ($ok, $oi, $v, $p, $dk, $mi, $m)
                ON CONFLICT(owner_kind, owner_id, vendor_id, product_id, device_key, matrix_index)
                    DO UPDATE SET macro_id = excluded.macro_id;
                """;
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$v", vendorId);
            cmd.Parameters.AddWithValue("$p", productId);
            cmd.Parameters.AddWithValue("$dk", deviceKey ?? string.Empty);
            cmd.Parameters.AddWithValue("$mi", matrixIndex);
            cmd.Parameters.AddWithValue("$m", macroId);
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    // Called when a key is remapped back to an ordinary keycode.
    public void DeleteKeyMacroBinding(
        string ownerKind, string ownerId, int vendorId, int productId, string deviceKey, int matrixIndex)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM key_macro_bindings WHERE owner_kind = $ok AND owner_id = $oi AND vendor_id = $v AND product_id = $p AND device_key = $dk AND matrix_index = $mi";
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$v", vendorId);
            cmd.Parameters.AddWithValue("$p", productId);
            cmd.Parameters.AddWithValue("$dk", deviceKey ?? string.Empty);
            cmd.Parameters.AddWithValue("$mi", matrixIndex);
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    // Removes any keyboard-key rows pointing at a macro that's being deleted (across all
    // devices and owners), so no key stays bound to a macro that no longer exists. Mirrors
    // DeleteButtonBindingsForMacro on the mouse side.
    public void DeleteKeyMacroBindingsForMacro(int macroId)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM key_macro_bindings WHERE macro_id = $m";
            cmd.Parameters.AddWithValue("$m", macroId);
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    public void SetButtonBinding(string ownerKind, string ownerId, int vendorId, int productId, string deviceKey, int button, int action, int? macroId)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO button_bindings (owner_kind, owner_id, vendor_id, product_id, device_key, button, action, macro_id)
                VALUES ($ok, $oi, $v, $p, $dk, $b, $a, $m)
                ON CONFLICT(owner_kind, owner_id, vendor_id, product_id, device_key, button)
                    DO UPDATE SET action = excluded.action, macro_id = excluded.macro_id;
                """;
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$v", vendorId);
            cmd.Parameters.AddWithValue("$p", productId);
            cmd.Parameters.AddWithValue("$dk", deviceKey ?? string.Empty);
            cmd.Parameters.AddWithValue("$b", button);
            cmd.Parameters.AddWithValue("$a", action);
            cmd.Parameters.AddWithValue("$m", (object?)macroId ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    // ── Keyboard key remap overrides (app-truth, per owner + device) ─────────────────
    // One row per remapped physical key (keyed by its HID scancode). Absence = default.

    public List<(byte PhysScancode, byte TypeByte, byte Bitmask, byte TargetCode)> GetKeyBindings(
        string ownerKind, string ownerId, int vendorId, int productId, string deviceKey)
    {
        AcquireLock();
        try
        {
            var result = new List<(byte, byte, byte, byte)>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT phys_scancode, type_byte, bitmask, target_code FROM key_bindings WHERE owner_kind = $ok AND owner_id = $oi AND vendor_id = $v AND product_id = $p AND device_key = $dk";
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$v", vendorId);
            cmd.Parameters.AddWithValue("$p", productId);
            cmd.Parameters.AddWithValue("$dk", deviceKey ?? string.Empty);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add(((byte)reader.GetInt32(0), (byte)reader.GetInt32(1), (byte)reader.GetInt32(2), (byte)reader.GetInt32(3)));
            return result;
        }
        finally { _lock.Release(); }
    }

    public void SetKeyBinding(string ownerKind, string ownerId, int vendorId, int productId, string deviceKey,
        byte physScancode, byte typeByte, byte bitmask, byte targetCode)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO key_bindings (owner_kind, owner_id, vendor_id, product_id, device_key, phys_scancode, type_byte, bitmask, target_code)
                VALUES ($ok, $oi, $v, $p, $dk, $sc, $t, $bm, $tc)
                ON CONFLICT(owner_kind, owner_id, vendor_id, product_id, device_key, phys_scancode)
                    DO UPDATE SET type_byte = excluded.type_byte, bitmask = excluded.bitmask, target_code = excluded.target_code;
                """;
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$v", vendorId);
            cmd.Parameters.AddWithValue("$p", productId);
            cmd.Parameters.AddWithValue("$dk", deviceKey ?? string.Empty);
            cmd.Parameters.AddWithValue("$sc", physScancode);
            cmd.Parameters.AddWithValue("$t", typeByte);
            cmd.Parameters.AddWithValue("$bm", bitmask);
            cmd.Parameters.AddWithValue("$tc", targetCode);
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    public void DeleteKeyBinding(string ownerKind, string ownerId, int vendorId, int productId, string deviceKey, byte physScancode)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM key_bindings WHERE owner_kind = $ok AND owner_id = $oi AND vendor_id = $v AND product_id = $p AND device_key = $dk AND phys_scancode = $sc";
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$v", vendorId);
            cmd.Parameters.AddWithValue("$p", productId);
            cmd.Parameters.AddWithValue("$dk", deviceKey ?? string.Empty);
            cmd.Parameters.AddWithValue("$sc", physScancode);
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    // ── Applied-preset tracking (which preset was last pushed to a physical device) ──

    // The '' device_key is the unit-agnostic scope the sign-in pull writes into (API v2.0
    // keys device state on VID/PID alone), so a unit-keyed read falls back to it when the
    // unit has no row of its own — that is how a cloud active preset reaches a physical
    // unit's reapply-on-connect. A row under the exact key always wins over the '' row.
    public int? GetAppliedPreset(string ownerKind, string ownerId, int vendorId, int productId, string deviceKey)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT preset_id FROM device_applied_preset WHERE owner_kind = $ok AND owner_id = $oi AND vendor_id = $v AND product_id = $p AND device_key IN ($dk, '') ORDER BY (device_key = $dk) DESC LIMIT 1";
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$v", vendorId);
            cmd.Parameters.AddWithValue("$p", productId);
            cmd.Parameters.AddWithValue("$dk", deviceKey ?? string.Empty);
            var result = cmd.ExecuteScalar();
            return result is null or DBNull ? null : (int)(long)result;
        }
        finally { _lock.Release(); }
    }

    // Every unit's applied-preset row for one (owner, device type), ordered by device_key so
    // '' (the unit-agnostic row) sorts first and the unit rows follow in ordinal order. The
    // sync push collapses these to one because API v2.0's devices/update keys on VID/PID
    // alone (no device_key) — see SettingsSyncService.ChooseAppliedPresetRow for which row
    // wins, which is NOT simply the first one.
    public List<(string DeviceKey, int PresetId)> GetAppliedPresets(string ownerKind, string ownerId, int vendorId, int productId)
    {
        AcquireLock();
        try
        {
            var result = new List<(string, int)>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT device_key, preset_id FROM device_applied_preset WHERE owner_kind = $ok AND owner_id = $oi AND vendor_id = $v AND product_id = $p ORDER BY device_key";
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$v", vendorId);
            cmd.Parameters.AddWithValue("$p", productId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add((reader.GetString(0), reader.GetInt32(1)));
            return result;
        }
        finally { _lock.Release(); }
    }

    // markDirty: false is for the sign-in pull — the applied preset it writes IS the
    // server's device state, so marking it dirty would push it straight back as an echo.
    public void SetAppliedPreset(string ownerKind, string ownerId, int vendorId, int productId, string deviceKey, int presetId, bool markDirty = true)
    {
        var marked = false;
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO device_applied_preset (owner_kind, owner_id, vendor_id, product_id, device_key, preset_id)
                VALUES ($ok, $oi, $v, $p, $dk, $id)
                ON CONFLICT(owner_kind, owner_id, vendor_id, product_id, device_key)
                    DO UPDATE SET preset_id = excluded.preset_id;
                """;
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$v", vendorId);
            cmd.Parameters.AddWithValue("$p", productId);
            cmd.Parameters.AddWithValue("$dk", deviceKey ?? string.Empty);
            cmd.Parameters.AddWithValue("$id", presetId);
            cmd.ExecuteNonQuery();

            if (markDirty && ownerKind == OwnerKindUser)
            {
                SetSyncMetaCore(ownerKind, ownerId, SyncMetaDeviceStateDirty(vendorId, productId), NextSyncDirtyGeneration());
                marked = true;
            }
        }
        finally { _lock.Release(); }

        if (marked)
            RaiseSyncRelevantDataChanged();
    }

    // ── Macros (library — the source of truth; profiles/presets will join to these) ──
    // Macros are per (owner, device): the library is scoped to a single physical device by its
    // settings identity (vendor_id, product_id, device_key), the same scope as button_bindings.
    // The '' device_key additionally acts as a unit-agnostic shared scope: the sign-in pull
    // lands cloud macros there (API v2.0 keys macros on VID/PID alone), and a unit-keyed read
    // returns the union of the unit's own rows and the '' rows so pulled macros are visible to
    // every physical unit of the model. Unit rows never leak to another unit; a same-uid row
    // cannot exist in both scopes (uid is globally unique).

    public List<MacroRecord> GetMacros(string ownerKind, string ownerId, int vendorId, int productId, string deviceKey)
    {
        AcquireLock();
        try
        {
            var byId = new Dictionary<int, MacroRecord>();

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT id, name, repeat_count, repeat_mode, uid, server_updated_at FROM macros WHERE owner_kind = $ok AND owner_id = $oi AND vendor_id = $v AND product_id = $p AND device_key IN ($dk, '') ORDER BY id";
                cmd.Parameters.AddWithValue("$ok", ownerKind);
                cmd.Parameters.AddWithValue("$oi", ownerId);
                cmd.Parameters.AddWithValue("$v", vendorId);
                cmd.Parameters.AddWithValue("$p", productId);
                cmd.Parameters.AddWithValue("$dk", deviceKey ?? string.Empty);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var m = new MacroRecord
                    {
                        Id = reader.GetInt32(0),
                        Name = reader.GetString(1),
                        RepeatCount = reader.GetInt32(2),
                        Mode = (MacroRepeatMode)reader.GetInt32(3),
                        Uid = reader.GetString(4),
                        ServerUpdatedAt = reader.IsDBNull(5) ? null : reader.GetString(5),
                    };
                    byId[m.Id] = m;
                }
            }

            if (byId.Count == 0) return [];

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT macro_id, key_code, is_press, delay_ms FROM macro_events ORDER BY macro_id, ordinal";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (byId.TryGetValue(reader.GetInt32(0), out var m))
                        m.Events.Add(new MacroEventRecord
                        {
                            KeyCode = (byte)reader.GetInt32(1),
                            IsPress = reader.GetInt32(2) != 0,
                            DelayMs = reader.GetInt32(3),
                        });
                }
            }

            return [.. byId.Values];
        }
        finally { _lock.Release(); }
    }

    // Insert (Id == 0) or update; replaces the macro's events. Returns the macro id.
    // Inserts are scoped to the (owner, device) that created the macro; updates keep the row's scope.
    // markDirty: false is for the sign-in pull — its rows ARE server content, so they must
    // land clean and raise no sync signal. Guest rows are never marked either way.
    public int SaveMacro(string ownerKind, string ownerId, int vendorId, int productId, string deviceKey, MacroRecord macro, bool markDirty = true)
    {
        bool marked;
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();

            if (macro.Id > 0)
            {
                // Dirty-mark user rows only: guest macros never sync, and the pull never
                // updates (its merge is additive), so every update here is a local edit.
                marked = markDirty && ownerKind == OwnerKindUser;

                using var update = _connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = marked
                    ? "UPDATE macros SET name = $n, repeat_count = $rc, repeat_mode = $rm, is_dirty = 1, local_rev = local_rev + 1 WHERE id = $id"
                    : "UPDATE macros SET name = $n, repeat_count = $rc, repeat_mode = $rm WHERE id = $id";
                update.Parameters.AddWithValue("$n", macro.Name);
                update.Parameters.AddWithValue("$rc", macro.RepeatCount);
                update.Parameters.AddWithValue("$rm", (int)macro.Mode);
                update.Parameters.AddWithValue("$id", macro.Id);
                // A nonexistent id updates nothing — a no-op raises no sync signal.
                if (update.ExecuteNonQuery() == 0)
                    marked = false;

                using var clear = _connection.CreateCommand();
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM macro_events WHERE macro_id = $id";
                clear.Parameters.AddWithValue("$id", macro.Id);
                clear.ExecuteNonQuery();
            }
            else
            {
                // A non-empty Uid on the record wins (tests pin known uids); otherwise generate.
                var uid = string.IsNullOrEmpty(macro.Uid) ? Guid.NewGuid().ToString("D") : macro.Uid;

                // A user-created macro is born dirty/unsynced; the pull's inserts pass
                // markDirty: false (server content — nothing to push) and stay clean.
                marked = markDirty && ownerKind == OwnerKindUser;

                using var insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO macros (owner_kind, owner_id, vendor_id, product_id, device_key, name, repeat_count, repeat_mode, created_at, uid, server_updated_at, is_dirty, local_rev)
                    VALUES ($ok, $oi, $v, $p, $dk, $n, $rc, $rm, $at, $u, $su, $dirty, $rev);
                    SELECT last_insert_rowid();
                    """;
                insert.Parameters.AddWithValue("$ok", ownerKind);
                insert.Parameters.AddWithValue("$oi", ownerId);
                insert.Parameters.AddWithValue("$v", vendorId);
                insert.Parameters.AddWithValue("$p", productId);
                insert.Parameters.AddWithValue("$dk", deviceKey ?? string.Empty);
                insert.Parameters.AddWithValue("$n", macro.Name);
                insert.Parameters.AddWithValue("$rc", macro.RepeatCount);
                insert.Parameters.AddWithValue("$rm", (int)macro.Mode);
                insert.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
                insert.Parameters.AddWithValue("$u", uid);
                // Honoured on insert so the sign-in pull's write is one statement, not an
                // insert plus SetMacroServerUpdatedAt; every other caller leaves it null.
                insert.Parameters.AddWithValue("$su", (object?)macro.ServerUpdatedAt ?? DBNull.Value);
                insert.Parameters.AddWithValue("$dirty", marked ? 1 : 0);
                insert.Parameters.AddWithValue("$rev", marked ? 1 : 0);
                macro.Id = (int)(long)insert.ExecuteScalar()!;
                macro.Uid = uid;
            }

            for (var i = 0; i < macro.Events.Count; i++)
            {
                var e = macro.Events[i];
                using var ev = _connection.CreateCommand();
                ev.Transaction = transaction;
                ev.CommandText = """
                    INSERT INTO macro_events (macro_id, ordinal, key_code, is_press, delay_ms)
                    VALUES ($mid, $ord, $kc, $ip, $d);
                    """;
                ev.Parameters.AddWithValue("$mid", macro.Id);
                ev.Parameters.AddWithValue("$ord", i);
                ev.Parameters.AddWithValue("$kc", (int)e.KeyCode);
                ev.Parameters.AddWithValue("$ip", e.IsPress ? 1 : 0);
                ev.Parameters.AddWithValue("$d", e.DelayMs);
                ev.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        finally { _lock.Release(); }

        if (marked)
            RaiseSyncRelevantDataChanged();
        return macro.Id;
    }

    // Sync write-back (API v2.0): stores the server's updated_at exactly as received. Only
    // sync code calls this — nothing else may touch server_updated_at.
    public void SetMacroServerUpdatedAt(int macroId, string value)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE macros SET server_updated_at = $ts WHERE id = $id";
            cmd.Parameters.AddWithValue("$ts", value);
            cmd.Parameters.AddWithValue("$id", macroId);
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    // The distinct device_key values holding macros for one (owner, device type) — the sync
    // push iterates these so it finds macros under every physical unit, not just the units
    // that still have a devices-table card.
    public List<string> GetMacroDeviceKeys(string ownerKind, string ownerId, int vendorId, int productId)
    {
        AcquireLock();
        try
        {
            var result = new List<string>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT device_key FROM macros WHERE owner_kind = $ok AND owner_id = $oi AND vendor_id = $v AND product_id = $p ORDER BY device_key";
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$v", vendorId);
            cmd.Parameters.AddWithValue("$p", productId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add(reader.GetString(0));
            return result;
        }
        finally { _lock.Release(); }
    }

    // Every macro uid the owner has, across all device types and units — one read instead of
    // a per-item round-trip, so the sign-in pull's absence checks are dictionary lookups
    // (docs/plans/add-api-v2-pull). Uids are globally unique (unique index), keyed by uid alone.
    public Dictionary<string, int> GetMacroIdsByUid(string ownerKind, string ownerId)
    {
        AcquireLock();
        try
        {
            var result = new Dictionary<string, int>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT uid, id FROM macros WHERE owner_kind = $ok AND owner_id = $oi";
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result[reader.GetString(0)] = reader.GetInt32(1);
            return result;
        }
        finally { _lock.Release(); }
    }

    // Macro ids are globally unique (auto-increment), so deletion isn't owner-scoped —
    // whoever holds the id gets to remove it, and both macro_events and any button_bindings
    // referencing it cascade.
    public void DeleteMacro(int id)
    {
        bool tombstoned;
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();
            tombstoned = WriteTombstoneCore(transaction, "macros", TombstoneKindMacro, id);
            DeleteMacroCore(transaction, id);
            transaction.Commit();
        }
        finally { _lock.Release(); }

        if (tombstoned)
            RaiseSyncRelevantDataChanged();
    }

    // Records a pending server delete in the same transaction as the local delete — but only
    // for a user-owned row the server actually knows about: uid assigned AND
    // server_updated_at non-NULL. A never-synced row is deleted without a tombstone (there is
    // nothing remote to remove, so even the *_NOT_FOUND round-trip is skipped); guest rows
    // never sync at all. Local deletion never waits on the server either way.
    private bool WriteTombstoneCore(SqliteTransaction transaction, string table, string kind, int rowId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"""
            INSERT OR REPLACE INTO sync_tombstones (owner_kind, owner_id, kind, uid, vendor_id, product_id, server_updated_at)
            SELECT owner_kind, owner_id, $kind, uid, vendor_id, product_id, server_updated_at
            FROM {table}
            WHERE id = $id AND owner_kind = $uk AND uid <> '' AND server_updated_at IS NOT NULL;
            """;
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$id", rowId);
        cmd.Parameters.AddWithValue("$uk", OwnerKindUser);
        return cmd.ExecuteNonQuery() > 0;
    }

    // ── Live sync tracking (docs/plans/add-api-v2-live-sync) ─────────────────────

    // Every macro the coordinator's push pass must consider for one owner: rows explicitly
    // marked dirty plus never-synced rows (server_updated_at IS NULL) — the latter is what
    // lets an interrupted upgrade push converge. Enumerating rows (not device cards) is what
    // reaches macros under device types without a card, and rows in the pulled shared scope
    // (device_key ''): the device_key plays no part in the filter.
    public List<(MacroRecord Macro, int VendorId, int ProductId)> GetDirtyMacros(string ownerKind, string ownerId)
    {
        AcquireLock();
        try
        {
            var result = new List<(MacroRecord, int, int)>();
            var byId = new Dictionary<int, MacroRecord>();

            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT id, name, repeat_count, repeat_mode, uid, server_updated_at, vendor_id, product_id, is_dirty, local_rev FROM macros WHERE owner_kind = $ok AND owner_id = $oi AND (is_dirty = 1 OR server_updated_at IS NULL) ORDER BY id";
                cmd.Parameters.AddWithValue("$ok", ownerKind);
                cmd.Parameters.AddWithValue("$oi", ownerId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var m = new MacroRecord
                    {
                        Id = reader.GetInt32(0),
                        Name = reader.GetString(1),
                        RepeatCount = reader.GetInt32(2),
                        Mode = (MacroRepeatMode)reader.GetInt32(3),
                        Uid = reader.GetString(4),
                        ServerUpdatedAt = reader.IsDBNull(5) ? null : reader.GetString(5),
                        IsDirty = reader.GetInt32(8) != 0,
                        LocalRev = reader.GetInt64(9),
                    };
                    byId[m.Id] = m;
                    result.Add((m, reader.GetInt32(6), reader.GetInt32(7)));
                }
            }

            if (result.Count == 0) return result;

            using (var cmd = _connection.CreateCommand())
            {
                // Same filter as the row query above (one lock, consistent snapshot) so
                // only the dirty/unsynced macros' events are read, not the whole table.
                cmd.CommandText = "SELECT e.macro_id, e.key_code, e.is_press, e.delay_ms FROM macro_events e JOIN macros m ON m.id = e.macro_id WHERE m.owner_kind = $ok AND m.owner_id = $oi AND (m.is_dirty = 1 OR m.server_updated_at IS NULL) ORDER BY e.macro_id, e.ordinal";
                cmd.Parameters.AddWithValue("$ok", ownerKind);
                cmd.Parameters.AddWithValue("$oi", ownerId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (byId.TryGetValue(reader.GetInt32(0), out var m))
                        m.Events.Add(new MacroEventRecord
                        {
                            KeyCode = (byte)reader.GetInt32(1),
                            IsPress = reader.GetInt32(2) != 0,
                            DelayMs = reader.GetInt32(3),
                        });
                }
            }

            return result;
        }
        finally { _lock.Release(); }
    }

    // GetDirtyMacros' preset twin: dirty plus never-synced rows for one owner, any device type.
    public List<(PresetRecord Preset, int VendorId, int ProductId)> GetDirtyPresets(string ownerKind, string ownerId)
    {
        AcquireLock();
        try
        {
            var result = new List<(PresetRecord, int, int)>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id, name, description, is_default, uid, server_updated_at, vendor_id, product_id, is_dirty, local_rev FROM presets WHERE owner_kind = $ok AND owner_id = $oi AND (is_dirty = 1 OR server_updated_at IS NULL) ORDER BY id";
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add((new PresetRecord
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    Description = reader.GetString(2),
                    IsDefault = reader.GetInt32(3) != 0,
                    Uid = reader.GetString(4),
                    ServerUpdatedAt = reader.IsDBNull(5) ? null : reader.GetString(5),
                    OwnerKind = ownerKind,
                    OwnerId = ownerId,
                    IsDirty = reader.GetInt32(8) != 0,
                    LocalRev = reader.GetInt64(9),
                }, reader.GetInt32(6), reader.GetInt32(7)));
            return result;
        }
        finally { _lock.Release(); }
    }

    // Rev-guarded dirty clear: only clears when the row was not re-edited since the push
    // read it — a mutation that landed mid-push bumped local_rev, the guard misses, and the
    // row stays dirty for the follow-up pass. One atomic UPDATE, no read-then-write race.
    public void ClearMacroDirty(int macroId, long localRev)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE macros SET is_dirty = 0 WHERE id = $id AND local_rev = $rev";
            cmd.Parameters.AddWithValue("$id", macroId);
            cmd.Parameters.AddWithValue("$rev", localRev);
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    public void ClearPresetDirty(int presetId, long localRev)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE presets SET is_dirty = 0 WHERE id = $id AND local_rev = $rev";
            cmd.Parameters.AddWithValue("$id", presetId);
            cmd.Parameters.AddWithValue("$rev", localRev);
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    public List<(string Uid, int VendorId, int ProductId, string? ServerUpdatedAt)> GetSyncTombstones(string ownerKind, string ownerId, string kind)
    {
        AcquireLock();
        try
        {
            var result = new List<(string, int, int, string?)>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT uid, vendor_id, product_id, server_updated_at FROM sync_tombstones WHERE owner_kind = $ok AND owner_id = $oi AND kind = $k ORDER BY uid";
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$k", kind);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            return result;
        }
        finally { _lock.Release(); }
    }

    // Point read of a single uid's tombstone. The sign-in pull calls it immediately before
    // each insert, inside the same storage item: the coordinator's tombstone pass runs
    // concurrently with the pull and removes a tombstone as soon as the server confirms the
    // delete, so a set read before the merge loop can be stale by the time the insert runs
    // (docs/plans/add-api-v2-live-sync).
    public bool ExistsSyncTombstone(string ownerKind, string ownerId, string kind, string uid)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sync_tombstones WHERE owner_kind = $ok AND owner_id = $oi AND kind = $k AND uid = $u LIMIT 1";
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$u", uid);
            return cmd.ExecuteScalar() is not (null or DBNull);
        }
        finally { _lock.Release(); }
    }

    public void RemoveSyncTombstone(string ownerKind, string ownerId, string kind, string uid)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM sync_tombstones WHERE owner_kind = $ok AND owner_id = $oi AND kind = $k AND uid = $u";
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$k", kind);
            cmd.Parameters.AddWithValue("$u", uid);
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    public string? GetSyncMeta(string ownerKind, string ownerId, string key)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM sync_meta WHERE owner_kind = $ok AND owner_id = $oi AND key = $k";
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$k", key);
            var result = cmd.ExecuteScalar();
            return result is null or DBNull ? null : (string)result;
        }
        finally { _lock.Release(); }
    }

    // Plain upsert with NO event: the coordinator's own bookkeeping writes (updated_at
    // echoes) must not signal a fresh pass. User-facing mutations go through MarkSyncDirty
    // instead; the coordinator's mid-pass re-dirtying uses MarkSyncDirty(signal: false).
    public void SetSyncMeta(string ownerKind, string ownerId, string key, string value)
    {
        AcquireLock();
        try { SetSyncMetaCore(ownerKind, ownerId, key, value); }
        finally { _lock.Release(); }
    }

    public void RemoveSyncMeta(string ownerKind, string ownerId, string key)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM sync_meta WHERE owner_kind = $ok AND owner_id = $oi AND key = $k";
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$k", key);
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    // Generation-guarded removal — the coordinator's clear-after-success for dirty flags:
    // deletes the key only while it still holds the exact value the pass read before
    // pushing. A MarkSyncDirty landing mid-push wrote a newer generation, so the clear
    // misses and the flag survives for the follow-up pass (the sync_meta twin of the rows'
    // rev-guarded ClearMacroDirty/ClearPresetDirty).
    public void RemoveSyncMetaIfValue(string ownerKind, string ownerId, string key, string value)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM sync_meta WHERE owner_kind = $ok AND owner_id = $oi AND key = $k AND value = $val";
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$val", value);
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    // The coordinator's device-state enumeration: every sync_meta entry whose key starts
    // with the given prefix (no LIKE wildcards expected in prefixes).
    public List<(string Key, string Value)> GetSyncMetaByPrefix(string ownerKind, string ownerId, string keyPrefix)
    {
        AcquireLock();
        try
        {
            var result = new List<(string, string)>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT key, value FROM sync_meta WHERE owner_kind = $ok AND owner_id = $oi AND key LIKE $prefix || '%' ORDER BY key";
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$prefix", keyPrefix);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add((reader.GetString(0), reader.GetString(1)));
            return result;
        }
        finally { _lock.Release(); }
    }

    // The marking entry point for state that lives outside a dirty-flagged row (preference
    // saves via SettingsRepository, applied-preset changes): writes a fresh generation as
    // the flag's value AND raises SyncRelevantDataChanged. The generation is what lets the
    // coordinator clear-after-success with RemoveSyncMetaIfValue — a mark landing while a
    // push is in flight carries a newer generation, so the stale clear misses; and unlike a
    // clear-first + restore choreography, an app exit mid-push loses nothing. signal: false
    // is for the coordinator's own mid-pass re-dirtying — the mark is consumed later in the
    // same pass, so no fresh signal is wanted. Guest owners are a full no-op by contract —
    // guest data never syncs.
    public void MarkSyncDirty(string ownerKind, string ownerId, string key, bool signal = true)
    {
        if (ownerKind != OwnerKindUser)
            return;

        AcquireLock();
        try { SetSyncMetaCore(ownerKind, ownerId, key, NextSyncDirtyGeneration()); }
        finally { _lock.Release(); }

        if (signal)
            RaiseSyncRelevantDataChanged();
    }

    // Monotonic within one app run and clock-seeded so a value left by a previous run never
    // collides with this run's — a pass that read a flag before the app restarted can never
    // guard-clear a mark written after it.
    private long _syncDirtyGeneration = DateTime.UtcNow.Ticks;

    private string NextSyncDirtyGeneration() =>
        Interlocked.Increment(ref _syncDirtyGeneration).ToString(CultureInfo.InvariantCulture);

    // Runs under _lock (callers hold it): the sync_meta upsert shared by SetSyncMeta,
    // MarkSyncDirty and SetAppliedPreset's dirty mark. The transaction is required whenever
    // the caller has one open — Microsoft.Data.Sqlite rejects a transactionless command on a
    // connection with a pending transaction (the roster marks inside AddDevice/DeleteDevice).
    private void SetSyncMetaCore(string ownerKind, string ownerId, string key, string value, SqliteTransaction? transaction = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT INTO sync_meta (owner_kind, owner_id, key, value)
            VALUES ($ok, $oi, $k, $val)
            ON CONFLICT(owner_kind, owner_id, key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$ok", ownerKind);
        cmd.Parameters.AddWithValue("$oi", ownerId);
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$val", value);
        cmd.ExecuteNonQuery();
    }

    // SetSyncMetaCore's delete twin, for the "last intent wins" swap the roster marks do
    // inside the device mutators' transactions.
    private void RemoveSyncMetaCore(string ownerKind, string ownerId, string key, SqliteTransaction? transaction = null)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "DELETE FROM sync_meta WHERE owner_kind = $ok AND owner_id = $oi AND key = $k";
        cmd.Parameters.AddWithValue("$ok", ownerKind);
        cmd.Parameters.AddWithValue("$oi", ownerId);
        cmd.Parameters.AddWithValue("$k", key);
        cmd.ExecuteNonQuery();
    }

    // ── Session ───────────────────────────────────────────────────────────────

    public void InitializeSession(Guid sessionId)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM app_session WHERE id = 1";
            if ((long)cmd.ExecuteScalar()! > 0)
                return;

            cmd.CommandText = "INSERT INTO app_session (id, session_uuid, started_at) VALUES (1, $uuid, $at)";
            cmd.Parameters.AddWithValue("$uuid", sessionId.ToString());
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    // Once-per-session latch for the guest expiry warning
    // (docs/plans/add-guest-session-expiry-warning). A missing row reads as "not sent", like every
    // other absent-row read here; it cannot cause a repeat warning in practice, because a session
    // that has no app_session row is never restored on the next launch either.
    // Call this pair only from the storage executor (RunOnStorageAsync). Read-then-write is only
    // atomic against other executor items — from a bare Task.Run it would sit outside the FIFO and
    // two callers could both claim the same warning.
    public bool GetSessionExpiryWarningSent()
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT expiry_warning_sent FROM app_session WHERE id = 1";
            var result = cmd.ExecuteScalar();
            return result is not (null or DBNull) && Convert.ToInt64(result) != 0;
        }
        finally { _lock.Release(); }
    }

    // Returns the number of rows updated, so the caller can tell "flag written" from "there was no
    // session row to write it to" — the UPDATE affects zero rows and still returns normally when
    // app_session is empty, and a claim reported on a write that never happened would let the
    // warning announce twice. Deliberately NOT an upsert: inserting the row here would fabricate an
    // app_session with an empty session_uuid, and InitializeSession early-returns when a row already
    // exists, so that phantom row would permanently stop the next real guest session from
    // persisting its identity.
    public int SetSessionExpiryWarningSent()
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE app_session SET expiry_warning_sent = 1 WHERE id = 1";
            return cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    // Removes the persisted session, its session-scoped device list and the guest bell history,
    // without touching saved_auth or owner-scoped guest preference data. Sign-in calls this so a
    // finished guest session cannot be resurrected on the next launch; ClearData remains the
    // guest-expiry wipe (session + guest-owned rows). Guest notifications go here too (PR #112
    // m2): they are keyed by the finished session's uuid, so after sign-in no future owner could
    // ever load them — left behind they are permanent orphans, not reusable guest data.
    public void ClearSession()
    {
        AcquireLock();
        try
        {
            // One transaction, like ClearData: without it each DELETE autocommits on its own, and
            // a failure between them leaves orphaned `devices` rows with no session — which the
            // next guest restore would silently re-attach (GetDevices reads `devices` without
            // joining app_session, so nothing downstream filters them out).
            // Guest rows only: a signed-in user's cards are owner-scoped and must survive the
            // sign-in that calls this.
            using var transaction = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                DELETE FROM app_session;
                DELETE FROM devices WHERE owner_kind = 'guest';
                DELETE FROM notifications WHERE owner_kind = 'guest';
                """;
            cmd.ExecuteNonQuery();
            transaction.Commit();
        }
        finally { _lock.Release(); }
    }

    public (SessionState State, Guid? Uuid, DateTime? StartedAt) GetSessionState()
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT started_at, session_uuid FROM app_session WHERE id = 1";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return (SessionState.None, null, null);

            var startedAt = DateTime.Parse(reader.GetString(0), null, DateTimeStyles.RoundtripKind);
            var state = (DateTime.UtcNow - startedAt).TotalHours >= AppSettings.SessionTimeHours
                ? SessionState.Expired
                : SessionState.Active;

            Guid? uuid = null;
            if (!reader.IsDBNull(1) && Guid.TryParse(reader.GetString(1), out var g))
                uuid = g;

            return (state, uuid, startedAt);
        }
        finally { _lock.Release(); }
    }

    // Wipes guest-scoped state on session expiry. User rows in owner-scoped tables are
    // untouched — a signed-in user's persisted preferences must survive a guest-session
    // expiry that happens to fire on the same DB.
    public void ClearData()
    {
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();

            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    DELETE FROM devices WHERE owner_kind = 'guest';
                    DELETE FROM app_session;
                    DELETE FROM notifications WHERE owner_kind = 'guest';
                    DELETE FROM device_settings WHERE owner_kind = 'guest';
                    DELETE FROM app_preferences WHERE owner_kind = 'guest';
                    DELETE FROM device_rgb WHERE owner_kind = 'guest';
                    DELETE FROM device_rgb_effect WHERE owner_kind = 'guest';
                    DELETE FROM button_bindings WHERE owner_kind = 'guest';
                    DELETE FROM key_macro_bindings WHERE owner_kind = 'guest';
                    DELETE FROM device_actuation WHERE owner_kind = 'guest';
                    DELETE FROM device_extended_keys WHERE owner_kind = 'guest';
                    -- Delete events for guest macros before the macros themselves.
                    DELETE FROM macro_events WHERE macro_id IN (
                        SELECT id FROM macros WHERE owner_kind = 'guest'
                    );
                    DELETE FROM macros WHERE owner_kind = 'guest';
                    -- Cascade preset snapshots for guest-owned presets before dropping
                    -- the presets themselves.
                    DELETE FROM preset_button_bindings WHERE preset_id IN (
                        SELECT id FROM presets WHERE owner_kind = 'guest'
                    );
                    DELETE FROM preset_rgb_effect WHERE preset_id IN (
                        SELECT id FROM presets WHERE owner_kind = 'guest'
                    );
                    DELETE FROM preset_rgb WHERE preset_id IN (
                        SELECT id FROM presets WHERE owner_kind = 'guest'
                    );
                    DELETE FROM preset_settings WHERE preset_id IN (
                        SELECT id FROM presets WHERE owner_kind = 'guest'
                    );
                    DELETE FROM preset_key_bindings WHERE preset_id IN (
                        SELECT id FROM presets WHERE owner_kind = 'guest'
                    );
                    DELETE FROM preset_actuation WHERE preset_id IN (
                        SELECT id FROM presets WHERE owner_kind = 'guest'
                    );
                    DELETE FROM preset_extended_keys WHERE preset_id IN (
                        SELECT id FROM presets WHERE owner_kind = 'guest'
                    );
                    DELETE FROM preset_key_macro_bindings WHERE preset_id IN (
                        SELECT id FROM presets WHERE owner_kind = 'guest'
                    );
                    DELETE FROM presets WHERE owner_kind = 'guest';
                    """;
                cmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        finally { _lock.Release(); }
    }

    // ── Presets (per owner, per device type — VID/PID) ─────────────────────────

    public List<PresetRecord> GetPresets(string ownerKind, string ownerId, int vendorId, int productId)
    {
        AcquireLock();
        try
        {
            var result = new List<PresetRecord>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT id, name, description, is_default, uid, server_updated_at, is_dirty FROM presets WHERE owner_kind = $ok AND owner_id = $oi AND vendor_id = $v AND product_id = $p ORDER BY id";
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$v", vendorId);
            cmd.Parameters.AddWithValue("$p", productId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add(new PresetRecord
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    Description = reader.GetString(2),
                    IsDefault = reader.GetInt32(3) != 0,
                    Uid = reader.GetString(4),
                    ServerUpdatedAt = reader.IsDBNull(5) ? null : reader.GetString(5),
                    // The pull's default arbitration pre-checks DeleteNeverSyncedPreset's
                    // "local-born, never pushed" predicate off this record — both halves
                    // (ServerUpdatedAt + IsDirty) must be populated.
                    IsDirty = reader.GetInt32(6) != 0,
                    // Populate owner scope on read (see PresetRecord.OwnerKind/OwnerId).
                    // Callers that already have the scope in local vars don't have to look
                    // at the record; callers that pass records around do.
                    OwnerKind = ownerKind,
                    OwnerId = ownerId,
                });
            return result;
        }
        finally { _lock.Release(); }
    }

    // GetMacroIdsByUid's preset twin — the sign-in pull's bulk absence check.
    public Dictionary<string, int> GetPresetIdsByUid(string ownerKind, string ownerId)
    {
        AcquireLock();
        try
        {
            var result = new Dictionary<string, int>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT uid, id FROM presets WHERE owner_kind = $ok AND owner_id = $oi";
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result[reader.GetString(0)] = reader.GetInt32(1);
            return result;
        }
        finally { _lock.Release(); }
    }

    // markDirty: false is for the sign-in pull — same contract as SaveMacro.
    public int InsertPreset(string ownerKind, string ownerId, int vendorId, int productId, PresetRecord preset, bool markDirty = true)
    {
        bool marked;
        int id;
        AcquireLock();
        try
        {
            // A non-empty Uid on the record wins (tests pin known uids); otherwise generate.
            // "D" format matches the API's lowercase hyphenated UUID examples.
            var uid = string.IsNullOrEmpty(preset.Uid) ? Guid.NewGuid().ToString("D") : preset.Uid;

            // Born dirty/unsynced for user rows; the pull's inserts pass markDirty: false
            // (server content) and stay clean — same rule as SaveMacro.
            marked = markDirty && ownerKind == OwnerKindUser;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO presets (owner_kind, owner_id, vendor_id, product_id, name, description, is_default, created_at, uid, server_updated_at, is_dirty, local_rev)
                VALUES ($ok, $oi, $v, $p, $n, $d, $def, $ts, $u, $su, $dirty, $rev);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$v", vendorId);
            cmd.Parameters.AddWithValue("$p", productId);
            cmd.Parameters.AddWithValue("$n", preset.Name);
            cmd.Parameters.AddWithValue("$d", preset.Description ?? string.Empty);
            cmd.Parameters.AddWithValue("$def", preset.IsDefault ? 1 : 0);
            cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cmd.Parameters.AddWithValue("$u", uid);
            // Honoured on insert for the sign-in pull (see SaveMacro); null everywhere else.
            cmd.Parameters.AddWithValue("$su", (object?)preset.ServerUpdatedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$dirty", marked ? 1 : 0);
            cmd.Parameters.AddWithValue("$rev", marked ? 1 : 0);
            id = Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            preset.Id = id;
            preset.Uid = uid;
            // Mirror the owner scope onto the record so downstream code that only holds
            // the object (not the parameters) sees the same values as GetPresets would return.
            preset.OwnerKind = ownerKind;
            preset.OwnerId = ownerId;
        }
        finally { _lock.Release(); }

        if (marked)
            RaiseSyncRelevantDataChanged();
        return id;
    }

    public void DeletePreset(int presetId)
    {
        var tombstoned = false;
        AcquireLock();
        try
        {
            // Safety net: refuse to delete the row while it is marked as the default
            // preset for its (owner, device type). The ViewModel already hides the delete
            // button for defaults, but this guarantees the invariant in storage.
            using (var guard = _connection.CreateCommand())
            {
                guard.CommandText = "SELECT is_default FROM presets WHERE id = $id";
                guard.Parameters.AddWithValue("$id", presetId);
                var row = guard.ExecuteScalar();
                if (row is not null && Convert.ToInt32(row, CultureInfo.InvariantCulture) != 0)
                    return;
            }

            using var transaction = _connection.BeginTransaction();

            tombstoned = WriteTombstoneCore(transaction, "presets", TombstoneKindPreset, presetId);
            DeletePresetCascadeCore(transaction, presetId);
            transaction.Commit();
        }
        finally { _lock.Release(); }

        if (tombstoned)
            RaiseSyncRelevantDataChanged();
    }

    // Deletes a preset ONLY if it is local-born and never pushed (server_updated_at IS NULL
    // AND is_dirty = 1); returns whether the row was deleted. IS NULL alone is not enough:
    // a coordinator create whose 2xx echoed no updated_at clears dirty but leaves the stamp
    // NULL, and a pull-inserted row is born clean with a NULL stamp when the server omits
    // updated_at — both ARE server data. Requiring is_dirty = 1 excludes exactly those (a
    // seed is born dirty, ClearPresetDirty only runs after a push reached the server). No
    // tombstone is ever written — under the guard the server has nothing to remove (the
    // same never-synced rule WriteTombstoneCore applies). The guard runs inside the
    // delete's transaction so a concurrent push that just synced the row makes this refuse
    // rather than orphan server data. Unlike DeletePreset this
    // intentionally accepts is_default rows: the only caller is the sign-in pull's default
    // arbitration superseding a just-seeded local default with the server's, and it promotes
    // the server's row immediately after, restoring the one-default invariant
    // (docs/plans/fix-duplicate-default-preset).
    public bool DeleteNeverSyncedPreset(int presetId)
    {
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();
            using (var guard = _connection.CreateCommand())
            {
                guard.Transaction = transaction;
                guard.CommandText = "SELECT 1 FROM presets WHERE id = $id AND server_updated_at IS NULL AND is_dirty = 1";
                guard.Parameters.AddWithValue("$id", presetId);
                if (guard.ExecuteScalar() is null)
                    return false;
            }

            DeletePresetCascadeCore(transaction, presetId);
            transaction.Commit();
            return true;
        }
        finally { _lock.Release(); }
    }

    // The shared delete of DeletePreset / DeleteNeverSyncedPreset. Fresh installs have FK
    // ON DELETE CASCADE on the preset_* tables (see EnsureSchema) so deleting the presets
    // row alone would suffice. Legacy DBs created before the FK was declared don't cascade
    // automatically — this explicit cascade is idempotent on new DBs (the child rows are
    // gone by the time the parent DELETE runs) and load-bearing on old ones.
    private void DeletePresetCascadeCore(SqliteTransaction transaction, int presetId)
    {
        using var cascade = _connection.CreateCommand();
        cascade.Transaction = transaction;
        cascade.CommandText = """
            DELETE FROM preset_button_bindings WHERE preset_id = $id;
            DELETE FROM preset_rgb_effect     WHERE preset_id = $id;
            DELETE FROM preset_rgb            WHERE preset_id = $id;
            DELETE FROM preset_settings       WHERE preset_id = $id;
            DELETE FROM preset_key_bindings   WHERE preset_id = $id;
            DELETE FROM preset_key_macro_bindings WHERE preset_id = $id;
            DELETE FROM preset_actuation      WHERE preset_id = $id;
            DELETE FROM preset_extended_keys  WHERE preset_id = $id;
            DELETE FROM presets               WHERE id        = $id;
            """;
        cascade.Parameters.AddWithValue("$id", presetId);
        cascade.ExecuteNonQuery();
    }

    // Seeds a default preset for (owner, device type) when the scope has zero rows, and
    // promotes the first existing row when rows exist but none is marked default (this
    // covers legacy DBs created before is_default existed). Idempotent — calling it a
    // second time on a scope that already has a default is a no-op. Returns the id of
    // the row that ended up as the default, or 0 if nothing needed to change.
    // The caller then re-reads with GetPresets to see the resulting list.
    //
    // Seeded distinguishes "this call INSERTed the row" (Case 3) from "an existing row was
    // promoted" (Case 2). It is the only safe signal for a caller that wants to capture a
    // snapshot into the default preset: a row inserted microseconds ago has no snapshot to
    // destroy, whereas a promoted legacy row may already carry one — and overwriting THAT is
    // what wiped existing installs on their first post-upgrade open.
    public (int Id, bool Seeded) CreateDefaultPresetIfNotExists(string ownerKind, string ownerId, int vendorId, int productId, string defaultName)
    {
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();

            int? existingDefaultId = null;
            int? firstRowId = null;
            using (var scan = _connection.CreateCommand())
            {
                scan.Transaction = transaction;
                scan.CommandText = "SELECT id, is_default FROM presets WHERE owner_kind = $ok AND owner_id = $oi AND vendor_id = $v AND product_id = $p ORDER BY id";
                scan.Parameters.AddWithValue("$ok", ownerKind);
                scan.Parameters.AddWithValue("$oi", ownerId);
                scan.Parameters.AddWithValue("$v", vendorId);
                scan.Parameters.AddWithValue("$p", productId);
                using var reader = scan.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetInt32(0);
                    var isDefault = reader.GetInt32(1) != 0;
                    firstRowId ??= id;
                    if (isDefault) { existingDefaultId = id; break; }
                }
            }

            // Case 1 — a default already exists; nothing to do.
            if (existingDefaultId is not null)
            {
                transaction.Commit();
                return (0, false);
            }

            // Case 2 — legacy DB with rows but no default; promote the first row.
            if (firstRowId is not null)
            {
                using (var promote = _connection.CreateCommand())
                {
                    promote.Transaction = transaction;
                    promote.CommandText = "UPDATE presets SET is_default = 1 WHERE id = $id";
                    promote.Parameters.AddWithValue("$id", firstRowId.Value);
                    promote.ExecuteNonQuery();
                }
                transaction.Commit();
                return (firstRowId.Value, false);
            }

            // Case 3 — empty scope; insert a fresh default row. Born dirty for user rows
            // (InsertPreset's rule): the seed is local-born content the coordinator is
            // expected to push, and is_dirty = 1 is half of DeleteNeverSyncedPreset's
            // "local-born, never pushed" guard — a clean seed could never be superseded
            // by the sign-in pull's default arbitration.
            int seededId;
            var seedDirty = ownerKind == OwnerKindUser ? 1 : 0;
            using (var insert = _connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO presets (owner_kind, owner_id, vendor_id, product_id, name, description, is_default, created_at, uid, is_dirty, local_rev)
                    VALUES ($ok, $oi, $v, $p, $n, '', 1, $ts, $u, $dirty, $rev);
                    SELECT last_insert_rowid();
                    """;
                insert.Parameters.AddWithValue("$ok", ownerKind);
                insert.Parameters.AddWithValue("$oi", ownerId);
                insert.Parameters.AddWithValue("$v", vendorId);
                insert.Parameters.AddWithValue("$p", productId);
                insert.Parameters.AddWithValue("$n", defaultName);
                insert.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                // Every insert path must supply a uid: a second '' row would trip idx_presets_uid.
                insert.Parameters.AddWithValue("$u", Guid.NewGuid().ToString("D"));
                insert.Parameters.AddWithValue("$dirty", seedDirty);
                insert.Parameters.AddWithValue("$rev", seedDirty);
                seededId = Convert.ToInt32(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            transaction.Commit();
            return (seededId, true);
        }
        finally { _lock.Release(); }
    }

    // Marks a single preset as the default for its (owner, device type), clearing the flag
    // on every sibling row. Called when the user toggles "Set as default" while
    // creating / editing a preset, and by the auto-create path in the ViewModel.
    // markDirty: false is for the sign-in pull's default arbitration — the flag it applies
    // IS the server's state, so re-pushing it would be an echo (the pull never raises
    // SyncRelevantDataChanged). Every user-driven caller keeps the default and dirty-marks
    // BOTH affected rows: the promoted preset and the demoted previous default.
    public void PromotePresetToDefault(int presetId, bool markDirty = true)
    {
        var marked = false;
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();

            // Look up the target preset's scope so we only demote its own siblings.
            string ownerKind = string.Empty, ownerId = string.Empty;
            int vendorId = 0, productId = 0;
            using (var scope = _connection.CreateCommand())
            {
                scope.Transaction = transaction;
                scope.CommandText = "SELECT owner_kind, owner_id, vendor_id, product_id FROM presets WHERE id = $id";
                scope.Parameters.AddWithValue("$id", presetId);
                using var reader = scope.ExecuteReader();
                if (!reader.Read())
                {
                    transaction.Rollback();
                    return;
                }
                ownerKind = reader.GetString(0);
                ownerId = reader.GetString(1);
                vendorId = reader.GetInt32(2);
                productId = reader.GetInt32(3);
            }

            // When dirty-marking, both statements narrow to rows whose is_default actually
            // flips — a row that keeps its flag must not have its local_rev bumped (the rev
            // guard would otherwise miss and re-push an unchanged row). The unfiltered shapes
            // are kept for the non-marking path so guest/pull behavior is bit-for-bit what it
            // was (including the legacy multi-default wipe of already-0 rows, a no-op).
            var mark = markDirty && ownerKind == OwnerKindUser;

            using (var demote = _connection.CreateCommand())
            {
                // Scoped by device TYPE, matching GetPresets. It previously also filtered on the
                // target row's device_key, which only worked because every row written since the
                // rescope has device_key = '' — on a database still holding legacy rows it demoted
                // just the new ones and silently left a second default behind.
                demote.Transaction = transaction;
                demote.CommandText = mark
                    ? "UPDATE presets SET is_default = 0, is_dirty = 1, local_rev = local_rev + 1 WHERE owner_kind = $ok AND owner_id = $oi AND vendor_id = $v AND product_id = $p AND id <> $id AND is_default = 1"
                    : "UPDATE presets SET is_default = 0 WHERE owner_kind = $ok AND owner_id = $oi AND vendor_id = $v AND product_id = $p AND id <> $id";
                demote.Parameters.AddWithValue("$ok", ownerKind);
                demote.Parameters.AddWithValue("$oi", ownerId);
                demote.Parameters.AddWithValue("$v", vendorId);
                demote.Parameters.AddWithValue("$p", productId);
                demote.Parameters.AddWithValue("$id", presetId);
                marked |= demote.ExecuteNonQuery() > 0 && mark;
            }

            using (var promote = _connection.CreateCommand())
            {
                promote.Transaction = transaction;
                promote.CommandText = mark
                    ? "UPDATE presets SET is_default = 1, is_dirty = 1, local_rev = local_rev + 1 WHERE id = $id AND is_default = 0"
                    : "UPDATE presets SET is_default = 1 WHERE id = $id";
                promote.Parameters.AddWithValue("$id", presetId);
                marked |= promote.ExecuteNonQuery() > 0 && mark;
            }

            transaction.Commit();
        }
        finally { _lock.Release(); }

        if (marked)
            RaiseSyncRelevantDataChanged();
    }

    // Drops the preset's captured RGB / button-binding snapshot. Leaves the presets row
    // (name / description / default flag) untouched. Called from the row-menu Reset flow.
    public void ResetPresetSnapshot(int presetId)
    {
        bool marked;
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();
            using (var wipe = _connection.CreateCommand())
            {
                wipe.Transaction = transaction;
                wipe.CommandText = """
                    DELETE FROM preset_button_bindings WHERE preset_id = $id;
                    DELETE FROM preset_rgb_effect     WHERE preset_id = $id;
                    DELETE FROM preset_rgb            WHERE preset_id = $id;
                    DELETE FROM preset_settings       WHERE preset_id = $id;
                    DELETE FROM preset_key_bindings   WHERE preset_id = $id;
                    DELETE FROM preset_key_macro_bindings WHERE preset_id = $id;
                    DELETE FROM preset_actuation      WHERE preset_id = $id;
                    DELETE FROM preset_extended_keys  WHERE preset_id = $id;
                    """;
                wipe.Parameters.AddWithValue("$id", presetId);
                wipe.ExecuteNonQuery();
            }
            marked = MarkPresetDirtyCore(transaction, presetId);
            transaction.Commit();
        }
        finally { _lock.Release(); }

        if (marked)
            RaiseSyncRelevantDataChanged();
    }

    // Reverse of SavePresetSnapshot: overwrites the (owner, device) rows in device_rgb /
    // device_rgb_effect / button_bindings with the snapshot stored under presetId. If the
    // preset holds no snapshot (e.g. just reset), the device rows end up empty and the
    // next section-reload falls back to defaults.
    public void LoadPresetSnapshotToDevice(int presetId, string ownerKind, string ownerId,
        int vendorId, int productId, string deviceKey)
    {
        deviceKey ??= string.Empty;
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();

            using (var wipe = _connection.CreateCommand())
            {
                wipe.Transaction = transaction;
                wipe.CommandText = """
                    DELETE FROM device_rgb        WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk;
                    DELETE FROM device_rgb_effect WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk;
                    DELETE FROM button_bindings   WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk;
                    DELETE FROM device_settings   WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk;
                    DELETE FROM key_bindings      WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk;
                    DELETE FROM key_macro_bindings WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk;
                    DELETE FROM device_actuation  WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk;
                    DELETE FROM device_extended_keys WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk;
                    """;
                wipe.Parameters.AddWithValue("$id", presetId);
                wipe.Parameters.AddWithValue("$ok", ownerKind);
                wipe.Parameters.AddWithValue("$oi", ownerId);
                wipe.Parameters.AddWithValue("$v", vendorId);
                wipe.Parameters.AddWithValue("$p", productId);
                wipe.Parameters.AddWithValue("$dk", deviceKey);
                wipe.ExecuteNonQuery();
            }

            using (var copyHead = _connection.CreateCommand())
            {
                copyHead.Transaction = transaction;
                copyHead.CommandText = """
                    INSERT INTO device_rgb (owner_kind, owner_id, vendor_id, product_id, device_key, is_enabled, selected_effect)
                    SELECT $ok, $oi, $v, $p, $dk, is_enabled, selected_effect
                    FROM preset_rgb WHERE preset_id = $id;
                    """;
                copyHead.Parameters.AddWithValue("$id", presetId);
                copyHead.Parameters.AddWithValue("$ok", ownerKind);
                copyHead.Parameters.AddWithValue("$oi", ownerId);
                copyHead.Parameters.AddWithValue("$v", vendorId);
                copyHead.Parameters.AddWithValue("$p", productId);
                copyHead.Parameters.AddWithValue("$dk", deviceKey);
                copyHead.ExecuteNonQuery();
            }

            using (var copyEffects = _connection.CreateCommand())
            {
                copyEffects.Transaction = transaction;
                copyEffects.CommandText = """
                    INSERT INTO device_rgb_effect (owner_kind, owner_id, vendor_id, product_id, device_key, effect_key, hex, brightness, speed, is_rainbow)
                    SELECT $ok, $oi, $v, $p, $dk, effect_key, hex, brightness, speed, is_rainbow
                    FROM preset_rgb_effect WHERE preset_id = $id;
                    """;
                copyEffects.Parameters.AddWithValue("$id", presetId);
                copyEffects.Parameters.AddWithValue("$ok", ownerKind);
                copyEffects.Parameters.AddWithValue("$oi", ownerId);
                copyEffects.Parameters.AddWithValue("$v", vendorId);
                copyEffects.Parameters.AddWithValue("$p", productId);
                copyEffects.Parameters.AddWithValue("$dk", deviceKey);
                copyEffects.ExecuteNonQuery();
            }

            using (var copyBindings = _connection.CreateCommand())
            {
                copyBindings.Transaction = transaction;
                copyBindings.CommandText = """
                    INSERT INTO button_bindings (owner_kind, owner_id, vendor_id, product_id, device_key, button, action, macro_id)
                    SELECT $ok, $oi, $v, $p, $dk, button, action, macro_id
                    FROM preset_button_bindings WHERE preset_id = $id;
                    """;
                copyBindings.Parameters.AddWithValue("$id", presetId);
                copyBindings.Parameters.AddWithValue("$ok", ownerKind);
                copyBindings.Parameters.AddWithValue("$oi", ownerId);
                copyBindings.Parameters.AddWithValue("$v", vendorId);
                copyBindings.Parameters.AddWithValue("$p", productId);
                copyBindings.Parameters.AddWithValue("$dk", deviceKey);
                copyBindings.ExecuteNonQuery();
            }

            using (var copySettings = _connection.CreateCommand())
            {
                copySettings.Transaction = transaction;
                copySettings.CommandText = """
                    INSERT INTO device_settings (owner_kind, owner_id, vendor_id, product_id, device_key, key, value)
                    SELECT $ok, $oi, $v, $p, $dk, key, value
                    FROM preset_settings WHERE preset_id = $id;
                    """;
                copySettings.Parameters.AddWithValue("$id", presetId);
                copySettings.Parameters.AddWithValue("$ok", ownerKind);
                copySettings.Parameters.AddWithValue("$oi", ownerId);
                copySettings.Parameters.AddWithValue("$v", vendorId);
                copySettings.Parameters.AddWithValue("$p", productId);
                copySettings.Parameters.AddWithValue("$dk", deviceKey);
                copySettings.ExecuteNonQuery();
            }

            using (var copyKeys = _connection.CreateCommand())
            {
                copyKeys.Transaction = transaction;
                copyKeys.CommandText = """
                    INSERT INTO key_bindings (owner_kind, owner_id, vendor_id, product_id, device_key, phys_scancode, type_byte, bitmask, target_code)
                    SELECT $ok, $oi, $v, $p, $dk, phys_scancode, type_byte, bitmask, target_code
                    FROM preset_key_bindings WHERE preset_id = $id;
                    """;
                copyKeys.Parameters.AddWithValue("$id", presetId);
                copyKeys.Parameters.AddWithValue("$ok", ownerKind);
                copyKeys.Parameters.AddWithValue("$oi", ownerId);
                copyKeys.Parameters.AddWithValue("$v", vendorId);
                copyKeys.Parameters.AddWithValue("$p", productId);
                copyKeys.Parameters.AddWithValue("$dk", deviceKey);
                copyKeys.ExecuteNonQuery();
            }

            using (var copyKeyMacros = _connection.CreateCommand())
            {
                copyKeyMacros.Transaction = transaction;
                copyKeyMacros.CommandText = """
                    INSERT INTO key_macro_bindings (owner_kind, owner_id, vendor_id, product_id, device_key, matrix_index, macro_id)
                    SELECT $ok, $oi, $v, $p, $dk, matrix_index, macro_id
                    FROM preset_key_macro_bindings WHERE preset_id = $id;
                    """;
                copyKeyMacros.Parameters.AddWithValue("$id", presetId);
                copyKeyMacros.Parameters.AddWithValue("$ok", ownerKind);
                copyKeyMacros.Parameters.AddWithValue("$oi", ownerId);
                copyKeyMacros.Parameters.AddWithValue("$v", vendorId);
                copyKeyMacros.Parameters.AddWithValue("$p", productId);
                copyKeyMacros.Parameters.AddWithValue("$dk", deviceKey);
                copyKeyMacros.ExecuteNonQuery();
            }

            using (var copyActuation = _connection.CreateCommand())
            {
                copyActuation.Transaction = transaction;
                copyActuation.CommandText = """
                    INSERT INTO device_actuation (owner_kind, owner_id, vendor_id, product_id, device_key, slot, press_centi_mm, release_centi_mm, rapid_trigger, quick_press_centi_mm, quick_release_centi_mm)
                    SELECT $ok, $oi, $v, $p, $dk, slot, press_centi_mm, release_centi_mm, rapid_trigger, quick_press_centi_mm, quick_release_centi_mm
                    FROM preset_actuation WHERE preset_id = $id;
                    """;
                copyActuation.Parameters.AddWithValue("$id", presetId);
                copyActuation.Parameters.AddWithValue("$ok", ownerKind);
                copyActuation.Parameters.AddWithValue("$oi", ownerId);
                copyActuation.Parameters.AddWithValue("$v", vendorId);
                copyActuation.Parameters.AddWithValue("$p", productId);
                copyActuation.Parameters.AddWithValue("$dk", deviceKey);
                copyActuation.ExecuteNonQuery();
            }

            using (var copyExtendedKeys = _connection.CreateCommand())
            {
                copyExtendedKeys.Transaction = transaction;
                copyExtendedKeys.CommandText = """
                    INSERT INTO device_extended_keys (owner_kind, owner_id, vendor_id, product_id, device_key, mode, slot1, slot2, priority, time_ms, hold_type, hold_code1, hold_code2, tap_type, tap_code1, tap_code2)
                    SELECT $ok, $oi, $v, $p, $dk, mode, slot1, slot2, priority, time_ms, hold_type, hold_code1, hold_code2, tap_type, tap_code1, tap_code2
                    FROM preset_extended_keys WHERE preset_id = $id;
                    """;
                copyExtendedKeys.Parameters.AddWithValue("$id", presetId);
                copyExtendedKeys.Parameters.AddWithValue("$ok", ownerKind);
                copyExtendedKeys.Parameters.AddWithValue("$oi", ownerId);
                copyExtendedKeys.Parameters.AddWithValue("$v", vendorId);
                copyExtendedKeys.Parameters.AddWithValue("$p", productId);
                copyExtendedKeys.Parameters.AddWithValue("$dk", deviceKey);
                copyExtendedKeys.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        finally { _lock.Release(); }
    }

    // Snapshots the current device_rgb / device_rgb_effect / button_bindings rows for the
    // given (owner, device) into the preset_* tables under presetId, replacing any prior
    // snapshot for that preset. Runs in a single transaction so partial writes can't leave
    // a mismatched snapshot on disk.
    public void SavePresetSnapshot(int presetId, string ownerKind, string ownerId,
        int vendorId, int productId, string deviceKey)
    {
        deviceKey ??= string.Empty;
        bool marked;
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();

            using (var wipe = _connection.CreateCommand())
            {
                wipe.Transaction = transaction;
                wipe.CommandText = """
                    DELETE FROM preset_button_bindings WHERE preset_id = $id;
                    DELETE FROM preset_rgb_effect     WHERE preset_id = $id;
                    DELETE FROM preset_rgb            WHERE preset_id = $id;
                    DELETE FROM preset_settings       WHERE preset_id = $id;
                    DELETE FROM preset_key_bindings   WHERE preset_id = $id;
                    DELETE FROM preset_key_macro_bindings WHERE preset_id = $id;
                    DELETE FROM preset_actuation      WHERE preset_id = $id;
                    DELETE FROM preset_extended_keys  WHERE preset_id = $id;
                    """;
                wipe.Parameters.AddWithValue("$id", presetId);
                wipe.ExecuteNonQuery();
            }

            using (var copyHead = _connection.CreateCommand())
            {
                copyHead.Transaction = transaction;
                copyHead.CommandText = """
                    INSERT INTO preset_rgb (preset_id, is_enabled, selected_effect)
                    SELECT $id, is_enabled, selected_effect
                    FROM device_rgb
                    WHERE owner_kind = $ok AND owner_id = $oi
                      AND vendor_id = $v AND product_id = $p AND device_key = $dk;
                    """;
                copyHead.Parameters.AddWithValue("$id", presetId);
                copyHead.Parameters.AddWithValue("$ok", ownerKind);
                copyHead.Parameters.AddWithValue("$oi", ownerId);
                copyHead.Parameters.AddWithValue("$v", vendorId);
                copyHead.Parameters.AddWithValue("$p", productId);
                copyHead.Parameters.AddWithValue("$dk", deviceKey);
                copyHead.ExecuteNonQuery();
            }

            using (var copyEffects = _connection.CreateCommand())
            {
                copyEffects.Transaction = transaction;
                copyEffects.CommandText = """
                    INSERT INTO preset_rgb_effect (preset_id, effect_key, hex, brightness, speed, is_rainbow)
                    SELECT $id, effect_key, hex, brightness, speed, is_rainbow
                    FROM device_rgb_effect
                    WHERE owner_kind = $ok AND owner_id = $oi
                      AND vendor_id = $v AND product_id = $p AND device_key = $dk;
                    """;
                copyEffects.Parameters.AddWithValue("$id", presetId);
                copyEffects.Parameters.AddWithValue("$ok", ownerKind);
                copyEffects.Parameters.AddWithValue("$oi", ownerId);
                copyEffects.Parameters.AddWithValue("$v", vendorId);
                copyEffects.Parameters.AddWithValue("$p", productId);
                copyEffects.Parameters.AddWithValue("$dk", deviceKey);
                copyEffects.ExecuteNonQuery();
            }

            using (var copyBindings = _connection.CreateCommand())
            {
                copyBindings.Transaction = transaction;
                copyBindings.CommandText = """
                    INSERT INTO preset_button_bindings (preset_id, button, action, macro_id)
                    SELECT $id, button, action, macro_id
                    FROM button_bindings
                    WHERE owner_kind = $ok AND owner_id = $oi
                      AND vendor_id = $v AND product_id = $p AND device_key = $dk;
                    """;
                copyBindings.Parameters.AddWithValue("$id", presetId);
                copyBindings.Parameters.AddWithValue("$ok", ownerKind);
                copyBindings.Parameters.AddWithValue("$oi", ownerId);
                copyBindings.Parameters.AddWithValue("$v", vendorId);
                copyBindings.Parameters.AddWithValue("$p", productId);
                copyBindings.Parameters.AddWithValue("$dk", deviceKey);
                copyBindings.ExecuteNonQuery();
            }

            using (var copySettings = _connection.CreateCommand())
            {
                copySettings.Transaction = transaction;
                copySettings.CommandText = """
                    INSERT INTO preset_settings (preset_id, key, value)
                    SELECT $id, key, value
                    FROM device_settings
                    WHERE owner_kind = $ok AND owner_id = $oi
                      AND vendor_id = $v AND product_id = $p AND device_key = $dk;
                    """;
                copySettings.Parameters.AddWithValue("$id", presetId);
                copySettings.Parameters.AddWithValue("$ok", ownerKind);
                copySettings.Parameters.AddWithValue("$oi", ownerId);
                copySettings.Parameters.AddWithValue("$v", vendorId);
                copySettings.Parameters.AddWithValue("$p", productId);
                copySettings.Parameters.AddWithValue("$dk", deviceKey);
                copySettings.ExecuteNonQuery();
            }

            using (var copyKeys = _connection.CreateCommand())
            {
                copyKeys.Transaction = transaction;
                copyKeys.CommandText = """
                    INSERT INTO preset_key_bindings (preset_id, phys_scancode, type_byte, bitmask, target_code)
                    SELECT $id, phys_scancode, type_byte, bitmask, target_code
                    FROM key_bindings
                    WHERE owner_kind = $ok AND owner_id = $oi
                      AND vendor_id = $v AND product_id = $p AND device_key = $dk;
                    """;
                copyKeys.Parameters.AddWithValue("$id", presetId);
                copyKeys.Parameters.AddWithValue("$ok", ownerKind);
                copyKeys.Parameters.AddWithValue("$oi", ownerId);
                copyKeys.Parameters.AddWithValue("$v", vendorId);
                copyKeys.Parameters.AddWithValue("$p", productId);
                copyKeys.Parameters.AddWithValue("$dk", deviceKey);
                copyKeys.ExecuteNonQuery();
            }

            using (var copyKeyMacros = _connection.CreateCommand())
            {
                copyKeyMacros.Transaction = transaction;
                copyKeyMacros.CommandText = """
                    INSERT INTO preset_key_macro_bindings (preset_id, matrix_index, macro_id)
                    SELECT $id, matrix_index, macro_id
                    FROM key_macro_bindings
                    WHERE owner_kind = $ok AND owner_id = $oi
                      AND vendor_id = $v AND product_id = $p AND device_key = $dk;
                    """;
                copyKeyMacros.Parameters.AddWithValue("$id", presetId);
                copyKeyMacros.Parameters.AddWithValue("$ok", ownerKind);
                copyKeyMacros.Parameters.AddWithValue("$oi", ownerId);
                copyKeyMacros.Parameters.AddWithValue("$v", vendorId);
                copyKeyMacros.Parameters.AddWithValue("$p", productId);
                copyKeyMacros.Parameters.AddWithValue("$dk", deviceKey);
                copyKeyMacros.ExecuteNonQuery();
            }

            using (var copyActuation = _connection.CreateCommand())
            {
                copyActuation.Transaction = transaction;
                copyActuation.CommandText = """
                    INSERT INTO preset_actuation (preset_id, slot, press_centi_mm, release_centi_mm, rapid_trigger, quick_press_centi_mm, quick_release_centi_mm)
                    SELECT $id, slot, press_centi_mm, release_centi_mm, rapid_trigger, quick_press_centi_mm, quick_release_centi_mm
                    FROM device_actuation
                    WHERE owner_kind = $ok AND owner_id = $oi
                      AND vendor_id = $v AND product_id = $p AND device_key = $dk;
                    """;
                copyActuation.Parameters.AddWithValue("$id", presetId);
                copyActuation.Parameters.AddWithValue("$ok", ownerKind);
                copyActuation.Parameters.AddWithValue("$oi", ownerId);
                copyActuation.Parameters.AddWithValue("$v", vendorId);
                copyActuation.Parameters.AddWithValue("$p", productId);
                copyActuation.Parameters.AddWithValue("$dk", deviceKey);
                copyActuation.ExecuteNonQuery();
            }

            using (var copyExtendedKeys = _connection.CreateCommand())
            {
                copyExtendedKeys.Transaction = transaction;
                copyExtendedKeys.CommandText = """
                    INSERT INTO preset_extended_keys (preset_id, mode, slot1, slot2, priority, time_ms, hold_type, hold_code1, hold_code2, tap_type, tap_code1, tap_code2)
                    SELECT $id, mode, slot1, slot2, priority, time_ms, hold_type, hold_code1, hold_code2, tap_type, tap_code1, tap_code2
                    FROM device_extended_keys
                    WHERE owner_kind = $ok AND owner_id = $oi
                      AND vendor_id = $v AND product_id = $p AND device_key = $dk;
                    """;
                copyExtendedKeys.Parameters.AddWithValue("$id", presetId);
                copyExtendedKeys.Parameters.AddWithValue("$ok", ownerKind);
                copyExtendedKeys.Parameters.AddWithValue("$oi", ownerId);
                copyExtendedKeys.Parameters.AddWithValue("$v", vendorId);
                copyExtendedKeys.Parameters.AddWithValue("$p", productId);
                copyExtendedKeys.Parameters.AddWithValue("$dk", deviceKey);
                copyExtendedKeys.ExecuteNonQuery();
            }

            // O(1) extra SQL in the already-open transaction (hot path: preset apply/save).
            marked = MarkPresetDirtyCore(transaction, presetId);
            transaction.Commit();
        }
        finally { _lock.Release(); }

        if (marked)
            RaiseSyncRelevantDataChanged();
    }

    // True when every live per-device table (device_rgb/device_rgb_effect/button_bindings/
    // device_settings/key_bindings) exactly matches what presetId's own snapshot tables
    // (preset_rgb/preset_rgb_effect/preset_button_bindings/preset_settings/preset_key_bindings)
    // hold — i.e. there is nothing new to save. Drives HasUnsavedPresetChanges as a live
    // comparison instead of a one-way "something was touched" flag, so reverting an edit back to
    // the preset's saved value correctly clears the dirty state again. One symmetric
    // NOT EXISTS/EXCEPT pair (set equality) per table, ANDed together — mirrors the same five
    // table pairs SavePresetSnapshot/LoadPresetSnapshotToDevice copy between; this reads instead
    // of copies.
    // Adds settings rows a preset's snapshot does not have yet, leaving any it already has alone.
    //
    // Needed because a preset can be missing a key while still behaving as though it had one: the
    // DPI section shows a stage and a report rate resolved from the catalog even when the preset
    // stored neither, and ApplyPresetToDeviceCoreAsync's restoreUnsetToDefaults already pushes those
    // same resolved values to the hardware. The preset's EFFECTIVE configuration therefore already
    // includes them — they are simply not written down.
    //
    // That gap is what made "change a setting, change it back" leave Save Preset enabled: the round
    // trip writes a live row, the snapshot still has none, and PresetMatchesLiveState compares rows.
    // Writing the resolved value into the snapshot records what the preset was already doing, so the
    // comparison can see them as equal. It never overwrites a value the user actually saved.
    public void BackfillPresetSettings(int presetId, IReadOnlyList<(string Key, double Value)> values)
    {
        if (values is null || values.Count == 0) return;

        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var (key, value) in values)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = """
                    INSERT INTO preset_settings (preset_id, key, value)
                    SELECT $id, $k, $v
                    WHERE NOT EXISTS (SELECT 1 FROM preset_settings WHERE preset_id = $id AND key = $k);
                    """;
                cmd.Parameters.AddWithValue("$id", presetId);
                cmd.Parameters.AddWithValue("$k", key);
                cmd.Parameters.AddWithValue("$v", value);
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        finally { _lock.Release(); }
    }

    // Fills in rows a preset's snapshot is missing, across EVERY table PresetMatchesLiveState
    // compares, leaving anything already stored untouched.
    //
    // Why this is needed at all: PresetMatchesLiveState compares ROWS, so a row present on one side
    // and absent on the other reads as a difference — and once a live row exists, changing its value
    // back cannot remove it. That is what left Save Preset stuck on.
    //
    // Why it is safe: applying a preset WIPES the live tables and copies the snapshot into them, so
    // immediately afterwards live == snapshot exactly. The only rows live can have that the snapshot
    // does not are ones written by the page-open flow itself after that copy —
    // PersistRgbStateForCurrentDevice writing device_rgb / device_rgb_effect, the slider defaults,
    // the resolved DPI stage and report rate. Recording those is recording what the preset already
    // produces in practice, not inventing configuration.
    //
    // Why it matters most for the DEFAULT preset: a user-created preset is snapshotted at creation,
    // and a freshly seeded default preset is snapshotted by LoadPresetsForDevice. A default preset
    // created by an older build has neither — its snapshot can be empty while the open flow keeps
    // writing live rows, so every comparison reported a difference no revert could clear.
    public void BackfillPresetSnapshotFromLive(int presetId, string ownerKind, string ownerId,
        int vendorId, int productId, string deviceKey)
    {
        deviceKey ??= string.Empty;
        bool marked;
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();

            // Each entry: the snapshot table, its columns, the live table, and the key column(s)
            // that decide whether a row is "already present" in the snapshot.
            (string Snap, string Cols, string Live, string KeyMatch)[] tables =
            [
                ("preset_rgb", "is_enabled, selected_effect", "device_rgb", "1=1"),
                ("preset_rgb_effect", "effect_key, hex, brightness, speed, is_rainbow", "device_rgb_effect", "ps.effect_key = l.effect_key"),
                ("preset_button_bindings", "button, action, macro_id", "button_bindings", "ps.button = l.button"),
                ("preset_settings", "key, value", "device_settings", "ps.key = l.key"),
                // The three keyboard-only tables below are listed for completeness, NOT because they
                // currently run: this method's only caller, BackfillSelectedPresetSettings, returns
                // early on !Device.SupportsDpiStageSwitch, which is mouse-only — so no keyboard ever
                // reaches the backfill, and keyboards are the only devices that have these rows.
                //
                // They are not made reachable here on purpose. The backfill's contract is "record
                // what the preset was already effectively doing", which is true of a DPI stage
                // resolved from the catalog but NOT of a key remap or an actuation value the user
                // edited and deliberately has not saved — widening the gate would silently promote
                // unsaved work into the snapshot. Closing that properly is its own change.
                ("preset_key_bindings", "phys_scancode, type_byte, bitmask, target_code", "key_bindings", "ps.phys_scancode = l.phys_scancode"),
                ("preset_key_macro_bindings", "matrix_index, macro_id", "key_macro_bindings", "ps.matrix_index = l.matrix_index"),
                ("preset_actuation", "slot, press_centi_mm, release_centi_mm, rapid_trigger, quick_press_centi_mm, quick_release_centi_mm", "device_actuation", "ps.slot = l.slot"),
                ("preset_extended_keys", "mode, slot1, slot2, priority, time_ms, hold_type, hold_code1, hold_code2, tap_type, tap_code1, tap_code2", "device_extended_keys", "ps.slot1 = l.slot1"),
            ];

            foreach (var (snap, cols, live, keyMatch) in tables)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = $"""
                    INSERT INTO {snap} (preset_id, {cols})
                    SELECT $id, {cols}
                    FROM {live} l
                    WHERE l.owner_kind = $ok AND l.owner_id = $oi
                      AND l.vendor_id = $v AND l.product_id = $p AND l.device_key = $dk
                      AND NOT EXISTS (
                          SELECT 1 FROM {snap} ps WHERE ps.preset_id = $id AND {keyMatch});
                    """;
                cmd.Parameters.AddWithValue("$id", presetId);
                cmd.Parameters.AddWithValue("$ok", ownerKind);
                cmd.Parameters.AddWithValue("$oi", ownerId);
                cmd.Parameters.AddWithValue("$v", vendorId);
                cmd.Parameters.AddWithValue("$p", productId);
                cmd.Parameters.AddWithValue("$dk", deviceKey);
                cmd.ExecuteNonQuery();
            }

            marked = MarkPresetDirtyCore(transaction, presetId);
            transaction.Commit();
        }
        finally { _lock.Release(); }

        if (marked)
            RaiseSyncRelevantDataChanged();
    }

    public bool PresetMatchesLiveState(int presetId, string ownerKind, string ownerId,
        int vendorId, int productId, string deviceKey)
    {
        deviceKey ??= string.Empty;
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT
                    NOT EXISTS (
                        SELECT is_enabled, selected_effect FROM device_rgb
                        WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk
                        EXCEPT
                        SELECT is_enabled, selected_effect FROM preset_rgb WHERE preset_id=$id
                    )
                    AND NOT EXISTS (
                        SELECT is_enabled, selected_effect FROM preset_rgb WHERE preset_id=$id
                        EXCEPT
                        SELECT is_enabled, selected_effect FROM device_rgb
                        WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk
                    )
                    AND NOT EXISTS (
                        SELECT effect_key, hex, brightness, speed, is_rainbow FROM device_rgb_effect
                        WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk
                        EXCEPT
                        SELECT effect_key, hex, brightness, speed, is_rainbow FROM preset_rgb_effect WHERE preset_id=$id
                    )
                    AND NOT EXISTS (
                        SELECT effect_key, hex, brightness, speed, is_rainbow FROM preset_rgb_effect WHERE preset_id=$id
                        EXCEPT
                        SELECT effect_key, hex, brightness, speed, is_rainbow FROM device_rgb_effect
                        WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk
                    )
                    AND NOT EXISTS (
                        SELECT button, action, macro_id FROM button_bindings
                        WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk
                        EXCEPT
                        SELECT button, action, macro_id FROM preset_button_bindings WHERE preset_id=$id
                    )
                    AND NOT EXISTS (
                        SELECT button, action, macro_id FROM preset_button_bindings WHERE preset_id=$id
                        EXCEPT
                        SELECT button, action, macro_id FROM button_bindings
                        WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk
                    )
                    AND NOT EXISTS (
                        SELECT key, value FROM device_settings
                        WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk
                        EXCEPT
                        SELECT key, value FROM preset_settings WHERE preset_id=$id
                    )
                    AND NOT EXISTS (
                        SELECT key, value FROM preset_settings WHERE preset_id=$id
                        EXCEPT
                        SELECT key, value FROM device_settings
                        WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk
                    )
                    AND NOT EXISTS (
                        SELECT phys_scancode, type_byte, bitmask, target_code FROM key_bindings
                        WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk
                        EXCEPT
                        SELECT phys_scancode, type_byte, bitmask, target_code FROM preset_key_bindings WHERE preset_id=$id
                    )
                    AND NOT EXISTS (
                        SELECT phys_scancode, type_byte, bitmask, target_code FROM preset_key_bindings WHERE preset_id=$id
                        EXCEPT
                        SELECT phys_scancode, type_byte, bitmask, target_code FROM key_bindings
                        WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk
                    )
                    AND NOT EXISTS (
                        SELECT matrix_index, macro_id FROM key_macro_bindings
                        WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk
                        EXCEPT
                        SELECT matrix_index, macro_id FROM preset_key_macro_bindings WHERE preset_id=$id
                    )
                    AND NOT EXISTS (
                        SELECT matrix_index, macro_id FROM preset_key_macro_bindings WHERE preset_id=$id
                        EXCEPT
                        SELECT matrix_index, macro_id FROM key_macro_bindings
                        WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk
                    )
                    AND NOT EXISTS (
                        SELECT slot, press_centi_mm, release_centi_mm, rapid_trigger, quick_press_centi_mm, quick_release_centi_mm FROM device_actuation
                        WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk
                        EXCEPT
                        SELECT slot, press_centi_mm, release_centi_mm, rapid_trigger, quick_press_centi_mm, quick_release_centi_mm FROM preset_actuation WHERE preset_id=$id
                    )
                    AND NOT EXISTS (
                        SELECT slot, press_centi_mm, release_centi_mm, rapid_trigger, quick_press_centi_mm, quick_release_centi_mm FROM preset_actuation WHERE preset_id=$id
                        EXCEPT
                        SELECT slot, press_centi_mm, release_centi_mm, rapid_trigger, quick_press_centi_mm, quick_release_centi_mm FROM device_actuation
                        WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk
                    )
                    AND NOT EXISTS (
                        SELECT mode, slot1, slot2, priority, time_ms, hold_type, hold_code1, hold_code2, tap_type, tap_code1, tap_code2 FROM device_extended_keys
                        WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk
                        EXCEPT
                        SELECT mode, slot1, slot2, priority, time_ms, hold_type, hold_code1, hold_code2, tap_type, tap_code1, tap_code2 FROM preset_extended_keys WHERE preset_id=$id
                    )
                    AND NOT EXISTS (
                        SELECT mode, slot1, slot2, priority, time_ms, hold_type, hold_code1, hold_code2, tap_type, tap_code1, tap_code2 FROM preset_extended_keys WHERE preset_id=$id
                        EXCEPT
                        SELECT mode, slot1, slot2, priority, time_ms, hold_type, hold_code1, hold_code2, tap_type, tap_code1, tap_code2 FROM device_extended_keys
                        WHERE owner_kind=$ok AND owner_id=$oi AND vendor_id=$v AND product_id=$p AND device_key=$dk
                    );
                """;
            cmd.Parameters.AddWithValue("$id", presetId);
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$v", vendorId);
            cmd.Parameters.AddWithValue("$p", productId);
            cmd.Parameters.AddWithValue("$dk", deviceKey);
            return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
        }
        finally { _lock.Release(); }
    }

    // Copies one preset's saved snapshot directly into another preset — used by Duplicate, which
    // must reproduce the SOURCE preset's own configuration, never the live rows (those reflect
    // whichever preset is currently active, which may be a different one entirely). Unlike
    // SavePresetSnapshot, this never reads or writes the live device_* tables, so the device the
    // user has open is completely unaffected — a duplicate is just a new, inactive row until the
    // user explicitly selects it.
    public void CopyPresetSnapshot(int sourcePresetId, int newPresetId)
    {
        bool marked;
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();

            using (var wipe = _connection.CreateCommand())
            {
                wipe.Transaction = transaction;
                wipe.CommandText = """
                    DELETE FROM preset_button_bindings WHERE preset_id = $newId;
                    DELETE FROM preset_rgb_effect     WHERE preset_id = $newId;
                    DELETE FROM preset_rgb            WHERE preset_id = $newId;
                    DELETE FROM preset_settings       WHERE preset_id = $newId;
                    DELETE FROM preset_key_bindings   WHERE preset_id = $newId;
                    DELETE FROM preset_key_macro_bindings WHERE preset_id = $newId;
                    DELETE FROM preset_actuation      WHERE preset_id = $newId;
                    DELETE FROM preset_extended_keys  WHERE preset_id = $newId;
                    """;
                wipe.Parameters.AddWithValue("$newId", newPresetId);
                wipe.ExecuteNonQuery();
            }

            using (var copyHead = _connection.CreateCommand())
            {
                copyHead.Transaction = transaction;
                copyHead.CommandText = """
                    INSERT INTO preset_rgb (preset_id, is_enabled, selected_effect)
                    SELECT $newId, is_enabled, selected_effect
                    FROM preset_rgb WHERE preset_id = $srcId;
                    """;
                copyHead.Parameters.AddWithValue("$newId", newPresetId);
                copyHead.Parameters.AddWithValue("$srcId", sourcePresetId);
                copyHead.ExecuteNonQuery();
            }

            using (var copyEffects = _connection.CreateCommand())
            {
                copyEffects.Transaction = transaction;
                copyEffects.CommandText = """
                    INSERT INTO preset_rgb_effect (preset_id, effect_key, hex, brightness, speed, is_rainbow)
                    SELECT $newId, effect_key, hex, brightness, speed, is_rainbow
                    FROM preset_rgb_effect WHERE preset_id = $srcId;
                    """;
                copyEffects.Parameters.AddWithValue("$newId", newPresetId);
                copyEffects.Parameters.AddWithValue("$srcId", sourcePresetId);
                copyEffects.ExecuteNonQuery();
            }

            using (var copyBindings = _connection.CreateCommand())
            {
                copyBindings.Transaction = transaction;
                copyBindings.CommandText = """
                    INSERT INTO preset_button_bindings (preset_id, button, action, macro_id)
                    SELECT $newId, button, action, macro_id
                    FROM preset_button_bindings WHERE preset_id = $srcId;
                    """;
                copyBindings.Parameters.AddWithValue("$newId", newPresetId);
                copyBindings.Parameters.AddWithValue("$srcId", sourcePresetId);
                copyBindings.ExecuteNonQuery();
            }

            using (var copySettings = _connection.CreateCommand())
            {
                copySettings.Transaction = transaction;
                copySettings.CommandText = """
                    INSERT INTO preset_settings (preset_id, key, value)
                    SELECT $newId, key, value
                    FROM preset_settings WHERE preset_id = $srcId;
                    """;
                copySettings.Parameters.AddWithValue("$newId", newPresetId);
                copySettings.Parameters.AddWithValue("$srcId", sourcePresetId);
                copySettings.ExecuteNonQuery();
            }

            using (var copyKeys = _connection.CreateCommand())
            {
                copyKeys.Transaction = transaction;
                copyKeys.CommandText = """
                    INSERT INTO preset_key_bindings (preset_id, phys_scancode, type_byte, bitmask, target_code)
                    SELECT $newId, phys_scancode, type_byte, bitmask, target_code
                    FROM preset_key_bindings WHERE preset_id = $srcId;
                    INSERT INTO preset_key_macro_bindings (preset_id, matrix_index, macro_id)
                    SELECT $newId, matrix_index, macro_id
                    FROM preset_key_macro_bindings WHERE preset_id = $srcId;
                    INSERT INTO preset_actuation (preset_id, slot, press_centi_mm, release_centi_mm, rapid_trigger, quick_press_centi_mm, quick_release_centi_mm)
                    SELECT $newId, slot, press_centi_mm, release_centi_mm, rapid_trigger, quick_press_centi_mm, quick_release_centi_mm
                    FROM preset_actuation WHERE preset_id = $srcId;
                    INSERT INTO preset_extended_keys (preset_id, mode, slot1, slot2, priority, time_ms, hold_type, hold_code1, hold_code2, tap_type, tap_code1, tap_code2)
                    SELECT $newId, mode, slot1, slot2, priority, time_ms, hold_type, hold_code1, hold_code2, tap_type, tap_code1, tap_code2
                    FROM preset_extended_keys WHERE preset_id = $srcId;
                    """;
                copyKeys.Parameters.AddWithValue("$newId", newPresetId);
                copyKeys.Parameters.AddWithValue("$srcId", sourcePresetId);
                copyKeys.ExecuteNonQuery();
            }

            // The new preset's content just changed wholesale; the source is untouched.
            marked = MarkPresetDirtyCore(transaction, newPresetId);
            transaction.Commit();
        }
        finally { _lock.Release(); }

        if (marked)
            RaiseSyncRelevantDataChanged();
    }

    // Sync write-back (API v2.0): stores the server's updated_at exactly as received. Only
    // sync code calls this — nothing else may touch server_updated_at.
    public void SetPresetServerUpdatedAt(int presetId, string value)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE presets SET server_updated_at = $ts WHERE id = $id";
            cmd.Parameters.AddWithValue("$ts", value);
            cmd.Parameters.AddWithValue("$id", presetId);
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    public void UpdatePreset(int presetId, string name, string description)
    {
        bool marked;
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();

            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "UPDATE presets SET name = $n, description = $d WHERE id = $id";
                cmd.Parameters.AddWithValue("$n", name);
                cmd.Parameters.AddWithValue("$d", description ?? string.Empty);
                cmd.Parameters.AddWithValue("$id", presetId);
                cmd.ExecuteNonQuery();
            }

            marked = MarkPresetDirtyCore(transaction, presetId);
            transaction.Commit();
        }
        finally { _lock.Release(); }

        if (marked)
            RaiseSyncRelevantDataChanged();
    }

    // Runs inside the caller's transaction: bumps the sync dirty state of the preset row
    // when (and only when) it is user-owned — guest rows never sync, and the owner_kind
    // guard in the SQL is what lets id-only mutators skip a separate scope lookup. Returns
    // whether a row was marked so the caller can raise SyncRelevantDataChanged outside _lock.
    private bool MarkPresetDirtyCore(SqliteTransaction transaction, int presetId)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "UPDATE presets SET is_dirty = 1, local_rev = local_rev + 1 WHERE id = $id AND owner_kind = $uk";
        cmd.Parameters.AddWithValue("$id", presetId);
        cmd.Parameters.AddWithValue("$uk", OwnerKindUser);
        return cmd.ExecuteNonQuery() > 0;
    }

    // ── Devices ───────────────────────────────────────────────────────────────

    // The guest-session restore path: session-scoped rows only. A signed-in user's cards are
    // read through the owner-scoped overload below.
    public List<DeviceRecord> GetDevices()
    {
        AcquireLock();
        try
        {
            var devices = new List<DeviceRecord>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT id, name, device_type_label, image_path, battery_level, connection_icon, card_type, vendor_id, product_id, device_key, merge_transports, settings_vendor_id, settings_product_id, settings_device_key, image_scale, low_battery_announced_band FROM devices WHERE owner_kind = 'guest'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var rec = new DeviceRecord
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    DeviceTypeLabel = reader.GetString(2),
                    ImagePath = reader.IsDBNull(3) ? null : reader.GetString(3),
                    BatteryLevel = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    ConnectionIcon = reader.IsDBNull(5) ? null : reader.GetString(5),
                    CardType = reader.GetString(6),
                    VendorId = reader.GetInt32(7),
                    ProductId = reader.GetInt32(8),
                    DeviceKey = reader.GetString(9),
                    MergeTransports = reader.GetInt32(10) != 0,
                    SettingsVendorId = reader.GetInt32(11),
                    SettingsProductId = reader.GetInt32(12),
                    SettingsDeviceKey = reader.GetString(13),
                    ImageScale = reader.IsDBNull(14) ? 0 : reader.GetDouble(14),
                    LowBatteryAnnouncedBand = reader.GetInt32(15)
                };
                devices.Add(rec);
            }
            return devices;
        }
        finally { _lock.Release(); }
    }

    public void Flush(UserSession session) => Flush(session.SnapshotDevices());

    // Snapshot overload for queued flushes: HomeViewModel takes the snapshot at enqueue time
    // (through UserSession's gate, since a persist can come from the discovery thread), so the
    // executor never enumerates a list another thread may still be mutating -- and the queued
    // write records exactly the state that existed when it was ordered, which is what FIFO
    // promises.
    public void Flush(IReadOnlyList<DeviceRecord> devices)
    {
        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();

            using var deleteCmd = _connection.CreateCommand();
            deleteCmd.Transaction = transaction;
            deleteCmd.CommandText = "DELETE FROM devices WHERE owner_kind = 'guest';";
            deleteCmd.ExecuteNonQuery();

            foreach (var device in devices)
            {
                using var insertCmd = _connection.CreateCommand();
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = """
                    INSERT INTO devices (name, device_type_label, image_path, battery_level, connection_icon, card_type, vendor_id, product_id, device_key, merge_transports, settings_vendor_id, settings_product_id, settings_device_key, image_scale, low_battery_announced_band)
                    VALUES ($name, $label, $image, $battery, $icon, $type, $vid, $pid, $dk, $merge, $svid, $spid, $sdk, $scale, $band);
                    """;
                insertCmd.Parameters.AddWithValue("$name", device.Name);
                insertCmd.Parameters.AddWithValue("$label", device.DeviceTypeLabel);
                insertCmd.Parameters.AddWithValue("$image", (object?)device.ImagePath ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("$battery", (object?)device.BatteryLevel ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("$icon", (object?)device.ConnectionIcon ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("$type", device.CardType);
                insertCmd.Parameters.AddWithValue("$vid", device.VendorId);
                insertCmd.Parameters.AddWithValue("$pid", device.ProductId);
                insertCmd.Parameters.AddWithValue("$dk", device.DeviceKey);
                insertCmd.Parameters.AddWithValue("$merge", device.MergeTransports ? 1 : 0);
                insertCmd.Parameters.AddWithValue("$svid", device.SettingsVendorId);
                insertCmd.Parameters.AddWithValue("$spid", device.SettingsProductId);
                insertCmd.Parameters.AddWithValue("$sdk", device.SettingsDeviceKey ?? string.Empty);
                insertCmd.Parameters.AddWithValue("$scale", device.ImageScale);
                insertCmd.Parameters.AddWithValue("$band", device.LowBatteryAnnouncedBand);
                insertCmd.ExecuteNonQuery();
            }

            transaction.Commit();
        }
        finally { _lock.Release(); }
    }

    // ── Owner-scoped devices (signed-in users; write-through, no exit flush) ──

    public List<DeviceRecord> GetDevices(string ownerKind, string ownerId)
    {
        AcquireLock();
        try
        {
            var devices = new List<DeviceRecord>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, name, device_type_label, image_path, battery_level, connection_icon, card_type, vendor_id, product_id, device_key, merge_transports, settings_vendor_id, settings_product_id, settings_device_key, image_scale, low_battery_announced_band
                FROM devices WHERE owner_kind = $ok AND owner_id = $oi ORDER BY id
                """;
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                devices.Add(new DeviceRecord
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    DeviceTypeLabel = reader.GetString(2),
                    ImagePath = reader.IsDBNull(3) ? null : reader.GetString(3),
                    BatteryLevel = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    ConnectionIcon = reader.IsDBNull(5) ? null : reader.GetString(5),
                    CardType = reader.GetString(6),
                    VendorId = reader.GetInt32(7),
                    ProductId = reader.GetInt32(8),
                    DeviceKey = reader.GetString(9),
                    MergeTransports = reader.GetInt32(10) != 0,
                    SettingsVendorId = reader.GetInt32(11),
                    SettingsProductId = reader.GetInt32(12),
                    SettingsDeviceKey = reader.GetString(13),
                    ImageScale = reader.IsDBNull(14) ? 0 : reader.GetDouble(14),
                    LowBatteryAnnouncedBand = reader.GetInt32(15)
                });
            }
            return devices;
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Upsert keyed by the card's IDENTITY — the same rule the device list matches on, so storage
    /// and UI can never disagree about what "the same device" is. A merged-transport card is one
    /// row per (owner, name), because its vendor_id / product_id / device_key follow whichever
    /// transport is live (RAMPAGE: 1389/49271 + the receiver's Container ID over the dongle,
    /// 10473/12288 with no key over Bluetooth) and keying on those accumulates one row per
    /// transport. Every other card keys on (owner, vendor_id, product_id, device_key, name);
    /// device_key is in that key because VID/PID/name alone collapse two identical units of one
    /// model into one row (PR #112 M2) — discovery keeps such units apart precisely by their
    /// Container ID. Names compare case-insensitively, matching discovery. The card's previous row
    /// is dropped by id as well, so a re-persist after the routing identity moved (a port move
    /// rewrites device_key) replaces that row instead of orphaning it. Returns the new row id.
    /// </summary>
    public int AddDevice(string ownerKind, string ownerId, DeviceRecord device, bool markDirty = true)
    {
        var name = device.Name ?? string.Empty;
        var deviceKey = device.DeviceKey ?? string.Empty;
        var marked = false;
        int newId;

        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();

            // Scoped to the owner because a card's StorageId is only a devices-table id while a
            // user owns it: under a guest session the ids come from UserSession's own counter.
            if (device.Id > 0)
            {
                using var byIdCmd = _connection.CreateCommand();
                byIdCmd.Transaction = transaction;
                byIdCmd.CommandText = "DELETE FROM devices WHERE id = $id AND owner_kind = $ok AND owner_id = $oi";
                byIdCmd.Parameters.AddWithValue("$id", device.Id);
                byIdCmd.Parameters.AddWithValue("$ok", ownerKind);
                byIdCmd.Parameters.AddWithValue("$oi", ownerId);
                byIdCmd.ExecuteNonQuery();
            }

            using (var deleteCmd = _connection.CreateCommand())
            {
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = device.MergeTransports
                    ? """
                      DELETE FROM devices
                      WHERE owner_kind = $ok AND owner_id = $oi AND name = $name COLLATE NOCASE
                      """
                    : """
                      DELETE FROM devices
                      WHERE owner_kind = $ok AND owner_id = $oi AND vendor_id = $vid AND product_id = $pid AND device_key = $dk AND name = $name COLLATE NOCASE
                      """;
                deleteCmd.Parameters.AddWithValue("$ok", ownerKind);
                deleteCmd.Parameters.AddWithValue("$oi", ownerId);
                deleteCmd.Parameters.AddWithValue("$name", name);
                if (!device.MergeTransports)
                {
                    deleteCmd.Parameters.AddWithValue("$vid", device.VendorId);
                    deleteCmd.Parameters.AddWithValue("$pid", device.ProductId);
                    deleteCmd.Parameters.AddWithValue("$dk", deviceKey);
                }
                deleteCmd.ExecuteNonQuery();
            }

            int id;
            using (var insertCmd = _connection.CreateCommand())
            {
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = """
                    INSERT INTO devices (owner_kind, owner_id, name, device_type_label, image_path, battery_level, connection_icon, card_type, vendor_id, product_id, device_key, merge_transports, settings_vendor_id, settings_product_id, settings_device_key, image_scale, low_battery_announced_band)
                    VALUES ($ok, $oi, $name, $label, $image, $battery, $icon, $type, $vid, $pid, $dk, $merge, $svid, $spid, $sdk, $scale, $band);
                    SELECT last_insert_rowid();
                    """;
                insertCmd.Parameters.AddWithValue("$ok", ownerKind);
                insertCmd.Parameters.AddWithValue("$oi", ownerId);
                insertCmd.Parameters.AddWithValue("$name", name);
                insertCmd.Parameters.AddWithValue("$label", device.DeviceTypeLabel);
                insertCmd.Parameters.AddWithValue("$image", (object?)device.ImagePath ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("$battery", (object?)device.BatteryLevel ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("$icon", (object?)device.ConnectionIcon ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("$type", device.CardType);
                insertCmd.Parameters.AddWithValue("$vid", device.VendorId);
                insertCmd.Parameters.AddWithValue("$pid", device.ProductId);
                insertCmd.Parameters.AddWithValue("$dk", deviceKey);
                insertCmd.Parameters.AddWithValue("$merge", device.MergeTransports ? 1 : 0);
                insertCmd.Parameters.AddWithValue("$svid", device.SettingsVendorId);
                insertCmd.Parameters.AddWithValue("$spid", device.SettingsProductId);
                insertCmd.Parameters.AddWithValue("$sdk", device.SettingsDeviceKey ?? string.Empty);
                insertCmd.Parameters.AddWithValue("$scale", device.ImageScale);
                insertCmd.Parameters.AddWithValue("$band", device.LowBatteryAnnouncedBand);
                id = Convert.ToInt32(insertCmd.ExecuteScalar());
            }

            // Roster intent (docs/plans/add-api-v21-device-roster): a user card registers its
            // MODEL server-side. Same transaction as the upsert, last intent wins (a pending
            // remove for the identity is cancelled). markDirty: false is the sign-in pull's
            // hydration — the row IS the server's roster, re-adding it would be an echo.
            if (markDirty && ownerKind == OwnerKindUser)
            {
                var (bucketVid, bucketPid) = SettingsSyncService.SettingsBucketOf(device);
                if (bucketVid > 0 && bucketPid > 0)
                {
                    SetSyncMetaCore(ownerKind, ownerId,
                        SyncMetaDeviceRosterAdd(bucketVid, bucketPid), NextSyncDirtyGeneration(), transaction);
                    RemoveSyncMetaCore(ownerKind, ownerId,
                        SyncMetaDeviceRosterRemove(bucketVid, bucketPid), transaction);
                    marked = true;
                }
            }

            transaction.Commit();
            newId = id;
        }
        finally { _lock.Release(); }

        if (marked)
            RaiseSyncRelevantDataChanged();
        return newId;
    }

    /// <summary>
    /// Deletes one card by row id, scoped to its owner — the same invariant
    /// <see cref="AddDevice"/>'s delete-by-id carries: a card's StorageId is only a devices-table
    /// id while a USER owns it, because under a guest session the ids come from UserSession's own
    /// counter starting at 1. An unscoped delete-by-id would therefore let a guest id address a
    /// stranger's row. A mismatched owner deletes nothing.
    /// </summary>
    public void DeleteDevice(string ownerKind, string ownerId, int id)
    {
        var marked = false;

        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();

            // The row's settings identity, read before the delete: it keys the roster mark
            // below, and the row is gone by the time the last-card check runs.
            var bucketVid = 0;
            var bucketPid = 0;
            var found = false;
            using (var readCmd = _connection.CreateCommand())
            {
                readCmd.Transaction = transaction;
                readCmd.CommandText = """
                    SELECT vendor_id, product_id, settings_vendor_id, settings_product_id
                    FROM devices WHERE id = $id AND owner_kind = $ok AND owner_id = $oi
                    """;
                readCmd.Parameters.AddWithValue("$id", id);
                readCmd.Parameters.AddWithValue("$ok", ownerKind);
                readCmd.Parameters.AddWithValue("$oi", ownerId);
                using var reader = readCmd.ExecuteReader();
                if (reader.Read())
                {
                    found = true;
                    var settingsVid = reader.GetInt32(2);
                    var settingsPid = reader.GetInt32(3);
                    bucketVid = settingsVid > 0 ? settingsVid : reader.GetInt32(0);
                    bucketPid = settingsPid > 0 ? settingsPid : reader.GetInt32(1);
                }
            }

            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM devices WHERE id = $id AND owner_kind = $ok AND owner_id = $oi";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$ok", ownerKind);
                cmd.Parameters.AddWithValue("$oi", ownerId);
                cmd.ExecuteNonQuery();
            }

            // Roster intent (docs/plans/add-api-v21-device-roster): the server rosters MODELS,
            // so removal is owed only when the deleted card was the owner's last of its
            // settings identity — a second identical unit keeps the model registered. Last
            // intent wins: a pending add for the identity is cancelled.
            if (found && ownerKind == OwnerKindUser && bucketVid > 0 && bucketPid > 0)
            {
                long remaining;
                using (var countCmd = _connection.CreateCommand())
                {
                    countCmd.Transaction = transaction;
                    countCmd.CommandText = """
                        SELECT COUNT(*) FROM devices
                        WHERE owner_kind = $ok AND owner_id = $oi
                          AND (CASE WHEN settings_vendor_id > 0 THEN settings_vendor_id ELSE vendor_id END) = $bvid
                          AND (CASE WHEN settings_product_id > 0 THEN settings_product_id ELSE product_id END) = $bpid
                        """;
                    countCmd.Parameters.AddWithValue("$ok", ownerKind);
                    countCmd.Parameters.AddWithValue("$oi", ownerId);
                    countCmd.Parameters.AddWithValue("$bvid", bucketVid);
                    countCmd.Parameters.AddWithValue("$bpid", bucketPid);
                    remaining = (long)countCmd.ExecuteScalar()!;
                }
                if (remaining == 0)
                {
                    SetSyncMetaCore(ownerKind, ownerId,
                        SyncMetaDeviceRosterRemove(bucketVid, bucketPid), NextSyncDirtyGeneration(), transaction);
                    RemoveSyncMetaCore(ownerKind, ownerId,
                        SyncMetaDeviceRosterAdd(bucketVid, bucketPid), transaction);
                    marked = true;
                }
            }

            transaction.Commit();
        }
        finally { _lock.Release(); }

        if (marked)
            RaiseSyncRelevantDataChanged();
    }

    /// <summary>
    /// Targeted write for the low-battery announcement guard (0 = re-armed). Only the signed-in
    /// write-through path calls it — a guest card's band rides the whole-list Flush instead —
    /// and it is owner-scoped for the same reason <see cref="DeleteDevice"/> is: a guest
    /// StorageId is a UserSession counter, not a devices-table id, and must not address a
    /// stranger's row.
    /// </summary>
    public void SetDeviceLowBatteryAnnouncedBand(string ownerKind, string ownerId, int id, int band)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE devices SET low_battery_announced_band = $band WHERE id = $id AND owner_kind = $ok AND owner_id = $oi";
            cmd.Parameters.AddWithValue("$band", band);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    // ── Notifications (the bell's history, owner-scoped) ─────────────────────

    // The store is history, not a queue — a cap keeps it from growing forever. internal:
    // the notifications pull caps its merge to the same number
    // (docs/plans/add-api-v21-notifications-sync).
    internal const int MaxNotificationsPerOwner = 100;

    public List<NotificationRecord> GetNotifications(string ownerKind, string ownerId)
    {
        AcquireLock();
        try
        {
            var items = new List<NotificationRecord>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, title, subtitle, icon_data, icon_uri, icon_width, icon_height, created_at, is_read, server_id, type, is_dirty, local_rev
                FROM notifications WHERE owner_kind = $ok AND owner_id = $oi
                ORDER BY id DESC LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$limit", MaxNotificationsPerOwner);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new NotificationRecord
                {
                    Id = reader.GetInt32(0),
                    Title = reader.GetString(1),
                    Subtitle = reader.GetString(2),
                    IconData = reader.GetString(3),
                    IconUri = reader.GetString(4),
                    IconWidth = reader.GetDouble(5),
                    IconHeight = reader.GetDouble(6),
                    CreatedAt = DateTime.Parse(reader.GetString(7), null, DateTimeStyles.RoundtripKind),
                    IsRead = reader.GetInt32(8) != 0,
                    ServerId = reader.IsDBNull(9) ? null : reader.GetInt64(9),
                    Type = reader.GetString(10),
                    IsDirty = reader.GetInt32(11) != 0,
                    LocalRev = reader.GetInt32(12)
                });
            }
            return items;
        }
        finally { _lock.Release(); }
    }

    // markDirty: false is the pull's shape (docs/plans/add-api-v21-notifications-sync) —
    // the row IS the server's list (ServerId set), so re-pushing it would be an echo.
    // User-owned live entries land dirty and raise the sync event; guest rows never do.
    public int AddNotification(string ownerKind, string ownerId, NotificationRecord notification, bool markDirty = true)
    {
        var marked = markDirty && ownerKind == OwnerKindUser;
        int newId;

        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();

            int id;
            using (var insertCmd = _connection.CreateCommand())
            {
                insertCmd.Transaction = transaction;
                insertCmd.CommandText = """
                    INSERT INTO notifications (owner_kind, owner_id, title, subtitle, icon_data, icon_uri, icon_width, icon_height, created_at, is_read, server_id, type, is_dirty, local_rev)
                    VALUES ($ok, $oi, $title, $subtitle, $data, $uri, $w, $h, $at, $read, $sid, $type, $dirty, $rev);
                    SELECT last_insert_rowid();
                    """;
                insertCmd.Parameters.AddWithValue("$ok", ownerKind);
                insertCmd.Parameters.AddWithValue("$oi", ownerId);
                insertCmd.Parameters.AddWithValue("$title", notification.Title);
                insertCmd.Parameters.AddWithValue("$subtitle", notification.Subtitle);
                insertCmd.Parameters.AddWithValue("$data", notification.IconData);
                insertCmd.Parameters.AddWithValue("$uri", notification.IconUri);
                insertCmd.Parameters.AddWithValue("$w", notification.IconWidth);
                insertCmd.Parameters.AddWithValue("$h", notification.IconHeight);
                insertCmd.Parameters.AddWithValue("$at", notification.CreatedAt.ToString("O"));
                insertCmd.Parameters.AddWithValue("$read", notification.IsRead ? 1 : 0);
                insertCmd.Parameters.AddWithValue("$sid", (object?)notification.ServerId ?? DBNull.Value);
                insertCmd.Parameters.AddWithValue("$type", notification.Type);
                insertCmd.Parameters.AddWithValue("$dirty", marked ? 1 : 0);
                insertCmd.Parameters.AddWithValue("$rev", marked ? 1 : 0);
                id = Convert.ToInt32(insertCmd.ExecuteScalar());
            }

            using (var trimCmd = _connection.CreateCommand())
            {
                trimCmd.Transaction = transaction;
                // is_dirty = 0: a dirty row is a push the coordinator still owes the server
                // — trimming it would silently lose the entry, since nothing re-enumerates
                // it (PR #127 review, M1). The table can briefly exceed the cap by the
                // dirty count; the display read (GetNotifications) stays capped, and the
                // row becomes trimmable the moment its push clears the flag.
                trimCmd.CommandText = """
                    DELETE FROM notifications WHERE owner_kind = $ok AND owner_id = $oi AND is_dirty = 0 AND id NOT IN (
                        SELECT id FROM notifications WHERE owner_kind = $ok AND owner_id = $oi
                        ORDER BY id DESC LIMIT $limit
                    )
                    """;
                trimCmd.Parameters.AddWithValue("$ok", ownerKind);
                trimCmd.Parameters.AddWithValue("$oi", ownerId);
                trimCmd.Parameters.AddWithValue("$limit", MaxNotificationsPerOwner);
                trimCmd.ExecuteNonQuery();
            }

            transaction.Commit();
            newId = id;
        }
        finally { _lock.Release(); }

        if (marked)
            RaiseSyncRelevantDataChanged();
        return newId;
    }

    // markDirty: false is the pull's adoption of the server's read state — applied without
    // marking, so it can never echo back as a push. The live toggle marks USER rows dirty
    // (rev-bumped, the presets/macros convention) and raises the sync event.
    public void SetNotificationRead(int id, bool isRead, bool markDirty = true)
    {
        var marked = false;

        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();

            if (markDirty)
                marked = IsUserNotificationCore(transaction, id);

            using var cmd = _connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = marked
                ? "UPDATE notifications SET is_read = $read, is_dirty = 1, local_rev = local_rev + 1 WHERE id = $id"
                : "UPDATE notifications SET is_read = $read WHERE id = $id";
            cmd.Parameters.AddWithValue("$read", isRead ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();

            transaction.Commit();
        }
        finally { _lock.Release(); }

        if (marked)
            RaiseSyncRelevantDataChanged();
    }

    // Batched sibling of SetNotificationRead for "mark all as read": one UPDATE + one
    // commit instead of up to MaxNotificationsPerOwner individual UPDATE+commit cycles.
    // A user owner also records the notifications.read_all batch intent — one server call
    // covers the whole list, so the rows themselves are deliberately NOT marked dirty.
    public void SetAllNotificationsRead(string ownerKind, string ownerId)
    {
        var marked = ownerKind == OwnerKindUser;

        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();

            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "UPDATE notifications SET is_read = 1 WHERE owner_kind = $ok AND owner_id = $oi";
                cmd.Parameters.AddWithValue("$ok", ownerKind);
                cmd.Parameters.AddWithValue("$oi", ownerId);
                cmd.ExecuteNonQuery();
            }

            if (marked)
                SetSyncMetaCore(ownerKind, ownerId, SyncMetaNotificationsReadAll, NextSyncDirtyGeneration(), transaction);

            transaction.Commit();
        }
        finally { _lock.Release(); }

        if (marked)
            RaiseSyncRelevantDataChanged();
    }

    // tombstone: false is the coordinator's cleanup of a row the server already lost
    // (NOTIFICATION_NOT_FOUND) — nothing remote is owed a delete. The user-driven delete of
    // a synced row (server_id non-NULL) writes a notification tombstone in the same
    // transaction; a never-synced row deletes without one, like every other kind.
    public void DeleteNotification(int id, bool tombstone = true)
    {
        var marked = false;

        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();

            string? ownerKind = null;
            string? ownerId = null;
            long? serverId = null;
            using (var readCmd = _connection.CreateCommand())
            {
                readCmd.Transaction = transaction;
                readCmd.CommandText = "SELECT owner_kind, owner_id, server_id FROM notifications WHERE id = $id";
                readCmd.Parameters.AddWithValue("$id", id);
                using var reader = readCmd.ExecuteReader();
                if (reader.Read())
                {
                    ownerKind = reader.GetString(0);
                    ownerId = reader.GetString(1);
                    serverId = reader.IsDBNull(2) ? null : reader.GetInt64(2);
                }
            }

            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM notifications WHERE id = $id";
                cmd.Parameters.AddWithValue("$id", id);
                cmd.ExecuteNonQuery();
            }

            if (tombstone && ownerKind == OwnerKindUser && ownerId is not null && serverId is { } sid)
            {
                using var stoneCmd = _connection.CreateCommand();
                stoneCmd.Transaction = transaction;
                stoneCmd.CommandText = """
                    INSERT OR REPLACE INTO sync_tombstones (owner_kind, owner_id, kind, uid, vendor_id, product_id, server_updated_at)
                    VALUES ($ok, $oi, $kind, $uid, 0, 0, NULL)
                    """;
                stoneCmd.Parameters.AddWithValue("$ok", ownerKind);
                stoneCmd.Parameters.AddWithValue("$oi", ownerId);
                stoneCmd.Parameters.AddWithValue("$kind", TombstoneKindNotification);
                stoneCmd.Parameters.AddWithValue("$uid", sid.ToString(CultureInfo.InvariantCulture));
                stoneCmd.ExecuteNonQuery();
                marked = true;
            }

            transaction.Commit();
        }
        finally { _lock.Release(); }

        if (marked)
            RaiseSyncRelevantDataChanged();
    }

    // A user owner records the notifications.delete_all batch intent and drops the pending
    // per-row notification state it subsumes (tombstones and the read_all mark) in the same
    // transaction — one server call empties the whole account list.
    public void DeleteAllNotifications(string ownerKind, string ownerId)
    {
        var marked = ownerKind == OwnerKindUser;

        AcquireLock();
        try
        {
            using var transaction = _connection.BeginTransaction();

            using (var cmd = _connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM notifications WHERE owner_kind = $ok AND owner_id = $oi";
                cmd.Parameters.AddWithValue("$ok", ownerKind);
                cmd.Parameters.AddWithValue("$oi", ownerId);
                cmd.ExecuteNonQuery();
            }

            if (marked)
            {
                using (var stones = _connection.CreateCommand())
                {
                    stones.Transaction = transaction;
                    stones.CommandText = "DELETE FROM sync_tombstones WHERE owner_kind = $ok AND owner_id = $oi AND kind = $kind";
                    stones.Parameters.AddWithValue("$ok", ownerKind);
                    stones.Parameters.AddWithValue("$oi", ownerId);
                    stones.Parameters.AddWithValue("$kind", TombstoneKindNotification);
                    stones.ExecuteNonQuery();
                }
                RemoveSyncMetaCore(ownerKind, ownerId, SyncMetaNotificationsReadAll, transaction);
                SetSyncMetaCore(ownerKind, ownerId, SyncMetaNotificationsDeleteAll, NextSyncDirtyGeneration(), transaction);
            }

            transaction.Commit();
        }
        finally { _lock.Release(); }

        if (marked)
            RaiseSyncRelevantDataChanged();
    }

    // Runs inside the caller's transaction (caller holds _lock): whether the row exists,
    // is user-owned AND is sync-tracked — the gate for every per-row notification dirty
    // mark. A pre-sync row (server_id NULL and '' type, written before the columns existed)
    // is contractually local-only: marking it on a read toggle would push it through
    // notifications/add on the next pass (PR #127 review, m2).
    private bool IsUserNotificationCore(SqliteTransaction transaction, int id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT owner_kind, server_id, type FROM notifications WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
            return false;
        return reader.GetString(0) == OwnerKindUser
            && (!reader.IsDBNull(1) || reader.GetString(2).Length > 0);
    }

    // The coordinator's push enumeration (docs/plans/add-api-v21-notifications-sync):
    // user rows whose latest state the server has not confirmed. Oldest first, so pushed
    // entries get ascending server ids in the order they happened.
    public List<NotificationRecord> GetDirtyNotifications(string ownerKind, string ownerId)
    {
        AcquireLock();
        try
        {
            var items = new List<NotificationRecord>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT id, title, subtitle, created_at, is_read, server_id, type, local_rev
                FROM notifications WHERE owner_kind = $ok AND owner_id = $oi AND is_dirty = 1
                ORDER BY id
                """;
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                items.Add(new NotificationRecord
                {
                    Id = reader.GetInt32(0),
                    Title = reader.GetString(1),
                    Subtitle = reader.GetString(2),
                    CreatedAt = DateTime.Parse(reader.GetString(3), null, DateTimeStyles.RoundtripKind),
                    IsRead = reader.GetInt32(4) != 0,
                    ServerId = reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    Type = reader.GetString(6),
                    IsDirty = true,
                    LocalRev = reader.GetInt32(7)
                });
            }
            return items;
        }
        finally { _lock.Release(); }
    }

    // The push's write-back after notifications/add. Returns false when the row no longer
    // exists — the user deleted it while the add was in flight; the caller then tombstones
    // the freshly minted server id so the next pass deletes it remotely instead of letting
    // a later merge resurrect the entry (PR #127 review, m5). No event.
    public bool SetNotificationServerId(int id, long serverId)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE notifications SET server_id = $sid WHERE id = $id";
            cmd.Parameters.AddWithValue("$sid", serverId);
            cmd.Parameters.AddWithValue("$id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
        finally { _lock.Release(); }
    }

    // A notification tombstone minted directly from a server id — the delete-during-add
    // window above, where no local row remains to route the delete through. Raises the
    // sync event so a follow-up pass pushes the delete.
    public void WriteNotificationTombstone(string ownerKind, string ownerId, long serverId)
    {
        if (ownerKind != OwnerKindUser)
            return;

        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO sync_tombstones (owner_kind, owner_id, kind, uid, vendor_id, product_id, server_updated_at)
                VALUES ($ok, $oi, $kind, $uid, 0, 0, NULL)
                """;
            cmd.Parameters.AddWithValue("$ok", ownerKind);
            cmd.Parameters.AddWithValue("$oi", ownerId);
            cmd.Parameters.AddWithValue("$kind", TombstoneKindNotification);
            cmd.Parameters.AddWithValue("$uid", serverId.ToString(CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }

        RaiseSyncRelevantDataChanged();
    }

    // Rev-guarded dirty clear, the notifications twin of ClearMacroDirty: a toggle landing
    // while the push was in flight bumped local_rev, so the stale clear misses and the
    // follow-up pass pushes the newer state.
    public void ClearNotificationDirty(int id, int localRev)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE notifications SET is_dirty = 0 WHERE id = $id AND local_rev = $rev";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$rev", localRev);
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    // ── Remember-me / saved authentication ───────────────────────────────────
    //
    // The bearer token is stored encrypted with DPAPI (CurrentUser), never in plain text: the
    // database is an ordinary file under %LocalAppData%, readable by any process of this user and
    // copied by backups and profile sync, and the token is a 30-day credential for the account
    // (docs/plans/fix-repository-audit-findings, S7). DPAPI binds it to this Windows user on this
    // machine, so a copied database file is useless elsewhere.
    //
    // Stored as SavedTokenPrefix + base64 in the existing token column. A row without the prefix
    // was written by a build before 2026-09-24 in plain text; it is read once and rewritten
    // protected. A value that no longer decrypts (profile restored on another PC, a reset
    // password vault) means the saved sign-in is gone: the row is cleared and the user signs in
    // again.
    private const string SavedTokenPrefix = "dpapi1:";
    private static readonly byte[] SavedTokenEntropy = Encoding.UTF8.GetBytes("ProoveGaming.saved_auth.v1");

    internal static string ProtectSavedToken(string token)
    {
        // Intentional: the product is Windows-only and there is no equivalent protection to fall
        // back to. Do not "fix" this with a plaintext fallback; that is the hole it closes.
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The saved sign-in token is protected with Windows DPAPI.");

        var cipher = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), SavedTokenEntropy, DataProtectionScope.CurrentUser);
        return SavedTokenPrefix + Convert.ToBase64String(cipher);
    }

    // Null when the stored value cannot be decrypted by this user on this machine. A value without
    // the prefix is legacy plain text and is returned as is (the caller re-protects it).
    internal static string? UnprotectSavedToken(string stored)
    {
        if (!stored.StartsWith(SavedTokenPrefix, StringComparison.Ordinal))
            return stored;

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The saved sign-in token is protected with Windows DPAPI.");

        try
        {
            var cipher = Convert.FromBase64String(stored[SavedTokenPrefix.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(cipher, SavedTokenEntropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    // Test seam (InternalsVisibleTo ProoveGaming.App.Tests): the DPAPI step, replaceable so the
    // failure paths below can be driven without a broken Windows profile.
    internal Func<string, string> ProtectToken { get; set; } = ProtectSavedToken;

    // "Cannot protect" means "cannot remember", never a failed sign-in. DPAPI throws when the
    // profile's master key is unavailable (a temporary or damaged profile); sign-in must still
    // complete, only without "Remember me".
    public void SaveAuth(int customerId, string email, string nickname, string token, DateTime expiresAtUtc)
    {
        string protectedToken;
        try
        {
            protectedToken = ProtectToken(token);
        }
        catch (CryptographicException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Saved sign-in not persisted: the token could not be protected. {ex.Message}");
            // Whatever was saved before belongs to an earlier sign-in, possibly another account;
            // leaving it would restore THAT session on the next launch.
            ClearSavedAuth();
            return;
        }

        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO saved_auth (id, customer_id, email, nickname, token, token_expires_at)
                VALUES (1, $cid, $email, $nickname, $token, $exp);
                """;
            cmd.Parameters.AddWithValue("$cid", customerId);
            cmd.Parameters.AddWithValue("$email", email);
            cmd.Parameters.AddWithValue("$nickname", nickname);
            cmd.Parameters.AddWithValue("$token", protectedToken);
            cmd.Parameters.AddWithValue("$exp", expiresAtUtc.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    public SavedAuth? GetSavedAuth()
    {
        AcquireLock();
        try
        {
            int customerId;
            string email, nickname, storedToken;
            DateTime expires;
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT customer_id, email, nickname, token, token_expires_at FROM saved_auth WHERE id = 1";
                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) return null;

                if (!DateTime.TryParse(reader.GetString(4), null, DateTimeStyles.RoundtripKind, out expires))
                    return null;

                customerId = reader.GetInt32(0);
                email = reader.GetString(1);
                nickname = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                storedToken = reader.GetString(3);
            }

            var token = UnprotectSavedToken(storedToken);
            if (token is null)
            {
                using var clear = _connection.CreateCommand();
                clear.CommandText = "DELETE FROM saved_auth;";
                clear.ExecuteNonQuery();
                return null;
            }

            if (!storedToken.StartsWith(SavedTokenPrefix, StringComparison.Ordinal))
                TryUpgradeLegacyToken(token);

            return new SavedAuth
            {
                CustomerId = customerId,
                Email = email,
                Nickname = nickname,
                Token = token,
                TokenExpiresAtUtc = expires
            };
        }
        finally { _lock.Release(); }
    }

    // Called under _lock. A failure leaves the legacy row as it was (no worse than before
    // protection existed) and the next read tries again; it must never stop the App from
    // starting, which is where GetSavedAuth runs.
    private void TryUpgradeLegacyToken(string token)
    {
        string protectedToken;
        try
        {
            protectedToken = ProtectToken(token);
        }
        catch (CryptographicException ex)
        {
            System.Diagnostics.Debug.WriteLine($"Legacy saved sign-in left unprotected for now: {ex.Message}");
            return;
        }

        using var upgrade = _connection.CreateCommand();
        upgrade.CommandText = "UPDATE saved_auth SET token = $token WHERE id = 1";
        upgrade.Parameters.AddWithValue("$token", protectedToken);
        upgrade.ExecuteNonQuery();
    }

    public void ClearSavedAuth()
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM saved_auth;";
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    public void UpdateSavedAuthNickname(string nickname)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE saved_auth SET nickname = $n WHERE id = 1;";
            cmd.Parameters.AddWithValue("$n", nickname ?? string.Empty);
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    // ── Sign-in lockout (persisted so the 10-min timer survives app restart) ─

    public void SaveLockoutUntil(DateTime untilUtc)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO sign_in_lockout (id, lockout_until)
                VALUES (1, $until);
                """;
            cmd.Parameters.AddWithValue("$until", untilUtc.ToString("O"));
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    public DateTime? GetLockoutUntil()
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT lockout_until FROM sign_in_lockout WHERE id = 1";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            return DateTime.TryParse(reader.GetString(0), null, DateTimeStyles.RoundtripKind, out var u) ? u : (DateTime?)null;
        }
        finally { _lock.Release(); }
    }

    public void ClearLockout()
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM sign_in_lockout;";
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    // ── Last sign-in (email + Remember Me preference; survives logout) ───────

    public void SaveLastSignIn(string email, bool rememberMe)
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO last_sign_in (id, email, remember_me)
                VALUES (1, $email, $rm);
                """;
            cmd.Parameters.AddWithValue("$email", email ?? string.Empty);
            cmd.Parameters.AddWithValue("$rm", rememberMe ? 1 : 0);
            cmd.ExecuteNonQuery();
        }
        finally { _lock.Release(); }
    }

    public (string Email, bool RememberMe)? GetLastSignIn()
    {
        AcquireLock();
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT email, remember_me FROM last_sign_in WHERE id = 1";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return null;
            var email = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var rm = !reader.IsDBNull(1) && reader.GetInt32(1) != 0;
            return (email, rm);
        }
        finally { _lock.Release(); }
    }

    // ── Guest → user migration (D1: guest wins) ──────────────────────────────
    // Re-keys every row owned by the given guest session UUID to the customer id.
    // Any pre-existing user rows for the same natural key are overwritten (the guest's
    // values win — colliding user rows are deleted before the UPDATE). device_key is part
    // of the natural key wherever a device is involved, so per-unit state migrates
    // independently for each physical device the guest touched.
    // Routed through the storage executor: the transaction runs on a pool thread (the UI
    // paints during sign-in) and -- because the queue is FIFO -- strictly after every guest
    // write enqueued before it, so no queued write can recreate a guest row after the rekey.
    public Task MigrateGuestToUserAsync(
        Guid guestSessionId, int customerId, CancellationToken ct = default)
    {
        var guestId = guestSessionId.ToString("D");
        var userId  = customerId.ToString(CultureInfo.InvariantCulture);
        return RunOnStorageAsync(() => MigrateGuestToUserCoreAsync(guestId, userId, ct));
    }

    private async Task MigrateGuestToUserCoreAsync(string guestId, string userId, CancellationToken ct)
    {
        OnBeforeStorageAccess?.Invoke();
        await _lock.WaitAsync(ct);
        try
        {
            await using var tx = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);

            // device_settings — key: (vendor_id, product_id, device_key, key).
            await ReKeyAsync(tx, ct, """
                DELETE FROM device_settings
                WHERE owner_kind = 'user' AND owner_id = $uid
                  AND (vendor_id, product_id, device_key, key) IN (
                      SELECT vendor_id, product_id, device_key, key FROM device_settings
                      WHERE owner_kind = 'guest' AND owner_id = $gid
                  );
                """,
                "UPDATE device_settings SET owner_kind = 'user', owner_id = $uid WHERE owner_kind = 'guest' AND owner_id = $gid;",
                userId, guestId);

            // app_preferences — key: (key).
            await ReKeyAsync(tx, ct, """
                DELETE FROM app_preferences
                WHERE owner_kind = 'user' AND owner_id = $uid
                  AND key IN (
                      SELECT key FROM app_preferences
                      WHERE owner_kind = 'guest' AND owner_id = $gid
                  );
                """,
                "UPDATE app_preferences SET owner_kind = 'user', owner_id = $uid WHERE owner_kind = 'guest' AND owner_id = $gid;",
                userId, guestId);

            // device_actuation — key: (vendor_id, product_id, device_key, slot).
            await ReKeyAsync(tx, ct, """
                DELETE FROM device_actuation
                WHERE owner_kind = 'user' AND owner_id = $uid
                  AND (vendor_id, product_id, device_key, slot) IN (
                      SELECT vendor_id, product_id, device_key, slot FROM device_actuation
                      WHERE owner_kind = 'guest' AND owner_id = $gid
                  );
                """,
                "UPDATE device_actuation SET owner_kind = 'user', owner_id = $uid WHERE owner_kind = 'guest' AND owner_id = $gid;",
                userId, guestId);

            // device_extended_keys — key: (vendor_id, product_id, device_key, slot1).
            await ReKeyAsync(tx, ct, """
                DELETE FROM device_extended_keys
                WHERE owner_kind = 'user' AND owner_id = $uid
                  AND (vendor_id, product_id, device_key, slot1) IN (
                      SELECT vendor_id, product_id, device_key, slot1 FROM device_extended_keys
                      WHERE owner_kind = 'guest' AND owner_id = $gid
                  );
                """,
                "UPDATE device_extended_keys SET owner_kind = 'user', owner_id = $uid WHERE owner_kind = 'guest' AND owner_id = $gid;",
                userId, guestId);

            // device_rgb — key: (vendor_id, product_id, device_key).
            await ReKeyAsync(tx, ct, """
                DELETE FROM device_rgb
                WHERE owner_kind = 'user' AND owner_id = $uid
                  AND (vendor_id, product_id, device_key) IN (
                      SELECT vendor_id, product_id, device_key FROM device_rgb
                      WHERE owner_kind = 'guest' AND owner_id = $gid
                  );
                """,
                "UPDATE device_rgb SET owner_kind = 'user', owner_id = $uid WHERE owner_kind = 'guest' AND owner_id = $gid;",
                userId, guestId);

            // device_rgb_effect — key: (vendor_id, product_id, device_key, effect_key).
            await ReKeyAsync(tx, ct, """
                DELETE FROM device_rgb_effect
                WHERE owner_kind = 'user' AND owner_id = $uid
                  AND (vendor_id, product_id, device_key, effect_key) IN (
                      SELECT vendor_id, product_id, device_key, effect_key FROM device_rgb_effect
                      WHERE owner_kind = 'guest' AND owner_id = $gid
                  );
                """,
                "UPDATE device_rgb_effect SET owner_kind = 'user', owner_id = $uid WHERE owner_kind = 'guest' AND owner_id = $gid;",
                userId, guestId);

            // button_bindings — key: (vendor_id, product_id, device_key, button).
            await ReKeyAsync(tx, ct, """
                DELETE FROM button_bindings
                WHERE owner_kind = 'user' AND owner_id = $uid
                  AND (vendor_id, product_id, device_key, button) IN (
                      SELECT vendor_id, product_id, device_key, button FROM button_bindings
                      WHERE owner_kind = 'guest' AND owner_id = $gid
                  );
                """,
                "UPDATE button_bindings SET owner_kind = 'user', owner_id = $uid WHERE owner_kind = 'guest' AND owner_id = $gid;",
                userId, guestId);

            // key_bindings — key: (vendor_id, product_id, device_key, phys_scancode).
            await ReKeyAsync(tx, ct, """
                DELETE FROM key_bindings
                WHERE owner_kind = 'user' AND owner_id = $uid
                  AND (vendor_id, product_id, device_key, phys_scancode) IN (
                      SELECT vendor_id, product_id, device_key, phys_scancode FROM key_bindings
                      WHERE owner_kind = 'guest' AND owner_id = $gid
                  );
                """,
                "UPDATE key_bindings SET owner_kind = 'user', owner_id = $uid WHERE owner_kind = 'guest' AND owner_id = $gid;",
                userId, guestId);

            // device_applied_preset — key: (vendor_id, product_id, device_key).
            await ReKeyAsync(tx, ct, """
                DELETE FROM device_applied_preset
                WHERE owner_kind = 'user' AND owner_id = $uid
                  AND (vendor_id, product_id, device_key) IN (
                      SELECT vendor_id, product_id, device_key FROM device_applied_preset
                      WHERE owner_kind = 'guest' AND owner_id = $gid
                  );
                """,
                "UPDATE device_applied_preset SET owner_kind = 'user', owner_id = $uid WHERE owner_kind = 'guest' AND owner_id = $gid;",
                userId, guestId);

            // key_macro_bindings — key: (vendor_id, product_id, device_key, matrix_index).
            await ReKeyAsync(tx, ct, """
                DELETE FROM key_macro_bindings
                WHERE owner_kind = 'user' AND owner_id = $uid
                  AND (vendor_id, product_id, device_key, matrix_index) IN (
                      SELECT vendor_id, product_id, device_key, matrix_index FROM key_macro_bindings
                      WHERE owner_kind = 'guest' AND owner_id = $gid
                  );
                """,
                "UPDATE key_macro_bindings SET owner_kind = 'user', owner_id = $uid WHERE owner_kind = 'guest' AND owner_id = $gid;",
                userId, guestId);

            // macros — id is globally unique, so there is no natural-key collision here; the
            // rows just move from guest → user. No cascade needed on macro_events (they hang
            // off macro_id which is preserved).
            await using (var rekey = _connection.CreateCommand())
            {
                rekey.Transaction = tx;
                rekey.CommandText = "UPDATE macros SET owner_kind = 'user', owner_id = $uid WHERE owner_kind = 'guest' AND owner_id = $gid;";
                rekey.Parameters.AddWithValue("$uid", userId);
                rekey.Parameters.AddWithValue("$gid", guestId);
                await rekey.ExecuteNonQueryAsync(ct);
            }

            // presets — id is globally unique, no natural-key collision to resolve.
            await using (var rekey = _connection.CreateCommand())
            {
                rekey.Transaction = tx;
                rekey.CommandText = "UPDATE presets SET owner_kind = 'user', owner_id = $uid WHERE owner_kind = 'guest' AND owner_id = $gid;";
                rekey.Parameters.AddWithValue("$uid", userId);
                rekey.Parameters.AddWithValue("$gid", guestId);
                await rekey.ExecuteNonQueryAsync(ct);
            }

            // notifications — append-only history, no natural key; the rows just move. The
            // migrated rows are marked dirty so the reconcile pass pushes them to the new
            // account's server list (docs/plans/add-api-v21-notifications-sync); no signal —
            // the upgrade push that follows ends in SyncCompleted, which triggers the pass.
            // Pre-sync rows (server_id NULL and '' type) migrate WITHOUT the mark — they are
            // contractually local-only, same gate as SetNotificationRead (PR #127, m2).
            await using (var rekey = _connection.CreateCommand())
            {
                rekey.Transaction = tx;
                rekey.CommandText = """
                    UPDATE notifications
                    SET owner_kind = 'user', owner_id = $uid,
                        is_dirty = CASE WHEN server_id IS NOT NULL OR type != '' THEN 1 ELSE is_dirty END,
                        local_rev = local_rev + (CASE WHEN server_id IS NOT NULL OR type != '' THEN 1 ELSE 0 END)
                    WHERE owner_kind = 'guest' AND owner_id = $gid;
                    """;
                rekey.Parameters.AddWithValue("$uid", userId);
                rekey.Parameters.AddWithValue("$gid", guestId);
                await rekey.ExecuteNonQueryAsync(ct);
            }

            // devices — guest rows carry owner_id = '' (the guest device list is session-scoped as
            // a whole, not uuid-keyed), so they are matched by owner_kind alone. Guest wins on the
            // (vendor, product, name) natural key, like every table above.
            await using (var wipe = _connection.CreateCommand())
            {
                wipe.Transaction = tx;
                wipe.CommandText = """
                    DELETE FROM devices
                    WHERE owner_kind = 'user' AND owner_id = $uid
                      AND (vendor_id, product_id, name) IN (
                          SELECT vendor_id, product_id, name FROM devices WHERE owner_kind = 'guest'
                      );
                    """;
                wipe.Parameters.AddWithValue("$uid", userId);
                await wipe.ExecuteNonQueryAsync(ct);
            }
            await using (var rekey = _connection.CreateCommand())
            {
                rekey.Transaction = tx;
                rekey.CommandText = "UPDATE devices SET owner_kind = 'user', owner_id = $uid WHERE owner_kind = 'guest';";
                rekey.Parameters.AddWithValue("$uid", userId);
                await rekey.ExecuteNonQueryAsync(ct);
            }

            // Roster intent (docs/plans/add-api-v21-device-roster): the migrated cards' models
            // are owed to the new account's server roster. Marked without signalling — the
            // upgrade push that follows this migration ends in SyncCompleted, which triggers
            // the coordinator pass that consumes these.
            var migratedBuckets = new List<(int VendorId, int ProductId)>();
            await using (var buckets = _connection.CreateCommand())
            {
                buckets.Transaction = tx;
                buckets.CommandText = """
                    SELECT DISTINCT
                        CASE WHEN settings_vendor_id > 0 THEN settings_vendor_id ELSE vendor_id END,
                        CASE WHEN settings_product_id > 0 THEN settings_product_id ELSE product_id END
                    FROM devices WHERE owner_kind = 'user' AND owner_id = $uid
                    """;
                buckets.Parameters.AddWithValue("$uid", userId);
                await using var reader = await buckets.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    migratedBuckets.Add((reader.GetInt32(0), reader.GetInt32(1)));
            }
            foreach (var (bucketVid, bucketPid) in migratedBuckets)
            {
                if (bucketVid > 0 && bucketPid > 0)
                    SetSyncMetaCore(OwnerKindUser, userId,
                        SyncMetaDeviceRosterAdd(bucketVid, bucketPid), NextSyncDirtyGeneration(), tx);
            }

            await tx.CommitAsync(ct);
        }
        finally { _lock.Release(); }
    }

    // Applies a guest-wins pair (delete colliding user rows, then re-key guest rows) inside
    // an existing transaction. Caller already holds _lock.
    private async Task ReKeyAsync(
        SqliteTransaction tx, CancellationToken ct,
        string wipeSql, string rekeySql, string userId, string guestId)
    {
        await using (var wipe = _connection.CreateCommand())
        {
            wipe.Transaction = tx;
            wipe.CommandText = wipeSql;
            wipe.Parameters.AddWithValue("$uid", userId);
            wipe.Parameters.AddWithValue("$gid", guestId);
            await wipe.ExecuteNonQueryAsync(ct);
        }
        await using (var rekey = _connection.CreateCommand())
        {
            rekey.Transaction = tx;
            rekey.CommandText = rekeySql;
            rekey.Parameters.AddWithValue("$uid", userId);
            rekey.Parameters.AddWithValue("$gid", guestId);
            await rekey.ExecuteNonQueryAsync(ct);
        }
    }

    public void Dispose()
    {
        // Drain the executor first (bounded) so queued writes reach disk before the connection
        // teardown -- defensive double of the Exit-handler drain (idempotent;
        // DrainStorageQueueAsync never faults, so the wait can only time out). Only THEN flip
        // _disposed: items enqueued up to this point run against a live connection; stragglers
        // enqueued during teardown are dropped by the run-time check instead of crashing.
        //
        // Deliberately short: the app's Exit handler has already spent the sanctioned 3-second
        // drain right before disposing the container, so on the exit path this queue is
        // normally empty and both waits return immediately. The bound only matters for direct
        // Dispose callers (tests) and for a drain the Exit handler timed out on -- and there a
        // second multi-second wait would just stack on the first, holding the UI thread up to
        // ~7 s after the window is gone (PR #119 review m4).
        DrainStorageQueueAsync().Wait(TimeSpan.FromMilliseconds(500));
        Task tail;
        lock (_executorGate)
        {
            _disposed = true;
            tail = _executorTail;
        }
        tail.Wait(TimeSpan.FromMilliseconds(500));
        // Wait for any in-flight operation to complete before tearing down the connection.
        AcquireLock();
        try
        {
            _connection.Dispose();
        }
        finally
        {
            _lock.Release();
            _lock.Dispose();
        }
    }
}

public sealed class SavedAuth
{
    public int CustomerId { get; init; }
    public string Email { get; init; } = string.Empty;
    public string Nickname { get; init; } = string.Empty;
    public string Token { get; init; } = string.Empty;
    public DateTime TokenExpiresAtUtc { get; init; }

    public bool IsExpired => DateTime.UtcNow >= TokenExpiresAtUtc;
}
