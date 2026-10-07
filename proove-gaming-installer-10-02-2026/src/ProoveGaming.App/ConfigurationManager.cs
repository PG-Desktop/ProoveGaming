using System;
using System.IO;
using Microsoft.Extensions.Configuration;

namespace ProoveGaming.App;

public class ConfigurationManager
{
    public ConfigurationManager()
    {
        // appsettings.json is compiled into this assembly and never read from disk. An install ships
        // no settings file for anyone to read or edit, and no file anywhere can override it: the
        // App once read appsettings.json from the current working directory, so a folder holding a
        // different one decided UsersApiBaseUrl, i.e. where sign-in sent the user's email and
        // password (docs/plans/fix-repository-audit-findings, S6). Works the same in the XAML
        // previewer, which loads this assembly too.
        using var settings = OpenEmbeddedSettings();

        var builder = new ConfigurationBuilder()
            .AddJsonStream(settings);

#if DEBUG
        // Developer overrides only. A shipped build must not read a per-user file that could
        // redirect the API base URL.
        builder.AddUserSecrets<ConfigurationManager>(optional: true);
#endif

        IConfiguration config = builder.Build();

        var section = config.GetSection("ApiSettings").Get<ApiSettings>();

        if (section != null)
        {
            ApiSettings = section;
        }
        else
        {
            throw new Exception("ApiSettings section was not found in system configuration");
        }
    }

    // The LogicalName the csproj gives the embedded appsettings.json.
    internal const string EmbeddedSettingsName = "ProoveGaming.App.appsettings.json";

    // The App's only source of settings: both this class and AppSettings read it, so a value changed
    // in the repository reaches every reader.
    internal static Stream OpenEmbeddedSettings() =>
        typeof(ConfigurationManager).Assembly.GetManifestResourceStream(EmbeddedSettingsName)
            ?? throw new InvalidOperationException($"Embedded resource {EmbeddedSettingsName} is missing from the build.");

    public ApiSettings ApiSettings { get; private set; }
}
