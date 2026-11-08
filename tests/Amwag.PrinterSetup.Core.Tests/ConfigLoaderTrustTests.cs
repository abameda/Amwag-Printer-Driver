using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.Core.Tests;

/// <summary>
/// The loader with a trust check (in the application: only administrators can change the file). A rejected file next to the
/// executable is ignored in favour of the embedded copy, a rejected branch file in favour of the next location; the result
/// pins the exact bytes used.
/// </summary>
public sealed class ConfigLoaderTrustTests : IDisposable
{
    private const string Rejected = "standard users can change it";

    private readonly TempDirectory _temp = new();
    private readonly List<string> _checked = [];

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void A_rejected_file_next_to_the_executable_is_ignored_and_the_embedded_copy_is_used()
    {
        string path = _temp.Write(ConfigLoader.ConfigFileName, SampleJson(c => c with { ConfigVersion = "beside-exe" }));

        var loaded = Load(RejectAll, embedded: SampleJson(c => c with { ConfigVersion = "embedded" }));

        Assert.Equal("embedded", loaded.Config.ConfigVersion);
        Assert.Equal(ConfigLoader.EmbeddedSource, loaded.Source);
        Assert.Equal(ConfigLoader.EmbeddedSource, loaded.ConfigSha256);
        Assert.Equal(new[] { path }, loaded.IgnoredFiles);
        string warning = Assert.Single(loaded.Warnings, w => w.Contains(path, StringComparison.Ordinal));
        Assert.Contains(Rejected, warning, StringComparison.Ordinal);
        Assert.Contains("built into the program", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void An_accepted_file_next_to_the_executable_is_used_and_pinned_by_its_exact_bytes()
    {
        string json = SampleJson(c => c with { ConfigVersion = "beside-exe" });
        string path = _temp.Write(ConfigLoader.ConfigFileName, json);

        var loaded = Load(AcceptAll, embedded: SampleJson());

        Assert.Equal("beside-exe", loaded.Config.ConfigVersion);
        Assert.Equal(path, loaded.Source);
        Assert.Equal(Sha256(File.ReadAllBytes(path)), loaded.ConfigSha256);
        Assert.Empty(loaded.IgnoredFiles);
        Assert.Contains(path, _checked);
    }

    [Fact]
    public void The_check_and_the_read_use_one_handle_that_nobody_can_write_replace_or_delete_meanwhile()
    {
        string path = _temp.Write(ConfigLoader.ConfigFileName, SampleJson(c => c with { ConfigVersion = "checked" }));
        var attempts = new List<Exception>();

        var loaded = Load(file =>
        {
            Assert.Equal(path, file.Name);
            attempts.Add(Record.Exception(() => File.WriteAllText(path, SampleJson(c => c with { ConfigVersion = "swapped" }))));
            attempts.Add(Record.Exception(() => File.Delete(path)));
            attempts.Add(Record.Exception(() => File.Move(path, path + ".old")));
            return null;
        });

        Assert.Equal("checked", loaded.Config.ConfigVersion);
        Assert.All(attempts, a => Assert.IsType<IOException>(a));
        Assert.Equal(Sha256(File.ReadAllBytes(path)), loaded.ConfigSha256);
    }

    [Fact]
    public void An_explicit_path_is_the_callers_choice_and_is_not_checked()
    {
        string explicitPath = _temp.Write("it/custom.json", SampleJson(c => c with { ConfigVersion = "explicit" }));

        var loaded = Load(RejectAll, explicitPath: explicitPath);

        Assert.Equal("explicit", loaded.Config.ConfigVersion);
        Assert.DoesNotContain(explicitPath, _checked);
        Assert.Equal(Sha256(File.ReadAllBytes(explicitPath)), loaded.ConfigSha256);
    }

    [Fact]
    public void A_rejected_branch_file_next_to_the_executable_falls_back_to_an_accepted_one_in_program_data()
    {
        string programData = _temp.Subdirectory("ProgramData");
        string besideExe = _temp.Write(ConfigLoader.BranchFileName, """{ "branchId": "beside-exe" }""");
        string protectedBranch = _temp.Write("ProgramData/" + ConfigLoader.BranchFileName, """{ "branchId": "program-data" }""");

        var loaded = Load(file => file.Name == besideExe ? Rejected : null, embedded: SampleJson(), programData: programData);

        Assert.Equal("program-data", loaded.Branch!.BranchId);
        Assert.Equal(protectedBranch, loaded.BranchSource);
        Assert.Equal(Sha256(File.ReadAllBytes(protectedBranch)), loaded.BranchSha256);
        Assert.Equal(new[] { besideExe }, loaded.IgnoredFiles);
        Assert.Contains(loaded.Warnings, w => w.Contains(besideExe, StringComparison.Ordinal) && w.Contains("not applied", StringComparison.Ordinal));
    }

    [Fact]
    public void A_rejected_branch_file_in_program_data_is_not_used_either()
    {
        string programData = _temp.Subdirectory("ProgramData");
        string rejected = _temp.Write("ProgramData/" + ConfigLoader.BranchFileName, """{ "branchId": "program-data" }""");

        var loaded = Load(RejectAll, embedded: SampleJson(), programData: programData);

        Assert.Null(loaded.Branch);
        Assert.Null(loaded.BranchSource);
        Assert.Null(loaded.BranchSha256);
        Assert.Equal(new[] { rejected }, loaded.IgnoredFiles);
    }

    [Fact]
    public void An_accepted_branch_file_next_to_the_executable_still_wins()
    {
        string programData = _temp.Subdirectory("ProgramData");
        _temp.Write("ProgramData/" + ConfigLoader.BranchFileName, """{ "branchId": "program-data" }""");
        string besideExe = _temp.Write(ConfigLoader.BranchFileName, """{ "branchId": "beside-exe" }""");

        var loaded = Load(AcceptAll, embedded: SampleJson(), programData: programData);

        Assert.Equal("beside-exe", loaded.Branch!.BranchId);
        Assert.Equal(besideExe, loaded.BranchSource);
    }

    [Fact]
    public void A_rejected_file_is_never_parsed()
    {
        _temp.Write(ConfigLoader.ConfigFileName, "{ not json");
        _temp.Write(ConfigLoader.BranchFileName, """{ "ticket": { "width": 1 } }""");

        var loaded = Load(RejectAll, embedded: SampleJson());

        Assert.Equal(ConfigLoader.EmbeddedSource, loaded.Source);
        Assert.Null(loaded.Branch);
        Assert.Equal(2, loaded.IgnoredFiles.Count);
    }

    [Fact]
    public void Without_files_the_pin_names_the_embedded_copy_and_no_branch()
    {
        var loaded = Load(AcceptAll, embedded: SampleJson());

        Assert.Equal(ConfigLoader.EmbeddedSource, loaded.ConfigSha256);
        Assert.Null(loaded.BranchSha256);
        Assert.Empty(loaded.IgnoredFiles);
        Assert.Empty(_checked);
    }

    [Fact]
    public void The_pin_changes_with_any_byte_of_the_file()
    {
        _temp.Write(ConfigLoader.ConfigFileName, SampleJson());
        string first = Load(AcceptAll).ConfigSha256;

        _temp.Write(ConfigLoader.ConfigFileName, SampleJson() + " ");
        string second = Load(AcceptAll).ConfigSha256;

        Assert.Matches("^[0-9a-f]{64}$", first);
        Assert.NotEqual(first, second);
    }

    private string? RejectAll(FileStream file)
    {
        _checked.Add(file.Name);
        return Rejected;
    }

    private string? AcceptAll(FileStream file)
    {
        _checked.Add(file.Name);
        return null;
    }

    private LoadedConfiguration Load(FileTrustCheck check, string? explicitPath = null, string? embedded = null, string? programData = null) =>
        ConfigLoader.Load(explicitPath, _temp.Path, () => embedded is null ? null : new MemoryStream(Encoding.UTF8.GetBytes(embedded)), programData, check);

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string SampleJson(Func<DeploymentConfig, DeploymentConfig>? edit = null)
    {
        var config = SampleConfig.Create();
        return JsonSerializer.Serialize(edit is null ? config : edit(config), ConfigJson.Options);
    }
}
