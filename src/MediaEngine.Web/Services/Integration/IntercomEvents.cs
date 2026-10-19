namespace MediaEngine.Web.Services.Integration;

// Frozen until the dedicated Universe/Chronicle contract packet.
public sealed record UniverseEnrichmentProgressEvent(
    string WorkQid,
    string WorkTitle,
    int ProcessedCount,
    int TotalCount,
    string CurrentStep);
