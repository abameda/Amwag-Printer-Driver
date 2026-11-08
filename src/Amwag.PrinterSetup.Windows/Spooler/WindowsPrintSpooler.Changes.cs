using System.Runtime.InteropServices;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Setup;
using static Amwag.PrinterSetup.Windows.Interop.Winspool;

namespace Amwag.PrinterSetup.Windows.Spooler;

// Write operations. None of them runs on a development machine's tests; each documents the right it needs.
public sealed unsafe partial class WindowsPrintSpooler
{
    /// <summary>FORM_INFO_1 pName "cannot exceed 31 characters" (Microsoft Learn, FORM_INFO_1).</summary>
    private const int MaxFormNameLength = 31;

    private const string DefaultPrintProcessor = "winprint";

    public void AddForm(FormInfo form)
    {
        ArgumentNullException.ThrowIfNull(form);
        ValidateFormName(form.Name);

        using var server = Open(null, SERVER_ACCESS_ADMINISTER, FindingCode.FormCreateFailed);
        fixed (char* name = form.Name)
        {
            FORM_INFO_1W info = UserForm(form, name);
            if (!AddFormW(server, 1, ref info))
                throw Fail("AddForm", Marshal.GetLastPInvokeError(), FindingCode.FormCreateFailed, FormContext(form));
        }

        _log.Info("spooler.form.added", $"Form '{form.Name}' added.", FormData(form));
    }

    public void SetForm(string formName, FormInfo form)
    {
        ArgumentException.ThrowIfNullOrEmpty(formName);
        ArgumentNullException.ThrowIfNull(form);
        ValidateFormName(form.Name);

        using var server = Open(null, SERVER_ACCESS_ADMINISTER, FindingCode.FormCreateFailed);
        fixed (char* name = form.Name)
        {
            FORM_INFO_1W info = UserForm(form, name);
            if (!SetFormW(server, formName, 1, ref info))
                throw Fail("SetForm", Marshal.GetLastPInvokeError(), FindingCode.FormCreateFailed, $"'{formName}': {FormContext(form)}");
        }

        _log.Info("spooler.form.updated", $"Form '{formName}' updated.", FormData(form));
    }

    public void AddStandardTcpIpPort(TcpIpPortRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PortName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.HostAddress);
        ArgumentNullException.ThrowIfNull(request.SnmpCommunity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.PortNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.PortNumber, 65535);
        ArgumentOutOfRangeException.ThrowIfNegative(request.SnmpDeviceIndex);

        var data = new PORT_DATA_1
        {
            dwVersion = PORT_DATA_1_VERSION,
            dwProtocol = PROTOCOL_RAWTCP_TYPE,
            cbSize = (uint)sizeof(PORT_DATA_1),
            dwPortNumber = (uint)request.PortNumber,
            dwSNMPEnabled = request.SnmpEnabled ? 1u : 0u,
            dwSNMPDevIndex = (uint)request.SnmpDeviceIndex,
        };
        CopyPortText(request.PortName, data.sztPortName, MAX_PORTNAME_LEN, "port name");
        CopyPortText(request.HostAddress, data.sztHostAddress, MAX_NETWORKNAME_LEN, "host address");
        CopyPortText(request.SnmpCommunity, data.sztSNMPCommunity, MAX_SNMP_COMMUNITY_STR_LEN, "SNMP community");

        // AddPort takes PORT_DATA_1 as input and no output (https://learn.microsoft.com/windows-hardware/drivers/print/tcpmon-xcv-commands).
        using var xcv = Open(StandardTcpIpXcvMonitor, SERVER_ACCESS_ADMINISTER, FindingCode.PortCreateFailed);
        string context = $"port '{request.PortName}', host '{request.HostAddress}:{request.PortNumber}'";
        if (!XcvDataW(xcv, XcvAddPort, &data, sizeof(PORT_DATA_1), null, 0, out _, out int status))
            throw Fail("XcvData(AddPort)", Marshal.GetLastPInvokeError(), FindingCode.PortCreateFailed, context);
        if (status != 0)
            throw Fail("XcvData(AddPort)", status, FindingCode.PortCreateFailed, context);

        _log.Info("spooler.port.added", $"Standard TCP/IP port '{request.PortName}' added.", Data(
            ("port", request.PortName), ("host", request.HostAddress), ("portNumber", request.PortNumber), ("snmp", request.SnmpEnabled)));
    }

    public void AddQueue(NewQueueRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DriverName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PortName);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Datatype);

        string processor = request.PrintProcessor
            ?? GetDrivers().FirstOrDefault(d => string.Equals(d.Name, request.DriverName, StringComparison.OrdinalIgnoreCase))?.PrintProcessor
            ?? DefaultPrintProcessor;
        string context = $"queue '{request.Name}', driver '{request.DriverName}', port '{request.PortName}', processor '{processor}'";

        fixed (char* name = request.Name, port = request.PortName, driver = request.DriverName, printProcessor = processor,
               datatype = request.Datatype, location = request.Location, comment = request.Comment)
        {
            var info = new PRINTER_INFO_2W
            {
                pPrinterName = (IntPtr)name,
                pPortName = (IntPtr)port,
                pDriverName = (IntPtr)driver,
                pPrintProcessor = (IntPtr)printProcessor,
                pDatatype = (IntPtr)datatype,
                pLocation = (IntPtr)location,
                pComment = (IntPtr)comment,
                // Queues Windows creates use priority 1 (MIN_PRIORITY); a NULL security descriptor gives the default ACL.
                Priority = 1,
            };

            using SafePrinterHandle printer = AddPrinterW(null, 2, ref info);
            if (printer.IsInvalid)
                throw Fail("AddPrinter", Marshal.GetLastPInvokeError(), FindingCode.QueueCreateFailed, context);
        }

        _log.Info("spooler.queue.added", $"Print queue '{request.Name}' added.", Data(
            ("queue", request.Name), ("driver", request.DriverName), ("port", request.PortName), ("printProcessor", processor), ("datatype", request.Datatype)));
    }

    public void SetQueueDriver(string queueName, string driverName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(driverName);
        ChangeQueue(queueName, driverName, portName: null);
        _log.Info("spooler.queue.driver_changed", $"Print queue '{queueName}' now uses driver '{driverName}'.", Data(("queue", queueName), ("driver", driverName)));
    }

    public void SetQueuePort(string queueName, string portName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(portName);
        ChangeQueue(queueName, driverName: null, portName);
        _log.Info("spooler.queue.port_changed", $"Print queue '{queueName}' now uses port '{portName}'.", Data(("queue", queueName), ("port", portName)));
    }

    /// <summary>
    /// GetPrinter level 2, replace the driver or port, SetPrinter level 2. pSecurityDescriptor = NULL leaves the queue's
    /// security unchanged (https://learn.microsoft.com/windows/win32/printdocs/setprinter). pDevMode = NULL as well: a
    /// DEVMODE passed at level 2 becomes the caller's per-user DEVMODE
    /// (https://learn.microsoft.com/windows/win32/printdocs/per-user-devmode), and an old driver's private data does not
    /// fit a new driver.
    /// </summary>
    private void ChangeQueue(string queueName, string? driverName, string? portName)
    {
        using var printer = Open(queueName, PRINTER_ALL_ACCESS, FindingCode.OperationFailed);
        using var buffer = Fetch("GetPrinter", FindingCode.OperationFailed,
            (IntPtr b, int size, out int needed) => GetPrinterW(printer, 2, b, size, out needed));

        PRINTER_INFO_2W info = *Items<PRINTER_INFO_2W>(buffer, 1, "GetPrinter");
        fixed (char* driver = driverName, port = portName)
        {
            if (driver is not null)
                info.pDriverName = (IntPtr)driver;
            if (port is not null)
                info.pPortName = (IntPtr)port;
            info.pSecurityDescriptor = IntPtr.Zero;
            info.pDevMode = IntPtr.Zero;

            if (!SetPrinterW(printer, 2, &info, 0))
                throw Fail("SetPrinter", Marshal.GetLastPInvokeError(), FindingCode.OperationFailed,
                    $"queue '{queueName}', {(driverName is null ? $"port '{portName}'" : $"driver '{driverName}'")}");
        }
    }

    private static FORM_INFO_1W UserForm(FormInfo form, char* name) => new()
    {
        Flags = FORM_USER,
        pName = (IntPtr)name,
        Size = new SIZEL { cx = form.WidthMicrometers, cy = form.HeightMicrometers },
        ImageableArea = new RECTL { left = form.ImageableLeft, top = form.ImageableTop, right = form.ImageableRight, bottom = form.ImageableBottom },
    };

    private static void ValidateFormName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > MaxFormNameLength)
            throw new PrinterSetupException(FindingCode.FormCreateFailed, $"Form name '{name}' must have 1 to {MaxFormNameLength} characters.");
    }

    /// <summary>Copies into a PORT_DATA_1 WCHAR array, which needs room for the terminating null.</summary>
    private static void CopyPortText(string value, ushort* destination, int capacity, string field)
    {
        if (value.Length >= capacity)
            throw new PrinterSetupException(FindingCode.PortCreateFailed, $"The {field} '{value}' is longer than {capacity - 1} characters.");
        value.AsSpan().CopyTo(new Span<char>(destination, capacity));
        destination[value.Length] = 0;
    }

    private static string FormContext(FormInfo form) =>
        $"form '{form.Name}', {form.WidthMicrometers} x {form.HeightMicrometers} µm";

    private static Dictionary<string, object?> FormData(FormInfo form) =>
        Data(("form", form.Name), ("widthMicrometers", form.WidthMicrometers), ("heightMicrometers", form.HeightMicrometers));
}
