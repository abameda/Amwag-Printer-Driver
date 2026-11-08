using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.Windows.Drivers;

/// <summary>
/// Downloads an approved archive over HTTPS from the official hosts only. Redirects are followed by hand so that every hop
/// is checked against <see cref="OfficialDownloadHosts"/>; the body is streamed to a partial file and rejected as soon as
/// it exceeds the approved size.
/// </summary>
internal sealed class PackageDownloader
{
    private const int MaxRedirects = 5;
    private const int BufferSize = 256 * 1024;

    private static readonly ProductInfoHeaderValue UserAgent =
        new("AMWAG-Printer-Setup", typeof(PackageDownloader).Assembly.GetName().Version?.ToString(3) ?? "1.0");

    private readonly HttpMessageHandler _handler;
    private readonly ILog _log;

    public PackageDownloader(HttpMessageHandler handler, ILog log)
    {
        _handler = handler;
        _log = log;
    }

    /// <summary>
    /// Writes the verified archive to <paramref name="partialPath"/> (size and SHA-256 checked). Throws
    /// <see cref="PrinterSetupException"/>: DriverPackageUnavailable for policy, HTTP and network failures,
    /// DriverPackageIntegrityFailed when the content is not the approved file. The partial file is removed on failure.
    /// </summary>
    public async Task DownloadAsync(
        DriverPackageDefinition package,
        string partialPath,
        TimeSpan timeout,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(package.DownloadUrl, UriKind.Absolute, out Uri? uri))
            throw Unavailable($"the download URL '{package.DownloadUrl}' is not a valid absolute URL");

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        bool completed = false;
        try
        {
            using var client = new HttpClient(_handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            using HttpResponseMessage response = await GetFollowingRedirectsAsync(client, uri, timeoutSource.Token).ConfigureAwait(false);
            await CopyVerifiedAsync(package, response, partialPath, progress, timeoutSource.Token).ConfigureAwait(false);
            completed = true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable($"the download did not complete within {timeout.TotalSeconds:0} seconds");
        }
        catch (HttpRequestException ex)
        {
            throw Unavailable(DescribeNetworkFailure(ex), ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Unavailable($"the transfer was interrupted or could not be written: {ex.Message.TrimEnd('.')}", ex);
        }
        finally
        {
            if (!completed)
                TryDelete(partialPath);
        }
    }

    private async Task<HttpResponseMessage> GetFollowingRedirectsAsync(HttpClient client, Uri uri, CancellationToken cancellationToken)
    {
        for (int redirects = 0; ; redirects++)
        {
            if (!OfficialDownloadHosts.IsAllowed(uri, out string reason))
                throw Unavailable($"{uri.GetLeftPart(UriPartial.Path)} is not an allowed download source: {reason}");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.Add(UserAgent);
            _log.Info("driver.package.download.request", "Requesting the driver archive.",
                new Dictionary<string, object?> { ["url"] = uri.GetLeftPart(UriPartial.Path), ["redirects"] = redirects });

            HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!IsRedirect(response.StatusCode))
            {
                if (response.IsSuccessStatusCode)
                    return response;

                string status = $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim();
                response.Dispose();
                throw Unavailable($"{uri.Host} answered HTTP {status}");
            }

            Uri? location = response.Headers.Location;
            response.Dispose();
            if (location is null)
                throw Unavailable($"{uri.Host} sent a redirect without a location");

            if (redirects == MaxRedirects)
                throw Unavailable($"{uri.Host} sent more than {MaxRedirects} redirects");

            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
        }
    }

    private static async Task CopyVerifiedAsync(
        DriverPackageDefinition package,
        HttpResponseMessage response,
        string partialPath,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        if (string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase))
            throw Unavailable("the server returned a web page instead of the archive (a proxy or sign-in page may be intercepting the download)");

        long? announced = response.Content.Headers.ContentLength;
        if (announced is long length && length != package.SizeBytes)
            throw IntegrityFailed($"the server announced {length} bytes, but the approved archive has {package.SizeBytes} bytes");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long received = 0;
        await using (Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var file = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
        {
            byte[] buffer = new byte[BufferSize];
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                received += read;
                if (received > package.SizeBytes)
                    throw IntegrityFailed($"the server sent more than the approved {package.SizeBytes} bytes");

                hash.AppendData(buffer, 0, read);
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress?.Invoke(received);
            }
        }

        if (received != package.SizeBytes)
            throw IntegrityFailed($"the download ended after {received} of {package.SizeBytes} bytes");

        string actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actual, package.Sha256, StringComparison.OrdinalIgnoreCase))
            throw IntegrityFailed($"the downloaded archive has SHA-256 {actual}, but the approved archive has {package.Sha256.ToLowerInvariant()}");
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static string DescribeNetworkFailure(HttpRequestException ex)
    {
        string message = ex.Message.TrimEnd('.');
        string detail = ex.InnerException is { } inner && inner.Message != ex.Message ? $"{message} ({inner.Message.TrimEnd('.')})" : message;
        return ex.HttpRequestError == HttpRequestError.Unknown ? detail : $"{ex.HttpRequestError}: {detail}";
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover partial file is never used: the next download recreates it and only verified files are moved into place.
        }
    }

    private static PrinterSetupException Unavailable(string reason, Exception? inner = null) =>
        new(FindingCode.DriverPackageUnavailable, reason, inner: inner);

    private static PrinterSetupException IntegrityFailed(string reason) =>
        new(FindingCode.DriverPackageIntegrityFailed, reason);
}
