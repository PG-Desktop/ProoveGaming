using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace ProoveGaming.App.Tests;

// Localization has a silent failure mode, which is why it needs a test rather than a convention.
//
// A key present in en and missing from uk does not throw and does not fail the build: the Ukrainian
// UI simply shows the English string, and only a Ukrainian-speaking reader notices. The same is true
// one layer up, where text arriving from appsettings.json is turned into a resource key by a
// converter — DeviceTypeLabelConverter ends in `_ => null` and returns the RAW catalog string for
// anything it does not recognise. "Wireless Headset" shipped that way: the headsets were added to
// the catalog, the converter was never extended, and the card showed untranslated English with no
// error anywhere.
//
// These pin both halves so the next device cannot repeat it.
public class LanguageResourceParityTests
{
	private static string OutputPath(string name)
	{
		var path = Path.Combine(AppContext.BaseDirectory, name);
		Assert.True(File.Exists(path),
			$"{name} was not copied to the test output ({path}). " +
			"Check the <None Include=...> items in ProoveGaming.App.Tests.csproj.");
		return path;
	}

	private static HashSet<string> KeysOf(string fileName) =>
		[.. Regex.Matches(File.ReadAllText(OutputPath(fileName)), @"x:Key=""([^""]+)""")
			.Select(m => m.Groups[1].Value)];

	// ── en/uk parity ──────────────────────────────────────────────────────────────────────────

	[Fact]
	public void EveryEnglishStringHasAUkrainianTranslation()
	{
		var en = KeysOf("Strings.en.axaml");
		var uk = KeysOf("Strings.uk.axaml");

		var missing = en.Except(uk).Order().ToList();

		Assert.True(missing.Count == 0,
			"These keys exist in Strings.en.axaml but not in Strings.uk.axaml, so the Ukrainian UI " +
			"will silently show English:\n  " + string.Join("\n  ", missing));
	}

	// A sanity floor: if a parse change ever made KeysOf return nothing, the test above would pass
	// vacuously and stop protecting anything.
	[Fact]
	public void TheResourceFilesActuallyParse()
	{
		Assert.True(KeysOf("Strings.en.axaml").Count > 100);
		Assert.True(KeysOf("Strings.uk.axaml").Count > 100);
	}

	// ── Catalog text reaching the UI ──────────────────────────────────────────────────────────

	// Every DeviceType the catalog can show on a card must be translatable. The converter's
	// fallback returns the raw English string, so an unmapped value is invisible until someone
	// reads the UI in Ukrainian.
	[Fact]
	public void EveryCatalogDeviceTypeIsMappedAndTranslated()
	{
		// Copied into the output like the other inputs, rather than reached by walking up the
		// source tree — that walk breaks the moment the configuration or target framework changes
		// the output depth. Linked as .txt so the test project does not try to COMPILE it.
		var converter = File.ReadAllText(OutputPath("DeviceTypeLabelConverter.cs.txt"));

		var mapped = Regex.Matches(converter, @"""([^""]+)""\s*=>\s*""(DeviceType_[A-Za-z]+)""")
			.ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);

		var en = KeysOf("Strings.en.axaml");
		var uk = KeysOf("Strings.uk.axaml");

		var problems = new List<string>();
		foreach (var deviceType in CatalogDeviceTypes().Order())
		{
			if (!mapped.TryGetValue(deviceType, out var key))
			{
				problems.Add($"'{deviceType}' has no case in DeviceTypeLabelConverter — it renders as raw English.");
				continue;
			}

			if (!en.Contains(key)) problems.Add($"'{deviceType}' maps to {key}, which is missing from Strings.en.axaml.");
			if (!uk.Contains(key)) problems.Add($"'{deviceType}' maps to {key}, which is missing from Strings.uk.axaml.");
		}

		Assert.True(problems.Count == 0, string.Join("\n  ", problems));
	}

	// Device-level and per-transport values both reach the card, so both are collected.
	private static HashSet<string> CatalogDeviceTypes()
	{
		using var document = JsonDocument.Parse(File.ReadAllText(OutputPath("appsettings.DeviceHost.json")));
		var devices = document.RootElement.GetProperty("AllowedDevices").GetProperty("Devices");

		var types = new HashSet<string>(StringComparer.Ordinal);
		foreach (var device in devices.EnumerateArray())
		{
			Collect(device);
			if (device.TryGetProperty("Transports", out var transports))
				foreach (var transport in transports.EnumerateArray())
					Collect(transport);
		}

		return types;

		void Collect(JsonElement element)
		{
			if (element.TryGetProperty("DeviceType", out var value) &&
				value.GetString() is { } text &&
				!string.IsNullOrWhiteSpace(text))
			{
				types.Add(text.Trim());
			}
		}
	}
}
