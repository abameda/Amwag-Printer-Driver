using System.Text;
using Amwag.PrinterSetup.Core.Setup;
using Amwag.PrinterSetup.Windows.Drivers;

namespace Amwag.PrinterSetup.Windows.Tests.Drivers;

public sealed class OfficialDownloadHostsTests
{
    [Theory]
    [InlineData("https://www.triumph-adler.com/caas/v1/ta/media/102720/data/x/Kx87_UPD_8.7.0917.zip")]
    [InlineData("https://triumph-adler.com/a.zip")]
    [InlineData("https://cdn.eu.triumph-adler.de/a.zip")]
    [InlineData("https://www.triumph-adler.net/a.zip")]
    [InlineData("https://www.utax.com/a.zip")]
    [InlineData("https://utax.de/a.zip")]
    [InlineData("https://www.utax.it/a.zip")]
    [InlineData("https://www.kyoceradocumentsolutions.com/a.zip")]
    [InlineData("https://www.kyoceradocumentsolutions.eu/a.zip")]
    [InlineData("https://WWW.TRIUMPH-ADLER.COM/a.zip")]
    [InlineData("https://www.triumph-adler.com./a.zip")]
    public void OfficialHttpsHostsAreAllowed(string url) => Assert.True(OfficialDownloadHosts.IsAllowed(new Uri(url), out _));

    [Theory]
    [InlineData("http://www.triumph-adler.com/a.zip")]
    [InlineData("ftp://www.triumph-adler.com/a.zip")]
    [InlineData("https://eviltriumph-adler.com/a.zip")]
    [InlineData("https://triumph-adler.com.evil.net/a.zip")]
    [InlineData("https://www.triumph-adler.co/a.zip")]
    [InlineData("https://kyocera.com/a.zip")]
    [InlineData("https://kyoceradocumentsolutions.eu.evil.net/a.zip")]
    [InlineData("https://203.0.113.10/a.zip")]
    [InlineData("https://[2001:db8::1]/a.zip")]
    [InlineData("https://user:secret@www.triumph-adler.com/a.zip")]
    [InlineData("file:///C:/drivers/a.zip")]
    public void OtherUrlsAreRefused(string url)
    {
        Assert.False(OfficialDownloadHosts.IsAllowed(new Uri(url), out string reason));
        Assert.NotEmpty(reason);
    }
}

public sealed class InfVersionSectionTests : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private string WriteInf(string content, Encoding encoding)
    {
        string path = Path.Combine(_folders.Root, "test.inf");
        File.WriteAllText(path, content, encoding);
        return path;
    }

    [Fact]
    public void ReadsTheUndecoratedCatalogWithSpacesAndComment() =>
        Assert.Equal("TEST.CAT", InfVersionSection.ReadCatalogFile(WriteInf(TestPackages.Inf, Encoding.ASCII), "x64"));

    [Fact]
    public void PlatformDecorationWinsOverTheGenericEntries()
    {
        string inf = "[version]\r\nCatalogFile = generic.cat\r\nCatalogFile.NT = nt.cat\r\nCatalogFile.NTamd64 = \"amd64.cat\"\r\nCatalogFile.NTarm64 = arm.cat\r\n";

        Assert.Equal("amd64.cat", InfVersionSection.ReadCatalogFile(WriteInf(inf, Encoding.ASCII), "x64"));
        Assert.Equal("arm.cat", InfVersionSection.ReadCatalogFile(WriteInf(inf, Encoding.ASCII), "arm64"));
    }

    [Fact]
    public void NtEntryIsUsedWhenThePlatformHasNone() =>
        Assert.Equal("nt.cat", InfVersionSection.ReadCatalogFile(WriteInf("[Version]\r\nCatalogFile=generic.cat\r\nCatalogFile.NT=nt.cat\r\n", Encoding.ASCII), "x64"));

    [Fact]
    public void ReadsUtf16InfFiles() =>
        Assert.Equal("TEST.CAT", InfVersionSection.ReadCatalogFile(WriteInf(TestPackages.Inf, Encoding.Unicode), "x64"));

    [Fact]
    public void IgnoresCatalogEntriesOutsideTheVersionSection() =>
        Assert.Null(InfVersionSection.ReadCatalogFile(WriteInf("[Version]\r\nSignature=\"$Windows NT$\"\r\n[Strings]\r\nCatalogFile=x.cat\r\n", Encoding.ASCII), "x64"));

    [Fact]
    public void CommentedOutCatalogIsIgnored() =>
        Assert.Null(InfVersionSection.ReadCatalogFile(WriteInf("[Version]\r\n; CatalogFile=x.cat\r\n", Encoding.ASCII), "x64"));
}

public sealed class DriverInstallerClassificationTests
{
    [Theory]
    [InlineData(0x80070005u, FindingCode.ElevationRequired)]
    [InlineData(0x800B0100u, FindingCode.DriverSignatureInvalid)] // TRUST_E_NOSIGNATURE
    [InlineData(0x800B0109u, FindingCode.DriverSignatureInvalid)] // CERT_E_UNTRUSTEDROOT
    [InlineData(0x800B0004u, FindingCode.DriverSignatureInvalid)] // TRUST_E_SUBJECT_NOT_TRUSTED
    [InlineData(0x80096010u, FindingCode.DriverSignatureInvalid)] // TRUST_E_BAD_DIGEST
    [InlineData(0x800F0242u, FindingCode.DriverSignatureInvalid)] // SPAPI_E_AUTHENTICODE_TRUST_NOT_ESTABLISHED
    [InlineData(0x800F024Bu, FindingCode.DriverSignatureInvalid)] // SPAPI_E_FILE_HASH_NOT_IN_CATALOG
    [InlineData(0x800F022Fu, FindingCode.DriverSignatureInvalid)] // SPAPI_E_NO_CATALOG_FOR_OEM_INF
    [InlineData(0x800706BAu, FindingCode.SpoolerUnavailable)] // RPC_S_SERVER_UNAVAILABLE
    [InlineData(0x80070705u, FindingCode.DriverInstallFailed)] // ERROR_UNKNOWN_PRINTER_DRIVER
    [InlineData(0x80070002u, FindingCode.DriverInstallFailed)] // ERROR_FILE_NOT_FOUND
    [InlineData(0x800F0247u, FindingCode.DriverInstallFailed)] // SPAPI_E_DRIVER_STORE_ADD_FAILED
    [InlineData(0x800F0241u, FindingCode.DriverInstallFailed)] // SPAPI_E_AUTHENTICODE_TRUSTED_PUBLISHER is not a trust failure
    public void HResultsMapToFindings(uint hr, FindingCode expected) =>
        Assert.Equal(expected, DriverInstaller.Classify(unchecked((int)hr)));

    [Theory]
    [InlineData(0x800F0242u, true)]
    [InlineData(0x800F0243u, true)]
    [InlineData(0xE0000242u, true)]
    [InlineData(0xE0000243u, true)]
    [InlineData(0x800F0241u, false)]
    [InlineData(0x80070242u, false)]
    [InlineData(0x80070005u, false)]
    public void UntrustedPublisherFailuresCarryAdvice(uint code, bool expected) =>
        Assert.Equal(expected, DriverInstaller.PublisherTrustAdvice(unchecked((int)code)).Contains("Trusted Publishers"));
}

public sealed class NativeErrorTextTests
{
    [Fact]
    public void FormatsHexAndASystemMessage()
    {
        string text = NativeErrorText.Format(unchecked((int)0x80070005));

        Assert.StartsWith("0x80070005 (", text);
        Assert.True(text.Length > "0x80070005 ()".Length);
    }

    [Fact]
    public void SetupApiCodesUseTheirHResultMessage() =>
        Assert.Equal(NativeErrorText.Describe(unchecked((int)0x800F020B)), NativeErrorText.Describe(unchecked((int)0xE000020B)));
}
