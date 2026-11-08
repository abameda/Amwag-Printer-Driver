using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Principal;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.Windows.Drivers;

/// <summary>Locates approved driver archives (offline folder, cache, approved URL), verifies size, SHA-256 and signature, and extracts them.</summary>
public sealed class DriverPackageProvider : IDriverPackageProvider
{
    private const int HashBufferSize = 1024 * 1024;
    private static readonly TimeSpan DefaultDownloadTimeout = TimeSpan.FromSeconds(900);

    private readonly DriverSourceConfig _source;
    private readonly string _executableDirectory;
    private readonly ILog _log;
    private readonly string _cacheDirectory;
    private readonly string _extractionRoot;
    private readonly PackageDownloader _downloader;
    private readonly CatalogVerifier _verifyCatalog;
    private readonly bool _isElevated;

    /// <summary>
    /// Uses %ProgramData%\AMWAG\PrinterSetup\DriverCache and ...\Packages. Folders are hardened (administrators only)
    /// when the process is elevated.
    /// </summary>
    public DriverPackageProvider(DriverSourceConfig source, string executableDirectory, ILog log)
        : this(
            source,
            executableDirectory,
            log,
            DefaultDirectory("DriverCache"),
            DefaultDirectory("Packages"),
            CreateHttpHandler(),
            CatalogVerification.VerifyWithWindows,
            IsProcessElevated())
    {
    }

    internal DriverPackageProvider(
        DriverSourceConfig source,
        string executableDirectory,
        ILog log,
        string cacheDirectory,
        string extractionRoot,
        HttpMessageHandler httpHandler,
        CatalogVerifier verifyCatalog,
        bool isElevated)
    {
        _source = source;
        _executableDirectory = executableDirectory;
        _log = log;
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
        _extractionRoot = Path.GetFullPath(extractionRoot);
        _downloader = new PackageDownloader(httpHandler, log);
        _verifyCatalog = verifyCatalog;
        _isElevated = isElevated;
    }

    public PreparedDriverPackage Prepare(DriverPackageDefinition package, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.Approved)
        {
            throw new PrinterSetupException(
                FindingCode.DriverPackageNotApproved,
                $"The driver package '{package.Id}' is not approved in the configuration, so it is neither downloaded nor installed.");
        }

        ValidateDefinition(package);
        cancellationToken.ThrowIfCancellationRequested();
        string sha256 = package.Sha256.ToLowerInvariant();
        _log.Info("driver.package.prepare.start", "Preparing the approved driver package.", Fields(package,
            ("fileName", package.FileName), ("sizeBytes", package.SizeBytes), ("sha256", sha256)));

        using VerifiedArchive archive = Acquire(package, sha256, progress, cancellationToken);
        string extracted = Extract(package, sha256, archive, progress, cancellationToken);
        bool prepared = false;
        try
        {
            string infPath = ResolveInf(package, extracted);
            string catalogPath = ResolveCatalog(package, infPath, extracted);
            SignatureInfo signature = VerifyCatalog(package, catalogPath, infPath, progress);
            var result = new PreparedDriverPackage(package, archive.Path, archive.Origin, extracted, infPath, signature);
            prepared = true;
            _log.Info("driver.package.prepared", "The driver package is verified and extracted.", Fields(package,
                ("origin", archive.Origin.ToString()), ("archive", archive.Path), ("extractedRoot", extracted), ("inf", infPath),
                ("catalog", catalogPath), ("signer", signature.SignerSubject), ("thumbprint", signature.Thumbprint)));
            return result;
        }
        finally
        {
            if (!prepared)
                DeleteOwnExtraction(extracted);
        }
    }

    /// <summary>Exact match of a configured signer against the full subject or the CN (bare or as "CN=...").</summary>
    internal static bool IsApprovedSigner(string? subject, string? commonName, IReadOnlyList<string> trustedSigners)
    {
        foreach (string entry in trustedSigners)
        {
            string trusted = entry.Trim();
            if (trusted.Length == 0)
                continue;

            if (string.Equals(trusted, subject, StringComparison.Ordinal))
                return true;

            if (commonName is not null &&
                (string.Equals(trusted, commonName, StringComparison.Ordinal) || string.Equals(trusted, "CN=" + commonName, StringComparison.Ordinal)))
                return true;
        }

        return false;
    }

    private static void ValidateDefinition(DriverPackageDefinition package)
    {
        string? problem =
            package.SizeBytes <= 0 ? "sizeBytes must be greater than zero"
            : package.Sha256.Length != 64 || !package.Sha256.All(char.IsAsciiHexDigit) ? "sha256 must be 64 hexadecimal characters"
            : !IsPlainFileName(package.FileName) ? $"fileName '{package.FileName}' must be a file name without a folder"
            : string.IsNullOrWhiteSpace(package.InfPath) ? "infPath is empty"
            : null;
        if (problem is not null)
            throw new PrinterSetupException(FindingCode.ConfigurationInvalid, $"The driver package '{package.Id}' is misconfigured: {problem}.");
    }

    private VerifiedArchive Acquire(DriverPackageDefinition package, string sha256, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        var notes = new CandidateNotes();
        string? offlinePath = OfflineArchivePath(package);
        if (offlinePath is null)
            notes.Add("no offline folder is configured");
        else if (TryOpenVerified(offlinePath, PackageOrigin.OfflineFolder, package, notes, progress, cancellationToken) is { } offline)
            return offline;

        string cachedPath = Path.Combine(_cacheDirectory, sha256, package.FileName);
        if (TryOpenVerified(cachedPath, PackageOrigin.LocalCache, package, notes, progress, cancellationToken) is { } cached)
            return cached;

        if (!_source.AllowOnlineDownload)
            throw NoUsableCopy(package, notes, $"No valid copy of {package.FileName} was found ({notes}) and online download is disabled.");

        try
        {
            Download(package, cachedPath, progress, cancellationToken);
        }
        catch (PrinterSetupException ex) when (ex.Code == FindingCode.DriverPackageIntegrityFailed)
        {
            _log.Error("driver.package.download.rejected", "The downloaded archive is not the approved file.", ex, Fields(package));
            throw new PrinterSetupException(
                FindingCode.DriverPackageIntegrityFailed,
                $"The download of {package.FileName} was discarded because {ex.Message}. Other copies: {notes}.",
                inner: ex);
        }
        catch (PrinterSetupException ex) when (ex.Code == FindingCode.DriverPackageUnavailable)
        {
            throw NoUsableCopy(package, notes, $"No valid offline or cached copy of {package.FileName} was found ({notes}) and the download failed: {ex.Message}.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw NoUsableCopy(package, notes, $"No valid offline or cached copy of {package.FileName} was found ({notes}) and the download could not be stored in {_cacheDirectory}: {ex.Message}", ex);
        }

        return TryOpenVerified(cachedPath, PackageOrigin.Downloaded, package, notes, progress, cancellationToken)
            ?? throw new PrinterSetupException(
                FindingCode.DriverPackageIntegrityFailed,
                $"The downloaded archive {cachedPath} changed after it was verified ({notes}).");
    }

    private string? OfflineArchivePath(DriverPackageDefinition package)
    {
        if (string.IsNullOrWhiteSpace(_source.OfflineFolder))
            return null;

        string folder = Path.IsPathFullyQualified(_source.OfflineFolder)
            ? _source.OfflineFolder
            : Path.Combine(_executableDirectory, _source.OfflineFolder);
        return Path.GetFullPath(Path.Combine(folder, package.FileName));
    }

    /// <summary>
    /// Opens the candidate (others may only read it while it is open), checks its size and SHA-256 and keeps it open, so
    /// the bytes extracted later are exactly the bytes that were verified.
    /// </summary>
    private VerifiedArchive? TryOpenVerified(
        string path,
        PackageOrigin origin,
        DriverPackageDefinition package,
        CandidateNotes notes,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        string label = origin switch
        {
            PackageOrigin.OfflineFolder => "offline copy",
            PackageOrigin.LocalCache => "cached copy",
            _ => "downloaded copy",
        };

        if (!File.Exists(path))
        {
            notes.Add($"{label} {path} not found");
            return null;
        }

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            notes.Add($"{label} {path} could not be opened: {ex.Message.TrimEnd('.')}");
            _log.Warn("driver.package.archive.unreadable", "A copy of the driver archive could not be opened.", Fields(package, ("path", path)), ex);
            return null;
        }

        bool verified = false;
        try
        {
            if (stream.Length != package.SizeBytes)
            {
                notes.AddMismatch($"{label} {path} has {stream.Length} bytes instead of {package.SizeBytes}");
                _log.Warn("driver.package.archive.rejected", "A copy of the driver archive has the wrong size and is not used.",
                    Fields(package, ("path", path), ("origin", origin.ToString()), ("sizeBytes", stream.Length)));
                return null;
            }

            var reporter = new FractionReporter(progress, ProgressPhase.Verifying, $"Checking the SHA-256 of {path}");
            string actual = ComputeSha256(stream, package.SizeBytes, reporter, cancellationToken);
            if (!string.Equals(actual, package.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                notes.AddMismatch($"{label} {path} has SHA-256 {actual} instead of {package.Sha256.ToLowerInvariant()}");
                _log.Warn("driver.package.archive.rejected", "A copy of the driver archive has the wrong SHA-256 and is not used.",
                    Fields(package, ("path", path), ("origin", origin.ToString()), ("sha256", actual)));
                return null;
            }

            stream.Position = 0;
            verified = true;
            _log.Info("driver.package.archive.verified", "The driver archive matches the approved size and SHA-256.",
                Fields(package, ("path", path), ("origin", origin.ToString())));
            return new VerifiedArchive(path, origin, stream);
        }
        catch (IOException ex)
        {
            notes.Add($"{label} {path} could not be read: {ex.Message.TrimEnd('.')}");
            _log.Warn("driver.package.archive.unreadable", "A copy of the driver archive could not be read.", Fields(package, ("path", path)), ex);
            return null;
        }
        finally
        {
            if (!verified)
                stream.Dispose();
        }
    }

    private static string ComputeSha256(Stream stream, long length, FractionReporter reporter, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[HashBufferSize];
        long done = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
            done += read;
            reporter.Report((double)done / length);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private void Download(DriverPackageDefinition package, string cachedPath, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        EnsureSecureRoot(_cacheDirectory);
        ProtectedDirectory.EnsureRoot(Path.GetDirectoryName(cachedPath)!, _isElevated, _log);
        string partialPath = cachedPath + ".partial";
        TimeSpan timeout = _source.DownloadTimeoutSeconds > 0 ? TimeSpan.FromSeconds(_source.DownloadTimeoutSeconds) : DefaultDownloadTimeout;
        var reporter = new FractionReporter(progress, ProgressPhase.Downloading, $"Downloading {package.FileName}");
        reporter.Report(0);
        _log.Info("driver.package.download.start", "Downloading the driver archive from the approved URL.",
            Fields(package, ("url", package.DownloadUrl), ("timeoutSeconds", timeout.TotalSeconds)));

        Task.Run(
                () => _downloader.DownloadAsync(package, partialPath, timeout, received => reporter.Report((double)received / package.SizeBytes), cancellationToken),
                cancellationToken)
            .GetAwaiter()
            .GetResult();

        File.Move(partialPath, cachedPath, overwrite: true);
        _log.Info("driver.package.download.completed", "The driver archive was downloaded and verified.", Fields(package, ("path", cachedPath)));
    }

    private string Extract(DriverPackageDefinition package, string sha256, VerifiedArchive archive, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken)
    {
        string target;
        try
        {
            EnsureSecureRoot(_extractionRoot);
            target = CreateExtractionDirectory(sha256);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PrinterSetupException(FindingCode.OperationFailed, $"No extraction folder could be created under {_extractionRoot}: {ex.Message}", inner: ex);
        }

        _log.Info("driver.package.extract.start", "Extracting the driver archive.", Fields(package, ("archive", archive.Path), ("target", target)));
        var reporter = new FractionReporter(progress, ProgressPhase.Extracting, $"Extracting {package.FileName}");
        bool extracted = false;
        try
        {
            SafeZipExtractor.Extract(
                archive.Stream,
                target,
                SafeZipExtractor.DefaultMaxTotalBytes,
                SafeZipExtractor.DefaultMaxEntries,
                (written, total) => reporter.Report(total == 0 ? 1 : (double)written / total),
                cancellationToken,
                PackageEntries(package));
            extracted = true;
        }
        catch (InvalidDataException ex)
        {
            _log.Error("driver.package.extract.rejected", "The driver archive is not a safe, valid ZIP file.", ex, Fields(package, ("archive", archive.Path)));
            throw new PrinterSetupException(FindingCode.DriverPackageIntegrityFailed, $"The archive {archive.Path} was rejected: {ex.Message}", inner: ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error("driver.package.extract.failed", "The driver archive could not be extracted.", ex, Fields(package, ("target", target)));
            throw new PrinterSetupException(FindingCode.OperationFailed, $"The archive {archive.Path} could not be extracted to {target}: {ex.Message}", inner: ex);
        }
        finally
        {
            if (!extracted)
                DeleteOwnExtraction(target);
        }

        _log.Info("driver.package.extract.completed", "The driver archive was extracted.", Fields(package, ("target", target)));
        RemoveOlderExtractions(sha256, target);
        return target;
    }

    /// <summary>
    /// Entries the driver store needs: the INF's folder with its subfolders (where printer INFs keep their source files) and the
    /// catalog. Installers, utilities and other architectures stay in the archive, which also keeps paths short.
    /// </summary>
    internal static Func<string, bool> PackageEntries(DriverPackageDefinition package)
    {
        string inf = Normalize(package.InfPath);
        int slash = inf.LastIndexOf('/');
        string folder = slash < 0 ? "" : inf[..(slash + 1)];
        string? catalog = string.IsNullOrWhiteSpace(package.CatalogPath) ? null : Normalize(package.CatalogPath);
        return name =>
        {
            string entry = Normalize(name);
            return entry.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ||
                (catalog is not null && entry.Equals(catalog, StringComparison.OrdinalIgnoreCase));
        };

        static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
    }

    private string CreateExtractionDirectory(string sha256)
    {
        string name = $"{sha256[..16]}-{DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
        string path = Path.Combine(_extractionRoot, name);
        for (int suffix = 1; Directory.Exists(path) || File.Exists(path); suffix++)
            path = Path.Combine(_extractionRoot, $"{name}-{suffix}");

        ProtectedDirectory.CreateNew(path, _isElevated);
        return path;
    }

    private void RemoveOlderExtractions(string sha256, string current)
    {
        List<string> older;
        try
        {
            older = Directory.EnumerateDirectories(_extractionRoot, sha256[..16] + "-*")
                .Where(d => !string.Equals(d, current, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("driver.package.cleanup.failed", "Older extraction folders could not be listed.", Fields(("root", _extractionRoot)), ex);
            return;
        }

        foreach (string directory in older)
        {
            try
            {
                if (!ProtectedDirectory.IsSafeToDelete(directory, _isElevated))
                {
                    _log.Warn("driver.package.cleanup.skipped", "An older extraction folder is not protected and is left in place.", Fields(("path", directory)));
                    continue;
                }

                Directory.Delete(directory, recursive: true);
                _log.Info("driver.package.cleanup.deleted", "An older extraction folder of the same package was deleted.", Fields(("path", directory)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn("driver.package.cleanup.failed", "An older extraction folder could not be deleted.", Fields(("path", directory)), ex);
            }
        }
    }

    /// <summary>Deletes an extraction folder this call created (never a pre-existing one).</summary>
    private void DeleteOwnExtraction(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn("driver.package.cleanup.failed", "An unusable extraction folder could not be deleted.", Fields(("path", directory)), ex);
        }
    }

    private static string ResolveInf(DriverPackageDefinition package, string extracted)
    {
        string infPath = ResolveInside(extracted, package.InfPath)
            ?? throw new PrinterSetupException(
                FindingCode.ConfigurationInvalid,
                $"The infPath '{package.InfPath}' of driver package '{package.Id}' must be a relative path inside the archive.");
        if (!File.Exists(infPath))
        {
            throw new PrinterSetupException(
                FindingCode.ConfigurationInvalid,
                $"The approved archive of driver package '{package.Id}' does not contain the INF '{package.InfPath}' named in the configuration.");
        }

        return infPath;
    }

    private static string ResolveCatalog(DriverPackageDefinition package, string infPath, string extracted)
    {
        if (!string.IsNullOrWhiteSpace(package.CatalogPath))
        {
            string configured = ResolveInside(extracted, package.CatalogPath)
                ?? throw new PrinterSetupException(
                    FindingCode.ConfigurationInvalid,
                    $"The catalogPath '{package.CatalogPath}' of driver package '{package.Id}' must be a relative path inside the archive.");
            return File.Exists(configured)
                ? configured
                : throw new PrinterSetupException(
                    FindingCode.ConfigurationInvalid,
                    $"The approved archive of driver package '{package.Id}' does not contain the catalog '{package.CatalogPath}' named in the configuration.");
        }

        string? name;
        try
        {
            name = InfVersionSection.ReadCatalogFile(infPath, package.Architecture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PrinterSetupException(FindingCode.OperationFailed, $"The INF {infPath} could not be read: {ex.Message}", inner: ex);
        }

        if (name is null)
            throw new PrinterSetupException(FindingCode.DriverSignatureInvalid, $"The INF {infPath} names no catalog file, so the package is not signed.");

        if (!IsPlainFileName(name))
            throw new PrinterSetupException(FindingCode.DriverSignatureInvalid, $"The INF {infPath} names an invalid catalog file '{name}'.");

        string catalog = Path.Combine(Path.GetDirectoryName(infPath)!, name);
        return File.Exists(catalog)
            ? catalog
            : throw new PrinterSetupException(FindingCode.DriverSignatureInvalid, $"The catalog file '{name}' named in {infPath} is missing.");
    }

    private SignatureInfo VerifyCatalog(DriverPackageDefinition package, string catalogPath, string infPath, IProgress<ProgressUpdate>? progress)
    {
        progress?.Report(new ProgressUpdate(StepId.InstallDriver, StepState.Running, ActionKind.PrepareDriverPackage, ProgressPhase.Verifying,
            $"Verifying the signature of {Path.GetFileName(catalogPath)}"));
        CatalogVerification verification = _verifyCatalog(catalogPath, infPath);
        SignatureInfo signature = verification.Signature;
        var data = Fields(package, ("catalog", catalogPath), ("status", NativeErrorText.Hex(signature.StatusCode)),
            ("signer", signature.SignerSubject), ("issuer", signature.IssuerSubject), ("thumbprint", signature.Thumbprint));

        if (!signature.IsTrusted)
        {
            _log.Error("driver.package.signature.untrusted", "The catalog signature is not trusted.", data: data);
            throw new PrinterSetupException(
                FindingCode.DriverSignatureInvalid,
                $"The catalog {catalogPath} is not trusted: {NativeErrorText.Hex(signature.StatusCode)} {signature.StatusText} (signer: {signature.SignerSubject ?? "none"}).",
                signature.StatusCode);
        }

        if (!IsApprovedSigner(signature.SignerSubject, verification.SignerCommonName, package.TrustedSigners))
        {
            _log.Error("driver.package.signature.unapproved", "The catalog is signed by a signer that is not approved.", data: data);
            string approved = package.TrustedSigners.Count == 0 ? "none configured" : string.Join("; ", package.TrustedSigners);
            throw new PrinterSetupException(
                FindingCode.DriverSignatureInvalid,
                $"The catalog {catalogPath} is signed by '{signature.SignerSubject}', which is not an approved signer of driver package '{package.Id}' (approved: {approved}).");
        }

        CatalogMembershipResult membership = verification.InfMembership;
        switch (membership.Status)
        {
            case CatalogMembershipStatus.NotMember:
                _log.Error("driver.package.catalog.notmember", "The INF is not listed in the catalog.", data: data);
                throw new PrinterSetupException(
                    FindingCode.DriverSignatureInvalid,
                    $"The INF {infPath} is not covered by the catalog {catalogPath}: {membership.Detail}");
            case CatalogMembershipStatus.Unknown:
                _log.Warn("driver.package.catalog.unknown",
                    "Whether the INF is listed in the catalog could not be checked; Windows verifies the package itself when it is staged.",
                    Fields(package, ("catalog", catalogPath), ("detail", membership.Detail)));
                break;
            default:
                _log.Info("driver.package.catalog.member", "The INF is listed in the signed catalog.", Fields(package, ("detail", membership.Detail)));
                break;
        }

        _log.Info("driver.package.signature.verified", "The catalog signature is trusted and the signer is approved.", data);
        return signature;
    }

    private static string? ResolveInside(string root, string relativePath)
    {
        string relative = relativePath.Trim().Replace('/', Path.DirectorySeparatorChar);
        if (relative.Length == 0 || Path.IsPathRooted(relative) || relative.Contains(':'))
            return null;

        string rootWithSeparator = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(rootWithSeparator, relative));
        return full.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static bool IsPlainFileName(string name) =>
        name.Length > 0 && name is not "." and not ".." && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private PrinterSetupException NoUsableCopy(DriverPackageDefinition package, CandidateNotes notes, string message, Exception? inner = null)
    {
        // A copy that exists but is not the approved file is an integrity problem, not merely a missing package.
        FindingCode code = notes.SawMismatch ? FindingCode.DriverPackageIntegrityFailed : FindingCode.DriverPackageUnavailable;
        _log.Error("driver.package.unavailable", "No usable copy of the driver archive was obtained.", inner,
            Fields(package, ("code", code.ToString()), ("detail", message)));
        return new PrinterSetupException(code, message, inner: inner);
    }

    private static Dictionary<string, object?> Fields(DriverPackageDefinition package, params (string Key, object? Value)[] values)
    {
        var fields = Fields(values);
        fields["package"] = package.Id;
        fields["version"] = package.Version;
        return fields;
    }

    private static Dictionary<string, object?> Fields(params (string Key, object? Value)[] values)
    {
        var fields = new Dictionary<string, object?>(values.Length + 2);
        foreach (var (key, value) in values)
            fields[key] = value;

        return fields;
    }

    private static string DefaultDirectory(string name) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AMWAG", "PrinterSetup", name);

    /// <summary>Roots under %ProgramData% are secured level by level (standard users can create folders there); others alone.</summary>
    private void EnsureSecureRoot(string root)
    {
        string programData = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        if (root.StartsWith(programData + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            ProtectedDirectory.EnsureChain(programData, root, _isElevated, _log);
        else
            ProtectedDirectory.EnsureRoot(root, _isElevated, _log);
    }

    private static SocketsHttpHandler CreateHttpHandler() => new()
    {
        // Redirects are followed by PackageDownloader so that every hop is checked against the official host list.
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(30),
        // Branch networks may use an authenticating proxy (system proxy settings are used by default).
        DefaultProxyCredentials = CredentialCache.DefaultCredentials,
    };

    private static bool IsProcessElevated()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private sealed record VerifiedArchive(string Path, PackageOrigin Origin, FileStream Stream) : IDisposable
    {
        public void Dispose() => Stream.Dispose();
    }

    /// <summary>What happened to each candidate, for the final error message.</summary>
    private sealed class CandidateNotes
    {
        private readonly List<string> _notes = [];

        public bool SawMismatch { get; private set; }

        public void Add(string note) => _notes.Add(note);

        public void AddMismatch(string note)
        {
            SawMismatch = true;
            _notes.Add(note);
        }

        public override string ToString() => string.Join("; ", _notes);
    }

    /// <summary>Reports a phase's fraction in steps of at least one percent.</summary>
    private sealed class FractionReporter(IProgress<ProgressUpdate>? progress, ProgressPhase phase, string detail)
    {
        private double _last = -1;

        public void Report(double fraction)
        {
            if (progress is null)
                return;

            fraction = Math.Clamp(fraction, 0, 1);
            if (fraction == _last || (fraction < 1 && fraction - _last < 0.01))
                return;

            _last = fraction;
            progress.Report(new ProgressUpdate(StepId.InstallDriver, StepState.Running, ActionKind.PrepareDriverPackage, phase, detail)
            {
                Fraction = fraction,
            });
        }
    }
}
