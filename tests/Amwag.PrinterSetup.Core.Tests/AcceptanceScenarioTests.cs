using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;
using static Amwag.PrinterSetup.Core.Setup.ActionKind;
using static Amwag.PrinterSetup.Core.Tests.TestHelpers;

namespace Amwag.PrinterSetup.Core.Tests;

/// <summary>The acceptance scenarios, run through <see cref="SetupSession"/> against the fakes.</summary>
public sealed class AcceptanceScenarioTests
{
    private const string Address = "10.20.30.40";
    private static readonly string Queue4030 = SampleConfig.DriverName(SampleConfig.P4030);

    // 1 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void S01_usb_printer_without_driver_is_installed_and_configured()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(SampleConfig.P4030, driverBound: false).Build();

        var run = scenario.Run();

        Assert.Equal(SelectionReason.SingleCandidate, run.Selection.Reason);
        Assert.Equal([PrepareDriverPackage, InstallDriver, BindUsbDevice, CreateQueue, CreateForm, ConfigureGlobalDefaults, ConfigureUserDefaults], Kinds(run.Plan!));
        Assert.True(run.Plan!.CanProceed);
        Assert.True(run.Apply!.Succeeded, string.Join("; ", run.Apply.Results.Select(r => $"{r.Action.Kind}: {r.Outcome} {r.Detail}")));

        // Windows Plug and Play created the queue during the wait; the tool did not add a second one.
        Assert.Contains("Plug and Play", Result(run.Apply, CreateQueue).Detail, StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(6), scenario.Clock.TotalSlept);
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Operation == nameof(IPrintSpooler.AddQueue));
        Assert.Equal(ActionOutcome.AlreadySatisfied, Outcome(run.Apply, ConfigureUserDefaults));

        var global = scenario.Spooler.GetStoredDevMode(Queue4030, DevModeScope.Global)!;
        Assert.Equal(SampleConfig.FormName, global.FormName);
        Assert.Equal(FakeDriverProfile.FirstFormPaperId, global.PaperSize);
        Assert.Equal(DmOrientation.Portrait, global.Orientation);
        Assert.Equal(DmDuplex.Simplex, global.Duplex);
        Assert.Equal(100, global.Scale);
        Assert.Equal(SampleConfig.MpTrayBinId, global.DefaultSource);
        Assert.Single(scenario.Spooler.Forms, f => f.Name == SampleConfig.FormName && f.HasSize(SampleConfig.Ticket) && f.HasFullImageableArea);

        var verification = run.Verification!;
        Assert.Equal(ReadinessLevel.Ready, verification.Readiness);
        Assert.Equal(Queue4030, verification.Summary.QueueName);
        Assert.Equal("USB (USB001)", verification.Summary.Connection);
        Assert.Equal("AMWAG Ticket, 82.296 mm x 170.18 mm (3.24 in x 6.70 in)", verification.Summary.PaperSize);
        Assert.Equal(SampleConfig.MpTrayBinName, verification.Summary.PaperSource);
        Assert.Equal("8.4.1013.0", verification.Summary.DriverVersion);
    }

    [Fact]
    public void S01_when_windows_creates_no_queue_the_tool_creates_it_after_the_wait()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, driverBound: false)
            .WithPlugAndPlayQueueDelay(null)
            .Build();

        var run = scenario.Run();

        Assert.True(run.Apply!.Succeeded);
        Assert.Equal(TimeSpan.FromSeconds(scenario.Config.Connection.UsbQueueWaitSeconds), scenario.Clock.TotalSlept);
        var added = Assert.Single(scenario.Spooler.Writes, w => w.Operation == nameof(IPrintSpooler.AddQueue));
        Assert.Equal(Queue4030, added.Target);
        Assert.Equal("USB001", scenario.Spooler.Queues.Single(q => q.Name == Queue4030).PortName);
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
    }

    [Fact]
    public void S01_usb_port_unknown_before_the_driver_is_bound()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(SampleConfig.P4030, usbPort: null, driverBound: false).Build();

        var run = scenario.Run();

        Assert.True(run.Apply!.Succeeded);
        Assert.Equal("USB001", scenario.Spooler.Queues.Single(q => q.Name == Queue4030).PortName);
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
    }

    // 2 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void S02_usb_printer_with_correct_driver_and_queue_only_gets_its_defaults()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter(SampleConfig.P4030, correctDefaults: false).Build();

        var run = scenario.Run();

        Assert.Equal([ConfigureGlobalDefaults], Kinds(run.Plan!));
        Assert.True(Has(run.Plan!.Findings, FindingCode.GlobalDefaultsIncorrect, Severity.Warning));
        Assert.True(Has(run.Plan.Findings, FindingCode.DriverReady));
        Assert.True(Has(run.Plan.Findings, FindingCode.FormReady));
        Assert.True(run.Apply!.Succeeded);
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
        Assert.Empty(scenario.Installer.Calls);
        Assert.Empty(scenario.Packages.PreparedPackageIds);
    }

    // 3 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void S03_installed_printer_without_the_form_gets_the_form_and_defaults()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter().WithModelDriver().WithUsbQueue().Build();

        var run = scenario.Run();

        Assert.Equal([CreateForm, ConfigureGlobalDefaults, ConfigureUserDefaults], Kinds(run.Plan!));
        Assert.True(Has(run.Plan!.Findings, FindingCode.FormMissing));
        Assert.True(run.Apply!.Succeeded);
        Assert.Single(scenario.Spooler.Writes, w => w.Operation == nameof(IPrintSpooler.AddForm));
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
    }

    // 4 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void S04_correct_printer_needs_nothing_and_verifies_ready()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter().Build();

        var run = scenario.Run();

        Assert.True(run.Plan!.NothingToDo);
        Assert.True(run.Plan.CanProceed);
        Assert.Empty(run.Apply!.Results);
        Assert.Empty(scenario.Spooler.Writes);
        Assert.True(Has(run.Plan.Findings, FindingCode.GlobalDefaultsCorrect));
        Assert.True(Has(run.Plan.Findings, FindingCode.UserDefaultsAbsent));
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
        Assert.All(run.Verification.Checks, c => Assert.True(c.Status is CheckStatus.Pass or CheckStatus.NotApplicable, $"{c.Item}: {c.Status} {c.Detail}"));
    }

    [Fact]
    public void S04_applying_a_completed_plan_again_is_all_already_satisfied()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(SampleConfig.P4030, driverBound: false).Build();
        var run = scenario.Run();
        int writes = scenario.Spooler.Writes.Count;
        int prepared = scenario.Packages.PreparedPackageIds.Count;

        var again = run.Session.Apply(run.Plan!, ApplyScope.All, null, CancellationToken.None);

        Assert.All(again.Results, r => Assert.Equal(ActionOutcome.AlreadySatisfied, r.Outcome));
        Assert.Equal(run.Plan!.Actions.Count, again.Results.Count);
        Assert.Equal(writes, scenario.Spooler.Writes.Count);
        Assert.Equal(prepared, scenario.Packages.PreparedPackageIds.Count);
    }

    // 5 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void S05_wrong_driver_wrong_defaults_and_wrong_per_user_defaults_are_repaired()
    {
        const string queue = "TA P-4030DN";
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter()
            .WithModelDriver()
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver, new Version(10, 0, 26100, 1))
            .WithUsbQueue(name: queue, driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithTicketForm()
            .WithPerUserDefaults(queue, d => d.Orientation = DmOrientation.Landscape)
            .Build();
        var session = scenario.CreateSession();
        var target = session.SelectAutomatically(session.Detect(new DetectRequest(), default)).Selected!;

        var plan = session.Assess(target, default);
        Assert.Equal([ChangeQueueDriver, ConfigureGlobalDefaults, ConfigureUserDefaults], Kinds(plan));
        Assert.True(Has(plan.Findings, FindingCode.QueueDriverMismatch));
        Assert.False(plan.Actions.Single(a => a.Kind == ConfigureUserDefaults).RequiresElevation);

        var machine = session.Apply(plan, ApplyScope.Machine, null, default);
        Assert.True(machine.Succeeded);
        Assert.DoesNotContain(machine.Results, r => r.Action.Kind == ConfigureUserDefaults);
        Assert.Equal(SampleConfig.DriverName(SampleConfig.P4030), scenario.Spooler.Queues.Single(q => q.Name == queue).DriverName);

        var user = session.Apply(plan, ApplyScope.User, null, default);
        Assert.Equal(ActionOutcome.Succeeded, Outcome(user, ConfigureUserDefaults));
        Assert.Single(user.Results);

        var perUser = scenario.Spooler.GetStoredDevMode(queue, DevModeScope.PerUser)!;
        Assert.Equal(SampleConfig.FormName, perUser.FormName);
        Assert.Equal(DmOrientation.Portrait, perUser.Orientation);
        var verification = session.Verify(TargetSelector.For(target), default);
        Assert.Equal(ReadinessLevel.Ready, verification.Readiness);
        Assert.Equal(CheckStatus.Pass, Check(verification, VerificationItem.UserDefaultsConsistent).Status);
    }

    // 6 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void S06_network_printer_by_address_gets_port_queue_and_defaults()
    {
        var scenario = new ScenarioBuilder().WithNetworkPrinter(Address, SampleConfig.P4531).Build();

        var run = scenario.Run(request: new DetectRequest { NetworkAddresses = [Address] });

        var target = run.Selection.Selected!;
        Assert.Equal(PrinterCandidate.NetworkKey(Address), target.Key);
        Assert.Equal(MatchConfidence.Exact, target.Confidence);
        Assert.Equal([PrepareDriverPackage, InstallDriver, CreateTcpIpPort, CreateQueue, CreateForm, ConfigureGlobalDefaults, ConfigureUserDefaults], Kinds(run.Plan!));
        Assert.True(run.Apply!.Succeeded);

        var port = Assert.Single(scenario.Spooler.Ports, p => p.Kind == PortKind.StandardTcpIp);
        Assert.Equal("IP_" + Address, port.Name);
        Assert.Equal(Address, port.HostAddress);
        Assert.Equal(9100, port.PortNumber);
        var queue = Assert.Single(scenario.Spooler.Queues, q => q.PortName == port.Name);
        Assert.Equal(scenario.Config.Connection.NewQueueName, queue.Name);
        Assert.Equal(SampleConfig.DriverName(SampleConfig.P4531), queue.DriverName);

        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
        Assert.Equal("Network 10.20.30.40, port 9100", run.Verification.Summary.Connection);
    }

    [Fact]
    public void S06_an_installed_accepted_driver_is_reused_instead_of_installing_the_preferred_package()
    {
        string xps = SampleConfig.XpsDriverName(SampleConfig.P4531);
        var scenario = new ScenarioBuilder().WithNetworkPrinter(Address, SampleConfig.P4531).WithDriver(xps).Build();

        var run = scenario.Run(request: new DetectRequest { NetworkAddresses = [Address] });

        Assert.Equal([CreateTcpIpPort, CreateQueue, CreateForm, ConfigureGlobalDefaults, ConfigureUserDefaults], Kinds(run.Plan!));
        Assert.True(run.Apply!.Succeeded);
        Assert.Equal(xps, Assert.Single(scenario.Spooler.Queues, q => q.PortName == "IP_" + Address).DriverName);
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
    }

    [Fact]
    public void S06_existing_port_for_the_address_is_reused()
    {
        var scenario = new ScenarioBuilder().WithNetworkPrinter(Address, SampleConfig.P4531).WithTcpIpPort(Address).Build();

        var run = scenario.Run(request: new DetectRequest { NetworkAddresses = [Address] });

        Assert.DoesNotContain(CreateTcpIpPort, Kinds(run.Plan!));
        Assert.Equal("IP_" + Address, run.Plan!.Actions.Single(a => a.Kind == CreateQueue).Parameters["portName"]);
        Assert.True(run.Apply!.Succeeded);
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Operation == nameof(IPrintSpooler.AddStandardTcpIpPort));
        Assert.Single(scenario.Spooler.Ports, p => p.Kind == PortKind.StandardTcpIp);
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
    }

    [Fact]
    public void S06_port_name_taken_by_another_host_gets_a_suffix()
    {
        var scenario = new ScenarioBuilder()
            .WithNetworkPrinter(Address, SampleConfig.P4531)
            .WithTcpIpPort("10.20.30.99", portName: "IP_" + Address)
            .Build();

        var run = scenario.Run(request: new DetectRequest { NetworkAddresses = [Address] });

        Assert.Equal("IP_10.20.30.40_2", run.Plan!.Actions.Single(a => a.Kind == CreateTcpIpPort).Parameters["portName"]);
        Assert.True(run.Apply!.Succeeded);
        Assert.Equal("10.20.30.99", scenario.Spooler.Ports.Single(p => p.Name == "IP_" + Address).HostAddress);
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
    }

    // 7 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void S07_several_printers_need_a_choice_unless_a_preference_narrows_it()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001")
            .WithInstalledUsbPrinter(SampleConfig.P5032, "USB002")
            .Build();
        var detection = scenario.CreateSession().Detect(new DetectRequest(), default);

        var unguided = scenario.CreateSession().SelectAutomatically(detection);
        Assert.Equal(SelectionReason.MultipleCandidates, unguided.Reason);
        Assert.Null(unguided.Selected);
        Assert.Equal(2, unguided.Choices.Count);

        var byOption = scenario.CreateSession(o => o with { PreferredModelId = SampleConfig.P5032 }).SelectAutomatically(detection);
        Assert.Equal(SelectionReason.PreferredByConfiguration, byOption.Reason);
        Assert.Equal(SampleConfig.P5032, byOption.Selected!.Model!.Id);

        var byBranch = new SetupSession(scenario.Config, new BranchOverride { ModelId = SampleConfig.P4030 }, scenario.Options, scenario.Services)
            .SelectAutomatically(detection);
        Assert.Equal(SelectionReason.PreferredByConfiguration, byBranch.Reason);
        Assert.Equal(SampleConfig.P4030, byBranch.Selected!.Model!.Id);
    }

    [Fact]
    public void S07_branch_address_selects_the_network_printer_next_to_a_usb_printer()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030)
            .WithNetworkPrinter(Address, SampleConfig.P5032)
            .WithBranch(new BranchOverride { PrinterAddress = Address })
            .Build();
        var session = scenario.CreateSession();

        var selection = session.SelectAutomatically(session.Detect(new DetectRequest(), default));

        Assert.Equal(SelectionReason.PreferredByConfiguration, selection.Reason);
        Assert.Equal(PrinterCandidate.NetworkKey(Address), selection.Selected!.Key);
        Assert.Contains(Address, scenario.Network.ProbedAddresses);
    }

    // 8 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void S08_offline_printer_is_configured_with_warnings()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter()
            .WithModelDriver()
            .WithUsbQueue(status: PrinterStatusFlags.Offline)
            .WithTicketForm()
            .Build();

        var run = scenario.Run();

        Assert.True(Has(run.Plan!.Findings, FindingCode.PrinterOffline, Severity.Warning));
        Assert.True(run.Plan.CanProceed);
        Assert.Equal([ConfigureGlobalDefaults], Kinds(run.Plan));
        Assert.True(run.Apply!.Succeeded);
        Assert.Equal(ReadinessLevel.ReadyWithWarnings, run.Verification!.Readiness);
        Assert.Equal(CheckStatus.Warning, Check(run.Verification, VerificationItem.PrinterOnline).Status);
        Assert.True(Has(run.Verification.Findings, FindingCode.PrinterOffline));
    }

    // 9 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void S09_no_printer_means_no_candidates()
    {
        var scenario = new ScenarioBuilder().Build();

        var run = scenario.Run();

        Assert.Equal(SelectionReason.NoCandidates, run.Selection.Reason);
        Assert.Empty(run.Detection.Candidates);
        Assert.Null(run.Plan);
        Assert.Empty(scenario.Spooler.Writes);
    }

    // 10 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void S10_driver_package_unavailable_skips_everything_that_needs_it()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(SampleConfig.P4030, driverBound: false).Build();
        scenario.Packages.Failure = new PrinterSetupException(FindingCode.DriverPackageUnavailable,
            "The package is not in the offline folder or cache, and the download failed: no internet connection.");

        var run = scenario.Run();
        var apply = run.Apply!;

        Assert.False(apply.Succeeded);
        var prepare = Result(apply, PrepareDriverPackage);
        Assert.Equal(ActionOutcome.Failed, prepare.Outcome);
        Assert.Equal(FindingCode.DriverPackageUnavailable, prepare.FailureCode);
        foreach (var kind in new[] { InstallDriver, BindUsbDevice, CreateQueue, ConfigureGlobalDefaults, ConfigureUserDefaults })
        {
            Assert.Equal(ActionOutcome.Skipped, Outcome(apply, kind));
            Assert.Contains("depends on a failed action", Result(apply, kind).Detail, StringComparison.Ordinal);
        }
        Assert.Equal(ActionOutcome.Succeeded, Outcome(apply, CreateForm));
        Assert.True(Has(apply.Findings, FindingCode.DriverPackageUnavailable, Severity.Error));
        Assert.Empty(scenario.Installer.Calls);
        Assert.Equal(ReadinessLevel.NotReady, run.Verification!.Readiness);
    }

    // 11 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void S11_invalid_signature_fails_the_package_step()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(SampleConfig.P4030, driverBound: false).Build();
        scenario.Packages.Failure = new PrinterSetupException(FindingCode.DriverSignatureInvalid,
            "The catalog is signed by 'CN=Unknown', which is not a trusted signer.", unchecked((int)0x800B0109));

        var apply = scenario.Run().Apply!;

        var prepare = Result(apply, PrepareDriverPackage);
        Assert.Equal(FindingCode.DriverSignatureInvalid, prepare.FailureCode);
        Assert.Equal(unchecked((int)0x800B0109), prepare.Win32Error);
        Assert.Empty(scenario.Installer.Calls);
    }

    [Fact]
    public void S11_untrusted_signature_reported_by_the_provider_is_refused()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(SampleConfig.P4030, driverBound: false).Build();
        scenario.Packages.Signature = FakeDriverPackageProvider.TrustedSignature with { IsTrusted = false, StatusText = "Untrusted root." };

        var apply = scenario.Run().Apply!;

        Assert.Equal(FindingCode.DriverSignatureInvalid, Result(apply, PrepareDriverPackage).FailureCode);
        Assert.Empty(scenario.Installer.Calls);
    }

    [Fact]
    public void S11_incompatible_package_blocks_the_plan_without_touching_anything()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, driverBound: false)
            .WithPackage(p => p with { MinOsBuild = 22000 })
            .WithPlatform(p => p with { ProductName = "Windows 10 Pro", DisplayVersion = "22H2", Build = 19045 })
            .Build();

        var run = scenario.Run();

        Assert.False(run.Plan!.CanProceed);
        Assert.Contains(run.Plan.Blockers, b => b.Code == FindingCode.DriverPackageIncompatible);
        Assert.All(run.Apply!.Results, r => Assert.Equal(ActionOutcome.Skipped, r.Outcome));
        Assert.Contains(run.Apply.Findings, f => f.Code == FindingCode.DriverPackageIncompatible);
        Assert.Empty(scenario.Spooler.Writes);
        Assert.Empty(scenario.Packages.PreparedPackageIds);
        Assert.Empty(scenario.Installer.Calls);
    }

    // 12 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void S12_two_full_usb_runs_create_nothing_twice()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(SampleConfig.P4030, driverBound: false).Build();

        var first = scenario.Run();
        var counts = (scenario.Spooler.Queues.Count, scenario.Spooler.Ports.Count, scenario.Spooler.Forms.Count, scenario.Spooler.Drivers.Count);
        var second = scenario.Run();

        Assert.Equal(ReadinessLevel.Ready, first.Verification!.Readiness);
        Assert.True(second.Plan!.NothingToDo);
        Assert.Equal(counts, (scenario.Spooler.Queues.Count, scenario.Spooler.Ports.Count, scenario.Spooler.Forms.Count, scenario.Spooler.Drivers.Count));
        Assert.Single(scenario.Spooler.Forms, f => f.Name == SampleConfig.FormName);
        Assert.Equal(ReadinessLevel.Ready, second.Verification!.Readiness);
    }

    [Fact]
    public void S12_two_full_network_runs_create_nothing_twice()
    {
        var scenario = new ScenarioBuilder().WithNetworkPrinter(Address, SampleConfig.P4030).Build();
        var request = new DetectRequest { NetworkAddresses = [Address] };

        scenario.Run(request: request);
        var counts = (scenario.Spooler.Queues.Count, scenario.Spooler.Ports.Count, scenario.Spooler.Forms.Count);
        var second = scenario.Run(request: request);

        Assert.True(second.Plan!.NothingToDo);
        Assert.Equal(counts, (scenario.Spooler.Queues.Count, scenario.Spooler.Ports.Count, scenario.Spooler.Forms.Count));
        Assert.Single(scenario.Spooler.Ports, p => p.HostAddress == Address);
        Assert.Single(scenario.Spooler.Writes, w => w.Operation == nameof(IPrintSpooler.AddQueue));
        Assert.Equal(ReadinessLevel.Ready, second.Verification!.Readiness);
    }

    // 13 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void S13_driver_rejecting_the_new_form_fails_and_writes_no_defaults()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter()
            .WithModelDriver()
            .WithUsbQueue()
            .WithModelDriverProfile(SampleConfig.P4030, p => p with { OmittedForms = [SampleConfig.FormName] })
            .Build();

        var run = scenario.Run();
        var apply = run.Apply!;

        Assert.Equal(ActionOutcome.Succeeded, Outcome(apply, CreateForm));
        var global = Result(apply, ConfigureGlobalDefaults);
        Assert.Equal(ActionOutcome.Failed, global.Outcome);
        Assert.Equal(FindingCode.DriverDoesNotListForm, global.FailureCode);
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Operation == nameof(IPrintSpooler.SetDevMode));
        Assert.Equal(ReadinessLevel.NotReady, run.Verification!.Readiness);
        Assert.Equal(CheckStatus.Fail, Check(run.Verification, VerificationItem.DriverListsForm).Status);
    }

    [Fact]
    public void S13_existing_form_the_driver_does_not_list_blocks_the_plan()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter()
            .WithModelDriver()
            .WithUsbQueue()
            .WithTicketForm()
            .WithModelDriverProfile(SampleConfig.P4030, p => p with { OmittedForms = [SampleConfig.FormName] })
            .Build();

        var run = scenario.Run();

        var blocker = Assert.Single(run.Plan!.Blockers, b => b.Code == FindingCode.DriverDoesNotListForm);
        Assert.Equal("700", blocker.Data["minWidth"]);
        Assert.All(run.Apply!.Results, r => Assert.Equal(ActionOutcome.Skipped, r.Outcome));
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Fact]
    public void S13_driver_custom_paper_of_the_ticket_size_is_used_when_the_form_is_not_listed()
    {
        // The KX v3 driver: no forms-database forms, a custom page size made in its preferences, empty dmFormName.
        var custom = new PaperCapability(541, "Ticket 3.24x6.70", 823, 1702);
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter()
            .WithModelDriver()
            .WithUsbQueue()
            .WithTicketForm()
            .WithModelDriverProfile(SampleConfig.P4030, p => p with
            {
                OmittedForms = [SampleConfig.FormName],
                BuiltInPapers = [.. p.BuiltInPapers, custom],
                ClearsFormName = true,
            })
            .Build();

        var run = scenario.Run();

        Assert.Empty(run.Plan!.Blockers);
        Assert.Equal(ActionOutcome.Succeeded, Outcome(run.Apply!, ConfigureGlobalDefaults));
        Assert.Equal(custom.Id, scenario.Spooler.GetStoredDevMode(Queue4030, DevModeScope.Global)!.PaperSize);
        Assert.Equal(CheckStatus.Pass, Check(run.Verification!, VerificationItem.DriverListsForm).Status);
        Assert.Equal(CheckStatus.Pass, Check(run.Verification!, VerificationItem.DefaultPaper).Status);
        Assert.Equal(CheckStatus.Pass, Check(run.Verification!, VerificationItem.EffectiveDefaults).Status);
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
        Assert.True(scenario.Run().Plan!.NothingToDo);
    }

    // 14 -----------------------------------------------------------------------------------------------------------

    [Fact]
    public void S14_defaults_that_do_not_persist_are_a_failure()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter(correctDefaults: false).Build();
        scenario.Spooler.RevertDevModeWrites.Add(DevModeScope.Global);

        var run = scenario.Run();

        var global = Result(run.Apply!, ConfigureGlobalDefaults);
        Assert.Equal(ActionOutcome.Failed, global.Outcome);
        Assert.Equal(FindingCode.DefaultsNotPersisted, global.FailureCode);
        Assert.Contains(scenario.Spooler.Writes, w => w.Operation == nameof(IPrintSpooler.SetDevMode));
        Assert.Equal(ReadinessLevel.NotReady, run.Verification!.Readiness);
        Assert.Equal(CheckStatus.Fail, Check(run.Verification, VerificationItem.DefaultPaper).Status);
    }

    [Fact]
    public void S14_per_user_override_is_detected_and_fixed_in_user_scope()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter()
            .WithPerUserDefaults(Queue4030, d =>
            {
                d.FormName = "A4";
                d.PaperSize = DmPaper.A4;
            })
            .Build();
        var session = scenario.CreateSession();
        var target = session.SelectAutomatically(session.Detect(new DetectRequest(), default)).Selected!;

        var plan = session.Assess(target, default);
        Assert.Equal([ConfigureUserDefaults], Kinds(plan));
        Assert.True(Has(plan.Findings, FindingCode.UserDefaultsIncorrect, Severity.Warning));
        Assert.False(plan.RequiresElevation);
        Assert.Equal(ReadinessLevel.NotReady, session.Verify(TargetSelector.For(target), default).Readiness);

        Assert.Empty(session.Apply(plan, ApplyScope.Machine, null, default).Results);
        var user = session.Apply(plan, ApplyScope.User, null, default);

        Assert.Equal(ActionOutcome.Succeeded, Outcome(user, ConfigureUserDefaults));
        Assert.Contains(scenario.Spooler.Writes, w => w is { Operation: nameof(IPrintSpooler.SetDevMode), Detail: nameof(DevModeScope.PerUser) });
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w is { Operation: nameof(IPrintSpooler.SetDevMode), Detail: nameof(DevModeScope.Global) });
        var verification = session.Verify(TargetSelector.For(target), default);
        Assert.Equal(ReadinessLevel.Ready, verification.Readiness);
        Assert.Equal(CheckStatus.Pass, Check(verification, VerificationItem.UserDefaultsConsistent).Status);
    }
}
