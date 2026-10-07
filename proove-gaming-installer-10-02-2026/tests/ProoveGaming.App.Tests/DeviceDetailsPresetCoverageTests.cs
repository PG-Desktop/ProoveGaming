using System;
using System.Collections.Generic;
using System.Linq;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// The preset-coverage audit: for EVERY supported keyboard, every section that board offers must
// survive save → drift → restore. This is the test that would have caught actuation being absent
// from the preset system for three releases, and it is the standing answer to "do presets carry
// everything, on every keyboard?".
//
// Driven at the storage layer on purpose. The snapshot contract IS the pair of table lists inside
// SavePresetSnapshot / LoadPresetSnapshotToDevice, and a section is in a preset exactly when it
// appears in both. A view-model test would exercise one board's wiring; this exercises the contract
// itself against each board's real capability set, so a section added later without being added to
// those lists fails here rather than in the field.
//
// Capabilities come from src/ProoveGaming.DeviceHost/appsettings.json (AllowedDevices). Keep the
// table below in step with it: a board gaining a capability should gain a column here.
public class DeviceDetailsPresetCoverageTests
{
    private static LocalStorageService NewInMemory() => new("Data Source=:memory:");

    private const string DeviceKey = "";
    private static string GuestId() => Guid.NewGuid().ToString("D");

    // Name, VID, PID, RGB, macros (key→macro bindings), actuation. Key remapping is on every
    // keyboard here, so it has no column.
    public static TheoryData<string, int, int, bool, bool, bool> Keyboards() => new()
    {
        { "Raster",  14000, 12385, true,  true, false },
        // T-Racer declares no top-level ProductId — it is transport-scoped (12290 wired, 12370
        // wireless). 12370 is the one its settings identity resolves to.
        { "T-Racer", 14000, 12370, true,  true, false },
        { "Rampage", 10473, 12888, true,  true, false },
        { "Cyrex",   10473, 12914, true,  true, true  },
        { "Aress",   10473, 12910, true,  true, true  },
        { "T800",    12625, 20525, true,  true, true  },
    };

    [Theory]
    [MemberData(nameof(Keyboards))]
    public void EverySectionABoardSupports_SurvivesASavePresetAndRestore(
        string name, int vid, int pid, bool rgb, bool macros, bool actuation)
    {
        using var storage = NewInMemory();
        var owner = GuestId();
        var presetId = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, vid, pid,
            new PresetRecord { Name = $"{name} preset" });

        // ── Configure every section this board offers ────────────────────────
        if (rgb)
        {
            storage.SaveRgbState(LocalStorageService.OwnerKindGuest, owner, vid, pid, DeviceKey,
                isEnabled: true, selectedEffect: "Neon",
                effects: [new RgbEffectRecord { EffectKey = "Neon", Hex = "#00FF00", Brightness = 55, Speed = 33 }]);
        }

        // Key remapping — every keyboard.
        storage.SetKeyBinding(LocalStorageService.OwnerKindGuest, owner, vid, pid, DeviceKey,
            physScancode: 0x04, typeByte: 0x10, bitmask: 0x00, targetCode: 0x05);

        if (macros)
            storage.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, owner, vid, pid, DeviceKey, 12, 34);

        if (actuation)
        {
            storage.ReplaceDeviceActuation(LocalStorageService.OwnerKindGuest, owner, vid, pid, DeviceKey,
                [new ActuationRecord(3, 150, 160, true, 20, 25)]);
        }

        // ── Save, then drift every one of them ───────────────────────────────
        storage.SavePresetSnapshot(presetId, LocalStorageService.OwnerKindGuest, owner, vid, pid, DeviceKey);

        if (rgb)
        {
            storage.SaveRgbState(LocalStorageService.OwnerKindGuest, owner, vid, pid, DeviceKey,
                isEnabled: false, selectedEffect: "Static",
                effects: [new RgbEffectRecord { EffectKey = "Static", Hex = "#FF0000", Brightness = 10, Speed = 10 }]);
        }

        storage.SetKeyBinding(LocalStorageService.OwnerKindGuest, owner, vid, pid, DeviceKey,
            physScancode: 0x04, typeByte: 0x10, bitmask: 0x00, targetCode: 0x99);
        if (macros)
            storage.SetKeyMacroBinding(LocalStorageService.OwnerKindGuest, owner, vid, pid, DeviceKey, 12, 999);
        if (actuation)
        {
            storage.ReplaceDeviceActuation(LocalStorageService.OwnerKindGuest, owner, vid, pid, DeviceKey,
                [new ActuationRecord(3, 999, 999, false, 99, 99)]);
        }

        // ── Switch back to the preset ────────────────────────────────────────
        storage.LoadPresetSnapshotToDevice(presetId, LocalStorageService.OwnerKindGuest, owner, vid, pid, DeviceKey);

        if (rgb)
        {
            var restored = storage.GetRgbState(LocalStorageService.OwnerKindGuest, owner, vid, pid, DeviceKey);
            Assert.NotNull(restored);
            Assert.True(restored!.IsEnabled);
            Assert.Equal("Neon", restored.SelectedEffect);
            Assert.Equal("#00FF00", Assert.Single(restored.Effects).Hex);
        }

        var keys = storage.GetKeyBindings(LocalStorageService.OwnerKindGuest, owner, vid, pid, DeviceKey);
        Assert.Equal(0x05, Assert.Single(keys).TargetCode);

        if (macros)
        {
            var bound = storage.GetKeyMacroBindings(LocalStorageService.OwnerKindGuest, owner, vid, pid, DeviceKey);
            Assert.Equal(34, Assert.Single(bound).MacroId);
        }

        if (actuation)
        {
            var act = Assert.Single(storage.GetDeviceActuation(LocalStorageService.OwnerKindGuest, owner, vid, pid, DeviceKey));
            Assert.Equal(150, act.PressCentiMm);
            Assert.True(act.RapidTrigger);
        }

        // And the preset now reads as clean — nothing left unsaved after a restore.
        Assert.True(storage.PresetMatchesLiveState(presetId, LocalStorageService.OwnerKindGuest, owner, vid, pid, DeviceKey));
    }

    // Boards without actuation must never acquire actuation rows through the preset machinery —
    // the capability gate has to hold at the storage layer too, not only in the view model.
    [Theory]
    [MemberData(nameof(Keyboards))]
    public void ABoardWithoutActuation_NeverGainsActuationRowsFromAPreset(
        string name, int vid, int pid, bool rgb, bool macros, bool actuation)
    {
        _ = name; _ = rgb; _ = macros;
        if (actuation) return;   // covered by the round trip above

        using var storage = NewInMemory();
        var owner = GuestId();
        var presetId = storage.InsertPreset(LocalStorageService.OwnerKindGuest, owner, vid, pid,
            new PresetRecord { Name = "P" });

        storage.SetKeyBinding(LocalStorageService.OwnerKindGuest, owner, vid, pid, DeviceKey, 0x04, 0x10, 0x00, 0x05);
        storage.SavePresetSnapshot(presetId, LocalStorageService.OwnerKindGuest, owner, vid, pid, DeviceKey);
        storage.LoadPresetSnapshotToDevice(presetId, LocalStorageService.OwnerKindGuest, owner, vid, pid, DeviceKey);

        Assert.Empty(storage.GetPresetActuation(presetId));
        Assert.Empty(storage.GetDeviceActuation(LocalStorageService.OwnerKindGuest, owner, vid, pid, DeviceKey));
    }
}
