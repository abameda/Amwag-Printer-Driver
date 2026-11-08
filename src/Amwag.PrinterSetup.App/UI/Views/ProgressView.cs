using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.UI.Views;

/// <summary>
/// What is happening right now: headline, current activity beside a spinner (a percentage while a package downloads), the printer once known, and a note: the UAC
/// prompt while it is expected, and while stopping what stopping means (a step running with administrator rights finishes).
/// </summary>
internal sealed class ProgressView : ViewBase
{
    private readonly SetupMode _mode;
    private readonly TableLayoutPanel _content;
    private readonly Label _heading;
    private readonly Label _explanation;
    private readonly Label _activity;
    private readonly Label _detail;
    private readonly Spinner _spinner;
    private readonly NotePanel _note;
    private CardPanel? _card;
    private ProgressUpdate? _last;
    private PrinterCandidate? _target;
    private bool _elevation;
    private bool _cancelling;

    public ProgressView(ViewContext context, SetupMode mode) : base(context)
    {
        _mode = mode;
        (_content, var bar) = CreateFrame();
        bar.Visible = false; // nothing to press while the flow runs; closing the window cancels

        _heading = Ui.Heading(S);
        _explanation = Ui.Paragraph(S, bottom: 20);
        _activity = Ui.Paragraph(S, Theme.BodyStrong, Theme.Text, bottom: 0);
        _spinner = new Spinner(S) { Anchor = AnchorStyles.Left, Margin = S.Pad(0, 0, 14, 0) };
        _detail = Technical("");
        _note = new NotePanel(S, NoteTone.Accent) { Visible = false };

        _content.Controls.Add(_heading);
        _content.Controls.Add(_explanation);
        _content.Controls.Add(Ui.ProgressRow(S, _spinner, _activity));
        _content.Controls.Add(_detail);
        _content.Controls.Add(_note);

        Localize();
    }

    /// <summary>The activity line and the note while it is meant to show (Control.Visible is false while the window is hidden).</summary>
    internal (string Activity, string? Note) Texts => (_activity.Text, _cancelling || _elevation ? _note.AccessibleName : null);

    public override IButtonControl? AcceptAction => null;
    public override IButtonControl? CancelAction => null;

    public void ShowProgress(ProgressUpdate update)
    {
        _last = update;
        _spinner.SetFraction(update.Fraction);

        // Once verification starts, the machine steps (and any UAC prompt) are over.
        if (update.Step is StepId.Verify or StepId.Ready && _elevation)
        {
            _elevation = false;
            UpdateNote();
        }
        UpdateActivity();
    }

    public void ShowTarget(PrinterCandidate target)
    {
        _target = target;
        RebuildCard();
    }

    public void ShowElevationNote(bool visible)
    {
        _elevation = visible;
        UpdateNote();
    }

    /// <summary>The window was closed during the run: it waits until the run has stopped and the result was read back.</summary>
    public void ShowCancelling()
    {
        _cancelling = true;
        UpdateNote();
        UpdateActivity();
    }

    public override void Localize()
    {
        _heading.Text = T.ProgressTitle(_mode);
        _explanation.Text = T.Text("progress.explanation");
        _spinner.AccessibleName = T.Text("progress.accessible");
        _detail.TextAlign = T.IsRightToLeft ? ContentAlignment.TopRight : ContentAlignment.TopLeft;
        UpdateNote();
        UpdateActivity();
        RebuildCard();
    }

    private void UpdateNote()
    {
        _note.SetText(T.Text(_cancelling ? "progress.cancelling.note" : "progress.elevation"));
        _note.Visible = _cancelling || _elevation;
    }

    private void UpdateActivity()
    {
        if (_cancelling)
            _activity.Text = T.Text("progress.cancelling");
        else if (_last is null)
            _activity.Text = T.StepStatus(StepId.Detect, StepState.Running);
        else if (_last.Action is null && _last.Phase == ProgressPhase.Started)
            _activity.Text = T.StepStatus(_last.Step, _last.State);
        else if (_last is { Phase: ProgressPhase.Waiting, Code: FindingCode.ApplyLockBusy })
            _activity.Text = T.Text("progress.waitingForOtherRun");
        else
            _activity.Text = T.ProgressText(_last.Action, _last.Phase);

        _detail.Text = TechnicalText(_last?.Detail);
        _detail.Visible = !string.IsNullOrWhiteSpace(_last?.Detail);
    }

    private void RebuildCard()
    {
        _card?.Dispose();
        _card = null;
        if (_target is null)
            return;

        _card = PrinterCard.Create(Context, S, _target, PrinterCardDetail.Progress);
        _content.Controls.Add(_card);
    }
}
