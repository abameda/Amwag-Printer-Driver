using System.Text.Json;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.Core.Tests;

/// <summary>The engine against the shipped manifest (optionally with the ticket dimensions unconfirmed for the test).</summary>
public sealed class ShippedConfigurationScenarioTests
{
    private static DeploymentConfig Shipped(bool confirmed = true)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", ConfigLoader.ConfigFileName);
        var config = JsonSerializer.Deserialize<DeploymentConfig>(File.ReadAllText(path), ConfigJson.Options)!;
        return confirmed
            ? config
            : config with { Ticket = config.Ticket with { DimensionsConfirmed = false, ConfirmedBy = null, ConfirmedOn = null } };
    }

    [Fact]
    public void Shipped_configuration_has_the_confirmed_ticket_size()
    {
        Assert.True(Shipped().Ticket.DimensionsConfirmed);
    }

    [Theory]
    [InlineData(SampleConfig.P4030)]
    [InlineData(SampleConfig.P4531)]
    [InlineData(SampleConfig.P5032)]
    public void Usb_printer_is_installed_and_configured_with_the_shipped_manifest(string modelId)
    {
        var shipped = Shipped();
        var scenario = new ScenarioBuilder().WithConfig(_ => shipped).WithUsbPrinter(modelId, driverBound: false).Build();

        var run = scenario.Run();

        Assert.Equal(modelId, run.Selection.Selected!.Model!.Id);
        Assert.True(run.Apply!.Succeeded, string.Join("; ", run.Apply.Results.Select(r => $"{r.Action.Kind}: {r.Outcome} {r.Detail}")));
        var verification = run.Verification!;
        Assert.Equal(ReadinessLevel.Ready, verification.Readiness);
        Assert.Equal(shipped.FindModel(modelId)!.PreferredDriverName, verification.Summary.DriverName);
        Assert.Equal("AMWAG Ticket, 83.058 mm x 170.18 mm (3.27 in x 6.70 in)", verification.Summary.PaperSize);
    }

    [Fact]
    public void Network_identities_reported_by_kyocera_family_firmware_are_exact()
    {
        var scenario = new ScenarioBuilder()
            .WithConfig(_ => Shipped())
            .WithNetworkIdentity(new NetworkIdentity
            {
                Address = "10.0.0.21",
                RawPortReachable = true,
                SnmpResponded = true,
                SysDescr = "TA_UTAX Printing System",
                DeviceDescription = "P-5032DN",
                Ieee1284DeviceId = "ID:P-5032DN;MFG:TA_UTAX;CMD:PCLXL,PostScript Emulation,PCL5E,PJL;MDL:P-5032DN;CLS:PRINTER;DES:TA_UTAX P-5032DN;SER:VCF0000001;",
            })
            .WithNetworkIdentity(new NetworkIdentity
            {
                Address = "10.0.0.22",
                RawPortReachable = true,
                SnmpResponded = true,
                SysDescr = "KYOCERA Document Solutions Printing System",
                SysObjectId = "1.3.6.1.4.1.1347.41", // Kyocera enterprise: hrDeviceDescr then carries the bare model name.
                DeviceDescription = "P-4030DN",
            })
            .Build();

        var detection = scenario.CreateSession().Detect(new DetectRequest { NetworkAddresses = ["10.0.0.21", "10.0.0.22"] }, default);

        var p5032 = detection.Candidates.Single(c => c.NetworkAddress == "10.0.0.21");
        Assert.Equal((SampleConfig.P5032, MatchConfidence.Exact), (p5032.Model?.Id, p5032.Confidence));
        var p4030 = detection.Candidates.Single(c => c.NetworkAddress == "10.0.0.22");
        Assert.Equal((SampleConfig.P4030, MatchConfidence.Exact), (p4030.Model?.Id, p4030.Confidence));
    }

    [Fact]
    public void Shipped_configuration_blocks_setup_until_the_dimensions_are_confirmed()
    {
        var scenario = new ScenarioBuilder().WithConfig(_ => Shipped(confirmed: false)).WithUsbPrinter(SampleConfig.P4531, driverBound: false).Build();

        var run = scenario.Run();

        Assert.Contains(run.Plan!.Blockers, b => b.Code == FindingCode.DimensionsUnconfirmed);
        Assert.All(run.Apply!.Results, r => Assert.Equal(ActionOutcome.Skipped, r.Outcome));
        Assert.Empty(scenario.Spooler.Writes);
    }
}
