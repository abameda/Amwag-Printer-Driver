using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;

namespace Amwag.PrinterSetup.Core.Setup;

public enum SetupMode
{
    /// <summary>Detect, install and configure (creates a queue when none exists).</summary>
    Setup,
    /// <summary>Check and repair existing supported queues; never creates a queue for an unconfigured printer.</summary>
    Repair,
    /// <summary>Inspect and report only; never modifies the system.</summary>
    Diagnostics,
}

public enum TargetKind { UsbDevice, NetworkAddress, ExistingQueue }

public enum ConnectionType { Usb, TcpIp, Wsd, Other }

/// <summary>How a printer's model was established, weakest first.</summary>
public enum MatchConfidence
{
    /// <summary>Not identified as a supported model.</summary>
    None,
    /// <summary>The device could not report its identity; an operator explicitly chose the model.</summary>
    UserAsserted,
    /// <summary>Descriptive network strings name a supported model; needs operator confirmation before any change.</summary>
    Probable,
    /// <summary>
    /// Only the queue's (accepted, model-specific) driver says which model it is: the device's identity could not be read, is a
    /// brand string only, or names the driver's model.
    /// </summary>
    DriverOnly,
    /// <summary>The device's own Plug and Play / IEEE 1284 identity exactly matches the approved INF.</summary>
    Exact,
}

/// <summary>One physical printer (or queue without a readable device) that detection found.</summary>
public sealed record PrinterCandidate
{
    /// <summary>Stable key: "usb:&lt;device instance id&gt;", "net:&lt;address&gt;" or "queue:&lt;queue name&gt;".</summary>
    public required string Key { get; init; }
    public required TargetKind Kind { get; init; }
    public ModelDefinition? Model { get; init; }
    public MatchConfidence Confidence { get; init; }
    /// <summary>Human-readable technical evidence for the identification (English).</summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public ConnectionType Connection { get; init; }
    public string? NetworkAddress { get; init; }
    /// <summary>Other spellings that reach the same network printer as <see cref="NetworkAddress"/> (its host name or IP address).</summary>
    public IReadOnlyList<string> NetworkAliases { get; init; } = [];
    public PnpPrinterDevice? Device { get; init; }
    public NetworkIdentity? NetworkIdentity { get; init; }
    /// <summary>The queue this tool configures for the printer, when one exists.</summary>
    public PrintQueueInfo? Queue { get; init; }
    /// <summary>Other queues bound to the same printer (left untouched; reported as duplicates).</summary>
    public IReadOnlyList<PrintQueueInfo> AdditionalQueues { get; init; } = [];
    /// <summary>The device identity and the queue's driver disagree; the device identity wins.</summary>
    public bool IdentityConflict { get; init; }
    /// <summary>USB: device present. Network: raw print port reachable. Queue-only: false (unknown).</summary>
    public bool PhysicallyAvailable { get; init; }

    /// <summary>May be configured without further questions.</summary>
    public bool IsEligible => Model is not null && Confidence is MatchConfidence.Exact or MatchConfidence.DriverOnly or MatchConfidence.UserAsserted;

    /// <summary>Identified only probably; an operator must confirm the model first.</summary>
    public bool NeedsConfirmation => Model is not null && Confidence == MatchConfidence.Probable;

    /// <summary>The printer at <paramref name="address"/>: its network address or another spelling of it, or its "net:" key.</summary>
    public bool IsAtAddress(string address) =>
        Same(NetworkAddress, address)
        || Same(NetworkIdentity?.Address, address)
        || Same(Key, NetworkKey(address))
        || NetworkAliases.Any(alias => Same(alias, address));

    /// <summary>
    /// A USB printer known only from an earlier connection: its device node, or a USB queue without a connected device, is not
    /// connected while an eligible USB printer of the same model in <paramref name="candidates"/> is (a replaced unit, a loaner,
    /// or one Windows recorded under another serial number). A disconnected printer next to any other printer is not one,
    /// because a switched-off ticket printer is not connected either.
    /// </summary>
    public bool IsSupersededAmong(IEnumerable<PrinterCandidate> candidates) =>
        this is { Connection: ConnectionType.Usb, PhysicallyAvailable: false, Kind: TargetKind.UsbDevice or TargetKind.ExistingQueue, Model: { } model }
        && candidates.Any(c => c is { Kind: TargetKind.UsbDevice, IsEligible: true, Device.IsPresent: true } && Same(c.Model!.Id, model.Id));

    public static string UsbKey(string instanceId) => "usb:" + instanceId;
    public static string NetworkKey(string address) => "net:" + address;
    public static string QueueKey(string queueName) => "queue:" + queueName;

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

public sealed record DetectRequest
{
    /// <summary>Addresses to identify explicitly (operator entry, branch file, command line).</summary>
    public IReadOnlyList<string> NetworkAddresses { get; init; } = [];
    /// <summary>Probe addresses of existing TCP/IP queues that may be TA printers (bounded by ConnectionConfig.MaxNetworkProbes).</summary>
    public bool ProbeExistingNetworkQueues { get; init; } = true;
}

public sealed record DetectionResult
{
    public required SystemSnapshot Snapshot { get; init; }
    public IReadOnlyList<PrinterCandidate> Candidates { get; init; } = [];
    /// <summary>Environment-wide findings (spooler, protected print mode, unconfirmed dimensions, unsupported devices ...).</summary>
    public IReadOnlyList<Finding> Findings { get; init; } = [];
    /// <summary>
    /// Queues whose driver names a supported model but that are not candidates: their printer reports another identity, another
    /// manufacturer's printer is connected on their USB port, their drivers disagree, they print to a network printer that a
    /// queue the setup cannot configure (pooled ports, an LPR or Internet Port) prints to as well, that queue included (the
    /// printer then has no candidate under any of its spellings), or their port cannot be linked to a printer. Also queues whose
    /// driver names no single model (a class driver) that a printer they print to is not set up automatically for: pooled over
    /// Standard TCP/IP ports, or that printer's ticket queue on an LPR or Internet Port (also of a printer only requested by
    /// address). A pooled queue that is also a USB printer's queue stays that printer's and is not listed. And a queue already
    /// set up as the ticket printer (the ticket form, or the configured queue name) that may be a TA printer's but leads to no
    /// printer the setup configures. One of them may be the ticket printer's, so no other printer is chosen without asking,
    /// and the configured model picks none when a rejected queue's printer may be of that model (<see cref="SetupSession.RejectedQueueMayBe"/>).
    /// </summary>
    public IReadOnlyList<string> RejectedQueues { get; init; } = [];
    /// <summary>
    /// Queues that may be a TA printer's and whose driver names no single model (a class driver), on a Standard TCP/IP port whose
    /// host was not probed because the limit of network probes was reached (connection.maxNetworkProbes) and that leads to no
    /// probed spelling of a printer: their printer is not known and they are no candidate, so one of them may be the ticket
    /// printer's queue (a NetworkIdentityUnavailable warning names each), and they count as such when the printer is chosen.
    /// </summary>
    public IReadOnlyList<string> UncheckedQueues { get; init; } = [];
}

public enum SelectionReason { SingleCandidate, PreferredByConfiguration, MultipleCandidates, NeedsConfirmation, NoCandidates }

public sealed record CandidateSelection(PrinterCandidate? Selected, SelectionReason Reason, IReadOnlyList<PrinterCandidate> Choices)
{
    /// <summary>
    /// USB printers left out of the decision because they are not connected while a USB printer of the same model is (an
    /// earlier unit: replaced, loaner, or recorded under another serial number). Neither selected nor offered.
    /// </summary>
    public IReadOnlyList<PrinterCandidate> Superseded { get; init; } = [];

    /// <summary>
    /// The configured address the decision was limited to (an operator's entry in this run, --address, or the branch file's
    /// printerAddress): only the printer there is chosen, and NoCandidates then means nothing usable answers there. Null when
    /// no address is configured.
    /// </summary>
    public string? Address { get; init; }

    /// <summary>Repair only: the ticket printer Setup would choose, which has no local queue to repair yet (Setup sets it up).</summary>
    public PrinterCandidate? NeedsSetup { get; init; }
}

/// <summary>Identifies the chosen target across processes (UI -> elevated worker) and across re-detection.</summary>
public sealed record TargetSelector(string Key, string? ModelId = null, bool ModelAssertedByUser = false)
{
    public static TargetSelector For(PrinterCandidate candidate, bool modelAssertedByUser = false) =>
        new(candidate.Key, candidate.Model?.Id, modelAssertedByUser);

    /// <summary>The network address encoded in a "net:" key, otherwise null.</summary>
    public string? NetworkAddress => Key.StartsWith("net:", StringComparison.Ordinal) ? Key[4..] : null;
}

public sealed record SetupOptions
{
    public SetupMode Mode { get; init; } = SetupMode.Setup;
    /// <summary>IT testing only (--allow-unconfirmed-dimensions): run Setup/Repair although ticket dimensions are not confirmed.</summary>
    public bool AllowUnconfirmedDimensions { get; init; }
    public string? PreferredModelId { get; init; }
    public string? PreferredAddress { get; init; }
    public string? QueueNameOverride { get; init; }
    public string AppVersion { get; init; } = "";
    /// <summary>Where the configuration came from (file path or "embedded"), for reports.</summary>
    public string ConfigSource { get; init; } = "";
}

public enum Severity { Info, Warning, Error }

/// <summary>Every condition the tool can report. The UI localizes by code; Detail is English technical text.</summary>
public enum FindingCode
{
    // Environment
    SpoolerNotRunning,
    SpoolerDisabled,
    SpoolerUnavailable,
    ProtectedPrintModeEnabled,
    UnsupportedOperatingSystem,
    UnsupportedArchitecture,
    DimensionsUnconfirmed,
    ConfigurationInvalid,
    ServerConnectionNotManaged,

    // Detection
    NoSupportedPrinterFound,
    MultipleSupportedPrinters,
    ProbableMatchRequiresConfirmation,
    UnsupportedPrinterDetected,
    DeviceNotConnected,
    NetworkPrinterUnreachable,
    NetworkIdentityUnavailable,
    NetworkIdentityMismatch,
    IdentityConflict,
    DuplicateQueues,

    // Driver
    DriverReady,
    DriverMissing,
    DriverOutdated,
    DriverPackageUnavailable,
    DriverPackageIntegrityFailed,
    DriverSignatureInvalid,
    DriverPackageNotApproved,
    DriverPackageIncompatible,
    DriverInstallFailed,
    DeviceDriverNotBound,
    RebootRequired,

    // Queue and port
    QueueReady,
    QueueMissing,
    QueueDriverMismatch,
    QueuePortMismatch,
    QueueCreateFailed,
    PortCreateFailed,
    PrinterOffline,
    PrinterError,
    PrinterPaused,

    // Ticket form
    FormReady,
    FormMissing,
    FormSizeMismatch,
    FormSimilarExists,
    FormCreateFailed,

    // Driver media support and settings
    DriverListsForm,
    DriverDoesNotListForm,
    TicketSizeOutsideDriverRange,
    TicketSizeOutsideTrayRange,
    DriverRejectedSetting,
    PaperSourceNotFound,
    PaperSourceRejected,

    // Defaults
    GlobalDefaultsCorrect,
    GlobalDefaultsIncorrect,
    UserDefaultsCorrect,
    UserDefaultsIncorrect,
    UserDefaultsAbsent,
    DefaultsNotPersisted,
    PrintTicketMismatch,
    PrintCapabilitiesUnavailable,
    ApplicationMayOverrideDefaults,

    // Elevation and general
    ElevationRequired,
    ElevationDeclined,
    ElevationFailed,
    OperationFailed,
    Cancelled,
    /// <summary>The elevated worker loaded other configuration bytes than the window; nothing was changed (restart the program).</summary>
    ConfigurationChanged,
    /// <summary>Another run holds the machine-wide apply lock; nothing was changed (try again later).</summary>
    ApplyLockBusy,
    /// <summary>
    /// A queue whose driver names a supported model was not configured: the printer it may belong to reports another identity,
    /// another manufacturer's printer is connected on its USB port, its host's queues use drivers of different models, it is
    /// pooled or on an LPR or Internet Port (a warning that names the network printer it prints to, which is then not set up
    /// automatically; also for a pool that is a USB printer's queue), or its port cannot be linked to a printer. The same warning
    /// names a queue whose driver names no single model (a class driver) that is pooled over Standard TCP/IP ports, or that is the
    /// ticket queue of the printer it prints to through an LPR or Internet Port. As a plan blocker or an action failure: a queue
    /// the setup does not configure for the printer already prints to the port a new queue would use (a Standard TCP/IP port to
    /// the printer, or the USB printer's port), so none is added.
    /// </summary>
    QueueLeftUnchanged,
    /// <summary>
    /// The queue's accepted driver ignores Windows forms (the KX v3 driver), so it does not offer the ticket form; the queue is
    /// moved to the preferred driver.
    /// </summary>
    DriverIgnoresTicketForm,
    /// <summary>After moving the queue to another driver the ticket defaults could not be set, so its driver and defaults were restored.</summary>
    QueueDriverRestored,
    /// <summary>The queue's previous driver or defaults could not be restored after the ticket defaults failed with the new driver.</summary>
    QueueDriverRestoreFailed,
}

public sealed record Finding(FindingCode Code, Severity Severity, string Detail)
{
    /// <summary>Structured values for reports and localization placeholders (e.g. "queue", "expected", "actual").</summary>
    public IReadOnlyDictionary<string, string> Data { get; init; } = new Dictionary<string, string>();
}

public enum ActionKind
{
    StartSpooler,
    PrepareDriverPackage,
    InstallDriver,
    BindUsbDevice,
    CreateTcpIpPort,
    CreateQueue,
    ChangeQueueDriver,
    ChangeQueuePort,
    CreateForm,
    UpdateForm,
    ConfigureGlobalDefaults,
    ConfigureUserDefaults,
}

public sealed record PlannedAction(ActionKind Kind, bool RequiresElevation, string Summary)
{
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

/// <summary>Read-only assessment of a target plus the ordered actions needed to reach the desired state.</summary>
public sealed record SetupPlan
{
    public required PrinterCandidate Target { get; init; }
    public required SetupMode Mode { get; init; }
    public IReadOnlyList<Finding> Findings { get; init; } = [];
    public IReadOnlyList<PlannedAction> Actions { get; init; } = [];
    /// <summary>Errors that prevent applying the plan (e.g. unconfirmed dimensions, driver rejects the ticket size).</summary>
    public IReadOnlyList<Finding> Blockers { get; init; } = [];

    public bool RequiresElevation => Actions.Any(a => a.RequiresElevation);
    public bool CanProceed => Blockers.Count == 0;
    public bool NothingToDo => Actions.Count == 0;
}

public enum ApplyScope
{
    /// <summary>Machine-wide actions (driver, port, queue, form, global defaults); needs elevation.</summary>
    Machine,
    /// <summary>Current-user actions (per-user printing defaults); must run as the employee, not elevated as someone else.</summary>
    User,
    All,
}

public enum StepId { Detect, CheckDriver, InstallDriver, ConfigureTicket, Verify, Ready }

public enum StepState { Pending, Running, Succeeded, Skipped, Warning, Failed }

public enum ProgressPhase { Started, Downloading, Verifying, Extracting, Installing, Waiting, Configuring, Completed, Skipped, Failed }

/// <summary>Progress for the six visible steps; the UI localizes by Step/Action/Phase/Code and shows Detail as technical text.</summary>
public sealed record ProgressUpdate(StepId Step, StepState State, ActionKind? Action, ProgressPhase Phase, string Detail)
{
    public double? Fraction { get; init; }
    public FindingCode? Code { get; init; }
}

public enum ActionOutcome { Succeeded, AlreadySatisfied, Skipped, Failed }

public sealed record ActionResult(PlannedAction Action, ActionOutcome Outcome, string Detail)
{
    public FindingCode? FailureCode { get; init; }
    public int? Win32Error { get; init; }
}

public sealed record ApplyResult
{
    public IReadOnlyList<ActionResult> Results { get; init; } = [];
    public IReadOnlyList<Finding> Findings { get; init; } = [];
    public bool RebootRequired { get; init; }

    /// <summary>No action failed and no Error finding: a refusal (<see cref="Refused"/>) is not a success either.</summary>
    public bool Succeeded => Results.All(r => r.Outcome != ActionOutcome.Failed) && Findings.All(f => f.Severity != Severity.Error);

    /// <summary>
    /// Nothing was applied because an Error stopped the apply before any action ran: the worker found the target gone or the
    /// plan blocked, or another run was applying changes (no action results), or the plan's blockers skipped every action.
    /// </summary>
    public bool Refused => Findings.Any(f => f.Severity == Severity.Error) && Results.All(r => r.Outcome == ActionOutcome.Skipped);
}

public enum VerificationItem
{
    SpoolerRunning,
    DriverInstalled,
    QueueExists,
    QueueUsesAcceptedDriver,
    QueuePortCorrect,
    FormExists,
    FormSizeExact,
    DriverListsForm,
    DefaultPaper,
    DefaultOrientation,
    DefaultDuplex,
    DefaultScale,
    DefaultPaperSource,
    UserDefaultsConsistent,
    EffectiveDefaults,
    PrintTicketMediaSize,
    PrinterOnline,
    DeviceConnected,
}

public enum CheckStatus { Pass, Warning, Fail, NotApplicable, Unknown }

public sealed record VerificationCheck(VerificationItem Item, CheckStatus Status, string Expected, string Actual, string Detail);

public enum ReadinessLevel { Ready, ReadyWithWarnings, NotReady }

/// <summary>What the success screen shows.</summary>
public sealed record ConfiguredSummary
{
    public string ModelName { get; init; } = "";
    public string QueueName { get; init; } = "";
    public string DriverName { get; init; } = "";
    public string? DriverVersion { get; init; }
    /// <summary>For example "USB (USB001)", "Network 10.0.0.5, port 9100" or "WSD".</summary>
    public string Connection { get; init; } = "";
    /// <summary>For example "AMWAG Ticket, 82.296 mm x 170.18 mm (3.24 in x 6.70 in)".</summary>
    public string PaperSize { get; init; } = "";
    public string? PaperSource { get; init; }
}

public sealed record VerificationReport
{
    public required string TargetKey { get; init; }
    public PrinterCandidate? Target { get; init; }
    public IReadOnlyList<VerificationCheck> Checks { get; init; } = [];
    public ReadinessLevel Readiness { get; init; }
    public ConfiguredSummary Summary { get; init; } = new();
    public IReadOnlyList<Finding> Findings { get; init; } = [];
}

/// <summary>Everything IT needs to troubleshoot one run; rendered as text or JSON.</summary>
public sealed record DiagnosticReport
{
    public required DateTimeOffset GeneratedAt { get; init; }
    public required string AppVersion { get; init; }
    public required string SessionId { get; init; }
    public required SetupMode Mode { get; init; }
    public required string ConfigSource { get; init; }
    public required string ConfigVersion { get; init; }
    public required string TicketDescription { get; init; }
    public required bool TicketDimensionsConfirmed { get; init; }
    public SystemSnapshot? Snapshot { get; init; }
    public IReadOnlyList<PrinterCandidate> Candidates { get; init; } = [];
    public IReadOnlyList<SetupPlan> Plans { get; init; } = [];
    public IReadOnlyList<ApplyResult> ApplyResults { get; init; } = [];
    public IReadOnlyList<VerificationReport> Verifications { get; init; } = [];
    public IReadOnlyList<Finding> Findings { get; init; } = [];
    public string? LogFile { get; init; }
}

/// <summary>Process exit codes for silent (IT-managed) runs and the elevated worker.</summary>
public static class ExitCodes
{
    public const int Ready = 0;
    public const int ReadyWithWarnings = 1;
    public const int NotReady = 2;
    public const int NoSupportedPrinter = 3;
    public const int SelectionRequired = 4;
    public const int ElevationRequired = 5;
    public const int ConfigurationInvalid = 6;
    public const int SpoolerUnavailable = 7;
    public const int Blocked = 8;
    public const int Cancelled = 9;
    public const int InternalError = 10;
    public const int InvalidArguments = 11;
}
