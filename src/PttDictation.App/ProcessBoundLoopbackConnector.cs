using System.Net;
using System.Net.Sockets;

namespace PttDictation.App;

internal static class ProcessBoundLoopbackConnector
{
    public static async ValueTask<Stream> ConnectAsync(
        DnsEndPoint endpoint,
        int expectedProcessId,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(endpoint.Host, IPAddress.Loopback.ToString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Parakeet connections must use the IPv4 loopback address.");
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        try
        {
            await socket.ConnectAsync(
                new IPEndPoint(IPAddress.Loopback, endpoint.Port),
                cancellationToken);
            if (!TcpProcessInspector.IsConnectionOwnedBy(socket, expectedProcessId))
            {
                throw new InvalidOperationException(
                    "A different local process accepted the Parakeet audio connection.");
            }

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
