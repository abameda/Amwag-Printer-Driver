using System.ComponentModel;
using System.Globalization;
using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Localization;
using Amwag.PrinterSetup.App.UI.Views;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.UI;

/// <summary>The single application window: runs the setup flow automatically and shows progress, questions and results.</summary>
public sealed partial class MainForm : Form
{
    private const string WindowTitle = "AMWAG Printer Setup";

    private readonly ViewContext _context;
    private readonly Label _title;
    private readonly Label _subtitle;
    private readonly ActionButton? _contactIt;
    private readonly ActionButton _language;
    private readonly TableLayoutPanel _root;
    private readonly StepList _steps;
    private readonly Panel _host;
    private readonly FooterBar _footer;
    private readonly UiInteraction _interaction;
    private ViewBase? _view;
    private SetupMode _mode;

    public MainForm(AppRuntime runtime)
    {
        Runtime = runtime;
        _mode = runtime.CommandLine.Mode;
        var language = UiLanguages.Resolve(runtime.CommandLine.Language, CultureInfo.CurrentUICulture);
        _context = new ViewContext(this, Strings.For(language), runtime.Environment.Config);
        var s = UiScale.For(this);

        SuspendLayout();
        // Every size below is already in device pixels for this DPI, so the initial auto-scale factor is 1;
        // Dpi mode still lets Windows rescale the window when it moves to a monitor with another DPI.
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(DeviceDpi, DeviceDpi);
        Text = WindowTitle;
        Font = Theme.Body;
        BackColor = Theme.Background;
        StartPosition = FormStartPosition.CenterScreen;
        // 36 px of the height are the footer, so the views keep the 840 x 600 they were laid out for.
        ClientSize = new Size(s.Px(840), s.Px(636));
        MinimumSize = SizeFromClientSize(new Size(s.Px(760), s.Px(576)));
        Icon = LoadApplicationIcon();

        _title = new Label { Text = WindowTitle, AutoSize = true, Font = Theme.HeaderTitle, ForeColor = Color.White, BackColor = Color.Transparent, Margin = Padding.Empty, UseMnemonic = false };
        _subtitle = new Label { AutoSize = true, Font = Theme.HeaderSubtitle, ForeColor = Theme.OnNavySecondary, BackColor = Color.Transparent, Margin = s.Pad(0, 2, 0, 0), UseMnemonic = false };
        _language = new ActionButton(ButtonKind.OnDark, s) { Anchor = AnchorStyles.None, Margin = Padding.Empty, MinimumSize = new Size(s.Px(88), s.Px(32)) };
        _language.Click += (_, _) => SwitchLanguage();
        var headerActions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Anchor = AnchorStyles.None, Margin = Padding.Empty, Padding = Padding.Empty };
        // Contact IT is offered only when the configuration names IT Department members to write to.
        if (runtime.Environment.Config.ItContacts.Count > 0)
        {
            _contactIt = new ActionButton(ButtonKind.OnDark, s) { Anchor = AnchorStyles.None, Margin = s.Pad(0, 0, 8, 0), MinimumSize = new Size(s.Px(88), s.Px(32)) };
            _contactIt.Click += (_, _) => ContactIt();
            headerActions.Controls.Add(_contactIt);
        }
        headerActions.Controls.Add(_language);

        var titles = Ui.Stack();
        titles.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        titles.Dock = DockStyle.None;
        titles.Controls.Add(_title);
        titles.Controls.Add(_subtitle);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Theme.Navy,
            ColumnCount = 3,
            RowCount = 1,
            Padding = s.Pad(24, 14, 24, 14),
            Margin = Padding.Empty,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new HeaderMark(new Size(s.Px(104), s.Px(30))) { Anchor = AnchorStyles.None, Margin = s.Pad(0, 0, 16, 0) }, 0, 0);
        header.Controls.Add(titles, 1, 0);
        header.Controls.Add(headerActions, 2, 0);

        _steps = new StepList(_context.Strings) { Dock = DockStyle.Fill, Margin = Padding.Empty };
        _host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = Padding.Empty };

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, s.Px(248)));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(_steps, 0, 0);
        body.Controls.Add(_host, 1, 0);

        _footer = new FooterBar(s, runtime.Log);

        _root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = Padding.Empty };
        _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _root.RowStyles.Add(new RowStyle(SizeType.Absolute, s.Px(36)));
        _root.Controls.Add(header, 0, 0);
        _root.Controls.Add(body, 0, 1);
        _root.Controls.Add(_footer, 0, 2);
        Controls.Add(_root);

        ApplyLanguage(language);
        ResumeLayout(false);

        // The first control created on this thread installed the WinForms synchronization context.
        _interaction = new UiInteraction(this, SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext());
    }

    public AppRuntime Runtime { get; }

    private Strings T => _context.Strings;

    internal UiLanguage Language => _context.Strings.Language;

    internal ViewBase? CurrentView => _view;

    internal StepList Steps => _steps;

    internal ActionButton? ContactItButton => _contactIt;

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ = RunFlowAsync(_mode);
    }

    internal void SwitchLanguage()
    {
        var language = Language == UiLanguage.English ? UiLanguage.Arabic : UiLanguage.English;
        ApplyLanguage(language);
        Runtime.Log.Info("ui.language.changed", "The employee switched the window language.", new Dictionary<string, object?> { ["language"] = language.ToString() });
    }

    private void ApplyLanguage(UiLanguage language)
    {
        _context.Strings = Strings.For(language);
        bool rtl = language == UiLanguage.Arabic;
        RightToLeft = rtl ? RightToLeft.Yes : RightToLeft.No;
        RightToLeftLayout = rtl;

        _subtitle.Text = T.ModeSubtitle(_mode);
        _language.Text = rtl ? "English" : "العربية";
        _language.AccessibleName = T.Text("header.language.accessible");
        if (_contactIt is not null)
        {
            _contactIt.Text = T.Text("header.contactIt");
            _contactIt.AccessibleName = T.Text("header.contactIt.accessible");
        }
        _steps.SetStrings(T);
        _footer.SetStrings(T);
        _view?.Localize();
        if (_progress is not null && _progress != _view)
            _progress.Localize();
    }

    /// <summary>Shows one view in the content area; the previous one is disposed after the current message (it may be mid-click).</summary>
    private void ShowView(ViewBase view)
    {
        var previous = _view;
        if (previous == view)
            return;

        _view = view;
        _host.SuspendLayout();
        _host.Controls.Add(view);
        if (previous is not null)
        {
            _host.Controls.Remove(previous);
            if (previous != _progress)
                DisposeLater(previous);
        }
        _host.ResumeLayout();

        BindActions(view);
    }

    private void BindActions(ViewBase view)
    {
        AcceptButton = view.AcceptAction;
        CancelButton = view.CancelAction;
        if (IsHandleCreated && view.InitialFocus is { } focus)
            BeginInvoke(() =>
            {
                if (_view == view && !focus.IsDisposed && focus.CanFocus)
                    focus.Focus();
            });
    }

    private void DisposeLater(Control control)
    {
        if (IsHandleCreated)
            BeginInvoke(control.Dispose);
        else
            control.Dispose();
    }

    private bool Confirm(string text, string title)
    {
        var options = T.IsRightToLeft ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0;
        return MessageBox.Show(this, text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2, options) == DialogResult.Yes;
    }

    /// <param name="owner">The window the message belongs to: a dialog open over this one, or null for this window.</param>
    private void ShowMessage(string text, MessageBoxIcon icon, IWin32Window? owner = null)
    {
        var options = T.IsRightToLeft ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign : 0;
        MessageBox.Show(owner ?? this, text, WindowTitle, MessageBoxButtons.OK, icon, MessageBoxDefaultButton.Button1, options);
    }

    /// <summary>The "Stop the setup?" question when the window is closed during a run; null asks with a message box (tests answer it themselves).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<bool>? ConfirmStop { get; init; }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (e.Cancel)
            return;

        _testPrintRun?.Cancel();
        _ticketCheckRun?.Cancel();
        if (_run is null)
            return;

        // Nobody to ask (Windows shuts down, Task Manager, Application.Exit). A window whose machine-wide steps run in the elevated
        // worker may go at once: the worker notices the closed pipe and stops at its next action boundary. An elevated window runs
        // those steps itself, so it never exits in the middle of one: it stops the run and closes once it has stopped.
        bool userClosing = e.CloseReason == CloseReason.UserClosing;
        if (!userClosing && !Runtime.Elevation.IsElevated)
        {
            _run.Cancel();
            return;
        }

        // Asked once: closing again while the run stops keeps waiting for it (the read-back is what the employee is told about).
        if (_closeWhenStopped || !userClosing)
        {
            e.Cancel = true;
            StopRun(e.CloseReason);
            return;
        }

        if (!(ConfirmStop?.Invoke() ?? Confirm(T.Text("close.text"), T.Text("close.title"))))
        {
            e.Cancel = true;
            return;
        }

        // The question runs a message loop, so the run may have ended while it was open (its result is shown): the window closes now.
        if (_run is null)
            return;

        e.Cancel = true;
        StopRun(e.CloseReason);
    }

    /// <summary>
    /// Stops the run at its next step boundary (a step running with administrator rights finishes first); RunFlowAsync closes the
    /// window once the run has read back what was changed.
    /// </summary>
    private void StopRun(CloseReason reason)
    {
        var data = new Dictionary<string, object?> { ["reason"] = reason.ToString() };
        if (_closeWhenStopped)
            Runtime.Log.Info("ui.close.while_stopping", "The window was closed again while the run is stopping; it closes once the run has stopped.", data);
        else
            Runtime.Log.Info("ui.close.during_run", "The window was closed during a run; stopping after the current step.", data);

        _closeWhenStopped = true;
        _run?.Cancel();
        if (_progress is not null)
        {
            _progress.ShowCancelling();
            ShowView(_progress);
        }
    }

    internal static Icon? LoadApplicationIcon()
    {
        try
        {
            return Environment.ProcessPath is { } path ? Icon.ExtractAssociatedIcon(path) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return null;
        }
    }
}
