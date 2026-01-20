using System.Net;

namespace LANCommander.UDPRelay.Core;

public sealed class RelayOptions
{
    public Uri DockerEndpoint { get; init; } = new("unix:///var/run/docker.sock");

    // Optional overrides
    public string RelayServiceNameOverride { get; init; } = "";
    public string ComposeProjectOverride { get; init; } = "";

    // If you want to scope container IP selection
    public string PreferredTargetNetwork { get; init; } = "";

    // Forwarding behavior
    public bool FallbackToContainerIpWhenNoPublishedPort { get; init; } = true;

    // When HostIP is 0.0.0.0 / empty in bindings, use this address for forwarding.
    // In host network mode, Loopback is typically correct for hitting published ports.
    public IPAddress DefaultHostForwardAddress { get; init; } = IPAddress.Loopback;
}
