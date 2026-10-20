using MediaEngine.Contracts.Display;

namespace MediaEngine.Web.Services.Integration;

/// <summary>Why a View all grid read produced no page.</summary>
public enum DisplayGridLoadFailure
{
    /// <summary>The page loaded.</summary>
    None,

    /// <summary>The Engine has no such grid for this profile (404).</summary>
    NotFound,

    /// <summary>The Engine refused a sort or filter value in the request (400).</summary>
    BadRequest,

    /// <summary>The Engine did not answer, answered with another error, or the read was cancelled.</summary>
    Failed,
}

/// <summary>
/// Dashboard-only outcome of one View all grid read; not a wire type. The page decides from this call's own result,
/// never from a status another call may have left on the client.
/// </summary>
public sealed record DisplayGridLoadResult(DisplayGridPageDto? Page, DisplayGridLoadFailure Failure)
{
    public static DisplayGridLoadResult Loaded(DisplayGridPageDto page) => new(page, DisplayGridLoadFailure.None);

    public static DisplayGridLoadResult Failed(DisplayGridLoadFailure failure) => new(null, failure);
}
