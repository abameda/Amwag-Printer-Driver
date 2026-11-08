using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Windows.Drivers;

namespace Amwag.PrinterSetup.Windows.Tests.Drivers;

/// <summary>A unique temporary folder with the sub-folders a provider uses; deleted on dispose.</summary>
internal sealed class TempFolders : IDisposable
{
    public TempFolders()
    {
        Root = Path.Combine(Path.GetTempPath(), "amwag-drivers-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        ExecutableDirectory = Directory.CreateDirectory(Path.Combine(Root, "app")).FullName;
        OfflineFolder = Directory.CreateDirectory(Path.Combine(ExecutableDirectory, "drivers")).FullName;
    }

    public string Root { get; }
    public string ExecutableDirectory { get; }
    public string OfflineFolder { get; }
    public string CacheDirectory => Path.Combine(Root, "DriverCache");
    public string ExtractionRoot => Path.Combine(Root, "Packages");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed record LogEntry(LogLevel Level, string EventName, string Message, IReadOnlyDictionary<string, object?>? Data, Exception? Exception);

internal sealed class RecordingLog : ILog
{
    private readonly List<LogEntry> _entries = [];

    public string SessionId => "test";
    public string? FilePath => null;

    public IReadOnlyList<LogEntry> Entries
    {
        get
        {
            lock (_entries)
                return _entries.ToList();
        }
    }

    public bool Contains(string eventName) => Entries.Any(e => e.EventName == eventName);

    public void Write(LogLevel level, string eventName, string message, IReadOnlyDictionary<string, object?>? data = null, Exception? exception = null)
    {
        lock (_entries)
            _entries.Add(new LogEntry(level, eventName, message, data, exception));
    }
}

/// <summary>Answers requests from a callback and records them; never touches the network.</summary>
internal sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly List<Uri> _requests = [];

    public IReadOnlyList<Uri> Requests
    {
        get
        {
            lock (_requests)
                return _requests.ToList();
        }
    }

    public static FakeHttpHandler Failing() => new(_ => throw new InvalidOperationException("No request was expected."));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_requests)
            _requests.Add(request.RequestUri!);

        return Task.FromResult(respond(request));
    }
}

/// <summary>A body without Content-Length (like a chunked transfer), so the downloader must count the bytes itself.</summary>
internal sealed class UnsizedContent(byte[] body) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(body).AsTask();

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}

internal sealed class StubCatalogVerifier
{
    public const string SignerSubject = "CN=Test Signer, O=Test Organization, C=EG";
    public const string SignerCommonName = "Test Signer";

    public List<(string Catalog, string Inf)> Calls { get; } = [];
    public bool Trusted { get; init; } = true;
    public string Subject { get; init; } = SignerSubject;
    public string? CommonName { get; init; } = SignerCommonName;
    public CatalogMembershipStatus Membership { get; init; } = CatalogMembershipStatus.Member;

    public CatalogVerification Verify(string catalogPath, string infPath)
    {
        Calls.Add((catalogPath, infPath));
        var signature = new SignatureInfo(Trusted, Subject, "CN=Test CA", "ABCDEF", Trusted ? 0 : unchecked((int)0x800B0109), Trusted ? "ok" : "untrusted root");
        return new CatalogVerification(signature, CommonName, new CatalogMembershipResult(Membership, "stub"));
    }
}

internal static class TestPackages
{
    public const string InfPath = "pkg/64bit/OEMSETUP.INF";
    public const string DownloadUrl = "https://www.triumph-adler.com/caas/v1/media/pkg.zip";

    public static readonly string Inf = string.Join("\r\n",
        "; test INF",
        "[Version]",
        "Signature  = \"$Windows NT$\"",
        "Class      = Printer",
        "CatalogFile= TEST.CAT ; inline comment",
        "DriverVer  = 09/17/2026,8.7.0917.0",
        "",
        "[Manufacturer]",
        "TA=TA,NTamd64");

    /// <summary>A small package: the INF, its catalog (lower-case on disk, as in the real TA package) and a driver file.</summary>
    public static byte[] Archive() => Zip(
        ("pkg/", null),
        ("pkg/64bit/", null),
        (InfPath, Inf),
        ("pkg/64bit/test.cat", "not a real catalog"),
        ("pkg/64bit/driver.dll", new string('x', 4096)));

    public static byte[] Zip(params (string Name, string? Content)[] entries)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name);
                if (content is null)
                    continue;

                using Stream stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return buffer.ToArray();
    }

    public static DriverPackageDefinition Definition(byte[] archive) => new()
    {
        Id = "test-kx",
        DisplayName = "Test KX driver",
        Version = "8.7.0917",
        DownloadUrl = DownloadUrl,
        FileName = "pkg.zip",
        SizeBytes = archive.Length,
        Sha256 = Convert.ToHexString(SHA256.HashData(archive)),
        Environment = "Windows x64",
        Architecture = "x64",
        InfPath = InfPath,
        TrustedSigners = [StubCatalogVerifier.SignerCommonName],
        Approved = true,
    };

    public static DriverPackageProvider Provider(
        TempFolders folders,
        RecordingLog log,
        HttpMessageHandler? http = null,
        StubCatalogVerifier? verifier = null,
        bool allowDownload = false,
        string? offlineFolder = null) =>
        new(
            new DriverSourceConfig { OfflineFolder = offlineFolder ?? folders.OfflineFolder, AllowOnlineDownload = allowDownload, DownloadTimeoutSeconds = 30 },
            folders.ExecutableDirectory,
            log,
            folders.CacheDirectory,
            folders.ExtractionRoot,
            http ?? FakeHttpHandler.Failing(),
            (verifier ?? new StubCatalogVerifier()).Verify,
            isElevated: false);

    public static HttpResponseMessage Ok(byte[] body, bool sized = true) => new(HttpStatusCode.OK)
    {
        Content = sized ? new ByteArrayContent(body) : new UnsizedContent(body),
    };
}

internal sealed class RecordingProgress : IProgress<ProgressUpdate>
{
    private readonly List<ProgressUpdate> _updates = [];

    public IReadOnlyList<ProgressUpdate> Updates
    {
        get
        {
            lock (_updates)
                return _updates.ToList();
        }
    }

    public void Report(ProgressUpdate value)
    {
        lock (_updates)
            _updates.Add(value);
    }
}
