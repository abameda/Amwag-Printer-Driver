using System.Globalization;
using System.Text.RegularExpressions;
using Amwag.PrinterSetup.App.Elevation;
using Amwag.PrinterSetup.App.Hosting;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Flow;

/// <summary>
/// Orchestrates one run for the UI and silent mode: detect, choose a target (asking only when needed), assess,
/// run machine steps through the elevation broker, apply per-user steps as the signed-in user, verify, report.
/// Setup and Repair change exactly one printer, the ticket printer; other supported printers are only reported.
/// </summary>
public sealed class SetupFlow
{
    public SetupFlow(FlowEnvironment environment, ISetupInteraction interaction, IElevationBroker elevation)
    {
        Environment = environment;
        Interaction = interaction;
        Elevation = elevation;
    }

    public FlowEnvironment Environment { get; }
    public ISetupInteraction Interaction { get; }
    public IElevationBroker Elevation { get; }

    /// <summary>
    /// Takes the machine-wide apply lock before this (elevated) process applies machine steps itself; null when another run kept
    /// it too long. Tests replace it, so the busy case needs no real lock.
    /// </summary>
    internal Func<ILog, CancellationToken, Action, IDisposable?> AcquireApplyLock { get; init; } =
        (log, cancellationToken, waiting) => MachineApplyLock.Acquire(MachineApplyLock.DefaultTimeout, log, cancellationToken, waiting);

    /// <summary>
    /// Runs once and never throws: failures become <see cref="FlowOutcome.Failed"/> and cancellation <see cref="FlowOutcome.Cancelled"/>
    /// (once machine steps have started, only after the result was read back from the system).
    /// Engine calls run on background threads; questions and progress continue on the caller's synchronization context.
    /// </summary>
    public Task<FlowResult> RunAsync(IProgress<FlowUpdate> progress, CancellationToken cancellationToken) =>
        new FlowRun(this, progress, cancellationToken).ExecuteAsync();

    /// <summary>State of one run: everything detected, planned, applied and verified goes into the report.</summary>
    private sealed class FlowRun
    {
        private const int MaxAddressPrompts = 5;
        private static readonly TimeSpan SpoolerStartTimeout = TimeSpan.FromSeconds(60);
        private static readonly StepId[] ApplySteps = [StepId.InstallDriver, StepId.ConfigureTicket];

        private readonly SetupFlow _flow;
        private readonly IProgress<FlowUpdate> _progress;
        private readonly CancellationToken _cancellation;
        private readonly ILog _log;
        private readonly IProgress<ProgressUpdate> _engineProgress;
        private readonly List<string> _addresses = [];
        private readonly List<SetupPlan> _plans = [];
        private readonly List<ApplyResult> _applyResults = [];
        private readonly List<VerificationReport> _verifications = [];
        private readonly List<Finding> _findings = [];
        // Printers whose probable (or unknown) model nobody confirmed while a target was chosen.
        private readonly List<PrinterCandidate> _unconfirmed = [];
        private readonly HashSet<string> _superseded = new(StringComparer.OrdinalIgnoreCase);
        // Not connected USB units offered again because only their queue uses the ticket paper: detection's "left out" note is dropped.
        private readonly HashSet<string> _reoffered = new(StringComparer.OrdinalIgnoreCase);
        // Targets whose queue this run chose among queues of the printer that could not be told apart: detection's warning stays,
        // and the next detection's note that the queue is "already set up as the ticket printer" (by this run) is dropped.
        private readonly HashSet<string> _queueGuessed = new(StringComparer.OrdinalIgnoreCase);
        // The machine steps left a warning about this run's own change that later runs can no longer give (a queue added next to
        // one that may print to the same printer, or a queue chosen among the printer's queues): the run is ready with warnings.
        private bool _changeToCheck;
        private SetupSession? _session;
        private DetectionResult? _detection;
        private bool _selectionDeclined;
        // The printers offered when nobody chose one of them.
        private IReadOnlyList<PrinterCandidate> _declinedChoices = [];
        private bool _confirmationRequired;
        // IT's model for the printer at --address (--model, --confirm-model) is not what the printer reports: IT decides.
        private bool _assertionRefused;
        private string? _spoolerStartFailure;
        // The address the operator entered in this run: it replaces the configured one as the printer to set up.
        private string? _operatorAddress;
        // Diagnostics: the key of the ticket printer the run judged, "" when it is known but could not be checked (its model must
        // be confirmed, or nothing usable is at the configured address); null when it is one of several.
        private string? _diagnosedTicket;

        public FlowRun(SetupFlow flow, IProgress<FlowUpdate> progress, CancellationToken cancellationToken)
        {
            _flow = flow;
            _progress = progress;
            _cancellation = cancellationToken;
            _log = flow.Environment.Services.Log;
            _engineProgress = new EngineProgress(this);
        }

        private FlowEnvironment Env => _flow.Environment;

        private SetupMode Mode => Env.Options.Mode;

        /// <summary>The ticket printer's address: the operator's entry in this run, else --address, else the branch file's printerAddress.</summary>
        private string? PreferredAddress => _operatorAddress ?? Session.PreferredAddress;

        private SetupSession Session => _session!;

        private DetectionResult Detection => _detection!;

        public async Task<FlowResult> ExecuteAsync()
        {
            try
            {
                _session = new SetupSession(Env.Config, Env.Branch, Env.Options, Env.Services);
                AddAddress(Env.Options.PreferredAddress);
                AddAddress(Env.Branch?.PrinterAddress);
                _log.Info("flow.start", "Run started.", new Dictionary<string, object?>
                {
                    ["mode"] = CommandLineParser.ModeName(Mode),
                    ["addresses"] = _addresses.ToArray(),
                    ["elevated"] = _flow.Elevation.IsElevated,
                    ["repairTarget"] = Mode == SetupMode.Repair ? Env.RepairTarget?.Key : null,
                });
                return await RunAsync();
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                _log.Info("flow.cancelled", "The run was cancelled.");
                _findings.Add(new Finding(FindingCode.Cancelled, Severity.Warning, "The run was cancelled before it finished."));
                return Finish(FlowOutcome.Cancelled, null, null);
            }
            catch (Exception ex)
            {
                _log.Error("flow.failed", "The run failed unexpectedly.", ex);
                _findings.Add(FailureFinding(ex));
                if (ex is SpoolerException { IsSpoolerUnavailable: true })
                    _findings.Add(new Finding(FindingCode.SpoolerUnavailable, Severity.Error, ex.Message));
                return Finish(FlowOutcome.Failed, null, null);
            }
        }

        private async Task<FlowResult> RunAsync()
        {
            ReportStep(StepId.Detect, StepState.Running, ProgressPhase.Started, "Looking for supported printers");
            _detection = await DetectAsync();

            if (Mode != SetupMode.Diagnostics)
            {
                List<Finding> blockers = EnvironmentBlockers();
                // Only a stopped spooler can be fixed here; anything else blocks before anyone is asked to approve starting it.
                if (Detection.Snapshot.Spooler is { State: ServiceRunState.Stopped, StartMode: not ServiceStartMode.Disabled } && blockers.All(IsSpoolerBlocker))
                {
                    if (await StartSpoolerAsync() is { } declined)
                        return declined;
                    _detection = await DetectAsync();
                    blockers = EnvironmentBlockers();
                }

                if (blockers.Count > 0)
                    return EndBlocked(blockers);
            }

            return Mode switch
            {
                SetupMode.Diagnostics => await DiagnoseAsync(),
                SetupMode.Repair => await RepairAsync(),
                _ => await SetupAsync(),
            };
        }

        private async Task<FlowResult> SetupAsync()
        {
            Selected? selected = await SelectTargetAsync();
            // A silent run whose only usable printer still needs its model confirmed stops for IT; nothing was changed.
            _confirmationRequired = selected is null && !_selectionDeclined && !_assertionRefused && _flow.Interaction.IsUnattended && _unconfirmed.Count > 0;
            foreach (PrinterCandidate candidate in _unconfirmed.DistinctBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
                _findings.Add(UnconfirmedFinding(candidate, stopsTheRun: _confirmationRequired));

            if (selected is null)
            {
                if (EndUndecided() is { } undecided)
                    return undecided;

                ReportStep(StepId.Detect, StepState.Failed, ProgressPhase.Failed, "No supported printer was found", FindingCode.NoSupportedPrinterFound);
                if (_unconfirmed.Count > 0)
                    // As Repair's end of the same name: the window says the model was not confirmed and offers Try again.
                    AddNoPrinterFinding("No supported TA Triumph-Adler printer was confirmed.", new Dictionary<string, string> { ["reason"] = "modelNotConfirmed" });
                else if (PreferredAddress is { } address)
                    AddNoPrinterFinding(NotUsableAt(address, "no other printer was set up in its place."), NotUsableData(address));
                else
                    AddNoPrinterFinding("No supported TA Triumph-Adler printer was found.");
                return Finish(FlowOutcome.NoPrinter, null, null);
            }

            ReportUpdate(new FlowUpdate(FlowEventKind.TargetSelected, Target: selected.Target));
            ReportStep(StepId.Detect, StepState.Succeeded, ProgressPhase.Completed, Describe(selected.Target));
            TargetRun run = await ConfigureAsync(selected.Target, selected.Selector);
            return Finish(run.Outcome, run.Target, run.Verification);
        }

        /// <summary>
        /// Ends a run that stopped for a decision nobody made: IT's model does not fit the printer, the operator chose none of the
        /// printers offered, or (silent) a model must be confirmed. Nothing was changed. Null when none of these stopped it.
        /// </summary>
        private FlowResult? EndUndecided()
        {
            if (_assertionRefused)
            {
                ReportStep(StepId.Detect, StepState.Skipped, ProgressPhase.Skipped, "The model given for the printer does not match it", FindingCode.NetworkIdentityMismatch);
                return Finish(FlowOutcome.Cancelled, null, null);
            }

            if (_selectionDeclined)
            {
                string detail = NoneChosen(_declinedChoices.Count);
                if (_flow.Interaction.IsUnattended)
                {
                    // A silent run never chooses: the finding says how IT names the ticket printer, and every printer whose model
                    // must be decided first (and that IT gave no model for) gets the switches that confirm it.
                    detail += " " + DeclinedAdvice(_declinedChoices);
                    foreach (PrinterCandidate candidate in _declinedChoices.Where(c => NeedsDecision(c) && _flow.Interaction.AssertedModel(c, Env.Config.Models) is null))
                        _findings.Add(UnconfirmedFinding(candidate, stopsTheRun: true));
                }
                _findings.Add(new Finding(FindingCode.Cancelled, Severity.Warning, detail)
                {
                    Data = new Dictionary<string, string> { ["choices"] = string.Join(", ", _declinedChoices.Select(c => c.Key)) },
                });
                ReportStep(StepId.Detect, StepState.Skipped, ProgressPhase.Skipped, "No printer was chosen", FindingCode.MultipleSupportedPrinters);
                return Finish(FlowOutcome.Cancelled, null, null);
            }

            if (_confirmationRequired)
            {
                ReportStep(StepId.Detect, StepState.Skipped, ProgressPhase.Skipped, "The printer's model must be confirmed", FindingCode.ProbableMatchRequiresConfirmation);
                return Finish(FlowOutcome.Cancelled, null, null);
            }

            return null;
        }

        /// <summary>The computer cannot be set up as it is (spooler, protected print mode, platform, configuration): nothing is changed.</summary>
        private FlowResult EndBlocked(List<Finding> blockers)
        {
            _findings.AddRange(blockers.Except(Detection.Findings));
            _log.Warn("flow.blocked.environment", "The computer cannot be set up as it is.", new Dictionary<string, object?>
            {
                ["blockers"] = blockers.Select(b => b.Code.ToString()).ToArray(),
            });
            ReportStep(StepId.Detect, StepState.Failed, ProgressPhase.Failed, blockers[0].Detail, blockers[0].Code);
            return Finish(FlowOutcome.Blocked, null, null);
        }

        private async Task<FlowResult> RepairAsync()
        {
            // Repair changes one printer, the ticket printer as Setup would choose it: another supported TA printer may be an
            // ordinary office printer. The printer just set up or tested decides when given; a printer whose model must be
            // confirmed is asked about (silent: --confirm-model); one without a queue needs Setup; several printers are the
            // operator's choice (silent runs stop). IT's model for the printer at --address applies as in Setup.
            TargetSelector? requested = Env.RepairTarget;
            CandidateSelection selection = await Task.Run(() => Session.SelectRepairTarget(Detection, requested), _cancellation);
            _log.Info("flow.selection", "Repair selection finished.", new Dictionary<string, object?>
            {
                ["reason"] = selection.Reason.ToString(),
                ["requested"] = requested?.Key,
                ["selected"] = selection.Selected?.Key,
                ["choices"] = selection.Choices.Select(c => c.Key).ToArray(),
                ["needsSetup"] = selection.NeedsSetup?.Key,
            });
            RecordSuperseded(selection.Superseded);
            List<PrinterCandidate> reoffered = selection.Choices.Where(c => c.IsSupersededAmong(Detection.Candidates)).ToList();
            foreach (PrinterCandidate unit in reoffered)
                _reoffered.Add(unit.Key);

            // Once its model is confirmed (or asserted by IT), the chosen printer is repaired when it has a queue to repair. The
            // window does not ask to confirm a printer that has no queue: Repair could not change it, and Setup asks.
            PrinterCandidate? needsSetup = selection.NeedsSetup;
            Selected? target = null;
            if (!_flow.Interaction.IsUnattended && selection is { Reason: SelectionReason.NeedsConfirmation, Choices: [{ Queue: null } toSetUp] }
                && _flow.Interaction.AssertedModel(toSetUp, Env.Config.Models) is null)
                needsSetup = toSetUp;
            else if (await ChooseAsync(selection) is { } chosen)
            {
                if (IsRepairable(chosen.Target))
                    target = chosen;
                else
                    needsSetup = chosen.Target;
            }

            // IT's model applies to the printer at --address also when it has no queue yet: one the printer contradicts is refused
            // as in Setup, not left for a Setup run to find out.
            if (target is null && !_assertionRefused && needsSetup is { IsEligible: true } queueless
                && _flow.Interaction.AssertedModel(queueless, Env.Config.Models) is { } asserted && !SameKey(asserted.Id, queueless.Model!.Id))
                RefuseAssertion(queueless, asserted.Id);

            _confirmationRequired = target is null && needsSetup is null && !_selectionDeclined && !_assertionRefused && _flow.Interaction.IsUnattended && _unconfirmed.Count > 0;
            foreach (PrinterCandidate candidate in _unconfirmed.DistinctBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
                _findings.Add(UnconfirmedFinding(candidate, stopsTheRun: _confirmationRequired));
            if (target is null && _selectionDeclined)
            {
                // The operator in the window saw the choice and chose none: the stop is the headline, the note stays in the details.
                Severity severity = _flow.Interaction.IsUnattended ? Severity.Warning : Severity.Info;
                foreach (PrinterCandidate unit in reoffered)
                    _findings.Add(new Finding(FindingCode.MultipleSupportedPrinters, severity, ReofferedUnitDetail(unit) + ToConfirmBesideUnit(selection.Choices) + " Repair changed nothing.")
                    {
                        Data = new Dictionary<string, string> { ["target"] = unit.Key, ["queue"] = unit.Queue!.Name },
                    });
            }

            if (target is null && EndUndecided() is { } undecided)
                return undecided;

            if (target is null)
            {
                ReportStep(StepId.Detect, StepState.Failed, ProgressPhase.Failed, "No printer queue to repair was found", FindingCode.NoSupportedPrinterFound);
                _findings.Add(NothingToRepair(requested, needsSetup, selection.Address));
                return Finish(FlowOutcome.NoPrinter, null, null);
            }

            // Printers that share the target's queue (one queue pooled over several USB printers) were repaired with it.
            foreach (PrinterCandidate other in Detection.Candidates.Where(c => IsRepairable(c) && !SameKey(c.Key, target.Target.Key) && !_superseded.Contains(c.Key)
                && !SameKey(c.Queue!.Name, target.Target.Queue?.Name)))
            {
                _findings.Add(new Finding(FindingCode.MultipleSupportedPrinters, Severity.Info,
                    $"Queue '{other.Queue!.Name}' ({other.Model!.Id}) is another supported printer, not the ticket printer; Repair did not change it.")
                {
                    Data = new Dictionary<string, string> { ["target"] = other.Key, ["queue"] = other.Queue.Name },
                });
            }

            ReportUpdate(new FlowUpdate(FlowEventKind.TargetSelected, Target: target.Target));
            ReportStep(StepId.Detect, StepState.Succeeded, ProgressPhase.Completed, Describe(target.Target));
            TargetRun run = await ConfigureAsync(target.Target, target.Selector);
            return Finish(run.Outcome, run.Target, run.Verification);
        }

        /// <summary>
        /// Why a not connected USB unit is offered again: only its queue uses the ticket paper while a unit of the same model is
        /// connected, so either may be the ticket printer.
        /// </summary>
        private static string ReofferedUnitDetail(PrinterCandidate unit) =>
            $"Queue '{unit.Queue!.Name}' belongs to a {unit.Model!.Id} that is not connected, but it is the queue that uses the ticket paper, "
            + $"while another {unit.Model.Id} is connected; it is not known which of them is the ticket printer. Run Setup to set up the "
            + "connected printer as the ticket printer, or switch the earlier printer on (delete its queue if it is gone) and run the setup again.";

        /// <summary>
        /// The printers whose model must be confirmed among <paramref name="choices"/> next to a unit offered again: Setup offers
        /// them next to the connected unit, so a silent Setup stops for them too. Empty when there are none.
        /// </summary>
        private static string ToConfirmBesideUnit(IEnumerable<PrinterCandidate> choices) =>
            choices.Where(NeedsDecision).ToList() is { Count: > 0 } toDecide
                ? $" The printers whose model must be confirmed may be the ticket printer too ({Listed(toDecide)}): Setup offers them next to the connected "
                    + "printer, so choose the ticket printer in Setup's window, or set up a network one silently with its switches (see the ProbableMatchRequiresConfirmation finding)."
                : "";

        /// <summary>
        /// Why Repair found no printer to change; nothing was changed. Data "reason" says which end it is (noQueue: a known printer
        /// without a queue; modelNotConfirmed; address: nothing usable at the configured address; leftUnchanged: a queue there that
        /// the setup leaves unchanged; printersWithoutQueue: supported printers without a queue; noPrinter), and "suggestedMode"
        /// "setup" where Setup can go on from here: it adds a printer to Windows, which Repair never does.
        /// </summary>
        private Finding NothingToRepair(TargetSelector? requested, PrinterCandidate? needsSetup, string? address)
        {
            var data = new Dictionary<string, string>();
            string reason;
            string detail;
            if (requested is not null)
            {
                reason = "noQueue";
                data["target"] = requested.Key;
                detail = $"The printer chosen for Repair ({requested.Key}) has no queue of a supported printer to repair; nothing was changed. Run Setup to set it up.";
            }
            else if (needsSetup is not null)
            {
                reason = "noQueue";
                data["target"] = needsSetup.Key;
                detail = $"The ticket printer, {Describe(needsSetup)}, has no queue to repair yet; nothing was changed. Run Setup to set it up.";
            }
            else if (_unconfirmed.Count > 0)
            {
                reason = "modelNotConfirmed";
                detail = "No supported TA Triumph-Adler printer was confirmed; Repair changed nothing.";
            }
            else if (address is not null)
            {
                foreach (var (key, value) in NotUsableData(address))
                    data[key] = value;
                reason = data.ContainsKey("leftUnchanged") ? "leftUnchanged" : "address";
                detail = NotUsableAt(address, "Repair changed nothing and no other printer was changed in its place.");
            }
            else if (Detection.Candidates.Any(c => c.IsEligible || NeedsDecision(c)))
            {
                reason = "printersWithoutQueue";
                detail = "Supported printers were found, but none has a queue to repair yet; nothing was changed. Run Setup to set up the ticket printer.";
            }
            else
            {
                reason = "noPrinter";
                detail = "Repair found no existing queue for a supported printer; run Setup to install the printer.";
            }
            data["reason"] = reason;
            if (reason is "noQueue" or "printersWithoutQueue" or "noPrinter")
                data["suggestedMode"] = "setup";
            return new Finding(FindingCode.NoSupportedPrinterFound, Severity.Error, detail) { Data = data };
        }

        private async Task<FlowResult> DiagnoseAsync()
        {
            // What blocks Setup on this computer (the spooler, protected print mode, the platform) decides Diagnostics too, so a
            // detection rule built on it never reports a blocked computer as ready. Without a running spooler nothing can be read.
            List<Finding> blockers = EnvironmentBlockers().Where(b => b.Code != FindingCode.DimensionsUnconfirmed).ToList();
            if (blockers.Any(IsSpoolerBlocker))
                return EndBlocked(blockers);

            List<PrinterCandidate> eligible = Detection.Candidates.Where(c => c.IsEligible).ToList();
            // The ticket printer is chosen as Setup and Repair choose it. A USB printer known only from an earlier connection,
            // next to a connected one of the same model, is left out of that choice, so it is not counted here either.
            TicketPrinter ticket = await Task.Run(() => FindTicketPrinter(eligible), _cancellation);
            if (eligible.Count == 0 && ticket.Unconfirmed.Count == 0)
            {
                ReportStep(StepId.Detect, StepState.Failed, ProgressPhase.Failed, "No supported printer was found", FindingCode.NoSupportedPrinterFound);
                if (ticket.UnusableAddress is { } nowhere)
                    AddNoPrinterFinding(NotUsableAt(nowhere), NotUsableData(nowhere));
                else
                    AddNoPrinterFinding("No supported TA Triumph-Adler printer was found.");
                return blockers.Count > 0 ? EndBlocked(blockers) : Finish(FlowOutcome.NoPrinter, null, null);
            }

            RecordSuperseded(ticket.Superseded);
            foreach (PrinterCandidate unit in ticket.Reoffered)
                _reoffered.Add(unit.Key);
            List<PrinterCandidate> counted = eligible.Where(c => !ticket.Superseded.Any(s => SameKey(s.Key, c.Key))).ToList();
            PrinterCandidate? primary = ticket.Primary;
            _diagnosedTicket = primary?.Key ?? (ticket.Unconfirmed.Count > 0 || ticket.UnusableAddress is not null ? "" : null);
            if (ticket.Unconfirmed.Count > 0)
            {
                // A printer that is probably supported is not "no printer": it can be set up once its model is confirmed. When it
                // is the printer IT named, no other printer stands in for it.
                foreach (PrinterCandidate candidate in ticket.Unconfirmed)
                    _findings.Add(DiagnosticsUnconfirmedFinding(candidate));
                if (ticket.Unconfirmed is { Count: > 1 } several)
                {
                    // Several, none on the ticket paper: one of them is the ticket printer, the choice Setup and Repair would ask for.
                    _findings.Add(new Finding(FindingCode.MultipleSupportedPrinters, Severity.Warning,
                        string.Create(CultureInfo.InvariantCulture,
                            $"{several.Count} supported printers were found and none is known to be the ticket printer ({Listed(several)}); {Checked(several)} ")
                        + ChoiceAdvice(several) + ConfirmAdvice(several) + RejectedQueuesNote() + UncheckedQueuesNote())
                    {
                        Data = new Dictionary<string, string> { ["choices"] = string.Join(", ", several.Select(c => c.Key)) },
                    });
                    ReportStep(StepId.Detect, StepState.Warning, ProgressPhase.Completed,
                        string.Create(CultureInfo.InvariantCulture, $"Found {several.Count} supported printers"), FindingCode.MultipleSupportedPrinters);
                }
                else
                {
                    ReportStep(StepId.Detect, StepState.Warning, ProgressPhase.Completed, "The printer's model must be confirmed", FindingCode.ProbableMatchRequiresConfirmation);
                }
            }
            else if (ticket.UnusableAddress is { } address)
            {
                // The printer IT named is not there; the other printers are checked for the report, none stands in for it.
                _findings.Add(new Finding(FindingCode.NoSupportedPrinterFound, Severity.Error,
                    LeftUnchangedAt(address) is null
                        ? $"No supported TA Triumph-Adler printer is at {address}, the configured printer address (nothing answers there, or the device there is not a supported printer); the other printers were checked, none is the ticket printer."
                        : NotUsableAt(address, "the other printers were checked, none is the ticket printer."))
                {
                    Data = NotUsableData(address),
                });
                ReportStep(StepId.Detect, StepState.Failed, ProgressPhase.Failed, "The configured ticket printer was not found at " + address, FindingCode.NoSupportedPrinterFound);
            }
            else if (primary is not null)
            {
                ReportUpdate(new FlowUpdate(FlowEventKind.TargetSelected, Target: primary));
                ReportStep(StepId.Detect, StepState.Succeeded, ProgressPhase.Completed, Describe(primary));
            }
            else
            {
                // The printers one of which is the ticket printer, as Setup offers them: the queues of the printer at the configured
                // address, else all, among them those whose model must be decided first (only printers of a known model are checked).
                IReadOnlyList<PrinterCandidate> undecided = ticket.Undecided;
                var toDecide = undecided.Where(NeedsDecision).ToList();
                string detail = ticket.Reoffered is [var unit, ..]
                    ? ReofferedUnitDetail(unit)
                        + (toDecide.Count > 0 ? $" The printers whose model must be confirmed may be the ticket printer too: {Listed(toDecide)}." : "")
                        + " " + Sentence(Checked(undecided)) + ConfirmAdvice(undecided)
                    : ticket.Address is { } configured
                        ? (undecided.Count > 1
                            ? string.Create(CultureInfo.InvariantCulture,
                                $"The printer at {configured} has {undecided.Count} queues ({QueuesOnHosts(undecided)}) and none is known to be the ticket queue; {Checked(undecided)} ")
                                + "Choose the ticket queue in Setup, or name the host of the ticket queue with --address (printerAddress in branch.json), or delete the queue that is not used."
                            // One queue on another host than the configured address, next to queues the probe limit left unchecked.
                            : $"The printer at {configured} has queue {QueuesOnHosts(undecided)}, but other queues were not checked, so it is not known to be the ticket queue; {Checked(undecided)} "
                                + "Choose it in Setup, or name the host of the ticket queue with --address (printerAddress in branch.json).")
                            + ConfirmAdvice(undecided)
                        : undecided is [{ IsEligible: true } only] && HasQueuesElsewhere
                            ? RejectedQueuesDetail(only)
                            : (undecided.Count == 1
                                ? $"1 supported printer was found, but it is not known to be the ticket printer ({Listed(undecided)}); {Checked(undecided)} "
                                : string.Create(CultureInfo.InvariantCulture,
                                    $"{undecided.Count} supported printers were found and none is known to be the ticket printer ({Listed(undecided)}); {Checked(undecided)} "))
                                + ChoiceAdvice(undecided) + ConfirmAdvice(undecided) + RejectedQueuesNote();
                detail += UncheckedQueuesNote();
                _findings.Add(new Finding(FindingCode.MultipleSupportedPrinters, Severity.Warning, detail)
                {
                    Data = new Dictionary<string, string> { ["choices"] = string.Join(", ", undecided.Select(c => c.Key)) },
                });
                ReportStep(StepId.Detect, StepState.Warning, ProgressPhase.Completed,
                    undecided.Count == 1 ? "Found 1 supported printer" : string.Create(CultureInfo.InvariantCulture, $"Found {undecided.Count} supported printers"),
                    FindingCode.MultipleSupportedPrinters);
            }

            foreach (StepId step in ApplySteps)
                ReportStep(step, StepState.Skipped, ProgressPhase.Skipped, "Diagnostics changes nothing");

            VerificationReport? primaryVerification = null;
            SetupPlan? primaryPlan = null;
            var levels = new List<ReadinessLevel>();
            foreach (PrinterCandidate candidate in counted)
            {
                ReportStep(StepId.CheckDriver, StepState.Running, ProgressPhase.Started, "Checking " + Describe(candidate));
                SetupPlan plan = await Task.Run(() => Session.Assess(candidate, _cancellation), _cancellation);
                _plans.Add(plan);
                LogPlan(plan);
                ReportDriverCheck(plan);

                VerificationReport verification = await VerifyAsync(SelectorFor(candidate), _cancellation);
                if (ticket.Undecided.Any(c => SameKey(c.Key, candidate.Key)))
                    levels.Add(verification.Readiness);
                if (SameKey(candidate.Key, primary?.Key))
                    (primaryPlan, primaryVerification) = (plan, verification);
            }

            // The step list ends on the ticket printer, not on whichever printer was checked last; without one it says so.
            if (primaryPlan is not null)
            {
                ReportDriverCheck(primaryPlan);
                ReportVerification(primaryVerification!);
            }
            else if (ticket.Unconfirmed.Count > 0 || ticket.UnusableAddress is not null)
            {
                FindingCode why = ticket.Unconfirmed.Count > 0 ? FindingCode.ProbableMatchRequiresConfirmation : FindingCode.NoSupportedPrinterFound;
                ReportStep(StepId.CheckDriver, StepState.Skipped, ProgressPhase.Skipped, "The ticket printer could not be checked", why);
                ReportStep(StepId.Verify, StepState.Skipped, ProgressPhase.Skipped, "The ticket printer could not be checked", why);
            }
            else if (counted.Count > 0)
            {
                // One printer can be checked and still not be known as the ticket printer (queues next to it may be its).
                string checkedText = counted.Count == 1 ? "1 printer was checked; it is not known to be the ticket printer" : "Several printers were checked";
                ReportStep(StepId.CheckDriver, StepState.Warning, ProgressPhase.Completed, checkedText, FindingCode.MultipleSupportedPrinters);
                ReportStep(StepId.Verify, StepState.Warning, ProgressPhase.Completed, checkedText, FindingCode.MultipleSupportedPrinters);
            }

            // Only the ticket printer decides; the other printers are checked for the report. A ticket printer whose model is not
            // confirmed, or that is not at its configured address, is not ready. Without a known ticket printer a ready one of the
            // printers it may be gives "ready with warnings" (the warning says the ticket printer is not known), none "not ready".
            // A blocker decides over all of it, as it does for Setup.
            FlowOutcome outcome = blockers.Count > 0 ? FlowOutcome.Blocked
                : ticket.Unconfirmed.Count > 0 || ticket.UnusableAddress is not null ? FlowOutcome.NotReady
                : primaryVerification is not null ? OutcomeOf(primaryVerification.Readiness)
                : levels.DefaultIfEmpty(ReadinessLevel.NotReady).Min() == ReadinessLevel.NotReady ? FlowOutcome.NotReady : FlowOutcome.ReadyWithWarnings;
            if (outcome == FlowOutcome.Blocked)
                _findings.AddRange(blockers.Except(Detection.Findings));
            return Finish(outcome, primaryVerification?.Target ?? primary, primaryVerification);
        }

        /// <summary>
        /// The ticket printer as Setup and Repair would choose it: Repair's choice (Setup's printer when it has a queue, or the only
        /// queue with the ticket paper among several), else Setup's choice of a printer without a queue yet, else the only eligible
        /// printer left when nobody has to choose; none when several remain (Undecided: the printers Setup offers, among them those
        /// whose model must be decided first, at the configured address when there is one), when the printer to decide about
        /// needs its model confirmed (Unconfirmed: the one Repair asks about, whose queue has the ticket paper, else Setup's), or
        /// when nothing usable is at the configured address (UnusableAddress). Superseded: the not connected USB printers the
        /// choice leaves out because a USB printer of the same model is connected; Repair offers one again (Reoffered) when only
        /// its queue uses the ticket paper.
        /// </summary>
        private TicketPrinter FindTicketPrinter(IReadOnlyList<PrinterCandidate> eligible)
        {
            CandidateSelection automatic = Session.SelectAutomatically(Detection);
            CandidateSelection repair = Session.SelectRepairTarget(Detection, null);
            IReadOnlyList<PrinterCandidate> superseded = repair.Superseded;
            IReadOnlyList<PrinterCandidate> reoffered = [.. automatic.Superseded.Where(s => !superseded.Any(r => SameKey(r.Key, s.Key)))];
            if (repair.Selected is { } repairTarget)
                return new TicketPrinter(repairTarget, [], superseded) { Reoffered = reoffered };
            bool undecided = automatic.Reason == SelectionReason.MultipleCandidates || repair.Reason == SelectionReason.MultipleCandidates;
            if (automatic.Selected is { IsEligible: true } single && !undecided)
                return new TicketPrinter(single, [], superseded);
            // The printer whose model Repair asks to confirm is the ticket printer when its queue has the ticket paper, or when every
            // printer must be confirmed anyway; otherwise it is one of the printers Setup offers (Undecided).
            if (repair.Reason == SelectionReason.NeedsConfirmation
                && (automatic.Reason == SelectionReason.NeedsConfirmation || repair.Choices is [var toConfirm] && Session.HasQueueOnTicketForm(toConfirm)))
                return new TicketPrinter(null, repair.Choices, superseded);
            if (automatic.Reason == SelectionReason.NeedsConfirmation)
                return new TicketPrinter(null, automatic.Choices, superseded);
            if (automatic is { Reason: SelectionReason.NoCandidates, Address: { } address })
                return new TicketPrinter(null, [], superseded) { UnusableAddress = address };
            IReadOnlyList<PrinterCandidate> left = automatic.Reason == SelectionReason.MultipleCandidates
                ? [.. automatic.Choices, .. reoffered]
                : [.. eligible.Where(c => !superseded.Any(s => SameKey(s.Key, c.Key)))];
            return new TicketPrinter(left is [{ IsEligible: true } only] && !undecided ? only : null, [], superseded)
            {
                Undecided = left,
                Address = automatic.Address,
                Reoffered = reoffered,
            };
        }

        /// <summary>Assess, apply machine steps (elevated), apply per-user steps (as this user), verify.</summary>
        private async Task<TargetRun> ConfigureAsync(PrinterCandidate target, TargetSelector selector)
        {
            ReportStep(StepId.CheckDriver, StepState.Running, ProgressPhase.Started, "Checking " + Describe(target));
            SetupPlan plan = await Task.Run(() => Session.Assess(target, _cancellation), _cancellation);
            _plans.Add(plan);
            LogPlan(plan);
            ReportDriverCheck(plan);
            if (!plan.CanProceed)
            {
                foreach (StepId step in ApplySteps)
                    ReportStep(step, StepState.Skipped, ProgressPhase.Skipped, "Not started: " + plan.Blockers[0].Detail, plan.Blockers[0].Code);
                return new TargetRun(FlowOutcome.Blocked, target, null);
            }

            var results = new List<ApplyResult>();
            SetupPlan? userPlan = plan;
            ApplyResult? applied = null;
            bool blocked = false;
            if (plan.RequiresElevation)
            {
                MachineApply machine = await ApplyMachineAsync(plan, selector);
                if (machine.Result is { } machineResult)
                {
                    results.Add(machineResult);
                    _applyResults.Add(machineResult);
                }

                if (machine.End == MachineEnd.NotStarted)
                {
                    // The code tells the window these steps never began (approval declined, helper not started).
                    FindingCode why = machine.Outcome == FlowOutcome.ElevationDeclined ? FindingCode.ElevationDeclined : FindingCode.ElevationFailed;
                    foreach (StepId step in ApplySteps)
                        ReportStep(step, StepState.Skipped, ProgressPhase.Skipped, "Not applied", why);
                    return new TargetRun(machine.Outcome!.Value, target, null);
                }

                if (machine.End != MachineEnd.Applied)
                    return await EndAfterMachineStepsAsync(target, selector, plan, results, machine);

                applied = machine.Result!;
                KeepPossibleDuplicates(target, applied);
                KeepQueueChoice(target);
                try
                {
                    (userPlan, blocked) = await PlanUserStepAsync(selector);
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    return await EndAfterMachineStepsAsync(target, selector, plan, results, Stopped(machine.Result, selector));
                }

                if (!machine.Result!.Succeeded)
                    userPlan = null;
            }

            if (userPlan is { CanProceed: true } && userPlan.Actions.Any(a => !a.RequiresElevation))
            {
                _log.Info("flow.apply.user", "Applying the per-user printing defaults as the signed-in user.");
                // Not cancelled from outside: the runner stops before its next action when the run is cancelled and reports what it skipped.
                ApplyResult userResult = await Task.Run(() => Session.Apply(userPlan, ApplyScope.User, _engineProgress, _cancellation), CancellationToken.None);
                results.Add(userResult);
                _applyResults.Add(userResult);
            }

            // A stop during the per-user step leaves what it did not start undone, not "applied with warnings".
            ReportApplySteps(ExpectedActions(plan, userPlan, applied), results,
                _cancellation.IsCancellationRequested ? new StepStop(StepState.Skipped, FindingCode.Cancelled, "Not done: the setup was stopped") : null);
            if (_cancellation.IsCancellationRequested && results.Count > 0)
                return await EndStoppedAsync(target, selector, results);

            // Once anything was applied, a stop must not skip the read-back.
            VerificationReport verification = await VerifyAsync(selector, results.Count > 0 ? CancellationToken.None : _cancellation);
            FlowOutcome outcome = blocked ? FlowOutcome.Blocked : OutcomeOf(verification.Readiness);
            // A restart Windows asked for to finish installing the driver is pending whatever the device shows until then, and a
            // warning about this run's own change is shown now or never.
            if (outcome == FlowOutcome.Ready && (results.Any(r => r.RebootRequired) || _changeToCheck))
                outcome = FlowOutcome.ReadyWithWarnings;
            return new TargetRun(outcome, verification.Target ?? target, verification);
        }

        /// <summary>
        /// The machine steps (driver, queue, form) change what the per-user step sees: plans it again from fresh reads. Detection
        /// probes what the elevated worker and the final check probe (the target's address; every detection adds --address and the
        /// branch address), not the addresses entered in this run, so the probe limit leaves the same hosts out for all three and
        /// they find the same printer.
        /// </summary>
        private async Task<(SetupPlan? Plan, bool Blocked)> PlanUserStepAsync(TargetSelector selector)
        {
            _detection = await DetectAsync(selector.NetworkAddress is { } address ? [address] : []);
            PrinterCandidate? refreshed = await Task.Run(() => Session.Resolve(Detection, selector), _cancellation);
            if (refreshed is null)
            {
                _log.Warn("flow.target.lost", "The printer was not found again after the machine-wide steps.", new Dictionary<string, object?> { ["target"] = selector.Key });
                _findings.Add(new Finding(FindingCode.OperationFailed, Severity.Error, "The printer was not found again after the machine-wide steps.")
                {
                    Data = new Dictionary<string, string> { ["target"] = selector.Key },
                });
                return (null, false);
            }

            SetupPlan userPlan = await Task.Run(() => Session.Assess(refreshed, _cancellation), _cancellation);
            _plans.Add(userPlan);
            LogPlan(userPlan);
            return (userPlan, !userPlan.CanProceed);
        }

        /// <summary>
        /// Detection warned that another queue may print to the target's printer (a WSD queue or an unresolved or unprobed host
        /// name it could not compare, or a class-driver queue on a port the setup does not configure), and the machine steps have
        /// now added a queue for the target. The next detection no longer warns: the address has a queue. So the warning stays in
        /// the run's findings, saying that a second queue may now exist (Data reason "queueAdded"), and the run is ready with
        /// warnings, so the window and the exit code say it now.
        /// </summary>
        private void KeepPossibleDuplicates(PrinterCandidate target, ApplyResult machineResult)
        {
            // A port alone is no second queue: without the queue the next detection still warns as before.
            if (!machineResult.Results.Any(r => r is { Outcome: ActionOutcome.Succeeded, Action.Kind: ActionKind.CreateQueue }))
                return;
            string[] spellings = [.. new[] { target.NetworkAddress }.OfType<string>(), .. target.NetworkAliases];
            foreach (Finding possible in Detection.Findings.Where(f => f is { Code: FindingCode.DuplicateQueues, Severity: Severity.Warning }
                && f.Data.TryGetValue("address", out string? address) && spellings.Contains(address, StringComparer.OrdinalIgnoreCase)
                && f.Data.ContainsKey("queue")))
            {
                string address = possible.Data["address"];
                var data = new Dictionary<string, string>(possible.Data) { ["reason"] = "queueAdded" };
                _findings.Add(new Finding(FindingCode.DuplicateQueues, Severity.Warning,
                    $"A queue was added for {address}, but queue '{possible.Data["queue"]}' may print to the same printer. "
                    + "If they are one printer, keep the queue employees print to and delete the other.")
                {
                    Data = data,
                });
                _changeToCheck = true;
            }
        }

        /// <summary>
        /// Detection warned that the target's queue was chosen among queues of the printer none of which could be told apart as the
        /// ticket printer (by its driver and name), and the machine steps have now put it on the ticket form. The next detection
        /// takes it for the queue "already set up as the ticket printer", so the warning stays in the run's findings, that note is
        /// left out (<see cref="DetectionFindings"/>), and the run is ready with warnings.
        /// </summary>
        private void KeepQueueChoice(PrinterCandidate target)
        {
            foreach (Finding guessed in Detection.Findings.Where(f => f is { Code: FindingCode.DuplicateQueues, Severity: Severity.Warning }
                && f.Data.TryGetValue("candidate", out string? candidate) && SameKey(candidate, target.Key)).ToList())
            {
                _findings.Add(guessed);
                _queueGuessed.Add(target.Key);
                _changeToCheck = true;
            }
        }

        /// <summary>
        /// Ends a target whose machine steps were refused (nothing changed), gave no result (unknown) or were stopped (the step that
        /// was running finished): no per-user step, the apply steps say why, and unless the elevated side found a blocker the
        /// state is read back from fresh reads, also when the run was cancelled.
        /// </summary>
        private async Task<TargetRun> EndAfterMachineStepsAsync(PrinterCandidate target, TargetSelector selector, SetupPlan plan, IReadOnlyList<ApplyResult> results, MachineApply machine)
        {
            Finding reason = machine.Reason!;
            StepStop stop = machine.End switch
            {
                MachineEnd.Stopped => new StepStop(StepState.Skipped, FindingCode.Cancelled, "Not done: the setup was stopped"),
                MachineEnd.Unknown => new StepStop(StepState.Failed, reason.Code, "Not known whether this was applied: " + reason.Detail),
                // Refused: the elevated side began nothing; the code says why.
                _ => new StepStop(StepState.Skipped, reason.Code, "Not started: " + reason.Detail),
            };
            ReportApplySteps(plan.Actions, results, stop);
            if (machine.Outcome == FlowOutcome.Blocked)
                return new TargetRun(FlowOutcome.Blocked, target, null);

            // Only the system says what changed: read it back whatever happened, with a token the operator's stop does not cancel.
            // A refusal changed nothing, so the run is not ready even when the check finds nothing to fault (a USB binding, for
            // example, is not verified): run it again.
            VerificationReport verification = await VerifyAsync(selector, CancellationToken.None);
            FlowOutcome outcome = machine.Outcome ?? (machine.End == MachineEnd.Refused ? FlowOutcome.NotReady : OutcomeOf(verification.Readiness));
            return new TargetRun(outcome, verification.Target ?? target, verification);
        }

        /// <summary>The run was stopped after changes were applied: reads them back (not cancellable) and ends as cancelled.</summary>
        private async Task<TargetRun> EndStoppedAsync(PrinterCandidate target, TargetSelector selector, IReadOnlyList<ApplyResult> results)
        {
            _log.Info("flow.stopped", "The run was stopped after changes were applied; reading back the result.", new Dictionary<string, object?> { ["target"] = selector.Key });
            if (!results.Any(r => r.Findings.Any(f => f.Code == FindingCode.Cancelled)))
                _findings.Add(new Finding(FindingCode.Cancelled, Severity.Warning, "The run was stopped before it finished; changes already made stay in place and were read back from the system."));
            VerificationReport verification = await VerifyAsync(selector, CancellationToken.None);
            return new TargetRun(FlowOutcome.Cancelled, verification.Target ?? target, verification);
        }

        private async Task<MachineApply> ApplyMachineAsync(SetupPlan plan, TargetSelector selector)
        {
            string needed = string.Join("; ", plan.Actions.Where(a => a.RequiresElevation).Select(a => a.Summary));
            if (_flow.Elevation.IsElevated)
            {
                _log.Info("flow.apply.machine", "Applying the machine-wide actions in this elevated process.", new Dictionary<string, object?> { ["actions"] = needed });
                // The mutex belongs to the thread that took it: take and release it inside the same task.
                ApplyResult? applied = await Task.Run(() =>
                {
                    using IDisposable? applyLock = _flow.AcquireApplyLock(_log, _cancellation, () =>
                        ReportUpdate(new FlowUpdate(FlowEventKind.Step, WorkerHost.WaitingForApplyLock(selector))));
                    return applyLock is null ? null : Session.Apply(plan, ApplyScope.Machine, _engineProgress, _cancellation);
                }, _cancellation);

                // A busy lock is the same refusal the elevated worker returns: nothing changed, and the state is still read back.
                if (applied is null)
                    return Refused(new ApplyResult { Findings = [MachineApplyLock.BusyFinding()] }, exitCode: null);
                return _cancellation.IsCancellationRequested ? Stopped(applied, selector) : new MachineApply(MachineEnd.Applied, applied);
            }

            var request = new WorkerRequest(Mode, selector, Env.Options.AllowUnconfirmedDimensions, _log.SessionId);
            ElevatedRunResult run = await RunElevatedAsync(request, needed);
            switch (run.Outcome)
            {
                case ElevationOutcome.Completed when run.Result is { } refusal && IsRefusal(refusal, run.ExitCode):
                    return Refused(refusal, run.ExitCode);
                case ElevationOutcome.Completed when run.Result is { } result:
                    return _cancellation.IsCancellationRequested ? Stopped(result, selector) : new MachineApply(MachineEnd.Applied, result);
                case ElevationOutcome.Cancelled:
                    return Stopped(run.Result, selector);
                case ElevationOutcome.Declined:
                    _findings.Add(DeclinedFinding(run, needed));
                    return new MachineApply(MachineEnd.NotStarted, null, FlowOutcome.ElevationDeclined);
                case ElevationOutcome.FailedToStart:
                    _findings.Add(new Finding(FindingCode.ElevationFailed, Severity.Error, run.Error ?? "The elevated helper could not be started."));
                    return new MachineApply(MachineEnd.NotStarted, null, FlowOutcome.Failed);
                default:
                    // The helper started but gave no result: what it changed is unknown until the system is read back.
                    var unknown = new Finding(FindingCode.OperationFailed, Severity.Error, run.Error ?? "The elevated helper did not return a result.")
                    {
                        Data = new Dictionary<string, string> { ["exitCode"] = run.ExitCode.ToString(CultureInfo.InvariantCulture) },
                    };
                    _findings.Add(unknown);
                    return new MachineApply(MachineEnd.Unknown, run.Result, FlowOutcome.Failed, unknown);
            }
        }

        /// <summary>
        /// The elevated side changed nothing and said why: another run holds the apply lock, the printer is gone, or its own
        /// assessment found a blocker (then the run is blocked, as when this process finds one).
        /// </summary>
        private MachineApply Refused(ApplyResult result, int? exitCode)
        {
            Finding? reason = result.Findings.FirstOrDefault(f => f.Severity == Severity.Error);
            if (reason is null)
            {
                reason = new Finding(FindingCode.OperationFailed, Severity.Error,
                    string.Create(CultureInfo.InvariantCulture, $"The elevated helper changed nothing (exit code {exitCode})."));
                _findings.Add(reason);
            }

            _log.Warn("flow.apply.refused", "The machine-wide steps were refused; nothing was changed.", new Dictionary<string, object?>
            {
                ["code"] = reason.Code.ToString(),
                ["detail"] = reason.Detail,
                ["exitCode"] = exitCode,
            });
            bool blocked = exitCode is ExitCodes.Blocked or ExitCodes.ConfigurationInvalid or ExitCodes.SpoolerUnavailable;
            return new MachineApply(MachineEnd.Refused, result, blocked ? FlowOutcome.Blocked : null, reason);
        }

        /// <summary>The run was cancelled while (or right after) the machine steps ran: the step that was running finished, later ones did not start.</summary>
        private MachineApply Stopped(ApplyResult? result, TargetSelector selector)
        {
            _log.Info("flow.apply.stopped", "The run was stopped during the machine-wide steps; reading back what was applied.", new Dictionary<string, object?>
            {
                ["target"] = selector.Key,
                ["hasResult"] = result is not null,
            });
            Finding? reported = result?.Findings.FirstOrDefault(f => f.Code == FindingCode.Cancelled);
            Finding reason = reported ?? new Finding(FindingCode.Cancelled, Severity.Warning,
                "The run was stopped during the administrator steps: the step that was running finished and later steps were not started. The result was read back from the system.")
            {
                Data = new Dictionary<string, string> { ["target"] = selector.Key },
            };
            if (reported is null)
                _findings.Add(reason);
            return new MachineApply(MachineEnd.Stopped, result, FlowOutcome.Cancelled, reason);
        }

        // The spooler must run before anything can be identified, so it is started before the target and its machine steps are
        // known. Without elevation that costs its own UAC prompt, and the machine steps may ask a second time later. The spooler
        // almost always runs already, so this rare extra prompt is accepted. Returns a result only when the run must stop.
        private async Task<FlowResult?> StartSpoolerAsync()
        {
            ReportStep(StepId.Detect, StepState.Running, ProgressPhase.Started, "Starting the Print Spooler service", FindingCode.SpoolerNotRunning);
            _log.Info("flow.spooler.start", "The Print Spooler service is stopped; starting it.");
            if (_flow.Elevation.IsElevated)
            {
                _spoolerStartFailure = await Task.Run(() =>
                    Env.Services.Spooler.TryStartService(SpoolerStartTimeout, out string? reason) ? null : reason ?? "The Print Spooler service did not start.",
                    _cancellation);
                return null;
            }

            const string needed = "Start the Print Spooler service";
            var request = new WorkerRequest(Mode, new TargetSelector(WorkerArguments.SpoolerStartTarget), Env.Options.AllowUnconfirmedDimensions, _log.SessionId);
            ElevatedRunResult run = await RunElevatedAsync(request, needed);
            if (run.Outcome == ElevationOutcome.Declined)
            {
                _findings.Add(DeclinedFinding(run, needed));
                return Finish(FlowOutcome.ElevationDeclined, null, null);
            }

            if (run.Result is not null)
                _applyResults.Add(run.Result);
            if (run is { Outcome: ElevationOutcome.Completed, Result: { } result } && IsRefusal(result, run.ExitCode))
            {
                // The helper changed nothing and said why (another run holds the apply lock, the configuration changed): the run
                // ends as a refused machine step does, not as a spooler that would not start.
                MachineApply refused = Refused(result, run.ExitCode);
                ReportStep(StepId.Detect, StepState.Failed, ProgressPhase.Failed, refused.Reason!.Detail, refused.Reason.Code);
                return Finish(refused.Outcome ?? FlowOutcome.NotReady, null, null);
            }

            // Whether the spooler runs now is read back by the next detection, not taken from the worker.
            _spoolerStartFailure = run.Result?.Results.FirstOrDefault(r => r.Outcome == ActionOutcome.Failed)?.Detail ?? run.Error;
            return null;
        }

        private async Task<ElevatedRunResult> RunElevatedAsync(WorkerRequest request, string needed)
        {
            ReportUpdate(new FlowUpdate(FlowEventKind.ElevationRequested, Detail: needed));
            ElevatedRunResult run = await _flow.Elevation.RunMachineStepsAsync(request, _engineProgress, _cancellation);
            _log.Info("flow.elevation.result", "The elevated step returned.", new Dictionary<string, object?>
            {
                ["target"] = request.Target.Key,
                ["outcome"] = run.Outcome.ToString(),
                ["exitCode"] = run.ExitCode,
                ["succeeded"] = run.Result?.Succeeded,
                ["error"] = run.Error,
            });
            if (run.Outcome == ElevationOutcome.Declined)
                ReportUpdate(new FlowUpdate(FlowEventKind.ElevationDeclined, Detail: run.Error));
            return run;
        }

        private async Task<Selected?> SelectTargetAsync()
        {
            string? previousAddress = PreferredAddress;
            (FindingCode Code, string Detail)? inputProblem = null;
            for (int prompts = 0; ; prompts++)
            {
                CandidateSelection selection = await Task.Run(() => Session.SelectAutomatically(Detection, _operatorAddress), _cancellation);
                _log.Info("flow.selection", "Automatic selection finished.", new Dictionary<string, object?>
                {
                    ["reason"] = selection.Reason.ToString(),
                    ["address"] = selection.Address,
                    ["selected"] = selection.Selected?.Key,
                    ["choices"] = selection.Choices.Select(c => c.Key).ToArray(),
                });
                RecordSuperseded(selection.Superseded);

                Selected? selected = await ChooseAsync(selection);
                if (selected is not null || _assertionRefused || prompts == MaxAddressPrompts)
                    return selected;
                // Silent runs never choose. In the window an operator who chose none of the printers offered may name the ticket
                // printer's address instead: it may be a network printer that was never set up on this computer.
                if (_selectionDeclined && _flow.Interaction.IsUnattended)
                    return null;

                Finding? problem = inputProblem is null ? AddressProblem(previousAddress) : null;
                var prompt = new NetworkAddressPrompt(
                    Detection.Candidates.Count == 0,
                    previousAddress,
                    inputProblem?.Code ?? problem?.Code,
                    inputProblem?.Detail ?? problem?.Detail)
                {
                    OfferedPrintersDeclined = _selectionDeclined,
                };
                string? entered = (await _flow.Interaction.RequestNetworkAddressAsync(prompt, _cancellation))?.Trim();
                if (string.IsNullOrEmpty(entered))
                {
                    _log.Info("flow.address.none", "No network address was given.");
                    return null;
                }

                // The address replaces the choice the operator declined.
                _selectionDeclined = false;
                previousAddress = entered;
                if (!PrinterAddress.TryNormalize(entered, out string? normalized, out string? addressProblem))
                {
                    _log.Info("flow.address.invalid", "The entered network address is not valid.", new Dictionary<string, object?> { ["address"] = entered });
                    inputProblem = (FindingCode.NetworkPrinterUnreachable, addressProblem);
                    continue;
                }

                entered = normalized;
                previousAddress = entered;
                inputProblem = null;
                // The operator named the printer: in this run the printer at that address is the one to set up.
                _operatorAddress = entered;
                AddAddress(entered);
                ReportStep(StepId.Detect, StepState.Running, ProgressPhase.Started, "Looking for the printer at " + entered);
                _detection = await DetectAsync();
            }
        }

        /// <summary>The target the selection leaves, after the questions it needs; null when nothing (or nobody) chose one.</summary>
        private async Task<Selected?> ChooseAsync(CandidateSelection selection)
        {
            switch (selection.Reason)
            {
                case SelectionReason.SingleCandidate or SelectionReason.PreferredByConfiguration when selection.Selected is { IsEligible: true } single:
                    return await UseAsync(single);

                case SelectionReason.MultipleCandidates:
                case SelectionReason.NeedsConfirmation when selection.Choices.Count > 1:
                    // IT's model for the printer at --address that every printer offered contradicts (its queue hosts): refused,
                    // not left as a choice nobody can make.
                    if (ContradictedByAll(selection.Choices) is { } contradicted)
                        return RefuseAssertion(selection.Choices[0], contradicted.Id, selection.Address);
                    // The operator chooses first and only the chosen printer's model is confirmed, also when every printer needs
                    // its model confirmed. A printer whose model the operator then does not confirm leaves the others to choose from.
                    var choices = selection.Choices.ToList();
                    while (choices.Count > 0)
                    {
                        if (await ChooseOneAsync(choices) is not { } offered)
                            return null;
                        Selected? chosen = offered.IsEligible ? await UseAsync(offered) : await ConfirmAsync(offered);
                        if (chosen is not null || _assertionRefused)
                            return chosen;
                        choices.Remove(offered);
                    }
                    return null;

                case SelectionReason.NeedsConfirmation when selection.Choices is [var only]:
                    return await ConfirmAsync(only);

                default:
                    // Nothing usable (at the configured address): the operator may enter an address.
                    return null;
            }
        }

        /// <summary>
        /// An eligible printer to configure. When it cannot tell its model and IT asserted another model for it (--address,
        /// --model, --confirm-model), the assertion replaces the model of its queue's driver; an assertion the printer contradicts
        /// stops the run for IT. Never silently dropped.
        /// </summary>
        private async Task<Selected?> UseAsync(PrinterCandidate eligible)
        {
            if (_flow.Interaction.AssertedModel(eligible, Env.Config.Models) is not { } asserted || SameKey(asserted.Id, eligible.Model!.Id))
                return new Selected(eligible, SelectorFor(eligible));
            if (eligible.Confidence != MatchConfidence.DriverOnly)
                return RefuseAssertion(eligible, asserted.Id);

            _log.Info("flow.model.asserted", "The asserted model replaces the model of the queue's driver.", new Dictionary<string, object?>
            {
                ["target"] = eligible.Key,
                ["driverModel"] = eligible.Model!.Id,
                ["model"] = asserted.Id,
            });
            return await ResolveAssertedAsync(eligible, new TargetSelector(eligible.Key, asserted.Id, ModelAssertedByUser: true));
        }

        /// <summary>
        /// IT's model for the printer at --address is not what the printer reports (its exact or probable identity, or what its
        /// probes name): the address or the model is wrong, so nothing is changed and IT decides (silent: exit 4). The printer
        /// is named by the address IT gave (<paramref name="address"/>, else --address when the printer is at it), with its
        /// queue's host when that is another spelling.
        /// </summary>
        private Selected? RefuseAssertion(PrinterCandidate candidate, string assertedModelId, string? address = null)
        {
            _assertionRefused = true;
            string host = AddressOf(candidate);
            // Given: the refusal covers every queue host of the printer at that address, so none of them is named.
            bool allQueueHosts = address is not null;
            address ??= Env.Options.PreferredAddress is { } given && candidate.IsAtAddress(given) ? given : host;
            string? reported = Session.ReportedModelAgainst(Detection, candidate, assertedModelId)?.Id;
            _log.Warn("flow.model.refused", "The model given for the printer does not fit what the printer reports.", new Dictionary<string, object?>
            {
                ["target"] = candidate.Key,
                ["model"] = assertedModelId,
                ["reported"] = reported,
            });
            var data = new Dictionary<string, string> { ["target"] = candidate.Key, ["address"] = address, ["assertedModel"] = assertedModelId };
            if (reported is not null)
                data["model"] = reported;
            string printer = allQueueHosts || SameKey(address, host) || candidate.NetworkAddress is null ? address : $"{address} (its queue's host {host})";
            _findings.Add(new Finding(FindingCode.NetworkIdentityMismatch, Severity.Error,
                $"The model {assertedModelId} was given for the printer at {printer} (--model with --confirm-model), but the printer reports "
                + $"{reported ?? "another model"}; nothing was changed. Check the address and the model on the printer's label or status page, then run with the correct --address and --model.")
            {
                Data = data,
            });
            return null;
        }

        /// <summary>IT's model when it applies to every one of <paramref name="choices"/> and what each printer reports contradicts it; else null.</summary>
        private ModelDefinition? ContradictedByAll(IReadOnlyList<PrinterCandidate> choices)
        {
            var asserted = choices.Select(c => (Candidate: c, Model: _flow.Interaction.AssertedModel(c, Env.Config.Models))).ToList();
            return asserted.Count > 0 && asserted.All(a => a.Model is { } model && Session.ReportedModelAgainst(Detection, a.Candidate, model.Id) is not null)
                ? asserted[0].Model
                : null;
        }

        /// <summary>The operator's choice among <paramref name="choices"/>; null (selection declined) when nobody chose, as silent runs never do.</summary>
        private async Task<PrinterCandidate?> ChooseOneAsync(IReadOnlyList<PrinterCandidate> choices)
        {
            PrinterCandidate? chosen = await _flow.Interaction.ChooseCandidateAsync(choices, _cancellation);
            // Only one of the printers offered can be configured.
            PrinterCandidate? offered = chosen is null ? null : choices.FirstOrDefault(c => c.Key == chosen.Key);
            if (offered is null)
            {
                _selectionDeclined = true;
                _declinedChoices = [.. choices];
                _log.Info("flow.selection.declined", "None of the printers offered was chosen.", new Dictionary<string, object?>
                {
                    ["choices"] = choices.Select(c => c.Key).ToArray(),
                });
            }
            return offered;
        }

        private static string NoneChosen(int count) => count == 1
            ? "1 supported printer was found and it was not chosen."
            : string.Create(CultureInfo.InvariantCulture, $"{count} supported printers were found and none was chosen.");

        /// <summary>
        /// How IT decides a choice a silent run left open: the queue hosts of the printer at the configured address are told apart
        /// by the host passed with --address; one printer next to queues detection could not match or check, as Diagnostics says;
        /// else the ticket printer is named with the branch file or the switches. Queues the probe limit left unchecked are named.
        /// </summary>
        private string DeclinedAdvice(IReadOnlyList<PrinterCandidate> choices)
        {
            string advice;
            if (choices.Any(c => _reoffered.Contains(c.Key)))
                advice = "Run Setup to set up the connected printer as the ticket printer, or switch the earlier printer on (delete its queue if it is gone) and run the setup again."
                    + ToConfirmBesideUnit(choices);
            else if (PreferredAddress is { } address && choices.Count > 1 && choices.All(c => c.IsAtAddress(address)))
                advice = $"They are queues of the printer at {address} ({QueuesOnHosts(choices)}): run with --address set to the host of the ticket queue, or delete the queue that is not used.";
            else if (choices is [{ IsEligible: true } only] && HasQueuesElsewhere)
                advice = RejectedQueuesAdvice(only);
            else
                advice = ChoiceAdvice(choices);
            return advice + UncheckedQueuesNote();
        }

        private async Task<Selected?> ConfirmAsync(PrinterCandidate candidate)
        {
            TargetSelector selector;
            if (candidate.NeedsConfirmation)
            {
                // IT's model answers the question for the printer at --address; one the printer contradicts stops the run.
                if (_flow.Interaction.AssertedModel(candidate, Env.Config.Models) is { } asserted && !SameKey(asserted.Id, candidate.Model!.Id))
                    return RefuseAssertion(candidate, asserted.Id);
                if (!await _flow.Interaction.ConfirmProbableMatchAsync(candidate, _cancellation))
                {
                    _log.Info("flow.confirm.declined", "The probable model was not confirmed.", new Dictionary<string, object?> { ["target"] = candidate.Key, ["model"] = candidate.Model?.Id });
                    _unconfirmed.Add(candidate);
                    return null;
                }
                selector = new TargetSelector(candidate.Key, candidate.Model!.Id, ModelAssertedByUser: true);
            }
            else if (IsUnidentifiedNetworkPrinter(candidate))
            {
                ModelDefinition? model = await _flow.Interaction.ChooseModelForUnidentifiedPrinterAsync(candidate, Env.Config.Models, _cancellation);
                // Only a model from the approved configuration can be asserted.
                if (Env.Config.FindModel(model?.Id) is not { } known)
                {
                    _log.Info("flow.model.none", "No model was chosen for the unidentified printer.", new Dictionary<string, object?> { ["target"] = candidate.Key });
                    _unconfirmed.Add(candidate);
                    return null;
                }
                selector = new TargetSelector(candidate.Key, known.Id, ModelAssertedByUser: true);
            }
            else
            {
                return null;
            }

            return await ResolveAssertedAsync(candidate, selector);
        }

        /// <summary>The printer with the model the operator (or IT) asserted, when what the printer reports allows it.</summary>
        private async Task<Selected?> ResolveAssertedAsync(PrinterCandidate candidate, TargetSelector selector)
        {
            PrinterCandidate? resolved = await Task.Run(() => Session.Resolve(Detection, selector), _cancellation);
            if (resolved is not { IsEligible: true })
            {
                // IT's model is IT's decision; the operator who chose a model the printer contradicts may choose another printer.
                if (_flow.Interaction.AssertedModel(candidate, Env.Config.Models) is { } asserted && SameKey(asserted.Id, selector.ModelId))
                    return RefuseAssertion(candidate, asserted.Id);
                _log.Warn("flow.confirm.rejected", "The confirmed model does not fit what the printer reports.", new Dictionary<string, object?> { ["target"] = selector.Key, ["model"] = selector.ModelId });
                _findings.Add(new Finding(FindingCode.NetworkIdentityMismatch, Severity.Warning, $"Model {selector.ModelId} could not be used for {selector.Key}.")
                {
                    Data = new Dictionary<string, string> { ["target"] = selector.Key, ["model"] = selector.ModelId ?? "" },
                });
                return null;
            }

            _unconfirmed.RemoveAll(c => SameKey(c.Key, candidate.Key));
            _log.Info("flow.confirm.accepted", "The operator confirmed the printer model.", new Dictionary<string, object?> { ["target"] = selector.Key, ["model"] = selector.ModelId });
            return new Selected(resolved, selector);
        }

        /// <summary>
        /// A printer whose model was not confirmed while a target was chosen. When it stops a silent run it is an error that gives
        /// IT the switches that confirm it; otherwise it records that the printer was not changed (for example the operator's "no").
        /// </summary>
        private Finding UnconfirmedFinding(PrinterCandidate candidate, bool stopsTheRun)
        {
            string address = AddressOf(candidate);
            string? model = candidate.Model?.Id;
            if (stopsTheRun)
            {
                string found = model is not null ? $"The printer at {address} is probably a {model}, but its model was not confirmed"
                    : candidate.PhysicallyAvailable ? $"The printer at {address} answers but does not report its model"
                    : $"The printer at {address} does not answer (it may be switched off) and its model is not known";
                return ModelFinding(candidate, Severity.Error,
                    $"{found}, so nothing was changed. Check the model on the printer's label or status page, then run with {ConfirmSwitches(candidate)}.");
            }

            string detail = _flow.Interaction.IsUnattended
                ? $"The model of the printer at {address} was not confirmed ({ConfirmSwitches(candidate)} confirms it); it was not changed."
                : model is null
                    ? $"No model was chosen for the printer at {address}; it was not changed."
                    : $"The operator did not confirm that the printer at {address} is a {model}; it was not changed.";
            return ModelFinding(candidate, Severity.Info, detail);
        }

        /// <summary>Diagnostics: a probable (or unidentified) printer with nothing eligible next to it; it needs Setup with a confirmed model.</summary>
        private Finding DiagnosticsUnconfirmedFinding(PrinterCandidate candidate)
        {
            string address = AddressOf(candidate);
            string found = candidate.Model?.Id is { } model ? $"The printer at {address} is probably a {model}"
                : candidate.PhysicallyAvailable ? $"The printer at {address} answers but does not report its model"
                : $"The printer at {address} does not answer (it may be switched off) and its model is not known";
            return ModelFinding(candidate, Severity.Error,
                $"{found}; it cannot be checked or set up until its model is confirmed. Run Setup and confirm the model (silent: --mode setup {ConfirmSwitches(candidate)}).");
        }

        /// <summary>The printers one of which is the ticket printer, for a text: each described, or its address and probable model.</summary>
        private static string Listed(IEnumerable<PrinterCandidate> printers) =>
            string.Join("; ", printers.Select(c => c.IsEligible ? Describe(c)
                : $"the printer at {AddressOf(c)} ({(c.Model is { } model ? $"probably a {model.Id}" : c.PhysicallyAvailable ? "its model is not reported" : "it does not answer")}"
                    + $"{(c.Queue is { } queue ? $", queue \"{queue.Name}\"" : "")})"));

        /// <summary>The queues of one printer's queue hosts, for a text: "'A4' on printer12, 'Tickets' on 10.12.0.50".</summary>
        private static string QueuesOnHosts(IEnumerable<PrinterCandidate> hosts) =>
            string.Join(", ", hosts.Select(c => $"'{c.Queue?.Name}' on {AddressOf(c)}"));

        /// <summary>What Diagnostics checked among <paramref name="printers"/>: only a printer of a known model can be checked.</summary>
        private static string Checked(IReadOnlyList<PrinterCandidate> printers) =>
            printers.All(c => c.IsEligible) ? (printers.Count == 1 ? "it was checked." : "each was checked.")
            : printers.Any(c => c.IsEligible) ? "those of a known model were checked."
            : "none can be checked until its model is confirmed.";

        /// <summary>
        /// How IT names the ticket printer among <paramref name="printers"/>: in the window, or with the branch file or switches. A
        /// configured model that more than one of them may be does not tell them apart; one none of them is, is wrong.
        /// </summary>
        private string ChoiceAdvice(IReadOnlyList<PrinterCandidate> printers)
        {
            if (printers.Count > 1 && printers.All(c => c.Connection == ConnectionType.Usb)
                && printers.Select(c => c.Model?.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                return "USB printers of one model cannot be told apart by printerAddress or modelId: choose the ticket printer in Setup, or disconnect the other printer.";
            string? modelId = Session.PreferredModelId;
            bool byAddress = printers.Any(c => c.NetworkAddress is not null);
            string name;
            if (modelId is not null)
            {
                // A queue left unchecked may be of the configured model, so the model picks none (see UncheckedQueuesNote).
                name = printers.Any(c => c.Model is null || SameKey(c.Model.Id, modelId)) || Detection.UncheckedQueues.Count > 0
                    ? byAddress ? $"name it with printerAddress in branch.json or --address (modelId {modelId} is set but does not tell them apart)"
                        : $"disconnect the other printer (modelId {modelId} is set but does not tell them apart)"
                    : $"correct modelId (it names {modelId}, which none of them is)" + (byAddress ? ", or name it with printerAddress in branch.json or --address" : "");
            }
            else
            {
                bool byModel = DecidingModel(printers) is not null;
                name = (byAddress, byModel) switch
                {
                    (true, true) => "name it with printerAddress or modelId in branch.json, or with --address/--model",
                    (true, false) => "name it with printerAddress in branch.json or --address (modelId does not tell them apart)",
                    (false, true) => "name its model with modelId in branch.json or --model",
                    _ => "disconnect the other printer (printerAddress and modelId do not tell them apart)",
                };
            }
            return $"Choose the ticket printer in Setup, or {name}.";
        }

        /// <summary>
        /// A model that modelId (or --model) would pick exactly one of <paramref name="printers"/> by, as selection does without
        /// an address: one eligible printer has it, no printer whose model must be decided may be of it, no queue that could not
        /// be matched to its printer may be of it, and no queue was left unchecked (it may be of any model). Null when none does.
        /// </summary>
        private string? DecidingModel(IReadOnlyList<PrinterCandidate> printers)
        {
            if (Detection.UncheckedQueues.Count > 0)
                return null;
            return printers.Where(c => c.IsEligible)
                .GroupBy(c => c.Model!.Id, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() == 1 && !Session.RejectedQueueMayBe(Detection, g.Key)
                    && !printers.Any(c => NeedsDecision(c) && (c.Model is null || SameKey(c.Model.Id, g.Key))))
                .Select(g => g.Key)
                .FirstOrDefault();
        }

        /// <summary>Diagnostics' sentence about the queues that could not be matched to their printer, next to several printers; empty without any.</summary>
        private string RejectedQueuesNote() => Detection.RejectedQueues.Count == 0 ? ""
            : $" Queue(s) {Quoted(Detection.RejectedQueues)} could not be matched to their printer or cannot be configured "
                + "(see the QueueLeftUnchanged or NetworkIdentityMismatch finding); one of them may be the ticket printer's.";

        /// <summary>
        /// The sentence about the queues whose host the probe limit left out (<see cref="DetectionResult.UncheckedQueues"/>): one of
        /// them may be the ticket printer's, so no printer is taken as it without asking. Empty without any.
        /// </summary>
        private string UncheckedQueuesNote() => Detection.UncheckedQueues.Count == 0 ? ""
            : $" Queue(s) {UncheckedOnHosts()} were not checked because the limit of network probes was reached, and one of them may be the ticket printer's: "
                + "name the host of the ticket queue with --address or printerAddress, or raise connection.maxNetworkProbes in the configuration so that they are "
                + "checked (see the NetworkIdentityUnavailable finding).";

        /// <summary>
        /// The queues already set up as the ticket printer on a Standard TCP/IP port whose printer does not report its model (or
        /// does not answer), with their hosts, for a text: "'Tickets' on 10.0.0.5"; empty without any.
        /// </summary>
        private string UnmatchedTicketQueueHosts() => string.Join(", ", Detection.Findings
            .Where(f => f.Code == FindingCode.QueueLeftUnchanged && f.Data.GetValueOrDefault("reason") == "unmatchedTicketQueue" && f.Data.ContainsKey("address"))
            .Select(f => $"'{f.Data["queue"]}' on {f.Data["address"]}"));

        /// <summary>The queues the probe limit left unchecked with their hosts, for a text: "'Tickets' on printer12.branch.local".</summary>
        private string UncheckedOnHosts() => string.Join(", ", Detection.UncheckedQueues.Select(queue =>
            Detection.Findings.FirstOrDefault(f => f.Code == FindingCode.NetworkIdentityUnavailable && f.Data.GetValueOrDefault("queue") == queue)?.Data.GetValueOrDefault("address")
                is { } host ? $"'{queue}' on {host}" : $"'{queue}'"));

        /// <summary>Queues that may be the ticket printer's although no candidate configures them: rejected or left unchecked.</summary>
        private bool HasQueuesElsewhere => Detection.RejectedQueues.Count > 0 || Detection.UncheckedQueues.Count > 0;

        private static string Quoted(IEnumerable<string> queues) => string.Join(", ", queues.Select(q => $"'{q}'"));

        /// <summary>A clause as a sentence: its first letter in upper case.</summary>
        private static string Sentence(string clause) => clause.Length == 0 ? clause : char.ToUpperInvariant(clause[0]) + clause[1..];

        /// <summary>The switches that confirm the model of each printer among <paramref name="printers"/> whose model must be decided.</summary>
        private string ConfirmAdvice(IEnumerable<PrinterCandidate> printers) =>
            string.Concat(printers.Where(NeedsDecision).Select(c =>
                $" The model of the printer at {AddressOf(c)} must be confirmed before it can be set up (silent: --mode setup {ConfirmSwitches(c)})."));

        /// <summary>
        /// One printer next to queues that may be the ticket printer's although no candidate configures them: queues detection
        /// could not match to their printer or cannot configure (another identity, drivers of different models, another printer
        /// on the port, a port it cannot link, pooled ports, an LPR or Internet Port), or queues the probe limit left unchecked.
        /// It is not taken as the ticket printer; the text says how IT names it (<see cref="RejectedQueuesAdvice"/>).
        /// </summary>
        private string RejectedQueuesDetail(PrinterCandidate only)
        {
            string found = Detection.RejectedQueues.Count > 0
                ? $"The ticket printer is not known: queue(s) {Quoted(Detection.RejectedQueues)} could not be matched to their printer or cannot be configured "
                    + $"(see the QueueLeftUnchanged or NetworkIdentityMismatch finding); {Describe(only)} was checked but is not taken as the ticket printer."
                : $"The ticket printer is not known: {Describe(only)} was checked but is not taken as the ticket printer while other queues were not checked.";
            return $"{found} {RejectedQueuesAdvice(only)}";
        }

        /// <summary>
        /// How IT names <paramref name="only"/> next to rejected or unchecked queues: a network printer by its address (the
        /// host of its queue); a USB printer by its model, unless the rejected queues use the driver of that printer's model (or
        /// of the configured model) or a queue was left unchecked, when the model does not tell them apart.
        /// </summary>
        private string RejectedQueuesAdvice(PrinterCandidate only)
        {
            bool sameModel = Session.RejectedQueueMayBe(Detection, only.Model!.Id)
                || Session.PreferredModelId is { } configured && Session.RejectedQueueMayBe(Detection, configured);
            // Next to queues left unchecked only: its queue's host names that queue, while the ticket queue may be one of those.
            if (only is { NetworkAddress: { } host, Queue: not null } && Detection.RejectedQueues.Count == 0)
                return $"If {QueuesOnHosts([only])} is the ticket queue, name that host with printerAddress {host} in branch.json or --address {host}, or choose it in Setup.";
            // A ticket queue on a Standard TCP/IP port whose printer does not report its model: its host names that printer.
            if (UnmatchedTicketQueueHosts() is { Length: > 0 } ticketQueues)
            {
                string otherwise = only.NetworkAddress is { } other
                    ? $"otherwise name {Describe(only)} with printerAddress {other} or --address {other}, or choose the ticket printer in Setup."
                    : "otherwise choose the ticket printer in Setup.";
                return $"If {ticketQueues} is the ticket queue, name its host with printerAddress in branch.json or --address, and confirm its model with "
                    + $"--model and --confirm-model; {otherwise}";
            }
            return only.NetworkAddress is { } address
                ? $"Name it with printerAddress {address} in branch.json or --address {address}, or choose it in Setup."
                : sameModel
                    ? $"No switch tells them apart, because the printer of those queues may be of the same model: check the printer of {Quoted(Detection.RejectedQueues)} (delete a queue whose printer is gone), or choose the ticket printer in Setup."
                    : Detection.UncheckedQueues.Count > 0
                        ? "Choose the ticket printer in Setup."
                        : $"Name it with modelId {only.Model.Id} in branch.json or --model {only.Model.Id}, or choose it in Setup.";
        }

        private static Finding ModelFinding(PrinterCandidate candidate, Severity severity, string detail)
        {
            var data = new Dictionary<string, string> { ["target"] = candidate.Key, ["address"] = AddressOf(candidate) };
            if (candidate.Model is { } model)
                data["model"] = model.Id;
            return new Finding(FindingCode.ProbableMatchRequiresConfirmation, severity, detail) { Data = data };
        }

        /// <summary>The switches with which IT asserts the model of <paramref name="candidate"/> in a silent run.</summary>
        private string ConfirmSwitches(PrinterCandidate candidate)
        {
            // Repair changes only a printer that has a queue; Setup sets up one without.
            string mode = Mode == SetupMode.Repair && candidate.Queue is null ? "--mode setup " : "";
            return candidate.Model is { } model
                ? $"{mode}--address {AddressOf(candidate)} --model {model.Id} --confirm-model"
                : $"{mode}--address {AddressOf(candidate)} --model <{string.Join("|", Env.Config.Models.Select(m => m.Id))}> --confirm-model";
        }

        /// <summary>
        /// Remembers the USB printers a selection left out: known only from an earlier connection while a USB printer of the same
        /// model is connected, so they are neither offered, changed nor counted. Detection reports each of them as information.
        /// </summary>
        private void RecordSuperseded(IEnumerable<PrinterCandidate> superseded)
        {
            foreach (PrinterCandidate node in superseded)
                _superseded.Add(node.Key);
        }

        /// <param name="addresses">The addresses to probe besides the existing queues' hosts; null for the run's own (configured and entered).</param>
        private async Task<DetectionResult> DetectAsync(IReadOnlyList<string>? addresses = null)
        {
            var request = new DetectRequest { NetworkAddresses = addresses ?? _addresses.ToArray() };
            DetectionResult detection = await Task.Run(() => Session.Detect(request, _cancellation), _cancellation);
            _log.Info("flow.detected", "Detection finished.", new Dictionary<string, object?>
            {
                ["addresses"] = request.NetworkAddresses,
                ["spooler"] = detection.Snapshot.Spooler.State.ToString(),
                ["candidates"] = detection.Candidates.Select(c => $"{c.Key} [{c.Model?.Id ?? "unknown"}, {c.Confidence}]").ToArray(),
                ["findings"] = detection.Findings.Select(f => f.Code.ToString()).ToArray(),
            });
            return detection;
        }

        /// <param name="cancellationToken">The run's token, or none once changes may have been made: then the read-back always completes.</param>
        private async Task<VerificationReport> VerifyAsync(TargetSelector selector, CancellationToken cancellationToken)
        {
            ReportStep(StepId.Verify, StepState.Running, ProgressPhase.Started, "Reading back the printer settings");
            // Always re-read the system in this process; nothing the elevated worker claimed is trusted.
            VerificationReport verification = await Task.Run(() => Session.Verify(selector, cancellationToken), cancellationToken);
            _verifications.Add(verification);
            _log.Info("flow.verified", "Verification finished.", new Dictionary<string, object?>
            {
                ["target"] = selector.Key,
                ["readiness"] = verification.Readiness.ToString(),
                ["failed"] = verification.Checks.Where(c => c.Status == CheckStatus.Fail).Select(c => c.Item.ToString()).ToArray(),
            });

            ReportVerification(verification);
            return verification;
        }

        /// <summary>The Verify step's final state from a read-back.</summary>
        private void ReportVerification(VerificationReport verification)
        {
            Finding? firstProblem = verification.Findings.FirstOrDefault(f => f.Severity != Severity.Info);
            switch (verification.Readiness)
            {
                case ReadinessLevel.Ready:
                    ReportStep(StepId.Verify, StepState.Succeeded, ProgressPhase.Completed, "All settings were verified");
                    break;
                case ReadinessLevel.ReadyWithWarnings:
                    ReportStep(StepId.Verify, StepState.Warning, ProgressPhase.Completed, firstProblem?.Detail ?? "Verified with warnings", firstProblem?.Code);
                    break;
                default:
                    ReportStep(StepId.Verify, StepState.Failed, ProgressPhase.Failed, firstProblem?.Detail ?? "Some settings are not correct", firstProblem?.Code);
                    break;
            }
        }

        private List<Finding> EnvironmentBlockers()
        {
            // Only errors block: a running spooler that is merely set to Disabled, or dimensions allowed by IT, are warnings.
            var blockers = Detection.Findings.Where(f =>
                f.Severity == Severity.Error &&
                f.Code is FindingCode.SpoolerDisabled or FindingCode.SpoolerUnavailable or FindingCode.ProtectedPrintModeEnabled
                    or FindingCode.UnsupportedArchitecture or FindingCode.UnsupportedOperatingSystem or FindingCode.DimensionsUnconfirmed
                    or FindingCode.ConfigurationInvalid).ToList();

            // Nothing can be configured without a running spooler, whether or not detection reported it.
            ServiceRunState spooler = Detection.Snapshot.Spooler.State;
            if (!blockers.Any(IsSpoolerBlocker) && spooler is ServiceRunState.NotInstalled or ServiceRunState.Stopped or ServiceRunState.StopPending or ServiceRunState.Paused)
            {
                blockers.Add(new Finding(FindingCode.SpoolerUnavailable, Severity.Error,
                    _spoolerStartFailure is null ? $"The Print Spooler service is not running ({spooler})." : $"The Print Spooler service is not running ({spooler}): {_spoolerStartFailure}"));
            }

            return blockers;
        }

        private static bool IsSpoolerBlocker(Finding finding) => finding.Code is FindingCode.SpoolerDisabled or FindingCode.SpoolerUnavailable;

        private void ReportDriverCheck(SetupPlan plan)
        {
            if (plan.Blockers.Count > 0)
            {
                ReportStep(StepId.CheckDriver, StepState.Failed, ProgressPhase.Failed, plan.Blockers[0].Detail, plan.Blockers[0].Code);
                return;
            }

            bool diagnostics = Mode == SetupMode.Diagnostics;
            FindingCode? driver = plan.Findings.Select(f => (FindingCode?)f.Code)
                .FirstOrDefault(c => c is FindingCode.DriverReady or FindingCode.DriverMissing or FindingCode.DriverOutdated);
            (StepState state, string detail) = driver switch
            {
                FindingCode.DriverReady => (StepState.Succeeded, "The approved driver is already installed"),
                FindingCode.DriverMissing when diagnostics => (StepState.Failed, "The approved driver is not installed"),
                FindingCode.DriverMissing => (StepState.Succeeded, "The approved driver will be installed"),
                FindingCode.DriverOutdated when diagnostics => (StepState.Warning, "The installed driver is older than the approved one"),
                FindingCode.DriverOutdated => (StepState.Succeeded, "The installed driver will be updated"),
                _ => (StepState.Succeeded, "The driver was checked"),
            };
            ReportStep(StepId.CheckDriver, state, state == StepState.Failed ? ProgressPhase.Failed : ProgressPhase.Completed, detail, driver);
        }

        /// <summary>
        /// Final state of the two apply steps. A step succeeds only when every action expected of it ran (succeeded or was already
        /// satisfied); <paramref name="stop"/> says why the expected actions without a result did not run.
        /// </summary>
        private void ReportApplySteps(IEnumerable<PlannedAction> expected, IReadOnlyList<ApplyResult> results, StepStop? stop = null)
        {
            ActionResult[] all = results.SelectMany(r => r.Results).ToArray();
            ActionKind[] expectedKinds = expected.Select(a => a.Kind).Distinct().ToArray();
            foreach (StepId step in ApplySteps)
            {
                ActionResult[] inStep = all.Where(r => StepOf(r.Action.Kind) == step).ToArray();
                bool complete = expectedKinds.Where(k => StepOf(k) == step)
                    .All(k => inStep.Any(r => r.Action.Kind == k && r.Outcome is ActionOutcome.Succeeded or ActionOutcome.AlreadySatisfied));
                if (inStep.FirstOrDefault(r => r.Outcome == ActionOutcome.Failed) is { } failed)
                    ReportStep(step, StepState.Failed, ProgressPhase.Failed, failed.Detail, failed.FailureCode);
                else if (!complete && stop is not null)
                    ReportStep(step, stop.State, stop.State == StepState.Failed ? ProgressPhase.Failed : ProgressPhase.Skipped, stop.Detail, stop.Code);
                else if (!complete && all.FirstOrDefault(r => r.Outcome == ActionOutcome.Failed) is { } earlier)
                    // Left unfinished by another step's failure: the code says so (the window does not call it "with warnings").
                    ReportStep(step, StepState.Warning, ProgressPhase.Skipped, "Not finished: " + earlier.Detail, earlier.FailureCode ?? FindingCode.OperationFailed);
                else if (!complete)
                    ReportStep(step, StepState.Warning, ProgressPhase.Skipped, inStep.FirstOrDefault(r => r.Outcome == ActionOutcome.Skipped)?.Detail ?? "Not applied");
                else if (inStep.Any(r => r.Outcome == ActionOutcome.Succeeded))
                    ReportStep(step, StepState.Succeeded, ProgressPhase.Completed, step == StepId.InstallDriver ? "The driver and printer are installed" : "The ticket paper and preferences are set");
                else
                    ReportStep(step, StepState.Skipped, ProgressPhase.Skipped, "Already correct");
            }
        }

        private FlowResult Finish(FlowOutcome outcome, PrinterCandidate? target, VerificationReport? verification)
        {
            try
            {
                (StepState state, ProgressPhase phase) = outcome switch
                {
                    FlowOutcome.Ready => (StepState.Succeeded, ProgressPhase.Completed),
                    FlowOutcome.ReadyWithWarnings => (StepState.Warning, ProgressPhase.Completed),
                    FlowOutcome.Cancelled => (StepState.Skipped, ProgressPhase.Skipped),
                    _ => (StepState.Failed, ProgressPhase.Failed),
                };
                ReportStep(StepId.Ready, state, phase, outcome.ToString());

                DiagnosticReport? report = CreateReport();
                Finding[] findings = AllFindings();
                int exitCode = SetupFlowExitCodes.For(outcome, findings, _selectionDeclined || _confirmationRequired || _assertionRefused);
                bool rebootRequired = _applyResults.Any(r => r.RebootRequired);
                _log.Info("flow.result", "Run finished: " + outcome + ".", new Dictionary<string, object?>
                {
                    ["outcome"] = outcome.ToString(),
                    ["exitCode"] = exitCode,
                    ["target"] = target?.Key,
                    ["rebootRequired"] = rebootRequired,
                    ["findings"] = findings.Where(f => f.Severity != Severity.Info).Select(f => f.Code.ToString()).ToArray(),
                });

                return new FlowResult(outcome)
                {
                    Verification = verification,
                    Verifications = _verifications.ToArray(),
                    Target = target,
                    Report = report,
                    Findings = findings,
                    CurrentFindings = CurrentFindingsFor(outcome, findings),
                    RebootRequired = rebootRequired,
                    ExitCode = exitCode,
                };
            }
            catch (Exception ex)
            {
                _log.Error("flow.finish.failed", "The run result could not be assembled.", ex);
                return new FlowResult(FlowOutcome.Failed) { Findings = [FailureFinding(ex)], ExitCode = ExitCodes.InternalError };
            }
        }

        private DiagnosticReport? CreateReport()
        {
            if (_session is null)
                return null;
            try
            {
                DetectionResult? detection = _detection is null ? null : _detection with { Findings = DetectionFindings().ToArray() };
                DiagnosticReport report = _session.CreateReport(detection, _plans, _applyResults, _verifications, _findings);
                return _log.FilePath is { } logFile ? report with { LogFile = logFile } : report;
            }
            catch (Exception ex)
            {
                _log.Error("flow.report.failed", "The diagnostic report could not be created.", ex);
                return null;
            }
        }

        /// <summary>
        /// Detection's findings without its note that a USB unit offered again is left out, and without its note that a queue this
        /// run chose among the printer's queues is "already set up as the ticket printer" (<see cref="KeepQueueChoice"/>).
        /// </summary>
        private IEnumerable<Finding> DetectionFindings() =>
            (_detection?.Findings ?? []).Where(f => !(f is { Code: FindingCode.DeviceNotConnected, Severity: Severity.Info }
                && f.Data.TryGetValue("target", out string? key) && _reoffered.Contains(key))
                && !(f is { Code: FindingCode.DuplicateQueues, Severity: Severity.Info }
                    && f.Data.TryGetValue("candidate", out string? guessed) && _queueGuessed.Contains(guessed)));

        /// <summary>
        /// The findings that describe the end state (<see cref="FlowResult.CurrentFindings"/>). Once setup actions ran, the plans'
        /// findings are left out, as they describe the state before the changes (starting the Print Spooler is not a setup
        /// action). In Diagnostics only the ticket printer's plan and read-back count when it is known, none when it could not be
        /// checked (<see cref="_diagnosedTicket"/>): the other printers are checked for the report only.
        /// </summary>
        private Finding[] CurrentFindingsFor(FlowOutcome outcome, Finding[] all)
        {
            if (Mode == SetupMode.Diagnostics)
                return _diagnosedTicket is { } ticket ? AllFindings(onlyTarget: ticket) : all;
            bool actionsRan = _applyResults.Any(r => r.Results.Any(a => a.Action.Kind != ActionKind.StartSpooler));
            return actionsRan ? AllFindings(withPlans: false, lastBlockers: outcome == FlowOutcome.Blocked) : all;
        }

        /// <param name="withPlans">False once actions ran: the plans' findings then describe the state before the changes.</param>
        /// <param name="lastBlockers">Without the plans' findings: still the latest plan's blockers, which ended the run and still hold.</param>
        /// <param name="onlyTarget">The key of the one printer whose plans and read-backs count (Diagnostics' ticket printer); null for all.</param>
        private Finding[] AllFindings(bool withPlans = true, bool lastBlockers = false, string? onlyTarget = null)
        {
            var plans = _plans.Where(p => onlyTarget is null || SameKey(p.Target.Key, onlyTarget)).ToList();
            return DetectionFindings()
                .Concat(withPlans ? plans.SelectMany(p => p.Blockers.Concat(p.Findings)) : lastBlockers && plans.Count > 0 ? plans[^1].Blockers : [])
                .Concat(_applyResults.SelectMany(r => r.Findings))
                .Concat(_verifications.Where(v => onlyTarget is null || SameKey(v.TargetKey, onlyTarget)).SelectMany(v => v.Findings))
                .Concat(_findings)
                .DistinctBy(f => (f.Code, f.Severity, f.Detail))
                .OrderByDescending(f => f.Severity)
                .ToArray();
        }

        private Finding? AddressProblem(string? address)
        {
            if (address is null)
                return null;
            return Detection.Findings.FirstOrDefault(f =>
                f.Code is FindingCode.NetworkPrinterUnreachable or FindingCode.NetworkIdentityUnavailable
                    or FindingCode.NetworkIdentityMismatch or FindingCode.UnsupportedPrinterDetected or FindingCode.QueueLeftUnchanged &&
                Names(f, address));
        }

        /// <summary>
        /// Detection's warning that a queue printing to the printer at <paramref name="address"/> is left unchanged (pooled, on a
        /// port the setup cannot link, or next to queues with drivers of different models), so that printer is not set up
        /// automatically; null when there is none.
        /// </summary>
        private Finding? LeftUnchangedAt(string address) =>
            Detection.Findings.FirstOrDefault(f => f is { Code: FindingCode.QueueLeftUnchanged, Severity: not Severity.Info } && Names(f, address));

        /// <summary>
        /// Why the printer at the configured address is not used, then <paramref name="rest"/> (a clause ending in a period): a
        /// queue that prints to it is left unchanged (<see cref="LeftUnchangedAt"/>), else no supported printer is there.
        /// </summary>
        private string NotUsableAt(string address, string? rest = null) => LeftUnchangedAt(address) is { } leftUnchanged
            ? $"The printer at {address}, the configured printer address, is not set up automatically: {leftUnchanged.Detail}"
                + (rest is null ? "" : " " + char.ToUpperInvariant(rest[0]) + rest[1..])
            : $"No supported TA Triumph-Adler printer is at {address}, the configured printer address" + (rest is null ? "." : "; " + rest);

        /// <summary>Whether <paramref name="finding"/> is about <paramref name="address"/>: its data names it, or its text names it as a whole word.</summary>
        private static bool Names(Finding finding, string address)
        {
            // The address as a whole word: 10.1.2.5 is not 10.1.2.50 (a sentence's final period may follow it).
            var named = new Regex(@"(?<![\w.:-])" + Regex.Escape(address) + @"(?![\w:-]|\.\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return finding.Data.Values.Any(v => string.Equals(v, address, StringComparison.OrdinalIgnoreCase)) || named.IsMatch(finding.Detail);
        }

        private void AddNoPrinterFinding(string detail, IReadOnlyDictionary<string, string>? data = null)
        {
            if (Detection.Findings.All(f => f.Code != FindingCode.NoSupportedPrinterFound))
                _findings.Add(new Finding(FindingCode.NoSupportedPrinterFound, Severity.Error, detail) { Data = data ?? new Dictionary<string, string>() });
        }

        /// <summary>
        /// Data of a NoSupportedPrinterFound error about the configured address: the address, and the queue that is left unchanged
        /// there (<see cref="LeftUnchangedAt"/>; "leftUnchanged"), which IT must change before the printer can be set up.
        /// </summary>
        private Dictionary<string, string> NotUsableData(string address)
        {
            var data = new Dictionary<string, string> { ["address"] = address };
            if (LeftUnchangedAt(address)?.Data.GetValueOrDefault("queue") is { Length: > 0 } queue)
                data["leftUnchanged"] = queue;
            return data;
        }

        private void AddAddress(string? address)
        {
            if (!string.IsNullOrWhiteSpace(address) && !_addresses.Contains(address.Trim(), StringComparer.OrdinalIgnoreCase))
                _addresses.Add(address.Trim());
        }

        private void LogPlan(SetupPlan plan) =>
            _log.Info("flow.plan", "Assessment finished.", new Dictionary<string, object?>
            {
                ["target"] = plan.Target.Key,
                ["actions"] = plan.Actions.Select(a => a.Kind.ToString()).ToArray(),
                ["requiresElevation"] = plan.RequiresElevation,
                ["blockers"] = plan.Blockers.Select(b => b.Code.ToString()).ToArray(),
            });

        private void ReportStep(StepId step, StepState state, ProgressPhase phase, string detail, FindingCode? code = null) =>
            ReportUpdate(new FlowUpdate(FlowEventKind.Step, new ProgressUpdate(step, state, null, phase, detail) { Code = code }));

        private void ReportUpdate(FlowUpdate update)
        {
            try
            {
                _progress.Report(update);
            }
            catch (Exception ex)
            {
                // A broken progress display must not break the run itself.
                _log.Warn("flow.progress.failed", "A progress update could not be delivered.", exception: ex);
            }
        }

        private static Finding DeclinedFinding(ElevatedRunResult run, string needed) =>
            new(FindingCode.ElevationDeclined, Severity.Error,
                $"{run.Error ?? "Administrator approval was declined."} Nothing was changed. Needed to: {needed}.");

        private static Finding FailureFinding(Exception ex)
        {
            var data = new Dictionary<string, string> { ["exception"] = ex.GetType().FullName ?? ex.GetType().Name };
            if (ex is PrinterSetupException setup)
            {
                data["code"] = setup.Code.ToString();
                if (setup.Win32Error is int win32Error)
                    data["win32Error"] = win32Error.ToString(CultureInfo.InvariantCulture);
            }
            return new Finding(FindingCode.OperationFailed, Severity.Error, ex.Message) { Data = data };
        }

        /// <summary>Nothing was applied: the result says it was refused, or has no action result and comes with a failure exit code.</summary>
        private static bool IsRefusal(ApplyResult result, int exitCode) =>
            result.Refused || (result.Results.Count == 0 && exitCode is not (ExitCodes.Ready or ExitCodes.ReadyWithWarnings));

        /// <summary>
        /// The actions that should have a result: the machine steps the elevated side ran from its own plan (it may have had less
        /// to do than this process planned, as when Windows created the USB queue during the approval, or other work, as when it
        /// found the printer under another spelling), and those that the fresh plan after them still needs (without that plan,
        /// those this process sent); without the elevated side's result, the machine steps this process sent. Then the per-user
        /// steps of the latest plan.
        /// </summary>
        private static IEnumerable<PlannedAction> ExpectedActions(SetupPlan plan, SetupPlan? userPlan, ApplyResult? machine = null)
        {
            var stillNeeded = (userPlan ?? plan).Actions.Where(a => a.RequiresElevation);
            var machineActions = machine is null ? plan.Actions.Where(a => a.RequiresElevation) : machine.Results.Select(r => r.Action).Concat(stillNeeded);
            return machineActions.Concat((userPlan ?? plan).Actions.Where(a => !a.RequiresElevation));
        }

        private static TargetSelector SelectorFor(PrinterCandidate candidate) =>
            TargetSelector.For(candidate, candidate.Confidence == MatchConfidence.UserAsserted);

        /// <summary>A candidate Repair may change: eligible, with a local queue (connections to a print server are managed there).</summary>
        private static bool IsRepairable(PrinterCandidate candidate) => candidate is { IsEligible: true, Queue.IsServerConnection: false };

        /// <summary>As <see cref="SetupSession"/> judges it: answering at a requested address, or a queue host whose queue may be the ticket queue.</summary>
        private static bool IsUnidentifiedNetworkPrinter(PrinterCandidate candidate) =>
            candidate is { Model: null, Kind: TargetKind.NetworkAddress } && (candidate.PhysicallyAvailable || candidate.Queue is not null);

        /// <summary>Its model must be decided before it can be set up: identified only probably, or answering without naming its model.</summary>
        private static bool NeedsDecision(PrinterCandidate candidate) => candidate.NeedsConfirmation || IsUnidentifiedNetworkPrinter(candidate);

        private static string AddressOf(PrinterCandidate candidate) => candidate.NetworkAddress ?? candidate.NetworkIdentity?.Address ?? candidate.Key;

        private static bool SameKey(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static FlowOutcome OutcomeOf(ReadinessLevel readiness) => readiness switch
        {
            ReadinessLevel.Ready => FlowOutcome.Ready,
            ReadinessLevel.ReadyWithWarnings => FlowOutcome.ReadyWithWarnings,
            _ => FlowOutcome.NotReady,
        };

        private static StepId StepOf(ActionKind kind) => kind switch
        {
            ActionKind.StartSpooler => StepId.Detect,
            ActionKind.PrepareDriverPackage or ActionKind.InstallDriver or ActionKind.BindUsbDevice or ActionKind.CreateTcpIpPort
                or ActionKind.CreateQueue or ActionKind.ChangeQueueDriver or ActionKind.ChangeQueuePort => StepId.InstallDriver,
            _ => StepId.ConfigureTicket,
        };

        private static string Describe(PrinterCandidate candidate)
        {
            string model = candidate.Model?.DisplayName is { Length: > 0 } name ? name : candidate.Model?.Id ?? "Printer";
            string connection = candidate.NetworkAddress is { } address ? $"{candidate.Connection} {address}" : candidate.Connection.ToString();
            return candidate.Queue is { } queue ? $"{model} ({connection}, queue \"{queue.Name}\")" : $"{model} ({connection})";
        }

        private sealed record Selected(PrinterCandidate Target, TargetSelector Selector);

        private sealed record TargetRun(FlowOutcome Outcome, PrinterCandidate Target, VerificationReport? Verification);

        private sealed record TicketPrinter(PrinterCandidate? Primary, IReadOnlyList<PrinterCandidate> Unconfirmed, IReadOnlyList<PrinterCandidate> Superseded)
        {
            /// <summary>The configured address when nothing usable is there: no other printer is the ticket printer.</summary>
            public string? UnusableAddress { get; init; }

            /// <summary>Without a known ticket printer: the printers one of which is it; their checks decide.</summary>
            public IReadOnlyList<PrinterCandidate> Undecided { get; init; } = [];

            /// <summary>The configured address whose printer has several queues, none known to be the ticket queue.</summary>
            public string? Address { get; init; }

            /// <summary>Not connected USB units offered again because only their queue uses the ticket paper.</summary>
            public IReadOnlyList<PrinterCandidate> Reoffered { get; init; } = [];
        }

        /// <summary>
        /// Applied: the actions ran (some may have failed). NotStarted: nothing ran (approval declined, helper not started).
        /// Refused: the elevated side changed nothing and said why. Unknown: the helper started but gave no result. Stopped: the
        /// run was cancelled; the step that was running finished and later ones were not started.
        /// </summary>
        private enum MachineEnd { Applied, NotStarted, Refused, Unknown, Stopped }

        /// <summary>How the machine steps ended; Outcome is the run's outcome when the end decides it, Reason why they did not complete.</summary>
        private sealed record MachineApply(MachineEnd End, ApplyResult? Result, FlowOutcome? Outcome = null, Finding? Reason = null);

        /// <summary>What an apply step shows when its expected actions did not run.</summary>
        private sealed record StepStop(StepState State, FindingCode Code, string Detail);

        private sealed class EngineProgress(FlowRun run) : IProgress<ProgressUpdate>
        {
            public void Report(ProgressUpdate value) => run.ReportUpdate(new FlowUpdate(FlowEventKind.Step, value));
        }
    }
}
