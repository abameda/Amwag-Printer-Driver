using System.Runtime.InteropServices;

namespace Amwag.PrinterSetup.App.UI;

/// <summary>Whether Windows' "Animation effects" setting is off; animated controls then stand still.</summary>
internal static class Motion
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    public static bool Reduced
    {
        get
        {
            try
            {
                return SystemParametersInfo(SpiGetClientAreaAnimation, 0, out int enabled, 0) && enabled == 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true, ExactSpelling = true, EntryPoint = "SystemParametersInfoW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);
}
