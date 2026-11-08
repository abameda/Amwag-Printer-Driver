using Amwag.PrinterSetup.App.UI.Views;

namespace Amwag.PrinterSetup.App.Tests.UI;

public sealed class NetworkAddressInputTests
{
    [Theory]
    [InlineData("10.0.0.5", "10.0.0.5")]
    [InlineData("  192.168.1.50 ", "192.168.1.50")]
    [InlineData("printer-01", "printer-01")]
    [InlineData("tkt-prn.branch12.amwag.local", "tkt-prn.branch12.amwag.local")]
    [InlineData("fe80::1", "fe80::1")]
    public void Accepts_addresses_and_host_names(string input, string expected)
    {
        Assert.True(NetworkAddressInput.TryNormalize(input, out string address));
        Assert.Equal(expected, address);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("10.5")]
    [InlineData("10.0.0.300")]
    [InlineData("10.0.0")]
    [InlineData("-printer")]
    [InlineData("printer_01")]
    [InlineData("http://10.0.0.5")]
    [InlineData("\\\\server\\printer")]
    [InlineData("طابعة")]
    public void Rejects_anything_else(string? input) =>
        Assert.False(NetworkAddressInput.TryNormalize(input, out _));
}
