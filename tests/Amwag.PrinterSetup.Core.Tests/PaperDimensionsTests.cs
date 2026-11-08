using System.Globalization;
using Amwag.PrinterSetup.Core.Units;

namespace Amwag.PrinterSetup.Core.Tests;

public sealed class PaperDimensionsTests
{
    [Fact]
    public void Ticket_in_inches_is_exactly_82296_by_170180_micrometers()
    {
        var ticket = PaperDimensions.FromUnits(3.24m, 6.70m, LengthUnit.Inch);

        Assert.Equal(82_296, ticket.WidthMicrometers);
        Assert.Equal(170_180, ticket.HeightMicrometers);
        Assert.Equal(82.296m, ticket.WidthMillimeters);
        Assert.Equal(170.18m, ticket.HeightMillimeters);
        Assert.Equal(3.24m, ticket.WidthInches);
        Assert.Equal(6.70m, ticket.HeightInches);
    }

    [Fact]
    public void Ticket_converts_to_823_by_1702_tenths_of_a_millimeter()
    {
        var ticket = PaperDimensions.FromUnits(3.24m, 6.70m, LengthUnit.Inch);

        Assert.Equal(823, ticket.WidthTenthsOfMillimeter);
        Assert.Equal(1702, ticket.HeightTenthsOfMillimeter);
    }

    [Fact]
    public void Ticket_conversion_has_no_rounding_residual()
    {
        PaperDimensions.ToMicrometers(3.24m, LengthUnit.Inch, out decimal widthResidual);
        PaperDimensions.ToMicrometers(6.70m, LengthUnit.Inch, out decimal heightResidual);

        Assert.Equal(0m, widthResidual);
        Assert.Equal(0m, heightResidual);
    }

    [Fact]
    public void Inexact_length_reports_its_residual()
    {
        int micrometers = PaperDimensions.ToMicrometers(3.24001m, LengthUnit.Inch, out decimal residual);

        Assert.Equal(82_296, micrometers);
        Assert.NotEqual(0m, residual);
    }

    [Fact]
    public void Millimeters_convert_exactly()
    {
        var size = PaperDimensions.FromUnits(82.296m, 170.18m, LengthUnit.Millimeter);

        Assert.Equal(PaperDimensions.FromUnits(3.24m, 6.70m, LengthUnit.Inch), size);
    }

    [Fact]
    public void ToString_is_culture_invariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("82.296 mm x 170.18 mm", PaperDimensions.FromUnits(3.24m, 6.70m, LengthUnit.Inch).ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Non_positive_lengths_are_rejected(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PaperDimensions.ToMicrometers(value, LengthUnit.Millimeter, out _));
    }
}
