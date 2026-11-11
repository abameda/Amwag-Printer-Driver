using System.IO.Compression;
using Amwag.PrinterSetup.App.Support;

namespace Amwag.PrinterSetup.App.Tests.Support;

public sealed class SupportPackageTests : IDisposable
{
    private static readonly DateTime Time = new(2026, 11, 10, 14, 32, 5, 123);

    private readonly string _directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "amwag-support-tests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A file still held by a failed test; the temp folder is cleaned up later.
        }
    }

    [Fact]
    public void Writes_the_picture_and_an_archive_with_the_logs_report_and_summary()
    {
        string own = Write("20261110-143000-interactive-abc123def456-100.jsonl", """{"event":"app.start"}""");
        string worker = Write("20261110-143010-worker-abc123def456-200.jsonl", """{"event":"worker.start"}""");
        // The window's own log is still open for writing (as JsonLinesLog keeps it) while it is packed.
        using var openLog = new FileStream(own, FileMode.Append, FileAccess.Write, FileShare.Read | FileShare.Delete);
        using var picture = new Bitmap(40, 30);

        var package = SupportPackage.Write(_directory, Time, picture, [own, worker], "REPORT", "SUMMARY");

        Assert.Equal(Path.Combine(_directory, "AMWAG-IT-20261110-143205-123-screenshot.png"), package.ScreenshotPath);
        Assert.Equal(Path.Combine(_directory, "AMWAG-IT-20261110-143205-123-logs.zip"), package.ArchivePath);
        using (var image = Image.FromFile(package.ScreenshotPath))
            Assert.Equal(new Size(40, 30), image.Size);

        using var zip = ZipFile.OpenRead(package.ArchivePath);
        Assert.Equal(
            [Path.GetFileName(own), Path.GetFileName(worker), "report.txt", "summary.txt"],
            zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
        Assert.Equal("""{"event":"app.start"}""", Read(zip, Path.GetFileName(own)));
        Assert.Equal("""{"event":"worker.start"}""", Read(zip, Path.GetFileName(worker)));
        Assert.Equal("REPORT", Read(zip, "report.txt"));
        Assert.Equal("SUMMARY", Read(zip, "summary.txt"));
    }

    [Fact]
    public void A_log_that_cannot_be_read_is_named_in_the_summary()
    {
        using var picture = new Bitmap(4, 4);

        var package = SupportPackage.Write(_directory, Time, picture, [Path.Combine(_directory, "gone.jsonl")], report: null, "SUMMARY");

        using var zip = ZipFile.OpenRead(package.ArchivePath);
        Assert.Equal(["summary.txt"], zip.Entries.Select(e => e.FullName));
        Assert.Equal("SUMMARY" + Environment.NewLine + "Logs that could not be read: gone.jsonl", Read(zip, "summary.txt"));
    }

    [Fact]
    public void Never_overwrites_an_existing_file_and_leaves_no_partial_package()
    {
        string existing = Write("AMWAG-IT-20261110-143205-123-logs.zip", "keep");
        using var picture = new Bitmap(4, 4);

        Assert.Throws<IOException>(() => SupportPackage.Write(_directory, Time, picture, [], null, "SUMMARY"));

        Assert.Equal("keep", File.ReadAllText(existing));
        Assert.False(File.Exists(Path.Combine(_directory, "AMWAG-IT-20261110-143205-123-screenshot.png")));
    }

    [Fact]
    public void Session_logs_are_the_own_log_and_every_log_of_the_session()
    {
        string own = Write(@"user\20261110-143000-interactive-abc123def456-100.jsonl", "");
        string worker = Write(@"machine\20261110-143010-worker-abc123def456-200.jsonl", "");
        Write(@"machine\20261110-120000-interactive-0123456789ab-300.jsonl", "");
        Write(@"machine\notes-abc123def456-1.txt", "");
        string[] directories = [Path.Combine(_directory, "machine"), Path.Combine(_directory, "user"), Path.Combine(_directory, "missing")];

        var logs = SupportPackage.SessionLogs("abc123def456", own, directories);

        Assert.Equal([own, worker], logs);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("")]
    [InlineData(@"..\x")]
    public void A_session_id_that_is_not_plain_letters_and_digits_finds_only_the_own_log(string sessionId)
    {
        Write("20261110-143010-worker-abc123def456-200.jsonl", "");

        Assert.Equal([Path.Combine(_directory, "own.jsonl")], SupportPackage.SessionLogs(sessionId, Path.Combine(_directory, "own.jsonl"), [_directory]));
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_directory, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static string Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
}
