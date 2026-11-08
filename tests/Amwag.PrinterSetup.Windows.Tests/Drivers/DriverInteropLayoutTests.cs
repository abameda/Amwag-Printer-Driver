using System.Reflection;
using System.Runtime.InteropServices;
using Amwag.PrinterSetup.Windows.Interop;

namespace Amwag.PrinterSetup.Windows.Tests.Drivers;

/// <summary>Structure layouts against WinTrust.h (x64), and every declared entry point against the real system DLLs.</summary>
public sealed class DriverInteropLayoutTests
{
    [Fact]
    public void WinTrustDataMatchesTheX64HeaderLayout()
    {
        Assert.Equal(88, Marshal.SizeOf<WinTrust.WINTRUST_DATA>());
        Assert.Equal(24, (int)Marshal.OffsetOf<WinTrust.WINTRUST_DATA>(nameof(WinTrust.WINTRUST_DATA.dwUIChoice)));
        Assert.Equal(32, (int)Marshal.OffsetOf<WinTrust.WINTRUST_DATA>(nameof(WinTrust.WINTRUST_DATA.dwUnionChoice)));
        Assert.Equal(40, (int)Marshal.OffsetOf<WinTrust.WINTRUST_DATA>(nameof(WinTrust.WINTRUST_DATA.pFile)));
        Assert.Equal(48, (int)Marshal.OffsetOf<WinTrust.WINTRUST_DATA>(nameof(WinTrust.WINTRUST_DATA.dwStateAction)));
        Assert.Equal(56, (int)Marshal.OffsetOf<WinTrust.WINTRUST_DATA>(nameof(WinTrust.WINTRUST_DATA.hWVTStateData)));
        Assert.Equal(72, (int)Marshal.OffsetOf<WinTrust.WINTRUST_DATA>(nameof(WinTrust.WINTRUST_DATA.dwProvFlags)));
        Assert.Equal(80, (int)Marshal.OffsetOf<WinTrust.WINTRUST_DATA>(nameof(WinTrust.WINTRUST_DATA.pSignatureSettings)));
    }

    [Fact]
    public void WinTrustFileInfoAndProviderCertMatchTheX64HeaderLayout()
    {
        Assert.Equal(32, Marshal.SizeOf<WinTrust.WINTRUST_FILE_INFO>());
        Assert.Equal(8, (int)Marshal.OffsetOf<WinTrust.WINTRUST_FILE_INFO>(nameof(WinTrust.WINTRUST_FILE_INFO.pcwszFilePath)));
        Assert.Equal(8, (int)Marshal.OffsetOf<WinTrust.CRYPT_PROVIDER_CERT_HEAD>(nameof(WinTrust.CRYPT_PROVIDER_CERT_HEAD.pCert)));
    }

    [Fact]
    public void ActionGuidsMatchSoftPub()
    {
        Assert.Equal(new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"), WinTrust.GenericVerifyV2);
        Assert.Equal(new Guid("F750E6C3-38EE-11d1-85E5-00C04FC295EE"), WinTrust.DriverActionVerify);
    }

    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public void EveryDeclaredEntryPointIsExportedByItsDll()
    {
        var declarations = new[] { typeof(WinTrust), typeof(NewDev), typeof(DriverStoreNative) }
            .SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public))
            .Select(m => (Method: m, Import: m.GetCustomAttribute<DllImportAttribute>()))
            .Where(d => d.Import is not null)
            .ToList();
        Assert.Equal(13, declarations.Count);

        foreach (var (method, import) in declarations)
        {
            Assert.True(NativeLibrary.TryLoad(import!.Value, out IntPtr library), $"{import.Value} could not be loaded.");
            string entryPoint = import.EntryPoint ?? method.Name;
            Assert.True(NativeLibrary.TryGetExport(library, entryPoint, out _), $"{import.Value} does not export {entryPoint}.");
        }
    }
}
