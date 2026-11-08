using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;

namespace Amwag.PrinterSetup.Core.Setup;

/// <summary>Machine-wide conditions: spooler, protected print mode, OS and architecture, unconfirmed ticket dimensions.</summary>
internal static class EnvironmentChecks
{
    public const int MinimumBuild = 10240;

    public static IReadOnlyList<Finding> Evaluate(
        PlatformInfo platform,
        SpoolerServiceStatus spooler,
        bool spoolerUnavailable,
        DeploymentConfig config,
        SetupOptions options)
    {
        var findings = new List<Finding>();
        string state = spooler.State.ToString();
        string startMode = spooler.StartMode.ToString();

        if (spoolerUnavailable)
            findings.Add(Findings.Error(FindingCode.SpoolerUnavailable, "The Print Spooler did not respond (RPC error); printers cannot be read or configured.", ("state", state)));
        else if (spooler.State == ServiceRunState.NotInstalled)
            findings.Add(Findings.Error(FindingCode.SpoolerUnavailable, "The Print Spooler service is not installed.", ("state", state)));
        else if (!spooler.IsRunning && spooler.StartMode == ServiceStartMode.Disabled)
            findings.Add(Findings.Error(FindingCode.SpoolerDisabled, "The Print Spooler service is disabled, so it cannot be started.", ("state", state), ("startMode", startMode)));
        else if (spooler.StartMode == ServiceStartMode.Disabled)
            findings.Add(Findings.Warning(FindingCode.SpoolerDisabled, "The Print Spooler service is running but disabled; printing stops after the next restart.", ("state", state), ("startMode", startMode)));
        else if (spooler.State is ServiceRunState.Stopped or ServiceRunState.StopPending or ServiceRunState.Paused)
            findings.Add(Findings.Warning(FindingCode.SpoolerNotRunning, $"The Print Spooler service is {state}.", ("state", state), ("startMode", startMode)));

        if (platform.ProtectedPrintModeEnabled == true)
        {
            findings.Add(Findings.Error(FindingCode.ProtectedPrintModeEnabled,
                "Windows protected print mode is on; it only allows Microsoft inbox printer drivers, so the approved TA driver cannot be used."
                + (platform.ProtectedPrintModeDetail is { Length: > 0 } detail ? " " + detail : "")));
        }

        if (!string.IsNullOrEmpty(platform.Architecture) && !IsX64(platform.Architecture))
        {
            findings.Add(Findings.Error(FindingCode.UnsupportedArchitecture,
                $"Windows is {platform.Architecture}; the approved driver packages are for x64 only.", ("architecture", platform.Architecture)));
        }

        if (platform.Build is > 0 and < MinimumBuild)
        {
            findings.Add(Findings.Error(FindingCode.UnsupportedOperatingSystem,
                FormattableString.Invariant($"Windows build {platform.Build} is older than Windows 10 (build {MinimumBuild})."), ("build", platform.Build.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        }

        if (!config.Ticket.DimensionsConfirmed)
        {
            var severity = options.Mode == SetupMode.Diagnostics ? Severity.Info
                : options.AllowUnconfirmedDimensions ? Severity.Warning
                : Severity.Error;
            string detail = severity == Severity.Error
                ? $"The dimensions of '{config.Ticket.FormName}' are not confirmed in the configuration; Setup and Repair are blocked until IT confirms them."
                : $"The dimensions of '{config.Ticket.FormName}' are not confirmed in the configuration.";
            findings.Add(Findings.Create(FindingCode.DimensionsUnconfirmed, severity, detail, ("ticket", TicketDefaults.Describe(config.Ticket))));
        }

        return findings;
    }

    /// <summary>Environment errors that prevent any change.</summary>
    public static bool IsBlocker(Finding finding) =>
        finding.Severity == Severity.Error && finding.Code is
            FindingCode.SpoolerUnavailable or FindingCode.SpoolerDisabled or FindingCode.ProtectedPrintModeEnabled or
            FindingCode.UnsupportedArchitecture or FindingCode.UnsupportedOperatingSystem or FindingCode.DimensionsUnconfirmed;

    public static bool IsX64(string architecture) =>
        string.Equals(architecture, "x64", StringComparison.OrdinalIgnoreCase) || string.Equals(architecture, "amd64", StringComparison.OrdinalIgnoreCase);
}
