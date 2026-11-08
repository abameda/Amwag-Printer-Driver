using Amwag.PrinterSetup.App.Flow;
using Amwag.PrinterSetup.App.TestTicket;
using Amwag.PrinterSetup.App.UI;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.App.Tests.UI;

/// <summary>Representative detection and result data for rendering every view.</summary>
internal static class UiSamples
{
    public static PrinterCandidate UsbPrinter { get; } = new()
    {
        Key = PrinterCandidate.UsbKey(@"USBPRINT\TA_TRIUMPH-ADLERP-4030DN\7&1A2B3C&0&USB001"),
        Kind = TargetKind.UsbDevice,
        Model = TestRuntime.Config.Models[0],
        Confidence = MatchConfidence.Exact,
        Connection = ConnectionType.Usb,
        Device = new PnpPrinterDevice { InstanceId = @"USBPRINT\TA_TRIUMPH-ADLERP-4030DN\7&1A2B3C&0&USB001", Bus = PnpBus.Usb, IsPresent = true, UsbPortName = "USB001" },
        Queue = new PrintQueueInfo { Name = "AMWAG Ticket Printer", DriverName = "TA P-4030DN KX", PortName = "USB001" },
        PhysicallyAvailable = true,
    };

    public static PrinterCandidate NetworkPrinter { get; } = new()
    {
        Key = PrinterCandidate.NetworkKey("10.0.0.5"),
        Kind = TargetKind.NetworkAddress,
        Model = TestRuntime.Config.Models[1],
        Confidence = MatchConfidence.Probable,
        Connection = ConnectionType.TcpIp,
        NetworkAddress = "10.0.0.5",
        NetworkIdentity = new NetworkIdentity { Address = "10.0.0.5", RawPortReachable = true, SnmpResponded = true, DeviceDescription = "TA Triumph-Adler P-4531DN" },
        PhysicallyAvailable = true,
    };

    public static PrinterCandidate UnidentifiedNetworkPrinter { get; } = NetworkPrinter with
    {
        Key = PrinterCandidate.NetworkKey("10.0.0.23"),
        Model = null,
        Confidence = MatchConfidence.None,
        NetworkAddress = "10.0.0.23",
        NetworkIdentity = new NetworkIdentity { Address = "10.0.0.23", RawPortReachable = true },
    };

    public static ConfiguredSummary Summary { get; } = new()
    {
        ModelName = "TA P-4030DN",
        QueueName = "AMWAG Ticket Printer",
        DriverName = "TA P-4030DN KX",
        DriverVersion = "8.4.1012.0",
        Connection = "USB (USB001)",
        PaperSize = "AMWAG Ticket 82.30 x 170.18 mm (3.24 x 6.70 in)",
        PaperSource = "Multipurpose tray",
    };

    public static IReadOnlyList<VerificationCheck> PassingChecks { get; } =
    [
        new(VerificationItem.SpoolerRunning, CheckStatus.Pass, "Running", "Running", ""),
        new(VerificationItem.DriverInstalled, CheckStatus.Pass, "TA P-4030DN KX", "TA P-4030DN KX 8.4.1012.0", ""),
        new(VerificationItem.QueueExists, CheckStatus.Pass, "AMWAG Ticket Printer", "AMWAG Ticket Printer", ""),
        new(VerificationItem.FormSizeExact, CheckStatus.Pass, "82296 x 170180 µm", "82296 x 170180 µm", ""),
        new(VerificationItem.DefaultPaper, CheckStatus.Pass, "AMWAG Ticket", "AMWAG Ticket", ""),
        new(VerificationItem.DefaultOrientation, CheckStatus.Pass, "Portrait", "Portrait", ""),
        new(VerificationItem.DefaultDuplex, CheckStatus.Pass, "Simplex", "Simplex", ""),
        new(VerificationItem.DefaultScale, CheckStatus.Pass, "100%", "100%", ""),
        new(VerificationItem.DefaultPaperSource, CheckStatus.Pass, "Multipurpose tray", "Multipurpose tray", ""),
    ];

    public static FlowResult Ready(SetupMode mode = SetupMode.Setup) => new(FlowOutcome.Ready)
    {
        Target = UsbPrinter,
        Verification = new VerificationReport { TargetKey = UsbPrinter.Key, Target = UsbPrinter, Readiness = ReadinessLevel.Ready, Summary = Summary, Checks = PassingChecks },
        Report = Report(mode),
    };

    public static FlowResult ReadyWithWarnings()
    {
        var checks = PassingChecks.Concat(
        [
            new VerificationCheck(VerificationItem.UserDefaultsConsistent, CheckStatus.Warning, "AMWAG Ticket", "Letter", "Per-user DEVMODE differs."),
            new VerificationCheck(VerificationItem.PrintTicketMediaSize, CheckStatus.Unknown, "AMWAG Ticket", "", "PrintTicket conversion unavailable."),
        ]).ToList();
        var warning = new Finding(FindingCode.PaperSourceNotFound, Severity.Warning, "No bin named 'MP Tray' on queue 'AMWAG Ticket Printer'.");
        return new FlowResult(FlowOutcome.ReadyWithWarnings)
        {
            Target = UsbPrinter,
            Verification = new VerificationReport { TargetKey = UsbPrinter.Key, Target = UsbPrinter, Readiness = ReadinessLevel.ReadyWithWarnings, Summary = Summary with { PaperSource = null }, Checks = checks, Findings = [warning] },
            Findings = [new Finding(FindingCode.ApplicationMayOverrideDefaults, Severity.Warning, "Applications may pass their own DEVMODE.")],
            Report = Report(SetupMode.Setup),
        };
    }

    public static FlowResult Failed() => new(FlowOutcome.Failed)
    {
        Target = NetworkPrinter,
        Findings = [new Finding(FindingCode.DriverPackageUnavailable, Severity.Error, "No approved archive in 'drivers' and the download from the approved URL failed (HTTP 404).")],
        Report = Report(SetupMode.Setup) with
        {
            ApplyResults =
            [
                new ApplyResult
                {
                    Results =
                    [
                        new ActionResult(new PlannedAction(ActionKind.PrepareDriverPackage, false, "Prepare TA driver package"), ActionOutcome.Failed, "Download failed")
                        {
                            FailureCode = FindingCode.DriverPackageUnavailable,
                            Win32Error = 2,
                        },
                    ],
                },
            ],
        },
    };

    public static FlowResult ElevationDeclined() => new(FlowOutcome.ElevationDeclined)
    {
        Target = UsbPrinter,
        Findings = [new Finding(FindingCode.ElevationDeclined, Severity.Warning, "UAC prompt was cancelled (Win32 error 1223).")],
        Report = Report(SetupMode.Setup),
    };

    public static FlowResult Diagnostics()
    {
        var second = new VerificationReport
        {
            TargetKey = NetworkPrinter.Key,
            Target = NetworkPrinter,
            Readiness = ReadinessLevel.NotReady,
            Summary = new ConfiguredSummary { ModelName = "TA P-4531DN", QueueName = "TA P-4531DN (Office)" },
            Checks =
            [
                new(VerificationItem.QueueExists, CheckStatus.Pass, "TA P-4531DN (Office)", "TA P-4531DN (Office)", ""),
                new(VerificationItem.FormExists, CheckStatus.Fail, "AMWAG Ticket", "missing", ""),
                new(VerificationItem.PrinterOnline, CheckStatus.Warning, "Online", "Offline", ""),
            ],
        };
        var ready = Ready(SetupMode.Diagnostics);
        return ready with { Verifications = [ready.Verification!, second] };
    }

    /// <summary>A ticket software check that caught overrides of the paper, scale and tray (orientation set, duplex left out).</summary>
    public static TicketCheckResult TicketCheckDifferences() => new(TicketCheckOutcome.JobCaptured, "AMWAG Ticket Printer")
    {
        JobId = 12,
        Document = "Ticket 2026-1103-0042 (Cairo branch)",
        Rows =
        [
            new(TicketSetting.Paper, TicketSettingStatus.Differs, TicketValue.Data("A4"), TicketValue.Data("AMWAG Ticket", "82.3 x 170.18 mm"), ""),
            new(TicketSetting.Orientation, TicketSettingStatus.Matches, TicketValue.Text("ticketcheck.value.portrait"), TicketValue.Text("ticketcheck.value.portrait"), ""),
            new(TicketSetting.Duplex, TicketSettingStatus.NotSpecified, TicketValue.NotSpecified, TicketValue.Text("ticketcheck.value.simplex"), ""),
            new(TicketSetting.Scale, TicketSettingStatus.Differs, TicketValue.Data("94%"), TicketValue.Data("100%"), ""),
            new(TicketSetting.PaperSource, TicketSettingStatus.Differs, TicketValue.Data("Cassette 1"), TicketValue.Data("MP tray"), ""),
        ],
    };

    public static TicketCheckResult TicketCheckMatches() => TicketCheckDifferences() with
    {
        Rows =
        [
            new(TicketSetting.Paper, TicketSettingStatus.Matches, TicketValue.Data("AMWAG Ticket"), TicketValue.Data("AMWAG Ticket", "82.3 x 170.18 mm"), ""),
            new(TicketSetting.Orientation, TicketSettingStatus.Matches, TicketValue.Text("ticketcheck.value.portrait"), TicketValue.Text("ticketcheck.value.portrait"), ""),
            new(TicketSetting.Duplex, TicketSettingStatus.Matches, TicketValue.Text("ticketcheck.value.simplex"), TicketValue.Text("ticketcheck.value.simplex"), ""),
            new(TicketSetting.Scale, TicketSettingStatus.Matches, TicketValue.Data("100%"), TicketValue.Data("100%"), ""),
            new(TicketSetting.PaperSource, TicketSettingStatus.NotSpecified, TicketValue.NotSpecified, TicketValue.Data("MP tray"), ""),
        ],
    };

    public static TicketCheckResult TicketCheckGone() => new(TicketCheckOutcome.JobGoneBeforeRead, "AMWAG Ticket Printer") { JobId = 13, Document = "Ticket 2026-1103-0043" };

    public static IEnumerable<TicketCheckResult> TicketCheckResults() =>
    [
        TicketCheckDifferences(),
        TicketCheckMatches(),
        TicketCheckGone(),
        new(TicketCheckOutcome.SettingsUnavailable, "AMWAG Ticket Printer") { JobId = 14 },
        new(TicketCheckOutcome.NoJob, "AMWAG Ticket Printer"),
        new(TicketCheckOutcome.Cancelled, "AMWAG Ticket Printer"),
        new(TicketCheckOutcome.QueueUnreadable, "AMWAG Ticket Printer") { Detail = "EnumJobs failed (Win32 error 1801): The printer name is invalid." },
    ];

    /// <summary>Opens the ticket check view without starting its background watch, so a test can drive the view itself.</summary>
    public static void StartTicketCheckWithoutWatching(MainForm form, FlowResult result) =>
        Assert.NotNull(form.OpenTicketCheck(result, watch: false));

    public static DiagnosticReport Report(SetupMode mode) => new()
    {
        GeneratedAt = new DateTimeOffset(2026, 11, 3, 14, 5, 0, TimeSpan.FromHours(2)),
        AppVersion = "1.0.0",
        SessionId = "ui-tests",
        Mode = mode,
        ConfigSource = "test",
        ConfigVersion = "test",
        TicketDescription = "AMWAG Ticket 82.296 x 170.18 mm",
        TicketDimensionsConfirmed = true,
    };
}
