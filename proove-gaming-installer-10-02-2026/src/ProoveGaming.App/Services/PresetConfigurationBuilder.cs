using System.Collections.Generic;
using ProoveGaming.App.Models;

namespace ProoveGaming.App.Services;

// Serializes one preset's snapshot tables into the API v2.0 `configuration` object
// (docs/plans/add-api-v2-sync): rgb { is_enabled, selected_effect, effects[] }, settings {},
// actuation [], key_bindings [], button_bindings [] — the shapes the contract's example
// shows — plus extended_keys [] (docs/plans/add-cyrex-extended-keys) and two client-defined
// extensions gated on IncludeMacroReferences.
public sealed class PresetConfigurationBuilder
{
    // Open question 2 of the plan: whether the server treats `configuration` as an opaque
    // blob. Until the backend answers, the payload includes our proposed macro references —
    // a `key_macro_bindings` array and a `macro_uid` field on button bindings. If the server
    // turns out to validate the shape strictly, flipping this single switch drops both.
    // static readonly rather than const so the dead branch doesn't warn as unreachable.
    internal static readonly bool IncludeMacroReferences = true;

    private readonly LocalStorageService _storage;

    public PresetConfigurationBuilder(LocalStorageService storage)
    {
        _storage = storage;
    }

    // Synchronous by design: the push invokes it inside a RunOnStorageAsync delegate so
    // every snapshot read stays on the storage executor. macroUidsById maps local macro ids
    // to their sync uids — the configuration must reference macros by uid, because local
    // integer ids mean nothing to another installation restoring this preset.
    public Dictionary<string, object?> Build(int presetId, IReadOnlyDictionary<int, string> macroUidsById)
    {
        var rgbState = _storage.GetPresetRgbState(presetId);
        var effects = new List<object>();
        if (rgbState is not null)
        {
            foreach (var e in rgbState.Effects)
                effects.Add(new
                {
                    effect_key = e.EffectKey,
                    hex = e.Hex,
                    brightness = e.Brightness,
                    speed = e.Speed,
                    is_rainbow = e.IsRainbow
                });
        }

        var actuation = new List<object>();
        foreach (var a in _storage.GetPresetActuation(presetId))
            actuation.Add(new
            {
                slot = a.Slot,
                press_centi_mm = a.PressCentiMm,
                release_centi_mm = a.ReleaseCentiMm,
                rapid_trigger = a.RapidTrigger,
                quick_press_centi_mm = a.QuickPressCentiMm,
                quick_release_centi_mm = a.QuickReleaseCentiMm
            });

        // Same opaque-blob assumption as key_macro_bindings (plan Open question 3): the nested
        // hold_key / tap_key objects are the matrix-record form the App and the DeviceHost share.
        // A pair mode (RS / SOCD) links two physical keys and picks no output at all, so both
        // fields travel as null rather than as a zeroed object — the shape documented to the
        // backend team, and what the applier's GetCode already reads back as
        // ExtendedKeyCodeRecord.None. The single-key modes keep their objects, Toggle's unused
        // tap_key included: there the zeros are the record's real content.
        var extendedKeys = new List<object>();
        foreach (var e in _storage.GetPresetExtendedKeys(presetId))
        {
            var isPair = ExtendedKeyMode.IsPair(e.Mode);
            extendedKeys.Add(new
            {
                mode = e.Mode,
                slot1 = e.Slot1,
                slot2 = e.Slot2,
                priority = e.Priority,
                time_ms = e.TimeMs,
                hold_key = isPair ? null : SerializeCode(e.HoldKey),
                tap_key = isPair ? null : SerializeCode(e.TapKey)
            });
        }

        var keyBindings = new List<object>();
        foreach (var (physScancode, typeByte, bitmask, targetCode) in _storage.GetPresetKeyBindings(presetId))
            keyBindings.Add(new
            {
                phys_scancode = physScancode,
                type_byte = typeByte,
                bitmask,
                target_code = targetCode
            });

        var buttonBindings = new List<object>();
        foreach (var (button, action, macroId) in _storage.GetPresetButtonBindings(presetId))
        {
            if (IncludeMacroReferences)
                buttonBindings.Add(new { button, action, macro_uid = ResolveMacroUid(macroId, macroUidsById) });
            else
                buttonBindings.Add(new { button, action });
        }

        var configuration = new Dictionary<string, object?>
        {
            // A preset without a stored RGB head row serializes as the table defaults
            // (enabled, no selected effect) so the section is always present, like the
            // contract's example.
            ["rgb"] = new
            {
                is_enabled = rgbState?.IsEnabled ?? true,
                selected_effect = rgbState?.SelectedEffect ?? string.Empty,
                effects
            },
            ["settings"] = _storage.GetPresetSettings(presetId),
            ["actuation"] = actuation,
            ["extended_keys"] = extendedKeys,
            ["key_bindings"] = keyBindings,
            ["button_bindings"] = buttonBindings,
        };

        if (IncludeMacroReferences)
        {
            var keyMacroBindings = new List<object>();
            foreach (var (matrixIndex, macroId) in _storage.GetPresetKeyMacroBindings(presetId))
                keyMacroBindings.Add(new { matrix_index = matrixIndex, macro_uid = ResolveMacroUid(macroId, macroUidsById) });
            configuration["key_macro_bindings"] = keyMacroBindings;
        }

        return configuration;
    }

    // An extended key's output in the matrix-record form the App and the DeviceHost share.
    private static object SerializeCode(ExtendedKeyCodeRecord code) =>
        new { type_byte = code.TypeByte, code1 = code.Code1, code2 = code.Code2 };

    // null both for a binding that carries no macro and for one whose macro is gone from the
    // library — a snapshot may outlive the macro it points at (see preset_key_macro_bindings'
    // schema note), and a dangling uid would be worse than an explicit null.
    private static string? ResolveMacroUid(int? macroId, IReadOnlyDictionary<int, string> macroUidsById) =>
        macroId is int id && macroUidsById.TryGetValue(id, out var uid) ? uid : null;
}
