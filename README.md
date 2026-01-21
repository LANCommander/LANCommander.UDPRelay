# LANCommander UDP Relay

A Docker-aware UDP relay service that automatically discovers and forwards UDP traffic to containers in your Docker Compose stack. The relay dynamically binds to UDP ports based on container configurations and forwards incoming packets to the appropriate target endpoints.

## Features

- 🔍 **Automatic Container Discovery**: Discovers containers via Docker API using Compose labels
- 🔄 **Dynamic Port Binding**: Automatically binds to UDP ports exposed by dependent containers
- 📡 **Multi-Target Forwarding**: Forwards UDP packets to multiple endpoints (load balancing/fan-out)
- 🎯 **Event-Driven Updates**: Monitors Docker events and automatically updates forwarding rules
- 🐳 **Docker Compose Integration**: Seamlessly integrates with Docker Compose projects
- 🔒 **Secure by Default**: Runs as non-root user in container

## How It Works

1. **Discovery**: The relay identifies itself using container ID (from `HOSTNAME` or `RELAY_CONTAINER_ID`) and discovers its service name and Compose project from Docker labels.

2. **Target Detection**: Scans running containers for those that:
   - Belong to the same Compose project (if applicable)
   - Have a `depends_on` relationship with the relay service
   - Expose UDP ports internally

3. **Port Mapping**: For each discovered UDP port:
   - Binds a UDP listener on the host using the internal port number
   - Resolves forwarding targets from published port bindings or container IPs
   - Creates forwarding rules for each port

4. **Packet Forwarding**: When a UDP packet arrives on a listening port:
   - Reads the packet payload
   - Forwards it to all configured target endpoints for that port
   - Supports broadcast and multi-cast scenarios

5. **Dynamic Updates**: Monitors Docker events (container start/stop/network changes) and automatically reconciles forwarding rules.

## Requirements

- .NET 10.0 SDK (for building)
- Docker Engine with API access
- Docker Compose (for service discovery via labels)

## Building

### Build the Docker Image

```bash
docker build -t lancommander/udprelay:latest .
```

### Build Locally (Development)

```bash
# Restore dependencies
dotnet restore LANCommander.UDPRelay.slnx

# Build
dotnet build LANCommander.UDPRelay.slnx -c Release

# Run
dotnet run --project LANCommander.UDPRelay/LANCommander.UDPRelay.csproj
```

## Configuration

Configuration is done via environment variables:

| Variable | Default | Description |
|----------|---------|-------------|
| `DOCKER_ENDPOINT` | `unix:///var/run/docker.sock` | Docker API endpoint |
| `RELAY_SERVICE_NAME` | Auto-detected | Override the relay service name |
| `COMPOSE_PROJECT` | Auto-detected | Override the Compose project name |
| `PREFERRED_TARGET_NETWORK` | (empty) | Preferred Docker network for container IP fallback |
| `FALLBACK_TO_CONTAINER_IP` | `true` | Fallback to container IP when no published port |
| `DEFAULT_HOST_FORWARD_IP` | `127.0.0.1` | Default IP for forwarding when HostIP is 0.0.0.0 |
| `RELAY_CONTAINER_ID` | (from HOSTNAME) | Override container ID prefix for discovery |

## Usage

### Docker Compose Example

```yaml
version: '3.8'

services:
  udp-relay:
    image: lancommander/udprelay:latest
    container_name: udp-relay
    network_mode: host
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro
    restart: unless-stopped

  game-server:
    image: mygame:latest
    depends_on:
      - udp-relay
    expose:
      - "27015/udp"  # Internal UDP port
    ports:
      - "27015:27015/udp"  # Published port
    labels:
      com.docker.compose.depends_on: udp-relay
```

In this example:
- The relay discovers `game-server` via the `depends_on` label
- It binds to UDP port `27015` (matching the internal exposed port)
- It forwards packets to `127.0.0.1:27015` (the published host port)

### Docker Run Example

```bash
docker run -d \
  --name udp-relay \
  --network host \
  -v /var/run/docker.sock:/var/run/docker.sock:ro \
  -e COMPOSE_PROJECT=myproject \
  lancommander/udprelay:latest
```

### Multiple Targets (Fan-Out)

If multiple containers expose the same internal UDP port, the relay will forward packets to all of them:

```yaml
services:
  udp-relay:
    # ... relay configuration ...

  game-server-1:
    depends_on:
      - udp-relay
    expose:
      - "27015/udp"
    ports:
      - "27016:27015/udp"
    labels:
      com.docker.compose.depends_on: udp-relay

  game-server-2:
    depends_on:
      - udp-relay
    expose:
      - "27015/udp"
    ports:
      - "27017:27015/udp"
    labels:
      com.docker.compose.depends_on: udp-relay
```

The relay will:
- Listen on port `27015`
- Forward each packet to both `127.0.0.1:27016` and `127.0.0.1:27017`

## Architecture

The project is split into two components:

### LANCommander.UDPRelay.Core (Class Library)
Contains the core relay logic:
- `RelayOptions` - Configuration model
- `PublishedUdpTarget` - Data model for port mappings
- `DockerDiscovery` - Container discovery and port resolution
- `UdpRelayServer` - Main relay server managing listeners
- `UdpListener` - Individual UDP port listener and forwarder

### LANCommander.UDPRelay (Console Application)
Contains the application entry point:
- `Program.Main()` - Orchestrates discovery, event monitoring, and reconciliation
- `Env` - Environment variable parsing utilities

## How Discovery Works

1. **Relay Identity**: The relay identifies itself by:
   - Reading `HOSTNAME` environment variable (container ID prefix)
   - Or using `RELAY_CONTAINER_ID` if provided
   - Inspecting Docker labels to get service name and project

2. **Target Discovery**: For each running container:
   - Checks if it belongs to the same Compose project (if project is known)
   - Parses `com.docker.compose.depends_on` label to find relay dependency
   - Extracts exposed UDP ports from container configuration

3. **Port Resolution**: For each exposed UDP port:
   - **Primary**: Uses published port bindings (e.g., `27015:27015/udp`)
   - **Fallback**: Uses container IP + internal port (if `FALLBACK_TO_CONTAINER_IP=true`)

4. **Forwarding Rules**: Creates a mapping:
   - `ListenPort` (internal port) → `ForwardEndpoints[]` (published ports or container IPs)

## Troubleshooting

### Relay can't find containers

- Ensure containers have `com.docker.compose.depends_on` label set to the relay service name
- Verify containers are in the same Compose project (or set `COMPOSE_PROJECT` explicitly)
- Check that containers are running (not stopped)

### Ports not binding

- Ensure the relay container has `network_mode: host` or appropriate port mappings
- Check that target ports aren't already in use
- Verify UDP ports are properly exposed in target containers

### Packets not forwarding

- Check that target containers have published ports or are reachable via container IP
- Verify `DEFAULT_HOST_FORWARD_IP` is correct for your network setup
- Ensure target containers are actually listening on the expected ports

### Docker socket access issues

- Ensure Docker socket is mounted: `-v /var/run/docker.sock:/var/run/docker.sock:ro`
- On Windows, you may need to use named pipes: `npipe:////./pipe/docker_engine`
- **Permission denied errors**: If you see `System.Net.Sockets.SocketException (13): Permission denied`:
  - The container automatically detects the Docker socket's group GID at runtime and adjusts permissions accordingly
  - **No rebuild or root access required** - the entrypoint script handles this automatically
  - If automatic detection fails, you can still:
    - **Option 1**: Rebuild the image with the correct GID: `docker build --build-arg DOCKER_GID=<host_gid> -t lancommander/udprelay:latest .`
      - Find your host's docker GID: `getent group docker | cut -d: -f3`
    - **Option 2**: Run as root in docker-compose.yml by adding `user: "0:0"` (less secure but works everywhere)

## Development

### Project Structure

```
LANCommander.UDPRelay/
├── LANCommander.UDPRelay.Core/    # Class library (core logic)
│   ├── DockerDiscovery.cs
│   ├── PublishedUdpTarget.cs
│   ├── RelayOptions.cs
│   ├── UdpListener.cs
│   └── UdpRelayServer.cs
├── LANCommander.UDPRelay/          # Console application
│   └── Program.cs
├── Dockerfile
├── docker-compose.yml
└── LANCommander.UDPRelay.slnx
```

### Building from Source

```bash
# Clone the repository
git clone <repository-url>
cd LANCommander.UDPRelay

# Restore and build
dotnet restore LANCommander.UDPRelay.slnx
dotnet build LANCommander.UDPRelay.slnx -c Release

# Run tests (if available)
dotnet test
```

## License

See [LICENSE](LICENSE) file for details.

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.
