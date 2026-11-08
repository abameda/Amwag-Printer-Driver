using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Units;

namespace Amwag.PrinterSetup.Core.Inventory;

public enum PnpBus { Usb, Wsd, Lpt, Other }

/// <summary>A Plug and Play printer device node (USBPRINT\, WSDPRINT\, LPTENUM\ ...).</summary>
public sealed record PnpPrinterDevice
{
    public required string InstanceId { get; init; }
    public PnpBus Bus { get; init; }
    public IReadOnlyList<string> HardwareIds { get; init; } = [];
    public IReadOnlyList<string> CompatibleIds { get; init; } = [];
    public string? FriendlyName { get; init; }
    public string? Description { get; init; }
    public string? Manufacturer { get; init; }
    public string? DeviceClass { get; init; }
    public bool IsPresent { get; init; }
    /// <summary>CM_PROB_* code when the device has a problem (28 = drivers not installed); null when none.</summary>
    public int? ProblemCode { get; init; }
    public string? DriverDescription { get; init; }
    public string? DriverVersion { get; init; }
    public string? ParentInstanceId { get; init; }
    /// <summary>USB monitor port (e.g. USB001) bound to this device, when known.</summary>
    public string? UsbPortName { get; init; }
}

/// <summary>A Windows print queue (PRINTER_INFO_2).</summary>
public sealed record PrintQueueInfo
{
    public required string Name { get; init; }
    public string DriverName { get; init; } = "";
    public string PortName { get; init; } = "";
    /// <summary>Non-null for connections to a print server (\\server\queue); such queues are server-managed.</summary>
    public string? ServerName { get; init; }
    public string? ShareName { get; init; }
    public string? Location { get; init; }
    public string? Comment { get; init; }
    public string? PrintProcessor { get; init; }
    public string? Datatype { get; init; }
    public PrinterAttributes Attributes { get; init; }
    public PrinterStatusFlags Status { get; init; }
    public int JobCount { get; init; }

    public bool IsServerConnection => !string.IsNullOrEmpty(ServerName);
    public bool IsWorkOffline => (Attributes & PrinterAttributes.WorkOffline) != 0;
}

/// <summary>What a print port sends to, as its port monitor reports it.</summary>
public enum PortKind
{
    /// <summary>A USB monitor port (USB001, ...) bound to one USB printer.</summary>
    Usb,
    /// <summary>A Standard TCP/IP port; its host address and port number are known.</summary>
    StandardTcpIp,
    /// <summary>A WSD port; the printer's network address is not known.</summary>
    Wsd,
    /// <summary>A file, an LPT or COM port, a redirected share or another port of the local port monitor.</summary>
    Local,
    /// <summary>Any other port monitor's port.</summary>
    Other,
    /// <summary>An LPR Port monitor port, named "&lt;host&gt;:&lt;queue&gt;" after the LPD server and its queue.</summary>
    Lpr,
    /// <summary>An Internet Port (IPP over HTTP), named by its URL: "http(s)://&lt;host&gt;[:&lt;port&gt;]/...".</summary>
    Internet,
}

public enum TcpPortProtocol { Raw = 1, Lpr = 2 }

/// <summary>A print port (EnumPorts level 2, enriched with Standard TCP/IP monitor settings).</summary>
public sealed record PrinterPortInfo
{
    public required string Name { get; init; }
    public string? MonitorName { get; init; }
    public string? Description { get; init; }
    public PortKind Kind { get; init; }
    public string? HostAddress { get; init; }
    public int? PortNumber { get; init; }
    public TcpPortProtocol? Protocol { get; init; }
    public bool? SnmpEnabled { get; init; }
}

/// <summary>An installed printer driver (EnumPrinterDrivers level 8 or best available).</summary>
public sealed record PrinterDriverInfo
{
    public required string Name { get; init; }
    public string Environment { get; init; } = "";
    /// <summary>Driver model (cVersion): 3 = v3, 4 = v4.</summary>
    public int DriverModel { get; init; }
    public Version? Version { get; init; }
    public DateTime? Date { get; init; }
    public string? Manufacturer { get; init; }
    public string? Provider { get; init; }
    public string? HardwareId { get; init; }
    public string? InfPath { get; init; }
    public string? PrintProcessor { get; init; }
}

public enum FormKind { User = 0, Builtin = 1, Printer = 2 }

/// <summary>A form from the print server forms database; sizes in micrometers (thousandths of a millimeter).</summary>
public sealed record FormInfo(
    string Name,
    FormKind Kind,
    int WidthMicrometers,
    int HeightMicrometers,
    int ImageableLeft,
    int ImageableTop,
    int ImageableRight,
    int ImageableBottom)
{
    /// <summary>A user form whose imageable area is the whole sheet.</summary>
    public static FormInfo UserForm(string name, PaperDimensions size) =>
        new(name, FormKind.User, size.WidthMicrometers, size.HeightMicrometers, 0, 0, size.WidthMicrometers, size.HeightMicrometers);

    public bool HasSize(PaperDimensions size) =>
        WidthMicrometers == size.WidthMicrometers && HeightMicrometers == size.HeightMicrometers;

    public bool HasFullImageableArea =>
        ImageableLeft == 0 && ImageableTop == 0 && ImageableRight == WidthMicrometers && ImageableBottom == HeightMicrometers;
}

public enum ServiceRunState { NotInstalled, Stopped, StartPending, StopPending, Running, Paused, Unknown }

public enum ServiceStartMode { Automatic, Manual, Disabled, Unknown }

public sealed record SpoolerServiceStatus(ServiceRunState State, ServiceStartMode StartMode, string? Detail = null)
{
    public bool IsRunning => State == ServiceRunState.Running;
}

/// <summary>Operating system, process and print-policy facts.</summary>
public sealed record PlatformInfo
{
    public string ProductName { get; init; } = "";
    public string? DisplayVersion { get; init; }
    public int Build { get; init; }
    public int Ubr { get; init; }
    /// <summary>OS architecture: x64, arm64 or x86.</summary>
    public string Architecture { get; init; } = "";
    public string ProcessArchitecture { get; init; } = "";
    public string MachineName { get; init; } = "";
    public string UserName { get; init; } = "";
    public bool IsElevated { get; init; }
    public bool IsSystemAccount { get; init; }
    public bool IsInteractive { get; init; }
    /// <summary>Windows protected print mode (blocks third-party drivers); null when it could not be determined.</summary>
    public bool? ProtectedPrintModeEnabled { get; init; }
    public string? ProtectedPrintModeDetail { get; init; }
    /// <summary>Point and Print RestrictDriverInstallationToAdministrators policy, informational.</summary>
    public bool? RestrictDriverInstallationToAdministrators { get; init; }
}

/// <summary>Identity a network printer reports about itself (SNMP), plus reachability of the raw print port.</summary>
public sealed record NetworkIdentity
{
    public required string Address { get; init; }
    /// <summary>The IP address the probe contacted (a host name's resolved address, or the IP literal itself); null when none was.</summary>
    public string? ResolvedAddress { get; init; }
    public bool RawPortReachable { get; init; }
    public bool SnmpResponded { get; init; }
    public string? SysDescr { get; init; }
    public string? SysObjectId { get; init; }
    public string? DeviceDescription { get; init; }
    public string? PrinterName { get; init; }
    public string? Ieee1284DeviceId { get; init; }
    /// <summary>Model name from the Kyocera private MIB (kcprtGeneralModelName), which TA/UTAX devices also implement.</summary>
    public string? VendorModelName { get; init; }
    public string? SerialNumber { get; init; }
    public string? Error { get; init; }
}

/// <summary>A spooled job (JOB_INFO_1).</summary>
public sealed record PrintJobInfo
{
    public int JobId { get; init; }
    public string? Document { get; init; }
    public string? UserName { get; init; }
    public JobStatusFlags Status { get; init; }
    public string? StatusText { get; init; }
    public int TotalPages { get; init; }
    public int PagesPrinted { get; init; }
    public DateTime? Submitted { get; init; }
}

/// <summary>A read-only picture of everything detection needs, captured at one moment.</summary>
public sealed record SystemSnapshot
{
    public DateTimeOffset CapturedAt { get; init; }
    public PlatformInfo Platform { get; init; } = new();
    public SpoolerServiceStatus Spooler { get; init; } = new(ServiceRunState.Unknown, ServiceStartMode.Unknown);
    public IReadOnlyList<PnpPrinterDevice> Devices { get; init; } = [];
    public IReadOnlyList<PrintQueueInfo> Queues { get; init; } = [];
    public IReadOnlyList<PrinterPortInfo> Ports { get; init; } = [];
    public IReadOnlyList<PrinterDriverInfo> Drivers { get; init; } = [];
    public IReadOnlyList<FormInfo> Forms { get; init; } = [];
    public IReadOnlyList<NetworkIdentity> NetworkIdentities { get; init; } = [];
    /// <summary>Sources that could not be read (for example the spooler being stopped), as technical text.</summary>
    public IReadOnlyList<string> CaptureErrors { get; init; } = [];
}
