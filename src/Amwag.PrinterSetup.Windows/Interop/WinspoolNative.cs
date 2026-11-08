using System.Runtime.InteropServices;

namespace Amwag.PrinterSetup.Windows.Interop;

/// <summary>
/// winspool.drv declarations for the print spooler (Unicode entry points; signatures and constants from winspool.h,
/// wingdi.h and tcpxcv.h of the Windows SDK 10.0.26100). Sizes and counts declared as DWORD are passed as int.
/// </summary>
internal static unsafe partial class Winspool
{
    private const string Dll = "winspool.drv";

    // Access rights (winspool.h; MS-RPRN 2.2.3.1).
    public const uint SERVER_ACCESS_ADMINISTER = 0x00000001;
    public const uint SERVER_ACCESS_ENUMERATE = 0x00000002;
    public const uint PRINTER_ACCESS_ADMINISTER = 0x00000004;
    public const uint PRINTER_ACCESS_USE = 0x00000008;
    public const uint PRINTER_ALL_ACCESS = 0x000F000C;

    public const uint PRINTER_ENUM_LOCAL = 0x00000002;
    public const uint PRINTER_ENUM_CONNECTIONS = 0x00000004;

    public const uint FORM_USER = 0x00000000;
    public const uint FORM_BUILTIN = 0x00000001;
    public const uint FORM_PRINTER = 0x00000002;

    // DocumentProperties fMode (wingdi.h: DM_OUT_BUFFER = DM_COPY, DM_IN_BUFFER = DM_MODIFY).
    public const uint DM_OUT_BUFFER = 2;
    public const uint DM_IN_BUFFER = 8;
    public const int IDOK = 1;

    // DeviceCapabilities fwCapability (wingdi.h).
    public const ushort DC_PAPERS = 2;
    public const ushort DC_PAPERSIZE = 3;
    public const ushort DC_MINEXTENT = 4;
    public const ushort DC_MAXEXTENT = 5;
    public const ushort DC_BINS = 6;
    public const ushort DC_DUPLEX = 7;
    public const ushort DC_DRIVER = 11;
    public const ushort DC_BINNAMES = 12;
    public const ushort DC_PAPERNAMES = 16;
    public const ushort DC_ORIENTATION = 17;
    public const ushort DC_MEDIAREADY = 29;

    /// <summary>Characters per DC_PAPERNAMES and DC_MEDIAREADY entry (not null-terminated when full).</summary>
    public const int PaperNameChars = 64;

    /// <summary>Characters per DC_BINNAMES entry (not null-terminated when full).</summary>
    public const int BinNameChars = 24;

    // tcpxcv.h
    public const string StandardTcpIpXcvMonitor = ",XcvMonitor Standard TCP/IP Port";
    public const string XcvAddPort = "AddPort";
    public const uint PORT_DATA_1_VERSION = 1;
    public const uint PROTOCOL_RAWTCP_TYPE = 1;
    public const int MAX_PORTNAME_LEN = 64;
    public const int MAX_NETWORKNAME_LEN = 49;
    public const int MAX_SNMP_COMMUNITY_STR_LEN = 33;
    public const int MAX_QUEUENAME_LEN = 33;
    public const int MAX_IPADDR_STR_LEN = 16;

    // Win32 errors (winerror.h).
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_INVALID_DATA = 13;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;
    public const int ERROR_INVALID_LEVEL = 124;

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool OpenPrinterW(string? pPrinterName, out SafePrinterHandle phPrinter, ref PRINTER_DEFAULTSW pDefault);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool EnumPrintersW(uint Flags, string? Name, uint Level, IntPtr pPrinterEnum, int cbBuf, out int pcbNeeded, out int pcReturned);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool EnumPortsW(string? pName, uint Level, IntPtr pPort, int cbBuf, out int pcbNeeded, out int pcReturned);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool EnumPrinterDriversW(string? pName, string? pEnvironment, uint Level, IntPtr pDriverInfo, int cbBuf, out int pcbNeeded, out int pcReturned);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool EnumFormsW(SafePrinterHandle hPrinter, uint Level, IntPtr pForm, int cbBuf, out int pcbNeeded, out int pcReturned);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool AddFormW(SafePrinterHandle hPrinter, uint Level, ref FORM_INFO_1W pForm);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool SetFormW(SafePrinterHandle hPrinter, string pFormName, uint Level, ref FORM_INFO_1W pForm);

    /// <summary>The BOOL reports the spooler call; <paramref name="pdwStatus"/> receives the port monitor's own Win32 status.</summary>
    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool XcvDataW(
        SafePrinterHandle hXcv,
        string pszDataName,
        void* pInputData,
        int cbInputData,
        void* pOutputData,
        int cbOutputData,
        out int pcbOutputNeeded,
        out int pdwStatus);

    /// <summary>Returns the new printer's handle (PRINTER_ALL_ACCESS), invalid on failure.</summary>
    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern SafePrinterHandle AddPrinterW(string? pName, uint Level, ref PRINTER_INFO_2W pPrinter);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool GetPrinterW(SafePrinterHandle hPrinter, uint Level, IntPtr pPrinter, int cbBuf, out int pcbNeeded);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool SetPrinterW(SafePrinterHandle hPrinter, uint Level, void* pPrinter, uint Command);

    /// <summary>fMode 0 returns the driver's full DEVMODE size; otherwise IDOK on success and a negative value on failure.</summary>
    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int DocumentPropertiesW(IntPtr hWnd, SafePrinterHandle hPrinter, string pDeviceName, void* pDevModeOutput, void* pDevModeInput, uint fMode);

    /// <summary>Exported by winspool.drv although declared in wingdi.h; -1 means unsupported or failed.</summary>
    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int DeviceCapabilitiesW(string pDevice, string? pPort, ushort fwCapability, void* pOutput, void* pDevMode);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool EnumJobsW(SafePrinterHandle hPrinter, uint FirstJob, uint NoJobs, uint Level, IntPtr pJob, int cbBuf, out int pcbNeeded, out int pcReturned);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern bool GetJobW(SafePrinterHandle hPrinter, uint JobId, uint Level, IntPtr pJob, int cbBuf, out int pcbNeeded);
}
