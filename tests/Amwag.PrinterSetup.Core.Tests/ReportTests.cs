using System.Globalization;
using System.Text.Json;
using Amwag.PrinterSetup.Core.Diagnostics;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.Core.Tests;

public sealed class ReportTests
{
    private static (Scenario Scenario, DiagnosticReport Report) FullRun()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, driverBound: false)
            .WithNetworkPrinter("10.0.0.7", modelId: null)
            .Build();
        var session = scenario.CreateSession();
        var detection = session.Detect(new DetectRequest { NetworkAddresses = ["10.0.0.7"] }, default);
        // The printer at 10.0.0.7 does not report its model, so the operator chooses; this run sets up the USB printer.
        var target = Assert.Single(detection.Candidates, c => c.Kind == TargetKind.UsbDevice);
        var plan = session.Assess(target, default);
        var apply = session.Apply(plan, ApplyScope.All, null, default);
        var verification = session.Verify(TargetSelector.For(target), default);
        var extra = new Finding(FindingCode.ElevationRequired, Severity.Info, "The machine steps ran in the elevated worker.");
        var report = session.CreateReport(detection, [plan], [apply], [verification], [extra, extra]);
        return (scenario, report);
    }

    [Fact]
    public void Report_carries_the_run_and_deduplicates_findings()
    {
        var (scenario, report) = FullRun();

        Assert.Equal(scenario.Clock.Now, report.GeneratedAt);
        Assert.Equal("1.0.0-test", report.AppVersion);
        Assert.Equal("test-session", report.SessionId);
        Assert.Equal("2026.11-test", report.ConfigVersion);
        Assert.Equal("AMWAG Ticket, 82.296 mm x 170.18 mm (3.24 in x 6.70 in)", report.TicketDescription);
        Assert.True(report.TicketDimensionsConfirmed);
        Assert.Equal(2, report.Candidates.Count);
        Assert.Single(report.Findings, f => f.Code == FindingCode.ElevationRequired);
        Assert.Single(report.Findings, f => f.Code == FindingCode.ApplicationMayOverrideDefaults);
    }

    [Fact]
    public void Text_report_has_every_section_and_the_key_facts()
    {
        var (_, report) = FullRun();

        string text = DiagnosticReportFormatter.ToText(report);

        foreach (string section in new[]
        {
            "== Platform ==", "== Print Spooler ==", "== Plug and Play printer devices (1) ==", "== Print queues (1) ==", "== Ports (",
            "== Relevant printer drivers (", "== Ticket form ==", "== Network identities (1) ==", "== Candidates (2) ==", "== Plans (1) ==",
            "== Apply results (1) ==", "== Verification (1) ==", "== All findings (",
        })
        {
            Assert.Contains(section, text, StringComparison.Ordinal);
        }

        Assert.Contains("Windows 11 Pro 24H2 (build 26100.2033)", text, StringComparison.Ordinal);
        // The snapshot is the state found by detection, before any change; the verification shows the final state.
        Assert.Contains("AMWAG Ticket    missing", text, StringComparison.Ordinal);
        // A label as long as the label column still has a space before its value (it read "Other user formsnone").
        Assert.Matches(@"(?m)^Other user forms \S", text);
        Assert.Contains("TA P-4030DN KX", text, StringComparison.Ordinal);
        Assert.Contains("CreateQueue: Succeeded", text, StringComparison.Ordinal);
        Assert.Contains("PrintTicketMediaSize", text, StringComparison.Ordinal);
        Assert.Contains("[Info] ApplicationMayOverrideDefaults", text, StringComparison.Ordinal);
        Assert.Contains("10.0.0.7: raw port reachable, SNMP silent", text, StringComparison.Ordinal);
        Assert.Contains("Ready", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_report_is_culture_invariant()
    {
        var (_, report) = FullRun();
        var previous = CultureInfo.CurrentCulture;
        string invariant = DiagnosticReportFormatter.ToText(report);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-EG");
            Assert.Equal(invariant, DiagnosticReportFormatter.ToText(report));
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal(invariant, DiagnosticReportFormatter.ToText(report));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
        Assert.Contains("82.296 mm", invariant, StringComparison.Ordinal);
    }

    [Fact]
    public void Json_report_round_trips_with_camel_case_enums()
    {
        var (_, report) = FullRun();

        string json = DiagnosticReportFormatter.ToJson(report);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("test-session", root.GetProperty("sessionId").GetString());
        Assert.Equal("setup", root.GetProperty("mode").GetString());
        Assert.Equal("ready", root.GetProperty("verifications")[0].GetProperty("readiness").GetString());
        Assert.Equal(2, root.GetProperty("candidates").GetArrayLength());
        Assert.Equal("8.4.1013.0", root.GetProperty("verifications")[0].GetProperty("summary").GetProperty("driverVersion").GetString());

        var back = JsonSerializer.Deserialize<DiagnosticReport>(json, Configuration.ConfigJson.Options)!;
        Assert.Equal(report.Findings.Count, back.Findings.Count);
        Assert.Equal(report.Plans[0].Actions.Count, back.Plans[0].Actions.Count);
    }

    [Fact]
    public void Apply_results_say_whether_actions_failed_or_nothing_was_applied()
    {
        var scenario = new ScenarioBuilder().Build();
        var install = new PlannedAction(ActionKind.InstallDriver, RequiresElevation: true, "Install TA P-4030DN KX");
        var failed = new ApplyResult
        {
            Results = [new ActionResult(install, ActionOutcome.Failed, "InstallPrinterDriverFromPackage failed.") { FailureCode = FindingCode.DriverInstallFailed }],
            Findings = [new Finding(FindingCode.DriverInstallFailed, Severity.Error, "InstallPrinterDriverFromPackage failed.")],
        };
        var busy = new ApplyResult
        {
            Findings = [new Finding(FindingCode.OperationFailed, Severity.Error, "Another AMWAG Printer Setup run is applying changes on this computer; nothing was changed.")],
        };
        var report = scenario.CreateSession().CreateReport(null, [], [new ApplyResult(), failed, busy], [], []);

        string text = DiagnosticReportFormatter.ToText(report);

        Assert.Contains("== Apply results (3) ==" + Environment.NewLine + "- no failures" + Environment.NewLine + "- FAILED" + Environment.NewLine, text, StringComparison.Ordinal);
        Assert.Contains("- NOT APPLIED (refused: OperationFailed)" + Environment.NewLine + "    [Error] OperationFailed: Another AMWAG", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_without_a_snapshot_still_renders()
    {
        var scenario = new ScenarioBuilder().Build();
        var report = scenario.CreateSession().CreateReport(null, [], [], [], [new Finding(FindingCode.ConfigurationInvalid, Severity.Error, "models must contain at least one supported printer model.")]);

        string text = DiagnosticReportFormatter.ToText(report);

        Assert.Contains("No snapshot was captured.", text, StringComparison.Ordinal);
        Assert.Contains("[Error] ConfigurationInvalid", text, StringComparison.Ordinal);
        Assert.Contains("\"sessionId\"", DiagnosticReportFormatter.ToJson(report), StringComparison.Ordinal);
    }
}
