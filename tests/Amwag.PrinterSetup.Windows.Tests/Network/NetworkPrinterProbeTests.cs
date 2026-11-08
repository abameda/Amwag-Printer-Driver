using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Amwag.PrinterSetup.Core.Abstractions;
using Amwag.PrinterSetup.Core.Inventory;
using Amwag.PrinterSetup.Core.Logging;
using Amwag.PrinterSetup.Windows.Network;

namespace Amwag.PrinterSetup.Windows.Tests.Network;

/// <summary>Real sockets, loopback only (127.0.0.1); SNMP answers come from <see cref="FakeSnmpAgent"/>.</summary>
[Trait("Category", "WindowsIntegration")]
public sealed class NetworkPrinterProbeTests
{
    private const string DeviceId = "MFG:Kyocera;CMD:PCLXL,PCL5E,POSTSCRIPT,PJL;MDL:ECOSYS P3045dn;CLS:PRINTER;";

    private static readonly string[] IdentityOids =
    [
        NetworkPrinterProbe.SysDescrOid,
        NetworkPrinterProbe.SysObjectIdOid,
        NetworkPrinterProbe.HrDeviceDescrOid,
        NetworkPrinterProbe.PrinterNameOid,
        NetworkPrinterProbe.SerialNumberOid,
        NetworkPrinterProbe.Ieee1284DeviceIdOid,
        NetworkPrinterProbe.KyoceraModelNameOid,
    ];

    [Fact]
    public void Unused_ports_on_loopback_report_unreachable_quickly_without_throwing()
    {
        var log = new RecordingLog();
        var probe = new NetworkPrinterProbe(log, UnusedUdpPort());
        var stopwatch = Stopwatch.StartNew();

        NetworkIdentity identity = probe.Probe("127.0.0.1", Options(UnusedTcpPort(), timeoutMs: 1000), CancellationToken.None);

        Assert.Equal("127.0.0.1", identity.Address);
        Assert.Equal("127.0.0.1", identity.ResolvedAddress); // contacted, although nothing answered
        Assert.False(identity.RawPortReachable);
        Assert.False(identity.SnmpResponded);
        Assert.Null(identity.SysDescr);
        Assert.Contains("TCP port", identity.Error);
        Assert.Contains("SNMP", identity.Error);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"took {stopwatch.Elapsed}");
        Assert.DoesNotContain(log.Entries, e => e.Event == "network.probe.failed");
    }

    [Fact]
    public void Public_constructor_uses_udp_161_on_loopback_without_throwing()
    {
        var probe = new NetworkPrinterProbe(NullLog.Instance);

        NetworkIdentity identity = probe.Probe("127.0.0.1", Options(UnusedTcpPort(), timeoutMs: 1000), CancellationToken.None);

        Assert.False(identity.RawPortReachable);
        Assert.NotNull(identity.Error);
    }

    [Fact]
    public void Reports_a_listening_raw_port_as_reachable()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        NetworkIdentity identity = new NetworkPrinterProbe(NullLog.Instance)
            .Probe("127.0.0.1", Options(Port(listener), useSnmp: false), CancellationToken.None);

        Assert.True(identity.RawPortReachable);
        Assert.False(identity.SnmpResponded);
        Assert.Null(identity.Error);
    }

    [Fact]
    public void Resolves_a_host_name_once_and_prefers_ipv4()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        NetworkIdentity identity = new NetworkPrinterProbe(NullLog.Instance)
            .Probe("localhost", Options(Port(listener), useSnmp: false), CancellationToken.None);

        Assert.Equal("localhost", identity.Address);
        Assert.Equal("127.0.0.1", identity.ResolvedAddress);
        Assert.True(identity.RawPortReachable);
    }

    [Theory]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("[::ffff:127.0.0.1]")]
    [InlineData("::ffff:7f00:1")]
    public void Probes_an_ipv4_mapped_address_over_ipv4(string address)
    {
        // An IPv6 socket (not dual-mode) cannot connect to an IPv4-mapped address: it fails with AddressNotAvailable.
        using var agent = new FakeSnmpAgent(request =>
            [Ber.Response(1, request.RequestId, Ber.VarBind(NetworkPrinterProbe.SysDescrOid, Ber.OctetString("TA_UTAX Printing System")))]);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        NetworkIdentity identity = new NetworkPrinterProbe(NullLog.Instance, agent.Port)
            .Probe(address, Options(Port(listener)), CancellationToken.None);

        Assert.Equal(address, identity.Address);
        Assert.Equal("127.0.0.1", identity.ResolvedAddress);
        Assert.True(identity.RawPortReachable, identity.Error);
        Assert.True(identity.SnmpResponded, identity.Error);
        Assert.Equal("TA_UTAX Printing System", identity.SysDescr);
    }

    [Fact]
    public void Reads_the_identity_from_a_v2c_agent_in_one_request()
    {
        using var agent = new FakeSnmpAgent(request =>
        [
            Ber.Response(1, request.RequestId,
                Ber.VarBind(NetworkPrinterProbe.SysDescrOid, Ber.OctetString("KYOCERA Document Solutions Printing System")),
                Ber.VarBind(NetworkPrinterProbe.SysObjectIdOid, Ber.Oid("1.3.6.1.4.1.1347.41")),
                Ber.VarBind(NetworkPrinterProbe.HrDeviceDescrOid, Ber.OctetString("  ECOSYS P3045dn \0\0")),
                Ber.VarBind(NetworkPrinterProbe.PrinterNameOid, Ber.NoSuchInstance),
                Ber.VarBind(NetworkPrinterProbe.SerialNumberOid, Ber.OctetString("R4P0Z00001")),
                Ber.VarBind(NetworkPrinterProbe.Ieee1284DeviceIdOid, Ber.OctetString(DeviceId)),
                Ber.VarBind(NetworkPrinterProbe.KyoceraModelNameOid, Ber.OctetString("ECOSYS P3045dn"))),
        ]);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();

        NetworkIdentity identity = new NetworkPrinterProbe(NullLog.Instance, agent.Port)
            .Probe("127.0.0.1", Options(Port(listener)), CancellationToken.None);

        Assert.True(identity.RawPortReachable);
        Assert.True(identity.SnmpResponded);
        Assert.Equal("KYOCERA Document Solutions Printing System", identity.SysDescr);
        Assert.Equal("1.3.6.1.4.1.1347.41", identity.SysObjectId);
        Assert.Equal("ECOSYS P3045dn", identity.DeviceDescription);
        Assert.Null(identity.PrinterName);
        Assert.Equal("R4P0Z00001", identity.SerialNumber);
        Assert.Equal(DeviceId, identity.Ieee1284DeviceId);
        Assert.Equal("ECOSYS P3045dn", identity.VendorModelName);
        Assert.Null(identity.Error);

        SnmpMessage request = Assert.Single(agent.Requests);
        Assert.Equal(SnmpVersion.V2c, request.Version);
        Assert.Equal(SnmpPduType.GetRequest, request.PduType);
        Assert.Equal(IdentityOids, request.VarBinds.Select(v => v.Oid));
    }

    [Fact]
    public void Falls_back_to_v1_and_repeats_the_request_without_the_rejected_oid()
    {
        using var agent = new FakeSnmpAgent(request =>
        {
            if (request.Version == SnmpVersion.V2c)
                return []; // A v1-only agent drops v2c messages.

            string[] oids = [.. request.VarBinds.Select(v => v.Oid)];
            int missing = Array.IndexOf(oids, NetworkPrinterProbe.Ieee1284DeviceIdOid);
            if (missing >= 0)
                return [Ber.Message(0, "public", 0xA2, request.RequestId, 2, missing + 1, [.. oids.Select(o => Ber.VarBind(o, Ber.Null))])];

            return [Ber.Response(0, request.RequestId, [.. oids.Select(o => Ber.VarBind(o, ValueFor(o)))])];
        });

        NetworkIdentity identity = new NetworkPrinterProbe(NullLog.Instance, agent.Port)
            .Probe("127.0.0.1", Options(UnusedTcpPort(), timeoutMs: 300), CancellationToken.None);

        Assert.True(identity.SnmpResponded);
        Assert.Equal("sysDescr value", identity.SysDescr);
        Assert.Equal("1.3.6.1.4.1.1347.41", identity.SysObjectId);
        Assert.Equal("printer name value", identity.PrinterName);
        Assert.Equal("vendor model value", identity.VendorModelName);
        Assert.Null(identity.Ieee1284DeviceId);
        Assert.DoesNotContain("SNMP", identity.Error);

        SnmpMessage[] requests = [.. agent.Requests];
        Assert.Equal([SnmpVersion.V2c, SnmpVersion.V2c, SnmpVersion.V1, SnmpVersion.V1], requests.Select(r => r.Version));
        Assert.Equal(requests[0].RequestId, requests[1].RequestId);
        Assert.Equal(IdentityOids, requests[2].VarBinds.Select(v => v.Oid));
        Assert.Equal(IdentityOids.Where(o => o != NetworkPrinterProbe.Ieee1284DeviceIdOid), requests[3].VarBinds.Select(v => v.Oid));
    }

    [Fact]
    public void Ignores_mismatched_and_malformed_datagrams_while_waiting()
    {
        using var agent = new FakeSnmpAgent(request =>
        [
            Ber.Response(1, request.RequestId + 1, Ber.VarBind(NetworkPrinterProbe.SysDescrOid, Ber.OctetString("wrong request-id"))),
            [0x30, 0x03, 0x02, 0x01],
            Ber.Response(0, request.RequestId, Ber.VarBind(NetworkPrinterProbe.SysDescrOid, Ber.OctetString("wrong version"))),
            Ber.Message(1, "public", 0xA0, request.RequestId, 0, 0, Ber.VarBind(NetworkPrinterProbe.SysDescrOid, Ber.OctetString("not a response"))),
            Ber.Response(1, request.RequestId, Ber.VarBind(NetworkPrinterProbe.SysDescrOid, Ber.OctetString("right one"))),
        ]);

        NetworkIdentity identity = new NetworkPrinterProbe(NullLog.Instance, agent.Port)
            .Probe("127.0.0.1", Options(UnusedTcpPort(), timeoutMs: 1000), CancellationToken.None);

        Assert.Equal("right one", identity.SysDescr);
        Assert.Single(agent.Requests);
    }

    [Fact]
    public void Retries_once_with_the_same_request_id_after_a_lost_answer()
    {
        int seen = 0;
        using var agent = new FakeSnmpAgent(request =>
            Interlocked.Increment(ref seen) == 1
                ? []
                : [Ber.Response(1, request.RequestId, Ber.VarBind(NetworkPrinterProbe.SysDescrOid, Ber.OctetString("second attempt")))]);

        NetworkIdentity identity = new NetworkPrinterProbe(NullLog.Instance, agent.Port)
            .Probe("127.0.0.1", Options(UnusedTcpPort(), timeoutMs: 300), CancellationToken.None);

        Assert.Equal("second attempt", identity.SysDescr);
        SnmpMessage[] requests = [.. agent.Requests];
        Assert.Equal(2, requests.Length);
        Assert.Equal(requests[0].RequestId, requests[1].RequestId);
    }

    [Fact]
    public void Splits_the_request_when_the_agent_answers_tooBig()
    {
        using var agent = new FakeSnmpAgent(request =>
            request.VarBinds.Count > 1
                ? [Ber.Message(1, "public", 0xA2, request.RequestId, 1, 0, [.. request.VarBinds.Select(v => Ber.VarBind(v.Oid, Ber.Null))])]
                : [Ber.Response(1, request.RequestId, Ber.VarBind(request.VarBinds[0].Oid, ValueFor(request.VarBinds[0].Oid)))]);

        NetworkIdentity identity = new NetworkPrinterProbe(NullLog.Instance, agent.Port)
            .Probe("127.0.0.1", Options(UnusedTcpPort(), timeoutMs: 1000), CancellationToken.None);

        Assert.Equal("sysDescr value", identity.SysDescr);
        Assert.Equal("device id value", identity.Ieee1284DeviceId);
        Assert.Equal(1 + IdentityOids.Length, agent.Requests.Count);
    }

    [Fact]
    public void Reports_an_agent_error_for_the_whole_request()
    {
        using var agent = new FakeSnmpAgent(request =>
            [Ber.Message(1, "public", 0xA2, request.RequestId, 5, 0, [.. request.VarBinds.Select(v => Ber.VarBind(v.Oid, Ber.Null))])]);

        NetworkIdentity identity = new NetworkPrinterProbe(NullLog.Instance, agent.Port)
            .Probe("127.0.0.1", Options(UnusedTcpPort(), timeoutMs: 1000), CancellationToken.None);

        Assert.True(identity.SnmpResponded);
        Assert.Null(identity.SysDescr);
        Assert.Contains("genErr", identity.Error);
    }

    [Fact]
    public void Propagates_cancellation_while_waiting_for_the_agent()
    {
        using var agent = new FakeSnmpAgent(_ => []);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var probe = new NetworkPrinterProbe(NullLog.Instance, agent.Port);

        Assert.ThrowsAny<OperationCanceledException>(() =>
            probe.Probe("127.0.0.1", Options(UnusedTcpPort(), timeoutMs: 10_000), cancellation.Token));
    }

    [Fact]
    public void Propagates_cancellation_requested_before_the_call()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() =>
            new NetworkPrinterProbe(NullLog.Instance).Probe("127.0.0.1", Options(9100), cancellation.Token));
    }

    [Theory]
    [InlineData("192.168.1", "not a valid IPv4 address")]
    [InlineData("255.255.255.255", "broadcast")]
    [InlineData("", "No printer address")]
    public void Does_not_contact_invalid_or_multi_host_addresses(string address, string expectedError)
    {
        var log = new RecordingLog();

        NetworkIdentity identity = new NetworkPrinterProbe(log).Probe(address, Options(9100), CancellationToken.None);

        Assert.Equal(address, identity.Address);
        Assert.Null(identity.ResolvedAddress);
        Assert.False(identity.RawPortReachable);
        Assert.False(identity.SnmpResponded);
        Assert.Contains(expectedError, identity.Error);
        Assert.Contains(log.Entries, e => e.Event == "network.probe.notcontacted");
        Assert.DoesNotContain(log.Entries, e => e.Event == "network.probe.started");
    }

    [Theory]
    [InlineData(0, 9100)]
    [InlineData(1000, 0)]
    [InlineData(1000, 70000)]
    public void Rejects_invalid_options_without_contacting(int timeoutMs, int rawPort)
    {
        NetworkIdentity identity = new NetworkPrinterProbe(NullLog.Instance)
            .Probe("127.0.0.1", new NetworkProbeOptions(rawPort, true, "public", timeoutMs), CancellationToken.None);

        Assert.StartsWith("Invalid", identity.Error);
    }

    private static byte[] ValueFor(string oid) => oid switch
    {
        NetworkPrinterProbe.SysDescrOid => Ber.OctetString("sysDescr value"),
        NetworkPrinterProbe.SysObjectIdOid => Ber.Oid("1.3.6.1.4.1.1347.41"),
        NetworkPrinterProbe.HrDeviceDescrOid => Ber.OctetString("device description value"),
        NetworkPrinterProbe.PrinterNameOid => Ber.OctetString("printer name value"),
        NetworkPrinterProbe.SerialNumberOid => Ber.OctetString("serial value"),
        NetworkPrinterProbe.Ieee1284DeviceIdOid => Ber.OctetString("device id value"),
        NetworkPrinterProbe.KyoceraModelNameOid => Ber.OctetString("vendor model value"),
        _ => Ber.NoSuchObject,
    };

    private static NetworkProbeOptions Options(int rawPort, bool useSnmp = true, int timeoutMs = 2000) =>
        new(rawPort, useSnmp, "public", timeoutMs);

    private static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

    private static int UnusedTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Port(listener);
    }

    private static int UnusedUdpPort()
    {
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    }

    private sealed class RecordingLog : ILog
    {
        public ConcurrentQueue<(LogLevel Level, string Event, string Message)> Entries { get; } = new();

        public string SessionId => "test";

        public string? FilePath => null;

        public void Write(LogLevel level, string eventName, string message, IReadOnlyDictionary<string, object?>? data = null, Exception? exception = null) =>
            Entries.Enqueue((level, eventName, message));
    }
}
