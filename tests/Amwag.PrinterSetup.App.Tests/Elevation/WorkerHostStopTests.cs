using System.Diagnostics.CodeAnalysis;
using Amwag.PrinterSetup.App.Elevation;
using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.Hosting;
using Amwag.PrinterSetup.App.Tests.Flow;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.App.Tests.Elevation;

/// <summary>
/// The worker once the window is gone (its WindowGone token), and when it loaded other configuration bytes than the window or
/// bytes that no longer load; over the in-memory fakes.
/// </summary>
public sealed class WorkerHostStopTests
{
    private const string Hash = "5d41402abc4b2a76b9719d911017c592ae7f1f27a6de5bd2b6f5d2a3f2b2c2d1";

    [Fact]
    public void A_window_that_is_already_gone_stops_the_worker_before_it_changes_anything()
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        TargetSelector selector = TargetSelector.For(OnlyCandidate(scenario));
        using var gone = new CancellationTokenSource();
        gone.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => WorkerHost.Apply(scenario.Environment(), selector, null, new Collector(), gone.Token));

        scenario.AssertNothingChanged();
    }

    [Fact]
    public void A_window_that_closes_during_the_apply_stops_the_worker_at_the_next_action_boundary()
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        TargetSelector selector = TargetSelector.For(OnlyCandidate(scenario));
        using var gone = new CancellationTokenSource();
        // The window closes as soon as the first action has finished.
        var progress = new Collector(update =>
        {
            if (update is { Action: not null, Phase: ProgressPhase.Completed })
                gone.Cancel();
        });

        WorkerOutcome outcome = WorkerHost.Apply(scenario.Environment(), selector, null, progress, gone.Token);

        Assert.Equal(ExitCodes.Cancelled, outcome.ExitCode);
        Assert.Contains(outcome.Result.Findings, f => f.Code == FindingCode.Cancelled);
        ActionResult first = outcome.Result.Results[0];
        Assert.NotEqual(ActionOutcome.Skipped, first.Outcome);
        Assert.True(outcome.Result.Results.Count > 1, "the plan has more than one machine-wide action");
        Assert.All(outcome.Result.Results.Skip(1), r =>
        {
            Assert.Equal(ActionOutcome.Skipped, r.Outcome);
            Assert.Equal("Cancelled before it started.", r.Detail);
        });
    }

    [Fact]
    public void The_same_configuration_bytes_as_the_window_are_accepted()
    {
        Assert.Null(WorkerHost.ConfigurationChanged(new ConfigurationPin(Hash, ConfigurationPin.NoBranch), new ConfigurationPin(Hash, ConfigurationPin.NoBranch)));
        Assert.Null(WorkerHost.ConfigurationChanged(
            new ConfigurationPin(ConfigurationPin.Embedded, Hash), new ConfigurationPin(ConfigurationPin.Embedded, Hash)));
    }

    [Theory]
    [InlineData(ConfigurationPin.Embedded, ConfigurationPin.NoBranch)]
    [InlineData("6d41402abc4b2a76b9719d911017c592ae7f1f27a6de5bd2b6f5d2a3f2b2c2d1", ConfigurationPin.NoBranch)]
    [InlineData(Hash, Hash)]
    public void Other_configuration_or_branch_bytes_than_the_window_loaded_are_refused(string config, string branch)
    {
        var loaded = new ConfigurationPin(config, branch);

        Finding? refusal = WorkerHost.ConfigurationChanged(new ConfigurationPin(Hash, ConfigurationPin.NoBranch), loaded);

        Assert.NotNull(refusal);
        // Its own code: the window says "close the program and start it again" instead of "the configuration file is not valid".
        Assert.Equal(FindingCode.ConfigurationChanged, refusal.Code);
        Assert.Equal(Severity.Error, refusal.Severity);
        Assert.Equal("The configuration changed after the window loaded it; nothing was changed. Close the program and start it again.", refusal.Detail);
        Assert.Equal(loaded.Config, refusal.Data["workerConfig"]);
    }

    [Fact]
    public void A_configuration_that_no_longer_loads_is_refused_with_findings_not_an_error_message()
    {
        var log = new MemoryLog();
        var invalid = new ConfigurationException(@"C:\Program Files\AMWAG\amwag-printer-config.json", ["models[0].id is missing"]);

        bool loaded = WorkerHost.TryLoadEnvironment(WorkerOptions(SampleConfig.P4030), EmbeddedPin, () => throw invalid, log,
            out var environment, out WorkerOutcome? refusal);

        Assert.False(loaded);
        Assert.Null(environment);
        AssertRefusal(refusal, FindingCode.ConfigurationChanged);
        Finding finding = refusal.Result.Findings[0];
        Assert.Contains("no longer valid (" + invalid.Message + ")", finding.Detail, StringComparison.Ordinal);
        Assert.EndsWith("Close the program and start it again.", finding.Detail, StringComparison.Ordinal);
        Assert.Equal("models[0].id is missing", finding.Data["errors"]);
        Assert.True(log.Contains("worker.config.invalid"));
    }

    [Fact]
    public void A_target_model_removed_from_the_configuration_meanwhile_is_a_changed_configuration()
    {
        // IT removed the P-4030DN while the window was open: other bytes, and the worker's --model is no longer in them.
        DeploymentConfig edited = SampleConfig.Create() with { Models = [SampleConfig.Model(SampleConfig.P4531)] };
        var log = new MemoryLog();

        bool loaded = WorkerHost.TryLoadEnvironment(WorkerOptions(SampleConfig.P4030), EmbeddedPin, () => Loaded(edited, Hash), log,
            out _, out WorkerOutcome? refusal);

        Assert.False(loaded);
        AssertRefusal(refusal, FindingCode.ConfigurationChanged);
        Assert.Equal(Hash, refusal.Result.Findings[0].Data["workerConfig"]);
        Assert.True(log.Contains("worker.config.changed"));
        Assert.False(log.Contains("worker.arguments.invalid"));
    }

    [Fact]
    public void The_same_bytes_without_the_requested_model_are_refused_as_an_invalid_configuration()
    {
        var log = new MemoryLog();

        bool loaded = WorkerHost.TryLoadEnvironment(WorkerOptions("P-9999DN"), EmbeddedPin, () => Loaded(SampleConfig.Create(), ConfigurationPin.Embedded), log,
            out _, out WorkerOutcome? refusal);

        Assert.False(loaded);
        AssertRefusal(refusal, FindingCode.ConfigurationInvalid);
        Assert.StartsWith("Unknown --model 'P-9999DN'.", refusal.Result.Findings[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_bytes_as_the_window_build_the_workers_environment()
    {
        DeploymentConfig config = SampleConfig.Create();

        bool loaded = WorkerHost.TryLoadEnvironment(WorkerOptions(SampleConfig.P4030), EmbeddedPin, () => Loaded(config, ConfigurationPin.Embedded), new MemoryLog(),
            out var environment, out WorkerOutcome? refusal);

        Assert.True(loaded);
        Assert.Null(refusal);
        Assert.Same(config, environment!.Config);
        Assert.Equal((SetupMode.Repair, SampleConfig.P4030), (environment.Options.Mode, environment.Options.PreferredModelId));
    }

    [Fact]
    public async Task The_window_ends_a_run_whose_worker_refused_a_changed_configuration_as_blocked_without_guessing()
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        var invalid = new ConfigurationException(@"C:\Program Files\AMWAG\amwag-printer-config.json", ["ticket.width must be positive"]);
        Assert.False(WorkerHost.TryLoadEnvironment(WorkerOptions(SampleConfig.P4030), EmbeddedPin, () => throw invalid, new MemoryLog(), out _, out WorkerOutcome? refusal));
        // What the broker hands the flow once the worker sent this refusal over the pipe.
        var broker = new InProcessBroker(scenario.Environment())
        {
            Answer = new ElevatedRunResult(ElevationOutcome.Completed, refusal.ExitCode) { Result = refusal.Result },
        };

        FlowResult result = await new SetupFlow(scenario.Environment(), new ScriptedInteraction(), broker).RunAsync(new UpdateCollector(), CancellationToken.None);

        // Before: the worker sent only an error message, and the run failed with "Not known whether this was applied".
        Assert.Equal(FlowOutcome.Blocked, result.Outcome);
        Assert.Null(result.Verification);
        Assert.Contains(result.Findings, f => f is { Code: FindingCode.ConfigurationChanged, Severity: Severity.Error });
        Assert.DoesNotContain(result.Findings, f => f.Detail.StartsWith("Not known whether", StringComparison.Ordinal));
        scenario.AssertNothingChanged();
    }

    private static readonly ConfigurationPin EmbeddedPin = new(ConfigurationPin.Embedded, ConfigurationPin.NoBranch);

    private static CommandLineOptions WorkerOptions(string modelId) => new() { Role = AppRole.Worker, Mode = SetupMode.Repair, ModelId = modelId };

    private static LoadedConfiguration Loaded(DeploymentConfig config, string configSha256) =>
        new(config, "test", Branch: null, BranchSource: null, Warnings: [], configSha256, BranchSha256: null, IgnoredFiles: []);

    /// <summary>A findings-only refusal: nothing was applied, exit code 6, so the window ends the run blocked instead of failed.</summary>
    private static void AssertRefusal([NotNull] WorkerOutcome? refusal, FindingCode code)
    {
        Assert.NotNull(refusal);
        Assert.Equal(ExitCodes.ConfigurationInvalid, refusal.ExitCode);
        Assert.True(refusal.Result.Refused);
        Assert.Empty(refusal.Result.Results);
        Finding finding = Assert.Single(refusal.Result.Findings);
        Assert.Equal((code, Severity.Error), (finding.Code, finding.Severity));
        Assert.Contains("nothing was changed", finding.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static PrinterCandidate OnlyCandidate(Scenario scenario) =>
        Assert.Single(scenario.CreateSession().Detect(new DetectRequest(), CancellationToken.None).Candidates);

    private sealed class Collector(Action<ProgressUpdate>? onUpdate = null) : IProgress<ProgressUpdate>
    {
        public void Report(ProgressUpdate value) => onUpdate?.Invoke(value);
    }
}
