using Amwag.PrinterSetup.App.TestTicket;
using Amwag.PrinterSetup.App.Tests.UI;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Printing;

namespace Amwag.PrinterSetup.App.Tests.TestTicket;

public sealed class PrintJobMonitorTests
{
    private const string Queue = "AMWAG Ticket Printer";
    private const string Document = "AMWAG Test Ticket 2026-11-03 14:05:00";

    private readonly StubSpooler _spooler = new();
    private readonly ManualClock _clock = new();
    private readonly MemoryLog _log = new();

    private PrintJobMonitor Monitor => new(_spooler, _clock, _log);

    private static PrintJobInfo Job(JobStatusFlags status, string document = Document) =>
        new() { JobId = 7, Document = document, Status = status, TotalPages = 1 };

    /// <summary>Plays one job state per poll; null means the job is not (or no longer) in the queue.</summary>
    private void Script(params PrintJobInfo?[] polls)
    {
        int poll = 0;
        _spooler.Jobs = _ =>
        {
            var job = polls[Math.Min(poll++, polls.Length - 1)];
            return job is null ? [] : [job];
        };
    }

    [Fact]
    public void Job_that_spools_prints_and_leaves_is_sent_to_the_printer()
    {
        Script(Job(JobStatusFlags.Spooling), Job(JobStatusFlags.Printing), null);
        var seen = new List<JobObservation>();

        var result = Monitor.Watch(Queue, Document, new SynchronousProgress<JobObservation>(seen.Add), CancellationToken.None);

        Assert.Equal(JobMonitorOutcome.SentToPrinter, result.Outcome);
        Assert.Equal(7, result.JobId);
        Assert.Equal([JobStage.Spooling, JobStage.Printing], seen.Select(o => o.Stage));
        Assert.Contains(_log.Entries, e => e.Event == "testprint.job.result");
    }

    [Fact]
    public void Printed_flag_is_reported_as_printer_reported_printed()
    {
        Script(Job(JobStatusFlags.Printing), Job(JobStatusFlags.Printed | JobStatusFlags.Complete));

        var result = Monitor.Watch(Queue, Document, null, CancellationToken.None);

        Assert.Equal(JobMonitorOutcome.PrinterReportedPrinted, result.Outcome);
    }

    [Fact]
    public void Job_that_had_an_error_when_it_left_is_a_problem()
    {
        Script(Job(JobStatusFlags.Printing | JobStatusFlags.Error), null);

        var result = Monitor.Watch(Queue, Document, null, CancellationToken.None);

        Assert.Equal(JobMonitorOutcome.Problem, result.Outcome);
        Assert.True(result.Problems.HasFlag(JobProblems.JobError));
    }

    [Fact]
    public void Paper_out_until_the_timeout_is_a_problem()
    {
        Script(Job(JobStatusFlags.PaperOut | JobStatusFlags.UserIntervention));

        var result = Monitor.Watch(Queue, Document, TimeSpan.FromSeconds(10), null, CancellationToken.None);

        Assert.Equal(JobMonitorOutcome.Problem, result.Outcome);
        Assert.True(result.Problems.HasFlag(JobProblems.PaperOut));
        Assert.True(result.Problems.HasFlag(JobProblems.NeedsAttention));
    }

    [Fact]
    public void Queued_job_without_problems_until_the_timeout_is_still_queued()
    {
        Script(Job(JobStatusFlags.None));

        var result = Monitor.Watch(Queue, Document, TimeSpan.FromSeconds(10), null, CancellationToken.None);

        Assert.Equal(JobMonitorOutcome.StillQueued, result.Outcome);
        Assert.Equal(20, _clock.Sleeps); // polled every 500 ms for 10 s
    }

    [Fact]
    public void Default_watch_polls_every_half_second_for_90_seconds()
    {
        Script(Job(JobStatusFlags.None));

        Monitor.Watch(Queue, Document, null, CancellationToken.None);

        Assert.Equal(180, _clock.Sleeps);
        Assert.Equal(TimeSpan.FromMilliseconds(500), PrintJobMonitor.PollInterval);
    }

    [Fact]
    public void Offline_queue_is_reported_while_the_job_waits()
    {
        Script(Job(JobStatusFlags.None));
        _spooler.Queues = () => [new PrintQueueInfo { Name = Queue, Status = PrinterStatusFlags.Offline }];
        var seen = new List<JobObservation>();

        var result = Monitor.Watch(Queue, Document, TimeSpan.FromSeconds(2), new SynchronousProgress<JobObservation>(seen.Add), CancellationToken.None);

        Assert.True(seen[0].Problems.HasFlag(JobProblems.Offline));
        Assert.Equal(JobMonitorOutcome.Problem, result.Outcome);
    }

    [Fact]
    public void Job_never_seen_counts_as_handed_over_after_the_grace_period()
    {
        Script((PrintJobInfo?)null);

        var result = Monitor.Watch(Queue, Document, null, CancellationToken.None);

        Assert.Equal(JobMonitorOutcome.SentToPrinter, result.Outcome);
        Assert.Null(result.JobId);
        Assert.Equal((int)(PrintJobMonitor.NotSeenGrace / PrintJobMonitor.PollInterval), _clock.Sleeps);
    }

    [Fact]
    public void Other_documents_are_ignored()
    {
        Script(Job(JobStatusFlags.Printing, "Invoice 42"));

        var result = Monitor.Watch(Queue, Document, null, CancellationToken.None);

        Assert.Equal(JobMonitorOutcome.SentToPrinter, result.Outcome);
        Assert.Null(result.JobId);
    }

    [Fact]
    public void Transient_spooler_errors_do_not_end_the_watch()
    {
        int poll = 0;
        _spooler.Jobs = _ => poll++ switch
        {
            0 => throw new SpoolerException("EnumJobs", 1722, "The RPC server is unavailable."),
            1 => [Job(JobStatusFlags.Printing)],
            _ => [],
        };

        var result = Monitor.Watch(Queue, Document, null, CancellationToken.None);

        Assert.Equal(JobMonitorOutcome.SentToPrinter, result.Outcome);
        Assert.Contains(_log.Entries, e => e.Event == "testprint.job.read_failed");
    }

    [Fact]
    public void Cancellation_stops_the_watch()
    {
        Script(Job(JobStatusFlags.None));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Monitor.Watch(Queue, Document, null, cancellation.Token));
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
