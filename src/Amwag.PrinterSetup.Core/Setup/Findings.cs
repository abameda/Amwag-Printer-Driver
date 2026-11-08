namespace Amwag.PrinterSetup.Core.Setup;

internal static class Findings
{
    public static Finding Create(FindingCode code, Severity severity, string detail, params (string Key, string? Value)[] data)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in data)
        {
            if (value is not null)
                values[key] = value;
        }
        return new Finding(code, severity, detail) { Data = values };
    }

    public static Finding Info(FindingCode code, string detail, params (string Key, string? Value)[] data) =>
        Create(code, Severity.Info, detail, data);

    public static Finding Warning(FindingCode code, string detail, params (string Key, string? Value)[] data) =>
        Create(code, Severity.Warning, detail, data);

    public static Finding Error(FindingCode code, string detail, params (string Key, string? Value)[] data) =>
        Create(code, Severity.Error, detail, data);

    /// <summary>Distinct by code, severity and detail, keeping the first occurrence's order.</summary>
    public static IReadOnlyList<Finding> Distinct(IEnumerable<Finding> findings) =>
        findings.DistinctBy(f => (f.Code, f.Severity, f.Detail)).ToList();
}
