namespace MediaEngine.Domain.Models;

public sealed record ProviderArtworkCandidate(string Id, string Provider, string Url, string ThumbnailUrl,
    int? Width, int? Height, string? Language = null);
public sealed record ProviderArtworkDiscovery(IReadOnlyList<ProviderArtworkCandidate> Items, string? Message = null);
