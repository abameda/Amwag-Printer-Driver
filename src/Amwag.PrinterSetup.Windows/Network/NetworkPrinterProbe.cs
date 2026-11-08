using System.Net;
using System.Net.Sockets;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Logging;

namespace Amwag.PrinterSetup.Windows.Network;

/// <summary>Identifies one network printer: TCP connect to the raw port and a single SNMP GET. Never scans.</summary>
public sealed class NetworkPrinterProbe : INetworkPrinterProbe
{
    internal const string SysDescrOid = "1.3.6.1.2.1.1.1.0";
    internal const string SysObjectIdOid = "1.3.6.1.2.1.1.2.0";
    internal const string HrDeviceDescrOid = "1.3.6.1.2.1.25.3.2.1.3.1";
    internal const string PrinterNameOid = "1.3.6.1.2.1.43.5.1.1.16.1";
    internal const string SerialNumberOid = "1.3.6.1.2.1.43.5.1.1.17.1";

    // PWG 5107.1 ppmPrinterIEEE1284DeviceId ::= { ppmPrinterEntry 3 }, ppmMIB ::= { enterprises pwg(2699) mibs(1) ppmMIB(2) },
    // for ppmPrinterIndex 1.
    internal const string Ieee1284DeviceIdOid = "1.3.6.1.4.1.2699.1.2.1.2.1.1.3.1";

    // Kyocera private MIB kcprtGeneralModelName.1 (enterprise 1347); the TA/UTAX KX installer identifies models with it.
    internal const string KyoceraModelNameOid = "1.3.6.1.4.1.1347.43.5.1.1.1.1";

    private static readonly string[] IdentityOids =
        [SysDescrOid, SysObjectIdOid, HrDeviceDescrOid, PrinterNameOid, SerialNumberOid, Ieee1284DeviceIdOid, KyoceraModelNameOid];

    private static readonly SnmpVersion[] VersionsToTry = [SnmpVersion.V2c, SnmpVersion.V1];

    private readonly ILog _log;
    private readonly SnmpClient _snmp;

    public NetworkPrinterProbe(ILog log)
        : this(log, SnmpClient.DefaultPort)
    {
    }

    /// <summary>Lets tests point the SNMP queries at a loopback fake agent on another UDP port.</summary>
    internal NetworkPrinterProbe(ILog log, int snmpPort)
    {
        _log = log;
        _snmp = new SnmpClient(log, snmpPort);
    }

    /// <summary>
    /// Contacts only <paramref name="address"/> (a host name is resolved once, IPv4 preferred). The IP address contacted is
    /// reported as <see cref="NetworkIdentity.ResolvedAddress"/> (an IPv4-mapped IPv6 address as plain IPv4), so detection can
    /// tell that two spellings reach one printer. Failures are reported in <see cref="NetworkIdentity.Error"/>; only
    /// <see cref="OperationCanceledException"/> for <paramref name="cancellationToken"/> propagates. The raw port check runs
    /// alongside SNMP, so a silent host takes about four timeouts (two attempts each for v2c and v1).
    /// </summary>
    public NetworkIdentity Probe(string address, NetworkProbeOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Every await below uses ConfigureAwait(false), so blocking here cannot deadlock a UI synchronization context.
            return ProbeAsync(address, options, cancellationToken).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warn("network.probe.failed", $"Probing {address} failed unexpectedly.", new Dictionary<string, object?> { ["address"] = address }, ex);
            return new NetworkIdentity { Address = address, Error = $"Probe failed: {ex.Message}" };
        }
    }

    private async Task<NetworkIdentity> ProbeAsync(string address, NetworkProbeOptions options, CancellationToken cancellationToken)
    {
        if (InvalidOptions(options) is { } invalid)
            return NotContacted(address, invalid);
        if (!NetworkTarget.TryParse(address, out NetworkTarget? target, out string? parseError))
            return NotContacted(address, parseError);

        var timeout = TimeSpan.FromMilliseconds(options.TimeoutMilliseconds);
        IPAddress? ip = target.Address;
        if (ip is null)
        {
            (ip, string? resolveError) = await ResolveAsync(target.HostName!, options.TimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
            if (ip is null)
                return NotContacted(address, resolveError!);
            if (!NetworkTarget.IsSingleHost(ip, out string? reason))
                return NotContacted(address, $"{target.HostName} resolves to {ip}, which is {reason}, not a single printer.");
        }

        _log.Debug("network.probe.started", $"Probing {address} ({ip}).", new Dictionary<string, object?>
        {
            ["address"] = address,
            ["resolvedAddress"] = ip.ToString(),
            ["rawPort"] = options.RawPort,
            ["useSnmp"] = options.UseSnmp,
            ["timeoutMs"] = options.TimeoutMilliseconds,
        });

        Task<string?> rawPort = ConnectAsync(ip, options.RawPort, timeout, cancellationToken);
        Task<SnmpResult> snmp = options.UseSnmp
            ? QuerySnmpAsync(ip, options.SnmpCommunity, timeout, cancellationToken)
            : Task.FromResult(SnmpResult.NotQueried);
        await Task.WhenAll(rawPort, snmp).ConfigureAwait(false);
        string? rawPortError = await rawPort.ConfigureAwait(false);
        SnmpResult snmpResult = await snmp.ConfigureAwait(false);

        var identity = new NetworkIdentity
        {
            Address = address,
            ResolvedAddress = ip.ToString(),
            RawPortReachable = rawPortError is null,
            SnmpResponded = snmpResult.Responded,
            SysDescr = snmpResult.Text(SysDescrOid),
            SysObjectId = snmpResult.ObjectId(SysObjectIdOid),
            DeviceDescription = snmpResult.Text(HrDeviceDescrOid),
            PrinterName = snmpResult.Text(PrinterNameOid),
            Ieee1284DeviceId = snmpResult.Text(Ieee1284DeviceIdOid),
            VendorModelName = snmpResult.Text(KyoceraModelNameOid),
            SerialNumber = snmpResult.Text(SerialNumberOid),
            Error = JoinErrors(rawPortError, snmpResult.Error),
        };

        _log.Info("network.probe.completed",
            $"Probed {address}: raw port {(identity.RawPortReachable ? "reachable" : "not reachable")}, SNMP {(identity.SnmpResponded ? "answered" : "did not answer")}.",
            new Dictionary<string, object?>
            {
                ["address"] = address,
                ["resolvedAddress"] = ip.ToString(),
                ["rawPort"] = options.RawPort,
                ["rawPortReachable"] = identity.RawPortReachable,
                ["snmpResponded"] = identity.SnmpResponded,
                ["snmpVersion"] = snmpResult.Version is { } version ? VersionName(version) : null,
                ["sysDescr"] = identity.SysDescr,
                ["sysObjectId"] = identity.SysObjectId,
                ["deviceDescription"] = identity.DeviceDescription,
                ["printerName"] = identity.PrinterName,
                ["ieee1284DeviceId"] = identity.Ieee1284DeviceId,
                ["vendorModelName"] = identity.VendorModelName,
                ["serialNumber"] = identity.SerialNumber,
                ["error"] = identity.Error,
            });
        return identity;
    }

    private static string? InvalidOptions(NetworkProbeOptions options) => options switch
    {
        { TimeoutMilliseconds: <= 0 } => $"Invalid network timeout of {options.TimeoutMilliseconds} ms.",
        { RawPort: < 1 or > 65535 } => $"Invalid raw print port {options.RawPort}.",
        { UseSnmp: true, SnmpCommunity: null } => "SNMP is enabled but no community is configured.",
        _ => null,
    };

    private NetworkIdentity NotContacted(string address, string error)
    {
        _log.Warn("network.probe.notcontacted", $"Printer {address} was not contacted: {error}", new Dictionary<string, object?>
        {
            ["address"] = address,
            ["error"] = error,
        });
        return new NetworkIdentity { Address = address, Error = error };
    }

    private static async Task<(IPAddress? Address, string? Error)> ResolveAsync(string hostName, int timeoutMilliseconds, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeoutMilliseconds);
        try
        {
            // A hosts-file or DNS answer can be IPv4-mapped IPv6 ("::ffff:10.0.0.5"); an IPv6 socket cannot connect to it
            // (AddressNotAvailable), so it is used as the IPv4 address it stands for.
            IPAddress[] resolved = await Dns.GetHostAddressesAsync(hostName, timeoutSource.Token).ConfigureAwait(false);
            IPAddress[] addresses = [.. resolved.Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a)];
            IPAddress? chosen = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6);
            return chosen is null ? (null, $"{hostName} did not resolve to an IPv4 or IPv6 address.") : (chosen, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, $"Resolving {hostName} took longer than {timeoutMilliseconds} ms.");
        }
        catch (SocketException ex)
        {
            return (null, $"Resolving {hostName} failed: {ex.SocketErrorCode} ({ex.ErrorCode}).");
        }
    }

    /// <summary>Null when the TCP connection was accepted; otherwise why not. The connection is closed without sending data.</summary>
    private static async Task<string?> ConnectAsync(IPAddress address, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), timeoutSource.Token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Windows retries a refused SYN for about two seconds, so a closed port can also end up here.
            return $"TCP port {port}: no connection within {(int)timeout.TotalMilliseconds} ms";
        }
        catch (SocketException ex)
        {
            return $"TCP port {port}: {ex.SocketErrorCode} ({ex.ErrorCode})";
        }
    }

    private async Task<SnmpResult> QuerySnmpAsync(IPAddress address, string community, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            foreach (SnmpVersion version in VersionsToTry)
            {
                var query = new SnmpQuery(address, version, community, timeout, cancellationToken);
                if (!await CollectAsync(query, [.. IdentityOids]).ConfigureAwait(false))
                    continue;

                string? error = query.Problems.Count == 0 ? null : $"SNMP {VersionName(version)}: {string.Join("; ", query.Problems)}";
                return new SnmpResult(true, version, query.Values, error);
            }

            return SnmpResult.Failed(
                $"SNMP: no response to v2c or v1 GET on UDP port {_snmp.Port} ({SnmpClient.Attempts} attempts of {(int)timeout.TotalMilliseconds} ms each); " +
                "SNMP may be disabled, the community not accepted, or the host unreachable");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
        {
            return SnmpResult.Failed($"SNMP: no agent listens on UDP port {_snmp.Port} (ICMP port unreachable)");
        }
        catch (SocketException ex)
        {
            return SnmpResult.Failed($"SNMP: {ex.SocketErrorCode} ({ex.ErrorCode})");
        }
    }

    /// <summary>
    /// GETs <paramref name="oids"/>; values the agent lacks (noSuchObject, noSuchInstance) are left out. When the agent rejects
    /// the whole request because of one OID (an SNMPv1 noSuchName with its error-index), the request is repeated without that
    /// OID; tooBig splits it into one GET per OID. Returns false when the agent never answered.
    /// </summary>
    private async Task<bool> CollectAsync(SnmpQuery query, List<string> oids)
    {
        bool answered = false;
        while (oids.Count > 0)
        {
            SnmpMessage? response = await _snmp.GetAsync(query.Address, query.Version, query.Community, oids, query.Timeout, query.CancellationToken)
                .ConfigureAwait(false);
            if (response is null)
            {
                if (answered)
                    query.Problems.Add($"no answer to the follow-up request for {string.Join(", ", oids)}");
                return answered;
            }

            answered = true;
            if (response.ErrorStatus == SnmpCodec.NoError)
            {
                Record(query, oids, response);
                return true;
            }

            if (response.ErrorStatus == SnmpCodec.TooBig && oids.Count > 1)
            {
                foreach (string oid in oids)
                {
                    if (!await CollectAsync(query, [oid]).ConfigureAwait(false))
                        query.Problems.Add($"no answer for {oid}");
                }

                return true;
            }

            if (response.ErrorIndex >= 1 && response.ErrorIndex <= oids.Count)
            {
                string rejected = oids[response.ErrorIndex - 1];
                _log.Debug("network.snmp.oid.rejected", $"The agent rejected {rejected} ({SnmpCodec.DescribeError(response.ErrorStatus)}); asking again without it.",
                    new Dictionary<string, object?>
                    {
                        ["oid"] = rejected,
                        ["errorStatus"] = SnmpCodec.DescribeError(response.ErrorStatus),
                        ["version"] = VersionName(query.Version),
                    });
                oids.RemoveAt(response.ErrorIndex - 1);
                continue;
            }

            query.Problems.Add($"the agent answered {SnmpCodec.DescribeError(response.ErrorStatus)} (error-index {response.ErrorIndex}) for the whole request");
            return true;
        }

        return answered;
    }

    private void Record(SnmpQuery query, List<string> requested, SnmpMessage response)
    {
        foreach (SnmpVarBind varBind in response.VarBinds)
        {
            if (!requested.Contains(varBind.Oid))
                continue;

            if (varBind.Value.IsException)
            {
                _log.Debug("network.snmp.value.absent", $"The agent has no value for {varBind.Oid} ({varBind.Value.Type}).", new Dictionary<string, object?>
                {
                    ["oid"] = varBind.Oid,
                    ["exception"] = varBind.Value.Type.ToString(),
                });
                continue;
            }

            query.Values[varBind.Oid] = varBind.Value;
        }
    }

    private static string VersionName(SnmpVersion version) => version == SnmpVersion.V2c ? "v2c" : "v1";

    private static string? JoinErrors(params string?[] errors)
    {
        string[] present = [.. errors.OfType<string>()];
        return present.Length == 0 ? null : string.Join("; ", present);
    }

    private sealed record SnmpQuery(IPAddress Address, SnmpVersion Version, string Community, TimeSpan Timeout, CancellationToken CancellationToken)
    {
        public Dictionary<string, SnmpValue> Values { get; } = new(StringComparer.Ordinal);
        public List<string> Problems { get; } = [];
    }

    private sealed record SnmpResult(bool Responded, SnmpVersion? Version, IReadOnlyDictionary<string, SnmpValue> Values, string? Error)
    {
        public static readonly SnmpResult NotQueried = new(false, null, new Dictionary<string, SnmpValue>(), null);

        public static SnmpResult Failed(string error) => new(false, null, new Dictionary<string, SnmpValue>(), error);

        /// <summary>A DisplayString-like value with surrounding white space removed; null when absent, empty or of another type.</summary>
        public string? Text(string oid) =>
            Values.TryGetValue(oid, out SnmpValue? value) && value.Type == SnmpValueType.OctetString && !string.IsNullOrWhiteSpace(value.Text)
                ? value.Text.Trim()
                : null;

        public string? ObjectId(string oid) =>
            Values.TryGetValue(oid, out SnmpValue? value) && value.Type == SnmpValueType.ObjectIdentifier ? value.Text : null;
    }
}
