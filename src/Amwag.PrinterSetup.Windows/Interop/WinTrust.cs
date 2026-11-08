using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Amwag.PrinterSetup.Windows.Interop;

/// <summary>
/// wintrust.dll: Authenticode verification (WinVerifyTrust + WTHelper*) and catalog member lookup (CryptCAT*).
/// Layouts and constants follow WinTrust.h, SoftPub.h and mscat.h of the Windows SDK 10.0.26100.
/// </summary>
internal static class WinTrust
{
    private const string Dll = "wintrust.dll";

    /// <summary>WINTRUST_ACTION_GENERIC_VERIFY_V2 {00AAC56B-CD44-11d0-8CC2-00C04FC295EE} (SoftPub.h).</summary>
    internal static readonly Guid GenericVerifyV2 = new(0x00aac56b, 0xcd44, 0x11d0, 0x8c, 0xc2, 0x00, 0xc0, 0x4f, 0xc2, 0x95, 0xee);

    /// <summary>DRIVER_ACTION_VERIFY {F750E6C3-38EE-11d1-85E5-00C04FC295EE}: the catalog subsystem of drivers (SoftPub.h).</summary>
    internal static readonly Guid DriverActionVerify = new(0xf750e6c3, 0x38ee, 0x11d1, 0x85, 0xe5, 0x00, 0xc0, 0x4f, 0xc2, 0x95, 0xee);

    internal const uint WTD_UI_NONE = 2;
    internal const uint WTD_REVOKE_NONE = 0;
    internal const uint WTD_CHOICE_FILE = 1;
    internal const uint WTD_STATEACTION_VERIFY = 1;
    internal const uint WTD_STATEACTION_CLOSE = 2;
    internal const uint WTD_REVOCATION_CHECK_NONE = 0x00000010;
    internal const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;
    internal const uint WTD_DISABLE_MD2_MD4 = 0x00002000;

    internal const uint CRYPTCAT_OPEN_EXISTING = 0x00000004;

    internal const string BCRYPT_SHA256_ALGORITHM = "SHA256";
    internal const string BCRYPT_SHA1_ALGORITHM = "SHA1";

    /// <summary>"There is no interactive user": the trust provider never shows UI (WinVerifyTrust hwnd parameter).</summary>
    internal static readonly IntPtr NoInteractiveUser = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        /// <summary>Union member; WTD_CHOICE_FILE makes it a WINTRUST_FILE_INFO*.</summary>
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    /// <summary>The leading members of CRYPT_PROVIDER_CERT; only pCert is read.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CRYPT_PROVIDER_CERT_HEAD
    {
        public uint cbStruct;
        public IntPtr pCert;
    }

    /// <summary>Returns a LONG status: 0 only when the subject is trusted (not an HRESULT to test with SUCCEEDED).</summary>
    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern int WinVerifyTrust(IntPtr hwnd, in Guid pgActionID, ref WINTRUST_DATA pWVTData);

    /// <summary>CRYPT_PROVIDER_DATA* owned by the WinVerifyTrust state; valid until WTD_STATEACTION_CLOSE.</summary>
    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);

    /// <summary>CRYPT_PROVIDER_SGNR* or null.</summary>
    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr pProvData, uint idxSigner, int fCounterSigner, uint idxCounterSigner);

    /// <summary>CRYPT_PROVIDER_CERT* or null; index 0 is the signing certificate.</summary>
    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern IntPtr WTHelperGetProvCertFromChain(IntPtr pSgnr, uint idxCert);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern int CryptCATAdminAcquireContext2(
        out SafeCatAdminHandle phCatAdmin,
        in Guid pgSubsystem,
        string pwszHashAlgorithm,
        IntPtr pStrongHashPolicy,
        uint dwFlags);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern int CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern unsafe int CryptCATAdminCalcHashFromFileHandle2(
        SafeCatAdminHandle hCatAdmin,
        SafeFileHandle hFile,
        ref uint pcbHash,
        byte* pbHash,
        uint dwFlags);

    /// <summary>Returns INVALID_HANDLE_VALUE on failure.</summary>
    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern SafeCatalogHandle CryptCATOpen(
        string pwszFileName,
        uint fdwOpenFlags,
        IntPtr hProv,
        uint dwPublicVersion,
        uint dwEncodingType);

    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern int CryptCATClose(IntPtr hCatalog);

    /// <summary>CRYPTCATMEMBER* owned by the catalog handle (never freed by the caller), or null when the tag is absent.</summary>
    [DllImport(Dll, CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    internal static extern IntPtr CryptCATGetMemberInfo(SafeCatalogHandle hCatalog, string pwszReferenceTag);
}

internal sealed class SafeCatalogHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeCatalogHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => WinTrust.CryptCATClose(handle) != 0;
}

internal sealed class SafeCatAdminHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeCatAdminHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => WinTrust.CryptCATAdminReleaseContext(handle, 0) != 0;
}
