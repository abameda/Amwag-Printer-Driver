using Amwag.PrinterSetup.Core.Abstractions;

namespace Amwag.PrinterSetup.Windows.Drivers;

/// <summary>Checks a package catalog: its Authenticode signature and whether the package INF is one of its members.</summary>
internal delegate CatalogVerification CatalogVerifier(string catalogPath, string infPath);

internal sealed record CatalogVerification(SignatureInfo Signature, string? SignerCommonName, CatalogMembershipResult InfMembership)
{
    /// <summary>The production <see cref="CatalogVerifier"/>: WinVerifyTrust on the catalog, then the INF's hash among its members.</summary>
    public static CatalogVerification VerifyWithWindows(string catalogPath, string infPath)
    {
        AuthenticodeResult authenticode = AuthenticodeVerifier.Verify(catalogPath);
        return new(authenticode.Signature, authenticode.SignerCommonName, CatalogMembership.Check(catalogPath, infPath));
    }
}
