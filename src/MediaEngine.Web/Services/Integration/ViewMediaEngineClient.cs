namespace MediaEngine.Web.Services.Integration;

using MediaEngine.Domain.PersonalMedia;

public interface IViewMediaEngineClient
{
    Task<HttpResponseMessage> SendAsync(
        ViewMediaGrant grant,
        HttpMethod method,
        string? range,
        string? ifRange,
        CancellationToken cancellationToken);
}

/// <summary>
/// Keeps the temporary legacy Engine media route entirely behind the Dashboard proxy.
/// </summary>
public sealed class ViewMediaEngineClient(HttpClient http) : IViewMediaEngineClient, IDisposable
{
    public Task<HttpResponseMessage> SendAsync(
        ViewMediaGrant grant,
        HttpMethod method,
        string? range,
        string? ifRange,
        CancellationToken cancellationToken)
    {
        var resource = grant.ResourceKind switch
        {
            ViewMediaResourceKind.Thumbnail => "thumbnail",
            ViewMediaResourceKind.Content => "content",
            ViewMediaResourceKind.Preview => "preview",
            _ => throw new ArgumentOutOfRangeException(nameof(grant), "Unsupported View media resource kind."),
        };
        var scope = grant.ScopeKind.ToString().ToLowerInvariant();
        var path = $"/view/items/{grant.AssetId:D}/{resource}?scope={scope}";
        if (grant.ResourceKind == ViewMediaResourceKind.Content && grant.ResourceRole != ViewMediaResourceRole.Primary)
        {
            path += $"&role={RoleValue(grant.ResourceRole)}";
        }
        if (grant.ScopeKind.CarriesProfileId() && grant.ScopeProfileId.HasValue)
        {
            path += $"&scopeProfileId={grant.ScopeProfileId.Value:D}";
        }
        var request = new HttpRequestMessage(method, path);
        AddHeader(request, "Range", range);
        AddHeader(request, "If-Range", ifRange);
        return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    public void Dispose() => http.Dispose();

    private static string RoleValue(ViewMediaResourceRole role) => role switch
    {
        ViewMediaResourceRole.Original => "original",
        ViewMediaResourceRole.LivePhotoVideo => "live_photo_video",
        ViewMediaResourceRole.Raw => "raw",
        ViewMediaResourceRole.Jpeg => "jpeg",
        ViewMediaResourceRole.Sidecar => "sidecar",
        ViewMediaResourceRole.AudioCompanion => "audio_companion",
        _ => "primary",
    };

    private static void AddHeader(HttpRequestMessage request, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}
