using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace Amwag.PrinterSetup.Windows.Platform;

/// <summary>
/// Windows protected print mode (Windows 11 24H2 and later) allows only inbox print drivers. It is turned on in Settings or
/// by the Group Policy "Configure Windows protected print"; the WinRT property WindowsProtectedPrintInfo.IsProtectedPrintEnabled
/// reports the effective state for both, the policy value only the policy.
/// </summary>
internal static class ProtectedPrintMode
{
    /// <summary>Windows 11 24H2; neither the mode nor its policy exists on earlier builds.</summary>
    internal const int FirstBuild = 26100;

    // https://learn.microsoft.com/windows/client-management/mdm/policy-csp-printers#configurewindowsprotectedprint
    private const string PolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\Printers\WPP";
    private const string PolicyValue = "WindowsProtectedPrintGroupPolicyState";
    private const string PolicyName = "Group Policy 'Configure Windows protected print'";

    private const string RuntimeClass = "Windows.Graphics.Printing.ProtectedPrint.WindowsProtectedPrintInfo";

    // IWindowsProtectedPrintInfoStatics, from windows.graphics.printing.protectedprint.h (Windows SDK 10.0.26100).
    private static readonly Guid StaticsIid = new(0xa7d212f3, 0x4168, 0x5485, 0x98, 0xab, 0xd8, 0x9d, 0x04, 0x60, 0x3b, 0x40);

    internal static (bool? Enabled, string Detail) Determine(int build, RegistryKey localMachine)
    {
        if (build < FirstBuild)
            return (false, $"Not available on build {build}; Windows protected print mode needs Windows 11 24H2 (build {FirstBuild}) or later.");

        (int? policyValue, string? policyError) = ReadPolicy(localMachine);
        int hr = QueryIsEnabled(out bool enabled);
        return Evaluate(policyValue, policyError, hr, enabled);
    }

    /// <summary>The WinRT answer is authoritative; the policy value decides only when WinRT cannot be queried.</summary>
    internal static (bool? Enabled, string Detail) Evaluate(int? policyValue, string? policyError, int queryHResult, bool queryEnabled)
    {
        string policy = DescribePolicy(policyValue, policyError);
        if (queryHResult >= 0)
            return (queryEnabled, $"WindowsProtectedPrintInfo.IsProtectedPrintEnabled reports {(queryEnabled ? "enabled" : "not enabled")}; {policy}.");
        if (policyValue == 1)
            return (true, $"{policy}; the effective state could not be queried (HRESULT 0x{queryHResult:X8}).");
        return (null, $"WindowsProtectedPrintInfo.IsProtectedPrintEnabled could not be queried (HRESULT 0x{queryHResult:X8}) and {policy}; " +
            "the mode can also be turned on in Settings.");
    }

    private static string DescribePolicy(int? value, string? readError) => (value, readError) switch
    {
        (_, not null) => $"the {PolicyName} could not be read ({readError})",
        (null, _) => $"{PolicyName} is not configured",
        (1, _) => $"{PolicyName} is enabled ({PolicyValue} = 1)",
        (0, _) => $"{PolicyName} is disabled ({PolicyValue} = 0)",
        _ => $"{PolicyName} has the unexpected value {PolicyValue} = {value}",
    };

    private static (int? Value, string? Error) ReadPolicy(RegistryKey localMachine)
    {
        try
        {
            using RegistryKey? key = localMachine.OpenSubKey(PolicyKey);
            return (key?.GetValue(PolicyValue) as int?, null);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>Reads the static WinRT property through its activation factory (no WinRT projection is referenced); returns the HRESULT.</summary>
    private static unsafe int QueryIsEnabled(out bool enabled)
    {
        enabled = false;
        int hr = WindowsCreateString(RuntimeClass, (uint)RuntimeClass.Length, out IntPtr className);
        if (hr < 0)
            return hr;

        try
        {
            hr = RoGetActivationFactory(className, StaticsIid, out IntPtr factory);
            if (hr < 0)
                return hr;

            try
            {
                // IInspectable occupies vtable slots 0-5; get_IsProtectedPrintEnabled(boolean*) is slot 6.
                var getIsProtectedPrintEnabled = (delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)(*(void***)factory)[6];
                byte value = 0;
                hr = getIsProtectedPrintEnabled(factory, &value);
                enabled = hr >= 0 && value != 0;
                return hr;
            }
            finally
            {
                Marshal.Release(factory);
            }
        }
        finally
        {
            WindowsDeleteString(className);
        }
    }

    [DllImport("combase.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern int WindowsCreateString(string sourceString, uint length, out IntPtr hstring);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);
}
