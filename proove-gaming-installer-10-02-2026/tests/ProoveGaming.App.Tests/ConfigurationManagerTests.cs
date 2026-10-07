using System;
using System.IO;
using ProoveGaming.App;
using Xunit;

namespace ProoveGaming.App.Tests;

// The working directory is process-wide, so these tests must not run alongside any other.
[CollectionDefinition(nameof(ProcessWorkingDirectoryCollection), DisableParallelization = true)]
public sealed class ProcessWorkingDirectoryCollection
{
}

// Pins where the App reads appsettings.json from (docs/plans/fix-repository-audit-findings, S6).
// It used to read it from the CURRENT WORKING DIRECTORY, so launching the App from a folder that
// held another appsettings.json made that file decide UsersApiBaseUrl, i.e. where sign-in sent the
// user's email and password. It is now compiled into the assembly: no file on disk is read at all,
// not even one next to the EXE, and an install ships none.
[Collection(nameof(ProcessWorkingDirectoryCollection))]
public class ConfigurationManagerTests : IDisposable
{
    private readonly string _originalDirectory = Directory.GetCurrentDirectory();
    private readonly string _foreignDirectory = Path.Combine(Path.GetTempPath(), "PgConfigTests", Guid.NewGuid().ToString("N"));

    public ConfigurationManagerTests() => Directory.CreateDirectory(_foreignDirectory);

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalDirectory);
        try { Directory.Delete(_foreignDirectory, recursive: true); } catch { }
    }

    [Fact]
    public void AppSettingsInTheWorkingDirectory_AreIgnored()
    {
        File.WriteAllText(Path.Combine(_foreignDirectory, "appsettings.json"),
            """{ "ApiSettings": { "UsersApiBaseUrl": "https://attacker.example/steal?route=" } }""");
        Directory.SetCurrentDirectory(_foreignDirectory);

        var config = new ConfigurationManager();

        Assert.DoesNotContain("attacker.example", config.ApiSettings.UsersApiBaseUrl);
        Assert.Equal(ExpectedBaseUrl(), config.ApiSettings.UsersApiBaseUrl);
    }

    [Fact]
    public void AppSettingsNextToTheExe_AreIgnoredToo()
    {
        var besideTheExe = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        File.WriteAllText(besideTheExe,
            """{ "ApiSettings": { "UsersApiBaseUrl": "https://attacker.example/steal?route=" } }""");
        try
        {
            var config = new ConfigurationManager();

            Assert.Equal(ExpectedBaseUrl(), config.ApiSettings.UsersApiBaseUrl);
        }
        finally
        {
            File.Delete(besideTheExe);
        }
    }

    [Fact]
    public void AppSettingsLoad_ReadsTheEmbeddedDocument_NotAFileNextToTheExe()
    {
        // The second reader (session length, warning lead time, minimize-to-tray). It used to read
        // the file from disk; with no file shipped, repository values were silently ignored.
        var besideTheExe = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        File.WriteAllText(besideTheExe, """{ "SessionTimeHours": 1, "SessionExpiryWarningMinutes": 1 }""");
        var (hours, minutes) = (AppSettings.SessionTimeHours, AppSettings.SessionExpiryWarningMinutes);
        try
        {
            AppSettings.Load();

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.App.json")));
            Assert.Equal(doc.RootElement.GetProperty("SessionTimeHours").GetInt32(), AppSettings.SessionTimeHours);
            Assert.Equal(doc.RootElement.GetProperty("SessionExpiryWarningMinutes").GetInt32(), AppSettings.SessionExpiryWarningMinutes);
        }
        finally
        {
            File.Delete(besideTheExe);
            (AppSettings.SessionTimeHours, AppSettings.SessionExpiryWarningMinutes) = (hours, minutes);
        }
    }

    [Fact]
    public void TheSettingsAreEmbeddedInTheAppAssembly()
    {
        using var stream = typeof(ConfigurationManager).Assembly.GetManifestResourceStream(ConfigurationManager.EmbeddedSettingsName);

        Assert.NotNull(stream);
    }

    [Fact]
    public void StartedFromAFolderWithoutAppSettings_StillLoads()
    {
        // This used to throw: the file was required and looked for in the working directory.
        Directory.SetCurrentDirectory(_foreignDirectory);

        var config = new ConfigurationManager();

        Assert.Equal(ExpectedBaseUrl(), config.ApiSettings.UsersApiBaseUrl);
    }

    // The repository's src/ProoveGaming.App/appsettings.json, linked into the test output as
    // appsettings.App.json: the document the build embeds.
    private static string ExpectedBaseUrl()
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.App.json"));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("ApiSettings").GetProperty("UsersApiBaseUrl").GetString()!;
    }
}
