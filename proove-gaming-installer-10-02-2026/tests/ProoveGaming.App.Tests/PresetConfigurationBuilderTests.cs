using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// PresetConfigurationBuilder serializes one preset's snapshot tables into the API v2.0
// `configuration` object (docs/plans/add-api-v2-sync). Assertions go through real JSON —
// the builder's anonymous types only matter in their serialized snake_case shape, which is
// exactly what the server receives.
public class PresetConfigurationBuilderTests
{
	private const int Vid = 0x04D9;
	private const int Pid = 0xA23F;
	private const string NoKey = "";
	private const string Guest = LocalStorageService.OwnerKindGuest;

	private static JsonElement BuildJson(
		LocalStorageService storage, int presetId, IReadOnlyDictionary<int, string>? macroUidsById = null)
	{
		var configuration = new PresetConfigurationBuilder(storage)
			.Build(presetId, macroUidsById ?? new Dictionary<int, string>());
		return JsonSerializer.SerializeToElement(configuration);
	}

	// Golden path: every section populated through the same public API the app uses (seed
	// live device state, snapshot it into the preset), macro references resolved to uids.
	[Fact]
	public void Build_FullSnapshot_SerializesToTheDocumentedShape()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "P" });

		const int macroId = 77;
		const string macroUid = "11111111-2222-3333-4444-555555555555";

		storage.SaveRgbState(Guest, owner, Vid, Pid, NoKey,
			isEnabled: true, selectedEffect: "Neon",
			effects: [new RgbEffectRecord { EffectKey = "Neon", Hex = "#00FF00", Brightness = 55, Speed = 33, IsRainbow = false }]);
		storage.SetDeviceSetting(Guest, owner, Vid, Pid, NoKey, "dpi_stage", 3);
		storage.SetButtonBinding(Guest, owner, Vid, Pid, NoKey, button: 4, action: 9, macroId: macroId);
		storage.SetKeyBinding(Guest, owner, Vid, Pid, NoKey, 0x04, 0x10, 0, 0x05);
		storage.SetKeyMacroBinding(Guest, owner, Vid, Pid, NoKey, 33, macroId);
		storage.ReplaceDeviceActuation(Guest, owner, Vid, Pid, NoKey,
			[new ActuationRecord(3, 150, 160, true, 20, 25)]);
		storage.ReplaceDeviceExtendedKeys(Guest, owner, Vid, Pid, NoKey,
			[new ExtendedKeyRecord(ExtendedKeyMode.ModTap, 30, ExtendedKeyRecord.NoSlot, 0, 150,
				new ExtendedKeyCodeRecord(0x10, 0x01, 0x00), new ExtendedKeyCodeRecord(0x10, 0x00, 0x29))]);
		storage.SavePresetSnapshot(presetId, Guest, owner, Vid, Pid, NoKey);

		var json = BuildJson(storage, presetId, new Dictionary<int, string> { [macroId] = macroUid });

		var rgb = json.GetProperty("rgb");
		Assert.True(rgb.GetProperty("is_enabled").GetBoolean());
		Assert.Equal("Neon", rgb.GetProperty("selected_effect").GetString());
		var effect = Assert.Single(rgb.GetProperty("effects").EnumerateArray());
		Assert.Equal("Neon", effect.GetProperty("effect_key").GetString());
		Assert.Equal("#00FF00", effect.GetProperty("hex").GetString());
		Assert.Equal(55, effect.GetProperty("brightness").GetDouble());
		Assert.Equal(33, effect.GetProperty("speed").GetDouble());
		Assert.False(effect.GetProperty("is_rainbow").GetBoolean());

		Assert.Equal(3, json.GetProperty("settings").GetProperty("dpi_stage").GetDouble());

		var actuation = Assert.Single(json.GetProperty("actuation").EnumerateArray());
		Assert.Equal(3, actuation.GetProperty("slot").GetInt32());
		Assert.Equal(150, actuation.GetProperty("press_centi_mm").GetInt32());
		Assert.Equal(160, actuation.GetProperty("release_centi_mm").GetInt32());
		Assert.True(actuation.GetProperty("rapid_trigger").GetBoolean());
		Assert.Equal(20, actuation.GetProperty("quick_press_centi_mm").GetInt32());
		Assert.Equal(25, actuation.GetProperty("quick_release_centi_mm").GetInt32());

		// One wide item per extended key, the two outputs as nested matrix-record objects.
		var extendedKey = Assert.Single(json.GetProperty("extended_keys").EnumerateArray());
		Assert.Equal(0x81, extendedKey.GetProperty("mode").GetInt32());
		Assert.Equal(30, extendedKey.GetProperty("slot1").GetInt32());
		Assert.Equal(-1, extendedKey.GetProperty("slot2").GetInt32());
		Assert.Equal(0, extendedKey.GetProperty("priority").GetInt32());
		Assert.Equal(150, extendedKey.GetProperty("time_ms").GetInt32());
		Assert.Equal(0x01, extendedKey.GetProperty("hold_key").GetProperty("code1").GetInt32());
		Assert.Equal(0x29, extendedKey.GetProperty("tap_key").GetProperty("code2").GetInt32());

		var keyBinding = Assert.Single(json.GetProperty("key_bindings").EnumerateArray());
		Assert.Equal(0x04, keyBinding.GetProperty("phys_scancode").GetInt32());
		Assert.Equal(0x10, keyBinding.GetProperty("type_byte").GetInt32());
		Assert.Equal(0, keyBinding.GetProperty("bitmask").GetInt32());
		Assert.Equal(0x05, keyBinding.GetProperty("target_code").GetInt32());

		// Macro references travel as uids — local integer ids mean nothing to the server.
		var buttonBinding = Assert.Single(json.GetProperty("button_bindings").EnumerateArray());
		Assert.Equal(4, buttonBinding.GetProperty("button").GetInt32());
		Assert.Equal(9, buttonBinding.GetProperty("action").GetInt32());
		Assert.Equal(macroUid, buttonBinding.GetProperty("macro_uid").GetString());

		var keyMacroBinding = Assert.Single(json.GetProperty("key_macro_bindings").EnumerateArray());
		Assert.Equal(33, keyMacroBinding.GetProperty("matrix_index").GetInt32());
		Assert.Equal(macroUid, keyMacroBinding.GetProperty("macro_uid").GetString());
	}

	// A Toggle record travels as the same wide item: time_ms plus the toggled output in hold_key, tap_key
	// all zero (docs/plans/add-extended-key-toggle).
	[Fact]
	public void Build_ToggleRecord_CarriesTimeAndTheToggledOutputInHoldKey()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "P" });
		storage.ReplaceDeviceExtendedKeys(Guest, owner, Vid, Pid, NoKey,
			[ExtendedKeyRecord.Toggle(20, 100, new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A))]);
		storage.SavePresetSnapshot(presetId, Guest, owner, Vid, Pid, NoKey);

		var json = BuildJson(storage, presetId);

		var extendedKey = Assert.Single(json.GetProperty("extended_keys").EnumerateArray());
		Assert.Equal(0x82, extendedKey.GetProperty("mode").GetInt32());
		Assert.Equal(20, extendedKey.GetProperty("slot1").GetInt32());
		Assert.Equal(-1, extendedKey.GetProperty("slot2").GetInt32());
		Assert.Equal(100, extendedKey.GetProperty("time_ms").GetInt32());
		Assert.Equal(0x10, extendedKey.GetProperty("hold_key").GetProperty("type_byte").GetInt32());
		Assert.Equal(0x1A, extendedKey.GetProperty("hold_key").GetProperty("code2").GetInt32());
		Assert.Equal(0, extendedKey.GetProperty("tap_key").GetProperty("type_byte").GetInt32());
		Assert.Equal(0, extendedKey.GetProperty("tap_key").GetProperty("code2").GetInt32());
	}

	// An SOCD pair carries its priority through the snapshot into the API shape — and as the
	// canonical row: picked R-then-E with "Key 1 wins", stored as (E, R) with "Key 2 wins".
	[Fact]
	public void Build_SocdPair_CarriesTheNormalisedPriority()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "P" });

		storage.ReplaceDeviceExtendedKeys(Guest, owner, Vid, Pid, NoKey,
			[ExtendedKeyRecord.Socd(19, 18, SocdPriority.Key1)]);
		storage.SavePresetSnapshot(presetId, Guest, owner, Vid, Pid, NoKey);

		var json = BuildJson(storage, presetId);

		var extendedKey = Assert.Single(json.GetProperty("extended_keys").EnumerateArray());
		Assert.Equal(0x83, extendedKey.GetProperty("mode").GetInt32());
		Assert.Equal(18, extendedKey.GetProperty("slot1").GetInt32());
		Assert.Equal(19, extendedKey.GetProperty("slot2").GetInt32());
		Assert.Equal(SocdPriority.Key2, extendedKey.GetProperty("priority").GetInt32());
		Assert.Equal(0, extendedKey.GetProperty("time_ms").GetInt32());
	}

	// A pair mode (RS / SOCD) links two physical keys and picks no output at all, so both nested
	// code objects travel as JSON null rather than as a zeroed object — the shape documented to the
	// backend team (docs/plans/add-extended-keys-api-sync). The single-key modes are the contrast:
	// Mod-Tap keeps BOTH objects, and Toggle keeps its all-zero tap_key, because there the zeros are
	// the record's real content and not "no opinion".
	[Fact]
	public void Build_PairModes_SerializeNullCodes_WhileSingleKeyModesKeepTheirObjects()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "P" });

		storage.ReplaceDeviceExtendedKeys(Guest, owner, Vid, Pid, NoKey,
			[ExtendedKeyRecord.RapidSnap(31, 33),
			 ExtendedKeyRecord.Socd(18, 19, SocdPriority.Key2),
			 ExtendedKeyRecord.ModTap(30, 150, new ExtendedKeyCodeRecord(0x10, 0x01, 0x00), new ExtendedKeyCodeRecord(0x10, 0x00, 0x29)),
			 ExtendedKeyRecord.Toggle(20, 100, new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A))]);
		storage.SavePresetSnapshot(presetId, Guest, owner, Vid, Pid, NoKey);

		var items = BuildJson(storage, presetId).GetProperty("extended_keys").EnumerateArray().ToList();
		Assert.Equal(4, items.Count);

		JsonElement ByMode(int mode) => items.Single(i => i.GetProperty("mode").GetInt32() == mode);

		foreach (var mode in new[] { ExtendedKeyMode.RapidSnap, ExtendedKeyMode.Socd })
		{
			var pair = ByMode(mode);
			Assert.Equal(JsonValueKind.Null, pair.GetProperty("hold_key").ValueKind);
			Assert.Equal(JsonValueKind.Null, pair.GetProperty("tap_key").ValueKind);
		}

		var modTap = ByMode(ExtendedKeyMode.ModTap);
		Assert.Equal(0x01, modTap.GetProperty("hold_key").GetProperty("code1").GetInt32());
		Assert.Equal(0x29, modTap.GetProperty("tap_key").GetProperty("code2").GetInt32());

		// Toggle's tap_key is all zeros but still an object: dropping it would lose the distinction
		// between "this mode has no second output" and "the pair modes have no output at all".
		var toggle = ByMode(ExtendedKeyMode.Toggle);
		Assert.Equal(0x1A, toggle.GetProperty("hold_key").GetProperty("code2").GetInt32());
		Assert.Equal(JsonValueKind.Object, toggle.GetProperty("tap_key").ValueKind);
		Assert.Equal(0, toggle.GetProperty("tap_key").GetProperty("type_byte").GetInt32());
	}

	// build → apply → build with the null pair codes in play: the applier reads a JSON null back as
	// ExtendedKeyCodeRecord.None, so the second build must be byte-identical to the first. This is the
	// check that keeps the null tidy-up from silently becoming a lossy one.
	[Fact]
	public void BuildApplyBuild_WithNullPairCodes_RoundTripsToIdenticalJson()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var source = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "src" });
		var target = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "dst" });

		storage.ReplaceDeviceExtendedKeys(Guest, owner, Vid, Pid, NoKey,
			[ExtendedKeyRecord.RapidSnap(31, 33),
			 ExtendedKeyRecord.Socd(18, 19, SocdPriority.Key1),
			 ExtendedKeyRecord.ModTap(30, 150, new ExtendedKeyCodeRecord(0x10, 0x01, 0x00), new ExtendedKeyCodeRecord(0x10, 0x00, 0x29)),
			 ExtendedKeyRecord.Toggle(20, 100, new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A))]);
		storage.SavePresetSnapshot(source, Guest, owner, Vid, Pid, NoKey);

		var builder = new PresetConfigurationBuilder(storage);
		var noMacros = new Dictionary<int, string>();
		var first = JsonSerializer.SerializeToElement(builder.Build(source, noMacros));
		new PresetConfigurationApplier(storage).Apply(target, first, new Dictionary<string, int>());
		var second = JsonSerializer.SerializeToElement(builder.Build(target, noMacros));

		Assert.Equal(first.GetProperty("extended_keys").GetRawText(), second.GetProperty("extended_keys").GetRawText());
		Assert.Equal(storage.GetPresetExtendedKeys(source), storage.GetPresetExtendedKeys(target));
		// And the pairs came back as "no output", not as something invented from the null.
		foreach (var pair in storage.GetPresetExtendedKeys(target).Where(r => r.IsPair))
		{
			Assert.Equal(ExtendedKeyCodeRecord.None, pair.HoldKey);
			Assert.Equal(ExtendedKeyCodeRecord.None, pair.TapKey);
		}
	}

	// A preset with no snapshot at all: every section is PRESENT and empty (arrays/objects,
	// not omitted keys), and the rgb head falls back to the table defaults.
	[Fact]
	public void Build_EmptySnapshot_EmitsEverySectionPresentAndEmpty()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "Empty" });

		var json = BuildJson(storage, presetId);

		var rgb = json.GetProperty("rgb");
		Assert.True(rgb.GetProperty("is_enabled").GetBoolean());
		Assert.Equal(string.Empty, rgb.GetProperty("selected_effect").GetString());
		Assert.Empty(rgb.GetProperty("effects").EnumerateArray());

		Assert.Equal(JsonValueKind.Object, json.GetProperty("settings").ValueKind);
		Assert.Empty(json.GetProperty("settings").EnumerateObject());

		Assert.Empty(json.GetProperty("actuation").EnumerateArray());
		Assert.Empty(json.GetProperty("extended_keys").EnumerateArray());
		Assert.Empty(json.GetProperty("key_bindings").EnumerateArray());
		Assert.Empty(json.GetProperty("button_bindings").EnumerateArray());
		Assert.Empty(json.GetProperty("key_macro_bindings").EnumerateArray());
	}

	// A binding without a macro, and one whose macro has no uid in the map (the snapshot can
	// outlive the macro it points at), both serialize an explicit null macro_uid rather than
	// a dangling reference.
	[Fact]
	public void Build_UnresolvableMacroReferences_SerializeNullMacroUid()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "Dangling" });

		storage.SetButtonBinding(Guest, owner, Vid, Pid, NoKey, button: 1, action: 7, macroId: null);
		storage.SetKeyMacroBinding(Guest, owner, Vid, Pid, NoKey, 33, 999); // macro long gone
		storage.SavePresetSnapshot(presetId, Guest, owner, Vid, Pid, NoKey);

		var json = BuildJson(storage, presetId);

		var buttonBinding = Assert.Single(json.GetProperty("button_bindings").EnumerateArray());
		Assert.Equal(JsonValueKind.Null, buttonBinding.GetProperty("macro_uid").ValueKind);

		var keyMacroBinding = Assert.Single(json.GetProperty("key_macro_bindings").EnumerateArray());
		Assert.Equal(JsonValueKind.Null, keyMacroBinding.GetProperty("macro_uid").ValueKind);
	}
}
