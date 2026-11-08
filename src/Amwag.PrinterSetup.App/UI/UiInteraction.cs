using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.UI;

/// <summary>
/// Answers the flow's questions with views in the main window. The flow may ask from any thread: each question is
/// posted to the UI thread and completed from a view button; cancelling the run cancels a pending question.
/// IT can pre-answer the model questions for one printer with --address, --model and --confirm-model: the model of the
/// printer at that address is then taken from the command line instead of asking (any other printer is still asked about).
/// </summary>
internal sealed class UiInteraction : ISetupInteraction
{
    private readonly MainForm _form;
    private readonly SynchronizationContext _ui;

    public UiInteraction(MainForm form, SynchronizationContext ui)
    {
        _form = form;
        _ui = ui;
    }

    private CommandLineOptions Options => _form.Runtime.CommandLine;

    public Task<PrinterCandidate?> ChooseCandidateAsync(IReadOnlyList<PrinterCandidate> candidates, CancellationToken cancellationToken) =>
        AskAsync<PrinterCandidate?>(answer => _form.AskChoosePrinter(candidates, answer), cancellationToken);

    public Task<bool> ConfirmProbableMatchAsync(PrinterCandidate candidate, CancellationToken cancellationToken)
    {
        if (IsAssertedPrinter(candidate) && string.Equals(candidate.Model?.Id, Options.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            _form.Runtime.Log.Info("ui.confirm_model.from_command_line", "The probable model was confirmed by --model and --confirm-model.", AssertedData(candidate, candidate.Model!.Id));
            return Task.FromResult(true);
        }

        return AskAsync<bool>(answer => _form.AskConfirmModel(candidate, answer), cancellationToken);
    }

    public Task<string?> RequestNetworkAddressAsync(NetworkAddressPrompt prompt, CancellationToken cancellationToken) =>
        AskAsync<string?>(answer => _form.AskNetworkAddress(prompt, answer), cancellationToken);

    public Task<ModelDefinition?> ChooseModelForUnidentifiedPrinterAsync(PrinterCandidate candidate, IReadOnlyList<ModelDefinition> models, CancellationToken cancellationToken)
    {
        if (AssertedModel(candidate, models) is { } model)
        {
            _form.Runtime.Log.Info("ui.choose_model.from_command_line", "The model of the unidentified printer was given by --model and --confirm-model.", AssertedData(candidate, model.Id));
            return Task.FromResult<ModelDefinition?>(model);
        }

        return AskAsync<ModelDefinition?>(answer => _form.AskModel(candidate, models, answer), cancellationToken);
    }

    public ModelDefinition? AssertedModel(PrinterCandidate candidate, IReadOnlyList<ModelDefinition> models) =>
        IsAssertedPrinter(candidate) ? models.FirstOrDefault(m => string.Equals(m.Id, Options.ModelId, StringComparison.OrdinalIgnoreCase)) : null;

    /// <summary>The printer IT named with --address, when IT also gave --model and --confirm-model.</summary>
    private bool IsAssertedPrinter(PrinterCandidate candidate)
    {
        var options = Options;
        if (!options.ConfirmModel || string.IsNullOrEmpty(options.ModelId) || options.Address is not { Length: > 0 } address)
            return false;

        return candidate.IsAtAddress(address);
    }

    private Dictionary<string, object?> AssertedData(PrinterCandidate candidate, string modelId) => new()
    {
        ["target"] = candidate.Key,
        ["address"] = Options.Address,
        ["model"] = modelId,
    };

    /// <param name="show">Runs on the UI thread; gets a callback that completes the question and returns false when it was already over (cancelled).</param>
    private Task<T> AskAsync<T>(Action<Func<T, bool>> show, CancellationToken cancellationToken)
    {
        var answer = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (cancellationToken.IsCancellationRequested)
        {
            answer.SetCanceled(cancellationToken);
            return answer.Task;
        }

        var registration = cancellationToken.Register(() => answer.TrySetCanceled(cancellationToken));
        _ = answer.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try
        {
            _ui.Post(_ =>
            {
                if (answer.Task.IsCompleted)
                    return;
                try
                {
                    show(answer.TrySetResult);
                }
                catch (Exception ex)
                {
                    answer.TrySetException(ex);
                }
            }, null);
        }
        catch (InvalidOperationException)
        {
            // The window is gone (closed while the flow was still asking).
            answer.TrySetCanceled(CancellationToken.None);
        }

        return answer.Task;
    }
}
