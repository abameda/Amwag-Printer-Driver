using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Testing;
using static Amwag.PrinterSetup.Core.Setup.ActionKind;
using static Amwag.PrinterSetup.Core.Tests.TestHelpers;

namespace Amwag.PrinterSetup.Core.Tests;

/// <summary>
/// A driver package that only the USB device binding needs (an accepted driver is already installed): not getting it is a
/// warning, and the queue and its defaults are still configured with the installed driver.
/// </summary>
public sealed class BindingPackageTests
{
    private static readonly string Queue4030 = SampleConfig.DriverName(SampleConfig.P4030);

    private static PrinterSetupException Offline() => new(FindingCode.DriverPackageUnavailable,
        "The package is not in the offline folder or cache, and the download failed: no internet connection.");

    /// <summary>Device and its Plug and Play queue on an inbox class driver; the approved driver is installed.</summary>
    private static ScenarioBuilder ClassDriverQueue() => new ScenarioBuilder()
        .WithUsbPrinter(driverDescription: SampleConfig.MicrosoftPcl6ClassDriver)
        .WithModelDriver()
        .WithDriver(SampleConfig.MicrosoftPcl6ClassDriver)
        .WithUsbQueue(driverName: SampleConfig.MicrosoftPcl6ClassDriver)
        .WithTicketForm();

    [Fact]
    public void Class_driver_queue_gets_the_installed_driver_and_the_defaults_when_the_package_is_unavailable()
    {
        var scenario = ClassDriverQueue().Build();
        scenario.Packages.Failure = Offline();

        var run = scenario.Run();
        var apply = run.Apply!;

        Assert.Equal([PrepareDriverPackage, BindUsbDevice, ChangeQueueDriver, ConfigureGlobalDefaults, ConfigureUserDefaults], Kinds(run.Plan!));
        Assert.Equal(ActionOutcome.Skipped, Outcome(apply, PrepareDriverPackage));
        Assert.Equal(ActionOutcome.Skipped, Outcome(apply, BindUsbDevice));
        Assert.Equal(ActionOutcome.Succeeded, Outcome(apply, ChangeQueueDriver));
        Assert.Equal(ActionOutcome.Succeeded, Outcome(apply, ConfigureGlobalDefaults));
        Assert.DoesNotContain(apply.Results, r => r.Outcome == ActionOutcome.Failed);
        Assert.True(apply.Succeeded);

        var queue = Assert.Single(scenario.Spooler.Queues, q => q.PortName == "USB001");
        Assert.Equal(Queue4030, queue.DriverName);
        Assert.Equal(SampleConfig.FormName, scenario.Spooler.GetStoredDevMode(queue.Name, DevModeScope.Global)!.FormName);

        // The real reason, as a warning; nothing reports the installed driver as missing.
        var warning = Assert.Single(apply.Findings, f => f.Code == FindingCode.DeviceDriverNotBound);
        Assert.Equal(Severity.Warning, warning.Severity);
        Assert.Contains("no internet connection", warning.Detail, StringComparison.Ordinal);
        Assert.Contains(Queue4030, warning.Detail, StringComparison.Ordinal);
        Assert.Equal(nameof(FindingCode.DriverPackageUnavailable), warning.Data["reason"]);
        Assert.DoesNotContain(apply.Findings, f => f.Severity == Severity.Error);

        // One attempt to obtain the package, and nothing was installed on the device.
        Assert.Single(scenario.Packages.PreparedPackageIds);
        Assert.Empty(scenario.Installer.Calls);
        // The queue is ready; the device that is still on the class driver is a warning, so the run is not reported as fully ready.
        AssertDeviceNotBound(run.Verification!);
    }

    [Fact]
    public void Machine_result_succeeds_so_the_per_user_step_still_runs()
    {
        var scenario = ClassDriverQueue().WithPerUserDefaults(SampleConfig.MicrosoftPcl6ClassDriver, d => d.Scale = 80).Build();
        scenario.Packages.Failure = Offline();
        var session = scenario.CreateSession();
        var target = session.SelectAutomatically(session.Detect(new DetectRequest(), default)).Selected!;
        var plan = session.Assess(target, default);

        var machine = session.Apply(plan, ApplyScope.Machine, null, default);
        var user = session.Apply(plan, ApplyScope.User, null, default);

        Assert.True(machine.Succeeded);
        Assert.Equal(ActionOutcome.Succeeded, Outcome(user, ConfigureUserDefaults));
        Assert.Equal(100, scenario.Spooler.GetStoredDevMode(SampleConfig.MicrosoftPcl6ClassDriver, DevModeScope.PerUser)!.Scale);
        AssertDeviceNotBound(session.Verify(TargetSelector.For(target), default));
    }

    [Fact]
    public void Unbound_device_without_a_queue_gets_one_created_on_its_port_when_the_package_is_unavailable()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).WithModelDriver().WithTicketForm().Build();
        scenario.Packages.Failure = Offline();

        var run = scenario.Run();
        var apply = run.Apply!;

        Assert.Equal([PrepareDriverPackage, BindUsbDevice, CreateQueue, ConfigureGlobalDefaults, ConfigureUserDefaults], Kinds(run.Plan!));
        Assert.Equal(ActionOutcome.Succeeded, Outcome(apply, CreateQueue));
        Assert.Contains(scenario.Spooler.Writes, w => w is { Operation: nameof(IPrintSpooler.AddQueue) } && w.Detail == $"driver={Queue4030}; port=USB001");
        // Nothing was installed in this run, so Windows creates no queue: no Plug and Play wait.
        Assert.Equal(TimeSpan.Zero, scenario.Clock.TotalSlept);
        Assert.Equal(ActionOutcome.Succeeded, Outcome(apply, ConfigureGlobalDefaults));
        Assert.DoesNotContain(apply.Results, r => r.Outcome == ActionOutcome.Failed);
        Assert.Single(scenario.Packages.PreparedPackageIds);
        AssertDeviceNotBound(run.Verification!, "connected, problem code 28");
    }

    [Fact]
    public void Queue_actions_do_not_depend_on_the_package_step_itself()
    {
        // An unexpected error still fails the package step, but the queue uses the installed driver and is configured.
        var scenario = ClassDriverQueue().Build();
        scenario.Packages.Failure = new IOException("The cache folder is not readable.");

        var apply = scenario.Run().Apply!;

        Assert.Equal(ActionOutcome.Failed, Outcome(apply, PrepareDriverPackage));
        Assert.Contains("depends on a failed action (PrepareDriverPackage)", Result(apply, BindUsbDevice).Detail, StringComparison.Ordinal);
        Assert.Equal(ActionOutcome.Succeeded, Outcome(apply, ChangeQueueDriver));
        Assert.Equal(ActionOutcome.Succeeded, Outcome(apply, ConfigureGlobalDefaults));
        Assert.Equal(Queue4030, Assert.Single(scenario.Spooler.Queues, q => q.PortName == "USB001").DriverName);
        Assert.Single(scenario.Packages.PreparedPackageIds);
    }

    [Fact]
    public void Untrusted_package_needed_only_for_the_binding_is_never_used_and_reported_under_its_own_code_as_a_warning()
    {
        var scenario = ClassDriverQueue().Build();
        scenario.Packages.Signature = FakeDriverPackageProvider.TrustedSignature with { IsTrusted = false, StatusText = "Untrusted root." };

        var apply = scenario.Run().Apply!;

        Assert.Equal(ActionOutcome.Skipped, Outcome(apply, PrepareDriverPackage));
        Assert.Equal(ActionOutcome.Skipped, Outcome(apply, BindUsbDevice));
        // The signature problem itself reaches IT ("signature not trusted, contact IT"), not only an unbound device.
        var warning = Assert.Single(apply.Findings, f => f.Code == FindingCode.DriverSignatureInvalid);
        Assert.Equal(Severity.Warning, warning.Severity);
        Assert.Contains("Untrusted root.", warning.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(apply.Findings, f => f.Code == FindingCode.DeviceDriverNotBound);
        Assert.Empty(scenario.Installer.Calls);
        Assert.Equal(ActionOutcome.Succeeded, Outcome(apply, ChangeQueueDriver));
        Assert.True(apply.Succeeded);
    }

    [Theory]
    [InlineData(FindingCode.DriverPackageIntegrityFailed, @"The offline copy drivers\KX_Driver_8.4.1013_x64.zip has SHA-256 0000 instead of 9f2c.")]
    [InlineData(FindingCode.DriverSignatureInvalid, "The catalog is signed by 'CN=Unknown', which is not a trusted signer.")]
    [InlineData(FindingCode.ConfigurationInvalid, "The driver package 'ta-kx-x64' is misconfigured: infPath is empty.")]
    public void Package_that_fails_a_check_keeps_its_code_as_a_warning_while_the_queue_and_defaults_are_configured(FindingCode code, string reason)
    {
        var scenario = ClassDriverQueue().WithPerUserDefaults(SampleConfig.MicrosoftPcl6ClassDriver, d => d.Scale = 80).Build();
        scenario.Packages.Failure = new PrinterSetupException(code, reason);

        var run = scenario.Run();
        var apply = run.Apply!;

        // The queue and both defaults go ahead with the installed driver, the per-user step too: no Error, so Succeeded.
        Assert.True(apply.Succeeded);
        Assert.DoesNotContain(apply.Findings, f => f.Severity == Severity.Error);
        foreach (var kind in new[] { ChangeQueueDriver, ConfigureGlobalDefaults, ConfigureUserDefaults })
            Assert.Equal(ActionOutcome.Succeeded, Outcome(apply, kind));
        Assert.Equal(ActionOutcome.Skipped, Outcome(apply, BindUsbDevice));

        // The tampering or configuration signal keeps its code; it is not turned into "replug the USB cable".
        var warning = Assert.Single(apply.Findings, f => f.Code == code);
        Assert.Equal(Severity.Warning, warning.Severity);
        Assert.Contains(reason, warning.Detail, StringComparison.Ordinal);
        Assert.Contains("nothing from it was used", warning.Detail, StringComparison.Ordinal);
        Assert.Equal((SampleConfig.PackageId, SampleConfig.UsbHardwareId(SampleConfig.P4030)), (warning.Data["package"], warning.Data["hardwareId"]));
        Assert.DoesNotContain(apply.Findings, f => f.Code == FindingCode.DeviceDriverNotBound);
        Assert.Contains(code.ToString(), Result(apply, PrepareDriverPackage).Detail, StringComparison.Ordinal);
        Assert.Empty(scenario.Installer.Calls);
        Assert.Single(scenario.Packages.PreparedPackageIds);

        // The device still is on the class driver: the run ends with warnings (exit 1), never as fully ready (exit 0).
        AssertDeviceNotBound(run.Verification!);
    }

    // ---- A package that may not be installed on this PC -------------------------------------------------------------

    [Fact]
    public void Unapproved_package_needed_only_for_the_binding_leaves_the_device_and_configures_the_queue_and_defaults()
    {
        var scenario = ClassDriverQueue().WithPackage(p => p with { Approved = false }).Build();

        var run = scenario.Run();
        var plan = run.Plan!;

        // No blocker, no binding and no package step: the queue gets the installed approved driver and the defaults.
        Assert.True(plan.CanProceed);
        Assert.Equal([ChangeQueueDriver, ConfigureGlobalDefaults, ConfigureUserDefaults], Kinds(plan));
        Assert.DoesNotContain(plan.Findings, f => f.Code == FindingCode.DriverPackageNotApproved);
        var warning = Assert.Single(plan.Findings, f => f.Code == FindingCode.DeviceDriverNotBound);
        Assert.Equal(Severity.Warning, warning.Severity);
        Assert.Equal(nameof(FindingCode.DriverPackageNotApproved), warning.Data["reason"]);
        Assert.Equal((SampleConfig.PackageId, SampleConfig.UsbHardwareId(SampleConfig.P4030)), (warning.Data["package"], warning.Data["hardwareId"]));
        Assert.Contains("is not approved", warning.Detail, StringComparison.Ordinal);
        Assert.Contains(SampleConfig.MicrosoftPcl6ClassDriver, warning.Detail, StringComparison.Ordinal);

        Assert.True(run.Apply!.Succeeded);
        var queue = Assert.Single(scenario.Spooler.Queues, q => q.PortName == "USB001");
        Assert.Equal(Queue4030, queue.DriverName);
        Assert.Equal(SampleConfig.FormName, scenario.Spooler.GetStoredDevMode(queue.Name, DevModeScope.Global)!.FormName);
        Assert.Empty(scenario.Packages.PreparedPackageIds);
        Assert.Empty(scenario.Installer.Calls);
        AssertDeviceNotBound(run.Verification!);
    }

    [Fact]
    public void Incompatible_package_needed_only_for_the_binding_still_creates_the_queue_of_an_unbound_device()
    {
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(driverBound: false)
            .WithModelDriver()
            .WithTicketForm()
            .WithPackage(p => p with { MinOsBuild = 22000 })
            .WithPlatform(p => p with { ProductName = "Windows 10 Pro", DisplayVersion = "22H2", Build = 19045 })
            .Build();

        var run = scenario.Run();
        var plan = run.Plan!;

        Assert.True(plan.CanProceed);
        Assert.Equal([CreateQueue, ConfigureGlobalDefaults, ConfigureUserDefaults], Kinds(plan));
        var warning = Assert.Single(plan.Findings, f => f.Code == FindingCode.DeviceDriverNotBound);
        Assert.Equal(nameof(FindingCode.DriverPackageIncompatible), warning.Data["reason"]);
        Assert.Contains("needs Windows build 22000", warning.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(plan.Findings, f => f.Code == FindingCode.DriverPackageIncompatible);

        Assert.True(run.Apply!.Succeeded);
        Assert.Contains(scenario.Spooler.Writes, w => w is { Operation: nameof(IPrintSpooler.AddQueue) } && w.Detail == $"driver={Queue4030}; port=USB001");
        Assert.Empty(scenario.Packages.PreparedPackageIds);
        AssertDeviceNotBound(run.Verification!, "connected, problem code 28");
    }

    [Fact]
    public void Unapproved_package_that_the_driver_install_needs_still_blocks_the_binding_too()
    {
        // The approved driver is not installed: the package is needed to install it, so both stay held back as before.
        var scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).WithPackage(p => p with { Approved = false }).Build();

        var plan = scenario.Run().Plan!;

        Assert.False(plan.CanProceed);
        Assert.Contains(plan.Blockers, b => b.Code == FindingCode.DriverPackageNotApproved && b.Detail.StartsWith("The driver must be installed, but package", StringComparison.Ordinal));
        Assert.Contains(BindUsbDevice, Kinds(plan));
        Assert.DoesNotContain(plan.Findings, f => f.Code == FindingCode.DeviceDriverNotBound && f.Data.ContainsKey("reason"));
        Assert.Empty(scenario.Spooler.Writes);
    }

    /// <summary>The verification of a printer whose queue is set up while the connected device is not linked to an accepted driver.</summary>
    private static void AssertDeviceNotBound(VerificationReport verification, string actual = "connected, driver 'Microsoft PCL6 Class Driver'")
    {
        var device = Check(verification, VerificationItem.DeviceConnected);
        Assert.Equal((CheckStatus.Warning, actual), (device.Status, device.Actual));
        Assert.True(Has(verification.Findings, FindingCode.DeviceDriverNotBound, Severity.Warning));
        Assert.DoesNotContain(verification.Checks, c => c.Status == CheckStatus.Fail);
        Assert.Equal(ReadinessLevel.ReadyWithWarnings, verification.Readiness);
    }

    [Fact]
    public void Package_that_a_driver_install_also_needs_stays_a_failure()
    {
        // The device is on a class driver and its queue on the outdated preferred driver; only the XPS variant is current.
        var scenario = new ScenarioBuilder()
            .WithUsbPrinter(driverDescription: SampleConfig.MicrosoftPcl6ClassDriver)
            .WithModelDriver(version: new Version(7, 9, 0, 0))
            .WithDriver(SampleConfig.XpsDriverName(SampleConfig.P4030))
            .WithUsbQueue()
            .WithTicketForm()
            .Build();
        scenario.Packages.Failure = Offline();

        var run = scenario.Run();
        var apply = run.Apply!;

        Assert.Equal([PrepareDriverPackage, InstallDriver, BindUsbDevice, ConfigureGlobalDefaults, ConfigureUserDefaults], Kinds(run.Plan!));
        var prepare = Result(apply, PrepareDriverPackage);
        Assert.Equal(ActionOutcome.Failed, prepare.Outcome);
        Assert.Equal(FindingCode.DriverPackageUnavailable, prepare.FailureCode);
        foreach (var kind in new[] { InstallDriver, BindUsbDevice })
            Assert.Contains("depends on a failed action (PrepareDriverPackage)", Result(apply, kind).Detail, StringComparison.Ordinal);
        Assert.True(Has(apply.Findings, FindingCode.DriverPackageUnavailable, Severity.Error));
        Assert.DoesNotContain(apply.Findings, f => f.Code == FindingCode.DeviceDriverNotBound);
        Assert.Single(scenario.Packages.PreparedPackageIds);
    }

    [Fact]
    public void Package_failure_needed_for_the_install_still_skips_the_queue()
    {
        var scenario = new ScenarioBuilder().WithUsbPrinter(driverBound: false).Build();
        scenario.Packages.Failure = Offline();

        var apply = scenario.Run().Apply!;

        // CreateQueue no longer depends on the package directly; it is skipped through the skipped InstallDriver.
        Assert.Equal(ActionOutcome.Skipped, Outcome(apply, CreateQueue));
        Assert.Contains("depends on a failed action (PrepareDriverPackage)", Result(apply, CreateQueue).Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(scenario.Spooler.Writes, w => w.Operation == nameof(IPrintSpooler.AddQueue));
        Assert.Single(scenario.Packages.PreparedPackageIds);
    }
}
