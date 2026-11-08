using System.ComponentModel;
using System.Globalization;
using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.UI.Views;

internal sealed record ResultCommands(
    Action PrintTestTicket,
    Action Repair,
    Action TryAgain,
    Action SaveReport,
    Action CopyReport,
    Action OpenLogFolder,
    Action Close)
{
    /// <summary>Opens the read-only "Check ticket software" view for the configured queue.</summary>
    public Action CheckTicketSoftware { get; init; } = () => { };

    /// <summary>Runs Setup, after a Repair that found no printer it can change: only Setup adds a printer to Windows.</summary>
    public Action RunSetup { get; init; } = () => { };
}

/// <summary>
/// Why a run found no printer it could set up, repair or check, as its NoSupportedPrinterFound error says it. Repair's ends name
/// their "reason": a known printer without a queue (NoQueue), a model nobody confirmed (ModelNotConfirmed), supported printers none
/// of which has a queue (PrintersWithoutQueue), no supported printer at all (NoPrinter). In every mode the error about the configured
/// address carries that "address": nothing usable is there (Address), or a queue the setup leaves unchanged prints to the printer
/// there and IT must change it first ("leftUnchanged": LeftUnchanged).
/// </summary>
internal enum NoPrinterReason { None, NoQueue, ModelNotConfirmed, PrintersWithoutQueue, NoPrinter, Address, LeftUnchanged }

/// <summary>The end of a run: outcome banner, the most important finding in plain words, summary, every check, and actions.</summary>
internal sealed class ResultView : ViewBase
{
    private readonly FlowResult _result;
    private readonly SetupMode _mode;
    private readonly TableLayoutPanel _content;
    private readonly ActionButton _print;
    private readonly ActionButton _tryAgain;
    private readonly ActionButton _runSetup;
    private readonly ActionButton _repair;
    private readonly ActionButton _close;
    private readonly ActionButton _save;
    private readonly ActionButton _copy;
    private readonly ActionButton _openLog;
    private readonly ActionButton _checkSoftware;
    private readonly bool _canPrint;
    // IT's model that the printer contradicts, Repair's "run Setup" ends, and why no printer was set up, repaired or checked: each
    // has its own banner, headline and actions.
    private readonly Finding? _refusedModel;
    private readonly Finding? _setupNeeded;
    private readonly NoPrinterReason _whyNoPrinter;
    private bool _detailsExpanded;
    private string? _status;

    public ResultView(ViewContext context, FlowResult result, SetupMode mode, bool logAvailable, ResultCommands commands) : base(context)
    {
        _result = result;
        _mode = mode;
        _detailsExpanded = mode == SetupMode.Diagnostics || result.Outcome == FlowOutcome.NotReady;
        (_content, var bar) = CreateFrame();

        bool ready = result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings;
        // A configuration that changed while the window was open is refused again on every try until the program restarts.
        bool restartNeeded = AllFindings().Any(f => f is { Code: FindingCode.ConfigurationChanged, Severity: Severity.Error });
        _refusedModel = AllFindings().FirstOrDefault(IsRefusedModel);
        _setupNeeded = AllFindings().FirstOrDefault(IsSetupNeeded);
        _whyNoPrinter = AllFindings().Select(ReasonOf).FirstOrDefault(r => r != NoPrinterReason.None);
        _canPrint = ready && !string.IsNullOrEmpty(result.Verification?.Summary.QueueName);
        // IT's model comes from switches this window cannot change, so trying again ends the same way; Repair that found no
        // printer it can change would find none again, so Setup is offered instead.
        CanTryAgain = !ready && !restartNeeded && _refusedModel is null && _setupNeeded is null;
        CanRunSetup = _setupNeeded is not null;
        // A Diagnostics result blocked by the computer (protected print mode, the platform, the configuration) offers no Repair:
        // Repair checks the same blockers first and would stop the same way.
        CanRepair = (result.Verification is not null || mode == SetupMode.Diagnostics)
            && !(mode == SetupMode.Diagnostics && result.Outcome == FlowOutcome.Blocked)
            && RepairCanChange();
        _print = new ActionButton(ButtonKind.Primary, S) { Visible = _canPrint };
        _tryAgain = new ActionButton(ButtonKind.Primary, S) { Visible = CanTryAgain };
        _runSetup = new ActionButton(ButtonKind.Primary, S) { Visible = CanRunSetup };
        _repair = new ActionButton(ButtonKind.Secondary, S) { Visible = CanRepair };
        // Close is the primary action when the view offers no other.
        _close = new ActionButton(_canPrint || CanTryAgain || CanRunSetup ? ButtonKind.Secondary : ButtonKind.Primary, S);
        _save = new ActionButton(ButtonKind.Ghost, S) { Enabled = result.Report is not null };
        _copy = new ActionButton(ButtonKind.Ghost, S) { Enabled = result.Report is not null };
        _openLog = new ActionButton(ButtonKind.Ghost, S) { Enabled = logAvailable };
        // Reading what the ticket software sends needs only a queue; it changes nothing, so it is offered whatever the outcome.
        CanCheckTicketSoftware = !string.IsNullOrEmpty(result.Verification?.Summary.QueueName);
        _checkSoftware = new ActionButton(ButtonKind.Ghost, S) { Visible = CanCheckTicketSoftware };

        _print.Click += (_, _) => commands.PrintTestTicket();
        _checkSoftware.Click += (_, _) => commands.CheckTicketSoftware();
        _tryAgain.Click += (_, _) => commands.TryAgain();
        _runSetup.Click += (_, _) => commands.RunSetup();
        _repair.Click += (_, _) => commands.Repair();
        _close.Click += (_, _) => commands.Close();
        _save.Click += (_, _) => commands.SaveReport();
        _copy.Click += (_, _) => commands.CopyReport();
        _openLog.Click += (_, _) => commands.OpenLogFolder();

        var main = Ui.ButtonRow(S);
        main.Controls.AddRange([_print, _tryAgain, _runSetup, _repair, _close]);
        var secondary = Ui.ButtonRow(S);
        secondary.Margin = Padding.Empty;
        secondary.Controls.AddRange([_checkSoftware, _save, _copy, _openLog]);
        // The four text-only actions fit one line at the default window width in both languages (they wrap when narrower).
        foreach (var ghost in new[] { _checkSoftware, _save, _copy, _openLog })
        {
            ghost.Padding = S.Pad(4, 4, 4, 4);
            ghost.Margin = S.Pad(0, 0, 4, 4);
        }
        bar.Controls.Add(main);
        bar.Controls.Add(secondary);

        Localize();
    }

    // Control.Visible reports false while the window is hidden, so the intended state is kept separately.
    public override IButtonControl? AcceptAction => _canPrint ? _print : CanTryAgain ? _tryAgain : CanRunSetup ? _runSetup : _close;
    public override IButtonControl? CancelAction => _close;

    /// <summary>Whether the "Check ticket software" action is offered (a queue was configured or found).</summary>
    public bool CanCheckTicketSoftware { get; }

    /// <summary>Whether "Try again" is offered: the run did not end ready, and running it again in this window can succeed.</summary>
    public bool CanTryAgain { get; }

    /// <summary>
    /// Whether "Run Setup" is offered instead of "Try again": Repair found no printer it can change (the ticket printer has no
    /// queue yet, none of the printers found has one, or no printer was found) and says to run Setup, which adds the printer to
    /// Windows. Not for a model nobody confirmed (Try again asks again) nor for the configured address (Setup ends the same way).
    /// </summary>
    public bool CanRunSetup { get; }

    /// <summary>
    /// Whether "Repair" is offered: the state was read back (or Diagnostics checked it), no environment blocker stops Repair, and
    /// Repair can change the printer.
    /// </summary>
    public bool CanRepair { get; }

    /// <summary>A short confirmation under the content ("Report copied").</summary>
    public void ShowStatus(string text)
    {
        _status = text;
        Localize();
    }

    public override void Localize()
    {
        _print.Text = T.Text("result.printTest");
        _tryAgain.Text = T.Text("result.tryAgain");
        _runSetup.Text = T.Text("result.runSetup");
        _repair.Text = T.Text("result.repair");
        _close.Text = T.Text("result.close");
        _save.Text = T.Text("result.saveReport");
        _copy.Text = T.Text("result.copyReport");
        _openLog.Text = T.Text("result.openLog");
        _checkSoftware.Text = T.Text("result.checkSoftware");
        Rebuild();
    }

    private void Rebuild()
    {
        _content.SuspendLayout();
        foreach (Control old in _content.Controls.Cast<Control>().ToArray())
            old.Dispose();

        _content.Controls.Add(Banner());

        var finding = MostImportantFinding();
        bool ready = _result.Outcome is FlowOutcome.Ready or FlowOutcome.ReadyWithWarnings;
        if (!ready && finding is not null)
            AddExplanation(finding);
        if (_result.Outcome == FlowOutcome.ReadyWithWarnings)
            AddWarnings();
        if (_result.RebootRequired)
            AddNote(NoteTone.Warning, T.Text("result.reboot"));

        if (_result.Verification?.Summary is { } summary && (summary.QueueName.Length > 0 || summary.ModelName.Length > 0))
            _content.Controls.Add(SummaryCard(summary));

        AddDetails();

        if (_status is not null)
            _content.Controls.Add(Ui.Paragraph(S, Theme.SmallStrong, Theme.Success, bottom: 0).With(_status));

        _content.ResumeLayout();
    }

    private Control Banner()
    {
        var (glyph, color) = _result.Outcome switch
        {
            FlowOutcome.Ready => (GlyphKind.Succeeded, Theme.Success),
            FlowOutcome.ReadyWithWarnings or FlowOutcome.ElevationDeclined => (GlyphKind.Warning, Theme.Warning),
            FlowOutcome.Cancelled => (GlyphKind.Skipped, Theme.Text),
            _ => (GlyphKind.Failed, Theme.Error),
        };

        var banner = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = S.Pad(0, 0, 0, 16) };
        banner.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        banner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var icon = new GlyphBox(glyph, S.Px(36)) { Margin = S.Pad(0, 2, 14, 0), AccessibleName = OutcomeTitle() };
        var text = Ui.Stack();
        var title = Ui.Heading(S).With(OutcomeTitle());
        title.ForeColor = color;
        title.Margin = S.Pad(0, 0, 0, 2);
        text.Controls.Add(title);
        text.Controls.Add(Ui.Paragraph(S, bottom: 0).With(OutcomeText()));
        banner.Controls.Add(icon, 0, 0);
        banner.Controls.Add(text, 1, 0);
        return banner;
    }

    /// <summary>
    /// The outcome's title as this mode says it ("Repair was stopped"), or the end's own where the outcome's would mislead: a queue
    /// the setup leaves unchanged keeps the printer at the configured address from being set up, nothing usable is at that address,
    /// Repair found printers it cannot change yet or whose model nobody confirmed (not "no ticket printer found"), and IT's model was
    /// refused (nobody stopped the run).
    /// </summary>
    private string OutcomeTitle() => (_result.Outcome, _whyNoPrinter) switch
    {
        (FlowOutcome.NoPrinter or FlowOutcome.NotReady, NoPrinterReason.LeftUnchanged) => T.Text("outcome.leftUnchanged.title"),
        (FlowOutcome.NoPrinter or FlowOutcome.NotReady, NoPrinterReason.Address) => T.Text("outcome.notAtAddress.title"),
        (FlowOutcome.NoPrinter, NoPrinterReason.NoQueue or NoPrinterReason.PrintersWithoutQueue) => T.Text("outcome.NoPrinter.needsSetup.title"),
        (FlowOutcome.NoPrinter, NoPrinterReason.ModelNotConfirmed) => T.Text("outcome.NoPrinter.modelNotConfirmed.title"),
        (FlowOutcome.Cancelled, _) when _refusedModel is not null => T.Text("outcome.Cancelled.modelRefused.title"),
        _ => T.OutcomeTitle(_result.Outcome, _mode),
    };

    /// <summary>
    /// The outcome in one sentence, as this mode says it. A run blocked after it changed something does not say that nothing was
    /// changed. The printer at the configured address says what keeps it from being set up, never to connect a cable. A sentence
    /// that is not ready invites Repair only when Repair is offered (or is what Try again runs): a printer without a queue is not
    /// sent to Repair, which never adds one. Repair that found printers it cannot change yet points to Setup, and one whose model
    /// nobody confirmed says so; IT's refused model says that nothing was changed.
    /// </summary>
    private string OutcomeText() => (_result.Outcome, _whyNoPrinter) switch
    {
        (FlowOutcome.Blocked, _) when ChangesWereMade() => T.Text("outcome.Blocked.afterChanges", _mode),
        (FlowOutcome.NoPrinter or FlowOutcome.NotReady, NoPrinterReason.LeftUnchanged) => T.Text("outcome.leftUnchanged.text", _mode),
        (FlowOutcome.NoPrinter or FlowOutcome.NotReady, NoPrinterReason.Address) => T.Text("outcome.notAtAddress.text", _mode),
        (FlowOutcome.NotReady, _) when WithoutQueue() => T.Text("outcome.NotReady.noQueue", _mode),
        (FlowOutcome.NotReady, _) when !CanRepair && _mode != SetupMode.Repair => NotReadyWithoutRepair(),
        (FlowOutcome.NoPrinter, NoPrinterReason.NoQueue) => T.Text("outcome.NoPrinter.needsSetup.text"),
        (FlowOutcome.NoPrinter, NoPrinterReason.PrintersWithoutQueue) => T.Text("outcome.NoPrinter.printersWithoutQueue.text"),
        (FlowOutcome.NoPrinter, NoPrinterReason.ModelNotConfirmed) => T.Text("outcome.NoPrinter.modelNotConfirmed.text"),
        (FlowOutcome.NoPrinter, _) when _setupNeeded is not null => T.Text("outcome.NoPrinter.runSetup"),
        (FlowOutcome.Cancelled, _) when _refusedModel is not null => T.Text("outcome.Cancelled.modelRefused.text"),
        _ => T.OutcomeText(_result.Outcome, _mode),
    };

    /// <summary>
    /// The sentence of a result that is not ready and offers no Repair. The printers a Diagnostics result about no single printer
    /// would leave Repair to ask about have not been added to Windows yet: Setup adds them. Otherwise the problem shown below is to
    /// be solved first.
    /// </summary>
    private string NotReadyWithoutRepair()
    {
        IReadOnlyList<PrinterCandidate> printers = PrintersToAskAbout();
        return printers.Count == 0 || printers.Any(p => p.Queue is not null) ? T.Text("outcome.NotReady.solve")
            : printers.Count == 1 ? T.Text("outcome.NotReady.noQueue", _mode)
            : T.Text("outcome.NotReady.printersWithoutQueue");
    }

    /// <summary>
    /// Whether a setup action changed something (the report's apply results); without a report, whether the run left out the
    /// plans' findings, which Setup and Repair do once setup actions ran. Starting the Print Spooler, which the helper does before
    /// the printer is even detected, changes no printer and does not count. Diagnostics never changes anything: its findings about
    /// the end state are the ticket printer's only, which is not a sign of changes.
    /// </summary>
    private bool ChangesWereMade() => _mode != SetupMode.Diagnostics && (_result.Report is { } report
        ? report.ApplyResults.Any(r => r.Results.Any(a => a.Outcome == ActionOutcome.Succeeded && a.Action.Kind != ActionKind.StartSpooler))
        : _result.CurrentFindings.Count > 0 && _result.CurrentFindings.Count != _result.Findings.Count);

    private void AddExplanation(Finding finding)
    {
        var tone = _result.Outcome is FlowOutcome.ElevationDeclined or FlowOutcome.Cancelled ? NoteTone.Warning : NoteTone.Error;
        var (title, text) = Explanation(finding);
        AddNote(tone, text, title);

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(finding.Detail))
            lines.Add(FormattableString.Invariant($"{finding.Code}: {finding.Detail}"));
        if (Win32ErrorFor(finding) is int error)
            lines.Add(T.Format("result.win32", error.ToString(CultureInfo.InvariantCulture), new Win32Exception(error).Message));
        if (lines.Count == 0)
            return;

        _content.Controls.Add(Ui.Paragraph(S, Theme.SmallStrong, Theme.TextSecondary, bottom: 4).With(T.Text("result.technical")));
        _content.Controls.Add(Technical(string.Join(Environment.NewLine, lines)));
    }

    private void AddWarnings()
    {
        // Ranked as the headline is, so a sign of tampering comes first and is never left out; the others in the order found.
        var current = CurrentFindings().Where(f => f.Severity == Severity.Warning).ToList();
        // Every entry next to which the run added one is named, in one note: those that print to the printer first.
        var added = current.Where(f => Variant(f) == "queueAdded")
            .OrderByDescending(f => f.Data.ContainsKey("port"))
            .Select(f => f.Data["queue"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var warnings = current
            .OrderByDescending(f => ComesFirst(f.Code))
            .DistinctBy(f => (f.Code, Variant(f)))
            .Take(4);
        foreach (var warning in warnings)
        {
            switch (Variant(warning))
            {
                case "notChecked":
                    AddNote(NoteTone.Warning, T.Text("finding.NetworkIdentityUnavailable.notChecked.text"), T.Text("finding.NetworkIdentityUnavailable.notChecked.title"));
                    break;
                case "queueAdded":
                    AddNote(NoteTone.Warning,
                        added is [var only] ? T.Format("finding.DuplicateQueues.queueAdded.text", only)
                            : string.Format(CultureInfo.InvariantCulture, T.Text("finding.DuplicateQueues.queueAdded.many"), T.DataList(added)),
                        T.Text("finding.DuplicateQueues.queueAdded.title"));
                    break;
                default:
                    AddNote(NoteTone.Warning, T.FindingText(warning.Code, _mode), T.FindingTitle(warning.Code));
                    break;
            }
        }
    }

    /// <summary>
    /// The warnings whose note is not their code's: a printer address or printer entry the limit of network probes left
    /// unchecked (its model was not read, it was not looked at: "notChecked"), and a printer entry next to which the run added
    /// one, which may print to the same printer or to another ("queueAdded"). Null for the others.
    /// </summary>
    private static string? Variant(Finding finding) => finding switch
    {
        { Code: FindingCode.NetworkIdentityUnavailable } when finding.Data.GetValueOrDefault("reason") == "probeLimit" => "notChecked",
        { Code: FindingCode.DuplicateQueues } when finding.Data.GetValueOrDefault("reason") == "queueAdded" && finding.Data.ContainsKey("queue") => "queueAdded",
        _ => null,
    };

    /// <summary>
    /// The headline's title and text: the code's as this mode says it, or the end's own where the code's would mislead. IT's
    /// refused model names the printer at the address, which may well be supported ("not a supported ticket printer" is false).
    /// Where no printer was set up, repaired or checked although printers were found, "check that the printer is switched on and
    /// connected" is false: Repair's printers without a queue only need Setup, a model nobody confirmed is confirmed by trying
    /// again, and a queue the setup leaves unchanged at the configured address is IT's to change (named with its printer; the
    /// technical details say why). Nothing usable at the configured address names that address.
    /// </summary>
    private (string Title, string Text) Explanation(Finding finding)
    {
        if (IsRefusedModel(finding))
        {
            string asserted = finding.Data["assertedModel"];
            string address = finding.Data.GetValueOrDefault("address") ?? finding.Data.GetValueOrDefault("target") ?? "";
            string text = finding.Data.TryGetValue("model", out string? reported)
                ? T.Format("finding.NetworkIdentityMismatch.modelRefused.text", asserted, address, reported)
                : T.Format("finding.NetworkIdentityMismatch.modelRefused.otherModel", asserted, address);
            return (T.Text("finding.NetworkIdentityMismatch.modelRefused.title"), text);
        }

        return ReasonOf(finding) switch
        {
            NoPrinterReason.NoQueue => (T.Text("finding.NoSupportedPrinterFound.needsSetup.title"), T.Text("finding.NoSupportedPrinterFound.needsSetup.text")),
            NoPrinterReason.PrintersWithoutQueue => (T.Text("finding.NoSupportedPrinterFound.printersWithoutQueue.title"), T.Text("finding.NoSupportedPrinterFound.printersWithoutQueue.text")),
            NoPrinterReason.ModelNotConfirmed => (T.Text("finding.NoSupportedPrinterFound.modelNotConfirmed.title"), T.Text("finding.NoSupportedPrinterFound.modelNotConfirmed.text")),
            NoPrinterReason.Address => (T.Text("finding.NoSupportedPrinterFound.notAtAddress.title"), T.Format("finding.NoSupportedPrinterFound.notAtAddress.text", finding.Data["address"])),
            NoPrinterReason.LeftUnchanged => (T.FindingTitle(FindingCode.QueueLeftUnchanged),
                T.Format("finding.NoSupportedPrinterFound.leftUnchanged.text", finding.Data["leftUnchanged"], finding.Data["address"])),
            _ => (T.FindingTitle(finding.Code), T.FindingText(finding.Code, _mode)),
        };
    }

    private void AddNote(NoteTone tone, string text, string? title = null)
    {
        var note = new NotePanel(S, tone);
        note.SetText(text, title);
        _content.Controls.Add(note);
    }

    private CardPanel SummaryCard(ConfiguredSummary summary)
    {
        string driver = summary.DriverName.Length == 0
            ? T.Text("value.notAvailable")
            : string.IsNullOrEmpty(summary.DriverVersion) ? T.Data(summary.DriverName) : T.Format("value.driverVersion", summary.DriverName, summary.DriverVersion);

        var card = new CardPanel(S);
        card.Controls.Add(new Label { Text = T.Text("result.summary"), AutoSize = true, Font = Theme.SmallStrong, ForeColor = Theme.TextSecondary, BackColor = Color.Transparent, Margin = S.Pad(0, 0, 0, 6), UseMnemonic = false });
        card.Controls.Add(InfoTable.Create(S,
        [
            (T.Text("card.model"), OrMissing(summary.ModelName)),
            (T.Text("card.queue"), OrMissing(summary.QueueName)),
            (T.Text("card.driver"), driver),
            (T.Text("card.connection"), OrMissing(summary.Connection)),
            (T.Text("card.ticketSize"), T.Data(summary.PaperSize.Length > 0 ? summary.PaperSize : Context.TicketDescription)),
            (T.Text("card.paperSource"), string.IsNullOrEmpty(summary.PaperSource) ? T.Text("value.paperSourceDefault") : T.Data(summary.PaperSource)),
        ]));
        return card;
    }

    private string OrMissing(string value) => value.Length > 0 ? T.Data(value) : T.Text("value.notAvailable");

    private void AddDetails()
    {
        var reports = _mode == SetupMode.Diagnostics
            ? _result.Verifications
            : _result.Verification is { } single ? [single] : [];

        if (reports.Count == 0)
        {
            if (_mode == SetupMode.Diagnostics)
                _content.Controls.Add(Ui.Paragraph(S).With(T.Text("result.noPrinterChecked")));
            return;
        }

        var toggle = new ActionButton(ButtonKind.Ghost, S) { Margin = S.Pad(0, 0, 0, 8) };
        // One grid, glyphs in the first column and texts in the second, not a table per check: auto-sized tables measure their
        // labels again at every level they are nested in, and a list of checks nested twice more took seconds to lay out.
        var details = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        details.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toggle.Click += (_, _) =>
        {
            _detailsExpanded = !_detailsExpanded;
            FillDetails(toggle, details, reports);
        };
        _content.Controls.Add(toggle);
        _content.Controls.Add(details);
        FillDetails(toggle, details, reports);
    }

    private void FillDetails(ActionButton toggle, TableLayoutPanel details, IReadOnlyList<VerificationReport> reports)
    {
        toggle.Text = T.Text(_detailsExpanded ? "result.details.hide" : "result.details.show");
        details.SuspendLayout();
        foreach (Control old in details.Controls.Cast<Control>().ToArray())
            old.Dispose();
        details.Visible = _detailsExpanded;

        int row = 0;
        if (_detailsExpanded)
        {
            if (_mode == SetupMode.Diagnostics)
            {
                var heading = Ui.Paragraph(S, Theme.BodyStrong, Theme.Text, bottom: 0).With(T.Text("result.printersChecked"));
                details.Controls.Add(heading, 0, row++);
                details.SetColumnSpan(heading, 2);
            }

            foreach (var report in reports)
            {
                if (_mode == SetupMode.Diagnostics)
                {
                    string name = string.Join("  ·  ", new[] { report.Summary.ModelName, report.Summary.QueueName }.Where(v => v.Length > 0).DefaultIfEmpty(report.TargetKey));
                    AddPrinterRow(details, ref row, StatusGlyph.For(Readiness(report.Readiness)), T.Data(name), T.StatusName(Readiness(report.Readiness)));
                }

                foreach (var check in report.Checks)
                {
                    string? comparison = check.Expected.Length > 0 || check.Actual.Length > 0
                        ? T.Format("result.expectedActual", check.Expected, check.Actual)
                        : null;
                    AddCheckRow(details, ref row, StatusGlyph.For(check.Status), T.ItemName(check.Item), T.StatusName(check.Status), comparison);
                }
            }
        }

        details.RowCount = row;
        details.ResumeLayout();
    }

    private static CheckStatus Readiness(ReadinessLevel level) => level switch
    {
        ReadinessLevel.Ready => CheckStatus.Pass,
        ReadinessLevel.ReadyWithWarnings => CheckStatus.Warning,
        _ => CheckStatus.Fail,
    };

    /// <summary>
    /// One printer checked, as one row spanning both columns of the details grid (as the "Printers checked" heading does): its
    /// glyph, then "name · status", with some space above it. Its name does not follow the glyph column, which its checks' indented
    /// glyphs widen, so the checks' texts sit indented under it. Only this row has a small table of its own; a table per check,
    /// nested in two more, took seconds to lay out.
    /// </summary>
    private void AddPrinterRow(TableLayoutPanel details, ref int row, GlyphKind glyph, string name, string status)
    {
        var printer = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = S.Pad(0, 8, 0, 8),
            Padding = Padding.Empty,
        };
        printer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        printer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var icon = new GlyphBox(glyph, S.Px(16)) { Margin = S.Pad(0, 2, 10, 0) };
        var text = Ui.Paragraph(S, Theme.BodyStrong, Theme.Text, bottom: 0).With(name + "  ·  " + status);
        text.AccessibleName = $"{name}: {status}";
        printer.Controls.Add(icon, 0, 0);
        printer.Controls.Add(text, 1, 0);
        details.Controls.Add(printer, 0, row);
        details.SetColumnSpan(printer, 2);
        row++;
    }

    /// <summary>
    /// One check as rows of the details grid: its glyph, indented under its printer, then "name · status" with the expected and
    /// actual values under it. Screen readers hear the values with the check ("name: status. Expected: … Found: …").
    /// </summary>
    private void AddCheckRow(TableLayoutPanel details, ref int row, GlyphKind glyph, string name, string status, string? comparison)
    {
        var icon = new GlyphBox(glyph, S.Px(16)) { Margin = S.Pad(8, 2, 10, 0) };
        var text = Ui.Paragraph(S, Theme.Body, Theme.Text, bottom: comparison is null ? 8 : 0).With(name + "  ·  " + status);
        text.AccessibleName = comparison is null ? $"{name}: {status}" : $"{name}: {status}. {comparison}";
        details.Controls.Add(icon, 0, row);
        details.Controls.Add(text, 1, row++);
        if (comparison is not null)
            details.Controls.Add(Ui.Paragraph(S, Theme.Small, Theme.TextSecondary, bottom: 8).With(comparison), 1, row++);
    }

    private IEnumerable<Finding> AllFindings() =>
        _result.Findings.Concat(_result.Verification?.Findings ?? []).Concat(_result.Report?.Findings ?? []);

    /// <summary>
    /// The findings about the state the run ended in: once actions ran, what the plans found before them ("the driver will be
    /// installed", "unplug the USB cable") no longer holds and is left out. In Diagnostics they are about the ticket printer when
    /// it is known, not the other printers checked for the report. A result built outside a run has only its findings.
    /// </summary>
    private IEnumerable<Finding> CurrentFindings() => _result.CurrentFindings.Count > 0 || ActionsRan() ? _result.CurrentFindings : AllFindings();

    /// <summary>
    /// Whether any setup action ran (succeeded, already satisfied, skipped or failed): the plans' findings then describe the state
    /// before it. The helper starting the Print Spooler, before the printer is even detected, is not one.
    /// </summary>
    private bool ActionsRan() => _result.Report?.ApplyResults.Any(r => r.Results.Any(a => a.Action.Kind != ActionKind.StartSpooler)) == true;

    /// <summary>IT's model (--model with --confirm-model) that the printer contradicts: the run stopped itself with nothing changed.</summary>
    private static bool IsRefusedModel(Finding finding) =>
        finding is { Code: FindingCode.NetworkIdentityMismatch, Severity: Severity.Error } && finding.Data.ContainsKey("assertedModel");

    /// <summary>Repair found no printer it can change and says to run Setup (suggestedMode "setup"), which adds the printer to Windows.</summary>
    private static bool IsSetupNeeded(Finding finding) =>
        finding is { Code: FindingCode.NoSupportedPrinterFound, Severity: Severity.Error } && finding.Data.GetValueOrDefault("suggestedMode") == "setup";

    /// <summary>
    /// Why <paramref name="finding"/>, a NoSupportedPrinterFound error, found no printer (<see cref="NoPrinterReason"/>): the
    /// configured "address" it names, with the queue left unchanged there when there is one, else Repair's "reason". None for any
    /// other finding, and for a NoSupportedPrinterFound error that gives no reason (the read-back did not find the printer again).
    /// </summary>
    internal static NoPrinterReason ReasonOf(Finding finding)
    {
        if (finding is not { Code: FindingCode.NoSupportedPrinterFound, Severity: Severity.Error })
            return NoPrinterReason.None;
        if (finding.Data.ContainsKey("address"))
            return finding.Data.ContainsKey("leftUnchanged") ? NoPrinterReason.LeftUnchanged : NoPrinterReason.Address;
        return finding.Data.GetValueOrDefault("reason") switch
        {
            "noQueue" => NoPrinterReason.NoQueue,
            "modelNotConfirmed" => NoPrinterReason.ModelNotConfirmed,
            "printersWithoutQueue" => NoPrinterReason.PrintersWithoutQueue,
            "noPrinter" => NoPrinterReason.NoPrinter,
            _ => NoPrinterReason.None,
        };
    }

    /// <summary>Why the run of <paramref name="result"/> found no printer: its first NoSupportedPrinterFound error that says why.</summary>
    internal static NoPrinterReason ReasonOf(FlowResult result) =>
        result.Findings.Select(ReasonOf).FirstOrDefault(r => r != NoPrinterReason.None);

    /// <summary>The printer this result is about has no queue: neither its detection nor the read-back found one.</summary>
    private bool WithoutQueue() => _result.Target is { Queue: null } && string.IsNullOrEmpty(_result.Verification?.Summary.QueueName);

    /// <summary>
    /// Whether Repair can change the printer this result is about. Repair never adds a printer to Windows, so it is not offered
    /// for a printer without a queue (a Setup that ended before adding it, Diagnostics of a printer never set up). A Diagnostics
    /// result about no single printer offers it only when a printer Repair would ask about has a queue of its own: Repair asks
    /// about those alone (a connection to a print server is managed there). Nothing usable at the configured address, printers
    /// none of which was set up, and no printer at all leave Repair nothing to change.
    /// </summary>
    private bool RepairCanChange() => _result.Target is null
        ? PrintersToAskAbout().Any(p => p.Queue is { IsServerConnection: false })
        : !WithoutQueue();

    /// <summary>
    /// The printers a Diagnostics result about no single printer leaves Repair to ask about, as the report detected them: the
    /// printers one of which is the ticket printer (the MultipleSupportedPrinters warning's "choices"), or the ticket printer whose
    /// model must be confirmed (the ProbableMatchRequiresConfirmation error's "target"). Empty for any other result.
    /// </summary>
    private IReadOnlyList<PrinterCandidate> PrintersToAskAbout()
    {
        var undecided = AllFindings().Where(f => f is { Code: FindingCode.MultipleSupportedPrinters or FindingCode.ProbableMatchRequiresConfirmation }
            && f.Severity != Severity.Info).ToList();
        return (_result.Report?.Candidates ?? []).Where(candidate => undecided.Any(f => f.Code == FindingCode.MultipleSupportedPrinters
            ? f.Data.TryGetValue("choices", out string? choices) && Lists(choices, candidate.Key)
            : string.Equals(f.Data.GetValueOrDefault("target"), candidate.Key, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Whether <paramref name="list"/>, keys joined with ", " as the flow writes them, holds <paramref name="key"/>.</summary>
    private static bool Lists(string list, string key) => $", {list}, ".Contains($", {key}, ", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Read before others of the same severity: a driver package that failed its integrity or signature check may be tampered
    /// with, and an invalid configuration makes every other result doubtful.
    /// </summary>
    private static bool ComesFirst(FindingCode code) =>
        code is FindingCode.DriverPackageIntegrityFailed or FindingCode.DriverSignatureInvalid or FindingCode.ConfigurationInvalid;

    /// <summary>
    /// What ended a run that failed: the elevated helper could not start, or gave no result. It is read before what the read-back
    /// found afterwards ("Driver not installed" after a helper whose connection broke).
    /// </summary>
    private bool EndedTheRun(Finding finding) =>
        _result.Outcome == FlowOutcome.Failed && finding.Code is FindingCode.OperationFailed or FindingCode.ElevationFailed;

    /// <summary>
    /// The error (else warning) the employee should read first; a fallback per outcome when none was reported. Diagnostics takes
    /// its errors too from the findings about the ticket printer: the other printers are checked only for the report, and their
    /// problems are not the ticket printer's.
    /// </summary>
    private Finding? MostImportantFinding()
    {
        // An error holds at the end whenever it was found (a blocker stops what follows it); a warning only while it still
        // describes the end state, not "the driver will be installed" once it was. A sign of tampering comes first, then the
        // failure that ended a failed run.
        var errors = _mode == SetupMode.Diagnostics ? CurrentFindings() : AllFindings();
        var reported = errors.Where(f => f.Severity == Severity.Error)
            .Concat(CurrentFindings().Where(f => f.Severity == Severity.Warning))
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => ComesFirst(f.Code))
            .ThenByDescending(EndedTheRun)
            .FirstOrDefault();
        if (reported is not null)
            return reported;

        FindingCode? fallback = _result.Outcome switch
        {
            FlowOutcome.ElevationDeclined => FindingCode.ElevationDeclined,
            FlowOutcome.NoPrinter => FindingCode.NoSupportedPrinterFound,
            FlowOutcome.Cancelled => FindingCode.Cancelled,
            FlowOutcome.Failed => FindingCode.OperationFailed,
            _ => null,
        };
        return fallback is { } code ? new Finding(code, Severity.Error, "") : null;
    }

    private int? Win32ErrorFor(Finding finding)
    {
        foreach (string key in new[] { "win32Error", "win32" })
        {
            if (finding.Data.TryGetValue(key, out string? text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return value;
        }

        return _result.Report?.ApplyResults
            .SelectMany(r => r.Results)
            .Where(r => r.Outcome == ActionOutcome.Failed && r.Win32Error is not null)
            .OrderByDescending(r => r.FailureCode == finding.Code)
            .Select(r => r.Win32Error)
            .FirstOrDefault();
    }
}

internal static class LabelExtensions
{
    public static Label With(this Label label, string text)
    {
        label.Text = text;
        return label;
    }
}
