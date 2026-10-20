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
    /// </summary>
    Task<DisplayPageDto?> GetDisplayContinueAsync(
        string? lane = null,
        string? mediaType = null,
        int? limit = null,
        CancellationToken ct = default);

    /// <summary>
    /// POST /api/v1/display/items - the lane-style cards for a list of item ids, in calls of at most
    /// <see cref="DisplayItemsRequest.MaxIds"/> ids. An id the profile cannot see is simply absent. Returns null when a call fails.
    /// </summary>
    Task<DisplayPageDto?> GetDisplayItemsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default);

    /// <summary>GET /api/v1/display/contributor-shelves - logical multi-work shelves for Collections.</summary>
    Task<IReadOnlyList<ContributorShelfDto>> GetContributorShelvesAsync(CancellationToken ct = default);

    /// <summary>
    /// GET /api/v1/display/grids/{lane}/{shelfKey} - one page of a shelf's full list for the View all grid. A blank
    /// <paramref name="sort"/> lets the Engine apply the profile's remembered sort. Returns null on failure; a 404 (no such
    /// grid) is told apart from other failures by <see cref="LastStatusCode"/>.
    /// </summary>
    Task<DisplayGridPageDto?> GetDisplayGridAsync(
        string lane,
        string shelfKey,
        string? sort,
        string? type,
        string? genre,
        string? format,
        string? status,
        int offset,
        int limit,
        CancellationToken ct = default);

    /// <summary>PUT /api/v1/display/grid-preferences/{rememberKey} - remembers the sort the active profile chose on a View all grid.</summary>
    Task<bool> SetDisplayGridSortAsync(string rememberKey, string sort, CancellationToken ct = default);

}
