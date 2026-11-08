using Amwag.PrinterSetup.App.TestTicket;

namespace Amwag.PrinterSetup.App.UI.Views;

internal sealed record TestPrintCommands(Action<bool> Print, Action Repair, Action Back, Action<bool> Answer);

internal enum TestPrintStage { Confirm, PrecheckWarning, Working, Finished, Failed, AnsweredOk, AnsweredProblem }

/// <summary>
/// The test print, start to finish: load paper, (warning if the ticket defaults are not active), live spooler status,
/// then the physical check the employee confirms. It never says the ticket printed; only the employee can confirm that.
/// </summary>
internal sealed class TestPrintView : ViewBase
{
    private readonly TestPrintCommands _commands;
    private readonly TableLayoutPanel _content;
    private readonly FlowLayoutPanel _buttons;
    private TestPrintStage _stage = TestPrintStage.Confirm;
    private TestPrintPrecheck? _precheck;
    private JobObservation? _observation;
    private JobMonitorResult? _result;
    private TestPrintSubmission? _submission;
    private string? _failure;
    private IButtonControl? _accept;
    private IButtonControl? _cancel;

    public TestPrintView(ViewContext context, TestPrintCommands commands) : base(context)
    {
        _commands = commands;
        (_content, var bar) = CreateFrame();
        _buttons = Ui.ButtonRow(S);
        bar.Controls.Add(_buttons);
        Localize();
    }

    public TestPrintStage Stage => _stage;

    public override IButtonControl? AcceptAction => _accept;
    public override IButtonControl? CancelAction => _cancel;

    /// <summary>Raised when the buttons change, so the window can re-bind Enter/Escape and focus.</summary>
    public event EventHandler? ActionsChanged;

    public void ShowConfirm() => Go(TestPrintStage.Confirm);

    public void ShowPrecheckWarning(TestPrintPrecheck precheck)
    {
        _precheck = precheck;
        Go(TestPrintStage.PrecheckWarning);
    }

    public void ShowWorking(JobObservation? observation)
    {
        _observation = observation;
        Go(TestPrintStage.Working);
    }

    public void ShowFinished(TestPrintSubmission submission, JobMonitorResult result)
    {
        _submission = submission;
        _result = result;
        Go(TestPrintStage.Finished);
    }

    public void ShowFailed(string technicalDetail)
    {
        _failure = technicalDetail;
        Go(TestPrintStage.Failed);
    }

    public void ShowAnswer(bool printedCorrectly) => Go(printedCorrectly ? TestPrintStage.AnsweredOk : TestPrintStage.AnsweredProblem);

    private void Go(TestPrintStage stage)
    {
        _stage = stage;
        Localize();
    }

    public override void Localize()
    {
        _content.SuspendLayout();
        _buttons.SuspendLayout();
        foreach (Control old in _content.Controls.Cast<Control>().Concat(_buttons.Controls.Cast<Control>()).ToArray())
            old.Dispose();
        _accept = null;
        _cancel = null;

        switch (_stage)
        {
            case TestPrintStage.Confirm:
                Heading(T.Text("testprint.title"));
                Paragraph(T.Text("testprint.load"), Theme.BodyStrong, Theme.Text, 8);
                Paragraph(T.Text("testprint.loadHint"));
                _accept = Button(ButtonKind.Primary, "testprint.print", () => _commands.Print(false));
                _cancel = Button(ButtonKind.Secondary, "testprint.back", _commands.Back);
                break;

            case TestPrintStage.PrecheckWarning:
                Heading(T.Text("testprint.title"));
                Note(NoteTone.Warning, T.Text("testprint.precheck.title"),
                    _precheck?.ActualFormName is { Length: > 0 } actual
                        ? T.Format("testprint.precheck.mismatch", actual, _precheck.ExpectedFormName)
                        : T.Text("testprint.precheck.unreadable"));
                _accept = Button(ButtonKind.Primary, "result.repair", _commands.Repair);
                Button(ButtonKind.Secondary, "testprint.printAnyway", () => _commands.Print(true));
                _cancel = Button(ButtonKind.Secondary, "testprint.back", _commands.Back);
                break;

            case TestPrintStage.Working:
                Heading(T.Text("testprint.title"));
                Paragraph(_observation is null ? T.Text("testprint.sending") : T.Text($"testprint.stage.{_observation.Stage}"), Theme.BodyStrong, Theme.Text, 8);
                _content.Controls.Add(new Spinner(S) { Anchor = AnchorStyles.Left, Margin = S.Pad(0, 4, 0, 16), AccessibleName = T.Text("progress.accessible") });
                if (_observation is { Problems: not JobProblems.None } observation)
                    Note(NoteTone.Warning, T.Text("testprint.problem.title"), ProblemText(observation.Problems));
                _cancel = Button(ButtonKind.Secondary, "testprint.back", _commands.Back);
                break;

            case TestPrintStage.Finished:
                RenderFinished();
                break;

            case TestPrintStage.Failed:
                Heading(T.Text("testprint.title"));
                Note(NoteTone.Error, T.Text("testprint.failed.title"), T.Text("testprint.failed.text"));
                if (!string.IsNullOrWhiteSpace(_failure))
                {
                    _content.Controls.Add(Ui.Paragraph(S, Theme.SmallStrong, Theme.TextSecondary, bottom: 4).With(T.Text("result.technical")));
                    _content.Controls.Add(Technical(_failure));
                }
                _accept = Button(ButtonKind.Primary, "testprint.printAgain", () => _commands.Print(false));
                _cancel = Button(ButtonKind.Secondary, "testprint.back", _commands.Back);
                break;

            case TestPrintStage.AnsweredOk:
                Heading(T.Text("testprint.title"));
                Note(NoteTone.Success, T.Text("testprint.answer.ok"));
                _accept = Button(ButtonKind.Primary, "testprint.done", _commands.Back);
                _cancel = _accept;
                break;

            case TestPrintStage.AnsweredProblem:
                Heading(T.Text("testprint.title"));
                Paragraph(T.Text("testprint.answer.problem"), Theme.BodyStrong, Theme.Text, 12);
                foreach (string key in new[] { "testprint.guidance.misaligned", "testprint.guidance.size", "testprint.guidance.cutoff", "testprint.guidance.tray" })
                    Bullet(GlyphKind.Warning, T.Text(key));
                _accept = Button(ButtonKind.Primary, "result.repair", _commands.Repair);
                Button(ButtonKind.Secondary, "testprint.printAgain", () => _commands.Print(false));
                _cancel = Button(ButtonKind.Secondary, "testprint.done", _commands.Back);
                break;
        }

        _buttons.ResumeLayout();
        _content.ResumeLayout();
        ActionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RenderFinished()
    {
        var result = _result!;
        if (result.Outcome == JobMonitorOutcome.Problem)
        {
            Heading(T.Text("testprint.title"));
            Note(NoteTone.Error, T.Text("testprint.problem.title"), ProblemText(result.Problems));
            _accept = Button(ButtonKind.Primary, "testprint.printAgain", () => _commands.Print(false));
            _cancel = Button(ButtonKind.Secondary, "testprint.back", _commands.Back);
            return;
        }

        Heading(T.Text(result.Outcome == JobMonitorOutcome.StillQueued ? "testprint.stillQueued.title" : "testprint.sent.title"));
        if (result.Outcome == JobMonitorOutcome.StillQueued)
            Note(NoteTone.Warning, T.Text("testprint.stillQueued.text"));
        Paragraph(T.Text("testprint.sent.text"), Theme.BodyStrong, Theme.Text, 8);
        foreach (string key in new[] { "testprint.check.corners", "testprint.check.bars", "testprint.check.text", "testprint.check.tray" })
            Bullet(GlyphKind.Pending, T.Text(key));
        if (_submission?.Paper is { } paper)
            Paragraph(T.Format("testprint.paperUsed", paper.Describe()), Theme.Small, Theme.TextSecondary, 16);

        _accept = Button(ButtonKind.Primary, "testprint.ok", () => _commands.Answer(true));
        Button(ButtonKind.Secondary, "testprint.notOk", () => _commands.Answer(false));
        _cancel = Button(ButtonKind.Ghost, "testprint.back", _commands.Back);
    }

    private string ProblemText(JobProblems problems)
    {
        var texts = Enum.GetValues<JobProblems>()
            .Where(p => p != JobProblems.None && problems.HasFlag(p))
            .Select(p => T.Text($"testprint.problem.{p}"))
            .ToList();
        return texts.Count == 0 ? T.Text("testprint.problem.PrinterError") : string.Join(Environment.NewLine, texts);
    }

    private void Heading(string text) => _content.Controls.Add(Ui.Heading(S).With(text));

    private void Paragraph(string text, Font? font = null, Color? color = null, int bottom = 16) =>
        _content.Controls.Add(Ui.Paragraph(S, font, color, bottom).With(text));

    private void Note(NoteTone tone, string titleOrText, string? text = null)
    {
        var note = new NotePanel(S, tone);
        if (text is null)
            note.SetText(titleOrText);
        else
            note.SetText(text, titleOrText);
        _content.Controls.Add(note);
    }

    private void Bullet(GlyphKind glyph, string text)
    {
        var row = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left | AnchorStyles.Right, Margin = S.Pad(0, 0, 0, 8) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.Controls.Add(new GlyphBox(glyph, S.Px(16)) { Margin = S.Pad(0, 2, 10, 0) }, 0, 0);
        row.Controls.Add(Ui.Paragraph(S, Theme.Body, Theme.Text, bottom: 0).With(text), 1, 0);
        row.AccessibleName = text;
        _content.Controls.Add(row);
    }

    private ActionButton Button(ButtonKind kind, string key, Action onClick)
    {
        var button = new ActionButton(kind, S) { Text = T.Text(key) };
        // Most actions rebuild this view, which disposes the clicked button: let the click finish first.
        button.Click += (_, _) => BeginInvoke(onClick);
        _buttons.Controls.Add(button);
        return button;
    }
}
