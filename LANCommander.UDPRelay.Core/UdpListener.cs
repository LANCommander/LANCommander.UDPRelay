using System.Net;
using System.Net.Sockets;

namespace LANCommander.UDPRelay.Core;

public sealed class UdpListener : IAsyncDisposable
{
    private readonly int _listenPort;
    private readonly Func<int, IPEndPoint[]> _targetsResolver;
    private readonly Socket _socket;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public UdpListener(int listenPort, Func<int, IPEndPoint[]> targetsResolver)
    {
        _listenPort = listenPort;
        _targetsResolver = targetsResolver;

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, true);

        _socket.Bind(new IPEndPoint(IPAddress.Any, _listenPort));
    }

    public void Start() => _loop = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        var buffer = new byte[65535];
        EndPoint remote = new IPEndPoint(IPAddress.Any, 0);

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var res = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, remote, _cts.Token);

                var targets = _targetsResolver(_listenPort);
                if (targets.Length == 0)
                    continue;

                var payload = buffer.AsMemory(0, res.ReceivedBytes);

                for (var i = 0; i < targets.Length; i++)
                    await _socket.SendToAsync(payload, SocketFlags.None, targets[i], _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Keep running. Add logging if desired.
                await Task.Delay(200, _cts.Token).ContinueWith(_ => { });
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();

        try { if (_loop is not null) await _loop; } catch { /* ignore */ }

        try { _socket.Close(); } catch { /* ignore */ }
        try { _socket.Dispose(); } catch { /* ignore */ }

        _cts.Dispose();
    }
}
