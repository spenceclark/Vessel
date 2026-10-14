using System.Net;
using System.Net.Sockets;

namespace Vessel.Tests;

/// <summary>
/// #104 — a loopback port that refuses connections for as long as this lives. The socket is
/// bound but never listens, so a connect is refused, and the port stays taken: binding and
/// releasing a port left it free for any concurrently started listener to claim, which made a
/// "dead" backend answer 200 under a parallel run.
/// </summary>
public sealed class RefusedPort : IDisposable
{
    private readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

    public RefusedPort() => _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

    public string BaseUrl => $"http://127.0.0.1:{((IPEndPoint)_socket.LocalEndPoint!).Port}";

    public void Dispose() => _socket.Dispose();
}
