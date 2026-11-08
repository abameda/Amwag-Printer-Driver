using System.Security.Cryptography.X509Certificates;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Windows.Interop;

namespace Amwag.PrinterSetup.Windows.Drivers;

/// <summary>An Authenticode verdict plus the signer's common name (for matching approved signers by CN).</summary>
internal sealed record AuthenticodeResult(SignatureInfo Signature, string? SignerCommonName);

/// <summary>Verifies Authenticode signatures (files and catalogs) with WinVerifyTrust and reads the signer WinVerifyTrust evaluated.</summary>
internal static class AuthenticodeVerifier
{
    // Revocation is not checked and nothing is fetched from the network (WTD_CACHE_ONLY_URL_RETRIEVAL also covers AIA):
    // branch PCs are often offline, where CRL/OCSP/AIA retrieval only adds long timeouts or failures. The chain must
    // still build to a root trusted by this computer, and Windows enforces its own signing policy at installation.
    private const uint ProviderFlags =
        WinTrust.WTD_REVOCATION_CHECK_NONE | WinTrust.WTD_CACHE_ONLY_URL_RETRIEVAL | WinTrust.WTD_DISABLE_MD2_MD4;

    private const string CommonNameOid = "2.5.4.3";

    public static unsafe AuthenticodeResult Verify(string path)
    {
        string fullPath = Path.GetFullPath(path);
        fixed (char* filePath = fullPath)
        {
            var file = new WinTrust.WINTRUST_FILE_INFO
            {
                cbStruct = (uint)sizeof(WinTrust.WINTRUST_FILE_INFO),
                pcwszFilePath = (IntPtr)filePath,
            };
            var data = new WinTrust.WINTRUST_DATA
            {
                cbStruct = (uint)sizeof(WinTrust.WINTRUST_DATA),
                dwUIChoice = WinTrust.WTD_UI_NONE,
                fdwRevocationChecks = WinTrust.WTD_REVOKE_NONE,
                dwUnionChoice = WinTrust.WTD_CHOICE_FILE,
                pFile = (IntPtr)(&file),
                dwStateAction = WinTrust.WTD_STATEACTION_VERIFY,
                dwProvFlags = ProviderFlags,
            };

            int status = WinTrust.WinVerifyTrust(WinTrust.NoInteractiveUser, WinTrust.GenericVerifyV2, ref data);
            try
            {
                using X509Certificate2? signer = ReadSigner(data.hWVTStateData);
                string statusText = status == 0 ? "The signature is valid and trusted." : NativeErrorText.Describe(status);
                var signature = new SignatureInfo(
                    IsTrusted: status == 0,
                    SignerSubject: signer?.Subject,
                    IssuerSubject: signer?.Issuer,
                    Thumbprint: signer?.Thumbprint,
                    StatusCode: status,
                    StatusText: statusText);
                return new AuthenticodeResult(signature, signer is null ? null : CommonName(signer.SubjectName));
            }
            finally
            {
                data.dwStateAction = WinTrust.WTD_STATEACTION_CLOSE;
                WinTrust.WinVerifyTrust(WinTrust.NoInteractiveUser, WinTrust.GenericVerifyV2, ref data);
            }
        }
    }

    /// <summary>The first signer's certificate from the verification state (also present when the chain is untrusted).</summary>
    private static unsafe X509Certificate2? ReadSigner(IntPtr stateData)
    {
        if (stateData == IntPtr.Zero)
            return null;

        IntPtr providerData = WinTrust.WTHelperProvDataFromStateData(stateData);
        if (providerData == IntPtr.Zero)
            return null;

        IntPtr signer = WinTrust.WTHelperGetProvSignerFromChain(providerData, 0, 0, 0);
        if (signer == IntPtr.Zero)
            return null;

        IntPtr certificate = WinTrust.WTHelperGetProvCertFromChain(signer, 0);
        if (certificate == IntPtr.Zero)
            return null;

        IntPtr context = ((WinTrust.CRYPT_PROVIDER_CERT_HEAD*)certificate)->pCert;
        // The X509Certificate2 duplicates the CERT_CONTEXT, so it outlives WTD_STATEACTION_CLOSE.
        return context == IntPtr.Zero ? null : new X509Certificate2(context);
    }

    private static string? CommonName(X500DistinguishedName name)
    {
        foreach (X500RelativeDistinguishedName rdn in name.EnumerateRelativeDistinguishedNames())
        {
            if (!rdn.HasMultipleElements && rdn.GetSingleElementType().Value == CommonNameOid)
                return rdn.GetSingleElementValue();
        }

        return null;
    }
}
