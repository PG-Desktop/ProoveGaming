using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace ProoveGaming.App.Tests;

// LanguageResourceParityTests only catches a key added to ONE language — if the Bluetooth-mode
// banner keys were dropped from BOTH resource files, parity would stay green while the banner
// rendered its raw DynamicResource fallback. These pin that the keys exist with real text.
public class BluetoothModeBannerResourcesTests
{
	[Theory]
	[InlineData("Strings.en.axaml", "DeviceDetails_BluetoothMode_Title")]
	[InlineData("Strings.en.axaml", "DeviceDetails_BluetoothMode_Message")]
	[InlineData("Strings.uk.axaml", "DeviceDetails_BluetoothMode_Title")]
	[InlineData("Strings.uk.axaml", "DeviceDetails_BluetoothMode_Message")]
	public void BannerKeyIsPresentWithText(string fileName, string key)
	{
		var path = Path.Combine(AppContext.BaseDirectory, fileName);
		Assert.True(File.Exists(path),
			$"{fileName} was not copied to the test output ({path}). " +
			"Check the <None Include=...> items in ProoveGaming.App.Tests.csproj.");

		var match = Regex.Match(File.ReadAllText(path),
			$@"x:Key=""{Regex.Escape(key)}"">([^<]*)<");

		Assert.True(match.Success, $"{key} is missing from {fileName}.");
		Assert.False(string.IsNullOrWhiteSpace(match.Groups[1].Value),
			$"{key} in {fileName} has no text.");
	}
}
