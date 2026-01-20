using System.Net;
using System.Threading.Channels;
using Docker.DotNet;
using Docker.DotNet.Models;
using LANCommander.UDPRelay.Core;

static class Env
{
    public static string Get(string key, string @default) =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key))
            ? @default
            : Environment.GetEnvironmentVariable(key)!;

    public static bool GetBool(string key, bool @default)
        => bool.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : @default;

    public static int GetInt(string key, int @default)
        => int.TryParse(Environment.GetEnvironmentVariable(key), out var v) ? v : @default;
}

public static class Program
{
    public static async Task<int> Main()
    {
        var options = new RelayOptions
        {
            DockerEndpoint = new Uri(Env.Get("DOCKER_ENDPOINT", "unix:///var/run/docker.sock")),
            RelayServiceNameOverride = Env.Get("RELAY_SERVICE_NAME", ""),
            ComposeProjectOverride = Env.Get("COMPOSE_PROJECT", ""),
            PreferredTargetNetwork = Env.Get("PREFERRED_TARGET_NETWORK", ""),
            FallbackToContainerIpWhenNoPublishedPort = Env.GetBool("FALLBACK_TO_CONTAINER_IP", true),
            DefaultHostForwardAddress = IPAddress.Parse(Env.Get("DEFAULT_HOST_FORWARD_IP", "127.0.0.1")),
        };

        using var docker = new DockerClientConfiguration(options.DockerEndpoint).CreateClient();
        var discovery = new DockerDiscovery(docker, options);

        var (relayId, relayServiceName, composeProject) =
            await discovery.GetRelayIdentityAsync(CancellationToken.None);

        Console.WriteLine($"Relay container: {relayId[..12]} service={relayServiceName} project={composeProject ?? "-"}");

        await using var relay = new UdpRelayServer();

        // Event-driven reconcile trigger
        var trigger = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // 1) Start Docker events monitor
        _ = Task.Run(async () =>
        {
            try
            {
                var filters = new Dictionary<string, IDictionary<string, bool>>
                {
                    // Keep this broad; we filter by compose project at discovery time.
                    ["type"] = new Dictionary<string, bool> { ["container"] = true }
                };

                var parameters = new ContainerEventsParameters
                {
                    Filters = filters
                };

                var progress = new Progress<Message>(m =>
                {
                    // Typical events you care about: start, stop, die, destroy, create, rename,
                    // network connect/disconnect. We'll trigger on any container event.
                    // If you want to be stricter, check m.Action.
                    trigger.Writer.TryWrite($"{m.Type}:{m.Action}:{m.ID}");
                });

                await docker.System.MonitorEventsAsync(parameters, progress, cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                trigger.Writer.TryWrite($"events-error:{ex.Message}");
            }
        }, cts.Token);

        // 2) Initial reconcile
        await ReconcileOnceAsync("initial");

        // 3) Reconcile on events (debounced)
        var debounce = TimeSpan.FromMilliseconds(250);
        var last = DateTimeOffset.MinValue;

        while (!cts.IsCancellationRequested)
        {
            string reason;
            try
            {
                reason = await trigger.Reader.ReadAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var now = DateTimeOffset.UtcNow;
            var delta = now - last;
            if (delta < debounce)
                continue;

            last = now;
            await ReconcileOnceAsync(reason);
        }

        return 0;

        async Task ReconcileOnceAsync(string reason)
        {
            try
            {
                var targets = await discovery.DiscoverTargetsAsync(relayServiceName, composeProject, cts.Token);

                relay.UpdateTargets(targets);
                await relay.ReconcileListenersAsync(cts.Token);

                var summary = string.Join(", ",
                    targets.Select(t =>
                        $"{t.ListenPort}->[{string.Join(" ", t.ForwardEndpoints.Select(ep => $"{ep.Address}:{ep.Port}"))}]"));

                Console.WriteLine($"[{DateTimeOffset.Now:u}] reconcile ({reason}) {summary}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTimeOffset.Now:u}] reconcile error ({reason}): {ex.Message}");
            }
        }
    }
}
