using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Paging;
using MediaEngine.Domain.Authorization;

namespace MediaEngine.Api.Services.View;

/// <summary>
/// Records a server administrator opening another household's photos. Called by the View authorization boundary,
/// so no read of someone else's space can happen without it.
/// </summary>
public interface IViewOtherPeopleAuditor
{
    Task RecordOpenAsync(RequestAuthority actor, ResolvedViewScope scope, CancellationToken ct = default);
}

public interface IViewOtherPeopleService
{
    /// <summary>The households other than the caller's own, with their people.</summary>
    Task<ViewOtherPeopleDto> ListAsync(RequestAuthority caller, CancellationToken ct = default);

    /// <summary>
    /// Who opened your photos. A household administrator sees the whole household; everyone else only their own
    /// Personal Space. Newest first.
    /// </summary>
    Task<ViewPhotoViewsPageDto> ListViewsAsync(RequestAuthority caller, PagedRequest page, CancellationToken ct = default);
}
