using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.UI.Views;

/// <summary>Several eligible printers: one selectable card each; Continue answers with the selected one, Cancel with null.</summary>
internal sealed class ChoosePrinterView : ViewBase
{
    private readonly IReadOnlyList<PrinterCandidate> _candidates;
    private readonly TableLayoutPanel _content;
    private readonly Label _heading;
    private readonly Label _text;
    private readonly ActionButton _continue;
    private readonly ActionButton _cancel;
    private readonly List<CardPanel> _cards = [];
    private int _selected = -1;

    public ChoosePrinterView(ViewContext context, IReadOnlyList<PrinterCandidate> candidates, Action<PrinterCandidate?> answer) : base(context)
    {
        _candidates = candidates;
        (_content, var bar) = CreateFrame();
        _heading = Ui.Heading(S);
        _text = Ui.Paragraph(S);
        _content.Controls.Add(_heading);
        _content.Controls.Add(_text);

        _continue = new ActionButton(ButtonKind.Primary, S) { Enabled = false };
        _cancel = new ActionButton(ButtonKind.Secondary, S);
        _continue.Click += (_, _) =>
        {
            if (_selected >= 0)
                answer(_candidates[_selected]);
        };
        _cancel.Click += (_, _) => answer(null);
        var buttons = Ui.ButtonRow(S);
        buttons.Controls.Add(_continue);
        buttons.Controls.Add(_cancel);
        bar.Controls.Add(buttons);

        Localize();
    }

    public override IButtonControl? AcceptAction => _continue;
    public override IButtonControl? CancelAction => _cancel;
    public override Control? InitialFocus => _cards.FirstOrDefault();

    public override void Localize()
    {
        _heading.Text = T.Text("choose.title");
        // A single printer is offered when it is not certain to be the ticket printer (another model than configured, or next to
        // a queue whose printer turned out to be another one).
        _text.Text = T.Text(_candidates.Count == 1 ? "choose.text.single" : "choose.text");
        _continue.Text = T.Text("choose.continue");
        _cancel.Text = T.Text("ui.cancel");
        RebuildCards();
    }

    private void RebuildCards()
    {
        _content.SuspendLayout();
        foreach (var card in _cards)
            card.Dispose();
        _cards.Clear();

        for (int i = 0; i < _candidates.Count; i++)
        {
            int index = i;
            var card = PrinterCard.Create(Context, S, _candidates[i], PrinterCardDetail.Choice, selectable: true);
            card.TabIndex = i;
            card.SelectedChanged += (_, _) =>
            {
                if (!card.IsSelected)
                    return;
                _selected = index;
                foreach (var other in _cards.Where(c => c != card))
                    other.SetSelected(false);
                _continue.Enabled = true;
            };
            _cards.Add(card);
            _content.Controls.Add(card);
            if (i == _selected)
                card.SetSelected(true);
        }
        _content.ResumeLayout();
    }
}
