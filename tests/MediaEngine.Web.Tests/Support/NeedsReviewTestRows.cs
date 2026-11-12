using MediaEngine.Contracts.Library;
using MediaEngine.Contracts.Review;

namespace MediaEngine.Web.Tests.Support;

/// <summary>Builds the Engine's Needs Review rows and pages for tests, so each test says only what it cares about.</summary>
internal static class NeedsReviewTestRows
{
    public static NeedsReviewRowDto ReviewRow(
        Guid id,
        string title,
        string trigger = "LowConfidence",
        string group = NeedsReviewGroups.NoMatch,
        NeedsReviewBestGuessDto? guess = null,
        double? confidence = null,
        string? entityType = "MediaAsset",
        string mediaType = "Movies",
        string? candidatesJson = null,
        string? familyKey = null,
        string? familyTitle = null,
        Guid? familyWorkId = null,
        Guid? entityId = null,
        string? libraryId = "movies") => new()
        {
            Key = $"review:{id:D}",
            LibraryId = libraryId,
            LibraryName = "Movies",
            LastTriedAt = DateTimeOffset.UtcNow.AddHours(-2),
            Group = group,
            BestGuess = guess,
            FamilyKey = familyKey,
            FamilyTitle = familyTitle,
            FamilyWorkId = familyWorkId,
            Review = new ReviewItemDto
            {
                Id = id,
                EntityId = entityId ?? Guid.NewGuid(),
                EntityType = entityType ?? string.Empty,
                Trigger = trigger,
                Status = "Pending",
                EntityTitle = title,
                MediaType = mediaType,
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-2),
                LibraryId = libraryId,
                ConfidenceScore = confidence,
                CandidatesJson = candidatesJson,
            },
        };

    public static NeedsReviewRowDto FileRow(
        Guid assetId,
        string fileName,
        Guid? workId = null,
        string group = NeedsReviewGroups.NoMatch,
        string reason = "no_match",
        string folder = "C:/media/movies/Action",
        string? familyKey = null,
        string? familyTitle = null,
        Guid? familyWorkId = null,
        DateTimeOffset? nextRetryAt = null,
        string library = "movies") => new()
        {
            Key = $"file:{assetId:D}",
            LibraryId = library,
            LibraryName = library == "tv" ? "TV" : "Movies",
            LastTriedAt = DateTimeOffset.UtcNow.AddHours(-3),
            Group = group,
            FamilyKey = familyKey,
            FamilyTitle = familyTitle,
            FamilyWorkId = familyWorkId,
            NextRetryAt = nextRetryAt,
            Unmatched = new UnmatchedItemDto
            {
                AssetId = assetId,
                LibraryId = library,
                FileName = fileName,
                Folder = folder,
                WorkId = workId,
                Reason = reason,
                LastTriedAt = DateTimeOffset.UtcNow.AddHours(-3),
                NextRetryAt = nextRetryAt,
            },
        };

    /// <summary>A page of the list as the Engine returns it: totals and group totals worked out from the rows.</summary>
    public static NeedsReviewListDto Page(IReadOnlyList<NeedsReviewRowDto> rows, int keptTotal = 0, int? total = null)
    {
        var totals = new Dictionary<string, int>
        {
            [NeedsReviewGroups.Ready] = 0,
            [NeedsReviewGroups.Choose] = 0,
            [NeedsReviewGroups.NoMatch] = 0,
            [NeedsReviewGroups.FileProblem] = 0,
        };
        foreach (var row in rows)
        {
            totals[row.Group] = totals.GetValueOrDefault(row.Group) + 1;
        }

        return new NeedsReviewListDto
        {
            Items = [.. rows],
            Total = total ?? rows.Count,
            GroupTotals = totals,
            KeptTotal = keptTotal,
            Take = 500,
        };
    }
}
