using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Flow;

/// <summary>Maps a flow outcome (and the findings behind it) to the documented process exit codes.</summary>
internal static class SetupFlowExitCodes
{
    /// <param name="selectionRequired">
    /// The run stopped for a decision nobody made: a choice between several printers, or (silent) the model of the only usable
    /// printer, which IT confirms with --address, --model and --confirm-model.
    /// </param>
    public static int For(FlowOutcome outcome, IReadOnlyCollection<Finding> findings, bool selectionRequired) => outcome switch
    {
        FlowOutcome.Ready => ExitCodes.Ready,
        FlowOutcome.ReadyWithWarnings => ExitCodes.ReadyWithWarnings,
        FlowOutcome.NotReady => ExitCodes.NotReady,
        FlowOutcome.NoPrinter => ExitCodes.NoSupportedPrinter,
        FlowOutcome.Cancelled => selectionRequired ? ExitCodes.SelectionRequired : ExitCodes.Cancelled,
        FlowOutcome.ElevationDeclined => ExitCodes.ElevationRequired,
        // All are a matter of the configuration file, so all are "configuration" for deployment tools (docs/DEPLOYMENT.md §7).
        FlowOutcome.Blocked when Has(findings, FindingCode.ConfigurationInvalid, FindingCode.DimensionsUnconfirmed, FindingCode.ConfigurationChanged) => ExitCodes.ConfigurationInvalid,
        FlowOutcome.Blocked when Has(findings, FindingCode.SpoolerDisabled, FindingCode.SpoolerUnavailable) => ExitCodes.SpoolerUnavailable,
        FlowOutcome.Blocked => ExitCodes.Blocked,
        FlowOutcome.Failed when Has(findings, FindingCode.SpoolerUnavailable) => ExitCodes.SpoolerUnavailable,
        _ => ExitCodes.InternalError,
    };

    private static bool Has(IEnumerable<Finding> findings, params FindingCode[] codes) =>
        findings.Any(f => f.Severity == Severity.Error && codes.Contains(f.Code));
}
