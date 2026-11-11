using System.Text.Json;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Windows.Drivers;
using Xunit.Abstractions;

namespace Amwag.PrinterSetup.Windows.Tests.Drivers;

/// <summary>
/// The shipped manifest's driver packages whose original archives are in the repository's drivers folder (or
/// AMWAG_DRIVERS_FOLDER). The archives are not in source control, so packages without one are not tested here.
/// </summary>
public static class ShippedPackages
{
    public static DeploymentConfig Config { get; } = JsonSerializer.Deserialize<DeploymentConfig>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", ConfigLoader.ConfigFileName)), ConfigJson.Options)!;

    public static string DriversFolder =>
        Environment.GetEnvironmentVariable("AMWAG_DRIVERS_FOLDER") ?? Path.Combine(RepositoryRoot(), "drivers");

    public static IEnumerable<object[]> WithArchive() =>
        Config.DriverPackages.Where(p => File.Exists(Path.Combine(DriversFolder, p.FileName))).Select(p => new object[] { p.Id });

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AmwagPrinterSetup.sln")))
                return directory.FullName;
        }

        return AppContext.BaseDirectory;
    }
}

public sealed class ShippedArchiveTheoryAttribute : TheoryAttribute
{
    public ShippedArchiveTheoryAttribute()
    {
        if (!ShippedPackages.WithArchive().Any())
            Skip = $"No archive of the shipped driver packages is in {ShippedPackages.DriversFolder}.";
    }
}

[Trait("Category", "WindowsIntegration")]
public sealed class ShippedPackageIntegrationTests : IDisposable
{
    private readonly TempFolders _folders = new();
    private readonly ITestOutputHelper _output;

    public ShippedPackageIntegrationTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => _folders.Dispose();

    /// <summary>
    /// The real preparation pipeline (size, SHA-256, extraction, catalog signer and INF membership) accepts the archive,
    /// and the INF it installs from offers the driver name of every model that uses the package.
    /// </summary>
    [ShippedArchiveTheory]
    [MemberData(nameof(ShippedPackages.WithArchive), MemberType = typeof(ShippedPackages))]
    public void Archive_is_verified_and_its_inf_offers_each_models_driver(string packageId)
    {
        var package = ShippedPackages.Config.DriverPackages.Single(p => p.Id == packageId);
        var log = new RecordingLog();
        var provider = new DriverPackageProvider(
            new DriverSourceConfig { OfflineFolder = ShippedPackages.DriversFolder, AllowOnlineDownload = false },
            _folders.ExecutableDirectory,
            log,
            _folders.CacheDirectory,
            _folders.ExtractionRoot,
            FakeHttpHandler.Failing(),
            CatalogVerification.VerifyWithWindows,
            isElevated: false);

        PreparedDriverPackage prepared = provider.Prepare(package, null, default);

        _output.WriteLine($"{prepared.InfPath} | {prepared.CatalogSignature.SignerSubject}");
        Assert.Equal(PackageOrigin.OfflineFolder, prepared.Origin);
        Assert.True(prepared.CatalogSignature.IsTrusted, prepared.CatalogSignature.StatusText);
        Assert.Contains(log.Entries, e => e.EventName == "driver.package.catalog.member");

        string inf = File.ReadAllText(prepared.InfPath);
        var models = ShippedPackages.Config.Models.Where(m => string.Equals(m.DriverPackageId, packageId, StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.NotEmpty(models);
        Assert.All(models, model =>
        {
            Assert.Contains($"\"{model.PreferredDriverName}\"", inf, StringComparison.Ordinal);
            Assert.All(model.HardwareIds, id => Assert.Contains(id, inf, StringComparison.OrdinalIgnoreCase));
        });
    }
}
