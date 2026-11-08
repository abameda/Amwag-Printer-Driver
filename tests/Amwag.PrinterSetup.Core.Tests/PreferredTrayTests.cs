using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;
using static Amwag.PrinterSetup.Core.Setup.ActionKind;
using static Amwag.PrinterSetup.Core.Tests.TestHelpers;

namespace Amwag.PrinterSetup.Core.Tests;

/// <summary>
/// A multipurpose tray that is only preferred (ticketPaperSource.requiredForTicket false: a cassette can feed the ticket too)
/// and that the driver replaces: the driver's source is written once with a warning, then accepted, so the defaults are not
/// rewritten on every run and the employee's own defaults with that source do not make the printer "not ready".
/// </summary>
public sealed class PreferredTrayTests
{
    private static readonly string Queue4030 = SampleConfig.DriverName(SampleConfig.P4030);

    /// <summary>The tray is only preferred; the driver turns it into Auto (7) for the ticket, as a bulk feeder that disables the MP tray does.</summary>
    private static ScenarioBuilder PreferredTrayReplacedByTheDriver(bool correctDefaults = false) => PreferredTray(correctDefaults)
        .WithModelDriverProfile(SampleConfig.P4030, p => p with { CassetteMinimumWidthTenthsOfMillimeter = 1050, SourceBelowCassetteMinimum = DmBin.Auto });

    private static ScenarioBuilder PreferredTray(bool correctDefaults = true) => new ScenarioBuilder()
        .WithInstalledUsbPrinter(correctDefaults: correctDefaults)
        .WithModel(SampleConfig.P4030, m => m with { TicketPaperSource = m.TicketPaperSource with { RequiredForTicket = false } });

    private static int DevModeWrites(Scenario scenario) => scenario.Spooler.Writes.Count(w => w.Operation == nameof(IPrintSpooler.SetDevMode));

    [Fact]
    public void Replaced_tray_is_written_once_and_then_left_alone()
    {
        var scenario = PreferredTrayReplacedByTheDriver().Build();
        var first = scenario.Run();
        Assert.Equal(ActionOutcome.Succeeded, Outcome(first.Apply!, ConfigureGlobalDefaults));
        Assert.Equal(DmBin.Auto, scenario.Spooler.GetStoredDevMode(Queue4030, DevModeScope.Global)!.DefaultSource);
        int writes = DevModeWrites(scenario);

        var second = scenario.Run(SetupMode.Repair);

        // The driver's source is no paperSource difference any more: nothing is planned or written again.
        Assert.Empty(second.Plan!.Actions);
        Assert.DoesNotContain(second.Plan.Findings, f => f.Code == FindingCode.GlobalDefaultsIncorrect);
        var correct = Assert.Single(second.Plan.Findings, f => f.Code == FindingCode.GlobalDefaultsCorrect);
        Assert.Contains("the driver keeps source 7 instead of the multipurpose tray", correct.Detail, StringComparison.Ordinal);
        Assert.Equal(writes, DevModeWrites(scenario));
        // It stays the documented warning.
        Assert.Equal(CheckStatus.Warning, Check(second.Verification!, VerificationItem.DefaultPaperSource).Status);
        Assert.Equal(ReadinessLevel.ReadyWithWarnings, second.Verification!.Readiness);
    }

    [Fact]
    public void Replaced_tray_in_the_employees_own_defaults_is_ready_with_warnings()
    {
        var scenario = PreferredTrayReplacedByTheDriver().WithPerUserDefaults(Queue4030, d => d.Scale = 80).Build();

        var run = scenario.Run();

        Assert.Equal(ActionOutcome.Succeeded, Outcome(run.Apply!, ConfigureUserDefaults));
        Assert.Equal(DmBin.Auto, scenario.Spooler.GetStoredDevMode(Queue4030, DevModeScope.PerUser)!.DefaultSource);
        // The per-user defaults this run wrote carry the driver's source, as the queue's do: no conflict.
        Assert.Equal(CheckStatus.Pass, Check(run.Verification!, VerificationItem.UserDefaultsConsistent).Status);
        Assert.Equal(CheckStatus.Warning, Check(run.Verification!, VerificationItem.DefaultPaperSource).Status);
        Assert.Equal(ReadinessLevel.ReadyWithWarnings, run.Verification!.Readiness);

        // And the next run leaves both alone.
        int writes = DevModeWrites(scenario);
        Assert.Empty(scenario.Run(SetupMode.Repair).Plan!.Actions);
        Assert.Equal(writes, DevModeWrites(scenario));
    }

    [Fact]
    public void Defaults_that_already_carry_the_drivers_source_are_not_written_again_when_the_action_is_planned_anyway()
    {
        // The state after a first run; the action is planned without reading the defaults (as when the queue was missing).
        var scenario = PreferredTrayReplacedByTheDriver(correctDefaults: true).Build();
        var global = scenario.Spooler.GetStoredDevMode(Queue4030, DevModeScope.Global)!;
        global.DefaultSource = DmBin.Auto;
        scenario.Spooler.SetStoredDevMode(Queue4030, DevModeScope.Global, global);
        var session = scenario.CreateSession();
        var plan = new SetupPlan
        {
            Target = session.SelectAutomatically(session.Detect(new DetectRequest(), default)).Selected!,
            Mode = SetupMode.Setup,
            Actions = [new PlannedAction(ConfigureGlobalDefaults, true, "queue defaults") { Parameters = new Dictionary<string, string> { ["queueName"] = Queue4030 } }],
        };

        var apply = session.Apply(plan, ApplyScope.All, null, default);

        var result = Result(apply, ConfigureGlobalDefaults);
        Assert.Equal(ActionOutcome.AlreadySatisfied, result.Outcome);
        Assert.Contains("the driver keeps source 7", result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, DevModeWrites(scenario));
    }

    [Fact]
    public void Employees_own_defaults_with_another_source_only_warn_when_the_tray_is_only_preferred()
    {
        // The queue defaults are right; the employee's own defaults pick the cassette, which can feed this model's ticket.
        var preferred = PreferredTray().WithPerUserDefaults(Queue4030, d => d.DefaultSource = 257).Build();
        var required = new ScenarioBuilder().WithInstalledUsbPrinter().WithPerUserDefaults(Queue4030, d => d.DefaultSource = 257).Build();

        var warned = Verify(preferred);
        var failed = Verify(required);

        var user = Check(warned, VerificationItem.UserDefaultsConsistent);
        Assert.Equal((CheckStatus.Warning, "differs: paperSource"), (user.Status, user.Actual));
        Assert.True(Has(warned.Findings, FindingCode.UserDefaultsIncorrect, Severity.Warning));
        Assert.Equal(ReadinessLevel.ReadyWithWarnings, warned.Readiness);
        // Only the multipurpose tray can feed a model that requires it: that stays a failure.
        Assert.Equal(CheckStatus.Fail, Check(failed, VerificationItem.UserDefaultsConsistent).Status);
        Assert.Equal(ReadinessLevel.NotReady, failed.Readiness);
    }

    [Fact]
    public void Required_tray_is_never_replaced_by_the_drivers_source()
    {
        // requiredForTicket (the shipped models): the driver's substitute is still a difference, and nothing is written.
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter()
            .WithModelDriverProfile(SampleConfig.P4030, p => p with { CassetteMinimumWidthTenthsOfMillimeter = 1050, SourceBelowCassetteMinimum = DmBin.Auto })
            .Build();
        var global = scenario.Spooler.GetStoredDevMode(Queue4030, DevModeScope.Global)!;
        global.DefaultSource = DmBin.Auto;
        scenario.Spooler.SetStoredDevMode(Queue4030, DevModeScope.Global, global);

        var run = scenario.Run(SetupMode.Repair);

        Assert.Equal("paperSource", Assert.Single(run.Plan!.Findings, f => f.Code == FindingCode.GlobalDefaultsIncorrect).Data["fields"]);
        Assert.Equal(FindingCode.PaperSourceRejected, Result(run.Apply!, ConfigureGlobalDefaults).FailureCode);
        Assert.Equal(0, DevModeWrites(scenario));
        Assert.Equal(CheckStatus.Fail, Check(run.Verification!, VerificationItem.DefaultPaperSource).Status);
    }

    private static VerificationReport Verify(Scenario scenario)
    {
        var session = scenario.CreateSession(SetupMode.Diagnostics);
        return session.Verify(TargetSelector.For(session.SelectAutomatically(session.Detect(new DetectRequest(), default)).Selected!), default);
    }
}
