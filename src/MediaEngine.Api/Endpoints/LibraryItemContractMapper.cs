using MediaEngine.Api.Services;
using MediaEngine.Contracts.Items;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Models;

namespace MediaEngine.Api.Endpoints;

internal static class LibraryItemContractMapper
{
    public static LibraryItemDetailDto ToContract(this LibraryItemDetail source, string lookupState = ItemLookupStates.Matched) => new()
    {
        LookupState = lookupState,
        EntityId = source.EntityId,
        Title = source.Title,
        Year = source.Year,
        MediaType = source.MediaType,
        CoverUrl = source.CoverUrl,
        BackgroundUrl = source.BackgroundUrl,
        BannerUrl = source.BannerUrl,
        HeroUrl = source.HeroUrl,
        Confidence = source.Confidence,
        Status = source.Status,
        MatchSource = source.MatchSource,
        MatchMethod = source.MatchMethod,
        RetailProviderName = source.RetailProviderName,
        RetailProviderItemId = source.RetailProviderItemId,
        Author = source.Author,
        Director = source.Director,
        Artist = source.Artist,
        Album = source.Album,
        Composer = source.Composer,
        Illustrator = source.Illustrator,
        Writer = source.Writer,
        Cast = source.Cast,
        Language = source.Language,
        Genre = source.Genre,
        Runtime = source.Runtime,
        Description = source.Description,
        Tagline = source.Tagline,
        Series = source.Series,
        SeriesPosition = source.SeriesPosition,
        ShowName = source.ShowName,
        SeasonNumber = source.SeasonNumber,
        EpisodeNumber = source.EpisodeNumber,
        EpisodeTitle = source.EpisodeTitle,
        ReleaseDate = source.ReleaseDate,
        Narrator = source.Narrator,
        Rating = source.Rating,
        WikidataQid = source.WikidataQid,
        PlaybackSummary = source.PlaybackSummary is null ? null : new PlaybackTechnicalSummaryDto
        {
            VideoResolutionLabel = source.PlaybackSummary.VideoResolutionLabel,
            VideoCodec = source.PlaybackSummary.VideoCodec,
            AudioLanguage = source.PlaybackSummary.AudioLanguage,
            AudioCodec = source.PlaybackSummary.AudioCodec,
            AudioChannels = source.PlaybackSummary.AudioChannels,
            SubtitleSummary = source.PlaybackSummary.SubtitleSummary,
            AudioLanguages = source.PlaybackSummary.AudioLanguages,
            SubtitleLanguages = source.PlaybackSummary.SubtitleLanguages,
        },
        WikidataStatus = source.WikidataStatus,
        FileName = source.FileName,
        FilePath = source.FilePath,
        FileSizeBytes = source.FileSizeBytes,
        ContentHash = source.ContentHash,
        ReviewItemId = source.ReviewItemId,
        ReviewTrigger = source.ReviewTrigger,
        ReviewDetail = source.ReviewDetail,
        CandidatesJson = source.CandidatesJson,
        HasUserLocks = source.HasUserLocks,
        MatchLevel = source.MatchLevel,
        CanonicalValues = source.CanonicalValues.Select(value => new LibraryItemCanonicalValueDto
        {
            Key = value.Key,
            Value = value.Value,
            IsConflicted = value.IsConflicted,
            WinningProviderId = value.WinningProviderId,
            NeedsReview = value.NeedsReview,
            LastScoredAt = value.LastScoredAt,
        }).ToList(),
        ClaimHistory = source.ClaimHistory.Select(claim => new LibraryItemClaimRecordDto
        {
            Id = claim.Id,
            ClaimKey = claim.ClaimKey,
            ClaimValue = claim.ClaimValue,
            ProviderId = claim.ProviderId,
            Confidence = claim.Confidence,
            IsUserLocked = claim.IsUserLocked,
            ClaimedAt = claim.ClaimedAt,
        }).ToList(),
        BridgeIds = source.BridgeIds,
        PipelineStep = source.PipelineStep,
        LibraryVisibility = source.LibraryVisibility,
        IsReadyForLibrary = source.IsReadyForLibrary,
        ArtworkState = source.ArtworkState,
        ArtworkSource = source.ArtworkSource,
        ArtworkSettledAt = source.ArtworkSettledAt,
        UniverseSummary = source.UniverseSummary is null ? null : new LibraryItemUniverseSummaryDto
        {
            UniverseStatus = source.UniverseSummary.UniverseStatus,
            UniverseName = source.UniverseSummary.UniverseName,
            UniverseQid = source.UniverseSummary.UniverseQid,
            NarrativeRootQid = source.UniverseSummary.NarrativeRootQid,
            Stage3Status = source.UniverseSummary.Stage3Status,
            Stage3EnrichedAt = source.UniverseSummary.Stage3EnrichedAt,
            EntityCount = source.UniverseSummary.EntityCount,
            RelationshipCount = source.UniverseSummary.RelationshipCount,
            PortraitCount = source.UniverseSummary.PortraitCount,
        },
    };

    public static LibraryItemHistoryDto ToContract(this LibraryItemHistoryEntry source) => new()
    {
        Id = source.Id,
        EntityId = source.EntityId,
        OccurredAt = source.OccurredAt,
        EventType = source.EventType,
        Label = source.Label,
        Detail = source.Detail,
        Category = source.Category,
        ActorLabel = source.ActorLabel,
    };
}
