using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Windows.Drivers;
using Xunit.Abstractions;

namespace Amwag.PrinterSetup.Windows.Tests.Drivers;

/// <summary>
/// The official TA KX 8.7.0917 package as downloaded by the research step (read-only). The folders can be overridden with
/// AMWAG_TA_KX87_EXTRACT (the extracted archive root) and AMWAG_TA_KX87_DOWNLOAD (the folder holding the original ZIP).
/// </summary>
internal static class TaKx87
{
    public const string FileName = "Kx87_UPD_8.7.0917.zip";
    public const long SizeBytes = 203_308_603;
    public const string Sha256 = "862a5367a365f0ff61365baf0d229a0f761e6f64efe8013e8ff6da8c2c109e48";
    public const string Signer = "KYOCERA Document Solutions Inc.";

    private const string ResearchRoot =
        @"C:\Users\abdel\AppData\Local\Temp\claude\C--Users-abdel-Desktop-Amwag-Printer\7737442f-5f1d-46cc-85b0-3b47c5e9c693\scratchpad\research\kx87";

    public static string ExtractedRoot =>
        Environment.GetEnvironmentVariable("AMWAG_TA_KX87_EXTRACT") ?? Path.Combine(ResearchRoot, "extract");

    public static string DownloadFolder =>
        Environment.GetEnvironmentVariable("AMWAG_TA_KX87_DOWNLOAD") ?? Path.Combine(ResearchRoot, "download");

    public static string Inf => Path.Combine(ExtractedRoot, "Kx87_UPD_8.7.0917", "64bit", "OEMSETUP.INF");
    public static string Catalog => Path.Combine(ExtractedRoot, "Kx87_UPD_8.7.0917", "64bit", "utax.cat");
    public static string Archive => Path.Combine(DownloadFolder, FileName);

    public static bool ExtractedAvailable => File.Exists(Inf) && File.Exists(Catalog);
    public static bool ArchiveAvailable => File.Exists(Archive) && new FileInfo(Archive).Length == SizeBytes;
}

public sealed class TaExtractedPackageFactAttribute : FactAttribute
{
    public TaExtractedPackageFactAttribute()
    {
        if (!TaKx87.ExtractedAvailable)
            Skip = $"The extracted TA KX 8.7 package is not available at {TaKx87.ExtractedRoot}.";
    }
}

public sealed class TaArchiveFactAttribute : FactAttribute
{
    public TaArchiveFactAttribute()
    {
        if (!TaKx87.ArchiveAvailable)
            Skip = $"The original TA KX 8.7 archive is not available at {TaKx87.Archive}.";
    }
}

[Trait("Category", "WindowsIntegration")]
public sealed class TaPackageIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public TaPackageIntegrationTests(ITestOutputHelper output) => _output = output;

    [TaExtractedPackageFact]
    public void InfNamesItsCatalog() =>
        Assert.Equal("UTAX.CAT", InfVersionSection.ReadCatalogFile(TaKx87.Inf, "x64"));

    [TaExtractedPackageFact]
    public void CatalogIsTrustedAndSignedByKyoceraAndListsTheInf()
    {
        CatalogVerification verification = CatalogVerification.VerifyWithWindows(TaKx87.Catalog, TaKx87.Inf);

        _output.WriteLine($"{verification.Signature.SignerSubject} | {verification.Signature.IssuerSubject} | {verification.Signature.Thumbprint}");
        _output.WriteLine(verification.InfMembership.Detail);
        Assert.True(verification.Signature.IsTrusted, verification.Signature.StatusText);
        Assert.Equal(TaKx87.Signer, verification.SignerCommonName);
        Assert.True(DriverPackageProvider.IsApprovedSigner(verification.Signature.SignerSubject, verification.SignerCommonName, [TaKx87.Signer]));
        Assert.Equal(CatalogMembershipStatus.Member, verification.InfMembership.Status);
    }
}

/// <summary>The whole preparation pipeline on the real 203 MB archive (offline folder, no network, extraction to a temp folder).</summary>
[Trait("Category", "WindowsIntegration")]
public sealed class TaArchivePreparationTests : IDisposable
{
    private readonly TempFolders _folders = new();
    private readonly ITestOutputHelper _output;

    public TaArchivePreparationTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => _folders.Dispose();

    [TaArchiveFact]
    public void RealArchiveIsVerifiedExtractedAndItsCatalogTrusted()
    {
        var log = new RecordingLog();
        var definition = new DriverPackageDefinition
        {
            Id = "ta-kx-v3-8.7.0917",
            Version = "8.7.0917",
            DownloadUrl = "https://www.triumph-adler.com/caas/v1/ta/media/102720/data/04e63668c3001927fbccf7c5c22a6cc3/Kx87_UPD_8.7.0917.zip",
            FileName = TaKx87.FileName,
            SizeBytes = TaKx87.SizeBytes,
            Sha256 = TaKx87.Sha256.ToUpperInvariant(),
            Environment = "Windows x64",
            Architecture = "x64",
            InfPath = "Kx87_UPD_8.7.0917/64bit/OEMSETUP.INF",
            TrustedSigners = [TaKx87.Signer],
            Approved = true,
        };
        var provider = new DriverPackageProvider(
            new DriverSourceConfig { OfflineFolder = TaKx87.DownloadFolder, AllowOnlineDownload = false },
            _folders.ExecutableDirectory,
            log,
            _folders.CacheDirectory,
            _folders.ExtractionRoot,
            FakeHttpHandler.Failing(),
            CatalogVerification.VerifyWithWindows,
            isElevated: false);

        PreparedDriverPackage prepared = provider.Prepare(definition, null, default);

        _output.WriteLine($"{prepared.InfPath} | {prepared.CatalogSignature.SignerSubject}");
        Assert.Equal(PackageOrigin.OfflineFolder, prepared.Origin);
        Assert.True(File.Exists(prepared.InfPath));
        Assert.True(prepared.CatalogSignature.IsTrusted);
        Assert.Contains(log.Entries, e => e.EventName == "driver.package.catalog.member");

        // Only the x64 driver folder (536 files plus KACT\ 14 and KACT2\ 1) is extracted; installers and other architectures are not.
        string[] files = [.. Directory.EnumerateFiles(prepared.ExtractedRoot, "*", SearchOption.AllDirectories)];
        string x64 = Path.Combine(prepared.ExtractedRoot, "Kx87_UPD_8.7.0917", "64bit") + Path.DirectorySeparatorChar;
        Assert.Equal(551, files.Length);
        Assert.All(files, f => Assert.StartsWith(x64, f, StringComparison.OrdinalIgnoreCase));
    }
}
