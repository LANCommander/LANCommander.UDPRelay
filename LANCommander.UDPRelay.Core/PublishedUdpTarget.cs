using System.Net;

namespace LANCommander.UDPRelay.Core;

public sealed record PublishedUdpTarget(
    int ListenPort,                // host listen port (derived from internal exposed port)
    IPEndPoint[] ForwardEndpoints   // host endpoints to forward to (published ports, or container ip fallback)
);
