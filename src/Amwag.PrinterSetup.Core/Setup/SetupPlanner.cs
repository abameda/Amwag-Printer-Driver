using System.Globalization;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Detection;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Units;
using static Amwag.PrinterSetup.Core.Setup.ActionParameters;

namespace Amwag.PrinterSetup.Core.Setup;

/// <summary>
/// Read-only assessment of one target: reads the current state fresh and plans the ordered actions that reach the desired
/// state. Diagnostics mode plans nothing but reports everything. Actions whose need can only be known once earlier actions
/// ran (new queue, changed driver, new form) are planned unconditionally; they re-check at execution.
/// </summary>
internal sealed class SetupPlanner
{
    private const PrinterStatusFlags OfflineFlags = PrinterStatusFlags.Offline | PrinterStatusFlags.NotAvailable;
    private const PrinterStatusFlags ErrorFlags = PrinterStatusFlags.Error | PrinterStatusFlags.PaperOut | PrinterStatusFlags.DoorOpen |
        PrinterStatusFlags.PaperJam | PrinterStatusFlags.PaperProblem | PrinterStatusFlags.NoToner | PrinterStatusFlags.UserIntervention;

    private readonly DeploymentConfig _config;
    private readonly BranchOverride? _branch;
    private readonly SetupOptions _options;
    private readonly SetupServices _services;
    private readonly ModelMatcher _matcher;
    private readonly List<Finding> _findings = [];
    private readonly List<Finding> _blockers = [];
    private readonly List<PlannedAction> _actions = [];
    private bool _packageChecked;
    private string _driverForQueue = "";
    // The driver of the queue that is moved to another driver because it cannot offer the ticket paper (WillNotOfferTicket).
    private string? _ticketSwitchFrom;

    public SetupPlanner(DeploymentConfig config, BranchOverride? branch, SetupOptions options, SetupServices services, ModelMatcher matcher)
    {
        _config = config;
        _branch = branch;
        _options = options;
        _services = services;
        _matcher = matcher;
    }

    private bool Diagnostics => _options.Mode == SetupMode.Diagnostics;

    /// <summary>The configured addresses (command line, branch file) that every detection of this run probes again.</summary>
    private IReadOnlyCollection<string?> ProbedAddresses => [_options.PreferredAddress, _branch?.PrinterAddress];

    public SetupPlan Assess(PrinterCandidate target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var platform = Read("platform", _services.Platform.Inspect) ?? new PlatformInfo();
        var status = Read("spooler service", _services.Spooler.GetServiceStatus)
            ?? new SpoolerServiceStatus(ServiceRunState.Unknown, ServiceStartMode.Unknown);
        var state = SpoolerState.Read(_services.Spooler, status);

        foreach (var finding in EnvironmentChecks.Evaluate(platform, status, state.Unavailable, _config, _options))
            Add(finding, EnvironmentChecks.IsBlocker(finding));
        if (!state.Known && !state.Unavailable && status.State is ServiceRunState.Running or ServiceRunState.StartPending or ServiceRunState.Unknown)
            Add(Findings.Error(FindingCode.OperationFailed, $"The print configuration could not be read: {state.Error}"), blocker: true);

        if (target is not { IsEligible: true, Model: { } model })
        {
            Add(Findings.Error(FindingCode.ProbableMatchRequiresConfirmation,
                target.Model is null
                    ? "The printer's model has not been identified; the operator must choose the model first."
                    : $"The printer was identified only probably as {target.Model.Id}; the operator must confirm the model first.",
                ("candidate", target.Key)), blocker: true);
        }
        else if (_config.FindPackage(model.DriverPackageId) is not { } package)
        {
            Add(Findings.Error(FindingCode.ConfigurationInvalid,
                $"Model {model.Id} names driver package '{model.DriverPackageId}', which the configuration does not contain."), blocker: true);
        }
        else
        {
            AssessTarget(target, model, package, platform, status, state);
        }

        Add(Findings.Info(FindingCode.ApplicationMayOverrideDefaults,
            $"Applications can override the queue defaults when they print; a ticketing application must use the '{_config.Ticket.FormName}' paper itself if tickets print on the wrong size."));

        var plan = new SetupPlan
        {
            Target = target,
            Mode = _options.Mode,
            Findings = _findings,
            Blockers = _blockers,
            Actions = Diagnostics ? [] : _actions.OrderBy(a => a.Kind).ToList(),
        };
        _services.Log.Info("assess.plan", $"Assessed {target.Key}: {plan.Actions.Count} action(s), {plan.Blockers.Count} blocker(s).", new Dictionary<string, object?>
        {
            ["target"] = target.Key,
            ["mode"] = _options.Mode.ToString(),
            ["actions"] = plan.Actions.Select(a => a.Kind.ToString()).ToArray(),
            ["blockers"] = plan.Blockers.Select(b => b.Code.ToString()).ToArray(),
            ["findings"] = plan.Findings.Select(f => $"{f.Severity}:{f.Code}").ToArray(),
        });
        return plan;
    }

    private void AssessTarget(
        PrinterCandidate target,
        ModelDefinition model,
        DriverPackageDefinition package,
        PlatformInfo platform,
        SpoolerServiceStatus status,
        SpoolerState state)
    {
        // Setup creates missing queues; Diagnostics reports what Setup would do.
        bool mayCreateQueue = _options.Mode != SetupMode.Repair && target.Kind is TargetKind.UsbDevice or TargetKind.NetworkAddress;

        if (!status.IsRunning && status.State is ServiceRunState.Stopped or ServiceRunState.StopPending or ServiceRunState.Paused &&
            status.StartMode != ServiceStartMode.Disabled)
        {
            Plan(ActionKind.StartSpooler, true, "Start the Print Spooler service.");
        }

        var devices = target.Device is null ? null : ReadDevices(target);
        var device = FreshDevice(target, devices);
        var queue = state.Known ? LocateQueue(target, model, device, devices, state) : null;
        bool queueMissing = state.Known && queue is null;
        // Repair never creates a queue: with none there is nothing to repair. Unknown state (spooler stopped) is planned conservatively.
        // Diagnostics plans like Setup so that its findings (and blockers) are complete; Assess then drops the actions.
        bool act = !(queueMissing && !mayCreateQueue);
        bool manageInstall = act && (state.Known || mayCreateQueue);

        ReportConnection(target, model, device, queue);

        var queueDriver = queue is not null && DriverRules.IsAccepted(model, queue.DriverName)
            ? DriverRules.Check(model, queue.DriverName, state.Drivers, package.Environment)
            : null;
        bool queueDriverReady = queueDriver is { IsInstalled: true } && !target.IdentityConflict;
        bool queueUsesPreferred = queue is not null && Same(queue.DriverName, model.PreferredDriverName) && !target.IdentityConflict;
        var ticket = _config.Ticket.Dimensions;
        DriverCapabilities? caps = state.Known && queue is not null && queueDriverReady ? ReadCapabilities(queue.Name) : null;
        // An accepted driver that does not offer the ticket form is replaced like a wrong one, and its capabilities do not count.
        if (queue is not null && queueDriverReady && !queueUsesPreferred && MovesForTicket(model, queue, caps, ticket, state, package.Environment))
        {
            _ticketSwitchFrom = queue.DriverName;
            queueDriverReady = false;
            caps = null;
        }
        // An outdated preferred driver is updated in place by installing the package; any other wrong driver is replaced,
        // preferably by an approved driver that is already installed.
        _driverForQueue = queueUsesPreferred ? model.PreferredDriverName : DriverRules.DriverForQueue(model, state.Drivers, package.Environment, _ticketSwitchFrom);
        var chosen = DriverRules.Check(model, _driverForQueue, state.Drivers, package.Environment);
        bool changeDriver = queue is not null && !queueDriverReady && !queueUsesPreferred;
        bool driverNeeded = queue is null ? mayCreateQueue : !queueDriverReady;

        if (state.Known)
            ReportDrivers(queue, queueDriver, queueDriverReady, chosen);
        if (manageInstall && driverNeeded && !chosen.IsInstalled)
            PlanDriverInstall(model, package, platform);

        if (target.Kind == TargetKind.UsbDevice && device is { IsPresent: true } && !DriverRules.IsBound(device, model))
            PlanDeviceBinding(model, package, platform, device, manageInstall);

        switch (target.Kind)
        {
            case TargetKind.UsbDevice:
                PlanUsbQueue(target, model, device, queue, state, act, mayCreateQueue, changeDriver);
                break;
            case TargetKind.NetworkAddress:
                PlanNetworkQueue(target, model, queue, state, act, mayCreateQueue, changeDriver);
                break;
            default:
                PlanExistingQueue(target, model, queue, state, act, changeDriver);
                break;
        }

        if (queue is not null)
            ReportQueueStatus(queue);

        bool formExact = AssessForm(ticket, state, act);
        CheckTrayRanges(model, ticket);

        PaperCapability? paper = null;
        SourceDecision? source = null;
        if (caps is not null)
        {
            paper = AssessMedia(caps, ticket, formExact);
            source = TicketDefaults.DecideSource(_config.PrintPreferences, model, caps.Bins);
            ReportSource(source, model, caps);
        }

        if (queue is not null && caps is not null && paper is not null && source is not null)
            AssessDefaults(queue, TicketDefaults.Desired(_config.PrintPreferences, paper, caps, source), act);
        else if (act && (queue is not null || mayCreateQueue || !state.Known))
            PlanDefaults(queue?.Name);
    }

    private void ReportConnection(PrinterCandidate target, ModelDefinition model, PnpPrinterDevice? device, PrintQueueInfo? queue)
    {
        if (target.Connection == ConnectionType.Usb && device is not { IsPresent: true })
        {
            Add(Findings.Warning(FindingCode.DeviceNotConnected,
                target.Kind == TargetKind.UsbDevice
                    ? $"The USB printer {model.Id} is not connected; it can be configured, but it cannot print until it is connected."
                    : $"No connected USB printer is on port {queue?.PortName ?? target.Queue?.PortName}.",
                ("candidate", target.Key)));
        }

        if (target is { Connection: ConnectionType.TcpIp, NetworkIdentity.RawPortReachable: false })
        {
            Add(Findings.Warning(FindingCode.NetworkPrinterUnreachable,
                Inv($"The printer at {target.NetworkAddress} does not answer on port {_config.Connection.RawPortNumber}; it can be configured, but it cannot print until it is reachable."),
                ("address", target.NetworkAddress)));
        }
    }

    private void ReportDrivers(PrintQueueInfo? queue, DriverCheck? queueDriver, bool queueDriverReady, DriverCheck chosen)
    {
        if (queueDriverReady)
            Add(Findings.Info(FindingCode.DriverReady, $"Queue '{queue!.Name}' uses an accepted driver. {queueDriver!.Detail}", ("driver", queue.DriverName)));
        else if (chosen.IsInstalled)
            Add(Findings.Info(FindingCode.DriverReady, chosen.Detail, ("driver", _driverForQueue)));
        else if (chosen.State == DriverState.Outdated)
            Add(Findings.Warning(FindingCode.DriverOutdated, chosen.Detail, ("driver", _driverForQueue)));
        else
            Add(Findings.Warning(FindingCode.DriverMissing, $"The approved driver '{_driverForQueue}' is not installed.", ("driver", _driverForQueue)));

        if (queueDriver is { State: DriverState.Outdated } && !Same(queue!.DriverName, _driverForQueue))
            Add(Findings.Warning(FindingCode.DriverOutdated, queueDriver.Detail, ("driver", queue.DriverName)));
    }

    private void PlanDriverInstall(ModelDefinition model, DriverPackageDefinition package, PlatformInfo platform)
    {
        CheckPackage(package, platform);
        PlanPrepare(package);
        Plan(ActionKind.InstallDriver, true,
            $"Install printer driver '{model.PreferredDriverName}' ({package.Environment}) from package '{package.Id}'.",
            (PackageId, package.Id), (DriverName, model.PreferredDriverName), (DriverEnvironment, package.Environment));
    }

    private void PlanDeviceBinding(ModelDefinition model, DriverPackageDefinition package, PlatformInfo platform, PnpPrinterDevice device, bool manageInstall)
    {
        string state = device.ProblemCode is { } problem
            ? Inv($"The USB printer {device.InstanceId} reports problem code {problem} (28 means its driver is not installed).")
            : $"The USB printer {device.InstanceId} uses '{device.DriverDescription ?? "no driver"}', which is not an accepted driver of {model.Id}.";
        string? hardwareId = manageInstall ? _matcher.MatchDevice(device).MatchedId : null;
        // Without a planned driver install the approved driver is installed and only the binding needs the package: one that
        // may not be installed on this PC leaves the device on its driver instead of holding back the queue and its defaults.
        if (hardwareId is not null && !_actions.Any(a => a.Kind == ActionKind.InstallDriver) && PackageProblems(package, platform) is [var first, ..] problems)
        {
            Add(Findings.Warning(FindingCode.DeviceDriverNotBound,
                $"{state} The driver is not installed on it: {string.Join(" ", problems.Select(p => p.Detail))} The printer's queue is set up with the approved driver that is already installed.",
                ("device", device.InstanceId), ("package", package.Id), ("hardwareId", hardwareId), ("reason", first.Code.ToString())));
            return;
        }

        Add(Findings.Warning(FindingCode.DeviceDriverNotBound, state, ("device", device.InstanceId)));
        if (hardwareId is null)
            return;

        CheckPackage(package, platform);
        PlanPrepare(package);
        Plan(ActionKind.BindUsbDevice, true,
            $"Install the driver from package '{package.Id}' on the USB printer ({hardwareId}).",
            (HardwareId, hardwareId), (PackageId, package.Id), (InstanceId, device.InstanceId));
    }

    private void PlanUsbQueue(PrinterCandidate target, ModelDefinition model, PnpPrinterDevice? device, PrintQueueInfo? queue, SpoolerState state, bool act, bool mayCreateQueue, bool changeDriver)
    {
        if (queue is null)
        {
            if (mayCreateQueue)
            {
                // Another queue already prints to the printer's port, yet it is not this printer's (a pool a connected printer uses,
                // or the queue of the printer the port now leads to): a queue added there would print to the same printer, so
                // nothing is changed.
                string? usbPort = device?.UsbPortName ?? target.Device?.UsbPortName;
                if (QueueLocator.OnPort(state.Queues, usbPort).FirstOrDefault() is { } other)
                {
                    Add(Findings.Error(FindingCode.QueueLeftUnchanged,
                        $"Queue '{other.Name}' (ports {other.PortName}), which the setup does not configure for this printer, already prints to {usbPort}, the port of the USB printer {model.Id} ({target.Key}). "
                        + "A second queue on that port would print to the same printer, so nothing is changed. Set up the printer that queue belongs to, "
                        + "or connect this printer and run the setup again if it is the ticket printer.",
                        ("queue", other.Name), ("port", other.PortName), ("device", target.Device?.InstanceId), ("candidate", target.Key)), blocker: true);
                    return;
                }
                ReportQueueToCreate(target, state);
                if (act)
                {
                    string name = QueueLocator.NewQueueName(_options.QueueNameOverride ?? _branch?.QueueName ?? _driverForQueue, model.Id, state.Queues);
                    string portText = device?.UsbPortName ?? "its USB port (known once the driver is installed)";
                    Plan(ActionKind.CreateQueue, true,
                        $"Use the queue Windows creates for the USB printer, or create queue '{name}' with driver '{_driverForQueue}' on {portText}.",
                        (QueueName, name), (DriverName, _driverForQueue), (PortName, device?.UsbPortName),
                        (Connection, UsbConnection), (InstanceId, device?.InstanceId));
                }
            }
            else if (state.Known)
            {
                Add(Findings.Error(FindingCode.QueueMissing,
                    $"The USB printer {model.Id} has no queue; Repair does not create queues (run Setup).", ("candidate", target.Key)));
            }
            return;
        }

        bool ready = PlanDriverChange(target, model, queue, act, changeDriver);
        // A pooled queue that lists the printer's port already prints to it; moving it would undo the pooling.
        if (device is { IsPresent: true, UsbPortName: { } port } && !QueueLocator.UsesPort(queue, port))
        {
            ready = false;
            Add(Findings.Warning(FindingCode.QueuePortMismatch, $"Queue '{queue.Name}' uses port {queue.PortName}, but the printer is on {port}.",
                ("queue", queue.Name), ("expected", port), ("actual", queue.PortName)));
            if (act)
            {
                Plan(ActionKind.ChangeQueuePort, true, $"Move queue '{queue.Name}' from port {queue.PortName} to {port}.",
                    (QueueName, queue.Name), (PortName, port));
            }
        }
        if (ready)
            Add(Findings.Info(FindingCode.QueueReady, $"Queue '{queue.Name}' prints to {queue.PortName} with driver '{queue.DriverName}'.", ("queue", queue.Name)));
    }

    private void PlanNetworkQueue(PrinterCandidate target, ModelDefinition model, PrintQueueInfo? queue, SpoolerState state, bool act, bool mayCreateQueue, bool changeDriver)
    {
        string address = target.NetworkAddress ?? "";
        int portNumber = _config.Connection.RawPortNumber;
        // A port to another spelling of the printer (its host name or IP address) already sends to it.
        bool hostMatches = queue is not null && QueueLocator.HostOf(queue, state.Ports) is { } host
            && QueueLocator.NetworkHosts(target).Contains(host, StringComparer.OrdinalIgnoreCase);
        bool needPort = queue is null ? mayCreateQueue : !hostMatches;

        string? portName = null;
        string portText = "";
        if (needPort && act)
        {
            // An existing port on the printer's address, its IP address or another configured name of it: never a second port.
            if (state.Known && QueueLocator.ReusableTcpPort(state.Ports, target, portNumber, ProbedAddresses) is { } reusable)
            {
                // Another queue already prints to that port: detection leaves only a queue the setup does not configure for this
                // printer there (a pooled queue, also one a USB printer configures), and a queue added or moved there would print to
                // the same printer, so nothing is changed.
                if (QueueLocator.OnPort(state.Queues, reusable.Name).FirstOrDefault(q => !Same(q.Name, queue?.Name)) is { } other)
                {
                    Add(Findings.Error(FindingCode.QueueLeftUnchanged,
                        $"Queue '{other.Name}' (ports {other.PortName}), which the setup does not configure for this printer, already prints to port {reusable.Name}, which sends to the printer at {address}. "
                        + $"A second queue for {address} would print to the same printer, so nothing is changed. If '{other.Name}' is the ticket printer's queue, "
                        + "un-pool it or put it on a single Standard TCP/IP port, or delete it if it is not used, then run the setup again.",
                        ("queue", other.Name), ("port", other.PortName), ("address", address), ("candidate", target.Key)), blocker: true);
                    return;
                }
                portName = reusable.Name;
                if (!Same(reusable.HostAddress, address))
                    portText = $", which already sends to {reusable.HostAddress} (the printer at {address})";
            }
            else
            {
                portName = QueueLocator.NewTcpPortName(address, state.Ports);
                Plan(ActionKind.CreateTcpIpPort, true, Inv($"Create Standard TCP/IP port '{portName}' for {address}, RAW port {portNumber}."),
                    (PortName, portName), (HostAddress, address), (PortNumber, portNumber.ToString(CultureInfo.InvariantCulture)));
            }
        }

        if (queue is null)
        {
            if (mayCreateQueue)
            {
                ReportQueueToCreate(target, state);
                if (act)
                {
                    string name = QueueLocator.NewQueueName(_options.QueueNameOverride ?? _branch?.QueueName ?? _config.Connection.NewQueueName, model.Id, state.Queues);
                    Plan(ActionKind.CreateQueue, true, $"Create queue '{name}' with driver '{_driverForQueue}' on port '{portName}'{portText}.",
                        (QueueName, name), (DriverName, _driverForQueue), (PortName, portName),
                        (Connection, TcpIpConnection), (HostAddress, address));
                }
            }
            else if (state.Known)
            {
                Add(Findings.Error(FindingCode.QueueMissing,
                    $"The printer at {address} has no queue; Repair does not create queues (run Setup).", ("candidate", target.Key)));
            }
            return;
        }

        bool ready = PlanDriverChange(target, model, queue, act, changeDriver);
        if (!hostMatches)
        {
            ready = false;
            Add(Findings.Warning(FindingCode.QueuePortMismatch, $"Queue '{queue.Name}' uses port {queue.PortName}, which does not send to {address}.",
                ("queue", queue.Name), ("expected", address), ("actual", queue.PortName)));
            if (act)
            {
                Plan(ActionKind.ChangeQueuePort, true, $"Move queue '{queue.Name}' from port {queue.PortName} to '{portName}'{portText}.",
                    (QueueName, queue.Name), (PortName, portName), (HostAddress, address));
            }
        }
        if (ready)
            Add(Findings.Info(FindingCode.QueueReady, $"Queue '{queue.Name}' prints to {queue.PortName} with driver '{queue.DriverName}'.", ("queue", queue.Name)));
    }

    private void PlanExistingQueue(PrinterCandidate target, ModelDefinition model, PrintQueueInfo? queue, SpoolerState state, bool act, bool changeDriver)
    {
        if (queue is null)
        {
            if (state.Known)
            {
                Add(Findings.Error(FindingCode.QueueMissing, $"Queue '{target.Queue?.Name}' no longer exists; it cannot be recreated for this connection.",
                    ("candidate", target.Key)));
            }
            return;
        }

        if (PlanDriverChange(target, model, queue, act, changeDriver))
            Add(Findings.Info(FindingCode.QueueReady, $"Queue '{queue.Name}' prints to {queue.PortName} with driver '{queue.DriverName}'.", ("queue", queue.Name)));
    }

    private void ReportQueueToCreate(PrinterCandidate target, SpoolerState state)
    {
        if (Diagnostics && state.Known)
            Add(Findings.Warning(FindingCode.QueueMissing, $"The printer {target.Key} has no queue; Setup would create one.", ("candidate", target.Key)));
    }

    /// <summary>Plans ChangeQueueDriver when needed; true when the queue's driver is fine.</summary>
    private bool PlanDriverChange(PrinterCandidate target, ModelDefinition model, PrintQueueInfo queue, bool act, bool changeDriver)
    {
        if (!changeDriver)
            return true;

        bool forTicket = Same(_ticketSwitchFrom, queue.DriverName);
        if (forTicket)
        {
            Add(Findings.Warning(FindingCode.DriverIgnoresTicketForm,
                $"Queue '{queue.Name}' uses driver '{queue.DriverName}', which ignores Windows forms, so it does not offer the '{_config.Ticket.FormName}' form: " +
                "applications cannot select the ticket paper by name, and a custom page size made in that driver has another name and may exist for one Windows account only. " +
                $"The queue is moved to the approved driver '{_driverForQueue}', which offers the ticket form to every user; " +
                "if the ticket defaults cannot be set with it, the queue gets its driver and defaults back.",
                ("queue", queue.Name), ("expected", _driverForQueue), ("actual", queue.DriverName)));
        }
        else
        {
            Add(Findings.Warning(FindingCode.QueueDriverMismatch,
                target.IdentityConflict
                    ? $"Queue '{queue.Name}' uses driver '{queue.DriverName}' for another model, but the printer is a {model.Id}."
                    : $"Queue '{queue.Name}' uses driver '{queue.DriverName}', which is not an accepted driver of {model.Id}.",
                ("queue", queue.Name), ("expected", _driverForQueue), ("actual", queue.DriverName)));
        }
        if (act)
        {
            Plan(ActionKind.ChangeQueueDriver, true, $"Change the driver of queue '{queue.Name}' from '{queue.DriverName}' to '{_driverForQueue}'.",
                (QueueName, queue.Name), (DriverName, _driverForQueue), (RestoreDriverName, forTicket ? queue.DriverName : null));
        }
        return false;
    }

    /// <summary>
    /// Whether the queue moves to another approved driver that uses Windows forms, because its own accepted driver does not offer
    /// the ticket form: a driver known to ignore Windows forms (<see cref="AcceptedDriver.ListsWindowsForms"/>, the KX v3 driver)
    /// always moves, also with a custom page size of the ticket size (it has another name and may exist for one Windows account
    /// only); another driver moves when the exact form exists but it offers no paper of the ticket size. Only when the driver
    /// the queue would be given is another one that uses Windows forms.
    /// </summary>
    private bool MovesForTicket(ModelDefinition model, PrintQueueInfo queue, DriverCapabilities? caps, PaperDimensions ticket, SpoolerState state, string environment)
    {
        string replacement = DriverRules.DriverForQueue(model, state.Drivers, environment, queue.DriverName);
        if (Same(replacement, queue.DriverName) || !DriverRules.ListsWindowsForms(model, replacement))
            return false;
        if (!DriverRules.ListsWindowsForms(model, queue.DriverName))
            return true;
        var form = state.Forms.FirstOrDefault(f => Same(f.Name, _config.Ticket.FormName));
        bool formExact = form is not null && form.HasSize(ticket) && form.HasFullImageableArea;
        return formExact && caps is not null && caps.FindTicketPaper(_config.Ticket.FormName, ticket) is null;
    }

    private void ReportQueueStatus(PrintQueueInfo queue)
    {
        var status = queue.Status;
        if ((status & OfflineFlags) != 0 || queue.IsWorkOffline)
        {
            Add(Findings.Warning(FindingCode.PrinterOffline,
                $"Queue '{queue.Name}' is offline ({(queue.IsWorkOffline ? "Use Printer Offline is set" : (status & OfflineFlags).ToString())}).", ("queue", queue.Name)));
        }
        if ((status & ErrorFlags) != 0)
            Add(Findings.Warning(FindingCode.PrinterError, $"Queue '{queue.Name}' reports a printer problem ({status & ErrorFlags}).", ("queue", queue.Name)));
        if ((status & PrinterStatusFlags.Paused) != 0)
            Add(Findings.Warning(FindingCode.PrinterPaused, $"Queue '{queue.Name}' is paused.", ("queue", queue.Name)));
    }

    /// <summary>Reports and plans the ticket form; true when it exists with the exact size and a full imageable area.</summary>
    private bool AssessForm(PaperDimensions ticket, SpoolerState state, bool act)
    {
        string name = _config.Ticket.FormName;
        string size = TicketDefaults.Micrometers(ticket.WidthMicrometers, ticket.HeightMicrometers);
        if (!state.Known)
        {
            if (act)
                PlanForm(ActionKind.CreateForm, ticket);
            return false;
        }

        var form = state.Forms.FirstOrDefault(f => Same(f.Name, name));
        bool exact = form is not null && form.HasSize(ticket) && form.HasFullImageableArea;
        if (form is null)
        {
            Add(Findings.Warning(FindingCode.FormMissing, $"The form '{name}' ({size}) does not exist.", ("form", name)));
            if (act)
                PlanForm(ActionKind.CreateForm, ticket);
        }
        else if (exact)
        {
            Add(Findings.Info(FindingCode.FormReady, $"The form '{form.Name}' exists with {size} and a full imageable area.", ("form", form.Name)));
        }
        else
        {
            string actual = TicketDefaults.Micrometers(form.WidthMicrometers, form.HeightMicrometers) + (form.HasFullImageableArea ? "" : ", imageable area reduced");
            if (form.Kind == FormKind.User)
            {
                Add(Findings.Warning(FindingCode.FormSizeMismatch, $"The form '{form.Name}' is {actual}; it must be {size} with a full imageable area.",
                    ("form", form.Name), ("expected", size), ("actual", actual)));
                if (act)
                    PlanForm(ActionKind.UpdateForm, ticket);
            }
            else
            {
                Add(Findings.Error(FindingCode.FormSizeMismatch,
                    $"The {form.Kind.ToString().ToLowerInvariant()} form '{form.Name}' is {actual}; it cannot be changed, so the ticket form cannot use this name.",
                    ("form", form.Name), ("expected", size), ("actual", actual)), blocker: true);
            }
        }

        var similar = state.Forms.Where(f => !Same(f.Name, name) && f.HasSize(ticket)).Select(f => f.Name).ToList();
        if (similar.Count > 0)
        {
            Add(Findings.Info(FindingCode.FormSimilarExists, $"Other forms have the ticket size: {string.Join(", ", similar.Select(s => $"'{s}'"))}; only '{name}' is used.",
                ("forms", string.Join(", ", similar))));
        }
        return exact;
    }

    private void CheckTrayRanges(ModelDefinition model, PaperDimensions ticket)
    {
        string size = ticket.ToString();
        if (model.MultipurposeTrayRange is { } tray && !tray.Contains(ticket))
        {
            Add(Findings.Error(FindingCode.TicketSizeOutsideTrayRange,
                $"The ticket ({size}) is outside the multipurpose tray range {Describe(tray)} of {model.Id}.", ("tray", "multipurpose"), ("model", model.Id)), blocker: true);
        }

        if (model.CassetteRange is { } cassette && !cassette.Contains(ticket))
        {
            string detail = $"The ticket ({size}) is outside the cassette range {Describe(cassette)} of {model.Id}; only the multipurpose tray can feed it.";
            Add(model.TicketPaperSource.RequiredForTicket
                ? Findings.Info(FindingCode.TicketSizeOutsideTrayRange, detail, ("tray", "cassette"), ("model", model.Id))
                : Findings.Warning(FindingCode.TicketSizeOutsideTrayRange, detail + " The model's ticketPaperSource.requiredForTicket should be true.", ("tray", "cassette"), ("model", model.Id)));
        }
    }

    /// <summary>Checks the driver's media support; returns the paper it offers for the existing form (by name, else by size).</summary>
    private PaperCapability? AssessMedia(DriverCapabilities caps, PaperDimensions ticket, bool formExact)
    {
        string name = _config.Ticket.FormName;
        if (formExact)
        {
            var paper = caps.FindTicketPaper(name, ticket);
            if (paper is null)
            {
                var range = DriverSizeRange.Read(_services.Spooler, caps, _services.Log);
                Add(Findings.Create(FindingCode.DriverDoesNotListForm, Severity.Error,
                    Inv($"The driver of '{caps.QueueName}' lists neither the form '{name}' nor a paper of the ticket size ({ticket.WidthTenthsOfMillimeter} x {ticket.HeightTenthsOfMillimeter} tenths of a mm, custom size range {range}). ")
                    + "Add a custom page size of the ticket size in the driver's printing preferences.", range.Data(caps.QueueName)), blocker: true);
            }
            else
            {
                string how = Same(paper.Name, name) ? "" : $" with the ticket size; it does not list the form '{name}' by name";
                Add(Findings.Info(FindingCode.DriverListsForm,
                    Inv($"The driver of '{caps.QueueName}' lists '{paper.Name}' as paper {paper.Id} ({paper.WidthTenthsOfMillimeter} x {paper.HeightTenthsOfMillimeter} tenths of a mm){how}."),
                    ("queue", caps.QueueName), ("paperId", paper.Id.ToString(CultureInfo.InvariantCulture)), ("paper", paper.Name)));
            }
            return paper;
        }

        // The form does not exist yet (or is wrong), so the driver cannot list it: check that it accepts the size at all.
        var driverRange = DriverSizeRange.Read(_services.Spooler, caps, _services.Log);
        if (driverRange.Excludes(ticket))
        {
            Add(Findings.Create(FindingCode.TicketSizeOutsideDriverRange, Severity.Error,
                Inv($"The ticket ({ticket.WidthTenthsOfMillimeter} x {ticket.HeightTenthsOfMillimeter} tenths of a mm) is outside the custom size range {driverRange} of the driver of '{caps.QueueName}'."),
                driverRange.Data(caps.QueueName)), blocker: true);
        }
        return null;
    }

    private void ReportSource(SourceDecision source, ModelDefinition model, DriverCapabilities caps)
    {
        if (source.Status != SourceStatus.NotFound)
            return;

        var rule = model.TicketPaperSource;
        string detail = Inv($"The driver of '{caps.QueueName}' exposes no multipurpose tray (bin ids {string.Join(", ", rule.BinIds)}; names {string.Join(", ", rule.BinNames)}); it offers: ")
            + string.Join(", ", caps.Bins.Select(b => Inv($"{b.Id} '{b.Name}'"))) + ".";
        if (source.Required)
            Add(Findings.Error(FindingCode.PaperSourceNotFound, detail, ("queue", caps.QueueName)), blocker: true);
        else
            Add(Findings.Warning(FindingCode.PaperSourceNotFound, detail + " The driver's default source is kept.", ("queue", caps.QueueName)));
    }

    private void AssessDefaults(PrintQueueInfo queue, DesiredDefaults desired, bool act)
    {
        if (TryReadDevMode(queue.Name, DevModeScope.Global, out var global))
        {
            var accepted = global is null ? desired : AcceptDriverSource(queue.Name, desired, global);
            var differences = global is null ? ["no global default"] : accepted.Differences(global);
            if (differences.Count > 0)
            {
                Add(Findings.Warning(FindingCode.GlobalDefaultsIncorrect,
                    $"The printing defaults of '{queue.Name}' differ from the ticket settings: {string.Join(", ", differences)}.",
                    ("queue", queue.Name), ("fields", string.Join(",", differences))));
                if (act)
                    PlanDefault(ActionKind.ConfigureGlobalDefaults, queue.Name);
            }
            else
            {
                string kept = global!.DefaultSource == accepted.SourceSubstitute
                    ? Inv($" (the driver keeps source {global.DefaultSource} instead of the multipurpose tray, which is only preferred)")
                    : "";
                Add(Findings.Info(FindingCode.GlobalDefaultsCorrect, $"The printing defaults of '{queue.Name}' match the ticket settings{kept}.", ("queue", queue.Name)));
            }
        }
        else if (act)
        {
            PlanDefault(ActionKind.ConfigureGlobalDefaults, queue.Name);
        }

        if (!TryReadDevMode(queue.Name, DevModeScope.PerUser, out var perUser))
        {
            if (act)
                PlanDefault(ActionKind.ConfigureUserDefaults, queue.Name);
            return;
        }

        if (perUser is null)
        {
            Add(Findings.Info(FindingCode.UserDefaultsAbsent, $"The current user has no own printing defaults for '{queue.Name}'; the queue defaults apply.", ("queue", queue.Name)));
            return;
        }

        var userDifferences = AcceptDriverSource(queue.Name, desired, perUser).Differences(perUser);
        if (userDifferences.Count == 0)
        {
            Add(Findings.Info(FindingCode.UserDefaultsCorrect, $"The current user's printing defaults for '{queue.Name}' match the ticket settings.", ("queue", queue.Name)));
            return;
        }

        Add(Findings.Warning(FindingCode.UserDefaultsIncorrect,
            $"The current user's printing defaults for '{queue.Name}' override the queue defaults and differ: {string.Join(", ", userDifferences)}.",
            ("queue", queue.Name), ("fields", string.Join(",", userDifferences))));
        if (act)
            PlanDefault(ActionKind.ConfigureUserDefaults, queue.Name);
    }

    private void PlanDefaults(string? queueName)
    {
        PlanDefault(ActionKind.ConfigureGlobalDefaults, queueName);
        PlanDefault(ActionKind.ConfigureUserDefaults, queueName);
    }

    private void PlanDefault(ActionKind kind, string? queueName)
    {
        string queue = queueName is null ? "the printer's queue" : $"queue '{queueName}'";
        string settings = $"'{_config.Ticket.FormName}', {TicketDefaults.DescribePreferences(_config.PrintPreferences)}";
        bool global = kind == ActionKind.ConfigureGlobalDefaults;
        Plan(kind, global,
            global ? $"Set the printing defaults of {queue} for all users: {settings}." : $"Set the current user's printing defaults of {queue}: {settings}.",
            (QueueName, queueName), (FormName, _config.Ticket.FormName));
    }

    private void PlanForm(ActionKind kind, PaperDimensions ticket)
    {
        string name = _config.Ticket.FormName;
        string size = TicketDefaults.Micrometers(ticket.WidthMicrometers, ticket.HeightMicrometers);
        Plan(kind, true, kind == ActionKind.CreateForm ? $"Create the form '{name}' ({size})." : $"Resize the form '{name}' to {size}.",
            (FormName, name),
            (WidthMicrometers, ticket.WidthMicrometers.ToString(CultureInfo.InvariantCulture)),
            (HeightMicrometers, ticket.HeightMicrometers.ToString(CultureInfo.InvariantCulture)));
    }

    private void PlanPrepare(DriverPackageDefinition package) =>
        Plan(ActionKind.PrepareDriverPackage, true, $"Obtain and verify driver package '{package.DisplayName}' {package.Version} ({package.Id}).", (PackageId, package.Id));

    private void CheckPackage(DriverPackageDefinition package, PlatformInfo platform)
    {
        if (_packageChecked)
            return;
        _packageChecked = true;

        foreach (var (code, detail) in PackageProblems(package, platform))
        {
            Add(Findings.Error(code, code == FindingCode.DriverPackageNotApproved ? "The driver must be installed, but " + detail : detail, ("package", package.Id)),
                blocker: true);
        }
    }

    /// <summary>Why the package may not be installed on this PC: it is not approved, or it needs a later Windows build.</summary>
    private static List<(FindingCode Code, string Detail)> PackageProblems(DriverPackageDefinition package, PlatformInfo platform)
    {
        var problems = new List<(FindingCode Code, string Detail)>();
        if (!package.Approved)
            problems.Add((FindingCode.DriverPackageNotApproved, $"package '{package.Id}' is not approved; unapproved packages are never installed."));
        if (platform.Build > 0 && platform.Build < package.MinOsBuild)
            problems.Add((FindingCode.DriverPackageIncompatible, Inv($"Driver package '{package.Id}' needs Windows build {package.MinOsBuild} or later; this PC runs build {platform.Build}.")));
        return problems;
    }

    private void Plan(ActionKind kind, bool requiresElevation, string summary, params (string Key, string? Value)[] parameters)
    {
        if (_actions.Any(a => a.Kind == kind))
            return;

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in parameters)
        {
            if (!string.IsNullOrEmpty(value))
                values[key] = value;
        }
        _actions.Add(new PlannedAction(kind, requiresElevation, summary) { Parameters = values });
    }

    private void Add(Finding finding, bool blocker = false)
    {
        _findings.Add(finding);
        if (blocker)
            _blockers.Add(finding);
    }

    /// <summary>
    /// The Plug and Play printer devices, read fresh; null when they cannot be read. The target's queues are then only those
    /// detection gave it (<see cref="LocateQueue"/>), and the report says so.
    /// </summary>
    private IReadOnlyList<PnpPrinterDevice>? ReadDevices(PrinterCandidate target)
    {
        try
        {
            return _services.Devices.GetPrinterDevices();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _services.Log.Warn("assess.read.failed", "Could not read devices.", new Dictionary<string, object?> { ["source"] = "devices" }, ex);
            var detected = target.AdditionalQueues.Prepend(target.Queue).OfType<PrintQueueInfo>().ToList();
            string used = detected.Count == 0
                ? "detection found no queue for the printer"
                : $"only the queues detection found for the printer are used ({string.Join(", ", detected.Select(q => $"'{q.Name}'"))})";
            Add(Findings.Warning(FindingCode.OperationFailed,
                $"The printer devices could not be read ({ex.Message}); {used}, and no other queue is adopted or given another driver for it.",
                ("candidate", target.Key)));
            return null;
        }
    }

    /// <summary>The target's device as <paramref name="devices"/> (a fresh read) shows it; detection's when they could not be read.</summary>
    private static PnpPrinterDevice? FreshDevice(PrinterCandidate target, IReadOnlyList<PnpPrinterDevice>? devices)
    {
        if (target.Device is not { } known)
            return null;
        if (devices is null)
            return known;
        return devices.FirstOrDefault(d => Same(d.InstanceId, known.InstanceId)) ?? known with { IsPresent = false };
    }

    /// <summary>
    /// The target's queue in the current state: among the printer's queues the one detection chose (the queue already set up as
    /// the ticket printer), else <see cref="QueueLocator.Primary"/>; else by its name. A USB printer's queues are those detection
    /// gives it (<see cref="QueueLocator.ForUsbDevice"/>): a queue another printer on its ports uses is never an earlier unit's.
    /// When the devices cannot be read (<paramref name="devices"/> is null), which printer uses a queue on the port is not known,
    /// so only the queues detection gave the target are its own: no other queue is adopted or given another driver for it.
    /// </summary>
    private PrintQueueInfo? LocateQueue(PrinterCandidate target, ModelDefinition model, PnpPrinterDevice? device, IReadOnlyList<PnpPrinterDevice>? devices, SpoolerState state)
    {
        switch (target.Kind)
        {
            case TargetKind.UsbDevice when device is not null:
                var usbDevice = device with { UsbPortName = device.UsbPortName ?? target.Device?.UsbPortName };
                var onPort = devices is null
                    ? QueueLocator.OnPort(state.Queues, usbDevice.UsbPortName).Where(q => QueueLocator.IsDetectedQueue(target, q)).ToList()
                    : QueueLocator.ForUsbDevice(state.Queues, usbDevice, devices, _matcher);
                if (onPort.Count > 0)
                    return QueueLocator.ByName(onPort, target.Queue?.Name) ?? QueueLocator.Primary(onPort, model);
                break;
            case TargetKind.NetworkAddress when target.NetworkAddress is not null:
                // Queues on any spelling of the printer count, so a printer never gets a second port and queue for another spelling.
                var forHost = QueueLocator.ForHosts(state.Queues, state.Ports, QueueLocator.NetworkHosts(target));
                if (forHost.Count > 0)
                    return QueueLocator.ByName(forHost, target.Queue?.Name) ?? QueueLocator.Primary(forHost, model);
                break;
        }
        return QueueLocator.ByName(state.Queues, target.Queue?.Name);
    }

    private DriverCapabilities? ReadCapabilities(string queueName)
    {
        try
        {
            return _services.Spooler.GetCapabilities(queueName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Add(Findings.Warning(FindingCode.PrintCapabilitiesUnavailable,
                $"The driver capabilities of '{queueName}' could not be read ({ex.Message}); paper support is checked when the defaults are set.", ("queue", queueName)));
            _services.Log.Warn("assess.capabilities.failed", $"GetCapabilities failed for '{queueName}'.", null, ex);
            return null;
        }
    }

    /// <summary>
    /// The desired defaults accepting what the driver keeps instead of a multipurpose tray that is only preferred
    /// (<see cref="TicketDefaults.AcceptDriverSource"/>); unchanged when the driver cannot validate them (the write then decides).
    /// </summary>
    private DesiredDefaults AcceptDriverSource(string queueName, DesiredDefaults desired, DevMode current)
    {
        try
        {
            return TicketDefaults.AcceptDriverSource(desired, _services.Spooler, queueName, current);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _services.Log.Warn("assess.devmode.validate.failed", $"ValidateDevMode failed for '{queueName}'.", null, ex);
            return desired;
        }
    }

    private bool TryReadDevMode(string queueName, DevModeScope scope, out DevMode? devMode)
    {
        try
        {
            devMode = _services.Spooler.GetDevMode(queueName, scope);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Add(Findings.Warning(FindingCode.OperationFailed, $"The {scope} printing defaults of '{queueName}' could not be read: {ex.Message}", ("queue", queueName)));
            _services.Log.Warn("assess.devmode.failed", $"GetDevMode({scope}) failed for '{queueName}'.", null, ex);
            devMode = null;
            return false;
        }
    }

    private T? Read<T>(string source, Func<T> read) where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _services.Log.Warn("assess.read.failed", $"Could not read {source}.", new Dictionary<string, object?> { ["source"] = source }, ex);
            return null;
        }
    }

    private static string Describe(PaperRangeMm range) =>
        Inv($"{range.MinWidth}x{range.MinHeight}..{range.MaxWidth}x{range.MaxHeight} mm");

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
