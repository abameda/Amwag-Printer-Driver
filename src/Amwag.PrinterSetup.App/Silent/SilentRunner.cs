using System.Globalization;
using System.Text;
using System.Text.Json;
using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Hosting;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Diagnostics;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Silent;

/// <summary>
/// --silent: runs the flow without windows, writes the report to --report (text, plus JSON next to it) or to
/// %ProgramData%\AMWAG\PrinterSetup\Reports, and returns the flow's exit code.
/// </summary>
internal static class SilentRunner
{
    private static readonly Encoding TextEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    private static readonly Encoding JsonEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Runs with arguments that <see cref="CommandLineParser"/> already validated (no errors).</summary>
    public static int Run(CommandLineOptions options, ILog log)
    {
        FlowEnvironment environment;
        try
        {
            environment = AppBootstrap.Build(options, log);
        }
        catch (ConfigurationException ex)
        {
            log.Error("config.invalid", "The configuration is not valid; nothing was changed.", ex, new Dictionary<string, object?> { ["errors"] = ex.Errors });
            WriteReport(options, log, ConfigurationReport(options, log, ex));
            return ExitCodes.ConfigurationInvalid;
        }
        catch (StartupArgumentException ex)
        {
            log.Error("app.arguments.invalid", ex.Message);
            WriteText(options, log, "The command line is not valid:" + Environment.NewLine + "- " + ex.Message);
            return ExitCodes.InvalidArguments;
        }

        var flow = new SetupFlow(environment, new SilentInteraction(options), new SilentElevationBroker());
        // On a pool thread so no synchronization context can deadlock the blocking wait.
        FlowResult result = Task.Run(() => flow.RunAsync(new LogProgress(log), CancellationToken.None)).GetAwaiter().GetResult();
        if (result.Report is { } report)
            WriteReport(options, log, report);
        else
            WriteText(options, log, Summary(result));

        log.Info("silent.finished", "Silent run finished: " + result.Outcome + ".", new Dictionary<string, object?>
        {
            ["outcome"] = result.Outcome.ToString(),
            ["exitCode"] = result.ExitCode,
            ["rebootRequired"] = result.RebootRequired,
        });
        return result.ExitCode;
    }

    /// <summary>Writes plain text (help, argument errors) where the report would go.</summary>
    public static void WriteText(CommandLineOptions options, ILog log, string text)
    {
        if (ReportPaths(options, log) is { } paths)
            TryWrite(paths.Text, text, TextEncoding, log);
    }

    private static void WriteReport(CommandLineOptions options, ILog log, DiagnosticReport report)
    {
        if (ReportPaths(options, log) is not { } paths)
            return;

        string text = Format(report, DiagnosticReportFormatter.ToText, Summary, log);
        string json = Format(report, DiagnosticReportFormatter.ToJson, r => JsonSerializer.Serialize(r, ConfigJson.Options), log);
        bool textWritten = TryWrite(paths.Text, text, TextEncoding, log);
        bool jsonWritten = TryWrite(paths.Json, json, JsonEncoding, log);
        if (textWritten && jsonWritten)
            log.Info("silent.report.written", "Report written.", new Dictionary<string, object?> { ["text"] = paths.Text, ["json"] = paths.Json });
    }

    private static string Format(DiagnosticReport report, Func<DiagnosticReport, string> format, Func<DiagnosticReport, string> fallback, ILog log)
    {
        try
        {
            return format(report);
        }
        catch (Exception ex)
        {
            log.Error("silent.report.format", "The report formatter failed; writing a simpler form instead.", ex);
        }

        try
        {
            return fallback(report);
        }
        catch (Exception ex)
        {
            log.Error("silent.report.fallback", "The simpler report form failed too.", ex);
            return Summary(report);
        }
    }

    private static (string Text, string Json)? ReportPaths(CommandLineOptions options, ILog log)
    {
        if (options.ReportPath is { } path)
        {
            return string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase)
                ? (Path.ChangeExtension(path, ".txt"), path)
                : (path, Path.ChangeExtension(path, ".json"));
        }

        string name = string.Create(CultureInfo.InvariantCulture, $"report-{DateTime.Now:yyyyMMdd-HHmmss}");
        foreach (var (baseDirectory, directory) in AppPaths.ReportLocations)
        {
            if (AppPaths.TryPrepareDirectory(baseDirectory, directory))
                return (Path.Combine(directory, name + ".txt"), Path.Combine(directory, name + ".json"));
        }

        log.Error("silent.report.nowhere", "No folder could be used for the report.");
        return null;
    }

    private static bool TryWrite(string path, string content, Encoding encoding, ILog log)
    {
        try
        {
            if (Path.GetDirectoryName(path) is { Length: > 0 } directory)
                Directory.CreateDirectory(directory);
            File.WriteAllText(path, content, encoding);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            log.Error("silent.report.failed", "The report could not be written.", ex, new Dictionary<string, object?> { ["path"] = path });
            return false;
        }
    }

    private static DiagnosticReport ConfigurationReport(CommandLineOptions options, ILog log, ConfigurationException ex) => new()
    {
        GeneratedAt = DateTimeOffset.Now,
        AppVersion = AppBootstrap.AppVersion,
        SessionId = log.SessionId,
        Mode = options.Mode,
        ConfigSource = ex.Source,
        ConfigVersion = "unknown",
        TicketDescription = "unknown (configuration not loaded)",
        TicketDimensionsConfirmed = false,
        Findings = ex.Errors.Select(e => new Finding(FindingCode.ConfigurationInvalid, Severity.Error, e)).ToArray(),
        LogFile = log.FilePath,
    };

    private static string Summary(FlowResult result) =>
        string.Create(CultureInfo.InvariantCulture, $"Outcome: {result.Outcome}{Environment.NewLine}Exit code: {result.ExitCode}{Environment.NewLine}") +
        FindingLines(result.Findings);

    private static string Summary(DiagnosticReport report) =>
        string.Create(CultureInfo.InvariantCulture, $"AMWAG Printer Setup {report.AppVersion}, session {report.SessionId}, {report.GeneratedAt:O}{Environment.NewLine}") +
        FindingLines(report.Findings);

    private static string FindingLines(IEnumerable<Finding> findings) =>
        string.Concat(findings.Select(f => $"{f.Severity}: {f.Code}: {f.Detail}{Environment.NewLine}"));

    private sealed class LogProgress(ILog log) : IProgress<FlowUpdate>
    {
        public void Report(FlowUpdate value) =>
            log.Debug("silent.progress", value.Progress?.Detail ?? value.Detail ?? value.Kind.ToString(), new Dictionary<string, object?>
            {
                ["kind"] = value.Kind.ToString(),
                ["step"] = value.Progress?.Step.ToString(),
                ["state"] = value.Progress?.State.ToString(),
                ["action"] = value.Progress?.Action?.ToString(),
                ["target"] = value.Target?.Key,
            });
    }
}
