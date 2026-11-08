using System.Text.Json;
using Amwag.PrinterSetup.App.Hosting;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.App.Tests.Hosting;

/// <summary>
/// How every role (window, silent run, elevated worker) loads its configuration, with the real "only administrators can change
/// it" check: files the test writes to its own temporary folder belong to the current user, so they never count as protected.
/// </summary>
[Trait("Category", "WindowsIntegration")]
public sealed class AppBootstrapConfigurationTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "amwag-app-tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly MemoryLog _log = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a scanner may briefly hold a file.
        }
    }

    [Fact]
    public void Files_a_standard_user_can_change_are_ignored_and_the_built_in_configuration_is_used()
    {
        string executableDirectory = Directory.CreateDirectory(Path.Combine(_root, "Desktop copy")).FullName;
        string programData = Directory.CreateDirectory(Path.Combine(_root, "ProgramData")).FullName;
        // Invalid if it were used: the run would stop with a configuration error.
        string config = Write(executableDirectory, ConfigLoader.ConfigFileName, """{ "schemaVersion": 1 }""");
        string branch = Write(executableDirectory, ConfigLoader.BranchFileName, """{ "branchId": "desktop-copy" }""");
        string otherBranch = Write(programData, ConfigLoader.BranchFileName, """{ "branchId": "program-data" }""");

        LoadedConfiguration loaded = AppBootstrap.LoadConfiguration(null, executableDirectory, programData, _log);

        Assert.Equal(ConfigLoader.EmbeddedSource, loaded.Source);
        Assert.Equal(new ConfigurationPin(ConfigurationPin.Embedded, ConfigurationPin.NoBranch), ConfigurationPin.Of(loaded));
        Assert.Null(loaded.Branch);
        Assert.Equal(new[] { config, branch, otherBranch }, loaded.IgnoredFiles);
        var ignored = _log.Find("config.warning").Where(w => w.Message.Contains("is not used because", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, ignored.Count);
        Assert.Contains(ignored, w => w.Message.Contains(config, StringComparison.Ordinal) && w.Message.Contains("built into the program", StringComparison.Ordinal));
        LogEntry entry = Assert.Single(_log.Find("config.loaded"));
        Assert.Equal(ConfigurationPin.Embedded, entry.Data!["configSha256"]);
    }

    [Fact]
    public void An_explicit_configuration_is_the_operators_choice_and_is_used()
    {
        string executableDirectory = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
        string explicitPath = Write(_root, "it-test.json", JsonSerializer.Serialize(SampleConfig.Create() with { ConfigVersion = "it-test" }, ConfigJson.Options));

        LoadedConfiguration loaded = AppBootstrap.LoadConfiguration(explicitPath, executableDirectory, Path.Combine(_root, "none"), _log);

        Assert.Equal(explicitPath, loaded.Source);
        Assert.Equal("it-test", loaded.Config.ConfigVersion);
        Assert.Matches("^[0-9a-f]{64}$", loaded.ConfigSha256);
        Assert.Empty(loaded.IgnoredFiles);
    }

    private static string Write(string directory, string name, string content)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }
}
