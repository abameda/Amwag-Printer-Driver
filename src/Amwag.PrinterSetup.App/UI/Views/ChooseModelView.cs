using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.UI.Views;

/// <summary>A reachable network printer that does not report its model: the employee may pick the model, only if sure.</summary>
internal sealed class ChooseModelView : ViewBase
{
    private readonly PrinterCandidate _candidate;
    private readonly IReadOnlyList<ModelDefinition> _models;
    private readonly TableLayoutPanel _content;
    private readonly Label _heading;
    private readonly Label _text;
    private readonly NotePanel _warning;
    private readonly ActionButton _continue;
    private readonly ActionButton _cancel;
    private readonly List<CardPanel> _cards = [];
    private int _selected = -1;

    public ChooseModelView(ViewContext context, PrinterCandidate candidate, IReadOnlyList<ModelDefinition> models, Action<ModelDefinition?> answer) : base(context)
    {
        _candidate = candidate;
        _models = models;
        (_content, var bar) = CreateFrame();
        _heading = Ui.Heading(S);
        _text = Ui.Paragraph(S);
        _warning = new NotePanel(S, NoteTone.Warning);
        _content.Controls.Add(_heading);
        _content.Controls.Add(_text);
        _content.Controls.Add(_warning);

        _continue = new ActionButton(ButtonKind.Primary, S) { Enabled = false };
        _cancel = new ActionButton(ButtonKind.Secondary, S);
        _continue.Click += (_, _) =>
        {
            if (_selected >= 0)
                answer(_models[_selected]);
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
    public override Control? InitialFocus => _cancel;

    public override void Localize()
    {
        string address = _candidate.NetworkAddress ?? _candidate.NetworkIdentity?.Address ?? "";
        _heading.Text = T.Text("model.title");
        _text.Text = T.Format("model.text", address);
        _warning.SetText(T.Text("model.warning"));
        _continue.Text = T.Text("model.continue");
        _cancel.Text = T.Text("ui.cancel");
        RebuildCards();
    }

    private void RebuildCards()
    {
        _content.SuspendLayout();
        foreach (var card in _cards)
            card.Dispose();
        _cards.Clear();

        for (int i = 0; i < _models.Count; i++)
        {
            int index = i;
            var model = _models[i];
            var card = new CardPanel(S, selectable: true) { TabIndex = i };
            string name = model.DisplayName.Length > 0 ? model.DisplayName : model.Id;
            card.Controls.Add(new Label { Text = T.Data(name), AutoSize = true, Font = Theme.BodyStrong, ForeColor = Theme.Text, BackColor = Color.Transparent, UseMnemonic = false, Margin = Padding.Empty });
            if (!string.Equals(name, model.Id, StringComparison.OrdinalIgnoreCase))
                card.Controls.Add(new Label { Text = T.Data(model.Id), AutoSize = true, Font = Theme.Small, ForeColor = Theme.TextSecondary, BackColor = Color.Transparent, UseMnemonic = false, Margin = S.Pad(0, 2, 0, 0) });
            card.AccessibleName = name;
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
