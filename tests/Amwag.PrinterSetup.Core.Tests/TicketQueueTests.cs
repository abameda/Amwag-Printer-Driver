using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.Core.Tests;

/// <summary>
/// The queue the tool configures: among one printer's queues the one already set up as the ticket printer, from detection
/// through planning and applying; and no printer is chosen without asking next to a queue whose printer is another one.
/// </summary>
public sealed class TicketQueueTests
{
    private const string Office = "A4 office";
    private const string Tickets = "Tickets";

    [Fact]
    public void The_queue_already_set_up_as_the_ticket_printer_is_the_one_planned_and_applied()
    {
        // Both queues use the preferred driver, so without the ticket form the first by name ("A4 office") would be configured.
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, "USB001")
            .WithModelDriver(SampleConfig.P4030)
            .WithUsbQueue(SampleConfig.P4030, "USB001", name: Office)
            .WithUsbQueue(SampleConfig.P4030, "USB001", name: Tickets)
            .WithTicketForm()
            .WithCorrectDefaults(Tickets)
            .WithPerUserDefaults(Tickets, d => d.Scale = 80)
            .Build();

        var run = scenario.Run();

        Assert.Equal(Tickets, Assert.Single(run.Detection.Candidates).Queue!.Name);
        Assert.Contains(scenario.Spooler.Writes, w => w is { Operation: nameof(IPrintSpooler.SetDevMode), Target: Tickets });
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Target == Office);
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
    }

    [Fact]
    public void A_queue_whose_printer_is_another_model_keeps_the_only_other_printer_from_being_chosen_without_asking()
    {
        // The ticket queue on USB002 has the P-4531DN driver, but the printer on that port is an unsupported P-4532DN; the office
        // P-5032DN on USB001 may well not be the ticket printer.
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P5032, "USB001", correctDefaults: false)
            .WithDevice(new PnpPrinterDevice
            {
                InstanceId = @"USBPRINT\TA_UTAXP-4532DNFFBC\7&2B5A1C3&0&R9L0309954",
                Bus = PnpBus.Usb,
                HardwareIds = [@"USBPRINT\TA_UTAXP-4532DNFFBC", "TA_UTAXP-4532DNFFBC"],
                FriendlyName = "TA_UTAX P-4532DN",
                IsPresent = true,
                UsbPortName = "USB002",
            })
            .WithModelDriver(SampleConfig.P4531)
            .WithUsbQueue(SampleConfig.P4531, "USB002", name: Tickets)
            .Build();
        var session = scenario.CreateSession();

        var detection = session.Detect(new DetectRequest(), default);
        var selection = session.SelectAutomatically(detection);

        Assert.Equal([Tickets], detection.RejectedQueues);
        Assert.Equal(SelectionReason.MultipleCandidates, selection.Reason);
        Assert.Null(selection.Selected);
        Assert.Equal(SampleConfig.P5032, Assert.Single(selection.Choices).Model!.Id);
    }
}
