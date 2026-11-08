using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Tests.Flow;

public sealed class SetupFlowExitCodesTests
{
    [Theory]
    [InlineData(FlowOutcome.Ready, ExitCodes.Ready)]
    [InlineData(FlowOutcome.ReadyWithWarnings, ExitCodes.ReadyWithWarnings)]
    [InlineData(FlowOutcome.NotReady, ExitCodes.NotReady)]
    [InlineData(FlowOutcome.NoPrinter, ExitCodes.NoSupportedPrinter)]
    [InlineData(FlowOutcome.Cancelled, ExitCodes.Cancelled)]
    [InlineData(FlowOutcome.ElevationDeclined, ExitCodes.ElevationRequired)]
    [InlineData(FlowOutcome.Blocked, ExitCodes.Blocked)]
    [InlineData(FlowOutcome.Failed, ExitCodes.InternalError)]
    public void Outcomes_map_to_the_documented_exit_codes(FlowOutcome outcome, int expected)
    {
        Assert.Equal(expected, SetupFlowExitCodes.For(outcome, [], selectionRequired: false));
    }

    [Fact]
    public void An_unanswered_choice_between_printers_is_selection_required()
    {
        Assert.Equal(ExitCodes.SelectionRequired, SetupFlowExitCodes.For(FlowOutcome.Cancelled, [], selectionRequired: true));
    }

    [Theory]
    [InlineData(FindingCode.ConfigurationInvalid, ExitCodes.ConfigurationInvalid)]
    [InlineData(FindingCode.SpoolerDisabled, ExitCodes.SpoolerUnavailable)]
    [InlineData(FindingCode.SpoolerUnavailable, ExitCodes.SpoolerUnavailable)]
    [InlineData(FindingCode.DimensionsUnconfirmed, ExitCodes.ConfigurationInvalid)]
    [InlineData(FindingCode.ProtectedPrintModeEnabled, ExitCodes.Blocked)]
    public void Blocked_runs_name_the_blocking_area(FindingCode code, int expected)
    {
        Assert.Equal(expected, SetupFlowExitCodes.For(FlowOutcome.Blocked, [new Finding(code, Severity.Error, "x")], selectionRequired: false));
    }

    [Fact]
    public void A_failure_caused_by_an_unavailable_spooler_says_so()
    {
        Finding[] findings = [new(FindingCode.OperationFailed, Severity.Error, "RPC"), new(FindingCode.SpoolerUnavailable, Severity.Error, "RPC")];

        Assert.Equal(ExitCodes.SpoolerUnavailable, SetupFlowExitCodes.For(FlowOutcome.Failed, findings, selectionRequired: false));
    }

    [Theory]
    [InlineData(Severity.Info)]
    [InlineData(Severity.Warning)]
    public void Findings_below_error_do_not_change_the_exit_code(Severity severity)
    {
        Finding[] findings =
        [
            new(FindingCode.ProtectedPrintModeEnabled, Severity.Error, "blocked"),
            new(FindingCode.SpoolerDisabled, severity, "running but set to Disabled"),
            new(FindingCode.ConfigurationInvalid, severity, "x"),
            new(FindingCode.DimensionsUnconfirmed, severity, "--allow-unconfirmed-dimensions"),
        ];

        Assert.Equal(ExitCodes.Blocked, SetupFlowExitCodes.For(FlowOutcome.Blocked, findings, selectionRequired: false));
    }
}
