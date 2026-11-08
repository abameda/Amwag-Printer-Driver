using System.Text.Json;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;
using static Amwag.PrinterSetup.Core.Tests.TestHelpers;

namespace Amwag.PrinterSetup.Core.Tests;

/// <summary>
/// What a network device's own identity proves: a readable identity that names another model, several models or another
/// vendor is never configured as the model its queue's driver names, and the administrator-assigned printer name is never
/// model evidence. Only a device that names no model (or the driver's model) falls back to the driver (DriverOnly).
/// </summary>
public sealed class NetworkIdentityEvidenceTests
{
    private const string Address = "10.1.2.50";

    /// <summary>The LibreNMS capture of a TA P-4532DN (an unsupported successor model), which reports no IEEE 1284 ID.</summary>
    private static NetworkIdentity P4532(string address = Address) => new()
    {
        Address = address,
        RawPortReachable = true,
        SnmpResponded = true,
        SysDescr = "TA_UTAX Printing System",
        SysObjectId = "1.3.6.1.4.1.1347.41",
        DeviceDescription = "P-4532DN",
        VendorModelName = "P-4532DN",
        SerialNumber = "R9L0309954",
    };

    private static DetectionResult Detect(Scenario scenario, SetupMode mode = SetupMode.Setup, params string[] addresses) =>
        scenario.CreateSession(mode).Detect(new DetectRequest { NetworkAddresses = addresses }, default);

    private static DeploymentConfig Shipped()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "TestData", ConfigLoader.ConfigFileName);
        var config = JsonSerializer.Deserialize<DeploymentConfig>(File.ReadAllText(path), ConfigJson.Options)!;
        return config with { Ticket = config.Ticket with { DimensionsConfirmed = true, ConfirmedBy = "engine tests", ConfirmedOn = "2026-11-03" } };
    }

    // ---- A readable identity wins over the queue's driver ------------------------------------------------------------

    [Theory]
    [InlineData(SetupMode.Setup)]
    [InlineData(SetupMode.Repair)]
    public void Device_naming_an_unsupported_model_is_not_configured_as_its_queue_drivers_model(SetupMode mode)
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4531)
            .WithNetworkQueue(Address, SampleConfig.P4531, name: "AMWAG Ticket Printer")
            .WithTicketForm()
            .WithNetworkIdentity(P4532())
            .Build();

        var run = scenario.Run(mode);

        Assert.Empty(run.Detection.Candidates);
        var mismatch = Assert.Single(run.Detection.Findings, f => f.Code == FindingCode.NetworkIdentityMismatch);
        Assert.Equal(Severity.Warning, mismatch.Severity);
        Assert.Equal("P-4532DN", mismatch.Data["reported"]);
        Assert.Contains("'AMWAG Ticket Printer'", mismatch.Detail, StringComparison.Ordinal);
        Assert.Equal(SelectionReason.NoCandidates, run.Selection.Reason);
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Fact]
    public void Requested_device_naming_an_unsupported_model_is_not_offered_and_an_asserted_model_is_refused()
    {
        var scenario = new ScenarioBuilder().WithNetworkIdentity(P4532()).Build();
        var session = scenario.CreateSession();

        var detection = Detect(scenario, SetupMode.Setup, Address);

        Assert.Empty(detection.Candidates);
        Assert.Equal("P-4532DN", Assert.Single(detection.Findings, f => f.Code == FindingCode.NetworkIdentityMismatch).Data["reported"]);
        Assert.Null(session.Resolve(detection, new TargetSelector(PrinterCandidate.NetworkKey(Address), SampleConfig.P4531, ModelAssertedByUser: true)));
    }

    [Fact]
    public void Ambiguous_identity_behind_a_queue_is_not_configured()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4531)
            .WithNetworkQueue(Address, SampleConfig.P4531)
            .WithNetworkIdentity(new NetworkIdentity { Address = Address, RawPortReachable = true, SnmpResponded = true, SysDescr = "TA P-4030DN / P-5032DN" })
            .Build();

        var detection = Detect(scenario);

        Assert.Empty(detection.Candidates);
        var finding = Assert.Single(detection.Findings, f => f.Code == FindingCode.NetworkIdentityMismatch);
        Assert.Contains("none is chosen", finding.Detail, StringComparison.Ordinal);
        Assert.Equal(SelectionReason.NoCandidates, scenario.CreateSession().SelectAutomatically(detection).Reason);
    }

    [Fact]
    public void Another_vendors_printer_behind_a_ta_queue_is_not_a_candidate()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4531)
            .WithNetworkQueue(Address, SampleConfig.P4531, name: "Ticket printer")
            .WithNetworkIdentity(new NetworkIdentity
            {
                Address = Address,
                RawPortReachable = true,
                SnmpResponded = true,
                SysDescr = "HP ETHERNET MULTI-ENVIRONMENT",
                SysObjectId = "1.3.6.1.4.1.11.2.3.9.1",
                DeviceDescription = "HP LaserJet M404dn",
            })
            .Build();

        var detection = Detect(scenario);

        Assert.Empty(detection.Candidates);
        var finding = Assert.Single(detection.Findings, f => f.Code == FindingCode.NetworkIdentityMismatch);
        Assert.Contains("'HP LaserJet M404dn'", finding.Detail, StringComparison.Ordinal);
        Assert.Contains("'Ticket printer'", finding.Detail, StringComparison.Ordinal);
    }

    // ---- An unrelated printer (no queue driver claims a supported model, nobody named it) is no mismatch -------------

    /// <summary>An office Kyocera MFP as its firmware reports it (no IEEE 1284 ID, model name under the Kyocera private MIB).</summary>
    private static NetworkIdentity TaskAlfa(string address) => new()
    {
        Address = address,
        RawPortReachable = true,
        SnmpResponded = true,
        SysDescr = "KYOCERA Document Solutions Printing System",
        SysObjectId = "1.3.6.1.4.1.1347.41",
        DeviceDescription = "TASKalfa 3253ci",
        VendorModelName = "TASKalfa 3253ci",
    };

    [Theory]
    [InlineData("Kyocera TASKalfa 3253ci KX")]
    [InlineData(SampleConfig.MicrosoftPcl6ClassDriver)]
    public void Unrelated_kyocera_printer_on_its_own_driver_is_information_not_a_mismatch(string driver)
    {
        const string office = "10.1.2.60";
        var scenario = new ScenarioBuilder()
            .WithConfig(_ => Shipped())
            .WithDriver(driver)
            .WithNetworkQueue(office, name: "Office MFP", driverName: driver)
            .WithNetworkIdentity(TaskAlfa(office))
            .Build();

        var detection = Detect(scenario);

        Assert.Contains(office, scenario.Network.ProbedAddresses);
        Assert.Empty(detection.Candidates);
        Assert.DoesNotContain(detection.Findings, f => f.Code == FindingCode.NetworkIdentityMismatch);
        var unrelated = Assert.Single(detection.Findings, f => f.Code == FindingCode.UnsupportedPrinterDetected && f.Data.GetValueOrDefault("address") == office);
        Assert.Equal((Severity.Info, "TASKalfa 3253ci"), (unrelated.Severity, unrelated.Data["reported"]));
        Assert.Contains("'Office MFP'", unrelated.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Unrelated_printer_naming_several_models_on_a_class_driver_queue_is_information()
    {
        var scenario = new ScenarioBuilder()
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue(Address, name: "Office", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkIdentity(new NetworkIdentity { Address = Address, RawPortReachable = true, SnmpResponded = true, SysDescr = "TA P-4030DN / P-5032DN" })
            .Build();

        var detection = Detect(scenario);

        Assert.Empty(detection.Candidates);
        Assert.DoesNotContain(detection.Findings, f => f.Code == FindingCode.NetworkIdentityMismatch);
        Assert.Contains("none is chosen", Assert.Single(detection.Findings, f => f is { Code: FindingCode.UnsupportedPrinterDetected, Severity: Severity.Info }).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Requested_unrelated_kyocera_printer_is_a_mismatch()
    {
        const string office = "10.1.2.60";
        var scenario = new ScenarioBuilder()
            .WithConfig(_ => Shipped())
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue(office, name: "Office MFP", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkIdentity(TaskAlfa(office))
            .WithBranch(new BranchOverride { PrinterAddress = office })
            .Build();

        var detection = Detect(scenario);

        Assert.Empty(detection.Candidates);
        var mismatch = Assert.Single(detection.Findings, f => f.Code == FindingCode.NetworkIdentityMismatch);
        Assert.Equal((Severity.Warning, "TASKalfa 3253ci"), (mismatch.Severity, mismatch.Data["reported"]));
    }

    [Fact]
    public void Another_vendors_printer_with_a_1284_id_on_a_class_driver_queue_is_left_alone_without_a_warning()
    {
        var scenario = new ScenarioBuilder()
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue(Address, name: "Office", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkIdentity(new NetworkIdentity
            {
                Address = Address,
                RawPortReachable = true,
                SnmpResponded = true,
                SysDescr = "HP ETHERNET MULTI-ENVIRONMENT",
                Ieee1284DeviceId = "MFG:HP;MDL:HP LaserJet M404dn;CMD:PJL,PCLXL;CLS:PRINTER;",
            })
            .Build();

        var detection = Detect(scenario);

        Assert.Empty(detection.Candidates);
        Assert.DoesNotContain(detection.Findings, f => f.Code is FindingCode.NetworkIdentityMismatch or FindingCode.UnsupportedPrinterDetected);
    }

    [Fact]
    public void Another_vendors_printer_on_a_class_driver_queue_is_left_alone_without_a_warning()
    {
        var scenario = new ScenarioBuilder()
            .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkQueue(Address, name: "Office", driverName: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithNetworkIdentity(new NetworkIdentity { Address = Address, RawPortReachable = true, SnmpResponded = true, SysDescr = "HP ETHERNET MULTI-ENVIRONMENT" })
            .Build();

        var detection = Detect(scenario);

        Assert.Contains(Address, scenario.Network.ProbedAddresses);
        Assert.Empty(detection.Candidates);
        Assert.DoesNotContain(detection.Findings, f => f.Code == FindingCode.NetworkIdentityMismatch);
    }

    [Theory]
    [InlineData(null)] // sysDescr only: a brand string
    [InlineData("TA_UTAX Printing System")] // a description without a model designation
    public void Ta_device_that_names_no_model_stays_driver_only_behind_its_queue(string? deviceDescription)
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4531)
            .WithNetworkQueue(Address, SampleConfig.P4531)
            .WithNetworkIdentity(new NetworkIdentity
            {
                Address = Address,
                RawPortReachable = true,
                SnmpResponded = true,
                SysDescr = "TA_UTAX Printing System",
                SysObjectId = "1.3.6.1.4.1.1347.41",
                DeviceDescription = deviceDescription,
            })
            .Build();

        var detection = Detect(scenario);

        var candidate = Assert.Single(detection.Candidates);
        Assert.Equal((SampleConfig.P4531, MatchConfidence.DriverOnly), (candidate.Model?.Id, candidate.Confidence));
        Assert.True(candidate.IsEligible);
        Assert.DoesNotContain(detection.Findings, f => f.Code == FindingCode.NetworkIdentityMismatch);
    }

    [Fact]
    public void Kyocera_model_name_naming_the_drivers_model_is_not_rejected()
    {
        var scenario = new ScenarioBuilder()
            .WithModelDriver(SampleConfig.P4531)
            .WithNetworkQueue(Address, SampleConfig.P4531)
            .WithNetworkIdentity(new NetworkIdentity { Address = Address, RawPortReachable = true, SnmpResponded = true, VendorModelName = "TA P-4531DN" })
            .Build();

        var detection = Detect(scenario);

        var candidate = Assert.Single(detection.Candidates);
        Assert.Equal(SampleConfig.P4531, candidate.Model?.Id);
        Assert.True(candidate.IsEligible);
        Assert.DoesNotContain(detection.Findings, f => f.Code == FindingCode.NetworkIdentityMismatch);
    }

    // ---- prtGeneralPrinterName is never model evidence (shipped manifest: bare network model names) -------------------

    [Fact]
    public void Unsupported_branch_printer_named_after_a_supported_model_is_not_installed()
    {
        var scenario = new ScenarioBuilder()
            .WithConfig(_ => Shipped())
            .WithNetworkIdentity(P4532() with { PrinterName = "P-4030DN" })
            .WithBranch(new BranchOverride { PrinterAddress = Address })
            .Build();

        var run = scenario.Run();

        Assert.Empty(run.Detection.Candidates);
        Assert.Null(run.Selection.Selected);
        Assert.Equal("P-4532DN", Assert.Single(run.Detection.Findings, f => f.Code == FindingCode.NetworkIdentityMismatch).Data["reported"]);
        Assert.Empty(scenario.Spooler.Writes);
        Assert.Empty(scenario.Installer.Calls);
    }

    [Fact]
    public void Supported_printer_named_after_another_model_is_identified_by_its_model_name_and_its_queue_repaired()
    {
        const string oldDriver = "P-4030DN v4 KX (PCL6)";
        var scenario = new ScenarioBuilder()
            .WithConfig(_ => Shipped())
            .WithDriver(oldDriver)
            .WithNetworkQueue(Address, name: "Ticket printer", driverName: oldDriver)
            .WithNetworkIdentity(P4532() with { DeviceDescription = "P-4531DN", VendorModelName = "P-4531DN", PrinterName = "P-4030DN" })
            .Build();

        var run = scenario.Run();

        var target = run.Selection.Selected!;
        Assert.Equal((SampleConfig.P4531, MatchConfidence.Exact), (target.Model?.Id, target.Confidence));
        Assert.True(target.IdentityConflict);
        Assert.True(Has(run.Detection.Findings, FindingCode.IdentityConflict, Severity.Warning));
        Assert.DoesNotContain(run.Detection.Findings, f => f.Code == FindingCode.NetworkIdentityMismatch);
        Assert.Contains(ActionKind.ChangeQueueDriver, Kinds(run.Plan!));
        Assert.True(run.Apply!.Succeeded, string.Join("; ", run.Apply.Results.Select(r => $"{r.Action.Kind}: {r.Outcome} {r.Detail}")));
        Assert.Equal("P-4531DN v4 KX (PCL6)", Assert.Single(scenario.Spooler.Queues, q => q.Name == "Ticket printer").DriverName);
    }

    [Fact]
    public void Kyocera_firmware_without_a_1284_id_reporting_an_unsupported_model_is_rejected_with_the_shipped_manifest()
    {
        var scenario = new ScenarioBuilder()
            .WithConfig(_ => Shipped())
            .WithDriver("P-4531DN v4 KX (PCL6)")
            .WithNetworkQueue(Address, name: "AMWAG Ticket Printer", driverName: "P-4531DN v4 KX (PCL6)")
            .WithTicketForm()
            .WithNetworkIdentity(P4532())
            .Build();

        var setup = scenario.Run();
        var repair = scenario.Run(SetupMode.Repair);

        Assert.Empty(setup.Detection.Candidates);
        Assert.Empty(repair.Detection.Candidates);
        Assert.Empty(scenario.Spooler.Writes);
    }
}
