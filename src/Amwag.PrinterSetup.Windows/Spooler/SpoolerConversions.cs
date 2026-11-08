using System.ComponentModel;
using System.Text.RegularExpressions;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Windows.Interop;

namespace Amwag.PrinterSetup.Windows.Spooler;

/// <summary>Pure conversions from spooler data to the Core records (no system calls; unit tested).</summary>
internal static partial class SpoolerConversions
{
    // PORT_INFO_2 strings come from each port monitor, not from the registry key names under Print\Monitors: Windows
    // reports local ports as monitor "Local Monitor" with description "Local Port" (seen on Windows 11), and Standard
    // TCP/IP ports as monitor "TCPMON.DLL" with description "Standard TCP/IP Port" (Get-PrinterPort listing in
    // https://tech.guitarrapc.com/entry/2013/12/29/184608). The LPR Port monitor (the "LPR Port Monitor" Windows feature)
    // describes its ports as "LPR Port" and names them "<server>:<queue>"; the Internet print provider's ports (IPP over HTTP)
    // are described as "Internet Port" and named by their URL, which is checked as well because descriptions may be localized.
    // Both fields are compared.
    private static readonly string[] UsbNames = ["USB Monitor"];
    private static readonly string[] StandardTcpIpNames = ["Standard TCP/IP Port", "TCPMON.DLL"];
    private static readonly string[] WsdNames = ["WSD Port"];
    private static readonly string[] LprNames = ["LPR Port"];
    private static readonly string[] InternetNames = ["Internet Port"];
    private static readonly string[] LocalNames = ["Local Port", "Local Monitor"];

    public static PortKind ClassifyPort(string portName, string? monitorName, string? description)
    {
        if (Matches(UsbNames, monitorName, description) || UsbPortName().IsMatch(portName))
            return PortKind.Usb;
        if (Matches(StandardTcpIpNames, monitorName, description))
            return PortKind.StandardTcpIp;
        if (Matches(WsdNames, monitorName, description) || portName.StartsWith("WSD-", StringComparison.OrdinalIgnoreCase))
            return PortKind.Wsd;
        if (Matches(LprNames, monitorName, description))
            return PortKind.Lpr;
        if (Matches(InternetNames, monitorName, description) || HttpUrl().IsMatch(portName))
            return PortKind.Internet;
        if (Matches(LocalNames, monitorName, description))
            return PortKind.Local;
        return PortKind.Other;
    }

    public static FormKind ToFormKind(uint flags) => flags switch
    {
        Winspool.FORM_BUILTIN => FormKind.Builtin,
        Winspool.FORM_PRINTER => FormKind.Printer,
        _ => FormKind.User,
    };

    /// <summary>DRIVER_INFO dwlDriverVersion: four 16-bit parts, most significant first; zero means not reported.</summary>
    public static Version? ToVersion(ulong packed) =>
        packed == 0
            ? null
            : new Version((int)(packed >> 48), (int)((packed >> 32) & 0xFFFF), (int)((packed >> 16) & 0xFFFF), (int)(packed & 0xFFFF));

    /// <summary>DRIVER_INFO ftDriverDate (UTC); zero means not reported.</summary>
    public static DateTime? ToDate(Winspool.FILETIME time)
    {
        long ticks = ((long)time.dwHighDateTime << 32) | time.dwLowDateTime;
        return ticks <= 0 || ticks > DateTime.MaxValue.ToFileTimeUtc() ? null : DateTime.FromFileTimeUtc(ticks);
    }

    /// <summary>JOB_INFO Submitted is UTC; an empty or invalid SYSTEMTIME yields null.</summary>
    public static DateTime? ToUtc(Winspool.SYSTEMTIME time)
    {
        if (time.wYear == 0)
            return null;
        try
        {
            return new DateTime(time.wYear, time.wMonth, time.wDay, time.wHour, time.wMinute, time.wSecond, time.wMilliseconds, DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>RPC failures mean the spooler is unreachable and access denied means elevation is needed, whatever the call.</summary>
    public static FindingCode CodeFor(int win32Error, FindingCode fallback) => win32Error switch
    {
        1722 or 1726 or 1727 or 1753 => FindingCode.SpoolerUnavailable,
        Winspool.ERROR_ACCESS_DENIED => FindingCode.ElevationRequired,
        _ => fallback,
    };

    public static SpoolerException Failure(string operation, int win32Error, FindingCode code, string? context = null)
    {
        string message = win32Error == 0 ? "The call failed without reporting an error code." : new Win32Exception(win32Error).Message;
        if (!string.IsNullOrEmpty(context))
            message = $"{message} [{context}]";
        return new SpoolerException(operation, win32Error, message, CodeFor(win32Error, code));
    }

    public static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static bool Matches(string[] names, string? monitorName, string? description) =>
        names.Any(name => string.Equals(name, monitorName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, description, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex("^USB[0-9]{3}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UsbPortName();

    [GeneratedRegex("^https?://", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HttpUrl();
}
