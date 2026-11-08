using System.Buffers.Binary;
using Amwag.PrinterSetup.App.TestTicket;
using Amwag.PrinterSetup.App.Tests.UI;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Printing;

namespace Amwag.PrinterSetup.App.Tests.TestTicket;

public sealed class TestTicketPrinterTests
{
    private const string Queue = "AMWAG Ticket Printer";

    private readonly StubSpooler _spooler = new();
    private readonly MemoryLog _log = new();

    private TestTicketPrinter Printer => new(_spooler, TestRuntime.Config.Ticket, _log);

    private static DevMode WithForm(string formName)
    {
        var bytes = new byte[DevMode.CurrentPublicSize];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(68), DevMode.CurrentPublicSize); // dmSize
        var devMode = DevMode.FromBytes(bytes);
        devMode.FormName = formName;
        return devMode;
    }

    [Fact]
    public void Precheck_reads_what_programs_get_and_accepts_the_ticket_form()
    {
        DevModeScope? scope = null;
        _spooler.DevModes = (_, s) =>
        {
            scope = s;
            return WithForm("amwag ticket");
        };

        var result = Printer.Precheck(Queue);

        Assert.Equal(DevModeScope.Effective, scope);
        Assert.True(result.FormMatches);
        Assert.Contains(_log.Entries, e => e.Event == "testprint.precheck");
    }

    [Fact]
    public void Precheck_warns_when_programs_get_another_form()
    {
        _spooler.DevModes = (_, _) => WithForm("Letter");

        var result = Printer.Precheck(Queue);

        Assert.False(result.FormMatches);
        Assert.Equal("Letter", result.ActualFormName);
        Assert.Equal("AMWAG Ticket", result.ExpectedFormName);
    }

    [Fact]
    public void Precheck_warns_when_the_settings_cannot_be_read()
    {
        _spooler.DevModes = (_, _) => throw new SpoolerException("DocumentProperties", 1801, "The printer name is invalid.");

        var result = Printer.Precheck(Queue);

        Assert.False(result.FormMatches);
        Assert.Null(result.ActualFormName);
        Assert.Contains("1801", result.Detail);
    }

    [Fact]
    public void Precheck_warns_when_the_driver_returns_nothing()
    {
        var result = Printer.Precheck(Queue);

        Assert.False(result.FormMatches);
        Assert.Null(result.ActualFormName);
    }
}
