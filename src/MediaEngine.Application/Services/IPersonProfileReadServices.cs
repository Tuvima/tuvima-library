using MediaEngine.Contracts.Persons;
using MediaEngine.Contracts.Profiles;

namespace MediaEngine.Application.Services;

public interface IPersonCreditReadService
{
    Task<List<CastCreditDto>> BuildForWorkAsync(Guid workId, CancellationToken ct);
    Task<List<CastCreditDto>> BuildForCollectionRootAsync(Guid rootWorkId, string? rootWorkQid, CancellationToken ct);
    Task<List<PersonGroupMemberDto>> GetGroupMembersAsync(Guid personId, bool isGroup, ViewerCatalogue viewer, CancellationToken ct);
    Task<List<PersonLibraryCreditDto>> GetLibraryCreditsAsync(Guid personId, CancellationToken ct);
}

public interface IProfileOverviewReadService
{
    Task<ProfileOverviewResponseDto?> GetOverviewAsync(Guid profileId, CancellationToken ct);
}
