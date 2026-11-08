using Amwag.PrinterSetup.Core.Configuration;

namespace Amwag.PrinterSetup.Core.Tests;

public sealed class PrinterAddressTests
{
    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20")]
    [InlineData(" 10.0.0.5 ", "10.0.0.5")]
    [InlineData("fe80::1", "fe80::1")]
    [InlineData("[2001:DB8::5]", "2001:db8::5")]
    [InlineData("::ffff:192.168.1.20", "192.168.1.20")]
    [InlineData("[::ffff:192.168.1.20]", "192.168.1.20")]
    [InlineData("::ffff:c0a8:114", "192.168.1.20")]
    [InlineData("printer01", "printer01")]
    [InlineData("tkt-prn.branch12.amwag.local", "tkt-prn.branch12.amwag.local")]
    [InlineData("printer.local.", "printer.local")]
    public void Valid_addresses_get_their_canonical_spelling(string input, string expected)
    {
        Assert.True(PrinterAddress.TryNormalize(input, out string? normalized, out string? error), error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("[2001:db8::5]:9101")]
    [InlineData("[2001:db8::5]:")]
    [InlineData("[::1]:9100")]
    [InlineData("[2001:db8::5]:99999")]
    [InlineData("[[::1]]")]
    [InlineData("[::1")]
    [InlineData("::1]")]
    [InlineData("192.168.1.20:9100")]
    [InlineData("printer:9100")]
    public void A_port_or_stray_bracket_is_rejected_instead_of_dropped(string input)
    {
        Assert.False(PrinterAddress.TryNormalize(input, out _, out string? error));
        Assert.Contains("port number separately", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("10.5")]
    [InlineData("010.0.0.1")]
    [InlineData("192.168.001.020")]
    [InlineData("0x7f.0.0.1")]
    [InlineData("10.0.0.300")]
    [InlineData("pr_inter")]
    [InlineData("-printer")]
    [InlineData("print er")]
    [InlineData("http://10.0.0.5")]
    [InlineData("\\\\server\\printer")]
    public void Anything_else_is_rejected_with_a_reason(string? input)
    {
        Assert.False(PrinterAddress.TryNormalize(input, out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Ip_literals_also_return_the_address_and_host_names_do_not()
    {
        Assert.True(PrinterAddress.TryParse("::ffff:10.0.0.9", out _, out var ip, out _));
        Assert.Equal(System.Net.Sockets.AddressFamily.InterNetwork, ip!.AddressFamily);

        Assert.True(PrinterAddress.TryParse("printer01", out _, out var none, out _));
        Assert.Null(none);
    }
}
