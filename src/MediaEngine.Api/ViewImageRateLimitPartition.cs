namespace MediaEngine.Api;

/// <summary>Keep authorized image-grid traffic from consuming session/control request permits.</summary>
public static class ViewImageRateLimitPartition
{
    public static string Key(HttpContext context)
    {
        var endpointName = context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
        var purpose = endpointName is "GetViewItemThumbnail" or "GetViewItemPreview" ? "view-images" : "general";
        // Counted per person (their session) when the Dashboard forwards the request, per address otherwise.
        var who = MediaEngine.Api.Security.ReaderFileRateLimitPartition.SessionKey(context)
            ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return $"{who}:{purpose}";
    }
}
