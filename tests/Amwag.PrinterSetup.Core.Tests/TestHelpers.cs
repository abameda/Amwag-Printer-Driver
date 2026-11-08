using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.Core.Tests;

internal static class TestHelpers
{
    public static ActionKind[] Kinds(SetupPlan plan) => plan.Actions.Select(a => a.Kind).ToArray();

    public static ActionResult Result(ApplyResult apply, ActionKind kind) => apply.Results.Single(r => r.Action.Kind == kind);

    public static ActionOutcome Outcome(ApplyResult apply, ActionKind kind) => Result(apply, kind).Outcome;

    public static VerificationCheck Check(VerificationReport report, VerificationItem item) => report.Checks.Single(c => c.Item == item);

    public static bool Has(IEnumerable<Finding> findings, FindingCode code, Severity? severity = null) =>
        findings.Any(f => f.Code == code && (severity is null || f.Severity == severity));
}
