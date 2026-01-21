using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace LANCommander.UDPRelay.Core;

public sealed class DockerDiscovery
{
    private readonly DockerClient _docker;
    private readonly RelayOptions _options;

    public DockerDiscovery(DockerClient docker, RelayOptions options)
    {
        _docker = docker;
        _options = options;
    }

    public async Task<(string relayContainerId, string relayServiceName, string? composeProject)> GetRelayIdentityAsync(CancellationToken ct)
    {
        var hostname = Environment.GetEnvironmentVariable("HOSTNAME") ?? "";
        var relayIdPrefix = Environment.GetEnvironmentVariable("RELAY_CONTAINER_ID");
        if (!string.IsNullOrWhiteSpace(relayIdPrefix))
            hostname = relayIdPrefix.Trim();

        if (string.IsNullOrWhiteSpace(hostname))
            throw new InvalidOperationException("HOSTNAME or RELAY_CONTAINER_ID must be set to identify the relay container.");

        var containers = await _docker.Containers.ListContainersAsync(new ContainersListParameters { All = true }, ct);
        
        // Try matching by container ID prefix first
        var relay = containers.FirstOrDefault(c => c.ID.StartsWith(hostname, StringComparison.OrdinalIgnoreCase));
        
        // If not found by ID, try matching by container name
        if (relay is null)
        {
            relay = containers.FirstOrDefault(c => 
                c.Names?.Any(name => 
                    name.TrimStart('/').Equals(hostname, StringComparison.OrdinalIgnoreCase) ||
                    name.TrimStart('/').StartsWith(hostname, StringComparison.OrdinalIgnoreCase)
                ) == true
            );
        }

        if (relay is null)
            throw new InvalidOperationException($"Could not locate relay container by id prefix or name '{hostname}'. Available containers: {string.Join(", ", containers.Select(c => $"{c.ID[..12]} ({string.Join(", ", c.Names ?? Array.Empty<string>())})"))}");

        var inspect = await _docker.Containers.InspectContainerAsync(relay.ID, ct);

        string? composeProject = null;
        if (inspect.Config?.Labels is not null && inspect.Config.Labels.TryGetValue("com.docker.compose.project", out var proj))
            composeProject = proj;

        var relayServiceName = _options.RelayServiceNameOverride.Trim();
        if (string.IsNullOrWhiteSpace(relayServiceName) &&
            inspect.Config?.Labels is not null &&
            inspect.Config.Labels.TryGetValue("com.docker.compose.service", out var svc))
        {
            relayServiceName = svc;
        }

        if (string.IsNullOrWhiteSpace(relayServiceName))
            relayServiceName = "udp-relay";

        if (!string.IsNullOrWhiteSpace(_options.ComposeProjectOverride))
            composeProject = _options.ComposeProjectOverride.Trim();

        return (relay.ID, relayServiceName, composeProject);
    }

    public async Task<IReadOnlyList<PublishedUdpTarget>> DiscoverTargetsAsync(
        string relayServiceName,
        string? composeProject,
        CancellationToken ct)
    {
        var running = await _docker.Containers.ListContainersAsync(new ContainersListParameters { All = false }, ct);

        // listenPort => endpoints
        var map = new Dictionary<int, HashSet<IPEndPoint>>();

        foreach (var c in running)
        {
            var inspect = await _docker.Containers.InspectContainerAsync(c.ID, ct);

            // Scope to compose project if known.
            if (!string.IsNullOrWhiteSpace(composeProject))
            {
                var labels = inspect.Config?.Labels ?? new Dictionary<string, string>();
                if (!labels.TryGetValue("com.docker.compose.project", out var p) ||
                    !string.Equals(p, composeProject, StringComparison.Ordinal))
                {
                    continue;
                }
            }

            // Determine dependency on relay via Compose label.
            var dependsOnLabel = "";
            if (inspect.Config?.Labels is not null)
                inspect.Config.Labels.TryGetValue("com.docker.compose.depends_on", out dependsOnLabel);

            if (string.IsNullOrWhiteSpace(dependsOnLabel) || !DependsOnContainsService(dependsOnLabel, relayServiceName))
                continue;

            // Exposed internal UDP ports (these drive which listen ports we bind)
            var exposedUdpPorts = GetExposedUdpPorts(inspect);
            if (exposedUdpPorts.Count == 0)
                continue;

            // For each internal UDP port P:
            // - Listen on P
            // - Forward to published host port binding(s) for "P/udp"
            //   (e.g., P/udp -> host 27016/udp for container #2)
            foreach (var internalPort in exposedUdpPorts)
            {
                var key = $"{internalPort}/udp";

                var endpoints = ResolveForwardEndpoints(inspect, internalPort, key);
                if (endpoints.Length == 0)
                    continue;

                if (!map.TryGetValue(internalPort, out var set))
                {
                    set = new HashSet<IPEndPoint>(new IPEndPointComparer());
                    map[internalPort] = set;
                }

                foreach (var ep in endpoints)
                    set.Add(ep);
            }
        }

        return map
            .OrderBy(k => k.Key)
            .Select(k => new PublishedUdpTarget(k.Key, k.Value.ToArray()))
            .ToList();
    }

    private IPEndPoint[] ResolveForwardEndpoints(ContainerInspectResponse inspect, int internalPort, string portKey)
    {
        // Prefer published host port bindings.
        var bindings = inspect.NetworkSettings?.Ports;
        if (bindings is not null && bindings.TryGetValue(portKey, out var list) && list is not null && list.Count > 0)
        {
            var eps = new List<IPEndPoint>();
            foreach (var b in list)
            {
                if (!int.TryParse(b.HostPort, out var hostPort) || hostPort is <= 0 or > 65535)
                    continue;

                var hostIp = ParseHostIp(b.HostIP);
                eps.Add(new IPEndPoint(hostIp, hostPort));
            }

            if (eps.Count > 0)
                return eps.ToArray();
        }

        // Optional fallback: forward to container IP on internal port.
        if (_options.FallbackToContainerIpWhenNoPublishedPort)
        {
            var ip = TryGetContainerIp(inspect, _options.PreferredTargetNetwork);
            if (ip is not null)
                return new[] { new IPEndPoint(ip, internalPort) };
        }

        return Array.Empty<IPEndPoint>();
    }

    private IPAddress ParseHostIp(string? hostIp)
    {
        if (string.IsNullOrWhiteSpace(hostIp) || hostIp == "0.0.0.0" || hostIp == "::")
            return _options.DefaultHostForwardAddress;

        if (IPAddress.TryParse(hostIp, out var ip))
            return ip;

        return _options.DefaultHostForwardAddress;
    }

    private static bool DependsOnContainsService(string dependsOnLabelValue, string relayServiceName)
    {
        var tokens = dependsOnLabelValue
            .Split(new[] { ',', ' ', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.Trim().Trim('"', '\'', '[', ']', '{', '}', '(', ')'))
            .Where(t => !string.IsNullOrWhiteSpace(t));

        return tokens.Any(t => string.Equals(t, relayServiceName, StringComparison.OrdinalIgnoreCase));
    }

    private static HashSet<int> GetExposedUdpPorts(ContainerInspectResponse inspect)
    {
        var set = new HashSet<int>();
        var exposed = inspect.Config?.ExposedPorts;
        if (exposed is null)
            return set;

        foreach (var key in exposed.Keys)
        {
            if (!key.EndsWith("/udp", StringComparison.OrdinalIgnoreCase))
                continue;

            var parts = key.Split('/', 2);
            if (parts.Length != 2)
                continue;

            if (int.TryParse(parts[0], out var port) && port is > 0 and <= 65535)
                set.Add(port);
        }

        return set;
    }

    private static IPAddress? TryGetContainerIp(ContainerInspectResponse inspect, string preferredNetwork)
    {
        var networks = inspect.NetworkSettings?.Networks;
        if (networks is null || networks.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(preferredNetwork) &&
            networks.TryGetValue(preferredNetwork, out var preferred) &&
            IPAddress.TryParse(preferred.IPAddress, out var pip))
        {
            return pip;
        }

        foreach (var kvp in networks)
        {
            if (IPAddress.TryParse(kvp.Value.IPAddress, out var ip))
                return ip;
        }

        return null;
    }

    private sealed class IPEndPointComparer : IEqualityComparer<IPEndPoint>
    {
        public bool Equals(IPEndPoint? x, IPEndPoint? y)
            => x is not null && y is not null && x.Port == y.Port && Equals(x.Address, y.Address);

        public int GetHashCode(IPEndPoint obj)
            => HashCode.Combine(obj.Address, obj.Port);
    }
}
