using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Windows.Spooler;

namespace Amwag.PrinterSetup.Windows.Tests.Spooler;

/// <summary>
/// Read-only checks against this machine's spooler and the in-box "Microsoft Print to PDF" queue (PORTPROMPT:, v4 driver).
/// Nothing here writes printers, ports, drivers, forms, DEVMODEs or services: DocumentProperties is only called with
/// DM_OUT_BUFFER and DM_IN_BUFFER | DM_OUT_BUFFER, and the per-queue defaults are compared before and after.
/// </summary>
[Trait("Category", "WindowsIntegration")]
public sealed class WindowsPrintSpoolerIntegrationTests
{
    private const string PdfQueue = "Microsoft Print to PDF";
    private const string PdfDriver = "Microsoft Print To PDF";
    private const string MissingQueue = "AMWAG Spooler Test Queue That Does Not Exist";

    private readonly WindowsPrintSpooler _spooler = new(NullLog.Instance);

    [Fact]
    public void Spooler_service_is_running()
    {
        SpoolerServiceStatus status = _spooler.GetServiceStatus();

        Assert.True(status.IsRunning, status.Detail);
        Assert.NotEqual(ServiceStartMode.Unknown, status.StartMode);
    }

    [Fact]
    public void A_missing_service_is_reported_as_not_installed_and_is_not_started()
    {
        var spooler = new WindowsPrintSpooler(NullLog.Instance, "AmwagSpoolerTestMissingService");

        SpoolerServiceStatus status = spooler.GetServiceStatus();
        bool started = spooler.TryStartService(TimeSpan.FromSeconds(1), out string? reason);

        Assert.Equal(ServiceRunState.NotInstalled, status.State);
        Assert.False(started);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void Queues_include_the_pdf_queue_on_portprompt()
    {
        PrintQueueInfo queue = Assert.Single(_spooler.GetQueues(), q => q.Name == PdfQueue);

        Assert.Equal("PORTPROMPT:", queue.PortName);
        Assert.Equal(PdfDriver, queue.DriverName);
        Assert.Null(queue.ServerName);
        Assert.False(queue.IsServerConnection);
    }

    [Fact]
    public void Ports_include_portprompt_as_a_local_port()
    {
        PrinterPortInfo port = Assert.Single(_spooler.GetPorts(), p => p.Name == "PORTPROMPT:");

        Assert.Equal(PortKind.Local, port.Kind);
        Assert.Null(port.HostAddress);
    }

    [Fact]
    public void Drivers_include_the_v4_pdf_driver_for_windows_x64()
    {
        PrinterDriverInfo driver = Assert.Single(_spooler.GetDrivers(), d => d.Name == PdfDriver);

        Assert.Equal("Windows x64", driver.Environment);
        Assert.Equal(4, driver.DriverModel);
        Assert.NotNull(driver.Version);
        Assert.NotNull(driver.Date);
        Assert.False(string.IsNullOrEmpty(driver.InfPath));
    }

    [Fact]
    public void Forms_include_a4_and_letter_with_their_exact_sizes()
    {
        IReadOnlyList<FormInfo> forms = _spooler.GetForms();

        FormInfo a4 = Assert.Single(forms, f => f.Name == "A4");
        FormInfo letter = Assert.Single(forms, f => f.Name == "Letter");
        Assert.Equal((210000, 297000), (a4.WidthMicrometers, a4.HeightMicrometers));
        Assert.Equal((215900, 279400), (letter.WidthMicrometers, letter.HeightMicrometers));
        Assert.Equal(FormKind.Builtin, a4.Kind);
        Assert.True(a4.HasFullImageableArea);
    }

    [Fact]
    public void Driver_capabilities_of_the_pdf_queue_list_a4_in_tenths_of_a_millimeter()
    {
        DriverCapabilities capabilities = _spooler.GetCapabilities(PdfQueue);

        PaperCapability? a4 = capabilities.FindPaper("A4");
        Assert.NotNull(a4);
        Assert.Equal(DmPaper.A4, a4.Id);
        Assert.Equal((2100, 2970), (a4.WidthTenthsOfMillimeter, a4.HeightTenthsOfMillimeter));
        Assert.Equal(PdfQueue, capabilities.QueueName);
    }

    [Fact]
    public void Effective_devmode_has_the_current_public_size_and_its_private_data()
    {
        DevMode? devMode = _spooler.GetDevMode(PdfQueue, DevModeScope.Effective);

        Assert.NotNull(devMode);
        Assert.Equal(DevMode.CurrentPublicSize, devMode.Size);
        Assert.Equal(devMode.Size + devMode.DriverExtra, devMode.TotalSize);
        Assert.StartsWith(PdfQueue, devMode.DeviceName);
    }

    [Theory]
    [InlineData(DevModeScope.Global)]
    [InlineData(DevModeScope.PerUser)]
    public void Stored_default_devmodes_can_be_read(DevModeScope scope)
    {
        DevMode? devMode = _spooler.GetDevMode(PdfQueue, scope);

        // Either may be absent (no per-user defaults is the normal case); when present it must be well formed.
        if (devMode is not null)
            Assert.True(devMode.Size >= DevMode.MinimumPublicSize);
    }

    [Theory]
    [InlineData("Letter", DmPaper.Letter, 2159, 2794)]
    [InlineData("A4", DmPaper.A4, 2100, 2970)]
    public void Validating_a_devmode_returns_the_merged_form_and_persists_nothing(string form, short paper, int width, int length)
    {
        byte[]? globalBefore = _spooler.GetDevMode(PdfQueue, DevModeScope.Global)?.ToArray();
        byte[]? perUserBefore = _spooler.GetDevMode(PdfQueue, DevModeScope.PerUser)?.ToArray();

        DevMode validated = _spooler.ValidateDevMode(PdfQueue, Requesting(form, paper));

        Assert.Equal(form, validated.FormName);
        Assert.Equal(paper, validated.PaperSize);
        Assert.Equal((width, length), ((int)validated.PaperWidth, (int)validated.PaperLength));
        Assert.Equal(globalBefore, _spooler.GetDevMode(PdfQueue, DevModeScope.Global)?.ToArray());
        Assert.Equal(perUserBefore, _spooler.GetDevMode(PdfQueue, DevModeScope.PerUser)?.ToArray());
    }

    [Fact]
    public void Print_capabilities_are_read_without_throwing_and_list_a4()
    {
        PrintCapabilitiesSummary summary = _spooler.GetPrintCapabilities(PdfQueue, devMode: null);

        Assert.True(summary.Available, summary.Error);
        MediaSizeCapability a4 = Assert.Single(summary.MediaSizes, m => m.Name == "psk:ISOA4");
        Assert.Equal((210000, 297000), (a4.WidthMicrometers, a4.HeightMicrometers));
    }

    [Fact]
    public void Print_capabilities_for_a_devmode_are_read()
    {
        DevMode devMode = _spooler.GetDevMode(PdfQueue, DevModeScope.Effective)!;

        PrintCapabilitiesSummary summary = _spooler.GetPrintCapabilities(PdfQueue, devMode);

        Assert.True(summary.Available, summary.Error);
        Assert.NotEmpty(summary.MediaSizes);
    }

    [Fact]
    public void Print_ticket_of_an_a4_devmode_reports_the_a4_size()
    {
        DevMode a4 = _spooler.ValidateDevMode(PdfQueue, Requesting("A4", DmPaper.A4));

        PrintTicketMediaInfo ticket = _spooler.DescribePrintTicket(PdfQueue, a4);

        Assert.True(ticket.Available, ticket.Error);
        Assert.Equal("psk:ISOA4", ticket.MediaSizeName);
        Assert.InRange(ticket.WidthMicrometers!.Value, 209000, 211000);
        Assert.InRange(ticket.HeightMicrometers!.Value, 296000, 298000);
        Assert.Equal("psk:Portrait", ticket.Orientation);
    }

    [Fact]
    public void Jobs_of_the_pdf_queue_can_be_listed()
    {
        IReadOnlyList<PrintJobInfo> jobs = _spooler.GetJobs(PdfQueue);

        Assert.NotNull(jobs);
        Assert.All(jobs, job => Assert.True(job.JobId > 0));
    }

    [Fact]
    public void A_job_that_does_not_exist_has_no_devmode()
    {
        Assert.Null(_spooler.GetJobDevMode(PdfQueue, int.MaxValue));
    }

    [Fact]
    public void A_missing_queue_fails_with_invalid_printer_name()
    {
        var exception = Assert.Throws<SpoolerException>(() => _spooler.GetDevMode(MissingQueue, DevModeScope.Effective));

        Assert.Equal("OpenPrinter", exception.Operation);
        Assert.Equal(1801, exception.Win32Error);
        Assert.Equal(FindingCode.OperationFailed, exception.Code);
    }

    [Fact]
    public void Print_schema_checks_of_a_missing_queue_are_unavailable_without_throwing()
    {
        DevMode devMode = _spooler.GetDevMode(PdfQueue, DevModeScope.Effective)!;

        PrintCapabilitiesSummary capabilities = _spooler.GetPrintCapabilities(MissingQueue, devMode: null);
        PrintTicketMediaInfo ticket = _spooler.DescribePrintTicket(MissingQueue, devMode);

        Assert.False(capabilities.Available);
        Assert.Contains("PTOpenProvider", capabilities.Error);
        Assert.False(ticket.Available);
        Assert.Contains("0x80070709", ticket.Error);
    }

    /// <summary>The effective DEVMODE with a paper size and form selected, as the setup engine merges its defaults.</summary>
    private DevMode Requesting(string form, short paper)
    {
        DevMode devMode = _spooler.GetDevMode(PdfQueue, DevModeScope.Effective)!.Clone();
        devMode.FormName = form;
        devMode.PaperSize = paper;
        devMode.Fields = (devMode.Fields | DevModeFields.FormName | DevModeFields.PaperSize) & ~(DevModeFields.PaperLength | DevModeFields.PaperWidth);
        return devMode;
    }
}
