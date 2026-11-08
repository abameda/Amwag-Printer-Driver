using System.Runtime.InteropServices;

namespace Amwag.PrinterSetup.Windows.Interop;

/// <summary>
/// winspool.drv: package-aware printer driver installation (winspool.h of the Windows SDK 10.0.26100). Both functions
/// return an HRESULT and need administrator rights.
/// </summary>
internal static class DriverStoreNative
{
    private const string Dll = "winspool.drv";

    internal const uint UPDP_SILENT_UPLOAD = 0x00000001;

    /// <summary>
    /// Stages the package of <paramref name="pszInfPath"/> in the driver store. <paramref name="pcchDestInfPath"/> is the
    /// buffer size in characters on input and the path length including the terminating null on output.
    /// </summary>
    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern unsafe int UploadPrinterDriverPackageW(
        string? pszServer,
        string pszInfPath,
        string? pszEnvironment,
        uint dwFlags,
        IntPtr hwnd,
        char* pszDestInfPath,
        ref uint pcchDestInfPath);

    /// <summary>Installs one driver (INF model name) from a package that is already in the driver store.</summary>
    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern int InstallPrinterDriverFromPackageW(
        string? pszServer,
        string pszInfPath,
        string pszDriverName,
        string? pszEnvironment,
        uint dwFlags);
}
