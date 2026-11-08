using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;
using static Amwag.PrinterSetup.Core.Setup.ActionKind;
using static Amwag.PrinterSetup.Core.Tests.TestHelpers;

namespace Amwag.PrinterSetup.Core.Tests;

/// <summary>
/// A queue on an accepted driver that ignores Windows forms (the KX v3 driver, measured at a branch on 2026-11-04) is moved to
/// the preferred driver, which offers the ticket form; its driver and defaults come back when the ticket defaults then fail.
/// </summary>
public sealed class DriverSwitchTests
{
    private const string V3Driver = "TA P-4030DN KX v3";
    private static readonly string Preferred = SampleConfig.DriverName(SampleConfig.P4030);

    /// <summary>The branch PC: USB printer and its Plug and Play queue on the KX v3 driver; the preferred driver is not installed.</summary>
    private static ScenarioBuilder BranchWithV3Queue(bool form = true, PaperCapability? customPaper = null)
    {
        var builder = new ScenarioBuilder()
            .WithModel(SampleConfig.P4030, m => m with { AcceptedDrivers = [.. m.AcceptedDrivers, new AcceptedDriver { Name = V3Driver, ListsWindowsForms = false }] })
            .WithUsbPrinter(driverDescription: V3Driver)
            .WithDriver(V3Driver)
            .WithDriverProfile(V3Driver, p => p with
            {
                OmittedForms = [SampleConfig.FormName],
                ClearsFormName = true,
                BuiltInPapers = customPaper is null ? p.BuiltInPapers : [.. p.BuiltInPapers, customPaper],
            })
            .WithUsbQueue(driverName: V3Driver);
        return form ? builder.WithTicketForm() : builder;
    }

    [Fact]
    public void Queue_on_a_driver_that_ignores_the_ticket_form_is_moved_to_the_preferred_driver()
    {
        var scenario = BranchWithV3Queue().Build();

        var run = scenario.Run();

        var finding = Assert.Single(run.Plan!.Findings, f => f.Code == FindingCode.DriverIgnoresTicketForm);
        Assert.Equal(V3Driver, finding.Data["actual"]);
        Assert.Equal(Preferred, finding.Data["expected"]);
        Assert.DoesNotContain(run.Plan.Blockers, b => b.Code == FindingCode.DriverDoesNotListForm);
        var change = Assert.Single(run.Plan.Actions, a => a.Kind == ChangeQueueDriver);
        Assert.Equal(V3Driver, change.Parameters["restoreDriverName"]);
        Assert.Contains(InstallDriver, Kinds(run.Plan));

        Assert.Equal(ActionOutcome.Succeeded, Outcome(run.Apply!, ChangeQueueDriver));
        Assert.Equal(ActionOutcome.Succeeded, Outcome(run.Apply!, ConfigureGlobalDefaults));
        Assert.Equal(Preferred, scenario.Spooler.Queues.Single(q => q.Name == V3Driver).DriverName);
        Assert.Equal(SampleConfig.FormName, scenario.Spooler.GetStoredDevMode(V3Driver, DevModeScope.Global)!.FormName);
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
        Assert.True(scenario.Run().Plan!.NothingToDo);
    }

    [Fact]
    public void Without_the_form_yet_the_queue_is_moved_in_the_same_run()
    {
        // The driver is known not to use Windows forms, so the run does not first create a form it would ignore.
        var scenario = BranchWithV3Queue(form: false).Build();

        var run = scenario.Run();

        Assert.Contains(run.Plan!.Findings, f => f.Code == FindingCode.DriverIgnoresTicketForm);
        Assert.Contains(CreateForm, Kinds(run.Plan));
        Assert.Contains(ChangeQueueDriver, Kinds(run.Plan));
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
    }

    [Fact]
    public void Queue_on_that_driver_moves_also_with_its_own_paper_of_the_ticket_size()
    {
        // As at the branch: a custom page size of the ticket size made in the KX v3 driver (another name, maybe one account only).
        var custom = new PaperCapability(541, "Ticket 3.24x6.70", 823, 1702);
        var scenario = BranchWithV3Queue(customPaper: custom).Build();

        var run = scenario.Run();

        Assert.Contains(run.Plan!.Findings, f => f.Code == FindingCode.DriverIgnoresTicketForm);
        Assert.Contains(ChangeQueueDriver, Kinds(run.Plan));
        Assert.Equal(Preferred, scenario.Spooler.Queues.Single(q => q.Name == V3Driver).DriverName);
        Assert.Equal(SampleConfig.FormName, scenario.Spooler.GetStoredDevMode(V3Driver, DevModeScope.Global)!.FormName);
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
    }

    [Fact]
    public void Another_accepted_driver_moves_only_when_it_ignores_the_existing_form_and_has_no_paper_of_its_size()
    {
        string xps = SampleConfig.XpsDriverName(SampleConfig.P4030);
        ScenarioBuilder XpsQueue(bool paperOfTheSize) => new ScenarioBuilder()
            .WithUsbPrinter(driverDescription: xps)
            .WithDriver(xps)
            .WithDriverProfile(xps, p => p with
            {
                OmittedForms = [SampleConfig.FormName],
                BuiltInPapers = paperOfTheSize ? [.. p.BuiltInPapers, new PaperCapability(300, "Ticket", 823, 1702)] : p.BuiltInPapers,
            })
            .WithUsbQueue(driverName: xps)
            .WithTicketForm();

        var moved = XpsQueue(paperOfTheSize: false).Build();
        Assert.Contains(ChangeQueueDriver, Kinds(moved.Run().Plan!));
        Assert.Equal(Preferred, moved.Spooler.Queues.Single(q => q.Name == xps).DriverName);

        var kept = XpsQueue(paperOfTheSize: true).Build();
        var run = kept.Run();
        Assert.DoesNotContain(ChangeQueueDriver, Kinds(run.Plan!));
        Assert.Equal(xps, kept.Spooler.Queues.Single(q => q.Name == xps).DriverName);
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
    }

    [Fact]
    public void Failing_ticket_defaults_with_the_new_driver_give_the_queue_its_driver_and_defaults_back()
    {
        var scenario = BranchWithV3Queue()
            .WithDriverProfile(Preferred, p => p with { OmittedForms = [SampleConfig.FormName] })
            .Build();
        var before = scenario.Spooler.GetDevMode(V3Driver, DevModeScope.Global)!;

        var run = scenario.Run();

        Assert.Equal(ActionOutcome.Succeeded, Outcome(run.Apply!, ChangeQueueDriver));
        Assert.Equal(ActionOutcome.Failed, Outcome(run.Apply!, ConfigureGlobalDefaults));
        var restored = Assert.Single(run.Apply!.Findings, f => f.Code == FindingCode.QueueDriverRestored);
        Assert.Equal(Severity.Warning, restored.Severity);
        Assert.Equal(V3Driver, scenario.Spooler.Queues.Single(q => q.Name == V3Driver).DriverName);
        var after = scenario.Spooler.GetStoredDevMode(V3Driver, DevModeScope.Global)!;
        Assert.Equal((before.PaperSize, before.DefaultSource, before.Orientation), (after.PaperSize, after.DefaultSource, after.Orientation));
        Assert.Equal(ReadinessLevel.NotReady, run.Verification!.Readiness);
    }

    [Fact]
    public void A_driver_change_that_is_not_for_the_ticket_is_never_undone()
    {
        // A queue on a driver of no supported model gets the approved driver; a failure afterwards keeps it.
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter()
            .WithModelDriver()
            .WithDriver("Generic / Text Only")
            .WithUsbQueue(name: "Ticket", driverName: "Generic / Text Only")
            .WithTicketForm()
            .WithModelDriverProfile(SampleConfig.P4030, p => p with { OmittedForms = [SampleConfig.FormName] })
            .Build();

        var run = scenario.Run();

        Assert.Equal(ActionOutcome.Failed, Outcome(run.Apply!, ConfigureGlobalDefaults));
        Assert.DoesNotContain(run.Apply!.Findings, f => f.Code == FindingCode.QueueDriverRestored);
        Assert.Equal(Preferred, scenario.Spooler.Queues.Single(q => q.Name == "Ticket").DriverName);
    }

    [Fact]
    public void A_new_queue_does_not_reuse_an_installed_driver_that_ignores_the_ticket_form()
    {
        var scenario = new ScenarioBuilder()
            .WithModel(SampleConfig.P4030, m => m with { AcceptedDrivers = [.. m.AcceptedDrivers, new AcceptedDriver { Name = V3Driver, ListsWindowsForms = false }] })
            .WithUsbPrinter(driverDescription: V3Driver)
            .WithDriver(V3Driver)
            .Build();

        var run = scenario.Run();

        Assert.Contains(InstallDriver, Kinds(run.Plan!));
        var queue = Assert.Single(scenario.Spooler.Queues, q => q.PortName == "USB001");
        Assert.Equal(Preferred, queue.DriverName);
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
    }

    [Fact]
    public void Diagnostics_reports_the_driver_change_and_changes_nothing()
    {
        var scenario = BranchWithV3Queue().WithMode(SetupMode.Diagnostics).Build();

        var run = scenario.Run();

        Assert.Contains(run.Plan!.Findings, f => f.Code == FindingCode.DriverIgnoresTicketForm);
        Assert.All(run.Apply!.Results, r => Assert.Equal(ActionOutcome.Skipped, r.Outcome));
        Assert.Empty(scenario.Spooler.Writes);
        Assert.Equal(V3Driver, scenario.Spooler.Queues.Single(q => q.Name == V3Driver).DriverName);
    }

    [Fact]
    public void The_preferred_driver_must_use_windows_forms()
    {
        var config = SampleConfig.Create() with
        {
            Models = SampleConfig.Create().Models
                .Select(m => m with { AcceptedDrivers = m.AcceptedDrivers.Select(d => d with { ListsWindowsForms = !string.Equals(d.Name, m.PreferredDriverName, StringComparison.Ordinal) }).ToList() })
                .ToList(),
        };

        var errors = ConfigLoader.Validate(config);

        Assert.Contains(errors, e => e.Contains("must list Windows forms", StringComparison.Ordinal));
    }
}
