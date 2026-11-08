using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Amwag.PrinterSetup.Core.Logging;

namespace Amwag.PrinterSetup.Windows.Network;

/// <summary>SNMP GET over UDP to one agent: a timeout per attempt, one retry, and responses matched by request-id.</summary>
internal sealed class SnmpClient
{
    internal const int DefaultPort = 161;
    internal const int Attempts = 2;

    private readonly ILog _log;

    internal SnmpClient(ILog log, int port)
    {
        _log = log;
        Port = port;
    }

    internal int Port { get; }

    /// <summary>The agent's Response PDU, or null when none arrived on either attempt.</summary>
    /// <exception cref="SocketException">For example ConnectionReset when ICMP reports that no agent listens on the port.</exception>
    internal async Task<SnmpMessage?> GetAsync(
        IPAddress address, SnmpVersion version, string community, IReadOnlyList<string> oids, TimeSpan timeout, CancellationToken cancellationToken)
    {
        int requestId = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        byte[] request = SnmpCodec.EncodeGetRequest(version, community, requestId, oids);

        using var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        // A connected UDP socket accepts datagrams only from this agent and surfaces ICMP port unreachable as ConnectionReset.
        socket.Connect(new IPEndPoint(address, Port));
        var buffer = new byte[ushort.MaxValue];

        // The retry repeats the same request-id, so a late answer to the first attempt is accepted too.
        for (int attempt = 1; attempt <= Attempts; attempt++)
        {
            await socket.SendAsync(request, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            SnmpMessage? response = await ReceiveAsync(socket, buffer, version, requestId, timeout, cancellationToken).ConfigureAwait(false);
            if (response is not null)
                return response;
        }

        return null;
    }

    private async Task<SnmpMessage?> ReceiveAsync(
        Socket socket, byte[] buffer, SnmpVersion version, int requestId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            while (true)
            {
                int received = await socket.ReceiveAsync(buffer, SocketFlags.None, timeoutSource.Token).ConfigureAwait(false);
                if (TryAccept(buffer.AsSpan(0, received), version, requestId, out SnmpMessage? response))
                    return response;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private bool TryAccept(ReadOnlySpan<byte> datagram, SnmpVersion version, int requestId, [NotNullWhen(true)] out SnmpMessage? response)
    {
        response = null;
        SnmpMessage message;
        try
        {
            message = SnmpCodec.Decode(datagram);
        }
        catch (FormatException ex)
        {
            _log.Debug("network.snmp.datagram.ignored", $"Ignored a malformed SNMP datagram: {ex.Message}", new Dictionary<string, object?>
            {
                ["length"] = datagram.Length,
            });
            return false;
        }

        if (message.PduType != SnmpPduType.Response || message.RequestId != requestId || message.Version != version)
        {
            _log.Debug("network.snmp.datagram.ignored", "Ignored an SNMP message that does not answer the pending request.", new Dictionary<string, object?>
            {
                ["pduType"] = message.PduType.ToString(),
                ["requestId"] = message.RequestId.ToString(CultureInfo.InvariantCulture),
                ["expectedRequestId"] = requestId.ToString(CultureInfo.InvariantCulture),
                ["version"] = message.Version.ToString(),
            });
            return false;
        }

        response = message;
        return true;
    }
}
