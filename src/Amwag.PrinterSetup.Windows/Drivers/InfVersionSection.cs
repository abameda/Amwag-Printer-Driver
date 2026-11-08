using System.Text;

namespace Amwag.PrinterSetup.Windows.Drivers;

/// <summary>Reads the catalog reference from an INF's [Version] section (all [Version] sections are merged, as Windows does).</summary>
internal static class InfVersionSection
{
    /// <summary>
    /// The catalog file name that applies to <paramref name="architecture"/>: CatalogFile.NT&lt;platform&gt;, then
    /// CatalogFile.NT, then CatalogFile. Null when the INF names none.
    /// </summary>
    public static string? ReadCatalogFile(string infPath, string architecture)
    {
        var entries = ReadEntries(infPath);
        string[] keys = ["CatalogFile.NT" + PlatformSuffix(architecture), "CatalogFile.NT", "CatalogFile"];
        foreach (string key in keys)
        {
            if (entries.TryGetValue(key, out string? value) && value.Length > 0)
                return value;
        }

        return null;
    }

    private static string PlatformSuffix(string architecture) => architecture.Trim().ToLowerInvariant() switch
    {
        "x64" or "amd64" => "amd64",
        "arm64" => "arm64",
        "x86" => "x86",
        var other => other,
    };

    private static Dictionary<string, string> ReadEntries(string infPath)
    {
        var entries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // INF files are UTF-16 (with BOM) or ANSI; the [Version] keys and catalog names are ASCII, so Latin-1 is lossless here.
        using var reader = new StreamReader(infPath, Encoding.Latin1, detectEncodingFromByteOrderMarks: true);
        bool inVersion = false;
        while (reader.ReadLine() is { } rawLine)
        {
            string line = StripComment(rawLine).Trim();
            if (line.Length == 0)
                continue;

            if (line[0] == '[')
            {
                int end = line.IndexOf(']');
                inVersion = end > 0 && line[1..end].Trim().Equals("Version", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            int equals = line.IndexOf('=');
            if (!inVersion || equals <= 0)
                continue;

            string key = line[..equals].Trim();
            string value = Unquote(line[(equals + 1)..].Trim());
            entries.TryAdd(key, value);
        }

        return entries;
    }

    private static string StripComment(string line)
    {
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                quoted = !quoted;
            else if (line[i] == ';' && !quoted)
                return line[..i];
        }

        return line;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1].Trim() : value;
}
