using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Flow;

/// <summary>Questions the flow may need to ask. The UI answers with panels; silent mode answers non-interactively.</summary>
public interface ISetupInteraction
{
    /// <summary>Several eligible printers: returns the chosen one, or null to cancel.</summary>
    Task<PrinterCandidate?> ChooseCandidateAsync(IReadOnlyList<PrinterCandidate> candidates, CancellationToken cancellationToken);

    /// <summary>A probable match: true only when the operator confirms the shown model.</summary>
    Task<bool> ConfirmProbableMatchAsync(PrinterCandidate candidate, CancellationToken cancellationToken);

    /// <summary>Nothing usable was found (or a network address failed): returns an IP address or host name to identify, or null to stop.</summary>
    Task<string?> RequestNetworkAddressAsync(NetworkAddressPrompt prompt, CancellationToken cancellationToken);

    /// <summary>A reachable network printer whose model could not be read: returns the operator-chosen model, or null.</summary>
    Task<ModelDefinition?> ChooseModelForUnidentifiedPrinterAsync(PrinterCandidate candidate, IReadOnlyList<ModelDefinition> models, CancellationToken cancellationToken);

    /// <summary>
    /// Nobody answers (silent mode): a model left unconfirmed is a decision for IT, so the run stops and says how to confirm it,
    /// instead of treating it as an operator's "no".
    /// </summary>
    bool IsUnattended => false;

    /// <summary>
    /// The model IT asserted for this printer with --address, --model and --confirm-model, without asking anyone; null when IT
    /// asserted none for it. It also replaces the model of an existing queue's driver when the printer cannot tell its model.
    /// </summary>
    ModelDefinition? AssertedModel(PrinterCandidate candidate, IReadOnlyList<ModelDefinition> models) => null;
}

public sealed record NetworkAddressPrompt(bool NothingDetected, string? PreviousAddress, FindingCode? PreviousProblem, string? PreviousDetail)
{
    /// <summary>The operator chose none of the printers offered: the ticket printer may be one that was not found.</summary>
    public bool OfferedPrintersDeclined { get; init; }
}

public enum FlowEventKind { Step, TargetSelected, ElevationRequested, ElevationDeclined, Info }

public sealed record FlowUpdate(FlowEventKind Kind, ProgressUpdate? Progress = null, PrinterCandidate? Target = null, string? Detail = null);

public enum FlowOutcome { Ready, ReadyWithWarnings, NotReady, NoPrinter, Cancelled, ElevationDeclined, Blocked, Failed }

public sealed record FlowResult(FlowOutcome Outcome)
{
    /// <summary>Verification of the primary (selected) target.</summary>
    public VerificationReport? Verification { get; init; }
    public IReadOnlyList<VerificationReport> Verifications { get; init; } = [];
    public PrinterCandidate? Target { get; init; }
    public DiagnosticReport? Report { get; init; }
    public IReadOnlyList<Finding> Findings { get; init; } = [];
    /// <summary>
    /// The findings that describe the state at the end of the run: once setup actions were applied (starting the Print Spooler
    /// is not one), the plans' findings (what was found before the changes, such as "the driver will be installed") are left
    /// out; otherwise the same as <see cref="Findings"/>. In Diagnostics with a known ticket printer (<see cref="Target"/>) only
    /// its plan and read-back count next to the detection and run findings, and none when it is known but could not be checked
    /// (its model must be confirmed, or nothing usable is at the configured address); <see cref="Findings"/> keeps every printer's.
    /// </summary>
    public IReadOnlyList<Finding> CurrentFindings { get; init; } = [];
    public bool RebootRequired { get; init; }
    public int ExitCode { get; init; }
}

/// <summary>Everything a flow run needs; built once per process by the bootstrapper.</summary>
public sealed record FlowEnvironment(
    DeploymentConfig Config,
    BranchOverride? Branch,
    SetupOptions Options,
    SetupServices Services)
{
    /// <summary>
    /// Repair only: the printer the operator asked to repair (the Repair button after a run or a test print). Repair changes
    /// only this printer, or nothing when it is gone; null lets Repair choose (see <see cref="SetupSession.SelectRepairTarget"/>).
    /// </summary>
    public TargetSelector? RepairTarget { get; init; }
}

public sealed record WorkerRequest(SetupMode Mode, TargetSelector Target, bool AllowUnconfirmedDimensions, string SessionId);

/// <summary>
/// How an elevated run ended. Cancelled: the operator stopped the run after the worker had started; the broker waited for the
/// worker to stop at its next action boundary, so finished actions stay applied (the result holds what it reported, if anything).
/// </summary>
public enum ElevationOutcome { Completed, Declined, FailedToStart, WorkerFailed, Cancelled }

public sealed record ElevatedRunResult(ElevationOutcome Outcome, int ExitCode)
{
    public ApplyResult? Result { get; init; }
    public string? Error { get; init; }
}

/// <summary>Runs machine-wide steps with administrator rights: directly when already elevated, otherwise in an elevated worker (one UAC prompt).</summary>
public interface IElevationBroker
{
    bool IsElevated { get; }

    Task<ElevatedRunResult> RunMachineStepsAsync(WorkerRequest request, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken);
}

public enum AppRole { Interactive, Silent, Worker }

/// <summary>Parsed command line. See docs/DEPLOYMENT.md for the documented switches.</summary>
public sealed record CommandLineOptions
{
    public AppRole Role { get; init; } = AppRole.Interactive;
    public SetupMode Mode { get; init; } = SetupMode.Setup;
    public string? Address { get; init; }
    public string? ModelId { get; init; }
    /// <summary>IT asserts the model of a printer at --address whose identity cannot be read (--confirm-model).</summary>
    public bool ConfirmModel { get; init; }
    /// <summary>"en" or "ar".</summary>
    public string? Language { get; init; }
    public string? ReportPath { get; init; }
    public string? ConfigPath { get; init; }
    public bool AllowUnconfirmedDimensions { get; init; }
    public bool ShowHelp { get; init; }

    // Worker-only (internal) switches.
    public string? PipeName { get; init; }
    public string? SessionId { get; init; }
    public string? TargetKey { get; init; }
    public bool ModelAssertedByUser { get; init; }
    /// <summary>The window that started the worker: the worker reports only to a pipe server in that process.</summary>
    public int? WindowProcessId { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>Process-wide services handed to the main window.</summary>
public sealed record AppRuntime(
    FlowEnvironment Environment,
    IElevationBroker Elevation,
    CommandLineOptions CommandLine,
    ILog Log,
    string AppVersion,
    string ExecutableDirectory);
