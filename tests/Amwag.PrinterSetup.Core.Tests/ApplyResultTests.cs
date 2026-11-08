using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.Core.Tests;

/// <summary>What <see cref="ApplyResult.Succeeded"/> and <see cref="ApplyResult.Refused"/> promise to their consumers.</summary>
public sealed class ApplyResultTests
{
    private static readonly PlannedAction Install = new(ActionKind.InstallDriver, RequiresElevation: true, "Install TA P-4030DN KX");

    [Fact]
    public void A_refusal_without_action_results_is_not_a_success()
    {
        var refusal = new ApplyResult
        {
            Findings = [new Finding(FindingCode.OperationFailed, Severity.Error, "Another AMWAG Printer Setup run is applying changes on this computer; nothing was changed.")],
        };

        Assert.False(refusal.Succeeded);
        Assert.True(refusal.Refused);
    }

    [Fact]
    public void A_failed_action_is_a_failure_not_a_refusal()
    {
        var failed = new ApplyResult
        {
            Results = [new ActionResult(Install, ActionOutcome.Failed, "InstallPrinterDriverFromPackage failed.") { FailureCode = FindingCode.DriverInstallFailed }],
            Findings = [new Finding(FindingCode.DriverInstallFailed, Severity.Error, "InstallPrinterDriverFromPackage failed.")],
        };

        Assert.False(failed.Succeeded);
        Assert.False(failed.Refused);
    }

    [Fact]
    public void Warnings_and_nothing_to_do_are_successes()
    {
        var warned = new ApplyResult
        {
            Results = [new ActionResult(Install, ActionOutcome.Succeeded, "Installed.")],
            Findings = [new Finding(FindingCode.RebootRequired, Severity.Warning, "Windows must be restarted to finish installing the printer driver.")],
            RebootRequired = true,
        };
        var cancelled = new ApplyResult
        {
            Results = [new ActionResult(Install, ActionOutcome.Skipped, "Cancelled before it started.")],
            Findings = [new Finding(FindingCode.Cancelled, Severity.Warning, "The operation was cancelled.")],
        };

        Assert.True(warned.Succeeded);
        Assert.False(warned.Refused);
        Assert.True(cancelled.Succeeded);
        Assert.False(cancelled.Refused);
        Assert.True(new ApplyResult().Succeeded);
        Assert.False(new ApplyResult().Refused);
    }

    [Fact]
    public void Applying_a_blocked_plan_is_refused_and_changes_nothing()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(SampleConfig.P4030, driverBound: false)
            .WithPackage(p => p with { MinOsBuild = 22000 })
            .WithPlatform(p => p with { ProductName = "Windows 10 Pro", DisplayVersion = "22H2", Build = 19045 })
            .Build();

        var run = scenario.Run();

        Assert.False(run.Plan!.CanProceed);
        Assert.False(run.Apply!.Succeeded);
        Assert.True(run.Apply.Refused);
        Assert.Contains(run.Apply.Findings, f => f is { Code: FindingCode.DriverPackageIncompatible, Severity: Severity.Error });
        Assert.Empty(scenario.Spooler.Writes);
    }
}
