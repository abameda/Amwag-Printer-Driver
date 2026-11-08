using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Detection;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Logging;

namespace Amwag.PrinterSetup.Core.Setup;

/// <summary>
/// One run of detection, assessment, apply and verification against the services it is given.
/// All public methods are synchronous and safe to call from a background thread.
/// </summary>
public sealed class SetupSession
{
    private readonly ModelMatcher _matcher;

    public SetupSession(DeploymentConfig config, BranchOverride? branch, SetupOptions options, SetupServices services)
    {
        Config = config;
        Branch = branch;
        Options = options;
        Services = services;
        _matcher = new ModelMatcher(config.Models);
    }

    public DeploymentConfig Config { get; }
    public BranchOverride? Branch { get; }
    public SetupOptions Options { get; }
    public SetupServices Services { get; }

    private ILog Log => Services.Log;

    /// <summary>Captures a read-only snapshot and identifies supported printers. Never modifies the system.</summary>
    public DetectionResult Detect(DetectRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reader = new SnapshotReader(Services);
        var snapshot = reader.Capture();
        Log.Info("detect.snapshot", "Captured the printing configuration.", new Dictionary<string, object?>
        {
            ["spooler"] = snapshot.Spooler.State.ToString(),
            ["devices"] = snapshot.Devices.Count,
            ["queues"] = snapshot.Queues.Count,
            ["ports"] = snapshot.Ports.Count,
            ["drivers"] = snapshot.Drivers.Count,
            ["forms"] = snapshot.Forms.Count,
            ["captureErrors"] = snapshot.CaptureErrors.ToArray(),
        });

        var requested = DistinctAddresses(request.NetworkAddresses.Append(Options.PreferredAddress).Append(Branch?.PrinterAddress));
        var existing = request.ProbeExistingNetworkQueues && Config.Connection.IdentifyExistingNetworkQueues
            ? ExistingQueueHosts(snapshot)
            : [];
        var addresses = DistinctAddresses(requested.Concat(existing));
        int limit = Math.Max(Config.Connection.MaxNetworkProbes, 0);
        var dropped = addresses.Skip(limit).ToList();
        if (dropped.Count > 0)
        {
            Log.Warn("detect.probe.capped", $"Only {limit} network probes are allowed; {dropped.Count} address(es) were not probed.",
                new Dictionary<string, object?> { ["dropped"] = dropped.ToArray() });
        }

        snapshot = snapshot with { NetworkIdentities = Probe(addresses.Take(limit), cancellationToken) };

        var assembler = new CandidateAssembler(_matcher, snapshot, Config.Connection.RawPortNumber, TicketQueue);
        // Every address the limit left out: a queue whose host was not probed may still be the ticket printer's.
        var (candidates, detectionFindings) = assembler.Assemble(requested, dropped);
        var findings = EnvironmentChecks.Evaluate(snapshot.Platform, snapshot.Spooler, reader.SpoolerUnavailable, Config, Options)
            .Concat(detectionFindings)
            .ToList();

        Log.Info("detect.result", $"{candidates.Count} candidate(s).", new Dictionary<string, object?>
        {
            ["candidates"] = candidates.Select(c => $"{c.Key} {c.Model?.Id ?? "unidentified"} {c.Confidence}").ToArray(),
            ["rejectedQueues"] = assembler.RejectedQueues.ToArray(),
            ["uncheckedQueues"] = assembler.UncheckedQueues.ToArray(),
            ["findings"] = findings.Select(f => $"{f.Severity}:{f.Code}").ToArray(),
        });
        return new DetectionResult
        {
            Snapshot = snapshot,
            Candidates = candidates,
            Findings = Findings.Distinct(findings),
            RejectedQueues = [.. assembler.RejectedQueues],
            UncheckedQueues = [.. assembler.UncheckedQueues],
        };
    }

    /// <summary>
    /// Chooses the ticket printer when the choice is unambiguous; otherwise says what the operator must decide (silent runs stop).
    /// With a configured address (<paramref name="operatorAddress"/>, the address an operator entered in this run, else --address,
    /// else the branch file's printerAddress) only the printer at that address is chosen: one eligible printer there is selected
    /// (PreferredByConfiguration), several are the operator's choice unless the configured model picks one, as is one next to
    /// another queue of it whose model must be decided, or next to a queue the probe limit left unchecked when the address is
    /// not the host of its queue, one whose model is not established is asked about
    /// (NeedsConfirmation), and when nothing usable is there (nothing answers, or the device there
    /// is not a supported printer) no other printer is chosen in its place (NoCandidates). Without an address: the configured
    /// model's one printer when no other printer may be of that model, else the one eligible printer when no other printer
    /// needs a decision and its model is not another than the configured one; several printers are the operator's choice
    /// (MultipleCandidates); printers that all need their model confirmed or named are asked about (NeedsConfirmation; when
    /// there are several, the operator first chooses one). Preferred and connected ones come first. USB printers that are not
    /// connected are left out while a USB printer of the same model is (<see cref="CandidateSelection.Superseded"/>).
    /// </summary>
    public CandidateSelection SelectAutomatically(DetectionResult detection, string? operatorAddress = null)
    {
        var (candidates, superseded) = WithoutSuperseded(detection.Candidates);
        string? address = operatorAddress ?? PreferredAddress;
        CandidateSelection selection = (address is null ? WithoutAddress(candidates, detection) : AtAddress(candidates, address, detection)) with
        {
            Superseded = superseded,
            Address = address,
        };

        Log.Info("select.result", $"Selection: {selection.Reason}.", new Dictionary<string, object?>
        {
            ["reason"] = selection.Reason.ToString(),
            ["address"] = address,
            ["selected"] = selection.Selected?.Key,
            ["choices"] = selection.Choices.Select(c => c.Key).ToArray(),
            ["superseded"] = superseded.Select(c => c.Key).ToArray(),
        });
        return selection;
    }

    /// <summary>
    /// Repair's single target, which Repair may change only when it has a local queue (connections to a print server are managed
    /// there). <paramref name="explicitTarget"/> (the printer just set up or tested) decides when given: its candidate, or nothing
    /// when it is gone or has no local queue. Otherwise the ticket printer as <see cref="SelectAutomatically"/> chooses it: that
    /// printer when it has a repairable queue, else nothing (<see cref="CandidateSelection.NeedsSetup"/>: Setup sets it up); a
    /// printer whose model must be confirmed stays one (its queue is checked once confirmed); among several printers the only
    /// one with a local queue that already uses the ticket form as its machine-wide default (set up by this tool before; one
    /// queue pooled over several printers counts once), also among several whose model must be confirmed, else those with a
    /// local queue are the operator's choice (silent runs stop), including printers whose model must be confirmed first.
    /// A not connected USB unit that the choice left out is offered again when only its queue uses the ticket form and the
    /// choice is not already a printer on the ticket form: it may be the switched-off ticket printer next to an identical office
    /// printer (or next to the connected unit that has no queue yet). So Repair never rewrites a printer nobody chose. Reads
    /// queue defaults only.
    /// </summary>
    public CandidateSelection SelectRepairTarget(DetectionResult detection, TargetSelector? explicitTarget)
    {
        CandidateSelection selection;
        if (explicitTarget is not null)
        {
            PrinterCandidate? chosen = Resolve(detection, explicitTarget);
            // The explicit target is not a decision among candidates, so none was left out of one.
            selection = chosen is not null && IsRepairable(chosen)
                ? new CandidateSelection(chosen, SelectionReason.PreferredByConfiguration, [chosen])
                : new CandidateSelection(null, SelectionReason.NoCandidates, []);
        }
        else
        {
            CandidateSelection setup = SelectAutomatically(detection);
            // A queue no candidate configures that is on the ticket form too: the ticket form then tells no queue apart.
            bool formElsewhere = detection.RejectedQueues.Concat(detection.UncheckedQueues).Any(DefaultFormIsTicket);
            selection = RepairChoice(setup, formElsewhere) with { Superseded = setup.Superseded, Address = setup.Address };

            // Not with a configured address: the printer there is the ticket printer, and a USB unit never is.
            var ticketUnits = setup.Address is null ? setup.Superseded.Where(n => IsRepairable(n) && UsesTicketForm(n)).ToList() : [];
            bool undecided = selection.Selected is not { } chosen || !UsesTicketForm(chosen);
            if (ticketUnits.Count > 0 && undecided)
            {
                List<PrinterCandidate> others = selection.Selected is { } connected ? [connected] : [.. selection.Choices];
                selection = selection with
                {
                    Selected = null,
                    Reason = SelectionReason.MultipleCandidates,
                    Choices = [.. others, .. ticketUnits],
                    Superseded = [.. setup.Superseded.Except(ticketUnits)],
                };
            }
        }

        Log.Info("select.repair", $"Repair selection: {selection.Reason}.", new Dictionary<string, object?>
        {
            ["reason"] = selection.Reason.ToString(),
            ["explicit"] = explicitTarget?.Key,
            ["selected"] = selection.Selected?.Key,
            ["choices"] = selection.Choices.Select(c => c.Key).ToArray(),
            ["needsSetup"] = selection.NeedsSetup?.Key,
            ["superseded"] = selection.Superseded.Select(c => c.Key).ToArray(),
        });
        return selection;
    }

    /// <summary>
    /// What Repair does with Setup's decision (see <see cref="SelectRepairTarget"/>). <paramref name="formElsewhere"/>: a queue no
    /// candidate configures (rejected or left unchecked) is on the ticket form as well, so the ticket form decides nothing.
    /// </summary>
    private CandidateSelection RepairChoice(CandidateSelection setup, bool formElsewhere)
    {
        switch (setup.Reason)
        {
            case SelectionReason.PreferredByConfiguration or SelectionReason.SingleCandidate when setup.Selected is { } ticket:
                return IsRepairable(ticket) ? setup : new CandidateSelection(null, SelectionReason.NoCandidates, []) { NeedsSetup = ticket };
            case SelectionReason.NeedsConfirmation:
                // Of several printers whose model must be confirmed, the one whose queue already has the ticket paper is asked about.
                return setup.Choices.Count > 1 && !formElsewhere && OnTicketForm(setup.Choices.Where(c => c.Queue is { IsServerConnection: false })) is { } onForm
                    ? new CandidateSelection(null, SelectionReason.NeedsConfirmation, [onForm])
                    : setup;
            case SelectionReason.MultipleCandidates:
                // A printer whose model must be decided may be the ticket printer too: once confirmed, its queue is repaired.
                var withQueue = setup.Choices.Where(c => IsRepairable(c) || NeedsDecision(c) && c.Queue is { IsServerConnection: false }).ToList();
                if (!formElsewhere && OnTicketForm(withQueue) is { } configured)
                {
                    return IsRepairable(configured)
                        ? new CandidateSelection(configured, SelectionReason.SingleCandidate, [configured])
                        : new CandidateSelection(null, SelectionReason.NeedsConfirmation, [configured]);
                }
                return withQueue.Count == 0 ? new CandidateSelection(null, SelectionReason.NoCandidates, [])
                    : withQueue.All(NeedsDecision) ? new CandidateSelection(null, SelectionReason.NeedsConfirmation, withQueue)
                    : new CandidateSelection(null, SelectionReason.MultipleCandidates, withQueue);
            default:
                return new CandidateSelection(null, SelectionReason.NoCandidates, []);
        }
    }

    /// <summary>
    /// The one printer among <paramref name="candidates"/> whose queue already uses the ticket form as its machine-wide default,
    /// or null when none or several queues do. Printers that share that queue (one queue pooled over several USB printers)
    /// count once: the connected one is taken.
    /// </summary>
    private PrinterCandidate? OnTicketForm(IEnumerable<PrinterCandidate> candidates)
    {
        var onForm = candidates.Where(UsesTicketForm).ToList();
        return onForm.Count > 0 && onForm.Select(c => c.Queue!.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
            ? onForm.OrderByDescending(c => c.PhysicallyAvailable).First()
            : null;
    }

    /// <summary>
    /// Finds the selector's candidate in a fresh detection and re-applies the identity rules; null when it is gone or no longer
    /// valid. A network selector also finds the one candidate at its address when no candidate has its key: another run may key
    /// the same printer by another of its spellings (a host name and its IP address merge once both are known). An asserted
    /// model replaces the model of a queue's driver (DriverOnly: the device does not tell its model) unless the device's own
    /// probes, under any of its spellings, name another model.
    /// </summary>
    public PrinterCandidate? Resolve(DetectionResult detection, TargetSelector selector)
    {
        var candidate = detection.Candidates.FirstOrDefault(c => Same(c.Key, selector.Key));
        if (candidate is null && selector.NetworkAddress is { } address && detection.Candidates.Where(c => c.IsAtAddress(address)).ToList() is [var atAddress])
        {
            candidate = atAddress;
            Log.Info("resolve.alias", $"{selector.Key} is now keyed {atAddress.Key}.", new Dictionary<string, object?> { ["target"] = selector.Key, ["candidate"] = atAddress.Key });
        }
        PrinterCandidate? resolved = null;
        string outcome;

        if (candidate is null)
        {
            outcome = "not found";
        }
        else if (candidate.IsEligible && (selector.ModelId is null || Same(selector.ModelId, candidate.Model!.Id)))
        {
            resolved = candidate;
            outcome = "found";
        }
        else if (candidate.IsEligible && !(selector.ModelAssertedByUser && candidate.Confidence == MatchConfidence.DriverOnly))
        {
            outcome = $"now identified as {candidate.Model!.Id}, not {selector.ModelId}";
        }
        else if (!selector.ModelAssertedByUser || Config.FindModel(selector.ModelId) is not { } asserted)
        {
            outcome = "model not confirmed";
        }
        else if (candidate is { Confidence: MatchConfidence.Probable, Model: { } probable } && !Same(probable.Id, asserted.Id))
        {
            outcome = $"probably {probable.Id}, not the asserted {asserted.Id}";
        }
        else if (ContradictingModel(detection, candidate, asserted) is { } contradicting)
        {
            outcome = $"the device reports {contradicting.Id}, not the asserted {asserted.Id}";
        }
        else
        {
            // Over a queue's driver (a replaced printer that does not report its model) the assertion wins, and the queue's
            // driver is then repaired as for any identity conflict.
            bool overDriver = candidate.Confidence == MatchConfidence.DriverOnly;
            resolved = candidate with
            {
                Model = asserted,
                Confidence = MatchConfidence.UserAsserted,
                IdentityConflict = candidate.IdentityConflict || overDriver,
                Evidence = [.. candidate.Evidence, overDriver ? $"Model asserted by operator over the queue's driver for {candidate.Model!.Id}" : "Model asserted by operator"],
            };
            outcome = overDriver ? $"model asserted by operator over the driver's {candidate.Model!.Id}" : "model asserted by operator";
        }

        Log.Info("resolve.result", $"{selector.Key}: {outcome}.", new Dictionary<string, object?>
        {
            ["target"] = selector.Key,
            ["modelId"] = selector.ModelId,
            ["asserted"] = selector.ModelAssertedByUser,
            ["resolved"] = resolved is not null,
        });
        return resolved;
    }

    /// <summary>
    /// The model the printer itself reports against an asserted one (<paramref name="assertedModelId"/>): its exact or probable
    /// identity, else a supported model its probes name under any of its spellings; null when nothing it reports names another
    /// model. Says why <see cref="Resolve"/> refuses an assertion.
    /// </summary>
    public ModelDefinition? ReportedModelAgainst(DetectionResult detection, PrinterCandidate candidate, string assertedModelId)
    {
        if (candidate is { Confidence: MatchConfidence.Exact or MatchConfidence.Probable, Model: { } own } && !Same(own.Id, assertedModelId))
            return own;
        return Config.FindModel(assertedModelId) is { } asserted ? ContradictingModel(detection, candidate, asserted) : null;
    }

    /// <summary>
    /// A supported model other than <paramref name="asserted"/> that the printer's own probe names, under any of its spellings
    /// (also another queue host of it that leads to the same IP address).
    /// </summary>
    private ModelDefinition? ContradictingModel(DetectionResult detection, PrinterCandidate candidate, ModelDefinition asserted)
    {
        string? resolved = candidate.NetworkIdentity?.ResolvedAddress;
        return detection.Snapshot.NetworkIdentities
            .Where(i => candidate.IsAtAddress(i.Address) || resolved is not null && Same(i.ResolvedAddress, resolved))
            .Concat(candidate.NetworkIdentity is { } own ? [own] : [])
            .SelectMany(_matcher.ModelsSuggestedBy)
            .FirstOrDefault(m => !Same(m.Id, asserted.Id));
    }

    /// <summary>Read-only assessment of the target and the ordered actions needed (none in Diagnostics mode).</summary>
    public SetupPlan Assess(PrinterCandidate target, CancellationToken cancellationToken) =>
        new SetupPlanner(Config, Branch, Options, Services, _matcher).Assess(target, cancellationToken);

    /// <summary>Executes the plan's actions in <paramref name="scope"/>. Each action re-checks its precondition (idempotent) and verifies its result.</summary>
    public ApplyResult Apply(SetupPlan plan, ApplyScope scope, IProgress<ProgressUpdate>? progress, CancellationToken cancellationToken) =>
        new ActionRunner(Config, Branch, Options, Services, plan, progress, cancellationToken).Run(scope);

    /// <summary>Re-detects and verifies the target's final state from fresh reads. Never modifies the system.</summary>
    public VerificationReport Verify(TargetSelector selector, CancellationToken cancellationToken) =>
        new SetupVerifier(this).Verify(selector, cancellationToken);

    public DiagnosticReport CreateReport(
        DetectionResult? detection,
        IEnumerable<SetupPlan> plans,
        IEnumerable<ApplyResult> applyResults,
        IEnumerable<VerificationReport> verifications,
        IEnumerable<Finding> extraFindings)
    {
        var planList = plans.ToList();
        var applyList = applyResults.ToList();
        var verificationList = verifications.ToList();
        var findings = (detection?.Findings ?? [])
            .Concat(planList.SelectMany(p => p.Findings))
            .Concat(applyList.SelectMany(a => a.Findings))
            .Concat(verificationList.SelectMany(v => v.Findings))
            .Concat(extraFindings);

        var report = new DiagnosticReport
        {
            GeneratedAt = Services.Clock.Now,
            AppVersion = Options.AppVersion,
            SessionId = Log.SessionId,
            Mode = Options.Mode,
            ConfigSource = Options.ConfigSource,
            ConfigVersion = Config.ConfigVersion,
            TicketDescription = TicketDefaults.Describe(Config.Ticket),
            TicketDimensionsConfirmed = Config.Ticket.DimensionsConfirmed,
            Snapshot = detection?.Snapshot,
            Candidates = detection?.Candidates ?? [],
            Plans = planList,
            ApplyResults = applyList,
            Verifications = verificationList,
            Findings = Findings.Distinct(findings),
            LogFile = Log.FilePath,
        };
        Log.Info("report.created", "Created the diagnostic report.", new Dictionary<string, object?>
        {
            ["plans"] = planList.Count,
            ["applyResults"] = applyList.Count,
            ["verifications"] = verificationList.Count,
            ["findings"] = report.Findings.Count,
        });
        return report;
    }

    /// <summary>
    /// Hosts of queues that may be TA printers (<see cref="ModelMatcher.MayBeTaQueue"/>, the rule duplicate detection uses too),
    /// every one a queue prints to (<see cref="QueueLocator.HostsOf"/>: also each port of a pooled queue, the server of an LPR
    /// Port and the host of an Internet Port's URL). The hosts of queues on one Standard TCP/IP port come first, because their
    /// queues may be configured; the others only need a probe to be merged with a printer's other spellings, which an IP
    /// address does without one, so only their host names are probed, last, where the probe limit cuts first.
    /// </summary>
    private IReadOnlyList<string> ExistingQueueHosts(SystemSnapshot snapshot)
    {
        var queues = snapshot.Queues.Where(q => !q.IsServerConnection && _matcher.MayBeTaQueue(q)).ToList();
        var ownPort = queues.Select(q => QueueLocator.HostOf(q, snapshot.Ports)).OfType<string>().ToList();
        var others = queues.Where(q => QueueLocator.HostOf(q, snapshot.Ports) is null)
            .SelectMany(q => QueueLocator.HostsOf(q, snapshot.Ports))
            .Where(h => PrinterAddress.TryParse(h, out _, out var ip, out _) && ip is null);
        return [.. ownPort, .. others];
    }

    private List<NetworkIdentity> Probe(IEnumerable<string> addresses, CancellationToken cancellationToken)
    {
        var connection = Config.Connection;
        var options = new NetworkProbeOptions(connection.RawPortNumber, connection.SnmpEnabled, connection.SnmpCommunity, connection.NetworkTimeoutMilliseconds);
        var identities = new List<NetworkIdentity>();
        foreach (string address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NetworkIdentity identity;
            try
            {
                identity = Services.Network.Probe(address, options, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn("detect.probe.failed", $"Probing {address} failed.", new Dictionary<string, object?> { ["address"] = address }, ex);
                identity = new NetworkIdentity { Address = address, Error = ex.Message };
            }

            identities.Add(identity);
            Log.Info("detect.probe", $"Probed {address}.", new Dictionary<string, object?>
            {
                ["address"] = address,
                ["resolvedAddress"] = identity.ResolvedAddress,
                ["rawPortReachable"] = identity.RawPortReachable,
                ["snmpResponded"] = identity.SnmpResponded,
                ["sysDescr"] = identity.SysDescr,
                ["vendorModelName"] = identity.VendorModelName,
                ["deviceId"] = identity.Ieee1284DeviceId,
                ["serialNumber"] = identity.SerialNumber,
                ["error"] = identity.Error,
            });
        }
        return identities;
    }

    /// <summary>The ticket printer's address named by the command line (--address) or, without one, the branch file.</summary>
    public string? PreferredAddress => Options.PreferredAddress ?? Branch?.PrinterAddress;

    /// <summary>The ticket printer's model named by the command line (--model) or, without one, the branch file.</summary>
    public string? PreferredModelId => Options.PreferredModelId ?? Branch?.ModelId;

    /// <summary>
    /// The decision limited to the printer at <paramref name="address"/>: eligible printers there (the configured model picks
    /// among several), else those whose model must be confirmed or named; never a printer elsewhere. A queue host there whose
    /// model must be decided, next to an eligible one, holds another queue of the same printer that may be the ticket queue:
    /// the operator chooses (Repair: the queue already on the ticket form), and the model cannot tell them apart. So does a
    /// queue the probe limit left unchecked (<see cref="DetectionResult.UncheckedQueues"/>) when the address is only another
    /// spelling of the printer: it names no queue's host, and that queue's host may be one more.
    /// </summary>
    private CandidateSelection AtAddress(List<PrinterCandidate> candidates, string address, DetectionResult detection)
    {
        var atAddress = candidates.Where(c => c.IsAtAddress(address) && (c.IsEligible || NeedsDecision(c))).ToList();
        var eligible = atAddress.Where(c => c.IsEligible).ToList();
        bool undecidedQueue = atAddress.Any(c => NeedsDecision(c) && c.Queue is not null);
        bool UncheckedMayBeItsQueue(PrinterCandidate chosen) => detection.UncheckedQueues.Count > 0 && !Same(chosen.NetworkAddress, address);
        if (eligible is [var single] && !undecidedQueue && !UncheckedMayBeItsQueue(single))
            return new CandidateSelection(single, SelectionReason.PreferredByConfiguration, [single]);
        if (eligible.Count > 0)
            // One printer reached by several queues' hosts, or next to another queue of it: the configured model, or the operator, decides.
            return !undecidedQueue && ByConfiguredModel(eligible) is { } preferred && !UncheckedMayBeItsQueue(preferred)
                ? new CandidateSelection(preferred, SelectionReason.PreferredByConfiguration, [preferred])
                : new CandidateSelection(null, SelectionReason.MultipleCandidates, PreferenceFirst(atAddress));
        return atAddress.Count > 0
            // The printer IT named answers but needs its model confirmed or named (silent: --model --confirm-model).
            ? new CandidateSelection(null, SelectionReason.NeedsConfirmation, atAddress)
            : new CandidateSelection(null, SelectionReason.NoCandidates, []);
    }

    /// <summary>
    /// The decision without a configured address. The configured model picks its one eligible printer only when no other printer
    /// may be of that model: none whose model must be decided is probably that model or unknown, no queue that turned out to
    /// belong to another printer may be that model's (<see cref="RejectedQueueMayBe"/>), and no queue was left unchecked
    /// (<see cref="DetectionResult.UncheckedQueues"/>: its printer may be of any model). A single eligible printer is chosen
    /// only when nothing else could be the ticket printer: no printer whose model must be decided, no rejected or unchecked
    /// queue, and not another model than the configured one.
    /// </summary>
    private CandidateSelection WithoutAddress(List<PrinterCandidate> candidates, DetectionResult detection)
    {
        var eligible = candidates.Where(c => c.IsEligible).ToList();
        var undecided = candidates.Where(NeedsDecision).ToList();
        int queuesElsewhere = detection.RejectedQueues.Count + detection.UncheckedQueues.Count;
        string? modelId = PreferredModelId;
        bool modelElsewhere = modelId is not null
            && (undecided.Any(c => c.Model is null || Same(c.Model.Id, modelId)) || RejectedQueueMayBe(detection, modelId)
                || detection.UncheckedQueues.Count > 0);
        if (!modelElsewhere && ByConfiguredModel(eligible) is { } preferred)
            return new CandidateSelection(preferred, SelectionReason.PreferredByConfiguration, [preferred]);
        if (eligible is [var single] && undecided.Count == 0 && queuesElsewhere == 0 && (modelId is null || Same(single.Model!.Id, modelId)))
            return new CandidateSelection(single, SelectionReason.SingleCandidate, eligible);
        if (eligible.Count > 0)
            // Several eligible printers, or one next to printers whose model must be confirmed or named, next to a queue whose
            // printer is another one or not known, or of another model than the configured one: the operator chooses.
            return new CandidateSelection(null, SelectionReason.MultipleCandidates, PreferenceFirst([.. eligible, .. undecided]));
        return undecided.Count > 0
            ? new CandidateSelection(null, SelectionReason.NeedsConfirmation, PreferenceFirst(undecided))
            : new CandidateSelection(null, SelectionReason.NoCandidates, []);
    }

    /// <summary>
    /// Whether the printer of a rejected queue (<see cref="DetectionResult.RejectedQueues"/>) may be of <paramref name="modelId"/>:
    /// the queue's approved driver is that model's, or a probe of a host it prints to suggests that model. A queue whose driver
    /// names no single model (a class driver) may be of any model unless the printer at one of its hosts answered SNMP, so it
    /// told its model (or that it is none of the supported ones).
    /// </summary>
    public bool RejectedQueueMayBe(DetectionResult detection, string modelId) =>
        detection.Snapshot.Queues
            .Where(q => detection.RejectedQueues.Contains(q.Name, StringComparer.OrdinalIgnoreCase))
            .Any(q => ModelsOfPrinterOf(detection, q) is not { } models || models.Any(m => Same(m.Id, modelId)));

    /// <summary>The models a rejected queue's printer may be (see <see cref="RejectedQueueMayBe"/>); null when it may be any.</summary>
    private List<ModelDefinition>? ModelsOfPrinterOf(DetectionResult detection, PrintQueueInfo queue)
    {
        var identities = QueueLocator.HostsOf(queue, detection.Snapshot.Ports)
            .SelectMany(host => detection.Snapshot.NetworkIdentities.Where(i => Same(i.Address, host) || Same(i.ResolvedAddress, host)))
            .ToList();
        var models = _matcher.ModelsAcceptingDriver(queue.DriverName).Concat(identities.SelectMany(i => _matcher.ModelsSuggestedBy(i))).Distinct().ToList();
        return models.Count == 0 && !identities.Any(i => i.SnmpResponded) ? null : models;
    }

    /// <summary>The one candidate of the branch/command-line model, if exactly one has it.</summary>
    private PrinterCandidate? ByConfiguredModel(IEnumerable<PrinterCandidate> candidates) =>
        PreferredModelId is { } modelId && candidates.Where(c => Same(c.Model?.Id, modelId)).ToList() is [var only] ? only : null;

    /// <summary>Candidates of the configured model first, then connected ones, otherwise in detection order.</summary>
    private List<PrinterCandidate> PreferenceFirst(IEnumerable<PrinterCandidate> candidates)
    {
        string? modelId = PreferredModelId;
        return candidates
            .OrderByDescending(c => modelId is not null && Same(c.Model?.Id, modelId))
            .ThenByDescending(c => c.PhysicallyAvailable)
            .ToList();
    }

    /// <summary>
    /// Splits off USB printers that are not connected while a USB printer of the same model is
    /// (<see cref="PrinterCandidate.IsSupersededAmong"/>): the computer prints to the connected one. A disconnected printer next
    /// to any other printer stays in the decision, because a switched-off ticket printer must not lose to a printer that happens
    /// to be on; so does a lone one, which can still be set up.
    /// </summary>
    private static (List<PrinterCandidate> Considered, List<PrinterCandidate> Superseded) WithoutSuperseded(IReadOnlyList<PrinterCandidate> candidates) =>
        (candidates.Where(c => !c.IsSupersededAmong(candidates)).ToList(), candidates.Where(c => c.IsSupersededAmong(candidates)).ToList());

    /// <summary>The operator must decide about it: identified only probably, or answering at a requested address without naming its model.</summary>
    private static bool NeedsDecision(PrinterCandidate candidate) => candidate.NeedsConfirmation || IsUnidentifiedNetworkPrinter(candidate);

    private static bool IsRepairable(PrinterCandidate candidate) => candidate is { IsEligible: true, Queue.IsServerConnection: false };

    /// <summary>The candidate's queue's machine-wide default form is already the ticket form; false when the defaults cannot be read.</summary>
    private bool UsesTicketForm(PrinterCandidate candidate) => DefaultFormIsTicket(candidate.Queue!.Name);

    /// <summary>The candidate has a local queue whose machine-wide default is already the ticket form (Repair's rule). Reads defaults only.</summary>
    public bool HasQueueOnTicketForm(PrinterCandidate candidate) => candidate.Queue is { IsServerConnection: false } && UsesTicketForm(candidate);

    /// <summary>
    /// Among one printer's queues, the one already set up as the ticket printer: the only one whose machine-wide default is the
    /// ticket form, else the only one with the configured queue name; null when none can be told apart.
    /// </summary>
    private PrintQueueInfo? TicketQueue(IReadOnlyList<PrintQueueInfo> queues)
    {
        if (queues.Where(q => DefaultFormIsTicket(q.Name)).ToList() is [var configured])
            return configured;
        string name = Options.QueueNameOverride ?? Branch?.QueueName ?? Config.Connection.NewQueueName;
        return queues.Where(q => Same(q.Name, name)).ToList() is [var named] ? named : null;
    }

    private bool DefaultFormIsTicket(string queueName)
    {
        try
        {
            return Services.Spooler.GetDevMode(queueName, DevModeScope.Global) is { } defaults && Same(defaults.FormName, Config.Ticket.FormName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("detect.queue.defaults", $"The defaults of '{queueName}' could not be read.", exception: ex);
            return false;
        }
    }

    /// <summary>
    /// A network printer whose model is not known: one that answers at a requested address, or a queue host of a printer with
    /// another queue host, also when it does not answer (switched off): its queue may be the ticket queue, so it is decided
    /// about, never passed over for the printer's other queue.
    /// </summary>
    private static bool IsUnidentifiedNetworkPrinter(PrinterCandidate candidate) =>
        candidate is { Model: null, Kind: TargetKind.NetworkAddress } && (candidate.PhysicallyAvailable || candidate.Queue is not null);

    private static List<string> DistinctAddresses(IEnumerable<string?> addresses) =>
        addresses
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
