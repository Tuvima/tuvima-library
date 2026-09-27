using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using MediaEngine.Api.Endpoints;
using MediaEngine.Api.Models;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.Playback;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Persons;
using MediaEngine.Domain;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Domain.Services;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using static MediaEngine.Api.Services.Details.Internals.DetailPresentationPolicy;
using SeriesManifestItemDto = MediaEngine.Domain.Models.SeriesManifestItemDto;
using SeriesManifestViewDto = MediaEngine.Domain.Models.SeriesManifestViewDto;

namespace MediaEngine.Api.Services.Details.Internals;

internal sealed partial class DetailCompositionOrchestrator
{
    private sealed record WorkContributorResult(IReadOnlyList<CastCreditDto> CastCredits);
    private sealed record CanonicalPair(string Key, string Value);
    private sealed record WorkArtworkFallback
    {
        public string? CoverUrl { get; init; }
        public string? SquareUrl { get; init; }
        public string? BackgroundUrl { get; init; }
        public string? BannerUrl { get; init; }
    }
    private sealed record DescriptionSelection(string? Text, string? SourceKey, bool IsGeneratedFallback);
    private sealed record OwnedFormatRow(Guid EditionId, string? FormatLabel, Guid AssetId, string FilePathRoot, string? AssetCoverUrl, string? EditionCoverUrl, string? Runtime, string? PageCount, string? Narrator, double? ProgressPct);
    private sealed record CollectionDetailRow(Guid Id, string? DisplayName, string? CollectionType, string? WikidataQid, string? Description, string? Tagline, string? CoverUrl, string? BackgroundUrl, string? BannerUrl, string? LogoUrl, string? HeroBrandLabel, string? HeroBrandImageUrl);
    private sealed record SequenceLabels(string ContainerLabel, string ItemLabel, string ItemPluralLabel, string? GroupLabel);
    private sealed class SequenceContainerMetadataDbRow
    {
        public object? Description { get; init; }
        public object? WikipediaUrl { get; init; }
    }

    private sealed record SequenceContainerMetadataRow(string? Description, string? WikipediaUrl);

    private sealed class SequenceRow
    {
        public Guid WorkId { get; init; }
        public Guid? AssetId { get; init; }
        public string Title { get; init; } = "Untitled";
        public string? Description { get; init; }
        public string? MediaType { get; init; }
        public string? PositionLabel { get; init; }
        public double? PositionSort { get; init; }
        public string? SeasonLabel { get; init; }
        public string? EpisodeLabel { get; init; }
        public string? ArtworkUrl { get; init; }
        public string? ArtworkState { get; init; }
        public string? Duration { get; init; }
        public string? PublicationDate { get; init; }
    }

    private sealed class SeriesManifestItemRow
    {
        public Guid Id { get; set; }
        public Guid CollectionId { get; set; }
        public string SeriesQid { get; set; } = string.Empty;
        public string ItemQid { get; set; } = string.Empty;
        public string? ItemLabel { get; set; }
        public string? ItemDescription { get; set; }
        public string? MediaType { get; set; }
        public string? MediaKind { get; set; }
        public string InstanceOfQidsJson { get; set; } = "[]";
        public string? RawOrdinal { get; set; }
        public double? ParsedOrdinal { get; set; }
        public string? OrdinalScopeQid { get; set; }
        public double? SortOrder { get; set; }
        public string? PublicationDate { get; set; }
        public string? Duration { get; set; }
        public string? PreviousQid { get; set; }
        public string? NextQid { get; set; }
        public string? ParentCollectionQid { get; set; }
        public string? ParentCollectionLabel { get; set; }
        public int IsCollection { get; set; }
        public int IsExpandedFromCollection { get; set; }
        public string MembershipScope { get; set; } = SeriesMembershipScopeNames.MainSequence;
        public string SourcePropertiesJson { get; set; } = "[]";
        public string RelationshipsJson { get; set; } = "[]";
        public string OrderSource { get; set; } = "Unknown";
        public string OwnershipState { get; set; } = "Missing";
        public Guid? LinkedWorkId { get; set; }
        public string LastHydratedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");
        public string CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");
        public string UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");

        public SeriesManifestItemRecord ToEntity() => new()
        {
            Id = Id,
            CollectionId = CollectionId,
            SeriesQid = SeriesQid,
            ItemQid = ItemQid,
            ItemLabel = ItemLabel,
            ItemDescription = ItemDescription,
            MediaType = MediaType,
            MediaKind = MediaKind,
            InstanceOfQidsJson = InstanceOfQidsJson,
            RawOrdinal = RawOrdinal,
            ParsedOrdinal = ParsedOrdinal,
            OrdinalScopeQid = OrdinalScopeQid,
            SortOrder = SortOrder,
            PublicationDate = PublicationDate,
            Duration = Duration,
            PreviousQid = PreviousQid,
            NextQid = NextQid,
            ParentCollectionQid = ParentCollectionQid,
            ParentCollectionLabel = ParentCollectionLabel,
            IsCollection = IsCollection == 1,
            IsExpandedFromCollection = IsExpandedFromCollection == 1,
            MembershipScope = MembershipScope,
            SourcePropertiesJson = SourcePropertiesJson,
            RelationshipsJson = RelationshipsJson,
            OrderSource = OrderSource,
            OwnershipState = OwnershipState,
            LinkedWorkId = LinkedWorkId,
            LastHydratedAt = DateTimeOffset.Parse(LastHydratedAt, CultureInfo.InvariantCulture),
            CreatedAt = DateTimeOffset.Parse(CreatedAt, CultureInfo.InvariantCulture),
            UpdatedAt = DateTimeOffset.Parse(UpdatedAt, CultureInfo.InvariantCulture),
        };
    }

    private sealed record CollectionWorkSummary(
        string Id,
        string MediaType,
        int? Ordinal,
        string Title,
        string? Description,
        string? Season,
        string? Episode,
        string? TrackNumber,
        int? DiscNumber,
        string? Duration,
        string? Year,
        string? Artist,
        bool IsExplicit,
        string? Quality,
        double? ProgressPercent,
        bool HasAsset,
        string? Ownership,
        bool IsCatalogOnly,
        string? ArtworkUrl,
        string? BackgroundUrl,
        string? AssetId)
    {
        public string? LastAccessed { get; init; }
        public double? SequenceSort { get; init; }
        public string? SequenceLabel { get; init; }
        public string? MembershipScope { get; init; }
        public string? DetailRoute { get; init; }

        public bool IsOwned =>
            HasAsset
            && !IsCatalogOnly
            && !string.Equals(Ownership, "Unowned", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Ownership, "Missing", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record SeasonArtworkPresentation(
        string EntityId,
        string? ArtworkSmallUrl,
        string? ArtworkUrl,
        string? BackgroundArtworkUrl);

    private sealed class SeasonWorkArtworkRow
    {
        public Guid WorkId { get; init; }
        public int? Ordinal { get; init; }
        public string? SeasonNumber { get; init; }
    }

    private sealed record AudiobookAssetRow
    {
        public string? InspectionJson { get; init; }
        public Guid WorkId { get; init; }
        public Guid AssetId { get; init; }
        public string Title { get; init; } = string.Empty;
        public string? Author { get; init; }
        public string? Narrator { get; init; }
        public string? DurationSecondsValue { get; init; }
        public string? Duration { get; init; }
    }

    private sealed record ContributorEntry(string Name, string? Qid, int SortOrder);

    private sealed class ContributorTargetRow
    {
        public Guid WorkId { get; init; }
        public Guid? RootWorkId { get; init; }
        public Guid? AssetId { get; init; }
    }

    private sealed class ContributorClaimRow
    {
        public long RowNumber { get; init; }
        public string ClaimKey { get; init; } = string.Empty;
        public string ClaimValue { get; init; } = string.Empty;
    }

    private sealed record CharacterDetailRow(Guid Id, string Label, string? WikidataQid, string? UniverseQid, string? UniverseLabel, string? ImageUrl, string? EntitySubType);
    private sealed class AudiobookResumeRow
    {
        public int SourceRank { get; init; }
        public double? PositionSeconds { get; init; }
        public double? DurationSeconds { get; init; }
        public double? ProgressPct { get; init; }
        public string? LastAccessed { get; init; }
        public string? ExtendedProperties { get; init; }
    }

    private sealed class CollectionCharacterRow
    {
        public Guid Id { get; init; }
        public string Label { get; init; } = "";
        public string? WikidataQid { get; init; }
        public string? UniverseQid { get; init; }
        public string? UniverseLabel { get; init; }
        public string? ImageUrl { get; init; }
        public string? EntitySubType { get; init; }
        public Guid? PortraitId { get; init; }
        public string? PortraitImageUrl { get; init; }
        public string? PortraitLocalImagePath { get; init; }
        public bool PortraitIsDefault { get; init; }
    }

    private sealed record CharacterPortraitRow(Guid Id, string? ImageUrl, string? LocalImagePath, bool IsDefault);
    private sealed class UniversePerformerRow
    {
        public long LinkOrder { get; init; }
        public Guid? PersonId { get; init; }
        public string? PersonName { get; init; }
        public string? PersonQid { get; init; }
        public string? HeadshotUrl { get; init; }
        public string? LocalHeadshotPath { get; init; }
        public Guid CharacterId { get; init; }
        public string? CharacterName { get; init; }
        public Guid? PortraitId { get; init; }
        public string? PortraitImageUrl { get; init; }
        public string? PortraitLocalImagePath { get; init; }
        public bool PortraitIsDefault { get; init; }
    }

    private sealed class UniverseRelationshipRow
    {
        public string RelationshipType { get; init; } = "";
        public string SubjectQid { get; init; } = "";
        public string ObjectQid { get; init; } = "";
        public string SubjectLabel { get; init; } = "";
        public string ObjectLabel { get; init; } = "";
        public string? SubjectType { get; init; }
        public string? ObjectType { get; init; }
    }
}
