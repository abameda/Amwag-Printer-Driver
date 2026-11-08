using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Amwag.PrinterSetup.Core.Configuration;

namespace Amwag.PrinterSetup.Windows.Network;

/// <summary>
/// A printer address to probe: an IP literal or a DNS host name, parsed by <see cref="PrinterAddress"/> (legacy numeric forms
/// and ports are rejected; an IPv4-mapped IPv6 address becomes plain IPv4), and naming exactly one host.
/// </summary>
internal sealed record NetworkTarget(IPAddress? Address, string? HostName)
{
    internal static bool TryParse(string? input, [NotNullWhen(true)] out NetworkTarget? target, [NotNullWhen(false)] out string? error)
    {
        target = null;
        if (!PrinterAddress.TryParse(input, out string? normalized, out IPAddress? address, out error))
            return false;
        return Accept(address is null ? new NetworkTarget(null, normalized) : new NetworkTarget(address, null), out target, out error);
    }

    /// <summary>
    /// False for addresses that do not name exactly one host; <paramref name="reason"/> then completes "the address is ...":
    /// unspecified, multicast, broadcast, reserved, or a local subnet's broadcast or network address.
    /// </summary>
    internal static bool IsSingleHost(IPAddress address, [NotNullWhen(false)] out string? reason)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        reason = null;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any))
                reason = "the unspecified address";
            else if (address.IsIPv6Multicast)
                reason = "a multicast address";
        }
        else
        {
            byte[] octets = address.GetAddressBytes();
            if (octets[0] == 0)
                reason = address.Equals(IPAddress.Any) ? "the unspecified address" : "in the reserved range 0.0.0.0/8";
            else if (octets[0] is >= 224 and <= 239)
                reason = "a multicast address";
            else if (octets[0] >= 240)
                reason = address.Equals(IPAddress.Broadcast) ? "the broadcast address" : "a reserved address";
            else if (IsLocalSubnetEdge(address))
                reason = "the broadcast or network address of a local subnet";
        }

        return reason is null;
    }

    private static bool Accept(NetworkTarget candidate, out NetworkTarget? target, out string? error)
    {
        if (candidate.Address is not null && !IsSingleHost(candidate.Address, out string? reason))
        {
            target = null;
            error = $"{candidate.Address} is {reason}, not a single printer.";
            return false;
        }

        target = candidate;
        error = null;
        return true;
    }

    private static bool IsLocalSubnetEdge(IPAddress address)
    {
        uint target = ToUInt32(address);
        try
        {
            foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                foreach (UnicastIPAddressInformation unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    // /31 and /32 subnets have no broadcast or network address (RFC 3021).
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork || unicast.PrefixLength is < 1 or > 30)
                        continue;

                    uint mask = uint.MaxValue << (32 - unicast.PrefixLength);
                    uint network = ToUInt32(unicast.Address) & mask;
                    if (target == network || target == (network | ~mask))
                        return true;
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Without the adapter list the check cannot run; Windows refuses broadcast sends without SO_BROADCAST anyway.
        }

        return false;
    }

    private static uint ToUInt32(IPAddress address)
    {
        byte[] octets = address.GetAddressBytes();
        return ((uint)octets[0] << 24) | ((uint)octets[1] << 16) | ((uint)octets[2] << 8) | octets[3];
    }
}
