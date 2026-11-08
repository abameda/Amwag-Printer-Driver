using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.TestTicket;
using Amwag.PrinterSetup.App.UI.Views;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.UI;

public sealed partial class MainForm
{
    private CancellationTokenSource? _testPrintRun;
    private JobMonitorResult? _testPrintResult;

    internal void StartTestPrint(FlowResult result)
    {
        if (result.Verification?.Summary is not { QueueName.Length: > 0 } summary)
            return;

        _testPrintResult = null;
        TestPrintView? view = null;
        view = new TestPrintView(_context, new TestPrintCommands(
            Print: skipPrecheck => _ = PrintTestTicketAsync(view!, summary, skipPrecheck),
            // Repairs the printer that was just tested, never another supported printer.
            Repair: () =>
            {
                StopTestPrint();
                _ = RunFlowAsync(SetupMode.Repair, RepairTargetOf(result));
            },
            Back: () =>
            {
                StopTestPrint();
                ShowResult(result);
            },
            Answer: printedCorrectly => RecordPhysicalCheck(view!, summary, printedCorrectly)));
        view.ActionsChanged += (_, _) =>
        {
            if (_view == view)
                BindActions(view);
        };
        ShowView(view);
    }

    private void StopTestPrint()
    {
        _testPrintRun?.Cancel();
        _testPrintRun = null;
    }

    private async Task PrintTestTicketAsync(TestPrintView view, ConfiguredSummary summary, bool skipPrecheck)
    {
        StopTestPrint();
        var cancellation = new CancellationTokenSource();
        _testPrintRun = cancellation;
        var services = Runtime.Environment.Services;
        var printer = new TestTicketPrinter(services.Spooler, Runtime.Environment.Config.Ticket, Runtime.Log);
        string queue = summary.QueueName;

        try
        {
            if (!skipPrecheck)
            {
                var precheck = await Task.Run(() => printer.Precheck(queue));
                if (cancellation.IsCancellationRequested)
                    return;
                if (!precheck.FormMatches)
                {
                    view.ShowPrecheckWarning(precheck);
                    return;
                }
            }

            view.ShowWorking(null);
            var submission = await Task.Run(() => printer.Print(queue, summary.ModelName, services.Clock.Now));
            var monitor = new PrintJobMonitor(services.Spooler, services.Clock, Runtime.Log);
            var progress = new Progress<JobObservation>(observation =>
            {
                if (!cancellation.IsCancellationRequested)
                    view.ShowWorking(observation);
            });
            var outcome = await Task.Run(() => monitor.Watch(queue, submission.DocumentName, progress, cancellation.Token));
            if (cancellation.IsCancellationRequested)
                return;

            _testPrintResult = outcome;
            view.ShowFinished(submission, outcome);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Runtime.Log.Error("testprint.failed", "The test ticket could not be printed.", ex, new Dictionary<string, object?> { ["queue"] = queue });
            if (!cancellation.IsCancellationRequested)
                view.ShowFailed(ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (_testPrintRun == cancellation)
                _testPrintRun = null;
            cancellation.Dispose();
        }
    }

    private void RecordPhysicalCheck(TestPrintView view, ConfiguredSummary summary, bool printedCorrectly)
    {
        Runtime.Log.Info("testprint.physical_confirmation",
            printedCorrectly ? "The employee confirmed the test ticket printed correctly." : "The employee reported a problem with the test ticket.",
            new Dictionary<string, object?>
            {
                ["queue"] = summary.QueueName,
                ["model"] = summary.ModelName,
                ["jobResult"] = _testPrintResult?.Outcome.ToString(),
                ["jobProblems"] = _testPrintResult?.Problems.ToString(),
                ["jobId"] = _testPrintResult?.JobId,
                ["answer"] = printedCorrectly ? "printed_correctly" : "problem",
            });
        view.ShowAnswer(printedCorrectly);
    }
}
