using System.Globalization;
using System.Runtime.InteropServices;

namespace Amwag.PrinterSetup.Windows.Drivers;

/// <summary>Formats Win32 errors, HRESULTs and SetupAPI codes for logs and exception messages.</summary>
internal static class NativeErrorText
{
    private const uint SetupApiErrorMask = 0x20000000 | 0xC0000000;

    public static string Hex(int code) => "0x" + code.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>The system message for the code, in the Windows display language; never empty.</summary>
    public static string Describe(int code)
    {
        // SetupAPI codes (0xE0000xxx) have no message of their own; their HRESULT_FROM_SETUPAPI form (0x800Fxxxx) does.
        int lookup = ((uint)code & SetupApiErrorMask) == SetupApiErrorMask
            ? unchecked((int)(((uint)code & 0x0000FFFF) | 0x800F0000))
            : code;
        string message = Marshal.GetPInvokeErrorMessage(lookup).Trim();
        return message.Length == 0 ? "No system description." : message;
    }

    /// <summary>"0x800B0100 (No signature was present in the subject.)".</summary>
    public static string Format(int code) => $"{Hex(code)} ({Describe(code)})";
}
