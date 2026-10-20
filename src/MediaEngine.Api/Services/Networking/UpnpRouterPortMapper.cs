using System.Net;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace MediaEngine.Api.Services.Networking;

public sealed class UpnpRouterPortMapper : IRouterPortMapper
{
    private static readonly string[] SearchTargets =
    [
        "urn:schemas-upnp-org:service:WANIPConnection:2",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
    ];

    private readonly HttpClient _http;
    private readonly IUpnpDiscoveryTransport _discovery;
    private readonly ILogger<UpnpRouterPortMapper> _logger;

    public UpnpRouterPortMapper(
        HttpClient http,
        IUpnpDiscoveryTransport discovery,
        ILogger<UpnpRouterPortMapper> logger)
    {
        _http = http;
        _discovery = discovery;
        _logger = logger;
    }

    public string Method => "UPnP";
    public int Priority => 30;

    public Task<RouterMappingResult> TryCreateAsync(RouterMappingRequest request, CancellationToken ct) =>
        MapAsync(request, delete: false, ct);

    public Task<RouterMappingResult> TryRenewAsync(RouterMappingRequest request, CancellationToken ct) =>
        TryCreateAsync(request, ct);

    public async Task RemoveOwnedAsync(RouterMappingRequest request, CancellationToken ct)
    {
        _ = await MapAsync(request, delete: true, ct);
    }

    private async Task<RouterMappingResult> MapAsync(RouterMappingRequest request, bool delete, CancellationToken ct)
    {
        var service = await DiscoverServiceAsync(ct);
        if (service is null)
        {
            return new RouterMappingResult(RouterMappingState.ProtocolUnavailable, Method, "UPnP was not available on this router.", ReasonCode: "discovery-unavailable");
        }

        try
        {
            var action = delete ? "DeletePortMapping" : "AddPortMapping";
            var body = delete ? BuildDeleteBody(service.Value.ServiceType, request) : BuildAddBody(service.Value.ServiceType, request);
            using var soapRequest = new HttpRequestMessage(HttpMethod.Post, service.Value.ControlUri);
            soapRequest.Headers.TryAddWithoutValidation("SOAPAction", $"\"{service.Value.ServiceType}#{action}\"");
            soapRequest.Content = new StringContent(body, Encoding.UTF8, "text/xml");
            using var response = await _http.SendAsync(soapRequest, ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("UPnP {Action} returned {StatusCode}", action, response.StatusCode);
                return new RouterMappingResult(RouterMappingState.RouterRefused, Method, "The router refused this automatic mapping.", ReasonCode: $"http-{(int)response.StatusCode}");
            }

            var publicAddress = delete ? null : await GetExternalAddressAsync(service.Value, ct);
            return new RouterMappingResult(
                delete ? RouterMappingState.NotAttempted : RouterMappingState.Active,
                Method,
                delete ? "The Library UPnP mapping was removed." : "Your router was configured automatically.",
                request.ExternalPort,
                delete ? null : DateTimeOffset.UtcNow.AddHours(1),
                publicAddress);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            if (ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogDebug(ex, "UPnP mapping request failed");
            return new RouterMappingResult(RouterMappingState.Failed, Method, "Library could not configure your router automatically.", ReasonCode: "transport-error");
        }
    }

    private async Task<UpnpService?> DiscoverServiceAsync(CancellationToken ct)
    {
        foreach (var target in SearchTargets)
        {
            var locations = await _discovery.DiscoverLocationsAsync(target, ct);
            foreach (var location in locations)
            {
                try
                {
                    if (!await IsSafeLanUriAsync(location, ct))
                    {
                        continue;
                    }

                    var xml = await _http.GetStringAsync(location, ct);
                    var document = XDocument.Parse(xml, LoadOptions.None);
                    var serviceElement = document.Descendants()
                        .FirstOrDefault(element => element.Name.LocalName == "service"
                            && element.Elements().Any(child => child.Name.LocalName == "serviceType"
                                && SearchTargets.Contains(child.Value.Trim(), StringComparer.OrdinalIgnoreCase)));
                    if (serviceElement is null)
                    {
                        continue;
                    }

                    var serviceType = serviceElement.Elements().First(child => child.Name.LocalName == "serviceType").Value.Trim();
                    var controlUrl = serviceElement.Elements().FirstOrDefault(child => child.Name.LocalName == "controlURL")?.Value.Trim();
                    if (string.IsNullOrWhiteSpace(controlUrl))
                    {
                        continue;
                    }

                    var controlUri = new Uri(location, controlUrl);
                    if (!await IsSafeLanUriAsync(controlUri, ct))
                    {
                        continue;
                    }

                    return new UpnpService(serviceType, controlUri);
                }
                catch (Exception ex) when (ex is HttpRequestException or SocketException or System.Xml.XmlException or InvalidOperationException)
                {
                    _logger.LogDebug(ex, "UPnP device description could not be read from {Host}", location.Host);
                }
            }
        }

        return null;
    }

    private async Task<string?> GetExternalAddressAsync(UpnpService service, CancellationToken ct)
    {
        var body = $"""
            <?xml version="1.0"?>
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
              <s:Body><u:GetExternalIPAddress xmlns:u="{service.ServiceType}" /></s:Body>
            </s:Envelope>
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, service.ControlUri);
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{service.ServiceType}#GetExternalIPAddress\"");
        request.Content = new StringContent(body, Encoding.UTF8, "text/xml");
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return xml.Descendants().FirstOrDefault(element => element.Name.LocalName == "NewExternalIPAddress")?.Value.Trim();
    }

    private static string BuildAddBody(string serviceType, RouterMappingRequest request) => $"""
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
          <s:Body>
            <u:AddPortMapping xmlns:u="{serviceType}">
              <NewRemoteHost></NewRemoteHost><NewExternalPort>{request.ExternalPort}</NewExternalPort><NewProtocol>TCP</NewProtocol>
              <NewInternalPort>{request.InternalPort}</NewInternalPort><NewInternalClient>{SecurityElement.Escape(request.InternalAddress)}</NewInternalClient>
              <NewEnabled>1</NewEnabled><NewPortMappingDescription>{SecurityElement.Escape(request.Description)}</NewPortMappingDescription><NewLeaseDuration>3600</NewLeaseDuration>
            </u:AddPortMapping>
          </s:Body>
        </s:Envelope>
        """;

    private static string BuildDeleteBody(string serviceType, RouterMappingRequest request) => $"""
        <?xml version="1.0"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/" s:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
          <s:Body><u:DeletePortMapping xmlns:u="{serviceType}"><NewRemoteHost></NewRemoteHost><NewExternalPort>{request.ExternalPort}</NewExternalPort><NewProtocol>TCP</NewProtocol></u:DeletePortMapping></s:Body>
        </s:Envelope>
        """;

    private static async Task<bool> IsSafeLanUriAsync(Uri uri, CancellationToken ct)
    {
        if (uri.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        var addresses = await Dns.GetHostAddressesAsync(uri.Host, ct);
        return addresses.Length > 0 && addresses.All(IsPrivateOrLocal);
    }

    private static bool IsPrivateOrLocal(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal)
        {
            return true;
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || bytes[0] == 127
            || bytes[0] == 192 && bytes[1] == 168
            || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 169 && bytes[1] == 254;
    }

    private readonly record struct UpnpService(string ServiceType, Uri ControlUri);
}
