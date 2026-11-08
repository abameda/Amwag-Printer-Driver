using Amwag.PrinterSetup.Core.Units;

namespace Amwag.PrinterSetup.Core.Printing;

/// <summary>A paper size the driver exposes through DeviceCapabilities (DC_PAPERS / DC_PAPERNAMES / DC_PAPERSIZE).</summary>
public sealed record PaperCapability(short Id, string Name, int WidthTenthsOfMillimeter, int HeightTenthsOfMillimeter)
{
    /// <summary>Sizes within 1 mm are the same paper (the tolerance drivers match forms by size with).</summary>
    public const int SizeToleranceTenthsOfMillimeter = 10;

    /// <summary>How far this paper's width or height is from <paramref name="size"/>, in tenths of a millimeter (the larger of the two).</summary>
    public int DeviationFrom(PaperDimensions size) =>
        Math.Max(Math.Abs(WidthTenthsOfMillimeter - size.WidthTenthsOfMillimeter), Math.Abs(HeightTenthsOfMillimeter - size.HeightTenthsOfMillimeter));
}

/// <summary>A paper source the driver exposes through DeviceCapabilities (DC_BINS / DC_BINNAMES).</summary>
public sealed record BinCapability(short Id, string Name);

/// <summary>What the installed driver reports for a queue via DeviceCapabilities.</summary>
public sealed record DriverCapabilities
{
    public required string QueueName { get; init; }
    public IReadOnlyList<PaperCapability> Papers { get; init; } = [];
    public IReadOnlyList<BinCapability> Bins { get; init; } = [];

    /// <summary>DC_MINEXTENT / DC_MAXEXTENT in tenths of a millimeter (custom size limits); null when not reported.</summary>
    public int? MinWidthTenthsOfMillimeter { get; init; }
    public int? MinHeightTenthsOfMillimeter { get; init; }
    public int? MaxWidthTenthsOfMillimeter { get; init; }
    public int? MaxHeightTenthsOfMillimeter { get; init; }

    public bool SupportsDuplex { get; init; }
    /// <summary>DC_ORIENTATION: 0 (no landscape), 90 or 270.</summary>
    public int? LandscapeRotation { get; init; }
    /// <summary>DC_MEDIAREADY: form names currently loaded, when the driver reports them.</summary>
    public IReadOnlyList<string> MediaReady { get; init; } = [];
    public string? DriverDllVersion { get; init; }

    public PaperCapability? FindPaper(string name) =>
        Papers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The paper the driver offers for the ticket: the one named <paramref name="formName"/>, else the paper closest to
    /// <paramref name="size"/> within 1 mm. Drivers that keep their own paper list (the TA/UTAX/Kyocera KX v3 driver) do not
    /// list forms-database forms, but do list the custom page sizes made in their printing preferences. DMPAPER_USER is never
    /// chosen by size: it selects nothing without dmPaperWidth/dmPaperLength.
    /// </summary>
    public PaperCapability? FindTicketPaper(string formName, PaperDimensions size) =>
        FindPaper(formName) ?? Papers
            .Where(p => p.Id != DmPaper.User && p.DeviationFrom(size) <= PaperCapability.SizeToleranceTenthsOfMillimeter)
            .OrderBy(p => p.DeviationFrom(size))
            .FirstOrDefault();
}

/// <summary>Summary of the driver's PrintCapabilities document (Print Schema), used as an independent cross-check.</summary>
public sealed record PrintCapabilitiesSummary
{
    public bool Available { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<MediaSizeCapability> MediaSizes { get; init; } = [];
    public CustomMediaSizeRange? CustomSizeRange { get; init; }
    public IReadOnlyList<string> InputBins { get; init; } = [];
}

/// <summary>A psk:PageMediaSize option; sizes in micrometers (Print Schema unit).</summary>
public sealed record MediaSizeCapability(string Name, string? DisplayName, int? WidthMicrometers, int? HeightMicrometers);

public sealed record CustomMediaSizeRange(int MinWidthMicrometers, int MaxWidthMicrometers, int MinHeightMicrometers, int MaxHeightMicrometers);

/// <summary>How the driver expresses a DEVMODE as a PrintTicket (PTConvertDevModeToPrintTicket).</summary>
public sealed record PrintTicketMediaInfo
{
    public bool Available { get; init; }
    public string? Error { get; init; }
    public string? MediaSizeName { get; init; }
    public int? WidthMicrometers { get; init; }
    public int? HeightMicrometers { get; init; }
    public string? InputBin { get; init; }
    public string? Orientation { get; init; }
    public string? Duplex { get; init; }
}
