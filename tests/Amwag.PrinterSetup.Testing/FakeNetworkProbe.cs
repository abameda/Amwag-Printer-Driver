using System.Net;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Configuration;
using Amwag.PrinterSetup.Core.Inventory;

namespace Amwag.PrinterSetup.Testing;

/// <summary>
/// Answers probes from per-address identities; unknown addresses time out (unreachable, SNMP silent). Like the real probe,
/// an answer carries the IP address contacted (<see cref="NetworkIdentity.ResolvedAddress"/>): an IP literal itself, unless
/// the identity names one. A host name the fake does not know does not resolve.
/// </summary>
public sealed class FakeNetworkProbe : INetworkPrinterProbe
{
    private readonly object _gate = new();
    private readonly Dictionary<string, NetworkIdentity> _identities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _probed = [];
    private readonly List<NetworkProbeOptions> _options = [];

    /// <summary>Every address probed, in order.</summary>
    public IReadOnlyList<string> ProbedAddresses
    {
        get { lock (_gate) return [.. _probed]; }
    }

    /// <summary>The options passed with each probe, in order.</summary>
    public IReadOnlyList<NetworkProbeOptions> ProbeOptions
    {
        get { lock (_gate) return [.. _options]; }
    }

    /// <summary>When set, every probe throws it.</summary>
    public Exception? Failure { get; set; }

    public void SetIdentity(NetworkIdentity identity)
    {
        lock (_gate)
            _identities[identity.Address] = identity;
    }

    /// <summary>
    /// Makes <paramref name="alias"/> (another spelling, such as the printer's DNS name) reach the printer at
    /// <paramref name="address"/>: probes of the alias answer with that printer's identity, its resolved address and serial
    /// number included, as long as the printer is known.
    /// </summary>
    public void SetAlias(string alias, string address)
    {
        lock (_gate)
            _aliases[alias] = address;
    }

    public void Remove(string address)
    {
        lock (_gate)
            _identities.Remove(address);
    }

    public NetworkIdentity Probe(string address, NetworkProbeOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string target;
        lock (_gate)
        {
            _probed.Add(address);
            _options.Add(options);
            if (Failure is not null)
                throw Failure;
            target = _aliases.TryGetValue(address, out string? aliased) ? aliased : address;
            if (_identities.TryGetValue(target, out var identity))
                return identity with { Address = address, ResolvedAddress = identity.ResolvedAddress ?? IpLiteral(target) };
        }

        return new NetworkIdentity
        {
            Address = address,
            ResolvedAddress = IpLiteral(target),
            RawPortReachable = false,
            SnmpResponded = false,
            Error = $"No response from {address} within {options.TimeoutMilliseconds} ms.",
        };
    }

    /// <summary>The address in its canonical form when it is an IP literal (what a probe of it contacts); otherwise null.</summary>
    public static string? IpLiteral(string address) =>
        PrinterAddress.TryParse(address, out string? normalized, out IPAddress? ip, out _) && ip is not null ? normalized : null;
}
