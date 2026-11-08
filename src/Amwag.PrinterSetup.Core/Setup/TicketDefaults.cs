using System.Globalization;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Printing;

namespace Amwag.PrinterSetup.Core.Setup;

internal enum SourceStatus { NotManaged, Found, NotFound }

/// <summary>The paper source the ticket defaults use, per the configured policy and the model's multipurpose tray rule.</summary>
internal sealed record SourceDecision(SourceStatus Status, BinCapability? Bin, bool Required);

/// <summary>
/// Default printing preferences a queue must carry for the ticket. Duplex and SourceId are null when not managed.
/// PaperWidth and PaperLength are the size the driver lists for the ticket paper (DC_PAPERSIZE, tenths of a millimeter); they
/// are never written, but a DEVMODE whose dmPaperWidth/dmPaperLength give another size does not select the ticket paper.
/// SourceRequired: the multipurpose tray must be kept (policy, or only that tray can feed the ticket), otherwise it is preferred.
/// </summary>
internal sealed record DesiredDefaults(string FormName, short PaperId, int PaperWidth, int PaperLength, short Orientation, short? Duplex, short Scale, short? SourceId, bool SourceRequired = true)
{
    /// <summary>
    /// The source the driver keeps instead of a multipurpose tray that is only preferred (see
    /// <see cref="TicketDefaults.AcceptDriverSource"/>); a DEVMODE with it has no paperSource difference. Null when not known.
    /// </summary>
    public short? SourceSubstitute { get; init; }

    /// <summary>Names of the managed fields whose values differ ("paper", "orientation", "duplex", "scale", "paperSource").</summary>
    public IReadOnlyList<string> Differences(DevMode devMode)
    {
        var fields = new List<string>();
        if (!PaperMatches(devMode))
            fields.Add("paper");
        if (devMode.Orientation != Orientation)
            fields.Add("orientation");
        if (Duplex is { } duplex && devMode.Duplex != duplex)
            fields.Add("duplex");
        if (devMode.Scale != Scale)
            fields.Add("scale");
        if (!SourceMatches(devMode))
            fields.Add("paperSource");
        return fields;
    }

    /// <summary>The source is not managed, is the multipurpose tray, or is what the driver keeps instead of a tray that is only preferred.</summary>
    public bool SourceMatches(DevMode devMode) =>
        SourceId is not { } source || devMode.DefaultSource == source || (SourceSubstitute is { } kept && devMode.DefaultSource == kept);

    /// <summary>
    /// Whether <paramref name="devMode"/> selects the ticket paper (<see cref="TicketDefaults.SelectsPaper"/>), and has no
    /// dmPaperWidth/dmPaperLength that override it with another size (a set size flag wins over dmPaperSize; the driver prints on that size).
    /// </summary>
    public bool PaperMatches(DevMode devMode) => TicketDefaults.SelectsPaper(devMode, FormName, PaperId) && !OverridesSize(devMode);

    /// <summary>
    /// Whether dmPaperWidth or dmPaperLength is set (flag and a positive value) more than 1 mm away from the ticket paper's size.
    /// Flags that match the size are no difference, so a driver that writes them is not rewritten on every run.
    /// </summary>
    public bool OverridesSize(DevMode devMode) =>
        Overrides(devMode, DevModeFields.PaperWidth, devMode.PaperWidth, PaperWidth) ||
        Overrides(devMode, DevModeFields.PaperLength, devMode.PaperLength, PaperLength);

    /// <summary>A copy of <paramref name="baseDevMode"/> carrying these settings; driver-private bytes are untouched.</summary>
    public DevMode ApplyTo(DevMode baseDevMode)
    {
        var devMode = baseDevMode.Clone();
        var fields = (devMode.Fields & ~(DevModeFields.PaperLength | DevModeFields.PaperWidth))
            | DevModeFields.FormName | DevModeFields.PaperSize | DevModeFields.Orientation | DevModeFields.Scale;
        devMode.FormName = FormName;
        devMode.PaperSize = PaperId;
        devMode.Orientation = Orientation;
        devMode.Scale = Scale;
        if (Duplex is { } duplex)
        {
            fields |= DevModeFields.Duplex;
            devMode.Duplex = duplex;
        }
        if (SourceId is { } source)
        {
            fields |= DevModeFields.DefaultSource;
            devMode.DefaultSource = source;
        }
        devMode.Fields = fields;
        return devMode;
    }

    /// <summary>
    /// The same managed fields, with the values <paramref name="devMode"/> holds. The paper size stays the ticket paper's, so a
    /// read-back that keeps an overriding dmPaperWidth/dmPaperLength still differs.
    /// </summary>
    public DesiredDefaults ValuesOf(DevMode devMode) =>
        new(devMode.FormName, devMode.PaperSize, PaperWidth, PaperLength, devMode.Orientation, Duplex is null ? null : devMode.Duplex, devMode.Scale,
            SourceId is null ? null : devMode.DefaultSource, SourceRequired);

    public override string ToString() => string.Create(CultureInfo.InvariantCulture,
        $"form '{FormName}' (paper {PaperId}), orientation {Orientation}, duplex {Duplex?.ToString(CultureInfo.InvariantCulture) ?? "unmanaged"}, scale {Scale} %, source {SourceId?.ToString(CultureInfo.InvariantCulture) ?? "driver default"}");

    private static bool Overrides(DevMode devMode, DevModeFields field, short value, int size) =>
        devMode.Has(field) && value > 0 && Math.Abs(value - size) > PaperCapability.SizeToleranceTenthsOfMillimeter;
}

/// <summary>Turns the configured print preferences into DEVMODE values.</summary>
internal static class TicketDefaults
{
    public static short Orientation(PageOrientationSetting setting) =>
        setting == PageOrientationSetting.Landscape ? DmOrientation.Landscape : DmOrientation.Portrait;

    public static short Duplex(DuplexSetting setting) => setting switch
    {
        DuplexSetting.LongEdge => DmDuplex.Vertical,
        DuplexSetting.ShortEdge => DmDuplex.Horizontal,
        _ => DmDuplex.Simplex,
    };

    /// <summary>
    /// The multipurpose tray: the first configured bin id the driver exposes, else a bin with a configured name (trimmed,
    /// case-insensitive). Not managed under the DriverDefault policy; required under RequireMultipurposeTray or when only the MP tray can feed the ticket.
    /// </summary>
    public static SourceDecision DecideSource(PrintPreferencesConfig preferences, ModelDefinition model, IReadOnlyList<BinCapability> bins)
    {
        if (preferences.PaperSource == PaperSourcePolicy.DriverDefault)
            return new SourceDecision(SourceStatus.NotManaged, null, false);

        var rule = model.TicketPaperSource;
        var bin = rule.BinIds.Select(id => bins.FirstOrDefault(b => b.Id == id)).FirstOrDefault(b => b is not null)
            ?? bins.FirstOrDefault(b => rule.BinNames.Any(n => string.Equals(b.Name.Trim(), n.Trim(), StringComparison.OrdinalIgnoreCase)));
        bool required = preferences.PaperSource == PaperSourcePolicy.RequireMultipurposeTray || rule.RequiredForTicket;
        return new SourceDecision(bin is null ? SourceStatus.NotFound : SourceStatus.Found, bin, required);
    }

    /// <summary>
    /// Whether <paramref name="devMode"/> selects the paper <paramref name="paperId"/>: its dmPaperSize, with dmFormName the
    /// paper's name or empty. The KX v3 driver leaves dmFormName empty in the DEVMODEs it returns, even for A4.
    /// </summary>
    public static bool SelectsPaper(DevMode devMode, string paperName, short paperId) =>
        devMode.PaperSize == paperId && (devMode.FormName.Length == 0 || string.Equals(devMode.FormName, paperName, StringComparison.OrdinalIgnoreCase));

    /// <summary>What the queue's defaults must be, given the paper the driver offers for the ticket (<see cref="DriverCapabilities.FindTicketPaper"/>).</summary>
    public static DesiredDefaults Desired(PrintPreferencesConfig preferences, PaperCapability paper, DriverCapabilities caps, SourceDecision source) =>
        new(
            paper.Name,
            paper.Id,
            paper.WidthTenthsOfMillimeter,
            paper.HeightTenthsOfMillimeter,
            Orientation(preferences.Orientation),
            caps.SupportsDuplex ? Duplex(preferences.Duplex) : null,
            (short)preferences.ScalePercent,
            source.Status == SourceStatus.Found ? source.Bin!.Id : null,
            source.Required);

    /// <summary>
    /// <paramref name="desired"/>, accepting the source the driver keeps instead of a multipurpose tray that is only preferred:
    /// validating the desired settings over <paramref name="current"/> (DocumentProperties, read-only) shows which source the
    /// driver turns the tray into, and setting the defaults writes that source with a PaperSourceRejected warning. Defaults
    /// that already carry it are then no difference, so they are not rewritten on every run. A required or unmanaged tray, a
    /// DEVMODE that has the tray, or a driver that keeps it leaves <paramref name="desired"/> as it is.
    /// </summary>
    public static DesiredDefaults AcceptDriverSource(DesiredDefaults desired, IPrintSpooler spooler, string queueName, DevMode current)
    {
        if (desired is not { SourceId: { } tray, SourceRequired: false } || current.DefaultSource == tray)
            return desired;
        short kept = spooler.ValidateDevMode(queueName, desired.ApplyTo(current)).DefaultSource;
        return kept == tray ? desired : desired with { SourceSubstitute = kept };
    }

    /// <summary>"AMWAG Ticket, 82.296 mm x 170.18 mm (3.24 in x 6.70 in)".</summary>
    public static string Describe(TicketPaperConfig ticket)
    {
        if (ticket.Width <= 0 || ticket.Height <= 0)
            return $"{ticket.FormName} (invalid dimensions)";

        var size = ticket.Dimensions;
        return string.Create(CultureInfo.InvariantCulture,
            $"{ticket.FormName}, {size.WidthMillimeters:0.###} mm x {size.HeightMillimeters:0.###} mm ({size.WidthInches:0.00} in x {size.HeightInches:0.00} in)");
    }

    /// <summary>"portrait, single-sided, 100 %" style summary of the configured preferences.</summary>
    public static string DescribePreferences(PrintPreferencesConfig preferences)
    {
        string orientation = preferences.Orientation == PageOrientationSetting.Landscape ? "landscape" : "portrait";
        string duplex = preferences.Duplex switch
        {
            DuplexSetting.LongEdge => "two-sided (long edge)",
            DuplexSetting.ShortEdge => "two-sided (short edge)",
            _ => "single-sided",
        };
        string source = preferences.PaperSource switch
        {
            PaperSourcePolicy.DriverDefault => "driver default source",
            PaperSourcePolicy.RequireMultipurposeTray => "multipurpose tray (required)",
            _ => "multipurpose tray",
        };
        return string.Create(CultureInfo.InvariantCulture, $"{orientation}, {duplex}, {preferences.ScalePercent} %, {source}");
    }

    public static string Micrometers(int width, int height) => string.Create(CultureInfo.InvariantCulture, $"{width} x {height} µm");

    /// <summary>"'AMWAG Ticket' (paper 257)", followed by ", size 2100 x 2970 tenths of a mm" when a dmPaperWidth/dmPaperLength flag is set.</summary>
    public static string DescribePaper(DevMode devMode)
    {
        string paper = string.Create(CultureInfo.InvariantCulture, $"'{devMode.FormName}' (paper {devMode.PaperSize})");
        return devMode.Has(DevModeFields.PaperWidth) || devMode.Has(DevModeFields.PaperLength)
            ? paper + string.Create(CultureInfo.InvariantCulture, $", size {devMode.PaperWidth} x {devMode.PaperLength} tenths of a mm")
            : paper;
    }
}
