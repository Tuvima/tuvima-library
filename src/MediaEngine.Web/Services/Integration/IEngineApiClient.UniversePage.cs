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

    /// <summary>
    /// GET /universe/{qid}/things/{id} - one thing of a Universe on its own page, limited to what the active profile may see.
    /// A thing the profile may not see (or that does not exist) is <see cref="UniverseThingLoadStatus.NotFound"/>.
    /// </summary>
    Task<UniverseThingLoadResult> GetUniverseThingAsync(string qid, Guid thingId, CancellationToken ct = default);
}

/// <summary>Why a read of one Universe thing produced no thing.</summary>
public enum UniverseThingLoadStatus
{
    Loaded,
    NotFound,
    Unavailable,
}

public sealed record UniverseThingLoadResult(UniverseThingDto? Thing, UniverseThingLoadStatus Status)
{
    public static UniverseThingLoadResult Loaded(UniverseThingDto thing) => new(thing, UniverseThingLoadStatus.Loaded);

    public static UniverseThingLoadResult NotFound { get; } = new(null, UniverseThingLoadStatus.NotFound);

    public static UniverseThingLoadResult Unavailable { get; } = new(null, UniverseThingLoadStatus.Unavailable);
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
