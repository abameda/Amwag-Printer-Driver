using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.Core.Tests;

/// <summary>
/// <see cref="SetupSession.Resolve"/> finds the chosen printer again in a later detection (the elevated worker's, the final
/// check's): under another of its spellings, with a model the operator or IT asserted, and never against what it reports.
/// </summary>
public sealed class ResolveTests
{
    private const string Address = "10.20.30.40";
    private const string HostName = "printer12.branch.local";
    private const string Ip = "10.12.0.50";

    [Fact]
    public void A_network_selector_finds_the_printer_keyed_by_another_of_its_spellings()
    {
        // The window keyed the printer by its IP address; once its queue's host name is probed too, the two spellings merge and
        // the printer is keyed by that host name.
        var scenario = new ScenarioBuilder()
            .WithModelDriver()
            .WithNetworkQueue(HostName, name: "Ticket printer")
            .WithNetworkPrinter(Ip, SampleConfig.P4030)
            .WithNetworkAlias(HostName, Ip)
            .Build();
        var session = scenario.CreateSession();
        var detection = session.Detect(new DetectRequest { NetworkAddresses = [Ip] }, default);

        var resolved = session.Resolve(detection, new TargetSelector(PrinterCandidate.NetworkKey(Ip), SampleConfig.P4030));

        Assert.Equal(PrinterCandidate.NetworkKey(HostName), resolved?.Key);
        Assert.Equal("Ticket printer", resolved?.Queue?.Name);
    }

    [Fact]
    public void An_asserted_model_replaces_the_model_of_a_queue_driver_when_the_printer_does_not_report_one()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithNetworkQueue(Address, SampleConfig.P4030, name: "Tickets")
            .WithNetworkPrinter(Address, modelId: null, snmp: false)
            .Build();
        var session = scenario.CreateSession();
        var detection = session.Detect(new DetectRequest { NetworkAddresses = [Address] }, default);
        var key = PrinterCandidate.NetworkKey(Address);
        Assert.Equal(MatchConfidence.DriverOnly, detection.Candidates.Single(c => c.Key == key).Confidence);

        var asserted = session.Resolve(detection, new TargetSelector(key, SampleConfig.P4531, ModelAssertedByUser: true));
        var notAsserted = session.Resolve(detection, new TargetSelector(key, SampleConfig.P4531));

        Assert.Equal((SampleConfig.P4531, MatchConfidence.UserAsserted, true), (asserted?.Model?.Id, asserted?.Confidence, asserted?.IdentityConflict));
        Assert.Null(notAsserted);
    }

    [Fact]
    public void An_assertion_over_a_queue_driver_is_refused_when_the_printer_names_the_drivers_model()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4030)
            .WithNetworkQueue(Address, SampleConfig.P4030, name: "Tickets")
            .WithNetworkIdentity(new NetworkIdentity { Address = Address, ResolvedAddress = Address, RawPortReachable = true, SnmpResponded = true, SysDescr = "TA Triumph-Adler P-4030DN" })
            .Build();
        var session = scenario.CreateSession();
        var detection = session.Detect(new DetectRequest { NetworkAddresses = [Address] }, default);

        Assert.Null(session.Resolve(detection, new TargetSelector(PrinterCandidate.NetworkKey(Address), SampleConfig.P4531, ModelAssertedByUser: true)));
    }

    [Fact]
    public void An_assertion_is_checked_against_every_spelling_of_the_printer()
    {
        // The IP address's SNMP request times out; the host name's probe of the same printer reports another model.
        var scenario = new ScenarioBuilder()
            .WithNetworkIdentity(new NetworkIdentity { Address = "10.1.2.50", ResolvedAddress = "10.1.2.50", RawPortReachable = true, SnmpResponded = false, Error = "SNMP: no response" })
            .WithNetworkIdentity(new NetworkIdentity
            {
                Address = "tkt.branch.local",
                ResolvedAddress = "10.1.2.50",
                RawPortReachable = true,
                SnmpResponded = true,
                SysDescr = "TA_UTAX Printing System",
                SysObjectId = "1.3.6.1.4.1.1347.41",
                VendorModelName = "P-4531DN KX",
            })
            .Build();
        var session = scenario.CreateSession();
        var detection = session.Detect(new DetectRequest { NetworkAddresses = ["10.1.2.50", "tkt.branch.local"] }, default);
        var candidate = Assert.Single(detection.Candidates);

        Assert.Null(session.Resolve(detection, new TargetSelector(candidate.Key, SampleConfig.P4030, ModelAssertedByUser: true)));
    }
}
