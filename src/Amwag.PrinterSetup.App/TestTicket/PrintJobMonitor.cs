using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Printing;

namespace Amwag.PrinterSetup.App.TestTicket;

public enum JobStage { NotSeen, Spooling, Queued, Printing, Printed, LeftQueue }

/// <summary>Problems the spooler reports for the job or its queue (localized by flag name).</summary>
[Flags]
public enum JobProblems
{
    None = 0,
    Offline = 1 << 0,
    PaperOut = 1 << 1,
    DoorOpen = 1 << 2,
    PaperJam = 1 << 3,
    NoToner = 1 << 4,
    NeedsAttention = 1 << 5,
    JobError = 1 << 6,
    PrinterError = 1 << 7,
    Blocked = 1 << 8,
    Deleted = 1 << 9,
}

public sealed record JobObservation(JobStage Stage, JobProblems Problems, int? JobId, string Detail);

public enum JobMonitorOutcome
{
    /// <summary>The job left the queue without an error. Windows handed it to the printer; nothing more is known.</summary>
    SentToPrinter,
    /// <summary>The spooler reported JOB_STATUS_PRINTED (the port monitor says the printer received every page).</summary>
    PrinterReportedPrinted,
    Problem,
    /// <summary>Still in the queue when the watch ended.</summary>
    StillQueued,
}

public sealed record JobMonitorResult(JobMonitorOutcome Outcome, JobProblems Problems, int? JobId, string Detail);

/// <summary>
/// Watches one spooled document until it leaves the queue (or the watch times out), reporting live stages and problems.
/// It never claims that paper came out of the printer: the best it can see is the spooler's "printed" flag.
/// </summary>
public sealed class PrintJobMonitor
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    /// <summary>A job not seen within this time is taken as already handed over (fast or direct printing).</summary>
    public static readonly TimeSpan NotSeenGrace = TimeSpan.FromSeconds(6);

    private const JobStatusFlags JobProblemFlags = JobStatusFlags.Error | JobStatusFlags.Offline | JobStatusFlags.PaperOut |
        JobStatusFlags.UserIntervention | JobStatusFlags.BlockedDevq | JobStatusFlags.Deleted;

    private readonly IPrintSpooler _spooler;
    private readonly IClock _clock;
    private readonly ILog _log;

    public PrintJobMonitor(IPrintSpooler spooler, IClock clock, ILog log)
    {
        _spooler = spooler;
        _clock = clock;
        _log = log;
    }

    public JobMonitorResult Watch(string queueName, string documentName, IProgress<JobObservation>? progress, CancellationToken cancellationToken) =>
        Watch(queueName, documentName, DefaultTimeout, progress, cancellationToken);

    public JobMonitorResult Watch(string queueName, string documentName, TimeSpan timeout, IProgress<JobObservation>? progress, CancellationToken cancellationToken)
    {
        DateTimeOffset started = _clock.Now;
        PrintJobInfo? lastJob = null;
        JobObservation? last = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TimeSpan elapsed = _clock.Now - started;

            PrintJobInfo? job = TryFindJob(queueName, documentName);
            JobProblems queueProblems = ReadQueueProblems(queueName);

            if (job is null && lastJob is not null)
                return Finish(LeftQueue(lastJob, queueProblems), queueName);

            if (job is null && elapsed >= NotSeenGrace)
                return Finish(new JobMonitorResult(JobMonitorOutcome.SentToPrinter, queueProblems, null, "The job was not seen in the queue; the spooler accepted it and it was handed over before the first check."), queueName);

            if (job is not null && (job.Status & JobStatusFlags.Printed) != 0)
                return Finish(new JobMonitorResult(JobMonitorOutcome.PrinterReportedPrinted, queueProblems, job.JobId, Describe(job)), queueName);

            JobObservation observation = job is null
                ? new JobObservation(JobStage.NotSeen, queueProblems, null, "Waiting for the job to appear in the queue.")
                : new JobObservation(StageOf(job), ProblemsOf(job.Status) | queueProblems, job.JobId, Describe(job));

            if (observation != last)
            {
                progress?.Report(observation);
                _log.Debug("testprint.job.observed", "Test ticket job state changed.", new Dictionary<string, object?>
                {
                    ["queue"] = queueName,
                    ["stage"] = observation.Stage.ToString(),
                    ["problems"] = observation.Problems.ToString(),
                    ["jobId"] = observation.JobId,
                    ["detail"] = observation.Detail,
                });
                last = observation;
            }

            lastJob = job ?? lastJob;

            if (elapsed >= timeout)
            {
                return Finish(observation.Problems != JobProblems.None
                    ? new JobMonitorResult(JobMonitorOutcome.Problem, observation.Problems, observation.JobId, observation.Detail)
                    : new JobMonitorResult(JobMonitorOutcome.StillQueued, JobProblems.None, observation.JobId, observation.Detail), queueName);
            }

            _clock.Sleep(PollInterval, cancellationToken);
        }
    }

    private static JobMonitorResult LeftQueue(PrintJobInfo lastJob, JobProblems queueProblems)
    {
        // The job is gone; what it last reported decides between a clean hand-over and a problem.
        if ((lastJob.Status & JobStatusFlags.Printed) != 0)
            return new JobMonitorResult(JobMonitorOutcome.PrinterReportedPrinted, queueProblems, lastJob.JobId, Describe(lastJob));

        JobProblems fatal = ProblemsOf(lastJob.Status) & (JobProblems.JobError | JobProblems.Deleted);
        return fatal != JobProblems.None
            ? new JobMonitorResult(JobMonitorOutcome.Problem, fatal | queueProblems, lastJob.JobId, Describe(lastJob))
            : new JobMonitorResult(JobMonitorOutcome.SentToPrinter, queueProblems, lastJob.JobId, "The job left the queue without an error.");
    }

    private JobMonitorResult Finish(JobMonitorResult result, string queueName)
    {
        _log.Info("testprint.job.result", "Test ticket job watch finished.", new Dictionary<string, object?>
        {
            ["queue"] = queueName,
            ["outcome"] = result.Outcome.ToString(),
            ["problems"] = result.Problems.ToString(),
            ["jobId"] = result.JobId,
            ["detail"] = result.Detail,
        });
        return result;
    }

    private PrintJobInfo? TryFindJob(string queueName, string documentName)
    {
        try
        {
            return _spooler.GetJobs(queueName)
                .Where(j => string.Equals(j.Document, documentName, StringComparison.Ordinal))
                .OrderByDescending(j => j.JobId)
                .FirstOrDefault();
        }
        catch (PrinterSetupException ex)
        {
            // A transient spooler failure must not end the watch; the next poll tries again.
            _log.Warn("testprint.job.read_failed", "Could not read the print queue's jobs.", new Dictionary<string, object?> { ["queue"] = queueName }, ex);
            return null;
        }
    }

    private JobProblems ReadQueueProblems(string queueName)
    {
        PrintQueueInfo? queue;
        try
        {
            queue = _spooler.GetQueues().FirstOrDefault(q => string.Equals(q.Name, queueName, StringComparison.OrdinalIgnoreCase));
        }
        catch (PrinterSetupException ex)
        {
            _log.Warn("testprint.queue.read_failed", "Could not read the print queue status.", new Dictionary<string, object?> { ["queue"] = queueName }, ex);
            return JobProblems.None;
        }

        if (queue is null)
            return JobProblems.None;

        var status = queue.Status;
        JobProblems problems = JobProblems.None;
        if ((status & PrinterStatusFlags.Offline) != 0 || queue.IsWorkOffline) problems |= JobProblems.Offline;
        if ((status & PrinterStatusFlags.PaperOut) != 0) problems |= JobProblems.PaperOut;
        if ((status & PrinterStatusFlags.DoorOpen) != 0) problems |= JobProblems.DoorOpen;
        if ((status & PrinterStatusFlags.PaperJam) != 0) problems |= JobProblems.PaperJam;
        if ((status & PrinterStatusFlags.NoToner) != 0) problems |= JobProblems.NoToner;
        if ((status & PrinterStatusFlags.UserIntervention) != 0) problems |= JobProblems.NeedsAttention;
        if ((status & PrinterStatusFlags.Error) != 0) problems |= JobProblems.PrinterError;
        return problems;
    }

    private static JobStage StageOf(PrintJobInfo job) =>
        (job.Status & JobStatusFlags.Printing) != 0 ? JobStage.Printing
        : (job.Status & JobStatusFlags.Spooling) != 0 ? JobStage.Spooling
        : JobStage.Queued;

    private static JobProblems ProblemsOf(JobStatusFlags status)
    {
        if ((status & JobProblemFlags) == 0)
            return JobProblems.None;

        JobProblems problems = JobProblems.None;
        if ((status & JobStatusFlags.Error) != 0) problems |= JobProblems.JobError;
        if ((status & JobStatusFlags.Offline) != 0) problems |= JobProblems.Offline;
        if ((status & JobStatusFlags.PaperOut) != 0) problems |= JobProblems.PaperOut;
        if ((status & JobStatusFlags.UserIntervention) != 0) problems |= JobProblems.NeedsAttention;
        if ((status & JobStatusFlags.BlockedDevq) != 0) problems |= JobProblems.Blocked;
        if ((status & JobStatusFlags.Deleted) != 0) problems |= JobProblems.Deleted;
        return problems;
    }

    private static string Describe(PrintJobInfo job) =>
        FormattableString.Invariant($"Job {job.JobId}: status {job.Status} ({job.StatusText ?? "no status text"}), {job.PagesPrinted}/{job.TotalPages} pages.");
}
