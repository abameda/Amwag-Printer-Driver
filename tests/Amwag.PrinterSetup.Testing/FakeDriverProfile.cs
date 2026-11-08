using Amwag.PrinterSetup.Core.Printing;

namespace Amwag.PrinterSetup.Testing;

/// <summary>How a fake printer driver behaves: the papers, bins and limits it reports, and how it validates DEVMODEs.</summary>
public sealed record FakeDriverProfile
{
    /// <summary>Paper id the driver gives the first forms-database form it lists (ids above DMPAPER_USER, in forms order).</summary>
    public const short FirstFormPaperId = 257;

    /// <summary>Built-in papers with their standard DMPAPER ids (sizes in tenths of a millimeter).</summary>
    public static IReadOnlyList<PaperCapability> StandardPapers { get; } =
    [
        new(DmPaper.Letter, "Letter", 2159, 2794),
        new(5, "Legal", 2159, 3556),
        new(7, "Executive", 1842, 2667),
        new(DmPaper.A4, "A4", 2100, 2970),
        new(DmPaper.A5, "A5", 1480, 2100),
        new(70, "A6", 1050, 1480),
        new(13, "B5 (JIS)", 1820, 2570),
        new(20, "Envelope #10", 1048, 2413),
        new(27, "Envelope DL", 1100, 2200),
        new(28, "Envelope C5", 1620, 2290),
        new(37, "Envelope Monarch", 984, 1905),
    ];

    /// <summary>Auto selection, the multipurpose tray (DMBIN_MANUAL, "MP tray") and one cassette.</summary>
    public static IReadOnlyList<BinCapability> StandardBins { get; } =
    [
        new(DmBin.Auto, "Auto tray select"),
        new(DmBin.Manual, "MP tray"),
        new(257, "Cassette 1"),
    ];

    /// <summary>The approved TA KX driver as the sample configuration describes it (MP tray range 70x148..216x356 mm).</summary>
    public static FakeDriverProfile Default { get; } = new();

    public IReadOnlyList<PaperCapability> BuiltInPapers { get; init; } = StandardPapers;
    public IReadOnlyList<BinCapability> Bins { get; init; } = StandardBins;

    /// <summary>DC_MINEXTENT / DC_MAXEXTENT in tenths of a millimeter; forms outside are not listed.</summary>
    public int? MinWidthTenthsOfMillimeter { get; init; } = 700;
    public int? MinHeightTenthsOfMillimeter { get; init; } = 1480;
    public int? MaxWidthTenthsOfMillimeter { get; init; } = 2160;
    public int? MaxHeightTenthsOfMillimeter { get; init; } = 3560;

    public bool SupportsDuplex { get; init; } = true;

    /// <summary>Defaults of a queue that has just been created with this driver.</summary>
    public string DefaultPaperName { get; init; } = "A4";
    public short DefaultOrientation { get; init; } = DmOrientation.Portrait;
    public short DefaultDuplex { get; init; } = DmDuplex.Vertical;
    public short DefaultSource { get; init; } = DmBin.Auto;
    public short DefaultScale { get; init; } = 100;

    public short MinScale { get; init; } = 25;
    public short MaxScale { get; init; } = 400;

    /// <summary>dmDriverExtra of the DEVMODEs this driver produces.</summary>
    public int PrivateSize { get; init; } = 512;
    public ushort DevModeDriverVersion { get; init; } = 0x0600;
    public string? DriverDllVersion { get; init; } = "8.4.1013.0";

    /// <summary>Form names the driver never lists and replaces with its default paper during validation (it rejects them).</summary>
    public IReadOnlyList<string> OmittedForms { get; init; } = [];

    /// <summary>With <see cref="SourceBelowCassetteMinimum"/>: papers narrower than this width make the driver switch the source.</summary>
    public int? CassetteMinimumWidthTenthsOfMillimeter { get; init; }
    public short? SourceBelowCassetteMinimum { get; init; }

    /// <summary>The driver keeps the queue's current orientation/scale whatever is requested.</summary>
    public bool IgnoreOrientation { get; init; }
    public bool IgnoreScale { get; init; }

    /// <summary>
    /// Validation leaves DM_PAPERWIDTH/DM_PAPERLENGTH set with the resolved paper's size, as a v3 GDI driver may; the v4
    /// driver framework (and so this fake by default) clears both flags.
    /// </summary>
    public bool SetsPaperSizeFields { get; init; }

    /// <summary>
    /// DC_MINEXTENT/DC_MAXEXTENT report the bounding box of the listed papers instead of the custom size range, as the TA
    /// KXv4 driver measured on Windows does; the PrintCapabilities keep the range of the Min/Max properties.
    /// </summary>
    public bool ExtentsFromListedPapers { get; init; }

    /// <summary>Validation leaves dmFormName empty, as the TA/UTAX/Kyocera KX v3 driver does for every paper.</summary>
    public bool ClearsFormName { get; init; }

    public bool PrintCapabilitiesAvailable { get; init; } = true;
    public bool PrintTicketAvailable { get; init; } = true;

    /// <summary>When set, DescribePrintTicket returns it as is (e.g. to simulate a media size mismatch).</summary>
    public PrintTicketMediaInfo? PrintTicketOverride { get; init; }

    /// <summary>When set, GetCapabilities throws a spooler error with this Win32 code.</summary>
    public int? CapabilitiesWin32Error { get; init; }

    public bool Lists(string formName) => !OmittedForms.Contains(formName, StringComparer.OrdinalIgnoreCase);

    public bool Fits(int widthTenths, int heightTenths) =>
        (MinWidthTenthsOfMillimeter is not { } minW || widthTenths >= minW) &&
        (MinHeightTenthsOfMillimeter is not { } minH || heightTenths >= minH) &&
        (MaxWidthTenthsOfMillimeter is not { } maxW || widthTenths <= maxW) &&
        (MaxHeightTenthsOfMillimeter is not { } maxH || heightTenths <= maxH);

    public PaperCapability DefaultPaper =>
        BuiltInPapers.FirstOrDefault(p => string.Equals(p.Name, DefaultPaperName, StringComparison.OrdinalIgnoreCase)) ?? BuiltInPapers[0];
}
