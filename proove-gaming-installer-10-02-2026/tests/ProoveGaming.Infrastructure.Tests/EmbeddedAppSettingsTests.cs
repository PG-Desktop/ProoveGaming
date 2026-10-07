using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using ProoveGaming.DeviceHost.Helpers;
using Xunit;

namespace ProoveGaming.Infrastructure.Tests;

// The DeviceHost's appsettings are compiled into its assembly, so an install ships no settings file
// to read or edit. Pins that both documents are embedded, that they replace the host builder's FILE
// sources in place, and that environment variables still override them.
public class EmbeddedAppSettingsTests
{
	[Theory]
	[InlineData("appsettings.json")]
	[InlineData("appsettings.Development.json")]
	public void BothSettingsDocuments_AreEmbedded(string fileName)
	{
		Assert.NotNull(EmbeddedAppSettings.Load(fileName));
	}

	[Fact]
	public void EmbeddedSettings_AreTheRepositoryDocument()
	{
		// appsettings.DeviceHost.json is the repository file, linked into the test output by the csproj.
		using var repository = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.DeviceHost.json")));
		var expected = repository.RootElement.GetProperty("Serilog").GetProperty("MinimumLevel").GetString();

		var configuration = new ConfigurationBuilder().Add(EmbeddedAppSettings.Load("appsettings.json")!).Build();

		Assert.Equal(expected, configuration["Serilog:MinimumLevel"]);
		Assert.NotEmpty(configuration.GetSection("AllowedDevices:Devices").GetChildren());
	}

	[Fact]
	public void FileSources_AreReplacedInPlace_AndLaterSourcesStillOverride()
	{
		// The shape WebApplication.CreateBuilder produces: the two appsettings files, then sources that
		// must keep overriding them (environment variables, the command line).
		var configuration = new ConfigurationManager();
		configuration.AddJsonFile("appsettings.json", optional: true);
		configuration.AddJsonFile("appsettings.Production.json", optional: true);
		configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Serilog:MinimumLevel"] = "Verbose" });

		configuration.UseEmbeddedAppSettings("Production");

		Assert.DoesNotContain(configuration.Sources, s => s is JsonConfigurationSource);
		Assert.Equal("Verbose", configuration["Serilog:MinimumLevel"]);
		Assert.NotEmpty(configuration.GetSection("AllowedDevices:Devices").GetChildren());
	}

	[Fact]
	public void DevelopmentEnvironment_AlsoLoadsTheDevelopmentDocument()
	{
		// ConfigurationManager starts with an empty memory source of its own, so compare the two runs.
		static int SourcesFor(string environment)
		{
			var configuration = new ConfigurationManager();
			configuration.AddJsonFile("appsettings.json", optional: true);
			configuration.UseEmbeddedAppSettings(environment);
			return configuration.Sources.Count;
		}

		Assert.Equal(SourcesFor("Production") + 1, SourcesFor("Development"));
	}
}
