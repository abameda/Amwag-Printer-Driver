using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.UI.Views;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Diagnostics;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.UI;

public sealed partial class MainForm
{
    private ProgressView? _progress;
    private CancellationTokenSource? _run;
    private bool _closeWhenStopped;
    private FlowResult? _lastResult;
    private TargetSelector? _repairTarget;
    // The steps in which an action of this run began (the engine reports each action it starts, also from the helper).
    private readonly HashSet<StepId> _actionsBegun = [];

    private string? LogFolder => Path.GetDirectoryName(Runtime.Log.FilePath ?? "") is { Length: > 0 } folder ? folder : null;

    /// <summary>
    /// The printer a result is about, as Repair's explicit target: the Repair buttons repair exactly that printer. Only its key is
    /// pinned, so Repair goes by what the printer reports now (one that was asleep may since have reported its exact model), unless
    /// its model was asserted by the operator or IT: that model stays.
    /// </summary>
    internal static TargetSelector? RepairTargetOf(FlowResult result) => result.Target switch
    {
        null => null,
        { Confidence: MatchConfidence.UserAsserted } asserted => TargetSelector.For(asserted, modelAssertedByUser: true),
        { } target => new TargetSelector(target.Key),
    };

    /// <summary>Runs the flow once in <paramref name="mode"/>; never throws (failures become a result view).</summary>
    /// <param name="repairTarget">Repair only: the printer to repair (null lets Repair choose, asking when several qualify).</param>
    internal async Task RunFlowAsync(SetupMode mode, TargetSelector? repairTarget = null)
    {
        if (_run is not null)
            return;

        _closeWhenStopped = false;
        _mode = mode;
        _repairTarget = mode == SetupMode.Repair ? repairTarget : null;
        _lastResult = null;
        _subtitle.Text = T.ModeSubtitle(mode);
        _steps.Reset();
        _actionsBegun.Clear();
        var previousProgress = _progress;
        _progress = new ProgressView(_context, mode);
        ShowView(_progress);
        if (previousProgress is not null)
            DisposeLater(previousProgress);

        using var cancellation = new CancellationTokenSource();
        _run = cancellation;
        var environment = Runtime.Environment with { Options = Runtime.Environment.Options with { Mode = mode }, RepairTarget = _repairTarget };
        var flow = new SetupFlow(environment, _interaction, Runtime.Elevation);
        var progress = new Progress<FlowUpdate>(ApplyUpdate);
        Runtime.Log.Info("ui.run.started", "Setup flow started from the window.", new Dictionary<string, object?>
        {
            ["mode"] = mode.ToString(),
            ["repairTarget"] = _repairTarget?.Key,
        });

        FlowResult result;
        try
        {
            result = await Task.Run(() => flow.RunAsync(progress, cancellation.Token));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            result = new FlowResult(FlowOutcome.Cancelled) { ExitCode = ExitCodes.Cancelled };
        }
        catch (Exception ex)
        {
            Runtime.Log.Error("ui.run.failed", "The setup flow failed unexpectedly.", ex);
            result = new FlowResult(FlowOutcome.Failed)
            {
                Findings = [new Finding(FindingCode.OperationFailed, Severity.Error, ex.GetType().Name + ": " + ex.Message)],
                ExitCode = ExitCodes.InternalError,
            };
        }
        finally
        {
            _run = null;
        }

        Runtime.Log.Info("ui.run.finished", "Setup flow finished.", new Dictionary<string, object?>
        {
            ["mode"] = mode.ToString(),
            ["outcome"] = result.Outcome.ToString(),
            ["exitCode"] = result.ExitCode,
        });

        if (_closeWhenStopped)
        {
            Close();
            return;
        }
        ShowResult(result);
    }

    internal void ApplyUpdate(FlowUpdate update)
    {
        if (_progress is null)
            return;

        switch (update.Kind)
        {
            case FlowEventKind.Step when update.Progress is { } step:
                if (step is { Action: not null, Phase: not ProgressPhase.Skipped })
                    _actionsBegun.Add(step.Step);
                _steps.SetState(step.Step, step.State, StatusKeyFor(step, _mode, _actionsBegun.Contains(step.Step)));
                _progress.ShowProgress(step);
                break;
            case FlowEventKind.TargetSelected when update.Target is { } target:
                _progress.ShowTarget(target);
                break;
            case FlowEventKind.ElevationRequested:
                _progress.ShowElevationNote(true);
                break;
            case FlowEventKind.ElevationDeclined:
                _progress.ShowElevationNote(false);
                break;
        }
    }

    internal void ShowResult(FlowResult result)
    {
        _lastResult = result;
        FinishSteps(result);
        ShowView(new ResultView(_context, result, _mode, LogFolder is not null, new ResultCommands(
            PrintTestTicket: () => StartTestPrint(result),
            Repair: () => _ = RunFlowAsync(SetupMode.Repair, RepairTargetOf(result)),
            // The same run again, for the same printer when it was a Repair of a chosen printer.
            TryAgain: () => _ = RunFlowAsync(_mode, _repairTarget),
            SaveReport: () => SaveReport(result),
            CopyReport: () => CopyReport(result),
            OpenLogFolder: OpenLogFolder,
            Close: Close)
        {
            CheckTicketSoftware = () => StartTicketCheck(result),
            // Repair found no printer it can change: Setup chooses the printer itself, as it does when it starts.
            RunSetup = () => _ = RunFlowAsync(SetupMode.Setup),
        }));
    }

    /// <summary>
    /// The text a step shows for <paramref name="update"/> instead of its state's own, or null. A step that failed because another
    /// run was applying changes or the configuration changed began nothing: "not started", not "failed". A detection that failed
    /// because the computer cannot be set up as it is (the Print Spooler, protected print mode, the platform, the configuration or
    /// the ticket dimensions) found no fault with the printer: never "No printer found". One skipped because the printer reports
    /// another model than IT gave found the printer: never "Skipped". Diagnostics ends the driver check and the verification on the
    /// ticket printer; without one it says that several printers were checked, or that the ticket printer could not be checked
    /// (its model must be confirmed, or nothing usable is at the configured address). Setup and Repair check a missing or outdated
    /// driver before they install it: "will be installed" (or "updated"), not "Driver is ready". An apply step skipped for a reason
    /// (approval declined, the helper not started or refusing, a blocker) did not start either, nor did one left incomplete without
    /// any of its actions begun (<paramref name="actionsBegun"/>), such as the queue's defaults when the driver install they depend
    /// on failed. One that began but was left incomplete because an action of the other apply step failed (its update carries that
    /// failure's code) did not finish: never "with warnings". One skipped because the run was stopped may have done part of its
    /// work; in Diagnostics the apply steps change nothing. Only an apply step skipped without a reason had nothing to do: "Already
    /// installed" or "Already correct". A failed one keeps "failed": a helper whose connection broke may have begun actions the
    /// window never heard of.
    /// </summary>
    internal static string? StatusKeyFor(ProgressUpdate update, SetupMode mode, bool actionsBegun)
    {
        if (update is { State: StepState.Failed, Code: FindingCode.ApplyLockBusy or FindingCode.ConfigurationChanged })
            return "step.notStarted";
        if (update is { Step: StepId.Detect, Action: null })
        {
            return update switch
            {
                { State: StepState.Failed, Code: { } code } when IsEnvironmentBlocker(code) => "step.Detect.blocked",
                { State: StepState.Skipped, Code: FindingCode.NetworkIdentityMismatch } => "step.Detect.modelRefused",
                _ => null,
            };
        }
        if (update is { Step: StepId.CheckDriver or StepId.Verify, Action: null } && mode == SetupMode.Diagnostics)
        {
            string? ticketPrinter = update switch
            {
                { State: StepState.Warning, Code: FindingCode.MultipleSupportedPrinters } => "step.ticketUnknown",
                { State: StepState.Skipped, Code: FindingCode.ProbableMatchRequiresConfirmation or FindingCode.NoSupportedPrinterFound } => "step.notChecked",
                _ => null,
            };
            if (ticketPrinter is not null)
                return ticketPrinter;
        }
        if (update is { Step: StepId.CheckDriver, State: StepState.Succeeded, Action: null })
        {
            return update.Code switch
            {
                FindingCode.DriverMissing => "step.CheckDriver.willInstall",
                FindingCode.DriverOutdated => "step.CheckDriver.willUpdate",
                _ => null,
            };
        }

        // The engine's own updates for one action pass; the step's final state follows once the actions are over.
        if (!IsApplyStep(update.Step) || update.Action is not null)
            return null;
        return update switch
        {
            { State: StepState.Skipped } when mode == SetupMode.Diagnostics => "step.notChanged",
            { State: StepState.Skipped, Code: null } => null,
            { State: StepState.Skipped, Code: FindingCode.Cancelled } => "step.stopped",
            { State: StepState.Skipped } => "step.notStarted",
            { State: StepState.Warning } when !actionsBegun => "step.notStarted",
            { State: StepState.Warning, Code: not null } => "step.afterFailure",
            _ => null,
        };
    }

    private static bool IsApplyStep(StepId step) => step is StepId.InstallDriver or StepId.ConfigureTicket;

    /// <summary>What keeps the whole computer from being set up, whichever printer it is (the run's environment blockers).</summary>
    private static bool IsEnvironmentBlocker(FindingCode code) =>
        code is FindingCode.SpoolerDisabled or FindingCode.SpoolerUnavailable or FindingCode.ProtectedPrintModeEnabled
            or FindingCode.UnsupportedOperatingSystem or FindingCode.UnsupportedArchitecture or FindingCode.ConfigurationInvalid
            or FindingCode.DimensionsUnconfirmed;

    /// <summary>
    /// Steps the flow left running or never reached get a final state that matches the outcome: one never reached says so, never
    /// "Waiting". A driver check that said the driver will be installed (or updated) says at the end whether it was
    /// (<see cref="DriverInstalled"/>), or that it is not known. A detection that ended the run without a printer to set up or
    /// repair says "No printer found" only when that is so: not for Repair's printer or printers without a queue, nor for a model
    /// nobody confirmed, nor for the printer at the configured address that a queue left unchanged keeps from being set up.
    /// </summary>
    private void FinishSteps(FlowResult result)
    {
        FlowOutcome outcome = result.Outcome;
        bool ready = outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings;
        bool stopped = outcome is FlowOutcome.Cancelled or FlowOutcome.ElevationDeclined;
        foreach (var step in Enum.GetValues<StepId>())
        {
            if (_steps.StateOf(step) == StepState.Running)
            {
                // An apply step that was running when the run stopped did not finish: never "Already installed".
                string? stoppedKey = stopped && IsApplyStep(step) ? (outcome == FlowOutcome.Cancelled ? "step.stopped" : "step.notStarted") : null;
                _steps.SetState(step, ready ? StepState.Succeeded : stopped ? StepState.Skipped : StepState.Failed, stoppedKey);
            }
            else if (_steps.StateOf(step) == StepState.Pending && step != StepId.Ready)
            {
                _steps.SetState(step, StepState.Skipped, "step.notReached");
            }
        }

        string? planned = _steps.StatusKeyOf(StepId.CheckDriver);
        if (planned is "step.CheckDriver.willInstall" or "step.CheckDriver.willUpdate")
        {
            switch (DriverInstalled(result))
            {
                case true:
                    _steps.SetState(StepId.CheckDriver, StepState.Succeeded);
                    break;
                case false:
                    _steps.SetState(StepId.CheckDriver, StepState.Warning, planned == "step.CheckDriver.willInstall" ? "step.CheckDriver.notInstalled" : "step.CheckDriver.notUpdated");
                    break;
                default:
                    _steps.SetState(StepId.CheckDriver, StepState.Warning, "step.CheckDriver.notConfirmed");
                    break;
            }
        }

        string? detected = ResultView.ReasonOf(result) switch
        {
            NoPrinterReason.NoQueue => "step.Detect.notSetUp",
            NoPrinterReason.PrintersWithoutQueue => "step.Detect.noneSetUp",
            NoPrinterReason.ModelNotConfirmed => "step.Detect.modelNotConfirmed",
            NoPrinterReason.LeftUnchanged => "step.Detect.leftUnchanged",
            _ => null,
        };
        if (detected is not null && _steps.StateOf(StepId.Detect) == StepState.Failed)
            _steps.SetState(StepId.Detect, StepState.Failed, detected);

        if (ready)
            _steps.SetState(StepId.Ready, outcome == FlowOutcome.Ready ? StepState.Succeeded : StepState.Warning);
        else if (_steps.StateOf(StepId.Ready) == StepState.Pending)
            _steps.SetState(StepId.Ready, stopped ? StepState.Skipped : StepState.Failed);
    }

    /// <summary>
    /// Whether the approved driver is installed at the end, or null when that is not known. The read-back decides when it checked
    /// the driver; otherwise the install action of this run (succeeded or already satisfied, or not). Without either, a run that
    /// ended before reading anything back installed nothing (approval declined, the helper not started, its refusal blocked the
    /// run), so the driver is still not installed; a read-back that holds no driver check (it did not find the printer again)
    /// cannot say.
    /// </summary>
    private static bool? DriverInstalled(FlowResult result)
    {
        if (result.Verification?.Checks.FirstOrDefault(c => c.Item == VerificationItem.DriverInstalled) is { } check)
            return check.Status == CheckStatus.Pass;
        ActionResult[] installs = result.Report?.ApplyResults.SelectMany(r => r.Results).Where(a => a.Action.Kind == ActionKind.InstallDriver).ToArray() ?? [];
        if (installs.Length > 0)
            return installs.Any(a => a.Outcome is ActionOutcome.Succeeded or ActionOutcome.AlreadySatisfied);
        return result.Verification is null ? false : null;
    }

    internal void AskChoosePrinter(IReadOnlyList<PrinterCandidate> candidates, Func<PrinterCandidate?, bool> answer) =>
        ShowView(new ChoosePrinterView(_context, candidates, choice =>
        {
            Runtime.Log.Info("ui.choose_printer.answered", "The employee answered the printer choice.", new Dictionary<string, object?> { ["choice"] = choice?.Key, ["offered"] = candidates.Count });
            Answer(answer, choice);
        }));

    internal void AskConfirmModel(PrinterCandidate candidate, Func<bool, bool> answer) =>
        ShowView(new ConfirmModelView(_context, candidate, confirmed =>
        {
            Runtime.Log.Info("ui.confirm_model.answered", "The employee answered the probable model question.", new Dictionary<string, object?> { ["target"] = candidate.Key, ["model"] = candidate.Model?.Id, ["confirmed"] = confirmed });
            Answer(answer, confirmed);
        }));

    internal void AskNetworkAddress(NetworkAddressPrompt prompt, Func<string?, bool> answer) =>
        ShowView(new NetworkAddressView(_context, prompt, address =>
        {
            Runtime.Log.Info("ui.network_address.answered", "The employee answered the network address question.", new Dictionary<string, object?> { ["address"] = address, ["previousProblem"] = prompt.PreviousProblem?.ToString() });
            Answer(answer, address);
        }));

    internal void AskModel(PrinterCandidate candidate, IReadOnlyList<ModelDefinition> models, Func<ModelDefinition?, bool> answer) =>
        ShowView(new ChooseModelView(_context, candidate, models, model =>
        {
            Runtime.Log.Info("ui.choose_model.answered", "The employee answered the model choice for an unidentified printer.", new Dictionary<string, object?> { ["target"] = candidate.Key, ["model"] = model?.Id });
            Answer(answer, model);
        }));

    /// <summary>Back to the progress view, unless the question was already over (the run was cancelled meanwhile).</summary>
    private void Answer<T>(Func<T, bool> answer, T value)
    {
        if (answer(value) && _progress is not null)
            ShowView(_progress);
    }

    private string? FormatReport(FlowResult result)
    {
        if (result.Report is null)
            return null;
        try
        {
            return DiagnosticReportFormatter.ToText(result.Report);
        }
        catch (Exception ex)
        {
            Runtime.Log.Error("ui.report.format_failed", "The diagnostic report could not be formatted.", ex);
            return null;
        }
    }

    private void SaveReport(FlowResult result)
    {
        string? text = FormatReport(result);
        if (text is null)
        {
            ShowMessage(T.Text("result.reportUnavailable"), MessageBoxIcon.Warning);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = T.Text("result.saveTitle"),
            Filter = T.Text("result.textFiles") + " (*.txt)|*.txt",
            FileName = "AMWAG-Printer-Report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt",
            DefaultExt = "txt",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Runtime.Log.Info("ui.report.saved", "Diagnostic report saved.", new Dictionary<string, object?> { ["path"] = dialog.FileName });
            (_view as ResultView)?.ShowStatus(T.Text("result.saved"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Runtime.Log.Warn("ui.report.save_failed", "Saving the diagnostic report failed.", new Dictionary<string, object?> { ["path"] = dialog.FileName }, ex);
            ShowMessage(T.Format("result.saveFailed", ex.Message), MessageBoxIcon.Error);
        }
    }

    private void CopyReport(FlowResult result)
    {
        string? text = FormatReport(result);
        if (text is null)
        {
            ShowMessage(T.Text("result.reportUnavailable"), MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Clipboard.SetText(text);
            Runtime.Log.Info("ui.report.copied", "Diagnostic report copied to the clipboard.");
            (_view as ResultView)?.ShowStatus(T.Text("result.copied"));
        }
        catch (ExternalException ex)
        {
            // Another program holds the clipboard.
            Runtime.Log.Warn("ui.report.copy_failed", "Copying the diagnostic report failed.", exception: ex);
            ShowMessage(T.Text("result.reportUnavailable"), MessageBoxIcon.Warning);
        }
    }

    private void OpenLogFolder()
    {
        if (LogFolder is not { } folder || !Directory.Exists(folder))
        {
            ShowMessage(T.Text("result.logUnavailable"), MessageBoxIcon.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception ex)
        {
            Runtime.Log.Warn("ui.log_folder.open_failed", "Opening the log folder failed.", new Dictionary<string, object?> { ["folder"] = folder }, ex);
            ShowMessage(T.Text("result.logUnavailable"), MessageBoxIcon.Information);
        }
    }
}
