using System.Runtime.InteropServices;

namespace Amwag.PrinterSetup.Windows.Interop;

/// <summary>newdev.dll: driver installation on present Plug and Play devices (newdev.h, SetupAPI.h of the Windows SDK 10.0.26100).</summary>
internal static class NewDev
{
    private const string Dll = "newdev.dll";

    internal const uint INSTALLFLAG_FORCE = 0x00000001;
    internal const uint INSTALLFLAG_NONINTERACTIVE = 0x00000004;

    /// <summary>APPLICATION_ERROR_MASK | ERROR_SEVERITY_ERROR | 0x20B: no present device has the hardware ID.</summary>
    internal const int ERROR_NO_SUCH_DEVINST = unchecked((int)0xE000020B);

    /// <summary>Returns nonzero when a device was updated; otherwise the error is in GetLastError.</summary>
    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern int UpdateDriverForPlugAndPlayDevicesW(
        IntPtr hwndParent,
        string hardwareId,
        string fullInfPath,
        uint installFlags,
        out int rebootRequired);
}
