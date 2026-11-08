using System.Diagnostics;
using Amwag.PrinterSetup.App.TestTicket;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Testing;

namespace Amwag.PrinterSetup.App.Tests.TestTicket;

public sealed class TicketSoftwareCheckTests
{
    private const string Queue = "AMWAG Ticket Printer";
    private static readonly string Driver = SampleConfig.DriverName(SampleConfig.P4030);

    private readonly FakePrintSpooler _spooler = new();
    private readonly MemoryLog _log = new();
    private readonly Dictionary<int, Action> _atDelay = [];
    private DeploymentConfig _config = SampleConfig.Create();
    private int _delays;

    public TicketSoftwareCheckTests()
    {
        _spooler.SeedForm(FormInfo.UserForm(SampleConfig.FormName, SampleConfig.Ticket));
        _spooler.SeedQueue(new PrintQueueInfo { Name = Queue, DriverName = Driver, PortName = "USB001" });
    }

    /// <summary>The DEVMODE an application submits: by default exactly the AMWAG Ticket defaults (paper 257 is the ticket form).</summary>
    private static DevMode Job(short paper = FakeDriverProfile.FirstFormPaperId, string form = SampleConfig.FormName, short orientation = DmOrientation.Portrait,
        short duplex = DmDuplex.Simplex, short scale = 100, short source = SampleConfig.MpTrayBinId) =>
        FakeDevModes.Create(Queue, paper, form, orientation, duplex, scale, source);

    /// <summary>Instant waits that count the polls and run the scripted step of the n-th wait.</summary>
    private Task Delay(TimeSpan interval, CancellationToken cancellationToken)
    {
        Assert.Equal(TicketSoftwareCheck.PollInterval, interval);
        cancellationToken.ThrowIfCancellationRequested();
        _delays++;
        if (_atDelay.TryGetValue(_delays, out var step))
            step();
        return Task.CompletedTask;
    }

    private Task<TicketCheckResult> Run(ModelDefinition? model, TimeSpan? timeout = null, IProgress<TicketCheckProgress>? progress = null, CancellationToken cancellationToken = default) =>
        new TicketSoftwareCheck(Queue, _config, model, _spooler, _log, Delay, timeout).RunAsync(progress, cancellationToken);

    private Task<TicketCheckResult> Run(TimeSpan? timeout = null) => Run(SampleConfig.Model(SampleConfig.P4030), timeout);

    private static TicketSettingRow Row(TicketCheckResult result, TicketSetting setting) => Assert.Single(result.Rows, r => r.Setting == setting);

    [Fact]
    public async Task Job_with_the_ticket_settings_matches_every_setting_and_nothing_is_changed()
    {
        PrintJobInfo? job = null;
        _atDelay[2] = () => job = _spooler.SubmitJob(Queue, "Ticket 1042", Job());

        var result = await Run();

        Assert.Equal(TicketCheckOutcome.JobCaptured, result.Outcome);
        Assert.Equal(job!.JobId, result.JobId);
        Assert.Equal("Ticket 1042", result.Document);
        Assert.Equal([TicketSetting.Paper, TicketSetting.Orientation, TicketSetting.Duplex, TicketSetting.Scale, TicketSetting.PaperSource], result.Rows.Select(r => r.Setting));
        Assert.All(result.Rows, r => Assert.Equal(TicketSettingStatus.Matches, r.Status));
        Assert.False(result.HasDifferences);
        Assert.Equal(["MP tray"], Row(result, TicketSetting.PaperSource).Requested.Args);
        Assert.Equal([SampleConfig.FormName], Row(result, TicketSetting.Paper).Requested.Args);
        Assert.Empty(_spooler.Writes);

        var logged = Assert.Single(_log.Find("ticketcheck.result"));
        Assert.Equal("JobCaptured", logged.Data!["outcome"]);
        Assert.Equal(job.JobId, logged.Data["jobId"]);
        Assert.True(_log.Contains("ticketcheck.started"));
    }

    [Fact]
    public async Task Jobs_already_in_the_queue_when_the_check_starts_are_ignored()
    {
        _spooler.SubmitJob(Queue, "Stuck invoice", Job(DmPaper.A4, "A4"));
        _atDelay[3] = () => _spooler.SubmitJob(Queue, "Ticket 7", Job());

        var result = await Run();

        Assert.Equal(TicketCheckOutcome.JobCaptured, result.Outcome);
        Assert.Equal("Ticket 7", result.Document);
        Assert.False(result.HasDifferences);
    }

    [Fact]
    public async Task Every_overridden_setting_is_reported_with_what_the_job_asked_for()
    {
        _atDelay[1] = () => _spooler.SubmitJob(Queue, "Ticket", Job(DmPaper.Letter, "Letter", DmOrientation.Landscape, DmDuplex.Vertical, 90, DmBin.Auto));

        var result = await Run();

        Assert.Equal(TicketCheckOutcome.JobCaptured, result.Outcome);
        Assert.True(result.HasDifferences);
        Assert.All(result.Rows, r => Assert.Equal(TicketSettingStatus.Differs, r.Status));

        var paper = Row(result, TicketSetting.Paper);
        Assert.Equal(["Letter"], paper.Requested.Args);
        Assert.Null(paper.Requested.Key);
        Assert.Equal(["AMWAG Ticket", "82.3 x 170.18 mm"], paper.Expected.Args);
        Assert.Equal("ticketcheck.value.landscape", Row(result, TicketSetting.Orientation).Requested.Key);
        Assert.Equal("ticketcheck.value.portrait", Row(result, TicketSetting.Orientation).Expected.Key);
        Assert.Equal("ticketcheck.value.duplexLong", Row(result, TicketSetting.Duplex).Requested.Key);
        Assert.Equal("ticketcheck.value.simplex", Row(result, TicketSetting.Duplex).Expected.Key);
        Assert.Equal(["90%"], Row(result, TicketSetting.Scale).Requested.Args);
        Assert.Equal(["100%"], Row(result, TicketSetting.Scale).Expected.Args);
        Assert.Equal(["Auto tray select"], Row(result, TicketSetting.PaperSource).Requested.Args);
        Assert.Equal(["MP tray"], Row(result, TicketSetting.PaperSource).Expected.Args);

        var logged = Assert.Single(_log.Find("ticketcheck.result"));
        Assert.Equal(["Paper", "Orientation", "Duplex", "Scale", "PaperSource"], (string[])logged.Data!["differences"]!);
        Assert.Empty(_spooler.Writes);
    }

    [Theory]
    [InlineData(828, 1697, TicketSettingStatus.Matches)]  // 0.5 mm off in both directions
    [InlineData(823, 1702, TicketSettingStatus.Matches)]
    [InlineData(845, 1702, TicketSettingStatus.Differs)]  // 2.2 mm too wide
    [InlineData(823, 1690, TicketSettingStatus.Differs)]  // 1.2 mm too short
    public async Task Custom_size_counts_as_the_ticket_only_within_1_mm(int widthTenths, int lengthTenths, TicketSettingStatus expected)
    {
        var devMode = Job(DmPaper.User, "Custom");
        devMode.Fields |= DevModeFields.PaperWidth | DevModeFields.PaperLength;
        devMode.PaperWidth = (short)widthTenths;
        devMode.PaperLength = (short)lengthTenths;
        _atDelay[1] = () => _spooler.SubmitJob(Queue, "Ticket", devMode);

        var result = await Run();

        Assert.Equal(expected, Row(result, TicketSetting.Paper).Status);
        Assert.Equal("Custom", Row(result, TicketSetting.Paper).Requested.Args[0]);
        Assert.EndsWith(" mm", Row(result, TicketSetting.Paper).Requested.Args[1], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DmPaper.User, SampleConfig.FormName, 2100, 2970, TicketSettingStatus.Differs)]                     // the ticket's name, an A4 size
    [InlineData(FakeDriverProfile.FirstFormPaperId, SampleConfig.FormName, 2100, 2970, TicketSettingStatus.Differs)] // the ticket's paper id too
    [InlineData(DmPaper.A4, "A4", 823, 1702, TicketSettingStatus.Matches)]                                           // another paper, the ticket size
    public async Task A_set_size_alone_decides_the_paper_whatever_the_form_name_and_paper_id_say(short paperId, string formName, int widthTenths, int lengthTenths, TicketSettingStatus expected)
    {
        // A ticketing application that sets its own page size and leaves dmFormName and dmPaperSize as it found them.
        var devMode = Job(paperId, formName);
        devMode.Fields |= DevModeFields.PaperWidth | DevModeFields.PaperLength;
        devMode.PaperWidth = (short)widthTenths;
        devMode.PaperLength = (short)lengthTenths;
        // The driver resolves the paper by the size fields, so that is the size it prints on.
        Assert.Equal(widthTenths == 2100 ? "A4" : SampleConfig.FormName, _spooler.ValidateDevMode(Queue, devMode).FormName);
        _atDelay[1] = () => _spooler.SubmitJob(Queue, "Ticket", devMode);

        var result = await Run();

        var paper = Row(result, TicketSetting.Paper);
        Assert.Equal(expected, paper.Status);
        Assert.Equal([formName, widthTenths == 2100 ? "210 x 297 mm" : "82.3 x 170.2 mm"], paper.Requested.Args);
        Assert.Contains("the size fields decide", paper.Detail, StringComparison.Ordinal);
        Assert.Equal(expected == TicketSettingStatus.Differs, result.HasDifferences);
    }

    [Fact]
    public async Task Paper_given_only_by_number_is_named_from_the_driver_paper_list()
    {
        var devMode = Job();
        devMode.Fields &= ~DevModeFields.FormName;
        _atDelay[1] = () => _spooler.SubmitJob(Queue, "Ticket", devMode);

        var result = await Run();

        var paper = Row(result, TicketSetting.Paper);
        Assert.Equal(TicketSettingStatus.Matches, paper.Status);
        Assert.Equal([SampleConfig.FormName], paper.Requested.Args);
    }

    [Fact]
    public async Task Settings_the_program_leaves_out_are_not_specified_and_not_differences()
    {
        var devMode = Job(orientation: DmOrientation.Landscape, duplex: DmDuplex.Vertical, scale: 50, source: DmBin.Auto);
        devMode.Fields = DevModeFields.FormName | DevModeFields.PaperSize;
        _atDelay[1] = () => _spooler.SubmitJob(Queue, "Ticket", devMode);

        var result = await Run();

        Assert.Equal(TicketSettingStatus.Matches, Row(result, TicketSetting.Paper).Status);
        foreach (var setting in new[] { TicketSetting.Orientation, TicketSetting.Duplex, TicketSetting.Scale, TicketSetting.PaperSource })
        {
            Assert.Equal(TicketSettingStatus.NotSpecified, Row(result, setting).Status);
            Assert.Equal(TicketValue.NotSpecified, Row(result, setting).Requested);
        }
        Assert.False(result.HasDifferences);
    }

    [Fact]
    public async Task Multipurpose_tray_is_found_by_name_when_the_driver_numbers_it_differently()
    {
        _spooler.SetProfile(Driver, FakeDriverProfile.Default with { Bins = [new(DmBin.Auto, "Auto"), new(260, " MP Tray "), new(261, "Cassette 1")] });
        _atDelay[1] = () => _spooler.SubmitJob(Queue, "Ticket 1", Job(source: 260));
        var matching = await Run();

        _delays = 0;
        _atDelay[1] = () => _spooler.SubmitJob(Queue, "Ticket 2", Job(source: DmBin.Manual));
        var other = await Run();

        Assert.Equal(TicketSettingStatus.Matches, Row(matching, TicketSetting.PaperSource).Status);
        Assert.Equal(["MP Tray"], Row(matching, TicketSetting.PaperSource).Expected.Args);
        Assert.Equal(TicketSettingStatus.Differs, Row(other, TicketSetting.PaperSource).Status);
        Assert.Equal("ticketcheck.value.trayNumber", Row(other, TicketSetting.PaperSource).Requested.Key);
        Assert.Equal(["4"], Row(other, TicketSetting.PaperSource).Requested.Args);
    }

    [Fact]
    public async Task Without_the_driver_lists_the_configured_tray_ids_and_the_form_name_decide()
    {
        _spooler.SetProfile(Driver, FakeDriverProfile.Default with { CapabilitiesWin32Error = 1801 });
        _atDelay[1] = () => _spooler.SubmitJob(Queue, "Ticket", Job());

        var result = await Run();

        Assert.Equal(TicketCheckOutcome.JobCaptured, result.Outcome);
        Assert.All(result.Rows, r => Assert.Equal(TicketSettingStatus.Matches, r.Status));
        Assert.Equal("ticketcheck.value.multipurposeTray", Row(result, TicketSetting.PaperSource).Expected.Key);
        Assert.True(_log.Contains("ticketcheck.capabilities.read_failed"));
    }

    [Fact]
    public async Task Without_a_model_the_paper_source_cannot_be_judged()
    {
        _atDelay[1] = () => _spooler.SubmitJob(Queue, "Ticket", Job());

        var result = await Run(model: null);

        Assert.Equal(TicketSettingStatus.Unknown, Row(result, TicketSetting.PaperSource).Status);
        Assert.False(result.HasDifferences);
    }

    [Fact]
    public async Task Paper_source_is_not_compared_when_the_policy_leaves_it_to_the_driver()
    {
        _config = _config with { PrintPreferences = _config.PrintPreferences with { PaperSource = PaperSourcePolicy.DriverDefault } };
        _atDelay[1] = () => _spooler.SubmitJob(Queue, "Ticket", Job(source: DmBin.Auto));

        var result = await Run();

        Assert.DoesNotContain(result.Rows, r => r.Setting == TicketSetting.PaperSource);
        Assert.False(result.HasDifferences);
    }

    [Fact]
    public async Task Job_that_leaves_before_its_settings_could_be_read_is_reported()
    {
        PrintJobInfo? job = null;
        _atDelay[2] = () => job = _spooler.SubmitJob(Queue, "Ticket 9", devMode: null); // listed, settings not readable (yet)
        _atDelay[3] = () => _spooler.RemoveJob(Queue, job!.JobId);

        var result = await Run();

        Assert.Equal(TicketCheckOutcome.JobGoneBeforeRead, result.Outcome);
        Assert.Equal(job!.JobId, result.JobId);
        Assert.Equal("Ticket 9", result.Document);
        Assert.Empty(result.Rows);
        Assert.Equal(3, _delays); // stopped at the first poll after the job left, not at the timeout
    }

    [Fact]
    public async Task Job_whose_settings_Windows_never_reports_ends_at_the_timeout()
    {
        _atDelay[1] = () => _spooler.SubmitJob(Queue, "Ticket", devMode: null);

        var result = await Run(TimeSpan.FromSeconds(3));

        Assert.Equal(TicketCheckOutcome.SettingsUnavailable, result.Outcome);
        Assert.Equal(20, _delays); // 3 s in 150 ms polls
    }

    [Fact]
    public async Task No_job_is_reported_after_polling_every_150_ms_for_2_minutes()
    {
        var result = await Run();

        Assert.Equal(TicketCheckOutcome.NoJob, result.Outcome);
        Assert.Equal(TimeSpan.FromMilliseconds(150), TicketSoftwareCheck.PollInterval);
        Assert.Equal(TimeSpan.FromMinutes(2), TicketSoftwareCheck.DefaultTimeout);
        Assert.Equal(800, _delays);
        Assert.Equal("NoJob", Assert.Single(_log.Find("ticketcheck.result")).Data!["outcome"]);
    }

    [Fact]
    public async Task Real_waits_end_with_no_job_after_a_short_timeout()
    {
        var watch = Stopwatch.StartNew();

        var result = await new TicketSoftwareCheck(Queue, _config, SampleConfig.Model(SampleConfig.P4030), _spooler, _log, timeout: TimeSpan.FromMilliseconds(450))
            .RunAsync(null, CancellationToken.None);

        Assert.Equal(TicketCheckOutcome.NoJob, result.Outcome);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Stopping_the_check_reports_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        _atDelay[5] = cancellation.Cancel;

        var result = await Run(SampleConfig.Model(SampleConfig.P4030), cancellationToken: cancellation.Token);

        Assert.Equal(TicketCheckOutcome.Cancelled, result.Outcome);
        Assert.Equal(5, _delays);
        Assert.Equal("Cancelled", Assert.Single(_log.Find("ticketcheck.result")).Data!["outcome"]);
    }

    [Fact]
    public async Task Queue_that_cannot_be_read_ends_the_check_at_once()
    {
        _spooler.FailOperation(nameof(IPrintSpooler.GetJobs), FakePrintSpooler.ErrorRpcServerUnavailable);

        var result = await Run();

        Assert.Equal(TicketCheckOutcome.QueueUnreadable, result.Outcome);
        Assert.Contains("1722", result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, _delays);
    }

    [Fact]
    public async Task Transient_spooler_failures_do_not_end_the_check()
    {
        _atDelay[1] = () => _spooler.FailOperation(nameof(IPrintSpooler.GetJobs), FakePrintSpooler.ErrorRpcServerUnavailable);
        _atDelay[3] = () =>
        {
            _spooler.ClearFailure(nameof(IPrintSpooler.GetJobs));
            _spooler.SubmitJob(Queue, "Ticket", Job());
        };

        var result = await Run();

        Assert.Equal(TicketCheckOutcome.JobCaptured, result.Outcome);
        Assert.Single(_log.Find("ticketcheck.jobs.read_failed")); // logged once, not once per poll
    }

    [Fact]
    public async Task Progress_counts_down_once_per_second_and_names_the_job_being_read()
    {
        var reports = new List<TicketCheckProgress>();
        _atDelay[30] = () => _spooler.SubmitJob(Queue, "Ticket", devMode: null);
        _atDelay[34] = () =>
        {
            var job = _spooler.GetJobs(Queue).Single();
            _spooler.RemoveJob(Queue, job.JobId);
            _spooler.AddJob(Queue, job, Job());
        };

        var result = await Run(SampleConfig.Model(SampleConfig.P4030), progress: new SynchronousProgress<TicketCheckProgress>(reports.Add));

        Assert.Equal(TicketCheckOutcome.JobCaptured, result.Outcome);
        Assert.Equal(TimeSpan.Zero, reports[0].Waited);
        Assert.Equal(TimeSpan.FromMinutes(2), reports[0].Remaining);
        Assert.Null(reports[0].JobId);
        Assert.NotNull(reports[^1].JobId);
        Assert.True(reports.Count <= 8, $"{reports.Count} progress reports for 5 s of polling");
    }

    [Fact]
    public void Orientation_and_duplex_values_outside_the_standard_ones_are_shown_as_numbers()
    {
        var devMode = Job(orientation: 3, duplex: 9);
        var expected = TicketExpectation.For(_config, SampleConfig.Model(SampleConfig.P4030), FakeDriverProfile.StandardBins);

        var rows = TicketSettingsComparison.Compare(devMode, expected, null);

        var orientation = Assert.Single(rows, r => r.Setting == TicketSetting.Orientation);
        Assert.Equal(TicketSettingStatus.Differs, orientation.Status);
        Assert.Equal("ticketcheck.value.number", orientation.Requested.Key);
        Assert.Equal(["3"], orientation.Requested.Args);
        Assert.Equal(["9"], Assert.Single(rows, r => r.Setting == TicketSetting.Duplex).Requested.Args);
    }

    [Fact]
    public void Expectation_resolves_the_multipurpose_tray_like_the_setup_engine()
    {
        var model = SampleConfig.Model(SampleConfig.P4030);

        var byId = TicketExpectation.For(_config, model, FakeDriverProfile.StandardBins);
        var byName = TicketExpectation.For(_config, model, [new BinCapability(270, "mp TRAY")]);
        var missing = TicketExpectation.For(_config, model, [new BinCapability(DmBin.Auto, "Auto")]);

        Assert.Equal(new BinCapability(DmBin.Manual, "MP tray"), byId.SourceBin);
        Assert.Equal((short)270, byName.SourceBin?.Id);
        Assert.Null(missing.SourceBin);
        Assert.Equal([4], missing.SourceIds);
        Assert.Equal(DmOrientation.Portrait, byId.Orientation);
        Assert.Equal(DmDuplex.Simplex, byId.Duplex);
        Assert.Equal((short)100, byId.Scale);
        Assert.Equal(82_296, byId.Size?.WidthMicrometers);
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
