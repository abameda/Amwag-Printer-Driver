using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;

namespace Amwag.PrinterSetup.Testing;

/// <summary>
/// Installs drivers into a <see cref="FakePrintSpooler"/> and binds <see cref="FakeDeviceInventory"/> devices. After a USB
/// device is bound, "Windows" creates a Plug and Play queue on its port once <see cref="PlugAndPlayQueueDelay"/> of
/// <see cref="FakeClock"/> time has passed (never when null).
/// </summary>
public sealed class FakeDriverInstaller : IDriverInstaller
{
    /// <summary>SetupAPI ERROR_NO_SUCH_DEVINST: no present device has the hardware ID.</summary>
    public const int ErrorNoSuchDevinst = unchecked((int)0xE000020B);

    /// <summary>SetupAPI ERROR_NO_COMPAT_DRIVERS: the package has no driver for the hardware ID.</summary>
    public const int ErrorNoCompatDrivers = unchecked((int)0xE0000228);

    private readonly object _gate = new();
    private readonly List<string> _calls = [];
    private readonly FakePrintSpooler _spooler;
    private readonly FakeDeviceInventory _devices;
    private readonly FakeClock _clock;

    public FakeDriverInstaller(FakePrintSpooler spooler, FakeDeviceInventory devices, FakeClock clock)
    {
        _spooler = spooler;
        _devices = devices;
        _clock = clock;
    }

    /// <summary>"StagePackage:&lt;package id&gt;", "InstallPrinterDriver:&lt;driver&gt;", "InstallForDevice:&lt;hardware id&gt;", in order.</summary>
    public IReadOnlyList<string> Calls
    {
        get { lock (_gate) return [.. _calls]; }
    }

    /// <summary>INF model (driver) name a device gets when bound, by hardware ID.</summary>
    public Dictionary<string, string> DriverNamesByHardwareId { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Driver names the package contains; empty means it contains any requested name.</summary>
    public HashSet<string> PackageDriverNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public PrinterSetupException? StageFailure { get; set; }
    public PrinterSetupException? InstallFailure { get; set; }

    /// <summary>When set, InstallForDevice returns it without touching any device.</summary>
    public DeviceInstallResult? DeviceInstallResultOverride { get; set; }

    public bool DeviceInstallRequiresReboot { get; set; }

    public TimeSpan? PlugAndPlayQueueDelay { get; set; } = TimeSpan.FromSeconds(6);

    public Version DriverVersion { get; set; } = new(8, 4, 1013, 0);

    public string StagePackage(PreparedDriverPackage package)
    {
        lock (_gate)
            _calls.Add("StagePackage:" + package.Definition.Id);
        if (StageFailure is not null)
            throw StageFailure;
        string infName = Path.GetFileNameWithoutExtension(package.InfPath).ToLowerInvariant();
        return $@"C:\Windows\System32\DriverStore\FileRepository\{infName}.inf_amd64_5f2e1c3a9b7d4e60\{infName}.inf";
    }

    public void InstallPrinterDriver(string driverStoreInfPath, string driverName, string environment)
    {
        lock (_gate)
            _calls.Add("InstallPrinterDriver:" + driverName);
        if (InstallFailure is not null)
            throw InstallFailure;
        if (PackageDriverNames.Count > 0 && !PackageDriverNames.Contains(driverName))
            throw new PrinterSetupException(FindingCode.DriverInstallFailed, $"The package does not contain a driver named '{driverName}'.", FakePrintSpooler.ErrorUnknownPrinterDriver);
        _spooler.SeedDriver(Driver(driverName, environment, driverStoreInfPath));
    }

    public DeviceInstallResult InstallForDevice(PreparedDriverPackage package, string hardwareId)
    {
        lock (_gate)
            _calls.Add("InstallForDevice:" + hardwareId);
        if (DeviceInstallResultOverride is { } overridden)
            return overridden;

        var devices = _devices.Devices.Where(d => d.IsPresent && HasId(d, hardwareId)).ToList();
        if (devices.Count == 0)
            return new DeviceInstallResult(false, false, ErrorNoSuchDevinst, $"No present device has hardware ID {hardwareId}.");
        if (!DriverNamesByHardwareId.TryGetValue(hardwareId, out var driverName))
            return new DeviceInstallResult(false, false, ErrorNoCompatDrivers, $"The package has no driver for {hardwareId}.");

        if (!_spooler.Drivers.Any(d => string.Equals(d.Name, driverName, StringComparison.OrdinalIgnoreCase)))
            _spooler.SeedDriver(Driver(driverName, package.Definition.Environment, package.InfPath));

        foreach (var device in devices)
        {
            string port = device.UsbPortName ?? _spooler.AllocateUsbPort();
            _spooler.SeedUsbPort(port);
            _devices.Update(device.InstanceId, d => d with
            {
                ProblemCode = null,
                DriverDescription = driverName,
                DriverVersion = DriverVersion.ToString(),
                UsbPortName = port,
            });
            if (PlugAndPlayQueueDelay is { } delay)
                _clock.Schedule(delay, () => _spooler.CreatePlugAndPlayQueue(driverName, port));
        }

        return new DeviceInstallResult(true, DeviceInstallRequiresReboot, null, $"Installed '{driverName}' on {devices.Count} device(s).");
    }

    private PrinterDriverInfo Driver(string name, string environment, string infPath) => new()
    {
        Name = name,
        Environment = environment,
        DriverModel = 3,
        Version = DriverVersion,
        Date = new DateTime(2025, 6, 12, 0, 0, 0, DateTimeKind.Utc),
        Manufacturer = "TA Triumph-Adler",
        Provider = "TA Triumph-Adler",
        InfPath = infPath,
        PrintProcessor = "winprint",
    };

    private static bool HasId(PnpPrinterDevice device, string hardwareId) =>
        device.HardwareIds.Contains(hardwareId, StringComparer.OrdinalIgnoreCase) ||
        device.CompatibleIds.Contains(hardwareId, StringComparer.OrdinalIgnoreCase);
}
