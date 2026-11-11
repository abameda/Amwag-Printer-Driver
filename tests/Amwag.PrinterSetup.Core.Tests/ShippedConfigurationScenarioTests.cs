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

    [Fact]
    public void Shipped_configuration_is_valid()
    {
        var (errors, warnings) = ConfigLoader.Check(Shipped());

        Assert.Empty(errors);
        Assert.Empty(warnings);
    }

    /// <summary>An unbound USB device node reporting the model's own USBPRINT hardware ID and IEEE 1284 MFG+MDL from the shipped manifest.</summary>
    private static PnpPrinterDevice UsbDevice(ModelDefinition model)
    {
        const string Enumerator = @"USBPRINT\";
        string hardwareId = model.HardwareIds.First(id => id.StartsWith(Enumerator, StringComparison.OrdinalIgnoreCase));
        var ieee1284 = model.Ieee1284Ids[0];
        return SampleConfig.UsbDevice(SampleConfig.P4030, driverBound: false) with
        {
            InstanceId = $@"{hardwareId}\7&2B5A1C3&0&VCF5X01234",
            HardwareIds = [hardwareId, hardwareId[Enumerator.Length..]],
            CompatibleIds = [Enumerator + ieee1284.Manufacturer + ieee1284.Model, "GENPRINT"],
            FriendlyName = model.DisplayName,
            Description = model.DisplayName,
        };
    }

    [Theory]
    [InlineData(SampleConfig.P4030)]
    [InlineData(SampleConfig.P4531)]
    [InlineData(SampleConfig.P5032)]
    [InlineData("FS-2100DN")]
    [InlineData("FS-2100D")]
    public void Usb_printer_is_installed_and_configured_with_the_shipped_manifest(string modelId)
    {
        var shipped = Shipped();
        var builder = new ScenarioBuilder().WithConfig(_ => shipped).WithDevice(UsbDevice(shipped.FindModel(modelId)!));
        builder.Spooler.SeedUsbPort("USB001");
        var scenario = builder.Build();

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
    public void Kyocera_fs_2100d_and_fs_2100dn_are_told_apart_on_the_network()
    {
        var scenario = new ScenarioBuilder()
            .WithConfig(_ => Shipped())
            .WithNetworkIdentity(new NetworkIdentity
            {
                Address = "10.0.0.23",
                RawPortReachable = true,
                SnmpResponded = true,
                SysDescr = "KYOCERA Document Solutions Printing System",
                SysObjectId = "1.3.6.1.4.1.1347.41",
                DeviceDescription = "FS-2100DN",
                Ieee1284DeviceId = "MFG:Kyocera;CMD:PCLXL,PostScript Emulation,PCL5E,PJL;MDL:FS-2100DN;CLS:PRINTER;DES:Kyocera FS-2100DN;",
            })
            .WithNetworkIdentity(new NetworkIdentity
            {
                Address = "10.0.0.24",
                RawPortReachable = true,
                SnmpResponded = true,
                SysDescr = "KYOCERA Document Solutions Printing System",
                SysObjectId = "1.3.6.1.4.1.1347.41",
                VendorModelName = "FS-2100D",
            })
            .Build();

        var detection = scenario.CreateSession().Detect(new DetectRequest { NetworkAddresses = ["10.0.0.23", "10.0.0.24"] }, default);

        var dn = detection.Candidates.Single(c => c.NetworkAddress == "10.0.0.23");
        Assert.Equal(("FS-2100DN", MatchConfidence.Exact), (dn.Model?.Id, dn.Confidence));
        var d = detection.Candidates.Single(c => c.NetworkAddress == "10.0.0.24");
        Assert.Equal(("FS-2100D", MatchConfidence.Exact), (d.Model?.Id, d.Confidence));
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
