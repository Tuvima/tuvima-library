using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace MediaEngine.Api.Services.Networking;

public sealed class PcpRouterPortMapper : IRouterPortMapper
{
    private const int PcpPort = 5351;
    private readonly IGatewayDiscoveryService _gateways;
    private readonly IUdpGatewayTransport _transport;
    private readonly ILogger<PcpRouterPortMapper> _logger;
    private readonly byte[] _nonce;

    public PcpRouterPortMapper(
        IGatewayDiscoveryService gateways,
        IUdpGatewayTransport transport,
        IRouterNonceSource nonceSource,
        ILogger<PcpRouterPortMapper> logger)
    {
        _gateways = gateways;
        _transport = transport;
        _logger = logger;
        _nonce = nonceSource.Create(12);
    }

    public string Method => "PCP";
    public int Priority => 10;

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
                var payload = BuildMapRequest(gateway.InternalAddress, request, lifetimeSeconds);
                var response = await _transport.ExchangeAsync(
                    gateway.GatewayAddress,
                    PcpPort,
                    payload,
                    TimeSpan.FromSeconds(2),
                    ct);
                if (response.Length < 60 || response[0] != 2 || response[1] != 0x81)
                {
                    continue;
                }

                var resultCode = response[3];
                if (resultCode != 0)
                {
                    return new RouterMappingResult(RouterMappingState.RouterRefused, Method, TranslateResult(resultCode), ReasonCode: $"pcp-result-{resultCode}");
                }

                if (!response.AsSpan(24, 12).SequenceEqual(_nonce))
                {
                    continue;
                }

                var lease = BinaryPrimitives.ReadUInt32BigEndian(response.AsSpan(4, 4));
                var externalPort = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(42, 2));
                var externalAddressBytes = response.AsSpan(44, 16).ToArray();
                var externalAddress = new IPAddress(externalAddressBytes);
                var publicAddress = externalAddress.IsIPv4MappedToIPv6
                    ? externalAddress.MapToIPv4().ToString()
                    : externalAddress.ToString();
                return new RouterMappingResult(
                    lifetimeSeconds == 0 ? RouterMappingState.NotAttempted : RouterMappingState.Active,
                    Method,
                    lifetimeSeconds == 0 ? "The Library PCP mapping was removed." : "Your router was configured automatically.",
                    externalPort,
                    lifetimeSeconds == 0 ? null : DateTimeOffset.UtcNow.AddSeconds(lease),
                    publicAddress);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                if (ex is OperationCanceledException && ct.IsCancellationRequested)
                {
                    throw;
                }

                _logger.LogDebug(ex, "PCP was unavailable through gateway {Gateway}", gateway.GatewayAddress);
            }
        }

        return new RouterMappingResult(RouterMappingState.ProtocolUnavailable, Method, "PCP was not available on this router.", ReasonCode: "no-response");
    }

    private byte[] BuildMapRequest(IPAddress internalAddress, RouterMappingRequest request, uint lifetimeSeconds)
    {
        var payload = new byte[60];
        payload[0] = 2;
        payload[1] = 1; // MAP opcode
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(4, 4), lifetimeSeconds);
        WriteIpv4MappedAddress(payload.AsSpan(8, 16), internalAddress);
        _nonce.CopyTo(payload, 24);
        payload[36] = 6; // TCP
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(40, 2), checked((ushort)request.InternalPort));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(42, 2), checked((ushort)request.ExternalPort));
        return payload;
    }

    private static void WriteIpv4MappedAddress(Span<byte> destination, IPAddress address)
    {
        destination.Clear();
        destination[10] = 0xff;
        destination[11] = 0xff;
        address.GetAddressBytes().CopyTo(destination[12..]);
    }

    private static string TranslateResult(byte code) => code switch
    {
        1 => "The router rejected the request as unsupported.",
        2 => "The router could not process the request.",
        7 => "The router refused this mapping.",
        8 => "The router could not provide the requested address or port.",
        12 => "The router ran out of mapping resources.",
        _ => "The router could not create the requested mapping.",
    };
}
