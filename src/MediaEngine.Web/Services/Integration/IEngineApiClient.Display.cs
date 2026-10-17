using System.Text.Json;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Display;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Settings;
using MediaEngine.Domain.Models;
using MediaEngine.Web.Models.ViewDTOs;

namespace MediaEngine.Web.Services.Integration;

public partial interface IEngineApiClient
{
    /// <summary>GET /api/v1/display/home — cross-platform consumer display model for Home.</summary>
    Task<DisplayPageDto?> GetDisplayHomeAsync(Guid? profileId = null, CancellationToken ct = default);

    /// <summary>GET /api/v1/display/browse — cross-platform consumer display model for Watch, Read, Listen, and browse surfaces.</summary>
    Task<DisplayPageDto?> GetDisplayBrowseAsync(
        string? lane = null,
        string? mediaType = null,
        string? grouping = null,
        string? search = null,
        int? offset = null,
        int? limit = null,
        bool? includeCatalog = null,
        Guid? profileId = null,
        CancellationToken ct = default,
        string? genres = null,
        string? creator = null,
        string? status = null,
        string? year = null,
        string? sort = null);

    /// <summary>
    /// GET /api/v1/display/continue - compact in-progress cards for one lane and optional media type.
    /// With <paramref name="finished"/> the Engine returns the finished cards instead (most recently finished first).
    /// </summary>
    Task<DisplayPageDto?> GetDisplayContinueAsync(
        string? lane = null,
        string? mediaType = null,
        int? limit = null,
        CancellationToken ct = default,
        bool? finished = null);

    /// <summary>
    /// POST /api/v1/display/items - the lane-style cards for a list of item ids, in calls of at most
    /// <see cref="DisplayItemsRequest.MaxIds"/> ids. An id the profile cannot see is simply absent. Returns null when a call fails.
    /// </summary>
    Task<DisplayPageDto?> GetDisplayItemsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default);

    /// <summary>GET /api/v1/display/contributor-shelves - logical multi-work shelves for Collections.</summary>
    Task<IReadOnlyList<ContributorShelfDto>> GetContributorShelvesAsync(CancellationToken ct = default);

}
