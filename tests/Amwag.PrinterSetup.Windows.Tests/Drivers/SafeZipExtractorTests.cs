using Amwag.PrinterSetup.Windows.Drivers;

namespace Amwag.PrinterSetup.Windows.Tests.Drivers;

public sealed class SafeZipExtractorTests : IDisposable
{
    private readonly TempFolders _folders = new();

    public void Dispose() => _folders.Dispose();

    private string Destination => Directory.CreateDirectory(Path.Combine(_folders.Root, "out")).FullName;

    private static void Extract(byte[] archive, string destination, long maxBytes = SafeZipExtractor.DefaultMaxTotalBytes, int maxEntries = SafeZipExtractor.DefaultMaxEntries)
    {
        using var stream = new MemoryStream(archive);
        SafeZipExtractor.Extract(stream, destination, maxBytes, maxEntries, null, default);
    }

    [Fact]
    public void ExtractsFilesAndFoldersWithEitherSeparator()
    {
        string destination = Destination;

        Extract(TestPackages.Zip(("a/", null), ("a/b/c.txt", "c"), ("d\\e.txt", "e"), ("./f.txt", "f")), destination);

        Assert.Equal("c", File.ReadAllText(Path.Combine(destination, "a", "b", "c.txt")));
        Assert.Equal("e", File.ReadAllText(Path.Combine(destination, "d", "e.txt")));
        Assert.Equal("f", File.ReadAllText(Path.Combine(destination, "f.txt")));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("..\\evil.txt")]
    public void TraversalOutsideTheDestinationIsRejectedBeforeAnythingIsWritten(string name)
    {
        string destination = Destination;

        var ex = Assert.Throws<InvalidDataException>(() => Extract(TestPackages.Zip(("first.txt", "1"), (name, "evil")), destination));

        Assert.Contains("outside the extraction folder", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
        Assert.False(File.Exists(Path.Combine(_folders.Root, "evil.txt")));
    }

    [Fact]
    public void OnlyThePackageSubtreeAndItsCatalogAreExtracted()
    {
        string destination = Destination;
        var package = new Amwag.PrinterSetup.Core.Configuration.DriverPackageDefinition
        {
            InfPath = "Pkg/PrnDrv/PCL Driver/64bit/prnkycl1.inf",
            CatalogPath = "Pkg/Signed/prnkycl1.cat",
        };
        byte[] zip = TestPackages.Zip(
            ("Pkg/PrnDrv/PCL Driver/64bit/prnkycl1.inf", "inf"),
            ("Pkg/PrnDrv/PCL Driver/64bit/amd64/driver.dll", "dll"),
            ("Pkg/Signed/prnkycl1.cat", "cat"),
            ("Pkg/Setup.exe", "installer"),
            ("Pkg/PrnDrv/PCL Driver/32bit/prnkycl1.inf", "x86"));

        using (var stream = new MemoryStream(zip))
        {
            SafeZipExtractor.Extract(stream, destination, SafeZipExtractor.DefaultMaxTotalBytes, SafeZipExtractor.DefaultMaxEntries, null, default,
                DriverPackageProvider.PackageEntries(package));
        }

        Assert.True(File.Exists(Path.Combine(destination, "Pkg", "PrnDrv", "PCL Driver", "64bit", "prnkycl1.inf")));
        Assert.True(File.Exists(Path.Combine(destination, "Pkg", "PrnDrv", "PCL Driver", "64bit", "amd64", "driver.dll")));
        Assert.True(File.Exists(Path.Combine(destination, "Pkg", "Signed", "prnkycl1.cat")));
        Assert.False(File.Exists(Path.Combine(destination, "Pkg", "Setup.exe")));
        Assert.False(Directory.Exists(Path.Combine(destination, "Pkg", "PrnDrv", "PCL Driver", "32bit")));
    }

    [Fact]
    public void UnselectedEntriesAreStillValidated()
    {
        string destination = Destination;
        byte[] zip = TestPackages.Zip(("Pkg/64bit/a.inf", "inf"), ("../evil.txt", "evil"));

        using var stream = new MemoryStream(zip);
        Assert.Throws<InvalidDataException>(() => SafeZipExtractor.Extract(stream, destination, SafeZipExtractor.DefaultMaxTotalBytes,
            SafeZipExtractor.DefaultMaxEntries, null, default, name => name.StartsWith("Pkg/64bit/", StringComparison.Ordinal)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public void TooManyEntriesAreRejected()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Extract(TestPackages.Zip(("1.txt", "1"), ("2.txt", "2"), ("3.txt", "3")), Destination, maxEntries: 2));

        Assert.Contains("entries", ex.Message);
    }

    [Fact]
    public void ArchivesExpandingBeyondTheCapAreRejected()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Extract(TestPackages.Zip(("big.txt", new string('z', 10_000))), Destination, maxBytes: 1_000));

        Assert.Contains("more than", ex.Message);
    }

    [Fact]
    public void DuplicateEntriesAreRejected()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Extract(TestPackages.Zip(("same.txt", "1"), ("SAME.txt", "2")), Destination));

        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void CorruptArchiveIsInvalidData()
    {
        byte[] archive = TestPackages.Zip(("a.txt", new string('a', 1000)));
        Array.Clear(archive, archive.Length - 22, 22);

        Assert.Throws<InvalidDataException>(() => Extract(archive, Destination));
    }
}
