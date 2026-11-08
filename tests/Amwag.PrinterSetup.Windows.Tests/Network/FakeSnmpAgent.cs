using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Amwag.PrinterSetup.Windows.Network;

namespace Amwag.PrinterSetup.Windows.Tests.Network;

/// <summary>
/// A UDP responder bound to 127.0.0.1 on an ephemeral port. For each request it records the decoded message and sends the
/// datagrams the handler returns (none to simulate a lost or ignored request).
/// </summary>
internal sealed class FakeSnmpAgent : IDisposable
{
    private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<SnmpMessage, IEnumerable<byte[]>> _handler;
    private readonly Task _loop;

    public FakeSnmpAgent(Func<SnmpMessage, IEnumerable<byte[]>> handler)
    {
        _handler = handler;
        _loop = Task.Run(RunAsync);
    }

    public int Port => ((IPEndPoint)_socket.Client.LocalEndPoint!).Port;

    public ConcurrentQueue<SnmpMessage> Requests { get; } = new();

    public void Dispose()
    {
        _stop.Cancel();
        _socket.Dispose();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The loop ends with the disposed socket.
        }

        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _socket.ReceiveAsync(_stop.Token);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // Windows reports ICMP port unreachable for an earlier reply to a client socket that has since closed.
                continue;
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            SnmpMessage request = SnmpCodec.Decode(received.Buffer);
            Requests.Enqueue(request);
            foreach (byte[] datagram in _handler(request))
                await _socket.SendAsync(datagram, received.RemoteEndPoint, _stop.Token);
        }
    }
}
