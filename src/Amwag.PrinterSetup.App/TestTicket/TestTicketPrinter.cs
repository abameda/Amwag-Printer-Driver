using System.Drawing.Printing;
using System.Globalization;
using System.Runtime.InteropServices;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Logging;

namespace Amwag.PrinterSetup.App.TestTicket;

/// <summary>Whether programs currently get the ticket form from the queue (DocumentProperties with no input).</summary>
public sealed record TestPrintPrecheck(bool FormMatches, string ExpectedFormName, string? ActualFormName, string Detail);

/// <summary>The paper Windows actually used for the test page, as the driver reported it in PrintPage.</summary>
public sealed record UsedPaper(string PaperName, double WidthMm, double HeightMm, bool Landscape, string? SourceName, RectangleF PrintableAreaMm)
{
    public string Describe() => FormattableString.Invariant($"{PaperName} ({WidthMm:0.##} x {HeightMm:0.##} mm{(Landscape ? ", landscape" : "")})");
}

public sealed record TestPrintSubmission(string QueueName, string DocumentName, DateTimeOffset SubmittedAt, UsedPaper? Paper);

/// <summary>
/// Prints one test ticket through the normal Windows print path with the queue's own defaults: paper size, orientation
/// and source are deliberately not overridden, because the test exists to exercise those defaults.
/// </summary>
public sealed class TestTicketPrinter
{
    private const float MillimetersPerHundredthInch = 0.254f;

    private readonly IPrintSpooler _spooler;
    private readonly TicketPaperConfig _ticket;
    private readonly ILog _log;

    public TestTicketPrinter(IPrintSpooler spooler, TicketPaperConfig ticket, ILog log)
    {
        _spooler = spooler;
        _ticket = ticket;
        _log = log;
    }

    public TestPrintPrecheck Precheck(string queueName)
    {
        TestPrintPrecheck result;
        try
        {
            var devMode = _spooler.GetDevMode(queueName, DevModeScope.Effective);
            result = devMode is null
                ? new TestPrintPrecheck(false, _ticket.FormName, null, "The driver returned no effective DEVMODE.")
                : new TestPrintPrecheck(
                    string.Equals(devMode.FormName, _ticket.FormName, StringComparison.OrdinalIgnoreCase),
                    _ticket.FormName,
                    devMode.FormName,
                    FormattableString.Invariant($"Effective DEVMODE: form '{devMode.FormName}', paper {devMode.PaperSize}, {devMode.PaperWidth}x{devMode.PaperLength} (0.1 mm), orientation {devMode.Orientation}, source {devMode.DefaultSource}."));
        }
        catch (PrinterSetupException ex)
        {
            result = new TestPrintPrecheck(false, _ticket.FormName, null, "Reading the effective DEVMODE failed: " + ex.Message);
        }

        _log.Info("testprint.precheck", result.FormMatches ? "Programs get the ticket form." : "Programs do not get the ticket form.", new Dictionary<string, object?>
        {
            ["queue"] = queueName,
            ["expectedForm"] = result.ExpectedFormName,
            ["actualForm"] = result.ActualFormName,
            ["detail"] = result.Detail,
        });
        return result;
    }

    /// <summary>Spools one page to the queue. Blocking; throws when Windows refuses the job.</summary>
    public TestPrintSubmission Print(string queueName, string modelName, DateTimeOffset now)
    {
        string documentName = "AMWAG Test Ticket " + now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var drawing = TestTicketLayout.Create(_ticket.Dimensions, new TestTicketInfo(_ticket.FormName, modelName, queueName, now));
        UsedPaper? usedPaper = null;

        try
        {
            using var document = new PrintDocument
            {
                DocumentName = documentName,
                OriginAtMargins = false,
                PrintController = new StandardPrintController(),
            };
            document.PrinterSettings.PrinterName = queueName;
            if (!document.PrinterSettings.IsValid)
                throw new InvalidPrinterException(document.PrinterSettings);

            document.PrintPage += (_, e) =>
            {
                usedPaper = Describe(e.PageSettings);
                var g = e.Graphics!;
                g.PageUnit = GraphicsUnit.Millimeter;
                // With OriginAtMargins = false the origin is the printable area's corner; move it to the sheet corner.
                g.TranslateTransform(-e.PageSettings.HardMarginX * MillimetersPerHundredthInch, -e.PageSettings.HardMarginY * MillimetersPerHundredthInch);
                TestTicketRenderer.Draw(g, drawing);
                TestTicketRenderer.DrawPrintableArea(g, usedPaper.PrintableAreaMm);
                e.HasMorePages = false;
            };
            document.Print();
        }
        catch (Exception ex) when (ex is InvalidPrinterException or ExternalException or InvalidOperationException)
        {
            _log.Error("testprint.print.failed", "Windows did not accept the test ticket.", ex, new Dictionary<string, object?> { ["queue"] = queueName, ["document"] = documentName });
            throw;
        }

        _log.Info("testprint.submitted", "Test ticket spooled.", new Dictionary<string, object?>
        {
            ["queue"] = queueName,
            ["document"] = documentName,
            ["paper"] = usedPaper?.Describe(),
            ["paperSource"] = usedPaper?.SourceName,
            ["printableArea"] = usedPaper is null ? null : FormattableString.Invariant($"{usedPaper.PrintableAreaMm.X:0.##},{usedPaper.PrintableAreaMm.Y:0.##},{usedPaper.PrintableAreaMm.Width:0.##}x{usedPaper.PrintableAreaMm.Height:0.##} mm"),
        });
        return new TestPrintSubmission(queueName, documentName, now, usedPaper);
    }

    private static UsedPaper Describe(PageSettings settings)
    {
        var area = settings.PrintableArea;
        return new UsedPaper(
            settings.PaperSize.PaperName,
            settings.PaperSize.Width * MillimetersPerHundredthInch,
            settings.PaperSize.Height * MillimetersPerHundredthInch,
            settings.Landscape,
            settings.PaperSource?.SourceName,
            new RectangleF(area.X * MillimetersPerHundredthInch, area.Y * MillimetersPerHundredthInch, area.Width * MillimetersPerHundredthInch, area.Height * MillimetersPerHundredthInch));
    }
}
