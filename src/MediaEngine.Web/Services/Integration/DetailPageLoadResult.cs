using MediaEngine.Contracts.Details;

namespace MediaEngine.Web.Services.Integration;

/// <summary>Why a detail-page load produced no page.</summary>
public enum DetailPageLoadFailure
{
    /// <summary>The page loaded.</summary>
    None,

    /// <summary>The Engine has no such item, or the active profile cannot see it.</summary>
    NotFound,

    /// <summary>The Engine refused the request for this account.</summary>
    NotAllowed,

    /// <summary>The Engine did not answer, or answered with a server error.</summary>
    Unreachable,
}

/// <summary>Dashboard-only outcome of a detail-page load; not a wire type.</summary>
public sealed record DetailPageLoadResult(DetailPageViewModel? Detail, DetailPageLoadFailure Failure)
{
    public static DetailPageLoadResult Loaded(DetailPageViewModel detail) => new(detail, DetailPageLoadFailure.None);

    public static DetailPageLoadResult Failed(DetailPageLoadFailure failure) => new(null, failure);
}
