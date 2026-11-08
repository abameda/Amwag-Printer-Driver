using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Amwag.PrinterSetup.Windows.Interop;

/// <summary>Token queries and registry access rights (advapi32.dll; values from the Windows SDK 10.0.26100 winnt.h).</summary>
internal static partial class Advapi32
{
    private const string Library = "advapi32.dll";

    internal const uint KEY_QUERY_VALUE = 0x0001;

    internal enum TOKEN_INFORMATION_CLASS
    {
        TokenElevation = 20,
    }

    /// <summary>
    /// TOKEN_ELEVATION is a single DWORD (TokenIsElevated), so it is returned in <paramref name="information"/> with
    /// <paramref name="informationLength"/> 4.
    /// </summary>
    [DllImport(Library, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        TOKEN_INFORMATION_CLASS informationClass,
        out uint information,
        uint informationLength,
        out uint returnLength);
}
