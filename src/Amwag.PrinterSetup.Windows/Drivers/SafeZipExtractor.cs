using System.IO.Compression;

namespace Amwag.PrinterSetup.Windows.Drivers;

/// <summary>
/// Extracts a ZIP archive entry by entry while refusing anything that could escape the destination folder
/// (zip-slip, rooted or drive-relative names, alternate data streams) or exhaust the disk.
/// </summary>
internal static class SafeZipExtractor
{
    public const long DefaultMaxTotalBytes = 2L * 1024 * 1024 * 1024;
    public const int DefaultMaxEntries = 20_000;

    private const int BufferSize = 1024 * 1024;

    /// <summary>
    /// Extracts the entries of <paramref name="archive"/> that <paramref name="include"/> selects (all when null) under
    /// <paramref name="destination"/>, keeping their relative paths. Every entry name is validated, selected or not, before anything
    /// is written. Throws <see cref="InvalidDataException"/> for unsafe, oversized or malformed archives.
    /// <paramref name="progress"/> receives (bytes written, total declared bytes of the selected entries).
    /// </summary>
    public static void Extract(
        Stream archive,
        string destination,
        long maxTotalBytes,
        int maxEntries,
        Action<long, long>? progress,
        CancellationToken cancellationToken,
        Func<string, bool>? include = null)
    {
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
        IReadOnlyCollection<ZipArchiveEntry> entries = zip.Entries;
        if (entries.Count > maxEntries)
            throw new InvalidDataException($"The archive has {entries.Count} entries; at most {maxEntries} are accepted.");

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        var plan = new List<(ZipArchiveEntry Entry, string Target, bool IsDirectory)>(entries.Count);
        long declaredTotal = 0;
        foreach (ZipArchiveEntry entry in entries)
        {
            var resolved = ResolveTarget(root, entry);
            if (include is not null && !include(entry.FullName))
                continue;

            declaredTotal += entry.Length;
            if (declaredTotal > maxTotalBytes)
                throw new InvalidDataException($"The archive expands to more than {maxTotalBytes} bytes.");

            plan.Add(resolved);
        }

        byte[] buffer = new byte[BufferSize];
        long written = 0;
        foreach (var (entry, target, isDirectory) in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (isDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
                throw new InvalidDataException($"The archive contains '{entry.FullName}' more than once.");

            using Stream input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            long entryWritten = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                entryWritten += read;
                written += read;
                // The declared sizes are what the caps were checked against; an entry may not inflate beyond its own.
                if (entryWritten > entry.Length)
                    throw new InvalidDataException($"The entry '{entry.FullName}' expands beyond its declared size of {entry.Length} bytes.");

                output.Write(buffer, 0, read);
                progress?.Invoke(written, declaredTotal);
            }

            if (entryWritten != entry.Length)
                throw new InvalidDataException($"The entry '{entry.FullName}' is truncated ({entryWritten} of {entry.Length} bytes).");
        }
    }

    private static (ZipArchiveEntry Entry, string Target, bool IsDirectory) ResolveTarget(string root, ZipArchiveEntry entry)
    {
        string name = entry.FullName;
        if (name.Length == 0 || name.Contains('\0'))
            throw new InvalidDataException("The archive contains an entry with an empty or invalid name.");

        string relative = name.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException($"The archive entry '{name}' is an absolute path or names a drive or stream.");

        // Full-path containment also covers "..", "a/../../b" and the trailing dots and spaces Windows strips.
        string target = Path.GetFullPath(Path.Combine(root, relative));
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The archive entry '{name}' points outside the extraction folder.");

        return (entry, target, relative.EndsWith(Path.DirectorySeparatorChar));
    }
}
