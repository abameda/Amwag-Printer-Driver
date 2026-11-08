using System.Runtime.InteropServices;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Windows.Platform;
using Microsoft.Win32;

namespace Amwag.PrinterSetup.Windows.Tests.Platform;

public sealed class PlatformInspectorTests
{
    private const int ClassNotRegistered = unchecked((int)0x80040154);

    [Theory]
    [InlineData("Windows 10 Pro", 22631, "Windows 11 Pro")]
    [InlineData("Windows 10 Home Single Language", 26300, "Windows 11 Home Single Language")]
    [InlineData("Windows 10 Enterprise", 22000, "Windows 11 Enterprise")]
    [InlineData("Windows 10 Pro", 19045, "Windows 10 Pro")]
    [InlineData("Windows Server 2025 Standard", 26100, "Windows Server 2025 Standard")]
    [InlineData("", 26100, "")]
    public void Names_windows_11_by_build_number(string registryName, int build, string expected)
    {
        Assert.Equal(expected, WindowsPlatformInspector.NormalizeProductName(registryName, build));
    }

    [Theory]
    [InlineData(Architecture.X64, "x64")]
    [InlineData(Architecture.Arm64, "arm64")]
    [InlineData(Architecture.X86, "x86")]
    [InlineData(Architecture.Arm, "arm")]
    public void Names_architectures(Architecture architecture, string expected)
    {
        Assert.Equal(expected, WindowsPlatformInspector.ArchitectureName(architecture));
    }

    [Fact]
    public void Protected_print_mode_does_not_exist_before_24H2()
    {
        (bool? enabled, string detail) = ProtectedPrintMode.Determine(22631, Registry.LocalMachine);

        Assert.False(enabled);
        Assert.Contains("22631", detail);
    }

    [Theory]
    [InlineData(null, null, 0, false, false, "not configured")]
    [InlineData(null, null, 0, true, true, "reports enabled")]
    [InlineData(1, null, 0, true, true, "WindowsProtectedPrintGroupPolicyState = 1")]
    [InlineData(0, null, 0, false, false, "is disabled")]
    [InlineData(1, null, ClassNotRegistered, false, true, "could not be queried (HRESULT 0x80040154)")]
    [InlineData(null, null, ClassNotRegistered, false, null, "can also be turned on in Settings")]
    [InlineData(0, null, ClassNotRegistered, false, null, "is disabled")]
    [InlineData(null, "Access is denied.", 0, false, false, "could not be read (Access is denied.)")]
    [InlineData(7, null, 0, false, false, "unexpected value")]
    public void Protected_print_mode_trusts_winrt_and_falls_back_to_the_policy(
        int? policyValue, string? policyError, int hresult, bool queryEnabled, bool? expectedEnabled, string expectedDetail)
    {
        (bool? enabled, string detail) = ProtectedPrintMode.Evaluate(policyValue, policyError, hresult, queryEnabled);

        Assert.Equal(expectedEnabled, enabled);
        Assert.Contains(expectedDetail, detail);
    }

    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public void Absent_policy_reads_as_null()
    {
        Assert.Null(WindowsPlatformInspector.ReadPolicyFlag(Registry.CurrentUser, @"Software\Amwag-Test-" + Guid.NewGuid().ToString("N"), "Missing"));
    }

    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public void Inspects_this_machine()
    {
        PlatformInfo info = new WindowsPlatformInspector().Inspect();

        Assert.Equal("x64", info.Architecture);
        Assert.Equal(WindowsPlatformInspector.ArchitectureName(RuntimeInformation.ProcessArchitecture), info.ProcessArchitecture);
        Assert.True(info.Build >= 22000, $"build {info.Build}");
        Assert.StartsWith("Windows 11", info.ProductName);
        Assert.True(info.Ubr > 0);
        Assert.False(string.IsNullOrWhiteSpace(info.DisplayVersion));
        Assert.Equal(Environment.MachineName, info.MachineName);
        Assert.Contains('\\', info.UserName);
        Assert.Equal(Environment.IsPrivilegedProcess, info.IsElevated);
        Assert.False(info.IsSystemAccount);
        Assert.Equal(Environment.UserInteractive, info.IsInteractive);
        Assert.False(string.IsNullOrWhiteSpace(info.ProtectedPrintModeDetail));
        if (info.Build >= ProtectedPrintMode.FirstBuild)
        {
            // The WinRT query must work from a plain desktop process; null would mean it failed.
            Assert.NotNull(info.ProtectedPrintModeEnabled);
            Assert.Contains("IsProtectedPrintEnabled reports", info.ProtectedPrintModeDetail);
        }

        using RegistryKey? pointAndPrint = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Windows NT\Printers\PointAndPrint");
        bool? expectedRestriction = pointAndPrint?.GetValue("RestrictDriverInstallationToAdministrators") is int value ? value != 0 : null;
        Assert.Equal(expectedRestriction, info.RestrictDriverInstallationToAdministrators);
    }
}
