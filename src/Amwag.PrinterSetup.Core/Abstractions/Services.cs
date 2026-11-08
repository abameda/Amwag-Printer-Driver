using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.Core.Abstractions;

/// <summary>Which default DEVMODE of a queue to read or write.</summary>
public enum DevModeScope
{
    /// <summary>The queue's global default (Printing Defaults, SetPrinter/GetPrinter level 8). Writing needs administrator rights.</summary>
    Global,
    /// <summary>The current user's per-user default (level 9). Null when the user has none. Writing needs only print access.</summary>
    PerUser,
    /// <summary>What applications get for the current user (DocumentProperties with DM_OUT_BUFFER and no input). Read-only.</summary>
    Effective,
}

public sealed record TcpIpPortRequest(string PortName, string HostAddress, int PortNumber, bool SnmpEnabled, string SnmpCommunity, int SnmpDeviceIndex = 1);

public sealed record NewQueueRequest(string Name, string DriverName, string PortName, string? Location = null, string? Comment = null, string? PrintProcessor = null, string Datatype = "RAW");

/// <summary>
/// The Windows print spooler. Read methods never modify the system. Write methods document their required rights and
/// throw <see cref="Errors.SpoolerException"/> carrying the Win32 error on failure.
/// </summary>
public interface IPrintSpooler
{
    SpoolerServiceStatus GetServiceStatus();

    /// <summary>Starts the Print Spooler service if it is stopped and not disabled (administrator). Returns false with a reason otherwise.</summary>
    bool TryStartService(TimeSpan timeout, out string? failureReason);

    IReadOnlyList<PrintQueueInfo> GetQueues();
    IReadOnlyList<PrinterPortInfo> GetPorts();
    /// <summary>Drivers installed for the native print environment (e.g. "Windows x64").</summary>
    IReadOnlyList<PrinterDriverInfo> GetDrivers();
    IReadOnlyList<FormInfo> GetForms();

    /// <summary>AddForm on the local print server (administrator).</summary>
    void AddForm(FormInfo form);
    /// <summary>SetForm on the local print server (administrator).</summary>
    void SetForm(string formName, FormInfo form);
    /// <summary>Creates a Standard TCP/IP port through XcvData "AddPort" (administrator).</summary>
    void AddStandardTcpIpPort(TcpIpPortRequest request);
    /// <summary>AddPrinter level 2 (administrator).</summary>
    void AddQueue(NewQueueRequest request);
    /// <summary>Changes the queue's driver via SetPrinter level 2 (administrator).</summary>
    void SetQueueDriver(string queueName, string driverName);
    /// <summary>Changes the queue's port via SetPrinter level 2 (administrator).</summary>
    void SetQueuePort(string queueName, string portName);

    DriverCapabilities GetCapabilities(string queueName);
    DevMode? GetDevMode(string queueName, DevModeScope scope);
    /// <summary>Lets the driver validate and merge a DEVMODE (DocumentProperties DM_IN_BUFFER | DM_OUT_BUFFER). Nothing is persisted.</summary>
    DevMode ValidateDevMode(string queueName, DevMode requested);
    /// <summary>Persists a default DEVMODE (Global: SetPrinter level 8, administrator; PerUser: level 9). Effective is not writable.</summary>
    void SetDevMode(string queueName, DevModeScope scope, DevMode devMode);

    /// <summary>Print Schema capabilities; never throws for driver problems (returns Available = false with Error).</summary>
    PrintCapabilitiesSummary GetPrintCapabilities(string queueName, DevMode? devMode);
    /// <summary>How the driver converts the DEVMODE to a PrintTicket; never throws for driver problems.</summary>
    PrintTicketMediaInfo DescribePrintTicket(string queueName, DevMode devMode);

    IReadOnlyList<PrintJobInfo> GetJobs(string queueName);

    /// <summary>
    /// The DEVMODE a spooled job was submitted with (GetJob level 2), or null when the job is gone or carries none.
    /// Lets IT see whether ticket software overrides the queue defaults at print time.
    /// </summary>
    DevMode? GetJobDevMode(string queueName, int jobId) => null;
}

/// <summary>Plug and Play printer devices (present and recently present), with USB port mapping.</summary>
public interface IDeviceInventory
{
    IReadOnlyList<PnpPrinterDevice> GetPrinterDevices();
}

public interface IPlatformInspector
{
    PlatformInfo Inspect();
}

public sealed record NetworkProbeOptions(int RawPort, bool UseSnmp, string SnmpCommunity, int TimeoutMilliseconds);

/// <summary>Identifies one network printer with a few low-impact requests (TCP connect to the raw port, SNMP GET). Never scans.</summary>
public interface INetworkPrinterProbe
{
    NetworkIdentity Probe(string address, NetworkProbeOptions options, CancellationToken cancellationToken);
}

public enum PackageOrigin { OfflineFolder, LocalCache, Downloaded }

public sealed record SignatureInfo(bool IsTrusted, string? SignerSubject, string? IssuerSubject, string? Thumbprint, int StatusCode, string StatusText);

/// <summary>An approved driver package that passed size, SHA-256 and signature checks and was extracted to a protected folder.</summary>
public sealed record PreparedDriverPackage(
    DriverPackageDefinition Definition,
    string ArchivePath,
    PackageOrigin Origin,
    string ExtractedRoot,
    string InfPath,
    SignatureInfo CatalogSignature);

/// <summary>Locates (offline folder, cache, then approved download URL), verifies and extracts approved driver packages.</summary>
public interface IDriverPackageProvider
{
    /// <summary>Throws <see cref="Errors.PrinterSetupException"/> (DriverPackageUnavailable, DriverPackageIntegrityFailed, DriverSignatureInvalid, DriverPackageNotApproved).</summary>
    PreparedDriverPackage Prepare(DriverPackageDefinition package, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken);
}

public sealed record DeviceInstallResult(bool Succeeded, bool RebootRequired, int? Win32Error, string Detail);

/// <summary>Installs printer drivers through documented package-aware APIs. All members need administrator rights.</summary>
public interface IDriverInstaller
{
    /// <summary>UploadPrinterDriverPackage: stages the package in the driver store; returns the driver-store INF path.</summary>
    string StagePackage(PreparedDriverPackage package);

    /// <summary>InstallPrinterDriverFromPackage for one INF model name.</summary>
    void InstallPrinterDriver(string driverStoreInfPath, string driverName, string environment);

    /// <summary>Installs the package on present Plug and Play devices with this hardware ID (UpdateDriverForPlugAndPlayDevices).</summary>
    DeviceInstallResult InstallForDevice(PreparedDriverPackage package, string hardwareId);
}

public interface IClock
{
    DateTimeOffset Now { get; }
    void Sleep(TimeSpan duration, CancellationToken cancellationToken);
}

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset Now => DateTimeOffset.Now;

    public void Sleep(TimeSpan duration, CancellationToken cancellationToken)
    {
        if (cancellationToken.WaitHandle.WaitOne(duration))
            cancellationToken.ThrowIfCancellationRequested();
    }
}

/// <summary>The system-facing services a <see cref="SetupSession"/> works through (real ones on Windows, fakes in tests).</summary>
public sealed class SetupServices
{
    public required IPrintSpooler Spooler { get; init; }
    public required IDeviceInventory Devices { get; init; }
    public required IPlatformInspector Platform { get; init; }
    public required INetworkPrinterProbe Network { get; init; }
    public required IDriverPackageProvider Packages { get; init; }
    public required IDriverInstaller Installer { get; init; }
    public required ILog Log { get; init; }
    public IClock Clock { get; init; } = SystemClock.Instance;
}
