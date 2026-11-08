using System.Net;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.Windows.Tests.Drivers;

/// <summary>Downloads through a fake handler: nothing here reaches the network.</summary>
public sealed class DriverPackageDownloadTests : IDisposable
{
    private readonly TempFolders _folders = new();
    private readonly RecordingLog _log = new();
    private readonly byte[] _archive = TestPackages.Archive();

    public void Dispose() => _folders.Dispose();

    private string CachedPath => Path.Combine(_folders.CacheDirectory, TestPackages.Definition(_archive).Sha256.ToLowerInvariant(), "pkg.zip");

    [Fact]
    public void DownloadIsVerifiedCachedAndExtracted()
    {
        var http = new FakeHttpHandler(_ => TestPackages.Ok(_archive, sized: false));
        var progress = new RecordingProgress();

        PreparedDriverPackage prepared = TestPackages.Provider(_folders, _log, http, allowDownload: true)
            .Prepare(TestPackages.Definition(_archive), progress, default);

        Assert.Equal(PackageOrigin.Downloaded, prepared.Origin);
        Assert.Equal(CachedPath, prepared.ArchivePath);
        Assert.Equal(_archive, File.ReadAllBytes(CachedPath));
        Assert.False(File.Exists(CachedPath + ".partial"));
        Assert.Equal(new Uri(TestPackages.DownloadUrl), Assert.Single(http.Requests));
        Assert.True(File.Exists(prepared.InfPath));

        var downloading = progress.Updates.Where(u => u.Phase == ProgressPhase.Downloading).ToList();
        Assert.NotEmpty(downloading);
        Assert.All(downloading, u => Assert.Equal(ActionKind.PrepareDriverPackage, u.Action));
        Assert.Equal(1, downloading[^1].Fraction);
    }

    [Fact]
    public void SecondPreparationUsesTheCacheWithoutDownloading()
    {
        var http = new FakeHttpHandler(_ => TestPackages.Ok(_archive));
        TestPackages.Provider(_folders, _log, http, allowDownload: true).Prepare(TestPackages.Definition(_archive), null, default);

        PreparedDriverPackage second = TestPackages.Provider(_folders, _log, http, allowDownload: true).Prepare(TestPackages.Definition(_archive), null, default);

        Assert.Equal(PackageOrigin.LocalCache, second.Origin);
        Assert.Single(http.Requests);
    }

    [Fact]
    public void NotFoundIsUnavailableWithAClearDetail()
    {
        var http = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { ReasonPhrase = "Not Found" });

        var ex = Assert.Throws<PrinterSetupException>(() =>
            TestPackages.Provider(_folders, _log, http, allowDownload: true).Prepare(TestPackages.Definition(_archive), null, default));

        Assert.Equal(FindingCode.DriverPackageUnavailable, ex.Code);
        Assert.Contains("the download failed", ex.Message);
        Assert.Contains("404", ex.Message);
        Assert.False(File.Exists(CachedPath));
        Assert.False(File.Exists(CachedPath + ".partial"));
    }

    [Fact]
    public void NetworkFailureIsUnavailable()
    {
        var http = new FakeHttpHandler(_ => throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known."));

        var ex = Assert.Throws<PrinterSetupException>(() =>
            TestPackages.Provider(_folders, _log, http, allowDownload: true).Prepare(TestPackages.Definition(_archive), null, default));

        Assert.Equal(FindingCode.DriverPackageUnavailable, ex.Code);
        Assert.Contains("No valid offline or cached copy", ex.Message);
        Assert.Contains("No such host is known", ex.Message);
    }

    [Fact]
    public void HashMismatchIsAnIntegrityFailureAndThePartialFileIsDeleted()
    {
        byte[] other = (byte[])_archive.Clone();
        other[^10] ^= 0xFF;
        var http = new FakeHttpHandler(_ => TestPackages.Ok(other, sized: false));

        var ex = Assert.Throws<PrinterSetupException>(() =>
            TestPackages.Provider(_folders, _log, http, allowDownload: true).Prepare(TestPackages.Definition(_archive), null, default));

        Assert.Equal(FindingCode.DriverPackageIntegrityFailed, ex.Code);
        Assert.Contains("SHA-256", ex.Message);
        Assert.False(File.Exists(CachedPath));
        Assert.False(File.Exists(CachedPath + ".partial"));
    }

    [Fact]
    public void OversizeBodyIsAbortedAsAnIntegrityFailure()
    {
        byte[] oversize = [.. _archive, .. new byte[1024 * 1024]];
        var http = new FakeHttpHandler(_ => TestPackages.Ok(oversize, sized: false));

        var ex = Assert.Throws<PrinterSetupException>(() =>
            TestPackages.Provider(_folders, _log, http, allowDownload: true).Prepare(TestPackages.Definition(_archive), null, default));

        Assert.Equal(FindingCode.DriverPackageIntegrityFailed, ex.Code);
        Assert.Contains("more than the approved", ex.Message);
        Assert.False(File.Exists(CachedPath + ".partial"));
        Assert.False(File.Exists(CachedPath));
    }

    [Fact]
    public void AnnouncedLengthThatDiffersIsRejectedBeforeTheBodyIsRead()
    {
        var http = new FakeHttpHandler(_ => TestPackages.Ok([.. _archive, 1, 2, 3]));

        var ex = Assert.Throws<PrinterSetupException>(() =>
            TestPackages.Provider(_folders, _log, http, allowDownload: true).Prepare(TestPackages.Definition(_archive), null, default));

        Assert.Equal(FindingCode.DriverPackageIntegrityFailed, ex.Code);
        Assert.Contains("announced", ex.Message);
    }

    [Fact]
    public void PlainHttpUrlIsRejectedWithoutARequest()
    {
        var http = FakeHttpHandler.Failing();
        var definition = TestPackages.Definition(_archive) with { DownloadUrl = "http://www.triumph-adler.com/caas/v1/media/pkg.zip" };

        var ex = Assert.Throws<PrinterSetupException>(() =>
            TestPackages.Provider(_folders, _log, http, allowDownload: true).Prepare(definition, null, default));

        Assert.Equal(FindingCode.DriverPackageUnavailable, ex.Code);
        Assert.Contains("does not use HTTPS", ex.Message);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public void UnofficialHostIsRejectedWithoutARequest()
    {
        var http = FakeHttpHandler.Failing();
        var definition = TestPackages.Definition(_archive) with { DownloadUrl = "https://downloads.example.com/pkg.zip" };

        var ex = Assert.Throws<PrinterSetupException>(() =>
            TestPackages.Provider(_folders, _log, http, allowDownload: true).Prepare(definition, null, default));

        Assert.Equal(FindingCode.DriverPackageUnavailable, ex.Code);
        Assert.Empty(http.Requests);
    }

    [Fact]
    public void RedirectToAnUnofficialHostIsRejected()
    {
        var http = new FakeHttpHandler(request =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://mirror.example.net/pkg.zip");
            return redirect;
        });

        var ex = Assert.Throws<PrinterSetupException>(() =>
            TestPackages.Provider(_folders, _log, http, allowDownload: true).Prepare(TestPackages.Definition(_archive), null, default));

        Assert.Equal(FindingCode.DriverPackageUnavailable, ex.Code);
        Assert.Contains("mirror.example.net", ex.Message);
        Assert.Single(http.Requests);
    }

    [Fact]
    public void RedirectToPlainHttpOnAnOfficialHostIsRejected()
    {
        var http = new FakeHttpHandler(request =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
            redirect.Headers.Location = new Uri("http://www.utax.com/pkg.zip");
            return redirect;
        });

        var ex = Assert.Throws<PrinterSetupException>(() =>
            TestPackages.Provider(_folders, _log, http, allowDownload: true).Prepare(TestPackages.Definition(_archive), null, default));

        Assert.Equal(FindingCode.DriverPackageUnavailable, ex.Code);
        Assert.Single(http.Requests);
    }

    [Fact]
    public void RelativeRedirectOnAnOfficialHostIsFollowed()
    {
        var http = new FakeHttpHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/final/pkg.zip", StringComparison.Ordinal))
                return TestPackages.Ok(_archive);

            var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            redirect.Headers.Location = new Uri("/final/pkg.zip", UriKind.Relative);
            return redirect;
        });

        PreparedDriverPackage prepared = TestPackages.Provider(_folders, _log, http, allowDownload: true)
            .Prepare(TestPackages.Definition(_archive), null, default);

        Assert.Equal(PackageOrigin.Downloaded, prepared.Origin);
        Assert.Equal(2, http.Requests.Count);
        Assert.Equal("https://www.triumph-adler.com/final/pkg.zip", http.Requests[1].ToString());
    }

    [Fact]
    public void EndlessRedirectsAreCut()
    {
        var http = new FakeHttpHandler(request =>
        {
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri(request.RequestUri!, "?again=" + Guid.NewGuid().ToString("N"));
            return redirect;
        });

        var ex = Assert.Throws<PrinterSetupException>(() =>
            TestPackages.Provider(_folders, _log, http, allowDownload: true).Prepare(TestPackages.Definition(_archive), null, default));

        Assert.Equal(FindingCode.DriverPackageUnavailable, ex.Code);
        Assert.Contains("redirects", ex.Message);
        Assert.Equal(6, http.Requests.Count);
    }

    [Fact]
    public void WebPageInsteadOfTheArchiveIsUnavailable()
    {
        var http = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>Sign in to the proxy</html>", System.Text.Encoding.UTF8, "text/html"),
        });

        var ex = Assert.Throws<PrinterSetupException>(() =>
            TestPackages.Provider(_folders, _log, http, allowDownload: true).Prepare(TestPackages.Definition(_archive), null, default));

        Assert.Equal(FindingCode.DriverPackageUnavailable, ex.Code);
        Assert.Contains("web page", ex.Message);
    }

    [Fact]
    public void CorruptOfflineCopyIsSkippedAndTheDownloadIsUsed()
    {
        byte[] tampered = (byte[])_archive.Clone();
        tampered[^10] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(_folders.OfflineFolder, "pkg.zip"), tampered);
        var http = new FakeHttpHandler(_ => TestPackages.Ok(_archive));

        PreparedDriverPackage prepared = TestPackages.Provider(_folders, _log, http, allowDownload: true)
            .Prepare(TestPackages.Definition(_archive), null, default);

        Assert.Equal(PackageOrigin.Downloaded, prepared.Origin);
        Assert.Contains(_log.Entries, e => e.EventName == "driver.package.archive.rejected");
    }

    [Fact]
    public void CorruptOfflineCopyAndFailedDownloadReportBoth()
    {
        byte[] tampered = (byte[])_archive.Clone();
        tampered[^10] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(_folders.OfflineFolder, "pkg.zip"), tampered);
        var http = new FakeHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var ex = Assert.Throws<PrinterSetupException>(() =>
            TestPackages.Provider(_folders, _log, http, allowDownload: true).Prepare(TestPackages.Definition(_archive), null, default));

        Assert.Equal(FindingCode.DriverPackageIntegrityFailed, ex.Code);
        Assert.Contains("offline copy", ex.Message);
        Assert.Contains("SHA-256", ex.Message);
        Assert.Contains("503", ex.Message);
    }
}
