using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ProoveGaming.App.Models;
using ProoveGaming.App.Services;
using Xunit;

namespace ProoveGaming.App.Tests;

// PresetConfigurationApplier deserializes a pulled API v2.0 `configuration` object into a
// preset's snapshot tables (docs/plans/add-api-v2-pull) — the reverse of
// PresetConfigurationBuilder. The round-trip test (build → apply → build) is the strongest
// correctness check available without a server: it pins the two against each other, so any
// future configuration field added to one but not the other fails here.
public class PresetConfigurationApplierTests
{
	private const int Vid = 0x04D9;
	private const int Pid = 0xA23F;
	private const string NoKey = "";
	private const string Guest = LocalStorageService.OwnerKindGuest;

	private static JsonElement ToElement(object value) => JsonSerializer.SerializeToElement(value);

	private static readonly IReadOnlyDictionary<string, int> NoMacros = new Dictionary<string, int>();

	// ── Round-trip against the builder ───────────────────────────────────────

	[Fact]
	public void Apply_BuilderOutput_RoundTripsToIdenticalJson()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var sourceId = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "Source" });

		const int macroId = 77;
		const string macroUid = "11111111-2222-3333-4444-555555555555";

		// Every section populated through the same public API the app uses, exactly like the
		// builder's golden-path test.
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
			[ExtendedKeyRecord.RapidSnap(31, 33),
			 new ExtendedKeyRecord(ExtendedKeyMode.ModTap, 30, ExtendedKeyRecord.NoSlot, 0, 150,
				new ExtendedKeyCodeRecord(0x10, 0x01, 0x00), new ExtendedKeyCodeRecord(0x10, 0x00, 0x29))]);
		storage.SavePresetSnapshot(sourceId, Guest, owner, Vid, Pid, NoKey);

		var builder = new PresetConfigurationBuilder(storage);
		var macroUidsById = new Dictionary<int, string> { [macroId] = macroUid };
		var first = JsonSerializer.Serialize(builder.Build(sourceId, macroUidsById));

		// Apply the serialized configuration into a fresh preset, resolving macro uids with
		// the inverse map, then build again from the fresh preset's snapshot.
		var targetId = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "Target" });
		new PresetConfigurationApplier(storage).Apply(
			targetId,
			JsonSerializer.Deserialize<JsonElement>(first),
			new Dictionary<string, int> { [macroUid] = macroId });
		var second = JsonSerializer.Serialize(builder.Build(targetId, macroUidsById));

		Assert.Equal(first, second);
	}

	// ── Unknown-key tolerance ────────────────────────────────────────────────

	// Forward compatibility: unknown keys at every level (top-level section, inside rgb,
	// inside an item) are ignored while every known value still lands.
	[Fact]
	public void Apply_UnknownJsonKeys_AreIgnoredWhileKnownValuesApply()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "P" });

		var configuration = ToElement(new
		{
			schema_version = 3,
			future_section = new object[] { new { whatever = true } },
			rgb = new
			{
				is_enabled = false,
				selected_effect = "Wave",
				glow_radius = 4,
				effects = new object[]
				{
					new { effect_key = "Wave", hex = "#112233", brightness = 10d, speed = 20d, is_rainbow = true, sparkle = "yes" },
				},
			},
			settings = new Dictionary<string, double> { ["dpi_stage"] = 2 },
			key_bindings = new object[]
			{
				new { phys_scancode = 4, type_byte = 16, bitmask = 0, target_code = 5, comment = "?" },
			},
		});

		new PresetConfigurationApplier(storage).Apply(presetId, configuration, NoMacros);

		var rgb = storage.GetPresetRgbState(presetId);
		Assert.NotNull(rgb);
		Assert.False(rgb!.IsEnabled);
		Assert.Equal("Wave", rgb.SelectedEffect);
		var effect = Assert.Single(rgb.Effects);
		Assert.Equal("#112233", effect.Hex);
		Assert.True(effect.IsRainbow);
		Assert.Equal(2d, storage.GetPresetSettings(presetId)["dpi_stage"]);
		var keyBinding = Assert.Single(storage.GetPresetKeyBindings(presetId));
		Assert.Equal((byte)0x04, keyBinding.PhysScancode);
		Assert.Equal((byte)0x05, keyBinding.TargetCode);
	}

	// ── Macro-uid resolution ─────────────────────────────────────────────────

	// A null macro_uid and one that resolves to no local macro both store the button binding
	// without a macro (the builder's explicit-null stance, reversed). Key-macro bindings have
	// a NOT NULL macro_id, so an unresolvable one has nothing to store and drops.
	[Fact]
	public void Apply_NullOrUnresolvableMacroUid_StoresBindingWithoutMacro()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "P" });

		var configuration = ToElement(new
		{
			button_bindings = new object[]
			{
				new { button = 1, action = 7, macro_uid = (string?)null },
				new { button = 2, action = 9, macro_uid = "not-a-known-uid" },
			},
			key_macro_bindings = new object[]
			{
				new { matrix_index = 33, macro_uid = "not-a-known-uid" },
			},
		});

		new PresetConfigurationApplier(storage).Apply(presetId, configuration, NoMacros);

		var buttonBindings = storage.GetPresetButtonBindings(presetId);
		Assert.Equal(2, buttonBindings.Count);
		Assert.All(buttonBindings, b => Assert.Null(b.MacroId));
		Assert.Empty(storage.GetPresetKeyMacroBindings(presetId));
	}

	// ── Malformed-section tolerance ──────────────────────────────────────────

	// A section of the wrong JSON kind (a string or number where an array is expected) is
	// skipped while every well-formed section still applies.
	[Fact]
	public void Apply_MalformedSection_SkipsItAndAppliesTheRest()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "P" });

		var configuration = ToElement(new
		{
			rgb = new { is_enabled = true, selected_effect = "Neon", effects = Array.Empty<object>() },
			settings = new Dictionary<string, double> { ["dpi_stage"] = 1 },
			actuation = "totally-not-an-array",
			extended_keys = "nope",
			key_bindings = 42,
			button_bindings = new object[] { new { button = 4, action = 9, macro_uid = (string?)null } },
		});

		new PresetConfigurationApplier(storage).Apply(presetId, configuration, NoMacros);

		Assert.Empty(storage.GetPresetActuation(presetId));
		Assert.Empty(storage.GetPresetExtendedKeys(presetId));
		Assert.Empty(storage.GetPresetKeyBindings(presetId));
		// The well-formed sections around the malformed ones all landed.
		var rgb = storage.GetPresetRgbState(presetId);
		Assert.NotNull(rgb);
		Assert.Equal("Neon", rgb!.SelectedEffect);
		Assert.Equal(1d, storage.GetPresetSettings(presetId)["dpi_stage"]);
		var buttonBinding = Assert.Single(storage.GetPresetButtonBindings(presetId));
		Assert.Equal(4, buttonBinding.Button);
		Assert.Null(buttonBinding.MacroId);
	}
}

public class PresetConfigurationApplierExtendedKeysTests
{
	private const int Vid = 0x28E9;
	private const int Pid = 0x3272;
	private const string Guest = LocalStorageService.OwnerKindGuest;
	private static readonly IReadOnlyDictionary<string, int> NoMacros = new Dictionary<string, int>();

	// A pulled extended_keys array: a malformed item (no slot1) is skipped, an item on a slot the
	// normalised pair before it already owns is dropped, null / missing nested code objects read as
	// "none" on a pair, and the pair is normalised.
	[Fact]
	public void Apply_ExtendedKeys_SkipsMalformedItems_ToleratesNullCodes_NormalisesPairs()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "P" });

		var configuration = JsonSerializer.SerializeToElement(new
		{
			extended_keys = new object[]
			{
				new { mode = 0x84, slot1 = 33, slot2 = 31, hold_key = (object?)null },
				new { mode = 0x84, slot2 = 5 },                              // no slot1 — skipped
				// Well-formed on its own, but slot 33 is the second slot of the normalised pair above
				// (33 → slot2), and a key belongs to at most one record — dropped. Review B1: it is
				// NOT the raw slot1 that reserves a slot, so this item may not be asserted as a
				// "duplicate slot1"; the pair owns both of its slots.
				new { mode = 0x82, slot1 = 33, time_ms = 100, hold_key = new { type_byte = 0x10, code1 = 0, code2 = 0x1A } },
				new { mode = 0x81, slot1 = 30, time_ms = 150, hold_key = new { type_byte = 0x10, code1 = 1, code2 = 0 }, tap_key = new { type_byte = 0x10, code1 = 0, code2 = 0x29 } },
			},
		});

		new PresetConfigurationApplier(storage).Apply(presetId, configuration, NoMacros);

		var rows = storage.GetPresetExtendedKeys(presetId);
		Assert.Equal(2, rows.Count);
		var pair = rows.Single(r => r.Mode == ExtendedKeyMode.RapidSnap);
		Assert.Equal(31, pair.Slot1);
		Assert.Equal(33, pair.Slot2);
		Assert.Equal(ExtendedKeyCodeRecord.None, pair.HoldKey);
		Assert.Equal(ExtendedKeyCodeRecord.None, pair.TapKey);
		var modTap = rows.Single(r => r.Mode == ExtendedKeyMode.ModTap);
		Assert.Equal(ExtendedKeyRecord.NoSlot, modTap.Slot2);
		Assert.Equal(150, modTap.TimeMs);
		Assert.Equal(new ExtendedKeyCodeRecord(0x10, 1, 0), modTap.HoldKey);
		Assert.Equal(new ExtendedKeyCodeRecord(0x10, 0, 0x29), modTap.TapKey);
	}

	// A pulled SOCD pair keeps its priority; when the server's slots arrive in the other order
	// the absolute priority follows the swap, so the same physical key still wins. A missing
	// priority reads as last input (0).
	[Fact]
	public void Apply_SocdPair_KeepsThePriority_FlippingItWithTheSlotSwap()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "P" });

		var configuration = JsonSerializer.SerializeToElement(new
		{
			extended_keys = new object[]
			{
				new { mode = 0x83, slot1 = 19, slot2 = 18, priority = 2 },   // "Key 1 (19) wins" → (18, 19) with 3
				new { mode = 0x83, slot1 = 31, slot2 = 33, priority = 1 },
				new { mode = 0x83, slot1 = 40, slot2 = 41 },                 // no priority → 0
			},
		});

		new PresetConfigurationApplier(storage).Apply(presetId, configuration, NoMacros);

		var rows = storage.GetPresetExtendedKeys(presetId);
		Assert.Equal(3, rows.Count);
		Assert.Contains(new ExtendedKeyRecord(ExtendedKeyMode.Socd, 18, 19, SocdPriority.Key2, 0, ExtendedKeyCodeRecord.None, ExtendedKeyCodeRecord.None), rows);
		Assert.Contains(ExtendedKeyRecord.Socd(31, 33, SocdPriority.MutualCancel), rows);
		Assert.Contains(ExtendedKeyRecord.Socd(40, 41, SocdPriority.LastInput), rows);
	}

	// Review S1: an SOCD priority the firmware does not define (outside 0..3) is refused at intake like
	// a malformed item — stored, it would make the host refuse the preset's whole set. The RANGE check
	// is SOCD-only, because only SOCD's record has a nibble to carry a priority at all; a Rapid Snap
	// item carrying one is refused outright by the separate S11 guard, not by this range.
	[Fact]
	public void Apply_SocdPriorityOutsideTheFirmwareRange_SkipsOnlyThatItem()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "P" });

		var configuration = JsonSerializer.SerializeToElement(new
		{
			extended_keys = new object[]
			{
				new { mode = 0x83, slot1 = 18, slot2 = 19, priority = 4 },    // skipped
				new { mode = 0x83, slot1 = 20, slot2 = 21, priority = -1 },   // skipped
				new { mode = 0x83, slot1 = 31, slot2 = 33, priority = 1 },    // kept
				new { mode = 0x84, slot1 = 40, slot2 = 41 },                  // RS, no priority — kept
			},
		});

		new PresetConfigurationApplier(storage).Apply(presetId, configuration, NoMacros);

		var rows = storage.GetPresetExtendedKeys(presetId);
		Assert.Equal(2, rows.Count);
		Assert.Contains(ExtendedKeyRecord.Socd(31, 33, SocdPriority.MutualCancel), rows);
		var pair = rows.Single(r => r.Mode == ExtendedKeyMode.RapidSnap);
		Assert.Equal((40, 41), (pair.Slot1, pair.Slot2));
		Assert.DoesNotContain(rows, r => r.Slot1 is 18 or 20);
	}

	// Review S1 (MT): an MT item the host would refuse — a threshold off the 0..1000 slider, a missing
	// output, or an output that is not a keyboard record — is skipped at intake like a malformed item,
	// so one bad row cannot block a preset's whole set on the next push. These RULES are MT-only, since
	// only MT's record carries a time and two outputs; a pair carrying either is refused outright by
	// the separate S11 guard rather than judged against the slider.
	[Fact]
	public void Apply_ModTapOutsideTheFirmwareRules_SkipsOnlyThoseItems()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "P" });

		var hold = new { type_byte = 0x10, code1 = 1, code2 = 0 };
		var tap = new { type_byte = 0x10, code1 = 0, code2 = 0x29 };
		var configuration = JsonSerializer.SerializeToElement(new
		{
			extended_keys = new object[]
			{
				new { mode = 0x81, slot1 = 10, time_ms = 1001, hold_key = hold, tap_key = tap },                          // time past the slider — skipped
				new { mode = 0x81, slot1 = 11, time_ms = -1, hold_key = hold, tap_key = tap },                            // negative time — skipped
				new { mode = 0x81, slot1 = 12, time_ms = 150, hold_key = hold },                                          // no tap output — skipped
				new { mode = 0x81, slot1 = 13, time_ms = 150, hold_key = new { type_byte = 0x20, code1 = 0xE9, code2 = 0 }, tap_key = tap }, // consumer command — skipped
				new { mode = 0x81, slot1 = 30, time_ms = 150, hold_key = hold, tap_key = tap },                           // kept
				new { mode = 0x84, slot1 = 40, slot2 = 41 },                                                              // RS, no time — kept
			},
		});

		new PresetConfigurationApplier(storage).Apply(presetId, configuration, NoMacros);

		var rows = storage.GetPresetExtendedKeys(presetId);
		Assert.Equal(2, rows.Count);
		Assert.Contains(ExtendedKeyRecord.ModTap(30, 150, new ExtendedKeyCodeRecord(0x10, 1, 0), new ExtendedKeyCodeRecord(0x10, 0, 0x29)), rows);
		var pair = rows.Single(r => r.Mode == ExtendedKeyMode.RapidSnap);
		Assert.Equal((40, 41), (pair.Slot1, pair.Slot2));
		Assert.DoesNotContain(rows, r => r.Slot1 is 10 or 11 or 12 or 13);
	}

	// The same intake rule for a Toggle item (docs/plans/add-extended-key-toggle): a threshold off the
	// slider, a missing / non-keyboard hold_key (the toggled output), or a tap_key — which the host refuses
	// on this mode — is skipped; the well-formed neighbours are stored.
	[Fact]
	public void Apply_ToggleOutsideTheFirmwareRules_SkipsOnlyThoseItems()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "P" });

		var w = new { type_byte = 0x10, code1 = 0, code2 = 0x1A };
		var configuration = JsonSerializer.SerializeToElement(new
		{
			extended_keys = new object[]
			{
				new { mode = 0x82, slot1 = 10, time_ms = 1001, hold_key = w },                                       // time past the slider — skipped
				new { mode = 0x82, slot1 = 11, time_ms = -1, hold_key = w },                                         // negative time — skipped
				new { mode = 0x82, slot1 = 12, time_ms = 100 },                                                      // no output — skipped
				new { mode = 0x82, slot1 = 13, time_ms = 100, hold_key = new { type_byte = 0x20, code1 = 0xE9, code2 = 0 } }, // consumer command — skipped
				new { mode = 0x82, slot1 = 14, time_ms = 100, hold_key = w, tap_key = new { type_byte = 0x10, code1 = 0, code2 = 0x29 } }, // a tap on a Toggle — skipped
				new { mode = 0x82, slot1 = 20, time_ms = 100, hold_key = w },                                        // kept
				new { mode = 0x82, slot1 = 21, time_ms = 0, hold_key = w, tap_key = (object?)null },                 // kept: null tap is "none"
			},
		});

		new PresetConfigurationApplier(storage).Apply(presetId, configuration, NoMacros);

		var rows = storage.GetPresetExtendedKeys(presetId);
		Assert.Equal(2, rows.Count);
		Assert.Contains(ExtendedKeyRecord.Toggle(20, 100, new ExtendedKeyCodeRecord(0x10, 0, 0x1A)), rows);
		Assert.Contains(ExtendedKeyRecord.Toggle(21, 0, new ExtendedKeyCodeRecord(0x10, 0, 0x1A)), rows);
	}

	// Review S1 (TGL round): a single-key item — Toggle or Mod-Tap — carrying a second slot or a priority
	// is refused by the host ("carries no second slot and no priority"), so intake skips it like the other
	// malformed shapes; the pair modes keep their slot2 as before.
	[Fact]
	public void Apply_SingleKeyItemWithASecondSlotOrAPriority_SkipsOnlyThoseItems()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "P" });

		var w = new { type_byte = 0x10, code1 = 0, code2 = 0x1A };
		var hold = new { type_byte = 0x10, code1 = 1, code2 = 0 };
		var tap = new { type_byte = 0x10, code1 = 0, code2 = 0x29 };
		var configuration = JsonSerializer.SerializeToElement(new
		{
			extended_keys = new object[]
			{
				new { mode = 0x82, slot1 = 10, slot2 = 5, time_ms = 100, hold_key = w },                       // Toggle with a second slot — skipped
				new { mode = 0x82, slot1 = 11, priority = 2, time_ms = 100, hold_key = w },                    // Toggle with a priority — skipped
				new { mode = 0x81, slot1 = 12, slot2 = 6, time_ms = 150, hold_key = hold, tap_key = tap },     // MT with a second slot — skipped
				new { mode = 0x82, slot1 = 20, slot2 = -1, priority = 0, time_ms = 100, hold_key = w },        // kept: explicit "none" values
				new { mode = 0x81, slot1 = 30, time_ms = 150, hold_key = hold, tap_key = tap },                // kept
				new { mode = 0x84, slot1 = 40, slot2 = 41 },                                                   // RS keeps its slot2
			},
		});

		new PresetConfigurationApplier(storage).Apply(presetId, configuration, NoMacros);

		var rows = storage.GetPresetExtendedKeys(presetId);
		Assert.Equal(3, rows.Count);
		Assert.Contains(ExtendedKeyRecord.Toggle(20, 100, new ExtendedKeyCodeRecord(0x10, 0, 0x1A)), rows);
		Assert.Contains(ExtendedKeyRecord.ModTap(30, 150, new ExtendedKeyCodeRecord(0x10, 1, 0), new ExtendedKeyCodeRecord(0x10, 0, 0x29)), rows);
		Assert.Contains(ExtendedKeyRecord.RapidSnap(40, 41), rows);
		Assert.DoesNotContain(rows, r => r.Slot1 is 10 or 11 or 12);
	}

	// Review S11: a pair record is two slots and nothing else, so a Rapid Snap or SOCD item that also
	// carries a time or an output is refused by the host (IsPlainPair) — and SetExtendedKeys is a
	// whole-set request, so one stored row of that shape blocks the user's real records permanently and
	// invisibly, exactly as a bad slot does (review B2). The item is dropped alone; the single-key modes,
	// whose records DO carry a time and outputs, keep theirs.
	[Fact]
	public void Apply_PairItemCarryingSingleKeyFields_SkipsOnlyThoseItems()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "P" });

		var w = new { type_byte = 0x10, code1 = 0, code2 = 0x1A };
		var hold = new { type_byte = 0x10, code1 = 1, code2 = 0 };
		var tap = new { type_byte = 0x10, code1 = 0, code2 = 0x29 };
		var configuration = JsonSerializer.SerializeToElement(new
		{
			extended_keys = new object[]
			{
				new { mode = 0x84, slot1 = 10, slot2 = 11, time_ms = 150 },                                 // RS with a time — skipped
				new { mode = 0x84, slot1 = 12, slot2 = 13, hold_key = hold },                                // RS with an output — skipped
				new { mode = 0x83, slot1 = 14, slot2 = 15, priority = 1, tap_key = tap },                    // SOCD with a tap output — skipped
				new { mode = 0x83, slot1 = 16, slot2 = 17, priority = 2, time_ms = 100, hold_key = hold },   // SOCD with both — skipped
				new { mode = 0x84, slot1 = 40, slot2 = 41 },                                                 // kept
				new { mode = 0x83, slot1 = 31, slot2 = 33, priority = 1 },                                   // kept
				new { mode = 0x81, slot1 = 30, time_ms = 150, hold_key = hold, tap_key = tap },              // kept: MT's time and outputs are its own
				new { mode = 0x82, slot1 = 20, time_ms = 100, hold_key = w },                                // kept: and so are Toggle's
			},
		});

		new PresetConfigurationApplier(storage).Apply(presetId, configuration, NoMacros);

		var rows = storage.GetPresetExtendedKeys(presetId);
		Assert.Equal(4, rows.Count);
		Assert.Contains(ExtendedKeyRecord.RapidSnap(40, 41), rows);
		Assert.Contains(ExtendedKeyRecord.Socd(31, 33, SocdPriority.MutualCancel), rows);
		Assert.Contains(ExtendedKeyRecord.ModTap(30, 150, new ExtendedKeyCodeRecord(0x10, 1, 0), new ExtendedKeyCodeRecord(0x10, 0, 0x29)), rows);
		Assert.Contains(ExtendedKeyRecord.Toggle(20, 100, new ExtendedKeyCodeRecord(0x10, 0, 0x1A)), rows);
		Assert.DoesNotContain(rows, r => r.ContainsSlot(10) || r.ContainsSlot(12) || r.ContainsSlot(14) || r.ContainsSlot(16));
	}

	// Review S11, the half that is easy to get wrong: the host allows a priority on SOCD, whose LEN nibble
	// carries one, and refuses one on Rapid Snap, whose record has nowhere to put it (IsPlainPair's
	// priorityAllowed). So the SAME value — one well inside SOCD's 0..3 range — has to drop the Rapid Snap
	// item and stay on the SOCD one. Judging the priority by range alone would keep both; treating it as
	// foreign on every pair mode would drop both.
	[Fact]
	public void Apply_RapidSnapCarryingAnSocdValidPriority_IsSkippedWhileTheSocdPairKeepsIt()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "P" });

		var configuration = JsonSerializer.SerializeToElement(new
		{
			extended_keys = new object[]
			{
				new { mode = 0x84, slot1 = 10, slot2 = 11, priority = 1 },   // RS: a rule priority SOCD accepts — still foreign here, skipped
				new { mode = 0x84, slot1 = 12, slot2 = 13, priority = 3 },   // RS: an absolute one, likewise — skipped
				new { mode = 0x83, slot1 = 31, slot2 = 33, priority = 1 },   // kept: the same value, on the mode that carries it
				new { mode = 0x83, slot1 = 18, slot2 = 19, priority = 3 },   // kept
				new { mode = 0x84, slot1 = 40, slot2 = 41, priority = 0 },   // kept: an explicit "no priority" is not a carried field
			},
		});

		new PresetConfigurationApplier(storage).Apply(presetId, configuration, NoMacros);

		var rows = storage.GetPresetExtendedKeys(presetId);
		Assert.Equal(3, rows.Count);
		Assert.Contains(ExtendedKeyRecord.Socd(31, 33, SocdPriority.MutualCancel), rows);
		Assert.Contains(ExtendedKeyRecord.Socd(18, 19, SocdPriority.Key2), rows);
		Assert.Contains(ExtendedKeyRecord.RapidSnap(40, 41), rows);
		Assert.DoesNotContain(rows, r => r.ContainsSlot(10) || r.ContainsSlot(12));
	}

	// Review S11 (the coverage it displaced): the two tests above used to carry a Rapid Snap row with a
	// stray priority / time_ms as the control proving the SOCD range and the Mod-Tap time-and-output rules
	// are judged PER MODE rather than on every item. S11 now drops such a row for carrying the field at
	// all, so it can no longer tell a scoped rule from an unscoped one, and the controls had to go.
	// What still discriminates is the other single-key mode: a well-formed Toggle has no tap output, so it
	// dies the moment MT's "two keyboard outputs" rule stops being MT-only, and a well-formed Mod-Tap has
	// one, so it dies the moment Toggle's "no tap output" rule stops being Toggle-only. The pair rows pin
	// the far end — carrying no time and no outputs, they reach neither rule and are kept — while the
	// out-of-range SOCD shows the range itself still bites on the one mode that owns a priority.
	[Fact]
	public void Apply_TheSingleKeyRulesAreScopedToTheirOwnMode_AndThePairsNeverReachThem()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "P" });

		var w = new { type_byte = 0x10, code1 = 0, code2 = 0x1A };
		var hold = new { type_byte = 0x10, code1 = 1, code2 = 0 };
		var tap = new { type_byte = 0x10, code1 = 0, code2 = 0x29 };
		var configuration = JsonSerializer.SerializeToElement(new
		{
			extended_keys = new object[]
			{
				new { mode = 0x81, slot1 = 30, time_ms = 150, hold_key = hold, tap_key = tap },   // MT: two outputs — kept while Toggle's "no tap" rule stays Toggle-only
				new { mode = 0x82, slot1 = 20, time_ms = 100, hold_key = w },                     // TGL: one output — kept while MT's "two outputs" rule stays MT-only
				new { mode = 0x84, slot1 = 40, slot2 = 41 },                                      // RS: no time, no outputs — judged by neither rule
				new { mode = 0x83, slot1 = 31, slot2 = 33, priority = 1 },                        // SOCD: the one mode whose priority the range judges
				new { mode = 0x83, slot1 = 18, slot2 = 19, priority = 4 },                        // …and it still bites — skipped
			},
		});

		new PresetConfigurationApplier(storage).Apply(presetId, configuration, NoMacros);

		var rows = storage.GetPresetExtendedKeys(presetId);
		Assert.Equal(4, rows.Count);
		Assert.Contains(ExtendedKeyRecord.ModTap(30, 150, new ExtendedKeyCodeRecord(0x10, 1, 0), new ExtendedKeyCodeRecord(0x10, 0, 0x29)), rows);
		Assert.Contains(ExtendedKeyRecord.Toggle(20, 100, new ExtendedKeyCodeRecord(0x10, 0, 0x1A)), rows);
		Assert.Contains(ExtendedKeyRecord.RapidSnap(40, 41), rows);
		Assert.Contains(ExtendedKeyRecord.Socd(31, 33, SocdPriority.MutualCancel), rows);
		Assert.DoesNotContain(rows, r => r.ContainsSlot(18));
	}

	// Review B1: the de-dupe runs on the NORMALISED record and reserves both of a pair's slots.
	// The reproduced payload — a reversed pair followed by one that normalises onto its lower slot —
	// used to pass the raw-slot1 guard and then throw UNIQUE constraint failed out of the insert,
	// which escapes the applier and truncates the rest of the sign-in pull. It must now be a plain
	// skip, like every other malformed item, and the surviving pair must still be normalised.
	[Fact]
	public void Apply_ReversedPairsCollidingAfterNormalisation_AreSkippedRatherThanFaultingTheInsert()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "P" });

		var configuration = JsonSerializer.SerializeToElement(new
		{
			extended_keys = new object[]
			{
				new { mode = 0x84, slot1 = 20, slot2 = 5 },   // reversed → (5, 20)
				new { mode = 0x84, slot1 = 5, slot2 = 30 },   // would key on 5 too — dropped
				new { mode = 0x83, slot1 = 50, slot2 = 20, priority = 2 }, // reversed onto 20, already owned — dropped
				new { mode = 0x83, slot1 = 41, slot2 = 40, priority = 2 }, // reversed → (40, 41), priority follows
			},
		});

		new PresetConfigurationApplier(storage).Apply(presetId, configuration, NoMacros);

		var rows = storage.GetPresetExtendedKeys(presetId);
		Assert.Equal(2, rows.Count);
		Assert.Contains(ExtendedKeyRecord.RapidSnap(5, 20), rows);
		Assert.Contains(new ExtendedKeyRecord(ExtendedKeyMode.Socd, 40, 41, SocdPriority.Key2, 0, ExtendedKeyCodeRecord.None, ExtendedKeyCodeRecord.None), rows);
		Assert.DoesNotContain(rows, r => r.ContainsSlot(30) || r.ContainsSlot(50));
	}

	// Review B2: a pair item needs two real, distinct keys and the mode must be one the firmware
	// defines. SetExtendedKeys is a whole-set request, so a single stored row of any of these shapes
	// makes the host refuse the user's real records on every later push — invisibly and permanently,
	// because a preset-driven push does not persist the refusal. Each is skipped at intake instead.
	[Fact]
	public void Apply_PairItemsWithoutTwoRealKeys_AndUnknownModes_AreSkipped()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "P" });

		var w = new { type_byte = 0x10, code1 = 0, code2 = 0x1A };
		var configuration = JsonSerializer.SerializeToElement(new
		{
			extended_keys = new object[]
			{
				new { mode = 0x84, slot1 = 7 },                              // RS with no second slot — skipped
				new { mode = 0x84, slot1 = 8, slot2 = -1 },                  // RS with an explicit "none" — skipped
				new { mode = 0x84, slot1 = 9, slot2 = -4 },                  // RS with a negative second slot — skipped
				new { mode = 0x83, slot1 = 10, slot2 = 10, priority = 0 },   // SOCD paired with itself — skipped
				new { mode = 0x83, slot1 = -3, slot2 = 11, priority = 0 },   // negative first slot — skipped
				new { mode = 0x82, slot1 = -4, time_ms = 100, hold_key = w },// negative slot on a single-key mode — skipped
				new { mode = 0x85, slot1 = 12, slot2 = 13 },                 // unknown mode — skipped
				new { mode = 0, slot1 = 14 },                                // unknown mode — skipped
				new { mode = 0x84, slot1 = 40, slot2 = 41 },                 // kept
			},
		});

		new PresetConfigurationApplier(storage).Apply(presetId, configuration, NoMacros);

		var row = Assert.Single(storage.GetPresetExtendedKeys(presetId));
		Assert.Equal(ExtendedKeyRecord.RapidSnap(40, 41), row);
	}

	// Review round 4, m1: the intake mirrored the host's rules for an entry's SLOTS and its mode's
	// field set, but never for the OUTPUT itself. Four shapes got through and each one poisons the
	// whole set, because SetExtendedKeys is a single whole-set request:
	//
	//   {0x10, 0, 0} — a keyboard record naming no key. It is not "none", so the old check passed it;
	//     the host's KeyCodeFromRecord returns null for it and refuses the entry, so every real record
	//     the user configured is refused too, on every push and every self-heal, with a banner on
	//     every open. (The `IsNone` sentinel the pair modes carry is a different value and stays
	//     legal — see the pair cases above, which are unaffected.)
	//   a code above 255 / below 0 — outside the byte the wire format has, refused by the host's
	//     TryFromProto before it ever reaches the codec.
	//   a non-zero modifier mask AND a non-zero usage together — two keys where the format has room
	//     for one. The host used to encode that usage-only and then VERIFY it, so the read-back could
	//     never equal the stored row: the self-heal rewrote the flash on every single page open
	//     without ever converging. It now refuses it outright, which is this shape's real cost.
	//
	// One item per shape per single-key mode, so a rule that silently lost one mode is visible, and a
	// real record of each mode alongside them — the assertion that matters is that only the bad item
	// is dropped, not the set.
	[Fact]
	public void Apply_OutputShapesTheHostRefuses_SkipOnlyThoseItems()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var presetId = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "P" });

		var hold = new { type_byte = 0x10, code1 = 1, code2 = 0 };
		var tap = new { type_byte = 0x10, code1 = 0, code2 = 0x29 };
		var w = new { type_byte = 0x10, code1 = 0, code2 = 0x1A };
		var configuration = JsonSerializer.SerializeToElement(new
		{
			extended_keys = new object[]
			{
				// A keyboard record naming no key, on each of the two output positions and on Toggle's.
				new { mode = 0x81, slot1 = 10, time_ms = 150, hold_key = new { type_byte = 0x10, code1 = 0, code2 = 0 }, tap_key = tap },
				new { mode = 0x81, slot1 = 11, time_ms = 150, hold_key = hold, tap_key = new { type_byte = 0x10, code1 = 0, code2 = 0 } },
				new { mode = 0x82, slot1 = 12, time_ms = 100, hold_key = new { type_byte = 0x10, code1 = 0, code2 = 0 } },
				// Past the byte the wire format has room for — the host's own range check.
				new { mode = 0x81, slot1 = 13, time_ms = 150, hold_key = hold, tap_key = new { type_byte = 0x10, code1 = 0, code2 = 256 } },
				new { mode = 0x82, slot1 = 14, time_ms = 100, hold_key = new { type_byte = 0x10, code1 = 256, code2 = 0 } },
				// ...and below it.
				new { mode = 0x81, slot1 = 15, time_ms = 150, hold_key = new { type_byte = 0x10, code1 = -1, code2 = 0 }, tap_key = tap },
				new { mode = 0x82, slot1 = 16, time_ms = 100, hold_key = new { type_byte = 0x10, code1 = 0, code2 = -1 } },
				// Ctrl+C: a modifier mask and a usage in one output. Two keys, one slot in the format.
				new { mode = 0x81, slot1 = 17, time_ms = 150, hold_key = new { type_byte = 0x10, code1 = 0x01, code2 = 0x06 }, tap_key = tap },
				new { mode = 0x82, slot1 = 18, time_ms = 100, hold_key = new { type_byte = 0x10, code1 = 0x01, code2 = 0x06 } },
				// The records a real user has, which must survive every one of the above.
				new { mode = 0x81, slot1 = 30, time_ms = 150, hold_key = hold, tap_key = tap },   // kept
				new { mode = 0x82, slot1 = 20, time_ms = 100, hold_key = w },                     // kept
				new { mode = 0x84, slot1 = 40, slot2 = 41 },                                      // kept
			},
		});

		new PresetConfigurationApplier(storage).Apply(presetId, configuration, NoMacros);

		var rows = storage.GetPresetExtendedKeys(presetId);
		Assert.Equal(3, rows.Count);
		Assert.Contains(ExtendedKeyRecord.ModTap(30, 150, new ExtendedKeyCodeRecord(0x10, 1, 0), new ExtendedKeyCodeRecord(0x10, 0, 0x29)), rows);
		Assert.Contains(ExtendedKeyRecord.Toggle(20, 100, new ExtendedKeyCodeRecord(0x10, 0, 0x1A)), rows);
		Assert.Contains(ExtendedKeyRecord.RapidSnap(40, 41), rows);
		Assert.DoesNotContain(rows, r => r.Slot1 is >= 10 and <= 18);
	}

	// build → apply → build: the SOCD priority is stable across the whole API round trip.
	[Fact]
	public void BuildApplyBuild_RoundTripsAnSocdPriority()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var source = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "src" });
		var target = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "dst" });
		storage.ReplaceDeviceExtendedKeys(Guest, owner, Vid, Pid, "",
			[ExtendedKeyRecord.Socd(18, 19, SocdPriority.Key2), ExtendedKeyRecord.RapidSnap(31, 33)]);
		storage.SavePresetSnapshot(source, Guest, owner, Vid, Pid, "");

		var builder = new PresetConfigurationBuilder(storage);
		var first = JsonSerializer.SerializeToElement(builder.Build(source, new Dictionary<int, string>()));
		new PresetConfigurationApplier(storage).Apply(target, first, NoMacros);
		var second = JsonSerializer.SerializeToElement(builder.Build(target, new Dictionary<int, string>()));

		Assert.Equal(first.GetProperty("extended_keys").GetRawText(), second.GetProperty("extended_keys").GetRawText());
		Assert.Equal(storage.GetPresetExtendedKeys(source), storage.GetPresetExtendedKeys(target));
		Assert.Equal(SocdPriority.Key2, storage.GetPresetExtendedKeys(target).Single(r => r.Mode == ExtendedKeyMode.Socd).Priority);
	}

	// build → apply → build: a Mod-Tap record's time and both outputs are stable across the API round
	// trip next to the pair modes (docs/plans/add-extended-key-mod-tap).
	[Fact]
	public void BuildApplyBuild_RoundTripsAModTapRecordWithItsTimeAndOutputs()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var source = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "src" });
		var target = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "dst" });
		var modTap = ExtendedKeyRecord.ModTap(30, 261, new ExtendedKeyCodeRecord(0x10, 0x01, 0x00), new ExtendedKeyCodeRecord(0x10, 0x00, 0x29));
		storage.ReplaceDeviceExtendedKeys(Guest, owner, Vid, Pid, "",
			[modTap, ExtendedKeyRecord.Socd(18, 19, SocdPriority.MutualCancel), ExtendedKeyRecord.RapidSnap(31, 33)]);
		storage.SavePresetSnapshot(source, Guest, owner, Vid, Pid, "");

		var builder = new PresetConfigurationBuilder(storage);
		var first = JsonSerializer.SerializeToElement(builder.Build(source, new Dictionary<int, string>()));
		new PresetConfigurationApplier(storage).Apply(target, first, NoMacros);
		var second = JsonSerializer.SerializeToElement(builder.Build(target, new Dictionary<int, string>()));

		Assert.Equal(first.GetProperty("extended_keys").GetRawText(), second.GetProperty("extended_keys").GetRawText());
		Assert.Equal(storage.GetPresetExtendedKeys(source), storage.GetPresetExtendedKeys(target));
		var pulled = storage.GetPresetExtendedKeys(target).Single(r => r.Mode == ExtendedKeyMode.ModTap);
		Assert.Equal(modTap, pulled);
		Assert.Equal(261, pulled.TimeMs);
		Assert.Equal(ExtendedKeyRecord.NoSlot, pulled.Slot2);
		Assert.Equal(new ExtendedKeyCodeRecord(0x10, 0x01, 0x00), pulled.HoldKey);
		Assert.Equal(new ExtendedKeyCodeRecord(0x10, 0x00, 0x29), pulled.TapKey);
	}

	// build → apply → build: a Toggle record's time and its one output (in hold_key) are stable across the
	// API round trip, with tap_key staying "none" (docs/plans/add-extended-key-toggle).
	[Fact]
	public void BuildApplyBuild_RoundTripsAToggleRecordWithItsTimeAndOutput()
	{
		using var storage = new LocalStorageService("Data Source=:memory:");
		var owner = Guid.NewGuid().ToString("D");
		var source = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "src" });
		var target = storage.InsertPreset(Guest, owner, Vid, Pid, new PresetRecord { Name = "dst" });
		var toggle = ExtendedKeyRecord.Toggle(20, 261, new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A));
		storage.ReplaceDeviceExtendedKeys(Guest, owner, Vid, Pid, "",
			[toggle, ExtendedKeyRecord.ModTap(30, 150, new ExtendedKeyCodeRecord(0x10, 0x01, 0x00), new ExtendedKeyCodeRecord(0x10, 0x00, 0x29)), ExtendedKeyRecord.RapidSnap(31, 33)]);
		storage.SavePresetSnapshot(source, Guest, owner, Vid, Pid, "");

		var builder = new PresetConfigurationBuilder(storage);
		var first = JsonSerializer.SerializeToElement(builder.Build(source, new Dictionary<int, string>()));
		new PresetConfigurationApplier(storage).Apply(target, first, NoMacros);
		var second = JsonSerializer.SerializeToElement(builder.Build(target, new Dictionary<int, string>()));

		Assert.Equal(first.GetProperty("extended_keys").GetRawText(), second.GetProperty("extended_keys").GetRawText());
		Assert.Equal(storage.GetPresetExtendedKeys(source), storage.GetPresetExtendedKeys(target));
		var pulled = storage.GetPresetExtendedKeys(target).Single(r => r.Mode == ExtendedKeyMode.Toggle);
		Assert.Equal(toggle, pulled);
		Assert.Equal(261, pulled.TimeMs);
		Assert.Equal(ExtendedKeyRecord.NoSlot, pulled.Slot2);
		Assert.Equal(new ExtendedKeyCodeRecord(0x10, 0x00, 0x1A), pulled.HoldKey);
		Assert.Equal(ExtendedKeyCodeRecord.None, pulled.TapKey);
	}
}
