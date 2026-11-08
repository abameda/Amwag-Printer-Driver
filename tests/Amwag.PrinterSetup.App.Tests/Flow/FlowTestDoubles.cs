using Amwag.PrinterSetup.App.Elevation;
using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.App.Tests.Flow;

/// <summary>Answers the flow's questions from scripted functions; a question without a script fails the test.</summary>
internal sealed class ScriptedInteraction : ISetupInteraction
{
    private readonly List<string> _calls = [];

    public Func<IReadOnlyList<PrinterCandidate>, PrinterCandidate?>? Choose { get; init; }
    public Func<PrinterCandidate, bool>? Confirm { get; init; }
    public Func<NetworkAddressPrompt, string?>? Address { get; init; }
    public Func<PrinterCandidate, IReadOnlyList<ModelDefinition>, ModelDefinition?>? ChooseModel { get; init; }

    /// <summary>IT's model for a printer (--address, --model, --confirm-model); not a question, so not a call.</summary>
    public Func<PrinterCandidate, IReadOnlyList<ModelDefinition>, ModelDefinition?>? Asserted { get; init; }

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_calls)
                return [.. _calls];
        }
    }

    public List<NetworkAddressPrompt> Prompts { get; } = [];

    public Task<PrinterCandidate?> ChooseCandidateAsync(IReadOnlyList<PrinterCandidate> candidates, CancellationToken cancellationToken) =>
        Task.FromResult(Script("choose", Choose)(candidates));

    public Task<bool> ConfirmProbableMatchAsync(PrinterCandidate candidate, CancellationToken cancellationToken) =>
        Task.FromResult(Script("confirm", Confirm)(candidate));

    public Task<string?> RequestNetworkAddressAsync(NetworkAddressPrompt prompt, CancellationToken cancellationToken)
    {
        Prompts.Add(prompt);
        return Task.FromResult(Script("address", Address)(prompt));
    }

    public Task<ModelDefinition?> ChooseModelForUnidentifiedPrinterAsync(PrinterCandidate candidate, IReadOnlyList<ModelDefinition> models, CancellationToken cancellationToken) =>
        Task.FromResult(Script("model", ChooseModel)(candidate, models));

    public ModelDefinition? AssertedModel(PrinterCandidate candidate, IReadOnlyList<ModelDefinition> models) => Asserted?.Invoke(candidate, models);

    private T Script<T>(string name, T? script)
        where T : Delegate
    {
        lock (_calls)
            _calls.Add(name);
        return script ?? throw new Xunit.Sdk.XunitException($"The flow asked an unexpected question: {name}.");
    }
}

/// <summary>
/// A non-elevated broker whose "worker" runs <see cref="WorkerHost.Apply"/> in-process over the same fakes, as the elevated
/// worker would; or answers with a scripted outcome (declined, failed) without touching anything.
/// </summary>
internal sealed class InProcessBroker(FlowEnvironment environment) : IElevationBroker
{
    private readonly List<WorkerRequest> _requests = [];

    public bool IsElevated { get; init; }

    public ElevatedRunResult? Answer { get; init; }

    public IReadOnlyList<WorkerRequest> Requests
    {
        get
        {
            lock (_requests)
                return [.. _requests];
        }
    }

    public Task<ElevatedRunResult> RunMachineStepsAsync(WorkerRequest request, IProgress<ProgressUpdate> progress, CancellationToken cancellationToken)
    {
        lock (_requests)
            _requests.Add(request);
        if (Answer is not null)
            return Task.FromResult(Answer);

        var workerEnvironment = environment with
        {
            Options = environment.Options with { Mode = request.Mode, AllowUnconfirmedDimensions = request.AllowUnconfirmedDimensions },
        };
        WorkerOutcome outcome = WorkerHost.Apply(workerEnvironment, request.Target, environment.Options.PreferredAddress, progress);
        return Task.FromResult(new ElevatedRunResult(ElevationOutcome.Completed, outcome.ExitCode) { Result = outcome.Result });
    }
}

/// <summary>Collects flow updates synchronously, in order.</summary>
internal sealed class UpdateCollector : IProgress<FlowUpdate>
{
    private readonly List<FlowUpdate> _updates = [];

    public IReadOnlyList<FlowUpdate> Updates
    {
        get
        {
            lock (_updates)
                return [.. _updates];
        }
    }

    public IEnumerable<FlowUpdate> OfKind(FlowEventKind kind) => Updates.Where(u => u.Kind == kind);

    /// <summary>The last state reported for each step.</summary>
    public IReadOnlyDictionary<StepId, StepState> FinalStates() =>
        Updates.Where(u => u.Kind == FlowEventKind.Step && u.Progress is not null)
            .GroupBy(u => u.Progress!.Step)
            .ToDictionary(g => g.Key, g => g.Last().Progress!.State);

    public void Report(FlowUpdate value)
    {
        lock (_updates)
            _updates.Add(value);
    }
}

internal static class FlowTestExtensions
{
    public static FlowEnvironment Environment(this Scenario scenario, SetupMode mode = SetupMode.Setup, Func<SetupOptions, SetupOptions>? edit = null)
    {
        SetupOptions options = scenario.Options with { Mode = mode };
        return new FlowEnvironment(scenario.Config, scenario.Branch, edit is null ? options : edit(options), scenario.Services);
    }

    /// <summary>No machine-wide change of any kind reached the fakes.</summary>
    public static void AssertNothingChanged(this Scenario scenario)
    {
        Assert.Empty(scenario.Spooler.Writes);
        Assert.Empty(scenario.Installer.Calls);
        Assert.Empty(scenario.Packages.PreparedPackageIds);
    }
}
