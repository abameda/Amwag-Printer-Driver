using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Detection;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;
using static Amwag.PrinterSetup.Core.Setup.ActionKind;
using static Amwag.PrinterSetup.Core.Tests.TestHelpers;

namespace Amwag.PrinterSetup.Core.Tests;

/// <summary>
/// One physical network printer reached by several spellings (DNS name and IP address, IPv4 and IPv6, a WSD queue) is one
/// candidate: its existing queue is configured and no second port or queue is created for another spelling.
/// </summary>
public sealed class SameNetworkPrinterTests
{
    private const string HostName = "printer12.branch.local";
    private const string Ip = "10.12.0.50";

    private static DetectionResult Detect(Scenario scenario, params string[] addresses) =>
        scenario.CreateSession().Detect(new DetectRequest { NetworkAddresses = addresses }, default);

    private static string[] Created(Scenario scenario) =>
        [.. scenario.Spooler.Writes
            .Where(w => w.Operation is nameof(IPrintSpooler.AddStandardTcpIpPort) or nameof(IPrintSpooler.AddQueue))
            .Select(w => $"{w.Operation} {w.Target}")];

    // ---- An existing queue and a requested spelling ------------------------------------------------------------------

    [Fact]
    public void Branch_ip_of_a_printer_whose_queue_uses_its_dns_name_configures_that_queue()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithNetworkQueue(HostName, name: "Ticket printer")
            .WithTicketForm()
            .WithNetworkPrinter(Ip, SampleConfig.P4030)
            .WithNetworkAlias(HostName, Ip)
            .WithBranch(new BranchOverride { PrinterAddress = Ip })
            .Build();

        var run = scenario.Run();

        var candidate = Assert.Single(run.Detection.Candidates);
        Assert.Equal(PrinterCandidate.NetworkKey(HostName), candidate.Key);
        Assert.Equal([Ip], candidate.NetworkAliases);
        Assert.Equal("Ticket printer", candidate.Queue!.Name);
        Assert.Contains(candidate.Evidence, e => e.Contains("both lead to 10.12.0.50", StringComparison.Ordinal));
        var merged = Assert.Single(run.Detection.Findings, f => f.Code == FindingCode.DuplicateQueues);
        Assert.Equal((Severity.Info, Ip, "Ticket printer"), (merged.Severity, merged.Data["address"], merged.Data["queue"]));

        Assert.Equal(SelectionReason.PreferredByConfiguration, run.Selection.Reason);
        Assert.DoesNotContain(CreateTcpIpPort, Kinds(run.Plan!));
        Assert.DoesNotContain(CreateQueue, Kinds(run.Plan!));
        Assert.DoesNotContain(ChangeQueuePort, Kinds(run.Plan!));
        Assert.True(run.Apply!.Succeeded);
        Assert.Empty(Created(scenario));
        Assert.Contains(scenario.Spooler.Writes, w => w is { Operation: nameof(IPrintSpooler.SetDevMode), Target: "Ticket printer" });
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
    }

    [Fact]
    public void Branch_dns_name_of_a_printer_whose_queue_uses_its_ip_configures_that_queue()
    {
        const string name = "ticketprinter.branch.local";
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithNetworkQueue("10.20.1.50", name: "Tickets")
            .WithTicketForm()
            .WithNetworkPrinter("10.20.1.50", SampleConfig.P4030)
            .WithNetworkAlias(name, "10.20.1.50")
            .WithBranch(new BranchOverride { PrinterAddress = name })
            .Build();

        var run = scenario.Run();

        var candidate = Assert.Single(run.Detection.Candidates);
        Assert.Equal((PrinterCandidate.NetworkKey("10.20.1.50"), "Tickets"), (candidate.Key, candidate.Queue!.Name));
        Assert.Equal([name], candidate.NetworkAliases);
        Assert.Equal(SelectionReason.PreferredByConfiguration, run.Selection.Reason);
        Assert.True(run.Apply!.Succeeded);
        Assert.Empty(Created(scenario));
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
    }

    [Fact]
    public void Ipv4_mapped_spelling_of_an_existing_queues_address_is_the_same_printer()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithNetworkQueue("192.168.1.20")
            .WithTicketForm()
            .WithNetworkPrinter("192.168.1.20", SampleConfig.P4030)
            .Build();
        var session = scenario.CreateSession();

        // The probe contacts ::ffff:192.168.1.20 as 192.168.1.20, the host of the existing port.
        var detection = Detect(scenario, "::ffff:192.168.1.20");

        var candidate = Assert.Single(detection.Candidates);
        Assert.Equal(PrinterCandidate.NetworkKey("192.168.1.20"), candidate.Key);
        var plan = session.Assess(candidate, default);
        Assert.DoesNotContain(CreateTcpIpPort, Kinds(plan));
        Assert.DoesNotContain(CreateQueue, Kinds(plan));
    }

    [Fact]
    public void Ipv6_queue_and_ipv4_address_with_the_same_serial_and_exact_model_are_one_printer()
    {
        const string ipv6 = "2001:db8::50";
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithNetworkQueue(ipv6, name: "Ticket printer")
            .WithNetworkIdentity(SampleConfig.NetworkIdentity(ipv6, SampleConfig.P4030, serialNumber: "VCF5X09876"))
            .WithNetworkIdentity(SampleConfig.NetworkIdentity(Ip, SampleConfig.P4030, serialNumber: "VCF5X09876"))
            .WithBranch(new BranchOverride { PrinterAddress = Ip })
            .Build();

        var detection = Detect(scenario);

        var candidate = Assert.Single(detection.Candidates);
        Assert.Equal((PrinterCandidate.NetworkKey(ipv6), "Ticket printer"), (candidate.Key, candidate.Queue!.Name));
        Assert.Equal([Ip], candidate.NetworkAliases);
        Assert.Contains(candidate.Evidence, e => e.Contains("same serial number VCF5X09876 and model P-4030DN", StringComparison.Ordinal));
        Assert.Same(candidate, scenario.CreateSession().SelectAutomatically(detection).Selected);
    }

    [Fact]
    public void Requested_ip_of_a_class_driver_queues_printer_gets_that_queue_repaired_instead_of_a_new_one()
    {
        // Neither spelling answers SNMP, so the operator names the model; the printer's existing queue gets the approved driver.
        var scenario = new ScenarioBuilder()
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue(HostName, name: "Ticket printer", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkPrinter(Ip, modelId: null)
            .WithNetworkAlias(HostName, Ip)
            .Build();
        var session = scenario.CreateSession();

        var detection = Detect(scenario, Ip);

        var candidate = Assert.Single(detection.Candidates);
        Assert.Equal((PrinterCandidate.NetworkKey(HostName), "Ticket printer"), (candidate.Key, candidate.Queue?.Name));
        Assert.Null(candidate.Model);
        var asserted = session.Resolve(detection, new TargetSelector(candidate.Key, SampleConfig.P4030, ModelAssertedByUser: true))!;
        var plan = session.Assess(asserted, default);
        Assert.Contains(ChangeQueueDriver, Kinds(plan));
        Assert.DoesNotContain(CreateTcpIpPort, Kinds(plan));
        Assert.DoesNotContain(CreateQueue, Kinds(plan));
    }

    [Fact]
    public void Printer_that_is_not_configured_is_named_by_every_spelling_it_was_reached_by()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4531)
            .WithNetworkQueue(HostName, SampleConfig.P4531)
            .WithNetworkIdentity(new NetworkIdentity
            {
                Address = Ip,
                RawPortReachable = true,
                SnmpResponded = true,
                SysDescr = "TA_UTAX Printing System",
                SysObjectId = "1.3.6.1.4.1.1347.41",
                VendorModelName = "P-4532DN",
            })
            .WithNetworkAlias(HostName, Ip)
            .Build();

        var detection = Detect(scenario, Ip);

        // No queue-less candidate for the IP either: it is the same, unsupported printer.
        Assert.Empty(detection.Candidates);
        var finding = Assert.Single(detection.Findings, f => f.Code == FindingCode.NetworkIdentityMismatch);
        Assert.Contains(HostName, finding.Detail, StringComparison.Ordinal);
        Assert.Contains(Ip, finding.Detail, StringComparison.Ordinal);
    }

    // ---- Several queues of one printer ---------------------------------------------------------------------------------

    /// <summary>One P-4030DN with an A4 office queue on its DNS name and the ticket queue (already set up) on its IP address.</summary>
    private static ScenarioBuilder OfficeAndTicketQueuesOfOnePrinter(BranchOverride? branch = null) =>
        new ScenarioBuilder()
            .WithModelDriver()
            .WithNetworkQueue(HostName, name: "A4 office")
            .WithNetworkQueue(Ip, name: "Tickets")
            .WithTicketForm()
            .WithCorrectDefaults("Tickets")
            .WithNetworkPrinter(Ip, SampleConfig.P4030)
            .WithNetworkAlias(HostName, Ip)
            .WithBranch(branch);

    [Fact]
    public void Queues_on_the_dns_name_and_on_the_ip_of_one_printer_stay_separate_candidates_reported_as_duplicates()
    {
        var detection = Detect(OfficeAndTicketQueuesOfOnePrinter().Build());

        // Each queue host keeps its own candidate, keyed by its own host: neither queue is folded into the other.
        Assert.Equal(2, detection.Candidates.Count);
        var office = Assert.Single(detection.Candidates, c => c.Key == PrinterCandidate.NetworkKey(HostName));
        var tickets = Assert.Single(detection.Candidates, c => c.Key == PrinterCandidate.NetworkKey(Ip));
        Assert.Equal(("A4 office", "Tickets"), (office.Queue!.Name, tickets.Queue!.Name));
        Assert.All(detection.Candidates, c => Assert.Empty(c.AdditionalQueues));
        Assert.All(detection.Candidates, c => Assert.Empty(c.NetworkAliases));
        Assert.All(detection.Candidates, c => Assert.Equal((SampleConfig.P4030, MatchConfidence.Exact), (c.Model?.Id, c.Confidence)));
        Assert.Contains(office.Evidence, e => e.Contains("both lead to 10.12.0.50", StringComparison.Ordinal));

        var duplicates = Assert.Single(detection.Findings, f => f.Code == FindingCode.DuplicateQueues);
        Assert.Equal((Severity.Warning, "A4 office, Tickets"), (duplicates.Severity, duplicates.Data["queues"]));
        Assert.Contains($"'A4 office' on {HostName} and 'Tickets' on {Ip} print to the same printer", duplicates.Detail, StringComparison.Ordinal);
        Assert.Contains("the others keep their settings", duplicates.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Branch_address_naming_the_ticket_queues_spelling_selects_that_queue_of_a_printer_with_a_queue_on_each_spelling()
    {
        // (a) branch.json names the IP address, the host of the ticket queue.
        var scenario = OfficeAndTicketQueuesOfOnePrinter(new BranchOverride { PrinterAddress = Ip }).Build();
        string officeForm = scenario.Spooler.GetStoredDevMode("A4 office", DevModeScope.Global)!.FormName;

        var run = scenario.Run();

        var atAddress = Assert.Single(run.Detection.Candidates, c => c.IsAtAddress(Ip));
        Assert.Equal((PrinterCandidate.NetworkKey(Ip), "Tickets"), (atAddress.Key, atAddress.Queue!.Name));
        Assert.Equal(atAddress.Key, run.Selection.Selected!.Key);
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Target == "A4 office");
        Assert.Equal(officeForm, scenario.Spooler.GetStoredDevMode("A4 office", DevModeScope.Global)!.FormName);
    }

    [Fact]
    public void Printer_with_a_queue_on_each_spelling_offers_both_queues_without_a_preference()
    {
        // (b) Repair and (c) Setup without a preference: two eligible printers, never one silently folded into the other;
        // the ticket queue keeps its own candidate, so Repair's ticket-form rule can find it.
        var scenario = OfficeAndTicketQueuesOfOnePrinter().Build();

        var detection = Detect(scenario);

        Assert.Equal(2, detection.Candidates.Count(c => c.IsEligible));
        var tickets = Assert.Single(detection.Candidates, c => c.Queue?.Name == "Tickets");
        Assert.Equal(SampleConfig.FormName, scenario.Spooler.GetStoredDevMode(tickets.Queue!.Name, DevModeScope.Global)!.FormName);
        Assert.NotEqual(SampleConfig.FormName, scenario.Spooler.GetStoredDevMode("A4 office", DevModeScope.Global)!.FormName);
    }

    [Fact]
    public void Diagnostics_of_the_branch_address_verifies_the_ticket_queue_of_a_printer_with_a_queue_on_each_spelling()
    {
        // (d) The candidate at the branch address is the correctly set-up ticket queue, not the A4 office queue.
        var scenario = OfficeAndTicketQueuesOfOnePrinter(new BranchOverride { PrinterAddress = Ip }).WithMode(SetupMode.Diagnostics).Build();
        var session = scenario.CreateSession();

        var atAddress = Assert.Single(session.Detect(new DetectRequest(), default).Candidates, c => c.IsAtAddress(Ip));
        var report = session.Verify(TargetSelector.For(atAddress), default);

        Assert.Equal(("Tickets", ReadinessLevel.Ready), (report.Summary.QueueName, report.Readiness));
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Fact]
    public void Requested_spelling_without_a_queue_is_an_alias_of_each_queue_host_of_its_printer()
    {
        const string ticketsName = "tickets.branch.local";
        var scenario = OfficeAndTicketQueuesOfOnePrinter(new BranchOverride { PrinterAddress = ticketsName })
            .WithNetworkAlias(ticketsName, Ip)
            .Build();

        var detection = Detect(scenario);

        // Both queues reach the printer IT named, so the preference sees both: the operator (or Repair's rule) decides.
        Assert.Equal(2, detection.Candidates.Count);
        Assert.All(detection.Candidates, c => Assert.Equal([ticketsName], c.NetworkAliases));
        Assert.All(detection.Candidates, c => Assert.True(c.IsAtAddress(ticketsName)));
        Assert.DoesNotContain(detection.Candidates, c => c.Queue is null);
        var alias = Assert.Single(detection.Findings, f => f.Code == FindingCode.DuplicateQueues && f.Data.GetValueOrDefault("address") == ticketsName);
        Assert.Equal(Severity.Info, alias.Severity);
        Assert.Contains("'A4 office'", alias.Detail, StringComparison.Ordinal);
        Assert.Contains("'Tickets'", alias.Detail, StringComparison.Ordinal);
        Assert.Contains("no second queue is created", alias.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Queue_hosts_of_one_printer_are_rejected_together_when_the_device_names_an_unsupported_model()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4531)
            .WithNetworkQueue(HostName, SampleConfig.P4531, name: "A4 office")
            .WithNetworkQueue(Ip, SampleConfig.P4531, name: "Tickets")
            .WithNetworkIdentity(new NetworkIdentity
            {
                Address = Ip,
                RawPortReachable = true,
                SnmpResponded = true,
                SysDescr = "TA_UTAX Printing System",
                SysObjectId = "1.3.6.1.4.1.1347.41",
                VendorModelName = "P-4532DN",
            })
            .WithNetworkAlias(HostName, Ip)
            .Build();

        var detection = Detect(scenario);

        Assert.Empty(detection.Candidates);
        var mismatch = Assert.Single(detection.Findings, f => f.Code == FindingCode.NetworkIdentityMismatch);
        Assert.Contains("'A4 office', 'Tickets'", mismatch.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(detection.Findings, f => f.Code == FindingCode.DuplicateQueues);
    }

    // ---- A queue host that names no model, beside a queue host of the same printer with a candidate ---------------------

    /// <summary>
    /// One printer whose SNMP is off: the TA driver's 'TA queue' on its IP address and the class driver's 'Tickets' (Windows gave
    /// it to the ticket printer) on its DNS name, seeded in either order.
    /// </summary>
    private static ScenarioBuilder ClassDriverQueueBesideTaQueue(bool classDriverFirst)
    {
        var builder = new ScenarioBuilder().WithModelDriver().WithDriver(SampleConfig.MicrosoftPcl6ClassDriver);
        if (classDriverFirst)
            builder.WithNetworkQueue(HostName, name: "Tickets", driverName: SampleConfig.MicrosoftPcl6ClassDriver).WithNetworkQueue(Ip, name: "TA queue");
        else
            builder.WithNetworkQueue(Ip, name: "TA queue").WithNetworkQueue(HostName, name: "Tickets", driverName: SampleConfig.MicrosoftPcl6ClassDriver);
        return builder
            .WithNetworkPrinter(Ip, modelId: null)
            .WithNetworkAlias(HostName, Ip)
            .WithTicketForm();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Class_driver_queue_on_another_spelling_of_a_printer_that_names_no_model_is_an_unidentified_choice(bool classDriverFirst)
    {
        var scenario = ClassDriverQueueBesideTaQueue(classDriverFirst).Build();

        var run = scenario.Run();

        Assert.Contains(HostName, scenario.Network.ProbedAddresses);
        Assert.Equal(2, run.Detection.Candidates.Count);
        var ta = Assert.Single(run.Detection.Candidates, c => c.Key == PrinterCandidate.NetworkKey(Ip));
        Assert.Equal(("TA queue", MatchConfidence.DriverOnly), (ta.Queue!.Name, ta.Confidence));
        var tickets = Assert.Single(run.Detection.Candidates, c => c.Key == PrinterCandidate.NetworkKey(HostName));
        Assert.Null(tickets.Model);
        Assert.Equal((TargetKind.NetworkAddress, MatchConfidence.None, "Tickets", HostName, HostName, true),
            (tickets.Kind, tickets.Confidence, tickets.Queue?.Name, tickets.NetworkAddress, tickets.NetworkIdentity?.Address, tickets.PhysicallyAvailable));
        Assert.Empty(tickets.NetworkAliases);
        Assert.Empty(run.Detection.RejectedQueues);

        var duplicates = Assert.Single(run.Detection.Findings, f => f.Code == FindingCode.DuplicateQueues);
        Assert.Equal(Severity.Warning, duplicates.Severity);
        Assert.Equal(["TA queue", "Tickets"], duplicates.Data["queues"].Split(", ").Order(StringComparer.Ordinal));
        Assert.Contains("print to the same printer", duplicates.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot be chosen", duplicates.Detail, StringComparison.Ordinal);

        // The operator chooses (silent: exit 4); the TA queue is not configured in the other queue's place.
        Assert.Equal(SelectionReason.MultipleCandidates, run.Selection.Reason);
        Assert.Equal(2, run.Selection.Choices.Count);
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Fact]
    public void Branch_address_on_the_ta_queues_spelling_selects_that_queue_and_the_class_driver_queue_of_the_printer_is_named()
    {
        var scenario = ClassDriverQueueBesideTaQueue(classDriverFirst: true).WithBranch(new BranchOverride { PrinterAddress = Ip }).Build();

        var run = scenario.Run();

        Assert.Equal((SelectionReason.PreferredByConfiguration, "TA queue"), (run.Selection.Reason, run.Selection.Selected?.Queue?.Name));
        var duplicates = Assert.Single(run.Detection.Findings, f => f.Code == FindingCode.DuplicateQueues && f.Severity == Severity.Warning);
        Assert.Contains($"'Tickets' on {HostName}", duplicates.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Target == "Tickets");
    }

    [Fact]
    public void Class_driver_queue_host_that_does_not_answer_is_named_with_the_printers_queues_as_a_choice_whose_model_must_be_named()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue(Ip, name: "TA queue")
            .WithNetworkQueue(HostName, name: "Tickets", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkPrinter(Ip, modelId: null)
            // The name leads to the printer's address, but its own probe got no answer on the print port.
            .WithNetworkIdentity(new NetworkIdentity { Address = HostName, ResolvedAddress = Ip, Error = "No response within 2000 ms." })
            .Build();

        var detection = Detect(scenario);

        var tickets = Assert.Single(detection.Candidates, c => c.Key == PrinterCandidate.NetworkKey(HostName));
        Assert.Null(tickets.Model);
        Assert.False(tickets.PhysicallyAvailable);
        var duplicates = Assert.Single(detection.Findings, f => f.Code == FindingCode.DuplicateQueues);
        Assert.Contains("They are separate choices", duplicates.Detail, StringComparison.Ordinal);
        Assert.Contains($"'Tickets' on {HostName} does not answer on the print port; its model must be named before it can be set up.", duplicates.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot be chosen", duplicates.Detail, StringComparison.Ordinal);
        Assert.Contains(detection.Findings, f => f.Code == FindingCode.NetworkPrinterUnreachable && f.Data.GetValueOrDefault("address") == HostName);
    }

    [Fact]
    public void Queue_host_whose_drivers_name_different_models_stays_left_unchanged_beside_a_queue_host_with_a_candidate()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithModelDriver(SampleConfig.P5032)
            .WithNetworkQueue(Ip, SampleConfig.P4030, name: "TA queue")
            .WithNetworkQueue(HostName, SampleConfig.P4030, name: "Tickets")
            .WithNetworkQueue(HostName, SampleConfig.P5032, name: "Office")
            .WithNetworkPrinter(Ip, modelId: null)
            .WithNetworkAlias(HostName, Ip)
            .Build();

        var detection = Detect(scenario);

        Assert.Equal(PrinterCandidate.NetworkKey(Ip), Assert.Single(detection.Candidates).Key);
        Assert.Equal(["Office", "Tickets"], detection.RejectedQueues.Order(StringComparer.Ordinal));
        Assert.Equal(HostName, Assert.Single(detection.Findings, f => f.Code == FindingCode.QueueLeftUnchanged).Data["address"]);
    }

    [Fact]
    public void Queue_host_of_another_manufacturers_driver_beside_a_queue_host_with_a_candidate_is_no_candidate()
    {
        // The IPv4-mapped spelling leads to the same address without a probe; an HP driver's queue is no TA printer's.
        const string hpDriver = "HP Universal Printing PCL 6";
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithDriver(hpDriver)
            .WithNetworkQueue(Ip, name: "TA queue")
            .WithNetworkQueue("::ffff:" + Ip, name: "Office HP", driverName: hpDriver)
            .WithNetworkPrinter(Ip, modelId: null)
            .Build();

        var detection = Detect(scenario);

        Assert.Equal("TA queue", Assert.Single(detection.Candidates).Queue!.Name);
        Assert.DoesNotContain(detection.Findings, f => f.Code == FindingCode.DuplicateQueues);
    }

    [Fact]
    public void Two_requested_spellings_of_a_printer_without_a_queue_create_one_port_and_one_queue()
    {
        var scenario = new ScenarioBuilder()
            .WithNetworkPrinter(Ip, SampleConfig.P4030)
            .WithNetworkAlias(HostName, Ip)
            .WithBranch(new BranchOverride { PrinterAddress = Ip })
            .Build();

        var run = scenario.Run(request: new DetectRequest { NetworkAddresses = [HostName] });

        var candidate = Assert.Single(run.Detection.Candidates);
        Assert.Equal(PrinterCandidate.NetworkKey(HostName), candidate.Key);
        Assert.Equal([Ip], candidate.NetworkAliases);
        Assert.Equal(SelectionReason.PreferredByConfiguration, run.Selection.Reason);
        Assert.True(run.Apply!.Succeeded);
        Assert.Equal([$"AddStandardTcpIpPort IP_{HostName}", $"AddQueue {scenario.Config.Connection.NewQueueName}"], Created(scenario));
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
    }

    // ---- Distinct printers stay distinct --------------------------------------------------------------------------------

    [Fact]
    public void Printers_with_their_own_addresses_and_serial_numbers_stay_two_candidates()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithNetworkQueue(HostName, name: "Ticket printer")
            .WithNetworkIdentity(SampleConfig.NetworkIdentity(HostName, SampleConfig.P4030, resolvedAddress: "10.12.0.51"))
            .WithNetworkPrinter(Ip, SampleConfig.P4030)
            .Build();

        var detection = Detect(scenario, Ip);

        Assert.Equal(2, detection.Candidates.Count);
        Assert.All(detection.Candidates, c => Assert.Empty(c.NetworkAliases));
        Assert.DoesNotContain(detection.Findings, f => f.Code == FindingCode.DuplicateQueues);
    }

    [Theory]
    [InlineData("0000000", SampleConfig.P4030)] // a placeholder serial
    [InlineData("N/A", SampleConfig.P4030)]
    [InlineData("VCF5X09876", SampleConfig.P5032)] // the same serial, but another model
    public void Serial_number_alone_does_not_merge_unless_distinctive_and_of_the_same_exact_model(string serial, string otherModel)
    {
        var scenario = new ScenarioBuilder()
            .WithNetworkIdentity(SampleConfig.NetworkIdentity("10.0.0.5", SampleConfig.P4030, serialNumber: serial))
            .WithNetworkIdentity(SampleConfig.NetworkIdentity("10.0.0.6", otherModel, serialNumber: serial))
            .Build();

        Assert.Equal(2, Detect(scenario, "10.0.0.5", "10.0.0.6").Candidates.Count);
    }

    [Fact]
    public void Same_serial_with_only_a_probable_model_is_not_merged()
    {
        var probable = new NetworkIdentity { Address = "10.0.0.6", RawPortReachable = true, SnmpResponded = true, SysDescr = "TA P-4030DN", SerialNumber = "VCF5X09876" };
        var scenario = new ScenarioBuilder()
            .WithNetworkIdentity(SampleConfig.NetworkIdentity("10.0.0.5", SampleConfig.P4030, serialNumber: "VCF5X09876"))
            .WithNetworkIdentity(probable)
            .Build();

        Assert.Equal(2, Detect(scenario, "10.0.0.5", "10.0.0.6").Candidates.Count);
    }

    // ---- WSD: the device's network address is not known ------------------------------------------------------------

    [Fact]
    public void Requested_address_without_a_queue_next_to_a_wsd_queue_of_its_model_is_a_possible_duplicate()
    {
        var scenario = WsdQueue(SampleConfig.P4030).WithNetworkPrinter(Ip, SampleConfig.P4030).Build();

        var detection = Detect(scenario, Ip);

        Assert.Equal(2, detection.Candidates.Count);
        var warning = Assert.Single(detection.Findings, f => f.Code == FindingCode.DuplicateQueues);
        Assert.Equal((Severity.Warning, Ip, "TA P-4030DN (WSD)"), (warning.Severity, warning.Data["address"], warning.Data["queue"]));
    }

    [Fact]
    public void Wsd_queue_of_another_model_is_no_possible_duplicate()
    {
        var scenario = WsdQueue(SampleConfig.P5032).WithNetworkPrinter(Ip, SampleConfig.P4030).Build();

        Assert.DoesNotContain(Detect(scenario, Ip).Findings, f => f.Code == FindingCode.DuplicateQueues);
    }

    // ---- Spellings that cannot be probed or resolved -----------------------------------------------------------------

    [Fact]
    public void Queue_on_an_ip_that_was_not_probed_is_the_printer_of_a_requested_name_that_resolves_to_it()
    {
        const string name = "ticketprinter.branch.local";
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithNetworkQueue(Ip, name: "Tickets")
            .WithTicketForm()
            .WithNetworkPrinter(Ip, SampleConfig.P4030)
            .WithNetworkAlias(name, Ip)
            .Build();
        var session = scenario.CreateSession();

        // An IP address needs no probe to be compared: the requested name resolves to the queue's address.
        var detection = session.Detect(new DetectRequest { NetworkAddresses = [name], ProbeExistingNetworkQueues = false }, default);

        var candidate = Assert.Single(detection.Candidates);
        Assert.Equal((PrinterCandidate.NetworkKey(Ip), "Tickets"), (candidate.Key, candidate.Queue!.Name));
        Assert.Equal([name], candidate.NetworkAliases);
        var plan = session.Assess(candidate, default);
        Assert.DoesNotContain(CreateTcpIpPort, Kinds(plan));
        Assert.DoesNotContain(CreateQueue, Kinds(plan));
    }

    [Fact]
    public void Requested_address_next_to_a_queue_of_its_model_on_a_name_that_did_not_resolve_is_a_possible_duplicate()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithNetworkQueue(HostName, name: "Ticket printer")
            .WithNetworkPrinter(Ip, SampleConfig.P4030)
            .Build();

        var detection = Detect(scenario, Ip);

        Assert.Equal(2, detection.Candidates.Count);
        var warning = Assert.Single(detection.Findings, f => f.Code == FindingCode.DuplicateQueues);
        Assert.Equal((Severity.Warning, Ip, "Ticket printer"), (warning.Severity, warning.Data["address"], warning.Data["queue"]));
        Assert.Contains(HostName, warning.Detail);
    }

    [Fact]
    public void Requested_printer_without_its_model_next_to_a_queue_on_a_name_that_did_not_resolve_is_a_possible_duplicate()
    {
        // SNMP is off at the requested address, so the operator names the model; the queue's host name does not resolve.
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithNetworkQueue(HostName, name: "Ticket printer")
            .WithNetworkPrinter(Ip, modelId: null)
            .WithBranch(new BranchOverride { PrinterAddress = Ip })
            .Build();

        var detection = Detect(scenario);

        var unidentified = Assert.Single(detection.Candidates, c => c.Key == PrinterCandidate.NetworkKey(Ip));
        Assert.Null(unidentified.Model);
        var warning = Assert.Single(detection.Findings, f => f.Code == FindingCode.DuplicateQueues);
        Assert.Equal((Severity.Warning, Ip, "Ticket printer", SampleConfig.P4030), (warning.Severity, warning.Data["address"], warning.Data["queue"], warning.Data["model"]));
        Assert.Contains("does not report its model", warning.Detail, StringComparison.Ordinal);
        Assert.Contains(HostName, warning.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Requested_printer_without_its_model_next_to_a_wsd_queue_of_any_model_is_a_possible_duplicate()
    {
        var scenario = WsdQueue(SampleConfig.P5032).WithNetworkPrinter(Ip, modelId: null).Build();

        var detection = Detect(scenario, Ip);

        var warning = Assert.Single(detection.Findings, f => f.Code == FindingCode.DuplicateQueues);
        Assert.Equal((Severity.Warning, Ip, "TA P-5032DN (WSD)", SampleConfig.P5032), (warning.Severity, warning.Data["address"], warning.Data["queue"], warning.Data["model"]));
    }

    // ---- Queues that yield no candidate but may be the requested printer's ------------------------------------------------

    [Theory]
    [InlineData(SampleConfig.MicrosoftPcl6ClassDriver)] // the class driver Windows gave the ticket printer's queue
    [InlineData("TA P-4532DN KX")] // a TA driver of no supported model
    public void Queue_whose_driver_names_no_model_on_a_name_that_did_not_resolve_is_a_possible_duplicate_of_a_requested_printer_without_its_model(string driver)
    {
        // The queue's driver names no single model, so the queue is no candidate; its host name does not resolve.
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithDriver(driver)
            .WithNetworkQueue(HostName, name: "Ticket printer", driverName: driver)
            .WithNetworkPrinter(Ip, modelId: null)
            .WithTicketForm()
            .WithBranch(new BranchOverride { PrinterAddress = Ip })
            .Build();
        var session = scenario.CreateSession();

        var detection = session.Detect(new DetectRequest(), default);

        Assert.Contains(HostName, scenario.Network.ProbedAddresses);
        Assert.Equal(PrinterCandidate.NetworkKey(Ip), Assert.Single(detection.Candidates).Key);
        var warning = Assert.Single(detection.Findings, f => f.Code == FindingCode.DuplicateQueues);
        Assert.Equal((Severity.Warning, Ip, "Ticket printer", driver),
            (warning.Severity, warning.Data["address"], warning.Data["queue"], warning.Data["driver"]));
        Assert.False(warning.Data.ContainsKey("model"));
        Assert.Contains("may be for any model", warning.Detail, StringComparison.Ordinal);
        Assert.Contains(HostName, warning.Detail, StringComparison.Ordinal);
        Assert.Contains("once its model is named", warning.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Class_driver_queue_on_a_name_the_probe_limit_left_out_is_a_possible_duplicate()
    {
        var scenario = new ScenarioBuilder()
            .WithConfig(c => c with { Connection = c.Connection with { MaxNetworkProbes = 2 } })
            .WithModelDriver()
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("10.0.0.77", name: "A other", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue(HostName, name: "Ticket printer", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkPrinter(Ip, modelId: null)
            .WithNetworkAlias(HostName, Ip)
            .WithBranch(new BranchOverride { PrinterAddress = Ip })
            .Build();

        var detection = Detect(scenario);

        Assert.DoesNotContain(HostName, scenario.Network.ProbedAddresses);
        // The queue on an IP address that leads elsewhere is compared, so only the queue on the unprobed name is named.
        var warning = Assert.Single(detection.Findings, f => f.Code == FindingCode.DuplicateQueues);
        Assert.Equal((Ip, "Ticket printer"), (warning.Data["address"], warning.Data["queue"]));
    }

    /// <summary>
    /// The shipped probe limit (8) with one printer reached as printer12 and printer12.branch.local (SNMP off): the TA driver's
    /// 'A4 office' on printer12 and the class driver's 'Tickets' (on the ticket paper) on <paramref name="ticketHost"/>, with six
    /// other class-driver queues to other printers enumerated between them. The branch names the printer's IP address.
    /// </summary>
    private static ScenarioBuilder TicketQueueBehindOtherQueueHosts(string ticketHost, int maxNetworkProbes = 8)
    {
        var builder = new ScenarioBuilder()
            .WithConfig(c => c with { Connection = c.Connection with { MaxNetworkProbes = maxNetworkProbes } })
            .WithModelDriver()
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("printer12", name: "A4 office");
        foreach (int n in Enumerable.Range(71, 6))
            builder.WithNetworkQueue($"10.0.0.{n}", name: $"Office {n}", driverName: SampleConfig.MicrosoftPcl6ClassDriver);
        return builder
            .WithNetworkQueue(ticketHost, name: "Tickets", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkPrinter(Ip, modelId: null)
            .WithNetworkAlias("printer12", Ip)
            .WithNetworkAlias(HostName, Ip)
            .WithTicketForm()
            .WithCorrectDefaults("Tickets")
            .WithBranch(new BranchOverride { PrinterAddress = Ip });
    }

    [Theory]
    [InlineData(HostName)]
    [InlineData("10.12.0.99")] // an IP address that no probed spelling leads to is not known either
    public void Class_driver_queue_whose_host_the_probe_limit_left_out_is_reported_as_not_checked(string ticketHost)
    {
        var scenario = TicketQueueBehindOtherQueueHosts(ticketHost).Build();

        var detection = Detect(scenario);

        Assert.Equal(8, scenario.Network.ProbedAddresses.Count);
        Assert.DoesNotContain(ticketHost, scenario.Network.ProbedAddresses);
        Assert.DoesNotContain(detection.Candidates, c => c.Queue?.Name == "Tickets");
        var notChecked = Assert.Single(detection.Findings, f => f.Code == FindingCode.NetworkIdentityUnavailable && f.Data.GetValueOrDefault("queue") == "Tickets");
        Assert.Equal((Severity.Warning, ticketHost), (notChecked.Severity, notChecked.Data["address"]));
        Assert.Equal($"Queue 'Tickets' on {ticketHost} was not checked because the limit of network probes was reached; it may be the ticket printer's queue. "
            + "Raise connection.maxNetworkProbes in the configuration, or name the ticket queue's host with --address or printerAddress.", notChecked.Detail);
        // The other printers' queues were probed (they did not answer), so they are not named.
        Assert.DoesNotContain(detection.Findings, f => f.Code == FindingCode.NetworkIdentityUnavailable && f.Data.GetValueOrDefault("queue") is { } q && q.StartsWith("Office", StringComparison.Ordinal));

        // The assembler lists the queue for the selection (SetupSession copies the list into DetectionResult.UncheckedQueues).
        var assembler = new CandidateAssembler(new ModelMatcher(scenario.Config.Models), detection.Snapshot, scenario.Config.Connection.RawPortNumber);
        assembler.Assemble([Ip], [ticketHost]);
        Assert.Equal(["Tickets"], assembler.UncheckedQueues);
    }

    [Fact]
    public void Class_driver_queue_whose_host_was_probed_is_not_reported_as_not_checked()
    {
        var scenario = TicketQueueBehindOtherQueueHosts(HostName, maxNetworkProbes: 20).Build();

        var detection = Detect(scenario);

        Assert.Contains(HostName, scenario.Network.ProbedAddresses);
        Assert.Contains(detection.Candidates, c => c.Queue?.Name == "Tickets");
        Assert.DoesNotContain(detection.Findings, f => f.Code == FindingCode.NetworkIdentityUnavailable && f.Data.ContainsKey("queue"));
        var assembler = new CandidateAssembler(new ModelMatcher(scenario.Config.Models), detection.Snapshot, scenario.Config.Connection.RawPortNumber);
        assembler.Assemble([Ip], []);
        Assert.Empty(assembler.UncheckedQueues);
    }

    [Fact]
    public void A_queue_left_unchecked_keeps_a_printer_named_by_another_spelling_from_being_chosen_alone()
    {
        // The branch names the printer's IP address, the host of none of its queues: 'Tickets' on the unprobed name may be
        // another queue of it (here it is, on the ticket paper), so 'A4 office' is offered, not chosen.
        var scenario = TicketQueueBehindOtherQueueHosts(HostName).Build();
        var session = scenario.CreateSession();

        var detection = session.Detect(new DetectRequest(), default);
        var selection = session.SelectAutomatically(detection);

        Assert.Equal(["Tickets"], detection.UncheckedQueues);
        Assert.Equal((SelectionReason.MultipleCandidates, null), (selection.Reason, selection.Selected));
        Assert.Equal(["A4 office"], selection.Choices.Select(c => c.Queue?.Name));
    }

    [Fact]
    public void An_address_that_is_the_host_of_a_queue_chooses_it_although_a_queue_was_left_unchecked()
    {
        // The remedy the warning gives: the host of the ticket queue, named with printerAddress, decides.
        var scenario = TicketQueueBehindOtherQueueHosts(HostName, maxNetworkProbes: 7)
            .WithBranch(new BranchOverride { PrinterAddress = "printer12" })
            .Build();
        var session = scenario.CreateSession();

        var detection = session.Detect(new DetectRequest(), default);
        var selection = session.SelectAutomatically(detection);

        Assert.Equal(["Tickets"], detection.UncheckedQueues);
        Assert.Equal(SelectionReason.PreferredByConfiguration, selection.Reason);
        Assert.Equal("A4 office", selection.Selected?.Queue?.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(SampleConfig.P4030)] // the configured model: the unchecked queue's printer may be of it as well
    public void Without_an_address_a_queue_left_unchecked_keeps_the_only_printer_from_being_chosen_alone(string? modelId)
    {
        var scenario = TicketQueueBehindOtherQueueHosts(HostName, maxNetworkProbes: 7)
            .WithBranch(modelId is null ? null : new BranchOverride { ModelId = modelId })
            .Build();
        var session = scenario.CreateSession();

        var detection = session.Detect(new DetectRequest(), default);
        var selection = session.SelectAutomatically(detection);

        Assert.Equal(["Tickets"], detection.UncheckedQueues);
        Assert.Equal((SelectionReason.MultipleCandidates, null), (selection.Reason, selection.Selected));
        Assert.Equal(["A4 office"], selection.Choices.Select(c => c.Queue?.Name));
    }

    [Fact]
    public void Class_driver_wsd_queue_is_a_possible_duplicate_of_a_requested_printer_whatever_its_model()
    {
        // Without its model (SNMP off), and exactly identified: the WSD queue's driver says nothing about its printer.
        var silent = ClassDriverWsdQueue().WithNetworkPrinter(Ip, modelId: null).Build();
        var exact = ClassDriverWsdQueue().WithNetworkPrinter(Ip, SampleConfig.P4030).Build();

        foreach (var scenario in new[] { silent, exact })
        {
            var detection = Detect(scenario, Ip);

            Assert.Equal(PrinterCandidate.NetworkKey(Ip), Assert.Single(detection.Candidates).Key);
            var warning = Assert.Single(detection.Findings, f => f.Code == FindingCode.DuplicateQueues);
            Assert.Equal((Severity.Warning, Ip, "Ticket printer (WSD)"), (warning.Severity, warning.Data["address"], warning.Data["queue"]));
            Assert.Contains("WSD queue 'Ticket printer (WSD)' may be for any model", warning.Detail, StringComparison.Ordinal);
            Assert.Contains("network address is not known", warning.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Queue_on_an_unresolved_name_that_detection_does_not_count_as_a_ta_printers_is_no_possible_duplicate()
    {
        // Another manufacturer's driver: detection does not probe that host either.
        const string hpDriver = "HP Universal Printing PCL 6";
        var scenario = new ScenarioBuilder()
            .WithDriver(hpDriver)
            .WithNetworkQueue(HostName, name: "Office HP", driverName: hpDriver)
            .WithNetworkPrinter(Ip, modelId: null)
            .Build();

        var detection = Detect(scenario, Ip);

        Assert.DoesNotContain(HostName, scenario.Network.ProbedAddresses);
        Assert.DoesNotContain(detection.Findings, f => f.Code == FindingCode.DuplicateQueues);
    }

    [Fact]
    public void Requested_printer_without_its_model_next_to_a_queue_on_another_ip_address_is_no_possible_duplicate()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithNetworkQueue("10.12.0.99", name: "Old printer")
            .WithNetworkPrinter(Ip, modelId: null)
            .Build();

        Assert.DoesNotContain(Detect(scenario, Ip).Findings, f => f.Code == FindingCode.DuplicateQueues);
    }

    [Fact]
    public void Queue_on_another_ip_address_is_no_possible_duplicate()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithNetworkQueue("10.12.0.99", name: "Old printer")
            .WithNetworkPrinter(Ip, SampleConfig.P4030)
            .Build();

        Assert.DoesNotContain(Detect(scenario, Ip).Findings, f => f.Code == FindingCode.DuplicateQueues);
    }

    // ---- Planner: queues on every spelling count --------------------------------------------------------------------

    [Fact]
    public void Planner_uses_a_queue_on_any_spelling_of_the_target_instead_of_creating_one()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithNetworkQueue(HostName, name: "Ticket printer")
            .WithTicketForm()
            .WithNetworkPrinter(Ip, SampleConfig.P4030)
            .Build();
        var session = scenario.CreateSession();
        // Without probing the queue's host there is nothing to merge: the requested IP alone has no queue.
        var detected = session.Detect(new DetectRequest { NetworkAddresses = [Ip], ProbeExistingNetworkQueues = false }, default)
            .Candidates.Single(c => c.Key == PrinterCandidate.NetworkKey(Ip));

        var alone = session.Assess(detected, default);
        var withAlias = session.Assess(detected with { NetworkAliases = [HostName] }, default);

        Assert.Contains(CreateQueue, Kinds(alone));
        Assert.DoesNotContain(CreateTcpIpPort, Kinds(withAlias));
        Assert.DoesNotContain(CreateQueue, Kinds(withAlias));
        Assert.DoesNotContain(ChangeQueuePort, Kinds(withAlias));
        Assert.True(Has(withAlias.Findings, FindingCode.QueueReady, Severity.Info));
    }

    /// <summary>A WSD queue on the Microsoft PCL6 Class Driver, which names no model (the approved P-4030DN driver is installed too).</summary>
    private static ScenarioBuilder ClassDriverWsdQueue()
    {
        const string port = "WSD-5d6a1f3c-1111-2222-3333-444455556666";
        var builder = new ScenarioBuilder()
            .WithModelDriver()
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithQueue("Ticket printer (WSD)", SampleConfig.MicrosoftPcl6ClassDriver, port);
        builder.Spooler.SeedPort(new PrinterPortInfo { Name = port, Kind = PortKind.Wsd, MonitorName = "WSD Port" });
        return builder;
    }

    private static ScenarioBuilder WsdQueue(string modelId)
    {
        const string port = "WSD-5d6a1f3c-1111-2222-3333-444455556666";
        var builder = new ScenarioBuilder()
            .WithModelDriver(modelId)
            .WithQueue($"TA {modelId} (WSD)", SampleConfig.DriverName(modelId), port);
        builder.Spooler.SeedPort(new PrinterPortInfo { Name = port, Kind = PortKind.Wsd, MonitorName = "WSD Port" });
        return builder;
    }
}
