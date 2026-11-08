using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;
using static Amwag.PrinterSetup.Core.Setup.ActionKind;
using static Amwag.PrinterSetup.Core.Tests.TestHelpers;

namespace Amwag.PrinterSetup.Core.Tests;

/// <summary>
/// dmPaperWidth/dmPaperLength override dmPaperSize when their flags are set: defaults that name the ticket form but carry
/// another size print on that size, so they are not correct. Flags that match the ticket size are no difference.
/// </summary>
public sealed class PaperSizeOverrideTests
{
    private static readonly string Queue4030 = SampleConfig.DriverName(SampleConfig.P4030);

    private static void SetSize(DevMode devMode, int width, int length)
    {
        devMode.Fields |= DevModeFields.PaperWidth | DevModeFields.PaperLength;
        devMode.PaperWidth = (short)width;
        devMode.PaperLength = (short)length;
    }

    private static bool HasSizeFlag(DevMode devMode) => devMode.Has(DevModeFields.PaperWidth) || devMode.Has(DevModeFields.PaperLength);

    /// <summary>A correctly configured printer whose queue defaults were rewritten by another tool with an A4 size.</summary>
    private static Scenario WithA4SizeInTheQueueDefaults()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter().Build();
        var global = scenario.Spooler.GetStoredDevMode(Queue4030, DevModeScope.Global)!;
        SetSize(global, 2100, 2970);
        scenario.Spooler.SetStoredDevMode(Queue4030, DevModeScope.Global, global);
        return scenario;
    }

    [Fact]
    public void Fake_driver_resolves_the_paper_by_its_size_fields_first_and_clears_them()
    {
        // As measured on the v4 driver framework: 'Letter' with an A4 size comes back as A4, with both flags cleared.
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter().Build();
        var requested = FakeDevModes.Create(Queue4030, DmPaper.Letter, "Letter");
        SetSize(requested, 2100, 2970);

        var validated = scenario.Spooler.ValidateDevMode(Queue4030, requested);

        Assert.Equal(("A4", DmPaper.A4), (validated.FormName, validated.PaperSize));
        Assert.False(HasSizeFlag(validated));
        Assert.Equal((2100, 2970), ((int)validated.PaperWidth, (int)validated.PaperLength));
    }

    [Fact]
    public void Verification_fails_queue_defaults_whose_size_fields_override_the_ticket()
    {
        var scenario = WithA4SizeInTheQueueDefaults();
        var session = scenario.CreateSession();
        var target = session.SelectAutomatically(session.Detect(new DetectRequest(), default)).Selected!;

        var report = session.Verify(TargetSelector.For(target), default);

        var paper = Check(report, VerificationItem.DefaultPaper);
        Assert.Equal(CheckStatus.Fail, paper.Status);
        Assert.Equal("'AMWAG Ticket' (paper 257), size 2100 x 2970 tenths of a mm", paper.Actual);
        Assert.Contains("dmPaperWidth/dmPaperLength", paper.Detail, StringComparison.Ordinal);
        // The driver prints on the size the fields give.
        Assert.StartsWith("'A4' (paper 9)", Check(report, VerificationItem.EffectiveDefaults).Actual, StringComparison.Ordinal);
        Assert.Equal(ReadinessLevel.NotReady, report.Readiness);
    }

    [Fact]
    public void Repair_rewrites_queue_defaults_whose_size_fields_override_the_ticket()
    {
        var scenario = WithA4SizeInTheQueueDefaults();

        var run = scenario.Run(SetupMode.Repair);

        var incorrect = Assert.Single(run.Plan!.Findings, f => f.Code == FindingCode.GlobalDefaultsIncorrect);
        Assert.Equal("paper", incorrect.Data["fields"]);
        Assert.Equal([ConfigureGlobalDefaults], Kinds(run.Plan!));
        Assert.Equal(ActionOutcome.Succeeded, Outcome(run.Apply!, ConfigureGlobalDefaults));
        var written = scenario.Spooler.GetStoredDevMode(Queue4030, DevModeScope.Global)!;
        Assert.Equal(SampleConfig.FormName, written.FormName);
        Assert.False(HasSizeFlag(written));
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
    }

    [Fact]
    public void Repair_rewrites_per_user_defaults_whose_size_fields_override_the_ticket()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter()
            .WithPerUserDefaults(Queue4030, d => SetSize(d, 2100, 2970))
            .Build();

        var run = scenario.Run(SetupMode.Repair);

        var incorrect = Assert.Single(run.Plan!.Findings, f => f.Code == FindingCode.UserDefaultsIncorrect);
        Assert.Equal("paper", incorrect.Data["fields"]);
        Assert.Equal([ConfigureUserDefaults], Kinds(run.Plan!));
        Assert.Equal(ActionOutcome.Succeeded, Outcome(run.Apply!, ConfigureUserDefaults));
        Assert.False(HasSizeFlag(scenario.Spooler.GetStoredDevMode(Queue4030, DevModeScope.PerUser)!));
        Assert.Equal(CheckStatus.Pass, Check(run.Verification!, VerificationItem.UserDefaultsConsistent).Status);
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
    }

    [Fact]
    public void Size_fields_within_a_millimeter_of_the_ticket_are_left_alone()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter().Build();
        var paper = scenario.TicketPaper(Queue4030);
        var global = scenario.Spooler.GetStoredDevMode(Queue4030, DevModeScope.Global)!;
        SetSize(global, paper.WidthTenthsOfMillimeter + 5, paper.HeightTenthsOfMillimeter - 10);
        scenario.Spooler.SetStoredDevMode(Queue4030, DevModeScope.Global, global);

        var run = scenario.Run(SetupMode.Repair);

        Assert.True(Has(run.Plan!.Findings, FindingCode.GlobalDefaultsCorrect));
        Assert.Empty(run.Plan!.Actions);
        Assert.Empty(scenario.Spooler.Writes);
        Assert.Equal(ReadinessLevel.Ready, run.Verification!.Readiness);
    }

    [Fact]
    public void Driver_that_keeps_the_size_fields_is_configured_once_and_then_left_alone()
    {
        var scenario = new ScenarioBuilder()
            .WithInstalledUsbPrinter(correctDefaults: false)
            .WithModelDriverProfile(SampleConfig.P4030, p => p with { SetsPaperSizeFields = true })
            .Build();

        var first = scenario.Run();

        // The read-back keeps the size flags with the ticket's own size: no DefaultsNotPersisted.
        Assert.Equal(ActionOutcome.Succeeded, Outcome(first.Apply!, ConfigureGlobalDefaults));
        var written = scenario.Spooler.GetStoredDevMode(Queue4030, DevModeScope.Global)!;
        Assert.True(written.Has(DevModeFields.PaperWidth | DevModeFields.PaperLength));
        Assert.Equal(ReadinessLevel.Ready, first.Verification!.Readiness);
        int writes = scenario.Spooler.Writes.Count;

        var second = scenario.Run(SetupMode.Repair);

        Assert.Empty(second.Plan!.Actions);
        Assert.Equal(writes, scenario.Spooler.Writes.Count);
        Assert.Equal(ReadinessLevel.Ready, second.Verification!.Readiness);
    }

    [Fact]
    public void Validated_defaults_whose_size_fields_select_another_size_are_never_written()
    {
        var scenario = new ScenarioBuilder().WithInstalledUsbPrinter(correctDefaults: false).Build();
        var session = scenario.CreateSession();
        var plan = session.Assess(session.SelectAutomatically(session.Detect(new DetectRequest(), default)).Selected!, default);
        var services = new SetupServices
        {
            Spooler = new ValidationEditingSpooler(scenario.Spooler, validated => SetSize(validated, 2100, 2970)),
            Devices = scenario.Devices,
            Platform = scenario.Platform,
            Network = scenario.Network,
            Packages = scenario.Packages,
            Installer = scenario.Installer,
            Log = scenario.Log,
            Clock = scenario.Clock,
        };

        var apply = new SetupSession(scenario.Config, null, scenario.Options, services).Apply(plan, ApplyScope.All, null, default);

        var result = Result(apply, ConfigureGlobalDefaults);
        Assert.Equal(FindingCode.DriverRejectedSetting, result.FailureCode);
        Assert.Contains("size 2100 x 2970 tenths of a mm", result.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Operation == nameof(IPrintSpooler.SetDevMode));
    }

    /// <summary>Forwards to the fake, but edits every DEVMODE the driver returns from validation.</summary>
    private sealed class ValidationEditingSpooler : IPrintSpooler
    {
        private readonly FakePrintSpooler _inner;
        private readonly Action<DevMode> _edit;

        public ValidationEditingSpooler(FakePrintSpooler inner, Action<DevMode> edit)
        {
            _inner = inner;
            _edit = edit;
        }

        public DevMode ValidateDevMode(string queueName, DevMode requested)
        {
            var validated = _inner.ValidateDevMode(queueName, requested);
            _edit(validated);
            return validated;
        }

        public SpoolerServiceStatus GetServiceStatus() => _inner.GetServiceStatus();
        public bool TryStartService(TimeSpan timeout, out string? failureReason) => _inner.TryStartService(timeout, out failureReason);
        public IReadOnlyList<PrintQueueInfo> GetQueues() => _inner.GetQueues();
        public IReadOnlyList<PrinterPortInfo> GetPorts() => _inner.GetPorts();
        public IReadOnlyList<PrinterDriverInfo> GetDrivers() => _inner.GetDrivers();
        public IReadOnlyList<FormInfo> GetForms() => _inner.GetForms();
        public void AddForm(FormInfo form) => _inner.AddForm(form);
        public void SetForm(string formName, FormInfo form) => _inner.SetForm(formName, form);
        public void AddStandardTcpIpPort(TcpIpPortRequest request) => _inner.AddStandardTcpIpPort(request);
        public void AddQueue(NewQueueRequest request) => _inner.AddQueue(request);
        public void SetQueueDriver(string queueName, string driverName) => _inner.SetQueueDriver(queueName, driverName);
        public void SetQueuePort(string queueName, string portName) => _inner.SetQueuePort(queueName, portName);
        public DriverCapabilities GetCapabilities(string queueName) => _inner.GetCapabilities(queueName);
        public DevMode? GetDevMode(string queueName, DevModeScope scope) => _inner.GetDevMode(queueName, scope);
        public void SetDevMode(string queueName, DevModeScope scope, DevMode devMode) => _inner.SetDevMode(queueName, scope, devMode);
        public PrintCapabilitiesSummary GetPrintCapabilities(string queueName, DevMode? devMode) => _inner.GetPrintCapabilities(queueName, devMode);
        public PrintTicketMediaInfo DescribePrintTicket(string queueName, DevMode devMode) => _inner.DescribePrintTicket(queueName, devMode);
        public IReadOnlyList<PrintJobInfo> GetJobs(string queueName) => _inner.GetJobs(queueName);
    }
}
