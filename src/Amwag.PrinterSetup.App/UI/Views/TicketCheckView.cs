using System.Globalization;
using Amwag.PrinterSetup.App.TestTicket;

namespace Amwag.PrinterSetup.App.UI.Views;

internal sealed record TicketCheckCommands(Action CheckAgain, Action Back);

internal enum TicketCheckStage { Watching, Finished, Failed }

/// <summary>
/// "Check ticket software": asks for one real ticket from the ticket software, shows the live wait, then compares the
/// settings Windows received with that print job against the AMWAG Ticket defaults, with what to change when they differ.
/// The check only reads; this view says so.
/// </summary>
internal sealed class TicketCheckView : ViewBase
{
    private const string IntegrationGuide = "docs/TICKET-SOFTWARE-INTEGRATION.md";

    private readonly string _queueName;
    private readonly TicketCheckCommands _commands;
    private readonly TableLayoutPanel _content;
    private readonly FlowLayoutPanel _buttons;
    private TicketCheckStage _stage = TicketCheckStage.Watching;
    private TicketCheckProgress? _progress;
    private TicketCheckResult? _result;
    private string? _failure;
    private Label? _status;
    private IButtonControl? _accept;
    private IButtonControl? _cancel;

    public TicketCheckView(ViewContext context, string queueName, TicketCheckCommands commands) : base(context)
    {
        _queueName = queueName;
        _commands = commands;
        (_content, var bar) = CreateFrame();
        _buttons = Ui.ButtonRow(S);
        bar.Controls.Add(_buttons);
        Localize();
    }

    public TicketCheckStage Stage => _stage;

    public TicketCheckResult? Result => _result;

    public override IButtonControl? AcceptAction => _accept;
    public override IButtonControl? CancelAction => _cancel;

    /// <summary>Raised when the buttons change, so the window can re-bind Enter/Escape and focus.</summary>
    public event EventHandler? ActionsChanged;

    /// <summary>Waiting for the job; repeated progress only updates the status line.</summary>
    public void ShowWatching(TicketCheckProgress? progress)
    {
        _progress = progress;
        if (_stage == TicketCheckStage.Watching && _status is { IsDisposed: false })
        {
            _status.Text = StatusText();
            return;
        }

        _stage = TicketCheckStage.Watching;
        Localize();
    }

    public void ShowResult(TicketCheckResult result)
    {
        _result = result;
        _stage = TicketCheckStage.Finished;
        Localize();
    }

    public void ShowFailed(string technicalDetail)
    {
        _failure = technicalDetail;
        _stage = TicketCheckStage.Failed;
        Localize();
    }

    public override void Localize()
    {
        _content.SuspendLayout();
        _buttons.SuspendLayout();
        foreach (Control old in _content.Controls.Cast<Control>().Concat(_buttons.Controls.Cast<Control>()).ToArray())
            old.Dispose();
        _status = null;
        _accept = null;
        _cancel = null;

        Heading(T.Text("ticketcheck.title"));
        switch (_stage)
        {
            case TicketCheckStage.Watching:
                Paragraph(T.Text("ticketcheck.instruction"), Theme.BodyStrong, Theme.Text, 8);
                Paragraph(T.Format("ticketcheck.explanation", _queueName));
                _status = Ui.Paragraph(S, Theme.Body, Theme.Text, bottom: 8).With(StatusText());
                _content.Controls.Add(_status);
                _content.Controls.Add(new Spinner(S) { Anchor = AnchorStyles.Left, Margin = S.Pad(0, 4, 0, 16), AccessibleName = T.Text("progress.accessible") });
                _cancel = Button(ButtonKind.Secondary, "ticketcheck.stop", _commands.Back);
                break;

            case TicketCheckStage.Finished:
                RenderResult(_result!);
                _accept = Button(ButtonKind.Primary, "ticketcheck.again", _commands.CheckAgain);
                _cancel = Button(ButtonKind.Secondary, "ticketcheck.back", _commands.Back);
                break;

            case TicketCheckStage.Failed:
                Note(NoteTone.Error, T.Text("ticketcheck.failed.title"), T.Text("ticketcheck.failed.text"));
                AddTechnical(_failure);
                _accept = Button(ButtonKind.Primary, "ticketcheck.again", _commands.CheckAgain);
                _cancel = Button(ButtonKind.Secondary, "ticketcheck.back", _commands.Back);
                break;
        }

        _buttons.ResumeLayout();
        _content.ResumeLayout();
        ActionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private string StatusText()
    {
        if (_progress?.JobId is not null)
            return T.Text("ticketcheck.reading");

        var remaining = _progress?.Remaining ?? TicketSoftwareCheck.DefaultTimeout;
        int seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        return T.Format("ticketcheck.waiting", string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}"));
    }

    private void RenderResult(TicketCheckResult result)
    {
        switch (result.Outcome)
        {
            case TicketCheckOutcome.JobCaptured when result.HasDifferences:
                Note(NoteTone.Warning, T.Text("ticketcheck.differs.title"), T.Text("ticketcheck.differs.text"));
                JobLine(result);
                _content.Controls.Add(SettingsTable(result.Rows));
                Guidance(result.Rows);
                break;

            case TicketCheckOutcome.JobCaptured:
                string text = T.Text("ticketcheck.ok.text");
                if (result.Rows.Any(r => r.Status == TicketSettingStatus.NotSpecified))
                    text += " " + T.Text("ticketcheck.ok.notSpecified");
                Note(NoteTone.Success, T.Text("ticketcheck.ok.title"), text);
                JobLine(result);
                _content.Controls.Add(SettingsTable(result.Rows));
                break;

            case TicketCheckOutcome.JobGoneBeforeRead:
                Note(NoteTone.Warning, T.Text("ticketcheck.gone.title"), T.Text("ticketcheck.gone.text"));
                JobLine(result);
                break;

            case TicketCheckOutcome.SettingsUnavailable:
                Note(NoteTone.Warning, T.Text("ticketcheck.noSettings.title"), T.Text("ticketcheck.noSettings.text"));
                JobLine(result);
                break;

            case TicketCheckOutcome.NoJob:
                Note(NoteTone.Warning, T.Text("ticketcheck.noJob.title"), T.Format("ticketcheck.noJob.text", result.QueueName));
                break;

            case TicketCheckOutcome.Cancelled:
                Note(NoteTone.Warning, T.Text("ticketcheck.cancelled.title"), T.Text("ticketcheck.cancelled.text"));
                break;

            default:
                Note(NoteTone.Error, T.Text("ticketcheck.unreadable.title"), T.Text("ticketcheck.unreadable.text"));
                AddTechnical(result.Detail);
                break;
        }
    }

    private void JobLine(TicketCheckResult result)
    {
        string? job = !string.IsNullOrWhiteSpace(result.Document) ? result.Document
            : result.JobId is { } id ? id.ToString(CultureInfo.InvariantCulture)
            : null;
        if (job is not null)
            Paragraph(T.Format("ticketcheck.job", job), Theme.Small, Theme.TextSecondary, 12);
    }

    /// <summary>One row per setting: status glyph, setting and its status, what the job asked for, and the ticket default.</summary>
    private CardPanel SettingsTable(IReadOnlyList<TicketSettingRow> rows)
    {
        var table = new TableLayoutPanel
        {
            ColumnCount = 4,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = Color.Transparent,
            GrowStyle = TableLayoutPanelGrowStyle.AddRows,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        table.Controls.Add(new Control { Width = 0, Height = 0, Margin = Padding.Empty, TabStop = false });
        table.Controls.Add(Header(T.Text("ticketcheck.column.setting")));
        table.Controls.Add(Header(T.Text("ticketcheck.column.requested")));
        table.Controls.Add(Header(T.Text("ticketcheck.column.expected")));

        foreach (var row in rows)
        {
            string setting = T.Text($"ticketcheck.setting.{row.Setting}");
            string status = T.Text($"ticketcheck.status.{row.Status}");

            table.Controls.Add(new GlyphBox(Glyph(row.Status), S.Px(16)) { Margin = S.Pad(0, 6, 10, 0), AccessibleName = status });

            var name = CellStack(S.Pad(0, 4, 16, 6));
            name.Controls.Add(Cell(setting, Theme.BodyStrong, Theme.Text, Padding.Empty));
            name.Controls.Add(Cell(status, Theme.Small, StatusColor(row.Status), Padding.Empty));
            name.AccessibleName = $"{setting}: {status}";
            table.Controls.Add(name);

            table.Controls.Add(ValueCell(row.Requested, row.Status == TicketSettingStatus.Differs ? Theme.Warning : Theme.Text, S.Pad(0, 4, 16, 6)));
            table.Controls.Add(ValueCell(row.Expected, Theme.Text, S.Pad(0, 4, 0, 6)));
        }

        var card = new CardPanel(S);
        card.Controls.Add(table);
        return card;
    }

    private Label Header(string text) => Cell(text, Theme.SmallStrong, Theme.TextSecondary, S.Pad(0, 0, 16, 4));

    /// <summary>A wrapping cell label, top-aligned so every cell of a row starts level with the row's glyph.</summary>
    private static Label Cell(string text, Font font, Color color, Padding margin) => new()
    {
        Text = text,
        AutoSize = true,
        Font = font,
        ForeColor = color,
        BackColor = Color.Transparent,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        Margin = margin,
        UseMnemonic = false,
    };

    /// <summary>What to change in the ticket software, one line per differing setting, then margins, scale and the IT guide.</summary>
    private void Guidance(IReadOnlyList<TicketSettingRow> rows)
    {
        Paragraph(T.Text("ticketcheck.guidance.title"), Theme.BodyStrong, Theme.Text, 8);
        foreach (var row in rows.Where(r => r.Status == TicketSettingStatus.Differs && r.Setting != TicketSetting.Scale))
        {
            string expected = ValueText(row.Expected);
            string? line = row.Setting switch
            {
                TicketSetting.Paper => T.Format("ticketcheck.guidance.paper", Context.Config.Ticket.FormName),
                TicketSetting.Orientation => T.Format("ticketcheck.guidance.orientation", expected),
                TicketSetting.Duplex => T.Format("ticketcheck.guidance.duplex", expected),
                TicketSetting.PaperSource => T.Format("ticketcheck.guidance.source", expected),
                _ => null,
            };
            if (line is not null)
                Bullet(GlyphKind.Warning, line);
        }

        string scale = rows.FirstOrDefault(r => r.Setting == TicketSetting.Scale) is { } scaleRow
            ? ValueText(scaleRow.Expected)
            : Context.Config.PrintPreferences.ScalePercent.ToString(CultureInfo.InvariantCulture) + "%";
        Bullet(rows.Any(r => r.Setting == TicketSetting.Scale && r.Status == TicketSettingStatus.Differs) ? GlyphKind.Warning : GlyphKind.Pending,
            T.Format("ticketcheck.guidance.scale", scale));
        Paragraph(T.Text("ticketcheck.guidance.docs"), Theme.Small, Theme.TextSecondary, 2);
        Paragraph(T.Data(IntegrationGuide), Theme.Small, Theme.TextSecondary, 8);
    }

    /// <summary>A row value as plain localized text (data not isolated), for use inside a sentence.</summary>
    private string ValueText(TicketValue value) =>
        value.Key is null ? string.Join(", ", value.Args) : string.Format(CultureInfo.InvariantCulture, T.Text(value.Key), value.Args.Cast<object?>().ToArray());

    /// <summary>
    /// A row value for a table cell, isolated for the reading direction. Data with several parts (a paper name and its size)
    /// gets one label per part: a size never wraps inside its numbers, and GDI draws the spaces of a short first line
    /// that ends in a line break too wide.
    /// </summary>
    private Control ValueCell(TicketValue value, Color color, Padding margin)
    {
        if (value.Key is not null || value.Args.Count == 1)
            return Cell(T.Data(ValueText(value)), Theme.Body, color, margin);

        var stack = CellStack(margin);
        foreach (string line in value.Args)
            stack.Controls.Add(Cell(T.Data(line), Theme.Body, color, Padding.Empty));
        return stack;
    }

    private static TableLayoutPanel CellStack(Padding margin)
    {
        var stack = Ui.Stack();
        stack.Dock = DockStyle.None;
        stack.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        stack.Margin = margin;
        stack.BackColor = Color.Transparent;
        return stack;
    }

    private static GlyphKind Glyph(TicketSettingStatus status) => status switch
    {
        TicketSettingStatus.Matches => GlyphKind.Succeeded,
        TicketSettingStatus.Differs => GlyphKind.Warning,
        TicketSettingStatus.NotSpecified => GlyphKind.Skipped,
        _ => GlyphKind.Unknown,
    };

    private static Color StatusColor(TicketSettingStatus status) => status switch
    {
        TicketSettingStatus.Matches => Theme.Success,
        TicketSettingStatus.Differs => Theme.Warning,
        _ => Theme.TextSecondary,
    };

    private void AddTechnical(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return;
        _content.Controls.Add(Ui.Paragraph(S, Theme.SmallStrong, Theme.TextSecondary, bottom: 4).With(T.Text("result.technical")));
        _content.Controls.Add(Technical(detail));
    }

    private void Heading(string text) => _content.Controls.Add(Ui.Heading(S).With(text));

    private void Paragraph(string text, Font? font = null, Color? color = null, int bottom = 16) =>
        _content.Controls.Add(Ui.Paragraph(S, font, color, bottom).With(text));

    private void Note(NoteTone tone, string title, string text)
    {
        var note = new NotePanel(S, tone);
        note.SetText(text, title);
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
        // The actions rebuild or replace this view, which disposes the clicked button: let the click finish first.
        button.Click += (_, _) => BeginInvoke(onClick);
        _buttons.Controls.Add(button);
        return button;
    }
}
