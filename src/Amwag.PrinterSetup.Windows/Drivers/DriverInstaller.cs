using System.Runtime.InteropServices;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Windows.Interop;

namespace Amwag.PrinterSetup.Windows.Drivers;

/// <summary>Package-aware driver installation (UploadPrinterDriverPackage, InstallPrinterDriverFromPackage, UpdateDriverForPlugAndPlayDevices).</summary>
public sealed class DriverInstaller : IDriverInstaller
{
    private const int E_ACCESSDENIED = unchecked((int)0x80070005);
    private const int HRESULT_INSUFFICIENT_BUFFER = unchecked((int)0x8007007A);
    private const int InitialPathCapacity = 1024;

    private readonly ILog _log;

    public DriverInstaller(ILog log) => _log = log;

    public string StagePackage(PreparedDriverPackage package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.CatalogSignature.IsTrusted)
        {
            throw new PrinterSetupException(
                FindingCode.DriverSignatureInvalid,
                $"The package {package.InfPath} was not verified as trusted and is not staged.");
        }

        string environment = package.Definition.Environment;
        var data = new Dictionary<string, object?> { ["package"] = package.Definition.Id, ["inf"] = package.InfPath, ["environment"] = environment };
        _log.Info("driver.install.stage.start", "Staging the driver package in the driver store.", data);

        int hr = Upload(package.InfPath, environment, InitialPathCapacity, out string? storeInf, out uint required);
        if (hr == HRESULT_INSUFFICIENT_BUFFER && required > InitialPathCapacity)
            hr = Upload(package.InfPath, environment, (int)required, out storeInf, out _);

        if (hr != 0)
            throw Failure("UploadPrinterDriverPackage", "driver.install.stage.failed", hr, $"staging {package.InfPath} for {environment}", data);

        if (string.IsNullOrWhiteSpace(storeInf) || !File.Exists(storeInf))
        {
            _log.Error("driver.install.stage.failed", "UploadPrinterDriverPackage reported success without a usable driver-store INF.",
                data: new Dictionary<string, object?>(data) { ["storeInf"] = storeInf });
            throw new PrinterSetupException(
                FindingCode.DriverInstallFailed,
                $"UploadPrinterDriverPackage reported success for {package.InfPath}, but the driver-store INF '{storeInf}' does not exist.");
        }

        _log.Info("driver.install.stage.completed", "The driver package is in the driver store.",
            new Dictionary<string, object?>(data) { ["storeInf"] = storeInf });
        return storeInf;
    }

    public void InstallPrinterDriver(string driverStoreInfPath, string driverName, string environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverStoreInfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(driverName);
        ArgumentException.ThrowIfNullOrWhiteSpace(environment);

        var data = new Dictionary<string, object?> { ["storeInf"] = driverStoreInfPath, ["driver"] = driverName, ["environment"] = environment };
        _log.Info("driver.install.printer.start", "Installing the printer driver from the driver store.", data);

        // dwFlags 0: files newer than the ones in use are copied (IPDFP_COPY_ALL_FILES would force every file).
        int hr = DriverStoreNative.InstallPrinterDriverFromPackageW(null, driverStoreInfPath, driverName, environment, 0);
        if (hr != 0)
            throw Failure("InstallPrinterDriverFromPackage", "driver.install.printer.failed", hr, $"installing '{driverName}' ({environment}) from {driverStoreInfPath}", data);

        _log.Info("driver.install.printer.completed", "The printer driver is installed.", data);
    }

    public DeviceInstallResult InstallForDevice(PreparedDriverPackage package, string hardwareId)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(hardwareId);

        var data = new Dictionary<string, object?> { ["package"] = package.Definition.Id, ["inf"] = package.InfPath, ["hardwareId"] = hardwareId };
        _log.Info("driver.install.device.start", "Installing the driver on the connected device.", data);

        // INSTALLFLAG_FORCE binds the approved driver even when Windows ranks another one higher; NONINTERACTIVE makes the
        // call fail instead of showing any UI.
        int ok = NewDev.UpdateDriverForPlugAndPlayDevicesW(
            IntPtr.Zero, hardwareId, package.InfPath, NewDev.INSTALLFLAG_FORCE | NewDev.INSTALLFLAG_NONINTERACTIVE, out int rebootRequired);
        int error = ok != 0 ? 0 : Marshal.GetLastPInvokeError();
        bool reboot = rebootRequired != 0;
        var resultData = new Dictionary<string, object?>(data) { ["rebootRequired"] = reboot, ["error"] = error == 0 ? null : NativeErrorText.Hex(error) };

        if (ok != 0)
        {
            _log.Info("driver.install.device.completed", "The driver is installed on the device.", resultData);
            return new DeviceInstallResult(true, reboot, null,
                reboot ? "The driver is installed on the device; Windows needs a restart to finish." : "The driver is installed on the device.");
        }

        if (error == NewDev.ERROR_NO_SUCH_DEVINST)
        {
            _log.Info("driver.install.device.absent", "No connected device has the hardware ID.", resultData);
            return new DeviceInstallResult(false, false, error, "no connected device with this hardware ID");
        }

        _log.Error("driver.install.device.failed", "UpdateDriverForPlugAndPlayDevices failed.", data: resultData);
        return new DeviceInstallResult(false, reboot, error,
            $"UpdateDriverForPlugAndPlayDevices failed for {hardwareId} with {NativeErrorText.Format(error)}.{PublisherTrustAdvice(error)}");
    }

    /// <summary>The finding for a failed driver-store HRESULT.</summary>
    internal static FindingCode Classify(int hr) => hr switch
    {
        E_ACCESSDENIED => FindingCode.ElevationRequired,
        _ when IsTrustFailure(hr) => FindingCode.DriverSignatureInvalid,
        _ when IsSpoolerUnreachable(hr) => FindingCode.SpoolerUnavailable,
        _ => FindingCode.DriverInstallFailed,
    };

    /// <summary>TRUST_E_* and CERT_E_* (winerror.h) plus the SetupAPI catalog and Authenticode errors in HRESULT form.</summary>
    internal static bool IsTrustFailure(int hr)
    {
        uint code = (uint)hr;
        return (code & 0xFFFF0000) == 0x800B0000 // FACILITY_CERT: TRUST_E_PROVIDER_UNKNOWN ... CERT_E_INVALID_NAME
            || code is >= 0x80096001 and <= 0x8009601E // TRUST_E_SYSTEM_ERROR ... TRUST_E_FINANCIAL_CRITERIA
            || code is 0x800F022F // SPAPI_E_NO_CATALOG_FOR_OEM_INF
            || code is 0x800F023F or 0x800F0240 // SPAPI_E_NO_AUTHENTICODE_CATALOG, SPAPI_E_AUTHENTICODE_DISALLOWED
            || code is >= 0x800F0242 and <= 0x800F0245 // SPAPI_E_AUTHENTICODE_TRUST_NOT_ESTABLISHED ... SPAPI_E_ONLY_VALIDATE_VIA_AUTHENTICODE
            || code is 0x800F024B; // SPAPI_E_FILE_HASH_NOT_IN_CATALOG
    }

    /// <summary>
    /// Advice for "publisher not (yet) trusted", in HRESULT (0x800F0242/3) or SetupAPI (0xE0000242/3) form. A package signed
    /// by its vendor rather than by Microsoft installs without a prompt only when the publisher is trusted; otherwise
    /// Windows asks an administrator, which a silent installation cannot do
    /// (learn.microsoft.com/windows-hardware/drivers/install/signature-categories-and-driver-installation).
    /// </summary>
    internal static string PublisherTrustAdvice(int code) =>
        ((uint)code & 0x0000FFFF) is 0x0242 or 0x0243 && ((uint)code & 0xFFFF0000) is 0x800F0000 or 0xE0000000
            ? " Windows has not been told to trust the publisher of this vendor-signed (not WHQL) package, so it cannot be installed silently; " +
              "IT can add the publisher's code-signing certificate to the computer's Trusted Publishers store, or approve the publisher once in an interactive installation."
            : "";

    /// <summary>HRESULT_FROM_WIN32 of the RPC errors that mean the spooler is stopped or crashed (as SpoolerException.IsSpoolerUnavailable).</summary>
    private static bool IsSpoolerUnreachable(int hr) =>
        (uint)hr is 0x800706BA or 0x800706BE or 0x800706BF or 0x800706D9;

    private static unsafe int Upload(string infPath, string environment, int capacity, out string? storeInf, out uint length)
    {
        char[] buffer = new char[capacity];
        length = (uint)capacity;
        int hr;
        fixed (char* destination = buffer)
        {
            hr = DriverStoreNative.UploadPrinterDriverPackageW(
                null, infPath, environment, DriverStoreNative.UPDP_SILENT_UPLOAD, IntPtr.Zero, destination, ref length);
        }

        if (hr != 0)
        {
            storeInf = null;
            return hr;
        }

        int end = Array.IndexOf(buffer, '\0');
        storeInf = new string(buffer, 0, end < 0 ? Math.Min((int)length, capacity) : end);
        return hr;
    }

    private PrinterSetupException Failure(string api, string eventName, int hr, string operation, Dictionary<string, object?> data)
    {
        FindingCode code = Classify(hr);
        string message = $"{api} failed with HRESULT {NativeErrorText.Hex(hr)} while {operation}: {NativeErrorText.Describe(hr)}{PublisherTrustAdvice(hr)}";
        _log.Error(eventName, message,
            data: new Dictionary<string, object?>(data) { ["hresult"] = NativeErrorText.Hex(hr), ["code"] = code.ToString() });
        // A Win32 error wrapped as HRESULT (FACILITY_WIN32) is reported as that error; other HRESULTs as they are.
        int win32Error = ((uint)hr & 0xFFFF0000) == 0x80070000 ? hr & 0xFFFF : hr;
        return new PrinterSetupException(code, message, win32Error);
    }
}
