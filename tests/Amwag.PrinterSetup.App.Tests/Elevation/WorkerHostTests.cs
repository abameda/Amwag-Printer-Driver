using Amwag.PrinterSetup.App.Elevation;
using Amwag.PrinterSetup.App.Hosting;
using Amwag.PrinterSetup.App.Tests.Flow;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.App.Tests.Elevation;

/// <summary>The elevated worker's logic over the in-memory fakes (no pipe, no lock, no elevation).</summary>
public sealed class WorkerHostTests
{
    private readonly List<ProgressUpdate> _progress = [];

    [Fact]
    public void The_worker_applies_only_the_machine_wide_actions()
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        TargetSelector selector = TargetSelector.For(OnlyCandidate(scenario));

        WorkerOutcome outcome = WorkerHost.Apply(scenario.Environment(), selector, null, Progress());

        Assert.Equal(ExitCodes.Ready, outcome.ExitCode);
        Assert.True(outcome.Result.Succeeded);
        Assert.NotEmpty(outcome.Result.Results);
        Assert.All(outcome.Result.Results, r => Assert.True(r.Action.RequiresElevation, r.Action.Kind.ToString()));
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Operation == nameof(IPrintSpooler.SetDevMode) && w.Detail == nameof(DevModeScope.PerUser));
        Assert.NotEmpty(_progress);
    }

    [Fact]
    public void A_target_that_is_gone_changes_nothing()
    {
        Scenario scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();

        WorkerOutcome outcome = WorkerHost.Apply(scenario.Environment(), new TargetSelector(PrinterCandidate.UsbKey(@"USBPRINT\NOT\THERE"), SampleConfig.P4030), null, Progress());

        Assert.Equal(ExitCodes.NotReady, outcome.ExitCode);
        Finding finding = Assert.Single(outcome.Result.Findings);
        Assert.Equal("printer not found or its identity changed", finding.Detail);
        Assert.False(outcome.Result.Succeeded);
        Assert.True(outcome.Result.Refused);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public void A_blocked_plan_is_returned_without_changes()
    {
        Scenario scenario = new ScenarioBuilder().WithTicket(t => t with { DimensionsConfirmed = false }).WithUsbPrinter(driverBound: false).Build();
        TargetSelector selector = TargetSelector.For(OnlyCandidate(scenario));

        WorkerOutcome outcome = WorkerHost.Apply(scenario.Environment(), selector, null, Progress());

        Assert.Equal(ExitCodes.ConfigurationInvalid, outcome.ExitCode);
        Assert.Contains(outcome.Result.Findings, f => f.Code == FindingCode.DimensionsUnconfirmed);
        Assert.False(outcome.Result.Succeeded);
        Assert.True(outcome.Result.Refused);
        scenario.AssertNothingChanged();
    }

    [Fact]
    public void The_spooler_target_starts_a_stopped_spooler()
    {
        Scenario scenario = new ScenarioBuilder().WithSpooler(ServiceRunState.Stopped, ServiceStartMode.Manual).Build();

        WorkerOutcome outcome = WorkerHost.Apply(scenario.Environment(), new TargetSelector(WorkerArguments.SpoolerStartTarget), null, Progress());

        Assert.Equal(ExitCodes.Ready, outcome.ExitCode);
        Assert.Equal(ActionOutcome.Succeeded, Assert.Single(outcome.Result.Results).Outcome);
        Assert.True(scenario.Spooler.ServiceStatus.IsRunning);
        Assert.Equal(StepState.Succeeded, _progress[^1].State);
    }

    [Fact]
    public void The_spooler_target_reports_a_spooler_that_does_not_start()
    {
        Scenario scenario = new ScenarioBuilder().WithSpooler(ServiceRunState.Stopped, ServiceStartMode.Manual).Build();
        scenario.Spooler.StartServiceSucceeds = false;

        WorkerOutcome outcome = WorkerHost.Apply(scenario.Environment(), new TargetSelector(WorkerArguments.SpoolerStartTarget), null, Progress());

        Assert.Equal(ExitCodes.NotReady, outcome.ExitCode);
        ActionResult result = Assert.Single(outcome.Result.Results);
        Assert.Equal(ActionOutcome.Failed, result.Outcome);
        Assert.Equal(FindingCode.SpoolerUnavailable, result.FailureCode);
    }

    [Fact]
    public void The_spooler_target_leaves_a_running_spooler_alone()
    {
        Scenario scenario = new ScenarioBuilder().Build();

        WorkerOutcome outcome = WorkerHost.Apply(scenario.Environment(), new TargetSelector(WorkerArguments.SpoolerStartTarget), null, Progress());

        Assert.Equal(ExitCodes.Ready, outcome.ExitCode);
        Assert.Equal(ActionOutcome.AlreadySatisfied, Assert.Single(outcome.Result.Results).Outcome);
        Assert.Empty(scenario.Spooler.Writes);
    }

    [Fact]
    public void Another_run_holding_the_apply_lock_is_a_refusal_with_its_own_code()
    {
        Finding busy = MachineApplyLock.BusyFinding();

        Assert.Equal((FindingCode.ApplyLockBusy, Severity.Error), (busy.Code, busy.Severity));
        Assert.Equal("Another AMWAG Printer Setup run is applying changes on this computer; nothing was changed. Try again in a few minutes.", busy.Detail);
        Assert.True(new ApplyResult { Findings = [busy] }.Refused);
    }

    [Theory]
    [InlineData("queue:AMWAG Ticket Printer", StepId.InstallDriver)]
    [InlineData(WorkerArguments.SpoolerStartTarget, StepId.Detect)]
    public void The_wait_for_the_apply_lock_tells_the_window_what_it_waits_for(string targetKey, StepId step)
    {
        ProgressUpdate waiting = WorkerHost.WaitingForApplyLock(new TargetSelector(targetKey));

        // The code tells it apart from waits inside an action (Windows creating a queue), which carry that action.
        Assert.Equal((step, StepState.Running, (ActionKind?)null, ProgressPhase.Waiting, (FindingCode?)FindingCode.ApplyLockBusy),
            (waiting.Step, waiting.State, waiting.Action, waiting.Phase, waiting.Code));
    }

    private static PrinterCandidate OnlyCandidate(Scenario scenario) =>
        Assert.Single(scenario.CreateSession().Detect(new DetectRequest(), CancellationToken.None).Candidates);

    private IProgress<ProgressUpdate> Progress() => new Collector(_progress);

    private sealed class Collector(List<ProgressUpdate> updates) : IProgress<ProgressUpdate>
    {
        public void Report(ProgressUpdate value)
        {
            lock (updates)
                updates.Add(value);
        }
    }
}
