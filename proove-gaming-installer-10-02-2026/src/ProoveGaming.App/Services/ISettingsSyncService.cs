using System;
using System.Threading;
using System.Threading.Tasks;

namespace ProoveGaming.App.Services;

public interface ISettingsSyncService
{
    // Fired once when a guest registers a new account, after MigrateGuestToUserAsync has
    // rekeyed the local rows. The implementation starts a best-effort background push of the
    // migrated data (macros → presets → preferences → device state) to the API v2.0 sync
    // endpoints and returns immediately — it never blocks the sign-up flow and never throws.
    Task OnGuestUpgradedAsync(int customerId, CancellationToken ct = default);

    // Fired when a user signs into an existing account or a remembered session is restored
    // (never after a guest→register upgrade — a fresh account has nothing to pull). The
    // implementation starts a best-effort background pull of the account's cloud data
    // (macros → presets → preferences → device state) into the local database — additive
    // only, existing local rows are never modified — and returns immediately, same contract
    // as the push (docs/plans/add-api-v2-pull).
    Task OnSignedInAsync(int customerId, CancellationToken ct = default);

    // The background pull chain, or Task.CompletedTask when nothing ever ran. Extended by
    // every OnSignedInAsync call AND by each tick of the periodic notification poll (one
    // notifications/list merge per tick, docs/plans/add-api-v21-notifications-sync) — both
    // links share the never-faults contract and complete even on a network abort, and a
    // poll tick additionally caps its network time with its own timeout, so awaiting the
    // chain is always bounded. Awaited by DeviceDetailsViewModel before seeding a
    // default preset for a signed-in user, so the seed cannot mistake "the pull has not
    // landed yet" for "this account has no presets"
    // (docs/plans/fix-duplicate-default-preset).
    Task PullTask { get; }

    // Raised on a pool thread when a sign-in pull finished having written at least one local
    // record, so the UI can reload owner-scoped state. Subscribers marshal to the UI thread
    // themselves (same convention as ApiHttpClient.TokenRejected).
    event Action? PullCompletedWithChanges;

    // Raised on a pool thread when a background push or pull has finished, whether or not it
    // succeeded. SyncCoordinator treats it as its reconcile trigger: rows the run failed to
    // sync (server_updated_at still NULL) are picked up by the coordinator's next pass
    // (docs/plans/add-api-v2-live-sync). Subscribers must not throw.
    event Action? SyncCompleted;

    // Raised on a pool thread when a notifications/list merge — the sign-in pull's step or
    // the periodic poll — changed local bell rows, so HomeViewModel can reload the bell
    // without a full owner reload (docs/plans/add-api-v21-notifications-sync). Subscribers
    // marshal to the UI thread themselves.
    event Action? NotificationsPulled;
}
