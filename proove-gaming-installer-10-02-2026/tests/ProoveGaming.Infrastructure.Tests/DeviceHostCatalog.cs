using System.Text.Json;
using ProoveGaming.Infrastructure.Classes;

namespace ProoveGaming.Infrastructure.Tests;

// The DeviceHost's REAL device catalog, for tests that must assert against what actually ships
// rather than a hand-written copy that would drift away from it.
//
// Extracted from StarlightCatalogTests when a second caller appeared (the Bluetooth name-match
// regression, which has to see every real catalog name and not a sample of them).
internal static class DeviceHostCatalog
{
	// Copied into the test output by the csproj as appsettings.DeviceHost.json. Read from the build
	// output rather than by walking up to the source tree so the test is hermetic: it passes
	// wherever the assembly runs, and fails with a clear message rather than a null directory walk
	// if the copy is ever dropped.
	internal static List<DeviceGroupConfig> Load()
	{
		var path = Path.Combine(AppContext.BaseDirectory, "appsettings.DeviceHost.json");
		Assert.True(File.Exists(path),
			$"The DeviceHost catalog was not copied to the test output ({path}). " +
			"Check the <None Include=...appsettings.json> item in ProoveGaming.Infrastructure.Tests.csproj.");

		using var document = JsonDocument.Parse(File.ReadAllText(path));
		var devices = document.RootElement
			.GetProperty("AllowedDevices")
			.GetProperty("Devices");

		var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
		var catalog = devices.Deserialize<List<DeviceGroupConfig>>(options);

		Assert.NotNull(catalog);
		return catalog!;
	}
}
