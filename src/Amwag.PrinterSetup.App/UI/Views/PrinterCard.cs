using Amwag.PrinterSetup.App.Localization;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.UI.Views;

internal enum PrinterCardDetail
{
    /// <summary>While setting up: model, connection, driver, printer name, ticket size.</summary>
    Progress,
    /// <summary>When choosing between printers: model as title, connection, whether a USB printer is connected, printer name, how it was identified.</summary>
    Choice,
}

/// <summary>Builds the card that describes one detected printer; Latin data is bidi-isolated in Arabic.</summary>
internal static class PrinterCard
{
    public static CardPanel Create(ViewContext context, UiScale s, PrinterCandidate candidate, PrinterCardDetail detail, bool selectable = false)
    {
        var t = context.Strings;
        var card = new CardPanel(s, selectable);

        var title = new Label
        {
            AutoSize = true,
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
            UseMnemonic = false,
            BackColor = Color.Transparent,
            Margin = s.Pad(0, 0, 0, 6),
        };
        if (detail == PrinterCardDetail.Progress)
        {
            title.Text = t.Text("card.title");
            title.Font = Theme.SmallStrong;
            title.ForeColor = Theme.TextSecondary;
        }
        else
        {
            title.Text = ModelName(t, candidate);
            title.Font = Theme.BodyStrong;
            title.ForeColor = Theme.Text;
        }
        card.Controls.Add(title);

        var rows = new List<(string, string)>();
        if (detail == PrinterCardDetail.Progress)
            rows.Add((t.Text("card.model"), ModelName(t, candidate)));
        rows.Add((t.Text("card.connection"), Connection(t, candidate)));
        // Windows remembers every USB printer ever connected: the choice says which one is connected now.
        if (detail == PrinterCardDetail.Choice && candidate.Connection == ConnectionType.Usb)
            rows.Add((t.Text("card.presence"), t.Text(candidate.PhysicallyAvailable ? "value.connected" : "value.notConnected")));
        if (detail == PrinterCardDetail.Progress)
            rows.Add((t.Text("card.driver"), candidate.Queue is { } queue ? t.Data(queue.DriverName) : t.Text("value.notYetInstalled")));
        rows.Add((t.Text("card.queue"), QueueName(context, candidate)));
        if (detail == PrinterCardDetail.Progress)
            rows.Add((t.Text("card.ticketSize"), t.Data(context.TicketDescription)));
        else
            rows.Add((t.Text("card.identifiedBy"), IdentifiedBy(t, candidate)));

        card.Controls.Add(InfoTable.Create(s, rows));
        card.AccessibleName = string.Join(", ", rows.Select(r => r.Item1 + ": " + r.Item2).Prepend(title.Text));
        return card;
    }

    public static string ModelName(Strings t, PrinterCandidate candidate) =>
        candidate.Model is { } model ? t.Data(model.DisplayName.Length > 0 ? model.DisplayName : model.Id) : t.Text("value.unknownModel");

    /// <summary>How the model was established; a USB device that is not connected reported it at an earlier connection, not now.</summary>
    public static string IdentifiedBy(Strings t, PrinterCandidate candidate) =>
        candidate is { Confidence: MatchConfidence.Exact, Device.IsPresent: false } ? t.Text("value.earlierConnection") : t.ConfidenceName(candidate.Confidence);

    public static string Connection(Strings t, PrinterCandidate candidate)
    {
        switch (candidate.Connection)
        {
            case ConnectionType.Usb:
                string? port = candidate.Device?.UsbPortName ?? candidate.Queue?.PortName;
                return string.IsNullOrEmpty(port) ? t.ConnectionName(ConnectionType.Usb) : t.Format("connection.usbPort", port);
            case ConnectionType.TcpIp:
                string? address = candidate.NetworkAddress ?? candidate.NetworkIdentity?.Address;
                return string.IsNullOrEmpty(address) ? t.ConnectionName(ConnectionType.TcpIp) : t.Format("connection.network", address);
            default:
                return t.ConnectionName(candidate.Connection);
        }
    }

    public static string QueueName(ViewContext context, PrinterCandidate candidate)
    {
        var t = context.Strings;
        if (candidate.Queue is { } queue)
            return t.Data(queue.Name);

        string newName = context.Config.Connection.NewQueueName;
        return string.IsNullOrEmpty(newName) ? t.Text("value.newQueue") : t.Data(newName) + " (" + t.Text("value.newQueue") + ")";
    }
}
