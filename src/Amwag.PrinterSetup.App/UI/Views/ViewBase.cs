using Amwag.PrinterSetup.App.Localization;
using Amwag.PrinterSetup.App.TestTicket;
using Amwag.PrinterSetup.Core.Configuration;

namespace Amwag.PrinterSetup.App.UI.Views;

/// <summary>What every view needs: the current language, the window's DPI scale and the ticket configuration.</summary>
internal sealed class ViewContext
{
    private readonly Control _window;

    public ViewContext(Control window, Strings strings, DeploymentConfig config)
    {
        _window = window;
        Strings = strings;
        Config = config;
    }

    /// <summary>Changes when the employee switches the language; views re-read it in <see cref="ViewBase.Localize"/>.</summary>
    public Strings Strings { get; set; }

    public DeploymentConfig Config { get; }

    public UiScale Scale => UiScale.For(_window);

    /// <summary>"AMWAG Ticket 82.30 x 170.18 mm (3.24 x 6.70 in)", or the bare form name if the configured size is invalid.</summary>
    public string TicketDescription
    {
        get
        {
            var ticket = Config.Ticket;
            return ticket.Width > 0 && ticket.Height > 0 ? TicketPaperText.Describe(ticket.FormName, ticket.Dimensions) : ticket.FormName;
        }
    }
}

/// <summary>One screen of the content area. Builds its controls once and re-applies texts on language changes.</summary>
internal abstract class ViewBase : UserControl
{
    protected ViewBase(ViewContext context)
    {
        Context = context;
        S = context.Scale;
        BackColor = Theme.Background;
        Dock = DockStyle.Fill;
        Padding = S.Pad(32, 24, 32, 16);
        Margin = Padding.Empty;
    }

    protected ViewContext Context { get; }

    protected UiScale S { get; }

    protected Strings T => Context.Strings;

    /// <summary>The button Enter presses on this view (the form's AcceptButton).</summary>
    public abstract IButtonControl? AcceptAction { get; }

    /// <summary>The button Escape presses on this view (the form's CancelButton).</summary>
    public abstract IButtonControl? CancelAction { get; }

    /// <summary>The control that gets keyboard focus when the view appears.</summary>
    public virtual Control? InitialFocus => AcceptAction as Control;

    public abstract void Localize();

    /// <summary>A scrolling content column above a fixed button bar, the layout every view shares.</summary>
    protected (TableLayoutPanel Content, TableLayoutPanel Bar) CreateFrame()
    {
        // Both are auto-sizing tables: a plain auto-size Panel cannot measure docked children and grows too tall.
        var bar = Ui.Stack(DockStyle.Bottom);
        bar.Padding = S.Pad(0, 12, 0, 0);
        var scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var content = Ui.Stack();
        scroller.Controls.Add(content);
        Controls.Add(scroller);
        Controls.Add(bar);
        return (content, bar);
    }

    /// <summary>
    /// Technical text (finding details, Windows errors), mostly English: left to right, aligned to the reading side. A line
    /// that starts in Arabic (a localized "Windows error" line, an Arabic Windows message) keeps reading right to left.
    /// </summary>
    protected Label Technical(string text)
    {
        var label = Ui.Paragraph(S, Theme.Small, Theme.TextSecondary).With(TechnicalText(text));
        label.RightToLeft = RightToLeft.No;
        label.TextAlign = T.IsRightToLeft ? ContentAlignment.TopRight : ContentAlignment.TopLeft;
        return label;
    }

    /// <summary>Text for a <see cref="Technical"/> label: every line isolated in its own direction inside the left-to-right label.</summary>
    protected static string TechnicalText(string? text) => Bidi.IsolateLines(text, rightToLeftContext: false);
}
