using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;
using static Amwag.PrinterSetup.Core.Setup.ActionKind;
using static Amwag.PrinterSetup.Core.Tests.TestHelpers;

namespace Amwag.PrinterSetup.Core.Tests;

/// <summary>
/// An existing Standard TCP/IP port without a queue that sends to the printer under another of its spellings (its IP address
/// or another of its names) is used for the new queue: the printer never gets a second port. Detected again, the queue is
/// keyed by that port's host, and the printer is still found under the address it was chosen by.
/// </summary>
public sealed class NetworkPortReuseTests
{
    private const string HostName = "printer12.branch.local";
    private const string Ip = "10.12.0.50";

    private static string[] Created(Scenario scenario) =>
        [.. scenario.Spooler.Writes
            .Where(w => w.Operation is nameof(IPrintSpooler.AddStandardTcpIpPort) or nameof(IPrintSpooler.AddQueue))
            .Select(w => $"{w.Operation} {w.Target} {w.Detail}")];

    /// <summary>The printer answers at <see cref="Ip"/>; its DNS name leads there too.</summary>
    private static ScenarioBuilder Printer() => new ScenarioBuilder()
        .WithModelDriver()
        .WithTicketForm()
        .WithNetworkPrinter(Ip, SampleConfig.P4030)
        .WithNetworkAlias(HostName, Ip);

    [Fact]
    public void Queueless_port_on_the_ip_address_is_used_for_a_printer_configured_by_its_dns_name()
    {
        var scenario = Printer().WithTcpIpPort(Ip).WithBranch(new BranchOverride { PrinterAddress = HostName }).Build();
        string queue = scenario.Config.Connection.NewQueueName;

        var run = scenario.Run();

        var target = run.Selection.Selected!;
        Assert.Equal(PrinterCandidate.NetworkKey(HostName), target.Key);
        Assert.Empty(target.NetworkAliases);
        Assert.DoesNotContain(CreateTcpIpPort, Kinds(run.Plan!));
        var create = run.Plan!.Actions.Single(a => a.Kind == CreateQueue);
        Assert.Equal("IP_" + Ip, create.Parameters["portName"]);
        Assert.Contains($"which already sends to {Ip} (the printer at {HostName})", create.Summary, StringComparison.Ordinal);

        Assert.True(run.Apply!.Succeeded);
        Assert.Equal([$"AddQueue {queue} driver={SampleConfig.DriverName(SampleConfig.P4030)}; port=IP_{Ip}"], Created(scenario));
        Assert.Single(scenario.Spooler.Ports, p => p.Kind == PortKind.StandardTcpIp);

        // Detected again, the queue is keyed by its port's host; the DNS name the printer was chosen by finds it.
        var verification = run.Verification!;
        Assert.Equal(ReadinessLevel.Ready, verification.Readiness);
        Assert.Equal(PrinterCandidate.NetworkKey(Ip), verification.Target!.Key);
        Assert.Equal([HostName], verification.Target.NetworkAliases);
        Assert.Equal(queue, verification.Summary.QueueName);
        Assert.True(scenario.Log.Contains("resolve.alias"));

        // The next run plans nothing for the port or the queue.
        var again = scenario.Run(SetupMode.Repair);
        Assert.Empty(again.Plan!.Actions);
        Assert.Single(Created(scenario));
    }

    [Fact]
    public void Printer_chosen_by_its_ip_address_gets_its_queue_on_a_port_to_that_address_not_on_a_configured_dns_names_port()
    {
        // A queue on the name's port would be keyed by the name in later runs and found as this printer only while the name
        // resolves: a run without DNS would add a second queue for the IP address.
        var scenario = Printer()
            .WithTcpIpPort(HostName)
            .WithOptions(o => o with { PreferredAddress = Ip })
            .WithBranch(new BranchOverride { PrinterAddress = HostName })
            .Build();

        var run = scenario.Run();

        Assert.Equal(PrinterCandidate.NetworkKey(Ip), run.Selection.Selected!.Key);
        Assert.Equal([HostName], run.Selection.Selected.NetworkAliases);
        Assert.Equal("IP_" + Ip, run.Plan!.Actions.Single(a => a.Kind == CreateTcpIpPort).Parameters["portName"]);
        Assert.Equal("IP_" + Ip, run.Plan.Actions.Single(a => a.Kind == CreateQueue).Parameters["portName"]);
        Assert.True(run.Apply!.Succeeded);
        Assert.Equal("IP_" + Ip, Assert.Single(scenario.Spooler.Queues, q => q.Name == scenario.Config.Connection.NewQueueName).PortName);
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
        Assert.Equal(PrinterCandidate.NetworkKey(Ip), run.Verification.Target!.Key);
    }

    [Fact]
    public void Printer_chosen_by_its_ip_address_gets_no_second_queue_in_a_later_run_without_dns()
    {
        var scenario = Printer()
            .WithTcpIpPort(HostName)
            .WithOptions(o => o with { PreferredAddress = Ip })
            .WithBranch(new BranchOverride { PrinterAddress = HostName })
            .Build();
        Assert.Equal(ReadinessLevel.Ready, scenario.Run().Verification!.Readiness);

        // A later silent run while the DNS name does not resolve.
        scenario.Network.SetAlias(HostName, "unresolvable.invalid");
        var later = scenario.Run();

        Assert.Equal(PrinterCandidate.NetworkKey(Ip), later.Selection.Selected!.Key);
        Assert.DoesNotContain(CreateQueue, Kinds(later.Plan!));
        Assert.Single(scenario.Spooler.Queues, q => q.PortName.StartsWith("IP_", StringComparison.Ordinal));
        Assert.Single(scenario.Spooler.Writes, w => w.Operation == nameof(IPrintSpooler.AddQueue));
        Assert.Equal(ReadinessLevel.Ready, later.Verification!.Readiness);
    }

    [Fact]
    public void Queueless_port_on_another_configured_name_is_used_for_a_printer_chosen_by_a_name()
    {
        // Both spellings are names, so the queue depends on DNS either way: the existing port is used, not a second one.
        const string shortName = "printer12";
        var scenario = Printer()
            .WithNetworkAlias(shortName, Ip)
            .WithTcpIpPort(shortName)
            .WithOptions(o => o with { PreferredAddress = HostName })
            .WithBranch(new BranchOverride { PrinterAddress = shortName })
            .Build();

        var run = scenario.Run();

        Assert.Equal(PrinterCandidate.NetworkKey(HostName), run.Selection.Selected!.Key);
        Assert.Equal([shortName], run.Selection.Selected.NetworkAliases);
        Assert.DoesNotContain(CreateTcpIpPort, Kinds(run.Plan!));
        Assert.Equal("IP_" + shortName, run.Plan!.Actions.Single(a => a.Kind == CreateQueue).Parameters["portName"]);
        Assert.True(run.Apply!.Succeeded);
        Assert.Single(Created(scenario));
        Assert.Single(scenario.Spooler.Ports, p => p.Kind == PortKind.StandardTcpIp);
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
    }

    [Fact]
    public void Port_to_another_name_that_a_later_detection_does_not_probe_is_not_used()
    {
        // The DNS name is known only from this detection's request, not from the configuration: the verification would not
        // probe it again, so a queue on its port would not be found as this printer. A port on the printer's address is added.
        var scenario = Printer().WithTcpIpPort(HostName).Build();

        var run = scenario.Run(request: new DetectRequest { NetworkAddresses = [Ip, HostName] });

        Assert.Equal(PrinterCandidate.NetworkKey(Ip), run.Selection.Selected!.Key);
        Assert.Equal([HostName], run.Selection.Selected.NetworkAliases);
        Assert.Equal("IP_" + Ip, run.Plan!.Actions.Single(a => a.Kind == CreateTcpIpPort).Parameters["portName"]);
        Assert.True(run.Apply!.Succeeded);
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
    }

    [Fact]
    public void Port_that_appears_before_the_queue_is_created_is_used_at_execution()
    {
        var scenario = Printer().WithBranch(new BranchOverride { PrinterAddress = HostName }).Build();
        var session = scenario.CreateSession();
        var target = session.SelectAutomatically(session.Detect(new DetectRequest(), default)).Selected!;
        var plan = session.Assess(target, default);
        Assert.Equal("IP_" + HostName, plan.Actions.Single(a => a.Kind == CreateTcpIpPort).Parameters["portName"]);
        // Another run (or IT) adds a port to the printer's IP address meanwhile.
        scenario.Spooler.SeedPort(new PrinterPortInfo { Name = "IP_" + Ip, Kind = PortKind.StandardTcpIp, HostAddress = Ip, PortNumber = 9100, Protocol = TcpPortProtocol.Raw });

        var apply = session.Apply(plan, ApplyScope.All, null, default);

        var port = Result(apply, CreateTcpIpPort);
        Assert.Equal(ActionOutcome.AlreadySatisfied, port.Outcome);
        Assert.Contains($"already sends to {HostName} as {Ip}:9100", port.Detail, StringComparison.Ordinal);
        Assert.Equal(ActionOutcome.Succeeded, Outcome(apply, CreateQueue));
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Operation == nameof(IPrintSpooler.AddStandardTcpIpPort));
        Assert.Equal("IP_" + Ip, Assert.Single(scenario.Spooler.Queues, q => q.Name == scenario.Config.Connection.NewQueueName).PortName);
        Assert.Equal(ReadinessLevel.Ready, session.Verify(TargetSelector.For(target), default).Readiness);
    }

    [Theory]
    [InlineData("2001:DB8:0:0:0:0:0:50", true)] // the resolved address, written another way
    [InlineData("10.12.0.50", false)]           // another address
    public void Port_on_the_resolved_ip_address_is_matched_by_address_not_by_spelling(string portHost, bool reused)
    {
        var target = new PrinterCandidate
        {
            Key = PrinterCandidate.NetworkKey(HostName),
            Kind = TargetKind.NetworkAddress,
            NetworkAddress = HostName,
            NetworkIdentity = new NetworkIdentity { Address = HostName, ResolvedAddress = "2001:db8::50", RawPortReachable = true },
        };
        PrinterPortInfo[] ports =
        [
            new() { Name = "IP_" + portHost, Kind = PortKind.StandardTcpIp, HostAddress = portHost, PortNumber = 9100, Protocol = TcpPortProtocol.Raw },
            new() { Name = "LPR", Kind = PortKind.StandardTcpIp, HostAddress = "2001:db8::50", PortNumber = 9100, Protocol = TcpPortProtocol.Lpr },
            new() { Name = "Other port number", Kind = PortKind.StandardTcpIp, HostAddress = "2001:db8::50", PortNumber = 9101, Protocol = TcpPortProtocol.Raw },
        ];

        var port = QueueLocator.ReusableTcpPort(ports, target, 9100, []);

        Assert.Equal(reused ? "IP_" + portHost : null, port?.Name);
    }
}
