using System.Globalization;
using System.Text;
using System.Text.Json;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.Core.Diagnostics;

/// <summary>Renders a <see cref="DiagnosticReport"/> for IT: readable text, or JSON for tooling.</summary>
public static class DiagnosticReportFormatter
{
    private const int LabelWidth = 16;

    public static string ToText(DiagnosticReport report)
    {
        var text = new ReportText();
        WriteHeader(text, report);
        if (report.Snapshot is { } snapshot)
        {
            WritePlatform(text, snapshot.Platform);
            WriteSpooler(text, snapshot);
            WriteDevices(text, snapshot.Devices);
            WriteQueues(text, snapshot.Queues);
            WritePorts(text, snapshot.Ports);
            WriteDrivers(text, snapshot.Drivers, report);
            WriteForms(text, snapshot.Forms, report.TicketDescription);
            WriteNetwork(text, snapshot.NetworkIdentities);
        }
        else
        {
            text.Section("System");
            text.Line("No snapshot was captured.");
        }

        WriteCandidates(text, report.Candidates);
        WritePlans(text, report.Plans);
        WriteApplyResults(text, report.ApplyResults);
        WriteVerifications(text, report.Verifications);
        text.Section(Inv($"All findings ({report.Findings.Count})"));
        WriteFindings(text, report.Findings, "  ");
        return text.ToString();
    }

    public static string ToJson(DiagnosticReport report) => JsonSerializer.Serialize(report, ConfigJson.Options);

    private static void WriteHeader(ReportText text, DiagnosticReport report)
    {
        text.Line("AMWAG Printer Setup - diagnostic report");
        text.Line(new string('=', 39));
        text.Field("Generated", report.GeneratedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        text.Field("App version", report.AppVersion);
        text.Field("Session", report.SessionId);
        text.Field("Mode", report.Mode.ToString());
        text.Field("Configuration", $"{report.ConfigSource} (version {report.ConfigVersion})");
        text.Field("Ticket", report.TicketDescription);
        text.Field("Dimensions", report.TicketDimensionsConfirmed ? "confirmed" : "NOT confirmed");
        if (report.LogFile is { } logFile)
            text.Field("Log file", logFile);
    }

    private static void WritePlatform(ReportText text, PlatformInfo platform)
    {
        text.Section("Platform");
        string version = platform.DisplayVersion is { Length: > 0 } displayVersion ? " " + displayVersion : "";
        text.Field("Windows", Inv($"{platform.ProductName}{version} (build {platform.Build}.{platform.Ubr})"));
        text.Field("Architecture", $"{platform.Architecture} (process {platform.ProcessArchitecture})");
        text.Field("Machine", platform.MachineName);
        text.Field("User", $"{platform.UserName} (elevated: {YesNo(platform.IsElevated)}, system: {YesNo(platform.IsSystemAccount)}, interactive: {YesNo(platform.IsInteractive)})");
        text.Field("Protected print", platform.ProtectedPrintModeEnabled switch { true => "on", false => "off", null => "unknown" }
            + (platform.ProtectedPrintModeDetail is { Length: > 0 } detail ? $" ({detail})" : ""));
        text.Field("Driver policy", platform.RestrictDriverInstallationToAdministrators switch
        {
            true => "driver installation restricted to administrators",
            false => "driver installation not restricted",
            null => "Point and Print policy not configured (Windows default: administrators only)",
        });
    }

    private static void WriteSpooler(ReportText text, SystemSnapshot snapshot)
    {
        text.Section("Print Spooler");
        text.Field("State", $"{snapshot.Spooler.State}, start mode {snapshot.Spooler.StartMode}" + (snapshot.Spooler.Detail is { } detail ? $" ({detail})" : ""));
        text.Field("Captured", snapshot.CapturedAt.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
        foreach (string error in snapshot.CaptureErrors)
            text.Field("Capture error", error);
    }

    private static void WriteDevices(ReportText text, IReadOnlyList<PnpPrinterDevice> devices)
    {
        text.Section(Inv($"Plug and Play printer devices ({devices.Count})"));
        foreach (var device in devices)
        {
            text.Line($"- {device.InstanceId} [{device.Bus}, {(device.IsPresent ? "connected" : "not connected")}]");
            text.Detail("Name", device.FriendlyName ?? device.Description ?? "(none)");
            text.Detail("Hardware IDs", Join(device.HardwareIds));
            text.Detail("Compatible IDs", Join(device.CompatibleIds));
            text.Detail("Driver", device.DriverDescription is null ? "(none)" : $"{device.DriverDescription} {device.DriverVersion}".TrimEnd());
            text.Detail("Problem code", device.ProblemCode?.ToString(CultureInfo.InvariantCulture) ?? "none");
            text.Detail("USB port", device.UsbPortName ?? "(none)");
        }
    }

    private static void WriteQueues(ReportText text, IReadOnlyList<PrintQueueInfo> queues)
    {
        text.Section(Inv($"Print queues ({queues.Count})"));
        foreach (var queue in queues)
        {
            string server = queue.IsServerConnection ? $", server {queue.ServerName}" : "";
            text.Line($"- {queue.Name}");
            text.Detail("Driver", queue.DriverName);
            text.Detail("Port", queue.PortName + server);
            text.Detail("Status", (queue.Status == 0 ? "ready" : queue.Status.ToString()) + (queue.IsWorkOffline ? ", Use Printer Offline" : ""));
            text.Detail("Attributes", queue.Attributes.ToString());
            text.Detail("Jobs", queue.JobCount.ToString(CultureInfo.InvariantCulture));
        }
    }

    private static void WritePorts(ReportText text, IReadOnlyList<PrinterPortInfo> ports)
    {
        text.Section(Inv($"Ports ({ports.Count})"));
        foreach (var port in ports)
        {
            string target = port.Kind == PortKind.StandardTcpIp
                ? Inv($", {port.HostAddress}:{port.PortNumber} {port.Protocol}, SNMP {(port.SnmpEnabled == true ? "on" : "off")}")
                : "";
            text.Line($"- {port.Name} [{port.Kind}] {port.MonitorName}{target}".TrimEnd());
        }
    }

    private static void WriteDrivers(ReportText text, IReadOnlyList<PrinterDriverInfo> drivers, DiagnosticReport report)
    {
        var accepted = report.Candidates.Select(c => c.Model)
            .Concat(report.Plans.Select(p => p.Target.Model))
            .OfType<ModelDefinition>()
            .SelectMany(m => m.AcceptedDrivers.Select(d => d.Name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var relevant = drivers
            .Where(d => accepted.Contains(d.Name) || DriverRules.IsVendorName(d.Name) || DriverRules.IsMicrosoftClassDriver(d.Name))
            .ToList();

        text.Section(Inv($"Relevant printer drivers ({relevant.Count} of {drivers.Count})"));
        foreach (var driver in relevant)
        {
            text.Line($"- {driver.Name} {driver.Version?.ToString() ?? "(version unknown)"} [{driver.Environment}, v{driver.DriverModel.ToString(CultureInfo.InvariantCulture)}]");
            text.Detail("Provider", driver.Provider ?? driver.Manufacturer ?? "(unknown)");
            if (driver.InfPath is { } inf)
                text.Detail("INF", inf);
        }
    }

    private static void WriteForms(ReportText text, IReadOnlyList<FormInfo> forms, string ticketDescription)
    {
        text.Section("Ticket form");
        // TicketDescription is "<form name>, <size>"; the size part contains no ", ".
        int separator = ticketDescription.LastIndexOf(", ", StringComparison.Ordinal);
        string name = separator > 0 ? ticketDescription[..separator] : ticketDescription;
        var form = forms.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));
        if (form is null)
        {
            text.Field(name, "missing");
        }
        else
        {
            text.Field(form.Name, $"{form.Kind}, {Size(form)}, {(form.HasFullImageableArea ? "full imageable area" : Inv($"imageable area {form.ImageableLeft},{form.ImageableTop}-{form.ImageableRight},{form.ImageableBottom}"))}");
            var sameSize = forms.Where(f => !ReferenceEquals(f, form) && f.WidthMicrometers == form.WidthMicrometers && f.HeightMicrometers == form.HeightMicrometers).ToList();
            text.Field("Same size", sameSize.Count == 0 ? "none" : string.Join(", ", sameSize.Select(f => $"'{f.Name}' ({f.Kind})")));
        }

        // Forms someone created by hand matter (a near-duplicate ticket form, for example); driver-installed ones are only counted.
        var userForms = forms.Where(f => f.Kind == FormKind.User && !ReferenceEquals(f, form)).ToList();
        text.Field("Other user forms", userForms.Count == 0 ? "none" : string.Join(", ", userForms.Select(f => $"'{f.Name}' {Size(f)}")));
        text.Field("Driver forms", Inv($"{forms.Count(f => f.Kind == FormKind.Printer)} installed by printer drivers (not listed)"));
    }

    private static void WriteNetwork(ReportText text, IReadOnlyList<NetworkIdentity> identities)
    {
        text.Section(Inv($"Network identities ({identities.Count})"));
        foreach (var identity in identities)
        {
            text.Line($"- {identity.Address}: raw port {(identity.RawPortReachable ? "reachable" : "not reachable")}, SNMP {(identity.SnmpResponded ? "answered" : "silent")}");
            if (!string.Equals(identity.ResolvedAddress, identity.Address, StringComparison.OrdinalIgnoreCase))
                WriteOptional(text, "Resolved to", identity.ResolvedAddress);
            WriteOptional(text, "sysDescr", identity.SysDescr);
            WriteOptional(text, "Description", identity.DeviceDescription);
            WriteOptional(text, "Kyocera model", identity.VendorModelName);
            if (!string.IsNullOrEmpty(identity.PrinterName))
                text.Detail("Printer name", identity.PrinterName + " (set by an administrator; not used to identify the model)");
            WriteOptional(text, "1284 ID", identity.Ieee1284DeviceId);
            WriteOptional(text, "Serial", identity.SerialNumber);
            WriteOptional(text, "Object ID", identity.SysObjectId);
            WriteOptional(text, "Error", identity.Error);
        }
    }

    private static void WriteCandidates(ReportText text, IReadOnlyList<PrinterCandidate> candidates)
    {
        text.Section(Inv($"Candidates ({candidates.Count})"));
        foreach (var candidate in candidates)
        {
            string state = candidate.IsEligible ? "eligible" : candidate.NeedsConfirmation ? "needs confirmation" : "not identified";
            text.Line($"- {candidate.Key}: {candidate.Model?.Id ?? "unknown model"} [{candidate.Confidence}, {state}]");
            text.Detail("Connection", $"{candidate.Connection}{(candidate.NetworkAddress is { } address ? " " + address : "")}, {(candidate.PhysicallyAvailable ? "available" : "not available")}");
            if (candidate.NetworkAliases.Count > 0)
                text.Detail("Also reached as", string.Join(", ", candidate.NetworkAliases));
            text.Detail("Queue", candidate.Queue is { } queue ? $"{queue.Name} ({queue.DriverName} on {queue.PortName})" : "(none)");
            if (candidate.AdditionalQueues.Count > 0)
                text.Detail("Other queues", string.Join(", ", candidate.AdditionalQueues.Select(q => q.Name)));
            if (candidate.IdentityConflict)
                text.Detail("Conflict", "the device identity and the queue's driver disagree (the device identity wins)");
            foreach (string evidence in candidate.Evidence)
                text.Detail("Evidence", evidence);
        }
    }

    private static void WritePlans(ReportText text, IReadOnlyList<SetupPlan> plans)
    {
        text.Section(Inv($"Plans ({plans.Count})"));
        foreach (var plan in plans)
        {
            text.Line($"- {plan.Target.Key} ({plan.Target.Model?.Id ?? "unknown model"}), mode {plan.Mode}: "
                + $"{(plan.CanProceed ? "can proceed" : "BLOCKED")}, {(plan.RequiresElevation ? "needs administrator rights" : "no administrator rights needed")}");
            for (int i = 0; i < plan.Actions.Count; i++)
            {
                var action = plan.Actions[i];
                string parameters = action.Parameters.Count == 0 ? "" : " {" + string.Join(", ", action.Parameters.Select(p => $"{p.Key}={p.Value}")) + "}";
                text.Line(Inv($"    {i + 1}. {action.Kind}{(action.RequiresElevation ? " (admin)" : "")}: {action.Summary}{parameters}"));
            }
            if (plan.Actions.Count == 0)
                text.Line("    (no actions)");
            foreach (var blocker in plan.Blockers)
                text.Line($"    Blocker: {blocker.Code}: {blocker.Detail}");
            WriteFindings(text, plan.Findings, "    ");
        }
    }

    private static void WriteApplyResults(ReportText text, IReadOnlyList<ApplyResult> results)
    {
        text.Section(Inv($"Apply results ({results.Count})"));
        foreach (var result in results)
        {
            // A refusal (the worker found the target gone or the plan blocked, or another run applying) changed nothing.
            string state = result.Succeeded ? "no failures"
                : result.Refused ? $"NOT APPLIED (refused: {string.Join(", ", result.Findings.Where(f => f.Severity == Severity.Error).Select(f => f.Code).Distinct())})"
                : "FAILED";
            text.Line($"- {state}{(result.RebootRequired ? ", restart required" : "")}");
            foreach (var action in result.Results)
            {
                string failure = action.FailureCode is { } code
                    ? $" [{code}{(action.Win32Error is { } error ? Inv($", Win32 {error} (0x{error:X8})") : "")}]"
                    : "";
                text.Line($"    {action.Action.Kind}: {action.Outcome}{failure} - {action.Detail}");
            }
            WriteFindings(text, result.Findings, "    ");
        }
    }

    private static void WriteVerifications(ReportText text, IReadOnlyList<VerificationReport> verifications)
    {
        text.Section(Inv($"Verification ({verifications.Count})"));
        foreach (var verification in verifications)
        {
            var summary = verification.Summary;
            text.Line($"- {verification.TargetKey}: {verification.Readiness}");
            text.Detail("Printer", $"{summary.ModelName}, queue '{summary.QueueName}', driver {summary.DriverName} {summary.DriverVersion}".TrimEnd());
            text.Detail("Connection", summary.Connection);
            text.Detail("Paper", summary.PaperSize + (summary.PaperSource is { } source ? $", source {source}" : ""));

            int itemWidth = Math.Max(4, verification.Checks.Select(c => c.Item.ToString().Length).DefaultIfEmpty(4).Max());
            text.Line($"    {"Check".PadRight(itemWidth)}  {"Status",-13} Expected | Actual");
            foreach (var check in verification.Checks)
                text.Line($"    {check.Item.ToString().PadRight(itemWidth)}  {check.Status,-13} {check.Expected} | {check.Actual}  ({check.Detail})");
        }
    }

    private static void WriteFindings(ReportText text, IReadOnlyList<Finding> findings, string indent)
    {
        foreach (var finding in findings)
        {
            string data = finding.Data.Count == 0 ? "" : " {" + string.Join(", ", finding.Data.Select(d => $"{d.Key}={d.Value}")) + "}";
            text.Line($"{indent}[{finding.Severity}] {finding.Code}: {finding.Detail}{data}");
        }
    }

    private static void WriteOptional(ReportText text, string label, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            text.Detail(label, value);
    }

    private static string Size(FormInfo form) => Inv($"{form.WidthMicrometers} x {form.HeightMicrometers} µm");

    private static string Join(IReadOnlyList<string> values) => values.Count == 0 ? "(none)" : string.Join("; ", values);

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string Inv(FormattableString text) => FormattableString.Invariant(text);

    /// <summary>Line-oriented text with aligned labels.</summary>
    private sealed class ReportText
    {
        private readonly StringBuilder _builder = new();

        public void Section(string title)
        {
            _builder.AppendLine();
            _builder.Append("== ").Append(title).AppendLine(" ==");
        }

        public void Line(string line) => _builder.AppendLine(line);

        // At least one space after a label as long as the column ("Other user forms").
        public void Field(string label, string value) => _builder.Append(label.PadRight(LabelWidth - 1)).Append(' ').AppendLine(value);

        public void Detail(string label, string value) => _builder.Append("    ").Append((label + ":").PadRight(LabelWidth - 1)).Append(' ').AppendLine(value);

        public override string ToString() => _builder.ToString();
    }
}
