using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.TestTicket;
using Amwag.PrinterSetup.App.UI.Views;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Logging;

namespace Amwag.PrinterSetup.App.UI;

public sealed partial class MainForm
{
    private CancellationTokenSource? _ticketCheckRun;

    /// <summary>The latest ticket software check run (completed when none is running); tests wait for it.</summary>
    internal Task TicketCheckCompletion { get; private set; } = Task.CompletedTask;

    /// <summary>Opens "Check ticket software" for the result's queue and starts watching at once (it only reads).</summary>
    internal void StartTicketCheck(FlowResult result) => OpenTicketCheck(result, watch: true);

    /// <param name="watch">False only opens the view, for tests that drive it themselves.</param>
    internal TicketCheckView? OpenTicketCheck(FlowResult result, bool watch)
    {
        if (result.Verification?.Summary is not { QueueName.Length: > 0 } summary)
            return null;

        string queue = summary.QueueName;
        ModelDefinition? model = result.Verification?.Target?.Model ?? result.Target?.Model;
        TicketCheckView? view = null;
        view = new TicketCheckView(_context, queue, new TicketCheckCommands(
            CheckAgain: () => TicketCheckCompletion = RunTicketCheckAsync(view!, queue, model),
            Back: () =>
            {
                StopTicketCheck();
                ShowResult(result);
            }));
        view.ActionsChanged += (_, _) =>
        {
            if (_view == view)
                BindActions(view);
        };
        ShowView(view);
        if (watch)
            TicketCheckCompletion = RunTicketCheckAsync(view, queue, model);
        return view;
    }

    private void StopTicketCheck()
    {
        _ticketCheckRun?.Cancel();
        _ticketCheckRun = null;
    }

    private async Task RunTicketCheckAsync(TicketCheckView view, string queue, ModelDefinition? model)
    {
        StopTicketCheck();
        var cancellation = new CancellationTokenSource();
        _ticketCheckRun = cancellation;
        var check = new TicketSoftwareCheck(queue, Runtime.Environment.Config, model, Runtime.Environment.Services.Spooler, Runtime.Log);
        var progress = new Progress<TicketCheckProgress>(update =>
        {
            if (!cancellation.IsCancellationRequested && view.Stage == TicketCheckStage.Watching)
                view.ShowWatching(update);
        });
        view.ShowWatching(null);

        try
        {
            var result = await Task.Run(() => check.RunAsync(progress, cancellation.Token));
            if (!cancellation.IsCancellationRequested)
                view.ShowResult(result);
        }
        catch (Exception ex)
        {
            Runtime.Log.Error("ticketcheck.failed", "The ticket software check failed unexpectedly.", ex, new Dictionary<string, object?> { ["queue"] = queue });
            if (!cancellation.IsCancellationRequested)
                view.ShowFailed(ex.GetType().Name + ": " + ex.Message);
        }
        finally
        {
            if (_ticketCheckRun == cancellation)
                _ticketCheckRun = null;
            cancellation.Dispose();
        }
    }
}
