using System.Text;
using Amwag.PrinterSetup.Windows.Drivers;
using Xunit.Abstractions;

namespace Amwag.PrinterSetup.Windows.Tests.Drivers;

/// <summary>Read-only WinVerifyTrust and catalog lookups on files Windows itself ships.</summary>
[Trait("Category", "WindowsIntegration")]
public sealed class CatalogVerificationIntegrationTests : IDisposable
{
    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);

    private readonly TempFolders _folders = new();
    private readonly ITestOutputHelper _output;

    public CatalogVerificationIntegrationTests(ITestOutputHelper output) => _output = output;

    public void Dispose() => _folders.Dispose();

    [Fact]
    public void MicrosoftCatalogFromCatRootIsTrustedWithAMicrosoftSigner()
    {
        string catalog = WindowsFiles.MicrosoftCatalog();

        AuthenticodeResult result = AuthenticodeVerifier.Verify(catalog);

        _output.WriteLine($"{catalog}: {result.Signature.StatusCode:X8} {result.Signature.SignerSubject} / {result.Signature.IssuerSubject}");
        Assert.True(result.Signature.IsTrusted, result.Signature.StatusText);
        Assert.Equal(0, result.Signature.StatusCode);
        Assert.Contains("O=Microsoft Corporation", result.Signature.SignerSubject);
        Assert.StartsWith("Microsoft", result.SignerCommonName);
        Assert.Matches("^[0-9A-F]{40}$", result.Signature.Thumbprint!);
        Assert.Contains("Microsoft", result.Signature.IssuerSubject);
    }

    [Fact]
    public void UnsignedScriptIsNotTrusted()
    {
        string path = Path.Combine(_folders.Root, "unsigned.ps1");
        File.WriteAllText(path, "Write-Output 'not signed'\r\n", Encoding.UTF8);

        AuthenticodeResult result = AuthenticodeVerifier.Verify(path);

        _output.WriteLine($"{result.Signature.StatusCode:X8} {result.Signature.StatusText}");
        Assert.False(result.Signature.IsTrusted);
        Assert.Equal(TRUST_E_NOSIGNATURE, result.Signature.StatusCode);
        Assert.Null(result.Signature.SignerSubject);
        Assert.Null(result.SignerCommonName);
        Assert.False(string.IsNullOrWhiteSpace(result.Signature.StatusText));
    }

    [Theory]
    [InlineData("garbage.cat")]
    [InlineData("plain.txt")]
    [InlineData("fake.inf")]
    public void ArbitraryFilesAreNotTrusted(string name)
    {
        string path = Path.Combine(_folders.Root, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("[Version]\r\nSignature=\"$Windows NT$\"\r\n"));

        AuthenticodeResult result = AuthenticodeVerifier.Verify(path);

        _output.WriteLine($"{name}: {result.Signature.StatusCode:X8} {result.Signature.StatusText}");
        Assert.False(result.Signature.IsTrusted);
        Assert.Equal(TRUST_E_SUBJECT_FORM_UNKNOWN, result.Signature.StatusCode);
    }

    [Fact]
    public void InboxPrintToPdfInfIsAMemberOfItsSignedCatalog()
    {
        (string inf, string catalog) = WindowsFiles.PrintToPdfPackage();

        Assert.Equal("prnms009.cat", InfVersionSection.ReadCatalogFile(inf, "x64"), ignoreCase: true);
        CatalogVerification verification = CatalogVerification.VerifyWithWindows(catalog, inf);

        _output.WriteLine($"{verification.Signature.SignerSubject}: {verification.InfMembership.Detail}");
        Assert.True(verification.Signature.IsTrusted, verification.Signature.StatusText);
        Assert.Equal(CatalogMembershipStatus.Member, verification.InfMembership.Status);
    }

    [Fact]
    public void FileThatIsNotListedIsNotAMember()
    {
        (_, string catalog) = WindowsFiles.PrintToPdfPackage();
        string path = Path.Combine(_folders.Root, "other.inf");
        File.WriteAllText(path, "[Version]\r\nSignature=\"$Windows NT$\"\r\n; " + Guid.NewGuid() + "\r\n");

        CatalogMembershipResult result = CatalogMembership.Check(catalog, path);

        _output.WriteLine(result.Detail);
        Assert.Equal(CatalogMembershipStatus.NotMember, result.Status);
        Assert.Contains("SHA256", result.Detail);
        Assert.Contains("SHA1", result.Detail);
    }

    [Fact]
    public void FileThatIsNotACatalogGivesAnUndeterminedMembership()
    {
        string notACatalog = Path.Combine(_folders.Root, "garbage.cat");
        File.WriteAllText(notACatalog, "not a catalog");
        string member = Path.Combine(_folders.Root, "member.inf");
        File.WriteAllText(member, "x");

        CatalogMembershipResult result = CatalogMembership.Check(notACatalog, member);

        _output.WriteLine(result.Detail);
        Assert.Equal(CatalogMembershipStatus.Unknown, result.Status);
        Assert.Contains("CryptCATOpen", result.Detail);
    }
}

internal static class WindowsFiles
{
    private static readonly string System32 = Environment.GetFolderPath(Environment.SpecialFolder.System);

    /// <summary>The first Windows component catalog (never a third-party oem*.cat) of the driver catalog subsystem.</summary>
    public static string MicrosoftCatalog() =>
        Directory.EnumerateFiles(Path.Combine(System32, "CatRoot", "{F750E6C3-38EE-11D1-85E5-00C04FC295EE}"), "Microsoft-Windows-*.cat")
            .Order(StringComparer.Ordinal)
            .First();

    /// <summary>The inbox "Microsoft Print To PDF" driver package (prnms009) in the driver store.</summary>
    public static (string Inf, string Catalog) PrintToPdfPackage()
    {
        string package = Directory.EnumerateDirectories(Path.Combine(System32, "DriverStore", "FileRepository"), "prnms009.inf_amd64_*")
            .Order(StringComparer.Ordinal)
            .First();
        return (Path.Combine(package, "prnms009.inf"), Path.Combine(package, "prnms009.cat"));
    }
}
