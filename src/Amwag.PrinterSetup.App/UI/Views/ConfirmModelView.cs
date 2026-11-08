using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.UI.Views;

/// <summary>A probable match: "A printer at 10.0.0.5 reports '...'. Is this a P-4030DN?" Yes answers true, No false.</summary>
internal sealed class ConfirmModelView : ViewBase
{
    private readonly PrinterCandidate _candidate;
    private readonly Label _heading;
    private readonly Label _question;
    private readonly Label _hint;
    private readonly ActionButton _yes;
    private readonly ActionButton _no;

    public ConfirmModelView(ViewContext context, PrinterCandidate candidate, Action<bool> answer) : base(context)
    {
        _candidate = candidate;
        var (content, bar) = CreateFrame();
        _heading = Ui.Heading(S);
        _question = Ui.Paragraph(S, Theme.Body, Theme.Text, bottom: 8);
        _hint = Ui.Paragraph(S);
        content.Controls.Add(_heading);
        content.Controls.Add(_question);
        content.Controls.Add(_hint);

        _yes = new ActionButton(ButtonKind.Primary, S);
        _no = new ActionButton(ButtonKind.Secondary, S);
        _yes.Click += (_, _) => answer(true);
        _no.Click += (_, _) => answer(false);
        var buttons = Ui.ButtonRow(S);
        buttons.Controls.Add(_yes);
        buttons.Controls.Add(_no);
        bar.Controls.Add(buttons);

        Localize();
    }

    public override IButtonControl? AcceptAction => _yes;
    public override IButtonControl? CancelAction => _no;

    /// <summary>The question as shown (for tests).</summary>
    internal string Question => _question.Text;

    public override void Localize()
    {
        var identity = _candidate.NetworkIdentity;
        string address = _candidate.NetworkAddress ?? identity?.Address ?? _candidate.Queue?.PortName ?? "";
        string model = _candidate.Model is { } m ? (m.DisplayName.Length > 0 ? m.DisplayName : m.Id) : "";

        _heading.Text = T.Text("confirm.title");
        _question.Text = T.Format("confirm.text", address, Reported(_candidate), model);
        _hint.Text = T.Text("confirm.hint");
        _yes.Text = T.Text("ui.yes");
        _no.Text = T.Text("ui.no");
    }

    /// <summary>
    /// What the printer reports about itself: its Kyocera model name (kcprtGeneralModelName), else its device description, else
    /// sysDescr (without a network identity, its device node's description). The printer name (prtGeneralPrinterName) is
    /// assigned by an administrator, often after the printer a device replaced, so it is never presented as what the printer is.
    /// </summary>
    private static string Reported(PrinterCandidate candidate)
    {
        var identity = candidate.NetworkIdentity;
        string?[] reported = [identity?.VendorModelName, identity?.DeviceDescription, identity?.SysDescr, candidate.Device?.Description];
        return reported.FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))?.Trim() ?? "";
    }
}
