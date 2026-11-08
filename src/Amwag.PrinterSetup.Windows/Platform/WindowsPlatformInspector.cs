using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Errors;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Windows.Interop;
using Microsoft.Win32;

namespace Amwag.PrinterSetup.Windows.Platform;

/// <summary>Operating system, elevation and print-policy facts from the registry and the process token.</summary>
public sealed class WindowsPlatformInspector : IPlatformInspector
{
    // Windows 11 still reports ProductName "Windows 10 ..."; its builds start at 22000 (version 21H2).
    internal const int FirstWindows11Build = 22000;

    private const string CurrentVersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    // https://learn.microsoft.com/windows/client-management/mdm/policy-csp-printers#restrictdriverinstallationtoadministrators
    private const string PointAndPrintPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows NT\Printers\PointAndPrint";
    private const string RestrictDriverInstallationValue = "RestrictDriverInstallationToAdministrators";

    /// <summary>Throws <see cref="PrinterSetupException"/> when the OS version or the process token cannot be read.</summary>
    public PlatformInfo Inspect()
    {
        using RegistryKey localMachine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);
        OsVersion version = ReadCurrentVersion(localMachine);
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var (protectedPrintEnabled, protectedPrintDetail) = ProtectedPrintMode.Determine(version.Build, localMachine);

        return new PlatformInfo
        {
            ProductName = NormalizeProductName(version.ProductName, version.Build),
            DisplayVersion = version.DisplayVersion,
            Build = version.Build,
            Ubr = version.Ubr,
            Architecture = ArchitectureName(RuntimeInformation.OSArchitecture),
            ProcessArchitecture = ArchitectureName(RuntimeInformation.ProcessArchitecture),
            MachineName = Environment.MachineName,
            UserName = identity.Name,
            IsElevated = IsElevated(identity),
            IsSystemAccount = identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true,
            IsInteractive = Environment.UserInteractive,
            ProtectedPrintModeEnabled = protectedPrintEnabled,
            ProtectedPrintModeDetail = protectedPrintDetail,
            // Not configured (null) means the Windows default, which also limits print driver installation to administrators.
            RestrictDriverInstallationToAdministrators = ReadPolicyFlag(localMachine, PointAndPrintPolicyKey, RestrictDriverInstallationValue),
        };
    }

    internal static string NormalizeProductName(string productName, int build)
    {
        const string Windows10 = "Windows 10";
        return build >= FirstWindows11Build && productName.StartsWith(Windows10, StringComparison.OrdinalIgnoreCase)
            ? "Windows 11" + productName[Windows10.Length..]
            : productName;
    }

    internal static string ArchitectureName(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => architecture.ToString().ToLowerInvariant(),
    };

    /// <summary>A DWORD policy as a flag; null when it is not configured (or cannot be read), so the Windows default applies.</summary>
    internal static bool? ReadPolicyFlag(RegistryKey baseKey, string keyPath, string valueName)
    {
        try
        {
            using RegistryKey? key = baseKey.OpenSubKey(keyPath);
            return key?.GetValue(valueName) is int value ? value != 0 : null;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static OsVersion ReadCurrentVersion(RegistryKey localMachine)
    {
        try
        {
            using RegistryKey? key = localMachine.OpenSubKey(CurrentVersionKey);
            int build = int.TryParse(key?.GetValue("CurrentBuildNumber") as string, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0
                ? parsed
                : Environment.OSVersion.Version.Build;
            return new OsVersion(
                key?.GetValue("ProductName") as string ?? "",
                key?.GetValue("DisplayVersion") as string,
                build,
                key?.GetValue("UBR") is int ubr ? ubr : 0);
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            throw new PrinterSetupException(FindingCode.OperationFailed, $@"Reading HKLM\{CurrentVersionKey} failed: {ex.Message}", inner: ex);
        }
    }

    private static bool IsElevated(WindowsIdentity identity)
    {
        if (Advapi32.GetTokenInformation(identity.AccessToken, Advapi32.TOKEN_INFORMATION_CLASS.TokenElevation, out uint elevated, sizeof(uint), out _))
            return elevated != 0;

        int error = Marshal.GetLastPInvokeError();
        throw new PrinterSetupException(FindingCode.OperationFailed, $"GetTokenInformation(TokenElevation) failed (Win32 error {error}).", error);
    }

    private readonly record struct OsVersion(string ProductName, string? DisplayVersion, int Build, int Ubr);
}
