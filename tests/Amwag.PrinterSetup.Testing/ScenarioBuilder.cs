using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Core.Units;

namespace Amwag.PrinterSetup.Testing;

/// <summary>
/// Fluent setup of a branch PC for scenario tests. Starts as a healthy Windows 11 x64 machine with a running spooler,
/// the standard forms and a "Microsoft Print to PDF" queue, using <see cref="SampleConfig"/>. Seeding methods change the
/// fakes immediately (they are not recorded as writes); defaults requested with <see cref="WithCorrectDefaults"/> and
/// <see cref="WithPerUserDefaults"/> and the spooler state from <see cref="WithSpooler"/> are applied by <see cref="Build"/>.
/// </summary>
public sealed class ScenarioBuilder
{
    public const string PdfQueueName = "Microsoft Print to PDF";

    private readonly List<string> _correctDefaults = [];
    private readonly List<(string Queue, Action<DevMode> Edit)> _perUserDefaults = [];
    private DeploymentConfig _config = SampleConfig.Create();
    private BranchOverride? _branch;
    private SetupOptions _options = new() { AppVersion = "1.0.0-test", ConfigSource = "sample configuration" };
    private SpoolerServiceStatus? _spoolerStatus;

    public ScenarioBuilder()
    {
        Installer = new FakeDriverInstaller(Spooler, Devices, Clock);
        Spooler.SeedDriver(new PrinterDriverInfo
        {
            Name = "Microsoft Print To PDF",
            Environment = SampleConfig.Environment,
            DriverModel = 4,
            Version = new Version(10, 0, 26100, 1),
            Manufacturer = "Microsoft",
            Provider = "Microsoft",
        });
        Spooler.SeedQueue(new PrintQueueInfo
        {
            Name = PdfQueueName,
            DriverName = "Microsoft Print To PDF",
            PortName = "PORTPROMPT:",
            PrintProcessor = "winprint",
            Datatype = "RAW",
            Attributes = PrinterAttributes.Local,
        });
    }

    public FakeClock Clock { get; } = new();
    public FakePrintSpooler Spooler { get; } = new();
    public FakeDeviceInventory Devices { get; } = new();
    public FakePlatformInspector Platform { get; } = new();
    public FakeNetworkProbe Network { get; } = new();
    public FakeDriverPackageProvider Packages { get; } = new();
    public FakeDriverInstaller Installer { get; }
    public MemoryLog Log { get; } = new();

    public DeploymentConfig Config => _config;

    public ScenarioBuilder WithConfig(Func<DeploymentConfig, DeploymentConfig> edit)
    {
        _config = edit(_config);
        return this;
    }

    public ScenarioBuilder WithModel(string modelId, Func<ModelDefinition, ModelDefinition> edit) =>
        WithConfig(c => c with { Models = c.Models.Select(m => m.Id == modelId ? edit(m) : m).ToList() });

    public ScenarioBuilder WithPackage(Func<DriverPackageDefinition, DriverPackageDefinition> edit) =>
        WithConfig(c => c with { DriverPackages = c.DriverPackages.Select(edit).ToList() });

    public ScenarioBuilder WithTicket(Func<TicketPaperConfig, TicketPaperConfig> edit) =>
        WithConfig(c => c with { Ticket = edit(c.Ticket) });

    public ScenarioBuilder WithBranch(BranchOverride? branch)
    {
        _branch = branch;
        return this;
    }

    public ScenarioBuilder WithOptions(Func<SetupOptions, SetupOptions> edit)
    {
        _options = edit(_options);
        return this;
    }

    public ScenarioBuilder WithMode(SetupMode mode) => WithOptions(o => o with { Mode = mode });

    public ScenarioBuilder WithPlatform(Func<PlatformInfo, PlatformInfo> edit)
    {
        Platform.Info = edit(Platform.Info);
        return this;
    }

    /// <summary>Spooler service state, applied last by <see cref="Build"/> (seeding needs a running fake).</summary>
    public ScenarioBuilder WithSpooler(ServiceRunState state, ServiceStartMode startMode = ServiceStartMode.Automatic)
    {
        _spoolerStatus = new SpoolerServiceStatus(state, startMode);
        return this;
    }

    /// <summary>A USB device; when <paramref name="usbPort"/> is given the USB monitor port exists.</summary>
    public ScenarioBuilder WithUsbPrinter(
        string modelId = SampleConfig.P4030,
        string? usbPort = "USB001",
        bool present = true,
        bool driverBound = true,
        string? instanceId = null,
        string? driverDescription = null)
    {
        Devices.Add(SampleConfig.UsbDevice(modelId, usbPort, present, driverBound, instanceId, driverDescription));
        if (usbPort is not null)
            Spooler.SeedUsbPort(usbPort);
        return this;
    }

    public ScenarioBuilder WithDevice(PnpPrinterDevice device)
    {
        Devices.Add(device);
        return this;
    }

    public ScenarioBuilder WithDriver(string driverName, Version? version = null, string environment = SampleConfig.Environment)
    {
        Spooler.SeedDriver(new PrinterDriverInfo
        {
            Name = driverName,
            Environment = environment,
            DriverModel = 3,
            Version = version ?? Installer.DriverVersion,
            Date = new DateTime(2025, 6, 12, 0, 0, 0, DateTimeKind.Utc),
            Manufacturer = driverName.StartsWith("Microsoft", StringComparison.Ordinal) ? "Microsoft" : "TA Triumph-Adler",
            Provider = driverName.StartsWith("Microsoft", StringComparison.Ordinal) ? "Microsoft" : "TA Triumph-Adler",
            PrintProcessor = "winprint",
        });
        return this;
    }

    /// <summary>The model's preferred driver ("TA &lt;model&gt; KX").</summary>
    public ScenarioBuilder WithModelDriver(string modelId = SampleConfig.P4030, Version? version = null) =>
        WithDriver(SampleConfig.DriverName(modelId), version);

    public ScenarioBuilder WithQueue(
        string name,
        string driverName,
        string portName,
        PrinterStatusFlags status = PrinterStatusFlags.None,
        PrinterAttributes attributes = PrinterAttributes.Local | PrinterAttributes.DoCompleteFirst | PrinterAttributes.EnableBidi,
        string? serverName = null)
    {
        Spooler.SeedQueue(new PrintQueueInfo
        {
            Name = name,
            DriverName = driverName,
            PortName = portName,
            ServerName = serverName,
            PrintProcessor = "winprint",
            Datatype = "RAW",
            Attributes = attributes,
            Status = status,
        });
        return this;
    }

    /// <summary>A Plug and Play style queue on the USB port, named after its driver unless <paramref name="name"/> is given.</summary>
    public ScenarioBuilder WithUsbQueue(
        string modelId = SampleConfig.P4030,
        string usbPort = "USB001",
        string? name = null,
        string? driverName = null,
        PrinterStatusFlags status = PrinterStatusFlags.None)
    {
        string driver = driverName ?? SampleConfig.DriverName(modelId);
        Spooler.SeedUsbPort(usbPort);
        return WithQueue(name ?? driver, driver, usbPort, status);
    }

    /// <summary>A fully configured USB printer: bound device, preferred driver, Plug and Play queue, ticket form and (optionally) correct defaults.</summary>
    public ScenarioBuilder WithInstalledUsbPrinter(string modelId = SampleConfig.P4030, string usbPort = "USB001", bool correctDefaults = true)
    {
        WithUsbPrinter(modelId, usbPort).WithModelDriver(modelId).WithUsbQueue(modelId, usbPort).WithTicketForm();
        return correctDefaults ? WithCorrectDefaults(SampleConfig.DriverName(modelId)) : this;
    }

    /// <summary>A Standard TCP/IP port ("IP_&lt;address&gt;" unless named), RAW on <paramref name="portNumber"/>.</summary>
    public ScenarioBuilder WithTcpIpPort(string address, string? portName = null, int portNumber = 9100)
    {
        Spooler.SeedPort(new PrinterPortInfo
        {
            Name = portName ?? "IP_" + address,
            MonitorName = "Standard TCP/IP Port",
            Description = "Standard TCP/IP Port",
            Kind = PortKind.StandardTcpIp,
            HostAddress = address,
            PortNumber = portNumber,
            Protocol = portNumber == 515 ? TcpPortProtocol.Lpr : TcpPortProtocol.Raw,
            SnmpEnabled = true,
        });
        return this;
    }

    /// <summary>
    /// An LPR Port monitor port as Windows lists it: monitor and description "LPR Port" (<see cref="PortKind.Lpr"/>), named
    /// "&lt;server&gt;:&lt;queue&gt;" after the LPD server and its queue (<paramref name="name"/>).
    /// </summary>
    public ScenarioBuilder WithLprPort(string name)
    {
        Spooler.SeedPort(new PrinterPortInfo { Name = name, MonitorName = "LPR Port", Description = "LPR Port", Kind = PortKind.Lpr });
        return this;
    }

    /// <summary>
    /// An Internet Port (IPP over HTTP) as Windows lists it: named by its URL and described as "Internet Port", without a port
    /// monitor (the Internet print provider serves it; <see cref="PortKind.Internet"/>).
    /// </summary>
    public ScenarioBuilder WithInternetPort(string url)
    {
        Spooler.SeedPort(new PrinterPortInfo { Name = url, Description = "Internet Port", Kind = PortKind.Internet });
        return this;
    }

    /// <summary>A TCP/IP queue (and its port when missing), named like the queues this tool creates unless <paramref name="name"/> is given.</summary>
    public ScenarioBuilder WithNetworkQueue(
        string address,
        string modelId = SampleConfig.P4030,
        string? name = null,
        string? driverName = null,
        string? portName = null,
        PrinterStatusFlags status = PrinterStatusFlags.None)
    {
        string port = portName ?? "IP_" + address;
        if (!Spooler.Ports.Any(p => string.Equals(p.Name, port, StringComparison.OrdinalIgnoreCase)))
            WithTcpIpPort(address, port);
        return WithQueue(name ?? _config.Connection.NewQueueName, driverName ?? SampleConfig.DriverName(modelId), port, status);
    }

    /// <summary>A printer answering at <paramref name="address"/> (SNMP identity of <paramref name="modelId"/>, or silent when null).</summary>
    public ScenarioBuilder WithNetworkPrinter(string address, string? modelId = SampleConfig.P4030, bool reachable = true, bool snmp = true)
    {
        Network.SetIdentity(SampleConfig.NetworkIdentity(address, modelId, reachable, snmp));
        return this;
    }

    public ScenarioBuilder WithNetworkIdentity(NetworkIdentity identity)
    {
        Network.SetIdentity(identity);
        return this;
    }

    /// <summary>
    /// <paramref name="alias"/> (for example the printer's DNS name) reaches the printer at <paramref name="address"/>: both
    /// spellings answer with one identity (same resolved address and serial number), as one physical printer does.
    /// </summary>
    public ScenarioBuilder WithNetworkAlias(string alias, string address)
    {
        Network.SetAlias(alias, address);
        return this;
    }

    /// <summary>The ticket form in the forms database (exact size and full imageable area unless changed).</summary>
    public ScenarioBuilder WithTicketForm(PaperDimensions? size = null, FormKind kind = FormKind.User, string? name = null)
    {
        var form = FormInfo.UserForm(name ?? _config.Ticket.FormName, size ?? _config.Ticket.Dimensions) with { Kind = kind };
        Spooler.SeedForm(form);
        return this;
    }

    public ScenarioBuilder WithForm(FormInfo form)
    {
        Spooler.SeedForm(form);
        return this;
    }

    /// <summary>The queue's Global DEVMODE carries the ticket defaults (applied by <see cref="Build"/>).</summary>
    public ScenarioBuilder WithCorrectDefaults(string queueName)
    {
        _correctDefaults.Add(queueName);
        return this;
    }

    /// <summary>The current user has a per-user DEVMODE: a copy of the queue's Global DEVMODE changed by <paramref name="edit"/>.</summary>
    public ScenarioBuilder WithPerUserDefaults(string queueName, Action<DevMode> edit)
    {
        _perUserDefaults.Add((queueName, edit));
        return this;
    }

    public ScenarioBuilder WithDriverProfile(string driverName, Func<FakeDriverProfile, FakeDriverProfile> edit)
    {
        Spooler.SetProfile(driverName, edit(Spooler.ProfileFor(driverName)));
        return this;
    }

    public ScenarioBuilder WithModelDriverProfile(string modelId, Func<FakeDriverProfile, FakeDriverProfile> edit) =>
        WithDriverProfile(SampleConfig.DriverName(modelId), edit);

    /// <summary>How long after a USB device is bound Windows creates its Plug and Play queue (null: never).</summary>
    public ScenarioBuilder WithPlugAndPlayQueueDelay(TimeSpan? delay)
    {
        Installer.PlugAndPlayQueueDelay = delay;
        return this;
    }

    public Scenario Build()
    {
        foreach (var model in _config.Models)
        {
            foreach (string hardwareId in model.HardwareIds)
                Installer.DriverNamesByHardwareId.TryAdd(hardwareId, model.PreferredDriverName);
        }

        var scenario = new Scenario(_config, _branch, _options, this);
        foreach (string queue in _correctDefaults)
            scenario.ConfigureCorrectDefaults(queue);
        foreach (var (queue, edit) in _perUserDefaults)
        {
            var perUser = Spooler.GetStoredDevMode(queue, DevModeScope.Global)
                ?? throw new InvalidOperationException($"Queue '{queue}' has no Global DEVMODE to copy.");
            edit(perUser);
            Spooler.SetStoredDevMode(queue, DevModeScope.PerUser, perUser);
        }
        if (_spoolerStatus is not null)
            Spooler.ServiceStatus = _spoolerStatus;
        return scenario;
    }
}

/// <summary>A built scenario: the configuration, the fakes and helpers to run the engine against them.</summary>
public sealed class Scenario
{
    internal Scenario(DeploymentConfig config, BranchOverride? branch, SetupOptions options, ScenarioBuilder fakes)
    {
        Config = config;
        Branch = branch;
        Options = options;
        Clock = fakes.Clock;
        Spooler = fakes.Spooler;
        Devices = fakes.Devices;
        Platform = fakes.Platform;
        Network = fakes.Network;
        Packages = fakes.Packages;
        Installer = fakes.Installer;
        Log = fakes.Log;
        Services = new SetupServices
        {
            Spooler = Spooler,
            Devices = Devices,
            Platform = Platform,
            Network = Network,
            Packages = Packages,
            Installer = Installer,
            Log = Log,
            Clock = Clock,
        };
    }

    public DeploymentConfig Config { get; }
    public BranchOverride? Branch { get; }
    public SetupOptions Options { get; }
    public FakeClock Clock { get; }
    public FakePrintSpooler Spooler { get; }
    public FakeDeviceInventory Devices { get; }
    public FakePlatformInspector Platform { get; }
    public FakeNetworkProbe Network { get; }
    public FakeDriverPackageProvider Packages { get; }
    public FakeDriverInstaller Installer { get; }
    public MemoryLog Log { get; }
    public SetupServices Services { get; }

    public SetupSession CreateSession(SetupMode? mode = null) =>
        new(Config, Branch, mode is { } m ? Options with { Mode = m } : Options, Services);

    public SetupSession CreateSession(Func<SetupOptions, SetupOptions> edit) => new(Config, Branch, edit(Options), Services);

    /// <summary>Detect, select automatically, assess, apply in <paramref name="scope"/> and verify (stops after selection when nothing is selected).</summary>
    public ScenarioRun Run(SetupMode? mode = null, DetectRequest? request = null, ApplyScope scope = ApplyScope.All, CancellationToken cancellationToken = default) =>
        Run(CreateSession(mode), request, scope, cancellationToken);

    public static ScenarioRun Run(SetupSession session, DetectRequest? request = null, ApplyScope scope = ApplyScope.All, CancellationToken cancellationToken = default)
    {
        var detection = session.Detect(request ?? new DetectRequest(), cancellationToken);
        var selection = session.SelectAutomatically(detection);
        if (selection.Selected is not { } target)
            return new ScenarioRun(session, detection, selection, null, null, null);

        var plan = session.Assess(target, cancellationToken);
        var apply = session.Apply(plan, scope, null, cancellationToken);
        var verification = session.Verify(TargetSelector.For(target), cancellationToken);
        return new ScenarioRun(session, detection, selection, plan, apply, verification);
    }

    /// <summary>The paper the queue's driver offers for the ticket (by form name, else by size).</summary>
    public PaperCapability TicketPaper(string queueName) =>
        Spooler.GetCapabilities(queueName).FindTicketPaper(Config.Ticket.FormName, Config.Ticket.Dimensions)
        ?? throw new InvalidOperationException($"The driver of '{queueName}' does not list '{Config.Ticket.FormName}'; seed the ticket form first.");

    /// <summary>Stores the DEVMODE a correctly configured queue has: ticket paper, configured orientation/duplex/scale, MP tray.</summary>
    public void ConfigureCorrectDefaults(string queueName, DevModeScope scope = DevModeScope.Global)
    {
        var caps = Spooler.GetCapabilities(queueName);
        var paper = TicketPaper(queueName);
        var queue = Spooler.Queues.First(q => string.Equals(q.Name, queueName, StringComparison.OrdinalIgnoreCase));
        var devMode = Spooler.GetStoredDevMode(queueName, DevModeScope.Global)
            ?? FakeDevModes.CreateDefault(Spooler.ProfileFor(queue.DriverName), queueName);

        var binIds = Config.Models.SelectMany(m => m.TicketPaperSource.BinIds).ToHashSet();
        var bin = caps.Bins.FirstOrDefault(b => binIds.Contains(b.Id));
        var prefs = Config.PrintPreferences;

        devMode.Fields = (devMode.Fields | DevModeFields.FormName | DevModeFields.PaperSize | DevModeFields.Orientation | DevModeFields.Scale)
            & ~(DevModeFields.PaperLength | DevModeFields.PaperWidth);
        devMode.FormName = paper.Name;
        devMode.PaperSize = paper.Id;
        devMode.Orientation = prefs.Orientation == PageOrientationSetting.Landscape ? DmOrientation.Landscape : DmOrientation.Portrait;
        devMode.Scale = (short)prefs.ScalePercent;
        if (caps.SupportsDuplex)
        {
            devMode.Fields |= DevModeFields.Duplex;
            devMode.Duplex = prefs.Duplex switch
            {
                DuplexSetting.LongEdge => DmDuplex.Vertical,
                DuplexSetting.ShortEdge => DmDuplex.Horizontal,
                _ => DmDuplex.Simplex,
            };
        }
        if (bin is not null && prefs.PaperSource != PaperSourcePolicy.DriverDefault)
        {
            devMode.Fields |= DevModeFields.DefaultSource;
            devMode.DefaultSource = bin.Id;
        }
        Spooler.SetStoredDevMode(queueName, scope, devMode);
    }
}

public sealed record ScenarioRun(
    SetupSession Session,
    DetectionResult Detection,
    CandidateSelection Selection,
    SetupPlan? Plan,
    ApplyResult? Apply,
    VerificationReport? Verification);
