using System.Globalization;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Detection;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Units;
using static Amwag.PrinterSetup.Core.Setup.ActionParameters;

namespace Amwag.PrinterSetup.Core.Setup;

/// <summary>
/// Executes a plan's actions in one scope. Every action re-reads the current state first (AlreadySatisfied when nothing is
/// needed), acts, then verifies its postcondition from a fresh read. Failures never throw: they become Failed results with a
/// specific code, and the actions that depend on them are skipped. Cancellation skips the rest and reports Cancelled.
/// </summary>
internal sealed class ActionRunner
{
    /// <summary>SetupAPI ERROR_NO_SUCH_DEVINST (UpdateDriverForPlugAndPlayDevices): no present device has the hardware ID.</summary>
    public const int ErrorNoSuchDevinst = unchecked((int)0xE000020B);

    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SpoolerStartTimeout = TimeSpan.FromSeconds(30);

    // The queue actions use an installed driver and never the package itself: they depend on the package only through
    // InstallDriver, so a package that is needed just to bind the USB device does not hold them back.
    private static readonly Dictionary<ActionKind, ActionKind[]> Dependencies = new()
    {
        [ActionKind.PrepareDriverPackage] = [ActionKind.StartSpooler],
        [ActionKind.InstallDriver] = [ActionKind.StartSpooler, ActionKind.PrepareDriverPackage],
        [ActionKind.BindUsbDevice] = [ActionKind.PrepareDriverPackage, ActionKind.InstallDriver],
        [ActionKind.CreateTcpIpPort] = [ActionKind.StartSpooler],
        [ActionKind.CreateQueue] = [ActionKind.StartSpooler, ActionKind.InstallDriver, ActionKind.CreateTcpIpPort],
        [ActionKind.ChangeQueueDriver] = [ActionKind.StartSpooler, ActionKind.InstallDriver],
        [ActionKind.ChangeQueuePort] = [ActionKind.StartSpooler, ActionKind.CreateTcpIpPort],
        [ActionKind.CreateForm] = [ActionKind.StartSpooler],
        [ActionKind.UpdateForm] = [ActionKind.StartSpooler],
        [ActionKind.ConfigureGlobalDefaults] = [ActionKind.StartSpooler, ActionKind.CreateQueue, ActionKind.ChangeQueueDriver, ActionKind.CreateForm, ActionKind.UpdateForm],
        [ActionKind.ConfigureUserDefaults] = [ActionKind.StartSpooler, ActionKind.CreateQueue, ActionKind.ChangeQueueDriver, ActionKind.CreateForm, ActionKind.UpdateForm],
    };

    private readonly DeploymentConfig _config;
    private readonly BranchOverride? _branch;
    private readonly SetupOptions _options;
    private readonly SetupServices _services;
    private readonly SetupPlan _plan;
    private readonly IProgress<ProgressUpdate>? _progress;
    private readonly CancellationToken _cancellationToken;
    private readonly ModelMatcher _matcher;

    private readonly List<ActionResult> _results = [];
    private readonly List<Finding> _findings = [];
    // Failed actions and actions skipped because of them, with the failed action that caused it.
    private readonly Dictionary<ActionKind, ActionKind> _failed = [];
    private PreparedDriverPackage? _prepared;
    // The package that could not be obtained in this run, so it is not tried a second time.
    private (string PackageId, PrinterSetupException Error)? _packageFailure;
    private string? _queueName;
    private string? _portName;
    private bool _driverChanged;
    // A queue moved to another driver for the ticket: its previous driver and global defaults, restored when the ticket
    // defaults then fail (RestoreDriverName).
    private (string Queue, string Driver, string NewDriver, DevMode? Global)? _restorePoint;
    private bool _rebootRequired;
    private bool _cancelled;
    // Why the Plug and Play devices could not be read last, and whether the report already says so.
    private string? _devicesError;
    private bool _devicesUnreadableReported;

    public ActionRunner(
        DeploymentConfig config,
        BranchOverride? branch,
        SetupOptions options,
        SetupServices services,
        SetupPlan plan,
        IProgress<ProgressUpdate>? progress,
        CancellationToken cancellationToken)
    {
        _config = config;
        _branch = branch;
        _options = options;
        _services = services;
        _plan = plan;
        _progress = progress;
        _cancellationToken = cancellationToken;
        _matcher = new ModelMatcher(config.Models);
    }

    private ModelDefinition Model => _plan.Target.Model
        ?? throw new PrinterSetupException(FindingCode.ProbableMatchRequiresConfirmation, "The printer's model has not been identified.");

    public ApplyResult Run(ApplyScope scope)
    {
        var actions = _plan.Actions.Where(a => InScope(a, scope)).ToList();
        _services.Log.Info("apply.start", $"Applying {actions.Count} action(s) for {_plan.Target.Key} in scope {scope}.", new Dictionary<string, object?>
        {
            ["target"] = _plan.Target.Key,
            ["scope"] = scope.ToString(),
            ["actions"] = actions.Select(a => a.Kind.ToString()).ToArray(),
        });

        if (_plan.Mode == SetupMode.Diagnostics)
        {
            foreach (var action in actions)
                Skip(action, "Diagnostics mode never changes the system.");
            return Result();
        }

        if (!_plan.CanProceed)
        {
            string reason = "Blocked: " + string.Join(", ", _plan.Blockers.Select(b => b.Code.ToString()).Distinct()) + "; nothing was changed.";
            foreach (var action in actions)
                Skip(action, reason);
            _findings.AddRange(_plan.Blockers);
            return Result();
        }

        foreach (var action in actions)
        {
            if (_cancelled || _cancellationToken.IsCancellationRequested)
            {
                _cancelled = true;
                Skip(action, "Cancelled before it started.");
            }
            else if (FailedDependency(action.Kind) is { } dependency)
            {
                _failed[action.Kind] = dependency;
                Skip(action, $"Skipped because it depends on a failed action ({dependency}).");
            }
            else
            {
                Execute(action);
            }
        }

        RestoreDriverIfTicketDefaultsFailed();
        if (_cancelled)
            _findings.Add(Findings.Warning(FindingCode.Cancelled, "The operation was cancelled; actions that had not finished were skipped. Completed changes were kept."));
        if (_rebootRequired)
            _findings.Add(Findings.Warning(FindingCode.RebootRequired, "Windows must be restarted to finish installing the printer driver."));
        return Result();
    }

    private static bool InScope(PlannedAction action, ApplyScope scope) => scope switch
    {
        ApplyScope.Machine => action.RequiresElevation,
        ApplyScope.User => !action.RequiresElevation,
        _ => true,
    };

    private ActionKind? FailedDependency(ActionKind kind)
    {
        if (!Dependencies.TryGetValue(kind, out var dependencies))
            return null;
        foreach (var dependency in dependencies)
        {
            if (_failed.TryGetValue(dependency, out var cause))
                return cause;
        }
        return null;
    }

    private void Execute(PlannedAction action)
    {
        var step = StepOf(action.Kind);
        Report(step, StepState.Running, action, ProgressPhase.Started, action.Summary);
        _services.Log.Info("apply.action.start", action.Summary, Data(action));
        try
        {
            var (outcome, detail) = Perform(action);
            _results.Add(new ActionResult(action, outcome, detail));
            bool skipped = outcome == ActionOutcome.Skipped;
            Report(step, skipped ? StepState.Skipped : StepState.Succeeded, action, skipped ? ProgressPhase.Skipped : ProgressPhase.Completed, detail);
            _services.Log.Info(skipped ? "apply.action.skipped" : "apply.action.completed", detail, Data(action, ("outcome", outcome.ToString())));
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
        {
            _cancelled = true;
            Skip(action, "Cancelled while running; this step was not completed.");
        }
        catch (PrinterSetupException ex)
        {
            Fail(action, CodeFor(action.Kind, ex), ex.Message, ex.Win32Error, ex);
        }
        catch (Exception ex)
        {
            Fail(action, FindingCode.OperationFailed, $"Unexpected error: {ex.Message}", null, ex);
        }
    }

    private (ActionOutcome Outcome, string Detail) Perform(PlannedAction action) => action.Kind switch
    {
        ActionKind.StartSpooler => StartSpooler(),
        ActionKind.PrepareDriverPackage => PrepareDriverPackage(action),
        ActionKind.InstallDriver => InstallDriver(action),
        ActionKind.BindUsbDevice => BindUsbDevice(action),
        ActionKind.CreateTcpIpPort => CreateTcpIpPort(action),
        ActionKind.CreateQueue => CreateQueue(action),
        ActionKind.ChangeQueueDriver => ChangeQueueDriver(action),
        ActionKind.ChangeQueuePort => ChangeQueuePort(action),
        ActionKind.CreateForm or ActionKind.UpdateForm => EnsureForm(action),
        ActionKind.ConfigureGlobalDefaults => ConfigureDefaults(action, DevModeScope.Global),
        ActionKind.ConfigureUserDefaults => ConfigureDefaults(action, DevModeScope.PerUser),
        _ => throw new PrinterSetupException(FindingCode.OperationFailed, $"Unknown action {action.Kind}."),
    };

    // ---- Spooler -------------------------------------------------------------------------------------------------

    private (ActionOutcome, string) StartSpooler()
    {
        var status = _services.Spooler.GetServiceStatus();
        if (status.IsRunning)
            return Already("The Print Spooler service is running.");
        if (status.StartMode == ServiceStartMode.Disabled)
            throw new PrinterSetupException(FindingCode.SpoolerDisabled, "The Print Spooler service is disabled, so it cannot be started.");
        if (!_services.Spooler.TryStartService(SpoolerStartTimeout, out string? reason))
            throw new PrinterSetupException(FindingCode.SpoolerNotRunning, $"The Print Spooler service could not be started: {reason}");

        var after = _services.Spooler.GetServiceStatus();
        if (!after.IsRunning)
            throw new PrinterSetupException(FindingCode.SpoolerNotRunning, $"The Print Spooler service is {after.State} after being started.");
        return Done("Started the Print Spooler service.");
    }

    // ---- Driver --------------------------------------------------------------------------------------------------

    private (ActionOutcome, string) PrepareDriverPackage(PlannedAction action)
    {
        var package = Package(action);
        var model = Model;
        var drivers = _services.Spooler.GetDrivers();
        string driverName = DriverRules.DriverForQueue(model, drivers, package.Environment);
        // Ready: the queue's driver is installed and no planned install still needs the package.
        bool driverReady = DriverRules.Check(model, driverName, drivers, package.Environment).IsInstalled && !InstallNeeded(drivers, package);
        string? binding = PendingBinding();
        if (driverReady && binding is null)
            return Already($"Driver '{driverName}' is already installed; package '{package.Id}' is not needed.");

        PreparedDriverPackage prepared;
        try
        {
            prepared = EnsurePrepared(package);
        }
        catch (PrinterSetupException ex) when (driverReady && binding is not null)
        {
            return BindingPackageUnavailable(action, package, binding, driverName, ex);
        }
        return Done($"Package '{package.Id}' {package.Version} is verified and extracted ({prepared.Origin}; signed by {prepared.CatalogSignature.SignerSubject ?? "unknown"}).");
    }

    /// <summary>
    /// The package was needed only to install the driver on the USB device. The queue and its defaults use the installed
    /// driver, so this is a warning; BindUsbDevice is then skipped without a second attempt. A package that could not be
    /// obtained (offline, timeout, download refused) leaves the device unbound (DeviceDriverNotBound with the reason); one that
    /// failed a check (integrity, signature, approval, configuration) keeps its own code, so that sign of a damaged or altered
    /// package reaches IT as it does when the package is needed for the install.
    /// </summary>
    private (ActionOutcome, string) BindingPackageUnavailable(PlannedAction action, DriverPackageDefinition package, string hardwareId, string driverName, PrinterSetupException ex)
    {
        var code = CodeFor(action.Kind, ex);
        bool unavailable = code == FindingCode.DriverPackageUnavailable;
        string queueText = $"The installed driver '{driverName}' is used for the printer's queue.";
        _findings.Add(unavailable
            ? Findings.Warning(FindingCode.DeviceDriverNotBound,
                $"The approved driver could not be installed on the USB printer ({hardwareId}) because package '{package.Id}' could not be obtained: {ex.Message} {queueText}",
                ("package", package.Id), ("hardwareId", hardwareId), ("reason", code.ToString()))
            : Findings.Warning(code,
                $"Package '{package.Id}', needed only to install the driver on the USB printer ({hardwareId}), was rejected and nothing from it was used: {ex.Message} " +
                $"{queueText} The USB printer keeps its current driver.",
                ("package", package.Id), ("hardwareId", hardwareId), ("action", action.Kind.ToString()), ("win32Error", ex.Win32Error?.ToString(CultureInfo.InvariantCulture))));
        _services.Log.Warn("apply.package.bindingOnly", $"Package '{package.Id}' is needed only to bind the USB printer and {Unusable(code)}.",
            Data(action, ("code", code.ToString()), ("driver", driverName)), ex);
        return (ActionOutcome.Skipped, $"Package '{package.Id}' is needed only to install the driver on the USB printer and {Unusable(code)} ({code}); the installed driver '{driverName}' is used.");
    }

    private static string Unusable(FindingCode code) => code == FindingCode.DriverPackageUnavailable ? "could not be obtained" : "was rejected";

    private (ActionOutcome, string) InstallDriver(PlannedAction action)
    {
        var package = Package(action);
        var model = Model;
        string driverName = Param(action, DriverName) ?? model.PreferredDriverName;
        string environment = Param(action, DriverEnvironment) ?? package.Environment;
        var before = DriverRules.Check(model, driverName, _services.Spooler.GetDrivers(), environment);
        if (before.IsInstalled)
            return Already(before.Detail);

        var prepared = EnsurePrepared(package);
        Report(StepId.InstallDriver, StepState.Running, action, ProgressPhase.Installing, $"Installing driver '{driverName}'.");
        string storeInf = _services.Installer.StagePackage(prepared);
        _services.Installer.InstallPrinterDriver(storeInf, driverName, environment);
        _driverChanged = true;

        var after = DriverRules.Check(model, driverName, _services.Spooler.GetDrivers(), environment);
        if (!after.IsInstalled)
            throw new PrinterSetupException(FindingCode.DriverInstallFailed, $"Installation reported success, but {after.Detail}");
        string version = after.Driver?.Version is { } installed ? " " + installed : "";
        return Done($"Installed driver '{driverName}'{version} from {storeInf}.");
    }

    private (ActionOutcome, string) BindUsbDevice(PlannedAction action)
    {
        string hardwareId = Param(action, HardwareId)
            ?? throw new PrinterSetupException(FindingCode.OperationFailed, "The binding action has no hardware ID.");
        var device = FindDevice(Param(action, InstanceId), hardwareId);
        if (device is not { IsPresent: true })
            return NoPresentDevice(hardwareId);
        if (IsBound(device))
            return Already($"The USB printer already uses driver '{device.DriverDescription}'{NoteRestart(device)}.");

        var package = Package(action);
        // Reached after a failure only when the package was needed just for this binding (the warning is already reported).
        if (_packageFailure is { } failure && Same(failure.PackageId, package.Id))
            return (ActionOutcome.Skipped, $"The driver was not installed on the USB printer ({hardwareId}): package '{package.Id}' {Unusable(CodeFor(ActionKind.PrepareDriverPackage, failure.Error))}.");

        var prepared = EnsurePrepared(package);
        Report(StepId.InstallDriver, StepState.Running, action, ProgressPhase.Installing, $"Installing the driver on the USB printer ({hardwareId}).");
        var result = _services.Installer.InstallForDevice(prepared, hardwareId);
        _rebootRequired |= result.RebootRequired;
        if (!result.Succeeded)
        {
            if (result.Win32Error == ErrorNoSuchDevinst)
                return NoPresentDevice(hardwareId);
            throw new PrinterSetupException(FindingCode.DriverInstallFailed, $"Installing the driver on {hardwareId} failed: {result.Detail}", result.Win32Error);
        }

        _driverChanged = true;
        if (result.RebootRequired)
            return Done($"Installed the driver on the USB printer; Windows must restart to finish. {result.Detail}");

        var after = FindDevice(device.InstanceId, hardwareId);
        if (after is null || !IsBound(after))
        {
            string state = after?.ProblemCode is { } problem
                ? FormattableString.Invariant($"problem code {problem}")
                : $"driver '{after?.DriverDescription ?? "none"}'";
            throw new PrinterSetupException(FindingCode.DeviceDriverNotBound, $"After installation the USB printer still reports {state}.");
        }
        return Done($"The USB printer now uses driver '{after.DriverDescription}'{NoteRestart(after)}.");
    }

    /// <summary>"; Windows must restart to finish" for a device whose accepted driver waits for a restart (also remembered for the result).</summary>
    private string NoteRestart(PnpPrinterDevice device)
    {
        if (!DriverRules.IsRestartPending(device, Model))
            return "";
        _rebootRequired = true;
        return "; Windows must restart to finish installing it";
    }

    private (ActionOutcome, string) NoPresentDevice(string hardwareId)
    {
        _findings.Add(Findings.Warning(FindingCode.DeviceNotConnected,
            $"No connected printer has hardware ID {hardwareId}; Windows uses the installed driver when the printer is connected.", ("hardwareId", hardwareId)));
        return (ActionOutcome.Skipped, "No connected device to install the driver on.");
    }

    /// <summary>The verified package, obtained at most once per run: a failure is remembered and reported again without a retry.</summary>
    private PreparedDriverPackage EnsurePrepared(DriverPackageDefinition package)
    {
        if (_prepared is not null && Same(_prepared.Definition.Id, package.Id))
            return _prepared;
        // A second attempt could wait for the whole download timeout again and fail the same way.
        if (_packageFailure is { } failure && Same(failure.PackageId, package.Id))
            throw new PrinterSetupException(failure.Error.Code, failure.Error.Message, failure.Error.Win32Error, failure.Error);

        try
        {
            return _prepared = Prepare(package);
        }
        catch (PrinterSetupException ex)
        {
            _packageFailure = (package.Id, ex);
            throw;
        }
    }

    private PreparedDriverPackage Prepare(DriverPackageDefinition package)
    {
        if (!package.Approved)
            throw new PrinterSetupException(FindingCode.DriverPackageNotApproved, $"Driver package '{package.Id}' is not approved for installation.");

        var prepared = _services.Packages.Prepare(package, new StepProgress(_progress), _cancellationToken);
        if (!Same(prepared.Definition.Id, package.Id))
            throw new PrinterSetupException(FindingCode.DriverPackageIntegrityFailed, $"Package '{prepared.Definition.Id}' was prepared instead of '{package.Id}'.");
        if (!prepared.CatalogSignature.IsTrusted)
        {
            throw new PrinterSetupException(FindingCode.DriverSignatureInvalid,
                $"The catalog signature of package '{package.Id}' is not trusted: {prepared.CatalogSignature.StatusText}", prepared.CatalogSignature.StatusCode);
        }

        _services.Log.Info("apply.package.prepared", $"Package '{package.Id}' is ready.", new Dictionary<string, object?>
        {
            ["package"] = package.Id,
            ["origin"] = prepared.Origin.ToString(),
            ["inf"] = prepared.InfPath,
            ["signer"] = prepared.CatalogSignature.SignerSubject,
        });
        return prepared;
    }

    /// <summary>Whether a planned InstallDriver still has to install its driver (its own precondition, re-read now).</summary>
    private bool InstallNeeded(IReadOnlyList<PrinterDriverInfo> drivers, DriverPackageDefinition package) =>
        _plan.Actions.FirstOrDefault(a => a.Kind == ActionKind.InstallDriver) is { } install &&
        !DriverRules.Check(Model, Param(install, DriverName) ?? Model.PreferredDriverName, drivers, Param(install, DriverEnvironment) ?? package.Environment).IsInstalled;

    /// <summary>The hardware ID of the planned BindUsbDevice when its device is present and not bound to an accepted driver.</summary>
    private string? PendingBinding() =>
        _plan.Actions.FirstOrDefault(a => a.Kind == ActionKind.BindUsbDevice) is { } bind && BindNeeded(bind) ? Param(bind, HardwareId) : null;

    private bool BindNeeded(PlannedAction bind)
    {
        string? hardwareId = Param(bind, HardwareId);
        if (hardwareId is null)
            return false;
        try
        {
            return FindDevice(Param(bind, InstanceId), hardwareId) is { IsPresent: true } device && !IsBound(device);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Unknown device state: prepare the package; the binding step reports its own problem.
            _services.Log.Warn("apply.devices.failed", "Could not read the Plug and Play devices.", null, ex);
            return true;
        }
    }

    private bool IsBound(PnpPrinterDevice device) => DriverRules.IsBound(device, Model);

    // ---- Port and queue ------------------------------------------------------------------------------------------

    private (ActionOutcome, string) CreateTcpIpPort(PlannedAction action)
    {
        string address = Param(action, HostAddress) ?? _plan.Target.NetworkAddress
            ?? throw new PrinterSetupException(FindingCode.PortCreateFailed, "The printer has no network address.");
        int portNumber = int.TryParse(Param(action, PortNumber), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : _config.Connection.RawPortNumber;

        var ports = _services.Spooler.GetPorts();
        // A port to another spelling of the printer (its IP address, or another configured name) already sends to it.
        if ((QueueLocator.ReusableTcpPort(ports, address, portNumber) ?? ReusableTcpPort(ports, portNumber)) is { } existing)
        {
            _portName = existing.Name;
            string host = Same(existing.HostAddress, address) ? "" : $" as {existing.HostAddress}";
            return Already(Inv($"Port '{existing.Name}' already sends to {address}{host}:{portNumber}."));
        }

        string name = Param(action, PortName) is { } planned && QueueLocator.FindPort(ports, planned) is null
            ? planned
            : QueueLocator.NewTcpPortName(address, ports);
        Report(StepId.InstallDriver, StepState.Running, action, ProgressPhase.Configuring, $"Creating port '{name}'.");
        _services.Spooler.AddStandardTcpIpPort(new TcpIpPortRequest(name, address, portNumber, _config.Connection.SnmpEnabled, _config.Connection.SnmpCommunity));

        var created = QueueLocator.FindPort(_services.Spooler.GetPorts(), name);
        if (created is not { Kind: PortKind.StandardTcpIp } || !Same(created.HostAddress, address) || created.PortNumber != portNumber)
            throw new PrinterSetupException(FindingCode.PortCreateFailed, Inv($"Port '{name}' is not present for {address}:{portNumber} after it was created."));
        _portName = name;
        return Done(Inv($"Created Standard TCP/IP port '{name}' for {address}:{portNumber}."));
    }

    private (ActionOutcome, string) CreateQueue(PlannedAction action)
    {
        var model = Model;
        string driverName = Param(action, DriverName) ?? model.PreferredDriverName;
        string environment = Package(action).Environment;
        return Param(action, Connection) == UsbConnection || _plan.Target.Kind == TargetKind.UsbDevice
            ? CreateUsbQueue(action, model, driverName, environment)
            : CreateNetworkQueue(action, model, driverName, environment);
    }

    private (ActionOutcome, string) CreateUsbQueue(PlannedAction action, ModelDefinition model, string driverName, string environment)
    {
        string? instanceId = Param(action, InstanceId) ?? _plan.Target.Device?.InstanceId;
        var device = TryFindDevice(instanceId);
        string? port = device?.UsbPortName ?? Param(action, PortName);

        if (QueueOnPort(port, model) is { } existing)
            return Adopt(action, existing, model, driverName, environment, $"Queue '{existing.Name}' already prints to {port}", createdNow: false);

        // After a driver install Windows Plug and Play usually creates the queue itself; creating one too would duplicate it.
        if (device is { IsPresent: true } && _driverChanged)
        {
            var (created, currentPort) = WaitForPlugAndPlayQueue(action, instanceId, model, port);
            port = currentPort;
            if (created is not null)
                return Adopt(action, created, model, driverName, environment, $"Windows Plug and Play created queue '{created.Name}' on {created.PortName}", createdNow: true);
        }

        if (string.IsNullOrEmpty(port))
            throw new PrinterSetupException(FindingCode.QueueCreateFailed, "The USB port of the printer is not known; reconnect the printer and run the setup again.");
        // Not the printer's queue, yet it prints to its port (a pool a connected printer uses, or the queue of the printer the port
        // now leads to): a queue added there would print to the same printer. The worker plans from its own reads, so this holds
        // whatever the window planned.
        if (QueueLocator.OnPort(_services.Spooler.GetQueues(), port).FirstOrDefault() is { } other)
        {
            throw new PrinterSetupException(FindingCode.QueueLeftUnchanged,
                $"Queue '{other.Name}' (ports {other.PortName}), which the setup does not configure for this printer, already prints to {port}, the port of the USB printer {model.Id}, so no second queue is added. "
                + "Set up the printer that queue belongs to, or connect this printer and run the setup again if it is the ticket printer.");
        }
        return AddQueue(action, model, driverName, environment, port, usb: true);
    }

    private (ActionOutcome, string) CreateNetworkQueue(PlannedAction action, ModelDefinition model, string driverName, string environment)
    {
        string address = Param(action, HostAddress) ?? _plan.Target.NetworkAddress
            ?? throw new PrinterSetupException(FindingCode.QueueCreateFailed, "The printer has no network address.");
        var ports = _services.Spooler.GetPorts();
        var queues = _services.Spooler.GetQueues();
        // A queue that prints to another spelling of the printer (its host name or IP address) is the printer's queue as well.
        string[] hosts = [address, .. QueueLocator.NetworkHosts(_plan.Target)];
        if (QueueLocator.Primary(QueueLocator.ForHosts(queues, ports, hosts), model) is { } existing)
            return Adopt(action, existing, model, driverName, environment, $"Queue '{existing.Name}' already prints to {QueueLocator.HostOf(existing, ports) ?? address}", createdNow: false);

        string port = _portName ?? Param(action, PortName)
            ?? (QueueLocator.ReusableTcpPort(ports, address, _config.Connection.RawPortNumber) ?? ReusableTcpPort(ports, _config.Connection.RawPortNumber))?.Name
            ?? throw new PrinterSetupException(FindingCode.QueueCreateFailed, $"No port sends to {address}.");
        // Not the printer's queue, yet it prints to the port (a pooled queue the setup does not configure for this printer, also one
        // a USB printer configures): a queue added there would print to the same printer. The worker plans from its own reads, so
        // this holds whatever the window planned.
        if (QueueLocator.OnPort(queues, port).FirstOrDefault() is { } other)
        {
            throw new PrinterSetupException(FindingCode.QueueLeftUnchanged,
                $"Queue '{other.Name}' (ports {other.PortName}), which the setup does not configure for this printer, already prints to port {port}, which sends to the printer at {address}, so no second queue is added. "
                + $"If '{other.Name}' is the ticket printer's queue, un-pool it or put it on a single Standard TCP/IP port, or delete it if it is not used, then run the setup again.");
        }
        return AddQueue(action, model, driverName, environment, port, usb: false);
    }

    /// <summary>
    /// Uses the printer's queue instead of adding a second one. Windows may give a Plug and Play queue its inbox class driver
    /// (it ranks that first since July 2026); such a queue gets the approved driver, verified by a re-read.
    /// </summary>
    private (ActionOutcome, string) Adopt(PlannedAction action, PrintQueueInfo queue, ModelDefinition model, string driverName, string environment, string origin, bool createdNow)
    {
        _queueName = queue.Name;
        if (DriverRules.IsAccepted(model, queue.DriverName))
            return createdNow ? Done($"{origin} with driver '{queue.DriverName}'.") : Already($"{origin} with driver '{queue.DriverName}'.");

        var driver = DriverRules.Check(model, driverName, _services.Spooler.GetDrivers(), environment);
        if (!driver.IsInstalled)
            throw new PrinterSetupException(FindingCode.DriverMissing, $"{origin} with driver '{queue.DriverName}', which cannot be replaced: {driver.Detail}");

        Report(StepId.InstallDriver, StepState.Running, action, ProgressPhase.Configuring, $"Changing the driver of '{queue.Name}'.");
        _services.Spooler.SetQueueDriver(queue.Name, driverName);
        var after = QueueLocator.ByName(_services.Spooler.GetQueues(), queue.Name);
        if (after is null || !Same(after.DriverName, driverName))
            throw new PrinterSetupException(FindingCode.QueueDriverMismatch, $"Queue '{queue.Name}' uses '{after?.DriverName}' after the change to '{driverName}'.");
        return Done($"{origin} with driver '{queue.DriverName}'; its driver was changed to '{driverName}' instead of adding a second queue.");
    }

    private (ActionOutcome, string) AddQueue(PlannedAction action, ModelDefinition model, string driverName, string environment, string port, bool usb)
    {
        var driver = DriverRules.Check(model, driverName, _services.Spooler.GetDrivers(), environment);
        if (!driver.IsInstalled)
            throw new PrinterSetupException(FindingCode.DriverMissing, $"The queue cannot be created: {driver.Detail}");

        var queues = _services.Spooler.GetQueues();
        string? planned = Param(action, QueueName);
        string name = planned is not null && !queues.Any(q => Same(q.Name, planned))
            ? planned
            : QueueLocator.NewQueueName(_options.QueueNameOverride ?? _branch?.QueueName ?? (usb ? driverName : _config.Connection.NewQueueName), model.Id, queues);

        Report(StepId.InstallDriver, StepState.Running, action, ProgressPhase.Configuring, $"Creating queue '{name}'.");
        _services.Spooler.AddQueue(new NewQueueRequest(name, driverName, port));

        var created = QueueLocator.ByName(_services.Spooler.GetQueues(), name);
        if (created is null || !Same(created.DriverName, driverName) || !Same(created.PortName, port))
            throw new PrinterSetupException(FindingCode.QueueCreateFailed, $"Queue '{name}' is not present with driver '{driverName}' on {port} after it was created.");
        _queueName = name;
        return Done($"Created queue '{name}' on {port} with driver '{driverName}'.");
    }

    private (PrintQueueInfo? Queue, string? Port) WaitForPlugAndPlayQueue(PlannedAction action, string? instanceId, ModelDefinition model, string? port)
    {
        var clock = _services.Clock;
        var total = TimeSpan.FromSeconds(_config.Connection.UsbQueueWaitSeconds);
        var start = clock.Now;
        while (clock.Now - start < total)
        {
            double fraction = Math.Clamp((clock.Now - start) / total, 0, 1);
            Report(StepId.InstallDriver, StepState.Running, action, ProgressPhase.Waiting, "Waiting for Windows to create the printer queue.", fraction);
            clock.Sleep(PollInterval, _cancellationToken);

            port = TryFindDevice(instanceId)?.UsbPortName ?? port;
            if (QueueOnPort(port, model) is { } queue)
                return (queue, port);
        }

        _services.Log.Info("apply.queue.pnpTimeout", $"Windows created no queue within {total.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s; creating one.", Data(action));
        return (null, port);
    }

    private PrintQueueInfo? QueueOnPort(string? port, ModelDefinition model) =>
        QueueLocator.Primary(UsbQueues(_services.Spooler.GetQueues(), port), model);

    /// <summary>
    /// The local queues on the target's USB port as detection gives them to it (<see cref="QueueLocator.ForUsbDevice"/>): while
    /// the target is not connected, a queue that another printer on its ports uses is that printer's, so an earlier unit never
    /// adopts it or changes its driver. When the devices cannot be read, which printer uses a queue is not known, so only the
    /// queues detection gave the target are its own (reported once).
    /// </summary>
    private IReadOnlyList<PrintQueueInfo> UsbQueues(IReadOnlyList<PrintQueueInfo> queues, string? port)
    {
        if (_plan.Target.Device is not { } known || port is null)
            return QueueLocator.OnPort(queues, port);
        if (TryReadDevices() is not { } devices)
        {
            if (!_devicesUnreadableReported)
            {
                _devicesUnreadableReported = true;
                _findings.Add(Findings.Warning(FindingCode.OperationFailed,
                    $"The printer devices could not be read ({_devicesError}); only the queues detection found for the printer are used, and no other queue is adopted or given another driver for it.",
                    ("candidate", _plan.Target.Key)));
            }
            return QueueLocator.OnPort(queues, port).Where(q => QueueLocator.IsDetectedQueue(_plan.Target, q)).ToList();
        }
        var device = devices.FirstOrDefault(d => Same(d.InstanceId, known.InstanceId)) ?? known with { IsPresent = false };
        return QueueLocator.ForUsbDevice(queues, device with { UsbPortName = port }, devices, _matcher);
    }

    private (ActionOutcome, string) ChangeQueueDriver(PlannedAction action)
    {
        var model = Model;
        string driverName = Param(action, DriverName) ?? model.PreferredDriverName;
        var queue = NamedQueue(action);
        if (Same(queue.DriverName, driverName))
            return Already($"Queue '{queue.Name}' already uses driver '{driverName}'.");

        var driver = DriverRules.Check(model, driverName, _services.Spooler.GetDrivers(), Package(action).Environment);
        if (!driver.IsInstalled)
            throw new PrinterSetupException(FindingCode.DriverMissing, $"The driver of '{queue.Name}' cannot be changed: {driver.Detail}");

        if (Param(action, RestoreDriverName) is not null)
            _restorePoint = (queue.Name, queue.DriverName, driverName, ReadGlobalDefaults(queue.Name));

        Report(StepId.InstallDriver, StepState.Running, action, ProgressPhase.Configuring, $"Changing the driver of '{queue.Name}'.");
        _services.Spooler.SetQueueDriver(queue.Name, driverName);
        var after = QueueLocator.ByName(_services.Spooler.GetQueues(), queue.Name);
        if (after is null || !Same(after.DriverName, driverName))
            throw new PrinterSetupException(FindingCode.QueueDriverMismatch, $"Queue '{queue.Name}' uses '{after?.DriverName}' after the change to '{driverName}'.");
        return Done($"Changed the driver of '{queue.Name}' from '{queue.DriverName}' to '{driverName}'.");
    }

    private DevMode? ReadGlobalDefaults(string queueName)
    {
        try
        {
            return _services.Spooler.GetDevMode(queueName, DevModeScope.Global);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _services.Log.Warn("apply.restore_point.devmode_unreadable", $"The defaults of '{queueName}' could not be saved before its driver is changed; only the driver can be restored.", null, ex);
            return null;
        }
    }

    /// <summary>
    /// After a queue was moved to another driver for the ticket (<see cref="RestoreDriverName"/>) and its ticket defaults then
    /// failed or were skipped because of a failure, the queue gets its previous driver and global defaults back: the new driver
    /// is no use without them. Not after a cancellation (completed changes are kept) or when the defaults succeeded.
    /// </summary>
    private void RestoreDriverIfTicketDefaultsFailed()
    {
        if (_restorePoint is not { } restore || !_failed.ContainsKey(ActionKind.ConfigureGlobalDefaults))
            return;

        string detail;
        try
        {
            var queue = QueueLocator.ByName(_services.Spooler.GetQueues(), restore.Queue)
                ?? throw new PrinterSetupException(FindingCode.QueueMissing, $"Queue '{restore.Queue}' no longer exists.");
            if (!Same(queue.DriverName, restore.Driver))
                _services.Spooler.SetQueueDriver(restore.Queue, restore.Driver);
            var after = QueueLocator.ByName(_services.Spooler.GetQueues(), restore.Queue);
            if (after is null || !Same(after.DriverName, restore.Driver))
                throw new PrinterSetupException(FindingCode.QueueDriverMismatch, $"Queue '{restore.Queue}' uses '{after?.DriverName}' after the change back to '{restore.Driver}'.");

            detail = $"The ticket defaults could not be set with driver '{restore.NewDriver}', so queue '{restore.Queue}' was given its driver '{restore.Driver}' back";
            if (restore.Global is { } global)
            {
                _services.Spooler.SetDevMode(restore.Queue, DevModeScope.Global, _services.Spooler.ValidateDevMode(restore.Queue, global));
                detail += " with its previous defaults.";
            }
            else
            {
                detail += "; its previous defaults were not known, so the driver's defaults apply.";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string failure = $"The ticket defaults could not be set with driver '{restore.NewDriver}', and queue '{restore.Queue}' could not be given its driver '{restore.Driver}' and defaults back: {ex.Message}";
            _findings.Add(Findings.Error(FindingCode.QueueDriverRestoreFailed, failure, ("queue", restore.Queue), ("driver", restore.Driver)));
            _services.Log.Error("apply.restore.failed", failure, ex, new Dictionary<string, object?> { ["queue"] = restore.Queue, ["driver"] = restore.Driver });
            return;
        }

        _findings.Add(Findings.Warning(FindingCode.QueueDriverRestored, detail, ("queue", restore.Queue), ("driver", restore.Driver), ("newDriver", restore.NewDriver)));
        _services.Log.Warn("apply.restore.done", detail, new Dictionary<string, object?> { ["queue"] = restore.Queue, ["driver"] = restore.Driver });
    }

    private (ActionOutcome, string) ChangeQueuePort(PlannedAction action)
    {
        var queue = NamedQueue(action);
        string? port = _plan.Target.Kind == TargetKind.UsbDevice
            ? TryFindDevice(_plan.Target.Device?.InstanceId)?.UsbPortName ?? Param(action, PortName)
            : _portName ?? Param(action, PortName);
        if (port is null && _plan.Target.NetworkAddress is not null)
            port = ReusableTcpPort(_services.Spooler.GetPorts(), _config.Connection.RawPortNumber)?.Name;
        if (port is null)
            throw new PrinterSetupException(FindingCode.QueuePortMismatch, $"The correct port for '{queue.Name}' is not known.");
        // Also as one of its pooled ports: replacing the queue's port list would undo the pooling.
        if (QueueLocator.UsesPort(queue, port))
            return Already($"Queue '{queue.Name}' already uses port {port}.");
        if (QueueLocator.FindPort(_services.Spooler.GetPorts(), port) is null)
            throw new PrinterSetupException(FindingCode.QueuePortMismatch, $"Port '{port}' does not exist.");

        Report(StepId.InstallDriver, StepState.Running, action, ProgressPhase.Configuring, $"Moving '{queue.Name}' to port {port}.");
        _services.Spooler.SetQueuePort(queue.Name, port);
        var after = QueueLocator.ByName(_services.Spooler.GetQueues(), queue.Name);
        if (after is null || !Same(after.PortName, port))
            throw new PrinterSetupException(FindingCode.QueuePortMismatch, $"Queue '{queue.Name}' uses port '{after?.PortName}' after the change to '{port}'.");
        return Done($"Moved queue '{queue.Name}' from port {queue.PortName} to {port}.");
    }

    /// <summary>The existing queue an action names (the queue found or created earlier in this run wins).</summary>
    private PrintQueueInfo NamedQueue(PlannedAction action)
    {
        string name = _queueName ?? Param(action, QueueName)
            ?? throw new PrinterSetupException(FindingCode.QueueMissing, "The action names no queue.");
        var queue = QueueLocator.ByName(_services.Spooler.GetQueues(), name)
            ?? throw new PrinterSetupException(FindingCode.QueueMissing, $"Queue '{name}' no longer exists.");
        _queueName = queue.Name;
        return queue;
    }

    /// <summary>
    /// The target's queue at execution time: found by the printer's port or address (the queue detection chose among them,
    /// else <see cref="QueueLocator.Primary"/>), else by the planned name.
    /// </summary>
    private PrintQueueInfo? ResolveQueue(PlannedAction action)
    {
        var queues = _services.Spooler.GetQueues();
        if (QueueLocator.ByName(queues, _queueName) is { } current)
            return current;

        var target = _plan.Target;
        IReadOnlyList<PrintQueueInfo> candidates = target.Kind switch
        {
            TargetKind.UsbDevice => UsbQueues(queues, TryFindDevice(target.Device?.InstanceId)?.UsbPortName ?? target.Device?.UsbPortName),
            TargetKind.NetworkAddress => QueueLocator.ForHosts(queues, _services.Spooler.GetPorts(), QueueLocator.NetworkHosts(target)),
            _ => [],
        };
        return candidates.Count > 0
            ? QueueLocator.ByName(candidates, target.Queue?.Name) ?? QueueLocator.Primary(candidates, Model)
            : QueueLocator.ByName(queues, Param(action, QueueName));
    }

    // ---- Form and defaults ---------------------------------------------------------------------------------------

    private (ActionOutcome, string) EnsureForm(PlannedAction action)
    {
        string name = _config.Ticket.FormName;
        var size = _config.Ticket.Dimensions;
        string expected = TicketDefaults.Micrometers(size.WidthMicrometers, size.HeightMicrometers);

        var form = FindForm(name);
        if (form is not null && IsExact(form, size))
            return Already($"The form '{form.Name}' already is {expected} with a full imageable area.");

        Report(StepId.ConfigureTicket, StepState.Running, action, ProgressPhase.Configuring, $"Writing the form '{name}'.");
        if (form is null)
        {
            try
            {
                _services.Spooler.AddForm(FormInfo.UserForm(name, size));
            }
            catch (SpoolerException ex) when (ex.Win32Error is ErrorFileExists or ErrorAlreadyExists)
            {
                // Created concurrently: use it if it is right, otherwise resize it below.
                form = FindForm(name);
                if (form is not null && IsExact(form, size))
                    return Already($"The form '{form.Name}' already is {expected} with a full imageable area.");
                if (form is null)
                    throw;
            }
        }

        if (form is not null)
        {
            if (form.Kind != FormKind.User)
            {
                throw new PrinterSetupException(FindingCode.FormSizeMismatch,
                    $"The {form.Kind.ToString().ToLowerInvariant()} form '{form.Name}' is {TicketDefaults.Micrometers(form.WidthMicrometers, form.HeightMicrometers)}; it cannot be changed.");
            }
            _services.Spooler.SetForm(form.Name, FormInfo.UserForm(form.Name, size));
        }

        var after = FindForm(name);
        if (after is null || !IsExact(after, size))
        {
            string actual = after is null ? "missing" : TicketDefaults.Micrometers(after.WidthMicrometers, after.HeightMicrometers);
            throw new PrinterSetupException(FindingCode.FormCreateFailed, $"The form '{name}' is {actual} after the change; it must be {expected} with a full imageable area.");
        }
        return Done(form is null ? $"Created the form '{name}' ({expected})." : $"Resized the form '{after.Name}' to {expected}.");
    }

    private (ActionOutcome, string) ConfigureDefaults(PlannedAction action, DevModeScope scope)
    {
        var model = Model;
        string which = scope == DevModeScope.Global ? "queue" : "current user's";
        var queue = ResolveQueue(action)
            ?? throw new PrinterSetupException(FindingCode.QueueMissing, "The printer's queue does not exist, so its defaults cannot be set.");

        var current = _services.Spooler.GetDevMode(queue.Name, scope);
        if (scope == DevModeScope.PerUser && current is null)
            return Already($"The current user has no own defaults for '{queue.Name}'; the queue defaults apply.");

        string formName = _config.Ticket.FormName;
        var caps = _services.Spooler.GetCapabilities(queue.Name);
        var paper = caps.FindTicketPaper(formName, _config.Ticket.Dimensions)
            ?? throw new PrinterSetupException(FindingCode.DriverDoesNotListForm,
                $"The driver of '{queue.Name}' lists neither the form '{formName}' nor a paper of the ticket size (custom size range {DriverSizeRange.Read(_services.Spooler, caps, _services.Log)}); no defaults were written.");

        var source = TicketDefaults.DecideSource(_config.PrintPreferences, model, caps.Bins);
        if (source.Status == SourceStatus.NotFound)
        {
            string detail = $"The driver of '{queue.Name}' exposes no multipurpose tray (it offers {string.Join(", ", caps.Bins.Select(b => $"'{b.Name}'"))}).";
            if (source.Required)
                throw new PrinterSetupException(FindingCode.PaperSourceNotFound, detail + " No defaults were written.");
            _findings.Add(Findings.Warning(FindingCode.PaperSourceNotFound, detail + " The driver's default source is kept.", ("queue", queue.Name)));
        }

        var desired = TicketDefaults.Desired(_config.PrintPreferences, paper, caps, source);
        if (current is not null && desired.Differences(current).Count == 0)
            return Already($"The {which} defaults of '{queue.Name}' are already correct.");

        var baseDevMode = current ?? _services.Spooler.GetDevMode(queue.Name, DevModeScope.Effective)
            ?? throw new PrinterSetupException(FindingCode.OperationFailed, $"The driver returned no DEVMODE for '{queue.Name}'.");

        Report(StepId.ConfigureTicket, StepState.Running, action, ProgressPhase.Configuring, $"Setting the {which} defaults of '{queue.Name}'.");
        var validated = _services.Spooler.ValidateDevMode(queue.Name, desired.ApplyTo(baseDevMode));
        if (!desired.PaperMatches(validated))
        {
            throw new PrinterSetupException(FindingCode.DriverRejectedSetting,
                $"The driver of '{queue.Name}' replaced the ticket paper with {TicketDefaults.DescribePaper(validated)}; no defaults were written.");
        }

        var rejected = new List<string>();
        if (validated.Orientation != desired.Orientation)
            rejected.Add("orientation");
        if (desired.Duplex is { } duplex && validated.Duplex != duplex)
            rejected.Add("duplex");
        if (validated.Scale != desired.Scale)
            rejected.Add("scale");
        if (rejected.Count > 0)
        {
            _findings.Add(Findings.Warning(FindingCode.DriverRejectedSetting,
                $"The driver of '{queue.Name}' did not accept the {string.Join(", ", rejected)} setting; its own value is kept.",
                ("queue", queue.Name), ("fields", string.Join(",", rejected))));
        }

        if (desired.SourceId is { } sourceId && validated.DefaultSource != sourceId)
        {
            string detail = Inv($"The driver of '{queue.Name}' changed the paper source from '{source.Bin!.Name}' ({sourceId}) to {validated.DefaultSource}");
            // Required by the policy or by the model (only the MP tray can feed the ticket), as in the planner and the verifier.
            if (source.Required)
            {
                string reason = _config.PrintPreferences.PaperSource == PaperSourcePolicy.RequireMultipurposeTray
                    ? ""
                    : $", but only the multipurpose tray of {model.Id} can feed the ticket (ticketPaperSource.requiredForTicket)";
                throw new PrinterSetupException(FindingCode.PaperSourceRejected, detail + reason + "; no defaults were written.");
            }
            _findings.Add(Findings.Warning(FindingCode.PaperSourceRejected, detail + ".", ("queue", queue.Name)));

            // Only preferred: the driver's source stands in for the tray, so defaults that already carry it are not written again.
            desired = desired with { SourceSubstitute = validated.DefaultSource };
            if (current is not null && desired.Differences(current).Count == 0)
                return Already(Inv($"The {which} defaults of '{queue.Name}' are already correct; the driver keeps source {validated.DefaultSource} instead of '{source.Bin!.Name}'."));
        }

        _services.Spooler.SetDevMode(queue.Name, scope, validated);

        var expected = desired.ValuesOf(validated);
        var readBack = _services.Spooler.GetDevMode(queue.Name, scope);
        var lost = readBack is null ? ["all settings"] : expected.Differences(readBack);
        if (lost.Count > 0)
        {
            throw new PrinterSetupException(FindingCode.DefaultsNotPersisted,
                $"The {which} defaults of '{queue.Name}' did not keep the {string.Join(", ", lost)} setting after they were written.");
        }
        return Done($"Set the {which} defaults of '{queue.Name}': {expected}.");
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------

    private DriverPackageDefinition Package(PlannedAction action)
    {
        string id = Param(action, PackageId) ?? Model.DriverPackageId;
        return _config.FindPackage(id)
            ?? throw new PrinterSetupException(FindingCode.ConfigurationInvalid, $"Driver package '{id}' is not in the configuration.");
    }

    /// <summary>A RAW port that already sends to the target under another of its spellings; the configured addresses are probed again by every detection.</summary>
    private PrinterPortInfo? ReusableTcpPort(IReadOnlyList<PrinterPortInfo> ports, int portNumber) =>
        QueueLocator.ReusableTcpPort(ports, _plan.Target, portNumber, [_options.PreferredAddress, _branch?.PrinterAddress]);

    private PnpPrinterDevice? FindDevice(string? instanceId, string hardwareId)
    {
        var devices = _services.Devices.GetPrinterDevices();
        return devices.FirstOrDefault(d => Same(d.InstanceId, instanceId))
            ?? devices.FirstOrDefault(d => d.IsPresent && d.HardwareIds.Contains(hardwareId, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The device by instance id; null when unknown or when the inventory cannot be read.</summary>
    private PnpPrinterDevice? TryFindDevice(string? instanceId) =>
        instanceId is null ? null : TryReadDevices()?.FirstOrDefault(d => Same(d.InstanceId, instanceId));

    /// <summary>The Plug and Play printer devices; null when the inventory cannot be read (the error is kept for the report).</summary>
    private IReadOnlyList<PnpPrinterDevice>? TryReadDevices()
    {
        try
        {
            return _services.Devices.GetPrinterDevices();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _services.Log.Warn("apply.devices.failed", "Could not read the Plug and Play devices.", null, ex);
            _devicesError = ex.Message;
            return null;
        }
    }

    private FormInfo? FindForm(string name) => _services.Spooler.GetForms().FirstOrDefault(f => Same(f.Name, name));

    private static bool IsExact(FormInfo form, PaperDimensions size) => form.HasSize(size) && form.HasFullImageableArea;

    private static FindingCode CodeFor(ActionKind kind, PrinterSetupException ex)
    {
        if (ex is SpoolerException { IsSpoolerUnavailable: true })
            return FindingCode.SpoolerUnavailable;
        if (ex.Code != FindingCode.OperationFailed)
            return ex.Code;
        return kind switch
        {
            ActionKind.StartSpooler => FindingCode.SpoolerNotRunning,
            ActionKind.PrepareDriverPackage => FindingCode.DriverPackageUnavailable,
            ActionKind.InstallDriver or ActionKind.BindUsbDevice => FindingCode.DriverInstallFailed,
            ActionKind.CreateTcpIpPort => FindingCode.PortCreateFailed,
            ActionKind.CreateQueue => FindingCode.QueueCreateFailed,
            ActionKind.ChangeQueueDriver => FindingCode.QueueDriverMismatch,
            ActionKind.ChangeQueuePort => FindingCode.QueuePortMismatch,
            ActionKind.CreateForm or ActionKind.UpdateForm => FindingCode.FormCreateFailed,
            _ => FindingCode.OperationFailed,
        };
    }

    private static StepId StepOf(ActionKind kind) => kind switch
    {
        ActionKind.StartSpooler => StepId.CheckDriver,
        ActionKind.CreateForm or ActionKind.UpdateForm or ActionKind.ConfigureGlobalDefaults or ActionKind.ConfigureUserDefaults => StepId.ConfigureTicket,
        _ => StepId.InstallDriver,
    };

    private void Skip(PlannedAction action, string reason)
    {
        _results.Add(new ActionResult(action, ActionOutcome.Skipped, reason));
        Report(StepOf(action.Kind), StepState.Skipped, action, ProgressPhase.Skipped, reason);
        _services.Log.Info("apply.action.skipped", reason, Data(action));
    }

    private void Fail(PlannedAction action, FindingCode code, string detail, int? win32Error, Exception exception)
    {
        _results.Add(new ActionResult(action, ActionOutcome.Failed, detail) { FailureCode = code, Win32Error = win32Error });
        _failed[action.Kind] = action.Kind;
        _findings.Add(Findings.Error(code, detail, ("action", action.Kind.ToString()), ("win32Error", win32Error?.ToString(CultureInfo.InvariantCulture))));
        _progress?.Report(new ProgressUpdate(StepOf(action.Kind), StepState.Failed, action.Kind, ProgressPhase.Failed, detail) { Code = code });
        _services.Log.Error("apply.action.failed", detail, exception, Data(action, ("code", code.ToString()), ("win32Error", win32Error)));
    }

    private void Report(StepId step, StepState state, PlannedAction action, ProgressPhase phase, string detail, double? fraction = null) =>
        _progress?.Report(new ProgressUpdate(step, state, action.Kind, phase, detail) { Fraction = fraction });

    private ApplyResult Result()
    {
        var result = new ApplyResult { Results = _results, Findings = Findings.Distinct(_findings), RebootRequired = _rebootRequired };
        string summary = result.Succeeded ? "no failures" : result.Refused ? "nothing applied" : "with failures";
        _services.Log.Info("apply.result", $"{_results.Count} action(s): {summary}.", new Dictionary<string, object?>
        {
            ["target"] = _plan.Target.Key,
            ["outcomes"] = _results.Select(r => $"{r.Action.Kind}:{r.Outcome}").ToArray(),
            ["rebootRequired"] = _rebootRequired,
        });
        return result;
    }

    private static Dictionary<string, object?> Data(PlannedAction action, params (string Key, object? Value)[] extra)
    {
        var data = new Dictionary<string, object?> { ["action"] = action.Kind.ToString() };
        foreach (var (key, value) in action.Parameters)
            data[key] = value;
        foreach (var (key, value) in extra)
            data[key] = value;
        return data;
    }

    private static string? Param(PlannedAction action, string key) =>
        action.Parameters.TryGetValue(key, out string? value) && !string.IsNullOrEmpty(value) ? value : null;

    private static (ActionOutcome, string) Done(string detail) => (ActionOutcome.Succeeded, detail);

    private static (ActionOutcome, string) Already(string detail) => (ActionOutcome.AlreadySatisfied, detail);

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Forwards the package provider's progress as part of the install step.</summary>
    private sealed class StepProgress : IProgress<ProgressUpdate>
    {
        private readonly IProgress<ProgressUpdate>? _inner;

        public StepProgress(IProgress<ProgressUpdate>? inner) => _inner = inner;

        public void Report(ProgressUpdate value) =>
            _inner?.Report(value with { Step = StepId.InstallDriver, Action = ActionKind.PrepareDriverPackage });
    }
}
