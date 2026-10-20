using MediaEngine.Contracts.Universe;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>
    /// GET /universe/{qid}/overview - everything the Universe page shows, limited to what the active profile may see.
    /// A missing page is reported as <see cref="UniversePageLoadStatus.NotFound"/> (not available to this profile) or
    /// <see cref="UniversePageLoadStatus.Unavailable"/> (the Engine could not be reached; the failure is logged).
    /// </summary>
    Task<UniversePageLoadResult> GetUniversePageAsync(string qid, CancellationToken ct = default);
}

/// <summary>Why a Universe page read produced no page.</summary>
public enum UniversePageLoadStatus
{
    Loaded,
    NotFound,
    Unavailable,
}

public sealed record UniversePageLoadResult(UniversePageDto? Page, UniversePageLoadStatus Status)
{
    public static UniversePageLoadResult Loaded(UniversePageDto page) => new(page, UniversePageLoadStatus.Loaded);

    public static UniversePageLoadResult NotFound { get; } = new(null, UniversePageLoadStatus.NotFound);

    public static UniversePageLoadResult Unavailable { get; } = new(null, UniversePageLoadStatus.Unavailable);
}
