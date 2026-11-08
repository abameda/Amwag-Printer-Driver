using System.Text.Json.Serialization;

namespace Amwag.PrinterSetup.Core.Units;

/// <summary>Length units accepted in the deployment configuration.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LengthUnit>))]
public enum LengthUnit
{
    [JsonStringEnumMemberName("mm")] Millimeter,
    [JsonStringEnumMemberName("in")] Inch,
}

/// <summary>
/// A paper size held in micrometers (thousandths of a millimeter), the native unit of the Windows forms
/// database (FORM_INFO_1.Size). Conversions use decimal arithmetic, so 3.24 in x 6.70 in is exactly
/// 82 296 x 170 180 µm with no rounding. DEVMODE uses tenths of a millimeter; that conversion is explicit and rounded.
/// </summary>
public readonly record struct PaperDimensions
{
    public const decimal MicrometersPerInch = 25_400m;
    public const decimal MicrometersPerMillimeter = 1_000m;

    public PaperDimensions(int widthMicrometers, int heightMicrometers)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(widthMicrometers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heightMicrometers);
        WidthMicrometers = widthMicrometers;
        HeightMicrometers = heightMicrometers;
    }

    public int WidthMicrometers { get; }
    public int HeightMicrometers { get; }

    public decimal WidthMillimeters => WidthMicrometers / MicrometersPerMillimeter;
    public decimal HeightMillimeters => HeightMicrometers / MicrometersPerMillimeter;
    public decimal WidthInches => WidthMicrometers / MicrometersPerInch;
    public decimal HeightInches => HeightMicrometers / MicrometersPerInch;

    /// <summary>DEVMODE dmPaperWidth unit (0.1 mm), rounded half away from zero (82 296 µm -> 823).</summary>
    public short WidthTenthsOfMillimeter => ToTenths(WidthMicrometers);

    /// <summary>DEVMODE dmPaperLength unit (0.1 mm), rounded half away from zero (170 180 µm -> 1702).</summary>
    public short HeightTenthsOfMillimeter => ToTenths(HeightMicrometers);

    public static PaperDimensions FromUnits(decimal width, decimal height, LengthUnit unit) =>
        new(ToMicrometers(width, unit, out _), ToMicrometers(height, unit, out _));

    /// <summary>
    /// Converts a length to whole micrometers. <paramref name="roundingError"/> receives the residual in micrometers
    /// (zero when the value is exactly representable, as it is for the AMWAG ticket).
    /// </summary>
    public static int ToMicrometers(decimal value, LengthUnit unit, out decimal roundingError)
    {
        if (value <= 0)
            throw new ArgumentOutOfRangeException(nameof(value), value, "Length must be positive.");

        decimal micrometers = value * (unit == LengthUnit.Inch ? MicrometersPerInch : MicrometersPerMillimeter);
        decimal rounded = decimal.Round(micrometers, 0, MidpointRounding.AwayFromZero);
        if (rounded > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), value, "Length is too large.");

        roundingError = rounded - micrometers;
        return (int)rounded;
    }

    private static short ToTenths(int micrometers) =>
        checked((short)decimal.Round(micrometers / 100m, 0, MidpointRounding.AwayFromZero));

    public override string ToString() =>
        FormattableString.Invariant($"{WidthMillimeters:0.###} mm x {HeightMillimeters:0.###} mm");
}
