using Amwag.PrinterSetup.Windows.Security;

namespace Amwag.PrinterSetup.Windows.Tests.Drivers;

/// <summary>
/// The check on real files, read-only: files the test creates in its own temporary folder, and a Windows system file. Nothing
/// outside the test's folder is changed.
/// </summary>
[Trait("Category", "WindowsIntegration")]
public sealed class AdministratorControlledFileTests : IDisposable
{
    private readonly TempFolders _folders = new();
    private string? _junction;

    public void Dispose()
    {
        if (_junction is not null)
            Directory.Delete(_junction);
        _folders.Dispose();
    }

    [Fact]
    public void A_file_the_user_created_is_not_administrator_controlled()
    {
        string path = Path.Combine(_folders.ExecutableDirectory, "amwag-printer-config.json");
        File.WriteAllText(path, "{}");

        string? problem = Check(path);

        Assert.NotNull(problem);
        Assert.Contains(_folders.ExecutableDirectory, problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_windows_system_file_is_administrator_controlled()
    {
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "kernel32.dll");

        Assert.Null(Check(path));
    }

    [Fact]
    public void A_file_reached_through_a_junction_is_not_administrator_controlled()
    {
        string real = Directory.CreateDirectory(Path.Combine(_folders.Root, "real")).FullName;
        File.WriteAllText(Path.Combine(real, "branch.json"), "{}");
        string link = Path.Combine(_folders.Root, "link");
        Junction.Create(link, real);
        _junction = link;

        string? problem = Check(Path.Combine(link, "branch.json"));

        Assert.NotNull(problem);
        Assert.Contains("junction", problem, StringComparison.Ordinal);
        Assert.Contains(link, problem, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Check(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return AdministratorControlledFile.FindProblem(stream);
    }
}
