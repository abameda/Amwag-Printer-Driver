using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Tests.UI;

/// <summary>Spooler for UI tests: jobs, queues and the effective DEVMODE are scripted; every write throws.</summary>
internal sealed class StubSpooler : IPrintSpooler
{
    public Func<string, IReadOnlyList<PrintJobInfo>> Jobs { get; set; } = _ => [];
    public Func<IReadOnlyList<PrintQueueInfo>> Queues { get; set; } = () => [];
    public Func<string, DevModeScope, DevMode?> DevModes { get; set; } = (_, _) => null;
    public Func<string, int, DevMode?> JobDevModes { get; set; } = (_, _) => null;

    public SpoolerServiceStatus GetServiceStatus() => new(ServiceRunState.Running, ServiceStartMode.Automatic);
    public bool TryStartService(TimeSpan timeout, out string? failureReason) => throw Unexpected();
    public IReadOnlyList<PrintQueueInfo> GetQueues() => Queues();
    public IReadOnlyList<PrinterPortInfo> GetPorts() => [];
    public IReadOnlyList<PrinterDriverInfo> GetDrivers() => [];
    public IReadOnlyList<FormInfo> GetForms() => [];
    public void AddForm(FormInfo form) => throw Unexpected();
    public void SetForm(string formName, FormInfo form) => throw Unexpected();
    public void AddStandardTcpIpPort(TcpIpPortRequest request) => throw Unexpected();
    public void AddQueue(NewQueueRequest request) => throw Unexpected();
    public void SetQueueDriver(string queueName, string driverName) => throw Unexpected();
    public void SetQueuePort(string queueName, string portName) => throw Unexpected();
    public DriverCapabilities GetCapabilities(string queueName) => new() { QueueName = queueName };
    public DevMode? GetDevMode(string queueName, DevModeScope scope) => DevModes(queueName, scope);
    public DevMode ValidateDevMode(string queueName, DevMode requested) => throw Unexpected();
    public void SetDevMode(string queueName, DevModeScope scope, DevMode devMode) => throw Unexpected();
    public PrintCapabilitiesSummary GetPrintCapabilities(string queueName, DevMode? devMode) => new();
    public PrintTicketMediaInfo DescribePrintTicket(string queueName, DevMode devMode) => new();
    public IReadOnlyList<PrintJobInfo> GetJobs(string queueName) => Jobs(queueName);
    public DevMode? GetJobDevMode(string queueName, int jobId) => JobDevModes(queueName, jobId);

    private static InvalidOperationException Unexpected() => new("UI tests must never change the print system.");
}

/// <summary>A clock that only moves when slept on, so polling loops run instantly and deterministically.</summary>
internal sealed class ManualClock : IClock
{
    public DateTimeOffset Now { get; private set; } = new(2026, 11, 3, 14, 5, 0, TimeSpan.FromHours(2));

    public int Sleeps { get; private set; }

    public void Sleep(TimeSpan duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Sleeps++;
        Now += duration;
    }
}

internal sealed class MemoryLog : ILog
{
    private readonly List<(LogLevel Level, string Event, IReadOnlyDictionary<string, object?>? Data)> _entries = [];

    public string SessionId => "ui-tests";
    public string? FilePath { get; init; }

    public IReadOnlyList<(LogLevel Level, string Event, IReadOnlyDictionary<string, object?>? Data)> Entries
    {
        get { lock (_entries) return _entries.ToArray(); }
    }

    public void Write(LogLevel level, string eventName, string message, IReadOnlyDictionary<string, object?>? data = null, Exception? exception = null)
    {
        lock (_entries)
            _entries.Add((level, eventName, data));
    }
}

internal sealed class NoDevices : IDeviceInventory
{
    public IReadOnlyList<PnpPrinterDevice> GetPrinterDevices() => [];
}

internal sealed class FixedPlatform : IPlatformInspector
{
    public PlatformInfo Inspect() => new() { ProductName = "Windows 11 Pro", Build = 26100, Architecture = "x64", ProcessArchitecture = "x64" };
}

internal sealed class NoNetwork : INetworkPrinterProbe
{
    public NetworkIdentity Probe(string address, NetworkProbeOptions options, CancellationToken cancellationToken) => new() { Address = address };
}

internal sealed class NoPackages : IDriverPackageProvider
{
    public PreparedDriverPackage Prepare(DriverPackageDefinition package, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("UI tests never prepare driver packages.");
}

internal sealed class NoInstaller : IDriverInstaller
{
    public string StagePackage(PreparedDriverPackage package) => throw new InvalidOperationException("UI tests never install drivers.");
    public void InstallPrinterDriver(string driverStoreInfPath, string driverName, string environment) => throw new InvalidOperationException("UI tests never install drivers.");
    public DeviceInstallResult InstallForDevice(PreparedDriverPackage package, string hardwareId) => throw new InvalidOperationException("UI tests never install drivers.");
}

internal sealed class NeverElevate : IElevationBroker
{
    public bool IsElevated => false;

    public Task<ElevatedRunResult> RunMachineStepsAsync(WorkerRequest request, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken) =>
        Task.FromResult(new ElevatedRunResult(ElevationOutcome.Declined, ExitCodes.ElevationRequired));
}

internal static class TestRuntime
{
    public static DeploymentConfig Config { get; } = new()
    {
        SchemaVersion = 1,
        ConfigVersion = "test",
        Ticket = new TicketPaperConfig { FormName = "AMWAG Ticket", Width = 3.24m, Height = 6.70m, Unit = Core.Units.LengthUnit.Inch, DimensionsConfirmed = true },
        Models =
        [
            new ModelDefinition { Id = "P-4030DN", DisplayName = "TA P-4030DN", PreferredDriverName = "TA P-4030DN KX" },
            new ModelDefinition { Id = "P-4531DN", DisplayName = "TA P-4531DN", PreferredDriverName = "TA P-4531DN KX" },
            new ModelDefinition { Id = "P-5032DN", DisplayName = "TA P-5032DN", PreferredDriverName = "TA P-5032DN KX" },
        ],
    };

    public static AppRuntime Create(string? language = null, SetupMode mode = SetupMode.Setup, IPrintSpooler? spooler = null, ILog? log = null, CommandLineOptions? commandLine = null, DeploymentConfig? config = null)
    {
        log ??= new MemoryLog();
        var services = new SetupServices
        {
            Spooler = spooler ?? new StubSpooler(),
            Devices = new NoDevices(),
            Platform = new FixedPlatform(),
            Network = new NoNetwork(),
            Packages = new NoPackages(),
            Installer = new NoInstaller(),
            Log = log,
            Clock = new ManualClock(),
        };
        var options = new SetupOptions { Mode = mode, AppVersion = "1.0.0", ConfigSource = "test" };
        var environment = new FlowEnvironment(config ?? Config, null, options, services);
        return new AppRuntime(environment, new NeverElevate(), commandLine ?? new CommandLineOptions { Mode = mode, Language = language }, log, "1.0.0", AppContext.BaseDirectory);
    }
}

/// <summary>Runs WinForms test code on a dedicated STA thread (xUnit test threads are MTA).</summary>
internal static class Sta
{
    public static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
