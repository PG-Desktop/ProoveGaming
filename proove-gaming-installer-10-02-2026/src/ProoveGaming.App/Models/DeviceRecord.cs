namespace ProoveGaming.App.Models;

public sealed class DeviceRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string DeviceTypeLabel { get; set; } = "";
    public string? ImagePath { get; set; }

    // Render scale for ImagePath, carried from the catalog. Stored with the card because a card
    // restored while its device is OFF is never refilled by discovery — see RecordToViewModel.
    // 0 means "unset"; the ViewModel reads that as 1.0.
    public double ImageScale { get; set; }
    public int? BatteryLevel { get; set; }

    // The lowest low-battery band already announced this discharge (20, 10; 0 = none) — the
    // persisted half of HomeViewModel's announcement guard. In-memory-only state re-toasted on
    // every app launch with a sub-20 % mouse ("random moments", QA 2026-08-31); storing it with
    // the card makes "once per band per discharge" survive restarts. Cleared by the genuine
    // re-arm (a live, off-charger reading at/above the re-arm level).
    public int LowBatteryAnnouncedBand { get; set; }
    public string? ConnectionIcon { get; set; }
    public string CardType { get; set; } = "single";
    public int VendorId { get; set; }
    public int ProductId { get; set; }

    // Windows Container ID (the card's routing DeviceKey). What tells two identical units of the
    // same model apart — VID/PID/name alone collapse them (PR #112 M2). Empty for pre-existing
    // rows and manually added cards.
    public string DeviceKey { get; set; } = "";

    // True when the product is one physical device reachable over several transports (RAMPAGE,
    // TAIPAN). Its routing identity above changes with the live transport — the dongle enumerates
    // under the receiver's VID/PID and Container ID, Bluetooth under the board's own with no key —
    // so the row is keyed by (owner, name) instead, matching what the UI matches on. Stored so a
    // card restored from disk knows the rule before discovery reports in.
    public bool MergeTransports { get; set; }

    // ── Settings identity ─────────────────────────────────────────────────────
    // The bucket this card's settings live in: every per-device row (device_rgb, device_settings,
    // key_bindings, presets, …) is addressed by (owner, SettingsVendorId, SettingsProductId,
    // SettingsDeviceKey). Distinct from the routing identity above, which follows whichever
    // transport is live: for a merged-transport product the settings identity is pinned per
    // product so cable ↔ dongle ↔ Bluetooth share one bucket.
    //
    // Persisted so a card restored from disk addresses the bucket it was actually written under,
    // BEFORE discovery reports in — a restored card has no catalog entry, and deriving the bucket
    // from the routing identity sent a pre-discovery page open to rows nothing else ever reads.
    //
    // 0 / "" means "row written before these columns existed". A legacy row falls back to today's
    // behaviour (settings product id from ProductId, empty settings key), so no stored row changes
    // meaning on upgrade.
    public int SettingsVendorId { get; set; }
    public int SettingsProductId { get; set; }
    public string SettingsDeviceKey { get; set; } = "";
}
