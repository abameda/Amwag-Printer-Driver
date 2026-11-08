using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;
using static Amwag.PrinterSetup.Core.Setup.ActionKind;
using static Amwag.PrinterSetup.Core.Tests.TestHelpers;

namespace Amwag.PrinterSetup.Core.Tests;

/// <summary>
/// A model whose ticket only the multipurpose tray can feed (requiredForTicket) needs that tray under the default
/// preferMultipurposeTray policy too: when the driver replaces it, nothing is written, in both scopes.
/// </summary>
public sealed class RequiredTrayTests
{
    private static readonly string Queue4030 = SampleConfig.DriverName(SampleConfig.P4030);

    [Theory]
    [InlineData(DevModeScope.Global)]
    [InlineData(DevModeScope.PerUser)]
    public void Required_tray_replaced_by_the_driver_fails_under_the_prefer_policy_and_writes_nothing(DevModeScope scope)
    {
        bool global = scope == DevModeScope.Global;
        // Global: wrong queue defaults. PerUser: correct queue defaults, but the employee's own defaults differ.
        var builder = new ScenarioBuilder()
            .WithInstalledUsbPrinter(correctDefaults: !global)
            .WithModelDriverProfile(SampleConfig.P4030, p => p with { CassetteMinimumWidthTenthsOfMillimeter = 1050, SourceBelowCassetteMinimum = DmBin.Auto });
        if (!global)
            builder.WithPerUserDefaults(Queue4030, d => d.Scale = 80);
        var scenario = builder.Build();
        Assert.Equal(PaperSourcePolicy.PreferMultipurposeTray, scenario.Config.PrintPreferences.PaperSource);
        Assert.True(scenario.Config.FindModel(SampleConfig.P4030)!.TicketPaperSource.RequiredForTicket);
        var before = scenario.Spooler.GetStoredDevMode(Queue4030, scope)!.ToArray();

        var run = scenario.Run();

        var result = Result(run.Apply!, global ? ConfigureGlobalDefaults : ConfigureUserDefaults);
        Assert.Equal(ActionOutcome.Failed, result.Outcome);
        Assert.Equal(FindingCode.PaperSourceRejected, result.FailureCode);
        Assert.Contains("requiredForTicket", result.Detail, StringComparison.Ordinal);
        Assert.Contains("no defaults were written", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(run.Apply!.Findings, f => f is { Code: FindingCode.PaperSourceRejected, Severity: Severity.Warning });
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Operation == nameof(IPrintSpooler.SetDevMode));
        Assert.Equal(before, scenario.Spooler.GetStoredDevMode(Queue4030, scope)!.ToArray());
    }
}
