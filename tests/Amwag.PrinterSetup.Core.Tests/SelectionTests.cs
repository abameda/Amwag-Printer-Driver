using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.Core.Tests;

/// <summary>Target selection for Setup (<see cref="SetupSession.SelectAutomatically"/>) and Repair (<see cref="SetupSession.SelectRepairTarget"/>).</summary>
public sealed class SelectionTests
{
    private const string Address = "10.20.30.40";
    private static readonly string Queue4030 = SampleConfig.DriverName(SampleConfig.P4030);
    private static readonly string Usb4030 = PrinterCandidate.UsbKey(SampleConfig.UsbInstanceId(SampleConfig.P4030));
    private static readonly string OldUnit4030 = SampleConfig.UsbInstanceId(SampleConfig.P4030, "OLDSERIAL01");

    private static CandidateSelection Select(Scenario scenario, params string[] addresses)
    {
        var session = scenario.CreateSession();
        return session.SelectAutomatically(session.Detect(new DetectRequest { NetworkAddresses = addresses }, default));
    }

    private static CandidateSelection SelectForRepair(Scenario scenario, TargetSelector? explicitTarget = null)
    {
        var session = scenario.CreateSession(SetupMode.Repair);
        return session.SelectRepairTarget(session.Detect(new DetectRequest(), default), explicitTarget);
    }

    private static string[] Keys(IEnumerable<PrinterCandidate> candidates) => candidates.Select(c => c.Key).ToArray();

    private static NetworkIdentity ProbableIdentity(string address) =>
        new() { Address = address, RawPortReachable = true, SnmpResponded = true, SysDescr = "TA P-4030DN" };

    // ---- The printer at the requested address comes first ------------------------------------------------------------

    [Fact]
    public void Unidentified_printer_at_the_branch_address_is_asked_about_instead_of_a_stale_queue_of_the_branch_model()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithNetworkQueue("10.1.2.10", name: "Old ticket printer")
            .WithNetworkPrinter("10.1.2.80", modelId: null)
            .WithBranch(new BranchOverride { PrinterAddress = "10.1.2.80", ModelId = SampleConfig.P4030 })
            .Build();

        var selection = Select(scenario);

        Assert.Equal(SelectionReason.NeedsConfirmation, selection.Reason);
        Assert.Null(selection.Selected);
        Assert.Equal([PrinterCandidate.NetworkKey("10.1.2.80")], Keys(selection.Choices));
    }

    [Fact]
    public void Unidentified_printer_at_the_branch_address_is_asked_about_instead_of_the_usb_printer()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(correctDefaults: false)
            .WithNetworkPrinter(Address, modelId: null)
            .WithBranch(new BranchOverride { PrinterAddress = Address })
            .Build();

        var selection = Select(scenario);

        Assert.Equal(SelectionReason.NeedsConfirmation, selection.Reason);
        Assert.Equal([PrinterCandidate.NetworkKey(Address)], Keys(selection.Choices));
    }

    [Fact]
    public void Probable_printer_at_the_branch_address_is_asked_about_instead_of_another_printer_of_the_branch_model()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030)
            .WithNetworkIdentity(ProbableIdentity(Address))
            .WithBranch(new BranchOverride { PrinterAddress = Address, ModelId = SampleConfig.P4030 })
            .Build();

        var selection = Select(scenario);

        Assert.Equal(SelectionReason.NeedsConfirmation, selection.Reason);
        var probable = Assert.Single(selection.Choices);
        Assert.Equal(PrinterCandidate.NetworkKey(Address), probable.Key);
        Assert.True(probable.NeedsConfirmation);
    }

    [Fact]
    public void Command_line_address_names_the_printer_even_when_the_branch_address_has_an_eligible_one()
    {
        var scenario = new ScenarioBuilder()
            .WithNetworkPrinter("10.0.0.5", SampleConfig.P4030)
            .WithNetworkPrinter(Address, modelId: null)
            .WithBranch(new BranchOverride { PrinterAddress = "10.0.0.5" })
            .Build();
        var session = scenario.CreateSession(o => o with { PreferredAddress = Address });

        var selection = session.SelectAutomatically(session.Detect(new DetectRequest(), default));

        Assert.Equal(SelectionReason.NeedsConfirmation, selection.Reason);
        Assert.Equal([PrinterCandidate.NetworkKey(Address)], Keys(selection.Choices));
    }

    [Fact]
    public void Eligible_printer_at_the_branch_address_is_preferred_over_another_of_the_branch_model()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030)
            .WithNetworkPrinter(Address, SampleConfig.P5032)
            .WithBranch(new BranchOverride { PrinterAddress = Address, ModelId = SampleConfig.P4030 })
            .Build();

        var selection = Select(scenario);

        Assert.Equal(SelectionReason.PreferredByConfiguration, selection.Reason);
        Assert.Equal(PrinterCandidate.NetworkKey(Address), selection.Selected!.Key);
    }

    [Fact]
    public void Nothing_is_chosen_in_place_of_a_branch_address_where_nothing_answers()
    {
        // The printer IT named may only be switched off: a printer of the branch model elsewhere is not that printer.
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001")
            .WithInstalledUsbPrinter(SampleConfig.P5032, "USB002")
            .WithBranch(new BranchOverride { PrinterAddress = "10.9.9.9", ModelId = SampleConfig.P5032 })
            .Build();

        var selection = Select(scenario);

        Assert.Equal(SelectionReason.NoCandidates, selection.Reason);
        Assert.Null(selection.Selected);
        Assert.Equal("10.9.9.9", selection.Address);
    }

    [Fact]
    public void A_stale_queue_of_the_branch_model_at_another_address_is_not_chosen_while_the_branch_printer_is_off()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithNetworkQueue("10.1.2.10", name: "Old ticket printer")
            .WithNetworkPrinter("10.1.2.80", SampleConfig.P4030, reachable: false, snmp: false)
            .WithBranch(new BranchOverride { PrinterAddress = "10.1.2.80", ModelId = SampleConfig.P4030 })
            .Build();

        var selection = Select(scenario);

        Assert.Equal(SelectionReason.NoCandidates, selection.Reason);
        Assert.Empty(selection.Choices);
    }

    [Theory]
    [InlineData("MFG:Hewlett-Packard;MDL:HP LaserJet M404;CMD:PCL;")]
    [InlineData("MFG:UTAX_TA;MDL:P-4532DN;CMD:PCL;")]
    public void A_device_at_the_branch_address_that_is_not_a_supported_printer_lets_no_other_printer_be_chosen(string deviceId)
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, correctDefaults: false)
            .WithNetworkIdentity(new NetworkIdentity { Address = Address, RawPortReachable = true, SnmpResponded = true, Ieee1284DeviceId = deviceId })
            .WithBranch(new BranchOverride { PrinterAddress = Address })
            .Build();

        Assert.Equal(SelectionReason.NoCandidates, Select(scenario).Reason);
        Assert.Equal(SelectionReason.NoCandidates, SelectForRepair(scenario).Reason);
    }

    [Fact]
    public void The_address_an_operator_entered_replaces_the_branch_address()
    {
        var scenario = new ScenarioBuilder()
            .WithNetworkPrinter("10.0.0.5", SampleConfig.P4030)
            .WithNetworkPrinter(Address, SampleConfig.P4531)
            .WithBranch(new BranchOverride { PrinterAddress = "10.0.0.5" })
            .Build();
        var session = scenario.CreateSession();
        var detection = session.Detect(new DetectRequest { NetworkAddresses = [Address] }, default);

        var selection = session.SelectAutomatically(detection, operatorAddress: Address);

        Assert.Equal(SelectionReason.PreferredByConfiguration, selection.Reason);
        Assert.Equal(PrinterCandidate.NetworkKey(Address), selection.Selected!.Key);
        Assert.Equal(Address, selection.Address);
    }

    [Fact]
    public void A_single_printer_of_another_model_than_the_configured_one_is_a_choice()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter(SampleConfig.P4531).Build();
        var session = scenario.CreateSession(o => o with { PreferredModelId = SampleConfig.P4030 });

        var selection = session.SelectAutomatically(session.Detect(new DetectRequest(), default));

        Assert.Equal(SelectionReason.MultipleCandidates, selection.Reason);
        Assert.Equal(SampleConfig.P4531, Assert.Single(selection.Choices).Model!.Id);
    }

    [Fact]
    public void Unidentified_printer_at_an_entered_address_makes_a_single_eligible_printer_a_choice()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, driverBound: false)
            .WithNetworkPrinter("10.0.0.7", modelId: null)
            .Build();

        var selection = Select(scenario, "10.0.0.7");

        Assert.Equal(SelectionReason.MultipleCandidates, selection.Reason);
        Assert.Equal([Usb4030, PrinterCandidate.NetworkKey("10.0.0.7")], Keys(selection.Choices));
    }

    [Fact]
    public void Probable_printers_are_offered_next_to_several_eligible_ones()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001")
            .WithInstalledUsbPrinter(SampleConfig.P5032, "USB002")
            .WithNetworkIdentity(ProbableIdentity(Address))
            .Build();

        var selection = Select(scenario, Address);

        Assert.Equal(SelectionReason.MultipleCandidates, selection.Reason);
        Assert.Equal(3, selection.Choices.Count);
        Assert.Contains(selection.Choices, c => c.NeedsConfirmation);
    }

    // ---- USB printers that are not connected ---------------------------------------------------------------------------

    [Fact]
    public void Unplugged_unit_of_the_connected_model_is_left_out_and_the_connected_printer_is_selected()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001")
            .WithUsbPrinter(SampleConfig.P4030, "USB002", present: false, instanceId: OldUnit4030)
            .Build();

        var selection = Select(scenario);

        Assert.Equal(SelectionReason.SingleCandidate, selection.Reason);
        Assert.Equal(Usb4030, selection.Selected!.Key);
        Assert.Equal([PrinterCandidate.UsbKey(OldUnit4030)], Keys(selection.Superseded));
        Assert.DoesNotContain(selection.Choices, c => c.Key == PrinterCandidate.UsbKey(OldUnit4030));
    }

    [Fact]
    public void Replaced_unit_whose_old_queue_was_set_up_is_left_out_for_the_connected_one()
    {
        const string newQueue = "TA P-4030DN KX (Copy 1)";
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, "USB001")
            .WithModelDriver()
            .WithUsbQueue(SampleConfig.P4030, "USB001", name: newQueue)
            .WithUsbPrinter(SampleConfig.P4030, "USB002", present: false, instanceId: OldUnit4030)
            .WithUsbQueue(SampleConfig.P4030, "USB002")
            .WithTicketForm()
            .WithCorrectDefaults(Queue4030)
            .Build();

        var selection = Select(scenario);

        Assert.Equal(SelectionReason.SingleCandidate, selection.Reason);
        Assert.Equal(newQueue, selection.Selected!.Queue!.Name);
        Assert.Equal(Queue4030, Assert.Single(selection.Superseded).Queue!.Name);
    }

    [Fact]
    public void Branch_model_selects_the_connected_printer_next_to_an_unplugged_unit_of_it()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001")
            .WithUsbPrinter(SampleConfig.P4030, "USB002", present: false, instanceId: OldUnit4030)
            .WithBranch(new BranchOverride { ModelId = SampleConfig.P4030 })
            .Build();

        var selection = Select(scenario);

        Assert.Equal(SelectionReason.PreferredByConfiguration, selection.Reason);
        Assert.Equal(Usb4030, selection.Selected!.Key);
    }

    [Fact]
    public void Lone_unplugged_printer_is_still_selected()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(present: false).WithModelDriver().WithUsbQueue().Build();

        var selection = Select(scenario);

        Assert.Equal(SelectionReason.SingleCandidate, selection.Reason);
        Assert.False(selection.Selected!.PhysicallyAvailable);
        Assert.Empty(selection.Superseded);
    }

    [Fact]
    public void Two_connected_printers_of_one_model_are_still_a_choice_without_the_unplugged_unit()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001")
            .WithUsbPrinter(SampleConfig.P4030, "USB002", instanceId: SampleConfig.UsbInstanceId(SampleConfig.P4030, "VCF5X05678"))
            .WithUsbPrinter(SampleConfig.P4030, "USB003", present: false, instanceId: OldUnit4030)
            .Build();

        var selection = Select(scenario);

        Assert.Equal(SelectionReason.MultipleCandidates, selection.Reason);
        Assert.Equal(2, selection.Choices.Count);
        Assert.All(selection.Choices, c => Assert.True(c.PhysicallyAvailable));
        Assert.Single(selection.Superseded);
    }

    [Fact]
    public void Switched_off_usb_printer_is_not_replaced_by_a_network_printer_that_is_on()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, present: false)
            .WithModelDriver(SampleConfig.P4030)
            .WithUsbQueue(SampleConfig.P4030)
            .WithModelDriver(SampleConfig.P5032)
            .WithNetworkQueue(Address, SampleConfig.P5032, name: "Office")
            .WithNetworkPrinter(Address, SampleConfig.P5032)
            .Build();

        var selection = Select(scenario);

        Assert.Equal(SelectionReason.MultipleCandidates, selection.Reason);
        Assert.Equal([PrinterCandidate.NetworkKey(Address), Usb4030], Keys(selection.Choices));
        Assert.Empty(selection.Superseded);
    }

    [Fact]
    public void Unplugged_printer_of_another_model_stays_a_choice_after_the_connected_one()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, "USB001", present: false)
            .WithModelDriver(SampleConfig.P4030)
            .WithUsbQueue(SampleConfig.P4030, "USB001")
            .WithInstalledUsbPrinter(SampleConfig.P4531, "USB002")
            .Build();

        var selection = Select(scenario);

        Assert.Equal(SelectionReason.MultipleCandidates, selection.Reason);
        Assert.Equal([SampleConfig.P4531, SampleConfig.P4030], selection.Choices.Select(c => c.Model!.Id));
        Assert.Empty(selection.Superseded);
    }

    // ---- Repair ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Repair_targets_a_lone_queue()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter(correctDefaults: false).Build();

        var selection = SelectForRepair(scenario);

        Assert.Equal(SelectionReason.SingleCandidate, selection.Reason);
        Assert.Equal(Queue4030, selection.Selected!.Queue!.Name);
    }

    [Fact]
    public void Repair_needs_a_choice_between_queues_nobody_set_up_and_writes_nothing()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001", correctDefaults: false)
            .WithInstalledUsbPrinter(SampleConfig.P4531, "USB002", correctDefaults: false)
            .Build();

        var selection = SelectForRepair(scenario);

        Assert.Equal(SelectionReason.MultipleCandidates, selection.Reason);
        Assert.Null(selection.Selected);
        Assert.Equal(2, selection.Choices.Count);
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Fact]
    public void Repair_targets_the_only_queue_already_using_the_ticket_form()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001")
            .WithInstalledUsbPrinter(SampleConfig.P4531, "USB002", correctDefaults: false)
            .Build();

        var selection = SelectForRepair(scenario);

        Assert.Equal(SelectionReason.SingleCandidate, selection.Reason);
        Assert.Equal(Queue4030, selection.Selected!.Queue!.Name);
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Fact]
    public void Repair_needs_a_choice_when_several_queues_use_the_ticket_form()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001")
            .WithInstalledUsbPrinter(SampleConfig.P4531, "USB002")
            .Build();

        Assert.Equal(SelectionReason.MultipleCandidates, SelectForRepair(scenario).Reason);
    }

    [Fact]
    public void Repair_follows_the_branch_model()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001", correctDefaults: false)
            .WithInstalledUsbPrinter(SampleConfig.P4531, "USB002", correctDefaults: false)
            .WithBranch(new BranchOverride { ModelId = SampleConfig.P4531 })
            .Build();

        var selection = SelectForRepair(scenario);

        Assert.Equal(SelectionReason.PreferredByConfiguration, selection.Reason);
        Assert.Equal(SampleConfig.P4531, selection.Selected!.Model!.Id);
    }

    [Fact]
    public void Repair_targets_the_queue_of_the_printer_at_the_branch_address()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, correctDefaults: false)
            .WithModelDriver(SampleConfig.P4531)
            .WithNetworkQueue(Address, SampleConfig.P4531)
            .WithNetworkPrinter(Address, SampleConfig.P4531)
            .WithBranch(new BranchOverride { PrinterAddress = Address })
            .Build();

        var selection = SelectForRepair(scenario);

        Assert.Equal(SelectionReason.PreferredByConfiguration, selection.Reason);
        Assert.Equal(PrinterCandidate.NetworkKey(Address), selection.Selected!.Key);
    }

    [Fact]
    public void Repair_never_changes_another_printer_when_the_one_at_the_branch_address_has_no_queue()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, correctDefaults: false)
            .WithNetworkPrinter(Address, SampleConfig.P4030)
            .WithBranch(new BranchOverride { PrinterAddress = Address, ModelId = SampleConfig.P4030 })
            .Build();

        var selection = SelectForRepair(scenario);

        Assert.Equal(SelectionReason.NoCandidates, selection.Reason);
        Assert.Null(selection.Selected);
        Assert.Empty(selection.Choices);
        Assert.Equal(PrinterCandidate.NetworkKey(Address), selection.NeedsSetup?.Key);
    }

    [Fact]
    public void Repair_asks_about_an_unidentified_printer_at_the_branch_address_instead_of_changing_another_printer()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, correctDefaults: false)
            .WithNetworkPrinter(Address, modelId: null)
            .WithBranch(new BranchOverride { PrinterAddress = Address, ModelId = SampleConfig.P4030 })
            .Build();

        var selection = SelectForRepair(scenario);

        Assert.Equal(SelectionReason.NeedsConfirmation, selection.Reason);
        Assert.Equal([PrinterCandidate.NetworkKey(Address)], Keys(selection.Choices));
    }

    [Fact]
    public void Repair_does_not_rewrite_an_office_queue_when_the_ticket_printer_has_no_queue_yet()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, driverBound: false)
            .WithModelDriver(SampleConfig.P5032)
            .WithNetworkQueue(Address, SampleConfig.P5032, name: "Office A4")
            .WithNetworkPrinter(Address, SampleConfig.P5032)
            .Build();
        var byModel = scenario.CreateSession(o => o with { Mode = SetupMode.Repair, PreferredModelId = SampleConfig.P4030 });

        var withModel = byModel.SelectRepairTarget(byModel.Detect(new DetectRequest(), default), null);
        var withoutPreference = SelectForRepair(scenario);

        Assert.Equal(SelectionReason.NoCandidates, withModel.Reason);
        Assert.Equal(Usb4030, withModel.NeedsSetup?.Key);
        // Without a preference nobody knows which is the ticket printer: the office queue is only offered, never chosen.
        Assert.Equal(SelectionReason.MultipleCandidates, withoutPreference.Reason);
        Assert.Null(withoutPreference.Selected);
    }

    [Fact]
    public void Repair_explicit_target_decides()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001")
            .WithInstalledUsbPrinter(SampleConfig.P4531, "USB002", correctDefaults: false)
            .WithBranch(new BranchOverride { ModelId = SampleConfig.P4030 })
            .Build();
        var office = PrinterCandidate.UsbKey(SampleConfig.UsbInstanceId(SampleConfig.P4531));

        var selection = SelectForRepair(scenario, new TargetSelector(office, SampleConfig.P4531));

        Assert.Equal(SelectionReason.PreferredByConfiguration, selection.Reason);
        Assert.Equal(office, selection.Selected!.Key);
    }

    [Fact]
    public void Repair_explicit_target_that_is_gone_or_has_no_queue_is_no_candidate()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001")
            .WithUsbPrinter(SampleConfig.P4531, "USB002", driverBound: false)
            .Build();
        var withoutQueue = PrinterCandidate.UsbKey(SampleConfig.UsbInstanceId(SampleConfig.P4531));

        Assert.Equal(SelectionReason.NoCandidates, SelectForRepair(scenario, new TargetSelector("usb:missing", SampleConfig.P4030)).Reason);
        Assert.Equal(SelectionReason.NoCandidates, SelectForRepair(scenario, new TargetSelector(withoutQueue, SampleConfig.P4531)).Reason);
    }

    [Fact]
    public void Repair_leaves_out_an_unplugged_unit_of_the_connected_model()
    {
        const string oldQueue = "Old ticket queue";
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001", correctDefaults: false)
            .WithUsbPrinter(SampleConfig.P4030, "USB002", present: false, instanceId: OldUnit4030)
            .WithUsbQueue(SampleConfig.P4030, "USB002", name: oldQueue)
            .Build();

        var selection = SelectForRepair(scenario);

        Assert.Equal(SelectionReason.SingleCandidate, selection.Reason);
        Assert.Equal(Usb4030, selection.Selected!.Key);
        Assert.Equal(oldQueue, Assert.Single(selection.Superseded).Queue!.Name);
    }

    [Fact]
    public void Repair_asks_when_only_an_unplugged_unit_of_the_connected_model_uses_the_ticket_form()
    {
        // A replaced unit or a switched-off ticket printer next to an identical office printer: only the operator can tell.
        const string oldQueue = "Old ticket queue";
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(SampleConfig.P4030, "USB001", correctDefaults: false)
            .WithUsbPrinter(SampleConfig.P4030, "USB002", present: false, instanceId: OldUnit4030)
            .WithUsbQueue(SampleConfig.P4030, "USB002", name: oldQueue)
            .WithCorrectDefaults(oldQueue)
            .Build();

        var selection = SelectForRepair(scenario);

        Assert.Equal(SelectionReason.MultipleCandidates, selection.Reason);
        Assert.Equal([Usb4030, PrinterCandidate.UsbKey(OldUnit4030)], Keys(selection.Choices));
        Assert.Empty(selection.Superseded);
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Fact]
    public void Repair_keeps_the_switched_off_ticket_printer_next_to_an_office_printer_that_is_on()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, present: false)
            .WithModelDriver(SampleConfig.P4030)
            .WithUsbQueue(SampleConfig.P4030)
            .WithTicketForm()
            .WithCorrectDefaults(Queue4030)
            .WithModelDriver(SampleConfig.P5032)
            .WithNetworkQueue(Address, SampleConfig.P5032, name: "Office")
            .WithNetworkPrinter(Address, SampleConfig.P5032)
            .Build();

        var selection = SelectForRepair(scenario);

        Assert.Equal(SelectionReason.SingleCandidate, selection.Reason);
        Assert.Equal(Queue4030, selection.Selected!.Queue!.Name);
    }

    // ---- Queue hosts of one printer whose model must be decided ------------------------------------------------------------

    /// <summary>
    /// One printer at 10.12.0.50 reached as printer12 ('A4 office', TA driver) and printer12.branch.local ('Tickets', class
    /// driver, on the ticket form); it reports a descriptive identity only. The branch names 10.12.0.50.
    /// </summary>
    private static Scenario MixedQueueHostsAtTheBranchAddress() => new ScenarioBuilder()
        .WithModelDriver(SampleConfig.P4030)
        .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
        .WithNetworkQueue("printer12", SampleConfig.P4030, name: "A4 office")
        .WithNetworkQueue("printer12.branch.local", name: "Tickets", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
        .WithTicketForm()
        .WithCorrectDefaults("Tickets")
        .WithNetworkIdentity(ProbableIdentity("10.12.0.50"))
        .WithNetworkAlias("printer12", "10.12.0.50")
        .WithNetworkAlias("printer12.branch.local", "10.12.0.50")
        .WithBranch(new BranchOverride { PrinterAddress = "10.12.0.50", ModelId = SampleConfig.P4030 })
        .Build();

    [Fact]
    public void A_queue_host_whose_model_must_be_confirmed_next_to_an_eligible_one_at_the_address_is_a_choice()
    {
        var selection = Select(MixedQueueHostsAtTheBranchAddress());

        // The configured model cannot tell apart the queue host whose model is only probably the same.
        Assert.Equal(SelectionReason.MultipleCandidates, selection.Reason);
        Assert.Equal(["A4 office", "Tickets"], selection.Choices.Select(c => c.Queue!.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Repair_at_the_address_asks_about_the_queue_host_on_the_ticket_form_not_the_other()
    {
        var selection = SelectForRepair(MixedQueueHostsAtTheBranchAddress());

        Assert.Equal(SelectionReason.NeedsConfirmation, selection.Reason);
        Assert.Equal(PrinterCandidate.NetworkKey("printer12.branch.local"), Assert.Single(selection.Choices).Key);
    }

    [Fact]
    public void Repair_of_several_printers_whose_model_must_be_confirmed_asks_about_the_one_on_the_ticket_form()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("10.0.0.40", name: "A4 office", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue("10.0.0.50", name: "Tickets", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithTicketForm()
            .WithCorrectDefaults("Tickets")
            .WithNetworkIdentity(ProbableIdentity("10.0.0.40"))
            .WithNetworkIdentity(ProbableIdentity("10.0.0.50"))
            .Build();

        var selection = SelectForRepair(scenario);

        Assert.Equal(SelectionReason.NeedsConfirmation, selection.Reason);
        Assert.Equal(PrinterCandidate.NetworkKey("10.0.0.50"), Assert.Single(selection.Choices).Key);
    }

    [Fact]
    public void Repair_counts_one_queue_pooled_over_two_connected_units_once()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, "USB001")
            .WithUsbPrinter(SampleConfig.P4030, "USB002", instanceId: SampleConfig.UsbInstanceId(SampleConfig.P4030, "SECONDUNIT02"))
            .WithModelDriver(SampleConfig.P4030)
            .WithQueue("Tickets", Queue4030, "USB001,USB002")
            .WithTicketForm()
            .WithCorrectDefaults("Tickets")
            .Build();

        var selection = SelectForRepair(scenario);

        Assert.Equal(SelectionReason.SingleCandidate, selection.Reason);
        Assert.Equal("Tickets", selection.Selected!.Queue!.Name);
    }
}
