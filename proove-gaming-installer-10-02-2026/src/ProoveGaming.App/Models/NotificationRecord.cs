using System;

namespace ProoveGaming.App.Models;

// One persisted bell entry — see the `notifications` table in LocalStorageService. Title and
// subtitle are stored as displayed (already localized): rows are history, not live resources.
public sealed class NotificationRecord
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string IconData { get; set; } = string.Empty;
    public string IconUri { get; set; } = string.Empty;
    public double IconWidth { get; set; } = 22;
    public double IconHeight { get; set; } = 12;
    public DateTime CreatedAt { get; set; }
    public bool IsRead { get; set; }

    // ── Notifications sync (docs/plans/add-api-v21-notifications-sync) ───────
    // The server's row id from api/notifications/*; null = the server never saw this entry.
    // Rows created before the sync existed keep null AND is_dirty 0, so they stay local-only.
    public long? ServerId { get; set; }

    // Type slug (NotificationTypes) — pushed to the server and mapped back to a bell icon
    // when a row is pulled on another machine. '' on pre-sync rows.
    public string Type { get; set; } = string.Empty;

    // Live-sync tracking pair, read-side (the presets/macros convention): IsDirty marks a
    // user-owned row whose latest state the server has not confirmed; LocalRev guards the
    // coordinator's clear against a mid-push re-toggle.
    public bool IsDirty { get; set; }
    public int LocalRev { get; set; }
}
