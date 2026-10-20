using MediaEngine.Domain.Contracts;

namespace MediaEngine.Api.Services.Networking;

public sealed class RouterPortMappingCoordinator : BackgroundService
{
    private readonly IConfigurationLoader _configuration;
    private readonly INetworkEnvironmentService _environment;
    private readonly INetworkTopologyService _topology;
    private readonly IReadOnlyList<IRouterPortMapper> _mappers;
    private readonly NetworkRuntimeState _runtime;
    private readonly ILogger<RouterPortMappingCoordinator> _logger;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private RouterMappingRequest? _lastRequest;
    private string? _activeMethod;

    public RouterPortMappingCoordinator(
        IConfigurationLoader configuration,
        INetworkEnvironmentService environment,
        INetworkTopologyService topology,
        IEnumerable<IRouterPortMapper> mappers,
        NetworkRuntimeState runtime,
        ILogger<RouterPortMappingCoordinator> logger)
    {
        _configuration = configuration;
        _environment = environment;
        _topology = topology;
        _mappers = mappers.OrderBy(mapper => mapper.Priority).ToList();
        _runtime = runtime;
        _logger = logger;
    }

    public async Task<RouterMappingResult> EnsureMappingAsync(CancellationToken ct)
    {
        await _operationGate.WaitAsync(ct);
        try
        {
            var settings = _configuration.LoadNetwork();
            if (!settings.Remote.AutomaticRouterConfiguration
                || settings.Remote.ConnectionMode != MediaEngine.Domain.Configuration.NetworkConnectionModes.DirectOnly)
            {
                await RemoveActiveMappingAsync(ct);
                var inactive = new RouterMappingResult(RouterMappingState.NotAttempted, "None", "Automatic router configuration was not attempted because it is turned off.", ReasonCode: "disabled");
                _runtime.RecordRouterMapping(inactive);
                return inactive;
            }

            var topology = _topology.GetSnapshot();
            if (!topology.SupportsRouterDiscovery)
            {
                await RemoveActiveMappingAsync(ct);
                var unsupported = new RouterMappingResult(
                    RouterMappingState.UnsupportedTopology,
                    "None",
                    topology.Detail,
                    ReasonCode: "docker-bridge");
                _runtime.RecordRouterMapping(unsupported);
                return unsupported;
            }

            var usableAddresses = _environment.GetUsableAddresses(includeIpv6: false);
            var internalAddress = usableAddresses
                .FirstOrDefault(address => string.Equals(address.InterfaceName, topology.InterfaceName, StringComparison.OrdinalIgnoreCase))
                ?.Address
                ?? usableAddresses.FirstOrDefault()?.Address;
            if (string.IsNullOrWhiteSpace(internalAddress))
            {
                var unavailable = new RouterMappingResult(RouterMappingState.UnsupportedTopology, "None", "Library could not find a usable local IPv4 address.", ReasonCode: "no-local-ipv4");
                _runtime.RecordRouterMapping(unavailable);
                return unavailable;
            }

            var request = new RouterMappingRequest(
                "Library Remote Access",
                internalAddress,
                settings.Remote.TlsTerminationPort ?? settings.Local.Port,
                settings.Remote.ExternalPort ?? 443,
                TimeSpan.FromHours(1));
            _lastRequest = request;

            RouterMappingResult? lastFailure = null;
            foreach (var mapper in _mappers)
            {
                var result = string.Equals(_activeMethod, mapper.Method, StringComparison.OrdinalIgnoreCase)
                    ? await mapper.TryRenewAsync(request, ct)
                    : await mapper.TryCreateAsync(request, ct);
                if (result.State == RouterMappingState.Active)
                {
                    _activeMethod = mapper.Method;
                    _runtime.RecordRouterMapping(result);
                    return result;
                }

                if (result.State is RouterMappingState.RouterRefused or RouterMappingState.Failed)
                {
                    lastFailure = result;
                }
            }

            var final = lastFailure ?? new RouterMappingResult(
                RouterMappingState.ProtocolUnavailable,
                "None",
                "Library could not configure your router automatically.");
            _runtime.RecordRouterMapping(final);
            return final;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await EnsureMappingAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Automatic router mapping reconciliation failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await RemoveActiveMappingAsync(cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
        await base.StopAsync(cancellationToken);
    }

    private async Task RemoveActiveMappingAsync(CancellationToken ct)
    {
        if (_lastRequest is null || string.IsNullOrWhiteSpace(_activeMethod))
        {
            return;
        }

        var mapper = _mappers.FirstOrDefault(candidate => string.Equals(candidate.Method, _activeMethod, StringComparison.OrdinalIgnoreCase));
        if (mapper is not null)
        {
            await mapper.RemoveOwnedAsync(_lastRequest, ct);
        }

        _activeMethod = null;
        _lastRequest = null;
    }
}
