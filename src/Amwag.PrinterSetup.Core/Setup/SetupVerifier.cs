using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Printing;

namespace Amwag.PrinterSetup.Core.Setup;

/// <summary>Re-detects a target and checks its final state from fresh reads. Never modifies the system.</summary>
internal sealed class SetupVerifier
{
    /// <summary>A PrintTicket media size within 1 mm of the form counts as the form.</summary>
    private const int PrintTicketToleranceMicrometers = 1000;

    private const PrinterStatusFlags OfflineFlags = PrinterStatusFlags.Offline | PrinterStatusFlags.NotAvailable;
    private const PrinterStatusFlags ErrorFlags = PrinterStatusFlags.Error | PrinterStatusFlags.PaperOut | PrinterStatusFlags.DoorOpen |
        PrinterStatusFlags.PaperJam | PrinterStatusFlags.PaperProblem | PrinterStatusFlags.NoToner | PrinterStatusFlags.UserIntervention;

    private static readonly FindingCode[] EnvironmentCodes =
    [
        FindingCode.SpoolerNotRunning, FindingCode.SpoolerDisabled, FindingCode.SpoolerUnavailable, FindingCode.ProtectedPrintModeEnabled,
        FindingCode.UnsupportedOperatingSystem, FindingCode.UnsupportedArchitecture, FindingCode.DimensionsUnconfirmed,
    ];

    private static readonly VerificationItem[] QueueDependentItems =
    [
        VerificationItem.DriverListsForm, VerificationItem.DefaultPaper, VerificationItem.DefaultOrientation, VerificationItem.DefaultDuplex,
        VerificationItem.DefaultScale, VerificationItem.DefaultPaperSource, VerificationItem.UserDefaultsConsistent, VerificationItem.EffectiveDefaults,
        VerificationItem.PrintTicketMediaSize, VerificationItem.PrinterOnline,
    ];

    private readonly SetupSession _session;
    private readonly List<VerificationCheck> _checks = [];
    private readonly List<Finding> _findings = [];

    public SetupVerifier(SetupSession session) => _session = session;

    private DeploymentConfig Config => _session.Config;
    private IPrintSpooler Spooler => _session.Services.Spooler;

    /// <summary>
    /// A network selector's detection probes the existing TCP/IP queues' hosts as well, as the window's and the elevated
    /// worker's do: the same spellings then merge, so the queue the worker found under another spelling (resolved by alias)
    /// is the queue checked here.
    /// </summary>
    public VerificationReport Verify(TargetSelector selector, CancellationToken cancellationToken)
    {
        var request = new DetectRequest
        {
            NetworkAddresses = selector.NetworkAddress is { } address ? [address] : [],
            ProbeExistingNetworkQueues = selector.NetworkAddress is not null,
        };
        var detection = _session.Detect(request, cancellationToken);
        _findings.AddRange(detection.Findings.Where(f => EnvironmentCodes.Contains(f.Code)));
        var snapshot = detection.Snapshot;
        Add(VerificationItem.SpoolerRunning, snapshot.Spooler.IsRunning ? CheckStatus.Pass : CheckStatus.Fail, "Running", snapshot.Spooler.State.ToString(),
            $"The Print Spooler service is {snapshot.Spooler.State}.", FindingCode.SpoolerNotRunning);

        var target = _session.Resolve(detection, selector);
        if (target?.Model is not { } model)
            return Finish(selector, null, null, NotFound(selector, detection));

        var queue = target.Queue;
        string environment = Config.FindPackage(model.DriverPackageId)?.Environment ?? "Windows x64";
        var driver = CheckDriver(model, queue, snapshot, environment);
        CheckQueue(target, model, queue, snapshot);
        CheckForm(snapshot);

        SourceDecision? source = null;
        DevMode? global = null;
        if (queue is null)
        {
            foreach (var item in QueueDependentItems)
                Add(item, CheckStatus.Unknown, "", "not checked", "There is no queue to check.");
        }
        else
        {
            (source, global) = CheckDefaults(model, queue, cancellationToken);
            CheckOnline(queue);
        }
        CheckDeviceConnected(target, model);

        var summary = new ConfiguredSummary
        {
            ModelName = model.DisplayName,
            QueueName = queue?.Name ?? "",
            DriverName = queue?.DriverName ?? "",
            DriverVersion = driver?.Version?.ToString(),
            Connection = DescribeConnection(target, queue, snapshot.Ports),
            PaperSize = TicketDefaults.Describe(Config.Ticket),
            PaperSource = source is { Status: SourceStatus.Found, Bin: { } bin } && global?.DefaultSource == bin.Id ? bin.Name : null,
        };
        return Finish(selector, target, summary, null);
    }

    private VerificationReport Finish(TargetSelector selector, PrinterCandidate? target, ConfiguredSummary? summary, string? notFound)
    {
        var readiness = _checks.Any(c => c.Status == CheckStatus.Fail) ? ReadinessLevel.NotReady
            : _checks.Any(c => c.Status is CheckStatus.Warning or CheckStatus.Unknown) ? ReadinessLevel.ReadyWithWarnings
            : ReadinessLevel.Ready;
        var report = new VerificationReport
        {
            TargetKey = selector.Key,
            Target = target,
            Checks = _checks,
            Readiness = readiness,
            Summary = summary ?? new ConfiguredSummary { PaperSize = TicketDefaults.Describe(Config.Ticket) },
            Findings = Findings.Distinct(_findings),
        };

        _session.Services.Log.Info("verify.result", notFound ?? $"Verified {selector.Key}: {readiness}.", new Dictionary<string, object?>
        {
            ["target"] = selector.Key,
            ["readiness"] = readiness.ToString(),
            ["checks"] = _checks.Select(c => $"{c.Item}:{c.Status}").ToArray(),
        });
        return report;
    }

    private string NotFound(TargetSelector selector, DetectionResult detection)
    {
        var candidate = detection.Candidates.FirstOrDefault(c => string.Equals(c.Key, selector.Key, StringComparison.OrdinalIgnoreCase));
        (FindingCode code, string detail) = candidate switch
        {
            null => (FindingCode.NoSupportedPrinterFound, $"The printer {selector.Key} was not found again."),
            { NeedsConfirmation: true } or { Model: null } => (FindingCode.ProbableMatchRequiresConfirmation,
                $"The printer {selector.Key} is found, but its model is not confirmed."),
            _ => (candidate.Connection == ConnectionType.TcpIp ? FindingCode.NetworkIdentityMismatch : FindingCode.IdentityConflict,
                $"The printer {selector.Key} now reports model {candidate.Model?.Id}, not {selector.ModelId}."),
        };
        Add(VerificationItem.QueueExists, CheckStatus.Fail, $"Printer {selector.Key}" + (selector.ModelId is { } id ? $" ({id})" : ""), "not found", detail, code);
        return detail;
    }

    private PrinterDriverInfo? CheckDriver(ModelDefinition model, PrintQueueInfo? queue, SystemSnapshot snapshot, string environment)
    {
        string name = queue is not null && DriverRules.IsAccepted(model, queue.DriverName) ? queue.DriverName : model.PreferredDriverName;
        var check = DriverRules.Check(model, name, snapshot.Drivers, environment);
        Add(VerificationItem.DriverInstalled, check.IsInstalled ? CheckStatus.Pass : CheckStatus.Fail, $"'{name}' installed for {environment}",
            check.Driver is { } driver ? $"{driver.Name} {driver.Version?.ToString() ?? "(version unknown)"}" : "not installed", check.Detail,
            check.State == DriverState.Outdated ? FindingCode.DriverOutdated : FindingCode.DriverMissing);
        return check.Driver;
    }

    private void CheckQueue(PrinterCandidate target, ModelDefinition model, PrintQueueInfo? queue, SystemSnapshot snapshot)
    {
        if (queue is null)
        {
            Add(VerificationItem.QueueExists, CheckStatus.Fail, "A queue for the printer", "none", $"No queue prints to {target.Key}.", FindingCode.QueueMissing);
            Add(VerificationItem.QueueUsesAcceptedDriver, CheckStatus.Unknown, "", "not checked", "There is no queue to check.");
            Add(VerificationItem.QueuePortCorrect, CheckStatus.Unknown, "", "not checked", "There is no queue to check.");
            return;
        }

        Add(VerificationItem.QueueExists, CheckStatus.Pass, "A queue for the printer", queue.Name, $"Queue '{queue.Name}' prints to {queue.PortName}.");

        bool accepted = DriverRules.IsAccepted(model, queue.DriverName) && !target.IdentityConflict;
        Add(VerificationItem.QueueUsesAcceptedDriver, accepted ? CheckStatus.Pass : CheckStatus.Fail,
            string.Join(" or ", model.AcceptedDrivers.Select(d => $"'{d.Name}'")), queue.DriverName,
            accepted ? $"'{queue.DriverName}' is an accepted driver of {model.Id}." : $"'{queue.DriverName}' is not an accepted driver of {model.Id}.",
            FindingCode.QueueDriverMismatch);

        var (status, expected, detail) = CheckPort(target, queue, snapshot.Ports);
        Add(VerificationItem.QueuePortCorrect, status, expected, queue.PortName, detail, FindingCode.QueuePortMismatch);
    }

    private (CheckStatus Status, string Expected, string Detail) CheckPort(PrinterCandidate target, PrintQueueInfo queue, IReadOnlyList<PrinterPortInfo> ports)
    {
        switch (target.Kind)
        {
            case TargetKind.UsbDevice when target.Device is { IsPresent: true, UsbPortName: { } port }:
                // A pooled queue prints to any of its ports.
                return QueueLocator.UsesPort(queue, port)
                    ? (CheckStatus.Pass, port, string.Equals(queue.PortName, port, StringComparison.OrdinalIgnoreCase)
                        ? $"The queue uses the printer's USB port {port}."
                        : $"The queue uses the printer's USB port {port} among its pooled ports {queue.PortName}.")
                    : (CheckStatus.Fail, port, $"The printer is on {port}, but the queue uses {queue.PortName}.");
            case TargetKind.UsbDevice:
                return (CheckStatus.Unknown, "The printer's USB port", "The printer is not connected, so its USB port cannot be confirmed.");
            case TargetKind.NetworkAddress:
                // Another spelling of the printer (its host name or IP address) reaches the same device.
                string? host = QueueLocator.HostOf(queue, ports);
                return host is not null && QueueLocator.NetworkHosts(target).Contains(host, StringComparer.OrdinalIgnoreCase)
                    ? (CheckStatus.Pass, $"A port to {target.NetworkAddress}", $"Port {queue.PortName} sends to {host}.")
                    : (CheckStatus.Fail, $"A port to {target.NetworkAddress}", $"Port {queue.PortName} sends to {host ?? "an unknown host"}.");
            default:
                return QueueLocator.KindOf(queue.PortName, ports) == PortKind.Wsd
                    ? (CheckStatus.Pass, "A WSD port", $"The queue uses WSD port {queue.PortName}.")
                    : (CheckStatus.Unknown, "The printer's port", "Only the queue is known, so its port cannot be confirmed.");
        }
    }

    private void CheckForm(SystemSnapshot snapshot)
    {
        string name = Config.Ticket.FormName;
        var size = Config.Ticket.Dimensions;
        string expected = TicketDefaults.Micrometers(size.WidthMicrometers, size.HeightMicrometers);
        var form = snapshot.Forms.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        if (form is null)
        {
            Add(VerificationItem.FormExists, CheckStatus.Fail, $"Form '{name}'", "missing", $"The form '{name}' does not exist.", FindingCode.FormMissing);
            Add(VerificationItem.FormSizeExact, CheckStatus.Unknown, $"{expected}, full imageable area", "not checked", "The form does not exist.");
            return;
        }

        Add(VerificationItem.FormExists, CheckStatus.Pass, $"Form '{name}'", form.Name, $"The {form.Kind.ToString().ToLowerInvariant()} form '{form.Name}' exists.");
        bool exact = form.HasSize(size) && form.HasFullImageableArea;
        string actual = TicketDefaults.Micrometers(form.WidthMicrometers, form.HeightMicrometers)
            + (form.HasFullImageableArea ? ", full imageable area" : ", imageable area reduced");
        Add(VerificationItem.FormSizeExact, exact ? CheckStatus.Pass : CheckStatus.Fail, $"{expected}, full imageable area", actual,
            exact ? "The form has the exact ticket size." : "The form does not have the exact ticket size and a full imageable area.", FindingCode.FormSizeMismatch);
    }

    private (SourceDecision? Source, DevMode? Global) CheckDefaults(ModelDefinition model, PrintQueueInfo queue, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string formName = Config.Ticket.FormName;
        var preferences = Config.PrintPreferences;

        if (!TryRead(() => Spooler.GetCapabilities(queue.Name), out var caps, out string? error) || caps is null)
        {
            _findings.Add(Findings.Warning(FindingCode.PrintCapabilitiesUnavailable, $"The driver capabilities of '{queue.Name}' could not be read: {error}", ("queue", queue.Name)));
            foreach (var item in QueueDependentItems.Where(i => i != VerificationItem.PrinterOnline))
                Add(item, CheckStatus.Unknown, "", "not checked", $"The driver capabilities could not be read: {error}");
            return (null, null);
        }

        var paper = caps.FindTicketPaper(formName, Config.Ticket.Dimensions);
        Add(VerificationItem.DriverListsForm, paper is null ? CheckStatus.Fail : CheckStatus.Pass, $"'{formName}' or a paper of its size, listed by the driver",
            paper is null ? "not listed" : Inv($"'{paper.Name}' (paper {paper.Id})"),
            paper is null ? $"The driver of '{queue.Name}' lists neither the ticket form nor a paper of the ticket size."
                : string.Equals(paper.Name, formName, StringComparison.OrdinalIgnoreCase) ? $"The driver lists '{paper.Name}'."
                : $"The driver lists '{paper.Name}' with the ticket size.", FindingCode.DriverDoesNotListForm);

        var source = TicketDefaults.DecideSource(preferences, model, caps.Bins);
        var desired = paper is null ? null : TicketDefaults.Desired(preferences, paper, caps, source);
        short orientation = TicketDefaults.Orientation(preferences.Orientation);
        short? duplex = caps.SupportsDuplex ? TicketDefaults.Duplex(preferences.Duplex) : null;
        short scale = (short)preferences.ScalePercent;

        TryRead(() => Spooler.GetDevMode(queue.Name, DevModeScope.Global), out var global, out error);
        if (global is null)
        {
            string actual = error is null ? "no default DEVMODE" : "unreadable";
            string detail = error is null ? $"Queue '{queue.Name}' has no global default DEVMODE." : $"The defaults of '{queue.Name}' could not be read: {error}";
            foreach (var item in new[] { VerificationItem.DefaultPaper, VerificationItem.DefaultOrientation, VerificationItem.DefaultDuplex, VerificationItem.DefaultScale, VerificationItem.DefaultPaperSource })
                Add(item, CheckStatus.Fail, "Configured default", actual, detail, FindingCode.GlobalDefaultsIncorrect);
        }
        else
        {
            CheckGlobalDefaults(global, paper, desired, formName, orientation, duplex, scale, source);
        }

        CheckUserDefaults(queue, desired);
        CheckEffective(queue, paper, orientation);
        return (source, global);
    }

    private void CheckGlobalDefaults(DevMode global, PaperCapability? paper, DesiredDefaults? desired, string formName, short orientation, short? duplex, short scale, SourceDecision source)
    {
        // Name and id are not enough: a set dmPaperWidth/dmPaperLength of another size overrides them.
        bool paperOk = desired is not null && desired.PaperMatches(global);
        Add(VerificationItem.DefaultPaper, paperOk ? CheckStatus.Pass : CheckStatus.Fail,
            paper is null ? $"'{formName}'" : Inv($"'{paper.Name}' (paper {paper.Id})"), TicketDefaults.DescribePaper(global),
            desired is not null && desired.OverridesSize(global)
                ? "The queue's default paper: its dmPaperWidth/dmPaperLength select another size than the ticket paper."
                : "The queue's default paper.",
            FindingCode.GlobalDefaultsIncorrect);
        Add(VerificationItem.DefaultOrientation, global.Orientation == orientation ? CheckStatus.Pass : CheckStatus.Fail,
            OrientationText(orientation), OrientationText(global.Orientation), "The queue's default orientation.", FindingCode.GlobalDefaultsIncorrect);

        if (duplex is { } expectedDuplex)
        {
            Add(VerificationItem.DefaultDuplex, global.Duplex == expectedDuplex ? CheckStatus.Pass : CheckStatus.Fail,
                DuplexText(expectedDuplex), DuplexText(global.Duplex), "The queue's default duplex setting.", FindingCode.GlobalDefaultsIncorrect);
        }
        else
        {
            Add(VerificationItem.DefaultDuplex, CheckStatus.NotApplicable, "Single-sided", "no duplex unit", "The driver does not support duplex printing.");
        }

        Add(VerificationItem.DefaultScale, global.Scale == scale ? CheckStatus.Pass : CheckStatus.Fail,
            Inv($"{scale} %"), Inv($"{global.Scale} %"), "The queue's default scale.", FindingCode.GlobalDefaultsIncorrect);

        switch (source)
        {
            case { Status: SourceStatus.NotManaged }:
                Add(VerificationItem.DefaultPaperSource, CheckStatus.NotApplicable, "Driver default", Inv($"{global.DefaultSource}"), "The configuration leaves the paper source to the driver.");
                break;
            case { Status: SourceStatus.NotFound }:
                Add(VerificationItem.DefaultPaperSource, source.Required ? CheckStatus.Fail : CheckStatus.NotApplicable, "Multipurpose tray", "not offered by the driver",
                    "The driver exposes no multipurpose tray.", FindingCode.PaperSourceNotFound);
                break;
            case { Bin: { } bin }:
                bool sourceOk = global.DefaultSource == bin.Id;
                Add(VerificationItem.DefaultPaperSource, sourceOk ? CheckStatus.Pass : source.Required ? CheckStatus.Fail : CheckStatus.Warning,
                    Inv($"'{bin.Name}' ({bin.Id})"), Inv($"{global.DefaultSource}"), "The queue's default paper source.", FindingCode.PaperSourceRejected);
                break;
        }
    }

    private void CheckUserDefaults(PrintQueueInfo queue, DesiredDefaults? desired)
    {
        if (!TryRead(() => Spooler.GetDevMode(queue.Name, DevModeScope.PerUser), out var perUser, out string? error))
        {
            Add(VerificationItem.UserDefaultsConsistent, CheckStatus.Unknown, "No conflicting per-user defaults", "unreadable", $"The current user's defaults could not be read: {error}");
            return;
        }
        if (perUser is null)
        {
            Add(VerificationItem.UserDefaultsConsistent, CheckStatus.Pass, "No conflicting per-user defaults", "none", "The current user has no own defaults; the queue defaults apply.");
            return;
        }

        var differences = desired is null ? ["paper"] : AcceptDriverSource(queue.Name, desired, perUser).Differences(perUser);
        // A multipurpose tray that is only preferred is a warning, as in the queue's defaults.
        var status = differences.Count == 0 ? CheckStatus.Pass
            : differences is ["paperSource"] && desired is { SourceRequired: false } ? CheckStatus.Warning
            : CheckStatus.Fail;
        Add(VerificationItem.UserDefaultsConsistent, status, "No conflicting per-user defaults",
            differences.Count == 0 ? "per-user defaults match" : "differs: " + string.Join(", ", differences),
            "The current user's own defaults override the queue defaults.", FindingCode.UserDefaultsIncorrect);
    }

    /// <summary>
    /// The desired defaults accepting what the driver keeps instead of a multipurpose tray that is only preferred
    /// (<see cref="TicketDefaults.AcceptDriverSource"/>); unchanged when the driver cannot validate them.
    /// </summary>
    private DesiredDefaults AcceptDriverSource(string queueName, DesiredDefaults desired, DevMode current)
    {
        try
        {
            return TicketDefaults.AcceptDriverSource(desired, Spooler, queueName, current);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _session.Services.Log.Warn("verify.devmode.validate.failed", $"ValidateDevMode failed for '{queueName}'.", null, ex);
            return desired;
        }
    }

    private void CheckEffective(PrintQueueInfo queue, PaperCapability? paper, short orientation)
    {
        if (!TryRead(() => Spooler.GetDevMode(queue.Name, DevModeScope.Effective), out var effective, out string? error) || effective is null)
        {
            Add(VerificationItem.EffectiveDefaults, CheckStatus.Unknown, "Ticket paper, configured orientation", "unreadable", $"What applications get could not be read: {error ?? "no DEVMODE"}");
            Add(VerificationItem.PrintTicketMediaSize, CheckStatus.Unknown, "Ticket size", "not checked", "The effective DEVMODE could not be read.");
            return;
        }

        bool ok = paper is not null && TicketDefaults.SelectsPaper(effective, paper.Name, paper.Id) && effective.Orientation == orientation;
        Add(VerificationItem.EffectiveDefaults, ok ? CheckStatus.Pass : CheckStatus.Fail,
            Inv($"'{paper?.Name ?? Config.Ticket.FormName}', {OrientationText(orientation)}"),
            Inv($"'{effective.FormName}' (paper {effective.PaperSize}), {OrientationText(effective.Orientation)}"),
            "What applications get when they print with the defaults.", FindingCode.GlobalDefaultsIncorrect);

        CheckPrintTicket(queue, effective);
    }

    private void CheckPrintTicket(PrintQueueInfo queue, DevMode effective)
    {
        var size = Config.Ticket.Dimensions;
        string expected = TicketDefaults.Micrometers(size.WidthMicrometers, size.HeightMicrometers) + " (within 1 mm)";
        if (!TryRead(() => Spooler.DescribePrintTicket(queue.Name, effective), out var ticket, out string? error) || ticket is null)
        {
            Add(VerificationItem.PrintTicketMediaSize, CheckStatus.Unknown, expected, "unreadable", $"The PrintTicket could not be read: {error}");
            return;
        }
        if (!ticket.Available || ticket.WidthMicrometers is not { } width || ticket.HeightMicrometers is not { } height)
        {
            Add(VerificationItem.PrintTicketMediaSize, CheckStatus.Unknown, expected, "not available",
                $"The driver did not describe the PrintTicket media size{(ticket.Error is { Length: > 0 } reason ? ": " + reason : ".")}");
            return;
        }

        bool ok = Math.Abs(width - size.WidthMicrometers) <= PrintTicketToleranceMicrometers && Math.Abs(height - size.HeightMicrometers) <= PrintTicketToleranceMicrometers;
        Add(VerificationItem.PrintTicketMediaSize, ok ? CheckStatus.Pass : CheckStatus.Warning, expected,
            TicketDefaults.Micrometers(width, height) + (ticket.MediaSizeName is { } name ? $" ({name})" : ""),
            ok ? "The driver's PrintTicket uses the ticket size." : "The driver's PrintTicket media size differs from the ticket; XPS-based applications may print on another size.",
            FindingCode.PrintTicketMismatch);
    }

    private void CheckOnline(PrintQueueInfo queue)
    {
        var status = queue.Status;
        bool offline = queue.IsWorkOffline || (status & OfflineFlags) != 0;
        bool error = (status & ErrorFlags) != 0;
        bool paused = (status & PrinterStatusFlags.Paused) != 0;
        if (!offline && !error && !paused)
        {
            Add(VerificationItem.PrinterOnline, CheckStatus.Pass, "Ready", "ready", $"Queue '{queue.Name}' reports no problem.");
            return;
        }

        string actual = string.Join(", ", new[] { queue.IsWorkOffline ? "Use Printer Offline" : null, status == PrinterStatusFlags.None ? null : status.ToString() }.OfType<string>());
        Add(VerificationItem.PrinterOnline, CheckStatus.Warning, "Ready", actual, $"Queue '{queue.Name}' is not ready ({actual}); it is configured but cannot print until this is fixed.");
        if (offline)
            _findings.Add(Findings.Warning(FindingCode.PrinterOffline, $"Queue '{queue.Name}' is offline.", ("queue", queue.Name)));
        if (error)
            _findings.Add(Findings.Warning(FindingCode.PrinterError, $"Queue '{queue.Name}' reports a printer problem ({status & ErrorFlags}).", ("queue", queue.Name)));
        if (paused)
            _findings.Add(Findings.Warning(FindingCode.PrinterPaused, $"Queue '{queue.Name}' is paused.", ("queue", queue.Name)));
    }

    private void CheckDeviceConnected(PrinterCandidate target, ModelDefinition model)
    {
        switch (target.Kind)
        {
            case TargetKind.UsbDevice when target.Device is { IsPresent: true } device && DriverRules.IsRestartPending(device, model):
                // Linked to an accepted driver that takes effect once Windows restarts (after a binding that asked for it).
                Add(VerificationItem.DeviceConnected, CheckStatus.Warning, $"Connected, with an accepted driver of {model.Id}",
                    Inv($"connected, driver '{device.DriverDescription}', problem code {device.ProblemCode} (restart pending)"),
                    $"The USB printer is linked to an accepted driver of {model.Id}, but Windows must be restarted to finish installing it.", FindingCode.RebootRequired);
                break;
            case TargetKind.UsbDevice when target.Device is { IsPresent: true } device && !DriverRules.IsBound(device, model):
                // The queue prints with its approved driver, but Windows did not link the device itself to it (the planner binds it).
                string actual = device.ProblemCode is { } problem ? Inv($"connected, problem code {problem}") : $"connected, driver '{device.DriverDescription ?? "none"}'";
                Add(VerificationItem.DeviceConnected, CheckStatus.Warning, $"Connected, with an accepted driver of {model.Id}", actual,
                    $"The USB printer is connected, but Windows has not linked it to an accepted driver of {model.Id} ({actual}).", FindingCode.DeviceDriverNotBound);
                break;
            case TargetKind.UsbDevice:
                bool present = target.Device?.IsPresent == true;
                Add(VerificationItem.DeviceConnected, present ? CheckStatus.Pass : CheckStatus.Warning, "Connected", present ? "connected" : "not connected",
                    present ? "The USB printer is connected." : "The USB printer is not connected.", FindingCode.DeviceNotConnected);
                break;
            case TargetKind.NetworkAddress:
                bool reachable = target.NetworkIdentity?.RawPortReachable == true;
                Add(VerificationItem.DeviceConnected, reachable ? CheckStatus.Pass : CheckStatus.Warning, "Reachable", reachable ? "reachable" : "not reachable",
                    Inv($"Port {Config.Connection.RawPortNumber} of {target.NetworkAddress} is {(reachable ? "reachable" : "not reachable")}."), FindingCode.NetworkPrinterUnreachable);
                break;
            default:
                if (target.Device is { IsPresent: true })
                    Add(VerificationItem.DeviceConnected, CheckStatus.Pass, "Connected", "connected", "The printer's device is present.");
                else
                    Add(VerificationItem.DeviceConnected, CheckStatus.Unknown, "Connected", "unknown", "Only the queue is known, so the device cannot be checked.");
                break;
        }
    }

    private string DescribeConnection(PrinterCandidate target, PrintQueueInfo? queue, IReadOnlyList<PrinterPortInfo> ports)
    {
        switch (target.Connection)
        {
            case ConnectionType.Usb:
                return $"USB ({queue?.PortName ?? target.Device?.UsbPortName ?? "port unknown"})";
            case ConnectionType.TcpIp:
                var port = queue is null ? null : QueueLocator.FindPort(ports, queue.PortName);
                return Inv($"Network {target.NetworkAddress ?? port?.HostAddress}, port {port?.PortNumber ?? Config.Connection.RawPortNumber}");
            case ConnectionType.Wsd:
                return "WSD";
            default:
                return queue?.PortName ?? "";
        }
    }

    private void Add(VerificationItem item, CheckStatus status, string expected, string actual, string detail, FindingCode? code = null)
    {
        _checks.Add(new VerificationCheck(item, status, expected, actual, detail));
        if (code is { } finding && status is CheckStatus.Fail or CheckStatus.Warning)
        {
            _findings.Add(Findings.Create(finding, status == CheckStatus.Fail ? Severity.Error : Severity.Warning, detail,
                ("check", item.ToString()), ("expected", expected), ("actual", actual)));
        }
    }

    private bool TryRead<T>(Func<T?> read, out T? value, out string? error) where T : class
    {
        try
        {
            value = read();
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _session.Services.Log.Warn("verify.read.failed", ex.Message, null, ex);
            value = null;
            error = ex.Message;
            return false;
        }
    }

    private static string OrientationText(short orientation) => orientation switch
    {
        DmOrientation.Portrait => "portrait",
        DmOrientation.Landscape => "landscape",
        _ => Inv($"orientation {orientation}"),
    };

    private static string DuplexText(short duplex) => duplex switch
    {
        DmDuplex.Simplex => "single-sided",
        DmDuplex.Vertical => "two-sided (long edge)",
        DmDuplex.Horizontal => "two-sided (short edge)",
        _ => Inv($"duplex {duplex}"),
    };

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);
}
