namespace MediaEngine.Domain.Contracts;

/// <summary>One file behind a person's personal photos and videos. <paramref name="IsManaged"/> is true when Tuvima owns the copy on disk.</summary>
public sealed record ProfilePersonalFile(Guid FileId, string FilePath, bool IsManaged);

/// <summary>What removing a person has to know about their personal photos and videos (View).</summary>
public interface IProfilePersonalMediaRepository
{
    /// <summary>The person's personal items that are not in the trash.</summary>
    Task<IReadOnlyList<Guid>> GetPersonalItemIdsAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>Adds a tag to each of the person's personal items that is not in the trash.</summary>
    Task AddTagAsync(Guid profileId, string tag, DateTimeOffset addedAt, CancellationToken ct = default);

    /// <summary>Every file location behind the person's personal items (trash included).</summary>
    Task<IReadOnlyList<ProfilePersonalFile>> GetPersonalFilesAsync(Guid profileId, CancellationToken ct = default);

    /// <summary>Forgets file records that no item uses any more.</summary>
    Task DeleteUnusedFilesAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken ct = default);
}
