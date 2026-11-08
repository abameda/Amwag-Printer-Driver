using System.Globalization;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Units;

namespace Amwag.PrinterSetup.App.TestTicket;

/// <summary>A print setting the ticket software's job is compared on.</summary>
public enum TicketSetting { Paper, Orientation, Duplex, Scale, PaperSource }

public enum TicketSettingStatus
{
    /// <summary>The job asked for the AMWAG Ticket default.</summary>
    Matches,
    /// <summary>The job asked for something else: the ticket software overrides the printer default.</summary>
    Differs,
    /// <summary>The job's DEVMODE does not set this field (dmFields), so the driver keeps its own value.</summary>
    NotSpecified,
    /// <summary>What to expect is not known (no multipurpose tray rule for the printer's model).</summary>
    Unknown,
}

/// <summary>
/// A value shown in a result row: a catalog key with its arguments, or (no key) data shown as it is, one line per part
/// (a paper name, then its size).
/// </summary>
public sealed record TicketValue(string? Key, IReadOnlyList<string> Args)
{
    public static TicketValue NotSpecified { get; } = Text("ticketcheck.value.notSpecified");

    public static TicketValue Data(params string[] lines) => new(null, lines);

    public static TicketValue Text(string key, params string[] args) => new(key, args);
}

/// <summary>One compared setting; <see cref="Detail"/> holds the technical values (English) for the log.</summary>
public sealed record TicketSettingRow(TicketSetting Setting, TicketSettingStatus Status, TicketValue Requested, TicketValue Expected, string Detail);

public enum TicketCheckOutcome
{
    /// <summary>A new job appeared and the settings it was submitted with were read and compared.</summary>
    JobCaptured,
    /// <summary>A new job left the queue before its settings could be read (pausing the printer during the check helps).</summary>
    JobGoneBeforeRead,
    /// <summary>A new job stayed in the queue, but Windows never reported the settings it carries.</summary>
    SettingsUnavailable,
    /// <summary>No new job arrived before the check timed out.</summary>
    NoJob,
    /// <summary>The check was stopped.</summary>
    Cancelled,
    /// <summary>The queue's jobs could not be read when the check started (queue removed, spooler stopped).</summary>
    QueueUnreadable,
}

public sealed record TicketCheckResult(TicketCheckOutcome Outcome, string QueueName)
{
    public int? JobId { get; init; }
    public string? Document { get; init; }
    public IReadOnlyList<TicketSettingRow> Rows { get; init; } = [];

    /// <summary>Technical detail (English): the captured DEVMODE, or why nothing could be read.</summary>
    public string Detail { get; init; } = "";

    public bool HasDifferences => Rows.Any(r => r.Status == TicketSettingStatus.Differs);
}

/// <summary>Live state of a running check: how long it has waited, and the new job it is reading, once one appeared.</summary>
public sealed record TicketCheckProgress(TimeSpan Waited, TimeSpan Timeout, int? JobId)
{
    public TimeSpan Remaining => Timeout > Waited ? Timeout - Waited : TimeSpan.Zero;
}

/// <summary>
/// The settings a ticket job should ask for: the configured print preferences and ticket form, with the multipurpose tray
/// resolved against the driver's trays the way the setup engine resolves it (first configured bin id the driver exposes,
/// else a tray with a configured name).
/// </summary>
public sealed record TicketExpectation
{
    public required string FormName { get; init; }

    /// <summary>Null when the configured ticket size is not valid.</summary>
    public PaperDimensions? Size { get; init; }

    public short Orientation { get; init; } = DmOrientation.Portrait;
    public short Duplex { get; init; } = DmDuplex.Simplex;
    public short Scale { get; init; } = 100;

    /// <summary>False under the DriverDefault paper source policy: the paper source is then not compared.</summary>
    public bool SourceManaged { get; init; }

    /// <summary>The multipurpose tray of this driver, when it exposes one the model's rule names.</summary>
    public BinCapability? SourceBin { get; init; }

    /// <summary>The model's configured multipurpose tray ids and names (used when the driver's trays could not be read).</summary>
    public IReadOnlyList<int> SourceIds { get; init; } = [];
    public IReadOnlyList<string> SourceNames { get; init; } = [];

    public static TicketExpectation For(DeploymentConfig config, ModelDefinition? model, IReadOnlyList<BinCapability> bins)
    {
        var preferences = config.PrintPreferences;
        var ticket = config.Ticket;
        var rule = model?.TicketPaperSource;
        var bin = rule is null
            ? null
            : rule.BinIds.Select(id => bins.FirstOrDefault(b => b.Id == id)).FirstOrDefault(b => b is not null)
              ?? bins.FirstOrDefault(b => rule.BinNames.Any(n => string.Equals(b.Name.Trim(), n.Trim(), StringComparison.OrdinalIgnoreCase)));

        return new TicketExpectation
        {
            FormName = ticket.FormName,
            Size = ticket.Width > 0 && ticket.Height > 0 ? ticket.Dimensions : null,
            Orientation = preferences.Orientation == PageOrientationSetting.Landscape ? DmOrientation.Landscape : DmOrientation.Portrait,
            Duplex = preferences.Duplex switch
            {
                DuplexSetting.LongEdge => DmDuplex.Vertical,
                DuplexSetting.ShortEdge => DmDuplex.Horizontal,
                _ => DmDuplex.Simplex,
            },
            Scale = (short)Math.Clamp(preferences.ScalePercent, short.MinValue, short.MaxValue),
            SourceManaged = preferences.PaperSource != PaperSourcePolicy.DriverDefault,
            SourceBin = bin,
            SourceIds = rule?.BinIds ?? [],
            SourceNames = rule?.BinNames ?? [],
        };
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"form '{FormName}'{(Size is { } s ? $" {s.WidthMicrometers} x {s.HeightMicrometers} µm" : "")}, orientation {Orientation}, duplex {Duplex}, scale {Scale}, source {(SourceManaged ? SourceBin is { } bin ? $"{bin.Id} '{bin.Name}'" : $"ids [{string.Join(", ", SourceIds)}] names [{string.Join(", ", SourceNames)}]" : "not managed")}");
}

/// <summary>Compares the DEVMODE a job was submitted with against the <see cref="TicketExpectation"/>, field by field.</summary>
public static class TicketSettingsComparison
{
    /// <summary>A custom size (dmPaperWidth/dmPaperLength) within this many millimeters of the ticket counts as the ticket.</summary>
    public const double SizeToleranceMm = 1.0;

    public static IReadOnlyList<TicketSettingRow> Compare(DevMode job, TicketExpectation expected, DriverCapabilities? capabilities)
    {
        var rows = new List<TicketSettingRow>
        {
            Paper(job, expected, capabilities?.Papers ?? []),
            Orientation(job, expected),
            Duplex(job, expected),
            Scale(job, expected),
        };
        if (expected.SourceManaged)
            rows.Add(Source(job, expected, capabilities?.Bins ?? []));
        return rows;
    }

    private static TicketSettingRow Paper(DevMode job, TicketExpectation expected, IReadOnlyList<PaperCapability> papers)
    {
        string? form = job.Has(DevModeFields.FormName) && job.FormName.Trim().Length > 0 ? job.FormName.Trim() : null;
        short? paperId = job.Has(DevModeFields.PaperSize) ? job.PaperSize : null;
        string? name = form ?? (paperId is { } id ? papers.FirstOrDefault(p => p.Id == id)?.Name.Trim() : null);
        bool hasSize = job.Has(DevModeFields.PaperWidth) && job.Has(DevModeFields.PaperLength) && job.PaperWidth > 0 && job.PaperLength > 0;
        double width = job.PaperWidth / 10.0;
        double length = job.PaperLength / 10.0;

        bool nameMatches = name is not null && string.Equals(name, expected.FormName.Trim(), StringComparison.OrdinalIgnoreCase);
        bool sizeMatches = hasSize && expected.Size is { } size
            && Math.Abs(width - (double)size.WidthMillimeters) <= SizeToleranceMm
            && Math.Abs(length - (double)size.HeightMillimeters) <= SizeToleranceMm;
        string? expectedSize = expected.Size is { } ticket ? Millimeters((double)ticket.WidthMillimeters, (double)ticket.HeightMillimeters) : null;
        // A set size wins over dmPaperSize and dmFormName (the driver prints on that size), so it alone decides.
        bool sizeDecides = hasSize && expectedSize is not null;

        var status = sizeDecides ? (sizeMatches ? TicketSettingStatus.Matches : TicketSettingStatus.Differs)
            : nameMatches ? TicketSettingStatus.Matches
            : name is null && paperId is null && !hasSize ? TicketSettingStatus.NotSpecified
            : TicketSettingStatus.Differs;

        TicketValue requested = (name, hasSize) switch
        {
            ({ } n, true) => TicketValue.Data(n, Millimeters(width, length)),
            ({ } n, false) => TicketValue.Data(n),
            (null, true) => TicketValue.Data(Millimeters(width, length)),
            _ => paperId is { } number ? TicketValue.Text("ticketcheck.value.paperNumber", Number(number)) : TicketValue.NotSpecified,
        };
        var expectedValue = expectedSize is not null ? TicketValue.Data(expected.FormName, expectedSize) : TicketValue.Data(expected.FormName);

        string detail = Invariant($"dmFormName '{job.FormName}'{Unset(job, DevModeFields.FormName)}, dmPaperSize {job.PaperSize}{Unset(job, DevModeFields.PaperSize)}, dmPaperWidth x dmPaperLength {job.PaperWidth} x {job.PaperLength} (0.1 mm){(hasSize ? "" : " (not set)")}; expected '{expected.FormName}'")
            + (sizeDecides ? $" {expectedSize}; the size fields decide" : "");
        return new TicketSettingRow(TicketSetting.Paper, status, requested, expectedValue, detail);
    }

    private static TicketSettingRow Orientation(DevMode job, TicketExpectation expected) =>
        Simple(job, TicketSetting.Orientation, DevModeFields.Orientation, job.Orientation, expected.Orientation, OrientationValue, "dmOrientation");

    private static TicketSettingRow Duplex(DevMode job, TicketExpectation expected) =>
        Simple(job, TicketSetting.Duplex, DevModeFields.Duplex, job.Duplex, expected.Duplex, DuplexValue, "dmDuplex");

    private static TicketSettingRow Scale(DevMode job, TicketExpectation expected) =>
        Simple(job, TicketSetting.Scale, DevModeFields.Scale, job.Scale, expected.Scale, v => TicketValue.Data(Number(v) + "%"), "dmScale");

    private static TicketSettingRow Simple(DevMode job, TicketSetting setting, DevModeFields field, short value, short expected, Func<short, TicketValue> show, string name)
    {
        if (!job.Has(field))
            return new TicketSettingRow(setting, TicketSettingStatus.NotSpecified, TicketValue.NotSpecified, show(expected), Invariant($"{name} not set; expected {expected}"));

        var status = value == expected ? TicketSettingStatus.Matches : TicketSettingStatus.Differs;
        return new TicketSettingRow(setting, status, show(value), show(expected), Invariant($"{name} {value}; expected {expected}"));
    }

    private static TicketSettingRow Source(DevMode job, TicketExpectation expected, IReadOnlyList<BinCapability> bins)
    {
        var expectedValue = expected.SourceBin is { } tray && tray.Name.Trim().Length > 0
            ? TicketValue.Data(tray.Name.Trim())
            : TicketValue.Text("ticketcheck.value.multipurposeTray");
        string expectedDetail = expected.SourceBin is { } bin
            ? Invariant($"bin {bin.Id} '{bin.Name}'")
            : Invariant($"bin ids [{string.Join(", ", expected.SourceIds)}] or names [{string.Join(", ", expected.SourceNames)}]");

        if (!job.Has(DevModeFields.DefaultSource))
            return new TicketSettingRow(TicketSetting.PaperSource, TicketSettingStatus.NotSpecified, TicketValue.NotSpecified, expectedValue, "dmDefaultSource not set; expected " + expectedDetail);

        short source = job.DefaultSource;
        string? sourceName = bins.FirstOrDefault(b => b.Id == source)?.Name.Trim();
        var requested = sourceName is { Length: > 0 } ? TicketValue.Data(sourceName) : TicketValue.Text("ticketcheck.value.trayNumber", Number(source));

        TicketSettingStatus status;
        if (expected.SourceBin is { } resolved)
            status = source == resolved.Id ? TicketSettingStatus.Matches : TicketSettingStatus.Differs;
        else if (expected.SourceIds.Count > 0 || expected.SourceNames.Count > 0)
            status = expected.SourceIds.Contains(source) || (sourceName is not null && expected.SourceNames.Any(n => string.Equals(n.Trim(), sourceName, StringComparison.OrdinalIgnoreCase)))
                ? TicketSettingStatus.Matches
                : TicketSettingStatus.Differs;
        else
            status = TicketSettingStatus.Unknown;

        return new TicketSettingRow(TicketSetting.PaperSource, status, requested, expectedValue, Invariant($"dmDefaultSource {source} '{sourceName}'; expected {expectedDetail}"));
    }

    private static TicketValue OrientationValue(short value) => value switch
    {
        DmOrientation.Portrait => TicketValue.Text("ticketcheck.value.portrait"),
        DmOrientation.Landscape => TicketValue.Text("ticketcheck.value.landscape"),
        _ => TicketValue.Text("ticketcheck.value.number", Number(value)),
    };

    private static TicketValue DuplexValue(short value) => value switch
    {
        DmDuplex.Simplex => TicketValue.Text("ticketcheck.value.simplex"),
        DmDuplex.Vertical => TicketValue.Text("ticketcheck.value.duplexLong"),
        DmDuplex.Horizontal => TicketValue.Text("ticketcheck.value.duplexShort"),
        _ => TicketValue.Text("ticketcheck.value.number", Number(value)),
    };

    private static string Unset(DevMode job, DevModeFields field) => job.Has(field) ? "" : " (not set)";

    private static string Millimeters(double width, double length) => Invariant($"{width:0.##} x {length:0.##} mm");

    private static string Number(short value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}

/// <summary>
/// "Check ticket software": while it runs, IT prints one real ticket from the ticket software. It records the jobs already
/// in the queue, polls the queue, reads the DEVMODE of the first new job (GetJob level 2: the settings Windows received from
/// the program) and compares it with the AMWAG Ticket defaults. It only reads: the job list, that DEVMODE and the driver's
/// paper and tray lists. Nothing is changed; the job prints normally.
/// </summary>
public sealed class TicketSoftwareCheck
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    private readonly string _queueName;
    private readonly DeploymentConfig _config;
    private readonly ModelDefinition? _model;
    private readonly IPrintSpooler _spooler;
    private readonly ILog _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private bool _jobReadFailureLogged;
    private bool _devModeReadFailureLogged;

    /// <param name="model">The printer's model, for its multipurpose tray rule; null when unknown (the tray is then not judged).</param>
    /// <param name="delay">Waits between polls (tests replace it); <see cref="Task.Delay(TimeSpan, CancellationToken)"/> by default.</param>
    /// <param name="timeout">How long to wait for a job, counted in poll intervals; <see cref="DefaultTimeout"/> by default.</param>
    public TicketSoftwareCheck(string queueName, DeploymentConfig config, ModelDefinition? model, IPrintSpooler spooler, ILog log,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        _queueName = queueName;
        _config = config;
        _model = model;
        _spooler = spooler;
        _log = log;
        _delay = delay ?? Task.Delay;
        Timeout = timeout ?? DefaultTimeout;
    }

    public TimeSpan Timeout { get; }

    /// <summary>Watches the queue until a new job is read, leaves unread, or the timeout passes. Never throws for spooler problems or cancellation.</summary>
    public async Task<TicketCheckResult> RunAsync(IProgress<TicketCheckProgress>? progress, CancellationToken cancellationToken)
    {
        HashSet<int> existing;
        try
        {
            existing = _spooler.GetJobs(_queueName).Select(j => j.JobId).ToHashSet();
        }
        catch (PrinterSetupException ex)
        {
            _log.Warn("ticketcheck.queue.read_failed", "The print queue's jobs could not be read when the ticket software check started.", Data(("queue", _queueName)), ex);
            return Finish(new TicketCheckResult(TicketCheckOutcome.QueueUnreadable, _queueName) { Detail = "EnumJobs failed: " + ex.Message });
        }

        var capabilities = ReadCapabilities();
        var expected = TicketExpectation.For(_config, _model, capabilities?.Bins ?? []);
        _log.Info("ticketcheck.started", "Ticket software check started; waiting for a new print job.", Data(
            ("queue", _queueName), ("model", _model?.Id), ("jobsAlreadyQueued", existing.Count), ("expected", expected.ToString()),
            ("timeoutSeconds", (int)Timeout.TotalSeconds)));

        var waiting = new SortedDictionary<int, PrintJobInfo>(); // new jobs whose settings have not been read yet
        PrintJobInfo? gone = null;
        TimeSpan waited = TimeSpan.Zero;
        TicketCheckProgress? reported = null;

        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
                return Finish(new TicketCheckResult(TicketCheckOutcome.Cancelled, _queueName) { Detail = "Stopped by the user." });

            if (TryGetJobs() is { } jobs)
            {
                var queued = jobs.GroupBy(j => j.JobId).ToDictionary(g => g.Key, g => g.First());
                foreach (var job in queued.Values.Where(j => !existing.Contains(j.JobId)))
                {
                    if (waiting.TryAdd(job.JobId, job))
                        _log.Debug("ticketcheck.job.seen", "A new print job appeared in the queue.", Data(("queue", _queueName), ("jobId", job.JobId), ("document", job.Document)));
                }

                foreach (var (id, job) in waiting.ToList())
                {
                    if (!queued.TryGetValue(id, out var current))
                    {
                        // Its settings were never readable while it was queued.
                        gone ??= job;
                        waiting.Remove(id);
                        continue;
                    }

                    if (TryGetJobDevMode(id) is { } devMode)
                        return Finish(Captured(current, devMode, expected, capabilities));
                }

                if (gone is not null && waiting.Count == 0)
                {
                    return Finish(new TicketCheckResult(TicketCheckOutcome.JobGoneBeforeRead, _queueName)
                    {
                        JobId = gone.JobId,
                        Document = gone.Document,
                        Detail = Invariant($"Job {gone.JobId} left the queue before GetJob level 2 returned its DEVMODE."),
                    });
                }
            }

            if (waited >= Timeout)
            {
                if (waiting.Count > 0)
                {
                    var job = waiting.Values.First();
                    return Finish(new TicketCheckResult(TicketCheckOutcome.SettingsUnavailable, _queueName)
                    {
                        JobId = job.JobId,
                        Document = job.Document,
                        Detail = Invariant($"Job {job.JobId} stayed queued, but GetJob level 2 returned no DEVMODE for it."),
                    });
                }

                return Finish(new TicketCheckResult(TicketCheckOutcome.NoJob, _queueName) { Detail = Invariant($"No new job within {Timeout.TotalSeconds:0} s.") });
            }

            var now = new TicketCheckProgress(waited, Timeout, waiting.Count > 0 ? waiting.Keys.First() : null);
            if (reported is null || (int)reported.Remaining.TotalSeconds != (int)now.Remaining.TotalSeconds || reported.JobId != now.JobId)
            {
                progress?.Report(now);
                reported = now;
            }

            try
            {
                await _delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Finish(new TicketCheckResult(TicketCheckOutcome.Cancelled, _queueName) { Detail = "Stopped by the user." });
            }

            waited += PollInterval;
        }
    }

    private TicketCheckResult Captured(PrintJobInfo job, DevMode devMode, TicketExpectation expected, DriverCapabilities? capabilities) =>
        new(TicketCheckOutcome.JobCaptured, _queueName)
        {
            JobId = job.JobId,
            Document = job.Document,
            Rows = TicketSettingsComparison.Compare(devMode, expected, capabilities),
            Detail = Invariant($"Job {job.JobId} DEVMODE: dmFields 0x{(uint)devMode.Fields:X8}, form '{devMode.FormName}', paper {devMode.PaperSize}, {devMode.PaperWidth} x {devMode.PaperLength} (0.1 mm), orientation {devMode.Orientation}, duplex {devMode.Duplex}, scale {devMode.Scale}, source {devMode.DefaultSource}."),
        };

    private TicketCheckResult Finish(TicketCheckResult result)
    {
        _log.Info("ticketcheck.result", "Ticket software check finished.", Data(
            ("queue", _queueName),
            ("outcome", result.Outcome.ToString()),
            ("jobId", result.JobId),
            ("document", result.Document),
            ("differences", result.Rows.Where(r => r.Status == TicketSettingStatus.Differs).Select(r => r.Setting.ToString()).ToArray()),
            ("settings", result.Rows.Select(r => Invariant($"{r.Setting}: {r.Status} ({r.Detail})")).ToArray()),
            ("detail", result.Detail)));
        return result;
    }

    private DriverCapabilities? ReadCapabilities()
    {
        try
        {
            return _spooler.GetCapabilities(_queueName);
        }
        catch (PrinterSetupException ex)
        {
            // Without the driver's lists the paper and tray are judged by name and configured tray ids only.
            _log.Warn("ticketcheck.capabilities.read_failed", "The driver's paper and tray lists could not be read.", Data(("queue", _queueName)), ex);
            return null;
        }
    }

    private IReadOnlyList<PrintJobInfo>? TryGetJobs()
    {
        try
        {
            return _spooler.GetJobs(_queueName);
        }
        catch (PrinterSetupException ex)
        {
            // A transient spooler failure must not end the check; the next poll tries again.
            if (!_jobReadFailureLogged)
                _log.Warn("ticketcheck.jobs.read_failed", "Could not read the print queue's jobs.", Data(("queue", _queueName)), ex);
            _jobReadFailureLogged = true;
            return null;
        }
    }

    private DevMode? TryGetJobDevMode(int jobId)
    {
        try
        {
            return _spooler.GetJobDevMode(_queueName, jobId);
        }
        catch (Exception ex) when (ex is PrinterSetupException or ArgumentException)
        {
            // An unreadable or malformed DEVMODE is retried while the job stays queued.
            if (!_devModeReadFailureLogged)
                _log.Warn("ticketcheck.job.devmode_failed", "Could not read the settings of a new print job.", Data(("queue", _queueName), ("jobId", jobId)), ex);
            _devModeReadFailureLogged = true;
            return null;
        }
    }

    private static Dictionary<string, object?> Data(params (string Key, object? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value);

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
