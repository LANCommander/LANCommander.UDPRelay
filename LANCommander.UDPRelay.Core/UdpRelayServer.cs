using System.Collections.Concurrent;
using System.Net;

namespace LANCommander.UDPRelay.Core;

public sealed class UdpRelayServer : IAsyncDisposable
{
    private readonly ConcurrentDictionary<int, UdpListener> _listeners = new();

    // Port -> endpoints. Updated atomically.
    private volatile IReadOnlyDictionary<int, IPEndPoint[]> _targetsByListenPort =
        new Dictionary<int, IPEndPoint[]>();

    public void UpdateTargets(IReadOnlyList<PublishedUdpTarget> targets)
    {
        _targetsByListenPort = targets.ToDictionary(t => t.ListenPort, t => t.ForwardEndpoints);
    }

    public async Task ReconcileListenersAsync(CancellationToken ct)
    {
        var desiredPorts = _targetsByListenPort.Keys.ToHashSet();

        foreach (var port in desiredPorts)
        {
            _listeners.GetOrAdd(port, p =>
            {
                var l = new UdpListener(p, ResolveTargets);
                l.Start();
                return l;
            });
        }

        foreach (var kvp in _listeners.ToArray())
        {
            if (!desiredPorts.Contains(kvp.Key))
            {
                if (_listeners.TryRemove(kvp.Key, out var listener))
                    await listener.DisposeAsync();
            }
        }

        IPEndPoint[] ResolveTargets(int listenPort)
            => _targetsByListenPort.TryGetValue(listenPort, out var eps) ? eps : Array.Empty<IPEndPoint>();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var kvp in _listeners)
            await kvp.Value.DisposeAsync();

        _listeners.Clear();
    }
}
