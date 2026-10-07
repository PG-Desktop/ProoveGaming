using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;

namespace ProoveGaming.DeviceHost.Helpers;

/// <summary>
/// appsettings.json (and appsettings.{Environment}.json) are compiled into this assembly instead of
/// shipped as files, so an install carries no settings file for anyone to read or edit. Change them
/// in the repository.
/// </summary>
internal static class EmbeddedAppSettings
{
	// The csproj gives each embedded file this prefix as its LogicalName.
	internal const string ResourcePrefix = "ProoveGaming.DeviceHost.";

	/// <summary>
	/// Replaces the appsettings*.json FILE sources WebApplication.CreateBuilder adds with the embedded
	/// copies, at the same position, so environment variables and the command line still override
	/// them exactly as they overrode the files.
	/// </summary>
	public static void UseEmbeddedAppSettings(this IConfigurationBuilder configuration, string environmentName)
	{
		var sources = configuration.Sources;
		var position = sources.Count;
		for (var i = sources.Count - 1; i >= 0; i--)
		{
			if (sources[i] is JsonConfigurationSource { Path: { } path } &&
				path.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase))
			{
				sources.RemoveAt(i);
				position = i;
			}
		}

		sources.Insert(position, Load("appsettings.json") ??
			throw new InvalidOperationException($"Embedded resource {ResourcePrefix}appsettings.json is missing from the build."));

		if (Load($"appsettings.{environmentName}.json") is { } environmentSettings)
		{
			sources.Insert(position + 1, environmentSettings);
		}
	}

	// Flattened into a memory source rather than added as a JSON stream: the host's
	// ConfigurationManager reloads every source whenever one is added, and a stream can only be
	// read once.
	internal static MemoryConfigurationSource? Load(string fileName)
	{
		using var stream = typeof(EmbeddedAppSettings).Assembly.GetManifestResourceStream(ResourcePrefix + fileName);
		if (stream is null)
		{
			return null;
		}

		var values = new ConfigurationBuilder().AddJsonStream(stream).Build().AsEnumerable().ToList();
		return new MemoryConfigurationSource { InitialData = values };
	}
}
