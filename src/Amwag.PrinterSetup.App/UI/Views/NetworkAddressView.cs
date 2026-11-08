using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.Core.Configuration;

namespace Amwag.PrinterSetup.App.UI.Views;

/// <summary>What the employee may type as a printer address: an IPv4/IPv6 address or an RFC 1123 host name.</summary>
internal static class NetworkAddressInput
{
    public const string Example = "192.168.1.50";

    /// <summary>True for a valid address; <paramref name="address"/> is then its canonical spelling (<see cref="PrinterAddress"/>).</summary>
    public static bool TryNormalize(string? input, out string address)
    {
        bool valid = PrinterAddress.TryNormalize(input, out string? normalized, out _);
        address = normalized ?? (input ?? "").Trim();
        return valid;
    }
}

/// <summary>Nothing usable was found (or an address failed): asks for an IP address or host name.</summary>
internal sealed class NetworkAddressView : ViewBase
{
    private readonly NetworkAddressPrompt _prompt;
    private readonly Action<string?> _answer;
    private readonly Label _heading;
    private readonly Label _text;
    private readonly NotePanel _problem;
    private readonly Label _fieldLabel;
    private readonly TextBox _address;
    private readonly Label _validation;
    private readonly Label _hint;
    private readonly ActionButton _find;
    private readonly ActionButton _cancel;
    private bool _showValidation;

    public NetworkAddressView(ViewContext context, NetworkAddressPrompt prompt, Action<string?> answer) : base(context)
    {
        _prompt = prompt;
        _answer = answer;
        var (content, bar) = CreateFrame();

        _heading = Ui.Heading(S);
        _text = Ui.Paragraph(S);
        _problem = new NotePanel(S, NoteTone.Warning) { Visible = prompt.PreviousProblem is not null };
        _fieldLabel = Ui.Paragraph(S, Theme.BodyStrong, Theme.Text, bottom: 4);
        _address = new TextBox
        {
            Font = Theme.Body,
            RightToLeft = RightToLeft.No, // addresses are always written left to right
            Anchor = AnchorStyles.Left,
            Width = S.Px(320),
            Margin = S.Pad(0, 0, 0, 4),
            Text = prompt.PreviousAddress ?? "",
            MaxLength = 253,
        };
        _validation = Ui.Paragraph(S, Theme.Small, Theme.Error, bottom: 4);
        _validation.Visible = false;
        _hint = Ui.Paragraph(S, Theme.Small, Theme.TextSecondary);

        content.Controls.Add(_heading);
        content.Controls.Add(_text);
        content.Controls.Add(_problem);
        content.Controls.Add(_fieldLabel);
        content.Controls.Add(_address);
        content.Controls.Add(_validation);
        content.Controls.Add(_hint);

        _find = new ActionButton(ButtonKind.Primary, S);
        _cancel = new ActionButton(ButtonKind.Secondary, S);
        _find.Click += (_, _) => Submit();
        _cancel.Click += (_, _) => _answer(null);
        _address.TextChanged += (_, _) =>
        {
            _showValidation = false;
            _validation.Visible = false;
        };
        var buttons = Ui.ButtonRow(S);
        buttons.Controls.Add(_find);
        buttons.Controls.Add(_cancel);
        bar.Controls.Add(buttons);

        Localize();
    }

    public override IButtonControl? AcceptAction => _find;
    public override IButtonControl? CancelAction => _cancel;
    public override Control? InitialFocus => _address;

    private void Submit()
    {
        if (NetworkAddressInput.TryNormalize(_address.Text, out string address))
        {
            _answer(address);
            return;
        }

        _showValidation = true;
        Localize();
        _address.Focus();
        _address.SelectAll();
    }

    public override void Localize()
    {
        _heading.Text = T.Text("network.title");
        _text.Text = T.Text(_prompt switch
        {
            // The employee chose none of the printers found: the ticket printer may be one that was not found.
            { OfferedPrintersDeclined: true } => "network.text.declined",
            { NothingDetected: true, PreviousProblem: null } => "network.text.nothing",
            _ => "network.text.retry",
        });
        if (_prompt.PreviousProblem is { } problem)
        {
            string text = T.FindingText(problem);
            if (!string.IsNullOrEmpty(_prompt.PreviousAddress))
                text += Environment.NewLine + T.Format("network.previous", _prompt.PreviousAddress);
            _problem.SetText(text, T.FindingTitle(problem));
        }
        _fieldLabel.Text = T.Text("network.label");
        _address.AccessibleName = T.Text("network.label");
        _validation.Text = T.Format("network.invalid", NetworkAddressInput.Example);
        _validation.Visible = _showValidation;
        _hint.Text = T.Format("network.hint", NetworkAddressInput.Example);
        _find.Text = T.Text("network.find");
        _cancel.Text = T.Text("ui.cancel");
    }
}
