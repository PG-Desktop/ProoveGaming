using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ProoveGaming.App.Models;

namespace ProoveGaming.App.Services;

// The reverse of PresetConfigurationBuilder (docs/plans/add-api-v2-pull): deserializes a
// pulled API v2.0 `configuration` object into a freshly inserted preset's snapshot tables.
// Tolerant by contract — the shape is only self-documented (plan Open question 1), so a
// malformed section (wrong JSON kind) is skipped while the rest applies, a malformed item
// inside a section is skipped, and unknown keys are ignored for forward compatibility.
// Any future configuration field must be added here AND in the builder — the round-trip
// test (build → apply → build) pins the two together.
public sealed class PresetConfigurationApplier
{
    private readonly LocalStorageService _storage;
    private readonly ILogger<PresetConfigurationApplier>? _logger;

    // The logger is optional so the many direct constructions in the tests stay as they are; DI
    // always supplies one (App.axaml.cs registers AddLogging), and it is the only trace a section
    // that faulted leaves behind — see ApplySection.
    public PresetConfigurationApplier(LocalStorageService storage, ILogger<PresetConfigurationApplier>? logger = null)
    {
        _storage = storage;
        _logger = logger;
    }

    // Synchronous by design, like the builder: the pull invokes it inside the same
    // RunOnStorageAsync delegate that inserted the preset, so every snapshot write stays on
    // the storage executor. macroIdsByUid maps sync uids to local macro ids — the inverse of
    // the builder's macroUidsById — so pulled bindings resolve to the macros the same pull
    // just inserted (or already-present ones). A null or unresolvable macro_uid stores the
    // binding without a macro, the same explicit-null stance the builder takes.
    public void Apply(int presetId, JsonElement configuration, IReadOnlyDictionary<string, int> macroIdsByUid)
    {
        if (configuration.ValueKind != JsonValueKind.Object)
            return;

        // Each section is isolated from the others. Item-level tolerance is the guards inside the
        // Apply* methods; this is the floor under them — a fault they did not foresee (a constraint
        // a payload this client did not write can still reach, a column a later schema adds) would
        // otherwise escape the pull's best-effort catch and cost the REST of that sign-in pull: the
        // remaining presets, the device-state step and every pending-apply mark after it, silently
        // and once only, since the uid is known from then on (review B1). One section is the
        // smaller loss, and the warning below is what says which.
        ApplySection(presetId, "rgb", () => ApplyRgb(presetId, configuration));
        ApplySection(presetId, "settings", () => ApplySettings(presetId, configuration));
        ApplySection(presetId, "actuation", () => ApplyActuation(presetId, configuration));
        ApplySection(presetId, "extended_keys", () => ApplyExtendedKeys(presetId, configuration));
        ApplySection(presetId, "key_bindings", () => ApplyKeyBindings(presetId, configuration));
        ApplySection(presetId, "button_bindings", () => ApplyButtonBindings(presetId, configuration, macroIdsByUid));
        ApplySection(presetId, "key_macro_bindings", () => ApplyKeyMacroBindings(presetId, configuration, macroIdsByUid));
    }

    private void ApplySection(int presetId, string section, Action apply)
    {
        try
        {
            apply();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Sign-in pull: section '{Section}' of preset {PresetId} faulted and was skipped; the rest of the preset still applied.",
                section, presetId);
        }
    }

    private void ApplyRgb(int presetId, JsonElement configuration)
    {
        if (!configuration.TryGetProperty("rgb", out var rgb) || rgb.ValueKind != JsonValueKind.Object)
            return;

        var effects = new List<RgbEffectRecord>();
        var seenKeys = new HashSet<string>();
        if (rgb.TryGetProperty("effects", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                // effect_key is the row's primary key (with preset_id): a duplicate or
                // missing key would fault the insert, so such items are dropped.
                if (item.ValueKind != JsonValueKind.Object
                    || GetString(item, "effect_key") is not { Length: > 0 } effectKey
                    || !seenKeys.Add(effectKey))
                    continue;

                effects.Add(new RgbEffectRecord
                {
                    EffectKey = effectKey,
                    Hex = GetString(item, "hex") ?? string.Empty,
                    Brightness = GetDouble(item, "brightness") ?? 0,
                    Speed = GetDouble(item, "speed") ?? 0,
                    IsRainbow = GetBool(item, "is_rainbow") ?? false,
                });
            }
        }

        // Head-row defaults mirror the builder's serialization of a preset without a stored
        // RGB row (enabled, no selected effect), so build → apply → build is stable.
        _storage.InsertPresetRgbState(
            presetId,
            GetBool(rgb, "is_enabled") ?? true,
            GetString(rgb, "selected_effect") ?? string.Empty,
            effects);
    }

    private void ApplySettings(int presetId, JsonElement configuration)
    {
        if (!configuration.TryGetProperty("settings", out var settings) || settings.ValueKind != JsonValueKind.Object)
            return;

        var values = new List<(string Key, double Value)>();
        foreach (var property in settings.EnumerateObject())
        {
            // The builder serializes a Dictionary<string, double>; anything else (a string,
            // a nested object) is a foreign key/value pair this client cannot store.
            if (property.Value.ValueKind == JsonValueKind.Number)
                values.Add((property.Name, property.Value.GetDouble()));
        }

        if (values.Count > 0)
            _storage.InsertPresetSettings(presetId, values);
    }

    private void ApplyActuation(int presetId, JsonElement configuration)
    {
        if (!configuration.TryGetProperty("actuation", out var array) || array.ValueKind != JsonValueKind.Array)
            return;

        var rows = new List<ActuationRecord>();
        var seenSlots = new HashSet<int>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || GetInt(item, "slot") is not int slot
                || !seenSlots.Add(slot))
                continue;

            rows.Add(new ActuationRecord(
                slot,
                GetInt(item, "press_centi_mm") ?? 0,
                GetInt(item, "release_centi_mm") ?? 0,
                GetBool(item, "rapid_trigger") ?? false,
                GetInt(item, "quick_press_centi_mm") ?? 0,
                GetInt(item, "quick_release_centi_mm") ?? 0));
        }

        if (rows.Count > 0)
            _storage.InsertPresetActuation(presetId, rows);
    }

    private void ApplyExtendedKeys(int presetId, JsonElement configuration)
    {
        if (!configuration.TryGetProperty("extended_keys", out var array) || array.ValueKind != JsonValueKind.Array)
            return;

        var rows = new List<ExtendedKeyRecord>();
        var seenSlots = new HashSet<int>();
        foreach (var item in array.EnumerateArray())
        {
            // mode and slot1 are the row's identity, and slot1 is also its primary key (with
            // preset_id) — an item carrying neither is nothing this table can hold.
            if (item.ValueKind != JsonValueKind.Object
                || GetInt(item, "mode") is not int mode
                || GetInt(item, "slot1") is not int slot1)
                continue;

            // A mode outside the four the firmware defines has no encoding at all, and a slot is a
            // physical key index, so a negative one names no key. Either one stored would make the
            // host refuse the preset's WHOLE set on the next push — SetExtendedKeys is a whole-set
            // request — so the user's real records would never reach the board (review B2).
            if (slot1 < 0 || (!ExtendedKeyMode.IsPair(mode) && !ExtendedKeyMode.IsSingleKey(mode)))
                continue;

            // An SOCD priority the firmware does not define is as malformed as a missing slot: stored,
            // it would make the host refuse the preset's whole set on the next push (review S1).
            int priority = GetInt(item, "priority") ?? 0;
            if (mode == ExtendedKeyMode.Socd && !SocdPriority.IsDefined(priority))
                continue;

            // Same rule for a Mod-Tap item: a threshold off the slider or an output that is not a plain
            // keyboard key (missing, null, or a consumer command) is nothing the host would write.
            int timeMs = GetInt(item, "time_ms") ?? 0;
            var holdKey = GetCode(item, "hold_key");
            var tapKey = GetCode(item, "tap_key");
            if (mode == ExtendedKeyMode.ModTap
                && (!ExtendedKeyTime.IsDefined(timeMs) || !IsKeyboardOutput(holdKey) || !IsKeyboardOutput(tapKey)))
                continue;

            // And for a Toggle item, whose one output rides hold_key: a tap_key is nothing the host
            // would accept on this mode, so it is malformed rather than ignorable.
            if (mode == ExtendedKeyMode.Toggle
                && (!ExtendedKeyTime.IsDefined(timeMs) || !IsKeyboardOutput(holdKey) || !tapKey.IsNone))
                continue;

            // A single-key item (MT / TGL) with a second slot or a priority is refused by the host the
            // same way, so it is skipped here too rather than stored to block the set (review S1).
            int slot2 = GetInt(item, "slot2") ?? ExtendedKeyRecord.NoSlot;
            if (ExtendedKeyMode.IsSingleKey(mode) && (slot2 != ExtendedKeyRecord.NoSlot || priority != 0))
                continue;

            // And a pair item (RS / SOCD) is two physical keys: a missing, negative or self-paired
            // second slot is a record the host cannot encode, and it takes the whole set down with
            // it the same way (review B2).
            if (ExtendedKeyMode.IsPair(mode) && (slot2 < 0 || slot2 == slot1))
                continue;

            // A pair record is two slots and nothing else — no time and no outputs — except that
            // SOCD's LEN nibble also carries the priority validated above. The host refuses an entry
            // carrying a field its mode has no room for, so a stored one would take the whole set
            // down with it exactly as a bad slot does (review S11).
            if (ExtendedKeyMode.IsPair(mode)
                && (timeMs != 0 || !holdKey.IsNone || !tapKey.IsNone || (mode == ExtendedKeyMode.RapidSnap && priority != 0)))
                continue;

            // Normalise first, de-duplicate after. Normalized() swaps a reversed pair's slots, so
            // de-duplicating on the JSON order lets two items that both pass the guard collide on
            // PRIMARY KEY (preset_id, slot1) at insert time — the throw escapes the pull, not just
            // this section (review B1). And a pair OWNS both of its slots, since the board holds at
            // most one record per key, so both are reserved and not only the one the row is keyed
            // by; the host would refuse a set in which two records claim the same key.
            var record = new ExtendedKeyRecord(mode, slot1, slot2, priority, timeMs, holdKey, tapKey).Normalized();
            if (seenSlots.Contains(record.Slot1)
                || (record.Slot2 != ExtendedKeyRecord.NoSlot && seenSlots.Contains(record.Slot2)))
                continue;

            seenSlots.Add(record.Slot1);
            if (record.Slot2 != ExtendedKeyRecord.NoSlot)
                seenSlots.Add(record.Slot2);
            rows.Add(record);
        }

        if (rows.Count > 0)
            _storage.InsertPresetExtendedKeys(presetId, rows);
    }

    // An MT / TGL output the firmware can encode, term for term with the host's OutputKeyCode
    // (SmartInfiniteExtendedKeysService): a {0x10, mask, usage} record naming EXACTLY ONE key.
    //
    // Byte range first — a code1/code2 outside 0..255 is refused by the host's own intake, and the
    // whole set with it (review S5). Then exactly one of mask/usage: a record carrying neither is
    // no key at all, and one carrying both names two where the wire format has room for one. The
    // host used to encode that case usage-only and verify it, so the read-back could never match the
    // row, and the self-heal rewrote the flash on every page open without ever converging; it now
    // refuses the whole set instead, so storing such a row here costs the user every other record
    // (review round 4, m1).
    private static bool IsKeyboardOutput(ExtendedKeyCodeRecord code) =>
        code.TypeByte == KeyboardRecordType
        && code.Code1 is >= 0 and <= byte.MaxValue
        && code.Code2 is >= 0 and <= byte.MaxValue
        && (code.Code1 != 0) != (code.Code2 != 0);

    private const int KeyboardRecordType = 0x10;

    // A missing, null or malformed nested code object is "none" — the pair modes carry none.
    private static ExtendedKeyCodeRecord GetCode(JsonElement item, string name) =>
        item.TryGetProperty(name, out var code) && code.ValueKind == JsonValueKind.Object
            ? new ExtendedKeyCodeRecord(GetInt(code, "type_byte") ?? 0, GetInt(code, "code1") ?? 0, GetInt(code, "code2") ?? 0)
            : ExtendedKeyCodeRecord.None;

    private void ApplyKeyBindings(int presetId, JsonElement configuration)
    {
        if (!configuration.TryGetProperty("key_bindings", out var array) || array.ValueKind != JsonValueKind.Array)
            return;

        var rows = new List<(byte PhysScancode, byte TypeByte, byte Bitmask, byte TargetCode)>();
        var seenScancodes = new HashSet<byte>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || GetByte(item, "phys_scancode") is not byte physScancode
                || !seenScancodes.Add(physScancode))
                continue;

            rows.Add((
                physScancode,
                GetByte(item, "type_byte") ?? 0,
                GetByte(item, "bitmask") ?? 0,
                GetByte(item, "target_code") ?? 0));
        }

        if (rows.Count > 0)
            _storage.InsertPresetKeyBindings(presetId, rows);
    }

    private void ApplyButtonBindings(int presetId, JsonElement configuration, IReadOnlyDictionary<string, int> macroIdsByUid)
    {
        if (!configuration.TryGetProperty("button_bindings", out var array) || array.ValueKind != JsonValueKind.Array)
            return;

        var rows = new List<(int Button, int Action, int? MacroId)>();
        var seenButtons = new HashSet<int>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || GetInt(item, "button") is not int button
                || !seenButtons.Add(button))
                continue;

            rows.Add((button, GetInt(item, "action") ?? 0, ResolveMacroId(item, macroIdsByUid)));
        }

        if (rows.Count > 0)
            _storage.InsertPresetButtonBindings(presetId, rows);
    }

    private void ApplyKeyMacroBindings(int presetId, JsonElement configuration, IReadOnlyDictionary<string, int> macroIdsByUid)
    {
        if (!configuration.TryGetProperty("key_macro_bindings", out var array) || array.ValueKind != JsonValueKind.Array)
            return;

        var rows = new List<(int MatrixIndex, int MacroId)>();
        var seenIndexes = new HashSet<int>();
        foreach (var item in array.EnumerateArray())
        {
            // Unlike button bindings, preset_key_macro_bindings has a NOT NULL macro_id —
            // a binding whose macro cannot be resolved has nothing to store, so it drops.
            if (item.ValueKind != JsonValueKind.Object
                || GetInt(item, "matrix_index") is not int matrixIndex
                || ResolveMacroId(item, macroIdsByUid) is not int macroId
                || !seenIndexes.Add(matrixIndex))
                continue;

            rows.Add((matrixIndex, macroId));
        }

        if (rows.Count > 0)
            _storage.InsertPresetKeyMacroBindings(presetId, rows);
    }

    private static int? ResolveMacroId(JsonElement item, IReadOnlyDictionary<string, int> macroIdsByUid) =>
        GetString(item, "macro_uid") is { Length: > 0 } uid && macroIdsByUid.TryGetValue(uid, out var id) ? id : null;

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool? GetBool(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static double? GetDouble(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;

    private static int? GetInt(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : null;

    private static byte? GetByte(JsonElement obj, string name) =>
        GetInt(obj, name) is int value and >= byte.MinValue and <= byte.MaxValue ? (byte)value : null;
}
