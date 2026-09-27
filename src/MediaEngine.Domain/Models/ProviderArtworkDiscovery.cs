namespace MediaEngine.Domain.Models;

public sealed record ProviderArtworkCandidate(string Id, string Provider, string Url, string ThumbnailUrl,
    int? Width, int? Height);
public sealed record ProviderArtworkDiscovery(IReadOnlyList<ProviderArtworkCandidate> Items, string? Message = null);
