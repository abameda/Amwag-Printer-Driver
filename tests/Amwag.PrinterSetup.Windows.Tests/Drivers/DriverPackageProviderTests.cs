using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Windows.Drivers;

namespace Amwag.PrinterSetup.Windows.Tests.Drivers;

public sealed class DriverPackageProviderTests : IDisposable
{
    private readonly TempFolders _folders = new();
    private readonly RecordingLog _log = new();

    public void Dispose() => _folders.Dispose();

    [Fact]
    public void NotApprovedPackageIsRefusedBeforeAnyFileAccess()
    {
        byte[] archive = TestPackages.Archive();
        File.WriteAllBytes(Path.Combine(_folders.OfflineFolder, "pkg.zip"), archive);
        var definition = TestPackages.Definition(archive) with { Approved = false };

        var ex = Assert.Throws<PrinterSetupException>(() => TestPackages.Provider(_folders, _log).Prepare(definition, null, default));

        Assert.Equal(FindingCode.DriverPackageNotApproved, ex.Code);
        Assert.False(Directory.Exists(_folders.ExtractionRoot));
    }

    [Fact]
    public void ValidOfflineArchiveIsVerifiedExtractedAndSignatureChecked()
    {
        byte[] archive = TestPackages.Archive();
        string offline = Path.Combine(_folders.OfflineFolder, "pkg.zip");
        File.WriteAllBytes(offline, archive);
        var verifier = new StubCatalogVerifier();
        var progress = new RecordingProgress();

        PreparedDriverPackage prepared = TestPackages.Provider(_folders, _log, verifier: verifier)
            .Prepare(TestPackages.Definition(archive), progress, default);

        Assert.Equal(PackageOrigin.OfflineFolder, prepared.Origin);
        Assert.Equal(offline, prepared.ArchivePath);
        Assert.StartsWith(_folders.ExtractionRoot + Path.DirectorySeparatorChar, prepared.ExtractedRoot, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("^[0-9a-f]{16}-[0-9]{14}$", Path.GetFileName(prepared.ExtractedRoot));
        Assert.Equal(Path.Combine(prepared.ExtractedRoot, "pkg", "64bit", "OEMSETUP.INF"), prepared.InfPath);
        Assert.True(File.Exists(prepared.InfPath));
        Assert.True(File.Exists(Path.Combine(prepared.ExtractedRoot, "pkg", "64bit", "driver.dll")));
        Assert.True(prepared.CatalogSignature.IsTrusted);

        var call = Assert.Single(verifier.Calls);
        Assert.Equal(prepared.InfPath, call.Inf);
        Assert.Equal("TEST.CAT", Path.GetFileName(call.Catalog));
        Assert.True(File.Exists(call.Catalog));

        Assert.Contains(progress.Updates, u => u.Phase == ProgressPhase.Extracting && u.Fraction == 1);
        Assert.All(progress.Updates, u => Assert.Equal(StepId.InstallDriver, u.Step));
        Assert.True(_log.Contains("driver.package.prepared"));
    }

    [Fact]
    public void RelativeOfflineFolderResolvesAgainstTheExecutableDirectory()
    {
        byte[] archive = TestPackages.Archive();
        File.WriteAllBytes(Path.Combine(_folders.OfflineFolder, "pkg.zip"), archive);

        PreparedDriverPackage prepared = TestPackages.Provider(_folders, _log, offlineFolder: "drivers")
            .Prepare(TestPackages.Definition(archive), null, default);

        Assert.Equal(Path.Combine(_folders.OfflineFolder, "pkg.zip"), prepared.ArchivePath);
    }

    [Fact]
    public void OfflineArchiveWithWrongSizeIsNeverUsedAndTheCacheIsUsedInstead()
    {
        byte[] archive = TestPackages.Archive();
        var definition = TestPackages.Definition(archive);
        File.WriteAllBytes(Path.Combine(_folders.OfflineFolder, "pkg.zip"), [.. archive, 0]);
        string cached = Path.Combine(_folders.CacheDirectory, definition.Sha256.ToLowerInvariant(), "pkg.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
        File.WriteAllBytes(cached, archive);

        PreparedDriverPackage prepared = TestPackages.Provider(_folders, _log).Prepare(definition, null, default);

        Assert.Equal(PackageOrigin.LocalCache, prepared.Origin);
        Assert.Equal(cached, prepared.ArchivePath);
        Assert.Contains(_log.Entries, e => e.EventName == "driver.package.archive.rejected");
    }

    [Fact]
    public void OfflineArchiveWithWrongHashIsAnIntegrityFailureWhenNothingElseWorks()
    {
        byte[] archive = TestPackages.Archive();
        var definition = TestPackages.Definition(archive);
        byte[] tampered = (byte[])archive.Clone();
        tampered[^10] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(_folders.OfflineFolder, "pkg.zip"), tampered);

        var ex = Assert.Throws<PrinterSetupException>(() => TestPackages.Provider(_folders, _log).Prepare(definition, null, default));

        Assert.Equal(FindingCode.DriverPackageIntegrityFailed, ex.Code);
        Assert.Contains("SHA-256", ex.Message);
        Assert.Contains("online download is disabled", ex.Message);
        Assert.False(Directory.Exists(_folders.ExtractionRoot));
    }

    [Fact]
    public void NoCopyAnywhereAndDownloadDisabledIsUnavailable()
    {
        var definition = TestPackages.Definition(TestPackages.Archive());

        var ex = Assert.Throws<PrinterSetupException>(() => TestPackages.Provider(_folders, _log).Prepare(definition, null, default));

        Assert.Equal(FindingCode.DriverPackageUnavailable, ex.Code);
        Assert.Contains("online download is disabled", ex.Message);
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void ZipSlipEntryIsRejectedWithoutWritingOutsideTheExtractionFolder()
    {
        byte[] archive = TestPackages.Zip((TestPackages.InfPath, TestPackages.Inf), ("../evil.txt", "escaped"));
        File.WriteAllBytes(Path.Combine(_folders.OfflineFolder, "pkg.zip"), archive);

        var ex = Assert.Throws<PrinterSetupException>(() => TestPackages.Provider(_folders, _log).Prepare(TestPackages.Definition(archive), null, default));

        Assert.Equal(FindingCode.DriverPackageIntegrityFailed, ex.Code);
        Assert.Contains("outside the extraction folder", ex.Message);
        Assert.False(File.Exists(Path.Combine(_folders.ExtractionRoot, "evil.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_folders.ExtractionRoot));
    }

    [Theory]
    [InlineData("C:/Windows/Temp/amwag-evil.txt")]
    [InlineData("/amwag-evil.txt")]
    [InlineData("\\\\server\\share\\amwag-evil.txt")]
    [InlineData("pkg/file.txt:hidden")]
    public void AbsoluteOrDriveOrStreamEntryIsRejected(string entryName)
    {
        byte[] archive = TestPackages.Zip((TestPackages.InfPath, TestPackages.Inf), (entryName, "escaped"));
        File.WriteAllBytes(Path.Combine(_folders.OfflineFolder, "pkg.zip"), archive);

        var ex = Assert.Throws<PrinterSetupException>(() => TestPackages.Provider(_folders, _log).Prepare(TestPackages.Definition(archive), null, default));

        Assert.Equal(FindingCode.DriverPackageIntegrityFailed, ex.Code);
        Assert.Contains("absolute path", ex.Message);
        Assert.False(File.Exists("C:/Windows/Temp/amwag-evil.txt"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_folders.ExtractionRoot));
    }

    [Fact]
    public void MissingInfInTheApprovedArchiveIsAConfigurationError()
    {
        byte[] archive = TestPackages.Archive();
        File.WriteAllBytes(Path.Combine(_folders.OfflineFolder, "pkg.zip"), archive);
        var definition = TestPackages.Definition(archive) with { InfPath = "pkg\\64bit\\MISSING.INF" };

        var ex = Assert.Throws<PrinterSetupException>(() => TestPackages.Provider(_folders, _log).Prepare(definition, null, default));

        Assert.Equal(FindingCode.ConfigurationInvalid, ex.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_folders.ExtractionRoot));
    }

    [Fact]
    public void ConfiguredCatalogPathTakesPrecedenceOverTheInf()
    {
        byte[] archive = TestPackages.Zip((TestPackages.InfPath, TestPackages.Inf), ("pkg/64bit/test.cat", "x"), ("pkg/other.cat", "y"));
        File.WriteAllBytes(Path.Combine(_folders.OfflineFolder, "pkg.zip"), archive);
        var verifier = new StubCatalogVerifier();
        var definition = TestPackages.Definition(archive) with { CatalogPath = "pkg/other.cat" };

        TestPackages.Provider(_folders, _log, verifier: verifier).Prepare(definition, null, default);

        Assert.Equal("other.cat", Path.GetFileName(Assert.Single(verifier.Calls).Catalog));
    }

    [Fact]
    public void InfWithoutCatalogIsASignatureFailure()
    {
        byte[] archive = TestPackages.Zip((TestPackages.InfPath, "[Version]\r\nSignature=\"$Windows NT$\"\r\n"));
        File.WriteAllBytes(Path.Combine(_folders.OfflineFolder, "pkg.zip"), archive);

        var ex = Assert.Throws<PrinterSetupException>(() => TestPackages.Provider(_folders, _log).Prepare(TestPackages.Definition(archive), null, default));

        Assert.Equal(FindingCode.DriverSignatureInvalid, ex.Code);
        Assert.Contains("names no catalog", ex.Message);
    }

    [Fact]
    public void UntrustedCatalogIsRejectedWithItsStatusCodeAndTheExtractionIsRemoved()
    {
        byte[] archive = TestPackages.Archive();
        File.WriteAllBytes(Path.Combine(_folders.OfflineFolder, "pkg.zip"), archive);
        var verifier = new StubCatalogVerifier { Trusted = false };

        var ex = Assert.Throws<PrinterSetupException>(() =>
            TestPackages.Provider(_folders, _log, verifier: verifier).Prepare(TestPackages.Definition(archive), null, default));

        Assert.Equal(FindingCode.DriverSignatureInvalid, ex.Code);
        Assert.Equal(unchecked((int)0x800B0109), ex.Win32Error);
        Assert.Contains("0x800B0109", ex.Message);
        Assert.Contains(StubCatalogVerifier.SignerSubject, ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_folders.ExtractionRoot));
    }

    [Fact]
    public void SignerThatIsNotApprovedIsRejected()
    {
        byte[] archive = TestPackages.Archive();
        File.WriteAllBytes(Path.Combine(_folders.OfflineFolder, "pkg.zip"), archive);
        var verifier = new StubCatalogVerifier { Subject = "CN=Test Signer Ltd, O=Other", CommonName = "Test Signer Ltd" };

        var ex = Assert.Throws<PrinterSetupException>(() =>
            TestPackages.Provider(_folders, _log, verifier: verifier).Prepare(TestPackages.Definition(archive), null, default));

        Assert.Equal(FindingCode.DriverSignatureInvalid, ex.Code);
        Assert.Contains("not an approved signer", ex.Message);
    }

    [Fact]
    public void InfNotListedInTheCatalogIsRejected()
    {
        byte[] archive = TestPackages.Archive();
        File.WriteAllBytes(Path.Combine(_folders.OfflineFolder, "pkg.zip"), archive);
        var verifier = new StubCatalogVerifier { Membership = CatalogMembershipStatus.NotMember };

        var ex = Assert.Throws<PrinterSetupException>(() =>
            TestPackages.Provider(_folders, _log, verifier: verifier).Prepare(TestPackages.Definition(archive), null, default));

        Assert.Equal(FindingCode.DriverSignatureInvalid, ex.Code);
        Assert.Contains("not covered by the catalog", ex.Message);
    }

    [Fact]
    public void UndeterminedCatalogMembershipIsAWarningOnly()
    {
        byte[] archive = TestPackages.Archive();
        File.WriteAllBytes(Path.Combine(_folders.OfflineFolder, "pkg.zip"), archive);
        var verifier = new StubCatalogVerifier { Membership = CatalogMembershipStatus.Unknown };

        TestPackages.Provider(_folders, _log, verifier: verifier).Prepare(TestPackages.Definition(archive), null, default);

        Assert.Contains(_log.Entries, e => e.EventName == "driver.package.catalog.unknown" && e.Level == Core.Logging.LogLevel.Warning);
    }

    [Fact]
    public void OlderExtractionsOfTheSamePackageAreDeletedAndOthersKept()
    {
        byte[] archive = TestPackages.Archive();
        var definition = TestPackages.Definition(archive);
        File.WriteAllBytes(Path.Combine(_folders.OfflineFolder, "pkg.zip"), archive);
        string prefix = definition.Sha256.ToLowerInvariant()[..16];
        string older = Directory.CreateDirectory(Path.Combine(_folders.ExtractionRoot, prefix + "-20200101000000")).FullName;
        File.WriteAllText(Path.Combine(older, "old.inf"), "old");
        string unrelated = Directory.CreateDirectory(Path.Combine(_folders.ExtractionRoot, "0000000000000000-20200101000000")).FullName;

        PreparedDriverPackage prepared = TestPackages.Provider(_folders, _log).Prepare(definition, null, default);

        Assert.False(Directory.Exists(older));
        Assert.True(Directory.Exists(unrelated));
        Assert.True(Directory.Exists(prepared.ExtractedRoot));
    }

    [Fact]
    public void RepeatedPreparationUsesANewExtractionFolderEachTime()
    {
        byte[] archive = TestPackages.Archive();
        var definition = TestPackages.Definition(archive);
        File.WriteAllBytes(Path.Combine(_folders.OfflineFolder, "pkg.zip"), archive);
        var provider = TestPackages.Provider(_folders, _log);

        PreparedDriverPackage first = provider.Prepare(definition, null, default);
        PreparedDriverPackage second = provider.Prepare(definition, null, default);

        Assert.NotEqual(first.ExtractedRoot, second.ExtractedRoot);
        Assert.False(Directory.Exists(first.ExtractedRoot));
        Assert.True(File.Exists(second.InfPath));
    }

    [Fact]
    public void FileNameWithAFolderIsAConfigurationError()
    {
        var definition = TestPackages.Definition(TestPackages.Archive()) with { FileName = "..\\pkg.zip" };

        var ex = Assert.Throws<PrinterSetupException>(() => TestPackages.Provider(_folders, _log).Prepare(definition, null, default));

        Assert.Equal(FindingCode.ConfigurationInvalid, ex.Code);
    }

    [Fact]
    public void CancellationStopsPreparation()
    {
        byte[] archive = TestPackages.Archive();
        File.WriteAllBytes(Path.Combine(_folders.OfflineFolder, "pkg.zip"), archive);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            TestPackages.Provider(_folders, _log).Prepare(TestPackages.Definition(archive), null, cancelled.Token));
    }

    [Theory]
    [InlineData("Test Signer", true)]
    [InlineData("CN=Test Signer", true)]
    [InlineData(StubCatalogVerifier.SignerSubject, true)]
    [InlineData("  Test Signer  ", true)]
    [InlineData("test signer", false)]
    [InlineData("Test", false)]
    [InlineData("O=Test Organization", false)]
    [InlineData("", false)]
    public void SignerMatchingIsExactOnTheSubjectOrTheCommonName(string trusted, bool expected) =>
        Assert.Equal(expected, DriverPackageProvider.IsApprovedSigner(StubCatalogVerifier.SignerSubject, StubCatalogVerifier.SignerCommonName, [trusted]));
}
