using Amwag.PrinterSetup.App.Localization;
using Amwag.PrinterSetup.App.UI;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Tests.UI;

/// <summary>The step list's status texts, including the one that replaces "failed" for a step a refusal never began.</summary>
public sealed class StepListTests
{
    [Fact]
    public void A_step_a_refusal_never_began_says_so_instead_of_failed() => Sta.Run(() =>
    {
        using var steps = new StepList(Strings.English);

        steps.SetState(StepId.InstallDriver, StepState.Failed, "step.notStarted");
        string refused = steps.Describe(StepId.InstallDriver);
        steps.SetState(StepId.InstallDriver, StepState.Failed);
        string failed = steps.Describe(StepId.InstallDriver);

        Assert.Equal("Installing driver: Not started; nothing was changed", refused);
        Assert.Equal("Installing driver: Installation failed", failed);
        Assert.Equal(StepState.Failed, steps.StateOf(StepId.InstallDriver));
    });

    [Fact]
    public void Reset_drops_a_replaced_status_text() => Sta.Run(() =>
    {
        using var steps = new StepList(Strings.Arabic);
        steps.SetState(StepId.ConfigureTicket, StepState.Failed, "step.notStarted");

        steps.Reset();

        Assert.Equal(Strings.Arabic.StepTitle(StepId.ConfigureTicket) + ": " + Strings.Arabic.Text("step.pending"), steps.Describe(StepId.ConfigureTicket));
    });
}
