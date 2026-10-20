using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace MediaEngine.Api.Services.Networking;

public sealed class NatPmpRouterPortMapper : IRouterPortMapper
{
    private const int NatPmpPort = 5351;
    private readonly IGatewayDiscoveryService _gateways;
    private readonly IUdpGatewayTransport _transport;
    private readonly ILogger<NatPmpRouterPortMapper> _logger;

    public NatPmpRouterPortMapper(
        IGatewayDiscoveryService gateways,
        IUdpGatewayTransport transport,
        ILogger<NatPmpRouterPortMapper> logger)
    {
        _gateways = gateways;
        _transport = transport;
        _logger = logger;
    }

    public string Method => "NAT-PMP";
    public int Priority => 20;

    public Task<RouterMappingResult> TryCreateAsync(RouterMappingRequest request, CancellationToken ct) =>
        MapAsync(request, (uint)Math.Clamp((long)request.LeaseDuration.TotalSeconds, 60, 86_400), ct);

    public Task<RouterMappingResult> TryRenewAsync(RouterMappingRequest request, CancellationToken ct) =>
        TryCreateAsync(request, ct);

    public async Task RemoveOwnedAsync(RouterMappingRequest request, CancellationToken ct)
    {
        _ = await MapAsync(request, 0, ct);
    }

    private async Task<RouterMappingResult> MapAsync(RouterMappingRequest request, uint lifetimeSeconds, CancellationToken ct)
    {
        foreach (var gateway in _gateways.GetIpv4Gateways())
        {
            try
            {
                var publicAddress = await GetPublicAddressAsync(gateway.GatewayAddress, ct);
                var payload = new byte[12];
                payload[0] = 0;
                payload[1] = 2; // TCP mapping
                BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(4, 2), checked((ushort)request.InternalPort));
                BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(6, 2), checked((ushort)request.ExternalPort));
                BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(8, 4), lifetimeSeconds);
                var response = await _transport.ExchangeAsync(gateway.GatewayAddress, NatPmpPort, payload, TimeSpan.FromSeconds(2), ct);
                if (response.Length < 16 || response[0] != 0 || response[1] != 130)
                {
                    continue;
                }

                var resultCode = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2, 2));
                if (resultCode != 0)
                {
                    return new RouterMappingResult(RouterMappingState.RouterRefused, Method, TranslateResult(resultCode), ReasonCode: $"nat-pmp-result-{resultCode}");
                }

                var externalPort = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(10, 2));
                var lease = BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(12, 4));
                return new RouterMappingResult(
                    lifetimeSeconds == 0 ? RouterMappingState.NotAttempted : RouterMappingState.Active,
                    Method,
                    lifetimeSeconds == 0 ? "The Library NAT-PMP mapping was removed." : "Your router was configured automatically.",
                    externalPort,
                    lifetimeSeconds == 0 ? null : DateTimeOffset.UtcNow.AddSeconds(lease),
                    publicAddress);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or InvalidOperationException)
            {
                if (ex is OperationCanceledException && ct.IsCancellationRequested)
                {
                    throw;
                }

                _logger.LogDebug(ex, "NAT-PMP was unavailable through gateway {Gateway}", gateway.GatewayAddress);
            }
        }

        return new RouterMappingResult(RouterMappingState.ProtocolUnavailable, Method, "NAT-PMP was not available on this router.", ReasonCode: "no-response");
    }

    private async Task<string?> GetPublicAddressAsync(IPAddress gateway, CancellationToken ct)
    {
        var response = await _transport.ExchangeAsync(gateway, NatPmpPort, new byte[] { 0, 0 }, TimeSpan.FromSeconds(2), ct);
        return response.Length >= 12 && response[0] == 0 && response[1] == 128
            ? new IPAddress(response.AsSpan(8, 4)).ToString()
            : null;
    }

    private static string TranslateResult(ushort code) => code switch
    {
        1 => "The router rejected the request as unsupported.",
        2 => "The router refused this automatic mapping.",
        3 => "The router reported a network failure.",
        4 => "The router ran out of mapping resources.",
        5 => "The router rejected this mapping operation.",
        _ => "The router could not create the requested mapping.",
    };
}
