using System.Globalization;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Core.Printing;
using Amwag.PrinterSetup.Core.Units;

namespace Amwag.PrinterSetup.Core.Setup;

/// <summary>
/// The custom paper sizes a driver accepts, in tenths of a millimeter. The PrintCapabilities' psk:CustomMediaSize parameter
/// limits are the driver's own range; DC_MINEXTENT/DC_MAXEXTENT are used only when the driver describes no custom size there,
/// because some drivers report the bounding box of their listed papers in them (the TA KXv4 driver: 92 x 148 .. 297 x 420 mm
/// while its custom range is 70 x 148 .. 216 x 356 mm; docs/research/kx-custom-paper-size.md section 6).
/// </summary>
internal sealed record DriverSizeRange(int? MinWidth, int? MinHeight, int? MaxWidth, int? MaxHeight, string Source)
{
    public const string FromPrintCapabilities = "printCapabilities";
    public const string FromDeviceCapabilities = "deviceCapabilities";

    /// <summary>The range from <paramref name="schema"/> when it has a custom size range, else the DeviceCapabilities extents.</summary>
    public static DriverSizeRange Of(DriverCapabilities caps, PrintCapabilitiesSummary? schema) =>
        schema is { Available: true, CustomSizeRange: { } range }
            ? new(Tenths(range.MinWidthMicrometers), Tenths(range.MinHeightMicrometers), Tenths(range.MaxWidthMicrometers), Tenths(range.MaxHeightMicrometers), FromPrintCapabilities)
            : new(caps.MinWidthTenthsOfMillimeter, caps.MinHeightTenthsOfMillimeter, caps.MaxWidthTenthsOfMillimeter, caps.MaxHeightTenthsOfMillimeter, FromDeviceCapabilities);

    /// <summary>
    /// Reads the queue's PrintCapabilities (read-only) for <see cref="Of"/>; a driver that cannot describe them leaves the
    /// DeviceCapabilities extents.
    /// </summary>
    public static DriverSizeRange Read(IPrintSpooler spooler, DriverCapabilities caps, ILog log)
    {
        try
        {
            return Of(caps, spooler.GetPrintCapabilities(caps.QueueName, null));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Warn("assess.printcapabilities.failed", $"GetPrintCapabilities failed for '{caps.QueueName}'; the DeviceCapabilities extents are used.", null, ex);
            return Of(caps, null);
        }
    }

    /// <summary>Whether <paramref name="size"/> is outside a known limit (an unknown limit never excludes it).</summary>
    public bool Excludes(PaperDimensions size) =>
        size.WidthTenthsOfMillimeter < MinWidth || size.HeightTenthsOfMillimeter < MinHeight ||
        size.WidthTenthsOfMillimeter > MaxWidth || size.HeightTenthsOfMillimeter > MaxHeight;

    public (string Key, string? Value)[] Data(string queueName) =>
    [
        ("queue", queueName),
        ("minWidth", Text(MinWidth)),
        ("minHeight", Text(MinHeight)),
        ("maxWidth", Text(MaxWidth)),
        ("maxHeight", Text(MaxHeight)),
        ("rangeSource", Source),
    ];

    /// <summary>"700..2160 x 1480..3560 tenths of a mm".</summary>
    public override string ToString() =>
        $"{Text(MinWidth) ?? "?"}..{Text(MaxWidth) ?? "?"} x {Text(MinHeight) ?? "?"}..{Text(MaxHeight) ?? "?"} tenths of a mm";

    private static int Tenths(int micrometers) => (int)Math.Round(micrometers / 100m, MidpointRounding.AwayFromZero);

    private static string? Text(int? value) => value?.ToString(CultureInfo.InvariantCulture);
}
