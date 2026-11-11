using System.Drawing.Imaging;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Amwag.PrinterSetup.App.Support;

/// <summary>
/// What the Contact IT button sends: a PNG picture of the window and a zip with this session's logs (the window's and the
/// elevated helper's, which reuses the window's session id), the diagnostic report when a run made one, and the request
/// summary. Both files sit side by side as AMWAG-IT-&lt;time&gt;-screenshot.png and AMWAG-IT-&lt;time&gt;-logs.zip.
/// </summary>
internal sealed record SupportPackage(string ScreenshotPath, string ArchivePath)
{
    /// <summary>
    /// Writes the package into the existing <paramref name="directory"/>. Never overwrites or follows an existing entry; a log
    /// that cannot be read is named in summary.txt instead. Throws <see cref="IOException"/>, <see cref="UnauthorizedAccessException"/>
    /// or (saving the picture) <see cref="System.Runtime.InteropServices.ExternalException"/>, leaving no partial file behind.
    /// </summary>
    public static SupportPackage Write(string directory, DateTime time, Image screenshot, IEnumerable<string> logFiles, string? report, string summary)
    {
        string prefix = Path.Combine(directory, "AMWAG-IT-" + time.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
        var package = new SupportPackage(prefix + "-screenshot.png", prefix + "-logs.zip");
        var written = new List<string>();
        try
        {
            using (var stream = CreateNew(package.ScreenshotPath, written))
                screenshot.Save(stream, ImageFormat.Png);

            using (var stream = CreateNew(package.ArchivePath, written))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var unreadable = logFiles.Where(file => !TryAddFile(zip, file)).Select(Path.GetFileName).ToList();
                if (report is not null)
                    AddText(zip, "report.txt", report);
                AddText(zip, "summary.txt", unreadable.Count == 0
                    ? summary
                    : summary + Environment.NewLine + "Logs that could not be read: " + string.Join(", ", unreadable));
            }

            return package;
        }
        catch
        {
            foreach (string path in written)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort: the original failure is what the employee is told about.
                }
            }
            throw;
        }
    }

    /// <summary>
    /// This session's log files: <paramref name="ownLog"/> and every log of <paramref name="sessionId"/> in
    /// <paramref name="directories"/>. Links are skipped; a folder that cannot be read is left out.
    /// </summary>
    public static IReadOnlyList<string> SessionLogs(string sessionId, string? ownLog, IEnumerable<string> directories)
    {
        var files = new List<string>();
        if (ownLog is not null)
            files.Add(Path.GetFullPath(ownLog));
        if (sessionId.Length == 0 || !sessionId.All(char.IsAsciiLetterOrDigit))
            return files;

        foreach (string directory in directories)
        {
            try
            {
                foreach (var file in new DirectoryInfo(directory).EnumerateFiles($"*-{sessionId}-*.jsonl"))
                {
                    if ((file.Attributes & FileAttributes.ReparsePoint) == 0 && !files.Contains(file.FullName, StringComparer.OrdinalIgnoreCase))
                        files.Add(file.FullName);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A missing or unreadable folder holds nothing of this session that can be sent.
            }
        }

        return files;
    }

    private static FileStream CreateNew(string path, List<string> written)
    {
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        written.Add(path);
        return stream;
    }

    private static bool TryAddFile(ZipArchive zip, string path)
    {
        try
        {
            // The logs are still open for writing (this window's own, and a running helper's): share them while reading.
            using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var target = zip.CreateEntry(Path.GetFileName(path), CompressionLevel.Optimal).Open();
            source.CopyTo(target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.Write(text);
    }
}
