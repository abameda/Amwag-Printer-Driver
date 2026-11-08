using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.ServiceProcess;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Setup;
using Microsoft.Win32;
using static Amwag.PrinterSetup.Windows.Interop.Winspool;
using StartMode = Amwag.PrinterSetup.Core.Inventory.ServiceStartMode;

namespace Amwag.PrinterSetup.Windows.Spooler;

/// <summary>
/// Win32 print spooler implementation of <see cref="IPrintSpooler"/> (winspool.drv, prntvpt.dll). Read methods only
/// query; write methods need the rights their interface documentation names and throw <see cref="SpoolerException"/>.
/// Stateless and safe to use from several threads (each call opens its own handles).
/// </summary>
public sealed unsafe partial class WindowsPrintSpooler : IPrintSpooler
{
    private const string SpoolerServiceName = "Spooler";
    private const string TcpIpPortsKey = @"SYSTEM\CurrentControlSet\Control\Print\Monitors\Standard TCP/IP Port\Ports\";
    private const int MaxBufferAttempts = 4;

    private readonly ILog _log;
    private readonly string _serviceName;

    public WindowsPrintSpooler(ILog log)
        : this(log, SpoolerServiceName)
    {
    }

    /// <summary>Tests point the service methods at another (for example missing) service.</summary>
    internal WindowsPrintSpooler(ILog log, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        _log = log;
        _serviceName = serviceName;
    }

    private delegate bool BufferCall(IntPtr buffer, int size, out int needed);

    public SpoolerServiceStatus GetServiceStatus()
    {
        try
        {
            using var service = new ServiceController(_serviceName);
            var status = new SpoolerServiceStatus(ToRunState(service.Status), ToStartMode(service.StartType));
            _log.Debug("spooler.service.status", "Print Spooler service queried.", Data(("state", status.State), ("startMode", status.StartMode)));
            return status;
        }
        catch (InvalidOperationException ex)
        {
            bool missing = ex.InnerException is System.ComponentModel.Win32Exception { NativeErrorCode: 1060 };
            var state = missing ? ServiceRunState.NotInstalled : ServiceRunState.Unknown;
            _log.Warn("spooler.service.status_failed", "The Print Spooler service could not be queried.", Data(("state", state)), ex);
            return new SpoolerServiceStatus(state, StartMode.Unknown, ex.Message);
        }
    }

    public bool TryStartService(TimeSpan timeout, out string? failureReason)
    {
        try
        {
            using var service = new ServiceController(_serviceName);
            if (service.StartType == System.ServiceProcess.ServiceStartMode.Disabled)
            {
                failureReason = $"The {_serviceName} service is disabled.";
                _log.Warn("spooler.service.start_skipped", failureReason);
                return false;
            }

            var clock = Stopwatch.StartNew();
            switch (service.Status)
            {
                case ServiceControllerStatus.Running:
                    failureReason = null;
                    return true;
                case ServiceControllerStatus.StopPending:
                    service.WaitForStatus(ServiceControllerStatus.Stopped, timeout);
                    service.Start();
                    break;
                case ServiceControllerStatus.Stopped:
                    service.Start();
                    break;
            }

            service.WaitForStatus(ServiceControllerStatus.Running, Remaining(timeout, clock));
            _log.Info("spooler.service.started", $"The {_serviceName} service is running.", Data(("elapsedMs", clock.ElapsedMilliseconds)));
            failureReason = null;
            return true;
        }
        catch (Exception ex)
        {
            failureReason = ex.InnerException is null ? ex.Message : $"{ex.Message} {ex.InnerException.Message}";
            _log.Warn("spooler.service.start_failed", $"The {_serviceName} service could not be started.", Data(("reason", failureReason)), ex);
            return false;
        }
    }

    public IReadOnlyList<PrintQueueInfo> GetQueues()
    {
        int count = 0;
        using var buffer = Fetch("EnumPrinters", FindingCode.OperationFailed,
            (IntPtr b, int size, out int needed) => EnumPrintersW(PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS, null, 2, b, size, out needed, out count));

        var queues = new List<PrintQueueInfo>(count);
        PRINTER_INFO_2W* items = Items<PRINTER_INFO_2W>(buffer, count, "EnumPrinters");
        for (int i = 0; i < count; i++)
        {
            PRINTER_INFO_2W item = items[i];
            queues.Add(new PrintQueueInfo
            {
                Name = Text(item.pPrinterName) ?? "",
                DriverName = Text(item.pDriverName) ?? "",
                PortName = Text(item.pPortName) ?? "",
                ServerName = SpoolerConversions.NullIfEmpty(Text(item.pServerName)),
                ShareName = SpoolerConversions.NullIfEmpty(Text(item.pShareName)),
                Location = SpoolerConversions.NullIfEmpty(Text(item.pLocation)),
                Comment = SpoolerConversions.NullIfEmpty(Text(item.pComment)),
                PrintProcessor = SpoolerConversions.NullIfEmpty(Text(item.pPrintProcessor)),
                Datatype = SpoolerConversions.NullIfEmpty(Text(item.pDatatype)),
                Attributes = (PrinterAttributes)item.Attributes,
                Status = (PrinterStatusFlags)item.Status,
                JobCount = (int)item.cJobs,
            });
        }

        _log.Debug("spooler.queues.read", $"{queues.Count} print queues enumerated.", Data(("count", queues.Count)));
        return queues;
    }

    public IReadOnlyList<PrinterPortInfo> GetPorts()
    {
        int count = 0;
        using var buffer = Fetch("EnumPorts", FindingCode.OperationFailed,
            (IntPtr b, int size, out int needed) => EnumPortsW(null, 2, b, size, out needed, out count));

        var ports = new List<PrinterPortInfo>(count);
        PORT_INFO_2W* items = Items<PORT_INFO_2W>(buffer, count, "EnumPorts");
        for (int i = 0; i < count; i++)
        {
            PORT_INFO_2W item = items[i];
            string name = Text(item.pPortName) ?? "";
            string? monitor = SpoolerConversions.NullIfEmpty(Text(item.pMonitorName));
            string? description = SpoolerConversions.NullIfEmpty(Text(item.pDescription));
            var port = new PrinterPortInfo
            {
                Name = name,
                MonitorName = monitor,
                Description = description,
                Kind = SpoolerConversions.ClassifyPort(name, monitor, description),
            };
            ports.Add(port.Kind == PortKind.StandardTcpIp ? WithTcpIpSettings(port) : port);
        }

        _log.Debug("spooler.ports.read", $"{ports.Count} ports enumerated.", Data(("count", ports.Count)));
        return ports;
    }

    public IReadOnlyList<PrinterDriverInfo> GetDrivers()
    {
        // Level 8 exists since Windows Vista; the fallbacks only matter for print providers that reject it.
        foreach (uint level in (ReadOnlySpan<uint>)[8, 6, 3])
        {
            int count = 0;
            using var buffer = TryFetch("EnumPrinterDrivers", FindingCode.OperationFailed, ERROR_INVALID_LEVEL,
                (IntPtr b, int size, out int needed) => EnumPrinterDriversW(null, null, level, b, size, out needed, out count));
            if (buffer is null)
            {
                _log.Warn("spooler.drivers.level_unsupported", $"EnumPrinterDrivers rejected level {level}.", Data(("level", level)));
                continue;
            }

            EnsureFits(buffer, count, level switch { 8 => sizeof(DRIVER_INFO_8W), 6 => sizeof(DRIVER_INFO_6W), _ => sizeof(DRIVER_INFO_3W) }, "EnumPrinterDrivers");
            var drivers = new List<PrinterDriverInfo>(count);
            for (int i = 0; i < count; i++)
                drivers.Add(ReadDriver(buffer.Pointer, level, i));

            _log.Debug("spooler.drivers.read", $"{drivers.Count} printer drivers enumerated (level {level}).", Data(("count", drivers.Count), ("level", level)));
            return drivers;
        }

        throw Fail("EnumPrinterDrivers", ERROR_INVALID_LEVEL, FindingCode.OperationFailed, "levels 8, 6 and 3");
    }

    public IReadOnlyList<FormInfo> GetForms()
    {
        using var server = Open(null, SERVER_ACCESS_ENUMERATE, FindingCode.OperationFailed);
        int count = 0;
        using var buffer = Fetch("EnumForms", FindingCode.OperationFailed,
            (IntPtr b, int size, out int needed) => EnumFormsW(server, 1, b, size, out needed, out count));

        var forms = new List<FormInfo>(count);
        FORM_INFO_1W* items = Items<FORM_INFO_1W>(buffer, count, "EnumForms");
        for (int i = 0; i < count; i++)
        {
            FORM_INFO_1W item = items[i];
            forms.Add(new FormInfo(
                Text(item.pName) ?? "",
                SpoolerConversions.ToFormKind(item.Flags),
                item.Size.cx,
                item.Size.cy,
                item.ImageableArea.left,
                item.ImageableArea.top,
                item.ImageableArea.right,
                item.ImageableArea.bottom));
        }

        _log.Debug("spooler.forms.read", $"{forms.Count} forms enumerated.", Data(("count", forms.Count)));
        return forms;
    }

    private static PrinterDriverInfo ReadDriver(IntPtr buffer, uint level, int index)
    {
        switch (level)
        {
            case 8:
            {
                DRIVER_INFO_8W d = ((DRIVER_INFO_8W*)buffer)[index];
                return Driver(d.pName, d.pEnvironment, d.cVersion) with
                {
                    Version = SpoolerConversions.ToVersion(d.dwlDriverVersion),
                    Date = SpoolerConversions.ToDate(d.ftDriverDate),
                    Manufacturer = SpoolerConversions.NullIfEmpty(Text(d.pszMfgName)),
                    Provider = SpoolerConversions.NullIfEmpty(Text(d.pszProvider)),
                    HardwareId = SpoolerConversions.NullIfEmpty(Text(d.pszHardwareID)),
                    InfPath = SpoolerConversions.NullIfEmpty(Text(d.pszInfPath)),
                    PrintProcessor = SpoolerConversions.NullIfEmpty(Text(d.pszPrintProcessor)),
                };
            }
            case 6:
            {
                DRIVER_INFO_6W d = ((DRIVER_INFO_6W*)buffer)[index];
                return Driver(d.pName, d.pEnvironment, d.cVersion) with
                {
                    Version = SpoolerConversions.ToVersion(d.dwlDriverVersion),
                    Date = SpoolerConversions.ToDate(d.ftDriverDate),
                    Manufacturer = SpoolerConversions.NullIfEmpty(Text(d.pszMfgName)),
                    Provider = SpoolerConversions.NullIfEmpty(Text(d.pszProvider)),
                    HardwareId = SpoolerConversions.NullIfEmpty(Text(d.pszHardwareID)),
                };
            }
            default:
            {
                DRIVER_INFO_3W d = ((DRIVER_INFO_3W*)buffer)[index];
                return Driver(d.pName, d.pEnvironment, d.cVersion);
            }
        }

        static PrinterDriverInfo Driver(IntPtr name, IntPtr environment, uint version) =>
            new() { Name = Text(name) ?? "", Environment = Text(environment) ?? "", DriverModel = (int)version };
    }

    /// <summary>Standard TCP/IP monitor settings (read-only registry; value names are the monitor's, not a documented API).</summary>
    private PrinterPortInfo WithTcpIpSettings(PrinterPortInfo port)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(TcpIpPortsKey + port.Name, writable: false);
            if (key is null)
            {
                _log.Debug("spooler.ports.tcpip_settings_missing", $"No Standard TCP/IP settings were found for port '{port.Name}'.", Data(("port", port.Name)));
                return port;
            }

            return port with
            {
                HostAddress = SpoolerConversions.NullIfEmpty(key.GetValue("HostName") as string)
                    ?? SpoolerConversions.NullIfEmpty(key.GetValue("IPAddress") as string),
                PortNumber = key.GetValue("PortNumber") as int?,
                Protocol = (key.GetValue("Protocol") as int?) switch
                {
                    1 => TcpPortProtocol.Raw,
                    2 => TcpPortProtocol.Lpr,
                    _ => null,
                },
                SnmpEnabled = key.GetValue("SNMP Enabled") is int snmp ? snmp != 0 : null,
            };
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _log.Warn("spooler.ports.tcpip_settings_unreadable", $"The Standard TCP/IP settings of port '{port.Name}' could not be read.", Data(("port", port.Name)), ex);
            return port;
        }
    }

    private SafePrinterHandle Open(string? name, uint access, FindingCode code)
    {
        var defaults = new PRINTER_DEFAULTSW { DesiredAccess = access };
        if (OpenPrinterW(name, out SafePrinterHandle handle, ref defaults))
            return handle;

        int error = Marshal.GetLastPInvokeError();
        handle.Dispose();
        throw Fail("OpenPrinter", error, code, $"'{name ?? "local print server"}', access 0x{access:X8}");
    }

    /// <summary>The two-call buffer pattern; throws when the call fails for any reason other than a small buffer.</summary>
    private HGlobalBuffer Fetch(string operation, FindingCode code, BufferCall call) =>
        FetchCore(call, out int error) ?? throw Fail(operation, error, code);

    /// <summary>Like <see cref="Fetch"/>, but returns null when the call fails with <paramref name="absentError"/>.</summary>
    private HGlobalBuffer? TryFetch(string operation, FindingCode code, int absentError, BufferCall call)
    {
        HGlobalBuffer? buffer = FetchCore(call, out int error);
        if (buffer is null && error != absentError)
            throw Fail(operation, error, code);
        return buffer;
    }

    private static HGlobalBuffer? FetchCore(BufferCall call, out int error)
    {
        int size = 0;
        for (int attempt = 0; attempt < MaxBufferAttempts; attempt++)
        {
            var buffer = new HGlobalBuffer(size);
            bool succeeded = call(buffer.Pointer, size, out int needed);
            error = succeeded ? 0 : Marshal.GetLastPInvokeError();
            if (succeeded)
                return buffer;

            buffer.Dispose();
            // The data can grow between the calls (for example a queue added meanwhile): retry with the new size.
            if (error != ERROR_INSUFFICIENT_BUFFER || needed <= size)
                return null;
            size = needed;
        }

        error = ERROR_INSUFFICIENT_BUFFER;
        return null;
    }

    /// <summary>The structures at the start of a filled buffer, after checking the spooler returned that many bytes.</summary>
    private T* Items<T>(HGlobalBuffer buffer, int count, string operation)
        where T : unmanaged
    {
        EnsureFits(buffer, count, sizeof(T), operation);
        return (T*)buffer.Pointer;
    }

    private void EnsureFits(HGlobalBuffer buffer, int count, int entrySize, string operation)
    {
        if (count < 0 || (long)count * entrySize > buffer.Size)
            throw Fail(operation, ERROR_INVALID_DATA, FindingCode.OperationFailed, $"{count} entries do not fit the {buffer.Size} bytes returned");
    }

    private SpoolerException Fail(string operation, int error, FindingCode code, string? context = null)
    {
        SpoolerException exception = SpoolerConversions.Failure(operation, error, code, context);
        _log.Warn("spooler.call_failed", exception.Message, Data(("operation", operation), ("win32Error", error), ("code", exception.Code)));
        return exception;
    }

    private static string? Text(IntPtr value) => value == IntPtr.Zero ? null : Marshal.PtrToStringUni(value);

    private static Dictionary<string, object?> Data(params (string Key, object? Value)[] values) =>
        values.ToDictionary(value => value.Key, value => value.Value);

    private static TimeSpan Remaining(TimeSpan timeout, Stopwatch clock)
    {
        TimeSpan remaining = timeout - clock.Elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static ServiceRunState ToRunState(ServiceControllerStatus status) => status switch
    {
        ServiceControllerStatus.Running => ServiceRunState.Running,
        ServiceControllerStatus.Stopped => ServiceRunState.Stopped,
        ServiceControllerStatus.StartPending => ServiceRunState.StartPending,
        ServiceControllerStatus.StopPending => ServiceRunState.StopPending,
        ServiceControllerStatus.Paused => ServiceRunState.Paused,
        _ => ServiceRunState.Unknown,
    };

    private static StartMode ToStartMode(System.ServiceProcess.ServiceStartMode mode) => mode switch
    {
        System.ServiceProcess.ServiceStartMode.Automatic => StartMode.Automatic,
        System.ServiceProcess.ServiceStartMode.Manual => StartMode.Manual,
        System.ServiceProcess.ServiceStartMode.Disabled => StartMode.Disabled,
        _ => StartMode.Unknown,
    };
}
