using System.Runtime.InteropServices;

namespace Amwag.PrinterSetup.Windows.Interop;

// Blittable mirrors of the Unicode structures in winspool.h / tcpxcv.h (Windows SDK 10.0.26100, default packing).
// Field names follow the headers so they can be compared line by line; layouts are pinned by the layout tests.
internal static unsafe partial class Winspool
{
    [StructLayout(LayoutKind.Sequential)]
    public struct PRINTER_DEFAULTSW
    {
        public IntPtr pDatatype;
        public IntPtr pDevMode;
        public uint DesiredAccess;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PRINTER_INFO_2W
    {
        public IntPtr pServerName;
        public IntPtr pPrinterName;
        public IntPtr pShareName;
        public IntPtr pPortName;
        public IntPtr pDriverName;
        public IntPtr pComment;
        public IntPtr pLocation;
        public IntPtr pDevMode;
        public IntPtr pSepFile;
        public IntPtr pPrintProcessor;
        public IntPtr pDatatype;
        public IntPtr pParameters;
        public IntPtr pSecurityDescriptor;
        public uint Attributes;
        public uint Priority;
        public uint DefaultPriority;
        public uint StartTime;
        public uint UntilTime;
        public uint Status;
        public uint cJobs;
        public uint AveragePPM;
    }

    /// <summary>Global default DEVMODE (GetPrinter/SetPrinter level 8).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PRINTER_INFO_8W
    {
        public IntPtr pDevMode;
    }

    /// <summary>Per-user default DEVMODE (GetPrinter/SetPrinter level 9).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PRINTER_INFO_9W
    {
        public IntPtr pDevMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZEL
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECTL
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    /// <summary>Size and ImageableArea are in thousandths of a millimeter.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FORM_INFO_1W
    {
        public uint Flags;
        public IntPtr pName;
        public SIZEL Size;
        public RECTL ImageableArea;
    }

    /// <summary>DC_PAPERSIZE entries: portrait width (x) and length (y) in tenths of a millimeter.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PORT_INFO_2W
    {
        public IntPtr pPortName;
        public IntPtr pMonitorName;
        public IntPtr pDescription;
        public uint fPortType;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEMTIME
    {
        public ushort wYear;
        public ushort wMonth;
        public ushort wDayOfWeek;
        public ushort wDay;
        public ushort wHour;
        public ushort wMinute;
        public ushort wSecond;
        public ushort wMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DRIVER_INFO_3W
    {
        public uint cVersion;
        public IntPtr pName;
        public IntPtr pEnvironment;
        public IntPtr pDriverPath;
        public IntPtr pDataFile;
        public IntPtr pConfigFile;
        public IntPtr pHelpFile;
        public IntPtr pDependentFiles;
        public IntPtr pMonitorName;
        public IntPtr pDefaultDataType;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DRIVER_INFO_6W
    {
        public uint cVersion;
        public IntPtr pName;
        public IntPtr pEnvironment;
        public IntPtr pDriverPath;
        public IntPtr pDataFile;
        public IntPtr pConfigFile;
        public IntPtr pHelpFile;
        public IntPtr pDependentFiles;
        public IntPtr pMonitorName;
        public IntPtr pDefaultDataType;
        public IntPtr pszzPreviousNames;
        public FILETIME ftDriverDate;
        public ulong dwlDriverVersion;
        public IntPtr pszMfgName;
        public IntPtr pszOEMUrl;
        public IntPtr pszHardwareID;
        public IntPtr pszProvider;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DRIVER_INFO_8W
    {
        public uint cVersion;
        public IntPtr pName;
        public IntPtr pEnvironment;
        public IntPtr pDriverPath;
        public IntPtr pDataFile;
        public IntPtr pConfigFile;
        public IntPtr pHelpFile;
        public IntPtr pDependentFiles;
        public IntPtr pMonitorName;
        public IntPtr pDefaultDataType;
        public IntPtr pszzPreviousNames;
        public FILETIME ftDriverDate;
        public ulong dwlDriverVersion;
        public IntPtr pszMfgName;
        public IntPtr pszOEMUrl;
        public IntPtr pszHardwareID;
        public IntPtr pszProvider;
        public IntPtr pszPrintProcessor;
        public IntPtr pszVendorSetup;
        public IntPtr pszzColorProfiles;
        public IntPtr pszInfPath;
        public uint dwPrinterDriverAttributes;
        public IntPtr pszzCoreDriverDependencies;
        public FILETIME ftMinInboxDriverVerDate;
        public ulong dwlMinInboxDriverVerVersion;
    }

    /// <summary>Submitted is UTC.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct JOB_INFO_1W
    {
        public uint JobId;
        public IntPtr pPrinterName;
        public IntPtr pMachineName;
        public IntPtr pUserName;
        public IntPtr pDocument;
        public IntPtr pDatatype;
        public IntPtr pStatus;
        public uint Status;
        public uint Priority;
        public uint Position;
        public uint TotalPages;
        public uint PagesPrinted;
        public SYSTEMTIME Submitted;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct JOB_INFO_2W
    {
        public uint JobId;
        public IntPtr pPrinterName;
        public IntPtr pMachineName;
        public IntPtr pUserName;
        public IntPtr pDocument;
        public IntPtr pNotifyName;
        public IntPtr pDatatype;
        public IntPtr pPrintProcessor;
        public IntPtr pParameters;
        public IntPtr pDriverName;
        public IntPtr pDevMode;
        public IntPtr pStatus;
        public IntPtr pSecurityDescriptor;
        public uint Status;
        public uint Priority;
        public uint Position;
        public uint StartTime;
        public uint UntilTime;
        public uint TotalPages;
        public uint Size;
        public SYSTEMTIME Submitted;
        public uint Time;
        public uint PagesPrinted;
    }

    /// <summary>
    /// tcpxcv.h PORT_DATA_1 for XcvData "AddPort". WCHAR arrays are ushort buffers so the struct stays blittable;
    /// lengths include the terminating null (MAX_PORTNAME_LEN is "63 +1", and so on).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PORT_DATA_1
    {
        public fixed ushort sztPortName[MAX_PORTNAME_LEN];
        public uint dwVersion;
        public uint dwProtocol;
        public uint cbSize;
        public uint dwReserved;
        public fixed ushort sztHostAddress[MAX_NETWORKNAME_LEN];
        public fixed ushort sztSNMPCommunity[MAX_SNMP_COMMUNITY_STR_LEN];
        public uint dwDoubleSpool;
        public fixed ushort sztQueue[MAX_QUEUENAME_LEN];
        public fixed ushort sztIPAddress[MAX_IPADDR_STR_LEN];
        public fixed byte Reserved[540];
        public uint dwPortNumber;
        public uint dwSNMPEnabled;
        public uint dwSNMPDevIndex;
    }
}
