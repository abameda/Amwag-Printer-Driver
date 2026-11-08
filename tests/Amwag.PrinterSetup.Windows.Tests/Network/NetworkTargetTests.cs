using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Amwag.PrinterSetup.Windows.Network;

namespace Amwag.PrinterSetup.Windows.Tests.Network;

public sealed class NetworkTargetTests
{
    // Accepting an IPv4 literal reads the local adapter list (subnet broadcast check).
    [Theory]
    [Trait("Category", "WindowsIntegration")]
    [InlineData("192.168.1.20", "192.168.1.20")]
    [InlineData(" 10.0.0.5 ", "10.0.0.5")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("169.254.10.1", "169.254.10.1")]
    [InlineData("::1", "::1")]
    [InlineData("[::1]", "::1")]
    [InlineData("fe80::1", "fe80::1")]
    [InlineData("::ffff:192.168.1.20", "192.168.1.20")] // IPv4-mapped: probed over IPv4, not over an IPv6 socket
    [InlineData("[::ffff:192.168.1.20]", "192.168.1.20")]
    public void Accepts_ip_literals(string input, string expected)
    {
        Assert.True(NetworkTarget.TryParse(input, out NetworkTarget? target, out _));
        Assert.Equal(IPAddress.Parse(expected), target.Address);
        Assert.Null(target.HostName);
    }

    [Theory]
    [InlineData("printer01", "printer01")]
    [InlineData("prn-amwag.branch.local", "prn-amwag.branch.local")]
    [InlineData("printer.local.", "printer.local")]
    [InlineData("10.0.0.1a", "10.0.0.1a")]
    public void Accepts_host_names(string input, string expected)
    {
        Assert.True(NetworkTarget.TryParse(input, out NetworkTarget? target, out _));
        Assert.Null(target.Address);
        Assert.Equal(expected, target.HostName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("192.168.1")] // Windows would read this as 192.168.0.1
    [InlineData("1")] // ... and this as 0.0.0.1
    [InlineData("010.0.0.1")] // ... and this as octal 8.0.0.1
    [InlineData("192.168.001.020")]
    [InlineData("256.1.1.1")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1..2.3")]
    [InlineData("0x7f.0.0.1")] // hexadecimal legacy form
    [InlineData("0x7f000001")]
    [InlineData("printer:9100")]
    [InlineData("-printer")]
    [InlineData("printer-")]
    [InlineData("pr_inter")]
    [InlineData("a..b")]
    [InlineData("http://printer")]
    [InlineData("[::1]:9100")] // IPAddress.TryParse would drop the port
    [InlineData("[2001:db8::5]:")]
    [InlineData("[[::1]]")]
    public void Rejects_ambiguous_or_invalid_addresses(string? input)
    {
        Assert.False(NetworkTarget.TryParse(input, out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Rejects_a_label_longer_than_63_characters()
    {
        Assert.False(NetworkTarget.TryParse(new string('a', 64) + ".local", out _, out _));
        Assert.True(NetworkTarget.TryParse(new string('a', 63) + ".local", out _, out _));
    }

    [Theory]
    [InlineData("255.255.255.255", "broadcast")]
    [InlineData("224.0.0.1", "multicast")]
    [InlineData("239.255.255.250", "multicast")]
    [InlineData("240.0.0.1", "reserved")]
    [InlineData("0.0.0.0", "unspecified")]
    [InlineData("0.1.2.3", "reserved")]
    [InlineData("ff02::1", "multicast")]
    [InlineData("::", "unspecified")]
    public void Rejects_addresses_that_reach_no_or_many_hosts(string input, string expectedReason)
    {
        Assert.False(NetworkTarget.TryParse(input, out _, out string? error));
        Assert.Contains(expectedReason, error);
    }

    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public void Rejects_the_broadcast_address_of_a_local_subnet()
    {
        UnicastIPAddressInformation? local = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .FirstOrDefault(u => u.Address.AddressFamily == AddressFamily.InterNetwork && u.PrefixLength is >= 8 and <= 30);
        if (local is null)
            return; // No IPv4 subnet on this machine to derive a broadcast address from.

        byte[] octets = local.Address.GetAddressBytes();
        uint value = ((uint)octets[0] << 24) | ((uint)octets[1] << 16) | ((uint)octets[2] << 8) | octets[3];
        uint broadcast = value | (uint.MaxValue >> local.PrefixLength);
        var broadcastAddress = new IPAddress([(byte)(broadcast >> 24), (byte)(broadcast >> 16), (byte)(broadcast >> 8), (byte)broadcast]);

        Assert.False(NetworkTarget.IsSingleHost(broadcastAddress, out string? reason));
        Assert.Contains("local subnet", reason);
        Assert.True(NetworkTarget.IsSingleHost(local.Address, out _));
    }
}
