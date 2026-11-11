using Amwag.PrinterSetup.App.Localization;
using Amwag.PrinterSetup.App.Support;
using Amwag.PrinterSetup.Core.Configuration;

namespace Amwag.PrinterSetup.App.UI;

/// <summary>
/// Contact IT: the employee chooses the IT Department member to write to on WhatsApp. Choosing one runs <c>send</c> (prepare
/// the picture and the logs, open the chat); the dialog then says how to attach them in the chat and can show the files.
/// </summary>
internal sealed class ContactItDialog : Form
{
    private readonly Strings _t;
    private readonly Func<IWin32Window, ItContact, SupportPackage?> _send;
    private readonly Action<SupportPackage> _showFiles;
    private readonly FlowLayoutPanel _stack;
    private readonly Label _heading;
    private readonly Label _text;
    private readonly Label _note;
    private readonly FlowLayoutPanel _choices;
    private readonly FlowLayoutPanel _finish;
    private readonly ActionButton _cancel;
    private readonly ActionButton _done;
    private SupportPackage? _package;

    /// <param name="send">Prepares the files and opens the chat (it tells the employee itself what failed); null when nothing was prepared.</param>
    public ContactItDialog(Strings strings, IReadOnlyList<ItContact> contacts, Func<IWin32Window, ItContact, SupportPackage?> send, Action<SupportPackage> showFiles)
    {
        _t = strings;
        _send = send;
        _showFiles = showFiles;
        var s = UiScale.For(this);
        int width = s.Px(400);

        SuspendLayout();
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(DeviceDpi, DeviceDpi);
        Text = strings.Text("contact.title");
        Font = Theme.Body;
        BackColor = Theme.Background;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Padding = s.Pad(24, 20, 24, 16);
        RightToLeft = strings.IsRightToLeft ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = strings.IsRightToLeft;

        Label Paragraph(Font font, Color color, int bottom) => new()
        {
            AutoSize = true,
            MaximumSize = new Size(width, 0),
            Font = font,
            ForeColor = color,
            Margin = s.Pad(0, 0, 0, bottom),
            UseMnemonic = false,
        };

        _heading = Paragraph(Theme.ViewTitle, Theme.Text, 8);
        _heading.Text = strings.Text("contact.title");
        _text = Paragraph(Theme.Body, Theme.TextSecondary, 16);
        _text.Text = strings.Text("contact.text");
        _note = Paragraph(Theme.Small, Theme.TextSecondary, 16);
        _note.Visible = false;

        _choices = Column();
        foreach (var contact in contacts)
        {
            var choice = new ActionButton(ButtonKind.Secondary, s)
            {
                Text = NameOf(contact, strings),
                MinimumSize = new Size(width, s.Px(40)),
                Margin = s.Pad(0, 0, 0, 8),
            };
            choice.Click += (_, _) => Send(contact);
            _choices.Controls.Add(choice);
        }
        _cancel = new ActionButton(ButtonKind.Ghost, s) { Text = strings.Text("ui.cancel"), DialogResult = DialogResult.Cancel, Margin = s.Pad(0, 4, 0, 0) };
        _choices.Controls.Add(_cancel);

        _done = new ActionButton(ButtonKind.Primary, s) { Text = strings.Text("contact.done"), DialogResult = DialogResult.OK };
        var showFilesButton = new ActionButton(ButtonKind.Secondary, s) { Text = strings.Text("contact.showFiles") };
        showFilesButton.Click += (_, _) =>
        {
            if (_package is not null)
                _showFiles(_package);
        };
        _finish = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty, Padding = Padding.Empty, Visible = false };
        _finish.Controls.Add(_done);
        _finish.Controls.Add(showFilesButton);

        _stack = Column();
        _stack.Dock = DockStyle.Fill;
        _stack.Controls.AddRange([_heading, _text, _note, _choices, _finish]);
        Controls.Add(_stack);

        CancelButton = _cancel;
        FitToContent();
        ResumeLayout(false);
    }

    /// <summary>The contact's name in the window's language.</summary>
    public static string NameOf(ItContact contact, Strings strings) => strings.IsRightToLeft ? contact.NameArabic : contact.Name;

    private void Send(ItContact contact)
    {
        // Writing the files takes a moment on the window's thread.
        Cursor.Current = Cursors.WaitCursor;
        SupportPackage? package;
        try
        {
            package = _send(this, contact);
        }
        finally
        {
            Cursor.Current = Cursors.Default;
        }
        if (package is null)
            return;

        _package = package;
        _heading.Text = _t.Text("contact.sent.title");
        _text.Text = _t.Format("contact.sent.text", NameOf(contact, _t));
        _note.Text = _t.Text("contact.sent.note");
        _note.Visible = true;
        _choices.Visible = false;
        _finish.Visible = true;
        AcceptButton = _done;
        CancelButton = _done;
        FitToContent();
        _done.Focus();
    }

    private void FitToContent()
    {
        var content = _stack.GetPreferredSize(Size.Empty);
        ClientSize = new Size(content.Width + Padding.Horizontal, content.Height + Padding.Vertical);
    }

    private static FlowLayoutPanel Column() => new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        Margin = Padding.Empty,
        Padding = Padding.Empty,
    };
}
